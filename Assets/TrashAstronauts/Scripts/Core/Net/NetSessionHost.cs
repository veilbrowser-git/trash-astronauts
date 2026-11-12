using System;

namespace TrashAstronauts.Core.Net
{
    /// <summary>
    /// Sees every message above the reliable layer, for recording and replay (the tape test) and for debugging.
    /// <paramref name="outbound"/> true = this session sent it. <paramref name="peerSlot"/> is the other end
    /// (on a client always 0, the host). The payload bytes are only valid during the call.
    /// <paramref name="time"/> is host time: the host's clock when sending, the datagram's stamp when receiving.
    /// </summary>
    public delegate void NetMessageTap(bool outbound, byte peerSlot, byte type, byte[] buffer, int offset, int length, double time);

    /// <summary>Counters kept by a session. Plain fields; read them from tests and the debug line.</summary>
    public sealed class NetStats
    {
        public long datagramsSent;
        public long datagramsReceived;
        public long bytesSent;
        public long bytesReceived;
        /// <summary>Datagrams or messages dropped because they were truncated, garbage or held an out-of-range value.</summary>
        public long malformed;
        /// <summary>Datagrams from an unknown sender, with the wrong connection id or protocol.</summary>
        public long stray;
        /// <summary>Datagrams dropped as duplicates or as more than 32 behind.</summary>
        public long duplicates;
        /// <summary>Well-formed messages this end does not handle.</summary>
        public long unknownMessages;
        /// <summary>Well-formed messages refused by the rules (rate limit, stale epoch, not the owner ...).</summary>
        public long rejected;
        /// <summary>Datagrams that arrived after a newer one: their reliable messages were taken, their streams skipped.</summary>
        public long staleStreams;
        /// <summary>Largest datagram this session has sent.</summary>
        public int largestDatagram;
    }

    public sealed class NetHostConfig
    {
        public byte protocol = NetProtocol.Version;
        /// <summary>NetIdTable.ContentHash of this build. A Hello with a different hash is refused.</summary>
        public uint contentHash;
        /// <summary>The four digits shown as "TUG 4821".</summary>
        public ushort tugNumber;
        /// <summary>The host player's handedness (0 right, 1 left).</summary>
        public byte handedness;
        /// <summary>Seeds the connection ids.</summary>
        public int seed = 1;
    }

    /// <summary>
    /// The host end of a session: handshake, slots 1..3, Away and timeouts, relay of avatars and item poses,
    /// grab arbitration, reliable dispatch into <see cref="IHostWorld"/>. Pure C#: time comes in through
    /// <see cref="Poll"/> / <see cref="SendTick"/>, bytes go through an <see cref="INetTransport"/>.
    ///
    /// Call order per frame: Poll(now) from Update, the game's own work, SendTick(now) from LateUpdate
    /// (it sends at 20 Hz whatever the frame rate). <see cref="Tick"/> does both. Not thread-safe.
    /// </summary>
    public sealed partial class NetSessionHost : IDisposable
    {
        sealed class Peer
        {
            public byte slot;
            public bool used;
            public bool confirmed;
            public NetEndpoint endpoint;
            public ushort connId;
            public uint nonce;
            public byte handedness;
            public readonly ReliableChannel channel = new ReliableChannel();
            public double acceptedAt;
            public double lastHeard;
            public bool silent;
            public bool paused;
            public bool avatarAway;
            public bool away;
            public AvatarSnapshot avatar;
            public bool hasAvatar;
            public bool avatarFresh;
            public readonly double[] noiseTimes = new double[NetProtocol.MaxNoisePerSecond];
            public int noiseIndex;
            public readonly ItemPoseEntry[] poses = new ItemPoseEntry[NetProtocol.MaxItemPosesPerDatagram];
            public int poseCount;
            /// <summary>Sequence of the newest datagram whose streams were applied (older ones only deliver their reliable messages).</summary>
            public ushort streamSeq;
            public bool hasStreamSeq;
            /// <summary>Host clock minus this client's clock, following the least-delayed datagrams (see <see cref="UpdatePeerClock"/>).</summary>
            public double clockOffset;
            public bool hasClock;
            /// <summary>The sending time of the datagram being read, on the host's clock.</summary>
            public double datagramTime;
            /// <summary>The stamp given to this client's newest avatar (stamps never go backwards).</summary>
            public double lastAvatarStamp;
        }

        const int k_MaxDatagramsPerPoll = 512;
        const int k_MaxExtraDatagrams = 3;
        const int k_HostPoseCapacity = 32;
        /// <summary>Least time between two Gone answers to stray datagrams (4 a second).</summary>
        const double k_GoneInterval = 0.25;
        /// <summary>The same once the session is closed: every client still talking is told quickly.</summary>
        const double k_ClosedGoneInterval = 0.02;

        readonly INetTransport m_Transport;
        readonly IHostWorld m_World;
        readonly NetHostConfig m_Config;
        readonly Peer[] m_Peers = new Peer[NetProtocol.MaxPlayers];
        readonly ItemOwnershipTable m_Items = new ItemOwnershipTable();
        readonly NetStats m_Stats = new NetStats();
        readonly byte[] m_Receive = new byte[NetProtocol.MaxDatagram + 64];
        readonly byte[] m_Send = new byte[NetProtocol.MaxDatagram];
        readonly byte[] m_Scratch = new byte[NetProtocol.MaxMessageBytes];
        readonly byte[] m_Small = new byte[64];
        readonly byte[] m_Ids = new byte[ItemOwnershipTable.MaxItems];
        readonly float[] m_NoiseMax = { 1f, 1f, 0f, 0f, 1f, 1f, 0f };
        readonly Random m_Random;

        double m_Now;
        double m_LastSend = double.NegativeInfinity;
        double m_LastGone = double.NegativeInfinity;
        bool m_Closed;
        bool m_Disposed;
        byte m_Handedness;

        AvatarSnapshot m_LocalAvatar;
        bool m_HasLocalAvatar;
        bool m_LocalAvatarFresh;
        CreatureSnapshot m_Creature;
        bool m_CreatureFresh;
        ClockMsg m_Clock;
        bool m_ClockFresh;
        readonly ItemPoseEntry[] m_HostPoses = new ItemPoseEntry[k_HostPoseCapacity];
        int m_HostPoseCount;
        ItemPoseBatch m_PoseRead;
        ItemPoseBatch m_PoseWrite;
        WelcomeMsg m_Welcome;

        /// <summary>Optional recorder; see <see cref="NetMessageTap"/>.</summary>
        public NetMessageTap Tap;

        public NetHostConfig Config => m_Config;
        public NetStats Stats => m_Stats;
        public INetTransport Transport => m_Transport;
        /// <summary>Who simulates each salvage item. Reset by <see cref="SendRunStart"/>.</summary>
        public ItemOwnershipTable Items => m_Items;
        /// <summary>The time passed to the latest Poll / SendTick.</summary>
        public double Now => m_Now;
        public bool IsClosed => m_Closed;

        /// <summary>
        /// The session takes ownership of <paramref name="transport"/> (already open; the host socket is bound to
        /// <see cref="NetProtocol.HostPort"/>) and disposes it in <see cref="Dispose"/>.
        /// </summary>
        public NetSessionHost(INetTransport transport, IHostWorld world, NetHostConfig config)
        {
            m_Transport = transport ?? throw new ArgumentNullException(nameof(transport));
            m_World = world ?? throw new ArgumentNullException(nameof(world));
            m_Config = config ?? new NetHostConfig();
            m_Handedness = (byte)(m_Config.handedness & 1);
            m_Random = new Random(m_Config.seed);
            for (int i = 0; i < m_Peers.Length; i++)
            {
                m_Peers[i] = new Peer { slot = (byte)i };
                for (int n = 0; n < m_Peers[i].noiseTimes.Length; n++) m_Peers[i].noiseTimes[n] = double.NegativeInfinity;
            }
            m_PoseRead.entries = new ItemPoseEntry[ItemPoseBatch.MaxEntries];
            m_PoseWrite.entries = new ItemPoseEntry[ItemPoseBatch.MaxEntries];
        }

        // ================================================================== roster queries

        /// <summary>Host plus every slot that is taken (including a join still in its handshake).</summary>
        public int PlayerCount
        {
            get
            {
                int n = 1;
                for (int i = 1; i < m_Peers.Length; i++) if (m_Peers[i].used) n++;
                return n;
            }
        }

        /// <summary>True for slot 0 and for a client that completed the handshake and has not left.</summary>
        public bool IsConnected(int slot)
        {
            if (slot == 0) return !m_Closed;
            return slot > 0 && slot < m_Peers.Length && m_Peers[slot].used && m_Peers[slot].confirmed;
        }

        /// <summary>True when the slot is connected and Away (paused, untracked or silent for 1.5 s).</summary>
        public bool IsAway(int slot)
        {
            return slot > 0 && slot < m_Peers.Length && m_Peers[slot].used && m_Peers[slot].confirmed && m_Peers[slot].away;
        }

        /// <summary>Bit n set = slot n connected (bit 0, the host, is always set while open).</summary>
        public byte ConnectedMask
        {
            get
            {
                int mask = m_Closed ? 0 : 1;
                for (int i = 1; i < m_Peers.Length; i++) if (IsConnected(i)) mask |= 1 << i;
                return (byte)mask;
            }
        }

        /// <summary>Bit n set = slot n is connected and Away.</summary>
        public byte AwayMask
        {
            get
            {
                int mask = 0;
                for (int i = 1; i < m_Peers.Length; i++) if (IsAway(i)) mask |= 1 << i;
                return (byte)mask;
            }
        }

        public byte Handedness(int slot)
        {
            if (slot == 0) return m_Handedness;
            return slot > 0 && slot < m_Peers.Length && m_Peers[slot].used ? m_Peers[slot].handedness : (byte)0;
        }

        /// <summary>Reliable messages waiting for a slot (0 for an empty slot). Bounded by 512.</summary>
        public int Backlog(int slot)
        {
            return slot > 0 && slot < m_Peers.Length && m_Peers[slot].used ? m_Peers[slot].channel.Backlog : 0;
        }

        /// <summary>The newest avatar received from a client. False until one has arrived.</summary>
        public bool TryGetAvatar(int slot, out AvatarSnapshot avatar)
        {
            if (slot > 0 && slot < m_Peers.Length && m_Peers[slot].used && m_Peers[slot].hasAvatar)
            {
                avatar = m_Peers[slot].avatar;
                return true;
            }
            avatar = default(AvatarSnapshot);
            return false;
        }

        /// <summary>
        /// Loudest value accepted from a client for a noise kind; louder is clamped, a maximum of 0 drops the kind.
        /// Defaults: Impact, Cutting, Voice, Thruster 1.0; PanelBreak, Door, Web 0 (the host emits those itself
        /// when it applies the change).
        /// </summary>
        public void SetNoiseMax(NoiseKind kind, float maxLoudness)
        {
            int k = (int)kind;
            if (k >= 0 && k < m_NoiseMax.Length) m_NoiseMax[k] = maxLoudness > 0f ? maxLoudness : 0f;
        }

        // ================================================================== pump

        /// <summary>Poll then SendTick.</summary>
        public void Tick(double now)
        {
            Poll(now);
            SendTick(now);
        }

        /// <summary>
        /// Receives and dispatches everything waiting, then runs the Away / timeout / backstop timers. After
        /// <see cref="Close"/> and until <see cref="Dispose"/> it only answers clients that are still sending with
        /// "the host left", so keep calling it for a second or two if the goodbye should survive a burst of loss.
        /// </summary>
        public void Poll(double now)
        {
            if (m_Disposed) return;
            m_Now = now;
            if (m_Closed)
            {
                AnswerWhileClosed();
                return;
            }
            for (int i = 0; i < k_MaxDatagramsPerPoll; i++)
            {
                if (!m_Transport.Receive(out NetEndpoint from, m_Receive, out int length)) break;
                m_Stats.datagramsReceived++;
                m_Stats.bytesReceived += length;
                HandleDatagram(from, length);
                if (m_Closed) return;
            }
            RunTimers();
        }

        /// <summary>
        /// Sends one datagram to every client when the next 50 ms send tick is due. The tick keeps its phase, so
        /// the rate is 20 Hz at any frame rate (a tick lands on the first frame at or after its due time).
        /// </summary>
        public void SendTick(double now)
        {
            if (m_Closed || m_Disposed) return;
            m_Now = now;
            if (now - m_LastSend < NetProtocol.SendInterval - 1e-6) return;
            m_LastSend += NetProtocol.SendInterval;
            if (!(now - m_LastSend < NetProtocol.SendInterval)) m_LastSend = now; // first tick, or a stall: do not burst to catch up
            for (int i = 1; i < m_Peers.Length; i++)
            {
                Peer peer = m_Peers[i];
                if (peer.used && peer.confirmed) SendDatagrams(peer, true, false);
            }
            m_LocalAvatarFresh = false;
            m_CreatureFresh = false;
            m_ClockFresh = false;
            m_HostPoseCount = 0;
            ClearCutProgress();
            for (int i = 1; i < m_Peers.Length; i++)
            {
                m_Peers[i].avatarFresh = false;
                m_Peers[i].poseCount = 0;
            }
        }

        /// <summary>Sends pending reliable messages right now instead of at the next send tick (input-like events).</summary>
        public void Flush(double now)
        {
            if (m_Closed || m_Disposed) return;
            m_Now = now;
            for (int i = 1; i < m_Peers.Length; i++)
            {
                Peer peer = m_Peers[i];
                if (peer.used && peer.confirmed && peer.channel.HasDue(now)) SendDatagrams(peer, false, false);
            }
        }

        /// <summary>
        /// Ends the session: takes back every client-owned item, tells every client SessionClosed (reliable, sent
        /// three times) and, outside the reliable stream, Gone(HostLeft) twice, so the goodbye does not depend on
        /// the reliable window having room. Frees the slots. The host keeps its run as single-player. Call
        /// <see cref="Dispose"/> afterwards; until then <see cref="Poll"/> keeps answering clients that missed it.
        /// </summary>
        public void Close(double now)
        {
            if (m_Closed || m_Disposed) return;
            m_Now = now;
            int n = 0;
            for (int i = 0; i < m_Items.Count; i++)
                if (m_Items.IsClientOwned(i)) m_Ids[n++] = (byte)i;
            for (int i = 0; i < n; i++) ReturnItem(m_Ids[i], ItemReturnCause.HostTookOver, false, default(ItemHandoffMsg));

            var w = new NetWriter(m_Small);
            new SessionClosedMsg { reason = SessionCloseReason.HostLeft }.Write(ref w);
            Broadcast(NetMsg.SessionClosed, ref w);
            for (int repeat = 0; repeat < 3; repeat++)
                for (int i = 1; i < m_Peers.Length; i++)
                    if (m_Peers[i].used && m_Peers[i].confirmed) SendDatagrams(m_Peers[i], false, true);

            m_Closed = true;
            for (int i = 1; i < m_Peers.Length; i++)
            {
                Peer peer = m_Peers[i];
                if (!peer.used) continue;
                bool confirmed = peer.confirmed;
                for (int repeat = 0; repeat < 2; repeat++) SendGone(peer.endpoint, peer.connId, SessionCloseReason.HostLeft, 0);
                Free(peer);
                if (confirmed) m_World.OnPlayerLeft(peer.slot, PlayerLeaveReason.HostClosed);
            }
        }

        /// <summary>Disposes the transport. Sends nothing; call <see cref="Close"/> first for a clean goodbye.</summary>
        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            m_Closed = true;
            m_Transport.Dispose();
        }

        // ================================================================== receiving

        void HandleDatagram(NetEndpoint from, int length)
        {
            if (length < 3) { m_Stats.malformed++; return; }
            Connectionless.TryPeekConnId(m_Receive, length, out ushort connId);
            if (connId == 0) HandleConnectionless(from, length);
            else HandleSessionDatagram(from, length);
        }

        void HandleConnectionless(NetEndpoint from, int length)
        {
            var r = new NetReader(m_Receive, 0, length);
            if (!Connectionless.TryReadFrame(ref r, out byte protocol, out byte type)) { m_Stats.malformed++; return; }
            switch (type)
            {
                case NetMsg.Probe:
                {
                    if (!ProbeMsg.Read(ref r, protocol, out ProbeMsg probe)) { m_Stats.malformed++; return; }
                    var info = new HostInfoMsg
                    {
                        protocol = m_Config.protocol,
                        nonce = probe.nonce,
                        contentHash = m_Config.contentHash,
                        tugNumber = m_Config.tugNumber,
                        playerCount = (byte)PlayerCount,
                        phase = m_World.Phase,
                    };
                    var w = new NetWriter(m_Send);
                    info.WriteDatagram(ref w);
                    SendRaw(from, w.Position);
                    break;
                }
                case NetMsg.Hello:
                {
                    if (!HelloMsg.Read(ref r, protocol, out HelloMsg hello)) { m_Stats.malformed++; return; }
                    HandleHello(from, hello);
                    break;
                }
                case NetMsg.Quit:
                {
                    // The goodbye that does not need room in the reliable window (see NetSessionClient.Leave).
                    if (!QuitMsg.Read(ref r, protocol, out QuitMsg quit)) { m_Stats.malformed++; return; }
                    Peer peer = null;
                    for (int i = 1; i < m_Peers.Length; i++)
                        if (m_Peers[i].used && m_Peers[i].connId == quit.connId && m_Peers[i].endpoint == from) { peer = m_Peers[i]; break; }
                    if (peer == null || quit.protocol != m_Config.protocol || quit.connId == 0) { m_Stats.stray++; return; }
                    Drop(peer, PlayerLeaveReason.Bye, false);
                    break;
                }
                default:
                    m_Stats.unknownMessages++;
                    break;
            }
        }

        /// <summary>Closed but not yet disposed: tell anyone still sending session datagrams that the host left.</summary>
        void AnswerWhileClosed()
        {
            for (int i = 0; i < k_MaxDatagramsPerPoll; i++)
            {
                if (!m_Transport.Receive(out NetEndpoint from, m_Receive, out int length)) break;
                m_Stats.datagramsReceived++;
                m_Stats.bytesReceived += length;
                if (length < DatagramHeader.Size) continue;
                Connectionless.TryPeekConnId(m_Receive, length, out ushort connId);
                if (connId != 0 && m_Receive[0] == m_Config.protocol) SendGone(from, connId, SessionCloseReason.HostLeft, k_ClosedGoneInterval);
            }
        }

        void HandleHello(NetEndpoint from, in HelloMsg hello)
        {
            if (hello.protocol != m_Config.protocol) { SendRefuse(from, hello.clientNonce, RefuseReason.VersionMismatch); return; }
            if (hello.contentHash != m_Config.contentHash) { SendRefuse(from, hello.clientNonce, RefuseReason.ContentMismatch); return; }

            Peer existing = null;
            for (int i = 1; i < m_Peers.Length; i++)
                if (m_Peers[i].used && m_Peers[i].endpoint == from) { existing = m_Peers[i]; break; }
            if (existing != null && existing.nonce == hello.clientNonce)
            {
                SendAccept(existing); // our Accept was lost; say it again
                return;
            }

            MissionPhase phase = m_World.Phase;
            if (phase != MissionPhase.Hub && phase != MissionPhase.Results) { SendRefuse(from, hello.clientNonce, RefuseReason.ContractInProgress); return; }

            if (existing != null) Drop(existing, PlayerLeaveReason.Replaced, false);

            Peer peer = null;
            for (int i = 1; i < m_Peers.Length; i++)
                if (!m_Peers[i].used) { peer = m_Peers[i]; break; }
            if (peer == null) { SendRefuse(from, hello.clientNonce, RefuseReason.Full); return; }

            peer.used = true;
            peer.confirmed = false;
            peer.endpoint = from;
            peer.nonce = hello.clientNonce;
            peer.handedness = hello.handedness;
            peer.connId = NewConnId();
            peer.channel.Reset(peer.connId);
            peer.acceptedAt = m_Now;
            peer.lastHeard = m_Now;
            peer.silent = false;
            peer.paused = false;
            peer.avatarAway = false;
            peer.away = false;
            peer.hasAvatar = false;
            peer.avatarFresh = false;
            peer.poseCount = 0;
            peer.hasStreamSeq = false;
            peer.hasClock = false;
            peer.lastAvatarStamp = double.NegativeInfinity;
            peer.noiseIndex = 0;
            for (int n = 0; n < peer.noiseTimes.Length; n++) peer.noiseTimes[n] = double.NegativeInfinity;
            SendAccept(peer);
        }

        ushort NewConnId()
        {
            while (true)
            {
                ushort id = (ushort)m_Random.Next(1, 65536);
                bool taken = false;
                for (int i = 1; i < m_Peers.Length; i++)
                    if (m_Peers[i].used && m_Peers[i].connId == id) taken = true;
                if (!taken) return id;
            }
        }

        void SendAccept(Peer peer)
        {
            var accept = new AcceptMsg { protocol = m_Config.protocol, clientNonce = peer.nonce, connId = peer.connId, slot = peer.slot };
            var w = new NetWriter(m_Send);
            accept.WriteDatagram(ref w);
            SendRaw(peer.endpoint, w.Position);
        }

        void SendRefuse(NetEndpoint to, uint clientNonce, RefuseReason reason)
        {
            var refuse = new RefuseMsg { protocol = m_Config.protocol, clientNonce = clientNonce, reason = reason };
            var w = new NetWriter(m_Send);
            refuse.WriteDatagram(ref w);
            SendRaw(to, w.Position);
        }

        /// <summary>Sends Gone unless one went out less than <paramref name="minInterval"/> seconds ago (0 = always send).</summary>
        void SendGone(NetEndpoint to, ushort connId, SessionCloseReason reason, double minInterval)
        {
            if (minInterval > 0 && m_Now - m_LastGone < minInterval) return;
            m_LastGone = m_Now;
            var gone = new GoneMsg { protocol = m_Config.protocol, connId = connId, reason = reason };
            var w = new NetWriter(m_Send);
            gone.WriteDatagram(ref w);
            SendRaw(to, w.Position);
        }

        void SendRaw(NetEndpoint to, int length)
        {
            m_Transport.Send(to, m_Send, length);
            m_Stats.datagramsSent++;
            m_Stats.bytesSent += length;
            if (length > m_Stats.largestDatagram) m_Stats.largestDatagram = length;
        }

        void HandleSessionDatagram(NetEndpoint from, int length)
        {
            var r = new NetReader(m_Receive, 0, length);
            if (!DatagramHeader.Read(ref r, out DatagramHeader header)) { m_Stats.malformed++; return; }
            if (header.protocol != m_Config.protocol || header.senderSlot == 0 || header.senderSlot >= m_Peers.Length) { m_Stats.stray++; return; }
            Peer peer = m_Peers[header.senderSlot];
            if (!peer.used || peer.connId != header.connId || peer.endpoint != from)
            {
                m_Stats.stray++;
                SendGone(from, header.connId, SessionCloseReason.Dropped, k_GoneInterval);
                return;
            }
            if (!peer.channel.IsNewDatagram(header.seq)) { m_Stats.duplicates++; peer.channel.OnDatagramReceived(header.seq); return; }

            // Pass 1: validate the whole body before anything is applied or acknowledged. A truncated or garbled
            // datagram is dropped whole, and whatever reliable messages it carried will simply be sent again.
            int bodyStart = r.Position;
            if (!ValidateBody(ref r)) { m_Stats.malformed++; return; }
            peer.channel.OnDatagramReceived(header.seq);

            // back after a gap (a burst of loss, a headset waking up): resend what it missed now, not after a backoff
            if (m_Now - peer.lastHeard >= NetProtocol.HeardAgainSeconds) peer.channel.ResetBackoff();
            peer.lastHeard = m_Now;
            UpdatePeerClock(peer, DatagramHeader.ToSeconds(header.timeMs));
            if (!peer.confirmed) Confirm(peer);
            if (peer.silent)
            {
                peer.silent = false;
                RefreshAway(peer);
            }
            peer.channel.OnAck(header.ack, header.ackBits);

            // Pass 2: reliable messages first. They were queued before this datagram's streams were sampled
            // (a grab request comes before the first pose of the grabbed item).
            ushort connId = peer.connId;
            r = new NetReader(m_Receive, bodyStart, length - bodyStart);
            AcceptReliable(peer, ref r);
            while (peer.used && peer.connId == connId && peer.channel.TryDeliver(out byte type, out byte[] data, out int size))
            {
                Tap?.Invoke(false, peer.slot, type, data, 0, size, m_Now);
                var mr = new NetReader(data, 0, size);
                DispatchReliable(peer, type, ref mr);
                if (m_Closed) return;
            }
            if (!peer.used || peer.connId != connId) return; // it said Bye

            // Pass 3: the streams, unless a newer datagram's streams have been applied already. A datagram that
            // was overtaken on the way still delivers its reliable messages (above), but its avatar, Away flag
            // and poses are older than what the host has and must not replace it.
            if (peer.hasStreamSeq && (short)(header.seq - peer.streamSeq) <= 0) { m_Stats.staleStreams++; return; }
            peer.streamSeq = header.seq;
            peer.hasStreamSeq = true;
            r = new NetReader(m_Receive, bodyStart, length - bodyStart);
            while (!r.AtEnd)
            {
                byte type = r.U8();
                if (type == NetMsg.End) break;
                if (NetMsg.IsReliable(type))
                {
                    r.U16();
                    r.Skip(r.U16());
                }
                else if (!HandleStream(peer, type, ref r)) break;
            }
        }

        /// <summary>
        /// Maps a client's datagram time onto the host clock. The offset follows the least-delayed datagram at once
        /// and drifts toward later ones slowly (as NetSessionClient does for the host clock), so a datagram that sat
        /// in a router queue for 80 ms is stamped 80 ms before it was read. Never later than now.
        /// </summary>
        void UpdatePeerClock(Peer peer, double clientTime)
        {
            double sample = m_Now - clientTime;
            if (!peer.hasClock)
            {
                peer.hasClock = true;
                peer.clockOffset = sample;
            }
            else if (sample < peer.clockOffset) peer.clockOffset = sample;
            else peer.clockOffset += (sample - peer.clockOffset) * 0.02;
            double t = clientTime + peer.clockOffset;
            peer.datagramTime = t > m_Now ? m_Now : t;
        }

        /// <summary>
        /// Walks a datagram body without applying anything. False if any message is truncated, garbled or not one a
        /// client may send, or if the body does not close with <see cref="NetMsg.End"/> exactly at its last byte.
        /// </summary>
        bool ValidateBody(ref NetReader r)
        {
            while (!r.AtEnd)
            {
                byte type = r.U8();
                if (type == NetMsg.End) return !r.Failed && r.AtEnd;
                if (NetMsg.IsReliable(type))
                {
                    r.U16();
                    int size = r.U16();
                    if (r.Failed || size > r.Remaining || size > NetProtocol.MaxMessageBytes) return false;
                    r.Skip(size);
                }
                else if (!SkipStream(type, ref r)) return false;
            }
            return false; // ran out without the End byte: cut short
        }

        /// <summary>Reads one client stream message into scratch space. False for a bad one or a type a client may not send.</summary>
        bool SkipStream(byte type, ref NetReader r)
        {
            switch (type)
            {
                case NetMsg.AvatarState: return AvatarSnapshot.Read(ref r, out _);
                case NetMsg.ItemPoses: return ItemPoseBatch.Read(ref r, ref m_PoseRead);
                case NetMsg.Noise: return NoiseMsg.Read(ref r, out _);
                case CutProgressMsg.Id: return CutProgressMsg.Read(ref r, out _);
                default: return false;
            }
        }

        /// <summary>Hands every reliable message of an already validated body to the channel.</summary>
        void AcceptReliable(Peer peer, ref NetReader r)
        {
            while (!r.AtEnd)
            {
                byte type = r.U8();
                if (type == NetMsg.End) return;
                if (NetMsg.IsReliable(type))
                {
                    ushort relSeq = r.U16();
                    int size = r.U16();
                    if (r.Failed || size > r.Remaining) return;
                    peer.channel.Accept(relSeq, type, m_Receive, r.Position, size);
                    r.Skip(size);
                }
                else if (!SkipStream(type, ref r)) return;
            }
        }

        bool HandleStream(Peer peer, byte type, ref NetReader r)
        {
            int start = r.Position;
            switch (type)
            {
                case NetMsg.AvatarState:
                {
                    if (!AvatarSnapshot.Read(ref r, out AvatarSnapshot avatar)) return false;
                    Tap?.Invoke(false, peer.slot, type, m_Receive, start, r.Position - start, m_Now);
                    peer.avatar = avatar;
                    peer.hasAvatar = true;
                    peer.avatarFresh = true;
                    if (avatar.IsAway != peer.avatarAway)
                    {
                        peer.avatarAway = avatar.IsAway;
                        RefreshAway(peer);
                    }
                    // Stamped with when the client SENT it (its clock mapped onto ours), not when this Poll read it:
                    // two or three datagrams read in one Poll after a Wi-Fi hiccup keep their own 50 ms spacing.
                    double stamp = peer.datagramTime;
                    if (stamp < peer.lastAvatarStamp) stamp = peer.lastAvatarStamp;
                    peer.lastAvatarStamp = stamp;
                    m_World.OnAvatar(peer.slot, in avatar, stamp);
                    return true;
                }
                case NetMsg.ItemPoses:
                {
                    if (!ItemPoseBatch.Read(ref r, ref m_PoseRead)) return false;
                    Tap?.Invoke(false, peer.slot, type, m_Receive, start, r.Position - start, m_Now);
                    for (int i = 0; i < m_PoseRead.count; i++)
                    {
                        ItemPoseEntry e = m_PoseRead.entries[i];
                        if (peer.away || !m_Items.AcceptPose(e.itemId, peer.slot, e.epoch, e.position, e.rotation, m_Now)) { m_Stats.rejected++; continue; }
                        StoreRelayPose(peer, in e);
                        m_World.OnItemPose(e.itemId, peer.slot, e.position, e.rotation, e.held, m_Now);
                    }
                    return true;
                }
                case NetMsg.Noise:
                {
                    if (!NoiseMsg.Read(ref r, out NoiseMsg noise)) return false;
                    Tap?.Invoke(false, peer.slot, type, m_Receive, start, r.Position - start, m_Now);
                    float max = m_NoiseMax[(int)noise.kind];
                    if (max <= 0f || noise.loudness <= 0f) { m_Stats.rejected++; return true; }
                    if (m_Now - peer.noiseTimes[peer.noiseIndex] < 1.0) { m_Stats.rejected++; return true; } // 8 already this second
                    peer.noiseTimes[peer.noiseIndex] = m_Now;
                    peer.noiseIndex = (peer.noiseIndex + 1) % peer.noiseTimes.Length;
                    m_World.OnNoise(peer.slot, noise.position, noise.loudness > max ? max : noise.loudness, noise.kind);
                    return true;
                }
                case CutProgressMsg.Id: return HandleCutProgress(peer, ref r);
                default:
                    return false;
            }
        }

        static void StoreRelayPose(Peer peer, in ItemPoseEntry e)
        {
            for (int i = 0; i < peer.poseCount; i++)
                if (peer.poses[i].itemId == e.itemId) { peer.poses[i] = e; return; }
            if (peer.poseCount < peer.poses.Length) peer.poses[peer.poseCount++] = e;
        }

        void DispatchReliable(Peer peer, byte type, ref NetReader r)
        {
            byte slot = peer.slot;
            switch (type)
            {
                case NetMsg.SettingsChanged:
                {
                    if (!SettingsChangedMsg.Read(ref r, out SettingsChangedMsg m)) { m_Stats.malformed++; return; }
                    peer.handedness = m.handedness;
                    m_World.OnPlayerSettings(slot, m.handedness);
                    BroadcastRoster(slot, RosterChange.Settings, m.handedness, slot);
                    break;
                }
                case NetMsg.PauseNotice:
                {
                    if (!PauseNoticeMsg.Read(ref r, out PauseNoticeMsg m)) { m_Stats.malformed++; return; }
                    peer.paused = m.on;
                    RefreshAway(peer);
                    break;
                }
                case NetMsg.Bye:
                    Drop(peer, PlayerLeaveReason.Bye, false);
                    break;
                case NetMsg.StartRequest:
                    m_World.OnStartRequest(slot);
                    break;
                case NetMsg.ExtractRequest:
                    m_World.OnExtractRequest(slot);
                    break;
                case NetMsg.GrabRequest:
                {
                    if (!GrabRequestMsg.Read(ref r, out GrabRequestMsg m)) { m_Stats.malformed++; return; }
                    HandleGrabRequest(peer, in m);
                    break;
                }
                case NetMsg.ItemReleased:
                {
                    if (!ItemReleasedMsg.Read(ref r, out ItemReleasedMsg m)) { m_Stats.malformed++; return; }
                    if (!m_Items.Release(m.itemId, slot, m.epoch, m_Now)) { m_Stats.rejected++; return; }
                    m_Items.RememberPose(m.itemId, m.position, m.rotation);
                    m_World.OnItemReleased(m.itemId, slot, m.position, m.rotation, m.releasePosition);
                    BroadcastOwner(m.itemId);
                    break;
                }
                case NetMsg.ItemThrown:
                {
                    if (!ItemThrownMsg.Read(ref r, out ItemThrownMsg m)) { m_Stats.malformed++; return; }
                    if (!m_Items.IsCurrent(m.itemId, slot, m.epoch) || m_Items.IsHeld(m.itemId)) { m_Stats.rejected++; return; }
                    m_World.OnItemThrown(m.itemId, slot, m.linearVelocity, m.angularVelocity, m.throwSpeed, m.wasThrown);
                    break;
                }
                case NetMsg.ItemHandoff:
                {
                    if (!ItemHandoffMsg.Read(ref r, out ItemHandoffMsg m)) { m_Stats.malformed++; return; }
                    // Taken from the owner at the current epoch even when the table still says "held": the owner
                    // knows it let go (its ItemReleased can have been turned down along with an earlier request).
                    if (!m_Items.IsCurrent(m.itemId, slot, m.epoch)) { m_Stats.rejected++; return; }
                    ReturnItem(m.itemId, ItemReturnCause.Handoff, true, m);
                    break;
                }
                case NetMsg.ItemDamageReport:
                {
                    if (!ItemDamageReportMsg.Read(ref r, out ItemDamageReportMsg m)) { m_Stats.malformed++; return; }
                    if (!m_Items.Contains(m.itemId) || m_Items.Owner(m.itemId) != slot) { m_Stats.rejected++; return; }
                    m_World.OnItemDamageReport(m.itemId, slot, m.newValue);
                    break;
                }
                case NetMsg.DoorRequest:
                {
                    if (!DoorRequestMsg.Read(ref r, out DoorRequestMsg m)) { m_Stats.malformed++; return; }
                    m_World.OnDoorRequest(slot, m.doorId, m.desiredOpen);
                    break;
                }
                case NetMsg.PointCutReport:
                {
                    if (!PointCutReportMsg.Read(ref r, out PointCutReportMsg m)) { m_Stats.malformed++; return; }
                    m_World.OnPointCutReport(slot, m.panel, m.index);
                    break;
                }
                case NetMsg.TranqHitReport:
                {
                    if (!TranqHitReportMsg.Read(ref r, out TranqHitReportMsg m)) { m_Stats.malformed++; return; }
                    m_World.OnTranqHitReport(slot, m.origin, m.hitPoint);
                    break;
                }
                case NetMsg.WireReport:
                {
                    if (!WireReportMsg.Read(ref r, out WireReportMsg m)) { m_Stats.malformed++; return; }
                    m_World.OnWireReport(slot, m.index, m.change);
                    break;
                }
                default:
                    m_Stats.unknownMessages++;
                    break;
            }
        }

        // ================================================================== roster changes

        void Confirm(Peer peer)
        {
            peer.confirmed = true;

            // The Welcome is queued before the game hears of the join, so whatever the game broadcasts from
            // inside OnPlayerJoined reaches the joiner after its Welcome, never before it.
            m_Welcome.roster = 0u;
            m_Welcome.SetSlot(0, true, m_Handedness, false);
            for (int i = 1; i < m_Peers.Length; i++)
                if (m_Peers[i].used && m_Peers[i].confirmed) m_Welcome.SetSlot(i, true, m_Peers[i].handedness, m_Peers[i].away);
            m_Welcome.phase = m_World.Phase;
            m_Welcome.world.Clear();
            m_World.FillWorldState(ref m_Welcome.world);
            var w = new NetWriter(m_Scratch);
            m_Welcome.Write(ref w);
            SendTo(peer, NetMsg.Welcome, ref w);

            m_World.OnPlayerJoined(peer.slot, peer.handedness);
            if (!peer.used || !peer.confirmed) return; // the game closed the session or dropped the slot in the callback
            BroadcastRoster(peer.slot, RosterChange.Joined, peer.handedness, peer.slot);
        }

        void RefreshAway(Peer peer)
        {
            bool away = peer.silent || peer.paused || peer.avatarAway;
            if (away == peer.away || !peer.confirmed) return;
            peer.away = away;
            if (away) ReturnItemsOf(peer.slot, ItemReturnCause.OwnerAway);
            m_World.OnPlayerAway(peer.slot, away);
            BroadcastRoster(peer.slot, away ? RosterChange.Away : RosterChange.Back, peer.handedness, -1);
        }

        void Drop(Peer peer, PlayerLeaveReason reason, bool tellPeer)
        {
            if (!peer.used) return;
            bool confirmed = peer.confirmed;
            byte slot = peer.slot;
            NetEndpoint endpoint = peer.endpoint;
            ushort connId = peer.connId;
            if (confirmed) ReturnItemsOf(slot, ItemReturnCause.OwnerLeft);
            Free(peer);
            if (tellPeer) SendGone(endpoint, connId, SessionCloseReason.Dropped, 0);
            if (!confirmed) return;
            m_World.OnPlayerLeft(slot, reason);
            BroadcastRoster(slot, RosterChange.Left, 0, slot);
        }

        static void Free(Peer peer)
        {
            peer.used = false;
            peer.confirmed = false;
            peer.away = false;
            peer.silent = false;
            peer.paused = false;
            peer.avatarAway = false;
            peer.hasAvatar = false;
            peer.avatarFresh = false;
            peer.poseCount = 0;
            peer.hasStreamSeq = false;
            peer.hasClock = false;
            peer.lastAvatarStamp = double.NegativeInfinity;
            peer.connId = 0;
            peer.channel.Reset(0);
        }

        void RunTimers()
        {
            for (int i = 1; i < m_Peers.Length; i++)
            {
                Peer peer = m_Peers[i];
                if (!peer.used) continue;
                if (!peer.confirmed)
                {
                    if (m_Now - peer.acceptedAt > NetProtocol.PendingSeconds) Free(peer);
                    continue;
                }
                double silence = m_Now - peer.lastHeard;
                if (peer.channel.Overflowed) { Drop(peer, PlayerLeaveReason.BacklogOverflow, true); continue; }
                if (silence >= NetProtocol.SlotHoldSeconds) { Drop(peer, PlayerLeaveReason.Timeout, true); continue; }
                if (silence >= NetProtocol.AwaySeconds && !peer.silent)
                {
                    peer.silent = true;
                    RefreshAway(peer);
                }
            }

            int expired = m_Items.FindExpired(m_Now, NetProtocol.ItemBackstopSeconds, m_Ids);
            for (int i = 0; i < expired; i++) ReturnItem(m_Ids[i], ItemReturnCause.Backstop, false, default(ItemHandoffMsg));
            // A client the table has as holding an item streams it 20 times a second. One that has streamed
            // nothing for 3 s does not know it holds it; without this the item would stay "held" for good.
            expired = m_Items.FindUnstreamed(m_Now, NetProtocol.ItemBackstopSeconds, m_Ids);
            for (int i = 0; i < expired; i++) ReturnItem(m_Ids[i], ItemReturnCause.Backstop, false, default(ItemHandoffMsg));
        }

        // ================================================================== items

        void HandleGrabRequest(Peer peer, in GrabRequestMsg m)
        {
            bool grantable = !peer.away && m_Items.Contains(m.itemId) && m_World.CanGrabItem(m.itemId, peer.slot);
            GrabResult result = m_Items.TryGrab(m.itemId, peer.slot, m.epochSeen, grantable);
            if (result == GrabResult.Granted)
            {
                m_Items.Touch(m.itemId, m_Now);
                BroadcastOwner(m.itemId);
                m_World.OnItemGranted(m.itemId, peer.slot, m.hand);
                return;
            }
            m_Stats.rejected++;
            // The one direct answer to this request: the item's current state, flagged "denied".
            ItemOwnershipTable.Entry entry = m_Items.Get(m.itemId);
            var w = new NetWriter(m_Small);
            new ItemOwnerMsg { itemId = m.itemId, ownerSlot = entry.owner, epoch = entry.epoch, held = entry.held, denied = true }.Write(ref w);
            SendTo(peer, NetMsg.ItemOwner, ref w);
        }

        void BroadcastOwner(int itemId)
        {
            ItemOwnershipTable.Entry entry = m_Items.Get(itemId);
            var w = new NetWriter(m_Small);
            new ItemOwnerMsg { itemId = (byte)itemId, ownerSlot = entry.owner, epoch = entry.epoch, held = entry.held }.Write(ref w);
            Broadcast(NetMsg.ItemOwner, ref w);
        }

        void ReturnItemsOf(byte slot, ItemReturnCause cause)
        {
            int n = m_Items.FindOwnedBy(slot, m_Ids);
            for (int i = 0; i < n; i++) ReturnItem(m_Ids[i], cause, false, default(ItemHandoffMsg));
        }

        void ReturnItem(int itemId, ItemReturnCause cause, bool hasHandoff, in ItemHandoffMsg handoff)
        {
            ItemOwnershipTable.Entry before = m_Items.Get(itemId);
            if (!m_Items.ReturnToHost(itemId, out byte previousOwner)) return;
            var item = new ItemReturn { itemId = (byte)itemId, fromSlot = previousOwner, cause = cause, rotation = NetQuat.Identity };
            if (hasHandoff)
            {
                item.hasPose = true;
                item.position = handoff.position;
                item.rotation = handoff.rotation;
                item.linearVelocity = handoff.linearVelocity;
                item.angularVelocity = handoff.angularVelocity;
            }
            else if (before.hasPose)
            {
                item.hasPose = true;
                item.position = before.position;
                item.rotation = before.rotation;
            }
            BroadcastOwner(itemId);
            m_World.OnItemReturned(in item);
        }

        /// <summary>
        /// The host player's own hand grabbed an item. Call it the moment XRI selects (this is how the host wins
        /// ties). False when a client is holding it or it is locked: cancel the local selection.
        /// </summary>
        public bool LocalGrab(byte itemId)
        {
            if (m_Closed) return false;
            byte previousOwner = m_Items.Owner(itemId);
            if (!m_Items.HostGrab(itemId)) return false;
            BroadcastOwner(itemId);
            if (previousOwner != 0)
            {
                // it was in a client's free flight; the host now simulates it from where it is on the host
                var item = new ItemReturn { itemId = itemId, fromSlot = previousOwner, cause = ItemReturnCause.HostTookOver, rotation = NetQuat.Identity };
                m_World.OnItemReturned(in item);
            }
            return true;
        }

        /// <summary>The host player's own hand let go. The host keeps simulating the item.</summary>
        public void LocalRelease(byte itemId)
        {
            if (m_Closed) return;
            if (m_Items.HostRelease(itemId)) BroadcastOwner(itemId);
        }

        /// <summary>Takes an item away from whichever client simulates it (for example before the locker secures it). False if the host already had it.</summary>
        public bool TakeItem(byte itemId)
        {
            if (m_Closed || !m_Items.IsClientOwned(itemId)) return false;
            ReturnItem(itemId, ItemReturnCause.HostTookOver, false, default(ItemHandoffMsg));
            return true;
        }

        /// <summary>
        /// Adds a host-simulated item to the next send tick's pose stream (held or moving items, plus one resting
        /// item per tick as a refresh). May be called every frame: a later call for the same item replaces the
        /// earlier one. Call in priority order: each client gets at most 12 poses per datagram and poses relayed
        /// from other clients go first. Ignored for items a client simulates; the epoch is filled in when sending.
        /// </summary>
        public void QueueItemPose(byte itemId, NetVec3 position, NetQuat rotation, bool held, bool atRest)
        {
            if (m_Closed || !m_Items.Contains(itemId) || m_Items.Owner(itemId) != 0) return;
            var entry = new ItemPoseEntry { itemId = itemId, position = position, rotation = rotation, held = held, atRest = atRest };
            for (int i = 0; i < m_HostPoseCount; i++)
                if (m_HostPoses[i].itemId == itemId) { m_HostPoses[i] = entry; return; }
            if (m_HostPoseCount < m_HostPoses.Length) m_HostPoses[m_HostPoseCount++] = entry;
        }

        /// <summary>Drops a pose queued for this tick (a reliable message is about to say where the item is).</summary>
        void UnqueueItemPose(byte itemId)
        {
            for (int i = 0; i < m_HostPoseCount; i++)
            {
                if (m_HostPoses[i].itemId != itemId) continue;
                m_HostPoses[i] = m_HostPoses[--m_HostPoseCount];
                return;
            }
        }

        public void SendItemCollected(byte itemId, byte by)
        {
            var w = new NetWriter(m_Small);
            new ItemCollectedMsg { itemId = itemId, by = by }.Write(ref w);
            Broadcast(NetMsg.ItemCollected, ref w);
        }

        /// <summary>A host-simulated item has been slow for 0.5 s: tell everyone exactly where it rests.</summary>
        public void SendItemRest(byte itemId, NetVec3 position, NetQuat rotation)
        {
            if (!m_Items.Contains(itemId) || m_Items.Owner(itemId) != 0) return;
            UnqueueItemPose(itemId); // receivers apply reliable messages before the same datagram's streams
            var w = new NetWriter(m_Small);
            new ItemRestMsg { itemId = itemId, epoch = m_Items.Epoch(itemId), position = position, rotation = rotation }.Write(ref w);
            Broadcast(NetMsg.ItemRest, ref w);
        }

        public void SendItemValue(byte itemId, int value)
        {
            var w = new NetWriter(m_Small);
            new ItemValueMsg { itemId = itemId, value = ClampU16(value) }.Write(ref w);
            Broadcast(NetMsg.ItemValue, ref w);
        }

        /// <summary>The locker accepted an item. Locks it in the ownership table (taking it from a client if needed) and tells everyone.</summary>
        public void SendItemDeposited(byte itemId, byte by, int finalValue, int trickBonus, byte lockerSlot)
        {
            if (m_Closed) return;
            ItemOwnershipTable.Entry before = m_Items.Get(itemId);
            if (m_Items.Lock(itemId))
            {
                BroadcastOwner(itemId);
                if (before.owner != 0)
                {
                    var item = new ItemReturn { itemId = itemId, fromSlot = before.owner, cause = ItemReturnCause.HostTookOver, hasPose = before.hasPose, position = before.position, rotation = before.hasPose ? before.rotation : NetQuat.Identity };
                    m_World.OnItemReturned(in item);
                }
            }
            var w = new NetWriter(m_Small);
            new ItemDepositedMsg { itemId = itemId, by = by, finalValue = ClampU16(finalValue), trickBonus = ClampU16(trickBonus), lockerSlot = lockerSlot }.Write(ref w);
            Broadcast(NetMsg.ItemDeposited, ref w);
        }

        public void SendItemRecovered(byte itemId, NetVec3 position)
        {
            UnqueueItemPose(itemId);
            var w = new NetWriter(m_Small);
            new ItemRecoveredMsg { itemId = itemId, position = position }.Write(ref w);
            Broadcast(NetMsg.ItemRecovered, ref w);
        }

        static ushort ClampU16(int value) => value < 0 ? (ushort)0 : value > 65535 ? (ushort)65535 : (ushort)value;

        // ================================================================== host streams

        /// <summary>The host player's own avatar for this tick (from AvatarSampler in LateUpdate, before SendTick).</summary>
        public void SetLocalAvatar(in AvatarSnapshot avatar)
        {
            m_LocalAvatar = avatar;
            m_HasLocalAvatar = true;
            m_LocalAvatarFresh = true;
        }

        /// <summary>The creature's state for this tick. Call at 10 Hz (20 Hz in Investigate/Chase); it goes out with the next send tick.</summary>
        public void SetCreature(in CreatureSnapshot creature)
        {
            m_Creature = creature;
            m_CreatureFresh = true;
        }

        /// <summary>Mission clock and shared oxygen. Call at 2 Hz; it goes out with the next send tick.</summary>
        public void SetClock(float elapsedSeconds, float oxygenRemainingSeconds, byte flags)
        {
            m_Clock = new ClockMsg { elapsedSeconds = elapsedSeconds, oxygenRemainingSeconds = oxygenRemainingSeconds, flags = flags };
            m_ClockFresh = true;
        }

        // ================================================================== reliable broadcasts

        /// <summary>The host player changed handedness.</summary>
        public void SetLocalHandedness(byte handedness)
        {
            handedness &= 1;
            if (handedness == m_Handedness) return;
            m_Handedness = handedness;
            BroadcastRoster(0, RosterChange.Settings, handedness, -1);
        }

        public void SendWorldState(in WorldStateMsg state)
        {
            var w = new NetWriter(m_Scratch);
            state.Write(ref w);
            Broadcast(NetMsg.WorldState, ref w);
        }

        public void SendHostPause(bool on)
        {
            var w = new NetWriter(m_Small);
            new HostPauseMsg { on = on }.Write(ref w);
            Broadcast(NetMsg.HostPause, ref w);
        }

        /// <summary>
        /// Starts a contract for everyone and resets the ownership table to the item list. False (nothing sent)
        /// when there are more than <see cref="NetProtocol.MaxRunItems"/> items. A join still in its handshake is
        /// turned away with ContractInProgress.
        /// </summary>
        public bool SendRunStart(in RunStartParams parameters)
        {
            if (m_Closed) return false;
            var w = new NetWriter(m_Scratch);
            if (!RunStartCodec.Write(ref w, in parameters)) return false;
            for (int i = 1; i < m_Peers.Length; i++)
            {
                Peer peer = m_Peers[i];
                if (!peer.used || peer.confirmed) continue;
                SendRefuse(peer.endpoint, peer.nonce, RefuseReason.ContractInProgress);
                Free(peer);
            }
            m_Items.Reset(parameters.items != null ? parameters.items.Length : 0);
            Broadcast(NetMsg.RunStart, ref w);
            return true;
        }

        /// <summary>Answer to one client's ExtractRequest: these slots are not aboard.</summary>
        public void SendExtractRefused(byte slot, byte missingSlotsMask)
        {
            if (slot == 0 || slot >= m_Peers.Length || !m_Peers[slot].used || !m_Peers[slot].confirmed) return;
            var w = new NetWriter(m_Small);
            new ExtractRefusedMsg { missingSlotsMask = missingSlotsMask }.Write(ref w);
            SendTo(m_Peers[slot], NetMsg.ExtractRefused, ref w);
        }

        public void SendRunEnded(in RunEndedMsg result)
        {
            var w = new NetWriter(m_Small);
            result.Write(ref w);
            Broadcast(NetMsg.RunEnded, ref w);
        }

        public void SendReturnedToHub()
        {
            var w = new NetWriter(m_Small, 0, 0);
            Broadcast(NetMsg.ReturnedToHub, ref w);
        }

        public void SendDoorState(byte doorId, bool open)
        {
            var w = new NetWriter(m_Small);
            new DoorStateMsg { doorId = doorId, open = open }.Write(ref w);
            Broadcast(NetMsg.DoorState, ref w);
        }

        public void SendPointCut(byte panel, byte index)
        {
            var w = new NetWriter(m_Small);
            new PointCutMsg { panel = panel, index = index }.Write(ref w);
            Broadcast(NetMsg.PointCut, ref w);
        }

        public void SendPanelOpened(byte panel, byte by, NetVec3 plateImpulse, NetVec3 plateTorque)
        {
            var w = new NetWriter(m_Small);
            new PanelOpenedMsg { panel = panel, by = by, plateImpulse = plateImpulse, plateTorque = plateTorque }.Write(ref w);
            Broadcast(NetMsg.PanelOpened, ref w);
        }

        public void SendCreatureStateChanged(EnemyState state, byte targetSlot)
        {
            var w = new NetWriter(m_Small);
            new CreatureStateChangedMsg { state = state, targetSlot = targetSlot }.Write(ref w);
            Broadcast(NetMsg.CreatureStateChanged, ref w);
        }

        public void SendTranquilized(float seconds)
        {
            var w = new NetWriter(m_Small);
            new TranquilizedMsg { seconds = seconds }.Write(ref w);
            Broadcast(NetMsg.Tranquilized, ref w);
        }

        public void SendCatchStarted(byte victimSlot)
        {
            var w = new NetWriter(m_Small);
            new CatchStartedMsg { victimSlot = victimSlot }.Write(ref w);
            Broadcast(NetMsg.CatchStarted, ref w);
        }

        /// <summary>Tells everyone except the shooter to draw a dart tracer.</summary>
        public void SendTranqTracer(byte by, NetVec3 from, NetVec3 to)
        {
            var w = new NetWriter(m_Small);
            new TranqTracerMsg { by = by, from = from, to = to }.Write(ref w);
            Broadcast(NetMsg.TranqTracer, ref w, by);
        }

        public void SendBlackoutStarted(float seconds)
        {
            var w = new NetWriter(m_Small);
            new BlackoutStartedMsg { seconds = seconds }.Write(ref w);
            Broadcast(NetMsg.BlackoutStarted, ref w);
        }

        public void SendBlackoutEnded(bool instant)
        {
            var w = new NetWriter(m_Small);
            new BlackoutEndedMsg { instant = instant }.Write(ref w);
            Broadcast(NetMsg.BlackoutEnded, ref w);
        }

        public void SendWireChanged(byte index, NetWireChange change, byte by)
        {
            var w = new NetWriter(m_Small);
            new WireChangedMsg { index = index, change = change, by = by }.Write(ref w);
            Broadcast(NetMsg.WireChanged, ref w);
        }

        void BroadcastRoster(byte slot, RosterChange change, byte handedness, int exceptSlot)
        {
            var w = new NetWriter(m_Small);
            new RosterChangedMsg { slot = slot, change = change, handedness = handedness }.Write(ref w);
            Broadcast(NetMsg.RosterChanged, ref w, exceptSlot);
        }

        void Broadcast(byte type, ref NetWriter w, int exceptSlot = -1)
        {
            if (m_Closed) return;
            for (int i = 1; i < m_Peers.Length; i++)
            {
                Peer peer = m_Peers[i];
                if (i == exceptSlot || !peer.used || !peer.confirmed) continue;
                SendTo(peer, type, ref w);
            }
        }

        void SendTo(Peer peer, byte type, ref NetWriter w)
        {
            if (w.Overflow) { m_Stats.malformed++; return; }
            // A full backlog latches channel.Overflowed; RunTimers then drops the peer.
            if (peer.channel.Enqueue(type, w.Buffer, 0, w.Position))
                Tap?.Invoke(true, peer.slot, type, w.Buffer, 0, w.Position, m_Now);
        }

        // ================================================================== sending

        void SendDatagrams(Peer peer, bool streams, bool force)
        {
            // the writer stops one byte short of the cap: that byte is the closing End marker
            var w = new NetWriter(m_Send, 0, NetProtocol.MaxDatagram - 1);
            ushort seq = WriteHeader(peer, ref w);
            if (streams && !peer.silent) WriteStreams(peer, ref w);
            peer.channel.WriteDue(ref w, seq, m_Now, force);
            m_Send[w.Position] = NetMsg.End;
            SendRaw(peer.endpoint, w.Position + 1);

            // Reliable messages that did not fit go out in up to three reliable-only datagrams.
            for (int extra = 0; extra < k_MaxExtraDatagrams && !force && peer.channel.HasDue(m_Now); extra++)
            {
                w = new NetWriter(m_Send, 0, NetProtocol.MaxDatagram - 1);
                seq = WriteHeader(peer, ref w);
                if (peer.channel.WriteDue(ref w, seq, m_Now) == 0) break;
                m_Send[w.Position] = NetMsg.End;
                SendRaw(peer.endpoint, w.Position + 1);
            }
        }

        ushort WriteHeader(Peer peer, ref NetWriter w)
        {
            var header = new DatagramHeader
            {
                protocol = m_Config.protocol,
                connId = peer.connId,
                senderSlot = 0,
                seq = peer.channel.NextSendSequence(),
                timeMs = DatagramHeader.ToMs(m_Now),
            };
            peer.channel.GetAck(out header.ack, out header.ackBits);
            header.Write(ref w);
            return header.seq;
        }

        void WriteStreams(Peer to, ref NetWriter w)
        {
            if (m_HasLocalAvatar && m_LocalAvatarFresh) WriteAvatar(to, 0, in m_LocalAvatar, ref w);
            for (int i = 1; i < m_Peers.Length; i++)
            {
                Peer other = m_Peers[i];
                if (other == to || !other.used || !other.confirmed || !other.avatarFresh) continue;
                WriteAvatar(to, other.slot, in other.avatar, ref w);
            }

            if (m_CreatureFresh)
            {
                w.U8(NetMsg.CreatureState);
                int start = w.Position;
                m_Creature.Write(ref w);
                Tap?.Invoke(true, to.slot, NetMsg.CreatureState, m_Send, start, w.Position - start, m_Now);
            }
            if (m_ClockFresh)
            {
                w.U8(NetMsg.Clock);
                int start = w.Position;
                m_Clock.Write(ref w);
                Tap?.Invoke(true, to.slot, NetMsg.Clock, m_Send, start, w.Position - start, m_Now);
            }

            int count = 0;
            for (int i = 1; i < m_Peers.Length && count < ItemPoseBatch.MaxEntries; i++)
            {
                Peer other = m_Peers[i];
                if (other == to || !other.used) continue;
                for (int p = 0; p < other.poseCount && count < ItemPoseBatch.MaxEntries; p++) m_PoseWrite.entries[count++] = other.poses[p];
            }
            for (int p = 0; p < m_HostPoseCount && count < ItemPoseBatch.MaxEntries; p++)
            {
                byte id = m_HostPoses[p].itemId;
                if (m_Items.Owner(id) != 0) continue; // a client took it since it was queued
                m_PoseWrite.entries[count] = m_HostPoses[p];
                m_PoseWrite.entries[count].epoch = (byte)(m_Items.Epoch(id) & ItemPoseEntry.EpochMask);
                count++;
            }
            if (count > 0)
            {
                m_PoseWrite.count = count;
                w.U8(NetMsg.ItemPoses);
                int start = w.Position;
                m_PoseWrite.Write(ref w);
                Tap?.Invoke(true, to.slot, NetMsg.ItemPoses, m_Send, start, w.Position - start, m_Now);
            }
            WriteCutProgress(to, ref w);
        }

        void WriteAvatar(Peer to, byte slot, in AvatarSnapshot avatar, ref NetWriter w)
        {
            w.U8(NetMsg.RemoteAvatar);
            int start = w.Position;
            w.U8(slot);
            avatar.Write(ref w);
            Tap?.Invoke(true, to.slot, NetMsg.RemoteAvatar, m_Send, start, w.Position - start, m_Now);
        }
    }
}

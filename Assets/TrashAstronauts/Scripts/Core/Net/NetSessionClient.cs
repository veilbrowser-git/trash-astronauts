using System;

namespace TrashAstronauts.Core.Net
{
    public enum ClientState : byte
    {
        /// <summary>Not started.</summary>
        Idle = 0,
        /// <summary>Hello is being sent; waiting for Accept or Refuse.</summary>
        Connecting = 1,
        /// <summary>Accepted: session datagrams flow. <see cref="NetSessionClient.Welcomed"/> turns true a moment later.</summary>
        Connected = 2,
        /// <summary>Over; see <see cref="NetSessionClient.EndReason"/>.</summary>
        Ended = 3,
    }

    public sealed class NetClientConfig
    {
        public byte protocol = NetProtocol.Version;
        /// <summary>NetIdTable.ContentHash of this build.</summary>
        public uint contentHash;
        /// <summary>The local player's handedness (0 right, 1 left).</summary>
        public byte handedness;
        /// <summary>Seeds the client nonce.</summary>
        public int seed = 1;
    }

    /// <summary>
    /// The client end of a session. Talks only to the host: Hello until Accept/Refuse, then 20 Hz datagrams with
    /// the local avatar, owned item poses, noises and reliable requests; dispatches everything the host sends
    /// into <see cref="IClientWorld"/>. Pure C#, explicit time, same call pattern as <see cref="NetSessionHost"/>:
    /// Poll(now) from Update, SendTick(now) from LateUpdate.
    /// </summary>
    public sealed class NetSessionClient : IDisposable
    {
        const int k_MaxDatagramsPerPoll = 512;
        const int k_MaxExtraDatagrams = 3;
        const int k_NoiseCapacity = 8;
        const int k_PoseCapacity = NetProtocol.MaxItemPosesPerDatagram;

        readonly INetTransport m_Transport;
        readonly IClientWorld m_World;
        readonly NetClientConfig m_Config;
        readonly ReliableChannel m_Channel = new ReliableChannel();
        readonly NetStats m_Stats = new NetStats();
        readonly byte[] m_Receive = new byte[NetProtocol.MaxDatagram + 64];
        readonly byte[] m_Send = new byte[NetProtocol.MaxDatagram];
        readonly byte[] m_Small = new byte[64];
        readonly NoiseMsg[] m_Noises = new NoiseMsg[k_NoiseCapacity];
        int m_NoiseCount;
        ItemPoseBatch m_PoseWrite;
        ItemPoseBatch m_PoseRead;
        WelcomeMsg m_Welcome;
        WorldStateMsg m_WorldState;
        VoiceMsg m_VoiceRead;
        VoiceMsg m_VoiceWrite;

        NetEndpoint m_Host;
        uint m_Nonce;
        ushort m_ConnId;
        byte m_Handedness;
        double m_Now;
        double m_ConnectStarted;
        double m_LastHello = double.NegativeInfinity;
        double m_LastSend = double.NegativeInfinity;
        double m_LastHeard;
        ushort m_StreamSeq;
        bool m_HasStreamSeq;
        double m_ClockOffset;
        bool m_HasClock;
        bool m_Disposed;
        AvatarSnapshot m_Avatar;
        bool m_HasAvatar;
        bool m_Away;

        /// <summary>Optional recorder; see <see cref="NetMessageTap"/>.</summary>
        public NetMessageTap Tap;

        /// <summary>
        /// Proximity voice chat: raised (inside <see cref="Poll"/>) for every voice frame of another crew member
        /// (the host's own, or another client's relayed by the host). Never for this client's own voice.
        /// </summary>
        public event NetVoiceHandler VoiceReceived;

        public NetClientConfig Config => m_Config;
        public NetStats Stats => m_Stats;
        public INetTransport Transport => m_Transport;
        public ClientState State { get; private set; }
        /// <summary>The session slot the host gave us (1..3). 0 until accepted.</summary>
        public byte LocalSlot { get; private set; }
        public ushort ConnId => m_ConnId;
        public NetEndpoint Host => m_Host;
        /// <summary>True once Welcome has been applied.</summary>
        public bool Welcomed { get; private set; }
        /// <summary>Why the host said no (when <see cref="EndReason"/> is Refused).</summary>
        public RefuseReason LastRefusal { get; private set; }
        public SessionEndReason EndReason { get; private set; }
        /// <summary>True after 1.5 s without a datagram from the host (HOST PAUSED).</summary>
        public bool HostSilent { get; private set; }
        /// <summary>Reliable messages waiting to be acknowledged by the host.</summary>
        public int Backlog => m_Channel.Backlog;
        public double Now => m_Now;

        /// <summary>The session takes ownership of <paramref name="transport"/> (bound to an ephemeral port) and disposes it in <see cref="Dispose"/>.</summary>
        public NetSessionClient(INetTransport transport, IClientWorld world, NetClientConfig config)
        {
            m_Transport = transport ?? throw new ArgumentNullException(nameof(transport));
            m_World = world ?? throw new ArgumentNullException(nameof(world));
            m_Config = config ?? new NetClientConfig();
            m_Handedness = (byte)(m_Config.handedness & 1);
            uint nonce = (uint)new Random(m_Config.seed).Next(1, int.MaxValue);
            m_Nonce = nonce ^ ((uint)m_Config.seed * 2654435761u) | 1u;
            m_PoseWrite.entries = new ItemPoseEntry[k_PoseCapacity];
            m_PoseRead.entries = new ItemPoseEntry[k_PoseCapacity];
            m_VoiceRead.data = new byte[VoiceMsg.MaxFrames * VoiceMsg.MaxFrameBytes];
            m_VoiceWrite.data = new byte[VoiceMsg.MaxFrames * VoiceMsg.MaxFrameBytes];
        }

        // ================================================================== lifecycle

        /// <summary>Starts (or restarts) joining a host. Hello goes out now and every 250 ms until an answer or 5 s.</summary>
        public void Connect(NetEndpoint host, double now)
        {
            if (m_Disposed) return;
            m_Now = now;
            m_Host = host;
            m_Nonce = m_Nonce * 1664525u + 1013904223u | 1u; // a fresh nonce per attempt
            State = ClientState.Connecting;
            EndReason = SessionEndReason.None;
            LastRefusal = RefuseReason.None;
            LocalSlot = 0;
            m_ConnId = 0;
            Welcomed = false;
            HostSilent = false;
            m_HasClock = false;
            m_HasStreamSeq = false;
            m_NoiseCount = 0;
            m_PoseWrite.count = 0;
            m_ConnectStarted = now;
            m_LastHello = double.NegativeInfinity;
            m_LastSend = double.NegativeInfinity;
            SendHello();
        }

        /// <summary>Poll then SendTick.</summary>
        public void Tick(double now)
        {
            Poll(now);
            SendTick(now);
        }

        /// <summary>Receives and dispatches everything waiting, then runs the connect / silence / timeout timers.</summary>
        public void Poll(double now)
        {
            if (m_Disposed) return;
            m_Now = now;
            for (int i = 0; i < k_MaxDatagramsPerPoll; i++)
            {
                if (!m_Transport.Receive(out NetEndpoint from, m_Receive, out int length)) break;
                m_Stats.datagramsReceived++;
                m_Stats.bytesReceived += length;
                if (State == ClientState.Connecting || State == ClientState.Connected) HandleDatagram(from, length);
            }

            if (State == ClientState.Connecting)
            {
                if (now - m_ConnectStarted >= NetProtocol.ConnectTimeoutSeconds) End(SessionEndReason.ConnectTimeout, true);
                else if (now - m_LastHello >= NetProtocol.HelloInterval) SendHello();
            }
            else if (State == ClientState.Connected)
            {
                double silence = now - m_LastHeard;
                if (m_Channel.Overflowed)
                {
                    // A request could not be queued (512 already waiting): the ordered stream has a hole. Leave,
                    // as the host drops a client in the same state, and tell the host if it can still hear us.
                    SendQuit();
                    SendQuit();
                    End(SessionEndReason.BacklogOverflow, true);
                }
                else if (silence >= NetProtocol.HostTimeoutSeconds) End(SessionEndReason.HostTimeout, true);
                else if (silence >= NetProtocol.AwaySeconds && !HostSilent)
                {
                    HostSilent = true;
                    m_World.OnHostSilent(true);
                }
            }
        }

        /// <summary>
        /// Sends one datagram to the host when the next 50 ms send tick is due. The tick keeps its phase, so the
        /// rate is 20 Hz at any frame rate (a tick lands on the first frame at or after its due time).
        /// </summary>
        public void SendTick(double now)
        {
            if (m_Disposed || State != ClientState.Connected) return;
            m_Now = now;
            if (now - m_LastSend < NetProtocol.SendInterval - 1e-6) return;
            m_LastSend += NetProtocol.SendInterval;
            if (!(now - m_LastSend < NetProtocol.SendInterval)) m_LastSend = now; // first tick, or a stall: do not burst to catch up
            SendDatagrams(true, false);
        }

        /// <summary>Sends pending reliable requests right now (grab, release, door, start, extract happen on the frame).</summary>
        public void Flush(double now)
        {
            if (m_Disposed || State != ClientState.Connected) return;
            m_Now = now;
            if (m_Channel.HasDue(now)) SendDatagrams(false, false);
        }

        /// <summary>
        /// The local player pressed LEAVE: the reliable Bye is sent three times, then the connectionless Quit
        /// twice (it needs no room in the reliable window), and the session ends. IClientWorld.OnSessionEnded is
        /// NOT called.
        /// </summary>
        public void Leave(double now)
        {
            if (m_Disposed) return;
            m_Now = now;
            if (State == ClientState.Connected)
            {
                var w = new NetWriter(m_Small, 0, 0);
                Enqueue(NetMsg.Bye, ref w);
                for (int i = 0; i < 3; i++) SendDatagrams(false, true);
                SendQuit();
                SendQuit();
            }
            if (State != ClientState.Ended && State != ClientState.Idle) End(SessionEndReason.LocalLeave, false);
        }

        /// <summary>Disposes the transport. Sends nothing; call <see cref="Leave"/> first for a clean goodbye.</summary>
        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            if (State == ClientState.Connecting || State == ClientState.Connected)
            {
                State = ClientState.Ended;
                EndReason = SessionEndReason.LocalLeave;
            }
            m_Transport.Dispose();
        }

        void End(SessionEndReason reason, bool notify)
        {
            if (State == ClientState.Ended) return;
            State = ClientState.Ended;
            EndReason = reason;
            HostSilent = false;
            if (notify) m_World.OnSessionEnded(reason);
        }

        // ================================================================== host clock

        /// <summary>
        /// The host's clock right now, estimated from datagram time stamps (it trails the true host clock by about
        /// the one-way latency, which is what snapshot rendering wants). Equals <paramref name="now"/> before any
        /// host datagram has arrived.
        /// </summary>
        public double HostTime(double now) => m_HasClock ? now + m_ClockOffset : now;

        /// <summary>Where to sample SnapshotBuffers: <see cref="HostTime"/> minus the 100 ms interpolation delay.</summary>
        public double RenderTime(double now) => HostTime(now) - SnapshotBuffer<PoseSnapshot>.InterpolationDelay;

        void UpdateClock(double hostTime)
        {
            double sample = hostTime - m_Now;
            if (!m_HasClock) { m_HasClock = true; m_ClockOffset = sample; return; }
            // Follow the least-delayed datagrams quickly, drift toward later ones slowly.
            if (sample > m_ClockOffset) m_ClockOffset = sample;
            else m_ClockOffset += (sample - m_ClockOffset) * 0.02;
        }

        // ================================================================== local streams and requests

        /// <summary>The local rig for this tick (AvatarSampler, LateUpdate, before SendTick). The Away flag is managed by <see cref="SetAway"/>.</summary>
        public void SetLocalAvatar(in AvatarSnapshot avatar)
        {
            m_Avatar = avatar;
            m_HasAvatar = true;
        }

        /// <summary>
        /// Focus or tracking was lost / regained, or the app paused / resumed. Sets the avatar's Away flag, sends
        /// a reliable PauseNotice and flushes at once (it may be the last thing the headset gets to send).
        /// </summary>
        public void SetAway(bool away, double now)
        {
            if (m_Away == away) return;
            m_Away = away;
            if (State != ClientState.Connected) return;
            var w = new NetWriter(m_Small);
            new PauseNoticeMsg { on = away }.Write(ref w);
            Enqueue(NetMsg.PauseNotice, ref w);
            Flush(now);
        }

        public bool IsAway => m_Away;

        /// <summary>
        /// Adds an item this peer simulates to the next send tick's pose stream (at most 12 items per tick). May be
        /// called every frame: a later call for the same item replaces the earlier one. epoch is ItemAuthority.WireEpoch.
        /// Call it for every item whose ItemAuthority.ShouldStream is true, also while it is held still: the host
        /// treats a held item it has had no pose for in 3 s as abandoned and takes it back.
        /// </summary>
        public void QueueItemPose(byte itemId, NetVec3 position, NetQuat rotation, byte epoch, bool held)
        {
            if (State != ClientState.Connected) return;
            var entry = new ItemPoseEntry { itemId = itemId, position = position, rotation = rotation, epoch = (byte)(epoch & ItemPoseEntry.EpochMask), held = held };
            for (int i = 0; i < m_PoseWrite.count; i++)
                if (m_PoseWrite.entries[i].itemId == itemId) { m_PoseWrite.entries[i] = entry; return; }
            if (m_PoseWrite.count < k_PoseCapacity) m_PoseWrite.entries[m_PoseWrite.count++] = entry;
        }

        /// <summary>A gameplay noise made by the local player (unreliable, goes with the next send tick; at most 8 queued).</summary>
        public void SendNoise(NetVec3 position, float loudness, NoiseKind kind)
        {
            if (State != ClientState.Connected || m_NoiseCount >= k_NoiseCapacity) return;
            m_Noises[m_NoiseCount++] = new NoiseMsg { position = position, loudness = loudness, kind = kind };
        }

        public void SendSettingsChanged(byte handedness)
        {
            m_Handedness = (byte)(handedness & 1);
            var w = new NetWriter(m_Small);
            new SettingsChangedMsg { handedness = m_Handedness }.Write(ref w);
            Enqueue(NetMsg.SettingsChanged, ref w);
        }

        public void SendStartRequest()
        {
            var w = new NetWriter(m_Small, 0, 0);
            Enqueue(NetMsg.StartRequest, ref w);
        }

        public void SendExtractRequest()
        {
            var w = new NetWriter(m_Small, 0, 0);
            Enqueue(NetMsg.ExtractRequest, ref w);
        }

        /// <summary>After ItemAuthority.BeginGrab returned true.</summary>
        public void SendGrabRequest(byte itemId, byte epochSeen, byte hand)
        {
            var w = new NetWriter(m_Small);
            new GrabRequestMsg { itemId = itemId, epochSeen = epochSeen, hand = hand }.Write(ref w);
            Enqueue(NetMsg.GrabRequest, ref w);
        }

        /// <summary>After ItemAuthority.Release returned true; epoch is ItemAuthority.WireEpoch.</summary>
        public void SendItemReleased(byte itemId, byte epoch, NetVec3 position, NetQuat rotation, NetVec3 releasePosition)
        {
            var w = new NetWriter(m_Small);
            new ItemReleasedMsg { itemId = itemId, epoch = epoch, position = position, rotation = rotation, releasePosition = releasePosition }.Write(ref w);
            Enqueue(NetMsg.ItemReleased, ref w);
        }

        public void SendItemThrown(byte itemId, byte epoch, NetVec3 linearVelocity, NetVec3 angularVelocity, float throwSpeed, bool wasThrown)
        {
            var w = new NetWriter(m_Small);
            new ItemThrownMsg { itemId = itemId, epoch = epoch, linearVelocity = linearVelocity, angularVelocity = angularVelocity, throwSpeed = throwSpeed, wasThrown = wasThrown }.Write(ref w);
            Enqueue(NetMsg.ItemThrown, ref w);
        }

        /// <summary>After ItemAuthority.Tick returned true; epoch is the handoffEpoch it gave.</summary>
        public void SendItemHandoff(byte itemId, byte epoch, NetVec3 position, NetQuat rotation, NetVec3 linearVelocity, NetVec3 angularVelocity)
        {
            var w = new NetWriter(m_Small);
            new ItemHandoffMsg { itemId = itemId, epoch = epoch, position = position, rotation = rotation, linearVelocity = linearVelocity, angularVelocity = angularVelocity }.Write(ref w);
            Enqueue(NetMsg.ItemHandoff, ref w);
        }

        public void SendItemDamageReport(byte itemId, int newValue)
        {
            var w = new NetWriter(m_Small);
            new ItemDamageReportMsg { itemId = itemId, newValue = newValue < 0 ? (ushort)0 : newValue > 65535 ? (ushort)65535 : (ushort)newValue }.Write(ref w);
            Enqueue(NetMsg.ItemDamageReport, ref w);
        }

        public void SendDoorRequest(byte doorId, bool desiredOpen)
        {
            var w = new NetWriter(m_Small);
            new DoorRequestMsg { doorId = doorId, desiredOpen = desiredOpen }.Write(ref w);
            Enqueue(NetMsg.DoorRequest, ref w);
        }

        public void SendPointCutReport(byte panel, byte index)
        {
            var w = new NetWriter(m_Small);
            new PointCutReportMsg { panel = panel, index = index }.Write(ref w);
            Enqueue(NetMsg.PointCutReport, ref w);
        }

        public void SendTranqHitReport(NetVec3 origin, NetVec3 hitPoint)
        {
            var w = new NetWriter(m_Small);
            new TranqHitReportMsg { origin = origin, hitPoint = hitPoint }.Write(ref w);
            Enqueue(NetMsg.TranqHitReport, ref w);
        }

        public void SendWireReport(byte index, NetWireChange change)
        {
            var w = new NetWriter(m_Small);
            new WireReportMsg { index = index, change = change }.Write(ref w);
            Enqueue(NetMsg.WireReport, ref w);
        }

        void Enqueue(byte type, ref NetWriter w)
        {
            if (State != ClientState.Connected || w.Overflow) return;
            if (m_Channel.Enqueue(type, w.Buffer, 0, w.Position))
                Tap?.Invoke(true, 0, type, w.Buffer, 0, w.Position, HostTime(m_Now));
        }

        // ================================================================== sending

        void SendHello()
        {
            m_LastHello = m_Now;
            var hello = new HelloMsg { protocol = m_Config.protocol, contentHash = m_Config.contentHash, clientNonce = m_Nonce, handedness = m_Handedness };
            var w = new NetWriter(m_Send);
            hello.WriteDatagram(ref w);
            SendRaw(w.Position);
        }

        /// <summary>The goodbye outside the reliable stream; see <see cref="QuitMsg"/>.</summary>
        void SendQuit()
        {
            if (m_ConnId == 0) return;
            var w = new NetWriter(m_Send);
            new QuitMsg { protocol = m_Config.protocol, connId = m_ConnId }.WriteDatagram(ref w);
            SendRaw(w.Position);
        }

        void SendRaw(int length)
        {
            m_Transport.Send(m_Host, m_Send, length);
            m_Stats.datagramsSent++;
            m_Stats.bytesSent += length;
            if (length > m_Stats.largestDatagram) m_Stats.largestDatagram = length;
        }

        /// <summary>
        /// Sends this player's voice to the host (who relays it to the rest of the crew): <paramref name="frameCount"/>
        /// (1 or 2) consecutive encoded frames of <paramref name="frameBytes"/> each, in a datagram of its own, at once.
        /// False before the Welcome or for frames that do not fit a <see cref="VoiceMsg"/>.
        /// </summary>
        public bool SendVoice(ushort sequence, byte[] frames, int offset, int frameBytes, int frameCount)
        {
            if (m_Disposed || State != ClientState.Connected || !Welcomed) return false;
            if (!m_VoiceWrite.Set(LocalSlot, sequence, frames, offset, frameBytes, frameCount)) return false;
            var w = new NetWriter(m_Send, 0, NetProtocol.MaxDatagram - 1);
            WriteHeader(ref w);
            w.U8(NetMsg.Voice);
            int start = w.Position;
            m_VoiceWrite.Write(ref w);
            if (w.Overflow) return false;
            Tap?.Invoke(true, 0, NetMsg.Voice, m_Send, start, w.Position - start, HostTime(m_Now));
            m_Send[w.Position] = NetMsg.End;
            SendRaw(w.Position + 1);
            return true;
        }

        void SendDatagrams(bool streams, bool force)
        {
            // the writer stops one byte short of the cap: that byte is the closing End marker
            var w = new NetWriter(m_Send, 0, NetProtocol.MaxDatagram - 1);
            ushort seq = WriteHeader(ref w);
            if (streams) WriteStreams(ref w);
            m_Channel.WriteDue(ref w, seq, m_Now, force);
            m_Send[w.Position] = NetMsg.End;
            SendRaw(w.Position + 1);

            for (int extra = 0; extra < k_MaxExtraDatagrams && !force && m_Channel.HasDue(m_Now); extra++)
            {
                w = new NetWriter(m_Send, 0, NetProtocol.MaxDatagram - 1);
                seq = WriteHeader(ref w);
                if (m_Channel.WriteDue(ref w, seq, m_Now) == 0) break;
                m_Send[w.Position] = NetMsg.End;
                SendRaw(w.Position + 1);
            }
        }

        ushort WriteHeader(ref NetWriter w)
        {
            var header = new DatagramHeader
            {
                protocol = m_Config.protocol,
                connId = m_ConnId,
                senderSlot = LocalSlot,
                seq = m_Channel.NextSendSequence(),
                timeMs = DatagramHeader.ToMs(m_Now),
            };
            m_Channel.GetAck(out header.ack, out header.ackBits);
            header.Write(ref w);
            return header.seq;
        }

        void WriteStreams(ref NetWriter w)
        {
            double time = HostTime(m_Now);
            if (m_HasAvatar)
            {
                AvatarSnapshot avatar = m_Avatar;
                if (m_Away) avatar.flags |= AvatarFlags.Away;
                else avatar.flags &= unchecked((byte)~AvatarFlags.Away);
                w.U8(NetMsg.AvatarState);
                int start = w.Position;
                avatar.Write(ref w);
                Tap?.Invoke(true, 0, NetMsg.AvatarState, m_Send, start, w.Position - start, time);
            }
            if (m_PoseWrite.count > 0)
            {
                w.U8(NetMsg.ItemPoses);
                int start = w.Position;
                m_PoseWrite.Write(ref w);
                Tap?.Invoke(true, 0, NetMsg.ItemPoses, m_Send, start, w.Position - start, time);
                m_PoseWrite.count = 0;
            }
            for (int i = 0; i < m_NoiseCount; i++)
            {
                w.U8(NetMsg.Noise);
                int start = w.Position;
                m_Noises[i].Write(ref w);
                Tap?.Invoke(true, 0, NetMsg.Noise, m_Send, start, w.Position - start, time);
            }
            m_NoiseCount = 0;
        }

        // ================================================================== receiving

        void HandleDatagram(NetEndpoint from, int length)
        {
            if (from != m_Host) { m_Stats.stray++; return; }
            if (length < 3) { m_Stats.malformed++; return; }
            Connectionless.TryPeekConnId(m_Receive, length, out ushort connId);
            if (connId == 0) HandleConnectionless(length);
            else HandleSessionDatagram(length);
        }

        void HandleConnectionless(int length)
        {
            var r = new NetReader(m_Receive, 0, length);
            if (!Connectionless.TryReadFrame(ref r, out byte protocol, out byte type)) { m_Stats.malformed++; return; }
            switch (type)
            {
                case NetMsg.Accept:
                {
                    if (!AcceptMsg.Read(ref r, protocol, out AcceptMsg accept)) { m_Stats.malformed++; return; }
                    if (State != ClientState.Connecting || accept.clientNonce != m_Nonce) { m_Stats.stray++; return; }
                    m_ConnId = accept.connId;
                    LocalSlot = accept.slot;
                    m_Channel.Reset(accept.connId);
                    m_LastHeard = m_Now;
                    State = ClientState.Connected;
                    // The host confirms the join on our first session datagram, so do not wait for the send tick.
                    m_LastSend = m_Now;
                    SendDatagrams(true, false);
                    break;
                }
                case NetMsg.Refuse:
                {
                    if (!RefuseMsg.Read(ref r, protocol, out RefuseMsg refuse)) { m_Stats.malformed++; return; }
                    // Also honoured just after Accept (before Welcome): the host may take a slot back when a contract
                    // starts during the handshake.
                    bool joining = State == ClientState.Connecting || (State == ClientState.Connected && !Welcomed);
                    if (!joining || refuse.clientNonce != m_Nonce) { m_Stats.stray++; return; }
                    LastRefusal = refuse.reason;
                    End(SessionEndReason.Refused, true);
                    break;
                }
                case NetMsg.Gone:
                {
                    if (!GoneMsg.Read(ref r, protocol, out GoneMsg gone)) { m_Stats.malformed++; return; }
                    if (State != ClientState.Connected || gone.connId != m_ConnId) { m_Stats.stray++; return; }
                    End(gone.reason == SessionCloseReason.HostLeft ? SessionEndReason.HostClosed : SessionEndReason.Dropped, true);
                    break;
                }
                default:
                    m_Stats.unknownMessages++;
                    break;
            }
        }

        void HandleSessionDatagram(int length)
        {
            var r = new NetReader(m_Receive, 0, length);
            if (!DatagramHeader.Read(ref r, out DatagramHeader header)) { m_Stats.malformed++; return; }
            if (State != ClientState.Connected || header.protocol != m_Config.protocol || header.connId != m_ConnId || header.senderSlot != 0) { m_Stats.stray++; return; }
            if (!m_Channel.IsNewDatagram(header.seq)) { m_Stats.duplicates++; m_Channel.OnDatagramReceived(header.seq); return; }

            // Pass 1: validate the whole body before anything is applied or acknowledged (see NetSessionHost).
            int bodyStart = r.Position;
            if (!ValidateBody(ref r)) { m_Stats.malformed++; return; }
            m_Channel.OnDatagramReceived(header.seq);

            // back after a gap (a burst of loss, a host hitch): resend what it missed now, not after a backoff
            if (m_Now - m_LastHeard >= NetProtocol.HeardAgainSeconds) m_Channel.ResetBackoff();
            m_LastHeard = m_Now;
            double hostTime = DatagramHeader.ToSeconds(header.timeMs);
            UpdateClock(hostTime);
            if (HostSilent)
            {
                HostSilent = false;
                m_World.OnHostSilent(false);
            }
            m_Channel.OnAck(header.ack, header.ackBits);

            // Pass 2: reliable messages first. The host queued them before it sampled this datagram's streams
            // (ItemOwner with a new epoch comes before the first pose that carries that epoch).
            r = new NetReader(m_Receive, bodyStart, length - bodyStart);
            while (!r.AtEnd)
            {
                byte type = r.U8();
                if (type == NetMsg.End) break;
                if (NetMsg.IsReliable(type))
                {
                    ushort relSeq = r.U16();
                    int size = r.U16();
                    if (r.Failed || size > r.Remaining) break;
                    m_Channel.Accept(relSeq, type, m_Receive, r.Position, size);
                    r.Skip(size);
                }
                else if (!SkipStream(type, ref r)) break;
            }
            while (State == ClientState.Connected && m_Channel.TryDeliver(out byte type, out byte[] data, out int size))
            {
                Tap?.Invoke(false, 0, type, data, 0, size, hostTime);
                var mr = new NetReader(data, 0, size);
                if (!Dispatch(type, ref mr, hostTime)) m_Stats.malformed++;
            }
            if (State != ClientState.Connected) return; // SessionClosed

            // Pass 3: the streams. Not before the Welcome has been applied (the game has no proxies and no reset
            // mission to apply them to; this happens when the datagram carrying the Welcome is lost and the next
            // tick's arrives first), and not from a datagram that was overtaken by a newer one.
            // Voice is the exception: its frames carry their own sequence numbers (the jitter buffer reorders them).
            if (!Welcomed) return;
            bool stale = m_HasStreamSeq && (short)(header.seq - m_StreamSeq) <= 0;
            if (stale) m_Stats.staleStreams++;
            else
            {
                m_StreamSeq = header.seq;
                m_HasStreamSeq = true;
            }
            r = new NetReader(m_Receive, bodyStart, length - bodyStart);
            while (!r.AtEnd)
            {
                byte type = r.U8();
                if (type == NetMsg.End) break;
                if (NetMsg.IsReliable(type))
                {
                    r.U16();
                    r.Skip(r.U16());
                    continue;
                }
                if (stale && type != NetMsg.Voice)
                {
                    if (!SkipStream(type, ref r)) break;
                    continue;
                }
                int start = r.Position;
                if (!Dispatch(type, ref r, hostTime)) break;
                Tap?.Invoke(false, 0, type, m_Receive, start, r.Position - start, hostTime);
                if (State != ClientState.Connected) return;
            }
        }

        /// <summary>
        /// Walks a datagram body without applying anything. False if any message is truncated, garbled or not one a
        /// host may send, or if the body does not close with <see cref="NetMsg.End"/> exactly at its last byte.
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

        /// <summary>Reads one host stream message into scratch space. False for a bad one or a type a host may not send.</summary>
        bool SkipStream(byte type, ref NetReader r)
        {
            switch (type)
            {
                case NetMsg.RemoteAvatar: return RemoteAvatarMsg.Read(ref r, out _);
                case NetMsg.CreatureState: return CreatureSnapshot.Read(ref r, out _);
                case NetMsg.ItemPoses: return ItemPoseBatch.Read(ref r, ref m_PoseRead);
                case NetMsg.Clock: return ClockMsg.Read(ref r, out _);
                case NetMsg.Voice: return VoiceMsg.Read(ref r, ref m_VoiceRead);
                default: return false;
            }
        }

        /// <summary>
        /// Applies one recorded host -> client message as if it had just arrived (the tape test: record with the
        /// host's <see cref="Tap"/>, replay into a passive client). Works in any state; needs no transport traffic.
        /// Returns false for a malformed or unknown message.
        /// </summary>
        public bool ApplyRecorded(byte type, byte[] payload, int offset, int length, double hostTime)
        {
            var r = new NetReader(payload, offset, length);
            if (r.Failed) return false;
            return Dispatch(type, ref r, hostTime);
        }

        /// <summary>Gives the slot used by <see cref="ApplyRecorded"/> replays for "is this me" decisions (no handshake happens in a replay).</summary>
        public void SetReplaySlot(byte slot)
        {
            if (State == ClientState.Idle || State == ClientState.Ended) LocalSlot = slot;
        }

        bool Dispatch(byte type, ref NetReader r, double hostTime)
        {
            switch (type)
            {
                // ---------------- streams
                case NetMsg.RemoteAvatar:
                {
                    if (!RemoteAvatarMsg.Read(ref r, out RemoteAvatarMsg m)) return false;
                    if (m.slot != LocalSlot) m_World.OnRemoteAvatar(m.slot, in m.avatar, hostTime);
                    return true;
                }
                case NetMsg.CreatureState:
                {
                    if (!CreatureSnapshot.Read(ref r, out CreatureSnapshot m)) return false;
                    m_World.OnCreatureState(in m, hostTime);
                    return true;
                }
                case NetMsg.ItemPoses:
                {
                    if (!ItemPoseBatch.Read(ref r, ref m_PoseRead)) return false;
                    for (int i = 0; i < m_PoseRead.count; i++) m_World.OnItemPose(in m_PoseRead.entries[i], hostTime);
                    return true;
                }
                case NetMsg.Clock:
                {
                    if (!ClockMsg.Read(ref r, out ClockMsg m)) return false;
                    m_World.OnClock(m.elapsedSeconds, m.oxygenRemainingSeconds, m.flags);
                    return true;
                }
                case NetMsg.Voice:
                {
                    if (!VoiceMsg.Read(ref r, ref m_VoiceRead)) return false;
                    if (m_VoiceRead.slot == LocalSlot) { m_Stats.rejected++; return true; } // never our own voice back
                    var handler = VoiceReceived;
                    if (handler != null)
                        for (int f = 0; f < m_VoiceRead.count; f++)
                            handler(m_VoiceRead.slot, (ushort)(m_VoiceRead.sequence + f), m_VoiceRead.data, m_VoiceRead.Offset(f), m_VoiceRead.Length(f));
                    return true;
                }

                // ---------------- session
                case NetMsg.Welcome:
                {
                    if (!WelcomeMsg.Read(ref r, ref m_Welcome)) return false;
                    Welcomed = true;
                    m_World.OnWelcome(LocalSlot, in m_Welcome);
                    return true;
                }
                case NetMsg.RosterChanged:
                {
                    if (!RosterChangedMsg.Read(ref r, out RosterChangedMsg m)) return false;
                    m_World.OnRosterChanged(m.slot, m.change, m.handedness);
                    return true;
                }
                case NetMsg.WorldState:
                {
                    if (!WorldStateMsg.Read(ref r, ref m_WorldState)) return false;
                    m_World.OnWorldState(in m_WorldState);
                    return true;
                }
                case NetMsg.HostPause:
                {
                    if (!HostPauseMsg.Read(ref r, out HostPauseMsg m)) return false;
                    m_World.OnHostPause(m.on);
                    return true;
                }
                case NetMsg.SessionClosed:
                {
                    if (!SessionClosedMsg.Read(ref r, out SessionClosedMsg m)) return false;
                    if (State == ClientState.Connected)
                        End(m.reason == SessionCloseReason.HostLeft ? SessionEndReason.HostClosed : SessionEndReason.Dropped, true);
                    return true;
                }

                // ---------------- run
                case NetMsg.RunStart:
                {
                    if (!RunStartCodec.Read(ref r, out RunStartParams m)) return false;
                    m_World.OnRunStart(in m);
                    return true;
                }
                case NetMsg.ExtractRefused:
                {
                    if (!ExtractRefusedMsg.Read(ref r, out ExtractRefusedMsg m)) return false;
                    m_World.OnExtractRefused(m.missingSlotsMask);
                    return true;
                }
                case NetMsg.RunEnded:
                {
                    if (!RunEndedMsg.Read(ref r, out RunEndedMsg m)) return false;
                    m_World.OnRunEnded(in m);
                    return true;
                }
                case NetMsg.ReturnedToHub:
                    m_World.OnReturnedToHub();
                    return true;

                // ---------------- items
                case NetMsg.ItemOwner:
                {
                    if (!ItemOwnerMsg.Read(ref r, out ItemOwnerMsg m)) return false;
                    m_World.OnItemOwner(m.itemId, m.ownerSlot, m.epoch, m.held, m.denied);
                    return true;
                }
                case NetMsg.ItemCollected:
                {
                    if (!ItemCollectedMsg.Read(ref r, out ItemCollectedMsg m)) return false;
                    m_World.OnItemCollected(m.itemId, m.by);
                    return true;
                }
                case NetMsg.ItemRest:
                {
                    if (!ItemRestMsg.Read(ref r, out ItemRestMsg m)) return false;
                    m_World.OnItemRest(m.itemId, m.epoch, m.position, m.rotation);
                    return true;
                }
                case NetMsg.ItemValue:
                {
                    if (!ItemValueMsg.Read(ref r, out ItemValueMsg m)) return false;
                    m_World.OnItemValue(m.itemId, m.value);
                    return true;
                }
                case NetMsg.ItemDeposited:
                {
                    if (!ItemDepositedMsg.Read(ref r, out ItemDepositedMsg m)) return false;
                    m_World.OnItemDeposited(m.itemId, m.by, m.finalValue, m.trickBonus, m.lockerSlot);
                    return true;
                }
                case NetMsg.ItemRecovered:
                {
                    if (!ItemRecoveredMsg.Read(ref r, out ItemRecoveredMsg m)) return false;
                    m_World.OnItemRecovered(m.itemId, m.position);
                    return true;
                }

                // ---------------- world
                case NetMsg.DoorState:
                {
                    if (!DoorStateMsg.Read(ref r, out DoorStateMsg m)) return false;
                    m_World.OnDoorState(m.doorId, m.open);
                    return true;
                }
                case NetMsg.PointCut:
                {
                    if (!PointCutMsg.Read(ref r, out PointCutMsg m)) return false;
                    m_World.OnPointCut(m.panel, m.index);
                    return true;
                }
                case NetMsg.PanelOpened:
                {
                    if (!PanelOpenedMsg.Read(ref r, out PanelOpenedMsg m)) return false;
                    m_World.OnPanelOpened(m.panel, m.by, m.plateImpulse, m.plateTorque);
                    return true;
                }

                // ---------------- creature and hazards
                case NetMsg.CreatureStateChanged:
                {
                    if (!CreatureStateChangedMsg.Read(ref r, out CreatureStateChangedMsg m)) return false;
                    m_World.OnCreatureStateChanged(m.state, m.targetSlot);
                    return true;
                }
                case NetMsg.Tranquilized:
                {
                    if (!TranquilizedMsg.Read(ref r, out TranquilizedMsg m)) return false;
                    m_World.OnTranquilized(m.seconds);
                    return true;
                }
                case NetMsg.CatchStarted:
                {
                    if (!CatchStartedMsg.Read(ref r, out CatchStartedMsg m)) return false;
                    m_World.OnCatchStarted(m.victimSlot);
                    return true;
                }
                case NetMsg.TranqTracer:
                {
                    if (!TranqTracerMsg.Read(ref r, out TranqTracerMsg m)) return false;
                    m_World.OnTranqTracer(m.by, m.from, m.to);
                    return true;
                }
                case NetMsg.BlackoutStarted:
                {
                    if (!BlackoutStartedMsg.Read(ref r, out BlackoutStartedMsg m)) return false;
                    m_World.OnBlackoutStarted(m.seconds);
                    return true;
                }
                case NetMsg.BlackoutEnded:
                {
                    if (!BlackoutEndedMsg.Read(ref r, out BlackoutEndedMsg m)) return false;
                    m_World.OnBlackoutEnded(m.instant);
                    return true;
                }
                case NetMsg.WireChanged:
                {
                    if (!WireChangedMsg.Read(ref r, out WireChangedMsg m)) return false;
                    m_World.OnWireChanged(m.index, m.change, m.by);
                    return true;
                }
                default:
                    if (NetMsg.IsReliable(type)) { m_Stats.unknownMessages++; return true; } // its length is known: skip it
                    return false;
            }
        }
    }
}

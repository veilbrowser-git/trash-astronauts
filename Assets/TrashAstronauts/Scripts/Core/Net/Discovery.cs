using System;

namespace TrashAstronauts.Core.Net
{
    /// <summary>
    /// The crew code shown on the host's board: the third and fourth octet of its IPv4 address ("C 001  D 042").
    /// A joiner rebuilds the address from its own first two octets plus C and D.
    /// </summary>
    public static class JoinCode
    {
        public static int C(uint address) => (int)((address >> 8) & 0xFFu);
        public static int D(uint address) => (int)(address & 0xFFu);

        /// <summary>"C 001  D 042".</summary>
        public static string Format(uint address) => Format(C(address), D(address));

        public static string Format(int c, int d) => "C " + Pad3(c) + "  D " + Pad3(d);

        public static string Pad3(int value)
        {
            if (value < 0) value = 0;
            if (value > 999) value = 999;
            return value < 10 ? "00" + value : value < 100 ? "0" + value : value.ToString();
        }

        /// <summary>C 0..255 and D 1..254 (0 and 255 are the network and broadcast addresses of a /24).</summary>
        public static bool IsValid(int c, int d) => c >= 0 && c <= 255 && d >= 1 && d <= 254;

        /// <summary>The joiner's first two octets + C.D.</summary>
        public static uint ToAddress(uint localAddress, int c, int d)
        {
            return (localAddress & 0xFFFF0000u) | ((uint)(c & 0xFF) << 8) | (uint)(d & 0xFF);
        }

        public static bool TryToEndpoint(uint localAddress, int c, int d, ushort port, out NetEndpoint endpoint)
        {
            endpoint = default(NetEndpoint);
            if (localAddress == 0u || !IsValid(c, d)) return false;
            endpoint = new NetEndpoint(ToAddress(localAddress, c, d), port);
            return true;
        }

        /// <summary>The four digits of "TUG 4821": 1000..9999 from any seed.</summary>
        public static ushort MakeTugNumber(int seed)
        {
            uint h = 2166136261u;
            for (int i = 0; i < 4; i++) { h ^= (uint)(seed >> (8 * i)) & 0xFFu; h *= 16777619u; }
            return (ushort)(1000 + h % 9000u);
        }
    }

    /// <summary>
    /// Generates the unicast probe targets a.b.c.1 .. a.b.c.254 (skipping our own address) in batches, so the
    /// sweep is spread over about ten frames. Needs only the INTERNET permission, so it finds a host even when
    /// the OS drops broadcast. A struct: no allocation.
    /// </summary>
    public struct SweepGenerator
    {
        public const int DefaultFrames = 10;

        uint m_Base;
        uint m_Self;
        ushort m_Port;
        int m_Next;
        int m_BatchSize;

        public SweepGenerator(uint localAddress, ushort port, int frames = DefaultFrames)
        {
            m_Base = localAddress & 0xFFFFFF00u;
            m_Self = localAddress;
            m_Port = port;
            m_Next = localAddress == 0u ? 255 : 1;
            if (frames < 1) frames = 1;
            m_BatchSize = (253 + frames - 1) / frames;
        }

        public bool Done => m_Next > 254;
        public int BatchSize => m_BatchSize;

        /// <summary>The next single target. False when the sweep is finished.</summary>
        public bool Next(out NetEndpoint target)
        {
            while (m_Next <= 254)
            {
                uint address = m_Base | (uint)m_Next;
                m_Next++;
                if (address == m_Self) continue;
                target = new NetEndpoint(address, m_Port);
                return true;
            }
            target = default(NetEndpoint);
            return false;
        }

        /// <summary>Fills <paramref name="buffer"/> with one frame's worth of targets. Returns how many (0 when finished).</summary>
        public int NextBatch(NetEndpoint[] buffer)
        {
            if (buffer == null) return 0;
            int n = 0;
            int limit = buffer.Length < m_BatchSize ? buffer.Length : m_BatchSize;
            while (n < limit && Next(out NetEndpoint target)) buffer[n++] = target;
            return n;
        }
    }

    /// <summary>One host seen while searching.</summary>
    public struct HostEntry
    {
        public NetEndpoint endpoint;
        public HostInfoMsg info;
        public double firstSeen;
        public double lastSeen;

        /// <summary>The refusal a Hello to this host would get, as far as HostInfo can tell (None = worth trying).</summary>
        public RefuseReason Predict(byte protocol, uint contentHash)
        {
            if (info.protocol != protocol) return RefuseReason.VersionMismatch;
            if (info.contentHash != contentHash) return RefuseReason.ContentMismatch;
            if (info.phase == MissionPhase.Active) return RefuseReason.ContractInProgress;
            if (info.playerCount >= NetProtocol.MaxPlayers) return RefuseReason.Full;
            return RefuseReason.None;
        }

        /// <summary>"TUG 4821  2/4  IN HUB".</summary>
        public string Label()
        {
            string phase = info.phase == MissionPhase.Hub ? "IN HUB" : info.phase == MissionPhase.Active ? "ON CONTRACT" : "RESULTS";
            return "TUG " + info.tugNumber + "  " + info.playerCount + "/" + NetProtocol.MaxPlayers + "  " + phase;
        }
    }

    /// <summary>The hosts found so far, oldest first; removed when they stop answering (see <see cref="Discovery"/>). The board shows the first three.</summary>
    public sealed class HostList
    {
        public const int Capacity = 8;
        public const int MaxShown = 3;
        public const double ExpirySeconds = 5.0;

        readonly HostEntry[] m_Entries = new HostEntry[Capacity];
        int m_Count;

        public int Count => m_Count;
        /// <summary>How many the board shows (at most three).</summary>
        public int ShownCount => m_Count < MaxShown ? m_Count : MaxShown;
        public HostEntry this[int index] => index >= 0 && index < m_Count ? m_Entries[index] : default(HostEntry);

        public void Clear() { m_Count = 0; }

        /// <summary>Adds or refreshes a host. Returns true when it is new.</summary>
        public bool Offer(NetEndpoint from, in HostInfoMsg info, double now)
        {
            for (int i = 0; i < m_Count; i++)
            {
                if (m_Entries[i].endpoint != from) continue;
                m_Entries[i].info = info;
                m_Entries[i].lastSeen = now;
                return false;
            }
            if (m_Count >= Capacity) return false;
            m_Entries[m_Count++] = new HostEntry { endpoint = from, info = info, firstSeen = now, lastSeen = now };
            return true;
        }

        /// <summary>Removes hosts last heard from before <paramref name="time"/>. Returns how many were removed.</summary>
        public int RemoveNotSeenSince(double time)
        {
            int removed = 0;
            for (int i = m_Count - 1; i >= 0; i--)
            {
                if (m_Entries[i].lastSeen >= time) continue;
                for (int j = i; j < m_Count - 1; j++) m_Entries[j] = m_Entries[j + 1];
                m_Count--;
                removed++;
            }
            return removed;
        }

        /// <summary>Removes hosts not heard from for <see cref="ExpirySeconds"/>. Returns how many were removed.</summary>
        public int Age(double now)
        {
            int removed = 0;
            for (int i = m_Count - 1; i >= 0; i--)
            {
                if (now - m_Entries[i].lastSeen < ExpirySeconds) continue;
                for (int j = i; j < m_Count - 1; j++) m_Entries[j] = m_Entries[j + 1];
                m_Count--;
                removed++;
            }
            return removed;
        }
    }

    /// <summary>
    /// FIND CREW: for 3 s, once a second, sends a 12-byte Probe to 255.255.255.255, to a.b.c.255 and by unicast to
    /// a.b.c.1-254 (one batch per <see cref="Tick"/>, about ten ticks per round), and collects the HostInfo
    /// answers into <see cref="Hosts"/>. The host never broadcasts; it only answers. Pure: the transport and the
    /// time are passed in.
    ///
    /// The list is not aged by the clock. When the 3 s of a search are over, hosts that did not answer THAT
    /// search are removed, and the rest stay listed until the end of the next search, however long the player
    /// takes to reach the JOIN button. (Nothing refreshes an entry once the probes stop, so expiring entries
    /// by time would empty the board about 7 s after FIND CREW was pressed.) A listed host that has gone away
    /// since is found out by JOIN (no answer) or by pressing FIND CREW again. <see cref="HostList.Age"/> is
    /// there for a caller that keeps searching continuously and wants time-based expiry.
    /// </summary>
    public sealed class Discovery
    {
        public const double SearchSeconds = 3.0;
        public const double RoundInterval = 1.0;
        public const int Rounds = 3;

        readonly byte m_Protocol;
        readonly HostList m_Hosts = new HostList();
        readonly byte[] m_Send = new byte[ProbeMsg.Size];
        readonly byte[] m_Receive = new byte[NetProtocol.MaxDatagram + 64];
        readonly NetEndpoint[] m_Batch = new NetEndpoint[64];
        uint m_NonceState;
        uint m_LocalAddress;
        ushort m_HostPort;
        double m_Started;
        int m_RoundsSent;
        SweepGenerator m_Sweep;
        bool m_Active;
        bool m_PruneDue;

        public HostList Hosts => m_Hosts;
        /// <summary>True from <see cref="Start"/> for 3 s. Late answers to the same search are still accepted afterwards, and the hosts found stay listed.</summary>
        public bool IsSearching => m_Active;
        public uint Nonce { get; private set; }
        public long ProbesSent { get; private set; }
        public long RepliesAccepted { get; private set; }
        public long Ignored { get; private set; }

        public Discovery(byte protocol = NetProtocol.Version, int seed = 1)
        {
            m_Protocol = protocol;
            m_NonceState = (uint)seed * 2654435761u + 12345u;
        }

        /// <summary>Starts a search. <paramref name="localAddress"/> 0 (unknown) limits it to the global broadcast.</summary>
        public void Start(uint localAddress, double now, ushort hostPort = NetProtocol.HostPort)
        {
            m_NonceState = m_NonceState * 1664525u + 1013904223u;
            Nonce = m_NonceState | 1u;
            m_LocalAddress = localAddress;
            m_HostPort = hostPort;
            m_Started = now;
            m_RoundsSent = 0;
            m_Sweep = new SweepGenerator(0u, hostPort);
            m_Active = true;
            m_PruneDue = false;
        }

        /// <summary>Stops probing. The hosts found so far stay listed.</summary>
        public void Stop() { m_Active = false; m_PruneDue = false; }

        /// <summary>
        /// Call once per frame while searching. Sends whatever probes are due, then (when
        /// <paramref name="receive"/> is true, i.e. nothing else is reading this transport) takes in the answers.
        /// </summary>
        public void Tick(INetTransport transport, double now, bool receive = true)
        {
            if (transport == null) return;
            if (m_Active)
            {
                double elapsed = now - m_Started;
                if (m_RoundsSent < Rounds && elapsed >= m_RoundsSent * RoundInterval)
                {
                    m_RoundsSent++;
                    int length = WriteProbe();
                    Send(transport, NetEndpoint.Broadcast(m_HostPort), length);
                    if (m_LocalAddress != 0u)
                    {
                        Send(transport, NetEndpoint.SubnetBroadcast(m_LocalAddress, m_HostPort), length);
                        m_Sweep = new SweepGenerator(m_LocalAddress, m_HostPort);
                    }
                }
                else if (!m_Sweep.Done)
                {
                    int length = WriteProbe();
                    int n = m_Sweep.NextBatch(m_Batch);
                    for (int i = 0; i < n; i++) Send(transport, m_Batch[i], length);
                }
                if (elapsed >= SearchSeconds && m_RoundsSent >= Rounds && m_Sweep.Done)
                {
                    m_Active = false;
                    m_PruneDue = true;
                }
            }

            if (receive)
            {
                for (int i = 0; i < 256; i++)
                {
                    if (!transport.Receive(out NetEndpoint from, m_Receive, out int length)) break;
                    HandleDatagram(from, m_Receive, length, now);
                }
            }
            if (m_PruneDue)
            {
                // after this tick's answers have been taken in: keep only hosts that answered this search
                m_PruneDue = false;
                m_Hosts.RemoveNotSeenSince(m_Started);
            }
        }

        int WriteProbe()
        {
            var w = new NetWriter(m_Send);
            new ProbeMsg { protocol = m_Protocol, nonce = Nonce }.WriteDatagram(ref w);
            return w.Position;
        }

        void Send(INetTransport transport, NetEndpoint to, int length)
        {
            transport.Send(to, m_Send, length);
            ProbesSent++;
        }

        /// <summary>
        /// Offers a received datagram to the search. Returns true when it was a HostInfo answering the current
        /// search (the host list is updated). Anything else, including garbage, returns false.
        /// </summary>
        public bool HandleDatagram(NetEndpoint from, byte[] data, int length, double now)
        {
            var r = new NetReader(data, 0, length);
            if (!Connectionless.TryReadFrame(ref r, out byte protocol, out byte type) || type != NetMsg.HostInfo) { Ignored++; return false; }
            if (!HostInfoMsg.Read(ref r, protocol, out HostInfoMsg info) || info.nonce != Nonce || Nonce == 0u) { Ignored++; return false; }
            m_Hosts.Offer(from, in info, now);
            RepliesAccepted++;
            return true;
        }
    }
}

using System;
using System.Threading;
using TrashAstronauts.Core.Net;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Relay;

namespace TrashAstronauts.Net.Online
{
    /// <summary>
    /// An <see cref="INetTransport"/> over a Unity Transport NetworkDriver connected through Unity Relay, so
    /// NetSessionHost / NetSessionClient run unchanged over the internet. Polled on the main thread like
    /// UdpTransport: no threads, nothing blocks, network trouble is counted, never thrown.
    ///
    /// Endpoints: Relay has connections, not addresses. Each connection gets a synthetic, stable NetEndpoint:
    /// the host is always <see cref="HostEndpoint"/> (10.82.0.1:47800) to its clients, and the host sees client
    /// number n as 10.82.1.1:n (n counts up from 1 and is never reused by one transport). A client whose Relay
    /// connection drops and comes back is a new endpoint, exactly like a UDP client on a new port.
    ///
    /// Datagrams: unreliable, unfragmented (the default null pipeline). Unity Transport is given 1400-byte packets;
    /// after its own header and Relay's and DTLS's, the payload left is reported per connection by
    /// <see cref="MaxPayload"/> (over real Relay + DTLS it was measured at well over the core's 1200: see README).
    /// A send longer than the connection's capacity is refused and counted in <see cref="Oversize"/>.
    ///
    /// Pumping: one driver update per poll. The first Receive of a poll (or an explicit <see cref="Update"/>)
    /// runs the driver (receive, timeouts, Relay keep-alives, queued sends), accepts new connections and moves
    /// every event out of the driver (the driver drops events left unread at its next update) into a
    /// pre-allocated queue of 128 datagrams, which Receive then hands out until "nothing waiting". Sends go out at
    /// the next update; <see cref="Flush"/> puts them on the wire at once (call it after a send tick).
    /// No allocation per tick: the queue, its slots and the connection table are allocated once.
    ///
    /// Disconnects: the sessions already treat silence as the link signal (Away after 1.5 s, slot dropped after
    /// 45 s), so a dropped Relay connection simply goes silent for them; the transport also counts it, forgets
    /// the endpoint (sends to it fail), and a client sets <see cref="LinkLost"/> so a board can say so at once.
    /// </summary>
    public sealed class RelayTransport : INetTransport, IRelayLink
    {
        static int s_LiveCount;

        /// <summary>Open drivers in this process. Every online test asserts this is 0 in TearDown.</summary>
        public static int LiveCount => Volatile.Read(ref s_LiveCount);

        /// <summary>The host as its clients see it.</summary>
        public static readonly NetEndpoint HostEndpoint = new NetEndpoint(10, 82, 0, 1, NetProtocol.HostPort);
        /// <summary>A client's own Local (informative only: the host never learns it).</summary>
        public static readonly NetEndpoint ClientLocalEndpoint = new NetEndpoint(10, 82, 0, 2, 0);
        /// <summary>Address shared by every client endpoint on the host side; the port is the connection's serial.</summary>
        public static readonly uint PeerAddress = NetEndpoint.MakeAddress(10, 82, 1, 1);

        public static NetEndpoint PeerEndpoint(ushort serial) => new NetEndpoint(PeerAddress, serial);

        /// <summary>Packet size given to Unity Transport (headers included). The default, and safe on the internet.</summary>
        public const int PacketSize = 1400;
        /// <summary>Connections a host keeps at once: 3 players plus room for reconnects not yet timed out.</summary>
        public const int MaxConnections = 8;
        /// <summary>Datagrams held between a driver update and the session's poll. More in one frame are dropped (counted).</summary>
        public const int QueueSlots = 128;
        /// <summary>
        /// How often the driver pings the Relay server while nothing else is sent. Relay severs a connection after 10 s
        /// with no activity; 2 s (UTP's default is 3 s) leaves room for a few long frames. No setting can carry a
        /// connection through a pause longer than those 10 s (a suspended app sends nothing at all): NetSession notices
        /// the loss (<see cref="IRelayLink"/>) and ends the session instead of waiting 60 s.
        /// </summary>
        public const int RelayKeepAliveMS = 2000;
        const int k_MaxEventsPerUpdate = 4096;

        NetworkDriver m_Driver;
        readonly bool m_IsHost;
        readonly bool m_UsesRelay;
        readonly NetworkConnection[] m_Connections = new NetworkConnection[MaxConnections];
        readonly ushort[] m_Serials = new ushort[MaxConnections];
        int m_Count;
        ushort m_NextSerial = 1;
        NetworkConnection m_Server;
        bool m_PumpedThisPoll;
        bool m_EverConnected;

        readonly byte[][] m_Slots = new byte[QueueSlots][];
        readonly int[] m_SlotLength = new int[QueueSlots];
        readonly NetEndpoint[] m_SlotFrom = new NetEndpoint[QueueSlots];
        int m_QueueHead;
        int m_QueueCount;

        public bool IsHost => m_IsHost;
        public bool IsOpen => m_Driver.IsCreated;
        public NetEndpoint Local => m_IsHost ? HostEndpoint : ClientLocalEndpoint;

        public int SendErrors { get; private set; }
        public int ReceiveErrors { get; private set; }
        /// <summary>Sends refused because they were longer than the connection's payload capacity.</summary>
        public int Oversize { get; private set; }
        /// <summary>Received datagrams longer than the caller's buffer (or than a queue slot), dropped.</summary>
        public int TooLong { get; private set; }
        /// <summary>Received datagrams dropped because the queue was full (a frame with more than 128).</summary>
        public int QueueOverflow { get; private set; }
        public int EmptyDatagrams { get; private set; }
        /// <summary>Data from a connection this transport does not know (should not happen).</summary>
        public int Stray { get; private set; }
        /// <summary>Connections accepted (host) or established (client).</summary>
        public int Connects { get; private set; }
        /// <summary>Connections lost: timed out, closed by the other side, or refused by Relay.</summary>
        public int Disconnects { get; private set; }
        /// <summary>Connections refused because the host already had <see cref="MaxConnections"/>.</summary>
        public int Refused { get; private set; }
        /// <summary>Datagrams waiting in the queue.</summary>
        public int Queued => m_QueueCount;

        /// <summary>Host: connected clients. Client: 1 while connected to the host.</summary>
        public int ConnectionCount => m_IsHost ? m_Count : (IsConnected ? 1 : 0);

        /// <summary>Client: the connection to the host is up.</summary>
        public bool IsConnected => m_Driver.IsCreated && !m_IsHost && m_Server != default && !LinkLost && m_Driver.GetConnectionState(m_Server) == NetworkConnection.State.Connected;

        /// <summary>Client: it was connected and the connection is gone (host closed, Relay dropped it, or timeout).</summary>
        public bool LinkLost { get; private set; }

        /// <summary>
        /// Host: Relay has confirmed the binding (without Relay: bound and listening). Client: connected to the host.
        /// RelayConnector hands a transport out only once this is true.
        /// </summary>
        public bool IsReady
        {
            get
            {
                if (!m_Driver.IsCreated) return false;
                if (m_UsesRelay && m_Driver.GetRelayConnectionStatus() != RelayConnectionStatus.Established) return false;
                return m_IsHost ? m_Driver.Bound && m_Driver.Listening : IsConnected;
            }
        }

        /// <summary>Relay told us the allocation is no longer valid (expired, or the host went away).</summary>
        public bool AllocationInvalid => m_UsesRelay && m_Driver.IsCreated && m_Driver.GetRelayConnectionStatus() == RelayConnectionStatus.AllocationInvalid;

        bool m_RelayEstablished;

        /// <summary>
        /// The connection to the Relay server itself was established and is not any more (it stopped answering, for
        /// example after the headset slept longer than Relay's 10 s idle limit). Never true without Relay.
        /// </summary>
        public bool RelayLost
        {
            get
            {
                if (!m_UsesRelay || !m_Driver.IsCreated) return false;
                RelayConnectionStatus status = m_Driver.GetRelayConnectionStatus();
                if (status == RelayConnectionStatus.Established) { m_RelayEstablished = true; return false; }
                return m_RelayEstablished;
            }
        }

        RelayTransport(NetworkDriver driver, bool isHost, bool usesRelay)
        {
            m_Driver = driver;
            m_IsHost = isHost;
            m_UsesRelay = usesRelay;
            for (int i = 0; i < QueueSlots; i++) m_Slots[i] = new byte[PacketSize];
            Interlocked.Increment(ref s_LiveCount);
        }

        // ------------------------------------------------------------------ opening

        static NetworkSettings MakeSettings()
        {
            var settings = new NetworkSettings(Allocator.Temp);
            // Drop a silent connection after as long as the sessions hold a silent player's slot, not UTP's 30 s.
            settings.WithNetworkConfigParameters(disconnectTimeoutMS: (int)(NetProtocol.SlotHoldSeconds * 1000.0), maxMessageSize: PacketSize);
            // Relay keep-alives (see RelayKeepAliveMS) are set with the Relay parameters below.
            return settings;
        }

        /// <summary>Binds as the Relay allocation's host and starts listening. Call <see cref="Update"/> until <see cref="IsReady"/>.</summary>
        public static RelayTransport OpenRelayHost(RelayServerData serverData, out string error)
        {
            NetworkSettings settings = MakeSettings();
            try
            {
                settings.WithRelayParameters(ref serverData, RelayKeepAliveMS);
                return Open(settings, true, true, NetworkEndpoint.AnyIpv4, default, out error);
            }
            finally { settings.Dispose(); }
        }

        /// <summary>Connects to the host through Relay. Call <see cref="Update"/> until <see cref="IsReady"/> (connected).</summary>
        public static RelayTransport OpenRelayClient(RelayServerData serverData, out string error)
        {
            NetworkSettings settings = MakeSettings();
            try
            {
                settings.WithRelayParameters(ref serverData, RelayKeepAliveMS);
                return Open(settings, false, true, NetworkEndpoint.AnyIpv4, serverData.Endpoint, out error);
            }
            finally { settings.Dispose(); }
        }

        /// <summary>
        /// The same transport without Relay: plain Unity Transport UDP on 127.0.0.1. For tests and diagnostics only;
        /// it exercises the endpoint mapping, pumping, caps and disconnects with no internet. Port 0 picks one.
        /// </summary>
        public static RelayTransport OpenLoopbackHost(ushort port, out string error)
        {
            NetworkSettings settings = MakeSettings();
            try { return Open(settings, true, false, NetworkEndpoint.LoopbackIpv4.WithPort(port), default, out error); }
            finally { settings.Dispose(); }
        }

        /// <inheritdoc cref="OpenLoopbackHost"/>
        public static RelayTransport OpenLoopbackClient(ushort hostPort, out string error)
        {
            NetworkSettings settings = MakeSettings();
            try { return Open(settings, false, false, NetworkEndpoint.LoopbackIpv4, NetworkEndpoint.LoopbackIpv4.WithPort(hostPort), out error); }
            finally { settings.Dispose(); }
        }

        static RelayTransport Open(NetworkSettings settings, bool host, bool relay, NetworkEndpoint bind, NetworkEndpoint connectTo, out string error)
        {
            error = null;
            NetworkDriver driver = default;
            try
            {
                driver = NetworkDriver.Create(settings);
                if (driver.Bind(bind) != 0) { error = "bind failed"; driver.Dispose(); return null; }
                if (host)
                {
                    if (driver.Listen() != 0) { error = "listen failed"; driver.Dispose(); return null; }
                    return new RelayTransport(driver, true, relay);
                }
                var transport = new RelayTransport(driver, false, relay);
                transport.m_Server = driver.Connect(connectTo);
                if (transport.m_Server == default) { error = "connect failed"; transport.Dispose(); return null; }
                return transport;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                if (driver.IsCreated)
                {
                    try { driver.Dispose(); } catch (Exception) { }
                }
                return null;
            }
        }

        /// <summary>The bound local port (loopback mode: what a loopback client connects to). 0 when not bound.</summary>
        public ushort BoundPort => m_Driver.IsCreated ? m_Driver.GetLocalEndpoint().Port : (ushort)0;

        // ------------------------------------------------------------------ pumping

        /// <summary>
        /// Runs the driver once: receives, timeouts, Relay keep-alives, queued sends; accepts new connections; moves
        /// every received datagram into the queue. Receive does this itself once per poll; call it directly only
        /// while nothing polls (waiting for <see cref="IsReady"/>).
        /// </summary>
        public void Update()
        {
            if (!m_Driver.IsCreated) return;
            try
            {
                m_Driver.ScheduleUpdate().Complete();
                if (m_IsHost) AcceptAll();
                DrainEvents();
                if (!m_IsHost && m_Server != default)
                {
                    NetworkConnection.State state = m_Driver.GetConnectionState(m_Server);
                    if (state == NetworkConnection.State.Connected && !m_EverConnected) { m_EverConnected = true; Connects++; }
                    else if (state == NetworkConnection.State.Disconnected && !LinkLost) LoseLink();
                }
            }
            catch (Exception) { ReceiveErrors++; }
        }

        /// <summary>Puts queued sends on the wire now instead of at the next update.</summary>
        public void Flush()
        {
            if (!m_Driver.IsCreated) return;
            try { m_Driver.ScheduleFlushSend().Complete(); }
            catch (Exception) { SendErrors++; }
        }

        void AcceptAll()
        {
            NetworkConnection c;
            while ((c = m_Driver.Accept()) != default)
            {
                if (m_Count >= MaxConnections)
                {
                    Refused++;
                    m_Driver.Disconnect(c);
                    continue;
                }
                m_Connections[m_Count] = c;
                m_Serials[m_Count] = m_NextSerial;
                m_NextSerial = (ushort)(m_NextSerial == ushort.MaxValue ? 1 : m_NextSerial + 1);
                m_Count++;
                Connects++;
            }
        }

        void DrainEvents()
        {
            for (int n = 0; n < k_MaxEventsPerUpdate; n++)
            {
                NetworkEvent.Type type = m_Driver.PopEvent(out NetworkConnection c, out DataStreamReader reader);
                switch (type)
                {
                    case NetworkEvent.Type.Empty:
                        return;
                    case NetworkEvent.Type.Connect:
                        if (!m_IsHost && !m_EverConnected) { m_EverConnected = true; Connects++; }
                        break;
                    case NetworkEvent.Type.Disconnect:
                        if (m_IsHost)
                        {
                            int i = IndexOf(c);
                            if (i >= 0) { RemoveAt(i); Disconnects++; }
                        }
                        else if (c == m_Server && !LinkLost) LoseLink();
                        break;
                    case NetworkEvent.Type.Data:
                        Enqueue(c, ref reader);
                        break;
                }
            }
        }

        void Enqueue(NetworkConnection c, ref DataStreamReader reader)
        {
            int size = reader.Length;
            if (size <= 0) { EmptyDatagrams++; return; }
            if (size > PacketSize) { TooLong++; return; }
            NetEndpoint sender;
            if (m_IsHost)
            {
                int i = IndexOf(c);
                if (i < 0) { Stray++; return; }
                sender = PeerEndpoint(m_Serials[i]);
            }
            else
            {
                if (c != m_Server) { Stray++; return; }
                sender = HostEndpoint;
            }
            if (m_QueueCount >= QueueSlots) { QueueOverflow++; return; }
            int slot = (m_QueueHead + m_QueueCount) % QueueSlots;
            reader.ReadBytes(new Span<byte>(m_Slots[slot], 0, size));
            if (reader.HasFailedReads) { ReceiveErrors++; return; }
            m_SlotLength[slot] = size;
            m_SlotFrom[slot] = sender;
            m_QueueCount++;
        }

        void LoseLink()
        {
            LinkLost = true;
            Disconnects++;
        }

        int IndexOf(NetworkConnection c)
        {
            for (int i = 0; i < m_Count; i++)
                if (m_Connections[i] == c) return i;
            return -1;
        }

        int IndexOf(NetEndpoint e)
        {
            if (e.address != PeerAddress) return -1;
            for (int i = 0; i < m_Count; i++)
                if (m_Serials[i] == e.port) return i;
            return -1;
        }

        void RemoveAt(int index)
        {
            m_Count--;
            m_Connections[index] = m_Connections[m_Count];
            m_Serials[index] = m_Serials[m_Count];
            m_Connections[m_Count] = default;
            m_Serials[m_Count] = 0;
        }

        bool TryConnection(NetEndpoint to, out NetworkConnection c)
        {
            c = default;
            if (m_IsHost)
            {
                int i = IndexOf(to);
                if (i < 0) return false;
                c = m_Connections[i];
            }
            else
            {
                if (to != HostEndpoint || LinkLost || m_Server == default) return false;
                c = m_Server;
            }
            return m_Driver.GetConnectionState(c) == NetworkConnection.State.Connected;
        }

        // ------------------------------------------------------------------ INetTransport

        public bool Send(NetEndpoint to, byte[] data, int length)
        {
            if (!m_Driver.IsCreated || data == null || length <= 0 || length > data.Length) return false;
            try
            {
                if (!TryConnection(to, out NetworkConnection c)) return false;
                int capacity = m_Driver.GetMaxSupportedPayloadSize(c, default);
                if (capacity > 0 && length > capacity) { Oversize++; return false; }
                int status = m_Driver.BeginSend(c, out DataStreamWriter writer);
                if (status < 0) { SendErrors++; return false; }
                if (!writer.WriteBytes(new Span<byte>(data, 0, length)))
                {
                    m_Driver.AbortSend(writer);
                    Oversize++;
                    return false;
                }
                status = m_Driver.EndSend(writer);
                if (status < 0) { SendErrors++; return false; }
                return true;
            }
            catch (Exception)
            {
                SendErrors++;
                return false;
            }
        }

        /// <summary>
        /// The next queued datagram. When the queue is empty, the first call of a poll updates the driver and looks
        /// again; the call after "nothing waiting" starts the next poll.
        /// </summary>
        public bool Receive(out NetEndpoint from, byte[] buffer, out int length)
        {
            from = default(NetEndpoint);
            length = 0;
            if (!m_Driver.IsCreated || buffer == null || buffer.Length == 0) return false;
            while (true)
            {
                if (m_QueueCount == 0)
                {
                    if (m_PumpedThisPoll)
                    {
                        m_PumpedThisPoll = false;
                        return false;
                    }
                    Update();
                    m_PumpedThisPoll = true;
                    if (m_QueueCount == 0)
                    {
                        m_PumpedThisPoll = false;
                        return false;
                    }
                }
                int slot = m_QueueHead;
                m_QueueHead = (m_QueueHead + 1) % QueueSlots;
                m_QueueCount--;
                int size = m_SlotLength[slot];
                if (size > buffer.Length) { TooLong++; continue; }
                Buffer.BlockCopy(m_Slots[slot], 0, buffer, 0, size);
                from = m_SlotFrom[slot];
                length = size;
                return true;
            }
        }

        /// <summary>
        /// Payload capacity of the connection behind <paramref name="to"/> (bytes one datagram may carry), or -1 when
        /// there is no such connected endpoint. Over Relay with DTLS this is what decides whether the core's
        /// 1200-byte datagrams fit.
        /// </summary>
        public int MaxPayload(NetEndpoint to)
        {
            if (!m_Driver.IsCreated) return -1;
            try { return TryConnection(to, out NetworkConnection c) ? m_Driver.GetMaxSupportedPayloadSize(c, default) : -1; }
            catch (Exception) { return -1; }
        }

        /// <summary>Host: the endpoint of the i-th connected client (0 .. ConnectionCount-1).</summary>
        public NetEndpoint PeerAt(int index) => m_IsHost && index >= 0 && index < m_Count ? PeerEndpoint(m_Serials[index]) : default(NetEndpoint);

        /// <summary>Closes every connection (the other side hears a disconnect) and releases the driver. Safe twice.</summary>
        public void Dispose()
        {
            if (!m_Driver.IsCreated) return;
            try
            {
                // Unread events must be gone before the last update (the driver complains about dropping them).
                for (int n = 0; n < k_MaxEventsPerUpdate && m_Driver.PopEvent(out _, out _) != NetworkEvent.Type.Empty; n++) { }
                if (m_IsHost)
                {
                    for (int i = 0; i < m_Count; i++) m_Driver.Disconnect(m_Connections[i]);
                }
                else if (m_Server != default && m_Driver.GetConnectionState(m_Server) != NetworkConnection.State.Disconnected)
                {
                    m_Driver.Disconnect(m_Server);
                }
                m_Driver.ScheduleUpdate().Complete(); // sends the disconnect messages
            }
            catch (Exception) { }
            try { m_Driver.Dispose(); } catch (Exception) { }
            m_Driver = default;
            m_Count = 0;
            m_Server = default;
            m_QueueCount = 0;
            Interlocked.Decrement(ref s_LiveCount);
        }
    }
}

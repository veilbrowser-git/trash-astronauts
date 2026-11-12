using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace TrashAstronauts.Core.Net
{
    /// <summary>
    /// The only socket code in the game: one non-blocking IPv4 UDP socket with SO_BROADCAST on, polled from
    /// the main thread. No threads, no async, no port hunting. Network errors are counted, never thrown.
    ///
    /// Receive asks "is the socket readable" (a zero-timeout poll), not "how many bytes are waiting": on Linux
    /// and Android the byte count is the size of the NEXT datagram, so one empty datagram at the head of the
    /// queue would read as "nothing there" for ever and the socket would go deaf. Empty datagrams and datagrams
    /// that fail to read are consumed, counted and skipped, so whatever is queued behind them still arrives in
    /// the same call.
    /// </summary>
    public sealed class UdpTransport : INetTransport
    {
        static int s_LiveCount;

        /// <summary>Open sockets in this process. Every net test asserts this is 0 in TearDown.</summary>
        public static int LiveCount => Volatile.Read(ref s_LiveCount);

        Socket m_Socket;
        EndPoint m_From = new IPEndPoint(IPAddress.Any, 0);
        readonly Dictionary<NetEndpoint, IPEndPoint> m_Targets = new Dictionary<NetEndpoint, IPEndPoint>();
        const int k_MaxCachedTargets = 600; // one /24 sweep plus the session peers
        /// <summary>Most empty or unreadable datagrams one Receive call skips before it reports "nothing".</summary>
        const int k_MaxSkipsPerReceive = 16;

        public NetEndpoint Local { get; private set; }
        public bool IsOpen => m_Socket != null;
        public int SendErrors { get; private set; }
        /// <summary>Receives that failed (oversized datagram, ICMP error surfacing on the socket ...). Each one is skipped.</summary>
        public int ReceiveErrors { get; private set; }
        /// <summary>Zero-length datagrams received and dropped.</summary>
        public int EmptyDatagrams { get; private set; }

        UdpTransport(Socket socket, NetEndpoint local)
        {
            m_Socket = socket;
            Local = local;
            Interlocked.Increment(ref s_LiveCount);
        }

        /// <summary>
        /// Opens a socket bound to <paramref name="port"/> on every interface (0 = let the OS pick an ephemeral
        /// port). Returns null and a short reason when the bind fails (the lobby board shows PORT BUSY).
        /// </summary>
        public static UdpTransport TryOpen(int port, out string error)
        {
            return TryOpen(0u, port, out error);
        }

        /// <summary>Opens a socket bound to one local address (tests use 127.0.0.1). Address 0 means every interface.</summary>
        public static UdpTransport TryOpen(uint bindAddress, int port, out string error)
        {
            error = null;
            if (port < 0 || port > 65535) { error = "bad port"; return null; }
            Socket socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Blocking = false;
                socket.EnableBroadcast = true;
                socket.Bind(new IPEndPoint(ToIPAddress(bindAddress), port));
                int boundPort = port;
                uint boundAddress = bindAddress;
                if (socket.LocalEndPoint is IPEndPoint bound)
                {
                    boundPort = bound.Port;
                    boundAddress = FromIPAddress(bound.Address);
                }
                return new UdpTransport(socket, new NetEndpoint(boundAddress, (ushort)boundPort));
            }
            catch (Exception e)
            {
                error = e is SocketException se ? se.SocketErrorCode.ToString() : e.GetType().Name;
                if (socket != null)
                {
                    try { socket.Close(); } catch (Exception) { }
                }
                return null;
            }
        }

        public bool Send(NetEndpoint to, byte[] data, int length)
        {
            if (m_Socket == null || data == null || length <= 0 || length > data.Length) return false;
            try
            {
                if (!m_Targets.TryGetValue(to, out IPEndPoint target))
                {
                    if (m_Targets.Count >= k_MaxCachedTargets) m_Targets.Clear();
                    target = new IPEndPoint(ToIPAddress(to.address), to.port);
                    m_Targets.Add(to, target);
                }
                return m_Socket.SendTo(data, 0, length, SocketFlags.None, target) == length;
            }
            catch (Exception)
            {
                // WouldBlock, unreachable network, no route for a broadcast, socket closed under us ...
                SendErrors++;
                return false;
            }
        }

        /// <summary>
        /// The next datagram, if one is waiting. False means the queue is empty (or, after 16 bad datagrams in a
        /// row, that the rest waits for the next call). Empty and unreadable datagrams never reach the caller.
        /// </summary>
        public bool Receive(out NetEndpoint from, byte[] buffer, out int length)
        {
            from = default(NetEndpoint);
            length = 0;
            if (m_Socket == null || buffer == null || buffer.Length == 0) return false;
            for (int attempt = 0; attempt < k_MaxSkipsPerReceive; attempt++)
            {
                try
                {
                    // Readable, not Available: see the class comment.
                    if (!m_Socket.Poll(0, SelectMode.SelectRead)) return false;
                    int received = m_Socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref m_From);
                    if (received <= 0) { EmptyDatagrams++; continue; }
                    if (!(m_From is IPEndPoint ip)) { ReceiveErrors++; continue; }
                    from = new NetEndpoint(FromIPAddress(ip.Address), (ushort)ip.Port);
                    length = received;
                    return true;
                }
                catch (SocketException e)
                {
                    // Oversized datagram (MessageSize) or an ICMP port-unreachable surfacing as ConnectionReset: that
                    // one is consumed, look at the next. WouldBlock: readable was a false alarm, nothing to read.
                    ReceiveErrors++;
                    if (e.SocketErrorCode == SocketError.WouldBlock) return false;
                }
                catch (Exception)
                {
                    // socket closed under us
                    ReceiveErrors++;
                    return false;
                }
            }
            return false;
        }


        public void Dispose()
        {
            Socket socket = m_Socket;
            if (socket == null) return;
            m_Socket = null;
            try { socket.Close(); } catch (Exception) { }
            Interlocked.Decrement(ref s_LiveCount);
        }

        static IPAddress ToIPAddress(uint address)
        {
            return new IPAddress(new[] { (byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address });
        }

        /// <summary>a.b.c.d as a uint, 0 for anything that is not IPv4. Allocates nothing (it runs once per received datagram).</summary>
        static uint FromIPAddress(IPAddress address)
        {
            if (address == null) return 0u;
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                if (!address.IsIPv4MappedToIPv6) return 0u;
                address = address.MapToIPv4();
            }
            Span<byte> b = stackalloc byte[4];
            if (!address.TryWriteBytes(b, out int written) || written != 4) return 0u;
            return NetEndpoint.MakeAddress(b[0], b[1], b[2], b[3]);
        }

        /// <summary>
        /// This device's own IPv4 address on the LAN, for the crew code. First tries the routing table
        /// (connecting a UDP socket picks the outgoing interface and sends nothing), then falls back to
        /// enumerating interfaces. False when there is no usable address (no Wi-Fi).
        /// Unverified on Quest/IL2CPP until Stage 1b.
        /// </summary>
        public static bool TryGetLocalAddress(out uint address)
        {
            if (TryGetLocalAddressByRoute(out address)) return true;
            return TryGetLocalAddressByInterfaces(out address);
        }

        /// <summary>Method 1: connect a UDP socket to a public address and read LocalEndPoint. No packet is sent.</summary>
        public static bool TryGetLocalAddressByRoute(out uint address)
        {
            address = 0u;
            Socket socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(new IPEndPoint(new IPAddress(new byte[] { 192, 0, 2, 1 }), 9)); // TEST-NET-1: routable, never answered
                if (socket.LocalEndPoint is IPEndPoint local) address = FromIPAddress(local.Address);
            }
            catch (Exception) { address = 0u; }
            finally
            {
                if (socket != null)
                {
                    try { socket.Close(); } catch (Exception) { }
                }
            }
            return IsUsableLanAddress(address);
        }

        /// <summary>Method 2: the first up, non-loopback interface with an IPv4 address (private ranges preferred).</summary>
        public static bool TryGetLocalAddressByInterfaces(out uint address)
        {
            address = 0u;
            uint fallback = 0u;
            try
            {
                NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < interfaces.Length; i++)
                {
                    NetworkInterface ni = interfaces[i];
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation info in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        uint candidate = FromIPAddress(info.Address);
                        if (!IsUsableLanAddress(candidate)) continue;
                        if (IsPrivateAddress(candidate)) { address = candidate; return true; }
                        if (fallback == 0u) fallback = candidate;
                    }
                }
            }
            catch (Exception) { }
            address = fallback;
            return address != 0u;
        }

        /// <summary>Not 0.0.0.0, not loopback, not link-local 169.254.x.x, not broadcast.</summary>
        public static bool IsUsableLanAddress(uint address)
        {
            if (address == 0u || address == NetEndpoint.BroadcastAddress) return false;
            byte a = (byte)(address >> 24), b = (byte)(address >> 16);
            if (a == 127 || a == 0) return false;
            if (a == 169 && b == 254) return false;
            return true;
        }

        /// <summary>10/8, 172.16/12 or 192.168/16.</summary>
        public static bool IsPrivateAddress(uint address)
        {
            byte a = (byte)(address >> 24), b = (byte)(address >> 16);
            return a == 10 || (a == 172 && b >= 16 && b <= 31) || (a == 192 && b == 168);
        }
    }
}

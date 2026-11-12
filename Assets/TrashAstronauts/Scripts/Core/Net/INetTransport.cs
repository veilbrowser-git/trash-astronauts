using System;

namespace TrashAstronauts.Core.Net
{
    /// <summary>
    /// An IPv4 address and port. The address is in "reading order": a.b.c.d is (a &lt;&lt; 24) | (b &lt;&lt; 16) | (c &lt;&lt; 8) | d.
    /// </summary>
    public readonly struct NetEndpoint : IEquatable<NetEndpoint>
    {
        public readonly uint address;
        public readonly ushort port;

        public const uint BroadcastAddress = 0xFFFFFFFFu;
        public const uint LoopbackAddress = 0x7F000001u;

        public NetEndpoint(uint address, ushort port) { this.address = address; this.port = port; }

        public NetEndpoint(byte a, byte b, byte c, byte d, ushort port) : this(MakeAddress(a, b, c, d), port) { }

        public static uint MakeAddress(byte a, byte b, byte c, byte d) => ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;

        public byte A => (byte)(address >> 24);
        public byte B => (byte)(address >> 16);
        public byte C => (byte)(address >> 8);
        public byte D => (byte)address;

        /// <summary>True for no address at all (the default value).</summary>
        public bool IsNone => address == 0u && port == 0;
        /// <summary>True for 255.255.255.255 and for a.b.c.255 (a /24 directed broadcast).</summary>
        public bool IsBroadcast => address == BroadcastAddress || (address & 0xFFu) == 0xFFu;

        public static NetEndpoint Broadcast(ushort port) => new NetEndpoint(BroadcastAddress, port);
        /// <summary>a.b.c.255 for the /24 that <paramref name="localAddress"/> is in.</summary>
        public static NetEndpoint SubnetBroadcast(uint localAddress, ushort port) => new NetEndpoint(localAddress | 0xFFu, port);
        public static NetEndpoint Loopback(ushort port) => new NetEndpoint(LoopbackAddress, port);

        public bool Equals(NetEndpoint other) => address == other.address && port == other.port;
        public override bool Equals(object obj) => obj is NetEndpoint other && Equals(other);
        public override int GetHashCode() => (int)(address * 31u + port);
        public static bool operator ==(NetEndpoint a, NetEndpoint b) => a.address == b.address && a.port == b.port;
        public static bool operator !=(NetEndpoint a, NetEndpoint b) => a.address != b.address || a.port != b.port;
        public override string ToString() => A + "." + B + "." + C + "." + D + ":" + port;
    }

    /// <summary>
    /// A datagram socket as the sessions see it. Polled from the main thread; nothing here blocks, and
    /// nothing here throws for network conditions (a failed send or an empty queue is a false return).
    /// Implementations: <see cref="UdpTransport"/> (the only socket code) and <see cref="MemoryTransport"/> (tests, bots).
    /// </summary>
    public interface INetTransport : IDisposable
    {
        /// <summary>Queues one datagram of <paramref name="length"/> bytes from <paramref name="data"/>. False if it could not be sent.</summary>
        bool Send(NetEndpoint to, byte[] data, int length);

        /// <summary>
        /// Takes one received datagram, if any, into <paramref name="buffer"/>. False when nothing is waiting.
        /// A datagram longer than the buffer is dropped. An implementation must consume and skip datagrams it
        /// cannot hand over (empty, unreadable) rather than report "nothing waiting" while they sit in the queue.
        /// </summary>
        bool Receive(out NetEndpoint from, byte[] buffer, out int length);

        /// <summary>The bound address and port (the address may be 0.0.0.0 for a wildcard bind).</summary>
        NetEndpoint Local { get; }
    }
}

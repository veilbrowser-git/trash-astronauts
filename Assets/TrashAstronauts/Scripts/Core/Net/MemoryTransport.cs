using System;
using System.Collections.Generic;

namespace TrashAstronauts.Core.Net
{
    /// <summary>
    /// An in-process "network" for tests, bots and the fuzzers: every <see cref="MemoryTransport"/> created from
    /// one hub can reach the others. Faults are seeded, so a failing run replays exactly. Time only moves when
    /// <see cref="Advance"/> is called; there is no wall clock and no thread.
    /// </summary>
    public sealed class MemoryTransportHub
    {
        struct Packet
        {
            public double deliverAt;
            public long order;
            public NetEndpoint from;
            public NetEndpoint to;
            public byte[] data;
            public int length;
        }

        readonly List<MemoryTransport> m_Transports = new List<MemoryTransport>();
        readonly Stack<byte[]> m_Pool = new Stack<byte[]>();
        Packet[] m_Heap = new Packet[64];
        int m_HeapCount;
        long m_Order;
        Random m_Random;
        uint m_NextAddress = NetEndpoint.MakeAddress(10, 0, 0, 1);
        ushort m_NextPort = 50000;

        /// <summary>Largest datagram the hub carries. Bigger sends fail and are counted in <see cref="OversizeCount"/>.</summary>
        public const int MaxDatagram = 1200;

        /// <summary>One-way delay in seconds applied to every datagram.</summary>
        public double Latency;
        /// <summary>Extra random delay, 0..Jitter seconds.</summary>
        public double Jitter;
        /// <summary>Probability 0..1 that a datagram is lost.</summary>
        public double Loss;
        /// <summary>Probability 0..1 that a datagram is held back by up to <see cref="ReorderDelay"/>, so later ones overtake it.</summary>
        public double Reorder;
        public double ReorderDelay = 0.06;
        /// <summary>Probability 0..1 that a datagram is delivered twice.</summary>
        public double Duplicate;

        public double Now { get; private set; }
        public long SentCount { get; private set; }
        public long DeliveredCount { get; private set; }
        public long LostCount { get; private set; }
        public long DuplicatedCount { get; private set; }
        public long OversizeCount { get; private set; }
        public long BytesSent { get; private set; }
        /// <summary>Largest datagram seen so far (to prove the 1200-byte cap).</summary>
        public int LargestDatagram { get; private set; }
        /// <summary>Transports created and not yet disposed.</summary>
        public int LiveCount => m_Transports.Count;

        public MemoryTransportHub(int seed = 1) { m_Random = new Random(seed); }

        /// <summary>Restarts the fault stream (does not touch packets already in flight).</summary>
        public void Reseed(int seed) { m_Random = new Random(seed); }

        /// <summary>Creates a transport on the next free 10.0.0.x address with an ephemeral-style port.</summary>
        public MemoryTransport Create()
        {
            var endpoint = new NetEndpoint(m_NextAddress++, m_NextPort++);
            return Create(endpoint);
        }

        /// <summary>Creates a transport on the next free 10.0.0.x address bound to <paramref name="port"/>.</summary>
        public MemoryTransport Create(ushort port)
        {
            return Create(new NetEndpoint(m_NextAddress++, port));
        }

        /// <summary>Creates a transport bound to an exact endpoint. Returns null if it is already taken (like a failed bind).</summary>
        public MemoryTransport Create(NetEndpoint local)
        {
            for (int i = 0; i < m_Transports.Count; i++)
                if (m_Transports[i].Local == local) return null;
            var transport = new MemoryTransport(this, local);
            m_Transports.Add(transport);
            return transport;
        }

        /// <summary>Moves time forward and makes every datagram that is now due receivable.</summary>
        public void Advance(double seconds)
        {
            if (seconds > 0) Now += seconds;
            Pump();
        }

        internal void Unregister(MemoryTransport transport) { m_Transports.Remove(transport); }

        internal bool Send(MemoryTransport sender, NetEndpoint to, byte[] data, int length)
        {
            if (data == null || length < 0 || length > data.Length) return false;
            if (length > MaxDatagram) { OversizeCount++; return false; }
            SentCount++;
            BytesSent += length;
            if (length > LargestDatagram) LargestDatagram = length;
            if (sender.Blackhole) { LostCount++; return true; }

            if (to.IsBroadcast)
            {
                // Like a LAN broadcast: every other socket bound to that port hears it (/24 match for a.b.c.255).
                for (int i = 0; i < m_Transports.Count; i++)
                {
                    var t = m_Transports[i];
                    if (t == sender || t.Local.port != to.port) continue;
                    if (to.address != NetEndpoint.BroadcastAddress && (t.Local.address & 0xFFFFFF00u) != (to.address & 0xFFFFFF00u)) continue;
                    if (sender.BroadcastBlocked) continue;
                    Schedule(sender.Local, t.Local, data, length);
                }
                return true;
            }

            Schedule(sender.Local, to, data, length);
            return true;
        }

        void Schedule(NetEndpoint from, NetEndpoint to, byte[] data, int length)
        {
            if (Loss > 0 && m_Random.NextDouble() < Loss) { LostCount++; return; }
            double delay = Latency;
            if (Jitter > 0) delay += m_Random.NextDouble() * Jitter;
            if (Reorder > 0 && m_Random.NextDouble() < Reorder) delay += m_Random.NextDouble() * ReorderDelay;
            Push(from, to, data, length, Now + delay);
            if (Duplicate > 0 && m_Random.NextDouble() < Duplicate)
            {
                DuplicatedCount++;
                Push(from, to, data, length, Now + delay + m_Random.NextDouble() * ReorderDelay);
            }
        }

        void Push(NetEndpoint from, NetEndpoint to, byte[] data, int length, double deliverAt)
        {
            byte[] copy = m_Pool.Count > 0 ? m_Pool.Pop() : new byte[MaxDatagram];
            Buffer.BlockCopy(data, 0, copy, 0, length);
            if (m_HeapCount == m_Heap.Length) Array.Resize(ref m_Heap, m_Heap.Length * 2);
            var packet = new Packet { deliverAt = deliverAt, order = m_Order++, from = from, to = to, data = copy, length = length };
            int i = m_HeapCount++;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (!Before(packet, m_Heap[parent])) break;
                m_Heap[i] = m_Heap[parent];
                i = parent;
            }
            m_Heap[i] = packet;
        }

        static bool Before(in Packet a, in Packet b)
        {
            return a.deliverAt < b.deliverAt || (a.deliverAt == b.deliverAt && a.order < b.order);
        }

        Packet Pop()
        {
            Packet top = m_Heap[0];
            Packet last = m_Heap[--m_HeapCount];
            m_Heap[m_HeapCount] = default(Packet);
            int i = 0;
            while (true)
            {
                int child = i * 2 + 1;
                if (child >= m_HeapCount) break;
                if (child + 1 < m_HeapCount && Before(m_Heap[child + 1], m_Heap[child])) child++;
                if (!Before(m_Heap[child], last)) break;
                m_Heap[i] = m_Heap[child];
                i = child;
            }
            if (m_HeapCount > 0) m_Heap[i] = last;
            return top;
        }

        /// <summary>Moves every due packet into its receiver's inbox, in delivery order.</summary>
        internal void Pump()
        {
            while (m_HeapCount > 0 && m_Heap[0].deliverAt <= Now)
            {
                Packet packet = Pop();
                MemoryTransport target = null;
                for (int i = 0; i < m_Transports.Count; i++)
                    if (m_Transports[i].Local == packet.to) { target = m_Transports[i]; break; }
                if (target == null || target.Blackhole) { LostCount++; m_Pool.Push(packet.data); continue; }
                DeliveredCount++;
                target.Enqueue(packet.from, packet.data, packet.length);
            }
        }

        internal void Recycle(byte[] buffer) { if (buffer != null && buffer.Length == MaxDatagram) m_Pool.Push(buffer); }
    }

    /// <summary>One endpoint on a <see cref="MemoryTransportHub"/>.</summary>
    public sealed class MemoryTransport : INetTransport
    {
        struct Inbound
        {
            public NetEndpoint from;
            public byte[] data;
            public int length;
        }

        readonly MemoryTransportHub m_Hub;
        readonly Queue<Inbound> m_Inbox = new Queue<Inbound>();
        bool m_Disposed;

        public NetEndpoint Local { get; }
        /// <summary>While true this endpoint is cut off: everything it sends and everything sent to it is lost (headset asleep, cable pulled).</summary>
        public bool Blackhole;
        /// <summary>While true, broadcasts from this endpoint go nowhere (a network that drops broadcast); unicast still works.</summary>
        public bool BroadcastBlocked;
        public bool IsDisposed => m_Disposed;
        public long SentCount { get; private set; }
        public long ReceivedCount { get; private set; }

        internal MemoryTransport(MemoryTransportHub hub, NetEndpoint local)
        {
            m_Hub = hub;
            Local = local;
        }

        public bool Send(NetEndpoint to, byte[] data, int length)
        {
            if (m_Disposed) return false;
            SentCount++;
            return m_Hub.Send(this, to, data, length);
        }

        public bool Receive(out NetEndpoint from, byte[] buffer, out int length)
        {
            from = default(NetEndpoint);
            length = 0;
            if (m_Disposed || buffer == null) return false;
            m_Hub.Pump();
            while (m_Inbox.Count > 0)
            {
                Inbound inbound = m_Inbox.Dequeue();
                bool fits = inbound.length <= buffer.Length;
                if (fits)
                {
                    Buffer.BlockCopy(inbound.data, 0, buffer, 0, inbound.length);
                    from = inbound.from;
                    length = inbound.length;
                }
                m_Hub.Recycle(inbound.data);
                if (!fits) continue; // too big for the caller's buffer: dropped, like a truncated UDP read
                ReceivedCount++;
                return true;
            }
            return false;
        }

        internal void Enqueue(NetEndpoint from, byte[] data, int length)
        {
            m_Inbox.Enqueue(new Inbound { from = from, data = data, length = length });
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            while (m_Inbox.Count > 0) m_Hub.Recycle(m_Inbox.Dequeue().data);
            m_Hub.Unregister(this);
        }
    }
}

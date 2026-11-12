using System;
using System.Threading;

namespace TrashAstronauts.Core.Voice
{
    /// <summary>
    /// Lock-free single-producer, single-consumer ring of samples: the main thread writes decoded voice, the
    /// audio thread reads it in its callback. Neither side ever waits or allocates. The producer may only call
    /// <see cref="Write"/>; the consumer only <see cref="Read"/>. A full ring drops what does not fit; an empty
    /// one gives fewer samples than asked (the caller plays silence for the rest).
    /// </summary>
    public sealed class VoiceRing
    {
        readonly float[] m_Data;
        readonly int m_Mask;
        long m_Write; // only the producer writes this
        long m_Read;  // only the consumer writes this
        long m_Dropped;
        long m_Underruns;

        /// <param name="capacity">Rounded up to a power of two.</param>
        public VoiceRing(int capacity)
        {
            int size = 1;
            while (size < Math.Max(16, capacity)) size <<= 1;
            m_Data = new float[size];
            m_Mask = size - 1;
        }

        public int Capacity => m_Data.Length;
        /// <summary>Samples waiting (safe to read from either thread).</summary>
        public int Count => (int)(Volatile.Read(ref m_Write) - Volatile.Read(ref m_Read));
        public long TotalRead => Volatile.Read(ref m_Read);
        public long TotalWritten => Volatile.Read(ref m_Write);
        /// <summary>Samples the producer could not fit.</summary>
        public long Dropped => Volatile.Read(ref m_Dropped);
        /// <summary>Times the consumer asked for more than was there.</summary>
        public long Underruns => Volatile.Read(ref m_Underruns);

        /// <summary>Producer side. Returns the samples written (the rest is dropped).</summary>
        public int Write(float[] src, int offset, int count)
        {
            if (src == null || count <= 0) return 0;
            long write = m_Write;
            long read = Volatile.Read(ref m_Read);
            int free = m_Data.Length - (int)(write - read);
            int n = Math.Min(count, free);
            for (int i = 0; i < n; i++) m_Data[(int)((write + i) & m_Mask)] = src[offset + i];
            Volatile.Write(ref m_Write, write + n);
            if (n < count) Interlocked.Add(ref m_Dropped, count - n);
            return n;
        }

        /// <summary>Consumer side. Returns the samples read into <paramref name="dst"/>.</summary>
        public int Read(float[] dst, int offset, int count)
        {
            if (dst == null || count <= 0) return 0;
            long read = m_Read;
            long write = Volatile.Read(ref m_Write);
            int available = (int)(write - read);
            int n = Math.Min(count, available);
            for (int i = 0; i < n; i++) dst[offset + i] = m_Data[(int)((read + i) & m_Mask)];
            Volatile.Write(ref m_Read, read + n);
            if (n < count && n > 0) Interlocked.Increment(ref m_Underruns);
            return n;
        }

        /// <summary>Consumer side: throws away everything waiting.</summary>
        public void Skip()
        {
            Volatile.Write(ref m_Read, Volatile.Read(ref m_Write));
        }
    }
}

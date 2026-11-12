using System;

namespace TrashAstronauts.Core.Voice
{
    /// <summary>
    /// Streaming sample-rate converter for any pair of integer rates (16 kHz to 12 kHz, 48 kHz to 16 kHz,
    /// 16 kHz to 44.1 kHz ...): a polyphase windowed-sinc (Blackman) low-pass at the common rate, evaluated only
    /// where an output sample falls, so it costs about <c>tapsPerPhase</c> multiply-adds per output sample.
    /// Keeps its history between calls, so a stream cut into blocks of any size comes out the same as in one piece.
    /// Allocates only in the constructor.
    /// </summary>
    public sealed class VoiceResampler
    {
        public readonly int InRate;
        public readonly int OutRate;
        readonly int m_Up;
        readonly int m_Down;
        readonly float[] m_Taps;
        readonly int m_History;
        readonly float[] m_Buffer;
        readonly int m_MaxBlock;
        readonly bool m_Passthrough;
        long m_InCount;
        long m_OutCount;

        /// <param name="maxBlock">Largest number of input samples handled in one pass (longer input is split).</param>
        /// <param name="cutoffHz">Low-pass corner; 0 = 43% of the lower of the two rates.</param>
        /// <param name="tapsPerPhase">Filter length per output sample of the slower side; more is sharper and slower.</param>
        public VoiceResampler(int inRate, int outRate, int maxBlock = 1024, float cutoffHz = 0f, int tapsPerPhase = 32)
        {
            if (inRate <= 0 || outRate <= 0) throw new ArgumentOutOfRangeException(nameof(inRate), "rates must be positive");
            InRate = inRate;
            OutRate = outRate;
            m_MaxBlock = Math.Max(16, maxBlock);
            int g = Gcd(inRate, outRate);
            m_Up = outRate / g;
            m_Down = inRate / g;
            m_Passthrough = m_Up == 1 && m_Down == 1;
            if (m_Passthrough)
            {
                m_Taps = new float[0];
                m_Buffer = new float[0];
                return;
            }
            if (m_Up > 4096 || m_Down > 4096) throw new ArgumentOutOfRangeException(nameof(inRate), "rate ratio too awkward: " + inRate + " to " + outRate);

            int length = Math.Max(8, tapsPerPhase) * Math.Max(m_Up, m_Down);
            double high = (double)inRate * m_Up;
            double cutoff = cutoffHz > 0f ? cutoffHz : 0.43 * Math.Min(inRate, outRate);
            cutoff = Math.Min(cutoff, 0.49 * Math.Min(inRate, outRate));
            double fc = cutoff / high; // cycles per high-rate sample
            m_Taps = new float[length];
            double centre = (length - 1) * 0.5;
            double sum = 0.0;
            var taps = new double[length];
            for (int k = 0; k < length; k++)
            {
                double x = k - centre;
                double sinc = Math.Abs(x) < 1e-9 ? 2.0 * fc : Math.Sin(2.0 * Math.PI * fc * x) / (Math.PI * x);
                double phase = 2.0 * Math.PI * k / (length - 1);
                double window = 0.42 - 0.5 * Math.Cos(phase) + 0.08 * Math.Cos(2.0 * phase);
                taps[k] = sinc * window;
                sum += taps[k];
            }
            // Unity gain at DC on the output side (zero-stuffing by Up divides the level by Up).
            for (int k = 0; k < length; k++) m_Taps[k] = (float)(taps[k] / sum * m_Up);
            m_History = (length + m_Up - 1) / m_Up + 1;
            m_Buffer = new float[m_History + m_MaxBlock];
        }

        static int Gcd(int a, int b)
        {
            while (b != 0)
            {
                int t = a % b;
                a = b;
                b = t;
            }
            return a;
        }

        public bool IsPassthrough => m_Passthrough;

        /// <summary>Most output samples <paramref name="inputCount"/> inputs can produce (size output buffers with this).</summary>
        public int MaxOutput(int inputCount) => (int)((long)Math.Max(0, inputCount) * m_Up / m_Down) + 2;

        /// <summary>Forgets the history (as if newly made).</summary>
        public void Reset()
        {
            m_InCount = 0;
            m_OutCount = 0;
            Array.Clear(m_Buffer, 0, m_Buffer.Length);
        }

        /// <summary>
        /// Converts <paramref name="count"/> input samples. Returns how many were written to <paramref name="dst"/>;
        /// output beyond <paramref name="dstCapacity"/> is dropped (size it with <see cref="MaxOutput"/>).
        /// </summary>
        public int Process(float[] src, int offset, int count, float[] dst, int dstOffset, int dstCapacity)
        {
            if (src == null || dst == null || count <= 0) return 0;
            if (offset < 0 || offset + count > src.Length) return 0;
            dstCapacity = Math.Min(dstCapacity, dst.Length - dstOffset);
            if (dstCapacity <= 0) return 0;
            if (m_Passthrough)
            {
                int n = Math.Min(count, dstCapacity);
                Array.Copy(src, offset, dst, dstOffset, n);
                return n;
            }
            int written = 0;
            while (count > 0)
            {
                int n = Math.Min(count, m_MaxBlock);
                written += Block(src, offset, n, dst, dstOffset + written, dstCapacity - written);
                offset += n;
                count -= n;
            }
            return written;
        }

        int Block(float[] src, int offset, int n, float[] dst, int dstOffset, int capacity)
        {
            Array.Copy(src, offset, m_Buffer, m_History, n);
            long baseIndex = m_InCount - m_History; // global input index of m_Buffer[0]
            long lastIn = m_InCount + n - 1;
            int written = 0;
            int length = m_Taps.Length;
            while (true)
            {
                long t = m_OutCount * m_Down;
                long j = t / m_Up;
                if (j > lastIn) break;
                int k = (int)(t - j * m_Up);
                float sum = 0f;
                int index = (int)(j - baseIndex);
                for (; k < length && index >= 0; k += m_Up, index--) sum += m_Taps[k] * m_Buffer[index];
                if (written < capacity) dst[dstOffset + written] = sum;
                written++;
                m_OutCount++;
            }
            Array.Copy(m_Buffer, n, m_Buffer, 0, m_History);
            m_InCount += n;
            return Math.Min(written, capacity);
        }
    }
}

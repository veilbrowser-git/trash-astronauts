using System;

namespace TrashAstronauts.Core.Voice
{
    /// <summary>What <see cref="VoiceJitterBuffer.Pull"/> produced for one 20 ms slot.</summary>
    public enum VoicePull : byte
    {
        /// <summary>Nothing to play (not talking, or still filling up at the start of a talk spurt): silence.</summary>
        Silent = 0,
        /// <summary>A received frame, decoded.</summary>
        Played = 1,
        /// <summary>The frame was missing: the last one repeated, fading out.</summary>
        Concealed = 2,
    }

    /// <summary>
    /// Receiving side of one talker: holds encoded frames by sequence number and hands them out one per 20 ms in
    /// order, so the network's jitter, reordering and loss become a steady stream.
    ///
    /// At the start of each talk spurt it waits until it holds the target delay (80 to 120 ms, chosen from the
    /// arrival jitter it has measured) before playing. While playing, a missing frame is concealed by repeating
    /// the last one at falling volume (silence after three in a row); a frame that arrives after its slot has
    /// passed is dropped as late; a duplicate is dropped; if the buffer runs well ahead of the target (after a
    /// burst) it skips frames to catch up. When nothing has arrived for a few slots the spurt is over and it goes
    /// back to silence. Fixed memory: 32 slots of <see cref="MaxFrameBytes"/>.
    /// </summary>
    public sealed class VoiceJitterBuffer
    {
        public const int Capacity = 32;
        public const int MaxFrameBytes = 160;
        /// <summary>Arrival-jitter window, in frames received.</summary>
        const int k_JitterWindow = 50;

        public int minDelayFrames = 4;
        public int maxDelayFrames = 6;
        /// <summary>Concealed slots (fading) before the output goes silent.</summary>
        public int fadeFrames = 3;
        /// <summary>Empty slots in a row, with nothing buffered, that end a talk spurt.</summary>
        public int endFrames = 5;
        /// <summary>Buffered frames beyond the target that trigger a catch-up skip.</summary>
        public int catchUpExcess = 3;

        readonly byte[][] m_Data = new byte[Capacity][];
        readonly int[] m_Length = new int[Capacity];
        readonly ushort[] m_Seq = new ushort[Capacity];
        readonly bool[] m_Has = new bool[Capacity];
        int m_Count;

        readonly float[] m_Last = new float[VoiceFormat.FrameSamples];
        bool m_HasLast;
        float m_ConcealGain;

        bool m_Playing;
        ushort m_Next;
        bool m_HasNext;
        int m_Waited;
        int m_Missing;
        int m_IdleSlots;

        readonly double[] m_Transit = new double[k_JitterWindow];
        int m_TransitCount;
        int m_TransitIndex;
        long m_ExtendedSeq;
        ushort m_LastArrivalSeq;
        bool m_HasArrival;

        public VoiceJitterBuffer()
        {
            for (int i = 0; i < Capacity; i++) m_Data[i] = new byte[MaxFrameBytes];
            TargetDelayFrames = minDelayFrames;
        }

        // ------------------------------------------------------------------ statistics
        public long Received { get; private set; }
        public long Late { get; private set; }
        public long Duplicates { get; private set; }
        public long Rejected { get; private set; }
        public long Played { get; private set; }
        public long Concealed { get; private set; }
        public long CatchUps { get; private set; }
        public long Spurts { get; private set; }

        /// <summary>Frames waiting.</summary>
        public int Count => m_Count;
        public bool IsPlaying => m_Playing;
        /// <summary>The delay (in frames) a talk spurt starts with; adapted to the measured jitter within [min, max].</summary>
        public int TargetDelayFrames { get; private set; }
        /// <summary>Spread of recent arrival times around the sender's clock (s).</summary>
        public double JitterSeconds { get; private set; }

        public void Clear()
        {
            for (int i = 0; i < Capacity; i++) m_Has[i] = false;
            m_Count = 0;
            m_Playing = false;
            m_HasNext = false;
            m_HasLast = false;
            m_Waited = 0;
            m_Missing = 0;
            m_IdleSlots = 0;
            m_TransitCount = 0;
            m_TransitIndex = 0;
            m_HasArrival = false;
            JitterSeconds = 0;
            TargetDelayFrames = minDelayFrames;
        }

        /// <summary>
        /// Stores a received frame. False (and nothing stored) for a late, duplicate, empty or oversized frame.
        /// <paramref name="now"/> is the local clock in seconds (only differences matter).
        /// </summary>
        public bool Push(ushort sequence, byte[] buffer, int offset, int length, double now)
        {
            if (buffer == null || length <= 0 || length > MaxFrameBytes || offset < 0 || offset + length > buffer.Length) { Rejected++; return false; }
            MeasureArrival(sequence, now);

            if (m_HasNext)
            {
                int ahead = VoiceFormat.SeqDelta(sequence, m_Next);
                if (ahead < 0)
                {
                    // Its slot has been played (or concealed) already. Long after a spurt ended, a new spurt can
                    // legitimately start anywhere: forget the old position then.
                    if (!m_Playing && m_IdleSlots > Capacity * 2) m_HasNext = false;
                    else { Late++; return false; }
                }
                else if (ahead >= Capacity)
                {
                    if (m_Playing)
                    {
                        // The talker jumped far ahead (a long gap mid-spurt): start over from this frame.
                        DropAll();
                        m_Playing = false;
                    }
                    m_HasNext = false;
                }
            }

            int slot = sequence % Capacity;
            if (m_Has[slot])
            {
                if (m_Seq[slot] == sequence) { Duplicates++; return false; }
                // An older frame that was never played sits in this slot: the new one replaces it.
                if (VoiceFormat.SeqDelta(sequence, m_Seq[slot]) < 0) { Late++; return false; }
                m_Count--;
            }
            Buffer.BlockCopy(buffer, offset, m_Data[slot], 0, length);
            m_Length[slot] = length;
            m_Seq[slot] = sequence;
            m_Has[slot] = true;
            m_Count++;
            Received++;
            return true;
        }

        void DropAll()
        {
            for (int i = 0; i < Capacity; i++) m_Has[i] = false;
            m_Count = 0;
        }

        void MeasureArrival(ushort sequence, double now)
        {
            if (!m_HasArrival)
            {
                m_HasArrival = true;
                m_ExtendedSeq = sequence;
            }
            else m_ExtendedSeq += VoiceFormat.SeqDelta(sequence, m_LastArrivalSeq);
            m_LastArrivalSeq = sequence;
            // How late this frame is compared with when the sender made it (plus an unknown constant).
            m_Transit[m_TransitIndex] = now - m_ExtendedSeq * VoiceFormat.FrameSeconds;
            m_TransitIndex = (m_TransitIndex + 1) % k_JitterWindow;
            if (m_TransitCount < k_JitterWindow) m_TransitCount++;
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < m_TransitCount; i++)
            {
                if (m_Transit[i] < lo) lo = m_Transit[i];
                if (m_Transit[i] > hi) hi = m_Transit[i];
            }
            JitterSeconds = hi - lo;
            int wanted = 1 + (int)Math.Ceiling(JitterSeconds / VoiceFormat.FrameSeconds - 1e-6);
            TargetDelayFrames = Math.Max(minDelayFrames, Math.Min(maxDelayFrames, wanted));
        }

        bool FindOldest(out ushort oldest, out ushort newest)
        {
            oldest = newest = 0;
            bool any = false;
            for (int i = 0; i < Capacity; i++)
            {
                if (!m_Has[i]) continue;
                ushort s = m_Seq[i];
                if (!any) { oldest = newest = s; any = true; continue; }
                if (VoiceFormat.SeqDelta(s, oldest) < 0) oldest = s;
                if (VoiceFormat.SeqDelta(s, newest) > 0) newest = s;
            }
            return any;
        }

        /// <summary>
        /// The next 20 ms (<see cref="VoiceFormat.FrameSamples"/> samples at 16 kHz) into <paramref name="output"/>.
        /// Call once per 20 ms of playback. No allocation.
        /// </summary>
        public VoicePull Pull(VoiceDecoder decoder, float[] output, int offset = 0)
        {
            if (!m_Playing)
            {
                if (m_Count == 0 || !FindOldest(out ushort oldest, out ushort newest))
                {
                    m_IdleSlots++;
                    m_Waited = 0;
                    Silence(output, offset);
                    return VoicePull.Silent;
                }
                m_Waited++;
                int span = VoiceFormat.SeqDelta(newest, oldest) + 1;
                if (span < TargetDelayFrames && m_Waited < TargetDelayFrames)
                {
                    Silence(output, offset);
                    return VoicePull.Silent;
                }
                m_Playing = true;
                m_Next = oldest;
                m_HasNext = true;
                m_Missing = 0;
                m_HasLast = false;
                m_IdleSlots = 0;
                Spurts++;
            }

            // After a burst the buffer can hold far more than the target: skip ahead (oldest frames are dropped).
            if (m_Count > TargetDelayFrames + catchUpExcess && FindOldest(out ushort _, out ushort last))
            {
                while (m_Count > TargetDelayFrames && VoiceFormat.SeqDelta(last, m_Next) > 0)
                {
                    int s = m_Next % Capacity;
                    if (m_Has[s] && m_Seq[s] == m_Next) { m_Has[s] = false; m_Count--; }
                    m_Next++;
                    CatchUps++;
                }
            }

            int slot = m_Next % Capacity;
            if (m_Has[slot] && m_Seq[slot] == m_Next)
            {
                m_Has[slot] = false;
                m_Count--;
                m_Next++;
                if (decoder != null && decoder.Decode(m_Data[slot], 0, m_Length[slot], output, offset))
                {
                    Array.Copy(output, offset, m_Last, 0, VoiceFormat.FrameSamples);
                    m_HasLast = true;
                    m_ConcealGain = 1f;
                    m_Missing = 0;
                    Played++;
                    return VoicePull.Played;
                }
                Rejected++;
            }
            else m_Next++;

            // Missing: conceal, and end the spurt once nothing more is coming.
            m_Missing++;
            Concealed++;
            if (m_Count == 0 && m_Missing >= endFrames)
            {
                m_Playing = false;
                m_Waited = 0;
            }
            if (m_HasLast && m_Missing <= fadeFrames)
            {
                float start = m_ConcealGain;
                float end = Math.Max(0f, 1f - m_Missing / (float)fadeFrames);
                for (int i = 0; i < VoiceFormat.FrameSamples; i++)
                {
                    float g = start + (end - start) * (i + 1) / VoiceFormat.FrameSamples;
                    output[offset + i] = m_Last[i] * g;
                }
                m_ConcealGain = end;
            }
            else Silence(output, offset);
            return VoicePull.Concealed;
        }

        static void Silence(float[] output, int offset) => Array.Clear(output, offset, VoiceFormat.FrameSamples);
    }
}

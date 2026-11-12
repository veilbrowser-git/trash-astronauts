using System;

namespace TrashAstronauts.Core.Net
{
    /// <summary>A value that can be blended toward a newer sample of itself.</summary>
    public interface ISnapshot<T> where T : struct
    {
        /// <summary>
        /// Returns the state at fraction <paramref name="t"/> from this sample to <paramref name="next"/>.
        /// t is 0..1 between samples and may exceed 1 while extrapolating: implementations extrapolate
        /// positions only and hold everything else at <paramref name="next"/>.
        /// </summary>
        T Blend(in T next, float t);
    }

    /// <summary>A bare position + rotation sample (salvage items).</summary>
    public struct PoseSnapshot : ISnapshot<PoseSnapshot>
    {
        public NetVec3 position;
        public NetQuat rotation;

        public PoseSnapshot(NetVec3 position, NetQuat rotation) { this.position = position; this.rotation = rotation; }

        public PoseSnapshot Blend(in PoseSnapshot next, float t)
        {
            return new PoseSnapshot(NetVec3.Lerp(position, next.position, t), NetQuat.Nlerp(rotation, next.rotation, t));
        }
    }

    /// <summary>
    /// Time-ordered samples of a remote object, rendered <see cref="InterpolationDelay"/> (100 ms) in the past so
    /// there are normally two samples to blend between. When the newest sample is older than the render time the
    /// buffer extrapolates along the last two samples for at most <see cref="ExtrapolationCap"/> (250 ms) and
    /// then holds still. Out-of-order arrivals are sorted in; duplicates are ignored. Fixed capacity, no allocation
    /// after construction.
    /// </summary>
    public sealed class SnapshotBuffer<T> where T : struct, ISnapshot<T>
    {
        public const double InterpolationDelay = 0.1;
        public const double ExtrapolationCap = 0.25;
        /// <summary>Two samples further apart than this are not used as a velocity for extrapolation.</summary>
        public const double MaxExtrapolationSpan = 0.5;

        readonly double[] m_Times;
        readonly T[] m_Values;
        int m_Head;
        int m_Count;

        public SnapshotBuffer(int capacity = 32)
        {
            if (capacity < 2) capacity = 2;
            m_Times = new double[capacity];
            m_Values = new T[capacity];
        }

        public int Count => m_Count;
        public int Capacity => m_Times.Length;
        public double NewestTime => m_Count > 0 ? m_Times[Index(m_Count - 1)] : double.NegativeInfinity;
        public double OldestTime => m_Count > 0 ? m_Times[Index(0)] : double.NegativeInfinity;

        /// <summary>The time to sample at for a given "now" on the sender's clock.</summary>
        public static double RenderTime(double senderTimeNow) { return senderTimeNow - InterpolationDelay; }

        int Index(int i) { return (m_Head + i) % m_Times.Length; }

        public void Clear()
        {
            m_Head = 0;
            m_Count = 0;
        }

        /// <summary>Adds a sample. Returns false when it was a duplicate time or older than everything in a full buffer.</summary>
        public bool Push(double time, in T value)
        {
            if (double.IsNaN(time) || double.IsInfinity(time)) return false;
            int capacity = m_Times.Length;
            if (m_Count == 0 || time > m_Times[Index(m_Count - 1)])
            {
                if (m_Count == capacity)
                {
                    m_Head = (m_Head + 1) % capacity;
                    m_Count--;
                }
                int at = Index(m_Count);
                m_Times[at] = time;
                m_Values[at] = value;
                m_Count++;
                return true;
            }

            // late arrival: find its place
            int insert = m_Count;
            for (int i = 0; i < m_Count; i++)
            {
                double existing = m_Times[Index(i)];
                if (existing == time) return false;
                if (existing > time) { insert = i; break; }
            }
            if (m_Count == capacity)
            {
                if (insert == 0) return false; // older than everything we keep
                m_Head = (m_Head + 1) % capacity; // drop the oldest to make room
                m_Count--;
                insert--;
            }
            for (int i = m_Count; i > insert; i--)
            {
                int to = Index(i), from = Index(i - 1);
                m_Times[to] = m_Times[from];
                m_Values[to] = m_Values[from];
            }
            int slot = Index(insert);
            m_Times[slot] = time;
            m_Values[slot] = value;
            m_Count++;
            return true;
        }

        public bool TryGetNewest(out T value, out double time)
        {
            if (m_Count == 0) { value = default(T); time = 0; return false; }
            int at = Index(m_Count - 1);
            value = m_Values[at];
            time = m_Times[at];
            return true;
        }

        /// <summary>Seconds the render time is ahead of the newest sample (0 when interpolating). Above the cap the object is frozen.</summary>
        public double SecondsStale(double renderTime)
        {
            if (m_Count == 0) return 0;
            double ahead = renderTime - m_Times[Index(m_Count - 1)];
            return ahead > 0 ? ahead : 0;
        }

        /// <summary>The state at <paramref name="renderTime"/>. False only when the buffer is empty.</summary>
        public bool Sample(double renderTime, out T result)
        {
            if (m_Count == 0) { result = default(T); return false; }
            int newest = Index(m_Count - 1);
            if (m_Count == 1) { result = m_Values[newest]; return true; }

            if (renderTime >= m_Times[newest])
            {
                int previous = Index(m_Count - 2);
                double span = m_Times[newest] - m_Times[previous];
                double ahead = renderTime - m_Times[newest];
                if (ahead > ExtrapolationCap) ahead = ExtrapolationCap;
                if (ahead <= 0 || span < 1e-4 || span > MaxExtrapolationSpan) { result = m_Values[newest]; return true; }
                result = m_Values[previous].Blend(in m_Values[newest], (float)(1.0 + ahead / span));
                return true;
            }

            int oldest = Index(0);
            if (renderTime <= m_Times[oldest]) { result = m_Values[oldest]; return true; }

            for (int i = m_Count - 2; i >= 0; i--)
            {
                int a = Index(i);
                if (m_Times[a] > renderTime) continue;
                int b = Index(i + 1);
                double span = m_Times[b] - m_Times[a];
                float t = span > 1e-9 ? (float)((renderTime - m_Times[a]) / span) : 1f;
                result = m_Values[a].Blend(in m_Values[b], t);
                return true;
            }
            result = m_Values[oldest];
            return true;
        }
    }
}

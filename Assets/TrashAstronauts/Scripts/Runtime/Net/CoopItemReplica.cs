using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using UnityEngine;
using static TrashAstronauts.Net.CoopConvert;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// Ownership epochs as items carry them on the wire: 6 bits in a pose, 8 in an ItemRest, compared on the
    /// low 6 bits with wrap-around. Pure.
    /// </summary>
    public static class CoopEpoch
    {
        public const int Mask = ItemPoseEntry.EpochMask;

        /// <summary>
        /// Compares an incoming epoch with the current one (-1 = none seen yet): 0 same, 1 newer (adopt it),
        /// -1 older (drop). Up to half the 6-bit range ahead counts as newer.
        /// </summary>
        public static int Compare(int current, int incoming)
        {
            if (current < 0) return 1;
            int diff = (incoming - current) & Mask;
            if (diff == 0) return 0;
            return diff < (Mask + 1) / 2 ? 1 : -1;
        }
    }

    /// <summary>
    /// The copy of a salvage item that another machine simulates: on a client every item the host (or another
    /// crew member) moves, and on a host every item a client holds or is flying (co-op stage 3). The body is
    /// kinematic (<see cref="SalvageItem.RefreshBodyMode"/> decides that); this only moves it, about 100 ms
    /// behind the poses that arrive, through the core's SnapshotBuffer. When this machine hands the item back
    /// it glides from where it was to the streamed pose over <see cref="BlendSeconds"/> instead of jumping.
    /// Added by <see cref="MissionDirector.ApplyRunStart"/> on a client and by CoopItemHost on a host; never
    /// present offline.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CoopItemReplica : MonoBehaviour
    {
        readonly SnapshotBuffer<PoseSnapshot> m_Buffer = new SnapshotBuffer<PoseSnapshot>(16);
        SalvageItem m_Item;

        /// <summary>The current ownership epoch (low 6 bits), -1 before the first pose.</summary>
        public int Epoch { get; private set; } = -1;
        public bool Held { get; private set; }
        public bool AtRest { get; private set; }
        public int PosesAccepted { get; private set; }
        public int PosesDropped { get; private set; }

        /// <summary>
        /// Move the kinematic body through the physics engine (MovePosition) instead of placing it, so it carries
        /// its velocity into contacts: an item in a crew member's hand knocks the host's items about (and they
        /// take the knock) as hard as it really swings. Set by CoopItemHost; snaps still place it directly.
        /// </summary>
        public bool physicsDriven;

        /// <summary>How long the glide from a locally simulated pose back to the stream lasts.</summary>
        public const double BlendSeconds = 0.2;
        double m_BlendStart = -1, m_BlendEnd = -1, m_BlendDuration;
        bool m_BlendArmed;
        Vector3 m_BlendFromPosition;
        Quaternion m_BlendFromRotation = Quaternion.identity;
        /// <summary>True while it glides back to the stream after this machine stopped simulating it.</summary>
        public bool Blending => m_BlendEnd > LocalNow;

        // ---- a hand-back while the item is still flying (here or as another crew member's flight seen here)
        /// <summary>The glide onto the stream when the item is still flying: long enough that it never looks like a correction.</summary>
        public const double MovingBlendSeconds = 0.5;
        /// <summary>How long the lead (the stream drawn as far ahead as it lags) takes to fade back to plain playback.</summary>
        public const double LeadFadeSeconds = 1.0;
        /// <summary>Below this an item counts as still at a hand-back: the old short glide.</summary>
        public const float MovingSpeed = 0.2f;
        /// <summary>One-way delay from the host to here, seconds (NetSession sets it from the measured round trip).</summary>
        public static double LinkLatency = 0.05;
        bool m_Moving;
        Vector3 m_SeedPosition, m_SeedVelocity;
        Quaternion m_SeedRotation = Quaternion.identity;
        double m_SeedTime, m_LeadStart = -1, m_Lead0;
        // The stream's own velocity (from its newest samples, smoothed; the seed's until it is known) and, while
        // gliding, the offset from the stream that fades out.
        Vector3 m_LeadVelocity, m_BlendOffset;
        bool m_NeedOffset;
        /// <summary>Hand-backs of a moving item handled with the forward glide (tests).</summary>
        public int MovingHandbacks { get; private set; }

        /// <summary>Where the item would be now had it kept flying as it was at the hand-back.</summary>
        Vector3 SeedPath(double now) => m_SeedPosition + m_SeedVelocity * (float)(now - m_SeedTime);

        void BeginMoving(Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            m_Moving = true;
            m_SeedPosition = position;
            m_SeedRotation = rotation;
            m_SeedVelocity = velocity;
            m_LeadVelocity = velocity;
            m_SeedTime = LocalNow;
            m_LeadStart = -1;
            m_BlendDuration = MovingBlendSeconds;
            m_BlendArmed = true;
            m_BlendEnd = -1;
            MovingHandbacks++;
        }

        /// <summary>Velocity of the stream around <paramref name="time"/> (zero when it cannot tell).</summary>
        bool StreamVelocity(double time, out Vector3 velocity)
        {
            velocity = Vector3.zero;
            if (m_Buffer.Count < 2) return false;
            // Only inside the samples: before the first one the buffer holds still, which is no speed at all.
            double t1 = System.Math.Min(time, m_Buffer.NewestTime);
            double t0 = System.Math.Max(t1 - 0.1, m_Buffer.OldestTime);
            if (t1 - t0 < 0.04 || !m_Buffer.Sample(t1, out PoseSnapshot a) || !m_Buffer.Sample(t0, out PoseSnapshot b)) return false;
            velocity = (ToUnity(a.position) - ToUnity(b.position)) / (float)(t1 - t0);
            return true;
        }

        /// <summary>
        /// How far in the past items and the creature replica are drawn: SnapshotBuffer.InterpolationDelay (100 ms) on Wi-Fi; NetSession sets
        /// NetSession.OnlineInterpolationDelay for an online session and puts it back when the session ends.
        /// </summary>
        public static double Delay = SnapshotBuffer<PoseSnapshot>.InterpolationDelay;

        /// <summary>This machine's clock for samples: unscaled, like the network's.</summary>
        public static double LocalNow => Time.unscaledTimeAsDouble;

        public static CoopItemReplica For(SalvageItem item)
        {
            var replica = item.GetComponent<CoopItemReplica>();
            if (replica == null) replica = item.gameObject.AddComponent<CoopItemReplica>();
            replica.m_Item = item;
            return replica;
        }

        public void ResetForRun()
        {
            m_Buffer.Clear();
            Epoch = -1;
            Held = false;
            AtRest = true;
            m_BlendArmed = false;
            m_BlendEnd = -1;
            m_Moving = false;
        }

        bool AcceptEpoch(int epoch)
        {
            epoch &= CoopEpoch.Mask;
            int order = CoopEpoch.Compare(Epoch, epoch);
            if (order < 0) return false;
            if (order > 0)
            {
                // Somebody else simulates it now: earlier samples belong to a different flight. If it was flying,
                // it carries on along its path here and glides onto the new stream (never back along it).
                if (Epoch >= 0)
                {
                    Vector3 velocity = Vector3.zero;
                    bool flying = !AtRest && m_Item != null && !m_Item.SimulatedHere
                        && StreamVelocity(System.Math.Min(LocalNow - Delay, m_Buffer.NewestTime), out velocity)
                        && velocity.magnitude > MovingSpeed;
                    m_Buffer.Clear();
                    if (flying) BeginMoving(transform.position, transform.rotation, velocity);
                }
                Epoch = epoch;
            }
            return true;
        }

        /// <summary>A streamed pose. False (and ignored) for a stale epoch.</summary>
        public bool Push(Vector3 position, Quaternion rotation, int epoch, bool held, bool atRest, double localTime = double.NaN)
        {
            if (!AcceptEpoch(epoch))
            {
                PosesDropped++;
                return false;
            }
            double time = double.IsNaN(localTime) ? LocalNow : localTime;
            if (!m_Buffer.Push(time, new PoseSnapshot(ToNet(position), ToNet(rotation))))
            {
                PosesDropped++;
                return false;
            }
            PosesAccepted++;
            Held = held;
            AtRest = atRest;
            StartBlendIfArmed();
            return true;
        }

        /// <summary>The item settled at this pose on the host: glide there and stay.</summary>
        public bool Rest(Vector3 position, Quaternion rotation, int epoch)
        {
            if (!AcceptEpoch(epoch)) return false;
            double time = LocalNow;
            if (m_Buffer.Count > 0 && time <= m_Buffer.NewestTime) time = m_Buffer.NewestTime + 0.001;
            m_Buffer.Push(time, new PoseSnapshot(ToNet(position), ToNet(rotation)));
            Held = false;
            AtRest = true;
            StartBlendIfArmed();
            return true;
        }

        /// <summary>
        /// This machine stops simulating the item: forget samples from before (another flight) and stay where it
        /// is under <paramref name="epoch"/> until the stream says otherwise; from the first pose that arrives it
        /// glides there over <paramref name="blendSeconds"/> instead of jumping.
        /// </summary>
        public void Seed(Vector3 position, Quaternion rotation, int epoch, double blendSeconds = BlendSeconds, Vector3 velocity = default(Vector3))
        {
            m_Buffer.Clear();
            Epoch = epoch & CoopEpoch.Mask;
            Held = false;
            AtRest = true;
            m_BlendEnd = -1;
            m_BlendDuration = blendSeconds;
            m_BlendArmed = blendSeconds > 0;
            m_Moving = false;
            // Still flying when handed back: keep it on its path until the host's stream arrives, then glide onto it.
            if (blendSeconds > 0 && velocity.magnitude > MovingSpeed) BeginMoving(position, rotation, velocity);
        }

        /// <summary>The first pose after a seed starts the glide from wherever the item is shown now.</summary>
        void StartBlendIfArmed()
        {
            if (!m_BlendArmed) return;
            m_BlendArmed = false;
            m_BlendFromPosition = transform.position;
            m_BlendFromRotation = transform.rotation;
            m_BlendStart = LocalNow;
            m_BlendEnd = LocalNow + m_BlendDuration;
            if (m_Moving)
            {
                // The stream is Delay plus the trip behind the item itself: draw it that far ahead, fading slowly.
                m_LeadStart = LocalNow;
                m_Lead0 = Delay + (LinkLatency > 0 && LinkLatency < 1 ? LinkLatency : 0.05);
                m_NeedOffset = true;
            }
        }

        /// <summary>
        /// A pose whose epoch the caller has already checked (a host following the one client that owns the item).
        /// </summary>
        public bool Follow(Vector3 position, Quaternion rotation, bool held, double localTime = double.NaN)
        {
            double time = double.IsNaN(localTime) ? LocalNow : localTime;
            if (m_Buffer.Count > 0 && time <= m_Buffer.NewestTime) time = m_Buffer.NewestTime + 0.001;
            if (!m_Buffer.Push(time, new PoseSnapshot(ToNet(position), ToNet(rotation))))
            {
                PosesDropped++;
                return false;
            }
            PosesAccepted++;
            Held = held;
            AtRest = false;
            StartBlendIfArmed();
            return true;
        }

        /// <summary>Forgets every sample (this machine simulates the item again).</summary>
        public void ClearSamples()
        {
            m_Buffer.Clear();
            m_BlendEnd = -1;
            m_BlendArmed = false;
            m_Moving = false;
        }

        /// <summary>Jumps straight to a pose (a lost item brought back).</summary>
        public void Snap(Vector3 position, Quaternion rotation)
        {
            m_Buffer.Clear();
            m_Buffer.Push(LocalNow, new PoseSnapshot(ToNet(position), ToNet(rotation)));
            AtRest = true;
            m_BlendArmed = false;
            m_BlendEnd = -1;
            m_Moving = false;
            Move(position, rotation);
        }

        void Update()
        {
            if (m_Item == null) m_Item = GetComponent<SalvageItem>();
            if (m_Item == null || m_Item.IsSecured || m_Item.IsMounted || m_Item.SimulatedHere) return;
            if (m_Buffer.Count == 0)
            {
                // Handed back mid-flight and the host's stream is not here yet: it keeps flying, not frozen.
                if (m_Moving && m_BlendArmed) Move(SeedPath(LocalNow), m_SeedRotation, !physicsDriven);
                return;
            }
            double renderTime = LocalNow - Delay;
            PoseSnapshot pose;
            // Something at rest stays exactly where it was put: never carried on past it by extrapolation.
            if (AtRest && renderTime >= m_Buffer.NewestTime)
            {
                if (!m_Buffer.TryGetNewest(out pose, out _)) return;
            }
            else if (!m_Buffer.Sample(renderTime, out pose)) return;
            Vector3 position = ToUnity(pose.position);
            Quaternion rotation = ToUnity(pose.rotation);
            double now = LocalNow;
            if (m_Moving && m_LeadStart >= 0)
            {
                // A flight handed back here: the stream is drawn where the item really is now (its own motion carried
                // ahead by the delay and the trip), the lead fading slowly to plain playback, and the glide onto it
                // only fades out the small difference from where it was shown. It never turns back along its path.
                if (StreamVelocity(m_Buffer.NewestTime, out Vector3 measured))
                    m_LeadVelocity = Vector3.Lerp(m_LeadVelocity, measured, 1f - Mathf.Exp(-Time.unscaledDeltaTime / 0.1f));
                bool resting = AtRest && renderTime >= m_Buffer.NewestTime;
                if (!resting && renderTime < m_Buffer.OldestTime)
                    position += m_LeadVelocity * (float)(renderTime - m_Buffer.OldestTime); // before its first sample: where it was then
                double lead = m_Lead0 * (1.0 - (now - m_LeadStart) / LeadFadeSeconds);
                if (lead > 0 && !resting) position += m_LeadVelocity * (float)lead;
                else if (m_BlendEnd <= now) m_Moving = false;
                if (m_NeedOffset)
                {
                    m_NeedOffset = false;
                    m_BlendOffset = transform.position - position;
                }
            }
            if (m_BlendEnd > now && m_BlendEnd > m_BlendStart)
            {
                float k = Mathf.SmoothStep(0f, 1f, (float)((now - m_BlendStart) / (m_BlendEnd - m_BlendStart)));
                if (m_Moving) position += m_BlendOffset * (1f - k);
                else position = Vector3.Lerp(m_BlendFromPosition, position, k);
                rotation = Quaternion.Slerp(m_BlendFromRotation, rotation, k);
            }
            Move(position, rotation, !physicsDriven);
        }

        void Move(Vector3 position, Quaternion rotation, bool place = true)
        {
            var body = m_Item != null ? m_Item.Body : null;
            if (!place && body != null && body.isKinematic)
            {
                body.MovePosition(position);
                body.MoveRotation(rotation);
                return;
            }
            transform.SetPositionAndRotation(position, rotation);
            if (body != null && body.isKinematic)
            {
                body.position = position;
                body.rotation = rotation;
            }
        }
    }
}

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
        }

        bool AcceptEpoch(int epoch)
        {
            epoch &= CoopEpoch.Mask;
            int order = CoopEpoch.Compare(Epoch, epoch);
            if (order < 0) return false;
            if (order > 0)
            {
                // Somebody else simulates it now: earlier samples belong to a different flight.
                if (Epoch >= 0) m_Buffer.Clear();
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
        public void Seed(Vector3 position, Quaternion rotation, int epoch, double blendSeconds = BlendSeconds)
        {
            m_Buffer.Clear();
            Epoch = epoch & CoopEpoch.Mask;
            Held = false;
            AtRest = true;
            m_BlendEnd = -1;
            m_BlendDuration = blendSeconds;
            m_BlendArmed = blendSeconds > 0;
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
        }

        /// <summary>Jumps straight to a pose (a lost item brought back).</summary>
        public void Snap(Vector3 position, Quaternion rotation)
        {
            m_Buffer.Clear();
            m_Buffer.Push(LocalNow, new PoseSnapshot(ToNet(position), ToNet(rotation)));
            AtRest = true;
            m_BlendArmed = false;
            m_BlendEnd = -1;
            Move(position, rotation);
        }

        void Update()
        {
            if (m_Item == null) m_Item = GetComponent<SalvageItem>();
            if (m_Item == null || m_Item.IsSecured || m_Item.IsMounted || m_Item.SimulatedHere || m_Buffer.Count == 0) return;
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
            if (m_BlendEnd > now && m_BlendEnd > m_BlendStart)
            {
                float k = Mathf.SmoothStep(0f, 1f, (float)((now - m_BlendStart) / (m_BlendEnd - m_BlendStart)));
                position = Vector3.Lerp(m_BlendFromPosition, position, k);
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

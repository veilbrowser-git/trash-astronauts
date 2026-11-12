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
    /// On a co-op client, the copy of a salvage item that another machine (the host, in the ride-along stage)
    /// simulates. The body is kinematic (<see cref="SalvageItem.RefreshBodyMode"/> decides that); this only
    /// moves it, about 100 ms behind the poses that arrive, through the core's SnapshotBuffer. Added to an
    /// item by <see cref="MissionDirector.ApplyRunStart"/> on a client; never present offline or on a host.
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
            return true;
        }

        /// <summary>Jumps straight to a pose (a lost item brought back).</summary>
        public void Snap(Vector3 position, Quaternion rotation)
        {
            m_Buffer.Clear();
            m_Buffer.Push(LocalNow, new PoseSnapshot(ToNet(position), ToNet(rotation)));
            AtRest = true;
            Move(position, rotation);
        }

        void Update()
        {
            if (m_Item == null) m_Item = GetComponent<SalvageItem>();
            if (m_Item == null || m_Item.IsSecured || m_Item.IsMounted || m_Item.SimulatedHere || m_Buffer.Count == 0) return;
            double renderTime = SnapshotBuffer<PoseSnapshot>.RenderTime(LocalNow);
            PoseSnapshot pose;
            // Something at rest stays exactly where it was put: never carried on past it by extrapolation.
            if (AtRest && renderTime >= m_Buffer.NewestTime)
            {
                if (!m_Buffer.TryGetNewest(out pose, out _)) return;
            }
            else if (!m_Buffer.Sample(renderTime, out pose)) return;
            Move(ToUnity(pose.position), ToUnity(pose.rotation));
        }

        void Move(Vector3 position, Quaternion rotation)
        {
            transform.SetPositionAndRotation(position, rotation);
            var body = m_Item != null ? m_Item.Body : null;
            if (body != null && body.isKinematic)
            {
                body.position = position;
                body.rotation = rotation;
            }
        }
    }
}

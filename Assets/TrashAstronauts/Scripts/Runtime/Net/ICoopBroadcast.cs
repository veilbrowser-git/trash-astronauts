using TrashAstronauts.Core.Net;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// What the host's game tells the crew (co-op stage 2). <see cref="CoopHostRules"/> calls one method per
    /// host-side change, carrying exactly the fields of the matching message of the wire protocol, with the
    /// same numeric ids (NetIdTable indices; item = index in RunStartParams.items; players by slot). The
    /// session adapter implements it by forwarding each call to the NetSessionHost Send* method of the same
    /// name; nothing here knows about sockets.
    /// </summary>
    public interface ICoopBroadcast
    {
        // ---- run
        /// <summary>A contract started (host->all). Already limited to the ride-along contract.</summary>
        void RunStart(in RunStartParams parameters);
        /// <summary>Latest-value door, panel, weld and web state (host->all), sent right after RunStart.</summary>
        void WorldState(in WorldStateMsg state);
        /// <summary>Answer to one client's extraction request: these slots are not aboard (host->requester).</summary>
        void ExtractRefused(byte slot, byte missingSlotsMask);
        /// <summary>The contract ended (host->all).</summary>
        void RunEnded(in RunEndedMsg result);
        /// <summary>The host left the results screen (host->all).</summary>
        void ReturnedToHub();
        /// <summary>The host's hazards were paused or resumed (host->all).</summary>
        void HostPause(bool on);

        // ---- items
        void ItemCollected(byte itemId, byte by);
        void ItemValue(byte itemId, int value);
        void ItemDeposited(byte itemId, byte by, int finalValue, int trickBonus, byte lockerSlot);
        void ItemRecovered(byte itemId, NetVec3 position);
        /// <summary>A host-simulated item has been slow for 0.5 s; the epoch is the session's to fill.</summary>
        void ItemRest(byte itemId, NetVec3 position, NetQuat rotation);

        // ---- world
        void DoorState(byte doorId, bool open);
        void PointCut(byte panel, byte index);
        void PanelOpened(byte panel, byte by, NetVec3 plateImpulse, NetVec3 plateTorque);
    }

    /// <summary>Unity values to and from the core's wire types.</summary>
    public static class CoopConvert
    {
        public static NetVec3 ToNet(Vector3 v) => new NetVec3(v.x, v.y, v.z);
        public static NetQuat ToNet(Quaternion q) => new NetQuat(q.x, q.y, q.z, q.w);
        public static Vector3 ToUnity(NetVec3 v) => new Vector3(v.x, v.y, v.z);
        public static Quaternion ToUnity(NetQuat q)
        {
            var n = q.Normalized;
            return new Quaternion(n.x, n.y, n.z, n.w);
        }
    }
}

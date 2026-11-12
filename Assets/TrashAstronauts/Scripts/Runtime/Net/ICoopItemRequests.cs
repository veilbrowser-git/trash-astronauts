using TrashAstronauts.Core.Net;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// What a co-op client's hands tell the host about salvage (co-op stage 3). <see cref="CoopItemClient"/> and
    /// its <see cref="ItemSync"/> components call one method per client->host item message, with exactly the
    /// fields of that message and the same numeric ids (item = index in RunStartParams.items). The session
    /// adapter implements it by forwarding each call to the NetSessionClient method of the matching name
    /// (GrabRequest -> SendGrabRequest, ..., QueueItemPose -> QueueItemPose); nothing here knows about sockets.
    /// </summary>
    public interface ICoopItemRequests
    {
        /// <summary>A local hand took the item (it is already in the hand here). Reliable; flush at once.</summary>
        void GrabRequest(byte itemId, byte epochSeen, byte hand);

        /// <summary>The last local hand let go. Sent for every real release, thrown or not.</summary>
        void ItemReleased(byte itemId, byte epoch, NetVec3 position, NetQuat rotation, NetVec3 releasePosition);

        /// <summary>Follow-up to a release once the game has registered the throw (two physics steps later).</summary>
        void ItemThrown(byte itemId, byte epoch, NetVec3 linearVelocity, NetVec3 angularVelocity, float throwSpeed, bool wasThrown);

        /// <summary>The flight is over (slow for 0.3 s, or 1.5 s after the release): the host simulates it again from here.</summary>
        void ItemHandoff(byte itemId, byte epoch, NetVec3 position, NetQuat rotation, NetVec3 linearVelocity, NetVec3 angularVelocity);

        /// <summary>An item this machine simulates took a knock and should now be worth <paramref name="newValue"/>. The host decides.</summary>
        void ItemDamageReport(byte itemId, int newValue);

        /// <summary>
        /// Unreliable stream: the pose of an item this machine simulates (held or flying). Called every frame while
        /// it is simulated here, moving or not; a later call for the same item replaces the earlier one.
        /// </summary>
        void QueueItemPose(byte itemId, NetVec3 position, NetQuat rotation, byte epoch, bool held);
    }
}

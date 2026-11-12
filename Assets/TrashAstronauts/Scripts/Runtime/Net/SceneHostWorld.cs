using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// The real scene as the host session sees it. Stage 1 (presence) implements the roster and the avatars:
    /// crew members appear, move, go away and leave. Everything else is a deliberate no-op until the stage that
    /// shares it, and each one says which stage that is. While these are no-ops the session cannot do anything
    /// to the shared game: contracts are blocked (MissionDirector.ContractStartBlocked) and nothing grabs,
    /// opens or cuts on another headset's behalf.
    /// </summary>
    public sealed class SceneHostWorld : IHostWorld
    {
        readonly NetSession m_Net;
        readonly MissionDirector m_Director;

        public SceneHostWorld(NetSession net, MissionDirector director)
        {
            m_Net = net;
            m_Director = director;
        }

        public MissionPhase Phase => m_Director != null ? m_Director.Phase : MissionPhase.Hub;

        // ------------------------------------------------------------------ stage 1: presence

        public void FillWorldState(ref WorldStateMsg state)
        {
            // Stage 2: door and panel masks, done welds, wire states. In the hub of a stage 1 session there is
            // nothing shared to describe, so the cleared state the session hands in is the truth.
        }

        public void OnPlayerJoined(byte slot, byte handedness) => m_Net.AddCrew(slot, handedness, false);

        public void OnPlayerLeft(byte slot, PlayerLeaveReason reason) => m_Net.RemoveCrew(slot);

        public void OnPlayerAway(byte slot, bool away) => m_Net.SetCrewAway(slot, away);

        public void OnPlayerSettings(byte slot, byte handedness) => m_Net.SetCrewHandedness(slot, handedness);

        public void OnAvatar(byte slot, in AvatarSnapshot avatar, double hostTime) => m_Net.PushCrewAvatar(slot, hostTime, in avatar);

        // ------------------------------------------------------------------ stage 2: the shared contract

        public void OnNoise(byte slot, NetVec3 position, float loudness, NoiseKind kind) { /* Stage 4: feed NoiseSystem as PlayerId(slot). */ }

        public void OnStartRequest(byte slot) { /* Stage 2: start the shared contract. Stage 1 blocks contracts in a session. */ }

        public void OnExtractRequest(byte slot) { /* Stage 2: all-aboard extraction. */ }

        // ------------------------------------------------------------------ stage 3: items

        public bool CanGrabItem(byte itemId, byte slot) => false; // Stage 3. No run items exist in a stage 1 session.

        public void OnItemGranted(byte itemId, byte slot, byte hand) { /* Stage 3 */ }

        public void OnItemPose(byte itemId, byte slot, NetVec3 position, NetQuat rotation, bool held, double hostTime) { /* Stage 3 */ }

        public void OnItemReleased(byte itemId, byte slot, NetVec3 position, NetQuat rotation, NetVec3 releasePosition) { /* Stage 3 */ }

        public void OnItemThrown(byte itemId, byte slot, NetVec3 linearVelocity, NetVec3 angularVelocity, float throwSpeed, bool wasThrown) { /* Stage 3 */ }

        public void OnItemReturned(in ItemReturn item) { /* Stage 3: clear RemoteHolder, make the body dynamic at the pose. */ }

        public void OnItemDamageReport(byte itemId, byte slot, int newValue) { /* Stage 3 */ }

        // ------------------------------------------------------------------ stage 2: world

        public void OnDoorRequest(byte slot, byte doorId, bool desiredOpen) { /* Stage 2: SlidingDoor.SetOpen(desired, false, PlayerId(slot)). */ }

        public void OnPointCutReport(byte slot, byte panel, byte index) { /* Stage 2 */ }

        // ------------------------------------------------------------------ stage 4: creature and hazards

        public void OnTranqHitReport(byte slot, NetVec3 origin, NetVec3 hitPoint) { /* Stage 4 */ }

        public void OnWireReport(byte slot, byte index, NetWireChange change) { /* Stage 4 */ }
    }
}

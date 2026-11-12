using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// The real scene as the host session sees it. Presence (stage 1): crew members appear, move, go away and
    /// leave. The shared ride-along contract (stage 2): every request from the crew goes to the session's
    /// <see cref="CoopHostRules"/>, which decides it with the game's own rules exactly as if the host had
    /// pressed the button, and whose broadcasts go back out through NetSessionHost.
    /// Item grabs (stage 3) and the creature and its hazards (stage 4) are still deliberate no-ops, each
    /// marked with its stage: in this version only the host carries salvage and there is no creature in co-op.
    /// </summary>
    public sealed class SceneHostWorld : IHostWorld
    {
        readonly NetSession m_Net;
        readonly MissionDirector m_Director;
        readonly CoopHostRules m_Rules;

        public SceneHostWorld(NetSession net, MissionDirector director, CoopHostRules rules)
        {
            m_Net = net;
            m_Director = director;
            m_Rules = rules;
        }

        public CoopHostRules Rules => m_Rules;

        /// <summary>Reported in HostInfo and checked on every Hello: a join is refused ContractInProgress while a contract runs.</summary>
        public MissionPhase Phase => m_Director != null ? m_Director.Phase : MissionPhase.Hub;

        // ------------------------------------------------------------------ presence

        public void FillWorldState(ref WorldStateMsg state)
        {
            if (m_Rules != null) m_Rules.FillWorldState(ref state);
        }

        public void OnPlayerJoined(byte slot, byte handedness) => m_Net.AddCrew(slot, handedness, false);

        public void OnPlayerLeft(byte slot, PlayerLeaveReason reason) => m_Net.RemoveCrew(slot);

        public void OnPlayerAway(byte slot, bool away)
        {
            m_Net.SetCrewAway(slot, away);
            if (m_Rules != null) m_Rules.HostPlayerAway(slot, away);
        }

        public void OnPlayerSettings(byte slot, byte handedness) => m_Net.SetCrewHandedness(slot, handedness);

        public void OnAvatar(byte slot, in AvatarSnapshot avatar, double hostTime) => m_Net.PushCrewAvatar(slot, hostTime, in avatar);

        // ------------------------------------------------------------------ the shared contract

        public void OnStartRequest(byte slot)
        {
            if (m_Rules != null) m_Rules.HostStartRequest(slot);
        }

        public void OnExtractRequest(byte slot)
        {
            if (m_Rules != null) m_Rules.HostExtractRequest(slot);
        }

        public void OnDoorRequest(byte slot, byte doorId, bool desiredOpen)
        {
            if (m_Rules != null) m_Rules.HostDoorRequest(doorId, desiredOpen, slot);
        }

        public void OnPointCutReport(byte slot, byte panel, byte index)
        {
            if (m_Rules != null) m_Rules.HostPointCutReport(panel, index, slot);
        }

        // ------------------------------------------------------------------ stage 3: items (only the host carries in this version)

        /// <summary>No crew member can pick anything up yet: every client's items are "HOST ONLY".</summary>
        public bool CanGrabItem(byte itemId, byte slot) => false;

        public void OnItemGranted(byte itemId, byte slot, byte hand) { /* Stage 3: never granted (CanGrabItem is false). */ }

        public void OnItemPose(byte itemId, byte slot, NetVec3 position, NetQuat rotation, bool held, double hostTime) { /* Stage 3 */ }

        public void OnItemReleased(byte itemId, byte slot, NetVec3 position, NetQuat rotation, NetVec3 releasePosition) { /* Stage 3 */ }

        public void OnItemThrown(byte itemId, byte slot, NetVec3 linearVelocity, NetVec3 angularVelocity, float throwSpeed, bool wasThrown) { /* Stage 3 */ }

        public void OnItemReturned(in ItemReturn item) { /* Stage 3: no client ever owns an item in this version. */ }

        public void OnItemDamageReport(byte itemId, byte slot, int newValue) { /* Stage 3: a client never simulates an item, so never reports damage. */ }

        // ------------------------------------------------------------------ stage 4: creature and hazards (none in co-op yet)

        public void OnNoise(byte slot, NetVec3 position, float loudness, NoiseKind kind) { /* Stage 4: no creature to hear it. */ }

        public void OnTranqHitReport(byte slot, NetVec3 origin, NetVec3 hitPoint) { /* Stage 4 */ }

        public void OnWireReport(byte slot, byte index, NetWireChange change) { /* Stage 4: webs are never armed in a co-op contract. */ }
    }
}

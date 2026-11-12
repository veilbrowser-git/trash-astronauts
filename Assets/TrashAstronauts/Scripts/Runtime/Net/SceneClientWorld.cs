using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// The real scene as a joined client sees it. Stage 1 (presence) implements the Welcome (reset, take the
    /// slot, make stand-ins for the crew already there), the roster, the avatars, the host's silence and the end
    /// of the session. Everything else is a deliberate no-op until the stage that shares it, and says which.
    /// The host cannot send any of those messages in a stage 1 session (contracts are blocked), so ignoring
    /// them is exact, not lossy.
    /// </summary>
    public sealed class SceneClientWorld : IClientWorld
    {
        readonly NetSession m_Net;

        public SceneClientWorld(NetSession net)
        {
            m_Net = net;
        }

        // ------------------------------------------------------------------ stage 1: presence

        public void OnWelcome(byte localSlot, in WelcomeMsg welcome) => m_Net.ApplyWelcome(localSlot, in welcome);

        public void OnRosterChanged(byte slot, RosterChange change, byte handedness)
        {
            switch (change)
            {
                case RosterChange.Joined: m_Net.AddCrew(slot, handedness, false); break;
                case RosterChange.Left: m_Net.RemoveCrew(slot); break;
                case RosterChange.Away: m_Net.SetCrewAway(slot, true); break;
                case RosterChange.Back: m_Net.SetCrewAway(slot, false); break;
                case RosterChange.Settings: m_Net.SetCrewHandedness(slot, handedness); break;
            }
        }

        public void OnHostSilent(bool silent) => m_Net.HostSilent = silent;

        public void OnSessionEnded(SessionEndReason reason) => m_Net.QueueClientEnded(reason);

        public void OnRemoteAvatar(byte slot, in AvatarSnapshot avatar, double hostTime) => m_Net.PushCrewAvatar(slot, hostTime, in avatar);

        // ------------------------------------------------------------------ stage 2: the shared contract

        public void OnWorldState(in WorldStateMsg state) { /* Stage 2: doors, panels, welds, wires. Empty in a stage 1 hub. */ }

        public void OnHostPause(bool on) { /* Stage 2: HOST PAUSED overlay and frozen oxygen. */ }

        public void OnClock(float elapsedSeconds, float oxygenRemainingSeconds, byte flags) { /* Stage 2 */ }

        public void OnRunStart(in RunStartParams parameters) { /* Stage 2: MissionDirector.BeginRun after RunStartCodec.IsPlanHashValid. */ }

        public void OnExtractRefused(byte missingSlotsMask) { /* Stage 2 */ }

        public void OnRunEnded(in RunEndedMsg result) { /* Stage 2: MissionDirector.ApplyRunEnded. */ }

        public void OnReturnedToHub() { /* Stage 2 */ }

        // ------------------------------------------------------------------ stage 3: items

        public void OnItemPose(in ItemPoseEntry pose, double hostTime) { /* Stage 2/3 */ }

        public void OnItemOwner(byte itemId, byte ownerSlot, byte epoch, bool held, bool denied) { /* Stage 3: ItemAuthority; GrantedNotHeld -> ItemReleased. */ }

        public void OnItemCollected(byte itemId, byte by) { /* Stage 2 */ }

        public void OnItemRest(byte itemId, byte epoch, NetVec3 position, NetQuat rotation) { /* Stage 2 */ }

        public void OnItemValue(byte itemId, int value) { /* Stage 2 */ }

        public void OnItemDeposited(byte itemId, byte by, int finalValue, int trickBonus, byte lockerSlot) { /* Stage 2 */ }

        public void OnItemRecovered(byte itemId, NetVec3 position) { /* Stage 2 */ }

        // ------------------------------------------------------------------ stage 2: world

        public void OnDoorState(byte doorId, bool open) { /* Stage 2: SlidingDoor.ApplyReplicated. */ }

        public void OnPointCut(byte panel, byte index) { /* Stage 2 */ }

        public void OnPanelOpened(byte panel, byte by, NetVec3 plateImpulse, NetVec3 plateTorque) { /* Stage 2 */ }

        // ------------------------------------------------------------------ stage 4: creature and hazards

        public void OnCreatureState(in CreatureSnapshot creature, double hostTime) { /* Stage 4 */ }

        public void OnCreatureStateChanged(EnemyState state, byte targetSlot) { /* Stage 4 */ }

        public void OnTranquilized(float seconds) { /* Stage 4 */ }

        public void OnCatchStarted(byte victimSlot) { /* Stage 4 */ }

        public void OnTranqTracer(byte by, NetVec3 from, NetVec3 to) { /* Stage 5 */ }

        public void OnBlackoutStarted(float seconds) { /* Stage 4 */ }

        public void OnBlackoutEnded(bool instant) { /* Stage 4 */ }

        public void OnWireChanged(byte index, NetWireChange change, byte by) { /* Stage 4 */ }
    }
}

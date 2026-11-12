using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// The real scene as a joined client sees it. Presence (stage 1): the Welcome (reset, take the slot, make
    /// stand-ins for the crew already there), the roster, the avatars, the host's silence and the end of the
    /// session. The shared ride-along contract (stage 2): every host decision is handed to the session's
    /// <see cref="CoopClientApply"/>, which applies it to the director's idempotent mirror.
    /// Item ownership (stage 3) and the creature and its hazards (stage 4) are deliberate no-ops: a stage 2 host
    /// never grants a grab and never runs a creature, blackout or web in a co-op contract.
    /// </summary>
    public sealed class SceneClientWorld : IClientWorld
    {
        readonly NetSession m_Net;
        readonly CoopClientApply m_Apply;

        public SceneClientWorld(NetSession net, CoopClientApply apply)
        {
            m_Net = net;
            m_Apply = apply;
        }

        public CoopClientApply Apply => m_Apply;

        // ------------------------------------------------------------------ presence

        public void OnWelcome(byte localSlot, in WelcomeMsg welcome)
        {
            m_Net.ApplyWelcome(localSlot, in welcome);
            m_Apply.ApplyWorldState(in welcome.world);
        }

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

        public void OnHostSilent(bool silent)
        {
            m_Net.HostSilent = silent;
            m_Apply.ApplyHostSilent(silent);
        }

        public void OnSessionEnded(SessionEndReason reason) => m_Net.QueueClientEnded(reason);

        public void OnRemoteAvatar(byte slot, in AvatarSnapshot avatar, double hostTime) => m_Net.PushCrewAvatar(slot, hostTime, in avatar);

        // ------------------------------------------------------------------ the shared contract

        public void OnWorldState(in WorldStateMsg state) => m_Apply.ApplyWorldState(in state);

        public void OnHostPause(bool on) => m_Apply.ApplyHostPause(on);

        public void OnClock(float elapsedSeconds, float oxygenRemainingSeconds, byte flags) => m_Apply.ApplyClock(elapsedSeconds, oxygenRemainingSeconds, flags);

        public void OnRunStart(in RunStartParams parameters) => m_Apply.ApplyRunStart(in parameters);

        public void OnExtractRefused(byte missingSlotsMask) => m_Apply.ApplyExtractRefused(missingSlotsMask);

        public void OnRunEnded(in RunEndedMsg result) => m_Apply.ApplyRunEnded(in result);

        public void OnReturnedToHub() => m_Apply.ApplyReturnedToHub();

        public void OnItemPose(in ItemPoseEntry pose, double hostTime) => m_Apply.ApplyItemPose(in pose, hostTime);

        public void OnItemCollected(byte itemId, byte by) => m_Apply.ApplyItemCollected(itemId, by);

        public void OnItemRest(byte itemId, byte epoch, NetVec3 position, NetQuat rotation) => m_Apply.ApplyItemRest(itemId, epoch, position, rotation);

        public void OnItemValue(byte itemId, int value) => m_Apply.ApplyItemValue(itemId, value);

        public void OnItemDeposited(byte itemId, byte by, int finalValue, int trickBonus, byte lockerSlot) => m_Apply.ApplyItemDeposited(itemId, by, finalValue, trickBonus, lockerSlot);

        public void OnItemRecovered(byte itemId, NetVec3 position) => m_Apply.ApplyItemRecovered(itemId, position);

        public void OnDoorState(byte doorId, bool open) => m_Apply.ApplyDoorState(doorId, open);

        public void OnPointCut(byte panel, byte index) => m_Apply.ApplyPointCut(panel, index);

        public void OnPanelOpened(byte panel, byte by, NetVec3 plateImpulse, NetVec3 plateTorque) => m_Apply.ApplyPanelOpened(panel, by, plateImpulse, plateTorque);

        // ------------------------------------------------------------------ stage 3: items

        /// <summary>
        /// Stage 3. In this version the host only ever names itself (it locks deposited items, which the
        /// ItemDeposited that follows applies in full), so there is nothing to act on here.
        /// </summary>
        public void OnItemOwner(byte itemId, byte ownerSlot, byte epoch, bool held, bool denied) { }

        // ------------------------------------------------------------------ stage 4: creature and hazards (none in co-op yet)

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

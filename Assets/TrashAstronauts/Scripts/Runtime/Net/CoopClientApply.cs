using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// Co-op stage 2 on a client: one method per IClientWorld call the ride-along contract needs, with the
    /// same parameters (numeric ids as the session delivers them), each resolving its ids and handing the
    /// host's decision to the director's idempotent mirror (MissionDirector.Apply*). Unknown ids are ignored
    /// and return false. Plain C#; one per client session.
    /// </summary>
    public sealed class CoopClientApply
    {
        readonly MissionDirector m_Director;
        double m_HostToLocal;
        bool m_HaveHostClock;

        public CoopClientApply(MissionDirector director)
        {
            m_Director = director;
            Creature = new CoopCreatureClient(director);
            Revive = new CoopReviveClient(director);
        }

        public MissionDirector Director => m_Director;
        /// <summary>The host's creature and blackouts on this client (stage 4).</summary>
        public CoopCreatureClient Creature { get; }
        /// <summary>Co-op revive on this client: who is down, the rings, and this player's hand on a beacon.</summary>
        public CoopReviveClient Revive { get; }

        /// <summary>A wire slot as a player id (255 = nobody).</summary>
        public static PlayerId Who(byte slot) => slot == 255 ? PlayerId.None : new PlayerId(slot);

        // ------------------------------------------------------------------ session

        /// <summary>IClientWorld.OnWorldState (the downed crew included).</summary>
        public void ApplyWorldState(in WorldStateMsg state)
        {
            m_Director.ApplyWorldState(in state);
            Revive.ApplyWorldState(in state);
        }

        /// <summary>IClientWorld.OnHostPause.</summary>
        public void ApplyHostPause(bool on) => m_Director.ApplyHostPause(on);

        /// <summary>IClientWorld.OnHostSilent: freezes the local clock while the host is not heard.</summary>
        public void ApplyHostSilent(bool silent) => m_Director.ApplyHostSilent(silent);

        /// <summary>IClientWorld.OnClock.</summary>
        public bool ApplyClock(float elapsedSeconds, float oxygenRemainingSeconds, byte flags)
        {
            m_Director.ApplyHostPause((flags & ClockFlags.HazardsPaused) != 0);
            return m_Director.ApplyClock(elapsedSeconds, oxygenRemainingSeconds);
        }

        // ------------------------------------------------------------------ run

        /// <summary>IClientWorld.OnRunStart (the plan hash is checked here).</summary>
        public bool ApplyRunStart(in RunStartParams parameters)
        {
            m_HaveHostClock = false;
            return m_Director.ApplyRunStart(in parameters);
        }

        /// <summary>IClientWorld.OnExtractRefused.</summary>
        public void ApplyExtractRefused(byte missingSlotsMask) => m_Director.ApplyExtractRefused(missingSlotsMask);

        /// <summary>
        /// IClientWorld.OnRunEnded. Null when there was no run to end (a repeat), and also when it arrived while
        /// the jumpscare is still playing here: it is then applied when the jumpscare ends (1 s at most).
        /// </summary>
        public MissionResult ApplyRunEnded(in RunEndedMsg result)
        {
            var ended = result.ToResult();
            if (Creature.HoldRunEnded(() => m_Director.ApplyRunEnded(ended))) return null;
            return m_Director.ApplyRunEnded(ended);
        }

        /// <summary>IClientWorld.OnReturnedToHub.</summary>
        public bool ApplyReturnedToHub() => m_Director.ApplyReturnedToHub();

        /// <summary>Link lost or session left mid-contract: back to the hub, nothing banked.</summary>
        public bool AbortRun() => m_Director.AbortRun();

        // ------------------------------------------------------------------ items

        /// <summary>
        /// IClientWorld.OnItemPose. <paramref name="hostTime"/> is the host's clock when it was sent (NaN = use
        /// the arrival time); it is mapped onto this machine's clock with the smallest delay seen, so network
        /// jitter does not shake the item.
        /// </summary>
        public bool ApplyItemPose(in ItemPoseEntry pose, double hostTime = double.NaN)
        {
            var item = m_Director.RunItemAt(pose.itemId);
            if (item == null) return false;
            return m_Director.ApplyItemPose(item, CoopConvert.ToUnity(pose.position), CoopConvert.ToUnity(pose.rotation),
                pose.epoch, pose.held, pose.atRest, LocalTimeOf(hostTime));
        }

        double LocalTimeOf(double hostTime)
        {
            double now = CoopItemReplica.LocalNow;
            if (double.IsNaN(hostTime) || double.IsInfinity(hostTime)) return now;
            double offset = now - hostTime;
            if (!m_HaveHostClock || offset < m_HostToLocal)
            {
                m_HostToLocal = offset;
                m_HaveHostClock = true;
            }
            else
            {
                m_HostToLocal += (offset - m_HostToLocal) * 0.01; // follow slow clock drift
            }
            return System.Math.Min(now, hostTime + m_HostToLocal);
        }

        /// <summary>IClientWorld.OnItemCollected.</summary>
        public bool ApplyItemCollected(byte itemId, byte by) => m_Director.ApplyItemCollected(m_Director.RunItemAt(itemId), Who(by));

        /// <summary>IClientWorld.OnItemRest.</summary>
        public bool ApplyItemRest(byte itemId, byte epoch, NetVec3 position, NetQuat rotation)
        {
            return m_Director.ApplyItemRest(m_Director.RunItemAt(itemId), epoch, CoopConvert.ToUnity(position), CoopConvert.ToUnity(rotation));
        }

        /// <summary>IClientWorld.OnItemValue.</summary>
        public bool ApplyItemValue(byte itemId, int value) => m_Director.ApplyItemValue(m_Director.RunItemAt(itemId), value);

        /// <summary>IClientWorld.OnItemDeposited.</summary>
        public bool ApplyItemDeposited(byte itemId, byte by, int finalValue, int trickBonus, byte lockerSlot)
        {
            return m_Director.ApplyItemDeposited(m_Director.RunItemAt(itemId), Who(by), finalValue, trickBonus, lockerSlot);
        }

        /// <summary>IClientWorld.OnItemRecovered.</summary>
        public bool ApplyItemRecovered(byte itemId, NetVec3 position)
        {
            return m_Director.ApplyItemRecovered(m_Director.RunItemAt(itemId), CoopConvert.ToUnity(position));
        }

        // ------------------------------------------------------------------ creature and hazards (stage 4)

        /// <summary>
        /// IClientWorld.OnCreatureState. <paramref name="hostTime"/> is the host's clock when it was sent (NaN = now),
        /// mapped onto this machine's clock like item poses; the replica is shown about 100 ms behind.
        /// </summary>
        public bool ApplyCreatureSnapshot(in CreatureSnapshot creature, double hostTime = double.NaN)
            => Creature.ApplyCreatureSnapshot(in creature, LocalTimeOf(hostTime));

        /// <summary>IClientWorld.OnCreatureStateChanged.</summary>
        public bool ApplyCreatureStateChanged(EnemyState state, byte targetSlot) => Creature.ApplyCreatureStateChanged(state, targetSlot);

        /// <summary>IClientWorld.OnTranquilized.</summary>
        public bool ApplyTranquilized(float seconds) => Creature.ApplyTranquilized(seconds);

        /// <summary>IClientWorld.OnCatchStarted.</summary>
        public bool ApplyCatchStarted(byte victimSlot) => Creature.ApplyCatchStarted(victimSlot);

        /// <summary>IClientWorld.OnBlackoutStarted.</summary>
        public bool ApplyBlackoutStarted(float seconds) => Creature.ApplyBlackoutStarted(seconds);

        /// <summary>IClientWorld.OnBlackoutEnded.</summary>
        public bool ApplyBlackoutEnded(bool instant) => Creature.ApplyBlackoutEnded(instant);

        // ------------------------------------------------------------------ co-op revive

        /// <summary>IClientWorld.OnPlayerDowned (held while a jumpscare plays here).</summary>
        public bool ApplyPlayerDowned(byte slot, NetVec3 position) => Revive.ApplyPlayerDowned(slot, position);

        /// <summary>IClientWorld.OnPlayerRevived.</summary>
        public bool ApplyPlayerRevived(byte slot, byte by) => Revive.ApplyPlayerRevived(slot, by);

        /// <summary>IClientWorld.OnReviveProgress.</summary>
        public bool ApplyReviveProgress(byte slot, float progress, byte rescuerSlot) => Revive.ApplyReviveProgress(slot, progress, rescuerSlot);

        /// <summary>A crew member left (RosterChanged Left).</summary>
        public void ApplyPlayerLeft(byte slot) => Revive.PlayerLeft(slot);

        // ------------------------------------------------------------------ world

        /// <summary>IClientWorld.OnDoorState.</summary>
        public bool ApplyDoorState(byte doorId, bool open) => m_Director.ApplyDoorState(m_Director.DoorAt(doorId), open);

        /// <summary>IClientWorld.OnPointCut.</summary>
        public bool ApplyPointCut(byte panel, byte index) => m_Director.ApplyPointCut(m_Director.PanelAt(panel), index, PlayerId.None);

        /// <summary>IClientWorld.OnPanelOpened.</summary>
        public bool ApplyPanelOpened(byte panel, byte by, NetVec3 plateImpulse, NetVec3 plateTorque)
        {
            return m_Director.ApplyPanelOpened(m_Director.PanelAt(panel), Who(by),
                CoopConvert.ToUnity(plateImpulse), CoopConvert.ToUnity(plateTorque));
        }

        // ------------------------------------------------------------------ items in everyone's hands (stage 3)

        /// <summary>
        /// IClientWorld.OnItemOwner: who simulates an item now, handed to the session's <see cref="CoopItemClient"/>
        /// (MissionDirector.ItemClient). None when there is none (a session without item sync, whose items stay host-only).
        /// </summary>
        public ItemAuthorityEvent ApplyItemOwner(byte itemId, byte ownerSlot, byte epoch, bool held, bool denied)
        {
            var items = m_Director.ItemClient;
            return items != null ? items.ApplyItemOwner(itemId, ownerSlot, epoch, held, denied) : ItemAuthorityEvent.None;
        }
    }
}

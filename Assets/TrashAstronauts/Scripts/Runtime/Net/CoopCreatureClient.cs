using System;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// Co-op stage 4 on a client: the host's creature and blackouts, applied to this scene. One method per
    /// IClientWorld call, numeric ids as the session delivers them; each resolves them and hands the host's word
    /// to the creature replica (EnemyDrone.Apply*) or the blackout (BlackoutController.ApplyRemote*). Nothing here
    /// decides anything. Owned by <see cref="CoopClientApply"/>; plain C#.
    /// </summary>
    public sealed class CoopCreatureClient
    {
        readonly MissionDirector m_Director;

        public CoopCreatureClient(MissionDirector director)
        {
            m_Director = director ?? throw new ArgumentNullException(nameof(director));
        }

        /// <summary>The replica of the host's creature this contract, or null when none is aboard (or no contract runs).</summary>
        public EnemyDrone Creature
        {
            get
            {
                if (m_Director.Phase != MissionPhase.Active || !m_Director.CreatureAboard) return null;
                var creature = m_Director.ActiveEnemy;
                return creature != null && creature.IsReplica ? creature : null;
            }
        }

        /// <summary>IClientWorld.OnCreatureState, with the sample's time already on this machine's unscaled clock.</summary>
        public bool ApplyCreatureSnapshot(in CreatureSnapshot snapshot, double localTime)
        {
            var creature = Creature;
            return creature != null && creature.ApplySnapshot(in snapshot, localTime);
        }

        /// <summary>IClientWorld.OnCreatureStateChanged: the state and its cue, once per message.</summary>
        public bool ApplyCreatureStateChanged(EnemyState state, byte targetSlot)
        {
            var creature = Creature;
            return creature != null && creature.ApplyStateChanged(state, CoopClientApply.Who(targetSlot));
        }

        /// <summary>IClientWorld.OnTranquilized.</summary>
        public bool ApplyTranquilized(float seconds)
        {
            var creature = Creature;
            return creature != null && creature.ApplyTranquilized(seconds);
        }

        /// <summary>
        /// IClientWorld.OnCatchStarted: the jumpscare plays here. On the victim's own headset it is today's, on the
        /// real camera; on anyone else's it happens to the victim's avatar, with none of the victim's effects. The
        /// run itself ends only when the host's RunEnded arrives.
        /// </summary>
        public bool ApplyCatchStarted(byte victimSlot)
        {
            var creature = Creature;
            if (creature == null || victimSlot >= NetProtocol.MaxPlayers) return false;
            var id = new PlayerId(victimSlot);
            var victim = m_Director.FindPlayer(id);
            bool local = id == m_Director.LocalPlayerId && victim != null && !victim.IsRemote;
            return creature.ApplyCatchStarted(victim, id, local);
        }

        /// <summary>IClientWorld.OnBlackoutStarted: the host's blackout (none is ever scheduled on a client).</summary>
        public bool ApplyBlackoutStarted(float seconds)
        {
            var blackout = m_Director.blackout;
            if (blackout == null || m_Director.Phase != MissionPhase.Active) return false;
            return blackout.ApplyRemoteStart(seconds);
        }

        /// <summary>IClientWorld.OnBlackoutEnded. Ignored while a jumpscare plays here: it keeps its own darkness.</summary>
        public bool ApplyBlackoutEnded(bool instant)
        {
            var blackout = m_Director.blackout;
            if (blackout == null) return false;
            var creature = m_Director.ActiveEnemy;
            if (creature != null && creature.IsScaring) return false;
            return blackout.ApplyRemoteEnd(instant);
        }

        /// <summary>
        /// True when a RunEnded must wait for the jumpscare playing here to finish (at most
        /// EnemyDrone.RunEndHoldSeconds); <paramref name="apply"/> is then called once, later.
        /// </summary>
        public bool HoldRunEnded(Action apply)
        {
            var creature = m_Director.ActiveEnemy;
            return creature != null && creature.HoldRunEnded(apply);
        }
    }
}

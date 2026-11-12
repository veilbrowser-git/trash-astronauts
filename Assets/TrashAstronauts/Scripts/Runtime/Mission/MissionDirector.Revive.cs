using TrashAstronauts.Core;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// Co-op revive seams of the director. In a crew session a catch can down the victim instead of failing the
    /// contract (the host's <see cref="ReviveHost"/> decides); a downed player is not "up" for the all-aboard rule and
    /// does not add exertion to the shared tank, and each player banks their own share after their revive penalties.
    /// Offline neither <see cref="ReviveHost"/> nor <see cref="ReviveClient"/> exists and every path here is today's.
    /// </summary>
    public sealed partial class MissionDirector
    {
        /// <summary>The host's revive rules while hosting a crew (set by CoopHostRules); null offline and on a client.</summary>
        public CoopReviveHost ReviveHost { get; set; }
        /// <summary>A client's revive mirror and hand-on-beacon reporter (set by CoopClientApply); null offline and on a host.</summary>
        public CoopReviveClient ReviveClient { get; set; }

        /// <summary>
        /// The jumpscare of a catch is over (host and offline; a client's replica never ends a catch). In a crew with
        /// someone else still up the victim is downed where they were caught and the contract goes on; otherwise the
        /// contract fails for everyone, exactly as single-player. Returns the result when the run ended, else null.
        /// </summary>
        public MissionResult EndCatch(EnemyDrone creature, PlayerId victim)
        {
            if (Net != null && Net.IsClient) return null;
            if (ReviveHost != null && ReviveHost.TryDown(victim, creature)) return null;
            return FailMission(FailureReason.Caught, victim);
        }

        /// <summary>True when the creature may not catch <paramref name="target"/> right now (a fresh revive, or near a body it just downed). Always false offline.</summary>
        public bool CatchBlocked(PlayerContext target) => ReviveHost != null && ReviveHost.BlocksCatch(target);

        /// <summary>The crew slot this headset banks for: its own slot in a session, -1 (the whole payout) offline.</summary>
        int LocalPayoutSlot => Net != null ? LocalPlayerId.Value : -1;

        /// <summary>Adds the crew's revive record (downs, and who was left behind at an extraction) to a result before it is banked and sent.</summary>
        void StampRevive(MissionResult result, bool extraction)
        {
            if (result != null && ReviveHost != null) ReviveHost.Stamp(result, extraction);
        }

        /// <summary>Everyone is up again (end of a contract, leaving a session).</summary>
        public void ClearCrewDowned()
        {
            for (int i = 0; i < m_Players.Count; i++)
            {
                var player = m_Players[i];
                if (player != null && player.IsDowned) player.SetDowned(false, player.DownedPosition);
            }
        }
    }
}

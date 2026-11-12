using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using UnityEngine;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// Audio and haptic warnings from the suit: oxygen low, oxygen critical (repeating), and the moment
    /// the drone starts hunting. These back up the wrist display so warnings do not depend on looking at it.
    /// </summary>
    public sealed class SuitAlerts : MonoBehaviour
    {
        public PlayerContext player;
        public float criticalRepeatSeconds = 2.2f;

        MissionDirector m_Director;
        OxygenTank m_Tank;
        float m_NextCritical;
        bool m_WasHunted;

        void Update()
        {
            var director = MissionDirector.Active;
            if (director == null || director.Session == null) return;

            if (m_Director != director || m_Tank != director.Session.Oxygen)
            {
                if (m_Tank != null) m_Tank.LevelChanged -= OnLevelChanged;
                m_Director = director;
                m_Tank = director.Session.Oxygen;
                m_Tank.LevelChanged += OnLevelChanged;
            }

            bool active = director.Session.Phase == MissionPhase.Active && !director.HazardsPaused;
            if (!active)
            {
                m_WasHunted = false;
                return;
            }

            if (m_Tank.Level == OxygenWarningLevel.Critical && Time.time >= m_NextCritical
                && (player == null || !player.InSafeZone))
            {
                m_NextCritical = Time.time + criticalRepeatSeconds;
                if (Sfx.Library != null) Sfx.PlayUi(Sfx.Library.oxygenCritical, 0.7f);
                if (player != null) player.PulseBoth(0.5f, 0.12f);
            }

            bool hunted = false;
            if (director.enemies != null)
            {
                foreach (var enemy in director.enemies)
                {
                    // In a crew only the hunted player's suit buzzes; offline it is always this player.
                    if (enemy != null && enemy.State == EnemyState.Chase && (director.Net == null || enemy.Quarry == director.LocalPlayerId)) hunted = true;
                }
            }
            if (hunted && !m_WasHunted && player != null) player.PulseBoth(0.7f, 0.25f);
            m_WasHunted = hunted;
        }

        void OnDestroy()
        {
            if (m_Tank != null) m_Tank.LevelChanged -= OnLevelChanged;
        }

        void OnLevelChanged(OxygenWarningLevel previous, OxygenWarningLevel next)
        {
            if (next == OxygenWarningLevel.Low && previous == OxygenWarningLevel.Normal)
            {
                if (Sfx.Library != null) Sfx.PlayUi(Sfx.Library.oxygenLow, 0.8f);
                if (player != null) player.PulseBoth(0.4f, 0.15f);
            }
            else if (next == OxygenWarningLevel.Critical)
            {
                m_NextCritical = 0f;
            }
        }
    }
}

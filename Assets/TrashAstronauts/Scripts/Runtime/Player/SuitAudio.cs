using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using UnityEngine;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// What the player hears inside their own helmet: breathing that quickens with exertion and thins as
    /// oxygen runs low, and a heartbeat that rises when the creature is near or hunting. Both are quiet
    /// and neither is positional; they are there to make the suit feel inhabited.
    /// </summary>
    public sealed class SuitAudio : MonoBehaviour
    {
        public PlayerContext player;
        public AudioSource breathing;
        public AudioSource heartbeat;
        [Range(0f, 1f)] public float breathingVolume = 0.22f;
        [Range(0f, 1f)] public float heartbeatVolume = 0.3f;
        public float threatRadius = 9f;

        void Update()
        {
            var director = MissionDirector.Active;
            if (director == null || director.Session == null) return;
            bool active = director.Phase == MissionPhase.Active;

            float exertion = player != null && player.locomotion != null && player.locomotion.zeroG != null
                ? player.locomotion.zeroG.Exertion : 0f;
            float oxygenStress = active ? 1f - Mathf.Clamp01(director.Session.Oxygen.Fraction / 0.3f) : 0f;

            if (breathing != null)
            {
                if (!breathing.isPlaying) breathing.Play();
                float effort = Mathf.Max(exertion, oxygenStress);
                breathing.volume = Mathf.Lerp(breathing.volume, breathingVolume * (0.45f + 0.55f * effort), Time.deltaTime * 3f);
                breathing.pitch = Mathf.Lerp(breathing.pitch, 0.9f + 0.55f * effort, Time.deltaTime * 2f);
            }

            if (heartbeat != null)
            {
                float threat = 0f;
                if (active && director.enemies != null)
                {
                    foreach (var enemy in director.enemies)
                    {
                        if (enemy == null || !enemy.isActiveAndEnabled || enemy.State == EnemyState.Dormant) continue;
                        float near = 1f - Mathf.Clamp01(enemy.NearestPlayerDistance / threatRadius);
                        if (enemy.State == EnemyState.Chase) near = Mathf.Max(near, 0.85f);
                        threat = Mathf.Max(threat, near);
                    }
                }
                if (threat > 0.02f && !heartbeat.isPlaying) heartbeat.Play();
                heartbeat.volume = Mathf.Lerp(heartbeat.volume, heartbeatVolume * threat, Time.deltaTime * 2.5f);
                heartbeat.pitch = Mathf.Lerp(heartbeat.pitch, 0.85f + 0.75f * threat, Time.deltaTime * 2f);
                if (threat <= 0.02f && heartbeat.isPlaying && heartbeat.volume < 0.01f) heartbeat.Stop();
            }
        }
    }
}

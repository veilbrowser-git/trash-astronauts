using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using UnityEngine;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// What the player hears inside their own helmet: breathing that quickens and grows louder with exertion,
    /// fear and low oxygen, then settles slowly; and a soft heartbeat that fades in only when the creature is
    /// chasing or very near, its rate following the same fear level. Both are quiet and neither is positional.
    /// All state is a handful of floats; nothing is allocated per frame.
    /// </summary>
    public sealed class SuitAudio : MonoBehaviour
    {
        public PlayerContext player;
        public AudioSource breathing;
        public AudioSource heartbeat;
        [Range(0f, 1f)] public float breathingVolume = 0.22f;
        [Range(0f, 1f)] public float heartbeatVolume = 0.3f;
        public float threatRadius = 9f;
        [Tooltip("Breaths per minute of the breathing loop at pitch 1 (the clip is one 3.4 s breath).")]
        public float clipBreathsPerMinute = 60f / 3.4f;
        [Tooltip("How fast fear and effort rise (per second) and fall back (per second).")]
        public float riseRate = 1.6f, fearSettleRate = 0.12f, effortSettleRate = 0.25f;
        [Tooltip("Fear at which the heartbeat starts and reaches full volume.")]
        public float heartbeatFearStart = 0.35f, heartbeatFearFull = 0.85f;

        /// <summary>Smoothed fear, 0..1: the creature close or chasing.</summary>
        public float Fear { get; private set; }
        /// <summary>Smoothed physical effort, 0..1 (exertion or oxygen stress).</summary>
        public float Effort { get; private set; }
        /// <summary>The breathing rate the loop currently plays at, in breaths per minute.</summary>
        public float BreathRate => clipBreathsPerMinute * (breathing != null ? breathing.pitch : 1f);
        /// <summary>Heartbeat rate in beats per minute (the clip is one beat per second at pitch 1).</summary>
        public float HeartRate => 60f * (heartbeat != null ? heartbeat.pitch : 1f);

        void Update()
        {
            var director = MissionDirector.Active;
            if (director == null || director.Session == null) return;
            bool active = director.Phase == MissionPhase.Active;

            float exertion = player != null && player.locomotion != null && player.locomotion.zeroG != null
                ? player.locomotion.zeroG.Exertion : 0f;
            float oxygenStress = active ? 1f - Mathf.Clamp01(director.Session.Oxygen.Fraction / 0.3f) : 0f;

            float threat = 0f;
            if (active && director.enemies != null)
            {
                var enemies = director.enemies;
                for (int i = 0; i < enemies.Length; i++)
                {
                    var enemy = enemies[i];
                    if (enemy == null || !enemy.isActiveAndEnabled) continue;
                    threat = Mathf.Max(threat, ThreatOf(enemy.State, enemy.NearestPlayerDistance, threatRadius));
                }
            }
            Tick(Time.deltaTime, Mathf.Max(exertion, oxygenStress), threat);
        }

        /// <summary>How frightening one creature is: near it, or any distance while it chases.</summary>
        public static float ThreatOf(EnemyState state, float distance, float radius)
        {
            if (state == EnemyState.Dormant) return 0f;
            float near = 1f - Mathf.Clamp01(distance / Mathf.Max(0.01f, radius));
            near *= near; // only really near is scary
            if (state == EnemyState.Chase) near = Mathf.Max(near, 0.85f);
            return near;
        }

        /// <summary>Advances the helmet sounds by dt for the given raw effort and threat (both 0..1).</summary>
        public void Tick(float dt, float effort, float threat)
        {
            if (dt <= 0f) return;
            Effort = Approach(Effort, Mathf.Clamp01(effort), riseRate, effortSettleRate, dt);
            Fear = Approach(Fear, Mathf.Clamp01(threat), riseRate, fearSettleRate, dt);
            float arousal = Mathf.Clamp01(Mathf.Max(Effort, 0.8f * Fear) + 0.25f * Mathf.Min(Effort, Fear));

            if (breathing != null)
            {
                if (!breathing.isPlaying && breathing.isActiveAndEnabled) breathing.Play();
                float k = 1f - Mathf.Exp(-3f * dt);
                breathing.volume = Mathf.Lerp(breathing.volume, breathingVolume * (0.45f + 0.55f * arousal), k);
                breathing.pitch = Mathf.Lerp(breathing.pitch, 0.9f + 0.6f * arousal, k);
            }

            if (heartbeat != null)
            {
                float level = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(heartbeatFearStart, heartbeatFearFull, Fear));
                if (level > 0.01f && !heartbeat.isPlaying && heartbeat.isActiveAndEnabled) heartbeat.Play();
                float k = 1f - Mathf.Exp(-2.5f * dt);
                heartbeat.volume = Mathf.Lerp(heartbeat.volume, heartbeatVolume * level, k);
                heartbeat.pitch = Mathf.Lerp(heartbeat.pitch, 0.85f + 0.75f * Fear, k);
                if (level <= 0.01f && heartbeat.isPlaying && heartbeat.volume < 0.005f) heartbeat.Stop();
            }
        }

        static float Approach(float current, float target, float up, float down, float dt)
            => Mathf.MoveTowards(current, target, (target > current ? up : down) * dt);
    }
}

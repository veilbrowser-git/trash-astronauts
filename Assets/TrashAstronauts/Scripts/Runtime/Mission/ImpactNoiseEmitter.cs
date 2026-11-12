using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// Turns hard physics impacts into sound and, above a threshold, into gameplay noise the drone can
    /// hear. Thresholds and a per-object cooldown keep resting contact and gentle drifting silent, so
    /// throwing something down a corridor is a deliberate distraction rather than constant accidental noise.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class ImpactNoiseEmitter : MonoBehaviour
    {
        [Tooltip("Scales gameplay loudness. Heavy objects are louder.")]
        public float loudnessScale = 1f;
        public bool heavy;
        public float audioSpeedThreshold = 0.7f;
        public float audioCooldown = 0.2f;

        float m_LastAudio = -10f;
        float m_LastNoise = -10f;
        float m_SpawnTime;
        SalvageItem m_Item;
        bool m_LookedForItem;

        /// <summary>Who last handled this object, so a noise can be attributed to a player.</summary>
        public PlayerId LastHandler { get; set; } = PlayerId.None;

        void OnEnable() => m_SpawnTime = Time.time;

        void OnCollisionEnter(Collision collision)
        {
            if (Time.time - m_SpawnTime < 1f) return;
            float speed = collision.relativeVelocity.magnitude;
            if (speed < audioSpeedThreshold) return;

            Vector3 point = collision.contactCount > 0 ? collision.GetContact(0).point : transform.position;

            // Delicate salvage is worth less every time it is banged into something.
            if (!m_LookedForItem) { m_Item = GetComponent<SalvageItem>(); m_LookedForItem = true; }
            if (m_Item != null) m_Item.ApplyImpact(speed);

            if (Time.time - m_LastAudio >= audioCooldown && Sfx.Library != null)
            {
                m_LastAudio = Time.time;
                float volume = Mathf.Clamp01(speed / 6f);
                Sfx.PlayAt(heavy ? Sfx.Library.impactHeavy : Sfx.Library.impactLight, point, volume, Random.Range(0.9f, 1.1f), 18f);
            }

            var director = MissionDirector.Active;
            if (director == null || director.noise == null) return;
            if (NoiseRules.TryEvaluateImpact(speed, Time.time - m_LastNoise, director.Config.noise, out float loudness))
            {
                m_LastNoise = Time.time;
                director.noise.Emit(new NoiseEvent(point, loudness * loudnessScale, NoiseKind.Impact, LastHandler));
            }
        }
    }
}

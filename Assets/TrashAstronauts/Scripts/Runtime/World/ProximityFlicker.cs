using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>
    /// Makes a ship light stutter when the creature is close. It is both atmosphere and a fair warning:
    /// the lights around you start to fail before it arrives.
    /// </summary>
    [RequireComponent(typeof(Light))]
    public sealed class ProximityFlicker : MonoBehaviour
    {
        public float radius = 7.5f;
        [Range(0f, 1f)] public float minBrightness = 0.08f;

        Light m_Light;
        float m_BaseIntensity;
        float m_NextChange;
        float m_Current = 1f;

        void Awake()
        {
            m_Light = GetComponent<Light>();
            m_BaseIntensity = m_Light.intensity;
        }

        void Update()
        {
            float threat = 0f;
            var director = MissionDirector.Active;
            if (director != null && director.enemies != null)
            {
                foreach (var enemy in director.enemies)
                {
                    if (enemy == null || enemy.State == EnemyState.Dormant) continue;
                    float distance = Vector3.Distance(enemy.transform.position, transform.position);
                    threat = Mathf.Max(threat, 1f - Mathf.Clamp01(distance / radius));
                }
            }

            if (threat <= 0f)
            {
                if (m_Current != 1f)
                {
                    m_Current = 1f;
                    m_Light.intensity = m_BaseIntensity;
                }
                return;
            }

            if (Time.time >= m_NextChange)
            {
                m_NextChange = Time.time + Random.Range(0.03f, 0.22f);
                // The closer it is, the more often the light drops out.
                m_Current = Random.value < threat * 0.75f ? Random.Range(minBrightness, 0.5f) : 1f;
                m_Light.intensity = m_BaseIntensity * m_Current;
            }
        }
    }
}

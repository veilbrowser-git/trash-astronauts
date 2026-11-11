using TrashAstronauts.Core;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// Procedural motion for the creature's body: limbs that hang and twitch on patrol, splay and reach
    /// when it hunts, a head that sweeps while it searches and locks on to the player when it sees them,
    /// and cables that trail behind. All of it is cheap transform rotation; there are no skinned meshes.
    /// </summary>
    public sealed class CreatureAnimator : MonoBehaviour
    {
        public EnemyDrone drone;
        public Transform body;
        public Transform head;
        public Transform[] shoulders;
        public Transform[] elbows;
        public Transform[] tendrils;
        public Transform jaw;

        Quaternion[] m_ShoulderRest;
        Quaternion[] m_ElbowRest;
        Quaternion[] m_TendrilRest;
        Quaternion m_HeadRest;
        Vector3 m_BodyRest;
        Vector3 m_LastPosition;
        float m_Aggression;
        float m_Awake;
        float m_Twitch;
        float m_NextTwitch;
        float m_Seed;

        void Awake()
        {
            m_Seed = Random.value * 100f;
            m_ShoulderRest = Capture(shoulders);
            m_ElbowRest = Capture(elbows);
            m_TendrilRest = Capture(tendrils);
            if (head != null) m_HeadRest = head.localRotation;
            if (body != null) m_BodyRest = body.localPosition;
            m_LastPosition = transform.position;
        }

        static Quaternion[] Capture(Transform[] transforms)
        {
            if (transforms == null) return new Quaternion[0];
            var result = new Quaternion[transforms.Length];
            for (int i = 0; i < transforms.Length; i++) result[i] = transforms[i] != null ? transforms[i].localRotation : Quaternion.identity;
            return result;
        }

        void LateUpdate()
        {
            if (drone == null) return;
            float dt = Time.deltaTime;
            float t = Time.time + m_Seed;
            EnemyState state = drone.State;

            float targetAggression = state == EnemyState.Chase ? 1f : state == EnemyState.Investigate || state == EnemyState.Search ? 0.45f : 0f;
            m_Aggression = Mathf.MoveTowards(m_Aggression, targetAggression, dt * 2.5f);
            m_Awake = Mathf.MoveTowards(m_Awake, state == EnemyState.Dormant ? 0f : 1f, dt * 0.8f);

            // Occasional sharp twitch, more often when agitated.
            if (t >= m_NextTwitch && m_Awake > 0.5f)
            {
                m_Twitch = 1f;
                m_NextTwitch = t + Random.Range(0.6f, 3.5f) / (1f + m_Aggression * 3f);
            }
            m_Twitch = Mathf.MoveTowards(m_Twitch, 0f, dt * 6f);

            Vector3 velocity = dt > 0f ? (transform.position - m_LastPosition) / dt : Vector3.zero;
            m_LastPosition = transform.position;
            float speed = velocity.magnitude;

            if (body != null)
            {
                float bob = Mathf.Sin(t * (1.1f + m_Aggression * 3f)) * 0.03f * m_Awake;
                Vector3 jitter = m_Aggression > 0.6f ? Random.insideUnitSphere * 0.006f : Vector3.zero;
                body.localPosition = m_BodyRest + new Vector3(0f, bob - (1f - m_Awake) * 0.12f, 0f) + jitter;
            }

            // Limbs: curled in while dormant, dangling on patrol, splayed forward when hunting.
            for (int i = 0; i < shoulders.Length; i++)
            {
                if (shoulders[i] == null) continue;
                float phase = i * 1.7f;
                float side = i % 2 == 0 ? -1f : 1f;
                float sway = Mathf.Sin(t * (0.9f + m_Aggression * 4f) + phase) * (10f + m_Aggression * 14f);
                float reach = Mathf.Lerp(0f, -62f, m_Aggression) + m_Twitch * 18f * Mathf.Sin(phase * 3f);
                float curl = (1f - m_Awake) * 55f;
                shoulders[i].localRotation = m_ShoulderRest[i] * Quaternion.Euler(reach + sway + curl, side * (m_Aggression * 22f + sway * 0.3f), side * sway * 0.4f);
            }
            for (int i = 0; i < elbows.Length; i++)
            {
                if (elbows[i] == null) continue;
                float phase = i * 2.3f + 1f;
                float flex = Mathf.Sin(t * (1.3f + m_Aggression * 5f) + phase) * (12f + m_Aggression * 16f);
                float curl = (1f - m_Awake) * 70f;
                elbows[i].localRotation = m_ElbowRest[i] * Quaternion.Euler(-flex - m_Aggression * 25f + curl + m_Twitch * 25f, 0f, 0f);
            }

            // Trailing cables lag behind movement and sway.
            for (int i = 0; i < tendrils.Length; i++)
            {
                if (tendrils[i] == null) continue;
                float phase = i * 0.9f;
                float drag = Mathf.Clamp(speed * 16f, 0f, 38f);
                float sway = Mathf.Sin(t * 1.6f + phase) * (7f + m_Aggression * 10f);
                tendrils[i].localRotation = m_TendrilRest[i] * Quaternion.Euler(drag + sway, sway * 0.6f, Mathf.Cos(t * 1.1f + phase) * 6f);
            }

            if (head != null)
            {
                Quaternion look = m_HeadRest;
                if (state == EnemyState.Search || state == EnemyState.Investigate)
                    look = m_HeadRest * Quaternion.Euler(Mathf.Sin(t * 1.7f) * 10f, Mathf.Sin(t * 0.9f) * 50f, Mathf.Sin(t * 2.3f) * 8f);
                else if (state == EnemyState.Chase)
                    look = m_HeadRest * Quaternion.Euler(Random.Range(-3f, 3f), Random.Range(-4f, 4f), Random.Range(-6f, 6f));
                else if (state == EnemyState.Patrol)
                    look = m_HeadRest * Quaternion.Euler(Mathf.Sin(t * 0.6f) * 6f, Mathf.Sin(t * 0.4f) * 22f, 0f);
                head.localRotation = Quaternion.Slerp(head.localRotation, look, 1f - Mathf.Exp(-8f * dt));
            }

            if (jaw != null)
            {
                float open = m_Aggression * 26f + m_Twitch * 10f + Mathf.Sin(t * 9f) * 4f * m_Aggression;
                jaw.localRotation = Quaternion.Euler(open, 0f, 0f);
            }
        }
    }
}

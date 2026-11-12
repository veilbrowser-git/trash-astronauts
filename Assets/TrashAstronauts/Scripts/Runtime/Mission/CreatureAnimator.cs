using TrashAstronauts.Core;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// Procedural motion for the creature's body: limbs that hang and twitch on patrol, splay and reach
    /// when it hunts, a head that sweeps while it searches and locks on to the player when it sees them,
    /// and cables that trail behind. All of it is cheap transform rotation; there are no skinned meshes.
    /// </summary>
    [DefaultExecutionOrder(50)] // after SurfaceWalker has put the body on its surface this frame
    public sealed partial class CreatureAnimator : MonoBehaviour
    {
        public EnemyDrone drone;
        public Transform body;
        public Transform head;
        public Transform[] shoulders;
        public Transform[] elbows;
        public Transform[] tendrils;
        public Transform jaw;
        [Header("Spider")]
        [Tooltip("Eight legs hinged at the body (shoulders) and knee (elbows), animated as a real spider's.")]
        public bool spider;
        public Transform abdomen;
        public Transform[] palps;
        [Tooltip("The tip of each leg (a child of its lower segment). With these the feet are planted on the surface by IK.")]
        public Transform[] feet;

        Vector3[] m_LegLiftAxis;
        float[] m_LegLiftSign;
        float[] m_LegSide;
        Quaternion[] m_PalpRest;
        Quaternion m_AbdomenRest = Quaternion.identity;
        Quaternion m_BodyRestRotation = Quaternion.identity;
        float m_Gait;

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
            if (spider) CaptureSpider();
            m_Captured = true;
        }

        void CaptureSpider()
        {
            int count = shoulders != null ? shoulders.Length : 0;
            m_LegLiftAxis = new Vector3[count];
            m_LegLiftSign = new float[count];
            m_LegSide = new float[count];
            for (int i = 0; i < count; i++)
            {
                // Which way the leg points, from the body joint to the knee.
                Vector3 outward = elbows != null && i < elbows.Length && elbows[i] != null ? elbows[i].localPosition : Vector3.right;
                outward.y = 0f;
                outward = outward.sqrMagnitude > 1e-6f ? outward.normalized : Vector3.right;
                m_LegSide[i] = outward.x >= 0f ? 1f : -1f;
                m_LegLiftAxis[i] = Vector3.Cross(Vector3.up, outward).normalized;
                // Work out which direction of turn about that axis raises the leg.
                m_LegLiftSign[i] = (Quaternion.AngleAxis(10f, m_LegLiftAxis[i]) * outward).y > 0f ? 1f : -1f;
            }
            m_PalpRest = Capture(palps);
            if (abdomen != null) m_AbdomenRest = abdomen.localRotation;
            if (body != null) m_BodyRestRotation = body.localRotation;
            CaptureFeet();
        }

        /// <summary>
        /// Legs step in the alternating pattern real spiders use (two sets of four), slowly while it
        /// wanders and in a blur when it hunts. Hunting, the front pairs rear up in a threat display, the
        /// fangs spread and the abdomen lifts. Dormant, it curls its legs under itself.
        /// </summary>
        void AnimateSpider(float t, float dt, float speed, EnemyState state, bool scaring)
        {
            float moving = Mathf.Clamp01(speed / 0.5f);
            float rate = Mathf.Lerp(1.5f, 12f, m_Aggression) * Mathf.Lerp(0.55f, 1f, moving);
            m_Gait += dt * rate * m_Awake;
            float swingAmp = Mathf.Lerp(7f, 24f, Mathf.Max(m_Aggression, moving * 0.5f));
            float liftAmp = Mathf.Lerp(5f, 20f, Mathf.Max(m_Aggression, moving * 0.4f));
            float curl = Mathf.Max(1f - m_Awake, OnThread ? 0.55f : 0f);

            int legs = shoulders != null ? shoulders.Length : 0;
            for (int i = 0; i < legs; i++)
            {
                if (shoulders[i] == null) continue;
                int row = i / 2;
                // Alternating sets: left 1, right 2, left 3, right 4 move together; the others half a cycle later.
                float phase = m_Gait + ((row + i % 2) % 2) * Mathf.PI + row * 0.35f;
                float swing = Mathf.Sin(phase) * swingAmp;
                float lift = Mathf.Max(0f, Mathf.Cos(phase)) * liftAmp;
                float forward = 0f;
                if (scaring)
                {
                    // In the player's face: every leg thrown wide and shaking, the front pairs reaching round their head.
                    swing = Mathf.Sin(t * 47f + i * 1.3f) * 9f;
                    lift = (row < 2 ? 22f : 10f) + Mathf.Sin(t * 53f + i * 2.1f) * 8f;
                    // The front two pairs reach forward round the player's head, clawing at the visor.
                    forward = row == 0 ? 46f + Mathf.Sin(t * 31f + i) * 10f : row == 1 ? 26f : 0f;
                }

                // Threat display: the first two pairs reach up and forward.
                float rear = row == 0 ? 34f : row == 1 ? 14f : row == 3 ? -6f : 0f;
                lift += rear * m_Aggression + m_Twitch * 10f * Mathf.Sin(i * 2.1f);
                if (!scaring) forward = (row == 0 ? 20f : row == 1 ? 8f : 0f) * m_Aggression;
                lift -= curl * 52f;

                Quaternion yaw = Quaternion.AngleAxis(swing - m_LegSide[i] * forward, Vector3.up);
                Quaternion raise = Quaternion.AngleAxis(lift * m_LegLiftSign[i], m_LegLiftAxis[i]);
                shoulders[i].localRotation = m_ShoulderRest[i] * yaw * raise;

                if (elbows != null && i < elbows.Length && elbows[i] != null)
                {
                    float flex = Mathf.Sin(phase + 0.7f) * (6f + liftAmp * 0.5f) - m_Aggression * (row == 0 ? 18f : 6f) - curl * 58f + m_Twitch * 8f;
                    elbows[i].localRotation = m_ElbowRest[i] * Quaternion.AngleAxis(flex * m_LegLiftSign[i], m_LegLiftAxis[i]);
                }
            }

            if (palps != null)
            {
                for (int i = 0; i < palps.Length; i++)
                {
                    if (palps[i] == null) continue;
                    float side = i % 2 == 0 ? -1f : 1f;
                    palps[i].localRotation = m_PalpRest[i] * Quaternion.Euler(
                        Mathf.Sin(t * 5.3f + i * 1.9f) * 10f * (0.4f + m_Aggression) - m_Aggression * 22f - curl * 30f,
                        side * Mathf.Sin(t * 3.1f + i) * 7f, 0f);
                }
            }

            if (abdomen != null)
            {
                float breath = 1f + Mathf.Sin(t * (1.4f + m_Aggression * 3f)) * 0.018f * m_Awake;
                abdomen.localScale = new Vector3(breath, breath, 1f);
                abdomen.localRotation = m_AbdomenRest * Quaternion.Euler(-m_Aggression * 16f - m_Twitch * 4f, Mathf.Sin(t * 0.9f) * 3f, 0f);
            }

            if (body != null)
            {
                // The whole animal turns to look, a little; a spider cannot turn its head.
                Vector3 look = Vector3.zero;
                if (state == EnemyState.Search || state == EnemyState.Investigate) look = new Vector3(Mathf.Sin(t * 1.7f) * 4f, Mathf.Sin(t * 0.9f) * 20f, Mathf.Sin(t * 2.3f) * 3f);
                else if (state == EnemyState.Patrol) look = new Vector3(Mathf.Sin(t * 0.6f) * 2f, Mathf.Sin(t * 0.4f) * 9f, 0f);
                else if (state == EnemyState.Chase) look = new Vector3(-10f + Random.Range(-1.5f, 1.5f), Random.Range(-2f, 2f), Random.Range(-2f, 2f));
                if (scaring) look = new Vector3(-24f, 0f, 0f);
                body.localRotation = Quaternion.Slerp(body.localRotation, m_BodyRestRotation * Quaternion.Euler(look), 1f - Mathf.Exp(-7f * dt));
            }

            if (jaw != null)
            {
                // Chelicerae swing forward and up to bare the fangs.
                float open = m_Aggression * 24f + m_Twitch * 8f + Mathf.Sin(t * 9f) * 3f * m_Aggression + (scaring ? 34f + Mathf.Sin(t * 38f) * 14f : 0f);
                jaw.localRotation = Quaternion.Euler(-open, 0f, 0f);
            }
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

            bool scaring = drone.IsScaring;
            float targetAggression = scaring ? 1f : state == EnemyState.Chase ? 1f : state == EnemyState.Investigate || state == EnemyState.Search ? 0.45f : 0f;
            m_Aggression = Mathf.MoveTowards(m_Aggression, targetAggression, dt * (scaring ? 20f : 2.5f));
            m_Awake = Mathf.MoveTowards(m_Awake, state == EnemyState.Dormant && !scaring ? 0f : 1f, dt * (scaring ? 20f : 0.8f));

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
                float bob = Mathf.Sin(t * (1.1f + m_Aggression * 3f)) * Mathf.Lerp(0.03f, 0.007f, m_IkWeight) * m_Awake;
                Vector3 jitter = m_Aggression > 0.6f ? Random.insideUnitSphere * 0.006f : Vector3.zero;
                body.localPosition = m_BodyRest + new Vector3(0f, bob - (1f - m_Awake) * 0.12f, 0f) + jitter;
            }

            if (spider)
            {
                AnimateSpider(t, dt, speed, state, scaring);
                PlantFeet(dt, state, scaring);
                return;
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
                float open = m_Aggression * 26f + m_Twitch * 10f + Mathf.Sin(t * 9f) * 4f * m_Aggression + (scaring ? 34f : 0f);
                jaw.localRotation = Quaternion.Euler(open, 0f, 0f);
            }
        }
    }
}

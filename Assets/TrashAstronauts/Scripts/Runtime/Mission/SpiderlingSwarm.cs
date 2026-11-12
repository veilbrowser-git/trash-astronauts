using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// A handful of small spiders wandering over a patch of wall around their egg sacs. Come close and
    /// they bolt back into the sacs with a dry skitter; leave them alone for a while and they creep out
    /// again. They are harmless. They are also a warning: where there are young, the mother is not far.
    /// The patch is this object's local XY plane, with -Z pointing out of the wall.
    /// </summary>
    public sealed class SpiderlingSwarm : MonoBehaviour
    {
        public Transform[] spiders;
        public float radius = 0.6f;
        public float alarmRadius = 2.3f;
        public float wanderSpeed = 0.16f;
        public float fleeSpeed = 1.8f;
        public float calmSeconds = 7f;
        public float spiderScale = 0.12f;

        Vector2[] m_Position;
        Vector2[] m_Direction;
        float[] m_Turn;
        float m_Hidden;
        float m_Calm;
        System.Random m_Random;

        public bool Alarmed { get; private set; }
        public int ScatterCount { get; private set; }
        /// <summary>0 = all out on the wall, 1 = all hidden in the sacs.</summary>
        public float Hidden => m_Hidden;

        void Awake()
        {
            int n = spiders != null ? spiders.Length : 0;
            m_Random = new System.Random(name.GetHashCode() ^ 0x51ed);
            m_Position = new Vector2[n];
            m_Direction = new Vector2[n];
            m_Turn = new float[n];
            for (int i = 0; i < n; i++)
            {
                float a = (float)m_Random.NextDouble() * Mathf.PI * 2f;
                m_Position[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius * (0.3f + 0.6f * (float)m_Random.NextDouble());
                m_Direction[i] = new Vector2(Mathf.Cos(a * 3.1f), Mathf.Sin(a * 3.1f));
            }
        }

        void Update()
        {
            if (spiders == null || m_Position == null) return;
            var director = MissionDirector.Active;
            float dt = Time.deltaTime;

            bool near = false;
            // An infested wreck: the young notice you from farther off and roam faster.
            float swarm = director != null ? TrashAstronauts.Core.ContractConditions.SwarmMultiplier(director.Condition) : 1f;
            dt *= swarm;
            if (director != null)
            {
                foreach (var player in director.Players)
                {
                    if (player != null && Vector3.Distance(player.HeadPosition, transform.position) < alarmRadius * swarm) near = true;
                }
            }
            if (near && !Alarmed)
            {
                ScatterCount++;
                if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.cutterSparks, transform.position, 0.3f, 2.1f, 7f);
            }
            Alarmed = near;
            m_Calm = near ? 0f : m_Calm + dt;
            float hideTarget = near ? 1f : m_Calm >= calmSeconds ? 0f : m_Hidden;
            m_Hidden = Mathf.MoveTowards(m_Hidden, hideTarget, dt * (near ? 2.2f : 0.5f));

            for (int i = 0; i < spiders.Length; i++)
            {
                if (spiders[i] == null) continue;
                if (near)
                {
                    // Bolt for the sacs in the middle.
                    Vector2 before = m_Position[i];
                    m_Position[i] = Vector2.MoveTowards(m_Position[i], Vector2.zero, fleeSpeed * dt);
                    Vector2 moved = m_Position[i] - before;
                    if (moved.sqrMagnitude > 1e-8f) m_Direction[i] = moved.normalized;
                }
                else if (m_Hidden < 0.95f)
                {
                    m_Turn[i] -= dt;
                    if (m_Turn[i] <= 0f)
                    {
                        m_Turn[i] = 0.4f + 1.6f * (float)m_Random.NextDouble();
                        float a = (float)m_Random.NextDouble() * Mathf.PI * 2f;
                        m_Direction[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    }
                    // They stop and start, the way small spiders do.
                    float go = m_Turn[i] > 0.5f ? 1f : 0f;
                    m_Position[i] += m_Direction[i] * (wanderSpeed * go * dt);
                    if (m_Position[i].magnitude > radius)
                    {
                        m_Position[i] = m_Position[i].normalized * radius;
                        m_Direction[i] = -m_Position[i].normalized;
                    }
                }

                var t = spiders[i];
                t.localPosition = new Vector3(m_Position[i].x, m_Position[i].y, -0.012f);
                Vector3 forward = new Vector3(m_Direction[i].x, m_Direction[i].y, 0f);
                if (forward.sqrMagnitude > 1e-6f) t.localRotation = Quaternion.LookRotation(forward, Vector3.back);
                t.localScale = Vector3.one * (spiderScale * (1f - m_Hidden));
                bool visible = m_Hidden < 0.98f;
                if (t.gameObject.activeSelf != visible) t.gameObject.SetActive(visible);
            }
        }
    }
}

using TrashAstronauts.Core;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// Planted feet for the spider. Each leg has a home point beside the body on the surface it walks on;
    /// a planted foot stays where it is in the world while the body moves over it, and when it has fallen
    /// too far behind its home (or the leg is stretched too far) the leg lifts, arcs and lands a little ahead
    /// of home along the direction of travel, on the real floor, wall or ceiling found by one raycast.
    /// The legs step in two alternating groups of four (the tetrapod gait real spiders use), so a leg's
    /// neighbours are never in the air with it. Each leg is solved with two-bone analytic IK (thigh and
    /// shin), the knee bending away from the surface as in the modelled rest pose.
    /// </summary>
    public sealed partial class CreatureAnimator
    {
        SurfaceWalker m_Walker;
        Transform m_Surface;
        bool m_Captured;
        bool m_IkReady;
        bool m_Replant = true;
        float m_IkWeight;
        Vector3[] m_HomeLocal;      // rest foot point on the surface plane, in the Surface's space
        Vector3[] m_PoleLocal;      // which way the knee bends, in the body's space
        Vector3[] m_Plant;          // where each foot is (world)
        Vector3[] m_StepFrom, m_StepTo;
        float[] m_StepT;            // 0..1 while the leg is in the air, 1 when planted
        int[] m_Group;
        Quaternion[] m_ProcShoulder, m_ProcElbow;
        float m_Reach = 1f;
        int m_ActiveGroup = -1;
        int m_LastGroup = 1;
        float m_StepDuration = 0.25f;
        Vector3 m_LastSurfacePosition;
        Vector3 m_SurfaceVelocity;
        int m_StepEvents, m_Raycasts;

        /// <summary>True when the legs have feet to plant (otherwise the old procedural swing is used).</summary>
        public bool HasFootIk => m_IkReady;
        /// <summary>How much the planted-foot IK drives the legs right now (0 = procedural pose, 1 = feet planted).</summary>
        public float FootIkWeight => m_IkWeight;
        public int LegCount => m_IkReady ? feet.Length : 0;
        public bool IsFootPlanted(int leg) => m_IkReady && m_StepT[leg] >= 1f;
        /// <summary>Which of the two alternating groups of four a leg steps with (0 or 1).</summary>
        public int LegGroup(int leg) => m_Group != null ? m_Group[leg] : 0;
        public Vector3 FootTip(int leg) => feet[leg].position;
        public Vector3 PlantedPoint(int leg) => m_Plant[leg];
        /// <summary>Steps started so far, and raycasts made for them (diagnostics for tests).</summary>
        public int StepEvents => m_StepEvents;
        /// <summary>A line about one leg's state, for test messages.</summary>
        public string DescribeLeg(int leg) => m_IkReady
            ? "step " + m_StepT[leg].ToString("0.00") + " group " + m_Group[leg] + "/active " + m_ActiveGroup + " plant-home " + Vector3.Distance(m_Plant[leg], HomeWorld(leg)).ToString("0.000")
              + " plant-shoulder " + Vector3.Distance(m_Plant[leg], shoulders[leg].position).ToString("0.000") + " reach " + m_Reach.ToString("0.000") + " speed " + m_SurfaceVelocity.magnitude.ToString("0.000") + " steps " + m_StepEvents + " w " + m_IkWeight.ToString("0.00")
            : "no IK";
        public int FootRaycasts => m_Raycasts;

        bool OnThread => m_Walker != null && m_Walker.Current == CrawlSurface.Thread;

        void EnsureCaptured()
        {
            if (!m_Captured) Awake();
        }

        void CaptureFeet()
        {
            m_IkReady = false;
            m_Walker = GetComponent<SurfaceWalker>();
            int count = shoulders != null ? shoulders.Length : 0;
            if (body == null || body.parent == null || count == 0 || feet == null || feet.Length != count || elbows == null || elbows.Length != count) return;
            for (int i = 0; i < count; i++)
                if (shoulders[i] == null || elbows[i] == null || feet[i] == null) return;

            m_Surface = body.parent;
            m_HomeLocal = new Vector3[count];
            m_PoleLocal = new Vector3[count];
            m_Plant = new Vector3[count];
            m_StepFrom = new Vector3[count];
            m_StepTo = new Vector3[count];
            m_StepT = new float[count];
            m_Group = new int[count];
            m_ProcShoulder = new Quaternion[count];
            m_ProcElbow = new Quaternion[count];
            float bodyHeight = m_Walker != null ? m_Walker.bodyHeight : 0f;
            float reach = 0f;
            for (int i = 0; i < count; i++)
            {
                Vector3 s = body.InverseTransformPoint(shoulders[i].position);
                Vector3 k = body.InverseTransformPoint(elbows[i].position);
                Vector3 f = body.InverseTransformPoint(feet[i].position);
                reach += (k - s).magnitude + (f - k).magnitude;
                // Home: where the foot rests in the modelled pose, dropped onto the plane the walker stands it on.
                Vector3 home = m_BodyRest + m_BodyRestRotation * f;
                if (bodyHeight > 0f) home.y = -bodyHeight;
                m_HomeLocal[i] = home;
                Vector3 axis = (f - s).normalized;
                Vector3 pole = (k - s) - axis * Vector3.Dot(k - s, axis);
                m_PoleLocal[i] = pole.sqrMagnitude > 1e-6f ? pole.normalized : Vector3.up;
                // Alternating tetrapod: left 1, right 2, left 3, right 4 together, the other four half a cycle later.
                m_Group[i] = ((i / 2) + i % 2) % 2;
                m_StepT[i] = 1f;
            }
            m_Reach = Mathf.Max(0.05f, reach / count);
            m_Replant = true;
            m_IkReady = true;
        }

        Vector3 HomeWorld(int leg) => m_Surface.TransformPoint(m_HomeLocal[leg]);

        /// <summary>
        /// Editor/preview helper: plants every foot at its home point on the surface under it (one raycast
        /// per leg) and solves the legs, without needing the game to run.
        /// </summary>
        public void PoseFeetOnSurface()
        {
            EnsureCaptured();
            if (!m_IkReady) return;
            Vector3 normal = m_Surface.up;
            for (int i = 0; i < feet.Length; i++)
            {
                shoulders[i].localRotation = m_ShoulderRest[i];
                elbows[i].localRotation = m_ElbowRest[i];
                m_Plant[i] = FindFoothold(HomeWorld(i), normal);
                m_StepT[i] = 1f;
                SolveLeg(i, m_Plant[i]);
            }
            m_IkWeight = 1f;
            m_Replant = false;
        }

        Vector3 FindFoothold(Vector3 point, Vector3 normal)
        {
            float height = m_Walker != null ? Mathf.Max(0.1f, m_Walker.bodyHeight) : m_Reach * 0.5f;
            m_Raycasts++;
            if (Physics.Raycast(point + normal * height * 1.2f, -normal, out RaycastHit hit, height * 2.6f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                return hit.point;
            return point; // nothing there: keep it on the body's own plane
        }

        void PlantFeet(float dt, EnemyState state, bool scaring)
        {
            if (!m_IkReady) return;
            Vector3 surfacePosition = m_Surface.position;
            if (dt > 0f)
            {
                Vector3 v = (surfacePosition - m_LastSurfacePosition) / dt;
                if (v.sqrMagnitude > 144f) { v = Vector3.zero; m_Replant = true; } // it was placed, it did not walk
                m_SurfaceVelocity = Vector3.Lerp(m_SurfaceVelocity, v, 1f - Mathf.Exp(-12f * dt));
            }
            m_LastSurfacePosition = surfacePosition;

            // IK takes over whenever it is standing on something; the scare, sleep and the thread have their own poses.
            float target = scaring || OnThread ? 0f : Mathf.Clamp01((m_Awake - 0.5f) * 2f);
            m_IkWeight = Mathf.MoveTowards(m_IkWeight, target, dt * (scaring ? 12f : 3f));
            if (m_IkWeight <= 0.001f)
            {
                m_Replant = true;
                return;
            }

            Vector3 normal = m_Surface.up;
            if (m_Replant)
            {
                for (int i = 0; i < feet.Length; i++)
                {
                    m_Plant[i] = HomeWorld(i);
                    m_StepT[i] = 1f;
                }
                m_ActiveGroup = -1;
                m_Replant = false;
            }

            UpdateGait(dt, normal);

            for (int i = 0; i < feet.Length; i++)
            {
                m_ProcShoulder[i] = shoulders[i].localRotation;
                m_ProcElbow[i] = elbows[i].localRotation;
                Vector3 foot = m_Plant[i];
                if (m_StepT[i] < 1f)
                {
                    float s = m_StepT[i];
                    float e = s * s * (3f - 2f * s);
                    foot = Vector3.Lerp(m_StepFrom[i], m_StepTo[i], e) + normal * (Mathf.Sin(s * Mathf.PI) * m_Reach * 0.16f);
                }
                shoulders[i].localRotation = m_ShoulderRest[i];
                elbows[i].localRotation = m_ElbowRest[i];
                SolveLeg(i, foot);
                if (m_IkWeight < 0.999f)
                {
                    shoulders[i].localRotation = Quaternion.Slerp(m_ProcShoulder[i], shoulders[i].localRotation, m_IkWeight);
                    elbows[i].localRotation = Quaternion.Slerp(m_ProcElbow[i], elbows[i].localRotation, m_IkWeight);
                }
            }
        }

        void UpdateGait(float dt, Vector3 normal)
        {
            Vector3 velocity = Vector3.ProjectOnPlane(m_SurfaceVelocity, normal);
            float speed = velocity.magnitude;

            if (m_ActiveGroup >= 0)
            {
                bool landed = true;
                for (int i = 0; i < feet.Length; i++)
                {
                    if (m_Group[i] != m_ActiveGroup || m_StepT[i] >= 1f) continue;
                    m_StepT[i] = Mathf.Min(1f, m_StepT[i] + dt / m_StepDuration);
                    if (m_StepT[i] >= 1f) m_Plant[i] = m_StepTo[i];
                    else landed = false;
                }
                if (!landed) return;
                m_LastGroup = m_ActiveGroup;
                m_ActiveGroup = -1;
            }

            // Faster body, quicker steps; the foot lands ahead of home by about the distance the body covers meanwhile.
            m_StepDuration = Mathf.Lerp(0.26f, 0.09f, Mathf.Clamp01(speed / 2.5f));
            Vector3 lead = Vector3.ClampMagnitude(velocity * m_StepDuration * 1.2f, m_Reach * 0.3f);
            float moving = Mathf.Clamp01(speed / 0.08f);
            float threshold = Mathf.Lerp(m_Reach * 0.07f, m_Reach * 0.2f, moving);

            float err0 = 0f, err1 = 0f;
            for (int i = 0; i < feet.Length; i++)
            {
                float err = Vector3.Distance(m_Plant[i], HomeWorld(i) + lead);
                // An over-stretched leg has to step whatever its distance from home.
                if (Vector3.Distance(m_Plant[i], shoulders[i].position) > m_Reach * 0.97f) err += m_Reach;
                if (m_Group[i] == 0) err0 = Mathf.Max(err0, err);
                else err1 = Mathf.Max(err1, err);
            }
            int group = -1;
            bool want0 = err0 > threshold, want1 = err1 > threshold;
            if (want0 && want1) group = 1 - m_LastGroup;
            else if (want0) group = 0;
            else if (want1) group = 1;
            if (group < 0) return;

            float minimum = moving > 0.5f ? 0f : m_Reach * 0.03f;
            bool any = false;
            for (int i = 0; i < feet.Length; i++)
            {
                if (m_Group[i] != group) continue;
                Vector3 desired = HomeWorld(i) + lead;
                if (Vector3.Distance(m_Plant[i], desired) <= minimum) continue;
                m_StepFrom[i] = m_Plant[i];
                m_StepTo[i] = FindFoothold(desired, m_Walker != null ? m_Walker.SurfaceNormal : normal);
                m_StepT[i] = 0f;
                any = true;
            }
            if (!any) return;
            m_ActiveGroup = group;
            m_StepEvents++;
        }

        /// <summary>
        /// Two-bone IK from the leg's rest pose: thigh from the body joint to the knee, shin from the knee to
        /// the tip. The knee goes on the side the modelled leg bends to (up, away from the surface). A target
        /// out of reach leaves the leg straight towards it.
        /// </summary>
        void SolveLeg(int i, Vector3 target)
        {
            Transform hip = shoulders[i], knee = elbows[i], tip = feet[i];
            Vector3 s = hip.position, k0 = knee.position, f0 = tip.position;
            float l1 = (k0 - s).magnitude, l2 = (f0 - k0).magnitude;
            if (l1 < 1e-4f || l2 < 1e-4f) return;

            Vector3 toTarget = target - s;
            float d = toTarget.magnitude;
            Vector3 dir = d > 1e-4f ? toTarget / d : body.TransformDirection(Vector3.down);
            d = Mathf.Clamp(d, Mathf.Abs(l1 - l2) + 1e-3f, (l1 + l2) * 0.999f);
            float a = (l1 * l1 + d * d - l2 * l2) / (2f * d);
            float h = Mathf.Sqrt(Mathf.Max(0f, l1 * l1 - a * a));

            Vector3 pole = body.TransformDirection(m_PoleLocal[i]) + m_Surface.up * 0.6f;
            Vector3 bend = pole - dir * Vector3.Dot(pole, dir);
            if (bend.sqrMagnitude < 1e-6f) bend = Vector3.ProjectOnPlane(m_Surface.up, dir);
            if (bend.sqrMagnitude < 1e-6f) return;
            bend.Normalize();
            Vector3 kneeTarget = s + dir * a + bend * h;

            // Thigh: line up the rest leg's plane and knee direction with the solved ones.
            Vector3 restDir = (f0 - s).normalized;
            Vector3 restBend = Vector3.ProjectOnPlane(k0 - s, restDir);
            if (restBend.sqrMagnitude < 1e-8f) return;
            Vector3 restNormal = Vector3.Cross(restDir, restBend);
            Vector3 newNormal = Vector3.Cross(dir, bend);
            Quaternion swing = Quaternion.LookRotation(kneeTarget - s, newNormal) * Quaternion.Inverse(Quaternion.LookRotation(k0 - s, restNormal));
            hip.rotation = swing * hip.rotation;

            // Shin: turn about the knee so the tip lands on the (reachable) target.
            Vector3 k1 = knee.position;
            Vector3 reachable = s + dir * d;
            knee.rotation = Quaternion.FromToRotation(tip.position - k1, reachable - k1) * knee.rotation;
        }
    }
}

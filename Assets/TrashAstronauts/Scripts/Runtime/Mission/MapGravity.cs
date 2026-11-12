using System.Collections.Generic;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// Weak gravity inside one map's volume (the lunar lava tube: 1.62 m/s^2). Everywhere else (the tug,
    /// the airlock, the other wrecks) stays weightless: GravityAt returns zero outside every active volume.
    ///
    /// Loose rigidbodies inside the volume (salvage that is not mounted, held or secured; floating junk;
    /// cut-off panel plates) are accelerated downward with a low terminal speed and come to rest on the
    /// floor. Kinematic bodies (mounted salvage, doors, the locker) and the creature are left alone.
    /// The player is handled by ZeroGLocomotion through StepPlayer, so the camera is never moved directly.
    /// </summary>
    public sealed class MapGravity : MonoBehaviour
    {
        [Tooltip("Acceleration inside the volume (m/s^2).")]
        public Vector3 gravity = new Vector3(0f, -1.62f, 0f);
        [Tooltip("Centre and size of the volume, in this object's space.")]
        public Vector3 center;
        public Vector3 size = new Vector3(10f, 10f, 10f);
        [Tooltip("Loose objects never fall faster than this (m/s).")]
        public float maxFallSpeed = 2.2f;
        [Tooltip("How quickly something lying on the floor stops sliding and rolling (1/s).")]
        public float groundFriction = 3f;

        /// <summary>The player never falls faster than this (m/s): no sudden drops.</summary>
        public const float PlayerMaxFallSpeed = 1.1f;
        /// <summary>How quickly the player's drift dies while standing on the ground (1/s).</summary>
        public const float PlayerGroundFriction = 3f;

        static readonly List<MapGravity> s_Active = new List<MapGravity>();
        readonly List<Rigidbody> m_Bodies = new List<Rigidbody>();
        readonly List<float> m_HalfHeights = new List<float>();
        float m_NextRefresh;

        public static IReadOnlyList<MapGravity> Active => s_Active;

        void OnEnable()
        {
            if (!s_Active.Contains(this)) s_Active.Add(this);
            m_NextRefresh = 0f;
        }

        void OnDisable() => s_Active.Remove(this);

        public bool Contains(Vector3 worldPoint)
        {
            Vector3 p = transform.InverseTransformPoint(worldPoint) - center;
            Vector3 h = size * 0.5f;
            return Mathf.Abs(p.x) <= h.x && Mathf.Abs(p.y) <= h.y && Mathf.Abs(p.z) <= h.z;
        }

        /// <summary>The gravity at a point: zero (weightless) unless it is inside an active map's volume.</summary>
        public static Vector3 GravityAt(Vector3 worldPoint)
        {
            for (int i = 0; i < s_Active.Count; i++)
            {
                var volume = s_Active[i];
                if (volume != null && volume.Contains(worldPoint)) return volume.gravity;
            }
            return Vector3.zero;
        }

        /// <summary>
        /// One frame of the player's drift under gravity <paramref name="g"/>, with the feet at
        /// <paramref name="feet"/>. Standing on the ground: no downward speed, and the drift dies away.
        /// In the air: falls, never faster than PlayerMaxFallSpeed. Returns true while airborne.
        /// </summary>
        public static bool StepPlayer(ref Vector3 velocity, Vector3 feet, Vector3 g, float dt)
        {
            Vector3 down = g.normalized;
            float into = Vector3.Dot(velocity, down);
            if (into >= -0.05f && IsGrounded(feet, down))
            {
                if (into > 0f) velocity -= down * into;
                velocity *= Mathf.Exp(-PlayerGroundFriction * dt);
                return false;
            }
            velocity += g * dt;
            into = Vector3.Dot(velocity, down);
            if (into > PlayerMaxFallSpeed) velocity -= down * (into - PlayerMaxFallSpeed);
            return true;
        }

        /// <summary>Is there ground just below the player's feet?</summary>
        public static bool IsGrounded(Vector3 feet, Vector3 down)
        {
            const float radius = 0.15f, start = 0.3f;
            return Physics.SphereCast(feet - down * start, radius, down, out _, start - radius + 0.08f,
                GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore);
        }

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            if (Time.time >= m_NextRefresh) Refresh();
            Vector3 down = gravity.normalized;
            for (int i = 0; i < m_Bodies.Count; i++)
            {
                var body = m_Bodies[i];
                if (body == null || body.isKinematic || !body.gameObject.activeInHierarchy) continue;
                Vector3 p = body.worldCenterOfMass;
                if (!Contains(p)) continue;

                bool grounded = Physics.Raycast(p, down, m_HalfHeights[i] + 0.08f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore);
                Vector3 v = body.linearVelocity;
                float into = Vector3.Dot(v, down);
                if (body.IsSleeping())
                {
                    if (grounded) continue; // resting on the floor: let it sleep
                    body.WakeUp();
                }
                v += gravity * dt;
                into = Vector3.Dot(v, down);
                if (into > maxFallSpeed) v -= down * (into - maxFallSpeed);
                if (grounded && Mathf.Abs(into) < 0.6f)
                {
                    // On the floor: stop sliding and rolling, so it comes to rest instead of skating about.
                    Vector3 along = v - down * into;
                    float keep = Mathf.Exp(-groundFriction * dt);
                    v = along * keep + down * into;
                    body.angularVelocity *= keep;
                }
                body.linearVelocity = v;
            }
        }

        /// <summary>Finds the loose bodies (twice a second, so newly spawned items and cut plates are picked up).</summary>
        void Refresh()
        {
            m_NextRefresh = Time.time + 0.5f;
            m_Bodies.Clear();
            m_HalfHeights.Clear();
            foreach (var body in FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
            {
                int layer = body.gameObject.layer;
                if (layer == GameLayers.Player || layer == GameLayers.Hand || layer == GameLayers.Enemy) continue;
                if (body.GetComponentInParent<EnemyDrone>() != null) continue;
                if (!Contains(body.position)) continue;
                var col = body.GetComponentInChildren<Collider>();
                m_Bodies.Add(body);
                m_HalfHeights.Add(col != null ? Mathf.Max(0.03f, col.bounds.extents.y) : 0.1f);
            }
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.6f, 0.8f, 1f, 0.4f);
            Gizmos.DrawWireCube(center, size);
        }
    }
}

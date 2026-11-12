using TrashAstronauts.Core;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    public enum CrawlSurface { Floor, Wall, Ceiling, Thread }

    /// <summary>
    /// Puts the spider on a surface instead of leaving it floating. The creature's own transform is still
    /// the point it navigates with (mid-air, along its route); this moves its body from that point down
    /// onto the floor, out onto the nearest wall and up it, or across to the ceiling above, and turns it so
    /// its feet are on whatever it is walking on. It changes its mind as it wanders, lurks on the ceiling
    /// while dormant, and comes down to the floor to run someone down. Where there is no wall near enough
    /// to climb, it lowers itself on a thread instead.
    /// </summary>
    public sealed class SurfaceWalker : MonoBehaviour
    {
        public EnemyDrone drone;
        [Tooltip("Parent of the animated body. This is what gets moved onto the surface.")]
        public Transform surface;
        [Tooltip("How far the body's centre rides above whatever it is standing on.")]
        public float bodyHeight = 0.27f;
        public float wallSearch = 4.5f;
        [Tooltip("How fast it moves between floor, wall and ceiling (m/s along the surface).")]
        public float climbSpeed = 1.5f;
        public float followRate = 9f;
        [Tooltip("Seconds it stays on one surface before picking another, while wandering.")]
        public Vector2 dwell = new Vector2(9f, 20f);
        public LineRenderer thread;

        /// <summary>0 = on the floor under its route, 1 = on the ceiling above it, in between = on the way (up the wall, or on its thread).</summary>
        public float Climb { get; private set; }
        public float TargetClimb { get; private set; }
        public CrawlSurface Current { get; private set; }
        public Vector3 SurfaceNormal { get; private set; } = Vector3.up;
        /// <summary>Test seam: pins where it wants to be (0 floor, 1 ceiling). Null lets it decide.</summary>
        public float? ClimbOverride { get; set; }

        float m_NextChoice;
        float m_Side = 1f;
        bool m_HasWall;
        bool m_Snap = true;
        Vector3 m_LastNav;
        readonly System.Random m_Random = new System.Random(8123);

        void OnEnable() => m_Snap = true;

        float Range(float a, float b) => Mathf.Lerp(a, b, (float)m_Random.NextDouble());

        void LateUpdate()
        {
            if (drone == null || surface == null) return;
            Vector3 nav = drone.transform.position;
            if ((nav - m_LastNav).sqrMagnitude > 9f) m_Snap = true; // it was moved (new run, new wreck)
            m_LastNav = nav;
            float dt = Time.deltaTime;

            if (drone.IsScaring)
            {
                // The jumpscare places the creature itself; the body simply goes with it.
                surface.SetPositionAndRotation(nav, drone.transform.rotation);
                if (thread != null) thread.enabled = false;
                Current = CrawlSurface.Floor;
                m_Snap = true;
                return;
            }

            Vector3 heading = drone.transform.forward;
            heading.y = 0f;
            heading = heading.sqrMagnitude > 1e-4f ? heading.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, heading);
            int mask = GameLayers.EnvironmentMask;

            float floorY = nav.y - 1.4f, ceilY = nav.y + 1.3f;
            if (Physics.Raycast(nav, Vector3.down, out RaycastHit down, 5f, mask, QueryTriggerInteraction.Ignore)) floorY = down.point.y;
            if (Physics.Raycast(nav, Vector3.up, out RaycastHit up, 5f, mask, QueryTriggerInteraction.Ignore)) ceilY = up.point.y;
            float room = Mathf.Max(0.6f, ceilY - floorY);

            // The nearest side wall, with some stickiness so it does not flip from one side to the other.
            bool wallA = Physics.Raycast(nav, right, out RaycastHit a, wallSearch, mask, QueryTriggerInteraction.Ignore);
            bool wallB = Physics.Raycast(nav, -right, out RaycastHit b, wallSearch, mask, QueryTriggerInteraction.Ignore);
            bool onSurfaceEnd = Climb < 0.02f || Climb > 0.98f;
            if (onSurfaceEnd || !m_HasWall)
            {
                if (wallA && wallB) m_Side = (m_Side > 0f ? a.distance <= b.distance + 0.8f : a.distance + 0.8f < b.distance) ? 1f : -1f;
                else if (wallA) m_Side = 1f;
                else if (wallB) m_Side = -1f;
            }
            RaycastHit wall = m_Side > 0f ? a : b;
            m_HasWall = m_Side > 0f ? wallA : wallB;
            float d = m_HasWall ? Mathf.Max(0.05f, wall.distance) : 0f;
            Vector3 side = right * m_Side;

            // Where does it want to be?
            if (ClimbOverride.HasValue) TargetClimb = Mathf.Clamp01(ClimbOverride.Value);
            else
            {
                switch (drone.State)
                {
                    case EnemyState.Dormant: TargetClimb = 1f; break;          // lurking overhead
                    case EnemyState.Chase: TargetClimb = 0f; break;            // it runs you down on the ground
                    case EnemyState.Investigate:
                    case EnemyState.Search: TargetClimb = TargetClimb > 0.5f ? 1f : 0f; break;
                    default:
                        if (Time.time >= m_NextChoice)
                        {
                            m_NextChoice = Time.time + Range(dwell.x, dwell.y);
                            double r = m_Random.NextDouble();
                            TargetClimb = r < 0.45 ? 0f : r < 0.85 ? 1f : (m_HasWall ? 0.5f : 1f);
                        }
                        break;
                }
            }

            float length = m_HasWall ? 2f * d + room : room;
            float speed = climbSpeed * (drone.State == EnemyState.Chase ? 3f : 1f);
            Climb = m_Snap ? TargetClimb : Mathf.MoveTowards(Climb, TargetClimb, speed * dt / Mathf.Max(0.5f, length));

            // Where is that on the surfaces around it?
            Vector3 floorPoint = new Vector3(nav.x, floorY, nav.z), ceilPoint = new Vector3(nav.x, ceilY, nav.z);
            Vector3 position, normal;
            if (m_HasWall)
            {
                float s = Climb * length;
                Vector3 wallNormal = wall.normal;
                wallNormal.y = 0f;
                wallNormal = wallNormal.sqrMagnitude > 1e-4f ? wallNormal.normalized : -side;
                const float corner = 0.35f;
                if (s <= d)
                {
                    position = floorPoint + side * s;
                    normal = Vector3.Slerp(Vector3.up, wallNormal, 0.5f * Mathf.InverseLerp(d - corner, d, s));
                    Current = CrawlSurface.Floor;
                }
                else if (s <= d + room)
                {
                    float h = s - d;
                    position = floorPoint + side * d + Vector3.up * h;
                    float low = Mathf.Clamp01(h / corner), high = Mathf.Clamp01((room - h) / corner);
                    normal = wallNormal;
                    if (low < 1f) normal = Vector3.Slerp(Vector3.Slerp(Vector3.up, wallNormal, 0.5f), wallNormal, low);
                    else if (high < 1f) normal = Vector3.Slerp(Vector3.Slerp(Vector3.down, wallNormal, 0.5f), wallNormal, high);
                    Current = CrawlSurface.Wall;
                }
                else
                {
                    float back = s - d - room;
                    position = ceilPoint + side * (d - back);
                    normal = Vector3.Slerp(Vector3.down, wallNormal, 0.5f * (1f - Mathf.Clamp01(back / corner)));
                    Current = CrawlSurface.Ceiling;
                }
            }
            else
            {
                // No wall to climb: it lets itself down, or hauls itself up, on a thread.
                position = Vector3.Lerp(floorPoint, ceilPoint, Climb);
                normal = Climb > 0.98f ? Vector3.down : Vector3.up;
                Current = Climb < 0.02f ? CrawlSurface.Floor : Climb > 0.98f ? CrawlSurface.Ceiling : CrawlSurface.Thread;
            }
            normal = normal.sqrMagnitude > 1e-4f ? normal.normalized : Vector3.up;
            position += normal * bodyHeight;
            if (Current == CrawlSurface.Thread) position = Vector3.Lerp(floorPoint + Vector3.up * bodyHeight, ceilPoint + Vector3.down * bodyHeight, Climb);

            Vector3 forward = Vector3.ProjectOnPlane(heading, normal);
            if (forward.sqrMagnitude < 1e-3f) forward = Vector3.ProjectOnPlane(Vector3.up, normal);
            Quaternion rotation = Quaternion.LookRotation(forward.normalized, normal);

            if (m_Snap)
            {
                surface.SetPositionAndRotation(position, rotation);
                m_Snap = false;
            }
            else
            {
                float k = 1f - Mathf.Exp(-followRate * dt);
                surface.SetPositionAndRotation(Vector3.Lerp(surface.position, position, k), Quaternion.Slerp(surface.rotation, rotation, k));
            }
            SurfaceNormal = normal;

            if (thread != null)
            {
                bool hanging = Current == CrawlSurface.Thread;
                thread.enabled = hanging;
                if (hanging)
                {
                    thread.positionCount = 2;
                    thread.SetPosition(0, ceilPoint);
                    thread.SetPosition(1, surface.position);
                }
            }
        }
    }
}

using TrashAstronauts.Mission;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// A one-way pusher that follows part of the player: the chest, or an open hand. Each physics step it
    /// looks for loose salvage it is overlapping and, if it is moving into it, gives it that motion (plus
    /// a little bounce). So the player can shoulder floating junk aside and swat things with an open
    /// hand, and nothing can ever push the player back, because nothing here is a physical body.
    /// A hand only counts for a deliberate swing; reaching slowly to pick something up does not knock it away.
    /// </summary>
    public sealed class Bumper : MonoBehaviour
    {
        [Tooltip("What to follow.")]
        public Transform target;
        public Vector3 localOffset;
        public float radius = 0.06f;
        [Tooltip("0 = a sphere. Otherwise an upright capsule this tall.")]
        public float height;
        [Tooltip("For a hand: only solid while not holding or gripping and moving faster than this (m/s). 0 = always solid (the body).")]
        public float minSpeed;
        public HandRig hand;
        [Range(0f, 1f)] public float bounce = 0.3f;

        Vector3 m_Last;
        bool m_HasLast;
        float m_Speed;
        static readonly Collider[] s_Hits = new Collider[16];

        public bool IsSolid { get; private set; }
        public Vector3 Velocity { get; private set; }
        public int PushCount { get; private set; }

        void OnEnable() => m_HasLast = false;

        void FixedUpdate()
        {
            if (target == null) return;
            Vector3 goal = target.TransformPoint(localOffset);
            float dt = Time.fixedDeltaTime;
            Vector3 step = m_HasLast ? goal - m_Last : Vector3.zero;
            m_Last = goal;
            m_HasLast = true;
            transform.position = goal;

            // A teleport is not a shove.
            if (step.magnitude > 1.5f || dt <= 0f)
            {
                m_Speed = 0f;
                Velocity = Vector3.zero;
                IsSolid = minSpeed <= 0f;
                return;
            }
            Velocity = step / dt;
            m_Speed = Mathf.Lerp(m_Speed, Velocity.magnitude, 0.5f);
            bool busy = hand != null && (hand.IsHolding || hand.Grip > 0.3f || hand.IsAnchored);
            IsSolid = minSpeed <= 0f || (!busy && m_Speed >= minSpeed);
            if (!IsSolid || Velocity.sqrMagnitude < 0.01f) return;

            int count;
            if (height > radius * 2f)
            {
                Vector3 half = Vector3.up * (height * 0.5f - radius);
                count = Physics.OverlapCapsuleNonAlloc(goal + half, goal - half, radius, s_Hits, GameLayers.SalvageMask, QueryTriggerInteraction.Ignore);
            }
            else count = Physics.OverlapSphereNonAlloc(goal, radius, s_Hits, GameLayers.SalvageMask, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                var body = s_Hits[i] != null ? s_Hits[i].attachedRigidbody : null;
                if (body == null || body.isKinematic) continue;
                var item = body.GetComponent<SalvageItem>();
                if (item != null && (item.IsHeld || item.IsSecured || item.StoredIn != null)) continue;

                // Push along the line from the pusher to the thing, but only if we are closing on it.
                Vector3 axisPoint = goal;
                if (height > radius * 2f) axisPoint.y = Mathf.Clamp(body.worldCenterOfMass.y, goal.y - height * 0.5f + radius, goal.y + height * 0.5f - radius);
                Vector3 normal = body.worldCenterOfMass - axisPoint;
                normal = normal.sqrMagnitude > 1e-6f ? normal.normalized : Velocity.normalized;
                float closing = Vector3.Dot(Velocity, normal) - Vector3.Dot(body.linearVelocity, normal);
                if (closing <= 0.02f) continue;
                body.WakeUp();
                body.AddForce(normal * (closing * (1f + bounce)), ForceMode.VelocityChange);
                PushCount++;
            }
        }
    }
}

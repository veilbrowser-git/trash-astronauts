using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// On the host, a handed-back flying item starts where it was drawn with a little extra velocity, so that it
    /// arrives on its real path (where the thrower's pose said, advanced by the trip here) after
    /// <see cref="CoopItemHost.CatchUpSeconds"/>; then the extra is taken off again. Its path only ever moves
    /// forward. If it hits something meanwhile, physics owns the velocity and the extra is simply forgotten.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HandbackCatchUp : MonoBehaviour
    {
        Rigidbody m_Body;
        Vector3 m_Extra;
        Vector3 m_Expected;
        float m_Until;

        public static void Begin(Rigidbody body, Vector3 extra, float seconds)
        {
            if (body == null) return;
            var c = body.GetComponent<HandbackCatchUp>();
            if (c == null) c = body.gameObject.AddComponent<HandbackCatchUp>();
            c.m_Body = body;
            c.m_Extra = extra;
            c.m_Expected = body.linearVelocity;
            c.m_Until = Time.time + seconds;
            c.enabled = true;
        }

        /// <summary>True while an item is still catching up.</summary>
        public bool Active => enabled && m_Body != null;

        void FixedUpdate()
        {
            if (m_Body == null || m_Body.isKinematic) { enabled = false; return; }
            // Something knocked it (a wall, a hand): a sudden change in one step. Its flight is physics' business now.
            // (Lunar gravity and drag bend the path a little every step on their own; that is not a knock.)
            Vector3 velocity = m_Body.linearVelocity;
            Vector3 step = velocity - m_Expected - TrashAstronauts.Mission.MapGravity.GravityAt(m_Body.position) * Time.fixedDeltaTime;
            m_Expected = velocity;
            if (step.sqrMagnitude > 0.04f) { enabled = false; return; }
            if (Time.time < m_Until) return;
            m_Body.linearVelocity -= m_Extra;
            enabled = false;
        }
    }
}

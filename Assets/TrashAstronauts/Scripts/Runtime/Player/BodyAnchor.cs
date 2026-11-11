using TrashAstronauts.Core;
using UnityEngine;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// A point on the player's torso that follows the head position but only lazily follows head yaw,
    /// so holsters stay put while the player looks around. Works seated or standing because it is
    /// measured down from the head, not up from the floor.
    /// </summary>
    public sealed class BodyAnchor : MonoBehaviour
    {
        public Transform head;
        [Tooltip("How far below the eyes the chest anchor sits.")]
        public float chestDrop = 0.38f;
        [Tooltip("Body turns to follow the head once they differ by more than this.")]
        public float yawDeadzone = 30f;
        public float yawFollowSpeed = 220f;

        [Header("Holster sockets")]
        public Transform chestSocket;
        public Transform dominantHipSocket;
        public Transform offHipSocket;
        public Vector3 chestOffset = new Vector3(0f, 0.02f, 0.17f);
        public Vector3 hipOffset = new Vector3(0.23f, -0.24f, 0.08f);

        float m_Yaw;
        bool m_Initialized;

        public void ApplyHandedness(Handedness handedness)
        {
            float sign = handedness == Handedness.Left ? -1f : 1f;
            if (chestSocket != null) chestSocket.localPosition = chestOffset;
            if (dominantHipSocket != null) dominantHipSocket.localPosition = new Vector3(hipOffset.x * sign, hipOffset.y, hipOffset.z);
            if (offHipSocket != null) offHipSocket.localPosition = new Vector3(-hipOffset.x * sign, hipOffset.y, hipOffset.z);
        }

        void LateUpdate()
        {
            if (head == null) return;

            Vector3 flatForward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            float headYaw = flatForward.sqrMagnitude > 0.0001f
                ? Mathf.Atan2(flatForward.x, flatForward.z) * Mathf.Rad2Deg
                : m_Yaw;

            if (!m_Initialized)
            {
                m_Yaw = headYaw;
                m_Initialized = true;
            }

            float delta = Mathf.DeltaAngle(m_Yaw, headYaw);
            if (Mathf.Abs(delta) > yawDeadzone)
            {
                float target = headYaw - Mathf.Sign(delta) * yawDeadzone;
                m_Yaw = Mathf.MoveTowardsAngle(m_Yaw, target, yawFollowSpeed * Time.unscaledDeltaTime);
            }

            transform.SetPositionAndRotation(head.position + Vector3.down * chestDrop, Quaternion.Euler(0f, m_Yaw, 0f));
        }

        /// <summary>Snaps the body to face where the head faces. Called after teleports and turns.</summary>
        public void SnapToHead()
        {
            m_Initialized = false;
        }
    }
}

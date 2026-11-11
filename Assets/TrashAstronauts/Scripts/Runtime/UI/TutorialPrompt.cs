using TMPro;
using UnityEngine;

namespace TrashAstronauts.UI
{
    /// <summary>
    /// A small instruction panel that floats in front of the player and follows them lazily, so the
    /// current tutorial step is readable wherever they drift. It eases toward its spot instead of being
    /// locked to the head, which is more comfortable to read.
    /// </summary>
    public sealed class TutorialPrompt : MonoBehaviour
    {
        public Transform head;
        public TMP_Text title;
        public TMP_Text body;
        public GameObject visuals;
        public float distance = 1.05f;
        public float drop = 0.42f;
        public float followSpeed = 2.2f;
        [Tooltip("The panel only re-centres once the player has looked this far away from it.")]
        public float recenterAngle = 28f;

        Vector3 m_TargetDirection = Vector3.forward;
        bool m_Placed;

        public void SetText(string heading, string instruction)
        {
            if (title != null) title.text = heading;
            if (body != null) body.text = instruction;
        }

        public void SetVisible(bool visible)
        {
            if (visuals != null && visuals.activeSelf != visible)
            {
                visuals.SetActive(visible);
                m_Placed = false;
            }
        }

        void LateUpdate()
        {
            if (head == null || visuals == null || !visuals.activeSelf) return;

            Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (flat.sqrMagnitude < 0.001f) flat = m_TargetDirection;
            flat.Normalize();
            if (!m_Placed || Vector3.Angle(flat, m_TargetDirection) > recenterAngle) m_TargetDirection = flat;

            Vector3 target = head.position + m_TargetDirection * distance + Vector3.down * drop;
            if (!m_Placed)
            {
                transform.position = target;
                m_Placed = true;
            }
            else
            {
                transform.position = Vector3.Lerp(transform.position, target, 1f - Mathf.Exp(-followSpeed * Time.unscaledDeltaTime));
            }

            Vector3 away = transform.position - head.position;
            if (away.sqrMagnitude > 0.0001f) transform.rotation = Quaternion.LookRotation(away, Vector3.up);
        }
    }
}

using TMPro;
using UnityEngine;

namespace TrashAstronauts.UI
{
    /// <summary>Floating name-and-value label that appears over salvage when it is scanned or picked up.</summary>
    public sealed class ScanTag : MonoBehaviour
    {
        public TMP_Text text;
        public float heightOffset = 0.22f;

        Transform m_Target;
        Transform m_Camera;
        float m_HideAt;

        void Awake()
        {
            m_Target = transform.parent;
            gameObject.SetActive(false);
        }

        public void Show(string displayName, int value, float seconds)
        {
            if (text != null) text.text = displayName + "\n" + value + " cr";
            m_HideAt = Mathf.Max(m_HideAt, Time.time + seconds);
            gameObject.SetActive(true);
            Face();
        }

        public void Hide()
        {
            m_HideAt = 0f;
            gameObject.SetActive(false);
        }

        void LateUpdate()
        {
            if (Time.time >= m_HideAt)
            {
                gameObject.SetActive(false);
                return;
            }
            Face();
        }

        void Face()
        {
            if (m_Target == null) m_Target = transform.parent;
            if (m_Camera == null && Camera.main != null) m_Camera = Camera.main.transform;
            if (m_Target != null) transform.position = m_Target.position + Vector3.up * heightOffset;
            if (m_Camera == null) return;
            Vector3 away = transform.position - m_Camera.position;
            if (away.sqrMagnitude > 0.0001f) transform.rotation = Quaternion.LookRotation(away, Vector3.up);
        }
    }
}

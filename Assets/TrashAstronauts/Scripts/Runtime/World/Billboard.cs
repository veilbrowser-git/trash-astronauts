using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>Keeps a small label turned toward the player's view.</summary>
    public sealed class Billboard : MonoBehaviour
    {
        Transform m_Camera;

        void LateUpdate()
        {
            if (m_Camera == null)
            {
                if (Camera.main == null) return;
                m_Camera = Camera.main.transform;
            }
            Vector3 away = transform.position - m_Camera.position;
            if (away.sqrMagnitude > 0.0001f) transform.rotation = Quaternion.LookRotation(away, Vector3.up);
        }
    }
}

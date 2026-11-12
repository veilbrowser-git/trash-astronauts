using UnityEngine;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// Keeps the visible glove on the surface of a wall when the real hand reaches past it. The zero-g
    /// locomotion moves the body back so this is normally a tiny correction; it only becomes visible
    /// when the body itself is pinned and cannot move away.
    /// </summary>
    public sealed class HandWallClamp : MonoBehaviour
    {
        public HandRig hand;
        public Transform head;
        public float radius = 0.05f;

        Vector3 m_RestLocalPosition;

        void Awake() => m_RestLocalPosition = transform.localPosition;

        void LateUpdate()
        {
            if (hand == null || head == null) return;
            Vector3 palm = hand.interactor != null ? hand.interactor.transform.position : hand.transform.position;
            if (!hand.IsAnchored && ZeroGLocomotion.TryGetPenetration(head, palm, radius, out Vector3 correction, out _))
                transform.position = transform.parent.TransformPoint(m_RestLocalPosition) + correction;
            else if (transform.localPosition != m_RestLocalPosition)
                transform.localPosition = m_RestLocalPosition;
        }
    }
}

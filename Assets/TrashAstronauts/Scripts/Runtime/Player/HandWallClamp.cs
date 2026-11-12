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

        void LateUpdate()
        {
            if (hand == null || head == null) return;
            Vector3 palm = hand.interactor != null ? hand.interactor.transform.position : hand.transform.position;
            if (!hand.IsAnchored && ZeroGLocomotion.TryGetPenetration(head, palm, radius, out Vector3 correction, out _))
                transform.position = hand.transform.position + correction;
            else if (transform.localPosition != Vector3.zero)
                transform.localPosition = Vector3.zero;
        }
    }
}

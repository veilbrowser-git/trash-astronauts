using UnityEngine;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// Simple articulated glove. The index finger follows the trigger and the other fingers follow the
    /// grip, so the player can see their own grab and trigger state on their hands.
    /// </summary>
    public sealed class GloveVisual : MonoBehaviour
    {
        public HandRig hand;
        public Transform[] indexPivots;
        public Transform[] gripPivots;
        public Transform thumbPivot;
        public float curlDegrees = 48f;
        public float thumbDegrees = 40f;
        public float smoothing = 18f;

        float m_Index;
        float m_Grip;

        void LateUpdate()
        {
            if (hand == null) return;
            float t = 1f - Mathf.Exp(-smoothing * Time.unscaledDeltaTime);
            m_Index = Mathf.Lerp(m_Index, hand.Trigger, t);
            m_Grip = Mathf.Lerp(m_Grip, Mathf.Max(hand.Grip, hand.IsHolding ? 1f : 0f), t);

            if (indexPivots != null)
            {
                for (int i = 0; i < indexPivots.Length; i++)
                {
                    if (indexPivots[i] != null) indexPivots[i].localRotation = Quaternion.Euler(Mathf.Max(m_Index, m_Grip * 0.6f) * curlDegrees, 0f, 0f);
                }
            }
            if (gripPivots != null)
            {
                for (int i = 0; i < gripPivots.Length; i++)
                {
                    if (gripPivots[i] != null) gripPivots[i].localRotation = Quaternion.Euler(m_Grip * curlDegrees, 0f, 0f);
                }
            }
            if (thumbPivot != null)
            {
                float sign = hand.side == Core.Handedness.Left ? 1f : -1f;
                thumbPivot.localRotation = Quaternion.Euler(0f, 0f, sign * Mathf.Max(m_Grip, m_Index) * thumbDegrees);
            }
        }
    }
}

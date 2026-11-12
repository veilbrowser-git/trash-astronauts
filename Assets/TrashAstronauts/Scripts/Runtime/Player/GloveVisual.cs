using UnityEngine;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// Poses the glove's fingers from the hand's inputs, so the player sees their own grab and trigger state:
    ///   - idle: a relaxed hand, fingers slightly curled, thumb out;
    ///   - trigger: curls the index finger;
    ///   - grip (or holding something): curls the middle, ring and little fingers and closes the thumb.
    /// Each finger segment is a rigid part pivoting at its knuckle and curls about its own local X, which swings
    /// it toward the palm. Curls blend smoothly and are clamped per joint. Hand tracking drives the same inputs
    /// (HandRig.GripOverride / TriggerOverride), so a tracked hand animates the glove too.
    ///
    /// The Blender glove (fingerJoints / thumbJoints) is used when it was built; otherwise the older stand-in
    /// glove's pivots (indexPivots / gripPivots / thumbPivot) are posed the old way.
    /// </summary>
    public sealed class GloveVisual : MonoBehaviour
    {
        public HandRig hand;

        [Header("Modelled glove")]
        [Tooltip("Index, middle, ring, little: three segments each, knuckle to tip.")]
        public Transform[] fingerJoints;
        [Tooltip("Each finger joint's local rotation with the finger straight.")]
        public Quaternion[] fingerBase;
        [Tooltip("Thumb: three segments, base to tip.")]
        public Transform[] thumbJoints;
        public Quaternion[] thumbBase;
        [Tooltip("Relaxed curl of the three finger segments, degrees.")]
        public Vector3 restCurl = new Vector3(10f, 14f, 8f);
        [Tooltip("Fully closed curl of the three finger segments, degrees.")]
        public Vector3 fistCurl = new Vector3(78f, 98f, 62f);
        [Tooltip("Relaxed thumb (slightly out), degrees.")]
        public Vector3 thumbRestCurl = new Vector3(-6f, 6f, 6f);
        [Tooltip("Thumb closed over the fingers, degrees.")]
        public Vector3 thumbFistCurl = new Vector3(34f, 40f, 36f);
        [Tooltip("The outer fingers close a little further than the index (they meet the palm further in).")]
        public float outerFingerExtra = 0.06f;
        [Tooltip("How far the index stays curled while holding something with the trigger released (0..1).")]
        [Range(0f, 1f)] public float holdIndex = 0.45f;
        [Tooltip("No joint ever bends past these, whatever the inputs (degrees).")]
        public float minCurl = -15f, maxCurl = 105f;

        [Header("Stand-in glove")]
        public Transform[] indexPivots;
        public Transform[] gripPivots;
        public Transform thumbPivot;
        public float curlDegrees = 68f;
        public float thumbDegrees = 40f;

        [Header("Feel")]
        public float smoothing = 18f;

        float m_Index;
        float m_Grip;

        /// <summary>The smoothed index (trigger) curl, 0 = relaxed, 1 = closed.</summary>
        public float IndexAmount => m_Index;
        /// <summary>The smoothed grip curl, 0 = relaxed, 1 = closed.</summary>
        public float GripAmount => m_Grip;
        /// <summary>True when the Blender glove (not the stand-in) is driven.</summary>
        public bool IsModelled => fingerJoints != null && fingerJoints.Length == 12 && fingerBase != null && fingerBase.Length == 12;

        void LateUpdate()
        {
            if (hand == null) return;
            bool holding = hand.IsHolding;
            float index = Mathf.Clamp01(Mathf.Max(hand.Trigger, holding ? holdIndex : 0f));
            float grip = Mathf.Clamp01(Mathf.Max(hand.Grip, holding ? 1f : 0f));
            float t = 1f - Mathf.Exp(-smoothing * Time.unscaledDeltaTime);
            m_Index = Mathf.Lerp(m_Index, index, t);
            m_Grip = Mathf.Lerp(m_Grip, grip, t);

            Apply(m_Index, m_Grip);
        }

        /// <summary>The curl (degrees about local X) of one joint for the given amounts. finger 0..3 = index..little, 4 = thumb; segment 0..2.</summary>
        public float JointCurl(int finger, int segment, float index, float grip)
        {
            float curl;
            if (finger == 4)
            {
                curl = Mathf.Lerp(thumbRestCurl[segment], thumbFistCurl[segment], Mathf.Clamp01(grip));
            }
            else
            {
                float amount = Mathf.Clamp01(finger == 0 ? index : grip);
                float fist = fistCurl[segment] * (1f + outerFingerExtra * finger);
                curl = Mathf.Lerp(restCurl[segment], fist, amount);
            }
            return Mathf.Clamp(curl, minCurl, maxCurl);
        }

        /// <summary>Poses the fingers: index from the trigger, the rest and the thumb from the grip. 0 = relaxed, 1 = closed.</summary>
        public void Apply(float index, float grip)
        {
            if (IsModelled)
            {
                for (int f = 0; f < 4; f++)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int i = f * 3 + k;
                        if (fingerJoints[i] != null) fingerJoints[i].localRotation = fingerBase[i] * Quaternion.Euler(JointCurl(f, k, index, grip), 0f, 0f);
                    }
                }
                if (thumbJoints != null && thumbBase != null)
                {
                    for (int k = 0; k < thumbJoints.Length && k < thumbBase.Length && k < 3; k++)
                    {
                        if (thumbJoints[k] != null) thumbJoints[k].localRotation = thumbBase[k] * Quaternion.Euler(JointCurl(4, k, index, grip), 0f, 0f);
                    }
                }
                return;
            }

            // Stand-in glove.
            if (indexPivots != null)
            {
                for (int i = 0; i < indexPivots.Length; i++)
                {
                    if (indexPivots[i] != null) indexPivots[i].localRotation = Quaternion.Euler(Mathf.Max(index, grip * 0.6f) * curlDegrees, 0f, 0f);
                }
            }
            if (gripPivots != null)
            {
                for (int i = 0; i < gripPivots.Length; i++)
                {
                    if (gripPivots[i] != null) gripPivots[i].localRotation = Quaternion.Euler(grip * curlDegrees, 0f, 0f);
                }
            }
            if (thumbPivot != null)
            {
                float sign = hand == null || hand.side == Core.Handedness.Left ? 1f : -1f;
                thumbPivot.localRotation = Quaternion.Euler(0f, 0f, sign * Mathf.Max(grip, index) * thumbDegrees);
            }
        }
    }
}

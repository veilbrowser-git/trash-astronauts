using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// Poses the glove's fingers from the hand's inputs, so the player sees their own grab and trigger state:
    ///   - idle: a relaxed hand, fingers slightly curled, thumb out;
    ///   - trigger: curls the index finger;
    ///   - grip: curls the middle, ring and little fingers and closes the thumb;
    ///   - holding something: each finger closes until it meets the object (fitted once, just after the grab,
    ///     from the object's colliders); a tool gets a firm fist with the index on its trigger.
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

        [Header("Holding")]
        [Tooltip("Fingers stop this far from a held object's surface (half the glove's finger thickness), metres.")]
        public float graspGap = 0.007f;
        [Tooltip("Steps tried, open to closed, when fitting a finger to a held object.")]
        public int graspSteps = 20;
        [Tooltip("Seconds after the grab before the fit is measured, so the object has settled into the hand.")]
        public float graspDelay = 0.08f;

        [Header("Feel")]
        public float smoothing = 18f;

        // Smoothed closing amount per finger: index, middle, ring, little, thumb (0 = relaxed, 1 = closed).
        readonly float[] m_Amount = new float[5];
        readonly float[] m_Target = new float[5];
        // Fitted closing amount per finger for the held object (1 = a full fist).
        readonly float[] m_Hold = { 1f, 1f, 1f, 1f, 1f };
        bool m_HoldIsTool;
        bool m_HoldFitted = true;
        Component m_HoldTarget;
        float m_FitAt;
        bool m_Listening;

        /// <summary>The smoothed index (trigger) curl, 0 = relaxed, 1 = closed.</summary>
        public float IndexAmount => m_Amount[0];
        /// <summary>The smoothed grip curl (middle finger), 0 = relaxed, 1 = closed.</summary>
        public float GripAmount => m_Amount[1];
        /// <summary>How far a finger (0..3 index..little, 4 thumb) closes on the object being held, as fitted at the grab.</summary>
        public float HoldAmount(int finger) => m_Hold[finger];
        /// <summary>True once the fit for the current held object has been measured.</summary>
        public bool HoldFitted => m_HoldFitted;
        /// <summary>True when the Blender glove (not the stand-in) is driven.</summary>
        public bool IsModelled => fingerJoints != null && fingerJoints.Length == 12 && fingerBase != null && fingerBase.Length == 12;

        void OnEnable()
        {
            if (hand != null && hand.interactor != null && !m_Listening)
            {
                hand.interactor.selectEntered.AddListener(OnGrabbed);
                m_Listening = true;
            }
        }

        void OnDisable()
        {
            if (m_Listening && hand != null && hand.interactor != null) hand.interactor.selectEntered.RemoveListener(OnGrabbed);
            m_Listening = false;
        }

        void OnGrabbed(SelectEnterEventArgs args)
        {
            var target = args.interactableObject as Component;
            m_HoldTarget = target;
            m_HoldIsTool = target != null && target.GetComponentInParent<Tools.SuitTool>() != null;
            for (int i = 0; i < m_Hold.Length; i++) m_Hold[i] = 1f;
            m_HoldFitted = m_HoldIsTool; // a tool is a firm fist straight away; anything else is measured shortly
            m_FitAt = Time.unscaledTime + graspDelay;
        }

        void LateUpdate()
        {
            if (hand == null) return;
            if (!m_Listening) OnEnable(); // hand assigned after enable (built at runtime)
            bool holding = hand.IsHolding;
            if (holding && !m_HoldFitted && Time.unscaledTime >= m_FitAt)
            {
                FitTo(m_HoldTarget != null ? m_HoldTarget.GetComponentsInChildren<Collider>() : null, false);
            }

            float trigger = Mathf.Clamp01(hand.Trigger);
            float grip = Mathf.Clamp01(hand.Grip);
            if (holding && m_HoldIsTool)
            {
                // Firm fist round the handle, index free on the trigger.
                m_Target[0] = Mathf.Max(trigger, holdIndex);
                for (int f = 1; f < 5; f++) m_Target[f] = 1f;
            }
            else if (holding)
            {
                // Each finger closes until it meets the object (before the fit is measured: half closed).
                for (int f = 0; f < 5; f++) m_Target[f] = m_HoldFitted ? m_Hold[f] : 0.5f * m_Hold[f];
            }
            else
            {
                m_Target[0] = trigger;
                for (int f = 1; f < 5; f++) m_Target[f] = grip;
            }

            float t = 1f - Mathf.Exp(-smoothing * Time.unscaledDeltaTime);
            for (int f = 0; f < 5; f++) m_Amount[f] = Mathf.Lerp(m_Amount[f], m_Target[f], t);
            if (IsModelled) ApplyFingers(m_Amount);
            else Apply(m_Amount[0], m_Amount[1]);
        }

        /// <summary>
        /// Measures, once, how far each finger can close on a held object: the finger is swung shut step by step
        /// (open to closed) until one of its segments comes within <see cref="graspGap"/> of one of the colliders.
        /// A tool (firmFist) is held in a full fist instead. Leaves the fingers where they were.
        /// </summary>
        public void FitTo(Collider[] colliders, bool firmFist)
        {
            m_HoldFitted = true;
            m_HoldIsTool = firmFist;
            for (int i = 0; i < m_Hold.Length; i++) m_Hold[i] = 1f;
            if (firmFist || !IsModelled || colliders == null || colliders.Length == 0) return;

            var saved = new Quaternion[15];
            for (int i = 0; i < 12; i++) saved[i] = fingerJoints[i].localRotation;
            for (int k = 0; k < 3 && k < thumbJoints.Length; k++) saved[12 + k] = thumbJoints[k].localRotation;
            int steps = Mathf.Max(2, graspSteps);
            for (int f = 0; f < 5; f++)
            {
                float fitted = 1f;
                for (int s = 0; s <= steps; s++)
                {
                    float a = (float)s / steps;
                    PoseFinger(f, a);
                    if (FingerTouches(f, colliders))
                    {
                        fitted = Mathf.Max(0f, (s - 1f) / steps);
                        break;
                    }
                }
                m_Hold[f] = fitted;
            }
            for (int i = 0; i < 12; i++) fingerJoints[i].localRotation = saved[i];
            for (int k = 0; k < 3 && k < thumbJoints.Length; k++) thumbJoints[k].localRotation = saved[12 + k];
        }

        void PoseFinger(int finger, float amount)
        {
            for (int k = 0; k < 3; k++)
            {
                if (finger < 4)
                {
                    int i = finger * 3 + k;
                    fingerJoints[i].localRotation = fingerBase[i] * Quaternion.Euler(JointCurl(finger, k, amount, amount), 0f, 0f);
                }
                else if (k < thumbJoints.Length)
                {
                    thumbJoints[k].localRotation = thumbBase[k] * Quaternion.Euler(JointCurl(4, k, amount, amount), 0f, 0f);
                }
            }
        }

        Transform Joint(int finger, int segment) => finger < 4 ? fingerJoints[finger * 3 + segment] : thumbJoints[segment];

        bool FingerTouches(int finger, Collider[] colliders)
        {
            for (int k = 0; k < 3; k++)
            {
                Transform joint = Joint(finger, k);
                float length = k < 2 ? Joint(finger, k + 1).localPosition.magnitude : SegmentLength(joint);
                // The pad side of the segment (its -Y) at its middle and its end.
                for (int p = 1; p <= 2; p++)
                {
                    Vector3 probe = joint.TransformPoint(new Vector3(0f, -0.004f, length * 0.5f * p));
                    for (int c = 0; c < colliders.Length; c++)
                    {
                        var col = colliders[c];
                        if (col == null || !col.enabled || col.isTrigger) continue;
                        bool exact = !(col is MeshCollider mesh) || mesh.convex;
                        Vector3 nearest = exact ? col.ClosestPoint(probe) : col.bounds.ClosestPoint(probe);
                        if ((nearest - probe).sqrMagnitude <= graspGap * graspGap) return true;
                    }
                }
            }
            return false;
        }

        static float SegmentLength(Transform joint)
        {
            var filter = joint.GetComponent<MeshFilter>();
            return filter != null && filter.sharedMesh != null ? Mathf.Max(0.01f, filter.sharedMesh.bounds.max.z - 0.004f) : 0.02f;
        }

        /// <summary>Poses each finger by its own closing amount: index, middle, ring, little, thumb.</summary>
        public void ApplyFingers(float[] amounts)
        {
            if (!IsModelled || amounts == null || amounts.Length < 5) return;
            for (int f = 0; f < 5; f++) PoseFinger(f, amounts[f]);
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

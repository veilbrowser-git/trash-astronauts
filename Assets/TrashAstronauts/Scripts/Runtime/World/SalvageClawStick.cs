using System;
using TrashAstronauts.Player;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace TrashAstronauts.World
{
    /// <summary>
    /// The salvage claw's two-axis control stick. Squeeze GRIP on the knob to take it (an XRSimpleInteractable,
    /// so the knob itself never leaves its socket and the zero-g locomotion does not treat the squeeze as
    /// grabbing the wall); moving the hand tilts the stick. Let go and it springs back to centre. TRIGGER on
    /// the hand holding it opens and closes the claw.
    /// </summary>
    public sealed class SalvageClawStick : MonoBehaviour
    {
        [Tooltip("Rotates to show the tilt. Its parent's axes are the stick's: +X right, +Y up, +Z away from the operator.")]
        public Transform pivot;
        public XRSimpleInteractable interactable;
        [Tooltip("Height of the knob above the pivot (m), the lever arm the hand moves.")]
        public float knobHeight = 0.2f;
        [Tooltip("How quickly the released stick springs back (1/s).")]
        public float springRate = 14f;

        IXRSelectInteractor m_Interactor;
        Vector3 m_GrabPoint;    // the hand where it took hold, in the stick's frame
        Vector2 m_TiltAtGrab;
        Vector2 m_Tilt; // degrees: x = sideways (+ right), y = forward (+ away from the operator)

        /// <summary>Raised when the trigger is pulled on the hand that holds the stick.</summary>
        public event Action TriggerPulled;

        /// <summary>The hand holding the stick, or null.</summary>
        public HandRig HeldBy { get; private set; }
        public bool Held => m_Interactor != null;

        /// <summary>Stick deflection, each axis -1..1 after the dead zone: x = right, y = forward (away from the operator).</summary>
        public Vector2 Axis
        {
            get
            {
                Vector2 a = m_Tilt / SalvageClawConfig.StickMaxTilt;
                return new Vector2(DeadZone(a.x), DeadZone(a.y));
            }
        }

        /// <summary>The current tilt in degrees (x right, y forward).</summary>
        public Vector2 Tilt => m_Tilt;

        static float DeadZone(float v)
        {
            float dz = SalvageClawConfig.StickDeadZone;
            float m = Mathf.Abs(v);
            if (m <= dz) return 0f;
            return Mathf.Sign(v) * Mathf.Clamp01((m - dz) / (1f - dz));
        }

        void OnEnable()
        {
            if (interactable == null) return;
            interactable.selectEntered.AddListener(OnSelectEntered);
            interactable.selectExited.AddListener(OnSelectExited);
            interactable.activated.AddListener(OnActivated);
        }

        void OnDisable()
        {
            if (interactable != null)
            {
                interactable.selectEntered.RemoveListener(OnSelectEntered);
                interactable.selectExited.RemoveListener(OnSelectExited);
                interactable.activated.RemoveListener(OnActivated);
            }
            m_Interactor = null;
            HeldBy = null;
        }

        void OnSelectEntered(SelectEnterEventArgs args)
        {
            m_Interactor = args.interactorObject;
            HeldBy = (args.interactorObject as Component) != null ? ((Component)args.interactorObject).GetComponentInParent<HandRig>() : null;
            // Wherever the hand closed on the knob counts as "where the stick is now", so taking hold never jerks the arm.
            m_GrabPoint = HandPoint();
            m_TiltAtGrab = m_Tilt;
        }

        void OnSelectExited(SelectExitEventArgs args)
        {
            if (args.interactorObject != m_Interactor) return;
            m_Interactor = null;
            HeldBy = null;
        }

        void OnActivated(ActivateEventArgs args) => TriggerPulled?.Invoke();

        /// <summary>Test seam: what a hand pulling the trigger on the stick does.</summary>
        public void PullTrigger() => TriggerPulled?.Invoke();

        /// <summary>The holding hand's grab point in the stick's frame (+X right, +Y up, +Z away from the operator).</summary>
        Vector3 HandPoint()
        {
            if (m_Interactor == null || pivot == null) return Vector3.zero;
            Transform attach = m_Interactor.GetAttachTransform(interactable);
            if (attach == null) attach = m_Interactor.transform;
            Transform frame = pivot.parent != null ? pivot.parent : pivot;
            return frame.InverseTransformPoint(attach.position);
        }

        /// <summary>
        /// The tilt for a hand that has moved by <paramref name="moved"/> (m, stick frame) since it took hold: the knob
        /// follows the hand around the pivot, so a hand moving about 8 cm gives full deflection wherever it gripped.
        /// </summary>
        public Vector2 TiltFor(Vector3 moved)
        {
            float lever = Mathf.Max(0.08f, knobHeight);
            return m_TiltAtGrab + new Vector2(Mathf.Atan2(moved.x, lever) * Mathf.Rad2Deg, Mathf.Atan2(moved.z, lever) * Mathf.Rad2Deg);
        }

        /// <summary>Moves the stick (also the test seam: the station calls it every step).</summary>
        public void Step(float dt)
        {
            float max = SalvageClawConfig.StickMaxTilt;
            if (m_Interactor != null)
            {
                Vector2 want = TiltFor(HandPoint() - m_GrabPoint);
                m_Tilt = new Vector2(Mathf.Clamp(want.x, -max, max), Mathf.Clamp(want.y, -max, max));
            }
            else
            {
                m_Tilt *= Mathf.Exp(-springRate * dt);
                if (m_Tilt.sqrMagnitude < 0.0001f) m_Tilt = Vector2.zero;
            }
            if (pivot != null) pivot.localRotation = Quaternion.Euler(m_Tilt.y, 0f, -m_Tilt.x);
        }
    }
}

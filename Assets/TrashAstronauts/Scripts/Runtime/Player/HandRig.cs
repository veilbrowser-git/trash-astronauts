using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// One tracked controller: its grab interactor, haptics, and the raw inputs other hand-mounted
    /// components need. Everything input- or headset-specific for a hand lives on this object.
    /// </summary>
    public sealed class HandRig : MonoBehaviour
    {
        public Handedness side = Handedness.Right;
        public XRDirectInteractor interactor;
        public HapticImpulsePlayer haptics;
        [Tooltip("Tip of the index finger. Pushes physical buttons.")]
        public Transform fingertip;
        [Tooltip("Where the wrist display attaches when this is the off hand.")]
        public Transform wristMount;

        [Header("Input")]
        public InputActionReference thumbstick;
        public InputActionReference gripValue;
        public InputActionReference triggerValue;
        public InputActionReference triggerButton;
        public InputActionReference menuButton;
        public InputActionReference thumbstickClick;

        [Header("Suit thruster")]
        [Tooltip("Shown while this hand's thruster is firing.")]
        public GameObject thrusterVisual;
        public AudioSource thrusterAudio;

        [Header("Feedback")]
        [Range(0f, 1f)] public float hoverHaptic = 0.08f;
        [Range(0f, 1f)] public float grabHaptic = 0.35f;

        public PlayerContext Player { get; private set; }
        /// <summary>This hand's reach assist (picking things up from beyond the fingertips), or null.</summary>
        public ReachAssist Reach { get; private set; }
        public bool IsHolding => interactor != null && interactor.hasSelection;
        /// <summary>True while this hand's pointer is on a button (so its trigger means "press", not "thrust").</summary>
        public bool IsPointingAtButton { get; set; }
        /// <summary>Test seam and hand-tracking hook: forces the brake input.</summary>
        public bool? ThumbstickClickOverride { get; set; }
        public bool ThumbstickClickHeld => ThumbstickClickOverride ?? (thumbstickClick != null && thumbstickClick.action != null && thumbstickClick.action.enabled && thumbstickClick.action.IsPressed());

        /// <summary>True while this hand is holding on to the ship (wall, rail, prop). Set by ZeroGLocomotion.</summary>
        public bool IsAnchored { get; set; }

        /// <summary>Replaces the grip input when set (hand tracking, editor simulation, tests). Null = use the controller.</summary>
        public float? GripOverride { get; set; }
        /// <summary>Replaces the trigger input when set (hand tracking pinch).</summary>
        public float? TriggerOverride { get; set; }
        /// <summary>Frame on which a tracked hand pinched (acts as a trigger press).</summary>
        public int TriggerPressedFrame { get; set; } = -1;
        /// <summary>Frame on which a tracked hand made the menu gesture.</summary>
        public int MenuPressedFrame { get; set; } = -1;
        /// <summary>True while this hand is driven by hand tracking instead of a controller.</summary>
        public bool IsTrackedHand { get; set; }

        public Vector2 Thumbstick => Read(thumbstick, Vector2.zero);
        public float Grip => GripOverride ?? Read(gripValue, 0f);
        public float Trigger => TriggerOverride ?? Read(triggerValue, 0f);
        public bool TriggerPressedThisFrame => TriggerPressedFrame == Time.frameCount
            || (triggerButton != null && triggerButton.action != null && triggerButton.action.WasPressedThisFrame());
        public bool MenuPressedThisFrame => MenuPressedFrame == Time.frameCount
            || (menuButton != null && menuButton.action != null && menuButton.action.WasPressedThisFrame());

        void Awake()
        {
            Player = GetComponentInParent<PlayerContext>();
            Reach = GetComponent<ReachAssist>();
        }

        void OnEnable()
        {
            if (interactor == null) return;
            interactor.selectEntered.AddListener(OnSelectEntered);
            interactor.hoverEntered.AddListener(OnHoverEntered);
        }

        void OnDisable()
        {
            if (interactor == null) return;
            interactor.selectEntered.RemoveListener(OnSelectEntered);
            interactor.hoverEntered.RemoveListener(OnHoverEntered);
        }

        void OnSelectEntered(SelectEnterEventArgs args)
        {
            Pulse(grabHaptic, 0.06f);
            if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.grab, transform.position, 0.5f, Random.Range(0.92f, 1.08f), 8f);
        }

        void OnHoverEntered(HoverEnterEventArgs args)
        {
            if (!IsHolding) Pulse(hoverHaptic, 0.02f);
        }

        /// <summary>How many haptic pulses this hand has been asked for (test seam).</summary>
        public int PulseCount { get; private set; }

        /// <summary>Sends a short haptic pulse to this controller. Safe to call with no device connected.</summary>
        public void Pulse(float amplitude, float duration)
        {
            if (amplitude > 0f) PulseCount++;
            if (haptics != null && isActiveAndEnabled && amplitude > 0f) haptics.SendHapticImpulse(Mathf.Clamp01(amplitude), duration);
        }

        static T Read<T>(InputActionReference reference, T fallback) where T : struct
        {
            return reference != null && reference.action != null && reference.action.enabled
                ? reference.action.ReadValue<T>()
                : fallback;
        }
    }
}

using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Climbing;

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

        [Header("Feedback")]
        [Range(0f, 1f)] public float hoverHaptic = 0.08f;
        [Range(0f, 1f)] public float grabHaptic = 0.35f;

        public PlayerContext Player { get; private set; }
        public bool IsHolding => interactor != null && interactor.hasSelection;

        public bool IsHoldingRail
        {
            get
            {
                if (!IsHolding) return false;
                var selected = interactor.interactablesSelected;
                for (int i = 0; i < selected.Count; i++)
                {
                    if (selected[i] is ClimbInteractable) return true;
                }
                return false;
            }
        }

        public Vector2 Thumbstick => Read(thumbstick, Vector2.zero);
        public float Grip => Read(gripValue, 0f);
        public float Trigger => Read(triggerValue, 0f);
        public bool TriggerPressedThisFrame => triggerButton != null && triggerButton.action != null && triggerButton.action.WasPressedThisFrame();
        public bool MenuPressedThisFrame => menuButton != null && menuButton.action != null && menuButton.action.WasPressedThisFrame();

        void Awake()
        {
            Player = GetComponentInParent<PlayerContext>();
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

        /// <summary>Sends a short haptic pulse to this controller. Safe to call with no device connected.</summary>
        public void Pulse(float amplitude, float duration)
        {
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

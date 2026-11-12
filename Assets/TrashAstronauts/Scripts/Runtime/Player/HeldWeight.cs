using System;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// Weight in the hand. The grab already moves the object by setting its velocity (so a held thing
    /// still collides with walls and other objects instead of passing through them); this adds:
    ///  - inertia by mass: light things follow the hand 1:1, heavy ones lag a little and swing;
    ///  - a grip that slips (with a haptic tick) when the hand is pulled too far from where the object
    ///    is stuck, so the hand never drags it through the ship;
    ///  - throws limited by what an arm can give that mass: the release keeps the true hand velocity and
    ///    spin, up to <see cref="RealPhysics.MaxThrowSpeed"/> relative to the thrower.
    /// It never touches isKinematic or grab.enabled (SalvageItem.RefreshBodyMode owns those).
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class HeldWeight : MonoBehaviour
    {
        [Tooltip("Up to this mass (kg) an object follows the hand exactly.")]
        public float freeMass = 2f;
        [Tooltip("How far (m) the hand may be from its grip point on the object before the grip slips.")]
        public float slipDistance = 0.45f;
        [Tooltip("How long (s) the hand must stay that far away before letting go.")]
        public float slipSeconds = 0.15f;
        [Tooltip("Hardest spin (rad/s) a wrist flick gives an object with almost no mass.")]
        public float maxThrowSpin = 25f;

        XRGrabInteractable m_Grab;
        Rigidbody m_Body;
        IXRSelectInteractor m_Holder;
        Vector3 m_LocalGrip;
        float m_FarFor;
        float m_LimitUntil = -1f;
        float m_TwoHandedUntil = -1f;
        int m_ReleaseArms = 1;
        /// <summary>Arms behind the last throw (2 if both hands were on it as it was let go).</summary>
        public int LastThrowArms => m_ReleaseArms;
        Vector3 m_ThrowerVelocity;

        /// <summary>The grip slipped because the hand was pulled away from where the object was stuck.</summary>
        public event Action<HeldWeight> Slipped;
        public int SlipCount { get; private set; }
        /// <summary>How far the hand is from its grip point right now (0 when not held).</summary>
        public float GripGap { get; private set; }

        void Awake()
        {
            m_Grab = GetComponent<XRGrabInteractable>();
            m_Body = GetComponent<Rigidbody>();
        }

        void OnEnable()
        {
            if (m_Grab == null) return;
            m_Grab.selectEntered.AddListener(OnGrabbed);
            m_Grab.selectExited.AddListener(OnReleased);
        }

        void OnDisable()
        {
            if (m_Grab == null) return;
            m_Grab.selectEntered.RemoveListener(OnGrabbed);
            m_Grab.selectExited.RemoveListener(OnReleased);
        }

        /// <summary>
        /// How hard the grab steers an object of this mass toward the hand each physics step (1 = all the
        /// way, as for anything up to <paramref name="freeMass"/>), and how much of its own velocity it keeps
        /// (damping 1 = none). Heavy things are steered gently and keep their momentum, so they trail the
        /// hand and swing a little past it when it stops: a damped oscillation that settles in well under a
        /// second. Worked per physics step, so it is the same at any frame rate.
        /// </summary>
        public static void Follow(float mass, out float scale, out float damping, float freeMass = 2f)
        {
            if (mass <= freeMass) { scale = 1f; damping = 1f; return; }
            scale = Mathf.Clamp(freeMass / mass, 0.1f, 1f);
            damping = Mathf.Clamp(freeMass / mass + 0.25f, 0.3f, 1f);
        }

        /// <summary>How many hands hold this right now.</summary>
        public int HandCount => m_Grab != null ? m_Grab.interactorsSelecting.Count : 0;

        /// <summary>
        /// Two hands share the load: each arm moves half of it, so a pair steers it as if it weighed
        /// <see cref="TwoHandShare"/> of its mass. Heavy items (over <see cref="freeMass"/>) allow a second
        /// hand (the builder sets their select mode to Multiple); light ones never change.
        /// </summary>
        public const float TwoHandShare = 0.25f;

        void OnGrabbed(SelectEnterEventArgs args)
        {
            m_FarFor = 0f;
            m_LimitUntil = -1f;
            RefreshHolders();
        }

        /// <summary>Re-reads who is holding it and sets the follow for one or two hands.</summary>
        void RefreshHolders()
        {
            int hands = HandCount;
            if (hands >= 2) m_TwoHandedUntil = float.MaxValue;
            m_Holder = hands > 0 ? m_Grab.interactorsSelecting[0] : null;
            if (m_Holder == null) return;
            Transform attach = m_Holder.GetAttachTransform(m_Grab);
            m_LocalGrip = transform.InverseTransformPoint(attach != null ? attach.position : m_Holder.transform.position);

            Follow(hands >= 2 ? m_Body.mass * TwoHandShare : m_Body.mass, out float scale, out float damping, freeMass);
            m_Grab.smoothPosition = false;
            m_Grab.smoothRotation = false;
            m_Grab.velocityScale = scale;
            m_Grab.velocityDamping = damping;
            m_Grab.angularVelocityScale = scale;
            m_Grab.angularVelocityDamping = damping;
        }

        void OnReleased(SelectExitEventArgs args)
        {
            if (m_Grab.isSelected)
            {
                // One of two hands let go: the other carries on alone, without dropping it. A throw made
                // with both hands lets go of them a moment apart, so it still counts as two-handed.
                if (m_TwoHandedUntil == float.MaxValue) m_TwoHandedUntil = Time.time + 0.25f;
                m_FarFor = 0f;
                RefreshHolders();
                return;
            }
            m_ReleaseArms = Time.time <= m_TwoHandedUntil ? 2 : 1;
            m_TwoHandedUntil = -1f;
            m_Holder = null;
            GripGap = 0f;
            var player = args.interactorObject != null ? args.interactorObject.transform.GetComponentInParent<PlayerContext>() : null;
            m_ThrowerVelocity = player != null && player.locomotion != null && player.locomotion.zeroG != null ? player.locomotion.zeroG.Velocity : Vector3.zero;
            // The grab applies the throw as it detaches; keep it within what an arm can do for a moment.
            m_LimitUntil = Time.time + 0.12f;
        }

        void FixedUpdate()
        {
            if (m_LimitUntil >= 0f)
            {
                if (Time.time > m_LimitUntil || m_Grab.isSelected || m_Body.isKinematic) m_LimitUntil = -1f;
                else LimitThrow(m_ThrowerVelocity, m_ReleaseArms);
            }
            if (m_Holder == null || !m_Grab.isSelected || m_Body.isKinematic) return;

            Transform attach = m_Holder.GetAttachTransform(m_Grab);
            Vector3 hand = attach != null ? attach.position : m_Holder.transform.position;
            GripGap = Vector3.Distance(hand, transform.TransformPoint(m_LocalGrip));
            m_FarFor = GripGap > slipDistance ? m_FarFor + Time.fixedDeltaTime : 0f;
            if (m_FarFor >= slipSeconds) Slip();
        }

        /// <summary>Clamps the body's speed relative to the thrower, and its spin, to what an arm can give this mass.</summary>
        public void LimitThrow(Vector3 throwerVelocity, int arms = 1)
        {
            // Two arms put twice the energy in: the same as one arm throwing half the mass.
            float mass = m_Body.mass / Mathf.Clamp(arms, 1, 2);
            Vector3 relative = m_Body.linearVelocity - throwerVelocity;
            float maxSpeed = RealPhysics.MaxThrowSpeed(mass);
            if (relative.sqrMagnitude > maxSpeed * maxSpeed) m_Body.linearVelocity = throwerVelocity + relative.normalized * maxSpeed;
            float maxSpin = maxThrowSpin * Mathf.Sqrt(RealPhysics.ArmEffectiveMass / (RealPhysics.ArmEffectiveMass + mass));
            if (m_Body.angularVelocity.sqrMagnitude > maxSpin * maxSpin) m_Body.angularVelocity = m_Body.angularVelocity.normalized * maxSpin;
        }

        void Slip()
        {
            var holder = m_Holder;
            m_FarFor = 0f;
            if (holder == null || m_Grab.interactionManager == null) return;
            var hand = holder.transform.GetComponentInParent<HandRig>();
            if (hand == null && holder is Component c) hand = c.GetComponentInParent<HandRig>();
            m_Grab.interactionManager.SelectExit(holder, m_Grab);
            SlipCount++;
            if (hand != null) hand.Pulse(0.6f, 0.05f);
            Slipped?.Invoke(this);
        }
    }
}

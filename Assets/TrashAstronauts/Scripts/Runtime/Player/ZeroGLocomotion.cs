using System;
using TrashAstronauts.Audio;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// Weightless movement. The player floats and keeps drifting until something stops them.
    ///
    /// Two ways to move, both using GRIP on an empty hand:
    ///  - Near a wall, floor, ceiling, prop or handrail: the hand grabs the surface. Moving the hand
    ///    moves the body the opposite way (pull yourself along), and letting go keeps the speed you had,
    ///    so a shove sends you gliding (push off).
    ///  - In open space: the hand acts as a paddle. Sweeping it pushes you the other way, like swimming.
    ///    Open the hand on the way back.
    ///
    /// Grabbing any surface stops a drift. All motion is queued through XRI's locomotion mediator and
    /// the character controller, so the player cannot pass through walls and the headset camera is
    /// never moved directly.
    /// </summary>
    public sealed class ZeroGLocomotion : LocomotionProvider
    {
        public HandRig leftHand;
        public HandRig rightHand;

        [Header("Grabbing surfaces")]
        [Tooltip("How close the palm must be to a surface to grab it.")]
        public float grabReach = 0.16f;
        [Tooltip("Speed kept when letting go, as a fraction of how fast you were pulling.")]
        public float pushOffScale = 1f;

        [Header("Swimming")]
        [Tooltip("How much of a stroke's hand speed becomes body speed.")]
        public float swimStrength = 0.85f;
        [Tooltip("Strokes slower than this (m/s) do nothing, so small hand movements do not push you around.")]
        public float swimMinHandSpeed = 0.5f;

        [Header("Drift")]
        public float maxSpeed = 3.2f;
        [Tooltip("Fraction of speed lost per second while drifting. Low = long glides.")]
        public float drag = 0.22f;
        [Tooltip("Drag used when the comfort option 'drift' is off: you stop almost as soon as you let go.")]
        public float comfortDrag = 7f;
        public float bounce = 0.15f;
        [Tooltip("Comfort option. When off, there is no momentum: movement stops when you let go.")]
        public bool momentumEnabled = true;

        [Range(0.1f, 0.9f)] public float gripPress = 0.6f;
        [Range(0.05f, 0.8f)] public float gripRelease = 0.35f;

        sealed class HandState
        {
            public HandRig hand;
            public bool gripHeld;
            public bool anchored;
            public bool swimming;
            public bool nearSurface;
            public bool hasLast;
            public Vector3 lastLocal;
            public Vector3 smoothedVelocity;   // hand velocity relative to the body, world axes
            public float anchorTime;
            public HoverHighlight hoveredRail;
        }

        readonly HandState[] m_Hands = { new HandState(), new HandState() };
        readonly XROriginMovement m_Move = new XROriginMovement();
        readonly Collider[] m_Overlap = new Collider[8];
        HandState m_Driver;
        Vector3 m_Velocity;

        public Vector3 Velocity => m_Velocity;
        public float Speed => m_Velocity.magnitude;
        public bool IsAnchored => m_Driver != null;
        /// <summary>Total speed gained from swimming strokes since start (m/s). Used by the tutorial.</summary>
        public float SwimSpeedGained { get; private set; }

        /// <summary>A hand took hold of a surface. Argument: body speed at that moment.</summary>
        public event Action<float> Grabbed;
        /// <summary>The last hand let go. Argument: resulting drift speed.</summary>
        public event Action<float> PushedOff;
        public event Action Stroked;

        protected override void Awake()
        {
            base.Awake();
            m_Hands[0].hand = leftHand;
            m_Hands[1].hand = rightHand;
        }

        public bool IsHandAnchored(HandRig hand)
        {
            foreach (var state in m_Hands)
            {
                if (state.hand == hand) return state.anchored;
            }
            return false;
        }

        /// <summary>Kills all drift (teleports, unstuck, returning to the tug).</summary>
        public void Stop()
        {
            m_Velocity = Vector3.zero;
        }

        public void AddImpulse(Vector3 velocityChange)
        {
            m_Velocity = Vector3.ClampMagnitude(m_Velocity + velocityChange, maxSpeed);
        }

        void Update()
        {
            if (mediator == null || mediator.xrOrigin == null || mediator.xrOrigin.Origin == null) return;
            Transform origin = mediator.xrOrigin.Origin.transform;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            Vector3 anchoredMotion = Vector3.zero;

            foreach (var state in m_Hands)
            {
                if (state.hand == null) continue;
                Vector3 palm = PalmPosition(state.hand);
                Vector3 local = origin.InverseTransformPoint(palm);
                Vector3 worldDelta = state.hasLast ? origin.TransformVector(local - state.lastLocal) : Vector3.zero;
                state.lastLocal = local;
                state.hasLast = true;

                // Ignore tracking glitches (a hand jumping across the room in one frame).
                if (worldDelta.magnitude > 0.5f) worldDelta = Vector3.zero;
                Vector3 handVelocity = worldDelta / dt;
                state.smoothedVelocity = Vector3.Lerp(state.smoothedVelocity, handVelocity, 1f - Mathf.Exp(-dt / 0.07f));

                UpdateGrip(state, palm);

                if (state.anchored && state == m_Driver) anchoredMotion = -worldDelta;

                if (state.swimming && !state.hand.IsHolding)
                {
                    float speed = handVelocity.magnitude;
                    if (speed >= swimMinHandSpeed)
                    {
                        Vector3 gain = -handVelocity * (swimStrength * dt);
                        Vector3 before = m_Velocity;
                        m_Velocity = Vector3.ClampMagnitude(m_Velocity + gain, maxSpeed);
                        SwimSpeedGained += (m_Velocity - before).magnitude;
                        Stroked?.Invoke();
                    }
                }
            }

            if (m_Driver != null)
            {
                m_Velocity = Vector3.zero;
            }
            else
            {
                float k = momentumEnabled ? drag : comfortDrag;
                m_Velocity *= Mathf.Exp(-k * dt);
                if (m_Velocity.sqrMagnitude < 0.02f * 0.02f) m_Velocity = Vector3.zero;
            }

            Vector3 motion = anchoredMotion + m_Velocity * dt;
            if (motion.sqrMagnitude > 1e-10f)
            {
                TryStartLocomotionImmediately();
                if (locomotionState != LocomotionState.Moving) return;
                m_Move.motion = motion;
                TryQueueTransformation(m_Move);
            }
            else if (locomotionState == LocomotionState.Moving)
            {
                TryEndLocomotion();
            }
        }

        static Vector3 PalmPosition(HandRig hand)
        {
            return hand.interactor != null ? hand.interactor.transform.position : hand.transform.position;
        }

        void UpdateGrip(HandState state, Vector3 palm)
        {
            float grip = state.hand.Grip;
            bool pressed = !state.gripHeld && grip >= gripPress;
            bool released = state.gripHeld && grip <= gripRelease;

            // Feedback when the palm comes within reach of something grabbable.
            HoverHighlight rail = null;
            bool near = false;
            if (!state.anchored && !state.hand.IsHolding) near = IsNearSurface(palm, out rail);
            if (near && !state.nearSurface && !state.gripHeld) state.hand.Pulse(0.1f, 0.02f);
            state.nearSurface = near;
            if (state.hoveredRail != rail)
            {
                if (state.hoveredRail != null) state.hoveredRail.SetManualHover(false);
                state.hoveredRail = rail;
                if (rail != null) rail.SetManualHover(true);
            }

            if (pressed)
            {
                state.gripHeld = true;
                if (!WillGrabAnItem(state.hand))
                {
                    if (IsNearSurface(palm, out _)) BeginAnchor(state);
                    else state.swimming = true;
                }
            }
            else if (released)
            {
                state.gripHeld = false;
                state.swimming = false;
                if (state.anchored) EndAnchor(state);
            }

            // Picking something up cancels paddling with that hand.
            if (state.swimming && state.hand.IsHolding) state.swimming = false;
        }

        bool IsNearSurface(Vector3 palm, out HoverHighlight rail)
        {
            rail = null;
            int count = Physics.OverlapSphereNonAlloc(palm, grabReach, m_Overlap,
                GameLayers.EnvironmentMask | GameLayers.RailMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                if (m_Overlap[i].gameObject.layer == GameLayers.Rail)
                    rail = m_Overlap[i].GetComponentInParent<HoverHighlight>();
            }
            return count > 0;
        }

        static bool WillGrabAnItem(HandRig hand)
        {
            if (hand.interactor == null) return false;
            if (hand.interactor.hasSelection) return true;
            var hovered = hand.interactor.interactablesHovered;
            for (int i = 0; i < hovered.Count; i++)
            {
                if (hovered[i] is XRBaseInteractable) return true;
            }
            return false;
        }

        /// <summary>Tries to take hold of a nearby surface with this hand. Public for tests and simulation.</summary>
        public bool TryGrabSurface(HandRig hand)
        {
            foreach (var state in m_Hands)
            {
                if (state.hand != hand) continue;
                if (!IsNearSurface(PalmPosition(hand), out _)) return false;
                state.gripHeld = true;
                BeginAnchor(state);
                return true;
            }
            return false;
        }

        /// <summary>Lets go with this hand. Public for tests and simulation.</summary>
        public void ReleaseSurface(HandRig hand)
        {
            foreach (var state in m_Hands)
            {
                if (state.hand == hand && state.anchored)
                {
                    state.gripHeld = false;
                    EndAnchor(state);
                }
            }
        }

        void BeginAnchor(HandState state)
        {
            float speedBefore = m_Velocity.magnitude;
            state.anchored = true;
            state.swimming = false;
            state.anchorTime = Time.time;
            state.smoothedVelocity = Vector3.zero;
            state.hand.IsAnchored = true;
            m_Driver = state;
            m_Velocity = Vector3.zero;
            state.hand.Pulse(0.4f, 0.05f);
            if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.grab, PalmPosition(state.hand), 0.45f, 0.8f, 8f);
            Grabbed?.Invoke(speedBefore);
        }

        void EndAnchor(HandState state)
        {
            state.anchored = false;
            state.hand.IsAnchored = false;
            if (m_Driver != state) return;

            // Hand over to the other hand if it is still holding on.
            m_Driver = null;
            foreach (var other in m_Hands)
            {
                if (other != state && other.anchored) m_Driver = other;
            }
            if (m_Driver != null) return;

            // Released completely: keep the speed the body had from that last pull or shove.
            m_Velocity = Vector3.ClampMagnitude(-state.smoothedVelocity * pushOffScale, maxSpeed);
            if (m_Velocity.magnitude < 0.08f) m_Velocity = Vector3.zero;
            PushedOff?.Invoke(m_Velocity.magnitude);
        }

        // Sent by the CharacterController when the body touches the ship: stop moving into the surface.
        void OnControllerColliderHit(ControllerColliderHit hit)
        {
            float into = Vector3.Dot(m_Velocity, hit.normal);
            if (into < 0f) m_Velocity -= hit.normal * (into * (1f + bounce));
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            foreach (var state in m_Hands)
            {
                if (state.hoveredRail != null) state.hoveredRail.SetManualHover(false);
                state.hoveredRail = null;
                state.anchored = false;
                state.swimming = false;
                if (state.hand != null) state.hand.IsAnchored = false;
            }
            m_Driver = null;
        }
    }
}

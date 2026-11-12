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
    /// Ways to move:
    ///  - GRIP near a wall, floor, ceiling, prop or handrail grabs it. Moving the hand moves the body
    ///    the opposite way (pull yourself along); letting go keeps the speed you had (push off).
    ///  - Swim with open hands: sweep an arm quickly and you are pushed the other way, but never
    ///    backward relative to where you are looking, so reaching for things does not shove you away.
    ///  - Hold GRIP in open space to paddle in any direction, including backward.
    ///  - Hands are solid: push or slap a wall with an open hand and it shoves you away. The body
    ///    bounces off the hull instead of stopping dead.
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
        public float grabReach = 0.22f;
        [Tooltip("Speed kept when letting go, as a fraction of how fast you were pulling.")]
        public float pushOffScale = 1f;

        [Header("Swimming")]
        [Tooltip("How much of a stroke's hand speed becomes body speed.")]
        public float swimStrength = 1.6f;
        [Tooltip("Strokes slower than this (m/s) do nothing, so small hand movements do not push you around.")]
        public float swimMinHandSpeed = 0.35f;

        [Header("Open-hand swimming (no button)")]
        public bool openHandSwim = true;
        [Tooltip("Open-hand strokes must be at least this fast (m/s) to count, so normal hand movement is ignored.")]
        public float openSwimMinHandSpeed = 0.65f;
        public float openSwimStrength = 1.15f;

        [Header("Suit thruster (point an empty hand and hold trigger)")]
        public bool thrusterEnabled = true;
        [Tooltip("Acceleration in m/s^2 along the direction the hand points, for the suited body alone. The thrust is a fixed force, so carrying something heavy makes it proportionally slower (a = F / (body + load)).")]
        public float thrusterAcceleration = 1.9f;
        [Tooltip("The thruster stops adding speed once you are moving this fast in that direction.")]
        public float thrusterMaxSpeed = 2.4f;
        [Range(0.1f, 0.9f)] public float thrusterTriggerThreshold = 0.45f;
        [Tooltip("Speed lost per second while a thumbstick is clicked in (brake).")]
        public float brakeDeceleration = 4.5f;
        [Tooltip("How loud the thruster is to the creature (0 = silent). Swimming and pushing off are silent.")]
        [Range(0f, 1f)] public float thrusterLoudness = 0.2f;
        float m_ThrusterNoiseTimer;

        [Header("Solid hands")]
        [Tooltip("Head transform. The arm is checked from the chest to the palm so a hand cannot pass through a wall.")]
        public Transform head;
        public float handRadius = 0.05f;
        [Tooltip("Largest correction applied in one frame (m). Guards against tracking jumps.")]
        public float maxPushPerFrame = 0.2f;

        [Header("Drift")]
        public float maxSpeed = 3.6f;
        [Tooltip("Fraction of speed lost per second while drifting. Low = long glides.")]
        public float drag = 0.16f;
        [Tooltip("Drag used when the comfort option 'drift' is off: you stop almost as soon as you let go.")]
        public float comfortDrag = 7f;
        [Range(0f, 1f)] public float bounce = 0.55f;
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
            public bool touchingWall;
            public HoverHighlight hoveredRail;
        }

        readonly HandState[] m_Hands = { new HandState(), new HandState() };
        readonly XROriginMovement m_Move = new XROriginMovement();
        readonly Collider[] m_Overlap = new Collider[8];
        HandState m_Driver;
        Vector3 m_Velocity;
        float m_LastBump = -10f;
        bool m_ThrustThisFrame;

        public Vector3 Velocity => m_Velocity;
        public float Speed => m_Velocity.magnitude;
        public bool IsAnchored => m_Driver != null;
        /// <summary>Total speed gained from swimming strokes since start (m/s). Used by the tutorial.</summary>
        public float SwimSpeedGained { get; private set; }
        /// <summary>Total speed gained from the suit thruster since start (m/s).</summary>
        public float ThrusterSpeedGained { get; private set; }
        public bool IsThrusting { get; private set; }
        public bool IsBraking { get; private set; }
        /// <summary>0..1, how hard the player is working (strokes, thrust). Raises oxygen use and breathing.</summary>
        public float Exertion { get; private set; }

        /// <summary>A hand took hold of a surface. Argument: body speed at that moment.</summary>
        public event Action<float> Grabbed;
        /// <summary>The last hand let go. Argument: resulting drift speed.</summary>
        public event Action<float> PushedOff;
        public event Action Stroked;
        /// <summary>An open hand pushed or slapped a surface. Argument: speed of the shove.</summary>
        public event Action<float> WallPushed;

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

        /// <summary>Loses speed at once (caught in a web). 0 = stopped dead, 1 = no effect.</summary>
        public void Damp(float keep) => m_Velocity *= Mathf.Clamp01(keep);

        public void AddImpulse(Vector3 velocityChange)
        {
            m_Velocity = Vector3.ClampMagnitude(m_Velocity + velocityChange, maxSpeed);
        }

        /// <summary>
        /// Mass of the suited person (kg): the mission's figure when there is one. Throws, shoves and the
        /// thruster are all worked out against this plus whatever is in the hands.
        /// </summary>
        public float BodyMass
        {
            get
            {
                var director = TrashAstronauts.Mission.MissionDirector.Active;
                return director != null && director.Config != null ? Mathf.Max(20f, director.Config.playerMass) : RealPhysics.PlayerBodyMass;
            }
        }

        /// <summary>Mass of what this player's hands are holding right now (kg). Something held in both hands counts once.</summary>
        public float CarriedMass
        {
            get
            {
                Rigidbody first = HeldBody(leftHand);
                Rigidbody second = HeldBody(rightHand);
                float mass = first != null ? first.mass : 0f;
                if (second != null && second != first) mass += second.mass;
                return mass;
            }
        }

        public float TotalMass => BodyMass + CarriedMass;

        static Rigidbody HeldBody(HandRig hand)
        {
            if (hand == null || hand.interactor == null || !hand.interactor.hasSelection) return null;
            var selected = hand.interactor.interactablesSelected;
            return selected.Count > 0 && selected[0] != null ? selected[0].transform.GetComponent<Rigidbody>() : null;
        }

        /// <summary>
        /// Newton's third law: something pushed on the player with this impulse (kg m/s), so the player
        /// (and whatever they carry) changes speed by impulse / total mass.
        /// </summary>
        public void AddMomentum(Vector3 impulse)
        {
            if (m_Driver != null) return; // holding on to the ship: the hull takes it
            AddImpulse(impulse / Mathf.Max(1f, TotalMass));
        }

        void Update()
        {
            if (mediator == null || mediator.xrOrigin == null || mediator.xrOrigin.Origin == null) return;
            Transform origin = mediator.xrOrigin.Origin.transform;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            Vector3 anchoredMotion = Vector3.zero;
            m_ThrustThisFrame = false;
            bool thrusting = false;
            bool braking = false;

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
                    // Grip paddling: any direction.
                    if (handVelocity.magnitude >= swimMinHandSpeed) ApplyStroke(-handVelocity * (swimStrength * dt));
                }
                else if (openHandSwim && !state.gripHeld && !state.anchored && !state.touchingWall && !state.hand.IsHolding
                    && handVelocity.magnitude >= openSwimMinHandSpeed)
                {
                    // Open-hand stroke: drop any part that would push the player backward relative to
                    // their view, so the recovery stroke and reaching forward do not cancel the swim.
                    Vector3 gain = -handVelocity * (openSwimStrength * dt);
                    if (head != null)
                    {
                        float backward = Vector3.Dot(gain, head.forward);
                        if (backward < 0f) gain -= head.forward * backward;
                    }
                    if (gain.sqrMagnitude > 1e-8f) ApplyStroke(gain);
                }

                if (m_Driver == null) anchoredMotion += SolidHand(state, palm, handVelocity);
                thrusting |= UpdateThruster(state, dt);
                braking |= state.hand.ThumbstickClickHeld;
            }
            IsThrusting = thrusting;
            // The thruster hisses. It is quiet, but something with good ears will notice.
            if (thrusting)
            {
                m_ThrusterNoiseTimer -= dt;
                if (m_ThrusterNoiseTimer <= 0f)
                {
                    m_ThrusterNoiseTimer = 0.9f;
                    var director = TrashAstronauts.Mission.MissionDirector.Active;
                    if (director != null && director.noise != null)
                    {
                        // Attributed to whoever wears this rig (its co-op slot), not to "player zero".
                        var wearer = leftHand != null && leftHand.Player != null ? leftHand.Player
                            : rightHand != null ? rightHand.Player : null;
                        director.noise.Emit(new TrashAstronauts.Core.NoiseEvent(transform.position + Vector3.up * 1.2f, thrusterLoudness,
                            TrashAstronauts.Core.NoiseKind.Thruster, wearer != null ? wearer.Id : director.LocalPlayerId));
                    }
                }
            }
            else m_ThrusterNoiseTimer = 0f;
            IsBraking = braking;
            if (braking && m_Driver == null)
            {
                m_Velocity = Vector3.MoveTowards(m_Velocity, Vector3.zero, brakeDeceleration * dt);
                m_ThrustThisFrame = m_Velocity.sqrMagnitude > 1e-6f;
            }
            Exertion = Mathf.MoveTowards(Exertion, m_ThrustThisFrame ? 1f : 0f, dt * (m_ThrustThisFrame ? 1.2f : 0.35f));
            anchoredMotion = Vector3.ClampMagnitude(anchoredMotion, maxPushPerFrame);

            if (m_Driver != null)
            {
                m_Velocity = Vector3.zero;
            }
            else
            {
                float k = momentumEnabled ? drag : comfortDrag;
                m_Velocity *= Mathf.Exp(-k * dt);
                // In a map with gravity (the lava tube) the drift also falls, gently, and rests on the ground.
                Vector3 gravity = TrashAstronauts.Mission.MapGravity.GravityAt(origin.position);
                bool falling = gravity != Vector3.zero && TrashAstronauts.Mission.MapGravity.StepPlayer(ref m_Velocity, origin.position, gravity, dt);
                // Come to rest once nearly stopped, but never while a stroke or shove is adding speed:
                // each frame of a stroke adds only a little, and zeroing it here would stop swimming
                // from ever building up (this was why swimming did nothing on the headset).
                if (!m_ThrustThisFrame && !falling && m_Velocity.sqrMagnitude < 0.015f * 0.015f) m_Velocity = Vector3.zero;
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

        /// <summary>
        /// Suit thruster: an empty hand that is not on a surface or a button, with the trigger held,
        /// pushes the player the way the hand points. Gentle, capped, and usable with hand tracking (pinch).
        /// </summary>
        bool UpdateThruster(HandState state, float dt)
        {
            var hand = state.hand;
            bool firing = thrusterEnabled && !state.anchored && !state.touchingWall && !hand.IsHolding && !hand.IsPointingAtButton
                && hand.Trigger >= thrusterTriggerThreshold && m_Driver == null;

            if (hand.thrusterVisual != null && hand.thrusterVisual.activeSelf != firing) hand.thrusterVisual.SetActive(firing);
            if (hand.thrusterAudio != null && Application.isPlaying)
            {
                if (firing && !hand.thrusterAudio.isPlaying) hand.thrusterAudio.Play();
                else if (!firing && hand.thrusterAudio.isPlaying) hand.thrusterAudio.Stop();
            }
            if (!firing) return false;

            Vector3 direction = hand.transform.forward;
            float along = Vector3.Dot(m_Velocity, direction);
            if (along < thrusterMaxSpeed)
            {
                Vector3 before = m_Velocity;
                // Same force, more mass, less acceleration: hauling the reactor core is sluggish.
                float acceleration = thrusterAcceleration * BodyMass / Mathf.Max(1f, TotalMass);
                float gain = Mathf.Min(acceleration * dt, thrusterMaxSpeed - along);
                m_Velocity = Vector3.ClampMagnitude(m_Velocity + direction * gain, maxSpeed);
                ThrusterSpeedGained += (m_Velocity - before).magnitude;
            }
            m_ThrustThisFrame = true;
            hand.Pulse(0.08f, 0.03f);
            return true;
        }

        void ApplyStroke(Vector3 gain)
        {
            Vector3 before = m_Velocity;
            m_Velocity = Vector3.ClampMagnitude(m_Velocity + gain, maxSpeed);
            SwimSpeedGained += (m_Velocity - before).magnitude;
            m_ThrustThisFrame = true;
            Stroked?.Invoke();
        }

        /// <summary>
        /// Keeps a hand from passing through the ship. If the palm is beyond a surface, the body is moved
        /// back by that much (so the hand stays on the surface) and picks up the speed of the shove.
        /// Returns the body motion needed this frame.
        /// </summary>
        Vector3 SolidHand(HandState state, Vector3 palm, Vector3 handVelocity)
        {
            if (head == null || state.anchored) { state.touchingWall = false; return Vector3.zero; }
            if (!TryGetPenetration(head, palm, handRadius, out Vector3 correction, out Vector3 normal))
            {
                state.touchingWall = false;
                return Vector3.zero;
            }

            // How fast the hand was driving into the surface (relative to the body).
            float approach = Mathf.Max(0f, Vector3.Dot(handVelocity, -normal));
            float current = Vector3.Dot(m_Velocity, normal);
            if (current < approach)
            {
                m_Velocity = Vector3.ClampMagnitude(m_Velocity + normal * ((approach - current) * pushOffScale), maxSpeed);
                m_ThrustThisFrame = true;
            }

            if (!state.touchingWall)
            {
                state.touchingWall = true;
                state.hand.Pulse(Mathf.Clamp01(0.15f + approach * 0.25f), 0.04f);
                if (approach > 0.4f && Sfx.Library != null)
                    Sfx.PlayAt(Sfx.Library.impactLight, palm, Mathf.Clamp01(approach * 0.25f), 0.8f, 8f);
                WallPushed?.Invoke(approach);
            }
            return correction;
        }

        /// <summary>
        /// Checks the arm from the chest to the palm. If a surface is in the way, returns how far the
        /// palm must move back along the surface normal to sit on it. Shared with the glove visuals.
        /// </summary>
        public static bool TryGetPenetration(Transform head, Vector3 palm, float radius, out Vector3 correction, out Vector3 normal)
        {
            correction = Vector3.zero;
            normal = Vector3.up;
            Vector3 chest = head.position + Vector3.down * 0.2f;
            Vector3 to = palm - chest;
            float distance = to.magnitude;
            // Only within arm's reach: a tracking glitch that throws a hand across the room must not move the body.
            if (distance < 0.08f || distance > 1.4f) return false;
            Vector3 dir = to / distance;
            if (!Physics.SphereCast(chest, radius, dir, out RaycastHit hit, distance, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                return false;
            if (hit.distance <= 0.001f) return false; // started inside something: nothing sensible to do

            Vector3 allowed = chest + dir * hit.distance;
            float depth = Vector3.Dot(allowed - palm, hit.normal);
            if (depth <= 0.0005f) return false;
            normal = hit.normal;
            correction = hit.normal * depth;
            return true;
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
            if (into >= 0f) return;
            // Landing on the ground under gravity: no bounce, you just come down on your feet.
            Vector3 gravity = TrashAstronauts.Mission.MapGravity.GravityAt(hit.point);
            bool landing = gravity != Vector3.zero && Vector3.Dot(hit.normal, -gravity.normalized) > 0.6f;
            m_Velocity -= hit.normal * (into * (1f + (landing ? 0f : bounce)));

            // A solid bump: a thud and a tap on both hands, scaled by how hard the hull was hit.
            float impact = -into;
            if (impact > 0.6f && Time.time - m_LastBump > 0.25f)
            {
                m_LastBump = Time.time;
                if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.impactHeavy, hit.point, Mathf.Clamp01(impact * 0.2f), 0.9f, 10f);
                if (leftHand != null) leftHand.Pulse(Mathf.Clamp01(impact * 0.2f), 0.06f);
                if (rightHand != null) rightHand.Pulse(Mathf.Clamp01(impact * 0.2f), 0.06f);
            }
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

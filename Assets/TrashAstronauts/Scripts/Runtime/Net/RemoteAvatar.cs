using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Player;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// Another crew member as this headset sees them: the full space suit with the helmet on, in their slot's
    /// colour, two gloves whose fingers curl from the grip and trigger values they stream, and a kinematic capsule
    /// on the Player layer (so a door will not close on them). Built by the Builder as its own prefab (no camera,
    /// no audio listener, no XR origin, no interactor, nothing on the Tool or Salvage layers); NetSession makes
    /// one per remote slot and destroys it when that player leaves.
    ///
    /// Samples go into a <see cref="SnapshotBuffer{T}"/> and are drawn about 100 ms in the past, so there are
    /// normally two samples to blend between. The rules (safe zone, extraction, the creature's senses) use the
    /// newest sample instead, through <see cref="PlayerContext.HeadPosition"/>.
    /// </summary>
    [DefaultExecutionOrder(100)] // after PlayerAvatar.LateUpdate: a downed suit is re-posed and laid down here
    public sealed class RemoteAvatar : MonoBehaviour
    {
        public PlayerContext context;
        public PlayerAvatar suit;
        public Transform head;
        public HandRig leftHand;
        public HandRig rightHand;
        public GloveVisual leftGlove;
        public GloveVisual rightGlove;
        [Tooltip("Kinematic capsule on the Player layer, kept under the head.")]
        public Rigidbody body;
        [Tooltip("How far below the head the capsule's centre hangs (metres).")]
        public float bodyDrop = 0.85f;
        [Tooltip("Visual-only copies of the four suit tools (in hand or holstered) and their effects.")]
        public RemoteToolRig tools;
        [Tooltip("Co-op revive: the glowing handle on the chest pack, shown while this player is downed.")]
        public ReviveBeacon beacon;
        [Tooltip("Where the beacon sits on the torso (torso space: the neck is the origin, +z is the chest).")]
        public Vector3 beaconOffset = new Vector3(0f, -0.22f, 0.27f);
        /// <summary>A downed suit in zero-g curls this far forward about its head (degrees): limp, not standing.</summary>
        public const float ZeroGCurlDegrees = 32f;

        /// <summary>A downed suit in a gravity map lies this high (its back) off the floor, its chest over where its head was caught.</summary>
        public const float LyingPivotHeight = 0.16f;
        /// <summary>How far along the lying body (from the feet) its chest is; the body is laid so its chest is over the catch point.</summary>
        public const float LyingChestFromFeet = 1.1f;

        bool m_Downed;
        bool m_Lying;
        Vector3 m_DownedAt;
        Vector3 m_Floor;
        float m_DownedYaw;
        PlayerContext m_Hooked;

        /// <summary>True while this suit is shown downed (slumped in zero-g, lying on the floor under gravity).</summary>
        public bool ShownDowned => m_Downed;
        /// <summary>True while the downed suit is lying on a floor (a gravity map) rather than hanging limp.</summary>
        public bool Lying => m_Downed && m_Lying;
        /// <summary>Where the revive beacon is now (the chest of the suit, standing or lying).</summary>
        public Vector3 BeaconPosition => beacon != null ? beacon.transform.position
            : suit != null && suit.torso != null ? suit.torso.TransformPoint(beaconOffset)
            : head != null ? head.position + Vector3.down * 0.45f : transform.position;

        /// <summary>A head that moves further than this between two consecutive samples teleported: snap, do not glide.</summary>
        public const float TeleportDistance = 1f;

        SnapshotBuffer<AvatarSnapshot> m_Buffer;
        bool m_Shown = true;
        bool m_SnapPending;

        /// <summary>How many times the stand-in snapped instead of gliding (a teleport).</summary>
        public int Snaps { get; private set; }

        /// <summary>This player's crew slot (0 = the host).</summary>
        public int Slot => context != null ? context.Id.Value : -1;
        /// <summary>Samples waiting in the interpolation buffer.</summary>
        public int BufferedSamples => m_Buffer != null ? m_Buffer.Count : 0;
        /// <summary>True once a sample has been drawn: until then the suit is hidden (it would stand at the origin).</summary>
        public bool HasPose { get; private set; }
        /// <summary>The state drawn last (the interpolated one).</summary>
        public AvatarSnapshot Drawn { get; private set; }
        public Color Tint => suit != null ? suit.Tint : Color.white;

        /// <summary>
        /// Makes this the stand-in for crew slot <paramref name="id"/>. Call straight after Instantiate, before the
        /// first Start, so the director registers it as remote (and so never pushes this headset's profile onto it).
        /// </summary>
        public void Setup(PlayerId id, byte handedness)
        {
            if (context != null) context.AssignId(id, true);
            SetHandedness(handedness);
            // The slot's own colour, suit and gloves; NetSession's AddCrew then puts on the colour that player chose.
            CrewLook.ApplyLook(this, id.Value, SuitLook.Default);
            SetShown(false);
        }

        public void SetHandedness(byte handedness)
        {
            if (context != null) context.SetHandedness(handedness != 0 ? Handedness.Left : Handedness.Right);
        }

        /// <summary>
        /// A sample stamped with the host's clock. The newest one also becomes the player's head for the rules.
        /// </summary>
        public void Push(double time, in AvatarSnapshot sample)
        {
            if (m_Buffer == null) m_Buffer = new SnapshotBuffer<AvatarSnapshot>(32);
            if (m_Buffer.TryGetNewest(out AvatarSnapshot previous, out double previousTime) && time > previousTime
                && NetVec3.Distance(previous.headPosition, sample.headPosition) > TeleportDistance)
            {
                // A teleport (or the hub guard putting them back): drop the old samples so the suit jumps there
                // instead of sliding across the tug, and let the torso and legs start square again.
                m_Buffer.Clear();
                m_SnapPending = true;
            }
            if (!m_Buffer.Push(time, in sample)) return;
            if (context != null && m_Buffer.TryGetNewest(out AvatarSnapshot newest, out double _))
            {
                context.SetRemoteHead(newest.headPosition.ToUnity());
                context.RemoteExertion = newest.exertion;
            }
        }

        void OnEnable()
        {
            if (context != null && m_Hooked == null)
            {
                m_Hooked = context;
                context.DownedChanged += OnDownedChanged;
                if (context.IsDowned) OnDownedChanged(context, true);
            }
        }

        void OnDisable()
        {
            if (m_Hooked != null) m_Hooked.DownedChanged -= OnDownedChanged;
            m_Hooked = null;
        }

        void OnDownedChanged(PlayerContext player, bool downed)
        {
            if (downed)
            {
                m_Downed = true;
                m_DownedAt = player.DownedPosition;
                Vector3 forward = head != null ? head.forward : transform.forward;
                forward.y = 0f;
                m_DownedYaw = forward.sqrMagnitude > 1e-4f ? Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg : 0f;
                m_Lying = Mission.MapGravity.GravityAt(m_DownedAt) != Vector3.zero;
                m_Floor = m_DownedAt + Vector3.down * 1.5f;
                if (m_Lying && Physics.Raycast(m_DownedAt + Vector3.up * 0.2f, Vector3.down, out RaycastHit hit, 4f, World.GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                    m_Floor = hit.point;
                if (tools != null) tools.Apply(AvatarSnapshot.Default); // nothing in the gloves, every tool holstered
                SetThrusters(false);
                if (!HasPose)
                {
                    // Downed before any sample (a late joiner): show the suit where it lies.
                    HasPose = true;
                    SetShown(true);
                }
                if (suit != null) suit.SnapToHead();
                PoseDowned();
            }
            else
            {
                m_Downed = false;
                // Up again where they lay: the next sample stands the suit up, square under the head.
                m_SnapPending = true;
                if (m_Buffer != null && m_Buffer.TryGetNewest(out AvatarSnapshot newest, out double _)) Apply(in newest);
            }
            if (beacon != null) beacon.Show(downed);
        }

        void SetThrusters(bool on)
        {
            if (leftHand != null && leftHand.thrusterVisual != null) leftHand.thrusterVisual.SetActive(on);
            if (rightHand != null && rightHand.thrusterVisual != null) rightHand.thrusterVisual.SetActive(on);
        }

        /// <summary>Takes up the player's downed state now (previews in the editor, where OnEnable does not run) and poses it.</summary>
        public void ApplyDownedState()
        {
            if (context != null && context.IsDowned != m_Downed) OnDownedChanged(context, context.IsDowned);
            LateUpdate();
        }

        void LateUpdate()
        {
            if (m_Downed) PoseDowned();
            if (beacon != null && suit != null && suit.torso != null)
            {
                beacon.transform.SetPositionAndRotation(suit.torso.TransformPoint(beaconOffset), suit.torso.rotation);
                if (m_Downed && context != null) beacon.SetProgress(context.ReviveProgress);
            }
        }

        /// <summary>
        /// The downed pose, every frame (the stream is ignored while down). Zero-g: the suit hangs limp where its head was
        /// caught, head dropped forward, arms loose. Gravity: the same limp suit laid on its back on the floor, chest over
        /// the catch point. The head (with the voice and the talk light on it) goes wherever the helmet goes.
        /// </summary>
        void PoseDowned()
        {
            if (head == null) return;
            Quaternion yaw = Quaternion.Euler(0f, m_DownedYaw, 0f);
            Vector3 headPosition = m_Lying ? m_Floor + Vector3.up * 1.5f : m_DownedAt;
            Quaternion headRotation = yaw * (m_Lying ? Quaternion.Euler(12f, 0f, 0f) : Quaternion.Euler(38f, 0f, 9f));
            head.SetPositionAndRotation(headPosition, headRotation);
            PoseLimpHand(leftHand, headPosition + yaw * new Vector3(-0.23f, -0.74f, 0.16f), yaw);
            PoseLimpHand(rightHand, headPosition + yaw * new Vector3(0.23f, -0.76f, 0.12f), yaw);
            if (suit != null) suit.Pose(0f);

            Vector3 chest = suit != null && suit.torso != null ? suit.torso.TransformPoint(beaconOffset) : headPosition + Vector3.down * 0.45f;
            Quaternion bodyRotation = yaw;
            if (!m_Lying)
            {
                // Hanging limp: the whole body curls forward under the dropped head (the head stays where it was caught).
                Quaternion curl = Quaternion.AngleAxis(-ZeroGCurlDegrees, yaw * Vector3.right);
                Vector3 none = Vector3.zero;
                if (suit != null)
                {
                    Lay(suit.torso, curl, headPosition, none);
                    Lay(suit.legs, curl, headPosition, none);
                    Lay(suit.leftUpperArm, curl, headPosition, none);
                    Lay(suit.leftForearm, curl, headPosition, none);
                    Lay(suit.rightUpperArm, curl, headPosition, none);
                    Lay(suit.rightForearm, curl, headPosition, none);
                }
                Lay(leftHand != null ? leftHand.transform : null, curl, headPosition, none);
                Lay(rightHand != null ? rightHand.transform : null, curl, headPosition, none);
                chest = curl * (chest - headPosition) + headPosition;
                bodyRotation = curl * yaw;
            }
            else
            {
                // Laid on its back: everything turns about the feet, then slides so the chest is over the catch point.
                Vector3 forward = yaw * Vector3.forward;
                Quaternion down = Quaternion.FromToRotation(Vector3.up, -forward);
                Vector3 pivot = m_Floor + Vector3.up * LyingPivotHeight;
                Vector3 shift = forward * LyingChestFromFeet;
                if (suit != null)
                {
                    Lay(suit.helmet, down, pivot, shift);
                    Lay(suit.torso, down, pivot, shift);
                    Lay(suit.legs, down, pivot, shift);
                    Lay(suit.leftUpperArm, down, pivot, shift);
                    Lay(suit.leftForearm, down, pivot, shift);
                    Lay(suit.rightUpperArm, down, pivot, shift);
                    Lay(suit.rightForearm, down, pivot, shift);
                }
                Lay(head, down, pivot, shift);
                Lay(leftHand != null ? leftHand.transform : null, down, pivot, shift);
                Lay(rightHand != null ? rightHand.transform : null, down, pivot, shift);
                chest = down * (chest - pivot) + pivot + shift;
                bodyRotation = down * yaw;
            }
            if (body != null) body.transform.SetPositionAndRotation(chest, bodyRotation);
        }

        static void Lay(Transform t, Quaternion rotation, Vector3 pivot, Vector3 shift)
        {
            if (t == null) return;
            t.SetPositionAndRotation(rotation * (t.position - pivot) + pivot + shift, rotation * t.rotation);
        }

        static void PoseLimpHand(HandRig hand, Vector3 position, Quaternion yaw)
        {
            if (hand == null) return;
            hand.transform.SetPositionAndRotation(position, yaw * Quaternion.Euler(75f, 0f, 0f));
            hand.TriggerOverride = 0.2f;
            hand.GripOverride = 0.35f;
        }

        void Start()
        {
            // Made by a NetSession: weld progress starts flowing between this headset and the crew.
            var session = GetComponentInParent<NetSession>();
            if (session != null) CoopToolSync.Attach(session);
        }

        /// <summary>Draws the player as they were at <paramref name="renderTime"/> (host clock). Does nothing before the first sample.</summary>
        public void Render(double renderTime)
        {
            // A downed player's own movements are not drawn: the suit lies (or hangs) where they were caught.
            if (m_Downed) return;
            if (m_Buffer == null || !m_Buffer.Sample(renderTime, out AvatarSnapshot sample)) return;
            Apply(in sample);
        }

        /// <summary>Poses head, hands, finger curls, thruster flames and the tools from one sample.</summary>
        public void Apply(in AvatarSnapshot s)
        {
            Drawn = s;
            // The stream says which hand is dominant (it can change in that player's settings at any time).
            bool leftHanded = s.HasFlag(AvatarFlags.LeftHanded);
            if (context != null && (context.Handedness == Handedness.Left) != leftHanded) SetHandedness(leftHanded ? (byte)1 : (byte)0);
            if (head != null) head.SetPositionAndRotation(s.headPosition.ToUnity(), s.headRotation.ToUnity());
            ApplyHand(leftHand, s.leftHandPosition, s.leftHandRotation, s.leftIndexCurl, s.leftGripCurl, s.HasFlag(AvatarFlags.ThrusterLeft));
            ApplyHand(rightHand, s.rightHandPosition, s.rightHandRotation, s.rightIndexCurl, s.rightGripCurl, s.HasFlag(AvatarFlags.ThrusterRight));

            if (tools != null) tools.Apply(in s);

            if (body != null && head != null)
            {
                Vector3 forward = head.forward;
                forward.y = 0f;
                Quaternion yaw = forward.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(forward, Vector3.up) : body.transform.rotation;
                body.transform.SetPositionAndRotation(head.position + Vector3.down * bodyDrop, yaw);
            }

            if (m_SnapPending)
            {
                m_SnapPending = false;
                Snaps++;
                if (suit != null) suit.SnapToHead();
            }
            if (!HasPose)
            {
                HasPose = true;
                SetShown(true);
                if (suit != null) suit.SnapToHead();
            }
        }

        static void ApplyHand(HandRig hand, NetVec3 position, NetQuat rotation, float index, float grip, bool thrusting)
        {
            if (hand == null) return;
            hand.transform.SetPositionAndRotation(position.ToUnity(), rotation.ToUnity());
            // GloveVisual reads these exactly as it reads a tracked hand's pinch and grab.
            hand.TriggerOverride = index;
            hand.GripOverride = grip;
            if (hand.thrusterVisual != null && hand.thrusterVisual.activeSelf != thrusting) hand.thrusterVisual.SetActive(thrusting);
        }

        void SetShown(bool shown)
        {
            if (m_Shown == shown) return;
            m_Shown = shown;
            if (suit != null) suit.gameObject.SetActive(shown);
            if (leftGlove != null) leftGlove.gameObject.SetActive(shown);
            if (rightGlove != null) rightGlove.gameObject.SetActive(shown);
        }
    }
}

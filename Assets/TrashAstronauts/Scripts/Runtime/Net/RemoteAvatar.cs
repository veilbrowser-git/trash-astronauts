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

        void Start()
        {
            // Made by a NetSession: weld progress starts flowing between this headset and the crew.
            var session = GetComponentInParent<NetSession>();
            if (session != null) CoopToolSync.Attach(session);
        }

        /// <summary>Draws the player as they were at <paramref name="renderTime"/> (host clock). Does nothing before the first sample.</summary>
        public void Render(double renderTime)
        {
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

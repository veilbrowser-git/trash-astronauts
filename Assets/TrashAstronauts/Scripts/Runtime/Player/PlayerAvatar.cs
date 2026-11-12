using UnityEngine;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// The visible space suit: helmet, torso, two arms and legs, posed every frame from just three points,
    /// the head and the two hands. It knows nothing about controllers, input or the XR rig, so the same
    /// component dresses the local player (sources: the camera and the tracked hands) and a remote crew
    /// member (sources: three transforms moved by the network).
    ///
    /// - The helmet follows the head exactly.
    /// - The torso hangs below the head, turned to where the head faces with a lazy follow, and leans only
    ///   a little when the head pitches (the same idea as BodyAnchor, so holsters and suit agree). With the
    ///   helmet shown it hangs from the centre of the helmet, so the neck ring stays seated however the head
    ///   nods; with the helmet hidden (the wearer's own view) it hangs from the eyes and keeps clear of them.
    /// - Each arm is a two-bone solve from a shoulder point on the torso to the wrist. The segments never
    ///   stretch: a hand out of reach leaves the arm straight, still on the shoulder, pointing at the hand.
    /// - The legs hang from the hips and trail a little behind movement.
    ///
    /// The model comes from Blender (ModelSource/Player/build_player.py) and is assembled by Builder.Avatar.cs.
    /// </summary>
    [DefaultExecutionOrder(200)] // after BodyAnchor and the glove scripts have settled this frame's pose
    public sealed class PlayerAvatar : MonoBehaviour
    {
        /// <summary>Accent colours for up to four crew members. Index 0 matches the orange on the gloves.</summary>
        public static readonly Color[] CrewColors =
        {
            new Color(0.9f, 0.4f, 0.1f),   // orange
            new Color(0.1f, 0.5f, 0.95f),  // blue
            new Color(0.2f, 0.78f, 0.25f), // green
            new Color(0.85f, 0.2f, 0.75f), // magenta
        };

        /// <summary>
        /// The suit colours a player can choose (the SUIT LOCKER). The four crew colours come first, in their order, so a
        /// slot's default colour is also palette entry [slot]. On the wire and in the save, colour c (1..12) means entry
        /// [c - 1] and 0 means "my slot's colour" (see Core.Net.SuitLook).
        /// </summary>
        public static readonly Color[] SuitColors =
        {
            CrewColors[0],                     // orange
            CrewColors[1],                     // blue
            CrewColors[2],                     // green
            CrewColors[3],                     // magenta
            new Color(0.86f, 0.1f, 0.12f),     // red
            new Color(0.98f, 0.8f, 0.08f),     // yellow
            new Color(0.08f, 0.8f, 0.82f),     // cyan
            new Color(0.5f, 0.24f, 0.92f),     // purple
            new Color(1f, 0.52f, 0.68f),       // pink
            new Color(0.62f, 0.92f, 0.12f),    // lime
            new Color(0.1f, 0.17f, 0.48f),     // navy
            new Color(0.16f, 0.16f, 0.18f),    // graphite
        };

        /// <summary>Names of <see cref="SuitColors"/>, for the panel and the crew list.</summary>
        public static readonly string[] SuitColorNames =
        {
            "ORANGE", "BLUE", "GREEN", "MAGENTA", "RED", "YELLOW", "CYAN", "PURPLE", "PINK", "LIME", "NAVY", "GRAPHITE",
        };

        /// <summary>How much of a chosen colour washes into the white fabric (0 = none, 1 = full colour).</summary>
        public const float FabricWash = 0.32f;

        /// <summary>The fabric colour for a suit colour: white with a light wash of the same hue (the atlas's dirt and seams still show).</summary>
        public static Color WashOf(Color color) => Color.Lerp(Color.white, color, FabricWash);

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");

        [Header("Sources: the only things the pose is read from. Any of them may be missing.")]
        public Transform head;
        public Transform leftHand;
        public Transform rightHand;
        [Tooltip("Optional. When set, the torso faces the way this transform faces instead of working its own facing out from the head. "
            + "The local player uses the Body Anchor here so the suit and the tool holsters turn as one. Leave empty for remote players.")]
        public Transform yawSource;

        [Header("Parts (assigned by the builder)")]
        public Transform helmet;
        public Transform torso;
        public Transform legs;
        [Tooltip("Arm segments: unit length along +Z from joint to joint, scaled to the segment length when posed.")]
        public Transform leftUpperArm;
        public Transform leftForearm;
        public Transform rightUpperArm;
        public Transform rightForearm;
        public Renderer[] helmetRenderers;
        public Renderer[] bodyRenderers;
        [Tooltip("The crew-colour pieces (helmet stripe, chest stripe, shoulder patches, arm and leg bands).")]
        public Renderer[] accentRenderers;
        [Tooltip("Suit pieces whose second material (index 1) is the white fabric and helmet shell, washed with a chosen suit colour. "
            + "Their first material (metal rings, rubber, boots, the pack) is never tinted.")]
        public Renderer[] fabricRenderers;

        [Header("Proportions (metres; from the model)")]
        [Tooltip("Centre of the head, in the head source's own space (the source is between the eyes).")]
        public Vector3 headCentreOffset = new Vector3(0f, 0f, -0.085f);
        [Tooltip("How far below the centre of the head the base of the neck (the torso's origin) is.")]
        public float neckDrop = 0.17f;
        [Tooltip("Right shoulder joint in the torso's space. The left one is mirrored.")]
        public Vector3 shoulderOffset = new Vector3(0.245f, -0.09f, 0f);
        [Tooltip("Hips (the legs' origin) in the torso's space.")]
        public Vector3 hipOffset = new Vector3(0f, -0.52f, 0f);
        public float upperArmLength = 0.31f;
        public float forearmLength = 0.27f;
        [Tooltip("Where the suit's wrist ring meets the glove, in the left hand source's own space.")]
        public Vector3 leftWristOffset = new Vector3(-0.035f, -0.035f, -0.18f);
        [Tooltip("Where the suit's wrist ring meets the glove, in the right hand source's own space.")]
        public Vector3 rightWristOffset = new Vector3(0.035f, -0.035f, -0.18f);

        [Header("Torso")]
        [Tooltip("The body turns to follow the head once they differ by more than this (when there is no yaw source).")]
        public float yawDeadzone = 30f;
        public float yawFollowSpeed = 220f;
        [Tooltip("Degrees the torso leans per degree of head pitch.")]
        public float leanPerDegree = 0.14f;
        public float maxLeanForward = 10f;
        public float maxLeanBack = 6f;
        [Tooltip("Only while the helmet is hidden (the wearer's own view). Looking down carries the eyes forward of the neck: the torso "
            + "sits this much further back when looking straight down, so the wearer can see past the chest to the hip holsters.")]
        public float lookDownShift = 0.045f;

        [Header("Arms")]
        [Tooltip("Which way the elbows bend, in the torso's space for the right arm (x = outward).")]
        public Vector3 elbowDirection = new Vector3(0.75f, -1f, -0.3f);
        [Tooltip("Where a wrist rests, from its shoulder, when that hand has no source (x = outward).")]
        public Vector3 restingWrist = new Vector3(0.07f, -0.5f, 0.14f);

        [Header("Legs")]
        [Tooltip("Degrees the legs swing back per metre per second of movement.")]
        public float legTrailDegrees = 9f;
        public float maxLegSwing = 26f;
        public float legSmoothing = 5f;

        [SerializeField] bool m_HelmetVisible = true;
        [SerializeField] bool m_BodyVisible = true;
        [SerializeField] Color m_Tint = new Color(0.9f, 0.4f, 0.1f);
        [SerializeField] Color m_Wash = Color.white;
        [SerializeField] int m_SuitColor = -1;

        MaterialPropertyBlock m_Block;
        bool m_Initialized;
        float m_Yaw;
        bool m_HasHips;
        Vector3 m_LastHips;
        Vector3 m_Velocity;
        // index 0 = left, 1 = right
        readonly Vector3[] m_Shoulder = new Vector3[2];
        readonly Vector3[] m_Elbow = new Vector3[2];
        readonly Vector3[] m_Wrist = new Vector3[2];
        readonly Vector3[] m_WristTarget = new Vector3[2];

        public bool HelmetVisible => m_HelmetVisible;
        public bool BodyVisible => m_BodyVisible;
        public Color Tint => m_Tint;
        /// <summary>The fabric's wash: white unless a suit colour was chosen with <see cref="SetSuitColor"/>.</summary>
        public Color FabricTint => m_Wash;
        /// <summary>The palette entry last set with <see cref="SetSuitColor"/>, or -1 after <see cref="SetTint"/> (a crew colour on the stripes only).</summary>
        public int SuitColorIndex => m_SuitColor;
        /// <summary>Longest distance from a shoulder that a wrist can be.</summary>
        public float ArmReach => upperArmLength + forearmLength;

        /// <summary>World position of a shoulder joint, as of the last pose.</summary>
        public Vector3 GetShoulder(bool left) => m_Shoulder[left ? 0 : 1];
        /// <summary>World position of an elbow, as of the last pose.</summary>
        public Vector3 GetElbow(bool left) => m_Elbow[left ? 0 : 1];
        /// <summary>World position of the wrist end of a forearm, as of the last pose.</summary>
        public Vector3 GetWrist(bool left) => m_Wrist[left ? 0 : 1];
        /// <summary>World position the wrist was reaching for in the last pose (the hand source plus its wrist offset).</summary>
        public Vector3 GetWristTarget(bool left) => m_WristTarget[left ? 0 : 1];

        void Awake()
        {
            ApplyTint();
        }

        void OnEnable()
        {
            SnapToHead();
        }

        void LateUpdate()
        {
            Pose(Time.unscaledDeltaTime);
        }

        /// <summary>
        /// Shows or hides the helmet, with the neck ring it locks into. The local player's own helmet is hidden:
        /// it would block the view. It also picks how the torso hangs: under the helmet when the helmet is shown,
        /// clear of the eyes when it is hidden (see <see cref="Pose"/>).
        /// </summary>
        public void SetHelmetVisible(bool visible)
        {
            m_HelmetVisible = visible;
            SetEnabled(helmetRenderers, visible);
        }

        /// <summary>Shows or hides everything below the helmet: torso, arms and legs.</summary>
        public void SetBodyVisible(bool visible)
        {
            m_BodyVisible = visible;
            SetEnabled(bodyRenderers, visible);
        }

        /// <summary>Colours the crew stripes and patches and leaves the fabric white. See <see cref="CrewColors"/>.</summary>
        public void SetTint(Color color)
        {
            m_Tint = color;
            m_Wash = Color.white;
            m_SuitColor = -1;
            ApplyTint();
        }

        /// <summary>
        /// Dresses the suit in palette entry <paramref name="paletteIndex"/> (0..11, see <see cref="SuitColors"/>): the stripes and
        /// patches at full colour and the fabric and helmet shell with a light wash of it. Out of range uses entry 0.
        /// </summary>
        public void SetSuitColor(int paletteIndex)
        {
            if (paletteIndex < 0 || paletteIndex >= SuitColors.Length) paletteIndex = 0;
            m_Tint = SuitColors[paletteIndex];
            m_Wash = WashOf(m_Tint);
            m_SuitColor = paletteIndex;
            ApplyTint();
        }

        /// <summary>Forgets the lazy torso facing and the leg swing, so the next pose starts square under the head. Call after a teleport.</summary>
        public void SnapToHead()
        {
            m_Initialized = false;
            m_HasHips = false;
            m_Velocity = Vector3.zero;
        }

        /// <summary>Poses the whole suit from the three sources. LateUpdate calls this; tests can step it by hand.</summary>
        public void Pose(float dt)
        {
            if (head == null) return;

            Vector3 headPosition = head.position;
            Quaternion headRotation = head.rotation;
            Vector3 headForward = headRotation * Vector3.forward;

            Vector3 headCentre = headPosition + headRotation * headCentreOffset;
            if (helmet != null) helmet.SetPositionAndRotation(headCentre, headRotation);

            // --- which way the body faces: follows the head's heading lazily, never its pitch or roll
            // Looking steeply down, "forward" has almost no horizontal part left and what is left swings wildly with the
            // slightest roll; the top of the head then points the way the body faces (and away from it when looking
            // steeply up). Blending the two gives a steady heading at every pitch.
            Vector3 headUp = headRotation * Vector3.up;
            float flatX = headForward.x - headUp.x * headForward.y;
            float flatZ = headForward.z - headUp.z * headForward.y;
            float headYaw = flatX * flatX + flatZ * flatZ > 0.0001f ? Mathf.Atan2(flatX, flatZ) * Mathf.Rad2Deg : m_Yaw;
            if (!m_Initialized)
            {
                m_Yaw = headYaw;
                m_Initialized = true;
            }
            if (yawSource != null)
            {
                Vector3 f = yawSource.forward;
                if (f.x * f.x + f.z * f.z > 0.0001f) m_Yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            }
            else
            {
                float delta = Mathf.DeltaAngle(m_Yaw, headYaw);
                if (Mathf.Abs(delta) > yawDeadzone)
                {
                    float target = headYaw - Mathf.Sign(delta) * yawDeadzone;
                    m_Yaw = Mathf.MoveTowardsAngle(m_Yaw, target, yawFollowSpeed * Mathf.Max(dt, 0f));
                }
            }
            Quaternion yawRotation = Quaternion.Euler(0f, m_Yaw, 0f);

            // --- torso: hangs from the head.
            float pitchDown = Mathf.Asin(Mathf.Clamp(-headForward.y, -1f, 1f)) * Mathf.Rad2Deg; // + looking down
            Vector3 neck;
            if (m_HelmetVisible)
            {
                // Seen from outside: the collar stays seated under the helmet whatever the head does. (The head source
                // is between the eyes, so a nodding head carries its centre forward and up; a torso hung from the eyes
                // instead would leave the helmet floating clear of the neck ring.)
                neck = headCentre + Vector3.down * neckDrop;
            }
            else
            {
                // Seen by the wearer (helmet hidden): the torso hangs from the eyes. Its height never comes closer to
                // them than the neck allows, however far the head pitches down, and it slides back a little, so the
                // camera can never end up inside the collar and the chest does not cover the hip holsters.
                float sinDown = Mathf.Max(0f, -headForward.y);
                float sinUp = Mathf.Max(0f, headForward.y);
                neck = headPosition
                    + yawRotation * new Vector3(headCentreOffset.x, 0f, headCentreOffset.z - lookDownShift * sinDown)
                    + Vector3.up * (headCentreOffset.y - neckDrop + headCentreOffset.z * sinUp);
            }
            float lean = Mathf.Clamp(pitchDown * leanPerDegree, -maxLeanBack, maxLeanForward);
            Quaternion torsoRotation = yawRotation * Quaternion.Euler(lean, 0f, 0f);
            if (torso != null) torso.SetPositionAndRotation(neck, torsoRotation);

            // --- arms
            PoseArm(0, -1f, leftUpperArm, leftForearm, leftHand, leftWristOffset, neck, torsoRotation);
            PoseArm(1, 1f, rightUpperArm, rightForearm, rightHand, rightWristOffset, neck, torsoRotation);

            // --- legs: hang from the hips and swing back against the direction of travel
            Vector3 hipPosition = neck + torsoRotation * hipOffset;
            if (m_HasHips && dt > 0.0001f)
            {
                Vector3 velocity = (hipPosition - m_LastHips) / dt;
                if (velocity.sqrMagnitude > 400f) velocity = Vector3.zero; // a teleport, not a movement
                m_Velocity = Vector3.Lerp(m_Velocity, velocity, 1f - Mathf.Exp(-legSmoothing * dt));
            }
            m_LastHips = hipPosition;
            m_HasHips = true;
            if (legs != null)
            {
                Vector3 local = Quaternion.Inverse(yawRotation) * m_Velocity;
                float swingBack = Mathf.Clamp(local.z * legTrailDegrees, -maxLegSwing, maxLegSwing);
                float swingSide = Mathf.Clamp(-local.x * legTrailDegrees, -maxLegSwing, maxLegSwing);
                legs.SetPositionAndRotation(hipPosition, torsoRotation * Quaternion.Euler(swingBack, 0f, swingSide));
            }
        }

        void PoseArm(int index, float side, Transform upper, Transform fore, Transform hand, Vector3 wristOffset, Vector3 neck, Quaternion torsoRotation)
        {
            Vector3 shoulder = neck + torsoRotation * new Vector3(shoulderOffset.x * side, shoulderOffset.y, shoulderOffset.z);
            Vector3 target = hand != null
                ? hand.TransformPoint(wristOffset)
                : shoulder + torsoRotation * new Vector3(restingWrist.x * side, restingWrist.y, restingWrist.z);

            Vector3 to = target - shoulder;
            float distance = to.magnitude;
            Vector3 direction = distance > 0.0001f ? to / distance : torsoRotation * Vector3.down;

            // Two-bone solve with fixed lengths. The reach is clamped, so a hand too far away (or on top of the
            // shoulder) leaves the arm attached and aimed at the hand rather than stretched or folded through itself.
            float a = upperArmLength, b = forearmLength;
            float reach = Mathf.Clamp(distance, Mathf.Abs(a - b) + 0.01f, a + b - 0.002f);
            float along = (a * a - b * b + reach * reach) / (2f * reach);
            float out2 = a * a - along * along;
            float outward = out2 > 0f ? Mathf.Sqrt(out2) : 0f;

            Vector3 pole = torsoRotation * new Vector3(elbowDirection.x * side, elbowDirection.y, elbowDirection.z);
            Vector3 bend = pole - direction * Vector3.Dot(pole, direction);
            if (bend.sqrMagnitude < 0.0001f)
            {
                // the hand is exactly along the elbow direction: any sideways bend will do
                pole = torsoRotation * new Vector3(side, 0f, 0f);
                bend = pole - direction * Vector3.Dot(pole, direction);
            }
            bend.Normalize();

            Vector3 elbow = shoulder + direction * along + bend * outward;
            Vector3 wrist = shoulder + direction * reach;
            m_Shoulder[index] = shoulder;
            m_Elbow[index] = elbow;
            m_Wrist[index] = wrist;
            m_WristTarget[index] = target;

            // The segment meshes run along +Z with the outside of the elbow on -Y.
            if (upper != null)
            {
                upper.SetPositionAndRotation(shoulder, Quaternion.LookRotation(elbow - shoulder, -bend));
                upper.localScale = new Vector3(1f, 1f, a);
            }
            if (fore != null)
            {
                fore.SetPositionAndRotation(elbow, Quaternion.LookRotation(wrist - elbow, -bend));
                fore.localScale = new Vector3(1f, 1f, b);
            }
        }

        void ApplyTint()
        {
            if (m_Block == null) m_Block = new MaterialPropertyBlock();
            if (accentRenderers != null)
            {
                for (int i = 0; i < accentRenderers.Length; i++)
                {
                    var r = accentRenderers[i];
                    if (r == null) continue;
                    r.GetPropertyBlock(m_Block);
                    m_Block.SetColor(k_BaseColor, m_Tint);
                    r.SetPropertyBlock(m_Block);
                }
            }
            if (fabricRenderers != null)
            {
                bool white = m_Wash == Color.white;
                for (int i = 0; i < fabricRenderers.Length; i++)
                {
                    var r = fabricRenderers[i];
                    if (r == null || r.sharedMaterials.Length < 2) continue;
                    if (white)
                    {
                        // Untinted fabric: no block at all, exactly as before suit colours existed.
                        if (r.HasPropertyBlock()) r.SetPropertyBlock(null, 1);
                        continue;
                    }
                    r.GetPropertyBlock(m_Block, 1);
                    m_Block.SetColor(k_BaseColor, m_Wash);
                    r.SetPropertyBlock(m_Block, 1);
                }
            }
        }

        static void SetEnabled(Renderer[] renderers, bool visible)
        {
            if (renderers == null) return;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null) renderers[i].enabled = visible;
            }
        }
    }
}

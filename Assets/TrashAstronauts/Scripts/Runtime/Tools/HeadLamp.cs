using System.Collections.Generic;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Tools
{
    /// <summary>
    /// The helmet lamp, bought once in the company store (<see cref="ShopItem.HeadLamp"/>). A wide, soft spot light in
    /// the lamp housing on the right of the helmet, parented to the camera so it follows the head exactly, with no
    /// smoothing and nothing that moves the view.
    ///
    /// It has its own switch and its own battery, with the hand flashlight's rules: it drains only during a contract
    /// outside the tug, tops up in the tug (or slowly while switched off), dims and stutters when low, and will not
    /// light when flat. Switch it with the LAMP button on the wrist, or by tapping the side of the helmet with a
    /// fingertip. Without the purchase none of that does anything and the wrist button is not there.
    ///
    /// During a jumpscare on this headset it goes dark with the flashlight and comes back with it; blackouts leave it
    /// alone, as they leave the flashlight. Creatures do not see light, so nothing here touches their senses.
    /// </summary>
    public sealed class HeadLamp : MonoBehaviour
    {
        public PlayerContext player;
        public Light beam;
        [Tooltip("The LAMP button on the wrist display. Shown only once the lamp is owned.")]
        public PushButton wristButton;

        [Header("Battery")]
        [Tooltip("Seconds of light on a full charge.")]
        public float batterySeconds = 360f;
        [Tooltip("Charges this many times faster than it drains, while switched off.")]
        public float rechargeRate = 1.5f;

        [Header("Helmet tap")]
        [Tooltip("Tap spot on the right of the helmet (the left one is its mirror), in the head's (camera's) space: x right, "
            + "y up, z forward. It spans the real headset's side and the virtual lamp housing, which sits a little further out.")]
        public Vector3 tapSpot = new Vector3(0.145f, 0.04f, -0.05f);
        [Tooltip("A fingertip this close to a tap spot is touching the helmet there.")]
        public float tapRadius = 0.065f;
        [Tooltip("The finger must move this far away again before another tap counts.")]
        public float tapReleaseRadius = 0.1f;
        [Tooltip("A tap is a quick touch: slower than this (m/s, relative to the head) is a hand resting by the helmet.")]
        public float tapMinSpeed = 0.15f;
        [Tooltip("Seconds after a switch before a tap can switch it again, so it cannot flicker.")]
        public float tapCooldown = 0.7f;

        /// <summary>Test seam: owned or not without touching the save. Null = the profile decides.</summary>
        public static bool? OwnedOverride;
        /// <summary>The layer the wearer's own suit and gloves are moved to while the lamp is lit, so it never glares on them.</summary>
        public const int WearerLayer = GameLayers.LocalSuit;

        static readonly List<HeadLamp> s_Live = new List<HeadLamp>();

        public bool IsOn { get; private set; }
        /// <summary>0 = flat, 1 = full.</summary>
        public float Charge { get; private set; } = 1f;
        /// <summary>Too low to light: it must recover a little first.</summary>
        public bool IsFlat => Charge < 0.05f;
        /// <summary>True while something outside the switch keeps it dark (the jumpscare).</summary>
        public bool Suppressed { get; private set; }
        /// <summary>Switch changes so far (tests).</summary>
        public int ToggleCount { get; private set; }

        public bool Owned
        {
            get
            {
                if (OwnedOverride.HasValue) return OwnedOverride.Value;
                var director = MissionDirector.Active;
                return director != null && director.Profile != null && director.Profile.Data.headLamp;
            }
        }

        float m_BaseIntensity = -1f;
        float m_NextToggle;
        bool m_WearerLayered;
        readonly TapState[] m_Taps = new TapState[2];

        struct TapState
        {
            public bool Inside;
            public bool HasLast;
            public Vector3 Last;
        }

        /// <summary>Test seam.</summary>
        public void SetCharge(float charge) => Charge = Mathf.Clamp01(charge);

        /// <summary>The lamp on this player's helmet, or null.</summary>
        public static HeadLamp For(PlayerContext player)
        {
            if (player == null) return null;
            for (int i = 0; i < s_Live.Count; i++)
                if (s_Live[i] != null && s_Live[i].player == player) return s_Live[i];
            return null;
        }

        /// <summary>True if this player's helmet lamp is switched on (what crew mates are told).</summary>
        public static bool IsOnFor(PlayerContext player)
        {
            var lamp = For(player);
            return lamp != null && lamp.IsOn;
        }

        void Awake()
        {
            if (beam != null)
            {
                m_BaseIntensity = beam.intensity;
                beam.enabled = false;
            }
        }

        void OnEnable()
        {
            if (!s_Live.Contains(this)) s_Live.Add(this);
            if (wristButton != null) wristButton.Pressed += OnWristButton;
        }

        void OnDisable()
        {
            s_Live.Remove(this);
            if (wristButton != null) wristButton.Pressed -= OnWristButton;
        }

        void OnWristButton(PushButton button, HandRig hand) => Toggle(hand);

        /// <summary>Flips the switch, with a click and a haptic tick on <paramref name="hand"/>. Does nothing unless owned.</summary>
        public bool Toggle(HandRig hand = null) => SetOn(!IsOn, true, hand);

        /// <summary>
        /// Switches the lamp. Refused (false) when it is not owned, or when lighting it on a flat battery (that gets
        /// a dull click and nothing else).
        /// </summary>
        public bool SetOn(bool on, bool feedback, HandRig hand = null)
        {
            if (on && !Owned) return false;
            if (on && IsFlat)
            {
                if (feedback && Sfx.Library != null) Sfx.PlayUi(Sfx.Library.flashlightClick, 0.45f, 0.6f);
                if (feedback && hand != null) hand.Pulse(0.15f, 0.02f);
                return false;
            }
            if (on != IsOn)
            {
                IsOn = on;
                ToggleCount++;
                m_NextToggle = Time.unscaledTime + tapCooldown;
                if (on) LayerTheWearer();
            }
            ApplyBeam();
            if (feedback)
            {
                if (hand != null) hand.Pulse(0.3f, 0.025f);
                if (Sfx.Library != null) Sfx.PlayUi(Sfx.Library.flashlightClick, 0.45f, on ? 1.25f : 1.05f);
            }
            return true;
        }

        void Update()
        {
            bool owned = Owned;
            if (!owned && IsOn) SetOn(false, false);
            if (wristButton != null && wristButton.gameObject.activeSelf != owned) wristButton.gameObject.SetActive(owned);
            if (owned && wristButton != null)
            {
                string label = IsOn ? "LAMP\nON" : IsFlat ? "LAMP\nFLAT" : "LAMP\nOFF";
                if (wristButton.Text != label) wristButton.SetLabel(label);
            }

            StepBattery(Time.deltaTime);

            if (owned && player != null)
            {
                float dt = Time.deltaTime;
                StepTap(0, player.leftHand, dt);
                StepTap(1, player.rightHand, dt);
            }
        }

        void LateUpdate()
        {
            Suppressed = JumpscareOnThisHeadset();
            ApplyBeam();
        }

        /// <summary>The battery, by the flashlight's rules. Public so tests can step it.</summary>
        public void StepBattery(float dt)
        {
            var director = MissionDirector.Active;
            bool aboard = director != null && director.Phase == MissionPhase.Active;
            bool inTug = !aboard;
            var wearer = player != null ? player : aboard ? director.LocalPlayer : null;
            if (aboard && wearer != null) inTug = wearer.InSafeZone;

            if (inTug) Charge = Mathf.MoveTowards(Charge, 1f, dt * 0.5f);                    // the tug tops it up in a couple of seconds
            else if (IsOn) Charge = Mathf.Max(0f, Charge - dt / Mathf.Max(10f, batterySeconds));
            else Charge = Mathf.Min(1f, Charge + dt * rechargeRate / Mathf.Max(10f, batterySeconds));

            if (IsOn && Charge <= 0f) SetOn(false, true);
        }

        void ApplyBeam()
        {
            if (beam == null) return;
            bool lit = IsOn && !Suppressed;
            if (beam.enabled != lit) beam.enabled = lit;
            if (!lit || m_BaseIntensity <= 0f) return;
            // Low on charge it dims and stutters, as the flashlight does: warning before it goes.
            float low = Mathf.InverseLerp(0f, 0.2f, Charge);
            float stutter = low < 1f && Mathf.PerlinNoise(Time.time * 9f, 0.7f) > 0.35f + low * 0.6f ? 0.25f : 1f;
            beam.intensity = m_BaseIntensity * Mathf.Lerp(0.45f, 1f, low) * stutter;
        }

        /// <summary>The creature is mid-jumpscare on the wearer of this headset: the flashlight is dark, so is this.</summary>
        bool JumpscareOnThisHeadset()
        {
            var director = MissionDirector.Active;
            if (director == null || player == null || player.IsRemote) return false;
            PlayerId me = player.Id;
            if (director.enemies != null)
            {
                foreach (var enemy in director.enemies)
                    if (enemy != null && enemy.IsScaring && enemy.CatchVictim.Equals(me)) return true;
            }
            var active = director.ActiveEnemy;
            return active != null && active.IsScaring && active.CatchVictim.Equals(me);
        }

        /// <summary>
        /// One hand's fingertip against the two tap spots by the temples. A tap is the fingertip arriving inside a spot,
        /// moving, with that hand empty; it switches the lamp once. The finger has to leave (past the release radius)
        /// before it can tap again, and nothing switches within <see cref="tapCooldown"/> of the last switch.
        /// </summary>
        void StepTap(int index, HandRig hand, float dt)
        {
            if (hand == null || hand.fingertip == null || player.head == null) return;
            StepTap(index, hand.fingertip.position, hand.IsHolding, dt, hand);
        }

        /// <summary>Test seam: one step of the tap detector for hand <paramref name="index"/> (0 left, 1 right).</summary>
        public bool StepTap(int index, Vector3 fingertipWorld, bool holding, float dt, HandRig hand = null)
        {
            if (player == null || player.head == null || index < 0 || index > 1) return false;
            ref TapState state = ref m_Taps[index];
            Vector3 local = player.head.InverseTransformPoint(fingertipWorld);
            Vector3 mirrored = new Vector3(-tapSpot.x, tapSpot.y, tapSpot.z);
            float distance = Mathf.Min(Vector3.Distance(local, tapSpot), Vector3.Distance(local, mirrored));
            float speed = state.HasLast && dt > 0f ? Vector3.Distance(local, state.Last) / dt : 0f;
            state.Last = local;
            state.HasLast = true;

            bool switched = false;
            if (!state.Inside && distance < tapRadius)
            {
                state.Inside = true;
                if (!holding && speed >= tapMinSpeed && Time.unscaledTime >= m_NextToggle && Owned)
                    switched = Toggle(hand);
            }
            else if (state.Inside && distance > tapReleaseRadius)
            {
                state.Inside = false;
            }
            return switched;
        }

        /// <summary>
        /// The lamp sits a hand's width from the eyes and would blaze on the wearer's own suit and gloves when they look
        /// down or hold their hands up. Their renderers move to a layer the lamp does not light (only renderers: nothing
        /// with a collider is touched). Done the first time it is lit, so a player without the lamp is not changed at all.
        /// </summary>
        void LayerTheWearer()
        {
            if (m_WearerLayered || player == null) return;
            m_WearerLayered = true;
            if (beam != null) beam.cullingMask &= ~(1 << WearerLayer);
            if (player.avatar != null) Layer(player.avatar.transform);
            foreach (var hand in new[] { player.leftHand, player.rightHand })
                if (hand != null)
                    foreach (var glove in hand.GetComponentsInChildren<GloveVisual>(true)) Layer(glove.transform);
        }

        static void Layer(Transform root)
        {
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var go = renderer.gameObject;
                if (go.GetComponent<Collider>() != null) continue;
                if (go.GetComponentInParent<SuitTool>(true) != null) continue; // a tool in the hand is lit like any tool
                go.layer = WearerLayer;
            }
        }

        /// <summary>
        /// A line for the wrist display about the lamp battery, once it is worth worrying about (as the flashlight's).
        /// Starts a new line unless the flashlight already has one, then it shares it.
        /// </summary>
        public static string WristNote(PlayerContext player)
        {
            var lamp = For(player);
            if (lamp == null || !lamp.Owned) return "";
            bool flat = lamp.IsFlat;
            if (!flat && lamp.Charge >= 0.3f) return "";
            bool torchNote = false;
            if (player.tools != null)
                foreach (var tool in player.tools)
                    if (tool is FlashlightTool torch) torchNote = torch.IsFlat || torch.Charge < 0.3f;
            string text = flat ? (torchNote ? "LAMP FLAT" : "LAMP FLAT: switch off to charge") : "LAMP " + Mathf.CeilToInt(lamp.Charge * 100f) + "%";
            return (torchNote ? "  " : "\n") + text;
        }
    }
}

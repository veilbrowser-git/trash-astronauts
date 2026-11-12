using System.Collections.Generic;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// The four suit tools on another crew member's stand-in, for looking at only. The Builder makes each copy from
    /// the same construction code as the real tool and then strips it: no collider, rigidbody, interactable, tool
    /// script or anything on the Tool or Salvage layers is left, so a copy can never be grabbed, collide, cut, scan,
    /// fire or make gameplay noise. What it can do is look and sound like the real one:
    ///
    /// - a tool sits in the glove that holds it (<see cref="AvatarSnapshot.leftTool"/> / rightTool), seated exactly
    ///   as the real tool sits in its owner's hand, and otherwise in its holster on that suit's torso (mirrored for a
    ///   left-handed player, as their own holsters are);
    /// - flashlight on: lit lens and a soft additive beam cone, plus a real spot light for only the nearest
    ///   <see cref="MaxRealLights"/> remote flashlights to this headset (chosen every frame with hysteresis);
    /// - cutter firing: the flame, a glow and the cutter loop from that hand (3D, occluded by walls); flame on a weld:
    ///   sparks at the tip and their crackle;
    /// - scanner in hand: its screen lights up; tranquilizer: <see cref="ShowTracer"/> draws a brief tracer.
    ///
    /// <see cref="RemoteAvatar"/> calls <see cref="Apply"/> with every drawn sample. Nothing here exists offline:
    /// it is only part of the RemoteCrew prefab.
    /// </summary>
    public sealed class RemoteToolRig : MonoBehaviour
    {
        /// <summary>Slots of the per-tool arrays: the ids of <see cref="AvatarSampler"/> (index 0 = no tool, unused).</summary>
        public const int Slots = 5;
        /// <summary>Real spot lights shared by all remote flashlights (Quest budget). The rest keep only the cone.</summary>
        public static int MaxRealLights = 2;
        /// <summary>A lit flashlight keeps its real light until another is this much closer (fraction of its distance).</summary>
        public const float LightHysteresis = 0.8f;
        /// <summary>Test seam: where this headset's eyes are, for the light budget. Null = the local player's head.</summary>
        public static Transform ViewerOverride;

        public RemoteAvatar avatar;
        [Tooltip("Under each remote glove, where the local rig's grab interactor holds a tool's grip.")]
        public Transform leftAttach;
        public Transform rightAttach;
        [Tooltip("Visual copies, indexed by tool id (1 flashlight, 2 scanner, 3 cutter, 4 tranquilizer).")]
        public Transform[] tools = new Transform[Slots];
        [Tooltip("Each tool's grip point in its own space (the real tool's grab attach point).")]
        public Vector3[] grips = new Vector3[Slots];
        [Tooltip("Holster sockets on the suit's torso, indexed by tool id.")]
        public Transform[] holsters = new Transform[Slots];
        [Tooltip("Socket positions in torso space for a right-handed and a left-handed player.")]
        public Vector3[] holsterRight = new Vector3[Slots];
        public Vector3[] holsterLeft = new Vector3[Slots];

        [Header("Flashlight")]
        public Renderer flashlightLens;
        public GameObject flashlightBeam;
        public Light flashlightSpot;

        [Header("Plasma cutter")]
        public Transform cutterTip;
        public GameObject cutterFlame;
        public GameObject cutterGlow;
        public ParticleSystem cutterSparks;
        public AudioSource cutterLoop;
        [Tooltip("Sparks per second while the flame is on a weld.")]
        public float sparkRate = 70f;

        [Header("Scanner")]
        public GameObject scannerScreenLit;
        public TMPro.TMP_Text scannerText;

        [Header("Tranquilizer")]
        public LineRenderer tranqTracer;
        public Transform tranqMuzzle;
        public float tracerSeconds = 0.12f;

        static readonly List<RemoteToolRig> s_Live = new List<RemoteToolRig>();
        static readonly RemoteToolRig[] s_Candidates = new RemoteToolRig[NetProtocol.MaxPlayers * 2];
        static readonly float[] s_Scores = new float[NetProtocol.MaxPlayers * 2];
        static int s_BudgetFrame = -1;
        static readonly int k_Emission = Shader.PropertyToID("_EmissionColor");

        readonly byte[] m_Holder = new byte[Slots]; // 0 holster, 1 left hand, 2 right hand
        bool m_Placed;
        bool m_LeftHandedPlaced;
        MaterialPropertyBlock m_Block;
        float m_SparkDebt;
        float m_SparkSoundTimer;
        float m_TracerUntil = -1f;

        /// <summary>Remote rigs alive now (for the light budget and tests).</summary>
        public static IReadOnlyList<RemoteToolRig> Live => s_Live;

        public bool FlashlightOn { get; private set; }
        public bool CutterFiring { get; private set; }
        public bool Cutting { get; private set; }
        public bool ScannerHeld { get; private set; }
        /// <summary>True while the cutter loop has been told to play (what is heard depends on the audio device).</summary>
        public bool LoopOn { get; private set; }
        /// <summary>True while this rig's flashlight has one of the real spot lights.</summary>
        public bool HasRealLight => flashlightSpot != null && flashlightSpot.enabled;
        /// <summary>Sparks emitted so far (tests).</summary>
        public int SparksEmitted { get; private set; }
        public bool TracerShown => tranqTracer != null && tranqTracer.enabled;

        /// <summary>The visual copy for a tool id, or null.</summary>
        public Transform Tool(byte id) => id > 0 && id < Slots ? tools[id] : null;

        /// <summary>Where a tool is drawn: 0 its holster, 1 the left glove, 2 the right glove.</summary>
        public int HolderOf(byte id) => id > 0 && id < Slots ? m_Holder[id] : 0;

        void OnEnable()
        {
            if (!s_Live.Contains(this)) s_Live.Add(this);
        }

        void OnDisable()
        {
            s_Live.Remove(this);
            SetRealLight(false);
            if (cutterLoop != null && cutterLoop.isPlaying) cutterLoop.Stop();
            LoopOn = false;
            CutterFiring = false;
            if (tranqTracer != null) tranqTracer.enabled = false;
        }

        /// <summary>Places the tools and switches their effects from one drawn sample.</summary>
        public void Apply(in AvatarSnapshot s)
        {
            bool first = !m_Placed;
            bool leftHanded = s.HasFlag(AvatarFlags.LeftHanded);
            if (!m_Placed || leftHanded != m_LeftHandedPlaced) PlaceHolsters(leftHanded);

            for (byte id = 1; id < Slots; id++)
            {
                // One tool, one hand. The right hand wins the (impossible) tie, as in the sampler's order.
                byte holder = s.rightTool == id ? (byte)2 : s.leftTool == id ? (byte)1 : (byte)0;
                if (!m_Placed || holder != m_Holder[id]) Seat(id, holder);
            }
            m_Placed = true;

            SetFlashlight(s.HasFlag(AvatarFlags.FlashlightOn), first);
            SetCutter(s.HasFlag(AvatarFlags.CutterFiring), s.HasFlag(AvatarFlags.Cutting));
            bool scanner = s.leftTool == AvatarSampler.Scanner || s.rightTool == AvatarSampler.Scanner;
            if (scanner != ScannerHeld || first)
            {
                ScannerHeld = scanner;
                if (scannerScreenLit != null) scannerScreenLit.SetActive(scanner);
                if (scannerText != null) scannerText.text = scanner ? "SCANNER\nACTIVE" : "SCANNER\nSTANDBY";
            }
        }

        void PlaceHolsters(bool leftHanded)
        {
            m_LeftHandedPlaced = leftHanded;
            Vector3[] positions = leftHanded ? holsterLeft : holsterRight;
            for (int id = 1; id < Slots; id++)
            {
                if (holsters[id] != null && positions != null && id < positions.Length) holsters[id].localPosition = positions[id];
            }
        }

        void Seat(byte id, byte holder)
        {
            m_Holder[id] = holder;
            Transform tool = tools[id];
            if (tool == null) return;
            Transform parent = holder == 1 ? leftAttach : holder == 2 ? rightAttach : holsters[id];
            if (parent == null) return;
            tool.SetParent(parent, false);
            // In a hand the grip point sits on the attach point, the tool turned as the hand (the real grab's attach
            // transform has no rotation of its own); in a holster the tool sits on the socket like a docked tool.
            tool.localRotation = Quaternion.identity;
            tool.localPosition = holder != 0 ? -grips[id] : Vector3.zero;
        }

        void SetFlashlight(bool on, bool force)
        {
            if (on == FlashlightOn && !force) return;
            FlashlightOn = on;
            if (flashlightBeam != null) flashlightBeam.SetActive(on);
            if (!on) SetRealLight(false);
            if (flashlightLens != null)
            {
                if (m_Block == null) m_Block = new MaterialPropertyBlock();
                flashlightLens.GetPropertyBlock(m_Block);
                m_Block.SetColor(k_Emission, on ? new Color(3f, 2.8f, 2.2f) : Color.black);
                flashlightLens.SetPropertyBlock(m_Block);
            }
        }

        void SetCutter(bool firing, bool cutting)
        {
            cutting &= firing;
            Cutting = cutting;
            if (firing == CutterFiring) return;
            CutterFiring = firing;
            if (cutterFlame != null) cutterFlame.SetActive(firing);
            if (cutterGlow != null) cutterGlow.SetActive(firing);
            LoopOn = firing && cutterLoop != null;
            if (cutterLoop != null && Application.isPlaying && isActiveAndEnabled)
            {
                if (firing && !cutterLoop.isPlaying) cutterLoop.Play();
                else if (!firing && cutterLoop.isPlaying) cutterLoop.Stop();
            }
        }

        /// <summary>Gives or takes this rig's real spot light (the budget calls it; previews may too).</summary>
        public void SetRealLight(bool on)
        {
            if (flashlightSpot == null) return;
            on &= FlashlightOn;
            if (flashlightSpot.enabled != on) flashlightSpot.enabled = on;
        }

        /// <summary>A brief tracer line for a dart this player fired (world positions).</summary>
        public void ShowTracer(Vector3 from, Vector3 to)
        {
            if (tranqTracer == null) return;
            tranqTracer.positionCount = 2;
            tranqTracer.SetPosition(0, from);
            tranqTracer.SetPosition(1, to);
            tranqTracer.enabled = true;
            m_TracerUntil = Time.time + tracerSeconds;
        }

        /// <summary>Emits the sparks of a flame on a weld for <paramref name="dt"/> seconds (LateUpdate; tests and previews).</summary>
        public void StepSparks(float dt)
        {
            if (!Cutting || cutterSparks == null) { m_SparkDebt = 0f; return; }
            m_SparkDebt += sparkRate * dt;
            int count = (int)m_SparkDebt;
            if (count <= 0) return;
            m_SparkDebt -= count;
            if (cutterTip != null) cutterSparks.transform.position = cutterTip.position;
            cutterSparks.Emit(count);
            SparksEmitted += count;
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (s_BudgetFrame != Time.frameCount)
            {
                s_BudgetFrame = Time.frameCount;
                UpdateLightBudget();
            }

            StepSparks(dt);
            if (Cutting && cutterTip != null)
            {
                m_SparkSoundTimer -= dt;
                if (m_SparkSoundTimer <= 0f && Sfx.Library != null)
                {
                    m_SparkSoundTimer = 0.18f;
                    Sfx.PlayAt(Sfx.Library.cutterSparks, cutterTip.position, 0.6f, Random.Range(0.9f, 1.2f), 14f);
                }
            }
            if (tranqTracer != null && tranqTracer.enabled && Time.time >= m_TracerUntil) tranqTracer.enabled = false;
        }

        bool Shown => avatar == null || (avatar.HasPose && avatar.isActiveAndEnabled);

        static Vector3? ViewerPosition()
        {
            if (ViewerOverride != null) return ViewerOverride.position;
            var director = MissionDirector.Active;
            var local = director != null ? director.LocalPlayer : null;
            if (local != null) return local.HeadPosition;
            var cam = Camera.main;
            return cam != null ? cam.transform.position : (Vector3?)null;
        }

        /// <summary>
        /// Hands the real spot lights to the lit remote flashlights nearest this headset, at most
        /// <see cref="MaxRealLights"/>. A flashlight that has one keeps it until another is clearly closer
        /// (<see cref="LightHysteresis"/>), so two crew mates at similar distances do not make the lights flicker.
        /// </summary>
        public static void UpdateLightBudget()
        {
            Vector3? viewer = ViewerPosition();
            int count = 0;
            for (int i = 0; i < s_Live.Count && count < s_Candidates.Length; i++)
            {
                var rig = s_Live[i];
                if (rig == null || rig.flashlightSpot == null) continue;
                if (!rig.FlashlightOn || !rig.Shown || viewer == null) { rig.SetRealLight(false); continue; }
                float d = Vector3.Distance(viewer.Value, rig.flashlightSpot.transform.position);
                if (rig.HasRealLight) d *= LightHysteresis;
                // insertion sort, nearest first
                int at = count++;
                while (at > 0 && s_Scores[at - 1] > d)
                {
                    s_Scores[at] = s_Scores[at - 1];
                    s_Candidates[at] = s_Candidates[at - 1];
                    at--;
                }
                s_Scores[at] = d;
                s_Candidates[at] = rig;
            }
            int max = Mathf.Max(0, MaxRealLights);
            for (int i = max; i < count; i++) s_Candidates[i].SetRealLight(false); // take first, then give
            for (int i = 0; i < count && i < max; i++) s_Candidates[i].SetRealLight(true);
            for (int i = 0; i < count; i++) s_Candidates[i] = null;
        }
    }
}

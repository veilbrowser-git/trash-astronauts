using System.Collections.Generic;
using System.Text;
using TMPro;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>
    /// The SALVAGE CLAW: a lobby mini-game at the supply bay's observation window. A huge arm on the tug's hull
    /// catches cargo drifting past and feeds it into an intake hatch beside the window for a few credits.
    ///
    /// Controls: the stick (grip to hold, tilt right/left to swing the arm, pull back to raise it, push forward to
    /// lower it, trigger to open/close the claw), the EXTEND / RETRACT buttons (held), and the CLAW button.
    /// Everything is local to this headset: each player runs their own arm and their own cargo and is paid into
    /// their own save; nothing here touches the network. Only usable in the hub and on the results screen.
    ///
    /// All arm and cargo positions are in this transform's local space: +Z out through the window, +X to the
    /// operator's right, +Y up.
    /// </summary>
    public sealed class SalvageClawStation : MonoBehaviour
    {
        [System.Serializable]
        public sealed class Piston
        {
            public Transform barrel;
            public Transform rod;
            public Transform anchorA;
            public Transform anchorB;
        }

        public MissionDirector director;

        [Header("Arm joints (built along local +Z)")]
        public Transform turret;
        public Transform shoulder;
        public Transform elbow;
        public Transform wrist;
        public Transform[] fingers;
        public float fingerOpenAngle = -38f;
        public float fingerClosedAngle = 18f;
        public Piston[] pistons;
        public Light workLight;

        [Header("Intake hatch")]
        public Transform intakeMouth;
        public Transform intakeInside;
        public Transform intakeDoorLeft;
        public Transform intakeDoorRight;
        public float intakeDoorTravel = 0.55f;
        public Renderer intakeLamp;

        [Header("Console")]
        public SalvageClawStick stick;
        public PushButton extendButton;
        public PushButton retractButton;
        public PushButton clawButton;
        public TMP_Text monitor;
        [Tooltip("Floor spot where the operator stands.")]
        public Transform standPoint;
        public AudioSource servo;

        [Header("Cargo")]
        [Tooltip("Inactive templates, one per ClawCargoKind (by index).")]
        public SalvageClawCargo[] cargoTemplates;
        public Transform cargoRoot;
        public int seed = 7319;
        public bool runInUpdate = true;

        // ------------------------------------------------------------------ test seams
        /// <summary>Overrides the stick (x = right, y = forward/push).</summary>
        public Vector2? StickOverride { get; set; }
        /// <summary>Overrides EXTEND (+1) / RETRACT (-1).</summary>
        public float? ReachOverride { get; set; }
        /// <summary>Overrides "someone is at the station".</summary>
        public bool? OperatorPresentOverride { get; set; }

        // ------------------------------------------------------------------ state
        float m_Yaw = 25f;      // degrees, 0 = straight out from the turret
        float m_Reach = 4.2f;   // horizontal distance yaw axis -> tip
        float m_Height = 1.6f;  // tip height
        Vector3 m_Vel;          // smoothed (lateral, vertical, reach) m/s
        bool m_ClawClosed;
        float m_ClawAmount;     // 0 open .. 1 closed
        bool m_MissReported;
        SalvageClawCargo m_Held;
        float m_DoorOpen;
        float m_NextSpawn = SalvageClawConfig.FirstSpawnDelay;
        float m_Absent;
        float m_ExtendPulse, m_RetractPulse;
        float m_ServoLevel;
        Vector3 m_TipLocal, m_TipVelocity;
        bool m_HaveTip;
        System.Random m_Rng;
        string m_Message;
        float m_MessageUntil;
        float m_NextMonitor;
        int m_Spawned;
        int m_LastPay = -1;

        readonly List<SalvageClawCargo> m_Cargo = new List<SalvageClawCargo>();
        readonly List<SalvageClawCargo> m_Pool = new List<SalvageClawCargo>();
        readonly StringBuilder m_Text = new StringBuilder(160);
        Vector3 m_ShoulderLocal;
        Vector3 m_TurretLocal;
        bool m_Wired;
        Vector3 m_DoorLeftClosed, m_DoorRightClosed;

        public static readonly Vector3 ParkPose = new Vector3(15f, 3.6f, 1.2f); // yaw, reach, height

        // ------------------------------------------------------------------ queries
        public IReadOnlyList<SalvageClawCargo> Cargo => m_Cargo;
        public SalvageClawCargo Held => m_Held;
        public bool ClawClosed => m_ClawClosed;
        public float ClawAmount => m_ClawAmount;
        /// <summary>Claw fingertip centre in this station's local space.</summary>
        public Vector3 TipLocal => m_TipLocal;
        public Vector3 TipWorld => transform.TransformPoint(m_TipLocal);
        public Vector3 IntakeLocal => intakeMouth != null ? transform.InverseTransformPoint(intakeMouth.position) : new Vector3(1.5f, 1f, 2.6f);
        /// <summary>Things spawned since the last reset (for tests).</summary>
        public int SpawnedCount => m_Spawned;
        /// <summary>Credits paid by the last swallow (0 = nothing, e.g. after the shift cap).</summary>
        public int LastPay => m_LastPay;
        public string MonitorText => monitor != null ? monitor.text : m_Text.ToString();
        public bool Operable => director == null || director.Phase != MissionPhase.Active;

        /// <summary>Credits earned at the claw since the last contract ended (saved).</summary>
        public int ShiftEarned => director != null && director.Profile != null ? director.Profile.Data.clawShiftCredits : 0;
        public int ShiftRemaining => Mathf.Max(0, SalvageClawConfig.ShiftCap - ShiftEarned);

        public bool OperatorPresent
        {
            get
            {
                if (OperatorPresentOverride.HasValue) return OperatorPresentOverride.Value;
                if (standPoint == null) return false;
                Vector3 head;
                var player = director != null ? director.LocalPlayer : null;
                if (player != null) head = player.HeadPosition;
                else if (Camera.main != null) head = Camera.main.transform.position;
                else return false;
                Vector3 d = head - standPoint.position;
                d.y = 0f;
                return d.magnitude < SalvageClawConfig.OperatorRadius && head.y > standPoint.position.y - 0.2f && head.y < standPoint.position.y + 2.6f;
            }
        }

        // ------------------------------------------------------------------ lifecycle
        void Awake()
        {
            if (director == null) director = MissionDirector.Active;
            Init();
            m_Rng = new System.Random(seed);
            ApplyPose();
            ApplyClaw();
        }

        bool m_Init;

        /// <summary>Measures the arm's fixed geometry (also usable in the editor, before Awake).</summary>
        public void Init()
        {
            if (m_Init) return;
            m_Init = true;
            if (turret != null) m_TurretLocal = transform.InverseTransformPoint(turret.position);
            if (shoulder != null) m_ShoulderLocal = transform.InverseTransformPoint(shoulder.position);
            if (intakeDoorLeft != null) m_DoorLeftClosed = intakeDoorLeft.localPosition;
            if (intakeDoorRight != null) m_DoorRightClosed = intakeDoorRight.localPosition;
        }

        void OnEnable()
        {
            if (director == null) director = MissionDirector.Active;
            if (m_Wired) return;
            m_Wired = true;
            if (director != null) director.MissionEnded += OnMissionEnded;
            if (stick != null) stick.TriggerPulled += ToggleClaw;
            if (clawButton != null) clawButton.Pressed += OnClawButton;
            if (extendButton != null) extendButton.Pressed += OnExtendButton;
            if (retractButton != null) retractButton.Pressed += OnRetractButton;
        }

        void OnDisable()
        {
            if (!m_Wired) return;
            m_Wired = false;
            if (director != null) director.MissionEnded -= OnMissionEnded;
            if (stick != null) stick.TriggerPulled -= ToggleClaw;
            if (clawButton != null) clawButton.Pressed -= OnClawButton;
            if (extendButton != null) extendButton.Pressed -= OnExtendButton;
            if (retractButton != null) retractButton.Pressed -= OnRetractButton;
            if (servo != null) servo.Stop();
        }

        void OnClawButton(PushButton b, HandRig hand) => ToggleClaw();
        void OnExtendButton(PushButton b, HandRig hand) => m_ExtendPulse = 0.5f;   // a pointer press moves it a little
        void OnRetractButton(PushButton b, HandRig hand) => m_RetractPulse = 0.5f;

        /// <summary>A contract was completed or failed: the claw shift starts again.</summary>
        void OnMissionEnded(MissionResult result)
        {
            if (director == null || director.Profile == null) return;
            if (director.Profile.Data.clawShiftCredits == 0) return;
            director.Profile.Data.clawShiftCredits = 0;
            director.Profile.NotifyChangedAndSave();
        }

        void Update()
        {
            if (runInUpdate) Step(Mathf.Min(Time.deltaTime, 0.1f));
        }

        /// <summary>Restarts the drift pattern from a seed and clears everything outside (test seam).</summary>
        public void ResetRandom(int newSeed)
        {
            seed = newSeed;
            m_Rng = new System.Random(newSeed);
            ClearCargo();
            m_Spawned = 0;
            m_NextSpawn = SalvageClawConfig.FirstSpawnDelay;
        }

        /// <summary>Puts the tip straight at a pose (yaw degrees, reach m, height m), clamped (test seam and park).</summary>
        public void SetPose(float yaw, float reach, float height)
        {
            m_Yaw = yaw;
            m_Reach = reach;
            m_Height = height;
            m_Vel = Vector3.zero;
            Init();
            ClampPose();
            ApplyPose();
            m_TipLocal = ComputeTip();
            m_HaveTip = false;
        }

        /// <summary>Points the tip at a station-space point (clamped to what the arm can reach).</summary>
        public void PoseTo(Vector3 local)
        {
            Init();
            var p = PoseFor(local);
            SetPose(p.x, p.y, p.z);
        }

        /// <summary>Sets how far the claw is closed, 0..1, without the animation (previews).</summary>
        public void SetClawAmount(float amount)
        {
            m_ClawAmount = Mathf.Clamp01(amount);
            ApplyClaw();
        }

        /// <summary>The (yaw, reach, height) that puts the tip at a station-space point, before clamping.</summary>
        public Vector3 PoseFor(Vector3 local)
        {
            Init();
            Vector3 d = local - m_TurretLocal;
            float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            float reach = new Vector2(d.x, d.z).magnitude;
            return new Vector3(yaw, reach, local.y);
        }

        public void SetClaw(bool closed)
        {
            if (m_ClawClosed == closed) return;
            m_ClawClosed = closed;
            m_MissReported = false;
            if (!closed && m_Held != null) Release();
            if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.toolHolster != null ? Sfx.Library.toolHolster : Sfx.Library.buttonClick, ConsolePoint, 0.5f, closed ? 0.7f : 0.85f, 6f);
        }

        public void ToggleClaw()
        {
            if (!Operable) return;
            SetClaw(!m_ClawClosed);
        }

        Vector3 ConsolePoint => standPoint != null ? standPoint.position + Vector3.up * 1.1f : transform.position;

        // ------------------------------------------------------------------ the step
        public void Step(float dt)
        {
            if (dt <= 0f) return;
            if (m_Rng == null) m_Rng = new System.Random(seed);
            bool operable = Operable;
            bool present = operable && OperatorPresent;

            if (stick != null) stick.Step(dt);
            SetButtons(operable);

            // --- inputs
            Vector2 axis = Vector2.zero;
            float reachIn = 0f;
            if (operable)
            {
                axis = StickOverride ?? (stick != null ? stick.Axis : Vector2.zero);
                if (ReachOverride.HasValue) reachIn = ReachOverride.Value;
                else
                {
                    bool ext = m_ExtendPulse > 0f || FingerOn(extendButton);
                    bool ret = m_RetractPulse > 0f || FingerOn(retractButton);
                    reachIn = (ext ? 1f : 0f) - (ret ? 1f : 0f);
                }
            }
            m_ExtendPulse = Mathf.Max(0f, m_ExtendPulse - dt);
            m_RetractPulse = Mathf.Max(0f, m_RetractPulse - dt);

            // --- the heavy arm: velocities ease toward what the controls ask for
            float load = m_Held != null && m_Held.kind == ClawCargoKind.LongContainer ? 1.5f : 1f;
            float speedScale = 1f / load;
            // Pull back (negative forward) raises the arm, push forward lowers it, like a flight stick.
            Vector3 want = new Vector3(axis.x * SalvageClawConfig.LateralSpeed, -axis.y * SalvageClawConfig.VerticalSpeed, reachIn * SalvageClawConfig.ReachSpeed) * speedScale;
            if (!operable)
            {
                // Locked during a contract: drift home to the parking pose.
                want = new Vector3(Mathf.DeltaAngle(m_Yaw, ParkPose.x) * Mathf.Deg2Rad * m_Reach, ParkPose.z - m_Height, ParkPose.y - m_Reach) * 0.8f;
                want = Vector3.ClampMagnitude(want, 1.2f);
                if (m_ClawClosed) m_ClawClosed = false;
                if (m_Held != null) Release();
            }
            float k = 1f - Mathf.Exp(-dt / (SalvageClawConfig.Inertia * load));
            m_Vel += (want - m_Vel) * k;

            m_Yaw += m_Vel.x * dt / Mathf.Max(1f, m_Reach) * Mathf.Rad2Deg;
            m_Height += m_Vel.y * dt;
            m_Reach += m_Vel.z * dt;
            ClampPose();
            ApplyPose();

            Vector3 tip = ComputeTip();
            m_TipVelocity = m_HaveTip ? (tip - m_TipLocal) / dt : Vector3.zero;
            m_TipLocal = tip;
            m_HaveTip = true;

            // --- the claw
            float target = m_ClawClosed ? 1f : 0f;
            m_ClawAmount = Mathf.MoveTowards(m_ClawAmount, target, dt / SalvageClawConfig.ClawCloseTime);
            ApplyClaw();
            if (m_ClawClosed && m_Held == null && operable)
            {
                var near = NearestCargo(tip, out float dist);
                if (near != null && near.Current == SalvageClawCargo.State.Drifting && dist <= near.GripRadius && m_ClawAmount < 1f)
                    Grip(near);
                else if (m_ClawAmount >= 1f && !m_MissReported)
                {
                    m_MissReported = true;
                    Say("MISSED - OPEN AND TRY AGAIN", 2f);
                }
            }

            // --- cargo
            StepCargo(dt, tip);
            if (m_Held != null && Vector3.Distance(tip, IntakeLocal) <= SalvageClawConfig.IntakeRadius) Swallow(m_Held);

            // --- spawning (only while someone is here)
            if (present)
            {
                m_Absent = 0f;
                m_NextSpawn -= dt;
                if (m_NextSpawn <= 0f)
                {
                    if (CountDrifting() < SalvageClawConfig.MaxCargo) SpawnNext();
                    m_NextSpawn = Range(SalvageClawConfig.SpawnInterval.x, SalvageClawConfig.SpawnInterval.y);
                }
            }
            else
            {
                m_Absent += dt;
                if (m_Absent >= SalvageClawConfig.AbandonDelay && m_Cargo.Count > 0) ClearCargo();
                if (!operable) ClearCargo();
                m_NextSpawn = Mathf.Max(m_NextSpawn, SalvageClawConfig.FirstSpawnDelay);
            }

            // --- hatch doors, servo whine, monitor
            bool wantDoors = HatchShouldOpen(tip);
            m_DoorOpen = Mathf.MoveTowards(m_DoorOpen, wantDoors ? 1f : 0f, dt * 2.2f);
            float slide = Smooth(m_DoorOpen) * intakeDoorTravel;
            if (intakeDoorLeft != null) intakeDoorLeft.localPosition = m_DoorLeftClosed + Vector3.left * slide;
            if (intakeDoorRight != null) intakeDoorRight.localPosition = m_DoorRightClosed + Vector3.right * slide;
            UpdateServo(dt);
            if (Time.unscaledTime >= m_NextMonitor || !runInUpdate)
            {
                m_NextMonitor = Time.unscaledTime + 0.2f;
                UpdateMonitor(operable, tip);
            }
        }

        void SetButtons(bool operable)
        {
            if (extendButton != null) extendButton.SetInteractable(operable);
            if (retractButton != null) retractButton.SetInteractable(operable);
            if (clawButton != null) clawButton.SetInteractable(operable);
            if (stick != null && stick.interactable != null && stick.interactable.enabled != operable) stick.interactable.enabled = operable;
        }

        /// <summary>True while a local fingertip rests on the button (held buttons: EXTEND and RETRACT).</summary>
        bool FingerOn(PushButton button)
        {
            if (button == null || director == null) return false;
            var player = director.LocalPlayer;
            if (player == null) return false;
            var col = button.GetComponent<Collider>();
            if (col == null) return false;
            return Touching(player.leftHand, col) || Touching(player.rightHand, col);
        }

        static bool Touching(HandRig hand, Collider col)
        {
            if (hand == null || hand.fingertip == null) return false;
            Vector3 p = hand.fingertip.position;
            return (col.ClosestPoint(p) - p).sqrMagnitude < 0.03f * 0.03f;
        }

        // ------------------------------------------------------------------ the arm
        void ClampPose()
        {
            m_Yaw = Mathf.Clamp(m_Yaw, SalvageClawConfig.MinYaw, SalvageClawConfig.MaxYaw);
            m_Reach = Mathf.Clamp(m_Reach, SalvageClawConfig.MinReach, SalvageClawConfig.MaxReach);
            m_Height = Mathf.Clamp(m_Height, SalvageClawConfig.MinHeight, SalvageClawConfig.MaxHeight);

            // The wrist must stay within the booms' reach and not so high above the shoulder that the elbow folds back over the hull.
            float L1 = SalvageClawConfig.UpperBoomLength, L2 = SalvageClawConfig.ForeBoomLength;
            float r = m_Reach - SalvageClawConfig.ClawLength;
            float y = m_Height - m_ShoulderLocal.y;
            float maxUp = Mathf.Tan(25f * Mathf.Deg2Rad) * Mathf.Max(0.5f, r);
            if (y > maxUp) { y = maxUp; m_Height = m_ShoulderLocal.y + y; m_Vel.y = Mathf.Min(0f, m_Vel.y); }
            float d = Mathf.Sqrt(r * r + y * y);
            float maxD = L1 + L2 - 0.08f;
            if (d > maxD)
            {
                float s = maxD / d;
                r *= s;
                y *= s;
                m_Reach = r + SalvageClawConfig.ClawLength;
                m_Height = m_ShoulderLocal.y + y;
                m_Vel.z = Mathf.Min(0f, m_Vel.z);
            }
            if (m_Yaw <= SalvageClawConfig.MinYaw + 0.001f) m_Vel.x = Mathf.Max(0f, m_Vel.x);
            if (m_Yaw >= SalvageClawConfig.MaxYaw - 0.001f) m_Vel.x = Mathf.Min(0f, m_Vel.x);
            if (m_Reach <= SalvageClawConfig.MinReach + 0.001f) m_Vel.z = Mathf.Max(0f, m_Vel.z);
            if (m_Reach >= SalvageClawConfig.MaxReach - 0.001f) m_Vel.z = Mathf.Min(0f, m_Vel.z);
            if (m_Height <= SalvageClawConfig.MinHeight + 0.001f) m_Vel.y = Mathf.Max(0f, m_Vel.y);
            if (m_Height >= SalvageClawConfig.MaxHeight - 0.001f) m_Vel.y = Mathf.Min(0f, m_Vel.y);
        }

        /// <summary>Two-bone inverse kinematics in the vertical plane of the turret, elbow up, claw kept level.</summary>
        void ApplyPose()
        {
            if (turret == null || shoulder == null || elbow == null || wrist == null) return;
            float L1 = SalvageClawConfig.UpperBoomLength, L2 = SalvageClawConfig.ForeBoomLength;
            float r = m_Reach - SalvageClawConfig.ClawLength;
            float y = m_Height - m_ShoulderLocal.y;
            float d = Mathf.Clamp(Mathf.Sqrt(r * r + y * y), Mathf.Abs(L1 - L2) + 0.05f, L1 + L2 - 0.01f);
            float a = Mathf.Acos(Mathf.Clamp((L1 * L1 + d * d - L2 * L2) / (2f * L1 * d), -1f, 1f));
            float e = Mathf.Atan2(y, r);
            float p1 = e + a; // upper boom pitch (up positive)
            float er = Mathf.Cos(p1) * L1, ey = Mathf.Sin(p1) * L1;
            float p2 = Mathf.Atan2(y - ey, r - er); // forearm pitch

            turret.localRotation = Quaternion.Euler(0f, m_Yaw, 0f);
            shoulder.localRotation = Quaternion.Euler(-p1 * Mathf.Rad2Deg, 0f, 0f);
            elbow.localRotation = Quaternion.Euler(-(p2 - p1) * Mathf.Rad2Deg, 0f, 0f);
            wrist.localRotation = Quaternion.Euler(p2 * Mathf.Rad2Deg, 0f, 0f);

            if (pistons != null)
            {
                foreach (var piston in pistons)
                {
                    if (piston == null || piston.anchorA == null || piston.anchorB == null) continue;
                    Vector3 pa = piston.anchorA.position, pb = piston.anchorB.position;
                    Vector3 dir = pb - pa;
                    if (dir.sqrMagnitude < 1e-6f) continue;
                    Quaternion look = Quaternion.LookRotation(dir, turret.up);
                    if (piston.barrel != null) piston.barrel.SetPositionAndRotation(pa, look);
                    if (piston.rod != null) piston.rod.SetPositionAndRotation(pb, Quaternion.LookRotation(-dir, turret.up));
                }
            }
        }

        Vector3 ComputeTip()
        {
            if (wrist == null) return new Vector3(0f, m_Height, m_Reach);
            return transform.InverseTransformPoint(wrist.position + wrist.forward * SalvageClawConfig.ClawLength * transform.lossyScale.z);
        }

        void ApplyClaw()
        {
            if (fingers == null) return;
            float angle = Mathf.Lerp(fingerOpenAngle, fingerClosedAngle, Smooth(m_ClawAmount));
            foreach (var f in fingers)
                if (f != null) f.localRotation = Quaternion.Euler(angle, 0f, 0f);
        }

        static float Smooth(float t) => t * t * (3f - 2f * t);

        // ------------------------------------------------------------------ cargo
        int CountDrifting()
        {
            int n = 0;
            foreach (var c in m_Cargo) if (c.Current != SalvageClawCargo.State.Swallowing) n++;
            return n;
        }

        float Range(float a, float b) => a + (float)m_Rng.NextDouble() * (b - a);

        /// <summary>Sends the next thing drifting past, from the seeded pattern. Returns it (null without templates).</summary>
        public SalvageClawCargo SpawnNext()
        {
            var kind = SalvageClawConfig.KindForRoll(m_Rng.NextDouble());
            // Where its path crosses the arm's reach, then a mostly sideways drift through that point.
            float yaw = Range(-40f, 55f) * Mathf.Deg2Rad;
            float reach = Range(4.2f, 9.0f);
            float height = Range(-1.2f, 3.4f);
            Vector3 centre = m_TurretLocal + new Vector3(Mathf.Sin(yaw) * reach, 0f, Mathf.Cos(yaw) * reach);
            centre.y = height;
            centre.z = Mathf.Max(centre.z, 4.2f);
            float side = m_Rng.NextDouble() < 0.5 ? -1f : 1f;
            Vector3 dir = new Vector3(side, Range(-0.22f, 0.22f), Range(-0.15f, 0.15f)).normalized;
            Vector2 speeds = SalvageClawConfig.SpeedRange(kind);
            float speed = Range(speeds.x, speeds.y);
            float lead = Range(9f, 12f);
            Vector3 start = centre - dir * lead;
            Vector3 spinAxis = new Vector3(Range(-1f, 1f), Range(-1f, 1f), Range(-1f, 1f));
            if (spinAxis.sqrMagnitude < 0.01f) spinAxis = Vector3.up;
            float spinRate = kind == ClawCargoKind.Strongbox ? Range(30f, 50f) : Range(6f, 24f);
            Quaternion rotation = Quaternion.Euler(Range(0f, 360f), Range(0f, 360f), Range(0f, 360f));

            var cargo = Take(kind);
            if (cargo == null) return null;
            cargo.transform.localPosition = start;
            cargo.transform.localRotation = rotation;
            cargo.transform.localScale = cargo.BaseScale;
            cargo.Velocity = dir * speed;
            cargo.Spin = spinAxis.normalized * spinRate;
            cargo.PathCentre = centre;
            cargo.Current = SalvageClawCargo.State.Drifting;
            cargo.SinceKnock = 10f;
            cargo.gameObject.SetActive(true);
            m_Cargo.Add(cargo);
            m_Spawned++;
            return cargo;
        }

        /// <summary>Test seam: places a thing of a kind at a station-space point, at rest.</summary>
        public SalvageClawCargo SpawnAt(ClawCargoKind kind, Vector3 local, Vector3 velocity)
        {
            var cargo = Take(kind);
            if (cargo == null) return null;
            cargo.transform.localPosition = local;
            cargo.transform.localRotation = Quaternion.identity;
            cargo.transform.localScale = cargo.BaseScale;
            cargo.Velocity = velocity;
            cargo.Spin = Vector3.zero;
            cargo.PathCentre = local;
            cargo.Current = SalvageClawCargo.State.Drifting;
            cargo.SinceKnock = 10f;
            cargo.gameObject.SetActive(true);
            m_Cargo.Add(cargo);
            m_Spawned++;
            return cargo;
        }

        SalvageClawCargo Take(ClawCargoKind kind)
        {
            for (int i = 0; i < m_Pool.Count; i++)
            {
                if (m_Pool[i] != null && m_Pool[i].kind == kind)
                {
                    var pooled = m_Pool[i];
                    m_Pool.RemoveAt(i);
                    return pooled;
                }
            }
            int index = (int)kind;
            if (cargoTemplates == null || index >= cargoTemplates.Length || cargoTemplates[index] == null) return null;
            var template = cargoTemplates[index];
            var made = Instantiate(template, cargoRoot != null ? cargoRoot : transform, false);
            made.name = template.name;
            made.kind = kind;
            made.BaseScale = template.transform.localScale;
            return made;
        }

        void Recycle(SalvageClawCargo cargo)
        {
            if (cargo == null) return;
            if (cargo == m_Held) m_Held = null;
            cargo.gameObject.SetActive(false);
            m_Cargo.Remove(cargo);
            m_Pool.Add(cargo);
        }

        /// <summary>Removes everything outside (nobody at the station, or a contract started).</summary>
        public void ClearCargo()
        {
            m_Held = null;
            for (int i = m_Cargo.Count - 1; i >= 0; i--) Recycle(m_Cargo[i]);
        }

        SalvageClawCargo NearestCargo(Vector3 tip, out float best)
        {
            best = float.MaxValue;
            SalvageClawCargo found = null;
            foreach (var c in m_Cargo)
            {
                if (c.Current != SalvageClawCargo.State.Drifting) continue;
                float d = Vector3.Distance(c.transform.localPosition, tip);
                if (d < best) { best = d; found = c; }
            }
            return found;
        }

        void Grip(SalvageClawCargo cargo)
        {
            m_Held = cargo;
            cargo.Current = SalvageClawCargo.State.Gripped;
            cargo.Velocity = Vector3.zero;
            cargo.Spin = Vector3.zero;
            cargo.HeldFrom = cargo.transform.localPosition;
            cargo.HeldBlend = 0f;
            cargo.HeldRotation = Quaternion.Inverse(wrist != null ? wrist.rotation : transform.rotation) * cargo.transform.rotation;
            if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.RandomImpactHeavy(), ConsolePoint, 0.8f, 0.75f, 8f);
            Pulse(0.7f, 0.12f);
            Say("GOT IT - " + SalvageClawConfig.NameOf(cargo.kind) + ". BRING IT TO THE HATCH", 3f);
        }

        void Release()
        {
            var cargo = m_Held;
            m_Held = null;
            if (cargo == null || cargo.Current != SalvageClawCargo.State.Gripped) return;
            cargo.Current = SalvageClawCargo.State.Drifting;
            // Let go and it keeps the claw's swing (a throw), slowly.
            cargo.Velocity = Vector3.ClampMagnitude(m_TipVelocity * 0.8f, 2.5f);
            cargo.Spin = new Vector3(Range(-1f, 1f), Range(-1f, 1f), Range(-1f, 1f)) * 15f;
            cargo.PathCentre = cargo.transform.localPosition;
            cargo.SinceKnock = 0f;
        }

        void Swallow(SalvageClawCargo cargo)
        {
            m_Held = null;
            cargo.Current = SalvageClawCargo.State.Swallowing;
            cargo.SwallowT = 0f;
            cargo.SwallowFrom = cargo.transform.localPosition;
            m_ClawClosed = false;
            m_MissReported = true;

            int pay = 0;
            if (director != null && director.Profile != null)
            {
                pay = Mathf.Min(cargo.Pay, ShiftRemaining);
                if (pay > 0)
                {
                    var data = director.Profile.Data;
                    data.totalCredits += pay;
                    data.clawShiftCredits += pay;
                    director.Profile.NotifyChangedAndSave();
                }
            }
            m_LastPay = pay;
            if (pay > 0)
            {
                bool big = cargo.kind == ClawCargoKind.Strongbox;
                if (Sfx.Library != null) Sfx.PlayAt(big && Sfx.Library.depositJackpot != null ? Sfx.Library.depositJackpot : Sfx.Library.deposit, ConsolePoint, 0.8f, 1f, 8f);
                Say("+" + pay + " cr  " + SalvageClawConfig.NameOf(cargo.kind) + (ShiftRemaining == 0 ? "\n" + SalvageClawConfig.ShiftOverText : ""), 4f);
            }
            else
            {
                Say(SalvageClawConfig.ShiftOverText + "\nNO PAY - JUST FOR FUN", 4f);
            }
            Pulse(0.45f, 0.08f);
        }

        void StepCargo(float dt, Vector3 tip)
        {
            Quaternion wristRot = wrist != null ? wrist.rotation : transform.rotation;
            for (int i = m_Cargo.Count - 1; i >= 0; i--)
            {
                var c = m_Cargo[i];
                var t = c.transform;
                switch (c.Current)
                {
                    case SalvageClawCargo.State.Gripped:
                    {
                        c.HeldBlend = Mathf.MoveTowards(c.HeldBlend, 1f, dt / 0.25f);
                        t.localPosition = Vector3.Lerp(c.HeldFrom, tip, Smooth(c.HeldBlend));
                        t.rotation = wristRot * c.HeldRotation;
                        break;
                    }
                    case SalvageClawCargo.State.Swallowing:
                    {
                        c.SwallowT = Mathf.MoveTowards(c.SwallowT, 1f, dt / 0.8f);
                        Vector3 inside = intakeInside != null ? transform.InverseTransformPoint(intakeInside.position) : IntakeLocal + Vector3.back;
                        float s = Smooth(c.SwallowT);
                        t.localPosition = Vector3.Lerp(c.SwallowFrom, inside, s);
                        t.localScale = c.BaseScale * Mathf.Lerp(1f, 0.35f, s);
                        if (c.SwallowT >= 1f)
                        {
                            if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.impactHeavy, ConsolePoint, 0.7f, 0.55f, 8f);
                            Recycle(c);
                        }
                        break;
                    }
                    default:
                    {
                        c.SinceKnock += dt;
                        t.localPosition += c.Velocity * dt;
                        if (c.Spin.sqrMagnitude > 0f)
                        {
                            Vector3 axisWorld = transform.TransformDirection(c.Spin.normalized);
                            t.rotation = Quaternion.AngleAxis(c.Spin.magnitude * dt, axisWorld) * t.rotation;
                        }
                        // Clipping it with the claw (not closing on it) knocks it tumbling away.
                        Vector3 offset = t.localPosition - tip;
                        float dist = offset.magnitude;
                        Vector3 rel = m_TipVelocity - c.Velocity;
                        bool grabbing = m_ClawClosed && m_Held == null;
                        if (!grabbing && c.SinceKnock > 0.6f && dist < c.GripRadius * 0.8f && rel.magnitude > 0.8f && Vector3.Dot(rel, offset) > 0f)
                            Knock(c, offset, rel);
                        if ((t.localPosition - c.PathCentre).magnitude > SalvageClawConfig.DespawnDistance || t.localPosition.z < 0.4f)
                            Recycle(c);
                        break;
                    }
                }
            }
        }

        void Knock(SalvageClawCargo c, Vector3 offset, Vector3 rel)
        {
            Vector3 away = offset.sqrMagnitude > 1e-4f ? offset.normalized : Vector3.forward;
            c.Velocity += rel * 0.7f + away * 0.4f;
            c.Velocity = Vector3.ClampMagnitude(c.Velocity, 3f);
            c.Spin += Vector3.Cross(away, rel).normalized * (40f + 30f * rel.magnitude);
            c.SinceKnock = 0f;
            c.PathCentre = c.transform.localPosition;
            if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.RandomImpactLight(), ConsolePoint, 0.6f, 0.8f, 8f);
            Pulse(0.3f, 0.06f);
            Say("CLIPPED IT - CLOSE THE CLAW TO GRAB", 2f);
        }

        /// <summary>Test seam: knocks a thing as if the claw had struck it at a relative speed.</summary>
        public void KnockForTest(SalvageClawCargo cargo, Vector3 relativeVelocity) => Knock(cargo, cargo.transform.localPosition - m_TipLocal, relativeVelocity);

        bool HatchShouldOpen(Vector3 tip)
        {
            foreach (var c in m_Cargo) if (c.Current == SalvageClawCargo.State.Swallowing && c.SwallowT < 0.85f) return true;
            return m_Held != null && Vector3.Distance(tip, IntakeLocal) < 4f;
        }

        void Pulse(float amplitude, float duration)
        {
            var hand = stick != null ? stick.HeldBy : null;
            if (hand != null) hand.Pulse(amplitude, duration);
        }

        void UpdateServo(float dt)
        {
            float speed = m_Vel.magnitude;
            m_ServoLevel = Mathf.MoveTowards(m_ServoLevel, Mathf.Clamp01(speed / 1.2f), dt * 3f);
            if (servo == null) return;
            if (servo.clip == null && Sfx.Library != null) servo.clip = Sfx.Library.doorMove;
            if (servo.clip == null) return;
            if (m_ServoLevel > 0.02f)
            {
                if (!servo.isPlaying) servo.Play();
                servo.volume = 0.32f * m_ServoLevel;
                servo.pitch = 0.6f + 0.35f * m_ServoLevel;
            }
            else if (servo.isPlaying) servo.Stop();
        }

        void Say(string message, float seconds)
        {
            m_Message = message;
            m_MessageUntil = Time.unscaledTime + seconds;
            m_NextMonitor = 0f;
        }

        void UpdateMonitor(bool operable, Vector3 tip)
        {
            m_Text.Length = 0;
            if (!operable)
            {
                m_Text.Append("SALVAGE CLAW\nLOCKED\nCONTRACT IN PROGRESS");
            }
            else
            {
                var near = NearestCargo(tip, out float dist);
                m_Text.Append("SALVAGE CLAW\n");
                if (m_Held != null) m_Text.Append("HOLDING ").Append(SalvageClawConfig.NameOf(m_Held.kind)).Append("  HATCH ").Append(Vector3.Distance(tip, IntakeLocal).ToString("0.0")).Append(" m");
                else if (near != null) m_Text.Append("RANGE ").Append(dist.ToString("0.0")).Append(" m  ").Append(SalvageClawConfig.NameOf(near.kind));
                else m_Text.Append(OperatorPresent ? "SCANNING..." : "STEP UP TO OPERATE");
                m_Text.Append("\nCLAW ").Append(m_Held != null ? "LOCKED ON" : m_ClawClosed ? "CLOSED" : "OPEN");
                m_Text.Append("\nHAUL ").Append(ShiftEarned).Append(" / ").Append(SalvageClawConfig.ShiftCap).Append(" cr");
                if (Time.unscaledTime < m_MessageUntil && !string.IsNullOrEmpty(m_Message)) m_Text.Append("\n> ").Append(m_Message);
                else if (ShiftRemaining == 0) m_Text.Append("\n").Append(SalvageClawConfig.ShiftOverText);
            }
            if (workLight != null) workLight.enabled = operable;
            if (intakeLamp != null) intakeLamp.enabled = m_Held != null;
            if (monitor != null)
            {
                string s = m_Text.ToString();
                if (monitor.text != s) monitor.text = s;
            }
        }
    }
}

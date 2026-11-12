using System.Collections;
using System.Collections.Generic;
using TMPro;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// The thing that used to be maintenance unit JANITOR-7. This is the body: it senses (line of sight and
    /// noise), moves along the waypoint graph, and presents its state with light, a glyph and sound.
    /// Decisions are made by <see cref="EnemyBrain"/>.
    ///
    /// Fairness rules: it must see the player continuously for a moment before chasing, it announces
    /// every state change with a distinct sound and blink pattern, it never enters the tug, airlock or
    /// service duct, and losing line of sight for a few seconds makes it give up and search.
    /// </summary>
    public sealed class EnemyDrone : MonoBehaviour
    {
        [Header("Variant")]
        public string displayName = "JANITOR-7";
        [Tooltip("One line on the mission board telling the player how this one hunts.")]
        public string trait = "Sees and hears about equally.";
        [Tooltip("When set, this creature uses its own senses and speeds instead of the mission config's.")]
        public bool overrideTuning;
        public EnemyTuning tuning = new EnemyTuning();
        [Tooltip("Scales how long a tranquilizer dart keeps it asleep.")]
        public float sedationMultiplier = 1f;

        public MissionDirector director;
        public EnemyWaypoint[] graph;
        public EnemyWaypoint[] patrolRoute;
        public SlidingDoor[] doors;
        public Transform dock;
        public Transform eye;
        public float hoverHeight = 1.35f;
        public float turnSpeed = 140f;
        public float arriveDistance = 0.2f;
        [Tooltip("How far the drone may leave the last waypoint to reach a point of interest.")]
        public float maxOffGraphDistance = 4f;
        public float doorOpenDelay = 1.6f;

        [Header("Presentation")]
        public Light eyeLight;
        public Renderer eyeRenderer;
        [Tooltip("Additional eyes that glow together with the main one.")]
        public Renderer[] extraEyes;
        public TMP_Text glyph;
        public AudioSource hum;
        public AudioSource voice;

        static readonly int k_Emission = Shader.PropertyToID("_EmissionColor");
        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");

        readonly List<EnemyWaypoint> m_NodePath = new List<EnemyWaypoint>();
        readonly List<Vector3> m_Path = new List<Vector3>();
        MaterialPropertyBlock m_Block;
        EnemyTuning m_Tuning;
        int m_PathIndex;
        int m_PatrolIndex;
        Vector3 m_PathGoal;
        bool m_HasPath;
        bool m_Running;
        float m_WakeTimer;
        float m_DoorTimer;
        float m_LingerSpin;
        bool m_Direct;
        bool m_SawLastStep;
        bool m_SeesNow;
        Vector3 m_LastSeenPosition;
        Vector3 m_LastSeenVelocity;

        [Header("Lying in wait")]
        [Tooltip("Chance, at each stop on its rounds, that it goes completely still and silent for a while.")]
        [Range(0f, 1f)] public float lurkChance = 0.3f;
        public Vector2 lurkSeconds = new Vector2(3f, 9f);
        /// <summary>True while it has stopped dead on its rounds: no footsteps, no hum. You lose track of it.</summary>
        public bool IsLurking => m_Running && Brain != null && Brain.State == EnemyState.Patrol && m_Clock < m_LurkUntil;
        float m_Clock;
        float m_LurkUntil;
        float m_NoLurkBefore;
        System.Random m_LurkRandom = new System.Random(1);

        /// <summary>When it last reacted to a sound (Time.time).</summary>
        public float LastHeardNoiseTime { get; private set; } = -100f;
        /// <summary>True while it is flying straight at its quarry instead of following its route points.</summary>
        public bool IsPursuingDirectly => m_Direct;
        [Tooltip("How far from its route it will leave to reach something it is hunting.")]
        public float huntOffGraphDistance = 14f;
        [Tooltip("Speed multiplier for the last few metres of a chase.")]
        public float lungeMultiplier = 1.3f;
        float m_SedateTimer;
        PlayerContext m_Target;

        public EnemyBrain Brain { get; private set; }
        public EnemyState State => Brain != null ? Brain.State : EnemyState.Dormant;
        public bool IsRunning => m_Running;
        /// <summary>True while a tranquilizer dart has it asleep.</summary>
        public bool IsSedated => m_SedateTimer > 0f;
        public float SedatedSecondsLeft => Mathf.Max(0f, m_SedateTimer);
        /// <summary>True during the catch jumpscare.</summary>
        public bool IsScaring { get; private set; }
        /// <summary>Set by the blackout: the creature makes no sound at all.</summary>
        public bool Silenced { get; set; }
        [Header("Volume (kept low on purpose)")]
        [Range(0f, 1f)] public float voiceVolume = 0.35f;
        [Header("Catch jumpscare")]
        public float scareDistance = 0.46f;
        public float scareLungeSeconds = 0.09f;
        public float scareHoldSeconds = 0.75f;
        /// <summary>Distance to the nearest player, for wrist warnings.</summary>
        public float NearestPlayerDistance { get; private set; } = float.MaxValue;

        void Awake() => EnsureBrain();

        void EnsureBrain()
        {
            if (Brain != null) return;
            if (director == null) director = MissionDirector.Active;
            m_Tuning = overrideTuning ? tuning : director != null && director.Config != null ? director.Config.enemy : new EnemyTuning();
            Brain = new EnemyBrain(m_Tuning);
            Brain.StateChanged += OnStateChanged;
        }

        void OnEnable()
        {
            EnsureBrain();
            if (director != null && director.noise != null) director.noise.NoiseEmitted += OnNoise;
        }

        void OnDisable()
        {
            if (director != null && director.noise != null) director.noise.NoiseEmitted -= OnNoise;
        }

        /// <summary>Resets the drone to its dock for a fresh run. It wakes after a short grace period.</summary>
        public void BeginRun(int seed)
        {
            EnsureBrain();
            Brain.Sleep();
            m_Running = true;
            m_WakeTimer = m_Tuning.wakeDelaySeconds * (director != null ? ContractConditions.WakeDelayMultiplier(director.Condition) : 1f);
            m_PatrolIndex = 0;
            m_LurkRandom = new System.Random(seed ^ 0x6a09e667);
            m_LurkUntil = 0f;
            m_NoLurkBefore = m_Clock + 25f;
            m_HasPath = false;
            m_Target = null;
            m_DoorTimer = 0f;
            m_SedateTimer = 0f;
            IsScaring = false;
            StopAllCoroutines();
            if (dock != null) transform.SetPositionAndRotation(dock.position, dock.rotation);
            RefreshPresentation(true);
        }

        /// <summary>Powers down (hub, results). Called for every run end and at scene start.</summary>
        public void EndRun()
        {
            EnsureBrain();
            m_Running = false;
            m_SedateTimer = 0f;
            Brain.Sleep();
            m_HasPath = false;
            if (voice != null) voice.Stop();
            RefreshPresentation(true);
        }

        /// <summary>Wakes immediately, skipping the grace period (tests, dev hotkeys).</summary>
        public void WakeNow()
        {
            EnsureBrain();
            m_Running = true;
            m_WakeTimer = 0f;
            m_LurkUntil = 0f;
            m_NoLurkBefore = m_Clock + 25f;
            Brain.WakeUp();
        }

        /// <summary>
        /// Puts the creature to sleep for a few seconds (tranquilizer dart). It drops whatever it was
        /// doing, cannot see, hear or catch anyone, and then wakes up and goes back on patrol.
        /// </summary>
        public bool Tranquilize(float seconds)
        {
            if (!m_Running || IsScaring || seconds <= 0f) return false;
            m_SedateTimer = Mathf.Max(m_SedateTimer, seconds * Mathf.Max(0.1f, sedationMultiplier));
            Brain.Sleep();
            m_HasPath = false;
            if (voice != null) voice.Stop();
            RefreshPresentation(true);
            return true;
        }

        void OnNoise(NoiseEvent noise)
        {
            if (!m_Running || Brain == null || IsSedated) return;
            if (IsForbidden(noise.Position) && !IsNearGraph(noise.Position)) return;
            if (Brain.HearNoise(noise, transform.position))
            {
                m_HasPath = false;
                LastHeardNoiseTime = Time.time;
            }
        }

        void Update()
        {
            if (!m_Running || Brain == null) return;
            if (director != null && director.HazardsPaused) return;
            Step(Time.deltaTime);
        }

        /// <summary>One simulation step. Public so play-mode tests can advance the drone deterministically.</summary>
        public void Step(float dt)
        {
            if (!m_Running || IsScaring) return;
            m_Clock += dt;

            if (m_SedateTimer > 0f)
            {
                m_SedateTimer -= dt;
                if (m_SedateTimer <= 0f)
                {
                    m_SedateTimer = 0f;
                    Brain.WakeUp();
                }
                RefreshPresentation(false);
                return;
            }

            if (Brain.State == EnemyState.Dormant)
            {
                m_WakeTimer -= dt;
                if (m_WakeTimer <= 0f) Brain.WakeUp();
                RefreshPresentation(false);
                return;
            }

            bool sees = Perceive(out Vector3 playerPosition, out bool playerSafe);
            m_SeesNow = sees && !playerSafe;
            if (m_SeesNow)
            {
                // Remember where the quarry is and which way it is going, for when it slips out of sight.
                if (m_SawLastStep && dt > 0f)
                    m_LastSeenVelocity = Vector3.Lerp(m_LastSeenVelocity, Vector3.ClampMagnitude((playerPosition - m_LastSeenPosition) / dt, 4f), 0.35f);
                else m_LastSeenVelocity = Vector3.zero;
                m_LastSeenPosition = playerPosition;
            }
            else if (m_SawLastStep && Brain.State == EnemyState.Chase)
            {
                // Just lost sight: keep going to where they were heading, not where they were.
                Brain.Redirect(PredictQuarry(1.2f));
            }
            m_SawLastStep = m_SeesNow;
            Vector3 destination = CurrentDestination();
            bool arrived = m_HasPath && m_PathIndex >= m_Path.Count;

            Brain.Tick(new EnemyPercept
            {
                deltaTime = dt,
                canSeePlayer = sees,
                playerInSafeZone = playerSafe,
                playerPosition = playerPosition,
                atPointOfInterest = arrived,
            });

            if (Brain.State == EnemyState.Chase && sees && !playerSafe && m_Target != null)
            {
                Vector3 flat = playerPosition - transform.position;
                flat.y = 0f;
                if (flat.magnitude <= m_Tuning.catchRadius)
                {
                    Catch(m_Target);
                    return;
                }
            }

            Move(dt, destination);
            RefreshPresentation(false);
        }

        // ------------------------------------------------------------------ senses

        bool Perceive(out Vector3 playerPosition, out bool playerSafe)
        {
            playerPosition = transform.position;
            playerSafe = true;
            NearestPlayerDistance = float.MaxValue;
            m_Target = null;
            if (director == null) return false;

            bool anySeen = false;
            float best = float.MaxValue;
            var players = director.Players;
            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player == null) continue;
                Vector3 head = player.HeadPosition;
                float distance = Vector3.Distance(head, transform.position);
                NearestPlayerDistance = Mathf.Min(NearestPlayerDistance, distance);

                bool seen = !player.InSafeZone && CanSee(head, distance);
                // Prefer a visible player; otherwise remember the nearest one.
                if ((seen && !anySeen) || (seen == anySeen && distance < best))
                {
                    best = distance;
                    m_Target = player;
                    playerPosition = head;
                    playerSafe = player.InSafeZone;
                }
                anySeen |= seen;
            }
            return anySeen;
        }

        bool CanSee(Vector3 point, float distance)
        {
            if (distance > m_Tuning.sightRange) return false;
            Vector3 origin = eye != null ? eye.position : transform.position;
            Vector3 to = point - origin;
            // Very close targets are noticed regardless of facing.
            if (distance > 2.4f && Vector3.Angle(transform.forward, to) > m_Tuning.sightHalfAngle) return false;
            return !Physics.Linecast(origin, point, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore);
        }

        bool IsForbidden(Vector3 point)
        {
            if (director != null && director.safeZone != null && director.safeZone.Contains(point)) return true;
            var zones = EnemyExclusionRegistry.Zones;
            for (int i = 0; i < zones.Count; i++)
            {
                if (zones[i] != null && zones[i].Contains(point)) return true;
            }
            return false;
        }

        bool IsNearGraph(Vector3 point)
        {
            var node = EnemyWaypoint.FindNearest(graph, point);
            return node != null && Vector3.Distance(node.Position, point) <= maxOffGraphDistance;
        }

        // ------------------------------------------------------------------ movement

        Vector3 PredictQuarry(float seconds)
        {
            Vector3 guess = m_LastSeenPosition + m_LastSeenVelocity * seconds;
            if (Physics.Linecast(m_LastSeenPosition, guess, out RaycastHit hit, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                guess = Vector3.MoveTowards(hit.point, m_LastSeenPosition, 0.5f);
            return guess;
        }

        /// <summary>True if it could fly straight from a to b at its current height without hitting anything or entering a no-go zone.</summary>
        bool ClearLine(Vector3 a, Vector3 b)
        {
            a.y = b.y = transform.position.y;
            Vector3 d = b - a;
            float length = d.magnitude;
            if (length < 0.05f) return true;
            if (Physics.SphereCast(a, 0.22f, d / length, out _, length, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore)) return false;
            for (float s = 0.75f; s < length; s += 0.75f)
            {
                if (IsForbidden(a + d * (s / length))) return false;
            }
            return !IsForbidden(b);
        }

        Vector3 CurrentDestination()
        {
            if (Brain.State != EnemyState.Patrol)
            {
                // With a clear run at what it is hunting (the quarry, or the place a sound came from), go
                // straight for it; otherwise plan through the route points.
                bool chasing = Brain.State == EnemyState.Chase;
                Vector3 goal = AtHoverHeight(Brain.PointOfInterest);
                if (ClearLine(transform.position, goal))
                {
                    if (!m_Direct || (m_PathGoal - Brain.PointOfInterest).sqrMagnitude > 0.15f * 0.15f || (chasing && m_PathIndex >= m_Path.Count))
                    {
                        m_Path.Clear();
                        m_Path.Add(goal);
                        m_PathIndex = 0;
                        m_PathGoal = Brain.PointOfInterest;
                        m_HasPath = true;
                        m_Direct = true;
                    }
                    return Brain.PointOfInterest;
                }
                if (m_Direct)
                {
                    m_Direct = false;
                    m_HasPath = false;
                }
            }
            else m_Direct = false;

            if (Brain.State == EnemyState.Patrol)
            {
                if (patrolRoute == null || patrolRoute.Length == 0) return transform.position;
                var node = patrolRoute[m_PatrolIndex % patrolRoute.Length];
                Vector3 goal = node != null ? node.Position : transform.position;
                if (!m_HasPath || (m_PathGoal - goal).sqrMagnitude > 0.01f) BuildPath(goal, false);
                if (m_PathIndex >= m_Path.Count)
                {
                    m_PatrolIndex = (m_PatrolIndex + 1) % patrolRoute.Length;
                    // Now and then it simply stops here, and listens.
                    if (m_Clock >= m_NoLurkBefore && m_LurkRandom.NextDouble() < lurkChance)
                        m_LurkUntil = m_Clock + Mathf.Lerp(lurkSeconds.x, lurkSeconds.y, (float)m_LurkRandom.NextDouble());
                    node = patrolRoute[m_PatrolIndex];
                    goal = node != null ? node.Position : transform.position;
                    BuildPath(goal, false);
                }
                return goal;
            }

            Vector3 poi = Brain.PointOfInterest;
            if (!m_HasPath || (m_PathGoal - poi).sqrMagnitude > 0.5f * 0.5f) BuildPath(poi, true);
            return poi;
        }

        void BuildPath(Vector3 goal, bool allowFinalApproach)
        {
            m_PathGoal = goal;
            m_Path.Clear();
            m_PathIndex = 0;
            m_HasPath = true;
            if (graph == null || graph.Length == 0) return;

            var start = EnemyWaypoint.FindNearest(graph, transform.position);
            var end = EnemyWaypoint.FindNearest(graph, goal);
            if (start == null || end == null) return;

            if (EnemyWaypoint.FindPath(graph, start, end, m_NodePath))
            {
                // Skip the first node if we are already past it on the way to the second.
                int first = 0;
                if (m_NodePath.Count > 1)
                {
                    Vector3 toFirst = m_NodePath[0].Position - transform.position;
                    Vector3 firstToSecond = m_NodePath[1].Position - m_NodePath[0].Position;
                    if (Vector3.Dot(toFirst, firstToSecond) < 0f && toFirst.magnitude < 1.5f) first = 1;
                }
                for (int i = first; i < m_NodePath.Count; i++) m_Path.Add(AtHoverHeight(m_NodePath[i].Position));
            }
            else
            {
                m_Path.Add(AtHoverHeight(start.Position));
            }

            if (!allowFinalApproach) return;
            Vector3 final = AtHoverHeight(goal);
            Vector3 last = AtHoverHeight(end.Position);
            if (Vector3.Distance(last, final) <= huntOffGraphDistance
                && !IsForbidden(final)
                && !Physics.Linecast(last, final, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore))
            {
                m_Path.Add(final);
            }

            // Cut corners: skip route points it can already fly straight past.
            int guard = 0;
            while (m_Path.Count >= 2 && guard++ < 8 && ClearLine(transform.position, m_Path[1])) m_Path.RemoveAt(0);
        }

        Vector3 AtHoverHeight(Vector3 p) => new Vector3(p.x, hoverHeight, p.z);

        void Move(float dt, Vector3 lookGoal)
        {
            if (IsLurking) return; // perfectly still
            if (m_PathIndex >= m_Path.Count)
            {
                // Arrived: sweep the eye around so the player can read where it is looking.
                m_LingerSpin += dt;
                float sweep = Mathf.Sin(m_LingerSpin * 1.3f) * 70f;
                transform.Rotate(0f, sweep * dt, 0f, Space.World);
                return;
            }
            m_LingerSpin = 0f;

            bool chasing = Brain.State == EnemyState.Chase;
            Vector3 target = m_Path[m_PathIndex];
            Vector3 to = target - transform.position;
            to.y = 0f; // it steers in plan; height is eased separately below
            float distance = to.magnitude;
            if (distance <= arriveDistance)
            {
                m_PathIndex++;
                return;
            }

            Vector3 flat = to;
            if (flat.sqrMagnitude > 0.0001f)
            {
                transform.rotation = Quaternion.RotateTowards(transform.rotation,
                    Quaternion.LookRotation(flat.normalized, Vector3.up), turnSpeed * (chasing ? 1.8f : 1f) * dt);
            }

            // It lunges over the last few metres of a chase.
            float speed = Brain.SpeedForState;
            if (chasing && m_SeesNow && NearestPlayerDistance < 4f) speed *= lungeMultiplier;
            float step = Mathf.Min(distance, speed * dt);
            Vector3 next = transform.position + to / distance * step;

            // Rise or sink toward a quarry that is floating above or below it.
            // Only when it has left its route to fly straight at them; the routes are laid out at its usual height.
            float wantHeight = hoverHeight;
            if (chasing && m_Direct && m_SeesNow && m_Target != null)
                wantHeight = Mathf.Clamp(m_Target.HeadPosition.y - 0.15f, hoverHeight - 0.5f, hoverHeight + 0.8f);
            next.y = Mathf.MoveTowards(transform.position.y, wantHeight, 1.1f * dt);

            // Doors: wait, then open them. Slow and loud on purpose so the player gets warning.
            if (doors != null)
            {
                foreach (var door in doors)
                {
                    if (door == null || !door.BlocksSegment(transform.position, transform.position + to / distance * Mathf.Min(distance, 1.4f))) continue;
                    m_DoorTimer += dt;
                    if (m_DoorTimer >= (chasing ? doorOpenDelay * 0.5f : doorOpenDelay) && !door.IsOpen && !door.locked) door.SetOpen(true);
                    return;
                }
            }
            m_DoorTimer = 0f;

            if (IsForbidden(next)) return;

            // Off its route (flying straight at something, or at an unusual height) it checks where it is
            // about to be, so it can never push into a wall, a crate or anything hanging from the ceiling.
            bool offRoute = m_Direct || Mathf.Abs(next.y - hoverHeight) > 0.02f;
            if (offRoute && Physics.CheckSphere(next, 0.13f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore))
            {
                next.y = transform.position.y; // try the same step without changing height
                if (Physics.CheckSphere(next, 0.13f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                {
                    // Blocked: stay put this step, drift back toward its usual height if that is clear, and plan again.
                    Vector3 settle = transform.position;
                    settle.y = Mathf.MoveTowards(settle.y, hoverHeight, 1.1f * dt);
                    if (!Physics.CheckSphere(settle, 0.13f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore)) transform.position = settle;
                    m_HasPath = false;
                    m_Direct = false;
                    return;
                }
            }
            transform.position = next;
        }

        void Catch(PlayerContext player)
        {
            if (IsScaring) return;
            if (isActiveAndEnabled && player != null && player.head != null) StartCoroutine(Jumpscare(player));
            else FinishCatch(player);
        }

        void FinishCatch(PlayerContext player)
        {
            IsScaring = false;
            if (director != null) director.FailMission(FailureReason.Caught, player != null ? player.Id : PlayerId.Local);
        }

        [Header("Jumpscare")]
        [Tooltip("Before it strikes, every light dies and everything goes silent for this long.")]
        public float scareDarkSeconds = 0.6f;
        [Tooltip("A single flash in the dark shows it above the player for this long...")]
        public float scareGlimpseSeconds = 0.14f;
        [Tooltip("...then darkness again for this long before it lands.")]
        public float scareSecondDarkSeconds = 0.34f;
        [Tooltip("How long it is on the player's visor, biting.")]
        public float scareMaulSeconds = 2.6f;
        [Tooltip("How close its face comes on each bite (metres from the eyes to its centre).")]
        public float scareBiteDistance = 0.3f;
        [Tooltip("Black and silent, except for the sound of it feeding, before the run ends.")]
        public float scareAfterSeconds = 0.9f;
        /// <summary>0 = not scaring, 1 = the dark and the glimpse, 2 = on the player's visor, 3 = blackout afterwards.</summary>
        public int ScarePhase { get; private set; }
        /// <summary>How many times it has bitten during the current jumpscare.</summary>
        public int ScareBites { get; private set; }

        /// <summary>
        /// The catch, in four beats.
        /// 1. Every light in the wreck dies, the flashlight with them, and the ship goes silent.
        /// 2. One flash: it is right there, above and in front, legs spread. Dark again.
        /// 3. It lands on the visor: far closer than before, under a red strobe, shrieking, its front legs
        ///    wrapped round the player's view. It bites again and again, and with every bite the visor
        ///    cracks further, the view flashes red and the controllers kick.
        /// 4. Black. Only the sound of it feeding. Then the run is over.
        /// The camera itself is never moved or shaken; the creature and the visor do all the moving.
        /// </summary>
        IEnumerator Jumpscare(PlayerContext player)
        {
            IsScaring = true;
            ScarePhase = 1;
            ScareBites = 0;
            Transform head = player.head;
            if (voice != null) voice.Stop();
            if (hum != null) hum.Stop();
            var lib = Sfx.Library;
            var visor = player.visor;
            if (visor != null) visor.Clear();

            // --- 1. the dark
            float total = scareDarkSeconds + scareGlimpseSeconds + scareSecondDarkSeconds + scareLungeSeconds + scareMaulSeconds + scareAfterSeconds;
            var blackout = director != null ? director.blackout : null;
            if (blackout != null) blackout.TriggerNow(total + 1f);
            Tools.FlashlightTool torch = null;
            if (player.tools != null)
            {
                foreach (var tool in player.tools)
                {
                    if (tool is Tools.FlashlightTool f) torch = f;
                }
            }
            if (torch != null && torch.beam != null) torch.beam.enabled = false;
            if (eyeLight != null) eyeLight.enabled = false;
            SetEyesDark();
            if (lib != null) Sfx.PlayUi(lib.powerDown, 0.55f, 1.6f);
            player.PulseBoth(0.35f, 0.12f);

            Vector3 Ahead()
            {
                Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
                return flat.sqrMagnitude < 0.001f ? Vector3.forward : flat.normalized;
            }
            void Hover(float distance, float rise)
            {
                Vector3 ahead = Ahead();
                Vector3 at = head.position + ahead * distance + Vector3.up * rise;
                transform.SetPositionAndRotation(at, Quaternion.LookRotation((head.position - at).normalized, Vector3.up));
            }

            float t = 0f;
            while (t < scareDarkSeconds)
            {
                t += Time.unscaledDeltaTime;
                Hover(2.2f, 0.5f);
                // A heartbeat in the hands while there is nothing to see.
                if (Mathf.Repeat(t, 0.3f) < Time.unscaledDeltaTime) player.PulseBoth(0.5f, 0.05f);
                yield return null;
            }

            // --- 2. the glimpse: one flash shows it hanging there, then nothing
            if (lib != null) Sfx.PlayUi(lib.flashlightClick, 1f, 0.7f);
            t = 0f;
            while (t < scareGlimpseSeconds)
            {
                t += Time.unscaledDeltaTime;
                Hover(1.25f, 0.42f);
                if (eyeLight != null)
                {
                    eyeLight.enabled = true;
                    eyeLight.color = new Color(1f, 0.85f, 0.8f);
                    eyeLight.intensity = 7f;
                    eyeLight.range = 6f;
                }
                SetEyesBlazing(3f);
                yield return null;
            }
            if (eyeLight != null) eyeLight.enabled = false;
            SetEyesDark();
            t = 0f;
            while (t < scareSecondDarkSeconds)
            {
                t += Time.unscaledDeltaTime;
                Hover(1.25f, 0.42f);
                yield return null;
            }

            // --- 3. it lands on the visor
            ScarePhase = 2;
            if (lib != null)
            {
                Sfx.PlayUi(lib.droneScare != null ? lib.droneScare : lib.droneCatch, 1f);
                Sfx.PlayUi(lib.droneCatch, 1f, 0.75f);
                Sfx.PlayUi(lib.impactHeavy, 1f, 0.5f);
                Sfx.PlayUi(lib.oxygenCritical, 0.8f, 1.2f);
            }
            player.PulseBoth(1f, scareLungeSeconds + 0.25f);
            if (visor != null) visor.Crack(0.75f);
            Vector3 start = transform.position;
            Quaternion startRotation = transform.rotation;
            var random = new System.Random(71);
            const float biteEvery = 0.6f;
            int bitesDone = 0;

            t = 0f;
            while (t < scareLungeSeconds + scareMaulSeconds)
            {
                t += Time.unscaledDeltaTime;
                Vector3 ahead = Ahead();
                float maul = Mathf.Max(0f, t - scareLungeSeconds);

                // It snaps in to bite, hangs back a hand's width, snaps in again.
                float cycle = Mathf.Repeat(maul, biteEvery) / biteEvery;
                float bite = Mathf.Pow(Mathf.Sin(cycle * Mathf.PI), 8f);
                float distance = Mathf.Lerp(scareDistance, scareBiteDistance, bite);
                Vector3 shake = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f) * 0.06f;
                Vector3 target = head.position + ahead * distance + Vector3.down * 0.03f + shake;
                Quaternion face = Quaternion.LookRotation((head.position - target).normalized, Vector3.up)
                    * Quaternion.Euler(((float)random.NextDouble() - 0.5f) * 12f, ((float)random.NextDouble() - 0.5f) * 12f, Mathf.Sin(maul * 29f) * 14f);
                float k = Mathf.Clamp01(t / scareLungeSeconds);
                k = 1f - (1f - k) * (1f - k);
                transform.SetPositionAndRotation(Vector3.Lerp(start, target, k), Quaternion.Slerp(startRotation, face, k));

                // The moment each bite lands: the visor cracks, the view goes red, the hands kick, it shrieks.
                int bitesDue = 1 + Mathf.FloorToInt((maul + biteEvery * 0.5f) / biteEvery);
                if (maul > 0f && bitesDue > bitesDone + 1)
                {
                    bitesDone = bitesDue - 1;
                    ScareBites = bitesDone;
                    if (visor != null) visor.Crack(0.6f);
                    player.PulseBoth(1f, 0.2f);
                    if (lib != null)
                    {
                        Sfx.PlayUi(lib.panelBreak, 0.8f, 1.5f + bitesDone * 0.12f);
                        Sfx.PlayUi(lib.droneScare != null ? lib.droneScare : lib.droneCatch, 0.9f, 1.05f + (bitesDone % 2) * 0.3f);
                    }
                }

                // A hard red strobe from its own eyes is the only light there is.
                bool flash = Mathf.Repeat(t * 14f, 1f) < 0.6f;
                if (eyeLight != null)
                {
                    eyeLight.enabled = flash;
                    eyeLight.color = new Color(1f, 0.04f, 0.02f);
                    eyeLight.intensity = 10f;
                    eyeLight.range = 6f;
                }
                SetEyesBlazing(flash ? 4.5f : 1.2f);
                yield return null;
            }

            // --- 4. black, and the sound of it feeding
            ScarePhase = 3;
            if (eyeLight != null)
            {
                eyeLight.enabled = false;
                eyeLight.range = 9f;
            }
            SetEyesDark();
            if (player.fader != null) player.fader.FadeTo(1f, 0.04f);
            t = 0f;
            float nextCrunch = 0f;
            while (t < scareAfterSeconds)
            {
                t += Time.unscaledDeltaTime;
                if (t >= nextCrunch && lib != null)
                {
                    nextCrunch = t + 0.16f + (float)random.NextDouble() * 0.14f;
                    Sfx.PlayUi(lib.cutterSparks, 0.5f, 0.45f + (float)random.NextDouble() * 0.2f);
                    player.PulseBoth(0.35f, 0.04f);
                }
                yield return null;
            }

            // --- over: put the world back the way it was before the run ends
            if (torch != null && torch.beam != null) torch.beam.enabled = torch.IsOn;
            if (blackout != null) blackout.EndBlackout(true);
            if (visor != null) visor.Clear();
            ScarePhase = 0;
            FinishCatch(player);
        }

        void SetEyesDark()
        {
            if (m_Block == null) m_Block = new MaterialPropertyBlock();
            m_Block.SetColor(k_BaseColor, Color.black);
            m_Block.SetColor(k_Emission, Color.black);
            if (eyeRenderer != null) eyeRenderer.SetPropertyBlock(m_Block);
            if (extraEyes == null) return;
            foreach (var eye in extraEyes) if (eye != null) eye.SetPropertyBlock(m_Block);
        }

        void SetEyesBlazing(float strength)
        {
            if (m_Block == null) m_Block = new MaterialPropertyBlock();
            var red = new Color(1f, 0.05f, 0.03f);
            m_Block.SetColor(k_BaseColor, red);
            m_Block.SetColor(k_Emission, red * strength);
            if (eyeRenderer != null) eyeRenderer.SetPropertyBlock(m_Block);
            if (extraEyes == null) return;
            foreach (var eye in extraEyes) if (eye != null) eye.SetPropertyBlock(m_Block);
        }

        // ------------------------------------------------------------------ presentation

        void OnStateChanged(EnemyState previous, EnemyState next)
        {
            m_HasPath = false;
            m_Direct = false;
            // The trail went cold: search further along the way the quarry was heading.
            if (previous == EnemyState.Chase && next == EnemyState.Search) Brain.Redirect(PredictQuarry(3f));
            m_DoorTimer = 0f;
            var lib = Sfx.Library;

            if (voice != null)
            {
                voice.Stop();
                if (next == EnemyState.Chase && lib != null && lib.droneChase != null && Application.isPlaying && !Silenced)
                {
                    voice.clip = lib.droneChase;
                    voice.loop = true;
                    voice.volume = voiceVolume;
                    voice.Play();
                }
            }

            // State changes are announced, but quietly: these are cues, not alarms.
            if (lib != null && Application.isPlaying && !Silenced && !IsSedated)
            {
                if (next == EnemyState.Investigate) Sfx.PlayAt(lib.droneAlert, transform.position, voiceVolume, 1f, 20f);
                else if (next == EnemyState.Search) Sfx.PlayAt(lib.droneLost, transform.position, voiceVolume, 1f, 20f);
                else if (next == EnemyState.Patrol && previous != EnemyState.Dormant) Sfx.PlayAt(lib.droneLost, transform.position, voiceVolume * 0.6f, 0.8f, 16f);
            }

            RefreshPresentation(true);
        }

        void RefreshPresentation(bool force)
        {
            EnemyState state = State;
            Color color;
            float blinkHz;
            string symbol;
            float humPitch;

            switch (state)
            {
                case EnemyState.Patrol:
                    color = new Color(0.75f, 0.12f, 0.05f); blinkHz = 0f; symbol = ""; humPitch = 1f; break;
                case EnemyState.Investigate:
                    color = new Color(1f, 0.55f, 0.1f); blinkHz = 2f; symbol = ""; humPitch = 1.12f; break;
                case EnemyState.Chase:
                    color = new Color(1f, 0.05f, 0.03f); blinkHz = 9f; symbol = ""; humPitch = 1.35f; break;
                case EnemyState.Search:
                    color = new Color(1f, 0.35f, 0.15f); blinkHz = 1f; symbol = ""; humPitch = 1.06f; break;
                default:
                    color = Color.black; blinkHz = 0f; symbol = ""; humPitch = 0.8f; break;
            }

            float blink = blinkHz <= 0f ? 1f : (Mathf.Sin(Time.time * blinkHz * Mathf.PI * 2f) > 0f ? 1f : 0.15f);
            float brightness = state == EnemyState.Dormant ? 0f : blink * (1f + (state == EnemyState.Patrol ? Brain.Suspicion : 0f));
            if (IsScaring)
            {
                color = new Color(1f, 0.05f, 0.03f);
                brightness = 2.2f;
            }

            if (eyeLight != null)
            {
                eyeLight.enabled = state != EnemyState.Dormant || IsScaring;
                eyeLight.color = color;
                eyeLight.intensity = 3f * brightness;
            }
            if (eyeRenderer != null)
            {
                if (m_Block == null) m_Block = new MaterialPropertyBlock();
                eyeRenderer.GetPropertyBlock(m_Block);
                // Calm, the eyes are wet black beads with a dull ember in them; they only burn when it hunts.
                float glow = IsScaring || state == EnemyState.Chase ? 2.5f : state == EnemyState.Investigate ? 1.0f : state == EnemyState.Search ? 0.8f : 0.28f;
                m_Block.SetColor(k_BaseColor, state == EnemyState.Dormant && !IsScaring ? new Color(0.02f, 0.015f, 0.015f) : Color.Lerp(new Color(0.03f, 0.01f, 0.01f), color, Mathf.Clamp01(glow * 0.5f)));
                m_Block.SetColor(k_Emission, color * (glow * brightness));
                eyeRenderer.SetPropertyBlock(m_Block);
                if (extraEyes != null)
                {
                    for (int i = 0; i < extraEyes.Length; i++)
                    {
                        if (extraEyes[i] != null) extraEyes[i].SetPropertyBlock(m_Block);
                    }
                }
            }
            if (glyph != null && glyph.text != symbol) glyph.text = symbol;

            if (hum != null && Application.isPlaying)
            {
                bool shouldPlay = m_Running && state != EnemyState.Dormant && !Silenced && !IsScaring && !IsLurking;
                if (shouldPlay && !hum.isPlaying) hum.Play();
                else if (!shouldPlay && hum.isPlaying) hum.Stop();
                hum.pitch = Mathf.Lerp(hum.pitch, humPitch, 0.1f);
            }
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.red;
            for (int i = 0; i + 1 < m_Path.Count; i++) Gizmos.DrawLine(m_Path[i], m_Path[i + 1]);
        }
    }

    /// <summary>Scene registry of enemy no-go volumes.</summary>
    public static class EnemyExclusionRegistry
    {
        static readonly List<EnemyExclusionZone> s_Zones = new List<EnemyExclusionZone>();
        public static IReadOnlyList<EnemyExclusionZone> Zones => s_Zones;
        public static void Register(EnemyExclusionZone zone) { if (zone != null && !s_Zones.Contains(zone)) s_Zones.Add(zone); }
        public static void Unregister(EnemyExclusionZone zone) => s_Zones.Remove(zone);
    }
}

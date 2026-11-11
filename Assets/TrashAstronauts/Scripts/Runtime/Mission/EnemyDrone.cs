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
        PlayerContext m_Target;

        public EnemyBrain Brain { get; private set; }
        public EnemyState State => Brain != null ? Brain.State : EnemyState.Dormant;
        public bool IsRunning => m_Running;
        /// <summary>Distance to the nearest player, for wrist warnings.</summary>
        public float NearestPlayerDistance { get; private set; } = float.MaxValue;

        void Awake() => EnsureBrain();

        void EnsureBrain()
        {
            if (Brain != null) return;
            if (director == null) director = MissionDirector.Active;
            m_Tuning = director != null && director.Config != null ? director.Config.enemy : new EnemyTuning();
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
            m_WakeTimer = m_Tuning.wakeDelaySeconds;
            m_PatrolIndex = 0;
            m_HasPath = false;
            m_Target = null;
            m_DoorTimer = 0f;
            if (dock != null) transform.SetPositionAndRotation(dock.position, dock.rotation);
            RefreshPresentation(true);
        }

        /// <summary>Powers down (hub, results). Called for every run end and at scene start.</summary>
        public void EndRun()
        {
            EnsureBrain();
            m_Running = false;
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
            Brain.WakeUp();
        }

        void OnNoise(NoiseEvent noise)
        {
            if (!m_Running || Brain == null) return;
            if (IsForbidden(noise.Position) && !IsNearGraph(noise.Position)) return;
            if (Brain.HearNoise(noise, transform.position)) m_HasPath = false;
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
            if (!m_Running) return;

            if (Brain.State == EnemyState.Dormant)
            {
                m_WakeTimer -= dt;
                if (m_WakeTimer <= 0f) Brain.WakeUp();
                RefreshPresentation(false);
                return;
            }

            bool sees = Perceive(out Vector3 playerPosition, out bool playerSafe);
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
            if (distance > 1.6f && Vector3.Angle(transform.forward, to) > m_Tuning.sightHalfAngle) return false;
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

        Vector3 CurrentDestination()
        {
            if (Brain.State == EnemyState.Patrol)
            {
                if (patrolRoute == null || patrolRoute.Length == 0) return transform.position;
                var node = patrolRoute[m_PatrolIndex % patrolRoute.Length];
                Vector3 goal = node != null ? node.Position : transform.position;
                if (!m_HasPath || (m_PathGoal - goal).sqrMagnitude > 0.01f) BuildPath(goal, false);
                if (m_PathIndex >= m_Path.Count)
                {
                    m_PatrolIndex = (m_PatrolIndex + 1) % patrolRoute.Length;
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
            if (Vector3.Distance(last, final) <= maxOffGraphDistance
                && !IsForbidden(final)
                && !Physics.Linecast(last, final, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore))
            {
                m_Path.Add(final);
            }
        }

        Vector3 AtHoverHeight(Vector3 p) => new Vector3(p.x, hoverHeight, p.z);

        void Move(float dt, Vector3 lookGoal)
        {
            if (m_PathIndex >= m_Path.Count)
            {
                // Arrived: sweep the eye around so the player can read where it is looking.
                m_LingerSpin += dt;
                float sweep = Mathf.Sin(m_LingerSpin * 1.3f) * 70f;
                transform.Rotate(0f, sweep * dt, 0f, Space.World);
                return;
            }
            m_LingerSpin = 0f;

            Vector3 target = m_Path[m_PathIndex];
            Vector3 to = target - transform.position;
            float distance = to.magnitude;
            if (distance <= arriveDistance)
            {
                m_PathIndex++;
                return;
            }

            Vector3 flat = new Vector3(to.x, 0f, to.z);
            if (flat.sqrMagnitude > 0.0001f)
            {
                transform.rotation = Quaternion.RotateTowards(transform.rotation,
                    Quaternion.LookRotation(flat.normalized, Vector3.up), turnSpeed * dt);
            }

            float step = Mathf.Min(distance, Brain.SpeedForState * dt);
            Vector3 next = transform.position + to / distance * step;

            // Doors: wait, then open them. Slow and loud on purpose so the player gets warning.
            if (doors != null)
            {
                foreach (var door in doors)
                {
                    if (door == null || !door.BlocksSegment(transform.position, transform.position + to / distance * Mathf.Min(distance, 1.4f))) continue;
                    m_DoorTimer += dt;
                    if (m_DoorTimer >= doorOpenDelay && !door.IsOpen && !door.locked) door.SetOpen(true);
                    return;
                }
            }
            m_DoorTimer = 0f;

            if (IsForbidden(next)) return;
            transform.position = next;
        }

        void Catch(PlayerContext player)
        {
            if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.droneCatch, transform.position, 1f);
            if (player != null) player.PulseBoth(0.8f, 0.3f);
            if (director != null) director.FailMission(FailureReason.Caught, player != null ? player.Id : PlayerId.Local);
        }

        // ------------------------------------------------------------------ presentation

        void OnStateChanged(EnemyState previous, EnemyState next)
        {
            m_HasPath = false;
            m_DoorTimer = 0f;
            var lib = Sfx.Library;

            if (voice != null)
            {
                voice.Stop();
                if (next == EnemyState.Chase && lib != null && lib.droneChase != null && Application.isPlaying)
                {
                    voice.clip = lib.droneChase;
                    voice.loop = true;
                    voice.Play();
                }
            }

            if (lib != null && Application.isPlaying)
            {
                if (next == EnemyState.Investigate) Sfx.PlayAt(lib.droneAlert, transform.position, 0.9f, 1f, 26f);
                else if (next == EnemyState.Search) Sfx.PlayAt(lib.droneLost, transform.position, 0.9f, 1f, 26f);
                else if (next == EnemyState.Patrol && previous != EnemyState.Dormant) Sfx.PlayAt(lib.droneLost, transform.position, 0.5f, 0.8f, 20f);
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

            if (eyeLight != null)
            {
                eyeLight.enabled = state != EnemyState.Dormant;
                eyeLight.color = color;
                eyeLight.intensity = 3f * brightness;
            }
            if (eyeRenderer != null)
            {
                if (m_Block == null) m_Block = new MaterialPropertyBlock();
                eyeRenderer.GetPropertyBlock(m_Block);
                m_Block.SetColor(k_BaseColor, state == EnemyState.Dormant ? new Color(0.05f, 0.05f, 0.05f) : color);
                m_Block.SetColor(k_Emission, color * (2.5f * brightness));
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
                bool shouldPlay = m_Running && state != EnemyState.Dormant;
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

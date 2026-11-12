using System.Collections;
using System.Collections.Generic;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Player;
using TrashAstronauts.Tools;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    public enum ScareKind { DistantBang, HullGroan, Scuttle, BreathBehind, LightsFlicker, Glimpse, Charge, Drop, DoorSlam }

    /// <summary>
    /// Keeps the wreck from ever feeling empty. While a contract is running and the player is out of the
    /// tug it stages something every so often: a bang far away, the hull groaning, something scuttling
    /// overhead, a breath just behind the helmet, the lights and the flashlight stuttering, or a tall
    /// shape standing at the end of the corridor that is gone when looked at properly. None of it is the
    /// creature and none of it can hurt; it also flickers the lights for real when a chase begins.
    /// No camera shake and nothing is forced into the player's face.
    /// </summary>
    public sealed class ScareDirector : MonoBehaviour
    {
        public MissionDirector director;
        public Vector2 interval = new Vector2(20f, 44f);
        [Tooltip("A small spider on a thread, for the drop scare. Kept switched off between uses.")]
        public GameObject dropSpider;
        public LineRenderer dropThread;
        [Tooltip("The shape used for a glimpse. Kept switched off between glimpses.")]
        public GameObject apparition;
        public float firstDelay = 24f;

        public int EventCount { get; private set; }
        public ScareKind LastKind { get; private set; }
        public bool GlimpseVisible => apparition != null && apparition.activeSelf;

        float m_Next;
        float m_LookedAt;
        float m_GlimpseUntil;
        EnemyState m_LastCreatureState = EnemyState.Dormant;
        readonly System.Random m_Random = new System.Random(4409);
        Coroutine m_Flicker;

        void OnEnable()
        {
            if (director == null) director = MissionDirector.Active;
            if (director != null) director.MissionStarted += OnMissionStarted;
            if (apparition != null) apparition.SetActive(false);
            if (dropSpider != null) dropSpider.SetActive(false);
        }

        void OnDisable()
        {
            if (director != null) director.MissionStarted -= OnMissionStarted;
        }

        void OnMissionStarted()
        {
            m_Next = Time.time + firstDelay;
            HideGlimpse(false);
        }

        float Range(float a, float b) => Mathf.Lerp(a, b, (float)m_Random.NextDouble());

        /// <summary>Scares are private to each headset: staged for, and around, the player wearing it.</summary>
        PlayerContext Player => director != null ? director.LocalPlayer : null;

        void Update()
        {
            if (director == null) return;
            var player = Player;
            // With no creature aboard there is nothing to be afraid of, and nothing is staged.
            bool live = director.Phase == MissionPhase.Active && !director.HazardsPaused && player != null && director.CreatureAboard;
            if (!live)
            {
                if (GlimpseVisible) HideGlimpse(false);
                return;
            }

            UpdateGlimpse(player);

            // A chase starting makes the lights and the flashlight stutter.
            var creature = director.ActiveEnemy;
            var state = creature != null ? creature.State : EnemyState.Dormant;
            if (state == EnemyState.Chase && m_LastCreatureState != EnemyState.Chase)
            {
                Flicker(player, 1.1f);
                if (Sfx.Library != null && creature != null) Sfx.PlayAt(Sfx.Library.droneScare, creature.transform.position, 0.75f, 0.85f, 40f);
            }
            m_LastCreatureState = state;

            if (player.InSafeZone || Time.time < m_Next) return;
            if (director.blackout != null && director.blackout.IsBlackout) return;
            if (state == EnemyState.Chase) return; // the real thing is scary enough
            Trigger((ScareKind)m_Random.Next(9));
        }

        /// <summary>Stages one event now. Returns false if it could not (for example no spot for a glimpse).</summary>
        public bool Trigger(ScareKind kind)
        {
            var player = Player;
            if (director == null || player == null) return false;
            m_Next = Time.time + Range(interval.x, interval.y);
            var lib = Sfx.Library;
            Vector3 head = player.HeadPosition;
            Vector3 forward = player.head != null ? player.head.forward : Vector3.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 0.01f ? forward.normalized : Vector3.forward;
            float angle = Range(0f, Mathf.PI * 2f);
            Vector3 far = head + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * Range(11f, 18f);

            bool done = true;
            switch (kind)
            {
                case ScareKind.DistantBang:
                    if (lib != null) Sfx.PlayAt(lib.impactHeavy, far, 0.95f, Range(0.5f, 0.65f), 42f);
                    break;
                case ScareKind.HullGroan:
                    if (lib != null) Sfx.PlayAt(lib.doorMove, far, 0.8f, Range(0.36f, 0.46f), 42f);
                    break;
                case ScareKind.Scuttle:
                    StartCoroutine(Scuttle(head, forward));
                    break;
                case ScareKind.BreathBehind:
                    if (lib != null) Sfx.PlayAt(lib.breathing, head - forward * 0.9f + Vector3.up * 0.1f, 0.55f, Range(0.68f, 0.78f), 6f);
                    break;
                case ScareKind.LightsFlicker:
                    Flicker(player, 1.6f);
                    break;
                case ScareKind.Glimpse:
                    done = ShowGlimpse(player, head, forward);
                    break;
                case ScareKind.DoorSlam:
                    done = SlamDoor(head, forward);
                    break;
                case ScareKind.Drop:
                    done = dropSpider != null && !IsDropping;
                    if (done) StartCoroutine(Drop(player));
                    break;
                case ScareKind.Charge:
                    done = ShowGlimpse(player, head, forward);
                    if (done) StartCoroutine(Charge(player));
                    break;
            }
            if (!done) return false;
            EventCount++;
            LastKind = kind;
            return true;
        }

        IEnumerator Scuttle(Vector3 head, Vector3 forward)
        {
            // Quick ticking that travels overhead from behind to ahead.
            var lib = Sfx.Library;
            Vector3 side = Vector3.Cross(Vector3.up, forward) * Range(-1.2f, 1.2f);
            for (int i = 0; i < 9; i++)
            {
                Vector3 at = head + forward * Mathf.Lerp(-4f, 6f, i / 8f) + side + Vector3.up * 1.1f;
                if (lib != null) Sfx.PlayAt(lib.cutterSparks, at, 0.35f, Range(1.5f, 1.9f), 10f);
                yield return new WaitForSeconds(Range(0.07f, 0.16f));
            }
        }

        // ------------------------------------------------------------------ lights

        void Flicker(PlayerContext player, float seconds)
        {
            if (!isActiveAndEnabled) return;
            if (m_Flicker != null) return;
            m_Flicker = StartCoroutine(FlickerRoutine(player, seconds));
        }

        IEnumerator FlickerRoutine(PlayerContext player, float seconds)
        {
            // The few wreck lights nearest the player, and their flashlight.
            var near = new List<Light>();
            var map = director.ActiveMap;
            if (map != null && map.lights != null)
            {
                foreach (var l in map.lights)
                {
                    if (l != null && l.enabled && Vector3.Distance(l.transform.position, player.HeadPosition) < 9f) near.Add(l);
                }
            }
            FlashlightTool torch = null;
            if (player.tools != null)
            {
                foreach (var tool in player.tools)
                {
                    if (tool is FlashlightTool f) torch = f;
                }
            }
            bool torchWasOn = torch != null && torch.IsOn && torch.beam != null;

            float end = Time.time + seconds;
            bool on = false;
            while (Time.time < end)
            {
                bool blackout = director.blackout != null && director.blackout.IsBlackout;
                if (blackout) break;
                foreach (var l in near) if (l != null) l.enabled = on;
                if (torchWasOn) torch.beam.enabled = on;
                on = !on;
                yield return new WaitForSeconds(on ? Range(0.03f, 0.09f) : Range(0.05f, 0.22f));
            }
            if (director.blackout == null || !director.blackout.IsBlackout)
            {
                foreach (var l in near) if (l != null) l.enabled = true;
            }
            // A jumpscare that began during the flicker owns the darkness: its own end turns the torch back on.
            // Re-lighting it here would light the torch through the scare.
            if (torchWasOn && torch != null && torch.beam != null && !ScareInProgress()) torch.beam.enabled = torch.IsOn;
            m_Flicker = null;
        }

        bool ScareInProgress()
        {
            var enemies = director != null ? director.enemies : null;
            if (enemies == null) return false;
            for (int i = 0; i < enemies.Length; i++)
                if (enemies[i] != null && enemies[i].IsScaring) return true;
            return false;
        }

        // ------------------------------------------------------------------ the glimpse

        bool ShowGlimpse(PlayerContext player, Vector3 head, Vector3 forward)
        {
            var map = director.ActiveMap;
            if (apparition == null || map == null || map.graph == null) return false;
            var creature = director.ActiveEnemy;

            // Somewhere ahead of the player, a fair way off, in plain view, and not where the real one is.
            EnemyWaypoint best = null;
            float bestScore = float.MaxValue;
            foreach (var node in map.graph)
            {
                if (node == null) continue;
                Vector3 p = node.Position;
                Vector3 to = p - head;
                to.y = 0f;
                float distance = to.magnitude;
                if (distance < 7f || distance > 17f) continue;
                if (Vector3.Angle(forward, to) > 50f) continue;
                if (creature != null && Vector3.Distance(creature.transform.position, p) < 5f) continue;
                if (director.safeZone != null && director.safeZone.Contains(p)) continue;
                Vector3 eye = new Vector3(p.x, 1.5f, p.z);
                if (Physics.Linecast(head, eye, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore)) continue;
                float score = Mathf.Abs(distance - 11f) + (float)m_Random.NextDouble() * 3f;
                if (score < bestScore) { bestScore = score; best = node; }
            }
            if (best == null) return false;

            Vector3 at = best.Position;
            at.y = 0f;
            Vector3 face = head - at;
            face.y = 0f;
            apparition.transform.SetPositionAndRotation(at, Quaternion.LookRotation(face.sqrMagnitude > 0.01f ? face.normalized : Vector3.forward, Vector3.up));
            apparition.SetActive(true);
            m_LookedAt = 0f;
            m_GlimpseUntil = Time.time + 6f;
            return true;
        }

        /// <summary>An open door somewhere behind the player bangs shut on its own. They will have to open it again, and that is not quiet.</summary>
        bool SlamDoor(Vector3 head, Vector3 forward)
        {
            if (director.wreckDoors == null) return false;
            World.SlidingDoor best = null;
            float bestDistance = float.MaxValue;
            foreach (var door in director.wreckDoors)
            {
                if (door == null || !door.IsOpen || door.locked || !door.gameObject.activeInHierarchy) continue;
                Vector3 to = door.transform.position - head;
                to.y = 0f;
                float distance = to.magnitude;
                if (distance < 2.5f || distance > 16f) continue;      // not on top of them, not out of earshot
                if (Vector3.Angle(forward, to) < 70f) continue;        // behind them, where they are not looking
                if (distance < bestDistance) { bestDistance = distance; best = door; }
            }
            if (best == null) return false;
            best.SetOpen(false);
            if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.impactHeavy, best.transform.position + Vector3.up, 1f, 0.7f, 30f);
            LastSlammedDoor = best;
            return true;
        }

        public World.SlidingDoor LastSlammedDoor { get; private set; }

        /// <summary>True while the spider on a thread is in front of the player.</summary>
        public bool IsDropping { get; private set; }

        IEnumerator Drop(PlayerContext player)
        {
            // A spider the size of a hand drops on a thread to eye level, an arm's length away, hangs
            // there turning slowly, and is yanked back up. It cannot touch or hurt the player.
            IsDropping = true;
            Vector3 head = player.HeadPosition;
            Vector3 forward = player.head != null ? Vector3.ProjectOnPlane(player.head.forward, Vector3.up) : Vector3.forward;
            forward = forward.sqrMagnitude > 0.01f ? forward.normalized : Vector3.forward;
            Vector3 hang = head + forward * 0.7f + Vector3.down * 0.06f;
            float ceiling = head.y + 1.1f;
            if (Physics.Raycast(hang, Vector3.up, out RaycastHit hit, 3f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore)) ceiling = hit.point.y;
            Vector3 top = new Vector3(hang.x, ceiling, hang.z);

            dropSpider.SetActive(true);
            if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.cutterSparks, hang, 0.45f, 1.9f, 6f);
            float spin = Range(0f, 360f);
            void Place(Vector3 at)
            {
                dropSpider.transform.SetPositionAndRotation(at, Quaternion.LookRotation(-forward, Vector3.up) * Quaternion.Euler(0f, Mathf.Sin(spin) * 25f, 0f));
                if (dropThread != null)
                {
                    dropThread.enabled = true;
                    dropThread.positionCount = 2;
                    dropThread.SetPosition(0, top);
                    dropThread.SetPosition(1, at + Vector3.up * 0.02f);
                }
            }
            for (float t = 0f; t < 0.3f; t += Time.deltaTime)
            {
                float k = t / 0.3f;
                Place(Vector3.Lerp(top, hang, 1f - (1f - k) * (1f - k)));
                yield return null;
            }
            if (player != null) player.PulseBoth(0.5f, 0.1f);
            for (float t = 0f; t < 1.4f && director != null && director.Phase == MissionPhase.Active; t += Time.deltaTime)
            {
                spin += Time.deltaTime * 2.2f;
                Place(hang + Vector3.up * (Mathf.Sin(t * 9f) * 0.012f));
                yield return null;
            }
            for (float t = 0f; t < 0.22f; t += Time.deltaTime)
            {
                Place(Vector3.Lerp(hang, top, t / 0.22f));
                yield return null;
            }
            if (dropThread != null) dropThread.enabled = false;
            dropSpider.SetActive(false);
            IsDropping = false;
        }

        /// <summary>True while the shape is running at the player.</summary>
        public bool IsCharging { get; private set; }

        IEnumerator Charge(PlayerContext player)
        {
            // It stands there for a moment. Then it runs straight at the player, far too fast, and is
            // gone a metre short with a shriek. It never touches them.
            IsCharging = true;
            yield return new WaitForSeconds(Range(0.5f, 1.1f));
            if (Sfx.Library != null && apparition != null) Sfx.PlayAt(Sfx.Library.droneAlert, apparition.transform.position + Vector3.up * 1.4f, 0.8f, 1.3f, 40f);
            while (GlimpseVisible && player != null)
            {
                Vector3 to = player.HeadPosition - apparition.transform.position;
                to.y = 0f;
                if (to.magnitude < 1.3f) break;
                apparition.transform.position += to.normalized * Mathf.Min(to.magnitude - 1.25f, 17f * Time.deltaTime);
                apparition.transform.rotation = Quaternion.LookRotation(to.normalized, Vector3.up);
                yield return null;
            }
            if (GlimpseVisible)
            {
                Vector3 at = apparition.transform.position + Vector3.up * 1.4f;
                apparition.SetActive(false);
                if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.droneScare, at, 0.9f, 1.15f, 30f);
                if (player != null) player.PulseBoth(0.8f, 0.25f);
            }
            IsCharging = false;
        }

        void UpdateGlimpse(PlayerContext player)
        {
            if (!GlimpseVisible) return;
            if (IsCharging) return; // a charge ends itself
            Vector3 head = player.HeadPosition;
            Vector3 to = apparition.transform.position + Vector3.up * 1.5f - head;
            Vector3 forward = player.head != null ? player.head.forward : Vector3.forward;
            // It stays as long as it is only in the corner of the eye. Look straight at it and it is gone.
            if (Vector3.Angle(forward, to) < 14f) m_LookedAt += Time.deltaTime;
            if (m_LookedAt > 0.35f || to.magnitude < 5f || Time.time > m_GlimpseUntil) HideGlimpse(m_LookedAt > 0.35f || to.magnitude < 5f);
        }

        void HideGlimpse(bool withSound)
        {
            if (apparition == null || !apparition.activeSelf) return;
            Vector3 at = apparition.transform.position + Vector3.up * 1.4f;
            apparition.SetActive(false);
            if (withSound && Sfx.Library != null) Sfx.PlayAt(Sfx.Library.droneLost, at, 0.4f, 1.5f, 20f);
        }
    }
}

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
    public enum ScareKind { DistantBang, HullGroan, Scuttle, BreathBehind, LightsFlicker, Glimpse, Charge }

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
        public Vector2 interval = new Vector2(28f, 60f);
        [Tooltip("The shape used for a glimpse. Kept switched off between glimpses.")]
        public GameObject apparition;
        public float firstDelay = 35f;

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

        PlayerContext Player => director != null && director.Players.Count > 0 ? director.Players[0] : null;

        void Update()
        {
            if (director == null) return;
            var player = Player;
            bool live = director.Phase == MissionPhase.Active && !director.HazardsPaused && player != null;
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
            Trigger((ScareKind)m_Random.Next(7));
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
            if (torchWasOn && torch != null && torch.beam != null) torch.beam.enabled = torch.IsOn;
            m_Flicker = null;
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

using System;
using System.Collections;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// At a random moment during a contract the wreck loses power: every ship light dies, the glow
    /// strips go dark, the ambience cuts out and the creature makes no sound. After a while the power
    /// stutters back. The tug keeps its lights, and the player's flashlight, the yellow rails and the
    /// creature's eyes still show, so it is frightening but never unplayable.
    /// </summary>
    public sealed class BlackoutController : MonoBehaviour
    {
        public MissionDirector director;
        [Tooltip("Lights in the wreck. Tug lights are deliberately not in this list.")]
        public Light[] lights;
        public Renderer[] glowStrips;
        public AudioSource[] ambience;
        [Range(0f, 1f)] public float ambientDimming = 0.2f;

        static readonly int k_Emission = Shader.PropertyToID("_EmissionColor");
        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        MaterialPropertyBlock m_Block;
        float[] m_AmbienceVolumes;
        Color m_Ambient;
        float m_Countdown;
        float m_Remaining;
        bool m_Scheduled;
        Coroutine m_Restore;

        public bool IsBlackout { get; private set; }
        public int BlackoutCount { get; private set; }
        /// <summary>True while a blackout is counting down to happen on its own this contract.</summary>
        public bool IsScheduled => m_Scheduled;
        public event Action<bool> Changed;
        /// <summary>How long the blackout in progress (or the last one) was set to last, in seconds.</summary>
        public float LastDuration { get; private set; }
        /// <summary>Whether the last blackout ended at once (true) or with the lights stuttering back.</summary>
        public bool LastEndInstant { get; private set; }
        /// <summary>
        /// On a co-op client the host keeps the schedule: nothing is scheduled or counted down here, and blackouts
        /// start and end only through <see cref="ApplyRemoteStart"/> and <see cref="ApplyRemoteEnd"/>.
        /// </summary>
        public bool Replica => director != null && director.Net != null && director.Net.IsClient;

        void OnEnable()
        {
            if (director == null) director = MissionDirector.Active;
            if (director == null) return;
            director.MissionStarted += OnMissionStarted;
            director.MissionEnded += OnMissionEnded;
        }

        void OnDisable()
        {
            if (director == null) return;
            director.MissionStarted -= OnMissionStarted;
            director.MissionEnded -= OnMissionEnded;
        }

        [Header("Damage")]
        [Tooltip("Share of the wreck's lights that are simply dead on any given contract.")]
        [Range(0f, 0.6f)] public float deadFraction = 0.22f;
        [Tooltip("Share that are failing: they stutter on and off.")]
        [Range(0f, 0.6f)] public float flickerFraction = 0.18f;
        /// <summary>Test seam: scales both fractions (0 = every light works).</summary>
        public static float? DamageOverride;

        readonly System.Collections.Generic.HashSet<Light> m_Dead = new System.Collections.Generic.HashSet<Light>();
        readonly System.Collections.Generic.List<Light> m_Failing = new System.Collections.Generic.List<Light>();
        float[] m_FailingNext = new float[0];
        public int DeadLightCount => m_Dead.Count;
        public int FailingLightCount => m_Failing.Count;
        public bool IsDead(Light l) => m_Dead.Contains(l);

        /// <summary>Decides from the run's seed which lights are dead and which are failing. Different every contract.</summary>
        void ApplyDamage(int seed)
        {
            ClearDamage();
            if (lights == null) return;
            float scale = DamageOverride ?? 1f;
            var rng = new System.Random(seed ^ 0x1f83d9ab);
            foreach (var l in lights)
            {
                if (l == null || l.transform.position.z < 7.75f) continue; // the airlock always has light
                double roll = rng.NextDouble();
                if (roll < deadFraction * scale) m_Dead.Add(l);
                else if (roll < (deadFraction + flickerFraction) * scale) m_Failing.Add(l);
            }
            m_FailingNext = new float[m_Failing.Count];
            SetLights(!IsBlackout);
        }

        void ClearDamage()
        {
            m_Dead.Clear();
            m_Failing.Clear();
            m_FailingNext = new float[0];
            if (!IsBlackout) SetLights(true);
        }

        void UpdateFailingLights()
        {
            for (int i = 0; i < m_Failing.Count; i++)
            {
                var l = m_Failing[i];
                if (l == null || Time.time < m_FailingNext[i]) continue;
                l.enabled = !l.enabled;
                m_FailingNext[i] = Time.time + (l.enabled ? UnityEngine.Random.Range(0.3f, 4f) : UnityEngine.Random.Range(0.04f, 0.5f));
            }
        }

        [Tooltip("Ambience that plays whatever wreck is docked.")]
        public AudioSource[] globalAmbience;

        /// <summary>Points the blackout at a different wreck's lights. Any blackout in progress ends first.</summary>
        public void SetTargets(Light[] newLights, Renderer[] newStrips, AudioSource[] mapAmbience)
        {
            EndBlackout(true);
            ClearDamage();
            lights = newLights ?? new Light[0];
            glowStrips = newStrips ?? new Renderer[0];
            var all = new System.Collections.Generic.List<AudioSource>();
            if (globalAmbience != null) all.AddRange(globalAmbience);
            if (mapAmbience != null) all.AddRange(mapAmbience);
            ambience = all.ToArray();
        }

        void OnMissionStarted()
        {
            EndBlackout(true);
            Schedule();
            ApplyDamage(director != null && director.Session != null ? director.Session.Seed : 0);
        }

        void OnMissionEnded(MissionResult result)
        {
            m_Scheduled = false;
            EndBlackout(true);
            ClearDamage();
        }

        void Schedule()
        {
            var config = director.Config;
            // Whoever started the run says whether this contract has blackouts at all (always, offline).
            bool thisRun = (director.CurrentRun.hazardFlags & TrashAstronauts.Core.Net.RunStartParams.HazardBlackouts) != 0;
            m_Scheduled = config.blackoutsEnabled && thisRun && !Replica;
            m_Countdown = UnityEngine.Random.Range(config.blackoutMinDelay, Mathf.Max(config.blackoutMinDelay, config.blackoutMaxDelay))
                * ContractConditions.BlackoutDelayMultiplier(director.Condition);
        }

        void Update()
        {
            if (director == null || director.Phase != MissionPhase.Active || director.HazardsPaused) return;
            if (!IsBlackout && m_Failing.Count > 0) UpdateFailingLights();

            if (IsBlackout)
            {
                if (Replica) return; // the host says when it ends
                m_Remaining -= Time.deltaTime;
                if (m_Remaining <= 0f)
                {
                    EndBlackout(false);
                    Schedule();
                }
            }
            else if (m_Scheduled)
            {
                m_Countdown -= Time.deltaTime;
                if (m_Countdown <= 0f)
                {
                    var config = director.Config;
                    TriggerNow(UnityEngine.Random.Range(config.blackoutMinDuration, Mathf.Max(config.blackoutMinDuration, config.blackoutMaxDuration)));
                }
            }
        }

        /// <summary>Starts a blackout immediately. Public for tests and the dev hotkey.</summary>
        public void TriggerNow(float seconds) => TriggerNow(seconds, true);

        /// <summary>The host's blackout began on a client (BlackoutStarted). False if one was already on.</summary>
        public bool ApplyRemoteStart(float seconds)
        {
            if (IsBlackout) return false;
            TriggerNow(seconds);
            return true;
        }

        /// <summary>The host's blackout ended on a client (BlackoutEnded). False if none was on.</summary>
        public bool ApplyRemoteEnd(bool instant)
        {
            if (!IsBlackout) return false;
            EndBlackout(instant);
            return true;
        }

        /// <summary>Starts a blackout immediately; <paramref name="sound"/> false leaves out the power-down in the ears.</summary>
        public void TriggerNow(float seconds, bool sound)
        {
            if (IsBlackout) return;
            if (m_Restore != null) { StopCoroutine(m_Restore); m_Restore = null; }
            IsBlackout = true;
            BlackoutCount++;
            m_Remaining = Mathf.Max(0.1f, seconds);
            LastDuration = m_Remaining;
            m_Ambient = RenderSettings.ambientLight;
            RenderSettings.ambientLight = m_Ambient * ambientDimming;

            SetLights(false);
            SetStrips(false);
            if (ambience != null)
            {
                m_AmbienceVolumes = new float[ambience.Length];
                for (int i = 0; i < ambience.Length; i++)
                {
                    if (ambience[i] == null) continue;
                    m_AmbienceVolumes[i] = ambience[i].volume;
                    ambience[i].volume = 0f;
                }
            }
            SetCreaturesSilent(true);
            if (sound && Sfx.Library != null) Sfx.PlayUi(Sfx.Library.powerDown, 0.45f);
            Changed?.Invoke(true);
        }

        /// <summary>Ends the blackout. Normally the lights stutter back; instant is used between runs.</summary>
        public void EndBlackout(bool instant)
        {
            if (!IsBlackout) return;
            IsBlackout = false;
            LastEndInstant = instant || !isActiveAndEnabled;
            RenderSettings.ambientLight = m_Ambient;
            if (ambience != null && m_AmbienceVolumes != null)
            {
                for (int i = 0; i < ambience.Length; i++)
                {
                    if (ambience[i] != null) ambience[i].volume = m_AmbienceVolumes[i];
                }
            }
            SetCreaturesSilent(false);

            if (instant || !isActiveAndEnabled)
            {
                SetLights(true);
                SetStrips(true);
            }
            else
            {
                if (Sfx.Library != null) Sfx.PlayUi(Sfx.Library.powerUp, 0.4f);
                m_Restore = StartCoroutine(StutterBackOn());
            }
            Changed?.Invoke(false);
        }

        IEnumerator StutterBackOn()
        {
            float[] pattern = { 0.06f, 0.12f, 0.05f, 0.2f, 0.07f };
            bool on = true;
            foreach (float wait in pattern)
            {
                SetLights(on);
                SetStrips(on);
                on = !on;
                yield return new WaitForSeconds(wait);
            }
            SetLights(true);
            SetStrips(true);
            m_Restore = null;
        }

        void SetLights(bool on)
        {
            if (lights == null) return;
            foreach (var l in lights)
            {
                if (l != null) l.enabled = on && !m_Dead.Contains(l);
            }
        }

        void SetStrips(bool on)
        {
            if (glowStrips == null) return;
            if (m_Block == null) m_Block = new MaterialPropertyBlock();
            foreach (var r in glowStrips)
            {
                if (r == null) continue;
                if (on)
                {
                    r.SetPropertyBlock(null);
                }
                else
                {
                    r.GetPropertyBlock(m_Block);
                    m_Block.SetColor(k_Emission, Color.black);
                    m_Block.SetColor(k_BaseColor, new Color(0.03f, 0.03f, 0.035f));
                    r.SetPropertyBlock(m_Block);
                }
            }
        }

        void SetCreaturesSilent(bool silent)
        {
            if (director == null || director.enemies == null) return;
            foreach (var enemy in director.enemies)
            {
                if (enemy != null) enemy.Silenced = silent;
            }
        }
    }
}

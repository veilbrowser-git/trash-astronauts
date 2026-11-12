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
        public event Action<bool> Changed;

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

        void OnMissionStarted()
        {
            EndBlackout(true);
            Schedule();
        }

        void OnMissionEnded(MissionResult result)
        {
            m_Scheduled = false;
            EndBlackout(true);
        }

        void Schedule()
        {
            var config = director.Config;
            m_Scheduled = config.blackoutsEnabled;
            m_Countdown = UnityEngine.Random.Range(config.blackoutMinDelay, Mathf.Max(config.blackoutMinDelay, config.blackoutMaxDelay));
        }

        void Update()
        {
            if (director == null || director.Phase != MissionPhase.Active || director.HazardsPaused) return;

            if (IsBlackout)
            {
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
        public void TriggerNow(float seconds)
        {
            if (IsBlackout) return;
            if (m_Restore != null) { StopCoroutine(m_Restore); m_Restore = null; }
            IsBlackout = true;
            BlackoutCount++;
            m_Remaining = Mathf.Max(0.1f, seconds);
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
            if (Sfx.Library != null) Sfx.PlayUi(Sfx.Library.powerDown, 0.45f);
            Changed?.Invoke(true);
        }

        /// <summary>Ends the blackout. Normally the lights stutter back; instant is used between runs.</summary>
        public void EndBlackout(bool instant)
        {
            if (!IsBlackout) return;
            IsBlackout = false;
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
                if (l != null) l.enabled = on;
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

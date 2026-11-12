using TMPro;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Tools
{
    /// <summary>
    /// Plasma cutter. Hold the trigger for a flame; hold the flame on each marked weld of a seam to burn
    /// it through. It heats up while firing and locks out when overheated until it cools, so long cuts
    /// need pacing. It is loud: both running it and cutting attract the drone.
    /// </summary>
    public sealed class PlasmaCutterTool : SuitTool
    {
        public Transform tip;
        public float tipRadius = 0.07f;
        public GameObject flame;
        public Light flameLight;
        public ParticleSystem sparks;
        public AudioSource loopSource;
        public TMP_Text readout;
        [Tooltip("Scaled along X from 0 to 1 to show heat.")]
        public Transform heatBar;
        public Renderer heatBarRenderer;

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        readonly Collider[] m_Hits = new Collider[8];
        MaterialPropertyBlock m_Block;
        CutterHeatModel m_Heat;
        CutterTuning m_CutterTuning;
        float m_NoiseTimer;
        float m_HapticTimer;
        float m_SparkSoundTimer;
        bool m_WasFiring;

        public CutterHeatModel Heat => m_Heat;
        public bool IsFiring => m_Heat != null && m_Heat.Firing;
        public bool IsCutting { get; private set; }

        protected override void Awake()
        {
            base.Awake();
            EnsureModel();
            SetFlame(false);
        }

        void EnsureModel()
        {
            if (m_Heat != null) return;
            var director = MissionDirector.Active;
            m_CutterTuning = director != null && director.Config != null ? director.Config.cutter : new CutterTuning();
            m_Heat = new CutterHeatModel(m_CutterTuning);
            m_Heat.OverheatChanged += OnOverheatChanged;
        }

        void OnOverheatChanged(bool overheated)
        {
            if (overheated)
            {
                if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.cutterOverheat, transform.position, 0.9f);
                if (HoldingHand != null) HoldingHand.Pulse(0.9f, 0.25f);
            }
            else if (HoldingHand != null)
            {
                HoldingHand.Pulse(0.3f, 0.05f);
            }
        }

        protected override void OnDocked()
        {
            SetFlame(false);
        }

        protected override void Tick(float dt)
        {
            EnsureModel();
            var shopDirector = MissionDirector.Active;
            m_Heat.HeatCapacityMultiplier = shopDirector != null && shopDirector.Profile != null && shopDirector.Profile.Data.cutterUpgrade
                ? shopDirector.Config.cutterUpgradeHeatMultiplier : 1f;
            bool firing = m_Heat.Tick(dt, IsHeld && TriggerHeld);
            if (firing != m_WasFiring)
            {
                m_WasFiring = firing;
                SetFlame(firing);
            }

            IsCutting = false;
            if (firing) FireStep(dt);
            RefreshReadout();
        }

        void FireStep(float dt)
        {
            var player = HoldingHand != null ? HoldingHand.Player : null;
            Vector3 tipPosition = tip != null ? tip.position : transform.position;

            CutPoint best = null;
            float bestSqr = float.MaxValue;
            int count = Physics.OverlapSphereNonAlloc(tipPosition, tipRadius, m_Hits, GameLayers.CutPointMask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < count; i++)
            {
                var point = m_Hits[i].GetComponent<CutPoint>();
                if (point == null || point.IsCut || point.Owner == null || point.Owner.IsOpen) continue;
                float sqr = (point.transform.position - tipPosition).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = point;
                }
            }

            if (best != null)
            {
                IsCutting = true;
                best.Owner.ApplyCut(best, dt, m_CutterTuning.secondsPerCutPoint, player);
                if (sparks != null)
                {
                    sparks.transform.position = best.transform.position;
                    sparks.Emit(2);
                }
                m_SparkSoundTimer -= dt;
                if (m_SparkSoundTimer <= 0f && Sfx.Library != null)
                {
                    m_SparkSoundTimer = 0.18f;
                    Sfx.PlayAt(Sfx.Library.cutterSparks, tipPosition, 0.6f, Random.Range(0.9f, 1.2f), 14f);
                }
            }

            // Restrained continuous haptics: a light buzz while running, stronger while actually cutting.
            m_HapticTimer -= dt;
            if (m_HapticTimer <= 0f && HoldingHand != null)
            {
                m_HapticTimer = 0.05f;
                HoldingHand.Pulse(IsCutting ? 0.45f : 0.12f, 0.06f);
            }

            // Gameplay noise in pulses, not every frame.
            m_NoiseTimer -= dt;
            var director = MissionDirector.Active;
            if (m_NoiseTimer <= 0f && director != null && director.noise != null)
            {
                var tuning = director.Config.noise;
                m_NoiseTimer = tuning.cuttingNoiseInterval;
                director.noise.Emit(new NoiseEvent(tipPosition,
                    IsCutting ? tuning.cuttingLoudness : tuning.cutterIdleLoudness,
                    NoiseKind.Cutting, player != null ? player.Id : director.LocalPlayerId));
            }
        }

        void SetFlame(bool on)
        {
            if (flame != null) flame.SetActive(on);
            if (flameLight != null) flameLight.enabled = on;
            if (loopSource != null && Application.isPlaying)
            {
                if (on && !loopSource.isPlaying) loopSource.Play();
                else if (!on && loopSource.isPlaying) loopSource.Stop();
            }
            if (!on) m_NoiseTimer = 0f;
        }

        void RefreshReadout()
        {
            float heat = m_Heat.Heat;
            if (heatBar != null) heatBar.localScale = new Vector3(Mathf.Max(0.001f, heat), 1f, 1f);
            if (heatBarRenderer != null)
            {
                if (m_Block == null) m_Block = new MaterialPropertyBlock();
                heatBarRenderer.GetPropertyBlock(m_Block);
                m_Block.SetColor(k_BaseColor, Color.Lerp(new Color(0.2f, 0.8f, 1f), new Color(1f, 0.2f, 0.05f), heat));
                heatBarRenderer.SetPropertyBlock(m_Block);
            }
            if (readout == null) return;

            string text;
            int percent = Mathf.RoundToInt(heat * 100f);
            if (m_Heat.Overheated)
            {
                // Blink the warning so it is noticeable without relying on colour.
                bool blink = Mathf.Sin(Time.time * 10f) > 0f;
                text = (blink ? "!! OVERHEAT !!" : "   OVERHEAT   ") + "\nCOOLING " + percent + "%";
            }
            else if (heat > 0.75f) text = "HEAT " + percent + "%\nHOT - EASE OFF";
            else text = "HEAT " + percent + "%\n" + (IsCutting ? "CUTTING" : IsFiring ? "FLAME ON" : "READY");
            if (readout.text != text) readout.text = text;
        }
    }
}

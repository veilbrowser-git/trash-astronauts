using TMPro;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Tools
{
    /// <summary>
    /// Plasma cutter. Hold the trigger for a flame and draw it along a panel's seam, close to the metal, to cut it
    /// open (see <see cref="SeamCut"/>): a steady pass at the right pace cuts clean, too fast only scores the metal,
    /// holding still just burns heat. It heats up while firing (more slowly while it bites cleanly into fresh metal)
    /// and locks out when overheated until it cools, so long cuts need pacing. The hand feels and hears how well it
    /// bites, and the screen shows the seam's progress. It is loud: both running it and cutting attract the drone.
    /// </summary>
    public sealed class PlasmaCutterTool : SuitTool
    {
        /// <summary>How the flame is doing this frame (haptics, sound and the screen follow it).</summary>
        public enum CutQuality { Off, Air, Far, Dwelling, TooFast, Clean }

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
        readonly Collider[] m_Hits = new Collider[16];
        readonly CutPoint[] m_Stretches = new CutPoint[16];
        readonly SeamCut.Contact[] m_Contacts = new SeamCut.Contact[16];
        MaterialPropertyBlock m_Block;
        CutterHeatModel m_Heat;
        CutterTuning m_CutterTuning;
        float m_NoiseTimer;
        float m_HapticTimer;
        float m_SparkSoundTimer;
        bool m_WasFiring;
        bool m_HadContact;
        Vector3 m_LastContact;
        float m_Speed;
        float m_Bite;
        float m_HapticPhase;
        float m_LightBaseIntensity = -1f;
        Color m_LightBaseColor;
        float m_SparkDebt;
        float m_DropDebt;
        Cuttable m_SeamPanel;
        float m_SeamShownUntil;
        CutQuality m_ShownQuality;
        float m_QualityHold;

        public CutterHeatModel Heat => m_Heat;
        public bool IsFiring => m_Heat != null && m_Heat.Firing;
        public bool IsCutting { get; private set; }
        /// <summary>The weld the flame is on this frame (null when not cutting). Co-op streams its progress.</summary>
        public CutPoint CurrentPoint { get; private set; }
        /// <summary>How the flame is doing this frame.</summary>
        public CutQuality Quality { get; private set; }
        /// <summary>Speed of the flame's spot over the metal, m/s (smoothed).</summary>
        public float SpotSpeed => m_Speed;
        /// <summary>Share of the flame going into fresh metal (smoothed): 1 a clean cut, 0 dwelling or in the air.</summary>
        public float Bite => m_Bite;
        /// <summary>Where the flame meets the metal this frame (valid while <see cref="OnSeam"/>).</summary>
        public Vector3 ContactPoint => m_LastContact;
        /// <summary>True while the flame reaches a seam's metal (cut or not).</summary>
        public bool OnSeam { get; private set; }
        /// <summary>The panel whose seam the screen is showing (null when none recently).</summary>
        public Cuttable SeamPanel => Time.time <= m_SeamShownUntil ? m_SeamPanel : null;
        /// <summary>Strength of the last haptic pulse asked for (sent to the holding hand, if any).</summary>
        public float LastHapticAmplitude { get; private set; }

        /// <summary>
        /// Tests and previews: while set, the cutter does nothing on its own and only steps through <see cref="Step"/>,
        /// wherever its transform has been put, as if held with the trigger as given.
        /// </summary>
        public bool ManualDrive { get; set; }
        bool m_ManualStep;
        bool m_ManualTrigger;

        /// <summary>One step of <paramref name="dt"/> seconds with the trigger held or not (see <see cref="ManualDrive"/>).</summary>
        public void Step(float dt, bool trigger)
        {
            m_ManualStep = true;
            m_ManualTrigger = trigger;
            try { Tick(dt); }
            finally { m_ManualStep = false; }
        }

        protected override void Awake()
        {
            base.Awake();
            EnsureModel();
            if (flameLight != null)
            {
                m_LightBaseIntensity = flameLight.intensity;
                m_LightBaseColor = flameLight.color;
            }
            if (sparks != null)
            {
                // Room for the spray and the slower molten droplets together.
                var main = sparks.main;
                if (main.maxParticles < 120) main.maxParticles = 120;
            }
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
            if (ManualDrive && !m_ManualStep) return;
            EnsureModel();
            var shopDirector = MissionDirector.Active;
            float upgrade = shopDirector != null && shopDirector.Profile != null && shopDirector.Profile.Data.cutterUpgrade
                ? shopDirector.Config.cutterUpgradeHeatMultiplier : 1f;
            // A flame biting cleanly into fresh metal heats the cutter more slowly than one in the air or dwelling.
            m_Heat.HeatCapacityMultiplier = Mathf.Max(1f, upgrade) * (1f + (SeamCut.CleanCutHeatCapacity - 1f) * m_Bite);
            bool firing = m_Heat.Tick(dt, ManualDrive ? m_ManualTrigger : IsHeld && TriggerHeld);
            if (firing != m_WasFiring)
            {
                m_WasFiring = firing;
                SetFlame(firing);
            }

            IsCutting = false;
            OnSeam = false;
            CurrentPoint = null;
            Quality = firing ? CutQuality.Air : CutQuality.Off;
            if (firing) FireStep(dt);
            else
            {
                m_HadContact = false;
                m_Bite = 0f;
                m_Speed = 0f;
            }
            UpdateFlameEffects(dt, firing);
            RefreshReadout();
        }

        void FireStep(float dt)
        {
            var player = HoldingHand != null ? HoldingHand.Player : null;
            Vector3 nozzle = tip != null ? tip.position : transform.position;
            Vector3 flameDir = tip != null ? tip.forward : transform.forward;

            // Seams near the flame: the stretches' triggers reach a little out from the metal and past their ends.
            Vector3 probe = nozzle + flameDir * (SeamCut.MaxStandoff * 0.5f);
            float probeRadius = Mathf.Max(tipRadius, SeamCut.MaxStandoff * 0.5f + 0.03f);
            int count = Physics.OverlapSphereNonAlloc(probe, probeRadius, m_Hits, GameLayers.CutPointMask, QueryTriggerInteraction.Collide);
            int found = 0;
            int focus = -1;
            float focusScore = 0f;
            for (int i = 0; i < count; i++)
            {
                var point = m_Hits[i].GetComponent<CutPoint>();
                if (point == null || point.Owner == null || point.Owner.IsOpen) continue;
                if (!SeamCut.Evaluate(point, nozzle, flameDir, out var contact) || contact.strength <= 0f) continue;
                m_Stretches[found] = point;
                m_Contacts[found] = contact;
                // The stretch the flame is on: the strongest contact, uncut first, then the one it is inside.
                float inside = Mathf.Abs(contact.along) <= point.SeamLength * 0.5f ? 1f : 0.5f;
                float score = contact.strength * inside * (point.IsCut ? 0.25f : 1f);
                if (score > focusScore)
                {
                    focusScore = score;
                    focus = found;
                }
                found++;
            }

            float potential = 0f, added = 0f;
            float speedEfficiency = 1f;
            if (focus >= 0)
            {
                OnSeam = true;
                var main = m_Contacts[focus];
                // Speed of the spot over the metal, smoothed over a few frames.
                float raw = m_HadContact && dt > 0f ? Vector3.Distance(main.point, m_LastContact) / dt : 0f;
                m_Speed = m_HadContact ? Mathf.Lerp(m_Speed, raw, 1f - Mathf.Exp(-dt / 0.06f)) : 0f;
                m_LastContact = main.point;
                m_HadContact = true;
                speedEfficiency = SeamCut.SpeedEfficiency(m_Speed);

                for (int i = 0; i < found; i++)
                {
                    var stretch = m_Stretches[i];
                    var contact = m_Contacts[i];
                    float rate = contact.strength / SeamCut.CellBurnSeconds;
                    if (stretch.IsCut)
                    {
                        // Over a finished kerf the flame only reheats the line.
                        stretch.BurnAt(contact.along, rate, dt, 0.3f, out _, out _);
                        stretch.Owner.MarkSeamDirty();
                        continue;
                    }
                    // The molten line is as wide as the pass bites deep: full for a clean pass, thin for a skim.
                    float passBite = SeamCut.PassDepth(m_Speed) * contact.strength;
                    added += stretch.Owner.ApplySweep(stretch, contact.along, rate * speedEfficiency, dt, passBite, player, out float fresh);
                    potential += speedEfficiency > 0f ? fresh / speedEfficiency : fresh;
                }

                var focused = m_Stretches[focus];
                if (!focused.IsCut && main.strength > 0.05f)
                {
                    IsCutting = true;
                    CurrentPoint = focused;
                }
                else if (focused.IsCut)
                {
                    // The stretch the flame is on is done; a neighbour still being cut is what co-op streams.
                    for (int i = 0; i < found && CurrentPoint == null; i++)
                    {
                        if (!m_Stretches[i].IsCut && m_Contacts[i].strength > 0.05f)
                        {
                            IsCutting = true;
                            CurrentPoint = m_Stretches[i];
                        }
                    }
                }
                m_SeamPanel = focused.Owner;
                m_SeamShownUntil = Time.time + 1.5f;
                for (int i = 0; i < found; i++) m_Stretches[i] = null;
            }
            else
            {
                m_HadContact = false;
                m_Speed = 0f;
            }

            // Share of the flame going into fresh metal: 1 cutting cleanly, falling when dwelling, scoring or off the line.
            float biteNow = potential > 0.0001f ? Mathf.Clamp01(added / potential) : 0f;
            if (focus >= 0) biteNow *= m_Contacts[focus].strength;
            m_Bite = Mathf.Lerp(m_Bite, biteNow, 1f - Mathf.Exp(-dt / 0.15f));

            if (!OnSeam) Quality = CutQuality.Air;
            else if (m_Contacts[focus].strength < 0.5f) Quality = CutQuality.Far;
            else if (!IsCutting) Quality = CutQuality.Dwelling;
            else if (speedEfficiency < 0.9f) Quality = CutQuality.TooFast;
            else if (m_Bite < 0.35f) Quality = CutQuality.Dwelling;
            else Quality = CutQuality.Clean;

            if (IsCutting) EmitSparks(dt, m_Contacts[focus].point, CurrentPoint.Normal, speedEfficiency);
            if (IsCutting)
            {
                m_SparkSoundTimer -= dt;
                if (m_SparkSoundTimer <= 0f && Sfx.Library != null)
                {
                    // The crackle is deeper when it bites and thin and high when it only skims.
                    m_SparkSoundTimer = Quality == CutQuality.Clean ? 0.16f : 0.22f;
                    float pitch = Quality == CutQuality.Clean ? Random.Range(0.85f, 1.0f) : Random.Range(1.15f, 1.35f);
                    Sfx.PlayAt(Sfx.Library.cutterSparks, nozzle, Quality == CutQuality.Clean ? 0.7f : 0.5f, pitch, 14f);
                }
            }

            // Haptics follow the quality of the cut: a firm grinding buzz when it bites, a thin chatter when it
            // skims too fast, a dull hum when dwelling, a light tremor in the air.
            m_HapticTimer -= dt;
            if (m_HapticTimer <= 0f)
            {
                m_HapticTimer = 0.05f;
                m_HapticPhase += 1f;
                float amplitude;
                switch (Quality)
                {
                    case CutQuality.Clean: amplitude = (m_HapticPhase % 2f) < 1f ? 0.62f : 0.48f; break;
                    case CutQuality.TooFast: amplitude = Random.Range(0.12f, 0.34f); break;
                    case CutQuality.Dwelling: amplitude = 0.3f; break;
                    case CutQuality.Far: amplitude = 0.2f; break;
                    default: amplitude = 0.12f; break;
                }
                LastHapticAmplitude = amplitude;
                if (HoldingHand != null) HoldingHand.Pulse(amplitude, 0.06f);
            }

            // Gameplay noise in pulses, not every frame. A clean steady cut is a little quieter than hacking at it.
            m_NoiseTimer -= dt;
            var director = MissionDirector.Active;
            if (m_NoiseTimer <= 0f && director != null && director.noise != null)
            {
                var tuning = director.Config.noise;
                m_NoiseTimer = tuning.cuttingNoiseInterval;
                float loudness = !OnSeam ? tuning.cutterIdleLoudness
                    : Quality == CutQuality.Clean ? tuning.cuttingLoudness * 0.9f : tuning.cuttingLoudness;
                director.noise.Emit(new NoiseEvent(nozzle, loudness, NoiseKind.Cutting, player != null ? player.Id : director.LocalPlayerId));
            }
        }

        /// <summary>
        /// Sparks spraying off the cut (fast, short, yellow) and molten droplets (slower, bigger, orange, longer lived),
        /// thrown out of the face and back along the way the flame came. Fewer and thinner when it only skims.
        /// </summary>
        void EmitSparks(float dt, Vector3 at, Vector3 normal, float speedEfficiency)
        {
            if (sparks == null) return;
            Vector3 travel = m_Speed > 0.02f && tip != null ? Vector3.ProjectOnPlane(tip.forward, normal) : Vector3.zero;
            float strength = Mathf.Clamp01(0.35f + m_Bite);
            m_SparkDebt += dt * 90f * strength;
            m_DropDebt += dt * 14f * strength * speedEfficiency;
            var p = new ParticleSystem.EmitParams { applyShapeToPosition = false };
            while (m_SparkDebt >= 1f)
            {
                m_SparkDebt -= 1f;
                Vector3 dir = (normal * 0.9f + Random.insideUnitSphere * 0.75f - travel * 0.3f).normalized;
                p.position = at + normal * 0.004f;
                p.velocity = dir * Random.Range(0.8f, 2.6f) * (0.6f + 0.4f * speedEfficiency);
                p.startLifetime = Random.Range(0.18f, 0.42f);
                p.startSize = Random.Range(0.006f, 0.014f);
                p.startColor = Color.Lerp(new Color(1f, 0.95f, 0.7f), new Color(1f, 0.65f, 0.2f), Random.value);
                sparks.Emit(p, 1);
            }
            while (m_DropDebt >= 1f)
            {
                m_DropDebt -= 1f;
                Vector3 dir = (normal * 0.6f + Random.insideUnitSphere * 0.5f).normalized;
                p.position = at + normal * 0.003f;
                p.velocity = dir * Random.Range(0.15f, 0.55f);
                p.startLifetime = Random.Range(0.6f, 1.1f);
                p.startSize = Random.Range(0.012f, 0.022f);
                p.startColor = new Color(1f, Random.Range(0.35f, 0.55f), 0.08f);
                sparks.Emit(p, 1);
            }
        }

        /// <summary>The flame's light flickers warm off the cut and the loop's pitch drops when it bites.</summary>
        void UpdateFlameEffects(float dt, bool firing)
        {
            if (flameLight != null && m_LightBaseIntensity >= 0f)
            {
                if (firing && IsCutting)
                {
                    float flicker = 0.85f + 0.3f * Mathf.PerlinNoise(Time.time * 18f, 0.37f);
                    flameLight.intensity = m_LightBaseIntensity * (0.95f + 0.35f * m_Bite) * flicker;
                    flameLight.color = Color.Lerp(m_LightBaseColor, new Color(1f, 0.62f, 0.3f), 0.35f + 0.3f * m_Bite);
                }
                else
                {
                    flameLight.intensity = m_LightBaseIntensity;
                    flameLight.color = m_LightBaseColor;
                }
            }
            if (loopSource != null)
            {
                float target;
                switch (Quality)
                {
                    case CutQuality.Clean: target = 0.82f; break;
                    case CutQuality.Dwelling: target = 0.9f; break;
                    case CutQuality.TooFast: target = 1.02f; break;
                    case CutQuality.Far: target = 1.08f; break;
                    default: target = 1.12f; break;
                }
                if (!firing) target = 1f;
                loopSource.pitch = Mathf.MoveTowards(loopSource.pitch, target, dt * 1.5f);
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
            if (!on)
            {
                m_NoiseTimer = 0f;
                if (loopSource != null) loopSource.pitch = 1f;
            }
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
            else
            {
                var panel = SeamPanel;
                if (panel != null && !panel.IsOpen)
                {
                    // Hold a hint for a moment so it can be read, not flicker frame to frame.
                    m_QualityHold -= Time.deltaTime;
                    if (Quality != m_ShownQuality && (m_QualityHold <= 0f || Quality == CutQuality.Clean))
                    {
                        m_ShownQuality = Quality;
                        m_QualityHold = 0.4f;
                    }
                    string hint;
                    switch (m_ShownQuality)
                    {
                        case CutQuality.Clean: hint = " GOOD"; break;
                        case CutQuality.TooFast: hint = " SLOW"; break;
                        case CutQuality.Dwelling: hint = " MOVE"; break;
                        case CutQuality.Far: hint = " CLOSER"; break;
                        default: hint = ""; break;
                    }
                    text = "HEAT " + percent + "%" + (heat > 0.75f ? " HOT" : "") + "\nSEAM " + panel.CutCount + "/" + panel.TotalCuts + hint;
                }
                else if (heat > 0.75f) text = "HEAT " + percent + "%\nHOT - EASE OFF";
                else text = "HEAT " + percent + "%\n" + (IsCutting ? "CUTTING" : IsFiring ? "FLAME ON" : "READY");
            }
            if (readout.text != text) readout.text = text;
        }
    }
}

using TrashAstronauts.Audio;
using UnityEngine;

namespace TrashAstronauts.Tools
{
    /// <summary>
    /// Flashlight. Trigger toggles it. It keeps shining from its chest holster, so it doubles as a suit
    /// lamp when both hands are busy hauling salvage.
    /// </summary>
    public sealed class FlashlightTool : SuitTool
    {
        public Light beam;
        public Renderer lens;
        public bool startsOn = true;

        static readonly int k_Emission = Shader.PropertyToID("_EmissionColor");
        MaterialPropertyBlock m_Block;

        public bool IsOn { get; private set; }

        [Header("Battery")]
        [Tooltip("Seconds of light on a full charge.")]
        public float batterySeconds = 300f;
        [Tooltip("Charges this many times faster than it drains, while switched off.")]
        public float rechargeRate = 1.5f;
        /// <summary>0 = flat, 1 = full.</summary>
        public float Charge { get; private set; } = 1f;
        /// <summary>Too low to light: it must recover a little first.</summary>
        public bool IsFlat => Charge < 0.05f;
        float m_BaseIntensity = -1f;

        /// <summary>Test seam.</summary>
        public void SetCharge(float charge) => Charge = Mathf.Clamp01(charge);

        protected override void Start()
        {
            base.Start();
            SetOn(startsOn, false);
        }

        protected override void Update()
        {
            base.Update();
            if (beam != null && m_BaseIntensity < 0f) m_BaseIntensity = beam.intensity;
            var director = Mission.MissionDirector.Active;
            float dt = Time.deltaTime;
            bool aboard = director != null && director.Phase == Core.MissionPhase.Active;
            bool inTug = !aboard;
            if (aboard && director.Players.Count > 0 && director.Players[0] != null) inTug = director.Players[0].InSafeZone;

            if (inTug) Charge = Mathf.MoveTowards(Charge, 1f, dt * 0.5f);                    // the tug tops it up in a couple of seconds
            else if (IsOn) Charge = Mathf.Max(0f, Charge - dt / Mathf.Max(10f, batterySeconds));
            else Charge = Mathf.Min(1f, Charge + dt * rechargeRate / Mathf.Max(10f, batterySeconds));

            if (IsOn && Charge <= 0f) SetOn(false, true);
            // Low on charge the beam dims and stutters: you get warning before it goes.
            if (beam != null && IsOn && m_BaseIntensity > 0f)
            {
                float low = Mathf.InverseLerp(0f, 0.2f, Charge);
                float stutter = low < 1f && Mathf.PerlinNoise(Time.time * 9f, 0.3f) > 0.35f + low * 0.6f ? 0.25f : 1f;
                beam.intensity = m_BaseIntensity * Mathf.Lerp(0.45f, 1f, low) * stutter;
            }
        }

        protected override void OnTriggerDown() => SetOn(!IsOn, true);

        public void SetOn(bool on, bool feedback)
        {
            // A flat battery will not light until it has recovered a little.
            if (on && Charge < 0.05f)
            {
                if (feedback && Sfx.Library != null) Sfx.PlayAt(Sfx.Library.flashlightClick, transform.position, 0.6f, 0.6f, 5f);
                return;
            }
            IsOn = on;
            if (beam != null) beam.enabled = on;
            if (lens != null)
            {
                if (m_Block == null) m_Block = new MaterialPropertyBlock();
                lens.GetPropertyBlock(m_Block);
                m_Block.SetColor(k_Emission, on ? new Color(3f, 2.8f, 2.2f) : Color.black);
                lens.SetPropertyBlock(m_Block);
            }
            if (!feedback) return;
            if (HoldingHand != null) HoldingHand.Pulse(0.3f, 0.03f);
            if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.flashlightClick, transform.position, 0.6f, on ? 1.1f : 0.9f, 5f);
        }
    }
}

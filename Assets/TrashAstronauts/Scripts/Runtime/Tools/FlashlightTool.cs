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

        protected override void Start()
        {
            base.Start();
            SetOn(startsOn, false);
        }

        protected override void OnTriggerDown() => SetOn(!IsOn, true);

        public void SetOn(bool on, bool feedback)
        {
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

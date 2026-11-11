using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace TrashAstronauts.World
{
    /// <summary>
    /// Clear "you can interact with this" feedback: the object brightens (an emission boost, so it reads
    /// as a change in brightness rather than hue) while a hand hovers it. Also used by the scanner to
    /// pulse loot. Uses a MaterialPropertyBlock so no materials are duplicated at runtime.
    /// </summary>
    public sealed class HoverHighlight : MonoBehaviour
    {
        public Renderer[] renderers;
        [ColorUsage(false, true)] public Color hoverEmission = new Color(0.35f, 0.35f, 0.3f);
        [ColorUsage(false, true)] public Color pulseEmission = new Color(0.1f, 0.9f, 0.6f);
        public float pulseHz = 2.5f;

        static readonly int k_Emission = Shader.PropertyToID("_EmissionColor");
        static MaterialPropertyBlock s_Block;

        XRBaseInteractable m_Interactable;
        int m_HoverCount;
        bool m_ManualHover;
        float m_PulseUntil;
        bool m_Dirty = true;

        void Awake()
        {
            m_Interactable = GetComponent<XRBaseInteractable>();
            if (renderers == null || renderers.Length == 0) renderers = CollectRenderers();
        }

        Renderer[] CollectRenderers()
        {
            var list = new List<Renderer>();
            foreach (var r in GetComponentsInChildren<MeshRenderer>(true)) list.Add(r);
            return list.ToArray();
        }

        void OnEnable()
        {
            if (m_Interactable != null)
            {
                m_Interactable.hoverEntered.AddListener(OnHoverEntered);
                m_Interactable.hoverExited.AddListener(OnHoverExited);
            }
            m_Dirty = true;
        }

        void OnDisable()
        {
            if (m_Interactable != null)
            {
                m_Interactable.hoverEntered.RemoveListener(OnHoverEntered);
                m_Interactable.hoverExited.RemoveListener(OnHoverExited);
            }
            m_HoverCount = 0;
            m_ManualHover = false;
            Apply(Color.black);
        }

        void OnHoverEntered(HoverEnterEventArgs args) { m_HoverCount++; m_Dirty = true; }
        void OnHoverExited(HoverExitEventArgs args) { m_HoverCount = Mathf.Max(0, m_HoverCount - 1); m_Dirty = true; }

        /// <summary>Hover driven by something other than XRI (the button pointer).</summary>
        public void SetManualHover(bool hovered)
        {
            if (m_ManualHover == hovered) return;
            m_ManualHover = hovered;
            m_Dirty = true;
        }

        /// <summary>Pulses the object for a while (scanner hit).</summary>
        public void Pulse(float seconds)
        {
            m_PulseUntil = Mathf.Max(m_PulseUntil, Time.time + seconds);
            m_Dirty = true;
        }

        public void StopPulse()
        {
            m_PulseUntil = 0f;
            m_Dirty = true;
        }

        void Update()
        {
            bool pulsing = Time.time < m_PulseUntil;
            if (!pulsing && !m_Dirty) return;

            Color color = Color.black;
            if (pulsing)
            {
                float wave = 0.5f + 0.5f * Mathf.Sin(Time.time * pulseHz * Mathf.PI * 2f);
                color += pulseEmission * (0.35f + 0.65f * wave);
            }
            if (m_HoverCount > 0 || m_ManualHover) color += hoverEmission;

            Apply(color);
            m_Dirty = pulsing;
        }

        void Apply(Color emission)
        {
            if (renderers == null) return;
            if (s_Block == null) s_Block = new MaterialPropertyBlock();
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                if (emission.maxColorComponent <= 0.0001f)
                {
                    r.SetPropertyBlock(null);
                    continue;
                }
                r.GetPropertyBlock(s_Block);
                s_Block.SetColor(k_Emission, emission);
                r.SetPropertyBlock(s_Block);
            }
        }
    }
}

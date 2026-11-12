using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>
    /// The glowing lens of a ceiling fixture follows its lamp: dark when the lamp is dead or blacked out,
    /// flickering with it when it is failing, dimmed with it when it dims. Only watches the Light; never
    /// changes it. Writes a MaterialPropertyBlock only when the brightness changes, and allocates nothing per frame.
    /// </summary>
    public sealed class LampLens : MonoBehaviour
    {
        public Light lamp;
        public Renderer lens;
        [Tooltip("The lamp's intensity when it is fully on (as built).")]
        public float fullIntensity = 1f;
        public Color litEmission = Color.white;
        public Color litBase = Color.white;
        public Color darkBase = new Color(0.08f, 0.08f, 0.075f);

        static readonly int s_Emission = Shader.PropertyToID("_EmissionColor");
        static readonly int s_BaseColor = Shader.PropertyToID("_BaseColor");
        MaterialPropertyBlock m_Block;
        float m_Shown = -1f;

        /// <summary>0 = dark, 1 = fully lit, as last shown.</summary>
        public float Brightness => Mathf.Max(0f, m_Shown);

        void OnEnable() => m_Shown = -1f;

        void LateUpdate() => Refresh();

        public void Refresh()
        {
            float k = 0f;
            if (lamp != null && lamp.isActiveAndEnabled)
                k = fullIntensity > 0.0001f ? Mathf.Clamp01(lamp.intensity / fullIntensity) : 1f;
            if (m_Shown >= 0f && Mathf.Abs(k - m_Shown) < 0.02f) return;
            m_Shown = k;
            if (lens == null) return;
            if (m_Block == null) m_Block = new MaterialPropertyBlock();
            lens.GetPropertyBlock(m_Block);
            m_Block.SetColor(s_Emission, litEmission * k);
            m_Block.SetColor(s_BaseColor, Color.Lerp(darkBase, litBase, k));
            lens.SetPropertyBlock(m_Block);
        }
    }
}

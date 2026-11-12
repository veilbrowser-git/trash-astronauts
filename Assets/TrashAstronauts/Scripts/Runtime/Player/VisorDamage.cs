using UnityEngine;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// The helmet visor. When the spider bites, cracks spread across it in stages and the view flashes
    /// red. It is all in front of the camera, so nothing moves the player's view. Cleared between runs.
    /// </summary>
    public sealed class VisorDamage : MonoBehaviour
    {
        [Tooltip("Crack overlays, switched on one at a time.")]
        public Renderer[] cracks;
        [Tooltip("A red wash over the whole view, pulsed on each bite.")]
        public Renderer flash;
        public float flashFade = 4f;

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        MaterialPropertyBlock m_Block;
        float m_Flash;

        public int Stage { get; private set; }
        public float FlashLevel => m_Flash;

        void Awake() => Clear();

        /// <summary>Adds the next stage of cracks (up to however many there are) and flashes red.</summary>
        public void Crack(float flashStrength = 0.55f)
        {
            if (cracks != null && Stage < cracks.Length)
            {
                if (cracks[Stage] != null) cracks[Stage].enabled = true;
                Stage++;
            }
            m_Flash = Mathf.Max(m_Flash, flashStrength);
            ApplyFlash();
        }

        public void Clear()
        {
            Stage = 0;
            if (cracks != null) foreach (var c in cracks) if (c != null) c.enabled = false;
            m_Flash = 0f;
            ApplyFlash();
        }

        void Update()
        {
            if (m_Flash <= 0f) return;
            m_Flash = Mathf.Max(0f, m_Flash - Time.unscaledDeltaTime * flashFade * Mathf.Max(0.15f, m_Flash));
            ApplyFlash();
        }

        void ApplyFlash()
        {
            if (flash == null) return;
            flash.enabled = m_Flash > 0.004f;
            if (!flash.enabled) return;
            if (m_Block == null) m_Block = new MaterialPropertyBlock();
            flash.GetPropertyBlock(m_Block);
            m_Block.SetColor(k_BaseColor, new Color(0.85f, 0.02f, 0.02f, m_Flash));
            flash.SetPropertyBlock(m_Block);
        }
    }
}

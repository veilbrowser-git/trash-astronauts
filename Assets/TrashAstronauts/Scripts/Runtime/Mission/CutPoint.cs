using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// One weld on a cuttable seam. The plasma cutter's flame has to dwell on it to burn through.
    /// Burnt points change shape (they shrink flat) as well as colour.
    /// </summary>
    public sealed class CutPoint : MonoBehaviour
    {
        public Renderer marker;
        public Transform markerTransform;

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int k_Emission = Shader.PropertyToID("_EmissionColor");
        static readonly Color k_Fresh = new Color(1f, 0.55f, 0.05f);
        static readonly Color k_Hot = new Color(1f, 0.95f, 0.7f);
        static readonly Color k_Burnt = new Color(0.05f, 0.05f, 0.06f);

        MaterialPropertyBlock m_Block;
        Vector3 m_MarkerScale;
        bool m_ScaleCached;

        public Cuttable Owner { get; internal set; }
        public float Progress { get; private set; }
        public bool IsCut => Progress >= 1f;

        /// <summary>Applies cutting time. Returns true on the call that finishes this point.</summary>
        public bool Burn(float seconds, float secondsRequired)
        {
            if (IsCut) return false;
            Progress = Mathf.Min(1f, Progress + seconds / Mathf.Max(0.05f, secondsRequired));
            Refresh();
            return IsCut;
        }

        public void ResetPoint()
        {
            Progress = 0f;
            Refresh();
        }

        public void ForceCut()
        {
            Progress = 1f;
            Refresh();
        }

        void Refresh()
        {
            if (marker == null) return;
            if (m_Block == null) m_Block = new MaterialPropertyBlock();
            if (!m_ScaleCached && markerTransform != null)
            {
                m_MarkerScale = markerTransform.localScale;
                m_ScaleCached = true;
            }

            Color color = IsCut ? k_Burnt : Color.Lerp(k_Fresh, k_Hot, Progress);
            Color emission = IsCut ? Color.black : Color.Lerp(k_Fresh * 0.6f, k_Hot * 2.5f, Progress);
            marker.GetPropertyBlock(m_Block);
            m_Block.SetColor(k_BaseColor, color);
            m_Block.SetColor(k_Emission, emission);
            marker.SetPropertyBlock(m_Block);

            if (markerTransform != null)
            {
                float s = IsCut ? 0.45f : 1f + Progress * 0.35f;
                markerTransform.localScale = m_MarkerScale * s;
            }
        }
    }
}

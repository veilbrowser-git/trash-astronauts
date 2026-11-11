using System.Collections;
using UnityEngine;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// Fades the view to black with a quad just in front of the eyes. Used for the caught / out-of-oxygen
    /// transition so the player is never moved while they can see (no camera shake, no forced rotation).
    /// </summary>
    public sealed class ScreenFader : MonoBehaviour
    {
        public Renderer quad;
        public float defaultDuration = 0.35f;

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        MaterialPropertyBlock m_Block;
        Coroutine m_Routine;

        public float Alpha { get; private set; }

        void Awake()
        {
            m_Block = new MaterialPropertyBlock();
            Apply(0f);
        }

        public Coroutine FadeOut(float duration = -1f) => FadeTo(1f, duration);
        public Coroutine FadeIn(float duration = -1f) => FadeTo(0f, duration);

        public Coroutine FadeTo(float alpha, float duration = -1f)
        {
            if (duration < 0f) duration = defaultDuration;
            if (m_Routine != null) StopCoroutine(m_Routine);
            if (!isActiveAndEnabled)
            {
                Apply(alpha);
                return null;
            }
            m_Routine = StartCoroutine(FadeRoutine(alpha, duration));
            return m_Routine;
        }

        IEnumerator FadeRoutine(float target, float duration)
        {
            float start = Alpha;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                Apply(Mathf.Lerp(start, target, duration <= 0f ? 1f : t / duration));
                yield return null;
            }
            Apply(target);
            m_Routine = null;
        }

        void Apply(float alpha)
        {
            Alpha = Mathf.Clamp01(alpha);
            if (quad == null) return;
            if (m_Block == null) m_Block = new MaterialPropertyBlock();
            quad.enabled = Alpha > 0.001f;
            m_Block.SetColor(k_BaseColor, new Color(0f, 0f, 0f, Alpha));
            quad.SetPropertyBlock(m_Block);
        }
    }
}

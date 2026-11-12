using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using UnityEngine;

namespace TrashAstronauts.UI
{
    /// <summary>
    /// Packs the company store away for the trip. When a contract starts the awning drops, the shutters
    /// close over the counter, the goods sink out of sight and the whole stall shrinks into a small crate
    /// on the floor. Back in the hub it unpacks itself again.
    /// </summary>
    public sealed class ShopFolder : MonoBehaviour
    {
        public MissionDirector director;
        [Tooltip("The stall. Scaled down in the second half of the fold.")]
        public Transform stall;
        [Tooltip("Hinged parts, with the angles they turn to when folded (parallel arrays).")]
        public Transform[] hinges;
        public Vector3[] foldedAngles;
        [Tooltip("The goods on the counter: they sink into it as the shutters close.")]
        public Transform goods;
        public float goodsDrop = 0.55f;
        public float foldedScale = 0.24f;
        public float seconds = 1.6f;

        Quaternion[] m_Open;
        Vector3 m_GoodsOpen;
        float m_Fold;

        /// <summary>0 = open for business, 1 = packed into a crate.</summary>
        public float Fold => m_Fold;
        public bool IsFolded => m_Fold >= 0.999f;
        public bool IsOpen => m_Fold <= 0.001f;

        void Awake()
        {
            m_Open = new Quaternion[hinges != null ? hinges.Length : 0];
            for (int i = 0; i < m_Open.Length; i++) m_Open[i] = hinges[i] != null ? hinges[i].localRotation : Quaternion.identity;
            if (goods != null) m_GoodsOpen = goods.localPosition;
        }

        void Update()
        {
            if (director == null) director = MissionDirector.Active;
            if (director == null) return;
            float target = director.Phase == MissionPhase.Active ? 1f : 0f;
            if (Mathf.Approximately(m_Fold, target)) return;
            m_Fold = Mathf.MoveTowards(m_Fold, target, Time.deltaTime / Mathf.Max(0.1f, seconds));
            Apply(m_Fold);
        }

        /// <summary>Jumps straight to a pose (tests, previews).</summary>
        public void Snap(float fold)
        {
            if (m_Open == null) Awake();
            m_Fold = Mathf.Clamp01(fold);
            Apply(m_Fold);
        }

        void Apply(float fold)
        {
            // First half: everything closes. Second half: the closed stall shrinks into a crate.
            float close = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(fold / 0.55f));
            float shrink = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((fold - 0.5f) / 0.5f));
            for (int i = 0; i < m_Open.Length; i++)
            {
                if (hinges[i] == null) continue;
                Vector3 angles = foldedAngles != null && i < foldedAngles.Length ? foldedAngles[i] : Vector3.zero;
                hinges[i].localRotation = m_Open[i] * Quaternion.Euler(angles * close);
            }
            if (goods != null) goods.localPosition = m_GoodsOpen + Vector3.down * (goodsDrop * close);
            if (stall != null) stall.localScale = Vector3.one * Mathf.Lerp(1f, foldedScale, shrink);
        }
    }
}

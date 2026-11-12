using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// One stretch of a cuttable seam (the network still calls it a weld or cut point: one index in
    /// <see cref="Cuttable.cutPoints"/>). The stretch runs along this transform's local X for <see cref="length"/>
    /// metres, centred on it, on a metal face whose outward normal is local Z. It is divided into
    /// <see cref="Cells"/> small cells; the plasma cutter's flame deepens the cells it passes over
    /// (<see cref="BurnAt"/>), and the stretch is through once the metal along it is cut (<see cref="Progress"/> is
    /// the mean depth). Each cell also keeps a temperature for the molten line that follows the flame and cools
    /// behind it. A stretch of length 0 is a single weld spot, as the old seams were.
    /// </summary>
    public sealed class CutPoint : MonoBehaviour
    {
        [Tooltip("Optional legacy marker (a weld spot). Seams are drawn by the panel's seam line instead.")]
        public Renderer marker;
        public Transform markerTransform;
        [Tooltip("Length of this stretch of seam along local X, metres. 0 = a single weld spot.")]
        public float length;

        /// <summary>Cells along one stretch (the resolution of the cut and of its glow).</summary>
        public const int Cells = 16;
        /// <summary>Length used for a weld spot (length 0).</summary>
        public const float SpotLength = 0.06f;
        /// <summary>Seconds a cell takes to cool from white hot to dark.</summary>
        public const float GlowSeconds = 3.5f;
        /// <summary>The metal tears the rest of the way once the mean depth and the shallowest cell reach these.</summary>
        public const float FinishMeanDepth = 0.92f, FinishMinDepth = 0.75f;
        /// <summary>Highest burn shown for another player's progress: a weld only looks finished once it is.</summary>
        public const float MaxRemoteProgress = 0.98f;

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int k_Emission = Shader.PropertyToID("_EmissionColor");
        static readonly Color k_Fresh = new Color(1f, 0.55f, 0.05f);
        static readonly Color k_Hot = new Color(1f, 0.95f, 0.7f);
        static readonly Color k_Burnt = new Color(0.05f, 0.05f, 0.06f);

        readonly float[] m_Depth = new float[Cells];
        readonly float[] m_Glow = new float[Cells];
        readonly float[] m_Bite = new float[Cells];
        MaterialPropertyBlock m_Block;
        Vector3 m_MarkerScale;
        bool m_ScaleCached;
        float m_RemoteShown;
        float m_RemoteRate;
        bool m_RemoteFromEnd;
        bool m_GlowActive;

        public Cuttable Owner { get; internal set; }
        /// <summary>How far this machine has cut this stretch, 0..1 (the mean depth of its cells).</summary>
        public float Progress { get; private set; }
        public bool IsCut => Progress >= 1f;
        /// <summary>
        /// How far another crew member has burnt this weld on their machine (co-op display only, never completes it:
        /// a finished weld arrives through the reliable PointCut path). The seam shows the larger of the two.
        /// </summary>
        public float RemoteProgress { get; private set; }
        /// <summary>What the seam shows: this machine's progress or a crew member's, whichever is further.</summary>
        public float DisplayProgress => IsCut ? 1f : Mathf.Max(Progress, RemoteProgress);
        /// <summary>Length of the stretch in metres (a weld spot counts as <see cref="SpotLength"/>).</summary>
        public float SeamLength => length > 0.001f ? length : SpotLength;
        /// <summary>World direction the seam runs in (local X).</summary>
        public Vector3 Axis => transform.right;
        /// <summary>World outward normal of the metal face (local Z).</summary>
        public Vector3 Normal => transform.forward;
        /// <summary>True while any cell still glows or a crew member's line is still being drawn in.</summary>
        public bool VisualActive => m_GlowActive || m_RemoteShown != RemoteTarget;
        /// <summary>The hottest cell (0 dark .. 1 white hot).</summary>
        public float MaxGlow
        {
            get
            {
                float m = 0f;
                for (int i = 0; i < Cells; i++) m = Mathf.Max(m, m_Glow[i]);
                return m;
            }
        }

        float RemoteTarget => IsCut ? m_RemoteShown : RemoteProgress;

        /// <summary>Distance of cell <paramref name="cell"/>'s centre from the middle of the stretch, along it.</summary>
        public float CellOffset(int cell) => (-0.5f + (cell + 0.5f) / Cells) * SeamLength;
        /// <summary>World position of a cell's centre.</summary>
        public Vector3 CellPosition(int cell) => transform.position + Axis * CellOffset(cell);
        public float DepthAt(int cell) => m_Depth[cell];
        public float GlowAt(int cell) => m_Glow[cell];
        /// <summary>How cleanly the last pass over this cell bit (1 full depth, low = only scored): the glow's width.</summary>
        public float BiteAt(int cell) => m_Bite[cell];

        /// <summary>Depth shown for a cell: this machine's cut or a crew member's line, whichever is further.</summary>
        public float DisplayDepthAt(int cell)
        {
            float d = m_Depth[cell];
            if (m_RemoteShown <= 0f || IsCut) return d;
            int along = m_RemoteFromEnd ? Cells - 1 - cell : cell;
            return Mathf.Max(d, Mathf.Clamp01(m_RemoteShown * Cells - along));
        }

        /// <summary>
        /// Shows another crew member's progress on this weld (0..1, clamped below finished). Only the look changes:
        /// <see cref="Progress"/>, <see cref="IsCut"/> and the panel are untouched. Ignored once the weld is through.
        /// The line is drawn in over the next message interval, glowing as it goes, from the end next to whichever
        /// neighbouring stretch is further cut (where their flame most likely came from).
        /// </summary>
        public void ApplyRemoteProgress(float progress)
        {
            if (IsCut) return;
            float target = Mathf.Clamp(progress, 0f, MaxRemoteProgress);
            if (m_RemoteShown <= 0f && target > 0f) m_RemoteFromEnd = GrowsFromEnd();
            if (target < m_RemoteShown) m_RemoteShown = target;
            RemoteProgress = target;
            m_RemoteRate = Mathf.Max(0.25f, (target - m_RemoteShown) / 0.2f);
            Refresh();
        }

        bool GrowsFromEnd()
        {
            var owner = Owner;
            if (owner == null || owner.cutPoints == null) return false;
            int i = System.Array.IndexOf(owner.cutPoints, this);
            float before = i > 0 && owner.cutPoints[i - 1] != null ? owner.cutPoints[i - 1].DisplayProgress : 0f;
            float after = i >= 0 && i + 1 < owner.cutPoints.Length && owner.cutPoints[i + 1] != null ? owner.cutPoints[i + 1].DisplayProgress : 0f;
            return after > before;
        }

        /// <summary>Applies cutting time evenly along the whole stretch. Returns true on the call that finishes it.</summary>
        public bool Burn(float seconds, float secondsRequired)
        {
            if (IsCut) return false;
            float add = seconds / Mathf.Max(0.05f, secondsRequired);
            for (int i = 0; i < Cells; i++)
            {
                m_Depth[i] = Mathf.Min(1f, m_Depth[i] + add);
                m_Glow[i] = 1f;
                m_Bite[i] = Mathf.Max(m_Bite[i], Mathf.Clamp01(add * 4f));
            }
            m_GlowActive = true;
            return Settle();
        }

        /// <summary>
        /// The flame at <paramref name="along"/> metres from the middle of the stretch (along it) for
        /// <paramref name="dt"/> seconds, deepening the cells under its spot at <paramref name="depthPerSecond"/> (full
        /// depth per second at the centre of the spot). <paramref name="bite"/> (0..1) is how cleanly it bites, which
        /// sets the width of the glowing line. Returns the depth actually added (in whole cells: 1 = one cell cut
        /// through) and, in <paramref name="potential"/>, what the same flame could have added to fresh metal.
        /// <paramref name="finished"/> is true on the call that finishes the stretch.
        /// </summary>
        public float BurnAt(float along, float depthPerSecond, float dt, float bite, out float potential, out bool finished)
        {
            finished = false;
            potential = 0f;
            float added = 0f;
            if (dt <= 0f || depthPerSecond <= 0f) return 0f;
            float half = SeamLength * 0.5f;
            if (along < -half - SeamCut.SpotOuter || along > half + SeamCut.SpotOuter) return 0f;
            bool wasCut = IsCut;
            for (int i = 0; i < Cells; i++)
            {
                float w = SeamCut.SpotWeight(CellOffset(i) - along);
                if (w <= 0f) continue;
                float gain = depthPerSecond * w * dt;
                potential += gain;
                float before = m_Depth[i];
                float after = Mathf.Min(1f, before + gain);
                added += after - before;
                m_Depth[i] = after;
                if (w > 0.25f)
                {
                    // A cell the flame is on glows white whatever it is; how wide depends on how well it bit.
                    if (after > before) m_Bite[i] = m_Glow[i] > 0.9f ? Mathf.Max(m_Bite[i], bite) : bite;
                    else if (m_Glow[i] < 0.9f) m_Bite[i] = Mathf.Max(m_Bite[i], 0.35f);
                    m_Glow[i] = 1f;
                    m_GlowActive = true;
                }
            }
            if (wasCut) return 0f;
            finished = Settle();
            return added;
        }

        /// <summary>Recomputes progress; snaps the stretch through once nearly all of it is. True if that just happened.</summary>
        bool Settle()
        {
            float sum = 0f, min = 1f;
            for (int i = 0; i < Cells; i++)
            {
                sum += m_Depth[i];
                min = Mathf.Min(min, m_Depth[i]);
            }
            float mean = sum / Cells;
            if (mean >= FinishMeanDepth && min >= FinishMinDepth)
            {
                for (int i = 0; i < Cells; i++) m_Depth[i] = 1f;
                Progress = 1f;
                Refresh();
                return true;
            }
            Progress = Mathf.Min(mean, 0.999f);
            Refresh();
            return false;
        }

        /// <summary>Cools the molten line and draws in a crew member's line. True while anything still changes.</summary>
        public bool StepVisual(float dt)
        {
            bool active = false;
            float target = RemoteTarget;
            if (m_RemoteShown != target)
            {
                float before = m_RemoteShown;
                m_RemoteShown = Mathf.MoveTowards(m_RemoteShown, target, m_RemoteRate * dt);
                // The stretch their flame has just crossed glows as it would under ours.
                for (int i = 0; i < Cells; i++)
                {
                    int along = m_RemoteFromEnd ? Cells - 1 - i : i;
                    float at = (along + 0.5f) / Cells;
                    if (at > before && at <= m_RemoteShown + 0.5f / Cells)
                    {
                        m_Glow[i] = 1f;
                        m_Bite[i] = Mathf.Max(m_Bite[i], 0.8f);
                        m_GlowActive = true;
                    }
                }
                active = true;
            }
            if (m_GlowActive)
            {
                bool any = false;
                float fall = dt / GlowSeconds;
                for (int i = 0; i < Cells; i++)
                {
                    if (m_Glow[i] <= 0f) continue;
                    m_Glow[i] = Mathf.Max(0f, m_Glow[i] - fall);
                    if (m_Glow[i] > 0f) any = true;
                }
                m_GlowActive = any;
                active = true;
            }
            return active;
        }

        public void ResetPoint()
        {
            Progress = 0f;
            RemoteProgress = 0f;
            m_RemoteShown = 0f;
            m_GlowActive = false;
            for (int i = 0; i < Cells; i++)
            {
                m_Depth[i] = 0f;
                m_Glow[i] = 0f;
                m_Bite[i] = 0f;
            }
            Refresh();
        }

        /// <summary>
        /// Burns the stretch through with no judging (another machine's word, or the panel opening). Metal that was
        /// not already cut here is shown freshly cut: it glows and cools like the rest of the line.
        /// </summary>
        public void ForceCut()
        {
            for (int i = 0; i < Cells; i++)
            {
                if (m_Depth[i] < 1f)
                {
                    float shown = DisplayDepthAt(i);
                    m_Glow[i] = Mathf.Max(m_Glow[i], shown >= 1f ? 0.55f : 0.75f);
                    m_Bite[i] = Mathf.Max(m_Bite[i], 0.7f);
                    m_GlowActive = true;
                }
                m_Depth[i] = 1f;
            }
            Progress = 1f;
            Refresh();
        }

        /// <summary>Makes every cell glow at least this hot (a plate popping free keeps its edges hot for a moment).</summary>
        public void Heat(float glow)
        {
            for (int i = 0; i < Cells; i++)
            {
                if (m_Glow[i] < glow)
                {
                    m_Glow[i] = glow;
                    m_Bite[i] = Mathf.Max(m_Bite[i], 0.6f);
                }
            }
            m_GlowActive = glow > 0f || m_GlowActive;
        }

        void Refresh()
        {
            if (Owner != null) Owner.MarkSeamDirty();
            if (marker == null) return;
            if (m_Block == null) m_Block = new MaterialPropertyBlock();
            if (!m_ScaleCached && markerTransform != null)
            {
                m_MarkerScale = markerTransform.localScale;
                m_ScaleCached = true;
            }

            float shown = DisplayProgress;
            Color color = IsCut ? k_Burnt : Color.Lerp(k_Fresh, k_Hot, shown);
            Color emission = IsCut ? Color.black : Color.Lerp(k_Fresh * 0.6f, k_Hot * 2.5f, shown);
            marker.GetPropertyBlock(m_Block);
            m_Block.SetColor(k_BaseColor, color);
            m_Block.SetColor(k_Emission, emission);
            marker.SetPropertyBlock(m_Block);

            if (markerTransform != null)
            {
                float s = IsCut ? 0.45f : 1f + shown * 0.35f;
                markerTransform.localScale = m_MarkerScale * s;
            }
        }
    }
}

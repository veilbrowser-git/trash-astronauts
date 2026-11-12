using System;
using TMPro;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// A predefined detachable piece: an access panel or a salvage mounting bracket, held by a seam that is cut
    /// open with the plasma cutter. The seam is divided into stretches (<see cref="cutPoints"/>, one network
    /// "cut point" each); the flame dragged along it cuts each stretch through (<see cref="ApplySweep"/>), the
    /// line it leaves glows and cools (<see cref="CutSeamVisual"/>), and the plate loosens as stretches part. It
    /// opens when every stretch is through. No mesh slicing; the plate is a separate object that is released
    /// and floats away.
    /// </summary>
    public sealed class Cuttable : MonoBehaviour
    {
        [Tooltip("Stable id for the mission ledger (and future network sync).")]
        public string panelId = "panel";
        public string displayName = "ACCESS PANEL";
        public CutPoint[] cutPoints;
        [Tooltip("The part that comes off. Needs a Rigidbody; it is kinematic until cut free.")]
        public Rigidbody plate;
        public TMP_Text progressLabel;
        [Tooltip("Training panels work in the tug with no contract running and are not recorded.")]
        public bool isTraining;
        [Tooltip("Vertex-coloured transparent material for the seam line (bead, kerf and molten glow). None: no line.")]
        public Material seamMaterial;

        /// <summary>How far the cut side of the plate comes away from the face once every stretch but the last is through.</summary>
        public const float MaxLooseGap = 0.012f;
        /// <summary>A client's weld reports from the cutter: at most this many at once, refilled at this rate (under the host's limit).</summary>
        public const float ReportBurst = 4f, ReportsPerSecond = 3f;
        /// <summary>Seconds the edges of a freed plate stay hot (they start orange and cool like the line).</summary>
        public const float FreedEdgeGlow = 0.72f;

        /// <summary>Clock for the client's report pacing (tests); null uses unscaled time.</summary>
        public static Func<double> ReportClock;
        static float s_ReportTokens = ReportBurst;
        static double s_ReportTime = double.NegativeInfinity;
        readonly System.Collections.Generic.List<int> m_PendingReports = new System.Collections.Generic.List<int>();
        CutSeamVisual m_Seam;
        bool m_SeamDirty;
        bool m_VisualActive;
        float m_Loose;
        float m_LooseTarget;
        Vector2 m_LooseToward;
        float m_HalfExtent = 0.3f;

        Vector3 m_PlateHomePosition;
        Quaternion m_PlateHomeRotation;
        Transform m_PlateHomeParent;
        bool m_HomeCached;

        public bool IsOpen { get; private set; }
        public event Action<Cuttable> Opened;
        /// <summary>
        /// One weld of a contract panel finished here (panel, index in <see cref="cutPoints"/>, who), by this
        /// machine's cutter or by <see cref="ApplyRemotePointCut"/>. Not raised for training panels or for the
        /// welds that <see cref="ApplyOpened()"/> burns through on its own. A co-op host tells the crew.
        /// </summary>
        public event Action<Cuttable, int, PlayerId> PointCut;
        /// <summary>The velocity change the plate was given when it last came off (what a co-op host sends).</summary>
        public Vector3 LastPlateImpulse { get; private set; }
        /// <summary>The angular velocity change the plate was given when it last came off.</summary>
        public Vector3 LastPlateTorque { get; private set; }

        public int CutCount
        {
            get
            {
                int n = 0;
                if (cutPoints != null)
                {
                    foreach (var p in cutPoints)
                    {
                        if (p != null && p.IsCut) n++;
                    }
                }
                return n;
            }
        }

        public int TotalCuts => cutPoints != null ? cutPoints.Length : 0;
        public float Progress => TotalCuts == 0 ? 1f : (float)CutCount / TotalCuts;
        /// <summary>The drawn seam line (null without a seam material, or before it is built).</summary>
        public CutSeamVisual Seam => m_Seam;
        /// <summary>How far the plate has worked loose, 0 (welded) .. 1 (only the last stretch holds it).</summary>
        public float Looseness => m_Loose;
        /// <summary>Weld reports cut here that are waiting for the pacing (co-op client).</summary>
        public int PendingReports => m_PendingReports.Count;
        /// <summary>Total length of seam, metres.</summary>
        public float SeamLength
        {
            get
            {
                float sum = 0f;
                if (cutPoints != null) foreach (var p in cutPoints) if (p != null) sum += p.SeamLength;
                return sum;
            }
        }

        void Awake()
        {
            CacheHome();
            if (cutPoints != null)
            {
                foreach (var p in cutPoints)
                {
                    if (p != null) p.Owner = this;
                }
            }
            RefreshLabel();
            if (Application.isPlaying) EnsureSeam();
        }

        /// <summary>Builds the seam line on the plate if it is not there yet (play does this on Awake; previews call it).</summary>
        public CutSeamVisual EnsureSeam()
        {
            if (m_Seam != null) return m_Seam;
            if (cutPoints != null) foreach (var p in cutPoints) if (p != null) p.Owner = this;
            CacheHome();
            if (plate != null && cutPoints != null)
            {
                float extent = 0.05f;
                foreach (var p in cutPoints)
                {
                    if (p == null) continue;
                    Vector3 local = transform.InverseTransformPoint(p.transform.position);
                    extent = Mathf.Max(extent, Mathf.Abs(local.x), Mathf.Abs(local.y));
                }
                m_HalfExtent = extent;
            }
            m_Seam = CutSeamVisual.Build(this, plate != null ? plate.transform : transform, seamMaterial);
            m_SeamDirty = true;
            m_VisualActive = true;
            return m_Seam;
        }

        internal void MarkSeamDirty()
        {
            m_SeamDirty = true;
            m_VisualActive = true;
        }

        void CacheHome()
        {
            if (m_HomeCached || plate == null) return;
            m_PlateHomeParent = plate.transform.parent;
            m_PlateHomePosition = plate.transform.localPosition;
            m_PlateHomeRotation = plate.transform.localRotation;
            m_HomeCached = true;
        }

        /// <summary>
        /// Burns a whole stretch evenly for <paramref name="seconds"/> (it is through after
        /// <paramref name="secondsRequired"/>): a weld finished outright, for tools and tests. The cutter's flame
        /// uses <see cref="ApplySweep"/>.
        /// </summary>
        public void ApplyCut(CutPoint point, float seconds, float secondsRequired, PlayerContext by)
        {
            if (IsOpen || point == null || point.Owner != this) return;
            if (!point.Burn(seconds, secondsRequired)) return;
            CompletePoint(point, by, false);
        }

        /// <summary>
        /// The cutter's flame on one stretch for <paramref name="dt"/> seconds at <paramref name="along"/> metres from its
        /// middle, cutting at <paramref name="depthPerSecond"/> (see <see cref="CutPoint.BurnAt"/>). Returns the depth
        /// added; <paramref name="potential"/> is what fresh metal would have taken. Finishing the stretch books it
        /// as <see cref="ApplyCut"/> does, except that a co-op client's report is paced under the host's limit.
        /// </summary>
        public float ApplySweep(CutPoint point, float along, float depthPerSecond, float dt, float bite, PlayerContext by, out float potential)
        {
            potential = 0f;
            if (IsOpen || point == null || point.Owner != this) return 0f;
            float added = point.BurnAt(along, depthPerSecond, dt, bite, out potential, out bool finished);
            if (finished) CompletePoint(point, by, true);
            return added;
        }

        void CompletePoint(CutPoint point, PlayerContext by, bool paced)
        {
            if (by != null) by.DominantHand?.Pulse(0.7f, 0.08f);
            RefreshLabel();
            UpdateLooseTarget();

            var director = MissionDirector.Active;
            PlayerId playerId = by != null ? by.Id : director != null ? director.LocalPlayerId : PlayerId.Local;
            if (!isTraining)
            {
                int index = System.Array.IndexOf(cutPoints, point);
                PointCut?.Invoke(this, index, playerId);
                if (director != null && director.Net != null && director.Net.IsClient)
                {
                    // The cut shows at once here; the host books the weld and opens the panel for everyone.
                    bool token = TakeReportToken();
                    if (!paced || (token && m_PendingReports.Count == 0)) director.Net.ReportPointCut(this, index);
                    else m_PendingReports.Add(index);
                    return;
                }
            }

            if (CutCount < TotalCuts) return;

            if (director != null) director.RequestOpenPanel(this, playerId);
            else ApplyOpened();
        }

        static bool TakeReportToken()
        {
            double now = ReportClock != null ? ReportClock() : Time.unscaledTimeAsDouble;
            if (double.IsNegativeInfinity(s_ReportTime) || now < s_ReportTime) s_ReportTokens = ReportBurst;
            else s_ReportTokens = Mathf.Min(ReportBurst, s_ReportTokens + (float)((now - s_ReportTime) * ReportsPerSecond));
            s_ReportTime = now;
            if (s_ReportTokens < 1f) return false;
            s_ReportTokens -= 1f;
            return true;
        }

        /// <summary>Starts the client's report pacing afresh (tests).</summary>
        public static void ResetReportPacing()
        {
            s_ReportTokens = ReportBurst;
            s_ReportTime = double.NegativeInfinity;
        }

        /// <summary>Sends paced weld reports whose turn has come (every frame; tests call it with their own clock).</summary>
        public void PumpReports()
        {
            if (m_PendingReports.Count == 0) return;
            var director = MissionDirector.Active;
            if (director == null || director.Net == null || !director.Net.IsClient || IsOpen)
            {
                m_PendingReports.Clear();
                return;
            }
            while (m_PendingReports.Count > 0 && TakeReportToken())
            {
                director.Net.ReportPointCut(this, m_PendingReports[0]);
                m_PendingReports.RemoveAt(0);
            }
        }

        void Update()
        {
            PumpReports();
            StepVisuals(Time.deltaTime);
        }

        /// <summary>Cools the line, draws in crew progress, eases the plate loose and redraws if anything changed.</summary>
        public void StepVisuals(float dt)
        {
            if (!m_VisualActive) return;
            bool active = false;
            if (cutPoints != null)
            {
                foreach (var p in cutPoints)
                {
                    if (p != null && p.StepVisual(dt)) active = true;
                }
            }
            if (active) m_SeamDirty = true;

            if (!IsOpen && plate != null && m_HomeCached && plate.isKinematic)
            {
                if (m_Loose != m_LooseTarget)
                {
                    m_Loose = Mathf.MoveTowards(m_Loose, m_LooseTarget, dt * 0.8f);
                    ApplyLoosePose();
                    active = true;
                }
            }

            if (m_SeamDirty && m_Seam != null)
            {
                m_Seam.Refresh();
                m_SeamDirty = false;
            }
            m_VisualActive = active;
        }

        void UpdateLooseTarget()
        {
            int total = TotalCuts;
            if (total <= 1 || cutPoints == null) { m_LooseTarget = 0f; return; }
            Vector2 sum = Vector2.zero;
            int cut = 0;
            foreach (var p in cutPoints)
            {
                if (p == null || !p.IsCut) continue;
                Vector3 local = transform.InverseTransformPoint(p.transform.position);
                sum += new Vector2(local.x, local.y);
                cut++;
            }
            // Fully loose with one stretch left: the last one holds it until the host (or this machine) frees it.
            m_LooseTarget = Mathf.Clamp01((float)cut / (total - 1));
            m_LooseToward = cut > 0 ? sum / (cut * Mathf.Max(0.05f, m_HalfExtent)) : Vector2.zero;
            m_VisualActive = true;
        }

        /// <summary>
        /// The plate stands off the face on the side that has been cut (tilting about the side still welded), up to
        /// <see cref="MaxLooseGap"/>, with a small even gap all round once most of the seam is through.
        /// </summary>
        void ApplyLoosePose()
        {
            Vector2 toward = Vector2.ClampMagnitude(m_LooseToward, 1f);
            float lean = toward.magnitude;
            float tiltGap = MaxLooseGap * 0.5f * lean * m_Loose; // the cut side comes out twice this, the welded side stays put
            float angle = Mathf.Atan2(tiltGap, Mathf.Max(0.05f, m_HalfExtent)) * Mathf.Rad2Deg;
            Vector3 axis = lean > 0.001f ? new Vector3(toward.y, -toward.x, 0f).normalized : Vector3.right;
            float evenGap = MaxLooseGap * 0.25f * m_Loose * m_Loose;
            Quaternion tilt = Quaternion.AngleAxis(angle, axis);
            Vector3 outward = Vector3.forward * (evenGap + tiltGap);
            // Pose relative to the home pose, expressed in the panel's own frame (its +Z is out of the face).
            Transform parent = m_PlateHomeParent;
            Quaternion toParent = parent != null ? Quaternion.Inverse(parent.rotation) * transform.rotation : transform.rotation;
            plate.transform.localPosition = m_PlateHomePosition + toParent * outward;
            plate.transform.localRotation = toParent * tilt * Quaternion.Inverse(toParent) * m_PlateHomeRotation;
        }

        /// <summary>
        /// A weld finished on another machine (the host's word). Burns it through here with no judging.
        /// Returns false if it was already through, out of range, or the panel is open.
        /// </summary>
        public bool ApplyRemotePointCut(int index, PlayerId by)
        {
            if (IsOpen || cutPoints == null || index < 0 || index >= cutPoints.Length) return false;
            var point = cutPoints[index];
            if (point == null || point.IsCut) return false;
            point.ForceCut();
            RefreshLabel();
            UpdateLooseTarget();
            if (!isTraining) PointCut?.Invoke(this, index, by);
            return true;
        }

        /// <summary>
        /// Another crew member's burn on one weld (co-op, display only): the weld glows and swells as theirs does but
        /// is never completed or reported from here. False if the index is out of range, the weld is through or the
        /// panel is open.
        /// </summary>
        public bool ApplyRemoteProgress(int index, float progress)
        {
            if (IsOpen || cutPoints == null || index < 0 || index >= cutPoints.Length) return false;
            var point = cutPoints[index];
            if (point == null || point.IsCut) return false;
            point.ApplyRemoteProgress(progress);
            return true;
        }

        /// <summary>Presentation of the opened state. Only the mission director (or a training panel) calls this.</summary>
        public void ApplyOpened()
        {
            if (IsOpen) return;
            // The plate's push is rolled here, in the same order as always; a co-op client is sent the host's.
            Vector3 impulse = Vector3.zero, torque = Vector3.zero;
            if (plate != null)
            {
                impulse = transform.forward * 0.6f + UnityEngine.Random.insideUnitSphere * 0.15f;
                torque = UnityEngine.Random.insideUnitSphere * 0.4f;
            }
            ApplyOpened(impulse, torque);
        }

        /// <summary>
        /// The opened state with a given push for the plate (velocity change and angular velocity change),
        /// so the plate leaves the same way on every machine. Applying it to an open panel does nothing.
        /// </summary>
        public void ApplyOpened(Vector3 plateImpulse, Vector3 plateTorque)
        {
            if (IsOpen) return;
            IsOpen = true;
            m_PendingReports.Clear();
            if (cutPoints != null)
            {
                foreach (var p in cutPoints)
                {
                    if (p != null)
                    {
                        p.ForceCut();
                        // The edges of the freed plate are still hot as it goes.
                        p.Heat(FreedEdgeGlow);
                        p.gameObject.SetActive(false);
                    }
                }
            }
            MarkSeamDirty();

            if (plate != null)
            {
                CacheHome();
                plate.transform.SetParent(null, true);
                GameLayers.SetLayerRecursively(plate.gameObject, GameLayers.Salvage);
                plate.isKinematic = false;
                plate.useGravity = false;
                plate.AddForce(plateImpulse, ForceMode.VelocityChange);
                plate.AddTorque(plateTorque, ForceMode.VelocityChange);
                LastPlateImpulse = plateImpulse;
                LastPlateTorque = plateTorque;
            }

            if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.panelBreak, transform.position, 1f);
            RefreshLabel();
            Opened?.Invoke(this);
        }

        /// <summary>Welds everything back for a new run.</summary>
        public void ResetForRun()
        {
            IsOpen = false;
            m_PendingReports.Clear();
            m_Loose = 0f;
            m_LooseTarget = 0f;
            m_LooseToward = Vector2.zero;
            if (plate != null)
            {
                CacheHome();
                if (!plate.isKinematic)
                {
                    plate.linearVelocity = Vector3.zero;
                    plate.angularVelocity = Vector3.zero;
                }
                plate.isKinematic = true;
                plate.transform.SetParent(m_PlateHomeParent, false);
                plate.transform.localPosition = m_PlateHomePosition;
                plate.transform.localRotation = m_PlateHomeRotation;
                GameLayers.SetLayerRecursively(plate.gameObject, GameLayers.Environment);
            }
            if (cutPoints != null)
            {
                foreach (var p in cutPoints)
                {
                    if (p == null) continue;
                    p.gameObject.SetActive(true);
                    p.ResetPoint();
                }
            }
            MarkSeamDirty();
            RefreshLabel();
        }

        void RefreshLabel()
        {
            if (progressLabel == null) return;
            progressLabel.text = IsOpen
                ? displayName + "\nOPEN"
                : displayName + "\nCUT " + CutCount + "/" + TotalCuts;
        }
    }
}

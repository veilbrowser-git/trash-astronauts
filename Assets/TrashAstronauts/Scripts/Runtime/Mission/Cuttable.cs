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
    /// A predefined detachable piece: an access panel or a salvage mounting bracket. It opens when every
    /// cut point on its seam has been burnt through. No mesh slicing; the plate is a separate object
    /// that is released and floats away.
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
        }

        void CacheHome()
        {
            if (m_HomeCached || plate == null) return;
            m_PlateHomeParent = plate.transform.parent;
            m_PlateHomePosition = plate.transform.localPosition;
            m_PlateHomeRotation = plate.transform.localRotation;
            m_HomeCached = true;
        }

        /// <summary>Called by the plasma cutter while its flame overlaps one of this seam's points.</summary>
        public void ApplyCut(CutPoint point, float seconds, float secondsRequired, PlayerContext by)
        {
            if (IsOpen || point == null || point.Owner != this) return;
            if (!point.Burn(seconds, secondsRequired)) return;

            if (by != null) by.DominantHand?.Pulse(0.7f, 0.08f);
            RefreshLabel();

            var director = MissionDirector.Active;
            PlayerId playerId = by != null ? by.Id : director != null ? director.LocalPlayerId : PlayerId.Local;
            if (!isTraining)
            {
                int index = System.Array.IndexOf(cutPoints, point);
                PointCut?.Invoke(this, index, playerId);
                if (director != null && director.Net != null && director.Net.IsClient)
                {
                    // The burn shows at once here; the host books the weld and opens the panel for everyone.
                    director.Net.ReportPointCut(this, index);
                    return;
                }
            }

            if (CutCount < TotalCuts) return;

            if (director != null) director.RequestOpenPanel(this, playerId);
            else ApplyOpened();
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
            if (cutPoints != null)
            {
                foreach (var p in cutPoints)
                {
                    if (p != null)
                    {
                        p.ForceCut();
                        p.gameObject.SetActive(false);
                    }
                }
            }

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

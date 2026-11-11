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

            if (CutCount < TotalCuts) return;

            PlayerId playerId = by != null ? by.Id : PlayerId.Local;
            var director = MissionDirector.Active;
            if (director != null) director.RequestOpenPanel(this, playerId);
            else ApplyOpened();
        }

        /// <summary>Presentation of the opened state. Only the mission director (or a training panel) calls this.</summary>
        public void ApplyOpened()
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
                plate.AddForce(transform.forward * 0.6f + UnityEngine.Random.insideUnitSphere * 0.15f, ForceMode.VelocityChange);
                plate.AddTorque(UnityEngine.Random.insideUnitSphere * 0.4f, ForceMode.VelocityChange);
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

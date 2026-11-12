using System.Collections.Generic;
using TMPro;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// The tug's collection locker. Let go of salvage inside its hopper and it is secured behind the
    /// grille, counted once, and cannot be taken back out for the rest of the run.
    /// </summary>
    public sealed class CollectionLocker : MonoBehaviour
    {
        public MissionDirector director;
        [Tooltip("Centre of the hopper volume. Its local box of 'zoneSize' is the deposit area.")]
        public Transform zone;
        public Vector3 zoneSize = new Vector3(0.9f, 0.7f, 0.6f);
        public Transform[] slots;
        public TMP_Text display;
        public float scanInterval = 0.1f;

        readonly Collider[] m_Hits = new Collider[24];
        readonly HashSet<SalvageItem> m_Rejected = new HashSet<SalvageItem>();
        float m_NextScan;
        int m_NextSlot;
        string m_Flash;
        float m_FlashUntil;

        /// <summary>Raised when the tutorial's practice item is dropped in.</summary>
        public event System.Action TrainingItemDeposited;
        public int TrickShotCount { get; private set; }

        void OnEnable()
        {
            if (director == null) director = MissionDirector.Active;
            if (director == null) return;
            director.MissionStarted += OnMissionChanged;
            director.MissionEnded += OnMissionEnded;
        }

        void OnDisable()
        {
            if (director == null) return;
            director.MissionStarted -= OnMissionChanged;
            director.MissionEnded -= OnMissionEnded;
        }

        void Start() => RefreshDisplay();

        void OnMissionChanged() => RefreshDisplay();
        void OnMissionEnded(MissionResult result) => RefreshDisplay();

        public void ResetForRun()
        {
            m_NextSlot = 0;
            m_Rejected.Clear();
            m_Flash = null;
            if (slots != null)
            {
                foreach (var slot in slots)
                {
                    if (slot == null) continue;
                    for (int i = slot.childCount - 1; i >= 0; i--) Destroy(slot.GetChild(i).gameObject);
                }
            }
            RefreshDisplay();
        }

        void Update()
        {
            if (Time.time >= m_NextScan)
            {
                m_NextScan = Time.time + scanInterval;
                ScanZone();
            }
            if (m_Flash != null && Time.time >= m_FlashUntil)
            {
                m_Flash = null;
                RefreshDisplay();
            }
        }

        /// <summary>Checks the hopper for released salvage. Public so tests can drive it deterministically.</summary>
        public void ScanZone()
        {
            if (zone == null) return;
            int count = Physics.OverlapBoxNonAlloc(zone.position, zoneSize * 0.5f, m_Hits, zone.rotation,
                GameLayers.SalvageMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                var item = m_Hits[i].GetComponentInParent<SalvageItem>();
                if (item == null || item.IsSecured || item.IsHeld || item.IsMounted || item.StoredIn != null) continue;
                TryAccept(item);
            }
        }

        /// <summary>Attempts to secure one item. Returns true if it was accepted.</summary>
        public bool TryAccept(SalvageItem item)
        {
            if (item == null || item.IsSecured || item.IsMounted || item.StoredIn != null) return false;

            if (item.isTraining)
            {
                item.Secure(NextSlot());
                Flash("TRAINING ITEM SECURED\nWorth nothing. Like overtime.", 4f);
                Feedback(item, false);
                TrainingItemDeposited?.Invoke();
                return true;
            }

            if (director == null) return false;
            DepositResult result = director.DepositSalvage(item, item.LastHolder);
            if (result == DepositResult.Accepted)
            {
                // Thrown in from across the cabin? That is a trick shot.
                int trickBonus = 0;
                var config = director.Config;
                if (item.WasThrown && zone != null && Vector3.Distance(item.ReleasePosition, zone.position) >= config.trickShotDistance)
                    trickBonus = director.AwardTrickShot(item);

                item.Secure(NextSlot());
                string itemName = item.definition != null ? item.definition.displayName : "Salvage";
                if (trickBonus > 0)
                {
                    TrickShotCount++;
                    Flash("TRICK SHOT!  " + itemName + "\n+" + item.Value + " cr  +" + trickBonus + " bonus", 4f);
                    if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.trickShot, transform.position, 0.8f);
                }
                else
                {
                    Flash("SECURED: " + itemName + "\n+" + item.Value + " cr", 3f);
                }
                Feedback(item, item.Value >= 300 || trickBonus > 0);
                return true;
            }

            if (result == DepositResult.MissionNotActive && m_Rejected.Add(item))
            {
                Flash("LOCKER SEALED\nNo active contract.", 3f);
            }
            return false;
        }

        Transform NextSlot()
        {
            if (slots == null || slots.Length == 0) return transform;
            var slot = slots[m_NextSlot % slots.Length];
            m_NextSlot++;
            return slot;
        }

        void Feedback(SalvageItem item, bool jackpot)
        {
            if (Sfx.Library != null)
                Sfx.PlayAt(jackpot ? Sfx.Library.depositJackpot : Sfx.Library.deposit, transform.position, 0.9f);
            if (director != null)
            {
                var player = director.FindPlayer(item.LastHolder);
                if (player != null) player.PulseBoth(jackpot ? 0.7f : 0.45f, jackpot ? 0.2f : 0.1f);
            }
        }

        void Flash(string message, float seconds)
        {
            m_Flash = message;
            m_FlashUntil = Time.time + seconds;
            RefreshDisplay();
        }

        public void RefreshDisplay()
        {
            if (display == null) return;
            if (m_Flash != null)
            {
                display.text = m_Flash;
                return;
            }

            var session = director != null ? director.Session : null;
            if (session == null || session.Phase == MissionPhase.Hub)
            {
                display.text = "COLLECTION LOCKER\nDrop salvage in the hopper.";
            }
            else
            {
                string status = session.QuotaMet ? "QUOTA MET - extract when ready" : "Quota not met yet";
                display.text = "SECURED: " + session.DepositedCount + " items\n"
                    + session.SecuredValue + " / " + session.Quota + " cr\n" + status
                    + (session.TrickShots > 0 ? "\nTrick shots: " + session.TrickShots : "\nThrow it in from far away for a bonus");
            }
        }

        void OnDrawGizmos()
        {
            if (zone == null) return;
            Gizmos.matrix = Matrix4x4.TRS(zone.position, zone.rotation, Vector3.one);
            Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.5f);
            Gizmos.DrawWireCube(Vector3.zero, zoneSize);
        }
    }
}

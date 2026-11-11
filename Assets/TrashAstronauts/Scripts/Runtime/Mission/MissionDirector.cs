using System;
using System.Collections.Generic;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    [Flags]
    public enum HazardPauseReason
    {
        None = 0,
        ApplicationFocus = 1 << 0,
        TrackingLost = 1 << 1,
        Scripted = 1 << 2,
    }

    /// <summary>
    /// Scene-side authority for the salvage mission. It owns the <see cref="MissionSession"/> (rules) and
    /// the <see cref="ProfileService"/> (persistent credits and settings), and is the only thing that
    /// spawns salvage, opens panels, starts runs, ends runs and pays out.
    ///
    /// Every shared-state change is one public method here (StartMission, NotifySalvageCollected,
    /// DepositSalvage, RequestOpenPanel, Extract, FailMission, PurchaseOxygenUpgrade). In co-op these
    /// become host-authoritative requests; see Docs/MULTIPLAYER_NOTES.md.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class MissionDirector : MonoBehaviour
    {
        /// <summary>The mission running in the loaded scene. One mission per scene, any number of players.</summary>
        public static MissionDirector Active { get; private set; }

        /// <summary>Test seam: replaces the on-disk save so tests never touch real progress.</summary>
        public static ISaveStorage StorageOverride;
        /// <summary>Test seam: forces the loot seed for the next run.</summary>
        public static int? SeedOverride;

        [Header("Data")]
        [SerializeField] MissionConfig m_Config;
        [SerializeField] AudioLibrary m_AudioLibrary;
        [SerializeField] SalvageDefinition[] m_Catalog;

        [Header("Scene")]
        public NoiseSystem noise;
        public SafeZone safeZone;
        public PlayableBounds bounds;
        public CollectionLocker locker;
        public SlidingDoor tugHatch;
        public Transform hubSpawn;
        public Transform lostAndFound;
        public Transform salvageRoot;
        public SalvageSpawnPoint[] spawnPoints;
        public Cuttable[] cuttables;
        public EnemyDrone[] enemies;
        public SlidingDoor[] wreckDoors;

        readonly List<PlayerContext> m_Players = new List<PlayerContext>();
        readonly List<SalvageItem> m_Items = new List<SalvageItem>();
        readonly Dictionary<string, SalvageDefinition> m_CatalogById = new Dictionary<string, SalvageDefinition>();
        HazardPauseReason m_PauseReasons;
        float m_NextBoundsCheck;

        public MissionConfig Config => m_Config;
        public MissionSession Session { get; private set; }
        public ProfileService Profile { get; private set; }
        public SpawnPlan LastPlan { get; private set; }
        public IReadOnlyList<PlayerContext> Players => m_Players;
        public IReadOnlyList<SalvageItem> Items => m_Items;
        public bool HazardsPaused => m_PauseReasons != HazardPauseReason.None;
        public MissionPhase Phase => Session != null ? Session.Phase : MissionPhase.Hub;
        public float NextRunOxygenCapacity => m_Config.GetOxygenCapacity(Profile.Data.oxygenUpgradeLevel);

        public event Action MissionStarted;
        public event Action<MissionResult> MissionEnded;
        public event Action<SalvageItem> SalvageDeposited;
        public event Action<Cuttable> PanelOpened;
        public event Action ProfileChanged;
        public event Action<bool> HazardPauseChanged;

        void Awake()
        {
            Active = this;
            if (m_Config == null) m_Config = ScriptableObject.CreateInstance<MissionConfig>();
            Sfx.SetLibrary(m_AudioLibrary);

            if (m_Catalog != null)
            {
                foreach (var def in m_Catalog)
                {
                    if (def != null && !string.IsNullOrEmpty(def.id)) m_CatalogById[def.id] = def;
                }
            }

            Profile = new ProfileService(StorageOverride ?? FileSaveStorage.Default(), m_Config.maxOxygenUpgradeLevel);
            Profile.Load();
            Profile.Changed += OnProfileChanged;
            Session = MissionSession.FromConfig(m_Config);

            if (noise != null) noise.Muted = true;
        }

        void Start()
        {
            if (tugHatch != null)
            {
                tugHatch.SetLocked(true);
                tugHatch.SetOpen(false, true);
            }
            if (enemies != null)
            {
                foreach (var enemy in enemies)
                {
                    if (enemy != null) enemy.EndRun();
                }
            }
        }

        void OnDestroy()
        {
            if (Profile != null) Profile.Changed -= OnProfileChanged;
            if (Active == this) Active = null;
        }

        void OnProfileChanged() => ProfileChanged?.Invoke();

        // ------------------------------------------------------------------ players

        public void RegisterPlayer(PlayerContext player)
        {
            if (player == null || m_Players.Contains(player)) return;
            m_Players.Add(player);
            ApplyProfileTo(player);
        }

        public void UnregisterPlayer(PlayerContext player) => m_Players.Remove(player);

        public PlayerContext FindPlayer(PlayerId id)
        {
            for (int i = 0; i < m_Players.Count; i++)
            {
                if (m_Players[i] != null && m_Players[i].Id == id) return m_Players[i];
            }
            return m_Players.Count > 0 ? m_Players[0] : null;
        }

        /// <summary>Pushes saved comfort settings and handedness to the local rig(s) and saves them.</summary>
        public void ApplyAndSaveSettings()
        {
            Profile.NotifyChangedAndSave();
            foreach (var player in m_Players) ApplyProfileTo(player);
        }

        void ApplyProfileTo(PlayerContext player)
        {
            if (player == null) return;
            player.SetHandedness(Profile.Data.Handedness);
            if (player.locomotion != null) player.locomotion.ApplyComfort(Profile.Data);
        }

        // ------------------------------------------------------------------ hazards pause

        /// <summary>Pauses oxygen and the drone (headset removed, tracking lost, app unfocused).</summary>
        public void SetHazardPause(HazardPauseReason reason, bool paused)
        {
            bool was = HazardsPaused;
            if (paused) m_PauseReasons |= reason;
            else m_PauseReasons &= ~reason;
            if (was != HazardsPaused) HazardPauseChanged?.Invoke(HazardsPaused);
        }

        // ------------------------------------------------------------------ run lifecycle

        /// <summary>Starts a new contract from the hub or straight from a results screen.</summary>
        public bool StartMission(PlayerId by)
        {
            if (Session.Phase == MissionPhase.Active) return false;

            int seed = SeedOverride ?? UnityEngine.Random.Range(1, int.MaxValue);
            SeedOverride = null;

            var infos = new List<SpawnPointInfo>();
            if (spawnPoints != null)
            {
                foreach (var point in spawnPoints)
                {
                    if (point != null) infos.Add(point.ToInfo());
                }
            }
            var specs = new List<SalvageSpec>();
            if (m_Catalog != null)
            {
                foreach (var def in m_Catalog)
                {
                    if (def != null && def.prefab != null) specs.Add(def.ToSpec());
                }
            }

            var plan = SalvageSpawnPlanner.Plan(infos, specs, m_Config.GetCount, m_Config.quotaCredits,
                m_Config.quotaSafetyMargin, seed, Session.RunIndex + 1);
            if (plan.QuotaWasLowered)
                Debug.LogWarning("[Mission] Not enough spawn points to guarantee the quota; lowered it to " + plan.Quota + ".");

            ClearRunObjects();
            if (!Session.Begin(seed, plan.Quota, NextRunOxygenCapacity)) return false;
            LastPlan = plan;

            Profile.Data.tutorialCompleted = true;
            Profile.RecordRunStarted();

            SpawnFromPlan(plan);

            if (locker != null) locker.ResetForRun();
            if (wreckDoors != null)
            {
                foreach (var door in wreckDoors)
                {
                    if (door != null) door.ResetForRun();
                }
            }
            if (noise != null) noise.Muted = false;
            if (enemies != null)
            {
                foreach (var enemy in enemies)
                {
                    if (enemy != null) enemy.BeginRun(seed);
                }
            }
            if (tugHatch != null) tugHatch.SetOpen(true);

            MissionStarted?.Invoke();
            return true;
        }

        void SpawnFromPlan(SpawnPlan plan)
        {
            var pointsById = new Dictionary<string, SalvageSpawnPoint>();
            foreach (var point in spawnPoints)
            {
                if (point != null) pointsById[point.pointId] = point;
            }

            foreach (var assignment in plan.Assignments)
            {
                if (!pointsById.TryGetValue(assignment.spawnPointId, out var point)) continue;
                if (!m_CatalogById.TryGetValue(assignment.definitionId, out var def) || def.prefab == null) continue;

                var go = Instantiate(def.prefab, point.transform.position, point.transform.rotation, salvageRoot);
                var item = go.GetComponent<SalvageItem>();
                if (item == null)
                {
                    Destroy(go);
                    continue;
                }
                item.Initialize(this, assignment.salvageId, def, point.mount);
                Session.RegisterSalvage(assignment.salvageId, def.id, def.creditValue);
                m_Items.Add(item);
            }
        }

        void ClearRunObjects()
        {
            foreach (var item in m_Items)
            {
                if (item != null) Destroy(item.gameObject);
            }
            m_Items.Clear();

            // Anything else left under the salvage root (the tutorial's practice item, for example).
            if (salvageRoot != null)
            {
                for (int i = salvageRoot.childCount - 1; i >= 0; i--) Destroy(salvageRoot.GetChild(i).gameObject);
            }

            if (cuttables != null)
            {
                foreach (var cuttable in cuttables)
                {
                    if (cuttable != null) cuttable.ResetForRun();
                }
            }
        }

        /// <summary>Manual extraction. Only works for a player who is inside the tug.</summary>
        public MissionResult Extract(PlayerId by)
        {
            if (Session.Phase != MissionPhase.Active) return null;
            var player = FindPlayer(by);
            if (player != null && safeZone != null && !safeZone.Contains(player.HeadPosition)) return null;

            var result = Session.Extract(by);
            if (result != null) FinishRun(result);
            return result;
        }

        /// <summary>Fails the run (oxygen ran out or the drone caught someone).</summary>
        public MissionResult FailMission(FailureReason reason, PlayerId victim)
        {
            var result = Session.Fail(reason);
            if (result != null) FinishRun(result);
            return result;
        }

        void FinishRun(MissionResult result)
        {
            Session.BankInto(Profile);

            if (noise != null) noise.Muted = true;
            if (enemies != null)
            {
                foreach (var enemy in enemies)
                {
                    if (enemy != null) enemy.EndRun();
                }
            }

            if (Sfx.Library != null)
            {
                AudioClip sting = result.IsFailure ? Sfx.Library.resultFail
                    : result.Outcome == MissionOutcome.ContractComplete ? Sfx.Library.resultSuccess
                    : Sfx.Library.resultPartial;
                Sfx.PlayUi(sting, 0.8f);
            }

            if (result.IsFailure)
            {
                // Bring everyone home behind a fade, then seal the hatch.
                bool anyMoved = false;
                foreach (var player in m_Players)
                {
                    if (player == null || player.locomotion == null || hubSpawn == null) continue;
                    anyMoved = true;
                    player.locomotion.FadeTeleport(player.fader, hubSpawn, CloseHatch);
                }
                if (!anyMoved) CloseHatch();
            }
            else
            {
                CloseHatch();
            }

            MissionEnded?.Invoke(result);
        }

        void CloseHatch()
        {
            if (tugHatch != null) tugHatch.SetOpen(false);
        }

        /// <summary>Leaves the results screen without starting a new contract.</summary>
        public bool ReturnToHub()
        {
            if (!Session.ReturnToHub()) return false;
            ClearRunObjects();
            if (locker != null) locker.ResetForRun();
            return true;
        }

        // ------------------------------------------------------------------ shared-state changes

        public bool NotifySalvageCollected(SalvageItem item, PlayerId by)
        {
            return item != null && Session.TryCollect(item.SalvageId, by);
        }

        /// <summary>Records a deposit. The ledger accepts each salvage id exactly once.</summary>
        public DepositResult DepositSalvage(SalvageItem item, PlayerId by)
        {
            if (item == null) return DepositResult.UnknownItem;
            var result = Session.TryDeposit(item.SalvageId, by);
            if (result == DepositResult.Accepted) SalvageDeposited?.Invoke(item);
            return result;
        }

        /// <summary>Opens a cuttable once all of its points are burnt through.</summary>
        public bool RequestOpenPanel(Cuttable cuttable, PlayerId by)
        {
            if (cuttable == null || cuttable.IsOpen) return false;

            if (cuttable.isTraining)
            {
                cuttable.ApplyOpened();
                return true;
            }

            if (!Session.TryOpenPanel(cuttable.panelId, by)) return false;
            cuttable.ApplyOpened();
            if (noise != null)
                noise.Emit(new NoiseEvent(cuttable.transform.position, m_Config.noise.panelBreakLoudness, NoiseKind.PanelBreak, by));
            PanelOpened?.Invoke(cuttable);
            return true;
        }

        /// <summary>Buys the oxygen tank upgrade with banked credits. Not available mid-contract.</summary>
        public bool PurchaseOxygenUpgrade()
        {
            if (Session.Phase == MissionPhase.Active) return false;
            return Profile.TryPurchaseOxygenUpgrade(m_Config);
        }

        // ------------------------------------------------------------------ tick

        void Update()
        {
            bool everyoneSafe = true;
            for (int i = 0; i < m_Players.Count; i++)
            {
                var player = m_Players[i];
                if (player == null) continue;
                player.InSafeZone = safeZone == null || safeZone.Contains(player.HeadPosition);
                everyoneSafe &= player.InSafeZone;
            }

            if (Session.Phase == MissionPhase.Active && !HazardsPaused)
            {
                // With no registered player (tests) oxygen is controlled explicitly via TickMission.
                if (m_Players.Count > 0) TickMission(Time.deltaTime, everyoneSafe);
            }

            if (Time.time >= m_NextBoundsCheck)
            {
                m_NextBoundsCheck = Time.time + 0.5f;
                RecoverLostObjects();
            }
        }

        /// <summary>Advances mission time. Public so tests can fast-forward oxygen.</summary>
        public MissionResult TickMission(float deltaSeconds, bool playersInSafeZone)
        {
            var result = Session.Tick(deltaSeconds, playersInSafeZone);
            if (result != null) FinishRun(result);
            return result;
        }

        void RecoverLostObjects()
        {
            if (bounds == null) return;

            foreach (var item in m_Items)
            {
                if (item == null || item.IsSecured || item.IsHeld || item.IsMounted) continue;
                if (bounds.Contains(item.transform.position)) continue;
                Vector3 home = item.HasBeenCollected && lostAndFound != null ? lostAndFound.position : item.SpawnPosition;
                item.RecoverTo(home);
            }

            foreach (var player in m_Players)
            {
                if (player == null || player.locomotion == null) continue;
                if (!bounds.Contains(player.HeadPosition)) player.locomotion.Unstuck();
            }
        }
    }
}

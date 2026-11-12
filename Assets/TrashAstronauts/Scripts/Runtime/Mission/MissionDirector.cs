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
        /// <summary>Test seam: forces which wreck the next contract is on.</summary>
        public static int? MapOverride;
        /// <summary>Test seam: forces this contract's condition.</summary>
        public static ContractCondition? ConditionOverride;
        /// <summary>Test seam: forces which creature from the roster is aboard.</summary>
        public static int? CreatureOverride;

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
        [Header("Wrecks (one is docked per contract)")]
        public WreckMap[] maps;
        [Tooltip("Panels that exist whatever wreck is docked (the practice plate in the tug).")]
        public Cuttable[] sharedCuttables;
        [Tooltip("Doors that exist whatever wreck is docked (the airlock's inner door).")]
        public SlidingDoor[] sharedDoors;
        public BlackoutController blackout;
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
        /// <summary>The creature aboard this contract. A different one is picked from the roster each run.</summary>
        public EnemyDrone ActiveEnemy { get; private set; }
        /// <summary>What is unusual about the current contract.</summary>
        public ContractCondition Condition { get; private set; }
        /// <summary>The wreck currently docked to the airlock.</summary>
        public WreckMap ActiveMap { get; private set; }
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
            SelectCreature(0);
            SelectMap(0);
        }

        /// <summary>
        /// Docks one wreck: switches its geometry on and the others off, and points loot, panels, doors,
        /// the blackout and the creature's routes at it. Only called while nobody is aboard.
        /// </summary>
        void SelectMap(int index)
        {
            if (maps == null || maps.Length == 0) return;
            index = Mathf.Clamp(index, 0, maps.Length - 1);
            var map = maps[index];
            if (map == null) return;
            for (int i = 0; i < maps.Length; i++)
            {
                if (maps[i] != null) maps[i].SetAboard(i == index);
            }
            ActiveMap = map;

            spawnPoints = map.spawnPoints ?? new SalvageSpawnPoint[0];
            cuttables = Join(sharedCuttables, map.cuttables);
            wreckDoors = Join(sharedDoors, map.doors);
            if (enemies != null)
            {
                foreach (var enemy in enemies)
                {
                    if (enemy == null) continue;
                    enemy.graph = map.graph;
                    enemy.patrolRoute = map.patrolRoute;
                    enemy.doors = map.creatureDoors;
                    enemy.dock = map.dock;
                }
            }
            if (blackout != null) blackout.SetTargets(map.lights, map.glowStrips, map.ambience);
            Physics.SyncTransforms();
        }

        static T[] Join<T>(T[] a, T[] b)
        {
            var list = new List<T>();
            if (a != null) list.AddRange(a);
            if (b != null) list.AddRange(b);
            return list.ToArray();
        }

        /// <summary>Makes one creature from the roster the one aboard; the others are switched off.</summary>
        void SelectCreature(int index)
        {
            if (enemies == null || enemies.Length == 0) return;
            index = Mathf.Clamp(index, 0, enemies.Length - 1);
            for (int i = 0; i < enemies.Length; i++)
            {
                if (enemies[i] != null) enemies[i].gameObject.SetActive(i == index);
            }
            ActiveEnemy = enemies[index];
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

            // Clear the last wreck out completely before another one docks.
            ClearRunObjects();
            if (maps != null && maps.Length > 0)
                SelectMap(MapOverride ?? new System.Random(seed ^ 0x1b873593).Next(maps.Length));

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
                    if (def != null && def.prefab != null && !def.bonusSpawn) specs.Add(def.ToSpec());
                }
            }

            var plan = SalvageSpawnPlanner.Plan(infos, specs, m_Config.GetCount, m_Config.quotaCredits,
                m_Config.quotaSafetyMargin, seed, Session.RunIndex + 1);
            if (plan.QuotaWasLowered)
                Debug.LogWarning("[Mission] Not enough spawn points to guarantee the quota; lowered it to " + plan.Quota + ".");

            ClearRunObjects();
            Condition = ConditionOverride ?? ContractConditions.Pick(seed);
            if (!Session.Begin(seed, plan.Quota, NextRunOxygenCapacity * ContractConditions.OxygenMultiplier(Condition))) return false;
            Session.BonusMultiplier = ContractConditions.BonusMultiplier(Condition);
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
            if (enemies != null && enemies.Length > 0)
            {
                // A different thing is waiting in the wreck each time.
                int pick = CreatureOverride ?? new System.Random(seed ^ 0x5f3759df).Next(enemies.Length);
                SelectCreature(pick);
                if (ActiveEnemy != null) ActiveEnemy.BeginRun(seed);
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
                int worth = Mathf.RoundToInt(def.creditValue * ContractConditions.ValueMultiplier(Condition));
                item.SetBaseValue(worth);
                Session.RegisterSalvage(assignment.salvageId, def.id, worth);
                m_Items.Add(item);
            }

            SpawnBonusItem(plan, pointsById);
        }

        /// <summary>Hides one bonus item (the rubber duck) at a free, open spawn point. It is extra: never part of the quota maths.</summary>
        void SpawnBonusItem(SpawnPlan plan, Dictionary<string, SalvageSpawnPoint> pointsById)
        {
            SalvageDefinition bonus = null;
            foreach (var def in m_Catalog)
            {
                if (def != null && def.bonusSpawn && def.prefab != null) bonus = def;
            }
            if (bonus == null) return;

            var used = new HashSet<string>();
            foreach (var assignment in plan.Assignments) used.Add(assignment.spawnPointId);
            var free = new List<SalvageSpawnPoint>();
            foreach (var point in spawnPoints)
            {
                if (point != null && point.Gate == null && !used.Contains(point.pointId)) free.Add(point);
            }
            if (free.Count == 0) return;

            var spot = free[new System.Random(Session.Seed ^ 0x2545f491).Next(free.Count)];
            var go = Instantiate(bonus.prefab, spot.transform.position, spot.transform.rotation, salvageRoot);
            var item = go.GetComponent<SalvageItem>();
            if (item == null)
            {
                Destroy(go);
                return;
            }
            string id = SalvageSpawnPlanner.MakeSalvageId(Session.RunIndex, spot.pointId + "#bonus");
            item.Initialize(this, id, bonus, null);
            Session.RegisterSalvage(id, bonus.id, bonus.creditValue);
            m_Items.Add(item);
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

        /// <summary>Pays the trick-shot bonus for an item that was just deposited. Returns the bonus credits.</summary>
        public int AwardTrickShot(SalvageItem item)
        {
            if (item == null) return 0;
            int bonus = Mathf.RoundToInt(item.Value * m_Config.trickShotBonusFraction);
            return Session.AwardTrickShot(item.SalvageId, bonus) ? bonus : 0;
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

        /// <summary>Buys something from the tug's shop. The shop is closed during a contract.</summary>
        public bool PurchaseShopItem(ShopItem item)
        {
            if (Session.Phase == MissionPhase.Active) return false;
            return Profile.TryPurchase(item, m_Config);
        }

        // ------------------------------------------------------------------ tick

        [Header("Spawn safety")]
        [Tooltip("Puts the player's HEAD (not just their play-space origin) at the hub spawn when the game starts, and brings them back if they end up outside the tug before a contract has started.")]
        public bool keepPlayersInHub = true;
        /// <summary>How many times a player had to be put back inside the tug.</summary>
        public int HubRescueCount { get; private set; }
        bool m_SpawnAligned;
        float m_OutsideHubTimer;
        float m_SceneStartTime = -1f;

        /// <summary>
        /// A headset's play space is rarely centred on the person wearing it: someone standing a couple of
        /// metres from the middle of their room would otherwise start a couple of metres from the spawn
        /// point, which in a 4 m wide tug means outside it. Teleporting is head-accurate, so do one at the
        /// start, and whenever a player is found outside the tug while there is no contract running.
        /// </summary>
        void GuardHubSpawn(float dt)
        {
            if (!keepPlayersInHub || hubSpawn == null || safeZone == null || m_Players.Count == 0) return;
            if (m_SceneStartTime < 0f) m_SceneStartTime = Time.unscaledTime;

            if (!m_SpawnAligned)
            {
                // Give tracking a moment to report where the head really is.
                if (Time.unscaledTime - m_SceneStartTime < 0.5f) return;
                m_SpawnAligned = true;
                if (Session == null || Session.Phase != MissionPhase.Hub) return;
                foreach (var player in m_Players)
                {
                    if (player == null || player.locomotion == null) continue;
                    // Only if they are standing away from the middle of their play space (that is what puts
                    // the head somewhere other than the spawn point). Someone centred is left alone.
                    Vector3 offset = player.HeadPosition - player.transform.position;
                    offset.y = 0f;
                    if (offset.magnitude > 0.3f) player.locomotion.TeleportTo(hubSpawn.position, hubSpawn.rotation);
                }
                return;
            }

            if (Session == null || Session.Phase != MissionPhase.Hub)
            {
                m_OutsideHubTimer = 0f;
                return;
            }
            bool anyOutside = false;
            foreach (var player in m_Players)
            {
                if (player == null || player.locomotion == null) continue;
                Vector3 head = player.HeadPosition;
                head.y = safeZone.transform.position.y; // only the floor plan matters
                if (safeZone.Contains(head)) continue;
                anyOutside = true;
                if (m_OutsideHubTimer + dt >= 0.75f)
                {
                    player.locomotion.TeleportTo(hubSpawn.position, hubSpawn.rotation);
                    HubRescueCount++;
                }
            }
            m_OutsideHubTimer = anyOutside ? (m_OutsideHubTimer + dt >= 0.75f ? 0f : m_OutsideHubTimer + dt) : 0f;
        }

        void Update()
        {
            GuardHubSpawn(Time.unscaledDeltaTime);
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
                // Working hard (swimming, thrusting) uses oxygen faster.
                if (m_Players.Count > 0)
                {
                    float exertion = 0f;
                    foreach (var player in m_Players)
                    {
                        if (player != null && player.locomotion != null && player.locomotion.zeroG != null)
                            exertion = Mathf.Max(exertion, player.locomotion.zeroG.Exertion);
                    }
                    TickMission(Time.deltaTime * (1f + m_Config.exertionOxygenCost * exertion), everyoneSafe);
                }
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

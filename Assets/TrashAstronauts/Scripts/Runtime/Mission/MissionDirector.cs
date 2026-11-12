using System;
using System.Collections.Generic;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Net;
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
        /// <summary>Co-op client: the host's hazards are paused (only the host's pause freezes them).</summary>
        HostPaused = 1 << 3,
    }

    /// <summary>
    /// Scene-side authority for the salvage mission. It owns the <see cref="MissionSession"/> (rules) and
    /// the <see cref="ProfileService"/> (persistent credits and settings), and is the only thing that
    /// spawns salvage, opens panels, starts runs, ends runs and pays out.
    ///
    /// Every shared-state change is one public method here (StartMission, NotifySalvageCollected,
    /// DepositSalvage, RequestOpenPanel, Extract, FailMission, PurchaseOxygenUpgrade). In co-op these
    /// become host-authoritative requests; see Docs/MULTIPLAYER_NOTES.md.
    ///
    /// Co-op seam: <see cref="Net"/> is null in single-player, and that is the code path that runs
    /// today. On a client each of those methods hands the request to the session instead of acting.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed partial class MissionDirector : MonoBehaviour
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
        [Tooltip("Webs that exist whatever wreck is docked. Each wreck lists its own in WreckMap.tripwires.")]
        public WebTripwire[] sharedTripwires;

        /// <summary>
        /// The co-op session this mission is part of, or null in single-player. Set only by NetSession, after
        /// HOST CO-OP succeeded or a join was welcomed; cleared by <see cref="LeaveSession"/>.
        /// </summary>
        public IMissionNet Net { get; set; }

        /// <summary>
        /// Co-op: true only while this headset is between pressing JOIN (or HOST ONLINE) and being let in, so
        /// it cannot wander off into a solo contract mid-handshake. Hosting or joined, contracts are shared and
        /// this is false. Set by NetSession; always false in single-player.
        /// </summary>
        public bool ContractStartBlocked { get; set; }

        readonly List<PlayerContext> m_Players = new List<PlayerContext>();
        readonly List<SalvageItem> m_Items = new List<SalvageItem>();
        readonly List<SalvageItem> m_RunItems = new List<SalvageItem>();
        readonly Dictionary<string, SalvageDefinition> m_CatalogById = new Dictionary<string, SalvageDefinition>();
        HazardPauseReason m_PauseReasons;
        float m_NextBoundsCheck;

        public MissionConfig Config => m_Config;
        public MissionSession Session { get; private set; }
        public ProfileService Profile { get; private set; }
        public SpawnPlan LastPlan { get; private set; }
        public IReadOnlyList<PlayerContext> Players => m_Players;
        public IReadOnlyList<SalvageItem> Items => m_Items;
        /// <summary>
        /// This run's salvage by net id (the index in <see cref="RunStartParams.items"/>). An entry is null
        /// if that item could not be spawned here. Empty when no contract has been started.
        /// </summary>
        public IReadOnlyList<SalvageItem> RunItems => m_RunItems;
        /// <summary>What the current contract (or the last one) was started with.</summary>
        public RunStartParams CurrentRun { get; private set; }
        /// <summary>False on a contract that was started with no creature (it stays dormant at its dock).</summary>
        public bool CreatureAboard { get; private set; }
        public bool HazardsPaused => m_PauseReasons != HazardPauseReason.None;
        /// <summary>Co-op client: the hazards are paused because the host paused (not because of this headset).</summary>
        public bool HostPaused => (m_PauseReasons & HazardPauseReason.HostPaused) != 0;
        /// <summary>The hazards are paused for a reason of this headset's own (headset off, tracking or focus lost, scripted).</summary>
        public bool PausedHere => (m_PauseReasons & ~HazardPauseReason.HostPaused) != 0;
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

        // ------------------------------------------------------------------ net ids

        NetIdTable m_NetIds;
        SlidingDoor[] m_NetDoors = new SlidingDoor[0];
        Cuttable[] m_NetPanels = new Cuttable[0];
        SalvageSpawnPoint[] m_NetPoints = new SalvageSpawnPoint[0];
        WebTripwire[] m_NetWires = new WebTripwire[0];

        /// <summary>
        /// Numbers for every door, cut panel, spawn point, web, wreck and salvage definition in this scene
        /// (all wrecks, not just the docked one), the same on every machine running this build. Built on
        /// first use from the arrays the Builder filled in, and rebuilt by <see cref="BuildRunParams"/> if
        /// those arrays have since gained something it does not know (never in the shipped scene, and
        /// never in a session: there the table is part of what both sides agreed on when they connected).
        /// </summary>
        public NetIdTable NetIds
        {
            get
            {
                if (m_NetIds == null) BuildNetIds();
                return m_NetIds;
            }
        }

        /// <summary>The door with this net id (index in <see cref="NetIdTable.Doors"/>), or null.</summary>
        public SlidingDoor DoorAt(int netIndex) => NetIds != null ? At(m_NetDoors, netIndex) : null;
        /// <summary>The cut panel with this net id (index in <see cref="NetIdTable.Panels"/>), or null.</summary>
        public Cuttable PanelAt(int netIndex) => NetIds != null ? At(m_NetPanels, netIndex) : null;
        /// <summary>The web with this net id (index in <see cref="NetIdTable.Wires"/>), or null.</summary>
        public WebTripwire WireAt(int netIndex) => NetIds != null ? At(m_NetWires, netIndex) : null;
        /// <summary>The spawn point with this net id (index in <see cref="NetIdTable.Points"/>), or null.</summary>
        public SalvageSpawnPoint PointAt(int netIndex) => NetIds != null ? At(m_NetPoints, netIndex) : null;
        /// <summary>This run's salvage item with this net id (index in <see cref="RunStartParams.items"/>), or null.</summary>
        public SalvageItem RunItemAt(int netIndex) => netIndex >= 0 && netIndex < m_RunItems.Count ? m_RunItems[netIndex] : null;

        static T At<T>(T[] array, int index) where T : class => index >= 0 && index < array.Length ? array[index] : null;

        void BuildNetIds()
        {
            var doors = new List<SlidingDoor>();
            var panels = new List<Cuttable>();
            var points = new List<SalvageSpawnPoint>();
            var wires = new List<WebTripwire>();
            var mapIds = new List<string>();

            AddUnique(doors, tugHatch);
            AddUnique(doors, sharedDoors);
            AddUnique(panels, sharedCuttables);
            AddUnique(wires, sharedTripwires);
            if (maps != null && maps.Length > 0)
            {
                for (int i = 0; i < maps.Length; i++)
                {
                    var map = maps[i];
                    // Keeps a wreck's number equal to its place in the array even if a slot is empty.
                    mapIds.Add(map != null && !string.IsNullOrEmpty(map.mapId) ? map.mapId : "?" + i);
                    if (map == null) continue;
                    AddUnique(doors, map.doors);
                    AddUnique(doors, map.creatureDoors);
                    AddUnique(panels, map.cuttables);
                    AddUnique(points, map.spawnPoints);
                    AddUnique(wires, map.tripwires);
                }
            }
            else
            {
                // A scene with no wreck list: whatever is wired straight into the director.
                AddUnique(doors, wreckDoors);
                AddUnique(panels, cuttables);
                AddUnique(points, spawnPoints);
            }

            var doorIds = new List<string>();
            foreach (var door in doors) doorIds.Add(door.doorId);
            var panelIds = new List<KeyValuePair<string, int>>();
            foreach (var panel in panels) panelIds.Add(new KeyValuePair<string, int>(panel.panelId, panel.TotalCuts));
            var pointIds = new List<string>();
            foreach (var point in points) pointIds.Add(point.pointId);
            var wireIds = new List<string>();
            foreach (var wire in wires) wireIds.Add(wire.wireId);
            var definitionIds = new List<string>();
            if (m_Catalog != null)
            {
                foreach (var def in m_Catalog)
                {
                    if (def != null) definitionIds.Add(def.id);
                }
            }

            var table = new NetIdTable(doorIds, panelIds, pointIds, wireIds, mapIds, definitionIds, enemies != null ? enemies.Length : 0);
            m_NetDoors = new SlidingDoor[table.Doors.Count];
            foreach (var door in doors) Place(m_NetDoors, table.Doors.IndexOf(door.doorId), door);
            m_NetPanels = new Cuttable[table.Panels.Count];
            foreach (var panel in panels) Place(m_NetPanels, table.Panels.IndexOf(panel.panelId), panel);
            m_NetPoints = new SalvageSpawnPoint[table.Points.Count];
            foreach (var point in points) Place(m_NetPoints, table.Points.IndexOf(point.pointId), point);
            m_NetWires = new WebTripwire[table.Wires.Count];
            foreach (var wire in wires) Place(m_NetWires, table.Wires.IndexOf(wire.wireId), wire);
            m_NetIds = table;
        }

        static void AddUnique<T>(List<T> list, T item) where T : UnityEngine.Object
        {
            if (item != null && !list.Contains(item)) list.Add(item);
        }

        static void AddUnique<T>(List<T> list, T[] items) where T : UnityEngine.Object
        {
            if (items == null) return;
            foreach (var item in items) AddUnique(list, item);
        }

        // Two objects with the same id is a content error (ProjectContentTests); the first one keeps the number.
        static void Place<T>(T[] array, int index, T item) where T : class
        {
            if (index >= 0 && index < array.Length && array[index] == null) array[index] = item;
        }

        // ------------------------------------------------------------------ players

        public void RegisterPlayer(PlayerContext player)
        {
            if (player == null || m_Players.Contains(player)) return;
            m_Players.Add(player);
            // Another crew member's stand-in keeps their handedness, not this headset's saved settings.
            if (!player.IsRemote) ApplyProfileTo(player);
        }

        public void UnregisterPlayer(PlayerContext player) => m_Players.Remove(player);

        /// <summary>
        /// The player wearing this headset: the first registered player that is not a remote stand-in.
        /// Offline that is the only player there is. Null before the rig has registered.
        /// </summary>
        public PlayerContext LocalPlayer
        {
            get
            {
                for (int i = 0; i < m_Players.Count; i++)
                {
                    var player = m_Players[i];
                    if (player != null && !player.IsRemote) return player;
                }
                return null;
            }
        }

        /// <summary>Who "this headset" is in the rules. <see cref="PlayerId.Local"/> offline; the session slot in co-op.</summary>
        public PlayerId LocalPlayerId
        {
            get
            {
                var local = LocalPlayer;
                if (local != null) return local.Id;
                return Net != null ? new PlayerId(Net.LocalSlot) : PlayerId.Local;
            }
        }

        /// <summary>
        /// The registered player with this id. Offline an unknown id falls back to the first player (there
        /// is only one); in a session an unknown id is nobody, so one crew member is never mistaken for another.
        /// </summary>
        public PlayerContext FindPlayer(PlayerId id)
        {
            for (int i = 0; i < m_Players.Count; i++)
            {
                if (m_Players[i] != null && m_Players[i].Id == id) return m_Players[i];
            }
            if (Net != null) return null;
            return m_Players.Count > 0 ? m_Players[0] : null;
        }

        /// <summary>Pushes saved comfort settings and handedness to the local rig(s) and saves them.</summary>
        public void ApplyAndSaveSettings()
        {
            Profile.NotifyChangedAndSave();
            foreach (var player in m_Players)
            {
                if (player != null && player.IsRemote) continue;
                ApplyProfileTo(player);
            }
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
            // Co-op stage 1: crew can meet in the tug, but a contract cannot be shared yet.
            if (ContractStartBlocked) return false;
            if (Net != null && Net.IsClient)
            {
                // Only the host starts a contract; it answers with the run parameters for everyone.
                Net.RequestStart();
                return false;
            }

            var run = BuildRunParams();
            return BeginRun(in run);
        }

        SpawnPlan m_BuiltPlan;
        uint m_BuiltPlanHash;

        /// <summary>
        /// Decides everything about the next contract without touching the scene: rolls the seed (or takes
        /// the test overrides), picks the wreck, the condition and the creature, plans the loot and the
        /// bonus item's hiding place, and reads the oxygen tank size from this headset's profile.
        /// Offline and on a host this is the first half of <see cref="StartMission"/>; a client never
        /// calls it and receives the result instead.
        /// </summary>
        public RunStartParams BuildRunParams()
        {
            int seed = SeedOverride ?? UnityEngine.Random.Range(1, int.MaxValue);
            SeedOverride = null;

            int runIndex = Session.RunIndex + 1;
            var run = new RunStartParams { seed = seed, runIndex = (ushort)runIndex };

            // The wreck that will dock, and so the loot spots that are available.
            int mapIndex = 0;
            var points = spawnPoints;
            if (maps != null && maps.Length > 0)
            {
                mapIndex = Mathf.Clamp(MapOverride ?? new System.Random(seed ^ 0x1b873593).Next(maps.Length), 0, maps.Length - 1);
                if (maps[mapIndex] != null) points = maps[mapIndex].spawnPoints ?? new SalvageSpawnPoint[0];
            }
            run.mapIndex = (byte)mapIndex;

            var infos = new List<SpawnPointInfo>();
            if (points != null)
            {
                foreach (var point in points)
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
                m_Config.quotaSafetyMargin, seed, runIndex);
            if (plan.QuotaWasLowered)
                Debug.LogWarning("[Mission] Not enough spawn points to guarantee the quota; lowered it to " + plan.Quota + ".");

            var condition = ConditionOverride ?? ContractConditions.Pick(seed);
            run.condition = (byte)condition;
            run.quota = plan.Quota;
            run.oxygenCapacitySeconds = NextRunOxygenCapacity * ContractConditions.OxygenMultiplier(condition);
            run.bonusMultiplier = ContractConditions.BonusMultiplier(condition);

            // The loot, as numbers every machine reads the same way. An item's place in this list is its net id.
            var ids = NetIds;
            if (Net == null && !NetIdsCover(ids, points))
            {
                // The table is older than the arrays it was built from (a director put together by hand,
                // or changed after its first contract). Number everything again before any number is used.
                m_NetIds = null;
                ids = NetIds;
            }
            var items = new List<RunItem>();
            var used = new HashSet<string>();
            float valueMultiplier = ContractConditions.ValueMultiplier(condition);
            foreach (var assignment in plan.Assignments)
            {
                used.Add(assignment.spawnPointId);
                int pointIndex = ids.Points.IndexOf(assignment.spawnPointId);
                int definitionIndex = ids.Definitions.IndexOf(assignment.definitionId);
                if (pointIndex < 0 || definitionIndex < 0)
                {
                    // The planner has already counted this item towards the quota, so say so loudly.
                    Debug.LogError("[Mission] Planned salvage '" + assignment.salvageId + "' was dropped: "
                        + (pointIndex < 0 ? "spawn point '" + assignment.spawnPointId + "'" : "definition '" + assignment.definitionId + "'")
                        + " has no net id.");
                    continue;
                }
                if (!m_CatalogById.TryGetValue(assignment.definitionId, out var def)) continue;
                int worth = Mathf.RoundToInt(def.creditValue * valueMultiplier);
                items.Add(new RunItem
                {
                    pointIndex = (byte)pointIndex,
                    definitionIndex = (byte)definitionIndex,
                    value = (ushort)Mathf.Clamp(worth, 0, NetIdTable.MaxItemValue),
                });
            }

            // One bonus item (the rubber duck) hidden at a free, open spawn point. It is extra: never part of the quota maths.
            SalvageDefinition bonus = null;
            if (m_Catalog != null)
            {
                foreach (var def in m_Catalog)
                {
                    if (def != null && def.bonusSpawn && def.prefab != null) bonus = def;
                }
            }
            if (bonus != null && points != null)
            {
                var free = new List<SalvageSpawnPoint>();
                foreach (var point in points)
                {
                    if (point != null && point.Gate == null && !used.Contains(point.pointId)) free.Add(point);
                }
                if (free.Count > 0)
                {
                    var spot = free[new System.Random(seed ^ 0x2545f491).Next(free.Count)];
                    int pointIndex = ids.Points.IndexOf(spot.pointId);
                    int definitionIndex = ids.Definitions.IndexOf(bonus.id);
                    if (pointIndex >= 0 && definitionIndex >= 0)
                    {
                        items.Add(new RunItem
                        {
                            pointIndex = (byte)pointIndex,
                            definitionIndex = (byte)definitionIndex,
                            value = (ushort)Mathf.Clamp(bonus.creditValue, 0, NetIdTable.MaxItemValue),
                            flags = RunItem.FlagBonus,
                        });
                    }
                    else
                    {
                        Debug.LogError("[Mission] The bonus item was dropped: "
                            + (pointIndex < 0 ? "spawn point '" + spot.pointId + "'" : "definition '" + bonus.id + "'") + " has no net id.");
                    }
                }
            }
            run.items = items.ToArray();

            // A different thing is waiting in the wreck each time.
            run.creatureIndex = RunStartParams.NoCreature;
            if (enemies != null && enemies.Length > 0) run.creatureIndex = (byte)PickCreature(seed);

            // Which webs are strung. Decided here once, so no other machine has to derive it.
            uint wireMask = 0;
            for (int i = 0; i < m_NetWires.Length && i < NetIdTable.MaxWires; i++)
            {
                if (m_NetWires[i] != null && m_NetWires[i].WouldArm(seed, condition)) wireMask |= 1u << i;
            }
            run.wireArmedMask = wireMask;
            run.hazardFlags = RunStartParams.HazardBlackouts;
            ApplyCoopRunLimits(ref run);

            run.planHash = run.ComputePlanHash();
            m_BuiltPlan = plan;
            m_BuiltPlanHash = run.planHash;
            return run;
        }

        /// <summary>Whether the table has a number for every loot spot of the wreck about to dock and every catalog entry.</summary>
        bool NetIdsCover(NetIdTable ids, SalvageSpawnPoint[] points)
        {
            if (points != null)
            {
                foreach (var point in points)
                {
                    if (point != null && !string.IsNullOrEmpty(point.pointId) && !ids.Points.Contains(point.pointId)) return false;
                }
            }
            if (m_Catalog != null)
            {
                foreach (var def in m_Catalog)
                {
                    if (def != null && def.prefab != null && !string.IsNullOrEmpty(def.id) && !ids.Definitions.Contains(def.id)) return false;
                }
            }
            return true;
        }

        int PickCreature(int seed)
        {
            if (CreatureOverride.HasValue) return Mathf.Clamp(CreatureOverride.Value, 0, enemies.Length - 1);
            var rng = new System.Random(seed ^ 0x5f3759df);
            if (Net == null) return rng.Next(enemies.Length);

            // In a session the Mimic stays ashore: it plays back one player's own microphone, and no
            // microphone audio ever leaves a headset.
            var candidates = new List<int>();
            for (int i = 0; i < enemies.Length; i++)
            {
                if (enemies[i] != null && enemies[i].GetComponent<VoiceMimic>() == null) candidates.Add(i);
            }
            return candidates.Count > 0 ? candidates[rng.Next(candidates.Count)] : RunStartParams.NoCreature;
        }

        /// <summary>
        /// Starts the contract these parameters describe. Every machine runs exactly this: offline and on a
        /// host straight after <see cref="BuildRunParams"/>, on a client when the host's parameters arrive.
        /// Nothing in here rolls a die. The caller is expected to have checked <see cref="RunStartParams.planHash"/>.
        /// </summary>
        public bool BeginRun(in RunStartParams run)
        {
            if (Session.Phase == MissionPhase.Active) return false;

            // Clear the last wreck out completely before another one docks.
            ClearRunObjects();
            if (maps != null && maps.Length > 0) SelectMap(run.mapIndex);

            ClearRunObjects();
            Condition = (ContractCondition)run.condition;
            if (!Session.Begin(run.seed, run.quota, run.oxygenCapacitySeconds, run.runIndex)) return false;
            Session.BonusMultiplier = run.bonusMultiplier;
            CurrentRun = run;
            // The loot plan as the planner produced it, when this machine planned the run; otherwise rebuilt from the item list.
            LastPlan = m_BuiltPlan != null && m_BuiltPlanHash == run.planHash ? m_BuiltPlan : PlanFromParams(in run);
            m_BuiltPlan = null;

            Profile.Data.tutorialCompleted = true;
            Profile.RecordRunStarted();

            SpawnFromParams(in run);

            if (locker != null) locker.ResetForRun();
            if (wreckDoors != null)
            {
                foreach (var door in wreckDoors)
                {
                    if (door != null) door.ResetForRun();
                }
            }
            if (noise != null) noise.Muted = false;
            CreatureAboard = false;
            if (enemies != null && enemies.Length > 0)
            {
                if (run.creatureIndex != RunStartParams.NoCreature)
                {
                    SelectCreature(run.creatureIndex);
                    if (ActiveEnemy != null)
                    {
                        ActiveEnemy.BeginRun(run.seed);
                        CreatureAboard = true;
                    }
                }
                else if (ActiveEnemy != null)
                {
                    // Nothing aboard this time: whichever creature is selected stays asleep at the new wreck's dock.
                    ActiveEnemy.ParkAtDock();
                }
            }
            if (tugHatch != null) tugHatch.SetOpen(true);

            MissionStarted?.Invoke();
            return true;
        }

        void SpawnFromParams(in RunStartParams run)
        {
            var pointsById = new Dictionary<string, SalvageSpawnPoint>();
            if (spawnPoints != null)
            {
                foreach (var point in spawnPoints)
                {
                    if (point != null) pointsById[point.pointId] = point;
                }
            }

            var ids = NetIds;
            int count = run.items != null ? run.items.Length : 0;
            for (int i = 0; i < count; i++)
            {
                // The slot exists even if the item cannot be made here, so net ids never shift.
                m_RunItems.Add(null);
                var entry = run.items[i];
                string pointId = ids.Points[entry.pointIndex];
                string definitionId = ids.Definitions[entry.definitionIndex];
                if (pointId == null || definitionId == null) continue;
                if (!pointsById.TryGetValue(pointId, out var point)) continue;
                if (!m_CatalogById.TryGetValue(definitionId, out var def) || def.prefab == null) continue;

                var go = Instantiate(def.prefab, point.transform.position, point.transform.rotation, salvageRoot);
                var item = go.GetComponent<SalvageItem>();
                if (item == null)
                {
                    Destroy(go);
                    continue;
                }

                // The hidden bonus item (the rubber duck) floats free. What anything is worth is what the
                // parameters say (for the duck that is its list price), never this build's own catalog.
                bool isBonus = (entry.flags & RunItem.FlagBonus) != 0;
                string id = ids.SalvageIdFor(Session.RunIndex, entry);
                item.Initialize(this, id, def, isBonus ? null : point.mount);
                item.SetBaseValue(entry.value);
                item.NetIndex = i;
                Session.RegisterSalvage(id, def.id, entry.value);
                m_Items.Add(item);
                m_RunItems[i] = item;
            }
        }

        /// <summary>A loot plan equivalent to the item list, for a machine that did not plan the run itself.</summary>
        SpawnPlan PlanFromParams(in RunStartParams run)
        {
            var plan = new SpawnPlan { Quota = run.quota };
            var gated = new HashSet<string>();
            if (spawnPoints != null)
            {
                foreach (var point in spawnPoints)
                {
                    if (point != null && point.Gate != null) gated.Add(point.pointId);
                }
            }
            var ids = NetIds;
            int count = run.items != null ? run.items.Length : 0;
            for (int i = 0; i < count; i++)
            {
                var entry = run.items[i];
                if ((entry.flags & RunItem.FlagBonus) != 0) continue;
                string pointId = ids.Points[entry.pointIndex];
                string definitionId = ids.Definitions[entry.definitionIndex];
                if (pointId == null || definitionId == null || !m_CatalogById.TryGetValue(definitionId, out var def)) continue;
                plan.Assignments.Add(new SpawnAssignment
                {
                    salvageId = ids.SalvageIdFor(run.runIndex, entry),
                    spawnPointId = pointId,
                    definitionId = definitionId,
                    value = def.creditValue,
                    category = def.category,
                });
                plan.TotalValue += def.creditValue;
                if (!gated.Contains(pointId)) plan.UngatedValue += def.creditValue;
            }
            return plan;
        }

        void ClearRunObjects()
        {
            foreach (var item in m_Items)
            {
                if (item != null) Destroy(item.gameObject);
            }
            m_Items.Clear();
            m_RunItems.Clear();

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

        /// <summary>
        /// Manual extraction. Nobody is left behind: it only works while every crew member who is present
        /// is inside the tug. With one player that is simply "the player is inside the tug".
        /// </summary>
        public MissionResult Extract(PlayerId by)
        {
            if (Session.Phase != MissionPhase.Active) return null;
            if (Net != null && Net.IsClient)
            {
                Net.RequestExtract();
                return null;
            }
            int missing = MissingAboardMask();
            if (missing != 0)
            {
                // In a session the host's own board names who is missing, like a client's does.
                if (Net != null) ApplyExtractRefused(missing);
                return null;
            }

            var result = Session.Extract(by);
            if (result != null) FinishRun(result);
            return result;
        }

        /// <summary>
        /// Who is keeping the tug from leaving: one bit per player (bit = PlayerId.Value) whose head is
        /// outside the tug. Players who are away (headset off, link silent) are not waited for. 0 = all aboard.
        /// </summary>
        public int MissingAboardMask()
        {
            if (safeZone == null) return 0;
            int mask = 0;
            for (int i = 0; i < m_Players.Count; i++)
            {
                var player = m_Players[i];
                if (player == null || player.IsAway) continue;
                if (!safeZone.Contains(player.HeadPosition)) mask |= 1 << Mathf.Clamp(player.Id.Value, 0, 30);
            }
            return mask;
        }

        /// <summary>Fails the run (oxygen ran out or the drone caught someone).</summary>
        public MissionResult FailMission(FailureReason reason, PlayerId victim)
        {
            // Only the host ends a shared contract; a client hears about it.
            if (Net != null && Net.IsClient) return null;
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
                    player.locomotion.FadeTeleport(player.fader, HubSpotFor(player), hubSpawn.rotation, CloseHatch);
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

        // ------------------------------------------------------------------ co-op session lifecycle

        /// <summary>
        /// Where each crew slot stands in the tug, in the hub spawn's own space (+z is the way it faces, toward the
        /// mission board; +x is to its right). Slot 0 is the spawn itself, so single-player is unchanged. The others
        /// stand 0.8 m to either side and one step forward, all inside the tug and clear of the locker, the boards
        /// and the CO-OP CREW panel (checked by NetContentTests).
        /// </summary>
        static readonly Vector3[] k_CrewSpots =
        {
            Vector3.zero,
            new Vector3(0.8f, 0f, 0f),
            new Vector3(-0.8f, 0f, 0f),
            new Vector3(0.4f, 0f, 0.8f),
        };

        /// <summary>The floor point where crew slot <paramref name="slot"/> is put in the tug (the hub spawn for slot 0).</summary>
        public Vector3 CrewSpot(int slot)
        {
            if (hubSpawn == null) return Vector3.zero;
            return hubSpawn.TransformPoint(k_CrewSpots[Mathf.Clamp(slot, 0, k_CrewSpots.Length - 1)]);
        }

        /// <summary>Single-player: the hub spawn. In a session: this player's own crew spot, so nobody stands inside anybody.</summary>
        Vector3 HubSpotFor(PlayerContext player) => Net != null && player != null ? CrewSpot(player.Id.Value) : hubSpawn.position;

        /// <summary>Puts the local rig on its crew spot in the tug, facing the mission board (a head-accurate teleport).</summary>
        public bool PlaceAtCrewSpot(PlayerContext player)
        {
            if (player == null || player.locomotion == null || hubSpawn == null) return false;
            return player.locomotion.TeleportTo(CrewSpot(player.Id.Value), hubSpawn.rotation);
        }

        /// <summary>
        /// Puts this headset in the state a co-op session starts from. Called by NetSession when HOST CO-OP
        /// succeeds (<paramref name="asHost"/> true) and when a joiner's Welcome arrives. Only allowed from the
        /// hub or the results screen (a session is never opened mid-contract): a results screen goes back to
        /// the hub; a joiner also clears its own leftovers (the practice scrap, used panels, the locker) so it
        /// starts from the same empty tug as the host. The tutorial pauses itself while <see cref="Net"/> is set.
        /// </summary>
        public bool ResetForSession(bool asHost)
        {
            if (Session.Phase == MissionPhase.Active) return false;
            if (Session.Phase == MissionPhase.Results)
            {
                ReturnToHub();
            }
            else if (!asHost)
            {
                ClearRunObjects();
                if (locker != null) locker.ResetForRun();
            }
            return true;
        }

        /// <summary>
        /// Back to single-player: called by NetSession after LEAVE, a refused or lost link, or the host closing.
        /// A shared contract still running is abandoned with nothing banked, on the host as on a client (the
        /// ride-along contract has no creature, so it is not handed over as a solo run), the local rig takes
        /// slot 0 and its own colour again, every Away reason and the host's pause and silence are forgotten,
        /// and <see cref="Net"/> is cleared. The session has already destroyed the other crew members' stand-ins.
        /// </summary>
        public void LeaveSession()
        {
            if (Session.Phase == MissionPhase.Active) AbortRun();
            Net = null;
            ContractStartBlocked = false;
            RestoreRunItemsLocal();
            MoveAwayToPauses();
            ApplyHostSilent(false);
            SetHazardPause(HazardPauseReason.HostPaused, false);
            var local = LocalPlayer;
            if (local != null)
            {
                local.AssignId(PlayerId.Local, false);
                if (local.avatar != null) local.avatar.SetTint(PlayerAvatar.CrewColors[0]);
            }
        }

        // AbortRun() lives in MissionDirector.Coop.cs (one definition for link loss and LEAVE).

        // ------------------------------------------------------------------ shared-state changes

        public bool NotifySalvageCollected(SalvageItem item, PlayerId by)
        {
            if (Net != null && Net.IsClient) return false; // the host records it when it grants the grab
            return item != null && Session.TryCollect(item.SalvageId, by);
        }

        /// <summary>Raised when a knock lowered what an item is worth (item, new value). Not raised on a client.</summary>
        public event Action<SalvageItem, int> SalvageDamaged;

        /// <summary>
        /// An item took a knock on this machine and is now worth <paramref name="newValue"/>. Offline and
        /// on a host the ledger follows at once; a client only reports it and the host decides.
        /// Returns true if the ledger changed here.
        /// </summary>
        public bool ReportSalvageDamage(SalvageItem item, int newValue)
        {
            if (item == null) return false;
            if (Net != null && Net.IsClient)
            {
                // With shared grabbing the item sync tells the host (ItemDamageReport); the ledger follows ItemValue.
                if (ItemClient != null) ItemClient.ReportDamage(item, newValue);
                else Net.ReportSalvageDamage(item, newValue);
                return false;
            }
            if (!Session.DamageSalvage(item.SalvageId, newValue)) return false;
            SalvageDamaged?.Invoke(item, newValue);
            return true;
        }

        /// <summary>Records a deposit. The ledger accepts each salvage id exactly once.</summary>
        public DepositResult DepositSalvage(SalvageItem item, PlayerId by)
        {
            if (item == null) return DepositResult.UnknownItem;
            if (Net != null && Net.IsClient) return DepositResult.UnknownItem; // only the host's locker decides
            var result = Session.TryDeposit(item.SalvageId, by);
            if (result == DepositResult.Accepted) SalvageDeposited?.Invoke(item);
            return result;
        }

        /// <summary>Pays the trick-shot bonus for an item that was just deposited. Returns the bonus credits.</summary>
        public int AwardTrickShot(SalvageItem item)
        {
            if (item == null) return 0;
            if (Net != null && Net.IsClient) return 0;
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

            // A shared panel opens when the host says so.
            if (Net != null && Net.IsClient) return false;
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
                    if (offset.magnitude > 0.3f) player.locomotion.TeleportTo(HubSpotFor(player), hubSpawn.rotation);
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
                    player.locomotion.TeleportTo(HubSpotFor(player), hubSpawn.rotation);
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
                // Someone who is away (headset off, link silent) does not use the shared tank.
                if (!player.IsAway) everyoneSafe &= player.InSafeZone;
            }

            // One tank, and in a session it is the host's: a client is told the time and the level.
            if (Session.Phase == MissionPhase.Active && !HazardsPaused && !(Net != null && Net.IsClient))
            {
                // With no registered player (tests) oxygen is controlled explicitly via TickMission.
                // Working hard (swimming, thrusting) uses oxygen faster.
                if (m_Players.Count > 0)
                {
                    float exertion = 0f;
                    foreach (var player in m_Players)
                    {
                        if (player == null || player.IsAway) continue;
                        if (player.IsRemote)
                            exertion = Mathf.Max(exertion, player.RemoteExertion);
                        else if (player.locomotion != null && player.locomotion.zeroG != null)
                            exertion = Mathf.Max(exertion, player.locomotion.zeroG.Exertion);
                    }
                    TickMission(Time.deltaTime * (1f + m_Config.exertionOxygenCost * exertion), everyoneSafe);
                }
            }
            if (Net != null && Net.IsClient) ExtrapolateClientClock(everyoneSafe, Time.deltaTime);

            if (Time.time >= m_NextBoundsCheck)
            {
                m_NextBoundsCheck = Time.time + 0.5f;
                RecoverLostObjects();
            }
        }

        /// <summary>Advances mission time. Public so tests can fast-forward oxygen.</summary>
        public MissionResult TickMission(float deltaSeconds, bool playersInSafeZone)
        {
            if (Net != null && Net.IsClient) return null;
            var result = Session.Tick(deltaSeconds, playersInSafeZone);
            if (result != null) FinishRun(result);
            return result;
        }

        void RecoverLostObjects()
        {
            if (bounds == null) return;

            // Lost salvage is brought back by whoever owns the items: the host in a session.
            if (!(Net != null && Net.IsClient))
            {
                foreach (var item in m_Items)
                {
                    // Not one a crew member is holding or flying either: that copy only follows their stream.
                    if (item == null || item.IsSecured || item.IsHeld || item.IsMounted || !item.SimulatedHere) continue;
                    if (bounds.Contains(item.transform.position)) continue;
                    Vector3 home = item.HasBeenCollected && lostAndFound != null ? lostAndFound.position : item.SpawnPosition;
                    item.RecoverTo(home);
                }
            }

            foreach (var player in m_Players)
            {
                if (player == null || player.locomotion == null) continue;
                if (!bounds.Contains(player.HeadPosition)) player.locomotion.Unstuck();
            }
        }
    }
}

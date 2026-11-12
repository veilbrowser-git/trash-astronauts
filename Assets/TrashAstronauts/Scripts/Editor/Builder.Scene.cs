using System.Collections.Generic;
using System.Linq;
using TMPro;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.UI;
using TrashAstronauts.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// Builds the one mission scene: the salvage tug docked to the derelict hauler "Margin Call".
    ///
    /// Top-down layout (metres, +Z is "into the wreck"):
    ///   Tug            x[-2,2]      z[-3,2]
    ///   Docking collar x[-0.9,0.9]  z[2,3.6]
    ///   Airlock        x[-2,2]      z[3.6,7.6]
    ///   Corridor       x[-1.3,1.3]  z[7.6,21.6]   (spine)
    ///   Cargo bay      x[-10.3,-1.3] z[9.6,18.6]  (west)
    ///   Maintenance    x[1.3,8.3]   z[8.6,15.6]   (east)
    ///   Reactor        x[-4.5,4.5]  z[21.6,29.6]  (north end)
    ///   Service duct   maintenance -> reactor east wall (the loop / shortcut, sealed by a cuttable panel)
    /// </summary>
    public static partial class Builder
    {
        const float H = 2.8f;       // ceiling height
        const float T = 0.2f;       // wall thickness
        const float RailY = 1.05f;

        struct Gap
        {
            public float at, width, height;
            public Gap(float at, float width, float height) { this.at = at; this.width = width; this.height = height; }
        }

        static readonly List<SalvageSpawnPoint> s_SpawnPoints = new List<SalvageSpawnPoint>();
        static readonly List<Cuttable> s_Cuttables = new List<Cuttable>();
        static readonly Dictionary<string, EnemyWaypoint> s_Waypoints = new Dictionary<string, EnemyWaypoint>();
        static Transform s_Level, s_Rails, s_Teleport, s_Spawns, s_Props, s_Lights, s_Signs;
        static readonly List<Light> s_WreckLights = new List<Light>();
        static readonly List<Renderer> s_WreckGlow = new List<Renderer>();
        /// <summary>Webs strung in the wreck being built; each wreck stores its own (WreckMap.tripwires).</summary>
        static readonly List<WebTripwire> s_Tripwires = new List<WebTripwire>();

        static void BuildScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            s_SpawnPoints.Clear();
            s_Cuttables.Clear();
            s_Tripwires.Clear();
            s_Waypoints.Clear();
            s_BoxMeshes.Clear();
            s_WreckLights.Clear();
            s_WreckGlow.Clear();
            s_Loose = null;
            s_SpiderlingMesh = null;

            // Lighting: dim flat ambient so routes stay readable with the flashlight off, dark fog for mood.
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.095f, 0.11f, 0.145f); // darker than before: the flashlight matters now
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.015f, 0.02f, 0.03f);
            RenderSettings.fogDensity = 0.04f;
            RenderSettings.skybox = null;

            s_Level = new GameObject("Level").transform;
            s_Rails = Empty(s_Level, "Handrails", Vector3.zero).transform;
            s_Teleport = Empty(s_Level, "Teleport Surfaces", Vector3.zero).transform;
            s_Spawns = Empty(s_Level, "Salvage Spawn Points", Vector3.zero).transform;
            s_Props = Empty(s_Level, "Props", Vector3.zero).transform;
            s_Lights = Empty(s_Level, "Lights", Vector3.zero).transform;
            s_Signs = Empty(s_Level, "Signs", Vector3.zero).transform;

            var managerGo = new GameObject("XR Interaction Manager");
            managerGo.AddComponent<XRInteractionManager>();

            BuildShell();
            var hatch = BuildDoor("Tug Hatch", "door_tug_hatch", new Vector3(0, 0, 2f), 1.4f, 2.1f, true, true, false);
            var doorA = BuildDoor("Airlock Inner Door", "door_airlock", new Vector3(0, 0, 7.6f), 1.4f, 2.1f, false, true, true);
            var doorR = BuildDoor("Reactor Door", "door_reactor", new Vector3(0, 0, 21.6f), 1.6f, 2.1f, false, false, true);
            BuildRails();
            BuildTeleportAndAnchors();
            var reactorPanel = BuildCompartments(out var cagePanel, out var ductPanel, out var rackMount, out var practicePanel);
            BuildSpawnPoints(reactorPanel, cagePanel, rackMount);
            BuildDecor();
            BuildMapDetail();
            BuildWaypoints();

            // --- mission objects
            var missionGo = new GameObject("Mission");
            var noise = missionGo.AddComponent<NoiseSystem>();
            var director = missionGo.AddComponent<MissionDirector>();
            var pause = missionGo.AddComponent<HazardPauseMonitor>();
            pause.director = director;
            pause.headIsTracked = Ref("Head/IsTracked");
            var hotkeys = missionGo.AddComponent<DevHotkeys>();
            hotkeys.director = director;

            var safe = Empty(missionGo.transform, "Safe Zone (Tug)", new Vector3(0, 1.4f, -2.25f)).AddComponent<SafeZone>();
            safe.size = new Vector3(4.2f, 3.2f, 8.7f);
            var supplyZone = Empty(missionGo.transform, "Safe Zone (Supply Bay)", new Vector3(-4f, 1.4f, -4.8f)).AddComponent<BoxZone>();
            supplyZone.size = new Vector3(4.2f, 3.2f, 3.8f);
            safe.annexes = new[] { supplyZone };
            // Big enough for the largest wreck (they all dock at the same airlock) and the tug's flight deck.
            var bounds = Empty(missionGo.transform, "Playable Bounds", new Vector3(-1.5f, 1.5f, 17f)).AddComponent<PlayableBounds>();
            bounds.size = new Vector3(30f, 6f, 50f);
            Exclusion(missionGo.transform, "No Drone: Airlock", new Vector3(0, 1.4f, 4.8f), new Vector3(4.2f, 3.2f, 5.7f));
            Exclusion(missionGo.transform, "No Drone: Duct A", new Vector3(6.8f, 1.2f, 20.1f), new Vector3(1.3f, 2.6f, 9.1f));
            Exclusion(missionGo.transform, "No Drone: Duct B", new Vector3(5.4f, 1.2f, 24f), new Vector3(1.9f, 2.6f, 1.3f));

            var hubSpawn = Empty(missionGo.transform, "Hub Spawn", new Vector3(0.4f, 0, -0.9f), Quaternion.Euler(0, -90f, 0));
            var lostAndFound = Empty(missionGo.transform, "Lost And Found", new Vector3(-1.2f, 1.2f, 5.6f));
            var salvageRoot = Empty(missionGo.transform, "Salvage (runtime)", Vector3.zero);

            var locker = BuildLocker(director);
            var board = BuildMissionBoard(director);
            var tutorialText = BuildTutorialBoard(out var skipStep, out var skipAll);
            var settings = BuildSettingsPanel(s_Props, "Suit Settings Console", new Vector3(1.0f, 1.35f, -2.88f), Facing(Vector3.forward), 1.6f);
            settings.director = director;
            var shop = BuildShop(director);

            var blackout = missionGo.AddComponent<BlackoutController>();
            blackout.director = director;
            var scares = missionGo.AddComponent<ScareDirector>();
            scares.director = director;
            scares.apparition = BuildApparition(missionGo.transform);
            // A hand-sized spider on a thread, for the drop scare.
            var dropRoot = Empty(missionGo.transform, "Drop Spider", Vector3.zero, null, GameLayers.Default);
            var dropBody = Empty(dropRoot.transform, "Body", Vector3.zero, Quaternion.Euler(80f, 0f, 0f)).transform;
            LoadSpider("mimic", dropBody);
            dropBody.localScale = Vector3.one * 0.16f;
            dropRoot.SetActive(false);
            scares.dropSpider = dropRoot;
            scares.dropThread = MakeLine(missionGo.transform, "Drop Thread", 0.003f);
            scares.dropThread.startColor = scares.dropThread.endColor = new Color(0.85f, 0.87f, 0.9f);

            // --- tutorial practice item
            var practiceSpawn = Empty(missionGo.transform, "Practice Scrap Spawn", new Vector3(0.9f, 1.15f, 0.4f));
            var practice = (GameObject)PrefabUtility.InstantiatePrefab(s_TrainingScrapPrefab, salvageRoot.transform);
            practice.transform.position = practiceSpawn.transform.position;

            // --- drone
            // All three are placed at the dock; the director switches on one of them per contract.
            var dock = Empty(missionGo.transform, "Drone Dock", new Vector3(3.4f, 1.45f, 28.4f), Quaternion.Euler(0, 200f, 0));
            var creatures = new List<EnemyDrone>();
            for (int i = 0; i < s_CreaturePrefabs.Length; i++)
            {
                var droneGo = (GameObject)PrefabUtility.InstantiatePrefab(s_CreaturePrefabs[i]);
                droneGo.transform.SetPositionAndRotation(dock.transform.position, dock.transform.rotation);
                var creature = droneGo.GetComponent<EnemyDrone>();
                creature.director = director;
                creature.dock = dock.transform;
                creature.graph = s_Waypoints.Values.ToArray();
                creature.patrolRoute = Route("R3", "R4", "R0", "C4", "C3", "C2", "G0", "G1", "G4", "G3", "G4", "G2", "G4", "G1", "G0", "C2", "C1",
                    "M0", "M1", "M2", "M1", "M0", "C1", "C0", "C1", "C2", "C3", "C4", "R0", "R1", "R2");
                creature.doors = new[] { doorR };
                droneGo.SetActive(i == 0);
                creatures.Add(creature);
            }

            // --- player
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(s_RigPrefab);
            rig.transform.SetPositionAndRotation(hubSpawn.transform.position, hubSpawn.transform.rotation);

            // --- tutorial
            var tutorial = missionGo.AddComponent<TutorialController>();
            tutorial.director = director;
            tutorial.locker = locker;
            tutorial.practicePanel = practicePanel;
            tutorial.boardText = tutorialText;
            tutorial.skipStepButton = skipStep;
            tutorial.skipAllButton = skipAll;
            tutorial.prompt = rig.GetComponentInChildren<TutorialPrompt>(true);
            tutorial.practiceScrapPrefab = s_TrainingScrapPrefab;
            tutorial.practiceScrapSpawn = practiceSpawn.transform;

            // --- wire the director
            var so = new SerializedObject(director);
            so.FindProperty("m_Config").objectReferenceValue = s_Config;
            so.FindProperty("m_AudioLibrary").objectReferenceValue = s_Audio;
            var catalog = so.FindProperty("m_Catalog");
            var defs = s_Defs.Values.ToList();
            catalog.arraySize = defs.Count;
            for (int i = 0; i < defs.Count; i++) catalog.GetArrayElementAtIndex(i).objectReferenceValue = defs[i];
            so.ApplyModifiedPropertiesWithoutUndo();

            director.noise = noise;
            director.safeZone = safe;
            director.bounds = bounds;
            director.locker = locker;
            director.tugHatch = hatch;
            director.hubSpawn = hubSpawn.transform;
            director.lostAndFound = lostAndFound.transform;
            director.salvageRoot = salvageRoot.transform;
            director.spawnPoints = s_SpawnPoints.ToArray();
            director.cuttables = s_Cuttables.ToArray();
            director.enemies = creatures.ToArray();
            director.wreckDoors = new[] { doorA, doorR };

            // --- ambience
            var ambience = new GameObject("Ambience").AddComponent<AudioSource>();
            ambience.clip = s_Audio != null ? s_Audio.shipAmbience : null;
            ambience.loop = true;
            ambience.playOnAwake = true;
            ambience.spatialBlend = 0f;
            ambience.volume = 0.28f; // the re-synthesized ambience has more mid-range than the old one
            var tugHum = Empty(ambience.transform, "Tug Hum", new Vector3(0, 2.2f, -1.5f)).AddComponent<AudioSource>();
            ConfigureSpatial(tugHum, s_Audio != null ? s_Audio.tugAmbience : null, true, 0.5f, 1f, 9f);
            tugHum.playOnAwake = true;
            var reactorHum = Empty(ambience.transform, "Reactor Hum", new Vector3(0, 1.4f, 25.9f)).AddComponent<AudioSource>();
            ConfigureSpatial(reactorHum, s_Audio != null ? s_Audio.droneHum : null, true, 0.3f, 1f, 12f);
            reactorHum.pitch = 0.5f;
            reactorHum.playOnAwake = true;

            // --- wrecks: the freighter is stretched 1.5x; two more are built at full size. One docks per contract.
            var sharedLights = s_WreckLights.Where(l => l.transform.position.z < 7.75f).ToArray();
            var sharedGlow = s_WreckGlow.Where(r => r.transform.position.z < 7.75f).ToArray();
            var freighter = FinishFreighter(missionGo.transform, dock.transform, doorR, practicePanel, reactorHum);
            var barge = BuildOreBarge(sharedLights, sharedGlow);
            var survey = BuildSurveyShip(sharedLights, sharedGlow);
            foreach (var creature in creatures) creature.transform.SetPositionAndRotation(dock.transform.position, dock.transform.rotation);
            director.maps = new[] { freighter, barge, survey };
            director.sharedCuttables = new[] { practicePanel };
            director.sharedDoors = new[] { doorA };
            // Every web belongs to a wreck today; one strung in the tug or the airlock would go here.
            director.sharedTripwires = new WebTripwire[0];
            director.blackout = blackout;

            blackout.lights = freighter.lights;
            blackout.glowStrips = freighter.glowStrips;
            blackout.globalAmbience = new[] { ambience };
            blackout.ambience = new[] { ambience, reactorHum };

            // The fingerprint co-op peers compare before they agree to play together (version 0 = content only).
            var netIds = director.NetIds;
            foreach (var problem in netIds.Validate()) Debug.LogError("[Builder] Net ids: " + problem);
            Debug.Log("[Builder] Net content hash 0x" + netIds.ContentHash(0).ToString("x8") + " (" + netIds.Doors.Count + " doors, "
                + netIds.Panels.Count + " panels, " + netIds.Points.Count + " spawn points, " + netIds.Wires.Count + " webs, "
                + netIds.Maps.Count + " wrecks, " + netIds.Definitions.Count + " salvage definitions, " + netIds.CreatureCount + " creatures)");

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        static EnemyWaypoint[] Route(params string[] ids) => ids.Select(id => s_Waypoints[id]).ToArray();

        static void Exclusion(Transform parent, string name, Vector3 center, Vector3 size)
        {
            Empty(parent, name, center).AddComponent<EnemyExclusionZone>().size = size;
        }

        // ------------------------------------------------------------------ walls, floors, ceilings

        static void Wall(string name, Vector2 a, Vector2 b, float height, Material mat, params Gap[] gaps)
        {
            Vector2 d = b - a;
            float length = d.magnitude;
            Vector2 dir = d / length;
            bool alongX = Mathf.Abs(dir.x) > 0.5f;
            var parent = Empty(s_Level, name, Vector3.zero, null, GameLayers.Environment).transform;

            void Segment(float s0, float s1, float y0, float y1)
            {
                if (s1 - s0 < 0.01f || y1 - y0 < 0.01f) return;
                Vector2 mid = a + dir * ((s0 + s1) * 0.5f);
                float along = s1 - s0;
                Box(parent, "Segment", new Vector3(mid.x, (y0 + y1) * 0.5f, mid.y),
                    alongX ? new Vector3(along, y1 - y0, T) : new Vector3(T, y1 - y0, along), mat);

                // Trim: a dark kick-plate along the floor and a band under the ceiling, both sides at once.
                const float proud = 0.035f;
                if (y0 <= 0.001f)
                    Box(parent, "Skirting", new Vector3(mid.x, 0.07f, mid.y),
                        alongX ? new Vector3(along - 0.02f, 0.14f, T + proud) : new Vector3(T + proud, 0.14f, along - 0.02f), M("Trim"), GameLayers.Default, false);
                if (y1 >= height - 0.001f)
                    Box(parent, "Cornice", new Vector3(mid.x, height - 0.05f, mid.y),
                        alongX ? new Vector3(along - 0.02f, 0.1f, T + proud) : new Vector3(T + proud, 0.1f, along - 0.02f), M("Trim"), GameLayers.Default, false);
            }

            const float ext = T * 0.5f - 0.01f;
            float cursor = -ext;
            foreach (var gap in gaps.OrderBy(g => g.at))
            {
                float s = gap.at - gap.width * 0.5f, e = gap.at + gap.width * 0.5f;
                Segment(cursor, s, 0f, height);
                Segment(s, e, gap.height, height);
                cursor = e;
            }
            Segment(cursor, length + ext, 0f, height);
        }

        static void FloorAndCeiling(string name, float x0, float x1, float z0, float z1, float height, Material floorMat)
        {
            var parent = Empty(s_Level, name, Vector3.zero, null, GameLayers.Environment).transform;
            Vector3 size = new Vector3(x1 - x0, 0.2f, z1 - z0);
            Box(parent, "Floor", new Vector3((x0 + x1) * 0.5f, -0.1f, (z0 + z1) * 0.5f), size, floorMat);
            Box(parent, "Ceiling", new Vector3((x0 + x1) * 0.5f, height + 0.1f, (z0 + z1) * 0.5f), size, M("Ceiling"));
        }

        static void BuildShell()
        {
            var wall = M("Wall");
            var tug = M("WallTug");

            // Tug
            FloorAndCeiling("Tug Slabs", -2, 2, -3, 2, H, M("Floor"));
            Wall("Tug S", new Vector2(-2, -3), new Vector2(2, -3), H, tug, new Gap(1.15f, 1.9f, 2.2f)); // through to the flight deck
            Wall("Tug W", new Vector2(-2, -3), new Vector2(-2, 2), H, tug);
            Wall("Tug E", new Vector2(2, -3), new Vector2(2, 2), H, tug);
            Wall("Tug N", new Vector2(-2, 2), new Vector2(2, 2), H, tug, new Gap(2f, 1.4f, 2.1f));

            // Docking collar
            FloorAndCeiling("Collar Slabs", -0.9f, 0.9f, 2, 3.6f, 2.4f, M("Floor"));
            Wall("Collar W", new Vector2(-0.9f, 2), new Vector2(-0.9f, 3.6f), 2.4f, M("Trim"));
            Wall("Collar E", new Vector2(0.9f, 2), new Vector2(0.9f, 3.6f), 2.4f, M("Trim"));

            // Airlock
            FloorAndCeiling("Airlock Slabs", -2, 2, 3.6f, 7.6f, H, M("Floor"));
            Wall("Airlock S", new Vector2(-2, 3.6f), new Vector2(2, 3.6f), H, wall, new Gap(2f, 1.4f, 2.1f));
            Wall("Airlock W", new Vector2(-2, 3.6f), new Vector2(-2, 7.6f), H, wall);
            Wall("Airlock E", new Vector2(2, 3.6f), new Vector2(2, 7.6f), H, wall);
            Wall("Airlock N", new Vector2(-2, 7.6f), new Vector2(2, 7.6f), H, wall, new Gap(2f, 1.4f, 2.1f));

            // Corridor (spine)
            FloorAndCeiling("Corridor Slabs", -1.3f, 1.3f, 7.6f, 21.6f, H, M("Floor"));
            Wall("Corridor W", new Vector2(-1.3f, 7.6f), new Vector2(-1.3f, 21.6f), H, wall, new Gap(4.4f, 1.8f, 2.2f));  // cargo door at z=12
            Wall("Corridor E", new Vector2(1.3f, 7.6f), new Vector2(1.3f, 21.6f), H, wall, new Gap(2.9f, 1.6f, 2.2f));    // maintenance door at z=10.5

            // Cargo bay
            FloorAndCeiling("Cargo Slabs", -10.3f, -1.3f, 9.6f, 18.6f, H, M("Floor"));
            Wall("Cargo S", new Vector2(-10.3f, 9.6f), new Vector2(-1.3f, 9.6f), H, wall);
            Wall("Cargo N", new Vector2(-10.3f, 18.6f), new Vector2(-1.3f, 18.6f), H, wall);
            Wall("Cargo W", new Vector2(-10.3f, 9.6f), new Vector2(-10.3f, 18.6f), H, wall);

            // Maintenance
            FloorAndCeiling("Maintenance Slabs", 1.3f, 8.3f, 8.6f, 15.6f, H, M("Floor"));
            Wall("Maintenance S", new Vector2(1.3f, 8.6f), new Vector2(8.3f, 8.6f), H, wall);
            Wall("Maintenance N", new Vector2(1.3f, 15.6f), new Vector2(8.3f, 15.6f), H, wall, new Gap(5.5f, 1.0f, 2.0f)); // duct entrance at x=6.8
            Wall("Maintenance E", new Vector2(8.3f, 8.6f), new Vector2(8.3f, 15.6f), H, wall);

            // Reactor
            FloorAndCeiling("Reactor Slabs", -4.5f, 4.5f, 21.6f, 29.6f, H, M("Floor"));
            Wall("Reactor S", new Vector2(-4.5f, 21.6f), new Vector2(4.5f, 21.6f), H, wall, new Gap(4.5f, 1.6f, 2.1f));
            Wall("Reactor N", new Vector2(-4.5f, 29.6f), new Vector2(4.5f, 29.6f), H, wall);
            Wall("Reactor W", new Vector2(-4.5f, 21.6f), new Vector2(-4.5f, 29.6f), H, wall);
            Wall("Reactor E", new Vector2(4.5f, 21.6f), new Vector2(4.5f, 29.6f), H, wall, new Gap(2.4f, 1.0f, 2.0f));   // duct exit at z=24

            // Service duct (lower ceiling)
            FloorAndCeiling("Duct A Slabs", 6.2f, 7.4f, 15.6f, 24.6f, 2.2f, M("Floor"));
            FloorAndCeiling("Duct B Slabs", 4.5f, 6.2f, 23.4f, 24.6f, 2.2f, M("Floor"));
            Wall("Duct A W", new Vector2(6.2f, 15.6f), new Vector2(6.2f, 23.4f), 2.2f, M("Trim"));
            Wall("Duct A E", new Vector2(7.4f, 15.6f), new Vector2(7.4f, 24.6f), 2.2f, M("Trim"));
            Wall("Duct N", new Vector2(4.5f, 24.6f), new Vector2(7.4f, 24.6f), 2.2f, M("Trim"));
            Wall("Duct B S", new Vector2(4.5f, 23.4f), new Vector2(6.2f, 23.4f), 2.2f, M("Trim"));

            // Lights: a handful of unshadowed point lights plus emissive strips.
            PointLight(s_Lights, "Tug Light A", new Vector3(0, 2.4f, -1.5f), new Color(1f, 0.9f, 0.75f), 5f, 6f);
            PointLight(s_Lights, "Tug Light B", new Vector3(0, 2.4f, 1f), new Color(1f, 0.9f, 0.75f), 3.5f, 5f);
            WreckLight(            PointLight(s_Lights, "Airlock Light", new Vector3(0, 2.4f, 5.6f), new Color(1f, 0.25f, 0.2f), 3.5f, 6f));
            WreckLight(            PointLight(s_Lights, "Corridor Light S", new Vector3(0, 2.4f, 10.5f), new Color(0.7f, 0.85f, 1f), 2.5f, 6.5f));
            WreckLight(            PointLight(s_Lights, "Corridor Light N", new Vector3(0, 2.4f, 18f), new Color(0.7f, 0.85f, 1f), 2.2f, 6.5f));
            WreckLight(            PointLight(s_Lights, "Cargo Light A", new Vector3(-4.2f, 2.4f, 12.5f), new Color(1f, 0.6f, 0.3f), 3f, 7f));
            WreckLight(            PointLight(s_Lights, "Cargo Light B", new Vector3(-8f, 2.4f, 16f), new Color(1f, 0.3f, 0.2f), 2.5f, 7f));
            WreckLight(            PointLight(s_Lights, "Maintenance Light", new Vector3(4.8f, 2.4f, 12f), new Color(1f, 0.85f, 0.6f), 3.5f, 7.5f));
            WreckLight(            PointLight(s_Lights, "Reactor Light", new Vector3(0, 2.3f, 23.6f), new Color(0.3f, 1f, 0.85f), 3.5f, 8f));
            WreckLight(            PointLight(s_Lights, "Reactor Light Back", new Vector3(0, 2.3f, 28.4f), new Color(0.3f, 0.9f, 1f), 2.5f, 7f));
            WreckLight(            PointLight(s_Lights, "Duct Light", new Vector3(6.8f, 1.9f, 20f), new Color(1f, 0.5f, 0.2f), 1.8f, 5f));

            Strip("Tug Strip", new Vector3(0, H - 0.03f, -0.5f), new Vector3(0.12f, 0.03f, 3.6f), "GlowWarm");
            Strip("Airlock Strip", new Vector3(0, H - 0.03f, 5.6f), new Vector3(2.4f, 0.03f, 0.1f), "GlowRed");
            for (int i = 0; i < 4; i++)
                Strip("Corridor Strip " + i, new Vector3(0, H - 0.03f, 9.5f + i * 3.4f), new Vector3(0.1f, 0.03f, 1.6f), i == 2 ? "GlowRed" : "GlowWhite");
            Strip("Cargo Strip", new Vector3(-5.8f, H - 0.03f, 14.1f), new Vector3(4.5f, 0.03f, 0.1f), "GlowWarm");
            Strip("Maintenance Strip", new Vector3(4.8f, H - 0.03f, 12.1f), new Vector3(0.1f, 0.03f, 3.5f), "GlowWarm");
            Strip("Reactor Strip W", new Vector3(-2.6f, H - 0.03f, 25.6f), new Vector3(0.1f, 0.03f, 4.5f), "GlowTeal");
            Strip("Reactor Strip E", new Vector3(2.6f, H - 0.03f, 25.6f), new Vector3(0.1f, 0.03f, 4.5f), "GlowTeal");
        }

        static void WreckLight(Light light)
        {
            light.gameObject.AddComponent<ProximityFlicker>();
            s_WreckLights.Add(light);
        }

        static void Strip(string name, Vector3 center, Vector3 size, string mat)
        {
            var glow = Box(s_Lights, name, center, size, M(mat), GameLayers.Default, false);
            // Strips in the wreck go dark in a blackout; the tug keeps its lights. Glow strips must not be
            // static-batched, or their per-renderer colour could not be changed.
            if (center.z > 2.5f)
            {
                glow.isStatic = false;
                s_WreckGlow.Add(glow.GetComponent<MeshRenderer>());
            }
            Box(s_Lights, name + " Housing", center + Vector3.up * 0.012f, new Vector3(size.x + 0.08f, size.y, size.z + 0.08f), M("Trim"), GameLayers.Default, false);
        }

        // ------------------------------------------------------------------ doors

        static SlidingDoor BuildDoor(string name, string id, Vector3 floorCenter, float width, float height,
            bool locked, bool airlock, bool withButtons)
        {
            var root = Empty(s_Level, name, floorCenter, null, GameLayers.Environment);
            var leafGo = Box(root.transform, "Leaf", floorCenter + Vector3.up * (height * 0.5f), new Vector3(width + 0.06f, height, 0.1f), M("Door"), GameLayers.Environment, true, false);
            var body = leafGo.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            Box(leafGo.transform, "Stripe", floorCenter + new Vector3(0, 1.1f, 0), new Vector3(width + 0.07f, 0.16f, 0.11f), M("Hazard"), GameLayers.Environment, false, false);
            foreach (float y in new[] { 0.55f, 1.65f })
            {
                Box(leafGo.transform, "Panel", floorCenter + new Vector3(0, y, 0), new Vector3(width - 0.3f, 0.62f, 0.125f), M("Steel"), GameLayers.Environment, false, false);
                Box(leafGo.transform, "Panel Inset", floorCenter + new Vector3(0, y, 0), new Vector3(width - 0.5f, 0.42f, 0.135f), M("Trim"), GameLayers.Environment, false, false);
            }
            Box(root.transform, "Frame Top", floorCenter + new Vector3(0, height + 0.05f, 0), new Vector3(width + 0.3f, 0.1f, T + 0.06f), M("Hazard"), GameLayers.Default, false);

            // Hazard frame so doorways read clearly in the dark.
            Box(root.transform, "Frame L", floorCenter + new Vector3(-width * 0.5f - 0.05f, height * 0.5f, 0), new Vector3(0.1f, height, T + 0.06f), M("Hazard"), GameLayers.Default, false);
            Box(root.transform, "Frame R", floorCenter + new Vector3(width * 0.5f + 0.05f, height * 0.5f, 0), new Vector3(0.1f, height, T + 0.06f), M("Hazard"), GameLayers.Default, false);

            var door = root.AddComponent<SlidingDoor>();
            door.doorId = id;
            door.leaf = leafGo.transform;
            door.openOffset = new Vector3(0, height + 0.05f, 0);
            door.locked = locked;
            door.airlock = airlock;
            door.startsOpen = false;
            door.makesGameplayNoise = !locked;
            door.doorwaySize = new Vector3(width, height, 0.7f);

            var status = Text(root.transform, "Status South", locked ? "SEALED" : "CLOSED", new Vector3(0, height + 0.11f, -T * 0.5f - 0.02f), Facing(Vector3.back), 0.9f, new Vector2(1.4f, 0.2f), new Color(1f, 0.85f, 0.3f));
            status.fontStyle = FontStyles.Bold;
            door.statusLabel = status;

            if (withButtons)
            {
                float x = width * 0.5f + 0.32f;
                var south = Button(root.transform, "Button South", new Vector3(x, 1.2f, -T * 0.5f - 0.01f), Facing(Vector3.back), new Vector2(0.22f, 0.14f), "OPEN", M("ButtonYellow"), 0.5f);
                var north = Button(root.transform, "Button North", new Vector3(-x, 1.2f, T * 0.5f + 0.01f), Facing(Vector3.forward), new Vector2(0.22f, 0.14f), "OPEN", M("ButtonYellow"), 0.5f);
                door.buttons = new[] { south, north };
            }
            return door;
        }

        // ------------------------------------------------------------------ handrails

        static void Rail(Vector3 a, Vector3 b)
        {
            Vector3 d = b - a;
            float length = d.magnitude;
            var go = Empty(s_Rails, "Rail", (a + b) * 0.5f, Quaternion.LookRotation(d / length, Vector3.up), GameLayers.Rail);
            Prim(go.transform, "Bar", PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.04f, length * 0.5f, 0.04f), M("Rail"), false, new Vector3(90, 0, 0));
            // Brackets at each end and along the span.
            int brackets = Mathf.Max(2, Mathf.RoundToInt(length / 1.4f) + 1);
            for (int i = 0; i < brackets; i++)
            {
                float z = Mathf.Lerp(-length * 0.5f, length * 0.5f, i / (float)(brackets - 1));
                CBox(go.transform, "Bracket", new Vector3(0, 0, z), new Vector3(0.062f, 0.062f, 0.05f), M("Steel"), 0.012f);
            }
            var col = go.AddComponent<BoxCollider>();
            col.size = new Vector3(0.11f, 0.11f, length);

            // A dim guide light along the floor under each rail: it marks the route and survives a blackout.
            if (a.y < 1.5f && Mathf.Abs(a.y - b.y) < 0.01f)
            {
                var guide = Box(s_Lights, "Guide Light", new Vector3((a.x + b.x) * 0.5f, 0.2f, (a.z + b.z) * 0.5f),
                    Mathf.Abs(d.x) > Mathf.Abs(d.z) ? new Vector3(length - 0.1f, 0.025f, 0.03f) : new Vector3(0.03f, 0.025f, length - 0.1f), M("GuideLight"), GameLayers.Default, false);
            }

            // The zero-g locomotion grabs anything on the Rail or Environment layers; the highlight
            // lights the rail up when a hand is close enough to take hold.
            var highlight = go.AddComponent<HoverHighlight>();
            highlight.renderers = new Renderer[] { go.transform.Find("Bar").GetComponent<MeshRenderer>() };
            highlight.hoverEmission = new Color(0.9f, 0.7f, 0.1f);
        }

        static void BuildRails()
        {
            const float o = 0.2f; // distance from the wall centre line
            // Tug
            Rail(new Vector3(2 - o, RailY, 0.0f), new Vector3(2 - o, RailY, 0.8f));
            Rail(new Vector3(-1.8f, RailY, 2 - o), new Vector3(-0.85f, RailY, 2 - o));
            Rail(new Vector3(0.85f, RailY, 2 - o), new Vector3(1.8f, RailY, 2 - o));
            Rail(new Vector3(-0.6f, H - 0.25f, -2.2f), new Vector3(-0.6f, H - 0.25f, 1.6f));
            // Collar
            Rail(new Vector3(-0.9f + o, RailY, 2.1f), new Vector3(-0.9f + o, RailY, 3.5f));
            Rail(new Vector3(0.9f - o, RailY, 2.1f), new Vector3(0.9f - o, RailY, 3.5f));
            // Airlock
            Rail(new Vector3(-2 + o, RailY, 5.9f), new Vector3(-2 + o, RailY, 7.3f));   // lockers fill the rest of this wall
            Rail(new Vector3(2 - o, RailY, 3.9f), new Vector3(2 - o, RailY, 7.3f));
            // Corridor west wall (gap at the cargo door z 11.1..12.9)
            Rail(new Vector3(-1.3f + o, RailY, 7.9f), new Vector3(-1.3f + o, RailY, 10.9f));
            Rail(new Vector3(-1.3f + o, RailY, 13.1f), new Vector3(-1.3f + o, RailY, 17f));
            Rail(new Vector3(-1.3f + o, RailY, 17.3f), new Vector3(-1.3f + o, RailY, 21.3f));
            // Corridor east wall (gap at the maintenance door z 9.7..11.3)
            Rail(new Vector3(1.3f - o, RailY, 7.9f), new Vector3(1.3f - o, RailY, 9.5f));
            Rail(new Vector3(1.3f - o, RailY, 11.5f), new Vector3(1.3f - o, RailY, 16f));
            Rail(new Vector3(1.3f - o, RailY, 16.3f), new Vector3(1.3f - o, RailY, 21.3f));
            // Cargo
            Rail(new Vector3(-1.6f, RailY, 9.6f + o), new Vector3(-9.8f, RailY, 9.6f + o));
            Rail(new Vector3(-10.3f + o, RailY, 10f), new Vector3(-10.3f + o, RailY, 16.6f));
            Rail(new Vector3(-8.2f, RailY, 18.6f - o), new Vector3(-1.6f, RailY, 18.6f - o));
            Rail(new Vector3(-1.3f - o, RailY, 13.2f), new Vector3(-1.3f - o, RailY, 18.2f));
            Rail(new Vector3(-6.95f, RailY, 14f), new Vector3(-6.95f, RailY, 15.2f));  // on the side of a container
            // Maintenance
            Rail(new Vector3(1.7f, RailY, 8.6f + o), new Vector3(2.9f, RailY, 8.6f + o));
            Rail(new Vector3(4.3f, RailY, 8.6f + o), new Vector3(5.8f, RailY, 8.6f + o));
            Rail(new Vector3(8.3f - o, RailY, 9f), new Vector3(8.3f - o, RailY, 11.3f));
            Rail(new Vector3(8.3f - o, RailY, 12.8f), new Vector3(8.3f - o, RailY, 15.2f));
            Rail(new Vector3(1.7f, RailY, 15.6f - o), new Vector3(6f, RailY, 15.6f - o));
            Rail(new Vector3(1.3f + o, RailY, 11.6f), new Vector3(1.3f + o, RailY, 15.2f));
            // Reactor
            Rail(new Vector3(-4.5f + o, RailY, 22f), new Vector3(-4.5f + o, RailY, 29.2f));
            Rail(new Vector3(4.5f - o, RailY, 24.8f), new Vector3(4.5f - o, RailY, 29.2f));
            Rail(new Vector3(-4.1f, RailY, 29.6f - o), new Vector3(4.1f, RailY, 29.6f - o));
            Rail(new Vector3(-4.1f, RailY, 21.6f + o), new Vector3(-1.2f, RailY, 21.6f + o));
            Rail(new Vector3(1.2f, RailY, 21.6f + o), new Vector3(4.1f, RailY, 21.6f + o));
            Rail(new Vector3(-1.25f, RailY, 24.9f), new Vector3(-1.25f, RailY, 26.9f)); // around the housing
            Rail(new Vector3(1.25f, RailY, 24.9f), new Vector3(1.25f, RailY, 26.9f));
            // Service duct
            Rail(new Vector3(7.4f - o, RailY, 16f), new Vector3(7.4f - o, RailY, 24.2f));
            Rail(new Vector3(6.2f + o, RailY, 16f), new Vector3(6.2f + o, RailY, 23f));
            Rail(new Vector3(4.8f, RailY, 24.6f - o), new Vector3(7f, RailY, 24.6f - o));
        }

        // ------------------------------------------------------------------ teleport + recovery anchors

        static void TeleportArea(string name, float x0, float x1, float z0, float z1)
        {
            var go = Empty(s_Teleport, name, new Vector3((x0 + x1) * 0.5f, 0.02f, (z0 + z1) * 0.5f), null, GameLayers.TeleportSurface);
            var col = go.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.size = new Vector3(x1 - x0, 0.04f, z1 - z0);
            go.AddComponent<TeleportSurface>();
        }

        static void Anchor(string name, float x, float z)
        {
            Empty(s_Teleport, "Recovery Anchor " + name, new Vector3(x, 0, z)).AddComponent<RecoveryAnchor>();
        }

        static void BuildTeleportAndAnchors()
        {
            const float i = 0.35f; // inset from walls
            TeleportArea("Tug", -2 + i, 2 - i, -3 + i, 2 - i);
            TeleportArea("Tug Hatchway", -0.5f, 0.5f, 1.6f, 4f);
            TeleportArea("Airlock", -2 + i, 2 - i, 3.6f + i, 7.6f - i);
            TeleportArea("Airlock Doorway", -0.5f, 0.5f, 7.2f, 8f);
            TeleportArea("Corridor", -1.3f + i, 1.3f - i, 7.6f + i, 21.6f - i);
            TeleportArea("Cargo Doorway", -1.9f, -0.9f, 11.4f, 12.6f);
            TeleportArea("Cargo", -10.3f + i, -1.3f - i, 9.6f + i, 18.6f - i);
            TeleportArea("Maintenance Doorway", 0.9f, 1.9f, 10f, 11f);
            TeleportArea("Maintenance", 1.3f + i, 8.3f - i, 8.6f + i, 15.6f - i);
            TeleportArea("Reactor Doorway", -0.55f, 0.55f, 21.2f, 22f);
            TeleportArea("Reactor", -4.5f + i, 4.5f - i, 21.6f + i, 29.6f - i);
            TeleportArea("Duct Entrance", 6.5f, 7.1f, 15.1f, 16.2f);
            TeleportArea("Duct A", 6.5f, 7.1f, 15.9f, 24.3f);
            TeleportArea("Duct B", 4.2f, 6.6f, 23.7f, 24.3f);

            Anchor("Tug", 0f, -0.8f);
            Anchor("Airlock", 0f, 5.6f);
            Anchor("Corridor South", 0f, 9.4f);
            Anchor("Corridor Mid", 0f, 15f);
            Anchor("Corridor North", 0f, 20f);
            Anchor("Cargo", -3.4f, 12f);
            Anchor("Maintenance", 3.2f, 10.6f);
            Anchor("Reactor", 0f, 23.2f);
            Anchor("Duct", 6.8f, 19f);
        }

        // ------------------------------------------------------------------ cuttables

        static CutPoint MakeCutPoint(Transform parent, Vector3 localPos)
        {
            var go = Empty(parent, "Cut Point", localPos, null, GameLayers.CutPoint);
            var col = go.AddComponent<SphereCollider>();
            col.isTrigger = true;
            col.radius = 0.075f;
            var marker = Prim(go.transform, "Marker", PrimitiveType.Sphere, Vector3.zero, new Vector3(0.07f, 0.07f, 0.05f), M("CutMarker"), false, null, GameLayers.CutPoint);
            var point = go.AddComponent<CutPoint>();
            point.marker = marker.GetComponent<MeshRenderer>();
            point.markerTransform = marker.transform;
            return point;
        }

        /// <summary>
        /// A cuttable plate. The root's +Z points out of the surface toward the player, so the freed
        /// plate drifts toward them. Cut points sit on the plate edge and are reachable from both faces.
        /// </summary>
        static Cuttable MakeCuttable(string name, string id, string displayName, Vector3 position, Vector3 outward,
            Vector2 plateSize, Vector2[] cutLocal, bool training = false, bool hazardFrame = true)
        {
            var root = Empty(s_Props, name, position, Quaternion.LookRotation(outward, Vector3.up), GameLayers.Environment);
            var plateGo = new GameObject("Plate") { layer = GameLayers.Environment };
            plateGo.transform.SetParent(root.transform, false);
            Prim(plateGo.transform, "Sheet", PrimitiveType.Cube, Vector3.zero, new Vector3(plateSize.x, plateSize.y, 0.04f), M("Plate"), true, null, GameLayers.Environment);
            if (hazardFrame)
            {
                Prim(plateGo.transform, "Stripe Top", PrimitiveType.Cube, new Vector3(0, plateSize.y * 0.5f - 0.04f, 0.021f), new Vector3(plateSize.x, 0.06f, 0.004f), M("Hazard"), false, null, GameLayers.Environment);
                Prim(plateGo.transform, "Stripe Bottom", PrimitiveType.Cube, new Vector3(0, -plateSize.y * 0.5f + 0.04f, 0.021f), new Vector3(plateSize.x, 0.06f, 0.004f), M("Hazard"), false, null, GameLayers.Environment);
            }
            var body = plateGo.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.mass = 4f;
            body.linearDamping = 0.3f;
            body.angularDamping = 0.3f;
            var impact = plateGo.AddComponent<ImpactNoiseEmitter>();
            impact.heavy = true;

            var points = new List<CutPoint>();
            foreach (var p in cutLocal) points.Add(MakeCutPoint(root.transform, new Vector3(p.x, p.y, 0.01f)));

            var label = Text(root.transform, "Label", displayName, new Vector3(0, plateSize.y * 0.5f + 0.14f, 0.03f), Facing(Vector3.forward), 0.5f, new Vector2(1.2f, 0.22f), new Color(1f, 0.8f, 0.3f));
            label.fontStyle = FontStyles.Bold;

            var cuttable = root.AddComponent<Cuttable>();
            cuttable.panelId = id;
            cuttable.displayName = displayName;
            cuttable.cutPoints = points.ToArray();
            cuttable.plate = body;
            cuttable.progressLabel = label;
            cuttable.isTraining = training;
            if (!training) s_Cuttables.Add(cuttable);
            return cuttable;
        }

        static Vector2[] PerimeterPoints(Vector2 size, bool sixPoints)
        {
            float x = size.x * 0.5f - 0.03f, y = size.y * 0.5f - 0.03f;
            var list = new List<Vector2> { new Vector2(-x, y), new Vector2(x, y), new Vector2(x, -y), new Vector2(-x, -y) };
            if (sixPoints)
            {
                list.Insert(1, new Vector2(x, 0));
                list.Add(new Vector2(-x, 0));
            }
            return list.ToArray();
        }

        /// <summary>A sealed block with a cavity opening on its south (-Z) face, covered by a cuttable panel.</summary>
        static Cuttable SealedCompartment(string name, string id, string displayName, Vector3 floorCenter, Vector3 size,
            Material mat, out Vector3 cavityCenter)
        {
            var parent = Empty(s_Props, name, Vector3.zero, null, GameLayers.Environment).transform;
            float x0 = floorCenter.x - size.x * 0.5f, x1 = floorCenter.x + size.x * 0.5f;
            float z0 = floorCenter.z - size.z * 0.5f, z1 = floorCenter.z + size.z * 0.5f;
            const float cy0 = 0.8f, cy1 = 1.7f, half = 0.45f, depth = 0.7f;

            Box(parent, "Bottom", new Vector3(floorCenter.x, cy0 * 0.5f, floorCenter.z), new Vector3(size.x, cy0, size.z), mat);
            Box(parent, "Top", new Vector3(floorCenter.x, (cy1 + size.y) * 0.5f, floorCenter.z), new Vector3(size.x, size.y - cy1, size.z), mat);
            Box(parent, "Left", new Vector3((x0 + floorCenter.x - half) * 0.5f, (cy0 + cy1) * 0.5f, floorCenter.z), new Vector3(floorCenter.x - half - x0, cy1 - cy0, size.z), mat);
            Box(parent, "Right", new Vector3((x1 + floorCenter.x + half) * 0.5f, (cy0 + cy1) * 0.5f, floorCenter.z), new Vector3(x1 - floorCenter.x - half, cy1 - cy0, size.z), mat);
            Box(parent, "Back", new Vector3(floorCenter.x, (cy0 + cy1) * 0.5f, (z0 + depth + z1) * 0.5f), new Vector3(half * 2f, cy1 - cy0, z1 - z0 - depth), mat);
            Box(parent, "Inner Glow", new Vector3(floorCenter.x, cy1 - 0.02f, z0 + depth * 0.5f), new Vector3(half * 1.6f, 0.02f, 0.06f), M("GlowTeal"), GameLayers.Default, false);

            cavityCenter = new Vector3(floorCenter.x, (cy0 + cy1) * 0.5f, z0 + depth * 0.5f + 0.03f);
            var plateSize = new Vector2(half * 2f + 0.14f, cy1 - cy0 + 0.14f);
            return MakeCuttable(name + " Panel", id, displayName, new Vector3(floorCenter.x, (cy0 + cy1) * 0.5f, z0 - 0.03f),
                Vector3.back, plateSize, PerimeterPoints(plateSize, true));
        }

        static Vector3 s_ReactorCavity, s_CageCavity;

        static Cuttable BuildCompartments(out Cuttable cage, out Cuttable duct, out Cuttable rack, out Cuttable practice)
        {
            var reactor = SealedCompartment("Reactor Housing", "panel_reactor", "REACTOR ACCESS", new Vector3(0, 0, 25.9f), new Vector3(1.8f, 2.4f, 1.8f), M("ReactorShell"), out s_ReactorCavity);
            cage = SealedCompartment("Secure Cargo Locker", "panel_cage", "SECURE CARGO", new Vector3(-9.3f, 0, 17.6f), new Vector3(1.7f, 2.2f, 1.7f), M("ContainerBlue"), out s_CageCavity);

            // Service duct cover on the maintenance side (the shortcut to the reactor).
            var ductSize = new Vector2(1.12f, 2.04f);
            duct = MakeCuttable("Service Duct Cover", "panel_duct", "SERVICE DUCT", new Vector3(6.8f, 1.02f, 15.6f - T * 0.5f - 0.03f), Vector3.back, ductSize,
                new[] { new Vector2(-0.52f, 1.5f - 1.02f), new Vector2(0.52f, 1.5f - 1.02f), new Vector2(0.52f, 1.05f - 1.02f), new Vector2(0.52f, 0.6f - 1.02f), new Vector2(-0.52f, 0.6f - 1.02f), new Vector2(-0.52f, 1.05f - 1.02f) });

            // Electronics rack bracket on the maintenance east wall (mounted salvage).
            var bracketSize = new Vector2(0.62f, 0.1f);
            rack = MakeCuttable("Electronics Rack Bracket", "mount_rack", "RACK CLAMP", new Vector3(8.3f - T * 0.5f - 0.3f, 1.35f, 12.05f), Vector3.left, bracketSize,
                new[] { new Vector2(-0.28f, 0f), new Vector2(0.28f, 0f) }, false, false);
            // Shelf the module sits against.
            Box(s_Props, "Rack Shelf", new Vector3(8.02f, 1.1f, 12.05f), new Vector3(0.36f, 0.04f, 0.7f), M("Trim"));
            Box(s_Props, "Rack Back", new Vector3(8.17f, 1.4f, 12.05f), new Vector3(0.04f, 0.7f, 0.7f), M("Trim"));

            // Practice plate in the tug.
            var practiceSize = new Vector2(0.5f, 0.5f);
            practice = MakeCuttable("Practice Plate", "practice_plate", "PRACTICE PLATE", new Vector3(2f - T * 0.5f - 0.03f, 1.3f, 1.25f), Vector3.left, practiceSize, PerimeterPoints(practiceSize, false), true);
            return reactor;
        }

        // ------------------------------------------------------------------ salvage spawn points

        static SalvageSpawnPoint Spawn(string id, Vector3 position, SalvageCategoryMask allowed, Cuttable behindPanel = null, Cuttable mount = null)
        {
            var point = Empty(s_Spawns, "Spawn " + id, position).AddComponent<SalvageSpawnPoint>();
            point.pointId = id;
            point.allowed = allowed;
            point.behindPanel = behindPanel;
            point.mount = mount;
            s_SpawnPoints.Add(point);
            return point;
        }

        static void BuildSpawnPoints(Cuttable reactorPanel, Cuttable cagePanel, Cuttable rackMount)
        {
            const SalvageCategoryMask S = SalvageCategoryMask.SmallScrap;
            const SalvageCategoryMask E = SalvageCategoryMask.Electronics;
            const SalvageCategoryMask N = SalvageCategoryMask.Novelty;

            Spawn("reactor_housing", s_ReactorCavity, SalvageCategoryMask.ReactorComponent, reactorPanel);
            Spawn("cargo_locker", s_CageCavity, E | N, cagePanel);
            Spawn("maint_rack", new Vector3(7.98f, 1.24f, 12.05f), E, null, rackMount);

            Spawn("airlock_corner", new Vector3(1.3f, 1.0f, 6.8f), S);
            Spawn("corridor_south", new Vector3(-0.7f, 1.2f, 14.6f), S);
            Spawn("corridor_north", new Vector3(0.7f, 1.3f, 18.6f), S | E);
            Spawn("cargo_entry", new Vector3(-3.4f, 1.0f, 10.6f), S);
            Spawn("cargo_mid", new Vector3(-7.3f, 1.3f, 13.2f), S | E);
            Spawn("cargo_far", new Vector3(-9.4f, 1.1f, 10.6f), S | N);
            Spawn("cargo_north", new Vector3(-5.2f, 1.2f, 17.6f), S);
            Spawn("cargo_shelf", new Vector3(-2.2f, 1.25f, 17.6f), E | N);
            Spawn("maint_floor", new Vector3(3.0f, 1.1f, 9.6f), S);
            Spawn("maint_bench", new Vector3(5.2f, 1.2f, 14.3f), E | S);
            Spawn("maint_galley", new Vector3(2.3f, 1.3f, 14.7f), N | S);
            Spawn("maint_corner", new Vector3(7.5f, 1.1f, 9.4f), S | E);
            Spawn("duct_bend", new Vector3(6.8f, 1.0f, 22.6f), S | E);
            Spawn("reactor_west", new Vector3(-3.6f, 1.1f, 22.8f), S);
            Spawn("reactor_console", new Vector3(-3.5f, 1.25f, 27.6f), E | N);
            Spawn("reactor_back", new Vector3(1.6f, 1.2f, 28.8f), S | E);
            Spawn("reactor_east", new Vector3(3.7f, 1.3f, 22.6f), S | N);
        }

        // ------------------------------------------------------------------ props, signs, debris

        static void Sign(string text, Vector3 position, Vector3 outward, float fontSize = 1.6f, Color? color = null)
        {
            var t = Text(s_Signs, "Sign " + text.Replace('\n', ' '), text, position, Facing(outward), fontSize, new Vector2(3f, 0.6f), color ?? new Color(1f, 0.85f, 0.4f));
            t.fontStyle = FontStyles.Bold;
        }

        static void Container(Vector3 floorCenter, Vector3 size, string mat = "Container")
        {
            var root = Box(s_Props, "Container", floorCenter + Vector3.up * (size.y * 0.5f), size, M(mat)).transform;
            Vector3 c = floorCenter + Vector3.up * (size.y * 0.5f);
            // Corrugation ribs on the two long sides, posts on the corners, a frame top and bottom.
            bool longX = size.x >= size.z;
            float longLen = longX ? size.x : size.z, shortLen = longX ? size.z : size.x;
            int ribs = Mathf.Max(3, Mathf.RoundToInt(longLen / 0.28f));
            for (int i = 0; i < ribs; i++)
            {
                float along = -longLen * 0.5f + (i + 0.5f) * longLen / ribs;
                foreach (float side in new[] { -1f, 1f })
                {
                    Vector3 offset = longX ? new Vector3(along, 0, side * (shortLen * 0.5f + 0.012f)) : new Vector3(side * (shortLen * 0.5f + 0.012f), 0, along);
                    Vector3 ribSize = longX ? new Vector3(0.07f, size.y - 0.16f, 0.024f) : new Vector3(0.024f, size.y - 0.16f, 0.07f);
                    Box(root, "Rib", c + offset, ribSize, M(mat), GameLayers.Default, false);
                }
            }
            foreach (float sx in new[] { -1f, 1f })
                foreach (float sz in new[] { -1f, 1f })
                    Box(root, "Post", c + new Vector3(sx * (size.x * 0.5f - 0.02f), 0, sz * (size.z * 0.5f - 0.02f)), new Vector3(0.09f, size.y + 0.02f, 0.09f), M("Trim"), GameLayers.Default, false);
            Box(root, "Top Frame", c + Vector3.up * (size.y * 0.5f - 0.03f), new Vector3(size.x + 0.04f, 0.07f, size.z + 0.04f), M("Trim"), GameLayers.Default, false);
            Box(root, "Bottom Frame", c - Vector3.up * (size.y * 0.5f - 0.03f), new Vector3(size.x + 0.04f, 0.07f, size.z + 0.04f), M("Trim"), GameLayers.Default, false);
        }

        /// <summary>A round pipe between two points with flange rings along it.</summary>
        static void Pipe(string name, Vector3 a, Vector3 b, float radius, string mat)
        {
            Vector3 d = b - a;
            float length = d.magnitude;
            var root = Empty(s_Props, name, (a + b) * 0.5f, Quaternion.FromToRotation(Vector3.up, d / length), GameLayers.Default).transform;
            Part(root, "Pipe", Tube(radius, length, 10), Vector3.zero, M(mat)).isStatic = true;
            int flanges = Mathf.Max(2, Mathf.RoundToInt(length / 2.2f) + 1);
            for (int i = 0; i < flanges; i++)
            {
                float y = Mathf.Lerp(-length * 0.5f + 0.05f, length * 0.5f - 0.05f, i / (float)(flanges - 1));
                Part(root, "Flange", Tube(radius * 1.45f, 0.05f, 10), new Vector3(0, y, 0), M("Steel")).isStatic = true;
            }
        }

        /// <summary>Angled screen and a row of lit keys on top of a console block.</summary>
        static void ConsoleDetail(Vector3 topCentre, float width, Vector3 userSide)
        {
            var root = Empty(s_Props, "Console Detail", topCentre, Quaternion.LookRotation(-userSide, Vector3.up), GameLayers.Default).transform;
            CBox(root, "Screen Housing", new Vector3(0, 0.16f, 0.12f), new Vector3(width * 0.8f, 0.3f, 0.05f), M("Trim"), 0.012f, new Vector3(-20, 0, 0));
            CBox(root, "Screen", new Vector3(0, 0.165f, 0.092f), new Vector3(width * 0.72f, 0.23f, 0.006f), M("ScreenGlow"), 0.002f, new Vector3(-20, 0, 0));
            int keys = Mathf.Max(4, Mathf.RoundToInt(width / 0.12f));
            for (int i = 0; i < keys; i++)
            {
                string mat = i % 5 == 0 ? "GlowRed" : i % 3 == 0 ? "ButtonAmber" : "Steel";
                CBox(root, "Key", new Vector3(-width * 0.36f + i * (width * 0.72f / (keys - 1)), 0.012f, -0.12f), new Vector3(0.05f, 0.02f, 0.05f), M(mat), 0.006f);
            }
            CBox(root, "Keyboard Tray", new Vector3(0, 0.004f, -0.12f), new Vector3(width * 0.82f, 0.012f, 0.1f), M("Chip"), 0.003f);
        }

        static void Junk(GameObject prefab, Vector3 position)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, s_Loose != null ? s_Loose : s_Props);
            go.transform.SetPositionAndRotation(position, Quaternion.Euler(position.x * 37f, position.z * 53f, position.y * 71f));
        }

        static void BuildDecor()
        {
            // --- tug: window, pilot console, hatch signage
            BuildCockpit();
            Sign("SALVAGE TUG 'PAYCHEQUE'", new Vector3(0, 2.45f, 2f - T * 0.5f - 0.02f), Vector3.back, 1.3f);
            Sign("WRECK >>", new Vector3(1.35f, 1.7f, 2f - T * 0.5f - 0.02f), Vector3.back, 1.2f);
            Sign("GRIP ANY SURFACE TO HOLD ON", new Vector3(-1.35f, 1.7f, 2f - T * 0.5f - 0.02f), Vector3.back, 0.7f, new Color(1f, 0.85f, 0.3f));
            Sign("AIRLOCK\nOXYGEN DRAINS BEYOND THE TUG", new Vector3(0, 2.5f, 3.6f + T * 0.5f + 0.02f), Vector3.forward, 1.0f, new Color(1f, 0.5f, 0.4f));
            Sign("<< TUG (SAFE)", new Vector3(-1.3f, 1.7f, 3.6f + T * 0.5f + 0.02f), Vector3.forward, 1.1f, new Color(0.5f, 1f, 0.6f));

            // --- corridor signage, ribs, pipes
            Sign("CARGO BAY", new Vector3(-1.3f + T * 0.5f + 0.02f, 2.45f, 12f), Vector3.right, 1.3f);
            Sign("MAINTENANCE", new Vector3(1.3f - T * 0.5f - 0.02f, 2.45f, 10.5f), Vector3.left, 1.3f);
            Sign("REACTOR", new Vector3(0, 2.45f, 21.6f - T * 0.5f - 0.02f), Vector3.back, 1.5f, new Color(0.4f, 1f, 0.9f));
            Sign("<< EXIT TO TUG", new Vector3(0, 2.45f, 7.6f + T * 0.5f + 0.02f), Vector3.forward, 1.2f, new Color(0.5f, 1f, 0.6f));
            Sign("SERVICE DUCT\nTO REACTOR", new Vector3(5.6f, 1.9f, 15.6f - T * 0.5f - 0.02f), Vector3.back, 1.0f);
            Sign("SERVICE DUCT\nTO MAINTENANCE", new Vector3(4.5f - T * 0.5f - 0.02f, 2.35f, 24f), Vector3.left, 0.9f);
            Sign("MAINTENANCE UNIT DOCK\nDO NOT WAKE IT", new Vector3(3.4f, 2.2f, 29.6f - T * 0.5f - 0.02f), Vector3.back, 0.9f, new Color(1f, 0.5f, 0.4f));
            for (int i = 0; i < 6; i++)
            {
                float z = 8.6f + i * 2.4f;
                if (Mathf.Abs(z - 12f) > 1.2f) Box(s_Props, "Rib W", new Vector3(-1.3f + T * 0.5f + 0.04f, H * 0.5f, z), new Vector3(0.08f, H, 0.16f), M("Trim"), GameLayers.Default, false);
                if (Mathf.Abs(z - 10.5f) > 1.1f) Box(s_Props, "Rib E", new Vector3(1.3f - T * 0.5f - 0.04f, H * 0.5f, z), new Vector3(0.08f, H, 0.16f), M("Trim"), GameLayers.Default, false);
            }
            Pipe("Corridor Pipe A", new Vector3(-0.85f, H - 0.2f, 7.8f), new Vector3(-0.85f, H - 0.2f, 21.4f), 0.06f, "Pipe");
            Pipe("Corridor Pipe B", new Vector3(-0.66f, H - 0.15f, 7.8f), new Vector3(-0.66f, H - 0.15f, 21.4f), 0.035f, "Steel");
            Pipe("Corridor Cable Run", new Vector3(0.9f, H - 0.13f, 7.8f), new Vector3(0.9f, H - 0.13f, 21.4f), 0.028f, "Cable");
            Pipe("Cargo Pipe", new Vector3(-10.0f, H - 0.25f, 9.9f), new Vector3(-10.0f, H - 0.25f, 18.3f), 0.07f, "Pipe");
            Pipe("Maintenance Pipe", new Vector3(1.6f, H - 0.22f, 8.9f), new Vector3(8.0f, H - 0.22f, 8.9f), 0.05f, "Pipe");
            Pipe("Reactor Coolant W", new Vector3(-3.3f, 2.0f, 25.2f), new Vector3(-3.3f, H, 25.2f), 0.09f, "Steel");
            Pipe("Reactor Coolant E", new Vector3(3.3f, 2.0f, 26.4f), new Vector3(3.3f, H, 26.4f), 0.09f, "Steel");
            Pipe("Reactor Feed W", new Vector3(-0.9f, 2.25f, 25.9f), new Vector3(-3.3f, 2.25f, 25.4f), 0.05f, "Pipe");
            Pipe("Reactor Feed E", new Vector3(0.9f, 2.25f, 25.9f), new Vector3(3.3f, 2.25f, 26.2f), 0.05f, "Pipe");

            // Consoles get lit screens and key rows so they read as equipment, not blocks.
            ConsoleDetail(new Vector3(-3.6f, 1.0f, 28.4f), 1.1f, Vector3.back);
            ConsoleDetail(new Vector3(5.2f, 0.9f, 14.9f), 2.0f, Vector3.back);

            // Reactor housing: containment bands and warning beacons.
            for (int i = 0; i < 3; i++)
                Box(s_Props, "Containment Band", new Vector3(0, 0.45f + i * 0.85f, 25.9f), new Vector3(1.9f, 0.09f, 1.9f), M("Steel"), GameLayers.Default, false);
            foreach (float sx in new[] { -1f, 1f })
                Box(s_Props, "Reactor Beacon", new Vector3(sx * 0.78f, 2.05f, 24.97f), new Vector3(0.1f, 0.1f, 0.05f), M("GlowRed"), GameLayers.Default, false);

            // --- cargo bay: containers make cover to hide behind
            Container(new Vector3(-4.6f, 0, 11.2f), new Vector3(1.3f, 2.1f, 2.2f));
            Container(new Vector3(-4.6f, 0, 15.3f), new Vector3(1.3f, 2.1f, 2.4f), "ContainerBlue");
            Container(new Vector3(-8.2f, 0, 12.2f), new Vector3(2.2f, 1.4f, 1.3f), "ContainerBlue");
            Container(new Vector3(-8.2f, 0, 14.6f), new Vector3(2.2f, 2.1f, 1.3f));
            Container(new Vector3(-2.4f, 0, 15.8f), new Vector3(1.0f, 1.0f, 1.0f));
            Box(s_Props, "Cargo Shelf", new Vector3(-2.2f, 1.05f, 18.2f), new Vector3(1.4f, 0.05f, 0.5f), M("Trim"));
            Box(s_Props, "Cargo Hazard Line", new Vector3(-5.8f, 0.006f, 12.9f), new Vector3(7.5f, 0.01f, 0.14f), M("Hazard"), GameLayers.Default, false);

            // --- maintenance: bench, cabinet, galley shelf, pillar
            Box(s_Props, "Workbench", new Vector3(5.2f, 0.45f, 14.9f), new Vector3(2.2f, 0.9f, 0.8f), M("Trim"));
            Box(s_Props, "Tool Cabinet", new Vector3(3.6f, 1.0f, 8.95f), new Vector3(1.2f, 2.0f, 0.5f), M("ContainerBlue"));
            Box(s_Props, "Galley Counter", new Vector3(2.0f, 0.45f, 14.9f), new Vector3(1.0f, 0.9f, 0.8f), M("WallTug"));
            Box(s_Props, "Pillar", new Vector3(4.6f, H * 0.5f, 11.8f), new Vector3(0.5f, H, 0.5f), M("Trim"));
            Sign("GALLEY\nTOAST IS A PRIVILEGE", new Vector3(2.0f, 1.9f, 15.6f - T * 0.5f - 0.02f), Vector3.back, 0.8f);

            // --- reactor: coolant tanks (hiding spots) and a console
            Box(s_Props, "Coolant Tank W", new Vector3(-3.3f, 1.0f, 25.2f), new Vector3(1.0f, 2.0f, 1.0f), M("ReactorShell"));
            Box(s_Props, "Coolant Tank E", new Vector3(3.3f, 1.0f, 26.4f), new Vector3(1.0f, 2.0f, 1.0f), M("ReactorShell"));
            Box(s_Props, "Reactor Console", new Vector3(-3.6f, 0.5f, 28.4f), new Vector3(1.2f, 1.0f, 0.7f), M("Trim"));
            Box(s_Props, "Reactor Floor Ring", new Vector3(0, 0.006f, 24.6f), new Vector3(2.6f, 0.01f, 0.14f), M("Hazard"), GameLayers.Default, false);
            Sign("FUSION REGULATOR\nCUT PANEL TO ACCESS", new Vector3(0, 2.25f, 25f - 0.02f), Vector3.back, 0.8f, new Color(0.4f, 1f, 0.9f));

            // --- floating junk (throwable distractions) kept to a small, bounded count
            Junk(s_JunkCanPrefab, new Vector3(0.6f, 1.5f, 12.8f));
            Junk(s_JunkCanPrefab, new Vector3(-6.2f, 1.6f, 11.0f));
            Junk(s_JunkCanPrefab, new Vector3(3.9f, 1.4f, 13.2f));
            Junk(s_JunkCanPrefab, new Vector3(-2.6f, 1.5f, 23.4f));
            Junk(s_JunkCanPrefab, new Vector3(-1.1f, 1.3f, 4.6f));
            Junk(s_JunkCratePrefab, new Vector3(-3.0f, 1.3f, 14.0f));
            Junk(s_JunkCratePrefab, new Vector3(6.6f, 1.2f, 10.8f));
            Junk(s_JunkCratePrefab, new Vector3(2.4f, 1.4f, 27.4f));
        }

        // ------------------------------------------------------------------ drone navigation graph

        static void Waypoint(string id, float x, float z)
        {
            var wp = Empty(s_Level.Find("Drone Waypoints") ?? Empty(s_Level, "Drone Waypoints", Vector3.zero).transform, "WP " + id, new Vector3(x, 1.35f, z)).AddComponent<EnemyWaypoint>();
            s_Waypoints[id] = wp;
        }

        static void Link(params string[] chain)
        {
            for (int i = 0; i + 1 < chain.Length; i++) EnemyWaypoint.Link(s_Waypoints[chain[i]], s_Waypoints[chain[i + 1]]);
        }

        static void BuildWaypoints()
        {
            Waypoint("C0", 0, 8.7f); Waypoint("C1", 0, 10.5f); Waypoint("C2", 0, 12f); Waypoint("C3", 0, 16f); Waypoint("C4", 0, 20.6f);
            Waypoint("M0", 2.4f, 10.5f); Waypoint("M1", 3.4f, 12.6f); Waypoint("M2", 6.4f, 13.4f);
            Waypoint("G0", -2.4f, 12f); Waypoint("G1", -3.3f, 13.2f); Waypoint("G4", -6.5f, 13.2f); Waypoint("G2", -6.5f, 10.6f); Waypoint("G3", -6.5f, 17.2f);
            Waypoint("R0", 0, 22.7f); Waypoint("R1", -2.3f, 23.6f); Waypoint("R2", -2.3f, 28.3f); Waypoint("R3", 2.3f, 28.3f); Waypoint("R4", 2.3f, 23.6f);

            Link("C0", "C1", "C2", "C3", "C4", "R0");
            Link("C1", "M0", "M1", "M2");
            Link("C2", "G0", "G1", "G4");
            Link("G4", "G2");
            Link("G4", "G3");
            Link("R0", "R1", "R2", "R3", "R4", "R0");
        }

        // ------------------------------------------------------------------ tug furniture

        static CollectionLocker BuildLocker(MissionDirector director)
        {
            // Against the tug's east wall, hopper opening facing west (-X) into the cabin.
            var root = Empty(s_Props, "Collection Locker", new Vector3(1.45f, 0, -1.3f), null, GameLayers.Environment);
            Transform t = root.transform;
            Vector3 o = t.position;
            Box(t, "Base", o + new Vector3(0, 0.4f, 0), new Vector3(0.9f, 0.8f, 1.5f), M("ContainerBlue"));
            Box(t, "Back", o + new Vector3(0.4f, 1.5f, 0), new Vector3(0.1f, 1.4f, 1.5f), M("ContainerBlue"));
            Box(t, "Side N", o + new Vector3(0, 1.5f, 0.72f), new Vector3(0.9f, 1.4f, 0.06f), M("ContainerBlue"));
            Box(t, "Side S", o + new Vector3(0, 1.5f, -0.72f), new Vector3(0.9f, 1.4f, 0.06f), M("ContainerBlue"));
            Box(t, "Top", o + new Vector3(0, 2.23f, 0), new Vector3(0.9f, 0.06f, 1.5f), M("ContainerBlue"));
            // Shelf dividing the open hopper (below) from the secured display (above).
            Box(t, "Divider", o + new Vector3(0.05f, 1.5f, 0), new Vector3(0.8f, 0.04f, 1.4f), M("Trim"));
            Box(t, "Hopper Lip", o + new Vector3(-0.43f, 0.84f, 0), new Vector3(0.05f, 0.1f, 1.5f), M("Hazard"));
            // Grille bars in front of the secured shelf: visible but out of reach.
            for (int i = 0; i < 9; i++)
                Box(t, "Grille", o + new Vector3(-0.42f, 1.86f, -0.64f + i * 0.16f), new Vector3(0.03f, 0.68f, 0.03f), M("Trim"));

            var zone = Empty(t, "Hopper Zone", new Vector3(0, 1.15f, 0));
            var slots = new List<Transform>();
            for (int row = 0; row < 2; row++)
                for (int col = 0; col < 6; col++)
                    slots.Add(Empty(t, "Slot " + slots.Count, new Vector3(0.05f + row * 0.2f, 1.68f + row * 0.26f, -0.55f + col * 0.22f)).transform);

            var display = Text(t, "Display", "COLLECTION LOCKER", new Vector3(-0.46f, 2.45f, 0), Facing(Vector3.left), 0.9f, new Vector2(1.5f, 0.4f), new Color(0.6f, 1f, 0.7f));
            Text(t, "Hopper Label", "DROP SALVAGE HERE", new Vector3(-0.46f, 0.62f, 0), Facing(Vector3.left), 0.8f, new Vector2(1.5f, 0.2f), new Color(1f, 0.85f, 0.3f)).fontStyle = FontStyles.Bold;
            Box(t, "Hopper Glow", o + new Vector3(0.3f, 0.82f, 0), new Vector3(0.1f, 0.02f, 1.3f), M("GlowTeal"), GameLayers.Default, false);

            var locker = root.AddComponent<CollectionLocker>();
            locker.director = director;
            locker.zone = zone.transform;
            locker.zoneSize = new Vector3(0.75f, 0.62f, 1.36f);
            locker.slots = slots.ToArray();
            locker.display = display;
            return locker;
        }

        /// <summary>
        /// The company store: a real stall in the supply bay with the goods on the counter, each turning
        /// slowly above its own buy button. When a contract starts it folds itself up (awning down,
        /// shutter closed, goods sunk into the counter) and shrinks to a crate; see ShopFolder.
        /// </summary>
        static ShopPanel BuildShop(MissionDirector director)
        {
            var root = Empty(s_Props, "Company Store", new Vector3(-5.42f, 0f, -4.8f), Facing(Vector3.right), GameLayers.Default);
            var stall = Empty(root.transform, "Stall", Vector3.zero, null, GameLayers.Default).transform;
            Prim(stall, "Counter", PrimitiveType.Cube, new Vector3(0, 0.45f, 0), new Vector3(2.4f, 0.9f, 0.62f), M("ContainerBlue"), true, null, GameLayers.Environment);
            CBox(stall, "Counter Top", new Vector3(0, 0.915f, 0), new Vector3(2.5f, 0.04f, 0.7f), M("Trim"), 0.012f);
            CBox(stall, "Counter Stripe", new Vector3(0, 0.84f, -0.315f), new Vector3(2.4f, 0.05f, 0.006f), M("Hazard"), 0.001f);
            Prim(stall, "Back Board", PrimitiveType.Cube, new Vector3(0, 1.62f, 0.37f), new Vector3(2.4f, 1.42f, 0.06f), M("Screen"), true, null, GameLayers.Environment);
            foreach (float x in new[] { -1.22f, 1.22f })
                CBox(stall, "Post", new Vector3(x, 1.16f, 0.33f), new Vector3(0.08f, 2.32f, 0.08f), M("Trim"), 0.015f);
            CBox(stall, "Header Bar", new Vector3(0, 2.26f, 0.33f), new Vector3(2.52f, 0.1f, 0.1f), M("GlowTeal"), 0.02f);
            var header = Text(stall, "Header", "COMPANY STORE", new Vector3(0, 1.98f, 0.335f), Quaternion.identity, 0.62f, new Vector2(2.2f, 0.42f), new Color(0.6f, 1f, 0.9f));
            Text(stall, "Slogan", "everything you need to earn it back", new Vector3(0, 1.2f, 0.335f), Quaternion.identity, 0.3f, new Vector2(2.2f, 0.12f), new Color(1f, 0.85f, 0.4f));

            // Hinged parts: the awning swings down, the shutter rolls shut over the goods.
            var awningHinge = Empty(stall, "Awning Hinge", new Vector3(0, 2.34f, 0.36f), null, GameLayers.Default).transform;
            CBox(awningHinge, "Awning", new Vector3(0, -0.02f, -0.52f), new Vector3(2.62f, 0.04f, 1.04f), M("Hazard"), 0.012f);
            CBox(awningHinge, "Awning Lip", new Vector3(0, -0.08f, -1.03f), new Vector3(2.62f, 0.14f, 0.03f), M("Trim"), 0.008f);
            var shutterHinge = Empty(stall, "Shutter Hinge", new Vector3(0, 2.27f, -0.33f), Quaternion.Euler(90f, 0f, 0f), GameLayers.Default).transform;
            CBox(shutterHinge, "Shutter", new Vector3(0, -0.67f, 0), new Vector3(2.4f, 1.34f, 0.03f), M("Door"), 0.008f);
            for (int i = 0; i < 6; i++)
                CBox(shutterHinge, "Shutter Slat", new Vector3(0, -0.12f - i * 0.22f, -0.018f), new Vector3(2.4f, 0.02f, 0.012f), M("Trim"), 0.004f);

            // The goods, one display each.
            var goods = Empty(stall, "Goods", new Vector3(0, 0.94f, 0), null, GameLayers.Default).transform;
            var x90 = new Vector3(90, 0, 0);
            Transform Display(string name, float x)
            {
                Part(goods, name + " Plinth", Lathe("ShopPlinth", 16, new Vector2(0.17f, 0f), new Vector2(0.15f, 0.04f), new Vector2(0.12f, 0.05f)), new Vector3(x, 0, 0), M("Steel"));
                var turn = Empty(goods, name, new Vector3(x, 0.26f, 0), null, GameLayers.Default);
                turn.AddComponent<SlowSpin>();
                return turn.transform;
            }
            var darts = Display("Tranq Darts", -0.8f);
            Part(darts, "Rack", Tube(0.09f, 0.03f, 14), new Vector3(0, -0.16f, 0), M("TranqBody"));
            for (int i = 0; i < 3; i++)
            {
                float ang = i * Mathf.PI * 2f / 3f;
                var at = new Vector3(Mathf.Cos(ang) * 0.05f, 0, Mathf.Sin(ang) * 0.05f);
                Part(darts, "Dart", Cone(0.012f, 0.002f, 0.2f, 8), at + Vector3.up * 0.0f, M("Chrome"));
                Part(darts, "Dart Tail", Tube(0.014f, 0.05f, 8), at + Vector3.down * 0.11f, M("DartGlow"));
            }
            var dish = Display("Scanner Range", 0f);
            Part(dish, "Dish", Lathe("ScannerDish", 16, new Vector2(0.012f, 0f), new Vector2(0.044f, 0.03f), new Vector2(0.047f, 0.03f), new Vector2(0.016f, -0.004f)), new Vector3(0, 0.02f, 0), M("PaintOrange"), new Vector3(60, 0, 0)).transform.localScale = Vector3.one * 2.4f;
            Part(dish, "Module", Tube(0.05f, 0.14f, 12), new Vector3(0, -0.12f, 0), M("ToolBody"));
            Part(dish, "Antenna", Tube(0.004f, 0.2f, 6), new Vector3(0.04f, 0.02f, -0.03f), M("Chrome"));
            var coolant = Display("Cutter Coolant", 0.8f);
            Part(coolant, "Canister", Lathe("CutterTank", 12, new Vector2(0f, -0.055f), new Vector2(0.018f, -0.05f), new Vector2(0.022f, -0.035f), new Vector2(0.022f, 0.035f), new Vector2(0.018f, 0.05f), new Vector2(0f, 0.055f)),
                new Vector3(0, -0.02f, 0), M("PaintOrange")).transform.localScale = Vector3.one * 3f;
            for (int i = 0; i < 4; i++) Part(coolant, "Fin", Tube(0.085f, 0.008f, 14), new Vector3(0, -0.1f + i * 0.045f, 0), M("Steel"));
            Part(coolant, "Valve", Tube(0.016f, 0.06f, 8), new Vector3(0, 0.17f, 0), M("Brass"));

            var size = new Vector2(0.68f, 0.26f);
            var panel = root.AddComponent<ShopPanel>();
            panel.director = director;
            panel.header = header;
            panel.tranqButton = Button(stall, "Buy Tranq Dart", new Vector3(-0.8f, 0.5f, -0.325f), Quaternion.identity, size, "TRANQ DART", M("ButtonGreen"), 0.27f);
            panel.scannerButton = Button(stall, "Buy Scanner Range", new Vector3(0f, 0.5f, -0.325f), Quaternion.identity, size, "SCANNER RANGE+", M("ButtonBlue"), 0.27f);
            panel.cutterButton = Button(stall, "Buy Cutter Coolant", new Vector3(0.8f, 0.5f, -0.325f), Quaternion.identity, size, "CUTTER COOLANT", M("ButtonAmber"), 0.27f);

            var folder = root.AddComponent<ShopFolder>();
            folder.director = director;
            folder.stall = stall;
            folder.hinges = new[] { awningHinge, shutterHinge };
            folder.foldedAngles = new[] { new Vector3(-88f, 0f, 0f), new Vector3(-90f, 0f, 0f) };
            folder.goods = goods;
            return panel;
        }

        // ------------------------------------------------------------------ map art pass

        static void Beams(float x0, float x1, float z0, float z1, float height, bool alongX)
        {
            // Ceiling cross-beams roughly every 2.2 m, spanning the short side.
            float length = alongX ? x1 - x0 : z1 - z0;
            int count = Mathf.Max(1, Mathf.RoundToInt(length / 2.2f));
            for (int i = 0; i <= count; i++)
            {
                float t = Mathf.Lerp(0.15f, length - 0.15f, i / (float)count);
                Vector3 centre = alongX ? new Vector3(x0 + t, height - 0.07f, (z0 + z1) * 0.5f) : new Vector3((x0 + x1) * 0.5f, height - 0.07f, z0 + t);
                Vector3 size = alongX ? new Vector3(0.16f, 0.14f, z1 - z0 - 0.24f) : new Vector3(x1 - x0 - 0.24f, 0.14f, 0.16f);
                Box(s_Props, "Ceiling Beam", centre, size, M("Trim"), GameLayers.Default, false);
            }
        }

        static void Vent(Vector3 position, Vector3 outward, float width = 0.7f, float height = 0.35f)
        {
            var root = Empty(s_Props, "Vent", position, Facing(outward), GameLayers.Default).transform;
            CBox(root, "Frame", new Vector3(0, 0, 0.004f), new Vector3(width, height, 0.04f), M("Trim"), 0.01f);
            Prim(root, "Dark", PrimitiveType.Cube, new Vector3(0, 0, -0.012f), new Vector3(width - 0.08f, height - 0.08f, 0.006f), M("Vent"));
            int slats = Mathf.Max(3, Mathf.RoundToInt(height / 0.06f));
            for (int i = 0; i < slats; i++)
                CBox(root, "Slat", new Vector3(0, -height * 0.5f + 0.05f + i * (height - 0.1f) / (slats - 1), -0.02f), new Vector3(width - 0.08f, 0.012f, 0.03f), M("Steel"), 0.003f, new Vector3(35, 0, 0));
        }

        static void Window(Vector3 position, Vector3 outward, float width, float height)
        {
            var root = Empty(s_Props, "Window", position, Facing(outward), GameLayers.Default).transform;
            Prim(root, "Stars", PrimitiveType.Cube, new Vector3(0, 0, 0.002f), new Vector3(width, height, 0.004f), M("Stars"));
            CBox(root, "Frame Top", new Vector3(0, height * 0.5f + 0.04f, -0.01f), new Vector3(width + 0.16f, 0.08f, 0.06f), M("Trim"), 0.012f);
            CBox(root, "Frame Bottom", new Vector3(0, -height * 0.5f - 0.04f, -0.01f), new Vector3(width + 0.16f, 0.08f, 0.06f), M("Trim"), 0.012f);
            CBox(root, "Frame L", new Vector3(-width * 0.5f - 0.04f, 0, -0.01f), new Vector3(0.08f, height, 0.06f), M("Trim"), 0.012f);
            CBox(root, "Frame R", new Vector3(width * 0.5f + 0.04f, 0, -0.01f), new Vector3(0.08f, height, 0.06f), M("Trim"), 0.012f);
            int bars = Mathf.Max(1, Mathf.RoundToInt(width / 0.9f) - 1);
            for (int i = 1; i <= bars; i++)
                CBox(root, "Mullion", new Vector3(-width * 0.5f + i * width / (bars + 1), 0, -0.01f), new Vector3(0.05f, height, 0.05f), M("Trim"), 0.01f);
        }

        static void Barrel(Vector3 floorPosition, string mat = "Barrel")
        {
            var go = Empty(s_Props, "Barrel", floorPosition + Vector3.up * 0.45f, null, GameLayers.Environment);
            Part(go.transform, "Drum", Lathe("Barrel", 14, new Vector2(0.24f, -0.45f), new Vector2(0.27f, -0.42f), new Vector2(0.27f, -0.16f), new Vector2(0.285f, -0.14f),
                new Vector2(0.285f, -0.1f), new Vector2(0.27f, -0.08f), new Vector2(0.27f, 0.12f), new Vector2(0.285f, 0.14f), new Vector2(0.285f, 0.18f), new Vector2(0.27f, 0.2f),
                new Vector2(0.27f, 0.42f), new Vector2(0.24f, 0.45f)), Vector3.zero, M(mat));
            Part(go.transform, "Cap", Tube(0.05f, 0.03f, 8), new Vector3(0.12f, 0.46f, 0.05f), M("Steel"));
            var col = go.AddComponent<CapsuleCollider>();
            col.radius = 0.28f;
            col.height = 0.92f;
            go.isStatic = true;
            foreach (Transform child in go.transform) child.gameObject.isStatic = true;
        }

        static void LockerRow(Vector3 floorCentre, Vector3 outward, int count)
        {
            var root = Empty(s_Props, "Lockers", floorCentre, Quaternion.LookRotation(outward, Vector3.up), GameLayers.Environment).transform;
            float width = 0.5f;
            var col = root.gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(count * width, 1.9f, 0.45f);
            col.center = new Vector3(0, 0.95f, 0);
            for (int i = 0; i < count; i++)
            {
                float x = (i - (count - 1) * 0.5f) * width;
                CBox(root, "Cabinet", new Vector3(x, 0.95f, 0), new Vector3(width - 0.02f, 1.9f, 0.44f), M("Trim"), 0.012f);
                CBox(root, "Door", new Vector3(x, 0.98f, 0.225f), new Vector3(width - 0.07f, 1.72f, 0.02f), M("LockerDoor"), 0.008f);
                for (int v = 0; v < 3; v++) CBox(root, "Door Vent", new Vector3(x, 1.62f - v * 0.05f, 0.237f), new Vector3(width - 0.2f, 0.012f, 0.006f), M("Vent"), 0.002f);
                CBox(root, "Handle", new Vector3(x + width * 0.3f, 1.0f, 0.243f), new Vector3(0.02f, 0.12f, 0.02f), M("Steel"), 0.005f);
                CBox(root, "Name Plate", new Vector3(x, 1.38f, 0.237f), new Vector3(0.16f, 0.05f, 0.004f), M("Poster"), 0.001f);
            }
        }

        static void Poster(string text, Vector3 position, Vector3 outward, float width = 0.8f, float height = 0.5f, float fontSize = 0.5f)
        {
            var root = Empty(s_Props, "Poster", position, Facing(outward), GameLayers.Default).transform;
            CBox(root, "Board", new Vector3(0, 0, 0.004f), new Vector3(width, height, 0.012f), M("Poster"), 0.004f);
            CBox(root, "Hazard Edge", new Vector3(0, -height * 0.5f + 0.03f, -0.003f), new Vector3(width, 0.05f, 0.004f), M("Hazard"), 0.001f);
            Text(root, "Text", text, new Vector3(0, 0.03f, -0.004f), Quaternion.identity, fontSize, new Vector2(width - 0.06f, height - 0.12f), new Color(0.12f, 0.1f, 0.08f)).fontStyle = FontStyles.Bold;
        }

        static void Dust(string name, Vector3 centre, Vector3 size, int count)
        {
            var go = Empty(s_Props, name, centre, null, GameLayers.Default);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.loop = true;
            main.prewarm = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(10f, 18f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.01f, 0.05f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.004f, 0.012f);
            main.startColor = new Color(0.75f, 0.8f, 0.85f, 1f);
            main.maxParticles = count;
            main.gravityModifier = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var emission = ps.emission;
            emission.rateOverTime = count / 14f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = size;
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = M("Spark");
            Cheap(renderer);
        }

        static void BuildMapDetail()
        {
            // --- structure: ceiling beams in every room
            Beams(-2, 2, -3, 2, H, false);
            Beams(-2, 2, 3.6f, 7.6f, H, false);
            Beams(-1.3f, 1.3f, 7.6f, 21.6f, H, false);
            Beams(-10.3f, -1.3f, 9.6f, 18.6f, H, true);
            Beams(1.3f, 8.3f, 8.6f, 15.6f, H, true);
            Beams(-4.5f, 4.5f, 21.6f, 29.6f, H, false);

            // --- walkways: dark anti-slip strips with hazard edges down the middle of the routes
            void Walkway(float x0, float x1, float z0, float z1)
            {
                Box(s_Props, "Walkway", new Vector3((x0 + x1) * 0.5f, 0.007f, (z0 + z1) * 0.5f), new Vector3(x1 - x0, 0.012f, z1 - z0), M("Walkway"), GameLayers.Default, false);
                bool alongZ = (z1 - z0) > (x1 - x0);
                foreach (float side in new[] { -1f, 1f })
                {
                    Vector3 c = alongZ ? new Vector3((side < 0 ? x0 : x1), 0.009f, (z0 + z1) * 0.5f) : new Vector3((x0 + x1) * 0.5f, 0.009f, (side < 0 ? z0 : z1));
                    Vector3 sz = alongZ ? new Vector3(0.07f, 0.012f, z1 - z0) : new Vector3(x1 - x0, 0.012f, 0.07f);
                    Box(s_Props, "Walkway Edge", c, sz, M("Hazard"), GameLayers.Default, false);
                }
            }
            Walkway(-0.55f, 0.55f, 7.9f, 21.3f);
            Walkway(-0.55f, 0.55f, 3.9f, 7.3f);
            Walkway(-9.6f, -1.6f, 12.3f, 13.9f);
            Walkway(1.6f, 6.6f, 10.0f, 11.0f);
            Walkway(-0.55f, 0.55f, 21.9f, 24.5f);
            Walkway(-0.5f, 0.5f, -2.0f, 1.7f);

            // --- vents
            Vent(new Vector3(-1.3f + T * 0.5f + 0.01f, 2.25f, 15.2f), Vector3.right);
            Vent(new Vector3(1.3f - T * 0.5f - 0.01f, 2.25f, 18.6f), Vector3.left);
            Vent(new Vector3(-6f, 2.25f, 9.6f + T * 0.5f + 0.01f), Vector3.forward, 1.0f);
            Vent(new Vector3(-10.3f + T * 0.5f + 0.01f, 0.5f, 16.0f), Vector3.right, 0.6f, 0.4f);
            Vent(new Vector3(8.3f - T * 0.5f - 0.01f, 2.25f, 10.2f), Vector3.left);
            Vent(new Vector3(-4.5f + T * 0.5f + 0.01f, 2.25f, 23.2f), Vector3.right);
            Vent(new Vector3(4.5f - T * 0.5f - 0.01f, 2.25f, 28.0f), Vector3.left);
            Vent(new Vector3(-2f + T * 0.5f + 0.01f, 2.3f, 5.6f), Vector3.right);
            Vent(new Vector3(2f - T * 0.5f - 0.01f, 2.35f, -2.4f), Vector3.left, 0.6f, 0.3f);

            // --- windows onto the stars
            Window(new Vector3(-10.3f + T * 0.5f + 0.015f, 1.65f, 12.6f), Vector3.right, 2.6f, 1.0f);
            Window(new Vector3(-1.6f, 1.7f, 29.6f - T * 0.5f - 0.015f), Vector3.back, 2.4f, 0.9f);
            Window(new Vector3(8.3f - T * 0.5f - 0.015f, 1.7f, 13.9f), Vector3.left, 1.4f, 0.8f);
            Window(new Vector3(2f - T * 0.5f - 0.015f, 1.7f, 5.6f), Vector3.left, 1.6f, 0.8f);

            // --- lockers and stores
            LockerRow(new Vector3(-2f + T * 0.5f + 0.24f, 0, 4.9f), Vector3.right, 3);
            LockerRow(new Vector3(6.9f, 0, 8.6f + T * 0.5f + 0.24f), Vector3.forward, 4);
            Barrel(new Vector3(-9.5f, 0, 15.6f));
            Barrel(new Vector3(-8.9f, 0, 15.95f), "BarrelBlue");
            Barrel(new Vector3(-2.6f, 0, 10.4f));
            Barrel(new Vector3(7.7f, 0, 14.9f), "BarrelBlue");
            Barrel(new Vector3(-3.9f, 0, 22.4f));
            Barrel(new Vector3(-1.0f, 0, 29.1f), "BarrelBlue");
            Container(new Vector3(-6.3f, 0, 18.0f), new Vector3(1.4f, 0.9f, 0.9f), "ContainerBlue");
            Container(new Vector3(-9.75f, 0, 11.0f), new Vector3(0.9f, 0.9f, 0.9f));

            // --- cables hanging from the ceiling
            Pipe("Hanging Cable A", new Vector3(0.4f, H, 13.4f), new Vector3(0.75f, 2.15f, 13.7f), 0.012f, "Cable");
            Pipe("Hanging Cable B", new Vector3(-0.3f, H, 17.2f), new Vector3(-0.9f, 2.0f, 17.0f), 0.012f, "Cable");
            Pipe("Hanging Cable C", new Vector3(-7.2f, H, 16.6f), new Vector3(-7.5f, 1.9f, 16.2f), 0.014f, "Cable");
            Pipe("Hanging Cable D", new Vector3(-3.0f, H, 11.2f), new Vector3(-2.7f, 2.05f, 11.6f), 0.012f, "Cable");
            Pipe("Hanging Cable E", new Vector3(5.8f, H, 12.8f), new Vector3(6.2f, 2.1f, 13.1f), 0.012f, "Cable");
            Pipe("Hanging Cable F", new Vector3(-2.9f, H, 27.0f), new Vector3(-2.5f, 2.0f, 26.6f), 0.014f, "Cable");
            Pipe("Wall Conduit Cargo", new Vector3(-10.3f + T * 0.5f + 0.06f, 2.3f, 14.2f), new Vector3(-10.3f + T * 0.5f + 0.06f, 2.3f, 18.3f), 0.03f, "Steel");
            Pipe("Wall Conduit Reactor", new Vector3(-4.1f, 2.4f, 29.6f - T * 0.5f - 0.07f), new Vector3(-2.9f, 2.4f, 29.6f - T * 0.5f - 0.07f), 0.035f, "Steel");

            // --- reactor: a glowing column rising out of the housing
            Part(s_Props, "Reactor Column", Tube(0.32f, H - 2.4f, 16), new Vector3(0, (H + 2.4f) * 0.5f, 25.9f), M("ReactorGlow")).isStatic = true;
            for (int i = 0; i < 2; i++)
                Part(s_Props, "Reactor Column Ring", Torus(0.36f, 0.04f, 18, 6), new Vector3(0, 2.48f + i * 0.2f, 25.9f), M("Steel")).isStatic = true;

            // --- company notices
            Poster("DAYS SINCE LAST\nINCIDENT:  0", new Vector3(1.3f - T * 0.5f - 0.012f, 1.55f, 13.6f), Vector3.left);
            Poster("OXYGEN IS A\nPRIVILEGE", new Vector3(-1.3f + T * 0.5f + 0.012f, 1.55f, 19.4f), Vector3.right);
            Poster("LOST? FOLLOW THE\nYELLOW RAILS", new Vector3(-5.8f, 1.6f, 18.6f - T * 0.5f - 0.012f), Vector3.back, 1.0f);
            Poster("REPORT STRANGE\nNOISES. QUIETLY.", new Vector3(4.1f, 1.6f, 15.6f - T * 0.5f - 0.012f), Vector3.back, 0.9f);
            Poster("SALVAGE IS\nCOMPANY PROPERTY.\nSO ARE YOU.", new Vector3(-2f + T * 0.5f + 0.012f, 1.55f, -2.45f), Vector3.right, 0.8f, 0.55f, 0.4f);

            // --- floor stencils
            void Stencil(string text, Vector3 position, float yaw)
            {
                var t = Text(s_Signs, "Floor Stencil " + text, text, position, Quaternion.Euler(90f, yaw, 0f), 2.2f, new Vector2(3f, 0.5f), new Color(0.85f, 0.7f, 0.15f));
                t.fontStyle = FontStyles.Bold;
            }
            Stencil("AIRLOCK", new Vector3(0, 0.016f, 4.6f), 0f);
            Stencil("CARGO", new Vector3(-3.0f, 0.016f, 13.1f), 90f);
            Stencil("MAINT", new Vector3(3.0f, 0.016f, 10.5f), -90f);
            Stencil("REACTOR", new Vector3(0, 0.016f, 22.8f), 0f);

            // --- drifting dust in the big spaces
            Dust("Dust Corridor", new Vector3(0, 1.4f, 14.6f), new Vector3(2.2f, 2.2f, 13f), 50);
            // --- the crew's last entries, left on the walls
            Poster("LOG 41\nSomething is in the vents.\nMaintenance says rats.\nThere are no rats on a ship.", new Vector3(1.3f - T * 0.5f - 0.012f, 1.5f, 17.0f), Vector3.left, 0.95f, 0.6f, 0.36f);
            Poster("LOG 44\nOkafor went to check the reactor.\nThat was two shifts ago.", new Vector3(-10.3f + T * 0.5f + 0.012f, 1.5f, 14.4f), Vector3.right, 0.95f, 0.55f, 0.36f);
            Poster("LOG 47\nKeep your voice down.\nIt comes to the noise.", new Vector3(2.0f, 1.5f, 21.6f + T * 0.5f + 0.012f), Vector3.forward, 0.9f, 0.5f, 0.38f);

            // --- what lives here has been busy: webs in the corners, things wrapped and hung from the ceiling
            Web(new Vector3(-10.2f, H - 0.05f, 18.5f), Vector3.right, Vector3.back, Vector3.down, 1.5f);
            Web(new Vector3(-4.4f, H - 0.05f, 29.5f), Vector3.right, Vector3.back, Vector3.down, 1.6f);
            EggSacs(new Vector3(-4.5f + T * 0.5f, 1.9f, 27.6f), Vector3.right, 7, 61);
            EggSacs(new Vector3(8.3f - T * 0.5f, 1.8f, 10.4f), Vector3.left, 6, 63);
            Tripwire("a_corridor_mid", new Vector3(-1.2f, 1.3f, 13.5f), new Vector3(1.2f, 1.1f, 13.5f));
            Tripwire("a_corridor_far", new Vector3(-1.2f, 0.7f, 18.2f), new Vector3(1.2f, 1.8f, 18.2f));
            Tripwire("a_reactor_entry", new Vector3(-0.9f, 1.2f, 22.4f), new Vector3(0.9f, 1.4f, 22.4f));

            Dust("Dust Cargo", new Vector3(-5.8f, 1.4f, 14.1f), new Vector3(8f, 2.2f, 8f), 70);
            Dust("Dust Reactor", new Vector3(0, 1.4f, 25.6f), new Vector3(8f, 2.2f, 7f), 60);
            Dust("Dust Maintenance", new Vector3(4.8f, 1.4f, 12.1f), new Vector3(6f, 2.2f, 6f), 40);
        }

        static MissionBoard BuildMissionBoard(MissionDirector director)
        {
            // On the tug's west wall, facing east (+X).
            var root = Empty(s_Props, "Mission Board", new Vector3(-2f + T * 0.5f + 0.02f, 1.5f, -1.0f), Facing(Vector3.right), GameLayers.Default);
            Prim(root.transform, "Back", PrimitiveType.Cube, new Vector3(0, 0, 0.012f), new Vector3(1.9f, 1.5f, 0.02f), M("Screen"));
            var title = Text(root.transform, "Title", "SALVAGE CONTRACT", new Vector3(0, 0.62f, -0.002f), Quaternion.identity, 1.1f, new Vector2(1.8f, 0.18f), new Color(1f, 0.85f, 0.3f));
            title.fontStyle = FontStyles.Bold;
            var body = Text(root.transform, "Body", "", new Vector3(0, 0.1f, -0.002f), Quaternion.identity, 0.56f, new Vector2(1.78f, 0.85f), new Color(0.85f, 0.95f, 1f), TextAlignmentOptions.TopLeft);

            var board = root.AddComponent<MissionBoard>();
            board.director = director;
            board.title = title;
            board.body = body;
            board.primaryButton = Button(root.transform, "Primary Button", new Vector3(-0.45f, -0.56f, 0), Quaternion.identity, new Vector2(0.8f, 0.2f), "START CONTRACT", M("ButtonGreen"), 0.6f);
            board.upgradeButton = Button(root.transform, "Upgrade Button", new Vector3(0.45f, -0.56f, 0), Quaternion.identity, new Vector2(0.8f, 0.2f), "BUY O2 TANK+", M("ButtonAmber"), 0.42f);
            return board;
        }

        static TextMeshPro BuildTutorialBoard(out PushButton skipStep, out PushButton skipAll)
        {
            var root = Empty(s_Props, "Tutorial Board", new Vector3(-2f + T * 0.5f + 0.02f, 1.5f, 0.95f), Facing(Vector3.right), GameLayers.Default);
            Prim(root.transform, "Back", PrimitiveType.Cube, new Vector3(0, 0, 0.012f), new Vector3(1.7f, 1.5f, 0.02f), M("Screen"));
            CBox(root.transform, "Header Bar", new Vector3(0, 0.735f, 0.006f), new Vector3(1.7f, 0.03f, 0.03f), M("GlowWarm"), 0.006f);
            skipStep = Button(root.transform, "Skip Step", new Vector3(-0.42f, -0.6f, 0), Quaternion.identity, new Vector2(0.72f, 0.16f), "SKIP STEP", M("ButtonBlue"), 0.5f);
            skipAll = Button(root.transform, "Skip All", new Vector3(0.42f, -0.6f, 0), Quaternion.identity, new Vector2(0.72f, 0.16f), "SKIP ALL", M("ButtonAmber"), 0.5f);
            return Text(root.transform, "Text", "", new Vector3(0, 0.1f, -0.002f), Quaternion.identity, 0.5f, new Vector2(1.6f, 1.15f), new Color(0.75f, 1f, 0.8f), TextAlignmentOptions.TopLeft);
        }
    }
}

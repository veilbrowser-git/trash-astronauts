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
    /// Builds the one mission scene: the salvage tug, its docking collar and airlock, and the four wrecks
    /// (Builder.Maps.cs, Builder.Freighter.cs, Builder.Cave.cs) that take turns docking to it.
    ///
    /// Top-down layout (metres, +Z is "into the wreck"):
    ///   Tug            x[-2,2]      z[-3,2]
    ///   Docking collar x[-0.9,0.9]  z[2,3.6]
    ///   Airlock        x[-2,2]      z[3.6,7.6]
    ///   Every wreck joins the airlock's north wall at z = 7.6.
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
            RenderSettings.ambientLight = new Color(0.085f, 0.1f, 0.14f); // darker than before: the flashlight matters now
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
            BuildRails();
            BuildTeleportAndAnchors();
            var practicePanel = BuildPracticePlate();
            BuildDecor();
            BuildMapDetail();

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
            // Big enough for the largest wreck (they all dock at the same airlock) and the tug's flight deck:
            // x[-25,21] y[-1.5,9] z[-8,61]. Lost items and players outside it are brought back.
            var bounds = Empty(missionGo.transform, "Playable Bounds", new Vector3(-2f, 3.75f, 26.5f)).AddComponent<PlayableBounds>();
            bounds.size = new Vector3(46f, 10.5f, 69f);
            Exclusion(missionGo.transform, "No Drone: Airlock", new Vector3(0, 1.4f, 4.8f), new Vector3(4.2f, 3.2f, 5.7f));

            var hubSpawn = Empty(missionGo.transform, "Hub Spawn", new Vector3(0.4f, 0, -0.9f), Quaternion.Euler(0, -90f, 0));
            var lostAndFound = Empty(missionGo.transform, "Lost And Found", new Vector3(-1.2f, 1.2f, 5.6f));
            var salvageRoot = Empty(missionGo.transform, "Salvage (runtime)", Vector3.zero);

            var locker = BuildLocker(director);
            var board = BuildMissionBoard(director);
            var tutorialText = BuildTutorialBoard(out var skipStep, out var skipAll);
            var settings = BuildSettingsPanel(s_Props, "Suit Settings Console", new Vector3(1.0f, 1.35f, -2.88f), Facing(Vector3.forward), 1.6f);
            settings.director = director;
            var shop = BuildShop(director);
            BuildSuitLocker(director);
            var net = AddNetSession(missionGo, director);
            BuildLobbyBoard(net);

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
            // All of them wait at the docked wreck's creature dock; the director switches on one per contract.
            // Their routes, dock and doors are filled in from the first wreck once it is built (below).
            var creatures = new List<EnemyDrone>();
            for (int i = 0; i < s_CreaturePrefabs.Length; i++)
            {
                var droneGo = (GameObject)PrefabUtility.InstantiatePrefab(s_CreaturePrefabs[i]);
                var creature = droneGo.GetComponent<EnemyDrone>();
                creature.director = director;
                droneGo.SetActive(i == 0);
                creatures.Add(creature);
            }

            // --- player
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(s_RigPrefab);
            rig.transform.SetPositionAndRotation(hubSpawn.transform.position, hubSpawn.transform.rotation);
            BuildDustMotes(rig.transform.position);

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
            director.enemies = creatures.ToArray();

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

            // --- wrecks: four of them, each built at its own (1.5x since the bigger-maps update) size. One docks per contract.
            var sharedLights = s_WreckLights.Where(l => l.transform.position.z < JoinZ).ToArray();
            var sharedGlow = s_WreckGlow.Where(r => r.transform.position.z < JoinZ).ToArray();
            var freighter = BuildFreighter(sharedLights, sharedGlow);
            var barge = BuildOreBarge(sharedLights, sharedGlow);
            var survey = BuildSurveyShip(sharedLights, sharedGlow);
            var cave = BuildLavaTube(sharedLights, sharedGlow);
            DressWreck(freighter, "Hauler");
            DressWreck(barge, "Barge");
            DressWreck(survey, "Survey");
            DressTugView();
            director.spawnPoints = freighter.spawnPoints;
            director.cuttables = freighter.cuttables;
            director.wreckDoors = new[] { doorA }.Concat(freighter.doors).ToArray();
            foreach (var creature in creatures)
            {
                creature.dock = freighter.dock;
                creature.graph = freighter.graph;
                creature.patrolRoute = freighter.patrolRoute;
                creature.doors = freighter.creatureDoors;
                creature.transform.SetPositionAndRotation(freighter.dock.position, freighter.dock.rotation);
                // Set long after the instance was made (asset saves in between): record them, or they are lost.
                PrefabUtility.RecordPrefabInstancePropertyModifications(creature);
                PrefabUtility.RecordPrefabInstancePropertyModifications(creature.transform);
            }
            freighter.gameObject.SetActive(true); // the first wreck is docked while in the hub
            director.maps = new[] { freighter, barge, survey, cave };
            director.sharedCuttables = new[] { practicePanel };
            director.sharedDoors = new[] { doorA };
            // Every web belongs to a wreck today; one strung in the tug or the airlock would go here.
            director.sharedTripwires = new WebTripwire[0];
            director.blackout = blackout;

            blackout.lights = freighter.lights;
            blackout.glowStrips = freighter.glowStrips;
            blackout.globalAmbience = new[] { ambience };
            blackout.ambience = new[] { ambience }.Concat(freighter.ambience).ToArray();

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
                // Long walls are built in pieces: URP on Quest lights each renderer with at most four lamps, so
                // one 30 m box would only ever show four of the lights along it.
                int pieces = Mathf.Max(1, Mathf.CeilToInt((s1 - s0) / MaxPiece));
                if (pieces > 1)
                {
                    for (int i = 0; i < pieces; i++) Piece(Mathf.Lerp(s0, s1, i / (float)pieces), Mathf.Lerp(s0, s1, (i + 1) / (float)pieces), y0, y1);
                    return;
                }
                Piece(s0, s1, y0, y1);
            }

            void Piece(float s0, float s1, float y0, float y1)
            {
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

        /// <summary>Longest wall piece, floor or ceiling tile in metres (see Wall: four lights per renderer on Quest).</summary>
        const float MaxPiece = 6.5f;

        static void FloorAndCeiling(string name, float x0, float x1, float z0, float z1, float height, Material floorMat)
        {
            var parent = Empty(s_Level, name, Vector3.zero, null, GameLayers.Environment).transform;
            int nx = Mathf.Max(1, Mathf.CeilToInt((x1 - x0) / MaxPiece)), nz = Mathf.Max(1, Mathf.CeilToInt((z1 - z0) / MaxPiece));
            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < nz; j++)
                {
                    float a0 = Mathf.Lerp(x0, x1, i / (float)nx), a1 = Mathf.Lerp(x0, x1, (i + 1) / (float)nx);
                    float b0 = Mathf.Lerp(z0, z1, j / (float)nz), b1 = Mathf.Lerp(z0, z1, (j + 1) / (float)nz);
                    Vector3 size = new Vector3(a1 - a0, 0.2f, b1 - b0);
                    Box(parent, "Floor", new Vector3((a0 + a1) * 0.5f, -0.1f, (b0 + b1) * 0.5f), size, floorMat);
                    Box(parent, "Ceiling", new Vector3((a0 + a1) * 0.5f, height + 0.1f, (b0 + b1) * 0.5f), size, M("Ceiling"));
                }
            }
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

            // Lights: a handful of unshadowed point lights plus emissive strips. (Each wreck brings its own.)
            PointLight(s_Lights, "Tug Light A", new Vector3(0, 2.4f, -1.5f), new Color(1f, 0.9f, 0.75f), 5f, 6f);
            PointLight(s_Lights, "Tug Light B", new Vector3(0, 2.4f, 1f), new Color(1f, 0.9f, 0.75f), 3.5f, 5f);
            WreckLight(            PointLight(s_Lights, "Airlock Light", new Vector3(0, 2.4f, 5.6f), new Color(1f, 0.25f, 0.2f), 3.5f, 6f));

            Strip("Tug Strip", new Vector3(0, H - 0.03f, -0.5f), new Vector3(0.12f, 0.03f, 3.6f), "GlowWarm");
            Strip("Airlock Strip", new Vector3(0, H - 0.03f, 5.6f), new Vector3(2.4f, 0.03f, 0.1f), "GlowRed");
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

            Anchor("Tug", 0f, -0.8f);
            Anchor("Airlock", 0f, 5.6f);
        }

        // ------------------------------------------------------------------ cuttables

        /// <summary>
        /// One stretch of seam from <paramref name="a"/> to <paramref name="b"/> (panel-local, on the plate's face): a
        /// CutPoint whose local X runs along the stretch and local Z out of the face, with a trigger the cutter's
        /// overlap finds from a little in front of the metal and just past the stretch's ends.
        /// </summary>
        static CutPoint MakeCutPoint(Transform parent, Vector2 a, Vector2 b)
        {
            Vector2 dir2 = (b - a).normalized;
            Vector3 dir = new Vector3(dir2.x, dir2.y, 0f);
            Vector2 mid = (a + b) * 0.5f;
            var rotation = Quaternion.LookRotation(Vector3.forward, Vector3.Cross(Vector3.forward, dir));
            var go = Empty(parent, "Cut Point", new Vector3(mid.x, mid.y, SeamFaceZ), rotation, GameLayers.CutPoint);
            float length = Vector2.Distance(a, b);
            var col = go.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.size = new Vector3(length + 0.05f, 0.09f, 0.14f);
            col.center = new Vector3(0f, 0f, 0.04f);
            var point = go.AddComponent<CutPoint>();
            point.length = length;
            return point;
        }

        /// <summary>Panel-local depth of the plate's front face, where the seam runs (the sheet is 4 cm thick, centred).</summary>
        const float SeamFaceZ = 0.021f;
        /// <summary>Longest stretch of seam that is one network cut point.</summary>
        const float SeamStretch = 0.34f;

        /// <summary>
        /// The seam that holds a plate, from the weld anchors a panel was designed with. Each side of the plate with
        /// two or more anchors near it is welded between its outermost anchors (along the anchors' line); an anchor
        /// near no welded side is a clamp, cut straight across the plate's short way. Runs go round the plate in
        /// order (top, right, bottom, left) and are split into stretches of at most <see cref="SeamStretch"/>.
        /// </summary>
        static List<(Vector2 a, Vector2 b)> SeamStretches(Vector2 plateSize, Vector2[] anchors)
        {
            float hx = plateSize.x * 0.5f, hy = plateSize.y * 0.5f;
            float tol = Mathf.Min(0.06f, 0.3f * Mathf.Min(hx, hy));
            var runs = new List<(Vector2 a, Vector2 b)>();
            var used = new bool[anchors.Length];
            // side: 0 top (+y, runs -x to +x), 1 right (+x, +y to -y), 2 bottom (-y, +x to -x), 3 left (-x, -y to +y)
            for (int side = 0; side < 4; side++)
            {
                bool horizontal = side == 0 || side == 2;
                float edge = side == 0 ? hy : side == 1 ? hx : side == 2 ? -hy : -hx;
                float min = float.MaxValue, max = float.MinValue, perp = 0f;
                int near = 0;
                for (int i = 0; i < anchors.Length; i++)
                {
                    float d = horizontal ? anchors[i].y : anchors[i].x;
                    if (Mathf.Abs(d - edge) > tol) continue;
                    float t = horizontal ? anchors[i].x : anchors[i].y;
                    min = Mathf.Min(min, t);
                    max = Mathf.Max(max, t);
                    perp += d;
                    near++;
                }
                if (near < 2 || max - min < 0.05f) continue;
                perp /= near;
                for (int i = 0; i < anchors.Length; i++)
                {
                    float d = horizontal ? anchors[i].y : anchors[i].x;
                    if (Mathf.Abs(d - edge) <= tol) used[i] = true;
                }
                Vector2 from = horizontal ? new Vector2(min, perp) : new Vector2(perp, max);
                Vector2 to = horizontal ? new Vector2(max, perp) : new Vector2(perp, min);
                if (side == 2 || side == 3) (from, to) = (to, from);
                runs.Add((from, to));
            }
            for (int i = 0; i < anchors.Length; i++)
            {
                if (used[i]) continue;
                // A clamp: cut across the plate's short way through the anchor.
                Vector2 p = anchors[i];
                runs.Add(hx >= hy ? (new Vector2(p.x, hy), new Vector2(p.x, -hy)) : (new Vector2(-hx, p.y), new Vector2(hx, p.y)));
            }

            var stretches = new List<(Vector2 a, Vector2 b)>();
            foreach (var run in runs)
            {
                float len = Vector2.Distance(run.a, run.b);
                int n = Mathf.Max(1, Mathf.CeilToInt(len / SeamStretch - 0.001f));
                for (int k = 0; k < n; k++) stretches.Add((Vector2.Lerp(run.a, run.b, (float)k / n), Vector2.Lerp(run.a, run.b, (float)(k + 1) / n)));
            }
            return stretches;
        }

        /// <summary>
        /// A cuttable plate. The root's +Z points out of the surface toward the player, so the freed
        /// plate drifts toward them. <paramref name="cutLocal"/> are the weld anchors the panel was designed with;
        /// the seam that is actually cut runs between them along the plate's edges (<see cref="SeamStretches"/>).
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
            // A sandwich panel of painted aluminium: about 18 kg per square metre of face.
            body.mass = Mathf.Clamp(plateSize.x * plateSize.y * 18f, 4f, 30f);
            RealPhysics.ConfigureLooseBody(body);
            foreach (var plateCollider in plateGo.GetComponentsInChildren<Collider>()) plateCollider.sharedMaterial = SurfaceMaterial(SurfaceKind.PaintedMetal);
            var impact = plateGo.AddComponent<ImpactNoiseEmitter>();
            impact.heavy = true;
            impact.surface = SurfaceKind.PaintedMetal;

            var points = new List<CutPoint>();
            foreach (var stretch in SeamStretches(plateSize, cutLocal)) points.Add(MakeCutPoint(root.transform, stretch.a, stretch.b));

            var label = Text(root.transform, "Label", displayName, new Vector3(0, plateSize.y * 0.5f + 0.14f, 0.03f), Facing(Vector3.forward), 0.5f, new Vector2(1.2f, 0.22f), new Color(1f, 0.8f, 0.3f));
            label.fontStyle = FontStyles.Bold;

            var cuttable = root.AddComponent<Cuttable>();
            cuttable.panelId = id;
            cuttable.displayName = displayName;
            cuttable.cutPoints = points.ToArray();
            cuttable.plate = body;
            cuttable.progressLabel = label;
            cuttable.isTraining = training;
            cuttable.seamMaterial = M("SeamLine");
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

        /// <summary>The practice plate in the tug (the tutorial's first cut). Every wreck brings its own panels.</summary>
        static Cuttable BuildPracticePlate()
        {
            var practiceSize = new Vector2(0.5f, 0.5f);
            return MakeCuttable("Practice Plate", "practice_plate", "PRACTICE PLATE", new Vector3(2f - T * 0.5f - 0.03f, 1.3f, 1.25f), Vector3.left, practiceSize, PerimeterPoints(practiceSize, false), true);
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

            // Floating junk in the airlock (each wreck brings its own beyond the door).
            Junk(s_JunkCanPrefab, new Vector3(-1.1f, 1.3f, 4.6f));
        }

        // ------------------------------------------------------------------ drone navigation graph

        static void Waypoint(string id, float x, float z)
        {
            var wp = Empty(s_Level.Find("Drone Waypoints") ?? Empty(s_Level, "Drone Waypoints", Vector3.zero).transform, "WP " + id, new Vector3(x, 1.35f, z)).AddComponent<EnemyWaypoint>();
            s_Waypoints[id] = wp;
        }

        /// <summary>
        /// Links waypoints in a chain. A leg longer than MaxLeg gets waypoints along it (named "A-B 1" ...) so the
        /// creature's routes stay made of short, straight, checked legs however big the room.
        /// </summary>
        static void Link(params string[] chain)
        {
            for (int i = 0; i + 1 < chain.Length; i++)
            {
                var a = s_Waypoints[chain[i]];
                var b = s_Waypoints[chain[i + 1]];
                Vector3 d = b.Position - a.Position;
                d.y = 0f;
                int legs = Mathf.Max(1, Mathf.CeilToInt(d.magnitude / MaxLeg - 0.001f));
                var previous = a;
                for (int k = 1; k < legs; k++)
                {
                    string id = chain[i] + "-" + chain[i + 1] + " " + k;
                    Vector3 at = Vector3.Lerp(a.Position, b.Position, k / (float)legs);
                    if (!s_Waypoints.ContainsKey(id)) Waypoint(id, at.x, at.z);
                    EnemyWaypoint.Link(previous, s_Waypoints[id]);
                    previous = s_Waypoints[id];
                }
                EnemyWaypoint.Link(previous, b);
            }
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
            BuildHeadLampShelf(stall, goods, panel); // fourth column: the helmet lamp (Builder.HeadLamp.cs)
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

        /// <summary>A dark anti-slip strip with hazard edges down the middle of a route.</summary>
        static void Walkway(float x0, float x1, float z0, float z1)
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

        static void Stencil(string text, Vector3 position, float yaw)
        {
            var t = Text(s_Signs, "Floor Stencil " + text, text, position, Quaternion.Euler(90f, yaw, 0f), 2.2f, new Vector2(3f, 0.5f), new Color(0.85f, 0.7f, 0.15f));
            t.fontStyle = FontStyles.Bold;
        }

        /// <summary>The tug and the airlock's art pass (each wreck dresses itself).</summary>
        static void BuildMapDetail()
        {
            Beams(-2, 2, -3, 2, H, false);
            Beams(-2, 2, 3.6f, 7.6f, H, false);
            Walkway(-0.55f, 0.55f, 3.9f, 7.3f);
            Walkway(-0.5f, 0.5f, -2.0f, 1.7f);
            Vent(new Vector3(-2f + T * 0.5f + 0.01f, 2.3f, 5.6f), Vector3.right);
            Vent(new Vector3(2f - T * 0.5f - 0.01f, 2.35f, -2.4f), Vector3.left, 0.6f, 0.3f);
            Window(new Vector3(2f - T * 0.5f - 0.015f, 1.7f, 5.6f), Vector3.left, 1.6f, 0.8f);
            LockerRow(new Vector3(-2f + T * 0.5f + 0.24f, 0, 4.9f), Vector3.right, 3);
            Poster("SALVAGE IS\nCOMPANY PROPERTY.\nSO ARE YOU.", new Vector3(-2f + T * 0.5f + 0.012f, 1.55f, -2.45f), Vector3.right, 0.8f, 0.55f, 0.4f);
            Stencil("AIRLOCK", new Vector3(0, 0.016f, 4.6f), 0f);
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

using System.Collections.Generic;
using System.Linq;
using TMPro;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.UI;
using TrashAstronauts.World;
using UnityEditor;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// The wrecks. Four derelicts dock to the same airlock, one per contract:
    ///   A  Hauler MARGIN CALL        a spine corridor with rooms off it (Builder.Freighter.cs)
    ///   B  Ore barge SLOW DIVIDEND   a tall central hold with rooms on three sides and a service duct
    ///   C  Survey ship DUE DILIGENCE a ring corridor around a lab and reactor core
    ///   D  Lava tube outpost         a mining outpost in a lunar cave (Builder.Cave.cs)
    /// Also the tug's flight deck.
    ///
    /// The bigger-maps update made every floor plan 1.5x longer and wider about the airlock join (x = 0,
    /// z = 7.6), which stays where it is. Each builder still writes its layout in the coordinates it was
    /// designed in and passes them through X(), Z() and P(): rooms, corridors, routes and loot spots move
    /// out with the plan, while doors, buttons, panels, rails, lockers, racks and props keep their real size
    /// and are set against the walls they belong to. Nothing is transform-scaled.
    /// </summary>
    public static partial class Builder
    {
        /// <summary>How much bigger the floor plans are since the bigger-maps update.</summary>
        public const float MapScale = 1.5f;
        /// <summary>Where every wreck meets the airlock's north wall.</summary>
        public const float JoinZ = 7.6f;
        /// <summary>
        /// The freighter's builder is written in the coordinates of its first, small layout. It was stretched
        /// 1.5x once (by transform), and is now built natively at 1.5 x 1.5 of that layout.
        /// </summary>
        public const float FreighterScale = MapScale * 1.5f;
        /// <summary>Ceiling height in the wrecks' rooms (2.8 m before; the tug and the airlock keep H).</summary>
        const float WH = 3.4f;
        /// <summary>Creature routes are cut into legs no longer than this (metres).</summary>
        const float MaxLeg = 6f;

        /// <summary>The plan factor of the wreck being built (relative to the coordinates its builder is written in).</summary>
        static float s_Plan = 1f;
        static float X(float x) => x * s_Plan;
        static float Z(float z) => z <= JoinZ ? z : JoinZ + (z - JoinZ) * s_Plan;
        /// <summary>A point of the layout as written, where it is in the built wreck (the airlock does not move).</summary>
        static Vector3 P(float x, float y, float z) => z <= JoinZ ? new Vector3(x, y, z) : new Vector3(X(x), y, Z(z));

        static Vector3 PlanPoint(Vector3 p, float factor) =>
            p.z <= JoinZ ? p : new Vector3(p.x * factor, p.y, JoinZ + (p.z - JoinZ) * factor);
        /// <summary>Where a point of the freighter's original layout is in the built freighter.</summary>
        public static Vector3 FreighterPoint(Vector3 p) => PlanPoint(p, FreighterScale);
        public static Vector3 FreighterPoint(float x, float y, float z) => FreighterPoint(new Vector3(x, y, z));
        /// <summary>Where a point of the barge, survey ship or cave layout (as it was before the update) is now.</summary>
        public static Vector3 WreckPoint(float x, float y, float z) => PlanPoint(new Vector3(x, y, z), MapScale);

        struct Opening
        {
            public char side;
            public float at, width, height;
            /// <param name="side">N, S, E or W wall of the room.</param>
            /// <param name="at">World x (north/south walls) or world z (east/west walls) of the middle of the opening.</param>
            public Opening(char side, float at, float width, float height) { this.side = side; this.at = at; this.width = width; this.height = height; }
            public bool IsDoorway => height > 0f;
        }

        /// <summary>A break in a room's handrail only (lockers, a cabinet or a console stand against that wall).</summary>
        static Opening RailGap(char side, float at, float width) => new Opening(side, at, width, 0f);

        /// <summary>
        /// The wreck's side of the airlock join: wall from x0 to x1 along z = 7.6 around the airlock's own north
        /// wall, and a header over it up to the wreck's (taller) ceiling.
        /// </summary>
        static void JoinWall(string name, float x0, float x1, float height, Material mat)
        {
            const float edge = 2.18f; // the airlock wall runs to x = +-2.09 (2 + T/2 - 0.01)
            if (x0 < -edge - 0.05f) Wall(name + " West", new Vector2(x0, JoinZ), new Vector2(-edge, JoinZ), height, mat);
            if (x1 > edge + 0.05f) Wall(name + " East", new Vector2(edge, JoinZ), new Vector2(x1, JoinZ), height, mat);
            if (height > H + 0.01f)
            {
                var parent = Empty(s_Level, name + " Header", Vector3.zero, null, GameLayers.Environment).transform;
                Box(parent, "Header", new Vector3(0f, (H + height) * 0.5f, JoinZ), new Vector3(edge * 2f, height - H, T), mat);
            }
        }

        static Transform s_Loose;

        // ------------------------------------------------------------------ shared helpers

        static float RepresentativeZ(Transform t)
        {
            bool any = false;
            Bounds bounds = default;
            foreach (var r in t.GetComponentsInChildren<Renderer>(true))
            {
                Bounds rb = r.bounds;
                if (rb.size.sqrMagnitude < 1e-8f) rb = new Bounds(r.transform.position, Vector3.zero);
                if (!any) { bounds = rb; any = true; }
                else bounds.Encapsulate(rb);
            }
            return any ? bounds.center.z : t.position.z;
        }

        /// <summary>Handrails along one wall, on the side the normal points to, broken at doorways.</summary>
        static void RailLine(Vector2 a, Vector2 b, Vector2 inward, params Gap[] gaps)
        {
            const float o = 0.2f, endInset = 0.45f, gapInset = 0.3f;
            Vector2 d = b - a;
            float length = d.magnitude;
            Vector2 dir = d / length;

            void Segment(float s0, float s1)
            {
                if (s1 - s0 < 0.7f) return;
                int pieces = Mathf.Max(1, Mathf.CeilToInt((s1 - s0) / 5.5f));
                float len = (s1 - s0) / pieces;
                for (int i = 0; i < pieces; i++)
                {
                    float p0 = s0 + i * len + (i > 0 ? 0.15f : 0f);
                    float p1 = s0 + (i + 1) * len - (i < pieces - 1 ? 0.15f : 0f);
                    Vector2 pa = a + dir * p0 + inward * o, pb = a + dir * p1 + inward * o;
                    Rail(new Vector3(pa.x, RailY, pa.y), new Vector3(pb.x, RailY, pb.y));
                }
            }

            float cursor = endInset;
            foreach (var gap in gaps.OrderBy(g => g.at))
            {
                Segment(cursor, gap.at - gap.width * 0.5f - gapInset);
                cursor = gap.at + gap.width * 0.5f + gapInset;
            }
            Segment(cursor, length - endInset);
        }

        /// <summary>
        /// A rectangular room: slabs, the walls named in <paramref name="walls"/> (a wall shared with a
        /// neighbour is built by only one of the two rooms), doorway landing spots, handrails on all four
        /// sides, a teleport floor and ceiling beams.
        /// </summary>
        static void Room(string name, float x0, float x1, float z0, float z1, float height, string walls, Material wallMat, params Opening[] openings)
        {
            FloorAndCeiling(name + " Slabs", x0, x1, z0, z1, height, M("Floor"));
            foreach (char side in "SNWE")
            {
                Vector2 a, b, inward;
                switch (side)
                {
                    case 'S': a = new Vector2(x0, z0); b = new Vector2(x1, z0); inward = Vector2.up; break;
                    case 'N': a = new Vector2(x0, z1); b = new Vector2(x1, z1); inward = Vector2.down; break;
                    case 'W': a = new Vector2(x0, z0); b = new Vector2(x0, z1); inward = Vector2.right; break;
                    default: a = new Vector2(x1, z0); b = new Vector2(x1, z1); inward = Vector2.left; break;
                }
                bool alongX = side == 'S' || side == 'N';
                float origin = alongX ? x0 : z0;
                var mine = openings.Where(o => o.side == side).ToArray();
                var gaps = mine.Select(o => new Gap(o.at - origin, o.width, o.height)).ToArray();
                if (walls.IndexOf(side) >= 0)
                {
                    Wall(name + " " + side, a, b, height, wallMat, gaps.Where(g => g.height > 0f).ToArray());
                    foreach (var o in mine.Where(o => o.IsDoorway))
                    {
                        if (alongX) TeleportArea(name + " Doorway", o.at - 0.5f, o.at + 0.5f, a.y - 0.6f, a.y + 0.6f);
                        else TeleportArea(name + " Doorway", a.x - 0.6f, a.x + 0.6f, o.at - 0.5f, o.at + 0.5f);
                    }
                }
                RailLine(a, b, inward, gaps);
            }
            const float inset = 0.35f;
            TeleportArea(name, x0 + inset, x1 - inset, z0 + inset, z1 - inset);
            Beams(x0, x1, z0, z1, height, (x1 - x0) >= (z1 - z0));
        }

        /// <summary>A thread of silk between two points.</summary>
        static void Strand(Transform parent, Vector3 a, Vector3 b, float radius = 0.004f)
        {
            Vector3 d = b - a;
            float length = d.magnitude;
            if (length < 0.02f) return;
            var go = Part(parent, "Strand", Tube(radius, 1f, 5), parent.InverseTransformPoint((a + b) * 0.5f), M("Web"));
            go.transform.rotation = Quaternion.FromToRotation(Vector3.up, d / length);
            go.transform.localScale = new Vector3(1f, length, 1f);
        }

        /// <summary>A cobweb filling a corner: spokes fanning out between three edges, tied together by sagging rings.</summary>
        static void Web(Vector3 corner, Vector3 u, Vector3 v, Vector3 w, float size)
        {
            var root = Empty(s_Props, "Web", corner, null, GameLayers.Default).transform;
            var tips = new List<Vector3>();
            const int spokes = 7;
            for (int i = 0; i < spokes; i++)
            {
                // Sweep from one wall, across the ceiling line, to the other wall.
                float t = i / (float)(spokes - 1);
                Vector3 dir = (u * Mathf.Cos(t * Mathf.PI * 0.5f) + v * Mathf.Sin(t * Mathf.PI * 0.5f) + w * (0.25f + 0.5f * Mathf.Sin(t * Mathf.PI))).normalized;
                Vector3 tip = corner + dir * size * (0.8f + 0.2f * Mathf.Sin(i * 2.3f));
                tips.Add(tip);
                Strand(root, corner, tip, 0.0035f);
            }
            for (int ring = 1; ring <= 4; ring++)
            {
                float k = ring / 4.6f;
                for (int i = 0; i + 1 < tips.Count; i++)
                {
                    Vector3 a = Vector3.Lerp(corner, tips[i], k), b = Vector3.Lerp(corner, tips[i + 1], k);
                    Vector3 sag = (a + b) * 0.5f + w * (0.03f * ring);
                    Strand(root, a, sag, 0.0025f);
                    Strand(root, sag, b, 0.0025f);
                }
            }

            // One mesh per web, not a hundred little objects.
            var filters = root.GetComponentsInChildren<MeshFilter>();
            var pieces = new CombineInstance[filters.Length];
            for (int i = 0; i < filters.Length; i++)
            {
                pieces[i].mesh = filters[i].sharedMesh;
                pieces[i].transform = root.worldToLocalMatrix * filters[i].transform.localToWorldMatrix;
            }
            var combined = SaveMesh("Web_" + F(corner.x) + "_" + F(corner.y) + "_" + F(corner.z) + "_" + F(size), () =>
            {
                var m = new Mesh();
                m.CombineMeshes(pieces, true, true);
                m.RecalculateBounds();
                return m;
            });
            for (int i = root.childCount - 1; i >= 0; i--) UnityEngine.Object.DestroyImmediate(root.GetChild(i).gameObject);
            root.gameObject.AddComponent<MeshFilter>().sharedMesh = combined;
            var renderer = root.gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = M("Web");
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        /// <summary>Something wrapped in silk and hung from the ceiling. A boot sticks out of the bottom.</summary>
        static void Cocoon(Vector3 ceilingPoint, float drop, int seed)
        {
            var root = Empty(s_Props, "Cocoon", ceilingPoint, null, GameLayers.Default).transform;
            float length = 0.55f + (seed % 3) * 0.12f;
            Vector3 centre = Vector3.down * (drop + length * 0.5f);
            Strand(root, ceilingPoint, ceilingPoint + Vector3.down * drop, 0.008f);
            Strand(root, ceilingPoint + new Vector3(0.12f, 0, 0.05f), ceilingPoint + Vector3.down * (drop + 0.1f), 0.004f);
            Part(root, "Bundle", Blob("Cocoon", new Vector3(0.2f, length * 0.5f, 0.18f), 0.05f, seed, 16, 12), centre, M("Silk"), new Vector3(0, seed * 37f, 6f));
            for (int i = 0; i < 5; i++)
                Part(root, "Wrap", Torus(0.19f - Mathf.Abs(i - 2) * 0.035f, 0.007f, 14, 5), centre + Vector3.up * ((i - 2) * length * 0.2f), M("Web"), new Vector3(8f * (i % 2 == 0 ? 1 : -1), 0, 5f));
            CBox(root, "Boot", centre + Vector3.down * (length * 0.5f + 0.04f) + new Vector3(0.03f, 0, 0.04f), new Vector3(0.09f, 0.1f, 0.22f), M("Rubber"), 0.03f, new Vector3(25, seed * 11f, 0));
            // Solid enough to bump into and to hide behind.
            var col = Empty(root, "Collider", centre, null, GameLayers.Environment).AddComponent<CapsuleCollider>();
            col.radius = 0.18f;
            col.height = length;
        }

        /// <summary>A cluster of pale egg sacs stuck to a wall.</summary>
        static void EggSacs(Vector3 wallPoint, Vector3 outward, int count, int seed)
        {
            var root = Empty(s_Props, "Egg Sacs", wallPoint, Facing(outward), GameLayers.Default).transform;
            var rng = new System.Random(seed);
            for (int i = 0; i < count; i++)
            {
                float r = 0.045f + (float)rng.NextDouble() * 0.05f;
                var at = new Vector3(((float)rng.NextDouble() - 0.5f) * 0.5f, ((float)rng.NextDouble() - 0.5f) * 0.4f, -r * 0.7f);
                Part(root, "Sac", Blob("EggSac", new Vector3(r, r * 1.15f, r), r * 0.18f, seed + i % 4, 12, 8), at, M("Silk"));
            }

            // The young, wandering over the wall around the sacs.
            if (s_SpiderlingMesh == null) s_SpiderlingMesh = LoadSpiderMerged("spiderling", out s_SpiderlingSkin);
            var young = new List<Transform>();
            for (int i = 0; i < 8; i++)
            {
                var spider = Empty(root, "Spiderling " + i, Vector3.zero, null, GameLayers.Default);
                spider.AddComponent<MeshFilter>().sharedMesh = s_SpiderlingMesh;
                var renderer = spider.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = s_SpiderlingSkin;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                spider.transform.localScale = Vector3.one * 0.12f;
                young.Add(spider.transform);
            }
            var swarm = root.gameObject.AddComponent<SpiderlingSwarm>();
            swarm.spiders = young.ToArray();
        }

        /// <summary>A strand of web across a route. See WebTripwire: touching it slows the player and tells the spider where they are.</summary>
        static void Tripwire(string id, Vector3 a, Vector3 b)
        {
            var root = Empty(s_Props, "Web Tripwire " + id, (a + b) * 0.5f, null, GameLayers.Default);
            var endA = Empty(root.transform, "End A", root.transform.InverseTransformPoint(a));
            var endB = Empty(root.transform, "End B", root.transform.InverseTransformPoint(b));
            var visual = Empty(root.transform, "Strands", Vector3.zero).transform;
            Vector3 along = (b - a).normalized;
            Vector3 lift = Vector3.Cross(along, Vector3.Cross(Vector3.up, along)).normalized;
            if (lift.sqrMagnitude < 0.5f) lift = Vector3.right;
            Vector3 mid = (a + b) * 0.5f;
            Strand(visual, a, b, 0.0035f);
            Strand(visual, a + lift * 0.09f, mid - lift * 0.03f, 0.0025f);
            Strand(visual, mid - lift * 0.03f, b + lift * 0.11f, 0.0025f);
            Strand(visual, a - lift * 0.12f, mid + lift * 0.05f, 0.0025f);
            Strand(visual, mid + lift * 0.05f, b - lift * 0.08f, 0.0025f);
            for (int i = 1; i <= 4; i++)
            {
                Vector3 p = Vector3.Lerp(a, b, i / 5f);
                Strand(visual, p + lift * 0.1f, p - lift * 0.1f, 0.002f);
            }
            var wire = root.AddComponent<WebTripwire>();
            wire.wireId = id;
            wire.endA = endA.transform;
            wire.endB = endB.transform;
            wire.visual = visual.gameObject;
            // Hidden until a contract strings it (none does since 0.8.3), so it never shows in the hub or in previews.
            visual.gameObject.SetActive(false);
            s_Tripwires.Add(wire);
        }

        static Mesh s_SpiderlingMesh;
        static Material s_SpiderlingSkin;

        static Light RoomLight(string name, float x, float y, float z, Color color, float intensity, float range)
        {
            var light = PointLight(s_Lights, name, new Vector3(x, y, z), color, intensity, range);
            WreckLight(light);
            return light;
        }

        static AudioSource MapHum(string name, Vector3 position, float pitch)
        {
            var source = Empty(s_Level, name, position).AddComponent<AudioSource>();
            ConfigureSpatial(source, s_Audio != null ? s_Audio.droneHum : null, true, 0.3f, 1f, 12f);
            source.pitch = pitch;
            source.playOnAwake = true;
            return source;
        }

        static readonly Vector2[] s_HatchCuts =
        {
            new Vector2(-0.52f, 0.48f), new Vector2(0.52f, 0.48f), new Vector2(0.52f, 0.03f),
            new Vector2(0.52f, -0.42f), new Vector2(-0.52f, -0.42f), new Vector2(-0.52f, 0.03f),
        };

        static WreckMap BeginMap(string objectName, string id, string displayName, string blurb)
        {
            var root = new GameObject(objectName).transform;
            s_Level = Empty(root, "Level", Vector3.zero).transform;
            s_Rails = Empty(s_Level, "Handrails", Vector3.zero).transform;
            s_Teleport = Empty(s_Level, "Teleport Surfaces", Vector3.zero).transform;
            s_Spawns = Empty(s_Level, "Salvage Spawn Points", Vector3.zero).transform;
            s_Props = Empty(s_Level, "Props", Vector3.zero).transform;
            s_Lights = Empty(s_Level, "Lights", Vector3.zero).transform;
            s_Signs = Empty(s_Level, "Signs", Vector3.zero).transform;
            s_Loose = Empty(root, "Loose Props", Vector3.zero).transform;
            s_SpawnPoints.Clear();
            s_Cuttables.Clear();
            s_Tripwires.Clear();
            s_Waypoints.Clear();
            s_WreckLights.Clear();
            s_WreckGlow.Clear();

            var map = root.gameObject.AddComponent<WreckMap>();
            map.mapId = id;
            map.displayName = displayName;
            map.blurb = blurb;
            return map;
        }

        static void EndMap(WreckMap map, Transform dock, SlidingDoor[] doors, AudioSource[] ambience, Light[] sharedLights, Renderer[] sharedGlow, params string[] route)
        {
            map.spawnPoints = s_SpawnPoints.ToArray();
            map.cuttables = s_Cuttables.ToArray();
            map.tripwires = s_Tripwires.ToArray();
            map.doors = doors;
            map.creatureDoors = doors;
            map.dock = dock;
            map.graph = s_Waypoints.Values.ToArray();
            map.patrolRoute = Route(route);
            map.lights = sharedLights.Concat(s_WreckLights).ToArray();
            map.glowStrips = sharedGlow.Concat(s_WreckGlow).ToArray();
            map.ambience = ambience;
            s_Loose = null;
            s_Plan = 1f;
            map.gameObject.SetActive(false);
        }

        /// <summary>The electronics rack every wreck has somewhere: a bracket to cut, with a module clamped behind it.</summary>
        static Cuttable RackMount(string id, Vector3 wallPoint, Vector3 outward, string spawnId)
        {
            Vector3 side = Vector3.Cross(Vector3.up, outward);
            Vector3 face = wallPoint + outward * (T * 0.5f);
            var bracket = MakeCuttable("Electronics Rack Bracket", id, "RACK CLAMP", face + outward * 0.3f + Vector3.up * 1.35f, outward,
                new Vector2(0.62f, 0.1f), new[] { new Vector2(-0.28f, 0f), new Vector2(0.28f, 0f) }, false, false);
            Vector3 Size(float along, float y, float depth) => new Vector3(Mathf.Abs(side.x) * along + Mathf.Abs(outward.x) * depth, y, Mathf.Abs(side.z) * along + Mathf.Abs(outward.z) * depth);
            Box(s_Props, "Rack Shelf", face + outward * 0.18f + Vector3.up * 1.1f, Size(0.7f, 0.04f, 0.36f), M("Trim"));
            Box(s_Props, "Rack Back", face + outward * 0.03f + Vector3.up * 1.4f, Size(0.7f, 0.7f, 0.04f), M("Trim"));
            Spawn(spawnId, face + outward * 0.22f + Vector3.up * 1.24f, SalvageCategoryMask.Electronics, null, bracket);
            return bracket;
        }

        // ------------------------------------------------------------------ wreck B: the ore barge

        static WreckMap BuildOreBarge(Light[] sharedLights, Renderer[] sharedGlow)
        {
            var map = BeginMap("Wreck B - Ore Barge", "ore_barge", "Ore barge SLOW DIVIDEND", "A tall ore hold in the middle, rooms on three sides.");
            s_Plan = MapScale;
            Transform root = map.transform;
            var wall = M("Wall");
            const float AH = 5.0f; // the hold is tall: there is room to float over the containers (4 m before the bigger-maps update)
            const float LY = WH - 0.4f; // lamp height in the ordinary rooms
            const SalvageCategoryMask S = SalvageCategoryMask.SmallScrap;
            const SalvageCategoryMask E = SalvageCategoryMask.Electronics;
            const SalvageCategoryMask N = SalvageCategoryMask.Novelty;
            var amber = new Color(1f, 0.62f, 0.25f);

            // Fixtures that stand against a wall, worked out once so the rails can break around them.
            float lockersZ = Z(21.4f), consoleZ = Z(19.6f), driveConsoleZ = Z(34.5f), bunkX0 = X(-15f) + 1.2f;

            JoinWall("B Join", X(-2), X(2), WH, wall);
            Room("B Vestibule", X(-2), X(2), JoinZ, Z(11.6f), WH, "WE", wall, new Opening('S', 0, 1.4f, 2.1f), new Opening('N', 0, 1.8f, 2.2f));
            Room("B Ore Hold", X(-8), X(8), Z(11.6f), Z(26.6f), AH, "NSEW", wall, new Opening('S', 0, 1.8f, 2.2f), new Opening('N', 0, 1.6f, 2.1f),
                new Opening('W', Z(19), 1.8f, 2.2f), new Opening('E', Z(19), 1.6f, 2.2f));
            Room("B Crew Quarters", X(-15), X(-8), Z(14), Z(24), WH, "NSW", wall, new Opening('E', Z(19), 1.8f, 2.2f), new Opening('N', X(-13.4f), 1.0f, 2.0f),
                RailGap('W', lockersZ, 2.3f), RailGap('S', bunkX0 + 2.1f, 6.4f));
            Room("B Pump Room", X(8), X(13), Z(14.6f), Z(23.4f), WH, "NSE", wall, new Opening('W', Z(19), 1.6f, 2.2f),
                RailGap('E', consoleZ, 1.8f), RailGap('E', Z(17), 1.0f), RailGap('E', Z(23.4f) - 1.0f, 2.0f), RailGap('N', X(13f) - 1.1f, 2.0f));
            Room("B Drive Room", X(-6), X(6), Z(26.6f), Z(38.6f), WH, "NEW", wall, new Opening('S', 0, 1.6f, 2.1f), new Opening('W', Z(31), 1.0f, 2.0f),
                RailGap('W', driveConsoleZ, 2.2f));
            var driveDoor = BuildDoor("Drive Room Door", "b_door_drive", P(0, 0, 26.6f), 1.6f, 2.1f, false, false, true);

            // Service duct: crew quarters -> drive room (the shortcut, sealed by a cover). Still a crawlway: 1.8 m wide, 2.2 m high.
            float ductX = X(-13.4f), ductZ = Z(31f), dw = 0.9f;
            FloorAndCeiling("B Duct A Slabs", ductX - dw, ductX + dw, Z(24f), ductZ + dw, 2.2f, M("Floor"));
            FloorAndCeiling("B Duct B Slabs", ductX + dw, X(-6f), ductZ - dw, ductZ + dw, 2.2f, M("Floor"));
            Wall("B Duct A W", new Vector2(ductX - dw, Z(24f)), new Vector2(ductX - dw, ductZ + dw), 2.2f, M("Trim"));
            Wall("B Duct A E", new Vector2(ductX + dw, Z(24f)), new Vector2(ductX + dw, ductZ - dw), 2.2f, M("Trim"));
            Wall("B Duct N", new Vector2(ductX - dw, ductZ + dw), new Vector2(X(-6f), ductZ + dw), 2.2f, M("Trim"));
            Wall("B Duct B S", new Vector2(ductX + dw, ductZ - dw), new Vector2(X(-6f), ductZ - dw), 2.2f, M("Trim"));
            TeleportArea("B Duct A", ductX - dw + 0.3f, ductX + dw - 0.3f, Z(24f) + 0.3f, ductZ + dw - 0.3f);
            TeleportArea("B Duct B", ductX - dw + 0.3f, X(-6f) + 0.3f, ductZ - dw + 0.3f, ductZ + dw - 0.3f);
            RailLine(new Vector2(ductX - dw, Z(24f)), new Vector2(ductX - dw, ductZ + dw), Vector2.right);
            RailLine(new Vector2(ductX - dw, ductZ + dw), new Vector2(X(-6f), ductZ + dw), Vector2.down);
            Exclusion(root, "No Drone: B Duct A", new Vector3(ductX, 1.2f, (Z(24f) + 0.05f + ductZ + dw + 0.05f) * 0.5f), new Vector3(dw * 2f + 0.1f, 2.6f, ductZ + dw - Z(24f)));
            Exclusion(root, "No Drone: B Duct B", new Vector3((ductX + dw - 0.05f + X(-6f) + 0.05f) * 0.5f, 1.2f, ductZ), new Vector3(X(-6f) - ductX - dw + 0.1f, 2.6f, dw * 2f + 0.1f));

            // Panels and loot
            var drivePanel = SealedCompartment("Drive Core Housing", "b_panel_reactor", "DRIVE CORE", P(0, 0, 33.5f), new Vector3(1.8f, 2.4f, 1.8f), M("ReactorShell"), out Vector3 driveCavity);
            var payPanel = SealedCompartment("Paymaster's Locker", "b_panel_cage", "PAYMASTER", new Vector3(X(13f) - 1.1f, 0, Z(23.4f) - 1.0f), new Vector3(1.7f, 2.2f, 1.7f), M("ContainerBlue"), out Vector3 payCavity);
            MakeCuttable("Service Duct Cover", "b_panel_duct", "SERVICE DUCT", new Vector3(ductX, 1.02f, Z(24f) - T * 0.5f - 0.03f), Vector3.back, new Vector2(1.12f, 2.04f), s_HatchCuts);
            Spawn("b_drive_housing", driveCavity, SalvageCategoryMask.ReactorComponent, drivePanel);
            Spawn("b_pay_locker", payCavity, E | N, payPanel);
            RackMount("b_mount_rack", new Vector3(X(13f), 0, Z(17f)), Vector3.left, "b_pump_rack");

            Spawn("b_airlock", new Vector3(-1.3f, 1.0f, 6.8f), S);
            Spawn("b_vestibule", P(1.2f, 1.2f, 10.4f), S);
            Spawn("b_hold_south", P(-5.8f, 1.3f, 13.4f), S | E);
            Spawn("b_hold_west", P(-6.6f, 1.0f, 17.2f), S);
            Spawn("b_hold_mid", P(0.6f, 1.4f, 19f), S | E);
            Spawn("b_hold_east", P(4.8f, 1.5f, 16.5f), S | N);
            Spawn("b_hold_north", P(5.6f, 1.2f, 25.4f), S);
            Spawn("b_hold_gap", P(-4.8f, 1.4f, 21.6f), E | N);
            Spawn("b_quarters_door", P(-9.2f, 1.1f, 17.4f), S);
            Spawn("b_quarters_table", new Vector3(X(-10.2f), 1.2f, Z(21.9f) - 0.9f), E | S);
            Spawn("b_quarters_bunk", new Vector3(bunkX0 + 0.2f, 1.2f, Z(14f) + 1.6f), N | S);
            Spawn("b_quarters_lockers", new Vector3(X(-15f) + 1.0f, 1.3f, Z(24f) - 1.2f), S | E); // the far corner, past the lockers
            Spawn("b_duct_bend", new Vector3(ductX, 1.0f, ductZ), S | E);
            Spawn("b_pump_floor", new Vector3(X(8f) + 1.2f, 1.1f, Z(23.4f) - 1.0f), S); // the far corner of the pump room
            Spawn("b_pump_console", new Vector3(X(13f) - 1.6f, 1.25f, consoleZ + 0.7f), E | N);
            Spawn("b_drive_west", P(-4.8f, 1.1f, 28.2f), S | E);
            Spawn("b_drive_back", P(2.2f, 1.2f, 37.6f), S | N);

            // Hold: four big ore containers to hide behind and float over, smaller ones along the walls.
            Container(P(-2.75f, 0, 16.5f), new Vector3(2.4f, 2.4f, 2.6f));
            Container(P(2.75f, 0, 16.5f), new Vector3(2.6f, 2.2f, 2.4f), "ContainerBlue");
            Container(P(-2.75f, 0, 21.6f), new Vector3(2.6f, 2.6f, 2.4f), "ContainerBlue");
            Container(P(2.75f, 0, 21.6f), new Vector3(2.4f, 2.0f, 2.6f));
            Container(new Vector3(X(-8f) + 1.0f, 0, Z(11.6f) + 1.4f), new Vector3(1.2f, 1.0f, 1.6f));
            Container(new Vector3(X(8f) - 1.1f, 0, Z(26.6f) - 1.4f), new Vector3(1.4f, 1.2f, 1.4f), "ContainerBlue");
            Container(new Vector3(X(-8f) + 1.0f, 0, Z(22.4f)), new Vector3(1.2f, 1.0f, 1.4f), "ContainerBlue");
            Container(new Vector3(X(8f) - 1.0f, 0, Z(15.6f)), new Vector3(1.2f, 1.1f, 1.6f));
            Barrel(new Vector3(X(8f) - 0.75f, 0, Z(11.6f) + 0.85f));
            Barrel(new Vector3(X(8f) - 1.4f, 0, Z(11.6f) + 0.8f), "BarrelBlue");
            Barrel(new Vector3(X(-8f) + 0.75f, 0, Z(26.6f) - 0.9f));
            Pipe("B Hold Pipe W", new Vector3(X(-8f) + 0.4f, AH - 0.3f, Z(11.6f) + 0.3f), new Vector3(X(-8f) + 0.4f, AH - 0.3f, Z(26.6f) - 0.3f), 0.09f, "Pipe");
            Pipe("B Hold Pipe E", new Vector3(X(8f) - 0.4f, AH - 0.3f, Z(11.6f) + 0.3f), new Vector3(X(8f) - 0.4f, AH - 0.3f, Z(26.6f) - 0.3f), 0.07f, "Steel");
            Dust("B Dust Hold", P(0, 2.4f, 19f), new Vector3(21f, 4.2f, 19.5f), 120);
            float c0 = X(-2.75f) - 1.2f, c1 = X(2.75f) + 1.2f; // the outer faces of the big containers
            Rail(new Vector3(c0 - 0.15f, RailY, Z(16.5f) - 1.1f), new Vector3(c0 - 0.15f, RailY, Z(16.5f) + 1.1f));
            Rail(new Vector3(c1 + 0.25f, RailY, Z(21.6f) - 1.0f), new Vector3(c1 + 0.25f, RailY, Z(21.6f) + 1.0f));
            Rail(new Vector3(X(-2.75f) + 1.32f, 2.9f, Z(21.6f)), new Vector3(X(2.75f) - 1.22f, 2.9f, Z(21.6f))); // high bar between the two tall containers

            // Crew quarters: bunks along the south wall, lockers on the west wall, a mess table.
            for (int i = 0; i < 3; i++)
            {
                float x = bunkX0 + i * 2.1f, z = Z(14f) + 0.6f;
                Box(s_Props, "Bunk Lower", new Vector3(x, 0.35f, z), new Vector3(1.9f, 0.3f, 0.9f), M("Trim"));
                Box(s_Props, "Bunk Upper", new Vector3(x, 1.55f, z), new Vector3(1.9f, 0.12f, 0.9f), M("Trim"));
                Box(s_Props, "Bunk Mattress", new Vector3(x, 0.56f, z), new Vector3(1.8f, 0.12f, 0.8f), M("Rubber"), GameLayers.Default, false);
                Box(s_Props, "Bunk Post", new Vector3(x + 0.95f, 1.0f, z + 0.4f), new Vector3(0.06f, 2.0f, 0.06f), M("Steel"), GameLayers.Default, false);
            }
            LockerRow(new Vector3(X(-15f) + T * 0.5f + 0.24f, 0, lockersZ), Vector3.right, 4);
            Box(s_Props, "Mess Table", P(-10.2f, 0.4f, 21.9f), new Vector3(1.5f, 0.8f, 0.9f), M("Trim"));
            Window(new Vector3(X(-15f) + T * 0.5f + 0.015f, 1.65f, Z(17.2f)), Vector3.right, 2.4f, 1.0f);
            Poster("BUNK ROTA\nA: NOBODY\nB: ALSO NOBODY", new Vector3(X(-11.5f), 1.6f, Z(24f) - T * 0.5f - 0.015f), Vector3.back, 0.9f, 0.6f, 0.45f);
            Container(new Vector3(X(-8f) - 1.0f, 0, Z(24f) - 0.9f), new Vector3(1.0f, 0.9f, 1.0f), "ContainerBlue");

            // Pump room
            for (int i = 0; i < 3; i++)
                Pipe("B Pump Riser " + i, new Vector3(X(8f) + 0.5f, 0.05f, Z(14.6f) + 0.6f + i * 0.5f), new Vector3(X(8f) + 0.5f, WH - 0.05f, Z(14.6f) + 0.6f + i * 0.5f), 0.1f, i == 1 ? "Steel" : "Pipe");
            Barrel(new Vector3(X(13f) - 0.7f, 0, Z(14.6f) + 0.7f), "BarrelBlue");
            Barrel(new Vector3(X(13f) - 1.4f, 0, Z(14.6f) + 0.6f));
            Box(s_Props, "Pump Console", new Vector3(X(13f) - 0.55f, 0.45f, consoleZ), new Vector3(0.7f, 0.9f, 1.4f), M("Trim"));
            ConsoleDetail(new Vector3(X(13f) - 0.55f, 0.9f, consoleZ), 1.2f, Vector3.left);
            Vent(new Vector3(X(13f) - T * 0.5f - 0.01f, 2.25f, Z(21f)), Vector3.left);

            // Drive room
            Box(s_Props, "Drive Console", new Vector3(X(-6f) + 0.6f, 0.45f, driveConsoleZ), new Vector3(0.7f, 0.9f, 1.8f), M("Trim"));
            ConsoleDetail(new Vector3(X(-6f) + 0.6f, 0.9f, driveConsoleZ), 1.5f, Vector3.right);
            Barrel(new Vector3(X(6f) - 0.8f, 0, Z(28.0f)));
            Barrel(new Vector3(X(-6f) + 0.8f, 0, Z(38.6f) - 0.8f), "BarrelBlue");
            Barrel(new Vector3(X(6f) - 0.7f, 0, Z(33.0f)), "BarrelBlue");
            Rail(new Vector3(-1.25f, RailY, Z(33.5f) - 1f), new Vector3(-1.25f, RailY, Z(33.5f) + 1f));
            Rail(new Vector3(1.25f, RailY, Z(33.5f) - 1f), new Vector3(1.25f, RailY, Z(33.5f) + 1f));
            Window(new Vector3(X(-3f), 1.7f, Z(38.6f) - T * 0.5f - 0.015f), Vector3.back, 2.4f, 0.9f);
            Dust("B Dust Drive", P(0, 1.6f, 32.6f), new Vector3(15f, 2.6f, 15f), 80);

            Web(new Vector3(X(-8f) + 0.1f, AH - 0.05f, Z(26.6f) - 0.1f), Vector3.right, Vector3.back, Vector3.down, 2.6f);
            Web(new Vector3(X(8f) - 0.1f, AH - 0.05f, Z(11.6f) + 0.1f), Vector3.left, Vector3.forward, Vector3.down, 2.4f);
            EggSacs(new Vector3(X(6f) - T * 0.5f, 1.8f, Z(35.2f)), Vector3.left, 8, 81);

            Tripwire("b_vestibule", new Vector3(X(-2f) + 0.2f, 1.2f, Z(10.6f)), new Vector3(X(2f) - 0.2f, 1.4f, Z(10.6f)));
            Tripwire("b_hold_gap", new Vector3(X(-2.75f) + 1.3f, 1.2f, Z(16.5f)), new Vector3(X(2.75f) - 1.35f, 1.4f, Z(16.5f)));
            Tripwire("b_drive_entry", new Vector3(-0.9f, 1.2f, Z(27.4f)), new Vector3(0.9f, 1.35f, Z(27.4f)));
            EggSacs(new Vector3(X(-8f) + T * 0.5f, 2.0f, Z(23.4f)), Vector3.right, 6, 82);

            Poster("SHIFT NOTE\nDo not go in the hold alone.\nCount the crates. One of them moves.", new Vector3(X(-2f) + T * 0.5f + 0.012f, 1.5f, Z(9.6f)), Vector3.right, 0.95f, 0.55f, 0.36f);
            Poster("LOG 12\nBunk C is empty again.\nNobody saw her leave.", new Vector3(X(-10f), 1.5f, Z(14f) + T * 0.5f + 0.012f), Vector3.forward, 0.9f, 0.5f, 0.38f);
            Poster("LOG 19\nWe sealed the duct.\nIt was already inside.", new Vector3(X(6f) - T * 0.5f - 0.012f, 1.5f, Z(30.2f)), Vector3.left, 0.9f, 0.5f, 0.38f);

            // Signs
            Sign("ORE HOLD", new Vector3(0, 2.45f, Z(11.6f) - T * 0.5f - 0.02f), Vector3.back, 1.4f);
            Sign("CREW QUARTERS", new Vector3(X(-8f) + T * 0.5f + 0.02f, 2.5f, Z(19f)), Vector3.right, 1.3f);
            Sign("PUMP ROOM", new Vector3(X(8f) - T * 0.5f - 0.02f, 2.5f, Z(19f)), Vector3.left, 1.3f);
            Sign("DRIVE ROOM", new Vector3(0, 2.5f, Z(26.6f) - T * 0.5f - 0.02f), Vector3.back, 1.5f, new Color(1f, 0.6f, 0.3f));
            Sign("SERVICE DUCT\nTO DRIVE ROOM", new Vector3(ductX + 1.3f, 1.9f, Z(24f) - T * 0.5f - 0.02f), Vector3.back, 0.9f);
            Sign("SERVICE DUCT\nTO QUARTERS", new Vector3(X(-6f) + T * 0.5f + 0.02f, 2.35f, ductZ), Vector3.right, 0.9f);
            Sign("SOMETHING SLEEPS HERE\nDO NOT WAKE IT", new Vector3(X(6f) - 1.8f, 2.2f, Z(38.6f) - T * 0.5f - 0.02f), Vector3.back, 0.9f, new Color(1f, 0.5f, 0.4f));

            // Lights: sodium amber, with a red drive room. Twice as many as before for 2.25x the floor.
            RoomLight("B Vestibule Light", 0, LY, Z(9.6f), amber, 2.8f, 8f);
            float hy = AH - 0.8f;
            RoomLight("B Hold Light SW", X(-5.4f), hy, Z(14.2f), amber, 8.1f, 14f);
            RoomLight("B Hold Light SE", X(5.4f), hy, Z(14.2f), amber, 7.5f, 14f);
            RoomLight("B Hold Light NW", X(-5.4f), hy, Z(24f), new Color(1f, 0.5f, 0.25f), 7.5f, 14f);
            RoomLight("B Hold Light NE", X(5.4f), hy, Z(24f), amber, 8.1f, 14f);
            RoomLight("B Hold Light Mid", 0f, AH - 0.6f, Z(19f), new Color(1f, 0.8f, 0.55f), 7.5f, 14f);
            RoomLight("B Hold Light S", 0f, hy, Z(13.2f), amber, 5.6f, 11f);
            RoomLight("B Hold Light N", 0f, hy, Z(25.0f), amber, 5.6f, 11f);
            RoomLight("B Hold Light W", X(-7f), hy, Z(19f), new Color(1f, 0.55f, 0.28f), 5.6f, 11f);
            RoomLight("B Hold Light E", X(7f), hy, Z(19f), amber, 5.6f, 11f);
            RoomLight("B Quarters Light", X(-11.5f), LY, Z(16.5f), new Color(1f, 0.85f, 0.65f), 3.4f, 9.5f);
            RoomLight("B Quarters Light N", X(-11.5f), LY, Z(21.5f), new Color(1f, 0.85f, 0.65f), 3.0f, 9.5f);
            RoomLight("B Pump Light", X(10.5f), LY, Z(16.5f), new Color(0.55f, 1f, 0.6f), 3.2f, 8.5f);
            RoomLight("B Pump Light N", X(10.5f), LY, Z(21.6f), new Color(0.55f, 1f, 0.6f), 3.0f, 8.5f);
            RoomLight("B Drive Light", 0, LY - 0.1f, Z(29.6f), new Color(1f, 0.3f, 0.2f), 3.8f, 10f);
            RoomLight("B Drive Light Back", 0, LY - 0.1f, Z(36.4f), new Color(1f, 0.45f, 0.2f), 3.2f, 9f);
            RoomLight("B Drive Light W", X(-4.5f), LY - 0.1f, Z(33f), new Color(1f, 0.35f, 0.2f), 2.8f, 8.5f);
            RoomLight("B Drive Light E", X(4.5f), LY - 0.1f, Z(33f), new Color(1f, 0.4f, 0.2f), 2.8f, 8.5f);
            RoomLight("B Quarters Light Mid", X(-13f), LY, Z(19f), new Color(1f, 0.8f, 0.6f), 2.4f, 8f);
            RoomLight("B Duct Light", ductX, 1.9f, Z(26.5f), new Color(1f, 0.5f, 0.2f), 1.8f, 5.5f);
            RoomLight("B Duct Light B", X(-9.6f), 1.9f, ductZ, new Color(1f, 0.5f, 0.2f), 1.8f, 5.5f);
            Strip("B Vestibule Strip", new Vector3(0, WH - 0.03f, Z(9.6f)), new Vector3(0.1f, 0.03f, 3.6f), "GlowWarm");
            Strip("B Hold Strip W", new Vector3(X(-5.5f), AH - 0.03f, Z(19f)), new Vector3(0.12f, 0.03f, 16.5f), "GlowWarm");
            Strip("B Hold Strip E", new Vector3(X(5.5f), AH - 0.03f, Z(19f)), new Vector3(0.12f, 0.03f, 16.5f), "GlowWarm");
            Strip("B Quarters Strip", new Vector3(X(-11.5f), WH - 0.03f, Z(19f)), new Vector3(6f, 0.03f, 0.1f), "GlowWhite");
            Strip("B Pump Strip", new Vector3(X(10.5f), WH - 0.03f, Z(19f)), new Vector3(0.1f, 0.03f, 7.5f), "GlowTeal");
            Strip("B Drive Strip W", new Vector3(X(-3.4f), WH - 0.03f, Z(32.6f)), new Vector3(0.1f, 0.03f, 10.5f), "GlowRed");
            Strip("B Drive Strip E", new Vector3(X(3.4f), WH - 0.03f, Z(32.6f)), new Vector3(0.1f, 0.03f, 10.5f), "GlowRed");

            Anchor("B Vestibule", 0f, Z(9.6f));
            Anchor("B Hold South", 0f, Z(13.4f));
            Anchor("B Hold North", 0f, Z(24.8f));
            Anchor("B Hold West", X(-6.4f), Z(19f));
            Anchor("B Hold East", X(6.4f), Z(19f));
            Anchor("B Quarters", X(-10.6f), Z(19f));
            Anchor("B Pump", X(9.8f), Z(19f));
            Anchor("B Drive", 0f, Z(28.6f));
            Anchor("B Duct", ductX, Z(27.6f));

            Junk(s_JunkCanPrefab, P(-0.8f, 1.5f, 14.2f));
            Junk(s_JunkCanPrefab, P(6.2f, 2.6f, 20.4f));
            Junk(s_JunkCanPrefab, P(-11.6f, 1.4f, 17.6f));
            Junk(s_JunkCratePrefab, P(0.4f, 3.0f, 22.4f));
            Junk(s_JunkCratePrefab, P(10.4f, 1.3f, 17.6f));
            Junk(s_JunkCratePrefab, P(-3.0f, 1.4f, 30.4f));

            // Creature routes: around and across the hold, into each side room, around the drive core.
            // Link() puts extra waypoints on any leg longer than MaxLeg.
            Waypoint("V0", 0, Z(9.6f));
            Waypoint("A0", 0, Z(13.2f)); Waypoint("A1", 0, Z(19f)); Waypoint("A2", 0, Z(25.2f));
            Waypoint("ASW", X(-5.5f), Z(14f)); Waypoint("AW", X(-5.5f), Z(19f)); Waypoint("ANW", X(-5.5f), Z(24.2f));
            Waypoint("ASE", X(5.5f), Z(14f)); Waypoint("AE", X(5.5f), Z(19f)); Waypoint("ANE", X(5.5f), Z(24.2f));
            Waypoint("Q0", X(-9.5f), Z(19f)); Waypoint("Q1", X(-12f), Z(19f)); Waypoint("Q2", X(-12f), Z(16.4f)); Waypoint("Q3", X(-12f), Z(22.4f));
            Waypoint("P0", X(9.5f), Z(19f)); Waypoint("P1", X(10.4f), Z(16.6f)); Waypoint("P2", X(10.2f), Z(21f));
            Waypoint("E0", 0, Z(28f)); Waypoint("E1", X(-3.5f), Z(29.6f)); Waypoint("E2", X(-3.5f), Z(36.6f)); Waypoint("E3", X(3.5f), Z(36.6f)); Waypoint("E4", X(3.5f), Z(29.6f));
            Link("V0", "A0", "A1", "A2", "E0");
            Link("A0", "ASW", "AW", "ANW", "A2");
            Link("A0", "ASE", "AE", "ANE", "A2");
            Link("AW", "A1", "AE");
            Link("AW", "Q0", "Q1", "Q2");
            Link("Q1", "Q3");
            Link("AE", "P0", "P1");
            Link("P0", "P2");
            Link("E0", "E1", "E2", "E3", "E4", "E0");

            var dock = Empty(root, "Creature Dock", new Vector3(X(6f) - 1.4f, 1.45f, Z(38.6f) - 1.2f), Quaternion.Euler(0, 200f, 0)).transform;
            var hum = MapHum("B Drive Hum", P(0, 1.4f, 33.5f), 0.42f);
            EndMap(map, dock, new[] { driveDoor }, new[] { hum }, sharedLights, sharedGlow,
                "E3", "E4", "E0", "A2", "ANE", "AE", "P0", "P1", "P0", "P2", "P0", "AE", "ASE", "A0", "V0", "A0", "ASW", "AW", "Q0", "Q1", "Q2", "Q1", "Q3",
                "Q1", "Q0", "AW", "A1", "AE", "A1", "AW", "ANW", "A2", "E0", "E1", "E2");
            return map;
        }

        // ------------------------------------------------------------------ wreck C: the survey ship

        static WreckMap BuildSurveyShip(Light[] sharedLights, Renderer[] sharedGlow)
        {
            var map = BeginMap("Wreck C - Survey Ship", "survey_ship", "Survey ship DUE DILIGENCE", "A ring corridor around the lab and the reactor. Two ways round.");
            s_Plan = MapScale;
            Transform root = map.transform;
            var wall = M("Wall");
            const float LY = WH - 0.4f;
            const SalvageCategoryMask S = SalvageCategoryMask.SmallScrap;
            const SalvageCategoryMask E = SalvageCategoryMask.Electronics;
            const SalvageCategoryMask N = SalvageCategoryMask.Novelty;
            var cold = new Color(0.7f, 0.8f, 1f);
            var violet = new Color(0.75f, 0.6f, 1f);

            float benchX = X(-5f) + 0.6f, benchZ = Z(15.5f), consoleZ = Z(25.4f), lockersX = X(-12.4f);
            Vector3 vaultAt = new Vector3(X(13f) - 1.0f, 0, Z(23.6f) - 1.0f);

            // Ring corridor: four runs around the core block (6 m wide now, 4 m before).
            FloorAndCeiling("C Ring S Slabs", X(-9), X(9), JoinZ, Z(11.6f), WH, M("Floor"));
            FloorAndCeiling("C Ring N Slabs", X(-9), X(9), Z(27.6f), Z(31.6f), WH, M("Floor"));
            FloorAndCeiling("C Ring W Slabs", X(-9), X(-5), Z(11.6f), Z(27.6f), WH, M("Floor"));
            FloorAndCeiling("C Ring E Slabs", X(5), X(9), Z(11.6f), Z(27.6f), WH, M("Floor"));
            JoinWall("C Ring S", X(-9), X(9), WH, wall);
            float sideGap = Z(19.6f) - JoinZ, northGap = -X(-9);
            Wall("C Ring W", new Vector2(X(-9), JoinZ), new Vector2(X(-9), Z(31.6f)), WH, wall, new Gap(sideGap, 1.8f, 2.2f));
            Wall("C Ring E", new Vector2(X(9), JoinZ), new Vector2(X(9), Z(31.6f)), WH, wall, new Gap(sideGap, 1.6f, 2.2f));
            Wall("C Ring N", new Vector2(X(-9), Z(31.6f)), new Vector2(X(9), Z(31.6f)), WH, wall, new Gap(northGap, 1.6f, 2.2f));
            RailLine(new Vector2(X(-9), JoinZ), new Vector2(-2f, JoinZ), Vector2.up);
            RailLine(new Vector2(2f, JoinZ), new Vector2(X(9), JoinZ), Vector2.up);
            RailLine(new Vector2(X(-9), JoinZ), new Vector2(X(-9), Z(31.6f)), Vector2.right, new Gap(sideGap, 1.8f, 2.2f));
            RailLine(new Vector2(X(9), JoinZ), new Vector2(X(9), Z(31.6f)), Vector2.left, new Gap(sideGap, 1.6f, 2.2f));
            RailLine(new Vector2(X(-9), Z(31.6f)), new Vector2(X(9), Z(31.6f)), Vector2.down, new Gap(northGap, 1.6f, 2.2f));
            RailLine(new Vector2(X(-5), Z(11.6f)), new Vector2(X(5), Z(11.6f)), Vector2.down, new Gap(-X(-5), 1.8f, 2.2f));
            RailLine(new Vector2(X(-5), Z(27.6f)), new Vector2(X(5), Z(27.6f)), Vector2.up, new Gap(-X(-5), 1.6f, 2.1f));
            RailLine(new Vector2(X(-5), Z(11.6f)), new Vector2(X(-5), Z(27.6f)), Vector2.left);
            RailLine(new Vector2(X(5), Z(11.6f)), new Vector2(X(5), Z(27.6f)), Vector2.right);
            const float i0 = 0.35f;
            TeleportArea("C Ring S", X(-9) + i0, X(9) - i0, JoinZ + i0, Z(11.6f) - i0);
            TeleportArea("C Ring N", X(-9) + i0, X(9) - i0, Z(27.6f) + i0, Z(31.6f) - i0);
            TeleportArea("C Ring W", X(-9) + i0, X(-5) - i0, Z(11.6f) - i0, Z(27.6f) + i0);
            TeleportArea("C Ring E", X(5) + i0, X(9) - i0, Z(11.6f) - i0, Z(27.6f) + i0);
            TeleportArea("C Store Doorway", X(-9) - 0.6f, X(-9) + 0.6f, Z(19.6f) - 0.5f, Z(19.6f) + 0.5f);
            TeleportArea("C Vault Doorway", X(9) - 0.6f, X(9) + 0.6f, Z(19.6f) - 0.5f, Z(19.6f) + 0.5f);
            TeleportArea("C Deck Doorway", -0.5f, 0.5f, Z(31.6f) - 0.6f, Z(31.6f) + 0.6f);
            Beams(X(-9), X(9), JoinZ, Z(11.6f), WH, true);
            Beams(X(-9), X(9), Z(27.6f), Z(31.6f), WH, true);
            Beams(X(-9), X(-5), Z(11.6f), Z(27.6f), WH, false);
            Beams(X(5), X(9), Z(11.6f), Z(27.6f), WH, false);

            // Core: lab to the south, reactor to the north, a sealed hatch between them.
            Room("C Lab", X(-5), X(5), Z(11.6f), Z(19.4f), WH, "NSEW", wall, new Opening('S', 0, 1.8f, 2.2f), new Opening('N', X(3), 1.0f, 2.0f),
                RailGap('W', benchZ, 3.4f));
            Room("C Reactor", X(-5), X(5), Z(19.4f), Z(27.6f), WH, "NEW", wall, new Opening('N', 0, 1.6f, 2.1f), new Opening('S', X(3), 1.0f, 2.0f),
                RailGap('W', consoleZ, 2.2f));
            Room("C Specimen Store", X(-15), X(-9), Z(15.6f), Z(23.6f), WH, "NSW", wall, new Opening('E', Z(19.6f), 1.8f, 2.2f),
                RailGap('N', lockersX, 2.3f), RailGap('W', Z(18f), 1.0f));
            Room("C Data Vault", X(9), X(13), Z(15.6f), Z(23.6f), WH, "NSE", wall, new Opening('W', Z(19.6f), 1.6f, 2.2f),
                RailGap('E', vaultAt.z, 2.0f), RailGap('N', vaultAt.x, 2.0f));
            Room("C Observation Deck", X(-6), X(6), Z(31.6f), Z(36.6f), WH, "NEW", wall, new Opening('S', 0, 1.6f, 2.2f),
                RailGap('W', Z(34.2f), 2.8f), RailGap('E', Z(33.4f), 2.2f));
            var reactorDoor = BuildDoor("Reactor Door", "c_door_reactor", P(0, 0, 27.6f), 1.6f, 2.1f, false, false, true);
            Exclusion(root, "No Drone: C Hatch", new Vector3(X(3f), 1.2f, Z(19.4f)), new Vector3(1.4f, 2.6f, 1.3f));

            // Panels and loot
            var reactorPanel = SealedCompartment("Reactor Housing", "c_panel_reactor", "REACTOR ACCESS", P(0, 0, 23.9f), new Vector3(1.8f, 2.4f, 1.8f), M("ReactorShell"), out Vector3 reactorCavity);
            var vaultPanel = SealedCompartment("Sample Vault", "c_panel_cage", "SAMPLE VAULT", vaultAt, new Vector3(1.7f, 2.2f, 1.7f), M("ContainerBlue"), out Vector3 vaultCavity);
            MakeCuttable("Lab Hatch Cover", "c_panel_hatch", "REACTOR HATCH", new Vector3(X(3f), 1.02f, Z(19.4f) - T * 0.5f - 0.03f), Vector3.back, new Vector2(1.12f, 2.04f), s_HatchCuts);
            Spawn("c_reactor_housing", reactorCavity, SalvageCategoryMask.ReactorComponent, reactorPanel);
            Spawn("c_sample_vault", vaultCavity, E | N, vaultPanel);
            RackMount("c_mount_rack", new Vector3(X(-15f), 0, Z(18f)), Vector3.right, "c_store_rack");

            Spawn("c_airlock", new Vector3(1.3f, 1.0f, 4.4f), S);
            Spawn("c_ring_sw", P(-7.6f, 1.2f, 9.2f), S);
            Spawn("c_ring_se", P(7.4f, 1.3f, 10.4f), S | E);
            Spawn("c_ring_w", P(-8.0f, 1.0f, 14.2f), S);
            Spawn("c_ring_e", P(8.0f, 1.3f, 24.6f), S | E);
            Spawn("c_ring_nw", P(-7.6f, 1.2f, 30.4f), S | N);
            Spawn("c_ring_n", P(3.2f, 1.1f, 30.6f), S);
            Spawn("c_lab_bench", P(-3.5f, 1.25f, 17.6f), E | N);
            Spawn("c_lab_floor", P(2.0f, 1.0f, 13.2f), S);
            Spawn("c_lab_tank", P(3.4f, 1.2f, 16.0f), E | S);
            Spawn("c_store_shelf", P(-13.6f, 1.3f, 22.2f), N | S); // the far corner of the store
            Spawn("c_store_floor", P(-11.2f, 1.1f, 16.8f), S | E);
            Spawn("c_vault_desk", new Vector3(X(9f) + 1.5f, 1.2f, Z(15.6f) + 2.4f), S | E);
            Spawn("c_reactor_west", P(-4.0f, 1.1f, 20.6f), S);
            Spawn("c_reactor_console", new Vector3(benchX + 1.0f, 1.25f, Z(25.6f)), E | N);
            Spawn("c_deck_west", P(-4.8f, 1.2f, 35.4f), S | E);
            Spawn("c_deck_east", new Vector3(X(6f) - 1.2f, 1.3f, Z(36.6f) - 0.9f), S | N); // the deck's far corner

            // Ring dressing: pipes overhead, crates in the corners (cover), vents and windows.
            Pipe("C Ring Pipe S", new Vector3(X(-9) + 0.4f, WH - 0.2f, JoinZ + 0.5f), new Vector3(X(9) - 0.4f, WH - 0.2f, JoinZ + 0.5f), 0.06f, "Pipe");
            Pipe("C Ring Pipe N", new Vector3(X(-9) + 0.4f, WH - 0.2f, Z(31.6f) - 0.5f), new Vector3(X(9) - 0.4f, WH - 0.2f, Z(31.6f) - 0.5f), 0.06f, "Steel");
            Pipe("C Ring Pipe W", new Vector3(X(-9) + 0.5f, WH - 0.2f, JoinZ + 0.6f), new Vector3(X(-9) + 0.5f, WH - 0.2f, Z(31.6f) - 0.6f), 0.05f, "Pipe");
            Pipe("C Ring Pipe E", new Vector3(X(9) - 0.5f, WH - 0.2f, JoinZ + 0.6f), new Vector3(X(9) - 0.5f, WH - 0.2f, Z(31.6f) - 0.6f), 0.05f, "Pipe");
            Container(new Vector3(X(-9) + 0.85f, 0, JoinZ + 0.85f), new Vector3(1.0f, 1.0f, 1.0f), "ContainerBlue");
            Container(new Vector3(X(9) - 0.85f, 0, Z(31.6f) - 0.85f), new Vector3(1.0f, 1.2f, 1.0f));
            Container(new Vector3(X(-9) + 0.85f, 0, Z(26.2f)), new Vector3(1.0f, 1.6f, 1.4f));
            Container(new Vector3(X(9) - 0.85f, 0, Z(13.2f)), new Vector3(1.0f, 1.4f, 1.4f), "ContainerBlue");
            Container(new Vector3(X(-9) + 0.85f, 0, Z(16.6f)), new Vector3(1.0f, 1.2f, 1.2f));
            Container(new Vector3(X(9) - 0.85f, 0, Z(22.4f)), new Vector3(1.0f, 1.0f, 1.4f), "ContainerBlue");
            Barrel(P(5.6f, 0, 8.3f));
            Barrel(P(-5.6f, 0, 30.9f), "BarrelBlue");
            Vent(new Vector3(X(-9) + T * 0.5f + 0.01f, 2.25f, Z(12.4f)), Vector3.right);
            Vent(new Vector3(X(9) - T * 0.5f - 0.01f, 2.25f, Z(26.4f)), Vector3.left);
            foreach (float wx in new[] { -10.4f, -5.4f, 5.4f, 10.4f })
                Window(new Vector3(wx, 1.7f, JoinZ + T * 0.5f + 0.015f), Vector3.forward, 2.2f, 0.9f);

            // Lab
            Box(s_Props, "Lab Bench", new Vector3(benchX, 0.45f, benchZ), new Vector3(0.8f, 0.9f, 3.2f), M("Trim"));
            ConsoleDetail(new Vector3(benchX, 0.9f, benchZ - 0.9f), 1.2f, Vector3.right);
            ConsoleDetail(new Vector3(benchX, 0.9f, benchZ + 1.0f), 1.2f, Vector3.right);
            Box(s_Props, "Lab Table", new Vector3(0f, 0.45f, Z(15.6f) + 1.2f), new Vector3(2.2f, 0.9f, 0.9f), M("Trim"));
            ConsoleDetail(new Vector3(0f, 0.9f, Z(15.6f) + 1.2f), 1.4f, Vector3.back);
            Barrel(new Vector3(X(5f) - 0.7f, 0, Z(14.4f)), "BarrelBlue");
            Barrel(new Vector3(X(5f) - 0.7f, 0, Z(17.6f)), "BarrelBlue");
            Barrel(new Vector3(X(5f) - 0.7f, 0, Z(15.2f)));
            Poster("SAMPLE 14 IS MISSING\nAGAIN", new Vector3(X(-1.6f), 1.7f, Z(19.4f) - T * 0.5f - 0.015f), Vector3.back, 0.9f, 0.5f, 0.45f);

            // Specimen store, data vault, observation deck
            Container(new Vector3(X(-15) + 1.1f, 0, Z(15.6f) + 0.9f), new Vector3(1.4f, 1.0f, 1.0f));
            Container(new Vector3(X(-9) - 1.2f, 0, Z(23.6f) - 0.9f), new Vector3(1.0f, 0.9f, 1.0f), "ContainerBlue");
            LockerRow(new Vector3(lockersX, 0, Z(23.6f) - T * 0.5f - 0.24f), Vector3.back, 4);
            Box(s_Props, "Vault Desk", new Vector3(X(9f) + 1.0f, 0.4f, Z(15.6f) + 0.8f), new Vector3(1.3f, 0.8f, 0.7f), M("Trim"));
            ConsoleDetail(new Vector3(X(9f) + 1.0f, 0.8f, Z(15.6f) + 0.8f), 1.0f, Vector3.forward);
            Vent(new Vector3(X(13f) - T * 0.5f - 0.01f, 2.25f, Z(18f)), Vector3.left);
            foreach (float wx in new[] { -6.6f, -3.3f, 0f, 3.3f, 6.6f })
                Window(new Vector3(wx, 1.6f, Z(36.6f) - T * 0.5f - 0.015f), Vector3.back, 2.8f, 1.2f);
            Box(s_Props, "Deck Bench W", new Vector3(X(-6f) + 0.6f, 0.25f, Z(34.2f)), new Vector3(0.6f, 0.5f, 2.6f), M("Trim"));
            Box(s_Props, "Deck Bench E", new Vector3(X(6f) - 0.6f, 0.25f, Z(33.4f)), new Vector3(0.6f, 0.5f, 2.0f), M("Trim"));

            // Reactor
            Box(s_Props, "Reactor Console", new Vector3(benchX, 0.45f, consoleZ), new Vector3(0.7f, 0.9f, 1.8f), M("Trim"));
            ConsoleDetail(new Vector3(benchX, 0.9f, consoleZ), 1.5f, Vector3.right);
            Barrel(new Vector3(X(5f) - 0.7f, 0, Z(26.6f)));
            Barrel(new Vector3(X(-5f) + 0.7f, 0, Z(22.8f)), "BarrelBlue");
            Rail(new Vector3(-1.25f, RailY, Z(23.9f) - 1f), new Vector3(-1.25f, RailY, Z(23.9f) + 1f));
            Rail(new Vector3(1.25f, RailY, Z(23.9f) - 1f), new Vector3(1.25f, RailY, Z(23.9f) + 1f));
            Dust("C Dust Reactor", P(0, 1.6f, 23.5f), new Vector3(13.5f, 2.6f, 10.5f), 70);
            Dust("C Dust Ring", P(0, 1.6f, 9.6f), new Vector3(24f, 2.6f, 5f), 55);

            Web(new Vector3(X(-9) + 0.1f, WH - 0.05f, JoinZ + 0.1f), Vector3.right, Vector3.forward, Vector3.down, 1.8f);
            Web(new Vector3(X(9) - 0.1f, WH - 0.05f, Z(31.6f) - 0.1f), Vector3.left, Vector3.back, Vector3.down, 1.9f);
            EggSacs(new Vector3(X(-5f) + T * 0.5f, 1.9f, Z(22.4f)), Vector3.right, 7, 95);

            Tripwire("c_ring_west", new Vector3(X(-9) + 0.2f, 1.2f, Z(17f)), new Vector3(X(-5) - 0.2f, 1.35f, Z(17f)));
            Tripwire("c_ring_east", new Vector3(X(5) + 0.2f, 1.2f, Z(22f)), new Vector3(X(9) - 0.2f, 1.3f, Z(22f)));
            Tripwire("c_lab_door", new Vector3(-0.9f, 1.2f, Z(12f)), new Vector3(0.9f, 1.3f, Z(12f)));
            EggSacs(new Vector3(X(9f) - T * 0.5f, 1.9f, Z(12.6f)), Vector3.left, 6, 96);

            Poster("LAB NOTE\nSpecimen 14 has doubled in size.\nIt watches the door.", new Vector3(X(-5f) + T * 0.5f + 0.012f, 1.5f, Z(13.4f)), Vector3.right, 0.95f, 0.55f, 0.36f);
            Poster("LOG 30\nIf you can hear it walking,\nit is not hunting you yet.", new Vector3(X(-9f) + T * 0.5f + 0.012f, 1.5f, Z(24.8f)), Vector3.right, 0.95f, 0.55f, 0.36f);
            Poster("LOG 33\nIt has stopped walking.", new Vector3(X(3.6f), 1.5f, Z(31.6f) + T * 0.5f + 0.012f), Vector3.forward, 0.8f, 0.4f, 0.42f);

            // Signs
            Sign("LABORATORY", new Vector3(0, 2.45f, Z(11.6f) - T * 0.5f - 0.02f), Vector3.back, 1.4f);
            Sign("REACTOR", new Vector3(0, 2.45f, Z(27.6f) + T * 0.5f + 0.02f), Vector3.forward, 1.5f, new Color(0.4f, 1f, 0.9f));
            Sign("SPECIMEN STORE", new Vector3(X(-9f) + T * 0.5f + 0.02f, 2.5f, Z(19.6f)), Vector3.right, 1.2f);
            Sign("DATA VAULT", new Vector3(X(9f) - T * 0.5f - 0.02f, 2.5f, Z(19.6f)), Vector3.left, 1.2f);
            Sign("OBSERVATION DECK", new Vector3(0, 2.5f, Z(31.6f) - T * 0.5f - 0.02f), Vector3.back, 1.2f);
            Sign("REACTOR HATCH\nSEALED", new Vector3(X(3f) - 1.3f, 1.9f, Z(19.4f) - T * 0.5f - 0.02f), Vector3.back, 0.9f);
            Sign("<< RING  /  RING >>\nBOTH WAYS MEET AT THE FAR SIDE", new Vector3(0, 2.2f, Z(11.6f) - T * 0.5f - 0.02f), Vector3.back, 0.6f, new Color(0.6f, 0.9f, 1f));
            Sign("IT NESTS HERE\nDO NOT WAKE IT", new Vector3(X(5.2f), 2.3f, Z(31.6f) + T * 0.5f + 0.02f), Vector3.forward, 0.8f, new Color(1f, 0.5f, 0.4f));

            // Lights: cold white and violet; more of them along the longer ring runs and in the bigger rooms.
            RoomLight("C Ring Light S", 0, LY, Z(9.6f), cold, 2.6f, 9f);
            RoomLight("C Ring Light SW", X(-7f), LY, Z(10f), violet, 2.4f, 9f);
            RoomLight("C Ring Light SE", X(7f), LY, Z(10f), cold, 2.4f, 9f);
            RoomLight("C Ring Light S W", X(-3.6f), LY, Z(9.6f), cold, 2.2f, 8f);
            RoomLight("C Ring Light S E", X(3.6f), LY, Z(9.6f), violet, 2.2f, 8f);
            RoomLight("C Ring Light W", X(-7f), LY, Z(19.6f), cold, 2.6f, 9.5f);
            RoomLight("C Ring Light E", X(7f), LY, Z(19.6f), violet, 2.6f, 9.5f);
            RoomLight("C Ring Light W S", X(-7f), LY, Z(14.8f), violet, 2.2f, 8f);
            RoomLight("C Ring Light W N", X(-7f), LY, Z(24.4f), cold, 2.2f, 8f);
            RoomLight("C Ring Light E S", X(7f), LY, Z(14.8f), cold, 2.2f, 8f);
            RoomLight("C Ring Light E N", X(7f), LY, Z(24.4f), violet, 2.2f, 8f);
            RoomLight("C Ring Light NW", X(-7f), LY, Z(29.4f), cold, 2.4f, 9f);
            RoomLight("C Ring Light NE", X(7f), LY, Z(29.4f), violet, 2.4f, 9f);
            RoomLight("C Ring Light N W", X(-3.6f), LY, Z(29.6f), violet, 2.2f, 8f);
            RoomLight("C Ring Light N E", X(3.6f), LY, Z(29.6f), cold, 2.2f, 8f);
            RoomLight("C Lab Light", 0, LY, Z(13.6f), Color.white, 3.4f, 9f);
            RoomLight("C Lab Light N", 0, LY, Z(17.6f), Color.white, 3.0f, 9f);
            RoomLight("C Reactor Light", 0, LY - 0.1f, Z(21.4f), new Color(0.3f, 1f, 0.85f), 3.6f, 9f);
            RoomLight("C Reactor Light W", X(-1.8f), LY - 0.1f, Z(26f), new Color(0.3f, 1f, 0.85f), 2.8f, 8f);
            RoomLight("C Reactor Light E", X(1.8f), LY - 0.1f, Z(26f), new Color(0.3f, 0.9f, 1f), 2.8f, 8f);
            RoomLight("C Store Light", X(-12f), LY, Z(17.6f), new Color(1f, 0.9f, 0.7f), 3f, 8.5f);
            RoomLight("C Store Light N", X(-12f), LY, Z(21.6f), new Color(1f, 0.9f, 0.7f), 2.6f, 8.5f);
            RoomLight("C Vault Light", X(11f), LY, Z(17.6f), new Color(1f, 0.3f, 0.25f), 2.6f, 7.5f);
            RoomLight("C Vault Light N", X(11f), LY, Z(21.6f), new Color(1f, 0.3f, 0.25f), 2.4f, 7.5f);
            RoomLight("C Deck Light", X(-3.5f), LY, Z(34f), new Color(0.5f, 0.7f, 1f), 3f, 9.5f);
            RoomLight("C Deck Light E", X(3.5f), LY, Z(34f), new Color(0.5f, 0.7f, 1f), 3f, 9.5f);
            Strip("C Ring Strip S", new Vector3(0, WH - 0.03f, Z(9.6f)), new Vector3(18f, 0.03f, 0.1f), "GlowWhite");
            Strip("C Ring Strip N", new Vector3(0, WH - 0.03f, Z(29.6f)), new Vector3(18f, 0.03f, 0.1f), "GlowWhite");
            Strip("C Ring Strip W", new Vector3(X(-7f), WH - 0.03f, Z(19.6f)), new Vector3(0.1f, 0.03f, 15f), "GlowWhite");
            Strip("C Ring Strip E", new Vector3(X(7f), WH - 0.03f, Z(19.6f)), new Vector3(0.1f, 0.03f, 15f), "GlowWhite");
            Strip("C Lab Strip", new Vector3(0, WH - 0.03f, Z(15.5f)), new Vector3(7.5f, 0.03f, 0.1f), "GlowWhite");
            Strip("C Reactor Strip W", new Vector3(X(-2.8f), WH - 0.03f, Z(23.5f)), new Vector3(0.1f, 0.03f, 7.5f), "GlowTeal");
            Strip("C Reactor Strip E", new Vector3(X(2.8f), WH - 0.03f, Z(23.5f)), new Vector3(0.1f, 0.03f, 7.5f), "GlowTeal");

            Anchor("C Ring South", 0f, Z(9.6f));
            Anchor("C Ring West", X(-7f), Z(19.6f));
            Anchor("C Ring East", X(7f), Z(19.6f));
            Anchor("C Ring North", 0f, Z(29.6f));
            Anchor("C Lab", 0f, Z(13.4f));
            Anchor("C Reactor", 0f, Z(26.2f));
            Anchor("C Store", X(-11.6f), Z(19.6f));
            Anchor("C Vault", X(10.4f), Z(19.6f));
            Anchor("C Deck", 0f, Z(33.2f));

            Junk(s_JunkCanPrefab, P(-3.2f, 1.5f, 9.4f));
            Junk(s_JunkCanPrefab, P(7.2f, 1.3f, 16.4f));
            Junk(s_JunkCanPrefab, P(1.4f, 1.4f, 34.6f));
            Junk(s_JunkCratePrefab, P(-7.0f, 1.3f, 22.6f));
            Junk(s_JunkCratePrefab, P(1.6f, 1.3f, 17.4f));
            Junk(s_JunkCratePrefab, P(4.4f, 1.4f, 29.4f));

            Waypoint("S0", 0, Z(9.6f)); Waypoint("SW", X(-7f), Z(9.6f)); Waypoint("SE", X(7f), Z(9.6f));
            Waypoint("W1", X(-7f), Z(15f)); Waypoint("W2", X(-7f), Z(19.6f)); Waypoint("W3", X(-7f), Z(24.4f));
            Waypoint("E1", X(7f), Z(15f)); Waypoint("E2", X(7f), Z(19.6f)); Waypoint("E3", X(7f), Z(24.4f));
            Waypoint("NW", X(-7f), Z(29.6f)); Waypoint("N0", 0, Z(29.6f)); Waypoint("NE", X(7f), Z(29.6f));
            Waypoint("L0", 0, Z(13.2f)); Waypoint("L1", X(-2.4f), Z(15.6f)); Waypoint("L2", X(2.2f), Z(15.6f));
            Waypoint("R0", 0, Z(26.2f)); Waypoint("R1", X(-3.3f), Z(25.2f)); Waypoint("R2", X(-3.3f), Z(21.2f)); Waypoint("R3", X(3.3f), Z(21.2f)); Waypoint("R4", X(3.3f), Z(25.2f));
            Waypoint("T0", X(-10.6f), Z(19.6f)); Waypoint("T1", X(-12.8f), Z(19.6f));
            Waypoint("X0", X(10.2f), Z(19.6f)); Waypoint("X1", X(10.8f), Z(18.0f));
            Waypoint("O0", 0, Z(33.2f)); Waypoint("O1", X(-3.4f), Z(34.4f)); Waypoint("O2", X(3.4f), Z(34.4f));
            Link("S0", "SW", "W1", "W2", "W3", "NW", "N0", "NE", "E3", "E2", "E1", "SE", "S0");
            Link("S0", "L0", "L1");
            Link("L0", "L2");
            Link("N0", "R0", "R1", "R2", "R3", "R4", "R0");
            Link("W2", "T0", "T1");
            Link("E2", "X0", "X1");
            Link("N0", "O0", "O1");
            Link("O0", "O2");

            var dock = Empty(root, "Creature Dock", new Vector3(X(6f) - 1.4f, 1.45f, Z(36.6f) - 1.2f), Quaternion.Euler(0, 200f, 0)).transform;
            var hum = MapHum("C Reactor Hum", P(0, 1.4f, 23.9f), 0.55f);
            EndMap(map, dock, new[] { reactorDoor }, new[] { hum }, sharedLights, sharedGlow,
                "O2", "O0", "N0", "NE", "E3", "E2", "X0", "X1", "X0", "E2", "E1", "SE", "S0", "L0", "L1", "L0", "L2", "L0", "S0", "SW", "W1", "W2",
                "T0", "T1", "T0", "W2", "W3", "NW", "N0", "R0", "R1", "R2", "R3", "R4", "R0", "N0", "O0", "O1");
            return map;
        }

        // ------------------------------------------------------------------ the tug's flight deck

        /// <summary>
        /// The lobby upgrade: a flight deck behind the cabin with a wraparound windscreen, two seats, a
        /// console, side windows, a crew record screen and a few comforts in the cabin itself.
        /// </summary>
        static void BuildCockpit()
        {
            var tug = M("WallTug");
            const float CH = 2.5f, z0 = -6.6f;

            FloorAndCeiling("Flight Deck Slabs", -2, 2, z0, -3, CH, M("Floor"));
            Wall("Flight Deck W", new Vector2(-2, z0), new Vector2(-2, -3), CH, tug, new Gap(1.8f, 1.6f, 2.1f)); // through to the supply bay

            // --- supply bay: a side room for the company store, crates and lockers
            Room("Supply Bay", -6, -2, z0, -3, CH, "NSW", tug, new Opening('E', -4.8f, 1.6f, 2.1f));
            TeleportArea("Supply Bay Doorway", -2.6f, -1.4f, -5.3f, -4.3f);
            Anchor("Supply Bay", -3.6f, -4.8f);
            PointLight(s_Lights, "Supply Bay Light", new Vector3(-4f, 2.1f, -4.8f), new Color(1f, 0.9f, 0.7f), 3.6f, 6f);
            Strip("Supply Bay Strip", new Vector3(-3.6f, CH - 0.03f, -4.8f), new Vector3(0.1f, 0.03f, 2.6f), "GlowWarm");
            Sign("SUPPLY BAY", new Vector3(-2f + T * 0.5f + 0.02f, 2.3f, -4.8f), Vector3.right, 1.0f, new Color(1f, 0.85f, 0.4f));
            Container(new Vector3(-2.75f, 0, -6.05f), new Vector3(0.9f, 0.8f, 0.8f), "ContainerBlue");
            Container(new Vector3(-3.7f, 0, -6.1f), new Vector3(0.7f, 0.6f, 0.7f));
            Barrel(new Vector3(-2.5f, 0, -3.5f), "BarrelBlue");
            LockerRow(new Vector3(-3.6f, 0, -3f - T * 0.5f - 0.24f), Vector3.back, 3);
            Poster("STORE CREDIT IS\nSTILL CREDIT", new Vector3(-4.6f, 1.7f, z0 + T * 0.5f + 0.015f), Vector3.forward, 0.9f, 0.5f, 0.42f);
            Wall("Flight Deck E", new Vector2(2, z0), new Vector2(2, -3), CH, tug);
            Wall("Flight Deck S", new Vector2(-2, z0), new Vector2(2, z0), CH, tug);
            TeleportArea("Flight Deck", -1.65f, 1.65f, -4.45f, -3.25f);
            TeleportArea("Flight Deck Doorway", -1.45f, -0.25f, -3.5f, -2.5f);
            Anchor("Flight Deck", -0.85f, -3.9f);
            Rail(new Vector3(-1.8f, RailY, -3.85f), new Vector3(-1.8f, RailY, -3.3f));
            Rail(new Vector3(1.8f, RailY, -3.9f), new Vector3(1.8f, RailY, -3.2f));
            Rail(new Vector3(0f, CH - 0.25f, -5.6f), new Vector3(0f, CH - 0.25f, -3.3f));

            // Windscreen: three panes of stars with frames between, and a side window each way.
            float glassZ = z0 + T * 0.5f + 0.012f;
            for (int i = 0; i < 3; i++)
            {
                float x = -1.2f + i * 1.2f;
                Box(s_Props, "Windscreen Pane", new Vector3(x, 1.6f, glassZ), new Vector3(1.12f, 1.1f, 0.02f), M("Stars"), GameLayers.Default, false);
            }
            Box(s_Props, "Windscreen Sill", new Vector3(0, 1.02f, glassZ + 0.03f), new Vector3(3.7f, 0.08f, 0.08f), M("Trim"), GameLayers.Default, false);
            Box(s_Props, "Windscreen Header", new Vector3(0, 2.18f, glassZ + 0.03f), new Vector3(3.7f, 0.08f, 0.08f), M("Trim"), GameLayers.Default, false);
            foreach (float x in new[] { -0.6f, 0.6f })
                Box(s_Props, "Windscreen Post", new Vector3(x, 1.6f, glassZ + 0.03f), new Vector3(0.08f, 1.16f, 0.08f), M("Trim"), GameLayers.Default, false);
            Window(new Vector3(2f - T * 0.5f - 0.015f, 1.6f, -5.9f), Vector3.left, 1.0f, 0.8f);

            // Console across the front, a pedestal between the seats, an overhead panel.
            Box(s_Props, "Flight Console", new Vector3(0, 0.5f, -6.08f), new Vector3(3.6f, 1.0f, 0.75f), M("Trim"));
            ConsoleDetail(new Vector3(-0.95f, 1.0f, -6.08f), 1.3f, Vector3.forward);
            ConsoleDetail(new Vector3(0.95f, 1.0f, -6.08f), 1.3f, Vector3.forward);
            Box(s_Props, "Centre Pedestal", new Vector3(0, 0.35f, -5.35f), new Vector3(0.36f, 0.7f, 0.8f), M("Trim"));
            var pedestal = Empty(s_Props, "Pedestal Detail", new Vector3(0, 0.7f, -5.35f), null, GameLayers.Default).transform;
            CBox(pedestal, "Throttle Slot", new Vector3(0, 0.006f, 0.05f), new Vector3(0.2f, 0.012f, 0.4f), M("Chip"), 0.003f);
            CBox(pedestal, "Throttle", new Vector3(-0.05f, 0.07f, 0.1f), new Vector3(0.04f, 0.14f, 0.05f), M("PaintOrange"), 0.012f, new Vector3(18, 0, 0));
            CBox(pedestal, "Throttle 2", new Vector3(0.05f, 0.07f, 0.02f), new Vector3(0.04f, 0.14f, 0.05f), M("PaintOrange"), 0.012f, new Vector3(-10, 0, 0));
            for (int i = 0; i < 4; i++)
                CBox(pedestal, "Switch", new Vector3(-0.09f + i * 0.06f, 0.012f, -0.28f), new Vector3(0.035f, 0.024f, 0.035f), M(i == 1 ? "GlowRed" : i == 2 ? "ButtonAmber" : "Steel"), 0.006f);
            var overhead = Empty(s_Props, "Overhead Panel", new Vector3(0, CH - 0.06f, -5.4f), null, GameLayers.Default).transform;
            CBox(overhead, "Panel", Vector3.zero, new Vector3(2.0f, 0.08f, 1.0f), M("Trim"), 0.02f);
            for (int i = 0; i < 14; i++)
                CBox(overhead, "Toggle", new Vector3(-0.78f + (i % 7) * 0.26f, -0.05f, -0.2f + (i / 7) * 0.4f), new Vector3(0.05f, 0.03f, 0.05f),
                    M(i % 5 == 0 ? "GlowRed" : i % 3 == 0 ? "GlowTeal" : i % 4 == 0 ? "ButtonAmber" : "Steel"), 0.008f);

            // Two seats facing the windscreen.
            foreach (float x in new[] { -0.95f, 0.95f })
            {
                Box(s_Props, "Seat Base", new Vector3(x, 0.22f, -5.2f), new Vector3(0.3f, 0.44f, 0.3f), M("Steel"));
                var seat = Empty(s_Props, "Seat", new Vector3(x, 0, -5.2f), null, GameLayers.Default).transform;
                CBox(seat, "Cushion", new Vector3(0, 0.5f, 0), new Vector3(0.58f, 0.13f, 0.56f), M("Rubber"), 0.04f);
                CBox(seat, "Back", new Vector3(0, 0.95f, 0.3f), new Vector3(0.56f, 0.82f, 0.13f), M("Rubber"), 0.04f, new Vector3(8, 0, 0));
                CBox(seat, "Headrest", new Vector3(0, 1.44f, 0.36f), new Vector3(0.32f, 0.2f, 0.12f), M("PaintOrange"), 0.04f, new Vector3(8, 0, 0));
                foreach (float side in new[] { -1f, 1f })
                    CBox(seat, "Armrest", new Vector3(side * 0.33f, 0.72f, 0.02f), new Vector3(0.07f, 0.06f, 0.44f), M("Trim"), 0.02f);
                // Control yoke on a column in front of each seat.
                Part(seat, "Yoke Column", Tube(0.025f, 0.5f, 8), new Vector3(0, 0.82f, -0.52f), M("Steel"), new Vector3(65, 0, 0));
                Part(seat, "Yoke", Torus(0.13f, 0.018f, 16, 6), new Vector3(0, 0.95f, -0.3f), M("Rubber"), new Vector3(65, 0, 0));
            }

            // Crew record screen on the starboard wall.
            var rec = Empty(s_Props, "Crew Record", new Vector3(2f - T * 0.5f - 0.02f, 1.5f, -4.55f), Facing(Vector3.left), GameLayers.Default);
            Prim(rec.transform, "Back", PrimitiveType.Cube, new Vector3(0, 0, 0.012f), new Vector3(1.3f, 1.0f, 0.02f), M("Screen"));
            CBox(rec.transform, "Header Bar", new Vector3(0, 0.485f, 0.006f), new Vector3(1.3f, 0.03f, 0.03f), M("GlowTeal"), 0.006f);
            var recTitle = Text(rec.transform, "Title", "CREW RECORD", new Vector3(0, 0.38f, -0.002f), Quaternion.identity, 0.8f, new Vector2(1.2f, 0.14f), new Color(0.6f, 1f, 0.9f));
            recTitle.fontStyle = FontStyles.Bold;
            var recBody = Text(rec.transform, "Body", "", new Vector3(0, -0.07f, -0.002f), Quaternion.identity, 0.52f, new Vector2(1.2f, 0.72f), new Color(0.85f, 0.95f, 1f));
            rec.AddComponent<CrewRecordBoard>().body = recBody;

            // Light and signage.
            PointLight(s_Lights, "Flight Deck Light", new Vector3(0, 2.1f, -4.6f), new Color(0.6f, 0.85f, 1f), 3.2f, 5.5f);
            Strip("Flight Deck Strip", new Vector3(0, CH - 0.03f, -4.1f), new Vector3(2.6f, 0.03f, 0.1f), "GlowTeal");
            Sign("FLIGHT DECK", new Vector3(-0.85f, 2.5f, -3f + T * 0.5f + 0.02f), Vector3.forward, 1.0f, new Color(0.6f, 1f, 0.9f));
            Sign("PAYCHEQUE\nSALVAGE TUG  CLASS IV", new Vector3(0, 2.32f, z0 + T * 0.5f + 0.06f), Vector3.forward, 0.6f, new Color(0.6f, 0.9f, 1f));
            Poster("DAYS SINCE LAST\nINCIDENT:  0", new Vector3(-2f + T * 0.5f + 0.015f, 1.65f, -3.48f), Vector3.right, 0.72f, 0.5f, 0.42f);

            // Cabin comforts: a walkway into the flight deck, a plant, a floating mug.
            Box(s_Props, "Cabin Walkway", new Vector3(-0.85f, 0.007f, -2.6f), new Vector3(1.0f, 0.012f, 5.6f), M("Walkway"), GameLayers.Default, false);
            var plant = Empty(s_Props, "Plant", new Vector3(1.72f, 0, -3.35f), null, GameLayers.Default).transform;
            Part(plant, "Pot", Cone(0.11f, 0.15f, 0.26f, 12), new Vector3(0, 0.13f, 0), M("PaintOrange"));
            for (int i = 0; i < 7; i++)
                Part(plant, "Leaf", Cone(0.035f, 0.002f, 0.34f + (i % 3) * 0.08f, 5), new Vector3(Mathf.Sin(i * 0.9f) * 0.05f, 0.42f + (i % 2) * 0.04f, Mathf.Cos(i * 0.9f) * 0.05f),
                    M("LeafGreen"), new Vector3(Mathf.Cos(i * 0.9f) * 24f, 0, -Mathf.Sin(i * 0.9f) * 24f));
            Junk(s_JunkCanPrefab, new Vector3(0.45f, 1.25f, -5.5f));
        }
    }
}

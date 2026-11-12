using System.Collections.Generic;
using System.Linq;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// Wreck D: an abandoned mining outpost in a lunar lava tube. The tug sets down at the cave mouth and
    /// the airlock opens straight into it.
    ///
    /// Top-down layout (metres, +Z is deeper into the tube):
    ///   Cave mouth       x[-6,9.6]    z[7.6,13.6]   the airlock opens here; crates, a cargo sled
    ///   Main tunnel      x[-1.6,1.6]  z[13.6,33.6]  work lights on stands, some dead
    ///   Habitat module   x[1.6,9.6]   z[13.6,24.6]  pressurised rooms: living (door from the mouth), workshop
    ///                                               (sealed hatch to the tunnel = the shortcut)
    ///   Side tunnel      x[-6,-3.8]   z[13.6,18]    mouth to the skylight chamber
    ///   Skylight chamber x[-12,-1.6]  z[18,28]      collapsed roof, stars above, rubble below
    ///   Back passage     x[-9.2,-6]   z[28,38.8]    skylight chamber to the drill chamber
    ///   Drill chamber    x[-6,6]      z[33.6,41.4]  the drill rig, its power core, the creature's lair
    /// Loops: mouth - tunnel - skylight - side tunnel - mouth, and tunnel - drill - back passage - skylight - tunnel.
    /// The Moon's gravity (1.62 m/s^2) applies inside the tube only (MapGravity).
    /// </summary>
    public static partial class Builder
    {
        public const string CaveMapId = "lava_tube";
        public const float LunarGravity = 1.62f;

        static void CaveMaterials()
        {
            var basalt = MakeTexture("Basalt", 512, (px, n, rng) => PaintRock(px, n, BasaltHeight, new Color(0.15f, 0.145f, 0.14f), new Color(0.26f, 0.25f, 0.24f)));
            var basaltN = MakeNormalMap("Basalt_N", 512, (x, y) => BasaltHeight(x, y, 512), 3.5f);
            var regolith = MakeTexture("Regolith", 512, (px, n, rng) => PaintRock(px, n, RegolithHeight, new Color(0.34f, 0.335f, 0.33f), new Color(0.5f, 0.49f, 0.48f)));
            var regolithN = MakeNormalMap("Regolith_N", 512, (x, y) => RegolithHeight(x, y, 512), 2.5f);
            Lit("Basalt", new Color(0.85f, 0.85f, 0.88f), 0.12f, basalt, null, basaltN);
            Lit("Regolith", new Color(0.95f, 0.95f, 0.95f), 0.05f, regolith, null, regolithN);
            Lit("RockCeiling", new Color(0.6f, 0.6f, 0.62f), 0.1f, basalt, null, basaltN);
        }

        // ------------------------------------------------------------------ tileable noise for the rock textures

        static float Hash01(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xffffff) / (float)0xffffff;
            }
        }

        /// <summary>Value noise that wraps every <paramref name="period"/> lattice cells.</summary>
        static float TileNoise(float x, float y, int period, int seed)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            float sx = fx * fx * (3f - 2f * fx), sy = fy * fy * (3f - 2f * fy);
            int Wrap(int v) => ((v % period) + period) % period;
            float a = Hash01(Wrap(x0), Wrap(y0), seed), b = Hash01(Wrap(x0 + 1), Wrap(y0), seed);
            float c = Hash01(Wrap(x0), Wrap(y0 + 1), seed), d = Hash01(Wrap(x0 + 1), Wrap(y0 + 1), seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, sx), Mathf.Lerp(c, d, sx), sy);
        }

        static float TileFbm(float u, float v, int basePeriod, int octaves, int seed)
        {
            float sum = 0f, amp = 0.5f, norm = 0f;
            int period = basePeriod;
            for (int o = 0; o < octaves; o++)
            {
                sum += TileNoise(u * period, v * period, period, seed + o * 31) * amp;
                norm += amp;
                amp *= 0.5f;
                period *= 2;
            }
            return sum / norm;
        }

        /// <summary>Basalt: lumpy, with small gas bubbles (vesicles) pitted into it.</summary>
        static float BasaltHeight(int x, int y, int n)
        {
            float u = x / (float)n, v = y / (float)n;
            float h = TileFbm(u, v, 6, 5, 11);
            float pits = TileNoise(u * 64f, v * 64f, 64, 5);
            if (pits > 0.86f) h -= (pits - 0.86f) * 2.2f;
            return h;
        }

        /// <summary>Regolith: fine grey dust over gentle ripples and a few pebbles.</summary>
        static float RegolithHeight(int x, int y, int n)
        {
            float u = x / (float)n, v = y / (float)n;
            float h = TileFbm(u, v, 4, 3, 23) * 0.6f + TileNoise(u * 128f, v * 128f, 128, 7) * 0.25f;
            float pebble = TileNoise(u * 32f, v * 32f, 32, 9);
            if (pebble > 0.8f) h += (pebble - 0.8f) * 1.5f;
            return h;
        }

        static void PaintRock(Color32[] px, int n, System.Func<int, int, int, float> height, Color dark, Color light)
        {
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float h = Mathf.Clamp01(height(x, y, n));
                    float grain = (Hash01(x, y, 99) - 0.5f) * 0.06f;
                    Color c = Color.Lerp(dark, light, Mathf.Clamp01(h * 1.2f - 0.1f + grain));
                    px[y * n + x] = new Color32(B(c.r), B(c.g), B(c.b), 255);
                }
            }
        }

        // ------------------------------------------------------------------ cave building blocks

        /// <summary>Rock floor and ceiling slabs (the walls are separate).</summary>
        static void CaveSlabs(string name, float x0, float x1, float z0, float z1, float height)
        {
            var parent = Empty(s_Level, name, Vector3.zero, null, GameLayers.Environment).transform;
            Vector3 size = new Vector3(x1 - x0, 0.2f, z1 - z0);
            Box(parent, "Floor", new Vector3((x0 + x1) * 0.5f, -0.1f, (z0 + z1) * 0.5f), size, M("Regolith"));
            Box(parent, "Ceiling", new Vector3((x0 + x1) * 0.5f, height + 0.1f, (z0 + z1) * 0.5f), size, M("RockCeiling"));
        }

        /// <summary>A rock chamber: like Room() but basalt and regolith, no ceiling beams.</summary>
        static void CaveRoom(string name, float x0, float x1, float z0, float z1, float height, string walls, params Opening[] openings)
        {
            CaveSlabs(name + " Slabs", x0, x1, z0, z1, height);
            CaveWallsAndRails(name, x0, x1, z0, z1, height, walls, openings);
            const float inset = 0.35f;
            TeleportArea(name, x0 + inset, x1 - inset, z0 + inset, z1 - inset);
        }

        static void CaveWallsAndRails(string name, float x0, float x1, float z0, float z1, float height, string walls, params Opening[] openings)
        {
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
                    Wall(name + " " + side, a, b, height, M("Basalt"), gaps);
                    foreach (var o in mine)
                    {
                        if (alongX) TeleportArea(name + " Doorway", o.at - 0.5f, o.at + 0.5f, a.y - 0.6f, a.y + 0.6f);
                        else TeleportArea(name + " Doorway", a.x - 0.6f, a.x + 0.6f, o.at - 0.5f, o.at + 0.5f);
                    }
                }
                RailLine(a, b, inward, gaps);
            }
        }

        /// <summary>A mining work light on a tripod stand. Dead ones have no lamp and lie where they fell.</summary>
        static Light WorkLight(string name, float x, float z, float height, Color color, float intensity, float range, bool dead = false)
        {
            var root = Empty(s_Props, name, new Vector3(x, 0, z), null, GameLayers.Default).transform;
            if (dead)
            {
                // Knocked over: the mast on the floor, the head cracked and cold.
                Part(root, "Mast", Tube(0.025f, height, 8), new Vector3(0, 0.05f, 0), M("Steel"), new Vector3(0, x * 40f, 88f));
                CBox(root, "Lamp Head", new Vector3(height * 0.95f, 0.14f, 0), new Vector3(0.28f, 0.2f, 0.18f), M("Trim"), 0.03f, new Vector3(0, 0, 80f));
                return null;
            }
            Part(root, "Mast", Tube(0.025f, height, 8), new Vector3(0, height * 0.5f, 0), M("Steel"));
            for (int i = 0; i < 3; i++)
            {
                float a = i * 120f + x * 13f;
                Vector3 dir = Quaternion.Euler(0, a, 0) * Vector3.forward;
                Part(root, "Leg", Tube(0.018f, 0.75f, 6), dir * 0.22f + Vector3.up * 0.3f, M("Steel"), Quaternion.FromToRotation(Vector3.up, (Vector3.up * 0.6f - dir * 0.45f).normalized).eulerAngles);
            }
            CBox(root, "Lamp Head", new Vector3(0, height + 0.08f, 0), new Vector3(0.34f, 0.22f, 0.2f), M("Trim"), 0.03f);
            var lens = Box(root, "Lamp Lens", new Vector3(x, height + 0.08f, z - 0.105f), new Vector3(0.28f, 0.16f, 0.015f), M("GlowWarm"), GameLayers.Default, false, false);
            s_WreckGlow.Add(lens.GetComponent<MeshRenderer>());
            var lensBack = Box(root, "Lamp Lens Back", new Vector3(x, height + 0.08f, z + 0.105f), new Vector3(0.28f, 0.16f, 0.015f), M("GlowWarm"), GameLayers.Default, false, false);
            s_WreckGlow.Add(lensBack.GetComponent<MeshRenderer>());
            return RoomLight(name + " Light", x, height + 0.1f, z, color, intensity, range);
        }

        static void Rock(Vector3 floorPoint, Vector3 radii, int seed, bool solid)
        {
            var go = Part(s_Props, "Boulder", Blob("Boulder", radii, Mathf.Min(radii.x, radii.z) * 0.35f, seed, 14, 9), floorPoint + Vector3.up * (radii.y * 0.6f), M("Basalt"),
                new Vector3(seed * 17f % 30f, seed * 47f, seed * 23f % 20f), solid ? GameLayers.Environment : GameLayers.Default);
            go.isStatic = true;
            if (solid)
            {
                var col = go.AddComponent<SphereCollider>();
                col.radius = Mathf.Min(radii.x, Mathf.Min(radii.y, radii.z)) * 0.95f;
            }
        }

        // ------------------------------------------------------------------ wreck D: the lava tube outpost

        static WreckMap BuildLavaTube(Light[] sharedLights, Renderer[] sharedGlow)
        {
            CaveMaterials();
            var map = BeginMap("Wreck D - Lava Tube", CaveMapId, "LAVA TUBE OUTPOST", "A mining outpost in a lunar lava tube. Low gravity. Two loops round the dark.");
            Transform root = map.transform;
            const SalvageCategoryMask S = SalvageCategoryMask.SmallScrap;
            const SalvageCategoryMask E = SalvageCategoryMask.Electronics;
            const SalvageCategoryMask N = SalvageCategoryMask.Novelty;
            const float MH = 3.4f, TH = 3.2f, SH = 4.2f, DH = 4.2f, PH = 2.6f, HH = 2.8f;
            var sodium = new Color(1f, 0.72f, 0.42f);
            var white = new Color(0.9f, 0.93f, 1f);
            var hab = M("Wall");

            // --- cave mouth: the airlock opens into its south wall
            CaveSlabs("D Mouth Slabs", -6f, 9.6f, 7.6f, 13.6f, MH);
            Wall("D Mouth S West", new Vector2(-6f, 7.6f), new Vector2(-2.18f, 7.6f), MH, M("Basalt"));
            Wall("D Mouth S East", new Vector2(2.18f, 7.6f), new Vector2(9.6f, 7.6f), MH, M("Basalt"));
            Wall("D Mouth N", new Vector2(-6f, 13.6f), new Vector2(9.6f, 13.6f), MH, M("Basalt"),
                new Gap(1.1f, 2.2f, 2.6f), new Gap(6f, 3.0f, 3.0f), new Gap(12f, 1.6f, 2.1f));
            CaveWallsAndRails("D Mouth", -6f, 9.6f, 7.6f, 13.6f, MH, "WE",
                new Opening('S', 0f, 4.4f, 2.2f), new Opening('N', -4.9f, 2.2f, 2.6f), new Opening('N', 0f, 3.0f, 3.0f), new Opening('N', 6f, 1.6f, 2.1f));
            TeleportArea("D Mouth", -5.65f, 9.25f, 7.95f, 13.25f);
            TeleportArea("D Mouth Airlock Doorway", -0.5f, 0.5f, 7.2f, 8.2f);
            TeleportArea("D Side Tunnel Doorway", -5.4f, -4.4f, 13.0f, 14.2f);
            TeleportArea("D Tunnel Doorway", -1.2f, 1.2f, 13.0f, 14.2f);
            TeleportArea("D Hab Doorway", 5.5f, 6.5f, 13.0f, 14.2f);

            // --- main tunnel; its west wall beside the skylight chamber belongs to the (taller) chamber
            CaveSlabs("D Tunnel Slabs", -1.6f, 1.6f, 13.6f, 33.6f, TH);
            Wall("D Tunnel W South", new Vector2(-1.6f, 13.6f), new Vector2(-1.6f, 18f), TH, M("Basalt"));
            Wall("D Tunnel W North", new Vector2(-1.6f, 28f), new Vector2(-1.6f, 33.6f), TH, M("Basalt"));
            Wall("D Tunnel E", new Vector2(1.6f, 13.6f), new Vector2(1.6f, 33.6f), TH, M("Basalt"), new Gap(22f - 13.6f, 1.0f, 2.0f));
            CaveWallsAndRails("D Tunnel", -1.6f, 1.6f, 13.6f, 33.6f, TH, "", new Opening('W', 23f, 2.4f, 2.6f), new Opening('E', 22f, 1.0f, 2.0f));
            TeleportArea("D Tunnel", -1.25f, 1.25f, 13.95f, 33.25f);

            // --- habitat module: two pressurised rooms with metal walls
            Room("D Hab Living", 1.6f, 9.6f, 13.6f, 18.6f, HH, "E", hab, new Opening('S', 6f, 1.6f, 2.1f), new Opening('N', 4f, 1.2f, 2.0f));
            Room("D Hab Workshop", 1.6f, 9.6f, 18.6f, 24.6f, HH, "NSE", hab, new Opening('S', 4f, 1.2f, 2.0f), new Opening('W', 22f, 1.0f, 2.0f));
            TeleportArea("D Hab Hatch Doorway", 1.0f, 2.2f, 21.5f, 22.5f);
            var habDoor = BuildDoor("Habitat Door", "lt_door_hab", new Vector3(6f, 0, 13.6f), 1.6f, 2.1f, false, false, true);

            // --- side tunnel, skylight chamber, back passage, drill chamber
            CaveRoom("D Side Tunnel", -6f, -3.8f, 13.6f, 18f, 2.8f, "WE", new Opening('S', -4.9f, 2.2f, 2.6f), new Opening('N', -4.9f, 2.2f, 2.6f));
            CaveRoom("D Skylight", -12f, -1.6f, 18f, 28f, SH, "SNWE", new Opening('S', -4.9f, 2.2f, 2.6f), new Opening('E', 23f, 2.4f, 2.6f), new Opening('N', -8.1f, 2.0f, 2.4f));
            CaveSlabs("D Passage A Slabs", -9.2f, -7f, 28f, 36.6f, PH);
            CaveSlabs("D Passage B Slabs", -9.2f, -6f, 36.6f, 38.8f, PH);
            Wall("D Passage W", new Vector2(-9.2f, 28f), new Vector2(-9.2f, 38.8f), PH, M("Basalt"));
            Wall("D Passage E", new Vector2(-7f, 28f), new Vector2(-7f, 36.6f), PH, M("Basalt"));
            Wall("D Passage N", new Vector2(-9.2f, 38.8f), new Vector2(-6f, 38.8f), PH, M("Basalt"));
            Wall("D Passage S", new Vector2(-7f, 36.6f), new Vector2(-6f, 36.6f), PH, M("Basalt"));
            TeleportArea("D Passage A", -8.85f, -7.35f, 28.3f, 38.45f);
            TeleportArea("D Passage B", -8.85f, -5.6f, 36.95f, 38.45f);
            Rail(new Vector3(-9f, RailY, 28.6f), new Vector3(-9f, RailY, 38.2f));
            Rail(new Vector3(-6.6f, RailY, 38.6f), new Vector3(-8.6f, RailY, 38.6f));
            CaveRoom("D Drill", -6f, 6f, 33.6f, 41.4f, DH, "SNWE", new Opening('S', 0f, 3.0f, 3.0f), new Opening('W', 37.7f, 1.8f, 2.4f));

            // --- the skylight: a collapsed section of roof, open to the stars
            // (built as a frame of ceiling around a hole, a shaft above it and a sky cap)
            {
                var sky = Empty(s_Level, "D Skylight Shaft", Vector3.zero, null, GameLayers.Environment).transform;
                const float top = 7.5f;
                Box(sky, "Shaft W", new Vector3(-9.1f, (SH + top) * 0.5f, 23f), new Vector3(0.2f, top - SH, 4.4f), M("Basalt"));
                Box(sky, "Shaft E", new Vector3(-4.9f, (SH + top) * 0.5f, 23f), new Vector3(0.2f, top - SH, 4.4f), M("Basalt"));
                Box(sky, "Shaft S", new Vector3(-7f, (SH + top) * 0.5f, 20.9f), new Vector3(4.4f, top - SH, 0.2f), M("Basalt"));
                Box(sky, "Shaft N", new Vector3(-7f, (SH + top) * 0.5f, 25.1f), new Vector3(4.4f, top - SH, 0.2f), M("Basalt"));
                Box(sky, "Sky", new Vector3(-7f, top + 0.05f, 23f), new Vector3(4.6f, 0.1f, 4.6f), M("Stars"), GameLayers.Environment);
                // Cut the hole: the chamber's ceiling slab is replaced by four pieces around it.
                var slabs = s_Level.Find("D Skylight Slabs");
                var ceiling = slabs.Find("Ceiling");
                UnityEngine.Object.DestroyImmediate(ceiling.gameObject);
                Box(slabs, "Ceiling W", new Vector3((-12f + -9f) * 0.5f, SH + 0.1f, 23f), new Vector3(3f, 0.2f, 10f), M("RockCeiling"));
                Box(slabs, "Ceiling E", new Vector3((-5f + -1.6f) * 0.5f, SH + 0.1f, 23f), new Vector3(3.4f, 0.2f, 10f), M("RockCeiling"));
                Box(slabs, "Ceiling S", new Vector3(-7f, SH + 0.1f, (18f + 21f) * 0.5f), new Vector3(4f, 0.2f, 3f), M("RockCeiling"));
                Box(slabs, "Ceiling N", new Vector3(-7f, SH + 0.1f, (25f + 28f) * 0.5f), new Vector3(4f, 0.2f, 3f), M("RockCeiling"));
                // What came down: a rubble heap under the hole.
                Rock(new Vector3(-7.2f, 0, 23.2f), new Vector3(1.2f, 0.7f, 1.0f), 301, true);
                Rock(new Vector3(-6.3f, 0, 22.4f), new Vector3(0.6f, 0.4f, 0.55f), 302, true);
                Rock(new Vector3(-7.9f, 0, 22.1f), new Vector3(0.5f, 0.35f, 0.45f), 303, true);
                Rock(new Vector3(-6.6f, 0, 24.1f), new Vector3(0.45f, 0.3f, 0.5f), 304, false);
                var earthshine = PointLight(s_Lights, "Earthshine", new Vector3(-7f, SH - 0.3f, 23f), new Color(0.55f, 0.65f, 0.85f), 1.6f, 7f);
                earthshine.gameObject.name = "Earthshine (not powered)";
            }

            // --- panels and loot
            var corePanel = SealedCompartment("Drill Power Core", "lt_panel_core", "DRILL POWER CORE", new Vector3(-3.2f, 0, 39.9f), new Vector3(1.8f, 2.4f, 1.8f), M("ReactorShell"), out Vector3 coreCavity);
            MakeCuttable("Habitat Service Hatch", "lt_panel_hatch", "SERVICE HATCH", new Vector3(1.6f - T * 0.5f - 0.03f, 1.02f, 22f), Vector3.left, new Vector2(1.12f, 2.04f), s_HatchCuts);
            Exclusion(root, "No Drone: D Hab Hatch", new Vector3(1.6f, 1.2f, 22f), new Vector3(1.4f, 2.6f, 1.3f));
            Spawn("lt_drill_core", coreCavity, SalvageCategoryMask.ReactorComponent, corePanel);
            RackMount("lt_mount_rack", new Vector3(9.6f, 0, 21.4f), Vector3.left, "lt_hab_rack");

            Spawn("lt_mouth_west", new Vector3(-4.4f, 1.0f, 9.0f), S);
            Spawn("lt_mouth_sled", new Vector3(3.4f, 1.2f, 11.9f), S | E);
            Spawn("lt_mouth_east", new Vector3(8.4f, 1.1f, 9.0f), S | N);
            Spawn("lt_side_mid", new Vector3(-4.6f, 1.1f, 16.0f), S);
            Spawn("lt_tunnel_south", new Vector3(0.8f, 1.1f, 16.2f), S);
            Spawn("lt_tunnel_mid", new Vector3(-0.9f, 1.2f, 26.6f), S | E);
            Spawn("lt_tunnel_north", new Vector3(0.9f, 1.0f, 31.4f), S);
            Spawn("lt_sky_east", new Vector3(-3.0f, 1.2f, 19.4f), S | E);
            Spawn("lt_sky_west", new Vector3(-10.8f, 1.1f, 26.6f), S | N);
            Spawn("lt_sky_south", new Vector3(-10.6f, 1.0f, 19.2f), S);
            Spawn("lt_sky_north", new Vector3(-4.0f, 1.3f, 26.8f), E | N);
            Spawn("lt_passage_mid", new Vector3(-8.1f, 1.1f, 32.0f), S | E);
            Spawn("lt_passage_bend", new Vector3(-6.8f, 1.0f, 37.6f), S);
            Spawn("lt_drill_west", new Vector3(-4.9f, 1.1f, 35.0f), S | E);
            Spawn("lt_drill_east", new Vector3(5.0f, 1.2f, 38.0f), S | N);
            Spawn("lt_drill_back", new Vector3(0.6f, 1.2f, 40.6f), S | E);
            Spawn("lt_hab_galley", new Vector3(2.4f, 1.2f, 14.6f), N | S);
            Spawn("lt_hab_table", new Vector3(4.6f, 1.25f, 16.4f), E | S);
            Spawn("lt_hab_bunk", new Vector3(8.6f, 1.2f, 17.4f), S | N);
            Spawn("lt_hab_bench", new Vector3(3.0f, 1.25f, 23.8f), E | N);
            Spawn("lt_hab_floor", new Vector3(7.2f, 1.0f, 20.0f), S);

            // --- dressing: crates and a cargo sled in the mouth
            Container(new Vector3(-3.0f, 0, 12.7f), new Vector3(1.2f, 1.1f, 1.0f), "ContainerBlue");
            Container(new Vector3(-2.8f, 0, 8.6f), new Vector3(1.2f, 1.0f, 1.0f));
            Container(new Vector3(8.6f, 0, 12.4f), new Vector3(1.4f, 1.3f, 1.2f));
            Container(new Vector3(3.4f, 0, 8.5f), new Vector3(1.6f, 0.9f, 1.0f), "ContainerBlue");
            Box(s_Props, "Cargo Sled", new Vector3(3.4f, 0.3f, 11.9f), new Vector3(1.8f, 0.12f, 0.9f), M("Trim"));
            Box(s_Props, "Cargo Sled Runner", new Vector3(3.4f, 0.12f, 11.55f), new Vector3(1.9f, 0.24f, 0.08f), M("Steel"), GameLayers.Default, false);
            Box(s_Props, "Cargo Sled Runner", new Vector3(3.4f, 0.12f, 12.25f), new Vector3(1.9f, 0.24f, 0.08f), M("Steel"), GameLayers.Default, false);
            Barrel(new Vector3(-5.4f, 0, 8.3f));
            Barrel(new Vector3(9.0f, 0, 8.3f), "BarrelBlue");
            Pipe("D Power Cable Mouth", new Vector3(1.4f, 0.05f, 8.0f), new Vector3(1.4f, 0.05f, 13.5f), 0.035f, "Cable");
            Pipe("D Power Cable Tunnel", new Vector3(1.4f, 0.05f, 13.7f), new Vector3(1.4f, 0.05f, 33.4f), 0.035f, "Cable");

            // Drill chamber: the rig, its power core, ore bins.
            var rig = Empty(s_Props, "Drill Rig", new Vector3(3.0f, 0, 40.0f), null, GameLayers.Environment).transform;
            Box(rig, "Rig Base", new Vector3(3.0f, 0.5f, 40.0f), new Vector3(2.4f, 1.0f, 2.0f), M("Container"));
            Box(rig, "Rig Mast", new Vector3(3.0f, 2.0f, 40.6f), new Vector3(0.5f, 2.0f, 0.5f), M("Steel"));
            Part(rig, "Drill Bit", Cone(0.35f, 0.05f, 1.2f, 12), new Vector3(0, 2.0f, 1.2f), M("Steel"), new Vector3(90f, 0, 0));
            Box(rig, "Rig Cab", new Vector3(2.2f, 1.4f, 39.6f), new Vector3(0.8f, 0.8f, 0.9f), M("Trim"));
            Container(new Vector3(-5.0f, 0, 40.4f), new Vector3(1.2f, 0.9f, 1.4f), "ContainerBlue");
            Container(new Vector3(5.0f, 0, 34.6f), new Vector3(1.2f, 0.8f, 0.9f));
            Barrel(new Vector3(-5.3f, 0, 34.2f), "BarrelBlue");
            Pipe("D Drill Cable", new Vector3(1.4f, 0.05f, 33.8f), new Vector3(2.2f, 0.05f, 39.0f), 0.035f, "Cable");

            // Habitat: bunks, a table, a galley, the workshop bench.
            Box(s_Props, "Bunk Lower", new Vector3(8.6f, 0.35f, 16.2f), new Vector3(0.9f, 0.3f, 1.9f), M("Trim"));
            Box(s_Props, "Bunk Upper", new Vector3(8.6f, 1.55f, 16.2f), new Vector3(0.9f, 0.12f, 1.9f), M("Trim"));
            Box(s_Props, "Bunk Mattress", new Vector3(8.6f, 0.56f, 16.2f), new Vector3(0.8f, 0.12f, 1.8f), M("Rubber"), GameLayers.Default, false);
            Box(s_Props, "Mess Table", new Vector3(4.6f, 0.4f, 16.0f), new Vector3(1.4f, 0.8f, 0.8f), M("Trim"));
            Box(s_Props, "Galley Counter", new Vector3(2.15f, 0.45f, 15.4f), new Vector3(0.7f, 0.9f, 1.4f), M("Trim"));
            Box(s_Props, "Workshop Bench", new Vector3(3.0f, 0.45f, 24.0f), new Vector3(2.2f, 0.9f, 0.7f), M("Trim"));
            ConsoleDetail(new Vector3(3.0f, 0.9f, 24.0f), 1.6f, Vector3.back);
            LockerRow(new Vector3(9.6f - T * 0.5f - 0.24f, 0, 19.6f), Vector3.left, 2);
            Window(new Vector3(9.6f - T * 0.5f - 0.015f, 1.65f, 15.6f), Vector3.left, 1.6f, 0.8f);
            Poster("OUTPOST RULES\n1. Helmets on past the door.\n2. Count heads at every shift.", new Vector3(5.2f, 1.6f, 18.6f - T * 0.5f - 0.015f), Vector3.back, 0.95f, 0.55f, 0.36f);
            Poster("SHIFT LOG 41\nDrill team did not come back up.\nThe lights in the tube keep dying.", new Vector3(9.6f - T * 0.5f - 0.012f, 1.5f, 23.2f), Vector3.left, 0.95f, 0.55f, 0.36f);

            // Work lights on stands down the tunnel (two of them knocked over), lamps in the habitat.
            WorkLight("D Work Light Mouth W", -3.0f, 11.6f, 2.0f, sodium, 3.4f, 9f);
            WorkLight("D Work Light Mouth E", 6.6f, 9.0f, 2.0f, sodium, 3.0f, 8f);
            WorkLight("D Work Light Tunnel 1", -1.05f, 16.8f, 1.9f, sodium, 2.6f, 7f);
            WorkLight("D Work Light Tunnel 2", 1.05f, 20.6f, 1.9f, sodium, 2.4f, 7f, true);
            WorkLight("D Work Light Tunnel 3", -1.05f, 25.6f, 1.9f, sodium, 2.6f, 7f);
            WorkLight("D Work Light Tunnel 4", 1.05f, 29.2f, 1.9f, sodium, 2.4f, 7f);
            WorkLight("D Work Light Tunnel 5", -1.05f, 32.4f, 1.9f, sodium, 2.4f, 7f, true);
            WorkLight("D Work Light Skylight", -2.6f, 26.8f, 2.2f, sodium, 2.8f, 9f);
            WorkLight("D Work Light Side", -4.0f, 17.4f, 1.8f, sodium, 2.0f, 6f, true);
            WorkLight("D Work Light Drill W", -4.6f, 36.4f, 2.4f, new Color(1f, 0.55f, 0.3f), 3.4f, 10f);
            WorkLight("D Work Light Drill E", 4.8f, 36.8f, 2.4f, sodium, 3.0f, 10f);
            RoomLight("D Hab Living Light", 5.6f, 2.4f, 16.1f, white, 3.0f, 8f);
            RoomLight("D Hab Workshop Light", 5.6f, 2.4f, 21.6f, new Color(0.6f, 1f, 0.75f), 2.8f, 8f);
            RoomLight("D Passage Light", -8.1f, 2.1f, 33f, new Color(1f, 0.5f, 0.25f), 1.6f, 6f);
            Strip("D Hab Living Strip", new Vector3(5.6f, HH - 0.03f, 16.1f), new Vector3(4f, 0.03f, 0.1f), "GlowWhite");
            Strip("D Hab Workshop Strip", new Vector3(5.6f, HH - 0.03f, 21.6f), new Vector3(4f, 0.03f, 0.1f), "GlowTeal");

            Web(new Vector3(-11.9f, SH - 0.05f, 27.9f), Vector3.right, Vector3.back, Vector3.down, 1.8f);
            Web(new Vector3(5.9f, DH - 0.05f, 41.3f), Vector3.left, Vector3.back, Vector3.down, 2.0f);

            Sign("LAVA TUBE OUTPOST\nSHAFT 2", new Vector3(-2.6f, 2.4f, 13.6f - T * 0.5f - 0.02f), Vector3.back, 0.8f);
            Sign("HABITAT", new Vector3(6f, 2.5f, 13.6f - T * 0.5f - 0.02f), Vector3.back, 1.3f, new Color(0.6f, 1f, 0.9f));
            Sign("DRILL FACE >>", new Vector3(-1.6f + T * 0.5f + 0.02f, 2.4f, 16.4f), Vector3.right, 1.0f);
            Sign("SKYLIGHT\nROCKFALL", new Vector3(-4.9f, 2.9f, 13.6f - T * 0.5f - 0.02f), Vector3.back, 0.9f, new Color(1f, 0.5f, 0.4f));
            Sign("SERVICE HATCH\nSEALED", new Vector3(1.6f - T * 0.5f - 0.02f, 2.25f, 22f), Vector3.left, 0.8f);
            Sign("DRILL CHAMBER", new Vector3(1.6f - T * 0.5f - 0.02f, 2.4f, 31.6f), Vector3.left, 1.1f, new Color(1f, 0.6f, 0.3f));
            Sign("IT CAME UP THE DRILL HOLE", new Vector3(-2.0f, 2.6f, 41.4f - T * 0.5f - 0.02f), Vector3.back, 0.8f, new Color(1f, 0.5f, 0.4f));

            Anchor("D Mouth", -1.6f, 10.6f);
            Anchor("D Tunnel", 0f, 24f);
            Anchor("D Hab", 6.4f, 16.4f);
            Anchor("D Workshop", 6.0f, 21.8f);
            Anchor("D Skylight", -4.0f, 23f);
            Anchor("D Passage", -8.1f, 31f);
            Anchor("D Drill", 0f, 36.4f);
            Anchor("D Side", -4.9f, 15.8f);

            Junk(s_JunkCanPrefab, new Vector3(-1.4f, 1.4f, 9.6f));
            Junk(s_JunkCanPrefab, new Vector3(0.4f, 1.6f, 27.6f));
            Junk(s_JunkCanPrefab, new Vector3(6.6f, 1.4f, 20.6f));
            Junk(s_JunkCratePrefab, new Vector3(-9.6f, 1.8f, 21.0f));
            Junk(s_JunkCratePrefab, new Vector3(1.8f, 1.6f, 36.2f));
            Junk(s_JunkCratePrefab, new Vector3(6.4f, 1.5f, 10.4f));

            // Creature routes
            Waypoint("M0", 0, 10.6f); Waypoint("MW", -4.9f, 10.6f); Waypoint("ME", 6f, 10.6f);
            Waypoint("S1", -4.9f, 15.8f);
            Waypoint("T0", 0, 14.6f); Waypoint("T1", 0, 18.6f); Waypoint("T2", 0, 23f); Waypoint("T3", 0, 28f); Waypoint("T4", 0, 32.6f);
            Waypoint("KS", -4.9f, 20f); Waypoint("KE", -3.2f, 23f); Waypoint("KW", -10.4f, 23f); Waypoint("KNW", -8.1f, 26.4f);
            Waypoint("P2", -8.1f, 28.8f); Waypoint("P1", -8.1f, 32.4f); Waypoint("P0", -8.1f, 37.7f);
            Waypoint("D0", 0, 35.2f); Waypoint("D1", -4.2f, 37.7f); Waypoint("D2", 0, 38.0f); Waypoint("D3", 4.2f, 36.6f);
            Waypoint("H0", 6f, 15.2f); Waypoint("H1", 7.8f, 15.0f); Waypoint("H2", 4f, 17.6f); Waypoint("H3", 4f, 20.4f); Waypoint("H4", 7.4f, 22.2f);
            Link("M0", "MW", "S1", "KS", "KE", "T2");
            Link("M0", "T0", "T1", "T2", "T3", "T4", "D0");
            Link("M0", "ME", "H0", "H1");
            Link("H0", "H2", "H3", "H4");
            Link("KE", "KNW", "KW", "KS");
            Link("KNW", "P2", "P1", "P0", "D1", "D0");
            Link("D0", "D2", "D3", "D0");
            Link("D1", "D2");

            var dock = Empty(root, "Creature Dock", new Vector3(4.8f, 1.45f, 35.2f), Quaternion.Euler(0, 200f, 0)).transform;
            var hum = MapHum("D Drill Hum", new Vector3(3f, 1.4f, 40f), 0.38f);
            EndMap(map, dock, new[] { habDoor }, new[] { hum }, sharedLights, sharedGlow,
                "D3", "D0", "T4", "T3", "T2", "T1", "T0", "M0", "ME", "H0", "H1", "H0", "H2", "H3", "H4", "H3", "H2", "H0", "ME", "M0", "MW", "S1", "KS",
                "KE", "T2", "KE", "KNW", "KW", "KS", "KW", "KNW", "P2", "P1", "P0", "D1", "D2", "D3");

            // The Moon: weak gravity everywhere beyond the airlock door, none in the airlock or the tug.
            var moon = map.gameObject.AddComponent<MapGravity>();
            moon.gravity = new Vector3(0f, -LunarGravity, 0f);
            moon.center = new Vector3(-1.25f, 3.5f, 24.7f);
            moon.size = new Vector3(23.5f, 9f, 33.9f); // x[-13,10.5] y[-1,8] z[7.75,41.65]
            return map;
        }
    }
}

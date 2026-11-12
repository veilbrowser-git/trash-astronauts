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
            var basalt = MakeTexture("Basalt", 512, (px, n, rng) => PaintRock(px, n, BasaltHeight, new Color(0.2f, 0.195f, 0.19f), new Color(0.36f, 0.35f, 0.34f)));
            var basaltN = MakeNormalMap("Basalt_N", 512, (x, y) => BasaltHeight(x, y, 512), 3.5f);
            var regolith = MakeTexture("Regolith", 512, (px, n, rng) => PaintRock(px, n, RegolithHeight, new Color(0.34f, 0.335f, 0.33f), new Color(0.5f, 0.49f, 0.48f)));
            var regolithN = MakeNormalMap("Regolith_N", 512, (x, y) => RegolithHeight(x, y, 512), 2.5f);
            Lit("Basalt", new Color(1f, 1f, 1f), 0.12f, basalt, null, basaltN);
            Lit("Regolith", new Color(0.95f, 0.95f, 0.95f), 0.05f, regolith, null, regolithN);
            Lit("RockCeiling", new Color(0.8f, 0.8f, 0.82f), 0.1f, basalt, null, basaltN);
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
        static void CaveRoom(string name, float x0, float x1, float z0, float z1, float height, string walls, string railSides, params Opening[] openings)
        {
            CaveSlabs(name + " Slabs", x0, x1, z0, z1, height);
            CaveWallsAndRails(name, x0, x1, z0, z1, height, walls, railSides, openings);
            const float inset = 0.35f;
            TeleportArea(name, x0 + inset, x1 - inset, z0 + inset, z1 - inset);
        }

        /// <param name="railSides">Which sides get handrails (a cave is not a ship: guide rails only along the main walls).</param>
        static void CaveWallsAndRails(string name, float x0, float x1, float z0, float z1, float height, string walls, string railSides, params Opening[] openings)
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
                if (railSides.IndexOf(side) >= 0) RailLine(a, b, inward, gaps);
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
            // One glowing block through the head: a lens on both faces, one renderer.
            var lens = Box(root, "Lamp Lens", new Vector3(x, height + 0.08f, z), new Vector3(0.28f, 0.16f, 0.23f), M("GlowWarm"), GameLayers.Default, false, false);
            s_WreckGlow.Add(lens.GetComponent<MeshRenderer>());
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

        // ------------------------------------------------------------------ organic rock and combining

        /// <summary>
        /// A smooth 3D warp of space used to roughen every rock surface. It depends only on the world
        /// position, so neighbouring slabs and wall pieces bend the same way and meet without cracks.
        /// Small near the floor (the floor stays walkable and matches its flat collider), larger overhead.
        /// </summary>
        static Vector3 CaveWarp(Vector3 p)
        {
            float amp = Mathf.Lerp(0.04f, 0.2f, Mathf.InverseLerp(0.05f, 0.9f, p.y));
            const float f = 0.55f;
            float N(float a, float b, float c, float o) =>
                (Mathf.PerlinNoise(a * f + o, b * f + c * 0.7f * f + o * 0.37f) + 0.5f * Mathf.PerlinNoise(a * f * 2.3f + c * 0.9f + o, b * f * 2.3f - o)) / 0.75f - 1f;
            return new Vector3(N(p.x, p.z, p.y, 3.1f), 0.55f * N(p.z, p.y, p.x, 11.3f), N(p.y, p.x, p.z, 17.9f)) * amp;
        }

        /// <summary>Static scenery of one material in one patch of the cave, merged into one mesh.</summary>
        sealed class CaveChunk
        {
            public readonly List<Vector3> verts = new List<Vector3>();
            public readonly List<Vector3> normals = new List<Vector3>();
            public readonly List<Vector2> uvs = new List<Vector2>();
            public readonly List<int> tris = new List<int>();
            public readonly List<CombineInstance> parts = new List<CombineInstance>();
        }

        /// <summary>
        /// Patches are small enough that each is lit by its own few lights: URP on Quest lights each
        /// renderer with at most four, so one mesh for the whole cave would leave most of it black.
        /// </summary>
        static Vector2Int CaveCell(Vector3 p, float size) =>
            size >= 1000f ? Vector2Int.zero : new Vector2Int(Mathf.FloorToInt(p.x / size), Mathf.FloorToInt(p.z / size));

        /// <summary>
        /// Walls and floor cover everything and need their local lights: 10 m patches. The ceiling is seen
        /// less (14 m), props 24 m. Self-lit things (glow, webs, stars, guide lights) need no lights: one mesh.
        /// </summary>
        static float CaveChunkSize(Material mat, HashSet<Material> rock)
        {
            if (mat.name == "RockCeiling") return 14f;
            if (rock.Contains(mat)) return 10f;
            string n = mat.name;
            if (n.StartsWith("Glow") || n == "Web" || n == "GuideLight") return 1000f;
            if (n == "Stars") return 10f; // the sky over the skylight and the habitat window stay apart (previews hide the sky from above)
            return 24f;
        }

        static Vector3 Warped(Vector3 p) => p + CaveWarp(p);

        /// <summary>
        /// A box's surface, subdivided about every 0.4 m and warped by CaveWarp, written quad by quad into the
        /// chunk each quad falls in. Normals come from the warp itself, so the rock is smooth-shaded across
        /// quads and chunks alike.
        /// </summary>
        static void WarpedBoxInto(Vector3 center, Vector3 size, System.Func<Vector3, CaveChunk> chunkAt)
        {
            Vector3 h = size * 0.5f;
            void Face(Vector3 normal, Vector3 right, Vector3 up, float w, float ht, float depth)
            {
                Vector3 c = center + normal * depth;
                // Skip the outsides nobody sees: under the floor and above the ceiling.
                if (normal.y < -0.5f && c.y < -0.05f) return;
                if (normal.y > 0.5f && c.y > 1f && size.y < 0.5f) return;
                int nx = Mathf.Max(1, Mathf.CeilToInt(w / 0.4f)), ny = Mathf.Max(1, Mathf.CeilToInt(ht / 0.4f));
                bool flip = Vector3.Dot(Vector3.Cross(up, right), normal) <= 0f;
                Vector3 Point(int i, int j) => c + right * (-w * 0.5f + w * i / nx) + up * (-ht * 0.5f + ht * j / ny);
                Vector3 NormalAt(Vector3 p)
                {
                    const float e = 0.05f;
                    Vector3 n = Vector3.Cross(Warped(p + right * e) - Warped(p - right * e), Warped(p + up * e) - Warped(p - up * e)).normalized;
                    return Vector3.Dot(n, normal) < 0f ? -n : n;
                }
                for (int j = 0; j < ny; j++)
                {
                    for (int i = 0; i < nx; i++)
                    {
                        var chunk = chunkAt((Point(i, j) + Point(i + 1, j + 1)) * 0.5f);
                        int start = chunk.verts.Count;
                        foreach (var p in new[] { Point(i, j), Point(i, j + 1), Point(i + 1, j + 1), Point(i + 1, j) })
                        {
                            chunk.verts.Add(Warped(p));
                            chunk.normals.Add(NormalAt(p));
                            chunk.uvs.Add(new Vector2(Vector3.Dot(p, right), Vector3.Dot(p, up)) * 0.35f);
                        }
                        if (!flip) chunk.tris.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
                        else chunk.tris.AddRange(new[] { start, start + 2, start + 1, start, start + 3, start + 2 });
                    }
                }
            }
            Face(Vector3.forward, Vector3.right, Vector3.up, size.x, size.y, h.z);
            Face(Vector3.back, Vector3.right, Vector3.up, size.x, size.y, h.z);
            Face(Vector3.right, Vector3.forward, Vector3.up, size.z, size.y, h.x);
            Face(Vector3.left, Vector3.forward, Vector3.up, size.z, size.y, h.x);
            Face(Vector3.up, Vector3.right, Vector3.forward, size.x, size.z, h.y);
            Face(Vector3.down, Vector3.right, Vector3.forward, size.x, size.z, h.y);
        }

        static Mesh SaveCaveMesh(string key, Mesh built)
        {
            EnsureFolder(MeshDir);
            string path = MeshDir + "/" + key + ".asset";
            built.name = key;
            var existing = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                UnityEditor.EditorUtility.CopySerialized(built, existing);
                existing.name = key;
                UnityEditor.EditorUtility.SetDirty(existing);
                UnityEngine.Object.DestroyImmediate(built);
                return existing;
            }
            UnityEditor.AssetDatabase.CreateAsset(built, path);
            return built;
        }

        /// <summary>
        /// The cave's finishing pass: rock walls lose their metal trim, every rock slab and wall piece is
        /// replaced by a warped, subdivided, smooth-shaded copy of itself (its box collider stays exactly
        /// where it was, so the play space stays watertight and the creature's surface probes still work),
        /// and all static scenery is merged into one mesh per material per patch (see CaveChunkSize).
        /// </summary>
        static void CaveRockAndCombine(Transform root)
        {
            var rock = new HashSet<Material> { M("Basalt"), M("RockCeiling"), M("Regolith") };
            var all = root.GetComponentsInChildren<MeshRenderer>(true);

            // Metal trim does not belong on bare rock.
            foreach (var r in all)
            {
                if (r == null || (r.name != "Skirting" && r.name != "Cornice")) continue;
                var segment = r.transform.parent != null ? r.transform.parent.GetComponentInChildren<MeshRenderer>() : null;
                if (segment != null && rock.Contains(segment.sharedMaterial)) UnityEngine.Object.DestroyImmediate(r.gameObject);
            }

            var chunks = new Dictionary<(Material, Vector2Int), CaveChunk>();
            CaveChunk Chunk(Material mat, Vector3 at)
            {
                var key = (mat, CaveCell(at, CaveChunkSize(mat, rock)));
                if (!chunks.TryGetValue(key, out var chunk)) chunks[key] = chunk = new CaveChunk();
                return chunk;
            }
            var merged = new List<MeshRenderer>();
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var go = r.gameObject;
                if (r.sharedMaterials.Length != 1 || r.sharedMaterial == null) continue;
                if (s_WreckGlow.Contains(r) || go.GetComponent<TMPro.TMP_Text>() != null) continue;
                if (go.GetComponentInParent<Rigidbody>(true) != null) continue; // doors, panels, loose junk move
                // Anything driven by a script stays separate (doors, panels, buttons); a handrail's bar lights up
                // on its own, its brackets do not.
                bool scripted = false;
                foreach (var behaviour in go.GetComponentsInParent<MonoBehaviour>(true))
                {
                    if (behaviour is WreckMap) continue;
                    if (behaviour is HoverHighlight highlight && (highlight.renderers == null || !highlight.renderers.Contains(r))) continue;
                    scripted = true;
                    break;
                }
                if (scripted) continue;
                var filter = go.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                var mat = r.sharedMaterial;
                var box = go.GetComponent<BoxCollider>();
                if (rock.Contains(mat) && box != null && go.transform.rotation == Quaternion.identity && go.transform.lossyScale == Vector3.one)
                    WarpedBoxInto(go.transform.TransformPoint(box.center), box.size, at => Chunk(mat, at));
                else
                    Chunk(mat, r.bounds.center).parts.Add(new CombineInstance { mesh = filter.sharedMesh, transform = go.transform.localToWorldMatrix });
                merged.Add(r);
            }

            var level = root.Find("Level");
            var parent = Empty(level, "Combined Scenery", Vector3.zero, null, GameLayers.Default).transform;
            var temporary = new List<Mesh>();
            foreach (var pair in chunks.OrderBy(c => c.Key.Item1.name).ThenBy(c => c.Key.Item2.x).ThenBy(c => c.Key.Item2.y))
            {
                var chunk = pair.Value;
                var parts = new List<CombineInstance>(chunk.parts);
                if (chunk.verts.Count > 0)
                {
                    var raw = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                    raw.SetVertices(chunk.verts);
                    raw.SetNormals(chunk.normals);
                    raw.SetUVs(0, chunk.uvs);
                    raw.SetTriangles(chunk.tris, 0);
                    temporary.Add(raw);
                    parts.Add(new CombineInstance { mesh = raw, transform = Matrix4x4.identity });
                }
                var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.CombineMeshes(parts.ToArray(), true, true);
                mesh.RecalculateBounds();
                mesh.RecalculateTangents();
                string cell = pair.Key.Item2.x + "_" + pair.Key.Item2.y;
                mesh = SaveCaveMesh("Cave_" + pair.Key.Item1.name + "_" + cell, mesh);
                var go = Empty(parent, "Combined " + pair.Key.Item1.name + " " + cell, Vector3.zero, null, GameLayers.Default);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = pair.Key.Item1;
                Cheap(renderer);
                go.isStatic = true;
            }
            foreach (var mesh in temporary) UnityEngine.Object.DestroyImmediate(mesh);

            // The originals keep their colliders; their renderers go.
            foreach (var r in merged)
            {
                var go = r.gameObject;
                var filter = go.GetComponent<MeshFilter>();
                UnityEngine.Object.DestroyImmediate(r);
                if (filter != null) UnityEngine.Object.DestroyImmediate(filter);
                if (go.transform.childCount == 0 && go.GetComponents<Component>().Length == 1) UnityEngine.Object.DestroyImmediate(go);
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
            Wall("D Mouth N", new Vector2(-6f, 13.6f), new Vector2(1.6f, 13.6f), MH, M("Basalt"),
                new Gap(1.1f, 2.2f, 2.6f), new Gap(6f, 3.0f, 3.0f));
            Wall("D Hab Front", new Vector2(1.6f, 13.6f), new Vector2(9.6f, 13.6f), MH, hab, new Gap(4.4f, 1.6f, 2.1f)); // the module's front, facing the mouth
            CaveWallsAndRails("D Mouth", -6f, 9.6f, 7.6f, 13.6f, MH, "WE", "WE",
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
            Wall("D Hab West", new Vector2(1.6f, 13.6f), new Vector2(1.6f, 24.6f), TH, hab, new Gap(22f - 13.6f, 1.0f, 2.0f));
            Wall("D Tunnel E", new Vector2(1.6f, 24.6f), new Vector2(1.6f, 33.6f), TH, M("Basalt"));
            CaveWallsAndRails("D Tunnel", -1.6f, 1.6f, 13.6f, 33.6f, TH, "", "WE", new Opening('W', 23f, 2.4f, 2.6f), new Opening('E', 22f, 1.0f, 2.0f));
            TeleportArea("D Tunnel", -1.25f, 1.25f, 13.95f, 33.25f);

            // --- habitat module: two pressurised rooms with metal walls
            Room("D Hab Living", 1.6f, 9.6f, 13.6f, 18.6f, HH, "E", hab, new Opening('S', 6f, 1.6f, 2.1f), new Opening('N', 4f, 1.2f, 2.0f));
            Room("D Hab Workshop", 1.6f, 9.6f, 18.6f, 24.6f, HH, "NSE", hab, new Opening('S', 4f, 1.2f, 2.0f), new Opening('W', 22f, 1.0f, 2.0f));
            TeleportArea("D Hab Hatch Doorway", 1.0f, 2.2f, 21.5f, 22.5f);
            var habDoor = BuildDoor("Habitat Door", "lt_door_hab", new Vector3(6f, 0, 13.6f), 1.6f, 2.1f, false, false, true);

            // --- side tunnel, skylight chamber, back passage, drill chamber
            CaveRoom("D Side Tunnel", -6f, -3.8f, 13.6f, 18f, 2.8f, "WE", "W", new Opening('S', -4.9f, 2.2f, 2.6f), new Opening('N', -4.9f, 2.2f, 2.6f));
            CaveRoom("D Skylight", -12f, -1.6f, 18f, 28f, SH, "SNWE", "WN", new Opening('S', -4.9f, 2.2f, 2.6f), new Opening('E', 23f, 2.4f, 2.6f), new Opening('N', -8.1f, 2.0f, 2.4f));
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
            CaveRoom("D Drill", -6f, 6f, 33.6f, 41.4f, DH, "SNWE", "NE", new Opening('S', 0f, 3.0f, 3.0f), new Opening('W', 37.7f, 1.8f, 2.4f));

            // --- the skylight: a collapsed section of roof, open to the stars
            // (built as a frame of ceiling around a hole, a shaft above it and a sky cap)
            {
                var sky = Empty(s_Level, "D Skylight Shaft", Vector3.zero, null, GameLayers.Environment).transform;
                const float top = 6.2f; // close enough that the stars are not lost in the fog
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
                var earthshine = PointLight(s_Lights, "Earthshine", new Vector3(-7f, SH - 0.3f, 23f), new Color(0.55f, 0.65f, 0.85f), 2.6f, 9f);
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
            WorkLight("D Work Light Mouth W", -3.0f, 11.6f, 2.0f, sodium, 4.8f, 11f);
            WorkLight("D Work Light Mouth E", 6.6f, 9.0f, 2.0f, sodium, 4.2f, 10f);
            WorkLight("D Work Light Tunnel 1", -1.05f, 16.8f, 1.9f, sodium, 3.6f, 9f);
            WorkLight("D Work Light Tunnel 2", 1.05f, 20.6f, 1.9f, sodium, 3.4f, 9f, true);
            WorkLight("D Work Light Tunnel 3", -1.05f, 25.6f, 1.9f, sodium, 3.6f, 9f);
            WorkLight("D Work Light Tunnel 4", 1.05f, 29.2f, 1.9f, sodium, 3.4f, 9f);
            WorkLight("D Work Light Tunnel 5", -1.05f, 32.4f, 1.9f, sodium, 3.4f, 9f, true);
            WorkLight("D Work Light Skylight", -2.6f, 26.8f, 2.2f, sodium, 3.9f, 11f);
            WorkLight("D Work Light Side", -4.0f, 17.4f, 1.8f, sodium, 2.8f, 8f, true);
            WorkLight("D Work Light Drill W", -4.6f, 36.4f, 2.4f, new Color(1f, 0.55f, 0.3f), 4.8f, 12f);
            WorkLight("D Work Light Drill E", 4.8f, 36.8f, 2.4f, sodium, 4.2f, 12f);
            RoomLight("D Hab Living Light", 5.6f, 2.4f, 16.1f, white, 3.0f, 8f);
            RoomLight("D Hab Workshop Light", 5.6f, 2.4f, 21.6f, new Color(0.6f, 1f, 0.75f), 2.8f, 8f);
            RoomLight("D Passage Light", -8.1f, 2.1f, 33f, new Color(1f, 0.5f, 0.25f), 2.8f, 8f);
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

            // Fallen rock along the foot of the walls, so the tube does not read as a box.
            var scatter = new System.Random(4242);
            foreach (var p in new[]
            {
                new Vector3(-5.75f, 0, 9.4f), new Vector3(-5.8f, 0, 11.2f), new Vector3(9.35f, 0, 10.6f), new Vector3(-1.95f, 0, 7.85f),
                new Vector3(-1.35f, 0, 19.0f), new Vector3(-1.35f, 0, 30.2f), new Vector3(1.35f, 0, 27.0f), new Vector3(1.35f, 0, 32.2f),
                new Vector3(-11.75f, 0, 19.0f), new Vector3(-11.8f, 0, 21.6f), new Vector3(-11.7f, 0, 25.0f), new Vector3(-2.0f, 0, 27.7f),
                new Vector3(-11.0f, 0, 27.75f), new Vector3(-5.8f, 0, 34.4f), new Vector3(5.75f, 0, 37.6f), new Vector3(-1.2f, 0, 41.15f),
                new Vector3(5.7f, 0, 40.6f), new Vector3(-3.95f, 0, 14.4f), new Vector3(-8.95f, 0, 30.0f), new Vector3(-7.25f, 0, 34.8f),
            })
            {
                float r = 0.22f + (float)scatter.NextDouble() * 0.25f;
                Rock(p, new Vector3(r * 1.3f, r * 0.8f, r), 400 + (int)(p.x * 10 + p.z * 100), false);
            }
            foreach (var t in s_Props.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Mast" || t.name == "Leg" || t.name == "Lamp Head" || t.name == "Boulder" || t.name == "Rig Mast" || t.name == "Drill Bit") t.gameObject.isStatic = true;
            }
            CaveRockAndCombine(root);

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

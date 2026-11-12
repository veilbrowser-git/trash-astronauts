using System;
using System.Collections.Generic;
using System.Linq;
using TrashAstronauts.Mission;
using TrashAstronauts.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// Environment art pass: worn, painted, riveted surfaces with normal maps, a character per wreck,
    /// ceiling light fixtures and wall pipe runs. Everything here is decoration: no colliders, no
    /// gameplay objects moved. Each wreck gets at most a handful of extra (combined, static) renderers.
    /// </summary>
    public static partial class Builder
    {
        public const string DressingName = "Env Dressing";

        sealed class SurfaceStyle
        {
            public string id;
            public Color paint, metal, grime, stain, rust;
            public int panelW = 256, panelH = 256, rivetStep = 32, seed = 1, leaks = 4, scorches = 1;
            public float chip = 0.6f, rustAmount, grimeAmount = 0.6f, hatchChance = 0.25f;
            public Color floorMetal, floorDirt, ceiling;
            public bool perforatedCeiling = true;
        }

        static readonly Dictionary<string, SurfaceStyle> s_SurfaceStyles = new Dictionary<string, SurfaceStyle>();

        static SurfaceStyle[] EnvironmentStyles() => new[]
        {
            // The airlock every wreck docks to: plain grey service paint.
            new SurfaceStyle { id = "Dock", paint = new Color(0.40f, 0.42f, 0.44f), metal = new Color(0.58f, 0.59f, 0.61f), grime = new Color(0.10f, 0.095f, 0.085f),
                stain = new Color(0.20f, 0.15f, 0.10f), rust = new Color(0.30f, 0.15f, 0.07f), seed = 11, chip = 0.55f, grimeAmount = 0.55f, leaks = 3, scorches = 0,
                floorMetal = new Color(0.42f, 0.43f, 0.44f), floorDirt = new Color(0.10f, 0.09f, 0.08f), ceiling = new Color(0.30f, 0.31f, 0.32f) },
            // The tug: kept, cleaned, a warm cream.
            new SurfaceStyle { id = "Tug", paint = new Color(0.56f, 0.52f, 0.45f), metal = new Color(0.62f, 0.62f, 0.62f), grime = new Color(0.16f, 0.14f, 0.11f),
                stain = new Color(0.25f, 0.2f, 0.14f), rust = new Color(0.3f, 0.16f, 0.08f), seed = 23, chip = 0.3f, grimeAmount = 0.3f, leaks = 1, scorches = 0, hatchChance = 0.4f,
                floorMetal = new Color(0.40f, 0.40f, 0.40f), floorDirt = new Color(0.12f, 0.11f, 0.10f), ceiling = new Color(0.36f, 0.35f, 0.33f) },
            // A: hauler. Navy-grey paint, chipped down to steel, leaks and scorch marks.
            new SurfaceStyle { id = "Hauler", paint = new Color(0.25f, 0.31f, 0.38f), metal = new Color(0.55f, 0.56f, 0.58f), grime = new Color(0.07f, 0.065f, 0.06f),
                stain = new Color(0.22f, 0.14f, 0.07f), rust = new Color(0.32f, 0.15f, 0.07f), seed = 37, chip = 0.85f, rustAmount = 0.3f, grimeAmount = 0.75f, leaks = 6, scorches = 2,
                floorMetal = new Color(0.36f, 0.38f, 0.40f), floorDirt = new Color(0.07f, 0.065f, 0.06f), ceiling = new Color(0.22f, 0.25f, 0.28f) },
            // B: ore barge. Ochre industrial paint, densely riveted plating, rust everywhere.
            new SurfaceStyle { id = "Barge", paint = new Color(0.44f, 0.36f, 0.16f), metal = new Color(0.46f, 0.44f, 0.41f), grime = new Color(0.09f, 0.07f, 0.05f),
                stain = new Color(0.26f, 0.12f, 0.05f), rust = new Color(0.34f, 0.15f, 0.06f), seed = 53, panelW = 256, panelH = 128, rivetStep = 20, chip = 1f, rustAmount = 1f,
                grimeAmount = 0.9f, leaks = 9, scorches = 3, hatchChance = 0.1f, perforatedCeiling = false,
                floorMetal = new Color(0.33f, 0.30f, 0.26f), floorDirt = new Color(0.10f, 0.06f, 0.03f), ceiling = new Color(0.25f, 0.22f, 0.17f) },
            // C: survey ship. Off-white laboratory panels, screwed not riveted, scuffed and stained.
            new SurfaceStyle { id = "Survey", paint = new Color(0.62f, 0.63f, 0.60f), metal = new Color(0.62f, 0.64f, 0.67f), grime = new Color(0.12f, 0.12f, 0.11f),
                stain = new Color(0.30f, 0.24f, 0.15f), rust = new Color(0.3f, 0.18f, 0.1f), seed = 71, panelW = 128, panelH = 256, rivetStep = 0, chip = 0.35f,
                grimeAmount = 0.5f, leaks = 4, scorches = 1, hatchChance = 0.35f,
                floorMetal = new Color(0.40f, 0.42f, 0.43f), floorDirt = new Color(0.11f, 0.11f, 0.10f), ceiling = new Color(0.46f, 0.47f, 0.46f) },
        };

        /// <summary>Called right after BuildMaterials: replaces the flat wall, floor, ceiling and door materials with detailed ones.</summary>
        static void BuildEnvironmentMaterials()
        {
            s_SurfaceStyles.Clear();
            foreach (var style in EnvironmentStyles())
            {
                s_SurfaceStyles[style.id] = style;
                const int n = 512;
                WallSurface(style, n, out var wallPx, out var wallH);
                FloorSurface(style, n, out var floorPx, out var floorH);
                CeilingSurface(style, n, out var ceilPx, out var ceilH);
                var wallA = MakeTexture("Env_Wall_" + style.id, n, (px, size, rng) => Array.Copy(wallPx, px, px.Length));
                var wallN = MakeNormalMap("Env_Wall_" + style.id + "_N", n, (x, y) => wallH[y * n + x], 2.2f);
                var floorA = MakeTexture("Env_Floor_" + style.id, n, (px, size, rng) => Array.Copy(floorPx, px, px.Length));
                var floorN = MakeNormalMap("Env_Floor_" + style.id + "_N", n, (x, y) => floorH[y * n + x], 2.6f);
                var ceilA = MakeTexture("Env_Ceiling_" + style.id, n, (px, size, rng) => Array.Copy(ceilPx, px, px.Length));
                var ceilN = MakeNormalMap("Env_Ceiling_" + style.id + "_N", n, (x, y) => ceilH[y * n + x], 1.8f);
                EnvLit("Wall_" + style.id, wallA, wallN, 0.45f);
                EnvLit("Floor_" + style.id, floorA, floorN, 0.5f);
                EnvLit("Ceiling_" + style.id, ceilA, ceilN, 0.3f);
            }

            // The shared materials keep their names (props, prefabs and the tug use them) but get the new surfaces.
            CopyEnv("Wall", "Wall_Dock");
            CopyEnv("WallTug", "Wall_Tug");
            CopyEnv("Floor", "Floor_Dock");
            CopyEnv("Ceiling", "Ceiling_Dock");

            // Doors and bulkheads: heavy single plates with a hatch and dense fasteners.
            var door = new SurfaceStyle
            {
                id = "Door", paint = new Color(0.46f, 0.47f, 0.47f), metal = new Color(0.62f, 0.62f, 0.63f), grime = new Color(0.08f, 0.075f, 0.07f),
                stain = new Color(0.2f, 0.14f, 0.08f), rust = new Color(0.3f, 0.15f, 0.07f), seed = 97, panelW = 256, panelH = 512, rivetStep = 24,
                chip = 0.9f, rustAmount = 0.2f, grimeAmount = 0.6f, leaks = 2, scorches = 1, hatchChance = 1f,
            };
            WallSurface(door, 512, out var doorPx, out var doorH);
            var doorA = MakeTexture("Env_Door", 512, (px, size, rng) => Array.Copy(doorPx, px, px.Length));
            var doorN = MakeNormalMap("Env_Door_N", 512, (x, y) => doorH[y * 512 + x], 2.4f);
            EnvLit("Door", doorA, doorN, 0.5f);

            Lit("Rust_Pipe", new Color(0.36f, 0.2f, 0.11f), 0.2f);
            Lit("FixtureLens", new Color(0.95f, 0.93f, 0.85f), 0.8f, null, new Color(1.25f, 1.18f, 1.0f));
            AssetDatabase.SaveAssets();
        }

        static Material EnvLit(string name, Texture2D albedo, Texture2D normal, float specular)
        {
            var m = Lit(name, Color.white, 0.5f, albedo, null, normal);
            // Smoothness comes from the albedo's alpha: bare metal shines, grime and rust do not.
            m.SetFloat("_SpecularHighlights", 1f);
            m.EnableKeyword("_SPECULAR_COLOR");
            m.SetFloat("_SmoothnessSource", 1f);
            m.EnableKeyword("_GLOSSINESS_FROM_BASE_ALPHA");
            m.SetColor("_SpecColor", new Color(specular, specular, specular, 0.5f));
            m.SetFloat("_BumpScale", 1f);
            EditorUtility.SetDirty(m);
            return m;
        }

        static void CopyEnv(string target, string source)
        {
            var src = M(source);
            var m = EnvLit(target, (Texture2D)src.GetTexture("_BaseMap"), (Texture2D)src.GetTexture("_BumpMap"), src.GetColor("_SpecColor").r);
            s_Mats[target] = m;
        }

        // ------------------------------------------------------------------ tileable noise

        /// <summary>Perlin noise that wraps around the unit square, so tiles meet without a seam.</summary>
        static float TileNoise(float u, float v, float scale, float seed)
        {
            float ox = seed * 17.13f, oy = seed * 31.7f;
            float P(float a, float b) => Mathf.PerlinNoise(a * scale + ox, b * scale + oy);
            float n00 = P(u, v), n10 = P(u - 1f, v), n01 = P(u, v - 1f), n11 = P(u - 1f, v - 1f);
            float r = Mathf.Lerp(Mathf.Lerp(n00, n10, u), Mathf.Lerp(n01, n11, u), v);
            // The blend flattens contrast in the middle of the tile; stretch it back.
            return Mathf.Clamp01((r - 0.5f) * 1.6f + 0.5f);
        }

        static float Fbm(float u, float v, float scale, float seed) =>
            TileNoise(u, v, scale, seed) * 0.55f + TileNoise(u, v, scale * 2f, seed + 1f) * 0.3f + TileNoise(u, v, scale * 4f, seed + 2f) * 0.15f;

        static Color32 C32(Color c, float alpha) => new Color32(B(c.r), B(c.g), B(c.b), B(alpha));

        static int Wrap(int d, int n) { d = Mathf.Abs(d) % n; return Mathf.Min(d, n - d); }

        struct Mark { public int x, y, w, len; public float r; }

        // ------------------------------------------------------------------ walls

        static void WallSurface(SurfaceStyle s, int n, out Color32[] albedo, out float[] height)
        {
            var rng = new System.Random(s.seed);
            int pw = s.panelW, ph = s.panelH, cols = n / pw, rows = n / ph;
            var tint = new float[cols * rows];
            var hatch = new bool[cols * rows];
            for (int i = 0; i < tint.Length; i++)
            {
                tint[i] = ((float)rng.NextDouble() - 0.5f) * 0.12f;
                hatch[i] = rng.NextDouble() < s.hatchChance;
            }
            var rowBrush = new float[n];
            for (int y = 0; y < n; y++) rowBrush[y] = ((float)rng.NextDouble() - 0.5f);
            var leaks = new Mark[s.leaks];
            for (int i = 0; i < leaks.Length; i++)
            {
                int c = rng.Next(cols);
                // Leaks start under a horizontal seam or a fastener and run down.
                leaks[i] = new Mark { x = c * pw + 12 + rng.Next(pw - 24), y = ((rng.Next(rows) + 1) * ph - 6) % n, w = 3 + rng.Next(6), len = 60 + rng.Next(200) };
            }
            var scorch = new Mark[s.scorches];
            for (int i = 0; i < scorch.Length; i++) scorch[i] = new Mark { x = rng.Next(n), y = rng.Next(n), r = 30f + (float)rng.NextDouble() * 60f };
            float seed = s.seed * 0.37f;

            albedo = new Color32[n * n];
            height = new float[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float u = x / (float)n, v = y / (float)n;
                    int px = x % pw, py = y % ph, pi = (y / ph) * cols + x / pw;
                    int ex = Mathf.Min(px, pw - 1 - px), ey = Mathf.Min(py, ph - 1 - py);
                    int e = Mathf.Min(ex, ey);

                    // --- relief
                    float h = 1f;
                    if (e < 2) h = 0.1f;                                         // seam
                    else if (e < 6) h = Mathf.Lerp(0.1f, 1f, (e - 2) / 4f);      // bevelled edge
                    bool inHatch = false;
                    if (hatch[pi])
                    {
                        const int inset = 26;
                        int hx = Mathf.Min(px - inset, pw - 1 - inset - px), hy = Mathf.Min(py - inset, ph - 1 - inset - py);
                        int he = Mathf.Min(hx, hy);
                        if (he >= -1 && he < 2) h -= 0.55f;                       // hatch groove
                        if (he >= 2) { inHatch = true; h += 0.12f; }              // the hatch sits proud
                        // its four screws
                        int sx = Mathf.Min(Mathf.Abs(px - inset - 9), Mathf.Abs(pw - 1 - inset - 9 - px));
                        int sy = Mathf.Min(Mathf.Abs(py - inset - 9), Mathf.Abs(ph - 1 - inset - 9 - py));
                        float sd = Mathf.Sqrt(sx * sx + sy * sy);
                        if (sd < 3.5f) h += 0.35f * (1f - sd / 3.5f) - (sd < 1f ? 0.3f : 0f);
                    }
                    float rivet = 0f;
                    if (s.rivetStep > 0)
                    {
                        const int rin = 10;
                        float best = 99f;
                        // along vertical edges
                        int ax = px < pw / 2 ? rin : pw - 1 - rin;
                        int ay = Mathf.RoundToInt((py - s.rivetStep * 0.5f) / s.rivetStep) * s.rivetStep + s.rivetStep / 2;
                        if (ay > rin - 2 && ay < ph - rin + 2) best = Mathf.Min(best, Mathf.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay)));
                        int by = py < ph / 2 ? rin : ph - 1 - rin;
                        int bx = Mathf.RoundToInt((px - s.rivetStep * 0.5f) / s.rivetStep) * s.rivetStep + s.rivetStep / 2;
                        if (bx > rin - 2 && bx < pw - rin + 2) best = Mathf.Min(best, Mathf.Sqrt((px - bx) * (px - bx) + (py - by) * (py - by)));
                        const float rr = 4.2f;
                        if (best < rr) { rivet = Mathf.Sqrt(1f - (best / rr) * (best / rr)); h += 0.7f * rivet; }
                    }
                    else
                    {
                        // Countersunk screws in the panel corners only.
                        int cx = Mathf.Min(px, pw - 1 - px) - 12, cy = Mathf.Min(py, ph - 1 - py) - 12;
                        float d = Mathf.Sqrt(cx * cx + cy * cy);
                        if (d < 4.5f) { h -= 0.35f * (1f - d / 4.5f); if (Mathf.Abs(cx) < 1 && d < 3.5f) h -= 0.2f; rivet = 0.5f; }
                    }
                    float dents = Fbm(u, v, 5f, seed + 3f);
                    h += (dents - 0.5f) * 0.18f;

                    // --- paint and wear
                    float chipNoise = Fbm(u, v, 14f, seed + 7f) * 0.75f + TileNoise(u, v, 48f, seed + 9f) * 0.25f;
                    float edgeWear = Mathf.Clamp01(1f - e / 22f) + rivet * 0.6f + (inHatch ? 0f : 0f);
                    bool chipped = chipNoise + edgeWear * 0.3f * s.chip > 1.02f - 0.3f * s.chip;
                    if (chipped) h -= 0.05f;

                    float brush = 1f + rowBrush[y] * 0.06f + (TileNoise(u * 0.02f, v, 120f, seed) - 0.5f) * 0.08f;
                    Color paint = s.paint * (1f + tint[pi]) * (0.97f + 0.03f * brush);
                    Color c = chipped ? s.metal * brush : paint;
                    float gloss = chipped ? 0.62f : 0.38f;

                    // grime: in seams, along the bottom of every panel, in blotches, thick near the floor
                    float blotch = Fbm(u, v, 3f, seed + 13f);
                    float grime = Mathf.Clamp01(1f - e / 12f) * 0.55f
                                  + Mathf.Clamp01(1f - py / (ph * 0.3f)) * 0.3f * TileNoise(u, v, 20f, seed + 17f)
                                  + Mathf.Clamp01(blotch - 0.45f) * 1.1f
                                  + Mathf.Clamp01(1f - v / 0.14f) * 0.45f;
                    grime = Mathf.Clamp01(grime * s.grimeAmount);
                    if (rivet > 0f) c = Color.Lerp(c, s.metal * 0.8f, 0.35f * rivet);

                    float rust = 0f;
                    if (s.rustAmount > 0f)
                    {
                        float rn = Fbm(u, v, 9f, seed + 21f);
                        rust = Mathf.Clamp01((rn - 0.62f + edgeWear * 0.12f) * 4f) * s.rustAmount;
                        if (chipped) rust = Mathf.Max(rust, 0.6f * s.rustAmount);
                        if (rivet > 0f) rust = Mathf.Max(rust, 0.7f * s.rustAmount);
                    }

                    float leak = 0f;
                    foreach (var l in leaks)
                    {
                        int below = l.y - y;
                        if (below < 0) below += n;
                        if (below > l.len) continue;
                        float t = below / (float)l.len;
                        float w = l.w * (1f + t * 1.5f) + TileNoise(u, v, 30f, seed + l.x) * 3f;
                        int dx = Wrap(x - l.x, n);
                        if (dx < w) leak = Mathf.Max(leak, (1f - dx / w) * (1f - t) * 0.85f);
                    }

                    float soot = 0f;
                    foreach (var sc in scorch)
                    {
                        float dx = Wrap(x - sc.x, n), dy = Wrap(y - sc.y, n);
                        float d = Mathf.Sqrt(dx * dx + dy * dy * 0.6f) / sc.r;     // flames lick upward: taller than wide
                        d += (TileNoise(u, v, 18f, seed + 29f) - 0.5f) * 0.5f;
                        soot = Mathf.Max(soot, Mathf.Clamp01(1f - d));
                    }

                    c = Color.Lerp(c, s.rust * (0.8f + 0.4f * TileNoise(u, v, 40f, seed + 5f)), rust * 0.85f);
                    c = Color.Lerp(c, s.grime, grime);
                    c = Color.Lerp(c, s.stain, leak);
                    c = Color.Lerp(c, new Color(0.02f, 0.018f, 0.016f), soot * 0.9f);
                    // cavity shading: seams and grooves read darker even under flat light
                    c *= Mathf.Lerp(0.45f, 1f, Mathf.Clamp01(h + 0.1f));
                    c *= 0.96f + ((float)rng.NextDouble() - 0.5f) * 0.05f;

                    gloss = Mathf.Lerp(gloss, 0.08f, Mathf.Max(grime, Mathf.Max(rust, soot)));
                    gloss = Mathf.Lerp(gloss, 0.55f, leak * 0.5f);                  // fresh leaks are wet
                    albedo[y * n + x] = C32(c, gloss);
                    height[y * n + x] = h;
                }
            }
        }

        // ------------------------------------------------------------------ floors: anti-slip tread plate

        static void FloorSurface(SurfaceStyle s, int n, out Color32[] albedo, out float[] height)
        {
            var rng = new System.Random(s.seed + 1000);
            float seed = s.seed * 0.53f + 100f;
            const int plate = 256, cell = 32;
            var spills = new Mark[1 + s.leaks / 4];
            for (int i = 0; i < spills.Length; i++) spills[i] = new Mark { x = rng.Next(n), y = rng.Next(n), r = 18f + (float)rng.NextDouble() * 45f };
            albedo = new Color32[n * n];
            height = new float[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float u = x / (float)n, v = y / (float)n;
                    int px = x % plate, py = y % plate;
                    int e = Mathf.Min(Mathf.Min(px, plate - 1 - px), Mathf.Min(py, plate - 1 - py));

                    // Diamond tread: lozenges on a grid, alternating direction like the real thing.
                    int gx = x / cell, gy = y / cell;
                    float lx = (x % cell) - cell * 0.5f + 0.5f, ly = (y % cell) - cell * 0.5f + 0.5f;
                    float sgn = ((gx + gy) & 1) == 0 ? 1f : -1f;
                    float a = (lx + sgn * ly) * 0.7071f, b = (lx - sgn * ly) * 0.7071f;
                    float lz = (a * a) / (11.5f * 11.5f) + (b * b) / (3.2f * 3.2f);
                    float tread = lz < 1f ? Mathf.Sqrt(1f - lz) : 0f;
                    float h = 0.2f + Mathf.Min(1f, tread * 1.6f) * 0.7f;

                    float bolt = 0f;
                    if (e < 3) h = -0.4f;                                       // plate joint
                    else
                    {
                        int cx = Mathf.Min(px, plate - 1 - px) - 14, cy = Mathf.Min(py, plate - 1 - py) - 14;
                        float d = Mathf.Sqrt(cx * cx + cy * cy);
                        if (d < 7f) { h = d < 5f ? 0.55f - (d < 1.2f ? 0.3f : 0f) : 0f; bolt = 1f; }
                    }

                    float wear = Fbm(u, v, 4f, seed + 1f);
                    float dirt = Mathf.Clamp01(1f - h) * 0.65f + Mathf.Clamp01(Fbm(u, v, 3f, seed + 4f) - 0.52f) * 0.8f + Mathf.Clamp01(1f - e / 10f) * 0.4f;
                    dirt = Mathf.Clamp01(dirt * (0.55f + 0.45f * s.grimeAmount));
                    Color metal = s.floorMetal * (0.94f + 0.12f * TileNoise(u, v, 60f, seed + 7f));
                    // the tops of the tread are polished by boots, the low ground collects dirt
                    float shine = tread > 0f ? Mathf.Clamp01(wear * 1.4f - 0.3f) * tread : 0f;
                    Color c = Color.Lerp(metal, s.metal * 1.05f, shine * 0.6f);
                    c = Color.Lerp(c, s.floorDirt, dirt);
                    float rust = 0f;
                    if (s.rustAmount > 0f) rust = Mathf.Clamp01((Fbm(u, v, 7f, seed + 9f) - 0.55f) * 3f) * s.rustAmount;
                    c = Color.Lerp(c, s.rust, rust * 0.7f);
                    float spill = 0f;
                    foreach (var sp in spills)
                    {
                        float dx = Wrap(x - sp.x, n), dy = Wrap(y - sp.y, n);
                        float d = Mathf.Sqrt(dx * dx + dy * dy) / sp.r + (TileNoise(u, v, 12f, seed + sp.x) - 0.5f) * 0.6f;
                        spill = Mathf.Max(spill, Mathf.Clamp01((1f - d) * 1.5f));
                    }
                    c = Color.Lerp(c, s.floorDirt, spill * 0.35f);
                    if (bolt > 0f) c = Color.Lerp(c, s.metal * 0.7f, 0.4f);
                    c *= Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(h + 0.3f));
                    c *= 0.97f + ((float)rng.NextDouble() - 0.5f) * 0.06f;

                    float gloss = Mathf.Lerp(0.3f, 0.7f, shine);
                    gloss = Mathf.Lerp(gloss, 0.1f, Mathf.Max(dirt * 0.8f, rust));
                    gloss = Mathf.Lerp(gloss, 0.75f, spill * 0.8f);              // oily
                    albedo[y * n + x] = C32(c, gloss);
                    height[y * n + x] = h;
                }
            }
        }

        // ------------------------------------------------------------------ ceilings

        static void CeilingSurface(SurfaceStyle s, int n, out Color32[] albedo, out float[] height)
        {
            var rng = new System.Random(s.seed + 2000);
            float seed = s.seed * 0.71f + 200f;
            const int tile = 128;
            var missing = new bool[(n / tile) * (n / tile)];
            for (int i = 0; i < missing.Length; i++) missing[i] = rng.NextDouble() < 0.04;
            albedo = new Color32[n * n];
            height = new float[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float u = x / (float)n, v = y / (float)n;
                    int px = x % tile, py = y % tile;
                    int ti = (y / tile) * (n / tile) + x / tile;
                    int e = Mathf.Min(Mathf.Min(px, tile - 1 - px), Mathf.Min(py, tile - 1 - py));
                    float h;
                    if (e < 5) h = 1f;                                          // grid rib
                    else if (e < 8) h = 0.3f;
                    else if (missing[ti]) h = -0.6f;                            // a panel has fallen out: dark void
                    else
                    {
                        h = 0.45f;
                        if (s.perforatedCeiling)
                        {
                            float hx = ((px - 8) % 9) - 4f, hy = ((py - 8) % 9) - 4f;
                            if (hx * hx + hy * hy < 3.2f) h = 0.2f;              // perforation
                        }
                        else h = 0.4f + 0.12f * Mathf.Sin(px * Mathf.PI * 2f / 16f); // corrugated
                    }
                    Color c = Color.Lerp(s.ceiling, s.metal * 0.6f, e < 5 ? 0.6f : 0f);
                    float water = Mathf.Clamp01((Fbm(u, v, 3f, seed + 2f) - 0.58f) * 3.5f);
                    float ring = water > 0f && water < 0.25f ? 0.5f : 0f;           // tide-mark at the stain's edge
                    c = Color.Lerp(c, s.stain, water * 0.45f + ring * 0.3f);
                    c = Color.Lerp(c, s.grime, Mathf.Clamp01(Fbm(u, v, 7f, seed + 5f) - 0.4f) * s.grimeAmount);
                    if (missing[ti] && e >= 8) c = new Color(0.02f, 0.02f, 0.025f);
                    c *= Mathf.Lerp(0.55f, 1f, Mathf.Clamp01(h));
                    c *= 0.97f + ((float)rng.NextDouble() - 0.5f) * 0.05f;
                    albedo[y * n + x] = C32(c, e < 5 ? 0.45f : 0.2f);
                    height[y * n + x] = h;
                }
            }
        }

        // ------------------------------------------------------------------ per-wreck dressing

        /// <summary>
        /// Gives one wreck its own surfaces, fixtures where its lights are, and pipe runs along its walls.
        /// Call after the wreck is complete. Moves nothing and adds no collider.
        /// </summary>
        static void DressWreck(WreckMap map, string styleId)
        {
            var root = map.transform;
            Material wall = M("Wall_" + styleId), floor = M("Floor_" + styleId), ceiling = M("Ceiling_" + styleId);
            Material oldWall = M("Wall"), oldFloor = M("Floor"), oldCeiling = M("Ceiling");
            var renderers = root.GetComponentsInChildren<MeshRenderer>(true);
            foreach (var r in renderers)
            {
                var m = r.sharedMaterial;
                if (m == oldWall) r.sharedMaterial = wall;
                else if (m == oldFloor) r.sharedMaterial = floor;
                else if (m == oldCeiling) r.sharedMaterial = ceiling;
            }

            var level = root.Find("Level") ?? root;
            var parts = new Dictionary<Material, List<CombineInstance>>();
            void Add(Mesh mesh, Matrix4x4 matrix, Material mat)
            {
                if (!parts.TryGetValue(mat, out var list)) parts[mat] = list = new List<CombineInstance>();
                list.Add(new CombineInstance { mesh = mesh, transform = matrix });
            }
            void AddBox(Vector3 centre, Vector3 size, Material mat) => Add(BoxMesh(Vector3.one), Matrix4x4.TRS(centre, Quaternion.identity, size), mat);
            void AddTube(Vector3 a, Vector3 b, float radius, Material mat, int segments = 8)
            {
                Vector3 d = b - a;
                float len = d.magnitude;
                if (len < 0.01f) return;
                Add(Tube(radius, 1f, segments), Matrix4x4.TRS((a + b) * 0.5f, Quaternion.FromToRotation(Vector3.up, d / len), new Vector3(1f, len, 1f)), mat);
            }

            var floors = renderers.Where(r => r.name == "Floor").Select(r => r.bounds).ToList();
            var ceilings = renderers.Where(r => r.name == "Ceiling").Select(r => r.bounds).ToList();
            var strips = renderers.Where(r => r.name.Contains("Strip")).Select(r => r.bounds).ToList();
            bool OverFloor(Vector3 p) => floors.Any(f => p.x > f.min.x + 0.05f && p.x < f.max.x - 0.05f && p.z > f.min.z + 0.05f && p.z < f.max.z - 0.05f);

            // --- light fixtures: a housing and a glowing lens on the ceiling above each lamp
            float lensR = 0f, lensG = 0f, lensB = 0f;
            int lamps = 0;
            foreach (var light in root.GetComponentsInChildren<Light>(true))
            {
                if (light.type != LightType.Point) continue;
                // Slightly different colour temperature from lamp to lamp, as old tubes age unevenly.
                int hash = (Mathf.RoundToInt(light.transform.position.x * 13f) * 73856093) ^ (Mathf.RoundToInt(light.transform.position.z * 13f) * 19349663);
                float k = ((hash & 0xff) / 255f - 0.5f) * 0.16f;
                Color c = light.color;
                light.color = new Color(Mathf.Clamp01(c.r * (1f + k)), c.g, Mathf.Clamp01(c.b * (1f - k)), c.a);

                Vector3 p = light.transform.position;
                if (strips.Any(b => Mathf.Abs(b.center.x - p.x) < b.extents.x + 0.5f && Mathf.Abs(b.center.z - p.z) < b.extents.z + 0.5f)) continue;
                float best = float.MaxValue;
                foreach (var cb in ceilings)
                {
                    if (p.x < cb.min.x || p.x > cb.max.x || p.z < cb.min.z || p.z > cb.max.z) continue;
                    if (cb.min.y < p.y - 0.05f || cb.min.y - p.y > 1.5f) continue;
                    best = Mathf.Min(best, cb.min.y);
                }
                if (best == float.MaxValue) continue;
                // Long axis along the room's long side is unknown here; alternate by position for variety.
                bool alongX = ((hash >> 8) & 1) == 0;
                Vector3 len = alongX ? new Vector3(0.72f, 0f, 0f) : new Vector3(0f, 0f, 0.72f);
                Vector3 wid = alongX ? new Vector3(0f, 0f, 0.22f) : new Vector3(0.22f, 0f, 0f);
                Vector3 top = new Vector3(p.x, best, p.z);
                AddBox(top + Vector3.down * 0.04f, len + wid + new Vector3(0f, 0.08f, 0f), M("Trim"));
                AddBox(top + Vector3.down * 0.084f, len * 0.86f + wid * 0.62f + new Vector3(0f, 0.012f, 0f), M("FixtureLens"));
                // a wire cage over the lens
                for (int i = -2; i <= 2; i++)
                    AddBox(top + Vector3.down * 0.094f + len * (i * 0.2f), (alongX ? new Vector3(0.012f, 0.008f, 0.2f) : new Vector3(0.2f, 0.008f, 0.012f)), M("Steel"));
                lensR += light.color.r; lensG += light.color.g; lensB += light.color.b; lamps++;
            }

            // --- pipe runs, cables and brackets high on full-height walls that face a floor
            var pipeMat = styleId == "Barge" ? M("Rust_Pipe") : styleId == "Survey" ? M("PaintWhite") : M("Pipe");
            int wallIndex = 0;
            foreach (var r in renderers)
            {
                if (r.name != "Segment" || r.sharedMaterial != wall) continue;
                var bw = r.bounds;
                if (bw.min.y > 0.05f) continue;
                float hgt = bw.max.y;
                if (hgt < 2.2f) continue;
                bool alongX = bw.size.x > bw.size.z;
                float length = alongX ? bw.size.x : bw.size.z;
                if (length < 1.4f) continue;
                wallIndex++;
                foreach (int side in new[] { -1, 1 })
                {
                    Vector3 normal = alongX ? new Vector3(0, 0, side) : new Vector3(side, 0, 0);
                    Vector3 axis = alongX ? Vector3.right : Vector3.forward;
                    Vector3 face = new Vector3(bw.center.x, 0f, bw.center.z) + normal * (alongX ? bw.extents.z : bw.extents.x);
                    if (!OverFloor(face + normal * 0.7f)) continue;
                    // Only some walls carry services, as on a real ship.
                    if (((wallIndex * 7 + side + 3) % 3) == 0) continue;
                    float half = length * 0.5f - 0.12f;
                    Vector3 a = face - axis * half, b = face + axis * half;
                    float y1 = hgt - 0.34f, y2 = hgt - 0.47f, yc = hgt - 0.24f;
                    Vector3 o1 = normal * 0.085f, o2 = normal * 0.07f, oc = normal * 0.05f;
                    AddTube(a + Vector3.up * y1 + o1, b + Vector3.up * y1 + o1, 0.045f, pipeMat);
                    AddTube(a + Vector3.up * y2 + o2, b + Vector3.up * y2 + o2, 0.028f, M("Steel"));
                    // elbows back into the wall at each end
                    AddTube(a + Vector3.up * y1, a + Vector3.up * y1 + o1, 0.045f, pipeMat);
                    AddTube(b + Vector3.up * y1, b + Vector3.up * y1 + o1, 0.045f, pipeMat);
                    AddTube(a + Vector3.up * y2, a + Vector3.up * y2 + o2, 0.028f, M("Steel"));
                    AddTube(b + Vector3.up * y2, b + Vector3.up * y2 + o2, 0.028f, M("Steel"));
                    int spans = Mathf.Max(1, Mathf.RoundToInt((2f * half) / 1.1f));
                    for (int i = 0; i <= spans; i++)
                    {
                        Vector3 at = Vector3.Lerp(a, b, i / (float)spans);
                        // bracket: a strap from the wall holding both pipes
                        Vector3 size = alongX ? new Vector3(0.035f, 0.26f, 0.15f) : new Vector3(0.15f, 0.26f, 0.035f);
                        AddBox(at + Vector3.up * (y1 + y2) * 0.5f + normal * 0.075f, size, M("Trim"));
                        if (i < spans)
                        {
                            // a sagging cable bundle between brackets
                            Vector3 nxt = Vector3.Lerp(a, b, (i + 1) / (float)spans);
                            const int seg = 4;
                            for (int s = 0; s < seg; s++)
                            {
                                float t0 = s / (float)seg, t1 = (s + 1) / (float)seg;
                                float s0 = 4f * t0 * (1f - t0) * 0.07f, s1 = 4f * t1 * (1f - t1) * 0.07f;
                                Vector3 p0 = Vector3.Lerp(at, nxt, t0) + Vector3.up * (yc - s0) + oc;
                                Vector3 p1 = Vector3.Lerp(at, nxt, t1) + Vector3.up * (yc - s1) + oc;
                                AddTube(p0, p1, 0.018f, M("Cable"), 6);
                                AddTube(p0 + normal * 0.02f + Vector3.up * 0.02f, p1 + normal * 0.02f + Vector3.up * 0.02f, 0.011f, M("Cable"), 5);
                            }
                        }
                        // a valve wheel now and then
                        if (i > 0 && i < spans && ((i + wallIndex) % 4) == 0)
                            Add(Torus(0.07f, 0.01f, 12, 5), Matrix4x4.TRS(at + Vector3.up * y1 + normal * 0.16f, Quaternion.FromToRotation(Vector3.up, normal), Vector3.one), M("PaintOrange"));
                    }
                }
            }

            // --- the view: a planet in one window, each wreck orbiting a different world
            if (styleId == "Hauler") ShowPlanet(root, "Stars", PlanetView(styleId, new Color(0.55f, 0.42f, 0.3f), new Color(0.75f, 0.62f, 0.45f), new Color(0.5f, 0.6f, 0.9f), 0.85f, 0.15f, 0.55f));
            else if (styleId == "Barge") ShowPlanet(root, "Stars", PlanetView(styleId, new Color(0.45f, 0.3f, 0.22f), new Color(0.3f, 0.2f, 0.16f), new Color(0.8f, 0.5f, 0.3f), 0.2f, 0.1f, 0.5f));
            else ShowPlanet(root, "Stars", PlanetView(styleId, new Color(0.18f, 0.32f, 0.5f), new Color(0.6f, 0.7f, 0.8f), new Color(0.4f, 0.7f, 1f), 0.7f, 0.2f, 0.45f));

            // --- combine: one static renderer per material
            var dressing = Empty(level, DressingName, Vector3.zero, null, GameLayers.Default);
            dressing.isStatic = true;
            foreach (var kv in parts)
            {
                var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                mesh.CombineMeshes(kv.Value.ToArray(), true, true);
                mesh.RecalculateBounds();
                string key = "Dress_" + styleId + "_" + kv.Key.name;
                var saved = SaveDressingMesh(key, mesh);
                var go = Empty(dressing.transform, kv.Key.name, Vector3.zero, null, GameLayers.Default);
                go.isStatic = true;
                go.AddComponent<MeshFilter>().sharedMesh = saved;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = kv.Key;
                Cheap(mr);
            }
            Debug.Log("[Builder] Dressed " + styleId + ": " + lamps + " fixtures, " + parts.Count + " renderers, "
                + parts.Sum(kv => kv.Value.Sum(ci => ci.mesh.triangles.Length / 3)) + " triangles.");
        }

        // ------------------------------------------------------------------ dust in the air

        /// <summary>One small lit particle system that follows the player's head: dust that shows in the flashlight beam.</summary>
        static GameObject BuildDustMotes(Vector3 at)
        {
            var dot = MakeTexture("DustMote", 32, (px, n, rng) =>
            {
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float dx = (x + 0.5f) / n - 0.5f, dy = (y + 0.5f) / n - 0.5f;
                        float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy) * 2f);
                        px[y * n + x] = new Color32(255, 255, 255, B(a * a));
                    }
            });
            var mat = Unlit("DustMote", new Color(0.85f, 0.82f, 0.74f, 0.55f), true, dot, "Universal Render Pipeline/Particles/Simple Lit");

            var go = new GameObject("Dust Motes");
            go.transform.position = at;
            go.layer = GameLayers.Default;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.prewarm = true;
            main.playOnAwake = true;
            main.duration = 10f;
            main.startLifetime = 10f;
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.005f, 0.03f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.004f, 0.011f);
            main.startColor = new Color(1f, 1f, 1f, 1f);
            main.gravityModifier = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 80;
            var emission = ps.emission;
            emission.rateOverTime = 8f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(5f, 3f, 5f);
            shape.randomDirectionAmount = 1f;
            var fade = ps.colorOverLifetime;
            fade.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(1f, 0.8f), new GradientAlphaKey(0f, 1f) });
            fade.color = gradient;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.maxParticleSize = 0.02f;
            r.sortMode = ParticleSystemSortMode.None;
            go.AddComponent<DustMotes>();
            return go;
        }

        // ------------------------------------------------------------------ the view outside

        /// <summary>Stars with a faint nebula and a large banded planet, lit from one side, rising into the frame.</summary>
        static Material PlanetView(string id, Color surfaceA, Color surfaceB, Color atmosphere, float cx, float cy, float radius)
        {
            var tex = MakeTexture("View_" + id, 512, (px, n, rng) =>
            {
                PaintStars(px, n, rng);
                Vector3 sun = new Vector3(-0.6f, 0.55f, 0.58f).normalized;
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        float u = x / (float)n, v = y / (float)n;
                        Color c = px[y * n + x];
                        float neb = Mathf.Clamp01(Fbm(u, v, 3f, cx * 10f) - 0.45f);
                        c += new Color(0.10f, 0.05f, 0.13f) * neb * 0.6f + new Color(0.02f, 0.05f, 0.08f) * Mathf.Clamp01(Fbm(u, v, 5f, cy * 10f) - 0.5f);
                        float dx = (u - cx) / radius, dy = (v - cy) / radius;
                        float r2 = dx * dx + dy * dy;
                        if (r2 < 1f)
                        {
                            var nrm = new Vector3(dx, dy, Mathf.Sqrt(1f - r2));
                            float lit = Mathf.Clamp01(Vector3.Dot(nrm, sun) * 1.2f + 0.05f);
                            float lat = dy + 0.025f * Mathf.Sin(dx * 5f + dy * 3f) + 0.04f * (Mathf.PerlinNoise(u * 18f, v * 4f) - 0.5f);
                            float band = Mathf.PerlinNoise(lat * 9f + 3.1f, cx * 7f) * 0.7f + 0.3f * (0.5f + 0.5f * Mathf.Sin(lat * 37f));
                            Color surf = Color.Lerp(surfaceA, surfaceB, band);
                            float limb = Mathf.Pow(1f - nrm.z, 3f);
                            c = surf * lit + atmosphere * limb * (0.3f + lit) ;
                        }
                        else
                        {
                            // thin atmosphere halo on the lit side
                            float d = Mathf.Sqrt(r2) - 1f;
                            float side = Mathf.Clamp01(Vector3.Dot(new Vector3(dx, dy, 0f).normalized, sun) + 0.4f);
                            c += atmosphere * Mathf.Clamp01(1f - d / 0.06f) * side * 0.8f;
                        }
                        px[y * n + x] = new Color32(B(c.r), B(c.g), B(c.b), 255);
                    }
                }
            });
            return Unlit("View_" + id, Color.white, false, tex);
        }

        /// <summary>One window of a wreck (or the tug's windscreen) gets the planet; the rest keep the plain star field.</summary>
        static void ShowPlanet(Transform root, string paneName, Material view)
        {
            var stars = M("Stars");
            var panes = root.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.name == paneName && r.sharedMaterial == stars)
                .OrderBy(r => Mathf.Abs(r.transform.position.x)).ToArray();
            if (panes.Length == 0) return;
            panes[0].sharedMaterial = view;
            // Wide windows: show a horizontal slice of the texture so the planet stays round.
            Vector3 scale = panes[0].transform.lossyScale;
            float aspect = Mathf.Max(1f, scale.x / Mathf.Max(0.01f, scale.y));
            view.SetTextureScale("_BaseMap", new Vector2(1f, 1f / aspect));
            EditorUtility.SetDirty(view);
        }

        /// <summary>The tug's windscreen: the middle pane looks out on a pale ringless giant (the pane shows the lower-left of the texture).</summary>
        static void DressTugView()
        {
            var view = PlanetView("Tug", new Color(0.62f, 0.55f, 0.42f), new Color(0.82f, 0.74f, 0.6f), new Color(0.55f, 0.65f, 1f), 0.36f, 0.02f, 0.26f);
            var stars = M("Stars");
            var pane = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(r => r.name == "Windscreen Pane" && r.sharedMaterial == stars).OrderBy(r => Mathf.Abs(r.transform.position.x)).FirstOrDefault();
            if (pane != null) pane.sharedMaterial = view;
        }

        // ------------------------------------------------------------------ flashlight

        /// <summary>A soft beam: bright centre, a faint reflector ring, a gentle fall-off to the edge of the cone.</summary>
        static Texture2D FlashlightCookie()
        {
            const string name = "FlashlightCookie";
            MakeTexture(name, 256, (px, n, rng) =>
            {
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float dx = (x + 0.5f) / n - 0.5f, dy = (y + 0.5f) / n - 0.5f;
                        float r = Mathf.Sqrt(dx * dx + dy * dy) * 2f;
                        float v = Mathf.Lerp(1f, 0.6f, Mathf.SmoothStep(0.15f, 0.6f, r));
                        v += 0.1f * Mathf.Exp(-Mathf.Pow((r - 0.64f) / 0.04f, 2f));
                        v *= 1f - Mathf.SmoothStep(0.72f, 0.97f, r);
                        v *= 0.97f + 0.03f * Mathf.PerlinNoise(x * 0.09f, y * 0.09f);
                        px[y * n + x] = new Color32(B(v), B(v), B(v), B(v));
                    }
            });
            string path = TextureDir + "/" + name + ".png";
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.sRGBTexture = false;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Mesh SaveDressingMesh(string key, Mesh mesh)
        {
            EnsureFolder(MeshDir);
            string path = MeshDir + "/" + key + ".asset";
            if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
            mesh.name = key;
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }
    }
}

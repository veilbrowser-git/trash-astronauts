using System;
using System.Collections.Generic;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// Organic shapes for the creatures: lumpy smooth-shaded masses, tapered muscle, curved ribs, and the
    /// skin texture. Still generated in code, but rounded and wet rather than boxes.
    /// </summary>
    public static partial class Builder
    {
        /// <summary>A smooth lump: an ellipsoid pushed in and out by a few overlapping waves so no two look alike.</summary>
        static Mesh Blob(string key, Vector3 radii, float lump, int seed, int lon = 20, int lat = 12)
        {
            return SaveMesh("Blob_" + key + "_" + F(radii.x) + "_" + F(radii.y) + "_" + F(radii.z) + "_" + F(lump) + "_" + seed, () =>
            {
                var rng = new System.Random(seed);
                float[] ph = new float[9];
                for (int i = 0; i < ph.Length; i++) ph[i] = (float)rng.NextDouble() * 6.283f;

                var verts = new List<Vector3>();
                var uvs = new List<Vector2>();
                var tris = new List<int>();
                for (int y = 0; y <= lat; y++)
                {
                    float v = y / (float)lat;
                    float phi = v * Mathf.PI;
                    for (int x = 0; x <= lon; x++)
                    {
                        float u = x / (float)lon;
                        float theta = u * Mathf.PI * 2f;
                        var n = new Vector3(Mathf.Sin(phi) * Mathf.Cos(theta), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(theta));
                        // Waves are functions of direction, so the seam at u = 0 / 1 closes exactly.
                        float d = Mathf.Sin(n.x * 3.1f + ph[0]) * Mathf.Sin(n.y * 2.7f + ph[1]) * 0.5f
                                + Mathf.Sin(n.z * 4.3f + ph[2] + n.x * 2.1f) * 0.3f
                                + Mathf.Sin(n.y * 6.1f + ph[3]) * Mathf.Sin(n.z * 5.3f + ph[4]) * 0.2f;
                        Vector3 p = Vector3.Scale(n, radii) * (1f + d * (radii.magnitude > 1e-5f ? lump / Mathf.Max(radii.x, Mathf.Max(radii.y, radii.z)) : 0f));
                        verts.Add(p);
                        uvs.Add(new Vector2(u * 2f, v * 1.5f));
                    }
                }
                for (int y = 0; y < lat; y++)
                {
                    for (int x = 0; x < lon; x++)
                    {
                        int a = y * (lon + 1) + x, b = a + 1, c = a + lon + 1, e = c + 1;
                        tris.Add(a); tris.Add(b); tris.Add(c);
                        tris.Add(b); tris.Add(e); tris.Add(c);
                    }
                }
                return SmoothMesh(verts, uvs, tris, lon + 1, lat + 1, true);
            });
        }

        /// <summary>Revolves a profile like <see cref="Lathe"/>, but shaded smoothly along its length (for limbs and necks).</summary>
        static Mesh SmoothLathe(string key, int segments, params Vector2[] profile)
        {
            return SaveMesh("SmoothLathe_" + key + "_" + segments, () =>
            {
                var verts = new List<Vector3>();
                var uvs = new List<Vector2>();
                var tris = new List<int>();
                for (int i = 0; i < profile.Length; i++)
                {
                    for (int s = 0; s <= segments; s++)
                    {
                        float ang = s / (float)segments * Mathf.PI * 2f;
                        verts.Add(new Vector3(profile[i].x * Mathf.Cos(ang), profile[i].y, profile[i].x * Mathf.Sin(ang)));
                        uvs.Add(new Vector2(s / (float)segments * 2f, profile[i].y * 4f));
                    }
                }
                for (int i = 0; i + 1 < profile.Length; i++)
                {
                    for (int s = 0; s < segments; s++)
                    {
                        int a = i * (segments + 1) + s, b = a + 1, c = a + segments + 1, e = c + 1;
                        tris.Add(a); tris.Add(c); tris.Add(b);
                        tris.Add(b); tris.Add(c); tris.Add(e);
                    }
                }
                return SmoothMesh(verts, uvs, tris, segments + 1, profile.Length, true);
            });
        }

        /// <summary>A tapered length of muscle or bone hanging down the Y axis, thicker in the middle. Centred on its length.</summary>
        static Mesh Muscle(string key, float length, float rTop, float rMid, float rEnd, int segments = 10)
        {
            float h = length * 0.5f;
            return SmoothLathe("Muscle_" + key + "_" + F(length) + "_" + F(rTop) + "_" + F(rMid) + "_" + F(rEnd), segments,
                new Vector2(0.0001f, h), new Vector2(rTop * 0.75f, h * 0.96f), new Vector2(rTop, h * 0.8f), new Vector2(rMid, h * 0.25f),
                new Vector2(Mathf.Lerp(rMid, rEnd, 0.6f), -h * 0.4f), new Vector2(rEnd, -h * 0.82f), new Vector2(rEnd * 0.7f, -h * 0.96f), new Vector2(0.0001f, -h));
        }

        /// <summary>A curved bar: part of a ring around the Z axis, from <paramref name="fromDeg"/> to <paramref name="toDeg"/> measured from +Y toward +X, tapering to a point.</summary>
        static Mesh Arc(string key, float ringRadius, float tubeRadius, float fromDeg, float toDeg, int steps = 12, int tubeSegments = 6)
        {
            return SaveMesh("Arc_" + key + "_" + F(ringRadius) + "_" + F(tubeRadius) + "_" + F(fromDeg) + "_" + F(toDeg), () =>
            {
                var verts = new List<Vector3>();
                var uvs = new List<Vector2>();
                var tris = new List<int>();
                for (int i = 0; i <= steps; i++)
                {
                    float t = i / (float)steps;
                    float a = Mathf.Lerp(fromDeg, toDeg, t) * Mathf.Deg2Rad;
                    var centre = new Vector3(Mathf.Sin(a) * ringRadius, Mathf.Cos(a) * ringRadius, 0f);
                    var outward = new Vector3(Mathf.Sin(a), Mathf.Cos(a), 0f);
                    float r = tubeRadius * Mathf.Lerp(1f, 0.25f, t * t);
                    for (int s = 0; s <= tubeSegments; s++)
                    {
                        float b = s / (float)tubeSegments * Mathf.PI * 2f;
                        verts.Add(centre + (outward * Mathf.Cos(b) + Vector3.forward * Mathf.Sin(b)) * r);
                        uvs.Add(new Vector2(s / (float)tubeSegments, t * 2f));
                    }
                }
                for (int i = 0; i < steps; i++)
                {
                    for (int s = 0; s < tubeSegments; s++)
                    {
                        int a = i * (tubeSegments + 1) + s, b = a + 1, c = a + tubeSegments + 1, e = c + 1;
                        tris.Add(a); tris.Add(b); tris.Add(c);
                        tris.Add(b); tris.Add(e); tris.Add(c);
                    }
                }
                return SmoothMesh(verts, uvs, tris, tubeSegments + 1, steps + 1, true);
            });
        }

        /// <summary>
        /// Builds a mesh from a grid of vertices with smooth normals. The first and last column are the
        /// same points (the UV seam), so their normals are averaged to hide it. Winding is fixed up so the
        /// surface faces outward from the mesh's centre.
        /// </summary>
        static Mesh SmoothMesh(List<Vector3> verts, List<Vector2> uvs, List<int> tris, int columns, int rows, bool closeSeam)
        {
            // Make sure triangles face away from the centroid.
            Vector3 centre = Vector3.zero;
            foreach (var v in verts) centre += v;
            centre /= Mathf.Max(1, verts.Count);
            float facing = 0f;
            for (int i = 0; i + 2 < tris.Count; i += 3)
            {
                Vector3 a = verts[tris[i]], b = verts[tris[i + 1]], c = verts[tris[i + 2]];
                Vector3 n = Vector3.Cross(b - a, c - a);
                facing += Vector3.Dot(n, (a + b + c) / 3f - centre);
            }
            if (facing < 0f)
            {
                for (int i = 0; i + 2 < tris.Count; i += 3)
                {
                    int t = tris[i + 1];
                    tris[i + 1] = tris[i + 2];
                    tris[i + 2] = t;
                }
            }

            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            if (closeSeam && columns > 1)
            {
                var normals = mesh.normals;
                for (int row = 0; row < rows; row++)
                {
                    int first = row * columns, last = first + columns - 1;
                    if (last >= normals.Length) break;
                    Vector3 n = (normals[first] + normals[last]).normalized;
                    normals[first] = n;
                    normals[last] = n;
                }
                mesh.normals = normals;
            }
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ------------------------------------------------------------------ skin

        // A smooth, tileable, irregular field in -1..1: waves in many directions with whole-number
        // frequencies, so it wraps exactly and has no visible grain or direction.
        static readonly int[,] s_FleshWaves =
        {
            { 1, 2 }, { 2, -1 }, { 3, 1 }, { 1, -3 }, { 4, 3 }, { 5, -2 }, { 2, 5 }, { 6, 1 }, { 3, -5 }, { 7, 4 }, { 1, 7 }, { 8, -3 },
        };
        static readonly float[] s_FleshPhase = { 0.4f, 2.1f, 4.7f, 1.3f, 5.6f, 3.2f, 0.9f, 2.8f, 4.1f, 6.0f, 1.9f, 3.7f };

        static float FleshField(float u, float v, int first, int count)
        {
            float sum = 0f, weight = 0f;
            for (int i = first; i < first + count; i++)
            {
                int k = i % s_FleshPhase.Length;
                float w = 1f / (1f + (i - first) * 0.35f);
                sum += w * Mathf.Sin(6.2831853f * (s_FleshWaves[k, 0] * u + s_FleshWaves[k, 1] * v) + s_FleshPhase[k]);
                weight += w;
            }
            return sum / weight;
        }

        /// <summary>0 away from a vein, 1 on it. Veins are the contour lines of a smooth field, so they branch and wander.</summary>
        static float FleshVein(float u, float v)
        {
            float a = Mathf.Abs(FleshField(u, v, 0, 5));
            float b = Mathf.Abs(FleshField(u + 0.37f, v + 0.61f, 3, 5));
            float line = Mathf.Min(a, b * 1.4f);
            return Mathf.Clamp01(1f - line / 0.045f);
        }

        static float FleshHeight(int x, int y, int n)
        {
            float u = x / (float)n, v = y / (float)n;
            float lumps = FleshField(u, v, 0, 6);                       // broad swellings
            float wrinkles = FleshField(u * 3f, v * 3f, 4, 6);          // finer creases
            float pores = Mathf.Sin(u * 6.2831853f * 41f) * Mathf.Sin(v * 6.2831853f * 37f);
            return 0.5f + lumps * 0.22f + wrinkles * 0.1f + pores * 0.035f + FleshVein(u, v) * 0.16f;
        }

        static void PaintFlesh(Color32[] px, int n, System.Random rng)
        {
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float u = x / (float)n, v = y / (float)n;
                    float mottle = FleshField(u, v, 2, 6);               // slow blotches of lighter and darker skin
                    float bruise = Mathf.Clamp01(FleshField(u + 0.2f, v + 0.5f, 5, 5) * 1.6f - 0.55f);
                    float vein = FleshVein(u, v);
                    float grain = (float)(rng.NextDouble() - 0.5) * 0.05f;
                    // Mostly an even, slightly blotchy tone so each creature's own colour shows through.
                    float value = Mathf.Clamp01(0.66f + mottle * 0.1f + grain - bruise * 0.16f);
                    float r = value - vein * 0.1f + bruise * 0.03f;
                    float g = value - vein * 0.3f - bruise * 0.06f;
                    float b = value - vein * 0.26f - bruise * 0.02f;
                    px[y * n + x] = new Color32(B(r), B(g), B(b), 255);
                }
            }
        }

        // ------------------------------------------------------------------ the visor

        /// <summary>
        /// Cracks in the visor glass: a star of jagged lines from an impact point, branching as they go.
        /// Everything else is transparent. Each stage starts somewhere else and is bigger than the last.
        /// </summary>
        static void PaintCracks(Color32[] px, int n, int stage)
        {
            var rng = new System.Random(900 + stage * 17);
            Vector2[] impacts = { new Vector2(0.34f, 0.4f), new Vector2(0.66f, 0.58f), new Vector2(0.46f, 0.68f), new Vector2(0.56f, 0.36f) };
            Vector2 centre = impacts[(stage - 1) % impacts.Length] * n;

            void Dot(float x, float y, float radius, byte alpha)
            {
                int r = Mathf.CeilToInt(radius);
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int ix = Mathf.RoundToInt(x) + dx, iy = Mathf.RoundToInt(y) + dy;
                        if (ix < 0 || iy < 0 || ix >= n || iy >= n) continue;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        if (d > radius) continue;
                        byte a = (byte)(alpha * Mathf.Clamp01(1.2f - d / Mathf.Max(0.5f, radius)));
                        int idx = iy * n + ix;
                        if (a > px[idx].a) px[idx] = new Color32(235, 245, 255, a);
                    }
                }
            }

            void Line(Vector2 from, float angle, float length, float width, int depth)
            {
                Vector2 p = from;
                float travelled = 0f;
                while (travelled < length)
                {
                    angle += ((float)rng.NextDouble() - 0.5f) * 0.5f;
                    float step = 3f + (float)rng.NextDouble() * 7f;
                    Vector2 q = p + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * step;
                    for (float t = 0f; t <= 1f; t += 0.34f / step)
                        Dot(Mathf.Lerp(p.x, q.x, t), Mathf.Lerp(p.y, q.y, t), width * (1f - 0.6f * travelled / length), (byte)(230 - depth * 45));
                    p = q;
                    travelled += step;
                    if (depth < 2 && rng.NextDouble() < 0.07)
                        Line(p, angle + ((float)rng.NextDouble() < 0.5f ? 0.7f : -0.7f), (length - travelled) * 0.6f, width * 0.65f, depth + 1);
                }
            }

            int spokes = 6 + stage * 2;
            for (int i = 0; i < spokes; i++)
            {
                float angle = i * Mathf.PI * 2f / spokes + ((float)rng.NextDouble() - 0.5f) * 0.5f;
                Line(centre, angle, n * (0.16f + 0.09f * stage) * (0.6f + 0.6f * (float)rng.NextDouble()), 1.7f + stage * 0.25f, 0);
            }
            // Concentric shatter rings close to the impact.
            for (int ring = 1; ring <= 2 + stage / 2; ring++)
            {
                float radius = n * 0.028f * ring;
                float start = (float)rng.NextDouble() * 6.28f;
                for (float a = 0f; a < 6.28f; a += 0.02f)
                {
                    if (Mathf.Repeat(a * 3f + start, 2.2f) < 0.7f) continue; // broken arcs
                    Dot(centre.x + Mathf.Cos(a + start) * radius, centre.y + Mathf.Sin(a + start) * radius, 1.1f, 170);
                }
            }
            Dot(centre.x, centre.y, 5f + stage, 255);
        }

        // ------------------------------------------------------------------ the glimpse

        /// <summary>A tall thin shape with two pinprick eyes: what the player sometimes sees standing at the end of a corridor.</summary>
        static GameObject BuildApparition(Transform parent)
        {
            var root = Empty(parent, "Apparition", Vector3.zero, null, 0);
            Transform t = root.transform;
            var dark = M("Shadow");
            Part(t, "Torso", Blob("AppTorso", new Vector3(0.17f, 0.42f, 0.12f), 0.03f, 71), new Vector3(0, 1.38f, 0), dark, new Vector3(6, 0, 0));
            Part(t, "Hips", Blob("AppHips", new Vector3(0.15f, 0.16f, 0.11f), 0.02f, 72), new Vector3(0, 0.98f, 0.01f), dark);
            Part(t, "Neck", Muscle("AppNeck", 0.26f, 0.045f, 0.04f, 0.035f), new Vector3(0, 1.88f, 0.03f), dark, new Vector3(14, 0, 0));
            Part(t, "Head", Blob("AppHead", new Vector3(0.1f, 0.15f, 0.11f), 0.012f, 73), new Vector3(0, 2.08f, 0.07f), dark, new Vector3(18, 0, 0));
            foreach (float side in new[] { -1f, 1f })
            {
                Part(t, "Upper Arm", Muscle("AppArmA", 0.62f, 0.05f, 0.045f, 0.032f), new Vector3(side * 0.25f, 1.42f, 0.02f), dark, new Vector3(4, 0, side * 9f));
                Part(t, "Forearm", Muscle("AppArmB", 0.7f, 0.034f, 0.03f, 0.018f), new Vector3(side * 0.31f, 0.8f, 0.06f), dark, new Vector3(-8, 0, side * 2f));
                for (int f = 0; f < 3; f++)
                    Part(t, "Finger", Cone(0.01f, 0f, 0.26f, 5), new Vector3(side * (0.3f + (f - 1) * 0.022f), 0.34f, 0.1f), dark, new Vector3(180 + (f - 1) * 5f, 0, 0));
                Part(t, "Thigh", Muscle("AppLegA", 0.55f, 0.07f, 0.06f, 0.04f), new Vector3(side * 0.09f, 0.66f, 0f), dark, new Vector3(-4, 0, side * 3f));
                Part(t, "Shin", Muscle("AppLegB", 0.5f, 0.04f, 0.035f, 0.02f), new Vector3(side * 0.1f, 0.2f, -0.02f), dark, new Vector3(5, 0, 0));
                Prim(t, "Eye", PrimitiveType.Sphere, new Vector3(side * 0.035f, 2.1f, 0.165f), Vector3.one * 0.022f, M("ApparitionEye"));
            }
            foreach (var collider in root.GetComponentsInChildren<Collider>()) UnityEngine.Object.DestroyImmediate(collider);
            root.SetActive(false);
            return root;
        }
    }
}

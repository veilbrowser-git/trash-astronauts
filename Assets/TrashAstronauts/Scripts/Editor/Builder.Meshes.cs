using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// Procedural meshes for props: chamfered boxes (soft edges catch the light instead of looking like
    /// raw cubes) and lathed shapes (cylinders with flanges, cones, tanks, rings). Meshes are saved as
    /// assets so prefabs can reference them.
    /// </summary>
    public static partial class Builder
    {
        public const string MeshDir = GeneratedDir + "/Meshes";
        static readonly Dictionary<string, Mesh> s_PropMeshes = new Dictionary<string, Mesh>();

        static string F(float v) => Mathf.RoundToInt(v * 1000f).ToString();

        static Mesh SaveMesh(string key, Func<Mesh> build)
        {
            if (s_PropMeshes.TryGetValue(key, out var cached) && cached != null) return cached;
            EnsureFolder(MeshDir);
            string path = MeshDir + "/" + key + ".asset";
            var built = build();
            built.name = key;
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                // Overwrite in place so prefab references (and the asset GUID) stay valid.
                existing.Clear();
                existing.SetVertices(new List<Vector3>(built.vertices));
                existing.SetNormals(new List<Vector3>(built.normals));
                existing.SetUVs(0, new List<Vector2>(built.uv));
                existing.subMeshCount = Mathf.Max(1, built.subMeshCount);
                for (int sub = 0; sub < built.subMeshCount; sub++) existing.SetTriangles(built.GetTriangles(sub), sub);
                existing.RecalculateBounds();
                EditorUtility.SetDirty(existing);
                UnityEngine.Object.DestroyImmediate(built);
                built = existing;
            }
            else
            {
                AssetDatabase.CreateAsset(built, path);
            }
            s_PropMeshes[key] = built;
            return built;
        }

        sealed class MeshBuilder
        {
            public readonly List<Vector3> verts = new List<Vector3>();
            public readonly List<Vector3> normals = new List<Vector3>();
            public readonly List<Vector2> uvs = new List<Vector2>();
            public readonly List<int> tris = new List<int>();

            /// <summary>Adds a flat convex polygon of a convex shape centred on the origin, facing outward.</summary>
            public void Poly(params Vector3[] p)
            {
                Vector3 centroid = Vector3.zero;
                foreach (var v in p) centroid += v;
                centroid /= p.Length;
                Vector3 n = Vector3.Cross(p[1] - p[0], p[2] - p[0]).normalized;
                if (Vector3.Dot(n, centroid) < 0f)
                {
                    Array.Reverse(p);
                    n = -n;
                }
                int start = verts.Count;
                Vector3 an = new Vector3(Mathf.Abs(n.x), Mathf.Abs(n.y), Mathf.Abs(n.z));
                foreach (var v in p)
                {
                    verts.Add(v);
                    normals.Add(n);
                    uvs.Add(an.y >= an.x && an.y >= an.z ? new Vector2(v.x, v.z)
                        : an.x >= an.z ? new Vector2(v.z, v.y) : new Vector2(v.x, v.y));
                }
                for (int i = 1; i + 1 < p.Length; i++)
                {
                    tris.Add(start);
                    tris.Add(start + i);
                    tris.Add(start + i + 1);
                }
            }

            public Mesh ToMesh()
            {
                var mesh = new Mesh();
                mesh.SetVertices(verts);
                mesh.SetNormals(normals);
                mesh.SetUVs(0, uvs);
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        /// <summary>A box with every edge and corner cut off by <paramref name="bevel"/>.</summary>
        static Mesh ChamferBox(Vector3 size, float bevel)
        {
            string key = "Chamfer_" + F(size.x) + "_" + F(size.y) + "_" + F(size.z) + "_" + F(bevel);
            return SaveMesh(key, () =>
            {
                var mb = new MeshBuilder();
                float[] h = { size.x * 0.5f, size.y * 0.5f, size.z * 0.5f };
                float b = Mathf.Min(bevel, Mathf.Min(h[0], Mathf.Min(h[1], h[2])) * 0.9f);
                Vector3 V(int a, float va, int bx, float vb, int c, float vc)
                {
                    var v = new float[3];
                    v[a] = va; v[bx] = vb; v[c] = vc;
                    return new Vector3(v[0], v[1], v[2]);
                }

                for (int a = 0; a < 3; a++)
                {
                    int bx = (a + 1) % 3, c = (a + 2) % 3;
                    foreach (float s in new[] { -1f, 1f })
                    {
                        // face
                        mb.Poly(V(a, s * h[a], bx, -(h[bx] - b), c, -(h[c] - b)), V(a, s * h[a], bx, (h[bx] - b), c, -(h[c] - b)),
                                V(a, s * h[a], bx, (h[bx] - b), c, (h[c] - b)), V(a, s * h[a], bx, -(h[bx] - b), c, (h[c] - b)));
                        // edges between face a (sign s) and face bx (sign t)
                        foreach (float t in new[] { -1f, 1f })
                        {
                            mb.Poly(V(a, s * h[a], bx, t * (h[bx] - b), c, (h[c] - b)), V(a, s * h[a], bx, t * (h[bx] - b), c, -(h[c] - b)),
                                    V(a, s * (h[a] - b), bx, t * h[bx], c, -(h[c] - b)), V(a, s * (h[a] - b), bx, t * h[bx], c, (h[c] - b)));
                        }
                    }
                }
                foreach (float sx in new[] { -1f, 1f })
                    foreach (float sy in new[] { -1f, 1f })
                        foreach (float sz in new[] { -1f, 1f })
                            mb.Poly(new Vector3(sx * h[0], sy * (h[1] - b), sz * (h[2] - b)),
                                    new Vector3(sx * (h[0] - b), sy * h[1], sz * (h[2] - b)),
                                    new Vector3(sx * (h[0] - b), sy * (h[1] - b), sz * h[2]));
                return mb.ToMesh();
            });
        }

        /// <summary>
        /// Revolves a profile of (radius, height) points around the Y axis. Smooth around the axis, a
        /// crisp edge at each profile point. Ends with a non-zero radius are capped.
        /// </summary>
        static Mesh Lathe(string key, int segments, params Vector2[] profile)
        {
            return SaveMesh("Lathe_" + key + "_" + segments, () =>
            {
                var verts = new List<Vector3>();
                var normals = new List<Vector3>();
                var uvs = new List<Vector2>();
                var tris = new List<int>();

                for (int i = 0; i + 1 < profile.Length; i++)
                {
                    Vector2 p0 = profile[i], p1 = profile[i + 1];
                    Vector2 d = p1 - p0;
                    if (d.sqrMagnitude < 1e-10f) continue;
                    Vector2 n2 = new Vector2(d.y, -d.x).normalized; // (radial, up)
                    int start = verts.Count;
                    for (int s = 0; s <= segments; s++)
                    {
                        float ang = s / (float)segments * Mathf.PI * 2f;
                        float cx = Mathf.Cos(ang), cz = Mathf.Sin(ang);
                        verts.Add(new Vector3(p0.x * cx, p0.y, p0.x * cz));
                        verts.Add(new Vector3(p1.x * cx, p1.y, p1.x * cz));
                        var n = new Vector3(n2.x * cx, n2.y, n2.x * cz);
                        normals.Add(n);
                        normals.Add(n);
                        uvs.Add(new Vector2(s / (float)segments * 2f, p0.y * 4f));
                        uvs.Add(new Vector2(s / (float)segments * 2f, p1.y * 4f));
                    }
                    for (int s = 0; s < segments; s++)
                    {
                        int a = start + s * 2, b = a + 1, c = a + 2, e = a + 3;
                        Vector3 faceNormal = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
                        bool ok = Vector3.Dot(faceNormal, normals[a]) >= 0f;
                        if (ok) { tris.Add(a); tris.Add(b); tris.Add(c); tris.Add(c); tris.Add(b); tris.Add(e); }
                        else { tris.Add(a); tris.Add(c); tris.Add(b); tris.Add(c); tris.Add(e); tris.Add(b); }
                    }
                }

                void Cap(Vector2 p, float up)
                {
                    if (p.x <= 1e-5f) return;
                    int centre = verts.Count;
                    verts.Add(new Vector3(0, p.y, 0));
                    normals.Add(new Vector3(0, up, 0));
                    uvs.Add(new Vector2(0.5f, 0.5f));
                    for (int s = 0; s <= segments; s++)
                    {
                        float ang = s / (float)segments * Mathf.PI * 2f;
                        verts.Add(new Vector3(p.x * Mathf.Cos(ang), p.y, p.x * Mathf.Sin(ang)));
                        normals.Add(new Vector3(0, up, 0));
                        uvs.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(ang), 0.5f + 0.5f * Mathf.Sin(ang)));
                    }
                    for (int s = 0; s < segments; s++)
                    {
                        int a = centre, b = centre + 1 + s, c = centre + 2 + s;
                        Vector3 faceNormal = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
                        if (faceNormal.y * up >= 0f) { tris.Add(a); tris.Add(b); tris.Add(c); }
                        else { tris.Add(a); tris.Add(c); tris.Add(b); }
                    }
                }
                Cap(profile[0], -1f);
                Cap(profile[profile.Length - 1], 1f);

                var mesh = new Mesh();
                mesh.SetVertices(verts);
                mesh.SetNormals(normals);
                mesh.SetUVs(0, uvs);
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateBounds();
                return mesh;
            });
        }

        static Mesh Tube(float radius, float height, int segments = 14)
        {
            return Lathe("Tube_" + F(radius) + "_" + F(height), segments, new Vector2(radius, -height * 0.5f), new Vector2(radius, height * 0.5f));
        }

        static Mesh Cone(float bottomRadius, float topRadius, float height, int segments = 12)
        {
            return Lathe("Cone_" + F(bottomRadius) + "_" + F(topRadius) + "_" + F(height), segments,
                new Vector2(bottomRadius, -height * 0.5f), new Vector2(topRadius, height * 0.5f));
        }

        static Mesh Torus(float ringRadius, float tubeRadius, int segments = 20, int tubeSegments = 8)
        {
            var profile = new Vector2[tubeSegments + 1];
            for (int i = 0; i <= tubeSegments; i++)
            {
                float a = i / (float)tubeSegments * Mathf.PI * 2f;
                profile[i] = new Vector2(ringRadius + tubeRadius * Mathf.Cos(a), tubeRadius * Mathf.Sin(a));
            }
            return Lathe("Torus_" + F(ringRadius) + "_" + F(tubeRadius) + "_" + tubeSegments, segments, profile);
        }

        /// <summary>A mesh part in local space (no collider).</summary>
        static GameObject Part(Transform parent, string name, Mesh mesh, Vector3 localPos, Material mat, Vector3? localEuler = null, int layer = -1)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(localEuler ?? Vector3.zero);
            go.layer = layer >= 0 ? layer : (parent != null ? parent.gameObject.layer : 0);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            Cheap(r);
            return go;
        }

        /// <summary>A chamfered box part.</summary>
        static GameObject CBox(Transform parent, string name, Vector3 localPos, Vector3 size, Material mat, float bevel = -1f, Vector3? localEuler = null, int layer = -1)
        {
            if (bevel < 0f) bevel = Mathf.Min(size.x, Mathf.Min(size.y, size.z)) * 0.18f;
            return Part(parent, name, ChamferBox(size, bevel), localPos, mat, localEuler, layer);
        }

        // ------------------------------------------------------------------ normal maps

        /// <summary>Builds a tangent-space normal map from a tileable height function.</summary>
        static Texture2D MakeNormalMap(string name, int size, Func<int, int, float> height, float strength)
        {
            string path = TextureDir + "/" + name + ".png";
            var px = new Color32[size * size];
            float H(int x, int y) => height((x + size) % size, (y + size) % size);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (H(x + 1, y) - H(x - 1, y)) * strength;
                    float dy = (H(x, y + 1) - H(x, y - 1)) * strength;
                    var n = new Vector3(-dx, -dy, 1f).normalized;
                    px[y * size + x] = new Color32(B(n.x * 0.5f + 0.5f), B(n.y * 0.5f + 0.5f), B(n.z * 0.5f + 0.5f), 255);
                }
            }
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            if (importer != null && (importer.textureType != TextureImporterType.NormalMap || importer.wrapMode != TextureWrapMode.Repeat))
            {
                importer.textureType = TextureImporterType.NormalMap;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.mipmapEnabled = true;
                importer.maxTextureSize = size;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static float PlatesHeight(int x, int y, int n)
        {
            int cell = n / 2;
            int cx = x % cell, cy = y % cell;
            float h = 1f;
            int edge = Mathf.Min(Mathf.Min(cx, cell - cx), Mathf.Min(cy, cell - cy));
            if (edge < 5) h = edge / 5f;                                   // recessed seam
            int rx = Mathf.Min(cx, cell - cx), ry = Mathf.Min(cy, cell - cy);
            float rd = Mathf.Sqrt((rx - 18) * (rx - 18) + (ry - 18) * (ry - 18));
            if (rd < 6f) h += (1f - rd / 6f) * 0.8f;                       // raised rivet
            h += 0.06f * Mathf.PerlinNoise(x * 0.11f, y * 0.11f);           // dents
            return h;
        }

        static float FloorHeight(int x, int y, int n)
        {
            int cx = x % 64, cy = y % 64;
            int dx = Mathf.Abs(cx - 32), dy = Mathf.Abs(cy - 32);
            float h = (dx * 2 + dy < 22 || dx + dy * 2 < 22) ? 1f : 0f;     // raised tread
            if (x % 256 < 4 || y % 256 < 4) h -= 0.8f;                     // plate joints
            return h;
        }
    }
}

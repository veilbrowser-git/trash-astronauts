using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using TrashAstronauts.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// Idempotent project builder. Generates every asset the game needs (materials, textures, audio,
    /// data, prefabs, the scene) and wires them together, so nothing has to be assembled by hand.
    /// Run it from the menu (Trash Astronauts > Build Everything) or in batch mode.
    ///
    /// Safe to re-run: generated art, prefabs and the scene are rebuilt in place; balancing data assets
    /// (MissionConfig, salvage definitions) are created once and never overwritten.
    /// </summary>
    public static partial class Builder
    {
        public const string Root = "Assets/TrashAstronauts";
        public const string GeneratedDir = Root + "/Generated";
        public const string MaterialDir = GeneratedDir + "/Materials";
        public const string TextureDir = GeneratedDir + "/Textures";
        public const string AudioDir = GeneratedDir + "/Audio";
        public const string PrefabDir = Root + "/Prefabs";
        public const string DataDir = Root + "/Data";
        public const string SceneDir = Root + "/Scenes";
        public const string ScenePath = SceneDir + "/TrashAstronauts.unity";
        public const string InputPath = Root + "/Input/TrashAstronautsInput.inputactions";
        const string TmpEssentialsPackage = "Packages/com.unity.ugui/Package Resources/TMP Essential Resources.unitypackage";

        // ------------------------------------------------------------------ entry points

        [MenuItem("Trash Astronauts/1. Install Prerequisites (TextMeshPro, layers)", priority = 0)]
        public static void InstallPrerequisites()
        {
            ConfigureLayers();
            if (!HasTmpEssentials())
            {
                UnityEditor.AssetPackage.Package.Import(TmpEssentialsPackage, false);
                AssetDatabase.Refresh();
            }
            ImportSimulatorSample();
            AssetDatabase.SaveAssets();
            Debug.Log("[Builder] Prerequisites installed. TMP essentials present: " + HasTmpEssentials());
        }

        [MenuItem("Trash Astronauts/2. Build Everything (assets, prefabs, scene)", priority = 1)]
        public static void BuildAll()
        {
            if (!HasTmpEssentials())
            {
                InstallPrerequisites();
                if (!HasTmpEssentials())
                    throw new InvalidOperationException("TextMeshPro essential resources are missing. Run 'Trash Astronauts > 1. Install Prerequisites' and then build again.");
            }

            EnsureFolders();
            ConfigureProject();
            BuildMaterials();
            BuildAudio();
            BuildData();
            BuildPrefabs();
            BuildScene();
            ConfigureSimulator();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[Builder] Build complete. Open " + ScenePath + " and press Play.");
        }

        /// <summary>Batch entry: -executeMethod TrashAstronauts.EditorTools.Builder.BatchPrerequisites</summary>
        public static void BatchPrerequisites() => InstallPrerequisites();

        /// <summary>Batch entry: -executeMethod TrashAstronauts.EditorTools.Builder.BatchBuildAll</summary>
        public static void BatchBuildAll() => BuildAll();

        static bool HasTmpEssentials() => AssetDatabase.FindAssets("t:TMP_Settings").Length > 0;

        static void EnsureFolders()
        {
            foreach (string dir in new[] { Root, GeneratedDir, MaterialDir, TextureDir, AudioDir, MeshDir, PrefabDir, DataDir, DataDir + "/Salvage", SceneDir })
                EnsureFolder(dir);
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        // ------------------------------------------------------------------ materials

        static readonly Dictionary<string, Material> s_Mats = new Dictionary<string, Material>();
        static Material M(string name) => s_Mats[name];

        static Material Lit(string name, Color color, float smoothness = 0.25f, Texture2D texture = null, Color? emission = null, Texture2D normal = null)
        {
            string path = MaterialDir + "/" + name + ".mat";
            var shader = Shader.Find("Universal Render Pipeline/Simple Lit");
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }
            else m.shader = shader;

            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", smoothness);
            m.SetColor("_SpecColor", new Color(0.25f, 0.25f, 0.25f, 1f));
            if (texture != null) m.SetTexture("_BaseMap", texture);
            if (normal != null)
            {
                m.SetTexture("_BumpMap", normal);
                m.EnableKeyword("_NORMALMAP");
            }
            else
            {
                m.SetTexture("_BumpMap", null);
                m.DisableKeyword("_NORMALMAP");
            }
            // A tiny non-black emission keeps the emission keyword alive so hover highlights
            // (set per-renderer through a MaterialPropertyBlock) are visible.
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            m.SetColor("_EmissionColor", emission ?? new Color(0.004f, 0.004f, 0.004f));
            EditorUtility.SetDirty(m);
            s_Mats[name] = m;
            return m;
        }

        static Material Unlit(string name, Color color, bool transparent = false, Texture2D texture = null, string shaderName = "Universal Render Pipeline/Unlit")
        {
            string path = MaterialDir + "/" + name + ".mat";
            var shader = Shader.Find(shaderName);
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }
            else m.shader = shader;

            m.SetColor("_BaseColor", color);
            if (texture != null) m.SetTexture("_BaseMap", texture);
            if (transparent)
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 0f);
                m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0f);
                m.SetOverrideTag("RenderType", "Transparent");
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = (int)RenderQueue.Transparent;
            }
            EditorUtility.SetDirty(m);
            s_Mats[name] = m;
            return m;
        }

        static void BuildMaterials()
        {
            var plates = MakeTexture("Plates", 512, PaintPlates);
            var floor = MakeTexture("FloorGrate", 512, PaintFloor);
            var hazard = MakeTexture("Hazard", 256, PaintHazard);
            var stars = MakeTexture("Stars", 512, PaintStars);
            var platesN = MakeNormalMap("Plates_N", 512, (x, y) => PlatesHeight(x, y, 512), 2.2f);
            var floorN = MakeNormalMap("FloorGrate_N", 512, (x, y) => FloorHeight(x, y, 512), 1.6f);

            Lit("Wall", new Color(0.42f, 0.45f, 0.48f), 0.3f, plates, null, platesN);
            Lit("WallTug", new Color(0.55f, 0.52f, 0.46f), 0.3f, plates, null, platesN);
            Lit("Floor", new Color(0.3f, 0.32f, 0.34f), 0.3f, floor, null, floorN);
            Lit("Ceiling", new Color(0.22f, 0.24f, 0.26f), 0.15f, plates, null, platesN);
            Lit("Trim", new Color(0.16f, 0.17f, 0.19f), 0.35f);
            Lit("Container", new Color(0.45f, 0.22f, 0.14f), 0.25f, plates, null, platesN);
            Lit("ContainerBlue", new Color(0.16f, 0.3f, 0.42f), 0.25f, plates, null, platesN);
            Lit("Hazard", Color.white, 0.2f, hazard);
            Lit("Rail", new Color(1f, 0.8f, 0.05f), 0.4f, null, new Color(0.35f, 0.26f, 0.0f));
            Lit("Pipe", new Color(0.5f, 0.36f, 0.2f), 0.5f);
            Lit("Cable", new Color(0.06f, 0.06f, 0.07f), 0.1f);
            Lit("Door", new Color(0.5f, 0.5f, 0.52f), 0.45f, plates, null, platesN);
            Lit("Plate", new Color(0.62f, 0.6f, 0.52f), 0.4f, plates, null, platesN);
            Lit("CutMarker", new Color(1f, 0.55f, 0.05f), 0.3f, null, new Color(0.6f, 0.33f, 0.03f));
            Lit("Screen", new Color(0.02f, 0.03f, 0.04f), 0.7f);
            Lit("ButtonGreen", new Color(0.15f, 0.6f, 0.25f), 0.4f, null, new Color(0.02f, 0.1f, 0.03f));
            Lit("ButtonAmber", new Color(0.85f, 0.55f, 0.1f), 0.4f, null, new Color(0.1f, 0.06f, 0.01f));
            Lit("ButtonBlue", new Color(0.2f, 0.4f, 0.7f), 0.4f, null, new Color(0.02f, 0.05f, 0.1f));
            Lit("ButtonYellow", new Color(0.9f, 0.8f, 0.15f), 0.4f, null, new Color(0.1f, 0.09f, 0.01f));
            Lit("Glove", new Color(0.85f, 0.85f, 0.82f), 0.2f);
            Lit("GloveAccent", new Color(0.9f, 0.4f, 0.1f), 0.3f);
            Lit("ToolBody", new Color(0.2f, 0.21f, 0.24f), 0.5f);
            Lit("ToolAccent", new Color(0.9f, 0.45f, 0.1f), 0.4f);
            Lit("Lens", new Color(0.9f, 0.9f, 0.8f), 0.8f, null, new Color(1.5f, 1.4f, 1.1f));
            Lit("DroneBody", new Color(0.25f, 0.27f, 0.3f), 0.5f, plates, null, platesN);
            Lit("DroneEye", new Color(1f, 0.6f, 0.1f), 0.8f, null, new Color(2f, 1.2f, 0.2f));
            Lit("Scrap", new Color(0.5f, 0.5f, 0.48f), 0.35f, plates, null, platesN);
            Lit("ScrapTraining", new Color(0.95f, 0.85f, 0.2f), 0.3f, hazard);
            Lit("Copper", new Color(0.72f, 0.42f, 0.2f), 0.6f);
            Lit("Pcb", new Color(0.08f, 0.35f, 0.16f), 0.35f);
            Lit("Chip", new Color(0.05f, 0.05f, 0.06f), 0.5f);
            Lit("Led", new Color(0.2f, 1f, 0.4f), 0.6f, null, new Color(0.3f, 2f, 0.6f));
            Lit("ReactorShell", new Color(0.3f, 0.33f, 0.4f), 0.6f, plates, null, platesN);
            Lit("ReactorGlow", new Color(0.2f, 1f, 0.9f), 0.7f, null, new Color(0.3f, 2.2f, 2f));
            Lit("Chrome", new Color(0.82f, 0.84f, 0.88f), 0.85f);
            Lit("Toast", new Color(0.72f, 0.5f, 0.24f), 0.05f);
            Lit("Junk", new Color(0.4f, 0.3f, 0.25f), 0.2f, plates, null, platesN);
            Lit("GlowRed", new Color(1f, 0.15f, 0.1f), 0.3f, null, new Color(2.2f, 0.25f, 0.15f));
            Lit("GlowWhite", new Color(0.9f, 0.95f, 1f), 0.3f, null, new Color(1.6f, 1.8f, 2f));
            Lit("GlowWarm", new Color(1f, 0.85f, 0.6f), 0.3f, null, new Color(2f, 1.5f, 0.9f));
            Lit("GlowTeal", new Color(0.2f, 1f, 0.85f), 0.3f, null, new Color(0.3f, 1.8f, 1.5f));
            Lit("Carapace", new Color(0.07f, 0.075f, 0.085f), 0.65f, plates, null, platesN);
            Lit("Sinew", new Color(0.16f, 0.07f, 0.06f), 0.55f);
            Lit("Bone", new Color(0.62f, 0.58f, 0.5f), 0.35f);
            Lit("Claw", new Color(0.03f, 0.03f, 0.035f), 0.85f);
            Lit("CreatureEye", new Color(1f, 0.08f, 0.04f), 0.9f, null, new Color(3f, 0.25f, 0.12f));
            Lit("Rubber", new Color(0.05f, 0.05f, 0.055f), 0.15f);
            Lit("Steel", new Color(0.55f, 0.57f, 0.6f), 0.7f, plates, null, platesN);
            Lit("Brass", new Color(0.72f, 0.56f, 0.24f), 0.7f);
            Lit("PaintOrange", new Color(0.85f, 0.38f, 0.08f), 0.45f);
            Lit("PaintWhite", new Color(0.82f, 0.82f, 0.78f), 0.4f);
            Lit("ScreenGlow", new Color(0.1f, 0.5f, 0.45f), 0.8f, null, new Color(0.15f, 0.9f, 0.8f));
            Unlit("Fade", new Color(0f, 0f, 0f, 0f), true);
            Unlit("Line", Color.white, false, null, "Universal Render Pipeline/Particles/Unlit");
            Unlit("Flame", new Color(0.6f, 0.9f, 1f));
            Unlit("Reticle", new Color(0.3f, 0.95f, 1f));
            Unlit("Ghost", new Color(0.3f, 0.9f, 1f, 0.25f), true);
            Unlit("Stars", Color.white, false, stars);
            Unlit("Spark", new Color(1f, 0.75f, 0.3f), false, null, "Universal Render Pipeline/Particles/Unlit");
            AssetDatabase.SaveAssets();
        }

        // ------------------------------------------------------------------ procedural textures

        static Texture2D MakeTexture(string name, int size, Action<Color32[], int, System.Random> paint)
        {
            string path = TextureDir + "/" + name + ".png";
            var pixels = new Color32[size * size];
            paint(pixels, size, new System.Random(name.GetHashCode() & 0x7fffffff));
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.SetPixels32(pixels);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            if (importer != null && (importer.wrapMode != TextureWrapMode.Repeat || !importer.mipmapEnabled || importer.maxTextureSize != size))
            {
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.mipmapEnabled = true;
                importer.maxTextureSize = size;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static byte B(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);

        static void PaintPlates(Color32[] px, int n, System.Random rng)
        {
            int cell = n / 2;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float v = 0.78f + (float)(rng.NextDouble() - 0.5) * 0.07f;
                    // vertical grime streaks
                    v -= 0.06f * Mathf.PerlinNoise(x * 0.09f, y * 0.006f);
                    v -= 0.05f * Mathf.PerlinNoise(x * 0.02f + 7f, y * 0.02f + 3f);
                    int cx = x % cell, cy = y % cell;
                    if (cx < 3 || cy < 3) v *= 0.45f;                         // seam
                    else if (cx < 5 || cy < 5) v *= 1.12f;                    // bevel highlight
                    // rivets in the corners of each plate
                    int rx = Mathf.Min(cx, cell - cx), ry = Mathf.Min(cy, cell - cy);
                    if ((rx - 18) * (rx - 18) + (ry - 18) * (ry - 18) < 20) v *= 0.6f;
                    px[y * n + x] = new Color32(B(v), B(v), B(v), 255);
                }
            }
        }

        static void PaintFloor(Color32[] px, int n, System.Random rng)
        {
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float v = 0.7f + (float)(rng.NextDouble() - 0.5) * 0.06f;
                    int cx = x % 64, cy = y % 64;
                    // diamond tread pattern
                    int dx = Mathf.Abs(cx - 32), dy = Mathf.Abs(cy - 32);
                    if (dx * 2 + dy < 22 || dx + dy * 2 < 22) v *= 1.18f;
                    if (x % 256 < 3 || y % 256 < 3) v *= 0.4f;
                    v -= 0.08f * Mathf.PerlinNoise(x * 0.03f, y * 0.03f);
                    px[y * n + x] = new Color32(B(v), B(v), B(v), 255);
                }
            }
        }

        static void PaintHazard(Color32[] px, int n, System.Random rng)
        {
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    bool yellow = ((x + y) / 32) % 2 == 0;
                    float scuff = 1f - 0.25f * Mathf.PerlinNoise(x * 0.07f, y * 0.07f) - (float)rng.NextDouble() * 0.05f;
                    Color c = yellow ? new Color(0.95f, 0.75f, 0.05f) : new Color(0.07f, 0.07f, 0.07f);
                    px[y * n + x] = new Color32(B(c.r * scuff), B(c.g * scuff), B(c.b * scuff), 255);
                }
            }
        }

        static void PaintStars(Color32[] px, int n, System.Random rng)
        {
            for (int i = 0; i < px.Length; i++) px[i] = new Color32(1, 1, 3, 255);
            for (int i = 0; i < 420; i++)
            {
                int x = rng.Next(n), y = rng.Next(n);
                float b = 0.35f + (float)rng.NextDouble() * 0.65f;
                px[y * n + x] = new Color32(B(b), B(b), B(Mathf.Min(1f, b * 1.1f)), 255);
                if (rng.NextDouble() < 0.12)
                {
                    px[y * n + (x + 1) % n] = new Color32(B(b * 0.6f), B(b * 0.6f), B(b * 0.7f), 255);
                    px[((y + 1) % n) * n + x] = new Color32(B(b * 0.6f), B(b * 0.6f), B(b * 0.7f), 255);
                }
            }
        }

        // ------------------------------------------------------------------ geometry helpers

        static readonly Dictionary<string, Mesh> s_BoxMeshes = new Dictionary<string, Mesh>();

        /// <summary>Box mesh whose UVs are scaled by its real size, so tiled textures never stretch.</summary>
        static Mesh BoxMesh(Vector3 size, float uvPerMeter = 0.5f)
        {
            string key = size.x.ToString("F3") + "_" + size.y.ToString("F3") + "_" + size.z.ToString("F3");
            if (s_BoxMeshes.TryGetValue(key, out var cached) && cached != null) return cached;

            Vector3 h = size * 0.5f;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var normals = new List<Vector3>();
            var tris = new List<int>();

            void Face(Vector3 normal, Vector3 right, Vector3 up, float w, float ht, float depth)
            {
                int start = verts.Count;
                Vector3 c = normal * depth;
                verts.Add(c - right * (w / 2) - up * (ht / 2));
                verts.Add(c - right * (w / 2) + up * (ht / 2));
                verts.Add(c + right * (w / 2) + up * (ht / 2));
                verts.Add(c + right * (w / 2) - up * (ht / 2));
                uvs.Add(new Vector2(0, 0));
                uvs.Add(new Vector2(0, ht * uvPerMeter));
                uvs.Add(new Vector2(w * uvPerMeter, ht * uvPerMeter));
                uvs.Add(new Vector2(w * uvPerMeter, 0));
                for (int i = 0; i < 4; i++) normals.Add(normal);
                // Winding chosen so the face is visible from outside (along +normal).
                if (Vector3.Dot(Vector3.Cross(up, right), normal) > 0f)
                    tris.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
                else
                    tris.AddRange(new[] { start, start + 2, start + 1, start, start + 3, start + 2 });
            }

            Face(Vector3.forward, Vector3.right, Vector3.up, size.x, size.y, h.z);
            Face(Vector3.back, Vector3.right, Vector3.up, size.x, size.y, h.z);
            Face(Vector3.right, Vector3.forward, Vector3.up, size.z, size.y, h.x);
            Face(Vector3.left, Vector3.forward, Vector3.up, size.z, size.y, h.x);
            Face(Vector3.up, Vector3.right, Vector3.forward, size.x, size.z, h.y);
            Face(Vector3.down, Vector3.right, Vector3.forward, size.x, size.z, h.y);

            var mesh = new Mesh { name = "Box_" + key };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            s_BoxMeshes[key] = mesh;
            return mesh;
        }

        static void Cheap(Renderer r)
        {
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        /// <summary>Static level geometry: a world-UV box with an optional box collider.</summary>
        static GameObject Box(Transform parent, string name, Vector3 center, Vector3 size, Material mat,
            int layer = GameLayers.Environment, bool collider = true, bool isStatic = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = center;
            go.layer = layer;
            go.AddComponent<MeshFilter>().sharedMesh = BoxMesh(size);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            Cheap(r);
            if (collider) go.AddComponent<BoxCollider>().size = size;
            if (isStatic) go.isStatic = true;
            return go;
        }

        /// <summary>A primitive used for props and prefab parts (local space, optional collider).</summary>
        static GameObject Prim(Transform parent, string name, PrimitiveType type, Vector3 localPos, Vector3 localScale,
            Material mat, bool keepCollider = false, Vector3? localEuler = null, int layer = -1)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(localEuler ?? Vector3.zero);
            go.transform.localScale = localScale;
            go.layer = layer >= 0 ? layer : (parent != null ? parent.gameObject.layer : 0);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            Cheap(r);
            if (!keepCollider) UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        static GameObject Empty(Transform parent, string name, Vector3 localPos, Quaternion? localRot = null, int layer = -1)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot ?? Quaternion.identity;
            go.layer = layer >= 0 ? layer : (parent != null ? parent.gameObject.layer : 0);
            return go;
        }

        /// <summary>Rotation for text or a button mounted on a surface whose outward normal is given.</summary>
        static Quaternion Facing(Vector3 outwardNormal) => Quaternion.LookRotation(-outwardNormal, Vector3.up);

        /// <summary>World-space TextMeshPro label. Readable when looking along its +Z.</summary>
        static TextMeshPro Text(Transform parent, string name, string content, Vector3 localPos, Quaternion localRot,
            float fontSize, Vector2 size, Color color, TextAlignmentOptions align = TextAlignmentOptions.Center)
        {
            // TextMeshPro swaps in a RectTransform whose x/y do not survive being saved under a plain
            // Transform, so the label sits at the origin of a normal holder object that carries the pose.
            var holder = new GameObject(name);
            holder.transform.SetParent(parent, false);
            holder.layer = parent != null ? parent.gameObject.layer : 0;
            holder.transform.localPosition = localPos;
            holder.transform.localRotation = localRot;

            var go = new GameObject("Text");
            go.transform.SetParent(holder.transform, false);
            go.layer = holder.layer;
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.rectTransform.sizeDelta = size;
            tmp.text = content;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = align;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.richText = false;
            var r = go.GetComponent<MeshRenderer>();
            if (r != null) Cheap(r);
            return tmp;
        }

        /// <summary>
        /// A physical push button. Its +Z points into the surface it is mounted on (same convention as
        /// text), so pressing pushes the cap along +Z.
        /// </summary>
        static PushButton Button(Transform parent, string name, Vector3 localPos, Quaternion localRot, Vector2 size,
            string label, Material capMat, float fontSize = 0.32f)
        {
            var go = Empty(parent, name, localPos, localRot, GameLayers.Button);
            Prim(go.transform, "Base", PrimitiveType.Cube, new Vector3(0, 0, 0.008f), new Vector3(size.x + 0.03f, size.y + 0.03f, 0.016f), M("Trim"));
            var cap = Prim(go.transform, "Cap", PrimitiveType.Cube, new Vector3(0, 0, -0.012f), new Vector3(size.x, size.y, 0.024f), capMat);
            var col = go.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.center = new Vector3(0, 0, -0.02f);
            col.size = new Vector3(size.x, size.y, 0.06f);
            var text = Text(go.transform, "Label", label, new Vector3(0, 0, -0.027f), Quaternion.identity, fontSize, size, Color.white);
            text.fontStyle = FontStyles.Bold;

            var highlight = go.AddComponent<HoverHighlight>();
            highlight.renderers = new Renderer[] { cap.GetComponent<MeshRenderer>() };
            highlight.hoverEmission = new Color(0.5f, 0.5f, 0.45f);

            var button = go.AddComponent<PushButton>();
            button.label = text;
            button.cap = cap.transform;
            button.capRenderer = cap.GetComponent<MeshRenderer>();
            button.highlight = highlight;
            return button;
        }

        static Light PointLight(Transform parent, string name, Vector3 position, Color color, float intensity, float range)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.shadows = LightShadows.None;
            return light;
        }

        // ------------------------------------------------------------------ input references

        static Dictionary<string, InputActionReference> s_ActionRefs;

        static InputActionReference Ref(string mapAndAction)
        {
            if (s_ActionRefs == null)
            {
                s_ActionRefs = new Dictionary<string, InputActionReference>();
                foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(InputPath))
                {
                    if (obj is InputActionReference reference && reference.action != null)
                        s_ActionRefs[reference.action.actionMap.name + "/" + reference.action.name] = reference;
                }
            }
            if (!s_ActionRefs.TryGetValue(mapAndAction, out var result))
                throw new InvalidOperationException("Input action not found: " + mapAndAction + " in " + InputPath);
            return result;
        }

        static GameObject SavePrefab(GameObject temp, string name)
        {
            string path = PrefabDir + "/" + name + ".prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(temp, path);
            UnityEngine.Object.DestroyImmediate(temp);
            return prefab;
        }

        static T LoadOrCreate<T>(string path, out bool created) where T : ScriptableObject
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            created = asset == null;
            if (created)
            {
                asset = ScriptableObject.CreateInstance<T>();
                AssetDatabase.CreateAsset(asset, path);
            }
            return asset;
        }
    }
}

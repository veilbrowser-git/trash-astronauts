using System;
using System.Collections.Generic;
using System.IO;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEditor;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// The player's EVA gloves. Loads the model ModelSource/Player/build_hands.py makes in Blender (the same
    /// file format as the suit, plus a rest rotation per part) and hangs it under a hand's "Glove" object as a
    /// hierarchy of rigid finger segments, each pivoting at its knuckle, for <see cref="GloveVisual"/> to curl.
    /// Only the right glove is modelled: the left is the same model under a holder scaled -1 in x, which Unity
    /// draws with the winding flipped, so it is a true left hand and not inside out.
    /// </summary>
    public static partial class Builder
    {
        const string GloveSourceDir = "ModelSource/Player";
        const string GloveModelId = "glove";

        /// <summary>The segments GloveVisual drives, in its order: index, middle, ring, little (three each), then the thumb.</summary>
        public static readonly string[] GloveFingerParts =
        {
            "index_1", "index_2", "index_3", "middle_1", "middle_2", "middle_3",
            "ring_1", "ring_2", "ring_3", "little_1", "little_2", "little_3",
        };
        public static readonly string[] GloveThumbParts = { "thumb_1", "thumb_2", "thumb_3" };

        [Serializable]
        class GlovePartInfo
        {
            public string name, parent, material;
            public float[] pivot, rotation;
            public float length;
            public int vertexStart, vertexCount, indexStart, indexCount;
        }

        [Serializable]
        class GloveInfo
        {
            public string id;
            public float smoothness;
            public int vertexTotal, indexTotal, triangles;
            public GlovePartInfo[] parts;
        }

        sealed class GloveModel
        {
            public GlovePartInfo[] parts;
            public readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();
            public Material material;
        }

        static GloveModel s_GloveModel;
        static long s_GloveModelStamp;

        /// <summary>
        /// Builds the modelled glove under <paramref name="glove"/> and wires its joints into <paramref name="visual"/>.
        /// False (and nothing built) when the model files are missing or damaged, so the caller can build the stand-in.
        /// </summary>
        static bool BuildModelGlove(Transform glove, bool left, GloveVisual visual)
        {
            var model = LoadGloveModel();
            if (model == null) return false;
            foreach (string name in GloveFingerParts) if (!model.meshes.ContainsKey(name)) return false;
            foreach (string name in GloveThumbParts) if (!model.meshes.ContainsKey(name)) return false;
            if (!model.meshes.ContainsKey("palm")) return false;

            var holder = Empty(glove, "Model", Vector3.zero, null, GameLayers.Hand);
            holder.transform.localScale = new Vector3(left ? -1f : 1f, 1f, 1f);
            var made = new Dictionary<string, Transform>();
            foreach (var part in model.parts)
            {
                if (!model.meshes.ContainsKey(part.name)) continue;
                Transform parent = string.IsNullOrEmpty(part.parent) ? holder.transform : made[part.parent];
                Vector3 pivot = part.pivot != null && part.pivot.Length == 3 ? new Vector3(part.pivot[0], part.pivot[1], part.pivot[2]) : Vector3.zero;
                Quaternion rotation = part.rotation != null && part.rotation.Length == 4
                    ? new Quaternion(part.rotation[0], part.rotation[1], part.rotation[2], part.rotation[3]).normalized
                    : Quaternion.identity;
                var go = Empty(parent, part.name, pivot, rotation, GameLayers.Hand);
                go.AddComponent<MeshFilter>().sharedMesh = model.meshes[part.name];
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = model.material;
                Cheap(renderer);
                made[part.name] = go.transform;
            }

            visual.fingerJoints = new Transform[GloveFingerParts.Length];
            visual.fingerBase = new Quaternion[GloveFingerParts.Length];
            for (int i = 0; i < GloveFingerParts.Length; i++)
            {
                visual.fingerJoints[i] = made[GloveFingerParts[i]];
                visual.fingerBase[i] = visual.fingerJoints[i].localRotation;
            }
            visual.thumbJoints = new Transform[GloveThumbParts.Length];
            visual.thumbBase = new Quaternion[GloveThumbParts.Length];
            for (int i = 0; i < GloveThumbParts.Length; i++)
            {
                visual.thumbJoints[i] = made[GloveThumbParts[i]];
                visual.thumbBase[i] = visual.thumbJoints[i].localRotation;
            }
            visual.Apply(0f, 0f); // save the prefab in the relaxed rest pose
            return true;
        }

        /// <summary>Reads the Blender export and turns it into mesh, texture and material assets. Null if it is not there.</summary>
        static GloveModel LoadGloveModel()
        {
            string jsonPath = GloveSourceDir + "/" + GloveModelId + ".mesh.json";
            string binPath = GloveSourceDir + "/" + GloveModelId + ".mesh.bin";
            string atlasPath = GloveSourceDir + "/" + GloveModelId + "_atlas.png";
            if (!File.Exists(jsonPath) || !File.Exists(binPath) || !File.Exists(atlasPath))
            {
                Debug.LogWarning("[Builder] Glove model is missing; using the stand-in glove. Run Blender on " + GloveSourceDir + "/build_hands.py to make it.");
                s_GloveModel = null;
                return null;
            }

            long stamp = File.GetLastWriteTimeUtc(jsonPath).Ticks ^ File.GetLastWriteTimeUtc(binPath).Ticks ^ File.GetLastWriteTimeUtc(atlasPath).Ticks;
            bool changed = s_GloveModel == null || stamp != s_GloveModelStamp;
            if (!changed && GloveModelAlive(s_GloveModel)) return s_GloveModel;

            GloveInfo info;
            try { info = JsonUtility.FromJson<GloveInfo>(File.ReadAllText(jsonPath)); }
            catch (Exception) { info = null; }
            byte[] bytes = File.ReadAllBytes(binPath);
            if (info == null || info.parts == null || bytes.Length < info.vertexTotal * 32 + info.indexTotal * 4)
            {
                Debug.LogWarning("[Builder] Glove model is damaged (" + binPath + "); using the stand-in glove.");
                s_GloveModel = null;
                return null;
            }
            int v = info.vertexTotal;
            var positions = new float[v * 3];
            var normals = new float[v * 3];
            var uvs = new float[v * 2];
            var indices = new int[info.indexTotal];
            int offset = 0;
            Buffer.BlockCopy(bytes, offset, positions, 0, positions.Length * 4); offset += positions.Length * 4;
            Buffer.BlockCopy(bytes, offset, normals, 0, normals.Length * 4); offset += normals.Length * 4;
            Buffer.BlockCopy(bytes, offset, uvs, 0, uvs.Length * 4); offset += uvs.Length * 4;
            Buffer.BlockCopy(bytes, offset, indices, 0, indices.Length * 4);

            EnsureFolder(TextureDir);
            string texturePath = TextureDir + "/Glove_" + GloveModelId + ".png";
            File.Copy(atlasPath, texturePath, true);
            AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(texturePath);
            if (importer != null && (importer.wrapMode != TextureWrapMode.Clamp || !importer.mipmapEnabled || importer.maxTextureSize != 512 || importer.anisoLevel != 2))
            {
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.mipmapEnabled = true;
                importer.maxTextureSize = 512;
                importer.anisoLevel = 2;
                importer.SaveAndReimport();
            }
            var atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);

            var model = new GloveModel { parts = info.parts };
            model.material = Lit("GloveSuit", Color.white, info.smoothness, atlas);
            foreach (var part in info.parts)
            {
                if (part.vertexCount <= 0 || part.indexCount <= 0) continue;
                if (part.vertexStart + part.vertexCount > v || part.indexStart + part.indexCount > info.indexTotal) continue;
                var p = part;
                string key = "Glove_" + GloveModelId + "_" + part.name;
                if (changed) s_PropMeshes.Remove(key);
                model.meshes[part.name] = SaveMesh(key, () =>
                {
                    var verts = new List<Vector3>(p.vertexCount);
                    var norms = new List<Vector3>(p.vertexCount);
                    var uv = new List<Vector2>(p.vertexCount);
                    for (int i = 0; i < p.vertexCount; i++)
                    {
                        int k = (p.vertexStart + i) * 3;
                        verts.Add(new Vector3(positions[k], positions[k + 1], positions[k + 2]));
                        norms.Add(new Vector3(normals[k], normals[k + 1], normals[k + 2]));
                        int u = (p.vertexStart + i) * 2;
                        uv.Add(new Vector2(uvs[u], uvs[u + 1]));
                    }
                    var tris = new int[p.indexCount];
                    Array.Copy(indices, p.indexStart, tris, 0, p.indexCount);
                    var m = new Mesh();
                    m.SetVertices(verts);
                    m.SetNormals(norms);
                    m.SetUVs(0, uv);
                    m.SetTriangles(tris, 0);
                    m.RecalculateBounds();
                    return m;
                });
            }
            s_GloveModel = model;
            s_GloveModelStamp = stamp;
            return model;
        }

        static bool GloveModelAlive(GloveModel model)
        {
            if (model == null || model.material == null) return false;
            foreach (var mesh in model.meshes.Values) if (mesh == null) return false;
            return true;
        }
    }
}

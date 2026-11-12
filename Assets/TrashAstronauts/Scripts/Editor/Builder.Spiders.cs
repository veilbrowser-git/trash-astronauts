using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// Loads the spider models that ModelSource/Spiders/build_spiders.py makes in Blender (one species per
    /// creature) and assembles them into a hierarchy the game can animate: body, head, abdomen, jaw
    /// (chelicerae and fangs), two pedipalps, eight eyes, and eight legs each hinged at the body and the knee.
    /// </summary>
    public static partial class Builder
    {
        const string SpiderSourceDir = "ModelSource/Spiders";

        [Serializable]
        class SpiderPartInfo
        {
            public string name, parent, material;
            public float[] pivot;
            public int vertexStart, vertexCount, indexStart, indexCount;
        }

        [Serializable]
        class SpiderInfo
        {
            public string id;
            public float smoothness;
            public int vertexTotal, indexTotal, triangles, hairs;
            public SpiderPartInfo[] parts;
        }

        sealed class SpiderModel
        {
            public Transform head, abdomen, jaw;
            public readonly List<Transform> legs = new List<Transform>();
            public readonly List<Transform> lowerLegs = new List<Transform>();
            public readonly List<Transform> palps = new List<Transform>();
            public readonly List<GameObject> eyes = new List<GameObject>();
            public int triangles, hairs;
        }

        static SpiderModel LoadSpider(string id, Transform body)
        {
            string jsonPath = SpiderSourceDir + "/" + id + ".mesh.json";
            string binPath = SpiderSourceDir + "/" + id + ".mesh.bin";
            string atlasPath = SpiderSourceDir + "/" + id + "_atlas.png";
            if (!File.Exists(jsonPath) || !File.Exists(binPath) || !File.Exists(atlasPath))
                throw new FileNotFoundException("Spider model '" + id + "' is missing. Run Blender on " + SpiderSourceDir + "/build_spiders.py first.");

            var info = JsonUtility.FromJson<SpiderInfo>(File.ReadAllText(jsonPath));
            byte[] bytes = File.ReadAllBytes(binPath);
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

            // The colour atlas the whole spider shares.
            EnsureFolder(TextureDir);
            string texturePath = TextureDir + "/Spider_" + id + ".png";
            File.Copy(atlasPath, texturePath, true);
            AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(texturePath);
            if (importer != null && (importer.wrapMode != TextureWrapMode.Clamp || !importer.mipmapEnabled))
            {
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.mipmapEnabled = true;
                importer.SaveAndReimport();
            }
            var atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            var skin = Lit("Spider_" + id, Color.white, info.smoothness, atlas);

            var model = new SpiderModel { triangles = info.triangles, hairs = info.hairs };
            var byName = new Dictionary<string, Transform>();
            var pivots = new Dictionary<string, Vector3>();
            foreach (var part in info.parts)
            {
                var pivot = new Vector3(part.pivot[0], part.pivot[1], part.pivot[2]);
                pivots[part.name] = pivot;
                Transform t;
                if (string.IsNullOrEmpty(part.parent))
                {
                    t = body; // the model's root is the creature's animated body
                }
                else
                {
                    Transform parent = byName[part.parent];
                    t = Empty(parent, part.name, pivot - pivots[part.parent]).transform;
                }
                byName[part.name] = t;

                if (part.vertexCount > 0 && part.indexCount > 0)
                {
                    var p = part;
                    var mesh = SaveMesh("Spider_" + id + "_" + part.name, () =>
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
                    var visual = t == body ? Empty(body, "Body Mesh", Vector3.zero) : t.gameObject;
                    visual.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = visual.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = part.material == "eye" ? M("SpiderEye") : skin;
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                    if (part.material == "eye") model.eyes.Add(visual);
                }

                if (part.name == "Head") model.head = t;
                else if (part.name == "Abdomen") model.abdomen = t;
                else if (part.name == "Jaw") model.jaw = t;
                else if (part.name.StartsWith("LegLower_")) model.lowerLegs.Add(t);
                else if (part.name.StartsWith("Leg_")) model.legs.Add(t);
                else if (part.name.StartsWith("Palp_")) model.palps.Add(t);
            }
            return model;
        }
    }
}

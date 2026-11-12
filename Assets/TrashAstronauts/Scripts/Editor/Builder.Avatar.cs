using System;
using System.Collections.Generic;
using System.IO;
using TrashAstronauts.Player;
using UnityEditor;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// The player's space suit. Loads the model that ModelSource/Player/build_player.py makes in Blender
    /// (the same file format as the spiders) and assembles it under a <see cref="PlayerAvatar"/>, which poses
    /// it from a head and two hands. Used for the local player and, with the helmet shown, for remote crew.
    /// </summary>
    public static partial class Builder
    {
        const string AvatarSourceDir = "ModelSource/Player";
        const string AvatarModelId = "astronaut";

        [Serializable]
        class AvatarPartInfo
        {
            public string name, parent, material;
            public float[] pivot;
            public float length;
            public int vertexStart, vertexCount, indexStart, indexCount;
        }

        [Serializable]
        class AvatarRigInfo
        {
            public float[] headCentre, neck, shoulder, hips;
            public float upperArmLength, forearmLength;
        }

        [Serializable]
        class AvatarInfo
        {
            public string id;
            public float smoothness;
            public int vertexTotal, indexTotal, triangles;
            public AvatarRigInfo rig;
            public AvatarPartInfo[] parts;
        }

        sealed class AvatarModel
        {
            public AvatarRigInfo rig;
            public readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();
            public readonly Dictionary<string, string> materials = new Dictionary<string, string>();
            public Material suit, accent, visor;

            public bool Has(string part) => meshes.ContainsKey(part);
        }

        static AvatarModel s_AvatarModel;
        static long s_AvatarModelStamp;

        /// <summary>
        /// Builds a suit under <paramref name="parent"/> and returns its PlayerAvatar with every part assigned.
        /// The caller sets the three sources (head, leftHand, rightHand). Pass helmetVisible = false for the local
        /// player, whose own helmet would block the view. No colliders, no rigidbodies.
        /// </summary>
        static PlayerAvatar BuildAvatar(Transform parent, string name, bool helmetVisible)
        {
            var model = LoadAvatarModel();
            var root = Empty(parent, name, Vector3.zero);
            var avatar = root.AddComponent<PlayerAvatar>();
            var helmetRenderers = new List<Renderer>();
            var bodyRenderers = new List<Renderer>();
            var accentRenderers = new List<Renderer>();

            if (model != null && model.rig != null)
            {
                avatar.headCentreOffset = AvatarVector(model.rig.headCentre, avatar.headCentreOffset);
                avatar.neckDrop = model.rig.neck != null && model.rig.neck.Length == 3 ? -model.rig.neck[1] : avatar.neckDrop;
                avatar.shoulderOffset = AvatarVector(model.rig.shoulder, avatar.shoulderOffset);
                avatar.hipOffset = AvatarVector(model.rig.hips, avatar.hipOffset);
                if (model.rig.upperArmLength > 0.01f) avatar.upperArmLength = model.rig.upperArmLength;
                if (model.rig.forearmLength > 0.01f) avatar.forearmLength = model.rig.forearmLength;
            }

            // A standing reference pose, so the prefab looks right before the first frame poses it.
            Vector3 headCentre = new Vector3(0f, 1.6f, 0f) + avatar.headCentreOffset;
            Vector3 neck = headCentre + Vector3.down * avatar.neckDrop;
            var helmet = Empty(root.transform, "Helmet", headCentre);
            var torso = Empty(root.transform, "Torso", neck);
            var legs = Empty(root.transform, "Legs", neck + avatar.hipOffset);
            avatar.helmet = helmet.transform;
            avatar.torso = torso.transform;
            avatar.legs = legs.transform;

            bool modelled = model != null && model.Has("helmet") && model.Has("torso") && model.Has("upperarm") && model.Has("forearm") && model.Has("legs");
            if (modelled)
            {
                AvatarAttach(model, helmet, "helmet", 1f, helmetRenderers, accentRenderers, helmetRenderers);
                AvatarAttach(model, torso, "torso", 1f, bodyRenderers, accentRenderers, helmetRenderers);
                AvatarAttach(model, legs, "legs", 1f, bodyRenderers, accentRenderers, helmetRenderers);
            }
            else
            {
                BuildFallbackAvatarBody(helmet.transform, torso.transform, legs.transform, helmetRenderers, bodyRenderers, accentRenderers);
            }

            // Two arms from the one pair of segment meshes: the left is the right, mirrored.
            for (int i = 0; i < 2; i++)
            {
                bool left = i == 0;
                float side = left ? -1f : 1f;
                string label = left ? "Left" : "Right";
                Vector3 shoulder = neck + new Vector3(avatar.shoulderOffset.x * side, avatar.shoulderOffset.y, avatar.shoulderOffset.z);
                Quaternion down = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                var upper = Empty(root.transform, label + " Upper Arm", shoulder, down);
                var fore = Empty(root.transform, label + " Forearm", shoulder + Vector3.down * avatar.upperArmLength, down);
                upper.transform.localScale = new Vector3(1f, 1f, avatar.upperArmLength);
                fore.transform.localScale = new Vector3(1f, 1f, avatar.forearmLength);
                if (modelled)
                {
                    AvatarAttach(model, Empty(upper.transform, "Mesh", Vector3.zero), "upperarm", side, bodyRenderers, accentRenderers, helmetRenderers);
                    AvatarAttach(model, Empty(fore.transform, "Mesh", Vector3.zero), "forearm", side, bodyRenderers, accentRenderers, helmetRenderers);
                }
                else
                {
                    BuildFallbackAvatarSegment(upper.transform, 0.12f, bodyRenderers);
                    BuildFallbackAvatarSegment(fore.transform, 0.1f, bodyRenderers);
                }
                if (left)
                {
                    avatar.leftUpperArm = upper.transform;
                    avatar.leftForearm = fore.transform;
                }
                else
                {
                    avatar.rightUpperArm = upper.transform;
                    avatar.rightForearm = fore.transform;
                }
            }

            avatar.helmetRenderers = helmetRenderers.ToArray();
            avatar.bodyRenderers = bodyRenderers.ToArray();
            avatar.accentRenderers = accentRenderers.ToArray();
            avatar.SetHelmetVisible(helmetVisible);
            avatar.SetBodyVisible(true);
            avatar.SetTint(PlayerAvatar.CrewColors[0]);
            return avatar;
        }

        static Vector3 AvatarVector(float[] values, Vector3 fallback)
        {
            return values != null && values.Length == 3 ? new Vector3(values[0], values[1], values[2]) : fallback;
        }

        /// <summary>
        /// Puts a model part's mesh on <paramref name="holder"/> and hangs the part's children (visor, accent pieces,
        /// collar) under it. mirror = -1 flips the part left to right. Accent pieces are also added to the accent list.
        /// A collar (the neck ring the helmet locks into) is shown and hidden with the helmet, so it joins that group.
        /// </summary>
        static void AvatarAttach(AvatarModel model, GameObject holder, string part, float mirror, List<Renderer> group, List<Renderer> accents, List<Renderer> withHelmet)
        {
            holder.transform.localScale = new Vector3(mirror, 1f, 1f);
            AvatarRenderer(model, holder, part, group, accents);
            foreach (string suffix in new[] { "_visor", "_accent", "_collar" })
            {
                string child = part + suffix;
                if (!model.Has(child)) continue;
                string label = suffix == "_visor" ? "Visor" : suffix == "_accent" ? "Accent" : "Collar";
                AvatarRenderer(model, Empty(holder.transform, label, Vector3.zero), child, suffix == "_collar" ? withHelmet : group, accents);
            }
        }

        static void AvatarRenderer(AvatarModel model, GameObject go, string part, List<Renderer> group, List<Renderer> accents)
        {
            go.AddComponent<MeshFilter>().sharedMesh = model.meshes[part];
            var renderer = go.AddComponent<MeshRenderer>();
            string material = model.materials[part];
            renderer.sharedMaterial = material == "accent" ? model.accent : material == "visor" ? model.visor : model.suit;
            Cheap(renderer);
            group.Add(renderer);
            if (material == "accent") accents.Add(renderer);
        }

        /// <summary>Reads the Blender export and turns it into mesh, texture and material assets. Null if it is not there.</summary>
        static AvatarModel LoadAvatarModel()
        {
            string jsonPath = AvatarSourceDir + "/" + AvatarModelId + ".mesh.json";
            string binPath = AvatarSourceDir + "/" + AvatarModelId + ".mesh.bin";
            string atlasPath = AvatarSourceDir + "/" + AvatarModelId + "_atlas.png";
            if (!File.Exists(jsonPath) || !File.Exists(binPath) || !File.Exists(atlasPath))
            {
                Debug.LogWarning("[Builder] Player suit model is missing; using stand-in shapes. Run Blender on " + AvatarSourceDir + "/build_player.py to make it.");
                s_AvatarModel = null; // the stand-in strips the atlas off the suit materials: load afresh when the files are back
                return null;
            }

            long stamp = File.GetLastWriteTimeUtc(jsonPath).Ticks ^ File.GetLastWriteTimeUtc(binPath).Ticks ^ File.GetLastWriteTimeUtc(atlasPath).Ticks;
            bool changed = s_AvatarModel == null || stamp != s_AvatarModelStamp;
            if (!changed && AvatarModelAlive(s_AvatarModel)) return s_AvatarModel;

            var info = JsonUtility.FromJson<AvatarInfo>(File.ReadAllText(jsonPath));
            byte[] bytes = File.ReadAllBytes(binPath);
            int v = info.vertexTotal;
            if (info.parts == null || bytes.Length < v * 32 + info.indexTotal * 4)
            {
                Debug.LogWarning("[Builder] Player suit model is damaged (" + binPath + "); using stand-in shapes.");
                s_AvatarModel = null;
                return null;
            }
            var positions = new float[v * 3];
            var normals = new float[v * 3];
            var uvs = new float[v * 2];
            var indices = new int[info.indexTotal];
            int offset = 0;
            Buffer.BlockCopy(bytes, offset, positions, 0, positions.Length * 4); offset += positions.Length * 4;
            Buffer.BlockCopy(bytes, offset, normals, 0, normals.Length * 4); offset += normals.Length * 4;
            Buffer.BlockCopy(bytes, offset, uvs, 0, uvs.Length * 4); offset += uvs.Length * 4;
            Buffer.BlockCopy(bytes, offset, indices, 0, indices.Length * 4);

            // The one colour atlas every part of the suit shares.
            EnsureFolder(TextureDir);
            string texturePath = TextureDir + "/Avatar_" + AvatarModelId + ".png";
            File.Copy(atlasPath, texturePath, true);
            AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(texturePath);
            if (importer != null && (importer.wrapMode != TextureWrapMode.Clamp || !importer.mipmapEnabled || importer.maxTextureSize != 1024 || importer.anisoLevel != 4))
            {
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.mipmapEnabled = true;
                importer.maxTextureSize = 1024;
                importer.anisoLevel = 4;
                importer.SaveAndReimport();
            }
            var atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);

            var model = new AvatarModel { rig = info.rig };
            model.suit = Lit("AvatarSuit", Color.white, info.smoothness, atlas);
            // The crew-colour pieces share the atlas (their cloth is painted near white) and are tinted per player.
            model.accent = Lit("AvatarAccent", PlayerAvatar.CrewColors[0], info.smoothness, atlas);
            model.visor = AvatarVisorMaterial(atlas);

            foreach (var part in info.parts)
            {
                if (part.vertexCount <= 0 || part.indexCount <= 0) continue;
                var p = part;
                string key = "Avatar_" + AvatarModelId + "_" + part.name;
                if (changed) s_PropMeshes.Remove(key); // the model file is newer than the cached mesh
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
                model.materials[part.name] = string.IsNullOrEmpty(part.material) ? "suit" : part.material;
            }
            s_AvatarModel = model;
            s_AvatarModelStamp = stamp;
            return model;
        }

        /// <summary>False once any asset the cached model points at has been deleted (for example the Generated folder was cleared).</summary>
        static bool AvatarModelAlive(AvatarModel model)
        {
            if (model == null || model.suit == null || model.accent == null || model.visor == null) return false;
            foreach (var mesh in model.meshes.Values)
            {
                if (mesh == null) return false;
            }
            return true;
        }

        /// <summary>Dark and glossy: the only suit material with a specular highlight.</summary>
        static Material AvatarVisorMaterial(Texture2D atlas)
        {
            var visor = Lit("AvatarVisor", atlas != null ? Color.white : new Color(0.02f, 0.03f, 0.05f), 0.9f, atlas);
            visor.EnableKeyword("_SPECULAR_COLOR");
            visor.SetFloat("_SpecularHighlights", 1f);
            visor.SetColor("_SpecColor", new Color(0.75f, 0.8f, 0.9f, 0.9f));
            EditorUtility.SetDirty(visor);
            return visor;
        }

        // ------------------------------------------------------------------ stand-in shapes (model files missing)

        static void BuildFallbackAvatarBody(Transform helmet, Transform torso, Transform legs, List<Renderer> helmetRenderers, List<Renderer> bodyRenderers, List<Renderer> accentRenderers)
        {
            var suit = Lit("AvatarSuit", new Color(0.82f, 0.82f, 0.78f), 0.3f);
            suit.SetTexture("_BaseMap", null);
            var accent = Lit("AvatarAccent", PlayerAvatar.CrewColors[0], 0.3f);
            accent.SetTexture("_BaseMap", null);
            var visor = AvatarVisorMaterial(null);
            visor.SetTexture("_BaseMap", null);

            Renderer Add(Transform parent, string name, PrimitiveType type, Vector3 position, Vector3 scale, Material material, List<Renderer> group)
            {
                var renderer = Prim(parent, name, type, position, scale, material).GetComponent<MeshRenderer>();
                group.Add(renderer);
                if (material == accent) accentRenderers.Add(renderer);
                return renderer;
            }

            Add(helmet, "Shell", PrimitiveType.Sphere, Vector3.zero, new Vector3(0.33f, 0.33f, 0.34f), suit, helmetRenderers);
            Add(helmet, "Visor", PrimitiveType.Sphere, new Vector3(0f, 0.005f, 0.07f), new Vector3(0.25f, 0.2f, 0.22f), visor, helmetRenderers);
            Add(helmet, "Accent", PrimitiveType.Cube, new Vector3(0f, 0.155f, -0.03f), new Vector3(0.045f, 0.03f, 0.16f), accent, helmetRenderers);
            Add(torso, "Chest", PrimitiveType.Cube, new Vector3(0f, -0.2f, 0f), new Vector3(0.4f, 0.4f, 0.28f), suit, bodyRenderers);
            Add(torso, "Hips", PrimitiveType.Cube, new Vector3(0f, -0.5f, 0f), new Vector3(0.36f, 0.22f, 0.26f), suit, bodyRenderers);
            Add(torso, "Backpack", PrimitiveType.Cube, new Vector3(0f, -0.25f, -0.21f), new Vector3(0.33f, 0.5f, 0.17f), suit, bodyRenderers);
            Add(torso, "Accent", PrimitiveType.Cube, new Vector3(0f, -0.1f, 0.142f), new Vector3(0.3f, 0.05f, 0.01f), accent, bodyRenderers);
            foreach (float side in new[] { -1f, 1f })
            {
                Add(legs, side < 0f ? "Left Leg" : "Right Leg", PrimitiveType.Capsule, new Vector3(side * 0.1f, -0.45f, 0.04f), new Vector3(0.17f, 0.46f, 0.17f), suit, bodyRenderers);
            }
        }

        /// <summary>A unit-length cylinder along +Z from the segment's joint.</summary>
        static void BuildFallbackAvatarSegment(Transform segment, float thickness, List<Renderer> bodyRenderers)
        {
            var suit = M("AvatarSuit");
            var renderer = Prim(segment, "Mesh", PrimitiveType.Cylinder, new Vector3(0f, 0f, 0.5f), new Vector3(thickness, 0.5f, thickness), suit, false, new Vector3(90f, 0f, 0f)).GetComponent<MeshRenderer>();
            bodyRenderers.Add(renderer);
        }
    }
}

using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Mission;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TrashAstronauts.Tests
{
    /// <summary>Pins the environment art rules: detailed surfaces with normal maps, decoration that cannot block anything, and a per-wreck budget.</summary>
    public class EnvironmentArtTests
    {
        const string ScenePath = "Assets/TrashAstronauts/Scenes/TrashAstronauts.unity";
        const string MaterialDir = "Assets/TrashAstronauts/Generated/Materials/";
        const string DressingName = "Env Dressing";

        static readonly string[] SurfaceMaterials =
        {
            "Wall", "WallTug", "Floor", "Ceiling", "Door",
            "Wall_Hauler", "Floor_Hauler", "Ceiling_Hauler",
            "Wall_Barge", "Floor_Barge", "Ceiling_Barge",
            "Wall_Survey", "Floor_Survey", "Ceiling_Survey",
        };

        static WreckMap[] Maps()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            return scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<WreckMap>(true)).ToArray();
        }

        [Test]
        public void WallFloorCeilingAndDoorMaterials_HaveTexturesAndNormalMaps_WithinQuestLimits()
        {
            foreach (var name in SurfaceMaterials)
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(MaterialDir + name + ".mat");
                Assert.IsNotNull(m, name);
                var albedo = m.GetTexture("_BaseMap") as Texture2D;
                var normal = m.GetTexture("_BumpMap") as Texture2D;
                Assert.IsNotNull(albedo, name + " needs a surface texture.");
                Assert.IsNotNull(normal, name + " needs a normal map so the flashlight picks out relief.");
                Assert.IsTrue(m.IsKeywordEnabled("_NORMALMAP"), name);
                var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(normal));
                Assert.AreEqual(TextureImporterType.NormalMap, importer.textureType, name);
                foreach (var tex in new[] { albedo, normal })
                {
                    Assert.LessOrEqual(Mathf.Max(tex.width, tex.height), 1024, name + ": textures must stay small on a headset.");
                    Assert.Greater(tex.mipmapCount, 1, name + ": textures must be mipmapped.");
                }
            }
        }

        [Test]
        public void EachWreck_HasItsOwnSurfaces()
        {
            // The three ship wrecks; other kinds of map (not built from ship walls) are not covered here.
            var maps = Maps().Where(m => m.GetComponentsInChildren<Transform>(true).Any(t => t.name == DressingName)).ToArray();
            Assert.GreaterOrEqual(maps.Length, 3);
            var wallSets = maps.Select(map => map.GetComponentsInChildren<MeshRenderer>(true)
                .Where(r => r.name == "Segment").Select(r => r.sharedMaterial.name).Where(n => n.StartsWith("Wall_")).Distinct().OrderBy(n => n).ToArray()).ToArray();
            foreach (var set in wallSets) Assert.AreEqual(1, set.Length, "Each wreck's walls use its own material.");
            Assert.AreEqual(3, wallSets.Select(s => s[0]).Distinct().Count(), "No two wrecks look the same.");
        }

        [Test]
        public void Decoration_HasNoColliders_AndStaysWithinTheBudget()
        {
            int dressed = 0;
            foreach (var map in Maps())
            {
                var dressing = map.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == DressingName);
                if (dressing == null) continue;
                dressed++;
                Assert.IsEmpty(dressing.GetComponentsInChildren<Collider>(true), map.mapId + ": decoration must never block a route.");
                var renderers = dressing.GetComponentsInChildren<MeshRenderer>(true);
                var labels = map.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Env Labels");
                int labelRenderers = labels != null ? labels.GetComponentsInChildren<Renderer>(true).Length : 0;
                var lenses = map.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Env Lenses");
                if (lenses != null)
                {
                    labelRenderers += lenses.GetComponentsInChildren<Renderer>(true).Length;
                    Assert.IsEmpty(lenses.GetComponentsInChildren<Collider>(true), map.mapId + ": lenses must never block a route.");
                }
                if (labels != null) Assert.IsEmpty(labels.GetComponentsInChildren<Collider>(true), map.mapId + ": labels must never block a route.");
                Assert.LessOrEqual(renderers.Length + labelRenderers, 60, map.mapId + ": too many extra draw calls.");
                int triangles = renderers.Sum(r => r.GetComponent<MeshFilter>().sharedMesh.triangles.Length / 3);
                Assert.LessOrEqual(triangles, 60000, map.mapId + ": too many extra triangles.");
                foreach (var r in renderers)
                {
                    Assert.AreEqual(UnityEngine.Rendering.ShadowCastingMode.Off, r.shadowCastingMode);
                    Assert.IsTrue(r.gameObject.isStatic, "Decoration is static so it batches.");
                }
            }
            Assert.GreaterOrEqual(dressed, 3, "All three ship wrecks are dressed.");
        }

        [Test]
        public void NormalMappedWalls_HaveTangents()
        {
            foreach (var map in Maps())
            {
                var wall = map.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(r => r.name == "Segment" && r.sharedMaterial.name.StartsWith("Wall_"));
                if (wall == null) continue;
                var mesh = wall.GetComponent<MeshFilter>().sharedMesh;
                Assert.AreEqual(mesh.vertexCount, mesh.tangents.Length, map.mapId + ": a normal map without tangents lights wrongly.");
            }
        }

        [Test]
        public void DustMotes_AreOneSmallUncollidingSystem_ThatFollowsThePlayer()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var dust = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<TrashAstronauts.World.DustMotes>(true)).ToArray();
            Assert.AreEqual(1, dust.Length, "Exactly one dust system, following the player.");
            var ps = dust[0].GetComponent<ParticleSystem>();
            Assert.LessOrEqual(ps.main.maxParticles, 100);
            Assert.AreEqual(ParticleSystemSimulationSpace.World, ps.main.simulationSpace, "Dust hangs in the air; it must not swim with the head.");
            Assert.IsFalse(ps.collision.enabled);
            Assert.AreEqual(UnityEngine.Rendering.ShadowCastingMode.Off, ps.GetComponent<ParticleSystemRenderer>().shadowCastingMode);
        }
    }
}

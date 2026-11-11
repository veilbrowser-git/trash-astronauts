using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.Tools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TrashAstronauts.Tests
{
    /// <summary>Checks the generated assets and the real level data, not just the rules in isolation.</summary>
    public class ProjectContentTests
    {
        const string ScenePath = "Assets/TrashAstronauts/Scenes/TrashAstronauts.unity";

        [Test]
        public void Scene_IsTheOnlyEnabledBuildScene()
        {
            var enabled = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            CollectionAssert.AreEqual(new[] { ScenePath }, enabled);
        }

        [Test]
        public void SalvageCatalog_HasAllFourKinds_WithPrefabs()
        {
            var defs = AssetDatabase.FindAssets("t:SalvageDefinition")
                .Select(g => AssetDatabase.LoadAssetAtPath<SalvageDefinition>(AssetDatabase.GUIDToAssetPath(g))).ToList();
            foreach (SalvageCategory category in System.Enum.GetValues(typeof(SalvageCategory)))
                Assert.IsTrue(defs.Any(d => d.category == category), "Missing salvage of kind " + category);
            foreach (var def in defs)
            {
                Assert.IsNotNull(def.prefab, def.id + " has no prefab");
                Assert.IsNotNull(def.prefab.GetComponent<SalvageItem>(), def.id + " prefab has no SalvageItem");
                Assert.Greater(def.creditValue, 0);
            }
            Assert.AreEqual(defs.Count, defs.Select(d => d.id).Distinct().Count(), "Salvage ids must be unique.");
        }

        [Test]
        public void PlayerRig_CarriesAllThreeEssentialTools_InHolsters()
        {
            var rig = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TrashAstronauts/Prefabs/PlayerRig.prefab");
            Assert.IsNotNull(rig);
            var context = rig.GetComponent<PlayerContext>();
            Assert.IsNotNull(context.leftHand);
            Assert.IsNotNull(context.rightHand);
            Assert.AreEqual(3, context.tools.Length);
            Assert.IsTrue(context.tools.Any(t => t is FlashlightTool));
            Assert.IsTrue(context.tools.Any(t => t is ScannerTool));
            Assert.IsTrue(context.tools.Any(t => t is PlasmaCutterTool));
            // Essential tools start on the player, so no tool can ever be behind a barrier that needs it.
            foreach (var tool in context.tools)
            {
                Assert.IsNotNull(tool.holster, tool.toolName + " has no holster");
                Assert.IsTrue(tool.transform.IsChildOf(rig.transform));
            }
        }

        [Test]
        public void RealLevel_AlwaysGeneratesEnoughLoot_ForEverySeed()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var director = Object.FindFirstObjectByType<MissionDirector>();
            Assert.IsNotNull(director);
            var config = director.Config;
            var points = director.spawnPoints.Select(p => p.ToInfo()).ToList();
            var specs = AssetDatabase.FindAssets("t:SalvageDefinition")
                .Select(g => AssetDatabase.LoadAssetAtPath<SalvageDefinition>(AssetDatabase.GUIDToAssetPath(g)).ToSpec()).ToList();

            Assert.AreEqual(points.Count, points.Select(p => p.id).Distinct().Count(), "Spawn point ids must be unique.");

            for (int seed = 1; seed <= 500; seed++)
            {
                var plan = SalvageSpawnPlanner.Plan(points, specs, config.GetCount, config.quotaCredits, config.quotaSafetyMargin, seed, 1);
                Assert.IsFalse(plan.QuotaWasLowered, "seed " + seed + " could not support the quota");
                Assert.Greater(plan.TotalValue, config.quotaCredits, "seed " + seed);
                Assert.AreEqual(1, plan.Assignments.Count(a => a.category == SalvageCategory.ReactorComponent));
                Assert.GreaterOrEqual(plan.Assignments.Count(a => a.category == SalvageCategory.Novelty), 1);
            }
        }

        [Test]
        public void RealLevel_GatedSpawnPoints_ReferenceRegisteredCuttables()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var director = Object.FindFirstObjectByType<MissionDirector>();
            var ids = director.cuttables.Select(c => c.panelId).ToList();
            Assert.AreEqual(ids.Count, ids.Distinct().Count(), "Panel ids must be unique.");
            foreach (var point in director.spawnPoints.Where(p => p.Gate != null))
                CollectionAssert.Contains(director.cuttables, point.Gate, point.pointId);
            Assert.IsTrue(director.spawnPoints.Any(p => p.allowed.Allows(SalvageCategory.ReactorComponent) && p.behindPanel != null),
                "The reactor component must sit behind a cuttable panel.");
            Assert.IsTrue(director.spawnPoints.Any(p => p.mount != null), "There should be at least one mounted salvage point.");
        }
    }
}

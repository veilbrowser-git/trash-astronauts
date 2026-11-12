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
            Assert.AreEqual(4, context.tools.Length);
            Assert.IsTrue(context.tools.Any(t => t is TranqTool));
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
        public void PlayerRig_FloatsWithZeroGLocomotion()
        {
            var rig = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TrashAstronauts/Prefabs/PlayerRig.prefab");
            var zeroG = rig.GetComponent<ZeroGLocomotion>();
            Assert.IsNotNull(zeroG, "The rig should move with the zero-g provider.");
            Assert.IsNotNull(zeroG.leftHand);
            Assert.IsNotNull(zeroG.rightHand);
            Assert.AreSame(zeroG, rig.GetComponent<PlayerLocomotion>().zeroG);
            Assert.IsNotNull(zeroG.head, "Solid hands need the head reference.");
            Assert.AreEqual(2, rig.GetComponentsInChildren<HandTrackingDriver>(true).Length, "Each hand should support hand tracking.");
            Assert.AreEqual(2, rig.GetComponentsInChildren<HandWallClamp>(true).Length);
            foreach (var interactor in rig.GetComponentsInChildren<UnityEngine.XR.Interaction.Toolkit.Interactors.XRDirectInteractor>(true))
                Assert.IsTrue(interactor.improveAccuracyWithSphereCollider, "Grab detection must use overlap queries (trigger events never reach the hand layer).");
            Assert.IsNotNull(rig.GetComponentInChildren<TrashAstronauts.UI.TutorialPrompt>(true));
        }

        [Test]
public void Creature_IsARealSpider_EightLegsEightEyesFangsAndFur()
        {
            foreach (var name in new[] { "Drone_Janitor7", "Creature_Listener", "Creature_Warden", "Creature_Mimic" })
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TrashAstronauts/Prefabs/" + name + ".prefab");
                Assert.IsNotNull(prefab, name);
                var drone = prefab.GetComponent<EnemyDrone>();
                var animator = prefab.GetComponent<CreatureAnimator>();
                Assert.IsNotNull(animator, name);
                Assert.IsTrue(animator.spider, name);
                Assert.AreEqual(8, animator.shoulders.Length, name + " should have eight legs.");
                Assert.AreEqual(8, animator.elbows.Length, name + " legs should bend at the knee.");
                for (int i = 0; i < 8; i++)
                {
                    Assert.IsNotNull(animator.shoulders[i]);
                    Assert.IsTrue(animator.elbows[i].IsChildOf(animator.shoulders[i]), "A knee belongs to its own leg.");
                }
                Assert.AreEqual(4, animator.shoulders.Count(l => l.localPosition.x < 0f), "Four legs on each side.");
                Assert.AreEqual(2, animator.palps.Length, name + " should have two pedipalps.");
                Assert.IsNotNull(animator.head);
                Assert.IsNotNull(animator.abdomen);
                Assert.IsNotNull(animator.jaw, name + " should have fangs.");
                Assert.IsNotNull(drone.eyeRenderer);
                Assert.AreEqual(7, drone.extraEyes.Length, name + " should have eight eyes.");
                int triangles = prefab.GetComponentsInChildren<MeshFilter>().Sum(f => f.sharedMesh.triangles.Length / 3);
                Assert.Greater(triangles, 20000, name + " should be a detailed, furred model.");
                Assert.Less(triangles, 90000, name + " must stay affordable on a standalone headset.");
                foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>())
                    Assert.AreEqual(filter.sharedMesh.vertexCount, filter.sharedMesh.normals.Length, filter.name);
            }
        }

        [Test]
        public void ThreeDifferentCreatures_CanBeAboard_AndOnlyOneIsSwitchedOnInTheScene()
        {
            var janitor = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TrashAstronauts/Prefabs/Drone_Janitor7.prefab").GetComponent<EnemyDrone>();
            var listener = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TrashAstronauts/Prefabs/Creature_Listener.prefab").GetComponent<EnemyDrone>();
            var warden = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TrashAstronauts/Prefabs/Creature_Warden.prefab").GetComponent<EnemyDrone>();
            Assert.AreEqual(3, new[] { janitor.displayName, listener.displayName, warden.displayName }.Distinct().Count());
            Assert.IsFalse(janitor.overrideTuning);
            Assert.IsTrue(listener.overrideTuning && warden.overrideTuning);
            Assert.Less(listener.tuning.sightRange, 2f, "The Listener is blind.");
            Assert.Greater(listener.tuning.hearingRadiusAtFullLoudness, 20f);
            Assert.Greater(warden.tuning.sightRange, 12f, "The Warden sees far.");
            Assert.Less(warden.tuning.chaseSpeed, janitor.director == null ? 2.6f : 2.6f, "The Warden is the slowest hunter.");
            var listenerMesh = listener.GetComponentsInChildren<MeshFilter>().Sum(f => f.sharedMesh.triangles.Length);
            var janitorMesh = janitor.GetComponentsInChildren<MeshFilter>().Sum(f => f.sharedMesh.triangles.Length);
            Assert.AreNotEqual(listenerMesh, janitorMesh, "Each creature is its own species, not a recolour.");

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var director = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<MissionDirector>(true)).Single();
            Assert.AreEqual(4, director.enemies.Length);
            Assert.AreEqual(1, director.enemies.Count(e => e.gameObject.activeSelf), "Only one creature is aboard at a time.");
            foreach (var enemy in director.enemies)
            {
                Assert.IsNotNull(enemy.dock);
                Assert.Greater(enemy.patrolRoute.Length, 10);
            }
        }

        [Test]
        public void Mimic_HasASpeaker_ItsOwnSenses_AndTheSameSkeleton()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TrashAstronauts/Prefabs/Creature_Mimic.prefab");
            Assert.IsNotNull(prefab);
            var drone = prefab.GetComponent<EnemyDrone>();
            var mimic = prefab.GetComponent<VoiceMimic>();
            Assert.IsNotNull(mimic);
            Assert.AreSame(drone, mimic.drone);
            Assert.IsNotNull(mimic.speaker);
            Assert.AreEqual(1f, mimic.speaker.spatialBlend, "Its voice must come from where it is.");
            Assert.IsFalse(mimic.speaker.playOnAwake);
            Assert.Less(mimic.pitch, 1f, "It should sound lower than the player.");
            Assert.AreEqual("THE MIMIC", drone.displayName);
            Assert.IsTrue(drone.overrideTuning);
            Assert.AreEqual(8, prefab.GetComponent<CreatureAnimator>().shoulders.Length);
            foreach (var name in new[] { "Drone_Janitor7", "Creature_Listener", "Creature_Warden" })
                Assert.IsNull(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TrashAstronauts/Prefabs/" + name + ".prefab").GetComponent<VoiceMimic>(), "Only the Mimic records phrases.");
        }

        [Test]
        public void Hands_AreInAHandshakePose_AndToolsAreHeldByTheirHandles()
        {
            var rig = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TrashAstronauts/Prefabs/PlayerRig.prefab");
            var hands = rig.GetComponentsInChildren<HandRig>(true);
            Assert.AreEqual(2, hands.Length);
            foreach (var hand in hands)
            {
                bool left = hand.side == TrashAstronauts.Core.Handedness.Left;
                var glove = hand.GetComponentInChildren<GloveVisual>(true).transform;
                // The modelled palm faces glove -Y; held like a handshake it must face the other hand.
                Vector3 palmNormal = glove.localRotation * Vector3.down;
                Assert.Greater(Vector3.Dot(palmNormal, left ? Vector3.right : Vector3.left), 0.95f, hand.name + " palm should face inward.");
                Vector3 thumbSide = glove.localRotation * (left ? Vector3.right : Vector3.left);
                Assert.Greater(Vector3.Dot(thumbSide, Vector3.up), 0.95f, hand.name + " thumb should be on top.");

                // The grab point is in front of the palm surface, not inside the glove or out at the fingertips.
                Vector3 grab = hand.transform.InverseTransformPoint(hand.interactor.transform.position);
                Vector3 palmCentre = hand.transform.InverseTransformPoint(glove.TransformPoint(new Vector3(0, -0.03f, -0.075f)));
                Assert.Less(Vector3.Distance(grab, palmCentre), 0.06f);
                Assert.Greater(Vector3.Dot(grab - palmCentre, left ? Vector3.right : Vector3.left), 0.015f);

                Assert.IsNotNull(hand.thumbstickClick, "Brake input missing.");
                Assert.IsNotNull(hand.thrusterVisual);
                Assert.IsFalse(hand.thrusterVisual.activeSelf);
                Assert.IsNotNull(hand.thrusterAudio);
                Assert.AreSame(glove, hand.GetComponent<HandTrackingDriver>().glove);
                Assert.IsTrue(hand.fingertip.IsChildOf(glove));
            }

            foreach (var tool in rig.GetComponentsInChildren<SuitTool>(true))
            {
                var grip = tool.grab.attachTransform;
                Assert.IsNotNull(grip, tool.toolName);
                Assert.Less(grip.localPosition.y, -0.04f, tool.toolName + " should be held by its handle, below the body.");
                Assert.AreEqual(Quaternion.identity, grip.localRotation);
            }
            Assert.IsNotNull(rig.GetComponent<SuitAudio>().breathing);
            Assert.IsNotNull(rig.GetComponent<SuitAudio>().heartbeat);
        }

        [Test]
        public void RubberDuck_IsAHiddenBonus_ThatSqueaks()
        {
            var duck = AssetDatabase.LoadAssetAtPath<SalvageDefinition>("Assets/TrashAstronauts/Data/Salvage/novelty_rubber_duck.asset");
            Assert.IsNotNull(duck);
            Assert.IsTrue(duck.bonusSpawn);
            Assert.IsNotNull(duck.prefab.GetComponent<TrashAstronauts.World.SqueakyToy>());
            Assert.IsNotNull(duck.prefab.GetComponent<SalvageItem>());
            var audio = AssetDatabase.LoadAssetAtPath<TrashAstronauts.Audio.AudioLibrary>("Assets/TrashAstronauts/Data/AudioLibrary.asset");
            Assert.IsNotNull(audio.squeak);
            Assert.IsNotNull(audio.trickShot);
            Assert.IsNotNull(audio.thruster);
            Assert.IsNotNull(audio.breathing);
            Assert.IsNotNull(audio.heartbeat);
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
                .Select(g => AssetDatabase.LoadAssetAtPath<SalvageDefinition>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(d => !d.bonusSpawn).Select(d => d.ToSpec()).ToList();

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

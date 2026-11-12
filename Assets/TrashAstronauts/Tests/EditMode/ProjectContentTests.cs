using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
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

        // ------------------------------------------------------------------ co-op: stable ids and wire field widths

        static T[] InScene<T>(UnityEngine.SceneManagement.Scene scene) where T : Component
        {
            return scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).ToArray();
        }

        [Test]
        public void NetIds_AreUniqueAcrossTheWholeScene_AndEveryObjectHasANumber()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var director = InScene<MissionDirector>(scene).Single();
            var ids = director.NetIds;
            CollectionAssert.IsEmpty(ids.Validate(), "Net id problems: " + string.Join("; ", ids.Validate()));

            // Scene-wide, all three wrecks at once (objects of wrecks that are not docked are inactive but still there).
            var doors = InScene<TrashAstronauts.World.SlidingDoor>(scene);
            var panels = InScene<Cuttable>(scene);
            var wires = InScene<WebTripwire>(scene);
            var points = InScene<SalvageSpawnPoint>(scene);
            Assert.AreEqual(doors.Length, doors.Select(d => d.doorId).Distinct().Count(), "Door ids must be unique across every wreck.");
            Assert.AreEqual(panels.Length, panels.Select(p => p.panelId).Distinct().Count(), "Panel ids must be unique across every wreck.");
            Assert.AreEqual(wires.Length, wires.Select(w => w.wireId).Distinct().Count(), "Web ids must be unique across every wreck.");
            Assert.AreEqual(points.Length, points.Select(p => p.pointId).Distinct().Count(), "Spawn point ids must be unique across every wreck.");
            Assert.IsTrue(doors.All(d => !string.IsNullOrEmpty(d.doorId)) && panels.All(p => !string.IsNullOrEmpty(p.panelId))
                && wires.All(w => !string.IsNullOrEmpty(w.wireId)) && points.All(p => !string.IsNullOrEmpty(p.pointId)));

            // Nothing in the scene is left without a number, and a number leads back to the same object.
            Assert.AreEqual(doors.Length, ids.Doors.Count, "A door is not reachable from the director's lists.");
            Assert.AreEqual(panels.Length, ids.Panels.Count, "A cut panel is not reachable from the director's lists.");
            Assert.AreEqual(wires.Length, ids.Wires.Count, "A web is not in any wreck's tripwire list.");
            Assert.AreEqual(points.Length, ids.Points.Count, "A spawn point is not in any wreck's list.");
            foreach (var door in doors) Assert.AreSame(door, director.DoorAt(ids.Doors.IndexOf(door.doorId)), door.doorId);
            foreach (var panel in panels)
            {
                int index = ids.Panels.IndexOf(panel.panelId);
                Assert.AreSame(panel, director.PanelAt(index), panel.panelId);
                Assert.AreEqual(panel.cutPoints.Length, ids.CutPointCount(index), panel.panelId);
                Assert.IsTrue(panel.cutPoints.All(p => p != null), panel.panelId + " has an empty cut point slot.");
            }
            foreach (var wire in wires) Assert.AreSame(wire, director.WireAt(ids.Wires.IndexOf(wire.wireId)), wire.wireId);
            foreach (var point in points) Assert.AreSame(point, director.PointAt(ids.Points.IndexOf(point.pointId)), point.pointId);
            Assert.IsNull(director.DoorAt(-1));
            Assert.IsNull(director.DoorAt(ids.Doors.Count));

            // Numbers follow the sorted ids, wrecks keep the director's order.
            CollectionAssert.AreEqual(doors.Select(d => d.doorId).OrderBy(s => s, System.StringComparer.Ordinal).ToList(), ids.Doors.Ids);
            CollectionAssert.AreEqual(wires.Select(w => w.wireId).OrderBy(s => s, System.StringComparer.Ordinal).ToList(), ids.Wires.Ids);
            CollectionAssert.AreEqual(director.maps.Select(m => m.mapId).ToList(), ids.Maps.Ids);
            Assert.AreEqual(director.maps.Length, director.maps.Select(m => m.mapId).Distinct().Count(), "Map ids must be unique.");

            // Each wreck owns its webs; none is shared and none is listed twice.
            Assert.IsNotNull(director.sharedTripwires);
            foreach (var map in director.maps)
            {
                Assert.IsNotNull(map.tripwires, map.displayName);
                // The lava tube was built without web tripwires (they are being retired); the ships keep theirs.
                if (map.mapId != "lava_tube") Assert.GreaterOrEqual(map.tripwires.Length, 2, map.displayName + " should have webs.");
                foreach (var wire in map.tripwires) Assert.IsTrue(wire != null && wire.transform.IsChildOf(map.transform), map.displayName + " lists a web that is not its own.");
            }
            Assert.AreEqual(wires.Length, director.maps.Sum(m => m.tripwires.Length) + director.sharedTripwires.Length);
        }

        [Test]
        public void NetCounts_FitTheirWireFields()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var director = InScene<MissionDirector>(scene).Single();
            var ids = director.NetIds;
            var defs = AssetDatabase.FindAssets("t:SalvageDefinition")
                .Select(g => AssetDatabase.LoadAssetAtPath<SalvageDefinition>(AssetDatabase.GUIDToAssetPath(g))).ToList();

            Assert.LessOrEqual(ids.Doors.Count, NetIdTable.MaxDoors, "Door states travel as a 16-bit mask.");
            Assert.LessOrEqual(ids.Panels.Count, NetIdTable.MaxPanels, "Open panels travel as a 16-bit mask.");
            Assert.LessOrEqual(ids.Wires.Count, NetIdTable.MaxWires, "Web states travel as 32-bit masks.");
            Assert.LessOrEqual(ids.CreatureCount, NetIdTable.MaxCreatures);
            Assert.AreEqual(director.enemies.Length, ids.CreatureCount);
            Assert.LessOrEqual(ids.Points.Count, NetIdTable.MaxPoints, "A spawn point index is one byte.");
            Assert.LessOrEqual(ids.Definitions.Count, NetIdTable.MaxDefinitions, "A salvage definition index is one byte.");
            Assert.LessOrEqual(ids.Maps.Count, NetIdTable.MaxMaps);
            Assert.AreEqual(defs.Count, ids.Definitions.Count, "Every salvage definition must be in the director's catalog.");
            foreach (var def in defs) Assert.GreaterOrEqual(ids.Definitions.IndexOf(def.id), 0, def.id);

            // A run can never have more items than a one-byte id can name: one per spawn point, plus the bonus item.
            foreach (var map in director.maps)
                Assert.LessOrEqual(map.spawnPoints.Length + 1, NetIdTable.MaxItems, map.displayName + " could spawn more items than the item id field holds.");

            // Item values travel as 16 bits, including the richest contract condition.
            float richest = System.Enum.GetValues(typeof(ContractCondition)).Cast<ContractCondition>().Max(c => ContractConditions.ValueMultiplier(c));
            foreach (var def in defs)
                Assert.LessOrEqual(Mathf.RoundToInt(def.creditValue * richest), NetIdTable.MaxItemValue, def.id + " is worth more than the value field holds.");

            // The run index in a salvage id travels as 16 bits; conditions as one byte.
            Assert.LessOrEqual(System.Enum.GetValues(typeof(ContractCondition)).Cast<int>().Max(), 255);
        }

        [Test]
        public void NetContentHash_IsTheSameEveryTimeTheSceneIsLoaded_AndDoesNotDependOnObjectOrder()
        {
            const int protocol = 1;
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var director = InScene<MissionDirector>(scene).Single();
            uint first = director.NetIds.ContentHash(protocol);
            string summary = director.NetIds.Doors.Count + "/" + director.NetIds.Panels.Count + "/" + director.NetIds.Points.Count + "/" + director.NetIds.Wires.Count;
            Assert.AreNotEqual(first, director.NetIds.ContentHash(protocol + 1), "Another protocol version is another fingerprint.");

            // The same content gathered a different way (straight from the hierarchy, in reverse) gives the same fingerprint.
            var byHand = new NetIdTable(
                InScene<TrashAstronauts.World.SlidingDoor>(scene).Reverse().Select(d => d.doorId),
                InScene<Cuttable>(scene).Reverse().Select(p => new System.Collections.Generic.KeyValuePair<string, int>(p.panelId, p.cutPoints.Length)),
                InScene<SalvageSpawnPoint>(scene).Reverse().Select(p => p.pointId),
                InScene<WebTripwire>(scene).Reverse().Select(w => w.wireId),
                director.maps.Select(m => m.mapId),
                AssetDatabase.FindAssets("t:SalvageDefinition").Select(g => AssetDatabase.LoadAssetAtPath<SalvageDefinition>(AssetDatabase.GUIDToAssetPath(g)).id),
                director.enemies.Length);
            Assert.AreEqual(first, byHand.ContentHash(protocol));

            // And again from a freshly loaded scene.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var reloaded = InScene<MissionDirector>(scene).Single();
            Assert.AreNotSame(director, reloaded);
            Assert.AreEqual(first, reloaded.NetIds.ContentHash(protocol), "Content " + summary);
        }
    }
}

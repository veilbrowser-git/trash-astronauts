using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.UI;
using TrashAstronauts.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Co-op content as the Builder generates it: the crew stand-in prefab carries nothing of a local rig, the
    /// scene has exactly one offline NetSession and a wired CO-OP CREW panel placed clear of the other tug
    /// furniture, and the Android settings keep the INTERNET permission that the sockets need.
    /// </summary>
    public class NetContentTests
    {
        const string ScenePath = "Assets/TrashAstronauts/Scenes/TrashAstronauts.unity";
        const string RemotePrefabPath = "Assets/TrashAstronauts/Prefabs/RemoteCrew.prefab";

        // ------------------------------------------------------------------ the stand-in prefab

        [Test]
        public void RemoteCrewPrefab_HasTheSuitGlovesAndBody_AndNothingOfALocalRig()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RemotePrefabPath);
            Assert.IsNotNull(prefab, "Builder makes the RemoteCrew prefab.");
            var remote = prefab.GetComponent<RemoteAvatar>();
            Assert.IsNotNull(remote);
            Assert.IsNotNull(remote.context);
            Assert.AreSame(remote.context, prefab.GetComponent<PlayerContext>());
            Assert.IsNotNull(remote.context.head);
            Assert.AreSame(remote.leftHand, remote.context.leftHand);
            Assert.AreSame(remote.rightHand, remote.context.rightHand);

            // The full Blender suit with the helmet shown, posed from the three sources.
            Assert.IsNotNull(remote.suit);
            Assert.AreSame(remote.suit, remote.context.avatar);
            Assert.IsTrue(remote.suit.HelmetVisible, "Other crew wear their helmets.");
            Assert.AreSame(remote.context.head, remote.suit.head);
            Assert.AreSame(remote.leftHand.transform, remote.suit.leftHand);
            Assert.AreSame(remote.rightHand.transform, remote.suit.rightHand);
            Assert.IsNull(remote.suit.yawSource);
            Assert.Greater(remote.suit.helmetRenderers.Length, 0);
            Assert.Greater(remote.suit.accentRenderers.Length, 0, "Crew-colour stripes to tint by slot.");

            // The same modelled gloves as the local rig, driven only by the streamed curl values.
            foreach (var glove in new[] { remote.leftGlove, remote.rightGlove })
            {
                Assert.IsNotNull(glove);
                Assert.IsTrue(glove.IsModelled, "The Blender glove, not the stand-in boxes.");
                Assert.IsNotNull(glove.hand);
            }
            Assert.AreSame(remote.leftHand, remote.leftGlove.hand);
            Assert.AreSame(remote.rightHand, remote.rightGlove.hand);
            foreach (var hand in new[] { remote.leftHand, remote.rightHand })
            {
                Assert.IsNull(hand.interactor);
                Assert.IsNull(hand.haptics);
                Assert.IsNull(hand.gripValue);
                Assert.IsNull(hand.triggerValue);
                Assert.IsNull(hand.thumbstick);
                Assert.IsNotNull(hand.thrusterVisual);
                Assert.IsFalse(hand.thrusterVisual.activeSelf);
            }

            // A kinematic capsule on the Player layer: doors see someone standing in them.
            Assert.IsNotNull(remote.body);
            Assert.IsTrue(remote.body.isKinematic);
            Assert.IsFalse(remote.body.useGravity);
            Assert.AreEqual(GameLayers.Player, remote.body.gameObject.layer);
            var colliders = prefab.GetComponentsInChildren<Collider>(true);
            Assert.AreEqual(1, colliders.Length, "The capsule is the only collider.");
            Assert.IsInstanceOf<CapsuleCollider>(colliders[0]);
            Assert.IsFalse(colliders[0].isTrigger);
        }

        [Test]
        public void RemoteCrewPrefab_HasNoCameraListenerOriginInteractorInputLightOrToolAndSalvageLayers()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RemotePrefabPath);
            Assert.IsNotNull(prefab);
            Assert.IsEmpty(prefab.GetComponentsInChildren<Camera>(true));
            Assert.IsEmpty(prefab.GetComponentsInChildren<AudioListener>(true));
            Assert.IsEmpty(prefab.GetComponentsInChildren<Light>(true), "No light (and so no shadow-casting light).");
            Assert.IsEmpty(prefab.GetComponentsInChildren<XRBaseInteractor>(true));
            Assert.IsEmpty(prefab.GetComponentsInChildren<CharacterController>(true));
            Assert.IsEmpty(prefab.GetComponentsInChildren<Tools.SuitTool>(true), "No tools: a crew member's tools are not shared yet.");
            Assert.IsEmpty(prefab.GetComponentsInChildren<VoiceSensor>(true), "Nothing that listens to a microphone.");
            foreach (var component in prefab.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                string type = component.GetType().FullName;
                Assert.AreNotEqual("Unity.XR.CoreUtils.XROrigin", type, "no XR origin");
                Assert.IsFalse(type.Contains("TrackedPoseDriver"), "no tracked pose driver: " + component.name);
                Assert.IsFalse(type.Contains("InputActionManager"), "no input: " + component.name);
                Assert.IsFalse(type.StartsWith("UnityEngine.XR.Interaction.Toolkit.Locomotion"), "no locomotion: " + type);
                Assert.IsFalse(component is AudioSource, "no audio source on a stand-in: " + component.name);
            }
            foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
            {
                Assert.AreNotEqual(GameLayers.Tool, t.gameObject.layer, t.name + " is on the Tool layer");
                Assert.AreNotEqual(GameLayers.Salvage, t.gameObject.layer, t.name + " is on the Salvage layer");
            }
            foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
                Assert.AreEqual(UnityEngine.Rendering.ShadowCastingMode.Off, r.shadowCastingMode, r.name + " casts shadows");
        }

        [Test]
        public void SalvageAndCreaturePrefabs_CarryNoNetworkComponent()
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/TrashAstronauts/Prefabs" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path == RemotePrefabPath) continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                Assert.IsEmpty(prefab.GetComponentsInChildren<NetSession>(true), path);
                Assert.IsEmpty(prefab.GetComponentsInChildren<RemoteAvatar>(true), path);
                Assert.IsEmpty(prefab.GetComponentsInChildren<NetLobbyBoard>(true), path);
            }
        }

        // ------------------------------------------------------------------ the scene

        [Test]
        public void Scene_HasOneOfflineNetSession_OnTheMissionObject_WiredToTheStandInPrefab()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var sessions = UnityEngine.Object.FindObjectsByType<NetSession>(FindObjectsInactive.Include);
            Assert.AreEqual(1, sessions.Length, "Exactly one NetSession.");
            var session = sessions[0];
            var director = session.GetComponent<MissionDirector>();
            Assert.IsNotNull(director, "It lives on the Mission object.");
            Assert.AreSame(director, session.director);
            Assert.IsNotNull(session.remoteAvatarPrefab);
            Assert.AreEqual(RemotePrefabPath, AssetDatabase.GetAssetPath(session.remoteAvatarPrefab));
            Assert.AreEqual(TrashAstronauts.Core.Net.LobbyMode.Offline, session.Mode);
            Assert.IsTrue(session.IsOffline);
            Assert.IsNull(director.Net);
        }

        [Test]
        public void Scene_CoopCrewPanel_IsWired_AndHangsInTheTugClearOfTheOtherFurniture()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var boards = UnityEngine.Object.FindObjectsByType<NetLobbyBoard>(FindObjectsInactive.Include);
            Assert.AreEqual(1, boards.Length);
            var board = boards[0];
            Assert.IsNotNull(board.session);
            foreach (var b in new[] { board.hostButton, board.findButton, board.codeButton, board.leaveButton, board.fieldCButton, board.fieldDButton, board.backButton, board.joinCodeButton })
            {
                Assert.IsNotNull(b);
                Assert.AreEqual(GameLayers.Button, b.gameObject.layer, b.name + " is pressable by hand and pointer");
            }
            Assert.AreEqual("HOST\nCO-OP", board.hostButton.Text);
            Assert.AreEqual("FIND\nCREW", board.findButton.Text);
            Assert.AreEqual("ENTER\nCODE", board.codeButton.Text);
            Assert.AreEqual("LEAVE", board.leaveButton.Text);
            Assert.AreEqual(10, board.digitButtons.Length);
            for (int d = 0; d < 10; d++) Assert.AreEqual(d.ToString(), board.digitButtons[d].Text);
            Assert.AreEqual(3, board.joinButtons.Length);
            Assert.AreEqual(3, board.resultRows.Length);
            Assert.IsFalse(board.keypadPanel.activeSelf);
            Assert.IsFalse(board.resultsPanel.activeSelf);
            Assert.IsTrue(board.infoPanel.activeSelf);

            // Inside the tug (x -2..2, z -3..2), reachable heights.
            Bounds panel = BoundsOf(board.gameObject);
            Assert.That(panel.center.x, Is.InRange(1.6f, 2f));
            Assert.That(panel.min.z, Is.GreaterThan(-3f));
            Assert.That(panel.max.z, Is.LessThan(2f));
            Assert.That(panel.min.y, Is.InRange(1.05f, 1.4f), "Above the hand rail, the lowest keys within reach.");
            Assert.That(panel.max.y, Is.LessThan(2.65f));
            foreach (var b in board.digitButtons) Assert.That(b.transform.position.y, Is.InRange(1.1f, 1.8f), "Keypad at hand height.");
            Assert.That(board.hostButton.transform.position.y, Is.InRange(1.6f, 2.1f));

            // Clear of the mission board, the tutorial board, the locker, the practice plate, the shop and the hatch.
            var director = UnityEngine.Object.FindAnyObjectByType<MissionDirector>();
            var mission = UnityEngine.Object.FindAnyObjectByType<MissionBoard>();
            var shop = UnityEngine.Object.FindAnyObjectByType<ShopPanel>();
            var practice = director.sharedCuttables[0];
            foreach (var other in new GameObject[] { mission.gameObject, director.locker.gameObject, practice.gameObject, shop.gameObject, director.tugHatch.gameObject })
            {
                Bounds o = BoundsOf(other);
                Assert.IsFalse(panel.Intersects(o), "The CO-OP CREW panel overlaps " + other.name);
            }
            Assert.Greater(Vector3.Distance(panel.center, mission.transform.position), 3f, "On the opposite wall from the mission board.");
        }

        [Test]
        public void CrewSpots_FourSlots_StandApartInsideTheTug_OnClearFloor()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Physics.SyncTransforms();
            var director = UnityEngine.Object.FindAnyObjectByType<MissionDirector>();
            Assert.AreEqual(director.hubSpawn.position, director.CrewSpot(0), "slot 0 (and single-player) is the hub spawn itself");
            var spots = Enumerable.Range(0, 4).Select(director.CrewSpot).ToArray();
            var obstacles = new[]
            {
                BoundsOf(UnityEngine.Object.FindAnyObjectByType<MissionBoard>().gameObject),
                BoundsOf(UnityEngine.Object.FindAnyObjectByType<NetLobbyBoard>().gameObject),
                BoundsOf(director.locker.gameObject),
                BoundsOf(director.sharedCuttables[0].gameObject),
                BoundsOf(GameObject.Find("Suit Settings Console")),
            };
            int mask = GameLayers.EnvironmentMask | (1 << GameLayers.Default) | GameLayers.RailMask | GameLayers.ButtonMask | GameLayers.SalvageMask;
            for (int i = 0; i < 4; i++)
            {
                Vector3 p = spots[i];
                Assert.That(p.x, Is.InRange(-1.6f, 1.6f), "slot " + i + " inside the tug, clear of the side walls");
                Assert.That(p.z, Is.InRange(-2.6f, 1.6f), "slot " + i + " inside the tug, clear of the end walls");
                Assert.IsTrue(director.safeZone.Contains(p + Vector3.up * 1.6f), "slot " + i + " head inside the safe zone");
                Assert.IsFalse(Physics.CheckCapsule(p + Vector3.up * 0.35f, p + Vector3.up * 1.75f, 0.3f, mask, QueryTriggerInteraction.Ignore),
                    "slot " + i + " has room to stand (a 0.3 m body from ankle to head hits nothing)");
                var feet = new Bounds(p + Vector3.up * 1.05f, new Vector3(0.7f, 1.9f, 0.7f));
                foreach (var o in obstacles) Assert.IsFalse(feet.Intersects(o), "slot " + i + " is too close to a board, the locker or the plate");
                for (int j = 0; j < i; j++)
                {
                    Vector2 a2 = new Vector2(p.x, p.z), b2 = new Vector2(spots[j].x, spots[j].z);
                    Assert.GreaterOrEqual(Vector2.Distance(a2, b2), 0.7f, "slots " + j + " and " + i + " are at least 0.7 m apart");
                }
            }
        }

        static Bounds BoundsOf(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled && !(r is ParticleSystemRenderer)).ToArray();
            Assert.Greater(renderers.Length, 0, go.name + " has nothing to measure");
            Bounds b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            return b;
        }

        // ------------------------------------------------------------------ Android permission

        [Test]
        public void Android_KeepsTheInternetPermission_ForCoopSockets()
        {
            Assert.IsTrue(PlayerSettings.Android.forceInternetPermission, "PlayerSettings.Android.forceInternetPermission must be on.");

            // MetaQuestFeature.ForceRemoveInternetPermission must be off (it would strip the permission again).
            // The test assembly does not reference OpenXR, so it is read by reflection.
            Type settingsType = FindType("UnityEngine.XR.OpenXR.OpenXRSettings");
            Assert.IsNotNull(settingsType, "OpenXR is installed.");
            var get = settingsType.GetMethod("GetSettingsForBuildTargetGroup", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(BuildTargetGroup) }, null);
            var settings = get.Invoke(null, new object[] { BuildTargetGroup.Android });
            Assert.IsNotNull(settings);
            var getFeatures = settingsType.GetMethods().First(m => m.Name == "GetFeatures" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            var features = (Array)getFeatures.Invoke(settings, null);
            object quest = features.Cast<object>().FirstOrDefault(f => f.GetType().Name == "MetaQuestFeature");
            Assert.IsNotNull(quest, "The Meta Quest feature is set up for Android.");
            var remove = quest.GetType().GetProperty("ForceRemoveInternetPermission");
            Assert.IsNotNull(remove, "MetaQuestFeature.ForceRemoveInternetPermission exists in this OpenXR version.");
            Assert.IsFalse((bool)remove.GetValue(quest), "The Meta Quest feature must not strip the INTERNET permission.");
        }

        [Test]
        public void AndroidManifestCheck_PassesWithInternet_FailsWithoutIt_AndFailsWhenAManifestRemovesIt()
        {
            Type check = FindType("TrashAstronauts.EditorTools.AndroidManifestCheck");
            Assert.IsNotNull(check, "The Gradle manifest check is in the editor assembly.");
            Assert.IsTrue(check.GetInterfaces().Any(i => i.Name == "IPostGenerateGradleAndroidProject"), "It runs on every Android build.");
            var method = check.GetMethod("Check", BindingFlags.Public | BindingFlags.Static);

            string root = Path.Combine(Path.GetTempPath(), "ta_manifest_" + Guid.NewGuid().ToString("N"));
            try
            {
                string lib = Path.Combine(root, "unityLibrary");
                string launcher = Path.Combine(root, "launcher");
                Directory.CreateDirectory(Path.Combine(lib, "src", "main"));
                Directory.CreateDirectory(Path.Combine(launcher, "src", "main"));
                const string head = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\" xmlns:tools=\"http://schemas.android.com/tools\">\n";
                string libManifest = Path.Combine(lib, "src", "main", "AndroidManifest.xml");
                string launcherManifest = Path.Combine(launcher, "src", "main", "AndroidManifest.xml");
                File.WriteAllText(launcherManifest, head + "<application/>\n</manifest>");

                File.WriteAllText(libManifest, head + "<uses-permission android:name=\"android.permission.INTERNET\"/>\n<application/>\n</manifest>");
                Assert.IsNull(method.Invoke(null, new object[] { lib }), "INTERNET requested: the build goes on.");

                File.WriteAllText(libManifest, head + "<uses-permission android:name=\"android.permission.RECORD_AUDIO\"/>\n<application/>\n</manifest>");
                StringAssert.Contains("android.permission.INTERNET", (string)method.Invoke(null, new object[] { lib }), "No INTERNET: the build fails.");

                File.WriteAllText(libManifest, head + "<uses-permission android:name=\"android.permission.INTERNET\"/>\n<application/>\n</manifest>");
                File.WriteAllText(launcherManifest, head + "<uses-permission android:name=\"android.permission.INTERNET\" tools:node=\"remove\"/>\n<application/>\n</manifest>");
                StringAssert.Contains("remove", (string)method.Invoke(null, new object[] { lib }), "A merge rule that removes INTERNET fails the build too.");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }
    }
}

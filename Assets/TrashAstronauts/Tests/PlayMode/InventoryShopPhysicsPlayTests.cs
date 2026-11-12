using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.UI;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>The folding 3D shop, the supply bay, hand and body pushing, and the dropping spider.</summary>
    public class InventoryShopPhysicsPlayTests
    {
        MissionDirector m_Director;
        PlayerContext m_Player;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            MissionDirector.StorageOverride = new MemorySaveStorage();
            MissionDirector.SeedOverride = 58;
            MissionDirector.CreatureOverride = 0;
            MissionDirector.MapOverride = 0;
            MissionDirector.ConditionOverride = ContractCondition.Standard;
            BlackoutController.DamageOverride = 0f;
            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            yield return null;
            yield return null;
            m_Director = MissionDirector.Active;
            m_Player = m_Director.Players.First();
        }

        [TearDown]
        public void TearDown()
        {
            MissionDirector.StorageOverride = null;
            MissionDirector.SeedOverride = null;
            MissionDirector.CreatureOverride = null;
            MissionDirector.MapOverride = null;
            MissionDirector.ConditionOverride = null;
            BlackoutController.DamageOverride = null;
        }

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        [UnityTest]
        public IEnumerator Shop_IsARealStallInTheSupplyBay_AndFoldsIntoACrateForTheContract()
        {
            var shop = Object.FindFirstObjectByType<ShopPanel>();
            var folder = shop.GetComponent<ShopFolder>();
            Assert.IsNotNull(folder, "The shop should be able to fold away.");
            Assert.IsTrue(folder.IsOpen);
            Assert.AreEqual(Vector3.one, folder.stall.localScale);
            Assert.GreaterOrEqual(shop.GetComponentsInChildren<SlowSpin>().Length, 3, "Each thing for sale should be on display.");
            Assert.Greater(shop.GetComponentsInChildren<MeshRenderer>().Length, 30, "It should be a 3D stall, not a flat panel.");

            // It is in the supply bay, which is part of the tug: safe, and reachable by teleport.
            Assert.IsTrue(m_Director.safeZone.Contains(shop.transform.position + Vector3.up));
            var bay = RecoveryAnchor.All.First(a => a.name.Contains("Supply Bay"));
            Assert.IsTrue(m_Director.safeZone.Contains(bay.transform.position + Vector3.up * 1.5f), "The supply bay is part of the safe tug.");
            Assert.Less(Vector3.Distance(bay.transform.position, shop.transform.position), 3f);
            Assert.IsFalse(m_Director.safeZone.Contains(new Vector3(-4f, 1.4f, 0f)), "Outside the hull beside the cabin is not the tug.");
            m_Player.locomotion.TeleportTo(bay.transform.position);
            for (float t = 0f; t < 1.5f; t += Time.unscaledDeltaTime) yield return null;
            Assert.AreEqual(0, m_Director.HubRescueCount, "Standing in the supply bay is not 'outside the lobby'.");
            Assert.Less(Vector3.Distance(m_Player.transform.position, bay.transform.position), 0.6f);

            // You can still buy there.
            m_Director.Profile.Data.totalCredits = m_Director.Config.tranqDartCost;
            m_Director.Profile.NotifyChangedAndSave();
            yield return Frames(2);
            Assert.IsTrue(shop.tranqButton.Press(null));
            Assert.AreEqual(1, m_Director.Profile.Data.tranqDarts);

            // Starting a contract folds it up small; coming back unfolds it.
            Vector3 openSize = Size(folder.stall);
            m_Director.StartMission(PlayerId.Local);
            for (float t = 0f; t < 4f && !folder.IsFolded; t += Time.deltaTime) yield return null;
            Assert.IsTrue(folder.IsFolded, "The shop should fold up when the round starts.");
            Vector3 foldedSize = Size(folder.stall);
            Assert.Less(foldedSize.y, openSize.y * 0.4f, "Folded, it should be a small crate. Open " + openSize + ", folded " + foldedSize);
            Assert.Less(foldedSize.x * foldedSize.z, openSize.x * openSize.z * 0.2f);

            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            yield return Frames(2);
            m_Director.ReturnToHub();
            for (float t = 0f; t < 4f && !folder.IsOpen; t += Time.deltaTime) yield return null;
            Assert.IsTrue(folder.IsOpen, "Back in the hub it should unfold again.");
            Assert.AreEqual(Vector3.one, folder.stall.localScale);
        }

        static Vector3 Size(Transform root)
        {
            var bounds = new Bounds(root.position, Vector3.zero);
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>()) bounds.Encapsulate(r.bounds);
            return bounds.size;
        }

        [UnityTest]
        public IEnumerator OpenHandsSwatLooseThings_AndTheBodyShouldersThemAside_ButNothingPushesThePlayer()
        {
            Assert.AreEqual(3, m_Player.bumpers.Length);
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            m_Director.keepPlayersInHub = false;
            foreach (var driver in m_Player.GetComponentsInChildren<UnityEngine.InputSystem.XR.TrackedPoseDriver>()) driver.enabled = false;
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 13f));
            yield return Frames(4);
            m_Player.locomotion.zeroG.Stop();

            var hand = m_Player.rightHand;
            var bumper = m_Player.bumpers[2];
            var item = m_Director.Items.First(i => !i.IsMounted && i.definition != null && !i.definition.bulky && i.definition.fragility <= 0f);
            Vector3 palm = hand.interactor.transform.position;

            // Reaching slowly does not knock it away.
            item.Body.linearVelocity = Vector3.zero;
            item.Body.angularVelocity = Vector3.zero;
            item.transform.position = palm + Vector3.forward * 0.25f;
            Physics.SyncTransforms();
            for (int i = 0; i < 30; i++)
            {
                hand.transform.position += Vector3.forward * 0.004f; // about 0.3 m/s
                yield return new WaitForFixedUpdate();
            }
            Assert.IsFalse(bumper.IsSolid, "A slow reach must not be solid.");
            Assert.Less(item.Body.linearVelocity.magnitude, 0.05f, "Reaching for something should not push it away.");

            // A fast swing bats it down the corridor.
            item.transform.position = hand.interactor.transform.position + Vector3.forward * 0.35f;
            item.Body.linearVelocity = Vector3.zero;
            Physics.SyncTransforms();
            for (int i = 0; i < 24; i++)
            {
                hand.transform.position += Vector3.forward * 0.035f; // about 2.5 m/s
                yield return new WaitForFixedUpdate();
            }
            Assert.Greater(item.Body.linearVelocity.z, 0.8f, "A swat with an open hand should send it flying. Velocity " + item.Body.linearVelocity);
            Assert.Greater(bumper.PushCount, 0);
            // (A fast open-hand sweep is also a swimming stroke, so the player may drift; what matters is
            // that the pusher is not a physical body, so nothing can push back on it.)
            Assert.IsNull(bumper.GetComponent<Rigidbody>());
            Assert.IsNull(bumper.GetComponent<Collider>());

            // Drifting into something with your chest shoulders it aside.
            var chest = m_Player.bumpers[0];
            Assert.IsTrue(chest.IsSolid);
            item.Body.linearVelocity = Vector3.zero;
            item.Body.angularVelocity = Vector3.zero;
            item.transform.position = chest.transform.position + Vector3.forward * 0.55f;
            Physics.SyncTransforms();
            m_Player.locomotion.zeroG.Stop();
            m_Player.locomotion.zeroG.AddImpulse(Vector3.forward * 1.2f);
            for (int i = 0; i < 90; i++) yield return new WaitForFixedUpdate();
            Assert.Greater(item.Body.linearVelocity.z, 0.3f, "Floating into loose salvage should push it along. Velocity " + item.Body.linearVelocity);
            Assert.Greater(m_Player.locomotion.zeroG.Velocity.z, 0.8f, "And it should not slow the player down.");
        }

        [UnityTest]
        public IEnumerator DropScare_PutsASmallSpiderOnAThreadInFrontOfYou_ThenTakesItAway()
        {
            var scares = m_Director.GetComponent<ScareDirector>();
            Assert.IsNotNull(scares.dropSpider);
            Assert.IsFalse(scares.dropSpider.activeSelf);
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            m_Director.keepPlayersInHub = false;
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 14f), Quaternion.identity);
            yield return Frames(4);

            Assert.IsTrue(scares.Trigger(ScareKind.Drop));
            float nearest = float.MaxValue;
            bool seen = false;
            for (float t = 0f; t < 4f && (scares.IsDropping || !seen); t += Time.deltaTime)
            {
                if (scares.dropSpider.activeSelf)
                {
                    seen = true;
                    nearest = Mathf.Min(nearest, Vector3.Distance(scares.dropSpider.transform.position, m_Player.HeadPosition));
                    var size = Size(scares.dropSpider.transform);
                    Assert.Less(Mathf.Max(size.x, size.z), 0.7f, "It should be a small spider, not the creature.");
                }
                yield return null;
            }
            Assert.IsTrue(seen, "The spider never appeared.");
            Assert.That(nearest, Is.InRange(0.45f, 1.0f), "It should hang at about arm's length, at eye level.");
            Assert.IsFalse(scares.dropSpider.activeSelf, "It should be gone again afterwards.");
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase, "It cannot hurt the player.");
        }
    }
}

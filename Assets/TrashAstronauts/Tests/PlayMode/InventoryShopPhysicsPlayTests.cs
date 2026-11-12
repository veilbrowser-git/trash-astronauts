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
    /// <summary>The wrist inventory, the folding 3D shop, the supply bay, hand and body pushing, and the dropping spider.</summary>
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
        }

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        [UnityTest]
        public IEnumerator Inventory_OpensFromTheWrist_StowsSmallSalvage_AndGivesItBack()
        {
            var inventory = m_Player.inventory;
            Assert.IsNotNull(inventory, "The suit should have an inventory.");
            Assert.AreEqual(5, inventory.Capacity);
            Assert.IsFalse(inventory.IsOpen, "It starts closed.");
            Assert.IsNotNull(inventory.wristButton, "There should be an INV button on the wrist display.");
            Assert.IsTrue(inventory.wristButton.transform.IsChildOf(m_Player.wristDisplay), "The button belongs on the wrist display.");

            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);

            // Touching the wrist button opens the tray in front of the player; it is see-through.
            Assert.IsTrue(inventory.wristButton.Press(m_Player.rightHand));
            yield return null;
            Assert.IsTrue(inventory.IsOpen);
            Vector3 toPanel = inventory.panel.transform.position - m_Player.HeadPosition;
            Assert.That(toPanel.magnitude, Is.InRange(0.3f, 0.8f), "The tray should appear within easy reach.");
            var glass = inventory.panel.GetComponentsInChildren<MeshRenderer>().First(r => r.name == "Glass");
            Assert.Less(glass.sharedMaterial.color.a, 0.5f, "The tray should be see-through.");

            var small = m_Director.Items.Where(i => !i.IsMounted && i.definition != null && !i.definition.bulky).Take(6).ToArray();
            Assert.AreEqual(6, small.Length);
            var bulky = m_Director.Items.FirstOrDefault(i => i.definition != null && i.definition.bulky);

            // Letting go of an item over a slot stows it there, shrunk to pocket size.
            Vector3 fullScale = small[0].transform.localScale;
            small[0].transform.position = inventory.slots[2].position + Vector3.up * 0.03f;
            Assert.IsTrue(inventory.TryStoreReleased(small[0]));
            Assert.AreSame(small[0], inventory.ItemAt(2));
            Assert.AreSame(inventory, small[0].StoredIn);
            Assert.IsTrue(small[0].Body.isKinematic);
            Assert.Less(Vector3.Distance(small[0].transform.position, inventory.slots[2].position), 0.01f);
            var bounds = new Bounds(small[0].transform.position, Vector3.zero);
            foreach (var r in small[0].GetComponentsInChildren<MeshRenderer>()) bounds.Encapsulate(r.bounds);
            Assert.Less(Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z), 0.16f, "A stowed item should be shrunk to fit its pocket.");

            // Letting go somewhere else does not.
            small[1].transform.position = inventory.panel.transform.position + Vector3.up * 0.6f;
            Assert.IsFalse(inventory.TryStoreReleased(small[1]));

            // It fills up, refuses a sixth, and refuses anything big.
            for (int i = 1; i < 5; i++) Assert.IsTrue(inventory.TryStore(small[i]), "slot " + i);
            Assert.AreEqual(5, inventory.Count);
            Assert.IsFalse(inventory.TryStore(small[5]));
            Assert.AreEqual("Pockets full.", inventory.Refusal(small[5]));
            Assert.AreEqual(small.Take(5).Sum(i => i.Value), inventory.CarriedValue);
            if (bulky != null && !bulky.IsMounted) Assert.AreEqual("Too big for a pocket.", inventory.Refusal(bulky));

            // Stowed things are safe from knocks, and the locker does not take them out of your pockets.
            var delicate = small.Take(5).FirstOrDefault(i => i.definition.fragility > 0f);
            if (delicate != null) Assert.AreEqual(0, delicate.ApplyImpact(9f));
            Assert.IsFalse(m_Director.locker.TryAccept(small[0]));
            Assert.AreEqual(0, m_Director.Session.DepositedCount);

            // Close it: everything travels with you, out of sight. Open it somewhere else: still there.
            inventory.wristButton.Press(m_Player.rightHand);
            yield return new WaitForSecondsRealtime(0.4f);
            if (inventory.IsOpen) inventory.Close();
            Assert.IsFalse(inventory.IsOpen);
            Assert.IsFalse(small[0].gameObject.activeSelf);
            m_Player.locomotion.TeleportTo(new Vector3(0f, 0f, 5.6f));
            yield return Frames(4);
            inventory.Open();
            Assert.IsTrue(small[0].gameObject.activeSelf);
            Assert.Less(Vector3.Distance(small[0].transform.position, m_Player.HeadPosition), 1.2f, "Stowed salvage should come with you.");

            // Take one out: full size, loose again, and the locker accepts it.
            var taken = inventory.Take(2);
            Assert.AreSame(small[0], taken);
            Assert.IsNull(taken.StoredIn);
            Assert.IsFalse(taken.Body.isKinematic);
            Assert.AreEqual(fullScale, taken.transform.localScale);
            Assert.AreEqual(4, inventory.Count);
            Assert.IsTrue(m_Director.locker.TryAccept(taken));

            // The pockets are emptied when the contract ends.
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            yield return Frames(3);
            Assert.AreEqual(0, inventory.Count);
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

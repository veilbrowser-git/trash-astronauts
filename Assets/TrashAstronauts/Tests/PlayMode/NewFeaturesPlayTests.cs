using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.Tools;
using TrashAstronauts.UI;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>The shop, the tranquilizer and the blackout event, exercised in the real scene.</summary>
    public class NewFeaturesPlayTests
    {
        MemorySaveStorage m_Storage;
        MissionDirector m_Director;
        PlayerContext m_Player;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            m_Storage = new MemorySaveStorage();
            MissionDirector.StorageOverride = m_Storage;
            MissionDirector.SeedOverride = 99;
            MissionDirector.CreatureOverride = 0;
            MissionDirector.MapOverride = 0;
            MissionDirector.ConditionOverride = ContractCondition.Standard;
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
        }

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        [UnityTest]
        public IEnumerator Shop_SellsDartsAndUpgrades_AndClosesDuringAContract()
        {
            var shop = Object.FindFirstObjectByType<ShopPanel>();
            Assert.IsNotNull(shop, "The tug should have a shop.");
            var config = m_Director.Config;

            Assert.IsFalse(shop.tranqButton.Interactable, "Nothing is affordable with no credits.");
            m_Director.Profile.Data.totalCredits = config.tranqDartCost * 2 + config.scannerUpgradeCost + 5;
            m_Director.Profile.NotifyChangedAndSave();
            yield return Frames(2);
            Assert.IsTrue(shop.tranqButton.Interactable);

            Assert.IsTrue(shop.tranqButton.Press(null));
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.IsTrue(shop.tranqButton.Press(null));
            Assert.IsTrue(shop.scannerButton.Press(null));
            Assert.AreEqual(2, m_Director.Profile.Data.tranqDarts);
            Assert.IsTrue(m_Director.Profile.Data.scannerUpgrade);
            Assert.AreEqual(5, m_Director.Profile.Data.totalCredits);
            yield return Frames(2);
            Assert.IsFalse(shop.scannerButton.Interactable, "An owned upgrade cannot be bought again.");
            StringAssert.Contains("OWNED", shop.scannerButton.Text);

            var saved = new ProfileService(m_Storage);
            saved.Load();
            Assert.AreEqual(2, saved.Data.tranqDarts, "Purchases must be saved.");

            m_Director.Profile.Data.totalCredits = 5000;
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            Assert.IsFalse(m_Director.PurchaseShopItem(ShopItem.CutterCoolant), "The shop is closed during a contract.");
            Assert.IsFalse(m_Director.Profile.Data.cutterUpgrade);
        }

        [UnityTest]
        public IEnumerator TranquilizerDart_PutsTheCreatureToSleep_ThenItWakes()
        {
            m_Director.Profile.Data.tranqDarts = 2;
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(3);
            var drone = m_Director.enemies[0];
            drone.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            Physics.SyncTransforms();
            for (int i = 0; i < 40; i++) drone.Step(0.05f);
            Assert.AreEqual(EnemyState.Patrol, drone.State);

            var tranq = m_Player.tools.OfType<TranqTool>().First();
            Assert.IsNotNull(tranq.muzzle);

            // A dart fired at nothing is spent and does nothing to the creature.
            tranq.transform.SetParent(null);
            tranq.transform.SetPositionAndRotation(drone.transform.position + Vector3.right * 2f, Quaternion.LookRotation(Vector3.right));
            Physics.SyncTransforms();
            Assert.IsFalse(tranq.Fire());
            Assert.AreEqual(1, m_Director.Profile.Data.tranqDarts);
            Assert.IsFalse(drone.IsSedated);

            // Aim at it from two metres away: at its body, wherever it is clinging (it waits on the ceiling).
            yield return null;
            Vector3 bodyAt = drone.GetComponentInChildren<SphereCollider>().bounds.center;
            Vector3 from = bodyAt + Vector3.back * 2f + Vector3.down * 0.6f;
            tranq.transform.SetPositionAndRotation(from, Quaternion.LookRotation(bodyAt - from));
            Physics.SyncTransforms();
            Assert.IsTrue(tranq.Fire(), "A dart aimed at the creature should hit it.");
            Assert.AreEqual(0, m_Director.Profile.Data.tranqDarts);
            Assert.IsTrue(drone.IsSedated);
            Assert.AreEqual(EnemyState.Dormant, drone.State);

            // Asleep: it does not move, and noise does not rouse it.
            Vector3 asleepAt = drone.transform.position;
            m_Director.noise.Emit(new NoiseEvent(asleepAt + Vector3.forward, 1f, NoiseKind.Cutting, PlayerId.Local));
            float half = m_Director.Config.tranqSleepSeconds * 0.5f;
            for (float t = 0; t < half; t += 0.05f) drone.Step(0.05f);
            Assert.IsTrue(drone.IsSedated);
            Assert.AreEqual(asleepAt, drone.transform.position);

            // Then it wakes and goes back on patrol.
            for (float t = 0; t < half + 0.5f; t += 0.05f) drone.Step(0.05f);
            Assert.IsFalse(drone.IsSedated);
            Assert.AreEqual(EnemyState.Patrol, drone.State);

            // No darts left: the launcher just clicks.
            Assert.IsFalse(tranq.Fire());
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
        }

        [UnityTest]
        public IEnumerator Blackout_KillsTheWreckLightsAndSound_ThenPowerReturns()
        {
            var blackout = m_Director.GetComponent<BlackoutController>();
            Assert.IsNotNull(blackout);
            Assert.Greater(blackout.lights.Length, 5);
            Assert.Greater(blackout.glowStrips.Length, 3);

            m_Director.StartMission(PlayerId.Local);
            yield return Frames(3);
            Assert.IsFalse(blackout.IsBlackout, "A run does not start in the dark.");
            Assert.IsTrue(blackout.lights.All(l => l.enabled));
            float ambienceBefore = blackout.ambience[0].volume;
            Assert.Greater(ambienceBefore, 0f);
            var tugLights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Where(l => l.name.StartsWith("Tug Light")).ToArray();
            Assert.Greater(tugLights.Length, 0);

            blackout.TriggerNow(1.0f);
            Assert.IsTrue(blackout.IsBlackout);
            Assert.IsTrue(blackout.lights.All(l => !l.enabled), "Every wreck light should be out.");
            Assert.IsTrue(blackout.ambience.All(a => a.volume == 0f), "The ship should fall silent.");
            Assert.IsTrue(m_Director.enemies[0].Silenced, "The creature should make no sound in the dark.");
            Assert.IsTrue(tugLights.All(l => l.enabled), "The tug keeps its lights.");

            // It ends by itself.
            yield return new WaitForSeconds(2.2f);
            Assert.IsFalse(blackout.IsBlackout);
            Assert.IsTrue(blackout.lights.All(l => l.enabled), "The lights should be back.");
            Assert.AreEqual(ambienceBefore, blackout.ambience[0].volume, 0.001f);
            Assert.IsFalse(m_Director.enemies[0].Silenced);

            // And a run that ends mid-blackout is restored immediately.
            blackout.TriggerNow(30f);
            m_Director.Extract(PlayerId.Local);
            Assert.IsFalse(blackout.IsBlackout);
            Assert.IsTrue(blackout.lights.All(l => l.enabled));
        }
    }
}

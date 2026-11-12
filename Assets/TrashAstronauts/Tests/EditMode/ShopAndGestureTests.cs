using NUnit.Framework;
using TrashAstronauts.Core;
using UnityEngine;

namespace TrashAstronauts.Tests
{
    public class ShopTests
    {
        MissionConfig m_Config;
        ProfileService m_Profile;
        MemorySaveStorage m_Storage;

        [SetUp]
        public void SetUp()
        {
            m_Config = ScriptableObject.CreateInstance<MissionConfig>();
            m_Storage = new MemorySaveStorage();
            m_Profile = new ProfileService(m_Storage);
            m_Profile.Load();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(m_Config);

        [Test]
        public void TranqDarts_CostCredits_StackUpToTheLimit_AndPersist()
        {
            Assert.IsFalse(m_Profile.TryPurchase(ShopItem.TranqDart, m_Config), "No credits, no dart.");

            m_Profile.Data.totalCredits = m_Config.tranqDartCost * 20;
            for (int i = 0; i < m_Config.maxTranqDarts; i++) Assert.IsTrue(m_Profile.TryPurchase(ShopItem.TranqDart, m_Config));
            Assert.AreEqual(m_Config.maxTranqDarts, m_Profile.Data.tranqDarts);
            Assert.AreEqual(m_Config.tranqDartCost * (20 - m_Config.maxTranqDarts), m_Profile.Data.totalCredits);
            Assert.IsFalse(m_Profile.TryPurchase(ShopItem.TranqDart, m_Config), "Cannot carry more than the limit.");

            var later = new ProfileService(m_Storage);
            later.Load();
            Assert.AreEqual(m_Config.maxTranqDarts, later.Data.tranqDarts);
        }

        [Test]
        public void TranqDarts_AreUsedUpOneAtATime()
        {
            m_Profile.Data.tranqDarts = 2;
            Assert.IsTrue(m_Profile.TryConsumeTranqDart());
            Assert.IsTrue(m_Profile.TryConsumeTranqDart());
            Assert.IsFalse(m_Profile.TryConsumeTranqDart());
            Assert.AreEqual(0, m_Profile.Data.tranqDarts);
        }

        [Test]
        public void Upgrades_CanBeBoughtOnce_AndPersist()
        {
            m_Profile.Data.totalCredits = 10000;
            Assert.IsTrue(m_Profile.TryPurchase(ShopItem.ScannerRange, m_Config));
            Assert.IsTrue(m_Profile.TryPurchase(ShopItem.CutterCoolant, m_Config));
            Assert.IsFalse(m_Profile.TryPurchase(ShopItem.ScannerRange, m_Config));
            Assert.IsFalse(m_Profile.TryPurchase(ShopItem.CutterCoolant, m_Config));
            Assert.AreEqual(10000 - m_Config.scannerUpgradeCost - m_Config.cutterUpgradeCost, m_Profile.Data.totalCredits);

            var later = new ProfileService(m_Storage);
            later.Load();
            Assert.IsTrue(later.Data.scannerUpgrade);
            Assert.IsTrue(later.Data.cutterUpgrade);
        }

        [Test]
        public void CoolantUpgrade_MakesTheCutterRunLonger()
        {
            var tuning = new CutterTuning { secondsToOverheat = 4f };
            var stock = new CutterHeatModel(tuning);
            var cooled = new CutterHeatModel(tuning) { HeatCapacityMultiplier = 1.6f };
            for (float t = 0; t < 4.5f; t += 0.1f)
            {
                stock.Tick(0.1f, true);
                cooled.Tick(0.1f, true);
            }
            Assert.IsTrue(stock.Overheated);
            Assert.IsFalse(cooled.Overheated);
        }
    }

    public class HandGestureTests
    {
        static readonly Vector3 k_Palm = Vector3.zero;

        [Test]
        public void OpenHand_IsNotAGrip_AndAFist_Is()
        {
            float open = HandGestures.FistAmount(k_Palm, new Vector3(0, 0, 0.11f), new Vector3(0.02f, 0, 0.105f), new Vector3(0.04f, 0, 0.095f));
            float fist = HandGestures.FistAmount(k_Palm, new Vector3(0, -0.03f, 0.035f), new Vector3(0.02f, -0.03f, 0.035f), new Vector3(0.035f, -0.03f, 0.03f));
            Assert.Less(open, 0.2f);
            Assert.Greater(fist, 0.8f);
        }

        [Test]
        public void Pinch_RisesAsThumbAndIndexMeet()
        {
            Assert.AreEqual(0f, HandGestures.PinchAmount(Vector3.zero, new Vector3(0.08f, 0, 0)), 0.001f);
            Assert.AreEqual(1f, HandGestures.PinchAmount(Vector3.zero, new Vector3(0.01f, 0, 0)), 0.001f);
            float half = HandGestures.PinchAmount(Vector3.zero, new Vector3(0.039f, 0, 0));
            Assert.That(half, Is.InRange(0.3f, 0.7f));
        }

        [Test]
        public void Latch_DoesNotFlickerAroundTheThreshold()
        {
            bool pressed = false;
            pressed = HandGestures.Latch(pressed, 0.6f);
            Assert.IsFalse(pressed, "Not past the press threshold yet.");
            pressed = HandGestures.Latch(pressed, 0.75f);
            Assert.IsTrue(pressed);
            pressed = HandGestures.Latch(pressed, 0.55f);
            Assert.IsTrue(pressed, "Still held until it drops below the release threshold.");
            pressed = HandGestures.Latch(pressed, 0.3f);
            Assert.IsFalse(pressed);
        }
    }
}

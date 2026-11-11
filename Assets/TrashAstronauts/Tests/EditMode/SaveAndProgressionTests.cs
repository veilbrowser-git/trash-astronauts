using System.IO;
using NUnit.Framework;
using TrashAstronauts.Core;
using UnityEngine;

namespace TrashAstronauts.Tests
{
    public class SaveAndProgressionTests
    {
        MissionConfig m_Config;

        [SetUp]
        public void SetUp()
        {
            m_Config = ScriptableObject.CreateInstance<MissionConfig>();
            m_Config.baseOxygenSeconds = 600f;
            m_Config.oxygenUpgradeBonusSeconds = 180f;
            m_Config.oxygenUpgradeCost = 600;
            m_Config.maxOxygenUpgradeLevel = 1;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(m_Config);

        [Test]
        public void SaveThenLoad_RoundTripsEverything()
        {
            var storage = new MemorySaveStorage();
            var a = new ProfileService(storage);
            a.Load();
            a.Data.totalCredits = 1234;
            a.Data.oxygenUpgradeLevel = 1;
            a.Data.Handedness = Handedness.Left;
            a.Data.snapTurnDegrees = 60f;
            a.Data.seatedHeightOffset = 0.4f;
            a.Data.tutorialCompleted = true;
            a.Save();

            var b = new ProfileService(storage);
            b.Load();

            Assert.IsFalse(b.LoadedFromDefaults);
            Assert.AreEqual(1234, b.Data.totalCredits);
            Assert.AreEqual(1, b.Data.oxygenUpgradeLevel);
            Assert.AreEqual(Handedness.Left, b.Data.Handedness);
            Assert.AreEqual(60f, b.Data.snapTurnDegrees);
            Assert.AreEqual(0.4f, b.Data.seatedHeightOffset, 0.0001f);
            Assert.IsTrue(b.Data.tutorialCompleted);
        }

        [Test]
        public void MissingSave_StartsFromDefaults()
        {
            var profile = new ProfileService(new MemorySaveStorage());
            profile.Load();
            Assert.IsTrue(profile.LoadedFromDefaults);
            Assert.AreEqual(0, profile.Data.totalCredits);
            Assert.AreEqual(0, profile.Data.oxygenUpgradeLevel);
            Assert.AreEqual(Handedness.Right, profile.Data.Handedness);
            Assert.AreEqual(45f, profile.Data.snapTurnDegrees);
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("not json at all")]
        [TestCase("{\"totalCredits\": 12")]
        [TestCase("[1,2,3]")]
        [TestCase("null")]
        public void CorruptSave_FallsBackToDefaults_WithoutThrowing(string contents)
        {
            var profile = new ProfileService(new MemorySaveStorage { Contents = contents });
            Assert.DoesNotThrow(() => profile.Load());
            Assert.IsTrue(profile.LoadedFromDefaults);
            Assert.AreEqual(0, profile.Data.totalCredits);
        }

        [Test]
        public void OutOfRangeValues_AreSanitized()
        {
            var storage = new MemorySaveStorage
            {
                Contents = "{\"totalCredits\":-50,\"oxygenUpgradeLevel\":99,\"handedness\":7,\"snapTurnDegrees\":1000,\"seatedHeightOffset\":50}"
            };
            var profile = new ProfileService(storage, 1);
            profile.Load();
            Assert.AreEqual(0, profile.Data.totalCredits);
            Assert.AreEqual(1, profile.Data.oxygenUpgradeLevel);
            Assert.AreEqual(Handedness.Right, profile.Data.Handedness);
            Assert.AreEqual(90f, profile.Data.snapTurnDegrees);
            Assert.AreEqual(SaveData.MaxHeightOffset, profile.Data.seatedHeightOffset, 0.0001f);
        }

        [Test]
        public void FileStorage_RoundTrips_AndRecoversFromACorruptMainFile()
        {
            string dir = Path.Combine(Application.temporaryCachePath, "ta_save_test_" + System.Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "save.json");
            try
            {
                var storage = new FileSaveStorage(path);
                var profile = new ProfileService(storage);
                profile.Load();
                Assert.IsTrue(profile.LoadedFromDefaults);

                profile.Data.totalCredits = 300;
                profile.Save();
                profile.Data.totalCredits = 700;
                profile.Save(); // the 300 save is now the backup

                var reloaded = new ProfileService(new FileSaveStorage(path));
                reloaded.Load();
                Assert.AreEqual(700, reloaded.Data.totalCredits);

                File.WriteAllText(path, "{{{{ garbage");
                var recovered = new ProfileService(new FileSaveStorage(path));
                Assert.DoesNotThrow(() => recovered.Load());
                Assert.AreEqual(300, recovered.Data.totalCredits, "Should fall back to the backup save.");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Test]
        public void OxygenUpgrade_CostsCredits_Persists_AndIncreasesCapacity()
        {
            var storage = new MemorySaveStorage();
            var profile = new ProfileService(storage, m_Config.maxOxygenUpgradeLevel);
            profile.Load();
            Assert.AreEqual(600f, m_Config.GetOxygenCapacity(profile.Data.oxygenUpgradeLevel));

            profile.Data.totalCredits = 599;
            Assert.IsFalse(profile.TryPurchaseOxygenUpgrade(m_Config), "Cannot buy without enough credits.");

            profile.Data.totalCredits = 750;
            Assert.IsTrue(profile.TryPurchaseOxygenUpgrade(m_Config));
            Assert.AreEqual(150, profile.Data.totalCredits);
            Assert.AreEqual(1, profile.Data.oxygenUpgradeLevel);
            Assert.IsFalse(profile.TryPurchaseOxygenUpgrade(m_Config), "Only one upgrade level exists.");

            var later = new ProfileService(storage, m_Config.maxOxygenUpgradeLevel);
            later.Load();
            Assert.AreEqual(1, later.Data.oxygenUpgradeLevel);
            Assert.AreEqual(780f, m_Config.GetOxygenCapacity(later.Data.oxygenUpgradeLevel));
        }
    }
}

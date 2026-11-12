using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>The glowing lens of each ceiling fixture follows its lamp: dead lamps and blackouts leave it dark.</summary>
    public class LampLensPlayTests
    {
        MissionDirector m_Director;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            MissionDirector.StorageOverride = new MemorySaveStorage();
            MissionDirector.SeedOverride = 12;
            MissionDirector.CreatureOverride = 0;
            MissionDirector.MapOverride = null;
            MissionDirector.ConditionOverride = ContractCondition.Standard;
            BlackoutController.DamageOverride = 0f;
            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            yield return null;
            yield return null;
            m_Director = MissionDirector.Active;
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

        static void AssertLensesMatchLamps(LampLens[] lenses, string when)
        {
            foreach (var lens in lenses)
            {
                lens.Refresh();
                if (lens.lamp.isActiveAndEnabled) Assert.Greater(lens.Brightness, 0f, lens.name + " should glow while its lamp is on (" + when + ").");
                else Assert.Less(lens.Brightness, 0.02f, lens.name + " should be dark while its lamp is off (" + when + ").");
            }
        }

        [UnityTest]
        public IEnumerator FixtureLenses_GoDarkWithDeadLamps_AndInABlackout_AndComeBackAfterwards()
        {
            BlackoutController.DamageOverride = 1f;
            var blackout = m_Director.blackout;
            LampLens[] lenses = null;
            LampLens dead = null;
            for (int seed = 1; seed <= 20 && dead == null; seed++)
            {
                MissionDirector.MapOverride = seed % 3;
                MissionDirector.SeedOverride = seed;
                if (m_Director.Phase == MissionPhase.Active) m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
                Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
                yield return Frames(2);
                lenses = m_Director.ActiveMap.GetComponentsInChildren<LampLens>(true);
                dead = lenses.FirstOrDefault(l => blackout.IsDead(l.lamp));
            }
            Assert.IsNotNull(dead, "Some contract should have a dead lamp under a fixture.");
            dead.Refresh();
            Assert.Less(dead.Brightness, 0.02f, "A dead lamp's lens is dark.");
            AssertLensesMatchLamps(lenses, "normal");
            Assert.IsTrue(lenses.Any(l => l.Brightness > 0.2f), "Working lamps' lenses glow.");

            blackout.TriggerNow(2f);
            yield return null;
            foreach (var lens in lenses)
            {
                lens.Refresh();
                Assert.Less(lens.Brightness, 0.02f, lens.name + " should be dark in a blackout.");
            }

            blackout.EndBlackout(true);
            yield return null;
            AssertLensesMatchLamps(lenses, "after the blackout");
            Assert.IsTrue(lenses.Any(l => l.Brightness > 0.2f), "The lenses come back with the power.");
            dead.Refresh();
            Assert.Less(dead.Brightness, 0.02f, "A dead lamp stays dead after the blackout.");
        }
    }
}

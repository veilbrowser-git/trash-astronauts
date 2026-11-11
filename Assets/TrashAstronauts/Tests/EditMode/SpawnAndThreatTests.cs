using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using UnityEngine;

namespace TrashAstronauts.Tests
{
    public class SpawnPlannerTests
    {
        static readonly SalvageSpec[] k_Specs =
        {
            new SalvageSpec("scrap", SalvageCategory.SmallScrap, 40),
            new SalvageSpec("board", SalvageCategory.Electronics, 150),
            new SalvageSpec("core", SalvageCategory.ReactorComponent, 600),
            new SalvageSpec("toaster", SalvageCategory.Novelty, 350),
        };

        static List<SpawnPointInfo> Points()
        {
            var points = new List<SpawnPointInfo>
            {
                new SpawnPointInfo("reactor", SalvageCategoryMask.ReactorComponent, "panel_reactor"),
                new SpawnPointInfo("cage", SalvageCategoryMask.Electronics | SalvageCategoryMask.Novelty, "panel_cage"),
                new SpawnPointInfo("rack", SalvageCategoryMask.Electronics, "mount_rack"),
            };
            for (int i = 0; i < 8; i++) points.Add(new SpawnPointInfo("s" + i, SalvageCategoryMask.SmallScrap));
            for (int i = 0; i < 4; i++) points.Add(new SpawnPointInfo("e" + i, SalvageCategoryMask.Electronics | SalvageCategoryMask.SmallScrap));
            for (int i = 0; i < 3; i++) points.Add(new SpawnPointInfo("n" + i, SalvageCategoryMask.Novelty | SalvageCategoryMask.SmallScrap));
            return points;
        }

        static int Count(SalvageCategory c)
        {
            switch (c)
            {
                case SalvageCategory.SmallScrap: return 6;
                case SalvageCategory.Electronics: return 3;
                default: return 1;
            }
        }

        [Test]
        public void EverySeed_HasEnoughLootToExceedTheQuota()
        {
            var points = Points();
            for (int seed = 1; seed <= 300; seed++)
            {
                var plan = SalvageSpawnPlanner.Plan(points, k_Specs, Count, 900, 1.3f, seed, 1);
                Assert.IsFalse(plan.QuotaWasLowered, "seed " + seed);
                Assert.GreaterOrEqual(plan.TotalValue, 900 * 1.3f, "seed " + seed);
                Assert.AreEqual(plan.TotalValue, plan.Assignments.Sum(a => a.value));
            }
        }

        [Test]
        public void EachSpawnPoint_IsUsedAtMostOnce_AndCategoriesAreRespected()
        {
            var points = Points();
            var byId = points.ToDictionary(p => p.id);
            for (int seed = 1; seed <= 100; seed++)
            {
                var plan = SalvageSpawnPlanner.Plan(points, k_Specs, Count, 900, 1.3f, seed, 2);
                Assert.AreEqual(plan.Assignments.Count, plan.Assignments.Select(a => a.spawnPointId).Distinct().Count());
                Assert.AreEqual(plan.Assignments.Count, plan.Assignments.Select(a => a.salvageId).Distinct().Count());
                foreach (var a in plan.Assignments)
                    Assert.IsTrue(byId[a.spawnPointId].allowed.Allows(a.category), a.spawnPointId + " got " + a.category);
            }
        }

        [Test]
        public void ReactorComponent_AlwaysSpawnsBehindACuttablePanel()
        {
            var points = Points();
            for (int seed = 1; seed <= 100; seed++)
            {
                var plan = SalvageSpawnPlanner.Plan(points, k_Specs, Count, 900, 1.3f, seed, 1);
                var reactor = plan.Assignments.Where(a => a.category == SalvageCategory.ReactorComponent).ToList();
                Assert.AreEqual(1, reactor.Count);
                Assert.AreEqual("reactor", reactor[0].spawnPointId);
            }
        }

        [Test]
        public void SameSeed_GivesTheSamePlan_AndDifferentSeedsVary()
        {
            var points = Points();
            var a = SalvageSpawnPlanner.Plan(points, k_Specs, Count, 900, 1.3f, 42, 1);
            var b = SalvageSpawnPlanner.Plan(points, k_Specs, Count, 900, 1.3f, 42, 1);
            CollectionAssert.AreEqual(a.Assignments.Select(x => x.spawnPointId + x.definitionId), b.Assignments.Select(x => x.spawnPointId + x.definitionId));

            var layouts = new HashSet<string>();
            for (int seed = 1; seed <= 20; seed++)
            {
                var plan = SalvageSpawnPlanner.Plan(points, k_Specs, Count, 900, 1.3f, seed, 1);
                layouts.Add(string.Join(",", plan.Assignments.Select(x => x.spawnPointId).OrderBy(x => x)));
            }
            Assert.Greater(layouts.Count, 5, "Loot locations should change between runs.");
        }

        [Test]
        public void ImpossibleQuota_IsLoweredSoTheRunIsStillWinnable()
        {
            var points = new List<SpawnPointInfo> { new SpawnPointInfo("s0", SalvageCategoryMask.SmallScrap), new SpawnPointInfo("s1", SalvageCategoryMask.SmallScrap) };
            var plan = SalvageSpawnPlanner.Plan(points, k_Specs, Count, 900, 1.3f, 7, 1);
            Assert.IsTrue(plan.QuotaWasLowered);
            Assert.AreEqual(80, plan.TotalValue);
            Assert.LessOrEqual(plan.Quota * 1.3f, plan.TotalValue + 0.001f);
        }
    }

    public class EnemyBrainTests
    {
        static EnemyTuning Tuning() => new EnemyTuning
        {
            sightAcquireSeconds = 1f,
            hearingRadiusAtFullLoudness = 16f,
            investigateLingerSeconds = 4f,
            chaseLoseSightSeconds = 3f,
            searchSeconds = 6f,
        };

        static EnemyPercept See(Vector3 player, float dt = 0.1f, bool safe = false) =>
            new EnemyPercept { deltaTime = dt, canSeePlayer = true, playerPosition = player, playerInSafeZone = safe };

        static EnemyPercept Blind(float dt = 0.1f, bool arrived = false) =>
            new EnemyPercept { deltaTime = dt, canSeePlayer = false, atPointOfInterest = arrived, playerInSafeZone = false };

        static void Run(EnemyBrain brain, EnemyPercept percept, float seconds)
        {
            for (float t = 0; t < seconds; t += percept.deltaTime) brain.Tick(percept);
        }

        [Test]
        public void Dormant_IgnoresEverything()
        {
            var brain = new EnemyBrain(Tuning());
            Assert.IsFalse(brain.HearNoise(new NoiseEvent(Vector3.zero, 1f, NoiseKind.Cutting, PlayerId.Local), Vector3.zero));
            Run(brain, See(Vector3.one), 5f);
            Assert.AreEqual(EnemyState.Dormant, brain.State);
        }

        [Test]
        public void AudibleNoise_SendsThePatrolToInvestigate_ThenBackToPatrol()
        {
            var brain = new EnemyBrain(Tuning());
            brain.WakeUp();
            var noiseAt = new Vector3(5, 0, 0);

            Assert.IsTrue(brain.HearNoise(new NoiseEvent(noiseAt, 0.85f, NoiseKind.Cutting, PlayerId.Local), Vector3.zero));
            Assert.AreEqual(EnemyState.Investigate, brain.State);
            Assert.AreEqual(noiseAt, brain.PointOfInterest);

            Run(brain, Blind(0.1f, false), 10f);
            Assert.AreEqual(EnemyState.Investigate, brain.State, "Keeps investigating until it arrives.");
            Run(brain, Blind(0.1f, true), 4.5f);
            Assert.AreEqual(EnemyState.Patrol, brain.State);
        }

        [Test]
        public void QuietOrDistantNoise_IsIgnored()
        {
            var brain = new EnemyBrain(Tuning());
            brain.WakeUp();
            // loudness 0.3 carries 4.8 m
            Assert.IsFalse(brain.HearNoise(new NoiseEvent(new Vector3(10, 0, 0), 0.3f, NoiseKind.Impact, PlayerId.Local), Vector3.zero));
            Assert.AreEqual(EnemyState.Patrol, brain.State);
        }

        [Test]
        public void SeeingThePlayer_TakesAMomentBeforeTheChaseStarts()
        {
            var brain = new EnemyBrain(Tuning());
            brain.WakeUp();
            Run(brain, See(Vector3.forward * 5), 0.5f);
            Assert.AreEqual(EnemyState.Patrol, brain.State, "A brief glimpse must not start a chase.");
            Run(brain, See(Vector3.forward * 5), 0.7f);
            Assert.AreEqual(EnemyState.Chase, brain.State);
        }

        [Test]
        public void BreakingLineOfSight_EndsTheChase_ThenSearch_ThenPatrol()
        {
            var brain = new EnemyBrain(Tuning());
            brain.WakeUp();
            Run(brain, See(new Vector3(3, 0, 3)), 1.5f);
            Assert.AreEqual(EnemyState.Chase, brain.State);

            Run(brain, Blind(), 2f);
            Assert.AreEqual(EnemyState.Chase, brain.State);
            Run(brain, Blind(), 1.5f);
            Assert.AreEqual(EnemyState.Search, brain.State);
            Assert.AreEqual(new Vector3(3, 0, 3), brain.PointOfInterest, "Searches where it last saw the player.");

            Run(brain, Blind(0.1f, true), 6.5f);
            Assert.AreEqual(EnemyState.Patrol, brain.State);
        }

        [Test]
        public void PlayerInTheSafeZone_IsNeverChased_AndEndsAnActiveChase()
        {
            var brain = new EnemyBrain(Tuning());
            brain.WakeUp();
            Run(brain, See(Vector3.one, 0.1f, true), 10f);
            Assert.AreEqual(EnemyState.Patrol, brain.State);

            Run(brain, See(Vector3.one), 1.5f);
            Assert.AreEqual(EnemyState.Chase, brain.State);
            brain.Tick(See(Vector3.one, 0.1f, true));
            Assert.AreNotEqual(EnemyState.Chase, brain.State);
        }

        [Test]
        public void NoiseDuringAChase_DoesNotDistractIt()
        {
            var brain = new EnemyBrain(Tuning());
            brain.WakeUp();
            Run(brain, See(Vector3.one), 1.5f);
            Assert.IsFalse(brain.HearNoise(new NoiseEvent(Vector3.zero, 1f, NoiseKind.Impact, PlayerId.Local), Vector3.zero));
            Assert.AreEqual(EnemyState.Chase, brain.State);
        }
    }

    public class NoiseAndCutterTests
    {
        [Test]
        public void GentleBumps_MakeNoGameplayNoise_HardImpactsDo_WithACooldown()
        {
            var tuning = new NoiseTuning();
            Assert.IsFalse(NoiseRules.TryEvaluateImpact(0.3f, 100f, tuning, out _), "physics jitter");
            Assert.IsFalse(NoiseRules.TryEvaluateImpact(tuning.impactSpeedThreshold - 0.1f, 100f, tuning, out _));
            Assert.IsTrue(NoiseRules.TryEvaluateImpact(tuning.impactSpeedThreshold + 0.5f, 100f, tuning, out float soft));
            Assert.IsTrue(NoiseRules.TryEvaluateImpact(tuning.impactSpeedForFullLoudness + 3f, 100f, tuning, out float loud));
            Assert.Greater(loud, soft);
            Assert.AreEqual(1f, loud, 0.001f);
            Assert.IsFalse(NoiseRules.TryEvaluateImpact(20f, tuning.perObjectCooldown * 0.5f, tuning, out _), "cooldown");
        }

        [Test]
        public void Cutter_Overheats_LocksOut_ThenRecovers()
        {
            var tuning = new CutterTuning { secondsToOverheat = 4f, secondsToCool = 4f, restartHeatFraction = 0.5f };
            var heat = new CutterHeatModel(tuning);
            bool firing = true;
            for (float t = 0; t < 3.5f; t += 0.1f) firing = heat.Tick(0.1f, true);
            Assert.IsTrue(firing);
            Assert.IsFalse(heat.Overheated);

            for (float t = 0; t < 1f; t += 0.1f) firing = heat.Tick(0.1f, true);
            Assert.IsTrue(heat.Overheated);
            Assert.IsFalse(firing, "No flame while overheated, even with the trigger held.");

            for (float t = 0; t < 1f; t += 0.1f) firing = heat.Tick(0.1f, true);
            Assert.IsFalse(firing);
            for (float t = 0; t < 1.5f; t += 0.1f) heat.Tick(0.1f, false);
            Assert.IsFalse(heat.Overheated);
            Assert.IsTrue(heat.Tick(0.1f, true));
        }
    }
}

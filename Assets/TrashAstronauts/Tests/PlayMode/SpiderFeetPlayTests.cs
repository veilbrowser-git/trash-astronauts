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
    /// <summary>The spider's feet: planted on the floor, wall and ceiling, stepping in two alternating groups.</summary>
    public class SpiderFeetPlayTests
    {
        MissionDirector m_Director;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            MissionDirector.StorageOverride = new MemorySaveStorage();
            MissionDirector.SeedOverride = 91;
            MissionDirector.CreatureOverride = 0;
            MissionDirector.MapOverride = 0;
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

        static IEnumerator Seconds(float seconds)
        {
            for (float t = 0f; t < seconds; t += Time.deltaTime) yield return null;
        }

        /// <summary>How far a foot tip is from the surface under it (along the walker's normal).</summary>
        static float GapToSurface(Vector3 tip, Vector3 normal)
        {
            if (!Physics.Raycast(tip + normal * 0.3f, -normal, out RaycastHit hit, 0.8f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore)) return 99f;
            return Mathf.Abs(hit.distance - 0.3f);
        }

        IEnumerator StartWith(int kind)
        {
            MissionDirector.CreatureOverride = kind;
            if (m_Director.Phase == MissionPhase.Active) m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            m_Director.StartMission(PlayerId.Local);
            yield return null;
            yield return null;
            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            creature.enabled = false; // the test moves it by hand; its brain stays awake but does not walk it
            creature.transform.position = MapPoints.Freighter(0f, creature.hoverHeight, 12f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.forward);
            creature.GetComponent<SurfaceWalker>().ClimbOverride = 0f;
            yield return Seconds(0.2f);
        }

        static void AssertPlantedOnSurface(CreatureAnimator animator, SurfaceWalker walker, string where, string who)
        {
            for (int i = 0; i < animator.LegCount; i++)
            {
                Assert.IsTrue(animator.IsFootPlanted(i), who + " leg " + i + " should be planted " + where + ". " + animator.DescribeLeg(i));
                float gap = GapToSurface(animator.FootTip(i), walker.SurfaceNormal);
                Assert.Less(gap, 0.04f, who + " leg " + i + " tip should touch the " + where + "; gap " + gap + " " + animator.DescribeLeg(i));
            }
        }

        [UnityTest]
        public IEnumerator WalkingAlongTheFloor_PlantedFeetDoNotSlide_AndTheGroupsAlternate()
        {
            for (int kind = 0; kind < 4; kind++)
            {
                yield return StartWith(kind);
                var creature = m_Director.ActiveEnemy;
                var walker = creature.GetComponent<SurfaceWalker>();
                var animator = creature.GetComponent<CreatureAnimator>();
                string who = creature.displayName;
                Assert.IsTrue(animator.HasFootIk, who + " should have feet to plant.");
                Assert.AreEqual(8, animator.LegCount);
                for (float t = 0f; t < 12f && walker.Climb > 0.001f; t += Time.deltaTime) yield return null;
                yield return Seconds(2f);
                Assert.AreEqual(CrawlSurface.Floor, walker.Current);
                Assert.Greater(animator.FootIkWeight, 0.99f, who + " awake on the floor, its feet should be planted.");
                AssertPlantedOnSurface(animator, walker, "floor", who);

                // Walk it down the corridor.
                var anchor = new Vector3[8];
                var wasPlanted = new bool[8];
                for (int i = 0; i < 8; i++) { anchor[i] = animator.FootTip(i); wasPlanted[i] = true; }
                int stepsBefore = animator.StepEvents, raysBefore = animator.FootRaycasts;
                float worstDrift = 0f, worstGap = 0f;
                for (float t = 0f; t < 3f; t += Time.deltaTime)
                {
                    creature.transform.position += Vector3.forward * 0.9f * Time.deltaTime;
                    yield return null;
                    int air0 = 0, air1 = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        bool planted = animator.IsFootPlanted(i);
                        if (!planted) { if (animator.LegGroup(i) == 0) air0++; else air1++; }
                        if (planted && wasPlanted[i]) worstDrift = Mathf.Max(worstDrift, Vector3.Distance(anchor[i], animator.FootTip(i)));
                        if (planted && !wasPlanted[i]) anchor[i] = animator.FootTip(i);
                        if (planted) worstGap = Mathf.Max(worstGap, GapToSurface(animator.FootTip(i), walker.SurfaceNormal));
                        wasPlanted[i] = planted;
                    }
                    Assert.IsFalse(air0 > 0 && air1 > 0, who + ": both groups of legs were in the air at once.");
                }
                int steps = animator.StepEvents - stepsBefore;
                Assert.Greater(steps, 4, who + " should step as it walks.");
                Assert.Less(worstDrift, 0.02f, who + ": a planted foot slid " + worstDrift + " m.");
                Assert.Less(worstGap, 0.04f, who + ": a planted foot was " + worstGap + " m off the floor.");
                Assert.LessOrEqual(animator.FootRaycasts - raysBefore, steps * 8, "At most eight raycasts per step.");

                // It stops: the feet settle and stay put, the body only breathes.
                yield return Seconds(1.2f);
                AssertPlantedOnSurface(animator, walker, "floor", who);
                for (int i = 0; i < 8; i++) anchor[i] = animator.FootTip(i);
                stepsBefore = animator.StepEvents;
                yield return Seconds(1f);
                Assert.AreEqual(stepsBefore, animator.StepEvents, who + " standing still should not keep stepping.");
                for (int i = 0; i < 8; i++)
                    Assert.Less(Vector3.Distance(anchor[i], animator.FootTip(i)), 0.01f, who + " standing still, its feet stay put.");
            }
        }

        [UnityTest]
        public IEnumerator OnTheWallAndCeiling_EveryFootTouchesTheSurface()
        {
            yield return StartWith(0);
            var creature = m_Director.ActiveEnemy;
            var walker = creature.GetComponent<SurfaceWalker>();
            var animator = creature.GetComponent<CreatureAnimator>();
            for (float t = 0f; t < 12f && walker.Climb > 0.001f; t += Time.deltaTime) yield return null;
            yield return Seconds(2f);

            walker.ClimbOverride = 0.5f;
            for (float t = 0f; t < 12f && !(walker.Current == CrawlSurface.Wall && Mathf.Abs(walker.Climb - 0.5f) < 0.01f); t += Time.deltaTime) yield return null;
            yield return Seconds(1.5f);
            Assert.AreEqual(CrawlSurface.Wall, walker.Current);
            Assert.Greater(animator.FootIkWeight, 0.99f);
            AssertPlantedOnSurface(animator, walker, "wall", creature.displayName);

            walker.ClimbOverride = 1f;
            for (float t = 0f; t < 12f && walker.Climb < 0.999f; t += Time.deltaTime) yield return null;
            yield return Seconds(1.5f);
            Assert.AreEqual(CrawlSurface.Ceiling, walker.Current);
            AssertPlantedOnSurface(animator, walker, "ceiling", creature.displayName);
            walker.ClimbOverride = null;
        }
    }
}

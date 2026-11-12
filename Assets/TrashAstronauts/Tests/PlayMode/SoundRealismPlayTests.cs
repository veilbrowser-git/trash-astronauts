using System.Collections;
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
    /// <summary>Helmet breathing and heartbeat that follow effort and fear; sound muffled through walls.</summary>
    public class SoundRealismPlayTests
    {
        [TearDown]
        public void TearDown()
        {
            MissionDirector.StorageOverride = null;
            MissionDirector.SeedOverride = null;
            MissionDirector.MapOverride = null;
            MissionDirector.ConditionOverride = null;
            BlackoutController.DamageOverride = null;
        }

        static SuitAudio MakeSuit(out GameObject go)
        {
            go = new GameObject("Suit Audio Test");
            var clip = AudioClip.Create("test", 44100, 1, 44100, false);
            var suit = go.AddComponent<SuitAudio>();
            suit.enabled = false; // driven by Tick only
            suit.breathing = go.AddComponent<AudioSource>();
            suit.breathing.clip = clip; suit.breathing.loop = true; suit.breathing.volume = 0f; suit.breathing.pitch = 1f;
            var hb = new GameObject("Heartbeat");
            hb.transform.SetParent(go.transform);
            suit.heartbeat = hb.AddComponent<AudioSource>();
            suit.heartbeat.clip = clip; suit.heartbeat.loop = true; suit.heartbeat.volume = 0f; suit.heartbeat.pitch = 1f;
            return suit;
        }

        static void Run(SuitAudio suit, float seconds, float effort, float threat, ref float maxRateStep)
        {
            const float dt = 1f / 72f;
            for (float t = 0f; t < seconds; t += dt)
            {
                float before = suit.BreathRate;
                suit.Tick(dt, effort, threat);
                maxRateStep = Mathf.Max(maxRateStep, Mathf.Abs(suit.BreathRate - before));
            }
        }

        [Test]
        public void Threat_IsZeroWhenDormant_HighWhenChasing_OnlyNearCounts()
        {
            Assert.AreEqual(0f, SuitAudio.ThreatOf(EnemyState.Dormant, 0.5f, 9f));
            Assert.Less(SuitAudio.ThreatOf(EnemyState.Patrol, 8f, 9f), 0.05f);
            Assert.Greater(SuitAudio.ThreatOf(EnemyState.Patrol, 1f, 9f), 0.7f);
            Assert.GreaterOrEqual(SuitAudio.ThreatOf(EnemyState.Chase, 30f, 9f), 0.85f);
        }

        [Test]
        public void Breathing_And_Heartbeat_FollowEffortAndFear_Smoothly_AndSettleSlowly()
        {
            var suit = MakeSuit(out var go);
            try
            {
                float step = 0f;
                Run(suit, 8f, 0f, 0f, ref step);
                float restRate = suit.BreathRate, restVolume = suit.breathing.volume;
                Assert.Less(suit.Fear, 0.01f);
                Assert.Less(suit.heartbeat.volume, 0.005f, "No heartbeat at rest.");

                step = 0f;
                Run(suit, 3f, 1f, 0f, ref step);
                Assert.Greater(suit.BreathRate, restRate * 1.35f, "Hard work quickens breathing.");
                Assert.Greater(suit.breathing.volume, restVolume * 1.5f, "Hard work makes breathing louder.");
                Assert.LessOrEqual(suit.breathing.volume, suit.breathingVolume + 1e-4f, "Breathing stays modest.");
                Assert.Less(step, 0.5f, "Breath rate changes smoothly, never in steps.");
                Assert.Less(suit.heartbeat.volume, 0.005f, "Effort alone does not bring the heartbeat in.");
                float workRate = suit.BreathRate;

                Run(suit, 1.5f, 0f, 0f, ref step);
                Assert.Greater(suit.BreathRate, restRate + 0.4f * (workRate - restRate), "Breathing settles slowly.");
                Run(suit, 12f, 0f, 0f, ref step);
                Assert.Less(suit.BreathRate, restRate + 0.6f, "...but it does settle.");

                // The spider gives chase.
                step = 0f;
                float chase = SuitAudio.ThreatOf(EnemyState.Chase, 12f, suit.threatRadius);
                Run(suit, 3f, 0f, chase, ref step);
                Assert.Greater(suit.Fear, 0.8f);
                Assert.Greater(suit.BreathRate, restRate * 1.3f, "Fear quickens breathing too.");
                Assert.Greater(suit.heartbeat.volume, 0.5f * suit.heartbeatVolume, "Heartbeat fades in during a chase.");
                Assert.LessOrEqual(suit.heartbeat.volume, suit.heartbeatVolume + 1e-4f);
                Assert.Greater(suit.HeartRate, 60f * 1.3f, "Heart races with fear.");
                Assert.Less(step, 0.5f);

                Run(suit, 2f, 0f, 0f, ref step);
                Assert.Greater(suit.Fear, 0.5f, "Fear lingers after the chase.");
                Run(suit, 20f, 0f, 0f, ref step);
                Assert.Less(suit.Fear, 0.02f);
                Assert.Less(suit.heartbeat.volume, 0.01f, "The heartbeat fades out once calm.");
            }
            finally { Object.Destroy(go); }
        }

        [UnityTest]
        public IEnumerator InTheTug_WithNoCreatureAwake_HelmetIsCalm()
        {
            MissionDirector.StorageOverride = new MemorySaveStorage();
            MissionDirector.SeedOverride = 31;
            MissionDirector.MapOverride = 0;
            MissionDirector.ConditionOverride = ContractCondition.Standard;
            BlackoutController.DamageOverride = 0f;
            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            for (int i = 0; i < 90; i++) yield return null;
            var suit = Object.FindFirstObjectByType<SuitAudio>();
            Assert.IsNotNull(suit);
            Assert.AreNotEqual(MissionPhase.Active, MissionDirector.Active.Phase);
            Assert.Less(suit.Fear, 0.01f);
            Assert.Less(suit.heartbeat.volume, 0.01f);
            Assert.Less(suit.BreathRate, suit.clipBreathsPerMinute * 1.1f, "Resting breath in the tug.");

        }
        }

    }
}

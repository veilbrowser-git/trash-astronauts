using System.Collections;
using NUnit.Framework;
using TrashAstronauts.Audio;
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

            foreach (var enemy in MissionDirector.Active.enemies)
            {
                Assert.IsNotNull(enemy.hum.GetComponent<SoundOcclusion>(), enemy.name + " hum");
                Assert.IsNotNull(enemy.voice.GetComponent<SoundOcclusion>(), enemy.name + " voice");
                var walker = enemy.GetComponent<SurfaceWalker>();
                if (walker != null) Assert.IsNotNull(walker.steps.GetComponent<SoundOcclusion>(), enemy.name + " footsteps");
            }
            var room = Object.FindFirstObjectByType<RoomAcoustics>();
            Assert.IsNotNull(room, "The listener measures the room.");
            Assert.IsNotNull(room.zone);
            Assert.IsTrue(suit.breathing.bypassReverbZones, "Helmet sounds stay dry.");
        }

        [Test]
        public void SoundBehindAWall_IsMuffledAndQuieter_ClearInLineOfSight()
        {
            var listener = new GameObject("Test Listener").transform;
            listener.position = new Vector3(200f, 200f, 200f);
            var sourceGo = new GameObject("Test Source");
            sourceGo.transform.position = listener.position + new Vector3(0f, 0f, 6f);
            var source = sourceGo.AddComponent<AudioSource>();
            source.spatialBlend = 1f; source.maxDistance = 15f;
            var lowPass = sourceGo.AddComponent<AudioLowPassFilter>();
            var occ = sourceGo.AddComponent<SoundOcclusion>();
            occ.lowPass = lowPass;
            occ.listenerOverride = listener;
            occ.enabled = false; // stepped by hand
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.layer = GameLayers.Environment;
            wall.transform.position = listener.position + new Vector3(0f, 0f, 3f);
            wall.transform.localScale = new Vector3(4f, 4f, 0.2f);
            try
            {
                Physics.SyncTransforms();
                const float dt = 1f / 72f;
                for (int i = 0; i < 72; i++) occ.Step(dt);
                Assert.IsTrue(occ.Blocked);
                Assert.Greater(occ.Occlusion, 0.95f);
                Assert.Less(occ.Cutoff, 1500f, "Muffled behind the wall.");
                Assert.Less(occ.VolumeFactor, 0.6f, "Quieter behind the wall.");
                Assert.IsTrue(lowPass.enabled);
                Assert.Less(lowPass.cutoffFrequency, 1500f);

                // Smooth: a quarter of the way back after a couple of frames, never a jump.
                wall.SetActive(false);
                Physics.SyncTransforms();
                occ.Check();
                float before = occ.Occlusion;
                occ.Step(dt);
                Assert.Less(before - occ.Occlusion, 0.15f, "Occlusion changes smoothly.");
                for (int i = 0; i < 72; i++) occ.Step(dt);
                Assert.IsFalse(occ.Blocked);
                Assert.Less(occ.Occlusion, 0.01f);
                Assert.Greater(occ.Cutoff, 18000f, "Clear in line of sight.");
                Assert.Greater(occ.VolumeFactor, 0.99f);
                Assert.IsFalse(lowPass.enabled);

                // A wall on another layer (a crate, the player) does not count.
                wall.SetActive(true);
                wall.layer = GameLayers.Salvage;
                Physics.SyncTransforms();
                for (int i = 0; i < 72; i++) occ.Step(dt);
                Assert.IsFalse(occ.Blocked);
            }
            finally
            {
                Object.Destroy(wall);
                Object.Destroy(sourceGo);
                Object.Destroy(listener.gameObject);
            }
        }

        static GameObject Box(Vector3 center, float size)
        {
            // A hollow cube of six thin walls on the Environment layer.
            var root = new GameObject("Test Room");
            root.transform.position = center;
            float h = size * 0.5f, t = 0.1f;
            var faces = new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            foreach (var n in faces)
            {
                var wall = new GameObject("Wall");
                wall.layer = GameLayers.Environment;
                wall.transform.SetParent(root.transform, false);
                wall.transform.localPosition = n * h;
                var box = wall.AddComponent<BoxCollider>();
                box.size = new Vector3(n.x != 0f ? t : size, n.y != 0f ? t : size, n.z != 0f ? t : size);
            }
            return root;
        }

        [Test]
        public void RoomSize_IsMeasured_AndBigSpacesGetALongerReverbTail()
        {
            var small = Box(new Vector3(-300f, 300f, 300f), 1.4f);
            var large = Box(new Vector3(300f, 300f, 300f), 20f);
            var listener = new GameObject("Test Room Listener");
            var zoneGo = new GameObject("Test Reverb");
            var zone = zoneGo.AddComponent<AudioReverbZone>();
            var room = listener.AddComponent<RoomAcoustics>();
            room.enabled = false;
            room.zone = zone;
            try
            {
                const float dt = 1f / 72f;
                listener.transform.position = small.transform.position;
                Physics.SyncTransforms();
                for (int i = 0; i < 360; i++) room.Step(dt);
                float smallSize = room.RoomSize, smallDecay = zone.decayTime;
                int smallRoom = zone.room;
                Assert.Less(smallSize, 1.5f, "A duct-sized box measures small.");

                listener.transform.position = large.transform.position;
                Physics.SyncTransforms();
                room.Step(dt);
                Assert.Less(room.RoomSize, smallSize + 3f, "The size changes smoothly, not in one step.");
                for (int i = 0; i < 720; i++) room.Step(dt);
                float largeSize = room.RoomSize;
                Assert.Greater(largeSize, 8f, "A hold-sized box measures large.");
                Assert.Greater(zone.decayTime, smallDecay + 1f, "A big hold has a longer tail.");
                Assert.Greater(zone.room, smallRoom, "...and a little more reverb.");
                Assert.LessOrEqual(zone.decayTime, room.maxDecay + 1e-3f, "Kept subtle.");
                Assert.LessOrEqual(zone.room, room.maxRoom);
                Assert.AreEqual(listener.transform.position, zone.transform.position, "The zone follows the listener.");
            }
            finally
            {
                Object.Destroy(small);
                Object.Destroy(large);
                Object.Destroy(listener);
                Object.Destroy(zoneGo);
            }
        }
    }
}

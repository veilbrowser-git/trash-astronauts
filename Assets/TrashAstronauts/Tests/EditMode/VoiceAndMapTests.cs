using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TrashAstronauts.Tests
{
    public class VoiceAndMapTests
    {
        const string ScenePath = "Assets/TrashAstronauts/Scenes/TrashAstronauts.unity";

        static float Rms(float db) => Mathf.Pow(10f, db / 20f);

        [Test]
        public void Voice_SilenceAndSteadyBackgroundNoise_AreNotHeard()
        {
            var meter = new VoiceMeter();
            int emits = 0;
            for (int i = 0; i < 600; i++) if (meter.Tick(Rms(-70f), 0.02f, out _)) emits++;
            Assert.AreEqual(0, emits, "Silence must not count.");
            // A loud fan: constant -38 dB. The background estimate rises to meet it.
            meter.Reset();
            for (int i = 0; i < 3000; i++) meter.Tick(Rms(-38f), 0.02f, out _);
            emits = 0;
            for (int i = 0; i < 600; i++) if (meter.Tick(Rms(-38f), 0.02f, out _)) emits++;
            Assert.AreEqual(0, emits, "A steady background must not count as speech.");
            Assert.AreEqual(0f, meter.Loudness);
        }

        [Test]
        public void Voice_TalkingIsHeard_ShoutingIsLouder_AWhisperIsNot_AndAClickIsIgnored()
        {
            var meter = new VoiceMeter();
            for (int i = 0; i < 200; i++) meter.Tick(Rms(-60f), 0.02f, out _);

            float Speak(float db, float seconds)
            {
                float loudest = 0f;
                for (float t = 0f; t < seconds; t += 0.02f)
                    if (meter.Tick(Rms(db), 0.02f, out float l)) loudest = Mathf.Max(loudest, l);
                for (int i = 0; i < 100; i++) meter.Tick(Rms(-60f), 0.02f, out _);
                return loudest;
            }

            Assert.AreEqual(0f, Speak(-50f, 1f), "A whisper (10 dB over the room) should not carry.");
            float talk = Speak(-32f, 1f);
            float shout = Speak(-14f, 1f);
            Assert.Greater(talk, 0.3f, "Normal speech should be heard.");
            Assert.Less(talk, 0.9f);
            Assert.Greater(shout, talk + 0.2f, "Shouting should carry further than talking.");
            Assert.AreEqual(1f, shout, 0.05f);

            // One loud frame (a click or a bumped headset) is too short to count.
            bool heard = meter.Tick(Rms(-10f), 0.02f, out _);
            for (int i = 0; i < 50; i++) heard |= meter.Tick(Rms(-60f), 0.02f, out _);
            Assert.IsFalse(heard);
        }

        [Test]
        public void Voice_ReportsAtMostAboutTwiceASecond_WhileSpeaking()
        {
            var meter = new VoiceMeter();
            for (int i = 0; i < 200; i++) meter.Tick(Rms(-60f), 0.02f, out _);
            int emits = 0;
            for (int i = 0; i < 250; i++) if (meter.Tick(Rms(-25f), 0.02f, out _)) emits++; // 5 seconds
            Assert.That(emits, Is.InRange(8, 12));
        }

        static float[] Tone(int rate, float seconds, float amplitude)
        {
            var samples = new float[Mathf.RoundToInt(rate * seconds)];
            for (int i = 0; i < samples.Length; i++) samples[i] = amplitude * Mathf.Sin(2f * Mathf.PI * 220f * i / rate);
            return samples;
        }

        static void Feed(PhraseRecorder recorder, float[] samples, bool speaking)
        {
            var block = new float[256];
            for (int i = 0; i + block.Length <= samples.Length; i += block.Length)
            {
                System.Array.Copy(samples, i, block, 0, block.Length);
                recorder.Feed(block, block.Length, speaking);
            }
        }

        [Test]
        public void PhraseRecorder_CutsSpeechIntoPhrases_KeepsOnlyTheLastFew_AndForgetsOnClear()
        {
            const int rate = 16000;
            var recorder = new PhraseRecorder(rate);
            Feed(recorder, new float[rate], false);
            Assert.AreEqual(0, recorder.Count, "Silence is not a phrase.");

            // One second of speech, then silence.
            Feed(recorder, Tone(rate, 1f, 0.2f), true);
            Assert.IsTrue(recorder.IsRecording);
            Assert.AreEqual(0, recorder.Count, "The phrase is not finished until the speaker stops.");
            Feed(recorder, new float[rate], false);
            Assert.AreEqual(1, recorder.Count);
            Assert.AreEqual(1, recorder.Version);
            float seconds = recorder.Newest.Length / (float)rate;
            Assert.That(seconds, Is.InRange(1.1f, 1.5f), "The phrase is the speech plus a little before and after.");
            Assert.AreEqual(0.9f, recorder.Newest.Max(v => Mathf.Abs(v)), 0.02f, "Phrases are levelled.");
            Assert.AreEqual(0f, recorder.Newest[0], 1e-4f, "Phrases fade in so they do not click.");

            // A cough is too short to keep.
            Feed(recorder, Tone(rate, 0.12f, 0.3f), true);
            Feed(recorder, new float[rate], false);
            Assert.AreEqual(1, recorder.Count);

            // Someone who never stops talking is cut off at the maximum length.
            Feed(recorder, Tone(rate, 6f, 0.2f), true);
            Assert.GreaterOrEqual(recorder.Count, 2);
            Assert.LessOrEqual(recorder.Get(1).Length, Mathf.RoundToInt(rate * (recorder.maxSeconds + 0.1f)));
            Feed(recorder, new float[rate], false);

            // Only the most recent few are kept.
            for (int i = 0; i < 8; i++)
            {
                Feed(recorder, Tone(rate, 0.5f, 0.2f), true);
                Feed(recorder, new float[rate], false);
            }
            Assert.AreEqual(recorder.maxPhrases, recorder.Count);

            recorder.Clear();
            Assert.AreEqual(0, recorder.Count);
            Assert.IsNull(recorder.Newest);
        }

        [Test]
        public void ThreeWrecks_AreInTheScene_OnlyTheFirstIsDocked_AndEachIsComplete()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var director = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<MissionDirector>(true)).Single();
            Assert.AreEqual(3, director.maps.Length);
            Assert.AreEqual(3, director.maps.Select(m => m.mapId).Distinct().Count());
            Assert.AreEqual(3, director.maps.Select(m => m.displayName).Distinct().Count());
            Assert.IsTrue(director.maps[0].gameObject.activeSelf);
            Assert.IsFalse(director.maps[1].gameObject.activeSelf);
            Assert.IsFalse(director.maps[2].gameObject.activeSelf);
            Assert.IsNotNull(director.blackout);
            Assert.AreEqual(1, director.sharedDoors.Length);
            Assert.AreEqual(1, director.sharedCuttables.Length);

            var specs = AssetDatabase.FindAssets("t:SalvageDefinition")
                .Select(g => AssetDatabase.LoadAssetAtPath<SalvageDefinition>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(d => !d.bonusSpawn).Select(d => d.ToSpec()).ToList();
            var config = director.Config;
            var allIds = new System.Collections.Generic.HashSet<string>();
            foreach (var map in director.maps)
            {
                Assert.GreaterOrEqual(map.spawnPoints.Length, 18, map.displayName);
                Assert.GreaterOrEqual(map.cuttables.Length, 4, map.displayName + " needs a core housing, a locker, a rack and a shortcut.");
                Assert.IsNotNull(map.dock, map.displayName);
                Assert.Greater(map.graph.Length, 15, map.displayName);
                Assert.Greater(map.patrolRoute.Length, 20, map.displayName);
                Assert.IsTrue(map.patrolRoute.All(n => n != null && map.graph.Contains(n)), map.displayName);
                Assert.GreaterOrEqual(map.doors.Length, 1, map.displayName);
                Assert.Greater(map.lights.Length, 8, map.displayName);
                Assert.IsTrue(map.lights.All(l => l != null) && map.glowStrips.All(r => r != null));
                foreach (var point in map.spawnPoints)
                {
                    Assert.IsTrue(allIds.Add(point.pointId), "Duplicate spawn point id " + point.pointId);
                    if (point.Gate != null) Assert.IsTrue(map.cuttables.Contains(point.Gate), point.pointId + " is gated by a panel from another wreck.");
                }
                Assert.AreEqual(map.cuttables.Length, map.cuttables.Select(c => c.panelId).Distinct().Count());
                Assert.IsTrue(map.spawnPoints.Any(p => (p.allowed & SalvageCategoryMask.ReactorComponent) != 0), map.displayName + " needs a reactor core spot.");

                // Every wreck can always pay the quota, whatever the seed.
                var points = map.spawnPoints.Select(p => p.ToInfo()).ToList();
                for (int seed = 1; seed <= 300; seed++)
                {
                    var plan = SalvageSpawnPlanner.Plan(points, specs, config.GetCount, config.quotaCredits, config.quotaSafetyMargin, seed, 1);
                    Assert.IsFalse(plan.QuotaWasLowered, map.displayName + " seed " + seed);
                }

                // All of it fits inside the playable volume.
                foreach (var point in map.spawnPoints) Assert.IsTrue(director.bounds.Contains(point.transform.position), point.pointId);
                foreach (var node in map.graph) Assert.IsTrue(director.bounds.Contains(node.transform.position), map.displayName + " " + node.name);
            }
        }
    }
}

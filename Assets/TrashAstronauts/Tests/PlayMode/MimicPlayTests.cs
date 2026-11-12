using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>The Mimic: it keeps what the player says (in memory, only while it is aboard) and says it back.</summary>
    public class MimicPlayTests
    {
        const int Mimic = 3;
        MissionDirector m_Director;
        PlayerContext m_Player;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            MissionDirector.StorageOverride = new MemorySaveStorage();
            MissionDirector.SeedOverride = 21;
            MissionDirector.CreatureOverride = Mimic;
            MissionDirector.MapOverride = 0;
            MissionDirector.ConditionOverride = ContractCondition.Standard;
            BlackoutController.DamageOverride = 0f;
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
            BlackoutController.DamageOverride = null;
        }

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        static float[] Phrase(float seconds, float hz)
        {
            var samples = new float[Mathf.RoundToInt(16000 * seconds)];
            for (int i = 0; i < samples.Length; i++) samples[i] = 0.3f * Mathf.Sin(2f * Mathf.PI * hz * i / 16000f);
            return samples;
        }

        [UnityTest]
        public IEnumerator Mimic_AnswersWithWhatYouJustSaid_FromWhereItIs_AndTheMicIgnoresItsVoice()
        {
            var voice = m_Player.voice;
            voice.LevelOverride = 0.0005f; // a quiet room; no real microphone is used in tests
            Assert.IsFalse(voice.CaptureActive, "Nothing is kept while in the hub.");

            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            yield return Frames(3);
            var creature = m_Director.ActiveEnemy;
            Assert.AreEqual("THE MIMIC", creature.displayName);
            var mimic = creature.GetComponent<VoiceMimic>();
            Assert.IsNotNull(mimic);
            Assert.IsTrue(voice.CaptureActive, "With a Mimic aboard, phrases are kept for it.");

            // It says nothing until it has heard something, and nothing while it is still dormant.
            voice.InjectPhrase(Phrase(0.8f, 220f));
            for (float t = 0f; t < 3.5f; t += Time.deltaTime) yield return null;
            Assert.AreEqual(EnemyState.Dormant, creature.State);
            Assert.AreEqual(0, mimic.PlaybackCount, "A dormant Mimic is silent.");

            creature.WakeNow();
            for (float t = 0f; t < 4f && mimic.PlaybackCount == 0; t += Time.deltaTime) yield return null;
            Assert.AreEqual(1, mimic.PlaybackCount, "It should answer within a few seconds of hearing a phrase.");
            Assert.IsTrue(mimic.IsSpeaking);
            Assert.AreEqual(voice.Recorder.Newest.Length, mimic.speaker.clip.samples, "It plays back the phrase it heard.");
            Assert.AreEqual(1f, mimic.speaker.spatialBlend);
            Assert.Less(Vector3.Distance(mimic.speaker.transform.position, creature.GetComponent<SurfaceWalker>().surface.position), 1f, "The voice comes from the creature's body.");
            Assert.That(mimic.LastPlaybackSeconds, Is.InRange(0.85f, 1.1f), "Played a little slower and lower than it was said.");
            Assert.IsTrue(voice.IsSuppressed, "The headset microphone must not treat the Mimic's voice as the player talking.");

            // While it talks, a loud microphone level raises no noise (it is hearing itself through the speakers).
            int before = voice.EmitCount;
            voice.LevelOverride = 0.3f;
            for (float t = 0f; t < 0.5f; t += Time.deltaTime) yield return null;
            Assert.AreEqual(before, voice.EmitCount);
            voice.LevelOverride = 0.0005f;

            // A new phrase gets a new answer.
            for (float t = 0f; t < 2f && (mimic.IsSpeaking || voice.IsSuppressed); t += Time.deltaTime) yield return null;
            voice.InjectPhrase(Phrase(0.5f, 330f));
            for (float t = 0f; t < 4f && mimic.PlaybackCount < 2; t += Time.deltaTime) yield return null;
            Assert.AreEqual(2, mimic.PlaybackCount);
            Assert.AreEqual(Mathf.RoundToInt(16000 * 0.5f), mimic.speaker.clip.samples);
        }

        [UnityTest]
        public IEnumerator Phrases_AreOnlyKeptForAMimic_AndForgottenWhenTheContractEnds_OrTheMicIsSwitchedOff()
        {
            var voice = m_Player.voice;
            voice.LevelOverride = 0.0005f;
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(3);
            voice.InjectPhrase(Phrase(0.6f, 220f));
            voice.InjectPhrase(Phrase(0.6f, 260f));
            Assert.AreEqual(2, voice.Recorder.Count);

            // The contract ends: nothing asks for phrases any more, so they are dropped.
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            for (float t = 0f; t < 2f; t += Time.unscaledDeltaTime) yield return null;
            Assert.IsFalse(voice.CaptureActive);
            Assert.AreEqual(0, voice.Recorder.Count, "Phrases must be forgotten when the contract ends.");

            // A different creature aboard: phrases are never kept at all.
            MissionDirector.CreatureOverride = 0;
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(5);
            Assert.IsFalse(voice.CaptureActive, "Only the Mimic may keep phrases.");
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);

            // Mimic aboard but MIC switched off in the suit menu: nothing is kept and it stays silent.
            MissionDirector.CreatureOverride = Mimic;
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(3);
            voice.InjectPhrase(Phrase(0.6f, 220f));
            m_Director.Profile.Data.voiceDetection = false;
            m_Director.ApplyAndSaveSettings();
            yield return Frames(3);
            Assert.IsFalse(voice.CaptureActive);
            Assert.AreEqual(0, voice.Recorder.Count);
            var mimic = m_Director.ActiveEnemy.GetComponent<VoiceMimic>();
            m_Director.ActiveEnemy.WakeNow();
            for (float t = 0f; t < 3.5f; t += Time.deltaTime) yield return null;
            Assert.AreEqual(0, mimic.PlaybackCount, "With the microphone off the Mimic has nothing to say.");
        }

        [UnityTest]
        public IEnumerator Mimic_GoesQuiet_InABlackout_AndWhenTranquilized()
        {
            var voice = m_Player.voice;
            voice.LevelOverride = 0.0005f;
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(3);
            var creature = m_Director.ActiveEnemy;
            var mimic = creature.GetComponent<VoiceMimic>();
            creature.WakeNow();
            voice.InjectPhrase(Phrase(2.5f, 220f));
            for (float t = 0f; t < 4f && mimic.PlaybackCount == 0; t += Time.deltaTime) yield return null;
            Assert.IsTrue(mimic.IsSpeaking);

            m_Director.blackout.TriggerNow(3f);
            yield return Frames(3);
            Assert.IsFalse(mimic.IsSpeaking, "Everything goes quiet in a blackout, the Mimic included.");
            m_Director.blackout.EndBlackout(true);

            for (float t = 0f; t < 3f && voice.IsSuppressed; t += Time.unscaledDeltaTime) yield return null;
            voice.InjectPhrase(Phrase(2.5f, 300f));
            for (float t = 0f; t < 4f && mimic.PlaybackCount < 2; t += Time.deltaTime) yield return null;
            Assert.AreEqual(2, mimic.PlaybackCount);
            Assert.IsTrue(creature.Tranquilize(5f));
            yield return Frames(3);
            Assert.IsFalse(mimic.IsSpeaking, "A sleeping Mimic does not talk.");
        }
    }
}

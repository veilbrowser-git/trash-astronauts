using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using TrashAstronauts.Audio;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Core.Voice;
using TrashAstronauts.Net;
using TrashAstronauts.Net.Voice;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Proximity voice chat in the real scene over the in-memory network, with no real microphone: the scene's
    /// microphone is fed through VoiceSensor.InjectMicSamples and the simulated crew members encode a synthetic
    /// voice themselves. NetSession does not create voice chat yet (that hookup is one line, added separately), so
    /// each test calls VoiceChat.Attach right after hosting or joining, exactly as that line will.
    /// </summary>
    public class VoiceChatPlayTests : NetPlayFixture
    {
        const int Rate = VoiceFormat.SampleRate;
        const int Frame = VoiceFormat.FrameSamples;

        [TearDown]
        public void ClearVoiceSeams()
        {
            VoiceChat.EnabledOverride = null;
        }

        /// <summary>A speech-like signal (gliding pitch through vowel-like formants, 4 Hz syllables), about -22 dBFS.</summary>
        static float[] Speech(double seconds, float level = 1f, int seed = 1)
        {
            int n = (int)(Rate * seconds);
            var x = new float[n];
            double phase = 0;
            double[] f1 = { 700, 400, 300, 600 };
            double[] f2 = { 1200, 2000, 900, 1700 };
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;
                double f0 = 150 + 40 * Math.Sin(2 * Math.PI * 0.7 * t + seed);
                phase += 2 * Math.PI * f0 / Rate;
                int vowel = (int)(t * 3.3 + seed) % 4;
                double sum = 0;
                for (int k = 1; k * f0 < 3800; k++)
                {
                    double f = k * f0;
                    double amp = Math.Exp(-Math.Pow((f - f1[vowel]) / 180, 2)) + 0.6 * Math.Exp(-Math.Pow((f - f2[vowel]) / 250, 2)) + 0.08 / k;
                    sum += amp * Math.Sin(k * phase + k * 0.7);
                }
                x[i] = (float)(0.09 * level * (0.55 + 0.45 * Math.Sin(2 * Math.PI * 4 * t)) * sum);
            }
            return x;
        }

        /// <summary>A simulated crew member's voice: encodes one 20 ms frame per call and sends them in pairs.</summary>
        sealed class BotVoice
        {
            readonly VoiceEncoder m_Encoder = new VoiceEncoder();
            readonly byte[] m_Packet = new byte[2 * VoiceFormat.FrameBytes];
            readonly float[] m_Signal;
            int m_Frame;
            int m_Pending;
            ushort m_Seq = 30000;
            public int FramesSent;

            public BotVoice(float[] signal) { m_Signal = signal; }

            public bool Done => (m_Frame + 1) * Frame > m_Signal.Length;

            public void Step(Func<ushort, byte[], int, bool> send)
            {
                if (Done) return;
                m_Encoder.Encode(m_Signal, m_Frame * Frame, m_Packet, m_Pending * VoiceFormat.FrameBytes);
                m_Frame++;
                m_Pending++;
                if (m_Pending == 2)
                {
                    if (send(m_Seq, m_Packet, 2)) FramesSent += 2;
                    m_Seq += 2;
                    m_Pending = 0;
                }
            }
        }

        sealed class Heard
        {
            public readonly List<(byte slot, ushort seq)> Frames = new List<(byte, ushort)>();
            public void On(byte slot, ushort seq, byte[] buffer, int offset, int length) => Frames.Add((slot, seq));
            public int From(int slot) => Frames.FindAll(f => f.slot == slot).Count;
        }

        /// <summary>Hosts, attaches voice chat as NetSession's hookup line will, and lets a bot join with a pose near the player.</summary>
        IEnumerator HostWithBot(Action<Bot> made, Vector3 offset)
        {
            Assert.IsNull(m_Net.GetComponent<VoiceChat>(), "nothing of voice chat exists before a session");
            Assert.IsTrue(m_Net.Host());
            VoiceChat chat = VoiceChat.Attach(m_Net);
            Assert.AreSame(chat, VoiceChat.Attach(m_Net), "attaching twice is harmless");
            Vector3 at = m_Player.head.position + offset;
            var bot = AddBot(m_Net.Transport.Local);
            bot.Avatar.headPosition = new NetVec3(at.x, at.y, at.z);
            made(bot);
            yield return Until(() => bot.Session.Welcomed && m_Net.Crew(1) != null && m_Net.Crew(1).HasPose);
            yield return Frames(5);
            Assert.IsNotNull(chat.Playback(1), "the joiner's helmet got a voice source");
        }

        IEnumerator BotTalks(Bot bot, BotVoice voice, int frames, Action eachFrame = null)
        {
            for (int i = 0; i < frames; i++)
            {
                Tick();
                voice.Step((seq, data, count) => bot.Session.SendVoice(seq, data, 0, VoiceFormat.FrameBytes, count));
                eachFrame?.Invoke();
                yield return null;
            }
        }

        /// <summary>Feeds the scene's microphone 20 ms per frame.</summary>
        IEnumerator MicSays(float[] signal, Action eachFrame = null)
        {
            for (int at = 0; at + Frame <= signal.Length; at += Frame)
            {
                var chunk = new float[Frame];
                Array.Copy(signal, at, chunk, 0, Frame);
                m_Player.voice.InjectMicSamples(chunk, Rate);
                Tick();
                eachFrame?.Invoke();
                yield return null;
            }
        }

        // ------------------------------------------------------------------ receiving

        [UnityTest]
        public IEnumerator AJoinersVoice_ArrivesAndPlaysFromItsHelmet_In3D_AndShowsAsTalking()
        {
            Bot bot = null;
            yield return HostWithBot(b => bot = b, new Vector3(1f, 0f, 0f));
            VoiceChat chat = VoiceChat.Active;
            Assert.IsNotNull(chat);
            VoicePlayback playback = chat.Playback(1);
            RemoteAvatar crew = m_Net.Crew(1);
            Assert.AreSame(crew.head, playback.transform.parent, "the voice comes from that player's helmet");
            Assert.IsNull(chat.Playback(0), "no source for this player's own slot");
            AudioSource source = playback.source;
            Assert.AreEqual(1f, source.spatialBlend, "fully 3D");
            Assert.AreEqual(AudioRolloffMode.Custom, source.rolloffMode);
            Assert.AreEqual(VoiceRolloff.MaxDistance, source.maxDistance);
            Assert.IsTrue(source.loop);
            Assert.IsFalse(source.bypassReverbZones, "a world sound: room reverb applies");
            Assert.IsNotNull(playback.occlusion);
            Assert.AreSame(playback.lowPass, playback.occlusion.lowPass);
            var components = playback.GetComponents<Component>();
            Assert.Less(Array.IndexOf(components, playback), Array.IndexOf(components, playback.lowPass), "the voice is written before the wall filter runs");
            Assert.Less(Array.IndexOf(components, playback), Array.IndexOf(components, playback.occlusion));
            var listener = UnityEngine.Object.FindFirstObjectByType<AudioListener>();
            Assert.IsNotNull(listener.GetComponent<AudioLowPassFilter>(), "and the helmet low-pass on the listener takes the top off it like every world sound");
            Assert.IsFalse(chat.IsSpeaking(1));

            var voice = new BotVoice(Speech(1.5));
            bool spoke = false;
            float loudest = -100f;
            yield return BotTalks(bot, voice, 75, () =>
            {
                spoke |= chat.IsSpeaking(1);
                loudest = Mathf.Max(loudest, playback.LastFrameDb);
            });
            yield return Frames(20);
            Assert.AreEqual(74, voice.FramesSent);
            Assert.Greater(chat.FramesReceived, 70);
            Assert.GreaterOrEqual(playback.FramesPlayed, 70, "every frame played");
            Assert.Greater(loudest, -35f, "at about the level it was spoken");
            TestContext.WriteLine("queued " + playback.SamplesQueued + ", consumed by the audio thread " + playback.SamplesConsumed + ", dropped frames " + playback.FramesDropped);
            Assert.Greater(playback.SamplesQueued, 0.1 * playback.OutputRate, "the audio went to the source");
            // (The test clock runs faster than real time, so the audio thread cannot keep up here: frames beyond
            // 120 ms of queued audio are dropped by design rather than piling up latency.)
            Assert.IsTrue(source.isPlaying);
            Assert.IsTrue(spoke, "the talk indicator was on");
            Assert.IsFalse(chat.IsSpeaking(1), "and goes off when they stop");
            Assert.AreEqual(0f, chat.Level(0));
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator TheVoice_FallsWithDistance_AndIsQuieterAndMuffledThroughAWall()
        {
            Bot bot = null;
            yield return HostWithBot(b => bot = b, new Vector3(1.2f, 0f, 0f));
            VoicePlayback playback = VoiceChat.Active.Playback(1);
            AnimationCurve curve = playback.source.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
            Vector3 mouth = playback.transform.position;
            float previous = 2f;
            foreach (float d in new[] { 0.5f, 1f, 2f, 2.5f, 4f, 6f, 10f, 14f, 18f, 25f })
            {
                Assert.AreEqual(VoiceRolloff.Gain(d), curve.Evaluate(Mathf.Min(1f, d / VoiceRolloff.MaxDistance)), 0.02f, "the source's curve at " + d + " m");
                float gain = playback.AudibleGain(mouth + Vector3.forward * d);
                Assert.LessOrEqual(gain, previous, "never louder further away");
                previous = gain;
            }
            Assert.Greater(playback.AudibleGain(mouth + Vector3.forward * 2f), 0.95f, "clear across a room");
            Assert.Less(playback.AudibleGain(mouth + Vector3.forward * 10f), 0.22f, "quiet at 10 m");
            Assert.AreEqual(0f, playback.AudibleGain(mouth + Vector3.forward * 19f), "inaudible beyond 18 m");

            // A wall between the player and the speaker: muffled (low-pass) and quieter.
            SoundOcclusion occlusion = playback.occlusion;
            Vector3 listener = m_Player.head.position;
            occlusion.Check();
            for (int i = 0; i < 60; i++) occlusion.Step(0.02f);
            Assert.IsFalse(occlusion.Blocked, "nothing between them in the tug");
            float clear = playback.AudibleGain(listener);
            Assert.Greater(clear, 0.9f);
            Assert.IsFalse(playback.lowPass.enabled);

            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.layer = GameLayers.Environment;
            wall.transform.position = (listener + mouth) * 0.5f;
            wall.transform.rotation = Quaternion.LookRotation(mouth - listener);
            wall.transform.localScale = new Vector3(3f, 3f, 0.1f);
            Physics.SyncTransforms();
            for (int i = 0; i < 100; i++) occlusion.Step(0.02f);
            Assert.IsTrue(occlusion.Blocked);
            float behind = playback.AudibleGain(listener);
            Assert.Less(behind, clear * 0.6f, "quieter through the wall");
            Assert.IsTrue(playback.lowPass.enabled);
            Assert.Less(playback.lowPass.cutoffFrequency, 1500f, "and muffled");

            UnityEngine.Object.DestroyImmediate(wall);
            Physics.SyncTransforms();
            for (int i = 0; i < 100; i++) occlusion.Step(0.02f);
            Assert.IsFalse(occlusion.Blocked);
            Assert.AreEqual(clear, playback.AudibleGain(listener), 0.01f, "clear again");
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator ALossyReorderingLink_StillGivesContinuousAudio_WithoutErrors()
        {
            Bot bot = null;
            yield return HostWithBot(b => bot = b, new Vector3(1f, 0f, 0f));
            VoicePlayback playback = VoiceChat.Active.Playback(1);
            m_Hub.Loss = 0.3;
            m_Hub.Reorder = 0.3;
            m_Hub.Jitter = 0.03;
            long pulledBefore = playback.SlotsPulled;
            var voice = new BotVoice(Speech(4.0));
            int silentMidSpeech = 0;
            bool started = false;
            long lastPlayed = 0, lastConcealed = 0, lastPulled = playback.SlotsPulled;
            yield return BotTalks(bot, voice, 190, () =>
            {
                long pulled = playback.SlotsPulled - lastPulled;
                long played = playback.FramesPlayed - lastPlayed;
                long concealed = playback.FramesConcealed - lastConcealed;
                if (played > 0) started = true;
                else if (started && concealed == 0 && pulled > 0) silentMidSpeech++;
                lastPulled = playback.SlotsPulled;
                lastPlayed = playback.FramesPlayed;
                lastConcealed = playback.FramesConcealed;
            });
            m_Hub.Loss = 0;
            m_Hub.Reorder = 0;
            m_Hub.Jitter = 0;
            yield return Frames(30);
            VoiceStream stream = VoiceChat.Active.Stream(1);
            TestContext.WriteLine("played " + playback.FramesPlayed + ", concealed " + playback.FramesConcealed + ", silent mid-speech " + silentMidSpeech
                                  + ", late " + stream.Jitter.Late + ", target " + stream.Jitter.TargetDelayFrames + " frames, spurts " + stream.Jitter.Spurts);
            Assert.That(playback.FramesPlayed, Is.InRange(100, 170), "about 70% of 190 frames");
            Assert.Greater(playback.FramesConcealed, 20, "the gaps were concealed");
            Assert.Less(silentMidSpeech, 30, "the voice does not keep dropping out");
            Assert.IsNotNull(VoiceChat.Active.Playback(1));
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator TheAvatarLeavingMidSentence_TakesItsVoiceSourceWithIt()
        {
            Bot bot = null;
            yield return HostWithBot(b => bot = b, new Vector3(1f, 0f, 0f));
            VoiceChat chat = VoiceChat.Active;
            var voice = new BotVoice(Speech(3.0));
            yield return BotTalks(bot, voice, 30);
            Assert.IsTrue(chat.IsSpeaking(1));
            Assert.AreEqual(1, UnityEngine.Object.FindObjectsByType<VoicePlayback>(FindObjectsSortMode.None).Length);

            bot.Session.Leave(Now);
            bot.Frozen = true;
            yield return Until(() => m_Net.Crew(1) == null);
            yield return Frames(3);
            Assert.IsNull(chat.Playback(1));
            Assert.IsFalse(chat.IsSpeaking(1));
            Assert.AreEqual(0, UnityEngine.Object.FindObjectsByType<VoicePlayback>(FindObjectsSortMode.None).Length, "no voice source left behind");

            // A new joiner in the same slot gets a fresh source and a fresh stream.
            var again = AddBot(m_Net.Transport.Local);
            Vector3 at = m_Player.head.position + Vector3.right;
            again.Avatar.headPosition = new NetVec3(at.x, at.y, at.z);
            yield return Until(() => m_Net.Crew(1) != null && m_Net.Crew(1).HasPose);
            yield return Frames(3);
            Assert.IsNotNull(chat.Playback(1));
            Assert.AreEqual(0, chat.Stream(1).Jitter.Received);
            m_Net.Leave();
            yield return Frames(3);
            Assert.IsNull(VoiceChat.Active, "LEAVE: voice chat is gone with the session");
            Assert.IsNull(m_Net.GetComponent<VoiceChat>());
            Assert.IsNull(m_Player.voice.MicTap);
        }

        // ------------------------------------------------------------------ sending

        [UnityTest]
        public IEnumerator ThePlayersVoice_GoesToTheCrew_OnlyWhileTalking_AndNeverWithMicOrVoiceChatOff()
        {
            Bot bot = null;
            yield return HostWithBot(b => bot = b, new Vector3(1f, 0f, 0f));
            var heard = new Heard();
            bot.Session.VoiceReceived += heard.On;
            VoiceChat chat = VoiceChat.Active;
            Assert.AreSame(chat, m_Player.voice.MicTap, "voice chat taps the one microphone capture the game already runs");

            yield return MicSays(new float[Rate]); // a second of silence
            Assert.AreEqual(0, heard.Frames.Count, "silence sends nothing");
            Assert.IsFalse(chat.IsTransmitting);

            bool transmitting = false;
            yield return MicSays(Speech(1.0), () => transmitting |= chat.IsTransmitting);
            yield return MicSays(new float[Rate / 2]);
            Assert.IsTrue(transmitting);
            Assert.That(heard.From(0), Is.InRange(45, 75), "a second of talking, with pre-roll and hang-over");
            Assert.AreEqual(heard.Frames.Count, heard.From(0), "from slot 0, the host");
            Assert.IsFalse(chat.IsTransmitting, "and it stops when the player does");

            // MIC off: nothing is captured at all, so nothing is sent.
            int before = heard.Frames.Count;
            m_Director.Profile.Data.voiceDetection = false;
            yield return MicSays(Speech(1.0));
            Assert.AreEqual(before, heard.Frames.Count, "MIC off");
            Assert.IsFalse(m_Player.voice.Listening);
            m_Director.Profile.Data.voiceDetection = true;

            // VOICE CHAT off: the creature still hears the microphone, the crew do not, and their voices are not played.
            m_Director.Profile.Data.voiceChat = false;
            long heardBlocks = m_Player.voice.HeardBlocks;
            yield return MicSays(Speech(1.0));
            Assert.AreEqual(before, heard.Frames.Count, "VOICE CHAT off");
            Assert.Greater(m_Player.voice.HeardBlocks, heardBlocks, "MIC is still on for the creature");
            Assert.IsNull(m_Player.voice.MicTap);
            Assert.IsNull(chat.Playback(1), "no crew voice is played either");
            var voice = new BotVoice(Speech(0.5));
            yield return BotTalks(bot, voice, 25);
            Assert.AreEqual(0, chat.FramesReceived);
            Assert.Greater(chat.FramesIgnored, 0);

            m_Director.Profile.Data.voiceChat = true;
            yield return Frames(3);
            Assert.IsNotNull(chat.Playback(1), "back on: the source is made again");
            yield return MicSays(Speech(0.6));
            yield return MicSays(new float[Rate / 2]);
            Assert.Greater(heard.Frames.Count, before + 20, "and the voice flows again");
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator EchoOfACrewVoice_FromTheSpeakers_IsNotResent_AndTheCreatureDoesNotHearItAsThePlayer()
        {
            Bot bot = null;
            yield return HostWithBot(b => bot = b, new Vector3(0.8f, 0f, 0f));
            var heard = new Heard();
            bot.Session.VoiceReceived += heard.On;
            VoiceChat chat = VoiceChat.Active;
            VoicePlayback playback = chat.Playback(1);
            float[] speech = Speech(3.0, 1f, 2);

            // The crew member talks; once it is playing, the microphone picks it up from the speakers about 100 ms
            // later at half its level (what a headset microphone near the speakers would do).
            var voice = new BotVoice(speech);
            int playedFrom = -1;
            long heardBefore = 0, echoBefore = 0;
            int frame = 0;
            yield return BotTalks(bot, voice, 150, () =>
            {
                frame++;
                if (playedFrom < 0 && chat.IsSpeaking(1))
                {
                    playedFrom = frame;
                    heardBefore = m_Player.voice.HeardBlocks;
                    echoBefore = m_Player.voice.EchoBlocks;
                }
                if (playedFrom < 0) return;
                int at = (frame - playedFrom) * Frame;
                if (at + Frame > speech.Length) return;
                var leak = new float[Frame];
                for (int i = 0; i < Frame; i++) leak[i] = speech[at + i] * 0.5f;
                m_Player.voice.InjectMicSamples(leak, Rate);
            });
            yield return Frames(10);
            Assert.Greater(playedFrom, 0, "the crew voice played");
            Assert.Greater(playback.FramesPlayed, 100);
            Assert.AreEqual(0, heard.Frames.Count, "the echo of their own voice was not sent back to them");
            Assert.Greater(m_Player.voice.EchoBlocks - echoBefore, 100, "the microphone blocks were recognised as the speakers");
            Assert.LessOrEqual(m_Player.voice.HeardBlocks - heardBefore, 3, "and the creature was not told the player talked");
            Assert.Greater(chat.Capture.FramesEchoGated, 0);

            // The same voice from the player themselves, with nothing playing, is sent and heard as usual.
            yield return Frames(30);
            long heardBlocks = m_Player.voice.HeardBlocks;
            yield return MicSays(Speech(1.0, 1f, 2));
            yield return MicSays(new float[Rate / 2]);
            Assert.Greater(heard.From(0), 40, "the player's own voice goes out");
            Assert.Greater(m_Player.voice.HeardBlocks, heardBlocks + 50);

            // Talking clearly over the crew member (double talk) still goes out.
            heard.Frames.Clear();
            var second = new BotVoice(Speech(2.0, 1f, 3));
            float[] loud = Speech(2.0, 6f, 4);
            int k = 0;
            yield return BotTalks(bot, second, 100, () =>
            {
                var chunk = new float[Frame];
                Array.Copy(loud, Math.Min(k++ * Frame, loud.Length - Frame), chunk, 0, Frame);
                m_Player.voice.InjectMicSamples(chunk, Rate);
            });
            Assert.Greater(heard.From(0), 30, "the player can talk over the crew");
            m_Net.Leave();
        }

        // ------------------------------------------------------------------ client side and single-player

        [UnityTest]
        public IEnumerator Client_HearsTheHostFromItsHelmet_AndTheHostHearsIt()
        {
            var host = AddScriptedHost();
            var heard = new Heard();
            host.Session.VoiceReceived += heard.On;
            Assert.IsTrue(m_Net.Join(host.Endpoint));
            VoiceChat chat = VoiceChat.Attach(m_Net);
            Vector3 at = m_Player.head.position + Vector3.right;
            host.Avatar.headPosition = new NetVec3(at.x, at.y, at.z);
            yield return Until(() => m_Net.Mode == Core.Net.LobbyMode.Joined && m_Net.Crew(0) != null && m_Net.Crew(0).HasPose);
            yield return Frames(5);
            Assert.IsNotNull(chat.Playback(0), "the host's helmet speaks");
            Assert.AreSame(m_Net.Crew(0).head, chat.Playback(0).transform.parent);

            var voice = new BotVoice(Speech(1.0));
            for (int i = 0; i < 50; i++)
            {
                Tick();
                voice.Step((seq, data, count) => host.Session.SendVoice(seq, data, 0, VoiceFormat.FrameBytes, count) > 0);
                yield return null;
            }
            yield return Frames(15);
            Assert.GreaterOrEqual(chat.Playback(0).FramesPlayed, 45);

            yield return MicSays(Speech(1.0));
            yield return MicSays(new float[Rate / 2]);
            Assert.Greater(heard.From(1), 40, "the host hears slot 1");
            m_Net.Leave();
            yield return Frames(2);
            Assert.IsNull(VoiceChat.Active);
        }

        [UnityTest]
        public IEnumerator SinglePlayer_NeverCreatesAnyOfIt()
        {
            yield return Frames(5);
            Assert.IsNull(VoiceChat.Active);
            Assert.AreEqual(0, UnityEngine.Object.FindObjectsByType<VoiceChat>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length);
            Assert.AreEqual(0, UnityEngine.Object.FindObjectsByType<VoicePlayback>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length);
            Assert.IsNull(m_Player.voice.MicTap);
            long heardBlocks = m_Player.voice.HeardBlocks;
            yield return MicSays(Speech(1.0));
            Assert.Greater(m_Player.voice.HeardBlocks, heardBlocks + 50, "the microphone works for the creature exactly as before");
            Assert.AreEqual(0, m_Player.voice.EchoBlocks);
            Assert.IsNull(VoiceChat.Active);
            Assert.IsTrue(m_Net.IsOffline);
        }
    }
}

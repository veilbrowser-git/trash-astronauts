using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Core.Voice;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Net.Voice;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// The Mimic in a co-op crew, in the real scene over the in-memory network. Host half: bots talk over voice chat,
    /// the host's Mimic keeps their phrases (only while it is aboard), says them from the creature to everyone as the
    /// creature's voice, prefers someone who is not near the player it stalks, and forgets them when it should.
    /// Client half, against a scripted host: the creature's voice plays from the replica, never lights a talk light,
    /// and is neither sent back as this player's voice nor reported to the spider as this player's noise.
    /// </summary>
    public class MimicCoopPlayTests : NetPlayFixture
    {
        const int Rate = VoiceFormat.SampleRate;
        const int Frame = VoiceFormat.FrameSamples;

        [TearDown]
        public void ClearSeams() => VoiceChat.EnabledOverride = null;

        int MimicIndex() => Enumerable.Range(0, m_Director.enemies.Length).First(i => m_Director.enemies[i].GetComponent<VoiceMimic>() != null);

        /// <summary>A speech-like test signal (formant-shaped harmonics, 4 Hz syllables).</summary>
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

        sealed class Heard
        {
            public readonly List<(byte slot, ushort seq, byte[] bytes)> Frames = new List<(byte, ushort, byte[])>();
            public void On(byte slot, ushort seq, byte[] buffer, int offset, int length)
            {
                var copy = new byte[length];
                Buffer.BlockCopy(buffer, offset, copy, 0, length);
                Frames.Add((slot, seq, copy));
            }
            public int From(int slot) => Frames.Count(f => f.slot == slot);
        }

        /// <summary>A bot talking: one 20 ms frame per tick, sent in pairs, then the silence that ends the phrase.</summary>
        IEnumerator BotSays(Bot bot, float[] speech, ushort firstSeq)
        {
            var encoder = new VoiceEncoder();
            var packet = new byte[2 * VoiceFormat.FrameBytes];
            int pending = 0;
            ushort seq = firstSeq;
            for (int at = 0; at + Frame <= speech.Length; at += Frame)
            {
                Tick();
                encoder.Encode(speech, at, packet, pending * VoiceFormat.FrameBytes);
                if (++pending == 2)
                {
                    bot.Session.SendVoice(seq, packet, 0, VoiceFormat.FrameBytes, 2);
                    seq += 2;
                    pending = 0;
                }
                yield return null;
            }
            yield return Frames(50);
        }

        static void SetHead(Bot bot, Vector3 head)
        {
            bot.Avatar.headPosition = new NetVec3(head.x, head.y, head.z);
            bot.Avatar.leftHandPosition = new NetVec3(head.x - 0.2f, head.y - 0.4f, head.z + 0.2f);
            bot.Avatar.rightHandPosition = new NetVec3(head.x + 0.2f, head.y - 0.4f, head.z + 0.2f);
        }

        /// <summary>Ticks every frame for up to <paramref name="seconds"/> of real time (VoiceMimic paces itself on Time.time).</summary>
        IEnumerator RealTimeUntil(Func<bool> done, float seconds)
        {
            float until = Time.unscaledTime + seconds;
            while (!done() && Time.unscaledTime < until)
            {
                Tick();
                yield return null;
            }
        }

        Bot m_Bot, m_Bot2;

        /// <summary>Hosts, lets a bot (or two) in, and starts a contract with <paramref name="creature"/> aboard (the bot asks for it).</summary>
        IEnumerator HostContract(int creature, bool twoBots = false)
        {
            MissionDirector.CreatureOverride = creature;
            Assert.IsTrue(m_Net.Host());
            m_Bot = AddBot(m_Net.Transport.Local);
            SetHead(m_Bot, m_Director.CrewSpot(1) + Vector3.up * 1.6f);
            yield return Until(() => m_Bot.Session.Welcomed && m_Net.Crew(1) != null && m_Net.Crew(1).HasPose);
            if (twoBots)
            {
                m_Bot2 = AddBot(m_Net.Transport.Local);
                SetHead(m_Bot2, m_Director.CrewSpot(2) + Vector3.up * 1.6f);
                yield return Until(() => m_Bot2.Session.Welcomed && m_Net.Crew(2) != null && m_Net.Crew(2).HasPose);
            }
            Assert.IsNotNull(VoiceChat.Active);
            m_Bot.Session.SendStartRequest();
            yield return Until(() => m_Director.Phase == MissionPhase.Active);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.AreEqual(creature, (int)m_Director.CurrentRun.creatureIndex);
            yield return Frames(3);
        }

        // ================================================================== host half

        [UnityTest]
        public IEnumerator Host_ABotsVoiceBecomesAPhrase_AndTheMimicSaysItToTheWholeCrew_FromTheCreature()
        {
            m_Player.voice.LevelOverride = 0.0005f;
            yield return HostContract(MimicIndex());
            var creature = m_Director.ActiveEnemy;
            var mimic = creature.GetComponent<VoiceMimic>();
            Assert.IsNotNull(mimic, "the Mimic is aboard a co-op contract");
            var chat = VoiceChat.Active;
            var crew = chat.Mimic;
            Assert.AreSame(crew, VoiceMimic.Crew, "the host's Mimic hears through the crew voice");
            Assert.IsTrue(crew.CaptureActive, "a Mimic aboard asks for phrases");
            var heard = new Heard();
            m_Bot.Session.VoiceReceived += heard.On;

            yield return BotSays(m_Bot, Speech(1.2), 100);
            Assert.AreEqual(1, crew.Bank.CountOf(1), "the bot's voice-chat frames became one phrase on the host");
            Assert.That(crew.Bank.Recorder(1).Newest.Length / (float)Rate, Is.InRange(0.9f, 1.8f));
            Assert.AreEqual(0, mimic.PlaybackCount, "a dormant Mimic is silent");

            creature.WakeNow();
            yield return RealTimeUntil(() => mimic.PlaybackCount > 0, 5f);
            Assert.AreEqual(1, mimic.PlaybackCount, "it answers within a few seconds");
            Assert.AreEqual(1, mimic.LastSpeaker, "with the bot's words");
            Assert.IsTrue(mimic.IsSpeaking);
            Assert.IsFalse(mimic.speaker.isPlaying, "in co-op it speaks through the crew voice, not the offline clip");
            Assert.IsTrue(m_Player.voice.IsSuppressed, "the host's microphone ignores it");

            yield return RealTimeUntil(() => !crew.Sender.Active, 6f);
            yield return Frames(20);
            int creatureFrames = heard.From(VoiceMsg.CreatureSlot);
            float expected = crew.Bank.Recorder(1).Newest.Length / (float)Rate / mimic.pitch / (float)VoiceFormat.FrameSeconds;
            Assert.AreEqual(expected, creatureFrames, 3f, "the bot was sent the whole phrase, lowered and slowed, as the creature's voice");
            Assert.AreEqual(0, heard.From(1), "never its own voice back");

            // The frames decode into the bot's own words, slowed: as loud as it was said, never louder than the Mimic's level.
            var decoder = new VoiceDecoder();
            var pcm = new float[Frame];
            float loudest = -100f;
            foreach (var f in heard.Frames.Where(f => f.slot == VoiceMsg.CreatureSlot))
            {
                Assert.IsTrue(decoder.Decode(f.bytes, 0, f.bytes.Length, pcm, 0), "a decodable voice frame");
                loudest = Mathf.Max(loudest, VoiceFormat.ToDb(VoiceFormat.Rms(pcm, 0, Frame)));
            }
            Assert.Greater(loudest, -30f, "it carries the phrase");
            Assert.Less(loudest, 0f);

            // The host hears it the same way: from a voice source on the creature, at the Mimic's own volume.
            var playback = crew.Playback;
            Assert.IsNotNull(playback);
            Assert.AreSame(mimic.speaker.transform, playback.transform.parent, "from the creature's mouth");
            Assert.Greater(playback.FramesPlayed, creatureFrames - 10);
            Assert.AreEqual(1f, playback.source.spatialBlend);
            Assert.LessOrEqual(playback.source.volume, mimic.volume + 1e-4f, "never louder than the single-player Mimic");
            Assert.AreEqual(mimic.speaker.maxDistance, playback.source.maxDistance);
            Assert.IsNotNull(playback.occlusion, "walls muffle it");
            Assert.IsNull(playback.GetComponentInParent<VoiceTalkLight>(), "the creature is not a crew member");
            Assert.IsFalse(chat.IsSpeaking(1), "and the bot's talk light stays off");

            m_Net.Leave();
            yield return Frames(3);
            Assert.IsNull(VoiceMimic.Crew, "LEAVE: back to the single-player Mimic");
            Assert.AreEqual(0, crew.Bank.Count, "and every crew phrase is forgotten");
        }

        [UnityTest]
        public IEnumerator Host_ItGoesQuiet_InABlackout_AndWhenTranquilized()
        {
            m_Player.voice.LevelOverride = 0.0005f;
            yield return HostContract(MimicIndex());
            var creature = m_Director.ActiveEnemy;
            var mimic = creature.GetComponent<VoiceMimic>();
            var crew = VoiceChat.Active.Mimic;
            var heard = new Heard();
            m_Bot.Session.VoiceReceived += heard.On;
            creature.WakeNow();

            yield return BotSays(m_Bot, Speech(3.0), 100);
            yield return RealTimeUntil(() => mimic.PlaybackCount > 0, 5f);
            Assert.AreEqual(1, mimic.PlaybackCount);
            Assert.IsTrue(crew.Sender.Active, "it is talking");
            m_Director.blackout.TriggerNow(3f);
            yield return Frames(3);
            Assert.IsFalse(mimic.IsSpeaking, "everything goes quiet in a blackout");
            Assert.IsFalse(crew.Sender.Active, "nothing more goes to the crew");
            Assert.IsNull(crew.Playback, "and nothing plays here");
            int sent = heard.From(VoiceMsg.CreatureSlot);
            yield return Frames(30);
            Assert.AreEqual(sent, heard.From(VoiceMsg.CreatureSlot));
            m_Director.blackout.EndBlackout(true);

            yield return BotSays(m_Bot, Speech(3.0, 1f, 2), 400);
            yield return RealTimeUntil(() => mimic.PlaybackCount > 1, 5f);
            Assert.AreEqual(2, mimic.PlaybackCount, "a new phrase gets a new answer after the blackout");
            Assert.IsTrue(crew.Sender.Active);
            Assert.IsTrue(creature.Tranquilize(5f));
            yield return Frames(3);
            Assert.IsFalse(mimic.IsSpeaking, "a sleeping Mimic does not talk");
            Assert.IsFalse(crew.Sender.Active);
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Host_ItPrefersAFriendsVoice_ToThatOfThePlayerItIsStalking()
        {
            m_Player.voice.LevelOverride = 0.0005f;
            yield return HostContract(MimicIndex());
            var creature = m_Director.ActiveEnemy;
            var mimic = creature.GetComponent<VoiceMimic>();
            var crew = VoiceChat.Active.Mimic;

            // The host and the bot are 14 m apart; whichever of them the creature stalks, the other is "not near".
            Vector3 host = m_Player.HeadPosition;
            Vector3 away = host - creature.transform.position;
            away.y = 0f;
            SetHead(m_Bot, host + away.normalized * 14f);
            yield return Until(() => Vector3.Distance(m_Net.Crew(1).context.HeadPosition, host) > 13f);
            // Held still (hazards paused: only the test steps it), so whom it stalks cannot change under the test.
            m_Director.SetHazardPause(HazardPauseReason.Scripted, true);
            creature.WakeNow();
            creature.Step(0.01f);
            int stalked = creature.Quarry.Value;
            Assert.That(stalked, Is.EqualTo(0).Or.EqualTo(1), "test set-up: it is stalking one of the two");
            int friend = 1 - stalked;

            // Both have said something; the one it stalks spoke last.
            foreach (int who in new[] { friend, stalked })
            {
                if (who == 1) yield return BotSays(m_Bot, Speech(1.0, 1f, 2), 100);
                else
                {
                    m_Player.voice.InjectPhrase(Speech(0.8, 1f, 3));
                    yield return Frames(2);
                }
            }
            Assert.AreEqual(1, crew.Bank.CountOf(1));
            Assert.AreEqual(1, crew.Bank.CountOf(0), "the host's own phrase is in it too (MIC on)");
            Assert.AreEqual(stalked, crew.Bank.NewestSlot, "the stalked one spoke last");

            yield return RealTimeUntil(() => mimic.PlaybackCount > 0, 5f);
            Assert.AreEqual(1, mimic.PlaybackCount);
            Assert.AreEqual(stalked, creature.Quarry.Value);
            Assert.AreEqual(friend, mimic.LastSpeaker, "the one it stalks hears their friend calling from the dark, not themself");
            m_Director.SetHazardPause(HazardPauseReason.Scripted, false);
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Host_ABotsPhrases_AreForgotten_WhenItTurnsVoiceOff_WhenItLeaves_AndAtContractEnd()
        {
            m_Player.voice.LevelOverride = 0.0005f;
            yield return HostContract(MimicIndex(), twoBots: true);
            var crew = VoiceChat.Active.Mimic;

            yield return BotSays(m_Bot, Speech(1.0), 100);
            Assert.AreEqual(1, crew.Bank.CountOf(1));
            // VOICE CHAT (or MIC) off on the bot: its headset says so, and its voice stops.
            m_Bot.Session.SendVoice(0, VoiceMsg.OffNotice, 0, VoiceMsg.OffNoticeLength, 1);
            yield return Frames(3);
            Assert.AreEqual(1, crew.OffNoticesHeard);
            Assert.AreEqual(0, crew.Bank.CountOf(1), "turning voice off forgets what it said");
            Assert.AreEqual(0, crew.Bank.Count);

            // Back on, it talks again; then it leaves the crew.
            yield return BotSays(m_Bot, Speech(1.0, 1f, 2), 500);
            Assert.AreEqual(1, crew.Bank.CountOf(1));
            m_Bot.Session.Leave(Now);
            yield return Until(() => !m_Net.HostSession.IsConnected(1));
            yield return Frames(2);
            Assert.AreEqual(0, crew.Bank.CountOf(1), "a crew member who left is forgotten");

            // The other bot talks; then the contract ends.
            yield return BotSays(m_Bot2, Speech(1.0, 1f, 3), 900);
            Assert.AreEqual(1, crew.Bank.CountOf(2));
            Assert.AreEqual(1, crew.Bank.Count);
            m_Director.FailMission(FailureReason.Caught, m_Director.LocalPlayerId);
            yield return RealTimeUntil(() => false, 1.5f);
            Assert.IsFalse(crew.CaptureActive);
            Assert.AreEqual(0, crew.Bank.Count, "everything is forgotten when the contract ends");
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Host_NothingIsKept_WithoutAMimicAboard()
        {
            m_Player.voice.LevelOverride = 0.0005f;
            Assert.IsTrue(m_Net.Host());
            var crew = VoiceChat.Active.Mimic;
            m_Bot = AddBot(m_Net.Transport.Local);
            SetHead(m_Bot, m_Director.CrewSpot(1) + Vector3.up * 1.6f);
            yield return Until(() => m_Bot.Session.Welcomed && m_Net.Crew(1) != null && m_Net.Crew(1).HasPose && VoiceChat.Active.Playback(1) != null);

            // In the hub.
            yield return BotSays(m_Bot, Speech(1.0), 100);
            Assert.AreEqual(0, crew.Bank.FramesTaken, "not in the hub");
            Assert.Greater(VoiceChat.Active.Playback(1).FramesPlayed, 20, "though the voice chat itself played");

            // A contract with another species aboard.
            MissionDirector.CreatureOverride = 0;
            m_Bot.Session.SendStartRequest();
            yield return Until(() => m_Director.Phase == MissionPhase.Active);
            Assert.IsNull(m_Director.ActiveEnemy.GetComponent<VoiceMimic>());
            yield return BotSays(m_Bot, Speech(1.0), 300);
            Assert.IsFalse(crew.CaptureActive);
            Assert.AreEqual(0, crew.Bank.FramesTaken, "only a Mimic keeps anything");
            Assert.AreEqual(0, crew.Bank.Count);
            Assert.IsFalse(m_Player.voice.CaptureActive);
            m_Net.Leave();
        }

        // ================================================================== client half

        RunStartParams HostRunWithMimic(int runIndex)
        {
            MissionDirector.CreatureOverride = MimicIndex();
            m_Director.CoopRideAlong = true;
            m_Director.CoopCreatureEnabled = true;
            var run = m_Director.BuildRunParams();
            m_Director.CoopRideAlong = false;
            m_Director.CoopCreatureEnabled = false;
            run.runIndex = (ushort)runIndex;
            run.planHash = run.ComputePlanHash();
            return run;
        }

        IEnumerator JoinMimicRun(ScriptedHost host)
        {
            host.World.AutoStart = true;
            host.World.NextRun = HostRunWithMimic(5);
            Vector3 at = m_Player.head.position + Vector3.right;
            host.Avatar.headPosition = new NetVec3(at.x, at.y, at.z);
            Assert.IsTrue(m_Net.Join(host.Endpoint));
            yield return Until(() => m_Net.Mode == LobbyMode.Joined && m_Net.Crew(0) != null && m_Net.Crew(0).HasPose);
            m_Director.StartMission(m_Director.LocalPlayerId);
            yield return Until(() => m_Director.Phase == MissionPhase.Active);
            Assert.IsTrue(m_Director.CreatureAboard);
            yield return Frames(5);
        }

        [UnityTest]
        public IEnumerator Client_TheCreaturesVoice_PlaysFromTheReplica_LightsNoTalkLight_AndIsNeitherResentNorHeardAsThisPlayer()
        {
            var host = AddScriptedHost();
            yield return JoinMimicRun(host);
            var replica = m_Director.ActiveEnemy;
            Assert.IsTrue(replica.IsReplica);
            var mimic = replica.GetComponent<VoiceMimic>();
            Assert.IsNotNull(mimic, "the client's replica is the Mimic");
            var chat = VoiceChat.Active;
            yield return Until(() => chat.Playback(0) != null);
            Assert.IsNull(VoiceMimic.Crew, "a client's Mimic decides nothing");
            var light = m_Net.Crew(0).head.GetComponent<VoiceTalkLight>();
            Assert.IsNotNull(light);
            var heard = new Heard();
            host.Session.VoiceReceived += heard.On;
            int noises = host.World.Calls[NetMsg.Noise];

            // The host's creature speaks, two metres in front of this player; this headset's speakers leak it into the microphone.
            float[] voice = MimicVoiceRender.AtPitch(MimicVoiceRender.Warble(Speech(1.6, 1f, 4), Rate, mimic.warble), Rate, mimic.pitch);
            var sender = new CreatureVoiceSender(1000);
            sender.Begin(voice, Now);
            VoicePacketSink sink = (seq, frames, offset, bytes, count) => host.Session.SendCreatureVoice(seq, frames, offset, bytes, count) > 0;
            bool lit = false, crewTalking = false, suppressed = false;
            int at = 0;
            while (sender.Active || at < voice.Length)
            {
                replica.transform.position = m_Player.head.position + m_Player.head.forward * 2f;
                Tick();
                sender.Pump(Now, sink);
                var played = chat.Mimic.Playback;
                if (played != null && played.FramesPlayed > 0 && at + Frame <= voice.Length)
                {
                    var leak = new float[Frame];
                    for (int i = 0; i < Frame; i++) leak[i] = voice[at + i] * 0.8f;
                    m_Player.voice.InjectMicSamples(leak, Rate);
                    at += Frame;
                }
                else if (played != null && played.FramesPlayed > 0) at = voice.Length;
                lit |= light.IsOn;
                crewTalking |= chat.IsSpeaking(0);
                suppressed |= m_Player.voice.IsSuppressed;
                yield return null;
                if (Now > 1100) break;
            }
            yield return Frames(15);

            var playback = chat.Mimic.Playback;
            Assert.IsNotNull(playback, "the creature's voice has a source here");
            Assert.AreSame(mimic.speaker.transform, playback.transform.parent, "on the replica, at its mouth");
            Assert.Greater(playback.FramesPlayed, 60, "and it played");
            Assert.Greater(chat.Mimic.CreatureFramesReceived, 60);
            Assert.AreEqual(1f, playback.source.spatialBlend);
            Assert.LessOrEqual(playback.source.volume, mimic.volume + 1e-4f);
            Assert.IsFalse(lit, "the host's talk light never lit for the creature");
            Assert.IsFalse(crewTalking);
            Assert.IsTrue(suppressed, "the microphone was suppressed while it played");
            Assert.AreEqual(0, heard.Frames.Count, "the leak was not sent as this player's voice");
            Assert.AreEqual(noises, host.World.Calls[NetMsg.Noise], "nor reported to the spider as this player's noise");
            Assert.IsFalse(mimic.speaker.isPlaying, "the replica's own offline speaker stays silent");
            Assert.IsFalse(m_Player.voice.CaptureActive, "a client never keeps phrases");

            // Afterwards this player's own voice works as before (so the test could see it).
            yield return RealTimeUntil(() => !m_Player.voice.IsSuppressed, 2f);
            float[] talk = Speech(1.5, 3f, 5);
            for (int i = 0; i + Frame <= talk.Length; i += Frame)
            {
                var chunk = new float[Frame];
                Array.Copy(talk, i, chunk, 0, Frame);
                m_Player.voice.InjectMicSamples(chunk, Rate);
                Tick();
                yield return null;
            }
            yield return Frames(10);
            Assert.Greater(heard.From(1), 20, "its own voice goes out");
            Assert.Greater(host.World.Calls[NetMsg.Noise], noises, "and the spider hears it");
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Client_VoiceChatOff_TellsTheHost_AndTheCreaturesVoiceIsNotPlayed()
        {
            var host = AddScriptedHost();
            yield return JoinMimicRun(host);
            var chat = VoiceChat.Active;
            var heard = new Heard();
            host.Session.VoiceReceived += heard.On;
            yield return Frames(5);
            Assert.AreEqual(0, heard.Frames.Count);

            VoiceChat.EnabledOverride = false;
            yield return Frames(30);
            Assert.AreEqual(VoiceChat.OffNoticeRepeats, heard.Frames.Count, "a few 'voice off' notices and nothing else");
            Assert.IsTrue(heard.Frames.All(f => f.slot == 1 && VoiceMsg.IsOffNotice(f.bytes.Length)));
            yield return Frames(30);
            Assert.AreEqual(VoiceChat.OffNoticeRepeats, heard.Frames.Count, "nothing while it stays off");

            var sender = new CreatureVoiceSender();
            sender.Begin(Speech(0.8), Now);
            VoicePacketSink sink = (seq, frames, offset, bytes, count) => host.Session.SendCreatureVoice(seq, frames, offset, bytes, count) > 0;
            while (sender.Active)
            {
                Tick();
                sender.Pump(Now, sink);
                yield return null;
            }
            yield return Frames(15);
            Assert.IsNull(chat.Mimic.Playback, "with VOICE CHAT off the creature's copy of crew voices is not played");
            Assert.AreEqual(0, chat.Mimic.CreatureFramesReceived);

            // MIC off does the same.
            VoiceChat.EnabledOverride = null;
            yield return Frames(5);
            m_Director.Profile.Data.voiceDetection = false;
            yield return Frames(30);
            Assert.AreEqual(2 * VoiceChat.OffNoticeRepeats, heard.Frames.Count, "MIC off tells the host too");
            m_Director.Profile.Data.voiceDetection = true;
            m_Net.Leave();
        }
    }
}

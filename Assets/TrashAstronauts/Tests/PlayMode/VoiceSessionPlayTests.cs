using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Core.Voice;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Net.Voice;
using TrashAstronauts.UI;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Voice chat as NetSession switches it on (no test calls VoiceChat.Attach here): it appears with every host or
    /// client session and goes with it; voices play from helmets with the talk light on; the host relays a client's
    /// voice to the other clients and never back; the wrist shows MIC; the spider hears a client's own voice through
    /// the host but not a crew member's voice leaking from the speakers. In-memory network, plus one UDP loopback.
    /// </summary>
    public class VoiceSessionPlayTests : NetPlayFixture
    {
        const int Rate = VoiceFormat.SampleRate;
        const int Frame = VoiceFormat.FrameSamples;

        [TearDown]
        public void ClearVoiceSeams() => VoiceChat.EnabledOverride = null;

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

        /// <summary>A simulated crew member's voice, sent in pairs of 20 ms frames.</summary>
        sealed class BotVoice
        {
            readonly VoiceEncoder m_Encoder = new VoiceEncoder();
            readonly byte[] m_Packet = new byte[2 * VoiceFormat.FrameBytes];
            readonly float[] m_Signal;
            int m_Frame, m_Pending;
            ushort m_Seq = 30000;
            public BotVoice(float[] signal) { m_Signal = signal; }
            public bool Done => (m_Frame + 1) * Frame > m_Signal.Length;
            public void Step(Func<ushort, byte[], int, bool> send)
            {
                if (Done) return;
                m_Encoder.Encode(m_Signal, m_Frame * Frame, m_Packet, m_Pending * VoiceFormat.FrameBytes);
                m_Frame++;
                if (++m_Pending < 2) return;
                send(m_Seq, m_Packet, 2);
                m_Seq += 2;
                m_Pending = 0;
            }
        }

        sealed class Heard
        {
            public readonly List<byte> Slots = new List<byte>();
            public void On(byte slot, ushort seq, byte[] buffer, int offset, int length) => Slots.Add(slot);
            public int From(int slot) => Slots.FindAll(s => s == slot).Count;
        }

        IEnumerator BotTalks(Bot bot, float[] speech, Action each = null)
        {
            var voice = new BotVoice(speech);
            while (!voice.Done)
            {
                Tick();
                voice.Step((seq, data, count) => bot.Session.SendVoice(seq, data, 0, VoiceFormat.FrameBytes, count));
                each?.Invoke();
                yield return null;
            }
        }

        IEnumerator MicSays(float[] signal, Action each = null)
        {
            for (int at = 0; at + Frame <= signal.Length; at += Frame)
            {
                var chunk = new float[Frame];
                Array.Copy(signal, at, chunk, 0, Frame);
                m_Player.voice.InjectMicSamples(chunk, Rate);
                Tick();
                each?.Invoke();
                yield return null;
            }
        }

        Bot BotAt(NetEndpoint host, Vector3 offset)
        {
            var bot = AddBot(host);
            Vector3 at = m_Player.head.position + offset;
            bot.Avatar.headPosition = new NetVec3(at.x, at.y, at.z);
            return bot;
        }

        static int Count<T>() where T : Object => Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;

        // ------------------------------------------------------------------ host

        [UnityTest]
        public IEnumerator Hosting_SwitchesVoiceChatOn_TheBotsVoicePlaysFromItsHelmetWithTheTalkLight_TheHostsReachesIt_AndLeaveRemovesItAll()
        {
            Assert.IsNull(VoiceChat.Active, "single-player: none of it");
            Assert.IsTrue(m_Net.Host());
            var chat = m_Net.GetComponent<VoiceChat>();
            Assert.IsNotNull(chat, "NetSession attached voice chat when hosting");
            Assert.AreSame(chat, VoiceChat.Active);
            var bot = BotAt(m_Net.Transport.Local, new Vector3(1f, 0f, 0f));
            var heard = new Heard();
            bot.Session.VoiceReceived += heard.On;
            yield return Until(() => m_Net.Crew(1) != null && m_Net.Crew(1).HasPose && chat.Playback(1) != null);
            var playback = chat.Playback(1);
            Assert.IsNotNull(playback, "the bot's helmet has a voice source");
            Assert.AreSame(m_Net.Crew(1).head, playback.transform.parent);
            var light = m_Net.Crew(1).head.GetComponent<VoiceTalkLight>();
            Assert.IsNotNull(light, "and a talk light");
            Assert.IsFalse(light.IsOn);

            bool lit = false;
            yield return BotTalks(bot, Speech(1.5), () => lit |= light.IsOn);
            yield return Frames(20);
            Assert.Greater(playback.FramesPlayed, 60, "the bot's voice played on its avatar's source");
            Assert.Greater(playback.SamplesQueued, 0);
            Assert.IsTrue(lit, "the helmet light glowed while it talked");
            Assert.IsFalse(light.IsOn, "and is off when it stops");
            Assert.LessOrEqual(bot.Session.Stats.largestDatagram, NetProtocol.MaxDatagram);

            // The host talks: the bot hears slot 0, and the wrist shows MIC while it goes out.
            var wrist = Object.FindAnyObjectByType<WristDisplay>();
            bool mic = false;
            yield return MicSays(Speech(1.0), () => mic |= wrist.voiceText.text.Contains("MIC") && chat.IsTransmitting);
            yield return MicSays(new float[Rate / 2]);
            yield return Frames(5);
            Assert.Greater(heard.From(0), 40, "the host's voice reached the bot");
            Assert.IsTrue(mic, "the wrist showed MIC while transmitting");
            StringAssert.DoesNotContain("MIC", wrist.voiceText.text.Replace("MIC REC", ""), "and not after");

            // VOICE CHAT off: nothing goes out.
            int before = heard.Slots.Count;
            VoiceChat.EnabledOverride = false;
            yield return MicSays(Speech(0.8));
            Assert.AreEqual(before, heard.Slots.Count, "VOICE CHAT off sends nothing");
            VoiceChat.EnabledOverride = null;

            m_Net.Leave();
            yield return Frames(3);
            Assert.IsNull(VoiceChat.Active);
            Assert.AreEqual(0, Count<VoiceChat>(), "LEAVE removes voice chat");
            Assert.AreEqual(0, Count<VoicePlayback>(), "and every voice source");
            Assert.AreEqual(0, Count<VoiceTalkLight>(), "and every talk light");
            Assert.IsNull(m_Player.voice.MicTap, "and lets go of the microphone");
        }

        [UnityTest]
        public IEnumerator ThreePeers_AClientsVoiceIsRelayedToTheOtherClient_NeverBackToItself()
        {
            Assert.IsTrue(m_Net.Host());
            var chat = VoiceChat.Active;
            var a = BotAt(m_Net.Transport.Local, new Vector3(1f, 0f, 0f));
            var b = BotAt(m_Net.Transport.Local, new Vector3(-1f, 0f, 0f));
            var heardA = new Heard();
            var heardB = new Heard();
            a.Session.VoiceReceived += heardA.On;
            b.Session.VoiceReceived += heardB.On;
            yield return Until(() => chat.Playback(1) != null && chat.Playback(2) != null && b.World.Present[1]);
            yield return BotTalks(a, Speech(1.0));
            yield return Frames(10);
            Assert.Greater(heardB.From(1), 40, "the host relayed crew 2's voice to crew 3");
            Assert.AreEqual(0, heardA.From(1), "and never back to crew 2");
            Assert.AreEqual(0, heardA.Slots.Count);
            Assert.Greater(chat.Playback(1).FramesPlayed, 40, "the host hears it too");
            Assert.AreEqual(0, chat.Playback(2).FramesPlayed, "crew 3 said nothing");
            m_Net.Leave();
        }

        // ------------------------------------------------------------------ client

        RunStartParams HostRunWithSpider(int runIndex)
        {
            m_Director.CoopRideAlong = true;
            m_Director.CoopCreatureEnabled = true;
            var run = m_Director.BuildRunParams();
            m_Director.CoopRideAlong = false;
            m_Director.CoopCreatureEnabled = false;
            run.runIndex = (ushort)runIndex;
            run.planHash = run.ComputePlanHash();
            return run;
        }

        IEnumerator JoinSpiderRun(ScriptedHost host)
        {
            host.World.AutoStart = true;
            host.World.NextRun = HostRunWithSpider(5);
            Vector3 at = m_Player.head.position + Vector3.right;
            host.Avatar.headPosition = new NetVec3(at.x, at.y, at.z);
            Assert.IsTrue(m_Net.Join(host.Endpoint));
            yield return Until(() => m_Net.Mode == LobbyMode.Joined && m_Net.Crew(0) != null && m_Net.Crew(0).HasPose);
            Assert.IsNotNull(m_Net.GetComponent<VoiceChat>(), "NetSession attached voice chat when joining");
            m_Director.StartMission(m_Director.LocalPlayerId);
            yield return Until(() => m_Director.Phase == MissionPhase.Active);
            Assert.IsTrue(m_Director.CreatureAboard);
            yield return Frames(5);
        }

        [UnityTest]
        public IEnumerator Client_ItsOwnVoice_IsHeardByTheHostsSpider_AsThisPlayer()
        {
            var host = AddScriptedHost();
            yield return JoinSpiderRun(host);
            int noises = host.World.Calls[NetMsg.Noise];
            yield return MicSays(Speech(1.5, 3f));
            yield return Frames(10);
            Assert.Greater(host.World.Calls[NetMsg.Noise], noises, "the client's voice went to the host as noise");
            Assert.AreEqual(NoiseKind.Voice, host.World.LastNoiseKind);
            Assert.AreEqual(1, host.World.LastSlot, "credited to this player");
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Client_AFriendsVoiceFromTheSpeakers_IsNotSentToTheSpiderAsThisPlayer()
        {
            var host = AddScriptedHost();
            yield return JoinSpiderRun(host);
            var chat = VoiceChat.Active;
            yield return Until(() => chat.Playback(0) != null);
            var heard = new Heard();
            host.Session.VoiceReceived += heard.On;
            float[] speech = Speech(3.0, 1f, 2);
            var voice = new BotVoice(speech);
            int noises = host.World.Calls[NetMsg.Noise];
            long echoBefore = m_Player.voice.EchoBlocks;
            int playedFrom = -1, frame = 0;
            while (!voice.Done)
            {
                Tick();
                voice.Step((seq, data, count) => host.Session.SendVoice(seq, data, 0, VoiceFormat.FrameBytes, count) > 0);
                frame++;
                if (playedFrom < 0 && chat.IsSpeaking(0)) playedFrom = frame;
                if (playedFrom >= 0)
                {
                    // The host's voice comes out of this headset's speakers into its microphone at half level.
                    int at = (frame - playedFrom) * Frame;
                    if (at + Frame <= speech.Length)
                    {
                        var leak = new float[Frame];
                        for (int i = 0; i < Frame; i++) leak[i] = speech[at + i] * 0.5f;
                        m_Player.voice.InjectMicSamples(leak, Rate);
                    }
                }
                yield return null;
            }
            yield return Frames(10);
            Assert.Greater(playedFrom, 0, "the host's voice played here");
            Assert.Greater(m_Player.voice.EchoBlocks - echoBefore, 80, "the microphone blocks were recognised as the speakers");
            Assert.AreEqual(noises, host.World.Calls[NetMsg.Noise], "the spider on the host was not told this player talked");
            Assert.AreEqual(0, heard.Slots.Count, "and the echo was not sent back as this player's voice");
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Client_WhenTheHostCloses_VoiceChatGoesWithTheSession()
        {
            var host = AddScriptedHost();
            Assert.IsTrue(m_Net.Join(host.Endpoint));
            yield return Until(() => m_Net.Mode == LobbyMode.Joined && m_Net.Crew(0) != null && m_Net.Crew(0).HasPose);
            yield return Frames(3);
            Assert.IsNotNull(VoiceChat.Active);
            Assert.Greater(Count<VoicePlayback>(), 0);
            host.Session.Close(Now);
            yield return Until(() => m_Net.Mode == LobbyMode.Offline);
            yield return Frames(3);
            Assert.IsNull(VoiceChat.Active, "host closed: voice chat is gone");
            Assert.AreEqual(0, Count<VoiceChat>());
            Assert.AreEqual(0, Count<VoicePlayback>());
            Assert.IsNull(m_Player.voice.MicTap);
        }
    }

    /// <summary>Voice both ways once over real UDP sockets on 127.0.0.1.</summary>
    public class VoiceSessionUdpPlayTests : NetPlayFixture
    {
        [UnityTest]
        public IEnumerator Udp_ABotsVoicePlaysOnTheHost_AndTheHostsVoiceReachesTheBot()
        {
            Assert.IsTrue(m_Net.Host());
            var chat = VoiceChat.Active;
            Assert.IsNotNull(chat);
            var bot = AddBot(m_Net.Transport.Local);
            Vector3 at = m_Player.head.position + Vector3.right;
            bot.Avatar.headPosition = new NetVec3(at.x, at.y, at.z);
            int fromHost = 0;
            bot.Session.VoiceReceived += (slot, seq, buffer, offset, length) => { if (slot == 0) fromHost++; };
            yield return Until(() => chat.Playback(1) != null, maxFrames: 100000, maxRealSeconds: 8f);
            Assert.IsNotNull(chat.Playback(1));

            var encoder = new VoiceEncoder();
            var packet = new byte[2 * VoiceFormat.FrameBytes];
            var signal = new float[VoiceFormat.SampleRate];
            for (int i = 0; i < signal.Length; i++) signal[i] = 0.2f * Mathf.Sin(2f * Mathf.PI * 220f * i / VoiceFormat.SampleRate) * (0.6f + 0.4f * Mathf.Sin(2f * Mathf.PI * 4f * i / VoiceFormat.SampleRate));
            ushort seq0 = 100;
            for (int f = 0; f + 1 < 50; f += 2)
            {
                encoder.Encode(signal, f * VoiceFormat.FrameSamples, packet, 0);
                encoder.Encode(signal, (f + 1) * VoiceFormat.FrameSamples, packet, VoiceFormat.FrameBytes);
                bot.Session.SendVoice((ushort)(seq0 + f), packet, 0, VoiceFormat.FrameBytes, 2);
                float until = Time.unscaledTime + 0.04f;
                while (Time.unscaledTime < until) { Tick(); yield return null; }
            }
            yield return Until(() => chat.Playback(1).FramesPlayed >= 40, maxFrames: 100000, maxRealSeconds: 3f);
            Assert.GreaterOrEqual(chat.Playback(1).FramesPlayed, 40, "the bot's voice played on the host over UDP");

            for (int at2 = 0; at2 + VoiceFormat.FrameSamples <= signal.Length; at2 += VoiceFormat.FrameSamples)
            {
                var chunk = new float[VoiceFormat.FrameSamples];
                Array.Copy(signal, at2, chunk, 0, chunk.Length);
                m_Player.voice.InjectMicSamples(chunk, VoiceFormat.SampleRate);
                float until = Time.unscaledTime + 0.02f;
                while (Time.unscaledTime < until) { Tick(); yield return null; }
            }
            yield return Until(() => fromHost > 20, maxFrames: 100000, maxRealSeconds: 3f);
            // (a one-second test tone; the voice gate takes a moment to open on it)
            Assert.Greater(fromHost, 20, "the host's voice reached the bot over UDP");

            m_Net.Leave();
            bot.Session.Dispose();
            yield return Until(() => UdpTransport.LiveCount == 0, maxFrames: 100000, maxRealSeconds: (float)NetSession.HostLingerSeconds + 2f);
            Assert.IsNull(VoiceChat.Active);
        }

        protected override bool UseMemoryTransport => false;
    }
}

using System;
using System.Collections.Generic;
using NUnit.Framework;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Core.Voice;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// The Mimic in co-op, the pure parts: the creature's pseudo-sender in the Voice message (the host may use it, a
    /// client may not), the "voice off" notice, the codec untouched, the real-time sender, the render, and the crew's
    /// phrase memory (caps, forgetting, who it prefers).
    /// </summary>
    public class MimicCoopCoreTests
    {
        NetTestRig m_Rig;

        [SetUp]
        public void SetUp() { m_Rig = new NetTestRig(); }

        [TearDown]
        public void TearDown()
        {
            m_Rig.Dispose();
            Assert.AreEqual(0, m_Rig.Hub.LiveCount);
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
            public int From(byte slot) => Frames.FindAll(f => f.slot == slot).Count;
        }

        /// <summary>Rewrites the slot byte of a client's outgoing voice datagrams.</summary>
        sealed class SlotForger : INetTransport
        {
            readonly INetTransport m_Inner;
            readonly byte[] m_Copy = new byte[NetProtocol.MaxDatagram + 64];
            public byte? Slot;
            public SlotForger(INetTransport inner) { m_Inner = inner; }
            public NetEndpoint Local => m_Inner.Local;
            public void Dispose() { m_Inner.Dispose(); }
            public bool Receive(out NetEndpoint from, byte[] buffer, out int length) => m_Inner.Receive(out from, buffer, out length);
            public bool Send(NetEndpoint to, byte[] data, int length)
            {
                if (Slot.HasValue && length > DatagramHeader.Size && data[DatagramHeader.Size] == NetMsg.Voice)
                {
                    Buffer.BlockCopy(data, 0, m_Copy, 0, length);
                    m_Copy[DatagramHeader.Size + 1] = Slot.Value;
                    return m_Inner.Send(to, m_Copy, length);
                }
                return m_Inner.Send(to, data, length);
            }
        }

        static byte[] Frames(int seed, int count = 2)
        {
            var bytes = new byte[VoiceFormat.FrameBytes * count];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        static float[] Tone(double seconds, float level = 0.3f, double hz = 220)
        {
            var x = new float[(int)(VoiceFormat.SampleRate * seconds)];
            for (int i = 0; i < x.Length; i++)
                x[i] = (float)(level * Math.Sin(2 * Math.PI * hz * i / VoiceFormat.SampleRate) * (0.7 + 0.3 * Math.Sin(2 * Math.PI * 4 * i / VoiceFormat.SampleRate)));
            return x;
        }

        // ------------------------------------------------------------------ wire

        [Test]
        public void TheCreaturesVoice_IsAcceptedFromTheHost_AndReachesEveryClientIntact()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            var heardA = new Heard();
            var heardB = new Heard();
            a.Session.VoiceReceived += heardA.On;
            b.Session.VoiceReceived += heardB.On;
            byte[] frames = Frames(4);
            Assert.AreEqual(2, m_Rig.Host.SendCreatureVoice(300, frames, 0, VoiceFormat.FrameBytes, 2), "to both clients");
            m_Rig.Step(0.05);
            Assert.AreEqual(2, heardA.From(VoiceMsg.CreatureSlot));
            Assert.AreEqual(2, heardB.From(VoiceMsg.CreatureSlot));
            Assert.AreEqual(300, heardA.Frames[0].seq);
            Assert.AreEqual(301, heardA.Frames[1].seq);
            for (int i = 0; i < VoiceFormat.FrameBytes; i++) Assert.AreEqual(frames[VoiceFormat.FrameBytes + i], heardB.Frames[1].bytes[i]);
            Assert.AreEqual(2, m_Rig.Host.CreatureVoiceSent, "one message per client");
            Assert.AreEqual(0, a.Session.Stats.malformed);
        }

        [Test]
        public void TheCreaturesVoice_IsRefusedFromAClient_AndNotRelayed()
        {
            SlotForger forger = null;
            NetTestRig.Member a = m_Rig.AddClient(wrap: inner => forger = new SlotForger(inner));
            Assert.IsTrue(m_Rig.StepUntil(() => a.Session.Welcomed, 5.0));
            NetTestRig.Member b = m_Rig.Join();
            var host = new Heard();
            var heardB = new Heard();
            m_Rig.Host.VoiceReceived += host.On;
            b.Session.VoiceReceived += heardB.On;

            forger.Slot = VoiceMsg.CreatureSlot;
            long rejected = m_Rig.Host.Stats.rejected;
            Assert.IsTrue(a.Session.SendVoice(10, Frames(1), 0, VoiceFormat.FrameBytes, 2));
            m_Rig.Step(0.05);
            Assert.AreEqual(0, host.Frames.Count, "a client cannot speak as the creature");
            Assert.AreEqual(0, heardB.Frames.Count, "nor have it relayed");
            Assert.Greater(m_Rig.Host.Stats.rejected, rejected, "counted as refused");
            Assert.AreEqual(ClientState.Connected, a.Session.State, "and it is not thrown out for it");

            // Other made-up senders are still malformed; its own voice still goes through.
            foreach (byte bad in new byte[] { 4, 6, 8, 255 })
            {
                forger.Slot = bad;
                long malformed = m_Rig.Host.Stats.malformed;
                a.Session.SendVoice(20, Frames(2), 0, VoiceFormat.FrameBytes, 2);
                m_Rig.Step(0.05);
                Assert.Greater(m_Rig.Host.Stats.malformed, malformed, "slot " + bad);
            }
            forger.Slot = null;
            a.Session.SendVoice(30, Frames(3), 0, VoiceFormat.FrameBytes, 2);
            m_Rig.Step(0.05);
            Assert.AreEqual(2, host.From(a.Slot));
            Assert.AreEqual(2, heardB.From(a.Slot));
            Assert.AreEqual(0, heardB.From(VoiceMsg.CreatureSlot));
        }

        [Test]
        public void TheVoiceOffNotice_IsOneByte_AndReachesTheHostFromItsSender()
        {
            NetTestRig.Member a = m_Rig.Join();
            var host = new Heard();
            m_Rig.Host.VoiceReceived += host.On;
            Assert.IsTrue(a.Session.SendVoice(0, VoiceMsg.OffNotice, 0, VoiceMsg.OffNoticeLength, 1));
            m_Rig.Step(0.05);
            Assert.AreEqual(1, host.From(a.Slot));
            Assert.IsTrue(VoiceMsg.IsOffNotice(host.Frames[0].bytes.Length));
            Assert.IsFalse(VoiceMsg.IsOffNotice(VoiceFormat.FrameBytes), "a real frame is never mistaken for it");
        }

        [Test]
        public void TheCodec_IsUnchanged()
        {
            Assert.AreEqual(16000, VoiceFormat.SampleRate);
            Assert.AreEqual(320, VoiceFormat.FrameSamples);
            Assert.AreEqual(123, VoiceFormat.FrameBytes);
            Assert.AreEqual(2, VoiceFormat.FramesPerPacket);
            Assert.AreEqual(160, VoiceMsg.MaxFrameBytes);
            Assert.AreEqual(4 + 2 * 161, VoiceMsg.MaxSize);

            // The creature's frames are ordinary voice frames: any decoder plays them.
            var sent = new List<byte[]>();
            var sender = new CreatureVoiceSender();
            float[] tone = Tone(0.4);
            sender.Begin(tone, 0.0);
            for (double t = 0; sender.Active && t < 2.0; t += 0.02)
                sender.Pump(t, (seq, frames, offset, bytes, count) =>
                {
                    for (int i = 0; i < count; i++)
                    {
                        var f = new byte[bytes];
                        Buffer.BlockCopy(frames, offset + i * bytes, f, 0, bytes);
                        sent.Add(f);
                    }
                    return true;
                });
            Assert.AreEqual(20, sent.Count);
            var decoder = new VoiceDecoder();
            var output = new float[VoiceFormat.FrameSamples * sent.Count];
            for (int i = 0; i < sent.Count; i++)
            {
                Assert.AreEqual(VoiceFormat.FrameBytes, sent[i].Length);
                Assert.IsTrue(decoder.Decode(sent[i], 0, sent[i].Length, output, i * VoiceFormat.FrameSamples));
            }
            // Level survives the codec (the filters delay it by a few samples, so compare loudness).
            float inDb = VoiceFormat.ToDb(VoiceFormat.Rms(tone, 1600, 3200));
            float outDb = VoiceFormat.ToDb(VoiceFormat.Rms(output, 1600, 3200));
            Assert.That(outDb - inDb, Is.InRange(-2f, 1f));
        }

        // ------------------------------------------------------------------ render and sender

        [Test]
        public void TheRender_IsLowerAndSlower_AndNeverLouder()
        {
            float[] phrase = Tone(1.0, 0.9f);
            float[] warped = MimicVoiceRender.Warble(phrase, 16000, 0.35f);
            Assert.AreEqual(phrase.Length, warped.Length);
            for (int i = 0; i < phrase.Length; i++) Assert.LessOrEqual(Math.Abs(warped[i]), Math.Abs(phrase[i]) + 1e-6f);
            float[] slow = MimicVoiceRender.AtPitch(warped, 16000, 0.86f);
            Assert.AreEqual(1.0 / 0.86, slow.Length / 16000.0, 0.01, "as long as an AudioSource at pitch 0.86 plays it");
            float peakIn = 0f, peakOut = 0f;
            foreach (float v in warped) peakIn = Math.Max(peakIn, Math.Abs(v));
            foreach (float v in slow) peakOut = Math.Max(peakOut, Math.Abs(v));
            Assert.LessOrEqual(peakOut, peakIn + 1e-6f);
            // From another rate it lands at the voice rate.
            Assert.AreEqual(slow.Length, MimicVoiceRender.AtPitch(new float[48000], 48000, 0.86f).Length, 2);
        }

        [Test]
        public void TheSender_KeepsToRealTime_InPairs_AndStopsWhenTold()
        {
            var sender = new CreatureVoiceSender(100);
            var packets = new List<(ushort seq, int count, double at)>();
            double now = 0;
            VoicePacketSink sink = (seq, frames, offset, bytes, count) => { packets.Add((seq, count, now)); return true; };
            sender.Begin(Tone(1.0), now);
            Assert.AreEqual(50, sender.FrameCount);
            Assert.AreEqual(2, sender.Pump(now, sink), "the first pair goes at once");
            now = 0.5;
            sender.Pump(now, sink);
            Assert.That(sender.FramesSent, Is.InRange(26, 27), "no faster than it is spoken");
            for (; sender.Active && now < 3; now += 0.02) sender.Pump(now, sink);
            Assert.IsFalse(sender.Active);
            Assert.AreEqual(50, sender.FramesSent);
            Assert.That(now, Is.InRange(0.9, 1.1));
            foreach (var p in packets) Assert.AreEqual(2, p.count);
            for (int i = 1; i < packets.Count; i++) Assert.AreEqual((ushort)(packets[i - 1].seq + 2), packets[i].seq);

            sender.Begin(Tone(2.0), 10.0);
            ushort start = (ushort)(packets[packets.Count - 1].seq + 2);
            int before = packets.Count;
            now = 10.0;
            sender.Pump(now, sink);
            Assert.GreaterOrEqual(VoiceFormat.SeqDelta(packets[before].seq, start), CreatureVoiceSender.PhraseGap - 2, "a new phrase is a new talk spurt");
            sender.Stop();
            now = 11.0;
            Assert.AreEqual(0, sender.Pump(now, sink), "stopped: nothing more goes out");
        }

        // ------------------------------------------------------------------ the crew's phrases

        static void Speak(CrewPhraseBank bank, int slot, float[] signal, ref double now, ref ushort seq)
        {
            var encoder = new VoiceEncoder();
            var frame = new byte[VoiceFormat.FrameBytes];
            for (int at = 0; at + VoiceFormat.FrameSamples <= signal.Length; at += VoiceFormat.FrameSamples)
            {
                encoder.Encode(signal, at, frame, 0);
                bank.Push(slot, seq++, frame, 0, frame.Length, now);
                now += VoiceFormat.FrameSeconds;
                bank.Pump(now);
            }
            for (int i = 0; i < 60; i++)
            {
                now += VoiceFormat.FrameSeconds;
                bank.Pump(now);
            }
        }

        [Test]
        public void ACrewMembersVoiceFrames_BecomePhrases_CappedInNumberAndLength()
        {
            var bank = new CrewPhraseBank();
            double now = 100;
            ushort seq = 7;
            Speak(bank, 2, Tone(0.8), ref now, ref seq);
            Assert.AreEqual(1, bank.CountOf(2), "one phrase of crew 2");
            Assert.AreEqual(1, bank.Version);
            Assert.AreEqual(2, bank.NewestSlot);
            Assert.That(bank.Recorder(2).Newest.Length / 16000f, Is.InRange(0.7f, 1.3f));
            for (int i = 0; i < 5; i++)
            {
                seq += 100;
                Speak(bank, 2, Tone(0.6, 0.3f, 200 + 30 * i), ref now, ref seq);
            }
            Assert.AreEqual(CrewPhraseBank.PhrasesPerSpeaker, bank.CountOf(2), "only a few are kept");
            seq += 100;
            Speak(bank, 2, Tone(7.0), ref now, ref seq);
            Assert.LessOrEqual(bank.Recorder(2).Newest.Length, (int)(CrewPhraseBank.MaxPhraseSeconds * 16000), "a few seconds each at most");

            bank.Forget(2);
            Assert.AreEqual(0, bank.Count, "forgotten");
            Assert.IsNull(bank.Recorder(2));
        }

        [Test]
        public void AnAttachedRecorder_IsUsedButNeverClearedByTheBank()
        {
            var bank = new CrewPhraseBank();
            var own = new TrashAstronauts.Core.PhraseRecorder(16000);
            bank.Attach(0, own);
            own.Add(Tone(0.5));
            bank.Pump(1.0);
            Assert.AreEqual(1, bank.CountOf(0));
            Assert.AreEqual(0, bank.NewestSlot);
            Assert.IsFalse(bank.Push(0, 1, Frames(1, 1), 0, VoiceFormat.FrameBytes, 1.0), "frames are never taken for an attached slot");
            bank.Clear();
            Assert.AreEqual(0, bank.Count);
            Assert.AreEqual(1, own.Count, "the host's own recorder keeps its own rules (VoiceSensor)");
        }

        [Test]
        public void ThePick_PrefersSomeoneNotNearTheOneBeingStalked()
        {
            var bank = new CrewPhraseBank();
            double now = 100;
            ushort seq = 1;
            Speak(bank, 1, Tone(0.6, 0.3f, 300), ref now, ref seq);
            Speak(bank, 2, Tone(0.6, 0.3f, 500), ref now, ref seq);
            Assert.AreEqual(2, bank.NewestSlot);
            var random = new Random(5);

            // Stalking crew 2 (who just spoke): its echo uses crew 1's newest instead.
            Assert.IsTrue(bank.TryPick(true, s => s != 2, random, out float[] phrase, out int rate, out int who));
            Assert.AreEqual(1, who);
            Assert.AreSame(bank.Recorder(1).Newest, phrase);
            Assert.AreEqual(16000, rate);
            for (int i = 0; i < 20; i++)
            {
                Assert.IsTrue(bank.TryPick(false, s => s != 2, random, out phrase, out rate, out who));
                Assert.AreEqual(1, who, "and so do its repeats");
            }
            // Nobody preferred has anything: anybody's.
            Assert.IsTrue(bank.TryPick(true, s => s == 3, random, out phrase, out rate, out who));
            Assert.AreEqual(2, who, "the newest of all");
            // No preference: the newest of all.
            Assert.IsTrue(bank.TryPick(true, null, random, out phrase, out rate, out who));
            Assert.AreEqual(2, who);
            bank.Clear();
            Assert.IsFalse(bank.TryPick(true, null, random, out phrase, out rate, out who));
        }
    }
}

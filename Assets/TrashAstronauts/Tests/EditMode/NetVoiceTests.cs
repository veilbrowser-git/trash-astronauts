using System;
using System.Collections.Generic;
using NUnit.Framework;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Core.Voice;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// The Voice stream message on real sessions over the in-memory network: client to host to the other clients
    /// (never back to the sender, never to an Away player), the host's own voice to everyone, loss and reordering,
    /// the per-client rate limit, and garbage that must be dropped without throwing.
    /// </summary>
    public class NetVoiceTests
    {
        NetTestRig m_Rig;

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

        [SetUp]
        public void SetUp() { m_Rig = new NetTestRig(); }

        [TearDown]
        public void TearDown()
        {
            m_Rig.Dispose();
            Assert.AreEqual(0, m_Rig.Hub.LiveCount);
            Assert.LessOrEqual(m_Rig.Hub.LargestDatagram, NetProtocol.MaxDatagram);
            Assert.AreEqual(0, m_Rig.Hub.OversizeCount);
        }

        static byte[] Frames(int seed, int count = 2)
        {
            var bytes = new byte[VoiceFormat.FrameBytes * count];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        static void AssertFrame(byte[] expected, int frame, byte[] actual)
        {
            Assert.AreEqual(VoiceFormat.FrameBytes, actual.Length);
            for (int i = 0; i < actual.Length; i++) Assert.AreEqual(expected[frame * VoiceFormat.FrameBytes + i], actual[i]);
        }

        [Test]
        public void Protocol_IsVersion2_AndAVersion1BuildIsRefused()
        {
            Assert.GreaterOrEqual(NetProtocol.Version, 2, "Voice changed the message set"); Assert.AreEqual(4, NetProtocol.Version, "voice and suit colour (3), then everyone grabs and the helmet lamp bit (4): each refuses the others");
            NetTestRig.Member old = m_Rig.AddClient(protocol: 1);
            Assert.IsTrue(m_Rig.StepUntil(() => old.Session.State == ClientState.Ended, 3.0));
            Assert.AreEqual(RefuseReason.VersionMismatch, old.Session.LastRefusal);
            Assert.LessOrEqual(VoiceMsg.MaxSize, 400);
            Assert.Less(DatagramHeader.Size + 1 + VoiceMsg.MaxSize + 1, NetProtocol.MaxDatagram / 2);
        }

        [Test]
        public void ThreePeers_EachVoiceReachesTheOthers_NeverItsSender()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            var host = new Heard();
            var heardA = new Heard();
            var heardB = new Heard();
            m_Rig.Host.VoiceReceived += host.On;
            a.Session.VoiceReceived += heardA.On;
            b.Session.VoiceReceived += heardB.On;

            byte[] fromA = Frames(1);
            Assert.IsTrue(a.Session.SendVoice(500, fromA, 0, VoiceFormat.FrameBytes, 2));
            m_Rig.Step(0.05);
            Assert.AreEqual(2, host.From(1), "the host hears slot 1");
            Assert.AreEqual(2, heardB.From(1), "and relays it to slot 2");
            Assert.AreEqual(0, heardA.Frames.Count, "never back to the sender");
            Assert.AreEqual(500, host.Frames[0].seq);
            Assert.AreEqual(501, host.Frames[1].seq);
            AssertFrame(fromA, 0, heardB.Frames[0].bytes);
            AssertFrame(fromA, 1, heardB.Frames[1].bytes);

            byte[] fromHost = Frames(2, 1);
            Assert.AreEqual(2, m_Rig.Host.SendVoice(65535, fromHost, 0, VoiceFormat.FrameBytes, 1), "to both clients");
            byte[] fromB = Frames(3);
            Assert.IsTrue(b.Session.SendVoice(7, fromB, 0, VoiceFormat.FrameBytes, 2));
            m_Rig.Step(0.05);
            Assert.AreEqual(1, heardA.From(0));
            Assert.AreEqual(1, heardB.From(0));
            Assert.AreEqual(65535, heardA.Frames.Find(f => f.slot == 0).seq);
            Assert.AreEqual(2, heardA.From(2), "slot 2 reaches slot 1 through the host");
            Assert.AreEqual(2, host.From(2));
            Assert.AreEqual(0, heardB.From(2), "and not slot 2 itself");
            Assert.AreEqual(0, host.From(0), "the host does not hear itself");
            AssertFrame(fromB, 1, heardA.Frames.FindLast(f => f.slot == 2).bytes);
        }

        [Test]
        public void Voice_IsNotSentBeforeTheWelcome_NorToAnAwayPlayer()
        {
            NetTestRig.Member early = m_Rig.AddClient();
            Assert.IsFalse(early.Session.SendVoice(1, Frames(1), 0, VoiceFormat.FrameBytes, 2), "not connected yet");
            Assert.IsTrue(m_Rig.StepUntil(() => early.Session.Welcomed, 5.0));
            NetTestRig.Member b = m_Rig.Join();
            var heardB = new Heard();
            b.Session.VoiceReceived += heardB.On;

            b.Session.SetAway(true, m_Rig.Now);
            Assert.IsTrue(m_Rig.StepUntil(() => m_Rig.Host.IsAway(b.Slot), 2.0));
            Assert.AreEqual(1, m_Rig.Host.SendVoice(1, Frames(4), 0, VoiceFormat.FrameBytes, 2), "only the player who is here");
            early.Session.SendVoice(10, Frames(5), 0, VoiceFormat.FrameBytes, 2);
            m_Rig.Step(0.1);
            Assert.AreEqual(0, heardB.Frames.Count, "an Away player gets no voice");

            b.Session.SetAway(false, m_Rig.Now);
            Assert.IsTrue(m_Rig.StepUntil(() => !m_Rig.Host.IsAway(b.Slot), 2.0));
            early.Session.SendVoice(12, Frames(6), 0, VoiceFormat.FrameBytes, 2);
            m_Rig.Step(0.05);
            Assert.AreEqual(2, heardB.Frames.Count, "back: voice flows again");

            Assert.IsFalse(early.Session.SendVoice(1, Frames(1), 0, VoiceFormat.FrameBytes, 3), "at most two frames");
            Assert.IsFalse(early.Session.SendVoice(1, new byte[400], 0, 200, 2), "frames over 160 bytes do not fit");
            Assert.IsFalse(early.Session.SendVoice(1, new byte[10], 0, VoiceFormat.FrameBytes, 1), "a short buffer");
        }

        [Test]
        public void LossAndReordering_AreTolerated_FramesThatArriveAreIntact()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            var heardB = new Heard();
            b.Session.VoiceReceived += heardB.On;
            m_Rig.Hub.Loss = 0.3;
            m_Rig.Hub.Reorder = 0.3;
            m_Rig.Hub.Jitter = 0.03;
            var sent = new Dictionary<ushort, byte[]>();
            ushort seq = 60000;
            m_Rig.EachStep = now =>
            {
                // 25 packets a second of two frames each, like a talking player
                if (sent.Count >= 400) return;
                if (((int)Math.Round((now - 1000.0) / 0.01)) % 4 != 0) return;
                byte[] frames = Frames(seq);
                a.Session.SendVoice(seq, frames, 0, VoiceFormat.FrameBytes, 2);
                sent[seq] = frames;
                sent[(ushort)(seq + 1)] = frames;
                seq += 2;
            };
            Assert.DoesNotThrow(() => m_Rig.Step(10.0));
            m_Rig.Hub.Loss = 0;
            m_Rig.Step(1.0);
            int got = heardB.Frames.Count;
            TestContext.WriteLine("sent " + sent.Count + ", slot 2 got " + got + ", stale datagrams " + b.Session.Stats.staleStreams);
            Assert.AreEqual(400, sent.Count);
            Assert.That(got, Is.InRange(400 * 0.35, 400 * 0.75), "two hops of 30% loss");
            var seen = new HashSet<ushort>();
            foreach (var f in heardB.Frames)
            {
                Assert.AreEqual(1, f.slot);
                Assert.IsTrue(sent.ContainsKey(f.seq));
                int index = (ushort)(f.seq - 60000) % 2;
                AssertFrame(sent[f.seq], index, f.bytes);
                seen.Add(f.seq);
            }
            Assert.Greater(b.Session.Stats.staleStreams + m_Rig.Host.Stats.staleStreams, 0, "some voice datagrams were overtaken and still used");
        }

        [Test]
        public void TheHost_LimitsEachClientTo60FramesASecond()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            var host = new Heard();
            var heardB = new Heard();
            m_Rig.Host.VoiceReceived += host.On;
            b.Session.VoiceReceived += heardB.On;
            m_Rig.Step(0.5);

            // A flood: 100 packets (200 frames) in one frame. Only the burst allowance gets through.
            for (int i = 0; i < 100; i++) a.Session.SendVoice((ushort)(i * 2), Frames(i), 0, VoiceFormat.FrameBytes, 2);
            m_Rig.Step(0.05);
            Assert.LessOrEqual(host.Frames.Count, VoiceMsg.BurstFrames + 4);
            Assert.AreEqual(host.Frames.Count, heardB.Frames.Count, "what is refused is not relayed either");
            Assert.Greater(m_Rig.Host.Stats.rejected, 80);

            // Then 3 s at 100 frames a second: about 60 a second are taken.
            host.Frames.Clear();
            ushort seq = 1000;
            m_Rig.EachStep = now => { a.Session.SendVoice(seq, Frames(seq), 0, VoiceFormat.FrameBytes, 1); seq++; };
            m_Rig.Step(3.0);
            m_Rig.EachStep = null;
            Assert.That(host.Frames.Count, Is.InRange(170, 200), "60 frames a second");

            // A normal talker (50 frames a second) loses nothing.
            host.Frames.Clear();
            m_Rig.Step(1.0);
            int tick = 0;
            m_Rig.EachStep = now => { if (tick++ % 4 == 0) { a.Session.SendVoice(seq, Frames(seq), 0, VoiceFormat.FrameBytes, 2); seq += 2; } };
            m_Rig.Step(2.0);
            m_Rig.EachStep = null;
            m_Rig.Step(0.1);
            Assert.AreEqual(100, host.Frames.Count);
        }

        /// <summary>Rewrites the client's outgoing voice datagrams into garbage of a chosen kind.</summary>
        sealed class VoiceTamper : INetTransport
        {
            readonly INetTransport m_Inner;
            readonly byte[] m_Copy = new byte[NetProtocol.MaxDatagram + 64];
            public Func<byte[], int, int> Rewrite;
            public int Rewritten;

            public VoiceTamper(INetTransport inner) { m_Inner = inner; }
            public NetEndpoint Local => m_Inner.Local;
            public void Dispose() { m_Inner.Dispose(); }
            public bool Receive(out NetEndpoint from, byte[] buffer, out int length) => m_Inner.Receive(out from, buffer, out length);

            public bool Send(NetEndpoint to, byte[] data, int length)
            {
                if (Rewrite != null && length > DatagramHeader.Size && data[DatagramHeader.Size] == NetMsg.Voice)
                {
                    Buffer.BlockCopy(data, 0, m_Copy, 0, length);
                    Rewritten++;
                    return m_Inner.Send(to, m_Copy, Rewrite(m_Copy, length));
                }
                return m_Inner.Send(to, data, length);
            }
        }

        [Test]
        public void MalformedVoice_IsDroppedWithoutThrowing_AndAForgedSlotIsReplaced()
        {
            VoiceTamper tamper = null;
            NetTestRig.Member a = m_Rig.AddClient(wrap: inner => tamper = new VoiceTamper(inner));
            Assert.IsTrue(m_Rig.StepUntil(() => a.Session.Welcomed, 5.0));
            NetTestRig.Member b = m_Rig.Join();
            var host = new Heard();
            var heardB = new Heard();
            m_Rig.Host.VoiceReceived += host.On;
            b.Session.VoiceReceived += heardB.On;
            const int body = DatagramHeader.Size + 1; // where the Voice message starts

            var cases = new List<(string name, Func<byte[], int, int> rewrite)>
            {
                ("slot out of range", (d, n) => { d[body] = 9; return n; }),
                ("no frames", (d, n) => { d[body + 3] = 0; return n; }),
                ("three frames", (d, n) => { d[body + 3] = 3; return n; }),
                ("frame of length 0", (d, n) => { d[body + 4] = 0; return n; }),
                ("frame longer than 160", (d, n) => { d[body + 4] = 200; return n; }),
                ("frame longer than the datagram", (d, n) => { d[body + 4] = 150; return n; }),
                ("cut short", (d, n) => n - 20),
                ("cut inside the header", (d, n) => body + 2),
                ("no End byte", (d, n) => n - 1),
            };
            foreach (var c in cases)
            {
                long malformed = m_Rig.Host.Stats.malformed;
                tamper.Rewrite = c.rewrite;
                Assert.DoesNotThrow(() => a.Session.SendVoice(1, Frames(1), 0, VoiceFormat.FrameBytes, 2), c.name);
                Assert.DoesNotThrow(() => m_Rig.Step(0.05), c.name);
                Assert.AreEqual(0, host.Frames.Count, c.name + " was accepted");
                Assert.Greater(m_Rig.Host.Stats.malformed, malformed, c.name + " was not counted as malformed");
            }
            Assert.AreEqual(0, heardB.Frames.Count);

            // A client that writes someone else's slot is heard as itself.
            tamper.Rewrite = (d, n) => { d[body] = 0; return n; };
            a.Session.SendVoice(1, Frames(1), 0, VoiceFormat.FrameBytes, 2);
            m_Rig.Step(0.05);
            Assert.AreEqual(2, host.From(a.Slot));
            Assert.AreEqual(2, heardB.From(a.Slot));
            Assert.AreEqual(0, heardB.From(0), "it cannot pretend to be the host");

            // Random garbage behind a valid header never throws.
            var random = new Random(3);
            tamper.Rewrite = (d, n) =>
            {
                int length = random.Next(body, NetProtocol.MaxDatagram);
                for (int i = body; i < length; i++) d[i] = (byte)random.Next(256);
                return length;
            };
            for (int i = 0; i < 300; i++)
            {
                Assert.DoesNotThrow(() => a.Session.SendVoice((ushort)i, Frames(i), 0, VoiceFormat.FrameBytes, 2));
                Assert.DoesNotThrow(() => m_Rig.Step(0.01));
            }
            Assert.AreEqual(ClientState.Connected, a.Session.State, "garbage does not end the session");
            Assert.AreEqual(ClientState.Connected, b.Session.State);
        }

        [Test]
        public void ARecordedVoiceMessage_ReplaysIntoAPassiveClient()
        {
            NetTestRig.Member a = m_Rig.Join();
            var tape = new List<(byte type, byte[] bytes)>();
            m_Rig.Host.Tap = (outbound, peer, type, buffer, offset, length, time) =>
            {
                if (!outbound || type != NetMsg.Voice) return;
                var copy = new byte[length];
                Buffer.BlockCopy(buffer, offset, copy, 0, length);
                tape.Add((type, copy));
            };
            m_Rig.Host.SendVoice(42, Frames(9), 0, VoiceFormat.FrameBytes, 2);
            Assert.AreEqual(1, tape.Count);

            var passive = new NetSessionClient(m_Rig.Hub.Create(), new BotClientWorld(), new NetClientConfig { contentHash = NetTestRig.Content });
            passive.SetReplaySlot(2);
            var heard = new Heard();
            passive.VoiceReceived += heard.On;
            Assert.IsTrue(passive.ApplyRecorded(tape[0].type, tape[0].bytes, 0, tape[0].bytes.Length, 1.0));
            passive.Dispose();
            Assert.AreEqual(2, heard.Frames.Count);
            Assert.AreEqual(42, heard.Frames[0].seq);
        }
    }
}

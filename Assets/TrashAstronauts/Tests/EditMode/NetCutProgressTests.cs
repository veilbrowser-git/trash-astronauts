using System;
using System.Collections.Generic;
using NUnit.Framework;
using TrashAstronauts.Core.Net;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// The weld progress stream (CutProgressMsg): its codec, its id, and its relay through the real host and client
    /// sessions on the in-memory hub (client -> host -> the other clients; host -> every client).
    /// </summary>
    public class NetCutProgressTests
    {
        NetTestRig m_Rig;

        [SetUp]
        public void SetUp() => m_Rig = new NetTestRig(7);

        [TearDown]
        public void TearDown() => m_Rig.Dispose();

        struct Heard
        {
            public byte slot, panel, index, progress;
            public override string ToString() => slot + ":" + panel + "/" + index + "=" + progress;
        }

        static List<Heard> Listen(Action<Action<byte, byte, byte, byte>> subscribe)
        {
            var list = new List<Heard>();
            subscribe((slot, panel, index, progress) => list.Add(new Heard { slot = slot, panel = panel, index = index, progress = progress }));
            return list;
        }

        [Test]
        public void Codec_RoundTripsFourBytes_RejectsABadSlotAndATruncatedMessage()
        {
            var buffer = new byte[16];
            var w = new NetWriter(buffer);
            new CutProgressMsg { slot = 3, panel = 200, index = 17, progress = 254 }.Write(ref w);
            Assert.AreEqual(CutProgressMsg.Size, w.Position);
            var r = new NetReader(buffer, 0, w.Position);
            Assert.IsTrue(CutProgressMsg.Read(ref r, out CutProgressMsg m));
            Assert.AreEqual(3, m.slot);
            Assert.AreEqual(200, m.panel);
            Assert.AreEqual(17, m.index);
            Assert.AreEqual(254, m.progress);

            buffer[0] = NetProtocol.MaxPlayers; // no such slot
            r = new NetReader(buffer, 0, CutProgressMsg.Size);
            Assert.IsFalse(CutProgressMsg.Read(ref r, out _));
            buffer[0] = 1;
            r = new NetReader(buffer, 0, CutProgressMsg.Size - 1);
            Assert.IsFalse(CutProgressMsg.Read(ref r, out _), "Cut short.");

            Assert.AreEqual(0, CutProgressMsg.ToByte(0f));
            Assert.AreEqual(255, CutProgressMsg.ToByte(1f));
            Assert.AreEqual(0.5f, CutProgressMsg.FromByte(CutProgressMsg.ToByte(0.5f)), 0.005f);
        }

        [Test]
        public void Id_IsAnUnreliableStreamId_ThatNoOtherMessageUses()
        {
            Assert.IsFalse(NetMsg.IsReliable(CutProgressMsg.Id));
            Assert.AreNotEqual(NetMsg.End, CutProgressMsg.Id);
            Assert.Greater(CutProgressMsg.Id, NetMsg.Quit, "Not a connectionless id.");
            bool registeredAsSomethingElse = Array.IndexOf(NetMsg.All, CutProgressMsg.Id) >= 0 && NetMsg.Name(CutProgressMsg.Id) != "CutProgress";
            Assert.IsFalse(registeredAsSomethingElse, "Another message took id " + CutProgressMsg.Id + ": move CutProgressMsg.Id.");
        }

        [Test]
        public void ClientProgress_ReachesTheHostAndTheOtherClient_WithTheSendersSlot_NotTheSenderItself()
        {
            var a = m_Rig.Join();
            var b = m_Rig.Join();
            var atHost = Listen(h => m_Rig.Host.CutProgressReceived += h);
            var atA = Listen(h => a.Session.CutProgressReceived += h);
            var atB = Listen(h => b.Session.CutProgressReceived += h);

            a.Session.SendCutProgress(5, 2, 128);
            m_Rig.Step(0.2);

            Assert.AreEqual(1, atHost.Count, string.Join(",", atHost));
            Assert.AreEqual(a.Slot, atHost[0].slot);
            Assert.AreEqual(5, atHost[0].panel);
            Assert.AreEqual(2, atHost[0].index);
            Assert.AreEqual(128, atHost[0].progress);
            Assert.AreEqual(1, atB.Count, "Relayed to the other client once: " + string.Join(",", atB));
            Assert.AreEqual(a.Slot, atB[0].slot);
            Assert.AreEqual(128, atB[0].progress);
            Assert.AreEqual(0, atA.Count, "The cutter does not hear its own progress back.");
            Assert.AreEqual(0, m_Rig.Host.Stats.malformed);
            Assert.AreEqual(0, a.Session.Stats.malformed + b.Session.Stats.malformed);
        }

        [Test]
        public void HostProgress_ReachesEveryClientAsSlotZero_AndOnlyTheNewestOfOneTickIsSent()
        {
            var a = m_Rig.Join();
            var b = m_Rig.Join();
            var atA = Listen(h => a.Session.CutProgressReceived += h);
            var atB = Listen(h => b.Session.CutProgressReceived += h);

            m_Rig.Host.SendCutProgress(1, 0, 10);
            m_Rig.Host.SendCutProgress(1, 0, 60); // the same tick: replaces the first
            m_Rig.Step(0.3);

            Assert.AreEqual(1, atA.Count, string.Join(",", atA));
            Assert.AreEqual(1, atB.Count, string.Join(",", atB));
            Assert.AreEqual(0, atA[0].slot);
            Assert.AreEqual(60, atA[0].progress);
            Assert.AreEqual(60, atB[0].progress);

            // Nothing is repeated once it has gone out: a stopped cutter goes quiet.
            m_Rig.Step(0.5);
            Assert.AreEqual(1, atA.Count);
            Assert.AreEqual(1, atB.Count);
        }

        [Test]
        public void ProgressAtFiveHertz_ArrivesAboutFiveTimesASecond_AlongsideTheAvatarStream()
        {
            var a = m_Rig.Join();
            var b = m_Rig.Join();
            var atB = Listen(h => b.Session.CutProgressReceived += h);
            double next = 0;
            byte progress = 0;
            m_Rig.EachStep = now =>
            {
                if (now < next) return;
                next = now + CutProgressMsg.Interval;
                progress = (byte)Math.Min(255, progress + 20);
                a.Session.SendCutProgress(2, 1, progress);
            };
            int avatarsBefore = b.World.AvatarCount[a.Slot];
            m_Rig.Step(2.0);
            Assert.That(atB.Count, Is.InRange(9, 11), "About 5 Hz for two seconds.");
            for (int i = 1; i < atB.Count; i++) Assert.Greater(atB[i].progress, atB[i - 1].progress, "In order and growing.");
            Assert.Greater(b.World.AvatarCount[a.Slot] - avatarsBefore, 30, "The avatar stream still flows beside it.");
        }
    }
}

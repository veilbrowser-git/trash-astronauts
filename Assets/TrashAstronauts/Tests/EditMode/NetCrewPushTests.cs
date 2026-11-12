using System;
using System.Collections.Generic;
using NUnit.Framework;
using TrashAstronauts.Core.Net;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Crew shoves (CrewPushMsg): the codec, the id, de-duplication of the repeated copies, and the host's checks and
    /// relay through the real host and client sessions on the in-memory hub (to the target only).
    /// </summary>
    public class NetCrewPushTests
    {
        NetTestRig m_Rig;

        [SetUp]
        public void SetUp()
        {
            m_Rig = new NetTestRig(9);
            var hostAvatar = AvatarSnapshot.Default;
            hostAvatar.headPosition = new NetVec3(0f, 1.6f, 0f);
            m_Rig.Host.SetLocalAvatar(in hostAvatar);
        }

        [TearDown]
        public void TearDown() => m_Rig.Dispose();

        struct Heard
        {
            public byte sender, flags;
            public NetVec3 velocity, contact;
            public override string ToString() => sender + ":" + velocity;
        }

        static List<Heard> Listen(Action<CrewPushHandler> subscribe)
        {
            var list = new List<Heard>();
            subscribe((sender, velocity, contact, flags) => list.Add(new Heard { sender = sender, velocity = velocity, contact = contact, flags = flags }));
            return list;
        }

        /// <summary>Joins a client with its head at (x, 1.6, 0) and lets its avatar reach the host.</summary>
        NetTestRig.Member JoinAt(float x)
        {
            var m = m_Rig.Join();
            m.Avatar.headPosition = new NetVec3(x, 1.6f, 0f);
            m_Rig.Step(0.15);
            return m;
        }

        [Test]
        public void Codec_RoundTrips_ClampsTheVelocity_AndRejectsBadSlotsAndATruncatedMessage()
        {
            var buffer = new byte[32];
            var w = new NetWriter(buffer);
            new CrewPushMsg
            {
                sender = 2, target = 3, seq = 65000,
                velocity = new NetVec3(0.9f, -0.25f, 0.4f),
                contact = new NetVec3(-12.34f, 1.5f, 80.01f),
                flags = CrewPushMsg.FlagShove,
            }.Write(ref w);
            Assert.AreEqual(CrewPushMsg.Size, w.Position);
            var r = new NetReader(buffer, 0, w.Position);
            Assert.IsTrue(CrewPushMsg.Read(ref r, out CrewPushMsg m));
            Assert.AreEqual(2, m.sender);
            Assert.AreEqual(3, m.target);
            Assert.AreEqual(65000, m.seq);
            Assert.AreEqual(0.9f, m.velocity.x, 0.001f);
            Assert.AreEqual(-0.25f, m.velocity.y, 0.001f);
            Assert.AreEqual(0.4f, m.velocity.z, 0.001f);
            Assert.AreEqual(-12.34f, m.contact.x, 0.006f);
            Assert.AreEqual(80.01f, m.contact.z, 0.006f);
            Assert.IsTrue(m.IsShove);

            // Never more than the cap on the wire, whatever was asked for.
            w = new NetWriter(buffer);
            new CrewPushMsg { sender = 0, target = 1, velocity = new NetVec3(30f, 0f, 0f) }.Write(ref w);
            r = new NetReader(buffer, 0, w.Position);
            Assert.IsTrue(CrewPushMsg.Read(ref r, out m));
            Assert.AreEqual(CrewPushMsg.MaxTargetSpeed, m.velocity.Magnitude, 0.002f);
            Assert.AreEqual(0f, CrewPushMsg.Clamp(new NetVec3(float.NaN, 0f, 0f)).Magnitude);

            buffer[0] = NetProtocol.MaxPlayers; // no such sender
            r = new NetReader(buffer, 0, CrewPushMsg.Size);
            Assert.IsFalse(CrewPushMsg.Read(ref r, out _));
            buffer[0] = 1; // a player shoving themself
            r = new NetReader(buffer, 0, CrewPushMsg.Size);
            Assert.IsFalse(CrewPushMsg.Read(ref r, out _));
            buffer[0] = 0;
            r = new NetReader(buffer, 0, CrewPushMsg.Size - 1);
            Assert.IsFalse(CrewPushMsg.Read(ref r, out _), "Cut short.");
        }

        [Test]
        public void Id_Is41_AnUnreliableStreamId_ThatNoOtherMessageUses()
        {
            Assert.AreEqual(41, CrewPushMsg.Id);
            Assert.IsFalse(NetMsg.IsReliable(CrewPushMsg.Id));
            Assert.Greater(CrewPushMsg.Id, NetMsg.Quit, "Not a connectionless id.");
            Assert.AreNotEqual(CutProgressMsg.Id, CrewPushMsg.Id, "40 is the weld progress stream.");
            bool registeredAsSomethingElse = Array.IndexOf(NetMsg.All, CrewPushMsg.Id) >= 0 && NetMsg.Name(CrewPushMsg.Id) != "CrewPush";
            Assert.IsFalse(registeredAsSomethingElse, "Another message took id " + CrewPushMsg.Id + ": move CrewPushMsg.Id.");
        }

        [Test]
        public void Dedup_TakesEachShoveOnce_PerSender_AndForgetsAfterTheWindow()
        {
            var d = new CrewPushDedup();
            Assert.IsTrue(d.TryTake(1, 7, 10.0));
            Assert.IsFalse(d.TryTake(1, 7, 10.05), "The repeated copy.");
            Assert.IsFalse(d.TryTake(1, 7, 10.1));
            Assert.IsTrue(d.TryTake(2, 7, 10.1), "Another sender's 7 is another shove.");
            Assert.IsTrue(d.TryTake(1, 8, 10.1));
            Assert.IsTrue(d.TryTake(1, 7, 10.0 + CrewPushDedup.Window + 0.1), "A rejoined sender counting from 1 again.");
            Assert.IsFalse(d.TryTake(9, 1, 10.0), "No such slot.");
        }

        [Test]
        public void ClientShove_GoesToTheTargetOnly_Once_WithTheSendersSlot_AndTheHostHearsNothing()
        {
            var a = JoinAt(0.8f);
            var b = JoinAt(1.4f);
            var c = JoinAt(-0.6f);
            var atHost = Listen(h => m_Rig.Host.CrewPushReceived += h);
            var atA = Listen(h => a.Session.CrewPushReceived += h);
            var atB = Listen(h => b.Session.CrewPushReceived += h);
            var atC = Listen(h => c.Session.CrewPushReceived += h);

            Assert.IsTrue(a.Session.SendCrewPush(b.Slot, new NetVec3(1.2f, 0f, 0f), new NetVec3(1.2f, 1.3f, 0f), CrewPushMsg.FlagShove));
            m_Rig.Step(0.4);

            Assert.AreEqual(1, atB.Count, "Once, however many copies: " + string.Join(",", atB));
            Assert.AreEqual(a.Slot, atB[0].sender);
            Assert.AreEqual(1.2f, atB[0].velocity.x, 0.002f);
            Assert.AreEqual(1.2f, atB[0].contact.x, 0.01f);
            Assert.AreEqual(CrewPushMsg.FlagShove, atB[0].flags);
            Assert.AreEqual(0, atHost.Count, "Not for the host.");
            Assert.AreEqual(0, atA.Count, "Not back to the pusher.");
            Assert.AreEqual(0, atC.Count, "Not to a bystander.");
            Assert.AreEqual(1, m_Rig.Host.CrewPushStats.received);
            Assert.AreEqual(1, m_Rig.Host.CrewPushStats.relayed);
            Assert.AreEqual(CrewPushMsg.Repeats - 1, m_Rig.Host.CrewPushStats.duplicates, "The repeats reached the host and were dropped there.");
            Assert.AreEqual(CrewPushMsg.Repeats - 1, b.Session.CrewPushStats.duplicates, "The host's own repeats reached the target and were dropped there.");
            Assert.AreEqual(0, m_Rig.Host.Stats.malformed);
            Assert.AreEqual(0, a.Session.Stats.malformed + b.Session.Stats.malformed + c.Session.Stats.malformed);
        }

        [Test]
        public void ShoveForTheHost_IsHeardOnTheHost_AndTheHostsShove_ReachesItsTargetOnly()
        {
            var a = JoinAt(0.7f);
            var b = JoinAt(-0.7f);
            var atHost = Listen(h => m_Rig.Host.CrewPushReceived += h);
            var atA = Listen(h => a.Session.CrewPushReceived += h);
            var atB = Listen(h => b.Session.CrewPushReceived += h);

            Assert.IsTrue(a.Session.SendCrewPush(NetProtocol.HostSlot, new NetVec3(-0.5f, 0f, 0.1f), new NetVec3(0.3f, 1.4f, 0f), 0));
            m_Rig.Step(0.4);
            Assert.AreEqual(1, atHost.Count);
            Assert.AreEqual(a.Slot, atHost[0].sender);
            Assert.AreEqual(-0.5f, atHost[0].velocity.x, 0.002f);
            Assert.AreEqual(0, atA.Count + atB.Count);

            Assert.IsTrue(m_Rig.Host.SendCrewPush(b.Slot, new NetVec3(0f, 0f, 9f), new NetVec3(-0.4f, 1.4f, 0f), CrewPushMsg.FlagShove));
            Assert.IsFalse(m_Rig.Host.SendCrewPush(NetProtocol.HostSlot, new NetVec3(1f, 0f, 0f), NetVec3.Zero, 0), "Not itself.");
            Assert.IsFalse(m_Rig.Host.SendCrewPush(3, new NetVec3(1f, 0f, 0f), NetVec3.Zero, 0), "Nobody in slot 3.");
            m_Rig.Step(0.4);
            Assert.AreEqual(1, atB.Count);
            Assert.AreEqual(0, atB[0].sender);
            Assert.AreEqual(CrewPushMsg.MaxTargetSpeed, atB[0].velocity.Magnitude, 0.002f, "Clamped to the cap.");
            Assert.AreEqual(0, atA.Count);
        }

        [Test]
        public void TheHostRefuses_ATooDistantShove_AnAwaySenderOrTarget_AndMoreThanFourASecond()
        {
            var a = JoinAt(0.8f);
            var far = JoinAt(6f);
            var atA = Listen(h => a.Session.CrewPushReceived += h);
            var atHost = Listen(h => m_Rig.Host.CrewPushReceived += h);

            // Too far: heads 5.2 m apart.
            Assert.IsTrue(far.Session.SendCrewPush(a.Slot, new NetVec3(1f, 0f, 0f), new NetVec3(1f, 1.4f, 0f), 0));
            m_Rig.Step(0.3);
            Assert.AreEqual(0, atA.Count);
            Assert.AreEqual(1, m_Rig.Host.CrewPushStats.tooFar);

            // Rate limit: six shoves in well under a second from one sender, only four get through.
            for (int i = 0; i < 6; i++)
            {
                Assert.IsTrue(a.Session.SendCrewPush(NetProtocol.HostSlot, new NetVec3(0.2f, 0f, 0f), new NetVec3(0.4f, 1.4f, 0f), 0));
                m_Rig.Step(0.06);
            }
            m_Rig.Step(0.3);
            Assert.AreEqual(CrewPushMsg.MaxPerSecond, atHost.Count, string.Join(",", atHost));
            Assert.AreEqual(2, m_Rig.Host.CrewPushStats.rateLimited);
            m_Rig.Step(1.0);
            Assert.IsTrue(a.Session.SendCrewPush(NetProtocol.HostSlot, new NetVec3(0.2f, 0f, 0f), new NetVec3(0.4f, 1.4f, 0f), 0));
            m_Rig.Step(0.3);
            Assert.AreEqual(CrewPushMsg.MaxPerSecond + 1, atHost.Count, "A second later the sender may shove again.");

            // An Away target: nothing reaches it.
            a.Session.SetAway(true, m_Rig.Now);
            m_Rig.Step(0.3);
            Assert.IsTrue(m_Rig.Host.IsAway(a.Slot), "Test set-up: a is Away.");
            Assert.IsFalse(m_Rig.Host.SendCrewPush(a.Slot, new NetVec3(1f, 0f, 0f), NetVec3.Zero, 0), "The host does not shove an Away player.");
            var near = JoinAt(1.5f);
            Assert.IsTrue(near.Session.SendCrewPush(a.Slot, new NetVec3(1f, 0f, 0f), new NetVec3(1f, 1.4f, 0f), 0));
            m_Rig.Step(0.3);
            Assert.AreEqual(0, atA.Count);
            Assert.GreaterOrEqual(m_Rig.Host.CrewPushStats.away, 1);

            // An Away sender: refused too.
            int heard = atHost.Count;
            Assert.IsTrue(a.Session.SendCrewPush(NetProtocol.HostSlot, new NetVec3(0.2f, 0f, 0f), new NetVec3(0.4f, 1.4f, 0f), 0));
            m_Rig.Step(0.3);
            Assert.AreEqual(heard, atHost.Count);
            Assert.GreaterOrEqual(m_Rig.Host.CrewPushStats.away, 2);
        }

        [Test]
        public void TheGamesOwnCheck_CanRefuseAShove_ForExampleADownedPlayer()
        {
            var a = JoinAt(0.8f);
            var atHost = Listen(h => m_Rig.Host.CrewPushReceived += h);
            m_Rig.Host.CrewPushAllowed = (sender, target) => sender != a.Slot;
            Assert.IsTrue(a.Session.SendCrewPush(NetProtocol.HostSlot, new NetVec3(0.5f, 0f, 0f), new NetVec3(0.4f, 1.4f, 0f), 0));
            m_Rig.Step(0.3);
            Assert.AreEqual(0, atHost.Count);
            Assert.AreEqual(1, m_Rig.Host.CrewPushStats.refused);
            m_Rig.Host.CrewPushAllowed = null;
            Assert.IsTrue(a.Session.SendCrewPush(NetProtocol.HostSlot, new NetVec3(0.5f, 0f, 0f), new NetVec3(0.4f, 1.4f, 0f), 0));
            m_Rig.Step(0.3);
            Assert.AreEqual(1, atHost.Count);
        }

        [Test]
        public void AShove_SurvivesLosingMostDatagrams_BecauseItIsSentThreeTimes()
        {
            var a = JoinAt(0.8f);
            var b = JoinAt(1.4f);
            var atB = Listen(h => b.Session.CrewPushReceived += h);
            // Of the three copies on the way to the host, lose the first two.
            int seen = 0;
            m_Rig.Host.Tap = (outgoing, slot, type, buffer, offset, length, time) =>
            {
                if (!outgoing && type == CrewPushMsg.Id) seen++;
            };
            int written = 0;
            a.Session.Tap = (outgoing, slot, type, buffer, offset, length, time) =>
            {
                if (outgoing && type == CrewPushMsg.Id) written++;
            };
            Assert.IsTrue(a.Session.SendCrewPush(b.Slot, new NetVec3(0.6f, 0f, 0f), new NetVec3(1.1f, 1.4f, 0f), 0));
            a.Transport.Blackhole = true;
            Assert.IsTrue(m_Rig.StepUntil(() => written >= 2, 1.0), "Test set-up: two copies sent into the void.");
            a.Transport.Blackhole = false;
            m_Rig.Step(0.5);
            Assert.AreEqual(CrewPushMsg.Repeats, written, "Three copies in all, then no more.");
            Assert.AreEqual(1, seen, "Only the third copy arrived.");
            Assert.AreEqual(1, atB.Count, "And that was enough.");
        }
    }
}

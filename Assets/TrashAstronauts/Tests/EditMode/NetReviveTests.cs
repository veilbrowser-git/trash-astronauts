using System.Collections.Generic;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Co-op revive in the core (protocol 5): the rules' numbers, the four new messages and the revive fields of
    /// WorldState and RunEnded on the wire, and the sessions carrying them (a client's ReviveHold to the host with its
    /// own slot; the host's PlayerDowned, ReviveProgress and PlayerRevived to every client, in order, under loss; the
    /// downed crew in a joiner's Welcome).
    /// </summary>
    public class NetReviveTests
    {
        NetTestRig m_Rig;
        readonly byte[] m_Buffer = new byte[2048];

        [SetUp] public void SetUp() => m_Rig = new NetTestRig(7);
        [TearDown] public void TearDown() => m_Rig.Dispose();

        static NetVec3 V(float x, float y, float z) => new NetVec3(x, y, z);

        // ------------------------------------------------------------------ rules

        [Test]
        public void Ring_FillsIn5sWithOneHand_AQuarterFasterWithTwoButNoMore_AndDrainsIn2_5sWithNone()
        {
            Assert.AreEqual(6, NetProtocol.Version, "0.13.0 (revive, shoves, the Mimic in co-op) is protocol 6");
            float p = 0f;
            int frames = 0;
            while (p < 1f && frames < 10000) { p = ReviveRules.StepProgress(p, 1, 0.01f); frames++; }
            Assert.That(frames * 0.01f, Is.InRange(4.98f, 5.02f), "one rescuer: about 5 s");

            p = 0f; frames = 0;
            while (p < 1f && frames < 10000) { p = ReviveRules.StepProgress(p, 2, 0.01f); frames++; }
            Assert.That(frames * 0.01f, Is.InRange(3.98f, 4.02f), "two rescuers: 4 s");
            p = 0f; frames = 0;
            while (p < 1f && frames < 10000) { p = ReviveRules.StepProgress(p, 3, 0.01f); frames++; }
            Assert.That(frames * 0.01f, Is.InRange(3.98f, 4.02f), "a third adds nothing");

            p = 1f; frames = 0;
            while (p > 0f && frames < 10000) { p = ReviveRules.StepProgress(p, 0, 0.01f); frames++; }
            Assert.That(frames * 0.01f, Is.InRange(2.48f, 2.52f), "let go: a full ring drains in 2.5 s");
            Assert.AreEqual(0f, ReviveRules.StepProgress(0f, 0, 1f));
            Assert.AreEqual(0.4f, ReviveRules.StepProgress(0.4f, 1, 0f), "no time, no change");
        }

        [Test]
        public void Penalties_Are10PercentADown_UpTo40_Plus35LeftBehind_OnTheirOwnShareOnly()
        {
            Assert.AreEqual(0f, ReviveRules.PenaltyFraction(0, false));
            Assert.AreEqual(0.1f, ReviveRules.PenaltyFraction(1, false), 1e-5f);
            Assert.AreEqual(0.3f, ReviveRules.PenaltyFraction(3, false), 1e-5f);
            Assert.AreEqual(0.4f, ReviveRules.PenaltyFraction(9, false), 1e-5f, "capped at 40 %");
            Assert.AreEqual(0.45f, ReviveRules.PenaltyFraction(1, true), 1e-5f);
            Assert.AreEqual(0.75f, ReviveRules.PenaltyFraction(7, true), 1e-5f);
            Assert.AreEqual(1000, ReviveRules.PayoutFor(1000, 0, false));
            Assert.AreEqual(800, ReviveRules.PayoutFor(1000, 2, false));
            Assert.AreEqual(550, ReviveRules.PayoutFor(1000, 1, true));
            Assert.AreEqual(0, ReviveRules.PayoutFor(0, 3, true));

            var result = new MissionResult { Outcome = MissionOutcome.ContractComplete, Payout = 1000, CrewDowns = new[] { 0, 2, 0, 1 }, LeftBehindMask = 1 << 3 };
            Assert.AreEqual(1000, result.PayoutFor(-1), "single-player banks it all");
            Assert.AreEqual(1000, result.PayoutFor(0));
            Assert.AreEqual(800, result.PayoutFor(1));
            Assert.AreEqual(1000, result.PayoutFor(2));
            Assert.AreEqual(550, result.PayoutFor(3));
            Assert.IsTrue(result.AnyRevivePenalty);
            Assert.IsFalse(new MissionResult { Payout = 5 }.AnyRevivePenalty);

            // The client's mirror takes the record with the host's figures.
            var mirror = new MissionSession();
            Assert.IsTrue(mirror.Begin(1, 100, 300f, 1));
            var stored = mirror.ApplyRemoteEnd(result);
            Assert.AreEqual(2, stored.DownsOf(1));
            Assert.IsTrue(stored.WasLeftBehind(3));
            Assert.AreEqual(550, stored.PayoutFor(3));
            Assert.AreNotSame(result.CrewDowns, stored.CrewDowns);
        }

        // ------------------------------------------------------------------ codec

        [Test]
        public void ReviveMessages_RoundTrip_AndRejectBadSlots()
        {
            var w = new NetWriter(m_Buffer);
            new PlayerDownedMsg { slot = 2, position = V(12.34f, -1.5f, 40.01f) }.Write(ref w);
            new PlayerRevivedMsg { slot = 2, by = 1 }.Write(ref w);
            new PlayerRevivedMsg { slot = 3, by = NetProtocol.NoSlot }.Write(ref w);
            new ReviveProgressMsg { slot = 1, progress = 0.5f, rescuerSlot = 0 }.Write(ref w);
            new ReviveHoldMsg { slot = 3, on = true }.Write(ref w);
            Assert.AreEqual(PlayerDownedMsg.MaxSize + 2 * PlayerRevivedMsg.MaxSize + ReviveProgressMsg.MaxSize + ReviveHoldMsg.MaxSize, w.Position);
            var r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(PlayerDownedMsg.Read(ref r, out PlayerDownedMsg downed));
            Assert.AreEqual(2, downed.slot);
            Assert.AreEqual(12.34f, downed.position.x, Quant.PosCmMaxError);
            Assert.AreEqual(40.01f, downed.position.z, Quant.PosCmMaxError);
            Assert.IsTrue(PlayerRevivedMsg.Read(ref r, out PlayerRevivedMsg revived));
            Assert.AreEqual(2, revived.slot);
            Assert.AreEqual(1, revived.by);
            Assert.IsTrue(PlayerRevivedMsg.Read(ref r, out revived));
            Assert.AreEqual(NetProtocol.NoSlot, revived.by);
            Assert.IsTrue(ReviveProgressMsg.Read(ref r, out ReviveProgressMsg progress));
            Assert.AreEqual(1, progress.slot);
            Assert.AreEqual(0.5f, progress.progress, 1f / 255f);
            Assert.AreEqual(0, progress.rescuerSlot);
            Assert.IsTrue(ReviveHoldMsg.Read(ref r, out ReviveHoldMsg hold));
            Assert.AreEqual(3, hold.slot);
            Assert.IsTrue(hold.on);
            Assert.IsTrue(r.AtEnd);

            foreach (byte bad in new byte[] { 4, 200 })
            {
                m_Buffer[0] = bad; m_Buffer[1] = 0;
                r = new NetReader(m_Buffer, 0, 7);
                Assert.IsFalse(PlayerDownedMsg.Read(ref r, out _), "slot " + bad);
                r = new NetReader(m_Buffer, 0, 2);
                Assert.IsFalse(PlayerRevivedMsg.Read(ref r, out _));
                r = new NetReader(m_Buffer, 0, 2);
                Assert.IsFalse(ReviveHoldMsg.Read(ref r, out _));
            }
            m_Buffer[0] = 1; m_Buffer[1] = 9;
            r = new NetReader(m_Buffer, 0, 2);
            Assert.IsFalse(PlayerRevivedMsg.Read(ref r, out _), "a rescuer slot that does not exist");
            foreach (byte id in new[] { NetMsg.PlayerDowned, NetMsg.PlayerRevived, NetMsg.ReviveProgress, NetMsg.ReviveHold })
            {
                Assert.IsTrue(NetMsg.IsReliable(id), NetMsg.Name(id));
                CollectionAssert.Contains(NetMsg.All, id);
            }
            CollectionAssert.DoesNotContain(NetMsg.All, (byte)40, "40 is the CutProgress stream");
            CollectionAssert.DoesNotContain(NetMsg.All, (byte)41, "41 is kept for the Push stream");
        }

        [Test]
        public void WorldState_CarriesWhoIsDown_WhereAndHowOften_AtAFixedSize()
        {
            var world = new WorldStateMsg { doorOpenMask = 3 };
            world.SetCrew(1, true, V(4f, 1.6f, 19.5f), 2);
            world.SetCrew(3, false, V(9f, 9f, 9f), 1);
            world.SetCrew(7, true, V(1f, 1f, 1f), 9); // ignored
            Assert.IsTrue(world.IsDowned(1));
            Assert.IsFalse(world.IsDowned(3));
            Assert.AreEqual(0f, world.DownedAt(3).x, "an up player has no position");
            var w = new NetWriter(m_Buffer);
            world.Write(ref w);
            Assert.AreEqual(WorldStateMsg.MaxSize - WorldStateMsg.MaxWeldBytes, w.Position, "no welds: the cap less the weld bytes, crew section always there");
            var r = new NetReader(m_Buffer, 0, w.Position);
            var back = new WorldStateMsg();
            Assert.IsTrue(WorldStateMsg.Read(ref r, ref back));
            Assert.IsTrue(back.IsDowned(1));
            Assert.IsFalse(back.IsDowned(0));
            Assert.IsFalse(back.IsDowned(3));
            Assert.AreEqual(2, back.DownCount(1));
            Assert.AreEqual(1, back.DownCount(3));
            Assert.AreEqual(19.5f, back.DownedAt(1).z, Quant.PosCmMaxError);
            Assert.AreEqual(3, back.doorOpenMask);

            var copy = new WorldStateMsg();
            copy.CopyFrom(in back);
            Assert.IsTrue(copy.IsDowned(1));
            Assert.AreEqual(2, copy.DownCount(1));
            Assert.AreEqual(4f, copy.DownedAt(1).x, Quant.PosCmMaxError);
            copy.Clear();
            Assert.IsFalse(copy.IsDowned(1));
            Assert.AreEqual(0, copy.DownCount(1));
        }

        [Test]
        public void RunEnded_CarriesDownsAndLeftBehind_IntoTheResult()
        {
            var result = new MissionResult { Outcome = MissionOutcome.QuotaMissed, Payout = 400, CrewDowns = new[] { 1, 0, 3, 0 }, LeftBehindMask = 0b0100 };
            var w = new NetWriter(m_Buffer);
            RunEndedMsg.From(result).Write(ref w);
            Assert.AreEqual(RunEndedMsg.MaxSize, w.Position);
            var r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(RunEndedMsg.Read(ref r, out RunEndedMsg message));
            MissionResult back = message.ToResult();
            Assert.AreEqual(1, back.DownsOf(0));
            Assert.AreEqual(3, back.DownsOf(2));
            Assert.IsTrue(back.WasLeftBehind(2));
            Assert.IsFalse(back.WasLeftBehind(0));
            Assert.AreEqual(result.PayoutFor(2), back.PayoutFor(2));
            Assert.IsNull(RunEndedMsg.From(new MissionResult { Payout = 9 }).ToResult().CrewDowns, "nobody downed: nothing allocated");
        }

        // ------------------------------------------------------------------ sessions

        [Test]
        public void ReviveHold_ReachesTheHostWorld_WithTheSendersSlot_UnderLoss_AndNobodyRevivesThemselves()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            m_Rig.Hub.Loss = 0.25;
            m_Rig.Hub.Duplicate = 0.25;
            ScriptedHostWorld host = m_Rig.HostWorld;

            b.Session.SendReviveHold(a.Slot, true);
            b.Session.Flush(m_Rig.Now);
            Assert.IsTrue(m_Rig.StepUntil(() => host.Calls[NetMsg.ReviveHold] == 1 && b.Session.Backlog == 0, 10.0));
            Assert.AreEqual(b.Slot, host.LastSlot, "the hold is the sender's, whatever it claims");
            Assert.AreEqual(a.Slot, host.LastReviveTarget);
            Assert.IsTrue(host.LastReviveOn);

            b.Session.SendReviveHold(a.Slot, false);
            b.Session.SendReviveHold(b.Slot, true); // itself: dropped by the host session
            b.Session.Flush(m_Rig.Now);
            Assert.IsTrue(m_Rig.StepUntil(() => b.Session.Backlog == 0, 10.0));
            m_Rig.Step(0.5);
            Assert.AreEqual(2, host.Calls[NetMsg.ReviveHold], "exactly once each, the self-revive refused");
            Assert.IsFalse(host.LastReviveOn);
            Assert.AreEqual(a.Slot, host.LastReviveTarget);
        }

        [Test]
        public void DownedProgressRevived_ReachEveryClient_Once_InOrder_UnderLoss()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            m_Rig.Step(0.3);
            foreach (NetTestRig.Member m in m_Rig.Members) m.World.Order.Clear();
            m_Rig.Hub.Loss = 0.3;
            m_Rig.Hub.Reorder = 0.3;
            m_Rig.Hub.Duplicate = 0.3;
            NetSessionHost host = m_Rig.Host;

            host.SendCatchStarted(1);
            host.SendPlayerDowned(1, V(3f, 1.4f, 20f));
            for (int i = 1; i <= 5; i++) host.SendReviveProgress(1, i * 0.2f, 2);
            host.SendPlayerRevived(1, 2);
            host.Flush(m_Rig.Now);
            Assert.IsTrue(m_Rig.StepUntil(() => host.Backlog(1) == 0 && host.Backlog(2) == 0, 20.0));
            m_Rig.Step(0.5);

            var expected = new List<byte> { NetMsg.CatchStarted, NetMsg.PlayerDowned };
            for (int i = 0; i < 5; i++) expected.Add(NetMsg.ReviveProgress);
            expected.Add(NetMsg.PlayerRevived);
            foreach (NetTestRig.Member m in m_Rig.Members)
            {
                CollectionAssert.AreEqual(expected, m.World.Order, "slot " + m.Slot);
                Assert.IsFalse(m.World.Downed[1], "up again");
                Assert.AreEqual(2, m.World.RevivedBy[1]);
                Assert.AreEqual(3f, m.World.DownedAt[1].x, Quant.PosCmMaxError);
                Assert.AreEqual(2, m.World.ReviveRescuer[1]);
            }
        }

        [Test]
        public void AJoinersWelcome_CarriesTheDownedCrew_ForALateJoiner()
        {
            m_Rig.HostWorld.WelcomeWorld.SetCrew(0, true, V(-2f, 1.2f, 30f), 1);
            NetTestRig.Member a = m_Rig.Join();
            Assert.IsTrue(a.World.Downed[0], "the joiner sees the host down");
            Assert.AreEqual(30f, a.World.DownedAt[0].z, Quant.PosCmMaxError);
            Assert.IsFalse(a.World.Downed[1]);
            Assert.AreEqual(1, a.World.World.DownCount(0));
        }
    }
}

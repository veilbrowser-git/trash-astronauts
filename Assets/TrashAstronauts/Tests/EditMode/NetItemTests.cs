using System;
using System.Collections.Generic;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Who simulates a salvage item: host arbitration with epochs (ItemOwnershipTable), the client's mode machine
    /// and hand-back timer (ItemAuthority), and both of them working through real sessions.
    /// </summary>
    public class NetItemTests
    {
        NetTestRig m_Rig;
        readonly HashSet<NetTestRig.Member> m_NoHandoff = new HashSet<NetTestRig.Member>();
        readonly Dictionary<NetTestRig.Member, float> m_Speed = new Dictionary<NetTestRig.Member, float>();

        [SetUp]
        public void SetUp()
        {
            m_Rig = new NetTestRig();
            m_NoHandoff.Clear();
            m_Speed.Clear();
            m_Rig.EachStep = DriveItems;
        }

        [TearDown]
        public void TearDown()
        {
            m_Rig.Dispose();
            Assert.AreEqual(0, m_Rig.Hub.LiveCount);
            Assert.AreEqual(0, UdpTransport.LiveCount);
            Assert.LessOrEqual(m_Rig.Hub.LargestDatagram, NetProtocol.MaxDatagram);
        }

        static NetVec3 V(float x, float y, float z) => new NetVec3(x, y, z);

        /// <summary>Where a member's copy of an item "is" while that member simulates it.</summary>
        static NetVec3 PoseOf(NetTestRig.Member member, int item) => V(member.Slot * 10 + item, 1.25f, 2f);

        /// <summary>What ItemSync does every frame on a client: stream owned items, run their hand-back timers.</summary>
        void DriveItems(double now)
        {
            foreach (NetTestRig.Member m in m_Rig.Members)
            {
                if (m.Frozen || m.Session.State != ClientState.Connected) continue;
                ItemAuthority[] items = m.World.Items;
                float speed = m_Speed.TryGetValue(m, out float s) ? s : 0f;
                for (int i = 0; i < items.Length; i++)
                {
                    if (items[i].ShouldStream) m.Session.QueueItemPose((byte)i, PoseOf(m, i), NetQuat.Identity, items[i].WireEpoch, items[i].HeldLocally);
                    if (m_NoHandoff.Contains(m)) continue;
                    if (items[i].Tick(now, speed, out byte handoffEpoch))
                        m.Session.SendItemHandoff((byte)i, handoffEpoch, PoseOf(m, i), NetQuat.Identity, V(speed, 0, 0), NetVec3.Zero);
                }
            }
        }

        bool Grab(NetTestRig.Member m, byte item)
        {
            if (!m.World.Items[item].BeginGrab(out byte epochSeen)) return false;
            m.Session.SendGrabRequest(item, epochSeen, 1);
            m.Session.Flush(m_Rig.Now);
            return true;
        }

        bool Release(NetTestRig.Member m, byte item)
        {
            if (!m.World.Items[item].Release(m_Rig.Now)) return false;
            m.Session.SendItemReleased(item, m.World.Items[item].WireEpoch, PoseOf(m, item), NetQuat.Identity, V(0, 1.4f, 0));
            m.Session.Flush(m_Rig.Now);
            return true;
        }

        void StartRun(int items = 6)
        {
            var p = new RunStartParams { seed = 1, runIndex = 1, creatureIndex = RunStartParams.NoCreature, items = new RunItem[items] };
            for (int i = 0; i < items; i++) p.items[i] = new RunItem { pointIndex = (byte)i, value = (ushort)(100 + 10 * i) };
            p.planHash = p.ComputePlanHash();
            Assert.IsTrue(m_Rig.Host.SendRunStart(in p));
            m_Rig.HostWorld.CurrentPhase = MissionPhase.Active;
            m_Rig.Step(0.3);
            foreach (NetTestRig.Member m in m_Rig.Members) Assert.AreEqual(items, m.World.Items.Length, "RunStart reached slot " + m.Slot);
        }

        void AssertEveryoneAgrees(int item, string when)
        {
            ItemOwnershipTable table = m_Rig.Host.Items;
            foreach (NetTestRig.Member m in m_Rig.Members)
            {
                if (m.Frozen || m.Session.State != ClientState.Connected) continue;
                ItemAuthority a = m.World.Items[item];
                string who = when + ": slot " + m.Slot + " ";
                Assert.AreEqual(table.Epoch(item), a.Epoch, who + "epoch");
                Assert.AreEqual(table.Owner(item), a.OwnerSlot, who + "owner");
                Assert.IsFalse(a.Pending, who + "still has an unanswered request");
                ItemMode expected = table.Owner(item) == m.Slot ? ItemMode.Mine : table.IsHeld(item) ? ItemMode.RemoteHeld : ItemMode.HostFree;
                Assert.AreEqual(expected, a.Mode, who + "mode");
            }
        }

        // ================================================================== the table on its own

        [Test]
        public void Table_TwoRequestsAgainstTheSameEpoch_GiveExactlyOneGrant()
        {
            var table = new ItemOwnershipTable();
            table.Reset(4);
            Assert.AreEqual(4, table.Count);
            Assert.AreEqual(GrabResult.Granted, table.TryGrab(2, 1, 0, true), "first to arrive wins");
            Assert.AreEqual(GrabResult.StaleEpoch, table.TryGrab(2, 2, 0, true), "the second saw the same epoch and loses");
            Assert.AreEqual(1, table.Owner(2));
            Assert.AreEqual(1, table.Epoch(2), "a grant raises the epoch");
            Assert.IsTrue(table.IsHeld(2));
            Assert.AreEqual(GrabResult.Held, table.TryGrab(2, 2, 1, true), "even with the right epoch, a held item is not free");
            Assert.AreEqual(GrabResult.Held, table.TryGrab(2, 1, 1, true), "nor for its own holder asking twice");
            Assert.AreEqual(GrabResult.UnknownItem, table.TryGrab(4, 1, 0, true));
            Assert.AreEqual(GrabResult.UnknownItem, table.TryGrab(-1, 1, 0, true));
            Assert.AreEqual(GrabResult.NotGrantable, table.TryGrab(3, 1, 0, false), "the game said no (mounted or secured)");
            Assert.AreEqual(0, table.Epoch(3), "a refusal changes nothing");
            Assert.AreEqual(0, table.Owner(3));
        }

        [Test]
        public void Table_HostGrab_WinsUnlessAClientHolds_AndEveryChangeOfOwnerRaisesTheEpoch()
        {
            var table = new ItemOwnershipTable();
            table.Reset(3);
            Assert.IsTrue(table.HostGrab(0));
            Assert.AreEqual(1, table.Epoch(0));
            Assert.AreEqual(GrabResult.StaleEpoch, table.TryGrab(0, 1, 0, true), "a client that grabbed at the same moment as the host loses");
            Assert.IsFalse(table.HostGrab(0), "already in the host's hand");
            Assert.IsTrue(table.HostRelease(0));
            Assert.IsFalse(table.HostRelease(0));
            Assert.AreEqual(1, table.Epoch(0), "a release keeps the epoch");
            Assert.AreEqual(GrabResult.Granted, table.TryGrab(0, 1, 1, true));
            Assert.AreEqual(2, table.Epoch(0));
            Assert.IsFalse(table.HostGrab(0), "the host cannot take it out of a client's hand");
            Assert.IsFalse(table.HostRelease(0), "nor release what it does not hold");

            Assert.IsTrue(table.Release(0, 1, 2, 10.0));
            Assert.IsFalse(table.IsHeld(0));
            Assert.AreEqual(1, table.Owner(0), "the thrower keeps simulating the flight");
            Assert.IsTrue(table.HostGrab(0), "the host may catch it in flight");
            Assert.AreEqual(0, table.Owner(0));
            Assert.AreEqual(3, table.Epoch(0));
        }

        [Test]
        public void Table_DropsEverythingWithAStaleEpoch_OrFromANonOwner()
        {
            var table = new ItemOwnershipTable();
            table.Reset(2);
            Assert.AreEqual(GrabResult.Granted, table.TryGrab(1, 2, 0, true)); // epoch 1, owner 2

            Assert.IsFalse(table.AcceptPose(1, 2, 0, V(1, 1, 1), NetQuat.Identity), "pose with the old epoch");
            Assert.IsFalse(table.AcceptPose(1, 3, 1, V(1, 1, 1), NetQuat.Identity), "pose from somebody who is not the owner");
            Assert.IsFalse(table.AcceptPose(1, 0, 1, V(1, 1, 1), NetQuat.Identity), "slot 0 never streams as a client");
            Assert.IsFalse(table.Get(1).hasPose);
            Assert.IsTrue(table.AcceptPose(1, 2, 1, V(4, 5, 6), NetQuat.Identity));
            Assert.IsTrue(table.AcceptPose(1, 2, 1 + 64, V(4, 5, 6), NetQuat.Identity), "poses carry only the low 6 bits of the epoch");
            Assert.IsTrue(table.Get(1).hasPose);
            Assert.AreEqual(5f, table.Get(1).position.y);

            Assert.IsFalse(table.Release(1, 2, 0, 1.0), "release with the old epoch");
            Assert.IsFalse(table.Release(1, 3, 1, 1.0), "release from a non-owner");
            Assert.IsTrue(table.Release(1, 2, 1, 1.0));
            Assert.IsFalse(table.Release(1, 2, 1, 1.0), "released twice");
            Assert.IsFalse(table.Handoff(1, 2, 0), "handoff with the old epoch");
            Assert.IsFalse(table.Handoff(1, 3, 1), "handoff from a non-owner");
            Assert.IsTrue(table.Handoff(1, 2, 1));
            Assert.AreEqual(0, table.Owner(1));
            Assert.AreEqual(2, table.Epoch(1), "the return to the host raises the epoch");
            Assert.IsFalse(table.Handoff(1, 2, 1), "a second handoff is stale");
            Assert.IsFalse(table.AcceptPose(1, 2, 1, V(9, 9, 9), NetQuat.Identity), "the old owner's late poses are dropped");
            Assert.IsFalse(table.IsCurrent(1, 2, 2));
            Assert.IsTrue(table.IsCurrent(1, 0, 2));
        }

        [Test]
        public void Table_Backstops_ListItemsReleasedThreeSecondsAgo_AndEverythingASlotOwns()
        {
            var table = new ItemOwnershipTable();
            table.Reset(5);
            var ids = new byte[8];
            table.TryGrab(0, 1, 0, true);
            table.TryGrab(1, 1, 0, true);
            table.TryGrab(2, 2, 0, true);
            table.HostGrab(3);
            Assert.AreEqual(0, table.FindExpired(100.0, 3.0, ids), "held items never expire");
            table.Release(0, 1, 1, 100.0);
            table.Release(2, 2, 1, 101.0);
            Assert.AreEqual(0, table.FindExpired(102.9, 3.0, ids));
            Assert.AreEqual(1, table.FindExpired(103.0, 3.0, ids));
            Assert.AreEqual(0, ids[0]);
            Assert.AreEqual(2, table.FindExpired(104.0, 3.0, ids));

            Assert.AreEqual(2, table.FindOwnedBy(1, ids));
            Assert.AreEqual(0, ids[0]);
            Assert.AreEqual(1, ids[1]);
            Assert.AreEqual(2, table.FindOwnedBy(0, ids), "items 3 and 4 are the host's");
            Assert.AreEqual(0, table.FindOwnedBy(3, ids));

            Assert.IsTrue(table.ReturnToHost(1, out byte previous));
            Assert.AreEqual(1, previous);
            Assert.AreEqual(0, table.Owner(1));
            Assert.IsFalse(table.IsHeld(1), "taken out of the (absent) client's hand");
            Assert.AreEqual(2, table.Epoch(1));
            Assert.IsFalse(table.ReturnToHost(1, out _), "already the host's");
            Assert.IsFalse(table.ReturnToHost(3, out _));
            Assert.IsFalse(table.ReturnToHost(99, out _));
        }

        [Test]
        public void Table_Handoff_IsTakenFromTheOwner_EvenWhileTheTableStillSaysHeld()
        {
            // The owner knows whether its hand is on the item. Its ItemReleased can have been turned down (sent
            // against an epoch that was refused), which would leave the table at "held" for good if the handoff
            // were refused too.
            var table = new ItemOwnershipTable();
            table.Reset(2);
            Assert.AreEqual(GrabResult.Granted, table.TryGrab(0, 1, 0, true));
            Assert.IsTrue(table.IsHeld(0));
            Assert.IsFalse(table.Handoff(0, 2, 1), "not from somebody else");
            Assert.IsFalse(table.Handoff(0, 1, 0), "not with a stale epoch");
            Assert.IsFalse(table.Handoff(0, 0, 1), "slot 0 never hands off as a client");
            Assert.IsTrue(table.IsHeld(0));
            Assert.IsTrue(table.Handoff(0, 1, 1), "from the owner at the current epoch: yes, held or not");
            Assert.AreEqual(0, table.Owner(0));
            Assert.IsFalse(table.IsHeld(0));
            Assert.AreEqual(2, table.Epoch(0));
        }

        [Test]
        public void Table_HeldBackstop_ListsItemsAClientHoldsButHasNotStreamedForThreeSeconds()
        {
            var table = new ItemOwnershipTable();
            table.Reset(4);
            var ids = new byte[8];
            table.TryGrab(0, 1, 0, true);
            table.Touch(0, 100.0);          // what the host session does when it grants
            table.TryGrab(1, 2, 0, true);
            table.Touch(1, 100.0);
            table.HostGrab(2);              // the host's own hand never counts
            Assert.AreEqual(0, table.FindUnstreamed(102.9, 3.0, ids));
            Assert.IsTrue(table.AcceptPose(1, 2, 1, V(1, 2, 3), NetQuat.Identity, 102.5), "slot 2 really holds item 1 and streams it");
            Assert.IsFalse(table.AcceptPose(0, 1, 0, V(1, 2, 3), NetQuat.Identity, 102.5), "a pose with a stale epoch does not count as streaming");
            Assert.AreEqual(1, table.FindUnstreamed(103.0, 3.0, ids), "slot 1 has streamed nothing since the grant");
            Assert.AreEqual(0, ids[0]);
            Assert.AreEqual(0, table.FindExpired(103.0, 3.0, ids), "the released-item backstop does not list held items");
            Assert.AreEqual(2, table.FindUnstreamed(105.5, 3.0, ids));
            Assert.IsTrue(table.Release(0, 1, 1, 105.6));
            Assert.AreEqual(1, table.FindUnstreamed(105.7, 3.0, ids), "a released item is the other backstop's business");
            Assert.AreEqual(1, ids[0]);
            Assert.AreEqual(0, table.FindUnstreamed(105.7, 3.0, null));
        }

        [Test]
        public void Table_Lock_SecuresAnItemForTheRestOfTheRun()
        {
            var table = new ItemOwnershipTable();
            table.Reset(3);
            Assert.IsFalse(table.Lock(0), "free and host-simulated: locking changes no ownership");
            Assert.AreEqual(0, table.Epoch(0));
            Assert.IsTrue(table.IsLocked(0));
            Assert.AreEqual(GrabResult.NotGrantable, table.TryGrab(0, 1, 0, true));
            Assert.IsFalse(table.HostGrab(0));

            table.TryGrab(1, 2, 0, true);
            Assert.IsTrue(table.Lock(1), "locking an item in a client's hand takes it away");
            Assert.AreEqual(0, table.Owner(1));
            Assert.IsFalse(table.IsHeld(1));
            Assert.AreEqual(2, table.Epoch(1));
            Assert.IsFalse(table.Lock(99));

            table.Reset(2);
            Assert.IsFalse(table.IsLocked(0), "a new run starts clean");
            Assert.AreEqual(0, table.Epoch(1));
            Assert.IsFalse(table.Contains(2));
            table.Reset(100000);
            Assert.AreEqual(ItemOwnershipTable.MaxItems, table.Count);
        }

        // ================================================================== the client state machine on its own

        [Test]
        public void Authority_GrabReleaseHandBack_WalksThroughItsModes()
        {
            var item = new ItemAuthority(3, 2);
            Assert.AreEqual(ItemMode.HostFree, item.Mode);
            Assert.IsTrue(item.CanGrab);
            Assert.IsFalse(item.SimulatedHere);

            Assert.IsTrue(item.BeginGrab(out byte epochSeen));
            Assert.AreEqual(0, epochSeen);
            Assert.AreEqual(ItemMode.Mine, item.Mode, "in the hand the same frame, before the host answers");
            Assert.IsTrue(item.Pending);
            Assert.IsTrue(item.HeldLocally);
            Assert.IsTrue(item.ShouldStream);
            Assert.AreEqual(1, item.WireEpoch, "streams with the epoch the grant will have");
            Assert.IsFalse(item.BeginGrab(out _), "already in the hand");

            Assert.AreEqual(ItemAuthorityEvent.Granted, item.OnOwner(2, 1, true, false, 10.0));
            Assert.IsFalse(item.Pending);
            Assert.AreEqual(1, item.Epoch);
            Assert.AreEqual(ItemMode.Mine, item.Mode);
            Assert.IsFalse(item.Tick(10.5, 0f, out _), "no hand-back while held");

            Assert.IsTrue(item.Release(11.0));
            Assert.IsFalse(item.Release(11.0), "already released");
            Assert.IsFalse(item.HeldLocally);
            Assert.AreEqual(ItemMode.Mine, item.Mode, "still simulated here during the flight");
            Assert.AreEqual(ItemAuthorityEvent.None, item.OnOwner(2, 1, false, false, 11.05), "the host echoing our release");

            Assert.IsFalse(item.Tick(11.1, 2.0f, out _), "moving");
            Assert.IsFalse(item.Tick(11.2, 0.2f, out _), "slow, but not for 0.3 s yet");
            Assert.IsFalse(item.Tick(11.4, 0.2f, out _));
            Assert.IsTrue(item.Tick(11.51, 0.2f, out byte handoffEpoch), "slower than 0.25 m/s for 0.3 s: hand it back");
            Assert.AreEqual(1, handoffEpoch);
            Assert.IsTrue(item.HandoffSent);
            Assert.IsFalse(item.ShouldStream);
            Assert.IsTrue(item.SimulatedHere, "the body stays dynamic here until the host confirms");
            Assert.IsFalse(item.Tick(11.6, 0f, out _), "fires once");
            Assert.IsFalse(item.Tick(20.0, 0f, out _));

            Assert.AreEqual(ItemAuthorityEvent.Returned, item.OnOwner(0, 2, false, false, 11.7));
            Assert.AreEqual(ItemMode.HostFree, item.Mode);
            Assert.AreEqual(2, item.Epoch);
            Assert.AreEqual(2, item.WireEpoch);
            Assert.AreEqual(11.7 + ItemAuthority.BlendSeconds, item.BlendUntil, 1e-9, "0.2 s blend back to the host's pose");
            Assert.IsTrue(item.AcceptPose(2));
            Assert.IsTrue(item.CanGrab);
        }

        [Test]
        public void HandBackTimer_FiresAfterThreeSeconds_EvenWhenNoThrowWasEverRegistered()
        {
            var item = new ItemAuthority(0, 1);
            item.BeginGrab(out _);
            item.OnOwner(1, 1, true, false, 0.0);
            Assert.IsTrue(item.Release(5.0)); // only the release was reported; no ItemThrown, no RegisterThrow
            for (double t = 5.0; t < 7.99; t += 0.011)
                Assert.IsFalse(item.Tick(t, 3.0f, out _), "still flying fast at " + t);
            Assert.IsTrue(item.Tick(8.0, 3.0f, out byte epoch), "3 s after the release it is handed back regardless of speed");
            Assert.AreEqual(1, epoch);
            Assert.IsFalse(item.Tick(8.1, 3.0f, out _));
            Assert.AreEqual(ItemAuthority.HandbackMaxSeconds, 3.0, 1e-12);
            Assert.AreEqual(ItemAuthority.HandbackSlowSeconds, 0.3, 1e-12);
            Assert.AreEqual(ItemAuthority.HandbackSpeed, 0.25f);
        }

        [Test]
        public void HandBackTimer_RestartsItsSlowClock_WhenTheItemSpeedsUpAgain_AndSurvivesBadSpeeds()
        {
            var item = new ItemAuthority(0, 1);
            item.BeginGrab(out _);
            item.OnOwner(1, 1, true, false, 0.0);
            item.Release(1.0);
            Assert.IsFalse(item.Tick(1.00, 0.1f, out _));
            Assert.IsFalse(item.Tick(1.25, 0.1f, out _));
            Assert.IsFalse(item.Tick(1.28, 4.0f, out _), "a bounce: fast again");
            Assert.IsFalse(item.Tick(1.30, 0.1f, out _));
            Assert.IsFalse(item.Tick(1.55, 0.1f, out _), "only 0.25 s slow since the bounce");
            Assert.IsTrue(item.Tick(1.61, 0.1f, out _));

            var nan = new ItemAuthority(0, 1);
            nan.BeginGrab(out _);
            nan.OnOwner(1, 1, true, false, 0.0);
            nan.Release(1.0);
            Assert.DoesNotThrow(() =>
            {
                Assert.IsFalse(nan.Tick(1.4, float.NaN, out _), "an unknown speed is not 'slow'");
                Assert.IsFalse(nan.Tick(2.0, float.PositiveInfinity, out _));
                Assert.IsFalse(nan.Tick(3.9, float.NaN, out _));
                Assert.IsTrue(nan.Tick(4.0, float.NaN, out _), "the 3 s limit still ends it");
            });
        }

        [Test]
        public void HandBackTimer_AndTheWholeStateMachine_NeverThrow_WhateverOrderThingsHappenIn()
        {
            var random = new Random(99);
            var item = new ItemAuthority(7, 2);
            double now = 0;
            int handoffs = 0, grabs = 0;
            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 200000; i++)
                {
                    now += random.NextDouble() * 0.2;
                    switch (random.Next(0, 9))
                    {
                        case 0: if (item.BeginGrab(out _)) grabs++; break;
                        case 1: item.Release(now); break;
                        case 2:
                        case 3: if (item.Tick(now, (float)random.NextDouble(), out _)) handoffs++; break;
                        case 4: item.OnOwner((byte)random.Next(0, 4), (byte)random.Next(0, 256), random.Next(0, 2) == 0, random.Next(0, 4) == 0, now); break;
                        case 5: item.AcceptPose((byte)random.Next(0, 256)); item.AcceptRest((byte)random.Next(0, 256)); break;
                        case 6: if (random.Next(0, 50) == 0) item.OnDeposited(); break;
                        case 7: if (random.Next(0, 20) == 0) item.Reset((byte)random.Next(0, 4)); break;
                        default: item.Tick(now, float.NaN, out _); break;
                    }
                    Assert.IsFalse(item.HeldLocally && item.Mode != ItemMode.Mine, "a held item is always simulated here");
                    Assert.IsFalse(item.ShouldStream && item.Mode != ItemMode.Mine);
                }
            });
            Assert.Greater(grabs, 1000);
            Assert.Greater(handoffs, 100);
        }

        [Test]
        public void Authority_DeniedGrab_PutsTheItemBack_AndThereIsNothingToRelease()
        {
            var item = new ItemAuthority(1, 2);
            Assert.IsTrue(item.BeginGrab(out _));
            // slot 3 won the race: first the broadcast naming it, then the direct "no" to our request
            Assert.AreEqual(ItemAuthorityEvent.None, item.OnOwner(3, 1, true, false, 4.0), "not our answer yet");
            Assert.IsTrue(item.Pending);
            Assert.AreEqual(ItemAuthorityEvent.Denied, item.OnOwner(3, 1, true, true, 4.0));
            Assert.AreEqual(ItemMode.RemoteHeld, item.Mode);
            Assert.IsFalse(item.Pending);
            Assert.IsFalse(item.HeldLocally);
            Assert.IsFalse(item.CanGrab, "somebody else holds it");
            Assert.IsFalse(item.SimulatedHere);
            Assert.AreEqual(1, item.Epoch);
            Assert.AreEqual(3, item.OwnerSlot);
            Assert.AreEqual(4.0 + ItemAuthority.BlendSeconds, item.BlendUntil, 1e-9);

            Assert.IsFalse(item.Release(4.1), "the cancelled selection sends no ItemReleased");
            Assert.IsFalse(item.Tick(10.0, 0f, out _), "and there is nothing to hand back");
            Assert.IsFalse(item.BeginGrab(out _));

            Assert.AreEqual(ItemAuthorityEvent.RemoteChanged, item.OnOwner(3, 1, false, false, 5.0), "the winner let go");
            Assert.AreEqual(ItemMode.HostFree, item.Mode);
            Assert.IsTrue(item.CanGrab);

            // refused by the game itself (mounted): the reply names the host, not holding
            var mounted = new ItemAuthority(1, 2);
            mounted.BeginGrab(out _);
            Assert.AreEqual(ItemAuthorityEvent.Denied, mounted.OnOwner(0, 0, false, true, 1.0));
            Assert.AreEqual(ItemMode.HostFree, mounted.Mode);
            Assert.AreEqual(0, mounted.WireEpoch);
            Assert.IsTrue(mounted.BeginGrab(out byte again), "free to try again later");
            Assert.AreEqual(0, again);
        }

        [Test]
        public void Authority_GrantArrivingAfterADenialCancelledTheHold_AsksForAReleaseAndHandsTheItemBack()
        {
            // Two requests out at once (catch, then re-grab inside one round trip). The host refuses the first and
            // grants the second: the refusal cancelled the local hold, so the grant makes us the holder of an item
            // no hand is on.
            var item = new ItemAuthority(0, 1);
            Assert.AreEqual(ItemAuthorityEvent.RemoteChanged, item.OnOwner(2, 1, false, false, 9.0), "slot 2 threw it: free flight, epoch 1");
            Assert.IsTrue(item.BeginGrab(out byte seen1));
            Assert.AreEqual(1, seen1);
            Assert.IsTrue(item.Release(10.0));
            Assert.AreEqual(2, item.WireEpoch);
            Assert.IsTrue(item.BeginGrab(out byte seen2));
            Assert.AreEqual(2, seen2);

            Assert.AreEqual(ItemAuthorityEvent.None, item.OnOwner(0, 2, false, false, 10.1), "the thrower's handoff reached the host first");
            Assert.AreEqual(ItemAuthorityEvent.Denied, item.OnOwner(0, 2, false, true, 10.1), "so our first request was stale");
            Assert.IsFalse(item.HeldLocally, "the denial cancels the selection");
            Assert.AreEqual(ItemMode.HostFree, item.Mode);
            Assert.IsTrue(item.Pending, "the second request is still out");
            Assert.IsFalse(item.Release(10.1), "cancelling the selection sends nothing");

            Assert.AreEqual(ItemAuthorityEvent.GrantedNotHeld, item.OnOwner(1, 3, true, false, 10.2), "the second request matched the new epoch");
            Assert.AreEqual(ItemMode.Mine, item.Mode);
            Assert.IsFalse(item.HeldLocally);
            Assert.IsFalse(item.Pending);
            Assert.AreEqual(3, item.WireEpoch, "the epoch for the ItemReleased the caller must send now");
            Assert.IsTrue(item.ShouldStream);
            Assert.IsFalse(item.Release(10.2), "there is no local hold to release: the caller sends ItemReleased because of the event");

            Assert.IsFalse(item.Tick(10.2, 0f, out _));
            Assert.IsFalse(item.Tick(10.45, 0f, out _));
            Assert.IsTrue(item.Tick(10.5, 0f, out byte handoffEpoch), "at rest for 0.3 s: hand it back");
            Assert.AreEqual(3, handoffEpoch);
            Assert.AreEqual(ItemAuthorityEvent.None, item.OnOwner(1, 3, false, false, 10.6), "the host confirms our release");
            Assert.AreEqual(ItemAuthorityEvent.Returned, item.OnOwner(0, 4, false, false, 10.7));
            Assert.AreEqual(ItemMode.HostFree, item.Mode);
            Assert.IsTrue(item.CanGrab);

            // The host naming us as holder out of the blue (no request out) is answered the same way.
            var surprised = new ItemAuthority(0, 1);
            Assert.AreEqual(ItemAuthorityEvent.GrantedNotHeld, surprised.OnOwner(1, 5, true, false, 20.0));
            Assert.AreEqual(5, surprised.WireEpoch);
            Assert.IsFalse(surprised.Tick(22.9, 9f, out _), "still flying fast, inside the 3 s window");
            Assert.IsTrue(surprised.Tick(23.0, 9f, out handoffEpoch), "the 3 s hand-back window");
            Assert.AreEqual(5, handoffEpoch);
            // ... and as owner of a free-flying item: nothing to release, only to hand back.
            var flying = new ItemAuthority(0, 1);
            Assert.AreEqual(ItemAuthorityEvent.None, flying.OnOwner(1, 5, false, false, 20.0));
            Assert.AreEqual(ItemMode.Mine, flying.Mode);
        }

        [Test]
        public void Authority_DropsPosesAndRests_WithAStaleEpoch_OrWhileItSimulatesTheItemItself()
        {
            var item = new ItemAuthority(1, 2);
            Assert.IsTrue(item.AcceptPose(0));
            Assert.IsFalse(item.AcceptPose(1), "from an epoch we have not been told about");
            item.OnOwner(3, 5, true, false, 0);
            Assert.AreEqual(ItemMode.RemoteHeld, item.Mode);
            Assert.IsFalse(item.AcceptPose(4), "stale");
            Assert.IsTrue(item.AcceptPose(5));
            Assert.IsTrue(item.AcceptPose(5 + 64 + 128), "only the low 6 bits travel in a pose");
            Assert.IsTrue(item.AcceptRest(5));
            Assert.IsFalse(item.AcceptRest(4));

            item.OnOwner(0, 6, false, false, 0);
            item.BeginGrab(out _);
            Assert.IsFalse(item.AcceptPose(6), "simulated here: the stream must not move it");
            Assert.IsFalse(item.AcceptPose(7));
            Assert.IsFalse(item.AcceptRest(6));
        }

        [Test]
        public void Authority_GrabReleaseAndHandBack_InsideOneRoundTrip_UseTheEpochsTheHostWillHave()
        {
            // Nothing from the host arrives until the very end: everything is predicted.
            var item = new ItemAuthority(0, 1);
            Assert.IsTrue(item.BeginGrab(out byte seen));
            Assert.AreEqual(0, seen);
            Assert.IsTrue(item.Release(0.05));
            Assert.AreEqual(1, item.WireEpoch, "ItemReleased carries the grant's epoch");
            Assert.IsTrue(item.Tick(0.06, 0f, out _) == false);
            Assert.IsTrue(item.Tick(0.40, 0f, out byte handoffEpoch));
            Assert.AreEqual(1, handoffEpoch);
            Assert.IsTrue(item.BeginGrab(out seen), "picked up again before any answer came back");
            Assert.AreEqual(2, seen, "against the epoch the host will have after our handoff");
            Assert.AreEqual(3, item.WireEpoch);

            // now the answers arrive, in the order the host produced them
            Assert.AreEqual(ItemAuthorityEvent.Granted, item.OnOwner(1, 1, true, false, 0.5));
            Assert.IsTrue(item.Pending, "the second request is still out");
            Assert.AreEqual(ItemAuthorityEvent.None, item.OnOwner(1, 1, false, false, 0.5), "echo of our release");
            Assert.AreEqual(ItemAuthorityEvent.None, item.OnOwner(0, 2, false, false, 0.5), "confirmation of our handoff: history, not a denial");
            Assert.AreEqual(ItemMode.Mine, item.Mode);
            Assert.IsTrue(item.HeldLocally);
            Assert.AreEqual(ItemAuthorityEvent.Granted, item.OnOwner(1, 3, true, false, 0.5));
            Assert.IsFalse(item.Pending);
            Assert.AreEqual(3, item.Epoch);
            Assert.AreEqual(3, item.WireEpoch);
            Assert.AreEqual(ItemMode.Mine, item.Mode);
        }

        [Test]
        public void Authority_Deposit_SecuresTheItem()
        {
            var item = new ItemAuthority(0, 1);
            item.BeginGrab(out _);
            item.OnOwner(1, 1, true, false, 0);
            item.OnDeposited();
            Assert.IsTrue(item.Secured);
            Assert.AreEqual(ItemMode.HostFree, item.Mode);
            Assert.IsFalse(item.HeldLocally);
            Assert.IsFalse(item.CanGrab);
            Assert.IsFalse(item.BeginGrab(out _));
            Assert.IsFalse(item.Release(1.0));
            item.Reset(1);
            Assert.IsTrue(item.CanGrab, "the next run starts clean");
        }

        // ================================================================== through real sessions

        [Test]
        public void TwoClientsGrabTheSameItemAtTheSameMoment_OneIsGranted_TheOtherDenied()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            NetTestRig.Member c = m_Rig.Join();
            StartRun();
            m_Rig.Hub.Latency = 0.08; // both requests are in the air together

            Assert.IsTrue(Grab(b, 2));
            Assert.IsTrue(Grab(a, 2));
            Assert.AreEqual(ItemMode.Mine, a.World.Items[2].Mode, "both have it in hand locally at once");
            Assert.AreEqual(ItemMode.Mine, b.World.Items[2].Mode);
            m_Rig.Step(1.0);

            Assert.AreEqual(1, m_Rig.HostWorld.GrantedItems.Count, "exactly one grant");
            Assert.AreEqual(2, m_Rig.Host.Items.Owner(2), "the request that reached the host first won");
            Assert.AreEqual(1, m_Rig.Host.Items.Epoch(2));
            Assert.IsTrue(m_Rig.Host.Items.IsHeld(2));
            Assert.AreEqual(1, b.World.Granted);
            Assert.AreEqual(0, b.World.Denied);
            Assert.AreEqual(0, a.World.Granted);
            Assert.AreEqual(1, a.World.Denied, "the loser is told once");
            Assert.AreEqual(ItemAuthorityEvent.Denied, a.World.ItemLastEvent[2]);
            Assert.IsFalse(a.World.Items[2].HeldLocally);
            Assert.IsFalse(a.World.Items[2].CanGrab);
            AssertEveryoneAgrees(2, "after the race");
            Assert.AreEqual(ItemMode.RemoteHeld, c.World.Items[2].Mode, "the bystander sees it in slot 2's hand");

            // the loser's cancelled selection produces no release, no handoff, no poses
            Assert.IsFalse(Release(a, 2));
            m_Rig.Step(2.0);
            Assert.AreEqual(0, m_Rig.HostWorld.Calls[NetMsg.ItemReleased]);
            Assert.AreEqual(0, m_Rig.HostWorld.Returns.Count);
            Assert.AreEqual(2, m_Rig.Host.Items.Owner(2));
            Assert.AreEqual(2, m_Rig.HostWorld.LastSlot);
            Assert.AreEqual(1, a.World.Calls[NetMsg.ItemCollected], "collected once, by the winner");
            Assert.AreEqual(2, a.World.ItemCollectedBy[2]);
        }

        [Test]
        public void TheSameRace_UnderLossAndReordering_StillGivesOneOwner_ForManySeeds()
        {
            for (int seed = 1; seed <= 12; seed++)
            {
                using (var rig = new NetTestRig(seed))
                {
                    m_Rig.Dispose();
                    m_Rig = rig;
                    rig.EachStep = DriveItems;
                    NetTestRig.Member a = rig.Join();
                    NetTestRig.Member b = rig.Join();
                    NetTestRig.Member c = rig.Join();
                    StartRun();
                    rig.Hub.Latency = 0.05;
                    rig.Hub.Jitter = 0.05;
                    rig.Hub.Loss = 0.2;
                    rig.Hub.Reorder = 0.3;
                    rig.Hub.Duplicate = 0.2;
                    Grab(a, 1);
                    Grab(b, 1);
                    Grab(c, 1);
                    rig.Step(4.0);
                    int granted = a.World.Granted + b.World.Granted + c.World.Granted;
                    int denied = a.World.Denied + b.World.Denied + c.World.Denied;
                    Assert.AreEqual(1, granted, "seed " + seed + ": one grant");
                    Assert.AreEqual(2, denied, "seed " + seed + ": two denials");
                    Assert.AreEqual(1, rig.HostWorld.GrantedItems.Count, "seed " + seed);
                    Assert.AreNotEqual(0, rig.Host.Items.Owner(1));
                    AssertEveryoneAgrees(1, "seed " + seed);
                    m_Rig = new NetTestRig();
                }
            }
        }

        [Test]
        public void TheHostsOwnGrab_WinsATieAgainstAClient()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            StartRun();
            m_Rig.Hub.Latency = 0.08;
            Assert.IsTrue(Grab(a, 3));             // the client grabs ...
            Assert.IsTrue(m_Rig.Host.LocalGrab(3)); // ... and the host's own hand closes before the request arrives
            m_Rig.Step(1.0);
            Assert.AreEqual(0, m_Rig.Host.Items.Owner(3));
            Assert.IsTrue(m_Rig.Host.Items.IsHeld(3));
            Assert.AreEqual(0, m_Rig.HostWorld.GrantedItems.Count);
            Assert.AreEqual(1, a.World.Denied);
            Assert.AreEqual(ItemMode.RemoteHeld, a.World.Items[3].Mode, "held by the host");
            Assert.AreEqual(ItemMode.RemoteHeld, b.World.Items[3].Mode);
            AssertEveryoneAgrees(3, "host tie");
            Assert.IsFalse(m_Rig.Host.LocalGrab(3), "the host cannot grab twice");

            m_Rig.Host.LocalRelease(3);
            m_Rig.Step(0.5);
            Assert.AreEqual(ItemMode.HostFree, a.World.Items[3].Mode);
            Assert.IsTrue(Grab(a, 3));
            m_Rig.Step(0.5);
            Assert.AreEqual(1, a.World.Granted, "free again once the host lets go");
            Assert.IsFalse(m_Rig.Host.LocalGrab(3), "and now the host is the one refused");
            AssertEveryoneAgrees(3, "after the client took it");
        }

        [Test]
        public void StaleEpochs_AreDropped_ByTheHost_AndByEveryClient()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            StartRun();
            Grab(a, 1);
            m_Rig.Step(0.5);
            Assert.AreEqual(1, m_Rig.Host.Items.Owner(1));
            int posesAtHost = m_Rig.HostWorld.Calls[NetMsg.ItemPoses];
            int posesAtB = b.World.PosesAccepted;
            Assert.Greater(posesAtHost, 5);
            Assert.Greater(posesAtB, 5);
            m_NoHandoff.Add(a);
            a.Frozen = true; // the owner goes quiet so only the stale traffic below arrives
            m_Rig.Step(0.2);
            posesAtHost = m_Rig.HostWorld.Calls[NetMsg.ItemPoses];
            posesAtB = b.World.PosesAccepted;
            long rejected = m_Rig.Host.Stats.rejected;

            // b, who is not the owner, sends poses, a release, a throw and a handoff for the item with the old epoch 0
            // and with the current epoch 1: none of it may reach the game or the other clients.
            for (byte epoch = 0; epoch <= 1; epoch++)
            {
                b.Session.QueueItemPose(1, V(99, 99, 99), NetQuat.Identity, epoch, true);
                b.Session.SendItemReleased(1, epoch, V(99, 99, 99), NetQuat.Identity, V(99, 99, 99));
                b.Session.SendItemThrown(1, epoch, V(9, 9, 9), NetVec3.Zero, 9f, true);
                b.Session.SendItemHandoff(1, epoch, V(99, 99, 99), NetQuat.Identity, NetVec3.Zero, NetVec3.Zero);
                m_Rig.Step(0.2);
            }
            Assert.AreEqual(posesAtHost, m_Rig.HostWorld.Calls[NetMsg.ItemPoses], "no stale pose reached the game");
            Assert.AreEqual(0, m_Rig.HostWorld.Calls[NetMsg.ItemReleased]);
            Assert.AreEqual(0, m_Rig.HostWorld.Calls[NetMsg.ItemThrown]);
            Assert.AreEqual(0, m_Rig.HostWorld.Returns.Count);
            Assert.AreEqual(rejected + 8, m_Rig.Host.Stats.rejected, "all eight were counted as rejected");
            Assert.AreEqual(1, m_Rig.Host.Items.Owner(1), "ownership is untouched");
            Assert.AreEqual(1, m_Rig.Host.Items.Epoch(1));
            Assert.IsTrue(m_Rig.Host.Items.IsHeld(1));

            // the real owner, but with last run's epoch
            a.Frozen = false;
            a.Session.SendItemReleased(1, 0, V(5, 5, 5), NetQuat.Identity, V(5, 5, 5));
            a.Session.SendItemHandoff(1, 0, V(5, 5, 5), NetQuat.Identity, NetVec3.Zero, NetVec3.Zero);
            m_Rig.Step(0.3);
            Assert.AreEqual(0, m_Rig.HostWorld.Calls[NetMsg.ItemReleased], "stale epoch from the owner is dropped too");
            Assert.AreEqual(0, m_Rig.HostWorld.Returns.Count);
            Assert.IsTrue(m_Rig.Host.Items.IsHeld(1));

            // a grab against a stale epoch is denied
            m_Rig.Host.TakeItem(1); // epoch 2, host owns it, free
            m_Rig.Step(0.3);
            int grants = m_Rig.HostWorld.GrantedItems.Count;
            b.Session.SendGrabRequest(1, 1, 0);
            m_Rig.Step(0.3);
            Assert.AreEqual(grants, m_Rig.HostWorld.GrantedItems.Count, "a request that saw epoch 1 loses against epoch 2");
            Assert.AreEqual(0, m_Rig.Host.Items.Owner(1));

            // and on the receiving side: a client drops a pose whose epoch is not the current one
            int accepted = b.World.PosesAccepted, dropped = b.World.PosesDropped;
            b.World.OnItemPose(new ItemPoseEntry { itemId = 1, epoch = 1, position = V(50, 50, 50) }, 0);
            Assert.AreEqual(accepted, b.World.PosesAccepted);
            Assert.AreEqual(dropped + 1, b.World.PosesDropped);
            b.World.OnItemPose(new ItemPoseEntry { itemId = 1, epoch = 2, position = V(7, 7, 7) }, 0);
            Assert.AreEqual(accepted + 1, b.World.PosesAccepted);
            Assert.AreEqual(7f, b.World.ItemPoses[1].position.x);
        }

        [Test]
        public void ACarriedItem_IsStreamedToTheHostAndTheOtherClients_NotBackToItsOwner()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            NetTestRig.Member c = m_Rig.Join();
            StartRun();
            Grab(a, 4);
            m_Rig.Step(1.0);
            NetVec3 expected = PoseOf(a, 4);

            Assert.AreEqual(20, m_Rig.HostWorld.Calls[NetMsg.ItemPoses], 3, "the owner streams at 20 Hz");
            Assert.AreEqual(4, m_Rig.HostWorld.LastItem);
            Assert.AreEqual(expected.x, m_Rig.HostWorld.LastPosition.x, Quant.PosCmMaxError);
            Assert.IsTrue(m_Rig.Host.Items.Get(4).hasPose);
            Assert.AreEqual(expected.x, m_Rig.Host.Items.Get(4).position.x, Quant.PosCmMaxError, "the host remembers the last pose for its backstops");
            foreach (NetTestRig.Member other in new[] { b, c })
            {
                Assert.AreEqual(19, other.World.PosesAccepted, 4, "relayed to slot " + other.Slot);
                Assert.AreEqual(expected.x, other.World.ItemPoses[4].position.x, 2 * Quant.PosCmMaxError);
                Assert.AreEqual(ItemMode.RemoteHeld, other.World.Items[4].Mode);
                Assert.IsFalse(other.World.Items[4].CanGrab, "taking it out of another hand is not in the first release");
            }
            Assert.AreEqual(0, a.World.Calls[NetMsg.ItemPoses], "never echoed to the owner");
            Assert.AreEqual(ItemMode.Mine, a.World.Items[4].Mode);
            Assert.AreEqual(1, m_Rig.HostWorld.Calls[NetMsg.GrabRequest]);
            Assert.AreEqual(1, m_Rig.HostWorld.LastHand);
        }

        [Test]
        public void Release_ThenTheHandBackTimer_ReturnTheItemToTheHost_WithoutAnyThrowMessage()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            StartRun();
            m_Rig.Hub.Latency = 0.04;
            Grab(a, 0);
            m_Rig.Step(0.5);
            Assert.IsTrue(Release(a, 0));
            m_Rig.Step(0.2);
            Assert.AreEqual(1, m_Rig.HostWorld.Calls[NetMsg.ItemReleased], "the host clears the remote holder on ItemReleased");
            Assert.AreEqual(1.4f, m_Rig.HostWorld.LastReleasePosition.y, Quant.PosCmMaxError);
            Assert.IsFalse(m_Rig.Host.Items.IsHeld(0));
            Assert.AreEqual(1, m_Rig.Host.Items.Owner(0), "the thrower still simulates the flight");
            Assert.AreEqual(ItemMode.HostFree, b.World.Items[0].Mode, "others see it as free (nobody holds it)");
            Assert.AreEqual(1, b.World.Items[0].OwnerSlot);
            Assert.AreEqual(0, m_Rig.HostWorld.Returns.Count);

            m_Rig.Step(0.6); // resting: 0.3 s slow, then the handoff travels
            Assert.AreEqual(0, m_Rig.HostWorld.Calls[NetMsg.ItemThrown], "no throw was ever registered");
            Assert.AreEqual(1, m_Rig.HostWorld.Returns.Count, "and the item still came back");
            ItemReturn back = m_Rig.HostWorld.Returns[0];
            Assert.AreEqual(ItemReturnCause.Handoff, back.cause);
            Assert.AreEqual(1, back.fromSlot);
            Assert.IsTrue(back.hasPose);
            Assert.AreEqual(PoseOf(a, 0).x, back.position.x, Quant.PosCmMaxError);
            Assert.AreEqual(0, m_Rig.Host.Items.Owner(0));
            Assert.AreEqual(2, m_Rig.Host.Items.Epoch(0));
            Assert.AreEqual(1, a.World.Returned);
            Assert.AreEqual(0, a.World.Revoked);
            Assert.Greater(a.World.Items[0].BlendUntil, m_Rig.Now - 0.5, "everyone blends back to the host's copy");
            AssertEveryoneAgrees(0, "after the handoff");

            // host-simulated again: its poses and its rest message are accepted everywhere, the old owner included
            int acceptedA = a.World.PosesAccepted;
            m_Rig.EachStep = now => { DriveItems(now); m_Rig.Host.QueueItemPose(0, V(1, 0.5f, 1), NetQuat.Identity, false, false); };
            m_Rig.Step(0.5);
            m_Rig.EachStep = DriveItems;
            Assert.Greater(a.World.PosesAccepted, acceptedA + 5);
            m_Rig.Host.SendItemRest(0, V(1.5f, 0.2f, 1), NetQuat.Identity);
            m_Rig.Step(0.3);
            Assert.AreEqual(1, a.World.RestsAccepted);
            Assert.AreEqual(1, b.World.RestsAccepted);
            Assert.AreEqual(1.5f, b.World.ItemPoses[0].position.x, Quant.PosCmMaxError, "ItemRest heals the final position");
        }

        [Test]
        public void AFastThrow_IsHandedBackAfterThreeSeconds_AndTheThrowReachesTheHost()
        {
            NetTestRig.Member a = m_Rig.Join();
            StartRun();
            m_Speed[a] = 6f; // still flying fast the whole time
            Grab(a, 2);
            m_Rig.Step(0.3);
            Release(a, 2);
            a.Session.SendItemThrown(2, a.World.Items[2].WireEpoch, V(6, 1, 0), V(0, 3, 0), 6.08f, true);
            m_Rig.Step(2.8);
            Assert.AreEqual(1, m_Rig.HostWorld.Calls[NetMsg.ItemThrown]);
            Assert.AreEqual(6.08f, m_Rig.HostWorld.LastThrowSpeed, 0.0051f);
            Assert.AreEqual(0, m_Rig.HostWorld.Returns.Count, "not yet: 3 s have not passed");
            Assert.AreEqual(1, m_Rig.Host.Items.Owner(2));
            m_Rig.Step(0.4);
            Assert.AreEqual(1, m_Rig.HostWorld.Returns.Count, "handed back at 3 s");
            Assert.AreEqual(ItemReturnCause.Handoff, m_Rig.HostWorld.Returns[0].cause);
            Assert.AreEqual(6f, m_Rig.HostWorld.Returns[0].linearVelocity.x, 0.0051f, "with its velocity, so the host carries the flight on");
            AssertEveryoneAgrees(2, "after the 3 s handoff");
        }

        [Test]
        public void NoHandoffWithinTheFlightBackstop_TheHostTakesTheItemBack_AtItsLastPose()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            StartRun();
            m_NoHandoff.Add(a); // a client whose hand-back never comes
            Grab(a, 5);
            m_Rig.Step(0.5);
            Release(a, 5);
            m_Rig.Step(4.3);
            Assert.AreEqual(1, m_Rig.Host.Items.Owner(5), "still the thrower's just before 4.5 s");
            Assert.AreEqual(0, m_Rig.HostWorld.Returns.Count);
            m_Rig.Step(0.4);
            Assert.AreEqual(0, m_Rig.Host.Items.Owner(5), "backstop: owned but not held for 4.5 s");
            Assert.AreEqual(1, m_Rig.HostWorld.Returns.Count);
            ItemReturn back = m_Rig.HostWorld.Returns[0];
            Assert.AreEqual(ItemReturnCause.Backstop, back.cause);
            Assert.AreEqual(1, back.fromSlot);
            Assert.IsTrue(back.hasPose);
            Assert.AreEqual(PoseOf(a, 5).x, back.position.x, Quant.PosCmMaxError, "at the last pose it streamed");
            Assert.AreEqual(0f, back.linearVelocity.x);
            m_Rig.Step(0.3);
            Assert.AreEqual(1, a.World.Revoked, "the owner is told it lost the item");
            Assert.AreEqual(ItemMode.HostFree, a.World.Items[5].Mode);
            AssertEveryoneAgrees(5, "after the backstop");
            Assert.AreEqual(NetProtocol.ItemFlightBackstopSeconds, 4.5, 1e-12);
            Assert.Greater(NetProtocol.ItemFlightBackstopSeconds, ItemAuthority.HandbackMaxSeconds + 1.0, "the backstop leaves the thrower's own hand-back time to arrive");
        }

        /// <summary>The trip a hand-back took, for the host's catch-up and the thrower's lead: measured from the acks every header carries.</summary>
        [Test]
        public void RoundTrip_IsMeasuredFromTheAcks_OnBothEnds_WithNoChangeOnTheWire()
        {
            NetTestRig.Member a = m_Rig.Join();
            Assert.IsNaN(m_Rig.Host.RoundTripSeconds(0), "no round trip to itself");
            Assert.IsNaN(m_Rig.Host.RoundTripSeconds(7), "nor to an empty slot");
            m_Rig.Hub.Latency = 0.15;
            m_Rig.Step(4.0);
            double client = a.Session.RoundTripSeconds, host = m_Rig.Host.RoundTripSeconds(a.Slot);
            // 300 ms, plus at most a send tick for the ack to ride back on.
            Assert.That(client, Is.InRange(0.28, 0.4), "client's estimate " + client);
            Assert.That(host, Is.InRange(0.28, 0.4), "host's estimate " + host);
            m_Rig.Hub.Latency = 0.02;
            m_Rig.Step(4.0);
            Assert.That(a.Session.RoundTripSeconds, Is.InRange(0.03, 0.12), "it follows a faster link");
            Assert.That(m_Rig.Host.RoundTripSeconds(a.Slot), Is.InRange(0.03, 0.12));
        }

        [Test]
        public void WhenTheOwnerGoesAway_TheItemReturnsToTheHost()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            StartRun();
            Grab(a, 1);
            Grab(a, 3);
            m_Rig.Step(0.5);
            Assert.IsTrue(m_Rig.Host.Items.IsHeld(1));
            m_Rig.Freeze(a); // headset off with two items in hand
            m_Rig.Step(1.4);
            Assert.AreEqual(1, m_Rig.Host.Items.Owner(1));
            m_Rig.Step(0.3);
            Assert.IsTrue(m_Rig.Host.IsAway(1));
            Assert.AreEqual(0, m_Rig.Host.Items.Owner(1), "taken over when the owner went Away");
            Assert.AreEqual(0, m_Rig.Host.Items.Owner(3));
            Assert.IsFalse(m_Rig.Host.Items.IsHeld(1));
            Assert.AreEqual(2, m_Rig.HostWorld.Returns.Count);
            foreach (ItemReturn back in m_Rig.HostWorld.Returns)
            {
                Assert.AreEqual(ItemReturnCause.OwnerAway, back.cause);
                Assert.AreEqual(1, back.fromSlot);
                Assert.IsTrue(back.hasPose);
                Assert.AreEqual(PoseOf(a, back.itemId).x, back.position.x, Quant.PosCmMaxError, "at its last streamed pose");
            }
            m_Rig.Step(0.3);
            Assert.AreEqual(ItemMode.HostFree, b.World.Items[1].Mode);
            Assert.IsTrue(Grab(b, 1), "somebody else can pick it up");
            m_Rig.Step(0.3);
            Assert.AreEqual(2, m_Rig.Host.Items.Owner(1));

            // the sleeper wakes up still "holding" both: it is told otherwise
            m_Rig.Thaw(a);
            m_Rig.Step(1.0);
            Assert.AreEqual(2, a.World.Revoked);
            Assert.IsFalse(a.World.Items[1].HeldLocally);
            Assert.AreEqual(ItemMode.RemoteHeld, a.World.Items[1].Mode);
            Assert.AreEqual(ItemMode.HostFree, a.World.Items[3].Mode);
            Assert.IsFalse(Release(a, 1), "its stale selection releases nothing");
            AssertEveryoneAgrees(1, "after waking");
            AssertEveryoneAgrees(3, "after waking");
        }

        [Test]
        public void WhenTheOwnerLeavesOrIsDropped_TheItemReturnsToTheHost()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            StartRun();
            Grab(a, 0);
            Grab(b, 2);
            m_Rig.Step(0.5);

            a.Session.Leave(m_Rig.Now); // Bye with an item in hand
            m_Rig.Step(0.2);
            Assert.AreEqual(0, m_Rig.Host.Items.Owner(0));
            Assert.AreEqual(1, m_Rig.HostWorld.Returns.Count);
            Assert.AreEqual(ItemReturnCause.OwnerLeft, m_Rig.HostWorld.Returns[0].cause);
            Assert.AreEqual(1, m_Rig.HostWorld.Returns[0].fromSlot);
            Assert.IsTrue(m_Rig.HostWorld.Returns[0].hasPose);
            Assert.AreEqual(ItemMode.HostFree, b.World.Items[0].Mode);
            Assert.AreEqual(2, b.World.Items[0].Epoch);

            // b pauses (Away) and then stays silent until the 45 s drop: the item came back at the Away step
            b.Session.SetAway(true, m_Rig.Now);
            m_Rig.Step(0.2);
            Assert.AreEqual(0, m_Rig.Host.Items.Owner(2));
            Assert.AreEqual(ItemReturnCause.OwnerAway, m_Rig.HostWorld.Returns[1].cause);
            m_Rig.Freeze(b);
            m_Rig.Step(46.0);
            Assert.IsFalse(m_Rig.Host.IsConnected(2));
            Assert.AreEqual(2, m_Rig.HostWorld.Returns.Count, "nothing left to return at the drop");
            Assert.AreEqual(0, m_Rig.Host.Items.FindOwnedBy(2, new byte[8]));
        }

        [Test]
        public void AGrabTheGameRefuses_IsDenied_AndChangesNothing()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            StartRun();
            m_Rig.HostWorld.AllowGrabs = false; // still mounted behind a panel
            Assert.IsTrue(Grab(a, 4));
            m_Rig.Step(0.4);
            Assert.AreEqual(1, a.World.Denied);
            Assert.AreEqual(0, m_Rig.HostWorld.GrantedItems.Count);
            Assert.AreEqual(0, m_Rig.Host.Items.Epoch(4), "no epoch change, so nobody else is disturbed");
            Assert.AreEqual(0, b.World.Calls[NetMsg.ItemOwner], "the refusal goes only to the requester");
            Assert.AreEqual(ItemMode.HostFree, a.World.Items[4].Mode);
            AssertEveryoneAgrees(4, "after the refusal");

            m_Rig.HostWorld.AllowGrabs = true; // the panel is open now
            Assert.IsTrue(Grab(a, 4));
            m_Rig.Step(0.4);
            Assert.AreEqual(1, a.World.Granted);
            AssertEveryoneAgrees(4, "after the retry");

            // an Away player cannot grab
            b.Session.SetAway(true, m_Rig.Now);
            m_Rig.Step(0.2);
            Assert.IsTrue(Grab(b, 5));
            m_Rig.Step(0.4);
            Assert.AreEqual(1, b.World.Denied);
            Assert.AreEqual(0, m_Rig.Host.Items.Owner(5));

            // an item id outside the run is refused without touching anything
            a.Session.SendGrabRequest(200, 0, 0);
            m_Rig.Step(0.3);
            Assert.AreEqual(1, m_Rig.HostWorld.GrantedItems.Count);
        }

        [Test]
        public void ADeposit_LocksTheItem_EvenOutOfAClientsHand()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            StartRun();
            Grab(a, 3);
            m_Rig.Step(0.5);
            m_Rig.Host.SendItemDeposited(3, 1, 130, 25, 2); // the locker scanned it while it was still in the hand
            m_Rig.Step(0.4);

            Assert.IsTrue(m_Rig.Host.Items.IsLocked(3));
            Assert.AreEqual(0, m_Rig.Host.Items.Owner(3));
            Assert.AreEqual(1, m_Rig.HostWorld.Returns.Count);
            Assert.AreEqual(ItemReturnCause.HostTookOver, m_Rig.HostWorld.Returns[0].cause);
            Assert.AreEqual(1, a.World.Revoked, "the holder's selection is cancelled");
            Assert.IsFalse(a.World.Items[3].HeldLocally);
            foreach (NetTestRig.Member m in new[] { a, b })
            {
                Assert.IsTrue(m.World.Items[3].Secured);
                Assert.IsFalse(m.World.Items[3].CanGrab);
                Assert.AreEqual(1, m.World.ItemDepositedBy[3]);
                Assert.AreEqual(130, m.World.ItemValues[3]);
                Assert.AreEqual(2, m.World.ItemLockerSlot[3], "everyone uses the host's locker slot");
            }
            Assert.IsFalse(Grab(b, 3), "refused locally");
            b.Session.SendGrabRequest(3, m_Rig.Host.Items.Epoch(3), 0); // and by the host, if a client tried anyway
            m_Rig.Step(0.3);
            Assert.AreEqual(1, m_Rig.HostWorld.GrantedItems.Count);
            Assert.AreEqual(0, m_Rig.Host.Items.Owner(3));
            Assert.IsFalse(m_Rig.Host.LocalGrab(3));
            AssertEveryoneAgrees(3, "after the deposit");
        }

        [Test]
        public void AnItemInFreeFlight_CanBeCaughtByAnotherPlayer_AndTheThrowerLetsGoOfIt()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            StartRun();
            m_Speed[a] = 5f; // a long, fast throw: no early hand-back
            Grab(a, 1);
            m_Rig.Step(0.4);
            Release(a, 1);
            m_Rig.Step(0.3);
            Assert.AreEqual(1, m_Rig.Host.Items.Owner(1));
            Assert.IsTrue(b.World.Items[1].CanGrab, "in flight, nobody holding it");

            Assert.IsTrue(Grab(b, 1));
            m_Rig.Step(0.4);
            Assert.AreEqual(2, m_Rig.Host.Items.Owner(1), "caught");
            Assert.IsTrue(m_Rig.Host.Items.IsHeld(1));
            Assert.AreEqual(1, b.World.Granted);
            Assert.AreEqual(1, a.World.Revoked, "the thrower stops simulating it");
            Assert.AreEqual(ItemMode.RemoteHeld, a.World.Items[1].Mode);
            m_Rig.Step(2.0);
            Assert.AreEqual(0, m_Rig.HostWorld.Returns.Count, "the thrower's hand-back timer no longer applies");
            Assert.AreEqual(2, m_Rig.Host.Items.Owner(1));
            Assert.AreEqual(1, a.World.Calls[NetMsg.ItemCollected], "collected only on the first pickup");
            AssertEveryoneAgrees(1, "after the catch");
        }

        /// <summary>
        /// Slot 2 throws item 0 and its hand-back leaves; in that same frame slot 1 catches the item, lets go and
        /// grabs again (a pass to the other hand). The host sees: handoff, grab (stale: denied), release (not the
        /// owner: dropped), grab (matches the new epoch: granted).
        /// </summary>
        void RaceACatchAgainstTheThrowersHandBack(NetTestRig.Member catcher, NetTestRig.Member thrower)
        {
            StartRun(3);
            m_Rig.Hub.Latency = 0.04;
            m_Speed[thrower] = 5f; // a fast throw: only the 3 s limit hands it back
            Assert.IsTrue(Grab(thrower, 0));
            m_Rig.Step(0.5);
            Assert.IsTrue(Release(thrower, 0));
            Assert.IsTrue(m_Rig.StepUntil(() => thrower.World.Items[0].HandoffSent, 3.5), "the thrower's 3 s hand-back fired");
            thrower.Session.Flush(m_Rig.Now); // the handoff is on the wire ...

            ItemAuthority item = catcher.World.Items[0];
            Assert.IsTrue(item.BeginGrab(out byte seen1)); // ... when the catcher grabs,
            catcher.Session.SendGrabRequest(0, seen1, 0);
            Assert.IsTrue(item.Release(m_Rig.Now));        // lets go,
            catcher.Session.SendItemReleased(0, item.WireEpoch, V(9, 9, 9), NetQuat.Identity, NetVec3.Zero);
            Assert.IsTrue(item.BeginGrab(out byte seen2)); // and grabs with the other hand
            catcher.Session.SendGrabRequest(0, seen2, 1);
            catcher.Session.Flush(m_Rig.Now);
            Assert.AreEqual(1, seen1);
            Assert.AreEqual(2, seen2);

            Assert.IsTrue(m_Rig.StepUntil(() => catcher.World.Denied == 1, 1.0), "the first grab lost to the handoff");
            catcher.World.Items[0].Release(m_Rig.Now); // ItemSync cancels the selection on Denied: nothing is sent
        }

        [Test]
        public void ACatchRacingTheThrowersHandBack_FirstGrabDeniedSecondGranted_NeverLeavesTheItemHeldForEver()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            RaceACatchAgainstTheThrowersHandBack(a, b);

            Assert.IsTrue(m_Rig.StepUntil(() => a.World.GrantedNotHeld == 1, 1.0), "the second grab was granted to a hand that had been told no");
            Assert.AreEqual(0, a.World.Granted);
            m_Rig.Step(0.2);
            Assert.AreEqual(1, m_Rig.Host.Items.Owner(0));
            Assert.IsFalse(m_Rig.Host.Items.IsHeld(0), "the ItemReleased sent on GrantedNotHeld took it out of slot 1's hand on the host");
            Assert.AreEqual(ItemMode.HostFree, b.World.Items[0].Mode, "so nobody sees it stuck in slot 1's hand");

            m_Rig.Step(1.0);
            ItemOwnershipTable table = m_Rig.Host.Items;
            Assert.AreEqual(0, table.Owner(0), "and the hand-back returned it to the host");
            Assert.IsFalse(table.IsHeld(0));
            Assert.AreEqual(4, table.Epoch(0), "thrower's grab, its handoff, the catcher's grant, the catcher's handoff");
            Assert.AreEqual(ItemReturnCause.Handoff, m_Rig.HostWorld.Returns[m_Rig.HostWorld.Returns.Count - 1].cause);
            AssertEveryoneAgrees(0, "after the race");
            Assert.IsTrue(a.World.Items[0].CanGrab && b.World.Items[0].CanGrab, "anybody can pick it up again");
            Assert.IsTrue(m_Rig.Host.LocalGrab(0), "including the host");
        }

        [Test]
        public void TheSameRace_WhenTheClientNeverSendsTheRelease_IsHealedByTheHandoffAlone()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            a.World.Session = null; // an ItemSync that ignores GrantedNotHeld: no ItemReleased is sent
            RaceACatchAgainstTheThrowersHandBack(a, b);

            Assert.IsTrue(m_Rig.StepUntil(() => a.World.GrantedNotHeld == 1, 1.0));
            m_Rig.Step(0.1);
            Assert.AreEqual(1, m_Rig.Host.Items.Owner(0));
            Assert.IsTrue(m_Rig.Host.Items.IsHeld(0), "the host still believes slot 1 holds it");
            int releasedBefore = m_Rig.HostWorld.Calls[NetMsg.ItemReleased];

            m_Rig.Step(1.0); // slot 1's hand-back timer (0.3 s at rest) sends ItemHandoff for a "held" item
            Assert.AreEqual(0, m_Rig.Host.Items.Owner(0), "the host takes a handoff from the owner even when it thought the item was held");
            Assert.IsFalse(m_Rig.Host.Items.IsHeld(0));
            Assert.AreEqual(releasedBefore, m_Rig.HostWorld.Calls[NetMsg.ItemReleased], "no release was ever sent");
            ItemReturn back = m_Rig.HostWorld.Returns[m_Rig.HostWorld.Returns.Count - 1];
            Assert.AreEqual(ItemReturnCause.Handoff, back.cause);
            Assert.AreEqual(1, back.fromSlot);
            Assert.AreEqual(1, a.World.Returned);
            AssertEveryoneAgrees(0, "after the handoff of a held item");
            Assert.IsTrue(b.World.Items[0].CanGrab);
        }

        [Test]
        public void AnItemAClientHoldsOnTheHostButDoesNotStream_IsTakenBackAfterThreeSeconds()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            StartRun();

            // a real holder streams the item and keeps it as long as it likes
            Assert.IsTrue(Grab(b, 2));
            m_Rig.Step(8.0);
            Assert.AreEqual(2, m_Rig.Host.Items.Owner(2));
            Assert.IsTrue(m_Rig.Host.Items.IsHeld(2));
            Assert.AreEqual(0, m_Rig.HostWorld.Returns.Count, "eight seconds in the hand: no backstop");

            // a client that holds the item as far as the host knows, but has itself forgotten (it neither
            // streams the item nor will ever release or hand it back)
            Assert.IsTrue(Grab(a, 4));
            m_Rig.Step(0.3);
            Assert.AreEqual(1, m_Rig.Host.Items.Owner(4));
            a.World.Items[4].Reset(a.Slot);
            m_Rig.Step(2.6);
            Assert.AreEqual(1, m_Rig.Host.Items.Owner(4), "still held on the host just before 3 s without a pose");
            m_Rig.Step(0.6);
            Assert.AreEqual(0, m_Rig.Host.Items.Owner(4), "taken back: it would otherwise stay 'held' until that client left");
            Assert.IsFalse(m_Rig.Host.Items.IsHeld(4));
            Assert.AreEqual(1, m_Rig.HostWorld.Returns.Count);
            Assert.AreEqual(ItemReturnCause.Backstop, m_Rig.HostWorld.Returns[0].cause);
            Assert.AreEqual(4, m_Rig.HostWorld.Returns[0].itemId);
            m_Rig.Step(0.3);
            AssertEveryoneAgrees(4, "after the held-item backstop");
            Assert.IsTrue(b.World.Items[4].CanGrab);
            Assert.AreEqual(2, m_Rig.Host.Items.Owner(2), "the real holder is untouched");
        }

        [Test]
        public void AQuickGrabReleaseAndHandBack_InsideOneRoundTrip_IsAcceptedByTheHost()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            StartRun();
            m_Rig.Hub.Latency = 0.25; // half a second round trip: nothing comes back while the player acts
            Assert.IsTrue(Grab(a, 2));
            m_Rig.Step(0.05);
            Assert.IsTrue(Release(a, 2));
            m_Rig.Step(0.38); // the 0.3 s slow timer fires and the handoff is sent, all still unconfirmed
            Assert.IsTrue(a.World.Items[2].HandoffSent);
            Assert.IsTrue(a.World.Items[2].Pending);
            Assert.AreEqual(0, a.World.Granted);
            m_Rig.Step(1.5);
            Assert.AreEqual(1, m_Rig.HostWorld.GrantedItems.Count, "the host processed grab, release and handoff in order");
            Assert.AreEqual(1, m_Rig.HostWorld.Calls[NetMsg.ItemReleased]);
            Assert.AreEqual(1, m_Rig.HostWorld.Returns.Count);
            Assert.AreEqual(ItemReturnCause.Handoff, m_Rig.HostWorld.Returns[0].cause);
            Assert.AreEqual(0, m_Rig.Host.Items.Owner(2));
            Assert.AreEqual(2, m_Rig.Host.Items.Epoch(2));
            Assert.AreEqual(1, a.World.Granted);
            Assert.AreEqual(1, a.World.Returned);
            Assert.AreEqual(0, a.World.Denied + a.World.Revoked);
            AssertEveryoneAgrees(2, "after the quick toss");
        }

        [Test]
        public void ItemPoses_AreCappedAt12PerDatagram_WithRelayedPosesFirst()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            StartRun(30);
            Grab(a, 0);
            m_Rig.Step(0.3);
            int largest = 0, batches = 0, batchesWithA = 0;
            b.Session.Tap = (outbound, peer, type, buffer, offset, length, time) =>
            {
                if (outbound || type != NetMsg.ItemPoses) return;
                batches++;
                int count = buffer[offset];
                if (count > largest) largest = count;
                if (count > 0 && buffer[offset + 1] == 0) batchesWithA++; // first entry is item 0, the one a client carries
            };
            m_Rig.EachStep = now =>
            {
                DriveItems(now);
                if (m_Rig.Hub.Now % 0.05 < 0.01)
                    for (byte i = 1; i < 30; i++) m_Rig.Host.QueueItemPose(i, V(i, 0, 0), NetQuat.Identity, false, false); // 29 moving host items
            };
            m_Rig.Step(1.0);
            Assert.Greater(batches, 15);
            Assert.AreEqual(12, largest, "never more than 12 poses in one datagram");
            Assert.AreEqual(batches, batchesWithA, "the carried item is relayed first, ahead of the host's own");
            Assert.Less(b.Session.Stats.bytesReceived / 1.6, 6000, "inside the planned 6 kB/s down per client");
        }

        [Test]
        public void DamageReports_AreOnlyTakenFromWhoeverSimulatesTheItem()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            StartRun();
            m_Rig.HostWorld.AutoWorld = true; // the scripted host accepts any report that reaches it
            Grab(a, 2);
            m_Rig.Step(0.4);
            b.Session.SendItemDamageReport(2, 5); // not b's item
            b.Session.SendItemDamageReport(4, 5); // nobody's item: the host simulates it
            b.Session.SendItemDamageReport(99, 5);
            m_Rig.Step(0.4);
            Assert.AreEqual(0, m_Rig.HostWorld.Calls[NetMsg.ItemDamageReport]);
            Assert.AreEqual(120, a.World.ItemValues[2]);
            a.Session.SendItemDamageReport(2, 80);
            m_Rig.Step(0.4);
            Assert.AreEqual(1, m_Rig.HostWorld.Calls[NetMsg.ItemDamageReport]);
            Assert.AreEqual(1, m_Rig.HostWorld.LastSlot);
            Assert.AreEqual(80, a.World.ItemValues[2], "the accepted value comes back to everyone as ItemValue");
            Assert.AreEqual(80, b.World.ItemValues[2]);
        }

        [Test]
        public void ANewRun_ResetsEveryItemToHostOwnedAtEpochZero()
        {
            NetTestRig.Member a = m_Rig.Join();
            StartRun(4);
            Grab(a, 1);
            m_Rig.Step(0.4);
            Assert.AreEqual(1, m_Rig.Host.Items.Owner(1));
            m_Rig.HostWorld.CurrentPhase = MissionPhase.Results;
            StartRun(7);
            Assert.AreEqual(7, m_Rig.Host.Items.Count);
            for (int i = 0; i < 7; i++)
            {
                Assert.AreEqual(0, m_Rig.Host.Items.Owner(i));
                Assert.AreEqual(0, m_Rig.Host.Items.Epoch(i));
                Assert.AreEqual(ItemMode.HostFree, a.World.Items[i].Mode);
                Assert.AreEqual(0, a.World.Items[i].Epoch);
            }
            Assert.AreEqual(2, a.World.Calls[NetMsg.RunStart]);
            Assert.AreEqual(0, m_Rig.HostWorld.Returns.Count, "the old run's items are simply gone");
        }

        [Test]
        public void WhenTheHostClosesTheSession_ItTakesOverEveryClientOwnedItem()
        {
            NetTestRig.Member a = m_Rig.Join();
            StartRun();
            Grab(a, 0);
            m_Rig.Step(0.4);
            m_Rig.Host.Close(m_Rig.Now);
            Assert.AreEqual(1, m_Rig.HostWorld.Returns.Count);
            Assert.AreEqual(ItemReturnCause.HostTookOver, m_Rig.HostWorld.Returns[0].cause);
            Assert.IsTrue(m_Rig.HostWorld.Returns[0].hasPose, "at its last pose, so the host's single-player run carries on");
            Assert.AreEqual(0, m_Rig.Host.Items.Owner(0));
            Assert.IsFalse(m_Rig.Host.LocalGrab(1), "a closed session arbitrates nothing");
        }
    }
}

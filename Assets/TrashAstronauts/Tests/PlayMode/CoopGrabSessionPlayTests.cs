using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using UnityEngine;
using UnityEngine.TestTools;
using static TrashAstronauts.Net.CoopConvert;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// "Everyone grabs" through the scene's real NetSession (no rig): the host's own simulated controller and
    /// bot clients (BotClientWorld's ItemAuthority, the plain-C# twin of ItemSync) share the run's salvage. First
    /// of all the owner's bug, both ways: whoever lets go, the other can grab it. In-memory network on the
    /// fixture's clock, paced to real time so XRI and physics see a normal frame rate.
    /// </summary>
    public class CoopGrabSessionPlayTests : NetPlayFixture
    {
        SimHands m_Hands;

        [TearDown]
        public void DisposeHands()
        {
            if (m_Hands != null) m_Hands.Dispose();
            m_Hands = null;
        }

        /// <summary>One frame: the fake clock and the hub advance by the real frame time.</summary>
        IEnumerator Frame()
        {
            yield return null;
            Tick(Math.Min(0.05, Math.Max(0.001, Time.unscaledDeltaTime)));
        }

        IEnumerator WaitFor(Func<bool> done, float seconds, string what)
        {
            float until = Time.unscaledTime + seconds;
            while (!done() && Time.unscaledTime < until) yield return Frame();
            Assert.IsTrue(done(), "Timed out waiting for: " + what);
        }

        IEnumerator FramesFor(float seconds)
        {
            float until = Time.unscaledTime + seconds;
            while (Time.unscaledTime < until) yield return Frame();
        }

        static void SetHead(Bot bot, Vector3 head) => bot.Avatar.headPosition = ToNet(head);

        Vector3 Spot => m_Player.HeadPosition + new Vector3(0.25f, -0.35f, 0.35f);

        SalvageItem[] Loose(int count) => m_Director.RunItems
            .Where(i => i != null && i.definition != null && !i.IsMounted && !i.definition.bonusSpawn && i.Body.mass <= 2f)
            .Take(count).ToArray();

        static void PlaceItem(SalvageItem item, Vector3 at)
        {
            if (!item.Body.isKinematic)
            {
                item.Body.linearVelocity = Vector3.zero;
                item.Body.angularVelocity = Vector3.zero;
            }
            item.transform.SetPositionAndRotation(at, Quaternion.identity);
            item.Body.position = at;
            item.Body.rotation = Quaternion.identity;
            Physics.SyncTransforms();
        }

        /// <summary>The scene hosts, the bots join, a contract runs everywhere, the host's player stands in the wreck.</summary>
        IEnumerator HostWithCrew(int count, Action<Bot[]> got)
        {
            Assert.IsTrue(m_Net.Host());
            var bots = new Bot[count];
            for (int i = 0; i < count; i++)
            {
                bots[i] = AddBot(m_Net.Transport.Local);
                SetHead(bots[i], m_Director.CrewSpot(i + 1) + Vector3.up * 1.6f);
            }
            yield return WaitFor(() => bots.All(b => b.Session.Welcomed) && Enumerable.Range(1, count).All(s => m_Net.Crew(s) != null), 5f, "the crew joined");
            Assert.IsTrue(m_Director.StartMission(m_Director.LocalPlayerId));
            yield return WaitFor(() => bots.All(b => b.World.Items.Length == m_Director.RunItems.Count && m_Director.RunItems.Count > 0), 5f, "RunStart on every bot");
            m_Director.keepPlayersInHub = false;
            m_Player.locomotion.TeleportTo(new Vector3(0f, 0.2f, 9.2f), Quaternion.identity);
            yield return Frame();
            yield return Frame();
            m_Player.locomotion.zeroG.Stop();
            yield return Frame();
            got(bots);
        }

        IEnumerator MoveHead(Bot bot, Vector3 head)
        {
            SetHead(bot, head);
            int slot = bot.Session.LocalSlot;
            yield return WaitFor(() => m_Net.Crew(slot) != null && Vector3.Distance(m_Net.Crew(slot).context.HeadPosition, head) < 0.05f, 2f, "the bot's head moved");
        }

        bool BotGrab(Bot bot, byte id)
        {
            if (!bot.World.Items[id].BeginGrab(out byte epoch)) return false;
            bot.Session.SendGrabRequest(id, epoch, 1);
            bot.Session.Flush(Now);
            return true;
        }

        void BotPose(Bot bot, byte id, Vector3 at)
        {
            var a = bot.World.Items[id];
            if (a.ShouldStream) bot.Session.QueueItemPose(id, ToNet(at), NetQuat.Identity, a.WireEpoch, a.HeldLocally);
        }

        bool BotRelease(Bot bot, byte id, Vector3 at)
        {
            var a = bot.World.Items[id];
            if (!a.Release(bot.World.Now)) return false;
            bot.Session.SendItemReleased(id, a.WireEpoch, ToNet(at), NetQuat.Identity, ToNet(at));
            bot.Session.Flush(Now);
            return true;
        }

        void BotThrown(Bot bot, byte id, Vector3 velocity)
        {
            bot.Session.SendItemThrown(id, bot.World.Items[id].WireEpoch, ToNet(velocity), default(NetVec3), velocity.magnitude, velocity.magnitude > 0.8f);
            bot.Session.Flush(Now);
        }

        bool BotHandoff(Bot bot, byte id, Vector3 at)
        {
            var a = bot.World.Items[id];
            if (!a.Tick(bot.World.Now + ItemAuthority.HandbackMaxSeconds + 1.0, 0f, out byte epoch)) return false;
            bot.Session.SendItemHandoff(id, epoch, ToNet(at), NetQuat.Identity, default(NetVec3), default(NetVec3));
            bot.Session.Flush(Now);
            return true;
        }

        IEnumerator BotTakes(Bot bot, SalvageItem item)
        {
            byte id = (byte)item.NetIndex;
            yield return MoveHead(bot, item.transform.position + Vector3.up * 0.4f);
            Assert.IsTrue(BotGrab(bot, id), "the bot's hand closes on it");
            yield return WaitFor(() => item.RemoteHolder == new PlayerId(bot.Session.LocalSlot) && !bot.World.Items[id].Pending, 2f, "the grant");
        }

        IEnumerator BotCarries(Bot bot, byte id, Vector3 from, Vector3 to, float seconds)
        {
            float t0 = Time.unscaledTime;
            while (Time.unscaledTime - t0 < seconds)
            {
                Vector3 at = Vector3.Lerp(from, to, (Time.unscaledTime - t0) / seconds);
                BotPose(bot, id, at);
                SetHead(bot, at + Vector3.up * 0.4f);
                yield return Frame();
            }
            float hold = Time.unscaledTime + 0.4f;
            while (Time.unscaledTime < hold)
            {
                BotPose(bot, id, to);
                yield return Frame();
            }
        }

        // ================================================================== the owner's bug, both ways

        [UnityTest]
        public IEnumerator OwnersBug_TheHostLetsGoAndABotGrabsAndCarriesIt_ThenTheBotLetsGoAndTheHostsControllerGrabsIt()
        {
            Bot[] bots = null;
            yield return HostWithCrew(1, b => bots = b);
            var bot = bots[0];
            m_Hands = new SimHands(m_Player);
            var item = Loose(1)[0];
            byte id = (byte)item.NetIndex;
            Assert.IsFalse(item.HostOnly, "nothing is HOST ONLY any more");
            Vector3 at = Spot;
            yield return SimHands.Pose(m_Hands.Right, at);
            PlaceItem(item, at);
            yield return SimHands.Settle();

            // The host holds it: the bot sees it in the host's hand.
            yield return SimHands.Grip(m_Hands.Right, true);
            Assert.IsTrue(item.grab.isSelected, "the host picks it up");
            yield return WaitFor(() => bot.World.Items[id].Mode == ItemMode.RemoteHeld && bot.World.Items[id].OwnerSlot == 0, 2f, "the bot sees it in the host's hand");
            Assert.IsFalse(bot.World.Items[id].CanGrab);

            // The host lets go: the bot sees it free, grabs it (granted, not refused) and carries it; the host sees it move.
            yield return SimHands.Grip(m_Hands.Right, false);
            Assert.IsFalse(item.IsHeld);
            yield return WaitFor(() => bot.World.Items[id].Mode == ItemMode.HostFree, 2f, "the bot sees it free");
            yield return SimHands.Pose(m_Hands.Right, at + new Vector3(-0.5f, 0.3f, 0f));
            item.Body.linearVelocity = Vector3.zero;
            Vector3 taken = item.transform.position;
            yield return BotTakes(bot, item);
            Assert.AreEqual(ItemAuthorityEvent.Granted, bot.World.ItemLastEvent[id], "THE BUG: the bot is granted the item the host dropped");
            Assert.AreEqual(0, bot.World.Denied);
            Vector3 carried = taken + new Vector3(0.8f, 0.2f, 0.4f);
            yield return BotCarries(bot, id, taken, carried, 1f);
            Assert.Less(Vector3.Distance(item.transform.position, carried), 0.1f, "the host sees it in the bot's hand: " + item.transform.position + " vs " + carried);
            Assert.IsTrue(item.IsHeld);

            // The bot lets go and hands it back; the host's controller grabs it.
            Assert.IsTrue(BotRelease(bot, id, carried));
            Assert.IsTrue(BotHandoff(bot, id, carried));
            yield return WaitFor(() => item.SimulatedHere && !item.IsHeld, 2f, "back with the host");
            Assert.IsTrue(item.grab.enabled);
            yield return SimHands.Pose(m_Hands.Right, carried + Vector3.down * 0.3f);
            PlaceItem(item, carried + Vector3.down * 0.3f);
            yield return SimHands.Settle();
            yield return SimHands.Grip(m_Hands.Right, true);
            Assert.IsTrue(item.grab.isSelected, "THE BUG, the other way: the host grabs what the bot dropped");
            yield return WaitFor(() => bot.World.Items[id].Mode == ItemMode.RemoteHeld && bot.World.Items[id].OwnerSlot == 0, 2f, "the bot sees it in the host's hand again");
            yield return SimHands.Grip(m_Hands.Right, false);
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator TwoBotsGrabTheSameItemInTheSameTick_OneGrant_OneDenial()
        {
            Bot[] bots = null;
            yield return HostWithCrew(2, b => bots = b);
            var item = Loose(1)[0];
            byte id = (byte)item.NetIndex;
            yield return MoveHead(bots[0], item.transform.position + Vector3.up * 0.4f);
            yield return MoveHead(bots[1], item.transform.position + Vector3.up * 0.3f);
            // Both requests go out before the host polls again: they arrive in the same poll.
            Assert.IsTrue(BotGrab(bots[0], id));
            Assert.IsTrue(BotGrab(bots[1], id));
            yield return WaitFor(() => !bots[0].World.Items[id].Pending && !bots[1].World.Items[id].Pending, 2f, "both answers");
            int granted = bots.Count(b => b.World.Items[id].Mode == ItemMode.Mine);
            Assert.AreEqual(1, granted, "exactly one winner");
            var loser = bots.First(b => b.World.Items[id].Mode != ItemMode.Mine);
            Assert.AreEqual(1, loser.World.Denied);
            Assert.AreEqual(ItemMode.RemoteHeld, loser.World.Items[id].Mode, "the loser sees it in the winner's hand");
            Assert.IsFalse(loser.World.Items[id].HeldLocally);
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator ABotThrowsIntoTheLockerFromRange_TheHostScoresTheTrickShot_AndEveryoneGetsItemDeposited()
        {
            Bot[] bots = null;
            yield return HostWithCrew(2, b => bots = b);
            var bot = bots[0];
            var item = Loose(1)[0];
            byte id = (byte)item.NetIndex;
            Transform zone = m_Director.locker.zone;
            float range = m_Director.Config.trickShotDistance + 1.5f;
            Vector3 from = zone.position + zone.forward * range + Vector3.up * 0.2f;
            PlaceItem(item, from);
            yield return Frame();
            yield return BotTakes(bot, item);
            for (int i = 0; i < 5; i++) { BotPose(bot, id, from); yield return Frame(); }
            Vector3 velocity = (zone.position - from).normalized * 4f;
            Assert.IsTrue(BotRelease(bot, id, from));
            BotThrown(bot, id, velocity);
            yield return BotCarries(bot, id, from, zone.position, Vector3.Distance(from, zone.position) / velocity.magnitude);
            yield return WaitFor(() => item.IsSecured, 3f, "the host's locker to take it");
            Assert.AreEqual(1, m_Director.Session.TrickShots, "thrown in from " + range + " m: a trick shot");
            Assert.Greater(m_Director.Session.TrickBonus, 0);
            Assert.IsTrue(m_Director.Session.TryGetSalvage(item.SalvageId, out var record));
            Assert.AreEqual(new PlayerId(bot.Session.LocalSlot), record.DepositedBy);
            yield return WaitFor(() => bots.All(b => b.World.ItemDepositedBy[id] == bot.Session.LocalSlot), 2f, "ItemDeposited on every bot");
            Assert.IsTrue(bots.All(b => b.World.Items[id].Secured));
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator ABotThatGoesAwayOrLeavesHoldingAnItem_ItReturnsToTheHostAtItsLastPose_AndCanBeGrabbed()
        {
            Bot[] bots = null;
            yield return HostWithCrew(2, b => bots = b);
            var items = Loose(2);

            // Away: the headset goes silent while holding it.
            var item = items[0];
            byte id = (byte)item.NetIndex;
            Vector3 from = item.transform.position;
            yield return BotTakes(bots[0], item);
            Vector3 last = from + new Vector3(0.3f, 0.2f, 0f);
            yield return BotCarries(bots[0], id, from, last, 0.4f);
            bots[0].Frozen = true;
            yield return WaitFor(() => item.SimulatedHere && !item.IsHeld, 5f, "the host takes it back from an Away holder");
            Assert.Less(Vector3.Distance(item.transform.position, last), 0.15f, "at its last pose");
            Assert.IsTrue(item.grab.enabled, "and grabbable again");
            Assert.IsFalse(item.RemoteHolder.IsValid);
            yield return BotTakes(bots[1], item);
            Assert.AreEqual(ItemAuthorityEvent.Granted, bots[1].World.ItemLastEvent[id], "another crew member can take it");
            Assert.IsTrue(BotRelease(bots[1], id, item.transform.position));
            Assert.IsTrue(BotHandoff(bots[1], id, item.transform.position));
            yield return WaitFor(() => item.SimulatedHere, 2f, "handed back");

            // Leaving: a crew member says goodbye with an item in hand.
            var second = items[1];
            byte id2 = (byte)second.NetIndex;
            yield return BotTakes(bots[1], second);
            Vector3 where = second.transform.position + new Vector3(0f, 0.25f, 0.2f);
            yield return BotCarries(bots[1], id2, second.transform.position, where, 0.4f);
            bots[1].Session.Leave(Now);
            bots[1].Frozen = true;
            yield return WaitFor(() => second.SimulatedHere && !second.IsHeld, 3f, "the host takes it back from a crew member who left");
            Assert.Less(Vector3.Distance(second.transform.position, where), 0.15f);
            Assert.IsTrue(second.grab.enabled);
            m_Net.Leave();
        }
    }

    /// <summary>A whole contract over real UDP on 127.0.0.1 in which a bot client, not the host, collects and deposits.</summary>
    public class CoopGrabSessionUdpPlayTests : NetPlayFixture
    {
        protected override bool UseMemoryTransport => false;

        [UnityTest]
        public IEnumerator Udp_TheBotCollectsAndDepositsTheQuota_ThenExtracts_BothPaid()
        {
            Assert.IsTrue(m_Net.Host());
            var bot = AddBot(m_Net.Transport.Local);
            Vector3 aboard = m_Director.CrewSpot(1) + Vector3.up * 1.6f;
            bot.Avatar.headPosition = ToNet(aboard);
            yield return Until(() => bot.Session.Welcomed && m_Net.Crew(1) != null && m_Net.Crew(1).HasPose, maxFrames: 100000, maxRealSeconds: 8f);
            bot.Session.SendStartRequest();
            yield return Until(() => m_Director.Phase == MissionPhase.Active && bot.World.Items.Length == m_Director.RunItems.Count && m_Director.RunItems.Count > 0, maxFrames: 100000, maxRealSeconds: 8f);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);

            var zone = m_Director.locker.zone;
            foreach (var item in m_Director.RunItems.Where(i => i != null && !i.IsMounted && !i.definition.bonusSpawn).OrderByDescending(i => i.Value).ToList())
            {
                if (m_Director.Session.QuotaMet) break;
                byte id = (byte)item.NetIndex;
                // The bot floats to it, takes it, carries it to the locker and lets go in the hopper.
                bot.Avatar.headPosition = ToNet(item.transform.position + Vector3.up * 0.4f);
                yield return Until(() => Vector3.Distance(m_Net.Crew(1).context.HeadPosition, item.transform.position + Vector3.up * 0.4f) < 0.05f, maxFrames: 100000, maxRealSeconds: 3f);
                Assert.IsTrue(bot.World.Items[id].BeginGrab(out byte epoch));
                bot.Session.SendGrabRequest(id, epoch, 1);
                bot.Session.Flush(Now);
                yield return Until(() => !bot.World.Items[id].Pending, maxFrames: 100000, maxRealSeconds: 3f);
                Assert.AreEqual(ItemMode.Mine, bot.World.Items[id].Mode, item.SalvageId + " granted to the bot");
                Vector3 start = item.transform.position;
                float t0 = Time.unscaledTime;
                while (Time.unscaledTime - t0 < 0.6f)
                {
                    Vector3 at = Vector3.Lerp(start, zone.position, (Time.unscaledTime - t0) / 0.6f);
                    var a = bot.World.Items[id];
                    if (a.ShouldStream) bot.Session.QueueItemPose(id, ToNet(at), NetQuat.Identity, a.WireEpoch, a.HeldLocally);
                    bot.Avatar.headPosition = ToNet(at + Vector3.up * 0.4f);
                    Tick();
                    yield return null;
                }
                var auth = bot.World.Items[id];
                Assert.IsTrue(auth.Release(bot.World.Now));
                bot.Session.SendItemReleased(id, auth.WireEpoch, ToNet(zone.position), NetQuat.Identity, ToNet(zone.position));
                bot.Session.Flush(Now);
                float until = Time.unscaledTime + 3f;
                while (!item.IsSecured && Time.unscaledTime < until)
                {
                    var a = bot.World.Items[id];
                    if (a.ShouldStream) bot.Session.QueueItemPose(id, ToNet(zone.position), NetQuat.Identity, a.WireEpoch, false);
                    Tick();
                    yield return null;
                }
                Assert.IsTrue(item.IsSecured, item.SalvageId + " went into the host's locker from the bot's hand");
                Assert.IsTrue(m_Director.Session.TryGetSalvage(item.SalvageId, out var record));
                Assert.AreEqual(new PlayerId(1), record.DepositedBy, "the bot deposited it");
            }
            Assert.IsTrue(m_Director.Session.QuotaMet, "the bot met the quota");

            bot.Avatar.headPosition = ToNet(aboard);
            yield return Until(() => Vector3.Distance(m_Net.Crew(1).context.HeadPosition, aboard) < 0.05f, maxFrames: 100000, maxRealSeconds: 3f);
            yield return Until(() => m_Net.Crew(1).context.InSafeZone, maxFrames: 100000, maxRealSeconds: 2f);
            int credits = m_Director.Profile.Data.totalCredits;
            bot.Session.SendExtractRequest();
            yield return Until(() => bot.World.Calls[NetMsg.RunEnded] > 0, maxFrames: 100000, maxRealSeconds: 8f);
            var result = m_Director.Session.Result;
            Assert.AreEqual(MissionOutcome.ContractComplete, result.Outcome);
            Assert.AreEqual(credits + result.Payout, m_Director.Profile.Data.totalCredits, "the host is paid");
            Assert.AreEqual(result.Payout, bot.World.Result.payout, "the bot is paid the same");

            m_Net.Leave();
            bot.Session.Dispose();
            yield return Until(() => UdpTransport.LiveCount == 0, maxFrames: 100000, maxRealSeconds: (float)NetSession.HostLingerSeconds + 2f);
        }
    }
}

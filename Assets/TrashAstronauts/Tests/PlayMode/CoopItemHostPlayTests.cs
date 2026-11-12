using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using static TrashAstronauts.Net.CoopConvert;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Co-op stage 3, host half: the scene hosts a real NetSessionHost (its ItemOwnershipTable arbitrates every
    /// grab) wired to CoopHostRules and CoopItemHost exactly as the session adapter will be; crew members are
    /// the net core's bots on the other end of an in-memory network; the host player's own hand is a
    /// simulated controller through the real XRI interactor.
    /// </summary>
    public class CoopItemHostPlayTests
    {
        MissionDirector m_Director;
        PlayerContext m_Player;
        CoopHostRig m_Rig;
        SimHands m_Hands;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            MissionDirector.StorageOverride = new MemorySaveStorage();
            MissionDirector.SeedOverride = 44;
            MissionDirector.CreatureOverride = 0;
            MissionDirector.MapOverride = 0;
            MissionDirector.ConditionOverride = ContractCondition.Standard;
            BlackoutController.DamageOverride = 0f;
            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            yield return null;
            yield return null;
            m_Director = MissionDirector.Active;
            m_Player = m_Director.Players.First();
        }

        [TearDown]
        public void TearDown()
        {
            if (m_Hands != null) m_Hands.Dispose();
            m_Hands = null;
            if (m_Rig != null) m_Rig.Dispose();
            m_Rig = null;
            if (MissionDirector.Active != null)
            {
                MissionDirector.Active.Net = null;
                MissionDirector.Active.CoopRideAlong = false;
            }
            MissionDirector.StorageOverride = null;
            MissionDirector.SeedOverride = null;
            MissionDirector.CreatureOverride = null;
            MissionDirector.MapOverride = null;
            MissionDirector.ConditionOverride = null;
            BlackoutController.DamageOverride = null;
        }

        // ------------------------------------------------------------------ helpers

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        static IEnumerator Seconds(float seconds)
        {
            float until = Time.unscaledTime + seconds;
            while (Time.unscaledTime < until) yield return null;
        }

        static IEnumerator Until(Func<bool> done, float seconds, string what)
        {
            float until = Time.unscaledTime + seconds;
            while (!done() && Time.unscaledTime < until) yield return null;
            Assert.IsTrue(done(), "Timed out waiting for: " + what);
        }

        /// <summary>The host's session with <paramref name="bots"/> crew members and a contract running everywhere.</summary>
        IEnumerator HostWithCrew(int bots)
        {
            m_Rig = new CoopHostRig(m_Director);
            for (int i = 0; i < bots; i++) m_Rig.AddBot(m_Director.hubSpawn.position + Vector3.up * 1.6f);
            m_Rig.StartPump();
            Assert.IsTrue(m_Director.StartMission(m_Director.LocalPlayerId));
            yield return Until(() => m_Rig.Bots.All(b => b.World.Items.Length == m_Director.RunItems.Count), 3f, "RunStart on every bot");
            m_Director.keepPlayersInHub = false;
            m_Player.locomotion.TeleportTo(new Vector3(0f, 0.2f, 9.2f), Quaternion.identity);
            yield return Frames(3);
            m_Player.locomotion.zeroG.Stop();
            yield return Frames(2);
        }

        Vector3 Spot => m_Player.HeadPosition + new Vector3(0.25f, -0.35f, 0.35f);

        SalvageItem[] Loose(int count, bool light = true) => m_Director.RunItems
            .Where(i => i != null && i.definition != null && !i.IsMounted && !i.definition.bonusSpawn && (!light || i.Body.mass <= 2f))
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

        /// <summary>The bot takes the item (its head next to it) and the host grants it.</summary>
        IEnumerator BotTakes(CoopBot bot, SalvageItem item)
        {
            byte id = (byte)item.NetIndex;
            m_Rig.MoveHead(bot, item.transform.position + Vector3.up * 0.4f);
            Assert.IsTrue(m_Rig.BotGrab(bot, id), "The bot's hand closes on it.");
            yield return Until(() => item.RemoteHolder == new PlayerId(bot.Slot) && !bot.Item(id).Pending, 2f, "the grant to slot " + bot.Slot);
        }

        /// <summary>The bot carries the item from where it is to <paramref name="to"/> over <paramref name="seconds"/>, streaming every frame.</summary>
        IEnumerator BotCarries(CoopBot bot, byte id, Vector3 from, Vector3 to, float seconds)
        {
            float t0 = Time.unscaledTime;
            while (Time.unscaledTime - t0 < seconds)
            {
                Vector3 at = Vector3.Lerp(from, to, (Time.unscaledTime - t0) / seconds);
                m_Rig.BotPose(bot, id, at);
                m_Rig.MoveHead(bot, at + Vector3.up * 0.4f);
                yield return null;
            }
            // Held there a moment: the host draws it about a tenth of a second behind.
            float hold = Time.unscaledTime + 0.4f;
            while (Time.unscaledTime < hold)
            {
                m_Rig.BotPose(bot, id, to);
                yield return null;
            }
        }

        // ================================================================== tests

        [UnityTest]
        public IEnumerator Host_ACrewMemberHoldingAnItem_IsItsHolderHere_TheLockerBumpersAndRecoveryLeaveItAlone_AndLettingGoClearsIt()
        {
            yield return HostWithCrew(1);
            var bot = m_Rig.Bots[0];
            var item = Loose(1)[0];
            byte id = (byte)item.NetIndex;
            Vector3 start = item.transform.position;

            yield return BotTakes(bot, item);
            Assert.IsTrue(item.IsHeld, "In a crew member's hand it counts as held here.");
            Assert.AreEqual(new PlayerId(bot.Slot), item.RemoteHolder);
            Assert.IsTrue(item.Body.isKinematic, "It follows their hand, not this machine's physics.");
            Assert.IsFalse(item.SimulatedHere);
            Assert.AreEqual(ItemMode.Mine, bot.Item(id).Mode);
            Assert.AreEqual(1, m_Rig.Count("collected " + id + " by " + bot.Slot), "The first pickup is booked once, to them.");
            Assert.IsTrue(m_Director.Session.TryGetSalvage(item.SalvageId, out var record));
            Assert.AreEqual(new PlayerId(bot.Slot), record.CollectedBy);
            yield return Until(() => bot.World.ItemCollectedBy[id] == bot.Slot, 1f, "ItemCollected on the bot");

            // Carried into the hopper: the locker leaves an item in somebody's hand alone.
            Vector3 zone = m_Director.locker.zone.position;
            yield return BotCarries(bot, id, start, zone, 0.6f);
            Assert.Less(Vector3.Distance(item.transform.position, zone), 0.1f, "It moved with their hand into the hopper.");
            m_Director.locker.ScanZone();
            yield return Frames(5);
            Assert.IsFalse(item.IsSecured, "Held items are never taken by the locker.");
            Assert.AreEqual(0, m_Director.Session.DepositedCount);

            // Out of bounds in their hand: lost-item recovery leaves it alone.
            Vector3 far = zone;
            for (int d = 5; d < 300 && m_Director.bounds.Contains(far); d += 5) far = zone + Vector3.up * d;
            far += Vector3.up * 5f;
            Assert.IsFalse(m_Director.bounds.Contains(far));
            yield return BotCarries(bot, id, zone, far, 0.3f);
            yield return Seconds(1.3f);
            for (int i = 0; i < 5; i++) { m_Rig.BotPose(bot, id, far); yield return null; }
            Assert.Less(Vector3.Distance(item.transform.position, far), 0.5f, "Not recovered while someone holds it.");
            Assert.AreEqual(0, m_Rig.Count("recovered " + id));

            // Back, and let go without a throw: the holder is cleared, they still fly it.
            Vector3 drop = start + Vector3.up * 0.3f;
            yield return BotCarries(bot, id, far, drop, 0.3f);
            Assert.IsTrue(m_Rig.BotRelease(bot, id, drop, drop));
            yield return Until(() => !item.RemoteHolder.IsValid, 1f, "the release");
            Assert.IsFalse(item.IsHeld);
            Assert.IsFalse(item.SimulatedHere, "Still theirs to fly until they hand it back.");
            Assert.AreEqual(0, m_Rig.Count("thrown " + id));
            Assert.IsFalse(item.WasThrown);

            // The hand-back: the host simulates it from their pose.
            Assert.IsTrue(m_Rig.BotHandoff(bot, id, drop, Vector3.zero));
            yield return Until(() => item.SimulatedHere, 1f, "the handoff");
            Assert.AreEqual(1, m_Rig.Count("returned " + id + " Handoff"));
            Assert.IsFalse(item.Body.isKinematic);
            Assert.Less(Vector3.Distance(item.transform.position, drop), 0.05f);
            yield return Until(() => bot.Item(id).Mode == ItemMode.HostFree, 1f, "the bot sees it back with the host");
            Assert.AreEqual(1, m_Rig.Count("collected " + id), "Still collected once.");
        }

        [UnityTest]
        public IEnumerator OwnersBug_TheHostHoldsAndLetsGo_ACrewMemberThenGrabsAndCarriesIt_TheHostSeesItMove_AndTheReverse()
        {
            yield return HostWithCrew(1);
            var bot = m_Rig.Bots[0];
            m_Hands = new SimHands(m_Player);
            var item = Loose(1)[0];
            byte id = (byte)item.NetIndex;
            Vector3 at = Spot;
            yield return SimHands.Pose(m_Hands.Right, at);
            PlaceItem(item, at);
            yield return SimHands.Settle();

            // A (the host) holds it: through the same table, so the crew sees the host's hand on it.
            yield return SimHands.Grip(m_Hands.Right, true);
            Assert.IsTrue(item.grab.isSelected, "The host picks it up.");
            Assert.AreEqual(1, m_Rig.Count("localgrab " + id + " True"));
            yield return Until(() => bot.Item(id).Mode == ItemMode.RemoteHeld && bot.Item(id).OwnerSlot == 0, 1f, "the bot sees it in the host's hand");
            Assert.IsFalse(bot.Item(id).CanGrab, "Nobody takes it out of the host's hand.");

            // A lets go.
            yield return SimHands.Grip(m_Hands.Right, false);
            Assert.IsFalse(item.IsHeld);
            Assert.AreEqual(1, m_Rig.Count("localrelease " + id));
            yield return Until(() => bot.Item(id).Mode == ItemMode.HostFree, 1f, "the bot sees it free");
            yield return SimHands.Pose(m_Hands.Right, at + new Vector3(-0.5f, 0.3f, 0f));
            item.Body.linearVelocity = Vector3.zero;

            // B (the crew member) can now grab it...
            Vector3 taken = item.transform.position;
            yield return BotTakes(bot, item);
            Assert.AreEqual(ItemAuthorityEvent.Granted, bot.World.ItemLastEvent[id], "Granted, not refused.");
            Assert.AreEqual(0, bot.World.Denied);
            // ...and carry it: the host sees it move in B's hand.
            Vector3 carried = taken + new Vector3(0.8f, 0.2f, 0.4f);
            yield return BotCarries(bot, id, taken, carried, 1f);
            Assert.Less(Vector3.Distance(item.transform.position, carried), 0.1f, "The host sees it where B's hand took it: " + item.transform.position + " vs " + carried);
            Assert.IsTrue(item.IsHeld);

            // The host cannot take it out of B's hand.
            yield return SimHands.Pose(m_Hands.Right, item.transform.position);
            yield return SimHands.Grip(m_Hands.Right, true);
            Assert.IsFalse(item.grab.isSelected, "Taking it out of someone's hand is not possible.");
            yield return SimHands.Grip(m_Hands.Right, false);

            // The reverse: B lets go and hands it back; A grabs it and carries it; B sees it move in A's hand.
            Assert.IsTrue(m_Rig.BotRelease(bot, id, carried, carried));
            Assert.IsTrue(m_Rig.BotHandoff(bot, id, carried, Vector3.zero));
            yield return Until(() => item.SimulatedHere && !item.IsHeld, 1f, "back with the host");
            // The hand reaches it (hand first: a hand that jumps there would shove it aside).
            yield return SimHands.Pose(m_Hands.Right, carried + Vector3.down * 0.3f);
            PlaceItem(item, carried + Vector3.down * 0.3f);
            yield return SimHands.Settle();
            yield return SimHands.Grip(m_Hands.Right, true);
            Assert.IsTrue(item.grab.isSelected, "A grabs it again.");
            yield return Until(() => bot.Item(id).Mode == ItemMode.RemoteHeld, 1f, "B sees it in A's hand");
            Vector3 seenBefore = bot.SeenAt(id);
            Vector3 hand = m_Hands.Right.at;
            for (int i = 0; i < 40; i++)
            {
                hand += new Vector3(0f, 0.01f, -0.01f);
                SimHands.Place(m_Hands.Right, hand);
                yield return null;
            }
            yield return Seconds(0.4f);
            Assert.Greater(Vector3.Distance(bot.SeenAt(id), seenBefore), 0.3f, "B sees it move.");
            Assert.Less(Vector3.Distance(bot.SeenAt(id), item.transform.position), 0.1f, "B sees it where A's hand has it.");
            yield return SimHands.Grip(m_Hands.Right, false);
        }

        [UnityTest]
        public IEnumerator TwoGrabsInTheSameInstant_HaveExactlyOneWinner_TheHostsOwnHandWinsATie_AndTheLoserHoldsNothing()
        {
            yield return HostWithCrew(2);
            var a = m_Rig.Bots[0];
            var b = m_Rig.Bots[1];
            var items = Loose(2);
            byte id = (byte)items[0].NetIndex;

            // Two crew members, one item, the same instant (both requests arrive in one step).
            m_Rig.MoveHead(a, items[0].transform.position);
            m_Rig.MoveHead(b, items[0].transform.position);
            m_Rig.Paused = true;
            Assert.IsTrue(m_Rig.BotGrab(a, id));
            Assert.IsTrue(m_Rig.BotGrab(b, id));
            m_Rig.Paused = false;
            yield return Until(() => !a.Item(id).Pending && !b.Item(id).Pending, 2f, "both answers");
            Assert.AreEqual(1, m_Rig.Count("granted " + id), "Exactly one winner.");
            var winner = a.Item(id).Mode == ItemMode.Mine ? a : b;
            var loser = winner == a ? b : a;
            Assert.AreEqual(ItemMode.Mine, winner.Item(id).Mode);
            Assert.AreEqual(ItemMode.RemoteHeld, loser.Item(id).Mode, "The loser sees it in the winner's hand.");
            Assert.IsFalse(loser.Item(id).HeldLocally, "The loser's hand is empty.");
            Assert.AreEqual(1, loser.World.Denied);
            Assert.IsFalse(loser.Item(id).Release(m_Rig.Now), "Nothing to release, so no throw either.");
            Assert.AreEqual(new PlayerId(winner.Slot), items[0].RemoteHolder);
            Assert.AreEqual(0, m_Rig.Count("thrown"));

            // The host's own hand and a crew member's request at once: the host's grab is in the table first.
            m_Hands = new SimHands(m_Player);
            var tie = items[1];
            byte tieId = (byte)tie.NetIndex;
            Vector3 at = Spot;
            yield return SimHands.Pose(m_Hands.Right, at);
            PlaceItem(tie, at);
            yield return SimHands.Settle();
            m_Rig.MoveHead(a, at);
            Assert.IsTrue(m_Rig.BotGrab(a, tieId), "Sent, still on its way.");
            m_Rig.Paused = true;
            yield return SimHands.Grip(m_Hands.Right, true);
            Assert.IsTrue(tie.grab.isSelected, "The host's hand closed on it.");
            m_Rig.Paused = false;
            yield return Until(() => !a.Item(tieId).Pending, 2f, "the crew member's answer");
            Assert.AreEqual(ItemMode.RemoteHeld, a.Item(tieId).Mode, "The crew member lost: it is in the host's hand.");
            Assert.AreEqual(0, a.Item(tieId).OwnerSlot);
            Assert.IsTrue(tie.grab.isSelected, "The host keeps it.");
            Assert.IsFalse(tie.RemoteHolder.IsValid);
            Assert.IsTrue(tie.SimulatedHere);
            Assert.AreEqual(0, m_Rig.Count("granted " + tieId));
            yield return SimHands.Grip(m_Hands.Right, false);
        }

        [UnityTest]
        public IEnumerator ACrewMemberThrowsIntoTheLockerFromRange_TheHostScoresTheTrickShot_AndDepositsIt()
        {
            yield return HostWithCrew(1);
            var bot = m_Rig.Bots[0];
            var item = Loose(1)[0];
            byte id = (byte)item.NetIndex;
            Transform zone = m_Director.locker.zone;
            float range = m_Director.Config.trickShotDistance + 1.5f;
            Vector3 from = zone.position + zone.forward * range + Vector3.up * 0.2f;
            PlaceItem(item, from);
            yield return Frames(2);

            yield return BotTakes(bot, item);
            for (int i = 0; i < 5; i++) { m_Rig.BotPose(bot, id, from); yield return null; }
            Vector3 velocity = (zone.position - from).normalized * 4f;
            Assert.IsTrue(m_Rig.BotRelease(bot, id, from, from));
            m_Rig.BotThrown(bot, id, velocity);
            // Its flight on the thrower's machine, streamed to the host.
            float flight = Vector3.Distance(from, zone.position) / velocity.magnitude;
            yield return BotCarries(bot, id, from, zone.position, flight);
            yield return Until(() => item.IsSecured, 2f, "the host's locker to take it");

            Assert.AreEqual(1, m_Rig.Count("thrown " + id));
            Assert.AreEqual(1, m_Director.Session.TrickShots, "Thrown in from " + range + " m: a trick shot.");
            Assert.Greater(m_Director.Session.TrickBonus, 0);
            Assert.AreEqual(1, m_Rig.Count("deposited " + id + " by " + bot.Slot + " +" + m_Director.Session.TrickBonus));
            Assert.IsTrue(m_Director.Session.TryGetSalvage(item.SalvageId, out var record));
            Assert.AreEqual(SalvageState.Deposited, record.State);
            Assert.AreEqual(new PlayerId(bot.Slot), record.DepositedBy);
            Assert.AreEqual(0f, m_Player.locomotion.zeroG.Velocity.magnitude, 0.05f, "No recoil on the host: that stays with the thrower.");
            yield return Until(() => bot.World.ItemDepositedBy[id] == bot.Slot, 1f, "ItemDeposited on the bot");
            Assert.IsTrue(bot.Item(id).Secured);
        }

        [UnityTest]
        public IEnumerator AnItemInACrewMembersHand_HittingAHostItem_DamagesItOnce_AndDamageReportsAreClamped()
        {
            yield return HostWithCrew(1);
            var bot = m_Rig.Bots[0];
            var loose = m_Director.RunItems.Where(i => i != null && i.definition != null && !i.IsMounted && !i.definition.bonusSpawn).ToArray();
            var target = loose.First(i => i.definition.fragility > 0f);
            var club = loose.Where(i => i != target).OrderByDescending(i => i.Body.mass).First();
            byte targetId = (byte)target.NetIndex, clubId = (byte)club.NetIndex;

            // The target floats at rest in the open cargo bay, simulated here.
            Vector3 at = m_Player.HeadPosition + new Vector3(0f, -0.2f, 1.5f);
            PlaceItem(target, at);
            yield return Seconds(1.2f); // past the knock sensor's settle-in time
            target.Body.linearVelocity = Vector3.zero;
            target.Body.angularVelocity = Vector3.zero;
            int fullTarget = target.Value;
            int clubValue = club.Value;

            // The crew member swings the club through it at 4 m/s.
            Vector3 from = at + Vector3.left * 1.2f;
            PlaceItem(club, from + Vector3.up * 2f);
            yield return BotTakes(bot, club);
            for (int i = 0; i < 10; i++) { m_Rig.BotPose(bot, clubId, from); yield return null; }
            yield return Seconds(0.2f);
            float t0 = Time.unscaledTime;
            while (target.DamageCount == 0 && Time.unscaledTime - t0 < 1.5f)
            {
                m_Rig.BotPose(bot, clubId, from + Vector3.right * 4f * (Time.unscaledTime - t0));
                yield return null;
            }
            // One knock: take the target out of the way at once, so nothing else (a wall, the club again) hits it.
            Assert.AreEqual(1, target.DamageCount, "The knock from the crew member's hand damaged the host's item.");
            PlaceItem(target, at + Vector3.up * 6f);
            for (int i = 0; i < 6; i++) yield return new WaitForFixedUpdate();
            Assert.AreEqual(1, target.DamageCount, "The knock from the crew member's hand damages the host's item once.");
            Assert.Less(target.Value, fullTarget);
            Assert.IsTrue(m_Director.Session.TryGetSalvage(target.SalvageId, out var targetRecord));
            Assert.AreEqual(target.Value, targetRecord.Value, "Booked in the host's ledger.");
            Assert.AreEqual(1, m_Rig.Count("value " + targetId + " "), "...and told to the crew once.");
            Assert.AreEqual(0, club.DamageCount, "The club's own knock is its holder's to report, not counted here.");
            Assert.AreEqual(clubValue, club.Value);
            target.Body.linearVelocity = Vector3.zero;
            target.Body.angularVelocity = Vector3.zero;
            yield return Until(() => bot.World.ItemValues[targetId] == target.Value, 1f, "ItemValue on the bot");

            // The holder reports the club's damage: a value below a third of full is clamped to the floor...
            int floor = Mathf.CeilToInt(club.FullValue / 3f);
            bot.Session.SendItemDamageReport(clubId, 0);
            bot.Session.Flush(m_Rig.Now);
            yield return Until(() => club.Value == floor, 1f, "the clamped report");
            Assert.AreEqual(1, m_Rig.Items.DamageClamped);
            Assert.IsTrue(m_Director.Session.TryGetSalvage(club.SalvageId, out var clubRecord));
            Assert.AreEqual(floor, clubRecord.Value);
            Assert.AreEqual(1, m_Rig.Count("value " + clubId + " " + floor));
            // ...and one that would raise it again is refused.
            bot.Session.SendItemDamageReport(clubId, club.FullValue);
            bot.Session.Flush(m_Rig.Now);
            yield return Seconds(0.2f);
            Assert.AreEqual(floor, club.Value);
            Assert.AreEqual(1, m_Rig.Count("value " + clubId + " "));
            Assert.GreaterOrEqual(m_Rig.Items.DamageRefused, 1);
        }

        [UnityTest]
        public IEnumerator AnOwnedItemWithNoHandoff_OrWhoseOwnerGoesAway_IsTakenBackAtItsLastPose()
        {
            yield return HostWithCrew(2);
            var a = m_Rig.Bots[0];
            var b = m_Rig.Bots[1];
            var items = Loose(2);
            byte first = (byte)items[0].NetIndex, second = (byte)items[1].NetIndex;

            // A takes one, lets go, and never hands it back (its headset froze).
            yield return BotTakes(a, items[0]);
            Vector3 last = items[0].transform.position + new Vector3(0.3f, 0.2f, 0f);
            yield return BotCarries(a, first, items[0].transform.position, last, 0.3f);
            Assert.IsTrue(m_Rig.BotRelease(a, first, last, last));
            yield return Until(() => m_Rig.Count("returned " + first + " Backstop") == 1, 4.5f, "the 3 s backstop");
            Assert.IsTrue(items[0].SimulatedHere);
            Assert.IsFalse(items[0].IsHeld);
            Assert.IsFalse(items[0].Body.isKinematic);
            Assert.Less(Vector3.Distance(items[0].transform.position, last), 0.1f, "Taken back where it last was.");

            // B is holding the other when it goes Away: taken back at once, at its last pose.
            yield return BotTakes(b, items[1]);
            Vector3 lastB = items[1].transform.position + Vector3.up * 0.4f;
            yield return BotCarries(b, second, items[1].transform.position, lastB, 0.3f);
            b.Session.SetAway(true, m_Rig.Now);
            yield return Until(() => m_Rig.Count("returned " + second + " OwnerAway") == 1, 2f, "the Away take-back");
            Assert.IsFalse(items[1].IsHeld, "Out of the absent player's hand.");
            Assert.IsTrue(items[1].SimulatedHere);
            Assert.Less(Vector3.Distance(items[1].transform.position, lastB), 0.1f);
            // And an Away player cannot grab.
            yield return Until(() => b.Item(second).Mode != ItemMode.Mine, 1f, "B told it is the host's again");
            m_Rig.MoveHead(b, items[1].transform.position);
            Assert.IsTrue(m_Rig.BotGrab(b, second));
            yield return Until(() => !b.Item(second).Pending, 2f, "the answer");
            Assert.AreNotEqual(ItemMode.Mine, b.Item(second).Mode);
            Assert.IsFalse(items[1].IsHeld);
        }

        [UnityTest]
        public IEnumerator AThrowCanBeCaught_ByAnotherCrewMember_OrByTheHostsHand()
        {
            yield return HostWithCrew(2);
            var a = m_Rig.Bots[0];
            var b = m_Rig.Bots[1];
            var items = Loose(2);
            byte id = (byte)items[0].NetIndex;

            // A throws; B catches it in flight.
            yield return BotTakes(a, items[0]);
            Vector3 from = items[0].transform.position;
            Vector3 to = from + new Vector3(1.5f, 0f, 0f);
            Assert.IsTrue(m_Rig.BotRelease(a, id, from, from));
            m_Rig.BotThrown(a, id, Vector3.right * 3f);
            yield return BotCarries(a, id, from, Vector3.Lerp(from, to, 0.5f), 0.25f);
            Assert.AreEqual(ItemMode.HostFree, b.Item(id).Mode, "In A's free flight anyone may catch it.");
            m_Rig.MoveHead(b, items[0].transform.position);
            Assert.IsTrue(m_Rig.BotGrab(b, id));
            yield return Until(() => !b.Item(id).Pending, 2f, "B's answer");
            Assert.AreEqual(ItemMode.Mine, b.Item(id).Mode, "B caught it.");
            Assert.AreEqual(new PlayerId(b.Slot), items[0].RemoteHolder);
            yield return Until(() => a.World.ItemLastEvent[id] == ItemAuthorityEvent.Revoked, 1f, "A told it is no longer A's");
            Assert.AreNotEqual(ItemMode.Mine, a.Item(id).Mode);

            // A throws the other one to the host, whose hand catches it.
            m_Hands = new SimHands(m_Player);
            var ball = items[1];
            byte ballId = (byte)ball.NetIndex;
            Vector3 hand = Spot;
            yield return SimHands.Pose(m_Hands.Right, hand);
            Vector3 start = hand + new Vector3(0f, 0.2f, 1.2f);
            PlaceItem(ball, start);
            yield return Frames(2);
            yield return BotTakes(a, ball);
            Assert.IsTrue(m_Rig.BotRelease(a, ballId, start, start));
            m_Rig.BotThrown(a, ballId, (hand - start).normalized * 2f);
            // Its flight on A's machine ends in the host's palm (wherever the host's hand is now).
            Func<Vector3> palm = () => m_Hands.Right.hand.interactor.transform.position;
            float t0 = Time.unscaledTime;
            while (Time.unscaledTime - t0 < 0.9f)
            {
                m_Rig.BotPose(a, ballId, Vector3.Lerp(start, palm(), Mathf.Clamp01((Time.unscaledTime - t0) / 0.5f)));
                yield return null;
            }
            Assert.Less(Vector3.Distance(ball.transform.position, palm()), 0.05f, "It flew into the host's palm.");
            Assert.IsFalse(m_Hands.Right.hand.IsHolding);
            SimHands.SetGrip(m_Hands.Right, true);
            for (int i = 0; i < 6; i++)
            {
                m_Rig.BotPose(a, ballId, palm());
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            Assert.IsTrue(ball.grab.isSelected, "The host's hand caught it: ball at " + ball.transform.position.ToString("F2") + " hand at "
                + m_Hands.Right.hand.interactor.transform.position.ToString("F2") + " hovering " + m_Hands.Right.hand.interactor.interactablesHovered.Count
                + " holder " + ball.RemoteHolder + " kinematic " + ball.Body.isKinematic + " simulated here " + ball.SimulatedHere + " selectActive " + m_Hands.Right.hand.interactor.isSelectActive);
            Assert.IsTrue(ball.SimulatedHere);
            Assert.IsFalse(ball.RemoteHolder.IsValid);
            Assert.AreEqual(1, m_Rig.Count("localgrab " + ballId + " True"));
            yield return Until(() => a.Item(ballId).Mode == ItemMode.RemoteHeld && a.Item(ballId).OwnerSlot == 0, 1f, "A sees it in the host's hand");
            yield return SimHands.Grip(m_Hands.Right, false);
        }
    }
}

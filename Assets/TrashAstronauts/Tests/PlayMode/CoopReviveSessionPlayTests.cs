using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.UI;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Co-op revive over real sessions. Host half: the scene hosts, scene-less bots are the crew; one is caught, downed
    /// and revived by the other through its ReviveHold messages. Client half: the scene joins a scripted host and
    /// follows its PlayerDowned / ReviveProgress / PlayerRevived, and its own hand on a crew mate's beacon goes out as
    /// ReviveHold. Shared helpers for the UDP loopback contract below.
    /// </summary>
    public abstract class CoopReviveFixture : NetPlayFixture
    {
        protected static void SetHead(Bot bot, Vector3 head)
        {
            bot.Avatar.headPosition = new NetVec3(head.x, head.y, head.z);
            bot.Avatar.leftHandPosition = new NetVec3(head.x - 0.2f, head.y - 0.4f, head.z + 0.2f);
            bot.Avatar.rightHandPosition = new NetVec3(head.x + 0.2f, head.y - 0.4f, head.z + 0.2f);
        }

        protected Vector3 AboardHead(int slot) => m_Director.CrewSpot(slot) + Vector3.up * 1.6f;

        protected IEnumerator Paced(double dt = 0.02)
        {
            float until = Time.unscaledTime + (float)dt;
            Tick(dt);
            do { yield return null; } while (Time.unscaledTime < until);
        }

        protected IEnumerator PacedUntil(System.Func<bool> done, float seconds)
        {
            float until = Time.unscaledTime + seconds;
            while (!done() && Time.unscaledTime < until) yield return Paced();
        }

        protected IEnumerator HostWithTwoBots(System.Action<Bot, Bot> got)
        {
            Assert.IsTrue(m_Net.Host());
            var a = AddBot(m_Net.Transport.Local);
            SetHead(a, AboardHead(1));
            yield return Until(() => a.Session.Welcomed && m_Net.Crew(1) != null && m_Net.Crew(1).HasPose, maxRealSeconds: 8f);
            var b = AddBot(m_Net.Transport.Local);
            SetHead(b, AboardHead(2));
            yield return Until(() => b.Session.Welcomed && m_Net.Crew(2) != null && m_Net.Crew(2).HasPose, maxRealSeconds: 8f);
            Assert.IsTrue(a.Session.Welcomed && b.Session.Welcomed);
            got(a, b);
        }

        protected IEnumerator StartFromBot(Bot bot)
        {
            bot.Session.SendStartRequest();
            yield return Until(() => m_Director.Phase == MissionPhase.Active && bot.World.Calls[NetMsg.RunStart] > 0, maxRealSeconds: 8f);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.IsTrue(m_Director.CreatureAboard);
        }

        /// <summary>Puts bot 1's suit in the wreck and lets the spider catch it; returns once the jumpscare has begun.</summary>
        protected IEnumerator CatchBotOne(Bot victim, System.Action<EnemyDrone> got)
        {
            Vector3 feet = MapPoints.Freighter(0f, 0f, 19.6f);
            SetHead(victim, feet + Vector3.up * 1.6f);
            yield return Until(() => Vector3.Distance(m_Net.Crew(1).context.HeadPosition, feet + Vector3.up * 1.6f) < 0.05f, maxRealSeconds: 5f);
            yield return Frames(3);
            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            Physics.SyncTransforms();
            Vector3 head = m_Net.Crew(1).context.HeadPosition;
            creature.transform.position = new Vector3(head.x, creature.hoverHeight, head.z + 1.6f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.back);
            for (int i = 0; i < 300 && !creature.IsScaring && m_Director.Phase == MissionPhase.Active; i++) creature.Step(0.05f);
            Assert.IsTrue(creature.IsScaring, "The spider caught bot 1's suit.");
            got(creature);
        }

    }

    public class CoopReviveSessionPlayTests : CoopReviveFixture
    {
        // ================================================================== host half

        [UnityTest]
        public IEnumerator Host_ABotCaught_IsDownedForEveryone_TheOtherBotRevivesItThroughItsMessages_AndAllSeeTheRing()
        {
            Bot victim = null, rescuer = null;
            yield return HostWithTwoBots((a, b) => { victim = a; rescuer = b; });
            yield return StartFromBot(victim);
            EnemyDrone creature = null;
            yield return CatchBotOne(victim, c => creature = c);

            yield return PacedUntil(() => victim.World.Downed[1] && rescuer.World.Downed[1], creature.ScareTotalSeconds + 3f);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase, "The contract goes on.");
            Assert.IsTrue(m_Net.Crew(1).context.IsDowned);
            Assert.IsTrue(victim.World.Downed[1], "The victim's headset is told.");
            Assert.IsTrue(rescuer.World.Downed[1], "So is the rest of the crew.");
            Assert.Less(Distance(rescuer.World.DownedAt[1], m_Net.Crew(1).context.DownedPosition), 0.02f);
            Assert.AreEqual(0, victim.World.Calls[NetMsg.RunEnded]);
            Assert.IsTrue(m_Net.Crew(1).ShownDowned, "The host sees the suit down...");
            Assert.IsTrue(m_Net.Crew(1).beacon.Shown, "...with its beacon.");

            // The rescuer goes over and holds the beacon.
            Vector3 body = m_Net.Crew(1).context.DownedPosition;
            SetHead(rescuer, body + Vector3.right * 0.9f);
            yield return Until(() => Vector3.Distance(m_Net.Crew(2).context.HeadPosition, body + Vector3.right * 0.9f) < 0.05f, maxRealSeconds: 5f);
            rescuer.Session.SendReviveHold(1, true);
            float start = Time.unscaledTime;
            yield return PacedUntil(() => !m_Net.Crew(1).context.IsDowned, ReviveRules.ReviveSeconds + 3f);
            Assert.IsFalse(m_Net.Crew(1).context.IsDowned, "Revived by the second bot.");
            Assert.That(Time.unscaledTime - start, Is.InRange(ReviveRules.ReviveSeconds - 0.3f, ReviveRules.ReviveSeconds + 1.5f));
            yield return Until(() => !victim.World.Downed[1] && !rescuer.World.Downed[1], maxRealSeconds: 3f);
            Assert.AreEqual(2, victim.World.RevivedBy[1], "Everyone hears who did it.");
            Assert.Greater(victim.World.Calls[NetMsg.ReviveProgress], 3, "The downed player saw the ring fill.");
            Assert.Greater(rescuer.World.Calls[NetMsg.ReviveProgress], 3, "So did the rescuer.");
            Assert.IsFalse(m_Net.Crew(1).ShownDowned);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Host_ADownedBotThatLeaves_IsRemovedLikeAnyLeaver_AndTheContractGoesOn()
        {
            Bot victim = null, other = null;
            yield return HostWithTwoBots((a, b) => { victim = a; other = b; });
            yield return StartFromBot(victim);
            EnemyDrone creature = null;
            yield return CatchBotOne(victim, c => creature = c);
            yield return PacedUntil(() => m_Net.Crew(1).context.IsDowned, creature.ScareTotalSeconds + 3f);
            Assert.IsTrue(m_Net.Crew(1).context.IsDowned);
            victim.Session.Leave(Now);
            victim.Frozen = true;
            yield return Until(() => m_Net.Crew(1) == null && !other.World.Present[1], maxRealSeconds: 5f);
            Assert.IsNull(m_Net.Crew(1));
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.AreEqual(0, m_Director.MissingAboardMask(), "Nobody left to wait for (the other bot is aboard).");
            m_Net.Leave();
        }

        /// <summary>The host caught with bots watching: the host is downed, the crew stays in the contract and revives the host.</summary>
        [UnityTest]
        public IEnumerator Host_TheHostCaught_WithBotsWatching_IsDowned_TheBotsStayIn_AndOneRevivesTheHost()
        {
            Bot a = null, b = null;
            yield return HostWithTwoBots((x, y) => { a = x; b = y; });
            yield return StartFromBot(a);
            Assert.IsTrue(m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 19.6f)));
            yield return Frames(4);
            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            Physics.SyncTransforms();
            Vector3 head = m_Player.HeadPosition;
            creature.transform.position = new Vector3(head.x, creature.hoverHeight, head.z + 1.6f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.back);
            for (int i = 0; i < 300 && !creature.IsScaring && m_Director.Phase == MissionPhase.Active; i++) creature.Step(0.05f);
            Assert.IsTrue(creature.IsScaring, "The spider caught the host.");

            yield return PacedUntil(() => m_Player.IsDowned && a.World.Downed[0] && b.World.Downed[0], creature.ScareTotalSeconds + 3f);
            Assert.IsTrue(m_Player.IsDowned, "The host is downed, not the contract failed.");
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.Greater(a.World.Calls[NetMsg.CatchStarted], 0, "The crew saw the catch.");
            float waitUntil = Time.unscaledTime + EnemyDrone.RunEndWaitSeconds + 0.5f;
            yield return PacedUntil(() => Time.unscaledTime >= waitUntil, EnemyDrone.RunEndWaitSeconds + 1.5f);
            Assert.AreEqual(0, a.World.Calls[NetMsg.RunEnded] + b.World.Calls[NetMsg.RunEnded], "Nobody's contract ended.");
            Assert.IsTrue(a.Session.Welcomed && b.Session.Welcomed, "Both bots still in the crew.");

            Vector3 body = m_Player.DownedPosition;
            SetHead(a, body + Vector3.right * 0.9f);
            yield return Until(() => Vector3.Distance(m_Net.Crew(1).context.HeadPosition, body + Vector3.right * 0.9f) < 0.05f, maxRealSeconds: 5f);
            a.Session.SendReviveHold(0, true);
            yield return PacedUntil(() => !m_Player.IsDowned, ReviveRules.ReviveSeconds + 3f);
            Assert.IsFalse(m_Player.IsDowned, "Revived by the bot.");
            yield return Until(() => !a.World.Downed[0], maxRealSeconds: 3f);
            Assert.AreEqual(1, a.World.RevivedBy[0]);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            m_Net.Leave();
        }

        // ================================================================== client half

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

        [UnityTest]
        public IEnumerator Client_FollowsTheHostsDownsRingsAndRevives_AndItsHandOnABeaconGoesOutAsReviveHold()
        {
            var host = AddScriptedHost();
            host.World.AutoStart = true;
            host.World.NextRun = HostRunWithSpider(9);
            Assert.IsTrue(m_Net.Join(host.Endpoint));
            yield return Until(() => m_Net.Mode == LobbyMode.Joined && host.World.Joined[1]);
            var other = AddBot(host.Endpoint);
            SetHead(other, m_Player.HeadPosition + m_Player.head.forward * 0.9f);
            yield return Until(() => m_Net.Crew(2) != null && m_Net.Crew(2).HasPose);
            m_Director.StartMission(m_Director.LocalPlayerId);
            yield return Until(() => m_Director.Phase == MissionPhase.Active);
            var suit = m_Net.Crew(2);
            Assert.IsNotNull(suit);

            // Crew 3 is downed (as the host decided): their suit slumps here with its beacon.
            // As the host does it: CatchStarted (the jumpscare plays here on their suit), then PlayerDowned.
            Vector3 at = suit.context.HeadPosition;
            var creature = m_Director.ActiveEnemy;
            host.Session.SendCatchStarted(2);
            yield return Until(() => creature.IsScaring);
            host.Session.SendPlayerDowned(2, CoopConvert.ToNet(at));
            yield return PacedUntil(() => suit.context.IsDowned && !creature.IsScaring, creature.ScareTotalSeconds + 4f);
            yield return Frames(2);
            Assert.IsTrue(suit.context.IsDowned, "downed: scaring " + creature.IsScaring + ", holding " + creature.HoldingAfterScare
                + ", phase " + m_Director.Phase + ", mode " + m_Net.Mode + ", crew " + (m_Net.Crew(2) != null) + ", same suit " + (m_Net.Crew(2) == suit)
                + ", link losses " + creature.RunEndLinkLosses + ", downed msgs " + host.World.Calls[NetMsg.PlayerDowned]);
            // A bystander stays in the contract after the wait for a RunEnded would have run out.
            float waitUntil = Time.unscaledTime + EnemyDrone.RunEndWaitSeconds + 0.5f;
            yield return PacedUntil(() => Time.unscaledTime >= waitUntil, EnemyDrone.RunEndWaitSeconds + 1.5f);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase, "Watching a crew mate get downed does not end the contract here.");
            Assert.AreEqual(0, creature.RunEndLinkLosses);
            Assert.IsTrue(suit.ShownDowned);
            Assert.IsTrue(suit.beacon.Shown);

            // This player's gripping hand on the beacon: one ReviveHold(on), then one (off) when it lets go.
            foreach (var driver in m_Player.GetComponentsInChildren<TrackedPoseDriver>()) driver.enabled = false;
            var hand = m_Player.rightHand;
            hand.transform.position += suit.BeaconPosition - CoopRevive.Palm(hand);
            hand.GripOverride = 1f;
            yield return Until(() => host.World.Calls[NetMsg.ReviveHold] > 0);
            Assert.AreEqual(1, host.World.Calls[NetMsg.ReviveHold]);
            Assert.AreEqual(1, host.World.LastSlot, "From this player's slot.");
            Assert.AreEqual(2, host.World.LastReviveTarget);
            Assert.IsTrue(host.World.LastReviveOn);

            host.Session.SendReviveProgress(2, 0.5f, 1);
            yield return Until(() => suit.context.ReviveProgress > 0.4f);
            yield return Frames(1);
            Assert.AreEqual(suit.beacon.ring.Length / 2, suit.beacon.LitSegments, "The ring fills on the beacon.");

            hand.GripOverride = 0f;
            yield return Until(() => host.World.Calls[NetMsg.ReviveHold] > 1);
            Assert.IsFalse(host.World.LastReviveOn);

            host.Session.SendPlayerRevived(2, 1);
            yield return Until(() => !suit.context.IsDowned);
            yield return Frames(2);
            Assert.IsFalse(suit.ShownDowned);
            Assert.IsFalse(suit.beacon.Shown);

            // This player caught and downed: the jumpscare, then locked, then revived.
            host.Session.SendCatchStarted(1);
            yield return Until(() => creature.IsScaring);
            host.Session.SendPlayerDowned(1, CoopConvert.ToNet(m_Player.HeadPosition));
            yield return PacedUntil(() => m_Player.IsDowned && !creature.IsScaring, creature.ScareTotalSeconds + 4f);
            Assert.IsTrue(m_Player.IsDowned);
            Assert.IsFalse(m_Player.locomotion.zeroG.enabled);
            host.Session.SendPlayerRevived(1, 2);
            yield return Until(() => !m_Player.IsDowned);
            Assert.IsTrue(m_Player.locomotion.zeroG.enabled);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            m_Net.Leave();
            Assert.IsFalse(m_Player.IsDowned);
        }
    }

    public class CoopReviveGiveUpPlayTests : CoopReviveFixture
    {
        /// <summary>A downed player whose crew wandered off can GIVE UP from the wrist: a 1.5 s fingertip hold, then the normal LEAVE.</summary>
        [UnityTest]
        public IEnumerator Client_Downed_GivesUpFromTheWrist_WithAHeldFingertip_AndLeavesTheCrew()
        {
            var host = AddScriptedHost();
            host.World.AutoStart = true;
            m_Director.CoopRideAlong = true;
            m_Director.CoopCreatureEnabled = true;
            var run = m_Director.BuildRunParams();
            m_Director.CoopRideAlong = false;
            m_Director.CoopCreatureEnabled = false;
            run.runIndex = 9;
            run.planHash = run.ComputePlanHash();
            host.World.NextRun = run;
            Assert.IsTrue(m_Net.Join(host.Endpoint));
            yield return Until(() => m_Net.Mode == LobbyMode.Joined && host.World.Joined[1]);
            m_Director.StartMission(m_Director.LocalPlayerId);
            yield return Until(() => m_Director.Phase == MissionPhase.Active);

            host.Session.SendPlayerDowned(1, CoopConvert.ToNet(m_Player.HeadPosition));
            yield return Until(() => m_Player.IsDowned);
            Assert.IsTrue(m_Player.GetComponentsInChildren<HandButtonInteractor>().All(bi => !bi.enabled), "Every button is off...");
            var giveUp = m_Player.GetComponent<DownedGiveUp>();
            Assert.IsNotNull(giveUp, "...but GIVE UP is there.");
            var wrist = m_Player.GetComponentInChildren<WristDisplay>(true);
            wrist.Refresh();
            StringAssert.Contains("GIVE UP", wrist.crewText.text);

            foreach (var driver in m_Player.GetComponentsInChildren<TrackedPoseDriver>()) driver.enabled = false;
            Transform spot = giveUp.Spot;
            Assert.IsNotNull(spot);
            var hand = spot.IsChildOf(m_Player.rightHand.transform) ? m_Player.leftHand : m_Player.rightHand;
            Vector3 away = hand.transform.position;
            // A brush of the wrist: not long enough.
            hand.transform.position += spot.position - hand.fingertip.position;
            float until = Time.unscaledTime + 0.6f;
            while (Time.unscaledTime < until) { Tick(); yield return null; }
            Assert.Greater(giveUp.Progress, 0f);
            hand.transform.position = away;
            yield return Frames(2);
            Assert.AreEqual(0f, giveUp.Progress, "Letting go early resets it.");
            Assert.AreEqual(LobbyMode.Joined, m_Net.Mode);

            // Held for the full time: the normal LEAVE.
            hand.transform.position += spot.position - hand.fingertip.position;
            until = Time.unscaledTime + DownedGiveUp.HoldSeconds + 0.5f;
            while (Time.unscaledTime < until && giveUp.GaveUp == 0) { Tick(); yield return null; }
            Assert.AreEqual(1, giveUp.GaveUp);
            yield return Frames(3);
            Assert.AreNotEqual(LobbyMode.Joined, m_Net.Mode, "Left the crew.");
            Assert.AreNotEqual(MissionPhase.Active, m_Director.Phase, "Back to playing alone.");
            Assert.IsFalse(m_Player.IsDowned);
            Assert.IsTrue(m_Player.GetComponentsInChildren<HandButtonInteractor>().All(bi => bi.enabled), "Everything back on.");
            yield return Until(() => !host.World.Joined[1], maxRealSeconds: 3f);
            Assert.IsFalse(host.World.Joined[1], "The host heard the goodbye.");
        }
    }

    /// <summary>A whole contract over real UDP on 127.0.0.1: a crew member is caught and downed, revived by another, the crew extracts, everyone is paid.</summary>
    public class CoopReviveUdpPlayTests : CoopReviveFixture
    {
        protected override bool UseMemoryTransport => false;

        [UnityTest]
        public IEnumerator Udp_AWholeContract_WithADowningAndARevive_ThenExtraction_PaysEachTheirShare()
        {
            Bot victim = null, rescuer = null;
            yield return HostWithTwoBots((a, b) => { victim = a; rescuer = b; });
            yield return StartFromBot(victim);

            // Something in the locker first, so there is a payout.
            var locker = m_Director.locker;
            var item = m_Director.RunItems.Where(i => i != null && !i.IsMounted && !i.definition.bonusSpawn).OrderByDescending(i => i.Value).First();
            item.Body.linearVelocity = Vector3.zero;
            item.Body.angularVelocity = Vector3.zero;
            item.transform.position = locker.zone.position;
            item.Body.position = locker.zone.position;
            yield return Until(() => item.IsSecured, maxFrames: 100000, maxRealSeconds: 3f);
            Assert.IsTrue(item.IsSecured);

            EnemyDrone creature = null;
            yield return CatchBotOne(victim, c => creature = c);
            yield return Until(() => victim.World.Downed[1] && rescuer.World.Downed[1], maxFrames: 100000, maxRealSeconds: creature.ScareTotalSeconds + 4f);
            Assert.IsTrue(victim.World.Downed[1], "Downed, over UDP.");
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);

            Vector3 body = m_Net.Crew(1).context.DownedPosition;
            SetHead(rescuer, body + Vector3.left * 0.8f);
            yield return Until(() => Vector3.Distance(m_Net.Crew(2).context.HeadPosition, body + Vector3.left * 0.8f) < 0.05f, maxFrames: 100000, maxRealSeconds: 5f);
            rescuer.Session.SendReviveHold(1, true);
            yield return Until(() => !victim.World.Downed[1], maxFrames: 100000, maxRealSeconds: ReviveRules.ReviveSeconds + 4f);
            Assert.IsFalse(victim.World.Downed[1], "Revived, over UDP.");
            Assert.AreEqual(2, victim.World.RevivedBy[1]);
            rescuer.Session.SendReviveHold(1, false);

            // Everyone back aboard and out.
            SetHead(victim, AboardHead(1));
            SetHead(rescuer, AboardHead(2));
            yield return Until(() => m_Director.MissingAboardMask() == 0, maxFrames: 100000, maxRealSeconds: 5f);
            int credits = m_Director.Profile.Data.totalCredits;
            victim.Session.SendExtractRequest();
            yield return Until(() => victim.World.Calls[NetMsg.RunEnded] > 0 && rescuer.World.Calls[NetMsg.RunEnded] > 0, maxFrames: 100000, maxRealSeconds: 8f);
            var result = m_Director.Session.Result;
            Assert.IsNotNull(result);
            Assert.IsFalse(result.IsFailure);
            Assert.Greater(result.Payout, 0);
            Assert.AreEqual(credits + result.Payout, m_Director.Profile.Data.totalCredits, "The host, never down, is paid in full.");
            MissionResult told = victim.World.Result.ToResult();
            Assert.AreEqual(1, told.DownsOf(1), "The crew are told who was downed.");
            Assert.AreEqual(0, told.LeftBehindMask, "Nobody was left behind.");
            Assert.AreEqual(ReviveRules.PayoutFor(result.Payout, 1, false), told.PayoutFor(1), "The downed bot's share is 10 % less.");
            Assert.AreEqual(result.Payout, told.PayoutFor(2), "The rescuer's is full.");

            m_Net.Leave();
            victim.Session.Dispose();
            rescuer.Session.Dispose();
            yield return Until(() => UdpTransport.LiveCount == 0, maxFrames: 100000, maxRealSeconds: (float)NetSession.HostLingerSeconds + 2f);
            Assert.AreEqual(0, UdpTransport.LiveCount);
        }
    }
}

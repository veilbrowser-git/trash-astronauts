using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.UI;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Co-op stage 2 over a real session: the shared ride-along contract wired through NetSession. Host half: the
    /// scene hosts and a scene-less bot (BotClientWorld) asks for things over the in-memory transport; the real
    /// contract runs here. Client half: the scene joins a scripted host (ScriptedHostWorld) and follows what it
    /// sends. The session clock is the fixture's fake one, so 60 s of host silence takes a moment.
    /// </summary>
    public class CoopSessionPlayTests : NetPlayFixture
    {
        static Vector3 InTheWreck => MapPoints.Freighter(0f, 0f, 12f);

        Vector3 AboardHead(int slot) => m_Director.CrewSpot(slot) + Vector3.up * 1.6f;

        static void SetHead(Bot bot, Vector3 head)
        {
            bot.Avatar.headPosition = new NetVec3(head.x, head.y, head.z);
            bot.Avatar.leftHandPosition = new NetVec3(head.x - 0.2f, head.y - 0.4f, head.z + 0.2f);
            bot.Avatar.rightHandPosition = new NetVec3(head.x + 0.2f, head.y - 0.4f, head.z + 0.2f);
        }

        IEnumerator HostWithBot(System.Action<Bot> got)
        {
            Assert.IsTrue(m_Net.Host());
            var bot = AddBot(m_Net.Transport.Local);
            SetHead(bot, AboardHead(1));
            yield return Until(() => bot.Session.Welcomed && m_Net.Crew(1) != null && m_Net.Crew(1).HasPose);
            Assert.IsTrue(bot.Session.Welcomed);
            Assert.IsFalse(m_Director.ContractStartBlocked, "A contract can be started while hosting.");
            got(bot);
        }

        IEnumerator StartFromBot(Bot bot)
        {
            int runStarts = bot.World.Calls[NetMsg.RunStart];
            bot.Session.SendStartRequest();
            yield return Until(() => m_Director.Phase == MissionPhase.Active && bot.World.Calls[NetMsg.RunStart] > runStarts);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase, "The bot's START started the contract here.");
            Assert.AreEqual(runStarts + 1, bot.World.Calls[NetMsg.RunStart]);
        }

        /// <summary>One tick of the fake clock per 20 ms of real time, so time-based smoothing sees the stream at its real pace.</summary>
        IEnumerator Paced(double dt = 0.02)
        {
            float until = Time.unscaledTime + (float)dt;
            Tick(dt);
            do { yield return null; } while (Time.unscaledTime < until);
        }

        IEnumerator WaitOutButtonCooldown()
        {
            float until = Time.unscaledTime + 0.5f;
            while (Time.unscaledTime < until) { Tick(); yield return null; }
        }

        SalvageItem[] LooseRunItems() => m_Director.RunItems.Where(i => i != null && !i.IsSecured && !i.IsMounted && i.definition != null).ToArray();

        /// <summary>The run a host would send: built offline with the ride-along limits, under the host's own run number.</summary>
        RunStartParams HostRun(int runIndex)
        {
            m_Director.CoopRideAlong = true;
            var run = m_Director.BuildRunParams();
            m_Director.CoopRideAlong = false;
            run.runIndex = (ushort)runIndex;
            run.planHash = run.ComputePlanHash();
            return run;
        }

        // ================================================================== host half

        [UnityTest]
        public IEnumerator Host_TheBotsStartRequest_StartsTheRealContract_AndItHearsRunStartWorldStateClockAndItemPoses()
        {
            Bot bot = null;
            yield return HostWithBot(b => bot = b);
            Assert.IsTrue(m_Director.CoopRideAlong, "A hosting scene's contracts are ride-along contracts.");

            yield return StartFromBot(bot);
            var run = m_Director.CurrentRun;
            Assert.IsTrue(bot.World.RunHashValid);
            Assert.AreEqual(run.planHash, bot.World.Run.planHash, "The bot was told exactly the contract that started here.");
            Assert.AreEqual(run.runIndex, bot.World.Run.runIndex);
            Assert.AreEqual(run.items.Length, bot.World.Run.items.Length);
            Assert.AreNotEqual(RunStartParams.NoCreature, run.creatureIndex, "The shared spider is aboard a co-op contract.");
            Assert.IsTrue(m_Director.CreatureAboard);
            Assert.AreSame(m_Director.enemies[run.creatureIndex], m_Director.ActiveEnemy, "The species the host picked is the one aboard.");
            Assert.AreEqual(0u, run.wireArmedMask, "No webs in co-op yet.");
            Assert.AreEqual(run.creatureIndex, bot.World.Run.creatureIndex);

            // World state right after the run start.
            yield return Until(() => bot.World.Calls[NetMsg.WorldState] > 0);
            int runAt = bot.World.Order.IndexOf(NetMsg.RunStart);
            int worldAt = bot.World.Order.IndexOf(NetMsg.WorldState);
            Assert.GreaterOrEqual(runAt, 0);
            Assert.Greater(worldAt, runAt, "WorldState follows RunStart.");
            int hatch = m_Director.NetIds.Doors.IndexOf(m_Director.tugHatch.doorId);
            Assert.AreNotEqual(0, bot.World.DoorOpenMask & (1 << hatch), "The world state says the hatch is open.");

            // The clock and the shared tank, twice a second.
            int clocksBefore = bot.World.Calls[NetMsg.Clock];
            yield return Frames(60); // 1.2 s of session time
            int clocks = bot.World.Calls[NetMsg.Clock] - clocksBefore;
            Assert.That(clocks, Is.InRange(2, 3), "Clock at 2 Hz.");
            Assert.AreEqual(m_Director.Session.ElapsedSeconds, bot.World.ElapsedSeconds, 0.25f, "The mission time the host has (0.1 s steps).");
            Assert.AreEqual(m_Director.Session.Oxygen.Remaining, bot.World.OxygenRemainingSeconds, 1.5f, "The tank level the host has.");

            // Item poses: one resting item per tick even when nothing moves...
            Assert.Greater(bot.World.PosesAccepted, 10, "A resting item is refreshed on every send tick.");
            Assert.AreEqual(0, bot.World.PosesDropped);
            // ...and an item the host's physics moves is streamed where it is.
            var item = LooseRunItems().First(i => i.definition.category == SalvageCategory.SmallScrap);
            byte id = (byte)item.NetIndex;
            item.Body.linearVelocity = new Vector3(0.6f, 0f, 0f);
            Vector3 startAt = item.transform.position;
            float moveUntil = Time.unscaledTime + 3f;
            while (Time.unscaledTime < moveUntil && Vector3.Distance(item.transform.position, startAt) < 0.1f) yield return Paced();
            yield return Paced();
            yield return Paced();
            yield return Paced();
            Assert.GreaterOrEqual(Vector3.Distance(item.transform.position, startAt), 0.1f, "Test set-up: the item moved.");
            Assert.Less(Distance(bot.World.ItemPoses[id].position, item.transform.position), 0.1f, "The bot sees the moving item where the host has it.");

            // A late joiner is turned away while the contract runs.
            var late = AddBot(m_Net.Transport.Local);
            yield return Until(() => late.Session.State == ClientState.Ended);
            Assert.AreEqual(SessionEndReason.Refused, late.Session.EndReason);
            Assert.AreEqual(RefuseReason.ContractInProgress, late.Session.LastRefusal);
            Assert.IsNull(m_Net.Crew(2));
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Host_DoorRequestsAndPointCutReports_ChangeTheRealScene_AndAreBroadcastOnce()
        {
            Bot bot = null;
            yield return HostWithBot(b => bot = b);
            yield return StartFromBot(bot);
            yield return Frames(3);

            // A door the bot wants open opens here; asking twice is not a toggle.
            var door = m_Director.wreckDoors.First(d => d != null && !d.locked && !d.IsOpen && d.buttons != null && d.buttons.Length > 0);
            byte doorId = (byte)m_Director.NetIds.Doors.IndexOf(door.doorId);
            int doorStates = bot.World.Calls[NetMsg.DoorState];
            yield return MoveBotHead(bot, door.transform.position + Vector3.up * 1.2f);
            bot.Session.SendDoorRequest(doorId, true);
            yield return Until(() => door.IsOpen && (bot.World.DoorOpenMask & (1 << doorId)) != 0);
            Assert.IsTrue(door.IsOpen, "The real door opened.");
            bot.Session.SendDoorRequest(doorId, true);
            yield return Frames(30);
            Assert.IsTrue(door.IsOpen);
            Assert.AreEqual(doorStates + 1, bot.World.Calls[NetMsg.DoorState], "Broadcast once.");

            // Every weld of a panel reported (each twice): it is cut here, each weld told once, the panel opened once.
            var panel = m_Director.cuttables.First(c => c != null && !c.isTraining && !c.IsOpen && c.TotalCuts > 0);
            byte pid = (byte)m_Director.NetIds.Panels.IndexOf(panel.panelId);
            yield return MoveBotHead(bot, panel.transform.position);
            for (int k = 0; k < panel.TotalCuts; k++)
            {
                bot.Session.SendPointCutReport(pid, (byte)k);
                bot.Session.SendPointCutReport(pid, (byte)k);
                yield return Frames(25); // a weld takes a cutter far longer than this
            }
            yield return Until(() => panel.IsOpen && bot.World.Calls[NetMsg.PanelOpened] > 0);
            yield return Frames(10);
            Assert.IsTrue(panel.IsOpen, "The real panel came off.");
            Assert.IsTrue(m_Director.Session.IsPanelOpen(panel.panelId));
            Assert.AreEqual(panel.TotalCuts, bot.World.Calls[NetMsg.PointCut], "Each weld broadcast once.");
            Assert.AreEqual(1, bot.World.Calls[NetMsg.PanelOpened], "The panel's opening broadcast once.");
            Assert.AreNotEqual(0, bot.World.PanelOpenMask & (1 << pid));
            Assert.Greater(new Vector3(bot.World.LastPlateImpulse.x, bot.World.LastPlateImpulse.y, bot.World.LastPlateImpulse.z).magnitude, 0.3f,
                "The plate's push travels with it.");
            m_Net.Leave();
        }

        IEnumerator MoveBotHead(Bot bot, Vector3 head)
        {
            SetHead(bot, head);
            yield return Until(() => m_Net.Crew(1) != null && Vector3.Distance(m_Net.Crew(1).context.HeadPosition, head) < 0.05f);
        }

        [UnityTest]
        public IEnumerator Host_DoorAndWeldRequests_FromFarAway_OrTooFast_AreIgnored()
        {
            Bot bot = null;
            yield return HostWithBot(b => bot = b);
            yield return StartFromBot(bot);
            yield return Frames(3);
            var rules = m_Net.HostRules;
            var door = m_Director.wreckDoors.First(d => d != null && !d.locked && !d.IsOpen && d.buttons != null && d.buttons.Length > 0);
            byte doorId = (byte)m_Director.NetIds.Doors.IndexOf(door.doorId);
            var panel = m_Director.cuttables.First(c => c != null && !c.isTraining && !c.IsOpen && c.TotalCuts > 0);
            byte pid = (byte)m_Director.NetIds.Panels.IndexOf(panel.panelId);

            // From inside the tug (a modified client): no door opens, no weld is cut.
            Assert.Greater(Vector3.Distance(m_Net.Crew(1).context.HeadPosition, door.transform.position), CoopHostRules.DoorReach + 1f, "Test set-up: the bot is far away.");
            bot.Session.SendDoorRequest(doorId, true);
            for (int k = 0; k < panel.TotalCuts; k++) bot.Session.SendPointCutReport(pid, (byte)k);
            yield return Frames(30);
            Assert.IsFalse(door.IsOpen, "A door request from across the ship is ignored.");
            Assert.AreEqual(0, panel.CutCount, "Weld reports from across the ship are ignored.");
            Assert.AreEqual(0, bot.World.Calls[NetMsg.PointCut]);
            Assert.Greater(rules.TooFar, 0);

            // At the door but hammering it: only a few get through, so nobody's reliable stream floods.
            yield return MoveBotHead(bot, door.transform.position + Vector3.up * 1.2f);
            yield return Frames(50); // the bucket refills
            int doorStates = bot.World.Calls[NetMsg.DoorState];
            for (int i = 0; i < 40; i++) bot.Session.SendDoorRequest(doorId, i % 2 == 0);
            yield return Frames(30);
            int changes = bot.World.Calls[NetMsg.DoorState] - doorStates;
            Assert.Greater(changes, 0, "Honest requests still work.");
            Assert.LessOrEqual(changes, (int)CoopHostRules.RequestBurst + 3, "40 requests at once: at most a burst gets through.");
            Assert.Greater(rules.RateLimited, 0);
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Host_ExtractionIsRefusedWithTheMaskWhileTheBotIsOutside_AcceptedAboard_EveryonePaid_AndASecondContractStarts()
        {
            Bot bot = null;
            yield return HostWithBot(b => bot = b);
            var missionBoard = Object.FindAnyObjectByType<MissionBoard>();
            missionBoard.Refresh();
            Assert.AreEqual("CO-OP CONTRACT", missionBoard.title.text);
            StringAssert.Contains("ONE THING aboard for the whole crew", missionBoard.body.text);
            Assert.IsTrue(missionBoard.primaryButton.Interactable, "START works on the host's board.");

            yield return StartFromBot(bot);
            int firstRun = m_Director.Session.RunIndex;

            // The host puts something in its locker: the bot hears where it went.
            var scrap = LooseRunItems().OrderByDescending(i => i.Value).First(i => !i.definition.bonusSpawn);
            Assert.IsTrue(m_Director.locker.TryAccept(scrap));
            yield return Until(() => bot.World.Calls[NetMsg.ItemDeposited] > 0);
            Assert.AreEqual(scrap.Value, bot.World.ItemValues[scrap.NetIndex]);
            Assert.AreNotEqual(255, bot.World.ItemLockerSlot[scrap.NetIndex]);

            // The bot is out in the wreck: its request is refused, naming it (crew 2 = slot 1).
            SetHead(bot, InTheWreck + Vector3.up * 1.6f);
            yield return Until(() => Vector3.Distance(m_Net.Crew(1).context.HeadPosition, InTheWreck + Vector3.up * 1.6f) < 0.05f);
            bot.Session.SendExtractRequest();
            yield return Until(() => bot.World.Calls[NetMsg.ExtractRefused] > 0);
            Assert.AreEqual(1 << 1, bot.World.ExtractMissingMask);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);

            // The host's own EXTRACT says the same on its board.
            Assert.IsNull(m_Director.Extract(m_Director.LocalPlayerId));
            Assert.AreEqual(1 << 1, m_Director.LastExtractRefusedMask);
            yield return null;
            missionBoard.Refresh();
            StringAssert.Contains("NOT ABOARD: CREW 2", missionBoard.body.text);

            // Aboard: the bot's request ends the run, and everyone hears the payout.
            SetHead(bot, AboardHead(1));
            yield return Until(() => m_Net.Crew(1).context.InSafeZone && Vector3.Distance(m_Net.Crew(1).context.HeadPosition, AboardHead(1)) < 0.05f);
            int credits = m_Director.Profile.Data.totalCredits;
            bot.Session.SendExtractRequest();
            yield return Until(() => bot.World.Calls[NetMsg.RunEnded] > 0);
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            var result = m_Director.Session.Result;
            Assert.IsNotNull(result);
            Assert.Greater(result.Payout, 0);
            Assert.AreEqual(result.Payout, bot.World.Result.payout, "The crew is paid what the host was paid.");
            Assert.AreEqual(result.Outcome, bot.World.Result.outcome);
            Assert.AreEqual(credits + result.Payout, m_Director.Profile.Data.totalCredits, "The host's own save is paid once.");

            // A second contract straight from the results screen.
            yield return StartFromBot(bot);
            Assert.AreEqual(firstRun + 1, m_Director.Session.RunIndex);
            Assert.AreEqual(m_Director.Session.RunIndex, bot.World.Run.runIndex);

            // The host leaving mid-contract abandons it: nothing banked, solo again, the crew told.
            credits = m_Director.Profile.Data.totalCredits;
            m_Net.Leave();
            Assert.AreEqual(MissionPhase.Hub, m_Director.Phase);
            Assert.AreEqual(credits, m_Director.Profile.Data.totalCredits);
            Assert.IsFalse(m_Director.CoopRideAlong);
            Assert.IsNull(m_Director.Net);
            yield return Until(() => bot.World.Ended == SessionEndReason.HostClosed);
            Assert.AreEqual(SessionEndReason.HostClosed, bot.World.Ended);
        }

        [UnityTest]
        public IEnumerator Host_ItsHazardPause_IsSentToTheCrew()
        {
            Bot bot = null;
            yield return HostWithBot(b => bot = b);
            yield return StartFromBot(bot);
            m_Director.SetHazardPause(HazardPauseReason.Scripted, true);
            yield return Until(() => bot.World.HostPaused);
            Assert.IsTrue(bot.World.HostPaused);
            yield return Until(() => (bot.World.ClockFlagBits & ClockFlags.HazardsPaused) != 0);
            Assert.AreNotEqual(0, bot.World.ClockFlagBits & ClockFlags.HazardsPaused, "The clock carries it too.");
            m_Director.SetHazardPause(HazardPauseReason.Scripted, false);
            yield return Until(() => !bot.World.HostPaused);
            Assert.IsFalse(bot.World.HostPaused);
            m_Net.Leave();
        }

        // ================================================================== the shared spider, host half

        EnemyDrone WakeSpiderAt(Vector3 at)
        {
            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            creature.patrolRoute = new EnemyWaypoint[0];
            creature.transform.position = at;
            for (int i = 0; i < 4; i++) creature.Step(0.05f);
            return creature;
        }

        /// <summary>Real-time paced ticks until <paramref name="done"/> or <paramref name="seconds"/> of real time.</summary>
        IEnumerator PacedUntil(System.Func<bool> done, float seconds)
        {
            float until = Time.unscaledTime + seconds;
            while (!done() && Time.unscaledTime < until) yield return Paced();
        }

        [UnityTest]
        public IEnumerator Spider_TheBotSeesItsSamplesAndStateChanges_ItsNoiseDrawsIt_AndItsDartSedatesIt()
        {
            Bot bot = null;
            yield return HostWithBot(b => bot = b);
            yield return StartFromBot(bot);
            var creature = WakeSpiderAt(MapPoints.Freighter(0f, m_Director.ActiveEnemy.hoverHeight, 14f));
            Assert.IsFalse(creature.IsReplica);
            Assert.AreEqual(EnemyState.Patrol, creature.State);

            // The stream: 10 samples a second of where it is.
            int before = bot.World.Calls[NetMsg.CreatureState];
            yield return Frames(50); // 1 s of session time
            Assert.That(bot.World.Calls[NetMsg.CreatureState] - before, Is.InRange(8, 21));
            Assert.Less(Distance(bot.World.Creature.position, creature.transform.position), 0.1f);
            Assert.Greater(bot.World.Calls[NetMsg.CreatureStateChanged], 0, "Its waking was told.");
            Assert.AreEqual(EnemyState.Patrol, bot.World.CreatureEventState);

            // The bot's crash two metres from it: it goes to look, and knows who made it.
            Vector3 crash = MapPoints.Freighter(0f, 1.2f, 15.5f);
            bot.Session.SendNoise(CoopConvert.ToNet(crash), 1f, NoiseKind.Impact);
            yield return Until(() => creature.State == EnemyState.Investigate && bot.World.CreatureEventState == EnemyState.Investigate);
            Assert.AreEqual(EnemyState.Investigate, creature.State, "The bot's noise made the real spider investigate.");
            Assert.AreEqual(new PlayerId(1), creature.LastHeardNoiseBy);
            Assert.AreEqual(EnemyState.Investigate, bot.World.CreatureEventState);

            // The bot's dart, in range and on it: asleep, and everyone is told for how long.
            yield return null;
            Vector3 body = creature.Walker.surface.position;
            bot.Session.SendTranqHitReport(CoopConvert.ToNet(body + Vector3.back * 5f), CoopConvert.ToNet(body + Vector3.up * 0.3f));
            yield return Until(() => creature.IsSedated && bot.World.TranquilizedSeconds > 0f);
            Assert.IsTrue(creature.IsSedated, "The bot's dart sedated it.");
            Assert.AreEqual(creature.SedatedSecondsLeft, bot.World.TranquilizedSeconds, 0.5f);
            Assert.AreEqual(1, m_Net.HostRules.Creature.TranqAccepted);
            m_Net.Leave();
        }

        IEnumerator CatchBot(Bot victim, System.Action<EnemyDrone> got)
        {
            Vector3 feet = MapPoints.Freighter(0f, 0f, 19.6f);
            yield return MoveBotHead(victim, feet + Vector3.up * 1.6f);
            yield return Frames(3); // the director sees the suit out of the tug
            Assert.IsFalse(m_Net.Crew(1).context.InSafeZone);
            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            Physics.SyncTransforms();
            Vector3 head = m_Net.Crew(1).context.HeadPosition;
            creature.transform.position = new Vector3(head.x, creature.hoverHeight, head.z + 1.6f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.back);
            for (int i = 0; i < 300 && !creature.IsScaring && m_Director.Phase == MissionPhase.Active; i++)
            {
                creature.Step(0.05f);
            }
            Assert.IsTrue(creature.IsScaring, "The spider caught the bot's suit.");
            Assert.AreEqual(new PlayerId(1), creature.CatchVictim);
            got(creature);
        }

        [UnityTest]
        public IEnumerator Spider_CatchesTheBotsSuit_CatchStartedNamesIt_AndRunEndedReachesItAfterASilenceDuringTheScare()
        {
            Bot bot = null;
            yield return HostWithBot(b => bot = b);
            yield return StartFromBot(bot);
            EnemyDrone creature = null;
            yield return CatchBot(bot, c => creature = c);
            yield return Until(() => bot.World.CatchVictim != NetProtocol.NoSlot);
            Assert.AreEqual(1, bot.World.CatchVictim, "CatchStarted names the bot's slot.");
            Assert.AreEqual(1, bot.World.Calls[NetMsg.CatchStarted]);

            // The bot's headset goes quiet through the scare (a lost link, a sleeping headset).
            bot.Frozen = true;
            yield return PacedUntil(() => m_Director.Phase != MissionPhase.Active, creature.ScareTotalSeconds + 3f);
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase, "The host ended the run.");
            Assert.AreEqual(MissionOutcome.FailedCaught, m_Director.Session.Result.Outcome);
            Assert.IsNotNull(m_Net.Crew(1), "The bot's suit is still in the crew (its slot is held).");
            Assert.AreEqual(0, bot.World.Calls[NetMsg.RunEnded]);

            // It comes back: the reliable stream delivers the end of the run.
            bot.Frozen = false;
            yield return Until(() => bot.World.Calls[NetMsg.RunEnded] > 0);
            Assert.AreEqual(1, bot.World.Calls[NetMsg.RunEnded]);
            Assert.AreEqual(MissionOutcome.FailedCaught, bot.World.Result.outcome, "Being caught fails the contract for everyone.");
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Spider_ACaughtBotThatLeavesMidScare_KeepsItsSuitUntilTheScareEnds_AndTheRunStillEnds()
        {
            Bot bot = null;
            yield return HostWithBot(b => bot = b);
            var other = AddBot(m_Net.Transport.Local);
            SetHead(other, AboardHead(2));
            yield return Until(() => other.Session.Welcomed && m_Net.Crew(2) != null);
            yield return StartFromBot(bot);
            EnemyDrone creature = null;
            yield return CatchBot(bot, c => creature = c);
            var suit = m_Net.Crew(1);

            // The victim says goodbye the moment it is caught: the scare still plays against its suit.
            bot.Session.Leave(Now);
            bot.Frozen = true;
            yield return Until(() => other.World.Present[1] == false);
            Assert.IsFalse(other.World.Present[1], "The others are told it left.");
            Assert.IsTrue(creature.IsScaring);
            Assert.IsTrue(suit != null && suit.gameObject != null, "Its suit is kept while the jumpscare plays against it.");
            Assert.AreSame(suit, m_Net.Crew(1));
            Assert.AreEqual(2, m_Net.PlayerCount, "...though it is no longer in the crew.");

            yield return PacedUntil(() => !creature.IsScaring && m_Net.Crew(1) == null, creature.ScareTotalSeconds + 3f);
            Assert.IsFalse(creature.IsScaring);
            Assert.IsNull(m_Net.Crew(1), "Gone once the scare is over.");
            yield return PacedUntil(() => m_Director.Phase != MissionPhase.Active, 3f);
            Assert.AreEqual(MissionOutcome.FailedCaught, m_Director.Session.Result.Outcome);
            yield return Until(() => other.World.Calls[NetMsg.RunEnded] > 0);
            Assert.AreEqual(MissionOutcome.FailedCaught, other.World.Result.outcome);
            m_Net.Leave();
        }

        // ================================================================== client half

        IEnumerator JoinScripted(ScriptedHost host)
        {
            Assert.IsTrue(m_Net.Join(host.Endpoint));
            yield return Until(() => m_Net.Mode == LobbyMode.Joined && host.World.Joined[1]);
            Assert.AreEqual(LobbyMode.Joined, m_Net.Mode);
            Assert.IsFalse(m_Director.ContractStartBlocked, "Joined: the board's START asks the host.");
        }

        [UnityTest]
        public IEnumerator Client_StartOnTheBoard_AsksTheHost_RunStartBuildsTheSameContract_ItemsFollowTheStream_AndADepositLandsInTheHostsSlot()
        {
            var run = HostRun(7);
            var host = AddScriptedHost();
            host.World.AutoStart = true;
            host.World.NextRun = run;
            yield return JoinScripted(host);

            var missionBoard = Object.FindAnyObjectByType<MissionBoard>();
            missionBoard.Refresh();
            StringAssert.Contains("START asks the host", missionBoard.body.text);
            Assert.IsTrue(missionBoard.primaryButton.Press(null));
            yield return Until(() => m_Director.Phase == MissionPhase.Active);
            Assert.AreEqual(1, host.World.Calls[NetMsg.StartRequest], "The press went to the host as a StartRequest.");
            Assert.AreEqual(1, host.World.LastSlot);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.AreEqual(7, m_Director.Session.RunIndex);
            Assert.AreEqual(run.items.Length, m_Director.RunItems.Count);
            for (int i = 0; i < run.items.Length; i++)
            {
                Assert.AreEqual(m_Director.NetIds.SalvageIdFor(7, run.items[i]), m_Director.RunItems[i].SalvageId);
                Assert.IsFalse(m_Director.RunItems[i].HostOnly, "Everyone can grab salvage: nothing is HOST ONLY.");
                Assert.IsFalse(m_Director.RunItems[i].SimulatedHere);
            }
            Assert.IsFalse(m_Director.CreatureAboard);

            // The host moves an item and streams it; the copy here follows.
            var item = m_Director.RunItems.First(i => i != null && !i.IsMounted && i.definition.category == SalvageCategory.SmallScrap);
            byte id = (byte)item.NetIndex;
            Vector3 start = item.transform.position;
            Quaternion rotation = item.transform.rotation;
            Vector3 at = start;
            for (int i = 0; i < 40; i++)
            {
                at = start + Vector3.right * (i * 0.02f);
                host.Session.QueueItemPose(id, CoopConvert.ToNet(at), CoopConvert.ToNet(rotation), false, false);
                yield return Paced();
            }
            Assert.Greater(item.transform.position.x, start.x + 0.3f, "The copy moves with the stream.");
            Assert.Less(Vector3.Distance(item.transform.position, at), 0.4f);
            for (int i = 0; i < 10; i++)
            {
                host.Session.QueueItemPose(id, CoopConvert.ToNet(at), CoopConvert.ToNet(rotation), false, true);
                yield return Paced();
            }
            float settle = Time.unscaledTime + 1f;
            while (Time.unscaledTime < settle && Vector3.Distance(item.transform.position, at) > 0.01f) { Tick(); yield return null; }
            Assert.Less(Vector3.Distance(item.transform.position, at), 0.01f, "It ends where the last pose said.");
            Vector3 rest = at + new Vector3(0.2f, 0.1f, 0f);
            host.Session.SendItemRest(id, CoopConvert.ToNet(rest), CoopConvert.ToNet(rotation));
            settle = Time.unscaledTime + 1f;
            while (Time.unscaledTime < settle && Vector3.Distance(item.transform.position, rest) > 0.01f) { Tick(); yield return null; }
            Assert.Less(Vector3.Distance(item.transform.position, rest), 0.01f, "ItemRest puts it where it settled on the host.");
            Assert.IsTrue(item.Body.isKinematic, "Moved, never simulated, here.");

            // The host's locker took another item into its slot 3: it lands in slot 3 here too.
            var deposit = m_Director.RunItems.First(i => i != null && !i.IsMounted && i != item);
            host.Session.SendItemCollected((byte)deposit.NetIndex, 0);
            host.Session.SendItemDeposited((byte)deposit.NetIndex, 0, deposit.Value, 0, 3);
            yield return Until(() => deposit.IsSecured);
            Assert.IsTrue(deposit.IsSecured);
            Assert.AreSame(m_Director.locker.slots[3], deposit.transform.parent, "The host's slot, not this locker's next.");
            Assert.AreEqual(deposit.Value, m_Director.Session.DepositedValue);

            // EXTRACT on this board: the host refuses, naming crew 3; the board says so.
            host.World.AutoExtract = true;
            host.World.ExtractMissingMask = 1 << 2;
            yield return WaitOutButtonCooldown();
            Assert.IsTrue(missionBoard.primaryButton.Press(null));
            yield return WaitOutButtonCooldown();
            Assert.IsTrue(missionBoard.primaryButton.Press(null), "confirm");
            yield return Until(() => host.World.Calls[NetMsg.ExtractRequest] > 0 && m_Director.LastExtractRefusedMask != 0);
            Assert.AreEqual(1 << 2, m_Director.LastExtractRefusedMask);
            missionBoard.Refresh();
            StringAssert.Contains("NOT ABOARD: CREW 3", missionBoard.body.text);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase, "Only the host ends the run.");
            m_Net.Leave();
            Assert.AreEqual(MissionPhase.Hub, m_Director.Phase, "Leaving mid-contract abandons it here.");
        }

        [UnityTest]
        public IEnumerator Client_RunEndedPaysTheLocalSaveOnce_BringsTheRigHome_AndTheNextContractStarts()
        {
            var host = AddScriptedHost();
            host.World.AutoStart = true;
            host.World.NextRun = HostRun(3);
            yield return JoinScripted(host);
            Assert.IsFalse(m_Director.StartMission(m_Director.LocalPlayerId), "A client never starts a contract itself...");
            yield return Until(() => m_Director.Phase == MissionPhase.Active);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase, "...it asks, and the host's RunStart starts it.");
            var item = m_Director.RunItems.First(i => i != null && !i.IsMounted);
            host.Session.SendItemDeposited((byte)item.NetIndex, 0, item.Value, 0, 0);
            yield return Until(() => item.IsSecured);

            m_Player.locomotion.TeleportTo(InTheWreck);
            yield return Frames(4);
            Assert.IsFalse(m_Player.InSafeZone);
            int credits = m_Director.Profile.Data.totalCredits;
            var message = new RunEndedMsg
            {
                outcome = MissionOutcome.QuotaMissed, quota = m_Director.Session.Quota,
                depositedValue = item.Value, depositedCount = 1, payout = item.Value,
                oxygenRemainingSeconds = 100f, elapsedSeconds = 200f,
            };
            host.World.CurrentPhase = MissionPhase.Results;
            host.Session.SendRunEnded(in message);
            host.Session.SendRunEnded(in message); // a repeat changes nothing
            yield return Until(() => m_Director.Phase == MissionPhase.Results);
            yield return Frames(10);
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            Assert.AreEqual(credits + item.Value, m_Director.Profile.Data.totalCredits, "Paid once.");
            float until = Time.unscaledTime + 5f;
            while (Time.unscaledTime < until && !m_Player.InSafeZone) { Tick(); yield return null; }
            Assert.IsTrue(m_Player.InSafeZone, "The rig left in the wreck is brought home.");
            yield return Frames(5);
            Vector3 feet = m_Player.HeadPosition, spot = m_Director.CrewSpot(1);
            Assert.Less(Vector2.Distance(new Vector2(feet.x, feet.z), new Vector2(spot.x, spot.z)), 0.3f,
                "...onto its own crew spot, not onto the host's.");

            // The next contract from the results screen, asked for from here.
            host.World.NextRun = HostRun(4);
            Assert.IsFalse(m_Director.StartMission(m_Director.LocalPlayerId));
            yield return Until(() => m_Director.Phase == MissionPhase.Active);
            Assert.AreEqual(4, m_Director.Session.RunIndex);
            Assert.AreEqual(credits + item.Value, m_Director.Profile.Data.totalCredits);
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Client_LeavingOnTheResultsScreen_LeavesEveryItemAnOrdinaryGrabbableOne()
        {
            var host = AddScriptedHost();
            host.World.AutoStart = true;
            host.World.NextRun = HostRun(8);
            yield return JoinScripted(host);
            m_Director.StartMission(m_Director.LocalPlayerId);
            yield return Until(() => m_Director.Phase == MissionPhase.Active);
            var item = m_Director.RunItems.First(i => i != null && !i.IsMounted);
            host.Session.QueueItemPose((byte)item.NetIndex, CoopConvert.ToNet(item.transform.position), CoopConvert.ToNet(item.transform.rotation), false, false);
            yield return Frames(5);
            Assert.IsNotNull(item.GetComponent<CoopItemReplica>(), "Test set-up: items follow the host.");
            host.World.CurrentPhase = MissionPhase.Results;
            var message = new RunEndedMsg { outcome = MissionOutcome.QuotaMissed, quota = m_Director.Session.Quota };
            host.Session.SendRunEnded(in message);
            yield return Until(() => m_Director.Phase == MissionPhase.Results);

            m_Net.Leave();
            yield return null;
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase, "The results stay on the board.");
            foreach (var each in m_Director.RunItems)
            {
                if (each == null || each.IsSecured || each.IsMounted) continue;
                Assert.IsFalse(each.HostOnly, each.SalvageId);
                Assert.IsTrue(each.SimulatedHere, each.SalvageId);
                Assert.IsTrue(each.grab.enabled, each.SalvageId + " can be picked up again");
                Assert.IsFalse(each.Body.isKinematic, each.SalvageId);
                Assert.IsNull(each.GetComponent<CoopItemReplica>(), each.SalvageId);
            }
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local), "And the next solo contract starts.");
        }

        [UnityTest]
        public IEnumerator Client_APauseFromBeforeJoining_BecomesAway_AndAwayBecomesAPauseAgainOnLeaving_NothingStranded()
        {
            // Offline: this headset's hazards are paused (the test's stand-in for tracking lost; HazardPauseMonitor
            // clears TrackingLost every frame when no headset is bound, as in batch mode).
            m_Director.SetHazardPause(HazardPauseReason.Scripted, true);
            Assert.IsTrue(m_Director.HazardsPaused);
            var host = AddScriptedHost();
            yield return JoinScripted(host);

            // Joined: that is Away now, not a hazard pause (the host's pause is the only one in a session).
            Assert.IsFalse(m_Director.HazardsPaused, "The pause moved over to Away.");
            Assert.AreEqual(HazardPauseReason.Scripted, m_Director.AwayReasons);
            yield return Until(() => host.World.Away[1]);
            Assert.IsTrue(host.World.Away[1], "The host hears this player is away.");

            // Tracking comes back: in a session HazardPauseMonitor reports it through SetLocalAway.
            m_Director.SetLocalAway(HazardPauseReason.Scripted, false);
            yield return Until(() => !host.World.Away[1]);
            Assert.IsFalse(host.World.Away[1], "Back: nothing was stranded.");
            Assert.IsFalse(m_Net.LocalAway);

            // The app is suspended and tracking lost; the app resumes first: still away.
            m_Director.SetLocalAway(HazardPauseReason.Scripted, true);
            m_Net.SendMessage("OnApplicationPause", true);
            m_Net.SendMessage("OnApplicationPause", false);
            yield return Frames(10);
            Assert.IsTrue(m_Net.LocalAway, "Resuming the app while tracking is still lost is not back.");
            Assert.IsTrue(host.World.Away[1]);

            // Leaving while still untracked: single-player pauses for it again, and the "back" clears it.
            m_Net.Leave();
            Assert.IsTrue(m_Director.HazardsPaused, "Away became a hazard pause again.");
            Assert.AreEqual(HazardPauseReason.None, m_Director.AwayReasons);
            Assert.IsFalse(m_Player.IsAway);
            m_Director.SetHazardPause(HazardPauseReason.Scripted, false);
            Assert.IsFalse(m_Director.HazardsPaused, "Nothing stranded in single-player either.");
        }

        // ================================================================== the shared spider, client half

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

        IEnumerator JoinSpiderRun(ScriptedHost host, int runIndex, System.Func<IEnumerator> beforeStart = null)
        {
            host.World.AutoStart = true;
            host.World.NextRun = HostRunWithSpider(runIndex);
            Assert.AreNotEqual(RunStartParams.NoCreature, host.World.NextRun.creatureIndex);
            yield return JoinScripted(host);
            if (beforeStart != null) yield return beforeStart();
            m_Director.StartMission(m_Director.LocalPlayerId);
            yield return Until(() => m_Director.Phase == MissionPhase.Active);
            Assert.IsTrue(m_Director.CreatureAboard);
            Assert.IsTrue(m_Director.ActiveEnemy.IsReplica, "The spider here only shows the host's.");
        }

        static CreatureSnapshot SpiderAt(Vector3 at, EnemyState state, byte target = NetProtocol.NoSlot) => new CreatureSnapshot
        {
            position = CoopConvert.ToNet(at), yawDegrees = 90f, state = state, targetSlot = target, flags = CreatureFlags.Running,
        };

        int LocalPulses() => (m_Player.leftHand != null ? m_Player.leftHand.PulseCount : 0) + (m_Player.rightHand != null ? m_Player.rightHand.PulseCount : 0);

        [UnityTest]
        public IEnumerator Client_TheHostsSamplesMoveTheSpider_ItsStateCuesFireOnce_TheHuntedOneFeelsIt_AndOthersAreToldWho()
        {
            var host = AddScriptedHost();
            yield return JoinSpiderRun(host, 6);
            var creature = m_Director.ActiveEnemy;
            var wrist = Object.FindAnyObjectByType<WristDisplay>();

            // The host's samples move the replica.
            Vector3 at = MapPoints.Freighter(0f, creature.hoverHeight, 14f);
            for (int i = 0; i < 30; i++)
            {
                var sample = SpiderAt(at + Vector3.forward * (i * 0.02f), EnemyState.Patrol);
                host.Session.SetCreature(in sample);
                yield return Paced();
            }
            yield return PacedUntil(() => Vector3.Distance(creature.transform.position, at + Vector3.forward * 0.58f) < 0.05f, 1f);
            Assert.Less(Vector3.Distance(creature.transform.position, at + Vector3.forward * 0.58f), 0.05f, "It is where the host's spider is.");
            Assert.Greater(creature.SnapshotsApplied, 10);

            // A chase of crew 3: one cue here, and this suit does not buzz: it is not after this player.
            int cues = creature.StateCuesPresented, pulses = LocalPulses();
            host.Session.SendCreatureStateChanged(EnemyState.Chase, 2);
            yield return Until(() => creature.State == EnemyState.Chase);
            yield return Frames(10);
            Assert.AreEqual(cues + 1, creature.StateCuesPresented, "One message, one cue.");
            Assert.AreEqual(new PlayerId(2), creature.Quarry);
            Assert.AreEqual(pulses, LocalPulses(), "No hunted buzz for someone else's chase.");
            yield return PacedUntil(() => wrist.threatText.text.Contains("CREW 3"), 1.5f);
            StringAssert.Contains("DRONE HUNTING CREW 3", wrist.threatText.text);

            // Now it is after this player: the suit buzzes and the wrist says so.
            host.Session.SendCreatureStateChanged(EnemyState.Chase, 1);
            yield return Until(() => creature.Quarry == new PlayerId(1));
            yield return Frames(10);
            Assert.AreEqual(cues + 2, creature.StateCuesPresented);
            Assert.Greater(LocalPulses(), pulses, "The hunted player's suit buzzes.");
            yield return PacedUntil(() => wrist.threatText.text.Contains("HUNTING YOU"), 1.5f);
            StringAssert.Contains("HUNTING YOU", wrist.threatText.text);

            // The host's dart.
            host.Session.SendTranquilized(8f);
            yield return Until(() => creature.IsSedated);
            Assert.IsTrue(creature.IsSedated);
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Client_CaughtHere_PlaysTheLocalJumpscare_AndTheRunEndsOnlyOnTheHostsRunEnded()
        {
            var host = AddScriptedHost();
            yield return JoinSpiderRun(host, 7);
            var creature = m_Director.ActiveEnemy;
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 19.6f));
            yield return Frames(4);
            int credits = m_Director.Profile.Data.totalCredits;

            host.Session.SendCatchStarted(1);
            yield return Until(() => creature.IsScaring);
            Assert.AreEqual(new PlayerId(1), creature.CatchVictim);
            float caughtAt = Time.unscaledTime;
            yield return PacedUntil(() => creature.ScarePhase >= 2, 3f);
            yield return Paced(0.4);
            Assert.Greater(m_Player.visor.Stage, 0, "This player's own jumpscare: the visor cracks.");
            yield return PacedUntil(() => !creature.IsScaring, 8f);
            Assert.That(Time.unscaledTime - caughtAt, Is.InRange(creature.ScareTotalSeconds - 0.1f, creature.ScareTotalSeconds + 0.6f), "Today's timing.");
            yield return Paced(0.5);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase, "Only the host ends the run.");

            host.World.CurrentPhase = MissionPhase.Results;
            var message = new RunEndedMsg { outcome = MissionOutcome.FailedCaught, quota = m_Director.Session.Quota, elapsedSeconds = 30f, oxygenRemainingSeconds = 100f };
            host.Session.SendRunEnded(in message);
            yield return Until(() => m_Director.Phase == MissionPhase.Results);
            Assert.AreEqual(MissionOutcome.FailedCaught, m_Director.Session.Result.Outcome);
            Assert.AreEqual(credits, m_Director.Profile.Data.totalCredits, "Caught: nothing banked.");
            Assert.AreEqual(0, creature.RunEndLinkLosses);
            yield return PacedUntil(() => m_Player.InSafeZone && m_Player.fader.Alpha <= 0f, 4f);
            Assert.IsTrue(m_Player.InSafeZone, "Home.");
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Client_SomeoneElseCaught_PlaysAgainstTheirSuit_WithNoVisorCrackHere()
        {
            var host = AddScriptedHost();
            Bot other = null;
            IEnumerator OtherJoins()
            {
                // Another crew member joins before the contract (nobody can join during one).
                other = AddBot(host.Endpoint);
                SetHead(other, MapPoints.Freighter(0f, 1.6f, 19.6f));
                yield return Until(() => m_Net.Crew(2) != null && m_Net.Crew(2).HasPose);
            }
            yield return JoinSpiderRun(host, 8, OtherJoins);
            Assert.IsNotNull(m_Net.Crew(2));
            var creature = m_Director.ActiveEnemy;
            int pulses = LocalPulses();

            host.Session.SendCatchStarted(2);
            yield return Until(() => creature.IsScaring);
            Assert.AreEqual(new PlayerId(2), creature.CatchVictim);
            yield return PacedUntil(() => creature.ScarePhase >= 2, 3f);
            yield return Paced(0.4);
            Assert.Less(Vector3.Distance(creature.transform.position, m_Net.Crew(2).context.HeadPosition), 0.8f, "On the other player's suit.");
            while (creature.IsScaring)
            {
                Assert.AreEqual(0, m_Player.visor.Stage, "No visor crack here.");
                Assert.AreEqual(0f, m_Player.fader.Alpha);
                Assert.AreEqual(pulses, LocalPulses());
                yield return Paced();
            }
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Client_HostPauseAndSilence_AreShown_And60sOfSilenceAbortsWithNothingBanked()
        {
            var host = AddScriptedHost();
            host.World.AutoStart = true;
            host.World.NextRun = HostRun(5);
            yield return JoinScripted(host);
            m_Director.StartMission(m_Director.LocalPlayerId);
            yield return Until(() => m_Director.Phase == MissionPhase.Active);
            var item = m_Director.RunItems.First(i => i != null && !i.IsMounted);
            host.Session.SendItemDeposited((byte)item.NetIndex, 0, item.Value, 0, 0);
            yield return Until(() => item.IsSecured);
            int credits = m_Director.Profile.Data.totalCredits;
            var missionBoard = Object.FindAnyObjectByType<MissionBoard>();
            var crewBoard = Object.FindAnyObjectByType<NetLobbyBoard>();

            // HOST PAUSED: the host's hazard pause reaches here; it is not this headset going away.
            host.Session.SendHostPause(true);
            yield return Until(() => m_Director.HostPaused);
            Assert.IsTrue(m_Director.HazardsPaused);
            Assert.IsFalse(m_Net.LocalAway, "The host's pause does not make this player away.");
            missionBoard.Refresh();
            StringAssert.Contains("HOST PAUSED", missionBoard.body.text);
            host.Session.SendHostPause(false);
            yield return Until(() => !m_Director.HostPaused);
            missionBoard.Refresh();
            StringAssert.DoesNotContain("HOST PAUSED", missionBoard.body.text);

            // The host goes silent: after 1.5 s the boards say so; the contract waits.
            host.Frozen = true;
            yield return Until(() => m_Director.HostSilent, dt: 0.1);
            Assert.IsTrue(m_Director.HostSilent);
            Assert.IsTrue(m_Net.HostSilent);
            missionBoard.Refresh();
            StringAssert.Contains("HOST NOT RESPONDING", missionBoard.body.text);
            crewBoard.Refresh();
            StringAssert.Contains("HOST NOT RESPONDING", crewBoard.status.text);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            float oxygen = m_Director.Session.Oxygen.Remaining;
            yield return Frames(30, 0.1);
            Assert.AreEqual(oxygen, m_Director.Session.Oxygen.Remaining, "The local tank does not run on while the host is silent.");

            // 60 s of silence (the fake clock, not real waiting): back to solo, nothing banked.
            yield return Until(() => m_Net.Mode == LobbyMode.Offline, maxFrames: 400, dt: 0.5);
            Assert.AreEqual(LobbyMode.Offline, m_Net.Mode);
            Assert.AreEqual(LobbyNotice.LinkLost, m_Net.Notice);
            Assert.AreEqual(MissionPhase.Hub, m_Director.Phase, "The contract was abandoned.");
            Assert.IsNull(m_Director.Session.Result);
            Assert.AreEqual(credits, m_Director.Profile.Data.totalCredits, "Nothing banked.");
            Assert.IsNull(m_Director.Net);
            Assert.IsFalse(m_Director.HostSilent);
            Assert.IsFalse(m_Director.HazardsPaused);
            Assert.IsFalse(m_Player.IsAway);
            Assert.AreEqual(PlayerId.Local, m_Player.Id);
            Assert.IsTrue(m_Net.IsOffline);
            yield return Frames(3);
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local), "Solo contracts start as before.");
            Assert.IsTrue(m_Director.CreatureAboard);
        }
    }

    /// <summary>A whole shared contract once over real UDP sockets on 127.0.0.1 with the real clock.</summary>
    public class CoopSessionUdpPlayTests : NetPlayFixture
    {
        protected override bool UseMemoryTransport => false;

        [UnityTest]
        public IEnumerator Udp_TheSceneHostsAWholeContract_ABotRidesAlong_SalvageGoesIntoTheLocker_Extract_BothPaid()
        {
            Assert.IsTrue(m_Net.Host());
            var bot = AddBot(m_Net.Transport.Local);
            Vector3 aboard = m_Director.CrewSpot(1) + Vector3.up * 1.6f;
            bot.Avatar.headPosition = new NetVec3(aboard.x, aboard.y, aboard.z);
            yield return Until(() => bot.Session.Welcomed && m_Net.Crew(1) != null && m_Net.Crew(1).HasPose, maxFrames: 100000, maxRealSeconds: 8f);
            Assert.IsTrue(bot.Session.Welcomed);

            bot.Session.SendStartRequest();
            yield return Until(() => m_Director.Phase == MissionPhase.Active && bot.World.Calls[NetMsg.RunStart] > 0, maxFrames: 100000, maxRealSeconds: 8f);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.IsTrue(bot.World.RunHashValid);
            Assert.IsTrue(m_Director.CreatureAboard, "The spider rides with the crew.");
            var spider = m_Director.ActiveEnemy;
            yield return Until(() => bot.World.Calls[NetMsg.CreatureState] > 0, maxFrames: 100000, maxRealSeconds: 5f);
            int samplesAtStart = bot.World.Calls[NetMsg.CreatureState];
            Assert.Greater(samplesAtStart, 0, "The bot sees the spider from the start.");

            // Salvage dropped into the host's locker hopper until the quota is met.
            var locker = m_Director.locker;
            foreach (var item in m_Director.RunItems.Where(i => i != null && !i.IsMounted && !i.definition.bonusSpawn).OrderByDescending(i => i.Value).ToList())
            {
                if (m_Director.Session.QuotaMet) break;
                item.Body.linearVelocity = Vector3.zero;
                item.Body.angularVelocity = Vector3.zero;
                item.transform.position = locker.zone.position;
                item.Body.position = locker.zone.position;
                yield return Until(() => item.IsSecured, maxFrames: 100000, maxRealSeconds: 3f);
                Assert.IsTrue(item.IsSecured, item.SalvageId + " was taken by the locker.");
            }
            Assert.IsTrue(m_Director.Session.QuotaMet);
            int deposits = m_Director.Session.DepositedCount;
            yield return Until(() => bot.World.Calls[NetMsg.ItemDeposited] == deposits, maxFrames: 100000, maxRealSeconds: 5f);
            Assert.AreEqual(deposits, bot.World.Calls[NetMsg.ItemDeposited], "The bot saw every deposit.");
            Assert.Greater(bot.World.PosesAccepted, 0, "...and the items' poses while it rode along.");

            Assert.AreSame(spider, m_Director.ActiveEnemy);
            Assert.IsTrue(spider.IsRunning, "Still aboard at extraction.");
            Assert.Greater(bot.World.Calls[NetMsg.CreatureState], samplesAtStart, "...and the bot kept seeing it.");
            Assert.Less(Distance(bot.World.Creature.position, spider.transform.position), 0.2f);
            int credits = m_Director.Profile.Data.totalCredits;
            bot.Session.SendExtractRequest();
            yield return Until(() => bot.World.Calls[NetMsg.RunEnded] > 0, maxFrames: 100000, maxRealSeconds: 8f);
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            var result = m_Director.Session.Result;
            Assert.AreEqual(MissionOutcome.ContractComplete, result.Outcome);
            Assert.AreEqual(credits + result.Payout, m_Director.Profile.Data.totalCredits, "The host is paid.");
            Assert.AreEqual(result.Payout, bot.World.Result.payout, "The bot is paid the same.");
            Assert.AreEqual(MissionOutcome.ContractComplete, bot.World.Result.outcome);

            m_Net.Leave();
            bot.Session.Dispose();
            yield return Until(() => UdpTransport.LiveCount == 0, maxFrames: 100000, maxRealSeconds: (float)NetSession.HostLingerSeconds + 2f);
            Assert.AreEqual(0, UdpTransport.LiveCount);
        }
    }
}

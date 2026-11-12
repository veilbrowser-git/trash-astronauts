using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Co-op stage 2, "shared contract, ride-along", in the real scene with no networking: the scene is the
    /// host (requests go straight into <see cref="CoopHostRules"/>, broadcasts are recorded) or a client
    /// (the host's messages go straight into <see cref="CoopClientApply"/>). A crew member is a head-only
    /// stand-in; the session is a stub that records what the game asked of it.
    /// </summary>
    public class CoopContractPlayTests
    {
        MissionDirector m_Director;
        PlayerContext m_Player;
        MemorySaveStorage m_Storage;
        CoopHostRules m_Rules;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            m_Storage = new MemorySaveStorage();
            MissionDirector.StorageOverride = m_Storage;
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
            if (m_Rules != null) m_Rules.Dispose();
            m_Rules = null;
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

        static PlayerContext AddRemotePlayer(int slot, Vector3 feet)
        {
            var go = new GameObject("Remote Crew " + slot);
            go.transform.position = feet;
            var head = new GameObject("Head").transform;
            head.SetParent(go.transform, false);
            head.localPosition = Vector3.up * 1.6f;
            var player = go.AddComponent<PlayerContext>();
            player.head = head;
            player.AssignId(new PlayerId(slot), true);
            return player;
        }

        sealed class FakeNet : IMissionNet
        {
            public bool client;
            public int slot;
            public readonly List<string> calls = new List<string>();
            public bool IsClient => client;
            public bool IsHost => !client;
            public int LocalSlot => slot;
            public void RequestStart() => calls.Add("start");
            public void RequestExtract() => calls.Add("extract");
            public void RequestDoor(SlidingDoor door, bool desiredOpen) => calls.Add("door " + door.doorId + (desiredOpen ? " open" : " close"));
            public void ReportPointCut(Cuttable panel, int pointIndex) => calls.Add("cut " + panel.panelId + " " + pointIndex);
            public void ReportSalvageDamage(SalvageItem item, int newValue) => calls.Add("damage " + item.NetIndex + " " + newValue);
            public void ReportWire(WebTripwire wire, WireChange change) => calls.Add("wire " + wire.wireId + " " + change);
            public void ReportTranqHit(Vector3 origin, Vector3 hitPoint) => calls.Add("tranq");
            public void ForwardNoise(in NoiseEvent noise) => calls.Add("noise " + noise.Kind);
            public void ReportPause(bool paused) => calls.Add("pause " + paused);
            public void ReportSettingsChanged(Handedness handedness) => calls.Add("settings " + handedness);
        }

        /// <summary>
        /// Writes down everything the host broadcasts, as text and as a tape that can be played into a client,
        /// optionally passing each message on to a scene-less mirror.
        /// </summary>
        sealed class Recorder : ICoopBroadcast
        {
            public readonly List<string> log = new List<string>();
            public readonly List<Entry> tape = new List<Entry>();
            public readonly List<RunStartParams> runStarts = new List<RunStartParams>();
            public readonly List<RunEndedMsg> runEnds = new List<RunEndedMsg>();
            public readonly List<string> mismatches = new List<string>();
            public CoopSessionMirror mirror;
            /// <summary>The host's books, to compare the mirror with after every message that changes them.</summary>
            public MissionSession host;

            public struct Entry
            {
                public string text;
                public Func<CoopClientApply, bool> replay;
                public bool ledger;
                public uint hostDigest;
            }

            public int Count(string prefix) => log.Count(l => l.StartsWith(prefix, StringComparison.Ordinal));
            public void Clear() { log.Clear(); }

            void Add(string text, Action<ICoopBroadcast> forward, Func<CoopClientApply, bool> replay, bool ledger)
            {
                log.Add(text);
                if (mirror != null) forward(mirror);
                uint digest = host != null ? SessionDigest.Compute(host) : 0u;
                if (ledger && mirror != null && digest != SessionDigest.Compute(mirror.Session))
                    mismatches.Add("after '" + text + "':\nhost   " + SessionDigest.Describe(host) + "\nmirror " + SessionDigest.Describe(mirror.Session));
                tape.Add(new Entry { text = text, replay = replay, ledger = ledger, hostDigest = digest });
            }

            public void RunStart(in RunStartParams parameters)
            {
                var run = parameters;
                runStarts.Add(run);
                Add("runstart " + run.runIndex, m => m.RunStart(in run), a => a.ApplyRunStart(in run), true);
            }

            public void WorldState(in WorldStateMsg state)
            {
                var copy = new WorldStateMsg();
                copy.CopyFrom(in state);
                Add("world doors " + copy.doorOpenMask + " panels " + copy.panelOpenMask, m => m.WorldState(in copy),
                    a => { a.ApplyWorldState(in copy); return true; }, false);
            }

            public void ExtractRefused(byte slot, byte missingSlotsMask) =>
                Add("refused " + slot + " mask " + missingSlotsMask, m => m.ExtractRefused(slot, missingSlotsMask),
                    a => { a.ApplyExtractRefused(missingSlotsMask); return true; }, false);

            public void RunEnded(in RunEndedMsg result)
            {
                var r = result;
                runEnds.Add(r);
                Add("runended " + r.outcome + " " + r.payout, m => m.RunEnded(in r), a => a.ApplyRunEnded(in r) != null, true);
            }

            public void ReturnedToHub() => Add("hub", m => m.ReturnedToHub(), a => a.ApplyReturnedToHub(), true);

            public void HostPause(bool on) => Add("pause " + on, m => m.HostPause(on), a => { a.ApplyHostPause(on); return true; }, false);

            public void ItemCollected(byte itemId, byte by) =>
                Add("collected " + itemId + " by " + by, m => m.ItemCollected(itemId, by), a => a.ApplyItemCollected(itemId, by), true);

            public void ItemValue(byte itemId, int value) =>
                Add("value " + itemId + " " + value, m => m.ItemValue(itemId, value), a => a.ApplyItemValue(itemId, value), true);

            public void ItemDeposited(byte itemId, byte by, int finalValue, int trickBonus, byte lockerSlot) =>
                Add("deposited " + itemId + " by " + by + " " + finalValue + " +" + trickBonus + " slot " + lockerSlot,
                    m => m.ItemDeposited(itemId, by, finalValue, trickBonus, lockerSlot),
                    a => a.ApplyItemDeposited(itemId, by, finalValue, trickBonus, lockerSlot), true);

            public void ItemRecovered(byte itemId, NetVec3 position) =>
                Add("recovered " + itemId, m => m.ItemRecovered(itemId, position), a => a.ApplyItemRecovered(itemId, position), false);

            public void ItemRest(byte itemId, NetVec3 position, NetQuat rotation) =>
                Add("rest " + itemId, m => m.ItemRest(itemId, position, rotation), a => a.ApplyItemRest(itemId, 0, position, rotation), false);

            public void DoorState(byte doorId, bool open) =>
                Add("door " + doorId + (open ? " open" : " closed"), m => m.DoorState(doorId, open), a => { a.ApplyDoorState(doorId, open); return true; }, false);

            public void PointCut(byte panel, byte index) =>
                Add("cut " + panel + " " + index, m => m.PointCut(panel, index), a => { a.ApplyPointCut(panel, index); return true; }, false);

            // Stage 4: the creature's messages (these tests run with the creature switched off, so they stay silent).
            public void CreatureStateChanged(EnemyState state, byte targetSlot) => log.Add("creature " + state + " " + targetSlot);
            public void Tranquilized(float seconds) => log.Add("tranquilized");
            public void CatchStarted(byte victimSlot) => log.Add("catch " + victimSlot);
            public void BlackoutStarted(float seconds) => log.Add("blackout on");
            public void BlackoutEnded(bool instant) => log.Add("blackout off");
            public void PlayerDowned(byte slot, NetVec3 position) => log.Add("downed " + slot);
            public void PlayerRevived(byte slot, byte by) => log.Add("revived " + slot);
            public void ReviveProgress(byte slot, float progress, byte rescuerSlot) => log.Add("progress " + slot);
            // Stage 3: the host player's own hand (these tests have no crew grabs: the host always gets it).
            public bool LocalGrab(byte itemId) => true;
            public void LocalRelease(byte itemId) { }

            public readonly List<(byte panel, byte by, NetVec3 impulse, NetVec3 torque)> panelsOpened = new List<(byte, byte, NetVec3, NetVec3)>();

            public void PanelOpened(byte panel, byte by, NetVec3 plateImpulse, NetVec3 plateTorque)
            {
                panelsOpened.Add((panel, by, plateImpulse, plateTorque));
                Add("opened " + panel + " by " + by, m => m.PanelOpened(panel, by, plateImpulse, plateTorque),
                    a => a.ApplyPanelOpened(panel, by, plateImpulse, plateTorque), true);
            }
        }

        SalvageItem[] LooseItems() => m_Director.Items.Where(i => i != null && !i.IsSecured && !i.IsMounted && i.definition != null).ToArray();

        double m_RulesClock;

        Recorder Host()
        {
            m_Director.Net = new FakeNet { client = false, slot = 0 };
            var recorder = new Recorder();
            m_Rules = new CoopHostRules(m_Director, recorder);
            // A second passes between any two requests here, so the per-crew request rate limit never bites
            // in the tests that are about something else (it has its own test).
            m_Rules.Clock = () => m_RulesClock += 1.0;
            return recorder;
        }

        /// <summary>The run a host would send: built here offline with the ride-along limits, under the host's own run number.</summary>
        RunStartParams HostRun(int runIndex)
        {
            m_Director.CoopRideAlong = true;
            var run = m_Director.BuildRunParams();
            m_Director.CoopRideAlong = false;
            run.runIndex = (ushort)runIndex;
            run.planHash = run.ComputePlanHash();
            return run;
        }

        FakeNet BecomeClient(int slot)
        {
            var net = new FakeNet { client = true, slot = slot };
            m_Player.AssignId(new PlayerId(slot), false);
            m_Director.Net = net;
            return net;
        }

        static Vector3 InTheWreck => MapPoints.Freighter(0f, 0f, 12f);

        // ================================================================== host half

        [UnityTest]
        public IEnumerator Host_AStartRequestFromTheCrew_StartsARealRideAlongContract_AndTellsEveryone()
        {
            var crew = AddRemotePlayer(1, m_Director.hubSpawn.position);
            var rec = Host();
            yield return Frames(2);
            Assert.IsTrue(m_Director.CoopRideAlong, "A host's contracts are ride-along contracts.");
            int runsBefore = m_Director.Profile.Data.runsStarted;

            Assert.IsTrue(m_Rules.HostStartRequest(1));
            yield return Frames(2);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);

            // No creature, no blackouts, no webs: the existing no-creature path.
            var run = m_Director.CurrentRun;
            Assert.AreEqual(RunStartParams.NoCreature, run.creatureIndex);
            Assert.AreEqual(0, run.hazardFlags);
            Assert.AreEqual(0u, run.wireArmedMask);
            Assert.AreEqual(run.ComputePlanHash(), run.planHash);
            Assert.IsFalse(m_Director.CreatureAboard);
            Assert.IsFalse(m_Director.blackout.IsScheduled);
            Assert.IsTrue(m_Director.ActiveMap.tripwires.All(w => !w.Armed));
            Assert.AreEqual(runsBefore + 1, m_Director.Profile.Data.runsStarted);

            // Everyone is told the contract, then the state of the world it starts in.
            Assert.AreEqual(1, rec.runStarts.Count);
            Assert.AreEqual(run.planHash, rec.runStarts[0].planHash);
            Assert.AreEqual(run.items.Length, rec.runStarts[0].items.Length);
            int at = rec.log.IndexOf("runstart " + run.runIndex);
            Assert.GreaterOrEqual(at, 0);
            StringAssert.StartsWith("world ", rec.log[at + 1]);
            int hatch = m_Director.NetIds.Doors.IndexOf(m_Director.tugHatch.doorId);
            Assert.IsTrue(m_Director.tugHatch.IsOpen);
            StringAssert.Contains("doors " + (1 << hatch) + " ", rec.log[at + 1] + " ", "The world state says the hatch is open.");
            Assert.AreEqual(run.items.Length, m_Director.RunItems.Count(i => i != null), "Every item was spawned.");

            // One contract at a time.
            Assert.IsFalse(m_Rules.HostStartRequest(1));
            Assert.AreEqual(1, rec.runStarts.Count);

            // The host's own START button goes the same way.
            m_Director.FailMission(FailureReason.OxygenDepleted, PlayerId.Local);
            Assert.AreEqual(1, rec.Count("runended FailedOxygen"));
            Assert.IsTrue(m_Director.StartMission(m_Director.LocalPlayerId));
            Assert.AreEqual(2, rec.runStarts.Count);
            Assert.AreEqual(RunStartParams.NoCreature, rec.runStarts[1].creatureIndex);
            CollectionAssert.IsEmpty(((FakeNet)m_Director.Net).calls, "A host decides; it never asks.");

            // Once the session is over, contracts are ordinary again.
            m_Rules.Dispose();
            m_Rules = null;
            Assert.IsFalse(m_Director.CoopRideAlong);
            Object.Destroy(crew.gameObject);
        }

        [UnityTest]
        public IEnumerator Host_DoorRequestsAreDesiredStates_SoTwoCrewOpeningOneDoorLeaveItOpen()
        {
            var rec = Host();
            Assert.IsTrue(m_Rules.HostStartRequest(0));
            yield return Frames(2);
            var door = m_Director.wreckDoors.First(d => d != null && !d.locked && !d.IsOpen && d.buttons != null && d.buttons.Length > 0);
            byte id = (byte)m_Director.NetIds.Doors.IndexOf(door.doorId);
            // Both crew members stand at the door (requests from far away are refused; see the trust test).
            var crew1 = AddRemotePlayer(1, door.transform.position);
            var crew2 = AddRemotePlayer(2, door.transform.position);
            yield return Frames(2); // they register on their first frame
            rec.Clear();

            // Two crew members press OPEN on either side at once: it opens, once.
            Assert.IsTrue(m_Rules.HostDoorRequest(id, true, 1));
            Assert.IsFalse(m_Rules.HostDoorRequest(id, true, 2), "Already open: nothing to do.");
            Assert.IsTrue(door.IsOpen);
            yield return Frames(10);
            Assert.IsTrue(door.IsOpen, "A second press must not toggle it shut again.");
            CollectionAssert.AreEqual(new[] { "door " + id + " open" }, rec.log);

            // Wanting it shut shuts it (once the doorway is clear).
            Assert.IsTrue(m_Rules.HostDoorRequest(id, false, 2));
            for (int i = 0; i < 300 && door.IsOpen; i++) yield return null;
            Assert.IsFalse(door.IsOpen);
            CollectionAssert.AreEqual(new[] { "door " + id + " open", "door " + id + " closed" }, rec.log);

            // The hatch is the mission's, whoever asks; unknown ids are nobody's.
            byte hatch = (byte)m_Director.NetIds.Doors.IndexOf(m_Director.tugHatch.doorId);
            Assert.IsFalse(m_Rules.HostDoorRequest(hatch, false, 1));
            Assert.IsTrue(m_Director.tugHatch.IsOpen);
            Assert.IsFalse(m_Rules.HostDoorRequest(200, true, 1));
            Assert.AreEqual(2, rec.log.Count);
            Object.Destroy(crew1.gameObject);
            Object.Destroy(crew2.gameObject);
        }

        [UnityTest]
        public IEnumerator Host_PointCutReports_OpenARealPanelOnce_AndFreeWhatWasMountedOnIt()
        {
            var rec = Host();
            SalvageSpawnPoint point = null;
            for (int seed = 1; seed <= 40 && point == null; seed++)
            {
                if (m_Director.Phase == MissionPhase.Active) m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
                MissionDirector.SeedOverride = seed;
                Assert.IsTrue(m_Rules.HostStartRequest(1));
                point = m_Director.spawnPoints.FirstOrDefault(p => p.mount != null
                    && m_Director.Items.Any(i => i.IsMounted && Vector3.Distance(i.SpawnPosition, p.transform.position) < 0.01f));
            }
            Assert.IsNotNull(point, "Test set-up: expected a contract with mounted salvage.");
            yield return Frames(2);
            var panel = point.mount;
            var item = m_Director.Items.First(i => i.IsMounted && Vector3.Distance(i.SpawnPosition, point.transform.position) < 0.01f);
            byte pid = (byte)m_Director.NetIds.Panels.IndexOf(panel.panelId);
            Assert.AreSame(panel, m_Director.PanelAt(pid));
            Assert.Greater(panel.TotalCuts, 0);
            // Both crew members are at the panel with their cutters (far-away reports are refused; see the trust test).
            var cutter1 = AddRemotePlayer(1, panel.transform.position);
            var cutter2 = AddRemotePlayer(2, panel.transform.position);
            cutter1.SetRemoteHead(panel.transform.position);
            cutter2.SetRemoteHead(panel.transform.position);
            yield return Frames(2); // they register on their first frame
            rec.Clear();

            for (int k = 0; k < panel.TotalCuts - 1; k++)
            {
                Assert.IsTrue(m_Rules.HostPointCutReport(pid, (byte)k, 1));
                Assert.IsFalse(m_Rules.HostPointCutReport(pid, (byte)k, 2), "The same weld twice counts once.");
            }
            Assert.IsFalse(panel.IsOpen);
            Assert.IsTrue(item.IsMounted);
            Assert.AreEqual(panel.TotalCuts - 1, rec.Count("cut " + pid + " "));
            Assert.AreEqual(0, rec.Count("opened"));

            // The last weld: the host opens it, with a push it rolled itself, and the item comes free.
            Assert.IsTrue(m_Rules.HostPointCutReport(pid, (byte)(panel.TotalCuts - 1), 1));
            Assert.IsTrue(panel.IsOpen);
            Assert.IsTrue(m_Director.Session.IsPanelOpen(panel.panelId));
            Assert.AreEqual(panel.TotalCuts, rec.Count("cut " + pid + " "));
            Assert.AreEqual(1, rec.panelsOpened.Count);
            var opened = rec.panelsOpened[0];
            Assert.AreEqual(pid, opened.panel);
            Assert.AreEqual(1, opened.by);
            Assert.AreEqual(panel.LastPlateImpulse, CoopConvert.ToUnity(opened.impulse));
            Assert.AreEqual(panel.LastPlateTorque, CoopConvert.ToUnity(opened.torque));
            Assert.Greater(panel.LastPlateImpulse.magnitude, 0.3f, "The plate was pushed off.");
            Assert.IsFalse(item.IsMounted);
            Assert.IsFalse(item.Body.isKinematic, "The host simulates the freed item.");
            Assert.IsTrue(item.grab.enabled);

            // Nothing more comes of further reports.
            int messages = rec.log.Count;
            Assert.IsFalse(m_Rules.HostPointCutReport(pid, 0, 2));
            Assert.AreEqual(messages, rec.log.Count);

            // A panel the host cuts with its own cutter is told the same way.
            var own = m_Director.cuttables.First(c => c != null && !c.isTraining && !c.IsOpen && c.TotalCuts > 0);
            byte ownId = (byte)m_Director.NetIds.Panels.IndexOf(own.panelId);
            rec.Clear();
            foreach (var p in own.cutPoints) own.ApplyCut(p, 10f, 1f, m_Player);
            Assert.IsTrue(own.IsOpen);
            Assert.AreEqual(own.TotalCuts, rec.Count("cut " + ownId + " "));
            Assert.AreEqual(1, rec.Count("opened " + ownId + " by 0"));
        }

        [UnityTest]
        public IEnumerator Host_ExtractionWaitsForEveryCrewMemberWhoIsPresent_AndNamesWhoIsMissing()
        {
            var crew1 = AddRemotePlayer(1, m_Director.hubSpawn.position);
            var crew2 = AddRemotePlayer(2, m_Director.hubSpawn.position);
            var rec = Host();
            yield return Frames(2);
            Assert.IsTrue(m_Rules.HostStartRequest(2));
            yield return Frames(2);
            var scrap = LooseItems().First(i => i.definition.category == SalvageCategory.SmallScrap);
            Assert.IsTrue(m_Director.locker.TryAccept(scrap));

            // Crew 2 is out in the wreck: crew 1's request is turned down, naming crew 2.
            crew2.SetRemoteHead(InTheWreck + Vector3.up * 1.6f);
            rec.Clear();
            Assert.IsNull(m_Rules.HostExtractRequest(1));
            CollectionAssert.AreEqual(new[] { "refused 1 mask " + (1 << 2) }, rec.log);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);

            // The host's own player out there too: both are named, to whoever asked.
            m_Player.locomotion.TeleportTo(InTheWreck);
            yield return Frames(4);
            Assert.IsNull(m_Rules.HostExtractRequest(2));
            Assert.AreEqual("refused 2 mask " + (1 << 0 | 1 << 2), rec.log.Last());

            // Everyone aboard: the request ends the run for everybody.
            m_Player.locomotion.TeleportTo(m_Director.hubSpawn.position, m_Director.hubSpawn.rotation);
            crew2.SetRemoteHead(m_Director.hubSpawn.position + Vector3.up * 1.6f);
            yield return Frames(4);
            var result = m_Rules.HostExtractRequest(1);
            Assert.IsNotNull(result);
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            Assert.AreEqual(1, rec.runEnds.Count);
            Assert.AreEqual(result.Outcome, rec.runEnds[0].outcome);
            Assert.AreEqual(result.Payout, rec.runEnds[0].payout);
            Assert.AreEqual(result.DepositedValue, rec.runEnds[0].depositedValue);
            Assert.IsNull(m_Rules.HostExtractRequest(1), "No contract, nothing to extract from.");

            // Next contract: crew 2 has taken the headset off out in the wreck. Away players are not waited for.
            Assert.IsTrue(m_Rules.HostStartRequest(1));
            yield return Frames(2);
            crew2.SetRemoteHead(InTheWreck + Vector3.up * 1.6f);
            Assert.IsNull(m_Rules.HostExtractRequest(1));
            m_Rules.HostPlayerAway(2, true);
            Assert.IsTrue(crew2.IsAway);
            Assert.IsNotNull(m_Rules.HostExtractRequest(1));
            Assert.AreEqual(2, rec.runEnds.Count);

            // Leaving the results screen is told too.
            rec.Clear();
            Assert.IsTrue(m_Director.ReturnToHub());
            CollectionAssert.AreEqual(new[] { "hub" }, rec.log);
            Object.Destroy(crew1.gameObject);
            Object.Destroy(crew2.gameObject);
        }

        [UnityTest]
        public IEnumerator Host_TheSharedTankDrainsForCrewOutside_ButNotForSomeoneAway_AndPausesAreTold()
        {
            var crew = AddRemotePlayer(1, m_Director.hubSpawn.position);
            var rec = Host();
            yield return Frames(2);
            Assert.IsTrue(m_Rules.HostStartRequest(1));
            yield return Frames(3);
            var tank = m_Director.Session.Oxygen;
            tank.SetRemaining(60f);
            Assert.IsTrue(m_Player.InSafeZone);

            // Out in the wreck but away: no air used.
            crew.SetRemoteHead(InTheWreck + Vector3.up * 1.6f);
            m_Rules.HostPlayerAway(1, true);
            yield return null;
            float before = tank.Remaining;
            yield return Frames(20);
            Assert.AreEqual(before, tank.Remaining, "An away crew member must not drain the tank.");

            // Back: it drains.
            m_Rules.HostPlayerAway(1, false);
            yield return null;
            before = tank.Remaining;
            yield return Frames(20);
            Assert.Less(tank.Remaining, before);

            // The clock carries the tank as it is.
            m_Rules.ReadClock(out float elapsed, out float oxygen, out byte flags);
            Assert.AreEqual(m_Director.Session.ElapsedSeconds, elapsed);
            Assert.AreEqual(tank.Remaining, oxygen);
            Assert.AreEqual(0, flags);

            // The host's pause freezes the tank, and everyone hears of it.
            rec.Clear();
            m_Director.SetHazardPause(HazardPauseReason.Scripted, true);
            CollectionAssert.AreEqual(new[] { "pause True" }, rec.log);
            m_Rules.ReadClock(out _, out _, out flags);
            Assert.AreEqual(ClockFlags.HazardsPaused, flags);
            before = tank.Remaining;
            yield return Frames(10);
            Assert.AreEqual(before, tank.Remaining);
            m_Director.SetHazardPause(HazardPauseReason.Scripted, false);
            CollectionAssert.AreEqual(new[] { "pause True", "pause False" }, rec.log);
            Object.Destroy(crew.gameObject);
        }

        [UnityTest]
        public IEnumerator Host_StreamsMovingItemsNearestFirst_AtMostTwelve_PlusOneRestingInTurn_AndRestsEachOnce()
        {
            var crew = AddRemotePlayer(1, m_Director.hubSpawn.position);
            var rec = Host();
            yield return Frames(2);
            Assert.IsTrue(m_Rules.HostStartRequest(1));
            yield return Frames(2);
            var buffer = new ItemPoseEntry[16];

            // Nothing has moved yet: only the one refresh entry per tick, a different item each time.
            m_Rules.Tick(0.05f);
            int n = m_Rules.CollectItemPoses(buffer);
            Assert.AreEqual(1, n);
            Assert.IsTrue(buffer[0].atRest);
            int first = buffer[0].itemId;
            m_Rules.CollectItemPoses(buffer);
            Assert.AreNotEqual(first, buffer[0].itemId, "The resting refresh goes round the items in turn.");
            Assert.AreEqual(0, rec.Count("rest"), "Items still where they spawned are never 'rested'.");

            // Items set drifting (all but one), and the farthest held by crew 1.
            var loose = LooseItems().OrderBy(i => i.NetIndex).ToList();
            Assert.Greater(loose.Count, 6, "Test set-up: a contract with plenty of loose salvage.");
            Vector3 head = crew.HeadPosition;
            var held = loose.OrderByDescending(i => Vector3.Distance(i.transform.position, head)).First();
            var spare = loose.First(i => i != held);
            var moving = loose.Where(i => i != held && i != spare).ToList();
            foreach (var item in moving) item.Body.linearVelocity = Vector3.up * 0.3f;
            held.SetRemoteHolder(new PlayerId(1));
            m_Rules.Tick(0.05f);
            n = m_Rules.CollectItemPoses(buffer);
            Assert.AreEqual(12, CoopItemSampler.MaxMovingPerTick);
            Assert.AreEqual(Mathf.Min(moving.Count + 1, 12) + 1, n, "Every moving item (up to twelve) plus one resting refresh.");
            Assert.AreEqual(spare.NetIndex, buffer[n - 1].itemId, "The refresh is the one item at rest.");

            // A contract only has a dozen or so items, so the cap is proven at four.
            m_Rules.Sampler.maxMoving = 4;
            n = m_Rules.CollectItemPoses(buffer);
            Assert.AreEqual(4 + 1, n, "Four moving items plus one resting refresh.");
            Assert.AreEqual(held.NetIndex, buffer[0].itemId, "An item in a hand goes first, however far away.");
            Assert.IsTrue(buffer[0].held);
            float last = -1f;
            for (int i = 1; i < 4; i++)
            {
                Assert.IsFalse(buffer[i].atRest);
                float d = Vector3.Distance(CoopConvert.ToUnity(buffer[i].position), head);
                Assert.GreaterOrEqual(d, last - 1e-3f, "Then nearest first (all equally fast).");
                last = d;
                var item = m_Director.RunItemAt(buffer[i].itemId);
                Assert.AreEqual(item.transform.position, CoopConvert.ToUnity(buffer[i].position));
            }
            var farthest = moving.OrderByDescending(i => Vector3.Distance(i.transform.position, head)).First();
            Assert.IsFalse(buffer.Take(n).Any(e => e.itemId == farthest.NetIndex && !e.atRest), "Beyond twelve, the farthest waits.");
            Assert.IsTrue(buffer[n - 1].atRest);
            Assert.AreEqual(spare.NetIndex, buffer[n - 1].itemId);
            m_Rules.Sampler.maxMoving = CoopItemSampler.MaxMovingPerTick;

            // They stop: after half a second of stillness each is rested exactly once.
            foreach (var item in moving) item.Body.linearVelocity = Vector3.zero;
            held.SetRemoteHolder(PlayerId.None);
            held.Body.linearVelocity = Vector3.zero;
            m_Rules.Tick(0.3f);
            Assert.AreEqual(0, rec.Count("rest"));
            m_Rules.Tick(0.3f);
            Assert.AreEqual(moving.Count + 1, rec.Count("rest"));
            foreach (var item in moving) Assert.AreEqual(1, rec.log.Count(l => l == "rest " + item.NetIndex), "rest " + item.NetIndex);
            m_Rules.Tick(1f);
            Assert.AreEqual(moving.Count + 1, rec.Count("rest"));
            Assert.AreEqual(1, m_Rules.CollectItemPoses(buffer));
            Object.Destroy(crew.gameObject);
        }

        [UnityTest]
        public IEnumerator Host_ASlowZeroGDrift_IsNotDeclaredAtRest_AndARestedItemThatCreepsAwayIsStreamedAgain()
        {
            var crew = AddRemotePlayer(1, m_Director.hubSpawn.position);
            var rec = Host();
            yield return Frames(2);
            Assert.IsTrue(m_Rules.HostStartRequest(1));
            yield return Frames(2);
            var buffer = new ItemPoseEntry[16];
            var item = LooseItems().First();
            int id = item.NetIndex;

            // 3 cm/s: the old threshold called this still and the host's copy drifted on while the crew's stopped.
            item.Body.linearVelocity = Vector3.right * 0.03f;
            item.Body.angularVelocity = Vector3.zero;
            for (int i = 0; i < 10; i++) m_Rules.Tick(0.1f);
            Assert.AreEqual(0, rec.Count("rest " + id), "A drifting item is never rested.");
            int n = m_Rules.CollectItemPoses(buffer);
            Assert.IsTrue(buffer.Take(n).Any(e => e.itemId == id && !e.atRest), "It is streamed as moving.");

            // Really still for half a second: rested once.
            item.Body.linearVelocity = Vector3.zero;
            m_Rules.Tick(0.3f);
            m_Rules.Tick(0.3f);
            Assert.AreEqual(1, rec.Count("rest " + id));

            // It creeps 5 cm (too slowly to count as moving): streamed again, then rested where it is now.
            item.Body.position += Vector3.up * 0.05f;
            item.transform.position += Vector3.up * 0.05f;
            m_Rules.Tick(0.05f);
            n = m_Rules.CollectItemPoses(buffer);
            Assert.IsTrue(buffer.Take(n).Any(e => e.itemId == id && !e.atRest), "Moved more than 2 cm since its rest: streamed again.");
            m_Rules.Tick(0.3f);
            m_Rules.Tick(0.3f);
            Assert.AreEqual(2, rec.Count("rest " + id), "...and rested again at its new place.");
            Object.Destroy(crew.gameObject);
        }

        // ================================================================== client half

        [UnityTest]
        public IEnumerator Client_RunStart_SpawnsTheHostsSalvageIds_DuckIncluded_WhateverThisHeadsetPlayedBefore()
        {
            // This headset has its own history: one contract played offline.
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            yield return Frames(2);
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            Assert.AreEqual(1, m_Director.Session.RunIndex);

            // The host is on its ninth.
            var run = HostRun(9);
            var net = BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            int runsBefore = m_Director.Profile.Data.runsStarted;
            Assert.IsTrue(apply.ApplyRunStart(in run));
            yield return Frames(2);

            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.AreEqual(9, m_Director.Session.RunIndex);
            Assert.IsFalse(m_Director.CreatureAboard);
            Assert.IsFalse(m_Director.ActiveEnemy.IsRunning, "Nothing hunts on a client of a ride-along contract.");
            Assert.IsFalse(m_Director.blackout.IsScheduled, "No blackout is scheduled here: the host sends none.");
            Assert.IsTrue(m_Director.ActiveMap.tripwires.All(w => !w.Armed), "No web is strung: the host's mask is empty.");
            Assert.AreEqual(runsBefore + 1, m_Director.Profile.Data.runsStarted, "Each headset records its own run start.");
            Assert.AreEqual(run.items.Length, m_Director.RunItems.Count);
            for (int i = 0; i < run.items.Length; i++)
            {
                var item = m_Director.RunItems[i];
                Assert.IsNotNull(item);
                Assert.AreEqual(m_Director.NetIds.SalvageIdFor(9, run.items[i]), item.SalvageId);
                StringAssert.StartsWith("run9/", item.SalvageId);
                // Only the host moves salvage in this stage.
                Assert.IsFalse(item.SimulatedHere);
                Assert.IsTrue(item.HostOnly);
                Assert.IsTrue(item.Body.isKinematic);
                Assert.IsFalse(item.grab.enabled, item.SalvageId + " must not be grabbable on a client.");
            }
            var duck = m_Director.RunItems[run.items.Length - 1];
            Assert.IsTrue(duck.definition.bonusSpawn);
            StringAssert.EndsWith("#bonus", duck.SalvageId);

            // The books are exactly what a scene-less mirror of the same message holds.
            var mirror = new CoopSessionMirror(m_Director.NetIds, MissionSession.FromConfig(m_Director.Config));
            mirror.RunStart(in run);
            Assert.AreEqual(SessionDigest.Describe(mirror.Session), SessionDigest.Describe(m_Director.Session));

            // The scanner shows what it is; the stage 2 "HOST ONLY" hint is gone (stage 3 shares grabbing through
            // CoopItemClient; this session has none, so the items simply stay with the host).
            var scrap = m_Director.RunItems.First(i => !i.IsMounted && i.definition.category == SalvageCategory.SmallScrap);
            scrap.ShowScan(2f);
            StringAssert.Contains(scrap.definition.displayName, scrap.scanTag.text.text);
            StringAssert.DoesNotContain("HOST ONLY", scrap.scanTag.text.text);

            // The same message again changes nothing; a corrupted one is refused.
            var items = m_Director.RunItems.ToList();
            Assert.IsTrue(apply.ApplyRunStart(in run));
            CollectionAssert.AreEqual(items, m_Director.RunItems.ToList());
            Assert.AreEqual(runsBefore + 1, m_Director.Profile.Data.runsStarted);
            var bad = run;
            bad.quota += 1;
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("plan hash"));
            Assert.IsFalse(apply.ApplyRunStart(in bad));
            CollectionAssert.AreEqual(items, m_Director.RunItems.ToList());
            CollectionAssert.IsEmpty(net.calls);
        }

        [UnityTest]
        public IEnumerator Client_TheLockerRecoveryOxygenAndRunEnd_AreNotDecidedHere()
        {
            var run = HostRun(3);
            var net = BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            Assert.IsTrue(apply.ApplyRunStart(in run));
            yield return Frames(2);
            var items = m_Director.RunItems.Where(i => !i.IsMounted).Take(2).ToArray();

            // An item that ends up in the hopper on this machine is not taken: the host's locker decides.
            Assert.IsTrue(apply.ApplyItemRecovered((byte)items[0].NetIndex, CoopConvert.ToNet(m_Director.locker.zone.position)));
            Assert.AreEqual(m_Director.locker.zone.position, items[0].transform.position);
            yield return Frames(10);
            m_Director.locker.ScanZone();
            Assert.IsFalse(items[0].IsSecured);
            Assert.AreEqual(0, m_Director.Session.DepositedCount);

            // An item lost out of bounds here is not brought back here.
            Vector3 lost = new Vector3(0f, 0f, 500f);
            CoopItemReplica.For(items[1]).Snap(lost, Quaternion.identity);
            Assert.IsFalse(m_Director.bounds.Contains(lost));
            yield return Seconds(1.2f);
            Assert.AreEqual(lost, items[1].transform.position, "Only the host recovers lost salvage.");

            // The tank: counted on between the host's clocks, never further than that, and never to a failure.
            m_Player.locomotion.TeleportTo(InTheWreck);
            yield return Frames(4);
            Assert.IsFalse(m_Player.InSafeZone);
            Assert.IsTrue(apply.ApplyClock(10f, 0.5f, 0));
            yield return Seconds(MissionDirector.MaxClockExtrapolation + 0.8f);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase, "A client never fails the run on its own clock.");
            Assert.AreEqual(0f, m_Director.Session.Oxygen.Remaining, 1e-4f, "It counted the last half second down...");
            Assert.LessOrEqual(m_Director.Session.ElapsedSeconds, 10f + MissionDirector.MaxClockExtrapolation + 1e-3f, "...and then stopped, waiting for the host.");
            Assert.Greater(m_Director.Session.ElapsedSeconds, 10f + MissionDirector.MaxClockExtrapolation - 0.1f);
            Assert.IsNull(m_Director.TickMission(1000f, false));
            Assert.IsNull(m_Director.FailMission(FailureReason.OxygenDepleted, m_Director.LocalPlayerId));
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);

            // The host's word replaces the estimate; its pause stops the count.
            Assert.IsTrue(apply.ApplyClock(12f, 30f, ClockFlags.HazardsPaused));
            Assert.IsTrue(m_Director.HazardsPaused);
            yield return Frames(10);
            Assert.AreEqual(12f, m_Director.Session.ElapsedSeconds);
            Assert.AreEqual(30f, m_Director.Session.Oxygen.Remaining);
            Assert.IsTrue(apply.ApplyClock(12.5f, 29.5f, 0));
            Assert.IsFalse(m_Director.HazardsPaused);
            // So does a silent host.
            apply.ApplyHostSilent(true);
            yield return Frames(10);
            Assert.AreEqual(29.5f, m_Director.Session.Oxygen.Remaining);
            apply.ApplyHostSilent(false);
            yield return Frames(10);
            Assert.Less(m_Director.Session.Oxygen.Remaining, 29.5f);

            // Extraction is only a request.
            Assert.IsNull(m_Director.Extract(m_Director.LocalPlayerId));
            CollectionAssert.AreEqual(new[] { "extract" }, net.calls);
            int refused = -1;
            m_Director.ExtractRefused += mask => refused = mask;
            apply.ApplyExtractRefused(1 << 2);
            Assert.AreEqual(1 << 2, refused);
            Assert.AreEqual(1 << 2, m_Director.LastExtractRefusedMask);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
        }

        [UnityTest]
        public IEnumerator Client_ItemsFollowTheHostsPoses_AndARestHealsAMissedFinalPose()
        {
            var run = HostRun(2);
            BecomeClient(2);
            var apply = new CoopClientApply(m_Director);
            Assert.IsTrue(apply.ApplyRunStart(in run));
            yield return Frames(2);
            var item = m_Director.RunItems.First(i => i != null && !i.IsMounted && i.definition.category == SalvageCategory.SmallScrap);
            byte id = (byte)item.NetIndex;
            Vector3 start = item.transform.position;
            Quaternion rotation = item.transform.rotation;

            ItemPoseEntry Pose(Vector3 at, byte epoch) => new ItemPoseEntry
            {
                itemId = id, position = CoopConvert.ToNet(at), rotation = CoopConvert.ToNet(rotation), epoch = epoch,
            };

            // The host moves it 1 m/s along x and streams it at 20 Hz.
            float t0 = Time.unscaledTime, lastSent = -1f;
            Vector3 lastPose = start;
            bool sawLag = false;
            while (Time.unscaledTime - t0 < 1f)
            {
                float t = Time.unscaledTime - t0;
                if (lastSent < 0f || t - lastSent >= 0.05f)
                {
                    lastSent = t;
                    lastPose = start + Vector3.right * t;
                    Assert.IsTrue(apply.ApplyItemPose(Pose(lastPose, 0)));
                }
                yield return null;
                if (t > 0.3f)
                {
                    float behind = lastPose.x - item.transform.position.x;
                    Assert.Less(behind, 0.3f, "It keeps up, about a tenth of a second behind.");
                    if (behind > 0.02f) sawLag = true;
                }
            }
            Assert.IsTrue(sawLag, "Shown slightly in the past, so there are two samples to blend.");
            Assert.Greater(item.transform.position.x, start.x + 0.6f);
            Assert.IsTrue(item.Body.isKinematic, "It is moved, never simulated, here.");
            // It stops there; the host streams the stopped item until it counts as resting.
            for (int i = 0; i < 4; i++)
            {
                Assert.IsTrue(apply.ApplyItemPose(Pose(lastPose, 0)));
                yield return Seconds(0.05f);
            }
            yield return Seconds(0.4f);
            Assert.Less(Vector3.Distance(lastPose, item.transform.position), 0.01f, "It ends where the last pose said.");

            // The host's item drifted on and settled, but those poses never arrived. The reliable rest puts it right.
            Vector3 final = lastPose + new Vector3(0.3f, 0.1f, 0f);
            yield return Seconds(0.3f);
            Assert.Less(Vector3.Distance(lastPose, item.transform.position), 0.01f);
            Assert.IsTrue(apply.ApplyItemRest(id, 0, CoopConvert.ToNet(final), CoopConvert.ToNet(rotation)));
            yield return Seconds(0.3f);
            Assert.Less(Vector3.Distance(final, item.transform.position), 0.001f);

            // Somebody else's flight (a newer epoch) replaces it; anything from before that is stale and dropped.
            Vector3 newer = final + Vector3.up;
            Assert.IsTrue(apply.ApplyItemPose(Pose(newer, 1)));
            Assert.IsFalse(apply.ApplyItemPose(Pose(final - Vector3.up, 0)), "A pose from an older epoch is ignored.");
            Assert.IsFalse(apply.ApplyItemRest(id, 0, CoopConvert.ToNet(start), CoopConvert.ToNet(rotation)));
            yield return Seconds(0.4f);
            Assert.Less(Vector3.Distance(newer, item.transform.position), 0.001f);
            // The 6-bit epoch wraps: 63 -> 0 is newer.
            Assert.AreEqual(1, CoopEpoch.Compare(63, 0));
            Assert.AreEqual(-1, CoopEpoch.Compare(0, 63));
            Assert.IsFalse(apply.ApplyItemPose(new ItemPoseEntry { itemId = 250 }), "Unknown items are ignored.");
        }

        [UnityTest]
        public IEnumerator Client_ADepositLandsInTheSlotTheHostChose_Once()
        {
            var run = HostRun(5);
            BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            Assert.IsTrue(apply.ApplyRunStart(in run));
            yield return Frames(2);
            var loose = m_Director.RunItems.Where(i => i != null && !i.IsMounted).ToArray();
            var item = loose.First(i => i.definition.fragility > 0f);
            var other = loose.First(i => i != item);
            byte id = (byte)item.NetIndex;
            int deposited = 0;
            m_Director.SalvageDeposited += _ => deposited++;

            Assert.IsTrue(apply.ApplyItemCollected(id, 2));
            Assert.IsFalse(apply.ApplyItemCollected(id, 3), "Collected once, by whoever the host said.");
            Assert.IsTrue(m_Director.Session.TryGetSalvage(item.SalvageId, out var record));
            Assert.AreEqual(SalvageState.Collected, record.State);
            Assert.AreEqual(new PlayerId(2), record.CollectedBy);
            int worth = item.Value - 5;
            Assert.IsTrue(apply.ApplyItemValue(id, worth));
            Assert.AreEqual(worth, item.Value);
            Assert.AreEqual(worth, record.Value);

            Assert.IsTrue(apply.ApplyItemDeposited(id, 2, worth, 7, 3));
            Assert.IsTrue(item.IsSecured);
            Assert.AreSame(m_Director.locker.slots[3], item.transform.parent, "The slot the host chose, not this locker's next.");
            Assert.AreEqual(SalvageState.Deposited, record.State);
            Assert.AreEqual(new PlayerId(2), record.DepositedBy);
            Assert.AreEqual(worth, m_Director.Session.DepositedValue);
            Assert.AreEqual(7, m_Director.Session.TrickBonus);
            Assert.AreEqual(1, m_Director.Session.TrickShots);
            Assert.AreEqual(1, deposited);
            uint books = SessionDigest.Compute(m_Director.Session);
            Assert.IsFalse(apply.ApplyItemDeposited(id, 2, worth, 7, 3));
            Assert.IsFalse(apply.ApplyItemDeposited(id, 1, 999, 0, 5), "A deposit is booked once, whatever arrives later.");
            Assert.AreEqual(books, SessionDigest.Compute(m_Director.Session));
            Assert.AreSame(m_Director.locker.slots[3], item.transform.parent);
            Assert.AreEqual(1, deposited);
            Assert.IsFalse(apply.ApplyItemValue(id, 1), "Its value is final once it is in the locker.");

            Assert.IsTrue(apply.ApplyItemDeposited((byte)other.NetIndex, 0, other.Value, 0, 0));
            Assert.AreSame(m_Director.locker.slots[0], other.transform.parent);
            Assert.AreEqual(2, m_Director.Session.DepositedCount);
            yield return Seconds(0.8f);
            Assert.Less(item.transform.localPosition.magnitude, 1e-4f, "Settled into its slot, and nothing moves it out again.");
        }

        [UnityTest]
        public IEnumerator Client_RunEndedPaysThisHeadsetOnce_AndBringsHomeARigLeftInTheWreck()
        {
            var run = HostRun(4);
            BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            Assert.IsTrue(apply.ApplyRunStart(in run));
            yield return Frames(2);
            var item = m_Director.RunItems.First(i => i != null && !i.IsMounted);
            Assert.IsTrue(apply.ApplyItemDeposited((byte)item.NetIndex, 0, item.Value, 0, 0));
            m_Player.locomotion.TeleportTo(InTheWreck);
            yield return Frames(4);
            Assert.IsFalse(m_Player.InSafeZone);

            int credits = m_Director.Profile.Data.totalCredits;
            int completed = m_Director.Profile.Data.contractsCompleted;
            int writes = m_Storage.WriteCount;
            int ended = 0;
            m_Director.MissionEnded += _ => ended++;
            var message = new RunEndedMsg
            {
                outcome = MissionOutcome.ContractComplete, quota = m_Director.Session.Quota,
                depositedValue = item.Value, depositedCount = 1, bonus = 400, payout = item.Value + 400,
                oxygenRemainingSeconds = 200f, elapsedSeconds = 321f,
            };
            var result = apply.ApplyRunEnded(in message);
            Assert.IsNotNull(result);
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            Assert.AreEqual(credits + item.Value + 400, m_Director.Profile.Data.totalCredits);
            Assert.AreEqual(completed + 1, m_Director.Profile.Data.contractsCompleted);
            Assert.Greater(m_Storage.WriteCount, writes, "This headset's own save was written.");
            Assert.AreEqual(321f, m_Director.Session.ElapsedSeconds);
            Assert.AreEqual(1, ended);

            // Twice is still once.
            Assert.IsNull(apply.ApplyRunEnded(in message));
            Assert.AreEqual(credits + item.Value + 400, m_Director.Profile.Data.totalCredits);
            Assert.AreEqual(1, ended);

            // The rig that was left in the wreck is brought home, and the hatch shuts behind it.
            for (float until = Time.unscaledTime + 5f; Time.unscaledTime < until && !m_Player.InSafeZone;) yield return null;
            Assert.IsTrue(m_Player.InSafeZone, "A player still out in the wreck when the run ends comes home.");
            for (float until = Time.unscaledTime + 5f; Time.unscaledTime < until && m_Director.tugHatch.IsOpen;) yield return null;
            Assert.IsFalse(m_Director.tugHatch.IsOpen);

            Assert.IsTrue(apply.ApplyReturnedToHub());
            Assert.AreEqual(MissionPhase.Hub, m_Director.Phase);
            Assert.IsFalse(apply.ApplyReturnedToHub());
        }

        [UnityTest]
        public IEnumerator Client_LinkLossAbortsToTheHub_BankingNothing_AndAnOfflineContractThenPlaysAsBefore()
        {
            var run = HostRun(6);
            var net = BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            Assert.IsTrue(apply.ApplyRunStart(in run));
            yield return Frames(2);
            var item = m_Director.RunItems.First(i => i != null && !i.IsMounted);
            Assert.IsTrue(apply.ApplyItemDeposited((byte)item.NetIndex, 1, item.Value, 0, 0));
            m_Player.locomotion.TeleportTo(InTheWreck);
            yield return Frames(4);
            int credits = m_Director.Profile.Data.totalCredits;
            int ended = 0;
            m_Director.MissionEnded += _ => ended++;

            Assert.IsTrue(apply.AbortRun());
            Assert.AreEqual(MissionPhase.Hub, m_Director.Phase);
            Assert.IsNull(m_Director.Session.Result);
            Assert.AreEqual(credits, m_Director.Profile.Data.totalCredits, "Nothing is banked from a run that was cut off.");
            Assert.AreEqual(0, ended);
            Assert.AreEqual(0, m_Director.RunItems.Count);
            Assert.IsFalse(apply.AbortRun());
            for (float until = Time.unscaledTime + 5f; Time.unscaledTime < until && !m_Player.InSafeZone;) yield return null;
            Assert.IsTrue(m_Player.InSafeZone, "Back in the tug.");
            yield return Frames(2);
            Assert.IsTrue(m_Director.locker.slots.All(s => s.childCount == 0), "The locker is empty again.");

            // The session ends; single-player is exactly as it was.
            m_Director.Net = null;
            m_Player.AssignId(PlayerId.Local, false);
            m_Director.ClearLocalAway();
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            yield return Frames(2);
            Assert.IsTrue(m_Director.CreatureAboard);
            foreach (var loose in LooseItems())
            {
                Assert.IsTrue(loose.SimulatedHere);
                Assert.IsFalse(loose.HostOnly);
                Assert.IsTrue(loose.grab.enabled);
                Assert.IsFalse(loose.Body.isKinematic);
                Assert.IsNull(loose.GetComponent<CoopItemReplica>());
            }
            foreach (var each in LooseItems().Where(i => !i.definition.bonusSpawn).OrderByDescending(i => i.Value))
            {
                if (m_Director.Session.QuotaMet) break;
                Assert.IsTrue(m_Director.locker.TryAccept(each));
            }
            Assert.IsTrue(m_Director.Session.QuotaMet);
            var result = m_Director.Extract(PlayerId.Local);
            Assert.IsNotNull(result);
            Assert.AreEqual(MissionOutcome.ContractComplete, result.Outcome);
            Assert.AreEqual(credits + result.Payout, m_Director.Profile.Data.totalCredits);
            CollectionAssert.IsEmpty(net.calls);
        }

        [UnityTest]
        public IEnumerator Client_DoorsPanelsAndWorldState_FollowTheHost_ACutHereIsOnlyReported_AndAwayIsNotAPause()
        {
            var run = HostRun(3);
            var net = BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            Assert.IsTrue(apply.ApplyRunStart(in run));
            yield return Frames(2);

            // Cutting here burns the weld at once and reports it; the panel waits for the host.
            var panel = m_Director.cuttables.First(c => c != null && !c.isTraining && c.TotalCuts > 0);
            byte pid = (byte)m_Director.NetIds.Panels.IndexOf(panel.panelId);
            for (int k = 0; k < panel.TotalCuts; k++) panel.ApplyCut(panel.cutPoints[k], 10f, 1f, m_Player);
            Assert.AreEqual(panel.TotalCuts, panel.CutCount);
            Assert.IsFalse(panel.IsOpen);
            CollectionAssert.AreEqual(Enumerable.Range(0, panel.TotalCuts).Select(k => "cut " + panel.panelId + " " + k), net.calls);
            net.calls.Clear();

            // The host's PanelOpened, with its push.
            var push = new NetVec3(0f, 0.5f, 0.25f);
            Assert.IsTrue(apply.ApplyPanelOpened(pid, 2, push, NetVec3.Zero));
            Assert.IsTrue(panel.IsOpen);
            Assert.IsTrue(m_Director.Session.IsPanelOpen(panel.panelId));
            Assert.AreEqual(CoopConvert.ToUnity(push), panel.LastPlateImpulse);
            Assert.IsFalse(apply.ApplyPanelOpened(pid, 2, push, NetVec3.Zero));

            // A weld the host reports, and a door.
            var second = Enumerable.Range(0, m_Director.NetIds.Panels.Count).Select(m_Director.PanelAt)
                .First(c => c != null && c != panel && !c.isTraining && c.TotalCuts > 1);
            byte sid = (byte)m_Director.NetIds.Panels.IndexOf(second.panelId);
            Assert.IsTrue(apply.ApplyPointCut(sid, 1));
            Assert.IsFalse(apply.ApplyPointCut(sid, 1));
            Assert.IsTrue(second.cutPoints[1].IsCut);
            Assert.IsFalse(second.IsOpen);
            var door = m_Director.wreckDoors.First(d => d != null && !d.locked && !d.IsOpen);
            byte did = (byte)m_Director.NetIds.Doors.IndexOf(door.doorId);
            int noise = m_Director.noise.EmittedCount;
            Assert.IsTrue(apply.ApplyDoorState(did, true));
            Assert.IsTrue(door.IsOpen);
            Assert.IsFalse(apply.ApplyDoorState(did, true));
            Assert.AreEqual(noise, m_Director.noise.EmittedCount);

            // The world state only moves things toward it.
            var other = Enumerable.Range(0, m_Director.NetIds.Doors.Count).Select(m_Director.DoorAt)
                .First(d => d != null && d != door && d != m_Director.tugHatch && !d.IsOpen);
            int oid = m_Director.NetIds.Doors.IndexOf(other.doorId);
            var state = new WorldStateMsg();
            state.doorOpenMask = (ushort)((1 << oid) | (1 << did));
            state.SetWeldDone(m_Director.NetIds.CutPointOffset(sid) + 0);
            apply.ApplyWorldState(in state);
            Assert.IsTrue(other.IsOpen);
            Assert.IsTrue(second.cutPoints[0].IsCut);
            Assert.IsFalse(second.IsOpen);
            int hatch = m_Director.NetIds.Doors.IndexOf(m_Director.tugHatch.doorId);
            Assert.AreEqual(0, state.doorOpenMask & (1 << hatch));
            Assert.IsFalse(m_Director.tugHatch.IsOpen, "A door the host says is shut, is shut.");
            CollectionAssert.IsEmpty(net.calls);

            // Taking the headset off makes this player Away (and says so once); it does not pause anything.
            m_Director.SetLocalAway(HazardPauseReason.TrackingLost, true);
            m_Director.SetLocalAway(HazardPauseReason.ApplicationFocus, true);
            Assert.IsTrue(m_Player.IsAway);
            Assert.IsFalse(m_Director.HazardsPaused);
            m_Director.SetLocalAway(HazardPauseReason.TrackingLost, false);
            Assert.IsTrue(m_Player.IsAway);
            m_Director.SetLocalAway(HazardPauseReason.ApplicationFocus, false);
            Assert.IsFalse(m_Player.IsAway);
            CollectionAssert.AreEqual(new[] { "pause True", "pause False" }, net.calls);
        }

        // ================================================================== mirror

        [UnityTest]
        public IEnumerator Mirror_ABookkeeperFedOnlyByTheHostsBroadcasts_KeepsTheSameBooksAtEveryStep_AndSoDoesThisSceneAsAClient()
        {
            var crew = AddRemotePlayer(1, m_Director.hubSpawn.position);
            var rec = Host();
            var mirrorProfile = new ProfileService(new MemorySaveStorage(), m_Director.Config.maxOxygenUpgradeLevel);
            rec.mirror = new CoopSessionMirror(m_Director.NetIds, MissionSession.FromConfig(m_Director.Config), mirrorProfile);
            rec.host = m_Director.Session;
            var host = m_Director.Session;
            var mirror = rec.mirror;
            yield return Frames(2);

            void Same(string step)
            {
                Assert.IsEmpty(rec.mismatches, string.Join("\n", rec.mismatches));
                Assert.AreEqual(SessionDigest.Describe(host), SessionDigest.Describe(mirror.Session), step);
                Assert.AreEqual(SessionDigest.Compute(host), SessionDigest.Compute(mirror.Session), step);
            }

            // Start, at a crew member's request.
            Assert.IsTrue(m_Rules.HostStartRequest(1));
            yield return Frames(2);
            Same("start");
            var loose = LooseItems().Where(i => !i.definition.bonusSpawn).ToList();
            var first = loose.First(i => i.definition.category == SalvageCategory.SmallScrap);
            var second = loose.First(i => i != first && i.definition.fragility <= 0f);
            var delicate = loose.Where(i => i != first && i != second && i.definition.fragility > 0f).OrderByDescending(i => i.definition.fragility).First();
            var stray = loose.First(i => i != first && i != second && i != delicate);

            // Pickups: one by crew 1 (a remote hold), one by the host.
            first.SetRemoteHolder(new PlayerId(1));
            Same("collected by crew 1");
            first.SetRemoteHolder(PlayerId.None);
            Assert.IsTrue(m_Director.NotifySalvageCollected(second, m_Director.LocalPlayerId));
            Same("collected by the host");

            // A knock.
            Assert.Greater(delicate.ApplyImpact(7f), 0);
            Same("damaged");

            // Deposits: an ordinary one, a trick shot, the damaged one.
            Assert.IsTrue(m_Director.locker.TryAccept(first));
            Same("deposit");
            Vector3 far = m_Director.locker.zone.position + Vector3.back * (m_Director.Config.trickShotDistance + 1f);
            second.RegisterThrow(Vector3.forward * 5f, null, far);
            Assert.IsTrue(m_Director.locker.TryAccept(second));
            Assert.Greater(host.TrickBonus, 0, "Test set-up: that was a trick shot.");
            Same("trick shot");
            Assert.IsTrue(m_Director.locker.TryAccept(delicate));
            Same("damaged deposit");
            Assert.AreEqual(3, rec.Count("deposited"));
            Assert.AreEqual(1, rec.Count("deposited " + first.NetIndex + " by 1 "), "The deposit is the carrier's.");

            // A panel cut open by crew reports.
            var panel = m_Director.cuttables.First(c => c != null && !c.isTraining && !c.IsOpen && c.TotalCuts > 0);
            byte pid = (byte)m_Director.NetIds.Panels.IndexOf(panel.panelId);
            crew.SetRemoteHead(panel.transform.position);
            for (int k = 0; k < panel.TotalCuts; k++) m_Rules.HostPointCutReport(pid, (byte)k, 1);
            Assert.IsTrue(panel.IsOpen);
            Same("panel");
            crew.SetRemoteHead(m_Director.hubSpawn.position + Vector3.up * 1.6f); // back aboard

            // Time and air, as the clock carries them.
            m_Director.TickMission(5f, false);
            m_Rules.ReadClock(out float elapsed, out float oxygen, out _);
            Assert.IsTrue(mirror.ApplyClock(elapsed, oxygen));
            Assert.AreEqual(SessionDigest.Compute(host, true), SessionDigest.Compute(mirror.Session, true), "clock");

            // An item lost out of bounds comes back, and everyone is told where.
            stray.Body.linearVelocity = Vector3.zero;
            stray.Body.position = new Vector3(0f, 0f, 500f);
            stray.transform.position = new Vector3(0f, 0f, 500f);
            yield return Seconds(1.2f);
            Assert.IsTrue(m_Director.bounds.Contains(stray.transform.position));
            Assert.AreEqual(1, rec.log.Count(l => l == "recovered " + stray.NetIndex));

            // Extraction: everyone aboard.
            var result = m_Rules.HostExtractRequest(1);
            Assert.IsNotNull(result);
            Same("extracted");
            Assert.AreEqual(result.Payout, mirror.CreditsBanked, "The mirror's own save is paid the full payout...");
            Assert.AreEqual(result.Payout, mirrorProfile.Data.totalCredits);
            Assert.IsTrue(m_Director.ReturnToHub());
            Same("hub");
            Assert.AreEqual(result.Payout, mirrorProfile.Data.totalCredits, "...once.");

            // ---- the same tape played into this very scene, now a client of that host
            m_Rules.Dispose();
            m_Rules = null;
            Object.Destroy(crew.gameObject);
            yield return Frames(2);
            BecomeClient(2);
            var apply = new CoopClientApply(m_Director);
            int credits = m_Director.Profile.Data.totalCredits;
            Assert.AreEqual(MissionPhase.Hub, m_Director.Phase);
            int checkedSteps = 0;
            foreach (var entry in rec.tape)
            {
                entry.replay(apply);
                if (!entry.ledger) continue;
                checkedSteps++;
                Assert.AreEqual(entry.hostDigest, SessionDigest.Compute(m_Director.Session),
                    "replaying '" + entry.text + "': " + SessionDigest.Describe(m_Director.Session));
            }
            Assert.GreaterOrEqual(checkedSteps, 10);
            Assert.AreEqual(credits + result.Payout, m_Director.Profile.Data.totalCredits, "This headset is paid once for the same run.");
            Assert.AreEqual(MissionPhase.Hub, m_Director.Phase);
        }
    }
}

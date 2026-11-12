using System.Collections;
using System.Collections.Generic;
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
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Co-op stage 0 in the real scene: the seams exist and single-player still runs through them.
    /// There is no networking here. A "crew member" is a head-only stand-in registered with the director,
    /// and where a session is needed it is a stub that only writes down what the game asked of it.
    /// </summary>
    public class NetSeamsPlayTests
    {
        MissionDirector m_Director;
        PlayerContext m_Player;
        MemorySaveStorage m_Storage;

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
            if (MissionDirector.Active != null) MissionDirector.Active.Net = null;
            MissionDirector.StorageOverride = null;
            MissionDirector.SeedOverride = null;
            MissionDirector.CreatureOverride = null;
            MissionDirector.MapOverride = null;
            MissionDirector.ConditionOverride = null;
            BlackoutController.DamageOverride = null;
        }

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        static IEnumerator Physics(int steps)
        {
            for (int i = 0; i < steps; i++) yield return new WaitForFixedUpdate();
        }

        /// <summary>Another crew member as the director sees one: a PlayerContext with only a head, no rig behind it.</summary>
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

        /// <summary>What a session would be to the game: it only records what it was asked.</summary>
        sealed class StubNet : IMissionNet
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

        SalvageItem[] LooseItems() => m_Director.Items.Where(i => i != null && !i.IsSecured && !i.IsMounted && i.definition != null).ToArray();

        struct Spawned
        {
            public string id, definition;
            public int value;
            public bool mounted;
            public Vector3 at;
            public override string ToString() => id + " " + definition + " " + value + (mounted ? " mounted " : " loose ") + at.ToString("F3");
        }

        List<string> SpawnedNow()
        {
            var list = new List<string>();
            foreach (var item in m_Director.RunItems)
            {
                list.Add(item == null ? "(missing)" : new Spawned
                {
                    id = item.SalvageId, definition = item.definition.id, value = item.Value, mounted = item.IsMounted, at = item.SpawnPosition,
                }.ToString());
            }
            return list;
        }

        // ------------------------------------------------------------------ run parameters

        [UnityTest]
        public IEnumerator BeginRun_FromTheParametersOfAnEarlierContract_RebuildsItExactly_OnADirectorThatHasPlayedOthersSince()
        {
            Assert.IsNull(m_Director.Net, "Single-player has no session.");
            MissionDirector.SeedOverride = 777;
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            yield return Frames(2);

            // What StartMission decided, as numbers.
            var run = m_Director.CurrentRun;
            Assert.AreEqual(777, run.seed);
            Assert.AreEqual(1, run.runIndex);
            Assert.AreEqual(0, run.mapIndex);
            Assert.AreEqual(0, run.creatureIndex);
            Assert.AreEqual((byte)ContractCondition.Standard, run.condition);
            Assert.AreEqual(m_Director.Session.Quota, run.quota);
            Assert.AreEqual(m_Director.Session.Oxygen.Capacity, run.oxygenCapacitySeconds);
            Assert.AreEqual(1f, run.bonusMultiplier);
            Assert.AreEqual(RunStartParams.HazardBlackouts, run.hazardFlags & RunStartParams.HazardBlackouts);
            Assert.AreEqual(run.ComputePlanHash(), run.planHash);
            Assert.IsTrue(m_Director.CreatureAboard);

            // Net id = place in the item list; the hidden duck is the explicit last entry.
            Assert.AreEqual(run.items.Length, m_Director.RunItems.Count);
            Assert.AreEqual(m_Director.Items.Count, m_Director.RunItems.Count);
            for (int i = 0; i < run.items.Length; i++)
            {
                var item = m_Director.RunItems[i];
                Assert.IsNotNull(item, "Item " + i + " was not spawned.");
                Assert.AreSame(m_Director.Items[i], item);
                Assert.AreEqual(i, item.NetIndex);
                Assert.AreSame(item, m_Director.RunItemAt(i));
                Assert.AreEqual(m_Director.NetIds.SalvageIdFor(run.runIndex, run.items[i]), item.SalvageId);
                Assert.AreEqual(m_Director.NetIds.Definitions[run.items[i].definitionIndex], item.definition.id);
                Assert.AreEqual(run.items[i].value, item.Value);
                Assert.AreEqual(i == run.items.Length - 1, (run.items[i].flags & RunItem.FlagBonus) != 0, "Only the last entry is the bonus item.");
            }
            Assert.IsNull(m_Director.RunItemAt(run.items.Length));
            Assert.IsNull(m_Director.RunItemAt(-1));
            var duck = m_Director.RunItems[run.items.Length - 1];
            Assert.IsTrue(duck.definition.bonusSpawn);
            StringAssert.StartsWith("run1/", duck.SalvageId);
            StringAssert.EndsWith("#bonus", duck.SalvageId);
            CollectionAssert.AreEqual(m_Director.LastPlan.Assignments.Select(a => a.salvageId).ToList(),
                m_Director.RunItems.Take(run.items.Length - 1).Select(i => i.SalvageId).ToList());

            var firstSpawn = SpawnedNow();
            uint firstDigest = SessionDigest.Compute(m_Director.Session);
            string firstLedger = SessionDigest.Describe(m_Director.Session);
            var firstPlan = m_Director.LastPlan;

            // Two more contracts, one of them on another wreck: a different history.
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            MissionDirector.MapOverride = 1;
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            yield return Frames(2);
            Assert.AreSame(m_Director.maps[1], m_Director.ActiveMap);
            Assert.IsTrue(m_Director.Items.All(i => i.SalvageId.StartsWith("run2/")));
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            MissionDirector.MapOverride = 0;
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            Assert.AreEqual(3, m_Director.Session.RunIndex);
            MissionDirector.MapOverride = 2; // would be used if anything were re-rolled
            MissionDirector.CreatureOverride = 2;
            yield return Frames(2);

            // The same contract again, from its parameters alone.
            Assert.IsTrue(m_Director.BeginRun(in run));
            yield return Frames(2);
            Assert.AreEqual(1, m_Director.Session.RunIndex, "The run number comes from the parameters.");
            Assert.AreSame(m_Director.maps[0], m_Director.ActiveMap);
            Assert.AreSame(m_Director.enemies[0], m_Director.ActiveEnemy);
            Assert.AreEqual(ContractCondition.Standard, m_Director.Condition);
            CollectionAssert.AreEqual(firstSpawn, SpawnedNow(), "Every item must come back with the same id, kind, value and place.");
            Assert.AreEqual(firstDigest, SessionDigest.Compute(m_Director.Session), firstLedger + "\nvs\n" + SessionDigest.Describe(m_Director.Session));
            Assert.IsTrue(m_Director.tugHatch.IsOpen);
            Assert.IsTrue(m_Director.ActiveEnemy.IsRunning);

            // The loot plan is rebuilt from the list when this machine did not plan the run itself.
            var rebuilt = m_Director.LastPlan;
            Assert.AreNotSame(firstPlan, rebuilt);
            Assert.AreEqual(firstPlan.Quota, rebuilt.Quota);
            Assert.AreEqual(firstPlan.TotalValue, rebuilt.TotalValue);
            Assert.AreEqual(firstPlan.UngatedValue, rebuilt.UngatedValue);
            CollectionAssert.AreEqual(firstPlan.Assignments.Select(a => a.salvageId + "|" + a.spawnPointId + "|" + a.definitionId + "|" + a.value + "|" + a.category).ToList(),
                rebuilt.Assignments.Select(a => a.salvageId + "|" + a.spawnPointId + "|" + a.definitionId + "|" + a.value + "|" + a.category).ToList());

            // A run in progress is not started over.
            Assert.IsFalse(m_Director.BeginRun(in run));
            Assert.AreEqual(firstSpawn.Count, m_Director.Items.Count(i => i != null));

            // Nothing is worked out again from the seed or from this build's own catalog. The same contract
            // with one web the other way round and the duck at another price is exactly that, offline too.
            var wires = m_Director.ActiveMap.tripwires;
            foreach (var w in wires)
                Assert.AreEqual((run.wireArmedMask & (1u << m_Director.NetIds.Wires.IndexOf(w.wireId))) != 0, w.Armed, w.wireId);
            var flipped = wires.First(w => w.wireId == "a_corridor_mid");
            int flippedIndex = m_Director.NetIds.Wires.IndexOf(flipped.wireId);
            bool wasArmed = flipped.Armed;
            Assert.AreEqual(wasArmed, flipped.WouldArm(run.seed, ContractCondition.Standard), "Test set-up: the original mask is the seed's own roll.");
            var altered = run;
            altered.items = (RunItem[])run.items.Clone();
            altered.wireArmedMask = run.wireArmedMask ^ (1u << flippedIndex);
            int duckIndex = altered.items.Length - 1;
            int listPrice = m_Director.RunItems[duckIndex].definition.creditValue;
            Assert.AreEqual(listPrice, run.items[duckIndex].value, "Offline the duck is sent at its list price.");
            altered.items[duckIndex].value = (ushort)(listPrice + 7);
            altered.planHash = altered.ComputePlanHash();
            Assert.AreNotEqual(run.planHash, altered.planHash);
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            Assert.IsTrue(m_Director.BeginRun(in altered));
            yield return Frames(2);
            Assert.AreEqual(!wasArmed, flipped.Armed, "The web follows the parameters, not the seed.");
            Assert.AreEqual(flipped.Armed, flipped.visual.activeSelf);
            foreach (var w in wires.Where(w => w != flipped))
                Assert.AreEqual((run.wireArmedMask & (1u << m_Director.NetIds.Wires.IndexOf(w.wireId))) != 0, w.Armed, w.wireId);
            var pricedDuck = m_Director.RunItems[duckIndex];
            Assert.IsTrue(pricedDuck.definition.bonusSpawn);
            Assert.AreEqual(listPrice + 7, pricedDuck.Value, "The bonus item is worth what the parameters say.");
            Assert.AreEqual(listPrice + 7, pricedDuck.FullValue);
            Assert.IsTrue(m_Director.Session.TryGetSalvage(pricedDuck.SalvageId, out var duckRecord));
            Assert.AreEqual(pricedDuck.Value, duckRecord.Value, "The item and the ledger must agree.");
            Assert.IsFalse(pricedDuck.IsMounted, "It still floats free.");
        }

        [UnityTest]
        public IEnumerator StartMission_NumbersLootSpotsThatChangedAfterTheIdTableWasBuilt_SoNoPlannedItemIsLost()
        {
            // The id table is built on first use...
            var stale = m_Director.NetIds;
            var wreck = m_Director.maps[0];
            Assert.Greater(wreck.spawnPoints.Length, 5);
            Assert.IsTrue(wreck.spawnPoints.All(p => stale.Points.Contains(p.pointId)));

            // ...and then the wreck's loot spots get other ids (a scene put together or changed by hand).
            foreach (var point in wreck.spawnPoints) point.pointId = "moved_" + point.pointId;
            Assert.IsFalse(wreck.spawnPoints.Any(p => stale.Points.Contains(p.pointId)));

            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            yield return Frames(2);
            var ids = m_Director.NetIds;
            Assert.AreNotSame(stale, ids, "The table must be numbered again when it no longer covers the wreck.");
            foreach (var point in wreck.spawnPoints)
                Assert.AreSame(point, m_Director.PointAt(ids.Points.IndexOf(point.pointId)), point.pointId);

            // Everything the planner counted towards the quota is really there.
            var plan = m_Director.LastPlan;
            Assert.Greater(plan.Assignments.Count, 3);
            Assert.IsTrue(m_Director.RunItems.All(i => i != null));
            var planned = m_Director.RunItems.Where(i => !i.definition.bonusSpawn).ToList();
            CollectionAssert.AreEqual(plan.Assignments.Select(a => a.salvageId).ToList(), planned.Select(i => i.SalvageId).ToList());
            Assert.IsTrue(planned.All(i => i.SalvageId.StartsWith("run1/moved_")));
            Assert.AreEqual(plan.TotalValue, planned.Sum(i => i.Value));
            Assert.AreEqual(1, m_Director.RunItems.Count(i => i.definition.bonusSpawn), "The bonus item too.");

            // A table that still covers everything is left alone.
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            Assert.AreSame(ids, m_Director.NetIds);
        }

        [UnityTest]
        public IEnumerator AContractWithNoCreatureAboard_RunsFromStartToPayout_WithItAsleepAtTheDock()
        {
            // An ordinary contract first. Out in the wreck something is staged almost at once (the test
            // removes the usual wait), and the creature gets up and about.
            var scares = m_Director.GetComponent<ScareDirector>();
            scares.firstDelay = 0f;
            scares.interval = Vector2.zero;
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            yield return Frames(2);
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 12f));
            yield return Frames(4);
            Assert.IsFalse(m_Player.InSafeZone);
            for (int i = 0; i < 240 && scares.EventCount == 0; i++) yield return null;
            Assert.Greater(scares.EventCount, 0, "Test set-up: with a creature aboard, scares are staged for a player in the wreck.");
            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            creature.transform.position += new Vector3(0f, 0f, -3f);
            Assert.AreEqual(EnemyState.Patrol, creature.State);
            // The board in the tug and the wrist both say what is aboard.
            var board = Object.FindFirstObjectByType<MissionBoard>();
            var wrist = m_Player.GetComponentInChildren<WristDisplay>(true);
            board.Refresh();
            wrist.Refresh();
            StringAssert.Contains("Aboard: " + creature.displayName, board.body.text);
            StringAssert.Contains("DRONE", wrist.threatText.text);
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            yield return Frames(2);

            var run = m_Director.BuildRunParams();
            Assert.AreEqual(0, run.creatureIndex, "Test set-up: the roster pick is forced to the first creature.");
            Assert.AreEqual(RunStartParams.HazardBlackouts, run.hazardFlags, "Offline every contract has its blackouts.");
            run.creatureIndex = RunStartParams.NoCreature;
            run.hazardFlags = 0; // and no scheduled blackouts either: the quietest contract there is
            run.planHash = run.ComputePlanHash();
            Assert.IsTrue(m_Director.BeginRun(in run));
            yield return Frames(2);

            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.IsFalse(m_Director.blackout.IsScheduled, "The parameters said no blackouts.");
            Assert.IsFalse(m_Director.CreatureAboard);
            Assert.AreSame(creature, m_Director.ActiveEnemy, "Everything that reads the creature still finds one.");
            Assert.IsFalse(creature.IsRunning);
            Assert.AreEqual(EnemyState.Dormant, creature.State);
            Assert.Less(Vector3.Distance(creature.transform.position, m_Director.ActiveMap.dock.position), 0.01f, "It should be parked at the dock.");

            // Nobody is told something is aboard when nothing is.
            board.Refresh();
            wrist.Refresh();
            StringAssert.Contains("CONTRACT ACTIVE", board.title.text);
            StringAssert.DoesNotContain("Aboard:", board.body.text);
            Assert.AreEqual("", wrist.threatText.text, "No threat line for a creature that is not aboard.");

            // Nothing wakes it: not time, not noise.
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 12f));
            yield return Frames(4);
            Assert.IsFalse(m_Player.InSafeZone);
            int scaresBefore = scares.EventCount;
            for (int i = 0; i < 80; i++)
            {
                if (i % 10 == 0) m_Director.noise.Emit(new NoiseEvent(m_Player.HeadPosition, 1.5f, NoiseKind.Impact, PlayerId.Local));
                creature.Step(0.5f);
            }
            yield return Frames(30);
            Assert.AreEqual(EnemyState.Dormant, creature.State);
            Assert.IsFalse(creature.IsScaring);
            Assert.Less(Vector3.Distance(creature.transform.position, m_Director.ActiveMap.dock.position), 0.01f);
            Assert.AreEqual(scaresBefore, scares.EventCount, "With nothing aboard, nothing is staged either.");
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);

            // The whole loop still works: fill the locker, go home, get paid.
            m_Player.locomotion.TeleportTo(m_Director.hubSpawn.position, m_Director.hubSpawn.rotation);
            yield return Frames(4);
            foreach (var item in LooseItems().Where(i => !i.definition.bonusSpawn).OrderByDescending(i => i.Value))
            {
                if (m_Director.Session.QuotaMet) break;
                Assert.IsTrue(m_Director.locker.TryAccept(item));
            }
            Assert.IsTrue(m_Director.Session.QuotaMet);
            int creditsBefore = m_Director.Profile.Data.totalCredits;
            var result = m_Director.Extract(PlayerId.Local);
            Assert.IsNotNull(result);
            Assert.AreEqual(MissionOutcome.ContractComplete, result.Outcome);
            Assert.Greater(result.Payout, 0);
            Assert.AreEqual(creditsBefore + result.Payout, m_Director.Profile.Data.totalCredits);
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            yield return Frames(2);

            // And the next ordinary contract has something aboard again.
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            Assert.IsTrue(m_Director.CreatureAboard);
            Assert.IsTrue(m_Director.ActiveEnemy.IsRunning);
            Assert.IsTrue(m_Director.Config.blackoutsEnabled);
            Assert.IsTrue(m_Director.blackout.IsScheduled, "An ordinary contract schedules its blackouts as before.");
            board.Refresh();
            wrist.Refresh();
            StringAssert.Contains("Aboard: " + m_Director.ActiveEnemy.displayName, board.body.text);
            StringAssert.Contains("DRONE", wrist.threatText.text);
        }

        // ------------------------------------------------------------------ identity

        [UnityTest]
        public IEnumerator Offline_TheOneRigIsTheLocalPlayer_AndACrewStandIn_IsCountedButNeverMistakenForIt()
        {
            Assert.IsNull(m_Director.Net);
            Assert.AreSame(m_Player, m_Director.LocalPlayer);
            Assert.AreSame(m_Director.Players[0], m_Director.LocalPlayer);
            Assert.AreEqual(PlayerId.Local, m_Director.LocalPlayerId);
            Assert.AreEqual(PlayerId.Local, m_Player.Id);
            Assert.IsFalse(m_Player.IsRemote);
            Assert.IsFalse(m_Player.IsAway);
            Assert.AreSame(m_Player, m_Director.FindPlayer(new PlayerId(3)), "Offline an unknown id still means the only player.");

            // A stand-in keeps its own handedness: this headset's saved settings are not pushed onto it.
            m_Director.Profile.Data.Handedness = Handedness.Left;
            var crew = AddRemotePlayer(2, m_Director.hubSpawn.position);
            yield return Frames(2);
            Assert.AreEqual(2, m_Director.Players.Count);
            Assert.IsTrue(crew.IsRemote);
            Assert.AreEqual(new PlayerId(2), crew.Id);
            Assert.AreEqual(Handedness.Right, crew.Handedness, "Registering must not apply the local profile to a stand-in.");
            m_Director.ApplyAndSaveSettings();
            Assert.AreEqual(Handedness.Left, m_Player.Handedness);
            Assert.AreEqual(Handedness.Right, crew.Handedness, "Saving settings must not apply them to a stand-in.");
            Assert.AreSame(m_Player, m_Director.LocalPlayer);
            Assert.AreSame(crew, m_Director.FindPlayer(new PlayerId(2)));
            Assert.AreSame(m_Player, m_Director.FindPlayer(PlayerId.Local));

            // The rules use the newest head position received, not where the avatar happens to be drawn.
            Vector3 drawnAt = crew.head.position;
            Assert.AreEqual(drawnAt, crew.HeadPosition);
            Vector3 inTheWreck = MapPoints.Freighter(0f, 1.6f, 14f);
            crew.SetRemoteHead(inTheWreck);
            Assert.AreEqual(inTheWreck, crew.HeadPosition);
            Assert.AreEqual(drawnAt, crew.head.position);

            // One shared tank: it drains while anyone who is present is outside the tug...
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            yield return Frames(3);
            Assert.IsTrue(m_Player.InSafeZone);
            Assert.IsFalse(crew.InSafeZone);
            var tank = m_Director.Session.Oxygen;
            float cost = m_Director.Config.exertionOxygenCost;
            Assert.Greater(cost, 0.05f);
            // Measured on a part-used tank. A batch run's frames are a millisecond or two long, and a
            // full ten-minute tank held in a float cannot count steps that small exactly: the rounding
            // alone moved the measured rate by a few percent on some runs. At one minute it is exact enough.
            tank.SetRemaining(60f);

            float Rate(float drained, float seconds) => seconds > 0f ? drained / seconds : 0f;
            float before = tank.Remaining, elapsed = 0f;
            for (int i = 0; i < 20; i++) { yield return null; elapsed += Time.deltaTime; }
            // (The local rig's own exertion also counts, and its hands are not always perfectly still, so
            // "calm" is a range; "working hard" below is exact, because the hardest worker sets the rate.)
            float calm = Rate(before - tank.Remaining, elapsed);
            Assert.That(calm, Is.InRange(0.98f, 1f + cost * 0.5f), "A calm crew member outside the tug uses oxygen at about the normal rate.");

            // ...faster when the one out there is working hard (their exertion arrives with their avatar)...
            crew.RemoteExertion = 1f;
            yield return null;
            before = tank.Remaining; elapsed = 0f;
            for (int i = 0; i < 20; i++) { yield return null; elapsed += Time.deltaTime; }
            float hard = Rate(before - tank.Remaining, elapsed);
            Assert.AreEqual(1f + cost, hard, 0.02f, "The hardest-working crew member sets the rate.");
            Assert.Greater(hard, calm + cost * 0.4f);

            // ...and nobody leaves without them.
            Assert.AreEqual(1 << 2, m_Director.MissingAboardMask());
            Assert.IsNull(m_Director.Extract(PlayerId.Local), "Extraction waits for everyone who is present.");
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);

            // Someone who is away (headset off, link silent) is not waited for and uses no air.
            crew.IsAway = true;
            yield return null;
            before = tank.Remaining;
            yield return Frames(20);
            Assert.AreEqual(before, tank.Remaining, "An away crew member must not drain the tank.");
            Assert.AreEqual(0, m_Director.MissingAboardMask());

            // Back, and aboard: now the tug can leave.
            crew.IsAway = false;
            Assert.AreEqual(1 << 2, m_Director.MissingAboardMask());
            crew.SetRemoteHead(m_Director.hubSpawn.position + Vector3.up * 1.6f);
            Assert.AreEqual(0, m_Director.MissingAboardMask());
            Assert.IsNotNull(m_Director.Extract(PlayerId.Local));
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);

            // When the stand-in goes, the director forgets it.
            Object.Destroy(crew.gameObject);
            yield return Frames(2);
            Assert.AreEqual(1, m_Director.Players.Count);
            Assert.AreSame(m_Player, m_Director.LocalPlayer);
        }

        // ------------------------------------------------------------------ the creature and a second player

        [UnityTest]
        public IEnumerator ACrewMemberSafeInTheTug_DoesNotCallOffAChaseOfSomeoneElse()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            var crew = AddRemotePlayer(1, m_Director.hubSpawn.position);
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 19.6f));
            yield return Frames(4);
            Assert.AreEqual(2, m_Director.Players.Count);
            Assert.IsTrue(crew.InSafeZone);
            Assert.IsFalse(m_Player.InSafeZone);

            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            creature.patrolRoute = new EnemyWaypoint[0]; // it stays where it is put until it has something to hunt
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            UnityEngine.Physics.SyncTransforms();

            // Close in front of the local player: it notices them and starts a chase.
            Vector3 head = m_Player.HeadPosition;
            creature.transform.position = new Vector3(head.x, creature.hoverHeight, head.z + 2f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.back);
            for (int i = 0; i < 100 && creature.State != EnemyState.Chase && !creature.IsScaring; i++) creature.Step(0.05f);
            Assert.AreEqual(EnemyState.Chase, creature.State, "Test set-up: it should be chasing the local player.");
            Assert.IsFalse(creature.IsScaring);

            // The quarry is suddenly a long way off and out of sight, and the crew member standing in the
            // tug is the nearer of the two in a straight line.
            creature.transform.position = new Vector3(0f, creature.hoverHeight, 10.5f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.forward);
            float toQuarry = Vector3.Distance(m_Player.HeadPosition, creature.transform.position);
            float toCrew = Vector3.Distance(crew.HeadPosition, creature.transform.position);
            Assert.Greater(toQuarry, m_Director.Config.enemy.sightRange + 2f, "Test set-up: the quarry must be out of sight.");
            Assert.Less(toCrew, toQuarry - 1f, "Test set-up: the safe crew member must be the nearer one.");

            for (int i = 0; i < 10; i++)
            {
                creature.Step(0.05f);
                Assert.AreEqual(EnemyState.Chase, creature.State, "Someone safe in the tug must not end the hunt for someone who is not (step " + i + ").");
            }
            Assert.Greater(creature.Brain.LostSightSeconds, 0.4f, "It is hunting from memory, as it would with one player.");
            Assert.AreEqual(toCrew, creature.NearestPlayerDistance, 2f);

            // An away crew member right under its nose is not there to be hunted...
            creature.Brain.Sleep();
            creature.WakeNow();
            creature.transform.position = new Vector3(0f, creature.hoverHeight, 14f);
            Vector3 underItsNose = creature.transform.position + Vector3.forward * 1.5f + Vector3.up * 0.2f;
            crew.SetRemoteHead(underItsNose);
            crew.IsAway = true;
            yield return null;
            creature.transform.position = new Vector3(0f, creature.hoverHeight, 14f);
            Assert.IsFalse(crew.InSafeZone);
            for (int i = 0; i < 40; i++) creature.Step(0.05f);
            Assert.AreNotEqual(EnemyState.Chase, creature.State, "An away player must not be targeted.");
            Assert.IsFalse(creature.IsScaring);

            // ...but once they are back, a stand-in is hunted like anyone else.
            crew.IsAway = false;
            creature.transform.position = new Vector3(0f, creature.hoverHeight, 14f);
            crew.SetRemoteHead(underItsNose);
            for (int i = 0; i < 60 && creature.State != EnemyState.Chase && !creature.IsScaring; i++) creature.Step(0.05f);
            Assert.IsTrue(creature.State == EnemyState.Chase || creature.IsScaring, "A crew member's stand-in in the wreck is fair game.");
        }

        [UnityTest]
        public IEnumerator Jumpscare_OfACrewMemberWhoVanishesHalfWay_StillEndsTheRun_AtTheDeadline()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            var crew = AddRemotePlayer(1, MapPoints.Freighter(0f, 0f, 19.6f));
            yield return Frames(3);
            Assert.IsFalse(crew.InSafeZone);
            Assert.IsTrue(m_Player.InSafeZone, "The local player watches from the tug.");

            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            UnityEngine.Physics.SyncTransforms();
            Vector3 head = crew.HeadPosition;
            creature.transform.position = new Vector3(head.x, creature.hoverHeight, head.z + 1.6f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.back);
            for (int i = 0; i < 200 && !creature.IsScaring && m_Director.Phase == MissionPhase.Active; i++) creature.Step(0.05f);
            Assert.IsTrue(creature.IsScaring, "It should have caught the crew member standing in front of it.");
            float caughtAt = Time.unscaledTime;
            Assert.AreEqual(new PlayerId(1), creature.CatchVictim);
            float deadline = creature.ScareTotalSeconds + EnemyDrone.CatchDeadlineSlack;
            Assert.AreEqual(4.67f + 0.5f, deadline, 0.001f);

            // A bystander sees it happen to someone else: their own visor stays whole.
            for (float t = 0f; t < 3f && creature.ScarePhase < 2; t += Time.unscaledDeltaTime) yield return null;
            for (float t = 0f; t < 0.4f; t += Time.unscaledDeltaTime) yield return null;
            Assert.IsTrue(creature.IsScaring);
            Assert.AreEqual(2, creature.ScarePhase);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.AreEqual(0, m_Player.visor.Stage, "Only the victim's own visor cracks.");
            Assert.IsTrue(m_Director.blackout.IsBlackout);
            Assert.Less(Vector3.Distance(creature.transform.position, crew.HeadPosition), 0.8f, "It should be on its victim.");

            // The victim's stand-in is removed (they dropped out of the session).
            Object.Destroy(crew.gameObject);
            yield return Frames(3);
            Assert.AreEqual(1, m_Director.Players.Count);
            Assert.IsTrue(creature.IsScaring, "The catch is not over just because its victim is gone.");
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);

            float endedAt = -1f;
            for (float waited = 0f; waited < 10f; waited += Time.unscaledDeltaTime)
            {
                if (m_Director.Phase != MissionPhase.Active) { endedAt = Time.unscaledTime; break; }
                yield return null;
            }
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase, "The run must end even though the jumpscare could not finish.");
            Assert.AreEqual(MissionOutcome.FailedCaught, m_Director.Session.Result.Outcome);
            Assert.That(endedAt - caughtAt, Is.InRange(deadline - 0.05f, deadline + 0.75f), "It should end at the deadline, not before and not much after.");
            Assert.IsFalse(creature.IsScaring);
            Assert.AreEqual(0, creature.ScarePhase);
            Assert.AreEqual(PlayerId.None, creature.CatchVictim);
            Assert.IsFalse(m_Director.blackout.IsBlackout, "The lights must be restored afterwards.");
            Assert.AreEqual(0, m_Player.visor.Stage);

            // Exactly once: nothing fires again later, and the next contract starts cleanly.
            for (float t = 0f; t < 1f; t += Time.unscaledDeltaTime) yield return null;
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            yield return Frames(2);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.IsFalse(m_Director.ActiveEnemy.IsScaring);
        }

        [UnityTest]
        public IEnumerator Jumpscare_ThatIsStillPlayingWhenTheDeadlinePasses_IsNeverCutShort()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 19.6f));
            yield return Frames(4);
            Assert.IsFalse(m_Player.InSafeZone);

            var creature = m_Director.ActiveEnemy;
            creature.scareMaulSeconds = 0.4f; // a short one, to keep the test short
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            UnityEngine.Physics.SyncTransforms();
            Vector3 head = m_Player.HeadPosition;
            creature.transform.position = new Vector3(head.x, creature.hoverHeight, head.z + 1.6f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.back);
            for (int i = 0; i < 200 && !creature.IsScaring && m_Director.Phase == MissionPhase.Active; i++) creature.Step(0.05f);
            Assert.IsTrue(creature.IsScaring, "It should have caught the player standing in front of it.");
            float caughtAt = Time.unscaledTime;
            Assert.AreEqual(PlayerId.Local, creature.CatchVictim);
            float nominal = creature.ScareTotalSeconds;
            float deadline = nominal + EnemyDrone.CatchDeadlineSlack;

            // The jumpscare falls a second and a half behind (on a headset: a long stalled frame, or the
            // app being suspended part-way). Its deadline was set when the catch began and does not move.
            const float late = 1.5f;
            creature.scareAfterSeconds += late;
            while (Time.unscaledTime - caughtAt < deadline + 0.35f)
            {
                Assert.AreEqual(MissionPhase.Active, m_Director.Phase, "Cut short " + (Time.unscaledTime - caughtAt) + " s in (deadline " + deadline + ").");
                yield return null;
            }
            Assert.IsTrue(creature.IsScaring, "A jumpscare that is still playing is left to finish.");
            Assert.AreEqual(3, creature.ScarePhase, "It should be in its last beat: black, and the sound of it feeding.");
            Assert.AreEqual(new PlayerId(0), creature.CatchVictim);
            Assert.Greater(m_Player.visor.Stage, 0, "The victim's visor stays cracked until the end.");

            float endedAt = -1f;
            for (float waited = 0f; waited < 10f; waited += Time.unscaledDeltaTime)
            {
                if (m_Director.Phase != MissionPhase.Active) { endedAt = Time.unscaledTime; break; }
                yield return null;
            }
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            Assert.AreEqual(MissionOutcome.FailedCaught, m_Director.Session.Result.Outcome);
            Assert.That(endedAt - caughtAt, Is.InRange(nominal + late - 0.05f, nominal + late + 0.75f), "It ends when the jumpscare does.");
            Assert.IsFalse(creature.IsScaring);
            Assert.AreEqual(0, creature.ScarePhase);
            Assert.AreEqual(0, m_Player.visor.Stage, "The jumpscare cleaned up after itself.");

            // Exactly once: the watchdog does not fire afterwards.
            for (float t = 0f; t < 0.8f; t += Time.unscaledDeltaTime) yield return null;
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            Assert.AreEqual(1, m_Director.Profile.Data.runsStarted);
        }

        // ------------------------------------------------------------------ world seams, offline

        [UnityTest]
        public IEnumerator Locker_ReportsWhatItAccepted_AndApplyAccepted_RepeatsADepositInAGivenSlot_WithoutJudging()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            var locker = m_Director.locker;
            var items = LooseItems().Where(i => !i.definition.bonusSpawn).Take(3).ToArray();
            Assert.AreEqual(3, items.Length);
            var accepted = new List<string>();
            locker.Accepted += (item, trickBonus, slot) => accepted.Add(item.NetIndex + " " + trickBonus + " " + slot);
            Assert.AreEqual(0, locker.NextSlotIndex);

            // The deciding half (offline, or the host): a trick shot into slot 0, a plain drop into slot 1.
            float far = m_Director.Config.trickShotDistance + 0.5f;
            items[0].RegisterThrow(Vector3.forward * 3f, null, locker.zone.position + Vector3.back * far);
            Assert.IsTrue(locker.TryAccept(items[0]));
            int bonus = Mathf.RoundToInt(items[0].Value * m_Director.Config.trickShotBonusFraction);
            Assert.IsTrue(locker.TryAccept(items[1]));
            CollectionAssert.AreEqual(new[] { items[0].NetIndex + " " + bonus + " 0", items[1].NetIndex + " 0 1" }, accepted);
            Assert.AreSame(locker.slots[0], items[0].transform.parent);
            Assert.AreSame(locker.slots[1], items[1].transform.parent);
            Assert.AreEqual(2, m_Director.Session.DepositedCount);
            Assert.AreEqual(1, locker.TrickShotCount);
            Assert.AreEqual(2, locker.NextSlotIndex);

            // The repeating half (a client told what the host decided): the given slot, no ledger, no event.
            uint ledger = SessionDigest.Compute(m_Director.Session);
            locker.ApplyAccepted(items[2], 0, 5);
            Assert.IsTrue(items[2].IsSecured);
            Assert.AreSame(locker.slots[5], items[2].transform.parent);
            Assert.AreEqual(ledger, SessionDigest.Compute(m_Director.Session), "ApplyAccepted must not touch the ledger.");
            Assert.AreEqual(2, accepted.Count);
            Assert.AreEqual(6, locker.NextSlotIndex, "The next local deposit goes after the highest slot used.");
            locker.ApplyAccepted(items[2], 40, 7);
            Assert.AreSame(locker.slots[5], items[2].transform.parent, "An item that is already secured stays where it is.");
            Assert.AreEqual(1, locker.TrickShotCount);
            locker.ApplyAccepted(null, 0, 0);
        }

        [UnityTest]
        public IEnumerator Door_ApplyReplicated_SlidesTheLeaf_ButMakesNoNoiseForTheCreature_AndIgnoresRepeats()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            var door = m_Director.wreckDoors.First(d => d != null && !d.locked && d.makesGameplayNoise && !d.IsOpen);
            int changes = 0;
            door.Changed += d => changes++;
            int noiseBefore = m_Director.noise.EmittedCount;

            door.ApplyReplicated(true);
            Assert.IsTrue(door.IsOpen);
            Assert.IsTrue(door.IsMoving);
            Assert.AreEqual(1, changes);
            door.ApplyReplicated(true);
            Assert.AreEqual(1, changes, "The state it already has is not a change.");
            for (float t = 0f; t < 3f && door.IsMoving; t += Time.deltaTime) yield return null;
            Assert.IsTrue(door.IsFullyOpen, "The leaf should have slid open.");
            Assert.AreEqual(noiseBefore, m_Director.noise.EmittedCount, "Following the host's door makes no gameplay noise here.");

            // The deciding path is unchanged: it does make the noise.
            door.SetOpen(false, false, PlayerId.Local);
            Assert.IsFalse(door.IsOpen);
            Assert.AreEqual(2, changes);
            Assert.AreEqual(noiseBefore + 1, m_Director.noise.EmittedCount);
            Assert.AreEqual(NoiseKind.Door, m_Director.noise.LastNoise.Kind);
        }

        [UnityTest]
        public IEnumerator Web_TheRunParametersNameTheStrungOnes_AndStateAndFeelAreSeparate()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(3);
            var wires = m_Director.ActiveMap.tripwires;
            Assert.AreEqual(3, wires.Length);
            CollectionAssert.AreEquivalent(Object.FindObjectsByType<WebTripwire>(FindObjectsInactive.Exclude), wires, "The wreck lists exactly its own webs.");

            // The parameters name the strung ones; offline they are exactly the seed's own roll.
            uint mask = m_Director.CurrentRun.wireArmedMask;
            foreach (var w in wires)
            {
                int index = m_Director.NetIds.Wires.IndexOf(w.wireId);
                Assert.GreaterOrEqual(index, 0, w.wireId);
                Assert.AreSame(w, m_Director.WireAt(index));
                Assert.AreEqual(w.Armed, (mask & (1u << index)) != 0, w.wireId);
                Assert.AreEqual(w.Armed, w.WouldArm(m_Director.Session.Seed, m_Director.Condition), w.wireId);
            }

            // State: gone for everyone, heard once.
            var wire = wires.First(w => w.wireId == "a_corridor_mid");
            wire.SetArmed(true);
            var changes = new List<string>();
            foreach (var w in wires) w.Changed += (which, how, who) => changes.Add(which.wireId + " " + how + " " + who.Value);
            Vector3 at = (wire.endA.position + wire.endB.position) * 0.5f;
            int noiseBefore = m_Director.noise.EmittedCount;
            Assert.IsTrue(wire.ApplyState(WireChange.Snapped, at, new PlayerId(2)));
            CollectionAssert.AreEqual(new[] { "a_corridor_mid Snapped 2" }, changes, "Whoever applies the change is told once (a host passes it on).");
            Assert.IsTrue(wire.Snapped);
            Assert.IsFalse(wire.Armed);
            Assert.IsFalse(wire.visual.activeSelf);
            Assert.AreEqual(noiseBefore + 1, m_Director.noise.EmittedCount);
            Assert.AreEqual(NoiseKind.Web, m_Director.noise.LastNoise.Kind);
            Assert.AreEqual(new PlayerId(2), m_Director.noise.LastNoise.Source);
            Assert.IsFalse(wire.ApplyState(WireChange.Snapped, at, new PlayerId(2)), "A web can only go once.");
            Assert.AreEqual(noiseBefore + 1, m_Director.noise.EmittedCount);
            Assert.AreEqual(1, changes.Count, "Nothing changed the second time.");

            var other = wires.First(w => w.wireId == "a_corridor_far");
            other.SetArmed(true);
            Assert.IsTrue(other.ApplyState(WireChange.Burnt, at, PlayerId.None));
            Assert.IsTrue(other.Burnt);
            Assert.IsFalse(other.Snapped);
            Assert.AreEqual(noiseBefore + 1, m_Director.noise.EmittedCount, "Burning a web is silent.");
            CollectionAssert.AreEqual(new[] { "a_corridor_mid Snapped 2", "a_corridor_far Burnt " + PlayerId.None.Value }, changes);

            // The player's own blunder and the player's own cutter go the same way.
            var third = wires.First(w => w != wire && w != other);
            third.SetArmed(true);
            third.Snap(m_Player);
            Assert.AreEqual(third.wireId + " Snapped " + m_Player.Id.Value, changes.Last());
            third.Snap(m_Player);
            third.BurnAway();
            Assert.AreEqual(3, changes.Count);

            // Feel: only for the player wearing this headset.
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 12f), Quaternion.identity);
            yield return Frames(4);
            var zeroG = m_Player.locomotion.zeroG;
            zeroG.Stop();
            zeroG.AddImpulse(Vector3.forward * 1.2f);
            yield return null;
            float speed = zeroG.Speed;
            Assert.Greater(speed, 0.5f);
            var crew = AddRemotePlayer(1, MapPoints.Freighter(0f, 0f, 14f));
            wire.ApplyFeel(crew);
            wire.ApplyFeel(null);
            Assert.AreEqual(speed, zeroG.Speed, 0.05f, "Someone else's web does not drag at this player.");
            wire.ApplyFeel(m_Player);
            Assert.Less(zeroG.Speed, speed * 0.5f, "Their own does.");
        }

        [UnityTest]
        public IEnumerator SalvageBody_HasOneRuleForKinematicAndGrabbable_AndARemoteHolderCountsAsHeld()
        {
            // A contract with something clamped to a mount.
            for (int seed = 1; seed <= 40; seed++)
            {
                if (m_Director.Phase == MissionPhase.Active) m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
                MissionDirector.SeedOverride = seed;
                Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
                if (m_Director.Items.Any(i => i.IsMounted)) break;
            }
            yield return Frames(2);
            var mounted = m_Director.Items.FirstOrDefault(i => i.IsMounted);
            Assert.IsNotNull(mounted, "Test set-up: expected a contract with mounted salvage.");
            var item = LooseItems().First(i => i.definition.category == SalvageCategory.SmallScrap);

            // Offline: simulated here, held by nobody, free to move and to grab.
            Assert.IsTrue(item.SimulatedHere);
            Assert.AreEqual(PlayerId.None, item.RemoteHolder);
            Assert.IsFalse(item.IsHeld);
            Assert.IsFalse(item.Body.isKinematic);
            Assert.IsTrue(item.grab.enabled);
            Assert.IsTrue(item.impact.SimulatedHere, "Offline every knock counts here.");

            // Held on another machine: held as far as every rule is concerned, and its body just follows.
            Assert.AreEqual(PlayerId.None, item.LastHolder);
            Assert.IsFalse(item.HasBeenCollected);
            item.RegisterThrow(Vector3.forward * 3f, null);
            Assert.IsTrue(item.WasThrown);
            item.SetRemoteHolder(new PlayerId(2));
            Assert.IsTrue(item.IsHeld);
            Assert.IsTrue(item.Body.isKinematic);
            // ...and it leaves the traces a grab on this headset would: whose it was, and that it was taken.
            Assert.AreEqual(new PlayerId(2), item.LastHolder);
            Assert.AreEqual(new PlayerId(2), item.impact.LastHandler, "Its knocks are theirs from now on.");
            Assert.IsFalse(item.WasThrown, "An earlier throw is forgotten once it is back in a hand.");
            Assert.IsTrue(item.HasBeenCollected, "If it is lost now it comes back to the tug, not to where it was found.");
            Assert.IsTrue(m_Director.Session.TryGetSalvage(item.SalvageId, out var held));
            Assert.AreEqual(SalvageState.Collected, held.State, "Offline and on a host the ledger hears of the pickup.");
            Assert.AreEqual(new PlayerId(2), held.CollectedBy);
            item.transform.position = m_Director.locker.zone.position;
            item.Body.position = m_Director.locker.zone.position;
            yield return Physics(3);
            m_Director.locker.ScanZone();
            Assert.IsFalse(item.IsSecured, "The locker must not take an item out of a crew member's hand.");
            item.SetRemoteHolder(PlayerId.None);
            Assert.IsFalse(item.IsHeld);
            Assert.IsFalse(item.Body.isKinematic);
            Assert.AreEqual(new PlayerId(2), item.LastHolder, "Letting go does not forget who had it.");

            // Simulated on another machine: kinematic here, still grabbable, and its knocks do not count here.
            item.SetSimulatedHere(false);
            Assert.IsTrue(item.Body.isKinematic);
            Assert.IsTrue(item.grab.enabled);
            Assert.IsFalse(item.impact.SimulatedHere);
            item.SetSimulatedHere(true);
            Assert.IsFalse(item.Body.isKinematic);
            Assert.IsTrue(item.impact.SimulatedHere);

            // A mount outranks all of that until its bracket is cut.
            Assert.IsTrue(mounted.Body.isKinematic);
            Assert.IsFalse(mounted.grab.enabled);
            mounted.SetSimulatedHere(false);
            mounted.SetSimulatedHere(true);
            mounted.SetRemoteHolder(new PlayerId(1));
            Assert.AreEqual(PlayerId.None, mounted.LastHolder, "Nothing clamped to a mount is in anybody's hand.");
            Assert.IsFalse(mounted.HasBeenCollected);
            mounted.SetRemoteHolder(PlayerId.None);
            Assert.IsTrue(mounted.Body.isKinematic);
            Assert.IsFalse(mounted.grab.enabled);
            foreach (var point in m_Director.spawnPoints.Where(p => p.mount != null)) m_Director.RequestOpenPanel(point.mount, PlayerId.Local);
            Assert.IsFalse(mounted.IsMounted);
            Assert.IsFalse(mounted.Body.isKinematic);
            Assert.IsTrue(mounted.grab.enabled);

            // Secured in the locker is final.
            m_Director.locker.ScanZone();
            Assert.IsTrue(item.IsSecured, "Let go of (no longer held remotely), the locker takes it.");
            Assert.IsTrue(item.Body.isKinematic);
            Assert.IsFalse(item.grab.enabled);
            Assert.AreEqual(SalvageState.Deposited, held.State);
            Assert.AreEqual(new PlayerId(2), held.DepositedBy, "The deposit belongs to the crew member who carried it in.");
            item.SetSimulatedHere(true);
            item.SetRemoteHolder(PlayerId.None);
            Assert.IsTrue(item.Body.isKinematic);
            Assert.IsFalse(item.grab.enabled);

            // Damage goes through the director, which tells whoever is listening (a host would broadcast it).
            var delicate = m_Director.Items.Where(i => !i.IsSecured && !i.IsMounted && i.definition.fragility > 0f)
                .OrderByDescending(i => i.definition.fragility).First();
            var reports = new List<string>();
            m_Director.SalvageDamaged += (damaged, value) => reports.Add(damaged.NetIndex + " " + value);
            int lost = delicate.ApplyImpact(7f);
            Assert.Greater(lost, 0);
            CollectionAssert.AreEqual(new[] { delicate.NetIndex + " " + delicate.Value }, reports);
            Assert.IsTrue(m_Director.Session.TryGetSalvage(delicate.SalvageId, out var record));
            Assert.AreEqual(delicate.Value, record.Value, "Offline the books follow the damage at once.");

            // The host's word on a value (a knock that happened on someone else's machine).
            delicate.SetRemoteValue(delicate.Value - 1);
            Assert.AreEqual(delicate.FullValue - lost - 1, delicate.Value);
            delicate.SetRemoteValue(delicate.FullValue + 100);
            Assert.AreEqual(delicate.FullValue, delicate.Value);
        }

        // ------------------------------------------------------------------ with a session (a stub)

        [UnityTest]
        public IEnumerator Junk_OnAClient_CountsOnlyOnceItsOwnPlayerHasHadItInHand()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            var junk = Object.FindObjectsByType<ImpactNoiseEmitter>(FindObjectsSortMode.None)
                .Where(e => e.GetComponent<SalvageItem>() == null && e.GetComponent<XRGrabInteractable>() != null)
                .OrderBy(e => e.transform.position.z).Take(2).ToArray();
            Assert.AreEqual(2, junk.Length, "Test set-up: the wreck has loose junk in it.");

            // Offline every knock counts here, and junk noise belongs to nobody (as it always has).
            foreach (var j in junk)
            {
                Assert.IsTrue(j.SimulatedHere);
                Assert.AreEqual(PlayerId.None, j.LastGrabbedBy);
                Assert.AreEqual(PlayerId.None, j.NoiseSource);
            }

            // This headset is crew slot 1 in someone else's session. Junk is not shared: the copy drifting
            // about on this machine is not the one the creature hears.
            var net = new StubNet { client = true, slot = 1 };
            m_Player.AssignId(new PlayerId(1), false);
            m_Director.Net = net;
            Assert.IsFalse(junk[0].SimulatedHere);
            Assert.IsFalse(junk[1].SimulatedHere);

            // Until this player picks one up: a can you threw yourself is your noise.
            var hand = m_Player.rightHand.interactor;
            var grab = junk[0].GetComponent<XRGrabInteractable>();
            Assert.IsNotNull(hand.interactionManager, "Test set-up: the hand's interactor should be registered.");
            hand.interactionManager.SelectEnter((IXRSelectInteractor)hand, (IXRSelectInteractable)grab);
            Assert.IsTrue(grab.isSelected, "Test set-up: the hand should be holding the junk.");
            hand.interactionManager.SelectExit((IXRSelectInteractor)hand, (IXRSelectInteractable)grab);
            Assert.AreEqual(new PlayerId(1), junk[0].LastGrabbedBy);
            Assert.IsTrue(junk[0].SimulatedHere, "Junk this player has handled makes gameplay noise on their machine.");
            Assert.AreEqual(new PlayerId(1), junk[0].NoiseSource, "And the noise is theirs, so it is passed on to the host.");
            Assert.AreEqual(PlayerId.None, junk[0].LastHandler);
            Assert.IsFalse(junk[1].SimulatedHere, "Junk nobody here has touched still does not count.");
            Assert.AreEqual(PlayerId.None, junk[1].LastGrabbedBy);

            // A host counts all of its own junk, attributed as offline.
            net.client = false;
            Assert.IsTrue(junk[0].SimulatedHere);
            Assert.IsTrue(junk[1].SimulatedHere);
            Assert.AreEqual(PlayerId.None, junk[0].NoiseSource);

            // And once the session is over everything is as it was.
            m_Director.Net = null;
            m_Player.AssignId(PlayerId.Local, false);
            Assert.IsTrue(junk[1].SimulatedHere);
            Assert.AreEqual(PlayerId.None, junk[0].NoiseSource);
            yield return Frames(2);
            CollectionAssert.IsEmpty(net.calls);
        }

        [UnityTest]
        public IEnumerator OnAClient_EverySharedDecisionBecomesARequest_AndNothingChangesUntilTheHostsWordIsApplied()
        {
            // The host's parameters for this contract, with its own choice of webs: only the far corridor one.
            var run = m_Director.BuildRunParams();
            int farWire = m_Director.NetIds.Wires.IndexOf("a_corridor_far");
            Assert.GreaterOrEqual(farWire, 0);
            run.wireArmedMask = 1u << farWire;
            run.planHash = run.ComputePlanHash();

            // This headset joins as crew slot 1.
            var net = new StubNet { client = true, slot = 1 };
            m_Player.AssignId(new PlayerId(1), false);
            m_Director.Net = net;
            PlayerId me = m_Director.LocalPlayerId;
            Assert.AreEqual(new PlayerId(1), me);
            Assert.AreSame(m_Player, m_Director.LocalPlayer);
            Assert.AreSame(m_Player, m_Director.FindPlayer(me));
            Assert.IsNull(m_Director.FindPlayer(new PlayerId(0)), "In a session an unknown id is nobody, not 'the first player'.");

            // START only asks.
            Assert.IsFalse(m_Director.StartMission(me));
            Assert.AreEqual(MissionPhase.Hub, m_Director.Phase);
            CollectionAssert.AreEqual(new[] { "start" }, net.calls);

            // The host answers with the run and every machine begins exactly that.
            Assert.IsTrue(m_Director.BeginRun(in run));
            yield return Frames(3);
            net.calls.Clear();
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            foreach (var w in m_Director.ActiveMap.tripwires)
                Assert.AreEqual(w.wireId == "a_corridor_far", w.Armed, w.wireId + ": in a session the webs come from the parameters.");
            uint ledger = SessionDigest.Compute(m_Director.Session, true);

            // Ending the run, the clock.
            Assert.IsNull(m_Director.Extract(me));
            Assert.IsNull(m_Director.FailMission(FailureReason.Caught, me));
            Assert.IsNull(m_Director.TickMission(100000f, false));
            CollectionAssert.AreEqual(new[] { "extract" }, net.calls);
            net.calls.Clear();

            // Salvage: pickups, deposits, the locker.
            var scrap = LooseItems().First(i => i.definition.category == SalvageCategory.SmallScrap);
            Assert.IsFalse(m_Director.NotifySalvageCollected(scrap, me));
            Assert.AreEqual(DepositResult.UnknownItem, m_Director.DepositSalvage(scrap, me));
            Assert.AreEqual(0, m_Director.AwardTrickShot(scrap));
            scrap.RecoverTo(m_Director.locker.zone.position);
            yield return Physics(3);
            m_Director.locker.ScanZone();
            Assert.IsFalse(scrap.IsSecured, "A client's locker does not judge.");

            // A knock on something this machine simulates is reported, not booked.
            var delicate = LooseItems().First(i => i.definition.fragility > 0.4f);
            int full = delicate.Value;
            int lost = delicate.ApplyImpact(5f);
            Assert.Greater(lost, 0);
            CollectionAssert.AreEqual(new[] { "damage " + delicate.NetIndex + " " + (full - lost) }, net.calls);
            net.calls.Clear();

            // Panels and doors.
            var panel = m_Director.cuttables.First(c => c != null && !c.isTraining);
            Assert.IsFalse(m_Director.RequestOpenPanel(panel, me));
            Assert.IsFalse(panel.IsOpen);
            var door = m_Director.wreckDoors.First(d => d != null && !d.locked && d.buttons != null && d.buttons.Length > 0 && !d.IsOpen);
            int doorNoise = m_Director.noise.EmittedCount;
            Assert.IsTrue(door.buttons[0].Press(m_Player.rightHand));
            Assert.AreEqual(doorNoise, m_Director.noise.EmittedCount);
            Assert.IsFalse(door.IsOpen, "The door waits for the host.");
            CollectionAssert.AreEqual(new[] { "door " + door.doorId + " open" }, net.calls);
            net.calls.Clear();

            // A web: felt at once, reported once, still there until the host says it went.
            var wire = m_Director.ActiveMap.tripwires.First(w => w.Armed);
            wire.Snap(m_Player);
            wire.Snap(m_Player);
            Assert.IsTrue(wire.Armed);
            Assert.IsFalse(wire.Snapped);
            CollectionAssert.AreEqual(new[] { "wire a_corridor_far Snapped" }, net.calls);
            net.calls.Clear();

            // The local tank does not run on a client, even with the player outside.
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 9f));
            yield return Frames(20);
            Assert.IsFalse(m_Player.InSafeZone);
            Assert.AreEqual(ledger, SessionDigest.Compute(m_Director.Session, true), "Nothing above may have changed the client's ledger or clock.");
            CollectionAssert.IsEmpty(net.calls);

            // Then the host's word arrives, and is applied.
            int noiseBefore = m_Director.noise.EmittedCount;
            door.ApplyReplicated(true);
            Assert.IsTrue(door.IsOpen);
            Assert.IsTrue(wire.ApplyState(WireChange.Snapped, m_Player.HeadPosition, me));
            Assert.IsFalse(wire.Armed);
            Assert.AreEqual(noiseBefore, m_Director.noise.EmittedCount, "A client makes no door or web noise of its own: the host did.");
            Assert.IsTrue(m_Director.Session.ApplyRemoteCollect(scrap.SalvageId, me));
            Assert.IsTrue(m_Director.Session.ApplyRemoteDeposit(scrap.SalvageId, me, scrap.Value, 0));
            m_Director.locker.ApplyAccepted(scrap, 0, 2);
            Assert.IsTrue(scrap.IsSecured);
            Assert.AreSame(m_Director.locker.slots[2], scrap.transform.parent);
            Assert.AreEqual(1, m_Director.Session.DepositedCount);
            Assert.IsTrue(m_Director.Session.ApplyClock(42f, 100f));
            Assert.AreEqual(100f, m_Director.Session.Oxygen.Remaining);
            CollectionAssert.IsEmpty(net.calls);
        }

        [UnityTest]
        public IEnumerator OnAHost_TheContractStartsAsUsual_AndTheMimicIsPickedAsOffline()
        {
            yield return null;
            var mimics = Enumerable.Range(0, m_Director.enemies.Length).Where(i => m_Director.enemies[i].GetComponent<VoiceMimic>() != null).ToList();
            Assert.AreEqual(1, mimics.Count, "Test set-up: the roster has one Mimic.");
            MissionDirector.CreatureOverride = null;

            // Offline the Mimic turns up now and then.
            var offlinePicks = new HashSet<int>();
            for (int seed = 1; seed <= 60; seed++)
            {
                MissionDirector.SeedOverride = seed;
                offlinePicks.Add(m_Director.BuildRunParams().creatureIndex);
            }
            CollectionAssert.Contains(offlinePicks, mimics[0]);
            Assert.AreEqual(m_Director.enemies.Length, offlinePicks.Count);

            // In a session it is picked exactly as offline (in co-op it hears the whole crew).
            var net = new StubNet { client = false, slot = 0 };
            m_Director.Net = net;
            var hostPicks = new HashSet<int>();
            for (int seed = 1; seed <= 60; seed++)
            {
                MissionDirector.SeedOverride = seed;
                hostPicks.Add(m_Director.BuildRunParams().creatureIndex);
            }
            CollectionAssert.Contains(hostPicks, mimics[0]);
            CollectionAssert.AreEquivalent(offlinePicks, hostPicks);

            // A host decides for itself: nothing is turned into a request.
            MissionDirector.SeedOverride = 5;
            Assert.IsTrue(m_Director.StartMission(m_Director.LocalPlayerId));
            yield return Frames(2);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.AreSame(m_Director.enemies[m_Director.CurrentRun.creatureIndex], m_Director.ActiveEnemy);
            var scrap = LooseItems().First(i => i.definition.category == SalvageCategory.SmallScrap);
            Assert.IsTrue(m_Director.locker.TryAccept(scrap));
            Assert.IsNotNull(m_Director.Extract(m_Director.LocalPlayerId));
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            CollectionAssert.IsEmpty(net.calls);
            Assert.IsNull(m_Director.FindPlayer(new PlayerId(3)));
        }
    }
}

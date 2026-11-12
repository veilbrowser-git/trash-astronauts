using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Co-op stage 0: the seams, with no networking behind them. Explicit run parameters, stable numbers
    /// for the Builder's string ids, and the client's mirror of the mission ledger.
    /// </summary>
    public class NetSeamsTests
    {
        static readonly PlayerId P0 = new PlayerId(0), P1 = new PlayerId(1), P2 = new PlayerId(2);

        // ------------------------------------------------------------------ run parameters

        static RunStartParams SampleRun()
        {
            var run = new RunStartParams
            {
                seed = 20261111,
                runIndex = 4,
                mapIndex = 2,
                condition = (byte)ContractCondition.ThinAir,
                creatureIndex = 1,
                quota = 900,
                oxygenCapacitySeconds = 480f,
                bonusMultiplier = 1.5f,
                wireArmedMask = 0b101u,
                hazardFlags = RunStartParams.HazardBlackouts,
                items = new[]
                {
                    new RunItem { pointIndex = 3, definitionIndex = 1, value = 150 },
                    new RunItem { pointIndex = 9, definitionIndex = 0, value = 600 },
                    new RunItem { pointIndex = 5, definitionIndex = 2, value = 45, flags = RunItem.FlagBonus },
                },
            };
            run.planHash = run.ComputePlanHash();
            return run;
        }

        [Test]
        public void PlanHash_CoversEveryField_AndTheOrderOfTheItems_ButNotItself()
        {
            var run = SampleRun();
            uint hash = run.ComputePlanHash();
            Assert.AreEqual(hash, SampleRun().ComputePlanHash(), "The same parameters must hash the same.");
            Assert.AreEqual(hash, run.planHash);

            var copy = run; copy.planHash = 12345;
            Assert.AreEqual(hash, copy.ComputePlanHash(), "planHash is not part of what it hashes.");

            var changes = new Dictionary<string, Func<RunStartParams, RunStartParams>>
            {
                { "seed", r => { r.seed++; return r; } },
                { "runIndex", r => { r.runIndex++; return r; } },
                { "mapIndex", r => { r.mapIndex = 1; return r; } },
                { "condition", r => { r.condition = (byte)ContractCondition.Standard; return r; } },
                { "creatureIndex", r => { r.creatureIndex = RunStartParams.NoCreature; return r; } },
                { "quota", r => { r.quota = 901; return r; } },
                { "oxygenCapacitySeconds", r => { r.oxygenCapacitySeconds = 480.5f; return r; } },
                { "bonusMultiplier", r => { r.bonusMultiplier = 1f; return r; } },
                { "wireArmedMask", r => { r.wireArmedMask = 0b100u; return r; } },
                { "hazardFlags", r => { r.hazardFlags = 0; return r; } },
                { "item point", r => { r.items = (RunItem[])r.items.Clone(); r.items[0].pointIndex = 4; return r; } },
                { "item definition", r => { r.items = (RunItem[])r.items.Clone(); r.items[1].definitionIndex = 3; return r; } },
                { "item value", r => { r.items = (RunItem[])r.items.Clone(); r.items[1].value = 601; return r; } },
                { "item flags", r => { r.items = (RunItem[])r.items.Clone(); r.items[2].flags = 0; return r; } },
                { "item order", r => { r.items = new[] { r.items[1], r.items[0], r.items[2] }; return r; } },
                { "item count", r => { r.items = r.items.Take(2).ToArray(); return r; } },
            };
            foreach (var change in changes)
                Assert.AreNotEqual(hash, change.Value(run).ComputePlanHash(), "Changing " + change.Key + " must change the plan hash.");

            var empty = new RunStartParams();
            Assert.AreEqual(empty.ComputePlanHash(), new RunStartParams { items = new RunItem[0] }.ComputePlanHash(), "No items and an empty list are the same plan.");
        }

        // ------------------------------------------------------------------ id tables

        static readonly string[] k_Doors = { "door_tug_hatch", "door_airlock", "door_reactor", "b_door_drive", "c_door_reactor" };
        static readonly string[] k_Points = { "cargo_a", "cargo_b", "reactor_housing", "maint_rack", "b_hold_1", "b_hold_2", "c_lab_1" };
        static readonly string[] k_Wires = { "a_corridor_mid", "b_vestibule", "c_ring_west" };
        static readonly string[] k_Maps = { "freighter", "ore_barge", "survey_ship" };
        static readonly string[] k_Definitions = { "scrap_plate", "board", "reactor_core", "novelty_rubber_duck" };

        static KeyValuePair<string, int>[] Panels(params object[] idThenCount)
        {
            var list = new List<KeyValuePair<string, int>>();
            for (int i = 0; i < idThenCount.Length; i += 2) list.Add(new KeyValuePair<string, int>((string)idThenCount[i], (int)idThenCount[i + 1]));
            return list.ToArray();
        }

        static NetIdTable Table(IEnumerable<string> doors = null, KeyValuePair<string, int>[] panels = null, IEnumerable<string> points = null,
            IEnumerable<string> wires = null, IEnumerable<string> maps = null, IEnumerable<string> definitions = null, int creatures = 4)
        {
            return new NetIdTable(doors ?? k_Doors, panels ?? Panels("practice_plate", 4, "panel_reactor", 6, "b_rack", 2), points ?? k_Points,
                wires ?? k_Wires, maps ?? k_Maps, definitions ?? k_Definitions, creatures);
        }

        [Test]
        public void IdTables_NumberBySortedId_WhateverOrderTheSceneListsThem()
        {
            var table = Table();
            CollectionAssert.AreEqual(new[] { "b_door_drive", "c_door_reactor", "door_airlock", "door_reactor", "door_tug_hatch" }, table.Doors.Ids);
            Assert.AreEqual(2, table.Doors.IndexOf("door_airlock"));
            Assert.AreEqual("door_airlock", table.Doors[2]);
            Assert.AreEqual(-1, table.Doors.IndexOf("door_nowhere"));
            Assert.AreEqual(-1, table.Doors.IndexOf(null));
            Assert.IsNull(table.Doors[5]);
            Assert.IsNull(table.Doors[-1]);

            // Ordinal, not culture or case-insensitive: capitals sort before lower case.
            var mixed = new NetIdTable.Table(new[] { "b", "B", "a", "_x", "A" });
            CollectionAssert.AreEqual(new[] { "A", "B", "_x", "a", "b" }, mixed.Ids);

            // Wrecks keep their place in the director's array: the run parameters carry that index.
            CollectionAssert.AreEqual(k_Maps, table.Maps.Ids);
            Assert.AreEqual(1, table.Maps.IndexOf("ore_barge"));

            // Cut points are addressed as (panel, index); the offsets lay every weld out in one bitset.
            CollectionAssert.AreEqual(new[] { "b_rack", "panel_reactor", "practice_plate" }, table.Panels.Ids);
            Assert.AreEqual(2, table.CutPointCount(0));
            Assert.AreEqual(6, table.CutPointCount(1));
            Assert.AreEqual(4, table.CutPointCount(2));
            Assert.AreEqual(0, table.CutPointOffset(0));
            Assert.AreEqual(2, table.CutPointOffset(1));
            Assert.AreEqual(8, table.CutPointOffset(2));
            Assert.AreEqual(12, table.TotalCutPoints);
            Assert.AreEqual(0, table.CutPointCount(3));

            var shuffled = Table(k_Doors.Reverse(), Panels("b_rack", 2, "practice_plate", 4, "panel_reactor", 6), k_Points.Reverse(), k_Wires.Reverse(), null, k_Definitions.Reverse());
            CollectionAssert.AreEqual(table.Doors.Ids, shuffled.Doors.Ids);
            CollectionAssert.AreEqual(table.Points.Ids, shuffled.Points.Ids);
            Assert.AreEqual(table.CutPointOffset(2), shuffled.CutPointOffset(2));
            CollectionAssert.IsEmpty(table.Validate());
        }

        [Test]
        public void ContentHash_IsTheSameForTheSameContent_AndDifferentForAnyOtherBuild()
        {
            const int protocol = 1;
            uint hash = Table().ContentHash(protocol);
            Assert.AreEqual(hash, Table().ContentHash(protocol));
            var shuffled = Table(k_Doors.Reverse(), Panels("b_rack", 2, "practice_plate", 4, "panel_reactor", 6), k_Points.Reverse(), k_Wires.Reverse(), null, k_Definitions.Reverse());
            Assert.AreEqual(hash, shuffled.ContentHash(protocol), "The order objects happen to be listed in must not matter.");

            var others = new Dictionary<string, uint>
            {
                { "protocol version", Table().ContentHash(protocol + 1) },
                { "a renamed door", Table(doors: k_Doors.Select(d => d == "door_reactor" ? "door_reactor2" : d)).ContentHash(protocol) },
                { "one door fewer", Table(doors: k_Doors.Take(4)).ContentHash(protocol) },
                { "a renamed panel", Table(panels: Panels("practice_plate", 4, "panel_reactor", 6, "b_rack2", 2)).ContentHash(protocol) },
                { "a panel with another weld", Table(panels: Panels("practice_plate", 4, "panel_reactor", 7, "b_rack", 2)).ContentHash(protocol) },
                { "an extra spawn point", Table(points: k_Points.Concat(new[] { "c_lab_2" })).ContentHash(protocol) },
                { "a renamed web", Table(wires: new[] { "a_corridor_mid", "b_vestibule", "c_ring_east" }).ContentHash(protocol) },
                { "wrecks in another order", Table(maps: new[] { "ore_barge", "freighter", "survey_ship" }).ContentHash(protocol) },
                { "another salvage catalog", Table(definitions: k_Definitions.Take(3)).ContentHash(protocol) },
                { "another creature roster", Table(creatures: 3).ContentHash(protocol) },
                // The same strings split differently between two lists is still different content.
                { "a door that became a web", Table(doors: k_Doors.Take(4), wires: k_Wires.Concat(new[] { "door_tug_hatch" })).ContentHash(protocol) },
            };
            foreach (var other in others) Assert.AreNotEqual(hash, other.Value, "A build with " + other.Key + " must not look like this one.");
            Assert.AreEqual(others.Count, others.Values.Distinct().Count(), "Each of those differences should give its own hash.");
        }

        [Test]
        public void Validate_ReportsRepeatedIds_EmptyIds_AndCountsThatOutgrowTheirWireFields()
        {
            CollectionAssert.IsEmpty(Table().Validate());

            var repeated = Table(doors: k_Doors.Concat(new[] { "door_airlock" }));
            Assert.AreEqual(5, repeated.Doors.Count, "A repeated id still gets one number.");
            Assert.IsTrue(repeated.Validate().Any(p => p.Contains("duplicate door id 'door_airlock'")), string.Join("; ", repeated.Validate()));
            Assert.IsTrue(Table(panels: Panels("p", 2, "p", 2)).Validate().Any(p => p.Contains("duplicate panel id")));
            Assert.IsTrue(Table(wires: new[] { "w", "w" }).Validate().Any(p => p.Contains("duplicate web id")));
            Assert.IsTrue(Table(points: new[] { "a", "", null }).Validate().Any(p => p.Contains("2 spawn point id(s) are empty")));

            IEnumerable<string> Many(string prefix, int count) => Enumerable.Range(0, count).Select(i => prefix + i);
            CollectionAssert.IsEmpty(Table(doors: Many("d", NetIdTable.MaxDoors)).Validate());
            Assert.IsTrue(Table(doors: Many("d", NetIdTable.MaxDoors + 1)).Validate().Any(p => p.Contains("17 door ids")));
            CollectionAssert.IsEmpty(Table(wires: Many("w", NetIdTable.MaxWires)).Validate());
            Assert.IsTrue(Table(wires: Many("w", NetIdTable.MaxWires + 1)).Validate().Any(p => p.Contains("33 web ids")));
            Assert.IsTrue(Table(panels: Many("p", NetIdTable.MaxPanels + 1).Select(p => new KeyValuePair<string, int>(p, 2)).ToArray()).Validate().Any(p => p.Contains("17 panel ids")));
            Assert.IsTrue(Table(points: Many("s", NetIdTable.MaxPoints + 1)).Validate().Any(p => p.Contains("256 spawn point ids")));
            Assert.IsTrue(Table(definitions: Many("x", NetIdTable.MaxDefinitions + 1)).Validate().Any(p => p.Contains("256 salvage definition ids")));
            CollectionAssert.IsEmpty(Table(creatures: NetIdTable.MaxCreatures).Validate());
            Assert.IsTrue(Table(creatures: NetIdTable.MaxCreatures + 1).Validate().Any(p => p.Contains("9 creatures")));
            Assert.IsTrue(Table(panels: Panels("huge", NetIdTable.MaxCutPointsPerPanel + 1)).Validate().Any(p => p.Contains("cut points")));

            // The limits are the widths of the wire fields: 16-bit door and panel masks, 32-bit web masks, one-byte ids.
            Assert.AreEqual(16, NetIdTable.MaxDoors);
            Assert.AreEqual(16, NetIdTable.MaxPanels);
            Assert.AreEqual(32, NetIdTable.MaxWires);
            Assert.AreEqual(255, NetIdTable.MaxItems);
            Assert.AreEqual(8, NetIdTable.MaxCreatures);
            Assert.Less(NetIdTable.MaxCreatures, RunStartParams.NoCreature);
        }

        // ------------------------------------------------------------------ the same salvage ids everywhere

        static void PlayAndLose(MissionSession session, int runs)
        {
            for (int i = 0; i < runs; i++)
            {
                Assert.IsTrue(session.Begin(100 + i, 10, 60f));
                session.Fail(FailureReason.Caught);
            }
        }

        [Test]
        public void ExplicitRunParameters_ReproduceTheOwnersSalvageIds_BonusIncluded_OnAMachineWithAnotherRunHistory()
        {
            // A wreck: twelve spots, two of them behind panels.
            var points = new List<SpawnPointInfo>();
            for (int i = 0; i < 12; i++)
                points.Add(new SpawnPointInfo("spot_" + (char)('a' + i), SalvageCategoryMask.Any, i == 3 || i == 7 ? "panel_" + i : null));
            var specs = new List<SalvageSpec>
            {
                new SalvageSpec("scrap_plate", SalvageCategory.SmallScrap, 60),
                new SalvageSpec("board", SalvageCategory.Electronics, 150),
                new SalvageSpec("reactor_core", SalvageCategory.ReactorComponent, 600),
                new SalvageSpec("snow_globe", SalvageCategory.Novelty, 90),
            };
            int Count(SalvageCategory c) => c == SalvageCategory.ReactorComponent ? 1 : 2;
            var ids = new NetIdTable(k_Doors, null, points.Select(p => p.id).Reverse(), k_Wires, k_Maps,
                specs.Select(s => s.id).Concat(new[] { "novelty_rubber_duck" }), 4);
            const int seed = 4242;

            // The owner of the run has played six contracts before this one.
            var owner = new MissionSession(100);
            PlayAndLose(owner, 6);
            var plan = SalvageSpawnPlanner.Plan(points, specs, Count, 300, 1.3f, seed, owner.RunIndex + 1);
            Assert.GreaterOrEqual(plan.Assignments.Count, 7);
            Assert.IsTrue(owner.Begin(seed, plan.Quota, 600f));
            Assert.AreEqual(7, owner.RunIndex);
            var ownerIds = new List<string>();
            foreach (var a in plan.Assignments)
            {
                Assert.IsTrue(owner.RegisterSalvage(a.salvageId, a.definitionId, a.value));
                ownerIds.Add(a.salvageId);
            }
            var used = new HashSet<string>(plan.Assignments.Select(a => a.spawnPointId));
            var free = points.Where(p => !p.IsGated && !used.Contains(p.id)).ToList();
            Assert.IsNotEmpty(free);
            var bonusSpot = free[new Random(seed ^ 0x2545f491).Next(free.Count)];
            string ownerBonusId = SalvageSpawnPlanner.MakeSalvageId(owner.RunIndex, bonusSpot.id + "#bonus");
            Assert.IsTrue(owner.RegisterSalvage(ownerBonusId, "novelty_rubber_duck", 45));
            ownerIds.Add(ownerBonusId);

            // What travels: numbers only.
            var run = new RunStartParams
            {
                seed = seed, runIndex = (ushort)owner.RunIndex, quota = plan.Quota, oxygenCapacitySeconds = 600f, bonusMultiplier = 1f,
                creatureIndex = RunStartParams.NoCreature,
                items = plan.Assignments.Select(a => new RunItem
                {
                    pointIndex = (byte)ids.Points.IndexOf(a.spawnPointId),
                    definitionIndex = (byte)ids.Definitions.IndexOf(a.definitionId),
                    value = (ushort)a.value,
                }).Concat(new[]
                {
                    new RunItem
                    {
                        pointIndex = (byte)ids.Points.IndexOf(bonusSpot.id),
                        definitionIndex = (byte)ids.Definitions.IndexOf("novelty_rubber_duck"),
                        value = 45, flags = RunItem.FlagBonus,
                    },
                }).ToArray(),
            };
            run.planHash = run.ComputePlanHash();

            // Another machine, which has played two contracts, starts the same run from the parameters.
            var other = new MissionSession(100);
            PlayAndLose(other, 2);
            Assert.AreNotEqual(owner.RunIndex, other.RunIndex + 1, "Test set-up: the two machines must disagree on the run number.");
            Assert.IsTrue(other.Begin(run.seed, run.quota, run.oxygenCapacitySeconds, run.runIndex));
            other.BonusMultiplier = run.bonusMultiplier;
            Assert.AreEqual(7, other.RunIndex, "The run number comes from the parameters, not from the local counter.");
            var otherIds = new List<string>();
            foreach (var item in run.items)
            {
                string id = ids.SalvageIdFor(other.RunIndex, item);
                Assert.IsTrue(other.RegisterSalvage(id, ids.Definitions[item.definitionIndex], item.value), id);
                otherIds.Add(id);
            }

            CollectionAssert.AreEqual(ownerIds, otherIds, "Net id i must be the same piece of salvage, with the same string id, everywhere.");
            Assert.AreEqual("run7/" + bonusSpot.id + "#bonus", otherIds.Last());
            Assert.IsTrue(otherIds.Take(otherIds.Count - 1).All(id => id.StartsWith("run7/") && !id.Contains("#")));
            Assert.AreEqual(SessionDigest.Compute(owner), SessionDigest.Compute(other), SessionDigest.Describe(owner) + "\nvs\n" + SessionDigest.Describe(other));

            // Left to its own counter the second machine would have named everything "run3/...".
            Assert.AreNotEqual(ownerIds[0], SalvageSpawnPlanner.MakeSalvageId(3, plan.Assignments[0].spawnPointId));

            // Back on its own afterwards, it simply carries on counting from there.
            other.Fail(FailureReason.Caught);
            Assert.IsTrue(other.Begin(1, 10, 60f));
            Assert.AreEqual(8, other.RunIndex);
        }

        // ------------------------------------------------------------------ the client's mirror of the ledger

        /// <summary>A host session, a client mirror, and every host decision turned into a "message" for the mirror.</summary>
        sealed class Pair
        {
            public readonly MissionSession host = new MissionSession(100);
            public readonly MissionSession mirror = new MissionSession(100);
            public readonly List<string> ids = new List<string>();
            public int messages;

            public Pair(int items, int mirrorRunsBefore = 3)
            {
                PlayAndLose(mirror, mirrorRunsBefore);
                Assert.IsTrue(host.Begin(77, 400, 600f));
                host.BonusMultiplier = 1.5f;
                Assert.IsTrue(mirror.Begin(77, 400, 600f, host.RunIndex));
                mirror.BonusMultiplier = 1.5f;
                for (int i = 0; i < items; i++)
                {
                    string id = SalvageSpawnPlanner.MakeSalvageId(host.RunIndex, "spot_" + i);
                    ids.Add(id);
                    host.RegisterSalvage(id, "def_" + (i % 3), 100 + i * 10);
                    mirror.RegisterSalvage(id, "def_" + (i % 3), 100 + i * 10);
                }
                AssertSame("at the start");
            }

            /// <summary>Delivers one message to the mirror twice (a duplicate must change nothing) and compares.</summary>
            public void Send(string what, Action<MissionSession> message)
            {
                messages++;
                message(mirror);
                uint once = SessionDigest.Compute(mirror, true);
                message(mirror);
                Assert.AreEqual(once, SessionDigest.Compute(mirror, true), "Applying '" + what + "' a second time changed the mirror.");
                AssertSame("after " + what);
            }

            public void AssertSame(string when, bool clock = false)
            {
                Assert.AreEqual(SessionDigest.Compute(host, clock), SessionDigest.Compute(mirror, clock),
                    "Host and mirror differ " + when + ":\n" + SessionDigest.Describe(host, clock) + "\nvs\n" + SessionDigest.Describe(mirror, clock));
            }

            public void Collect(string id, PlayerId by)
            {
                if (host.TryCollect(id, by)) Send("collect " + id, m => m.ApplyRemoteCollect(id, by));
            }

            public void Damage(string id, int newValue)
            {
                if (host.DamageSalvage(id, newValue)) Send("value " + id, m => m.ApplyRemoteValue(id, newValue));
            }

            public void Deposit(string id, PlayerId by, int trickBonus)
            {
                if (host.TryDeposit(id, by) != DepositResult.Accepted) return;
                int awarded = trickBonus > 0 && host.AwardTrickShot(id, trickBonus) ? trickBonus : 0;
                host.TryGetSalvage(id, out var record);
                int finalValue = record.Value;
                Send("deposit " + id, m => m.ApplyRemoteDeposit(id, by, finalValue, awarded));
            }

            public void OpenPanel(string panelId, PlayerId by)
            {
                if (host.TryOpenPanel(panelId, by)) Send("panel " + panelId, m => m.TryOpenPanel(panelId, by));
            }

            public MissionResult Tick(float seconds, bool safe)
            {
                var result = host.Tick(seconds, safe);
                if (result != null) End(result);
                else
                {
                    float elapsed = host.ElapsedSeconds, oxygen = host.Oxygen.Remaining;
                    Send("clock", m => m.ApplyClock(elapsed, oxygen));
                    AssertSame("after the clock, clock included", true);
                }
                return result;
            }

            public void End(MissionResult result)
            {
                Send("end", m => m.ApplyRemoteEnd(result));
                AssertSame("after the end, clock included", true);
            }
        }

        [Test]
        public void Mirror_FedOnlyByTheApplyMethods_MatchesTheHost_AndEveryMessageCanArriveTwice()
        {
            var pair = new Pair(5);
            string a = pair.ids[0], b = pair.ids[1], c = pair.ids[2], d = pair.ids[3];
            int collected = 0, deposited = 0, ended = 0;
            pair.mirror.SalvageCollected += r => collected++;
            pair.mirror.SalvageDeposited += r => deposited++;
            pair.mirror.Ended += r => ended++;

            pair.Collect(a, P1);
            pair.Collect(a, P2); // the host refuses a second first-pickup; nothing is sent
            pair.Damage(c, 95);
            pair.Damage(c, 80);
            pair.Collect(c, P0);
            pair.Deposit(a, P1, 25);      // a trick shot
            pair.Deposit(b, P2, 0);       // dropped in without ever being "collected"
            pair.OpenPanel("panel_reactor", P1);
            pair.OpenPanel("panel_reactor", P2);
            pair.Tick(12.5f, false);
            pair.Deposit(c, P0, 0);
            pair.Damage(c, 10);           // safe in the locker: refused by the host
            pair.Tick(3f, true);
            Assert.AreEqual(10, pair.messages, "Test set-up: every accepted host change is one message, and every refused one is none.");

            Assert.AreEqual(2, collected, "A duplicate collect message must not raise the event again.");
            Assert.AreEqual(3, deposited, "A duplicate deposit message must not raise the event again.");
            Assert.AreEqual(pair.host.SecuredValue, pair.mirror.SecuredValue);
            Assert.AreEqual(1, pair.mirror.TrickShots);
            Assert.AreEqual(25, pair.mirror.TrickBonus);
            Assert.IsTrue(pair.mirror.TryGetSalvage(a, out var mirrored));
            Assert.AreEqual(P1, mirrored.CollectedBy);
            Assert.IsTrue(mirrored.TrickShot);
            Assert.IsTrue(pair.mirror.IsPanelOpen("panel_reactor"));
            Assert.AreEqual(15.5f, pair.mirror.ElapsedSeconds, 0.0001f);
            Assert.AreEqual(600f - 12.5f, pair.mirror.Oxygen.Remaining, 0.0001f);
            Assert.IsTrue(pair.mirror.TryGetSalvage(d, out var untouched));
            Assert.AreEqual(SalvageState.InWreck, untouched.State);

            var result = pair.host.Extract(P0);
            Assert.IsNotNull(result);
            pair.End(result);
            Assert.AreEqual(1, ended, "The run ends once on the mirror, however often it is told.");
            Assert.AreEqual(MissionPhase.Results, pair.mirror.Phase);
            Assert.AreEqual(result.Payout, pair.mirror.Result.Payout);
            Assert.AreNotSame(result, pair.mirror.Result, "The mirror keeps its own copy of the result.");

            // Nothing that arrives late can reopen or change a finished run.
            uint final = SessionDigest.Compute(pair.mirror, true);
            Assert.IsFalse(pair.mirror.ApplyRemoteCollect(d, P2));
            Assert.IsFalse(pair.mirror.ApplyRemoteValue(d, 1));
            Assert.IsFalse(pair.mirror.ApplyRemoteDeposit(d, P2, 1, 0));
            Assert.IsFalse(pair.mirror.ApplyClock(999f, 1f));
            Assert.IsNull(pair.mirror.ApplyRemoteEnd(result));
            Assert.AreEqual(final, SessionDigest.Compute(pair.mirror, true));
        }

        [Test]
        public void Mirror_StaysEqualToTheHost_ThroughAThousandRandomHostDecisions()
        {
            for (int trial = 0; trial < 5; trial++)
            {
                var rng = new Random(9000 + trial);
                var pair = new Pair(12, trial);
                var players = new[] { P0, P1, P2, new PlayerId(3) };
                for (int step = 0; step < 200 && pair.host.Phase == MissionPhase.Active; step++)
                {
                    string id = pair.ids[rng.Next(pair.ids.Count)];
                    PlayerId by = players[rng.Next(players.Length)];
                    switch (rng.Next(7))
                    {
                        case 0: case 1: pair.Collect(id, by); break;
                        case 2:
                            pair.host.TryGetSalvage(id, out var record);
                            pair.Damage(id, record.Value - rng.Next(-5, 30));
                            break;
                        case 3: pair.Deposit(id, by, rng.Next(3) == 0 ? 10 + rng.Next(40) : 0); break;
                        case 4: pair.OpenPanel("panel_" + rng.Next(6), by); break;
                        case 5: pair.Tick((float)rng.NextDouble() * 9f, rng.Next(3) == 0); break;
                        case 6:
                            if (step > 150 && rng.Next(4) == 0) pair.End(rng.Next(2) == 0 ? pair.host.Extract(by) : pair.host.Fail(FailureReason.Caught));
                            break;
                    }
                }
                if (pair.host.Phase == MissionPhase.Active) pair.End(pair.host.Extract(P0));
                Assert.Greater(pair.messages, 40, "trial " + trial + " should have exercised the mirror.");
                pair.AssertSame("at the end of trial " + trial, true);
            }
        }

        [Test]
        public void Digest_NoticesEveryKindOfDifference_AndIgnoresTheClockUnlessAsked()
        {
            MissionSession Fresh()
            {
                var s = new MissionSession(100);
                s.Begin(5, 300, 600f);
                s.RegisterSalvage("a", "scrap", 100);
                s.RegisterSalvage("b", "board", 150);
                return s;
            }
            uint baseline = SessionDigest.Compute(Fresh());
            Assert.AreEqual(baseline, SessionDigest.Compute(Fresh()));
            Assert.AreEqual(0u, SessionDigest.Compute(null));

            var changes = new Dictionary<string, Action<MissionSession>>
            {
                { "a pickup", s => s.TryCollect("a", P0) },
                { "who picked it up", s => s.TryCollect("a", P1) },
                { "a deposit", s => s.TryDeposit("a", P0) },
                { "a damaged value", s => s.DamageSalvage("b", 149) },
                { "an open panel", s => s.TryOpenPanel("panel", P0) },
                { "an extra item", s => s.RegisterSalvage("c", "core", 600) },
                { "the hazard pay", s => s.BonusMultiplier = 1.5f },
                { "the outcome", s => s.Fail(FailureReason.Caught) },
                { "a trick shot", s => { s.TryDeposit("a", P0); s.AwardTrickShot("a", 5); } },
            };
            var seen = new HashSet<uint> { baseline };
            foreach (var change in changes)
            {
                var s = Fresh();
                change.Value(s);
                Assert.IsTrue(seen.Add(SessionDigest.Compute(s)), "The digest did not notice " + change.Key + ".");
            }
            var otherRun = new MissionSession(100);
            otherRun.Begin(5, 300, 600f, 9);
            otherRun.RegisterSalvage("a", "scrap", 100);
            otherRun.RegisterSalvage("b", "board", 150);
            Assert.AreNotEqual(baseline, SessionDigest.Compute(otherRun), "The run number is part of the digest.");

            // Order of registration is not content.
            var reversed = new MissionSession(100);
            reversed.Begin(5, 300, 600f);
            reversed.RegisterSalvage("b", "board", 150);
            reversed.RegisterSalvage("a", "scrap", 100);
            Assert.AreEqual(baseline, SessionDigest.Compute(reversed));

            // The clock is streamed, not replicated exactly: left out unless asked for.
            var later = Fresh();
            later.Tick(30f, false);
            Assert.AreEqual(baseline, SessionDigest.Compute(later));
            Assert.AreNotEqual(SessionDigest.Compute(Fresh(), true), SessionDigest.Compute(later, true));
            StringAssert.Contains("a scrap 100 InWreck", SessionDigest.Describe(later));
        }

        [Test]
        public void RemoteEnd_ThenBank_PaysThisDeviceExactlyOnce_AndAFailurePaysNothing()
        {
            // Extraction: every device banks the full payout into its own save, once.
            var pair = new Pair(4);
            pair.Deposit(pair.ids[0], P0, 0);
            pair.Deposit(pair.ids[1], P1, 0);
            pair.Deposit(pair.ids[2], P1, 0);
            pair.Deposit(pair.ids[3], P0, 20);
            var result = pair.host.Extract(P0);
            Assert.AreEqual(MissionOutcome.ContractComplete, result.Outcome);
            Assert.Greater(result.Payout, 400);

            var clientProfile = new ProfileService(new MemorySaveStorage());
            clientProfile.Load();
            clientProfile.Data.totalCredits = 50;
            Assert.AreEqual(0, pair.mirror.BankInto(clientProfile), "Nothing to bank while the run is still going.");
            pair.End(result); // delivered twice
            Assert.IsFalse(pair.mirror.Banked);
            Assert.AreEqual(result.Payout, pair.mirror.BankInto(clientProfile));
            Assert.AreEqual(0, pair.mirror.BankInto(clientProfile), "The same result must not pay twice.");
            Assert.IsNull(pair.mirror.ApplyRemoteEnd(result));
            Assert.AreEqual(0, pair.mirror.BankInto(clientProfile));
            Assert.AreEqual(50 + result.Payout, clientProfile.Data.totalCredits);
            Assert.AreEqual(1, clientProfile.Data.contractsCompleted);

            // The host banks its own copy the same way; the two saves never meet.
            var hostProfile = new ProfileService(new MemorySaveStorage());
            hostProfile.Load();
            Assert.AreEqual(result.Payout, pair.host.BankInto(hostProfile));
            Assert.AreEqual(result.Payout, hostProfile.Data.totalCredits);

            // Caught: the run is over for everyone and nobody is paid.
            var lost = new Pair(3);
            lost.Deposit(lost.ids[0], P0, 0);
            var failure = lost.host.Fail(FailureReason.Caught);
            lost.End(failure);
            Assert.AreEqual(MissionOutcome.FailedCaught, lost.mirror.Result.Outcome);
            Assert.AreEqual(0, lost.mirror.BankInto(clientProfile));
            Assert.IsTrue(lost.mirror.Banked);
            Assert.AreEqual(50 + result.Payout, clientProfile.Data.totalCredits);
            Assert.AreEqual(1, clientProfile.Data.contractsCompleted);

            // And a new run starts clean on the mirror, with the host's run number again.
            Assert.IsTrue(lost.host.Begin(8, 100, 300f));
            Assert.IsTrue(lost.mirror.Begin(8, 100, 300f, lost.host.RunIndex));
            Assert.IsFalse(lost.mirror.Banked);
            lost.AssertSame("on the next run", true);
        }

        [Test]
        public void Clock_MovesTheMirrorsOxygenAndWarnings_ButNeverEndsTheRun()
        {
            var mirror = new MissionSession(100, 0.3f, 0.12f);
            mirror.Begin(1, 100, 100f);
            var levels = new List<string>();
            mirror.Oxygen.LevelChanged += (from, to) => levels.Add(from + ">" + to);

            Assert.IsTrue(mirror.ApplyClock(10f, 90f));
            Assert.AreEqual(90f, mirror.Oxygen.Remaining);
            Assert.AreEqual(10f, mirror.ElapsedSeconds);
            Assert.IsTrue(mirror.ApplyClock(80f, 25f));
            Assert.IsTrue(mirror.ApplyClock(80f, 25f));
            Assert.IsTrue(mirror.ApplyClock(95f, 8f));
            // A late datagram can carry an older reading; the warning follows whatever the tank now says.
            Assert.IsTrue(mirror.ApplyClock(81f, 24f));
            Assert.IsTrue(mirror.ApplyClock(100f, 0f));
            CollectionAssert.AreEqual(new[] { "Normal>Low", "Low>Critical", "Critical>Low", "Low>Depleted" }, levels);
            Assert.AreEqual(MissionPhase.Active, mirror.Phase, "Only the host's verdict ends a client's run, never its own tank reading.");
            Assert.IsNull(mirror.Result);

            mirror.Oxygen.SetRemaining(500f);
            Assert.AreEqual(100f, mirror.Oxygen.Remaining, "Never more than the tank holds.");
            mirror.Oxygen.SetRemaining(-3f);
            Assert.AreEqual(0f, mirror.Oxygen.Remaining);
            mirror.Oxygen.SetRemaining(float.NaN);
            Assert.AreEqual(0f, mirror.Oxygen.Remaining);
        }

        [Test]
        public void AbortToHub_DropsTheRun_WithNoResult_AndNothingToBank()
        {
            var session = new MissionSession(100);
            Assert.IsFalse(session.AbortToHub(), "Nothing to abort in the hub.");
            session.Begin(1, 100, 300f);
            session.RegisterSalvage("a", "scrap", 100);
            session.TryDeposit("a", P0);
            session.TryOpenPanel("panel", P0);
            var phases = new List<MissionPhase>();
            int ended = 0;
            session.PhaseChanged += phases.Add;
            session.Ended += r => ended++;

            Assert.IsTrue(session.AbortToHub());
            Assert.AreEqual(MissionPhase.Hub, session.Phase);
            CollectionAssert.AreEqual(new[] { MissionPhase.Hub }, phases);
            Assert.AreEqual(0, ended);
            Assert.IsNull(session.Result);
            Assert.AreEqual(0, session.DepositedValue);
            Assert.AreEqual(0, session.Items.Count);
            Assert.IsFalse(session.IsPanelOpen("panel"));

            var profile = new ProfileService(new MemorySaveStorage());
            profile.Load();
            Assert.AreEqual(0, session.BankInto(profile));
            Assert.AreEqual(0, profile.Data.totalCredits);
            Assert.AreEqual(0, profile.Data.contractsCompleted);

            // A results screen is left the ordinary way, not by aborting.
            session.Begin(2, 100, 300f);
            session.Extract(P0);
            Assert.IsFalse(session.AbortToHub());
            Assert.AreEqual(MissionPhase.Results, session.Phase);
        }
    }
}

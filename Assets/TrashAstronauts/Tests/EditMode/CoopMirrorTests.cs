using System.Collections.Generic;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Net;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Co-op stage 2 without a scene: the scene-less ledger mirror (<see cref="CoopSessionMirror"/>) follows a
    /// host's books from its broadcasts alone, every message applied twice changes nothing, and the run is paid
    /// into the mirror's own save once. Also the epoch order items use on the wire.
    /// </summary>
    public class CoopMirrorTests
    {
        static readonly string[] k_Points = { "p_a", "p_b", "p_c", "p_d" };
        static readonly string[] k_Definitions = { "scrap_plate", "circuit_board", "novelty_rubber_duck" };

        static NetIdTable Table()
        {
            return new NetIdTable(new[] { "door_tug_hatch", "door_a" },
                new[] { new KeyValuePair<string, int>("panel_a", 3), new KeyValuePair<string, int>("panel_b", 2) },
                k_Points, new string[0], new[] { "freighter" }, k_Definitions, 1);
        }

        static RunStartParams Run(int runIndex)
        {
            var run = new RunStartParams
            {
                seed = 77, runIndex = (ushort)runIndex, quota = 300, oxygenCapacitySeconds = 400f, bonusMultiplier = 1.25f,
                creatureIndex = RunStartParams.NoCreature,
                items = new[]
                {
                    new RunItem { pointIndex = 0, definitionIndex = 0, value = 120 },
                    new RunItem { pointIndex = 2, definitionIndex = 1, value = 250 },
                    new RunItem { pointIndex = 3, definitionIndex = 2, value = 50, flags = RunItem.FlagBonus },
                },
            };
            run.planHash = run.ComputePlanHash();
            return run;
        }

        /// <summary>A host's books, kept by hand the way the host's director keeps them.</summary>
        static MissionSession HostBegin(NetIdTable ids, in RunStartParams run)
        {
            var host = new MissionSession(400);
            Assert.IsTrue(host.Begin(run.seed, run.quota, run.oxygenCapacitySeconds, run.runIndex));
            host.BonusMultiplier = run.bonusMultiplier;
            foreach (var item in run.items)
                Assert.IsTrue(host.RegisterSalvage(ids.SalvageIdFor(host.RunIndex, item), ids.Definitions[item.definitionIndex], item.value));
            return host;
        }

        [Test]
        public void Mirror_FollowsTheHostsBooks_FromTheBroadcastsAlone_AndEveryMessageTwiceChangesNothing()
        {
            var ids = Table();
            var run = Run(12);
            var host = HostBegin(ids, in run);
            var profile = new ProfileService(new MemorySaveStorage());
            var mirror = new CoopSessionMirror(ids, new MissionSession(400), profile);
            // The mirror has a history of its own: a different run counter must not matter.
            Assert.IsTrue(mirror.Session.Begin(1, 10, 10f));
            mirror.Session.Fail(FailureReason.Caught);

            void Twice(System.Action send, string step)
            {
                send();
                Assert.AreEqual(SessionDigest.Describe(host), SessionDigest.Describe(mirror.Session), step);
                uint once = SessionDigest.Compute(mirror.Session, true);
                send();
                Assert.AreEqual(once, SessionDigest.Compute(mirror.Session, true), step + " (applied twice)");
            }

            Twice(() => mirror.RunStart(in run), "run start");
            Assert.AreEqual(ids.SalvageIdFor(12, run.items[2]), mirror.SalvageIdOf(2));
            StringAssert.EndsWith(NetIdTable.BonusSuffix, mirror.SalvageIdOf(2));
            string scrap = mirror.SalvageIdOf(0), board = mirror.SalvageIdOf(1), duck = mirror.SalvageIdOf(2);

            Assert.IsTrue(host.TryCollect(scrap, new PlayerId(1)));
            Twice(() => mirror.ItemCollected(0, 1), "collected");
            Assert.IsTrue(host.DamageSalvage(board, 200));
            Twice(() => mirror.ItemValue(1, 200), "value");
            Assert.AreEqual(DepositResult.Accepted, host.TryDeposit(scrap, new PlayerId(1)));
            Assert.IsTrue(host.AwardTrickShot(scrap, 30));
            Twice(() => mirror.ItemDeposited(0, 1, 120, 30, 0), "trick-shot deposit");
            Assert.AreEqual(DepositResult.Accepted, host.TryDeposit(duck, PlayerId.Local));
            Twice(() => mirror.ItemDeposited(2, 0, 50, 0, 1), "duck");
            Assert.IsTrue(host.TryOpenPanel("panel_b", new PlayerId(2)));
            Twice(() => mirror.PanelOpened((byte)ids.Panels.IndexOf("panel_b"), 2, NetVec3.Zero, NetVec3.Zero), "panel");
            host.Tick(30f, false);
            Twice(() => mirror.ApplyClock(host.ElapsedSeconds, host.Oxygen.Remaining), "clock");
            Assert.AreEqual(SessionDigest.Compute(host, true), SessionDigest.Compute(mirror.Session, true));

            // Messages that only move things in a scene leave the books alone.
            uint books = SessionDigest.Compute(mirror.Session, true);
            mirror.DoorState(0, true);
            mirror.PointCut(0, 1);
            mirror.ItemRest(1, NetVec3.Zero, NetQuat.Identity);
            mirror.ItemRecovered(1, NetVec3.Zero);
            mirror.HostPause(true);
            mirror.ExtractRefused(1, 4);
            Assert.AreEqual(books, SessionDigest.Compute(mirror.Session, true));
            Assert.AreEqual(4, mirror.LastMissingMask);

            // The end: the host's figures, and this save paid once.
            var result = host.Extract(PlayerId.Local);
            Assert.IsNotNull(result);
            Assert.Greater(result.Payout, 0);
            var message = RunEndedMsg.From(result);
            Twice(() => mirror.RunEnded(in message), "run ended");
            Assert.AreEqual(result.Payout, mirror.CreditsBanked);
            Assert.AreEqual(result.Payout, profile.Data.totalCredits);
            Assert.AreEqual(SessionDigest.Compute(host, true), SessionDigest.Compute(mirror.Session, true));
            Assert.IsTrue(host.ReturnToHub());
            Twice(() => mirror.ReturnedToHub(), "hub");
            Assert.AreEqual(result.Payout, profile.Data.totalCredits);

            // Unknown ids are ignored.
            mirror.ItemCollected(200, 1);
            mirror.PanelOpened(200, 1, NetVec3.Zero, NetVec3.Zero);
            Assert.AreEqual(SessionDigest.Compute(host, true), SessionDigest.Compute(mirror.Session, true));
        }

        [Test]
        public void Mirror_ANewRunWhileOneIsStillOpen_DropsTheOldOne_AndPaysNothingForIt()
        {
            var ids = Table();
            var profile = new ProfileService(new MemorySaveStorage());
            var mirror = new CoopSessionMirror(ids, new MissionSession(400), profile);
            var first = Run(3);
            mirror.RunStart(in first);
            mirror.ItemDeposited(1, 1, 250, 0, 0);
            Assert.AreEqual(250, mirror.Session.DepositedValue);

            var second = Run(4);
            mirror.RunStart(in second);
            Assert.AreEqual(4, mirror.Session.RunIndex);
            Assert.AreEqual(0, mirror.Session.DepositedValue);
            StringAssert.StartsWith("run4/", mirror.SalvageIdOf(0));
            Assert.AreEqual(0, profile.Data.totalCredits);
            Assert.AreEqual(0, mirror.CreditsBanked);
        }

        [Test]
        public void Epochs_CompareOnSixBits_WithWrapAround()
        {
            Assert.AreEqual(1, CoopEpoch.Compare(-1, 0), "The first epoch seen is taken.");
            Assert.AreEqual(0, CoopEpoch.Compare(5, 5));
            Assert.AreEqual(0, CoopEpoch.Compare(5, 5 + 64), "An 8-bit epoch is compared on its low 6 bits.");
            Assert.AreEqual(1, CoopEpoch.Compare(5, 6));
            Assert.AreEqual(-1, CoopEpoch.Compare(6, 5));
            Assert.AreEqual(1, CoopEpoch.Compare(63, 0), "63 -> 0 wraps forward.");
            Assert.AreEqual(-1, CoopEpoch.Compare(0, 63));
            Assert.AreEqual(1, CoopEpoch.Compare(10, 41));
            Assert.AreEqual(-1, CoopEpoch.Compare(10, 42), "More than half the range ahead is behind.");
        }
    }
}

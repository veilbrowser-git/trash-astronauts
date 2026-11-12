using NUnit.Framework;
using TrashAstronauts.Core;

namespace TrashAstronauts.Tests
{
    public class MissionRulesTests
    {
        const int Quota = 300;
        const int Bonus = 100;
        const float Oxygen = 600f;

        static MissionSession NewRun(out ProfileService profile, int startingCredits = 0)
        {
            profile = new ProfileService(new MemorySaveStorage());
            profile.Load();
            profile.Data.totalCredits = startingCredits;
            var session = new MissionSession(Bonus);
            Assert.IsTrue(session.Begin(1234, Quota, Oxygen));
            session.RegisterSalvage("a", "scrap", 100);
            session.RegisterSalvage("b", "board", 150);
            session.RegisterSalvage("c", "core", 600);
            return session;
        }

        [Test]
        public void TrickShot_PaysABonusOnce_OnlyForDepositedSalvage_AndCountsTowardQuota()
        {
            var session = NewRun(out var profile);
            Assert.IsFalse(session.AwardTrickShot("a", 25), "Nothing deposited yet.");
            session.TryDeposit("a", PlayerId.Local);
            session.TryDeposit("b", PlayerId.Local);
            Assert.IsFalse(session.QuotaMet, "250 of 300.");
            Assert.IsTrue(session.AwardTrickShot("a", 25));
            Assert.IsFalse(session.AwardTrickShot("a", 25), "One bonus per item.");
            Assert.IsFalse(session.AwardTrickShot("zzz", 25));
            Assert.IsFalse(session.AwardTrickShot("b", 0));
            Assert.IsTrue(session.AwardTrickShot("b", 38));
            Assert.AreEqual(2, session.TrickShots);
            Assert.AreEqual(63, session.TrickBonus);
            Assert.AreEqual(313, session.SecuredValue);
            Assert.IsTrue(session.QuotaMet, "Trick-shot bonuses count toward the quota.");

            var result = session.Extract(PlayerId.Local);
            Assert.AreEqual(2, result.TrickShots);
            Assert.AreEqual(63, result.TrickBonus);
            Assert.AreEqual(250 + 63 + Bonus, result.Payout);
        }

        [Test]
        public void Extract_AtOrAboveQuota_IsContractComplete_AndPaysDepositsPlusBonus()
        {
            var session = NewRun(out var profile);
            session.TryDeposit("a", PlayerId.Local);
            session.TryDeposit("c", PlayerId.Local);

            var result = session.Extract(PlayerId.Local);

            Assert.AreEqual(MissionOutcome.ContractComplete, result.Outcome);
            Assert.AreEqual(700, result.DepositedValue);
            Assert.AreEqual(700 + Bonus, result.Payout);
            Assert.AreEqual(700 + Bonus, session.BankInto(profile));
            Assert.AreEqual(700 + Bonus, profile.Data.totalCredits);
        }

        [Test]
        public void Extract_BelowQuota_IsQuotaMissed_AndPaysOnlyRecoveredSalvage()
        {
            var session = NewRun(out var profile);
            session.TryDeposit("b", PlayerId.Local);

            var result = session.Extract(PlayerId.Local);

            Assert.AreEqual(MissionOutcome.QuotaMissed, result.Outcome);
            Assert.AreEqual(150, result.Payout);
            Assert.AreEqual(0, result.Bonus);
            session.BankInto(profile);
            Assert.AreEqual(150, profile.Data.totalCredits);
        }

        [Test]
        public void OxygenRunningOut_FailsTheRun_AndForfeitsEarnings()
        {
            var session = NewRun(out var profile, 500);
            session.TryDeposit("c", PlayerId.Local);

            Assert.IsNull(session.Tick(Oxygen - 1f, false));
            var result = session.Tick(2f, false);

            Assert.IsNotNull(result);
            Assert.AreEqual(MissionOutcome.FailedOxygen, result.Outcome);
            Assert.AreEqual(0, result.Payout);
            Assert.AreEqual(MissionPhase.Results, session.Phase);
            session.BankInto(profile);
            Assert.AreEqual(500, profile.Data.totalCredits, "Credits from earlier runs must survive a failure.");
        }

        [Test]
        public void BeingCaught_FailsTheRun_AndKeepsPreviouslyBankedCredits()
        {
            var session = NewRun(out var profile, 820);
            session.TryDeposit("a", PlayerId.Local);

            var result = session.Fail(FailureReason.Caught);

            Assert.AreEqual(MissionOutcome.FailedCaught, result.Outcome);
            Assert.AreEqual(0, result.Payout);
            Assert.AreEqual(0, session.BankInto(profile));
            Assert.AreEqual(820, profile.Data.totalCredits);
        }

        [Test]
        public void Deposit_IsAcceptedExactlyOncePerItem()
        {
            var session = NewRun(out _);
            int events = 0;
            session.SalvageDeposited += _ => events++;

            Assert.AreEqual(DepositResult.Accepted, session.TryDeposit("a", PlayerId.Local));
            Assert.AreEqual(DepositResult.AlreadyDeposited, session.TryDeposit("a", PlayerId.Local));
            Assert.AreEqual(DepositResult.AlreadyDeposited, session.TryDeposit("a", new PlayerId(3)));

            Assert.AreEqual(100, session.DepositedValue);
            Assert.AreEqual(1, session.DepositedCount);
            Assert.AreEqual(1, events);
        }

        [Test]
        public void Deposit_RejectsUnknownItems_AndItemsOutsideAnActiveRun()
        {
            var session = NewRun(out _);
            Assert.AreEqual(DepositResult.UnknownItem, session.TryDeposit("nope", PlayerId.Local));
            session.Extract(PlayerId.Local);
            Assert.AreEqual(DepositResult.MissionNotActive, session.TryDeposit("a", PlayerId.Local));
            Assert.AreEqual(0, session.DepositedValue);
        }

        [Test]
        public void Payout_IsBankedExactlyOnce()
        {
            var session = NewRun(out var profile);
            session.TryDeposit("c", PlayerId.Local);
            session.Extract(PlayerId.Local);

            Assert.AreEqual(700, session.BankInto(profile));
            Assert.AreEqual(0, session.BankInto(profile));
            Assert.AreEqual(0, session.BankInto(profile));
            Assert.AreEqual(700, profile.Data.totalCredits);
            Assert.IsTrue(session.Banked);
        }

        [Test]
        public void Banking_BeforeTheRunEnds_DoesNothing()
        {
            var session = NewRun(out var profile);
            session.TryDeposit("c", PlayerId.Local);
            Assert.AreEqual(0, session.BankInto(profile));
            Assert.AreEqual(0, profile.Data.totalCredits);
        }

        [Test]
        public void ExtractAndFail_OnlyWorkOncePerRun()
        {
            var session = NewRun(out _);
            Assert.IsNotNull(session.Extract(PlayerId.Local));
            Assert.IsNull(session.Extract(PlayerId.Local));
            Assert.IsNull(session.Fail(FailureReason.Caught));
            Assert.IsNull(session.Tick(10000f, false));
        }

        [Test]
        public void Oxygen_DoesNotDrainInTheSafeZone()
        {
            var session = NewRun(out _);
            session.Tick(120f, true);
            Assert.AreEqual(Oxygen, session.Oxygen.Remaining, 0.001f);
            session.Tick(120f, false);
            Assert.AreEqual(Oxygen - 120f, session.Oxygen.Remaining, 0.001f);
        }

        [Test]
        public void Oxygen_WarningLevels_FireInOrder()
        {
            var tank = new OxygenTank(100f, 0.3f, 0.12f);
            var seen = new System.Collections.Generic.List<OxygenWarningLevel>();
            tank.LevelChanged += (_, next) => seen.Add(next);

            tank.Drain(60f);
            Assert.AreEqual(OxygenWarningLevel.Normal, tank.Level);
            tank.Drain(11f);
            Assert.AreEqual(OxygenWarningLevel.Low, tank.Level);
            tank.Drain(18f);
            Assert.AreEqual(OxygenWarningLevel.Critical, tank.Level);
            Assert.IsTrue(tank.Drain(50f));
            Assert.AreEqual(OxygenWarningLevel.Depleted, tank.Level);
            CollectionAssert.AreEqual(new[] { OxygenWarningLevel.Low, OxygenWarningLevel.Critical, OxygenWarningLevel.Depleted }, seen);
        }

        [Test]
        public void Panels_OpenOnce_AndOnlyDuringARun()
        {
            var session = NewRun(out _);
            Assert.IsTrue(session.TryOpenPanel("panel_reactor", PlayerId.Local));
            Assert.IsFalse(session.TryOpenPanel("panel_reactor", PlayerId.Local));
            Assert.IsTrue(session.IsPanelOpen("panel_reactor"));
            session.Extract(PlayerId.Local);
            Assert.IsFalse(session.TryOpenPanel("panel_cage", PlayerId.Local));
        }

        [Test]
        public void Restart_ProducesACleanRun()
        {
            var session = NewRun(out var profile);
            session.TryCollect("a", PlayerId.Local);
            session.TryDeposit("a", PlayerId.Local);
            session.TryOpenPanel("panel_reactor", PlayerId.Local);
            session.Tick(200f, false);
            session.Fail(FailureReason.Caught);
            session.BankInto(profile);
            int firstRun = session.RunIndex;

            Assert.IsTrue(session.Begin(99, 450, 780f), "A new run must be startable straight from the results screen.");

            Assert.AreEqual(MissionPhase.Active, session.Phase);
            Assert.AreEqual(firstRun + 1, session.RunIndex);
            Assert.AreEqual(450, session.Quota);
            Assert.AreEqual(0, session.DepositedValue);
            Assert.AreEqual(0, session.DepositedCount);
            Assert.AreEqual(0, session.Items.Count);
            Assert.AreEqual(780f, session.Oxygen.Remaining, 0.001f);
            Assert.AreEqual(OxygenWarningLevel.Normal, session.Oxygen.Level);
            Assert.IsFalse(session.IsPanelOpen("panel_reactor"));
            Assert.IsFalse(session.Banked);
            Assert.IsNull(session.Result);
            Assert.AreEqual(DepositResult.UnknownItem, session.TryDeposit("a", PlayerId.Local), "Last run's salvage must not carry over.");
        }

        [Test]
        public void CannotBeginWhileARunIsActive()
        {
            var session = NewRun(out _);
            Assert.IsFalse(session.Begin(5, 100, 100f));
            Assert.AreEqual(3, session.Items.Count);
        }

        [Test]
        public void ReturnToHub_ClearsEverything()
        {
            var session = NewRun(out _);
            session.TryDeposit("a", PlayerId.Local);
            session.Extract(PlayerId.Local);
            Assert.IsTrue(session.ReturnToHub());
            Assert.AreEqual(MissionPhase.Hub, session.Phase);
            Assert.AreEqual(0, session.DepositedValue);
            Assert.IsNull(session.Result);
        }
    }
}

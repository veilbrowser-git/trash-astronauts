using System;
using System.Collections.Generic;

namespace TrashAstronauts.Core
{
    public enum MissionPhase
    {
        /// <summary>Safe in the tug, no contract running. Tutorial, shop and settings live here.</summary>
        Hub = 0,
        /// <summary>Contract running: oxygen drains outside the tug and the threat is awake.</summary>
        Active = 1,
        /// <summary>Contract ended (extracted or failed). Waiting for the player to start again.</summary>
        Results = 2,
    }

    public enum MissionOutcome
    {
        None = 0,
        ContractComplete = 1,
        QuotaMissed = 2,
        FailedOxygen = 3,
        FailedCaught = 4,
    }

    public enum FailureReason
    {
        OxygenDepleted = 0,
        Caught = 1,
    }

    public enum SalvageState
    {
        InWreck = 0,
        Collected = 1,
        Deposited = 2,
    }

    public enum DepositResult
    {
        Accepted = 0,
        MissionNotActive = 1,
        UnknownItem = 2,
        AlreadyDeposited = 3,
    }

    public sealed class SalvageRecord
    {
        public string Id;
        public string DefinitionId;
        public int Value;
        public SalvageState State;
        public PlayerId CollectedBy = PlayerId.None;
        public PlayerId DepositedBy = PlayerId.None;
        public bool TrickShot;
    }

    public sealed class MissionResult
    {
        public MissionOutcome Outcome;
        public int Quota;
        public int DepositedValue;
        public int DepositedCount;
        /// <summary>Extra credits earned from trick shots this run.</summary>
        public int TrickBonus;
        public int TrickShots;
        public int Bonus;
        /// <summary>Credits this run pays out. Zero on failure.</summary>
        public int Payout;
        public float OxygenRemainingSeconds;
        public float ElapsedSeconds;

        public bool IsFailure => Outcome == MissionOutcome.FailedOxygen || Outcome == MissionOutcome.FailedCaught;
        public bool QuotaMet => DepositedValue + TrickBonus >= Quota;

        /// <summary>Co-op revive: how many times each crew slot was downed this contract (null offline and for a crew nobody was downed in).</summary>
        public int[] CrewDowns;
        /// <summary>Co-op revive: bit n = slot n was still downed when the crew extracted (brought home by the company).</summary>
        public int LeftBehindMask;

        public int DownsOf(int slot) => CrewDowns != null && slot >= 0 && slot < CrewDowns.Length ? CrewDowns[slot] : 0;
        public bool WasLeftBehind(int slot) => slot >= 0 && slot < 31 && (LeftBehindMask & (1 << slot)) != 0;
        public bool AnyRevivePenalty
        {
            get
            {
                if (LeftBehindMask != 0) return true;
                if (CrewDowns == null) return false;
                foreach (int d in CrewDowns) if (d > 0) return true;
                return false;
            }
        }

        /// <summary>
        /// What crew slot <paramref name="slot"/> banks: the crew payout less that player's own revive penalties
        /// (<see cref="ReviveRules"/>). A negative slot (single-player) is the payout itself.
        /// </summary>
        public int PayoutFor(int slot) => slot < 0 ? Payout : ReviveRules.PayoutFor(Payout, DownsOf(slot), WasLeftBehind(slot));
    }

    /// <summary>
    /// The authoritative rules for one salvage run: salvage ledger, panels, oxygen, outcome and payout.
    ///
    /// This class has no scene, input or headset knowledge. Every shared-state change goes through one of
    /// its methods (Begin, TryCollect, TryDeposit, TryOpenPanel, Extract, Fail, BankInto). In co-op the
    /// host would own the only writable MissionSession and clients would send these calls as requests.
    /// </summary>
    public sealed class MissionSession
    {
        readonly Dictionary<string, SalvageRecord> m_Items = new Dictionary<string, SalvageRecord>();
        readonly HashSet<string> m_OpenPanels = new HashSet<string>();
        readonly int m_ContractBonus;
        readonly float m_LowFraction;
        readonly float m_CriticalFraction;

        public MissionPhase Phase { get; private set; } = MissionPhase.Hub;
        public int RunIndex { get; private set; }
        public int Seed { get; private set; }
        public int Quota { get; private set; }
        public int DepositedValue { get; private set; }
        public int DepositedCount { get; private set; }
        /// <summary>Extra credits from trick shots. Counts toward the quota and is paid out on extraction.</summary>
        public int TrickBonus { get; private set; }
        public int TrickShots { get; private set; }
        public int SecuredValue => DepositedValue + TrickBonus;
        public float ElapsedSeconds { get; private set; }
        public OxygenTank Oxygen { get; }
        public MissionResult Result { get; private set; }
        /// <summary>True once this run's payout has been added to the profile. Guards against double payment.</summary>
        public bool Banked { get; private set; }

        public IReadOnlyCollection<SalvageRecord> Items => m_Items.Values;
        /// <summary>Ids of the panels cut open this run.</summary>
        public IReadOnlyCollection<string> OpenPanels => m_OpenPanels;
        public bool QuotaMet => SecuredValue >= Quota;

        public event Action<MissionPhase> PhaseChanged;
        public event Action<SalvageRecord> SalvageCollected;
        public event Action<SalvageRecord> SalvageDeposited;
        public event Action<string, PlayerId> PanelOpened;
        public event Action<MissionResult> Ended;

        public MissionSession(int contractBonus = 0, float oxygenLowFraction = 0.3f, float oxygenCriticalFraction = 0.12f)
        {
            m_ContractBonus = Math.Max(0, contractBonus);
            m_LowFraction = oxygenLowFraction;
            m_CriticalFraction = oxygenCriticalFraction;
            Oxygen = new OxygenTank(1f, m_LowFraction, m_CriticalFraction);
        }

        public static MissionSession FromConfig(MissionConfig config)
        {
            return new MissionSession(config.contractBonusCredits, config.oxygenLowFraction, config.oxygenCriticalFraction);
        }

        /// <summary>Starts a fresh run. Allowed from Hub or Results; clears every trace of the previous run.</summary>
        /// <summary>Hazard pay: scales the contract bonus for this run. Set after <see cref="Begin"/>; reset by it.</summary>
        public float BonusMultiplier { get; set; } = 1f;

        /// <param name="runIndex">
        /// The run number to take instead of counting up. In co-op the host sends its own, so salvage ids
        /// ("run&lt;N&gt;/...") match on machines that have played a different number of runs.
        /// </param>
        public bool Begin(int seed, int quota, float oxygenCapacitySeconds, int? runIndex = null)
        {
            if (Phase == MissionPhase.Active) return false;

            ClearRunState();
            BonusMultiplier = 1f;
            if (runIndex.HasValue) RunIndex = runIndex.Value;
            else RunIndex++;
            Seed = seed;
            Quota = Math.Max(0, quota);
            Oxygen.Refill(oxygenCapacitySeconds, m_LowFraction, m_CriticalFraction);
            SetPhase(MissionPhase.Active);
            return true;
        }

        /// <summary>Registers a spawned salvage item. Ids must be unique within the run.</summary>
        public bool RegisterSalvage(string id, string definitionId, int value)
        {
            if (Phase != MissionPhase.Active || string.IsNullOrEmpty(id) || m_Items.ContainsKey(id)) return false;
            m_Items.Add(id, new SalvageRecord
            {
                Id = id,
                DefinitionId = definitionId,
                Value = Math.Max(0, value),
                State = SalvageState.InWreck,
            });
            return true;
        }

        public bool TryGetSalvage(string id, out SalvageRecord record)
        {
            if (id == null) { record = null; return false; }
            return m_Items.TryGetValue(id, out record);
        }

        public int TotalRegisteredValue
        {
            get
            {
                int total = 0;
                foreach (var item in m_Items.Values) total += item.Value;
                return total;
            }
        }

        /// <summary>Marks an item as picked up for the first time. Returns false if nothing changed.</summary>
        public bool TryCollect(string id, PlayerId by)
        {
            if (Phase != MissionPhase.Active) return false;
            if (!TryGetSalvage(id, out var record) || record.State != SalvageState.InWreck) return false;
            record.State = SalvageState.Collected;
            record.CollectedBy = by;
            SalvageCollected?.Invoke(record);
            return true;
        }

        /// <summary>Secures an item in the tug locker. Each item is accepted, and counted, exactly once.</summary>
        public DepositResult TryDeposit(string id, PlayerId by)
        {
            if (Phase != MissionPhase.Active) return DepositResult.MissionNotActive;
            if (!TryGetSalvage(id, out var record)) return DepositResult.UnknownItem;
            if (record.State == SalvageState.Deposited) return DepositResult.AlreadyDeposited;

            record.State = SalvageState.Deposited;
            record.DepositedBy = by;
            DepositedValue += record.Value;
            DepositedCount++;
            SalvageDeposited?.Invoke(record);
            return DepositResult.Accepted;
        }

        /// <summary>Adds a trick-shot bonus for an item that has just been deposited. One bonus per item.</summary>
        public bool AwardTrickShot(string salvageId, int bonus)
        {
            if (Phase != MissionPhase.Active || bonus <= 0) return false;
            if (!TryGetSalvage(salvageId, out var record) || record.State != SalvageState.Deposited || record.TrickShot) return false;
            record.TrickShot = true;
            TrickBonus += bonus;
            TrickShots++;
            return true;
        }

        /// <summary>Lowers what an item is worth after it took a knock. It can only go down, and not once it is in the locker.</summary>
        public bool DamageSalvage(string salvageId, int newValue)
        {
            if (Phase != MissionPhase.Active) return false;
            if (!TryGetSalvage(salvageId, out var record) || record.State == SalvageState.Deposited) return false;
            newValue = Math.Max(0, newValue);
            if (newValue >= record.Value) return false;
            record.Value = newValue;
            return true;
        }

        public bool IsPanelOpen(string panelId) => panelId != null && m_OpenPanels.Contains(panelId);

        /// <summary>Opens a cuttable panel or mount. Returns false if it was already open.</summary>
        public bool TryOpenPanel(string panelId, PlayerId by)
        {
            if (Phase != MissionPhase.Active || string.IsNullOrEmpty(panelId)) return false;
            if (!m_OpenPanels.Add(panelId)) return false;
            PanelOpened?.Invoke(panelId, by);
            return true;
        }

        /// <summary>
        /// Advances mission time. Oxygen only drains when the player is outside the safe tug.
        /// Returns the failure result if oxygen ran out on this tick, otherwise null.
        /// </summary>
        public MissionResult Tick(float deltaSeconds, bool playerInSafeZone)
        {
            if (Phase != MissionPhase.Active || deltaSeconds <= 0f) return null;
            ElapsedSeconds += deltaSeconds;
            if (playerInSafeZone) return null;
            return Oxygen.Drain(deltaSeconds) ? Fail(FailureReason.OxygenDepleted) : null;
        }

        /// <summary>Manual extraction from the tug. Pays for whatever was deposited; adds the bonus if the quota was met.</summary>
        public MissionResult Extract(PlayerId by)
        {
            if (Phase != MissionPhase.Active) return null;
            bool met = QuotaMet;
            int bonus = met ? (int)Math.Round(m_ContractBonus * Math.Max(0f, BonusMultiplier)) : 0;
            return End(met ? MissionOutcome.ContractComplete : MissionOutcome.QuotaMissed, SecuredValue + bonus, bonus);
        }

        /// <summary>Fails the run. This run's earnings are forfeited; banked credits are untouched.</summary>
        public MissionResult Fail(FailureReason reason)
        {
            if (Phase != MissionPhase.Active) return null;
            return End(reason == FailureReason.Caught ? MissionOutcome.FailedCaught : MissionOutcome.FailedOxygen, 0, 0);
        }

        MissionResult End(MissionOutcome outcome, int payout, int bonus)
        {
            Result = new MissionResult
            {
                Outcome = outcome,
                Quota = Quota,
                DepositedValue = DepositedValue,
                DepositedCount = DepositedCount,
                TrickBonus = TrickBonus,
                TrickShots = TrickShots,
                Bonus = bonus,
                Payout = Math.Max(0, payout),
                OxygenRemainingSeconds = Oxygen.Remaining,
                ElapsedSeconds = ElapsedSeconds,
            };
            SetPhase(MissionPhase.Results);
            Ended?.Invoke(Result);
            return Result;
        }

        /// <summary>
        /// Adds this run's payout to the profile exactly once. Returns the credits added (0 if already banked,
        /// not finished, or failed).
        /// </summary>
        public int BankInto(ProfileService profile) => BankInto(profile, -1);

        /// <summary>
        /// As <see cref="BankInto(ProfileService)"/>, for crew slot <paramref name="slot"/>: that player's own share
        /// after their revive penalties (<see cref="MissionResult.PayoutFor"/>). -1 = the whole payout (single-player).
        /// </summary>
        public int BankInto(ProfileService profile, int slot)
        {
            if (Phase != MissionPhase.Results || Result == null || Banked || profile == null) return 0;
            Banked = true;
            int payout = Result.PayoutFor(slot);
            profile.RecordRunFinished(payout, !Result.IsFailure);
            return payout;
        }

        /// <summary>Leaves the results screen and returns to the hub with no run state.</summary>
        public bool ReturnToHub()
        {
            if (Phase != MissionPhase.Results) return false;
            ClearRunState();
            SetPhase(MissionPhase.Hub);
            return true;
        }

        // ------------------------------------------------------------------ mirror (co-op clients)
        //
        // A client keeps a MissionSession that only ever follows the host's. These methods apply what the
        // host decided; none of them judges anything, and every one of them can be applied twice (or
        // arrive after the thing it describes is already true) without changing the result.

        /// <summary>The host recorded the first pickup of an item. Returns false if nothing changed.</summary>
        public bool ApplyRemoteCollect(string id, PlayerId by)
        {
            if (Phase != MissionPhase.Active) return false;
            if (!TryGetSalvage(id, out var record) || record.State != SalvageState.InWreck) return false;
            record.State = SalvageState.Collected;
            record.CollectedBy = by;
            SalvageCollected?.Invoke(record);
            return true;
        }

        /// <summary>The host changed what an item is worth. Ignored once the item is in the locker.</summary>
        public bool ApplyRemoteValue(string id, int value)
        {
            if (Phase != MissionPhase.Active) return false;
            if (!TryGetSalvage(id, out var record) || record.State == SalvageState.Deposited) return false;
            value = Math.Max(0, value);
            if (record.Value == value) return false;
            record.Value = value;
            return true;
        }

        /// <summary>
        /// The host secured an item in the locker for <paramref name="finalValue"/> credits, with
        /// <paramref name="trickBonus"/> extra if it was a trick shot. Counted once per item.
        /// </summary>
        public bool ApplyRemoteDeposit(string id, PlayerId by, int finalValue, int trickBonus)
        {
            if (Phase != MissionPhase.Active) return false;
            if (!TryGetSalvage(id, out var record) || record.State == SalvageState.Deposited) return false;
            record.Value = Math.Max(0, finalValue);
            record.State = SalvageState.Deposited;
            record.DepositedBy = by;
            DepositedValue += record.Value;
            DepositedCount++;
            if (trickBonus > 0)
            {
                record.TrickShot = true;
                TrickBonus += trickBonus;
                TrickShots++;
            }
            SalvageDeposited?.Invoke(record);
            return true;
        }

        /// <summary>The host's clock: mission time and what is left in the shared tank. Never ends the run.</summary>
        public bool ApplyClock(float elapsedSeconds, float oxygenRemainingSeconds)
        {
            if (Phase != MissionPhase.Active) return false;
            ElapsedSeconds = Math.Max(0f, elapsedSeconds);
            Oxygen.SetRemaining(oxygenRemainingSeconds);
            return true;
        }

        /// <summary>
        /// The host ended the run with this result. The mirror takes the host's figures as they are and
        /// moves to Results; <see cref="BankInto"/> then pays this device once. Returns the stored result,
        /// or null if the run was not active (already ended: applying it again changes nothing).
        /// </summary>
        public MissionResult ApplyRemoteEnd(MissionResult result)
        {
            if (Phase != MissionPhase.Active || result == null) return null;
            DepositedValue = result.DepositedValue;
            DepositedCount = result.DepositedCount;
            TrickBonus = result.TrickBonus;
            TrickShots = result.TrickShots;
            ElapsedSeconds = result.ElapsedSeconds;
            Oxygen.SetRemaining(result.OxygenRemainingSeconds);
            Result = new MissionResult
            {
                Outcome = result.Outcome,
                Quota = result.Quota,
                DepositedValue = result.DepositedValue,
                DepositedCount = result.DepositedCount,
                TrickBonus = result.TrickBonus,
                TrickShots = result.TrickShots,
                Bonus = result.Bonus,
                Payout = Math.Max(0, result.Payout),
                OxygenRemainingSeconds = result.OxygenRemainingSeconds,
                ElapsedSeconds = result.ElapsedSeconds,
                CrewDowns = result.CrewDowns != null ? (int[])result.CrewDowns.Clone() : null,
                LeftBehindMask = result.LeftBehindMask,
            };
            SetPhase(MissionPhase.Results);
            Ended?.Invoke(Result);
            return Result;
        }

        /// <summary>
        /// Drops a running contract and goes straight back to the hub (the link to the host was lost, or
        /// the player left the session). There is no result and nothing can be banked.
        /// </summary>
        public bool AbortToHub()
        {
            if (Phase != MissionPhase.Active) return false;
            ClearRunState();
            SetPhase(MissionPhase.Hub);
            return true;
        }

        void ClearRunState()
        {
            m_Items.Clear();
            m_OpenPanels.Clear();
            DepositedValue = 0;
            DepositedCount = 0;
            TrickBonus = 0;
            TrickShots = 0;
            ElapsedSeconds = 0f;
            Result = null;
            Banked = false;
        }

        void SetPhase(MissionPhase phase)
        {
            if (Phase == phase) return;
            Phase = phase;
            PhaseChanged?.Invoke(phase);
        }
    }
}

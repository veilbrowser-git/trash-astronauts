using TMPro;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.UI
{
    /// <summary>
    /// The tug's mission board: contract briefing and quota, live progress, extraction (with a confirm
    /// press), the results screen and the oxygen upgrade shop. Operated with two big physical buttons.
    /// </summary>
    public sealed class MissionBoard : MonoBehaviour
    {
        public MissionDirector director;
        public TMP_Text title;
        public TMP_Text body;
        public PushButton primaryButton;
        public PushButton upgradeButton;
        public float confirmWindowSeconds = 4f;
        public float refreshInterval = 0.5f;

        float m_ConfirmUntil;
        float m_NextRefresh;
        string m_Notice;
        float m_NoticeUntil;

        static readonly string[] k_SuccessQuips =
        {
            "Management is briefly satisfied.",
            "Your bonus has been rounded down for your convenience.",
            "Somebody upstairs almost learned your name.",
        };
        static readonly string[] k_PartialQuips =
        {
            "A disappointing amount of garbage.",
            "The shortfall has been noted in your file.",
            "Partial credit. Emphasis on partial.",
        };
        static readonly string[] k_OxygenQuips =
        {
            "Breathing is not covered by your contract.",
            "The recall tether dragged you home. The salvage stayed.",
        };
        static readonly string[] k_CaughtQuips =
        {
            "JANITOR-7 filed you under 'recyclables' and returned you to sender.",
            "You were tidied up. Your salvage was confiscated as litter.",
        };

        void OnEnable()
        {
            if (director == null) director = MissionDirector.Active;
            if (primaryButton != null) primaryButton.Pressed += OnPrimary;
            if (upgradeButton != null) upgradeButton.Pressed += OnUpgrade;
            if (director != null)
            {
                director.MissionStarted += RefreshNow;
                director.MissionEnded += OnMissionEnded;
                director.SalvageDeposited += OnDeposited;
                director.ProfileChanged += RefreshNow;
            }
        }

        void OnDisable()
        {
            if (primaryButton != null) primaryButton.Pressed -= OnPrimary;
            if (upgradeButton != null) upgradeButton.Pressed -= OnUpgrade;
            if (director != null)
            {
                director.MissionStarted -= RefreshNow;
                director.MissionEnded -= OnMissionEnded;
                director.SalvageDeposited -= OnDeposited;
                director.ProfileChanged -= RefreshNow;
            }
        }

        void Start() => Refresh();

        void Update()
        {
            if (Time.unscaledTime < m_NextRefresh) return;
            m_NextRefresh = Time.unscaledTime + refreshInterval;
            Refresh();
        }

        void RefreshNow() => Refresh();
        void OnMissionEnded(MissionResult result) { m_ConfirmUntil = 0f; Refresh(); }
        void OnDeposited(SalvageItem item) => Refresh();

        void OnPrimary(PushButton button, HandRig hand)
        {
            if (director == null) return;
            PlayerId by = hand != null && hand.Player != null ? hand.Player.Id : director.LocalPlayerId;

            switch (director.Phase)
            {
                case MissionPhase.Hub:
                case MissionPhase.Results:
                    director.StartMission(by);
                    break;
                case MissionPhase.Active:
                    if (Time.unscaledTime <= m_ConfirmUntil)
                    {
                        m_ConfirmUntil = 0f;
                        // On a co-op client the press is only a request; the host answers.
                        bool asked = director.Net != null && director.Net.IsClient;
                        if (director.Extract(by) == null && !asked) Notice("Extraction needs you inside the tug.");
                    }
                    else
                    {
                        m_ConfirmUntil = Time.unscaledTime + confirmWindowSeconds;
                    }
                    break;
            }
            Refresh();
        }

        void OnUpgrade(PushButton button, HandRig hand)
        {
            if (director == null) return;
            if (director.PurchaseOxygenUpgrade()) Notice("O2 TANK+ installed. Enjoy the extra breathing.");
            Refresh();
        }

        void Notice(string message)
        {
            m_Notice = message;
            m_NoticeUntil = Time.unscaledTime + 4f;
        }

        public void Refresh()
        {
            if (director == null || director.Session == null) return;
            var session = director.Session;
            var config = director.Config;
            var profile = director.Profile.Data;
            string notice = Time.unscaledTime < m_NoticeUntil ? "\n\n> " + m_Notice : "";

            switch (session.Phase)
            {
                case MissionPhase.Hub:
                    Set(title, "SALVAGE CONTRACT");
                    Set(body,
                        "Wreck: assigned when you launch (3 on the books)\n"
                        + "Quota: " + config.quotaCredits + " cr     Bonus: +" + config.contractBonusCredits + " cr\n"
                        + "Oxygen: " + OxygenTank.FormatClock(director.NextRunOxygenCapacity) + " (drains outside this tug)\n\n"
                        + "NOTICE: a different wreck and a different THING\n"
                        + "each contract. It can HEAR YOUR VOICE.\n\n"
                        + "Banked credits: " + profile.totalCredits + " cr" + notice);
                    SetPrimary("START CONTRACT", true);
                    break;

                case MissionPhase.Active:
                    bool confirming = Time.unscaledTime <= m_ConfirmUntil;
                    Set(title, "CONTRACT ACTIVE");
                    Set(body,
                        "Secured: " + session.SecuredValue + " / " + session.Quota + " cr  (" + session.DepositedCount + " items)\n"
                        + "Oxygen: " + OxygenTank.FormatClock(session.Oxygen.Remaining) + "\n"
                        + (director.ActiveMap != null ? "Wreck: " + director.ActiveMap.displayName + "\n" : "")
                        + "Conditions: " + ContractConditions.Title(director.Condition) + ". " + ContractConditions.Describe(director.Condition) + "\n"
                        + (director.CreatureAboard && director.ActiveEnemy != null ? "Aboard: " + director.ActiveEnemy.displayName + "\n  " + director.ActiveEnemy.trait + "\n\n" : "\n")
                        + (session.QuotaMet
                            ? "QUOTA MET. Extract now for the bonus,\nor risk another trip for more."
                            : "Quota NOT met. Extracting now pays only\nfor what is already secured.")
                        + "\n\nRunning out of oxygen or getting caught\nforfeits everything from this run."
                        + (confirming ? "\n\n> Press again to confirm extraction." : "") + notice);
                    SetPrimary(confirming ? "CONFIRM EXTRACT" : "EXTRACT", true);
                    break;

                case MissionPhase.Results:
                    var result = session.Result;
                    Set(title, Heading(result));
                    Set(body,
                        "Salvage secured: " + result.DepositedValue + " cr  (" + result.DepositedCount + " items)\n"
                        + (result.TrickShots > 0 ? "Trick shots: " + result.TrickShots + "  (+" + result.TrickBonus + " cr)\n" : "")
                        + (director.Profile.LastRunWasRecord ? "NEW BEST HAUL!\n" : "")
                        + "Quota: " + result.Quota + " cr\n"
                        + "Contract bonus: +" + result.Bonus + " cr\n"
                        + "PAYOUT: " + result.Payout + " cr\n"
                        + "Banked credits: " + profile.totalCredits + " cr\n\n"
                        + Quip(result, session.RunIndex) + notice);
                    SetPrimary("NEXT CONTRACT", true);
                    break;
            }

            RefreshUpgradeButton(session.Phase, config, profile);
        }

        void RefreshUpgradeButton(MissionPhase phase, MissionConfig config, SaveData profile)
        {
            if (upgradeButton == null) return;
            string bonus = "+" + OxygenTank.FormatClock(config.oxygenUpgradeBonusSeconds);
            if (profile.oxygenUpgradeLevel >= config.maxOxygenUpgradeLevel)
            {
                upgradeButton.SetLabel("O2 TANK+ OWNED\n" + bonus + " oxygen");
                upgradeButton.SetInteractable(false);
            }
            else if (phase == MissionPhase.Active)
            {
                upgradeButton.SetLabel("SHOP CLOSED\nduring contract");
                upgradeButton.SetInteractable(false);
            }
            else if (profile.totalCredits >= config.oxygenUpgradeCost)
            {
                upgradeButton.SetLabel("BUY O2 TANK+\n" + bonus + " for " + config.oxygenUpgradeCost + " cr");
                upgradeButton.SetInteractable(true);
            }
            else
            {
                upgradeButton.SetLabel("O2 TANK+ (" + bonus + ")\nNeed " + config.oxygenUpgradeCost + " cr");
                upgradeButton.SetInteractable(false);
            }
        }

        static string Heading(MissionResult result)
        {
            switch (result.Outcome)
            {
                case MissionOutcome.ContractComplete: return "CONTRACT COMPLETE";
                case MissionOutcome.QuotaMissed: return "QUOTA MISSED";
                case MissionOutcome.FailedOxygen: return "RUN FAILED: OUT OF OXYGEN";
                case MissionOutcome.FailedCaught: return "RUN FAILED: CAUGHT";
                default: return "CONTRACT OVER";
            }
        }

        static string Quip(MissionResult result, int runIndex)
        {
            string[] pool;
            switch (result.Outcome)
            {
                case MissionOutcome.ContractComplete: pool = k_SuccessQuips; break;
                case MissionOutcome.QuotaMissed: pool = k_PartialQuips; break;
                case MissionOutcome.FailedOxygen: pool = k_OxygenQuips; break;
                default: pool = k_CaughtQuips; break;
            }
            return pool[Mathf.Abs(runIndex) % pool.Length];
        }

        void SetPrimary(string label, bool interactable)
        {
            if (primaryButton == null) return;
            primaryButton.SetLabel(label);
            primaryButton.SetInteractable(interactable);
        }

        static void Set(TMP_Text text, string value)
        {
            if (text != null && text.text != value) text.text = value;
        }
    }
}

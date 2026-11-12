using TMPro;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.UI
{
    /// <summary>
    /// The tug's company store: tranquilizer darts and tool upgrades, paid for with banked credits.
    /// It is closed while a contract is running. Every button shows its price and whether you can
    /// afford it in words, not just by colour.
    /// </summary>
    public sealed class ShopPanel : MonoBehaviour
    {
        public MissionDirector director;
        public TMP_Text header;
        public PushButton tranqButton;
        public PushButton scannerButton;
        public PushButton cutterButton;
        public float refreshInterval = 0.5f;

        float m_NextRefresh;
        string m_Notice;
        float m_NoticeUntil;

        void OnEnable()
        {
            if (director == null) director = MissionDirector.Active;
            if (tranqButton != null) tranqButton.Pressed += OnTranq;
            if (scannerButton != null) scannerButton.Pressed += OnScanner;
            if (cutterButton != null) cutterButton.Pressed += OnCutter;
            if (director != null) director.ProfileChanged += Refresh;
        }

        void OnDisable()
        {
            if (tranqButton != null) tranqButton.Pressed -= OnTranq;
            if (scannerButton != null) scannerButton.Pressed -= OnScanner;
            if (cutterButton != null) cutterButton.Pressed -= OnCutter;
            if (director != null) director.ProfileChanged -= Refresh;
        }

        void Start() => Refresh();

        void Update()
        {
            if (Time.unscaledTime < m_NextRefresh) return;
            m_NextRefresh = Time.unscaledTime + refreshInterval;
            Refresh();
        }

        void OnTranq(PushButton b, HandRig hand) => Buy(ShopItem.TranqDart, "Dart loaded. It naps for " + Mathf.RoundToInt(director.Config.tranqSleepSeconds) + " seconds. Aim well.");
        void OnScanner(PushButton b, HandRig hand) => Buy(ShopItem.ScannerRange, "Scanner range extended.");
        void OnCutter(PushButton b, HandRig hand) => Buy(ShopItem.CutterCoolant, "Coolant fitted. The cutter runs longer.");

        void Buy(ShopItem item, string success)
        {
            if (director == null) return;
            if (director.PurchaseShopItem(item))
            {
                m_Notice = success;
                if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.deposit, transform.position, 0.7f, 1.2f, 6f);
            }
            else
            {
                m_Notice = director.Phase == MissionPhase.Active ? "The shop is closed during a contract." : "Not enough credits.";
            }
            m_NoticeUntil = Time.unscaledTime + 4f;
            Refresh();
        }

        public void Refresh()
        {
            if (director == null || director.Profile == null) return;
            var config = director.Config;
            var data = director.Profile.Data;
            bool open = director.Phase != MissionPhase.Active;

            if (header != null)
            {
                string text = "COMPANY STORE\nCredits: " + data.totalCredits + " cr     Darts: " + data.tranqDarts + " / " + config.maxTranqDarts;
                if (!open) text += "\nCLOSED during a contract";
                else if (Time.unscaledTime < m_NoticeUntil) text += "\n> " + m_Notice;
                if (header.text != text) header.text = text;
            }

            Set(tranqButton, open, ShopItem.TranqDart, "TRANQ DART", "sleeps it " + Mathf.RoundToInt(config.tranqSleepSeconds) + "s",
                data.tranqDarts >= config.maxTranqDarts ? "FULL" : null);
            Set(scannerButton, open, ShopItem.ScannerRange, "SCANNER RANGE+", "x" + config.scannerUpgradeRangeMultiplier.ToString("0.0") + " range",
                data.scannerUpgrade ? "OWNED" : null);
            Set(cutterButton, open, ShopItem.CutterCoolant, "CUTTER COOLANT", "x" + config.cutterUpgradeHeatMultiplier.ToString("0.0") + " run time",
                data.cutterUpgrade ? "OWNED" : null);
        }

        void Set(PushButton button, bool open, ShopItem item, string title, string effect, string soldOut)
        {
            if (button == null) return;
            int cost = director.Config.GetShopCost(item);
            if (soldOut != null)
            {
                button.SetLabel(title + "\n" + soldOut);
                button.SetInteractable(false);
            }
            else if (!open)
            {
                button.SetLabel(title + "\nCLOSED");
                button.SetInteractable(false);
            }
            else if (director.Profile.CanPurchase(item, director.Config))
            {
                button.SetLabel(title + "\n" + effect + " - " + cost + " cr");
                button.SetInteractable(true);
            }
            else
            {
                button.SetLabel(title + "\nNeed " + cost + " cr");
                button.SetInteractable(false);
            }
        }
    }
}

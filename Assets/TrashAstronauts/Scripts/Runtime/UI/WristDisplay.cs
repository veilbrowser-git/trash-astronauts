using TMPro;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using UnityEngine;

namespace TrashAstronauts.UI
{
    /// <summary>
    /// The suit's wrist readout, worn on the off hand: oxygen (bar, percentage, clock and a worded
    /// status), secured salvage versus quota, and what the drone is doing. Every warning is conveyed
    /// with words and blinking as well as colour.
    /// </summary>
    public sealed class WristDisplay : MonoBehaviour
    {
        public PlayerContext player;
        public TMP_Text oxygenText;
        public TMP_Text salvageText;
        public TMP_Text threatText;
        public Transform oxygenBar;
        public Renderer oxygenBarRenderer;
        public float refreshInterval = 0.2f;

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly Color k_Ok = new Color(0.3f, 0.95f, 1f);
        static readonly Color k_Low = new Color(1f, 0.8f, 0.2f);
        static readonly Color k_Critical = new Color(1f, 0.25f, 0.2f);

        MaterialPropertyBlock m_Block;
        float m_NextRefresh;

        void Update()
        {
            if (Time.unscaledTime < m_NextRefresh) return;
            m_NextRefresh = Time.unscaledTime + refreshInterval;
            Refresh();
        }

        public void Refresh()
        {
            var director = MissionDirector.Active;
            if (director == null || director.Session == null) return;
            var session = director.Session;
            bool blinkOn = Mathf.FloorToInt(Time.unscaledTime * 2.5f) % 2 == 0;

            float fraction;
            string oxygen;
            Color barColor = k_Ok;

            if (session.Phase == MissionPhase.Active)
            {
                var tank = session.Oxygen;
                fraction = tank.Fraction;
                string status;
                switch (tank.Level)
                {
                    case OxygenWarningLevel.Low: status = "LOW"; barColor = k_Low; break;
                    case OxygenWarningLevel.Critical: status = blinkOn ? "!! CRITICAL !!" : "   CRITICAL   "; barColor = k_Critical; break;
                    case OxygenWarningLevel.Depleted: status = "EMPTY"; barColor = k_Critical; break;
                    default: status = "OK"; break;
                }
                oxygen = "O2 " + Mathf.CeilToInt(fraction * 100f) + "%  " + OxygenTank.FormatClock(tank.Remaining) + "\n" + status;
                if (director.HazardsPaused) oxygen += "  (PAUSED)";
                else if (player != null && player.InSafeZone) oxygen += "  (TUG: NOT DRAINING)";
            }
            else
            {
                fraction = 1f;
                oxygen = "O2 100%  " + OxygenTank.FormatClock(director.NextRunOxygenCapacity) + "\nTANK FULL";
            }

            Set(oxygenText, oxygen);
            if (oxygenBar != null) oxygenBar.localScale = new Vector3(Mathf.Max(0.001f, fraction), 1f, 1f);
            if (oxygenBarRenderer != null)
            {
                if (m_Block == null) m_Block = new MaterialPropertyBlock();
                oxygenBarRenderer.GetPropertyBlock(m_Block);
                m_Block.SetColor(k_BaseColor, barColor);
                oxygenBarRenderer.SetPropertyBlock(m_Block);
            }

            switch (session.Phase)
            {
                case MissionPhase.Active:
                    Set(salvageText, "SECURED " + session.DepositedValue + " / " + session.Quota + " cr"
                        + (session.QuotaMet ? "\nQUOTA MET" : ""));
                    break;
                case MissionPhase.Results:
                    Set(salvageText, "CONTRACT OVER\nSee mission board");
                    break;
                default:
                    Set(salvageText, "NO CONTRACT\nBanked " + director.Profile.Data.totalCredits + " cr");
                    break;
            }

            Set(threatText, DescribeThreat(director, blinkOn));
        }

        static string DescribeThreat(MissionDirector director, bool blinkOn)
        {
            if (director.Session.Phase != MissionPhase.Active || director.enemies == null) return "";
            EnemyDrone worst = null;
            foreach (var enemy in director.enemies)
            {
                if (enemy == null) continue;
                if (worst == null || Severity(enemy.State) > Severity(worst.State)) worst = enemy;
            }
            if (worst == null) return "";

            string range = worst.NearestPlayerDistance < 7f ? " (CLOSE)" : "";
            switch (worst.State)
            {
                case EnemyState.Patrol: return "DRONE: ROAMING" + range;
                case EnemyState.Investigate: return "DRONE: HEARD A NOISE" + range;
                case EnemyState.Search: return "DRONE: SEARCHING" + range;
                case EnemyState.Chase: return blinkOn ? "!! DRONE HUNTING YOU !!" : "   DRONE HUNTING YOU   ";
                default: return "DRONE: DORMANT";
            }
        }

        static int Severity(EnemyState state)
        {
            switch (state)
            {
                case EnemyState.Chase: return 4;
                case EnemyState.Search: return 3;
                case EnemyState.Investigate: return 2;
                case EnemyState.Patrol: return 1;
                default: return 0;
            }
        }

        static void Set(TMP_Text text, string value)
        {
            if (text != null && text.text != value) text.text = value;
        }
    }
}

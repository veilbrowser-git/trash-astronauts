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
        public TMP_Text voiceText;
        [Tooltip("Noise meter: scaled along X from 0 (silent) to 1 (the whole deck heard that).")]
        public Transform noiseBar;
        public Renderer noiseBarRenderer;

        /// <summary>0..1: how loud the player is being right now (voice or the last loud thing they did, fading).</summary>
        public float NoiseLevel { get; private set; }
        public string NoiseLabel { get; private set; } = "";
        float m_EventLevel;
        string m_EventLabel = "";
        NoiseSystem m_Hooked;

        void OnDisable()
        {
            if (m_Hooked != null) m_Hooked.NoiseEmitted -= OnNoise;
            m_Hooked = null;
        }

        void OnNoise(NoiseEvent noise)
        {
            // Only what this player did: their voice, tools, thruster, and things they threw or dropped.
            if (noise.Kind == NoiseKind.Voice) return; // the live microphone level covers the voice
            if (player != null && !noise.Source.Equals(player.Id)) return;
            if (noise.Loudness < m_EventLevel) return;
            m_EventLevel = Mathf.Clamp01(noise.Loudness);
            switch (noise.Kind)
            {
                case NoiseKind.Cutting: m_EventLabel = "CUTTER"; break;
                case NoiseKind.PanelBreak: m_EventLabel = "PANEL"; break;
                case NoiseKind.Door: m_EventLabel = "DOOR"; break;
                case NoiseKind.Thruster: m_EventLabel = "THRUSTER"; break;
                case NoiseKind.Web: m_EventLabel = "WEB"; break;
                default: m_EventLabel = "IMPACT"; break;
            }
        }

        /// <summary>A line about the flashlight battery, once it is worth worrying about.</summary>
        string LightNote()
        {
            if (player == null || player.tools == null) return "";
            foreach (var tool in player.tools)
            {
                if (tool is Tools.FlashlightTool torch)
                    return torch.IsFlat ? "\nLIGHT FLAT: switch off to charge" : torch.Charge < 0.3f ? "\nLIGHT " + Mathf.CeilToInt(torch.Charge * 100f) + "%" : "";
            }
            return "";
        }

        void UpdateNoiseMeter()
        {
            var director = MissionDirector.Active;
            if (director == null) return;
            if (director.noise != m_Hooked)
            {
                if (m_Hooked != null) m_Hooked.NoiseEmitted -= OnNoise;
                m_Hooked = director.noise;
                if (m_Hooked != null) m_Hooked.NoiseEmitted += OnNoise;
            }

            m_EventLevel = Mathf.Max(0f, m_EventLevel - Time.unscaledDeltaTime * 0.45f);
            var voice = player != null ? player.voice : null;
            float voiceLevel = voice != null && voice.Listening ? voice.Loudness * voice.loudnessScale : 0f;
            bool voiceWins = voiceLevel >= m_EventLevel && voiceLevel > 0.02f;
            float target = Mathf.Max(voiceLevel, m_EventLevel);
            NoiseLevel = target > NoiseLevel ? target : Mathf.MoveTowards(NoiseLevel, target, Time.unscaledDeltaTime * 1.5f);
            NoiseLabel = NoiseLevel < 0.03f ? "" : voiceWins ? "VOICE" : m_EventLabel;

            if (noiseBar != null) noiseBar.localScale = new Vector3(Mathf.Max(0.001f, Mathf.Clamp01(NoiseLevel)), 1f, 1f);
            if (noiseBarRenderer != null)
            {
                if (m_Block == null) m_Block = new MaterialPropertyBlock();
                noiseBarRenderer.GetPropertyBlock(m_Block);
                m_Block.SetColor(k_BaseColor, NoiseLevel < 0.3f ? new Color(0.3f, 1f, 0.5f) : NoiseLevel < 0.6f ? k_Low : k_Critical);
                noiseBarRenderer.SetPropertyBlock(m_Block);
            }
            if (voiceText != null)
            {
                var creature = director.ActiveEnemy;
                bool heard = director.Phase == MissionPhase.Active && creature != null && Time.time - creature.LastHeardNoiseTime < 2f;
                string label = heard ? "!! IT HEARD THAT !!" : NoiseLabel.Length > 0 ? "NOISE: " + NoiseLabel : "NOISE: quiet";
                if (voice != null && !voice.Listening && voice.Status.Length > 0) label += "  (" + voice.Status + ")";
                else if (voice != null && voice.CaptureActive) label += "  REC";
                if (voiceText.text != label) voiceText.text = label;
            }
        }
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
            UpdateNoiseMeter();
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
                    Set(salvageText, "SECURED " + session.SecuredValue + " / " + session.Quota + " cr"
                        + (session.QuotaMet ? "\nQUOTA MET" : "") + LightNote());
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

            if (worst.IsSedated) return "DRONE: SEDATED " + Mathf.CeilToInt(worst.SedatedSecondsLeft) + "s";
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

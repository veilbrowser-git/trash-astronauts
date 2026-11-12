using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// The one way out for a downed player whose crew has wandered off: GIVE UP on the wrist. Every button is off while
    /// downed (DownedLock), so this does not use them: a fingertip of either glove held on the wrist display's crew
    /// lines (where "GIVE UP" is written) for <see cref="HoldSeconds"/> calls the normal LEAVE (NetSession.Leave).
    /// Letting go before then resets it, so a brush of the wrist does nothing. Added to the local player by
    /// DownedLock while downed; does nothing otherwise.
    /// </summary>
    public sealed class DownedGiveUp : MonoBehaviour
    {
        /// <summary>How long a fingertip must stay on GIVE UP.</summary>
        public const float HoldSeconds = 1.5f;
        /// <summary>A fingertip this close to the crew lines on the wrist counts as on them (m).</summary>
        public const float TouchRadius = 0.04f;

        PlayerContext m_Player;
        Transform m_Spot;

        /// <summary>How far the hold is, 0..1 (shown on the wrist).</summary>
        public float Progress { get; private set; }
        /// <summary>Times this headset gave up (tests).</summary>
        public int GaveUp { get; private set; }

        /// <summary>The wrist spot a fingertip is held on (the crew lines of the wrist display).</summary>
        public Transform Spot
        {
            get
            {
                if (m_Spot == null && m_Player != null)
                {
                    var wrist = m_Player.GetComponentInChildren<UI.WristDisplay>(true);
                    if (wrist != null && wrist.crewText != null) m_Spot = wrist.crewText.transform;
                }
                return m_Spot;
            }
        }

        public static DownedGiveUp For(PlayerContext player)
        {
            var giveUp = player.GetComponent<DownedGiveUp>();
            if (giveUp == null) giveUp = player.gameObject.AddComponent<DownedGiveUp>();
            giveUp.m_Player = player;
            return giveUp;
        }

        /// <summary>The line on the wrist while downed.</summary>
        public static string WristLine(PlayerContext player)
        {
            var giveUp = player != null ? player.GetComponent<DownedGiveUp>() : null;
            float p = giveUp != null ? giveUp.Progress : 0f;
            return "GIVE UP: HOLD A FINGER HERE " + HoldSeconds.ToString("0.0") + " s TO LEAVE CREW"
                + (p > 0f ? " " + Mathf.FloorToInt(p * 100f) + "%" : "");
        }

        void Update() => Step(Time.deltaTime);

        /// <summary>One frame of the hold. Public for tests.</summary>
        public void Step(float dt)
        {
            if (m_Player == null) m_Player = GetComponent<PlayerContext>();
            if (m_Player == null || !m_Player.IsDowned || m_Player.IsRemote) { Progress = 0f; return; }
            var spot = Spot;
            if (spot == null) return;
            if (!Touching(m_Player.leftHand, spot.position) && !Touching(m_Player.rightHand, spot.position))
            {
                Progress = 0f;
                return;
            }
            Progress = Mathf.Min(1f, Progress + dt / HoldSeconds);
            if (Progress < 1f) return;
            Progress = 0f;
            GaveUp++;
            var director = MissionDirector.Active;
            var session = director != null ? director.GetComponent<NetSession>() : null;
            if (session != null) session.Leave();
        }

        static bool Touching(HandRig hand, Vector3 spot)
        {
            return hand != null && hand.fingertip != null && (hand.fingertip.position - spot).sqrMagnitude <= TouchRadius * TouchRadius;
        }
    }
}

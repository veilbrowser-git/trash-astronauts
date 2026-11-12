using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Player;
using TrashAstronauts.Tools;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// A strand of web strung across a route. Blunder through it and it snaps: it drags at you, and the
    /// spider feels it go and comes to look. Burn it away with the plasma cutter (hold the flame on it
    /// for a moment) and it goes quietly. Which ones are strung changes every contract.
    /// </summary>
    public sealed class WebTripwire : MonoBehaviour
    {
        public string wireId = "web";
        public Transform endA, endB;
        [Tooltip("The visible strands. Hidden when the web is gone.")]
        public GameObject visual;
        public float touchRadius = 0.2f;
        [Range(0f, 1.5f)] public float loudness = 0.85f;
        [Tooltip("How much of the player's speed survives being caught in it.")]
        [Range(0f, 1f)] public float slow = 0.3f;
        public float burnSeconds = 0.45f;

        MissionDirector m_Director;
        float m_Burn;

        public bool Armed { get; private set; }
        public bool Snapped { get; private set; }
        public bool Burnt { get; private set; }

        void OnEnable()
        {
            Hook();
            ResetForRun();
        }

        void OnDisable()
        {
            if (m_Director != null) m_Director.MissionStarted -= ResetForRun;
            m_Director = null;
        }

        /// <summary>Finds the director (it may not exist yet when this is first enabled) and listens for new contracts.</summary>
        void Hook()
        {
            var director = MissionDirector.Active;
            if (director == m_Director) return;
            if (m_Director != null) m_Director.MissionStarted -= ResetForRun;
            m_Director = director;
            if (m_Director == null) return;
            m_Director.MissionStarted += ResetForRun;
            ResetForRun();
        }

        /// <summary>Decides from the run's seed whether this web is strung this time.</summary>
        public void ResetForRun()
        {
            Snapped = false;
            Burnt = false;
            m_Burn = 0f;
            bool live = m_Director != null && m_Director.Session != null && m_Director.Phase == MissionPhase.Active;
            if (!live)
            {
                SetArmed(false);
                return;
            }
            int hash = (m_Director.Session.Seed * 31) ^ (wireId != null ? wireId.GetHashCode() : 0);
            float roll = (float)new System.Random(hash).NextDouble();
            SetArmed(roll < ContractConditions.WebChance(m_Director.Condition));
        }

        /// <summary>Test seam: strings or removes the web directly.</summary>
        public void SetArmed(bool armed)
        {
            Armed = armed;
            if (visual != null && visual.activeSelf != armed) visual.SetActive(armed);
        }

        static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return Vector3.Distance(p, a + ab * t);
        }

        void Update()
        {
            if (m_Director == null || m_Director != MissionDirector.Active) Hook();
            if (!Armed || endA == null || endB == null || m_Director == null || m_Director.Phase != MissionPhase.Active) return;
            Vector3 a = endA.position, b = endB.position;
            var players = m_Director.Players;
            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player == null) continue;

                // A cutter flame held on it burns it through without a sound.
                if (player.tools != null)
                {
                    foreach (var tool in player.tools)
                    {
                        if (tool is PlasmaCutterTool cutter && cutter.IsFiring && cutter.tip != null && DistanceToSegment(cutter.tip.position, a, b) < 0.3f)
                        {
                            m_Burn += Time.deltaTime;
                            if (m_Burn >= burnSeconds) { BurnAway(); return; }
                        }
                    }
                }

                // Head, chest and hips: any of them through the strand and it goes.
                Vector3 head = player.HeadPosition;
                if (DistanceToSegment(head, a, b) < touchRadius
                    || DistanceToSegment(head + Vector3.down * 0.45f, a, b) < touchRadius
                    || DistanceToSegment(head + Vector3.down * 0.9f, a, b) < touchRadius)
                {
                    Snap(player);
                    return;
                }
            }
        }

        public void BurnAway()
        {
            if (!Armed) return;
            Burnt = true;
            SetArmed(false);
            if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.cutterSparks, (endA.position + endB.position) * 0.5f, 0.35f, 0.7f, 6f);
        }

        /// <summary>The web goes: the player is slowed, and the spider hears exactly where.</summary>
        public void Snap(PlayerContext player)
        {
            if (!Armed) return;
            Snapped = true;
            SetArmed(false);
            Vector3 at = (endA.position + endB.position) * 0.5f;
            if (player != null)
            {
                at = player.HeadPosition;
                if (player.locomotion != null && player.locomotion.zeroG != null) player.locomotion.zeroG.Damp(slow);
                player.PulseBoth(0.7f, 0.18f);
            }
            if (Sfx.Library != null)
            {
                Sfx.PlayAt(Sfx.Library.scannerPing, at, 0.7f, 0.45f, 14f);
                Sfx.PlayAt(Sfx.Library.cutterSparks, at, 0.6f, 0.6f, 14f);
            }
            if (m_Director != null && m_Director.noise != null)
                m_Director.noise.Emit(new NoiseEvent(at, loudness, NoiseKind.Web, player != null ? player.Id : PlayerId.Local));
        }
    }
}

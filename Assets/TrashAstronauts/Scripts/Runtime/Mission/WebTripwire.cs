using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Player;
using TrashAstronauts.Tools;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>How a web stopped being a web.</summary>
    public enum WireChange : byte
    {
        /// <summary>Somebody blundered through it. Loud.</summary>
        Snapped = 0,
        /// <summary>Burnt away with the cutter. Quiet.</summary>
        Burnt = 1,
    }

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
        bool m_Reported;

        public bool Armed { get; private set; }
        public bool Snapped { get; private set; }
        public bool Burnt { get; private set; }

        /// <summary>
        /// The web went (this web, how, who did it): raised once per web per contract, by
        /// <see cref="ApplyState"/>, on whichever machine applies it. A host listens to tell the crew.
        /// </summary>
        public event System.Action<WebTripwire, WireChange, PlayerId> Changed;

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

        /// <summary>
        /// Strings this web or not, as the contract's parameters say. Whoever started the run decided
        /// (<see cref="WouldArm"/>, in MissionDirector.BuildRunParams) and wrote the answer into
        /// <see cref="TrashAstronauts.Core.Net.RunStartParams.wireArmedMask"/>; nothing is worked out again here.
        /// </summary>
        public void ResetForRun()
        {
            Snapped = false;
            Burnt = false;
            m_Burn = 0f;
            m_Reported = false;
            bool live = m_Director != null && m_Director.Session != null && m_Director.Phase == MissionPhase.Active;
            if (!live)
            {
                SetArmed(false);
                return;
            }
            int index = m_Director.NetIds.Wires.IndexOf(wireId);
            if (index < 0 && m_Director.Net == null)
            {
                // A web the director does not list (a content error; the shipped scene has none) has no
                // bit in the mask. Single-player keeps it working the old way; in a session it stays down.
                SetArmed(WouldArm(m_Director.Session.Seed, m_Director.Condition));
                return;
            }
            SetArmed(index >= 0 && index < 32 && (m_Director.CurrentRun.wireArmedMask & (1u << index)) != 0);
        }

        /// <summary>
        /// Whether this web is strung on a contract with this seed and condition. Asked once per contract,
        /// by the machine that starts it: the roll uses string.GetHashCode, which is only guaranteed to
        /// agree with itself inside one process.
        /// </summary>
        public bool WouldArm(int seed, ContractCondition condition)
        {
            int hash = (seed * 31) ^ (wireId != null ? wireId.GetHashCode() : 0);
            float roll = (float)new System.Random(hash).NextDouble();
            return roll < ContractConditions.WebChance(condition);
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
            if (m_Reported) return; // told the host; waiting to hear what became of it
            Vector3 a = endA.position, b = endB.position;
            var players = m_Director.Players;
            // In a session every machine watches only its own player and cutter; the others watch theirs.
            var onlyPlayer = m_Director.Net != null ? m_Director.LocalPlayer : null;
            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player == null || player.IsAway) continue;
                if (m_Director.Net != null && player != onlyPlayer) continue;

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

        bool IsClient => m_Director != null && m_Director.Net != null && m_Director.Net.IsClient;

        public void BurnAway()
        {
            if (!Armed) return;
            if (IsClient)
            {
                // The host owns the web: tell it once and wait for its word.
                if (!m_Reported) m_Director.Net.ReportWire(this, WireChange.Burnt);
                m_Reported = true;
                return;
            }
            ApplyState(WireChange.Burnt, (endA.position + endB.position) * 0.5f, PlayerId.None);
        }

        /// <summary>The web goes: the player is slowed, and the spider hears exactly where.</summary>
        public void Snap(PlayerContext player)
        {
            if (!Armed) return;
            if (IsClient)
            {
                // Feel it at once; the web itself goes when the host says so.
                if (m_Reported) return;
                m_Reported = true;
                ApplyFeel(player);
                m_Director.Net.ReportWire(this, WireChange.Snapped);
                return;
            }
            Vector3 at = player != null ? player.HeadPosition : (endA.position + endB.position) * 0.5f;
            PlayerId by = player != null ? player.Id : m_Director != null ? m_Director.LocalPlayerId : PlayerId.Local;
            ApplyFeel(player);
            ApplyState(WireChange.Snapped, at, by);
        }

        /// <summary>
        /// What blundering into it does to the player who did: it drags at them and their hands buzz.
        /// Only ever for the player wearing this headset; a remote stand-in feels nothing here.
        /// </summary>
        public void ApplyFeel(PlayerContext player)
        {
            if (player == null || player.IsRemote) return;
            if (player.locomotion != null && player.locomotion.zeroG != null) player.locomotion.zeroG.Damp(slow);
            player.PulseBoth(0.7f, 0.18f);
        }

        /// <summary>
        /// The web is gone, for everyone: its state, its strands and its sound. Offline and on a host a
        /// snapped web also makes the noise the spider comes to; a client never emits it (the host did).
        /// Returns false if the web was not strung, so applying a change twice does nothing.
        /// </summary>
        /// <param name="at">Where it happened (a snap is heard at the head that went through it).</param>
        /// <param name="by">Who did it, for the noise.</param>
        public bool ApplyState(WireChange change, Vector3 at, PlayerId by)
        {
            if (!Armed) return false;
            m_Reported = false;
            if (change == WireChange.Burnt)
            {
                Burnt = true;
                SetArmed(false);
                if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.cutterSparks, (endA.position + endB.position) * 0.5f, 0.35f, 0.7f, 6f);
                Changed?.Invoke(this, change, by);
                return true;
            }

            Snapped = true;
            SetArmed(false);
            if (Sfx.Library != null)
            {
                Sfx.PlayAt(Sfx.Library.scannerPing, at, 0.7f, 0.45f, 14f);
                Sfx.PlayAt(Sfx.Library.cutterSparks, at, 0.6f, 0.6f, 14f);
            }
            if (!IsClient && m_Director != null && m_Director.noise != null)
                m_Director.noise.Emit(new NoiseEvent(at, loudness, NoiseKind.Web, by));
            Changed?.Invoke(this, change, by);
            return true;
        }
    }
}

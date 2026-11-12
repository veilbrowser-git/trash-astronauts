using System;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// Co-op stage 4: one creature, simulated only on the host and shown on every client.
    ///
    /// On the host nothing here changes how it hunts: it raises three events the session forwards
    /// (<see cref="StateChangedForCrew"/>, <see cref="CatchBegan"/>, <see cref="Darted"/>) and can be sampled
    /// for the CreatureState stream (<see cref="SampleSnapshot"/>).
    ///
    /// On a client it is a replica (<see cref="SetReplica"/>, set by BeginRun when the director is a client):
    /// no Step, no senses, no pathing, deaf to noise. Its body follows the host's samples about 100 ms behind
    /// and never moves without them; State, IsSedated, SedatedSecondsLeft, IsLurking, IsStaggered and
    /// LastHeardNoiseTime return what the host said, so everything that reads the creature (wrist, scares,
    /// footsteps, eye light, hum, animator, foot planting) works unchanged. State and its cues come only from
    /// the reliable CreatureStateChanged, so a dropped sample never loses or repeats a cue. The jumpscare is
    /// played here, locally, when the host says the catch began; only the host ends the run.
    /// </summary>
    public sealed partial class EnemyDrone
    {
        // ------------------------------------------------------------------ events (host)

        /// <summary>The creature's state changed (host and offline): the new state and who it is after.</summary>
        public event Action<EnemyDrone, EnemyState, PlayerId> StateChangedForCrew;
        /// <summary>It caught someone and the jumpscare is starting (host and offline).</summary>
        public event Action<EnemyDrone, PlayerId> CatchBegan;
        /// <summary>A dart put it to sleep; the seconds it will sleep (host and offline).</summary>
        public event Action<EnemyDrone, float> Darted;

        /// <summary>Who it is after right now (the quarry of its last look), or nobody.</summary>
        public PlayerId Quarry => IsReplica ? m_RepTarget : m_Target != null ? m_Target.Id : PlayerId.None;
        /// <summary>How suspicious it is, 0..1 (the host's figure on a replica).</summary>
        public float Suspicion => IsReplica ? m_RepSuspicion : Brain != null ? Brain.Suspicion : 0f;
        /// <summary>Who made the last noise it reacted to (on a replica: nobody, it does not hear).</summary>
        public PlayerId LastHeardNoiseBy { get; private set; } = PlayerId.None;
        /// <summary>How many times a state change has been presented (chase loop, cue, eye). Test seam.</summary>
        public int StateCuesPresented { get; private set; }

        bool m_HeardToggle;
        SurfaceWalker m_Walker;
        bool m_LookedForWalker;

        /// <summary>The walker that puts this creature's body on walls and ceilings (same object), or null.</summary>
        public SurfaceWalker Walker
        {
            get
            {
                if (!m_LookedForWalker)
                {
                    m_Walker = GetComponent<SurfaceWalker>();
                    if (m_Walker == null) m_Walker = GetComponentInChildren<SurfaceWalker>(true);
                    m_LookedForWalker = true;
                }
                return m_Walker;
            }
        }

        void NoteHeard(in NoiseEvent noise)
        {
            m_HeardToggle = !m_HeardToggle;
            LastHeardNoiseBy = noise.Source;
        }

        static byte SlotOf(PlayerId id) => id.Value >= 0 && id.Value < NetProtocol.MaxPlayers ? (byte)id.Value : NetProtocol.NoSlot;

        /// <summary>
        /// One sample of the creature for the CreatureState stream (host). Where it is, which way it faces, its
        /// state and quarry, how high it wants to be and on which side wall, and its flags.
        /// </summary>
        public CreatureSnapshot SampleSnapshot()
        {
            var walker = Walker;
            byte flags = 0;
            if (m_Running) flags |= CreatureFlags.Running;
            if (IsSedated) flags |= CreatureFlags.Sedated;
            if (IsLurking) flags |= CreatureFlags.Lurking;
            if (m_SeesNow && !IsReplica) flags |= CreatureFlags.SeesQuarry;
            if (walker == null || walker.Side > 0f) flags |= CreatureFlags.WallSide;
            if (m_HeardToggle) flags |= CreatureFlags.HeardNoiseToggle;
            if (IsScaring) flags |= CreatureFlags.Scaring;
            if (IsStaggered) flags |= CreatureFlags.Staggered;
            return new CreatureSnapshot
            {
                position = CoopConvert.ToNet(transform.position),
                yawDegrees = transform.eulerAngles.y,
                state = State,
                targetSlot = SlotOf(Quarry),
                climbStep = walker != null ? CreatureSnapshot.ClimbToStep(walker.TargetClimb) : (byte)0,
                flags = flags,
                suspicion = Suspicion,
                sedationSecondsLeft = SedatedSecondsLeft,
            };
        }

        // ------------------------------------------------------------------ replica (client)

        /// <summary>How long a client keeps the screen dark after its jumpscare waiting for the host's RunEnded.</summary>
        public const float RunEndWaitSeconds = 3f;
        /// <summary>A RunEnded that arrives while the jumpscare is still playing is held at most this long.</summary>
        public const float RunEndHoldSeconds = 1f;

        readonly SnapshotBuffer<CreatureSnapshot> m_RepBuffer = new SnapshotBuffer<CreatureSnapshot>(32);
        EnemyState m_RepState;
        PlayerId m_RepTarget = PlayerId.None;
        byte m_RepFlags;
        float m_RepSuspicion;
        float m_RepSedateTimer;
        bool m_RepHaveToggle;
        bool m_RepToggle;
        bool m_AwaitRunEnd;
        float m_AwaitRunEndUntil;
        bool m_AwaitVictimLocal;
        Action m_HeldRunEnd;
        float m_HeldRunEndSince;

        /// <summary>True on a co-op client: this creature only shows what the host's creature does.</summary>
        public bool IsReplica { get; private set; }
        /// <summary>Samples taken into the replica's buffer.</summary>
        public int SnapshotsApplied { get; private set; }
        /// <summary>Times the replica's run was dropped because the host's RunEnded never came after a catch.</summary>
        public int RunEndLinkLosses { get; private set; }
        /// <summary>True while a client victim's screen stays dark waiting for the host to end the run.</summary>
        public bool AwaitingRunEnd => m_AwaitRunEnd;
        /// <summary>True while a RunEnded is held back for the jumpscare to finish.</summary>
        public bool HoldingRunEnd => m_HeldRunEnd != null;

        bool ReplicaFlag(byte flag) => (m_RepFlags & flag) != 0;

        /// <summary>
        /// Turns replica mode on (a co-op client) or off. A replica does not think, sense, path or hear; it shows
        /// the host's creature. Called by BeginRun on every machine (on only when the director is a client).
        /// </summary>
        public void SetReplica(bool replica)
        {
            if (IsReplica == replica) return;
            IsReplica = replica;
            ResetReplica();
            var walker = Walker;
            if (!replica && walker != null)
            {
                walker.ClimbOverride = null;
                walker.SideOverride = null;
            }
        }

        void ResetReplica()
        {
            m_RepBuffer.Clear();
            m_RepState = EnemyState.Dormant;
            m_RepTarget = PlayerId.None;
            m_RepFlags = 0;
            m_RepSuspicion = 0f;
            m_RepSedateTimer = 0f;
            m_RepHaveToggle = false;
            m_AwaitRunEnd = false;
            m_HeldRunEnd = null;
            m_HeardToggle = false;
            LastHeardNoiseBy = PlayerId.None;
        }

        /// <summary>
        /// A sample of the host's creature, stamped with when it was taken on this machine's unscaled clock.
        /// Ignored while a jumpscare plays here (the jumpscare places the creature itself).
        /// </summary>
        public bool ApplySnapshot(in CreatureSnapshot snapshot, double localTime)
        {
            if (!IsReplica || !m_Running || IsScaring) return false;
            if (!m_RepBuffer.Push(localTime, in snapshot)) return false;
            SnapshotsApplied++;
            return true;
        }

        /// <summary>
        /// The host's creature changed state (reliable CreatureStateChanged). The new state shows at once and its
        /// cue (chase loop, alert or lost call, eye) is presented exactly once for each of these, whatever the
        /// samples did. A wake from sleep ends the replica's sedation.
        /// </summary>
        public bool ApplyStateChanged(EnemyState state, PlayerId target)
        {
            if (!IsReplica || !m_Running) return false;
            var previous = m_RepState;
            m_RepState = state;
            m_RepTarget = target;
            if (state != EnemyState.Dormant) m_RepSedateTimer = 0f;
            if (IsScaring) return true; // the jumpscare owns the eye and the voice
            PresentStateChange(previous, state);
            return true;
        }

        /// <summary>The host darted its creature for this long (reliable Tranquilized). Not mid-jumpscare.</summary>
        public bool ApplyTranquilized(float seconds)
        {
            if (!IsReplica || !m_Running || IsScaring || !(seconds > 0f)) return false;
            m_RepSedateTimer = Mathf.Max(m_RepSedateTimer, seconds);
            if (voice != null) voice.Stop();
            RefreshPresentation(true);
            return true;
        }

        /// <summary>
        /// The host's creature caught <paramref name="victim"/> (reliable CatchStarted): the jumpscare plays here,
        /// unchanged. <paramref name="victimIsLocal"/> only on the victim's own headset (real camera, cracked visor,
        /// dead torch, haptics, fade, sound in the ears); everyone else watches it happen to the victim's avatar
        /// with the sound coming from the creature. With no avatar to play it on, the catch simply waits out its
        /// deadline. The run is not ended here: the host's RunEnded does that.
        /// </summary>
        public bool ApplyCatchStarted(PlayerContext victim, PlayerId victimId, bool victimIsLocal)
        {
            if (!IsReplica || IsScaring) return false;
            m_RepSedateTimer = 0f;
            m_CatchVictim = victimId;
            m_CatchDeadline = Time.unscaledTime + ScareTotalSeconds + CatchDeadlineSlack;
            m_CatchPending = true;
            m_ScareAliveFrame = -1;
            m_AwaitRunEnd = false;
            if (isActiveAndEnabled && victim != null && victim.head != null) StartCoroutine(Jumpscare(victim, victimIsLocal));
            else IsScaring = true;
            return true;
        }

        /// <summary>
        /// A RunEnded that should not cut the jumpscare short: true when it was held (applied once the jumpscare is
        /// over, or after <see cref="RunEndHoldSeconds"/> at most), false when it should be applied now.
        /// </summary>
        public bool HoldRunEnded(Action apply)
        {
            if (!IsReplica || !IsScaring || apply == null) return false;
            if (m_HeldRunEnd == null) m_HeldRunEndSince = Time.unscaledTime;
            m_HeldRunEnd = apply;
            return true;
        }

        /// <summary>The replica's end of a catch: the run is the host's to end. A victim here stays in the dark until it does.</summary>
        void FinishReplicaCatch(PlayerId victim)
        {
            IsScaring = false;
            m_CatchPending = false;
            var local = director != null ? director.LocalPlayer : null;
            m_AwaitVictimLocal = local != null && local.Id == victim;
            if (director == null || director.Phase != MissionPhase.Active) return;
            if (m_AwaitVictimLocal && local.fader != null && local.fader.Alpha < 0.999f) local.fader.FadeTo(1f, 0.04f);
            m_AwaitRunEnd = true;
            m_AwaitRunEndUntil = Time.unscaledTime + RunEndWaitSeconds;
        }

        /// <summary>The run is over on this client: anything left of a jumpscare stops and is put back.</summary>
        void EndReplicaRun()
        {
            m_AwaitRunEnd = false;
            m_HeldRunEnd = null;
            m_RepSedateTimer = 0f;
            // Powered down, as the host's is: nothing reads a hunt that is over.
            m_RepState = EnemyState.Dormant;
            m_RepTarget = PlayerId.None;
            m_RepFlags = 0;
            m_RepBuffer.Clear();
            if (IsScaring || m_CatchPending)
            {
                StopAllCoroutines();
                EndScarePresentation();
                IsScaring = false;
                m_CatchPending = false;
            }
        }

        /// <summary>One frame of the replica. Public so play-mode tests can advance it at a chosen time.</summary>
        public void UpdateReplica(double localNow, float dt)
        {
            if (!IsReplica) return;

            if (m_HeldRunEnd != null && (!IsScaring || Time.unscaledTime - m_HeldRunEndSince >= RunEndHoldSeconds))
            {
                var apply = m_HeldRunEnd;
                m_HeldRunEnd = null;
                apply();
            }
            if (m_AwaitRunEnd)
            {
                if (director == null || director.Phase != MissionPhase.Active) m_AwaitRunEnd = false;
                else if (Time.unscaledTime >= m_AwaitRunEndUntil) DropRunAfterLostLink();
            }

            if (!m_Running) return;
            if (m_RepSedateTimer > 0f && !(director != null && director.HazardsPaused))
                m_RepSedateTimer = Mathf.Max(0f, m_RepSedateTimer - dt);

            if (!IsScaring && m_RepBuffer.Count > 0 && m_RepBuffer.Sample(localNow - CoopItemReplica.Delay, out CreatureSnapshot s))
            {
                transform.SetPositionAndRotation(CoopConvert.ToUnity(s.position), Quaternion.Euler(0f, s.yawDegrees, 0f));
                m_RepFlags = s.flags;
                m_RepSuspicion = s.suspicion;
                bool toggle = s.HasFlag(CreatureFlags.HeardNoiseToggle);
                if (m_RepHaveToggle && toggle != m_RepToggle) LastHeardNoiseTime = Time.time;
                m_RepToggle = toggle;
                m_RepHaveToggle = true;
                var walker = Walker;
                if (walker != null)
                {
                    walker.ClimbOverride = s.TargetClimb;
                    walker.SideOverride = s.HasFlag(CreatureFlags.WallSide) ? 1f : -1f;
                }
            }

            var me = director != null ? director.LocalPlayer : null;
            NearestPlayerDistance = me != null && !me.IsAway ? Vector3.Distance(me.HeadPosition, transform.position) : float.MaxValue;
            if (!IsScaring) RefreshPresentation(false);
        }

        /// <summary>No RunEnded came after the jumpscare: the link is gone. Back to the hub, and let the light back in.</summary>
        void DropRunAfterLostLink()
        {
            m_AwaitRunEnd = false;
            RunEndLinkLosses++;
            var local = director.LocalPlayer;
            bool inside = local != null && director.safeZone != null && director.safeZone.Contains(local.HeadPosition);
            director.AbortRun();
            // Outside the tug AbortRun fades the player home (and back in); inside it only seals the hatch.
            if (m_AwaitVictimLocal && local != null && local.fader != null && (inside || director.safeZone == null)) local.fader.FadeIn(0.6f);
        }
    }
}

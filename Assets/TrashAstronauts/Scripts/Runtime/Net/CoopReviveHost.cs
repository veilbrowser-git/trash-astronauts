using System;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// Co-op revive on the host, the only authority. Owned by <see cref="CoopHostRules"/> (so only while hosting a
    /// crew); plain C#; ticked by the director every frame.
    ///
    /// A catch: when the jumpscare is over (EnemyDrone -> MissionDirector.EndCatch -> <see cref="TryDown"/>) the victim
    /// is downed where their head was if anyone else connected and not Away is still up; otherwise the contract fails
    /// for everyone as before. The creature goes back on its rounds from the far end of its route and cannot catch
    /// anyone within <see cref="ReviveRules.GraceRadius"/> of the body for <see cref="ReviveRules.GraceSeconds"/>.
    ///
    /// Reviving: a crew member's hand on the beacon (the host's own hand is read here; a client's comes as ReviveHold)
    /// counts while that crew member is up, here and within <see cref="ReviveRules.HostReach"/> of the body. The ring
    /// fills in <see cref="ReviveRules.ReviveSeconds"/> (a second rescuer adds <see cref="ReviveRules.ExtraRescuerBonus"/>),
    /// drains when nobody holds, goes out as ReviveProgress about 5 times a second while it moves, and at full the player
    /// is up again where they lay (PlayerRevived), safe from a catch for <see cref="ReviveRules.ProtectionSeconds"/>.
    /// A rescuer at work makes a small noise every <see cref="ReviveRules.RescuerNoiseInterval"/> s.
    ///
    /// With somebody downed and nobody left up at all (the last one up left the session), the contract fails.
    /// </summary>
    public sealed class CoopReviveHost : IDisposable
    {
        readonly MissionDirector m_Director;
        readonly ICoopBroadcast m_Out;
        readonly byte[] m_Hold = { NetProtocol.NoSlot, NetProtocol.NoSlot, NetProtocol.NoSlot, NetProtocol.NoSlot };
        readonly int[] m_Downs = new int[NetProtocol.MaxPlayers];
        readonly float[] m_ProtectedUntil = new float[NetProtocol.MaxPlayers];
        readonly float[] m_GraceUntil = new float[NetProtocol.MaxPlayers];
        readonly Vector3[] m_GraceAt = new Vector3[NetProtocol.MaxPlayers];
        readonly float[] m_NextNoise = new float[NetProtocol.MaxPlayers];
        readonly byte[] m_SentProgress = new byte[NetProtocol.MaxPlayers];
        readonly float[] m_SentProgressValue = new float[NetProtocol.MaxPlayers];
        readonly byte[] m_SentRescuer = { NetProtocol.NoSlot, NetProtocol.NoSlot, NetProtocol.NoSlot, NetProtocol.NoSlot };
        readonly double[] m_NextProgressAt = new double[NetProtocol.MaxPlayers];
        bool m_Disposed;

        /// <summary>Is this slot still in the session (NetSession.IsPresent)? A stand-in kept only for a jumpscare is not. Default: every registered player is.</summary>
        public Func<byte, bool> IsPresent = slot => true;
        /// <summary>Seconds for the progress throttle (unscaled time by default).</summary>
        public Func<double> Clock = () => Time.unscaledTimeAsDouble;

        // counters, for tests and the debug line
        public int Downs { get; private set; }
        public int Revives { get; private set; }
        public int ProgressMessages { get; private set; }
        public int RescuerNoises { get; private set; }
        public int CatchesBlocked { get; private set; }

        public CoopReviveHost(MissionDirector director, ICoopBroadcast broadcast)
        {
            m_Director = director ?? throw new ArgumentNullException(nameof(director));
            m_Out = broadcast ?? throw new ArgumentNullException(nameof(broadcast));
            director.ReviveHost = this;
            director.MissionStarted += OnMissionStarted;
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            m_Director.MissionStarted -= OnMissionStarted;
            if (m_Director.ReviveHost == this) m_Director.ReviveHost = null;
            m_Director.ClearCrewDowned();
        }

        void OnMissionStarted()
        {
            for (int i = 0; i < NetProtocol.MaxPlayers; i++)
            {
                m_Hold[i] = NetProtocol.NoSlot;
                m_Downs[i] = 0;
                m_ProtectedUntil[i] = 0f;
                m_GraceUntil[i] = 0f;
                m_SentProgress[i] = 0;
                m_SentRescuer[i] = NetProtocol.NoSlot;
            }
            m_Director.ClearCrewDowned();
        }

        static byte SlotOf(PlayerContext player) => player != null && player.Id.Value >= 0 && player.Id.Value < NetProtocol.MaxPlayers ? (byte)player.Id.Value : NetProtocol.NoSlot;

        bool Present(PlayerContext player)
        {
            byte slot = SlotOf(player);
            return slot != NetProtocol.NoSlot && (IsPresent == null || IsPresent(slot));
        }

        /// <summary>How many times the player in this slot has been downed this contract.</summary>
        public int DownsOf(int slot) => slot >= 0 && slot < m_Downs.Length ? m_Downs[slot] : 0;

        // ------------------------------------------------------------------ the catch

        /// <summary>
        /// The jumpscare of a catch of <paramref name="victim"/> is over. True: they are downed where they were caught and
        /// the contract goes on. False (the caller fails the contract): there is nobody else up (alone in the session, or
        /// everyone else downed or Away), or the victim is no longer in the session.
        /// </summary>
        public bool TryDown(PlayerId victim, EnemyDrone creature)
        {
            if (m_Disposed || m_Director.Phase != MissionPhase.Active) return false;
            var player = m_Director.FindPlayer(victim);
            byte slot = SlotOf(player);
            if (player == null || slot == NetProtocol.NoSlot || !Present(player) || player.IsDowned) return false;
            if (!AnyoneElseUp(player, true)) return false;

            Vector3 at = player.HeadPosition;
            m_Downs[slot]++;
            Downs++;
            m_Hold[slot] = NetProtocol.NoSlot;
            m_SentProgress[slot] = 0;
            m_SentRescuer[slot] = NetProtocol.NoSlot;
            m_GraceUntil[slot] = Time.unscaledTime + ReviveRules.GraceSeconds;
            m_GraceAt[slot] = at;
            player.SetDowned(true, at);
            m_Out.PlayerDowned(slot, CoopConvert.ToNet(at));
            if (creature != null) creature.LeaveAfterCatch(at);
            return true;
        }

        /// <summary>Someone other than <paramref name="except"/> who is in the session and not downed (and, if asked, not Away).</summary>
        bool AnyoneElseUp(PlayerContext except, bool notAway)
        {
            var players = m_Director.Players;
            for (int i = 0; i < players.Count; i++)
            {
                var other = players[i];
                if (other == null || other == except || other.IsDowned || (notAway && other.IsAway) || !Present(other)) continue;
                return true;
            }
            return false;
        }

        /// <summary>The creature may not catch this player now: freshly revived, or near a body it has only just left.</summary>
        public bool BlocksCatch(PlayerContext target)
        {
            if (target == null || m_Disposed) return false;
            float now = Time.unscaledTime;
            byte slot = SlotOf(target);
            bool blocked = slot != NetProtocol.NoSlot && now < m_ProtectedUntil[slot];
            if (!blocked)
            {
                Vector3 head = target.HeadPosition;
                for (int i = 0; i < NetProtocol.MaxPlayers && !blocked; i++)
                    blocked = now < m_GraceUntil[i] && (head - m_GraceAt[i]).sqrMagnitude <= ReviveRules.GraceRadius * ReviveRules.GraceRadius;
            }
            if (blocked) CatchesBlocked++;
            return blocked;
        }

        // ------------------------------------------------------------------ reviving

        /// <summary>A crew member's hand on (or off) a downed crew mate's beacon (IHostWorld.OnReviveHold).</summary>
        public void HostReviveHold(byte slot, byte targetSlot, bool on)
        {
            if (slot >= NetProtocol.MaxPlayers || targetSlot >= NetProtocol.MaxPlayers || slot == targetSlot) return;
            if (on) m_Hold[slot] = targetSlot;
            else if (m_Hold[slot] == targetSlot) m_Hold[slot] = NetProtocol.NoSlot;
        }

        /// <summary>Which downed crew mate this slot's rescuer is holding, as the host knows it (255 = none).</summary>
        public byte HoldOf(int slot) => slot >= 0 && slot < m_Hold.Length ? m_Hold[slot] : NetProtocol.NoSlot;

        /// <summary>A crew member left the session: their holds stop; anyone holding them lets go.</summary>
        public void HostPlayerLeft(byte slot)
        {
            if (slot >= NetProtocol.MaxPlayers) return;
            m_Hold[slot] = NetProtocol.NoSlot;
            for (int i = 0; i < m_Hold.Length; i++)
                if (m_Hold[i] == slot) m_Hold[i] = NetProtocol.NoSlot;
            m_GraceUntil[slot] = 0f;
        }

        bool HoldCounts(byte rescuerSlot, PlayerContext downed)
        {
            var rescuer = m_Director.FindPlayer(new PlayerId(rescuerSlot));
            if (rescuer == null || rescuer == downed || rescuer.IsDowned || rescuer.IsAway || !Present(rescuer)) return false;
            return (rescuer.HeadPosition - downed.DownedPosition).sqrMagnitude <= ReviveRules.HostReach * ReviveRules.HostReach;
        }

        /// <summary>One frame: the host's own hand, every ring, the rescuers' noise, revives, progress messages, and the no-one-left rule.</summary>
        public void Tick(float dt)
        {
            if (m_Disposed || m_Director.Phase != MissionPhase.Active) return;

            var local = m_Director.LocalPlayer;
            byte localSlot = SlotOf(local);
            if (localSlot != NetProtocol.NoSlot)
            {
                var held = CoopRevive.HeldTarget(m_Director, local);
                m_Hold[localSlot] = held != null ? SlotOf(held) : NetProtocol.NoSlot;
            }

            var players = m_Director.Players;
            bool paused = m_Director.HazardsPaused;
            PlayerContext anyDowned = null;
            for (int i = 0; i < players.Count; i++)
            {
                var downed = players[i];
                if (downed == null || !downed.IsDowned) continue;
                byte slot = SlotOf(downed);
                if (slot == NetProtocol.NoSlot) continue;
                if (anyDowned == null) anyDowned = downed;

                int rescuers = 0;
                byte primary = NetProtocol.NoSlot;
                for (byte r = 0; r < m_Hold.Length; r++)
                {
                    if (m_Hold[r] != slot || !HoldCounts(r, downed)) continue;
                    rescuers++;
                    if (primary == NetProtocol.NoSlot) primary = r;
                    MakeRescuerNoise(r);
                }
                if (paused) continue;

                downed.ReviveProgress = ReviveRules.StepProgress(downed.ReviveProgress, rescuers, dt);
                downed.ReviveRescuer = primary != NetProtocol.NoSlot ? new PlayerId(primary) : PlayerId.None;
                if (downed.ReviveProgress >= 1f)
                {
                    Revive(downed, slot, primary);
                    continue;
                }
                SendProgress(slot, downed.ReviveProgress, primary);
            }

            // Somebody is down and there is nobody at all left up to come for them: the contract is lost.
            if (anyDowned != null && !AnyoneElseUp(anyDowned, false))
                m_Director.FailMission(FailureReason.Caught, anyDowned.Id);
        }

        void MakeRescuerNoise(byte rescuerSlot)
        {
            float now = Time.unscaledTime;
            if (now < m_NextNoise[rescuerSlot]) return;
            m_NextNoise[rescuerSlot] = now + ReviveRules.RescuerNoiseInterval;
            var rescuer = m_Director.FindPlayer(new PlayerId(rescuerSlot));
            var noise = m_Director.noise;
            if (rescuer == null || noise == null) return;
            noise.Emit(new NoiseEvent(rescuer.HeadPosition + Vector3.down * 0.5f, ReviveRules.RescuerNoiseLoudness, NoiseKind.Impact, rescuer.Id));
            RescuerNoises++;
        }

        void SendProgress(byte slot, float progress, byte rescuer)
        {
            byte quantized = Quant.ToByte(progress);
            bool emptied = progress <= 0f && m_SentProgressValue[slot] > 0f;
            if (quantized == m_SentProgress[slot] && rescuer == m_SentRescuer[slot] && !emptied) return;
            double now = Clock != null ? Clock() : Time.unscaledTimeAsDouble;
            // At most 5 a second while it moves; the end of a drain (0) and a change of hands go out at once.
            if (now < m_NextProgressAt[slot] && quantized != 0 && rescuer == m_SentRescuer[slot]) return;
            m_NextProgressAt[slot] = now + ReviveRules.ProgressInterval;
            m_SentProgress[slot] = quantized;
            m_SentProgressValue[slot] = progress;
            m_SentRescuer[slot] = rescuer;
            ProgressMessages++;
            m_Out.ReviveProgress(slot, progress, rescuer);
        }

        void Revive(PlayerContext player, byte slot, byte by)
        {
            Vector3 at = player.DownedPosition;
            player.SetDowned(false, at);
            m_ProtectedUntil[slot] = Time.unscaledTime + ReviveRules.ProtectionSeconds;
            m_SentProgress[slot] = 0;
            m_SentRescuer[slot] = NetProtocol.NoSlot;
            for (int i = 0; i < m_Hold.Length; i++)
                if (m_Hold[i] == slot) m_Hold[i] = NetProtocol.NoSlot;
            Revives++;
            m_Out.PlayerRevived(slot, by);
            PlayRevived(player, at);
        }

        /// <summary>A short, warm "back online" for the revived player in their ears, and at the body for everyone else.</summary>
        public static void PlayRevived(PlayerContext player, Vector3 at)
        {
            var lib = Sfx.Library;
            if (lib == null || lib.powerUp == null) return;
            if (player != null && !player.IsRemote) Sfx.PlayUi(lib.powerUp, 0.45f, 1.25f);
            else Sfx.PlayAt(lib.powerUp, at, 0.5f, 1.25f, 12f);
        }

        // ------------------------------------------------------------------ state for the crew

        /// <summary>Adds the revive record to a result: downs per slot, and at an extraction who was still down (left behind).</summary>
        public void Stamp(MissionResult result, bool extraction)
        {
            if (result == null) return;
            bool any = false;
            for (int i = 0; i < m_Downs.Length; i++) any |= m_Downs[i] > 0;
            result.CrewDowns = any ? (int[])m_Downs.Clone() : null;
            int left = 0;
            if (extraction)
            {
                var players = m_Director.Players;
                for (int i = 0; i < players.Count; i++)
                {
                    byte slot = SlotOf(players[i]);
                    if (slot != NetProtocol.NoSlot && players[i].IsDowned && Present(players[i])) left |= 1 << slot;
                }
            }
            result.LeftBehindMask = left;
        }

        /// <summary>Who is down, where, and how often each slot has been downed (part of WorldState).</summary>
        public void FillWorldState(ref WorldStateMsg state)
        {
            for (int slot = 0; slot < NetProtocol.MaxPlayers; slot++)
            {
                var player = m_Director.FindPlayer(new PlayerId(slot));
                bool downed = player != null && player.IsDowned;
                state.SetCrew(slot, downed, downed ? CoopConvert.ToNet(player.DownedPosition) : NetVec3.Zero, m_Downs[slot]);
            }
        }
    }
}

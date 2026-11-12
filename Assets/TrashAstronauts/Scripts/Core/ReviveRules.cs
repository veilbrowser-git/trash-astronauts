using System;

namespace TrashAstronauts.Core
{
    /// <summary>
    /// Every number of the co-op revive in one place (pure, unit tested). Only a crew session uses any of it:
    /// single-player is unchanged (caught = contract failed).
    ///
    /// Caught while at least one other connected, non-Away crew member is still up: the victim is DOWNED where they
    /// were caught instead of the contract failing. Another crew member holds a hand on the glowing beacon on the
    /// downed suit's chest for <see cref="ReviveSeconds"/> to bring them back. Nobody left up = contract failed.
    /// </summary>
    public static class ReviveRules
    {
        /// <summary>Seconds of one rescuer's steady hold from an empty ring to a revive.</summary>
        public const float ReviveSeconds = 5f;
        /// <summary>Two or more rescuers on the same suit fill the ring this much faster (they do not stack beyond it).</summary>
        public const float ExtraRescuerBonus = 0.25f;
        /// <summary>With nobody holding, a part-filled ring drains from full to empty in this many seconds.</summary>
        public const float DrainSeconds = 2.5f;
        /// <summary>A gripping hand this close to the beacon (metres, centre to centre) counts as holding it (the rescuer's own headset decides).</summary>
        public const float HandReach = 0.3f;
        /// <summary>The host only believes a crew member's hold while their head is this close to the downed player (metres; covers a suit lying full length plus an arm).</summary>
        public const float HostReach = 2.5f;
        /// <summary>Grip pressed at least this far closes the hand on the beacon.</summary>
        public const float GripToHold = 0.55f;

        /// <summary>After it downs someone the spider cannot catch anyone within <see cref="GraceRadius"/> of the body for this long (s).</summary>
        public const float GraceSeconds = 8f;
        public const float GraceRadius = 4f;
        /// <summary>A revived player cannot be caught for this long (s).</summary>
        public const float ProtectionSeconds = 4f;

        /// <summary>
        /// Loudest a downed player's noise may be to the spider (their voice included). 0.15 is heard only within about
        /// 2.4 m: they can call the crew over proximity voice without luring the spider back to the body from across the
        /// wreck, but a spider already standing over them still hears a shout (it is not a free silence either).
        /// </summary>
        public const float DownedNoiseCap = 0.15f;
        /// <summary>A rescuer's hands working the beacon: a small noise (heard within about 4.8 m) every <see cref="RescuerNoiseInterval"/> s.</summary>
        public const float RescuerNoiseLoudness = 0.3f;
        public const float RescuerNoiseInterval = 1.5f;

        /// <summary>Each time a player is downed their own payout from that contract shrinks by this fraction...</summary>
        public const float DownPenalty = 0.10f;
        /// <summary>...up to this much in all from downs.</summary>
        public const float MaxDownPenalty = 0.40f;
        /// <summary>Still downed when the crew extracts: brought home by the company, and this much more is taken.</summary>
        public const float LeftBehindPenalty = 0.35f;

        /// <summary>Progress updates go out at most this often while the ring moves (reliable, so a revive never overtakes them).</summary>
        public const double ProgressInterval = 0.2;

        /// <summary>The fraction of a payout this player loses (0..0.75).</summary>
        public static float PenaltyFraction(int downs, bool leftBehind)
        {
            float p = Math.Min(MaxDownPenalty, Math.Max(0, downs) * DownPenalty);
            if (leftBehind) p += LeftBehindPenalty;
            return Math.Min(1f, p);
        }

        /// <summary>Percent the player loses, for the results board.</summary>
        public static int PenaltyPercent(int downs, bool leftBehind) => (int)Math.Round(PenaltyFraction(downs, leftBehind) * 100f);

        /// <summary>What this player banks from a crew payout.</summary>
        public static int PayoutFor(int payout, int downs, bool leftBehind)
        {
            if (payout <= 0) return 0;
            return (int)Math.Round(payout * (1f - PenaltyFraction(downs, leftBehind)));
        }

        /// <summary>One step of a downed player's revive ring: rescuers holding = 0 drains, 1 fills, 2+ fills with the bonus.</summary>
        public static float StepProgress(float progress, int rescuers, float dt)
        {
            if (!(dt > 0f)) return progress;
            if (rescuers <= 0) return Math.Max(0f, progress - dt / DrainSeconds);
            float rate = (rescuers >= 2 ? 1f + ExtraRescuerBonus : 1f) / ReviveSeconds;
            return Math.Min(1f, progress + dt * rate);
        }
    }
}

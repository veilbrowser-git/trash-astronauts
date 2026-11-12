using System;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// Whether a player is downed (immobile, waiting to be revived), asked by features that must leave a downed player
    /// alone (crew shoves). <see cref="Check"/> reads <see cref="PlayerContext.IsDowned"/> directly (no reflection, so
    /// IL2CPP stripping cannot hide it); tests may replace it.
    /// </summary>
    public static class CrewDowned
    {
        /// <summary>The test for "downed". Reads the revive feature's <see cref="PlayerContext.IsDowned"/>.</summary>
        public static Func<PlayerContext, bool> Check = p => p.IsDowned;

        public static bool Is(PlayerContext player)
        {
            if (player == null) return false;
            return Check != null ? Check(player) : player.IsDowned;
        }
    }
}

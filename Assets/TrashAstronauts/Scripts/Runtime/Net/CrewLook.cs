using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// How a crew member looks: their slot and the suit colour they picked in the SUIT LOCKER (0 = "my slot's colour",
    /// 1..12 = <see cref="PlayerAvatar.SuitColors"/>[colour - 1]; the same value the save and the network carry, see
    /// <see cref="SuitLook"/>). Static helpers for the session code and the panel; nothing here keeps state.
    ///
    /// - Default (0): the stripes in the slot's crew colour and the fabric white, exactly as before suit colours existed
    ///   (<see cref="PlayerAvatar.SetTint"/>); the glove bands in the same crew colour (orange for slot 0, as modelled).
    /// - A chosen colour: the stripes at full colour, the fabric and helmet shell with a light wash of it
    ///   (<see cref="PlayerAvatar.SetSuitColor"/>) and the glove bands in it (<see cref="GloveVisual.SetBandColor"/>).
    /// </summary>
    public static class CrewLook
    {
        /// <summary>The palette entry a slot and colour resolve to: the chosen one, or the slot's own crew colour for 0.</summary>
        public static int PaletteIndexFor(int slot, int colour)
        {
            colour = SuitLook.Clamp(colour);
            return colour != SuitLook.Default ? colour - 1 : Mathf.Clamp(slot, 0, PlayerAvatar.CrewColors.Length - 1);
        }

        /// <summary>The colour the stripes show for this slot and colour.</summary>
        public static Color AccentFor(int slot, int colour) => PlayerAvatar.SuitColors[PaletteIndexFor(slot, colour)];

        /// <summary>A short name for the panel and the crew list, e.g. "CYAN" or "ORANGE (CREW)".</summary>
        public static string NameOf(int slot, int colour)
        {
            string name = PlayerAvatar.SuitColorNames[PaletteIndexFor(slot, colour)];
            return SuitLook.Clamp(colour) == SuitLook.Default ? name + " (CREW)" : name;
        }

        /// <summary>Dresses a suit and its gloves (any of them may be null) for <paramref name="slot"/> and <paramref name="colour"/>.</summary>
        public static void ApplyLook(PlayerAvatar suit, GloveVisual leftGlove, GloveVisual rightGlove, int slot, int colour)
        {
            colour = SuitLook.Clamp(colour);
            int index = PaletteIndexFor(slot, colour);
            if (suit != null)
            {
                if (colour == SuitLook.Default) suit.SetTint(PlayerAvatar.CrewColors[index]);
                else suit.SetSuitColor(index);
            }
            if (leftGlove != null) leftGlove.SetBandColor(index);
            if (rightGlove != null) rightGlove.SetBandColor(index);
        }

        /// <summary>Dresses a player (the local rig or a remote crew member's context): their suit and both gloves.</summary>
        public static void ApplyLook(PlayerContext player, int slot, int colour)
        {
            if (player == null) return;
            ApplyLook(player.avatar, GloveOf(player.leftHand), GloveOf(player.rightHand), slot, colour);
        }

        /// <summary>Dresses another crew member's stand-in: its suit and both gloves.</summary>
        public static void ApplyLook(RemoteAvatar crew, int slot, int colour)
        {
            if (crew == null) return;
            ApplyLook(crew.suit, crew.leftGlove, crew.rightGlove, slot, colour);
        }

        /// <summary>True when the suit and gloves already show this look (so a caller polling every frame does nothing).</summary>
        public static bool HasLook(PlayerAvatar suit, GloveVisual leftGlove, GloveVisual rightGlove, int slot, int colour)
        {
            colour = SuitLook.Clamp(colour);
            int index = PaletteIndexFor(slot, colour);
            if (suit != null)
            {
                bool suitOk = colour == SuitLook.Default
                    ? suit.SuitColorIndex == -1 && suit.Tint == PlayerAvatar.CrewColors[index]
                    : suit.SuitColorIndex == index;
                if (!suitOk) return false;
            }
            return GloveOk(leftGlove, index) && GloveOk(rightGlove, index);
        }

        static bool GloveOk(GloveVisual glove, int index)
        {
            if (glove == null || glove.bandAtlases == null || glove.bandAtlases.Length == 0) return true; // nothing to paint
            int expected = index < glove.bandAtlases.Length && glove.bandAtlases[index] != null ? index : 0;
            return glove.BandColor == expected;
        }

        /// <summary>The glove on a hand (the modelled or stand-in glove under it), or null.</summary>
        public static GloveVisual GloveOf(HandRig hand) => hand != null ? hand.GetComponentInChildren<GloveVisual>(true) : null;

        /// <summary>The colour this headset's profile saved (0 = slot colour, 1..12 = palette).</summary>
        public static byte SavedColour(MissionDirector director)
        {
            return director != null && director.Profile != null ? SuitLook.Clamp(director.Profile.Data.suitColor) : SuitLook.Default;
        }

        /// <summary>The slot the local player has: its crew slot in a session, 0 alone.</summary>
        public static int LocalSlot(MissionDirector director)
        {
            return director != null && director.Net is NetSession net ? net.LocalSlot : 0;
        }
    }
}

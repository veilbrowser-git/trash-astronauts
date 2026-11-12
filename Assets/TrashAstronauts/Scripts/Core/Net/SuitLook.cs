namespace TrashAstronauts.Core.Net
{
    /// <summary>
    /// A player's chosen suit colour as it travels with the settings: 4 bits, 0 = "my slot's colour" (the default),
    /// 1..12 = an entry of the suit palette (PlayerAvatar.SuitColors[colour - 1]). It rides in the upper half of the
    /// handedness byte of Hello, SettingsChanged and RosterChanged (bit 0 stays the handedness) and in bits 4..7 of
    /// each slot's byte of the Welcome roster. Anything out of range reads as the default.
    /// </summary>
    public static class SuitLook
    {
        public const byte Default = 0;
        /// <summary>How many colours the palette has; also the largest valid colour value.</summary>
        public const byte PaletteSize = 12;

        /// <summary>0..12 unchanged; anything else becomes <see cref="Default"/>.</summary>
        public static byte Clamp(int colour) => colour >= 1 && colour <= PaletteSize ? (byte)colour : Default;

        /// <summary>The settings byte: handedness in bit 0, colour in bits 4..7.</summary>
        public static byte Pack(byte handedness, byte colour) => (byte)((handedness & 1) | (Clamp(colour) << 4));

        public static byte HandednessOf(byte packed) => (byte)(packed & 1);

        public static byte ColourOf(byte packed) => Clamp(packed >> 4);
    }

    /// <summary>
    /// Optional extra callback for an <see cref="IHostWorld"/> or <see cref="IClientWorld"/>: a crew member's suit
    /// colour is known or changed. The sessions call it when the world implements it, right after the matching
    /// OnPlayerJoined / OnPlayerSettings / OnWelcome / OnRosterChanged call (whose signatures are unchanged), so the
    /// stand-in for that slot already exists. Never called for the local player's own slot.
    /// </summary>
    public interface ICrewLookListener
    {
        void OnCrewLook(byte slot, byte suitColour);
    }
}

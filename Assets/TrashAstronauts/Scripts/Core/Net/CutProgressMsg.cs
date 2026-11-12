namespace TrashAstronauts.Core.Net
{
    /// <summary>
    /// How far a crew member has burnt one weld (unreliable stream, about 5 Hz while the flame is on it), so the rest
    /// of the crew see the weld glow and swell as it is cut instead of only popping when it is done. Display only:
    /// completing a weld still goes through the reliable PointCutReport / PointCut path.
    ///
    /// Client -> host carries the client's own slot (the host overwrites it with the sender's real slot); host ->
    /// the other clients carries the cutter's slot (0 for the host's own cutting). Four bytes after the type byte.
    ///
    /// The id lives here, not in <see cref="NetMsg"/>, so this stream could be added without touching the shared
    /// tables; NetCutProgressTests fails if <see cref="NetMsg.All"/> ever gains the same number.
    /// </summary>
    public struct CutProgressMsg
    {
        /// <summary>Unreliable stream id (below <see cref="NetMsg.FirstReliable"/>, clear of the ids in <see cref="NetMsg.All"/>).</summary>
        public const byte Id = 40;
        public const int Size = 4;
        /// <summary>Seconds between two progress messages from one cutter (5 Hz).</summary>
        public const double Interval = 0.2;

        public byte slot;
        /// <summary>Net id of the panel (index in NetIdTable.Panels).</summary>
        public byte panel;
        /// <summary>Index of the weld in the panel's cut points.</summary>
        public byte index;
        /// <summary>0..255 = 0..1 burnt.</summary>
        public byte progress;

        public static byte ToByte(float progress01) => Quant.ToByte(progress01);
        public static float FromByte(byte progress) => Quant.FromByte(progress);

        public void Write(ref NetWriter w)
        {
            w.U8(slot);
            w.U8(panel);
            w.U8(index);
            w.U8(progress);
        }

        public static bool Read(ref NetReader r, out CutProgressMsg m)
        {
            m = new CutProgressMsg { slot = r.U8(), panel = r.U8(), index = r.U8(), progress = r.U8() };
            return !r.Failed && NetProtocol.IsValidSlot(m.slot);
        }
    }
}

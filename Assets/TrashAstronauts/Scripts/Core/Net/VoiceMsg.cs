namespace TrashAstronauts.Core.Net
{
    /// <summary>Raised by a session for each received voice frame. The bytes are valid only during the call.</summary>
    public delegate void NetVoiceHandler(byte slot, ushort sequence, byte[] buffer, int offset, int length);

    /// <summary>
    /// Unreliable stream message carrying proximity voice chat: one or two consecutive encoded 20 ms frames of one
    /// player's microphone (see Core/Voice/VoiceFormat.cs; 123 bytes each with the current codec). Layout: slot u8,
    /// sequence u16 (of the first frame; the second is sequence + 1), count u8 (1..2), then per frame length u8
    /// (1..160) and the bytes. At most 326 bytes, so a voice datagram is far under the 1200-byte cap.
    ///
    /// It travels in its own datagram, sent the moment two frames are ready (not on the 20 Hz tick), from a
    /// client to the host, which relays it at once to every OTHER connected player that is not Away and never
    /// back to the sender; the host's own voice goes to every client the same way. The host stamps the sender's
    /// real slot over the one written by a client, allows at most 60 frames a second per client, and drops a
    /// malformed message (and so the whole datagram) without throwing. Only sent while a player is talking, and
    /// only when that player has both MIC and VOICE CHAT on.
    /// </summary>
    public struct VoiceMsg
    {
        public const int MaxFrames = 2;
        public const int MaxFrameBytes = 160;
        public const int MaxSize = 4 + MaxFrames * (1 + MaxFrameBytes);
        /// <summary>Most frames a second the host accepts from one client.</summary>
        public const int MaxFramesPerSecond = 60;
        /// <summary>Frames a client may send in a burst (after a Wi-Fi stall) above the steady rate.</summary>
        public const int BurstFrames = 12;

        public byte slot;
        public ushort sequence;
        public int count;
        public int length0;
        public int length1;
        /// <summary>Frame i starts at i * <see cref="MaxFrameBytes"/>. Owned by the caller and reused.</summary>
        public byte[] data;

        public int Length(int frame) => frame == 0 ? length0 : frame == 1 ? length1 : 0;
        public int Offset(int frame) => frame * MaxFrameBytes;

        /// <summary>Fills this message from <paramref name="frameCount"/> frames of <paramref name="frameBytes"/> each, laid end to end.</summary>
        public bool Set(byte slot, ushort sequence, byte[] frames, int offset, int frameBytes, int frameCount)
        {
            if (frames == null || frameCount < 1 || frameCount > MaxFrames || frameBytes < 1 || frameBytes > MaxFrameBytes) return false;
            if (offset < 0 || offset + frameBytes * frameCount > frames.Length) return false;
            if (data == null || data.Length < MaxFrames * MaxFrameBytes) data = new byte[MaxFrames * MaxFrameBytes];
            this.slot = slot;
            this.sequence = sequence;
            count = frameCount;
            length0 = frameBytes;
            length1 = frameCount > 1 ? frameBytes : 0;
            for (int i = 0; i < frameCount; i++) System.Buffer.BlockCopy(frames, offset + i * frameBytes, data, i * MaxFrameBytes, frameBytes);
            return true;
        }

        public void Write(ref NetWriter w)
        {
            int n = data == null ? 0 : count < 0 ? 0 : count > MaxFrames ? MaxFrames : count;
            w.U8(slot);
            w.U16(sequence);
            w.U8((byte)n);
            for (int i = 0; i < n; i++)
            {
                int length = Length(i);
                if (length < 1 || length > MaxFrameBytes) length = 0;
                w.U8((byte)length);
                w.Bytes(data, Offset(i), length);
            }
        }

        /// <summary>Reads into <paramref name="m"/>, reusing its array. False for anything out of range.</summary>
        public static bool Read(ref NetReader r, ref VoiceMsg m)
        {
            m.count = 0;
            m.slot = r.U8();
            m.sequence = r.U16();
            int n = r.U8();
            if (r.Failed || m.slot >= NetProtocol.MaxPlayers || n < 1 || n > MaxFrames) { r.Fail(); return false; }
            if (m.data == null || m.data.Length < MaxFrames * MaxFrameBytes) m.data = new byte[MaxFrames * MaxFrameBytes];
            m.length0 = m.length1 = 0;
            for (int i = 0; i < n; i++)
            {
                int length = r.U8();
                if (r.Failed || length < 1 || length > MaxFrameBytes) { r.Fail(); return false; }
                r.Bytes(m.data, i * MaxFrameBytes, length);
                if (i == 0) m.length0 = length;
                else m.length1 = length;
            }
            if (r.Failed) return false;
            m.count = n;
            return true;
        }
    }
}

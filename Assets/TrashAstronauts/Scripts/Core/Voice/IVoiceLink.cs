namespace TrashAstronauts.Core.Voice
{
    /// <summary>One received voice frame: who said it, its sequence number and its encoded bytes (valid only during the call).</summary>
    public delegate void VoiceFrameHandler(byte slot, ushort sequence, byte[] buffer, int offset, int length);

    /// <summary>
    /// What voice chat needs from a network session: send this headset's frames to the rest of the crew, and
    /// hear everyone else's. Implemented over NetSessionHost / NetSessionClient (Runtime/Net/Voice/VoiceLinks.cs)
    /// and by fakes in tests.
    /// </summary>
    public interface IVoiceLink
    {
        /// <summary>True while frames can be sent and received (connected and welcomed).</summary>
        bool IsOpen { get; }
        /// <summary>This headset's crew slot.</summary>
        byte LocalSlot { get; }
        /// <summary>
        /// Sends <paramref name="frameCount"/> (1 or 2) consecutive frames of <paramref name="frameBytes"/> each,
        /// the first with <paramref name="firstSequence"/>. False if nothing could be sent.
        /// </summary>
        bool Send(ushort firstSequence, byte[] frames, int offset, int frameBytes, int frameCount);
        /// <summary>Raised for every frame of every other crew member (on the main thread).</summary>
        event VoiceFrameHandler Received;
    }
}

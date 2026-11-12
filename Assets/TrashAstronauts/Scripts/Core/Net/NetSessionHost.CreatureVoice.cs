namespace TrashAstronauts.Core.Net
{
    /// <summary>
    /// The co-op Mimic's voice on the host: frames of the creature speaking (already lowered and slowed here) go out
    /// in the Voice stream message with the pseudo-sender <see cref="VoiceMsg.CreatureSlot"/>, one datagram per
    /// message, to every connected client that is not Away, exactly like the host player's own voice. Only the host
    /// can send it: a client that writes the creature slot is refused in HandleVoice. Not rate-limited (the host
    /// paces it in real time itself, 50 frames a second at most, while the creature speaks). Kept out of IHostWorld
    /// so no world has to change.
    /// </summary>
    public sealed partial class NetSessionHost
    {
        VoiceMsg m_CreatureVoiceWrite;

        /// <summary>Creature voice messages sent (one per client reached).</summary>
        public long CreatureVoiceSent { get; private set; }

        /// <summary>
        /// Sends <paramref name="frameCount"/> (1 or 2) consecutive encoded frames of the creature's voice to every
        /// connected client that is not Away, at once. Returns the number of clients it went to.
        /// </summary>
        public int SendCreatureVoice(ushort sequence, byte[] frames, int offset, int frameBytes, int frameCount)
        {
            if (m_Closed || m_Disposed) return 0;
            if (!m_CreatureVoiceWrite.Set(VoiceMsg.CreatureSlot, sequence, frames, offset, frameBytes, frameCount)) return 0;
            int sent = 0;
            for (int i = 1; i < m_Peers.Length; i++)
                if (SendVoiceTo(m_Peers[i], ref m_CreatureVoiceWrite)) sent++;
            CreatureVoiceSent += sent;
            return sent;
        }
    }
}

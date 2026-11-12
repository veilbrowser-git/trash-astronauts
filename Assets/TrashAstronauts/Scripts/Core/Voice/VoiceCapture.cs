using System;

namespace TrashAstronauts.Core.Voice
{
    /// <summary>Takes one packet of 1 or 2 consecutive encoded frames. Returns false if it could not be sent.</summary>
    public delegate bool VoicePacketSink(ushort firstSequence, byte[] frames, int offset, int frameBytes, int frameCount);

    /// <summary>
    /// The sending half of voice chat, pure logic: microphone blocks (any size, any rate) in, packets of two
    /// encoded 20 ms frames out, only while the player is talking. Steps per block: measure (for the echo verdict
    /// the creature uses), resample to 16 kHz, cut into 20 ms frames. Per frame: number it, encode it (always, so
    /// the encoder's state runs on and the two frames before speech are ready as pre-roll), run the voice gate and
    /// the echo suppressor, and queue it when the gate is open and the frame is not echo. A packet goes out as soon
    /// as it holds two frames, or with one when talking stops. No allocation per block once the input rate is known.
    /// </summary>
    public sealed class VoiceCapture
    {
        public const int PreRollFrames = 2;
        const int k_History = PreRollFrames + 1;
        const int k_Chunk = 1024;

        public readonly VoiceActivityGate Gate = new VoiceActivityGate();
        public readonly VoiceEchoSuppressor Echo = new VoiceEchoSuppressor();
        readonly VoiceEncoder m_Encoder = new VoiceEncoder();

        VoiceResampler m_In;
        float[] m_Resampled = new float[0];
        float[] m_Fifo = new float[VoiceFormat.FrameSamples * 4];
        int m_FifoCount;
        readonly float[] m_Frame = new float[VoiceFormat.FrameSamples];

        readonly byte[] m_History = new byte[k_History * VoiceFormat.FrameBytes];
        readonly ushort[] m_HistorySeq = new ushort[k_History];
        readonly bool[] m_HistoryValid = new bool[k_History];
        readonly bool[] m_HistorySent = new bool[k_History];
        readonly byte[] m_Packet = new byte[VoiceFormat.FramesPerPacket * VoiceFormat.FrameBytes];
        int m_PacketCount;
        ushort m_PacketSeq;
        ushort m_Sequence;

        /// <summary>The sequence number the next frame will get.</summary>
        public ushort NextSequence => m_Sequence;
        public int InputRate => m_In != null ? m_In.InRate : 0;
        /// <summary>True while the last frame was queued for sending.</summary>
        public bool IsTransmitting { get; private set; }
        /// <summary>Level of the last frame (dBFS).</summary>
        public float LevelDb { get; private set; } = -100f;
        public long FramesCaptured { get; private set; }
        public long FramesSent { get; private set; }
        public long PacketsSent { get; private set; }
        public long PacketsFailed { get; private set; }
        /// <summary>Frames that would have been sent but were judged to be the speakers (echo).</summary>
        public long FramesEchoGated { get; private set; }
        /// <summary>Microphone blocks judged to be echo (not reported to the creature).</summary>
        public long BlocksEcho { get; private set; }

        public VoiceCapture(int startSequence = 0)
        {
            m_Sequence = (ushort)startSequence;
        }

        void EnsureRate(int rate)
        {
            if (m_In != null && m_In.InRate == rate) return;
            m_In = new VoiceResampler(rate, VoiceFormat.SampleRate, k_Chunk);
            int max = m_In.MaxOutput(k_Chunk);
            if (m_Resampled.Length < max) m_Resampled = new float[max];
            if (m_Fifo.Length < VoiceFormat.FrameSamples + max) m_Fifo = new float[VoiceFormat.FrameSamples + max];
            m_FifoCount = 0;
        }

        /// <summary>
        /// The playback level (dBFS, all remote voices together) going to the speakers now, for the echo suppressor.
        /// </summary>
        public void AddPlayback(float levelDb, double now) => Echo.AddPlayback(levelDb, now);

        /// <summary>
        /// Whether a microphone block of this level is explained by remote voice playing out of the speakers (it
        /// is then not the player's own voice, for the creature).
        /// </summary>
        public bool IsEcho(float blockDb, double now) => Echo.IsEcho(blockDb, now);

        /// <summary>
        /// Feeds a microphone block at <paramref name="rate"/> Hz. Packets go to <paramref name="sink"/> when
        /// <paramref name="allowSend"/> (otherwise frames are still processed, and nothing is sent). Returns true
        /// when the block is echo of remote voice (the creature should not hear it as the player).
        /// </summary>
        public bool Feed(float[] block, int offset, int count, int rate, double now, bool allowSend, VoicePacketSink sink)
        {
            if (block == null || count <= 0 || rate < 4000 || rate > 192000) return false;
            if (offset < 0 || offset + count > block.Length) return false;
            EnsureRate(rate);
            bool echo = Echo.IsEcho(VoiceFormat.ToDb(VoiceFormat.Rms(block, offset, count)), now);
            if (echo) BlocksEcho++;

            while (count > 0)
            {
                int n = Math.Min(count, k_Chunk);
                int produced = m_In.Process(block, offset, n, m_Resampled, 0, m_Resampled.Length);
                offset += n;
                count -= n;
                int room = m_Fifo.Length - m_FifoCount;
                if (produced > room) produced = room;
                Array.Copy(m_Resampled, 0, m_Fifo, m_FifoCount, produced);
                m_FifoCount += produced;
                while (m_FifoCount >= VoiceFormat.FrameSamples)
                {
                    Array.Copy(m_Fifo, 0, m_Frame, 0, VoiceFormat.FrameSamples);
                    m_FifoCount -= VoiceFormat.FrameSamples;
                    if (m_FifoCount > 0) Array.Copy(m_Fifo, VoiceFormat.FrameSamples, m_Fifo, 0, m_FifoCount);
                    ProcessFrame(now, allowSend, sink);
                }
            }
            if (!allowSend) Flush(sink);
            return echo;
        }

        void ProcessFrame(double now, bool allowSend, VoicePacketSink sink)
        {
            float db = VoiceFormat.ToDb(VoiceFormat.Rms(m_Frame, 0, VoiceFormat.FrameSamples));
            LevelDb = db;
            ushort seq = m_Sequence++;
            int h = seq % k_History;
            m_Encoder.Encode(m_Frame, 0, m_History, h * VoiceFormat.FrameBytes);
            m_HistorySeq[h] = seq;
            m_HistoryValid[h] = true;
            m_HistorySent[h] = false;
            FramesCaptured++;

            bool open = Gate.Process(db);
            bool echo = Echo.ProcessFrame(db, Gate.Voiced, now);
            if (echo && (open || Gate.Voiced))
            {
                // The speakers, not the player: close at once (no hang-over full of echo).
                Gate.Close();
                open = false;
                FramesEchoGated++;
            }

            if (open && allowSend && sink != null)
            {
                if (!IsTransmitting)
                {
                    // Talking starts: send the frames just before it too, so the first syllable is whole.
                    for (int k = PreRollFrames; k >= 1; k--)
                    {
                        ushort s = (ushort)(seq - k);
                        int hh = s % k_History;
                        if (m_HistoryValid[hh] && m_HistorySeq[hh] == s && !m_HistorySent[hh]) Queue(hh, sink);
                    }
                }
                Queue(h, sink);
                IsTransmitting = true;
            }
            else
            {
                Flush(sink);
                IsTransmitting = false;
            }
        }

        void Queue(int h, VoicePacketSink sink)
        {
            ushort seq = m_HistorySeq[h];
            if (m_PacketCount > 0 && (ushort)(m_PacketSeq + m_PacketCount) != seq) Flush(sink);
            if (m_PacketCount == 0) m_PacketSeq = seq;
            Buffer.BlockCopy(m_History, h * VoiceFormat.FrameBytes, m_Packet, m_PacketCount * VoiceFormat.FrameBytes, VoiceFormat.FrameBytes);
            m_PacketCount++;
            m_HistorySent[h] = true;
            if (m_PacketCount >= VoiceFormat.FramesPerPacket) Flush(sink);
        }

        void Flush(VoicePacketSink sink)
        {
            if (m_PacketCount == 0) return;
            int count = m_PacketCount;
            m_PacketCount = 0;
            if (sink != null && sink(m_PacketSeq, m_Packet, 0, VoiceFormat.FrameBytes, count))
            {
                FramesSent += count;
                PacketsSent++;
            }
            else PacketsFailed++;
        }

        /// <summary>Stops transmitting at once and drops anything queued (MIC or VOICE CHAT switched off).</summary>
        public void Stop()
        {
            m_PacketCount = 0;
            IsTransmitting = false;
            Gate.Close();
        }
    }

    /// <summary>
    /// The receiving half for one crew member: jitter buffer plus decoder, and the level and "is talking" state
    /// for a talk indicator. Pure logic; the Unity side pulls one 20 ms frame per 20 ms of playback.
    /// </summary>
    public sealed class VoiceStream
    {
        public readonly VoiceJitterBuffer Jitter = new VoiceJitterBuffer();
        readonly VoiceDecoder m_Decoder = new VoiceDecoder();

        /// <summary>A frame quieter than this (dBFS) does not count as talking for the indicator.</summary>
        public float speakingDb = -50f;
        /// <summary>How long the indicator stays on after the last loud frame (s).</summary>
        public double speakingHold = 0.25;

        /// <summary>Level of the last frame pulled (dBFS; -100 for silence).</summary>
        public float LevelDb { get; private set; } = -100f;
        /// <summary>Smoothed level, 0 (silent) .. 1 (a shout), for a talk light.</summary>
        public float Level01 { get; private set; }
        public double LastSpokeAt { get; private set; } = double.NegativeInfinity;
        public long FramesPlayed => Jitter.Played;

        public bool Push(ushort sequence, byte[] buffer, int offset, int length, double now) => Jitter.Push(sequence, buffer, offset, length, now);

        public VoicePull Pull(float[] output, int offset, double now)
        {
            VoicePull result = Jitter.Pull(m_Decoder, output, offset);
            LevelDb = result == VoicePull.Silent ? -100f : VoiceFormat.ToDb(VoiceFormat.Rms(output, offset, VoiceFormat.FrameSamples));
            float target = Math.Max(0f, Math.Min(1f, (LevelDb + 50f) / 40f));
            Level01 += (target - Level01) * (target > Level01 ? 0.6f : 0.15f);
            if (result == VoicePull.Played && LevelDb > speakingDb) LastSpokeAt = now;
            return result;
        }

        public bool IsSpeaking(double now) => now - LastSpokeAt < speakingHold;

        public void Clear()
        {
            Jitter.Clear();
            m_Decoder.Reset();
            LevelDb = -100f;
            Level01 = 0f;
            LastSpokeAt = double.NegativeInfinity;
        }
    }
}

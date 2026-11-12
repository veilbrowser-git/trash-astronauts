using System;

namespace TrashAstronauts.Core.Voice
{
    /// <summary>
    /// What the Mimic does to a phrase before it says it: a slow wobble (so it is recognisably the speaker but not
    /// quite right), then lower and slower. Single-player plays the wobbled phrase through an AudioSource with a
    /// pitch below 1; in co-op the host renders exactly that here, at the voice rate, and streams it to the crew.
    /// Pure C#.
    /// </summary>
    public static class MimicVoiceRender
    {
        /// <summary>The wobble the Mimic puts on a phrase (a new array; samples clamped to -1..1).</summary>
        public static float[] Warble(float[] phrase, int sampleRate, float warble)
        {
            if (phrase == null) return null;
            var warped = new float[phrase.Length];
            float rate = Math.Max(1, sampleRate);
            for (int i = 0; i < phrase.Length; i++)
            {
                float t = i / rate;
                float wobble = 1f - warble * (0.5f + 0.5f * (float)Math.Sin(2f * Math.PI * 37f * t)) * (0.6f + 0.4f * (float)Math.Sin(2f * Math.PI * 3.1f * t));
                float v = phrase[i] * wobble;
                warped[i] = v < -1f ? -1f : v > 1f ? 1f : v;
            }
            return warped;
        }

        /// <summary>
        /// <paramref name="samples"/> at <paramref name="sampleRate"/> played at <paramref name="pitch"/> (below 1 =
        /// lower and slower, as an AudioSource's pitch), as samples at the voice rate (16 kHz). Linear interpolation;
        /// the voice codec low-passes everything above 5 kHz anyway.
        /// </summary>
        public static float[] AtPitch(float[] samples, int sampleRate, float pitch)
        {
            if (samples == null || samples.Length == 0) return new float[0];
            pitch = Math.Max(0.1f, pitch);
            double step = pitch * (double)Math.Max(1000, sampleRate) / VoiceFormat.SampleRate; // source samples per output sample
            int count = (int)Math.Floor((samples.Length - 1) / step) + 1;
            var output = new float[count];
            for (int i = 0; i < count; i++)
            {
                double at = i * step;
                int a = (int)at;
                int b = Math.Min(samples.Length - 1, a + 1);
                float k = (float)(at - a);
                output[i] = samples[a] + (samples[b] - samples[a]) * k;
            }
            return output;
        }
    }

    /// <summary>
    /// Streams one rendered Mimic phrase (16 kHz) as voice frames, in real time: each frame is encoded when it is due
    /// (two frames' lead, so the first pair goes at once) and handed out two at a time, the way a talking player's are.
    /// <see cref="Stop"/> ends it mid-phrase (blackout, dart): nothing more is sent. Pure C#; the host calls
    /// <see cref="Pump"/> every frame with the session clock.
    /// </summary>
    public sealed class CreatureVoiceSender
    {
        /// <summary>Frames encoded ahead of the clock.</summary>
        public const int LeadFrames = 2;
        /// <summary>Sequence numbers skipped between phrases, so a jitter buffer sees a new talk spurt.</summary>
        public const int PhraseGap = 50;

        readonly VoiceEncoder m_Encoder = new VoiceEncoder();
        readonly float[] m_Frame = new float[VoiceFormat.FrameSamples];
        readonly byte[] m_Packet = new byte[VoiceFormat.FramesPerPacket * VoiceFormat.FrameBytes];
        float[] m_Samples;
        int m_Frames, m_Next, m_Pending;
        ushort m_PacketSeq;
        ushort m_Seq;
        double m_Start;

        public CreatureVoiceSender(int startSequence = 0) { m_Seq = (ushort)startSequence; }

        /// <summary>True from <see cref="Begin"/> until the last frame went out or <see cref="Stop"/>.</summary>
        public bool Active => m_Samples != null;
        /// <summary>Frames in the phrase being sent.</summary>
        public int FrameCount => m_Frames;
        /// <summary>Frames handed out so far for the phrase being sent.</summary>
        public int FramesSent { get; private set; }
        /// <summary>Frames handed out since creation.</summary>
        public long TotalFramesSent { get; private set; }

        /// <summary>Starts streaming <paramref name="voiceRateSamples"/> (16 kHz) from <paramref name="now"/>.</summary>
        public void Begin(float[] voiceRateSamples, double now)
        {
            Stop();
            if (voiceRateSamples == null || voiceRateSamples.Length == 0) return;
            m_Samples = voiceRateSamples;
            m_Frames = (voiceRateSamples.Length + VoiceFormat.FrameSamples - 1) / VoiceFormat.FrameSamples;
            m_Next = 0;
            m_Pending = 0;
            FramesSent = 0;
            m_Start = now;
            m_Seq = (ushort)(m_Seq + PhraseGap);
            m_Encoder.Reset();
        }

        /// <summary>Ends the phrase now; anything not yet handed out is dropped.</summary>
        public void Stop()
        {
            m_Samples = null;
            m_Pending = 0;
        }

        /// <summary>Encodes and hands out every frame due by <paramref name="now"/>. Returns frames handed out.</summary>
        public int Pump(double now, VoicePacketSink sink)
        {
            if (m_Samples == null) return 0;
            int due = (int)Math.Floor((now - m_Start) / VoiceFormat.FrameSeconds + 1e-6) + LeadFrames;
            if (due > m_Frames) due = m_Frames;
            int handed = 0;
            while (m_Next < due)
            {
                int at = m_Next * VoiceFormat.FrameSamples;
                int n = Math.Min(VoiceFormat.FrameSamples, m_Samples.Length - at);
                Array.Copy(m_Samples, at, m_Frame, 0, n);
                if (n < VoiceFormat.FrameSamples) Array.Clear(m_Frame, n, VoiceFormat.FrameSamples - n);
                if (m_Pending == 0) m_PacketSeq = m_Seq;
                m_Encoder.Encode(m_Frame, 0, m_Packet, m_Pending * VoiceFormat.FrameBytes);
                m_Pending++;
                m_Seq++;
                m_Next++;
                if (m_Pending >= VoiceFormat.FramesPerPacket || m_Next >= m_Frames) handed += Flush(sink);
            }
            if (m_Next >= m_Frames) Stop();
            return handed;
        }

        int Flush(VoicePacketSink sink)
        {
            int count = m_Pending;
            m_Pending = 0;
            if (count == 0) return 0;
            sink?.Invoke(m_PacketSeq, m_Packet, 0, VoiceFormat.FrameBytes, count);
            FramesSent += count;
            TotalFramesSent += count;
            return count;
        }
    }
}

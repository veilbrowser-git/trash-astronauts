using System;

namespace TrashAstronauts.Core.Voice
{
    /// <summary>
    /// The numbers proximity voice chat is built on. Pure C#: nothing here touches Unity, so the whole capture,
    /// codec and playout chain is tested in EditMode.
    ///
    /// Voice is handled as mono 16 kHz audio cut into 20 ms frames (320 samples). On the wire each frame is IMA
    /// ADPCM at 4 bits per sample after a 16 to 12 kHz down-sample (the helmet low-pass on the listener already
    /// takes everything above about 6 kHz off what the player hears, so a 12 kHz codec rate loses nothing audible):
    /// 240 samples = 120 bytes of nibbles plus a 3-byte decoder state, 123 bytes per frame, 6150 bytes per second
    /// of speech. Two frames travel per datagram (25 a second while talking); with the 16-byte session header,
    /// the message header and the end byte that is 270 bytes per datagram, 6.75 kB/s per speaker at the session
    /// level (about 7.45 kB/s counting UDP/IP headers). Silence sends nothing.
    /// </summary>
    public static class VoiceFormat
    {
        /// <summary>The rate everything between the microphone and the speaker works at.</summary>
        public const int SampleRate = 16000;
        public const int FrameMs = 20;
        /// <summary>Samples in one frame at <see cref="SampleRate"/>.</summary>
        public const int FrameSamples = SampleRate * FrameMs / 1000;
        public const double FrameSeconds = FrameMs / 1000.0;

        /// <summary>The codec's own rate (the frame is resampled to it and back).</summary>
        public const int CodecRate = 12000;
        public const int CodecSamples = CodecRate * FrameMs / 1000;
        /// <summary>Decoder state at the start of a frame: predictor (int16) and step index (u8). Each frame decodes on its own.</summary>
        public const int FrameHeaderBytes = 3;
        /// <summary>Encoded size of one frame.</summary>
        public const int FrameBytes = FrameHeaderBytes + CodecSamples / 2;

        /// <summary>Frames carried by one Voice message (and so one datagram).</summary>
        public const int FramesPerPacket = 2;

        /// <summary>Encoded payload bytes per second of continuous speech.</summary>
        public const int PayloadBytesPerSecond = FrameBytes * 1000 / FrameMs;

        public static float ToDb(float rms) => rms > 1e-5f ? 20f * (float)Math.Log10(rms) : -100f;
        public static float FromDb(float db) => (float)Math.Pow(10.0, db / 20.0);

        /// <summary>Root mean square of <paramref name="count"/> samples.</summary>
        public static float Rms(float[] samples, int offset, int count)
        {
            if (samples == null || count <= 0) return 0f;
            double sum = 0.0;
            int end = Math.Min(samples.Length, offset + count);
            for (int i = offset; i < end; i++) sum += samples[i] * samples[i];
            return (float)Math.Sqrt(sum / count);
        }

        /// <summary>Signed distance from <paramref name="b"/> to <paramref name="a"/> in 16-bit sequence space.</summary>
        public static int SeqDelta(ushort a, ushort b) => (short)(ushort)(a - b);
    }
}

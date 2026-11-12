using System;

namespace TrashAstronauts.Core.Voice
{
    /// <summary>
    /// IMA ADPCM, 4 bits per sample: the classic low-cost speech coder (no native library, no tables beyond two
    /// small arrays, integer arithmetic). Used one 20 ms frame at a time with the decoder state written at the
    /// start of each frame, so every frame decodes on its own and a lost datagram costs only its own frames.
    /// </summary>
    public static class ImaAdpcm
    {
        static readonly int[] s_IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

        static readonly int[] s_StepTable =
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97,
            107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
            876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428,
            4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350,
            22385, 24623, 27086, 29794, 32767,
        };

        public const int MaxIndex = 88;

        /// <summary>Encodes one sample, updating the state. Returns the 4-bit code.</summary>
        public static int EncodeSample(int sample, ref int predictor, ref int index)
        {
            int step = s_StepTable[index];
            int diff = sample - predictor;
            int code = 0;
            if (diff < 0)
            {
                code = 8;
                diff = -diff;
            }
            int delta = step >> 3;
            if (diff >= step) { code |= 4; diff -= step; delta += step; }
            step >>= 1;
            if (diff >= step) { code |= 2; diff -= step; delta += step; }
            step >>= 1;
            if (diff >= step) { code |= 1; delta += step; }
            predictor += (code & 8) != 0 ? -delta : delta;
            if (predictor > 32767) predictor = 32767;
            else if (predictor < -32768) predictor = -32768;
            index += s_IndexTable[code];
            if (index < 0) index = 0;
            else if (index > MaxIndex) index = MaxIndex;
            return code;
        }

        /// <summary>Decodes one 4-bit code, updating the state. Returns the sample.</summary>
        public static int DecodeSample(int code, ref int predictor, ref int index)
        {
            int step = s_StepTable[index];
            int delta = step >> 3;
            if ((code & 4) != 0) delta += step;
            if ((code & 2) != 0) delta += step >> 1;
            if ((code & 1) != 0) delta += step >> 2;
            predictor += (code & 8) != 0 ? -delta : delta;
            if (predictor > 32767) predictor = 32767;
            else if (predictor < -32768) predictor = -32768;
            index += s_IndexTable[code & 15];
            if (index < 0) index = 0;
            else if (index > MaxIndex) index = MaxIndex;
            return predictor;
        }

        /// <summary>
        /// Encodes <paramref name="count"/> (even) samples in [-1, 1] as a self-contained block: predictor (int16 LE),
        /// step index (u8), then two codes per byte, low nibble first. Returns the bytes written.
        /// </summary>
        public static int EncodeBlock(float[] src, int offset, int count, byte[] dst, int dstOffset, ref int predictor, ref int index)
        {
            dst[dstOffset] = (byte)predictor;
            dst[dstOffset + 1] = (byte)(predictor >> 8);
            dst[dstOffset + 2] = (byte)index;
            int o = dstOffset + 3;
            for (int i = 0; i < count; i += 2)
            {
                int a = EncodeSample(ToPcm(src[offset + i]), ref predictor, ref index);
                int b = i + 1 < count ? EncodeSample(ToPcm(src[offset + i + 1]), ref predictor, ref index) : 0;
                dst[o++] = (byte)(a | (b << 4));
            }
            return o - dstOffset;
        }

        /// <summary>Decodes a block written by <see cref="EncodeBlock"/>. False if its header is out of range.</summary>
        public static bool DecodeBlock(byte[] src, int offset, int count, float[] dst, int dstOffset)
        {
            int predictor = (short)(src[offset] | (src[offset + 1] << 8));
            int index = src[offset + 2];
            if (index > MaxIndex) return false;
            int o = dstOffset;
            int p = offset + 3;
            for (int i = 0; i < count; i += 2)
            {
                byte packed = src[p++];
                dst[o++] = DecodeSample(packed & 15, ref predictor, ref index) * (1f / 32768f);
                if (i + 1 < count) dst[o++] = DecodeSample(packed >> 4, ref predictor, ref index) * (1f / 32768f);
            }
            return true;
        }

        static int ToPcm(float x)
        {
            if (!(x > -1f)) return -32768; // also catches NaN
            if (x >= 1f) return 32767;
            return (int)Math.Round(x * 32767f);
        }
    }

    /// <summary>
    /// One talker's encoder: 20 ms at 16 kHz in, <see cref="VoiceFormat.FrameBytes"/> out. Low-passes and
    /// resamples to 12 kHz, then ADPCM. Stateful (filter history and ADPCM state run on from frame to frame), so
    /// feed it every frame in order, including the ones that are not sent. No allocation per frame.
    /// </summary>
    public sealed class VoiceEncoder
    {
        readonly VoiceResampler m_Down = new VoiceResampler(VoiceFormat.SampleRate, VoiceFormat.CodecRate, VoiceFormat.FrameSamples, 5200f);
        readonly float[] m_Codec = new float[VoiceFormat.CodecSamples + 4];
        int m_Predictor;
        int m_Index;

        /// <summary>Encodes <see cref="VoiceFormat.FrameSamples"/> samples. Returns the bytes written (always <see cref="VoiceFormat.FrameBytes"/>).</summary>
        public int Encode(float[] frame, int offset, byte[] dst, int dstOffset)
        {
            int n = m_Down.Process(frame, offset, VoiceFormat.FrameSamples, m_Codec, 0, m_Codec.Length);
            for (int i = n; i < VoiceFormat.CodecSamples; i++) m_Codec[i] = 0f; // never happens with whole frames
            return ImaAdpcm.EncodeBlock(m_Codec, 0, VoiceFormat.CodecSamples, dst, dstOffset, ref m_Predictor, ref m_Index);
        }

        public void Reset()
        {
            m_Down.Reset();
            m_Predictor = 0;
            m_Index = 0;
        }
    }

    /// <summary>
    /// One talker's decoder: an encoded frame in, 20 ms at 16 kHz out. Each frame carries its own ADPCM state, so
    /// lost frames do not corrupt the next one; only the 12 to 16 kHz filter keeps a short history.
    /// </summary>
    public sealed class VoiceDecoder
    {
        readonly VoiceResampler m_Up = new VoiceResampler(VoiceFormat.CodecRate, VoiceFormat.SampleRate, VoiceFormat.CodecSamples, 5200f);
        readonly float[] m_Codec = new float[VoiceFormat.CodecSamples];

        /// <summary>False (and nothing written) for a frame of the wrong size or with a bad header.</summary>
        public bool Decode(byte[] src, int offset, int length, float[] dst, int dstOffset)
        {
            if (src == null || dst == null || length != VoiceFormat.FrameBytes || offset < 0 || offset + length > src.Length) return false;
            if (dstOffset < 0 || dstOffset + VoiceFormat.FrameSamples > dst.Length) return false;
            if (!ImaAdpcm.DecodeBlock(src, offset, VoiceFormat.CodecSamples, m_Codec, 0)) return false;
            int n = m_Up.Process(m_Codec, 0, VoiceFormat.CodecSamples, dst, dstOffset, VoiceFormat.FrameSamples);
            for (int i = n; i < VoiceFormat.FrameSamples; i++) dst[dstOffset + i] = 0f;
            return true;
        }

        public void Reset() => m_Up.Reset();
    }
}

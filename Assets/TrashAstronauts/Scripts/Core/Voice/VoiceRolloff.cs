using System;

namespace TrashAstronauts.Core.Voice
{
    /// <summary>
    /// How a crew member's voice falls off with distance, as a gain 0..1: full and clear within 2.5 m (a normal
    /// conversation across a room), then falling roughly like a real voice in air ((2.5/d)^1.2: about -7 dB at
    /// 5 m, -14 dB at 10 m), faded smoothly to nothing between 12 m and 18 m so a voice never carries further
    /// than that. The playback AudioSource uses this as its custom rolloff curve; the echo suppressor uses it to
    /// estimate how loud a voice is at the listener.
    /// </summary>
    public static class VoiceRolloff
    {
        public const float ClearDistance = 2.5f;
        public const float FadeStart = 12f;
        public const float MaxDistance = 18f;
        public const float Exponent = 1.2f;

        public static float Gain(float distance)
        {
            if (!(distance > ClearDistance)) return 1f; // also NaN
            if (distance >= MaxDistance) return 0f;
            float gain = (float)Math.Pow(ClearDistance / distance, Exponent);
            if (distance > FadeStart)
            {
                float t = (distance - FadeStart) / (MaxDistance - FadeStart);
                gain *= 1f - t * t * (3f - 2f * t);
            }
            return gain;
        }
    }
}

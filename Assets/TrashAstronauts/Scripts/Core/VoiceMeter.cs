using UnityEngine;

namespace TrashAstronauts.Core
{
    /// <summary>
    /// Turns raw microphone level into "how loud was that to the creature". It tracks the room's
    /// background level so a noisy fan or the headset's own speakers do not count as speech, and it only
    /// reports sound that stays above that background for a moment, so a single click or bump is ignored.
    /// Pure logic: no microphone or Unity objects, so it can be tested directly.
    /// </summary>
    public sealed class VoiceMeter
    {
        /// <summary>How far above the background a sound must be before it counts at all (dB).</summary>
        public float marginDb = 14f;
        /// <summary>dB above the margin at which loudness reaches 1 (a shout).</summary>
        public float rangeDb = 22f;
        public float floorRiseDbPerSecond = 0.5f;
        public float floorFallDbPerSecond = 12f;
        public float minFloorDb = -66f;
        public float maxFloorDb = -30f;
        /// <summary>Quieter than this (0..1) is treated as silence.</summary>
        public float minLoudness = 0.12f;
        /// <summary>Sound must last this long before it is reported.</summary>
        public float sustainSeconds = 0.12f;
        /// <summary>Minimum time between reports while speaking.</summary>
        public float emitInterval = 0.45f;

        public float FloorDb { get; private set; } = -50f;
        public float LevelDb { get; private set; } = -100f;
        /// <summary>0 = silent or background, about 0.5 = talking, 1 = shouting.</summary>
        public float Loudness { get; private set; }
        public bool IsSpeaking => m_SpeakingFor >= sustainSeconds;

        float m_SpeakingFor;
        float m_SinceEmit = 10f;
        float m_Peak;

        public static float ToDb(float rms) => 20f * Mathf.Log10(Mathf.Max(rms, 1e-5f));

        public void Reset(float floorDb = -50f)
        {
            FloorDb = Mathf.Clamp(floorDb, minFloorDb, maxFloorDb);
            Loudness = 0f;
            m_SpeakingFor = 0f;
            m_SinceEmit = 10f;
            m_Peak = 0f;
        }

        /// <summary>
        /// Feeds one level reading (RMS amplitude, 0..1). Returns true when a noise should be raised, with
        /// the loudest level heard since the last one.
        /// </summary>
        public bool Tick(float rms, float dt, out float emitLoudness)
        {
            emitLoudness = 0f;
            dt = Mathf.Max(0f, dt);
            LevelDb = ToDb(rms);

            // The background estimate drops quickly to a quieter room and creeps up slowly, so speech
            // (which comes in bursts) does not drag it upward.
            if (LevelDb < FloorDb) FloorDb = Mathf.Max(LevelDb, FloorDb - floorFallDbPerSecond * dt);
            else FloorDb = Mathf.Min(LevelDb, FloorDb + floorRiseDbPerSecond * dt);
            FloorDb = Mathf.Clamp(FloorDb, minFloorDb, maxFloorDb);

            Loudness = Mathf.Clamp01((LevelDb - FloorDb - marginDb) / Mathf.Max(1f, rangeDb));
            m_SinceEmit += dt;

            if (Loudness >= minLoudness)
            {
                m_SpeakingFor += dt;
                m_Peak = Mathf.Max(m_Peak, Loudness);
            }
            else
            {
                m_SpeakingFor = 0f;
                m_Peak = 0f;
            }

            if (m_SpeakingFor >= sustainSeconds && m_SinceEmit >= emitInterval)
            {
                emitLoudness = m_Peak;
                m_Peak = 0f;
                m_SinceEmit = 0f;
                return true;
            }
            return false;
        }
    }
}

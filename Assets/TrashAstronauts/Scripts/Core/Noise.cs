using System;
using UnityEngine;

namespace TrashAstronauts.Core
{
    public enum NoiseKind
    {
        Impact = 0,
        Cutting = 1,
        PanelBreak = 2,
        Door = 3,
    }

    /// <summary>A gameplay-relevant sound. Loudness 1 is "the whole deck heard that".</summary>
    public readonly struct NoiseEvent
    {
        public readonly Vector3 Position;
        public readonly float Loudness;
        public readonly NoiseKind Kind;
        public readonly PlayerId Source;

        public NoiseEvent(Vector3 position, float loudness, NoiseKind kind, PlayerId source)
        {
            Position = position;
            Loudness = Mathf.Max(0f, loudness);
            Kind = kind;
            Source = source;
        }
    }

    public static class NoiseRules
    {
        /// <summary>
        /// Decides whether a physics impact counts as a gameplay noise. Slow bumps and resting jitter are
        /// ignored, and each object has a cooldown, so floating junk does not constantly summon the drone.
        /// </summary>
        public static bool TryEvaluateImpact(float relativeSpeed, float secondsSinceLastNoise, NoiseTuning tuning, out float loudness)
        {
            loudness = 0f;
            if (tuning == null) return false;
            if (relativeSpeed < tuning.impactSpeedThreshold) return false;
            if (secondsSinceLastNoise < tuning.perObjectCooldown) return false;

            float span = Mathf.Max(0.01f, tuning.impactSpeedForFullLoudness - tuning.impactSpeedThreshold);
            float t = Mathf.Clamp01((relativeSpeed - tuning.impactSpeedThreshold) / span);
            loudness = Mathf.Lerp(0.4f, 1f, t);
            return true;
        }

        public static bool IsAudible(in NoiseEvent noise, Vector3 listener, float hearingRadiusAtFullLoudness)
        {
            float radius = noise.Loudness * hearingRadiusAtFullLoudness;
            return radius > 0f && (noise.Position - listener).sqrMagnitude <= radius * radius;
        }
    }

    /// <summary>Plasma cutter heat. Fires while the trigger is held, locks out when overheated until it cools.</summary>
    public sealed class CutterHeatModel
    {
        readonly CutterTuning m_Tuning;

        public float Heat { get; private set; }
        public bool Overheated { get; private set; }
        public bool Firing { get; private set; }

        /// <summary>Raised when the cutter overheats (true) or becomes usable again (false).</summary>
        public event Action<bool> OverheatChanged;

        public CutterHeatModel(CutterTuning tuning) { m_Tuning = tuning ?? new CutterTuning(); }

        public void Reset()
        {
            Heat = 0f;
            Firing = false;
            if (Overheated)
            {
                Overheated = false;
                OverheatChanged?.Invoke(false);
            }
        }

        /// <summary>Advances the model. Returns true while the flame is actually on.</summary>
        public bool Tick(float dt, bool triggerHeld)
        {
            if (dt < 0f) dt = 0f;
            Firing = triggerHeld && !Overheated;

            if (Firing)
            {
                Heat = Mathf.Min(1f, Heat + dt / Mathf.Max(0.1f, m_Tuning.secondsToOverheat));
                if (Heat >= 1f)
                {
                    Overheated = true;
                    Firing = false;
                    OverheatChanged?.Invoke(true);
                }
            }
            else
            {
                Heat = Mathf.Max(0f, Heat - dt / Mathf.Max(0.1f, m_Tuning.secondsToCool));
                if (Overheated && Heat <= m_Tuning.restartHeatFraction)
                {
                    Overheated = false;
                    OverheatChanged?.Invoke(false);
                }
            }

            return Firing;
        }
    }
}

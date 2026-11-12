using System;

namespace TrashAstronauts.Core
{
    public enum OxygenWarningLevel
    {
        Normal = 0,
        Low = 1,
        Critical = 2,
        Depleted = 3,
    }

    /// <summary>Oxygen supply for one run. Pure rules: no Unity scene access.</summary>
    public sealed class OxygenTank
    {
        public float Capacity { get; private set; }
        public float Remaining { get; private set; }
        public float LowFraction { get; private set; }
        public float CriticalFraction { get; private set; }
        public OxygenWarningLevel Level { get; private set; }

        public float Fraction => Capacity <= 0f ? 0f : Remaining / Capacity;
        public bool IsDepleted => Remaining <= 0f;

        /// <summary>Raised when the warning level changes (old, new).</summary>
        public event Action<OxygenWarningLevel, OxygenWarningLevel> LevelChanged;

        public OxygenTank(float capacity, float lowFraction, float criticalFraction)
        {
            Refill(capacity, lowFraction, criticalFraction);
        }

        public void Refill(float capacity, float lowFraction, float criticalFraction)
        {
            Capacity = Math.Max(0.01f, capacity);
            Remaining = Capacity;
            CriticalFraction = Math.Max(0f, Math.Min(criticalFraction, lowFraction));
            LowFraction = Math.Max(CriticalFraction, lowFraction);
            Level = OxygenWarningLevel.Normal;
        }

        /// <summary>Drains oxygen. Returns true when the tank hit zero on this call.</summary>
        public bool Drain(float seconds)
        {
            if (seconds <= 0f || IsDepleted) return false;
            Remaining = Math.Max(0f, Remaining - seconds);
            UpdateLevel();
            return IsDepleted;
        }

        /// <summary>
        /// Sets the supply directly (a client mirroring the host's tank). Raises <see cref="LevelChanged"/>
        /// if that crosses a warning threshold, in either direction. Setting the same value twice is harmless.
        /// </summary>
        public void SetRemaining(float seconds)
        {
            if (float.IsNaN(seconds)) return;
            Remaining = Math.Max(0f, Math.Min(Capacity, seconds));
            UpdateLevel();
        }

        void UpdateLevel()
        {
            OxygenWarningLevel next;
            if (Remaining <= 0f) next = OxygenWarningLevel.Depleted;
            else if (Fraction <= CriticalFraction) next = OxygenWarningLevel.Critical;
            else if (Fraction <= LowFraction) next = OxygenWarningLevel.Low;
            else next = OxygenWarningLevel.Normal;

            if (next == Level) return;
            var previous = Level;
            Level = next;
            LevelChanged?.Invoke(previous, next);
        }

        public static string FormatClock(float seconds)
        {
            int total = (int)Math.Ceiling(Math.Max(0f, seconds));
            return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
        }
    }
}

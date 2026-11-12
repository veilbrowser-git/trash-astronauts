using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>What drifts past the salvage claw's window.</summary>
    public enum ClawCargoKind
    {
        /// <summary>Small company crate: easy, low value.</summary>
        Crate = 0,
        /// <summary>A twisted lump of hull scrap.</summary>
        Scrap = 1,
        /// <summary>Long container: awkward, must be gripped near its middle.</summary>
        LongContainer = 2,
        /// <summary>Rare gold-striped strongbox: fast, small, high value.</summary>
        Strongbox = 3,
    }

    /// <summary>
    /// Every number of the salvage claw mini-game in one place: pay, the per-shift cap, how often things
    /// drift past and how the arm moves. The claw is pocket money between contracts, not a way to skip them
    /// (a typical contract pays about 1,050 cr; the store charges 120 to 600 cr).
    /// </summary>
    public static class SalvageClawConfig
    {
        // ------------------------------------------------------------------ pay
        public const int CratePay = 14;
        public const int ScrapPay = 10;
        public const int LongContainerPay = 24;
        public const int StrongboxPay = 60;
        /// <summary>The most the claw pays between two contracts. Resets when a contract is completed or failed.</summary>
        public const int ShiftCap = 150;
        public const string ShiftOverText = "CLAW SHIFT OVER - FLY A CONTRACT";

        public static int PayFor(ClawCargoKind kind)
        {
            switch (kind)
            {
                case ClawCargoKind.Scrap: return ScrapPay;
                case ClawCargoKind.LongContainer: return LongContainerPay;
                case ClawCargoKind.Strongbox: return StrongboxPay;
                default: return CratePay;
            }
        }

        public static string NameOf(ClawCargoKind kind)
        {
            switch (kind)
            {
                case ClawCargoKind.Scrap: return "SCRAP";
                case ClawCargoKind.LongContainer: return "LONG CONTAINER";
                case ClawCargoKind.Strongbox: return "STRONGBOX";
                default: return "CRATE";
            }
        }

        /// <summary>How close the claw tip must be to a thing's grip point for a closing claw to take it (m).</summary>
        public static float GripRadius(ClawCargoKind kind)
        {
            switch (kind)
            {
                case ClawCargoKind.Strongbox: return 0.75f;
                case ClawCargoKind.LongContainer: return 0.9f; // measured to the middle of the container only
                case ClawCargoKind.Scrap: return 0.95f;
                default: return 1.0f;
            }
        }

        /// <summary>Drift speed range (m/s).</summary>
        public static Vector2 SpeedRange(ClawCargoKind kind)
        {
            switch (kind)
            {
                case ClawCargoKind.Strongbox: return new Vector2(0.9f, 1.25f);
                case ClawCargoKind.LongContainer: return new Vector2(0.25f, 0.45f);
                case ClawCargoKind.Scrap: return new Vector2(0.3f, 0.55f);
                default: return new Vector2(0.3f, 0.6f);
            }
        }

        /// <summary>Picks a kind from a uniform roll in [0, 1): crates are common, the strongbox rare.</summary>
        public static ClawCargoKind KindForRoll(double roll)
        {
            if (roll < 0.42) return ClawCargoKind.Crate;
            if (roll < 0.66) return ClawCargoKind.Scrap;
            if (roll < 0.91) return ClawCargoKind.LongContainer;
            return ClawCargoKind.Strongbox;
        }

        // ------------------------------------------------------------------ spawning
        /// <summary>At most this many things drift outside at once.</summary>
        public const int MaxCargo = 5;
        public const float FirstSpawnDelay = 0.6f;
        public static readonly Vector2 SpawnInterval = new Vector2(2.5f, 5.5f);
        /// <summary>Something that has drifted this far from where it crosses the arm's reach is removed (m).</summary>
        public const float DespawnDistance = 14f;
        /// <summary>Everything outside is cleared this long after the last operator leaves the station (s).</summary>
        public const float AbandonDelay = 3f;

        // ------------------------------------------------------------------ the arm
        public const float UpperBoomLength = 5.0f;
        public const float ForeBoomLength = 4.6f;
        /// <summary>Wrist pivot to fingertips.</summary>
        public const float ClawLength = 1.1f;
        /// <summary>Tip speeds at full stick (m/s): sideways, up/down, and extend/retract.</summary>
        public const float LateralSpeed = 1.7f;
        public const float VerticalSpeed = 1.4f;
        public const float ReachSpeed = 1.6f;
        /// <summary>Time constant of the arm's inertia (s): how long the heavy arm takes to follow the stick.</summary>
        public const float Inertia = 0.35f;
        public const float MinReach = 2.6f;
        public const float MaxReach = 10.2f;
        public const float MinYaw = -70f;
        public const float MaxYaw = 80f;
        public const float MinHeight = -4.5f;
        public const float MaxHeight = 5.5f;
        /// <summary>Claw open/close time (s).</summary>
        public const float ClawCloseTime = 0.35f;
        /// <summary>A gripped thing is swallowed once it comes this close to the intake hatch's mouth (m).</summary>
        public const float IntakeRadius = 1.2f;
        /// <summary>The stick's full deflection (degrees) and its dead zone (fraction).</summary>
        public const float StickMaxTilt = 22f;
        public const float StickDeadZone = 0.12f;
        /// <summary>How far the operator's head may be from the station's standing spot to count as "at the station" (m).</summary>
        public const float OperatorRadius = 1.3f;
    }
}

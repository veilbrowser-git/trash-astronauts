using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>What a loose object's surface is made of: decides friction, bounce and how a knock sounds.</summary>
    public enum SurfaceKind
    {
        BareMetal,
        PaintedMetal,
        RubberPlastic,
        GlassCeramic,
    }

    /// <summary>
    /// The one place the game's physical constants live, so the builder, the hands and the tests agree.
    /// Loose things are real masses in near-vacuum: they keep drifting and spinning, lose almost nothing
    /// to "air", use continuous collision so a hard throw never passes through a wall, and ease apart
    /// when they start out overlapping instead of exploding.
    /// </summary>
    public static class RealPhysics
    {
        /// <summary>A person in a pressurised work suit with its life-support pack (kg).</summary>
        public const float PlayerBodyMass = 150f;

        /// <summary>Velocity lost per second to nothing at all: tiny, so drifts and spins last.</summary>
        public const float VacuumLinearDamping = 0.02f;
        public const float VacuumAngularDamping = 0.04f;
        /// <summary>rad/s. A flicked plate may really spin this fast; PhysX's default would clip it.</summary>
        public const float MaxAngularVelocity = 40f;
        /// <summary>m/s. Overlapping bodies separate gently rather than shooting apart.</summary>
        public const float MaxDepenetrationVelocity = 1f;

        /// <summary>One physics step per frame at the Quest's base 72 Hz (interpolation covers 90 Hz).</summary>
        public const float FixedTimestep = 1f / 72f;

        /// <summary>
        /// The editor cannot reliably save the fixed timestep into the project settings from a batch build,
        /// so the game sets it itself before the first scene loads.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ApplyTimestep()
        {
            Time.fixedDeltaTime = FixedTimestep;
        }

        /// <summary>Hand speed (m/s) a strong overarm throw reaches with almost nothing in the hand.</summary>
        public const float ArmThrowSpeed = 14f;
        /// <summary>Effective mass of the forearm and hand that has to be swung along with the load (kg).</summary>
        public const float ArmEffectiveMass = 4f;

        /// <summary>
        /// The fastest an arm can send an object of this mass. The arm can only put so much energy into
        /// itself plus the load, so a 0.1 kg duck leaves at nearly full hand speed and a 40 kg reactor
        /// core at about a third of it.
        /// </summary>
        public static float MaxThrowSpeed(float mass)
        {
            mass = Mathf.Max(0f, mass);
            return ArmThrowSpeed * Mathf.Sqrt(ArmEffectiveMass / (ArmEffectiveMass + mass));
        }

        /// <summary>The near-vacuum settings every loose, throwable body gets.</summary>
        public static void ConfigureLooseBody(Rigidbody body)
        {
            body.linearDamping = VacuumLinearDamping;
            body.angularDamping = VacuumAngularDamping;
            body.maxAngularVelocity = MaxAngularVelocity;
            body.maxDepenetrationVelocity = MaxDepenetrationVelocity;
            // Interpolated so a held or drifting object does not judder between 72 Hz physics steps and a
            // 90 Hz display; speculative CCD works for kinematic and dynamic bodies alike (salvage switches
            // between the two) and stops fast throws tunnelling through thin walls and the locker.
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        }

        /// <summary>Friction (static, dynamic) and bounciness for a surface.</summary>
        public static void Describe(SurfaceKind kind, out float staticFriction, out float dynamicFriction, out float bounciness)
        {
            switch (kind)
            {
                case SurfaceKind.BareMetal: staticFriction = 0.45f; dynamicFriction = 0.35f; bounciness = 0.35f; break;
                case SurfaceKind.RubberPlastic: staticFriction = 0.9f; dynamicFriction = 0.75f; bounciness = 0.6f; break;
                case SurfaceKind.GlassCeramic: staticFriction = 0.35f; dynamicFriction = 0.3f; bounciness = 0.2f; break;
                default: staticFriction = 0.5f; dynamicFriction = 0.4f; bounciness = 0.3f; break;
            }
        }

        /// <summary>How loud a knock on this surface is for the same impulse (rubber thuds, metal clangs).</summary>
        public static float Loudness(SurfaceKind kind)
        {
            switch (kind)
            {
                case SurfaceKind.BareMetal: return 1.15f;
                case SurfaceKind.RubberPlastic: return 0.55f;
                case SurfaceKind.GlassCeramic: return 0.9f;
                default: return 1f;
            }
        }
    }
}

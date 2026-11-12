using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>
    /// One thing drifting past the salvage claw: a simple kinematic mover (no rigidbody, no collider, nothing
    /// networked) that <see cref="SalvageClawStation"/> steps. Positions and velocities are in the station's
    /// local space.
    /// </summary>
    public sealed class SalvageClawCargo : MonoBehaviour
    {
        public enum State
        {
            Drifting = 0,
            Gripped = 1,
            Swallowing = 2,
        }

        public ClawCargoKind kind;

        /// <summary>Drift velocity (station space, m/s).</summary>
        public Vector3 Velocity { get; set; }
        /// <summary>Tumble (station space axis * degrees per second).</summary>
        public Vector3 Spin { get; set; }
        public State Current { get; set; }
        /// <summary>Where its drift path passes closest to the arm; it is removed once it is far from here.</summary>
        public Vector3 PathCentre { get; set; }
        /// <summary>Seconds since it was knocked (it cannot be knocked again for a moment).</summary>
        public float SinceKnock { get; set; } = 10f;
        /// <summary>Swallow animation progress, 0..1.</summary>
        public float SwallowT { get; set; }
        public Vector3 SwallowFrom { get; set; }
        /// <summary>Rotation relative to the wrist while gripped.</summary>
        public Quaternion HeldRotation { get; set; }
        /// <summary>Blends from where it was gripped into the claw.</summary>
        public float HeldBlend { get; set; }
        public Vector3 HeldFrom { get; set; }
        public Vector3 BaseScale { get; set; } = Vector3.one;

        public int Pay => SalvageClawConfig.PayFor(kind);
        public float GripRadius => SalvageClawConfig.GripRadius(kind);
    }
}

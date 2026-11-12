using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>
    /// The tug interior. Oxygen does not drain here and the creature will not enter or chase into it.
    /// The tug is not one box (the supply bay sticks out to one side), so extra boxes can be added.
    /// </summary>
    public sealed class SafeZone : BoxZone
    {
        [Tooltip("Further rooms that are also part of the tug.")]
        public BoxZone[] annexes;

        public override bool Contains(Vector3 worldPoint)
        {
            if (base.Contains(worldPoint)) return true;
            if (annexes == null) return false;
            foreach (var annex in annexes)
            {
                if (annex != null && annex.Contains(worldPoint)) return true;
            }
            return false;
        }

        protected override Color GizmoColor => new Color(0.2f, 1f, 0.4f, 0.5f);
    }
}

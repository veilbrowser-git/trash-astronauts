using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>The tug interior. Oxygen does not drain here and the drone will not enter or chase into it.</summary>
    public sealed class SafeZone : BoxZone
    {
        protected override Color GizmoColor => new Color(0.2f, 1f, 0.4f, 0.5f);
    }
}

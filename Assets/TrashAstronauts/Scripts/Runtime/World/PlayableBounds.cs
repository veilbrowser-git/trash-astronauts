using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>The whole playable volume. Anything important that ends up outside is brought back.</summary>
    public sealed class PlayableBounds : BoxZone
    {
        protected override Color GizmoColor => new Color(1f, 1f, 1f, 0.25f);
    }
}

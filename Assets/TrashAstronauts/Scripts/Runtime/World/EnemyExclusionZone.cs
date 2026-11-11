using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>Places the drone must never move into (the airlock and the service duct).</summary>
    public sealed class EnemyExclusionZone : BoxZone
    {
        protected override Color GizmoColor => new Color(1f, 0.3f, 0.2f, 0.5f);

        void OnEnable() => Mission.EnemyExclusionRegistry.Register(this);
        void OnDisable() => Mission.EnemyExclusionRegistry.Unregister(this);
    }
}

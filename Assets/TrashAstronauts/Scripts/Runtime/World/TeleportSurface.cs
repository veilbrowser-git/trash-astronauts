using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>
    /// Marks a trigger collider (on the TeleportSurface layer) as somewhere the player may teleport.
    /// Area mode lets the player land anywhere on it; anchor mode snaps them to its centre.
    /// </summary>
    public sealed class TeleportSurface : MonoBehaviour
    {
        public bool snapToCenter;

        public Vector3 ResolveTarget(Vector3 hitPoint)
        {
            if (!snapToCenter) return hitPoint;
            Vector3 center = transform.position;
            return new Vector3(center.x, hitPoint.y, center.z);
        }
    }
}

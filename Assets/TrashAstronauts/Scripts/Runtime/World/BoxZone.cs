using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>
    /// An oriented box volume tested by position, not by physics triggers, so results are deterministic
    /// and work in edit-mode tests. Used for the tug safe zone, enemy no-go areas and level bounds.
    /// </summary>
    public class BoxZone : MonoBehaviour
    {
        public Vector3 size = Vector3.one;

        public virtual bool Contains(Vector3 worldPoint)
        {
            Vector3 local = transform.InverseTransformPoint(worldPoint);
            Vector3 half = size * 0.5f;
            return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y && Mathf.Abs(local.z) <= half.z;
        }

        public Vector3 ClosestPointInside(Vector3 worldPoint, float inset = 0f)
        {
            Vector3 local = transform.InverseTransformPoint(worldPoint);
            Vector3 half = size * 0.5f - Vector3.one * inset;
            local.x = Mathf.Clamp(local.x, -half.x, half.x);
            local.y = Mathf.Clamp(local.y, -half.y, half.y);
            local.z = Mathf.Clamp(local.z, -half.z, half.z);
            return transform.TransformPoint(local);
        }

        protected virtual Color GizmoColor => new Color(0.3f, 0.9f, 1f, 0.35f);

        void OnDrawGizmos()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = GizmoColor;
            Gizmos.DrawWireCube(Vector3.zero, size);
        }
    }
}

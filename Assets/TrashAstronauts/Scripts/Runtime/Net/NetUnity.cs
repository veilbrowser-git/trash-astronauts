using TrashAstronauts.Core.Net;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>Conversions between the net core's plain structs and Unity's. Same axes, metres, no allocation.</summary>
    public static class NetUnity
    {
        public static Vector3 ToUnity(this NetVec3 v) => new Vector3(v.x, v.y, v.z);
        public static NetVec3 ToNet(this Vector3 v) => new NetVec3(v.x, v.y, v.z);

        public static Quaternion ToUnity(this NetQuat q)
        {
            var r = new Quaternion(q.x, q.y, q.z, q.w);
            float m = r.x * r.x + r.y * r.y + r.z * r.z + r.w * r.w;
            return m > 1e-8f ? Quaternion.Normalize(r) : Quaternion.identity;
        }

        public static NetQuat ToNet(this Quaternion q) => new NetQuat(q.x, q.y, q.z, q.w);
    }
}

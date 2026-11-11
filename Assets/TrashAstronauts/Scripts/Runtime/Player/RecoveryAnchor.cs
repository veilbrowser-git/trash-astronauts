using System.Collections.Generic;
using UnityEngine;

namespace TrashAstronauts.Player
{
    /// <summary>An authored safe standing spot used by the unstuck action and out-of-bounds recovery.</summary>
    public sealed class RecoveryAnchor : MonoBehaviour
    {
        static readonly List<RecoveryAnchor> s_All = new List<RecoveryAnchor>();
        public static IReadOnlyList<RecoveryAnchor> All => s_All;

        void OnEnable() => s_All.Add(this);
        void OnDisable() => s_All.Remove(this);

        public static RecoveryAnchor FindNearest(Vector3 position)
        {
            RecoveryAnchor best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < s_All.Count; i++)
            {
                float sqr = (s_All[i].transform.position - position).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = s_All[i];
                }
            }
            return best;
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.8f);
            Gizmos.DrawWireSphere(transform.position + Vector3.up * 0.05f, 0.25f);
        }
    }
}

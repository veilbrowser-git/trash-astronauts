using System.Collections.Generic;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// A node in the drone's hand-authored navigation graph. The drone only ever travels along edges
    /// between waypoints (plus a short final approach), which keeps its behaviour predictable and keeps
    /// it out of the tug, the airlock and the service duct, none of which contain waypoints.
    /// </summary>
    public sealed class EnemyWaypoint : MonoBehaviour
    {
        public List<EnemyWaypoint> neighbors = new List<EnemyWaypoint>();

        public Vector3 Position => transform.position;

        public static void Link(EnemyWaypoint a, EnemyWaypoint b)
        {
            if (a == null || b == null || a == b) return;
            if (!a.neighbors.Contains(b)) a.neighbors.Add(b);
            if (!b.neighbors.Contains(a)) b.neighbors.Add(a);
        }

        public static EnemyWaypoint FindNearest(IReadOnlyList<EnemyWaypoint> graph, Vector3 position)
        {
            EnemyWaypoint best = null;
            float bestSqr = float.MaxValue;
            for (int i = 0; i < graph.Count; i++)
            {
                if (graph[i] == null) continue;
                float sqr = (graph[i].Position - position).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = graph[i];
                }
            }
            return best;
        }

        /// <summary>Shortest path by distance (Dijkstra; the graph has a couple of dozen nodes).</summary>
        public static bool FindPath(IReadOnlyList<EnemyWaypoint> graph, EnemyWaypoint from, EnemyWaypoint to, List<EnemyWaypoint> result)
        {
            result.Clear();
            if (from == null || to == null) return false;
            if (from == to)
            {
                result.Add(from);
                return true;
            }

            var distance = new Dictionary<EnemyWaypoint, float>();
            var previous = new Dictionary<EnemyWaypoint, EnemyWaypoint>();
            var open = new List<EnemyWaypoint>();
            for (int i = 0; i < graph.Count; i++)
            {
                if (graph[i] == null) continue;
                distance[graph[i]] = float.MaxValue;
                open.Add(graph[i]);
            }
            if (!distance.ContainsKey(from) || !distance.ContainsKey(to)) return false;
            distance[from] = 0f;

            while (open.Count > 0)
            {
                int bestIndex = 0;
                for (int i = 1; i < open.Count; i++)
                {
                    if (distance[open[i]] < distance[open[bestIndex]]) bestIndex = i;
                }
                var current = open[bestIndex];
                open.RemoveAt(bestIndex);
                if (distance[current] == float.MaxValue) break;
                if (current == to) break;

                foreach (var next in current.neighbors)
                {
                    if (next == null || !distance.ContainsKey(next)) continue;
                    float candidate = distance[current] + Vector3.Distance(current.Position, next.Position);
                    if (candidate < distance[next])
                    {
                        distance[next] = candidate;
                        previous[next] = current;
                    }
                }
            }

            if (!previous.ContainsKey(to)) return false;
            for (var node = to; node != null; node = previous.TryGetValue(node, out var p) ? p : null) result.Add(node);
            result.Reverse();
            return result.Count > 0 && result[0] == from;
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.9f);
            Gizmos.DrawSphere(transform.position, 0.08f);
            foreach (var n in neighbors)
            {
                if (n != null) Gizmos.DrawLine(transform.position, n.transform.position);
            }
        }
    }
}

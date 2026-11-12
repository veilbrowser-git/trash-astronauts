using System.Collections.Generic;
using System.Linq;
using System.Text;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// Measures each wreck in the built scene: floor plan, counts, the creature's patrol circuit, the
    /// longest loot round trip from the airlock, and the rendering budget. Batch:
    /// -executeMethod TrashAstronauts.EditorTools.MapMetrics.Report
    /// </summary>
    public static class MapMetrics
    {
        /// <summary>The suit thruster's top speed (ZeroGLocomotion.thrusterMaxSpeed).</summary>
        public const float ThrusterSpeed = 2.4f;
        static readonly Vector3 Airlock = new Vector3(0f, 1.35f, 5.6f);

        public static void Report()
        {
            var scene = EditorSceneManager.OpenScene(Builder.ScenePath, OpenSceneMode.Single);
            var director = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<MissionDirector>(true)).Single();
            var speeds = director.enemies.Select(e => e.overrideTuning ? e.tuning.patrolSpeed : director.Config.enemy.patrolSpeed).ToArray();
            var sb = new StringBuilder();
            foreach (var map in director.maps)
            {
                var plan = FloorPlan(map, out var floors);
                var lights = map.GetComponentsInChildren<Light>(true).Where(l => l.type == LightType.Point).ToArray();
                float circuit = Circuit(map.patrolRoute);
                float maxLeg = 0f;
                foreach (var node in map.graph) foreach (var n in node.neighbors) maxLeg = Mathf.Max(maxLeg, Flat(node.Position, n.Position));
                float farthest = 0f, total = 0f; string farId = "";
                foreach (var p in map.spawnPoints)
                {
                    float d = PathLength(map.graph, Airlock, p.transform.position);
                    total += d;
                    if (d > farthest) { farthest = d; farId = p.pointId; }
                }
                float mean = total / map.spawnPoints.Length;
                var cfg = director.Config;
                int items = cfg.smallScrapCount + cfg.electronicsCount + cfg.reactorComponentCount + cfg.noveltyCount;
                var renderers = map.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled).ToArray();
                long tris = 0;
                foreach (var r in renderers)
                {
                    var f = r.GetComponent<MeshFilter>();
                    if (f != null && f.sharedMesh != null) for (int i = 0; i < f.sharedMesh.subMeshCount; i++) tris += f.sharedMesh.GetIndexCount(i) / 3;
                }
                int statics = renderers.Count(r => r.gameObject.isStatic);
                sb.AppendLine("[MapMetrics] " + map.mapId
                    + " plan x[" + plan.min.x.ToString("F2") + "," + plan.max.x.ToString("F2") + "] z[" + plan.min.z.ToString("F2") + "," + plan.max.z.ToString("F2") + "]"
                    + " size " + plan.size.x.ToString("F2") + " x " + plan.size.z.ToString("F2")
                    + " floorArea " + floors.Sum(b => b.size.x * b.size.z).ToString("F1")
                    + " | waypoints " + map.graph.Length + " route " + map.patrolRoute.Length + " maxLeg " + maxLeg.ToString("F2")
                    + " circuit " + circuit.ToString("F1") + "m = " + string.Join("/", speeds.Select(s => (circuit / s).ToString("F0") + "s"))
                    + " | spawns " + map.spawnPoints.Length + " lights " + map.lights.Length + " (point " + lights.Length + ", "
                    + (lights.Length * 100f / floors.Sum(b => b.size.x * b.size.z)).ToString("F2") + " per 100 m2)"
                    + " anchors " + map.GetComponentsInChildren<RecoveryAnchor>(true).Length
                    + " | farthest loot " + farId + " " + farthest.ToString("F1") + "m, round trip " + (2f * farthest / ThrusterSpeed).ToString("F0") + "s"
                    + ", mean " + mean.ToString("F1") + "m, " + items + " items one per trip " + (items * 2f * mean / ThrusterSpeed).ToString("F0") + "s"
                    + " | renderers " + renderers.Length + " (static " + statics + ") triangles " + tris);
            }
            var bounds = director.bounds;
            sb.AppendLine("[MapMetrics] bounds centre " + bounds.transform.position + " size " + bounds.size);
            Debug.Log(sb.ToString());
        }

        /// <summary>The floor plan: every floor slab of the wreck (works on maps that are switched off, unlike Collider.bounds).</summary>
        public static Bounds FloorPlan(WreckMap map, out List<Bounds> floors)
        {
            floors = new List<Bounds>();
            foreach (var c in map.GetComponentsInChildren<BoxCollider>(true))
            {
                if (c.name != "Floor") continue;
                var b = new Bounds(c.transform.TransformPoint(c.center), Vector3.zero);
                Vector3 h = c.size * 0.5f;
                for (int i = 0; i < 8; i++)
                    b.Encapsulate(c.transform.TransformPoint(c.center + new Vector3((i & 1) == 0 ? -h.x : h.x, (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z)));
                floors.Add(b);
            }
            var plan = floors[0];
            foreach (var b in floors) plan.Encapsulate(b);
            return plan;
        }

        static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        public static float Circuit(EnemyWaypoint[] route)
        {
            float sum = 0f;
            for (int i = 0; i < route.Length; i++) sum += Flat(route[i].Position, route[(i + 1) % route.Length].Position);
            return sum;
        }

        /// <summary>Airlock to a point along the creature graph (the routes through the wreck), straight legs at the ends.</summary>
        public static float PathLength(EnemyWaypoint[] graph, Vector3 from, Vector3 to)
        {
            var start = EnemyWaypoint.FindNearest(graph, from);
            var end = graph.OrderBy(n => Flat(n.Position, to)).First();
            var dist = graph.ToDictionary(n => n, n => float.MaxValue);
            dist[start] = 0f;
            var open = new List<EnemyWaypoint>(graph);
            while (open.Count > 0)
            {
                var cur = open.OrderBy(n => dist[n]).First();
                open.Remove(cur);
                if (dist[cur] == float.MaxValue) break;
                foreach (var n in cur.neighbors)
                {
                    if (!dist.ContainsKey(n)) continue;
                    float d = dist[cur] + Flat(cur.Position, n.Position);
                    if (d < dist[n]) dist[n] = d;
                }
            }
            return Flat(from, start.Position) + dist[end] + Flat(end.Position, to);
        }
    }
}

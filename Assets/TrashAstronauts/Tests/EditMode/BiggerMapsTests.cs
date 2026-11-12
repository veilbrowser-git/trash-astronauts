using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Mission;
using TrashAstronauts.World;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Pins the bigger-maps update: every wreck's floor plan is 1.5x what it was in both directions, measured
    /// from the airlock join, and the things a player works with (doors, their buttons, cut panels, handrails,
    /// lockers) were not stretched along with it: unit scale all the way up, and the same sizes as before.
    /// </summary>
    public class BiggerMapsTests
    {
        const string ScenePath = "Assets/TrashAstronauts/Scenes/TrashAstronauts.unity";
        const float JoinZ = 7.6f, Scale = 1.5f;

        /// <summary>Floor plans before the update (union of every floor slab), measured with MapMetrics.</summary>
        static readonly Dictionary<string, Rect> OldPlans = new Dictionary<string, Rect>
        {
            { "freighter", Rect.MinMaxRect(-15.45f, 7.6f, 12.45f, 40.6f) },
            { "ore_barge", Rect.MinMaxRect(-15f, 7.6f, 13f, 38.6f) },
            { "survey_ship", Rect.MinMaxRect(-15f, 7.6f, 13f, 36.6f) },
            { "lava_tube", Rect.MinMaxRect(-12f, 7.6f, 9.6f, 41.4f) },
        };

        /// <summary>Counts before the update: loot spots stay the same, lights only go up, patrols stay under twice as long.</summary>
        static readonly Dictionary<string, (int spawns, int lights, float circuit)> Old = new Dictionary<string, (int, int, float)>
        {
            { "freighter", (20, 9, 135.0f) },
            { "ore_barge", (20, 12, 148.6f) },
            { "survey_ship", (20, 13, 159.4f) },
            { "lava_tube", (23, 12, 148.1f) },
        };

        static WreckMap[] Maps()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var director = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<MissionDirector>(true)).Single();
            return director.maps;
        }

        /// <summary>The union of a wreck's floor slabs, worked out from the colliders (the maps are switched off).</summary>
        static Rect FloorPlan(WreckMap map)
        {
            bool any = false;
            float x0 = 0, x1 = 0, z0 = 0, z1 = 0;
            foreach (var c in map.GetComponentsInChildren<BoxCollider>(true))
            {
                if (c.name != "Floor") continue;
                Vector3 h = c.size * 0.5f;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 p = c.transform.TransformPoint(c.center + new Vector3((i & 1) == 0 ? -h.x : h.x, (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z));
                    if (!any) { x0 = x1 = p.x; z0 = z1 = p.z; any = true; }
                    x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x); z0 = Mathf.Min(z0, p.z); z1 = Mathf.Max(z1, p.z);
                }
            }
            Assert.IsTrue(any, map.mapId + " has no floor.");
            return Rect.MinMaxRect(x0, z0, x1, z1);
        }

        [Test]
        public void EveryWreck_IsOneAndAHalfTimesItsOldFloorPlan_AboutTheAirlockJoin()
        {
            var maps = Maps();
            Assert.AreEqual(4, maps.Length);
            foreach (var map in maps)
            {
                Assert.IsTrue(OldPlans.ContainsKey(map.mapId), "No old plan recorded for " + map.mapId);
                Rect before = OldPlans[map.mapId], now = FloorPlan(map);
                string what = map.mapId + ": was x[" + before.xMin + "," + before.xMax + "] z[" + before.yMin + "," + before.yMax + "], is x["
                    + now.xMin + "," + now.xMax + "] z[" + now.yMin + "," + now.yMax + "]";
                Assert.AreEqual(before.width * Scale, now.width, 0.05f, what);
                Assert.AreEqual(before.height * Scale, now.height, 0.05f, what);
                Assert.AreEqual(JoinZ, now.yMin, 0.05f, what + ": the wreck still meets the airlock at the same place.");
                Assert.AreEqual(before.xMin * Scale, now.xMin, 0.05f, what);
                Assert.AreEqual(before.xMax * Scale, now.xMax, 0.05f, what);
                Assert.AreEqual(JoinZ + (before.yMax - JoinZ) * Scale, now.yMax, 0.05f, what);
            }
        }

        [Test]
        public void EveryWreck_KeepsItsLootSpots_HasMoreLights_AndAPatrolUnderTwiceAsLong()
        {
            foreach (var map in Maps())
            {
                var old = Old[map.mapId];
                Assert.AreEqual(old.spawns, map.spawnPoints.Length, map.mapId + ": the same number of loot spots, spread through the bigger space.");
                Assert.Greater(map.lights.Length, old.lights, map.mapId + ": a bigger wreck needs more lamps.");
                float circuit = 0f;
                for (int i = 0; i < map.patrolRoute.Length; i++)
                {
                    Vector3 a = map.patrolRoute[i].Position, b = map.patrolRoute[(i + 1) % map.patrolRoute.Length].Position;
                    circuit += Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
                }
                Assert.Greater(circuit, old.circuit * 1.3f, map.mapId + ": the patrol should cover the bigger wreck.");
                Assert.Less(circuit, old.circuit * 2f, map.mapId + ": a patrol much longer than before would keep the creature away too long.");
                foreach (var node in map.graph)
                    foreach (var next in node.neighbors)
                        Assert.LessOrEqual(Vector2.Distance(new Vector2(node.Position.x, node.Position.z), new Vector2(next.Position.x, next.Position.z)), 6.05f,
                            map.mapId + ": creature legs stay short (" + node.name + " - " + next.name + ")");
            }
        }

        /// <summary>Unit scale from the object all the way up to its wreck: nothing it sits under was stretched.</summary>
        static void AssertUnscaled(Transform t, WreckMap map, string what)
        {
            for (var p = t; p != null; p = p.parent)
            {
                Assert.IsTrue((p.localScale - Vector3.one).sqrMagnitude < 1e-6f, map.mapId + ": " + what + " sits under '" + p.name + "' scaled " + p.localScale);
                if (p == map.transform) break;
            }
        }

        [Test]
        public void DoorsButtonsPanelsRailsAndLockers_AreNotScaled_AndKeepTheirSizes()
        {
            foreach (var map in Maps())
            {
                AssertUnscaled(map.transform, map, "the wreck");
                var doors = map.GetComponentsInChildren<SlidingDoor>(true);
                Assert.IsNotEmpty(doors, map.mapId);
                foreach (var door in doors)
                {
                    AssertUnscaled(door.transform, map, door.name);
                    Assert.That(door.doorwaySize.x, Is.InRange(1.4f, 1.61f), map.mapId + ": " + door.name + " must stay door-sized.");
                    Assert.That(door.doorwaySize.y, Is.InRange(2.0f, 2.21f), map.mapId + ": " + door.name);
                    var leaf = door.leaf.GetComponent<BoxCollider>();
                    Assert.LessOrEqual(Vector3.Scale(leaf.size, leaf.transform.lossyScale).x, 1.7f, map.mapId + ": " + door.name + " leaf");
                    Assert.LessOrEqual(Vector3.Scale(leaf.size, leaf.transform.lossyScale).y, 2.2f, map.mapId + ": " + door.name + " leaf");
                    foreach (var button in door.buttons ?? new PushButton[0])
                    {
                        AssertUnscaled(button.transform, map, door.name + " " + button.name);
                        var box = button.GetComponent<BoxCollider>();
                        Assert.AreEqual(0.22f, box.size.x, 0.001f, map.mapId + ": " + button.name);
                        Assert.AreEqual(0.14f, box.size.y, 0.001f, map.mapId + ": " + button.name);
                        Assert.That(button.transform.position.y, Is.InRange(1.1f, 1.3f), map.mapId + ": door buttons stay at hand height.");
                    }
                }

                Assert.IsNotEmpty(map.cuttables, map.mapId);
                foreach (var panel in map.GetComponentsInChildren<Cuttable>(true))
                {
                    AssertUnscaled(panel.transform, map, panel.name);
                    var sheet = panel.plate.transform.Find("Sheet");
                    Vector3 size = sheet.lossyScale;
                    Assert.LessOrEqual(size.x, 1.13f, map.mapId + ": " + panel.panelId + " plate width");
                    Assert.LessOrEqual(size.y, 2.05f, map.mapId + ": " + panel.panelId + " plate height");
                    foreach (var point in panel.cutPoints) Assert.That(point.transform.position.y, Is.InRange(0.4f, 2.2f), map.mapId + ": " + panel.panelId + " cut points within reach.");
                }

                var rails = map.GetComponentsInChildren<HoverHighlight>(true).Where(h => h.name == "Rail").ToArray();
                Assert.Greater(rails.Length, 30, map.mapId + ": handrails along the walls.");
                foreach (var rail in rails)
                {
                    AssertUnscaled(rail.transform, map, "a handrail");
                    var bar = rail.transform.Find("Bar");
                    Assert.AreEqual(0.04f, bar.lossyScale.x, 0.001f, map.mapId + ": handrails keep their thickness.");
                    Assert.AreEqual(0.04f, bar.lossyScale.z, 0.001f, map.mapId + ": handrails keep their thickness.");
                    var col = rail.GetComponent<BoxCollider>();
                    Assert.AreEqual(0.11f, col.size.x, 0.001f);
                    Assert.LessOrEqual(col.size.z, 6.0f, map.mapId + ": longer walls get more rail segments, not longer bars.");
                }
                int handHeight = rails.Count(r => Mathf.Abs(r.transform.position.y - 1.05f) < 0.01f);
                Assert.Greater(handHeight, rails.Length * 0.9f, map.mapId + ": handrails stay at hand height.");

                foreach (var lockers in map.GetComponentsInChildren<Transform>(true).Where(t => t.name == "Lockers"))
                {
                    AssertUnscaled(lockers, map, "a locker row");
                    Assert.AreEqual(1.9f, lockers.GetComponent<BoxCollider>().size.y, 0.001f, map.mapId + ": lockers keep their height.");
                }
            }
        }
    }
}

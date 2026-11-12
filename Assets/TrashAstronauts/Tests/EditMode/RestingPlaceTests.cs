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
    /// Salvage must never come to rest hidden where a hand cannot go. Things rest on the colliders, so the
    /// rock you see must not stand proud of them in the band where items lie and hands reach for them
    /// (the cave's rock is a warped copy of each box, the boxes are the colliders).
    /// </summary>
    public class RestingPlaceTests
    {
        const string ScenePath = "Assets/TrashAstronauts/Scenes/TrashAstronauts.unity";

        /// <summary>How far the rendered rock stands out of the colliders into the open, near the floor (where items lie).</summary>
        static void MeasureRockIntrusion(float maxHeight, out float worst, out int intruding, out int sampled, out Vector3 where)
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var director = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<MissionDirector>(true)).Single();
            var cave = director.maps.Single(m => m.mapId == "lava_tube");
            var gravity = cave.GetComponent<MapGravity>();
            foreach (var map in director.maps) map.gameObject.SetActive(map == cave);
            Physics.SyncTransforms();

            worst = 0f;
            intruding = 0;
            sampled = 0;
            where = Vector3.zero;
            var hits = new Collider[16];
            var seen = new HashSet<Vector3>();
            foreach (var renderer in cave.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!renderer.name.StartsWith("Combined Regolith") && !renderer.name.StartsWith("Combined Basalt")) continue;
                var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                foreach (var local in mesh.vertices)
                {
                    Vector3 v = renderer.transform.TransformPoint(local);
                    if (v.y < -0.01f || v.y > maxHeight || !gravity.Contains(v) || !seen.Add(v)) continue;
                    sampled++;
                    if (Physics.CheckSphere(v, 0.0005f, GameLayers.RestingMask, QueryTriggerInteraction.Ignore)) continue; // inside a collider: fine
                    int count = Physics.OverlapSphereNonAlloc(v, 0.3f, hits, GameLayers.RestingMask, QueryTriggerInteraction.Ignore);
                    float nearest = 0.3f;
                    for (int i = 0; i < count; i++) nearest = Mathf.Min(nearest, Vector3.Distance(hits[i].ClosestPoint(v), v));
                    if (nearest > 0.005f) intruding++;
                    if (nearest > worst) { worst = nearest; where = v; }
                }
            }
        }

        [Test]
        public void InTheCave_TheRockYouSee_NeverStandsOutOfItsCollidersWhereItemsLie()
        {
            MeasureRockIntrusion(0.35f, out float worst, out int intruding, out int sampled, out Vector3 where);
            string summary = sampled + " rock vertices up to 0.35 m checked; " + intruding + " stand more than 5 mm out of the colliders, the worst "
                + (worst * 100f).ToString("F1") + " cm at " + where.ToString("F2");
            Debug.Log("[RestingPlace] " + summary);
            Assert.Greater(sampled, 500, summary);
            Assert.LessOrEqual(worst, 0.01f, "Rock standing out over the floor or low on the walls would hide an item resting against it: " + summary);
        }
    }
}

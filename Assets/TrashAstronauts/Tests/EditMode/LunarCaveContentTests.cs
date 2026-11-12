using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Mission;
using TrashAstronauts.World;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TrashAstronauts.Tests
{
    /// <summary>The lava tube outpost as built into the scene: its gravity volume, its lights and its rendering budget.</summary>
    public class LunarCaveContentTests
    {
        const string ScenePath = "Assets/TrashAstronauts/Scenes/TrashAstronauts.unity";

        static (MissionDirector director, WreckMap cave) Open()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var director = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<MissionDirector>(true)).Single();
            var cave = director.maps.SingleOrDefault(m => m.mapId == "lava_tube");
            Assert.IsNotNull(cave, "The lava tube should be in the rotation.");
            return (director, cave);
        }

        [Test]
        public void TheCave_IsInTheRotation_WithABoardName_AndOnlyItHasGravity()
        {
            var (director, cave) = Open();
            Assert.AreEqual("LAVA TUBE OUTPOST", cave.displayName);
            Assert.IsFalse(string.IsNullOrEmpty(cave.blurb));
            var gravity = cave.GetComponent<MapGravity>();
            Assert.IsNotNull(gravity, "The cave needs a gravity volume.");
            Assert.AreEqual(-1.62f, gravity.gravity.y, 0.001f, "Lunar gravity.");
            Assert.AreEqual(0f, gravity.gravity.x);
            Assert.AreEqual(0f, gravity.gravity.z);
            foreach (var map in director.maps.Where(m => m != cave))
                Assert.IsNull(map.GetComponentInChildren<MapGravity>(true), map.displayName + " is weightless.");
            Assert.AreEqual(1, Object.FindObjectsByType<MapGravity>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length, "Only the cave has gravity.");

            // The volume covers every loot spot and creature waypoint, and stops short of the airlock and the tug.
            foreach (var point in cave.spawnPoints) Assert.IsTrue(gravity.Contains(point.transform.position), point.pointId);
            foreach (var node in cave.graph) Assert.IsTrue(gravity.Contains(node.transform.position), node.name);
            Assert.IsFalse(gravity.Contains(new Vector3(0f, 1.2f, 7.4f)), "The airlock is weightless.");
            Assert.IsFalse(gravity.Contains(new Vector3(0f, 1.2f, -1f)), "The tug is weightless.");
            Assert.IsFalse(director.safeZone.Contains(gravity.transform.TransformPoint(gravity.center)));
        }

        [Test]
        public void EveryPoweredLightInTheCave_IsRegisteredForBlackouts()
        {
            var (_, cave) = Open();
            var lights = cave.GetComponentsInChildren<Light>(true);
            var powered = lights.Where(l => !l.name.StartsWith("Earthshine")).ToArray();
            Assert.Greater(powered.Length, 8);
            foreach (var light in powered) Assert.IsTrue(cave.lights.Contains(light), light.name + " would stay on in a blackout.");
            Assert.IsTrue(cave.lights.All(l => l != null && l.shadows == LightShadows.None));
            Assert.IsTrue(cave.glowStrips.All(r => r != null && !r.gameObject.isStatic), "Glow that goes dark must not be static-batched.");
            Assert.IsTrue(powered.All(l => l.GetComponent<ProximityFlicker>() != null), "Wreck lights flicker when the creature is near.");
        }
    }
}

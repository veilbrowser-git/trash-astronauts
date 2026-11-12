using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>The lava tube outpost: lunar gravity inside the tube only, the player resting on the ground, the creature's patrol.</summary>
    public class LunarCavePlayTests
    {
        MissionDirector m_Director;
        PlayerContext m_Player;
        int m_Cave;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            MissionDirector.StorageOverride = new MemorySaveStorage();
            MissionDirector.SeedOverride = 21;
            MissionDirector.CreatureOverride = 0;
            MissionDirector.MapOverride = 0;
            MissionDirector.ConditionOverride = ContractCondition.Standard;
            BlackoutController.DamageOverride = 0f;
            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            yield return null;
            yield return null;
            m_Director = MissionDirector.Active;
            m_Player = m_Director.Players.First();
            m_Cave = System.Array.FindIndex(m_Director.maps, m => m.mapId == "lava_tube");
            Assert.GreaterOrEqual(m_Cave, 0, "The lava tube should be one of the wrecks.");
        }

        [TearDown]
        public void TearDown()
        {
            MissionDirector.StorageOverride = null;
            MissionDirector.SeedOverride = null;
            MissionDirector.CreatureOverride = null;
            MissionDirector.MapOverride = null;
            MissionDirector.ConditionOverride = null;
            BlackoutController.DamageOverride = null;
        }

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        static IEnumerator Seconds(float seconds)
        {
            for (float t = 0f; t < seconds; t += Time.deltaTime) yield return null;
        }

        IEnumerator Dock(int map)
        {
            MissionDirector.MapOverride = map;
            MissionDirector.SeedOverride = 5;
            if (m_Director.Phase == MissionPhase.Active) m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            yield return Frames(2);
            yield return new WaitForFixedUpdate();
            Physics.SyncTransforms();
        }

        SalvageItem LooseItem()
        {
            var item = m_Director.Items.FirstOrDefault(i => !i.IsMounted && !i.IsSecured && i.definition != null && !i.definition.bulky
                && !i.GetComponent<Rigidbody>().isKinematic);
            Assert.IsNotNull(item, "Need a loose item to drop.");
            return item;
        }

        static void Place(SalvageItem item, Vector3 position, Vector3 velocity)
        {
            var body = item.GetComponent<Rigidbody>();
            body.position = position;
            item.transform.position = position;
            body.linearVelocity = velocity;
            body.angularVelocity = Vector3.zero;
            body.WakeUp();
            Physics.SyncTransforms();
        }

        void ParkHands()
        {
            foreach (var driver in m_Player.GetComponentsInChildren<TrackedPoseDriver>()) driver.enabled = false;
            foreach (var hand in new[] { m_Player.leftHand, m_Player.rightHand })
            {
                hand.GripOverride = 0f;
                hand.TriggerOverride = 0f;
                hand.ThumbstickClickOverride = false;
            }
        }

        [UnityTest]
        public IEnumerator InTheCave_AReleasedItemFallsAndRestsOnTheFloor_InAnotherWreckItKeepsDrifting()
        {
            yield return Dock(m_Cave);
            var item = LooseItem();
            var body = item.GetComponent<Rigidbody>();
            Vector3 drop = new Vector3(0f, 1.7f, 24f); // the middle of the main tunnel
            Assert.AreEqual(-1.62f, MapGravity.GravityAt(drop).y, 0.001f);
            Place(item, drop, Vector3.zero);

            float fastest = 0f;
            for (float t = 0f; t < 6f; t += Time.deltaTime)
            {
                fastest = Mathf.Max(fastest, -body.linearVelocity.y);
                yield return null;
            }
            Assert.Less(fastest, 2.3f, "Things fall slowly on the Moon.");
            Assert.Greater(fastest, 0.8f, "It should have fallen, not drifted.");
            Vector3 p = body.worldCenterOfMass;
            Assert.Less(p.y, 0.45f, item.name + " should be lying on the floor, not floating at " + p);
            Assert.GreaterOrEqual(p.y, -0.02f, item.name + " sank through the floor.");
            Assert.Less(body.linearVelocity.magnitude, 0.08f, item.name + " should have come to rest.");
            Assert.Less(Vector2.Distance(new Vector2(p.x, p.z), new Vector2(drop.x, drop.z)), 1.4f, "It should land roughly below where it was let go.");

            // The ore barge is weightless: the same item keeps drifting where it is put.
            yield return Dock(1);
            Assert.AreNotEqual("lava_tube", m_Director.ActiveMap.mapId);
            Assert.AreEqual(Vector3.zero, MapGravity.GravityAt(drop), "No gravity once the lava tube is not docked.");
            item = LooseItem();
            body = item.GetComponent<Rigidbody>();
            Vector3 start = new Vector3(0f, 1.6f, 19f);
            Place(item, start, new Vector3(0.2f, 0f, 0f));
            yield return Seconds(1.5f);
            Assert.AreEqual(start.y, body.position.y, 0.06f, "In zero g it does not fall.");
            Assert.Greater(body.linearVelocity.magnitude, 0.1f, "In zero g it keeps drifting.");
        }

        [UnityTest]
        public IEnumerator InTheCave_ThePlayerSinksGentlyToTheGround_RestsOnIt_AndCanHopOffWithTheThruster()
        {
            yield return Dock(m_Cave);
            ParkHands();
            m_Director.keepPlayersInHub = false;
            var zeroG = m_Player.locomotion.zeroG;
            Assert.IsTrue(m_Player.locomotion.TeleportTo(new Vector3(0f, 0f, 24f), Quaternion.identity));
            yield return Frames(3);
            zeroG.Stop();
            zeroG.AddImpulse(Vector3.up * 1.0f);

            // Up, then gently back down, never faster than the comfort cap, and never tilted.
            float top = 0f, fastest = 0f, lastY = m_Player.transform.position.y;
            for (float t = 0f; t < 6f; t += Time.deltaTime)
            {
                float y = m_Player.transform.position.y;
                top = Mathf.Max(top, y);
                if (Time.deltaTime > 0f) fastest = Mathf.Max(fastest, (lastY - y) / Time.deltaTime);
                lastY = y;
                Assert.Less(Vector3.Angle(m_Player.transform.up, Vector3.up), 0.01f, "The view must never tilt.");
                yield return null;
            }
            Assert.Greater(top, 0.15f, "A push upward should lift you off the ground.");
            Assert.LessOrEqual(fastest, MapGravity.PlayerMaxFallSpeed + 0.15f, "The fall is capped low for comfort.");
            float rest = m_Player.transform.position.y;
            Assert.Less(rest, 0.08f, "The player should be back on the ground.");
            Assert.GreaterOrEqual(rest, -0.02f, "The player must not sink through the floor.");
            Assert.Less(zeroG.Velocity.magnitude, 0.02f, "Standing on the ground, the player is at rest.");
            yield return Seconds(1f);
            Assert.AreEqual(rest, m_Player.transform.position.y, 0.01f, "...and stays there.");

            // Point a hand up and fire the thruster: a hop.
            var hand = m_Player.rightHand;
            hand.transform.position = m_Player.HeadPosition + new Vector3(0.35f, -0.25f, 0.1f);
            hand.transform.rotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);
            yield return Frames(2);
            hand.TriggerOverride = 1f;
            yield return Seconds(1.5f);
            Assert.IsTrue(zeroG.IsThrusting);
            hand.TriggerOverride = 0f;
            Assert.Greater(m_Player.transform.position.y, rest + 0.6f, "The thruster should lift you well clear of the ground, not just cancel your weight.");
            yield return Seconds(5f);
            Assert.Less(m_Player.transform.position.y, 0.08f, "After the hop you come back down.");
        }

        [UnityTest]
        public IEnumerator InsideTheTugAndTheAirlock_ThereIsNoGravity_EvenWithTheCaveDocked()
        {
            yield return Dock(m_Cave);
            Assert.AreEqual("lava_tube", m_Director.ActiveMap.mapId);
            Assert.AreEqual(Vector3.zero, MapGravity.GravityAt(new Vector3(0f, 1.2f, -1f)), "The tug is weightless.");
            Assert.AreEqual(Vector3.zero, MapGravity.GravityAt(new Vector3(0f, 1.2f, 5.6f)), "The airlock is weightless.");
            Assert.AreEqual(Vector3.zero, MapGravity.GravityAt(new Vector3(-4f, 1.2f, -4.8f)), "The supply bay is weightless.");
            Assert.AreEqual(-1.62f, MapGravity.GravityAt(new Vector3(0f, 1.2f, 10f)).y, 0.001f, "The cave mouth has the Moon's gravity.");

            // An item let go in the cabin floats.
            var item = LooseItem();
            var body = item.GetComponent<Rigidbody>();
            Vector3 cabin = new Vector3(0.6f, 1.5f, -1.2f);
            Place(item, cabin, Vector3.zero);
            // The player in the tug drifts upward and keeps going.
            ParkHands();
            var zeroG = m_Player.locomotion.zeroG;
            zeroG.Stop();
            float y0 = m_Player.transform.position.y;
            zeroG.AddImpulse(Vector3.up * 0.3f);
            yield return Seconds(1.2f);
            Assert.AreEqual(cabin.y, body.position.y, 0.06f, "In the tug things do not fall.");
            Assert.Greater(zeroG.Velocity.y, 0.2f, "In the tug the player keeps drifting up.");
            Assert.Greater(m_Player.transform.position.y, y0 + 0.2f);
        }

        [UnityTest]
        public IEnumerator InTheCave_TheCreaturePatrolsEveryChamber_AndIsNotPulledDown()
        {
            yield return Dock(m_Cave);
            var map = m_Director.ActiveMap;
            var creature = m_Director.ActiveEnemy;
            Assert.AreSame(map.dock, creature.dock);
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            Physics.SyncTransforms();
            var body = creature.GetComponent<Rigidbody>();
            Assert.IsTrue(body == null || body.isKinematic, "The creature is not a falling body.");

            var targets = new[] { "WP H4", "WP KW", "WP P0", "WP D2", "WP MW" };
            var visited = new HashSet<string>();
            for (int i = 0; i < 12000 && visited.Count < targets.Length; i++)
            {
                creature.Step(0.05f);
                Vector3 p = creature.transform.position;
                foreach (var node in map.patrolRoute)
                {
                    if (!targets.Contains(node.name)) continue;
                    Vector3 flat = node.transform.position - p;
                    flat.y = 0f;
                    if (flat.magnitude < 0.5f) visited.Add(node.name);
                }
                Assert.IsTrue(MapGravity.GravityAt(p) != Vector3.zero || p.z < 7.75f, "The creature left the cave at " + p);
                Assert.IsFalse(Physics.CheckSphere(p, 0.1f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore), "Inside rock at " + p);
                if (i % 400 == 0) yield return null;
            }
            Assert.AreEqual(targets.Length, visited.Count, "Did not reach " + string.Join(", ", targets.Where(t => !visited.Contains(t))));
        }
    }
}

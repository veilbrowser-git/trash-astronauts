using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>Loose things are real masses in near-vacuum: they drift, spin, bounce by material and never tunnel.</summary>
    public class RealPhysicsPlayTests
    {
        MissionDirector m_Director;
        PlayerContext m_Player;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            MissionDirector.StorageOverride = new MemorySaveStorage();
            MissionDirector.SeedOverride = 31;
            MissionDirector.CreatureOverride = null;
            MissionDirector.MapOverride = 0;
            MissionDirector.ConditionOverride = ContractCondition.Standard;
            BlackoutController.DamageOverride = 0f;
            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            yield return null;
            yield return null;
            m_Director = MissionDirector.Active;
            m_Player = m_Director.Players.First();
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

        static IEnumerator Steps(int count)
        {
            for (int i = 0; i < count; i++) yield return new WaitForFixedUpdate();
        }

        /// <summary>The axis direction from this point with the most open space, and how much.</summary>
        static Vector3 OpenDirection(Vector3 from, out float clear)
        {
            Vector3 best = Vector3.forward;
            clear = 0f;
            foreach (var dir in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right, Vector3.up, Vector3.down })
            {
                float d = Physics.SphereCast(from, 0.2f, dir, out RaycastHit hit, 30f, ~0, QueryTriggerInteraction.Ignore) ? hit.distance : 30f;
                if (d > clear) { clear = d; best = dir; }
            }
            return best;
        }

        SalvageItem Loose(string id) => m_Director.Items.FirstOrDefault(i => !i.IsMounted && i.definition != null && i.definition.id == id)
            ?? m_Director.Items.First(i => !i.IsMounted && i.definition != null && !i.definition.bulky);

        [UnityTest]
        public IEnumerator EveryLooseObject_HasARealMass_AMaterial_AndVacuumSettings()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Steps(2);
            Assert.AreEqual(RealPhysics.PlayerBodyMass, m_Director.Config.playerMass, 0.01f, "The player is a suited person: about 150 kg.");

            var bodies = Object.FindObjectsByType<ImpactNoiseEmitter>(FindObjectsInactive.Include)
                .Select(e => e.GetComponent<Rigidbody>()).Where(b => b != null && b.gameObject.activeInHierarchy).ToArray();
            Assert.Greater(bodies.Length, 5);
            foreach (var body in bodies)
            {
                string what = body.name;
                Assert.LessOrEqual(body.linearDamping, 0.05f, what + ": near-vacuum, things keep drifting.");
                Assert.LessOrEqual(body.angularDamping, 0.05f, what + ": and keep spinning.");
                Assert.GreaterOrEqual(body.maxAngularVelocity, 30f, what);
                Assert.LessOrEqual(body.maxDepenetrationVelocity, 1.01f, what + ": overlaps ease apart.");
                Assert.AreNotEqual(CollisionDetectionMode.Discrete, body.collisionDetectionMode, what + ": throwables use continuous collision.");
                Assert.AreEqual(RigidbodyInterpolation.Interpolate, body.interpolation, what + ": no judder between physics steps.");
                Assert.That(body.mass, Is.InRange(0.05f, 60f), what);
                foreach (var collider in body.GetComponentsInChildren<Collider>(true))
                {
                    if (collider.isTrigger) continue;
                    Assert.IsNotNull(collider.sharedMaterial, what + "/" + collider.name + " has a surface material.");
                    Assert.That(collider.sharedMaterial.bounciness, Is.InRange(0.1f, 0.7f), what);
                    Assert.That(collider.sharedMaterial.dynamicFriction, Is.InRange(0.2f, 0.9f), what);
                }
            }

            foreach (var item in m_Director.Items.Where(i => i.definition != null))
                Assert.AreEqual(item.definition.mass, item.Body.mass, 0.001f, item.name);
            var reactor = m_Director.Items.Where(i => i.definition != null).OrderByDescending(i => i.Body.mass).First();
            Assert.GreaterOrEqual(reactor.Body.mass, 30f, "A reactor regulator core in a steel cage weighs tens of kilograms.");
            var duck = m_Director.Items.Where(i => i.definition != null).OrderBy(i => i.Body.mass).First();
            Assert.LessOrEqual(duck.Body.mass, 0.2f, "The rubber duck weighs next to nothing.");
            Assert.AreEqual(1f / 72f, Time.fixedDeltaTime, 1e-5f);
        }

        [UnityTest]
        public IEnumerator ALooseObject_KeepsDriftingAndSpinning_InNearVacuum()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Steps(2);
            var item = Loose("elec_comms_relay");
            Vector3 start = item.transform.position;
            Vector3 dir = OpenDirection(start, out float clear);
            Assume.That(clear, Is.GreaterThan(1.2f), "Need open space to drift in.");

            item.Body.linearVelocity = dir * 0.5f;
            item.Body.angularVelocity = Vector3.up * 6f;
            yield return Steps(72); // one second
            Assert.Greater(Vector3.Dot(item.Body.linearVelocity, dir), 0.48f, "Drift is barely damped: " + item.Body.linearVelocity);
            Assert.Greater(item.Body.angularVelocity.magnitude, 5.6f, "Spin is barely damped: " + item.Body.angularVelocity);
            Assert.Greater(Vector3.Dot(item.transform.position - start, dir), 0.45f);
        }

        [UnityTest]
        public IEnumerator AHardThrow_NeverTunnelsThroughAWall()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Steps(2);
            var item = Loose("elec_nav_board"); // the thinnest thing aboard
            Vector3 from = item.transform.position;
            Vector3 dir = OpenDirection(from, out float clear);
            Assume.That(clear, Is.InRange(1.2f, 29f), "Need a wall to throw at.");
            Assert.IsTrue(Physics.Raycast(from, dir, out RaycastHit wall, 30f, ~0, QueryTriggerInteraction.Ignore));

            item.Body.position = from;
            item.Body.linearVelocity = dir * 25f; // far harder than anyone can throw
            yield return Steps(36);
            float along = Vector3.Dot(item.transform.position - from, dir);
            Assert.Less(along, wall.distance + 0.05f, "It must bounce off " + wall.collider.name + ", not pass through it.");
        }

        [UnityTest]
        public IEnumerator OverlappingObjects_EaseApart_InsteadOfExploding()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Steps(2);
            var items = m_Director.Items.Where(i => !i.IsMounted && i.definition != null && !i.definition.bulky).Take(2).ToArray();
            Assert.AreEqual(2, items.Length);
            Vector3 at = items[0].transform.position;
            OpenDirection(at, out float clear);
            Assume.That(clear, Is.GreaterThan(1f));
            foreach (var item in items)
            {
                item.Body.position = at;
                item.transform.position = at;
                item.Body.linearVelocity = Vector3.zero;
                item.Body.angularVelocity = Vector3.zero;
            }
            Physics.SyncTransforms();
            float fastest = 0f;
            for (int i = 0; i < 36; i++)
            {
                yield return new WaitForFixedUpdate();
                foreach (var item in items) fastest = Mathf.Max(fastest, item.Body.linearVelocity.magnitude);
            }
            Assert.LessOrEqual(fastest, RealPhysics.MaxDepenetrationVelocity + 0.25f, "Pushed apart gently.");
            Assert.Greater(Vector3.Distance(items[0].transform.position, items[1].transform.position), 0.03f, "But they do separate.");
        }

        [UnityTest]
        public IEnumerator Rubber_BouncesMoreThan_BareMetal()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Steps(2);
            var duck = m_Director.Items.First(i => i.definition != null && i.definition.id == "novelty_rubber_duck");
            var metal = Loose("scrap_hull_plate");
            float duckRebound = 0f, metalRebound = 0f;
            foreach (var item in new[] { duck, metal })
            {
                Vector3 from = item.transform.position;
                Vector3 dir = OpenDirection(from, out float clear);
                Assume.That(clear, Is.InRange(1.2f, 29f));
                // Start about 0.8 m from the wall, so both hit it squarely within the time allowed.
                Vector3 near = from + dir * (clear - 0.8f);
                item.Body.position = near;
                item.transform.position = near;
                Physics.SyncTransforms();
                item.Body.angularVelocity = Vector3.zero;
                item.transform.rotation = Quaternion.identity;
                item.Body.rotation = Quaternion.identity;
                item.Body.linearVelocity = dir * 3f;
                float rebound = 0f;
                for (int i = 0; i < 90; i++)
                {
                    yield return new WaitForFixedUpdate();
                    rebound = Mathf.Max(rebound, -Vector3.Dot(item.Body.linearVelocity, dir));
                }
                if (item == duck) duckRebound = rebound; else metalRebound = rebound;
            }
            Assert.Greater(metalRebound, 0.2f, "Metal still rings off a wall.");
            Assert.Greater(duckRebound, metalRebound, "Rubber is springier than bare metal.");
        }
    }
}

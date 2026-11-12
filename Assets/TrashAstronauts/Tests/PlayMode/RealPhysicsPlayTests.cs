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

        // ------------------------------------------------------------------ Newton's third law for the player

        /// <summary>Puts the player in open space at the near end of the long corridor, at rest, hands idle.</summary>
        IEnumerator FloatInCargoBay()
        {
            m_Director.keepPlayersInHub = false;
            m_Player.locomotion.TeleportTo(new Vector3(0f, 0.2f, 9.2f), Quaternion.identity);
            for (int i = 0; i < 3; i++) yield return null;
            m_Player.locomotion.zeroG.Stop();
            foreach (var hand in new[] { m_Player.leftHand, m_Player.rightHand })
            {
                hand.GripOverride = 0f;
                hand.TriggerOverride = 0f;
                hand.ThumbstickClickOverride = false;
            }
            yield return null;
            yield return null;
        }

        IEnumerator GrabWith(HandRig hand, SalvageItem item)
        {
            item.Body.linearVelocity = Vector3.zero;
            item.Body.angularVelocity = Vector3.zero;
            item.transform.position = hand.interactor.transform.position;
            item.Body.position = item.transform.position;
            Physics.SyncTransforms();
            hand.GripOverride = 1f;
            var select = hand.interactor.selectInput;
            select.inputSourceMode = UnityEngine.XR.Interaction.Toolkit.Inputs.Readers.XRInputButtonReader.InputSourceMode.ManualValue;
            select.QueueManualState(true, 1f, true, false);
            for (int i = 0; i < 4; i++) yield return null;
            Assert.IsTrue(hand.IsHolding, "The hand should have picked up " + item.name);
        }

        /// <summary>Thrust acceleration (m/s^2) measured over about half a second of firing the left hand's thruster.</summary>
        IEnumerator MeasureThrust(float[] result)
        {
            var zeroG = m_Player.locomotion.zeroG;
            zeroG.Stop();
            float gainedBefore = zeroG.ThrusterSpeedGained;
            float firing = 0f;
            m_Player.leftHand.TriggerOverride = 1f;
            yield return null;
            while (firing < 0.5f)
            {
                yield return null;
                if (zeroG.IsThrusting) firing += Time.deltaTime;
            }
            m_Player.leftHand.TriggerOverride = 0f;
            yield return null;
            result[0] = (zeroG.ThrusterSpeedGained - gainedBefore) / firing;
            zeroG.Stop();
        }

        [UnityTest]
        public IEnumerator Thrust_IsAFixedForce_SoHaulingTheReactorCoreIsSluggish()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return FloatInCargoBay();
            var zeroG = m_Player.locomotion.zeroG;
            Assert.AreEqual(0f, zeroG.CarriedMass, 0.001f);
            var empty = new float[1];
            yield return MeasureThrust(empty);

            var core = m_Director.Items.Where(i => !i.IsMounted && i.definition != null).OrderByDescending(i => i.Body.mass).First();
            yield return FloatInCargoBay();
            yield return GrabWith(m_Player.rightHand, core);
            Assert.AreEqual(core.Body.mass, zeroG.CarriedMass, 0.001f, "What the hand holds is carried mass.");
            var loaded = new float[1];
            yield return MeasureThrust(loaded);

            float expectedRatio = zeroG.BodyMass / (zeroG.BodyMass + core.Body.mass);
            Assert.Greater(empty[0], 1f, "The thruster works with empty hands.");
            Assert.AreEqual(expectedRatio, loaded[0] / empty[0], 0.08f,
                "a = F / m: with the " + core.Body.mass + " kg core the thrust should give " + expectedRatio + " of the acceleration. Empty " + empty[0] + ", loaded " + loaded[0]);
        }

        [UnityTest]
        public IEnumerator ThrowRecoil_ConservesMomentum_InTheThrowersOwnFrame()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return FloatInCargoBay();
            var zeroG = m_Player.locomotion.zeroG;
            var item = Loose("elec_comms_relay");

            // Drifting along at 1 m/s and letting go of something that drifts with you: no kick at all.
            zeroG.AddImpulse(Vector3.right);
            item.RegisterThrow(zeroG.Velocity, m_Player);
            Assert.AreEqual(1f, zeroG.Velocity.x, 0.001f, "Letting go of something moving with you is not a throw.");
            Assert.AreEqual(0f, zeroG.Velocity.z, 0.001f);

            // Throwing it forward at 5 m/s relative to the body: m v = M dV.
            Vector3 before = zeroG.Velocity;
            item.RegisterThrow(before + Vector3.forward * 5f, m_Player);
            Vector3 kick = zeroG.Velocity - before;
            Assert.AreEqual(-5f * item.Body.mass / zeroG.TotalMass, kick.z, 0.005f, "Momentum is conserved.");
            Assert.AreEqual(0f, kick.x, 0.005f, "The sideways drift is not touched.");
            yield return null;
        }

        [UnityTest]
        public IEnumerator ShovingSomethingHeavy_PushesThePlayerBack_InProportionToItsMass()
        {
            m_Director.StartMission(PlayerId.Local);
            var chest = m_Player.bumpers[0];
            var light = m_Director.Items.Where(i => !i.IsMounted && i.definition != null).OrderBy(i => i.Body.mass).First();
            var heavy = m_Director.Items.Where(i => !i.IsMounted && i.definition != null).OrderByDescending(i => i.Body.mass).First();
            var lost = new float[2];
            for (int pass = 0; pass < 2; pass++)
            {
                var item = pass == 0 ? light : heavy;
                yield return FloatInCargoBay();
                var zeroG = m_Player.locomotion.zeroG;
                item.Body.linearVelocity = Vector3.zero;
                item.Body.angularVelocity = Vector3.zero;
                item.transform.position = chest.transform.position + Vector3.forward * 0.7f;
                item.Body.position = item.transform.position;
                Physics.SyncTransforms();
                zeroG.AddImpulse(Vector3.forward * 1.2f);
                float speedBefore = zeroG.Velocity.z;
                float pushedAt = -1f;
                for (int i = 0; i < 90; i++)
                {
                    yield return new WaitForFixedUpdate();
                    if (pushedAt < 0f && item.Body.linearVelocity.z > 0.2f) pushedAt = i;
                }
                Assert.GreaterOrEqual(pushedAt, 0f, item.name + " should have been shouldered aside.");
                Assert.Greater(item.Body.linearVelocity.z, 0.3f, item.name);
                // The drift's own (tiny) drag over 1.25 s is the same both times, so the difference is the shove.
                lost[pass] = speedBefore - zeroG.Velocity.z;
                light.Body.position = light.transform.position = new Vector3(0f, 0.2f, 0f) + Vector3.up * 50f;
                heavy.Body.position = heavy.transform.position = new Vector3(0f, 0.2f, 0f) + Vector3.up * 52f;
                light.Body.linearVelocity = heavy.Body.linearVelocity = Vector3.zero;
                Physics.SyncTransforms();
            }
            // Both runs also lose the suit's own slow drift decay (about 0.22 m/s over this time).
            float dragOnly = 1.2f * (1f - Mathf.Exp(-m_Player.locomotion.zeroG.drag * 90f * Time.fixedDeltaTime));
            Assert.Less(lost[0], dragOnly + 0.05f, "Nudging the light " + light.name + " barely slows you.");
            Assert.Greater(lost[1] - lost[0], 0.15f, "Shouldering the " + heavy.Body.mass + " kg " + heavy.name + " slows you right down.");
            Assert.Less(lost[1], 1.2f, "But you do not bounce back off it.");
        }
        // ------------------------------------------------------------------ weight in the hand

        void HandsUnderTestControl()
        {
            foreach (var driver in m_Player.GetComponentsInChildren<UnityEngine.InputSystem.XR.TrackedPoseDriver>()) driver.enabled = false;
        }

        IEnumerator LetGo(HandRig hand)
        {
            hand.interactor.selectInput.QueueManualState(false, 0f, false, true);
            hand.GripOverride = 0f;
            yield return null;
            yield return null;
            Assert.IsFalse(hand.IsHolding);
        }

        /// <summary>Moves the hand 0.3 m forward at about 1.5 m/s and returns how far the grip point trails it.</summary>
        IEnumerator SwingAndMeasureLag(HandRig hand, SalvageItem item, float[] gap)
        {
            var weight = item.GetComponent<HeldWeight>();
            Assert.IsNotNull(weight, item.name + " has weight in the hand.");
            for (int i = 0; i < 14; i++)
            {
                hand.transform.position += Vector3.forward * 0.0214f;
                yield return new WaitForFixedUpdate();
            }
            gap[0] = weight.GripGap;
            for (int i = 0; i < 108; i++) yield return new WaitForFixedUpdate();
            gap[1] = weight.GripGap;
        }

        [UnityTest]
        public IEnumerator LightThingsFollowTheHandExactly_HeavyThingsLagAndCatchUp()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return FloatInCargoBay();
            HandsUnderTestControl();
            var hand = m_Player.rightHand;
            var duck = m_Director.Items.First(i => i.definition != null && i.definition.id == "novelty_rubber_duck");
            var core = m_Director.Items.Where(i => !i.IsMounted && i.definition != null).OrderByDescending(i => i.Body.mass).First();
            HeldWeight.Follow(duck.Body.mass, out float duckScale, out float duckDamping);
            Assert.AreEqual(1f, duckScale, "Light things follow the hand exactly.");
            Assert.AreEqual(1f, duckDamping);
            HeldWeight.Follow(core.Body.mass, out float coreScale, out _);
            Assert.Less(coreScale, 0.5f);

            Vector3 home = hand.transform.position;
            yield return GrabWith(hand, duck);
            var duckGap = new float[2];
            yield return SwingAndMeasureLag(hand, duck, duckGap);
            yield return LetGo(hand);
            duck.Body.position = duck.transform.position = home + Vector3.up * 40f;
            duck.Body.linearVelocity = Vector3.zero;

            hand.transform.position = home;
            m_Player.locomotion.zeroG.Stop();
            yield return null;
            yield return GrabWith(hand, core);
            var coreGap = new float[2];
            yield return SwingAndMeasureLag(hand, core, coreGap);
            Assert.IsTrue(hand.IsHolding, "A brisk swing must not tear the core out of the hand.");
            yield return LetGo(hand);

            Assert.Less(duckGap[0], 0.06f, "The duck stays in the hand during the swing.");
            Assert.Greater(coreGap[0], duckGap[0] + 0.04f, "The " + core.Body.mass + " kg core trails the hand: duck " + duckGap[0] + ", core " + coreGap[0]);
            Assert.Less(coreGap[0], 0.4f, "But only a little.");
            Assert.Less(coreGap[1], 0.06f, "And catches up once the hand stops.");
        }

        [UnityTest]
        public IEnumerator AHeldObject_IsStoppedByWalls_AndTheGripSlipsWhenTheHandPullsTooFar()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return FloatInCargoBay();
            HandsUnderTestControl();
            var hand = m_Player.rightHand;
            var item = Loose("elec_comms_relay");
            yield return GrabWith(hand, item);
            var weight = item.GetComponent<HeldWeight>();
            int slips = 0;
            weight.Slipped += _ => slips++;

            // A plate on the Default layer: the item collides with it, the hand's solidity check does not see it.
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(wall.GetComponent<MeshRenderer>());
            wall.transform.localScale = new Vector3(0.8f, 0.8f, 0.04f);
            float wallZ = hand.interactor.transform.position.z + 0.3f;
            wall.transform.position = new Vector3(item.transform.position.x, item.transform.position.y, wallZ);
            Physics.SyncTransforms();

            float deepest = float.MinValue;
            for (int i = 0; i < 72 && hand.IsHolding; i++)
            {
                hand.transform.position += Vector3.forward * 0.014f; // 1 m/s, straight through the plate
                yield return new WaitForFixedUpdate();
                foreach (var c in item.GetComponentsInChildren<Collider>())
                    if (!c.isTrigger) deepest = Mathf.Max(deepest, c.bounds.max.z);
            }
            Object.Destroy(wall);
            Assert.Less(deepest, wallZ + 0.02f + 0.04f, "The held item must stop at the plate, not pass through it.");
            Assert.IsFalse(hand.IsHolding, "The grip should slip once the hand is pulled well past where the item is stuck.");
            Assert.AreEqual(1, slips);
            Assert.AreEqual(1, weight.SlipCount);
        }

        [UnityTest]
        public IEnumerator Throws_KeepTheTrueReleaseVelocity_UpToWhatAnArmCanGiveThatMass()
        {
            Assert.Greater(RealPhysics.MaxThrowSpeed(0.1f), 13f);
            Assert.Less(RealPhysics.MaxThrowSpeed(40f), 5f);
            Assert.Greater(RealPhysics.MaxThrowSpeed(3f), RealPhysics.MaxThrowSpeed(6f));

            m_Director.StartMission(PlayerId.Local);
            yield return FloatInCargoBay();
            HandsUnderTestControl();
            var hand = m_Player.rightHand;
            var duck = m_Director.Items.First(i => i.definition != null && i.definition.id == "novelty_rubber_duck");
            var core = m_Director.Items.Where(i => !i.IsMounted && i.definition != null).OrderByDescending(i => i.Body.mass).First();
            var results = new Vector3[2];
            var spins = new float[2];
            var throwDirs = new Vector3[2];
            for (int pass = 0; pass < 2; pass++)
            {
                var item = pass == 0 ? duck : core;
                yield return FloatInCargoBay();
                yield return GrabWith(hand, item);
                yield return LetGo(hand);
                // A wild flick, as if the hand had been moving this fast as it let go.
                Vector3 thrower = m_Player.locomotion.zeroG.Velocity;
                Vector3 open = OpenDirection(item.transform.position, out _);
                throwDirs[pass] = open;
                item.Body.linearVelocity = thrower + open * 10f;
                item.Body.angularVelocity = Vector3.up * 30f;
                yield return new WaitForFixedUpdate();
                results[pass] = item.Body.linearVelocity - thrower;
                spins[pass] = item.Body.angularVelocity.magnitude;
                item.Body.linearVelocity = Vector3.zero;
                item.Body.angularVelocity = Vector3.zero;
                item.Body.position = item.transform.position = hand.transform.position + Vector3.up * (40f + pass * 3f);
                Physics.SyncTransforms();
            }
            Assert.AreEqual(10f, results[0].magnitude, 0.3f, "The duck leaves with the hand's true speed: " + results[0]);
            Assert.AreEqual(RealPhysics.MaxThrowSpeed(core.Body.mass), results[1].magnitude, 0.3f, "Nobody can hurl a " + core.Body.mass + " kg core at 10 m/s: " + results[1]);
            Assert.Greater(Vector3.Dot(results[1].normalized, throwDirs[1]), 0.95f, "The direction of the throw is kept.");
            Assert.Greater(spins[0], 20f, "A light thing keeps a fast flick's spin.");
            Assert.Less(spins[1], 10f, "A heavy one cannot be spun up like that by a wrist.");
        }

        // ------------------------------------------------------------------ contacts that read as real

        /// <summary>Sends the item into the nearest-open wall at this speed and returns the loudest impact noise it made (0 = none).</summary>
        IEnumerator KnockIntoAWall(SalvageItem item, float speed, float[] loudest)
        {
            var emitter = item.GetComponent<ImpactNoiseEmitter>();
            // From the middle of the cargo bay (nobody is there) toward its nearest wall.
            Vector3 from = new Vector3(0f, 0.9f, 9.2f);
            Vector3 dir = Vector3.forward;
            float clear = 100f;
            foreach (var d in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right, Vector3.up, Vector3.down })
            {
                if (Physics.SphereCast(from, 0.3f, d, out RaycastHit hit, 30f, ~0, QueryTriggerInteraction.Ignore) && hit.distance > 1.1f && hit.distance < clear)
                {
                    clear = hit.distance;
                    dir = d;
                }
            }
            Assume.That(clear, Is.InRange(1.1f, 29f));
            Vector3 near = from + dir * (clear - 0.8f);
            item.Body.position = near;
            item.transform.position = near;
            item.Body.angularVelocity = Vector3.zero;
            Physics.SyncTransforms();
            float heard = 0f;
            System.Action<NoiseEvent> listen = n => { if (n.Kind == NoiseKind.Impact) heard = Mathf.Max(heard, n.Loudness); };
            m_Director.noise.NoiseEmitted += listen;
            yield return new WaitForSeconds(1.1f); // past the emitter's settle-in time after spawning
            item.Body.linearVelocity = dir * speed;
            for (int i = 0; i < 60; i++) yield return new WaitForFixedUpdate();
            m_Director.noise.NoiseEmitted -= listen;
            loudest[0] = heard;
            loudest[1] = emitter.LastImpactStrength;
        }

        [UnityTest]
        public IEnumerator Knocks_AreAsLoudAsTheirImpulse_AndTheirMaterial()
        {
            Assert.Greater(RealPhysics.ImpactStrength(3f, 40f * 1.3f * 3f, 40f, SurfaceKind.PaintedMetal), RealPhysics.ImpactStrength(3f, 3.5f * 1.3f * 3f, 3.5f, SurfaceKind.PaintedMetal) * 1.5f);
            Assert.Less(RealPhysics.ImpactStrength(3f, 0.1f * 1.6f * 3f, 0.1f, SurfaceKind.RubberPlastic), 1f);

            m_Director.StartMission(PlayerId.Local);
            yield return Steps(2);
            var duck = m_Director.Items.First(i => i.definition != null && i.definition.id == "novelty_rubber_duck");
            var core = m_Director.Items.Where(i => !i.IsMounted && i.definition != null).OrderByDescending(i => i.Body.mass).First();
            var duckNoise = new float[2];
            var coreNoise = new float[2];
            // The same 3 m/s knock: past the old speed-only threshold for both.
            yield return KnockIntoAWall(duck, 3f, duckNoise);
            yield return KnockIntoAWall(core, 3f, coreNoise);
            Assert.Greater(coreNoise[1], duckNoise[1] * 3f, "The core hits far harder than the duck: " + coreNoise[1] + " vs " + duckNoise[1]);
            Assert.Greater(coreNoise[0], 0f, "The creature hears the reactor core bang into the wall.");
            Assert.AreEqual(0f, duckNoise[0], "A rubber duck bumping a wall is not a gameplay noise.");
        }

        [UnityTest]
        public IEnumerator AClosingDoor_EasesLooseThingsOutOfTheDoorway_InsteadOfTrappingThem()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Steps(2);
            var door = Object.FindObjectsByType<SlidingDoor>(FindObjectsSortMode.None)
                .Where(d => !d.locked && d.leaf != null)
                // Away from the creature: its body now shoves loose things too, which is not what this tests.
                .Where(d => m_Director.ActiveEnemy == null || Vector3.Distance(d.transform.position, CreatureBody(m_Director.ActiveEnemy).bounds.center) > 4f)
                .OrderByDescending(d => Vector3.Distance(d.transform.position, m_Player.transform.position)).First();
            door.SetOpen(true, true);
            yield return Steps(2);
            var item = Loose("elec_comms_relay");
            Vector3 center = door.transform.position + Vector3.up * 0.6f + door.transform.forward * 0.03f;
            item.Body.linearVelocity = Vector3.zero;
            item.Body.angularVelocity = Vector3.zero;
            item.Body.position = center;
            item.transform.position = center;
            Physics.SyncTransforms();
            yield return Steps(2);

            door.SetOpen(false);
            Assert.IsTrue(door.IsOpen, "The door waits while something is floating in its way.");
            for (float t = 0f; t < 4f && door.IsOpen; t += Time.deltaTime) yield return null;
            Assert.Greater(door.ObjectsCleared, 0);
            Assert.IsFalse(door.IsOpen, "Once the doorway is clear the door shuts.");
            for (float t = 0f; t < door.duration + 0.5f; t += Time.deltaTime) yield return null;
            float off = Mathf.Abs(Vector3.Dot(item.transform.position - door.transform.position, door.transform.forward));
            Assert.Greater(off, door.doorwaySize.z * 0.5f, "The item was eased out of the doorway, to the side it was on.");
            Assert.Greater(Vector3.Dot(item.transform.position - door.transform.position, door.transform.forward), 0f);
            foreach (var c in item.GetComponentsInChildren<Collider>())
                foreach (var leafCollider in door.leaf.GetComponentsInChildren<Collider>())
                    if (!c.isTrigger && !leafCollider.isTrigger)
                        Assert.IsFalse(Physics.ComputePenetration(c, c.transform.position, c.transform.rotation, leafCollider, leafCollider.transform.position, leafCollider.transform.rotation, out _, out _),
                            "Nothing is left stuck inside the shut door.");
        }
        // ------------------------------------------------------------------ two hands on a heavy object

        /// <summary>Puts this hand on the item where it is (the item does not move) and closes it.</summary>
        IEnumerator AlsoGrabWith(HandRig hand, SalvageItem item, Vector3 offset)
        {
            hand.transform.position += item.transform.position + offset - hand.interactor.transform.position;
            Physics.SyncTransforms();
            yield return new WaitForFixedUpdate();
            hand.GripOverride = 1f;
            var select = hand.interactor.selectInput;
            select.inputSourceMode = UnityEngine.XR.Interaction.Toolkit.Inputs.Readers.XRInputButtonReader.InputSourceMode.ManualValue;
            select.QueueManualState(true, 1f, true, false);
            for (int i = 0; i < 4; i++) yield return null;
        }

        [UnityTest]
        public IEnumerator TwoHandsOnTheCore_CarryItWithLessLag_ThrowHarder_AndOneCanLetGo()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return FloatInCargoBay();
            HandsUnderTestControl();
            var right = m_Player.rightHand;
            var left = m_Player.leftHand;
            var core = m_Director.Items.Where(i => !i.IsMounted && i.definition != null).OrderByDescending(i => i.Body.mass).First();
            var weight = core.GetComponent<HeldWeight>();
            var duck = m_Director.Items.First(i => i.definition != null && i.definition.id == "novelty_rubber_duck");
            Assert.AreEqual(UnityEngine.XR.Interaction.Toolkit.Interactables.InteractableSelectMode.Multiple, core.grab.selectMode, "Heavy things take a second hand.");
            Assert.AreEqual(UnityEngine.XR.Interaction.Toolkit.Interactables.InteractableSelectMode.Single, duck.grab.selectMode, "Light things are unchanged.");

            // One hand: the baseline lag.
            Vector3 rightHome = right.transform.position;
            yield return GrabWith(right, core);
            var oneHand = new float[2];
            yield return SwingAndMeasureLag(right, core, oneHand);
            float oneScale = core.grab.velocityScale;

            // The other hand takes hold too: both hold it, and the pair steers it much more firmly.
            yield return AlsoGrabWith(left, core, Vector3.up * 0.12f);
            Assert.IsTrue(right.IsHolding && left.IsHolding, "Both hands hold the core.");
            Assert.AreEqual(2, weight.HandCount);
            Assert.Greater(core.grab.velocityScale, oneScale * 1.5f, "Two hands steer it harder than one.");
            for (int i = 0; i < 72; i++) yield return new WaitForFixedUpdate();
            var twoHands = new float[2];
            // Swing both hands together.
            for (int i = 0; i < 14; i++)
            {
                right.transform.position += Vector3.forward * 0.0214f;
                left.transform.position += Vector3.forward * 0.0214f;
                yield return new WaitForFixedUpdate();
            }
            twoHands[0] = weight.GripGap;
            Assert.Less(twoHands[0], oneHand[0] * 0.8f, "Carried in two hands it lags less: one hand " + oneHand[0] + ", two " + twoHands[0]);

            // Letting go with one hand: still held by the other, back to the one-handed feel.
            left.interactor.selectInput.QueueManualState(false, 0f, false, true);
            left.GripOverride = 0f;
            yield return null;
            yield return null;
            Assert.IsFalse(left.IsHolding);
            Assert.IsTrue(right.IsHolding, "The other hand keeps hold of it.");
            Assert.IsTrue(core.IsHeld);
            Assert.AreEqual(1, weight.HandCount);
            Assert.AreEqual(oneScale, core.grab.velocityScale, 1e-4f, "Back to one-handed behaviour.");
            Assert.IsFalse(core.Body.isKinematic);
            yield return LetGo(right);

            // The throw cap with two arms is that of one arm throwing half the mass.
            Assert.Greater(RealPhysics.MaxThrowSpeed(core.Body.mass / 2f), RealPhysics.MaxThrowSpeed(core.Body.mass) * 1.3f);
        }
        // ------------------------------------------------------------------ the spider is a physical body

        static Collider CreatureBody(EnemyDrone creature) =>
            creature.GetComponentsInChildren<SphereCollider>().First(c => c.gameObject.layer == GameLayers.Enemy && !c.isTrigger);

        /// <summary>A horizontal direction from p with at least this much room, ignoring the creature itself.</summary>
        static bool RoomAround(Vector3 p, float room, out Vector3 dir)
        {
            int mask = ~(1 << GameLayers.Enemy);
            foreach (var d in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right })
            {
                if (!Physics.SphereCast(p, 0.25f, d, out _, room, mask, QueryTriggerInteraction.Ignore)) { dir = d; return true; }
            }
            dir = Vector3.forward;
            return false;
        }

        /// <summary>Throws the item squarely at the creature's body from 1 m away and waits for the hit.</summary>
        IEnumerator ThrowAtCreature(EnemyDrone creature, Rigidbody item, float speed)
        {
            var body = CreatureBody(creature);
            Vector3 c = body.bounds.center;
            Assume.That(RoomAround(c, 1.6f, out Vector3 dir), "Need open space beside the creature.");
            Vector3 start = c + dir * 1.1f;
            item.position = start;
            item.transform.position = start;
            item.rotation = Quaternion.identity;
            item.transform.rotation = Quaternion.identity;
            item.angularVelocity = Vector3.zero;
            item.linearVelocity = -dir * speed;
            Physics.SyncTransforms();
            // Keep it aimed at the body (which may still be settling on its surface) until it hits.
            var emitter = item.GetComponent<ImpactNoiseEmitter>();
            int hitsBefore = emitter.CreatureHits;
            for (int i = 0; i < 72 && emitter.CreatureHits == hitsBefore; i++)
            {
                Vector3 to = body.bounds.center - item.worldCenterOfMass;
                if (to.magnitude > 0.5f) item.linearVelocity = to.normalized * speed;
                yield return new WaitForFixedUpdate();
            }
            for (int i = 0; i < 4; i++) yield return new WaitForFixedUpdate();
            item.linearVelocity = Vector3.zero;
            item.position = item.transform.position = start + Vector3.up * 30f;
            Physics.SyncTransforms();
        }

        IEnumerator AwakeCreature(EnemyDrone[] result)
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Steps(2);
            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            // Out in the open middle of the cargo bay, standing on the floor (hazards are paused, so it stays).
            creature.transform.SetPositionAndRotation(new Vector3(0f, creature.hoverHeight, 9.2f), Quaternion.identity);
            Physics.SyncTransforms();
            for (float t = 0f; t < 1.5f; t += Time.deltaTime) yield return null; // let the body settle on its surface
            result[0] = creature;
        }

        [UnityTest]
        public IEnumerator AHeavyThrow_StaggersTheSpider_ACanDoesNot_AndItCannotBeStunLocked()
        {
            var found = new EnemyDrone[1];
            yield return AwakeCreature(found);
            var creature = found[0];
            Assert.AreNotEqual(EnemyState.Dormant, creature.State);
            var can = Object.FindObjectsByType<ImpactNoiseEmitter>(FindObjectsSortMode.None)
                .First(e => e.GetComponent<SalvageItem>() == null && e.name.Contains("Can") && !e.GetComponent<Rigidbody>().isKinematic);
            var core = m_Director.Items.Where(i => !i.IsMounted && i.definition != null).OrderByDescending(i => i.Body.mass).First();
            Assert.AreEqual(0f, EnemyDrone.StaggerSecondsFor(0.35f * 8f), "A tossed can is far below the threshold.");

            // A can thrown hard does nothing.
            yield return ThrowAtCreature(creature, can.GetComponent<Rigidbody>(), 8f);
            Assert.Greater(can.LastCreatureHitMomentum, 0.5f, "Test set-up: the can should have hit the creature at " + CreatureBody(creature).bounds.center + "; can at " + can.transform.position);
            Assert.AreEqual(0, creature.StaggerCount, "A 0.35 kg can does not stagger it.");

            // The 40 kg core at 4 m/s: 160 kg m/s.
            yield return ThrowAtCreature(creature, core.Body, 4f);
            var emitter = core.GetComponent<ImpactNoiseEmitter>();
            Assert.Greater(emitter.LastCreatureHitMomentum, 100f, "Test set-up: the core should have hit it squarely.");
            Assert.AreEqual(1, creature.StaggerCount, "The core staggers it.");
            Assert.IsTrue(creature.IsStaggered);

            // While staggered it does not advance.
            Vector3 at = creature.transform.position;
            for (int i = 0; i < 8; i++) creature.Step(0.05f);
            Assert.Less(Vector3.Distance(at, creature.transform.position), 0.001f, "Reeling, it does not move.");
            Assert.AreNotEqual(EnemyState.Dormant, creature.State, "Its mind is untouched.");
            for (int i = 0; i < 30; i++) creature.Step(0.05f);
            Assert.IsFalse(creature.IsStaggered, "The stagger is brief.");

            // Another heavy hit inside the cooldown does nothing.
            yield return ThrowAtCreature(creature, core.Body, 4f);
            Assert.AreEqual(1, creature.StaggerCount, "It cannot be stun-locked.");
            Assert.IsFalse(creature.Stagger(1f));
            // After the cooldown it can be staggered again; never while asleep.
            for (int i = 0; i < 140; i++) creature.Step(0.05f);
            creature.Tranquilize(5f);
            Assert.IsFalse(creature.Stagger(1f), "Not while sedated.");
        }

        [UnityTest]
        public IEnumerator TheSpider_CannotBeStaggered_DuringAJumpscare()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Steps(2);
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 19.6f));
            for (int i = 0; i < 4; i++) yield return null;
            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            Physics.SyncTransforms();
            Vector3 head = m_Player.HeadPosition;
            creature.transform.position = new Vector3(head.x, creature.hoverHeight, head.z + 1.6f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.back);
            for (int i = 0; i < 200 && !creature.IsScaring && m_Director.Phase == MissionPhase.Active; i++) creature.Step(0.05f);
            Assume.That(creature.IsScaring, "Test set-up: it should have caught the player.");
            Assert.IsFalse(creature.Stagger(1f), "Nothing interrupts a jumpscare.");
            Assert.IsFalse(creature.IsStaggered);
            Assert.AreEqual(0, creature.StaggerCount);
        }

        [UnityTest]
        public IEnumerator TheSpider_KnocksAFloatingCrateAside_InsteadOfGhostingThroughIt()
        {
            var found = new EnemyDrone[1];
            yield return AwakeCreature(found);
            var creature = found[0];
            var crate = Object.FindObjectsByType<ImpactNoiseEmitter>(FindObjectsSortMode.None)
                .First(e => e.GetComponent<SalvageItem>() == null && e.name.Contains("Crate")).GetComponent<Rigidbody>();
            Vector3 c = CreatureBody(creature).bounds.center;
            Assume.That(RoomAround(c, 3f, out Vector3 dir), "Need a clear run for the creature.");
            Vector3 at = c + dir * 1.3f;
            crate.position = crate.transform.position = at;
            crate.linearVelocity = Vector3.zero;
            crate.angularVelocity = Vector3.zero;
            Physics.SyncTransforms();
            yield return Steps(2);

            // Walk it straight through where the crate floats, at 2 m/s.
            for (int i = 0; i < 90; i++)
            {
                creature.transform.position += dir * (2f * Time.fixedDeltaTime);
                yield return new WaitForFixedUpdate();
            }
            var pusher = creature.GetComponentInChildren<Bumper>();
            Assert.IsNotNull(pusher, "The creature has a body pusher.");
            Assert.Greater(pusher.PushCount, 0, "It pushed the crate. Body moved from " + c + " to " + CreatureBody(creature).bounds.center + " along " + dir);
            Assert.Greater(Vector3.Dot(crate.linearVelocity, dir), 0.8f, "The crate is knocked ahead of it: " + crate.linearVelocity + " body now " + CreatureBody(creature).bounds.center + " crate " + crate.position);
            Assert.Greater(Vector3.Dot(crate.position - at, dir), 0.5f);
        }
    }
}

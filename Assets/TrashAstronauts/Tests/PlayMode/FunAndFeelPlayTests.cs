using System.Collections;
using System.Collections.Generic;
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
    /// <summary>Random creatures, the suit thruster and brake, trick shots, throw recoil and the rubber duck.</summary>
    public class FunAndFeelPlayTests
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
        }

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        /// <summary>Puts the player in open space at the near end of the long corridor, at rest, facing down it.</summary>
        IEnumerator FloatInCargoBay()
        {
            m_Director.keepPlayersInHub = false; // this test floats in the wreck without starting a contract
            m_Player.locomotion.TeleportTo(new Vector3(0f, 0.2f, 9.2f), Quaternion.identity);
            yield return Frames(3);
            m_Player.locomotion.zeroG.Stop();
            foreach (var hand in new[] { m_Player.leftHand, m_Player.rightHand })
            {
                hand.GripOverride = 0f;
                hand.TriggerOverride = 0f;
                hand.ThumbstickClickOverride = false;
            }
            yield return Frames(2);
        }

        [UnityTest]
        public IEnumerator EachContract_PutsOneCreatureAboard_ChosenAtRandom()
        {
            Assert.AreEqual(4, m_Director.enemies.Length);
            Assert.AreEqual(1, m_Director.enemies.Count(e => e.gameObject.activeSelf), "In the hub only one creature exists.");

            var seen = new HashSet<string>();
            for (int seed = 1; seed <= 40; seed++)
            {
                MissionDirector.SeedOverride = seed;
                if (m_Director.Phase == MissionPhase.Active) m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
                Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
                yield return null;
                Assert.AreEqual(1, m_Director.enemies.Count(e => e.gameObject.activeSelf), "Exactly one creature per contract.");
                Assert.IsTrue(m_Director.ActiveEnemy.gameObject.activeSelf);
                Assert.IsTrue(m_Director.ActiveEnemy.IsRunning);
                Assert.Less(Vector3.Distance(m_Director.ActiveEnemy.transform.position, m_Director.ActiveEnemy.dock.position), 0.1f);
                seen.Add(m_Director.ActiveEnemy.displayName);
            }
            Assert.AreEqual(4, seen.Count, "Over 40 contracts every creature should turn up: " + string.Join(", ", seen));

            // The same seed always brings the same creature (so a run can be reproduced).
            MissionDirector.SeedOverride = 7;
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            m_Director.StartMission(PlayerId.Local);
            string first = m_Director.ActiveEnemy.displayName;
            MissionDirector.SeedOverride = 7;
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            m_Director.StartMission(PlayerId.Local);
            Assert.AreEqual(first, m_Director.ActiveEnemy.displayName);
        }

        [UnityTest]
        public IEnumerator EveryCreature_Wakes_Hunts_AndUsesItsOwnSenses()
        {
            for (int index = 0; index < 4; index++)
            {
                MissionDirector.CreatureOverride = index;
                if (m_Director.Phase == MissionPhase.Active) m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
                m_Director.StartMission(PlayerId.Local);
                yield return Frames(2);
                var creature = m_Director.ActiveEnemy;
                Assert.AreSame(m_Director.enemies[index], creature);
                Assert.AreEqual(EnemyState.Dormant, creature.State);
                creature.WakeNow();
                Assert.AreEqual(EnemyState.Patrol, creature.State, creature.displayName);
                foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
                Physics.SyncTransforms();

                Vector3 start = creature.transform.position;
                for (int i = 0; i < 200; i++) creature.Step(0.05f);
                Assert.Greater(Vector3.Distance(start, creature.transform.position), 1f, creature.displayName + " should patrol.");

                // A loud noise 12 m away: the half-deaf Warden (index 2) does not hear it, the others do.
                Vector3 noiseAt = creature.transform.position + Vector3.forward * 0.1f;
                noiseAt = new Vector3(0.3f, 1.2f, creature.transform.position.z > 18f ? creature.transform.position.z - 12f : creature.transform.position.z + 12f);
                m_Director.noise.Emit(new NoiseEvent(noiseAt, 0.7f, NoiseKind.Impact, PlayerId.Local));
                float distance = Vector3.Distance(creature.transform.position, noiseAt);
                float radius = (creature.overrideTuning ? creature.tuning : m_Director.Config.enemy).hearingRadiusAtFullLoudness * 0.7f;
                Assert.AreEqual(distance <= radius ? EnemyState.Investigate : EnemyState.Patrol, creature.State,
                    creature.displayName + " at " + distance.ToString("0.0") + " m with a hearing radius of " + radius.ToString("0.0") + " m");
                if (index == 2) Assert.AreEqual(EnemyState.Patrol, creature.State, "The Warden should not have heard that.");
            }
        }

        [UnityTest]
        public IEnumerator Thruster_PushesWhereTheHandPoints_AndTheBrakeStopsYou()
        {
            yield return FloatInCargoBay();
            var zeroG = m_Player.locomotion.zeroG;
            var hand = m_Player.rightHand;
            Assert.Less(zeroG.Velocity.magnitude, 0.05f);

            Vector3 start = m_Player.transform.position;
            hand.TriggerOverride = 1f;
            for (float t = 0f; t < 1f; t += Time.deltaTime) yield return null;
            Assert.IsTrue(zeroG.IsThrusting);
            Assert.IsTrue(hand.thrusterVisual.activeSelf, "The thruster flame should show while firing.");
            Vector3 pointing = hand.transform.forward;
            Assert.Greater(Vector3.Dot(zeroG.Velocity, pointing), 0.8f, "A second of thrust should get you moving the way the hand points.");
            Assert.Greater(Vector3.Dot(m_Player.transform.position - start, pointing), 0.3f);
            Assert.Greater(zeroG.Exertion, 0.3f);

            // It tops out rather than accelerating forever.
            for (float t = 0f; t < 1.2f; t += Time.deltaTime) yield return null;
            Assert.LessOrEqual(Vector3.Dot(zeroG.Velocity, pointing), zeroG.thrusterMaxSpeed + 0.05f);
            Assert.Greater(Vector3.Dot(zeroG.Velocity, pointing), zeroG.thrusterMaxSpeed - 0.4f, "Diagnose: at " + m_Player.transform.position + " velocity " + zeroG.Velocity);

            hand.TriggerOverride = 0f;
            yield return Frames(2);
            Assert.IsFalse(zeroG.IsThrusting);
            Assert.IsFalse(hand.thrusterVisual.activeSelf);
            Assert.Greater(zeroG.Velocity.magnitude, 0.5f, "You keep drifting after letting go.");

            m_Player.leftHand.ThumbstickClickOverride = true;
            for (float t = 0f; t < 1.2f; t += Time.deltaTime) yield return null;
            Assert.IsTrue(zeroG.IsBraking);
            Assert.Less(zeroG.Velocity.magnitude, 0.05f, "Clicking a thumbstick should bring you to a stop.");
        }

        [UnityTest]
        public IEnumerator Thruster_DoesNotFire_WhileHoldingSomething()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return FloatInCargoBay();
            var hand = m_Player.rightHand;
            var item = m_Director.Items.First(i => !i.IsMounted && i.definition != null && !i.definition.bulky);
            item.transform.position = hand.interactor.transform.position;
            Physics.SyncTransforms();
            hand.GripOverride = 1f;
            var select = hand.interactor.selectInput;
            select.inputSourceMode = UnityEngine.XR.Interaction.Toolkit.Inputs.Readers.XRInputButtonReader.InputSourceMode.ManualValue;
            select.QueueManualState(true, 1f, true, false);
            yield return Frames(4);
            Assert.IsTrue(hand.IsHolding, "The hand should have picked the item up.");
            m_Player.locomotion.zeroG.Stop();
            hand.TriggerOverride = 1f;
            for (float t = 0f; t < 0.5f; t += Time.deltaTime) yield return null;
            Assert.IsFalse(m_Player.locomotion.zeroG.IsThrusting, "A hand that is holding something uses its trigger for the item.");
            Assert.Less(m_Player.locomotion.zeroG.ThrusterSpeedGained, 0.01f);
        }

        [UnityTest]
        public IEnumerator ThrowingSalvageIn_FromAcrossTheCabin_IsATrickShot()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            var locker = m_Director.locker;
            var items = m_Director.Items.Where(i => !i.IsMounted && i.definition != null && !i.definition.bonusSpawn).Take(3).ToArray();
            Assert.AreEqual(3, items.Length);
            float far = m_Director.Config.trickShotDistance + 0.5f;

            // Thrown from far away.
            items[0].RegisterThrow(Vector3.forward * 3f, null, locker.zone.position + Vector3.back * far);
            Assert.IsTrue(locker.TryAccept(items[0]));
            int expected = Mathf.RoundToInt(items[0].Value * m_Director.Config.trickShotBonusFraction);
            Assert.AreEqual(1, m_Director.Session.TrickShots);
            Assert.AreEqual(expected, m_Director.Session.TrickBonus);
            Assert.AreEqual(items[0].Value + expected, m_Director.Session.SecuredValue);
            Assert.AreEqual(1, locker.TrickShotCount);

            // Thrown from right next to the locker: no bonus.
            items[1].RegisterThrow(Vector3.forward * 3f, null, locker.zone.position + Vector3.back * 0.5f);
            Assert.IsTrue(locker.TryAccept(items[1]));
            // Carried over and let go gently from far away (it drifted in): no bonus either.
            items[2].RegisterThrow(Vector3.forward * 0.2f, null, locker.zone.position + Vector3.back * far);
            Assert.IsTrue(locker.TryAccept(items[2]));
            Assert.AreEqual(1, m_Director.Session.TrickShots);

            var result = m_Director.Extract(PlayerId.Local);
            Assert.AreEqual(1, result.TrickShots);
            Assert.AreEqual(expected, result.TrickBonus);
        }

        [UnityTest]
        public IEnumerator ThrowingSomethingHeavy_PushesYouBackTheOtherWay()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return FloatInCargoBay();
            var zeroG = m_Player.locomotion.zeroG;
            var heavy = m_Director.Items.OrderByDescending(i => i.Body.mass).First();
            var light = m_Director.Items.Where(i => i.definition != null).OrderBy(i => i.Body.mass).First();
            Assert.Greater(heavy.Body.mass, 5f);

            light.RegisterThrow(Vector3.forward * 4f, m_Player);
            float lightKick = zeroG.Velocity.magnitude;
            zeroG.Stop();
            heavy.RegisterThrow(Vector3.forward * 4f, m_Player);
            float heavyKick = zeroG.Velocity.magnitude;

            Assert.Less(Vector3.Dot(zeroG.Velocity.normalized, Vector3.forward), -0.95f, "Recoil is opposite to the throw.");
            Assert.AreEqual(4f * heavy.Body.mass / m_Director.Config.playerMass, heavyKick, 0.02f, "Momentum is conserved.");
            Assert.Greater(heavyKick, lightKick * 5f, "A heavy item kicks far harder than a light one.");
            Assert.LessOrEqual(heavyKick, 1.2f);

            zeroG.Stop();
            heavy.RegisterThrow(Vector3.forward * 0.3f, m_Player);
            Assert.Less(zeroG.Velocity.magnitude, 0.001f, "Gently letting go is not a throw.");
            yield return null;
        }

        [UnityTest]
        public IEnumerator RubberDuck_IsHiddenEveryRun_Squeaks_AndDistractsTheCreature()
        {
            MissionDirector.CreatureOverride = 0;
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            var ducks = m_Director.Items.Where(i => i.definition != null && i.definition.bonusSpawn).ToArray();
            Assert.AreEqual(1, ducks.Length, "Exactly one hidden bonus item per run.");
            var duck = ducks[0];
            Assert.IsFalse(duck.IsMounted);
            Assert.AreEqual(0, m_Director.Items.Count(i => i != duck && Vector3.Distance(i.transform.position, duck.transform.position) < 0.05f),
                "The duck must not share a spawn point with real salvage.");
            Assert.LessOrEqual(m_Director.Session.Quota, m_Director.Items.Where(i => i != duck).Sum(i => i.Value), "The quota never depends on finding the duck.");

            var toy = duck.GetComponent<SqueakyToy>();
            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            duck.transform.position = creature.transform.position + new Vector3(-4f, -0.2f, -7f); // a few metres from the creature
            Assert.IsTrue(toy.Squeak());
            Assert.IsFalse(toy.Squeak(), "It needs a moment to re-inflate.");
            Assert.AreEqual(1, toy.SqueakCount);
            Assert.AreEqual(EnemyState.Investigate, creature.State, "The squeak should draw the creature.");

            // It is still worth something at the locker.
            Assert.IsTrue(m_Director.locker.TryAccept(duck));
            Assert.AreEqual(duck.Value, m_Director.Session.DepositedValue);
        }
    }
}

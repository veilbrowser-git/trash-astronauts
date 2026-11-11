using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.UI;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Zero-g movement and the guided tutorial, exercised in the real scene. Hand motion is simulated by
    /// moving the hand transforms directly (the tracked pose drivers are switched off for the test).
    /// </summary>
    public class ZeroGAndTutorialPlayTests
    {
        MemorySaveStorage m_Storage;
        MissionDirector m_Director;
        PlayerContext m_Player;
        ZeroGLocomotion m_ZeroG;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            m_Storage = new MemorySaveStorage();
            MissionDirector.StorageOverride = m_Storage;
            MissionDirector.SeedOverride = 77;
            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            yield return null;
            yield return null;
            m_Director = MissionDirector.Active;
            m_Player = m_Director.Players.First();
            m_ZeroG = m_Player.locomotion.zeroG;
            Assert.IsNotNull(m_ZeroG, "The rig has no zero-g locomotion.");
            foreach (var driver in m_Player.GetComponentsInChildren<TrackedPoseDriver>()) driver.enabled = false;
            // Park both hands in open space: away from every surface and well clear of the suit's
            // holsters (gripping next to a holster draws the tool instead of paddling).
            m_Player.leftHand.transform.position = OpenSpace(-0.6f);
            m_Player.rightHand.transform.position = OpenSpace(0.6f);
            yield return null;
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            MissionDirector.StorageOverride = null;
            MissionDirector.SeedOverride = null;
        }

        /// <summary>A point beside the player with nothing grabbable within reach.</summary>
        Vector3 OpenSpace(float side) => m_Player.HeadPosition + new Vector3(0f, -0.3f, side);

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        void MoveHandWorld(HandRig hand, Vector3 worldDelta)
        {
            hand.transform.localPosition += hand.transform.parent.InverseTransformVector(worldDelta);
        }

        [UnityTest]
        public IEnumerator Drifting_KeepsMomentum_AndIsStoppedByWalls()
        {
            Vector3 start = m_Player.transform.position;
            m_ZeroG.AddImpulse(Vector3.forward * 1.2f);
            yield return new WaitForSeconds(0.5f);

            float moved = m_Player.transform.position.z - start.z;
            Assert.Greater(moved, 0.3f, "The player should keep gliding after the push.");
            Assert.Greater(m_ZeroG.Speed, 0.8f, "Drift should only decay slowly.");

            // Keep going: the closed hatch and the hull must stop the player inside the tug.
            m_ZeroG.AddImpulse(Vector3.forward * 3f);
            yield return new WaitForSeconds(2.5f);
            Assert.IsTrue(m_Director.safeZone.Contains(m_Player.HeadPosition), "The player drifted through the hull.");
            Assert.Less(m_Player.transform.position.z, 1.95f);
            Assert.Less(m_ZeroG.Speed, 0.7f, "Hitting the hull should take away nearly all of a 3 m/s drift (a small bounce is intended).");
        }

        [UnityTest]
        public IEnumerator GrabbingAWall_PullsTheBody_AndLettingGoPushesOff()
        {
            var hand = m_Player.rightHand;
            Assert.IsFalse(m_ZeroG.TryGrabSurface(hand), "There is nothing to grab in open space.");

            // Put the palm against the tug's west wall (inner face at x = -1.9).
            hand.transform.position = new Vector3(-1.8f, 1.2f, -0.4f);
            yield return null;
            hand.GripOverride = 1f;
            Assert.IsTrue(m_ZeroG.TryGrabSurface(hand), "A hand next to a wall should be able to grab it.");
            Assert.IsTrue(m_ZeroG.IsAnchored);
            Assert.IsTrue(hand.IsAnchored);

            // Shove toward the wall: the body is pushed away from it, one-to-one.
            Vector3 start = m_Player.transform.position;
            for (int i = 0; i < 12; i++)
            {
                MoveHandWorld(hand, Vector3.left * 0.02f);
                yield return null;
            }
            float pushed = m_Player.transform.position.x - start.x;
            Assert.Greater(pushed, 0.15f, "Pushing against the wall should move the body away from it.");
            Assert.AreEqual(0f, m_ZeroG.Speed, 0.001f, "While holding on there is no drift.");

            float pushOffSpeed = -1f;
            m_ZeroG.PushedOff += speed => pushOffSpeed = speed;
            hand.GripOverride = 0f;
            m_ZeroG.ReleaseSurface(hand);
            Assert.IsFalse(m_ZeroG.IsAnchored);
            Assert.Greater(m_ZeroG.Velocity.x, 0.2f, "Letting go mid-shove should send the player gliding away from the wall.");
            Assert.Greater(pushOffSpeed, 0.2f);

            // The glide continues after release (the cabin is small, so track the furthest point reached
            // rather than where the player ends up after bouncing off the far side).
            float before = m_Player.transform.position.x;
            float furthest = before;
            for (int i = 0; i < 400 && furthest < before + 0.05f; i++)
            {
                yield return null;
                furthest = Mathf.Max(furthest, m_Player.transform.position.x);
            }
            Assert.Greater(furthest, before + 0.05f, "The glide continues after release.");

            // Grabbing a surface again stops the drift dead.
            hand.transform.position = new Vector3(m_Player.transform.position.x, 0.08f, m_Player.transform.position.z);
            yield return null;
            hand.GripOverride = 1f;
            Assert.IsTrue(m_ZeroG.TryGrabSurface(hand), "The floor is grabbable too.");
            Assert.AreEqual(0f, m_ZeroG.Speed, 0.001f);
            hand.GripOverride = null;
        }

        [UnityTest]
        public IEnumerator SwimmingStrokes_InOpenSpace_PropelThePlayer()
        {
            var hand = m_Player.leftHand;
            hand.GripOverride = 1f;
            yield return null;
            Assert.IsFalse(m_ZeroG.IsAnchored, "Gripping in open space must not anchor.");

            Vector3 start = m_Player.transform.position;
            // Sweep the hand backward (-Z): the body should be pushed forward (+Z).
            for (int i = 0; i < 14; i++)
            {
                MoveHandWorld(hand, Vector3.back * 0.03f);
                yield return null;
            }
            Assert.Greater(m_ZeroG.Velocity.z, 0.15f, "A backward stroke should push the player forward.");
            Assert.Greater(m_ZeroG.SwimSpeedGained, 0.15f);

            // Open the hand for the recovery stroke: moving it forward again must not cancel the speed.
            hand.GripOverride = 0f;
            yield return null;
            float speed = m_ZeroG.Velocity.z;
            for (int i = 0; i < 14; i++)
            {
                MoveHandWorld(hand, Vector3.forward * 0.03f);
                yield return null;
            }
            Assert.Greater(m_ZeroG.Velocity.z, speed * 0.8f, "The recovery stroke with an open hand should not brake.");
            yield return new WaitForSeconds(0.3f);
            Assert.Greater(m_Player.transform.position.z, start.z + 0.05f);
            hand.GripOverride = null;
        }

        [UnityTest]
        public IEnumerator DriftComfortOption_Off_StopsThePlayerAlmostImmediately()
        {
            m_Director.Profile.Data.zeroGDrift = false;
            m_Director.ApplyAndSaveSettings();
            Assert.IsFalse(m_ZeroG.momentumEnabled);

            m_ZeroG.AddImpulse(Vector3.forward * 1.5f);
            yield return new WaitForSeconds(1f);
            Assert.Less(m_ZeroG.Speed, 0.05f, "With drift off the player should not keep gliding.");

            var reloaded = new ProfileService(m_Storage);
            reloaded.Load();
            Assert.IsFalse(reloaded.Data.zeroGDrift, "The comfort choice must be saved.");
        }

        [UnityTest]
        public IEnumerator Tutorial_GuidesStepByStep_AndCompletesOnRealActions()
        {
            var tutorial = Object.FindFirstObjectByType<TutorialController>();
            Assert.IsNotNull(tutorial);
            Assert.AreEqual(TutorialStep.Welcome, tutorial.Step, "A new player starts at the beginning.");
            yield return Frames(3);
            Assert.IsTrue(tutorial.prompt.visuals.activeSelf, "The floating instruction should be showing.");
            Assert.AreEqual(MissionPhase.Hub, m_Director.Phase);

            tutorial.Advance();
            Assert.AreEqual(TutorialStep.GrabAndPushOff, tutorial.Step);

            // Step 1 completes on a real push-off.
            var hand = m_Player.rightHand;
            hand.transform.position = new Vector3(-1.8f, 1.2f, -0.4f);
            yield return null;
            hand.GripOverride = 1f;
            Assert.IsTrue(m_ZeroG.TryGrabSurface(hand));
            for (int i = 0; i < 10; i++)
            {
                MoveHandWorld(hand, Vector3.left * 0.02f);
                yield return null;
            }
            Assert.AreEqual(TutorialStep.GrabAndPushOff, tutorial.Step, "Holding on is not enough; the step needs a push-off.");
            hand.GripOverride = 0f;
            m_ZeroG.ReleaseSurface(hand);
            Assert.AreEqual(TutorialStep.Swim, tutorial.Step, "Pushing off a wall should complete step 1.");

            // Step 2 completes on real strokes.
            m_ZeroG.Stop();
            hand.transform.position = OpenSpace(0.6f);
            yield return Frames(2);
            hand.GripOverride = 1f;
            yield return null;
            for (int i = 0; i < 60 && tutorial.Step == TutorialStep.Swim; i++)
            {
                MoveHandWorld(hand, (i / 10 % 2 == 0 ? Vector3.back : Vector3.right) * 0.03f);
                yield return null;
            }
            hand.GripOverride = null;
            Assert.AreEqual(TutorialStep.Stop, tutorial.Step, "Swimming should complete step 2.");

            tutorial.Advance();
            Assert.AreEqual(TutorialStep.Tools, tutorial.Step);
            tutorial.Advance();
            Assert.AreEqual(TutorialStep.Salvage, tutorial.Step);

            // Step 5 completes when the practice scrap is banked.
            var practice = Object.FindObjectsByType<SalvageItem>(FindObjectsSortMode.None).First(i => i.isTraining);
            Assert.IsTrue(m_Director.locker.TryAccept(practice));
            Assert.AreEqual(TutorialStep.Cut, tutorial.Step);

            // Step 6 completes when the practice plate is cut free.
            foreach (var point in tutorial.practicePanel.cutPoints) tutorial.practicePanel.ApplyCut(point, 5f, 0.5f, m_Player);
            Assert.AreEqual(TutorialStep.Done, tutorial.Step);
            Assert.IsTrue(tutorial.IsComplete);
            Assert.IsTrue(m_Director.Profile.Data.tutorialCompleted);
            yield return Frames(2);
            Assert.IsFalse(tutorial.prompt.visuals.activeSelf, "The prompt goes away once the tutorial is finished.");

            var saved = new ProfileService(m_Storage);
            saved.Load();
            Assert.IsTrue(saved.Data.tutorialCompleted, "Finishing the tutorial must be saved.");

            // It can be replayed, which restores the practice plate and scrap.
            Assert.IsTrue(tutorial.Replay());
            Assert.AreEqual(TutorialStep.Welcome, tutorial.Step);
            Assert.IsFalse(tutorial.practicePanel.IsOpen);
            Assert.IsTrue(Object.FindObjectsByType<SalvageItem>(FindObjectsSortMode.None).Any(i => i.isTraining && !i.IsSecured));

            // Starting a contract ends it.
            tutorial.SkipAll();
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            Assert.AreEqual(TutorialStep.Done, tutorial.Step);
            Assert.IsFalse(tutorial.Replay(), "The tutorial cannot be replayed in the middle of a contract.");
        }
    }
}

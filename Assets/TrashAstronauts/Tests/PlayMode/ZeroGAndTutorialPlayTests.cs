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
        string m_StrokeInfo = "";

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

        /// <summary>Moves the hand so that its palm (the grab point) is at a world position.</summary>
        void PlacePalm(HandRig hand, Vector3 palmWorld)
        {
            hand.transform.position += palmWorld - hand.interactor.transform.position;
        }

        /// <summary>Sweeps a hand at a steady real speed for a while, whatever the frame rate.</summary>
        IEnumerator Stroke(HandRig hand, Vector3 direction, float metresPerSecond = 1.6f, float seconds = 0.3f)
        {
            int frames = 0;
            Vector3 startLocal = hand.transform.localPosition;
            for (float t = 0f; t < seconds; t += Time.deltaTime)
            {
                yield return null;
                MoveHandWorld(hand, direction * Mathf.Min(0.2f, metresPerSecond * Time.deltaTime));
                frames++;
            }
            yield return null;
            m_StrokeInfo = " [stroke: " + frames + " frames, dt " + Time.deltaTime.ToString("F4") + ", hand moved "
                + (hand.transform.localPosition - startLocal).magnitude.ToString("F3") + " m, dir " + direction.ToString("F2")
                + ", velocity " + m_ZeroG.Velocity.ToString("F3") + ", anchored " + m_ZeroG.IsAnchored + ", holding " + hand.IsHolding + ", grip " + hand.Grip.ToString("F1") + "]";
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
            PlacePalm(hand, new Vector3(-1.8f, 1.2f, -0.4f));
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
            PlacePalm(hand, new Vector3(m_Player.transform.position.x, 0.08f, m_Player.transform.position.z));
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
            Vector3 ahead = Vector3.ProjectOnPlane(m_Player.head.forward, Vector3.up).normalized;
            // Sweep the hand backward: the body should be pushed forward (the way the player is looking).
            yield return Stroke(hand, -ahead);
            Assert.Greater(Vector3.Dot(m_ZeroG.Velocity, ahead), 0.15f, "A backward stroke should push the player forward." + m_StrokeInfo);
            Assert.Greater(m_ZeroG.SwimSpeedGained, 0.15f);

            // Open the hand for the recovery stroke: moving it forward again must not cancel the speed.
            hand.GripOverride = 0f;
            yield return null;
            float speed = Vector3.Dot(m_ZeroG.Velocity, ahead);
            yield return Stroke(hand, ahead);
            Assert.Greater(Vector3.Dot(m_ZeroG.Velocity, ahead), speed * 0.75f, "The recovery stroke with an open hand should not brake.");
            hand.GripOverride = null;
        }

        [UnityTest]
        public IEnumerator OpenHandStrokes_SwimWithoutAnyButton_ButNeverBackward()
        {
            var hand = m_Player.leftHand;
            Assert.IsTrue(m_ZeroG.openHandSwim);
            Vector3 ahead = Vector3.ProjectOnPlane(m_Player.head.forward, Vector3.up).normalized;

            // A fast backward sweep with an open hand, no grip held.
            yield return Stroke(hand, -ahead);
            float forwardSpeed = Vector3.Dot(m_ZeroG.Velocity, ahead);
            Assert.Greater(forwardSpeed, 0.1f, "An open-hand stroke should move the player the way they are looking." + m_StrokeInfo);

            // Throwing the hand forward (reaching, or the return stroke) must not shove the player back.
            m_ZeroG.Stop();
            yield return null;
            yield return Stroke(hand, ahead);
            Assert.GreaterOrEqual(Vector3.Dot(m_ZeroG.Velocity, ahead), -0.02f, "Reaching forward must not push the player backward.");
        }

        [UnityTest]
        public IEnumerator Hands_AreSolid_PushingAWallShovesThePlayerAway()
        {
            var hand = m_Player.rightHand;
            // Move the player next to the tug's west wall (inner face at x = -1.9) and let things settle.
            m_Player.locomotion.TeleportTo(new Vector3(-1.2f, 0f, 1.4f));
            yield return Frames(5);
            foreach (var driver in m_Player.GetComponentsInChildren<TrackedPoseDriver>()) driver.enabled = false;
            m_Player.leftHand.transform.position = OpenSpace(0.6f);
            Vector3 head = m_Player.HeadPosition;
            PlacePalm(hand, new Vector3(-1.7f, head.y - 0.25f, head.z));
            yield return Frames(3);
            m_ZeroG.Stop();

            float pushed = -1f;
            m_ZeroG.WallPushed += speed => pushed = speed;
            float startX = m_Player.transform.position.x;

            // Drive the open hand through the wall, a couple of centimetres per frame.
            for (int i = 0; i < 20; i++)
            {
                MoveHandWorld(hand, Vector3.left * 0.02f);
                yield return null;
            }

            Vector3 palm = hand.interactor.transform.position;
            Assert.Greater(palm.x, -1.9f - 0.02f, "The hand must not sink into the wall.");
            Assert.Greater(m_Player.transform.position.x, startX + 0.1f, "Pushing on the wall should move the body away from it.");
            Assert.GreaterOrEqual(pushed, 0f, "The wall contact should be reported.");
            Assert.Greater(m_ZeroG.Velocity.x, 0.1f, "The shove should leave the player drifting away from the wall.");
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
            PlacePalm(hand, new Vector3(-1.8f, 1.2f, -0.4f));
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
            for (int i = 0; i < 8 && tutorial.Step == TutorialStep.Swim; i++)
                yield return Stroke(hand, i % 2 == 0 ? Vector3.back : Vector3.right, 1.6f, 0.25f);
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

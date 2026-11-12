using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Co-op stage 3, written first: the XRI question. A salvage item that another machine simulates is a
    /// kinematic replica here. Grabbing it through the real input path (simulated tracked controllers, real
    /// bindings, real XRI interactors) must put it in the hand the same frame, carry it with the game's
    /// normal weight (lag for heavy things, a second hand on heavy things), and let it go non-kinematic with
    /// the throw XRI computed. The answer is SalvageGrabInteractable (Builder.MakeGrabbable): it makes the
    /// body simulated here in OnSelectEntering before the base grab remembers its kinematic state.
    /// </summary>
    public class CoopGrabXriPlayTests
    {
        MissionDirector m_Director;
        PlayerContext m_Player;
        InputSettings.BackgroundBehavior m_OldBackground;
        InputSettings.EditorInputBehaviorInPlayMode m_OldEditorBehavior;

        sealed class Controller
        {
            public XRSimulatedController device;
            public XRSimulatedControllerState state;
            public HandRig hand;
        }

        Controller m_Right, m_Left;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            m_OldBackground = InputSystem.settings.backgroundBehavior;
            m_OldEditorBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;

            MissionDirector.StorageOverride = new MemorySaveStorage();
            MissionDirector.SeedOverride = 4242;
            MissionDirector.CreatureOverride = 0;
            MissionDirector.MapOverride = 0;
            MissionDirector.ConditionOverride = ContractCondition.Standard;
            BlackoutController.DamageOverride = 0f;
            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            yield return null;
            m_Director = MissionDirector.Active;
            m_Player = m_Director.Players.First();

            m_Right = AddController("Test Right Controller", UnityEngine.InputSystem.CommonUsages.RightHand, m_Player.rightHand);
            m_Left = AddController("Test Left Controller", UnityEngine.InputSystem.CommonUsages.LeftHand, m_Player.leftHand);
            // Fixed frames: one physics step per frame at the game's 72 Hz, so a throw is the same every run.
            Time.captureDeltaTime = RealPhysics.FixedTimestep;
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            Time.captureDeltaTime = 0f;
            if (m_Right != null && m_Right.device != null) InputSystem.RemoveDevice(m_Right.device);
            if (m_Left != null && m_Left.device != null) InputSystem.RemoveDevice(m_Left.device);
            InputSystem.settings.backgroundBehavior = m_OldBackground;
            InputSystem.settings.editorInputBehaviorInPlayMode = m_OldEditorBehavior;
            MissionDirector.StorageOverride = null;
            MissionDirector.SeedOverride = null;
            MissionDirector.CreatureOverride = null;
            MissionDirector.MapOverride = null;
            MissionDirector.ConditionOverride = null;
            BlackoutController.DamageOverride = null;
        }

        static Controller AddController(string name, InternedString usage, HandRig hand)
        {
            var device = InputSystem.AddDevice<XRSimulatedController>(name);
            InputSystem.SetDeviceUsage(device, usage);
            return new Controller
            {
                device = device,
                hand = hand,
                state = new XRSimulatedControllerState { isTracked = true, trackingState = 3, deviceRotation = Quaternion.identity },
            };
        }

        static IEnumerator Settle(int frames = 3)
        {
            for (int i = 0; i < frames; i++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
        }

        /// <summary>Queues a controller pose so its grab point is at a world position (no waiting).</summary>
        static void Place(Controller c, Vector3 grabPointWorld)
        {
            Transform space = c.hand.transform.parent;
            Quaternion rotation = space.rotation;
            Vector3 grabOffset = c.hand.transform.InverseTransformPoint(c.hand.interactor.transform.position);
            Vector3 controllerWorld = grabPointWorld - rotation * grabOffset;
            c.state.devicePosition = space.InverseTransformPoint(controllerWorld);
            c.state.deviceRotation = Quaternion.Inverse(space.rotation) * rotation;
            InputSystem.QueueStateEvent(c.device, c.state);
        }

        static void SetGrip(Controller c, bool down)
        {
            c.state.grip = down ? 1f : 0f;
            c.state = c.state.WithButton(ControllerButton.GripButton, down);
            InputSystem.QueueStateEvent(c.device, c.state);
        }

        IEnumerator Pose(Controller c, Vector3 grabPointWorld)
        {
            Place(c, grabPointWorld);
            yield return Settle();
        }

        IEnumerator Grip(Controller c, bool down)
        {
            SetGrip(c, down);
            yield return Settle();
        }

        /// <summary>The player floats in the open cargo bay, at rest; returns a point in front of them with room around it.</summary>
        IEnumerator FloatInTheOpen()
        {
            m_Director.keepPlayersInHub = false;
            m_Player.locomotion.TeleportTo(new Vector3(0f, 0.2f, 9.2f), Quaternion.identity);
            for (int i = 0; i < 3; i++) yield return null;
            m_Player.locomotion.zeroG.Stop();
            yield return Settle();
        }

        Vector3 Spot => m_Player.HeadPosition + new Vector3(0.25f, -0.35f, 0.35f);

        static void PlaceItem(SalvageItem item, Vector3 at)
        {
            if (!item.Body.isKinematic)
            {
                item.Body.linearVelocity = Vector3.zero;
                item.Body.angularVelocity = Vector3.zero;
            }
            item.transform.SetPositionAndRotation(at, Quaternion.identity);
            item.Body.position = at;
            item.Body.rotation = Quaternion.identity;
            Physics.SyncTransforms();
        }

        /// <summary>A loose item light enough to follow the hand exactly (one hand, no lag).</summary>
        SalvageItem LightItem() => m_Director.Items
            .Where(i => i != null && i.definition != null && !i.IsMounted && !i.definition.bonusSpawn && i.Body.mass <= 2f)
            .OrderByDescending(i => i.Body.mass).First();

        SalvageItem HeavyItem() => m_Director.Items.Where(i => !i.IsMounted && i.definition != null).OrderByDescending(i => i.Body.mass).First();

        /// <summary>Grabs, moves the hand along <paramref name="direction"/> at <paramref name="speed"/> for a few frames and lets go mid-swing. Returns the item's velocity a few steps later.</summary>
        IEnumerator ThrowWith(Controller c, SalvageItem item, Vector3 direction, float speed, Vector3[] velocity, bool[] state)
        {
            // The hand first (a hand that jumps there would shove a light item aside), then the item into it.
            Vector3 at = Spot;
            yield return Pose(c, at);
            PlaceItem(item, at);
            yield return Settle();
            bool kinematicAtSelect = true, simulatedAtSelect = false, heldAtSelect = false;
            void OnSelect(SelectEnterEventArgs _)
            {
                kinematicAtSelect = item.Body.isKinematic;
                simulatedAtSelect = item.SimulatedHere;
                heldAtSelect = item.IsHeld;
            }
            item.grab.selectEntered.AddListener(OnSelect);
            yield return Grip(c, true);
            item.grab.selectEntered.RemoveListener(OnSelect);
            Assert.IsTrue(item.IsHeld, "Gripping a hovered item picks it up: " + item.name + " " + item.Body.mass + " kg at " + item.transform.position.ToString("F2")
                + ", grab point " + c.hand.interactor.transform.position.ToString("F2") + ", hovering " + c.hand.interactor.interactablesHovered.Count
                + ", selectActive " + c.hand.interactor.isSelectActive + ", enabled " + item.grab.enabled + ", kinematic " + item.Body.isKinematic);
            state[0] = kinematicAtSelect;
            state[1] = simulatedAtSelect;
            state[2] = heldAtSelect;
            yield return Settle(10);

            // A swing: one step per frame at 72 Hz, letting go while the hand still moves.
            float step = speed * RealPhysics.FixedTimestep;
            Vector3 hand = at;
            for (int i = 0; i < 16; i++)
            {
                hand += direction * step;
                Place(c, hand);
                yield return null;
            }
            hand += direction * step;
            Place(c, hand);
            SetGrip(c, false);
            yield return null;
            for (int i = 0; i < 4; i++)
            {
                hand += direction * step;
                Place(c, hand);
                yield return null;
            }
            Assert.IsFalse(item.IsHeld, "Letting go of the grip drops it.");
            for (int i = 0; i < 3; i++) yield return new WaitForFixedUpdate();
            velocity[0] = item.Body.linearVelocity;
            state[3] = item.Body.isKinematic;
            // Out of the way for the next pass.
            Place(c, at);
            yield return Settle();
        }

        [UnityTest]
        public IEnumerator AKinematicReplica_IsInTheHandTheSameFrame_AndLeavesItNonKinematic_WithTheTrueThrowVelocity()
        {
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            yield return FloatInTheOpen();
            var item = LightItem();
            Assert.IsInstanceOf<SalvageGrabInteractable>(item.grab, "Builder.MakeGrabbable puts the game's own grab on salvage.");
            Assert.AreEqual(XRBaseInteractable.MovementType.VelocityTracking, item.grab.movementType);
            Assert.IsTrue(item.grab.throwOnDetach);
            Vector3 direction = Vector3.forward;

            // 1) The same throw of an ordinary item simulated here: the reference.
            var reference = new Vector3[1];
            var state = new bool[4];
            yield return ThrowWith(m_Right, item, direction, 3f, reference, state);
            Assert.IsFalse(state[0], "Offline nothing changes: dynamic as it is picked up.");
            Assert.IsFalse(state[3]);
            Assert.Greater(Vector3.Dot(reference[0], direction), 1.5f, "The reference throw carries the hand's speed: " + reference[0]);

            // 2) The same item as a replica that another machine simulates: kinematic, following poses.
            item.SetSimulatedHere(false);
            Assert.IsTrue(item.Body.isKinematic, "A replica's body is kinematic.");
            Assert.IsTrue(item.grab.enabled, "...and it can still be taken in hand.");
            int replicaGrabs = ((SalvageGrabInteractable)item.grab).ReplicaGrabs;
            var replica = new Vector3[1];
            yield return ThrowWith(m_Right, item, direction, 3f, replica, state);
            Assert.IsFalse(state[0], "The body was already dynamic when the grab saw it (OnSelectEntering ran first).");
            Assert.IsTrue(state[1], "Simulated here from the moment the hand closes.");
            Assert.IsTrue(state[2], "In the hand the same frame.");
            Assert.AreEqual(replicaGrabs + 1, ((SalvageGrabInteractable)item.grab).ReplicaGrabs);
            Assert.IsFalse(state[3], "Released non-kinematic, so XRI's throw velocity was applied.");
            Assert.IsTrue(item.SimulatedHere);
            Assert.Greater(Vector3.Dot(replica[0], direction), 1.5f, "It flies on in the direction of the swing: " + replica[0]);
            Assert.Less(Vector3.Distance(replica[0], reference[0]), 0.15f * reference[0].magnitude + 0.05f,
                "The replica leaves with the same velocity as an ordinary item thrown the same way: " + replica[0] + " vs " + reference[0]);
        }

        [UnityTest]
        public IEnumerator AHeavyReplica_LagsInOneHand_TakesASecondHand_AndIsThrownWithBoth()
        {
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            yield return FloatInTheOpen();
            var core = HeavyItem();
            var weight = core.GetComponent<HeldWeight>();
            Assert.Greater(core.Body.mass, weight.freeMass);
            Assert.AreEqual(InteractableSelectMode.Multiple, core.grab.selectMode, "Heavy things take a second hand.");
            HeldWeight.Follow(core.Body.mass, out float oneHandScale, out _);
            HeldWeight.Follow(core.Body.mass * HeldWeight.TwoHandShare, out float twoHandScale, out _);

            core.SetSimulatedHere(false);
            Assert.IsTrue(core.Body.isKinematic);
            Vector3 at = Spot;
            yield return Pose(m_Right, at);
            PlaceItem(core, at);
            yield return Settle();
            yield return Grip(m_Right, true);
            Assert.IsTrue(core.IsHeld, "The heavy replica is picked up.");
            Assert.IsFalse(core.Body.isKinematic);
            Assert.AreEqual(1, weight.HandCount);
            Assert.AreEqual(oneHandScale, core.grab.velocityScale, 1e-4f, "The normal one-handed weight.");
            yield return Settle(20);

            // A brisk swing: the core trails the hand, then catches up.
            float lagDuring = 0f;
            Vector3 hand = at;
            for (int i = 0; i < 14; i++)
            {
                hand += Vector3.forward * 0.0214f;
                Place(m_Right, hand);
                yield return null;
                lagDuring = Mathf.Max(lagDuring, weight.GripGap);
            }
            Assert.Greater(lagDuring, 0.04f, "A " + core.Body.mass + " kg core trails the hand.");
            Assert.Less(lagDuring, 0.4f, "But only a little.");
            yield return Settle(90);
            Assert.Less(weight.GripGap, 0.06f, "And catches up once the hand stops.");
            Assert.IsTrue(core.IsHeld);

            // The other hand takes hold as well: two hands share the load.
            Vector3 leftAt = core.transform.TransformPoint(Vector3.up * 0.1f);
            yield return Pose(m_Left, leftAt);
            yield return Grip(m_Left, true);
            Assert.AreEqual(2, weight.HandCount, "Both hands hold the replica.");
            Assert.AreEqual(twoHandScale, core.grab.velocityScale, 1e-4f, "Two hands steer it as if it weighed a quarter.");
            yield return Settle(20);

            // Thrown with both hands: they let go a moment apart, mid-swing.
            Vector3 right = hand, left = leftAt;
            float step = 2.5f * RealPhysics.FixedTimestep;
            for (int i = 0; i < 16; i++)
            {
                right += Vector3.forward * step;
                left += Vector3.forward * step;
                Place(m_Right, right);
                Place(m_Left, left);
                yield return null;
            }
            SetGrip(m_Left, false);
            right += Vector3.forward * step;
            Place(m_Right, right);
            yield return null;
            Assert.IsTrue(core.IsHeld, "One hand still has it.");
            SetGrip(m_Right, false);
            right += Vector3.forward * step;
            Place(m_Right, right);
            yield return null;
            yield return null;
            Assert.IsFalse(core.IsHeld);
            for (int i = 0; i < 3; i++) yield return new WaitForFixedUpdate();
            Assert.IsFalse(core.Body.isKinematic, "Let go non-kinematic.");
            Assert.AreEqual(2, weight.LastThrowArms, "A two-handed throw.");
            Vector3 v = core.Body.linearVelocity - m_Player.locomotion.zeroG.Velocity;
            Assert.Greater(v.z, 0.5f, "It flies on with the swing: " + v);
            Assert.LessOrEqual(v.magnitude, RealPhysics.MaxThrowSpeed(core.Body.mass / 2f) + 0.05f, "Within what two arms can give it.");
        }

        [UnityTest]
        public IEnumerator AReplicaInAnotherCrewMembersHand_CannotBeSelected_AndCanOnceTheyLetGo()
        {
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            yield return FloatInTheOpen();
            var item = LightItem();
            item.SetSimulatedHere(false);
            item.SetRemoteHolder(new PlayerId(1));
            Vector3 at = Spot;
            yield return Pose(m_Right, at);
            PlaceItem(item, at);
            yield return Settle();
            yield return Grip(m_Right, true);
            yield return Settle(4);
            Assert.IsFalse(item.IsHeld && item.grab.isSelected, "Nothing can be taken out of another crew member's hand.");
            Assert.IsFalse(m_Right.hand.IsHolding);
            Assert.IsTrue(item.Body.isKinematic);
            Assert.IsFalse(item.SimulatedHere);
            yield return Grip(m_Right, false);

            // They let go: now it can be caught.
            item.SetRemoteHolder(PlayerId.None);
            yield return Grip(m_Right, true);
            Assert.IsTrue(item.grab.isSelected, "Once they let go it can be taken.");
            Assert.IsTrue(item.SimulatedHere);
            Assert.IsFalse(item.Body.isKinematic);
            yield return Grip(m_Right, false);
        }
    }
}

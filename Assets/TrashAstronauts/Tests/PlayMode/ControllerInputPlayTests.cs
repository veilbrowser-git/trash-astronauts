using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.Tools;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Drives the game through a simulated tracked controller: real input bindings, real tracked pose,
    /// real XRI grab interactor. This is the closest thing to a headset the editor can test, and it is
    /// what catches "the hands work but nothing can be picked up".
    /// </summary>
    public class ControllerInputPlayTests
    {
        MissionDirector m_Director;
        PlayerContext m_Player;
        XRSimulatedController m_Device;
        XRSimulatedControllerState m_State;
        HandRig m_Hand;
        InputSettings.BackgroundBehavior m_OldBackground;
        InputSettings.EditorInputBehaviorInPlayMode m_OldEditorBehavior;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            // The test run has no focused game window; without this the Input System drops device input.
            m_OldBackground = InputSystem.settings.backgroundBehavior;
            m_OldEditorBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;

            MissionDirector.StorageOverride = new MemorySaveStorage();
            MissionDirector.SeedOverride = 4242;
            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            yield return null;
            m_Director = MissionDirector.Active;
            m_Player = m_Director.Players.First();
            m_Hand = m_Player.rightHand;

            m_Device = InputSystem.AddDevice<XRSimulatedController>("Test Right Controller");
            InputSystem.SetDeviceUsage(m_Device, UnityEngine.InputSystem.CommonUsages.RightHand);
            m_State = new XRSimulatedControllerState { isTracked = true, trackingState = 3, deviceRotation = Quaternion.identity };
            yield return Pose(m_Player.HeadPosition + new Vector3(0.3f, -0.3f, 0.3f));
        }

        [TearDown]
        public void TearDown()
        {
            if (m_Device != null) InputSystem.RemoveDevice(m_Device);
            InputSystem.settings.backgroundBehavior = m_OldBackground;
            InputSystem.settings.editorInputBehaviorInPlayMode = m_OldEditorBehavior;
            MissionDirector.StorageOverride = null;
            MissionDirector.SeedOverride = null;
        }

        static IEnumerator Settle(int frames = 3)
        {
            for (int i = 0; i < frames; i++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
        }

        /// <summary>Moves the controller so its grab point is at a world position.</summary>
        IEnumerator Pose(Vector3 grabPointWorld, Quaternion? worldRotation = null)
        {
            Transform space = m_Hand.transform.parent;
            Quaternion rotation = worldRotation ?? space.rotation;
            Vector3 grabOffset = m_Hand.transform.InverseTransformPoint(m_Hand.interactor.transform.position);
            Vector3 controllerWorld = grabPointWorld - rotation * grabOffset;
            m_State.devicePosition = space.InverseTransformPoint(controllerWorld);
            m_State.deviceRotation = Quaternion.Inverse(space.rotation) * rotation;
            InputSystem.QueueStateEvent(m_Device, m_State);
            yield return Settle();
        }

        IEnumerator Buttons(float grip, float trigger)
        {
            m_State.grip = grip;
            m_State.trigger = trigger;
            m_State = m_State.WithButton(ControllerButton.GripButton, grip > 0.5f).WithButton(ControllerButton.TriggerButton, trigger > 0.5f);
            InputSystem.QueueStateEvent(m_Device, m_State);
            yield return Settle();
        }

        string Diagnose()
        {
            var interactor = m_Hand.interactor;
            return " [hand at " + m_Hand.transform.position.ToString("F2")
                + ", grab point " + interactor.transform.position.ToString("F2")
                + ", hovering " + interactor.interactablesHovered.Count
                + ", selecting " + interactor.interactablesSelected.Count
                + ", selectActive " + interactor.isSelectActive
                + ", gripValue " + m_Hand.Grip.ToString("F2") + "]";
        }

        [UnityTest]
        public IEnumerator Controller_TracksPose_AndReadsGripAndTrigger()
        {
            Vector3 target = m_Player.HeadPosition + new Vector3(0.1f, -0.2f, 0.4f);
            yield return Pose(target);
            Assert.Less(Vector3.Distance(m_Hand.interactor.transform.position, target), 0.02f, "The hand should follow the tracked controller." + Diagnose());

            yield return Buttons(1f, 0f);
            Assert.Greater(m_Hand.Grip, 0.9f, "Grip should be read from the controller." + Diagnose());
            yield return Buttons(0f, 1f);
            Assert.Greater(m_Hand.Trigger, 0.9f, "Trigger should be read from the controller." + Diagnose());
            yield return Buttons(0f, 0f);
        }

        [UnityTest]
        public IEnumerator Controller_CanPickUp_Carry_AndReleaseSalvage()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Settle();
            var item = m_Director.Items.First(i => !i.IsMounted && i.definition.category == SalvageCategory.SmallScrap);
            Vector3 start = item.transform.position;

            yield return Pose(start);
            Assert.Greater(m_Hand.interactor.interactablesHovered.Count, 0, "Reaching a hand to salvage should hover it." + Diagnose());

            yield return Buttons(1f, 0f);
            Assert.IsTrue(item.IsHeld, "Gripping while touching salvage should pick it up." + Diagnose());
            Assert.IsTrue(item.HasBeenCollected);

            // Carry it somewhere else: it follows the hand.
            Vector3 carried = start + new Vector3(0.25f, 0.1f, -0.2f);
            yield return Pose(carried);
            yield return Settle(20);
            Assert.Less(Vector3.Distance(item.transform.position, carried), 0.25f, "Held salvage should follow the hand." + Diagnose());

            yield return Buttons(0f, 0f);
            Assert.IsFalse(item.IsHeld, "Letting go of grip should release it." + Diagnose());
        }

        [UnityTest]
        public IEnumerator Controller_CanDrawTheCutter_FireIt_AndCutAPanelOpen()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Settle(4);
            var cutter = m_Player.tools.OfType<PlasmaCutterTool>().First();
            Assert.IsTrue(cutter.IsDocked);

            // Reach to the holster and grip: the cutter should end up in the hand.
            yield return Pose(cutter.transform.position);
            Assert.Greater(m_Hand.interactor.interactablesHovered.Count, 0, "The holstered cutter should be hoverable." + Diagnose());
            yield return Buttons(1f, 0f);
            yield return Settle(4);
            Assert.IsTrue(cutter.IsHeld, "Gripping at the holster should draw the cutter." + Diagnose());

            // Trigger lights the flame.
            yield return Buttons(1f, 1f);
            yield return Settle(4);
            Assert.IsTrue(cutter.TriggerHeld, "Trigger should activate the held tool." + Diagnose());
            Assert.IsTrue(cutter.IsFiring, "The cutter should fire while the trigger is held." + Diagnose());

            // Hold the flame on each weld of the secure cargo locker.
            var panel = m_Director.cuttables.First(c => c.panelId == "panel_cage");
            Quaternion facePanel = Quaternion.LookRotation(-panel.transform.forward, Vector3.up);
            foreach (var point in panel.cutPoints)
            {
                // Position the hand so the cutter tip sits on the weld.
                for (int attempt = 0; attempt < 200 && !point.IsCut; attempt++)
                {
                    Vector3 tipOffset = cutter.tip.position - m_Hand.interactor.transform.position;
                    yield return Pose(point.transform.position - tipOffset, facePanel);
                    if (cutter.Heat.Overheated)
                    {
                        yield return Buttons(1f, 0f);
                        yield return new WaitForSeconds(2.5f);
                        yield return Buttons(1f, 1f);
                    }
                }
                Assert.IsTrue(point.IsCut, "Holding the flame on a weld should burn it through." + Diagnose()
                    + " tip " + cutter.tip.position.ToString("F2") + " weld " + point.transform.position.ToString("F2") + " firing " + cutter.IsFiring);
            }
            Assert.IsTrue(panel.IsOpen, "Cutting every weld should open the panel.");
            yield return Buttons(0f, 0f);
        }
    }
}

using System.Collections.Generic;
using TrashAstronauts.Core;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
#if TA_XR_HANDS
using UnityEngine.XR.Hands;
#endif

namespace TrashAstronauts.Player
{
    /// <summary>
    /// Controller-free play. When the headset is tracking a bare hand (and that hand's controller is not
    /// tracked), this drives the hand rig from the hand's palm pose and turns two gestures into the
    /// inputs the rest of the game already understands:
    ///   - closing the fist  = grip   (grab items and surfaces, hold on, paddle)
    ///   - pinching thumb and index = trigger (use a tool, press a button you are pointing at)
    ///   - left palm toward your face + pinch = suit menu
    /// Everything else (grabbing, tools, zero-g movement, buttons) is unchanged, because it only ever
    /// sees "grip" and "trigger". Teleport and snap turn need a thumbstick and are controller-only.
    ///
    /// EXPERIMENTAL: written against the XR Hands API without a headset to test on.
    /// </summary>
    [DefaultExecutionOrder(-200)] // before XRI processes interactors, so they see this frame's gesture
    public sealed class HandTrackingDriver : MonoBehaviour
    {
        public HandRig hand;
        public TrackedPoseDriver poseDriver;
        [Tooltip("Tracking space (the rig's Camera Offset). Hand joint poses are relative to it.")]
        public Transform trackingSpace;
        public Transform head;
        [Tooltip("The glove model. Its palm block is centred at 'glovePalmCentre' in the glove's own space, palm facing -Y, fingers +Z.")]
        public Transform glove;
        public Vector3 glovePalmCentre = new Vector3(0f, -0.03f, -0.075f);

        /// <summary>A button reader fed by gestures. Plugged into the grab interactor as a bypass.</summary>
        sealed class GestureButton : IXRInputButtonReader
        {
            public float value;
            public bool pressed;
            public int pressedFrame = -1;
            public int releasedFrame = -1;

            public void Set(float newValue)
            {
                value = newValue;
                bool now = HandGestures.Latch(pressed, newValue);
                if (now && !pressed) pressedFrame = Time.frameCount;
                if (!now && pressed) releasedFrame = Time.frameCount;
                pressed = now;
            }

            public void Clear()
            {
                if (pressed) releasedFrame = Time.frameCount;
                pressed = false;
                value = 0f;
            }

            public bool ReadIsPerformed() => pressed;
            public bool ReadWasPerformedThisFrame() => pressedFrame == Time.frameCount;
            public bool ReadWasCompletedThisFrame() => releasedFrame == Time.frameCount;
            public float ReadValue() => value;
            public bool TryReadValue(out float result)
            {
                result = value;
                return true;
            }
        }

        readonly GestureButton m_Grip = new GestureButton();
        readonly GestureButton m_Pinch = new GestureButton();
        Vector3 m_FingertipRest;
        bool m_GloveCached;
        Quaternion m_GloveRestRotation = Quaternion.identity;
        Vector3 m_GloveRestPosition;
        bool m_RestCached;

        public bool IsActive { get; private set; }
        public float Fist => m_Grip.value;
        public float Pinch => m_Pinch.value;

        void OnDisable() => SetActive(false);

        void Update()
        {
            bool tracked = TryGetHand(out Pose palm, out Vector3 thumbTip, out Vector3 indexTip, out Vector3 middleTip, out Vector3 ringTip, out Vector3 littleTip);
            bool useHand = tracked && !ControllerIsTracked();
            if (useHand != IsActive) SetActive(useHand);
            if (!IsActive) return;

            // Pose: the tracked palm joint has the same layout as the glove model (fingers along +Z, palm
            // facing -Y), so place the rig such that the glove lands exactly on the real hand.
            Quaternion palmRotation = trackingSpace != null ? trackingSpace.rotation * palm.rotation : palm.rotation;
            Vector3 palmWorld = trackingSpace != null ? trackingSpace.TransformPoint(palm.position) : palm.position;
            if (!m_GloveCached && glove != null)
            {
                // Read once: the wall clamp nudges the glove around afterwards.
                m_GloveRestRotation = glove.localRotation;
                m_GloveRestPosition = glove.localPosition;
                m_GloveCached = true;
            }
            Quaternion gloveLocalRotation = m_GloveCached ? m_GloveRestRotation : Quaternion.identity;
            Vector3 gloveLocalPosition = m_GloveCached ? m_GloveRestPosition : Vector3.zero;
            Quaternion rotation = palmRotation * Quaternion.Inverse(gloveLocalRotation);
            Vector3 palmCentreInRig = gloveLocalPosition + gloveLocalRotation * glovePalmCentre;
            hand.transform.SetPositionAndRotation(palmWorld - rotation * palmCentreInRig, rotation);

            m_Grip.Set(HandGestures.FistAmount(palm.position, middleTip, ringTip, littleTip));
            m_Pinch.Set(HandGestures.PinchAmount(thumbTip, indexTip));
            hand.GripOverride = m_Grip.value;
            hand.TriggerOverride = m_Pinch.value;

            bool pinchedNow = m_Pinch.pressedFrame == Time.frameCount;
            if (pinchedNow)
            {
                // Left palm turned toward the face + pinch opens the suit menu instead of pulling a trigger.
                bool palmTowardFace = head != null && hand.side == TrashAstronauts.Core.Handedness.Left
                    && Vector3.Dot(-(palmRotation * Vector3.up), (head.position - palmWorld).normalized) > 0.6f;
                if (palmTowardFace) hand.MenuPressedFrame = Time.frameCount;
                else hand.TriggerPressedFrame = Time.frameCount;
            }

            // Poke buttons with the real index fingertip.
            if (hand.fingertip != null)
                hand.fingertip.position = trackingSpace != null ? trackingSpace.TransformPoint(indexTip) : indexTip;
        }

        void SetActive(bool active)
        {
            IsActive = active;
            if (hand == null) return;
            hand.IsTrackedHand = active;
            if (poseDriver != null) poseDriver.enabled = !active;

            if (hand.fingertip != null)
            {
                if (!m_RestCached)
                {
                    m_FingertipRest = hand.fingertip.localPosition;
                    m_RestCached = true;
                }
                if (!active) hand.fingertip.localPosition = m_FingertipRest;
            }

            if (hand.interactor != null)
            {
                hand.interactor.selectInput.bypass = active ? m_Grip : null;
                hand.interactor.activateInput.bypass = active ? m_Pinch : null;
            }
            if (!active)
            {
                m_Grip.Clear();
                m_Pinch.Clear();
                hand.GripOverride = null;
                hand.TriggerOverride = null;
            }
        }

        bool ControllerIsTracked()
        {
            if (poseDriver == null) return false;
            var action = poseDriver.trackingStateInput.action;
            if (action == null || !action.enabled || action.controls.Count == 0) return false;
            return action.ReadValue<int>() != 0;
        }

#if TA_XR_HANDS
        static readonly List<XRHandSubsystem> s_Subsystems = new List<XRHandSubsystem>();
        XRHandSubsystem m_Subsystem;

        bool TryGetHand(out Pose palm, out Vector3 thumbTip, out Vector3 indexTip, out Vector3 middleTip, out Vector3 ringTip, out Vector3 littleTip)
        {
            palm = Pose.identity;
            thumbTip = indexTip = middleTip = ringTip = littleTip = Vector3.zero;
            if (hand == null) return false;

            if (m_Subsystem == null || !m_Subsystem.running)
            {
                m_Subsystem = null;
                SubsystemManager.GetSubsystems(s_Subsystems);
                foreach (var subsystem in s_Subsystems)
                {
                    if (subsystem.running)
                    {
                        m_Subsystem = subsystem;
                        break;
                    }
                }
                if (m_Subsystem == null) return false;
            }

            XRHand xrHand = hand.side == TrashAstronauts.Core.Handedness.Left ? m_Subsystem.leftHand : m_Subsystem.rightHand;
            if (!xrHand.isTracked) return false;

            return xrHand.GetJoint(XRHandJointID.Palm).TryGetPose(out palm)
                && TryPosition(xrHand, XRHandJointID.ThumbTip, out thumbTip)
                && TryPosition(xrHand, XRHandJointID.IndexTip, out indexTip)
                && TryPosition(xrHand, XRHandJointID.MiddleTip, out middleTip)
                && TryPosition(xrHand, XRHandJointID.RingTip, out ringTip)
                && TryPosition(xrHand, XRHandJointID.LittleTip, out littleTip);
        }

        static bool TryPosition(XRHand xrHand, XRHandJointID id, out Vector3 position)
        {
            if (xrHand.GetJoint(id).TryGetPose(out Pose pose))
            {
                position = pose.position;
                return true;
            }
            position = Vector3.zero;
            return false;
        }
#else
        // XR Hands package not installed: hand tracking is simply unavailable.
        bool TryGetHand(out Pose palm, out Vector3 thumbTip, out Vector3 indexTip, out Vector3 middleTip, out Vector3 ringTip, out Vector3 littleTip)
        {
            palm = Pose.identity;
            thumbTip = indexTip = middleTip = ringTip = littleTip = Vector3.zero;
            return false;
        }
#endif
    }
}

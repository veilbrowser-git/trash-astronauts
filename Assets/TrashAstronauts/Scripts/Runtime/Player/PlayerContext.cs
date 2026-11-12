using System;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Tools;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// One player's presence in the mission: who they are, where their head is, and how to give them
    /// feedback. World objects receive a PlayerContext (or PlayerId) with each interaction instead of
    /// looking up a global player, which is the seam co-op will need: remote players get a PlayerContext
    /// too, just without local input or haptics behind it.
    /// </summary>
    public sealed class PlayerContext : MonoBehaviour
    {
        [SerializeField] int m_PlayerIndex;
        public Transform head;
        public VoiceSensor voice;
        public HandRig leftHand;
        public HandRig rightHand;
        public PlayerLocomotion locomotion;
        public ScreenFader fader;
        public BodyAnchor bodyAnchor;
        public Transform wristDisplay;
        public SuitTool[] tools;

        public PlayerId Id => new PlayerId(m_PlayerIndex);
        public Vector3 HeadPosition => head != null ? head.position : transform.position;
        /// <summary>Written by the mission director each frame.</summary>
        public bool InSafeZone { get; set; } = true;
        public Handedness Handedness { get; private set; } = Handedness.Right;

        public HandRig DominantHand => Handedness == Handedness.Left ? leftHand : rightHand;
        public HandRig OffHand => Handedness == Handedness.Left ? rightHand : leftHand;

        public event Action<Handedness> HandednessChanged;

        void Start()
        {
            if (MissionDirector.Active != null) MissionDirector.Active.RegisterPlayer(this);
        }

        void OnDestroy()
        {
            if (MissionDirector.Active != null) MissionDirector.Active.UnregisterPlayer(this);
        }

        /// <summary>Reels every tool that is not in a hand back to its holster.</summary>
        public void RecallTools()
        {
            if (tools == null) return;
            foreach (var tool in tools)
            {
                if (tool != null) tool.Recall();
            }
        }

        public bool IsHoldingAnyTool
        {
            get
            {
                if (tools == null) return false;
                foreach (var tool in tools)
                {
                    if (tool != null && tool.IsHeld) return true;
                }
                return false;
            }
        }

        public static PlayerContext From(IXRInteractor interactor)
        {
            return interactor != null && interactor.transform != null
                ? interactor.transform.GetComponentInParent<PlayerContext>()
                : null;
        }

        public static HandRig HandFrom(IXRInteractor interactor)
        {
            return interactor != null && interactor.transform != null
                ? interactor.transform.GetComponentInParent<HandRig>()
                : null;
        }

        public void SetHandedness(Handedness handedness)
        {
            Handedness = handedness;
            if (bodyAnchor != null) bodyAnchor.ApplyHandedness(handedness);
            AttachWristDisplay();
            HandednessChanged?.Invoke(handedness);
        }

        void AttachWristDisplay()
        {
            if (wristDisplay == null || OffHand == null || OffHand.wristMount == null) return;
            wristDisplay.SetParent(OffHand.wristMount, false);
            wristDisplay.localPosition = Vector3.zero;
            wristDisplay.localRotation = Quaternion.identity;
        }

        public void PulseBoth(float amplitude, float duration)
        {
            if (leftHand != null) leftHand.Pulse(amplitude, duration);
            if (rightHand != null) rightHand.Pulse(amplitude, duration);
        }
    }
}

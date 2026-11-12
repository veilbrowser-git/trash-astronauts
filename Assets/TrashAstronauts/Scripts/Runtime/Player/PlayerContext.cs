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
        public VisorDamage visor;
        [Tooltip("One-way pushers on the chest and hands (see Bumper).")]
        public Bumper[] bumpers;
        public HandRig leftHand;
        public HandRig rightHand;
        public PlayerLocomotion locomotion;
        public ScreenFader fader;
        public BodyAnchor bodyAnchor;
        [Tooltip("The visible suit (torso, arms, legs; helmet hidden for the local player).")]
        public PlayerAvatar avatar;
        public Transform wristDisplay;
        public SuitTool[] tools;

        public PlayerId Id => new PlayerId(m_PlayerIndex);
        /// <summary>
        /// Where this player's head is for the rules (safe zone, the creature's senses, extraction). For a
        /// remote player this is the newest position received, not the smoothed one the avatar is drawn at.
        /// </summary>
        public Vector3 HeadPosition => IsRemote && m_HasRemoteHead ? m_RemoteHead : head != null ? head.position : transform.position;
        /// <summary>Written by the mission director each frame.</summary>
        public bool InSafeZone { get; set; } = true;

        /// <summary>True for another crew member's stand-in: no input, haptics, tools or save profile behind it.</summary>
        public bool IsRemote { get; private set; }
        /// <summary>
        /// Headset off, app paused or link silent. An away player is not hunted, does not use oxygen and is
        /// not waited for at extraction. Always false offline.
        /// </summary>
        public bool IsAway { get; set; }
        /// <summary>How hard a remote player is working (0..1), as last reported. The local rig reads its own locomotion instead.</summary>
        public float RemoteExertion { get; set; }

        // ------------------------------------------------------------------ co-op revive

        /// <summary>
        /// Co-op only: caught by the spider and waiting for the crew to revive them. True on every headset, for the local
        /// player while they are downed and for a stand-in whose player is downed. A downed player cannot move, grab, use
        /// tools, press buttons, shove or be shoved, is not hunted, and does not count as "up" for the all-aboard rule.
        /// Always false offline. Set only through <see cref="SetDowned"/>.
        /// </summary>
        public bool IsDowned { get; private set; }
        /// <summary>Where this player was downed (their head when the spider caught them). Meaningless while up.</summary>
        public Vector3 DownedPosition { get; private set; }
        /// <summary>How far their revive ring is, 0..1 (the host's figure). 0 while up.</summary>
        public float ReviveProgress { get; set; }
        /// <summary>Who is holding their beacon right now (the host's word), or <see cref="PlayerId.None"/>.</summary>
        public PlayerId ReviveRescuer { get; set; } = PlayerId.None;

        /// <summary>This player went down (true) or was revived (false).</summary>
        public event Action<PlayerContext, bool> DownedChanged;
        /// <summary>Any player went down or came back up (for systems that do not hold a reference to each player).</summary>
        public static event Action<PlayerContext, bool> AnyDownedChanged;

        /// <summary>
        /// Downs this player at <paramref name="position"/> or brings them back up. For the local rig this also locks
        /// movement, grabbing, tools and buttons and dims the view (<see cref="DownedLock"/>); a stand-in's suit slumps
        /// (RemoteAvatar). Returns false when nothing changed.
        /// </summary>
        public bool SetDowned(bool downed, Vector3 position)
        {
            if (IsDowned == downed && (!downed || DownedPosition == position)) return false;
            bool changed = IsDowned != downed;
            IsDowned = downed;
            if (downed) DownedPosition = position;
            ReviveProgress = 0f;
            ReviveRescuer = PlayerId.None;
            if (!IsRemote) DownedLock.For(this).Set(downed);
            if (!changed) return true;
            DownedChanged?.Invoke(this, downed);
            AnyDownedChanged?.Invoke(this, downed);
            return true;
        }

        Vector3 m_RemoteHead;
        bool m_HasRemoteHead;

        /// <summary>
        /// Gives this player its place in the crew. Offline the one rig keeps <see cref="PlayerId.Local"/>.
        /// In a session the host assigns slots; call this on a stand-in before its first Start so the
        /// director registers it as remote.
        /// </summary>
        public void AssignId(PlayerId id, bool remote)
        {
            m_PlayerIndex = id.Value;
            IsRemote = remote;
            if (!remote)
            {
                m_HasRemoteHead = false;
                IsAway = false;
                RemoteExertion = 0f;
            }
            // A new place in a crew (or back to single-player) starts up.
            if (IsDowned) SetDowned(false, DownedPosition);
        }

        /// <summary>The newest head position received for a remote player. Ignored for the local rig.</summary>
        public void SetRemoteHead(Vector3 position)
        {
            m_RemoteHead = position;
            m_HasRemoteHead = true;
        }
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

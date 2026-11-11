using System;
using System.Collections;
using System.Collections.Generic;
using TrashAstronauts.Core;
using TrashAstronauts.World;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Climbing;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// The single place that moves the player. All movement (handrail pulling, teleport, snap turn,
    /// unstuck, return-to-tug) is queued through XRI's LocomotionMediator / XRBodyTransformer, which
    /// moves the XR Origin and never touches the tracked camera, so head tracking is always preserved.
    /// </summary>
    public sealed class PlayerLocomotion : MonoBehaviour
    {
        public XROrigin origin;
        public TeleportationProvider teleportation;
        public SnapTurnProvider snapTurn;
        public ClimbProvider climb;
        [Tooltip("Transform between the origin and the camera offset. Raised for seated play.")]
        public Transform heightAdjust;
        public BodyAnchor bodyAnchor;

        /// <summary>Raised whenever the player completes any artificial movement (rail, teleport).</summary>
        public event Action Moved;

        public float SeatedHeightOffset => heightAdjust != null ? heightAdjust.localPosition.y : 0f;

        void OnEnable()
        {
            if (teleportation != null) teleportation.locomotionEnded += OnLocomotionEnded;
            if (climb != null) climb.locomotionEnded += OnLocomotionEnded;
            if (snapTurn != null) snapTurn.locomotionEnded += OnTurnEnded;
        }

        void OnDisable()
        {
            if (teleportation != null) teleportation.locomotionEnded -= OnLocomotionEnded;
            if (climb != null) climb.locomotionEnded -= OnLocomotionEnded;
            if (snapTurn != null) snapTurn.locomotionEnded -= OnTurnEnded;
        }

        void OnLocomotionEnded(LocomotionProvider provider) => Moved?.Invoke();

        void OnTurnEnded(LocomotionProvider provider)
        {
            if (bodyAnchor != null) bodyAnchor.SnapToHead();
        }

        public void ApplyComfort(SaveData data)
        {
            if (data == null) return;
            if (snapTurn != null) snapTurn.turnAmount = data.snapTurnDegrees;
            SetHeightOffset(data.seatedHeightOffset);
        }

        public void SetHeightOffset(float offset)
        {
            if (heightAdjust == null) return;
            offset = Mathf.Clamp(offset, SaveData.MinHeightOffset, SaveData.MaxHeightOffset);
            heightAdjust.localPosition = new Vector3(0f, offset, 0f);
        }

        /// <summary>Teleports so the player's feet land on <paramref name="feetPosition"/>, keeping their current heading.</summary>
        public bool TeleportTo(Vector3 feetPosition)
        {
            if (teleportation == null) return false;
            return teleportation.QueueTeleportRequest(new TeleportRequest
            {
                destinationPosition = feetPosition,
                destinationRotation = Quaternion.identity,
                matchOrientation = MatchOrientation.WorldSpaceUp,
                requestTime = Time.time,
            });
        }

        /// <summary>Teleports to a pose and turns the player to face along it (used when returning to the tug).</summary>
        public bool TeleportTo(Vector3 feetPosition, Quaternion facing)
        {
            if (teleportation == null) return false;
            if (bodyAnchor != null) bodyAnchor.SnapToHead();
            return teleportation.QueueTeleportRequest(new TeleportRequest
            {
                destinationPosition = feetPosition,
                destinationRotation = Quaternion.Euler(0f, facing.eulerAngles.y, 0f),
                matchOrientation = MatchOrientation.TargetUpAndForward,
                requestTime = Time.time,
            });
        }

        /// <summary>
        /// Unstuck: moves the player to the nearest authored recovery anchor. Anchors are placed in open
        /// floor space in every room, so this always lands somewhere valid.
        /// </summary>
        public bool Unstuck()
        {
            Vector3 from = origin != null && origin.Camera != null ? origin.Camera.transform.position : transform.position;
            var anchor = RecoveryAnchor.FindNearest(from);
            return anchor != null && TeleportTo(anchor.transform.position);
        }

        /// <summary>Fade out, move to a pose, fade back in. No visible motion, no forced rotation while visible.</summary>
        public Coroutine FadeTeleport(ScreenFader fader, Transform destination, Action afterMove = null)
        {
            return StartCoroutine(FadeTeleportRoutine(fader, destination, afterMove));
        }

        IEnumerator FadeTeleportRoutine(ScreenFader fader, Transform destination, Action afterMove)
        {
            if (fader != null) yield return fader.FadeOut();
            if (destination != null) TeleportTo(destination.position, destination.rotation);
            // Give the mediator a frame to apply the queued move before revealing the view again.
            yield return null;
            yield return null;
            afterMove?.Invoke();
            yield return new WaitForSecondsRealtime(0.35f);
            if (fader != null) yield return fader.FadeIn(0.6f);
        }
    }
}

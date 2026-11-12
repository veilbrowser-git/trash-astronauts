using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Player;
using TrashAstronauts.Tools;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// Reads the local rig into an <see cref="AvatarSnapshot"/>: head, both hands, finger curls, tool flags and
    /// exertion. NetSession calls it from its LateUpdate (execution order 300), after XRI, hand tracking and the
    /// suit have written this frame's poses, and just before the 20 Hz send tick. Allocates nothing.
    ///
    /// Only poses and a few state bits are read. Nothing from the microphone or the voice sensor goes in here,
    /// and AvatarSnapshot has no field that could carry it.
    /// </summary>
    public static class AvatarSampler
    {
        /// <summary>Tool ids for <see cref="AvatarSnapshot.leftTool"/> / rightTool (0 = empty hand).</summary>
        public const byte NoTool = 0, Flashlight = 1, Scanner = 2, Cutter = 3, Tranq = 4;

        /// <summary>Fills <paramref name="snapshot"/> from <paramref name="player"/>. The Away bit is left to the session.</summary>
        public static void Sample(PlayerContext player, ref AvatarSnapshot snapshot)
        {
            if (player == null) return;
            Transform head = player.head != null ? player.head : player.transform;
            snapshot.headPosition = head.position.ToNet();
            snapshot.headRotation = head.rotation.ToNet();

            byte flags = 0;
            SampleHand(player.leftHand, head, ref snapshot.leftHandPosition, ref snapshot.leftHandRotation, ref snapshot.leftIndexCurl, ref snapshot.leftGripCurl);
            SampleHand(player.rightHand, head, ref snapshot.rightHandPosition, ref snapshot.rightHandRotation, ref snapshot.rightIndexCurl, ref snapshot.rightGripCurl);
            if (Thrusting(player.leftHand)) flags |= AvatarFlags.ThrusterLeft;
            if (Thrusting(player.rightHand)) flags |= AvatarFlags.ThrusterRight;
            if (player.Handedness == Handedness.Left) flags |= AvatarFlags.LeftHanded;

            snapshot.leftTool = NoTool;
            snapshot.rightTool = NoTool;
            if (player.tools != null)
            {
                for (int i = 0; i < player.tools.Length; i++)
                {
                    SuitTool tool = player.tools[i];
                    if (tool == null) continue;
                    if (tool is FlashlightTool light && light.IsOn) flags |= AvatarFlags.FlashlightOn;
                    if (tool is PlasmaCutterTool cutter)
                    {
                        if (cutter.IsFiring) flags |= AvatarFlags.CutterFiring;
                        if (cutter.IsCutting) flags |= AvatarFlags.Cutting;
                    }
                    if (!tool.IsHeld || tool.HoldingHand == null) continue;
                    byte id = ToolId(tool);
                    if (tool.HoldingHand == player.leftHand) snapshot.leftTool = id;
                    else if (tool.HoldingHand == player.rightHand) snapshot.rightTool = id;
                }
            }

            if (HeadLamp.IsOnFor(player)) flags |= AvatarFlags.HeadLampOn;
            // The session owns the Away bit (SetAway); keep whatever it is not ours to set.
            snapshot.flags = (byte)(flags | (snapshot.flags & AvatarFlags.Away));
            var zeroG = player.locomotion != null ? player.locomotion.zeroG : null;
            snapshot.exertion = zeroG != null ? Mathf.Clamp01(zeroG.Exertion) : 0f;
        }

        static void SampleHand(HandRig hand, Transform head, ref NetVec3 position, ref NetQuat rotation, ref float index, ref float grip)
        {
            if (hand == null)
            {
                // No hand: hang it below the head so the codec's head-relative range is never exceeded.
                position = (head.position + Vector3.down * 0.6f).ToNet();
                rotation = head.rotation.ToNet();
                index = 0f;
                grip = 0f;
                return;
            }
            Vector3 at = hand.transform.position;
            // The codec sends hands as a head-relative offset of at most 32 m; a hand that far away is a glitch.
            if ((at - head.position).sqrMagnitude > 30f * 30f) at = head.position;
            position = at.ToNet();
            rotation = hand.transform.rotation.ToNet();
            index = Mathf.Clamp01(hand.Trigger);
            // Holding something closes the hand whatever the controller says, as it does on the local glove.
            grip = hand.IsHolding ? 1f : Mathf.Clamp01(hand.Grip);
        }

        static bool Thrusting(HandRig hand) => hand != null && hand.thrusterVisual != null && hand.thrusterVisual.activeSelf;

        static byte ToolId(SuitTool tool)
        {
            if (tool is FlashlightTool) return Flashlight;
            if (tool is ScannerTool) return Scanner;
            if (tool is PlasmaCutterTool) return Cutter;
            if (tool is TranqTool) return Tranq;
            return NoTool;
        }
    }
}

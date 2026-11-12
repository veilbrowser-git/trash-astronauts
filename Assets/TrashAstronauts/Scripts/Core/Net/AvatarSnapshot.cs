using System;

namespace TrashAstronauts.Core.Net
{
    /// <summary>Bits of <see cref="AvatarSnapshot.flags"/>.</summary>
    public static class AvatarFlags
    {
        public const byte ThrusterLeft = 1;
        public const byte ThrusterRight = 2;
        public const byte FlashlightOn = 4;
        public const byte CutterFiring = 8;
        public const byte Cutting = 16;
        public const byte Away = 32;
        public const byte LeftHanded = 64;
        /// <summary>The helmet lamp is switched on (bought in the store; HeadLamp on the local rig).</summary>
        public const byte HeadLampOn = 128;
    }

    /// <summary>
    /// One sample of a player's rig: head, both hands, finger curls, tool flags. 35 bytes on the wire, sent at
    /// 20 Hz client -> host and host -> the other clients. Hand positions are WORLD space here; the codec sends
    /// them as an offset from the head (world axes) in millimetres, so hands must stay within 32 m of the head.
    /// </summary>
    public struct AvatarSnapshot : ISnapshot<AvatarSnapshot>
    {
        /// <summary>Encoded size in bytes, without the message type byte.</summary>
        public const int Size = 35;
        /// <summary>Largest value of a held-tool id (4 bits). 0 means empty hand.</summary>
        public const byte MaxToolId = 15;

        public NetVec3 headPosition;
        public NetQuat headRotation;
        public NetVec3 leftHandPosition;
        public NetQuat leftHandRotation;
        public NetVec3 rightHandPosition;
        public NetQuat rightHandRotation;
        /// <summary>0..1, sent at 4 bits each.</summary>
        public float leftIndexCurl, leftGripCurl, rightIndexCurl, rightGripCurl;
        /// <summary><see cref="AvatarFlags"/>.</summary>
        public byte flags;
        public byte leftTool, rightTool;
        /// <summary>ZeroGLocomotion.Exertion, 0..1.</summary>
        public float exertion;

        public bool HasFlag(byte flag) => (flags & flag) != 0;
        public bool IsAway => (flags & AvatarFlags.Away) != 0;

        public static AvatarSnapshot Default
        {
            get
            {
                var s = new AvatarSnapshot();
                s.headRotation = NetQuat.Identity;
                s.leftHandRotation = NetQuat.Identity;
                s.rightHandRotation = NetQuat.Identity;
                return s;
            }
        }

        public void Write(ref NetWriter w)
        {
            Quant.WritePosCm(ref w, headPosition);
            Quant.WriteQuat(ref w, headRotation);
            Quant.WritePosMm(ref w, leftHandPosition - headPosition);
            Quant.WriteQuat(ref w, leftHandRotation);
            Quant.WritePosMm(ref w, rightHandPosition - headPosition);
            Quant.WriteQuat(ref w, rightHandRotation);
            w.U8((byte)(Quant.ToBits(leftIndexCurl, 4) | (Quant.ToBits(leftGripCurl, 4) << 4)));
            w.U8((byte)(Quant.ToBits(rightIndexCurl, 4) | (Quant.ToBits(rightGripCurl, 4) << 4)));
            w.U8(flags);
            w.U8((byte)((leftTool & 15) | ((rightTool & 15) << 4)));
            w.U8(Quant.ToByte(exertion));
        }

        public static bool Read(ref NetReader r, out AvatarSnapshot s)
        {
            s = new AvatarSnapshot();
            s.headPosition = Quant.ReadPosCm(ref r);
            s.headRotation = Quant.ReadQuat(ref r);
            s.leftHandPosition = s.headPosition + Quant.ReadPosMm(ref r);
            s.leftHandRotation = Quant.ReadQuat(ref r);
            s.rightHandPosition = s.headPosition + Quant.ReadPosMm(ref r);
            s.rightHandRotation = Quant.ReadQuat(ref r);
            byte left = r.U8();
            byte right = r.U8();
            s.leftIndexCurl = Quant.FromBits(left & 15, 4);
            s.leftGripCurl = Quant.FromBits(left >> 4, 4);
            s.rightIndexCurl = Quant.FromBits(right & 15, 4);
            s.rightGripCurl = Quant.FromBits(right >> 4, 4);
            s.flags = r.U8();
            byte tools = r.U8();
            s.leftTool = (byte)(tools & 15);
            s.rightTool = (byte)(tools >> 4);
            s.exertion = Quant.FromByte(r.U8());
            return !r.Failed;
        }

        public AvatarSnapshot Blend(in AvatarSnapshot next, float t)
        {
            float c = t < 0f ? 0f : t > 1f ? 1f : t;
            AvatarSnapshot s = c >= 0.5f ? next : this; // flags and tools switch at the midpoint
            s.headPosition = NetVec3.Lerp(headPosition, next.headPosition, t);
            s.headRotation = NetQuat.Nlerp(headRotation, next.headRotation, c);
            s.leftHandPosition = NetVec3.Lerp(leftHandPosition, next.leftHandPosition, t);
            s.leftHandRotation = NetQuat.Nlerp(leftHandRotation, next.leftHandRotation, c);
            s.rightHandPosition = NetVec3.Lerp(rightHandPosition, next.rightHandPosition, t);
            s.rightHandRotation = NetQuat.Nlerp(rightHandRotation, next.rightHandRotation, c);
            s.leftIndexCurl = leftIndexCurl + (next.leftIndexCurl - leftIndexCurl) * c;
            s.leftGripCurl = leftGripCurl + (next.leftGripCurl - leftGripCurl) * c;
            s.rightIndexCurl = rightIndexCurl + (next.rightIndexCurl - rightIndexCurl) * c;
            s.rightGripCurl = rightGripCurl + (next.rightGripCurl - rightGripCurl) * c;
            s.exertion = exertion + (next.exertion - exertion) * c;
            return s;
        }
    }
}

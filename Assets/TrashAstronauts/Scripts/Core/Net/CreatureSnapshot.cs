using System;

namespace TrashAstronauts.Core.Net
{
    /// <summary>Bits of <see cref="CreatureSnapshot.flags"/>.</summary>
    public static class CreatureFlags
    {
        public const byte Running = 1;
        public const byte Sedated = 2;
        public const byte Lurking = 4;
        public const byte SeesQuarry = 8;
        public const byte WallSide = 16;
        /// <summary>Flips every time the creature hears a noise (drives LastHeardNoiseTime on replicas).</summary>
        public const byte HeardNoiseToggle = 32;
        public const byte Scaring = 64;
    }

    /// <summary>
    /// One sample of the creature, host -> clients at 10 Hz (20 Hz in Investigate/Chase). 11 bytes on the wire
    /// (12 with the message type).
    /// </summary>
    public struct CreatureSnapshot : ISnapshot<CreatureSnapshot>
    {
        /// <summary>Encoded size in bytes, without the message type byte.</summary>
        public const int Size = 11;
        public const float MaxSedationSeconds = 255f;

        public NetVec3 position;
        /// <summary>Heading about +Y in degrees. One byte on the wire.</summary>
        public float yawDegrees;
        public EnemyState state;
        /// <summary>Session slot 0..3 of the quarry, or <see cref="NetProtocol.NoSlot"/>.</summary>
        public byte targetSlot;
        /// <summary>SurfaceWalker.TargetClimb in halves: 0 = floor, 1 = wall (0.5), 2 = ceiling (1.0).</summary>
        public byte climbStep;
        /// <summary><see cref="CreatureFlags"/>.</summary>
        public byte flags;
        /// <summary>EnemyBrain.Suspicion 0..1, 4 bits on the wire.</summary>
        public float suspicion;
        /// <summary>Whole seconds on the wire (rounded up), capped at 255.</summary>
        public float sedationSecondsLeft;

        public bool HasFlag(byte flag) => (flags & flag) != 0;
        public float TargetClimb => climbStep * 0.5f;

        public static byte ClimbToStep(float targetClimb)
        {
            return targetClimb < 0.25f ? (byte)0 : targetClimb < 0.75f ? (byte)1 : (byte)2;
        }

        public void Write(ref NetWriter w)
        {
            Quant.WritePosCm(ref w, position);
            w.U8(Quant.PackYaw(yawDegrees));
            int s = (int)state;
            if (s < 0 || s > 7) s = 0;
            int target = targetSlot <= 6 ? targetSlot : 7;
            int climb = climbStep <= 3 ? climbStep : 3;
            w.U8((byte)(s | (target << 3) | (climb << 6)));
            w.U8((byte)(flags & 0x7F));
            w.U8(Quant.ToBits(suspicion, 4));
            float sedation = sedationSecondsLeft;
            w.U8(!(sedation > 0f) ? (byte)0 : sedation >= MaxSedationSeconds ? (byte)255 : (byte)Math.Ceiling(sedation));
        }

        public static bool Read(ref NetReader r, out CreatureSnapshot s)
        {
            s = new CreatureSnapshot();
            s.position = Quant.ReadPosCm(ref r);
            s.yawDegrees = Quant.UnpackYaw(r.U8());
            byte packed = r.U8();
            int state = packed & 7;
            if (state > (int)EnemyState.Search) r.Fail();
            s.state = (EnemyState)state;
            int target = (packed >> 3) & 7;
            s.targetSlot = target == 7 ? NetProtocol.NoSlot : (byte)target;
            s.climbStep = (byte)(packed >> 6);
            s.flags = (byte)(r.U8() & 0x7F);
            s.suspicion = Quant.FromBits(r.U8() & 15, 4);
            s.sedationSecondsLeft = r.U8();
            return !r.Failed;
        }

        public CreatureSnapshot Blend(in CreatureSnapshot next, float t)
        {
            float c = t < 0f ? 0f : t > 1f ? 1f : t;
            CreatureSnapshot s = c >= 0.5f ? next : this;
            s.position = NetVec3.Lerp(position, next.position, t);
            float delta = (next.yawDegrees - yawDegrees) % 360f;
            if (delta > 180f) delta -= 360f;
            else if (delta < -180f) delta += 360f;
            float yaw = (yawDegrees + delta * c) % 360f;
            s.yawDegrees = yaw < 0f ? yaw + 360f : yaw;
            s.suspicion = suspicion + (next.suspicion - suspicion) * c;
            return s;
        }
    }
}

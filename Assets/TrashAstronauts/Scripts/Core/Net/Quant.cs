using System;

namespace TrashAstronauts.Core.Net
{
    /// <summary>Plain 3-vector for the net layer (the Core.Net code has no UnityEngine or System.Numerics types in its API).</summary>
    public struct NetVec3 : IEquatable<NetVec3>
    {
        public float x, y, z;

        public NetVec3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }

        public static NetVec3 Zero => new NetVec3(0f, 0f, 0f);

        public static NetVec3 operator +(NetVec3 a, NetVec3 b) => new NetVec3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static NetVec3 operator -(NetVec3 a, NetVec3 b) => new NetVec3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static NetVec3 operator *(NetVec3 a, float s) => new NetVec3(a.x * s, a.y * s, a.z * s);

        public float SqrMagnitude => x * x + y * y + z * z;
        public float Magnitude => (float)Math.Sqrt(x * x + y * y + z * z);

        public static float Distance(NetVec3 a, NetVec3 b) => (a - b).Magnitude;

        /// <summary>Linear blend; t is NOT clamped, so t &gt; 1 extrapolates.</summary>
        public static NetVec3 Lerp(NetVec3 a, NetVec3 b, float t) => new NetVec3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);

        public bool Equals(NetVec3 other) => x == other.x && y == other.y && z == other.z;
        public override bool Equals(object obj) => obj is NetVec3 other && Equals(other);
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() * 397) ^ (z.GetHashCode() * 7919);
        public override string ToString() => "(" + x.ToString("0.###") + ", " + y.ToString("0.###") + ", " + z.ToString("0.###") + ")";
    }

    /// <summary>Plain quaternion for the net layer. Same component order as UnityEngine.Quaternion (x, y, z, w).</summary>
    public struct NetQuat : IEquatable<NetQuat>
    {
        public float x, y, z, w;

        public NetQuat(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }

        public static NetQuat Identity => new NetQuat(0f, 0f, 0f, 1f);

        public NetQuat Normalized
        {
            get
            {
                float m = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
                if (!(m > 1e-12f) || float.IsInfinity(m)) return Identity;
                float inv = 1f / m;
                return new NetQuat(x * inv, y * inv, z * inv, w * inv);
            }
        }

        public static float Dot(NetQuat a, NetQuat b) => a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;

        /// <summary>Angle in degrees between two rotations (0..180). Inputs need not be normalized.</summary>
        public static float AngleDegrees(NetQuat a, NetQuat b)
        {
            a = a.Normalized;
            b = b.Normalized;
            double d = Math.Abs((double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z + (double)a.w * b.w);
            if (d > 1.0) d = 1.0;
            return (float)(2.0 * Math.Acos(d) * 180.0 / Math.PI);
        }

        /// <summary>Normalized lerp along the shortest arc, t clamped to 0..1. Good enough at 20 Hz sample spacing.</summary>
        public static NetQuat Nlerp(NetQuat a, NetQuat b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            float sign = Dot(a, b) < 0f ? -1f : 1f;
            float s = 1f - t;
            return new NetQuat(a.x * s + b.x * sign * t, a.y * s + b.y * sign * t, a.z * s + b.z * sign * t, a.w * s + b.w * sign * t).Normalized;
        }

        /// <summary>Rotation of <paramref name="yawDegrees"/> about +Y (Unity convention).</summary>
        public static NetQuat FromYawDegrees(float yawDegrees)
        {
            double half = yawDegrees * Math.PI / 360.0;
            return new NetQuat(0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half));
        }

        public bool Equals(NetQuat other) => x == other.x && y == other.y && z == other.z && w == other.w;
        public override bool Equals(object obj) => obj is NetQuat other && Equals(other);
        public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() * 397) ^ (z.GetHashCode() * 7919) ^ (w.GetHashCode() * 104729);
        public override string ToString() => "(" + x.ToString("0.###") + ", " + y.ToString("0.###") + ", " + z.ToString("0.###") + ", " + w.ToString("0.###") + ")";
    }

    /// <summary>
    /// Wire quantization. Error bounds (pinned by NetCodecTests):
    ///  - PosCm: 3 x int16 centimetres, range +-327.67 m, error &lt;= 0.5 cm per axis.
    ///  - PosMm: 3 x int16 millimetres, range +-32.767 m, error &lt;= 0.5 mm per axis.
    ///  - Quat: smallest-three in 32 bits (2-bit index + 3 x 10 bits), error &lt; 0.25 degrees.
    ///  - Yaw: one byte, error &lt;= 0.71 degrees.
    /// Out-of-range and non-finite inputs are clamped, never thrown on.
    /// </summary>
    public static class Quant
    {
        public const float PosCmRange = 327.67f;
        public const float PosMmRange = 32.767f;
        public const float PosCmMaxError = 0.005f + 1e-4f;
        public const float PosMmMaxError = 0.0005f + 1e-5f;
        public const float QuatMaxErrorDegrees = 0.25f;
        public const float YawMaxErrorDegrees = 360f / 256f * 0.5f + 0.01f;

        // Components other than the largest lie in [-1/sqrt2, 1/sqrt2]. They are stored as a signed step count
        // -511..511 (offset by 511 into 10 bits), so 0 is exact and identity survives the trip unchanged.
        const float k_QuatScale = 511f / 0.70710678f;

        public static short ToInt16(float value, float scale)
        {
            if (float.IsNaN(value)) return 0;
            double v = Math.Round((double)value * scale);
            if (v > 32767.0) return 32767;
            if (v < -32767.0) return -32767;
            return (short)v;
        }

        public static byte ToByte(float value01)
        {
            if (!(value01 > 0f)) return 0;
            if (value01 >= 1f) return 255;
            return (byte)(value01 * 255f + 0.5f);
        }

        public static float FromByte(byte value) => value / 255f;

        /// <summary>0..1 into <paramref name="bits"/> bits (1..8).</summary>
        public static byte ToBits(float value01, int bits)
        {
            int max = (1 << bits) - 1;
            if (!(value01 > 0f)) return 0;
            if (value01 >= 1f) return (byte)max;
            return (byte)(value01 * max + 0.5f);
        }

        public static float FromBits(int value, int bits) => value / (float)((1 << bits) - 1);

        public static void WritePosCm(ref NetWriter w, NetVec3 p) { WriteVec16(ref w, p, 100f); }
        public static NetVec3 ReadPosCm(ref NetReader r) { return ReadVec16(ref r, 0.01f); }
        public static void WritePosMm(ref NetWriter w, NetVec3 p) { WriteVec16(ref w, p, 1000f); }
        public static NetVec3 ReadPosMm(ref NetReader r) { return ReadVec16(ref r, 0.001f); }

        /// <summary>Three int16 with an explicit scale (units per 1.0), used for velocities and impulses.</summary>
        public static void WriteVec16(ref NetWriter w, NetVec3 v, float scale)
        {
            w.I16(ToInt16(v.x, scale));
            w.I16(ToInt16(v.y, scale));
            w.I16(ToInt16(v.z, scale));
        }

        public static NetVec3 ReadVec16(ref NetReader r, float inverseScale)
        {
            float x = r.I16() * inverseScale;
            float y = r.I16() * inverseScale;
            float z = r.I16() * inverseScale;
            return new NetVec3(x, y, z);
        }

        /// <summary>Smallest-three: drop the largest component (2-bit index), send the other three at 10 bits each.</summary>
        public static uint PackQuat(NetQuat q)
        {
            q = q.Normalized;
            float ax = Math.Abs(q.x), ay = Math.Abs(q.y), az = Math.Abs(q.z), aw = Math.Abs(q.w);
            int largest = 3;
            float max = aw;
            if (ax > max) { largest = 0; max = ax; }
            if (ay > max) { largest = 1; max = ay; }
            if (az > max) { largest = 2; }

            float a, b, c, dropped;
            switch (largest)
            {
                case 0: dropped = q.x; a = q.y; b = q.z; c = q.w; break;
                case 1: dropped = q.y; a = q.x; b = q.z; c = q.w; break;
                case 2: dropped = q.z; a = q.x; b = q.y; c = q.w; break;
                default: dropped = q.w; a = q.x; b = q.y; c = q.z; break;
            }
            if (dropped < 0f) { a = -a; b = -b; c = -c; } // q and -q are the same rotation; keep the dropped one positive
            return ((uint)largest << 30) | (PackComponent(a) << 20) | (PackComponent(b) << 10) | PackComponent(c);
        }

        public static NetQuat UnpackQuat(uint packed)
        {
            int largest = (int)(packed >> 30);
            float a = UnpackComponent((packed >> 20) & 1023u);
            float b = UnpackComponent((packed >> 10) & 1023u);
            float c = UnpackComponent(packed & 1023u);
            float rest = 1f - a * a - b * b - c * c;
            float d = rest > 0f ? (float)Math.Sqrt(rest) : 0f;
            NetQuat q;
            switch (largest)
            {
                case 0: q = new NetQuat(d, a, b, c); break;
                case 1: q = new NetQuat(a, d, b, c); break;
                case 2: q = new NetQuat(a, b, d, c); break;
                default: q = new NetQuat(a, b, c, d); break;
            }
            return q.Normalized;
        }

        static uint PackComponent(float v)
        {
            double steps = Math.Round((double)v * k_QuatScale);
            if (!(steps > -511.0)) return 0u;
            if (steps >= 511.0) return 1022u;
            return (uint)((int)steps + 511);
        }

        static float UnpackComponent(uint v)
        {
            int steps = (int)v - 511;
            if (steps > 511) steps = 511;
            return steps / k_QuatScale;
        }

        public static void WriteQuat(ref NetWriter w, NetQuat q) { w.U32(PackQuat(q)); }
        public static NetQuat ReadQuat(ref NetReader r) { return UnpackQuat(r.U32()); }

        /// <summary>Any angle in degrees into one byte (wraps).</summary>
        public static byte PackYaw(float degrees)
        {
            if (float.IsNaN(degrees) || float.IsInfinity(degrees)) return 0;
            double turns = degrees / 360.0;
            turns -= Math.Floor(turns);
            return (byte)((int)(turns * 256.0 + 0.5) & 255);
        }

        /// <summary>Byte back to degrees in [0, 360).</summary>
        public static float UnpackYaw(byte packed) => packed * (360f / 256f);

        /// <summary>Smallest absolute difference between two angles in degrees (0..180).</summary>
        public static float YawDelta(float a, float b)
        {
            double d = (a - b) % 360.0;
            if (d < 0) d += 360.0;
            if (d > 180.0) d = 360.0 - d;
            return (float)d;
        }
    }
}

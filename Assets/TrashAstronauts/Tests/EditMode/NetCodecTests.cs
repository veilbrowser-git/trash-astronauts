using System;
using System.Collections.Generic;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Wire codec: every message of the protocol round-trips within its stated error bound and inside its size
    /// cap, and no reader ever throws on truncated or random input.
    /// </summary>
    public class NetCodecTests
    {
        delegate void WriteFn(ref NetWriter w);
        delegate bool ReadFn(ref NetReader r);

        sealed class Sample
        {
            public byte id;
            public WriteFn write;
            public ReadFn read;
        }

        readonly byte[] m_Buffer = new byte[2048];

        static NetVec3 V(float x, float y, float z) => new NetVec3(x, y, z);

        static NetQuat RandomQuat(Random random)
        {
            NetQuat q;
            do
            {
                q = new NetQuat((float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1));
            } while (NetQuat.Dot(q, q) < 0.01f);
            return q.Normalized;
        }

        static void AssertNear(NetVec3 expected, NetVec3 actual, float tolerance, string what)
        {
            Assert.LessOrEqual(Math.Abs(expected.x - actual.x), tolerance, what + ".x");
            Assert.LessOrEqual(Math.Abs(expected.y - actual.y), tolerance, what + ".y");
            Assert.LessOrEqual(Math.Abs(expected.z - actual.z), tolerance, what + ".z");
        }

        // ------------------------------------------------------------------ buffers

        [Test]
        public void WriterAndReader_RoundTripEveryPrimitive_LittleEndian()
        {
            var w = new NetWriter(m_Buffer);
            w.U8(0xAB);
            w.Bool(true);
            w.U16(0xBEEF);
            w.I16(-12345);
            w.U32(0xDEADBEEF);
            w.I32(int.MinValue);
            w.F32(-1234.5678f);
            w.Bytes(new byte[] { 1, 2, 3, 4, 5 }, 1, 3);
            Assert.IsFalse(w.Overflow);
            Assert.AreEqual(1 + 1 + 2 + 2 + 4 + 4 + 4 + 3, w.Position);
            Assert.AreEqual(0xEF, m_Buffer[2], "low byte first");
            Assert.AreEqual(0xBE, m_Buffer[3]);

            var r = new NetReader(m_Buffer, 0, w.Position);
            Assert.AreEqual(0xAB, r.U8());
            Assert.IsTrue(r.Bool());
            Assert.AreEqual(0xBEEF, r.U16());
            Assert.AreEqual(-12345, r.I16());
            Assert.AreEqual(0xDEADBEEF, r.U32());
            Assert.AreEqual(int.MinValue, r.I32());
            Assert.AreEqual(-1234.5678f, r.F32());
            var three = new byte[3];
            r.Bytes(three, 0, 3);
            CollectionAssert.AreEqual(new byte[] { 2, 3, 4 }, three);
            Assert.IsTrue(r.AtEnd);
            Assert.IsFalse(r.Failed);
        }

        [Test]
        public void Writer_PastTheLimit_SetsOverflow_AndNeverThrows()
        {
            var w = new NetWriter(m_Buffer, 0, 3);
            w.U16(1);
            Assert.IsFalse(w.Overflow);
            Assert.DoesNotThrow(() =>
            {
                var inner = new NetWriter(new byte[3]);
                inner.U16(1);
                inner.U32(2);
                inner.U8(3);
                inner.Bytes(null, 0, 4);
                inner.PatchU16(99, 1);
                Assert.IsTrue(inner.Overflow);
                Assert.AreEqual(2, inner.Position, "A write that does not fit writes nothing.");
            });
            w.U32(5);
            Assert.IsTrue(w.Overflow);
            Assert.AreEqual(2, w.Position);

            var none = new NetWriter(null);
            none.U8(1);
            Assert.IsTrue(none.Overflow);
        }

        [Test]
        public void Reader_PastTheEnd_FailsAndReturnsZero_AndNeverThrows()
        {
            m_Buffer[0] = 7;
            m_Buffer[1] = 8;
            var r = new NetReader(m_Buffer, 0, 2);
            Assert.AreEqual(7, r.U8());
            Assert.AreEqual(0u, r.U32(), "Not enough bytes left.");
            Assert.IsTrue(r.Failed);
            Assert.AreEqual(0, r.U8(), "A failed reader stays failed.");
            Assert.AreEqual(0f, r.F32());
            r.Skip(100);
            r.Bytes(new byte[4], 0, 4);
            Assert.IsTrue(r.Failed);

            Assert.IsTrue(new NetReader(null, 0, 4).Failed);
            Assert.IsTrue(new NetReader(m_Buffer, -1, 4).Failed);
            Assert.IsTrue(new NetReader(m_Buffer, 0, m_Buffer.Length + 1).Failed);
            Assert.IsTrue(new NetReader(m_Buffer, m_Buffer.Length, 1).Failed);
        }

        [Test]
        public void Reader_RejectsNaNAndInfinity()
        {
            var w = new NetWriter(m_Buffer);
            w.F32(float.NaN);
            w.F32(float.PositiveInfinity);
            var r = new NetReader(m_Buffer, 0, 4);
            Assert.AreEqual(0f, r.F32());
            Assert.IsTrue(r.Failed);
            var r2 = new NetReader(m_Buffer, 4, 4);
            r2.F32();
            Assert.IsTrue(r2.Failed);
        }

        // ------------------------------------------------------------------ quantization

        [Test]
        public void Positions_RoundTripWithinHalfACentimetre_AndHalfAMillimetre()
        {
            var random = new Random(11);
            for (int i = 0; i < 2000; i++)
            {
                var p = V((float)(random.NextDouble() * 600 - 300), (float)(random.NextDouble() * 600 - 300), (float)(random.NextDouble() * 600 - 300));
                var w = new NetWriter(m_Buffer);
                Quant.WritePosCm(ref w, p);
                Assert.AreEqual(6, w.Position);
                var r = new NetReader(m_Buffer, 0, 6);
                AssertNear(p, Quant.ReadPosCm(ref r), Quant.PosCmMaxError, "cm");

                var h = V((float)(random.NextDouble() * 60 - 30), (float)(random.NextDouble() * 60 - 30), (float)(random.NextDouble() * 60 - 30));
                w = new NetWriter(m_Buffer);
                Quant.WritePosMm(ref w, h);
                r = new NetReader(m_Buffer, 0, 6);
                AssertNear(h, Quant.ReadPosMm(ref r), Quant.PosMmMaxError, "mm");
            }
        }

        [Test]
        public void Positions_OutOfRangeAndNonFinite_AreClamped_NotWrapped()
        {
            var w = new NetWriter(m_Buffer);
            Quant.WritePosCm(ref w, V(1e9f, -1e9f, float.NaN));
            Quant.WritePosCm(ref w, V(float.PositiveInfinity, float.NegativeInfinity, 400f));
            var r = new NetReader(m_Buffer, 0, 12);
            NetVec3 a = Quant.ReadPosCm(ref r);
            NetVec3 b = Quant.ReadPosCm(ref r);
            Assert.AreEqual(Quant.PosCmRange, a.x, 1e-3f);
            Assert.AreEqual(-Quant.PosCmRange, a.y, 1e-3f);
            Assert.AreEqual(0f, a.z);
            Assert.AreEqual(Quant.PosCmRange, b.x, 1e-3f);
            Assert.AreEqual(-Quant.PosCmRange, b.y, 1e-3f);
            Assert.AreEqual(Quant.PosCmRange, b.z, 1e-3f);
        }

        [Test]
        public void Rotations_SmallestThree_RoundTripWithinAQuarterDegree_In32Bits()
        {
            var random = new Random(5);
            float worst = 0f;
            for (int i = 0; i < 5000; i++)
            {
                NetQuat q = RandomQuat(random);
                NetQuat back = Quant.UnpackQuat(Quant.PackQuat(q));
                float error = NetQuat.AngleDegrees(q, back);
                if (error > worst) worst = error;
            }
            Assert.Less(worst, Quant.QuatMaxErrorDegrees, "worst error over 5000 random rotations");

            // axis-aligned and degenerate inputs
            foreach (var q in new[] { NetQuat.Identity, new NetQuat(1, 0, 0, 0), new NetQuat(0, 1, 0, 0), new NetQuat(0, 0, 1, 0), new NetQuat(0, 0, 0, -1), new NetQuat(0.5f, 0.5f, 0.5f, 0.5f), new NetQuat(-0.5f, 0.5f, -0.5f, 0.5f) })
                Assert.Less(NetQuat.AngleDegrees(q, Quant.UnpackQuat(Quant.PackQuat(q))), Quant.QuatMaxErrorDegrees);
            Assert.Less(NetQuat.AngleDegrees(NetQuat.Identity, Quant.UnpackQuat(Quant.PackQuat(new NetQuat(0, 0, 0, 0)))), 0.01f, "A zero quaternion is sent as identity.");
            Assert.Less(NetQuat.AngleDegrees(NetQuat.Identity, Quant.UnpackQuat(Quant.PackQuat(new NetQuat(float.NaN, 0, 0, 1)))), 0.01f, "NaN is sent as identity.");
            Assert.Less(NetQuat.AngleDegrees(new NetQuat(0, 0, 0, 1), Quant.UnpackQuat(Quant.PackQuat(new NetQuat(0, 0, 0, 25f)))), 0.01f, "Unnormalized input is normalized.");
            for (uint bits = 0; bits < 4000; bits++)
            {
                NetQuat any = Quant.UnpackQuat(bits * 1073807u);
                Assert.AreEqual(1f, NetQuat.Dot(any, any), 1e-3f, "Any 32 bits decode to a unit quaternion.");
            }
        }

        [Test]
        public void Yaw_FitsOneByte_WithinPointSevenDegrees_AndWraps()
        {
            for (float degrees = -720f; degrees <= 720f; degrees += 0.37f)
            {
                float back = Quant.UnpackYaw(Quant.PackYaw(degrees));
                Assert.LessOrEqual(Quant.YawDelta(degrees, back), Quant.YawMaxErrorDegrees, "yaw " + degrees);
            }
            Assert.AreEqual(0, Quant.PackYaw(float.NaN));
            Assert.AreEqual(Quant.PackYaw(10f), Quant.PackYaw(370f));
        }

        // ------------------------------------------------------------------ snapshots

        static AvatarSnapshot RandomAvatar(Random random)
        {
            var a = new AvatarSnapshot();
            a.headPosition = V((float)(random.NextDouble() * 200 - 100), (float)(random.NextDouble() * 20 - 10), (float)(random.NextDouble() * 200 - 100));
            a.headRotation = RandomQuat(random);
            a.leftHandPosition = a.headPosition + V((float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1));
            a.leftHandRotation = RandomQuat(random);
            a.rightHandPosition = a.headPosition + V((float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1), (float)(random.NextDouble() * 2 - 1));
            a.rightHandRotation = RandomQuat(random);
            a.leftIndexCurl = (float)random.NextDouble();
            a.leftGripCurl = (float)random.NextDouble();
            a.rightIndexCurl = (float)random.NextDouble();
            a.rightGripCurl = (float)random.NextDouble();
            a.flags = (byte)random.Next(0, 128);
            a.leftTool = (byte)random.Next(0, 16);
            a.rightTool = (byte)random.Next(0, 16);
            a.exertion = (float)random.NextDouble();
            return a;
        }

        [Test]
        public void AvatarState_Is35Bytes_AndRoundTripsWithinBounds()
        {
            var random = new Random(3);
            for (int i = 0; i < 500; i++)
            {
                AvatarSnapshot a = RandomAvatar(random);
                var w = new NetWriter(m_Buffer);
                a.Write(ref w);
                Assert.AreEqual(AvatarSnapshot.Size, w.Position);
                Assert.AreEqual(35, w.Position, "The plan's AvatarState size.");
                var r = new NetReader(m_Buffer, 0, w.Position);
                Assert.IsTrue(AvatarSnapshot.Read(ref r, out AvatarSnapshot b));
                Assert.IsTrue(r.AtEnd);

                AssertNear(a.headPosition, b.headPosition, Quant.PosCmMaxError, "head");
                // hands travel as millimetre offsets from the (centimetre) head
                AssertNear(a.leftHandPosition, b.leftHandPosition, Quant.PosCmMaxError + Quant.PosMmMaxError, "left hand");
                AssertNear(a.rightHandPosition, b.rightHandPosition, Quant.PosCmMaxError + Quant.PosMmMaxError, "right hand");
                AssertNear(a.leftHandPosition - a.headPosition, b.leftHandPosition - b.headPosition, Quant.PosMmMaxError + 2e-5f, "left hand relative to head");
                Assert.Less(NetQuat.AngleDegrees(a.headRotation, b.headRotation), Quant.QuatMaxErrorDegrees);
                Assert.Less(NetQuat.AngleDegrees(a.leftHandRotation, b.leftHandRotation), Quant.QuatMaxErrorDegrees);
                Assert.Less(NetQuat.AngleDegrees(a.rightHandRotation, b.rightHandRotation), Quant.QuatMaxErrorDegrees);
                Assert.AreEqual(a.leftIndexCurl, b.leftIndexCurl, 1f / 30f + 1e-4f);
                Assert.AreEqual(a.leftGripCurl, b.leftGripCurl, 1f / 30f + 1e-4f);
                Assert.AreEqual(a.rightIndexCurl, b.rightIndexCurl, 1f / 30f + 1e-4f);
                Assert.AreEqual(a.rightGripCurl, b.rightGripCurl, 1f / 30f + 1e-4f);
                Assert.AreEqual(a.flags, b.flags);
                Assert.AreEqual(a.leftTool, b.leftTool);
                Assert.AreEqual(a.rightTool, b.rightTool);
                Assert.AreEqual(a.exertion, b.exertion, 1f / 510f + 1e-4f);
            }
        }

        [Test]
        public void AvatarState_GripOfOneAndZero_AreExact()
        {
            var a = AvatarSnapshot.Default;
            a.leftGripCurl = 1f;
            a.rightIndexCurl = 1f;
            a.exertion = 1f;
            a.flags = AvatarFlags.Away | AvatarFlags.LeftHanded | AvatarFlags.ThrusterLeft;
            var w = new NetWriter(m_Buffer);
            a.Write(ref w);
            var r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(AvatarSnapshot.Read(ref r, out AvatarSnapshot b));
            Assert.AreEqual(1f, b.leftGripCurl);
            Assert.AreEqual(0f, b.leftIndexCurl);
            Assert.AreEqual(1f, b.rightIndexCurl);
            Assert.AreEqual(1f, b.exertion);
            Assert.IsTrue(b.IsAway);
            Assert.IsTrue(b.HasFlag(AvatarFlags.LeftHanded));
            Assert.IsFalse(b.HasFlag(AvatarFlags.CutterFiring));
        }

        [Test]
        public void CreatureState_Is11Bytes_AndRoundTripsWithinBounds()
        {
            var random = new Random(8);
            for (int i = 0; i < 500; i++)
            {
                var c = new CreatureSnapshot
                {
                    position = V((float)(random.NextDouble() * 200 - 100), (float)(random.NextDouble() * 20 - 10), (float)(random.NextDouble() * 200 - 100)),
                    yawDegrees = (float)(random.NextDouble() * 360),
                    state = (EnemyState)random.Next(0, 5),
                    targetSlot = random.Next(0, 5) == 4 ? NetProtocol.NoSlot : (byte)random.Next(0, 4),
                    climbStep = (byte)random.Next(0, 3),
                    flags = (byte)random.Next(0, 128),
                    suspicion = (float)random.NextDouble(),
                    sedationSecondsLeft = (float)(random.NextDouble() * 40),
                };
                var w = new NetWriter(m_Buffer);
                c.Write(ref w);
                Assert.AreEqual(CreatureSnapshot.Size, w.Position);
                Assert.AreEqual(11, w.Position, "12 bytes with the message type, as planned.");
                var r = new NetReader(m_Buffer, 0, w.Position);
                Assert.IsTrue(CreatureSnapshot.Read(ref r, out CreatureSnapshot d));
                AssertNear(c.position, d.position, Quant.PosCmMaxError, "creature");
                Assert.LessOrEqual(Quant.YawDelta(c.yawDegrees, d.yawDegrees), Quant.YawMaxErrorDegrees);
                Assert.AreEqual(c.state, d.state);
                Assert.AreEqual(c.targetSlot, d.targetSlot);
                Assert.AreEqual(c.climbStep, d.climbStep);
                Assert.AreEqual(c.flags, d.flags);
                Assert.AreEqual(c.suspicion, d.suspicion, 1f / 30f + 1e-4f);
                Assert.GreaterOrEqual(d.sedationSecondsLeft, c.sedationSecondsLeft, "Sedation rounds up, so a replica never wakes early.");
                Assert.Less(d.sedationSecondsLeft - c.sedationSecondsLeft, 1f + 1e-4f);
            }
            Assert.AreEqual(0, CreatureSnapshot.ClimbToStep(0f));
            Assert.AreEqual(1, CreatureSnapshot.ClimbToStep(0.5f));
            Assert.AreEqual(2, CreatureSnapshot.ClimbToStep(1f));
            Assert.AreEqual(0.5f, new CreatureSnapshot { climbStep = 1 }.TargetClimb);
        }

        [Test]
        public void CreatureState_WithAnUnknownEnemyState_IsRejected()
        {
            var w = new NetWriter(m_Buffer);
            new CreatureSnapshot { state = EnemyState.Chase }.Write(ref w);
            m_Buffer[7] = (byte)((m_Buffer[7] & ~7) | 6); // state 6 does not exist
            var r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsFalse(CreatureSnapshot.Read(ref r, out _));
        }

        // ------------------------------------------------------------------ snapshot buffer

        [Test]
        public void SnapshotBuffer_Renders100MsBehind_BetweenTwoSamples()
        {
            var buffer = new SnapshotBuffer<PoseSnapshot>();
            for (int i = 0; i <= 10; i++) buffer.Push(i * 0.05, new PoseSnapshot(V(i * 0.05f * 2f, 0, 0), NetQuat.Identity)); // moving at 2 m/s
            double now = 0.5;
            Assert.AreEqual(0.4, SnapshotBuffer<PoseSnapshot>.RenderTime(now), 1e-9, "100 ms interpolation delay");
            Assert.IsTrue(buffer.Sample(SnapshotBuffer<PoseSnapshot>.RenderTime(now), out PoseSnapshot at));
            Assert.AreEqual(0.8f, at.position.x, 1e-4f);
            Assert.IsTrue(buffer.Sample(0.425, out at));
            Assert.AreEqual(0.85f, at.position.x, 1e-4f, "halfway between the samples at 0.40 and 0.45");
            Assert.AreEqual(0, buffer.SecondsStale(0.4), 1e-9);
        }

        [Test]
        public void SnapshotBuffer_Extrapolates_AtMost250Ms_ThenHolds()
        {
            var buffer = new SnapshotBuffer<PoseSnapshot>();
            buffer.Push(1.00, new PoseSnapshot(V(0, 0, 0), NetQuat.Identity));
            buffer.Push(1.05, new PoseSnapshot(V(0.1f, 0, 0), NetQuat.Identity)); // 2 m/s
            Assert.IsTrue(buffer.Sample(1.15, out PoseSnapshot at));
            Assert.AreEqual(0.3f, at.position.x, 1e-4f, "100 ms past the newest sample, still moving");
            Assert.IsTrue(buffer.Sample(1.30, out at));
            Assert.AreEqual(0.6f, at.position.x, 1e-4f, "exactly at the 250 ms cap");
            Assert.IsTrue(buffer.Sample(5.0, out at));
            Assert.AreEqual(0.6f, at.position.x, 1e-4f, "past the cap it holds still");
            Assert.AreEqual(SnapshotBuffer<PoseSnapshot>.ExtrapolationCap, 0.25, 1e-12);
            Assert.Greater(buffer.SecondsStale(5.0), 3.9);
        }

        [Test]
        public void SnapshotBuffer_TwoSamplesBunchedClose_AreNotUsedAsAVelocity_AndExtrapolationIsBoundedByTheirSpan()
        {
            // A Wi-Fi hiccup delivered two samples 11 ms apart that carry 50 ms of motion (0.1 m).
            var buffer = new SnapshotBuffer<PoseSnapshot>();
            buffer.Push(1.000, new PoseSnapshot(V(0, 0, 0), NetQuat.Identity));
            buffer.Push(1.011, new PoseSnapshot(V(0.1f, 0, 0), NetQuat.Identity));
            Assert.IsTrue(buffer.Sample(1.2, out PoseSnapshot at));
            Assert.AreEqual(0.1f, at.position.x, 1e-4f, "too close together to be a speed: hold the newest, do not fly off at 9 m/s");

            // A 40 ms pair may extrapolate, but never more than the ratio times its own span (and never past 250 ms).
            buffer.Clear();
            buffer.Push(2.00, new PoseSnapshot(V(0, 0, 0), NetQuat.Identity));
            buffer.Push(2.04, new PoseSnapshot(V(0.04f, 0, 0), NetQuat.Identity)); // 1 m/s
            Assert.IsTrue(buffer.Sample(5.0, out at));
            double maxAhead = System.Math.Min(SnapshotBuffer<PoseSnapshot>.ExtrapolationCap, 0.04 * SnapshotBuffer<PoseSnapshot>.MaxExtrapolationRatio);
            Assert.AreEqual(0.04f + (float)maxAhead, at.position.x, 1e-4f);
            Assert.LessOrEqual(SnapshotBuffer<PoseSnapshot>.MinExtrapolationSpan, 0.03 + 1e-9);
        }

        [Test]
        public void SnapshotBuffer_SortsLateArrivals_ReplacesSameTimeSamples_AndKeepsItsCapacity()
        {
            var buffer = new SnapshotBuffer<PoseSnapshot>(8);
            Assert.IsFalse(buffer.Sample(0, out _), "empty");
            Assert.IsTrue(buffer.Push(1.0, new PoseSnapshot(V(1, 0, 0), NetQuat.Identity)));
            Assert.IsTrue(buffer.Push(1.2, new PoseSnapshot(V(3, 0, 0), NetQuat.Identity)));
            Assert.IsTrue(buffer.Push(1.1, new PoseSnapshot(V(2, 0, 0), NetQuat.Identity)), "late arrival is sorted in");
            Assert.IsTrue(buffer.Push(1.1, new PoseSnapshot(V(9, 0, 0), NetQuat.Identity)), "same time: the later arrival replaces the earlier");
            Assert.IsTrue(buffer.Sample(1.1, out PoseSnapshot replaced));
            Assert.AreEqual(9f, replaced.position.x, 1e-4f);
            Assert.IsTrue(buffer.Push(1.1, new PoseSnapshot(V(2, 0, 0), NetQuat.Identity)));
            Assert.IsFalse(buffer.Push(double.NaN, default(PoseSnapshot)));
            Assert.AreEqual(3, buffer.Count);
            Assert.IsTrue(buffer.Sample(1.05, out PoseSnapshot at));
            Assert.AreEqual(1.5f, at.position.x, 1e-4f);
            Assert.IsTrue(buffer.Sample(0.2, out at));
            Assert.AreEqual(1f, at.position.x, 1e-4f, "before the oldest sample it shows the oldest");

            for (int i = 0; i < 40; i++) buffer.Push(2.0 + i * 0.05, new PoseSnapshot(V(i, 0, 0), NetQuat.Identity));
            Assert.AreEqual(8, buffer.Count);
            Assert.AreEqual(2.0 + 39 * 0.05, buffer.NewestTime, 1e-9);
            Assert.IsFalse(buffer.Push(0.5, default(PoseSnapshot)), "older than everything in a full buffer");
            Assert.IsTrue(buffer.Push(buffer.OldestTime + 0.01, new PoseSnapshot(V(-1, 0, 0), NetQuat.Identity)), "a late one inside the window still fits");
            Assert.AreEqual(8, buffer.Count);
            double previous = double.NegativeInfinity;
            for (double t = buffer.OldestTime; t <= buffer.NewestTime; t += 0.01)
            {
                Assert.IsTrue(buffer.Sample(t, out _));
                Assert.Greater(t, previous);
                previous = t;
            }
            buffer.Clear();
            Assert.AreEqual(0, buffer.Count);
        }

        [Test]
        public void SnapshotBuffer_BlendsAvatarsAndCreatures()
        {
            var avatars = new SnapshotBuffer<AvatarSnapshot>();
            AvatarSnapshot a = AvatarSnapshot.Default, b = AvatarSnapshot.Default;
            a.headPosition = V(0, 1.6f, 0);
            b.headPosition = V(1, 1.6f, 0);
            b.leftGripCurl = 1f;
            b.flags = AvatarFlags.FlashlightOn;
            b.headRotation = NetQuat.FromYawDegrees(90f);
            avatars.Push(0, a);
            avatars.Push(0.05, b);
            Assert.IsTrue(avatars.Sample(0.025, out AvatarSnapshot mid));
            Assert.AreEqual(0.5f, mid.headPosition.x, 1e-4f);
            Assert.AreEqual(0.5f, mid.leftGripCurl, 1e-4f);
            Assert.AreEqual(45f, NetQuat.AngleDegrees(NetQuat.Identity, mid.headRotation), 0.5f);
            Assert.IsTrue(avatars.Sample(0.04, out mid));
            Assert.AreEqual(AvatarFlags.FlashlightOn, mid.flags, "flags switch at the midpoint");

            var creatures = new SnapshotBuffer<CreatureSnapshot>();
            creatures.Push(0, new CreatureSnapshot { yawDegrees = 350f, state = EnemyState.Patrol });
            creatures.Push(0.1, new CreatureSnapshot { yawDegrees = 10f, state = EnemyState.Chase, position = V(1, 0, 0) });
            Assert.IsTrue(creatures.Sample(0.05, out CreatureSnapshot c));
            Assert.LessOrEqual(Quant.YawDelta(0f, c.yawDegrees), 0.01f, "yaw blends the short way round through 0");
            Assert.AreEqual(EnemyState.Chase, c.state);
        }

        // ------------------------------------------------------------------ header and connectionless

        [Test]
        public void DatagramHeader_Is16Bytes_AndRoundTrips()
        {
            var h = new DatagramHeader { protocol = 7, connId = 0xABCD, senderSlot = 3, seq = 65535, ack = 65530, ackBits = 0xF00DF00D, timeMs = 4000000000u };
            var w = new NetWriter(m_Buffer);
            h.Write(ref w);
            Assert.AreEqual(16, w.Position);
            Assert.AreEqual(DatagramHeader.Size, w.Position);
            var r = new NetReader(m_Buffer, 0, 16);
            Assert.IsTrue(DatagramHeader.Read(ref r, out DatagramHeader g));
            Assert.AreEqual(h.protocol, g.protocol);
            Assert.AreEqual(h.connId, g.connId);
            Assert.AreEqual(h.senderSlot, g.senderSlot);
            Assert.AreEqual(h.seq, g.seq);
            Assert.AreEqual(h.ack, g.ack);
            Assert.AreEqual(h.ackBits, g.ackBits);
            Assert.AreEqual(h.timeMs, g.timeMs);
            Assert.IsTrue(Connectionless.TryPeekConnId(m_Buffer, 16, out ushort connId));
            Assert.AreEqual(0xABCD, connId);
            Assert.AreEqual(12345.678, DatagramHeader.ToSeconds(DatagramHeader.ToMs(12345.678)), 0.0006);
            Assert.AreEqual(0u, DatagramHeader.ToMs(-5));
        }

        [Test]
        public void ConnectionlessMessages_RoundTrip_AndProbeIs12Bytes()
        {
            var w = new NetWriter(m_Buffer);
            new ProbeMsg { protocol = 9, nonce = 0xCAFEBABE }.WriteDatagram(ref w);
            Assert.AreEqual(12, w.Position, "The plan's 12-byte Probe.");
            Assert.AreEqual(ProbeMsg.Size, w.Position);
            Assert.IsTrue(Connectionless.TryPeekConnId(m_Buffer, w.Position, out ushort connId));
            Assert.AreEqual(0, connId, "connId 0 marks a connectionless datagram");
            var r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(Connectionless.TryReadFrame(ref r, out byte protocol, out byte type));
            Assert.AreEqual(9, protocol);
            Assert.AreEqual(NetMsg.Probe, type);
            Assert.IsTrue(ProbeMsg.Read(ref r, protocol, out ProbeMsg probe));
            Assert.AreEqual(0xCAFEBABE, probe.nonce);
            Assert.AreEqual(9, probe.protocol);

            w = new NetWriter(m_Buffer);
            new HostInfoMsg { protocol = 1, nonce = 5, contentHash = 0x11223344, tugNumber = 4821, playerCount = 2, phase = MissionPhase.Results }.WriteDatagram(ref w);
            Assert.AreEqual(HostInfoMsg.Size, w.Position);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(Connectionless.TryReadFrame(ref r, out protocol, out type));
            Assert.AreEqual(NetMsg.HostInfo, type);
            Assert.IsTrue(HostInfoMsg.Read(ref r, protocol, out HostInfoMsg info));
            Assert.AreEqual(5u, info.nonce);
            Assert.AreEqual(0x11223344u, info.contentHash);
            Assert.AreEqual(4821, info.tugNumber);
            Assert.AreEqual(2, info.playerCount);
            Assert.AreEqual(MissionPhase.Results, info.phase);

            w = new NetWriter(m_Buffer);
            new HelloMsg { protocol = 1, contentHash = 77, clientNonce = 88, handedness = 1 }.WriteDatagram(ref w);
            Assert.AreEqual(HelloMsg.Size, w.Position);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(Connectionless.TryReadFrame(ref r, out protocol, out type));
            Assert.AreEqual(NetMsg.Hello, type);
            Assert.IsTrue(HelloMsg.Read(ref r, protocol, out HelloMsg hello));
            Assert.AreEqual(77u, hello.contentHash);
            Assert.AreEqual(88u, hello.clientNonce);
            Assert.AreEqual(1, hello.handedness);

            w = new NetWriter(m_Buffer);
            new AcceptMsg { protocol = 1, clientNonce = 88, connId = 4242, slot = 3 }.WriteDatagram(ref w);
            Assert.AreEqual(AcceptMsg.Size, w.Position);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(Connectionless.TryReadFrame(ref r, out protocol, out type));
            Assert.AreEqual(NetMsg.Accept, type);
            Assert.IsTrue(AcceptMsg.Read(ref r, protocol, out AcceptMsg accept));
            Assert.AreEqual(88u, accept.clientNonce);
            Assert.AreEqual(4242, accept.connId);
            Assert.AreEqual(3, accept.slot);

            w = new NetWriter(m_Buffer);
            new RefuseMsg { protocol = 1, clientNonce = 88, reason = RefuseReason.ContractInProgress }.WriteDatagram(ref w);
            Assert.AreEqual(RefuseMsg.Size, w.Position);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(Connectionless.TryReadFrame(ref r, out protocol, out type));
            Assert.AreEqual(NetMsg.Refuse, type);
            Assert.IsTrue(RefuseMsg.Read(ref r, protocol, out RefuseMsg refuse));
            Assert.AreEqual(RefuseReason.ContractInProgress, refuse.reason);

            w = new NetWriter(m_Buffer);
            new GoneMsg { protocol = 1, connId = 999, reason = SessionCloseReason.Dropped }.WriteDatagram(ref w);
            Assert.AreEqual(GoneMsg.Size, w.Position);
            Assert.AreEqual(11, GoneMsg.Size);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(Connectionless.TryReadFrame(ref r, out protocol, out type));
            Assert.AreEqual(NetMsg.Gone, type);
            Assert.IsTrue(GoneMsg.Read(ref r, protocol, out GoneMsg gone));
            Assert.AreEqual(999, gone.connId);
            Assert.AreEqual(SessionCloseReason.Dropped, gone.reason);

            w = new NetWriter(m_Buffer);
            new GoneMsg { protocol = 1, connId = 7, reason = SessionCloseReason.HostLeft }.WriteDatagram(ref w);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(Connectionless.TryReadFrame(ref r, out protocol, out type));
            Assert.IsTrue(GoneMsg.Read(ref r, protocol, out gone));
            Assert.AreEqual(SessionCloseReason.HostLeft, gone.reason, "the host's goodbye outside the reliable stream");
            m_Buffer[w.Position - 1] = 200;
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(Connectionless.TryReadFrame(ref r, out protocol, out type));
            Assert.IsTrue(GoneMsg.Read(ref r, protocol, out gone));
            Assert.AreEqual(SessionCloseReason.Dropped, gone.reason, "an unknown reason reads as Dropped");

            w = new NetWriter(m_Buffer);
            new QuitMsg { protocol = 1, connId = 4242 }.WriteDatagram(ref w);
            Assert.AreEqual(QuitMsg.Size, w.Position);
            Assert.AreEqual(10, QuitMsg.Size);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(Connectionless.TryReadFrame(ref r, out protocol, out type));
            Assert.AreEqual(NetMsg.Quit, type);
            Assert.IsTrue(QuitMsg.Read(ref r, protocol, out QuitMsg quit));
            Assert.AreEqual(4242, quit.connId);
            Assert.AreEqual(1, quit.protocol);
        }

        [Test]
        public void ConnectionlessFrame_RejectsWrongMagic_SessionDatagrams_AndBadFields()
        {
            var w = new NetWriter(m_Buffer);
            new ProbeMsg { protocol = 1, nonce = 1 }.WriteDatagram(ref w);
            m_Buffer[5] ^= 0x40;
            var r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsFalse(Connectionless.TryReadFrame(ref r, out _, out _), "wrong magic");

            w = new NetWriter(m_Buffer);
            new DatagramHeader { protocol = 1, connId = 5 }.Write(ref w);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsFalse(Connectionless.TryReadFrame(ref r, out _, out _), "a session datagram is not connectionless");

            w = new NetWriter(m_Buffer);
            new AcceptMsg { protocol = 1, clientNonce = 1, connId = 0, slot = 1 }.WriteDatagram(ref w);
            r = new NetReader(m_Buffer, 0, w.Position);
            Connectionless.TryReadFrame(ref r, out byte protocol, out _);
            Assert.IsFalse(AcceptMsg.Read(ref r, protocol, out _), "connId 0 is not a connection");

            w = new NetWriter(m_Buffer);
            new AcceptMsg { protocol = 1, clientNonce = 1, connId = 9, slot = 4 }.WriteDatagram(ref w);
            r = new NetReader(m_Buffer, 0, w.Position);
            Connectionless.TryReadFrame(ref r, out protocol, out _);
            Assert.IsFalse(AcceptMsg.Read(ref r, protocol, out _), "slot 4 does not exist");

            Assert.IsFalse(Connectionless.TryPeekConnId(m_Buffer, 2, out _));
            Assert.IsFalse(Connectionless.TryPeekConnId(null, 20, out _));
        }

        // ------------------------------------------------------------------ every message: round trip

        [Test]
        public void SessionMessages_RoundTrip()
        {
            var world = new WorldStateMsg { doorOpenMask = 0xA5A5, panelOpenMask = 0x1001, wireSnappedMask = 0x80000001u, wireBurntMask = 0x00F00000u };
            world.SetWeldDone(0);
            world.SetWeldDone(37);
            world.SetWeldDone(NetProtocol.MaxCutPoints - 1);
            world.SetWeldDone(NetProtocol.MaxCutPoints); // ignored
            world.SetWeldDone(-1);                         // ignored
            var w = new NetWriter(m_Buffer);
            world.Write(ref w);
            Assert.AreEqual(WorldStateMsg.MaxSize, w.Position, "a full weld bitset is exactly the cap");
            var r = new NetReader(m_Buffer, 0, w.Position);
            var world2 = new WorldStateMsg();
            Assert.IsTrue(WorldStateMsg.Read(ref r, ref world2));
            Assert.AreEqual(world.doorOpenMask, world2.doorOpenMask);
            Assert.AreEqual(world.panelOpenMask, world2.panelOpenMask);
            Assert.AreEqual(world.wireSnappedMask, world2.wireSnappedMask);
            Assert.AreEqual(world.wireBurntMask, world2.wireBurntMask);
            for (int bit = 0; bit < NetProtocol.MaxCutPoints; bit++)
                Assert.AreEqual(bit == 0 || bit == 37 || bit == NetProtocol.MaxCutPoints - 1, world2.IsWeldDone(bit), "weld " + bit);
            byte[] reused = world2.weldDone;
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(WorldStateMsg.Read(ref r, ref world2));
            Assert.AreSame(reused, world2.weldDone, "reading again reuses the weld array");

            var welcome = new WelcomeMsg { phase = MissionPhase.Results };
            welcome.SetSlot(0, true, 1, false);
            welcome.SetSlot(2, true, 0, true);
            welcome.world.doorOpenMask = 3;
            welcome.world.SetWeldDone(9);
            w = new NetWriter(m_Buffer);
            welcome.Write(ref w);
            Assert.LessOrEqual(w.Position, WelcomeMsg.MaxSize);
            r = new NetReader(m_Buffer, 0, w.Position);
            var welcome2 = new WelcomeMsg();
            Assert.IsTrue(WelcomeMsg.Read(ref r, ref welcome2));
            Assert.IsTrue(welcome2.IsPresent(0));
            Assert.AreEqual(1, welcome2.Handedness(0));
            Assert.IsFalse(welcome2.IsPresent(1));
            Assert.IsTrue(welcome2.IsPresent(2));
            Assert.IsTrue(welcome2.IsAway(2));
            Assert.IsFalse(welcome2.IsAway(0));
            Assert.IsFalse(welcome2.IsPresent(3));
            Assert.AreEqual(2, welcome2.PlayerCount);
            Assert.AreEqual(MissionPhase.Results, welcome2.phase);
            Assert.AreEqual(3, welcome2.world.doorOpenMask);
            Assert.IsTrue(welcome2.world.IsWeldDone(9));
            Assert.IsFalse(welcome2.world.IsWeldDone(8));

            w = new NetWriter(m_Buffer);
            new RosterChangedMsg { slot = 3, change = RosterChange.Away, handedness = 1 }.Write(ref w);
            Assert.AreEqual(RosterChangedMsg.MaxSize, w.Position);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(RosterChangedMsg.Read(ref r, out RosterChangedMsg roster));
            Assert.AreEqual(3, roster.slot);
            Assert.AreEqual(RosterChange.Away, roster.change);
            Assert.AreEqual(1, roster.handedness);

            w = new NetWriter(m_Buffer);
            new SettingsChangedMsg { handedness = 1 }.Write(ref w);
            new PauseNoticeMsg { on = true }.Write(ref w);
            new HostPauseMsg { on = true }.Write(ref w);
            new SessionClosedMsg { reason = SessionCloseReason.Dropped }.Write(ref w);
            Assert.AreEqual(4, w.Position);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(SettingsChangedMsg.Read(ref r, out SettingsChangedMsg settings));
            Assert.AreEqual(1, settings.handedness);
            Assert.IsTrue(PauseNoticeMsg.Read(ref r, out PauseNoticeMsg pause));
            Assert.IsTrue(pause.on);
            Assert.IsTrue(HostPauseMsg.Read(ref r, out HostPauseMsg hostPause));
            Assert.IsTrue(hostPause.on);
            Assert.IsTrue(SessionClosedMsg.Read(ref r, out SessionClosedMsg closed));
            Assert.AreEqual(SessionCloseReason.Dropped, closed.reason);
        }

        [Test]
        public void StreamMessages_RoundTrip()
        {
            var random = new Random(21);
            var remote = new RemoteAvatarMsg { slot = 2, avatar = RandomAvatar(random) };
            var w = new NetWriter(m_Buffer);
            remote.Write(ref w);
            Assert.AreEqual(RemoteAvatarMsg.MaxSize, w.Position);
            var r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(RemoteAvatarMsg.Read(ref r, out RemoteAvatarMsg remote2));
            Assert.AreEqual(2, remote2.slot);
            AssertNear(remote.avatar.headPosition, remote2.avatar.headPosition, Quant.PosCmMaxError, "relayed head");

            var batch = new ItemPoseBatch { entries = new ItemPoseEntry[ItemPoseBatch.MaxEntries] };
            for (int i = 0; i < ItemPoseBatch.MaxEntries; i++)
                batch.entries[i] = new ItemPoseEntry { itemId = (byte)(200 + i), position = V(i, -i, i * 0.5f), rotation = RandomQuat(random), epoch = (byte)(i * 5), held = i % 2 == 0, atRest = i % 3 == 0 };
            batch.count = ItemPoseBatch.MaxEntries;
            w = new NetWriter(m_Buffer);
            batch.Write(ref w);
            Assert.AreEqual(ItemPoseBatch.MaxSize, w.Position);
            Assert.AreEqual(12, ItemPoseEntry.Size, "13 bytes per item with the id, as planned: 12 here plus a shared count");
            r = new NetReader(m_Buffer, 0, w.Position);
            var batch2 = new ItemPoseBatch();
            Assert.IsTrue(ItemPoseBatch.Read(ref r, ref batch2));
            Assert.AreEqual(12, batch2.count);
            for (int i = 0; i < batch2.count; i++)
            {
                Assert.AreEqual(batch.entries[i].itemId, batch2.entries[i].itemId);
                AssertNear(batch.entries[i].position, batch2.entries[i].position, Quant.PosCmMaxError, "item pose");
                Assert.Less(NetQuat.AngleDegrees(batch.entries[i].rotation, batch2.entries[i].rotation), Quant.QuatMaxErrorDegrees);
                Assert.AreEqual(batch.entries[i].epoch & ItemPoseEntry.EpochMask, batch2.entries[i].epoch);
                Assert.AreEqual(batch.entries[i].held, batch2.entries[i].held);
                Assert.AreEqual(batch.entries[i].atRest, batch2.entries[i].atRest);
            }
            m_Buffer[0] = 13; // one more than the cap
            r = new NetReader(m_Buffer, 0, m_Buffer.Length);
            Assert.IsFalse(ItemPoseBatch.Read(ref r, ref batch2), "a batch claiming 13 entries is rejected");
            Assert.AreEqual(0, batch2.count);

            w = new NetWriter(m_Buffer);
            new ClockMsg { elapsedSeconds = 754.32f, oxygenRemainingSeconds = 12.26f, flags = ClockFlags.HazardsPaused }.Write(ref w);
            Assert.AreEqual(ClockMsg.MaxSize, w.Position);
            Assert.AreEqual(5, w.Position, "6 bytes with the message type, as planned");
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(ClockMsg.Read(ref r, out ClockMsg clock));
            Assert.AreEqual(754.32f, clock.elapsedSeconds, 0.051f);
            Assert.AreEqual(12.26f, clock.oxygenRemainingSeconds, 0.051f);
            Assert.AreEqual(ClockFlags.HazardsPaused, clock.flags);
            w = new NetWriter(m_Buffer);
            new ClockMsg { elapsedSeconds = 1e9f, oxygenRemainingSeconds = -4f }.Write(ref w);
            r = new NetReader(m_Buffer, 0, w.Position);
            ClockMsg.Read(ref r, out clock);
            Assert.AreEqual(ClockMsg.MaxSeconds, clock.elapsedSeconds, 0.01f, "clamped, not wrapped");
            Assert.AreEqual(0f, clock.oxygenRemainingSeconds);

            w = new NetWriter(m_Buffer);
            new NoiseMsg { position = V(10.004f, -2.5f, 33.3f), loudness = 0.85f, kind = NoiseKind.Thruster }.Write(ref w);
            Assert.AreEqual(NoiseMsg.MaxSize, w.Position);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(NoiseMsg.Read(ref r, out NoiseMsg noise));
            AssertNear(V(10.004f, -2.5f, 33.3f), noise.position, Quant.PosCmMaxError, "noise");
            Assert.AreEqual(0.85f, noise.loudness, 0.0051f);
            Assert.AreEqual(NoiseKind.Thruster, noise.kind);
            m_Buffer[7] = 7; // kind 7 does not exist
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsFalse(NoiseMsg.Read(ref r, out _));
        }

        [Test]
        public void RunMessages_RoundTrip_AndRunEndedMirrorsMissionResult()
        {
            var result = new MissionResult
            {
                Outcome = MissionOutcome.ContractComplete, Quota = 900, DepositedValue = 1234, DepositedCount = 9, TrickBonus = 150,
                TrickShots = 3, Bonus = 250, Payout = 1634, OxygenRemainingSeconds = 87.25f, ElapsedSeconds = 512.5f,
            };
            RunEndedMsg message = RunEndedMsg.From(result);
            var w = new NetWriter(m_Buffer);
            message.Write(ref w);
            Assert.AreEqual(RunEndedMsg.MaxSize, w.Position);
            var r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(RunEndedMsg.Read(ref r, out RunEndedMsg message2));
            MissionResult back = message2.ToResult();
            Assert.AreEqual(result.Outcome, back.Outcome);
            Assert.AreEqual(result.Quota, back.Quota);
            Assert.AreEqual(result.DepositedValue, back.DepositedValue);
            Assert.AreEqual(result.DepositedCount, back.DepositedCount);
            Assert.AreEqual(result.TrickBonus, back.TrickBonus);
            Assert.AreEqual(result.TrickShots, back.TrickShots);
            Assert.AreEqual(result.Bonus, back.Bonus);
            Assert.AreEqual(result.Payout, back.Payout);
            Assert.AreEqual(result.OxygenRemainingSeconds, back.OxygenRemainingSeconds, "floats travel exactly");
            Assert.AreEqual(result.ElapsedSeconds, back.ElapsedSeconds);
            Assert.AreEqual(result.IsFailure, back.IsFailure);
            Assert.AreEqual(result.QuotaMet, back.QuotaMet);
            Assert.AreEqual(MissionOutcome.None, RunEndedMsg.From(null).outcome);

            m_Buffer[0] = 9; // outcome 9 does not exist
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsFalse(RunEndedMsg.Read(ref r, out _));

            w = new NetWriter(m_Buffer);
            new ExtractRefusedMsg { missingSlotsMask = 0b1010 }.Write(ref w);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(ExtractRefusedMsg.Read(ref r, out ExtractRefusedMsg refused));
            Assert.AreEqual(0b1010, refused.missingSlotsMask);
        }

        static RunStartParams MakeRun(int itemCount, int seed)
        {
            var random = new Random(seed);
            var p = new RunStartParams
            {
                seed = random.Next(int.MinValue, int.MaxValue),
                runIndex = (ushort)random.Next(0, 65536),
                mapIndex = (byte)random.Next(0, 4),
                condition = (byte)random.Next(0, 6),
                creatureIndex = RunStartParams.NoCreature,
                quota = random.Next(0, 5000),
                oxygenCapacitySeconds = 420.5f,
                bonusMultiplier = 1.75f,
                wireArmedMask = 0x8001F00Fu,
                hazardFlags = RunStartParams.HazardBlackouts,
                items = new RunItem[itemCount],
            };
            for (int i = 0; i < itemCount; i++)
                p.items[i] = new RunItem { pointIndex = (byte)random.Next(0, 256), definitionIndex = (byte)random.Next(0, 40), value = (ushort)random.Next(0, 65536) };
            if (itemCount > 0) p.items[itemCount - 1].flags = RunItem.FlagBonus;
            p.planHash = p.ComputePlanHash();
            return p;
        }

        [Test]
        public void RunStart_RoundTrips_WithItsPlanHash_AtAbout330Bytes()
        {
            RunStartParams p = MakeRun(61, 4); // 60 spawn points plus the bonus item
            var w = new NetWriter(m_Buffer);
            Assert.IsTrue(RunStartCodec.Write(ref w, in p));
            Assert.AreEqual(RunStartCodec.SizeOf(in p), w.Position);
            Assert.AreEqual(31 + 5 * 61, w.Position);
            Assert.Less(w.Position, 340, "about 330 bytes, as planned");

            var r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(RunStartCodec.Read(ref r, out RunStartParams q));
            Assert.IsTrue(r.AtEnd);
            Assert.AreEqual(p.seed, q.seed);
            Assert.AreEqual(p.runIndex, q.runIndex);
            Assert.AreEqual(p.mapIndex, q.mapIndex);
            Assert.AreEqual(p.condition, q.condition);
            Assert.AreEqual(p.creatureIndex, q.creatureIndex);
            Assert.AreEqual(p.quota, q.quota);
            Assert.AreEqual(p.oxygenCapacitySeconds, q.oxygenCapacitySeconds);
            Assert.AreEqual(p.bonusMultiplier, q.bonusMultiplier);
            Assert.AreEqual(p.wireArmedMask, q.wireArmedMask);
            Assert.AreEqual(p.hazardFlags, q.hazardFlags);
            Assert.AreEqual(p.items.Length, q.items.Length);
            for (int i = 0; i < p.items.Length; i++)
            {
                Assert.AreEqual(p.items[i].pointIndex, q.items[i].pointIndex);
                Assert.AreEqual(p.items[i].definitionIndex, q.items[i].definitionIndex);
                Assert.AreEqual(p.items[i].value, q.items[i].value);
                Assert.AreEqual(p.items[i].flags, q.items[i].flags);
            }
            Assert.AreEqual(RunItem.FlagBonus, q.items[60].flags, "the bonus item is the explicit last entry");
            Assert.AreEqual(p.planHash, q.planHash);
            Assert.AreEqual(p.planHash, q.ComputePlanHash(), "the receiver recomputes the same hash");
            Assert.IsTrue(RunStartCodec.IsPlanHashValid(in q));

            q.items[3].value ^= 1;
            Assert.IsFalse(RunStartCodec.IsPlanHashValid(in q), "any difference in the plan changes the hash");
        }

        [Test]
        public void RunStart_WithNoItems_AndWithTheMostItems_StaysUnderTheMessageCap()
        {
            RunStartParams empty = MakeRun(0, 1);
            var w = new NetWriter(m_Buffer);
            Assert.IsTrue(RunStartCodec.Write(ref w, in empty));
            Assert.AreEqual(RunStartCodec.FixedSize, w.Position);
            var r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(RunStartCodec.Read(ref r, out RunStartParams q));
            Assert.AreEqual(0, q.items.Length);

            var nothing = new RunStartParams();
            w = new NetWriter(m_Buffer);
            Assert.IsTrue(RunStartCodec.Write(ref w, in nothing), "a null item array is an empty plan");

            RunStartParams full = MakeRun(NetProtocol.MaxRunItems, 2);
            w = new NetWriter(m_Buffer);
            Assert.IsTrue(RunStartCodec.Write(ref w, in full));
            Assert.AreEqual(RunStartCodec.MaxSize, w.Position);
            Assert.LessOrEqual(w.Position, NetProtocol.MaxMessageBytes, "one reliable message, no fragmentation");
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(RunStartCodec.Read(ref r, out q));
            Assert.IsTrue(RunStartCodec.IsPlanHashValid(in q));

            RunStartParams tooMany = MakeRun(NetProtocol.MaxRunItems + 1, 3);
            w = new NetWriter(m_Buffer);
            Assert.IsFalse(RunStartCodec.Write(ref w, in tooMany), "more items than fit one message is refused by the writer");

            // a count byte larger than the payload really holds
            w = new NetWriter(m_Buffer);
            RunStartCodec.Write(ref w, in empty);
            m_Buffer[26] = 50;
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsFalse(RunStartCodec.Read(ref r, out _));
        }

        [Test]
        public void ItemMessages_RoundTrip()
        {
            var random = new Random(31);
            NetQuat rotation = RandomQuat(random);
            var w = new NetWriter(m_Buffer);
            new GrabRequestMsg { itemId = 200, epochSeen = 255, hand = 1 }.Write(ref w);
            Assert.AreEqual(GrabRequestMsg.MaxSize, w.Position);
            var r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(GrabRequestMsg.Read(ref r, out GrabRequestMsg grab));
            Assert.AreEqual(200, grab.itemId);
            Assert.AreEqual(255, grab.epochSeen);
            Assert.AreEqual(1, grab.hand);

            w = new NetWriter(m_Buffer);
            new ItemOwnerMsg { itemId = 7, ownerSlot = 3, epoch = 129, held = true, denied = true }.Write(ref w);
            Assert.AreEqual(ItemOwnerMsg.MaxSize, w.Position);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(ItemOwnerMsg.Read(ref r, out ItemOwnerMsg owner));
            Assert.AreEqual(7, owner.itemId);
            Assert.AreEqual(3, owner.ownerSlot);
            Assert.AreEqual(129, owner.epoch);
            Assert.IsTrue(owner.held);
            Assert.IsTrue(owner.denied);
            m_Buffer[1] = 4;
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsFalse(ItemOwnerMsg.Read(ref r, out _), "owner slot 4 does not exist");

            w = new NetWriter(m_Buffer);
            new ItemCollectedMsg { itemId = 9, by = 2 }.Write(ref w);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(ItemCollectedMsg.Read(ref r, out ItemCollectedMsg collected));
            Assert.AreEqual(9, collected.itemId);
            Assert.AreEqual(2, collected.by);

            w = new NetWriter(m_Buffer);
            new ItemReleasedMsg { itemId = 4, epoch = 17, position = V(1.234f, 5.678f, -9.012f), rotation = rotation, releasePosition = V(1, 2, 3) }.Write(ref w);
            Assert.AreEqual(ItemReleasedMsg.MaxSize, w.Position);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(ItemReleasedMsg.Read(ref r, out ItemReleasedMsg released));
            Assert.AreEqual(4, released.itemId);
            Assert.AreEqual(17, released.epoch);
            AssertNear(V(1.234f, 5.678f, -9.012f), released.position, Quant.PosCmMaxError, "released");
            AssertNear(V(1, 2, 3), released.releasePosition, Quant.PosCmMaxError, "release hand");
            Assert.Less(NetQuat.AngleDegrees(rotation, released.rotation), Quant.QuatMaxErrorDegrees);

            w = new NetWriter(m_Buffer);
            new ItemThrownMsg { itemId = 4, epoch = 17, linearVelocity = V(3.21f, -4.56f, 7.89f), angularVelocity = V(-12.3f, 0.5f, 25f), throwSpeed = 8.75f, wasThrown = true }.Write(ref w);
            Assert.AreEqual(ItemThrownMsg.MaxSize, w.Position);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(ItemThrownMsg.Read(ref r, out ItemThrownMsg thrown));
            AssertNear(V(3.21f, -4.56f, 7.89f), thrown.linearVelocity, 0.0051f, "throw velocity");
            AssertNear(V(-12.3f, 0.5f, 25f), thrown.angularVelocity, 0.0051f, "throw spin");
            Assert.AreEqual(8.75f, thrown.throwSpeed, 0.0051f);
            Assert.IsTrue(thrown.wasThrown);
            Assert.AreEqual(17, thrown.epoch);

            w = new NetWriter(m_Buffer);
            new ItemHandoffMsg { itemId = 5, epoch = 18, position = V(9, 8, 7), rotation = rotation, linearVelocity = V(0.1f, 0.2f, 0.3f), angularVelocity = V(1, 2, 3) }.Write(ref w);
            Assert.AreEqual(ItemHandoffMsg.MaxSize, w.Position);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(ItemHandoffMsg.Read(ref r, out ItemHandoffMsg handoff));
            Assert.AreEqual(5, handoff.itemId);
            Assert.AreEqual(18, handoff.epoch);
            AssertNear(V(9, 8, 7), handoff.position, Quant.PosCmMaxError, "handoff");
            AssertNear(V(0.1f, 0.2f, 0.3f), handoff.linearVelocity, 0.0051f, "handoff velocity");
            AssertNear(V(1, 2, 3), handoff.angularVelocity, 0.0051f, "handoff spin");

            w = new NetWriter(m_Buffer);
            new ItemRestMsg { itemId = 6, epoch = 3, position = V(-1, -2, -3), rotation = rotation }.Write(ref w);
            Assert.AreEqual(ItemRestMsg.MaxSize, w.Position);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(ItemRestMsg.Read(ref r, out ItemRestMsg rest));
            Assert.AreEqual(6, rest.itemId);
            Assert.AreEqual(3, rest.epoch);
            AssertNear(V(-1, -2, -3), rest.position, Quant.PosCmMaxError, "rest");

            w = new NetWriter(m_Buffer);
            new ItemDamageReportMsg { itemId = 1, newValue = 65535 }.Write(ref w);
            new ItemValueMsg { itemId = 2, value = 444 }.Write(ref w);
            new ItemDepositedMsg { itemId = 3, by = 1, finalValue = 600, trickBonus = 150, lockerSlot = 11 }.Write(ref w);
            new ItemRecoveredMsg { itemId = 4, position = V(2, 1, 0) }.Write(ref w);
            Assert.AreEqual(ItemDamageReportMsg.MaxSize + ItemValueMsg.MaxSize + ItemDepositedMsg.MaxSize + ItemRecoveredMsg.MaxSize, w.Position);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(ItemDamageReportMsg.Read(ref r, out ItemDamageReportMsg damage));
            Assert.AreEqual(65535, damage.newValue);
            Assert.IsTrue(ItemValueMsg.Read(ref r, out ItemValueMsg value));
            Assert.AreEqual(444, value.value);
            Assert.IsTrue(ItemDepositedMsg.Read(ref r, out ItemDepositedMsg deposited));
            Assert.AreEqual(3, deposited.itemId);
            Assert.AreEqual(1, deposited.by);
            Assert.AreEqual(600, deposited.finalValue);
            Assert.AreEqual(150, deposited.trickBonus);
            Assert.AreEqual(11, deposited.lockerSlot);
            Assert.IsTrue(ItemRecoveredMsg.Read(ref r, out ItemRecoveredMsg recovered));
            Assert.AreEqual(4, recovered.itemId);
            AssertNear(V(2, 1, 0), recovered.position, Quant.PosCmMaxError, "recovered");
            Assert.IsTrue(r.AtEnd);
        }

        [Test]
        public void WorldAndHazardMessages_RoundTrip()
        {
            var w = new NetWriter(m_Buffer);
            new DoorRequestMsg { doorId = 4, desiredOpen = true }.Write(ref w);
            new DoorStateMsg { doorId = 15, open = false }.Write(ref w);
            new PointCutReportMsg { panel = 12, index = 200 }.Write(ref w);
            new PointCutMsg { panel = 3, index = 7 }.Write(ref w);
            new PanelOpenedMsg { panel = 12, by = 2, plateImpulse = V(1.25f, -0.5f, 3f), plateTorque = V(-2f, 0.75f, 0.1f) }.Write(ref w);
            var r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(DoorRequestMsg.Read(ref r, out DoorRequestMsg doorRequest));
            Assert.AreEqual(4, doorRequest.doorId);
            Assert.IsTrue(doorRequest.desiredOpen);
            Assert.IsTrue(DoorStateMsg.Read(ref r, out DoorStateMsg doorState));
            Assert.AreEqual(15, doorState.doorId);
            Assert.IsFalse(doorState.open);
            Assert.IsTrue(PointCutReportMsg.Read(ref r, out PointCutReportMsg cutReport));
            Assert.AreEqual(12, cutReport.panel);
            Assert.AreEqual(200, cutReport.index);
            Assert.IsTrue(PointCutMsg.Read(ref r, out PointCutMsg cut));
            Assert.AreEqual(3, cut.panel);
            Assert.AreEqual(7, cut.index);
            Assert.IsTrue(PanelOpenedMsg.Read(ref r, out PanelOpenedMsg opened));
            Assert.AreEqual(12, opened.panel);
            Assert.AreEqual(2, opened.by);
            AssertNear(V(1.25f, -0.5f, 3f), opened.plateImpulse, 0.0051f, "plate impulse");
            AssertNear(V(-2f, 0.75f, 0.1f), opened.plateTorque, 0.0051f, "plate torque");
            Assert.IsTrue(r.AtEnd);

            w = new NetWriter(m_Buffer);
            new DoorRequestMsg { doorId = 16, desiredOpen = true }.Write(ref w);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsFalse(DoorRequestMsg.Read(ref r, out _), "door 16 does not fit the 16-bit mask");

            w = new NetWriter(m_Buffer);
            new CreatureStateChangedMsg { state = EnemyState.Chase, targetSlot = 2 }.Write(ref w);
            new CreatureStateChangedMsg { state = EnemyState.Patrol, targetSlot = NetProtocol.NoSlot }.Write(ref w);
            new TranquilizedMsg { seconds = 12.34f }.Write(ref w);
            new CatchStartedMsg { victimSlot = 1 }.Write(ref w);
            new TranqHitReportMsg { origin = V(1, 2, 3), hitPoint = V(4, 5, 6) }.Write(ref w);
            new TranqTracerMsg { by = 3, from = V(1, 2, 3), to = V(-4, -5, -6) }.Write(ref w);
            new BlackoutStartedMsg { seconds = 18.5f }.Write(ref w);
            new BlackoutEndedMsg { instant = true }.Write(ref w);
            new WireReportMsg { index = 31, change = NetWireChange.Burnt }.Write(ref w);
            new WireChangedMsg { index = 18, change = NetWireChange.Snapped, by = 2 }.Write(ref w);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(CreatureStateChangedMsg.Read(ref r, out CreatureStateChangedMsg changed));
            Assert.AreEqual(EnemyState.Chase, changed.state);
            Assert.AreEqual(2, changed.targetSlot);
            Assert.IsTrue(CreatureStateChangedMsg.Read(ref r, out changed));
            Assert.AreEqual(NetProtocol.NoSlot, changed.targetSlot);
            Assert.IsTrue(TranquilizedMsg.Read(ref r, out TranquilizedMsg tranq));
            Assert.AreEqual(12.34f, tranq.seconds, 0.051f);
            Assert.IsTrue(CatchStartedMsg.Read(ref r, out CatchStartedMsg caught));
            Assert.AreEqual(1, caught.victimSlot);
            Assert.IsTrue(TranqHitReportMsg.Read(ref r, out TranqHitReportMsg hit));
            AssertNear(V(1, 2, 3), hit.origin, Quant.PosCmMaxError, "dart origin");
            AssertNear(V(4, 5, 6), hit.hitPoint, Quant.PosCmMaxError, "dart hit");
            Assert.IsTrue(TranqTracerMsg.Read(ref r, out TranqTracerMsg tracer));
            Assert.AreEqual(3, tracer.by);
            AssertNear(V(-4, -5, -6), tracer.to, Quant.PosCmMaxError, "tracer end");
            Assert.IsTrue(BlackoutStartedMsg.Read(ref r, out BlackoutStartedMsg blackout));
            Assert.AreEqual(18.5f, blackout.seconds, 0.051f);
            Assert.IsTrue(BlackoutEndedMsg.Read(ref r, out BlackoutEndedMsg blackoutEnd));
            Assert.IsTrue(blackoutEnd.instant);
            Assert.IsTrue(WireReportMsg.Read(ref r, out WireReportMsg wireReport));
            Assert.AreEqual(31, wireReport.index);
            Assert.AreEqual(NetWireChange.Burnt, wireReport.change);
            Assert.IsTrue(WireChangedMsg.Read(ref r, out WireChangedMsg wireChanged));
            Assert.AreEqual(18, wireChanged.index);
            Assert.AreEqual(NetWireChange.Snapped, wireChanged.change);
            Assert.AreEqual(2, wireChanged.by);
            Assert.IsTrue(r.AtEnd);

            w = new NetWriter(m_Buffer);
            new WireReportMsg { index = 32, change = NetWireChange.Snapped }.Write(ref w);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsFalse(WireReportMsg.Read(ref r, out _), "wire 32 does not fit the 32-bit mask");
        }

        // ------------------------------------------------------------------ every message: caps, truncation, garbage

        /// <summary>One worst-case (largest) instance of every message, with its reader.</summary>
        static List<Sample> AllSamples()
        {
            var random = new Random(77);
            var far = V(-300f, 300f, 123.45f);
            var samples = new List<Sample>();
            void Add(byte id, WriteFn write, ReadFn read) { samples.Add(new Sample { id = id, write = write, read = read }); }

            Add(NetMsg.Probe, (ref NetWriter w) => new ProbeMsg { protocol = 1, nonce = 0xFFFFFFFF }.WriteDatagram(ref w),
                (ref NetReader r) => Connectionless.TryReadFrame(ref r, out byte p, out _) && ProbeMsg.Read(ref r, p, out _));
            Add(NetMsg.HostInfo, (ref NetWriter w) => new HostInfoMsg { protocol = 1, nonce = 9, contentHash = 0xFFFFFFFF, tugNumber = 9999, playerCount = 4, phase = MissionPhase.Results }.WriteDatagram(ref w),
                (ref NetReader r) => Connectionless.TryReadFrame(ref r, out byte p, out _) && HostInfoMsg.Read(ref r, p, out _));
            Add(NetMsg.Hello, (ref NetWriter w) => new HelloMsg { protocol = 1, contentHash = 0xFFFFFFFF, clientNonce = 0xFFFFFFFF, handedness = 1 }.WriteDatagram(ref w),
                (ref NetReader r) => Connectionless.TryReadFrame(ref r, out byte p, out _) && HelloMsg.Read(ref r, p, out _));
            Add(NetMsg.Accept, (ref NetWriter w) => new AcceptMsg { protocol = 1, clientNonce = 0xFFFFFFFF, connId = 65535, slot = 3 }.WriteDatagram(ref w),
                (ref NetReader r) => Connectionless.TryReadFrame(ref r, out byte p, out _) && AcceptMsg.Read(ref r, p, out _));
            Add(NetMsg.Refuse, (ref NetWriter w) => new RefuseMsg { protocol = 1, clientNonce = 0xFFFFFFFF, reason = RefuseReason.Full }.WriteDatagram(ref w),
                (ref NetReader r) => Connectionless.TryReadFrame(ref r, out byte p, out _) && RefuseMsg.Read(ref r, p, out _));
            Add(NetMsg.Gone, (ref NetWriter w) => new GoneMsg { protocol = 1, connId = 65535, reason = SessionCloseReason.Dropped }.WriteDatagram(ref w),
                (ref NetReader r) => Connectionless.TryReadFrame(ref r, out byte p, out _) && GoneMsg.Read(ref r, p, out _));
            Add(NetMsg.Quit, (ref NetWriter w) => new QuitMsg { protocol = 1, connId = 65535 }.WriteDatagram(ref w),
                (ref NetReader r) => Connectionless.TryReadFrame(ref r, out byte p, out _) && QuitMsg.Read(ref r, p, out _));

            AvatarSnapshot avatar = RandomAvatar(random);
            Add(NetMsg.AvatarState, (ref NetWriter w) => avatar.Write(ref w), (ref NetReader r) => AvatarSnapshot.Read(ref r, out _));
            Add(NetMsg.RemoteAvatar, (ref NetWriter w) => new RemoteAvatarMsg { slot = 3, avatar = avatar }.Write(ref w), (ref NetReader r) => RemoteAvatarMsg.Read(ref r, out _));
            Add(NetMsg.CreatureState, (ref NetWriter w) => new CreatureSnapshot { position = far, yawDegrees = 359f, state = EnemyState.Search, targetSlot = 3, climbStep = 2, flags = 127, suspicion = 1f, sedationSecondsLeft = 999f }.Write(ref w),
                (ref NetReader r) => CreatureSnapshot.Read(ref r, out _));
            var batch = new ItemPoseBatch { entries = new ItemPoseEntry[ItemPoseBatch.MaxEntries], count = ItemPoseBatch.MaxEntries };
            for (int i = 0; i < batch.count; i++) batch.entries[i] = new ItemPoseEntry { itemId = (byte)(255 - i), position = far, rotation = RandomQuat(random), epoch = 63, held = true, atRest = true };
            var readBatch = new ItemPoseBatch();
            Add(NetMsg.ItemPoses, (ref NetWriter w) => batch.Write(ref w), (ref NetReader r) => ItemPoseBatch.Read(ref r, ref readBatch));
            Add(NetMsg.Clock, (ref NetWriter w) => new ClockMsg { elapsedSeconds = 99999f, oxygenRemainingSeconds = 99999f, flags = 255 }.Write(ref w), (ref NetReader r) => ClockMsg.Read(ref r, out _));
            Add(NetMsg.Noise, (ref NetWriter w) => new NoiseMsg { position = far, loudness = 99f, kind = NoiseKind.Web }.Write(ref w), (ref NetReader r) => NoiseMsg.Read(ref r, out _));
            var voiceFrames = new byte[2 * VoiceMsg.MaxFrameBytes];
            random.NextBytes(voiceFrames);
            var voice = new VoiceMsg();
            voice.Set(3, 65535, voiceFrames, 0, VoiceMsg.MaxFrameBytes, VoiceMsg.MaxFrames);
            var readVoice = new VoiceMsg();
            Add(NetMsg.Voice, (ref NetWriter w) => voice.Write(ref w), (ref NetReader r) => VoiceMsg.Read(ref r, ref readVoice));

            var world = new WorldStateMsg { doorOpenMask = 0xFFFF, panelOpenMask = 0xFFFF, wireSnappedMask = 0xFFFFFFFF, wireBurntMask = 0xFFFFFFFF };
            for (int bit = 0; bit < NetProtocol.MaxCutPoints; bit++) world.SetWeldDone(bit);
            var readWorld = new WorldStateMsg();
            var welcome = new WelcomeMsg { phase = MissionPhase.Results, world = world };
            for (int i = 0; i < 4; i++) welcome.SetSlot(i, true, 1, true);
            var readWelcome = new WelcomeMsg();
            Add(NetMsg.Welcome, (ref NetWriter w) => welcome.Write(ref w), (ref NetReader r) => WelcomeMsg.Read(ref r, ref readWelcome));
            Add(NetMsg.RosterChanged, (ref NetWriter w) => new RosterChangedMsg { slot = 3, change = RosterChange.Settings, handedness = 1 }.Write(ref w), (ref NetReader r) => RosterChangedMsg.Read(ref r, out _));
            Add(NetMsg.WorldState, (ref NetWriter w) => world.Write(ref w), (ref NetReader r) => WorldStateMsg.Read(ref r, ref readWorld));
            Add(NetMsg.SettingsChanged, (ref NetWriter w) => new SettingsChangedMsg { handedness = 1 }.Write(ref w), (ref NetReader r) => SettingsChangedMsg.Read(ref r, out _));
            Add(NetMsg.PauseNotice, (ref NetWriter w) => new PauseNoticeMsg { on = true }.Write(ref w), (ref NetReader r) => PauseNoticeMsg.Read(ref r, out _));
            Add(NetMsg.HostPause, (ref NetWriter w) => new HostPauseMsg { on = true }.Write(ref w), (ref NetReader r) => HostPauseMsg.Read(ref r, out _));
            Add(NetMsg.Bye, (ref NetWriter w) => { }, (ref NetReader r) => !r.Failed);
            Add(NetMsg.SessionClosed, (ref NetWriter w) => new SessionClosedMsg { reason = SessionCloseReason.Dropped }.Write(ref w), (ref NetReader r) => SessionClosedMsg.Read(ref r, out _));

            RunStartParams run = MakeRun(NetProtocol.MaxRunItems, 5);
            Add(NetMsg.StartRequest, (ref NetWriter w) => { }, (ref NetReader r) => !r.Failed);
            Add(NetMsg.ExtractRequest, (ref NetWriter w) => { }, (ref NetReader r) => !r.Failed);
            Add(NetMsg.RunStart, (ref NetWriter w) => RunStartCodec.Write(ref w, in run), (ref NetReader r) => RunStartCodec.Read(ref r, out _));
            Add(NetMsg.ExtractRefused, (ref NetWriter w) => new ExtractRefusedMsg { missingSlotsMask = 15 }.Write(ref w), (ref NetReader r) => ExtractRefusedMsg.Read(ref r, out _));
            Add(NetMsg.RunEnded, (ref NetWriter w) => new RunEndedMsg { outcome = MissionOutcome.FailedCaught, quota = int.MaxValue, depositedValue = int.MaxValue, depositedCount = int.MaxValue, trickBonus = int.MaxValue, trickShots = int.MaxValue, bonus = int.MaxValue, payout = int.MaxValue, oxygenRemainingSeconds = float.MaxValue, elapsedSeconds = float.MaxValue }.Write(ref w),
                (ref NetReader r) => RunEndedMsg.Read(ref r, out _));
            Add(NetMsg.ReturnedToHub, (ref NetWriter w) => { }, (ref NetReader r) => !r.Failed);

            NetQuat q = RandomQuat(random);
            Add(NetMsg.GrabRequest, (ref NetWriter w) => new GrabRequestMsg { itemId = 255, epochSeen = 255, hand = 1 }.Write(ref w), (ref NetReader r) => GrabRequestMsg.Read(ref r, out _));
            Add(NetMsg.ItemOwner, (ref NetWriter w) => new ItemOwnerMsg { itemId = 255, ownerSlot = 3, epoch = 255, held = true, denied = true }.Write(ref w), (ref NetReader r) => ItemOwnerMsg.Read(ref r, out _));
            Add(NetMsg.ItemCollected, (ref NetWriter w) => new ItemCollectedMsg { itemId = 255, by = 3 }.Write(ref w), (ref NetReader r) => ItemCollectedMsg.Read(ref r, out _));
            Add(NetMsg.ItemReleased, (ref NetWriter w) => new ItemReleasedMsg { itemId = 255, epoch = 255, position = far, rotation = q, releasePosition = far }.Write(ref w), (ref NetReader r) => ItemReleasedMsg.Read(ref r, out _));
            Add(NetMsg.ItemThrown, (ref NetWriter w) => new ItemThrownMsg { itemId = 255, epoch = 255, linearVelocity = far, angularVelocity = far, throwSpeed = 9999f, wasThrown = true }.Write(ref w), (ref NetReader r) => ItemThrownMsg.Read(ref r, out _));
            Add(NetMsg.ItemHandoff, (ref NetWriter w) => new ItemHandoffMsg { itemId = 255, epoch = 255, position = far, rotation = q, linearVelocity = far, angularVelocity = far }.Write(ref w), (ref NetReader r) => ItemHandoffMsg.Read(ref r, out _));
            Add(NetMsg.ItemRest, (ref NetWriter w) => new ItemRestMsg { itemId = 255, epoch = 255, position = far, rotation = q }.Write(ref w), (ref NetReader r) => ItemRestMsg.Read(ref r, out _));
            Add(NetMsg.ItemDamageReport, (ref NetWriter w) => new ItemDamageReportMsg { itemId = 255, newValue = 65535 }.Write(ref w), (ref NetReader r) => ItemDamageReportMsg.Read(ref r, out _));
            Add(NetMsg.ItemValue, (ref NetWriter w) => new ItemValueMsg { itemId = 255, value = 65535 }.Write(ref w), (ref NetReader r) => ItemValueMsg.Read(ref r, out _));
            Add(NetMsg.ItemDeposited, (ref NetWriter w) => new ItemDepositedMsg { itemId = 255, by = 3, finalValue = 65535, trickBonus = 65535, lockerSlot = 255 }.Write(ref w), (ref NetReader r) => ItemDepositedMsg.Read(ref r, out _));
            Add(NetMsg.ItemRecovered, (ref NetWriter w) => new ItemRecoveredMsg { itemId = 255, position = far }.Write(ref w), (ref NetReader r) => ItemRecoveredMsg.Read(ref r, out _));

            Add(NetMsg.DoorRequest, (ref NetWriter w) => new DoorRequestMsg { doorId = 15, desiredOpen = true }.Write(ref w), (ref NetReader r) => DoorRequestMsg.Read(ref r, out _));
            Add(NetMsg.DoorState, (ref NetWriter w) => new DoorStateMsg { doorId = 15, open = true }.Write(ref w), (ref NetReader r) => DoorStateMsg.Read(ref r, out _));
            Add(NetMsg.PointCutReport, (ref NetWriter w) => new PointCutReportMsg { panel = 15, index = 255 }.Write(ref w), (ref NetReader r) => PointCutReportMsg.Read(ref r, out _));
            Add(NetMsg.PointCut, (ref NetWriter w) => new PointCutMsg { panel = 15, index = 255 }.Write(ref w), (ref NetReader r) => PointCutMsg.Read(ref r, out _));
            Add(NetMsg.PanelOpened, (ref NetWriter w) => new PanelOpenedMsg { panel = 15, by = 3, plateImpulse = far, plateTorque = far }.Write(ref w), (ref NetReader r) => PanelOpenedMsg.Read(ref r, out _));

            Add(NetMsg.CreatureStateChanged, (ref NetWriter w) => new CreatureStateChangedMsg { state = EnemyState.Search, targetSlot = 3 }.Write(ref w), (ref NetReader r) => CreatureStateChangedMsg.Read(ref r, out _));
            Add(NetMsg.Tranquilized, (ref NetWriter w) => new TranquilizedMsg { seconds = 99999f }.Write(ref w), (ref NetReader r) => TranquilizedMsg.Read(ref r, out _));
            Add(NetMsg.CatchStarted, (ref NetWriter w) => new CatchStartedMsg { victimSlot = 3 }.Write(ref w), (ref NetReader r) => CatchStartedMsg.Read(ref r, out _));
            Add(NetMsg.TranqHitReport, (ref NetWriter w) => new TranqHitReportMsg { origin = far, hitPoint = far }.Write(ref w), (ref NetReader r) => TranqHitReportMsg.Read(ref r, out _));
            Add(NetMsg.TranqTracer, (ref NetWriter w) => new TranqTracerMsg { by = 3, from = far, to = far }.Write(ref w), (ref NetReader r) => TranqTracerMsg.Read(ref r, out _));
            Add(NetMsg.BlackoutStarted, (ref NetWriter w) => new BlackoutStartedMsg { seconds = 99999f }.Write(ref w), (ref NetReader r) => BlackoutStartedMsg.Read(ref r, out _));
            Add(NetMsg.BlackoutEnded, (ref NetWriter w) => new BlackoutEndedMsg { instant = true }.Write(ref w), (ref NetReader r) => BlackoutEndedMsg.Read(ref r, out _));
            Add(NetMsg.WireReport, (ref NetWriter w) => new WireReportMsg { index = 31, change = NetWireChange.Burnt }.Write(ref w), (ref NetReader r) => WireReportMsg.Read(ref r, out _));
            Add(NetMsg.WireChanged, (ref NetWriter w) => new WireChangedMsg { index = 31, change = NetWireChange.Burnt, by = 3 }.Write(ref w), (ref NetReader r) => WireChangedMsg.Read(ref r, out _));
            return samples;
        }

        [Test]
        public void EveryMessage_HasASample_AUniqueId_AName_AndACapUnderTheMessageLimit()
        {
            List<Sample> samples = AllSamples();
            var seen = new HashSet<byte>();
            var names = new HashSet<string>();
            foreach (byte id in NetMsg.All)
            {
                Assert.IsTrue(seen.Add(id), "id " + id + " is used twice");
                Assert.IsTrue(names.Add(NetMsg.Name(id)), "name " + NetMsg.Name(id) + " is used twice");
                Assert.IsFalse(NetMsg.Name(id).StartsWith("Unknown"), "id " + id + " has no name");
                int cap = NetMsg.MaxSize(id);
                Assert.GreaterOrEqual(cap, 0, NetMsg.Name(id) + " has no size cap");
                Assert.LessOrEqual(cap, NetProtocol.MaxMessageBytes, NetMsg.Name(id) + " cap exceeds the 1000-byte message limit");
                Assert.IsTrue(samples.Exists(s => s.id == id), NetMsg.Name(id) + " has no codec sample in this test");
            }
            Assert.AreEqual(NetMsg.All.Length, samples.Count, "a sample for a message that is not in NetMsg.All");
            Assert.AreEqual(-1, NetMsg.MaxSize(250));
            Assert.AreEqual(53, NetMsg.All.Length, "7 connectionless + 7 streams + 39 reliable");

            // every reliable message plus its overhead and the datagram header fits one datagram: no fragmentation
            Assert.LessOrEqual(DatagramHeader.Size + ReliableChannel.Overhead + NetProtocol.MaxMessageBytes, NetProtocol.MaxDatagram);
            // the unreliable part of the fullest host datagram (3 avatars, creature, clock, 12 poses) leaves room for reliable traffic
            int streams = 3 * (1 + RemoteAvatarMsg.MaxSize) + 1 + CreatureSnapshot.Size + 1 + ClockMsg.MaxSize + 1 + ItemPoseBatch.MaxSize;
            Assert.Less(DatagramHeader.Size + streams, 300);
        }

        [Test]
        public void Reliability_IsDecidedByTheId()
        {
            foreach (byte id in new[] { NetMsg.Probe, NetMsg.HostInfo, NetMsg.Hello, NetMsg.Accept, NetMsg.Refuse, NetMsg.Gone, NetMsg.AvatarState, NetMsg.RemoteAvatar, NetMsg.CreatureState, NetMsg.ItemPoses, NetMsg.Clock, NetMsg.Noise, NetMsg.Voice })
                Assert.IsFalse(NetMsg.IsReliable(id), NetMsg.Name(id));
            foreach (byte id in NetMsg.All)
                if (id >= NetMsg.Welcome) Assert.IsTrue(NetMsg.IsReliable(id), NetMsg.Name(id));
        }

        [Test]
        public void EveryMessage_AtItsLargest_FitsItsSizeCap_AndReadsBack()
        {
            foreach (Sample sample in AllSamples())
            {
                var w = new NetWriter(m_Buffer);
                sample.write(ref w);
                Assert.IsFalse(w.Overflow, NetMsg.Name(sample.id));
                Assert.LessOrEqual(w.Position, NetMsg.MaxSize(sample.id), NetMsg.Name(sample.id) + " wrote more than its cap");
                var r = new NetReader(m_Buffer, 0, w.Position);
                Assert.IsTrue(sample.read(ref r), NetMsg.Name(sample.id) + " does not read back");
                Assert.IsTrue(r.AtEnd, NetMsg.Name(sample.id) + " left bytes unread");
            }
        }

        [Test]
        public void EveryMessage_Truncated_AtEveryLength_IsRejected_WithoutThrowing()
        {
            foreach (Sample sample in AllSamples())
            {
                var w = new NetWriter(m_Buffer);
                sample.write(ref w);
                int full = w.Position;
                for (int length = 0; length < full; length++)
                {
                    var r = new NetReader(m_Buffer, 0, length);
                    bool ok = true;
                    Assert.DoesNotThrow(() => { var inner = new NetReader(m_Buffer, 0, length); ok = sample.read(ref inner); }, NetMsg.Name(sample.id) + " threw at length " + length);
                    Assert.IsFalse(ok, NetMsg.Name(sample.id) + " accepted " + length + " of " + full + " bytes");
                }
            }
        }

        [Test]
        public void EveryReader_FedRandomBytes_NeverThrows()
        {
            var random = new Random(12345);
            List<Sample> samples = AllSamples();
            var garbage = new byte[1500];
            int accepted = 0;
            for (int round = 0; round < 400; round++)
            {
                random.NextBytes(garbage);
                int length = random.Next(0, garbage.Length);
                foreach (Sample sample in samples)
                {
                    Assert.DoesNotThrow(() =>
                    {
                        var r = new NetReader(garbage, 0, length);
                        if (sample.read(ref r)) accepted++;
                    }, NetMsg.Name(sample.id) + " threw on garbage");
                }
            }
            Assert.Greater(accepted, 0, "fixed-layout messages accept arbitrary bytes; the point is that nothing throws");
        }
    }
}

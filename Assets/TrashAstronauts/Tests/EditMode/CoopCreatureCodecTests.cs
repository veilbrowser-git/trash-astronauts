using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Co-op stage 4: the creature sample carries every flag the replica reads (all eight bits, staggered
    /// included) and stays inside its bounds at the edges; the creature and hazard messages refuse what they
    /// cannot mean.
    /// </summary>
    public class CoopCreatureCodecTests
    {
        readonly byte[] m_Buffer = new byte[64];

        CreatureSnapshot RoundTrip(in CreatureSnapshot c)
        {
            var w = new NetWriter(m_Buffer);
            c.Write(ref w);
            Assert.AreEqual(CreatureSnapshot.Size, w.Position);
            var r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(CreatureSnapshot.Read(ref r, out CreatureSnapshot d));
            return d;
        }

        [Test]
        public void CreatureSnapshot_CarriesEveryFlag_StaggeredIncluded()
        {
            byte[] flags =
            {
                CreatureFlags.Running, CreatureFlags.Sedated, CreatureFlags.Lurking, CreatureFlags.SeesQuarry,
                CreatureFlags.WallSide, CreatureFlags.HeardNoiseToggle, CreatureFlags.Scaring, CreatureFlags.Staggered,
            };
            byte all = 0;
            foreach (byte f in flags)
            {
                Assert.AreEqual(0, all & f, "Each flag has its own bit.");
                all |= f;
            }
            Assert.AreEqual(255, all);
            for (int value = 0; value < 256; value++)
            {
                var d = RoundTrip(new CreatureSnapshot { state = EnemyState.Patrol, flags = (byte)value });
                Assert.AreEqual(value, d.flags);
                Assert.AreEqual((value & CreatureFlags.Staggered) != 0, d.HasFlag(CreatureFlags.Staggered));
            }
        }

        [Test]
        public void CreatureSnapshot_AtItsBounds_ClampsInsteadOfWrapping()
        {
            var d = RoundTrip(new CreatureSnapshot
            {
                position = new NetVec3(-327.6f, 0f, 327.6f), yawDegrees = 359.9f, state = EnemyState.Search,
                targetSlot = 3, climbStep = 2, flags = 255, suspicion = 1f, sedationSecondsLeft = 9999f,
            });
            Assert.AreEqual(-327.6f, d.position.x, 0.011f);
            Assert.AreEqual(327.6f, d.position.z, 0.011f);
            Assert.LessOrEqual(Quant.YawDelta(359.9f, d.yawDegrees), Quant.YawMaxErrorDegrees);
            Assert.AreEqual(EnemyState.Search, d.state);
            Assert.AreEqual(3, d.targetSlot);
            Assert.AreEqual(2, d.climbStep);
            Assert.AreEqual(1f, d.suspicion, 1e-4f);
            Assert.AreEqual(CreatureSnapshot.MaxSedationSeconds, d.sedationSecondsLeft, "Capped, not wrapped.");

            // Nobody targeted, a wall half-way, the smallest sedation: no slot, half a climb, and never an early wake.
            d = RoundTrip(new CreatureSnapshot { state = EnemyState.Dormant, targetSlot = NetProtocol.NoSlot, climbStep = 1, sedationSecondsLeft = 0.01f, suspicion = -1f });
            Assert.AreEqual(NetProtocol.NoSlot, d.targetSlot);
            Assert.AreEqual(0.5f, d.TargetClimb);
            Assert.AreEqual(1f, d.sedationSecondsLeft);
            Assert.AreEqual(0f, d.suspicion);
            d = RoundTrip(new CreatureSnapshot { state = EnemyState.Chase, sedationSecondsLeft = float.NaN });
            Assert.AreEqual(0f, d.sedationSecondsLeft);
        }

        [Test]
        public void CreatureSnapshot_BlendsTheBodyButNotTheFacts()
        {
            var a = new CreatureSnapshot { position = new NetVec3(0f, 1f, 0f), yawDegrees = 350f, state = EnemyState.Patrol, flags = CreatureFlags.Running };
            var b = new CreatureSnapshot { position = new NetVec3(2f, 1f, 0f), yawDegrees = 10f, state = EnemyState.Chase, flags = CreatureFlags.Running | CreatureFlags.HeardNoiseToggle, climbStep = 2 };
            var early = a.Blend(in b, 0.25f);
            Assert.AreEqual(0.5f, early.position.x, 1e-4f);
            Assert.LessOrEqual(Quant.YawDelta(355f, early.yawDegrees), 0.01f, "Yaw takes the short way round.");
            Assert.AreEqual(a.flags, early.flags, "Flags and state switch whole, never half-way.");
            Assert.AreEqual(0, early.climbStep);
            var late = a.Blend(in b, 0.75f);
            Assert.AreEqual(b.flags, late.flags);
            Assert.AreEqual(2, late.climbStep);
        }

        [Test]
        public void CatchAndStateMessages_RefuseSlotsThatCannotExist()
        {
            var w = new NetWriter(m_Buffer);
            new CatchStartedMsg { victimSlot = 4 }.Write(ref w);
            var r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsFalse(CatchStartedMsg.Read(ref r, out _), "There is no fifth crew member to catch.");

            w = new NetWriter(m_Buffer);
            new CreatureStateChangedMsg { state = EnemyState.Chase, targetSlot = 9 }.Write(ref w);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsFalse(CreatureStateChangedMsg.Read(ref r, out _));

            w = new NetWriter(m_Buffer);
            new TranquilizedMsg { seconds = 8.04f }.Write(ref w);
            new BlackoutStartedMsg { seconds = 12.36f }.Write(ref w);
            r = new NetReader(m_Buffer, 0, w.Position);
            Assert.IsTrue(TranquilizedMsg.Read(ref r, out var tranq));
            Assert.IsTrue(BlackoutStartedMsg.Read(ref r, out var blackout));
            Assert.AreEqual(8f, tranq.seconds, 0.051f);
            Assert.AreEqual(12.4f, blackout.seconds, 0.051f);
        }
    }
}

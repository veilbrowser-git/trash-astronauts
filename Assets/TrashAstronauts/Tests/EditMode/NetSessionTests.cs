using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// One host session and any number of client sessions on a MemoryTransportHub, stepped 10 ms at a time.
    /// Shared by the session, lobby and item fixtures.
    /// </summary>
    internal sealed class NetTestRig : IDisposable
    {
        public const uint Content = 0xC0FFEE01u;

        public sealed class Member
        {
            public MemoryTransport Transport;
            public INetTransport SessionTransport;
            public BotClientWorld World;
            public NetSessionClient Session;
            /// <summary>A frozen member is not ticked and its link is cut: a headset that fell asleep.</summary>
            public bool Frozen;
            public bool SendAvatar = true;
            public AvatarSnapshot Avatar = AvatarSnapshot.Default;
            public byte Slot => Session.LocalSlot;
        }

        public readonly MemoryTransportHub Hub;
        public readonly MemoryTransport HostTransport;
        public readonly ScriptedHostWorld HostWorld = new ScriptedHostWorld();
        public readonly NetSessionHost Host;
        public readonly List<Member> Members = new List<Member>();
        /// <summary>Runs every step after everyone has polled and before anyone sends.</summary>
        public Action<double> EachStep;
        public bool HostFrozen;
        /// <summary>Length of one step (one frame for everybody). 10 ms unless a test wants a headset frame rate.</summary>
        public double StepSeconds = 0.01;
        int m_NextSeed = 100;

        /// <summary>Session time. Offset from the hub clock so nothing depends on time starting at zero.</summary>
        public double Now => 1000.0 + Hub.Now;

        public NetTestRig(int seed = 1, IHostWorld world = null)
        {
            Hub = new MemoryTransportHub(seed);
            HostTransport = Hub.Create(NetProtocol.HostPort);
            Host = new NetSessionHost(HostTransport, world ?? HostWorld, new NetHostConfig { contentHash = Content, tugNumber = 4821, seed = seed });
            HostWorld.Session = Host;
        }

        public Member AddClient(uint contentHash = Content, byte protocol = NetProtocol.Version, byte handedness = 0, bool connect = true, Func<MemoryTransport, INetTransport> wrap = null)
        {
            var member = new Member { Transport = Hub.Create(), World = new BotClientWorld() };
            member.SessionTransport = wrap != null ? wrap(member.Transport) : member.Transport;
            member.Session = new NetSessionClient(member.SessionTransport, member.World, new NetClientConfig { contentHash = contentHash, protocol = protocol, handedness = handedness, seed = m_NextSeed++ });
            member.World.Session = member.Session;
            Members.Add(member);
            if (connect) member.Session.Connect(HostTransport.Local, Now);
            return member;
        }

        /// <summary>Adds a client and steps until its Welcome has arrived.</summary>
        public Member Join(byte handedness = 0)
        {
            Member member = AddClient(handedness: handedness);
            Assert.IsTrue(StepUntil(() => member.Session.Welcomed, 10.0), "client did not join");
            member.Avatar.headPosition = new NetVec3(member.Slot, 1.6f, 0f);
            return member;
        }

        public void Step(double seconds)
        {
            int steps = (int)Math.Round(seconds / StepSeconds);
            for (int i = 0; i < steps; i++) StepOnce();
        }

        public bool StepUntil(Func<bool> done, double maxSeconds)
        {
            int steps = (int)Math.Round(maxSeconds / StepSeconds);
            for (int i = 0; i < steps; i++)
            {
                if (done()) return true;
                StepOnce();
            }
            return done();
        }

        void StepOnce()
        {
            Hub.Advance(StepSeconds);
            double now = Now;
            if (!HostFrozen) Host.Poll(now);
            for (int i = 0; i < Members.Count; i++)
            {
                Member m = Members[i];
                if (m.Frozen) continue;
                m.World.Now = now;
                m.Session.Poll(now);
            }
            EachStep?.Invoke(now);
            if (!HostFrozen) Host.SendTick(now);
            for (int i = 0; i < Members.Count; i++)
            {
                Member m = Members[i];
                if (m.Frozen) continue;
                if (m.SendAvatar) m.Session.SetLocalAvatar(in m.Avatar);
                m.Session.SendTick(now);
            }
        }

        public void Freeze(Member member)
        {
            member.Frozen = true;
            member.Transport.Blackhole = true;
        }

        public void Thaw(Member member)
        {
            member.Frozen = false;
            member.Transport.Blackhole = false;
        }

        public void Dispose()
        {
            Host.Dispose();
            for (int i = 0; i < Members.Count; i++) Members[i].Session.Dispose();
        }
    }

    /// <summary>
    /// NetSessionHost / NetSessionClient: handshake and refusals, slots, Away and timeouts, relay, dispatch of every
    /// message into the world interfaces, the recording tap, real UDP on loopback, and the FakeWorld fuzz.
    /// </summary>
    public class NetSessionTests
    {
        NetTestRig m_Rig;

        [SetUp]
        public void SetUp() { m_Rig = new NetTestRig(); }

        [TearDown]
        public void TearDown()
        {
            m_Rig.Dispose();
            Assert.AreEqual(0, m_Rig.Hub.LiveCount, "every memory transport disposed");
            Assert.AreEqual(0, UdpTransport.LiveCount, "no socket left open");
            Assert.LessOrEqual(m_Rig.Hub.LargestDatagram, NetProtocol.MaxDatagram, "datagram cap");
            Assert.AreEqual(0, m_Rig.Hub.OversizeCount, "nothing tried to exceed the datagram cap");
        }

        static NetVec3 V(float x, float y, float z) => new NetVec3(x, y, z);

        static RunStartParams MakeRun(int items)
        {
            var p = new RunStartParams { seed = 77, runIndex = 5, creatureIndex = RunStartParams.NoCreature, quota = 500, oxygenCapacitySeconds = 300f, bonusMultiplier = 1f, items = new RunItem[items] };
            for (int i = 0; i < items; i++) p.items[i] = new RunItem { pointIndex = (byte)i, definitionIndex = (byte)(i % 5), value = (ushort)(100 + i) };
            p.planHash = p.ComputePlanHash();
            return p;
        }

        // ------------------------------------------------------------------ handshake

        [Test]
        public void Handshake_GivesSlotOne_AConnectionId_AndAWelcomeWithRosterPhaseAndWorld()
        {
            m_Rig.HostWorld.CurrentPhase = MissionPhase.Results;
            m_Rig.HostWorld.WelcomeWorld.doorOpenMask = 0b10110;
            m_Rig.HostWorld.WelcomeWorld.panelOpenMask = 0b1;
            m_Rig.HostWorld.WelcomeWorld.wireBurntMask = 0x80u;
            m_Rig.HostWorld.WelcomeWorld.SetWeldDone(11);
            m_Rig.Host.SetLocalHandedness(1);

            NetTestRig.Member c = m_Rig.Join(handedness: 1);

            Assert.AreEqual(ClientState.Connected, c.Session.State);
            Assert.AreEqual(1, c.Session.LocalSlot, "joiners take the lowest free slot");
            Assert.AreNotEqual(0, c.Session.ConnId);
            Assert.AreEqual(1, c.World.LocalSlot);
            Assert.AreEqual(1, c.World.Calls[NetMsg.Welcome]);
            Assert.AreEqual(NetMsg.Welcome, c.World.Order[0], "Welcome is the first reliable message");
            Assert.IsTrue(c.World.Present[0]);
            Assert.IsTrue(c.World.Present[1]);
            Assert.IsFalse(c.World.Present[2]);
            Assert.AreEqual(1, c.World.Handedness[0], "the host's handedness travels in the roster");
            Assert.AreEqual(1, c.World.Handedness[1]);
            Assert.AreEqual(MissionPhase.Results, c.World.HostPhase);
            Assert.AreEqual(0b10110, c.World.DoorOpenMask);
            Assert.AreEqual(0b1, c.World.PanelOpenMask);
            Assert.AreEqual(0x80u, c.World.WireBurntMask);
            Assert.IsTrue(c.World.World.IsWeldDone(11));
            Assert.IsFalse(c.World.World.IsWeldDone(10));

            Assert.IsTrue(m_Rig.HostWorld.Joined[1]);
            Assert.AreEqual(1, m_Rig.HostWorld.Handedness[1]);
            Assert.AreEqual(1, m_Rig.HostWorld.JoinCount);
            Assert.AreEqual(2, m_Rig.Host.PlayerCount);
            Assert.IsTrue(m_Rig.Host.IsConnected(1));
            Assert.IsFalse(m_Rig.Host.IsConnected(2));
            Assert.AreEqual(0b0011, m_Rig.Host.ConnectedMask);
            Assert.AreEqual(1, m_Rig.Host.Handedness(1));
            Assert.AreEqual(0, c.World.EndedCount);
        }

        [Test]
        public void HostAndFourClients_ThreeGetSlots_TheFourthIsRefusedFull()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            NetTestRig.Member c = m_Rig.Join();
            Assert.AreEqual(1, a.Slot);
            Assert.AreEqual(2, b.Slot);
            Assert.AreEqual(3, c.Slot);
            Assert.AreEqual(4, m_Rig.Host.PlayerCount);
            Assert.AreEqual(0b1111, m_Rig.Host.ConnectedMask);

            NetTestRig.Member d = m_Rig.AddClient();
            Assert.IsTrue(m_Rig.StepUntil(() => d.Session.State == ClientState.Ended, 3.0));
            Assert.AreEqual(SessionEndReason.Refused, d.Session.EndReason);
            Assert.AreEqual(RefuseReason.Full, d.Session.LastRefusal);
            Assert.AreEqual(SessionEndReason.Refused, d.World.Ended);
            Assert.AreEqual(1, d.World.EndedCount);
            Assert.IsFalse(d.World.Welcomed);
            Assert.AreEqual(4, m_Rig.Host.PlayerCount);
            Assert.AreEqual(3, m_Rig.HostWorld.JoinCount, "the refused client never reached the game");

            m_Rig.Step(0.5);
            foreach (NetTestRig.Member m in new[] { a, b, c })
                for (int slot = 0; slot < 4; slot++) Assert.IsTrue(m.World.Present[slot], "slot " + m.Slot + " sees slot " + slot);
            Assert.AreEqual(2, a.World.Calls[NetMsg.RosterChanged], "the first client was told about the two later joins");
            Assert.AreEqual(1, b.World.Calls[NetMsg.RosterChanged]);
            Assert.AreEqual(0, c.World.Calls[NetMsg.RosterChanged], "the last one learned everything from its Welcome");

            ushort ida = a.Session.ConnId, idb = b.Session.ConnId, idc = c.Session.ConnId;
            Assert.IsTrue(ida != idb && idb != idc && ida != idc, "connection ids are unique");
        }

        [Test]
        public void Hello_WithAnotherProtocolVersion_IsRefusedVersionMismatch()
        {
            NetTestRig.Member c = m_Rig.AddClient(protocol: (byte)(NetProtocol.Version + 1));
            Assert.IsTrue(m_Rig.StepUntil(() => c.Session.State == ClientState.Ended, 3.0));
            Assert.AreEqual(SessionEndReason.Refused, c.Session.EndReason);
            Assert.AreEqual(RefuseReason.VersionMismatch, c.Session.LastRefusal);
            Assert.AreEqual(1, m_Rig.Host.PlayerCount);
            Assert.AreEqual(0, m_Rig.HostWorld.JoinCount);
        }

        [Test]
        public void Hello_WithAnotherContentHash_IsRefusedContentMismatch()
        {
            NetTestRig.Member c = m_Rig.AddClient(contentHash: NetTestRig.Content ^ 1u);
            Assert.IsTrue(m_Rig.StepUntil(() => c.Session.State == ClientState.Ended, 3.0));
            Assert.AreEqual(RefuseReason.ContentMismatch, c.Session.LastRefusal);
            Assert.AreEqual(1, m_Rig.Host.PlayerCount);
        }

        [Test]
        public void Hello_DuringAContract_IsRefusedContractInProgress_ButAcceptedInHubAndResults()
        {
            m_Rig.HostWorld.CurrentPhase = MissionPhase.Active;
            NetTestRig.Member c = m_Rig.AddClient();
            Assert.IsTrue(m_Rig.StepUntil(() => c.Session.State == ClientState.Ended, 3.0));
            Assert.AreEqual(RefuseReason.ContractInProgress, c.Session.LastRefusal);
            Assert.AreEqual(1, m_Rig.Host.PlayerCount);

            m_Rig.HostWorld.CurrentPhase = MissionPhase.Results;
            c.Session.Connect(m_Rig.HostTransport.Local, m_Rig.Now);
            Assert.IsTrue(m_Rig.StepUntil(() => c.Session.Welcomed, 3.0), "the same client may retry once the contract is over");
            Assert.AreEqual(1, c.Session.LocalSlot);
            Assert.AreEqual(RefuseReason.None, c.Session.LastRefusal);

            m_Rig.HostWorld.CurrentPhase = MissionPhase.Hub;
            Assert.AreEqual(2, m_Rig.Join().Slot);
        }

        [Test]
        public void RefusalOrder_IsVersionThenContentThenPhaseThenFull()
        {
            m_Rig.HostWorld.CurrentPhase = MissionPhase.Active;
            NetTestRig.Member wrongEverything = m_Rig.AddClient(contentHash: 5, protocol: 99);
            NetTestRig.Member wrongContent = m_Rig.AddClient(contentHash: 5);
            m_Rig.Step(1.0);
            Assert.AreEqual(RefuseReason.VersionMismatch, wrongEverything.Session.LastRefusal);
            Assert.AreEqual(RefuseReason.ContentMismatch, wrongContent.Session.LastRefusal);
        }

        [Test]
        public void Hello_ToNobody_IsRepeatedEvery250Ms_AndTimesOutAfterFiveSeconds()
        {
            NetTestRig.Member c = m_Rig.AddClient(connect: false);
            c.Session.Connect(new NetEndpoint(10, 0, 0, 200, NetProtocol.HostPort), m_Rig.Now);
            m_Rig.Step(4.9);
            Assert.AreEqual(ClientState.Connecting, c.Session.State);
            m_Rig.Step(0.2);
            Assert.AreEqual(ClientState.Ended, c.Session.State);
            Assert.AreEqual(SessionEndReason.ConnectTimeout, c.Session.EndReason);
            Assert.AreEqual(SessionEndReason.ConnectTimeout, c.World.Ended);
            Assert.AreEqual(20, c.Session.Stats.datagramsSent, 1, "one Hello every 250 ms for 5 s");
            Assert.AreEqual(HelloMsg.Size, c.Session.Stats.largestDatagram);
        }

        [Test]
        public void LostHellosAndAccepts_AreRepeated_AndTheClientStillGetsExactlyOneSlot()
        {
            for (int seed = 1; seed <= 8; seed++)
            {
                using (var rig = new NetTestRig(seed))
                {
                    rig.Hub.Loss = 0.4;
                    rig.Hub.Duplicate = 0.3;
                    NetTestRig.Member c = rig.AddClient();
                    bool joined = rig.StepUntil(() => c.Session.Welcomed, 5.0);
                    if (!joined && c.Session.EndReason == SessionEndReason.ConnectTimeout) continue; // every Hello lost: legitimate, try the next seed
                    Assert.IsTrue(joined, "seed " + seed);
                    rig.Step(1.0);
                    Assert.AreEqual(2, rig.Host.PlayerCount, "seed " + seed + ": repeated Hellos did not take a second slot");
                    Assert.AreEqual(1, rig.HostWorld.JoinCount, "seed " + seed);
                    Assert.AreEqual(1, c.World.Calls[NetMsg.Welcome], "seed " + seed + ": Welcome applied once");
                }
            }
        }

        [Test]
        public void AClientThatRestarts_FromTheSameAddress_ReplacesItsOldConnection()
        {
            NetTestRig.Member c = m_Rig.Join();
            ushort firstConnId = c.Session.ConnId;
            c.Session.Connect(m_Rig.HostTransport.Local, m_Rig.Now); // a new attempt: new nonce, same address
            Assert.IsTrue(m_Rig.StepUntil(() => c.Session.Welcomed, 3.0));
            Assert.AreEqual(1, c.Session.LocalSlot);
            Assert.AreNotEqual(firstConnId, c.Session.ConnId);
            Assert.AreEqual(2, m_Rig.Host.PlayerCount);
            Assert.AreEqual(2, m_Rig.HostWorld.JoinCount);
            Assert.AreEqual(1, m_Rig.HostWorld.LeftCount);
            Assert.AreEqual(PlayerLeaveReason.Replaced, m_Rig.HostWorld.LeftReason[1]);
        }

        [Test]
        public void AnAcceptedClientThatNeverSpeaks_LosesItsSlotAfterFiveSeconds()
        {
            MemoryTransport raw = m_Rig.Hub.Create();
            var buffer = new byte[64];
            var w = new NetWriter(buffer);
            new HelloMsg { protocol = NetProtocol.Version, contentHash = NetTestRig.Content, clientNonce = 4242 }.WriteDatagram(ref w);
            raw.Send(m_Rig.HostTransport.Local, buffer, w.Position);
            m_Rig.Step(0.1);
            Assert.AreEqual(2, m_Rig.Host.PlayerCount, "the slot is reserved as soon as Accept is sent");
            Assert.IsFalse(m_Rig.Host.IsConnected(1), "but the player is not in the game yet");
            Assert.AreEqual(0, m_Rig.HostWorld.JoinCount);
            var receive = new byte[1300];
            Assert.IsTrue(raw.Receive(out _, receive, out int length));
            Assert.AreEqual(AcceptMsg.Size, length);

            m_Rig.Step(4.5);
            Assert.AreEqual(2, m_Rig.Host.PlayerCount);
            m_Rig.Step(0.6);
            Assert.AreEqual(1, m_Rig.Host.PlayerCount, "freed after 5 s without a session datagram");
            Assert.AreEqual(0, m_Rig.HostWorld.JoinCount);
            Assert.AreEqual(0, m_Rig.HostWorld.LeftCount, "the game never heard of it");
            raw.Dispose();
        }

        [Test]
        public void SendRunStart_TurnsAwayAJoinStillInItsHandshake()
        {
            NetTestRig.Member joined = m_Rig.Join();
            NetTestRig.Member late = m_Rig.AddClient();
            m_Rig.Hub.Advance(0.01);
            m_Rig.Host.Poll(m_Rig.Now); // the Hello arrives and Accept goes out, but the client has not confirmed
            Assert.AreEqual(3, m_Rig.Host.PlayerCount);
            Assert.IsTrue(m_Rig.Host.SendRunStart(MakeRun(3)));
            m_Rig.HostWorld.CurrentPhase = MissionPhase.Active;
            m_Rig.Step(1.0);
            Assert.AreEqual(2, m_Rig.Host.PlayerCount);
            Assert.AreEqual(ClientState.Ended, late.Session.State);
            Assert.AreEqual(RefuseReason.ContractInProgress, late.Session.LastRefusal);
            Assert.AreEqual(1, joined.World.Calls[NetMsg.RunStart]);
            Assert.AreEqual(0, late.World.Calls[NetMsg.RunStart]);
        }

        // ------------------------------------------------------------------ connection id

        [Test]
        public void SessionDatagrams_WithTheWrongConnectionId_OrFromAnotherAddress_AreIgnored()
        {
            NetTestRig.Member c = m_Rig.Join();
            m_Rig.Step(0.2);
            long strayBefore = m_Rig.Host.Stats.stray;
            int avatarsBefore = m_Rig.HostWorld.AvatarCount[1];
            c.Frozen = true; // stop the real client so only our forgeries arrive

            var buffer = new byte[128];
            NetWriter Forge(ushort connId, byte slot, ushort seq)
            {
                var w = new NetWriter(buffer);
                new DatagramHeader { protocol = NetProtocol.Version, connId = connId, senderSlot = slot, seq = seq }.Write(ref w);
                w.U8(NetMsg.AvatarState);
                AvatarSnapshot fake = AvatarSnapshot.Default;
                fake.headPosition = V(99, 99, 99);
                fake.Write(ref w);
                return w;
            }

            // right address, wrong connection id
            NetWriter forged = Forge((ushort)(c.Session.ConnId + 1), 1, 60000);
            c.Transport.Send(m_Rig.HostTransport.Local, buffer, forged.Position);
            // right connection id, wrong address
            MemoryTransport rogue = m_Rig.Hub.Create();
            forged = Forge(c.Session.ConnId, 1, 60001);
            rogue.Send(m_Rig.HostTransport.Local, buffer, forged.Position);
            // right connection id and address, but claiming another slot / the host's slot
            forged = Forge(c.Session.ConnId, 2, 60002);
            c.Transport.Send(m_Rig.HostTransport.Local, buffer, forged.Position);
            forged = Forge(c.Session.ConnId, 0, 60003);
            c.Transport.Send(m_Rig.HostTransport.Local, buffer, forged.Position);
            // wrong protocol byte
            forged = Forge(c.Session.ConnId, 1, 60004);
            buffer[0] = 200;
            c.Transport.Send(m_Rig.HostTransport.Local, buffer, forged.Position);

            m_Rig.Step(0.1);
            Assert.AreEqual(strayBefore + 5, m_Rig.Host.Stats.stray, "all five forgeries counted as stray");
            Assert.AreEqual(avatarsBefore, m_Rig.HostWorld.AvatarCount[1], "none of them reached the game");
            Assert.AreNotEqual(99f, m_Rig.HostWorld.Avatars[1].headPosition.x);

            c.Frozen = false;
            m_Rig.Step(0.5);
            Assert.AreEqual(ClientState.Connected, c.Session.State, "the real client is unaffected (a Gone sent to the rogue address is not for it)");
            Assert.Greater(m_Rig.HostWorld.AvatarCount[1], avatarsBefore);
            rogue.Dispose();
        }

        [Test]
        public void Client_IgnoresDatagramsThatAreNotFromItsHost()
        {
            NetTestRig.Member c = m_Rig.Join();
            MemoryTransport rogue = m_Rig.Hub.Create();
            var buffer = new byte[64];
            var w = new NetWriter(buffer);
            new GoneMsg { protocol = NetProtocol.Version, connId = c.Session.ConnId }.WriteDatagram(ref w);
            rogue.Send(c.Transport.Local, buffer, w.Position);
            w = new NetWriter(buffer);
            new RefuseMsg { protocol = NetProtocol.Version, clientNonce = 1, reason = RefuseReason.Full }.WriteDatagram(ref w);
            rogue.Send(c.Transport.Local, buffer, w.Position);
            m_Rig.Step(0.3);
            Assert.AreEqual(ClientState.Connected, c.Session.State, "only the host can end the session");
            Assert.AreEqual(2, c.Session.Stats.stray);
            rogue.Dispose();
        }

        // ------------------------------------------------------------------ away, slot hold, drop

        [Test]
        public void SilentClient_IsAwayAfterOnePointFiveSeconds_AndBackWhenHeardAgain()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            m_Rig.Step(0.3);
            m_Rig.Freeze(a);
            m_Rig.Step(1.35);
            Assert.IsFalse(m_Rig.Host.IsAway(1), "not yet: under 1.5 s of silence");
            Assert.IsFalse(m_Rig.HostWorld.Away[1]);
            m_Rig.Step(0.35);
            Assert.IsTrue(m_Rig.Host.IsAway(1), "Away after 1.5 s of silence");
            Assert.IsTrue(m_Rig.HostWorld.Away[1], "the game was told");
            Assert.AreEqual(0b0010, m_Rig.Host.AwayMask);
            Assert.IsTrue(m_Rig.Host.IsConnected(1), "the slot is held");
            m_Rig.Step(0.3);
            Assert.IsTrue(b.World.Away[1], "the other client was told");
            Assert.IsFalse(b.World.Away[2]);

            m_Rig.Thaw(a);
            m_Rig.Step(0.5);
            Assert.IsFalse(m_Rig.Host.IsAway(1));
            Assert.IsFalse(m_Rig.HostWorld.Away[1]);
            Assert.IsFalse(b.World.Away[1]);
            Assert.IsTrue(a.World.Away[1] == false && a.World.Present[1], "the returning client's own roster is consistent again");
            Assert.AreEqual(2, m_Rig.HostWorld.AwayChanges);
            Assert.AreEqual(ClientState.Connected, a.Session.State);
        }

        [Test]
        public void ClientSilentFor20Seconds_KeepsItsSlot_AndThenReceivesTheWholeBacklogInOrder()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            var seenByA = new List<int>();
            a.Session.Tap = (outbound, peer, type, buffer, offset, length, time) =>
            {
                if (outbound || type != NetMsg.Tranquilized) return;
                var r = new NetReader(buffer, offset, length);
                TranquilizedMsg.Read(ref r, out TranquilizedMsg m);
                seenByA.Add((int)Math.Round(m.seconds * 10));
            };
            m_Rig.Step(0.2);
            m_Rig.Freeze(a);

            // 20 s of play that the sleeping headset misses: 300 reliable broadcasts, plus door changes
            int sent = 0;
            m_Rig.EachStep = now =>
            {
                if (sent < 300 && m_Rig.Hub.Now % 0.06 < 0.01) m_Rig.Host.SendTranquilized(sent++ * 0.1f);
            };
            m_Rig.Step(20.0);
            m_Rig.EachStep = null;
            Assert.AreEqual(300, sent);
            m_Rig.Host.SendDoorState(3, true);
            m_Rig.Host.SendDoorState(4, true);
            m_Rig.Host.SendDoorState(3, false);

            Assert.IsTrue(m_Rig.Host.IsConnected(1), "the slot is held through 20 s of silence");
            Assert.IsTrue(m_Rig.Host.IsAway(1));
            Assert.AreEqual(0, m_Rig.HostWorld.LeftCount);
            Assert.GreaterOrEqual(m_Rig.Host.Backlog(1), 300, "everything is waiting for it");
            Assert.LessOrEqual(m_Rig.Host.Backlog(1), ReliableChannel.MaxBacklog);
            Assert.AreEqual(0, seenByA.Count);
            Assert.AreEqual(300, b.World.Calls[NetMsg.Tranquilized], "the awake client got them as they happened");

            m_Rig.Thaw(a);
            Assert.IsTrue(m_Rig.StepUntil(() => m_Rig.Host.Backlog(1) == 0, 10.0), "the backlog drains once the headset wakes");
            Assert.AreEqual(300, seenByA.Count, "every missed message, exactly once");
            for (int i = 0; i < 300; i++) Assert.AreEqual(i, seenByA[i], "in the order the host sent them");
            Assert.AreEqual(0b10000, a.World.DoorOpenMask, "and the later door changes after them");
            Assert.AreEqual(ClientState.Connected, a.Session.State);
            Assert.AreEqual(1, a.Session.LocalSlot);
            Assert.IsFalse(m_Rig.Host.IsAway(1));
            Assert.IsTrue(a.World.Away[1] == false, "it saw itself go Away and come Back, in order");
            Assert.AreEqual(2, a.World.HostSilentChanges, "on waking it briefly showed HOST PAUSED, then cleared it");
            Assert.IsFalse(a.World.HostSilent);
        }

        [Test]
        public void ClientSilentFor45Seconds_IsDropped_ItsSlotIsFreedAndReused_AndItLearnsOnWaking()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            m_Rig.Step(0.2);
            m_Rig.Freeze(a);
            m_Rig.Step(44.5);
            Assert.IsTrue(m_Rig.Host.IsConnected(1), "still held just before 45 s");
            Assert.AreEqual(0, m_Rig.HostWorld.LeftCount);
            m_Rig.Step(1.0);
            Assert.IsFalse(m_Rig.Host.IsConnected(1), "dropped at 45 s");
            Assert.AreEqual(1, m_Rig.HostWorld.LeftCount);
            Assert.AreEqual(PlayerLeaveReason.Timeout, m_Rig.HostWorld.LeftReason[1]);
            Assert.IsFalse(m_Rig.HostWorld.Joined[1]);
            Assert.AreEqual(2, m_Rig.Host.PlayerCount);
            Assert.AreEqual(0b0101, m_Rig.Host.ConnectedMask);
            m_Rig.Step(0.3);
            Assert.IsFalse(b.World.Present[1], "the other client saw it leave");

            NetTestRig.Member c = m_Rig.Join();
            Assert.AreEqual(1, c.Slot, "the freed slot is the lowest free one and is reused");
            Assert.AreEqual(3, m_Rig.Host.PlayerCount);

            m_Rig.Thaw(a);
            Assert.IsTrue(m_Rig.StepUntil(() => a.Session.State == ClientState.Ended, 2.0), "the woken headset is told at once instead of waiting 60 s");
            Assert.AreEqual(SessionEndReason.Dropped, a.Session.EndReason);
            Assert.AreEqual(SessionEndReason.Dropped, a.World.Ended);
            m_Rig.Step(0.5);
            Assert.AreEqual(ClientState.Connected, c.Session.State, "the new owner of slot 1 is untouched");
            Assert.IsTrue(m_Rig.Host.IsConnected(1));
        }

        [Test]
        public void MoreThan512QueuedMessages_DropTheSilentClient()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            m_Rig.Step(0.2);
            m_Rig.Freeze(a);
            for (int i = 0; i < 300; i++) m_Rig.Host.SendDoorState((byte)(i % 5), i % 2 == 0);
            Assert.IsTrue(m_Rig.StepUntil(() => m_Rig.Host.Backlog(2) == 0, 10.0));
            Assert.IsTrue(m_Rig.Host.IsConnected(1), "300 queued: still held");
            Assert.AreEqual(300, m_Rig.Host.Backlog(1));
            for (int i = 300; i < 520; i++) m_Rig.Host.SendDoorState((byte)(i % 5), i % 2 == 0);
            m_Rig.Step(0.1);
            Assert.IsFalse(m_Rig.Host.IsConnected(1), "over 512 queued messages drops the client");
            Assert.AreEqual(PlayerLeaveReason.BacklogOverflow, m_Rig.HostWorld.LeftReason[1]);
            Assert.IsTrue(m_Rig.StepUntil(() => b.World.Calls[NetMsg.DoorState] == 520, 15.0), "the healthy client still gets all of it");
            Assert.IsTrue(m_Rig.Host.IsConnected(2));
            Assert.IsFalse(b.World.Present[1]);
        }

        [Test]
        public void HostSilence_ShowsHostPausedAfterOnePointFiveSeconds_AndEndsTheSessionAfter60()
        {
            NetTestRig.Member c = m_Rig.Join();
            m_Rig.Step(0.2);
            m_Rig.HostFrozen = true;
            m_Rig.Step(1.35);
            Assert.IsFalse(c.Session.HostSilent);
            m_Rig.Step(0.3);
            Assert.IsTrue(c.Session.HostSilent, "HOST PAUSED after 1.5 s");
            Assert.IsTrue(c.World.HostSilent);
            Assert.AreEqual(ClientState.Connected, c.Session.State);

            m_Rig.HostFrozen = false;
            m_Rig.Step(0.2);
            Assert.IsFalse(c.World.HostSilent, "cleared as soon as the host is heard");
            Assert.AreEqual(2, c.World.HostSilentChanges);

            // the host stays out of range; the client is too silent for the host, but 45 s only matters on the host
            m_Rig.HostFrozen = true;
            m_Rig.HostTransport.Blackhole = true;
            m_Rig.Step(59.0);
            Assert.AreEqual(ClientState.Connected, c.Session.State, "still waiting just before 60 s");
            m_Rig.Step(1.5);
            Assert.AreEqual(ClientState.Ended, c.Session.State);
            Assert.AreEqual(SessionEndReason.HostTimeout, c.Session.EndReason);
            Assert.AreEqual(SessionEndReason.HostTimeout, c.World.Ended);
            Assert.AreEqual(1, c.World.EndedCount);
        }

        [Test]
        public void Bye_FreesTheSlotAtOnce_AndTheOthersSeeItLeave()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            a.Session.Leave(m_Rig.Now);
            Assert.AreEqual(ClientState.Ended, a.Session.State);
            Assert.AreEqual(SessionEndReason.LocalLeave, a.Session.EndReason);
            Assert.AreEqual(0, a.World.EndedCount, "a local leave is not reported back to the world that asked for it");
            m_Rig.Step(0.2);
            Assert.IsFalse(m_Rig.Host.IsConnected(1));
            Assert.AreEqual(PlayerLeaveReason.Bye, m_Rig.HostWorld.LeftReason[1]);
            Assert.AreEqual(1, m_Rig.HostWorld.LeftCount, "three copies of Bye, one leave");
            Assert.IsFalse(b.World.Present[1]);
            Assert.AreEqual(2, m_Rig.Host.PlayerCount);
            Assert.AreEqual(1, m_Rig.Join().Slot);
        }

        [Test]
        public void HostClose_TellsEveryClient_EvenWhenTwoOfTheThreeCopiesAreLost()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            m_Rig.Host.Close(m_Rig.Now);
            Assert.IsTrue(m_Rig.Host.IsClosed);
            Assert.AreEqual(2, m_Rig.HostWorld.LeftCount);
            Assert.AreEqual(PlayerLeaveReason.HostClosed, m_Rig.HostWorld.LeftReason[1]);
            Assert.AreEqual(0, m_Rig.Host.ConnectedMask);
            m_Rig.Step(0.2);
            foreach (NetTestRig.Member m in new[] { a, b })
            {
                Assert.AreEqual(ClientState.Ended, m.Session.State);
                Assert.AreEqual(SessionEndReason.HostClosed, m.Session.EndReason);
                Assert.AreEqual(SessionEndReason.HostClosed, m.World.Ended);
                Assert.AreEqual(1, m.World.EndedCount, "three copies of SessionClosed, one ending");
            }
            m_Rig.Host.Close(m_Rig.Now);
            m_Rig.Host.SendDoorState(1, true);
            m_Rig.Host.Tick(m_Rig.Now); // all harmless after closing
        }

        // ------------------------------------------------------------------ streams

        [Test]
        public void Avatars_AreRelayedToEveryoneElse_NeverEchoed_AndCarryTheSendersSlot()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            a.Avatar.headPosition = V(3.21f, 1.5f, -4.5f);
            a.Avatar.leftHandPosition = V(3.0f, 1.2f, -4.2f);
            a.Avatar.rightGripCurl = 1f;
            a.Avatar.flags = AvatarFlags.FlashlightOn | AvatarFlags.ThrusterRight;
            a.Avatar.rightTool = 5;
            b.Avatar.headPosition = V(-7f, 2f, 9f);
            AvatarSnapshot hostAvatar = AvatarSnapshot.Default;
            hostAvatar.headPosition = V(0.5f, 1.7f, 0.25f);
            hostAvatar.flags = AvatarFlags.LeftHanded;
            m_Rig.EachStep = now => m_Rig.Host.SetLocalAvatar(in hostAvatar);
            m_Rig.Step(1.0);

            // host
            Assert.Greater(m_Rig.HostWorld.AvatarCount[1], 15);
            Assert.AreEqual(3.21f, m_Rig.HostWorld.Avatars[1].headPosition.x, Quant.PosCmMaxError);
            Assert.AreEqual(-7f, m_Rig.HostWorld.Avatars[2].headPosition.x, Quant.PosCmMaxError);
            Assert.IsTrue(m_Rig.Host.TryGetAvatar(1, out AvatarSnapshot newest));
            Assert.AreEqual(5, newest.rightTool);
            Assert.IsFalse(m_Rig.Host.TryGetAvatar(3, out _));

            // b sees a and the host, at about 20 Hz, never itself
            Assert.AreEqual(20, b.World.AvatarCount[1], 3, "20 Hz");
            Assert.AreEqual(20, b.World.AvatarCount[0], 3);
            Assert.AreEqual(0, b.World.AvatarCount[2], "never echoed back");
            Assert.AreEqual(0, a.World.AvatarCount[1]);
            AvatarSnapshot seen = b.World.Avatars[1];
            Assert.AreEqual(3.21f, seen.headPosition.x, Quant.PosCmMaxError);
            Assert.AreEqual(-4.5f, seen.headPosition.z, Quant.PosCmMaxError);
            Assert.AreEqual(3.0f, seen.leftHandPosition.x, 2 * Quant.PosCmMaxError + Quant.PosMmMaxError, "relayed twice: quantized on both hops");
            Assert.AreEqual(1f, seen.rightGripCurl);
            Assert.AreEqual(AvatarFlags.FlashlightOn | AvatarFlags.ThrusterRight, seen.flags);
            Assert.AreEqual(5, seen.rightTool);
            Assert.AreEqual(0.5f, b.World.Avatars[0].headPosition.x, Quant.PosCmMaxError);
            Assert.IsTrue(b.World.Avatars[0].HasFlag(AvatarFlags.LeftHanded));
            Assert.AreEqual(-7f, a.World.Avatars[2].headPosition.x, Quant.PosCmMaxError);

            // the stamp on a relayed avatar is host time, and the client's host clock tracks it
            Assert.AreEqual(m_Rig.Now, b.World.AvatarTime[1], 0.11);
            Assert.AreEqual(m_Rig.Now, b.Session.HostTime(m_Rig.Now), 0.06);
            Assert.AreEqual(b.Session.HostTime(m_Rig.Now) - 0.1, b.Session.RenderTime(m_Rig.Now), 1e-9);
        }

        [Test]
        public void SendRate_Is20Hz_WhateverTheFrameRate()
        {
            NetTestRig.Member c = m_Rig.Join();
            long clientBefore = c.Session.Stats.datagramsSent, hostBefore = m_Rig.Host.Stats.datagramsSent;
            m_Rig.Step(10.0); // 100 polls a second
            Assert.AreEqual(200, c.Session.Stats.datagramsSent - clientBefore, 3, "20 datagrams a second from the client");
            Assert.AreEqual(200, m_Rig.Host.Stats.datagramsSent - hostBefore, 3, "and from the host");
            long bytes = c.Session.Stats.bytesSent;
            Assert.Less(c.Session.Stats.largestDatagram, 80, "header + one AvatarState");
            Assert.Less(bytes / 10.5, 1500, "under the planned 1.5 kB/s uplink per client");
        }

        [Test]
        public void SendRate_Is20Hz_AtHeadsetFrameRates_AndAStallIsNotCaughtUpWithABurst()
        {
            // 50 ms is not a whole number of frames at 72, 90 or 120 fps. The tick must keep its phase: restarting
            // the 50 ms window from whichever frame sent last gives 18 Hz at 72 and 90 fps.
            foreach (double fps in new[] { 72.0, 90.0, 120.0, 60.0, 36.0 })
            {
                using (var rig = new NetTestRig(7))
                {
                    rig.StepSeconds = 1.0 / fps;
                    NetTestRig.Member c = rig.Join();
                    rig.Step(1.0);
                    long clientBefore = c.Session.Stats.datagramsSent, hostBefore = rig.Host.Stats.datagramsSent;
                    rig.Step(10.0);
                    Assert.AreEqual(200, c.Session.Stats.datagramsSent - clientBefore, 2, "client datagrams in 10 s at " + fps + " fps");
                    Assert.AreEqual(200, rig.Host.Stats.datagramsSent - hostBefore, 2, "host datagrams in 10 s at " + fps + " fps");

                    // everybody hitches for a second: one datagram when the frames resume, then the 50 ms rhythm again
                    rig.Hub.Advance(1.0);
                    clientBefore = c.Session.Stats.datagramsSent;
                    hostBefore = rig.Host.Stats.datagramsSent;
                    rig.Step((fps >= 60.0 ? 2.0 : 1.0) / fps); // well inside one 50 ms tick
                    Assert.AreEqual(1, c.Session.Stats.datagramsSent - clientBefore, "no burst from the client after a stall at " + fps + " fps");
                    Assert.AreEqual(1, rig.Host.Stats.datagramsSent - hostBefore, "no burst from the host after a stall at " + fps + " fps");
                    rig.Step(1.0);
                    Assert.AreEqual(21, c.Session.Stats.datagramsSent - clientBefore, 1);
                }
            }
        }

        [Test]
        public void HostClockAndCreature_ReachEveryClient()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            var creature = new CreatureSnapshot { position = V(12f, 3f, -8f), yawDegrees = 90f, state = EnemyState.Chase, targetSlot = 2, climbStep = 1, flags = CreatureFlags.Running | CreatureFlags.SeesQuarry, suspicion = 1f, sedationSecondsLeft = 0f };
            int tick = 0;
            m_Rig.EachStep = now =>
            {
                tick++;
                if (tick % 10 == 0) m_Rig.Host.SetCreature(in creature);                                  // 10 Hz
                if (tick % 50 == 0) m_Rig.Host.SetClock(123.4f, 56.7f, ClockFlags.HazardsPaused);         // 2 Hz
            };
            m_Rig.Step(2.0);
            foreach (NetTestRig.Member m in new[] { a, b })
            {
                Assert.AreEqual(20, m.World.Calls[NetMsg.CreatureState], 2);
                Assert.AreEqual(4, m.World.Calls[NetMsg.Clock], 1);
                Assert.AreEqual(EnemyState.Chase, m.World.Creature.state);
                Assert.AreEqual(2, m.World.Creature.targetSlot);
                Assert.AreEqual(12f, m.World.Creature.position.x, Quant.PosCmMaxError);
                Assert.AreEqual(CreatureFlags.Running | CreatureFlags.SeesQuarry, m.World.Creature.flags);
                Assert.AreEqual(123.4f, m.World.ElapsedSeconds, 0.051f);
                Assert.AreEqual(56.7f, m.World.OxygenRemainingSeconds, 0.051f);
                Assert.AreEqual(ClockFlags.HazardsPaused, m.World.ClockFlagBits);
                Assert.AreEqual(m_Rig.Now, m.World.CreatureTime, 0.15);
            }
        }

        [Test]
        public void AwayFlag_AndPauseNotice_MarkTheSlotAway_WithoutAnySilence()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            a.Session.SetAway(true, m_Rig.Now); // HazardPauseMonitor lost focus or tracking
            Assert.IsTrue(a.Session.IsAway);
            m_Rig.Step(0.3);
            Assert.IsTrue(m_Rig.Host.IsAway(1));
            Assert.IsTrue(m_Rig.HostWorld.Away[1]);
            Assert.IsTrue(m_Rig.HostWorld.Avatars[1].IsAway, "the avatar stream carries the flag too");
            Assert.IsTrue(b.World.Away[1]);
            Assert.IsTrue(b.World.Avatars[1].IsAway);
            Assert.AreEqual(1, m_Rig.HostWorld.AwayChanges);

            m_Rig.Step(3.0);
            Assert.IsTrue(m_Rig.Host.IsAway(1), "still Away while it keeps talking");
            Assert.AreEqual(1, m_Rig.HostWorld.AwayChanges);

            a.Session.SetAway(false, m_Rig.Now);
            m_Rig.Step(0.3);
            Assert.IsFalse(m_Rig.Host.IsAway(1));
            Assert.IsFalse(b.World.Away[1]);
            Assert.AreEqual(2, m_Rig.HostWorld.AwayChanges);

            // PauseNotice alone (app paused: no more avatars will come) is enough, and is sent at once
            long before = a.Session.Stats.datagramsSent;
            a.SendAvatar = false;
            a.Session.SetAway(true, m_Rig.Now);
            Assert.AreEqual(before + 1, a.Session.Stats.datagramsSent, "flushed synchronously, as OnApplicationPause needs");
            m_Rig.Freeze(a);
            m_Rig.Step(0.2);
            Assert.IsTrue(m_Rig.Host.IsAway(1), "Away long before the 1.5 s silence rule");
        }

        [Test]
        public void Noise_IsStampedWithTheSendersSlot_ClampedPerKind_AndLimitedTo8PerSecond()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            m_Rig.Host.SetNoiseMax(NoiseKind.Cutting, 0.85f);

            b.Session.SendNoise(V(4f, 1f, -2f), 0.6f, NoiseKind.Impact);
            m_Rig.Step(0.2);
            Assert.AreEqual(1, m_Rig.HostWorld.Calls[NetMsg.Noise]);
            Assert.AreEqual(2, m_Rig.HostWorld.LastSlot, "the source is the sender's slot; the message has no source field to lie in");
            Assert.AreEqual(NoiseKind.Impact, m_Rig.HostWorld.LastNoiseKind);
            Assert.AreEqual(0.6f, m_Rig.HostWorld.LastNoiseLoudness, 0.0051f);
            Assert.AreEqual(4f, m_Rig.HostWorld.LastNoisePosition.x, Quant.PosCmMaxError);

            a.Session.SendNoise(V(0, 0, 0), 2.5f, NoiseKind.Cutting);
            m_Rig.Step(0.2);
            Assert.AreEqual(0.85f, m_Rig.HostWorld.LastNoiseLoudness, 1e-6f, "clamped to the kind's maximum");
            Assert.AreEqual(1, m_Rig.HostWorld.LastSlot);

            a.Session.SendNoise(V(0, 0, 0), 2.5f, NoiseKind.Voice);
            m_Rig.Step(0.2);
            Assert.AreEqual(1f, m_Rig.HostWorld.LastNoiseLoudness, 1e-6f, "default maximum");

            int before = m_Rig.HostWorld.Calls[NetMsg.Noise];
            a.Session.SendNoise(V(0, 0, 0), 1f, NoiseKind.Door);
            a.Session.SendNoise(V(0, 0, 0), 1f, NoiseKind.PanelBreak);
            a.Session.SendNoise(V(0, 0, 0), 1f, NoiseKind.Web);
            a.Session.SendNoise(V(0, 0, 0), 0f, NoiseKind.Impact);
            m_Rig.Step(0.2);
            Assert.AreEqual(before, m_Rig.HostWorld.Calls[NetMsg.Noise], "door, panel and web noises are the host's to make; silent noises are dropped");

            // a flood: 40 a second for three seconds from one client
            m_Rig.Step(1.0);
            before = m_Rig.HostWorld.Calls[NetMsg.Noise];
            int tick = 0;
            m_Rig.EachStep = now =>
            {
                if (tick++ % 5 == 0) { a.Session.SendNoise(V(1, 1, 1), 1f, NoiseKind.Impact); a.Session.SendNoise(V(1, 1, 1), 1f, NoiseKind.Thruster); }
            };
            m_Rig.Step(3.0);
            m_Rig.EachStep = null;
            int accepted = m_Rig.HostWorld.Calls[NetMsg.Noise] - before;
            Assert.LessOrEqual(accepted, 8 * 3 + 8, "never more than 8 in any second");
            Assert.GreaterOrEqual(accepted, 8 * 3 - 2, "but the allowance is really used");
            Assert.Greater(m_Rig.Host.Stats.rejected, 80);

            // the limit is per client
            before = m_Rig.HostWorld.Calls[NetMsg.Noise];
            b.Session.SendNoise(V(0, 0, 0), 0.5f, NoiseKind.Impact);
            m_Rig.Step(0.2);
            Assert.AreEqual(before + 1, m_Rig.HostWorld.Calls[NetMsg.Noise]);
        }

        // ------------------------------------------------------------------ reliable dispatch

        [Test]
        public void EveryClientRequest_ReachesTheHostWorld_Once_WithTheSendersSlot_UnderLoss()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            Assert.IsTrue(m_Rig.Host.SendRunStart(MakeRun(6)));
            m_Rig.Step(0.3);
            m_Rig.Hub.Loss = 0.25;
            m_Rig.Hub.Duplicate = 0.25;
            m_Rig.Hub.Reorder = 0.25;
            ScriptedHostWorld host = m_Rig.HostWorld;

            b.Session.SendSettingsChanged(1);
            b.Session.SendStartRequest();
            b.Session.SendExtractRequest();
            b.Session.SendDoorRequest(3, true);
            b.Session.SendPointCutReport(12, 40);
            b.Session.SendTranqHitReport(V(1, 2, 3), V(4, 5, 6));
            b.Session.SendWireReport(18, NetWireChange.Burnt);
            b.Session.SendGrabRequest(4, 0, 1);
            b.Session.SendItemDamageReport(4, 77);
            b.Session.SendItemReleased(4, 1, V(2, 2, 2), NetQuat.Identity, V(2, 1.5f, 2));
            b.Session.SendItemThrown(4, 1, V(1, 2, 3), V(0, 5, 0), 3.74f, true);
            b.Session.SendItemHandoff(4, 1, V(8, 1, 8), NetQuat.FromYawDegrees(90), V(0.1f, 0, 0), V(0, 0.2f, 0));
            b.Session.Flush(m_Rig.Now);
            Assert.IsTrue(m_Rig.StepUntil(() => b.Session.Backlog == 0 && host.Calls[NetMsg.ItemHandoff] == 1, 10.0));
            m_Rig.Step(1.0);

            foreach (byte type in new[] { NetMsg.SettingsChanged, NetMsg.StartRequest, NetMsg.ExtractRequest, NetMsg.DoorRequest, NetMsg.PointCutReport, NetMsg.TranqHitReport, NetMsg.WireReport, NetMsg.GrabRequest, NetMsg.ItemDamageReport, NetMsg.ItemReleased, NetMsg.ItemThrown, NetMsg.ItemHandoff })
                Assert.AreEqual(1, host.Calls[type], NetMsg.Name(type) + " reached the host world exactly once");
            Assert.AreEqual(2, host.LastSlot, "every request carries the sender's slot");
            Assert.AreEqual(1, host.Handedness[2]);
            Assert.AreEqual(1, m_Rig.Host.Handedness(2));
            Assert.AreEqual(1, a.World.Handedness[2], "the settings change was relayed to the other client");
            Assert.AreEqual(3, host.LastDoor);
            Assert.IsTrue(host.LastDesiredOpen);
            Assert.AreEqual(12, host.LastPanel);
            Assert.AreEqual(18, host.LastIndex, "WireReport was the last to set the index");
            Assert.AreEqual(NetWireChange.Burnt, host.LastWireChange);
            Assert.AreEqual(6f, host.LastHitPoint.z, Quant.PosCmMaxError);
            Assert.AreEqual(4, host.LastItem);
            Assert.AreEqual(1, host.LastHand);
            Assert.AreEqual(77, host.LastValue);
            Assert.AreEqual(1.5f, host.LastReleasePosition.y, Quant.PosCmMaxError);
            Assert.AreEqual(3.74f, host.LastThrowSpeed, 0.0051f);
            Assert.IsTrue(host.LastWasThrown);
            Assert.AreEqual(2f, host.LastLinearVelocity.y, 0.0051f);
            Assert.AreEqual(1, host.Returns.Count);
            Assert.AreEqual(ItemReturnCause.Handoff, host.Returns[0].cause);
            Assert.AreEqual(2, host.Returns[0].fromSlot);
            Assert.AreEqual(8f, host.Returns[0].position.x, Quant.PosCmMaxError);
            Assert.AreEqual(0.1f, host.Returns[0].linearVelocity.x, 0.0051f);
            Assert.Less(NetQuat.AngleDegrees(NetQuat.FromYawDegrees(90), host.Returns[0].rotation), Quant.QuatMaxErrorDegrees);
        }

        [Test]
        public void EveryHostBroadcast_ReachesEveryClient_ExactlyOnce_InTheSameOrder_UnderLoss()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            NetTestRig.Member c = m_Rig.Join();
            m_Rig.Step(0.3);
            foreach (NetTestRig.Member m in m_Rig.Members) m.World.Order.Clear();
            m_Rig.Hub.Loss = 0.3;
            m_Rig.Hub.Duplicate = 0.3;
            m_Rig.Hub.Reorder = 0.3;
            m_Rig.Hub.Latency = 0.04;
            NetSessionHost host = m_Rig.Host;

            var world = new WorldStateMsg { doorOpenMask = 0b101, panelOpenMask = 0b10 };
            world.SetWeldDone(5);
            var result = new RunEndedMsg { outcome = MissionOutcome.ContractComplete, quota = 500, depositedValue = 640, depositedCount = 4, payout = 890, bonus = 250, elapsedSeconds = 300.5f, oxygenRemainingSeconds = 44.25f };
            var sent = new List<byte>();
            void Did(byte type) { sent.Add(type); }

            Assert.IsTrue(host.SendRunStart(MakeRun(8))); Did(NetMsg.RunStart);
            host.SendWorldState(in world); Did(NetMsg.WorldState);
            host.SendHostPause(true); Did(NetMsg.HostPause);
            Assert.IsTrue(host.LocalGrab(2)); Did(NetMsg.ItemOwner);
            host.SendItemCollected(2, 0); Did(NetMsg.ItemCollected);
            host.LocalRelease(2); Did(NetMsg.ItemOwner);
            host.SendItemRest(2, V(1, 2, 3), NetQuat.FromYawDegrees(45)); Did(NetMsg.ItemRest);
            host.SendItemValue(2, 61); Did(NetMsg.ItemValue);
            host.SendItemDeposited(2, 0, 61, 25, 3); Did(NetMsg.ItemDeposited);
            host.SendItemRecovered(5, V(7, 8, 9)); Did(NetMsg.ItemRecovered);
            host.SendDoorState(4, true); Did(NetMsg.DoorState);
            host.SendPointCut(3, 2); Did(NetMsg.PointCut);
            host.SendPanelOpened(3, 1, V(0.5f, 1f, -0.25f), V(0, 2, 0)); Did(NetMsg.PanelOpened);
            host.SendCreatureStateChanged(EnemyState.Investigate, NetProtocol.NoSlot); Did(NetMsg.CreatureStateChanged);
            host.SendTranquilized(12.5f); Did(NetMsg.Tranquilized);
            host.SendCatchStarted(2); Did(NetMsg.CatchStarted);
            host.SendBlackoutStarted(18f); Did(NetMsg.BlackoutStarted);
            host.SendBlackoutEnded(false); Did(NetMsg.BlackoutEnded);
            host.SendWireChanged(7, NetWireChange.Snapped, 3); Did(NetMsg.WireChanged);
            host.SendRunEnded(in result); Did(NetMsg.RunEnded);
            host.SendReturnedToHub(); Did(NetMsg.ReturnedToHub);
            host.SetLocalHandedness(1); Did(NetMsg.RosterChanged);
            host.SendTranqTracer(2, V(0, 0, 0), V(1, 1, 1));   // everyone except slot 2
            host.SendExtractRefused(3, 0b0110);                // only slot 3
            host.Flush(m_Rig.Now);

            Assert.IsTrue(m_Rig.StepUntil(() => host.Backlog(1) == 0 && host.Backlog(2) == 0 && host.Backlog(3) == 0, 20.0), "everything was acknowledged despite 30% loss");
            m_Rig.Step(0.5);

            foreach (NetTestRig.Member m in m_Rig.Members)
            {
                BotClientWorld w = m.World;
                string who = "slot " + m.Slot + ": ";
                var expected = new List<byte>(sent);
                if (m.Slot != 2) expected.Add(NetMsg.TranqTracer);
                if (m.Slot == 3) expected.Add(NetMsg.ExtractRefused);
                CollectionAssert.AreEqual(expected, w.Order, who + "the reliable messages arrived exactly once and in the order sent");

                Assert.IsTrue(w.RunHashValid, who + "plan hash");
                Assert.AreEqual(8, w.Run.items.Length);
                Assert.AreEqual(5, w.Run.runIndex);
                Assert.IsTrue(w.World.IsWeldDone(5));
                Assert.IsTrue(w.HostPaused);
                Assert.AreEqual(0, w.ItemCollectedBy[2]);
                Assert.AreEqual(61, w.ItemValues[2]);
                Assert.AreEqual(0, w.ItemDepositedBy[2]);
                Assert.AreEqual(3, w.ItemLockerSlot[2]);
                Assert.IsTrue(w.Items[2].Secured);
                Assert.AreEqual(1, w.Items[2].Epoch, "one epoch step for the host's grab");
                Assert.AreEqual(1, w.RestsAccepted);
                Assert.AreEqual(7f, w.ItemPoses[5].position.x, Quant.PosCmMaxError);
                Assert.AreEqual(0b10101, w.DoorOpenMask, who + "world state then the door change");
                Assert.AreEqual(1u << 2, w.CutMask[3]);
                Assert.AreEqual(0b1010, w.PanelOpenMask);
                Assert.AreEqual(1f, w.LastPlateImpulse.y, 0.0051f);
                Assert.AreEqual(EnemyState.Investigate, w.CreatureEventState);
                Assert.AreEqual(NetProtocol.NoSlot, w.CreatureEventTarget);
                Assert.AreEqual(12.5f, w.TranquilizedSeconds, 0.051f);
                Assert.AreEqual(2, w.CatchVictim);
                Assert.AreEqual(18f, w.BlackoutSeconds, 0.051f);
                Assert.IsFalse(w.BlackoutOn);
                Assert.AreEqual(1u << 7, w.WireSnappedMask);
                Assert.AreEqual(MissionOutcome.ContractComplete, w.Result.outcome);
                Assert.AreEqual(890, w.Result.payout);
                Assert.AreEqual(44.25f, w.Result.oxygenRemainingSeconds);
                Assert.AreEqual(MissionPhase.Hub, w.HostPhase);
                Assert.AreEqual(1, w.Handedness[0]);
                Assert.AreEqual(m.Slot == 3 ? 0b0110 : 0, w.ExtractMissingMask, who + "ExtractRefused goes only to the requester");
            }
        }

        [Test]
        public void MessagesFromTheWrongDirection_AndUnknownTypes_AreCountedAndIgnored()
        {
            NetTestRig.Member c = m_Rig.Join();
            m_Rig.Step(0.2);
            c.Frozen = true; // only our hand-made datagrams arrive from the client's real address and connection
            var datagram = new byte[128];
            ushort seq = (ushort)(c.Session.ConnId + 5000);

            NetWriter Begin()
            {
                var w = new NetWriter(datagram);
                new DatagramHeader { protocol = NetProtocol.Version, connId = c.Session.ConnId, senderSlot = 1, seq = seq++ }.Write(ref w);
                return w;
            }

            // a stream type only the host may send: the whole datagram is dropped
            long malformedBefore = m_Rig.Host.Stats.malformed;
            NetWriter body = Begin();
            body.U8(NetMsg.CreatureState);
            new CreatureSnapshot().Write(ref body);
            body.U8(NetMsg.End);
            c.Transport.Send(m_Rig.HostTransport.Local, datagram, body.Position);
            m_Rig.Step(0.05);
            Assert.AreEqual(malformedBefore + 1, m_Rig.Host.Stats.malformed);

            // a well-formed datagram without the closing End byte is treated as cut short
            body = Begin();
            body.U8(NetMsg.AvatarState);
            AvatarSnapshot.Default.Write(ref body);
            int avatars = m_Rig.HostWorld.AvatarCount[1];
            c.Transport.Send(m_Rig.HostTransport.Local, datagram, body.Position);
            m_Rig.Step(0.05);
            Assert.AreEqual(malformedBefore + 2, m_Rig.Host.Stats.malformed);
            Assert.AreEqual(avatars, m_Rig.HostWorld.AvatarCount[1], "nothing from a cut datagram is applied");
            body.U8(NetMsg.End);
            c.Transport.Send(m_Rig.HostTransport.Local, datagram, body.Position);
            m_Rig.Step(0.05);
            Assert.AreEqual(avatars + 1, m_Rig.HostWorld.AvatarCount[1], "the same datagram, complete, is accepted (it was never acknowledged)");

            // bytes after the End marker are not allowed either
            body = Begin();
            body.U8(NetMsg.End);
            body.U8(NetMsg.End);
            c.Transport.Send(m_Rig.HostTransport.Local, datagram, body.Position);
            m_Rig.Step(0.05);
            Assert.AreEqual(malformedBefore + 3, m_Rig.Host.Stats.malformed);
            c.Frozen = false;
            m_Rig.Step(0.3);
            Assert.AreEqual(ClientState.Connected, c.Session.State);
            Assert.IsTrue(m_Rig.Host.IsConnected(1));
        }

        // ------------------------------------------------------------------ tap / tape

        sealed class Tape
        {
            public readonly List<byte> Types = new List<byte>();
            public readonly List<byte[]> Payloads = new List<byte[]>();
            public readonly List<double> Times = new List<double>();
        }

        [Test]
        public void MessageTap_RecordsHostToClientTraffic_AndReplayingItRebuildsTheSameClientState()
        {
            NetTestRig.Member live = m_Rig.AddClient();
            var tape = new Tape();
            m_Rig.Host.Tap = (outbound, peer, type, buffer, offset, length, time) =>
            {
                if (!outbound || peer != 1) return;
                var copy = new byte[length];
                Buffer.BlockCopy(buffer, offset, copy, 0, length);
                tape.Types.Add(type);
                tape.Payloads.Add(copy);
                tape.Times.Add(time);
            };
            int inbound = 0;
            NetMessageTap previous = m_Rig.Host.Tap;
            m_Rig.Host.Tap = (outbound, peer, type, buffer, offset, length, time) =>
            {
                if (!outbound) inbound++;
                previous(outbound, peer, type, buffer, offset, length, time);
            };
            Assert.IsTrue(m_Rig.StepUntil(() => live.Session.Welcomed, 3.0));

            // a scripted hosted contract
            NetSessionHost host = m_Rig.Host;
            AvatarSnapshot hostAvatar = AvatarSnapshot.Default;
            int tick = 0;
            m_Rig.EachStep = now =>
            {
                tick++;
                hostAvatar.headPosition = V(tick * 0.01f, 1.6f, 0);
                host.SetLocalAvatar(in hostAvatar);
                if (tick % 50 == 0) host.SetClock(tick * 0.01f, 300f - tick * 0.01f, 0);
                if (tick == 20) host.SendRunStart(MakeRun(5));
                if (tick == 60) host.SendDoorState(2, true);
                if (tick == 100) { host.LocalGrab(1); host.SendItemCollected(1, 0); }
                if (tick > 100 && tick < 160) host.QueueItemPose(1, V(tick * 0.02f, 1f, 2f), NetQuat.Identity, true, false);
                if (tick == 160) { host.LocalRelease(1); host.SendItemRest(1, V(3.2f, 0.4f, 2f), NetQuat.Identity); }
                if (tick == 180) host.SendPointCut(4, 1);
                if (tick == 200) host.SendItemDeposited(1, 0, 101, 0, 0);
                if (tick == 240) host.SendRunEnded(new RunEndedMsg { outcome = MissionOutcome.QuotaMissed, payout = 101, depositedValue = 101, depositedCount = 1 });
            };
            m_Rig.Step(3.0);
            m_Rig.EachStep = null;
            Assert.Greater(inbound, 30, "the tap also sees what the client sent (its avatars)");
            Assert.Greater(tape.Types.Count, 60);
            Assert.AreEqual(NetMsg.Welcome, tape.Types[0]);
            for (int i = 1; i < tape.Times.Count; i++) Assert.GreaterOrEqual(tape.Times[i], tape.Times[i - 1], "host time never runs backwards on the tape");

            // replay into a passive client that never touches the network
            var replayWorld = new BotClientWorld();
            MemoryTransport unused = m_Rig.Hub.Create();
            using (var passive = new NetSessionClient(unused, replayWorld, new NetClientConfig { contentHash = NetTestRig.Content }))
            {
                passive.SetReplaySlot(1);
                for (int i = 0; i < tape.Types.Count; i++)
                    Assert.IsTrue(passive.ApplyRecorded(tape.Types[i], tape.Payloads[i], 0, tape.Payloads[i].Length, tape.Times[i]), NetMsg.Name(tape.Types[i]) + " replays");
                Assert.AreEqual(0, passive.Stats.datagramsSent, "a replay sends nothing");
                Assert.IsFalse(passive.ApplyRecorded(NetMsg.DoorState, new byte[] { 1 }, 0, 1, 0), "a truncated tape entry is refused, not thrown on");
                Assert.IsFalse(passive.ApplyRecorded(NetMsg.DoorState, null, 0, 2, 0));
            }

            BotClientWorld liveWorld = live.World;
            CollectionAssert.AreEqual(liveWorld.Order, replayWorld.Order, "same reliable messages in the same order");
            foreach (byte type in new[] { NetMsg.Welcome, NetMsg.RunStart, NetMsg.DoorState, NetMsg.ItemOwner, NetMsg.ItemCollected, NetMsg.ItemRest, NetMsg.PointCut, NetMsg.ItemDeposited, NetMsg.RunEnded, NetMsg.RemoteAvatar, NetMsg.Clock, NetMsg.ItemPoses })
            {
                Assert.Greater(replayWorld.Calls[type], 0, NetMsg.Name(type) + " is on the tape");
                Assert.AreEqual(liveWorld.Calls[type], replayWorld.Calls[type], NetMsg.Name(type) + " count (live malformed " + live.Session.Stats.malformed + ", dup " + live.Session.Stats.duplicates + ")");
            }
            Assert.AreEqual(liveWorld.LocalSlot, replayWorld.LocalSlot);
            Assert.AreEqual(liveWorld.DoorOpenMask, replayWorld.DoorOpenMask);
            Assert.AreEqual(liveWorld.CutMask[4], replayWorld.CutMask[4]);
            Assert.AreEqual(liveWorld.Result.payout, replayWorld.Result.payout);
            Assert.AreEqual(liveWorld.ItemValues[1], replayWorld.ItemValues[1]);
            Assert.AreEqual(liveWorld.ItemLockerSlot[1], replayWorld.ItemLockerSlot[1]);
            Assert.AreEqual(liveWorld.Items[1].Epoch, replayWorld.Items[1].Epoch);
            Assert.AreEqual(liveWorld.PosesAccepted, replayWorld.PosesAccepted);
            Assert.AreEqual(liveWorld.ItemPoses[1].position.x, replayWorld.ItemPoses[1].position.x);
            Assert.AreEqual(liveWorld.Avatars[0].headPosition.x, replayWorld.Avatars[0].headPosition.x);
            Assert.AreEqual(liveWorld.ElapsedSeconds, replayWorld.ElapsedSeconds);
        }

        // ------------------------------------------------------------------ hostile input

        /// <summary>Wraps a transport and damages a share of what passes through it.</summary>
        sealed class TamperTransport : INetTransport
        {
            readonly INetTransport m_Inner;
            readonly Random m_Random;
            readonly byte[] m_Copy = new byte[NetProtocol.MaxDatagram + 64];
            public double TruncateOut, TruncateIn;
            public int Truncated;

            public TamperTransport(INetTransport inner, int seed) { m_Inner = inner; m_Random = new Random(seed); }
            public NetEndpoint Local => m_Inner.Local;
            public void Dispose() { m_Inner.Dispose(); }

            public bool Send(NetEndpoint to, byte[] data, int length)
            {
                if (length > 1 && m_Random.NextDouble() < TruncateOut)
                {
                    Truncated++;
                    return m_Inner.Send(to, data, m_Random.Next(0, length)); // the cut datagram replaces the real one
                }
                return m_Inner.Send(to, data, length);
            }

            public bool Receive(out NetEndpoint from, byte[] buffer, out int length)
            {
                if (!m_Inner.Receive(out from, buffer, out length)) return false;
                if (length > 1 && m_Random.NextDouble() < TruncateIn)
                {
                    Truncated++;
                    length = m_Random.Next(0, length);
                }
                return true;
            }
        }

        [Test]
        public void TruncatedDatagrams_AreDroppedWhole_AndReliableDeliveryStillCompletes()
        {
            TamperTransport tamper = null;
            NetTestRig.Member c = m_Rig.AddClient(wrap: inner => tamper = new TamperTransport(inner, 9));
            Assert.IsTrue(m_Rig.StepUntil(() => c.Session.Welcomed, 3.0));
            tamper.TruncateOut = 0.3; // 30% of what the client sends arrives cut short
            tamper.TruncateIn = 0.3;  // and 30% of what it receives
            var seen = new List<int>();
            c.Session.Tap = (outbound, peer, type, buffer, offset, length, time) =>
            {
                if (outbound || type != NetMsg.Tranquilized) return;
                var r = new NetReader(buffer, offset, length);
                TranquilizedMsg.Read(ref r, out TranquilizedMsg m);
                seen.Add((int)Math.Round(m.seconds * 10));
            };

            int sent = 0, requested = 0;
            m_Rig.EachStep = now =>
            {
                if (sent < 400) m_Rig.Host.SendTranquilized(sent++ * 0.1f);
                if (requested < 400) { c.Session.SendDoorRequest((byte)(requested % 16), requested % 2 == 0); requested++; }
            };
            Assert.DoesNotThrow(() => m_Rig.Step(8.0));
            m_Rig.EachStep = null;
            Assert.IsTrue(m_Rig.StepUntil(() => m_Rig.Host.Backlog(1) == 0 && c.Session.Backlog == 0, 30.0), "both directions drain despite the truncation");

            Assert.Greater(tamper.Truncated, 50);
            Assert.AreEqual(400, seen.Count, "host -> client: nothing lost, nothing doubled");
            for (int i = 0; i < 400; i++) Assert.AreEqual(i, seen[i]);
            Assert.AreEqual(400, m_Rig.HostWorld.Calls[NetMsg.DoorRequest], "client -> host: nothing lost, nothing doubled");
            Assert.Greater(m_Rig.Host.Stats.malformed, 20, "the cut datagrams were counted");
            Assert.Greater(c.Session.Stats.malformed, 20);
            Assert.AreEqual(ClientState.Connected, c.Session.State);
        }

        [Test]
        public void GarbageDatagrams_NeverThrow_AndTheSessionKeepsWorking()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            MemoryTransport rogue = m_Rig.Hub.Create();
            var random = new Random(31337);
            var garbage = new byte[1200];
            int thrown = 0;
            m_Rig.EachStep = now =>
            {
                for (int n = 0; n < 6; n++)
                {
                    random.NextBytes(garbage);
                    int length = random.Next(0, 1201);
                    switch (random.Next(0, 6))
                    {
                        case 0: // pure noise
                            break;
                        case 1: // looks connectionless: right frame, random type and body
                            garbage[1] = 0; garbage[2] = 0;
                            garbage[4] = 0x54; garbage[5] = 0x52; garbage[6] = 0x53; garbage[7] = 0x48;
                            garbage[0] = NetProtocol.Version;
                            garbage[3] = (byte)random.Next(0, 8);
                            break;
                        case 2: // a session header for a's real connection, then noise
                        {
                            var w = new NetWriter(garbage);
                            new DatagramHeader { protocol = NetProtocol.Version, connId = a.Session.ConnId, senderSlot = 1, seq = (ushort)random.Next(0, 65536), ack = (ushort)random.Next(0, 65536), ackBits = (uint)random.Next() }.Write(ref w);
                            break;
                        }
                        case 3: // a header claiming to be the host, for the clients
                        {
                            var w = new NetWriter(garbage);
                            new DatagramHeader { protocol = NetProtocol.Version, connId = b.Session.ConnId, senderSlot = 0, seq = (ushort)random.Next(0, 65536) }.Write(ref w);
                            break;
                        }
                        case 4: // tiny
                            length = random.Next(0, 9);
                            break;
                        default: // a well-formed message type byte followed by noise
                            garbage[16] = NetMsg.All[random.Next(0, NetMsg.All.Length)];
                            break;
                    }
                    rogue.Send(m_Rig.HostTransport.Local, garbage, length);
                    rogue.Send(a.Transport.Local, garbage, length);
                    rogue.Send(b.Transport.Local, garbage, length);
                }
            };
            try { m_Rig.Step(6.0); }
            catch (Exception e) { thrown++; Assert.Fail("garbage made a session throw: " + e); }
            m_Rig.EachStep = null;
            Assert.AreEqual(0, thrown);
            Assert.Greater(m_Rig.Host.Stats.malformed + m_Rig.Host.Stats.stray + m_Rig.Host.Stats.unknownMessages, 2000, "the junk was counted, not processed");
            Assert.Greater(a.Session.Stats.stray, 2000, "clients ignore everything that is not from the host's address");

            // everybody is still connected and reliable traffic still flows both ways
            Assert.AreEqual(ClientState.Connected, a.Session.State);
            Assert.AreEqual(ClientState.Connected, b.Session.State);
            Assert.AreEqual(3, m_Rig.Host.PlayerCount);
            Assert.AreEqual(2, m_Rig.HostWorld.JoinCount, "garbage created no players");
            int doorsBefore = m_Rig.HostWorld.Calls[NetMsg.DoorRequest];
            a.Session.SendDoorRequest(2, true);
            m_Rig.Host.SendDoorState(9, true);
            m_Rig.Step(0.5);
            Assert.AreEqual(doorsBefore + 1, m_Rig.HostWorld.Calls[NetMsg.DoorRequest]);
            Assert.AreEqual(1 << 9, a.World.DoorOpenMask & (1 << 9));
            Assert.AreEqual(1 << 9, b.World.DoorOpenMask & (1 << 9));
            rogue.Dispose();
        }

        [Test]
        public void GarbageFromTheHostsOwnAddress_NeverThrowsOnAClient()
        {
            // The one sender a client listens to. Random bytes behind a valid header cannot be told from real
            // traffic without a checksum (UDP has one), so the only promise is: no exception.
            NetTestRig.Member c = m_Rig.Join();
            m_Rig.HostFrozen = true;
            var random = new Random(8);
            var garbage = new byte[1200];
            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 4000; i++)
                {
                    random.NextBytes(garbage);
                    int length = random.Next(0, 1201);
                    if (i % 2 == 0)
                    {
                        var w = new NetWriter(garbage);
                        new DatagramHeader { protocol = NetProtocol.Version, connId = c.Session.ConnId, senderSlot = 0, seq = (ushort)(c.Session.ConnId + 100 + i) }.Write(ref w);
                        if (i % 4 == 0) garbage[16] = NetMsg.All[random.Next(0, NetMsg.All.Length)];
                    }
                    else if (i % 3 == 0)
                    {
                        garbage[1] = 0; garbage[2] = 0;
                        garbage[4] = 0x54; garbage[5] = 0x52; garbage[6] = 0x53; garbage[7] = 0x48;
                        garbage[3] = (byte)random.Next(0, 8);
                    }
                    m_Rig.HostTransport.Send(c.Transport.Local, garbage, length);
                    if (i % 20 == 0) m_Rig.Step(0.01);
                }
                m_Rig.Step(0.2);
            });
            Assert.Greater(c.Session.Stats.malformed, 500);
        }

        // ------------------------------------------------------------------ real sockets

        [Test]
        public void UdpTransport_BindsAnEphemeralPort_CountsLiveSockets_AndReportsABusyPort()
        {
            Assert.AreEqual(0, UdpTransport.LiveCount);
            UdpTransport a = UdpTransport.TryOpen(NetEndpoint.LoopbackAddress, 0, out string error);
            Assert.IsNotNull(a, "bind 127.0.0.1:0 failed: " + error);
            Assert.AreEqual(1, UdpTransport.LiveCount);
            Assert.AreNotEqual(0, a.Local.port, "the OS picked a port");
            Assert.AreEqual(NetEndpoint.LoopbackAddress, a.Local.address);

            UdpTransport clash = UdpTransport.TryOpen(NetEndpoint.LoopbackAddress, a.Local.port, out error);
            Assert.IsNull(clash, "a second bind of the same port fails: this is PORT BUSY on the board");
            Assert.IsNotNull(error);
            Assert.AreEqual(1, UdpTransport.LiveCount, "a failed bind leaves no socket behind");
            Assert.IsNull(UdpTransport.TryOpen(70000, out error));

            UdpTransport b = UdpTransport.TryOpen(NetEndpoint.LoopbackAddress, 0, out error);
            Assert.IsNotNull(b, error);
            Assert.AreEqual(2, UdpTransport.LiveCount);
            var buffer = new byte[1300];
            Assert.IsFalse(a.Receive(out _, buffer, out _), "nothing waiting, and no blocking");
            Assert.IsTrue(a.Send(b.Local, new byte[] { 1, 2, 3 }, 3));
            bool got = false;
            for (int i = 0; i < 2000 && !got; i++)
            {
                got = b.Receive(out NetEndpoint from, buffer, out int length);
                if (got)
                {
                    Assert.AreEqual(a.Local, from);
                    Assert.AreEqual(3, length);
                    Assert.AreEqual(2, buffer[1]);
                }
                else System.Threading.Thread.Sleep(1);
            }
            Assert.IsTrue(got, "loopback datagram arrived");
            Assert.IsFalse(a.Send(b.Local, null, 3));
            Assert.IsFalse(a.Send(b.Local, new byte[2], 3));

            a.Dispose();
            a.Dispose();
            Assert.IsFalse(a.IsOpen);
            Assert.IsFalse(a.Send(b.Local, new byte[] { 1 }, 1), "sending on a closed socket fails quietly");
            Assert.IsFalse(a.Receive(out _, buffer, out _));
            Assert.AreEqual(1, UdpTransport.LiveCount);
            b.Dispose();
            Assert.AreEqual(0, UdpTransport.LiveCount);
        }

        [Test]
        public void UdpTransport_LocalAddressHelper_NeverThrows_AndClassifiesAddresses()
        {
            uint address = 123;
            bool found = false;
            Assert.DoesNotThrow(() => found = UdpTransport.TryGetLocalAddress(out address));
            if (found) Assert.IsTrue(UdpTransport.IsUsableLanAddress(address), "whatever it found is a usable LAN address");
            else Assert.AreEqual(0u, address);
            Assert.DoesNotThrow(() => UdpTransport.TryGetLocalAddressByRoute(out _));
            Assert.DoesNotThrow(() => UdpTransport.TryGetLocalAddressByInterfaces(out _));
            Assert.AreEqual(0, UdpTransport.LiveCount, "the probe socket was closed");

            Assert.IsFalse(UdpTransport.IsUsableLanAddress(0));
            Assert.IsFalse(UdpTransport.IsUsableLanAddress(NetEndpoint.LoopbackAddress));
            Assert.IsFalse(UdpTransport.IsUsableLanAddress(NetEndpoint.BroadcastAddress));
            Assert.IsFalse(UdpTransport.IsUsableLanAddress(NetEndpoint.MakeAddress(169, 254, 3, 4)), "link-local");
            Assert.IsTrue(UdpTransport.IsUsableLanAddress(NetEndpoint.MakeAddress(192, 168, 1, 42)));
            Assert.IsTrue(UdpTransport.IsPrivateAddress(NetEndpoint.MakeAddress(192, 168, 1, 42)));
            Assert.IsTrue(UdpTransport.IsPrivateAddress(NetEndpoint.MakeAddress(10, 1, 2, 3)));
            Assert.IsTrue(UdpTransport.IsPrivateAddress(NetEndpoint.MakeAddress(172, 20, 2, 3)));
            Assert.IsFalse(UdpTransport.IsPrivateAddress(NetEndpoint.MakeAddress(172, 32, 2, 3)));
            Assert.IsFalse(UdpTransport.IsPrivateAddress(NetEndpoint.MakeAddress(8, 8, 8, 8)));
        }

        [Test]
        public void TwoUdpTransports_OnLoopback_CompleteAHandshake_And1000ReliableMessagesEachWay()
        {
            UdpTransport hostSocket = UdpTransport.TryOpen(NetEndpoint.LoopbackAddress, 0, out string error);
            Assert.IsNotNull(hostSocket, error);
            UdpTransport clientSocket = UdpTransport.TryOpen(NetEndpoint.LoopbackAddress, 0, out error);
            Assert.IsNotNull(clientSocket, error);
            Assert.AreEqual(2, UdpTransport.LiveCount);

            var hostWorld = new ScriptedHostWorld();
            var clientWorld = new BotClientWorld();
            var host = new NetSessionHost(hostSocket, hostWorld, new NetHostConfig { contentHash = 42, tugNumber = 1234 });
            var client = new NetSessionClient(clientSocket, clientWorld, new NetClientConfig { contentHash = 42 });
            var received = new List<int>();
            client.Tap = (outbound, peer, type, buffer, offset, length, time) =>
            {
                if (outbound || type != NetMsg.ItemValue) return;
                var r = new NetReader(buffer, offset, length);
                ItemValueMsg.Read(ref r, out ItemValueMsg m);
                received.Add(m.itemId * 1000 + m.value);
            };
            var requests = new List<int>();
            host.Tap = (outbound, peer, type, buffer, offset, length, time) =>
            {
                if (outbound || type != NetMsg.ItemDamageReport) return;
                var r = new NetReader(buffer, offset, length);
                ItemDamageReportMsg.Read(ref r, out ItemDamageReportMsg m);
                requests.Add(m.newValue);
            };

            try
            {
                // Session time is simulated (10 ms per loop) so the test does not depend on the machine's speed;
                // the sockets are real. A short sleep now and then lets the kernel move the datagrams.
                double now = 50.0;
                client.Connect(hostSocket.Local, now);
                int sentDown = 0, sentUp = 0;
                bool done = false;
                for (int i = 0; i < 20000 && !done; i++)
                {
                    now += 0.01;
                    host.Poll(now);
                    client.Poll(now);
                    if (client.Welcomed)
                    {
                        // 1000 reliable messages each way, a few per step, kept inside the 512 backlog
                        for (int n = 0; n < 8 && sentDown < 1000 && host.Backlog(1) < 400; n++) { host.SendItemValue((byte)(sentDown / 1000), sentDown); sentDown++; }
                        for (int n = 0; n < 8 && sentUp < 1000 && client.Backlog < 400; n++) { client.SendItemDamageReport(0, sentUp); sentUp++; }
                    }
                    host.SendTick(now);
                    client.SendTick(now);
                    done = received.Count == 1000 && requests.Count == 1000 && host.Backlog(1) == 0 && client.Backlog == 0;
                    if (i % 4 == 3) System.Threading.Thread.Sleep(1);
                }

                Assert.IsTrue(client.Welcomed, "handshake over real UDP on 127.0.0.1");
                Assert.AreEqual(1, client.LocalSlot);
                Assert.IsTrue(hostWorld.Joined[1]);
                Assert.AreEqual(1000, received.Count, "host -> client reliable messages");
                for (int i = 0; i < 1000; i++) Assert.AreEqual(i, received[i], "in order, exactly once");
                Assert.AreEqual(1000, requests.Count, "client -> host reliable messages");
                for (int i = 0; i < 1000; i++) Assert.AreEqual(i, requests[i]);
                Assert.AreEqual(0, hostSocket.SendErrors + clientSocket.SendErrors);
                Assert.LessOrEqual(host.Stats.largestDatagram, NetProtocol.MaxDatagram);

                client.Leave(now);
                for (int i = 0; i < 200 && host.IsConnected(1); i++)
                {
                    now += 0.01;
                    host.Poll(now);
                    System.Threading.Thread.Sleep(1);
                }
                Assert.IsFalse(host.IsConnected(1), "Bye arrived over UDP");
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
            Assert.AreEqual(0, UdpTransport.LiveCount, "disposing the sessions closed both sockets");
        }

        [Test]
        public void UdpTransport_SkipsEmptyDatagrams_AndStillDeliversWhatIsQueuedBehindThem()
        {
            // One empty UDP datagram (what `nc -uz` or a port scanner sends) must not make the socket deaf. On
            // Linux and Android "bytes available" is the size of the NEXT datagram, so a byte-count gate reads an
            // empty one as "nothing there" for ever. macOS counts differently, so this test passing here proves the
            // skip logic, not the Linux behaviour: the same check belongs in the on-device self-test.
            UdpTransport t = UdpTransport.TryOpen(NetEndpoint.LoopbackAddress, 0, out string error);
            Assert.IsNotNull(t, error);
            var raw = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                var target = new IPEndPoint(IPAddress.Loopback, t.Local.port);
                var buffer = new byte[NetProtocol.MaxDatagram + 64];
                var empty = new byte[0];

                raw.SendTo(empty, 0, 0, SocketFlags.None, target);
                raw.SendTo(new byte[] { 1, 2, 3, 4 }, 0, 4, SocketFlags.None, target);
                raw.SendTo(empty, 0, 0, SocketFlags.None, target);
                raw.SendTo(empty, 0, 0, SocketFlags.None, target);
                raw.SendTo(new byte[] { 9, 8, 7 }, 0, 3, SocketFlags.None, target);
                System.Threading.Thread.Sleep(50);

                Assert.IsTrue(t.Receive(out NetEndpoint from, buffer, out int length), "the first call already returns the real datagram behind the empty one");
                Assert.AreEqual(4, length);
                Assert.AreEqual(3, buffer[2]);
                Assert.AreEqual(NetEndpoint.LoopbackAddress, from.address);
                Assert.AreEqual(((IPEndPoint)raw.LocalEndPoint).Port, from.port);
                Assert.IsTrue(t.Receive(out _, buffer, out length), "and the next call the one behind two more");
                Assert.AreEqual(3, length);
                Assert.AreEqual(9, buffer[0]);
                Assert.IsFalse(t.Receive(out _, buffer, out length), "then the queue is empty");
                Assert.AreEqual(0, length);
                Assert.AreEqual(3, t.EmptyDatagrams, "every empty datagram was consumed and counted");
                Assert.AreEqual(0, t.ReceiveErrors);

                // A flood of empties: one call gives up after 16, the next carries on. Nothing is stuck.
                for (int i = 0; i < 20; i++) raw.SendTo(empty, 0, 0, SocketFlags.None, target);
                raw.SendTo(new byte[] { 5, 5 }, 0, 2, SocketFlags.None, target);
                System.Threading.Thread.Sleep(50);
                bool got = false;
                int calls = 0;
                for (; calls < 4 && !got; calls++) got = t.Receive(out _, buffer, out length);
                Assert.IsTrue(got, "the datagram behind 20 empties arrives");
                Assert.AreEqual(2, length);
                Assert.AreEqual(2, calls, "16 skipped by the first call, 4 by the second");
                Assert.AreEqual(23, t.EmptyDatagrams);

                // An oversized datagram (bigger than the buffer) is either cut or refused by the OS; whichever
                // it is, the real datagram behind it still arrives.
                raw.SendTo(new byte[3000], 0, 3000, SocketFlags.None, target);
                raw.SendTo(new byte[] { 6, 6, 6, 6, 6 }, 0, 5, SocketFlags.None, target);
                System.Threading.Thread.Sleep(50);
                got = false;
                for (int i = 0; i < 4 && !got; i++) got = t.Receive(out _, buffer, out length) && length == 5;
                Assert.IsTrue(got, "the datagram behind an oversized one arrives");
            }
            finally
            {
                raw.Close();
                t.Dispose();
            }
            Assert.AreEqual(0, UdpTransport.LiveCount);
        }

        [Test]
        public void AHostedSession_OverUdp_SurvivesEmptyAndJunkDatagramsOnItsPort()
        {
            UdpTransport hostSocket = UdpTransport.TryOpen(NetEndpoint.LoopbackAddress, 0, out string error);
            Assert.IsNotNull(hostSocket, error);
            UdpTransport clientSocket = UdpTransport.TryOpen(NetEndpoint.LoopbackAddress, 0, out error);
            Assert.IsNotNull(clientSocket, error);
            var hostWorld = new ScriptedHostWorld();
            var clientWorld = new BotClientWorld();
            var host = new NetSessionHost(hostSocket, hostWorld, new NetHostConfig { contentHash = 42 });
            var client = new NetSessionClient(clientSocket, clientWorld, new NetClientConfig { contentHash = 42 });
            var raw = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                var hostTarget = new IPEndPoint(IPAddress.Loopback, hostSocket.Local.port);
                var clientTarget = new IPEndPoint(IPAddress.Loopback, clientSocket.Local.port);
                double now = 20.0;
                client.Connect(hostSocket.Local, now);
                int delivered = 0;
                for (int i = 0; i < 4000 && delivered < 100; i++)
                {
                    now += 0.01;
                    // an empty datagram and a one-byte one at both ports, every step
                    raw.SendTo(new byte[0], 0, 0, SocketFlags.None, hostTarget);
                    raw.SendTo(new byte[] { 1 }, 0, 1, SocketFlags.None, hostTarget);
                    raw.SendTo(new byte[0], 0, 0, SocketFlags.None, clientTarget);
                    host.Poll(now);
                    client.Poll(now);
                    if (client.Welcomed && client.Backlog < 8 && hostWorld.Calls[NetMsg.DoorRequest] + client.Backlog < 100) client.SendDoorRequest(1, true);
                    host.SendTick(now);
                    client.SendTick(now);
                    delivered = hostWorld.Calls[NetMsg.DoorRequest];
                    if (i % 4 == 3) System.Threading.Thread.Sleep(1);
                }
                Assert.IsTrue(client.Welcomed, "joined while empty datagrams rained on both sockets");
                Assert.AreEqual(100, delivered, "and the reliable stream kept flowing");
                Assert.IsTrue(host.IsConnected(1));
                Assert.IsFalse(host.IsAway(1), "the host never went deaf");
                Assert.Greater(hostSocket.EmptyDatagrams, 20);
                Assert.Greater(host.Stats.malformed, 20, "the one-byte datagrams were counted and dropped");
            }
            finally
            {
                raw.Close();
                host.Dispose();
                client.Dispose();
            }
            Assert.AreEqual(0, UdpTransport.LiveCount);
        }

        // ------------------------------------------------------------------ review fixes: latency, goodbyes, ordering

        [Test]
        public void ReliableRequests_KeepFlowing_ThroughSessions_At300And500MsOneWay()
        {
            foreach (double oneWay in new[] { 0.3, 0.5 })
            {
                using (var rig = new NetTestRig(3))
                {
                    rig.HostWorld.AutoWorld = true; // every DoorRequest is answered with a DoorState broadcast
                    rig.Hub.Latency = oneWay;
                    NetTestRig.Member a = rig.Join();
                    NetTestRig.Member b = rig.Join();
                    int sent = 0, maxBacklog = 0;
                    rig.EachStep = now =>
                    {
                        if (a.Session.Backlog > maxBacklog) maxBacklog = a.Session.Backlog;
                        if (rig.Host.Backlog(1) > maxBacklog) maxBacklog = rig.Host.Backlog(1);
                        if (rig.Host.Backlog(2) > maxBacklog) maxBacklog = rig.Host.Backlog(2);
                    };
                    for (int i = 0; i < 320; i++)
                    {
                        a.Session.SendDoorRequest((byte)(i % 5), (i & 1) == 0);
                        sent++;
                        rig.Step(0.1);
                    }
                    Assert.IsTrue(rig.StepUntil(() => a.Session.Backlog == 0 && rig.Host.Backlog(1) == 0 && rig.Host.Backlog(2) == 0, 10.0), "everything acknowledged at " + oneWay + " s one way");
                    Assert.AreEqual(sent, rig.HostWorld.Calls[NetMsg.DoorRequest], "every request reached the host once");
                    Assert.AreEqual(sent, a.World.Calls[NetMsg.DoorState], "every answer reached the requester");
                    Assert.AreEqual(sent, b.World.Calls[NetMsg.DoorState], "and the bystander");
                    Assert.Less(maxBacklog, 64, "the backlog never filled the in-flight window (it used to climb to 512 and drop the client)");
                    Assert.AreEqual(ClientState.Connected, a.Session.State);
                    Assert.IsTrue(rig.Host.IsConnected(1) && rig.Host.IsConnected(2));
                    Assert.IsFalse(rig.Host.IsAway(1) || rig.Host.IsAway(2));
                }
            }
        }

        [Test]
        public void AfterAShortOutage_UnackedMessagesAreResentAtOnce_NotOnTheirBackedOffSchedule()
        {
            NetTestRig.Member c = m_Rig.Join();
            m_Rig.Step(0.3);
            // 1.1 s of total loss, shorter than the 1.5 s that would make the client Away. Meanwhile the host's
            // message has been re-sent at 0.1, 0.2, 0.3, 0.4, 0.6 and 1.0 s; its next copy is due at 1.8 s.
            m_Rig.Freeze(c);
            m_Rig.Host.SendDoorState(2, true);
            m_Rig.Step(1.1);
            Assert.IsFalse(m_Rig.Host.IsAway(1));
            Assert.AreEqual(1, m_Rig.Host.Backlog(1));
            c.Session.SendDoorRequest(7, true); // and the client has something for the host as well
            m_Rig.Thaw(c);
            m_Rig.Step(0.15);
            Assert.AreEqual(1, c.World.Calls[NetMsg.DoorState], "re-sent as soon as the client was heard again, not 0.7 s later");
            Assert.AreEqual(1, m_Rig.HostWorld.Calls[NetMsg.DoorRequest]);
            Assert.AreEqual(0, m_Rig.HostWorld.AwayChanges, "the outage was too short to count as Away");
            m_Rig.Step(0.2);
            Assert.AreEqual(0, m_Rig.Host.Backlog(1));
            Assert.AreEqual(0, c.Session.Backlog);
        }

        [Test]
        public void ClientBacklogOverflow_EndsTheSession_InsteadOfSilentlyLosingRequests()
        {
            NetTestRig.Member c = m_Rig.Join();
            m_Rig.Step(0.2);
            m_Rig.HostFrozen = true; // the host stops answering while the player keeps playing
            for (int i = 0; i < 512; i++) c.Session.SendDoorRequest((byte)(i % 5), true);
            m_Rig.Step(0.1);
            Assert.AreEqual(512, c.Session.Backlog);
            Assert.AreEqual(ClientState.Connected, c.Session.State, "512 waiting is still within the cap");

            c.Session.SendPointCutReport(1, 2); // the 513th cannot be queued
            m_Rig.Step(0.02);
            Assert.AreEqual(ClientState.Ended, c.Session.State, "a hole in the ordered stream ends the session");
            Assert.AreEqual(SessionEndReason.BacklogOverflow, c.Session.EndReason);
            Assert.AreEqual(SessionEndReason.BacklogOverflow, c.World.Ended);
            Assert.AreEqual(1, c.World.EndedCount);
            Assert.AreEqual(LobbyNotice.LinkLost, LobbyState.NoticeFor(SessionEndReason.BacklogOverflow, RefuseReason.None));

            // the host comes back: the connectionless Quit tells it the client is gone, without waiting 45 s
            m_Rig.HostFrozen = false;
            m_Rig.Step(0.2);
            Assert.IsFalse(m_Rig.Host.IsConnected(1));
            Assert.AreEqual(PlayerLeaveReason.Bye, m_Rig.HostWorld.LeftReason[1]);
            Assert.AreEqual(0, m_Rig.HostWorld.Calls[NetMsg.PointCutReport], "the message that could not be queued was never half-delivered");
        }

        [Test]
        public void HostClose_ReachesAClient_WhoseReliableWindowIsFull()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            m_Rig.Step(0.2);
            // 70 broadcasts and Close in the same frame: SessionClosed is the 71st message and only 64 are in
            // flight, so the reliable goodbye cannot be in the three forced datagrams.
            for (int i = 0; i < 70; i++) m_Rig.Host.SendDoorState((byte)(i % 5), true);
            m_Rig.Host.Close(m_Rig.Now);
            m_Rig.HostFrozen = true; // as if it were disposed right away: only what Close itself sent can tell them
            m_Rig.Step(0.1);
            foreach (NetTestRig.Member m in new[] { a, b })
            {
                Assert.AreEqual(ClientState.Ended, m.Session.State, "told at once, not after the 60 s host timeout");
                Assert.AreEqual(SessionEndReason.HostClosed, m.Session.EndReason);
                Assert.AreEqual(SessionEndReason.HostClosed, m.World.Ended);
                Assert.AreEqual(1, m.World.EndedCount);
            }
        }

        [Test]
        public void HostClose_WhenEveryGoodbyeDatagramIsLost_IsStillLearned_WhileTheHostIsNotYetDisposed()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            m_Rig.Step(0.2);
            m_Rig.HostTransport.Blackhole = true; // a burst of loss swallows SessionClosed x3 and Gone x2
            m_Rig.Host.Close(m_Rig.Now);
            m_Rig.HostTransport.Blackhole = false;
            Assert.AreEqual(ClientState.Connected, a.Session.State);
            // the closed host keeps being polled: each client's next datagram is answered with Gone(HostLeft)
            m_Rig.Step(0.3);
            foreach (NetTestRig.Member m in new[] { a, b })
            {
                Assert.AreEqual(ClientState.Ended, m.Session.State);
                Assert.AreEqual(SessionEndReason.HostClosed, m.Session.EndReason, "HOST LEFT, not LINK LOST");
                Assert.AreEqual(1, m.World.EndedCount);
            }
            Assert.AreEqual(2, m_Rig.HostWorld.LeftCount, "the host itself heard of each leave once, at Close");
        }

        [Test]
        public void Leave_FreesTheSlotAtOnce_EvenWhenTheClientsReliableWindowIsFull()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            m_Rig.Step(0.2);
            // 100 requests and LEAVE in the same frame: Bye is the 101st message, outside the 64 in flight.
            for (int i = 0; i < 100; i++) a.Session.SendDoorRequest((byte)(i % 5), true);
            a.Session.Leave(m_Rig.Now);
            Assert.AreEqual(ClientState.Ended, a.Session.State);
            m_Rig.Step(0.1);
            Assert.IsFalse(m_Rig.Host.IsConnected(1), "the connectionless Quit freed the slot; it used to be held for 45 s");
            Assert.AreEqual(PlayerLeaveReason.Bye, m_Rig.HostWorld.LeftReason[1]);
            Assert.AreEqual(1, m_Rig.HostWorld.LeftCount);
            Assert.IsFalse(b.World.Present[1]);
            Assert.AreEqual(1, m_Rig.Join().Slot, "and can be taken again");
        }

        [Test]
        public void Quit_IsOnlyTakenFromTheSlotsOwnAddressAndConnectionId()
        {
            NetTestRig.Member a = m_Rig.Join();
            NetTestRig.Member b = m_Rig.Join();
            m_Rig.Step(0.2);
            long strayBefore = m_Rig.Host.Stats.stray;
            var buffer = new byte[32];

            MemoryTransport rogue = m_Rig.Hub.Create();
            var w = new NetWriter(buffer);
            new QuitMsg { protocol = NetProtocol.Version, connId = a.Session.ConnId }.WriteDatagram(ref w);
            rogue.Send(m_Rig.HostTransport.Local, buffer, w.Position);               // right id, wrong address
            b.Transport.Send(m_Rig.HostTransport.Local, buffer, w.Position);          // another client naming a's connection
            w = new NetWriter(buffer);
            new QuitMsg { protocol = NetProtocol.Version, connId = (ushort)(a.Session.ConnId + 1) }.WriteDatagram(ref w);
            a.Transport.Send(m_Rig.HostTransport.Local, buffer, w.Position);          // right address, wrong id
            w = new NetWriter(buffer);
            new QuitMsg { protocol = 99, connId = a.Session.ConnId }.WriteDatagram(ref w);
            a.Transport.Send(m_Rig.HostTransport.Local, buffer, w.Position);          // wrong protocol
            a.Transport.Send(m_Rig.HostTransport.Local, buffer, w.Position - 1);      // truncated
            m_Rig.Step(0.2);
            Assert.IsTrue(m_Rig.Host.IsConnected(1) && m_Rig.Host.IsConnected(2), "nobody was kicked");
            Assert.AreEqual(strayBefore + 4, m_Rig.Host.Stats.stray);
            Assert.AreEqual(0, m_Rig.HostWorld.LeftCount);

            w = new NetWriter(buffer);
            new QuitMsg { protocol = NetProtocol.Version, connId = a.Session.ConnId }.WriteDatagram(ref w);
            a.Transport.Send(m_Rig.HostTransport.Local, buffer, w.Position);          // the real thing
            a.Frozen = true;
            m_Rig.Step(0.1);
            Assert.IsFalse(m_Rig.Host.IsConnected(1));
            Assert.IsTrue(m_Rig.Host.IsConnected(2));
            rogue.Dispose();
        }

        /// <summary>Wraps a transport; can drop or hold back single session datagrams (connId != 0) on their way in or out.</summary>
        sealed class ShuffleTransport : INetTransport
        {
            readonly INetTransport m_Inner;
            readonly byte[] m_Held = new byte[NetProtocol.MaxDatagram + 64];
            int m_HeldLength;
            NetEndpoint m_HeldPeer;
            bool m_HasHeld, m_HeldIsInbound, m_ReleaseNext;
            /// <summary>Drop this many inbound session datagrams.</summary>
            public int DropInbound;
            /// <summary>Hold the next outgoing / incoming session datagram back until the one after it has passed.</summary>
            public bool SwapNextTwoOut, SwapNextTwoIn;
            public int Dropped, Swapped;

            public ShuffleTransport(INetTransport inner) { m_Inner = inner; }
            public NetEndpoint Local => m_Inner.Local;
            public void Dispose() { m_Inner.Dispose(); }

            static bool IsSession(byte[] data, int length) => Connectionless.TryPeekConnId(data, length, out ushort connId) && connId != 0;

            public bool Send(NetEndpoint to, byte[] data, int length)
            {
                if (SwapNextTwoOut && !m_HasHeld && IsSession(data, length))
                {
                    Buffer.BlockCopy(data, 0, m_Held, 0, length);
                    m_HeldLength = length;
                    m_HeldPeer = to;
                    m_HasHeld = true;
                    m_HeldIsInbound = false;
                    SwapNextTwoOut = false;
                    return true;
                }
                bool sent = m_Inner.Send(to, data, length);
                if (m_HasHeld && !m_HeldIsInbound && IsSession(data, length))
                {
                    m_Inner.Send(m_HeldPeer, m_Held, m_HeldLength); // the older datagram goes out after the newer one
                    m_HasHeld = false;
                    Swapped++;
                }
                return sent;
            }

            public bool Receive(out NetEndpoint from, byte[] buffer, out int length)
            {
                if (m_ReleaseNext)
                {
                    m_ReleaseNext = false;
                    m_HasHeld = false;
                    Buffer.BlockCopy(m_Held, 0, buffer, 0, m_HeldLength);
                    from = m_HeldPeer;
                    length = m_HeldLength;
                    Swapped++;
                    return true;
                }
                while (m_Inner.Receive(out from, buffer, out length))
                {
                    if (!IsSession(buffer, length)) return true;
                    if (DropInbound > 0) { DropInbound--; Dropped++; continue; }
                    if (SwapNextTwoIn && !m_HasHeld)
                    {
                        Buffer.BlockCopy(buffer, 0, m_Held, 0, length);
                        m_HeldLength = length;
                        m_HeldPeer = from;
                        m_HasHeld = true;
                        m_HeldIsInbound = true;
                        SwapNextTwoIn = false;
                        continue;
                    }
                    if (m_HasHeld && m_HeldIsInbound) m_ReleaseNext = true; // the older one is handed over on the next call
                    return true;
                }
                return false;
            }
        }

        [Test]
        public void Welcome_IsTheFirstCallback_EvenWhenTheDatagramCarryingItIsLost()
        {
            // The host's first datagram to a joiner carries the Welcome. If it is lost, the next tick's datagram
            // (avatar, clock, creature, poses) arrives 50 ms before the Welcome is re-sent.
            m_Rig.EachStep = now =>
            {
                AvatarSnapshot hostAvatar = AvatarSnapshot.Default;
                hostAvatar.headPosition = V(1, 1.7f, 1);
                m_Rig.Host.SetLocalAvatar(in hostAvatar);
                m_Rig.Host.SetClock(12f, 200f, 0);
                m_Rig.Host.SetCreature(new CreatureSnapshot { position = V(3, 0, 3), state = EnemyState.Patrol, targetSlot = NetProtocol.NoSlot });
            };
            ShuffleTransport shuffle = null;
            NetTestRig.Member c = m_Rig.AddClient(wrap: inner => shuffle = new ShuffleTransport(inner) { DropInbound = 1 });
            Assert.IsTrue(m_Rig.StepUntil(() => c.Session.Welcomed, 3.0));
            m_Rig.Step(0.3);
            Assert.AreEqual(1, shuffle.Dropped, "the datagram with the Welcome was lost; the next one (streams only) got through");
            Assert.AreEqual(0, c.World.CallsBeforeWelcome, "nothing reached the game before OnWelcome");
            Assert.AreEqual(NetMsg.Welcome, c.World.Order[0]);
            Assert.Greater(c.World.Calls[NetMsg.RemoteAvatar], 2, "the streams flow once the Welcome is in");
            Assert.Greater(c.World.Calls[NetMsg.Clock], 2);
            Assert.Greater(c.World.Calls[NetMsg.CreatureState], 2);
            Assert.AreEqual(1f, c.World.Avatars[0].headPosition.x, 0.01f);
        }

        [Test]
        public void BroadcastsMadeInsideOnPlayerJoined_ReachTheJoinerAfterItsWelcome()
        {
            m_Rig.HostWorld.WhenJoined = slot =>
            {
                m_Rig.Host.SendHostPause(true);
                m_Rig.Host.SendDoorState(3, true);
            };
            NetTestRig.Member a = m_Rig.Join();
            m_Rig.Step(0.2);
            Assert.AreEqual(0, a.World.CallsBeforeWelcome);
            Assert.AreEqual(NetMsg.Welcome, a.World.Order[0], "the Welcome is queued before the game hears of the join");
            Assert.AreEqual(NetMsg.HostPause, a.World.Order[1]);
            Assert.AreEqual(NetMsg.DoorState, a.World.Order[2]);
            Assert.IsTrue(a.World.HostPaused, "and what the game broadcast survives the joiner's reset");
            Assert.AreEqual(1 << 3, a.World.DoorOpenMask);

            NetTestRig.Member b = m_Rig.Join();
            m_Rig.Step(0.2);
            Assert.AreEqual(NetMsg.Welcome, b.World.Order[0]);
            Assert.IsTrue(b.World.Present[1], "the roster in the Welcome does not depend on the callback");
            Assert.IsTrue(b.World.Present[2]);
            Assert.IsTrue(a.World.Present[2], "the earlier client is told about the join after the callback's broadcasts");
            Assert.AreEqual(2, a.World.Calls[NetMsg.HostPause]);
        }

        [Test]
        public void AnOvertakenDatagram_DeliversItsReliableMessages_ButItsOlderStreamsAreSkipped()
        {
            ShuffleTransport shuffle = null;
            NetTestRig.Member c = m_Rig.AddClient(wrap: inner => shuffle = new ShuffleTransport(inner));
            Assert.IsTrue(m_Rig.StepUntil(() => c.Session.Welcomed, 3.0));
            NetTestRig.Member watcher = m_Rig.Join();
            m_Rig.Step(0.3);

            // client -> host: the datagram with x = 1 (and a DoorRequest) is overtaken by the one with x = 2
            long staleBefore = m_Rig.Host.Stats.staleStreams;
            c.Avatar.headPosition = V(1, 1.6f, 0);
            c.Session.SendDoorRequest(4, true);
            shuffle.SwapNextTwoOut = true;
            Assert.IsTrue(m_Rig.StepUntil(() => !shuffle.SwapNextTwoOut, 0.2), "the x = 1 datagram is held back");
            c.Avatar.headPosition = V(2, 1.6f, 0);
            Assert.IsTrue(m_Rig.StepUntil(() => shuffle.Swapped == 1, 0.2), "and goes out after the x = 2 one");
            m_Rig.Step(0.02);
            Assert.AreEqual(1, m_Rig.Host.Stats.staleStreams - staleBefore);
            Assert.IsTrue(m_Rig.Host.TryGetAvatar(1, out AvatarSnapshot newest));
            Assert.AreEqual(2f, newest.headPosition.x, 0.01f, "the host keeps the newer avatar");
            Assert.AreEqual(2f, m_Rig.HostWorld.Avatars[1].headPosition.x, 0.01f, "and the game was never handed the older one after it");
            Assert.AreEqual(1, m_Rig.HostWorld.Calls[NetMsg.DoorRequest], "the reliable message in the late datagram still arrived, once");
            m_Rig.Step(0.2);
            Assert.AreEqual(2f, watcher.World.Avatars[1].headPosition.x, 0.01f, "the relay carries the newer one too");
            Assert.AreEqual(1, m_Rig.HostWorld.Calls[NetMsg.DoorRequest]);

            // host -> client: the same rule on the way down
            long clientStaleBefore = c.Session.Stats.staleStreams;
            AvatarSnapshot hostAvatar = AvatarSnapshot.Default;
            hostAvatar.headPosition = V(5, 1.7f, 0);
            m_Rig.EachStep = now => m_Rig.Host.SetLocalAvatar(in hostAvatar);
            m_Rig.Step(0.2);
            m_Rig.Host.SendDoorState(6, true);
            shuffle.SwapNextTwoIn = true;
            Assert.IsTrue(m_Rig.StepUntil(() => !shuffle.SwapNextTwoIn, 0.2), "the x = 5 datagram is held back on its way in");
            hostAvatar.headPosition = V(6, 1.7f, 0);
            Assert.IsTrue(m_Rig.StepUntil(() => shuffle.Swapped == 2, 0.3));
            m_Rig.Step(0.02);
            Assert.AreEqual(1, c.Session.Stats.staleStreams - clientStaleBefore);
            Assert.AreEqual(6f, c.World.Avatars[0].headPosition.x, 0.01f, "the client keeps the newer host avatar");
            Assert.AreEqual(1, c.World.Calls[NetMsg.DoorState], "and still got the reliable message from the late datagram");
            Assert.AreEqual(1 << 6, c.World.DoorOpenMask);
        }

        // ------------------------------------------------------------------ fuzz with the fake game

        sealed class FuzzClient
        {
            public MemoryTransport Transport;
            public FakeWorld.Client World;
            public NetSessionClient Session;
            public AvatarSnapshot Avatar = AvatarSnapshot.Default;
        }

        [Test]
        public void FakeWorld_HostAndThreeClients_2000SeededActions_Over80MsAnd10PercentLoss_EndWithEqualDigests()
        {
            foreach (int seed in new[] { 1, 2 })
            {
                var hub = new MemoryTransportHub(seed) { Latency = 0.08, Jitter = 0.02, Loss = 0.10, Duplicate = 0.05, Reorder = 0.10 };
                var hostWorld = new FakeWorld.Host(seed);
                var host = new NetSessionHost(hub.Create(NetProtocol.HostPort), hostWorld, new NetHostConfig { contentHash = 7, seed = seed });
                hostWorld.Session = host;
                var clients = new List<FuzzClient>();
                var random = new Random(seed * 7919);
                double now = 500.0;

                void Step(int steps)
                {
                    for (int s = 0; s < steps; s++)
                    {
                        hub.Advance(0.01);
                        now += 0.01;
                        host.Poll(now);
                        foreach (FuzzClient c in clients) { c.World.Now = now; c.Session.Poll(now); c.World.TickItems(now); }
                        host.SendTick(now);
                        foreach (FuzzClient c in clients) { c.Session.SetLocalAvatar(in c.Avatar); c.Session.SendTick(now); }
                    }
                }

                void AssertConverged(string when)
                {
                    uint digest = hostWorld.S.Digest();
                    foreach (FuzzClient c in clients)
                    {
                        Assert.AreEqual(ClientState.Connected, c.Session.State, when + ": slot " + c.World.LocalSlot + " dropped");
                        Assert.AreEqual(digest, c.World.S.Digest(), when + ": slot " + c.World.LocalSlot + " mirror differs from the host");
                        Assert.AreEqual(0, c.World.BadPlanHashes);
                        for (int i = 0; i < c.World.Items.Length; i++)
                        {
                            ItemAuthority item = c.World.Items[i];
                            Assert.AreEqual(host.Items.Epoch(i), item.Epoch, when + ": item " + i + " epoch on slot " + c.World.LocalSlot);
                            Assert.AreEqual(host.Items.Owner(i), item.OwnerSlot, when + ": item " + i + " owner on slot " + c.World.LocalSlot);
                            Assert.IsFalse(item.Pending, when + ": item " + i + " has an unanswered grab on slot " + c.World.LocalSlot);
                            Assert.AreEqual(host.Items.Owner(i) == c.World.LocalSlot, item.Mode == ItemMode.Mine, when + ": item " + i + " mode on slot " + c.World.LocalSlot);
                            Assert.AreEqual(host.Items.IsLocked(i), item.Secured, when + ": item " + i + " locked on slot " + c.World.LocalSlot);
                        }
                    }
                }

                for (int i = 0; i < 3; i++)
                {
                    var c = new FuzzClient { Transport = hub.Create(), World = new FakeWorld.Client() };
                    c.Session = new NetSessionClient(c.Transport, c.World, new NetClientConfig { contentHash = 7, seed = seed * 10 + i });
                    c.World.Session = c.Session;
                    clients.Add(c);
                    for (int attempt = 0; attempt < 5 && !c.World.Welcomed; attempt++)
                    {
                        c.Session.Connect(host.Transport.Local, now);
                        for (int s = 0; s < 600 && !c.World.Welcomed && c.Session.State != ClientState.Ended; s++) Step(1);
                    }
                    Assert.IsTrue(c.World.Welcomed, "seed " + seed + ": client " + i + " joined over the lossy link");
                }
                Assert.AreEqual(4, host.PlayerCount);

                int actions = 0, grabs = 0, hostGrabs = 0;
                var heldByHost = new List<byte>();
                while (actions < 2000)
                {
                    actions++;
                    FuzzClient who = clients[random.Next(0, clients.Count)];
                    FakeWorld.State hs = hostWorld.S;
                    byte item = (byte)random.Next(0, 12);
                    switch (random.Next(0, 16))
                    {
                        case 0: who.Session.SendStartRequest(); break;
                        case 1: if (random.Next(0, 3) == 0) who.Session.SendExtractRequest(); break;
                        case 2: who.Session.SendDoorRequest((byte)random.Next(0, FakeWorld.Doors), random.Next(0, 2) == 0); break;
                        case 3: who.Session.SendPointCutReport((byte)random.Next(0, FakeWorld.Panels), (byte)random.Next(0, FakeWorld.CutPointsPerPanel)); break;
                        case 4: who.Session.SendWireReport((byte)random.Next(0, FakeWorld.Wires), (NetWireChange)random.Next(0, 2)); break;
                        case 5:
                        case 6:
                        case 7: if (who.World.Grab(item)) grabs++; break;
                        case 8:
                        case 9: who.World.Release(item, now); break;
                        case 10: if (item < who.World.S.itemCount) who.Session.SendItemDamageReport(item, who.World.S.value[item] - random.Next(1, 40)); break;
                        case 11: hostWorld.Deposit(item, (byte)random.Next(0, 4)); break;
                        case 12: hostWorld.SetDoor((byte)random.Next(0, FakeWorld.Doors), random.Next(0, 2) == 0); break;
                        case 13:
                            if (hostWorld.HostGrab(item)) { hostGrabs++; heldByHost.Add(item); }
                            else if (heldByHost.Count > 0) { host.LocalRelease(heldByHost[0]); heldByHost.RemoveAt(0); }
                            break;
                        case 14: who.Avatar.headPosition = new NetVec3(random.Next(0, 4) != 0 ? 0f : 20f, 1.6f, 0f); break; // step in or out of the tug
                        case 15:
                            if (hs.phase == MissionPhase.Results && random.Next(0, 3) == 0) hostWorld.ReturnToHub();
                            else if (hs.phase == MissionPhase.Hub && random.Next(0, 3) == 0) { heldByHost.Clear(); hostWorld.StartRun(12); }
                            break;
                    }
                    Step(random.Next(1, 6));

                    if (actions % 100 == 0)
                    {
                        // let the network go quiet (hand-back timers, backstops, resends), then everyone must agree
                        Step(600);
                        AssertConverged("seed " + seed + " after " + actions + " actions");
                    }
                }

                Assert.Greater(hostWorld.StartCount, 1, "several contracts were played");
                Assert.Greater(hostWorld.GrantCount, 40, "clients really grabbed things");
                Assert.Greater(grabs, hostWorld.GrantCount, "and some grabs were denied");
                Assert.Greater(hostGrabs, 3);
                Assert.Greater(hostWorld.RefusedExtracts + clients[0].World.RefusedExtracts, 0, "the all-aboard rule refused somebody");
                Assert.Greater(hub.LostCount, 1000);
                Assert.LessOrEqual(hub.LargestDatagram, NetProtocol.MaxDatagram);
                Assert.AreEqual(0, hub.OversizeCount);
                for (int slot = 1; slot <= 3; slot++) Assert.Less(host.Backlog(slot), 8, "bounded reliable queues");

                host.Dispose();
                foreach (FuzzClient c in clients) c.Session.Dispose();
                Assert.AreEqual(0, hub.LiveCount);
            }
        }
    }
}

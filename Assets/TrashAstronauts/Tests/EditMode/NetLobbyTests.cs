using System;
using System.Collections.Generic;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Finding and joining a crew without a keyboard: the crew code, the /24 unicast sweep, Probe / HostInfo
    /// discovery, host list ageing, and the lobby board's state machine with its refusal reasons.
    /// </summary>
    public class NetLobbyTests
    {
        static readonly uint k_Home = NetEndpoint.MakeAddress(192, 168, 1, 42);

        // ------------------------------------------------------------------ crew code

        [Test]
        public void JoinCode_IsTheThirdAndFourthOctet_ShownAsThreeDigitsEach()
        {
            Assert.AreEqual(1, JoinCode.C(k_Home));
            Assert.AreEqual(42, JoinCode.D(k_Home));
            Assert.AreEqual("C 001  D 042", JoinCode.Format(k_Home), "exactly the board text from the plan");
            Assert.AreEqual("C 255  D 007", JoinCode.Format(NetEndpoint.MakeAddress(10, 0, 255, 7)));
            Assert.AreEqual("C 000  D 254", JoinCode.Format(0, 254));
            Assert.AreEqual("007", JoinCode.Pad3(7));
            Assert.AreEqual("000", JoinCode.Pad3(-3));
        }

        [Test]
        public void JoinCode_RebuildsTheHostsAddress_FromTheJoinersFirstTwoOctets()
        {
            uint joiner = NetEndpoint.MakeAddress(192, 168, 1, 77);
            Assert.AreEqual(k_Home, JoinCode.ToAddress(joiner, 1, 42), "same /24: only D differs");
            Assert.AreEqual(NetEndpoint.MakeAddress(192, 168, 9, 13), JoinCode.ToAddress(joiner, 9, 13), "a /16 network: C differs too");
            Assert.IsTrue(JoinCode.TryToEndpoint(joiner, 1, 42, NetProtocol.HostPort, out NetEndpoint endpoint));
            Assert.AreEqual(new NetEndpoint(192, 168, 1, 42, 47800), endpoint);
            Assert.AreEqual("192.168.1.42:47800", endpoint.ToString());

            Assert.IsFalse(JoinCode.TryToEndpoint(joiner, 1, 0, NetProtocol.HostPort, out _), "D 000 is the network address");
            Assert.IsFalse(JoinCode.TryToEndpoint(joiner, 1, 255, NetProtocol.HostPort, out _), "D 255 is the broadcast address");
            Assert.IsFalse(JoinCode.TryToEndpoint(joiner, 256, 5, NetProtocol.HostPort, out _));
            Assert.IsFalse(JoinCode.TryToEndpoint(0u, 1, 42, NetProtocol.HostPort, out _), "we do not know our own address");
            Assert.IsTrue(JoinCode.IsValid(0, 1));
            Assert.IsTrue(JoinCode.IsValid(255, 254));

            for (int seed = -50; seed < 500; seed++)
            {
                int tug = JoinCode.MakeTugNumber(seed);
                Assert.GreaterOrEqual(tug, 1000, "always four digits");
                Assert.LessOrEqual(tug, 9999);
            }
            Assert.AreEqual(JoinCode.MakeTugNumber(77), JoinCode.MakeTugNumber(77));
            Assert.AreNotEqual(JoinCode.MakeTugNumber(77), JoinCode.MakeTugNumber(78));
        }

        [Test]
        public void NetEndpoint_KnowsItsOctets_AndWhatIsABroadcast()
        {
            var e = new NetEndpoint(10, 20, 30, 40, 5000);
            Assert.AreEqual(10, e.A);
            Assert.AreEqual(20, e.B);
            Assert.AreEqual(30, e.C);
            Assert.AreEqual(40, e.D);
            Assert.AreEqual(e, new NetEndpoint(NetEndpoint.MakeAddress(10, 20, 30, 40), 5000));
            Assert.IsTrue(e == new NetEndpoint(10, 20, 30, 40, 5000));
            Assert.IsTrue(e != new NetEndpoint(10, 20, 30, 40, 5001));
            Assert.AreEqual(e.GetHashCode(), new NetEndpoint(10, 20, 30, 40, 5000).GetHashCode());
            Assert.IsFalse(e.IsBroadcast);
            Assert.IsFalse(e.IsNone);
            Assert.IsTrue(default(NetEndpoint).IsNone);
            Assert.IsTrue(NetEndpoint.Broadcast(47800).IsBroadcast);
            Assert.AreEqual("255.255.255.255:47800", NetEndpoint.Broadcast(47800).ToString());
            Assert.AreEqual("10.20.30.255:47800", NetEndpoint.SubnetBroadcast(e.address, 47800).ToString());
            Assert.IsTrue(NetEndpoint.SubnetBroadcast(e.address, 47800).IsBroadcast);
            Assert.AreEqual("127.0.0.1:9", NetEndpoint.Loopback(9).ToString());
            Assert.AreEqual(47800, NetProtocol.HostPort, "the host port from the plan");
        }

        // ------------------------------------------------------------------ sweep

        [Test]
        public void Sweep_CoversEveryHostAddressOfTheSlash24_ExceptOurOwn_InAboutTenFrames()
        {
            var sweep = new SweepGenerator(k_Home, NetProtocol.HostPort);
            var seen = new HashSet<uint>();
            var batch = new NetEndpoint[64];
            int frames = 0;
            while (!sweep.Done)
            {
                int n = sweep.NextBatch(batch);
                if (n == 0) break;
                frames++;
                Assert.LessOrEqual(n, 26, "a frame's worth is small");
                for (int i = 0; i < n; i++)
                {
                    NetEndpoint target = batch[i];
                    Assert.AreEqual(NetProtocol.HostPort, target.port);
                    Assert.AreEqual(k_Home & 0xFFFFFF00u, target.address & 0xFFFFFF00u, "stays inside our /24");
                    Assert.IsTrue(target.D >= 1 && target.D <= 254, "never .0 or .255");
                    Assert.AreNotEqual(k_Home, target.address, "never ourselves");
                    Assert.IsTrue(seen.Add(target.address), "no address twice");
                }
            }
            Assert.AreEqual(253, seen.Count, "all 254 host addresses minus our own");
            Assert.AreEqual(10, frames, "spread over ten frames");
            Assert.AreEqual(0, sweep.NextBatch(batch), "finished");
            Assert.IsFalse(sweep.Next(out _));
            Assert.AreEqual(0, sweep.NextBatch(null));
        }

        [Test]
        public void Sweep_HandlesEdgeAddresses_OtherFrameCounts_AndAnUnknownAddress()
        {
            var first = new SweepGenerator(NetEndpoint.MakeAddress(10, 0, 0, 1), 47800, 1);
            var batch = new NetEndpoint[300];
            Assert.AreEqual(253, first.NextBatch(batch), "one frame: everything at once");
            Assert.AreEqual(2, batch[0].D, "our own .1 is skipped");
            Assert.AreEqual(254, batch[252].D);
            Assert.IsTrue(first.Done);

            var last = new SweepGenerator(NetEndpoint.MakeAddress(10, 0, 0, 254), 47800, 4);
            int total = 0, frames = 0, n;
            while ((n = last.NextBatch(batch)) > 0) { total += n; frames++; }
            Assert.AreEqual(253, total);
            Assert.AreEqual(4, frames);

            var small = new SweepGenerator(k_Home, 47800, 10);
            var tiny = new NetEndpoint[5];
            Assert.AreEqual(5, small.NextBatch(tiny), "a short buffer just takes more calls");

            var unknown = new SweepGenerator(0u, 47800);
            Assert.IsTrue(unknown.Done, "without our own address there is no subnet to sweep");
            Assert.AreEqual(0, unknown.NextBatch(batch));
            Assert.AreEqual(253, new SweepGenerator(k_Home, 47800, 0).BatchSize, "a nonsense frame count is treated as one frame");
        }

        // ------------------------------------------------------------------ host list

        [Test]
        public void HostList_KeepsHostsOldestFirst_RefreshesThem_AndAgesThemOutAfterFiveSeconds()
        {
            var list = new HostList();
            var info = new HostInfoMsg { protocol = NetProtocol.Version, contentHash = 9, tugNumber = 4821, playerCount = 2, phase = MissionPhase.Hub };
            NetEndpoint a = new NetEndpoint(192, 168, 1, 10, 47800), b = new NetEndpoint(192, 168, 1, 11, 47800);
            Assert.IsTrue(list.Offer(a, in info, 100.0));
            Assert.IsTrue(list.Offer(b, in info, 101.0));
            info.playerCount = 3;
            Assert.IsFalse(list.Offer(a, in info, 103.0), "seen again: refreshed, not added");
            Assert.AreEqual(2, list.Count);
            Assert.AreEqual(a, list[0].endpoint);
            Assert.AreEqual(3, list[0].info.playerCount);
            Assert.AreEqual(100.0, list[0].firstSeen);
            Assert.AreEqual(103.0, list[0].lastSeen);
            Assert.AreEqual("TUG 4821  3/4  IN HUB", list[0].Label(), "the board line from the plan");

            Assert.AreEqual(0, list.Age(105.9));
            Assert.AreEqual(1, list.Age(106.0), "b was last heard 5 s ago");
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual(a, list[0].endpoint);
            Assert.AreEqual(1, list.Age(108.0));
            Assert.AreEqual(0, list.Count);
            Assert.IsTrue(list[0].endpoint.IsNone, "indexing an empty list is safe");
            Assert.IsTrue(list[-1].endpoint.IsNone);

            // what Discovery does when a search ends: keep only the hosts that answered since it started
            list.Offer(a, in info, 150.0);
            list.Offer(b, in info, 160.0);
            Assert.AreEqual(0, list.RemoveNotSeenSince(150.0));
            Assert.AreEqual(1, list.RemoveNotSeenSince(155.0), "a did not answer the search that started at 155");
            Assert.AreEqual(b, list[0].endpoint);
            Assert.AreEqual(1, list.RemoveNotSeenSince(999.0));
            Assert.AreEqual(0, list.Count);

            for (int i = 0; i < 20; i++) list.Offer(new NetEndpoint(192, 168, 1, (byte)(50 + i), 47800), in info, 200.0);
            Assert.AreEqual(HostList.Capacity, list.Count, "bounded");
            Assert.AreEqual(3, list.ShownCount, "the board shows up to three");
            list.Clear();
            Assert.AreEqual(0, list.ShownCount);
        }

        [Test]
        public void HostEntry_PredictsWhyAJoinWouldBeRefused()
        {
            var entry = new HostEntry { info = new HostInfoMsg { protocol = NetProtocol.Version, contentHash = 9, playerCount = 2, phase = MissionPhase.Results, tugNumber = 1000 } };
            Assert.AreEqual(RefuseReason.None, entry.Predict(NetProtocol.Version, 9));
            Assert.AreEqual("TUG 1000  2/4  RESULTS", entry.Label());
            Assert.AreEqual(RefuseReason.VersionMismatch, entry.Predict((byte)(NetProtocol.Version + 1), 9));
            Assert.AreEqual(RefuseReason.ContentMismatch, entry.Predict(NetProtocol.Version, 10));
            entry.info.phase = MissionPhase.Active;
            Assert.AreEqual(RefuseReason.ContractInProgress, entry.Predict(NetProtocol.Version, 9));
            Assert.AreEqual("TUG 1000  2/4  ON CONTRACT", entry.Label());
            entry.info.phase = MissionPhase.Hub;
            entry.info.playerCount = 4;
            Assert.AreEqual(RefuseReason.Full, entry.Predict(NetProtocol.Version, 9));
        }

        // ------------------------------------------------------------------ discovery over the wire

        sealed class Lan : IDisposable
        {
            public readonly MemoryTransportHub Hub = new MemoryTransportHub(3);
            public readonly List<NetSessionHost> Hosts = new List<NetSessionHost>();
            public readonly List<ScriptedHostWorld> Worlds = new List<ScriptedHostWorld>();
            public MemoryTransport Finder;
            public Discovery Discovery = new Discovery(NetProtocol.Version, 5);
            public double Now => 300.0 + Hub.Now;

            public NetSessionHost AddHost(byte lastOctet, ushort tug, uint content = 0xABCD, byte subnet = 1)
            {
                var world = new ScriptedHostWorld();
                var host = new NetSessionHost(Hub.Create(new NetEndpoint(192, 168, subnet, lastOctet, NetProtocol.HostPort)), world, new NetHostConfig { contentHash = content, tugNumber = tug, seed = lastOctet });
                world.Session = host;
                Hosts.Add(host);
                Worlds.Add(world);
                return host;
            }

            public void Step(double seconds, bool tickDiscovery = true)
            {
                int steps = (int)Math.Round(seconds / 0.01);
                for (int i = 0; i < steps; i++)
                {
                    Hub.Advance(0.01);
                    foreach (NetSessionHost host in Hosts) host.Tick(Now);
                    if (tickDiscovery) Discovery.Tick(Finder, Now);
                }
            }

            public void Dispose()
            {
                foreach (NetSessionHost host in Hosts) host.Dispose();
                Finder?.Dispose();
            }
        }

        [Test]
        public void FindCrew_FindsAHost_ByBroadcast_AndReportsItsTugRosterAndPhase()
        {
            using (var lan = new Lan())
            {
                NetSessionHost host = lan.AddHost(42, 4821);
                lan.Worlds[0].CurrentPhase = MissionPhase.Results;
                lan.Finder = lan.Hub.Create(new NetEndpoint(192, 168, 1, 77, 50123));
                long hostSentBefore = host.Stats.datagramsSent;
                lan.Step(0.5, false);
                Assert.AreEqual(hostSentBefore, host.Stats.datagramsSent, "a host never broadcasts or announces itself; it only answers");

                lan.Discovery.Start(lan.Finder.Local.address, lan.Now);
                Assert.IsTrue(lan.Discovery.IsSearching);
                lan.Step(0.3);
                Assert.AreEqual(1, lan.Discovery.Hosts.Count);
                HostEntry found = lan.Discovery.Hosts[0];
                Assert.AreEqual(new NetEndpoint(192, 168, 1, 42, NetProtocol.HostPort), found.endpoint, "the answer is unicast from the host's own address");
                Assert.AreEqual(4821, found.info.tugNumber);
                Assert.AreEqual(1, found.info.playerCount);
                Assert.AreEqual(MissionPhase.Results, found.info.phase);
                Assert.AreEqual(0xABCDu, found.info.contentHash);
                Assert.AreEqual(NetProtocol.Version, found.info.protocol);
                Assert.AreEqual("TUG 4821  1/4  RESULTS", found.Label());
                Assert.AreEqual("C 001  D 042", JoinCode.Format(found.endpoint.address), "and it matches the code on the host's board");
                Assert.AreEqual(RefuseReason.None, found.Predict(NetProtocol.Version, 0xABCD));
                Assert.AreEqual(1, host.PlayerCount, "probing takes no slot");
            }
        }

        [Test]
        public void FindCrew_SendsThreeRoundsASecondApart_OfBroadcastSubnetBroadcastAndA253AddressSweep()
        {
            using (var lan = new Lan())
            {
                lan.Finder = lan.Hub.Create(new NetEndpoint(192, 168, 1, 77, 50123));
                lan.Discovery.Start(lan.Finder.Local.address, lan.Now);
                lan.Discovery.Tick(lan.Finder, lan.Now);
                Assert.AreEqual(2, lan.Discovery.ProbesSent, "first the two broadcasts");
                Assert.AreEqual(ProbeMsg.Size, lan.Hub.LargestDatagram, "a probe is 12 bytes");
                Assert.AreEqual(12, lan.Hub.LargestDatagram);
                for (int i = 0; i < 9; i++) lan.Discovery.Tick(lan.Finder, lan.Now);
                Assert.Less(lan.Discovery.ProbesSent, 255, "the sweep is still spread out after nine frames");
                lan.Discovery.Tick(lan.Finder, lan.Now);
                Assert.AreEqual(255, lan.Discovery.ProbesSent, "round one: 2 broadcasts + 253 unicasts over ten frames");
                lan.Step(0.9);
                Assert.AreEqual(255, lan.Discovery.ProbesSent, "nothing more until a second has passed");
                lan.Step(0.3);
                Assert.AreEqual(510, lan.Discovery.ProbesSent, "round two");
                lan.Step(1.0);
                Assert.AreEqual(765, lan.Discovery.ProbesSent, "round three");
                Assert.IsTrue(lan.Discovery.IsSearching);
                lan.Step(1.0);
                Assert.IsFalse(lan.Discovery.IsSearching, "the search lasts 3 s");
                lan.Step(3.0);
                Assert.AreEqual(765, lan.Discovery.ProbesSent, "and then stops on its own");
                Assert.AreEqual(765L * 12, lan.Hub.BytesSent, "about 9 kB for a whole search");
            }
        }

        [Test]
        public void FindCrew_StillFindsTheHost_ByUnicastSweep_WhenBroadcastIsDropped()
        {
            using (var lan = new Lan())
            {
                lan.AddHost(200, 1234);
                lan.Finder = lan.Hub.Create(new NetEndpoint(192, 168, 1, 77, 50123));
                lan.Finder.BroadcastBlocked = true; // what Horizon OS may do without a multicast lock
                lan.Discovery.Start(lan.Finder.Local.address, lan.Now);
                lan.Step(0.5);
                Assert.AreEqual(1, lan.Discovery.Hosts.Count, "found by the sweep, which needs only INTERNET");
                Assert.AreEqual(200, lan.Discovery.Hosts[0].endpoint.D);
                Assert.AreEqual(1234, lan.Discovery.Hosts[0].info.tugNumber);
            }
        }

        [Test]
        public void FindCrew_WithoutKnowingOurOwnAddress_FallsBackToTheGlobalBroadcastOnly()
        {
            using (var lan = new Lan())
            {
                lan.AddHost(42, 4821);
                lan.Finder = lan.Hub.Create(new NetEndpoint(192, 168, 1, 77, 50123));
                lan.Discovery.Start(0u, lan.Now);
                lan.Step(3.5);
                Assert.AreEqual(3, lan.Discovery.ProbesSent, "one 255.255.255.255 probe per round, no sweep");
                Assert.AreEqual(1, lan.Discovery.Hosts.Count);
                Assert.IsFalse(lan.Discovery.IsSearching);
            }
        }

        [Test]
        public void FindCrew_ListsSeveralHosts_IncludingIncompatibleOnes_AndForgetsThoseThatVanish()
        {
            using (var lan = new Lan())
            {
                lan.AddHost(10, 1111);
                lan.AddHost(20, 2222, content: 0x9999);          // a different build
                NetSessionHost busy = lan.AddHost(30, 3333);
                lan.Worlds[2].CurrentPhase = MissionPhase.Active; // on a contract
                lan.AddHost(40, 4444, subnet: 2);                 // another /24: reached by 255.255.255.255 on this hub
                lan.Finder = lan.Hub.Create(new NetEndpoint(192, 168, 1, 77, 50123));
                lan.Discovery.Start(lan.Finder.Local.address, lan.Now);
                lan.Step(0.5);
                Assert.AreEqual(4, lan.Discovery.Hosts.Count);
                Assert.AreEqual(3, lan.Discovery.Hosts.ShownCount);
                var byTug = new Dictionary<int, HostEntry>();
                for (int i = 0; i < lan.Discovery.Hosts.Count; i++) byTug[lan.Discovery.Hosts[i].info.tugNumber] = lan.Discovery.Hosts[i];
                Assert.AreEqual(RefuseReason.None, byTug[1111].Predict(NetProtocol.Version, 0xABCD));
                Assert.AreEqual(RefuseReason.ContentMismatch, byTug[2222].Predict(NetProtocol.Version, 0xABCD), "the board can grey it out before anybody presses JOIN");
                Assert.AreEqual(RefuseReason.ContractInProgress, byTug[3333].Predict(NetProtocol.Version, 0xABCD));
                Assert.AreEqual(2, byTug[4444].endpoint.C);

                // one host goes away while the board is up: what the search found stays listed after it ends
                lan.Step(3.0);
                Assert.IsFalse(lan.Discovery.IsSearching);
                Assert.AreEqual(4, lan.Discovery.Hosts.Count);
                busy.Dispose();
                lan.Hosts.Remove(busy);
                lan.Step(20.0);
                Assert.AreEqual(4, lan.Discovery.Hosts.Count, "nothing ages while no search is running: the JOIN buttons stay put");

                // a new search keeps only the hosts that answer it
                lan.Discovery.Start(lan.Finder.Local.address, lan.Now);
                lan.Step(0.5);
                Assert.AreEqual(4, lan.Discovery.Hosts.Count, "still listed while the new search runs");
                lan.Step(3.0);
                Assert.IsFalse(lan.Discovery.IsSearching);
                Assert.AreEqual(3, lan.Discovery.Hosts.Count, "the vanished host did not answer this search and is gone when it ends");
                for (int i = 0; i < 3; i++) Assert.AreNotEqual(3333, lan.Discovery.Hosts[i].info.tugNumber);
            }
        }

        [Test]
        public void Discovery_IgnoresAnswersToOtherSearches_Garbage_AndNonHostInfoDatagrams()
        {
            using (var lan = new Lan())
            {
                lan.Finder = lan.Hub.Create(new NetEndpoint(192, 168, 1, 77, 50123));
                lan.Discovery.Start(lan.Finder.Local.address, lan.Now);
                uint nonce = lan.Discovery.Nonce;
                Assert.AreNotEqual(0u, nonce);
                var from = new NetEndpoint(192, 168, 1, 9, 47800);
                var buffer = new byte[64];

                var w = new NetWriter(buffer);
                new HostInfoMsg { protocol = NetProtocol.Version, nonce = nonce + 1, tugNumber = 1 }.WriteDatagram(ref w);
                Assert.IsFalse(lan.Discovery.HandleDatagram(from, buffer, w.Position, lan.Now), "an answer to somebody else's probe");

                w = new NetWriter(buffer);
                new ProbeMsg { protocol = NetProtocol.Version, nonce = nonce }.WriteDatagram(ref w);
                Assert.IsFalse(lan.Discovery.HandleDatagram(from, buffer, w.Position, lan.Now), "a probe is not an answer");

                w = new NetWriter(buffer);
                new HostInfoMsg { protocol = NetProtocol.Version, nonce = nonce, tugNumber = 7, playerCount = 9 }.WriteDatagram(ref w);
                Assert.IsFalse(lan.Discovery.HandleDatagram(from, buffer, w.Position, lan.Now), "five players do not exist");

                w = new NetWriter(buffer);
                new HostInfoMsg { protocol = NetProtocol.Version, nonce = nonce, tugNumber = 7, playerCount = 2 }.WriteDatagram(ref w);
                for (int cut = 0; cut < w.Position; cut++)
                    Assert.IsFalse(lan.Discovery.HandleDatagram(from, buffer, cut, lan.Now), "truncated at " + cut);
                Assert.IsFalse(lan.Discovery.HandleDatagram(from, null, 20, lan.Now));

                var random = new Random(6);
                var garbage = new byte[300];
                Assert.DoesNotThrow(() =>
                {
                    for (int i = 0; i < 5000; i++)
                    {
                        random.NextBytes(garbage);
                        if (i % 2 == 0) { garbage[1] = 0; garbage[2] = 0; garbage[3] = NetMsg.HostInfo; garbage[4] = 0x54; garbage[5] = 0x52; garbage[6] = 0x53; garbage[7] = 0x48; }
                        lan.Discovery.HandleDatagram(from, garbage, random.Next(0, 300), lan.Now);
                    }
                });
                Assert.AreEqual(0, lan.Discovery.Hosts.Count, "none of that listed a host");
                Assert.Greater(lan.Discovery.Ignored, 5000);

                Assert.IsTrue(lan.Discovery.HandleDatagram(from, buffer, w.Position, lan.Now), "the genuine answer is accepted");
                Assert.AreEqual(1, lan.Discovery.Hosts.Count);
                Assert.AreEqual(1, lan.Discovery.RepliesAccepted);

                // a new search has a new nonce: the old answer no longer counts
                lan.Discovery.Start(lan.Finder.Local.address, lan.Now);
                Assert.AreNotEqual(nonce, lan.Discovery.Nonce);
                Assert.IsFalse(lan.Discovery.HandleDatagram(new NetEndpoint(192, 168, 1, 10, 47800), buffer, w.Position, lan.Now));
                lan.Discovery.Stop();
                Assert.IsFalse(lan.Discovery.IsSearching);
                Assert.DoesNotThrow(() => lan.Discovery.Tick(null, lan.Now));
            }
        }

        [Test]
        public void AHost_AnswersProbesFromOtherVersions_SoTheBoardCanSayWhy()
        {
            using (var lan = new Lan())
            {
                lan.AddHost(42, 4821);
                lan.Finder = lan.Hub.Create(new NetEndpoint(192, 168, 1, 77, 50123));
                var old = new Discovery((byte)(NetProtocol.Version + 3), 1);
                old.Start(lan.Finder.Local.address, lan.Now);
                for (int i = 0; i < 50; i++)
                {
                    lan.Hub.Advance(0.01);
                    lan.Hosts[0].Tick(lan.Now);
                    old.Tick(lan.Finder, lan.Now);
                }
                Assert.AreEqual(1, old.Hosts.Count, "found, even though it could not join");
                Assert.AreEqual(NetProtocol.Version, old.Hosts[0].info.protocol, "HostInfo says which version the host speaks");
                Assert.AreEqual(RefuseReason.VersionMismatch, old.Hosts[0].Predict((byte)(NetProtocol.Version + 3), 0xABCD));
            }
        }

        // ------------------------------------------------------------------ lobby state machine

        [Test]
        public void Lobby_Host_IsRefusedOutsideHubAndResults_OrWhenThePortIsBusy_OrWithoutWifi()
        {
            var lobby = new LobbyState();
            Assert.AreEqual(LobbyMode.Offline, lobby.Mode);
            Assert.IsFalse(lobby.SessionOpen);
            Assert.AreEqual("", lobby.RosterLine);

            Assert.IsFalse(lobby.CanHost(MissionPhase.Active));
            Assert.IsFalse(lobby.Host(MissionPhase.Active, true, k_Home, 4821), "not during a contract");
            Assert.AreEqual(LobbyNotice.NotInHub, lobby.Notice);
            Assert.AreEqual(LobbyMode.Offline, lobby.Mode);
            Assert.AreEqual("FINISH THE CONTRACT FIRST", lobby.NoticeLine);

            Assert.IsTrue(lobby.CanHost(MissionPhase.Hub));
            Assert.IsFalse(lobby.Host(MissionPhase.Hub, false, k_Home, 4821), "the bind failed");
            Assert.AreEqual(LobbyNotice.PortBusy, lobby.Notice);
            Assert.AreEqual("PORT BUSY", lobby.NoticeLine, "the plan's wording; no port hunting");
            Assert.AreEqual(LobbyMode.Offline, lobby.Mode);

            Assert.IsFalse(lobby.Host(MissionPhase.Hub, true, 0u, 4821));
            Assert.AreEqual(LobbyNotice.NoNetwork, lobby.Notice);

            Assert.IsTrue(lobby.Host(MissionPhase.Results, true, k_Home, 4821), "Results is allowed");
            Assert.AreEqual(LobbyMode.Hosting, lobby.Mode);
            Assert.AreEqual(LobbyNotice.None, lobby.Notice);
            Assert.IsTrue(lobby.SessionOpen);
            Assert.AreEqual("TUG 4821", lobby.TitleLine);
            Assert.AreEqual("C 001  D 042", lobby.CodeLine);
            Assert.AreEqual("CREW 1/4", lobby.RosterLine);
            lobby.OnRoster(3);
            Assert.AreEqual("CREW 3/4", lobby.RosterLine);
            lobby.OnRoster(4);
            Assert.AreEqual("CREW 4/4", lobby.RosterLine);

            Assert.IsFalse(lobby.Host(MissionPhase.Hub, true, k_Home, 1), "already hosting");
            Assert.IsFalse(lobby.Find(MissionPhase.Hub, k_Home, 0), "cannot search while hosting");
            Assert.IsFalse(lobby.Join(MissionPhase.Hub, new NetEndpoint(192, 168, 1, 9, 47800)), "cannot join while hosting");
            Assert.AreEqual(LobbyMode.Hosting, lobby.Mode);

            lobby.Leave();
            Assert.AreEqual(LobbyMode.Offline, lobby.Mode);
            Assert.AreEqual("", lobby.TitleLine);
            Assert.AreEqual(1, lobby.PlayerCount);
            Assert.IsFalse(lobby.SessionOpen);
        }

        [Test]
        public void Lobby_FindThenJoin_TakesTwoPresses_AndEndsJoinedWithItsSlot()
        {
            var lobby = new LobbyState();
            Assert.IsFalse(lobby.Find(MissionPhase.Active, k_Home, 10.0), "not during a contract");
            Assert.AreEqual(LobbyNotice.NotInHub, lobby.Notice);

            Assert.IsTrue(lobby.Find(MissionPhase.Hub, k_Home, 10.0));                      // press 1: FIND CREW
            Assert.AreEqual(LobbyMode.Searching, lobby.Mode);
            Assert.IsFalse(lobby.SessionOpen, "searching is not a session");
            var host = new NetEndpoint(192, 168, 1, 42, NetProtocol.HostPort);
            Assert.IsFalse(lobby.Join(MissionPhase.Active, host));
            Assert.AreEqual(LobbyNotice.NotInHub, lobby.Notice);
            Assert.IsFalse(lobby.Join(MissionPhase.Hub, default(NetEndpoint)), "no host selected");
            Assert.IsTrue(lobby.Join(MissionPhase.Hub, host));                              // press 2: JOIN
            Assert.AreEqual(LobbyMode.Connecting, lobby.Mode);
            Assert.AreEqual(host, lobby.Target);
            Assert.IsTrue(lobby.SessionOpen);
            Assert.AreEqual(LobbyNotice.None, lobby.Notice);

            lobby.OnWelcome(2, 3);
            Assert.AreEqual(LobbyMode.Joined, lobby.Mode);
            Assert.AreEqual(2, lobby.LocalSlot);
            Assert.AreEqual("CREW 3/4", lobby.RosterLine);
            Assert.AreEqual("", lobby.TitleLine, "only the host shows a tug number of its own");
            lobby.OnRoster(2);
            Assert.AreEqual(2, lobby.PlayerCount);

            lobby.Leave();                                                                   // LEAVE: one press
            Assert.AreEqual(LobbyMode.Offline, lobby.Mode);
            Assert.AreEqual(0, lobby.LocalSlot);
            Assert.AreEqual(LobbyNotice.None, lobby.Notice);
            Assert.IsTrue(lobby.Target.IsNone);
        }

        [Test]
        public void Lobby_ShowsWhyAJoinFailed_ForEveryRefusalAndEveryWayASessionEnds()
        {
            var cases = new[]
            {
                (SessionEndReason.Refused, RefuseReason.Full, LobbyNotice.Full, "CREW FULL"),
                (SessionEndReason.Refused, RefuseReason.VersionMismatch, LobbyNotice.VersionMismatch, "DIFFERENT GAME VERSION"),
                (SessionEndReason.Refused, RefuseReason.ContentMismatch, LobbyNotice.ContentMismatch, "DIFFERENT BUILD"),
                (SessionEndReason.Refused, RefuseReason.ContractInProgress, LobbyNotice.ContractInProgress, "CREW IS ON A CONTRACT"),
                (SessionEndReason.ConnectTimeout, RefuseReason.None, LobbyNotice.NoAnswer, "NO ANSWER"),
                (SessionEndReason.HostClosed, RefuseReason.None, LobbyNotice.HostClosed, "HOST LEFT"),
                (SessionEndReason.HostTimeout, RefuseReason.None, LobbyNotice.LinkLost, "LINK LOST"),
                (SessionEndReason.Dropped, RefuseReason.None, LobbyNotice.Dropped, "DROPPED BY HOST"),
            };
            foreach (var (reason, refusal, notice, text) in cases)
            {
                var lobby = new LobbyState();
                Assert.IsTrue(lobby.Join(MissionPhase.Hub, new NetEndpoint(192, 168, 1, 42, 47800)));
                if (reason != SessionEndReason.Refused && reason != SessionEndReason.ConnectTimeout) lobby.OnWelcome(1, 2);
                lobby.OnSessionEnded(reason, refusal);
                Assert.AreEqual(LobbyMode.Offline, lobby.Mode, reason + ": back to single-player");
                Assert.AreEqual(notice, lobby.Notice, reason + "/" + refusal);
                Assert.AreEqual(text, lobby.NoticeLine);
                Assert.IsFalse(lobby.SessionOpen);
                Assert.AreEqual(notice, LobbyState.NoticeFor(reason, refusal));
            }
            Assert.AreEqual(LobbyNotice.None, LobbyState.NoticeFor(SessionEndReason.LocalLeave, RefuseReason.None));
            Assert.AreEqual("", LobbyState.NoticeText(LobbyNotice.None));
            foreach (LobbyNotice n in Enum.GetValues(typeof(LobbyNotice)))
                if (n != LobbyNotice.None) Assert.IsNotEmpty(LobbyState.NoticeText(n), n + " has board text");

            var offline = new LobbyState();
            offline.OnSessionEnded(SessionEndReason.HostClosed, RefuseReason.None);
            Assert.AreEqual(LobbyNotice.None, offline.Notice, "a stray callback while offline changes nothing");
            offline.OnWelcome(1, 2);
            Assert.AreEqual(LobbyMode.Offline, offline.Mode);
        }

        [Test]
        public void Lobby_HintsAtRouterIsolation_AfterTenSecondsOfFindingNobody()
        {
            var lobby = new LobbyState();
            lobby.Find(MissionPhase.Hub, k_Home, 100.0);
            lobby.SearchTick(103.0, 0);
            Assert.AreEqual(LobbyNotice.None, lobby.Notice);
            lobby.Find(MissionPhase.Hub, k_Home, 104.0); // FIND pressed again: 4 s of emptiness so far
            lobby.SearchTick(109.0, 0);
            Assert.AreEqual(LobbyNotice.None, lobby.Notice, "9 s in total");
            lobby.SearchTick(110.1, 0);
            Assert.AreEqual(LobbyNotice.NobodyFound, lobby.Notice, "10 s of searching with nobody found");
            StringAssert.Contains("ENTER CODE", lobby.NoticeLine, "the hint points at the fallback");
            lobby.Find(MissionPhase.Hub, k_Home, 111.0);
            Assert.AreEqual(LobbyNotice.NobodyFound, lobby.Notice, "the hint stays while searching again");
            lobby.SearchTick(111.5, 1);
            Assert.AreEqual(LobbyNotice.None, lobby.Notice, "found somebody: hint gone");
            lobby.SearchTick(120.0, 0);
            Assert.AreEqual(LobbyNotice.None, lobby.Notice, "and the 10 s start over");
            Assert.AreEqual(LobbyState.NobodyHintSeconds, 10.0, 1e-12);
        }

        [Test]
        public void Lobby_Keypad_PrefillsCWithOurThirdOctet_SoOnlyTheThreeDigitsOfDAreTyped()
        {
            var lobby = new LobbyState();
            Assert.IsFalse(lobby.PressDigit(1), "the keypad is not open");
            Assert.IsFalse(lobby.EnterCode(MissionPhase.Active, k_Home));
            Assert.AreEqual(LobbyNotice.NotInHub, lobby.Notice);
            Assert.IsFalse(lobby.EnterCode(MissionPhase.Hub, 0u));
            Assert.AreEqual(LobbyNotice.NoNetwork, lobby.Notice);

            uint joiner = NetEndpoint.MakeAddress(192, 168, 1, 77);
            Assert.IsTrue(lobby.EnterCode(MissionPhase.Hub, joiner));
            Assert.AreEqual(LobbyMode.EnteringCode, lobby.Mode);
            Assert.AreEqual(1, lobby.CodeC, "prefilled from our own third octet");
            Assert.AreEqual(CodeField.D, lobby.Field);
            Assert.AreEqual("C 001  D ___", lobby.CodeEntryLine);
            Assert.IsFalse(lobby.TryGetCodeTarget(NetProtocol.HostPort, out _), "D is empty");

            Assert.IsTrue(lobby.PressDigit(0));
            Assert.AreEqual("C 001  D 0__", lobby.CodeEntryLine);
            Assert.IsTrue(lobby.PressDigit(4));
            Assert.IsTrue(lobby.PressDigit(2));
            Assert.AreEqual("C 001  D 042", lobby.CodeEntryLine);
            Assert.IsTrue(lobby.TryGetCodeTarget(NetProtocol.HostPort, out NetEndpoint target));
            Assert.AreEqual(new NetEndpoint(192, 168, 1, 42, NetProtocol.HostPort), target, "our first two octets + C.D");

            Assert.IsTrue(lobby.Join(MissionPhase.Hub, target));
            Assert.AreEqual(LobbyMode.Connecting, lobby.Mode);
            Assert.AreEqual(target, lobby.Target);
        }

        [Test]
        public void Lobby_Keypad_RejectsBadCodes_AndCanCorrectBothFields()
        {
            var lobby = new LobbyState();
            uint joiner = NetEndpoint.MakeAddress(10, 5, 1, 77);
            lobby.EnterCode(MissionPhase.Hub, joiner);

            lobby.PressDigit(3);
            lobby.PressDigit(0);
            Assert.IsFalse(lobby.PressDigit(0), "300 is not an octet");
            Assert.AreEqual(LobbyNotice.BadCode, lobby.Notice);
            Assert.AreEqual(30, lobby.CodeD, "the bad digit was not taken");
            Assert.IsTrue(lobby.PressBack());
            Assert.AreEqual(3, lobby.CodeD);
            Assert.IsTrue(lobby.PressDigit(1));
            Assert.AreEqual(LobbyNotice.None, lobby.Notice, "a good digit clears the complaint");
            Assert.IsTrue(lobby.TryGetCodeTarget(47800, out NetEndpoint target));
            Assert.AreEqual(31, target.D);

            Assert.IsFalse(lobby.PressDigit(5), "315 is not an octet either");
            Assert.AreEqual(LobbyNotice.BadCode, lobby.Notice);
            Assert.AreEqual("BAD CODE", lobby.NoticeLine);
            Assert.AreEqual(31, lobby.CodeD);
            Assert.IsTrue(lobby.TryGetCodeTarget(47800, out target), "the code typed so far is still usable");
            Assert.AreEqual(new NetEndpoint(10, 5, 1, 31, 47800), target);
        }

        [Test]
        public void Lobby_Keypad_FourthDigitStartsOver_AndCChangesForLargerSubnets()
        {
            var lobby = new LobbyState();
            uint joiner = NetEndpoint.MakeAddress(10, 5, 1, 77);
            lobby.EnterCode(MissionPhase.Hub, joiner);
            lobby.PressDigit(1);
            lobby.PressDigit(2);
            lobby.PressDigit(3);
            Assert.AreEqual(123, lobby.CodeD);
            Assert.IsTrue(lobby.PressDigit(9), "a fourth digit starts the field over");
            Assert.AreEqual(9, lobby.CodeD);
            Assert.AreEqual("C 001  D 9__", lobby.CodeEntryLine);
            Assert.IsTrue(lobby.PressBack());
            Assert.IsFalse(lobby.PressBack(), "nothing left to remove");
            Assert.IsFalse(lobby.TryGetCodeTarget(47800, out _));

            // the host is on 10.5.7.x: change C
            Assert.IsTrue(lobby.SelectField(CodeField.C));
            Assert.AreEqual("C ___  D ___", lobby.CodeEntryLine);
            lobby.PressDigit(0);
            lobby.PressDigit(0);
            lobby.PressDigit(7);
            Assert.IsTrue(lobby.SelectField(CodeField.D));
            lobby.PressDigit(2);
            lobby.PressDigit(5);
            lobby.PressDigit(4);
            Assert.AreEqual("C 007  D 254", lobby.CodeEntryLine);
            Assert.IsTrue(lobby.TryGetCodeTarget(47800, out NetEndpoint target));
            Assert.AreEqual(new NetEndpoint(10, 5, 7, 254, 47800), target);

            // unusable codes
            lobby.SelectField(CodeField.D);
            lobby.PressDigit(0);
            Assert.IsFalse(lobby.TryGetCodeTarget(47800, out _), "D 0 is the network address");
            lobby.SelectField(CodeField.D);
            lobby.PressDigit(2); lobby.PressDigit(5); lobby.PressDigit(5);
            Assert.IsFalse(lobby.TryGetCodeTarget(47800, out _), "D 255 is the broadcast address");
            lobby.SelectField(CodeField.C);
            lobby.PressDigit(1);
            lobby.SelectField(CodeField.D);
            lobby.PressDigit(7); lobby.PressDigit(7);
            Assert.IsFalse(lobby.TryGetCodeTarget(47800, out _), "that is our own address");
            Assert.IsFalse(lobby.PressDigit(10));
            Assert.IsFalse(lobby.PressDigit(-1));

            lobby.Leave();
            Assert.IsFalse(lobby.SelectField(CodeField.C));
            Assert.IsFalse(lobby.PressBack());
        }

        [Test]
        public void Lobby_BlocksStartingAContract_WhileASessionIsOpen_UntilContractsAreEnabled()
        {
            var lobby = new LobbyState();
            Assert.IsFalse(lobby.BlocksContractStart, "offline: single-player as always");
            lobby.Find(MissionPhase.Hub, k_Home, 0);
            Assert.IsFalse(lobby.BlocksContractStart, "searching opens no session");
            lobby.Host(MissionPhase.Hub, true, k_Home, 4821);
            Assert.IsTrue(lobby.BlocksContractStart, "Stage 1b: the board says a contract cannot start in a session");
            lobby.ContractsEnabled = true; // Stage 2 turns shared contracts on
            Assert.IsFalse(lobby.BlocksContractStart);
            lobby.ContractsEnabled = false;
            lobby.Leave();
            Assert.IsFalse(lobby.BlocksContractStart);
            lobby.Join(MissionPhase.Hub, new NetEndpoint(192, 168, 1, 42, 47800));
            Assert.IsTrue(lobby.BlocksContractStart, "also while connecting and joined");
            lobby.OnWelcome(1, 2);
            Assert.IsTrue(lobby.BlocksContractStart);
            Assert.IsTrue(LobbyState.PhaseAllowsSession(MissionPhase.Hub));
            Assert.IsTrue(LobbyState.PhaseAllowsSession(MissionPhase.Results));
            Assert.IsFalse(LobbyState.PhaseAllowsSession(MissionPhase.Active));
        }

        [Test]
        public void AFoundHost_StaysOnTheBoard_HoweverLongThePlayerTakesToPressJoin()
        {
            // The probes stop after 3 s. Entries used to expire 5 s after their last answer, so a host that was
            // still there vanished from the board 7 s after FIND CREW, and 10 s later the board suggested the
            // router was isolating clients.
            using (var lan = new Lan())
            {
                NetSessionHost host = lan.AddHost(42, 4821);
                lan.Finder = lan.Hub.Create(new NetEndpoint(192, 168, 1, 77, 50123));
                var lobby = new LobbyState();
                Assert.IsTrue(lobby.Find(MissionPhase.Hub, lan.Finder.Local.address, lan.Now));
                lan.Discovery.Start(lan.Finder.Local.address, lan.Now);
                long probesAtEnd = -1;
                for (int i = 0; i < 3000; i++) // 30 s with the board open
                {
                    lan.Step(0.01);
                    lobby.SearchTick(lan.Now, lan.Discovery.Hosts.Count);
                    if (i >= 50) Assert.AreEqual(1, lan.Discovery.Hosts.Count, "listed at " + (i * 0.01) + " s");
                    Assert.AreEqual(LobbyNotice.None, lobby.Notice, "no NOBODY FOUND hint at " + (i * 0.01) + " s");
                    if (!lan.Discovery.IsSearching && probesAtEnd < 0) probesAtEnd = lan.Discovery.ProbesSent;
                }
                Assert.IsFalse(lan.Discovery.IsSearching);
                Assert.AreEqual(probesAtEnd, lan.Discovery.ProbesSent, "and that without sending a single probe after the 3 s");
                Assert.AreEqual(LobbyMode.Searching, lobby.Mode);
                HostEntry entry = lan.Discovery.Hosts[0];
                Assert.AreEqual(4821, entry.info.tugNumber);
                Assert.IsTrue(lobby.Join(MissionPhase.Hub, entry.endpoint), "JOIN still works half a minute later");

                // Searching again with nobody there empties the list at the end of that search, and then the hint comes.
                lobby.Leave();
                host.Dispose();
                lan.Hosts.Remove(host);
                Assert.IsTrue(lobby.Find(MissionPhase.Hub, lan.Finder.Local.address, lan.Now));
                lan.Discovery.Start(lan.Finder.Local.address, lan.Now);
                for (int i = 0; i < 1400; i++)
                {
                    lan.Step(0.01);
                    lobby.SearchTick(lan.Now, lan.Discovery.Hosts.Count);
                }
                Assert.AreEqual(0, lan.Discovery.Hosts.Count, "the host that did not answer the second search is gone");
                Assert.AreEqual(LobbyNotice.NobodyFound, lobby.Notice, "10 s with an empty list");
            }
        }

        // ------------------------------------------------------------------ the whole path, on one transport

        [Test]
        public void FindThenJoin_EndToEnd_OnOneClientSocket_FromSearchToWelcome()
        {
            using (var lan = new Lan())
            {
                NetSessionHost host = lan.AddHost(42, 4821, content: 0x5151);
                var hostLobby = new LobbyState();
                Assert.IsTrue(hostLobby.Host(lan.Worlds[0].Phase, true, host.Transport.Local.address, host.Config.tugNumber));
                Assert.AreEqual("C 001  D 042", hostLobby.CodeLine);

                // the joiner: one ephemeral socket used first by Discovery, then by the client session
                MemoryTransport socket = lan.Hub.Create(new NetEndpoint(192, 168, 1, 77, 50999));
                var lobby = new LobbyState();
                var world = new BotClientWorld();
                Assert.IsTrue(lobby.Find(MissionPhase.Hub, socket.Local.address, lan.Now));
                lan.Finder = null;
                lan.Discovery.Start(socket.Local.address, lan.Now);
                for (int i = 0; i < 50 && lan.Discovery.Hosts.Count == 0; i++)
                {
                    lan.Hub.Advance(0.01);
                    host.Tick(lan.Now);
                    lan.Discovery.Tick(socket, lan.Now);
                    lobby.SearchTick(lan.Now, lan.Discovery.Hosts.Count);
                }
                Assert.AreEqual(1, lan.Discovery.Hosts.Count);
                HostEntry entry = lan.Discovery.Hosts[0];
                Assert.AreEqual(RefuseReason.None, entry.Predict(NetProtocol.Version, 0x5151));

                Assert.IsTrue(lobby.Join(MissionPhase.Hub, entry.endpoint));
                lan.Discovery.Stop();
                var client = new NetSessionClient(socket, world, new NetClientConfig { contentHash = 0x5151, seed = 3 });
                client.Connect(lobby.Target, lan.Now);
                for (int i = 0; i < 200 && !client.Welcomed; i++)
                {
                    lan.Hub.Advance(0.01);
                    host.Tick(lan.Now);
                    client.Tick(lan.Now); // late HostInfo answers still in the socket are simply not session traffic
                }
                Assert.IsTrue(client.Welcomed);
                Assert.AreEqual(ClientState.Connected, client.State);
                lobby.OnWelcome(client.LocalSlot, 2);
                hostLobby.OnRoster(host.PlayerCount);
                Assert.AreEqual(LobbyMode.Joined, lobby.Mode);
                Assert.AreEqual(1, lobby.LocalSlot);
                Assert.AreEqual("CREW 2/4", lobby.RosterLine);
                Assert.AreEqual("CREW 2/4", hostLobby.RosterLine);

                // the keypad reaches the same host
                var typed = new LobbyState();
                typed.EnterCode(MissionPhase.Hub, NetEndpoint.MakeAddress(192, 168, 1, 99));
                typed.PressDigit(0); typed.PressDigit(4); typed.PressDigit(2);
                Assert.IsTrue(typed.TryGetCodeTarget(NetProtocol.HostPort, out NetEndpoint byCode));
                Assert.AreEqual(entry.endpoint, byCode);

                // host leaves: the joiner's board says so
                host.Close(lan.Now);
                for (int i = 0; i < 50 && client.State == ClientState.Connected; i++)
                {
                    lan.Hub.Advance(0.01);
                    client.Tick(lan.Now);
                }
                Assert.AreEqual(SessionEndReason.HostClosed, client.EndReason);
                lobby.OnSessionEnded(client.EndReason, client.LastRefusal);
                Assert.AreEqual(LobbyMode.Offline, lobby.Mode);
                Assert.AreEqual("HOST LEFT", lobby.NoticeLine);
                client.Dispose();
                Assert.AreEqual(1, lan.Hub.LiveCount, "only the host's socket is still open");
            }
        }
    }
}

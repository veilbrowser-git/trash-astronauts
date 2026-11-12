using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Net.Online;
using Unity.Services.Core;
using UnityEngine;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Online play without a network: the cloud project link, error mapping, lobby entries, the RelayTransport over
    /// plain Unity Transport on loopback (endpoint mapping, caps, disconnects, full sessions), and the
    /// OnlineServices / OnlineLobby / OnlineLink state machines over a scripted fake of Unity Gaming Services.
    /// The fakes complete synchronously: the tests run with no synchronisation context so an awaited continuation
    /// runs inline when a held call is released.
    /// </summary>
    public class OnlineTests
    {
        SynchronizationContext m_Context;
        FakeCloud m_Cloud;
        FakeRelayConnector m_Relay;
        double m_Now;
        readonly List<IDisposable> m_Dispose = new List<IDisposable>();

        const string Version = "p1-c0ffee01";

        [SetUp]
        public void SetUp()
        {
            m_Context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            m_Cloud = new FakeCloud();
            m_Relay = new FakeRelayConnector();
            m_Now = 100.0;
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = m_Dispose.Count - 1; i >= 0; i--) m_Dispose[i].Dispose();
            m_Dispose.Clear();
            SynchronizationContext.SetSynchronizationContext(m_Context);
            Assert.AreEqual(0, RelayTransport.LiveCount, "every relay transport disposed");
            Assert.AreEqual(0, m_Relay.Hub.LiveCount, "every fake relay transport disposed");
        }

        T Track<T>(T disposable) where T : IDisposable
        {
            if (disposable != null) m_Dispose.Add(disposable);
            return disposable;
        }

        static T Done<T>(Task<T> task)
        {
            Assert.IsTrue(task.IsCompleted, "the fake completes synchronously");
            return task.Result;
        }

        OnlineServices Services(FakeOnlineBackend backend, bool internet = true, string projectId = OnlineConfig.CloudProjectId)
        {
            return new OnlineServices(() => backend, () => internet, () => projectId);
        }

        OnlineLink Link(FakeOnlineBackend backend) => new OnlineLink(Services(backend), m_Relay, () => m_Now);

        // ------------------------------------------------------------------ project link

        [Test]
        public void CloudProject_IsLinked_SoBatchBuildsCarryTheProjectId()
        {
            Assert.AreEqual("7b4c2047-35ae-40b4-8cd2-55b31c92d3ec", OnlineConfig.CloudProjectId);
            Assert.AreEqual(OnlineConfig.CloudProjectId, Application.cloudProjectId, "Application.cloudProjectId (what Services Core reads in a build)");
            // (UnityEditor.CloudProjectSettings.projectId stays empty in a batch-mode editor with no signed-in user;
            // that only affects the editor's Services windows, not builds or play mode.)
            Assert.AreEqual("production", OnlineConfig.Environment);
        }

        [Test]
        public void VersionKey_And_PhaseKeys()
        {
            Assert.AreEqual("p1-c0ffee01", OnlineConfig.VersionKey(1, 0xC0FFEE01u));
            Assert.AreNotEqual(OnlineConfig.VersionKey(1, 1u), OnlineConfig.VersionKey(1, 2u), "different content, different list");
            Assert.AreEqual("HUB", OnlineConfig.PhaseKey(MissionPhase.Hub));
            Assert.AreEqual("RESULTS", OnlineConfig.PhaseKey(MissionPhase.Results));
            Assert.AreEqual("ACTIVE", OnlineConfig.PhaseKey(MissionPhase.Active));
            Assert.AreEqual(NetProtocol.MaxPlayers - 1, OnlineConfig.RelayPeers);
        }

        // ------------------------------------------------------------------ errors

        [Test]
        public void Errors_MapServiceCodes_ToBoardText()
        {
            Assert.AreEqual(OnlineProblem.NoInternet, OnlineErrors.Classify(new RequestFailedException(1, "transport")));
            Assert.AreEqual(OnlineProblem.NoInternet, OnlineErrors.Classify(new RequestFailedException(16998, "lobby network")));
            Assert.AreEqual(OnlineProblem.NoInternet, OnlineErrors.Classify(new RequestFailedException(15998, "relay network")));
            Assert.AreEqual(OnlineProblem.ServiceBusy, OnlineErrors.Classify(new RequestFailedException(50, "too many")));
            Assert.AreEqual(OnlineProblem.ServiceBusy, OnlineErrors.Classify(new RequestFailedException(16429, "lobby rate limit")));
            Assert.AreEqual(OnlineProblem.ServiceBusy, OnlineErrors.Classify(new RequestFailedException(15503, "relay down")));
            Assert.AreEqual(OnlineProblem.GameGone, OnlineErrors.Classify(new RequestFailedException(15009, "join code")));
            Assert.AreEqual(OnlineProblem.GameGone, OnlineErrors.Classify(new RequestFailedException(16001, "lobby")));
            Assert.AreEqual(OnlineProblem.GameFull, OnlineErrors.Classify(new RequestFailedException(16004, "full")));
            Assert.AreEqual(OnlineProblem.NotSetUp, OnlineErrors.Classify(new RequestFailedException(15006, "inactive project")));
            Assert.AreEqual(OnlineProblem.NotSetUp, OnlineErrors.Classify(new ServicesInitializationException("not linked")));
            Assert.AreEqual(OnlineProblem.SignInFailed, OnlineErrors.Classify(new RequestFailedException(10009, "banned"), signingIn: true));
            Assert.AreEqual(OnlineProblem.SignInFailed, OnlineErrors.Classify(new RequestFailedException(53, "forbidden"), signingIn: true));
            Assert.AreEqual(OnlineProblem.NoAnswer, OnlineErrors.Classify(new TimeoutException()));
            Assert.AreEqual(OnlineProblem.NoAnswer, OnlineErrors.Classify(new AggregateException(new TaskCanceledException())));
            Assert.AreEqual(OnlineProblem.Failed, OnlineErrors.Classify(new InvalidOperationException("odd")));
            Assert.AreEqual(OnlineProblem.None, OnlineErrors.Classify(null));

            Assert.AreEqual("NO INTERNET", OnlineErrors.Text(OnlineProblem.NoInternet));
            Assert.AreEqual("SIGN-IN FAILED", OnlineErrors.Text(OnlineProblem.SignInFailed));
            Assert.AreEqual("SERVICE BUSY - TRY AGAIN", OnlineErrors.Text(OnlineProblem.ServiceBusy));
            Assert.AreEqual("ONLINE NOT SET UP", OnlineErrors.Text(OnlineProblem.NotSetUp));
            Assert.AreEqual("CREW FULL", OnlineErrors.Text(OnlineProblem.GameFull));
            foreach (OnlineProblem p in Enum.GetValues(typeof(OnlineProblem)))
            {
                string text = OnlineErrors.Text(p);
                Assert.IsNotNull(text);
                Assert.LessOrEqual(text.Length, 26, "fits a board line: " + p);
                Assert.AreEqual(text.ToUpperInvariant(), text);
            }
            StringAssert.Contains("16004", OnlineErrors.Describe(new RequestFailedException(16004, "full")));
        }

        // ------------------------------------------------------------------ lobby entries and codes

        [Test]
        public void OnlineGame_ParsesLobbyFields_DefensivelyAndOnlyForItsVersion()
        {
            var fields = new Dictionary<string, string> { ["ver"] = Version, ["phase"] = "RESULTS", ["crew"] = "2" };
            OnlineGame g = OnlineGame.FromLobby("L1", "TUG 4821", new DateTime(2026, 1, 1), 4, 1, k => fields.TryGetValue(k, out string v) ? v : null, Version);
            Assert.IsNotNull(g);
            Assert.AreEqual(4821, g.TugNumber);
            Assert.AreEqual(2, g.Players);
            Assert.AreEqual("TUG 4821  2/4  RESULTS", g.Line);
            Assert.IsTrue(g.Joinable);

            Assert.IsNull(OnlineGame.FromLobby("L1", "TUG 4821", DateTime.UtcNow, 4, 1, k => fields.TryGetValue(k, out string v) ? v : null, "p1-other"), "another build's game is not listed");
            Assert.IsNull(OnlineGame.FromLobby(null, "TUG 1", DateTime.UtcNow, 4, 1, k => null, null));

            var junk = new Dictionary<string, string> { ["ver"] = Version, ["phase"] = "<script>", ["crew"] = "99999999999" };
            OnlineGame j = OnlineGame.FromLobby("L2", "something else", DateTime.UtcNow, 64, 3, k => junk.TryGetValue(k, out string v) ? v : null, Version);
            Assert.IsNotNull(j);
            Assert.AreEqual(0, j.TugNumber);
            Assert.AreEqual("HUB", j.PhaseKey, "unknown phase text is not shown");
            Assert.AreEqual(NetProtocol.MaxPlayers, j.MaxPlayers, "never more than the game's 4");
            Assert.AreEqual(3, j.Players, "unparseable crew falls back to the member count");

            Assert.AreEqual(4821, OnlineGame.ParseTug("TUG 4821"));
            Assert.AreEqual(0, OnlineGame.ParseTug("TUG -1"));
            Assert.AreEqual(0, OnlineGame.ParseTug("TUG 99999"));
            Assert.AreEqual("TUG 7", OnlineGame.LobbyName(7));
        }

        [Test]
        public void OnlineGame_Arrange_NewestFirst_JoinableOnly_AtMostFive()
        {
            var games = new List<OnlineGame>();
            for (int i = 0; i < 8; i++)
                games.Add(new OnlineGame { LobbyId = "L" + i, TugNumber = (ushort)(1000 + i), Players = 1, MaxPlayers = 4, PhaseKey = "HUB", CreatedUtc = new DateTime(2026, 1, 1, 0, i, 0) });
            games[7].Players = 4;          // full
            games[6].PhaseKey = "ACTIVE";  // on a contract
            games.Add(null);
            List<OnlineGame> list = OnlineGame.Arrange(games, OnlineConfig.MaxListed);
            Assert.AreEqual(5, list.Count);
            Assert.AreEqual("L5", list[0].LobbyId, "newest joinable first");
            Assert.AreEqual("L1", list[4].LobbyId);
        }

        [Test]
        public void JoinCodes_Normalize()
        {
            Assert.AreEqual("AB12CD", JoinCodes.Normalize(" ab12cd "));
            Assert.IsNull(JoinCodes.Normalize("AB12C"));
            Assert.IsNull(JoinCodes.Normalize("AB-2CD"));
            Assert.IsNull(JoinCodes.Normalize(null));
        }

        // ------------------------------------------------------------------ RelayTransport on loopback

        static bool PumpUntil(Func<bool> done, int maxMilliseconds, params RelayTransport[] transports)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < maxMilliseconds)
            {
                for (int i = 0; i < transports.Length; i++) transports[i]?.Update();
                if (done()) return true;
                Thread.Sleep(1);
            }
            return done();
        }

        static bool ReceiveWithin(RelayTransport t, byte[] buffer, out NetEndpoint from, out int length, int maxMilliseconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < maxMilliseconds)
            {
                if (t.Receive(out from, buffer, out length)) return true;
                Thread.Sleep(1);
            }
            from = default(NetEndpoint);
            length = 0;
            return false;
        }

        [Test]
        public void RelayTransport_Loopback_MapsConnectionsToStableEndpoints_EnforcesTheCap_AndSurfacesDisconnects()
        {
            Assert.AreEqual(0, RelayTransport.LiveCount);
            RelayTransport host = Track(RelayTransport.OpenLoopbackHost(0, out string error));
            Assert.IsNotNull(host, error);
            Assert.IsTrue(host.IsHost);
            Assert.AreEqual(RelayTransport.HostEndpoint, host.Local);
            Assert.IsTrue(PumpUntil(() => host.IsReady, 2000, host), "bound and listening");
            Assert.AreNotEqual(0, host.BoundPort);

            RelayTransport a = Track(RelayTransport.OpenLoopbackClient(host.BoundPort, out error));
            Assert.IsNotNull(a, error);
            Assert.AreEqual(2, RelayTransport.LiveCount);
            Assert.IsTrue(PumpUntil(() => a.IsConnected && host.ConnectionCount == 1, 5000, host, a), "client connected");
            Assert.AreEqual(RelayTransport.ClientLocalEndpoint, a.Local);
            NetEndpoint aSeen = host.PeerAt(0);
            Assert.AreEqual(RelayTransport.PeerEndpoint(1), aSeen, "the first client is 10.82.1.1:1");
            Assert.IsFalse(aSeen.IsBroadcast);
            Assert.IsFalse(aSeen.IsNone);

            // the core's whole datagram fits; the driver's capacity is the real cap
            int capDown = host.MaxPayload(aSeen), capUp = a.MaxPayload(RelayTransport.HostEndpoint);
            Assert.GreaterOrEqual(capDown, NetProtocol.MaxDatagram, "host -> client capacity");
            Assert.GreaterOrEqual(capUp, NetProtocol.MaxDatagram, "client -> host capacity");
            Assert.AreEqual(-1, host.MaxPayload(RelayTransport.PeerEndpoint(77)));

            var big = new byte[NetProtocol.MaxDatagram];
            for (int i = 0; i < big.Length; i++) big[i] = (byte)(i * 7);
            var buffer = new byte[NetProtocol.MaxDatagram + 64];
            Assert.IsTrue(a.Send(RelayTransport.HostEndpoint, big, big.Length), "1200 bytes up");
            a.Flush();
            Assert.IsTrue(ReceiveWithin(host, buffer, out NetEndpoint from, out int length, 3000), "arrived at the host");
            Assert.AreEqual(aSeen, from, "the same stable endpoint");
            Assert.AreEqual(NetProtocol.MaxDatagram, length);
            for (int i = 0; i < length; i++) Assert.AreEqual((byte)(i * 7), buffer[i]);

            Assert.IsTrue(host.Send(aSeen, big, 300), "down");
            host.Flush();
            Assert.IsTrue(ReceiveWithin(a, buffer, out from, out length, 3000));
            Assert.AreEqual(RelayTransport.HostEndpoint, from, "a client only ever hears the host endpoint");
            Assert.AreEqual(300, length);

            // refusals: too long, unknown endpoints, bad arguments
            var huge = new byte[capUp + 1];
            Assert.IsFalse(a.Send(RelayTransport.HostEndpoint, huge, huge.Length), "longer than the capacity");
            Assert.AreEqual(1, a.Oversize);
            Assert.IsFalse(host.Send(RelayTransport.PeerEndpoint(9), big, 10), "no such client");
            Assert.IsFalse(a.Send(new NetEndpoint(10, 0, 0, 1, 47800), big, 10), "a client sends only to the host");
            Assert.IsFalse(a.Send(RelayTransport.HostEndpoint, null, 1));
            Assert.IsFalse(a.Send(RelayTransport.HostEndpoint, big, 0));
            Assert.IsFalse(a.Send(RelayTransport.HostEndpoint, new byte[2], 3));

            // a datagram longer than the receive buffer is dropped, not truncated, and the next one still arrives
            Assert.IsTrue(a.Send(RelayTransport.HostEndpoint, big, 500));
            Assert.IsTrue(a.Send(RelayTransport.HostEndpoint, big, 20));
            a.Flush();
            var small = new byte[100];
            Assert.IsTrue(ReceiveWithin(host, small, out from, out length, 3000));
            Assert.AreEqual(20, length);
            Assert.AreEqual(1, host.TooLong);

            // a second client gets the next serial
            RelayTransport b = Track(RelayTransport.OpenLoopbackClient(host.BoundPort, out error));
            Assert.IsNotNull(b, error);
            Assert.IsTrue(PumpUntil(() => b.IsConnected && host.ConnectionCount == 2, 5000, host, a, b));
            Assert.AreEqual(RelayTransport.PeerEndpoint(2), host.PeerAt(1));

            // client A leaves: the host hears the disconnect, forgets A, and B keeps its endpoint
            a.Dispose();
            a.Dispose();
            Assert.IsFalse(a.IsOpen);
            Assert.IsFalse(a.Send(RelayTransport.HostEndpoint, big, 10), "closed transport refuses quietly");
            Assert.IsFalse(a.Receive(out _, buffer, out _));
            Assert.IsTrue(PumpUntil(() => { host.Receive(out _, buffer, out _); return host.ConnectionCount == 1; }, 5000, b), "host saw A disconnect");
            Assert.AreEqual(1, host.Disconnects);
            Assert.AreEqual(RelayTransport.PeerEndpoint(2), host.PeerAt(0));
            Assert.IsFalse(host.Send(aSeen, big, 10), "A's endpoint is gone");
            Assert.IsTrue(host.Send(RelayTransport.PeerEndpoint(2), big, 10));

            // the host closes: B's link is lost
            host.Dispose();
            Assert.IsTrue(PumpUntil(() => { b.Receive(out _, buffer, out _); return b.LinkLost; }, 5000), "client notices the host closed");
            Assert.IsFalse(b.Send(RelayTransport.HostEndpoint, big, 10));
            b.Dispose();
            Assert.AreEqual(0, RelayTransport.LiveCount);
        }

        [Test]
        public void RelayTransport_Loopback_CarriesAFullSession_Handshake_200ReliableEachWay_AndAvatarsBothWays()
        {
            RelayTransport hostT = Track(RelayTransport.OpenLoopbackHost(0, out string error));
            Assert.IsNotNull(hostT, error);
            Assert.IsTrue(PumpUntil(() => hostT.IsReady, 2000, hostT));
            RelayTransport clientT = Track(RelayTransport.OpenLoopbackClient(hostT.BoundPort, out error));
            Assert.IsNotNull(clientT, error);
            Assert.IsTrue(PumpUntil(() => clientT.IsConnected, 5000, hostT, clientT));

            var run = new OnlineSessionRun(hostT, clientT, RelayTransport.HostEndpoint, 0x0B5E55EDu);
            m_Dispose.Add(run);
            m_Dispose.Remove(hostT);
            m_Dispose.Remove(clientT); // the sessions own and dispose them now
            double now = 10.0;
            run.Start(now);
            for (int i = 0; i < 30000 && !run.Done; i++)
            {
                now += 0.01;
                run.Step(now);
                hostT.Flush();
                clientT.Flush();
                if (i % 4 == 3) Thread.Sleep(1);
            }
            run.AssertComplete();
            Assert.AreEqual(0, hostT.Oversize + clientT.Oversize + hostT.SendErrors + clientT.SendErrors);
            run.Leave(now);
            run.Dispose();
            Assert.AreEqual(0, RelayTransport.LiveCount);
        }

        // ------------------------------------------------------------------ OnlineServices

        [Test]
        public void OnlineServices_DoesNothingUntilAsked()
        {
            int before = OnlineServices.StartRequests;
            bool created = false;
            var services = new OnlineServices(() => { created = true; return new FakeOnlineBackend(m_Cloud, "p"); }, () => true, () => OnlineConfig.CloudProjectId);
            var link = new OnlineLink(services, m_Relay, () => m_Now);
            link.Tick();
            link.UpdateHosting(2, MissionPhase.Hub);
            link.Leave();
            Assert.IsFalse(created, "no backend until StartAsync");
            Assert.IsNull(services.Backend);
            Assert.AreEqual(OnlineServiceState.Off, services.State);
            Assert.AreEqual("OFFLINE", services.StatusText);
            Assert.AreEqual(OnlineLinkState.Idle, link.State);
            Assert.AreEqual("", link.StatusText);
            Assert.AreEqual(before, OnlineServices.StartRequests);
            Assert.IsNotNull(new OnlineLink(), "the default link (OnlineServices.Shared) is free to construct too");
            Assert.AreEqual(before, OnlineServices.StartRequests);
        }

        [Test]
        public void OnlineServices_NoInternet_And_NotLinked_FailBeforeContactingAnything()
        {
            bool created = false;
            var offline = new OnlineServices(() => { created = true; return new FakeOnlineBackend(m_Cloud, "p"); }, () => false, () => OnlineConfig.CloudProjectId);
            Assert.IsFalse(Done(offline.StartAsync()));
            Assert.AreEqual(OnlineServiceState.Failed, offline.State);
            Assert.AreEqual(OnlineProblem.NoInternet, offline.Problem);
            Assert.AreEqual("NO INTERNET", offline.StatusText);

            var unlinked = new OnlineServices(() => { created = true; return new FakeOnlineBackend(m_Cloud, "p"); }, () => true, () => "");
            Assert.IsFalse(Done(unlinked.StartAsync()));
            Assert.AreEqual(OnlineProblem.NotSetUp, unlinked.Problem);
            Assert.IsFalse(created, "Unity Services were never touched");

            var throwing = new OnlineServices(() => throw new InvalidOperationException("boom"), () => true, () => OnlineConfig.CloudProjectId);
            Assert.DoesNotThrow(() => Done(throwing.StartAsync()));
            Assert.AreEqual(OnlineServiceState.Failed, throwing.State);
            Assert.AreEqual(OnlineProblem.SignInFailed, throwing.Problem);
        }

        [Test]
        public void OnlineServices_SignInFailures_AreClassified_AndARetryWorks()
        {
            var backend = new FakeOnlineBackend(m_Cloud, "p");
            OnlineServices services = Services(backend);
            backend.FailWith["SignIn"] = 1;
            Assert.IsFalse(Done(services.StartAsync()));
            Assert.AreEqual(OnlineProblem.NoInternet, services.Problem);
            StringAssert.Contains("scripted failure", services.LastError);

            backend.FailWith["SignIn"] = 10009;
            Assert.IsFalse(Done(services.StartAsync()));
            Assert.AreEqual("SIGN-IN FAILED", services.StatusText);

            backend.FailWith["SignIn"] = 50;
            Assert.IsFalse(Done(services.StartAsync()));
            Assert.AreEqual(OnlineProblem.ServiceBusy, services.Problem);

            backend.FailWith.Remove("SignIn");
            Assert.IsTrue(Done(services.StartAsync()));
            Assert.AreEqual(OnlineServiceState.Ready, services.State);
            Assert.AreEqual("ONLINE", services.StatusText);
            Assert.AreEqual("p", services.PlayerId);
            Assert.AreEqual(4, backend.Count("SignIn"));
        }

        [Test]
        public void OnlineServices_IsSafeToCallTwice_OneSignInForConcurrentCalls_NoneOnceReady()
        {
            var backend = new FakeOnlineBackend(m_Cloud, "p");
            OnlineServices services = Services(backend);
            var hold = new TaskCompletionSource<bool>();
            backend.Hold["SignIn"] = hold;
            int before = OnlineServices.StartRequests;
            Task<bool> first = services.StartAsync();
            Task<bool> second = services.StartAsync();
            Assert.AreSame(first, second, "a second call while signing in shares the first");
            Assert.IsFalse(first.IsCompleted, "nothing blocks: the call returns while sign-in is pending");
            Assert.AreEqual(OnlineServiceState.Starting, services.State);
            Assert.AreEqual("SIGNING IN...", services.StatusText);
            hold.SetResult(true);
            Assert.IsTrue(Done(first));
            Assert.IsTrue(Done(services.StartAsync()));
            Assert.AreEqual(1, backend.Count("SignIn"));
            Assert.AreEqual(before + 1, OnlineServices.StartRequests);
        }

        // ------------------------------------------------------------------ OnlineLink

        [Test]
        public void OnlineLink_HostFindJoin_EndToEnd_OnFakes_ThenASessionRunsOverTheTransports()
        {
            var hostBackend = new FakeOnlineBackend(m_Cloud, "host");
            var guestBackend = new FakeOnlineBackend(m_Cloud, "guest");
            OnlineLink host = Link(hostBackend);
            OnlineLink guest = Link(guestBackend);

            OnlineHostResult h = Done(host.HostAsync(4821, Version, MissionPhase.Hub));
            Assert.IsTrue(h.Ok, h.Text);
            Track(h.Transport);
            Assert.AreEqual(4821, h.TugNumber);
            Assert.IsNotNull(JoinCodes.Normalize(h.Code));
            Assert.IsTrue(h.Listed);
            Assert.AreEqual(OnlineLinkState.Hosting, host.State);
            Assert.AreEqual("TUG 4821  CODE " + h.Code, host.StatusText);
            Assert.AreEqual(1, m_Cloud.Lobbies.Count);
            FakeCloud.Lobby lobby = m_Cloud.Lobbies[0];
            Assert.AreEqual(Version, lobby.Info.versionKey);
            Assert.AreEqual("HUB", lobby.Info.phaseKey);
            Assert.AreEqual(1, lobby.Info.players);
            Assert.AreEqual(NetProtocol.MaxPlayers, lobby.Info.maxPlayers);
            Assert.AreEqual(h.Code, lobby.Code);

            OnlineFindResult f = Done(guest.FindAsync(Version));
            Assert.IsTrue(f.Ok, f.Text);
            Assert.AreEqual(1, f.Games.Count);
            Assert.AreEqual("TUG 4821  1/4  IN HUB", f.Games[0].Line);
            Assert.AreEqual("1 GAME", guest.StatusText);

            OnlineJoinResult j = Done(guest.JoinAsync(f.Games[0]));
            Assert.IsTrue(j.Ok, j.Text);
            Track(j.Transport);
            Assert.AreEqual(h.Transport.Local, j.Host);
            Assert.AreEqual(OnlineLinkState.Joined, guest.State);
            Assert.AreEqual(1, guestBackend.Count("JoinLobby"));
            Assert.AreEqual(1, guestBackend.Count("JoinRelay"));
            Assert.AreEqual(1, guestBackend.Count("SignIn"));

            // the transports are ordinary INetTransports: a session handshake runs over them
            var hostSession = new NetSessionHost(h.Transport, new ScriptedHostWorld(), new NetHostConfig { contentHash = 5, tugNumber = 4821 });
            var clientWorld = new BotClientWorld();
            var clientSession = new NetSessionClient(j.Transport, clientWorld, new NetClientConfig { contentHash = 5 });
            m_Dispose.Remove(h.Transport);
            m_Dispose.Remove(j.Transport);
            Track(hostSession);
            Track(clientSession);
            double now = 1.0;
            clientSession.Connect(j.Host, now);
            for (int i = 0; i < 300 && !clientSession.Welcomed; i++)
            {
                now += 0.01;
                m_Relay.Hub.Advance(0.01);
                hostSession.Poll(now);
                clientSession.Poll(now);
                hostSession.SendTick(now);
                clientSession.SendTick(now);
            }
            Assert.IsTrue(clientSession.Welcomed);

            host.UpdateHosting(2, MissionPhase.Hub);
            m_Now += OnlineConfig.LobbyUpdateSeconds;
            host.Tick();
            Assert.AreEqual(2, lobby.Info.players, "the crew count reaches the lobby");

            host.Leave();
            Assert.IsTrue(lobby.Deleted, "removed on close");
            Assert.AreEqual(OnlineLinkState.Idle, host.State);
            Assert.AreEqual("", host.StatusText);
            m_Now += 3.0;
            Assert.AreEqual(0, Done(guest.FindAsync(Version)).Games.Count, "a closed game is not listed");
        }

        [Test]
        public void OnlineLink_Find_RefreshesAtMostEveryTwoSeconds_AndShowsOnlyOpenGamesOfThisVersion_NewestFirst()
        {
            for (int i = 0; i < 9; i++)
            {
                m_Cloud.Lobbies.Add(new FakeCloud.Lobby
                {
                    Id = "L" + i,
                    Info = new OnlineGameInfo { tugNumber = (ushort)(2000 + i), versionKey = i == 0 ? "p1-old" : Version, players = i == 1 ? 4 : 1, maxPlayers = 4, phaseKey = i == 2 ? "ACTIVE" : "HUB" },
                    Code = "AAAAA" + i,
                    Created = m_Cloud.Clock.AddMinutes(i),
                });
            }
            var backend = new FakeOnlineBackend(m_Cloud, "guest");
            OnlineLink link = Link(backend);
            OnlineFindResult f = Done(link.FindAsync(Version));
            Assert.IsTrue(f.Ok);
            Assert.IsFalse(f.FromCache);
            Assert.AreEqual(OnlineConfig.MaxListed, f.Games.Count);
            Assert.AreEqual(2008, f.Games[0].TugNumber, "newest first");
            foreach (OnlineGame g in f.Games)
            {
                Assert.AreNotEqual(2000, g.TugNumber, "other version");
                Assert.AreNotEqual(2001, g.TugNumber, "full");
                Assert.AreNotEqual(2002, g.TugNumber, "on a contract");
            }
            Assert.AreEqual(1, backend.Count("Query"));

            m_Now += 1.9;
            OnlineFindResult again = Done(link.FindAsync(Version));
            Assert.IsTrue(again.FromCache, "within 2 s the previous answer comes back");
            Assert.AreEqual(5, again.Games.Count);
            Assert.AreEqual(1, backend.Count("Query"), "the service was not asked");

            m_Now += 0.2;
            Assert.IsFalse(Done(link.FindAsync(Version)).FromCache);
            Assert.AreEqual(2, backend.Count("Query"));

            m_Now += 2.5;
            backend.FailWith["Query"] = 16429;
            OnlineFindResult limited = Done(link.FindAsync(Version));
            Assert.IsFalse(limited.Ok);
            Assert.AreEqual(OnlineProblem.ServiceBusy, limited.Problem);
            Assert.AreEqual(5, limited.Games.Count, "the last good list stays up");
            Assert.AreEqual("SERVICE BUSY - TRY AGAIN", link.StatusText);
        }

        [Test]
        public void OnlineLink_Hosting_HeartbeatsEvery15s_CoalescesCrewUpdates_AndStopsWhenTheLobbyIsGone()
        {
            var backend = new FakeOnlineBackend(m_Cloud, "host");
            OnlineLink link = Link(backend);
            OnlineHostResult h = Done(link.HostAsync(77, Version, MissionPhase.Results));
            Track(h.Transport);
            FakeCloud.Lobby lobby = m_Cloud.Lobbies[0];
            Assert.AreEqual("RESULTS", lobby.Info.phaseKey);

            m_Now += 14.9;
            link.Tick();
            Assert.AreEqual(0, lobby.Heartbeats);
            m_Now += 0.1;
            link.Tick();
            link.Tick();
            Assert.AreEqual(1, lobby.Heartbeats, "one heartbeat at 15 s");

            link.UpdateHosting(1, MissionPhase.Results);
            m_Now += 5.0;
            link.Tick();
            Assert.AreEqual(0, backend.Count("UpdateLobby"), "nothing changed, nothing sent");

            link.UpdateHosting(2, MissionPhase.Hub);
            link.UpdateHosting(3, MissionPhase.Hub);
            link.Tick();
            Assert.AreEqual(1, backend.Count("UpdateLobby"), "two changes, one call");
            Assert.AreEqual(3, lobby.Info.players);
            link.UpdateHosting(2, MissionPhase.Hub);
            m_Now += 1.0;
            link.Tick();
            Assert.AreEqual(1, backend.Count("UpdateLobby"), "at most every 2 s");
            m_Now += 1.0;
            link.Tick();
            Assert.AreEqual(2, backend.Count("UpdateLobby"));
            Assert.AreEqual(2, lobby.Info.players);

            lobby.Deleted = true; // expired on the service side
            m_Now += 15.0;
            link.Tick();
            Assert.IsFalse(link.Lobby.IsListed, "a lobby that is gone is no longer heartbeated");
            Assert.AreEqual(OnlineLinkState.Hosting, link.State, "the game itself goes on");
            StringAssert.Contains("NOT LISTED", link.StatusText);
            link.Leave();
        }

        [Test]
        public void OnlineLink_ErrorPaths_GiveBoardText_AndReleaseWhatTheyOpened()
        {
            var backend = new FakeOnlineBackend(m_Cloud, "p");
            OnlineLink link = Link(backend);

            OnlineJoinResult bad = Done(link.JoinByCodeAsync("12"));
            Assert.AreEqual(OnlineProblem.BadCode, bad.Problem);
            Assert.AreEqual(0, backend.Count("SignIn"), "a bad code never contacts anything");

            backend.FailWith["CreateRelay"] = 15429;
            OnlineHostResult busy = Done(link.HostAsync(1, Version, MissionPhase.Hub));
            Assert.IsFalse(busy.Ok);
            Assert.IsNull(busy.Transport);
            Assert.AreEqual(OnlineProblem.ServiceBusy, busy.Problem);
            Assert.AreEqual(OnlineLinkState.Failed, link.State);
            Assert.AreEqual("SERVICE BUSY - TRY AGAIN", link.StatusText);
            backend.FailWith.Remove("CreateRelay");

            m_Relay.FailHost = OnlineProblem.NoAnswer;
            OnlineHostResult noRelay = Done(link.HostAsync(1, Version, MissionPhase.Hub));
            Assert.AreEqual(OnlineProblem.NoAnswer, noRelay.Problem);
            Assert.AreEqual(0, m_Cloud.Lobbies.Count, "nothing listed when Relay did not come up");
            m_Relay.FailHost = OnlineProblem.None;

            OnlineJoinResult gone = Done(link.JoinByCodeAsync("ZZZZZZ"));
            Assert.AreEqual(OnlineProblem.GameGone, gone.Problem);
            Assert.AreEqual("GAME GONE", link.StatusText);

            // listing fails but the game hosts anyway: the code still works
            backend.FailWith["CreateLobby"] = 16503;
            OnlineHostResult unlisted = Done(link.HostAsync(5, Version, MissionPhase.Hub));
            Assert.IsTrue(unlisted.Ok);
            Track(unlisted.Transport);
            Assert.IsFalse(unlisted.Listed);
            StringAssert.Contains("NOT LISTED", link.StatusText);
            backend.FailWith.Remove("CreateLobby");
            link.Leave();

            // a full game
            var other = new FakeOnlineBackend(m_Cloud, "host2");
            OnlineHostResult full = Done(Link(other).HostAsync(6, Version, MissionPhase.Hub));
            Track(full.Transport);
            FakeCloud.Lobby lobby = m_Cloud.Lobbies[m_Cloud.Lobbies.Count - 1];
            var game = new OnlineGame { LobbyId = lobby.Id, TugNumber = 6, Players = 3, MaxPlayers = 4, PhaseKey = "HUB" };
            lobby.Info.players = 4;
            OnlineJoinResult crewFull = Done(link.JoinAsync(game));
            Assert.AreEqual(OnlineProblem.GameFull, crewFull.Problem);
            Assert.AreEqual("CREW FULL", link.StatusText);
            Assert.AreEqual(0, m_Relay.Clients, "no relay connection was opened");

            // the relay connection does not come up
            lobby.Info.players = 1;
            m_Relay.FailClient = OnlineProblem.NoAnswer;
            OnlineJoinResult noAnswer = Done(link.JoinAsync(game));
            Assert.AreEqual(OnlineProblem.NoAnswer, noAnswer.Problem);
            Assert.AreEqual("NO ANSWER", link.StatusText);

            Assert.AreEqual(OnlineProblem.GameGone, Done(link.JoinAsync(null)).Problem);
        }

        [Test]
        public void OnlineLink_LeaveWhileHosting_CancelsTheStep_ClosesItsTransport_AndDeletesTheLateLobby()
        {
            var backend = new FakeOnlineBackend(m_Cloud, "host");
            OnlineLink link = Link(backend);
            var hold = new TaskCompletionSource<bool>();
            backend.Hold["CreateLobby"] = hold;

            Task<OnlineHostResult> hosting = link.HostAsync(9, Version, MissionPhase.Hub);
            Assert.IsFalse(hosting.IsCompleted);
            Assert.AreEqual(OnlineLinkState.Opening, link.State);
            Assert.AreEqual("OPENING...", link.StatusText);
            Assert.IsTrue(link.IsBusy);
            OnlineHostResult second = Done(link.HostAsync(10, Version, MissionPhase.Hub));
            Assert.AreEqual(OnlineProblem.Busy, second.Problem, "one step at a time");
            Assert.AreEqual(OnlineProblem.Busy, Done(link.FindAsync(Version)).Problem);
            Assert.AreEqual(1, m_Relay.Hub.LiveCount, "the relay transport is open while the lobby is created");

            link.Leave();
            Assert.AreEqual(OnlineLinkState.Idle, link.State);
            hold.SetResult(true);
            OnlineHostResult result = Done(hosting);
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(OnlineProblem.Cancelled, result.Problem);
            Assert.IsNull(result.Transport);
            Assert.AreEqual(0, m_Relay.Hub.LiveCount, "the transport it opened was closed");
            Assert.AreEqual(1, m_Cloud.Lobbies.Count);
            Assert.IsTrue(m_Cloud.Lobbies[0].Deleted, "the lobby that appeared after LEAVE was deleted");
            Assert.AreEqual(OnlineLinkState.Idle, link.State, "a cancelled step does not overwrite the state");
            Assert.IsFalse(link.IsBusy);
        }
    }

    /// <summary>
    /// A host and one client session over two transports: handshake, then 200 reliable messages each way (in order,
    /// once) and avatar snapshots both ways. Shared shape with the PlayMode online test.
    /// </summary>
    internal sealed class OnlineSessionRun : IDisposable
    {
        public const int Reliable = 200;
        public const int Avatars = 20;

        public readonly NetSessionHost Host;
        public readonly NetSessionClient Client;
        public readonly ScriptedHostWorld HostWorld = new ScriptedHostWorld();
        public readonly BotClientWorld ClientWorld = new BotClientWorld();
        public readonly List<int> Down = new List<int>();
        public readonly List<int> Up = new List<int>();
        public int HostAvatarsFromClient;
        readonly NetEndpoint m_HostEndpoint;
        int m_SentDown, m_SentUp;

        public OnlineSessionRun(INetTransport hostTransport, INetTransport clientTransport, NetEndpoint hostEndpoint, uint contentHash)
        {
            m_HostEndpoint = hostEndpoint;
            Host = new NetSessionHost(hostTransport, HostWorld, new NetHostConfig { contentHash = contentHash, tugNumber = 4821 });
            HostWorld.Session = Host;
            Client = new NetSessionClient(clientTransport, ClientWorld, new NetClientConfig { contentHash = contentHash });
            ClientWorld.Session = Client;
            Client.Tap = (outbound, peer, type, buffer, offset, length, time) =>
            {
                if (outbound || type != NetMsg.ItemValue) return;
                var r = new NetReader(buffer, offset, length);
                ItemValueMsg.Read(ref r, out ItemValueMsg m);
                Down.Add(m.value);
            };
            Host.Tap = (outbound, peer, type, buffer, offset, length, time) =>
            {
                if (outbound) return;
                if (type == NetMsg.AvatarState) { HostAvatarsFromClient++; return; }
                if (type != NetMsg.ItemDamageReport) return;
                var r = new NetReader(buffer, offset, length);
                ItemDamageReportMsg.Read(ref r, out ItemDamageReportMsg m);
                Up.Add(m.newValue);
            };
        }

        public void Start(double now) => Client.Connect(m_HostEndpoint, now);

        public bool Done => Down.Count == Reliable && Up.Count == Reliable && Host.Backlog(1) == 0 && Client.Backlog == 0
                            && HostAvatarsFromClient >= Avatars && ClientWorld.AvatarCount[0] >= Avatars;

        public void Step(double now)
        {
            Host.Poll(now);
            Client.Poll(now);
            if (Client.Welcomed)
            {
                for (int n = 0; n < 4 && m_SentDown < Reliable && Host.Backlog(1) < 400; n++) { Host.SendItemValue(1, m_SentDown); m_SentDown++; }
                for (int n = 0; n < 4 && m_SentUp < Reliable && Client.Backlog < 400; n++) { Client.SendItemDamageReport(1, m_SentUp); m_SentUp++; }
                var hostAvatar = AvatarSnapshot.Default;
                hostAvatar.headPosition = new NetVec3(0f, 1.6f, (float)(now % 10.0));
                Host.SetLocalAvatar(in hostAvatar);
                var clientAvatar = AvatarSnapshot.Default;
                clientAvatar.headPosition = new NetVec3(1f, 1.7f, (float)(now % 10.0));
                Client.SetLocalAvatar(in clientAvatar);
            }
            Host.SendTick(now);
            Client.SendTick(now);
        }

        public void AssertComplete()
        {
            Assert.IsTrue(Client.Welcomed, "Hello / Accept / Welcome completed");
            Assert.AreEqual(1, Client.LocalSlot);
            Assert.IsTrue(HostWorld.Joined[1]);
            Assert.AreEqual(Reliable, Down.Count, "host -> client reliable messages");
            for (int i = 0; i < Reliable; i++) Assert.AreEqual(i, Down[i], "in order, exactly once");
            Assert.AreEqual(Reliable, Up.Count, "client -> host reliable messages");
            for (int i = 0; i < Reliable; i++) Assert.AreEqual(i, Up[i]);
            Assert.GreaterOrEqual(ClientWorld.AvatarCount[0], Avatars, "host avatar snapshots reached the client");
            Assert.GreaterOrEqual(HostAvatarsFromClient, Avatars, "client avatar snapshots reached the host");
            Assert.IsTrue(Host.TryGetAvatar(1, out AvatarSnapshot seen));
            Assert.AreEqual(1f, seen.headPosition.x, 0.01f);
            Assert.LessOrEqual(Host.Stats.largestDatagram, NetProtocol.MaxDatagram);
            Assert.LessOrEqual(Client.Stats.largestDatagram, NetProtocol.MaxDatagram);
        }

        public void Leave(double now)
        {
            Client.Leave(now);
            Host.Close(now);
        }

        public void Dispose()
        {
            Client.Dispose();
            Host.Dispose();
        }
    }
}

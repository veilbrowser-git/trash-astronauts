using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net.Online;
using TrashAstronauts.World;
using Unity.Services.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Online play in play mode. First (Order 1) the offline guard: the real game scene runs and nothing in it starts
    /// Unity Services or opens a Relay transport. Then (Order 2, category "Online") one real end-to-end run against
    /// Unity's servers from this machine: two anonymous players in one process (separate services registries and
    /// Authentication profiles) host and join through Relay and Lobby, and a NetSessionHost / NetSessionClient pair
    /// does its handshake, 200 reliable messages each way and avatar snapshots both ways over the two
    /// RelayTransports. With no internet, or when a service refuses, it ends Inconclusive with the reason.
    /// </summary>
    public class OnlinePlayTests
    {
        OnlineLink m_HostLink, m_GuestLink;
        INetTransport m_HostTransport, m_GuestTransport;
        PlayOnlineSessionRun m_Run;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            MissionDirector.StorageOverride = null;
            MissionDirector.SeedOverride = null;
            MissionDirector.CreatureOverride = null;
            MissionDirector.MapOverride = null;
            MissionDirector.ConditionOverride = null;
            BlackoutController.DamageOverride = null;

            if (m_Run != null)
            {
                m_Run.Leave(Time.realtimeSinceStartupAsDouble);
                for (int i = 0; i < 5; i++) { m_Run.Flush(); yield return null; }
                m_Run.Dispose(); // disposes both transports
                m_Run = null;
                m_HostTransport = m_GuestTransport = null;
            }
            m_HostTransport?.Dispose();
            m_GuestTransport?.Dispose();
            m_HostTransport = m_GuestTransport = null;
            m_GuestLink?.Leave();
            m_HostLink?.Leave(); // deletes the lobby in the background
            if (m_HostLink != null)
            {
                float end = Time.realtimeSinceStartup + 5f;
                while (m_HostLink.Lobby.IsListed && Time.realtimeSinceStartup < end) yield return null;
                for (int i = 0; i < 60; i++) yield return null; // let the delete call go out
            }
            m_HostLink = m_GuestLink = null;
            Assert.AreEqual(0, RelayTransport.LiveCount, "every relay transport disposed");
        }

        // ------------------------------------------------------------------ offline guard

        [UnityTest, Order(1)]
        public IEnumerator OfflineGuard_TheGameNeverStartsUnityServices_UntilAPlayerAsks()
        {
            MissionDirector.StorageOverride = new MemorySaveStorage();
            MissionDirector.SeedOverride = 44;
            MissionDirector.CreatureOverride = 0;
            MissionDirector.MapOverride = 0;
            MissionDirector.ConditionOverride = ContractCondition.Standard;
            BlackoutController.DamageOverride = 0f;
            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            for (int i = 0; i < 60; i++) yield return null;
            Assert.IsNotNull(MissionDirector.Active, "the game scene is running");

            Assert.AreEqual(0, OnlineServices.StartRequests, "nothing asked to go online");
            Assert.AreEqual(ServicesInitializationState.Uninitialized, UnityServices.State, "Unity Services were not initialised");
            foreach (KeyValuePair<string, IUnityServices> s in UnityServices.Services)
                Assert.AreEqual(ServicesInitializationState.Uninitialized, s.Value.State, "services registry " + s.Key);
            Assert.AreEqual(0, RelayTransport.LiveCount, "no Relay driver");
            Assert.AreEqual(Application.cloudProjectId, OnlineConfig.CloudProjectId, "the build is linked even so");
        }

        // ------------------------------------------------------------------ the real thing

        static IEnumerator Wait(Task task, float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (!task.IsCompleted && Time.realtimeSinceStartup < end) yield return null;
        }

        static bool ServiceSide(OnlineProblem p) =>
            p == OnlineProblem.NoInternet || p == OnlineProblem.ServiceBusy || p == OnlineProblem.NotSetUp ||
            p == OnlineProblem.SignInFailed || p == OnlineProblem.NoAnswer || p == OnlineProblem.Failed;

        static string Why(string step, OnlineProblem problem, string text, OnlineServices services)
        {
            return step + ": " + problem + " (" + text + ")" + (services != null && services.LastError != null ? " - " + services.LastError : "");
        }

        [UnityTest, Order(2), Category("Online"), Timeout(240000)]
        public IEnumerator RealServices_HostListJoin_ThroughRelayAndLobby_SessionHandshake_200ReliableEachWay_AndAvatars()
        {
            LogAssert.ignoreFailingMessages = true; // the service SDKs log their own errors when something is refused
            if (Application.internetReachability == NetworkReachability.NotReachable)
                Assert.Inconclusive("No internet on this machine (Application.internetReachability = NotReachable).");

            string stamp = Guid.NewGuid().ToString("N").Substring(0, 8);
            string version = "test-" + stamp; // a version nobody else lists: we only ever see our own game
            ushort tug = JoinCode.MakeTugNumber(Environment.TickCount);
            var hostServices = new OnlineServices(() => new UgsBackend("ta-test-host"));
            var guestServices = OnlineServices.CreateSeparate("ta-test-guest");
            m_HostLink = new OnlineLink(hostServices);
            m_GuestLink = new OnlineLink(guestServices);
            var clock = System.Diagnostics.Stopwatch.StartNew();

            // host: sign in, Relay allocation + binding, lobby
            Task<OnlineHostResult> hosting = m_HostLink.HostAsync(tug, version, MissionPhase.Hub);
            yield return Wait(hosting, 90f);
            if (!hosting.IsCompleted) Assert.Inconclusive("Hosting did not finish in 90 s (state " + m_HostLink.State + ", " + hostServices.StatusText + ").");
            OnlineHostResult h = hosting.Result;
            if (h.Ok) m_HostTransport = h.Transport;
            if (!h.Ok)
            {
                string why = Why("host", h.Problem, h.Text, hostServices);
                if (ServiceSide(h.Problem)) Assert.Inconclusive("The service refused or could not be reached: " + why);
                Assert.Fail(why);
            }
            Debug.Log("[OnlineTest] hosted TUG " + h.TugNumber + " code " + h.Code + " listed " + h.Listed + " player " + hostServices.PlayerId + " after " + clock.ElapsedMilliseconds + " ms");
            Assert.IsTrue(h.Listed, "the lobby was created");
            Assert.IsInstanceOf<RelayTransport>(h.Transport);
            var hostT = (RelayTransport)h.Transport;
            Assert.IsTrue(hostT.IsReady, "Relay confirmed the host binding");

            // guest: sign in as a second anonymous player and find the game in the lobby list
            OnlineGame found = null;
            OnlineFindResult f = default(OnlineFindResult);
            float findUntil = Time.realtimeSinceStartup + 30f;
            while (found == null && Time.realtimeSinceStartup < findUntil)
            {
                Task<OnlineFindResult> finding = m_GuestLink.FindAsync(version);
                yield return Wait(finding, 60f);
                if (!finding.IsCompleted) Assert.Inconclusive("FIND did not answer in 60 s.");
                f = finding.Result;
                if (!f.Ok && ServiceSide(f.Problem) && f.Problem != OnlineProblem.ServiceBusy)
                    Assert.Inconclusive("The service refused or could not be reached: " + Why("find", f.Problem, f.Text, guestServices));
                if (f.Games != null)
                    foreach (OnlineGame g in f.Games)
                        if (g.TugNumber == tug) found = g;
                hostT.Update(); // keep the Relay binding alive while we wait
                if (found == null)
                {
                    float next = Time.realtimeSinceStartup + 2.1f;
                    while (Time.realtimeSinceStartup < next) { hostT.Update(); m_HostLink.Tick(); yield return null; }
                }
            }
            Assert.IsNotNull(found, "the hosted game appears in the lobby list (last answer: " + f.Text + ", " + (f.Games?.Count ?? 0) + " games)");
            Assert.AreNotEqual(hostServices.PlayerId, guestServices.PlayerId, "two distinct anonymous players");
            Assert.AreEqual("TUG " + tug + "  1/4  IN HUB", found.Line);
            Debug.Log("[OnlineTest] guest " + guestServices.PlayerId + " found '" + found.Line + "' after " + clock.ElapsedMilliseconds + " ms");

            // guest: join through the lobby (reads the member-only code) and Relay
            Task<OnlineJoinResult> joining = m_GuestLink.JoinAsync(found);
            while (!joining.IsCompleted && clock.Elapsed.TotalSeconds < 200) { hostT.Update(); m_HostLink.Tick(); yield return null; }
            if (!joining.IsCompleted) Assert.Inconclusive("JOIN did not finish.");
            OnlineJoinResult j = joining.Result;
            if (j.Ok) m_GuestTransport = j.Transport;
            if (!j.Ok)
            {
                string why = Why("join", j.Problem, j.Text, guestServices);
                if (ServiceSide(j.Problem)) Assert.Inconclusive("The service refused or could not be reached: " + why);
                Assert.Fail(why);
            }
            Assert.AreEqual(RelayTransport.HostEndpoint, j.Host);
            Assert.AreEqual(h.Code, m_GuestLink.Code, "the code read from the lobby is the host's Relay join code");
            var guestT = (RelayTransport)j.Transport;
            Assert.AreEqual(2, RelayTransport.LiveCount);
            Debug.Log("[OnlineTest] joined through Relay after " + clock.ElapsedMilliseconds + " ms");

            // the real datagram cap over Relay + DTLS, both ways, and one full 1200-byte datagram up
            var big = new byte[NetProtocol.MaxDatagram];
            for (int i = 0; i < big.Length; i++) big[i] = (byte)(i * 13);
            var buffer = new byte[NetProtocol.MaxDatagram + 64];
            bool sent = false, got = false;
            NetEndpoint guestSeen = default(NetEndpoint);
            float capUntil = Time.realtimeSinceStartup + 15f;
            while (!got && Time.realtimeSinceStartup < capUntil)
            {
                if (!sent) { sent = guestT.Send(RelayTransport.HostEndpoint, big, big.Length); guestT.Flush(); }
                while (hostT.Receive(out NetEndpoint from, buffer, out int length))
                {
                    if (length != big.Length) continue;
                    got = true;
                    guestSeen = from;
                    for (int i = 0; i < length; i++) Assert.AreEqual((byte)(i * 13), buffer[i], "byte " + i);
                }
                guestT.Update();
                yield return null;
            }
            Assert.IsTrue(sent, "a 1200-byte datagram was accepted for sending");
            Assert.IsTrue(got, "and arrived through Relay intact");
            Assert.AreEqual(RelayTransport.PeerEndpoint(1), guestSeen);
            int capUp = guestT.MaxPayload(RelayTransport.HostEndpoint);
            int capDown = hostT.MaxPayload(guestSeen);
            Debug.Log("[OnlineTest] payload capacity over Relay+DTLS: up " + capUp + " bytes, down " + capDown + " bytes");
            Assert.GreaterOrEqual(capUp, NetProtocol.MaxDatagram);
            Assert.GreaterOrEqual(capDown, NetProtocol.MaxDatagram);

            // the game's sessions, unchanged, over the two Relay transports
            m_Run = new PlayOnlineSessionRun(h.Transport, j.Transport, j.Host, 0x0E1E0E1Eu);
            m_HostTransport = m_GuestTransport = null; // owned by the sessions now
            double startedAt = Time.realtimeSinceStartupAsDouble;
            m_Run.Start(startedAt);
            while (!m_Run.Done && Time.realtimeSinceStartupAsDouble - startedAt < 60.0)
            {
                double now = Time.realtimeSinceStartupAsDouble;
                m_Run.Step(now);
                m_Run.Flush();
                m_HostLink.Tick();
                if (m_Run.Client.Welcomed) m_HostLink.UpdateHosting(m_Run.Host.PlayerCount, MissionPhase.Hub);
                yield return null;
            }
            Debug.Log("[OnlineTest] session: welcomed " + m_Run.Client.Welcomed + ", reliable down " + m_Run.Down.Count + " up " + m_Run.Up.Count +
                      ", avatars at host " + m_Run.HostAvatarsFromClient + " at client " + m_Run.ClientWorld.AvatarCount[0] +
                      ", in " + (Time.realtimeSinceStartupAsDouble - startedAt).ToString("0.0") + " s; host datagrams " + m_Run.Host.Stats.datagramsSent + "/" + m_Run.Host.Stats.datagramsReceived +
                      ", largest " + m_Run.Host.Stats.largestDatagram + "; relay drops host " + hostT.QueueOverflow + "/" + hostT.Oversize + " guest " + guestT.QueueOverflow + "/" + guestT.Oversize);
            m_Run.AssertComplete();
            Assert.AreEqual(0, hostT.Oversize + guestT.Oversize, "nothing over the cap");
            Assert.AreEqual(0, hostT.SendErrors + guestT.SendErrors, "no send errors");

            // the guest leaves: the host's transport hears the Relay disconnect
            m_Run.Client.Leave(Time.realtimeSinceStartupAsDouble);
            m_Run.Client.Dispose();
            float leaveUntil = Time.realtimeSinceStartup + 20f;
            while (hostT.ConnectionCount > 0 && Time.realtimeSinceStartup < leaveUntil)
            {
                m_Run.Host.Poll(Time.realtimeSinceStartupAsDouble);
                yield return null;
            }
            Assert.AreEqual(0, hostT.ConnectionCount, "the host saw the guest's Relay connection close");
            Debug.Log("[OnlineTest] complete after " + clock.ElapsedMilliseconds + " ms");
        }
    }

    /// <summary>The PlayMode twin of the EditMode OnlineSessionRun (test assemblies cannot share it).</summary>
    internal sealed class PlayOnlineSessionRun : IDisposable
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
        readonly RelayTransport m_HostRelay, m_ClientRelay;
        int m_SentDown, m_SentUp;

        public PlayOnlineSessionRun(INetTransport hostTransport, INetTransport clientTransport, NetEndpoint hostEndpoint, uint contentHash)
        {
            m_HostEndpoint = hostEndpoint;
            m_HostRelay = hostTransport as RelayTransport;
            m_ClientRelay = clientTransport as RelayTransport;
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

        public void Flush()
        {
            m_HostRelay?.Flush();
            m_ClientRelay?.Flush();
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

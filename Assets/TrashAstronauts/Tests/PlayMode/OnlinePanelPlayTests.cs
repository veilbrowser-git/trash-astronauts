using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Net.Online;
using TrashAstronauts.UI;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// The online half of the CO-OP CREW panel, driven through a FAKE online link (NetSession.OnlineLinkFactory):
    /// HOST ONLINE, FIND ONLINE, JOIN and LEAVE, every problem's words, a different build refused like on Wi-Fi,
    /// and a request still in flight when the scene goes away. The link hands back in-memory transports, so the
    /// same host and client sessions run over them as over Relay. No Unity service is contacted.
    /// </summary>
    public class OnlinePanelPlayTests : NetPlayFixture
    {
        NetLobbyBoard m_Board;
        MissionBoard m_Mission;
        FakeLink m_Link;
        readonly Dictionary<PushButton, float> m_LastPress = new Dictionary<PushButton, float>();

        /// <summary>An online link whose every step finishes when the test says so.</summary>
        sealed class FakeLink : IOnlineLink
        {
            public TaskCompletionSource<OnlineHostResult> Host;
            public TaskCompletionSource<OnlineFindResult> Find;
            public TaskCompletionSource<OnlineJoinResult> Join;
            public int HostCalls, FindCalls, JoinCalls, CodeCalls, LeaveCalls, TickCalls, UpdateCalls;
            public int LastPlayers;
            public MissionPhase LastPhase;
            public string LastVersionKey, LastCode;
            public OnlineGame LastGame;

            public OnlineLinkState State { get; set; }
            public OnlineProblem Problem { get; set; }
            public string StatusText => "FAKE " + State;
            public ushort TugNumber { get; set; }
            public string Code { get; set; }
            public bool IsListed { get; set; } = true;

            public Task<OnlineHostResult> HostAsync(ushort tugNumber, string versionKey, MissionPhase phase, int playerCount = 1)
            {
                HostCalls++;
                LastVersionKey = versionKey;
                State = OnlineLinkState.Opening;
                Host = new TaskCompletionSource<OnlineHostResult>();
                return Host.Task;
            }

            public Task<OnlineFindResult> FindAsync(string versionKey)
            {
                FindCalls++;
                LastVersionKey = versionKey;
                State = OnlineLinkState.Searching;
                Find = new TaskCompletionSource<OnlineFindResult>();
                return Find.Task;
            }

            public Task<OnlineJoinResult> JoinAsync(OnlineGame game)
            {
                JoinCalls++;
                LastGame = game;
                State = OnlineLinkState.Joining;
                Join = new TaskCompletionSource<OnlineJoinResult>();
                return Join.Task;
            }

            public Task<OnlineJoinResult> JoinByCodeAsync(string code)
            {
                CodeCalls++;
                LastCode = code;
                State = OnlineLinkState.Joining;
                Join = new TaskCompletionSource<OnlineJoinResult>();
                return Join.Task;
            }

            public void UpdateHosting(int playerCount, MissionPhase phase)
            {
                UpdateCalls++;
                LastPlayers = playerCount;
                LastPhase = phase;
            }

            public void Tick() => TickCalls++;

            public void Leave()
            {
                LeaveCalls++;
                State = OnlineLinkState.Idle;
            }
        }

        /// <summary>
        /// A Relay connection stand-in: an in-memory transport whose Relay health the test sets (the real
        /// RelayTransport needs Unity's servers).
        /// </summary>
        sealed class FakeRelay : INetTransport, IRelayLink
        {
            public readonly MemoryTransport Inner;
            public FakeRelay(MemoryTransport inner, bool host) { Inner = inner; IsHost = host; ConnectionCount = host ? 0 : 1; }
            public bool IsHost { get; }
            public bool LinkLost { get; set; }
            public bool AllocationInvalid { get; set; }
            public bool RelayLost { get; set; }
            public int Disconnects { get; set; }
            public int ConnectionCount { get; set; }
            public int Flushes;
            public void Flush() => Flushes++;
            public readonly Queue<NetEndpoint> Dropped = new Queue<NetEndpoint>();
            public bool TryTakeDropped(out NetEndpoint peer)
            {
                if (Dropped.Count == 0) { peer = default(NetEndpoint); return false; }
                peer = Dropped.Dequeue();
                return true;
            }
            public NetEndpoint Local => Inner.Local;
            public bool Send(NetEndpoint to, byte[] data, int length) => Inner.Send(to, data, length);
            public bool Receive(out NetEndpoint from, byte[] buffer, out int length) => Inner.Receive(out from, buffer, out length);
            public void Dispose() => Inner.Dispose();
        }

        static OnlineGame Game(ushort tug, int players, string phaseKey) => new OnlineGame
        {
            LobbyId = "lobby-" + tug, TugNumber = tug, Players = players, MaxPlayers = NetProtocol.MaxPlayers,
            PhaseKey = phaseKey, VersionKey = "fake", CreatedUtc = DateTime.UtcNow,
        };

        IEnumerator Board()
        {
            m_Link = new FakeLink();
            NetSession.OnlineLinkFactory = () => m_Link;
            m_Board = Object.FindAnyObjectByType<NetLobbyBoard>();
            m_Mission = Object.FindAnyObjectByType<MissionBoard>();
            Assert.IsNotNull(m_Board);
            Assert.IsNotNull(m_Board.hostOnlineButton, "The panel has HOST ONLINE.");
            Assert.IsNotNull(m_Board.findOnlineButton, "The panel has FIND ONLINE.");
            Assert.AreEqual(NetLobbyBoard.ResultRowCount, m_Board.joinButtons.Length, "Room for five online games.");
            yield return null;
        }

        IEnumerator Press(PushButton button)
        {
            Assert.IsNotNull(button);
            m_Board.Refresh();
            if (m_LastPress.TryGetValue(button, out float last))
            {
                while (Time.unscaledTime - last < button.cooldown + 0.02f)
                {
                    Tick();
                    yield return null;
                }
            }
            Assert.IsTrue(button.isActiveAndEnabled, button.name + " is on show");
            Assert.IsTrue(button.Interactable, button.name + " can be pressed");
            Assert.IsTrue(button.Press(null), button.name + " pressed");
            m_LastPress[button] = Time.unscaledTime;
            m_Board.Refresh();
            yield return null;
        }

        string Status { get { m_Board.Refresh(); return m_Board.status.text; } }
        string Notice { get { m_Board.Refresh(); return m_Board.notice.text; } }

        [UnityTest]
        public IEnumerator Panel_HostOnline_ThenFindOnlineAndJoin_EachWithLeave_RunTheSameSessionsOverTheLink()
        {
            yield return Board();
            m_Board.Refresh();
            StringAssert.Contains("HOST ONLINE", m_Board.status.text);
            StringAssert.Contains("Unity's servers", m_Board.info.text);
            StringAssert.Contains("your voice is sent to it while you talk", m_Board.info.text.Replace("\n", " "));
            Assert.IsFalse(m_Net.HasOnlineLink, "No online link exists before an online button is pressed.");

            // --- HOST ONLINE: busy while the link works, then the same host session over its transport
            yield return Press(m_Board.hostOnlineButton);
            Assert.AreEqual(1, m_Link.HostCalls);
            Assert.AreEqual(OnlineConfig.VersionKey(NetProtocol.Version, m_Net.ContentHash), m_Link.LastVersionKey, "Only games of this exact build are listed.");
            Assert.AreEqual(OnlineStep.Hosting, m_Net.OnlineBusy);
            StringAssert.StartsWith("HOSTING ONLINE...", Status);
            Assert.IsFalse(m_Board.hostButton.Interactable);
            Assert.IsFalse(m_Board.findOnlineButton.Interactable);
            Assert.IsTrue(m_Board.leaveButton.Interactable);
            Assert.IsTrue(m_Director.ContractStartBlocked, "No solo contract while the online host is opening.");
            Assert.IsNull(m_Director.Net);
            yield return Frames(5);
            Assert.AreEqual(LobbyMode.Offline, m_Net.Mode, "Nothing changes until the link answers: no frame waits for it.");

            var hostTransport = m_Hub.Create();
            m_Link.Host.SetResult(new OnlineHostResult { Ok = true, Transport = hostTransport, TugNumber = 4821, Code = "K7Q2ZD", Listed = true });
            yield return Frames(2);
            Assert.AreEqual(LobbyMode.Hosting, m_Net.Mode);
            Assert.IsTrue(m_Net.IsOnline);
            Assert.AreSame(m_Net, m_Director.Net);
            Assert.AreSame(hostTransport, m_Net.Transport, "The link's transport carries the ordinary host session.");
            StringAssert.Contains("TUG 4821  ONLINE", Status);
            StringAssert.Contains("CODE  K7Q2ZD", Status);
            StringAssert.Contains("CREW 1/4:  ORANGE (YOU)", Status);
            StringAssert.DoesNotContain("NOT LISTED", Status);
            m_Link.IsListed = false; // the lobby entry went away (heartbeat refused, deleted...)
            StringAssert.Contains("NOT LISTED", Status, "The panel reads the listing live.");
            m_Link.IsListed = true;
            Assert.AreEqual(NetSession.OnlineInterpolationDelay, m_Net.InterpolationDelay, "Online: drawn further behind for internet jitter.");
            Assert.AreEqual(NetSession.OnlineInterpolationDelay, CoopItemReplica.Delay);
            Assert.IsFalse(m_Director.ContractStartBlocked);
            Assert.Greater(m_Link.TickCalls, 0, "The lobby listing is kept alive.");
            Assert.AreEqual(1, m_Link.LastPlayers);
            Assert.AreEqual(MissionPhase.Hub, m_Link.LastPhase);

            var bot = AddBot(hostTransport.Local);
            yield return Until(() => m_Net.Crew(1) != null && m_Link.LastPlayers == 2);
            StringAssert.Contains("CREW 2/4:  ORANGE (YOU)  BLUE", Status);
            Assert.AreEqual(2, m_Link.LastPlayers, "The listing shows the crew count.");
            bot.Session.SendStartRequest();
            yield return Until(() => m_Director.Phase == MissionPhase.Active && m_Link.LastPhase == MissionPhase.Active && bot.World.Calls[NetMsg.RunStart] > 0);
            Assert.AreEqual(MissionPhase.Active, m_Link.LastPhase, "...and that the crew is out on a contract.");
            Assert.Greater(bot.World.Calls[NetMsg.RunStart], 0);

            int leavesBefore = m_Link.LeaveCalls;
            yield return Press(m_Board.leaveButton);
            Assert.AreEqual(LobbyMode.Offline, m_Net.Mode);
            Assert.IsFalse(m_Net.IsOnline);
            Assert.IsNull(m_Director.Net);
            Assert.AreEqual(MissionPhase.Hub, m_Director.Phase);
            Assert.Greater(m_Link.LeaveCalls, leavesBefore, "LEAVE unlists the online game.");
            yield return Until(() => bot.World.Ended == SessionEndReason.HostClosed);
            Assert.AreEqual(SessionEndReason.HostClosed, bot.World.Ended);
            yield return Frames(100, 0.05); // the closed host answers stragglers, then lets go
            Assert.IsTrue(hostTransport.IsDisposed, "The online host's transport is closed.");
            Assert.IsTrue(m_Net.IsOffline);
            Assert.AreEqual(SnapshotBuffer<PoseSnapshot>.InterpolationDelay, CoopItemReplica.Delay, "Back to the Wi-Fi delay.");
            int ticks = m_Link.TickCalls;
            yield return Frames(20);
            Assert.AreEqual(ticks, m_Link.TickCalls, "Nothing online runs after LEAVE.");
            bot.Frozen = true;

            // --- FIND ONLINE, then JOIN next to a game
            var host = AddScriptedHost(tug: 4821);
            yield return Press(m_Board.findOnlineButton);
            Assert.AreEqual(1, m_Link.FindCalls);
            Assert.AreEqual(OnlineStep.Finding, m_Net.OnlineBusy);
            StringAssert.StartsWith("SEARCHING ONLINE...", Status);
            Assert.IsTrue(m_Board.resultsPanel.activeSelf);
            var games = new List<OnlineGame> { Game(4821, 1, "HUB"), Game(1307, 4, "HUB"), Game(2718, 2, "ACTIVE") };
            m_Link.Find.SetResult(new OnlineFindResult { Ok = true, Games = games });
            yield return Frames(2);
            StringAssert.StartsWith("ONLINE: FOUND 3: PRESS JOIN", Status);
            Assert.IsTrue(m_Board.resultRows[2].activeSelf);
            Assert.IsFalse(m_Board.resultRows[3].activeSelf);
            Assert.AreEqual("TUG 4821  1/4  IN HUB", m_Board.resultLabels[0].text);
            Assert.AreEqual("TUG 1307  4/4  IN HUB  FULL", m_Board.resultLabels[1].text);
            Assert.AreEqual("TUG 2718  2/4  ON CONTRACT", m_Board.resultLabels[2].text);
            Assert.IsTrue(m_Board.joinButtons[0].Interactable);
            Assert.IsFalse(m_Board.joinButtons[1].Interactable, "A full crew cannot be joined.");
            Assert.IsFalse(m_Board.joinButtons[2].Interactable, "A crew on a contract cannot be joined.");

            yield return Press(m_Board.joinButtons[0]);
            Assert.AreEqual(1, m_Link.JoinCalls);
            Assert.AreSame(games[0], m_Link.LastGame);
            StringAssert.StartsWith("JOINING TUG 4821  ONLINE...", Status);
            Assert.IsTrue(m_Director.ContractStartBlocked);
            var clientTransport = m_Hub.Create();
            m_Link.Join.SetResult(new OnlineJoinResult { Ok = true, Transport = clientTransport, Host = host.Endpoint });
            yield return Until(() => m_Net.Mode == LobbyMode.Joined);
            Assert.AreEqual(LobbyMode.Joined, m_Net.Mode);
            Assert.IsTrue(m_Net.IsOnline);
            Assert.AreSame(clientTransport, m_Net.Transport);
            StringAssert.Contains("JOINED TUG 4821  ONLINE", Status);
            StringAssert.Contains("YOUR SUIT IS BLUE", Status);
            Assert.IsFalse(m_Director.ContractStartBlocked);
            Assert.IsTrue(host.World.Joined[1]);

            yield return Press(m_Board.leaveButton);
            Assert.AreEqual(LobbyMode.Offline, m_Net.Mode);
            Assert.IsTrue(clientTransport.IsDisposed, "LEAVE closed the online client's transport.");
            Assert.IsTrue(m_Net.IsOffline);
            Assert.IsNull(m_Director.Net);
            yield return Until(() => !host.World.Joined[1]);
            Assert.AreEqual(PlayerLeaveReason.Bye, host.World.LeftReason[1]);
            Assert.IsFalse(m_Board.resultsPanel.activeSelf);
            Assert.AreEqual("", Notice);

            // Solo again exactly as before.
            m_Mission.Refresh();
            Assert.AreEqual("SALVAGE CONTRACT", m_Mission.title.text);
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            Assert.IsTrue(m_Director.CreatureAboard);
        }

        [UnityTest]
        public IEnumerator Panel_EveryOnlineProblemIsShownInWords_AndLeavesTheGameSolo()
        {
            yield return Board();
            foreach (OnlineProblem problem in Enum.GetValues(typeof(OnlineProblem)))
            {
                if (problem == OnlineProblem.None) continue;
                yield return Press(m_Board.hostOnlineButton);
                m_Link.Host.SetResult(new OnlineHostResult { Problem = problem, Text = OnlineErrors.Text(problem) });
                yield return Frames(2);
                Assert.AreEqual(OnlineStep.None, m_Net.OnlineBusy);
                Assert.AreEqual(LobbyMode.Offline, m_Net.Mode, problem.ToString());
                Assert.IsTrue(m_Net.IsOffline);
                Assert.IsNull(m_Director.Net);
                Assert.IsFalse(m_Director.ContractStartBlocked);
                if (problem == OnlineProblem.Cancelled)
                {
                    Assert.AreEqual("", Notice, "A cancelled step says nothing.");
                    continue;
                }
                string text = OnlineErrors.Text(problem);
                Assert.IsNotEmpty(text, problem.ToString());
                StringAssert.StartsWith(text + "\n", Notice, problem + " is shown as its text");
                Assert.Greater(Notice.Length, text.Length + 10, problem + " says what to do about it");
            }

            // FIND ONLINE and JOIN problems show the same way.
            yield return Press(m_Board.findOnlineButton);
            m_Link.Find.SetResult(new OnlineFindResult { Problem = OnlineProblem.NoInternet, Games = new List<OnlineGame>() });
            yield return Frames(2);
            StringAssert.StartsWith("NO INTERNET", Notice);
            yield return Press(m_Board.findOnlineButton);
            m_Link.Find.SetResult(new OnlineFindResult { Ok = true, Games = new List<OnlineGame> { Game(5150, 1, "HUB") } });
            yield return Frames(2);
            Assert.AreEqual("", Notice, "A new answer clears the old problem.");
            yield return Press(m_Board.joinButtons[0]);
            m_Link.Join.SetResult(new OnlineJoinResult { Problem = OnlineProblem.GameGone, Text = OnlineErrors.Text(OnlineProblem.GameGone) });
            yield return Frames(2);
            StringAssert.StartsWith("GAME GONE", Notice);
            Assert.AreEqual(LobbyMode.Offline, m_Net.Mode);
            Assert.IsTrue(m_Net.IsOffline);
            Assert.IsFalse(m_Director.ContractStartBlocked);

            // An online game of a different build is refused with the same words as on Wi-Fi.
            var otherBuild = AddScriptedHost(contentHash: m_Net.ContentHash ^ 0x5A5A5A5Au, tug: 3333);
            Assert.IsTrue(m_Net.JoinOnlineCode("ABC123"));
            Assert.AreEqual(1, m_Link.CodeCalls);
            m_Link.Join.SetResult(new OnlineJoinResult { Ok = true, Transport = m_Hub.Create(), Host = otherBuild.Endpoint });
            yield return Until(() => m_Net.Mode == LobbyMode.Offline && m_Net.OnlineBusy == OnlineStep.None);
            Assert.AreEqual(LobbyNotice.ContentMismatch, m_Net.Notice);
            StringAssert.StartsWith("DIFFERENT VERSION", Notice);
            Assert.IsTrue(m_Net.IsOffline);

            // Not from a contract: crews form in the tug, online too. Nothing is asked of the link.
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            int hostCalls = m_Link.HostCalls, findCalls = m_Link.FindCalls;
            yield return Press(m_Board.hostOnlineButton);
            StringAssert.StartsWith("FINISH THE CONTRACT FIRST", Notice);
            yield return Press(m_Board.findOnlineButton);
            StringAssert.StartsWith("FINISH THE CONTRACT FIRST", Notice);
            Assert.AreEqual(hostCalls, m_Link.HostCalls);
            Assert.AreEqual(findCalls, m_Link.FindCalls);
            Assert.IsTrue(m_Net.IsOffline);
        }

        [UnityTest]
        public IEnumerator Panel_LeaveWhileTheLinkIsWorking_CancelsIt_AndATransportThatArrivesLateIsClosed()
        {
            yield return Board();
            yield return Press(m_Board.hostOnlineButton);
            yield return Press(m_Board.leaveButton);
            Assert.AreEqual(OnlineStep.None, m_Net.OnlineBusy);
            Assert.IsTrue(m_Net.IsOffline);
            Assert.IsFalse(m_Director.ContractStartBlocked);
            Assert.Greater(m_Link.LeaveCalls, 0);

            // The abandoned step is still winding down inside the link: the online buttons wait for it instead of
            // letting the next press be answered "still working".
            Assert.IsTrue(m_Net.OnlineSettling);
            m_Board.Refresh();
            Assert.IsFalse(m_Board.hostOnlineButton.Interactable);
            Assert.IsFalse(m_Board.findOnlineButton.Interactable);
            Assert.IsTrue(m_Board.hostButton.Interactable, "Same-Wi-Fi play is not held up.");
            StringAssert.StartsWith("CLOSING THE LAST ONLINE STEP", Status);
            Assert.IsFalse(m_Net.FindOnline());
            int hostCalls = m_Link.HostCalls;

            var late = m_Hub.Create();
            m_Link.Host.SetResult(new OnlineHostResult { Ok = true, Transport = late, TugNumber = 1, Code = "LATE00", Listed = true });
            yield return Frames(3);
            Assert.IsTrue(late.IsDisposed, "What the cancelled step opened is closed when it arrives.");
            Assert.AreEqual(LobbyMode.Offline, m_Net.Mode);
            Assert.IsNull(m_Director.Net);
            Assert.IsFalse(m_Net.OnlineSettling);
            m_Board.Refresh();
            Assert.IsTrue(m_Board.hostOnlineButton.Interactable, "Back as soon as the old step is over.");
            yield return Press(m_Board.hostOnlineButton);
            Assert.AreEqual(hostCalls + 1, m_Link.HostCalls);
            yield return Press(m_Board.leaveButton);
        }

        [UnityTest]
        public IEnumerator Relay_AClientWhoseRelayLinkDrops_LeavesAtOnce_NotAfter60s_AndItsAwayNoticeIsFlushed()
        {
            yield return Board();
            var host = AddScriptedHost(tug: 4821);
            Assert.IsTrue(m_Net.JoinOnlineCode("ABC123"));
            var relay = new FakeRelay(m_Hub.Create(), false);
            m_Link.Join.SetResult(new OnlineJoinResult { Ok = true, Transport = relay, Host = host.Endpoint });
            yield return Until(() => m_Net.Mode == LobbyMode.Joined);
            Assert.IsTrue(m_Net.IsOnline);

            // Going away: the notice is put on the wire at once (the app may be suspended right after).
            int flushes = relay.Flushes;
            m_Net.SendMessage("OnApplicationPause", true);
            Assert.Greater(relay.Flushes, flushes, "The Away notice was flushed out through Relay.");
            m_Net.SendMessage("OnApplicationPause", false);

            // Relay drops the connection (the host slept past Relay's limit, or its allocation died).
            relay.LinkLost = true;
            double before = m_Clock;
            yield return Frames(2);
            Assert.AreEqual(LobbyMode.Offline, m_Net.Mode, "Ended at once, not after 60 s of HOST NOT RESPONDING.");
            Assert.Less(m_Clock - before, 1.0);
            Assert.AreEqual(LobbyNotice.LinkLost, m_Net.Notice);
            StringAssert.StartsWith("LINK LOST", Notice);
            Assert.IsTrue(relay.Inner.IsDisposed);
            Assert.IsNull(m_Director.Net);
            Assert.Greater(m_Link.LeaveCalls, 0);

            // The same for an allocation that went invalid.
            Assert.IsTrue(m_Net.JoinOnlineCode("ABC123"));
            var second = new FakeRelay(m_Hub.Create(), false);
            m_Link.Join.SetResult(new OnlineJoinResult { Ok = true, Transport = second, Host = host.Endpoint });
            yield return Until(() => m_Net.Mode == LobbyMode.Joined);
            second.AllocationInvalid = true;
            yield return Frames(2);
            Assert.AreEqual(LobbyMode.Offline, m_Net.Mode);
            Assert.AreEqual(LobbyNotice.LinkLost, m_Net.Notice);
        }

        [UnityTest]
        public IEnumerator Relay_AHostWhoseRelaySideDies_ClosesTheGame_UnlistsIt_AndSaysLinkLost()
        {
            yield return Board();

            // The allocation went invalid (Relay dropped the sleeping host): close, unlist, LINK LOST.
            yield return Press(m_Board.hostOnlineButton);
            var relay = new FakeRelay(m_Hub.Create(), true);
            m_Link.Host.SetResult(new OnlineHostResult { Ok = true, Transport = relay, TugNumber = 4821, Code = "K7Q2ZD", Listed = true });
            yield return Until(() => m_Net.Mode == LobbyMode.Hosting);
            var bot = AddBot(relay.Local);
            yield return Until(() => m_Net.Crew(1) != null);
            relay.ConnectionCount = 1;
            int leaves = m_Link.LeaveCalls;
            relay.AllocationInvalid = true;
            yield return Frames(2);
            Assert.AreEqual(LobbyMode.Offline, m_Net.Mode, "Not left claiming HOSTING ... ONLINE.");
            Assert.Greater(m_Link.LeaveCalls, leaves, "The dead game is taken off the list.");
            Assert.IsNull(m_Director.Net);
            StringAssert.StartsWith("LINK LOST", Notice);
            yield return Until(() => bot.World.Ended != SessionEndReason.None);
            bot.Frozen = true;
            yield return Frames(100, 0.05);

            // The Relay server connection itself lost.
            yield return Press(m_Board.hostOnlineButton);
            var third = new FakeRelay(m_Hub.Create(), true);
            m_Link.Host.SetResult(new OnlineHostResult { Ok = true, Transport = third, TugNumber = 4823, Code = "K7Q2ZF", Listed = true });
            yield return Until(() => m_Net.Mode == LobbyMode.Hosting);
            third.RelayLost = true;
            yield return Frames(2);
            Assert.AreEqual(LobbyMode.Offline, m_Net.Mode);
        }

        [UnityTest]
        public IEnumerator Relay_OneCrewMembersConnectionDrops_OnlyThatOneIsLetGo_TheGameStaysOpenAndListed()
        {
            yield return Board();
            yield return Press(m_Board.hostOnlineButton);
            var relay = new FakeRelay(m_Hub.Create(), true);
            m_Link.Host.SetResult(new OnlineHostResult { Ok = true, Transport = relay, TugNumber = 4821, Code = "K7Q2ZD", Listed = true });
            yield return Until(() => m_Net.Mode == LobbyMode.Hosting);
            var a = AddBot(relay.Local);
            var b = AddBot(relay.Local);
            yield return Until(() => m_Net.Crew(1) != null && m_Net.Crew(2) != null && b.World.Present[1]);
            Assert.AreEqual(3, m_Net.PlayerCount);
            int leaves = m_Link.LeaveCalls;

            // Relay drops crew 2's connection (its headset slept): only that one goes, at once, not after 45 s.
            a.Frozen = true;
            relay.Disconnects = 1;
            relay.ConnectionCount = 1;
            relay.Dropped.Enqueue(a.Session.Transport.Local);
            double before = m_Clock;
            yield return Frames(3);
            Assert.Less(m_Clock - before, 1.0);
            Assert.IsNull(m_Net.Crew(1), "Its suit is gone.");
            Assert.IsNotNull(m_Net.Crew(2), "The rest of the crew stays.");
            Assert.AreEqual(2, m_Net.PlayerCount);
            yield return Until(() => !b.World.Present[1]);
            Assert.IsFalse(b.World.Present[1], "The others are told it left.");
            Assert.AreEqual(LobbyMode.Hosting, m_Net.Mode, "Still hosting...");
            Assert.AreEqual(leaves, m_Link.LeaveCalls, "...and still listed.");
            Assert.IsTrue(m_Net.OnlineListed);
            Assert.AreEqual(LobbyNotice.None, m_Net.Notice);

            // The last crew member's connection drops too: still hosting, alone, for whoever comes next.
            b.Frozen = true;
            relay.ConnectionCount = 0;
            relay.Disconnects = 2;
            relay.Dropped.Enqueue(b.Session.Transport.Local);
            yield return Frames(3);
            Assert.IsNull(m_Net.Crew(2));
            Assert.AreEqual(1, m_Net.PlayerCount);
            Assert.AreEqual(LobbyMode.Hosting, m_Net.Mode);
            Assert.AreEqual(leaves, m_Link.LeaveCalls);
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Panel_TheSceneUnloadedMidRequest_ClosesWhatArrivesLate_AndNothingIsApplied()
        {
            yield return Board();
            yield return Press(m_Board.findOnlineButton);
            m_Link.Find.SetResult(new OnlineFindResult { Ok = true, Games = new List<OnlineGame> { Game(4821, 1, "HUB") } });
            yield return Frames(2);
            yield return Press(m_Board.joinButtons[0]);
            var link = m_Link;
            var join = link.Join;
            int leaves = link.LeaveCalls;

            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            yield return null;
            Assert.Greater(link.LeaveCalls, leaves, "The scene going away cancelled the online step.");
            var late = m_Hub.Create();
            join.SetResult(new OnlineJoinResult { Ok = true, Transport = late, Host = new NetEndpoint(10, 82, 0, 1, NetProtocol.HostPort) });
            yield return null;
            yield return null;
            Assert.IsTrue(late.IsDisposed, "The Relay connection that arrived after the scene was gone is closed.");
            var fresh = MissionDirector.Active.GetComponent<NetSession>();
            Assert.IsTrue(fresh.IsOffline);
            Assert.IsFalse(fresh.HasOnlineLink);
            Assert.IsNull(MissionDirector.Active.Net);
        }
    }
}

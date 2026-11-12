using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Net;
using TrashAstronauts.UI;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// The CO-OP CREW panel in the tug, pressed the way the other tests press PushButtons: HOST CO-OP, FIND CREW
    /// with a JOIN per result, ENTER CODE on the keypad, LEAVE, the words shown for every refusal, and the
    /// mission board refusing a contract while the crew is linked. In-memory network, fake session clock.
    /// </summary>
    public class NetLobbyPlayTests : NetPlayFixture
    {
        NetLobbyBoard m_Board;
        MissionBoard m_Mission;
        readonly Dictionary<PushButton, float> m_LastPress = new Dictionary<PushButton, float>();

        IEnumerator Board()
        {
            m_Board = Object.FindAnyObjectByType<NetLobbyBoard>();
            m_Mission = Object.FindAnyObjectByType<MissionBoard>();
            Assert.IsNotNull(m_Board, "The tug has a CO-OP CREW panel.");
            Assert.AreSame(m_Net, m_Board.session);
            yield return null;
        }

        /// <summary>Presses a button like a hand would (waiting out its debounce if it was pressed a moment ago).</summary>
        IEnumerator Press(PushButton button)
        {
            Assert.IsNotNull(button);
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
        public IEnumerator Board_HostCoop_ThenFindCrewAndJoin_ThenEnterCode_EachWithLeave()
        {
            yield return Board();
            m_Board.Refresh();
            Assert.IsFalse(m_Board.leaveButton.Interactable, "Nothing to leave offline.");
            Assert.IsTrue(m_Board.infoPanel.activeSelf);
            StringAssert.Contains("HOST CO-OP", m_Board.status.text);
            StringAssert.Contains("Nothing is sent", m_Board.info.text);
            Assert.IsTrue(m_Net.IsOffline);

            // --- HOST CO-OP
            yield return Press(m_Board.hostButton);
            Assert.AreEqual(LobbyMode.Hosting, m_Net.Mode);
            uint address = m_Net.Transport.Local.address;
            StringAssert.Contains("HOSTING  TUG ", Status);
            StringAssert.Contains("CREW CODE  " + JoinCode.Format(address), Status);
            StringAssert.Contains("CREW 1/4:  ORANGE (YOU)", Status);
            Assert.IsFalse(m_Board.hostButton.Interactable);
            Assert.IsTrue(m_Board.leaveButton.Interactable);

            // The mission board will not start a contract while the crew is linked, and says why.
            m_Mission.Refresh();
            Assert.AreEqual("CO-OP CREW LINKED", m_Mission.title.text);
            StringAssert.Contains("LEAVE", m_Mission.body.text);
            Assert.IsFalse(m_Mission.primaryButton.Interactable);
            Assert.IsFalse(m_Mission.primaryButton.Press(null));
            Assert.IsFalse(m_Director.StartMission(PlayerId.Local));
            Assert.AreEqual(MissionPhase.Hub, m_Director.Phase);

            var bot = AddBot(m_Net.Transport.Local);
            yield return Until(() => m_Net.Crew(1) != null);
            StringAssert.Contains("CREW 2/4:  ORANGE (YOU)  BLUE", Status);

            yield return Press(m_Board.leaveButton);
            Assert.AreEqual(LobbyMode.Offline, m_Net.Mode);
            Assert.IsNull(m_Director.Net);
            yield return Until(() => bot.World.Ended == SessionEndReason.HostClosed);
            Assert.AreEqual(SessionEndReason.HostClosed, bot.World.Ended);
            m_Mission.Refresh();
            Assert.AreEqual("SALVAGE CONTRACT", m_Mission.title.text);
            Assert.IsTrue(m_Mission.primaryButton.Interactable, "Solo again: START CONTRACT is back.");
            yield return Frames(100, 0.05); // let the closed host's socket go

            // --- FIND CREW, then JOIN next to the tug found
            var host = AddScriptedHost(tug: 4821);
            yield return Press(m_Board.findButton);
            Assert.AreEqual(LobbyMode.Searching, m_Net.Mode);
            Assert.IsTrue(m_Board.resultsPanel.activeSelf);
            Assert.IsFalse(m_Board.keypadPanel.activeSelf);
            yield return Until(() => m_Net.FoundHosts != null && m_Net.FoundHosts.Count > 0);
            m_Board.Refresh();
            Assert.IsTrue(m_Board.resultRows[0].activeSelf);
            Assert.IsFalse(m_Board.resultRows[1].activeSelf);
            StringAssert.StartsWith("TUG 4821  1/4  IN HUB", m_Board.resultLabels[0].text);
            yield return Press(m_Board.joinButtons[0]);
            Assert.AreEqual(LobbyMode.Connecting, m_Net.Mode);
            yield return Until(() => m_Net.Mode == LobbyMode.Joined);
            Assert.AreEqual(LobbyMode.Joined, m_Net.Mode);
            StringAssert.Contains("JOINED TUG 4821", Status);
            StringAssert.Contains("YOUR SUIT IS BLUE", Status);
            StringAssert.Contains("ORANGE (HOST)  BLUE (YOU)", Status);
            Assert.AreSame(m_Net, m_Director.Net);
            m_Mission.Refresh();
            Assert.IsFalse(m_Mission.primaryButton.Interactable, "A joined crew member cannot start a contract either.");
            Assert.IsFalse(m_Director.StartMission(m_Director.LocalPlayerId));

            yield return Press(m_Board.leaveButton);
            Assert.AreEqual(LobbyMode.Offline, m_Net.Mode);
            Assert.IsNull(m_Director.Net);
            yield return Until(() => !host.World.Joined[1]);
            Assert.IsFalse(host.World.Joined[1]);

            // --- ENTER CODE: C is prefilled with our own third octet; type D and JOIN
            yield return Press(m_Board.codeButton);
            Assert.AreEqual(LobbyMode.EnteringCode, m_Net.Mode);
            Assert.IsTrue(m_Board.keypadPanel.activeSelf);
            Assert.IsFalse(m_Board.resultsPanel.activeSelf);
            int c = JoinCode.C(host.Endpoint.address), d = JoinCode.D(host.Endpoint.address);
            StringAssert.StartsWith("C " + JoinCode.Pad3(c) + "  D ___", m_Board.codeEntry.text);
            foreach (char ch in d.ToString()) yield return Press(m_Board.digitButtons[ch - '0']);
            m_Board.Refresh();
            Assert.AreEqual("C " + JoinCode.Pad3(c) + "  D " + d.ToString().PadRight(3, '_'), m_Board.codeEntry.text);
            yield return Press(m_Board.joinCodeButton);
            yield return Until(() => m_Net.Mode == LobbyMode.Joined);
            Assert.AreEqual(LobbyMode.Joined, m_Net.Mode, "Joined by crew code.");
            StringAssert.Contains("JOINED CREW CODE", Status);
            yield return Press(m_Board.leaveButton);
            Assert.AreEqual(LobbyMode.Offline, m_Net.Mode);
            Assert.IsTrue(m_Net.IsOffline);

            // Solo again: a contract starts from the mission board.
            m_Mission.Refresh();
            Assert.IsTrue(m_Mission.primaryButton.Press(null));
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
        }

        [UnityTest]
        public IEnumerator Board_KeypadBack_AndEditC_EditTheCode_AndABadCodeIsRefusedInWords()
        {
            yield return Board();
            yield return Press(m_Board.codeButton);
            yield return Press(m_Board.digitButtons[4]);
            yield return Press(m_Board.digitButtons[2]);
            m_Board.Refresh();
            StringAssert.EndsWith("D 42_", m_Board.codeEntry.text);
            yield return Press(m_Board.backButton);
            m_Board.Refresh();
            StringAssert.EndsWith("D 4__", m_Board.codeEntry.text);
            yield return Press(m_Board.fieldCButton);
            m_Board.Refresh();
            StringAssert.StartsWith("C ___", m_Board.codeEntry.text);
            Assert.AreEqual("> C <", m_Board.fieldCButton.Text);
            yield return Press(m_Board.digitButtons[9]);
            yield return Press(m_Board.digitButtons[9]);
            yield return Press(m_Board.digitButtons[9]);
            StringAssert.StartsWith("BAD CODE", Notice, "999 is not an address octet.");
            yield return Press(m_Board.fieldDButton);
            yield return Press(m_Board.joinCodeButton);
            Assert.AreEqual(LobbyMode.EnteringCode, m_Net.Mode, "An empty D cannot be joined.");
            StringAssert.StartsWith("BAD CODE", Notice);
            yield return Press(m_Board.leaveButton);
            Assert.AreEqual(LobbyMode.Offline, m_Net.Mode);
            Assert.IsTrue(m_Net.IsOffline);
        }

        [UnityTest]
        public IEnumerator Board_EveryRefusalIsShownInPlainWords()
        {
            yield return Board();

            // FULL: three crew already aboard that tug.
            var full = AddScriptedHost(tug: 1111);
            var bots = new List<Bot> { AddBot(full.Endpoint), AddBot(full.Endpoint), AddBot(full.Endpoint) };
            yield return Until(() => full.Session.PlayerCount == 4 && bots.TrueForAll(b => b.Session.Welcomed));
            Assert.IsTrue(m_Net.Join(full.Endpoint));
            yield return Until(() => m_Net.Mode == LobbyMode.Offline);
            Assert.AreEqual(LobbyNotice.Full, m_Net.Notice);
            StringAssert.StartsWith("FULL", Notice);
            Assert.IsNull(m_Director.Net);
            Assert.IsFalse(m_Director.ContractStartBlocked, "A refused join leaves the game solo.");
            full.Frozen = true;
            foreach (var b in bots) b.Frozen = true;

            // DIFFERENT VERSION: another protocol, or another build of the content.
            var oldVersion = AddScriptedHost(protocol: NetProtocol.Version + 1, tug: 2222);
            Assert.IsTrue(m_Net.Join(oldVersion.Endpoint));
            yield return Until(() => m_Net.Mode == LobbyMode.Offline);
            Assert.AreEqual(LobbyNotice.VersionMismatch, m_Net.Notice);
            StringAssert.StartsWith("DIFFERENT VERSION", Notice);
            oldVersion.Frozen = true;

            var otherBuild = AddScriptedHost(contentHash: m_Net.ContentHash ^ 0x5A5A5A5Au, tug: 3333);
            Assert.IsTrue(m_Net.Join(otherBuild.Endpoint));
            yield return Until(() => m_Net.Mode == LobbyMode.Offline);
            Assert.AreEqual(LobbyNotice.ContentMismatch, m_Net.Notice);
            StringAssert.StartsWith("DIFFERENT VERSION", Notice);
            otherBuild.Frozen = true;

            // CONTRACT IN PROGRESS: that crew is out on a wreck.
            var busy = AddScriptedHost(tug: 4444);
            busy.World.CurrentPhase = MissionPhase.Active;
            Assert.IsTrue(m_Net.Join(busy.Endpoint));
            yield return Until(() => m_Net.Mode == LobbyMode.Offline);
            Assert.AreEqual(LobbyNotice.ContractInProgress, m_Net.Notice);
            StringAssert.StartsWith("CONTRACT IN PROGRESS", Notice);
            busy.Frozen = true;

            // NO ANSWER: nobody at that address (5 s of Hello).
            Assert.IsTrue(m_Net.Join(new NetEndpoint(10, 0, 0, 200, NetProtocol.HostPort)));
            yield return Until(() => m_Net.Mode == LobbyMode.Offline, dt: 0.1);
            Assert.AreEqual(LobbyNotice.NoAnswer, m_Net.Notice);
            StringAssert.StartsWith("NO ANSWER", Notice);

            // PORT BUSY: something else has the host port.
            var factory = NetSession.TransportFactory;
            NetSession.TransportFactory = port => port == NetProtocol.HostPort ? null : factory(port);
            yield return Press(m_Board.hostButton);
            NetSession.TransportFactory = factory;
            Assert.AreEqual(LobbyMode.Offline, m_Net.Mode);
            Assert.AreEqual(LobbyNotice.PortBusy, m_Net.Notice);
            StringAssert.StartsWith("PORT BUSY", Notice);
            Assert.IsNull(m_Director.Net);
            Assert.IsFalse(m_Director.ContractStartBlocked);

            // NOBODY FOUND after 10 s of searching an empty network: the router client-isolation hint.
            yield return Press(m_Board.findButton);
            Assert.AreEqual(LobbyMode.Searching, m_Net.Mode);
            yield return Frames(35, 0.1);
            Assert.AreEqual(0, m_Net.FoundHosts.Count, "Every host in this test is asleep or refuses; none answer probes.");
            Assert.AreNotEqual(LobbyNotice.NobodyFound, m_Net.Notice, "Not after 3.5 s.");
            StringAssert.Contains("NOBODY ANSWERED", Status);
            yield return Frames(70, 0.1);
            Assert.AreEqual(LobbyNotice.NobodyFound, m_Net.Notice);
            StringAssert.StartsWith("NOBODY FOUND", Notice);
            StringAssert.Contains("client isolation", Notice);
            yield return Press(m_Board.leaveButton);
            Assert.IsTrue(m_Net.IsOffline);

            // Not from a contract: crews form in the tug between contracts.
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            m_Board.Refresh();
            yield return Press(m_Board.hostButton);
            Assert.AreEqual(LobbyMode.Offline, m_Net.Mode);
            StringAssert.StartsWith("FINISH THE CONTRACT FIRST", Notice);
            Assert.IsTrue(m_Net.IsOffline, "No socket was opened.");
            Assert.IsNull(m_Director.Net);
        }
    }
}

using TMPro;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Net;
using TrashAstronauts.Net.Online;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.UI
{
    /// <summary>
    /// The CO-OP CREW panel on the tug's east wall. Everything is a big physical button, so no keyboard is
    /// needed in the headset:
    ///   HOST CO-OP   open this tug to crew on the same Wi-Fi; shows the tug number and the crew code.
    ///   FIND CREW    look for hosting tugs on this Wi-Fi; each one found gets its own JOIN button.
    ///   ENTER CODE   a 10-key pad for the host's crew code (C and D), for networks FIND CREW cannot search.
    ///   HOST ONLINE  open this tug to crew anywhere, through Unity Relay and Lobby; shows "TUG n  ONLINE" and a
    ///                6-character code.
    ///   FIND ONLINE  list up to 5 online games of this build; each gets its own JOIN button.
    ///   LEAVE        back to playing alone, whichever kind of link it was.
    /// There is no keypad for the 6-character online code: 36 letter-and-digit keys do not fit this panel at a
    /// size a gloved hand can hit, so online games are joined from the FIND ONLINE list.
    /// Nothing touches the network until one of the HOST / FIND / ENTER buttons is pressed, and nothing contacts
    /// Unity's servers until HOST ONLINE or FIND ONLINE is. The panel only drives <see cref="NetSession"/> and
    /// shows its state; every refusal and every online problem is spelled out in plain words.
    /// </summary>
    public sealed class NetLobbyBoard : MonoBehaviour
    {
        public NetSession session;

        [Header("Text")]
        public TMP_Text title;
        public TMP_Text status;
        public TMP_Text notice;
        public TMP_Text info;

        [Header("Main buttons")]
        public PushButton hostButton;
        public PushButton findButton;
        public PushButton codeButton;
        public PushButton leaveButton;
        public PushButton hostOnlineButton;
        public PushButton findOnlineButton;

        /// <summary>Result rows on the panel: FIND CREW shows up to 3 tugs, FIND ONLINE up to 5 games.</summary>
        public const int ResultRowCount = 5;

        [Header("FIND CREW / FIND ONLINE results")]
        public GameObject resultsPanel;
        public GameObject[] resultRows;
        public TMP_Text[] resultLabels;
        public PushButton[] joinButtons;

        [Header("ENTER CODE keypad")]
        public GameObject keypadPanel;
        public TMP_Text codeEntry;
        [Tooltip("Index = the digit.")]
        public PushButton[] digitButtons;
        public PushButton fieldCButton;
        public PushButton fieldDButton;
        public PushButton backButton;
        public PushButton joinCodeButton;

        public GameObject infoPanel;
        public float refreshInterval = 0.25f;

        /// <summary>Crew colours by slot, as the suits wear them (PlayerAvatar.CrewColors).</summary>
        public static readonly string[] ColourNames = { "ORANGE", "BLUE", "GREEN", "MAGENTA" };

        float m_NextRefresh;
        string m_JoiningLabel = "";

        void OnEnable()
        {
            Hook(hostButton, OnHost);
            Hook(findButton, OnFind);
            Hook(codeButton, OnCode);
            Hook(leaveButton, OnLeave);
            Hook(hostOnlineButton, OnHostOnline);
            Hook(findOnlineButton, OnFindOnline);
            Hook(fieldCButton, OnFieldC);
            Hook(fieldDButton, OnFieldD);
            Hook(backButton, OnBack);
            Hook(joinCodeButton, OnJoinCode);
            if (joinButtons != null) foreach (var b in joinButtons) Hook(b, OnJoinFound);
            if (digitButtons != null) foreach (var b in digitButtons) Hook(b, OnDigit);
            Refresh();
        }

        void OnDisable()
        {
            Unhook(hostButton, OnHost);
            Unhook(findButton, OnFind);
            Unhook(codeButton, OnCode);
            Unhook(leaveButton, OnLeave);
            Unhook(hostOnlineButton, OnHostOnline);
            Unhook(findOnlineButton, OnFindOnline);
            Unhook(fieldCButton, OnFieldC);
            Unhook(fieldDButton, OnFieldD);
            Unhook(backButton, OnBack);
            Unhook(joinCodeButton, OnJoinCode);
            if (joinButtons != null) foreach (var b in joinButtons) Unhook(b, OnJoinFound);
            if (digitButtons != null) foreach (var b in digitButtons) Unhook(b, OnDigit);
        }

        static void Hook(PushButton button, System.Action<PushButton, HandRig> handler)
        {
            if (button != null) button.Pressed += handler;
        }

        static void Unhook(PushButton button, System.Action<PushButton, HandRig> handler)
        {
            if (button != null) button.Pressed -= handler;
        }

        void Update()
        {
            if (Time.unscaledTime < m_NextRefresh) return;
            Refresh();
        }

        // ------------------------------------------------------------------ presses

        void OnHost(PushButton b, HandRig hand) { if (session != null) session.Host(); Refresh(); }
        void OnFind(PushButton b, HandRig hand) { if (session != null) session.Find(); Refresh(); }
        void OnCode(PushButton b, HandRig hand) { if (session != null) session.EnterCode(); Refresh(); }
        void OnLeave(PushButton b, HandRig hand) { if (session != null) session.Leave(); Refresh(); }
        void OnHostOnline(PushButton b, HandRig hand) { if (session != null) session.HostOnline(); Refresh(); }
        void OnFindOnline(PushButton b, HandRig hand) { if (session != null) session.FindOnline(); Refresh(); }
        void OnFieldC(PushButton b, HandRig hand) { if (session != null) session.SelectCodeField(CodeField.C); Refresh(); }
        void OnFieldD(PushButton b, HandRig hand) { if (session != null) session.SelectCodeField(CodeField.D); Refresh(); }
        void OnBack(PushButton b, HandRig hand) { if (session != null) session.PressBack(); Refresh(); }

        void OnJoinCode(PushButton b, HandRig hand)
        {
            if (session == null) return;
            string code = session.Lobby.CodeEntryLine;
            if (session.JoinByCode()) m_JoiningLabel = "CREW CODE " + code;
            Refresh();
        }

        void OnDigit(PushButton b, HandRig hand)
        {
            if (session == null || digitButtons == null) return;
            int digit = System.Array.IndexOf(digitButtons, b);
            if (digit >= 0) session.PressDigit(digit);
            Refresh();
        }

        void OnJoinFound(PushButton b, HandRig hand)
        {
            if (session == null || joinButtons == null) return;
            int index = System.Array.IndexOf(joinButtons, b);
            if (session.OnlineListShown)
            {
                var games = session.OnlineGames;
                string online = index >= 0 && index < games.Count ? "TUG " + games[index].TugNumber : "";
                if (session.JoinOnline(index)) m_JoiningLabel = online;
                Refresh();
                return;
            }
            var hosts = session.FoundHosts;
            string label = hosts != null && index >= 0 && index < hosts.ShownCount ? "TUG " + hosts[index].info.tugNumber : "";
            if (session.JoinFound(index)) m_JoiningLabel = label;
            Refresh();
        }

        // ------------------------------------------------------------------ display

        /// <summary>Plain wording for every notice: a short headline, then what to do about it.</summary>
        public static string NoticeText(LobbyNotice value)
        {
            switch (value)
            {
                case LobbyNotice.NotInHub: return "FINISH THE CONTRACT FIRST\nCrews form in the tug, between contracts.";
                case LobbyNotice.PortBusy: return "PORT BUSY\nSomething else on this headset is using port 47800.";
                case LobbyNotice.NoNetwork: return "NO WI-FI\nConnect this headset to Wi-Fi first.";
                case LobbyNotice.NobodyFound: return "NOBODY FOUND\nSame Wi-Fi? Some routers stop devices seeing each\nother (client isolation, often on guest Wi-Fi). Try ENTER CODE.";
                case LobbyNotice.Full: return "FULL\nThat crew already has 4 players.";
                case LobbyNotice.VersionMismatch: return "DIFFERENT VERSION\nBoth headsets need the same version of the game.";
                case LobbyNotice.ContentMismatch: return "DIFFERENT VERSION\nBoth headsets need the same build of the game.";
                case LobbyNotice.ContractInProgress: return "CONTRACT IN PROGRESS\nThat crew is out on a contract. Try when they are back.";
                case LobbyNotice.NoAnswer: return "NO ANSWER\nNo reply from that tug. Is it still hosting?";
                case LobbyNotice.HostClosed: return "HOST LEFT\nThe host closed the crew. You are playing alone.";
                case LobbyNotice.LinkLost: return "LINK LOST\nNothing heard from the host for too long.";
                case LobbyNotice.Dropped: return "DROPPED\nThe host stopped hearing this headset.";
                case LobbyNotice.BadCode: return "BAD CODE\nC is 0 to 255, D is 1 to 254.";
                default: return "";
            }
        }

        /// <summary>Plain wording for every online problem: the link's own headline, then what to do about it.</summary>
        public static string OnlineNoticeText(OnlineProblem problem)
        {
            string head = OnlineErrors.Text(problem);
            if (head.Length == 0) return "";
            switch (problem)
            {
                case OnlineProblem.NoInternet: return head + "\nThis headset cannot reach the internet. Same Wi-Fi play still works.";
                case OnlineProblem.SignInFailed: return head + "\nUnity's anonymous sign-in did not work. Try again later.";
                case OnlineProblem.ServiceBusy: return head + "\nUnity's servers are busy. Wait a moment, then press again.";
                case OnlineProblem.NotSetUp: return head + "\nOnline play is not available in this build. Same Wi-Fi still works.";
                case OnlineProblem.GameGone: return head + "\nThat game has closed. Press FIND ONLINE again.";
                case OnlineProblem.GameFull: return head + "\nThat crew already has 4 players.";
                case OnlineProblem.NoAnswer: return head + "\nNo reply in time. Check the internet and try again.";
                case OnlineProblem.BadCode: return head + "\nAn online code is 6 letters and digits.";
                case OnlineProblem.Busy: return head + "\nStill working on the last press.";
                default: return head + "\nSomething went wrong online. Try again.";
            }
        }

        /// <summary>"CREW 2/4:  ORANGE (HOST)  BLUE (YOU)".</summary>
        public string RosterLine()
        {
            if (session == null) return "";
            string line = "CREW " + session.PlayerCount + "/" + NetProtocol.MaxPlayers + ":";
            for (int slot = 0; slot < NetProtocol.MaxPlayers; slot++)
            {
                if (!session.IsPresent(slot)) continue;
                line += "  " + ColourNames[slot];
                if (slot == session.LocalSlot) line += " (YOU)";
                else if (slot == NetProtocol.HostSlot) line += " (HOST)";
                var crew = session.Crew(slot);
                if (crew != null && crew.context != null && crew.context.IsAway) line += " AWAY";
            }
            return line;
        }

        public void Refresh()
        {
            m_NextRefresh = Time.unscaledTime + refreshInterval;
            if (session == null) return;
            LobbyState lobby = session.Lobby;
            LobbyMode mode = lobby.Mode;
            OnlineStep busy = session.OnlineBusy;
            bool canStart = (mode == LobbyMode.Offline || mode == LobbyMode.Searching || mode == LobbyMode.EnteringCode) && busy == OnlineStep.None;

            Set(title, "CO-OP CREW");
            Set(status, StatusText(lobby, mode, busy));
            string lobbyNotice = NoticeText(session.Notice);
            Set(notice, lobbyNotice.Length > 0 ? lobbyNotice : OnlineNoticeText(session.OnlineProblem));

            SetButton(hostButton, canStart);
            SetButton(findButton, canStart);
            SetButton(codeButton, canStart);
            SetButton(hostOnlineButton, canStart);
            SetButton(findOnlineButton, canStart);
            SetButton(leaveButton, mode != LobbyMode.Offline || busy != OnlineStep.None || session.OnlineListShown);

            bool onlineList = mode == LobbyMode.Offline && session.OnlineListShown;
            bool searching = mode == LobbyMode.Searching;
            bool showResults = searching || onlineList;
            bool keypad = mode == LobbyMode.EnteringCode;
            if (resultsPanel != null && resultsPanel.activeSelf != showResults) resultsPanel.SetActive(showResults);
            if (keypadPanel != null && keypadPanel.activeSelf != keypad) keypadPanel.SetActive(keypad);
            if (infoPanel != null && infoPanel.activeSelf != (!showResults && !keypad)) infoPanel.SetActive(!showResults && !keypad);

            if (searching) RefreshResults();
            else if (onlineList) RefreshOnlineResults(busy);
            if (keypad)
            {
                Set(codeEntry, lobby.CodeEntryLine);
                if (fieldCButton != null) fieldCButton.SetLabel(lobby.Field == CodeField.C ? "> C <" : "EDIT C");
                if (fieldDButton != null) fieldDButton.SetLabel(lobby.Field == CodeField.D ? "> D <" : "EDIT D");
            }
            Set(info, InfoText(mode));
        }

        string StatusText(LobbyState lobby, LobbyMode mode, OnlineStep busy)
        {
            string link = session.OnlineLinkStatus;
            switch (busy)
            {
                case OnlineStep.Hosting: return "HOSTING ONLINE...\n" + link;
                case OnlineStep.Finding: return "SEARCHING ONLINE...\n" + link;
                case OnlineStep.Joining: return "JOINING " + m_JoiningLabel + "  ONLINE...\n" + link;
            }
            bool online = session.IsOnline;
            switch (mode)
            {
                case LobbyMode.Hosting:
                    if (online)
                        return "HOSTING  " + lobby.TitleLine + "  ONLINE\nCODE  " + session.OnlineCode + (session.OnlineListed ? "" : "  (NOT LISTED: SHARE THE CODE)") + "\n" + RosterLine();
                    return "HOSTING  " + lobby.TitleLine + "\nCREW CODE  " + lobby.CodeLine + "\n" + RosterLine();
                case LobbyMode.Searching:
                {
                    var hosts = session.FoundHosts;
                    int found = hosts != null ? hosts.Count : 0;
                    string head = session.IsProbing ? "SEARCHING THIS WI-FI..." : found > 0 ? "FOUND " + found + ": PRESS JOIN" : "NOBODY ANSWERED: PRESS FIND CREW AGAIN";
                    return head + "\nTHIS HEADSET  " + lobby.CodeLine;
                }
                case LobbyMode.EnteringCode:
                    return "TYPE THE HOST'S CREW CODE\n(shown on the host's CO-OP CREW panel)\nTHIS HEADSET  " + lobby.CodeLine;
                case LobbyMode.Connecting:
                    if (online) return "JOINING " + m_JoiningLabel + "  ONLINE...\nTHROUGH UNITY RELAY";
                    return "JOINING " + m_JoiningLabel + "...\n" + lobby.Target.A + "." + lobby.Target.B + "." + lobby.Target.C + "." + lobby.Target.D;
                case LobbyMode.Joined:
                    return "JOINED " + m_JoiningLabel + (online ? "  ONLINE" : "") + "\nYOUR SUIT IS " + ColourNames[Mathf.Clamp(session.LocalSlot, 0, 3)] + "\n" + RosterLine()
                        + (session.HostSilent ? "\nHOST NOT RESPONDING..." : "");
                default:
                    if (session.OnlineListShown)
                    {
                        int games = session.OnlineGames.Count;
                        return games > 0 ? "ONLINE: FOUND " + games + ": PRESS JOIN" : "ONLINE: NO GAMES FOUND\nPress FIND ONLINE to look again.";
                    }
                    return "SAME WI-FI: one presses HOST CO-OP, the others\nFIND CREW, then JOIN.  ANYWHERE: one presses\nHOST ONLINE, the others FIND ONLINE, then JOIN.";
            }
        }

        static string InfoText(LobbyMode mode)
        {
            if (mode == LobbyMode.Hosting || mode == LobbyMode.Connecting || mode == LobbyMode.Joined)
                return "Contracts are shared: the host launches one and the\nwhole crew rides along, with one oxygen tank and one\nlocker, and everyone is paid. In this version only the\nhost can carry salvage and there is no creature.\nNothing from your microphone is ever sent.";
            return "Nothing is sent over the network until you press a\nbutton. Wi-Fi buttons reach only your local network.\nONLINE buttons use Unity's servers (Relay, Lobby and\nan anonymous sign-in), and only once pressed.\nNo voice is ever sent.";
        }

        void RefreshOnlineResults(OnlineStep busy)
        {
            var games = session.OnlineGames;
            int rows = resultRows != null ? resultRows.Length : 0;
            for (int i = 0; i < rows; i++)
            {
                bool on = i < games.Count;
                if (resultRows[i] != null && resultRows[i].activeSelf != on) resultRows[i].SetActive(on);
                if (!on) continue;
                OnlineGame game = games[i];
                string why = game.Players >= game.MaxPlayers ? "  FULL" : "";
                if (resultLabels != null && i < resultLabels.Length) Set(resultLabels[i], game.Line + why);
                if (joinButtons != null && i < joinButtons.Length) SetButton(joinButtons[i], game.Joinable && busy == OnlineStep.None);
            }
        }

        void RefreshResults()
        {
            var hosts = session.FoundHosts;
            int shown = hosts != null ? hosts.ShownCount : 0;
            int rows = resultRows != null ? resultRows.Length : 0;
            for (int i = 0; i < rows; i++)
            {
                bool on = i < shown;
                if (resultRows[i] != null && resultRows[i].activeSelf != on) resultRows[i].SetActive(on);
                if (!on) continue;
                HostEntry entry = hosts[i];
                RefuseReason predicted = entry.Predict(NetProtocol.Version, session.ContentHash);
                string why = predicted == RefuseReason.Full ? "  FULL"
                    : predicted == RefuseReason.VersionMismatch || predicted == RefuseReason.ContentMismatch ? "  DIFFERENT VERSION"
                    : "";
                if (resultLabels != null && i < resultLabels.Length) Set(resultLabels[i], entry.Label() + why);
                if (joinButtons != null && i < joinButtons.Length) SetButton(joinButtons[i], true);
            }
        }

        static void SetButton(PushButton button, bool interactable)
        {
            if (button != null) button.SetInteractable(interactable);
        }

        static void Set(TMP_Text text, string value)
        {
            if (text != null && text.text != value) text.text = value;
        }
    }
}

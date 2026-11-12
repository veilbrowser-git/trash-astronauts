using TMPro;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Net;
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
    ///   LEAVE        back to playing alone.
    /// Nothing touches the network until HOST CO-OP, FIND CREW or ENTER CODE is pressed. The panel only drives
    /// <see cref="NetSession"/> and shows its state; every refusal is spelled out in plain words.
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

        [Header("FIND CREW results")]
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
            bool canStart = mode == LobbyMode.Offline || mode == LobbyMode.Searching || mode == LobbyMode.EnteringCode;

            Set(title, "CO-OP CREW");
            Set(status, StatusText(lobby, mode));
            Set(notice, NoticeText(session.Notice));

            SetButton(hostButton, canStart);
            SetButton(findButton, canStart);
            SetButton(codeButton, canStart);
            SetButton(leaveButton, mode != LobbyMode.Offline);

            bool searching = mode == LobbyMode.Searching;
            bool keypad = mode == LobbyMode.EnteringCode;
            if (resultsPanel != null && resultsPanel.activeSelf != searching) resultsPanel.SetActive(searching);
            if (keypadPanel != null && keypadPanel.activeSelf != keypad) keypadPanel.SetActive(keypad);
            if (infoPanel != null && infoPanel.activeSelf != (!searching && !keypad)) infoPanel.SetActive(!searching && !keypad);

            if (searching) RefreshResults();
            if (keypad)
            {
                Set(codeEntry, lobby.CodeEntryLine);
                if (fieldCButton != null) fieldCButton.SetLabel(lobby.Field == CodeField.C ? "> C <" : "EDIT C");
                if (fieldDButton != null) fieldDButton.SetLabel(lobby.Field == CodeField.D ? "> D <" : "EDIT D");
            }
            Set(info, InfoText(mode));
        }

        string StatusText(LobbyState lobby, LobbyMode mode)
        {
            switch (mode)
            {
                case LobbyMode.Hosting:
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
                    return "JOINING " + m_JoiningLabel + "...\n" + lobby.Target.A + "." + lobby.Target.B + "." + lobby.Target.C + "." + lobby.Target.D;
                case LobbyMode.Joined:
                    return "JOINED " + m_JoiningLabel + "\nYOUR SUIT IS " + ColourNames[Mathf.Clamp(session.LocalSlot, 0, 3)] + "\n" + RosterLine()
                        + (session.HostSilent ? "\nHOST NOT RESPONDING..." : "");
                default:
                    return "Play together on the same Wi-Fi, 2 to 4 headsets.\nOne presses HOST CO-OP. The others press\nFIND CREW, then JOIN.";
            }
        }

        static string InfoText(LobbyMode mode)
        {
            if (mode == LobbyMode.Hosting || mode == LobbyMode.Connecting || mode == LobbyMode.Joined)
                return "This first co-op version shows the crew to each other\nin the tug. Contracts are not shared yet: press LEAVE\nto start one. Nothing from your microphone is sent.";
            return "Nothing is sent over the network until you press\nHOST CO-OP, FIND CREW or ENTER CODE. Only headsets\non this Wi-Fi are contacted. No voice is ever sent.";
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

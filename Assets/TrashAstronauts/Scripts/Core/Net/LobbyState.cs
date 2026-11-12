namespace TrashAstronauts.Core.Net
{
    public enum LobbyMode : byte
    {
        /// <summary>Single-player. No socket.</summary>
        Offline = 0,
        /// <summary>HOST CO-OP pressed: socket open on 47800, crew code on the board.</summary>
        Hosting = 1,
        /// <summary>FIND CREW pressed: probing, results listed.</summary>
        Searching = 2,
        /// <summary>ENTER CODE: the keypad is up.</summary>
        EnteringCode = 3,
        /// <summary>JOIN pressed: Hello is out.</summary>
        Connecting = 4,
        /// <summary>In somebody's crew.</summary>
        Joined = 5,
    }

    /// <summary>The one-line message on the lobby board. Local reasons and the host's refusal reasons together.</summary>
    public enum LobbyNotice : byte
    {
        None = 0,
        /// <summary>HOST or JOIN pressed outside Hub / Results.</summary>
        NotInHub = 1,
        /// <summary>Port 47800 could not be bound.</summary>
        PortBusy = 2,
        /// <summary>No Wi-Fi address.</summary>
        NoNetwork = 3,
        /// <summary>10 s of searching found nobody (router client isolation is the usual cause).</summary>
        NobodyFound = 4,
        Full = 5,
        VersionMismatch = 6,
        ContentMismatch = 7,
        ContractInProgress = 8,
        /// <summary>No answer to Hello within 5 s.</summary>
        NoAnswer = 9,
        HostClosed = 10,
        /// <summary>60 s without the host, or too many of our requests piled up while it was unreachable.</summary>
        LinkLost = 11,
        /// <summary>The host dropped us after a long silence.</summary>
        Dropped = 12,
        /// <summary>The keypad code is not a usable address.</summary>
        BadCode = 13,
    }

    public enum CodeField : byte
    {
        D = 0,
        C = 1,
    }

    /// <summary>
    /// The state machine behind the tug's NetLobbyBoard: HOST CO-OP, FIND CREW, ENTER CODE, JOIN, LEAVE, and why
    /// something was refused. Pure: NetLobbyBoard presses its buttons into this and NetSession reports results
    /// into it; it opens no socket itself. Every transition method returns whether the press was accepted, and a
    /// refused press sets <see cref="Notice"/>.
    /// </summary>
    public sealed class LobbyState
    {
        public const double NobodyHintSeconds = 10.0;

        double m_SearchStarted;
        double m_EmptySearchSeconds;
        int m_CodeC, m_CodeD;
        int m_DigitsC, m_DigitsD;

        public LobbyMode Mode { get; private set; }
        public LobbyNotice Notice { get; private set; }
        public uint LocalAddress { get; private set; }
        public ushort TugNumber { get; private set; }
        /// <summary>Who we are joining or have joined.</summary>
        public NetEndpoint Target { get; private set; }
        /// <summary>Our slot once joined (0 while hosting or offline).</summary>
        public byte LocalSlot { get; private set; }
        /// <summary>Players in the session including us (1 while alone).</summary>
        public int PlayerCount { get; private set; } = 1;
        public CodeField Field { get; private set; }
        public int CodeC => m_CodeC;
        public int CodeD => m_CodeD;
        /// <summary>
        /// Stage 1b rule: while a session is open no contract may start. Stage 2 sets this true.
        /// </summary>
        public bool ContractsEnabled { get; set; }

        /// <summary>True while a socket is (or should be) open: hosting, connecting or joined.</summary>
        public bool SessionOpen => Mode == LobbyMode.Hosting || Mode == LobbyMode.Connecting || Mode == LobbyMode.Joined;
        /// <summary>True when the mission board must refuse START because a session is open and contracts are not enabled yet.</summary>
        public bool BlocksContractStart => SessionOpen && !ContractsEnabled;

        /// <summary>HOST and JOIN are only allowed from the hub or the results screen.</summary>
        public static bool PhaseAllowsSession(MissionPhase phase) => phase == MissionPhase.Hub || phase == MissionPhase.Results;

        bool Refuse(LobbyNotice notice)
        {
            Notice = notice;
            return false;
        }

        /// <summary>
        /// HOST CO-OP. <paramref name="socketOpened"/> is whether port 47800 could be bound (call
        /// <see cref="CanHost"/> first, then open the socket, then this). False with NotInHub, PortBusy or NoNetwork.
        /// </summary>
        public bool Host(MissionPhase localPhase, bool socketOpened, uint localAddress, ushort tugNumber)
        {
            if (Mode != LobbyMode.Offline && Mode != LobbyMode.Searching && Mode != LobbyMode.EnteringCode) return false;
            if (!PhaseAllowsSession(localPhase)) return Refuse(LobbyNotice.NotInHub);
            if (!socketOpened) return Refuse(LobbyNotice.PortBusy);
            if (localAddress == 0u) return Refuse(LobbyNotice.NoNetwork);
            Mode = LobbyMode.Hosting;
            Notice = LobbyNotice.None;
            LocalAddress = localAddress;
            TugNumber = tugNumber;
            LocalSlot = 0;
            PlayerCount = 1;
            return true;
        }

        /// <summary>True when a HOST press would get as far as opening the socket.</summary>
        public bool CanHost(MissionPhase localPhase)
        {
            return (Mode == LobbyMode.Offline || Mode == LobbyMode.Searching || Mode == LobbyMode.EnteringCode) && PhaseAllowsSession(localPhase);
        }

        /// <summary>FIND CREW. Allowed when offline (or to search again). False with NotInHub.</summary>
        public bool Find(MissionPhase localPhase, uint localAddress, double now)
        {
            if (Mode != LobbyMode.Offline && Mode != LobbyMode.Searching && Mode != LobbyMode.EnteringCode) return false;
            if (!PhaseAllowsSession(localPhase)) return Refuse(LobbyNotice.NotInHub);
            if (Mode == LobbyMode.Searching) m_EmptySearchSeconds += now - m_SearchStarted > 0 ? now - m_SearchStarted : 0;
            Mode = LobbyMode.Searching;
            if (Notice != LobbyNotice.NobodyFound) Notice = LobbyNotice.None;
            LocalAddress = localAddress;
            m_SearchStarted = now;
            return true;
        }

        /// <summary>
        /// Call every frame while searching with the number of hosts listed. Raises NobodyFound after 10 s of
        /// searching (added up over repeated FIND presses) with an empty list; finding anyone clears it.
        /// </summary>
        public void SearchTick(double now, int hostCount)
        {
            if (Mode != LobbyMode.Searching) return;
            if (hostCount > 0)
            {
                m_EmptySearchSeconds = 0;
                m_SearchStarted = now;
                if (Notice == LobbyNotice.NobodyFound) Notice = LobbyNotice.None;
                return;
            }
            if (m_EmptySearchSeconds + (now - m_SearchStarted) >= NobodyHintSeconds) Notice = LobbyNotice.NobodyFound;
        }

        /// <summary>ENTER CODE: opens the keypad with C prefilled from our own third octet and D empty.</summary>
        public bool EnterCode(MissionPhase localPhase, uint localAddress)
        {
            if (Mode != LobbyMode.Offline && Mode != LobbyMode.Searching && Mode != LobbyMode.EnteringCode) return false;
            if (!PhaseAllowsSession(localPhase)) return Refuse(LobbyNotice.NotInHub);
            if (localAddress == 0u) return Refuse(LobbyNotice.NoNetwork);
            Mode = LobbyMode.EnteringCode;
            Notice = LobbyNotice.None;
            LocalAddress = localAddress;
            m_CodeC = JoinCode.C(localAddress);
            m_DigitsC = 3;
            m_CodeD = 0;
            m_DigitsD = 0;
            Field = CodeField.D;
            return true;
        }

        /// <summary>Chooses which half of the code the digits go to. Selecting a field clears it.</summary>
        public bool SelectField(CodeField field)
        {
            if (Mode != LobbyMode.EnteringCode) return false;
            Field = field;
            if (field == CodeField.C) { m_CodeC = 0; m_DigitsC = 0; }
            else { m_CodeD = 0; m_DigitsD = 0; }
            return true;
        }

        /// <summary>A keypad digit 0..9. A fourth digit starts the field over. False when it would exceed 255.</summary>
        public bool PressDigit(int digit)
        {
            if (Mode != LobbyMode.EnteringCode || digit < 0 || digit > 9) return false;
            if (Field == CodeField.C) return AddDigit(ref m_CodeC, ref m_DigitsC, digit);
            return AddDigit(ref m_CodeD, ref m_DigitsD, digit);
        }

        bool AddDigit(ref int value, ref int digits, int digit)
        {
            if (digits >= 3) { value = 0; digits = 0; }
            int next = value * 10 + digit;
            if (next > 255) return Refuse(LobbyNotice.BadCode);
            value = next;
            digits++;
            if (Notice == LobbyNotice.BadCode) Notice = LobbyNotice.None;
            return true;
        }

        /// <summary>Removes the last digit of the selected field.</summary>
        public bool PressBack()
        {
            if (Mode != LobbyMode.EnteringCode) return false;
            if (Field == CodeField.C) { if (m_DigitsC == 0) return false; m_CodeC /= 10; m_DigitsC--; }
            else { if (m_DigitsD == 0) return false; m_CodeD /= 10; m_DigitsD--; }
            return true;
        }

        /// <summary>The address the keypad currently spells (our first two octets + C.D), if it is usable.</summary>
        public bool TryGetCodeTarget(ushort port, out NetEndpoint target)
        {
            target = default(NetEndpoint);
            if (Mode != LobbyMode.EnteringCode || m_DigitsC == 0 || m_DigitsD == 0) return false;
            if (!JoinCode.TryToEndpoint(LocalAddress, m_CodeC, m_CodeD, port, out target)) return false;
            return target.address != LocalAddress;
        }

        /// <summary>"C 001  D 04_" for the keypad display.</summary>
        public string CodeEntryLine => "C " + Shown(m_CodeC, m_DigitsC) + "  D " + Shown(m_CodeD, m_DigitsD);

        static string Shown(int value, int digits)
        {
            if (digits <= 0) return "___";
            string s = value.ToString();
            while (s.Length < digits) s = "0" + s;
            while (s.Length < 3) s += "_";
            return s;
        }

        /// <summary>JOIN on a search result or on the keypad's address. False with NotInHub, or BadCode for an empty target.</summary>
        public bool Join(MissionPhase localPhase, NetEndpoint host)
        {
            if (Mode != LobbyMode.Searching && Mode != LobbyMode.EnteringCode && Mode != LobbyMode.Offline) return false;
            if (!PhaseAllowsSession(localPhase)) return Refuse(LobbyNotice.NotInHub);
            if (host.IsNone || host.port == 0) return Refuse(LobbyNotice.BadCode);
            Mode = LobbyMode.Connecting;
            Notice = LobbyNotice.None;
            Target = host;
            return true;
        }

        /// <summary>Welcome arrived: we are in.</summary>
        public void OnWelcome(byte localSlot, int playerCount)
        {
            if (Mode != LobbyMode.Connecting && Mode != LobbyMode.Joined) return;
            Mode = LobbyMode.Joined;
            Notice = LobbyNotice.None;
            LocalSlot = localSlot;
            PlayerCount = playerCount < 1 ? 1 : playerCount;
            m_EmptySearchSeconds = 0;
        }

        /// <summary>The roster changed (hosting or joined).</summary>
        public void OnRoster(int playerCount)
        {
            if (Mode == LobbyMode.Hosting || Mode == LobbyMode.Joined) PlayerCount = playerCount < 1 ? 1 : playerCount;
        }

        /// <summary>The client session ended for a reason other than our own LEAVE. Back to Offline with the matching notice.</summary>
        public void OnSessionEnded(SessionEndReason reason, RefuseReason refusal)
        {
            if (Mode != LobbyMode.Connecting && Mode != LobbyMode.Joined) return;
            Reset();
            Notice = NoticeFor(reason, refusal);
        }

        /// <summary>LEAVE (or closing the keypad / search). Always ends Offline with no notice.</summary>
        public void Leave()
        {
            Reset();
            Notice = LobbyNotice.None;
        }

        void Reset()
        {
            Mode = LobbyMode.Offline;
            LocalSlot = 0;
            PlayerCount = 1;
            Target = default(NetEndpoint);
            TugNumber = 0;
        }

        public static LobbyNotice NoticeFor(SessionEndReason reason, RefuseReason refusal)
        {
            switch (reason)
            {
                case SessionEndReason.Refused:
                    switch (refusal)
                    {
                        case RefuseReason.Full: return LobbyNotice.Full;
                        case RefuseReason.VersionMismatch: return LobbyNotice.VersionMismatch;
                        case RefuseReason.ContentMismatch: return LobbyNotice.ContentMismatch;
                        case RefuseReason.ContractInProgress: return LobbyNotice.ContractInProgress;
                        default: return LobbyNotice.NoAnswer;
                    }
                case SessionEndReason.ConnectTimeout: return LobbyNotice.NoAnswer;
                case SessionEndReason.HostClosed: return LobbyNotice.HostClosed;
                case SessionEndReason.Dropped: return LobbyNotice.Dropped;
                case SessionEndReason.HostTimeout: return LobbyNotice.LinkLost;
                case SessionEndReason.BacklogOverflow: return LobbyNotice.LinkLost;
                default: return LobbyNotice.None;
            }
        }

        /// <summary>Board wording for a notice (upper case, fits one line of the panel).</summary>
        public static string NoticeText(LobbyNotice notice)
        {
            switch (notice)
            {
                case LobbyNotice.NotInHub: return "FINISH THE CONTRACT FIRST";
                case LobbyNotice.PortBusy: return "PORT BUSY";
                case LobbyNotice.NoNetwork: return "NO WI-FI";
                case LobbyNotice.NobodyFound: return "NOBODY FOUND - SAME WI-FI? TRY ENTER CODE";
                case LobbyNotice.Full: return "CREW FULL";
                case LobbyNotice.VersionMismatch: return "DIFFERENT GAME VERSION";
                case LobbyNotice.ContentMismatch: return "DIFFERENT BUILD";
                case LobbyNotice.ContractInProgress: return "CREW IS ON A CONTRACT";
                case LobbyNotice.NoAnswer: return "NO ANSWER";
                case LobbyNotice.HostClosed: return "HOST LEFT";
                case LobbyNotice.LinkLost: return "LINK LOST";
                case LobbyNotice.Dropped: return "DROPPED BY HOST";
                case LobbyNotice.BadCode: return "BAD CODE";
                default: return "";
            }
        }

        public string NoticeLine => NoticeText(Notice);
        /// <summary>"TUG 4821" while hosting.</summary>
        public string TitleLine => Mode == LobbyMode.Hosting ? "TUG " + TugNumber : "";
        /// <summary>"C 001  D 042": our own crew code (shown while hosting; also the device self-check).</summary>
        public string CodeLine => LocalAddress != 0u ? JoinCode.Format(LocalAddress) : "";
        /// <summary>"CREW 2/4".</summary>
        public string RosterLine => SessionOpen ? "CREW " + PlayerCount + "/" + NetProtocol.MaxPlayers : "";
    }
}

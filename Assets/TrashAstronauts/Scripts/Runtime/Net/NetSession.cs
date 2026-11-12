using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net.Online;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>What the online half of the crew panel is waiting for.</summary>
    public enum OnlineStep : byte
    {
        None = 0,
        /// <summary>HOST ONLINE pressed: signing in, Relay allocation, lobby listing.</summary>
        Hosting = 1,
        /// <summary>FIND ONLINE pressed: signing in, reading the lobby list.</summary>
        Finding = 2,
        /// <summary>JOIN on an online game: reading its code, connecting through Relay.</summary>
        Joining = 3,
    }

    /// <summary>
    /// The one co-op MonoBehaviour, on the Mission object. Offline by default: it opens no socket, contacts no
    /// service and starts no timer or buffer until the crew panel (NetLobbyBoard) or a test calls one of the
    /// panel actions (<see cref="Host"/>, <see cref="Find"/>, <see cref="EnterCode"/>, <see cref="Join"/>,
    /// <see cref="HostOnline"/>, <see cref="FindOnline"/>, <see cref="JoinOnline"/>). Single-player never gets
    /// further than that.
    ///
    /// While a session is open it owns the pure C# session (NetSessionHost or NetSessionClient) and its
    /// transport: a UDP socket on the same Wi-Fi, or a Unity Relay connection for an online game (the session
    /// code is the same for both). It pumps the session (receive in Update, the 20 Hz send tick in LateUpdate
    /// after the rig has been posed), implements <see cref="IMissionNet"/> for the game, and keeps one
    /// RemoteAvatar per other crew member.
    ///
    /// The shared contract (co-op stage 2, "ride-along"): on the host a <see cref="CoopHostRules"/> decides every
    /// crew request with the game's own rules and its broadcasts go out through the host session; the send tick
    /// carries the mission clock (2 Hz) and the poses of the salvage the host simulates. On a client a
    /// <see cref="CoopClientApply"/> applies what the host decided. Only the host carries salvage, and a co-op
    /// contract has no creature, blackouts or webs yet.
    ///
    /// Nothing here reads the microphone, and no message type can carry audio, recorded phrases or profile data.
    /// </summary>
    [DefaultExecutionOrder(300)] // LateUpdate after XRI, hand tracking, the gloves and the suit have posed the rig
    public sealed class NetSession : MonoBehaviour, IMissionNet
    {
        /// <summary>
        /// Test seam: opens a transport. The argument is the port to bind (0 = any free port). Return null when the
        /// bind fails (the board then says PORT BUSY). Null = real UDP sockets on every interface.
        /// </summary>
        public static Func<ushort, INetTransport> TransportFactory;
        /// <summary>Test seam: the session clock in seconds. Null = real time.</summary>
        public static Func<double> ClockOverride;
        /// <summary>Test seam: this device's LAN address given the transport just opened (0 = unknown). Null = ask the OS.</summary>
        public static Func<INetTransport, uint> LocalAddressOverride;
        /// <summary>
        /// Test seam: makes the online link the first time an online button is pressed. Null = the real
        /// <see cref="OnlineLink"/> (Unity Relay + Lobby + anonymous sign-in on <see cref="OnlineServices.Shared"/>).
        /// </summary>
        public static Func<IOnlineLink> OnlineLinkFactory;

        /// <summary>Seconds a closed host keeps answering stragglers with "the host left" before its socket closes.</summary>
        public const double HostLingerSeconds = 1.5;
        /// <summary>The mission clock and shared tank go to the crew this often.</summary>
        public const double ClockInterval = 0.5;
        /// <summary>
        /// How far in the past crew suits and streamed salvage are drawn over an online (Relay) link, where the
        /// delay varies far more than on Wi-Fi (Wi-Fi uses SnapshotBuffer.InterpolationDelay, 100 ms).
        /// </summary>
        public const double OnlineInterpolationDelay = 0.19;

        public MissionDirector director;
        [Tooltip("Stand-in for another crew member (Builder.Net builds it).")]
        public RemoteAvatar remoteAvatarPrefab;

        static readonly List<NetSession> s_Live = new List<NetSession>();

        readonly LobbyState m_Lobby = new LobbyState { ContractsEnabled = true };
        readonly RemoteAvatar[] m_Crew = new RemoteAvatar[NetProtocol.MaxPlayers];
        readonly bool[] m_Present = new bool[NetProtocol.MaxPlayers];
        readonly bool[] m_RemoveAfterScare = new bool[NetProtocol.MaxPlayers];

        NetSessionHost m_Host;
        NetSessionClient m_Client;
        NetSessionHost m_Lingering;
        double m_LingerUntil;
        INetTransport m_SearchTransport;
        Discovery m_Discovery;
        AvatarSnapshot m_LocalAvatar = AvatarSnapshot.Default;
        int m_LocalSlot;
        bool m_ClientEnded;
        SessionEndReason m_ClientEndReason;
        LobbyNotice m_OwnNotice;
        bool m_AppPaused;
        bool m_ReportedAway;
        bool m_LocalAway;
        readonly List<Task> m_Abandoned = new List<Task>();

        // the shared contract
        CoopHostRules m_Rules;
        CoopClientApply m_Apply;
        readonly ItemPoseEntry[] m_Poses = new ItemPoseEntry[CoopItemSampler.MaxMovingPerTick + 1];
        double m_LastPoseTick = double.NegativeInfinity;
        double m_NextClock;

        // online
        IOnlineLink m_Link;
        Task<OnlineHostResult> m_HostTask;
        Task<OnlineFindResult> m_FindTask;
        Task<OnlineJoinResult> m_JoinTask;
        readonly List<OnlineGame> m_OnlineGames = new List<OnlineGame>(OnlineConfig.MaxListed);
        bool m_OnlineListShown;
        bool m_Online;
        OnlineProblem m_OnlineProblem;
        string m_OnlineCode;
        int m_ReportedCount = -1;
        MissionPhase m_ReportedPhase;

        // ================================================================== state for the board and tests

        /// <summary>The lobby state machine behind the crew panel (same-Wi-Fi and online sessions alike).</summary>
        public LobbyState Lobby => m_Lobby;
        public LobbyMode Mode => m_Lobby.Mode;
        /// <summary>True when no socket or Relay connection of any kind is open and no online step is running (single-player).</summary>
        public bool IsOffline => m_Host == null && m_Client == null && m_SearchTransport == null && m_Lingering == null && OnlineBusy == OnlineStep.None;
        /// <summary>Hosts answering FIND CREW (null before the first search).</summary>
        public HostList FoundHosts => m_Discovery != null ? m_Discovery.Hosts : null;
        /// <summary>True for the 3 s a FIND CREW search is probing.</summary>
        public bool IsProbing => m_Discovery != null && m_Discovery.IsSearching && m_SearchTransport != null;
        /// <summary>A joined client has heard nothing from the host for 1.5 s.</summary>
        public bool HostSilent { get; internal set; }
        /// <summary>The notice to show: this component's own when it has one (a socket that would not open, no Wi-Fi address), else the lobby's.</summary>
        public LobbyNotice Notice => m_OwnNotice != LobbyNotice.None ? m_OwnNotice : m_Lobby.Notice;
        /// <summary>The fingerprint peers compare before they agree to play together.</summary>
        public uint ContentHash => director != null ? director.NetIds.ContentHash(NetProtocol.Version) : 0u;
        /// <summary>Players in the session, this one included (1 while alone or offline).</summary>
        public int PlayerCount
        {
            get
            {
                if (m_Host == null && (m_Client == null || !m_Client.Welcomed)) return 1;
                int n = 0;
                for (int i = 0; i < m_Present.Length; i++) if (m_Present[i]) n++;
                return n < 1 ? 1 : n;
            }
        }
        public bool IsPresent(int slot) => slot >= 0 && slot < m_Present.Length && m_Present[slot] && (m_Host != null || (m_Client != null && m_Client.Welcomed));
        /// <summary>The stand-in for a crew slot, or null.</summary>
        public RemoteAvatar Crew(int slot) => slot >= 0 && slot < m_Crew.Length ? m_Crew[slot] : null;
        public NetSessionHost HostSession => m_Host;
        public NetSessionClient ClientSession => m_Client;
        /// <summary>The host's contract rules while hosting (null otherwise).</summary>
        public CoopHostRules HostRules => m_Rules;
        /// <summary>The transport currently in use (host, client or search), for tests.</summary>
        public INetTransport Transport => m_Host != null ? m_Host.Transport : m_Client != null ? m_Client.Transport : m_SearchTransport;

        /// <summary>The online step in flight (None when nothing is being waited for).</summary>
        public OnlineStep OnlineBusy => m_HostTask != null ? OnlineStep.Hosting : m_JoinTask != null ? OnlineStep.Joining : m_FindTask != null ? OnlineStep.Finding : OnlineStep.None;
        /// <summary>True while the session (hosting or joined) runs over the online link instead of the local Wi-Fi.</summary>
        public bool IsOnline => m_Online && (m_Host != null || m_Client != null);
        /// <summary>True after FIND ONLINE until LEAVE or another choice: the panel lists <see cref="OnlineGames"/>.</summary>
        public bool OnlineListShown => m_OnlineListShown;
        /// <summary>The last FIND ONLINE answer: at most 5 games of this build, newest first.</summary>
        public IReadOnlyList<OnlineGame> OnlineGames => m_OnlineGames;
        /// <summary>Why the last online step did not work (None when it did, or none ran).</summary>
        public OnlineProblem OnlineProblem => m_OnlineProblem;
        /// <summary>Hosting online: the 6-character Relay code a friend can type.</summary>
        public string OnlineCode => m_OnlineCode;
        /// <summary>Hosting online: the game is in the lobby list right now (read live from the link; false: only the code works).</summary>
        public bool OnlineListed => m_Online && m_Host != null && m_Link != null && m_Link.IsListed;
        /// <summary>
        /// An online step abandoned by LEAVE is still winding down (its service calls time out after up to 20 s).
        /// The online buttons stay off until it has, so the next press is not answered "still working".
        /// </summary>
        public bool OnlineSettling
        {
            get
            {
                for (int i = m_Abandoned.Count - 1; i >= 0; i--)
                    if (m_Abandoned[i] == null || m_Abandoned[i].IsCompleted) m_Abandoned.RemoveAt(i);
                return m_Abandoned.Count > 0;
            }
        }
        /// <summary>How far in the past the crew's suits and streamed salvage are drawn on this link.</summary>
        public double InterpolationDelay => m_Online ? OnlineInterpolationDelay : SnapshotBuffer<AvatarSnapshot>.InterpolationDelay;
        /// <summary>The online link's own one-line status, or "" before any online button was pressed.</summary>
        public string OnlineLinkStatus => m_Link != null ? m_Link.StatusText : "";
        /// <summary>True once an online button was pressed in this scene (the link exists).</summary>
        public bool HasOnlineLink => m_Link != null;

        /// <summary>The session clock.</summary>
        public static double Now => ClockOverride != null ? ClockOverride() : Time.realtimeSinceStartupAsDouble;

        // ================================================================== IMissionNet

        public bool IsClient => m_Client != null;
        public bool IsHost => m_Host != null;
        public int LocalSlot => m_LocalSlot;

        /// <summary>START on a client's mission board: the host decides, and answers with RunStart for everyone.</summary>
        public void RequestStart()
        {
            if (m_Client == null || !m_Client.Welcomed) return;
            m_Client.SendStartRequest();
            FlushClient();
        }

        /// <summary>EXTRACT (confirmed) on a client: the host checks that everyone is aboard.</summary>
        public void RequestExtract()
        {
            if (m_Client == null || !m_Client.Welcomed) return;
            m_Client.SendExtractRequest();
            FlushClient();
        }

        public void RequestDoor(SlidingDoor door, bool desiredOpen)
        {
            if (m_Client == null || !m_Client.Welcomed || door == null || director == null) return;
            int id = director.NetIds.Doors.IndexOf(door.doorId);
            if (id < 0 || id > 255) return;
            m_Client.SendDoorRequest((byte)id, desiredOpen);
            FlushClient();
        }

        public void ReportPointCut(Cuttable panel, int pointIndex)
        {
            if (m_Client == null || !m_Client.Welcomed || panel == null || director == null) return;
            int id = director.NetIds.Panels.IndexOf(panel.panelId);
            if (id < 0 || id > 255 || pointIndex < 0 || pointIndex > 255) return;
            m_Client.SendPointCutReport((byte)id, (byte)pointIndex);
            FlushClient();
        }

        // Stage 3: a client never simulates salvage in this version (every item is HOST ONLY), so it has no damage to report.
        public void ReportSalvageDamage(SalvageItem item, int newValue) { }
        // Stage 4: webs are never armed in a co-op contract, so a client has none to report.
        public void ReportWire(WebTripwire wire, WireChange change) { }

        /// <summary>The local player's dart hit the host's creature on this screen: the host checks it and sedates it.</summary>
        public void ReportTranqHit(Vector3 origin, Vector3 hitPoint)
        {
            if (m_Client == null || !m_Client.Welcomed) return;
            m_Client.SendTranqHitReport(CoopConvert.ToNet(origin), CoopConvert.ToNet(hitPoint));
            FlushClient();
        }

        /// <summary>A gameplay noise the local player made: the creature lives on the host, so it is heard there (Noise stream).</summary>
        public void ForwardNoise(in NoiseEvent noise)
        {
            if (m_Client == null || !m_Client.Welcomed) return;
            m_Client.SendNoise(CoopConvert.ToNet(noise.Position), noise.Loudness, noise.Kind);
        }

        /// <summary>The director's own Away state changed (headset off, tracking or focus lost: MissionDirector.SetLocalAway).</summary>
        public void ReportPause(bool paused)
        {
            m_ReportedAway = paused;
            RefreshAway();
        }

        /// <summary>
        /// Away = the app is paused OR the director says this player is away (a client: headset off, tracking or
        /// focus lost) OR this headset's own hazard pause is on. The sources are kept apart and combined here, so
        /// resuming the app while tracking is still lost does not report "back".
        /// The host's pause reaching a client is not this headset going away. A client tells the host
        /// (PauseNotice). A host shows it through the Away bit of its own avatar; its hazard pause also goes to
        /// the crew as HostPause (CoopHostRules).
        /// </summary>
        void RefreshAway()
        {
            m_LocalAway = m_AppPaused || m_ReportedAway || (director != null && director.PausedHere);
            if (m_Client != null)
            {
                m_Client.SetAway(m_LocalAway, Now);
                // The app may be about to be suspended: put the notice on the wire now, not at the next update.
                FlushRelay(m_Client.Transport);
            }
        }

        /// <summary>True while this headset counts as away (app paused or hazard pause on).</summary>
        public bool LocalAway => m_LocalAway;

        public void ReportSettingsChanged(Handedness handedness)
        {
            byte h = handedness == Handedness.Left ? (byte)1 : (byte)0;
            if (m_Client != null) m_Client.SendSettingsChanged(h);
            if (m_Host != null) m_Host.SetLocalHandedness(h);
        }

        // ================================================================== lifecycle

        void Awake()
        {
            if (director == null) director = GetComponent<MissionDirector>();
            s_Live.Add(this);
        }

        void OnEnable()
        {
            if (director != null) director.HazardPauseChanged += OnHazardPauseChanged;
        }

        void OnDisable()
        {
            if (director != null) director.HazardPauseChanged -= OnHazardPauseChanged;
        }

        void OnDestroy()
        {
            s_Live.Remove(this);
            // Best-effort goodbye, then every socket closed and every online step cancelled. The director may be going away too.
            double now = Now;
            if (m_Client != null) m_Client.Leave(now);
            if (m_Host != null) m_Host.Close(now);
            ShutDown();
            if (director != null && ReferenceEquals(director.Net, this))
            {
                director.Net = null;
                director.ContractStartBlocked = false;
            }
        }

        void OnApplicationPause(bool paused)
        {
            // Sent at once: it may be the last thing this headset gets to say for a while.
            m_AppPaused = paused;
            RefreshAway();
        }

        void OnHazardPauseChanged(bool paused)
        {
            // Headset off or tracking lost: the crew sees this player as away (and stops counting them).
            RefreshAway();
        }

        /// <summary>
        /// Domain reload is off in this project: a session left over from an earlier play-mode run must not keep a
        /// socket bound, and the test seams must not leak into the next run.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            for (int i = s_Live.Count - 1; i >= 0; i--)
            {
                if (s_Live[i] != null) s_Live[i].ShutDown();
            }
            s_Live.Clear();
            TransportFactory = null;
            ClockOverride = null;
            LocalAddressOverride = null;
            OnlineLinkFactory = null;
            CoopItemReplica.Delay = SnapshotBuffer<PoseSnapshot>.InterpolationDelay;
        }

        /// <summary>
        /// Closes every socket and Relay connection at once and cancels any online step, with no goodbye and no
        /// change to the game beyond ending the host's contract rules. For OnDestroy and test tear-down.
        /// </summary>
        public void ShutDown()
        {
            CancelOnline();
            DisposeContract();
            if (m_Client != null) { m_Client.Dispose(); m_Client = null; }
            if (m_Host != null) { m_Host.Dispose(); m_Host = null; }
            if (m_Lingering != null) { m_Lingering.Dispose(); m_Lingering = null; }
            CloseSearch();
        }

        // ================================================================== crew panel actions: same Wi-Fi

        /// <summary>HOST CO-OP: binds port 47800 and waits for crew. Only from the hub or the results screen.</summary>
        public bool Host()
        {
            m_OwnNotice = LobbyNotice.None;
            if (OnlineBusy != OnlineStep.None) return false;
            MissionPhase phase = director.Phase;
            if (!m_Lobby.CanHost(phase)) return m_Lobby.Host(phase, false, 0u, 0); // NotInHub, or not from this mode
            DisposeLingering();

            // Bind first: if it fails, a search or keypad that was open stays exactly as it was, with the reason.
            INetTransport transport = Open(NetProtocol.HostPort);
            if (transport == null)
            {
                m_OwnNotice = LobbyNotice.PortBusy;
                return false;
            }
            uint address = LocalAddress(transport);
            if (address == 0u)
            {
                transport.Dispose();
                m_OwnNotice = LobbyNotice.NoNetwork;
                return false;
            }
            ushort tug = JoinCode.MakeTugNumber(NewSeed());
            CloseSearch();
            if (!m_Lobby.Host(phase, true, address, tug))
            {
                transport.Dispose();
                Sync();
                return false;
            }
            ForgetOnline();
            StartHosting(transport, tug, false);
            return true;
        }

        /// <summary>FIND CREW: probes the local network for 3 s (broadcast plus a unicast sweep of the /24).</summary>
        public bool Find()
        {
            m_OwnNotice = LobbyNotice.None;
            if (OnlineBusy != OnlineStep.None) return false;
            MissionPhase phase = director.Phase;
            if (!LobbyState.PhaseAllowsSession(phase)) return m_Lobby.Find(phase, 0u, Now);
            if (m_Lobby.Mode != LobbyMode.Offline && m_Lobby.Mode != LobbyMode.Searching && m_Lobby.Mode != LobbyMode.EnteringCode) return false;
            bool wasOffline = m_SearchTransport == null;
            if (!EnsureSearchTransport()) return false;
            double now = Now;
            uint address = LocalAddress(m_SearchTransport);
            if (address == 0u)
            {
                // No Wi-Fi address: neither the subnet broadcast nor the sweep can be aimed. Say so; open nothing.
                if (m_Discovery != null) m_Discovery.Stop();
                if (wasOffline || m_Lobby.Mode == LobbyMode.Searching)
                {
                    m_Lobby.Leave();
                    CloseSearch();
                }
                m_OwnNotice = LobbyNotice.NoNetwork;
                Sync();
                return false;
            }
            bool newSearch = m_Lobby.Mode != LobbyMode.Searching;
            if (!m_Lobby.Find(phase, address, now)) return false;
            ForgetOnline();
            if (m_Discovery == null) m_Discovery = new Discovery(NetProtocol.Version, NewSeed());
            // Results of an earlier search are not joinable any more: only tugs that answer this one are listed.
            if (newSearch) m_Discovery.Hosts.Clear();
            m_Discovery.Start(address, now, NetProtocol.HostPort);
            Sync();
            return true;
        }

        /// <summary>ENTER CODE: the keypad, with C prefilled from this headset's own address.</summary>
        public bool EnterCode()
        {
            m_OwnNotice = LobbyNotice.None;
            if (OnlineBusy != OnlineStep.None) return false;
            MissionPhase phase = director.Phase;
            if (!LobbyState.PhaseAllowsSession(phase)) return m_Lobby.EnterCode(phase, 0u);
            if (m_Lobby.Mode != LobbyMode.Offline && m_Lobby.Mode != LobbyMode.Searching && m_Lobby.Mode != LobbyMode.EnteringCode) return false;
            bool wasOffline = m_SearchTransport == null;
            if (!EnsureSearchTransport()) return false;
            if (!m_Lobby.EnterCode(phase, LocalAddress(m_SearchTransport)))
            {
                // NO WI-FI: do not leave a socket open behind an Offline panel.
                if (wasOffline && m_Lobby.Mode == LobbyMode.Offline) CloseSearch();
                return false;
            }
            ForgetOnline();
            if (m_Discovery != null) m_Discovery.Stop();
            Sync();
            return true;
        }

        public bool SelectCodeField(CodeField field) => m_Lobby.SelectField(field);
        public bool PressDigit(int digit) => m_Lobby.PressDigit(digit);
        public bool PressBack() => m_Lobby.PressBack();

        /// <summary>JOIN on the keypad: the host at our first two octets plus C.D.</summary>
        public bool JoinByCode()
        {
            if (m_Lobby.Mode != LobbyMode.EnteringCode) return false;
            if (!m_Lobby.TryGetCodeTarget(NetProtocol.HostPort, out NetEndpoint target)) return m_Lobby.Join(director.Phase, default(NetEndpoint)); // BAD CODE
            return Join(target);
        }

        /// <summary>JOIN next to a FIND CREW result (0 = the top line).</summary>
        public bool JoinFound(int index)
        {
            HostList hosts = FoundHosts;
            if (hosts == null || index < 0 || index >= hosts.ShownCount) return false;
            return Join(hosts[index].endpoint);
        }

        /// <summary>Starts joining a host on the local network. The game becomes part of the session when its Welcome arrives.</summary>
        public bool Join(NetEndpoint host)
        {
            m_OwnNotice = LobbyNotice.None;
            if (m_Host != null || m_Client != null || OnlineBusy != OnlineStep.None) return false;
            if (!m_Lobby.Join(director.Phase, host)) return false;
            if (m_Discovery != null) m_Discovery.Stop();
            INetTransport transport = m_SearchTransport ?? Open(0);
            m_SearchTransport = null;
            if (transport == null)
            {
                m_Lobby.OnSessionEnded(SessionEndReason.ConnectTimeout, RefuseReason.None);
                m_OwnNotice = LobbyNotice.NoNetwork;
                Sync();
                return false;
            }
            ForgetOnline();
            StartClient(transport, host, false);
            return true;
        }

        // ================================================================== crew panel actions: online

        /// <summary>
        /// HOST ONLINE: signs in anonymously, opens a Relay allocation and lists the game as "TUG n" in the lobby,
        /// all in the background. When the link is ready the same host session as on Wi-Fi runs over it. Only from
        /// the hub or the results screen. False if the press was refused (the notice says why).
        /// </summary>
        public bool HostOnline()
        {
            m_OwnNotice = LobbyNotice.None;
            if (OnlineBusy != OnlineStep.None || m_Host != null || m_Client != null || OnlineSettling) return false;
            MissionPhase phase = director.Phase;
            if (!m_Lobby.CanHost(phase)) return m_Lobby.Host(phase, false, 0u, 0); // NotInHub, or not from this mode
            DisposeLingering();
            LeaveLocalSearch();
            if (OnlineSettling) return false;
            ForgetOnline();
            ushort tug = JoinCode.MakeTugNumber(NewSeed());
            m_HostTask = Link.HostAsync(tug, VersionKey, phase, 1);
            Sync();
            PollOnline();
            return true;
        }

        /// <summary>FIND ONLINE: lists up to 5 online games of this build (newest first) in the background.</summary>
        public bool FindOnline()
        {
            m_OwnNotice = LobbyNotice.None;
            if (OnlineBusy != OnlineStep.None || m_Host != null || m_Client != null) return false;
            if (m_Lobby.Mode != LobbyMode.Offline && m_Lobby.Mode != LobbyMode.Searching && m_Lobby.Mode != LobbyMode.EnteringCode) return false;
            if (OnlineSettling) return false;
            if (!LobbyState.PhaseAllowsSession(director.Phase))
            {
                m_OwnNotice = LobbyNotice.NotInHub;
                return false;
            }
            LeaveLocalSearch();
            ForgetOnline();
            m_OnlineListShown = true;
            m_FindTask = Link.FindAsync(VersionKey);
            Sync();
            PollOnline();
            return true;
        }

        /// <summary>JOIN next to an online game (0 = the top line): reads its code through the lobby and connects through Relay.</summary>
        public bool JoinOnline(int index)
        {
            if (!m_OnlineListShown || index < 0 || index >= m_OnlineGames.Count) return false;
            return StartOnlineJoin(m_OnlineGames[index], null);
        }

        /// <summary>Joins an online game by the host's 6-character code (no lobby involved). There is no keypad for it on the panel.</summary>
        public bool JoinOnlineCode(string code) => StartOnlineJoin(null, code ?? "");

        bool StartOnlineJoin(OnlineGame game, string code)
        {
            m_OwnNotice = LobbyNotice.None;
            if (OnlineBusy != OnlineStep.None || m_Host != null || m_Client != null) return false;
            if (m_Lobby.Mode != LobbyMode.Offline || OnlineSettling) return false;
            if (!LobbyState.PhaseAllowsSession(director.Phase))
            {
                m_OwnNotice = LobbyNotice.NotInHub;
                return false;
            }
            m_OnlineProblem = OnlineProblem.None;
            m_OnlineListShown = false;
            m_JoinTask = game != null ? Link.JoinAsync(game) : Link.JoinByCodeAsync(code);
            Sync();
            PollOnline();
            return true;
        }

        IOnlineLink Link => m_Link ?? (m_Link = OnlineLinkFactory != null ? OnlineLinkFactory() : new OnlineLink());

        /// <summary>Only games of this exact build are listed, so players who would refuse each other never see each other.</summary>
        string VersionKey => OnlineConfig.VersionKey(NetProtocol.Version, ContentHash);

        /// <summary>Applies whichever online step has finished. Main thread, from Update; never waits.</summary>
        void PollOnline()
        {
            if (m_HostTask != null && m_HostTask.IsCompleted)
            {
                var task = m_HostTask;
                m_HostTask = null;
                ApplyHostResult(task.Status == TaskStatus.RanToCompletion ? task.Result : new OnlineHostResult { Problem = OnlineProblem.Failed });
            }
            if (m_FindTask != null && m_FindTask.IsCompleted)
            {
                var task = m_FindTask;
                m_FindTask = null;
                ApplyFindResult(task.Status == TaskStatus.RanToCompletion ? task.Result : new OnlineFindResult { Problem = OnlineProblem.Failed });
            }
            if (m_JoinTask != null && m_JoinTask.IsCompleted)
            {
                var task = m_JoinTask;
                m_JoinTask = null;
                ApplyJoinResult(task.Status == TaskStatus.RanToCompletion ? task.Result : new OnlineJoinResult { Problem = OnlineProblem.Failed });
            }
        }

        void ApplyHostResult(OnlineHostResult result)
        {
            if (!result.Ok || result.Transport == null)
            {
                SetOnlineProblem(result.Ok ? OnlineProblem.Failed : result.Problem);
                Sync();
                return;
            }
            // HOST ONLINE blocked contracts while it ran, so the hub (or results) is still where the tug is.
            if (!m_Lobby.Host(director.Phase, true, RelayTransport.HostEndpoint.address, result.TugNumber))
            {
                result.Transport.Dispose();
                if (m_Link != null) m_Link.Leave();
                Sync();
                return;
            }
            m_OnlineCode = result.Code;
            StartHosting(result.Transport, result.TugNumber, true);
        }

        void ApplyFindResult(OnlineFindResult result)
        {
            m_OnlineGames.Clear();
            if (!result.Ok)
            {
                SetOnlineProblem(result.Problem);
                return;
            }
            if (result.Games == null) return;
            for (int i = 0; i < result.Games.Count && m_OnlineGames.Count < OnlineConfig.MaxListed; i++)
            {
                if (result.Games[i] != null) m_OnlineGames.Add(result.Games[i]);
            }
        }

        void ApplyJoinResult(OnlineJoinResult result)
        {
            if (!result.Ok || result.Transport == null)
            {
                SetOnlineProblem(result.Ok ? OnlineProblem.Failed : result.Problem);
                Sync();
                return;
            }
            if (!m_Lobby.Join(director.Phase, result.Host))
            {
                result.Transport.Dispose();
                if (m_Link != null) m_Link.Leave();
                Sync();
                return;
            }
            StartClient(result.Transport, result.Host, true);
        }

        void SetOnlineProblem(OnlineProblem problem)
        {
            m_OnlineProblem = problem == OnlineProblem.Cancelled ? OnlineProblem.None : problem;
        }

        /// <summary>Closes a same-Wi-Fi search or keypad before an online step takes over the panel.</summary>
        void LeaveLocalSearch()
        {
            CloseSearch();
            if (m_Lobby.Mode == LobbyMode.Searching || m_Lobby.Mode == LobbyMode.EnteringCode) m_Lobby.Leave();
        }

        /// <summary>Clears the online list and the last online problem (a new choice was made on the panel).</summary>
        void ForgetOnline()
        {
            m_OnlineListShown = false;
            m_OnlineGames.Clear();
            m_OnlineProblem = OnlineProblem.None;
        }

        /// <summary>
        /// Stops every online step: the link unlists a hosted game and gives up anything in flight. A step that
        /// still finishes later has its Relay connection closed as soon as it does (on the main thread), whether or
        /// not this scene still exists. Does not touch a session already running (its owner disposes the transport).
        /// </summary>
        void CancelOnline()
        {
            if (m_Link != null) m_Link.Leave();
            DisposeWhenDone(m_HostTask);
            DisposeWhenDone(m_JoinTask);
            Abandon(m_HostTask);
            Abandon(m_JoinTask);
            Abandon(m_FindTask);
            m_HostTask = null;
            m_JoinTask = null;
            m_FindTask = null;
            m_Online = false;
            m_OnlineCode = null;
            m_ReportedCount = -1;
            ForgetOnline();
        }

        void Abandon(Task task)
        {
            if (task != null && !task.IsCompleted) m_Abandoned.Add(task);
        }

        static TaskScheduler MainThreadScheduler()
        {
            return SynchronizationContext.Current != null ? TaskScheduler.FromCurrentSynchronizationContext() : TaskScheduler.Default;
        }

        static void DisposeWhenDone(Task<OnlineHostResult> task)
        {
            if (task == null) return;
            if (task.IsCompleted) DisposeHostResult(task);
            else task.ContinueWith(DisposeHostResult, CancellationToken.None, TaskContinuationOptions.None, MainThreadScheduler());
        }

        static void DisposeWhenDone(Task<OnlineJoinResult> task)
        {
            if (task == null) return;
            if (task.IsCompleted) DisposeJoinResult(task);
            else task.ContinueWith(DisposeJoinResult, CancellationToken.None, TaskContinuationOptions.None, MainThreadScheduler());
        }

        static void DisposeHostResult(Task<OnlineHostResult> task)
        {
            if (task.Status == TaskStatus.RanToCompletion && task.Result.Ok && task.Result.Transport != null) task.Result.Transport.Dispose();
        }

        static void DisposeJoinResult(Task<OnlineJoinResult> task)
        {
            if (task.Status == TaskStatus.RanToCompletion && task.Result.Ok && task.Result.Transport != null) task.Result.Transport.Dispose();
        }

        // ================================================================== starting and ending sessions

        void StartHosting(INetTransport transport, ushort tug, bool online)
        {
            director.ResetForSession(true);
            var config = new NetHostConfig
            {
                contentHash = ContentHash,
                tugNumber = tug,
                handedness = LocalHandedness(),
                seed = Environment.TickCount,
            };
            DisposeContract();
            m_Rules = new CoopHostRules(director, new HostBroadcast(this)) { Clock = () => Now };
            // Co-op contracts carry the shared spider (never the Mimic) and its blackouts; webs stay unstrung.
            m_Rules.CreatureEnabled = true;
            m_Host = new NetSessionHost(transport, new SceneHostWorld(this, director, m_Rules), config);
            m_Online = online;
            CoopItemReplica.Delay = InterpolationDelay;
            m_LastPoseTick = double.NegativeInfinity;
            m_NextClock = 0.0;
            m_ReportedCount = -1;
            m_LocalSlot = NetProtocol.HostSlot;
            ClearCrew();
            m_Present[m_LocalSlot] = true;
            director.Net = this;
            ApplyLocalSlot(m_LocalSlot);
            Sync();
        }

        void StartClient(INetTransport transport, NetEndpoint host, bool online)
        {
            var config = new NetClientConfig
            {
                contentHash = ContentHash,
                handedness = LocalHandedness(),
                seed = NewSeed(),
            };
            DisposeContract();
            m_Apply = new CoopClientApply(director);
            m_Client = new NetSessionClient(transport, new SceneClientWorld(this, m_Apply), config);
            m_Online = online;
            CoopItemReplica.Delay = InterpolationDelay;
            m_ClientEnded = false;
            HostSilent = false;
            m_Client.Connect(host, Now);
            FlushRelay(transport);
            Sync();
        }

        /// <summary>LEAVE: says goodbye, closes the socket or Relay connection, removes the crew and puts the game back to single-player.</summary>
        public bool Leave()
        {
            m_OwnNotice = LobbyNotice.None;
            bool wasOpen = !IsOffline || m_Lobby.Mode != LobbyMode.Offline || m_OnlineListShown || m_OnlineProblem != OnlineProblem.None;
            double now = Now;
            CancelOnline();
            if (m_Client != null)
            {
                m_Client.Leave(now);
                FlushRelay(m_Client.Transport);
                m_Client.Dispose();
                m_Client = null;
            }
            if (m_Host != null)
            {
                m_Host.Close(now); // tells every client; removes their stand-ins through OnPlayerLeft
                FlushRelay(m_Host.Transport);
                DisposeLingering();
                m_Lingering = m_Host;
                m_LingerUntil = now + HostLingerSeconds;
                m_Host = null;
            }
            DisposeContract();
            CloseSearch();
            ClearCrew();
            HostSilent = false;
            m_Lobby.Leave();
            BackToSinglePlayer();
            return wasOpen;
        }

        void DisposeContract()
        {
            if (m_Rules != null) m_Rules.Dispose();
            m_Rules = null;
            m_Apply = null;
            CoopItemReplica.Delay = SnapshotBuffer<PoseSnapshot>.InterpolationDelay;
        }

        // ================================================================== pump

        void Update()
        {
            PollOnline();
            if (IsOffline) return;
            double now = Now;

            if (m_Lingering != null)
            {
                m_Lingering.Poll(now);
                if (now >= m_LingerUntil) DisposeLingering();
            }

            if (m_Host != null)
            {
                m_Host.Poll(now);
                if (m_Online && m_Host.Transport is IRelayLink hostRelay && HostRelayLost(hostRelay))
                {
                    OnlineHostLost();
                    return;
                }
                RenderCrew(now - InterpolationDelay);
                if (m_Online && m_Link != null) KeepListing();
            }

            if (m_Client != null)
            {
                m_Client.Poll(now);
                // Over Relay a dead connection is known at once: do not sit on HOST NOT RESPONDING for 60 s.
                if (!m_ClientEnded && m_Client != null && m_Online && m_Client.Transport is IRelayLink clientRelay
                    && (clientRelay.LinkLost || clientRelay.AllocationInvalid || clientRelay.RelayLost))
                    QueueClientEnded(SessionEndReason.HostTimeout);
                if (m_ClientEnded) ClientEnded();
                else RenderCrew(m_Client.HostTime(now) - InterpolationDelay);
            }

            if (m_SearchTransport != null && m_Discovery != null)
            {
                m_Discovery.Tick(m_SearchTransport, now);
                m_Lobby.SearchTick(now, m_Discovery.Hosts.Count);
            }
            m_Lobby.OnRoster(PlayerCount);
            RemoveCrewAfterScare();
        }

        /// <summary>
        /// Hosting online. A crew member whose own Relay connection dropped is let go at once (its slot freed as a
        /// Bye would, the others told), and the game stays open and listed. True only when the host's own side is
        /// gone: the allocation is invalid, or the connection to the Relay server dropped (a headset that slept past
        /// Relay's 10 s idle limit).
        /// </summary>
        bool HostRelayLost(IRelayLink relay)
        {
            if (relay.AllocationInvalid || relay.RelayLost) return true;
            while (relay.TryTakeDropped(out NetEndpoint peer)) m_Host.DropPeer(peer);
            return false;
        }

        /// <summary>The online game cannot go on: close it as LEAVE would (unlisting it), and say LINK LOST.</summary>
        void OnlineHostLost()
        {
            Debug.LogWarning("[Net] The online host's Relay connection was lost; the session is closed.");
            Leave();
            m_OwnNotice = LobbyNotice.LinkLost;
        }

        /// <summary>Hosting online: the lobby heartbeat, and the listing's crew count and phase (so FIND shows "ON CONTRACT").</summary>
        void KeepListing()
        {
            m_Link.Tick();
            int count = PlayerCount;
            MissionPhase phase = director != null ? director.Phase : MissionPhase.Hub;
            if (count == m_ReportedCount && phase == m_ReportedPhase) return;
            m_ReportedCount = count;
            m_ReportedPhase = phase;
            m_Link.UpdateHosting(count, phase);
        }

        void LateUpdate()
        {
            if (m_Host == null && m_Client == null) return;
            double now = Now;
            PlayerContext local = director != null ? director.LocalPlayer : null;
            if (local != null)
            {
                AvatarSampler.Sample(local, ref m_LocalAvatar);
                // The host's own Away bit rides on its avatar (a client's is set by NetSessionClient.SetAway).
                m_LocalAvatar.flags = (byte)(m_LocalAway && m_Host != null ? m_LocalAvatar.flags | AvatarFlags.Away : m_LocalAvatar.flags & ~AvatarFlags.Away);
                if (m_Host != null) m_Host.SetLocalAvatar(in m_LocalAvatar);
                if (m_Client != null) m_Client.SetLocalAvatar(in m_LocalAvatar);
            }
            if (m_Host != null)
            {
                if (m_Rules != null) QueueContractStreams(now);
                m_Host.SendTick(now);
                FlushRelay(m_Host.Transport);
            }
            if (m_Client != null)
            {
                m_Client.SendTick(now);
                FlushRelay(m_Client.Transport);
            }
        }

        /// <summary>
        /// The host's part of the send tick during a contract: rest detection, the clock and shared tank at 2 Hz,
        /// and on each 20 Hz tick the poses of the items the host simulates (held or moving ones, nearest the crew
        /// first, plus one resting item in turn). Same phase rule as NetSessionHost.SendTick, so a batch is
        /// queued on exactly the frames a datagram goes out. No allocation.
        /// </summary>
        void QueueContractStreams(double now)
        {
            m_Rules.Tick(Time.deltaTime);
            if (director.Phase != MissionPhase.Active) return;
            // The spider: 10 samples a second, 20 while it investigates or chases.
            if (m_Rules.TrySampleCreature(now, out CreatureSnapshot creature)) m_Host.SetCreature(in creature);
            if (now >= m_NextClock)
            {
                m_NextClock = now + ClockInterval;
                m_Rules.ReadClock(out float elapsed, out float oxygen, out byte flags);
                m_Host.SetClock(elapsed, oxygen, flags);
            }
            if (now - m_LastPoseTick < NetProtocol.SendInterval - 1e-6) return;
            m_LastPoseTick += NetProtocol.SendInterval;
            if (!(now - m_LastPoseTick < NetProtocol.SendInterval)) m_LastPoseTick = now;
            int count = m_Rules.CollectItemPoses(m_Poses);
            for (int i = 0; i < count; i++)
            {
                ItemPoseEntry pose = m_Poses[i];
                m_Host.QueueItemPose(pose.itemId, pose.position, pose.rotation, pose.held, pose.atRest);
            }
        }

        void RenderCrew(double renderTime)
        {
            for (int i = 0; i < m_Crew.Length; i++)
            {
                if (m_Crew[i] != null) m_Crew[i].Render(renderTime);
            }
        }

        /// <summary>Input-like requests go out on the frame they happen, not at the next send tick.</summary>
        void FlushClient()
        {
            if (m_Client == null) return;
            m_Client.Flush(Now);
            FlushRelay(m_Client.Transport);
        }

        /// <summary>A Relay connection queues what is sent until the driver's next update; put it on the wire now.</summary>
        static void FlushRelay(INetTransport transport)
        {
            if (transport is IRelayLink relay) relay.Flush();
        }

        // ================================================================== called by the scene worlds

        /// <summary>Welcome: reset to the session's starting state, take our slot, make the stand-ins.</summary>
        internal void ApplyWelcome(byte localSlot, in WelcomeMsg welcome)
        {
            director.ResetForSession(false);
            m_LocalSlot = localSlot;
            ClearCrew();
            m_Present[localSlot] = true;
            director.Net = this;
            // A headset already off or untracked when the Welcome came: from now on that is Away, not a hazard pause.
            director.MovePausesToAway();
            ApplyLocalSlot(localSlot);
            // Every headset starts on the same spawn point: step onto this slot's own spot in the tug.
            director.PlaceAtCrewSpot(director.LocalPlayer);
            for (int slot = 0; slot < NetProtocol.MaxPlayers; slot++)
            {
                if (slot != localSlot && welcome.IsPresent(slot)) AddCrew((byte)slot, welcome.Handedness(slot), welcome.IsAway(slot));
            }
            m_Lobby.OnWelcome(localSlot, PlayerCount);
            RefreshAway(); // already paused or untracked when the Welcome came: the host hears it now
            Sync();
        }

        internal void AddCrew(byte slot, byte handedness, bool away)
        {
            if (slot >= NetProtocol.MaxPlayers || slot == m_LocalSlot) return;
            m_Present[slot] = true;
            m_RemoveAfterScare[slot] = false;
            RemoteAvatar crew = m_Crew[slot];
            if (crew == null && remoteAvatarPrefab != null)
            {
                crew = Instantiate(remoteAvatarPrefab, transform);
                crew.name = "Crew " + (slot + 1) + " (remote)";
                crew.Setup(new PlayerId(slot), handedness); // before its Start: registers as remote
                m_Crew[slot] = crew;
            }
            else if (crew != null)
            {
                crew.SetHandedness(handedness);
            }
            if (crew != null && crew.context != null) crew.context.IsAway = away;
            m_Lobby.OnRoster(PlayerCount);
        }

        internal void RemoveCrew(byte slot)
        {
            if (slot >= NetProtocol.MaxPlayers || slot == m_LocalSlot) return;
            m_Present[slot] = false;
            if (m_Crew[slot] != null)
            {
                // A jumpscare may be playing against this suit: it stays (hidden from the roster) until the scare ends.
                if (AnyCreatureScaring()) m_RemoveAfterScare[slot] = true;
                else
                {
                    Destroy(m_Crew[slot].gameObject);
                    m_Crew[slot] = null;
                }
            }
            m_Lobby.OnRoster(PlayerCount);
        }

        bool AnyCreatureScaring()
        {
            var enemies = director != null ? director.enemies : null;
            if (enemies == null) return false;
            for (int i = 0; i < enemies.Length; i++)
                if (enemies[i] != null && enemies[i].IsScaring) return true;
            return false;
        }

        /// <summary>Suits whose players left during a jumpscare go once it is over.</summary>
        void RemoveCrewAfterScare()
        {
            bool any = false;
            for (int i = 0; i < m_RemoveAfterScare.Length; i++) any |= m_RemoveAfterScare[i];
            if (!any || AnyCreatureScaring()) return;
            for (int i = 0; i < m_RemoveAfterScare.Length; i++)
            {
                if (!m_RemoveAfterScare[i]) continue;
                m_RemoveAfterScare[i] = false;
                if (m_Present[i] || m_Crew[i] == null) continue;
                Destroy(m_Crew[i].gameObject);
                m_Crew[i] = null;
            }
        }

        internal void SetCrewAway(byte slot, bool away)
        {
            RemoteAvatar crew = Crew(slot);
            if (crew != null && crew.context != null) crew.context.IsAway = away;
        }

        internal void SetCrewHandedness(byte slot, byte handedness)
        {
            RemoteAvatar crew = Crew(slot);
            if (crew != null) crew.SetHandedness(handedness);
        }

        internal void PushCrewAvatar(byte slot, double time, in AvatarSnapshot avatar)
        {
            RemoteAvatar crew = Crew(slot);
            if (crew == null || slot == m_LocalSlot) return;
            crew.Push(time, in avatar);
            // The host has no roster entry for its own Away state; its avatar carries it.
            if (slot == NetProtocol.HostSlot && crew.context != null) crew.context.IsAway = avatar.IsAway;
        }

        /// <summary>The client session ended for a reason other than our own LEAVE. Handled after Poll returns.</summary>
        internal void QueueClientEnded(SessionEndReason reason)
        {
            m_ClientEnded = true;
            m_ClientEndReason = reason;
        }

        void ClientEnded()
        {
            m_ClientEnded = false;
            RefuseReason refusal = m_Client != null ? m_Client.LastRefusal : RefuseReason.None;
            if (m_Client != null)
            {
                m_Client.Dispose();
                m_Client = null;
            }
            if (m_Online)
            {
                if (m_Link != null) m_Link.Leave();
                m_Online = false;
            }
            DisposeContract();
            ClearCrew();
            HostSilent = false;
            m_Lobby.OnSessionEnded(m_ClientEndReason, refusal);
            BackToSinglePlayer();
        }

        // ================================================================== the host's broadcasts

        /// <summary>
        /// The host's game to the crew: each <see cref="ICoopBroadcast"/> call goes to the NetSessionHost Send*
        /// method of the same name. An extraction refusal for the host's own slot is shown on this headset.
        /// </summary>
        sealed class HostBroadcast : ICoopBroadcast
        {
            readonly NetSession m_Net;

            public HostBroadcast(NetSession net) { m_Net = net; }

            NetSessionHost Host => m_Net.m_Host;

            public void RunStart(in RunStartParams parameters)
            {
                var host = Host;
                if (host != null && !host.SendRunStart(in parameters))
                    Debug.LogError("[Net] The contract has more items than a co-op contract can carry; the crew was not told.");
            }

            public void WorldState(in WorldStateMsg state)
            {
                var host = Host;
                if (host != null) host.SendWorldState(in state);
            }

            public void ExtractRefused(byte slot, byte missingSlotsMask)
            {
                if (slot == NetProtocol.HostSlot)
                {
                    if (m_Net.director != null) m_Net.director.ApplyExtractRefused(missingSlotsMask);
                    return;
                }
                var host = Host;
                if (host != null) host.SendExtractRefused(slot, missingSlotsMask);
            }

            public void RunEnded(in RunEndedMsg result)
            {
                var host = Host;
                if (host != null) host.SendRunEnded(in result);
            }

            public void ReturnedToHub() => Host?.SendReturnedToHub();
            public void HostPause(bool on) => Host?.SendHostPause(on);
            public void ItemCollected(byte itemId, byte by) => Host?.SendItemCollected(itemId, by);
            public void ItemValue(byte itemId, int value) => Host?.SendItemValue(itemId, value);
            public void ItemDeposited(byte itemId, byte by, int finalValue, int trickBonus, byte lockerSlot) => Host?.SendItemDeposited(itemId, by, finalValue, trickBonus, lockerSlot);
            public void ItemRecovered(byte itemId, NetVec3 position) => Host?.SendItemRecovered(itemId, position);
            public void ItemRest(byte itemId, NetVec3 position, NetQuat rotation) => Host?.SendItemRest(itemId, position, rotation);
            public void DoorState(byte doorId, bool open) => Host?.SendDoorState(doorId, open);
            public void PointCut(byte panel, byte index) => Host?.SendPointCut(panel, index);
            public void PanelOpened(byte panel, byte by, NetVec3 plateImpulse, NetVec3 plateTorque) => Host?.SendPanelOpened(panel, by, plateImpulse, plateTorque);
            public void CreatureStateChanged(EnemyState state, byte targetSlot) => Host?.SendCreatureStateChanged(state, targetSlot);
            public void Tranquilized(float seconds) => Host?.SendTranquilized(seconds);
            public void CatchStarted(byte victimSlot) => Host?.SendCatchStarted(victimSlot);
            public void BlackoutStarted(float seconds) => Host?.SendBlackoutStarted(seconds);
            public void BlackoutEnded(bool instant) => Host?.SendBlackoutEnded(instant);
        }

        // ================================================================== helpers

        void BackToSinglePlayer()
        {
            m_LocalSlot = 0;
            m_ReportedAway = false;
            if (director == null) return;
            if (ReferenceEquals(director.Net, this)) director.LeaveSession();
            Sync();
        }

        /// <summary>The local rig takes its crew slot and that slot's colour.</summary>
        void ApplyLocalSlot(int slot)
        {
            PlayerContext local = director.LocalPlayer;
            if (local == null) return;
            local.AssignId(new PlayerId(slot), false);
            if (local.avatar != null) local.avatar.SetTint(PlayerAvatar.CrewColors[Mathf.Clamp(slot, 0, PlayerAvatar.CrewColors.Length - 1)]);
        }

        void ClearCrew()
        {
            for (int i = 0; i < m_Crew.Length; i++)
            {
                if (m_Crew[i] != null) Destroy(m_Crew[i].gameObject);
                m_Crew[i] = null;
                m_Present[i] = false;
                m_RemoveAfterScare[i] = false;
            }
        }

        /// <summary>
        /// Contracts are shared while hosting or joined. They are blocked only between pressing JOIN (or HOST
        /// ONLINE) and being let in, so nobody walks into a solo contract mid-handshake.
        /// </summary>
        void Sync()
        {
            if (director != null)
                director.ContractStartBlocked = m_Lobby.Mode == LobbyMode.Connecting || m_HostTask != null || m_JoinTask != null;
        }

        bool EnsureSearchTransport()
        {
            if (m_SearchTransport != null) return true;
            m_SearchTransport = Open(0);
            if (m_SearchTransport != null) return true;
            m_OwnNotice = LobbyNotice.NoNetwork;
            return false;
        }

        void CloseSearch()
        {
            if (m_Discovery != null) m_Discovery.Stop();
            if (m_SearchTransport != null) m_SearchTransport.Dispose();
            m_SearchTransport = null;
        }

        void DisposeLingering()
        {
            if (m_Lingering != null) m_Lingering.Dispose();
            m_Lingering = null;
        }

        static INetTransport Open(ushort port)
        {
            if (TransportFactory != null) return TransportFactory(port);
            return UdpTransport.TryOpen(port, out string _);
        }

        /// <summary>This device's LAN address: the transport's own if it is bound to one, otherwise the OS's answer.</summary>
        static uint LocalAddress(INetTransport transport)
        {
            if (LocalAddressOverride != null) return LocalAddressOverride(transport);
            uint bound = transport != null ? transport.Local.address : 0u;
            if (UdpTransport.IsUsableLanAddress(bound)) return bound;
            if (UdpTransport.TryGetLocalAddress(out uint address)) return address;
            return bound;
        }

        static int s_SeedCounter;
        static int NewSeed() => Environment.TickCount ^ (++s_SeedCounter * 7919);

        byte LocalHandedness()
        {
            return director != null && director.Profile != null && director.Profile.Data.Handedness == Handedness.Left ? (byte)1 : (byte)0;
        }
    }
}

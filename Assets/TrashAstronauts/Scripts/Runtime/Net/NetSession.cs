using System;
using System.Collections.Generic;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// The one co-op MonoBehaviour, on the Mission object. Offline by default: it opens no socket and starts no
    /// timer or buffer until the crew panel (NetLobbyBoard) or a test calls <see cref="Host"/>, <see cref="Find"/>,
    /// <see cref="EnterCode"/> or <see cref="Join"/>. Single-player never gets further than that.
    ///
    /// While a session is open it owns the pure C# session (NetSessionHost or NetSessionClient) and its
    /// transport, pumps it (receive in Update, the 20 Hz send tick in LateUpdate after the rig has been posed),
    /// implements <see cref="IMissionNet"/> for the game, and keeps one RemoteAvatar per other crew member.
    ///
    /// Stage 1 shares presence only: everyone sees everyone's head, body and hands in the tug. Contracts are
    /// blocked on every headset while a link is open (<see cref="MissionDirector.ContractStartBlocked"/>), so none
    /// of the shared-contract messages can occur yet; their handlers are marked no-ops in SceneHostWorld /
    /// SceneClientWorld.
    ///
    /// Nothing here reads the microphone, and no message type can carry audio or recorded phrases.
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

        /// <summary>Seconds a closed host keeps answering stragglers with "the host left" before its socket closes.</summary>
        public const double HostLingerSeconds = 1.5;

        public MissionDirector director;
        [Tooltip("Stand-in for another crew member (Builder.Net builds it).")]
        public RemoteAvatar remoteAvatarPrefab;

        static readonly List<NetSession> s_Live = new List<NetSession>();

        readonly LobbyState m_Lobby = new LobbyState();
        readonly RemoteAvatar[] m_Crew = new RemoteAvatar[NetProtocol.MaxPlayers];
        readonly bool[] m_Present = new bool[NetProtocol.MaxPlayers];

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

        // ================================================================== state for the board and tests

        /// <summary>The lobby state machine behind the crew panel.</summary>
        public LobbyState Lobby => m_Lobby;
        public LobbyMode Mode => m_Lobby.Mode;
        /// <summary>True when no socket of any kind is open (single-player).</summary>
        public bool IsOffline => m_Host == null && m_Client == null && m_SearchTransport == null && m_Lingering == null;
        /// <summary>Hosts answering FIND CREW (null before the first search).</summary>
        public HostList FoundHosts => m_Discovery != null ? m_Discovery.Hosts : null;
        /// <summary>True for the 3 s a FIND CREW search is probing.</summary>
        public bool IsProbing => m_Discovery != null && m_Discovery.IsSearching && m_SearchTransport != null;
        /// <summary>A joined client has heard nothing from the host for 1.5 s.</summary>
        public bool HostSilent { get; internal set; }
        /// <summary>The notice to show: the lobby's, or one of this component's own (a socket that would not open).</summary>
        public LobbyNotice Notice => m_Lobby.Notice != LobbyNotice.None ? m_Lobby.Notice : m_OwnNotice;
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
        /// <summary>The transport currently in use (host, client or search), for tests.</summary>
        public INetTransport Transport => m_Host != null ? m_Host.Transport : m_Client != null ? m_Client.Transport : m_SearchTransport;

        /// <summary>The session clock.</summary>
        public static double Now => ClockOverride != null ? ClockOverride() : Time.realtimeSinceStartupAsDouble;

        // ================================================================== IMissionNet

        public bool IsClient => m_Client != null;
        public bool IsHost => m_Host != null;
        public int LocalSlot => m_LocalSlot;

        // Stage 1: contracts are blocked while a link is open (MissionDirector.ContractStartBlocked), so the
        // game never reaches the request gates below except for presence. Each says the stage that fills it.
        public void RequestStart() { /* Stage 2: m_Client.SendStartRequest + Flush. */ }
        public void RequestExtract() { /* Stage 2 */ }
        public void RequestDoor(SlidingDoor door, bool desiredOpen) { /* Stage 2: every door in the hub is locked. */ }
        public void ReportPointCut(Cuttable panel, int pointIndex) { /* Stage 2 */ }
        public void ReportSalvageDamage(SalvageItem item, int newValue) { /* Stage 3 */ }
        public void ReportWire(WebTripwire wire, WireChange change) { /* Stage 4 */ }
        public void ReportTranqHit(Vector3 origin, Vector3 hitPoint) { /* Stage 4 */ }
        public void ForwardNoise(in NoiseEvent noise) { /* Stage 4 */ }

        public void ReportPause(bool paused)
        {
            if (m_Client != null) m_Client.SetAway(paused, Now);
        }

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
            // Best-effort goodbye, then every socket closed. The director may be going away too.
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
            if (m_Client != null) m_Client.SetAway(paused, Now);
        }

        void OnHazardPauseChanged(bool paused)
        {
            // Headset off or tracking lost: the crew sees this player as away (and later stages stop counting them).
            if (m_Client != null) m_Client.SetAway(paused, Now);
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
        }

        /// <summary>Closes every socket at once, with no goodbye and no change to the game. For OnDestroy and test tear-down.</summary>
        public void ShutDown()
        {
            if (m_Client != null) { m_Client.Dispose(); m_Client = null; }
            if (m_Host != null) { m_Host.Dispose(); m_Host = null; }
            if (m_Lingering != null) { m_Lingering.Dispose(); m_Lingering = null; }
            CloseSearch();
        }

        // ================================================================== crew panel actions

        /// <summary>HOST CO-OP: binds port 47800 and waits for crew. Only from the hub or the results screen.</summary>
        public bool Host()
        {
            m_OwnNotice = LobbyNotice.None;
            MissionPhase phase = director.Phase;
            if (!m_Lobby.CanHost(phase)) return m_Lobby.Host(phase, false, 0u, 0); // NotInHub, or not from this mode
            DisposeLingering();
            CloseSearch();

            INetTransport transport = Open(NetProtocol.HostPort);
            if (transport == null) return m_Lobby.Host(phase, false, 0u, 0); // PORT BUSY
            uint address = LocalAddress(transport);
            ushort tug = JoinCode.MakeTugNumber(NewSeed());
            if (!m_Lobby.Host(phase, true, address, tug))
            {
                transport.Dispose(); // NO WI-FI
                Sync();
                return false;
            }

            director.ResetForSession(true);
            var config = new NetHostConfig
            {
                contentHash = ContentHash,
                tugNumber = tug,
                handedness = LocalHandedness(),
                seed = Environment.TickCount,
            };
            m_Host = new NetSessionHost(transport, new SceneHostWorld(this, director), config);
            m_LocalSlot = NetProtocol.HostSlot;
            ClearCrew();
            m_Present[m_LocalSlot] = true;
            director.Net = this;
            ApplyLocalSlot(m_LocalSlot);
            Sync();
            return true;
        }

        /// <summary>FIND CREW: probes the local network for 3 s (broadcast plus a unicast sweep of the /24).</summary>
        public bool Find()
        {
            m_OwnNotice = LobbyNotice.None;
            MissionPhase phase = director.Phase;
            if (!LobbyState.PhaseAllowsSession(phase)) return m_Lobby.Find(phase, 0u, Now);
            if (m_Lobby.Mode != LobbyMode.Offline && m_Lobby.Mode != LobbyMode.Searching && m_Lobby.Mode != LobbyMode.EnteringCode) return false;
            if (!EnsureSearchTransport()) return false;
            double now = Now;
            uint address = LocalAddress(m_SearchTransport);
            if (!m_Lobby.Find(phase, address, now)) return false;
            if (m_Discovery == null) m_Discovery = new Discovery(NetProtocol.Version, NewSeed());
            m_Discovery.Start(address, now, NetProtocol.HostPort);
            Sync();
            return true;
        }

        /// <summary>ENTER CODE: the keypad, with C prefilled from this headset's own address.</summary>
        public bool EnterCode()
        {
            m_OwnNotice = LobbyNotice.None;
            MissionPhase phase = director.Phase;
            if (!LobbyState.PhaseAllowsSession(phase)) return m_Lobby.EnterCode(phase, 0u);
            if (m_Lobby.Mode != LobbyMode.Offline && m_Lobby.Mode != LobbyMode.Searching && m_Lobby.Mode != LobbyMode.EnteringCode) return false;
            if (!EnsureSearchTransport()) return false;
            if (!m_Lobby.EnterCode(phase, LocalAddress(m_SearchTransport))) return false;
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

        /// <summary>Starts joining a host. The game becomes part of the session when its Welcome arrives.</summary>
        public bool Join(NetEndpoint host)
        {
            m_OwnNotice = LobbyNotice.None;
            if (m_Host != null || m_Client != null) return false;
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
            var config = new NetClientConfig
            {
                contentHash = ContentHash,
                handedness = LocalHandedness(),
                seed = NewSeed(),
            };
            m_Client = new NetSessionClient(transport, new SceneClientWorld(this), config);
            m_ClientEnded = false;
            HostSilent = false;
            m_Client.Connect(host, Now);
            Sync();
            return true;
        }

        /// <summary>LEAVE: says goodbye, closes the socket, removes the crew and puts the game back to single-player.</summary>
        public bool Leave()
        {
            m_OwnNotice = LobbyNotice.None;
            bool wasOpen = !IsOffline || m_Lobby.Mode != LobbyMode.Offline;
            double now = Now;
            if (m_Client != null)
            {
                m_Client.Leave(now);
                m_Client.Dispose();
                m_Client = null;
            }
            if (m_Host != null)
            {
                m_Host.Close(now); // tells every client; removes their stand-ins through OnPlayerLeft
                DisposeLingering();
                m_Lingering = m_Host;
                m_LingerUntil = now + HostLingerSeconds;
                m_Host = null;
            }
            CloseSearch();
            ClearCrew();
            HostSilent = false;
            m_Lobby.Leave();
            BackToSinglePlayer();
            return wasOpen;
        }

        // ================================================================== pump

        void Update()
        {
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
                RenderCrew(now - SnapshotBuffer<AvatarSnapshot>.InterpolationDelay);
            }

            if (m_Client != null)
            {
                m_Client.Poll(now);
                if (m_ClientEnded) ClientEnded();
                else RenderCrew(m_Client.RenderTime(now));
            }

            if (m_SearchTransport != null && m_Discovery != null)
            {
                m_Discovery.Tick(m_SearchTransport, now);
                m_Lobby.SearchTick(now, m_Discovery.Hosts.Count);
            }
            m_Lobby.OnRoster(PlayerCount);
        }

        void LateUpdate()
        {
            if (m_Host == null && m_Client == null) return;
            double now = Now;
            PlayerContext local = director != null ? director.LocalPlayer : null;
            if (local != null)
            {
                AvatarSampler.Sample(local, ref m_LocalAvatar);
                if (m_Host != null) m_Host.SetLocalAvatar(in m_LocalAvatar);
                if (m_Client != null) m_Client.SetLocalAvatar(in m_LocalAvatar);
            }
            if (m_Host != null) m_Host.SendTick(now);
            if (m_Client != null) m_Client.SendTick(now);
        }

        void RenderCrew(double renderTime)
        {
            for (int i = 0; i < m_Crew.Length; i++)
            {
                if (m_Crew[i] != null) m_Crew[i].Render(renderTime);
            }
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
            ApplyLocalSlot(localSlot);
            for (int slot = 0; slot < NetProtocol.MaxPlayers; slot++)
            {
                if (slot != localSlot && welcome.IsPresent(slot)) AddCrew((byte)slot, welcome.Handedness(slot), welcome.IsAway(slot));
            }
            m_Lobby.OnWelcome(localSlot, PlayerCount);
            Sync();
        }

        internal void AddCrew(byte slot, byte handedness, bool away)
        {
            if (slot >= NetProtocol.MaxPlayers || slot == m_LocalSlot) return;
            m_Present[slot] = true;
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
            if (m_Crew[slot] != null) Destroy(m_Crew[slot].gameObject);
            m_Crew[slot] = null;
            m_Lobby.OnRoster(PlayerCount);
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
            if (crew != null && slot != m_LocalSlot) crew.Push(time, in avatar);
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
            ClearCrew();
            HostSilent = false;
            m_Lobby.OnSessionEnded(m_ClientEndReason, refusal);
            BackToSinglePlayer();
        }

        // ================================================================== helpers

        void BackToSinglePlayer()
        {
            m_LocalSlot = 0;
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
            }
        }

        /// <summary>Keeps the director's contract block in step with the lobby (blocked while any link is open).</summary>
        void Sync()
        {
            if (director != null) director.ContractStartBlocked = m_Lobby.BlocksContractStart;
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

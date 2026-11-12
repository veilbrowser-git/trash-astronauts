using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using Unity.Networking.Transport.Relay;
using UnityEngine;

namespace TrashAstronauts.Net.Online
{
    /// <summary>
    /// PLAY ANYWHERE: the one entry point a lobby board needs for online games (Unity Relay + Lobby, anonymous
    /// sign-in). It hands back an INetTransport that NetSessionHost / NetSessionClient use exactly like a
    /// UdpTransport; it never touches the session, the scene or the board itself.
    ///
    ///   var link = new OnlineLink();                                   // touches nothing
    ///   OnlineHostResult h = await link.HostAsync(tug, versionKey, phase);   // h.Transport, h.TugNumber, h.Code
    ///       new NetSessionHost(h.Transport, world, config) ... ; every frame: link.Tick();
    ///       when the crew or phase changes: link.UpdateHosting(playerCount, phase);
    ///   OnlineFindResult f = await link.FindAsync(versionKey);         // f.Games: up to 5, newest first (line: "TUG 4821  2/4  IN HUB")
    ///   OnlineJoinResult j = await link.JoinAsync(f.Games[0]);         // or JoinByCodeAsync("ABC123")
    ///       new NetSessionClient(j.Transport, world, config).Connect(j.Host, now);
    ///   link.Leave();                                                  // unlists the game; the session still owns and disposes the transport
    ///   link.StatusText                                                // one line for the board: "SIGNING IN...", "TUG 4821  CODE ABC123", "NO INTERNET" ...
    ///
    /// versionKey: OnlineConfig.VersionKey(NetProtocol.Version, contentHash), so builds that would refuse each
    /// other never see each other. Every method is async, main thread, never throws and never blocks; a result
    /// with Ok == false carries an <see cref="OnlineProblem"/> and its board text. Only one step runs at a time
    /// (another call meanwhile answers Busy). Nothing contacts Unity until HostAsync, FindAsync or a Join is called.
    /// The returned transport belongs to the caller (hand it to a session, which disposes it, or Dispose it).
    /// After a successful Host or Join, poll the transport every frame: Relay drops a silent connection.
    /// </summary>
    public sealed class OnlineLink
    {
        readonly OnlineServices m_Services;
        readonly IRelayConnector m_Relay;
        readonly Func<double> m_Clock;
        readonly OnlineLobby m_Lobby;
        int m_Generation;
        bool m_Busy;

        public OnlineLinkState State { get; private set; }
        public OnlineProblem Problem { get; private set; }
        public ushort TugNumber { get; private set; }
        /// <summary>Hosting: the 6-character Relay join code (the typed fallback).</summary>
        public string Code { get; private set; }
        public OnlineServices Services => m_Services;
        public OnlineLobby Lobby => m_Lobby;
        public bool IsBusy => m_Busy;

        /// <param name="services">Default: <see cref="OnlineServices.Shared"/>.</param>
        /// <param name="relay">Default: <see cref="RelayConnector"/>.</param>
        /// <param name="clock">Seconds, for the lobby rate limits. Default: Time.realtimeSinceStartupAsDouble.</param>
        public OnlineLink(OnlineServices services = null, IRelayConnector relay = null, Func<double> clock = null)
        {
            m_Services = services ?? OnlineServices.Shared;
            m_Relay = relay ?? new RelayConnector();
            m_Clock = clock ?? (() => Time.realtimeSinceStartupAsDouble);
            m_Lobby = new OnlineLobby(() => m_Services.Backend, m_Clock);
        }

        /// <summary>One line for the lobby board.</summary>
        public string StatusText
        {
            get
            {
                switch (State)
                {
                    case OnlineLinkState.Idle: return "";
                    case OnlineLinkState.SigningIn: return "SIGNING IN...";
                    case OnlineLinkState.Opening: return "OPENING...";
                    case OnlineLinkState.Hosting:
                        return "TUG " + TugNumber + "  CODE " + Code + (m_Lobby.IsListed ? "" : "  (NOT LISTED)");
                    case OnlineLinkState.Searching: return "SEARCHING...";
                    case OnlineLinkState.Found: return m_Lobby.LastList.Count == 0 ? "NO GAMES FOUND" : m_Lobby.LastList.Count + (m_Lobby.LastList.Count == 1 ? " GAME" : " GAMES");
                    case OnlineLinkState.Joining: return "JOINING...";
                    case OnlineLinkState.Joined: return "ONLINE";
                    default: return OnlineErrors.Text(Problem);
                }
            }
        }

        // ------------------------------------------------------------------ host

        /// <summary>
        /// Signs in, creates a Relay allocation for 3 more players, binds to it, and lists the game as "TUG n".
        /// If only the listing fails the game still hosts (Ok, Listed false): players can type the code.
        /// </summary>
        public async Task<OnlineHostResult> HostAsync(ushort tugNumber, string versionKey, MissionPhase phase, int playerCount = 1)
        {
            if (m_Busy) return new OnlineHostResult { Problem = OnlineProblem.Busy, Text = OnlineErrors.Text(OnlineProblem.Busy) };
            if (State == OnlineLinkState.Hosting || State == OnlineLinkState.Joined) Leave();
            m_Busy = true;
            int generation = ++m_Generation;
            INetTransport transport = null;
            try
            {
                if (!await SignIn(generation)) return HostFail(generation, Problem);
                State = OnlineLinkState.Opening;

                RelayHostGrant grant;
                try { grant = await OnlineCalls.WithTimeout(m_Services.Backend.CreateRelayAsync(OnlineConfig.RelayPeers)); }
                catch (Exception e) { return HostFail(generation, Classified(e, "Relay allocation")); }
                if (generation != m_Generation) return HostFail(generation, OnlineProblem.Cancelled);

                var opened = await m_Relay.OpenHostAsync(grant.ServerData);
                transport = opened.transport;
                if (transport == null) return HostFail(generation, opened.problem == OnlineProblem.None ? OnlineProblem.Failed : opened.problem);
                if (generation != m_Generation) { transport.Dispose(); return HostFail(generation, OnlineProblem.Cancelled); }

                var info = new OnlineGameInfo
                {
                    tugNumber = tugNumber,
                    versionKey = versionKey,
                    players = Math.Max(1, playerCount),
                    maxPlayers = NetProtocol.MaxPlayers,
                    phaseKey = OnlineConfig.PhaseKey(phase),
                };
                OnlineProblem listed = await m_Lobby.PublishAsync(info, grant.JoinCode);
                if (generation != m_Generation)
                {
                    transport.Dispose();
                    OnlineServices.Observe(m_Lobby.CloseAsync());
                    return HostFail(generation, OnlineProblem.Cancelled);
                }

                TugNumber = tugNumber;
                Code = grant.JoinCode;
                State = OnlineLinkState.Hosting;
                Problem = OnlineProblem.None;
                return new OnlineHostResult
                {
                    Ok = true,
                    Transport = transport,
                    TugNumber = tugNumber,
                    Code = grant.JoinCode,
                    Listed = listed == OnlineProblem.None,
                    Text = StatusText,
                };
            }
            catch (Exception e)
            {
                // Nothing above should throw; this is the "never throws into game code" backstop.
                transport?.Dispose();
                return HostFail(generation, Classified(e, "host"));
            }
            finally
            {
                m_Busy = false;
            }
        }

        OnlineHostResult HostFail(int generation, OnlineProblem problem)
        {
            Fail(generation, problem);
            return new OnlineHostResult { Problem = problem, Text = OnlineErrors.Text(problem) };
        }

        /// <summary>The crew count or phase changed (the lobby is updated at most every 2 s).</summary>
        public void UpdateHosting(int playerCount, MissionPhase phase)
        {
            if (State != OnlineLinkState.Hosting) return;
            m_Lobby.SetCrew(playerCount, OnlineConfig.PhaseKey(phase));
        }

        /// <summary>Call every frame (cheap): lobby heartbeat and pending updates while hosting.</summary>
        public void Tick()
        {
            if (State == OnlineLinkState.Hosting) m_Lobby.Tick();
        }

        // ------------------------------------------------------------------ find and join

        /// <summary>Open games of this version: at most 5, newest first. Asked of the service at most every 2 s.</summary>
        public async Task<OnlineFindResult> FindAsync(string versionKey)
        {
            if (m_Busy) return new OnlineFindResult { Games = new List<OnlineGame>(), Problem = OnlineProblem.Busy, Text = OnlineErrors.Text(OnlineProblem.Busy) };
            if (State == OnlineLinkState.Hosting || State == OnlineLinkState.Joined) Leave();
            m_Busy = true;
            int generation = ++m_Generation;
            try
            {
                if (!await SignIn(generation))
                    return new OnlineFindResult { Games = new List<OnlineGame>(), Problem = Problem, Text = OnlineErrors.Text(Problem) };
                State = OnlineLinkState.Searching;
                var listed = await m_Lobby.ListAsync(versionKey);
                if (generation != m_Generation)
                    return new OnlineFindResult { Games = new List<OnlineGame>(), Problem = OnlineProblem.Cancelled };
                if (listed.problem != OnlineProblem.None)
                {
                    Fail(generation, listed.problem);
                    return new OnlineFindResult { Games = listed.games, Problem = listed.problem, Text = OnlineErrors.Text(listed.problem) };
                }
                State = OnlineLinkState.Found;
                Problem = OnlineProblem.None;
                return new OnlineFindResult { Ok = true, Games = listed.games, FromCache = listed.fromCache, Text = StatusText };
            }
            catch (Exception e)
            {
                OnlineProblem p = Classified(e, "find");
                Fail(generation, p);
                return new OnlineFindResult { Games = new List<OnlineGame>(), Problem = p, Text = OnlineErrors.Text(p) };
            }
            finally
            {
                m_Busy = false;
            }
        }

        /// <summary>Joins a game from the FIND list: reads its code through the lobby, then connects through Relay.</summary>
        public Task<OnlineJoinResult> JoinAsync(OnlineGame game)
        {
            if (game == null) return Task.FromResult(new OnlineJoinResult { Problem = OnlineProblem.GameGone, Text = OnlineErrors.Text(OnlineProblem.GameGone) });
            return Join(game, null);
        }

        /// <summary>The typed fallback: joins by the host's 6-character code (no lobby involved).</summary>
        public Task<OnlineJoinResult> JoinByCodeAsync(string code)
        {
            string normalized = JoinCodes.Normalize(code);
            if (normalized == null) return Task.FromResult(new OnlineJoinResult { Problem = OnlineProblem.BadCode, Text = OnlineErrors.Text(OnlineProblem.BadCode) });
            return Join(null, normalized);
        }

        async Task<OnlineJoinResult> Join(OnlineGame game, string code)
        {
            if (m_Busy) return new OnlineJoinResult { Problem = OnlineProblem.Busy, Text = OnlineErrors.Text(OnlineProblem.Busy) };
            if (State == OnlineLinkState.Hosting || State == OnlineLinkState.Joined) Leave();
            m_Busy = true;
            int generation = ++m_Generation;
            INetTransport transport = null;
            try
            {
                if (!await SignIn(generation)) return JoinFail(generation, Problem);
                State = OnlineLinkState.Joining;

                if (code == null)
                {
                    var read = await m_Lobby.JoinForCodeAsync(game);
                    if (generation != m_Generation) return JoinFail(generation, OnlineProblem.Cancelled);
                    if (read.problem != OnlineProblem.None) return JoinFail(generation, read.problem);
                    code = read.code;
                }

                RelayServerData data;
                try { data = await OnlineCalls.WithTimeout(m_Services.Backend.JoinRelayAsync(code)); }
                catch (Exception e) { return JoinFail(generation, Classified(e, "Relay join")); }
                if (generation != m_Generation) return JoinFail(generation, OnlineProblem.Cancelled);

                var opened = await m_Relay.OpenClientAsync(data);
                transport = opened.transport;
                if (transport == null) return JoinFail(generation, opened.problem == OnlineProblem.None ? OnlineProblem.NoAnswer : opened.problem);
                if (generation != m_Generation) { transport.Dispose(); return JoinFail(generation, OnlineProblem.Cancelled); }

                TugNumber = game != null ? game.TugNumber : (ushort)0;
                Code = code;
                State = OnlineLinkState.Joined;
                Problem = OnlineProblem.None;
                return new OnlineJoinResult { Ok = true, Transport = transport, Host = opened.host, Text = StatusText };
            }
            catch (Exception e)
            {
                transport?.Dispose();
                return JoinFail(generation, Classified(e, "join"));
            }
            finally
            {
                m_Busy = false;
            }
        }

        OnlineJoinResult JoinFail(int generation, OnlineProblem problem)
        {
            Fail(generation, problem);
            return new OnlineJoinResult { Problem = problem, Text = OnlineErrors.Text(problem) };
        }

        // ------------------------------------------------------------------ leave

        /// <summary>
        /// Stops whatever is running (a step in flight finishes as Cancelled and releases what it opened), unlists a
        /// hosted game (deleted in the background), and goes back to Idle. Does not dispose a transport already handed
        /// out: the session that owns it does. Safe to call any time, any number of times.
        /// </summary>
        public void Leave()
        {
            m_Generation++;
            OnlineServices.Observe(m_Lobby.CloseAsync());
            State = OnlineLinkState.Idle;
            Problem = OnlineProblem.None;
            TugNumber = 0;
            Code = null;
        }

        // ------------------------------------------------------------------ helpers

        async Task<bool> SignIn(int generation)
        {
            if (!m_Services.IsReady) State = OnlineLinkState.SigningIn;
            bool ok = await m_Services.StartAsync();
            if (generation != m_Generation) { Problem = OnlineProblem.Cancelled; return false; }
            if (!ok)
            {
                Fail(generation, m_Services.Problem == OnlineProblem.None ? OnlineProblem.SignInFailed : m_Services.Problem);
                return false;
            }
            return true;
        }

        void Fail(int generation, OnlineProblem problem)
        {
            if (generation != m_Generation) return; // a Leave or a newer step owns the state now
            if (problem == OnlineProblem.Cancelled) { State = OnlineLinkState.Idle; Problem = OnlineProblem.None; return; }
            State = OnlineLinkState.Failed;
            Problem = problem;
        }

        static OnlineProblem Classified(Exception e, string what)
        {
            Debug.LogWarning("[Online] " + what + ": " + OnlineErrors.Describe(e));
            OnlineProblem p = OnlineErrors.Classify(e);
            return p == OnlineProblem.None ? OnlineProblem.Failed : p;
        }
    }

    public enum OnlineLinkState : byte
    {
        Idle = 0,
        SigningIn = 1,
        /// <summary>Hosting: Relay allocation and binding, then the lobby.</summary>
        Opening = 2,
        Hosting = 3,
        Searching = 4,
        /// <summary>A FIND answer is in (possibly empty).</summary>
        Found = 5,
        Joining = 6,
        Joined = 7,
        Failed = 8,
    }

    public struct OnlineHostResult
    {
        public bool Ok;
        public INetTransport Transport;
        public ushort TugNumber;
        /// <summary>The Relay join code, 6 characters: show it so friends can type it if the list fails.</summary>
        public string Code;
        /// <summary>The game is in the lobby list (false: only the code works).</summary>
        public bool Listed;
        public OnlineProblem Problem;
        public string Text;
    }

    public struct OnlineFindResult
    {
        public bool Ok;
        public List<OnlineGame> Games;
        /// <summary>Asked again within 2 s: this is the previous answer.</summary>
        public bool FromCache;
        public OnlineProblem Problem;
        public string Text;
    }

    public struct OnlineJoinResult
    {
        public bool Ok;
        public INetTransport Transport;
        /// <summary>Where the client's Hello goes (RelayTransport.HostEndpoint).</summary>
        public NetEndpoint Host;
        public OnlineProblem Problem;
        public string Text;
    }
}

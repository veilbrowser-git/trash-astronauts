using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace TrashAstronauts.Net.Online
{
    /// <summary>
    /// Finding games without typing, on the Lobby service. A host publishes "TUG 4821" with its version key,
    /// crew count and phase (public, indexed) and its Relay join code (members only); it heartbeats every 15 s
    /// and deletes the lobby when it closes. A joiner lists open games of its own version (newest first, at most
    /// 5, refreshed at most every 2 s), joins one just long enough to read the code, and leaves the lobby again.
    ///
    /// Main thread only. Every method catches what the service throws and reports an <see cref="OnlineProblem"/>.
    /// Time comes from the clock passed in (seconds), so the rate limits are testable.
    /// </summary>
    public sealed class OnlineLobby
    {
        readonly Func<IOnlineBackend> m_Backend;
        readonly Func<double> m_Clock;

        // hosting
        string m_LobbyId;
        double m_NextHeartbeat;
        double m_NextUpdate;
        bool m_UpdatePending;
        int m_Players;
        string m_PhaseKey;
        Task m_InFlight;

        // finding
        List<OnlineGame> m_LastList = new List<OnlineGame>();
        double m_LastQueryAt = double.NegativeInfinity;
        Task<List<OnlineGame>> m_Query;

        /// <summary>The published lobby, or null.</summary>
        public string LobbyId => m_LobbyId;
        public bool IsListed => m_LobbyId != null;
        public int Heartbeats { get; private set; }
        public int Updates { get; private set; }
        public int Queries { get; private set; }
        /// <summary>The last background call (heartbeat, update, delete) that failed, for logs.</summary>
        public OnlineProblem LastBackgroundProblem { get; private set; }

        public OnlineLobby(Func<IOnlineBackend> backend, Func<double> clock)
        {
            m_Backend = backend ?? throw new ArgumentNullException(nameof(backend));
            m_Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        double Now => m_Clock();

        // ------------------------------------------------------------------ hosting

        /// <summary>Creates the public lobby. On failure the game still runs: it just is not listed (the code works).</summary>
        public async Task<OnlineProblem> PublishAsync(OnlineGameInfo info, string joinCode)
        {
            IOnlineBackend backend = m_Backend();
            if (backend == null) return OnlineProblem.NotSetUp;
            if (m_LobbyId != null) return OnlineProblem.Busy;
            try
            {
                string id = await OnlineCalls.WithTimeout(backend.CreateLobbyAsync(info, joinCode));
                m_LobbyId = id;
                m_Players = info.players;
                m_PhaseKey = info.phaseKey;
                m_UpdatePending = false;
                double now = Now;
                m_NextHeartbeat = now + OnlineConfig.HeartbeatSeconds;
                m_NextUpdate = now + OnlineConfig.LobbyUpdateSeconds;
                return OnlineProblem.None;
            }
            catch (Exception e)
            {
                OnlineProblem p = OnlineErrors.Classify(e);
                Debug.LogWarning("[Online] Lobby create: " + OnlineErrors.Describe(e));
                return p == OnlineProblem.None ? OnlineProblem.Failed : p;
            }
        }

        /// <summary>The crew or phase changed; pushed to the lobby at most every 2 s (only when something differs).</summary>
        public void SetCrew(int players, string phaseKey)
        {
            if (m_LobbyId == null) return;
            if (players == m_Players && phaseKey == m_PhaseKey) return;
            m_Players = players;
            m_PhaseKey = phaseKey;
            m_UpdatePending = true;
        }

        /// <summary>Call every frame while hosting (cheap): sends the heartbeat and pending crew updates, one call at a time.</summary>
        public void Tick()
        {
            if (m_LobbyId == null) return;
            if (m_InFlight != null && !m_InFlight.IsCompleted) return;
            double now = Now;
            IOnlineBackend backend = m_Backend();
            if (backend == null) return;
            if (now >= m_NextHeartbeat)
            {
                m_NextHeartbeat = now + OnlineConfig.HeartbeatSeconds;
                Heartbeats++;
                m_InFlight = Background(backend.HeartbeatLobbyAsync(m_LobbyId), m_LobbyId);
                return;
            }
            if (m_UpdatePending && now >= m_NextUpdate)
            {
                m_UpdatePending = false;
                m_NextUpdate = now + OnlineConfig.LobbyUpdateSeconds;
                Updates++;
                m_InFlight = Background(backend.UpdateLobbyAsync(m_LobbyId, m_Players, m_PhaseKey), m_LobbyId);
            }
        }

        async Task Background(Task call, string lobbyId)
        {
            try
            {
                await OnlineCalls.WithTimeout(call);
            }
            catch (Exception e)
            {
                LastBackgroundProblem = OnlineErrors.Classify(e);
                Debug.LogWarning("[Online] Lobby: " + OnlineErrors.Describe(e));
                // The lobby is gone (expired, or deleted from the dashboard): stop heartbeating it. The game goes on.
                if (LastBackgroundProblem == OnlineProblem.GameGone && m_LobbyId == lobbyId) m_LobbyId = null;
            }
        }

        /// <summary>Deletes the lobby (in the background; failures only logged). The service would expire it anyway.</summary>
        public Task CloseAsync()
        {
            string id = m_LobbyId;
            m_LobbyId = null;
            m_UpdatePending = false;
            if (id == null) return Task.CompletedTask;
            IOnlineBackend backend = m_Backend();
            if (backend == null) return Task.CompletedTask;
            Task delete;
            try { delete = backend.DeleteLobbyAsync(id); }
            catch (Exception e) { delete = Task.FromException(e); }
            return Background(delete, null);
        }

        // ------------------------------------------------------------------ finding

        /// <summary>The list from the last successful query.</summary>
        public IReadOnlyList<OnlineGame> LastList => m_LastList;

        /// <summary>
        /// Open games of <paramref name="versionKey"/>, newest first, at most 5. Within 2 s of the last query the
        /// previous list comes back without a call (fromCache true); a query already running is shared.
        /// </summary>
        public async Task<(List<OnlineGame> games, bool fromCache, OnlineProblem problem)> ListAsync(string versionKey)
        {
            IOnlineBackend backend = m_Backend();
            if (backend == null) return (new List<OnlineGame>(), false, OnlineProblem.NotSetUp);
            Task<List<OnlineGame>> query = m_Query;
            if (query == null || query.IsCompleted)
            {
                if (Now - m_LastQueryAt < OnlineConfig.ListRefreshSeconds) return (new List<OnlineGame>(m_LastList), true, OnlineProblem.None);
                m_LastQueryAt = Now;
                Queries++;
                try { query = backend.QueryLobbiesAsync(versionKey, OnlineConfig.MaxListed); }
                catch (Exception e) { query = Task.FromException<List<OnlineGame>>(e); }
                m_Query = query;
            }
            try
            {
                List<OnlineGame> raw = await OnlineCalls.WithTimeout(query);
                var arranged = new List<OnlineGame>();
                foreach (OnlineGame g in OnlineGame.Arrange(raw, int.MaxValue))
                    if (g.VersionKey == versionKey) arranged.Add(g);
                if (arranged.Count > OnlineConfig.MaxListed) arranged.RemoveRange(OnlineConfig.MaxListed, arranged.Count - OnlineConfig.MaxListed);
                m_LastList = arranged;
                return (new List<OnlineGame>(arranged), false, OnlineProblem.None);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Online] Lobby query: " + OnlineErrors.Describe(e));
                OnlineProblem p = OnlineErrors.Classify(e);
                return (new List<OnlineGame>(m_LastList), false, p == OnlineProblem.None ? OnlineProblem.Failed : p);
            }
        }

        /// <summary>Joins the game's lobby, reads its Relay join code and leaves again.</summary>
        public async Task<(string code, OnlineProblem problem)> JoinForCodeAsync(OnlineGame game)
        {
            if (game == null || string.IsNullOrEmpty(game.LobbyId)) return (null, OnlineProblem.GameGone);
            IOnlineBackend backend = m_Backend();
            if (backend == null) return (null, OnlineProblem.NotSetUp);
            try
            {
                string code = await OnlineCalls.WithTimeout(backend.JoinLobbyForCodeAsync(game.LobbyId));
                if (string.IsNullOrEmpty(code)) return (null, OnlineProblem.GameGone);
                return (code, OnlineProblem.None);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Online] Lobby join: " + OnlineErrors.Describe(e));
                OnlineProblem p = OnlineErrors.Classify(e);
                return (null, p == OnlineProblem.None ? OnlineProblem.Failed : p);
            }
        }
    }

    /// <summary>Timeouts for service calls that never answer.</summary>
    public static class OnlineCalls
    {
        public static async Task WithTimeout(Task call, double seconds = OnlineConfig.CallTimeoutSeconds)
        {
            if (call == null) throw new ArgumentNullException(nameof(call));
            if (!call.IsCompleted)
            {
                Task done = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(seconds)));
                if (done != call)
                {
                    OnlineServices.Observe(call);
                    throw new TimeoutException("no answer after " + seconds + " s");
                }
            }
            await call;
        }

        public static async Task<T> WithTimeout<T>(Task<T> call, double seconds = OnlineConfig.CallTimeoutSeconds)
        {
            if (call == null) throw new ArgumentNullException(nameof(call));
            if (!call.IsCompleted)
            {
                Task done = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(seconds)));
                if (done != call)
                {
                    OnlineServices.Observe(call);
                    throw new TimeoutException("no answer after " + seconds + " s");
                }
            }
            return await call;
        }
    }
}

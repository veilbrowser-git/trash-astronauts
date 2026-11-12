using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Net.Online;
using Unity.Networking.Transport.Relay;
using Unity.Services.Core;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// A scripted Unity Gaming Services stand-in: no network. Each call is counted; a test can make a call throw
    /// (a RequestFailedException with a service error code) or hang (a TaskCompletionSource it completes later).
    /// Lobbies live in a list shared by every FakeOnlineBackend made from the same <see cref="FakeCloud"/>.
    /// </summary>
    internal sealed class FakeCloud
    {
        public sealed class Lobby
        {
            public string Id;
            public string Host;
            public OnlineGameInfo Info;
            public string Code;
            public DateTime Created;
            public int Heartbeats;
            public int Updates;
            public bool Deleted;
        }

        public readonly List<Lobby> Lobbies = new List<Lobby>();
        public readonly Dictionary<string, string> Allocations = new Dictionary<string, string>(); // join code -> host player
        public DateTime Clock = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        int m_Next = 1;

        public string NextCode()
        {
            const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            int n = m_Next++;
            char[] c = new char[6];
            for (int i = 0; i < 6; i++) { c[i] = letters[(n * 7 + i * 13) % letters.Length]; }
            return new string(c);
        }

        public string NextId() => "lobby-" + (m_Next++);
    }

    internal sealed class FakeOnlineBackend : IOnlineBackend
    {
        readonly FakeCloud m_Cloud;
        readonly string m_Player;
        bool m_SignedIn;

        public readonly Dictionary<string, int> Calls = new Dictionary<string, int>();
        /// <summary>Calls named here throw RequestFailedException(code).</summary>
        public readonly Dictionary<string, int> FailWith = new Dictionary<string, int>();
        /// <summary>Calls named here wait for the test to complete the TaskCompletionSource.</summary>
        public readonly Dictionary<string, TaskCompletionSource<bool>> Hold = new Dictionary<string, TaskCompletionSource<bool>>();

        public FakeOnlineBackend(FakeCloud cloud, string player)
        {
            m_Cloud = cloud;
            m_Player = player;
        }

        public bool IsSignedIn => m_SignedIn;
        public string PlayerId => m_SignedIn ? m_Player : null;

        public int Count(string call) => Calls.TryGetValue(call, out int n) ? n : 0;

        async Task Enter(string call)
        {
            Calls[call] = Count(call) + 1;
            if (Hold.TryGetValue(call, out TaskCompletionSource<bool> hold)) await hold.Task;
            if (FailWith.TryGetValue(call, out int code)) throw new RequestFailedException(code, "scripted failure of " + call);
        }

        public async Task InitializeAndSignInAsync()
        {
            await Enter("SignIn");
            m_SignedIn = true;
        }

        public async Task<RelayHostGrant> CreateRelayAsync(int peers)
        {
            await Enter("CreateRelay");
            if (peers != NetProtocol.MaxPlayers - 1) throw new ArgumentException("peers " + peers);
            string code = m_Cloud.NextCode();
            m_Cloud.Allocations[code] = m_Player;
            return new RelayHostGrant(default(RelayServerData), code);
        }

        public async Task<RelayServerData> JoinRelayAsync(string joinCode)
        {
            await Enter("JoinRelay");
            if (!m_Cloud.Allocations.ContainsKey(joinCode)) throw new RequestFailedException(15009, "join code not found");
            return default(RelayServerData);
        }

        public async Task<string> CreateLobbyAsync(OnlineGameInfo info, string joinCode)
        {
            await Enter("CreateLobby");
            var lobby = new FakeCloud.Lobby { Id = m_Cloud.NextId(), Host = m_Player, Info = info, Code = joinCode, Created = m_Cloud.Clock };
            m_Cloud.Clock = m_Cloud.Clock.AddSeconds(1);
            m_Cloud.Lobbies.Add(lobby);
            return lobby.Id;
        }

        FakeCloud.Lobby Find(string id)
        {
            foreach (FakeCloud.Lobby l in m_Cloud.Lobbies)
                if (l.Id == id && !l.Deleted) return l;
            throw new RequestFailedException(16001, "lobby not found");
        }

        public async Task UpdateLobbyAsync(string lobbyId, int players, string phaseKey)
        {
            await Enter("UpdateLobby");
            FakeCloud.Lobby l = Find(lobbyId);
            l.Info.players = players;
            l.Info.phaseKey = phaseKey;
            l.Updates++;
        }

        public async Task HeartbeatLobbyAsync(string lobbyId)
        {
            await Enter("Heartbeat");
            Find(lobbyId).Heartbeats++;
        }

        public async Task DeleteLobbyAsync(string lobbyId)
        {
            await Enter("DeleteLobby");
            Find(lobbyId).Deleted = true;
        }

        public async Task<List<OnlineGame>> QueryLobbiesAsync(string versionKey, int max)
        {
            await Enter("Query");
            var games = new List<OnlineGame>();
            foreach (FakeCloud.Lobby l in m_Cloud.Lobbies)
            {
                if (l.Deleted) continue;
                FakeCloud.Lobby lobby = l;
                OnlineGame g = OnlineGame.FromLobby(l.Id, OnlineGame.LobbyName(l.Info.tugNumber), l.Created, l.Info.maxPlayers, 1, key =>
                {
                    switch (key)
                    {
                        case OnlineGame.KeyVersion: return lobby.Info.versionKey;
                        case OnlineGame.KeyPhase: return lobby.Info.phaseKey;
                        case OnlineGame.KeyCrew: return lobby.Info.players.ToString();
                        default: return null;
                    }
                }, versionKey);
                if (g != null) games.Add(g);
            }
            return games;
        }

        public async Task<string> JoinLobbyForCodeAsync(string lobbyId)
        {
            await Enter("JoinLobby");
            FakeCloud.Lobby l = Find(lobbyId);
            if (l.Info.players >= l.Info.maxPlayers) throw new RequestFailedException(16004, "lobby full");
            return l.Code;
        }
    }

    /// <summary>A Relay stand-in: hands out MemoryTransports on one hub, the host at port 47800.</summary>
    internal sealed class FakeRelayConnector : IRelayConnector
    {
        public readonly MemoryTransportHub Hub = new MemoryTransportHub(5);
        public OnlineProblem FailHost;
        public OnlineProblem FailClient;
        public int Hosts, Clients;
        public MemoryTransport LastHost;

        public Task<(INetTransport transport, OnlineProblem problem)> OpenHostAsync(RelayServerData serverData)
        {
            Hosts++;
            if (FailHost != OnlineProblem.None) return Task.FromResult<(INetTransport, OnlineProblem)>((null, FailHost));
            LastHost = Hub.Create(NetProtocol.HostPort);
            return Task.FromResult<(INetTransport, OnlineProblem)>((LastHost, OnlineProblem.None));
        }

        public Task<(INetTransport transport, NetEndpoint host, OnlineProblem problem)> OpenClientAsync(RelayServerData serverData)
        {
            Clients++;
            if (FailClient != OnlineProblem.None) return Task.FromResult<(INetTransport, NetEndpoint, OnlineProblem)>((null, default(NetEndpoint), FailClient));
            MemoryTransport t = Hub.Create();
            NetEndpoint host = LastHost != null ? LastHost.Local : default(NetEndpoint);
            return Task.FromResult<(INetTransport, NetEndpoint, OnlineProblem)>((t, host, OnlineProblem.None));
        }
    }
}

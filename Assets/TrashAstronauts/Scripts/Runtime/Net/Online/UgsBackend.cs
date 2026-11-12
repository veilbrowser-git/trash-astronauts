using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Core.Environments;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;

namespace TrashAstronauts.Net.Online
{
    /// <summary>
    /// The real <see cref="IOnlineBackend"/>: Unity Services Core, anonymous Authentication, Relay and Lobby from
    /// com.unity.services.multiplayer. Nothing here runs until a method is called; constructing it touches nothing.
    ///
    /// The default instance uses UnityServices.Instance. A "separate" instance creates its own services registry
    /// with its own Authentication profile (a second anonymous player in the same process: used by the online
    /// integration test, which hosts and joins from one editor).
    /// </summary>
    public sealed class UgsBackend : IOnlineBackend
    {
        readonly string m_Profile;
        readonly bool m_Separate;
        IUnityServices m_Services;
        IAuthenticationService m_Auth;
        IRelayService m_Relay;
        ILobbyService m_Lobby;

        /// <param name="profile">Authentication profile (letters, digits, - and _; at most 30). Null for the default.</param>
        /// <param name="separateInstance">True: its own services registry instead of UnityServices.Instance.</param>
        public UgsBackend(string profile = null, bool separateInstance = false)
        {
            m_Profile = profile;
            m_Separate = separateInstance;
        }

        public bool IsSignedIn => m_Auth != null && m_Auth.IsSignedIn;
        public string PlayerId => m_Auth != null ? m_Auth.PlayerId : null;

        public async Task InitializeAndSignInAsync()
        {
            if (m_Services == null)
            {
                if (m_Separate)
                {
                    string id = "ta-" + (m_Profile ?? "second");
                    m_Services = UnityServices.Services.TryGetValue(id, out IUnityServices existing) ? existing : UnityServices.CreateServices(id);
                }
                else
                {
                    m_Services = UnityServices.Instance;
                }
                if (m_Services == null) throw new ServicesInitializationException("Unity Services core is not loaded (not in play mode?)");
            }

            if (m_Services.State != ServicesInitializationState.Initialized)
            {
                var options = new InitializationOptions();
                options.SetEnvironmentName(OnlineConfig.Environment);
                if (!string.IsNullOrEmpty(m_Profile)) options.SetProfile(m_Profile);
                await m_Services.InitializeAsync(options);
            }

            m_Auth = m_Services.GetAuthenticationService();
            m_Relay = m_Services.GetRelayService();
            m_Lobby = m_Services.GetLobbyService();
            if (m_Auth == null || m_Relay == null || m_Lobby == null)
                throw new ServicesInitializationException("Authentication, Relay or Lobby is missing from the services registry");
            if (!m_Auth.IsSignedIn) await m_Auth.SignInAnonymouslyAsync();
        }

        void RequireSignedIn()
        {
            if (!IsSignedIn) throw new InvalidOperationException("not signed in");
        }

        public async Task<RelayHostGrant> CreateRelayAsync(int peers)
        {
            RequireSignedIn();
            Allocation allocation = await m_Relay.CreateAllocationAsync(peers);
            string code = await m_Relay.GetJoinCodeAsync(allocation.AllocationId);
            return new RelayHostGrant(allocation.ToRelayServerData(OnlineConfig.RelayConnectionType), code);
        }

        public async Task<RelayServerData> JoinRelayAsync(string joinCode)
        {
            RequireSignedIn();
            JoinAllocation join = await m_Relay.JoinAllocationAsync(joinCode);
            return join.ToRelayServerData(OnlineConfig.RelayConnectionType);
        }

        public async Task<string> CreateLobbyAsync(OnlineGameInfo info, string joinCode)
        {
            RequireSignedIn();
            var options = new CreateLobbyOptions
            {
                IsPrivate = false,
                Data = new Dictionary<string, DataObject>
                {
                    [OnlineGame.KeyVersion] = new DataObject(DataObject.VisibilityOptions.Public, info.versionKey, DataObject.IndexOptions.S1),
                    [OnlineGame.KeyPhase] = new DataObject(DataObject.VisibilityOptions.Public, info.phaseKey, DataObject.IndexOptions.S2),
                    [OnlineGame.KeyCrew] = new DataObject(DataObject.VisibilityOptions.Public, Num(info.players), DataObject.IndexOptions.N1),
                    [OnlineGame.KeyTug] = new DataObject(DataObject.VisibilityOptions.Public, Num(info.tugNumber)),
                    // Only lobby members read the Relay code: a joiner joins the lobby, reads it, and leaves.
                    [OnlineGame.KeyCode] = new DataObject(DataObject.VisibilityOptions.Member, joinCode),
                },
            };
            Lobby lobby = await m_Lobby.CreateLobbyAsync(OnlineGame.LobbyName(info.tugNumber), info.maxPlayers, options);
            return lobby.Id;
        }

        public async Task UpdateLobbyAsync(string lobbyId, int players, string phaseKey)
        {
            RequireSignedIn();
            var options = new UpdateLobbyOptions
            {
                Data = new Dictionary<string, DataObject>
                {
                    [OnlineGame.KeyPhase] = new DataObject(DataObject.VisibilityOptions.Public, phaseKey, DataObject.IndexOptions.S2),
                    [OnlineGame.KeyCrew] = new DataObject(DataObject.VisibilityOptions.Public, Num(players), DataObject.IndexOptions.N1),
                },
            };
            await m_Lobby.UpdateLobbyAsync(lobbyId, options);
        }

        public Task HeartbeatLobbyAsync(string lobbyId)
        {
            RequireSignedIn();
            return m_Lobby.SendHeartbeatPingAsync(lobbyId);
        }

        public Task DeleteLobbyAsync(string lobbyId)
        {
            RequireSignedIn();
            return m_Lobby.DeleteLobbyAsync(lobbyId);
        }

        public async Task<List<OnlineGame>> QueryLobbiesAsync(string versionKey, int max)
        {
            RequireSignedIn();
            var options = new QueryLobbiesOptions
            {
                Count = Math.Max(1, max),
                Filters = new List<QueryFilter>
                {
                    new QueryFilter(QueryFilter.FieldOptions.S1, versionKey, QueryFilter.OpOptions.EQ),
                    new QueryFilter(QueryFilter.FieldOptions.S2, "ACTIVE", QueryFilter.OpOptions.NE),
                    new QueryFilter(QueryFilter.FieldOptions.AvailableSlots, "0", QueryFilter.OpOptions.GT),
                },
                Order = new List<QueryOrder> { new QueryOrder(false, QueryOrder.FieldOptions.Created) },
            };
            QueryResponse response = await m_Lobby.QueryLobbiesAsync(options);
            var games = new List<OnlineGame>();
            if (response?.Results == null) return games;
            foreach (Lobby lobby in response.Results)
            {
                if (lobby == null) continue;
                Dictionary<string, DataObject> data = lobby.Data;
                OnlineGame game = OnlineGame.FromLobby(lobby.Id, lobby.Name, lobby.Created, lobby.MaxPlayers, lobby.Players?.Count ?? 1,
                    key => data != null && data.TryGetValue(key, out DataObject d) ? d?.Value : null, versionKey);
                if (game != null) games.Add(game);
            }
            return games;
        }

        public async Task<string> JoinLobbyForCodeAsync(string lobbyId)
        {
            RequireSignedIn();
            Lobby lobby = await m_Lobby.JoinLobbyByIdAsync(lobbyId);
            string code = null;
            if (lobby?.Data != null && lobby.Data.TryGetValue(OnlineGame.KeyCode, out DataObject d)) code = d?.Value;
            // Leave the lobby straight away: the crew is counted by the host's session, and a crashed joiner must not
            // hold a lobby seat. Failing to leave is harmless (the host deletes the lobby when it closes).
            try { await m_Lobby.RemovePlayerAsync(lobbyId, m_Auth.PlayerId); }
            catch (Exception) { }
            if (string.IsNullOrEmpty(code)) throw new RequestFailedException(16001, "the lobby carries no join code");
            return code;
        }

        static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}

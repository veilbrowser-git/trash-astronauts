using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using TrashAstronauts.Core.Net;
using Unity.Networking.Transport.Relay;

namespace TrashAstronauts.Net.Online
{
    /// <summary>
    /// Every call this module makes to Unity Gaming Services, in one seam. <see cref="UgsBackend"/> is the real
    /// one; tests use a scripted fake. Methods may throw (the callers classify with <see cref="OnlineErrors"/>).
    /// </summary>
    public interface IOnlineBackend
    {
        bool IsSignedIn { get; }
        string PlayerId { get; }

        /// <summary>UnityServices.InitializeAsync (production environment) then anonymous sign-in. Idempotent.</summary>
        Task InitializeAndSignInAsync();

        /// <summary>A Relay allocation for <paramref name="peers"/> other players and its join code.</summary>
        Task<RelayHostGrant> CreateRelayAsync(int peers);

        /// <summary>Joins the allocation behind a join code.</summary>
        Task<RelayServerData> JoinRelayAsync(string joinCode);

        /// <summary>Publishes a public lobby; returns its id.</summary>
        Task<string> CreateLobbyAsync(OnlineGameInfo info, string joinCode);

        Task UpdateLobbyAsync(string lobbyId, int players, string phaseKey);
        Task HeartbeatLobbyAsync(string lobbyId);
        Task DeleteLobbyAsync(string lobbyId);

        /// <summary>Open games for <paramref name="versionKey"/>, newest first, at most <paramref name="max"/>.</summary>
        Task<List<OnlineGame>> QueryLobbiesAsync(string versionKey, int max);

        /// <summary>Joins the lobby, reads the member-only Relay join code, and leaves the lobby again.</summary>
        Task<string> JoinLobbyForCodeAsync(string lobbyId);
    }

    /// <summary>What a host's Relay allocation hands back: the transport configuration and the 6-character join code.</summary>
    public sealed class RelayHostGrant
    {
        public readonly RelayServerData ServerData;
        public readonly string JoinCode;

        public RelayHostGrant(RelayServerData serverData, string joinCode)
        {
            ServerData = serverData;
            JoinCode = joinCode;
        }
    }

    /// <summary>What a host publishes about its game.</summary>
    public struct OnlineGameInfo
    {
        public ushort tugNumber;
        public string versionKey;
        public int players;
        public int maxPlayers;
        public string phaseKey;
    }

    /// <summary>One line of the FIND list.</summary>
    public sealed class OnlineGame
    {
        public const string KeyVersion = "ver";
        public const string KeyPhase = "phase";
        public const string KeyCrew = "crew";
        public const string KeyTug = "tug";
        public const string KeyCode = "code";

        public string LobbyId;
        public ushort TugNumber;
        public int Players;
        public int MaxPlayers;
        public string PhaseKey;
        public string VersionKey;
        public DateTime CreatedUtc;

        /// <summary>"TUG 4821  2/4  IN HUB"</summary>
        public string Line => "TUG " + TugNumber + "  " + Players + "/" + MaxPlayers + "  " + OnlineConfig.PhaseText(PhaseKey);

        public bool Joinable => Players < MaxPlayers && PhaseKey != "ACTIVE";

        /// <summary>"TUG 4821" back to 4821; 0 when the name is something else.</summary>
        public static ushort ParseTug(string lobbyName)
        {
            if (string.IsNullOrEmpty(lobbyName) || !lobbyName.StartsWith("TUG ", StringComparison.Ordinal)) return 0;
            return ushort.TryParse(lobbyName.Substring(4), NumberStyles.None, CultureInfo.InvariantCulture, out ushort tug) ? tug : (ushort)0;
        }

        public static string LobbyName(ushort tugNumber) => "TUG " + tugNumber.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Builds an entry from a lobby's public fields. Null when it is not a game of this build (wrong or missing
        /// version) or the fields do not parse: whatever is in the lobby list came from other clients.
        /// </summary>
        public static OnlineGame FromLobby(string lobbyId, string name, DateTime createdUtc, int maxPlayers, int memberCount,
            Func<string, string> field, string expectedVersion)
        {
            if (string.IsNullOrEmpty(lobbyId) || field == null) return null;
            string version = field(KeyVersion);
            if (expectedVersion != null && version != expectedVersion) return null;
            ushort tug = ParseTug(name);
            if (tug == 0 && ushort.TryParse(field(KeyTug), NumberStyles.None, CultureInfo.InvariantCulture, out ushort t)) tug = t;
            if (!int.TryParse(field(KeyCrew), NumberStyles.None, CultureInfo.InvariantCulture, out int crew)) crew = memberCount;
            int max = maxPlayers > 0 ? Math.Min(maxPlayers, NetProtocol.MaxPlayers) : NetProtocol.MaxPlayers;
            crew = Math.Max(1, Math.Min(crew, max));
            string phase = field(KeyPhase);
            if (phase != "HUB" && phase != "RESULTS" && phase != "ACTIVE") phase = "HUB";
            return new OnlineGame
            {
                LobbyId = lobbyId,
                TugNumber = tug,
                Players = crew,
                MaxPlayers = max,
                PhaseKey = phase,
                VersionKey = version,
                CreatedUtc = createdUtc,
            };
        }

        /// <summary>Newest first, joinable only, at most <paramref name="max"/>: the list FIND shows.</summary>
        public static List<OnlineGame> Arrange(List<OnlineGame> games, int max)
        {
            var result = new List<OnlineGame>();
            if (games == null) return result;
            for (int i = 0; i < games.Count; i++)
                if (games[i] != null && games[i].Joinable) result.Add(games[i]);
            result.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
            if (result.Count > max) result.RemoveRange(max, result.Count - max);
            return result;
        }
    }

    /// <summary>Opens the INetTransport behind a Relay grant. <see cref="RelayConnector"/> is the real one.</summary>
    public interface IRelayConnector
    {
        /// <summary>Binds as the allocation's host and waits for Relay to confirm. Null and a problem on failure.</summary>
        Task<(INetTransport transport, OnlineProblem problem)> OpenHostAsync(RelayServerData serverData);

        /// <summary>Connects to the host through Relay and waits for the connection. Null and a problem on failure.</summary>
        Task<(INetTransport transport, NetEndpoint host, OnlineProblem problem)> OpenClientAsync(RelayServerData serverData);
    }

    /// <summary>Normalises a typed join code: 6 letters/digits, upper case. Null when it is not one.</summary>
    public static class JoinCodes
    {
        public const int Length = 6;

        public static string Normalize(string code)
        {
            if (code == null) return null;
            code = code.Trim().ToUpperInvariant();
            if (code.Length != Length) return null;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return null;
            }
            return code;
        }
    }
}

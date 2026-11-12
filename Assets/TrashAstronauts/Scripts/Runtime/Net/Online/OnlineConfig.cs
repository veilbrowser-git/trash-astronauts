using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;

namespace TrashAstronauts.Net.Online
{
    /// <summary>
    /// The Unity Cloud project this game talks to when (and only when) the player chooses an online game.
    /// These identifiers are not secrets: every build carries the project id in Application.cloudProjectId.
    /// The Builder writes them into ProjectSettings (Builder.Online.cs), so batch builds keep them.
    /// </summary>
    public static class OnlineConfig
    {
        public const string CloudProjectId = "7b4c2047-35ae-40b4-8cd2-55b31c92d3ec";
        public const string OrganizationId = "apkgtmw1h4372";
        public const string ProjectName = "Trash Astronauts";
        public const string Environment = "production";

        /// <summary>Relay connection type. DTLS: encrypted UDP, supported on Android/Quest.</summary>
        public const string RelayConnectionType = "dtls";

        /// <summary>Other players a host's Relay allocation admits (the host itself is not counted).</summary>
        public const int RelayPeers = NetProtocol.MaxPlayers - 1;

        /// <summary>Most games FIND lists (newest first).</summary>
        public const int MaxListed = 5;

        /// <summary>The lobby list is asked for at most this often; sooner calls get the previous answer.</summary>
        public const double ListRefreshSeconds = 2.0;

        /// <summary>The Lobby service drops a lobby after 30 s without a heartbeat.</summary>
        public const double HeartbeatSeconds = 15.0;

        /// <summary>Crew/phase changes are pushed to the lobby at most this often (the Lobby update limit is 5 per 5 s).</summary>
        public const double LobbyUpdateSeconds = 2.0;

        /// <summary>A service call that has not answered after this long is given up on (NO ANSWER).</summary>
        public const double CallTimeoutSeconds = 20.0;

        /// <summary>How long the host waits for Relay to confirm its binding, and a joiner for its connection.</summary>
        public const double RelayReadySeconds = 10.0;

        /// <summary>
        /// The key that keeps different builds apart in the lobby list: protocol version and content hash.
        /// Two builds with different keys would refuse each other's Hello anyway; this hides them from each other.
        /// </summary>
        public static string VersionKey(byte protocol, uint contentHash) => "p" + protocol + "-" + contentHash.ToString("x8");

        public static string PhaseKey(MissionPhase phase)
        {
            switch (phase)
            {
                case MissionPhase.Hub: return "HUB";
                case MissionPhase.Results: return "RESULTS";
                default: return "ACTIVE";
            }
        }

        /// <summary>The word shown on a FIND result line.</summary>
        public static string PhaseText(string phaseKey)
        {
            switch (phaseKey)
            {
                case "HUB": return "IN HUB";
                case "RESULTS": return "RESULTS";
                case "ACTIVE": return "ON CONTRACT";
                default: return "";
            }
        }
    }
}

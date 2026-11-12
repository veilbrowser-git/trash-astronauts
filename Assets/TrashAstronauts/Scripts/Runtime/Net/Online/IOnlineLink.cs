using System.Threading.Tasks;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;

namespace TrashAstronauts.Net.Online
{
    /// <summary>
    /// What the crew panel's session needs from an online link: <see cref="OnlineLink"/> is the real one (Unity
    /// Relay + Lobby + anonymous sign-in); tests hand NetSession a fake through NetSession.OnlineLinkFactory.
    /// Same contract as OnlineLink: async on the main thread, never throws, a result with Ok == false carries an
    /// <see cref="OnlineProblem"/>, and a transport handed back belongs to the caller.
    /// </summary>
    public interface IOnlineLink
    {
        OnlineLinkState State { get; }
        OnlineProblem Problem { get; }
        /// <summary>One line for the board ("SIGNING IN...", "TUG 4821  CODE ABC123", "NO INTERNET" ...).</summary>
        string StatusText { get; }
        ushort TugNumber { get; }
        string Code { get; }
        /// <summary>Hosting: the game is in the lobby list right now (false once the listing is gone or failed).</summary>
        bool IsListed { get; }

        Task<OnlineHostResult> HostAsync(ushort tugNumber, string versionKey, MissionPhase phase, int playerCount = 1);
        Task<OnlineFindResult> FindAsync(string versionKey);
        Task<OnlineJoinResult> JoinAsync(OnlineGame game);
        Task<OnlineJoinResult> JoinByCodeAsync(string code);

        /// <summary>The crew count or phase changed (pushed to the lobby listing, rate-limited by the link).</summary>
        void UpdateHosting(int playerCount, MissionPhase phase);
        /// <summary>Every frame while hosting: lobby heartbeat and pending updates.</summary>
        void Tick();
        /// <summary>Stops whatever is running and unlists a hosted game. Safe any time.</summary>
        void Leave();
    }

    /// <summary>
    /// The health of an online (Relay) connection, read by NetSession every frame while a session runs over it:
    /// RelayTransport is the real one; tests use a fake. A plain UDP or in-memory transport does not implement it.
    /// </summary>
    public interface IRelayLink
    {
        bool IsHost { get; }
        /// <summary>Client: the connection to the host is gone.</summary>
        bool LinkLost { get; }
        /// <summary>Relay says the allocation no longer exists (expired, or the host's side went away).</summary>
        bool AllocationInvalid { get; }
        /// <summary>The connection to the Relay server itself was up and is gone.</summary>
        bool RelayLost { get; }
        /// <summary>Host: connections that dropped so far. Client: 1 once the link was lost.</summary>
        int Disconnects { get; }
        /// <summary>Host: connected clients. Client: 1 while connected.</summary>
        int ConnectionCount { get; }
        /// <summary>Puts queued sends on the wire now.</summary>
        void Flush();
        /// <summary>Host: the next peer whose connection dropped since the last call (its session endpoint). False when none.</summary>
        bool TryTakeDropped(out NetEndpoint peer);
    }
}

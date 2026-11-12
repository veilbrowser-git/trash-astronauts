using System.Threading.Tasks;
using TrashAstronauts.Core;

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
}

using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace TrashAstronauts.Net.Online
{
    public enum OnlineServiceState : byte
    {
        /// <summary>Nothing has been asked: no Unity Services call has been made.</summary>
        Off = 0,
        /// <summary>Initialising Unity Services and signing in.</summary>
        Starting = 1,
        /// <summary>Signed in anonymously; Relay and Lobby can be used.</summary>
        Ready = 2,
        /// <summary>The last attempt failed (see <see cref="OnlineServices.Problem"/>); StartAsync tries again.</summary>
        Failed = 3,
    }

    /// <summary>
    /// Unity Gaming Services start-up for the game: Initialize (production environment) and anonymous sign-in,
    /// with a state and a one-line status a lobby board can show.
    ///
    /// Rules: it does nothing until <see cref="StartAsync"/> is called (an offline player never contacts Unity);
    /// it never throws into game code; it never blocks the main thread (everything is async, continuations come
    /// back on the main thread through Unity's synchronisation context); calling StartAsync again while it runs
    /// returns the same task, and after success it returns true at once.
    /// </summary>
    public sealed class OnlineServices
    {
        static OnlineServices s_Shared;
        static int s_StartRequests;

        /// <summary>The game's instance. Creating it does nothing.</summary>
        public static OnlineServices Shared => s_Shared ?? (s_Shared = new OnlineServices());

        /// <summary>
        /// How many times anything in this process asked any OnlineServices to start (and so possibly contact Unity).
        /// The offline guard test asserts this stays 0 when the player never chooses an online game.
        /// </summary>
        public static int StartRequests => Volatile.Read(ref s_StartRequests);

        readonly Func<IOnlineBackend> m_BackendFactory;
        readonly Func<bool> m_InternetReachable;
        readonly Func<string> m_CloudProjectId;
        IOnlineBackend m_Backend;
        Task<bool> m_Starting;

        public OnlineServiceState State { get; private set; }
        public OnlineProblem Problem { get; private set; }
        /// <summary>Exception detail of the last failure, for logs (never shown on the board).</summary>
        public string LastError { get; private set; }
        public string PlayerId => m_Backend != null && m_Backend.IsSignedIn ? m_Backend.PlayerId : null;
        public bool IsReady => State == OnlineServiceState.Ready && m_Backend != null && m_Backend.IsSignedIn;

        /// <summary>Null until the first StartAsync.</summary>
        public IOnlineBackend Backend => m_Backend;

        public string StatusText
        {
            get
            {
                switch (State)
                {
                    case OnlineServiceState.Off: return "OFFLINE";
                    case OnlineServiceState.Starting: return "SIGNING IN...";
                    case OnlineServiceState.Ready: return "ONLINE";
                    default: return OnlineErrors.Text(Problem);
                }
            }
        }

        /// <param name="backend">Creates the backend on the first StartAsync. Default: <see cref="UgsBackend"/> on UnityServices.Instance.</param>
        /// <param name="internetReachable">Default: Application.internetReachability.</param>
        /// <param name="cloudProjectId">Default: Application.cloudProjectId.</param>
        public OnlineServices(Func<IOnlineBackend> backend = null, Func<bool> internetReachable = null, Func<string> cloudProjectId = null)
        {
            m_BackendFactory = backend ?? (() => new UgsBackend());
            m_InternetReachable = internetReachable ?? (() => Application.internetReachability != NetworkReachability.NotReachable);
            m_CloudProjectId = cloudProjectId ?? (() => Application.cloudProjectId);
        }

        /// <summary>A second anonymous player in this process (own services registry and Authentication profile).</summary>
        public static OnlineServices CreateSeparate(string profile)
        {
            return new OnlineServices(() => new UgsBackend(profile, separateInstance: true));
        }

        /// <summary>
        /// Initialises Unity Services and signs in anonymously. True when signed in. Never throws.
        /// </summary>
        public Task<bool> StartAsync()
        {
            if (IsReady) return Task.FromResult(true);
            if (m_Starting != null && !m_Starting.IsCompleted) return m_Starting;
            Interlocked.Increment(ref s_StartRequests);
            m_Starting = StartCore();
            return m_Starting;
        }

        async Task<bool> StartCore()
        {
            State = OnlineServiceState.Starting;
            Problem = OnlineProblem.None;
            LastError = null;
            try
            {
                if (!SafeBool(m_InternetReachable)) return Fail(OnlineProblem.NoInternet, "device reports no internet");
                string projectId = SafeString(m_CloudProjectId);
                if (projectId != OnlineConfig.CloudProjectId)
                    return Fail(OnlineProblem.NotSetUp, "cloud project id is '" + projectId + "', expected " + OnlineConfig.CloudProjectId);

                if (m_Backend == null) m_Backend = m_BackendFactory();
                await OnlineCalls.WithTimeout(m_Backend.InitializeAndSignInAsync()); // a timeout throws TimeoutException: NO ANSWER
                if (!m_Backend.IsSignedIn) return Fail(OnlineProblem.SignInFailed, "not signed in after sign-in");
                State = OnlineServiceState.Ready;
                return true;
            }
            catch (Exception e)
            {
                return Fail(OnlineErrors.Classify(e, signingIn: true), OnlineErrors.Describe(e));
            }
        }

        bool Fail(OnlineProblem problem, string detail)
        {
            State = OnlineServiceState.Failed;
            Problem = problem == OnlineProblem.None ? OnlineProblem.Failed : problem;
            LastError = detail;
            Debug.LogWarning("[Online] " + OnlineErrors.Text(Problem) + ": " + detail);
            return false;
        }

        static bool SafeBool(Func<bool> f)
        {
            try { return f(); }
            catch (Exception) { return false; }
        }

        static string SafeString(Func<string> f)
        {
            try { return f() ?? ""; }
            catch (Exception) { return ""; }
        }

        /// <summary>Marks a task's exception as observed so an abandoned call does not log an unobserved exception later.</summary>
        internal static void Observe(Task task)
        {
            if (task == null) return;
            task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
    }
}

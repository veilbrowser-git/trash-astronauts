using System;
using System.Threading.Tasks;
using Unity.Services.Core;

namespace TrashAstronauts.Net.Online
{
    /// <summary>Why an online step did not work, in words a lobby board can show.</summary>
    public enum OnlineProblem : byte
    {
        None = 0,
        /// <summary>The device has no internet, or a service could not be reached at all.</summary>
        NoInternet = 1,
        /// <summary>Anonymous sign-in was refused.</summary>
        SignInFailed = 2,
        /// <summary>Rate limited, or the service said it is overloaded / temporarily unavailable.</summary>
        ServiceBusy = 3,
        /// <summary>This build is not linked to the cloud project, or the project has the service turned off.</summary>
        NotSetUp = 4,
        /// <summary>The game (lobby, join code or relay allocation) no longer exists.</summary>
        GameGone = 5,
        GameFull = 6,
        /// <summary>A call or the Relay connection did not answer in time.</summary>
        NoAnswer = 7,
        /// <summary>The typed code is not a 6-character join code.</summary>
        BadCode = 8,
        /// <summary>Another online step is still running.</summary>
        Busy = 9,
        /// <summary>LEAVE was pressed while the step was running.</summary>
        Cancelled = 10,
        /// <summary>Anything else (the detail is in the log).</summary>
        Failed = 11,
    }

    /// <summary>Maps service exceptions to <see cref="OnlineProblem"/> and problems to board text. Pure.</summary>
    public static class OnlineErrors
    {
        // Unity Services error codes (Unity.Services.Core.CommonErrorCodes and the per-service enums).
        const int TransportError = 1, Timeout = 2, ServiceUnavailable = 3, ApiMissing = 4, RequestRejected = 5;
        const int TooManyRequests = 50, InvalidToken = 51, TokenExpired = 52, Forbidden = 53, NotFound = 54;
        const int ProjectPolicyAccessDenied = 56, PlayerPolicyAccessDenied = 57;
        const int AuthMin = 10000, AuthMax = 10999;
        const int RelayInactiveProject = 15006, RelayAllocationNotFound = 15008, RelayJoinCodeNotFound = 15009, RelayNoSuitableRelay = 15010;
        const int LobbyNotFound = 16001, LobbyFull = 16004, LobbyLocked = 16005, LobbyInvalidJoinCode = 16010;

        public static string Text(OnlineProblem problem)
        {
            switch (problem)
            {
                case OnlineProblem.None: return "";
                case OnlineProblem.NoInternet: return "NO INTERNET";
                case OnlineProblem.SignInFailed: return "SIGN-IN FAILED";
                case OnlineProblem.ServiceBusy: return "SERVICE BUSY - TRY AGAIN";
                case OnlineProblem.NotSetUp: return "ONLINE NOT SET UP";
                case OnlineProblem.GameGone: return "GAME GONE";
                case OnlineProblem.GameFull: return "CREW FULL";
                case OnlineProblem.NoAnswer: return "NO ANSWER";
                case OnlineProblem.BadCode: return "BAD CODE";
                case OnlineProblem.Busy: return "WAIT...";
                case OnlineProblem.Cancelled: return "";
                default: return "ONLINE ERROR";
            }
        }

        /// <summary>
        /// The problem behind an exception from Unity Services (or a Task that wrapped one). <paramref name="signingIn"/>
        /// turns "refused" answers into SIGN-IN FAILED.
        /// </summary>
        public static OnlineProblem Classify(Exception e, bool signingIn = false)
        {
            while (e is AggregateException agg && agg.InnerExceptions.Count == 1) e = agg.InnerExceptions[0];
            if (e == null) return OnlineProblem.None;
            if (e is TaskCanceledException || e is OperationCanceledException) return OnlineProblem.NoAnswer;
            if (e is TimeoutException) return OnlineProblem.NoAnswer;
            if (e is RequestFailedException rf) return ClassifyCode(rf.ErrorCode, signingIn);
            if (e is ServicesInitializationException) return OnlineProblem.NotSetUp;
            if (e.InnerException != null) return Classify(e.InnerException, signingIn);
            return signingIn ? OnlineProblem.SignInFailed : OnlineProblem.Failed;
        }

        public static OnlineProblem ClassifyCode(int code, bool signingIn = false)
        {
            switch (code)
            {
                case TransportError:
                case Timeout:
                    return OnlineProblem.NoInternet;
                case ServiceUnavailable:
                case TooManyRequests:
                    return OnlineProblem.ServiceBusy;
                case ApiMissing:
                case ProjectPolicyAccessDenied:
                case RelayInactiveProject:
                    return OnlineProblem.NotSetUp;
                case RelayAllocationNotFound:
                case RelayJoinCodeNotFound:
                case LobbyNotFound:
                case LobbyInvalidJoinCode:
                    return OnlineProblem.GameGone;
                case LobbyFull:
                case LobbyLocked:
                    return OnlineProblem.GameFull;
                case RelayNoSuitableRelay:
                    return OnlineProblem.ServiceBusy;
                case InvalidToken:
                case TokenExpired:
                case PlayerPolicyAccessDenied:
                case RequestRejected:
                case Forbidden:
                case NotFound:
                    return signingIn ? OnlineProblem.SignInFailed : OnlineProblem.Failed;
            }
            if (code >= AuthMin && code <= AuthMax) return OnlineProblem.SignInFailed;
            // Per-service ranges: xx998 is NetworkError, xx429 rate limited, xx5xx server trouble.
            int local = code % 1000;
            if (code >= 15000 && code < 17000)
            {
                if (local == 998) return OnlineProblem.NoInternet;
                if (local == 429) return OnlineProblem.ServiceBusy;
                if (local == 408 || local == 504) return OnlineProblem.NoAnswer;
                if (local >= 500 && local < 600) return OnlineProblem.ServiceBusy;
                if (local == 401 || local == 403) return signingIn ? OnlineProblem.SignInFailed : OnlineProblem.NotSetUp;
                if (local == 404) return OnlineProblem.GameGone;
            }
            return signingIn ? OnlineProblem.SignInFailed : OnlineProblem.Failed;
        }

        /// <summary>Short one-line description for logs: type, code and message.</summary>
        public static string Describe(Exception e)
        {
            while (e is AggregateException agg && agg.InnerExceptions.Count == 1) e = agg.InnerExceptions[0];
            if (e == null) return "";
            string code = e is RequestFailedException rf ? " (" + rf.ErrorCode + ")" : "";
            return e.GetType().Name + code + ": " + e.Message;
        }
    }
}

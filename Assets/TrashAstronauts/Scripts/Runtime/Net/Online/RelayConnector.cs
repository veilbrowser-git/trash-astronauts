using System;
using System.Diagnostics;
using System.Threading.Tasks;
using TrashAstronauts.Core.Net;
using Unity.Networking.Transport.Relay;

namespace TrashAstronauts.Net.Online
{
    /// <summary>
    /// The real <see cref="IRelayConnector"/>: opens a <see cref="RelayTransport"/> and pumps it (one update about every
    /// 10 ms, never blocking) until Relay has confirmed the host's binding or the client's connection, or gives up
    /// after <see cref="OnlineConfig.RelayReadySeconds"/>.
    /// </summary>
    public sealed class RelayConnector : IRelayConnector
    {
        readonly double m_TimeoutSeconds;

        public RelayConnector(double timeoutSeconds = OnlineConfig.RelayReadySeconds)
        {
            m_TimeoutSeconds = timeoutSeconds;
        }

        public async Task<(INetTransport transport, OnlineProblem problem)> OpenHostAsync(RelayServerData serverData)
        {
            RelayTransport t = RelayTransport.OpenRelayHost(serverData, out string error);
            if (t == null)
            {
                UnityEngine.Debug.LogWarning("[Online] Relay host transport: " + error);
                return (null, OnlineProblem.Failed);
            }
            OnlineProblem problem = await WaitReady(t);
            if (problem != OnlineProblem.None) { t.Dispose(); return (null, problem); }
            return (t, OnlineProblem.None);
        }

        public async Task<(INetTransport transport, NetEndpoint host, OnlineProblem problem)> OpenClientAsync(RelayServerData serverData)
        {
            RelayTransport t = RelayTransport.OpenRelayClient(serverData, out string error);
            if (t == null)
            {
                UnityEngine.Debug.LogWarning("[Online] Relay client transport: " + error);
                return (null, default(NetEndpoint), OnlineProblem.Failed);
            }
            OnlineProblem problem = await WaitReady(t);
            if (problem != OnlineProblem.None) { t.Dispose(); return (null, default(NetEndpoint), problem); }
            return (t, RelayTransport.HostEndpoint, OnlineProblem.None);
        }

        /// <summary>Pumps until ready. None, NoAnswer (timeout or connection refused) or GameGone (allocation invalid).</summary>
        public async Task<OnlineProblem> WaitReady(RelayTransport t)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                t.Update();
                if (t.IsReady) return OnlineProblem.None;
                if (t.AllocationInvalid) return OnlineProblem.GameGone;
                if (!t.IsHost && t.LinkLost) return OnlineProblem.NoAnswer;
                if (clock.Elapsed.TotalSeconds > m_TimeoutSeconds) return OnlineProblem.NoAnswer;
                await Task.Delay(10);
                if (!t.IsOpen) return OnlineProblem.Cancelled;
            }
        }
    }
}

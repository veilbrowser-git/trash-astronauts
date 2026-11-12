using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Shared set-up for the co-op play tests: the real scene, its NetSession, and test-owned peers (bots and
    /// scripted hosts from NetBots) ticked once per frame. By default every transport comes from one in-memory
    /// hub and the session clock is a fake one the test advances (so 45 s timeouts take a moment); a fixture can
    /// switch to real UDP sockets on 127.0.0.1 with ephemeral ports and the real clock instead.
    /// Tear-down closes the test's own peers, then asserts no UDP socket is left open by the scene.
    /// </summary>
    public abstract class NetPlayFixture
    {
        protected MissionDirector m_Director;
        protected PlayerContext m_Player;
        protected NetSession m_Net;
        protected MemorySaveStorage m_Storage;
        protected MemoryTransportHub m_Hub;
        protected double m_Clock;

        readonly List<Action<double>> m_Tickers = new List<Action<double>>();
        readonly List<IDisposable> m_Owned = new List<IDisposable>();

        /// <summary>False: real UDP on 127.0.0.1 and the real clock.</summary>
        protected virtual bool UseMemoryTransport => true;

        protected double Now => UseMemoryTransport ? m_Clock : NetSession.Now;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            m_Storage = new MemorySaveStorage();
            MissionDirector.StorageOverride = m_Storage;
            MissionDirector.SeedOverride = 44;
            MissionDirector.CreatureOverride = 0;
            MissionDirector.MapOverride = 0;
            MissionDirector.ConditionOverride = ContractCondition.Standard;
            BlackoutController.DamageOverride = 0f;
            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            yield return null;
            yield return null;
            m_Director = MissionDirector.Active;
            m_Player = m_Director.Players.First();
            m_Net = m_Director.GetComponent<NetSession>();
            Assert.IsNotNull(m_Net, "The Mission object carries the NetSession.");
            Assert.AreEqual(0, UdpTransport.LiveCount, "No socket is open before any co-op button is pressed.");

            if (UseMemoryTransport)
            {
                m_Clock = 1000.0;
                m_Hub = new MemoryTransportHub(11);
                NetSession.ClockOverride = () => m_Clock;
                NetSession.TransportFactory = port => port == 0 ? m_Hub.Create() : m_Hub.Create(port);
            }
            else
            {
                NetSession.TransportFactory = port => UdpTransport.TryOpen(NetEndpoint.LoopbackAddress, 0, out string _);
                NetSession.LocalAddressOverride = t => NetEndpoint.LoopbackAddress;
            }
        }

        [TearDown]
        public void TearDown()
        {
            m_Tickers.Clear();
            foreach (var owned in m_Owned)
            {
                try { owned.Dispose(); } catch (Exception) { }
            }
            m_Owned.Clear();
            int live = UdpTransport.LiveCount;
            // Clean up whatever a failed test left behind, so the next test starts closed either way.
            if (m_Net != null) m_Net.ShutDown();
            NetSession.TransportFactory = null;
            NetSession.ClockOverride = null;
            NetSession.LocalAddressOverride = null;
            if (MissionDirector.Active != null)
            {
                MissionDirector.Active.Net = null;
                MissionDirector.Active.ContractStartBlocked = false;
            }
            MissionDirector.StorageOverride = null;
            MissionDirector.SeedOverride = null;
            MissionDirector.CreatureOverride = null;
            MissionDirector.MapOverride = null;
            MissionDirector.ConditionOverride = null;
            BlackoutController.DamageOverride = null;
            Assert.AreEqual(0, live, "Every UDP socket must be closed when a co-op test ends.");
        }

        // ------------------------------------------------------------------ stepping

        /// <summary>Advances the fake clock (memory mode) and ticks every test peer once.</summary>
        protected void Tick(double dt = 0.02)
        {
            if (UseMemoryTransport)
            {
                m_Clock += dt;
                m_Hub.Advance(dt);
            }
            double now = Now;
            for (int i = 0; i < m_Tickers.Count; i++) m_Tickers[i](now);
        }

        protected IEnumerator Frames(int count, double dt = 0.02)
        {
            for (int i = 0; i < count; i++)
            {
                Tick(dt);
                yield return null;
            }
        }

        /// <summary>Ticks frame by frame until <paramref name="done"/> or the limits (frames, and real seconds).</summary>
        protected IEnumerator Until(Func<bool> done, int maxFrames = 1500, double dt = 0.02, float maxRealSeconds = 10f)
        {
            float start = Time.realtimeSinceStartup;
            for (int i = 0; i < maxFrames && !done() && Time.realtimeSinceStartup - start < maxRealSeconds; i++)
            {
                Tick(dt);
                yield return null;
            }
        }

        // ------------------------------------------------------------------ test peers

        protected sealed class Bot
        {
            public NetSessionClient Session;
            public BotClientWorld World;
            public AvatarSnapshot Avatar = AvatarSnapshot.Default;
            /// <summary>A frozen bot is not ticked at all: a headset that went to sleep.</summary>
            public bool Frozen;
        }

        protected INetTransport NewTransport(ushort port = 0)
        {
            INetTransport t = UseMemoryTransport
                ? (port == 0 ? (INetTransport)m_Hub.Create() : m_Hub.Create(port))
                : UdpTransport.TryOpen(NetEndpoint.LoopbackAddress, 0, out string _);
            Assert.IsNotNull(t, "Test set-up: could not open a transport.");
            return t;
        }

        /// <summary>A scene-less client (NetBots) joining <paramref name="host"/>, ticked every frame.</summary>
        protected Bot AddBot(NetEndpoint host, byte handedness = 0, uint? contentHash = null)
        {
            var bot = new Bot { World = new BotClientWorld() };
            bot.Session = new NetSessionClient(NewTransport(), bot.World, new NetClientConfig
            {
                contentHash = contentHash ?? m_Net.ContentHash,
                handedness = handedness,
                seed = 500 + m_Owned.Count,
            });
            bot.World.Session = bot.Session;
            m_Owned.Add(bot.Session);
            bot.Avatar.headPosition = new NetVec3(0.5f, 1.6f, -1.5f);
            bot.Avatar.leftHandPosition = new NetVec3(0.3f, 1.2f, -1.3f);
            bot.Avatar.rightHandPosition = new NetVec3(0.7f, 1.2f, -1.3f);
            m_Tickers.Add(now =>
            {
                if (bot.Frozen) return;
                bot.World.Now = now;
                bot.Session.Poll(now);
                bot.Session.SetLocalAvatar(in bot.Avatar);
                bot.Session.SendTick(now);
            });
            bot.Session.Connect(host, Now);
            return bot;
        }

        protected sealed class ScriptedHost
        {
            public NetSessionHost Session;
            public ScriptedHostWorld World;
            public INetTransport Transport;
            public AvatarSnapshot Avatar = AvatarSnapshot.Default;
            public bool Frozen;
            public NetEndpoint Endpoint => Transport.Local.address == 0u ? new NetEndpoint(NetEndpoint.LoopbackAddress, Transport.Local.port) : Transport.Local;
        }

        /// <summary>A scripted host (NetBots) on the host port (memory) or an ephemeral loopback port (UDP), ticked every frame.</summary>
        protected ScriptedHost AddScriptedHost(uint? contentHash = null, byte protocol = NetProtocol.Version, ushort tug = 4821)
        {
            var host = new ScriptedHost { World = new ScriptedHostWorld(), Transport = NewTransport(NetProtocol.HostPort) };
            host.Session = new NetSessionHost(host.Transport, host.World, new NetHostConfig
            {
                contentHash = contentHash ?? m_Net.ContentHash,
                protocol = protocol,
                tugNumber = tug,
                seed = 900 + m_Owned.Count,
            });
            host.World.Session = host.Session;
            m_Owned.Add(host.Session);
            host.Avatar.headPosition = new NetVec3(-1.0f, 1.7f, -2.0f);
            host.Avatar.leftHandPosition = new NetVec3(-1.2f, 1.3f, -1.8f);
            host.Avatar.rightHandPosition = new NetVec3(-0.8f, 1.3f, -1.8f);
            m_Tickers.Add(now =>
            {
                if (host.Frozen || host.Session.IsClosed) return;
                host.Session.Poll(now);
                host.Session.SetLocalAvatar(in host.Avatar);
                host.Session.SendTick(now);
            });
            return host;
        }

        protected static float Distance(NetVec3 a, Vector3 b) => Vector3.Distance(new Vector3(a.x, a.y, a.z), b);
    }
}

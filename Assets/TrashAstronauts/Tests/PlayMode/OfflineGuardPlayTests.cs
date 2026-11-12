using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Net.Online;
using TrashAstronauts.Player;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Single-player must stay exactly single-player: a whole contract played without touching the CO-OP CREW
    /// panel never opens a socket or a Relay connection, never asks Unity's services for anything, never sets
    /// MissionDirector.Net, and leaves the NetSession offline. Kept in the suite for good (plan section 8).
    /// </summary>
    public class OfflineGuardPlayTests
    {
        MissionDirector m_Director;
        int m_Opened;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            MissionDirector.StorageOverride = new MemorySaveStorage();
            MissionDirector.SeedOverride = 44;
            MissionDirector.CreatureOverride = 0;
            MissionDirector.MapOverride = 0;
            MissionDirector.ConditionOverride = ContractCondition.Standard;
            BlackoutController.DamageOverride = 0f;
            m_Opened = 0;
            // Any attempt to open a transport is counted (and fails).
            NetSession.TransportFactory = port => { m_Opened++; return null; };
            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            yield return null;
            yield return null;
            m_Director = MissionDirector.Active;
        }

        [TearDown]
        public void TearDown()
        {
            int live = UdpTransport.LiveCount;
            NetSession.TransportFactory = null;
            MissionDirector.StorageOverride = null;
            MissionDirector.SeedOverride = null;
            MissionDirector.CreatureOverride = null;
            MissionDirector.MapOverride = null;
            MissionDirector.ConditionOverride = null;
            BlackoutController.DamageOverride = null;
            Assert.AreEqual(0, live, "Single-player opens no socket.");
        }

        [UnityTest]
        public IEnumerator AFullOfflineContract_NeverOpensASocket_AndEndsWithTheSessionOffline()
        {
            var net = m_Director.GetComponent<NetSession>();
            Assert.IsNotNull(net);
            Assert.AreEqual(LobbyMode.Offline, net.Mode);
            Assert.IsTrue(net.IsOffline);
            Assert.IsNull(m_Director.Net);
            Assert.IsFalse(m_Director.ContractStartBlocked);
            int onlineStarts = OnlineServices.StartRequests;
            Assert.AreEqual(0, RelayTransport.LiveCount);

            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            Assert.IsFalse(m_Director.CoopRideAlong, "A solo contract is the full contract.");
            Assert.IsTrue(m_Director.CreatureAboard);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            for (int i = 0; i < 30; i++) yield return null;

            // Bank something, then extract from inside the tug.
            var item = m_Director.Items.First(x => x != null && !x.IsMounted && !x.IsSecured && x.definition != null);
            m_Director.DepositSalvage(item, PlayerId.Local);
            var result = m_Director.Extract(PlayerId.Local);
            Assert.IsNotNull(result, "The player is in the tug: extraction works as it always has.");
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            for (int i = 0; i < 10; i++) yield return null;
            Assert.IsTrue(m_Director.ReturnToHub());
            for (int i = 0; i < 5; i++) yield return null;

            Assert.AreEqual(LobbyMode.Offline, net.Mode);
            Assert.IsTrue(net.IsOffline);
            Assert.IsFalse(net.IsHost);
            Assert.IsFalse(net.IsClient);
            Assert.AreEqual(0, m_Opened, "No transport was ever asked for.");
            Assert.AreEqual(onlineStarts, OnlineServices.StartRequests, "Unity's services were never asked to start.");
            Assert.AreEqual(0, RelayTransport.LiveCount, "No Relay connection.");
            Assert.IsFalse(net.HasOnlineLink, "No online link was even created.");
            Assert.AreEqual(OnlineStep.None, net.OnlineBusy);
            Assert.AreEqual(0, UdpTransport.LiveCount);
            Assert.IsNull(m_Director.Net);
            Assert.IsFalse(m_Director.ContractStartBlocked);
            Assert.AreEqual(1, m_Director.Players.Count);
            Assert.AreEqual(PlayerId.Local, m_Director.LocalPlayerId);
            Assert.AreEqual(PlayerAvatar.CrewColors[0], m_Director.LocalPlayer.avatar.Tint);
            Assert.AreEqual(0, Object.FindObjectsByType<RemoteAvatar>(FindObjectsInactive.Include).Length, "No crew stand-in exists offline.");
        }
    }
}

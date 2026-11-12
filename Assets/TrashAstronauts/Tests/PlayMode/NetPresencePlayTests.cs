using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.UI;
using UnityEngine;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Co-op stage 1 in the real scene: crew members see each other. Host half: the scene hosts and scene-less
    /// bots join it. Client half: the scene joins a scripted host that already has (or later gets) other crew.
    /// Everything runs over the in-memory transport with a fake session clock.
    /// </summary>
    public class NetPresencePlayTests : NetPlayFixture
    {
        static void SetBotPose(Bot bot, float x, float index, float grip, byte flags = 0)
        {
            bot.Avatar.headPosition = new NetVec3(x, 1.6f, -1.6f);
            bot.Avatar.headRotation = NetUnity.ToNet(Quaternion.Euler(0f, 90f, 0f));
            bot.Avatar.leftHandPosition = new NetVec3(x - 0.2f, 1.25f, -1.4f);
            bot.Avatar.rightHandPosition = new NetVec3(x + 0.2f, 1.3f, -1.4f);
            bot.Avatar.leftIndexCurl = index;
            bot.Avatar.leftGripCurl = grip;
            bot.Avatar.rightIndexCurl = 0f;
            bot.Avatar.rightGripCurl = 0f;
            bot.Avatar.flags = flags;
        }

        // ------------------------------------------------------------------ host half

        [UnityTest]
        public IEnumerator Host_ABotJoins_ASuitAppearsAsSlot1_FollowsTheStreamBehindTime_CurlsItsFingers_AndGoesOnBye()
        {
            // The host is left-handed; the joiner is not. The stand-in must keep the joiner's handedness.
            m_Director.Profile.Data.Handedness = Handedness.Left;
            m_Director.ApplyAndSaveSettings();
            Assert.AreEqual(Handedness.Left, m_Player.Handedness);

            Assert.IsTrue(m_Net.Host(), "HOST CO-OP");
            Assert.AreSame(m_Net, m_Director.Net);
            Assert.IsTrue(m_Net.IsHost);
            Assert.IsFalse(m_Net.IsClient);
            Assert.AreEqual(LobbyMode.Hosting, m_Net.Mode);
            Assert.AreEqual(new PlayerId(0), m_Player.Id);
            Assert.IsTrue(m_Director.ContractStartBlocked);
            Assert.AreEqual(PlayerAvatar.CrewColors[0], m_Player.avatar.Tint, "The host wears slot 0's colour.");

            var bot = AddBot(m_Net.Transport.Local, handedness: 0);
            SetBotPose(bot, 0.2f, 0f, 0f);
            yield return Until(() => bot.Session.Welcomed && m_Net.Crew(1) != null && m_Director.Players.Count == 2);
            Assert.AreEqual(1, bot.Session.LocalSlot);
            var crew = m_Net.Crew(1);
            Assert.IsNotNull(crew, "A stand-in was made for slot 1.");
            Assert.IsTrue(crew.context.IsRemote);
            Assert.AreEqual(new PlayerId(1), crew.context.Id);
            Assert.AreEqual(2, m_Director.Players.Count);
            Assert.AreSame(crew.context, m_Director.FindPlayer(new PlayerId(1)));
            Assert.AreSame(m_Player, m_Director.LocalPlayer, "The rig is still this headset's player.");
            Assert.AreEqual(Handedness.Right, crew.context.Handedness, "The stand-in did not receive this headset's handedness.");
            Assert.IsNull(crew.context.locomotion, "Nothing of the local rig's profile or comfort settings is on a stand-in.");
            Assert.AreEqual(PlayerAvatar.CrewColors[1], crew.Tint, "Slot 1 wears blue.");
            Assert.IsTrue(crew.suit.HelmetVisible, "Other crew wear their helmets.");
            Assert.AreEqual(2, m_Net.PlayerCount);

            // The bot drifts along +x at 0.5 m/s (0.01 m per 20 ms tick) with its left index and grip curled.
            float lagSum = 0f;
            int lagSamples = 0;
            for (int i = 0; i < 60; i++)
            {
                SetBotPose(bot, 0.2f + i * 0.01f, 1f, 0.5f, AvatarFlags.ThrusterRight);
                Tick();
                yield return null;
                if (i >= 30 && crew.HasPose)
                {
                    lagSum += (0.2f + i * 0.01f) - crew.head.position.x;
                    lagSamples++;
                }
            }
            Assert.IsTrue(crew.HasPose);
            Assert.Greater(lagSamples, 0);
            float lag = lagSum / lagSamples;
            // 100 ms behind at 0.5 m/s is 5 cm, plus up to one send tick; never ahead and never far behind.
            Assert.That(lag, Is.InRange(0.02f, 0.11f), "The stand-in is drawn about 100 ms behind the stream.");
            Assert.Less(Vector3.Distance(crew.context.HeadPosition, new Vector3(0.79f, 1.6f, -1.6f)), 0.03f,
                "The rules use the newest sample, not the smoothed one.");

            // Hold still: the drawn pose settles on the last sample.
            float settleStart = Time.realtimeSinceStartup;
            yield return Until(() => crew.leftGlove.IndexAmount > 0.85f && Time.realtimeSinceStartup - settleStart > 0.3f, maxFrames: 3000, maxRealSeconds: 5f);
            Assert.Less(Vector3.Distance(crew.head.position, new Vector3(0.79f, 1.6f, -1.6f)), 0.02f);
            Assert.Less(Vector3.Distance(crew.leftHand.transform.position, new Vector3(0.59f, 1.25f, -1.4f)), 0.02f);
            Assert.Greater(crew.leftGlove.IndexAmount, 0.85f, "The index finger curls from the streamed trigger.");
            Assert.That(crew.leftGlove.GripAmount, Is.InRange(0.35f, 0.65f), "The other fingers curl from the streamed grip.");
            Assert.Less(crew.rightGlove.IndexAmount, 0.15f, "The other hand stays open.");
            Assert.IsTrue(crew.rightHand.thrusterVisual.activeSelf, "The right thruster flame shows while that player thrusts.");
            Assert.IsFalse(crew.leftHand.thrusterVisual.activeSelf);
            Assert.IsTrue(crew.suit.gameObject.activeSelf);

            // The other way round: the bot hears the host's own suit.
            Assert.Greater(bot.World.AvatarCount[0], 0);
            Assert.Less(Distance(bot.World.Avatars[0].headPosition, m_Player.head.position), 0.03f, "The host streams its own head.");
            Assert.IsTrue(bot.World.Avatars[0].HasFlag(AvatarFlags.LeftHanded), "...and its handedness.");

            // LEAVE on the bot: its suit goes.
            bot.Session.Leave(Now);
            bot.Frozen = true;
            yield return Until(() => m_Net.Crew(1) == null && m_Director.Players.Count == 1);
            Assert.IsNull(m_Net.Crew(1));
            Assert.AreEqual(1, m_Director.Players.Count);
            Assert.IsNull(m_Director.FindPlayer(new PlayerId(1)));
            Assert.AreEqual(1, m_Net.PlayerCount);
            Assert.AreEqual(LobbyMode.Hosting, m_Net.Mode, "The host stays open for the next joiner.");

            Assert.IsTrue(m_Net.Leave());
            Assert.IsNull(m_Director.Net);
            Assert.IsFalse(m_Director.ContractStartBlocked);
            Assert.AreEqual(LobbyMode.Offline, m_Net.Mode);
        }

        [UnityTest]
        public IEnumerator Host_ABotThatGoesSilent_IsAwayAfterASecondAndAHalf_AndIsRemovedWhenTheSlotTimesOut()
        {
            Assert.IsTrue(m_Net.Host());
            var bot = AddBot(m_Net.Transport.Local);
            yield return Until(() => m_Net.Crew(1) != null && m_Net.Crew(1).HasPose);
            var crew = m_Net.Crew(1);
            Assert.IsNotNull(crew);
            Assert.IsFalse(crew.context.IsAway);

            // The headset goes to sleep: nothing from it any more.
            bot.Frozen = true;
            yield return Until(() => crew.context.IsAway, maxFrames: 200, dt: 0.05);
            Assert.IsTrue(crew.context.IsAway, "Silent for 1.5 s: away (not hunted, not waited for).");
            Assert.IsNotNull(m_Net.Crew(1), "Away is not gone: the slot is held.");

            // 45 s later the slot is given up and the suit goes.
            yield return Until(() => m_Net.Crew(1) == null, maxFrames: 200, dt: 0.5);
            Assert.IsNull(m_Net.Crew(1), "The slot timed out.");
            yield return null;
            Assert.AreEqual(1, m_Director.Players.Count);
            Assert.AreEqual(LobbyMode.Hosting, m_Net.Mode);
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Host_SecondAndThirdJoiners_GetSlots2And3InTheirOwnColours_AndSeeEachOtherThroughTheHost()
        {
            Assert.IsTrue(m_Net.Host());
            var a = AddBot(m_Net.Transport.Local);
            var b = AddBot(m_Net.Transport.Local);
            var c = AddBot(m_Net.Transport.Local);
            SetBotPose(a, 0.1f, 0f, 0f);
            SetBotPose(b, -0.4f, 0f, 0f);
            SetBotPose(c, 0.6f, 0f, 0f);
            yield return Until(() => m_Net.Crew(1) != null && m_Net.Crew(2) != null && m_Net.Crew(3) != null
                && b.World.AvatarCount[1] > 0 && b.World.AvatarCount[3] > 0);
            for (int slot = 1; slot <= 3; slot++)
            {
                Assert.IsNotNull(m_Net.Crew(slot), "slot " + slot);
                Assert.AreEqual(PlayerAvatar.CrewColors[slot], m_Net.Crew(slot).Tint);
            }
            Assert.AreEqual(4, m_Net.PlayerCount);
            Assert.Less(Distance(b.World.Avatars[1].headPosition, new Vector3(0.1f, 1.6f, -1.6f)), 0.02f, "The host relays one crew member's avatar to the others.");

            // A fifth headset is refused: the crew is full.
            var d = AddBot(m_Net.Transport.Local);
            yield return Until(() => d.Session.State == ClientState.Ended);
            Assert.AreEqual(SessionEndReason.Refused, d.Session.EndReason);
            Assert.AreEqual(RefuseReason.Full, d.Session.LastRefusal);
            m_Net.Leave();
            yield return Until(() => a.World.Ended == SessionEndReason.HostClosed);
            Assert.AreEqual(SessionEndReason.HostClosed, a.World.Ended, "LEAVE on the host tells the crew.");
            yield return null;
            Assert.AreEqual(1, m_Director.Players.Count, "Every stand-in is gone.");
            Assert.AreEqual(0, Object.FindObjectsByType<RemoteAvatar>(FindObjectsInactive.Include).Length);
        }

        [UnityTest]
        public IEnumerator Host_ACrewMemberWhoTeleports_SnapsToTheNewSpot_InsteadOfGlidingAcrossTheTug()
        {
            Assert.IsTrue(m_Net.Host());
            var bot = AddBot(m_Net.Transport.Local);
            SetBotPose(bot, -0.6f, 0f, 0f);
            yield return Until(() => m_Net.Crew(1) != null && m_Net.Crew(1).HasPose
                && Vector3.Distance(m_Net.Crew(1).head.position, new Vector3(-0.6f, 1.6f, -1.6f)) < 0.02f);
            var crew = m_Net.Crew(1);
            Assert.AreEqual(0, crew.Snaps);

            SetBotPose(bot, 1.4f, 0f, 0f); // 2 m in one tick: a teleport
            yield return Until(() => crew.Snaps > 0, maxFrames: 20);
            Assert.AreEqual(1, crew.Snaps, "a jump of more than a metre snaps");
            yield return Frames(2);
            Assert.Less(Vector3.Distance(crew.head.position, new Vector3(1.4f, 1.6f, -1.6f)), 0.02f,
                "drawn at the new spot at once, not sliding there 100 ms behind");
            Assert.Less(Vector3.Distance(crew.context.HeadPosition, new Vector3(1.4f, 1.6f, -1.6f)), 0.02f, "the rules see the newest head");

            // Ordinary movement afterwards glides again.
            for (int i = 1; i <= 10; i++) { SetBotPose(bot, 1.4f - i * 0.01f, 0f, 0f); Tick(); yield return null; }
            Assert.AreEqual(1, crew.Snaps);
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Host_ItsOwnPause_ShowsAsAwayOnTheCrewsPanels()
        {
            Assert.IsTrue(m_Net.Host());
            var bot = AddBot(m_Net.Transport.Local);
            yield return Until(() => bot.World.AvatarCount[0] > 0);
            Assert.IsFalse(bot.World.Avatars[0].IsAway);
            m_Director.SetHazardPause(HazardPauseReason.Scripted, true);
            yield return Until(() => bot.World.Avatars[0].IsAway);
            Assert.IsTrue(bot.World.Avatars[0].IsAway, "the host's avatar carries its Away state");
            m_Director.SetHazardPause(HazardPauseReason.Scripted, false);
            yield return Until(() => !bot.World.Avatars[0].IsAway);
            Assert.IsFalse(bot.World.Avatars[0].IsAway);
            m_Net.Leave();
        }

        // ------------------------------------------------------------------ client half

        [UnityTest]
        public IEnumerator Client_OnJoining_TheRigStepsOntoItsOwnCrewSpot_AndTheHostsSuitIsShownAwayWhenItPauses()
        {
            var host = AddScriptedHost();
            Vector3 spot = m_Director.CrewSpot(1);
            Assert.Greater(Vector3.Distance(spot, m_Director.CrewSpot(0)), 0.7f);
            Assert.IsTrue(m_Net.Join(host.Endpoint));
            yield return Until(() => m_Net.Mode == LobbyMode.Joined && m_Net.Crew(0) != null && m_Net.Crew(0).HasPose);
            yield return Frames(10);
            Vector3 head = m_Player.HeadPosition;
            Assert.Less(Vector2.Distance(new Vector2(head.x, head.z), new Vector2(spot.x, spot.z)), 0.2f, "slot 1 stands on its own spot, not on the host's");

            // The hub guard keeps it on that spot (not the shared spawn) when it wanders off.
            yield return Frames(60);
            head = m_Player.HeadPosition;
            Assert.Less(Vector2.Distance(new Vector2(head.x, head.z), new Vector2(spot.x, spot.z)), 0.2f);

            host.Avatar.flags = AvatarFlags.Away;
            yield return Until(() => m_Net.Crew(0).context.IsAway);
            Assert.IsTrue(m_Net.Crew(0).context.IsAway, "a paused host shows as away");
            host.Avatar.flags = 0;
            yield return Until(() => !m_Net.Crew(0).context.IsAway);
            Assert.IsFalse(m_Net.Crew(0).context.IsAway);
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Client_AwayIsAppPausedOrTrackingLost_ResumingTheAppAloneIsNotBack()
        {
            var host = AddScriptedHost();
            Assert.IsTrue(m_Net.Join(host.Endpoint));
            yield return Until(() => m_Net.Mode == LobbyMode.Joined && host.World.Joined[1]);
            m_Director.SetHazardPause(HazardPauseReason.Scripted, true);
            m_Net.SendMessage("OnApplicationPause", true);
            yield return Until(() => host.World.Away[1]);
            Assert.IsTrue(host.World.Away[1]);
            m_Net.SendMessage("OnApplicationPause", false);
            yield return Frames(30);
            Assert.IsTrue(host.World.Away[1], "the app resumed but tracking is still lost: still away");
            Assert.IsTrue(m_Net.LocalAway);
            m_Director.SetHazardPause(HazardPauseReason.Scripted, false);
            yield return Until(() => !host.World.Away[1]);
            Assert.IsFalse(host.World.Away[1]);
            Assert.IsFalse(m_Net.LocalAway);
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Client_WithTheTutorialFinished_TheTutorialBoardDoesNotClaimItIsPaused()
        {
            var tutorial = Object.FindAnyObjectByType<TutorialController>();
            tutorial.SkipAll();
            var host = AddScriptedHost();
            Assert.IsTrue(m_Net.Join(host.Endpoint));
            yield return Until(() => m_Net.Mode == LobbyMode.Joined);
            yield return Frames(3);
            Assert.IsTrue(tutorial.SuspendedForSession);
            StringAssert.DoesNotContain("PAUSED", tutorial.boardText.text);
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator Client_TheSceneJoinsAScriptedHost_TakesSlot1_SeesTheHostAndAnotherJoiner_AndLeaveMakesItSoloAgain()
        {
            var tutorial = Object.FindAnyObjectByType<TutorialController>();
            Assert.IsNotNull(tutorial);
            var host = AddScriptedHost();
            Assert.IsTrue(m_Director.salvageRoot.GetComponentsInChildren<SalvageItem>().Any(i => i.isTraining), "Test set-up: the practice scrap floats in the tug.");

            Assert.IsTrue(m_Net.Join(host.Endpoint));
            Assert.AreEqual(LobbyMode.Connecting, m_Net.Mode);
            Assert.IsNull(m_Director.Net, "Not part of a session until the host welcomes us.");
            Assert.IsTrue(m_Director.ContractStartBlocked, "No contract while a join is under way either.");

            yield return Until(() => m_Net.Mode == LobbyMode.Joined && m_Net.Crew(0) != null && m_Net.Crew(0).HasPose);
            Assert.AreEqual(LobbyMode.Joined, m_Net.Mode);
            Assert.AreSame(m_Net, m_Director.Net);
            Assert.IsTrue(m_Net.IsClient);
            Assert.AreEqual(1, m_Net.LocalSlot);
            Assert.AreEqual(new PlayerId(1), m_Player.Id, "The local rig takes the slot the host gave.");
            Assert.AreEqual(new PlayerId(1), m_Director.LocalPlayerId);
            Assert.AreSame(m_Player, m_Director.LocalPlayer);
            Assert.AreEqual(PlayerAvatar.CrewColors[1], m_Player.avatar.Tint, "This headset's own suit takes its slot colour.");
            var hostCrew = m_Net.Crew(0);
            Assert.IsTrue(hostCrew.context.IsRemote);
            Assert.AreEqual(new PlayerId(0), hostCrew.context.Id);
            Assert.AreEqual(PlayerAvatar.CrewColors[0], hostCrew.Tint);
            Assert.Less(Vector3.Distance(hostCrew.head.position, new Vector3(-1f, 1.7f, -2f)), 0.02f, "The host's suit is where the host is.");
            Assert.IsTrue(host.World.Joined[1]);
            Assert.Greater(host.World.AvatarCount[1], 0, "The host receives this headset's avatar.");
            Assert.Less(Distance(host.World.Avatars[1].headPosition, m_Player.head.position), 0.03f);
            Assert.IsFalse(m_Director.salvageRoot.GetComponentsInChildren<SalvageItem>().Any(), "Joining starts from the same empty tug as the host.");
            yield return null;
            Assert.IsTrue(tutorial.SuspendedForSession, "The tutorial is off in a session.");

            // Another headset joins the same host: it appears here as slot 2, relayed by the host.
            var other = AddBot(host.Endpoint);
            SetBotPose(other, 0.9f, 1f, 1f);
            yield return Until(() => m_Net.Crew(2) != null && m_Net.Crew(2).HasPose && Vector3.Distance(m_Net.Crew(2).head.position, new Vector3(0.9f, 1.6f, -1.6f)) < 0.02f);
            var otherCrew = m_Net.Crew(2);
            Assert.IsNotNull(otherCrew);
            Assert.AreEqual(new PlayerId(2), otherCrew.context.Id);
            Assert.AreEqual(PlayerAvatar.CrewColors[2], otherCrew.Tint);
            Assert.Less(Vector3.Distance(otherCrew.head.position, new Vector3(0.9f, 1.6f, -1.6f)), 0.02f);
            Assert.AreEqual(3, m_Net.PlayerCount);
            Assert.AreEqual(3, m_Director.Players.Count);
            Assert.IsFalse(m_Director.StartMission(m_Director.LocalPlayerId), "No contract can start in a stage 1 session.");
            Assert.AreEqual(MissionPhase.Hub, m_Director.Phase);

            // The other joiner leaves: its suit goes here too.
            other.Session.Leave(Now);
            other.Frozen = true;
            yield return Until(() => m_Net.Crew(2) == null);
            Assert.IsNull(m_Net.Crew(2));

            // LEAVE: back to slot 0, solo, no session.
            Assert.IsTrue(m_Net.Leave());
            Assert.IsNull(m_Director.Net);
            Assert.IsFalse(m_Net.IsClient);
            Assert.AreEqual(LobbyMode.Offline, m_Net.Mode);
            Assert.AreEqual(new PlayerId(0), m_Player.Id);
            Assert.AreEqual(PlayerId.Local, m_Director.LocalPlayerId);
            Assert.AreEqual(PlayerAvatar.CrewColors[0], m_Player.avatar.Tint);
            Assert.IsFalse(m_Director.ContractStartBlocked);
            yield return Until(() => !host.World.Joined[1]);
            Assert.AreEqual(PlayerLeaveReason.Bye, host.World.LeftReason[1], "The host hears the goodbye.");
            yield return null;
            Assert.IsNull(m_Net.Crew(0));
            Assert.AreEqual(1, m_Director.Players.Count);
            Assert.IsFalse(tutorial.SuspendedForSession);
            Assert.IsTrue(m_Director.salvageRoot.GetComponentsInChildren<SalvageItem>().Any(i => i.isTraining), "The unfinished tutorial gets its practice scrap back.");
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local), "Solo again: contracts start as before.");
        }

        [UnityTest]
        public IEnumerator Client_WhenTheHostCloses_TheSceneIsSoloAgain_AndTheBoardSaysTheHostLeft()
        {
            var host = AddScriptedHost();
            Assert.IsTrue(m_Net.Join(host.Endpoint));
            yield return Until(() => m_Net.Mode == LobbyMode.Joined && m_Net.Crew(0) != null);
            Assert.AreEqual(LobbyMode.Joined, m_Net.Mode);

            host.Session.Close(Now);
            yield return Until(() => m_Net.Mode == LobbyMode.Offline);
            Assert.AreEqual(LobbyMode.Offline, m_Net.Mode);
            Assert.AreEqual(LobbyNotice.HostClosed, m_Net.Notice);
            Assert.IsNull(m_Director.Net);
            Assert.AreEqual(new PlayerId(0), m_Player.Id);
            Assert.IsFalse(m_Director.ContractStartBlocked);
            Assert.IsTrue(m_Net.IsOffline, "The socket is closed.");
            yield return null;
            Assert.IsNull(m_Net.Crew(0));
            Assert.AreEqual(1, m_Director.Players.Count);
            var board = Object.FindAnyObjectByType<NetLobbyBoard>();
            board.Refresh();
            StringAssert.StartsWith("HOST LEFT", board.notice.text);
        }

        [UnityTest]
        public IEnumerator Client_HeadsetPaused_TheHostSeesItAway_AndBackOnResume()
        {
            var host = AddScriptedHost();
            Assert.IsTrue(m_Net.Join(host.Endpoint));
            yield return Until(() => m_Net.Mode == LobbyMode.Joined && host.World.Joined[1]);
            m_Net.ReportPause(true);
            yield return Until(() => host.World.Away[1]);
            Assert.IsTrue(host.World.Away[1]);
            m_Net.ReportPause(false);
            yield return Until(() => !host.World.Away[1]);
            Assert.IsFalse(host.World.Away[1]);
            m_Net.Leave();
        }
    }

    /// <summary>The same presence flow once over real UDP sockets on 127.0.0.1 (ephemeral ports) with the real clock.</summary>
    public class NetPresenceUdpPlayTests : NetPlayFixture
    {
        protected override bool UseMemoryTransport => false;

        [UnityTest]
        public IEnumerator Udp_TheSceneHosts_ABotOnLoopbackJoins_ItsSuitFollows_AndEverySocketCloses()
        {
            Assert.IsTrue(m_Net.Host());
            Assert.AreEqual(1, UdpTransport.LiveCount);
            Assert.AreEqual(NetEndpoint.LoopbackAddress, m_Net.Transport.Local.address);
            var bot = AddBot(m_Net.Transport.Local);
            bot.Avatar.headPosition = new NetVec3(0.4f, 1.55f, -1.7f);
            yield return Until(() => m_Net.Crew(1) != null && m_Net.Crew(1).HasPose
                && Vector3.Distance(m_Net.Crew(1).head.position, new Vector3(0.4f, 1.55f, -1.7f)) < 0.02f, maxFrames: 100000, maxRealSeconds: 8f);
            var crew = m_Net.Crew(1);
            Assert.IsNotNull(crew, "A bot on a real socket joined.");
            Assert.Less(Vector3.Distance(crew.head.position, new Vector3(0.4f, 1.55f, -1.7f)), 0.02f);
            Assert.AreEqual(PlayerAvatar.CrewColors[1], crew.Tint);

            bot.Session.Leave(Now);
            bot.Frozen = true;
            yield return Until(() => m_Net.Crew(1) == null, maxFrames: 100000, maxRealSeconds: 5f);
            Assert.IsNull(m_Net.Crew(1));

            m_Net.Leave();
            Assert.IsNull(m_Director.Net);
            bot.Session.Dispose();
            // The closed host answers stragglers for a moment, then closes its socket.
            yield return Until(() => UdpTransport.LiveCount == 0, maxFrames: 100000, maxRealSeconds: (float)NetSession.HostLingerSeconds + 2f);
            Assert.AreEqual(0, UdpTransport.LiveCount);
        }

        [UnityTest]
        public IEnumerator Udp_TheSceneJoinsALoopbackHost_SeesItsSuit_AndLeaveClosesTheSocket()
        {
            var host = AddScriptedHost();
            Assert.IsTrue(m_Net.Join(host.Endpoint));
            yield return Until(() => m_Net.Mode == LobbyMode.Joined && m_Net.Crew(0) != null && m_Net.Crew(0).HasPose
                && Vector3.Distance(m_Net.Crew(0).head.position, new Vector3(-1f, 1.7f, -2f)) < 0.02f, maxFrames: 100000, maxRealSeconds: 8f);
            Assert.AreEqual(LobbyMode.Joined, m_Net.Mode);
            Assert.AreEqual(new PlayerId(1), m_Player.Id);
            Assert.Less(Vector3.Distance(m_Net.Crew(0).head.position, new Vector3(-1f, 1.7f, -2f)), 0.02f);
            yield return Until(() => host.World.AvatarCount[1] > 0, maxFrames: 100000, maxRealSeconds: 3f);
            Assert.Greater(host.World.AvatarCount[1], 0);

            m_Net.Leave();
            Assert.AreEqual(new PlayerId(0), m_Player.Id);
            Assert.IsNull(m_Director.Net);
            yield return Until(() => !host.World.Joined[1], maxFrames: 100000, maxRealSeconds: 3f);
            Assert.AreEqual(PlayerLeaveReason.Bye, host.World.LeftReason[1]);
            host.Session.Dispose();
            Assert.AreEqual(0, UdpTransport.LiveCount, "The client's socket closed with LEAVE.");
        }
    }
}

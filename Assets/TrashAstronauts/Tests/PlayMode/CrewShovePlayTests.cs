using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using UnityEngine;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Crew shoves in the real scene (CrewShove): the host's hand swung into a bot's stand-in sends one shove and
    /// pushes the host back; a bot's shove moves the host within the cap and plays its feedback; the host refuses a
    /// shove from too far away; CREW SHOVES off neither sends nor moves; a downed player is left alone; under gravity a
    /// shove only slides along the ground; as a client a scripted host's shove moves the player once despite its
    /// copies; and single-player never creates any of it. Bots are scene-less sessions on the in-memory transport.
    /// </summary>
    public class CrewShovePlayTests : NetPlayFixture
    {
        bool m_SwimWas;
        GameObject m_Gravity;

        [TearDown]
        public void RestoreShoveState()
        {
            CrewDowned.Check = p => p.IsDowned;
            if (m_Player != null && m_Player.IsDowned) m_Player.SetDowned(false, m_Player.DownedPosition);
            if (m_Gravity != null) Object.Destroy(m_Gravity);
            if (m_Player != null && m_Player.locomotion != null && m_Player.locomotion.zeroG != null) m_Player.locomotion.zeroG.openHandSwim = m_SwimWas;
        }

        ZeroGLocomotion ZeroG => m_Player.locomotion.zeroG;

        Vector3 Forward
        {
            get
            {
                Vector3 f = m_Player.head.forward;
                f.y = 0f;
                return f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
            }
        }

        static AvatarSnapshot Pose(Vector3 head, Vector3 facing)
        {
            var s = AvatarSnapshot.Default;
            Quaternion face = Quaternion.LookRotation(facing, Vector3.up);
            s.headPosition = head.ToNet();
            s.headRotation = face.ToNet();
            s.leftHandPosition = (head + face * new Vector3(-0.25f, -0.45f, 0.1f)).ToNet();
            s.rightHandPosition = (head + face * new Vector3(0.25f, -0.45f, 0.1f)).ToNet();
            s.leftHandRotation = face.ToNet();
            s.rightHandRotation = face.ToNet();
            return s;
        }

        /// <summary>Hosts, adds a bot whose head is <paramref name="distance"/> in front of the host's, and waits for its stand-in and the shove sync.</summary>
        IEnumerator HostWithBotInFront(float distance, List<(byte sender, NetVec3 v)> heardByBot)
        {
            m_SwimWas = ZeroG.openHandSwim;
            ZeroG.openHandSwim = false; // the test's swing must not also swim the host backwards
            // The test moves the hands itself (no headset: the pose drivers would put them back every frame).
            foreach (var driver in m_Player.GetComponentsInChildren<UnityEngine.InputSystem.XR.TrackedPoseDriver>()) driver.enabled = false;
            Assert.IsTrue(m_Net.Host());
            var bot = AddBot(m_Net.Transport.Local);
            m_Bot = bot;
            bot.Avatar = Pose(m_Player.head.position + Forward * distance, -Forward);
            if (heardByBot != null) bot.Session.CrewPushReceived += (sender, v, contact, flags) => heardByBot.Add((sender, v));
            yield return Until(() => m_Net.Crew(1) != null && m_Net.Crew(1).HasPose && bot.Session.Welcomed);
            yield return Frames(5);
            Assert.IsNotNull(m_Net.Crew(1), "Test set-up: the bot's stand-in.");
            Assert.IsNotNull(m_Net.GetComponent<CrewShove>(), "The first crew stand-in starts crew shoves.");
            ZeroG.Stop();
        }

        Bot m_Bot;
        string SwingLog = "";
        bool TwoHanded;

        /// <summary>Swings the right hand forward from chest height at <paramref name="speed"/> until a shove is sent (or the reach runs out), then holds it still.</summary>
        IEnumerator Swing(CrewShove shove, float speed, float from = 0.1f, float to = 1.0f)
        {
            const double dt = 0.02;
            Vector3 chest = m_Player.head.position + Vector3.down * 0.35f;
            Vector3 dir = Forward;
            int sent = shove.Sent, thudOnly = shove.ThudOnly;
            var log = new System.Text.StringBuilder();
            var crew = m_Net.Crew(1);
            for (float along = from; along <= to; along += speed * (float)dt)
            {
                Vector3 set = chest + dir * along;
                m_Player.rightHand.transform.position = set;
                if (TwoHanded) m_Player.leftHand.transform.position = set + Vector3.Cross(Vector3.up, dir) * 0.16f;
                Tick(dt);
                yield return null;
                Vector3 palm = m_Player.rightHand.interactor != null ? m_Player.rightHand.interactor.transform.position : m_Player.rightHand.transform.position;
                bool touch = CrewShape.Touches(crew, palm, CrewShove.HandRadius, out _, out _);
                log.Append(along.ToString("0.00")).Append(" set").Append(set).Append(" now").Append(m_Player.rightHand.transform.position).Append(" touch=").Append(touch).Append('\n');
                if (shove.Sent > sent || shove.ThudOnly > thudOnly) break;
            }
            SwingLog = log.ToString() + " away=" + m_Player.IsAway + " holding=" + m_Player.rightHand.IsHolding + " anchored=" + m_Player.rightHand.IsAnchored
                + " zgAnchored=" + ZeroG.IsHandAnchored(m_Player.rightHand) + " crewHead=" + (crew != null && crew.head != null ? crew.head.position.ToString() : "-")
                + " torso=" + (crew != null && crew.suit != null && crew.suit.torso != null ? crew.suit.torso.position.ToString() : "-") + " suitOn=" + (crew != null && crew.suit != null && crew.suit.isActiveAndEnabled)
                + " crewAway=" + (crew != null && crew.context != null && crew.context.IsAway);
            yield return Frames(15);
        }

        [UnityTest]
        public IEnumerator Host_HandSwungIntoTheBot_SendsOneShoveToTheBot_AndTheHostRecoils()
        {
            var heard = new List<(byte sender, NetVec3 v)>();
            yield return HostWithBotInFront(0.75f, heard);
            var shove = m_Net.GetComponent<CrewShove>();
            yield return Swing(shove, 1.6f);

            Assert.AreEqual(0, shove.BlockedByWall, "Test set-up: nothing between the two.");
            Assert.AreEqual(1, shove.Sent, "One swing, one shove.\n" + SwingLog);
            Assert.AreEqual(1, shove.LastTarget);
            Assert.AreEqual(1, heard.Count, "The bot heard it once, whatever the copies.");
            Assert.AreEqual(0, heard[0].sender);
            Vector3 v = heard[0].v.ToUnity();
            Assert.That(v.magnitude, Is.InRange(0.6f, CrewPushMsg.MaxTargetSpeed + 0.01f), "A shove at about 1.6 m/s gives about 1 m/s: " + v);
            Assert.Greater(Vector3.Dot(v.normalized, Forward), 0.8f, "Along the swing.");

            Vector3 recoil = shove.LastRecoil;
            Assert.That(recoil.magnitude, Is.InRange(0.3f, CrewPushMsg.MaxPusherSpeed + 0.001f), "Newton's third law, capped: " + recoil);
            Assert.Less(Vector3.Dot(recoil.normalized, Forward), -0.8f, "Back the other way.");
            Assert.Less(Vector3.Dot(ZeroG.Velocity, Forward), -0.2f, "The host drifts back: " + ZeroG.Velocity);
            Assert.LessOrEqual(ZeroG.Speed, CrewPushMsg.MaxPusherSpeed + 0.01f);
            Assert.Greater(shove.Thuds, 0, "A thud where the hand met the suit.");
            Assert.AreEqual(0, m_Net.HostSession.CrewPushStats.Rejected);
        }

        [UnityTest]
        public IEnumerator Host_ATwoHandedShove_AddsUpToTheCap_AsOneShove()
        {
            var heard = new List<(byte sender, NetVec3 v)>();
            yield return HostWithBotInFront(0.75f, heard);
            var shove = m_Net.GetComponent<CrewShove>();
            TwoHanded = true;
            yield return Swing(shove, 1.6f);
            Assert.AreEqual(1, shove.Sent, "Both hands together are one shove.\n" + SwingLog);
            Assert.AreEqual(1, heard.Count);
            Assert.AreEqual(CrewPushMsg.MaxTargetSpeed, heard[0].v.Magnitude, 0.01f, "Two hands at 1.6 m/s add up past the cap: capped.");
            Assert.AreEqual(CrewPushMsg.MaxPusherSpeed, shove.LastRecoil.magnitude, 0.01f, "The pusher's kick is capped lower.");
        }

        [UnityTest]
        public IEnumerator Host_ASlowPress_GentlyPushesInProportion()
        {
            var heard = new List<(byte sender, NetVec3 v)>();
            yield return HostWithBotInFront(0.75f, heard);
            var shove = m_Net.GetComponent<CrewShove>();
            yield return Swing(shove, 0.4f);
            Assert.AreEqual(1, shove.Sent, "A slow press still pushes.\n" + SwingLog);
            Assert.AreEqual(1, heard.Count);
            Assert.That(heard[0].v.Magnitude, Is.InRange(0.1f, 0.3f), "About half the 0.4 m/s hand speed.");
            Assert.That(shove.LastRecoil.magnitude, Is.InRange(0.1f, 0.3f), "And as much back.");
        }

        [UnityTest]
        public IEnumerator Host_ABotsShove_MovesTheHostWithinTheCap_WithHapticsAndAThud()
        {
            yield return HostWithBotInFront(0.8f, null);
            var shove = m_Net.GetComponent<CrewShove>();
            int left = m_Player.leftHand.PulseCount, right = m_Player.rightHand.PulseCount, thuds = shove.Thuds;
            Vector3 push = -Forward * 3f; // asks for far more than the cap
            Assert.IsTrue(m_Bot.Session.SendCrewPush(0, push.ToNet(), (m_Player.head.position + Forward * 0.2f).ToNet(), CrewPushMsg.FlagShove));
            yield return Until(() => shove.Received > 0, 60);
            yield return Frames(10);
            Assert.AreEqual(1, shove.Received, "Once, whatever the copies.");
            Assert.AreEqual(CrewPushMsg.MaxTargetSpeed, shove.LastReceived.magnitude, 0.01f, "Capped.");
            Assert.Greater(Vector3.Dot(ZeroG.Velocity, -Forward), 0.9f, "The host is moving the way it was pushed: " + ZeroG.Velocity);
            Assert.LessOrEqual(ZeroG.Speed, CrewPushMsg.MaxTargetSpeed + 0.01f);
            Assert.Greater(m_Player.leftHand.PulseCount, left, "A bump on the left controller.");
            Assert.Greater(m_Player.rightHand.PulseCount, right, "And on the right.");
            Assert.Greater(shove.Thuds, thuds, "A low thud.");
        }

        [UnityTest]
        public IEnumerator Host_RefusesAShove_FromABotTooFarAway()
        {
            yield return HostWithBotInFront(5f, null);
            var shove = m_Net.GetComponent<CrewShove>();
            Assert.IsTrue(m_Bot.Session.SendCrewPush(0, (Forward * -1f).ToNet(), m_Player.head.position.ToNet(), CrewPushMsg.FlagShove));
            yield return Frames(20);
            Assert.AreEqual(0, shove.Received);
            Assert.AreEqual(1, m_Net.HostSession.CrewPushStats.tooFar);
            Assert.Less(ZeroG.Speed, 0.01f, "Not moved.");
        }

        [UnityTest]
        public IEnumerator Host_WithCrewShovesOff_NeitherSendsNorMoves_ButTheHandStillThuds()
        {
            var heard = new List<(byte sender, NetVec3 v)>();
            yield return HostWithBotInFront(0.75f, heard);
            var shove = m_Net.GetComponent<CrewShove>();
            m_Director.Profile.Data.crewShoves = false;

            yield return Swing(shove, 1.6f);
            Assert.AreEqual(0, shove.Sent);
            Assert.AreEqual(1, shove.ThudOnly, "A recoil-free thud.");
            Assert.Greater(shove.Thuds, 0);
            Assert.AreEqual(0, heard.Count);
            Assert.Less(ZeroG.Speed, 0.01f, "No recoil.");

            Assert.IsTrue(m_Bot.Session.SendCrewPush(0, (Forward * -1f).ToNet(), m_Player.head.position.ToNet(), CrewPushMsg.FlagShove));
            yield return Frames(20);
            Assert.AreEqual(1, shove.Ignored);
            Assert.AreEqual(0, shove.Received);
            Assert.Less(ZeroG.Speed, 0.01f, "Not moved by the bot either.");
        }

        [UnityTest]
        public IEnumerator Host_ADownedPlayer_NeitherShovesNorIsShoved()
        {
            var heard = new List<(byte sender, NetVec3 v)>();
            yield return HostWithBotInFront(0.75f, heard);
            var shove = m_Net.GetComponent<CrewShove>();
            CrewDowned.Check = p => p == m_Player;

            yield return Swing(shove, 1.6f);
            Assert.AreEqual(0, shove.Sent);
            Assert.AreEqual(0, heard.Count);
            Assert.IsTrue(m_Bot.Session.SendCrewPush(0, (Forward * -1f).ToNet(), m_Player.head.position.ToNet(), CrewPushMsg.FlagShove));
            yield return Frames(20);
            Assert.AreEqual(0, shove.Received);
            Assert.AreEqual(1, m_Net.HostSession.CrewPushStats.refused, "The host's own check turned it down.");
            Assert.Less(ZeroG.Speed, 0.01f);

            // The bot downed instead: the host's hand does nothing to it.
            CrewDowned.Check = p => p != null && p.IsRemote;
            yield return Frames(20);
            yield return Swing(shove, 1.6f);
            Assert.AreEqual(0, shove.Sent);
        }

        /// <summary>The merged hook: the revive feature's own downed state (PlayerContext.IsDowned) stops shoves, no stand-in check.</summary>
        [UnityTest]
        public IEnumerator Host_APlayerDownedByTheReviveFeature_NeitherShovesNorIsShoved()
        {
            var heard = new List<(byte sender, NetVec3 v)>();
            yield return HostWithBotInFront(0.75f, heard);
            var shove = m_Net.GetComponent<CrewShove>();
            Assert.IsTrue(CrewDowned.Is(m_Player) == m_Player.IsDowned);
            Assert.IsTrue(m_Player.SetDowned(true, m_Player.head.position));
            Assert.IsTrue(CrewDowned.Is(m_Player), "CrewDowned reads PlayerContext.IsDowned.");
            yield return Swing(shove, 1.6f);
            Assert.AreEqual(0, shove.Sent);
            Assert.AreEqual(0, heard.Count);
            Assert.IsTrue(m_Bot.Session.SendCrewPush(0, (Forward * -1f).ToNet(), m_Player.head.position.ToNet(), CrewPushMsg.FlagShove));
            yield return Frames(20);
            Assert.AreEqual(0, shove.Received);
            Assert.Less(ZeroG.Speed, 0.01f);
            m_Player.SetDowned(false, m_Player.DownedPosition);
            Assert.IsFalse(CrewDowned.Is(m_Player));
        }

        [UnityTest]
        public IEnumerator Host_UnderGravity_AShoveSlidesAlongTheGround()
        {
            yield return HostWithBotInFront(0.8f, null);
            var shove = m_Net.GetComponent<CrewShove>();
            m_Gravity = new GameObject("Test Gravity");
            m_Gravity.transform.position = m_Player.head.position;
            var volume = m_Gravity.AddComponent<MapGravity>();
            volume.size = new Vector3(4f, 6f, 4f);
            Assert.AreNotEqual(Vector3.zero, MapGravity.GravityAt(m_Player.head.position), "Test set-up: gravity here.");

            Vector3 push = (-Forward + Vector3.down * 0.8f) * 0.8f;
            Assert.IsTrue(m_Bot.Session.SendCrewPush(0, push.ToNet(), m_Player.head.position.ToNet(), CrewPushMsg.FlagShove));
            yield return Until(() => shove.Received > 0, 60);
            Assert.AreEqual(1, shove.Received);
            Assert.AreEqual(0f, shove.LastReceived.y, 0.001f, "Nothing into (or out of) the ground.");
            Assert.AreEqual(0.8f, Vector3.Dot(shove.LastReceived, -Forward), 0.02f, "Along the ground as pushed.");
        }

        [UnityTest]
        public IEnumerator Client_AScriptedHostsShove_MovesThePlayerOnce_DespiteItsCopies()
        {
            m_SwimWas = ZeroG.openHandSwim;
            var host = AddScriptedHost();
            host.Avatar = Pose(m_Player.head.position + Forward * 0.8f, -Forward);
            Assert.IsTrue(m_Net.Join(host.Endpoint));
            yield return Until(() => m_Net.Mode == LobbyMode.Joined && m_Net.Crew(0) != null && m_Net.Crew(0).HasPose);
            yield return Frames(3);
            var shove = m_Net.GetComponent<CrewShove>();
            Assert.IsNotNull(shove, "The host's stand-in starts crew shoves on the client.");
            ZeroG.Stop();

            Vector3 push = -Forward * 0.9f;
            Assert.IsTrue(host.Session.SendCrewPush(1, push.ToNet(), m_Player.head.position.ToNet(), CrewPushMsg.FlagShove));
            yield return Frames(15);
            Assert.AreEqual(1, shove.Received, "Once.");
            Assert.AreEqual(CrewPushMsg.Repeats - 1, m_Net.ClientSession.CrewPushStats.duplicates, "The copies arrived and were dropped.");
            Assert.AreEqual(0.9f, shove.LastReceived.magnitude, 0.01f);
            Assert.That(Vector3.Dot(ZeroG.Velocity, -Forward), Is.InRange(0.5f, 0.92f), "Moved by one shove, not three: " + ZeroG.Velocity);
        }

        [UnityTest]
        public IEnumerator SinglePlayer_NeverCreatesCrewShoves()
        {
            m_SwimWas = ZeroG.openHandSwim;
            Vector3 chest = m_Player.head.position + Vector3.down * 0.35f;
            for (int i = 0; i < 20; i++)
            {
                m_Player.rightHand.transform.position = chest + Forward * (0.1f + i * 0.04f);
                yield return null;
            }
            Assert.IsTrue(m_Net.IsOffline);
            Assert.AreEqual(0, Object.FindObjectsByType<CrewShove>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length, "No crew shoves offline.");
        }
    }
}

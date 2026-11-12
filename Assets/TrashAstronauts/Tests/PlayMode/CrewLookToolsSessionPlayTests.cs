using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Net.Voice;
using TrashAstronauts.Player;
using TrashAstronauts.UI;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// The suit-colour and crew-tool modules wired into the sessions: a joiner sees each crew member's chosen colour
    /// from the first frame their suit is shown; a colour picked at the SUIT LOCKER mid-session reaches the crew at once;
    /// a crew member's accepted dart is drawn for everyone else; the tool copies keep their own colours; the voice talk
    /// light still sits on the helmet with the tool rig present.
    /// </summary>
    public class CrewLookToolsSessionPlayTests : NetPlayFixture
    {
        static bool Wears(RemoteAvatar crew, int slot, int colour) => CrewLook.HasLook(crew.suit, crew.leftGlove, crew.rightGlove, slot, colour);

        [UnityTest]
        public IEnumerator AJoiner_SeesTheHostsChosenColour_FromTheFirstFrameItsSuitIsShown_GlovesIncluded()
        {
            var host = AddScriptedHost();
            host.Session.SetLocalLook(0, 8); // the host picked palette colour 8 before anyone joined
            Assert.IsTrue(m_Net.Join(host.Endpoint));
            bool everWrong = false, seen = false;
            for (int i = 0; i < 400 && !(seen && m_Net.Crew(0).HasPose); i++)
            {
                Tick();
                yield return null;
                var crew = m_Net.Crew(0);
                if (crew == null) continue;
                seen = true;
                if (!Wears(crew, 0, 8)) everWrong = true;
            }
            Assert.IsTrue(seen, "the host's suit appeared");
            Assert.IsFalse(everWrong, "never shown in the slot's own colour, not even for a frame");
            Assert.IsTrue(Wears(m_Net.Crew(0), 0, 8));
            m_Director.Profile.Data.suitColor = 0;
            var board = Object.FindAnyObjectByType<NetLobbyBoard>();
            board.Refresh();
            StringAssert.Contains(CrewLook.CrewName(m_Net, 0) + " (HOST)", board.status.text, "the crew list names the chosen colour");
            Assert.AreEqual(PlayerAvatar.SuitColorNames[CrewLook.PaletteIndexFor(0, 8)], CrewLook.CrewName(m_Net, 0));
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator AColourPickedAtTheLockerMidSession_ReachesTheCrewAtOnce_AndTheLocalSuitWearsIt()
        {
            Assert.IsTrue(m_Net.Host());
            var bot = AddBot(m_Net.Transport.Local);
            yield return Until(() => bot.Session.Welcomed && m_Net.Crew(1) != null);
            Assert.AreEqual(SuitLook.Default, bot.Session.SuitColour(0));
            var locker = Object.FindAnyObjectByType<SuitLockerPanel>();
            locker.Choose(5);
            Assert.AreEqual(5, m_Net.HostSession.SuitColour(0), "the host session has it the moment it is picked");
            Assert.IsTrue(CrewLook.HasLook(m_Player.avatar, CrewLook.GloveOf(m_Player.leftHand), CrewLook.GloveOf(m_Player.rightHand), 0, 5));
            yield return Until(() => bot.Session.SuitColour(0) == 5);
            Assert.AreEqual(5, bot.Session.SuitColour(0), "the crew is told");

            // The bot changes its own: its suit here changes.
            bot.Session.SendSettingsChanged(0, 11);
            yield return Until(() => Wears(m_Net.Crew(1), 1, 11));
            Assert.IsTrue(Wears(m_Net.Crew(1), 1, 11));
            m_Director.Profile.Data.suitColor = 0;
            m_Net.Leave();
            Assert.IsTrue(CrewLook.HasLook(m_Player.avatar, CrewLook.GloveOf(m_Player.leftHand), CrewLook.GloveOf(m_Player.rightHand), 0, 0), "solo again in the saved colour");
        }

        [UnityTest]
        public IEnumerator ToolCopiesKeepTheirOwnColours_AndTheTalkLightSitsOnTheHelmet()
        {
            Assert.IsTrue(m_Net.Host());
            var bot = AddBot(m_Net.Transport.Local);
            yield return Until(() => m_Net.Crew(1) != null);
            var crew = m_Net.Crew(1);
            Assert.IsNotNull(crew.tools, "the tool rig is on the stand-in");
            CrewLook.ApplyLook(crew, 1, 9);
            var suitRenderers = (crew.suit.accentRenderers ?? new Renderer[0]).Concat(crew.suit.fabricRenderers ?? new Renderer[0]).ToList();
            int toolRenderers = 0;
            foreach (var tool in crew.tools.tools)
            {
                if (tool == null) continue;
                foreach (var r in tool.GetComponentsInChildren<Renderer>(true))
                {
                    toolRenderers++;
                    Assert.IsFalse(suitRenderers.Contains(r), r.name + " is part of a tool: the suit colour does not touch it");
                }
            }
            Assert.Greater(toolRenderers, 0, "test set-up: the tool copies have renderers");
            var light = crew.head.Find(VoiceTalkLight.ChildName);
            Assert.IsNotNull(light, "the talk light is still on the helmet");
            Assert.AreSame(crew.head, light.parent);
            foreach (var tool in crew.tools.tools)
                if (tool != null) Assert.IsFalse(light.IsChildOf(tool), "the talk light is not part of a tool");
            m_Net.Leave();
        }

        [UnityTest]
        public IEnumerator ACrewMembersAcceptedDart_IsDrawnForTheOthers_NotSentBackToTheShooter()
        {
            Assert.IsTrue(m_Net.Host());
            var shooter = AddBot(m_Net.Transport.Local);
            var other = AddBot(m_Net.Transport.Local);
            yield return Until(() => shooter.Session.Welcomed && other.Session.Welcomed && m_Net.Crew(2) != null);
            shooter.Session.SendStartRequest();
            yield return Until(() => m_Director.Phase == MissionPhase.Active);
            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            creature.patrolRoute = new EnemyWaypoint[0];
            creature.transform.position = MapPoints.Freighter(0f, creature.hoverHeight, 14f);
            for (int i = 0; i < 4; i++) creature.Step(0.05f);
            yield return null;
            Vector3 body = creature.Walker.surface.position;
            shooter.Session.SendTranqHitReport(CoopConvert.ToNet(body + Vector3.back * 5f), CoopConvert.ToNet(body + Vector3.up * 0.3f));
            yield return Until(() => creature.IsSedated && other.World.Calls[NetMsg.TranqTracer] > 0);
            Assert.IsTrue(creature.IsSedated);
            Assert.AreEqual(1, other.World.Calls[NetMsg.TranqTracer], "the other crew member sees the shot");
            yield return Frames(10);
            Assert.AreEqual(0, shooter.World.Calls[NetMsg.TranqTracer], "the shooter drew its own");
            m_Net.Leave();
        }
    }
}

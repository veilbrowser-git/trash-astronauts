using System;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Player;
using UnityEngine;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Suit colours in the core: the 4-bit colour beside handedness in Hello, SettingsChanged and RosterChanged, in the
    /// Welcome roster, through the host and client sessions to everyone (late joiners included), the protocol bump, the
    /// save field, and the palette itself.
    /// </summary>
    public class SuitColourTests
    {
        NetTestRig m_Rig;

        [SetUp]
        public void SetUp() { m_Rig = new NetTestRig(); }

        [TearDown]
        public void TearDown()
        {
            m_Rig.Dispose();
            Assert.AreEqual(0, m_Rig.Hub.LiveCount, "every memory transport disposed");
        }

        NetTestRig.Member Join(byte handedness, byte colour)
        {
            var member = new NetTestRig.Member { Transport = m_Rig.Hub.Create(), World = new BotClientWorld() };
            member.SessionTransport = member.Transport;
            member.Session = new NetSessionClient(member.SessionTransport, member.World,
                new NetClientConfig { contentHash = NetTestRig.Content, handedness = handedness, suitColour = colour, seed = 300 + m_Rig.Members.Count });
            member.World.Session = member.Session;
            m_Rig.Members.Add(member);
            member.Session.Connect(m_Rig.HostTransport.Local, m_Rig.Now);
            Assert.IsTrue(m_Rig.StepUntil(() => member.Session.Welcomed, 10.0), "client did not join");
            return member;
        }

        // ------------------------------------------------------------------ the byte

        [Test]
        public void SettingsByte_KeepsHandednessInBitZero_AndTheColourInTheUpperFourBits()
        {
            for (byte h = 0; h <= 1; h++)
            {
                for (byte c = 0; c <= SuitLook.PaletteSize; c++)
                {
                    byte packed = SuitLook.Pack(h, c);
                    Assert.AreEqual(h, packed & 1, "an old reader's '& 1' still finds the handedness");
                    Assert.AreEqual(h, SuitLook.HandednessOf(packed));
                    Assert.AreEqual(c, SuitLook.ColourOf(packed));
                }
            }
            Assert.AreEqual(1, SuitLook.Pack(1, 0), "the default colour leaves the byte exactly as before");
            foreach (int bad in new[] { -1, 13, 14, 15, 16, 200, int.MaxValue }) Assert.AreEqual(SuitLook.Default, SuitLook.Clamp(bad), bad.ToString());
            Assert.AreEqual(SuitLook.Default, SuitLook.ColourOf(0xFF), "colour 15 on the wire reads as the default");
            Assert.AreEqual(1, SuitLook.HandednessOf(0xFF));
        }

        [Test]
        public void Messages_CarryTheColour_AndHandednessIsUnaffected()
        {
            var buffer = new byte[64];
            var w = new NetWriter(buffer);
            new HelloMsg { protocol = NetProtocol.Version, contentHash = 7, clientNonce = 9, handedness = 1, suitColour = 11 }.WriteDatagram(ref w);
            Assert.AreEqual(HelloMsg.Size, w.Position, "Hello did not grow");
            var r = new NetReader(buffer, 0, w.Position);
            Assert.IsTrue(Connectionless.TryReadFrame(ref r, out byte protocol, out byte type));
            Assert.IsTrue(HelloMsg.Read(ref r, protocol, out HelloMsg hello));
            Assert.AreEqual(1, hello.handedness);
            Assert.AreEqual(11, hello.suitColour);
            Assert.AreEqual(7u, hello.contentHash);

            w = new NetWriter(buffer);
            new SettingsChangedMsg { handedness = 0, suitColour = 12 }.Write(ref w);
            Assert.AreEqual(SettingsChangedMsg.MaxSize, w.Position);
            r = new NetReader(buffer, 0, w.Position);
            Assert.IsTrue(SettingsChangedMsg.Read(ref r, out SettingsChangedMsg settings));
            Assert.AreEqual(0, settings.handedness);
            Assert.AreEqual(12, settings.suitColour);

            w = new NetWriter(buffer);
            new RosterChangedMsg { slot = 3, change = RosterChange.Joined, handedness = 1, suitColour = 6 }.Write(ref w);
            Assert.AreEqual(RosterChangedMsg.MaxSize, w.Position);
            r = new NetReader(buffer, 0, w.Position);
            Assert.IsTrue(RosterChangedMsg.Read(ref r, out RosterChangedMsg roster));
            Assert.AreEqual(3, roster.slot);
            Assert.AreEqual(RosterChange.Joined, roster.change);
            Assert.AreEqual(1, roster.handedness);
            Assert.AreEqual(6, roster.suitColour);

            // A value too big for the palette goes out as the default.
            w = new NetWriter(buffer);
            new SettingsChangedMsg { handedness = 1, suitColour = 13 }.Write(ref w);
            r = new NetReader(buffer, 0, w.Position);
            Assert.IsTrue(SettingsChangedMsg.Read(ref r, out settings));
            Assert.AreEqual(1, settings.handedness);
            Assert.AreEqual(SuitLook.Default, settings.suitColour);
        }

        [Test]
        public void WelcomeRoster_CarriesEachSlotsColour_InItsSpareBits()
        {
            var welcome = new WelcomeMsg();
            welcome.world.Clear();
            welcome.SetSlot(0, true, 1, false, 12);
            welcome.SetSlot(1, true, 0, true, 1);
            welcome.SetSlot(2, false, 1, true, 9); // absent: nothing about it travels
            welcome.SetSlot(3, true, 1, true, 0);
            var buffer = new byte[WelcomeMsg.MaxSize];
            var w = new NetWriter(buffer);
            welcome.Write(ref w);
            var r = new NetReader(buffer, 0, w.Position);
            var read = new WelcomeMsg();
            Assert.IsTrue(WelcomeMsg.Read(ref r, ref read));
            Assert.AreEqual(12, read.SuitColour(0));
            Assert.AreEqual(1, read.SuitColour(1));
            Assert.AreEqual(0, read.SuitColour(2));
            Assert.AreEqual(0, read.SuitColour(3));
            Assert.AreEqual(1, read.Handedness(0));
            Assert.AreEqual(0, read.Handedness(1));
            Assert.AreEqual(1, read.Handedness(3));
            Assert.IsFalse(read.IsAway(0));
            Assert.IsTrue(read.IsAway(1));
            Assert.IsFalse(read.IsPresent(2));
            Assert.AreEqual(3, read.PlayerCount);
            Assert.AreEqual(0, read.SuitColour(-1));
            Assert.AreEqual(0, read.SuitColour(4));

            // The old four-argument SetSlot still works and means "default colour".
            welcome.SetSlot(0, true, 1, false);
            Assert.AreEqual(0, welcome.SuitColour(0));
            Assert.AreEqual(1, welcome.Handedness(0));

            // Every bit set on the wire: present, left-handed, away, colour 15 (out of range, so the default); bit 3 ignored.
            for (int i = 0; i < NetProtocol.MaxPlayers; i++) buffer[i] = 0xFF;
            r = new NetReader(buffer, 0, w.Position);
            Assert.IsTrue(WelcomeMsg.Read(ref r, ref read));
            Assert.AreEqual(0, read.SuitColour(0));
            Assert.IsTrue(read.IsPresent(0) && read.IsAway(0));
            Assert.AreEqual(1, read.Handedness(0));
        }

        [Test]
        public void GarbageSettingsBytes_NeverThrow()
        {
            var random = new System.Random(5);
            var buffer = new byte[WelcomeMsg.MaxSize + 8];
            for (int round = 0; round < 2000; round++)
            {
                random.NextBytes(buffer);
                int length = random.Next(0, buffer.Length);
                Assert.DoesNotThrow(() =>
                {
                    var r = new NetReader(buffer, 0, length);
                    if (SettingsChangedMsg.Read(ref r, out SettingsChangedMsg s)) Assert.That(s.suitColour, Is.InRange(0, SuitLook.PaletteSize));
                    r = new NetReader(buffer, 0, length);
                    if (RosterChangedMsg.Read(ref r, out RosterChangedMsg m)) Assert.That(m.suitColour, Is.InRange(0, SuitLook.PaletteSize));
                    r = new NetReader(buffer, 0, length);
                    if (HelloMsg.Read(ref r, NetProtocol.Version, out HelloMsg h)) Assert.That(h.suitColour, Is.InRange(0, SuitLook.PaletteSize));
                    r = new NetReader(buffer, 0, length);
                    var welcome = new WelcomeMsg();
                    if (WelcomeMsg.Read(ref r, ref welcome))
                        for (int i = 0; i < NetProtocol.MaxPlayers; i++) Assert.That(welcome.SuitColour(i), Is.InRange(0, SuitLook.PaletteSize));
                });
            }
        }

        // ------------------------------------------------------------------ sessions

        [Test]
        public void Colour_TravelsHelloToRosterToWelcome_ForALateJoiner()
        {
            m_Rig.Host.SetLocalLook(0, 5);
            NetTestRig.Member a = Join(1, 3);
            Assert.AreEqual(3, m_Rig.Host.SuitColour(1), "the Hello carried the joiner's colour");
            Assert.AreEqual(3, m_Rig.HostWorld.SuitColour[1], "the host's world heard it with the join");
            Assert.AreEqual(1, m_Rig.Host.Handedness(1));
            Assert.AreEqual(1, m_Rig.HostWorld.Handedness[1]);
            Assert.AreEqual(5, a.Session.SuitColour(0), "the host's colour came in the Welcome");
            Assert.AreEqual(5, a.World.SuitColour[0]);
            Assert.AreEqual(3, a.Session.SuitColour(1), "a client knows its own colour");
            Assert.AreEqual(0, a.World.Handedness[0]);
            Assert.AreEqual(1, a.World.Handedness[1]);

            NetTestRig.Member late = Join(0, 12);
            m_Rig.Step(0.5);
            Assert.AreEqual(5, late.Session.SuitColour(0), "the late joiner sees the host's colour (Welcome)");
            Assert.AreEqual(3, late.Session.SuitColour(1), "and the first client's (Welcome)");
            Assert.AreEqual(5, late.World.SuitColour[0]);
            Assert.AreEqual(3, late.World.SuitColour[1]);
            Assert.AreEqual(1, late.World.Handedness[1], "handedness still travels beside it");
            Assert.AreEqual(12, a.Session.SuitColour(2), "the first client hears the late joiner's colour (RosterChanged Joined)");
            Assert.AreEqual(12, a.World.SuitColour[2]);
            Assert.AreEqual(0, a.World.Handedness[2]);
            Assert.AreEqual(12, m_Rig.Host.SuitColour(2));
            Assert.AreEqual(2, late.World.LookCalls, "the late joiner was told about exactly the two others");
        }

        [Test]
        public void MidSessionChange_ReachesEveryone_IncludingTheHost_AndKeepsHandedness()
        {
            NetTestRig.Member a = Join(1, 0);
            NetTestRig.Member b = Join(0, 2);

            a.Session.SendSettingsChanged(1, 9);
            Assert.IsTrue(m_Rig.StepUntil(() => b.Session.SuitColour(1) == 9, 5.0), "the other client hears the change");
            Assert.AreEqual(9, m_Rig.Host.SuitColour(1));
            Assert.AreEqual(9, m_Rig.HostWorld.SuitColour[1], "the host's own world hears it (OnCrewLook)");
            Assert.AreEqual(9, b.World.SuitColour[1]);
            Assert.AreEqual(1, m_Rig.Host.Handedness(1), "handedness unchanged");
            Assert.AreEqual(1, m_Rig.HostWorld.Handedness[1]);
            Assert.AreEqual(1, b.World.Handedness[1]);
            Assert.AreEqual(9, a.Session.LocalSuitColour);

            // The old overload changes handedness only and keeps the colour.
            a.Session.SendSettingsChanged(0);
            Assert.IsTrue(m_Rig.StepUntil(() => b.World.Handedness[1] == 0, 5.0));
            Assert.AreEqual(9, b.Session.SuitColour(1));
            Assert.AreEqual(9, m_Rig.Host.SuitColour(1));
            Assert.AreEqual(0, m_Rig.Host.Handedness(1));

            // The host's own change reaches both clients; its handedness stays.
            m_Rig.Host.SetLocalHandedness(1);
            m_Rig.Host.SetLocalLook(1, 11);
            Assert.IsTrue(m_Rig.StepUntil(() => a.Session.SuitColour(0) == 11 && b.Session.SuitColour(0) == 11, 5.0));
            Assert.AreEqual(11, a.World.SuitColour[0]);
            Assert.AreEqual(1, a.World.Handedness[0]);
            Assert.AreEqual(1, b.World.Handedness[0]);
            m_Rig.Host.SetLocalHandedness(0);
            Assert.IsTrue(m_Rig.StepUntil(() => a.World.Handedness[0] == 0, 5.0));
            Assert.AreEqual(11, m_Rig.Host.SuitColour(0), "SetLocalHandedness keeps the colour");
            Assert.AreEqual(11, a.Session.SuitColour(0));

            // Back to the default.
            b.Session.SendSettingsChanged(0, SuitLook.Default);
            Assert.IsTrue(m_Rig.StepUntil(() => a.Session.SuitColour(2) == 0, 5.0));
            Assert.AreEqual(0, m_Rig.Host.SuitColour(2));
        }

        [Test]
        public void OutOfRangeColours_AreClampedToTheDefault()
        {
            var host = new NetSessionHost(m_Rig.Hub.Create(), new ScriptedHostWorld(), new NetHostConfig { suitColour = 99 });
            Assert.AreEqual(0, host.SuitColour(0));
            host.SetLocalLook(0, 13);
            Assert.AreEqual(0, host.SuitColour(0));
            host.Dispose();

            NetTestRig.Member a = Join(0, 200);
            Assert.AreEqual(0, a.Session.LocalSuitColour);
            Assert.AreEqual(0, m_Rig.Host.SuitColour(1));
            a.Session.SendSettingsChanged(1, 16);
            Assert.IsTrue(m_Rig.StepUntil(() => m_Rig.Host.Handedness(1) == 1, 5.0));
            Assert.AreEqual(0, m_Rig.Host.SuitColour(1));
            Assert.AreEqual(0, a.Session.LocalSuitColour);
        }

        [Test]
        public void LeavingSlot_ForgetsItsColour()
        {
            NetTestRig.Member a = Join(0, 4);
            NetTestRig.Member b = Join(0, 7);
            Assert.IsTrue(m_Rig.StepUntil(() => a.Session.SuitColour(2) == 7, 5.0));
            b.Session.Leave(m_Rig.Now);
            Assert.IsTrue(m_Rig.StepUntil(() => !a.World.Present[2], 5.0));
            Assert.AreEqual(0, a.Session.SuitColour(2));
            Assert.AreEqual(0, m_Rig.Host.SuitColour(2));
        }

        [Test]
        public void OldBuild_IsRefusedCleanly()
        {
            Assert.GreaterOrEqual(NetProtocol.Version, 2, "the suit colour bits changed the wire format");
            NetTestRig.Member old = m_Rig.AddClient(protocol: 1);
            Assert.IsTrue(m_Rig.StepUntil(() => old.Session.State == ClientState.Ended, 3.0));
            Assert.AreEqual(RefuseReason.VersionMismatch, old.Session.LastRefusal);
            Assert.AreEqual(0, m_Rig.HostWorld.JoinCount);
        }

        // ------------------------------------------------------------------ save and palette

        [Test]
        public void Save_DefaultsToCrewColour_OldSavesLoadUnchanged_AndAChoiceSurvives()
        {
            Assert.AreEqual(0, new SaveData().suitColor, "a fresh profile wears its crew colour");

            var storage = new MemorySaveStorage
            {
                Contents = "{\"version\":1,\"totalCredits\":420,\"handedness\":1,\"snapTurnDegrees\":60.0,\"tutorialCompleted\":true}",
            };
            var profile = new ProfileService(storage);
            profile.Load();
            Assert.IsFalse(profile.LoadedFromDefaults);
            Assert.AreEqual(0, profile.Data.suitColor, "a save from before suit colours wears the crew colour");
            Assert.AreEqual(420, profile.Data.totalCredits);
            Assert.AreEqual(Handedness.Left, profile.Data.Handedness);

            profile.Data.suitColor = 7;
            profile.Save();
            var again = new ProfileService(storage);
            again.Load();
            Assert.AreEqual(7, again.Data.suitColor);

            storage.Contents = storage.Contents.Replace("\"suitColor\": 7", "\"suitColor\": 99");
            again.Load();
            Assert.AreEqual(0, again.Data.suitColor, "a damaged value falls back to the crew colour");
        }

        [Test]
        public void Palette_HasTwelveNamedColours_CrewColoursFirst_AllClearlyDifferent()
        {
            Assert.AreEqual(SuitLook.PaletteSize, PlayerAvatar.SuitColors.Length);
            Assert.AreEqual(PlayerAvatar.SuitColors.Length, PlayerAvatar.SuitColorNames.Length);
            for (int i = 0; i < PlayerAvatar.CrewColors.Length; i++) Assert.AreEqual(PlayerAvatar.CrewColors[i], PlayerAvatar.SuitColors[i], "crew colour " + i);
            for (int i = 0; i < PlayerAvatar.SuitColors.Length; i++)
            {
                Assert.IsFalse(string.IsNullOrEmpty(PlayerAvatar.SuitColorNames[i]));
                for (int j = i + 1; j < PlayerAvatar.SuitColors.Length; j++)
                {
                    Color a = PlayerAvatar.SuitColors[i], b = PlayerAvatar.SuitColors[j];
                    float d = Mathf.Sqrt((a.r - b.r) * (a.r - b.r) + (a.g - b.g) * (a.g - b.g) + (a.b - b.b) * (a.b - b.b));
                    Assert.Greater(d, 0.25f, PlayerAvatar.SuitColorNames[i] + " vs " + PlayerAvatar.SuitColorNames[j]);
                    Assert.AreNotEqual(PlayerAvatar.SuitColorNames[i], PlayerAvatar.SuitColorNames[j]);
                }
                Color wash = PlayerAvatar.WashOf(PlayerAvatar.SuitColors[i]);
                Assert.Greater(wash.r + wash.g + wash.b, 1.8f, "the fabric wash stays light: " + PlayerAvatar.SuitColorNames[i]);
            }
        }
    }
}

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.UI;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Suit colours in the real scene: the SUIT LOCKER dresses the local suit and gloves, saves the choice and a new
    /// launch restores it; the default leaves a solo player exactly as before; the panel is reachable and clear of
    /// everything else; CrewLook dresses remote crew; and in a session the choice reaches the others both ways.
    /// </summary>
    public class SuitColourPlayTests : NetPlayFixture
    {
        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int k_BaseMap = Shader.PropertyToID("_BaseMap");

        SuitLockerPanel Locker()
        {
            var locker = Object.FindAnyObjectByType<SuitLockerPanel>();
            Assert.IsNotNull(locker, "The tug has a SUIT LOCKER.");
            return locker;
        }

        static GloveVisual[] GlovesOf(PlayerContext player) => new[] { CrewLook.GloveOf(player.leftHand), CrewLook.GloveOf(player.rightHand) };

        /// <summary>Colours set through a property block come back converted to linear and back: compare loosely.</summary>
        static void Near(Color expected, Color actual, string message)
        {
            Assert.IsTrue(Mathf.Abs(expected.r - actual.r) < 0.01f && Mathf.Abs(expected.g - actual.g) < 0.01f && Mathf.Abs(expected.b - actual.b) < 0.01f,
                message + ": expected " + expected + " but was " + actual);
        }

        static Color AccentBlock(PlayerAvatar avatar)
        {
            var block = new MaterialPropertyBlock();
            avatar.accentRenderers[0].GetPropertyBlock(block);
            return block.GetColor(k_BaseColor);
        }

        static void AssertWearing(PlayerAvatar suit, GloveVisual[] gloves, int paletteIndex, string who)
        {
            Color c = PlayerAvatar.SuitColors[paletteIndex];
            Assert.AreEqual(paletteIndex, suit.SuitColorIndex, who + ": suit colour");
            Assert.AreEqual(c, suit.Tint, who + ": stripes");
            Assert.AreEqual(PlayerAvatar.WashOf(c), suit.FabricTint, who + ": fabric wash");
            Near(c, AccentBlock(suit), who + ": the stripes' property block");
            Assert.Greater(suit.fabricRenderers.Length, 0, who + ": the suit has fabric to wash");
            var block = new MaterialPropertyBlock();
            foreach (var r in suit.fabricRenderers)
            {
                Assert.AreEqual(2, r.sharedMaterials.Length, r.name);
                r.GetPropertyBlock(block, 1);
                Near(PlayerAvatar.WashOf(c), block.GetColor(k_BaseColor), who + ": " + r.name + " fabric");
                r.GetPropertyBlock(block, 0);
                Assert.IsFalse(block.HasColor(k_BaseColor), who + ": " + r.name + " hardware (rings, rubber, boots) stays untinted");
            }
            foreach (var g in gloves)
            {
                Assert.IsNotNull(g, who + ": glove");
                Assert.AreEqual(paletteIndex, g.BandColor, who + ": glove band");
                Assert.Greater(g.bandRenderers.Length, 0);
                foreach (var r in g.bandRenderers)
                {
                    if (paletteIndex == 0)
                    {
                        Assert.IsFalse(r.HasPropertyBlock(), who + ": the orange glove is the modelled one");
                        continue;
                    }
                    r.GetPropertyBlock(block);
                    Assert.AreSame(g.bandAtlases[paletteIndex], block.GetTexture(k_BaseMap), who + ": " + r.name + " band atlas");
                }
            }
        }

        // ------------------------------------------------------------------ the locker, alone

        [UnityTest]
        public IEnumerator PressingASwatch_DressesSuitAndGloves_SavesIt_AndTheNextLaunchRestoresIt()
        {
            var locker = Locker();
            Assert.AreEqual(12, locker.swatches.Length);
            int cyan = System.Array.IndexOf(PlayerAvatar.SuitColorNames, "CYAN");
            Assert.IsTrue(locker.swatches[cyan].Press(null), "the swatch takes a press");
            yield return null;

            AssertWearing(m_Player.avatar, GlovesOf(m_Player), cyan, "local");
            Assert.AreEqual(cyan + 1, m_Director.Profile.Data.suitColor);
            StringAssert.Contains("\"suitColor\": " + (cyan + 1), m_Storage.Contents, "saved at once");
            StringAssert.Contains("CYAN", locker.status.text);
            Assert.IsTrue(locker.swatchMark.gameObject.activeSelf, "the choice is marked");
            Assert.IsFalse(locker.crewMark.gameObject.activeSelf);
            Assert.Less(Vector2.Distance(locker.swatchMark.position, locker.swatches[cyan].transform.position), 0.01f, "the mark sits on the chosen swatch");
            Assert.AreEqual(cyan, locker.dummy.SuitColorIndex, "the display suit shows the choice");
            yield return null;
            Assert.AreEqual(cyan, m_Player.avatar.SuitColorIndex, "nothing undoes it a frame later");

            // A new launch: the same save, a fresh scene.
            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            yield return null;
            yield return null;
            m_Director = MissionDirector.Active;
            m_Player = m_Director.Players.First();
            m_Net = m_Director.GetComponent<NetSession>();
            Assert.AreEqual(cyan + 1, m_Director.Profile.Data.suitColor, "the choice was loaded");
            AssertWearing(m_Player.avatar, GlovesOf(m_Player), cyan, "after relaunch");
            var again = Locker();
            Assert.IsTrue(again.swatchMark.gameObject.activeSelf);
            StringAssert.Contains("CYAN", again.status.text);

            // CREW COLOUR goes back to the default.
            Assert.IsTrue(again.crewColourButton.Press(null));
            yield return null;
            Assert.AreEqual(0, m_Director.Profile.Data.suitColor);
            Assert.AreEqual(-1, m_Player.avatar.SuitColorIndex);
            Assert.AreEqual(PlayerAvatar.CrewColors[0], m_Player.avatar.Tint);
            Assert.AreEqual(Color.white, m_Player.avatar.FabricTint);
            foreach (var g in GlovesOf(m_Player)) Assert.AreEqual(0, g.BandColor);
            Assert.IsTrue(again.crewMark.gameObject.activeSelf);
            Assert.IsFalse(again.swatchMark.gameObject.activeSelf);
        }

        [UnityTest]
        public IEnumerator Default_LeavesASoloPlayerExactlyAsBefore()
        {
            yield return null;
            yield return null;
            Assert.AreEqual(0, m_Director.Profile.Data.suitColor, "a fresh profile wears its crew colour");
            var avatar = m_Player.avatar;
            Assert.AreEqual(PlayerAvatar.CrewColors[0], avatar.Tint);
            Near(PlayerAvatar.CrewColors[0], AccentBlock(avatar), "stripes");
            Assert.AreEqual(-1, avatar.SuitColorIndex);
            Assert.AreEqual(Color.white, avatar.FabricTint);
            Assert.Greater(avatar.fabricRenderers.Length, 0);
            foreach (var r in avatar.fabricRenderers)
            {
                Assert.IsFalse(r.HasPropertyBlock(), r.name + ": no block on the fabric, as before");
                Material hardware = r.sharedMaterials[0], fabric = r.sharedMaterials[1];
                Assert.AreSame(hardware.mainTexture, fabric.mainTexture, "the fabric shares the suit atlas");
                Assert.AreEqual(Color.white, fabric.GetColor(k_BaseColor));
                Assert.AreEqual(hardware.GetColor(k_BaseColor), fabric.GetColor(k_BaseColor));
                Assert.AreEqual(hardware.shader, fabric.shader);
            }
            foreach (var g in GlovesOf(m_Player))
            {
                Assert.AreEqual(0, g.BandColor);
                Assert.AreEqual(PlayerAvatar.SuitColors.Length, g.bandAtlases.Length, "one band atlas per palette colour");
                foreach (var r in g.bandRenderers)
                {
                    Assert.IsFalse(r.HasPropertyBlock(), r.name + ": the glove is untouched");
                    Assert.AreSame(g.bandAtlases[0], r.sharedMaterial.mainTexture, "entry 0 is the modelled atlas");
                }
            }
            for (int i = 1; i < 30; i++) yield return null;
            Assert.AreEqual(-1, avatar.SuitColorIndex, "nothing changes on its own");
            Assert.IsFalse(m_Storage.Contents != null && m_Storage.Contents.Contains("\"suitColor\": 1"), "nothing was chosen for the player");
        }

        [UnityTest]
        public IEnumerator Locker_ButtonsAreReachable_AndThePanelBlocksNothing()
        {
            yield return null;
            var locker = Locker();
            var buttons = locker.swatches.Concat(new[] { locker.crewColourButton }).ToArray();
            Assert.AreEqual(13, buttons.Length);
            Vector3 face = -locker.transform.forward; // the way the panel looks
            // Someone using it stands in front of the buttons (the right half; the display suit is on the left).
            Vector3 stand = locker.transform.TransformPoint(new Vector3(0.21f, 0f, 0f)) + face * 0.45f;
            stand.y = 0f;
            Vector3 eyes = stand + Vector3.up * 1.6f;
            var colliders = buttons.Select(b => b.GetComponent<BoxCollider>()).ToArray();
            for (int i = 0; i < buttons.Length; i++)
            {
                var b = buttons[i];
                Assert.AreEqual(GameLayers.Button, b.gameObject.layer, b.name);
                Assert.IsTrue(colliders[i].isTrigger && colliders[i].enabled, b.name);
                Assert.Greater(Vector3.Dot(b.transform.forward, -face), 0.99f, b.name + " faces into the bay");
                Assert.GreaterOrEqual(colliders[i].size.x * b.transform.lossyScale.x, 0.13f, b.name + " is finger-sized");
                Assert.GreaterOrEqual(colliders[i].size.y * b.transform.lossyScale.y, 0.085f, b.name + " is finger-sized");
                Vector3 centre = b.transform.TransformPoint(colliders[i].center);
                Assert.That(centre.y, Is.InRange(1.1f, 2.0f), b.name + " is at hand height");
                Assert.Less(Vector3.Distance(eyes + Vector3.down * 0.25f, centre), 0.75f, b.name + " is within arm's reach of someone standing at the panel");
                Assert.IsFalse(Physics.Linecast(eyes, centre - face * -0.03f, ~(1 << GameLayers.Button), QueryTriggerInteraction.Ignore), b.name + " can be seen and pointed at");
                for (int j = i + 1; j < buttons.Length; j++)
                    Assert.IsFalse(colliders[i].bounds.Intersects(colliders[j].bounds), b.name + " overlaps " + buttons[j].name);
            }

            // There is room to stand in front of it, inside the hub's safe area.
            var blocking = Physics.OverlapCapsule(stand + Vector3.up * 0.3f, stand + Vector3.up * 1.6f, 0.2f, ~0, QueryTriggerInteraction.Ignore)
                .Where(c => !c.transform.IsChildOf(m_Player.transform)).ToArray();
            Assert.IsEmpty(blocking, "Something stands where a player uses the locker: " + string.Join(", ", blocking.Select(c => c.name)));
            Assert.IsTrue(m_Director.safeZone.Contains(eyes), "the locker is inside the hub's safe area");

            // Clear of the other panels and fittings, the doorway and the crew spots.
            var bounds = Bounds(locker.gameObject);
            bounds.Expand(-0.01f);
            foreach (string name in new[] { "Company Store", "Lockers", "Co-op Crew Panel", "Mission Board", "Tutorial Board", "Suit Settings Console", "Collection Locker", "Crew Record" })
            {
                foreach (var go in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Where(t => t.name == name).Select(t => t.gameObject))
                    Assert.IsFalse(bounds.Intersects(Bounds(go)), "The SUIT LOCKER overlaps " + name);
            }
            var doorway = new Bounds(new Vector3(-2f, 1.05f, -4.8f), new Vector3(1.4f, 2.1f, 1.6f));
            Assert.IsFalse(bounds.Intersects(doorway), "clear of the doorway to the flight deck");
            for (int slot = 0; slot < NetProtocol.MaxPlayers; slot++)
                Assert.Greater(Vector3.Distance(m_Director.CrewSpot(slot), stand), 2f, "nobody spawns at the locker");
        }

        static Bounds Bounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            var b = renderers.Length > 0 ? renderers[0].bounds : new Bounds(go.transform.position, Vector3.zero);
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            return b;
        }

        // ------------------------------------------------------------------ crew

        [UnityTest]
        public IEnumerator ApplyLook_DressesARemoteAvatar_AndFallsBackToTheSlotColourForZero()
        {
            Assert.IsTrue(m_Net.Host(), "HOST CO-OP");
            var bot = AddBot(m_Net.Transport.Local, handedness: 1);
            yield return Until(() => bot.Session.Welcomed && m_Net.Crew(1) != null);
            var crew = m_Net.Crew(1);
            var gloves = new[] { crew.leftGlove, crew.rightGlove };

            CrewLook.ApplyLook(crew, 1, 9);
            AssertWearing(crew.suit, gloves, 8, "remote, colour 9");
            CrewLook.ApplyLook(crew, 1, SuitLook.Default);
            Assert.AreEqual(PlayerAvatar.CrewColors[1], crew.Tint, "0 = the slot's colour");
            Assert.AreEqual(-1, crew.suit.SuitColorIndex);
            Assert.AreEqual(Color.white, crew.suit.FabricTint, "no wash for the default");
            foreach (var g in gloves) Assert.AreEqual(1, g.BandColor, "the glove bands take the slot colour");
            CrewLook.ApplyLook(crew, 2, 0);
            Assert.AreEqual(PlayerAvatar.CrewColors[2], crew.Tint);
            CrewLook.ApplyLook(crew, 1, 99);
            Assert.AreEqual(PlayerAvatar.CrewColors[1], crew.Tint, "out of range = the default");
            Assert.AreEqual(1, m_Net.HostSession.Handedness(1), "the look leaves handedness alone");

            // The bot picks PURPLE: the host's scene dresses its stand-in.
            int purple = System.Array.IndexOf(PlayerAvatar.SuitColorNames, "PURPLE");
            bot.Session.SendSettingsChanged(1, (byte)(purple + 1));
            yield return Until(() => crew.suit.SuitColorIndex == purple);
            AssertWearing(crew.suit, gloves, purple, "remote after its SettingsChanged");
            Assert.AreEqual(1, m_Net.HostSession.Handedness(1), "still left-handed");

            // The host picks YELLOW at the locker: the bot hears it, and the host keeps its slot.
            int yellow = System.Array.IndexOf(PlayerAvatar.SuitColorNames, "YELLOW");
            Assert.IsTrue(Locker().swatches[yellow].Press(null));
            yield return Until(() => bot.Session.SuitColour(0) == yellow + 1);
            Assert.AreEqual(yellow + 1, bot.Session.SuitColour(0));
            Assert.AreEqual(yellow + 1, bot.World.SuitColour[0]);
            AssertWearing(m_Player.avatar, GlovesOf(m_Player), yellow, "host");
            Assert.AreEqual(PlayerAvatar.SuitColors[purple], crew.Tint, "the stand-in keeps its own colour");
            Assert.AreEqual("YELLOW", CrewLook.CrewName(m_Net, 0), "the crew list names the host's choice");
            Assert.AreEqual("PURPLE", CrewLook.CrewName(m_Net, 1), "and the joiner's");
            Assert.AreEqual("GREEN", CrewLook.CrewName(m_Net, 2), "an empty slot answers with its own colour");
        }

        [UnityTest]
        public IEnumerator Client_JoinsWithItsSavedColour_SeesTheHostsAndKeepsItWhenTheSlotIsApplied()
        {
            int lime = System.Array.IndexOf(PlayerAvatar.SuitColorNames, "LIME");
            int red = System.Array.IndexOf(PlayerAvatar.SuitColorNames, "RED");
            m_Director.Profile.Data.suitColor = lime + 1;
            m_Director.Profile.Save();
            var host = AddScriptedHost();
            host.Session.SetLocalLook(0, (byte)(red + 1));
            Assert.IsTrue(m_Net.Join(host.Endpoint));
            yield return Until(() => m_Net.Mode == LobbyMode.Joined && m_Net.Crew(0) != null && host.Session.SuitColour(1) == lime + 1);
            Assert.AreEqual(lime + 1, host.Session.SuitColour(1), "the host learned this headset's colour");
            Assert.AreEqual(lime + 1, host.World.SuitColour[1]);
            yield return Frames(3);
            AssertWearing(m_Player.avatar, GlovesOf(m_Player), lime, "slot 1 keeps its chosen colour, not blue");
            var hostCrew = m_Net.Crew(0);
            AssertWearing(hostCrew.suit, new[] { hostCrew.leftGlove, hostCrew.rightGlove }, red, "the host as seen here");

            // The host changes its mind mid-session.
            host.Session.SetLocalLook(0, SuitLook.Default);
            yield return Until(() => hostCrew.suit.SuitColorIndex == -1);
            Assert.AreEqual(PlayerAvatar.CrewColors[0], hostCrew.Tint, "back to the host's slot colour");
            m_Net.Leave();
            yield return Frames(3);
            AssertWearing(m_Player.avatar, GlovesOf(m_Player), lime, "solo again, still lime");
        }
    }
}

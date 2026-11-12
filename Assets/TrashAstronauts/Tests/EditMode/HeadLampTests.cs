using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.Tools;
using TrashAstronauts.World;
using UnityEditor;
using UnityEngine;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// The helmet lamp outside play: buying it, keeping it in the save, the AvatarSnapshot bit that tells crew mates,
    /// and how the PlayerRig and RemoteCrew prefabs carry it.
    /// </summary>
    public class HeadLampTests
    {
        const string RigPath = "Assets/TrashAstronauts/Prefabs/PlayerRig.prefab";
        const string RemotePath = "Assets/TrashAstronauts/Prefabs/RemoteCrew.prefab";
        MissionConfig m_Config;
        MemorySaveStorage m_Storage;
        ProfileService m_Profile;

        [SetUp]
        public void SetUp()
        {
            m_Config = ScriptableObject.CreateInstance<MissionConfig>();
            m_Storage = new MemorySaveStorage();
            m_Profile = new ProfileService(m_Storage);
            m_Profile.Load();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(m_Config);

        // ------------------------------------------------------------------ the store

        [Test]
        public void Buying_DeductsTheCreditsOnce_Persists_AndCannotBeBoughtTwice()
        {
            Assert.IsFalse(m_Profile.IsOwned(ShopItem.HeadLamp), "A new profile has no lamp.");
            Assert.IsFalse(m_Profile.TryPurchase(ShopItem.HeadLamp, m_Config), "No credits, no lamp.");

            int cost = m_Config.GetShopCost(ShopItem.HeadLamp);
            Assert.AreEqual(m_Config.headLampCost, cost);
            m_Profile.Data.totalCredits = cost * 3;
            int writes = m_Storage.WriteCount;
            Assert.IsTrue(m_Profile.TryPurchase(ShopItem.HeadLamp, m_Config));
            Assert.AreEqual(cost * 2, m_Profile.Data.totalCredits, "Paid for once.");
            Assert.IsTrue(m_Profile.IsOwned(ShopItem.HeadLamp));
            Assert.Greater(m_Storage.WriteCount, writes, "The purchase is saved at once.");

            Assert.IsFalse(m_Profile.CanPurchase(ShopItem.HeadLamp, m_Config));
            Assert.IsFalse(m_Profile.TryPurchase(ShopItem.HeadLamp, m_Config), "Bought once, kept forever: not sold twice.");
            Assert.AreEqual(cost * 2, m_Profile.Data.totalCredits);

            var later = new ProfileService(m_Storage);
            later.Load();
            Assert.IsTrue(later.Data.headLamp, "Still yours after a restart.");
            Assert.IsTrue(later.IsOwned(ShopItem.HeadLamp));
        }

        [Test]
        public void ASaveFromBeforeTheLamp_LoadsWithoutIt_AndKeepsEverythingElse()
        {
            m_Storage.Contents = "{\"version\":1,\"totalCredits\":740,\"scannerUpgrade\":true,\"tranqDarts\":2}";
            var profile = new ProfileService(m_Storage);
            profile.Load();
            Assert.IsFalse(profile.LoadedFromDefaults);
            Assert.IsFalse(profile.Data.headLamp);
            Assert.IsFalse(profile.IsOwned(ShopItem.HeadLamp));
            Assert.AreEqual(740, profile.Data.totalCredits);
            Assert.IsTrue(profile.Data.scannerUpgrade);
            Assert.AreEqual(2, profile.Data.tranqDarts);
        }

        [Test]
        public void Price_MakesItAFirstOrSecondPurchase()
        {
            int lamp = m_Config.headLampCost;
            int contract = m_Config.quotaCredits + m_Config.contractBonusCredits;
            Assert.Greater(lamp, m_Config.tranqDartCost, "Dearer than a single dart...");
            Assert.LessOrEqual(lamp, m_Config.scannerUpgradeCost, "...no dearer than the cheapest upgrade...");
            Assert.Less(lamp, m_Config.oxygenUpgradeCost);
            Assert.Less(lamp * 3, contract, "...and one met contract buys it with plenty to spare.");
        }

        // ------------------------------------------------------------------ the wire

        [Test]
        public void SnapshotFlag_HeadLampOn_IsTheFormerlyFreeTopBit_AndRoundTrips()
        {
            Assert.AreEqual(0x80, AvatarFlags.HeadLampOn);
            byte others = AvatarFlags.ThrusterLeft | AvatarFlags.ThrusterRight | AvatarFlags.FlashlightOn | AvatarFlags.CutterFiring
                          | AvatarFlags.Cutting | AvatarFlags.Away | AvatarFlags.LeftHanded;
            Assert.AreEqual(0x7F, others, "The other seven bits are all taken.");

            var buffer = new byte[64];
            for (int value = 0; value < 256; value++)
            {
                var a = AvatarSnapshot.Default;
                a.flags = (byte)value;
                var w = new NetWriter(buffer);
                a.Write(ref w);
                Assert.AreEqual(AvatarSnapshot.Size, w.Position, "Still 35 bytes: wire-compatible in size.");
                var r = new NetReader(buffer, 0, w.Position);
                Assert.IsTrue(AvatarSnapshot.Read(ref r, out AvatarSnapshot b));
                Assert.AreEqual((byte)value, b.flags, "All eight flag bits survive.");
                Assert.AreEqual((value & 0x80) != 0, b.HasFlag(AvatarFlags.HeadLampOn));
            }
        }

        // ------------------------------------------------------------------ the prefabs

        [Test]
        public void PlayerRig_HasTheLamp_AtTheHelmetsLampHousing_WiderAndDimmerThanTheFlashlight_WithAHiddenWristButton()
        {
            var rig = AssetDatabase.LoadAssetAtPath<GameObject>(RigPath);
            Assert.IsNotNull(rig);
            var context = rig.GetComponent<PlayerContext>();
            var lamp = rig.GetComponentInChildren<HeadLamp>(true);
            Assert.IsNotNull(lamp, "The rig carries a helmet lamp.");
            Assert.AreSame(context, lamp.player);
            Assert.AreSame(context.head, lamp.transform.parent, "It hangs straight off the camera: it follows the head exactly, no lag.");

            // The housing's lens in build_player.py is (0.146, 0.075, 0.087) from the centre of the head.
            Vector3 expected = context.avatar.headCentreOffset + new Vector3(0.146f, 0.075f, 0.087f);
            Assert.Less(Vector3.Distance(expected, lamp.transform.localPosition), 0.005f, "The light comes from the lamp housing.");
            Assert.Less(Vector3.Angle(Vector3.forward, lamp.transform.localRotation * Vector3.forward), 10f, "It shines where the wearer looks.");

            var light = lamp.beam;
            Assert.IsNotNull(light);
            Assert.AreEqual(LightType.Spot, light.type);
            Assert.IsFalse(light.enabled, "Off until switched on.");
            Assert.AreEqual(LightShadows.None, light.shadows, "No realtime shadows on Quest.");
            Assert.IsNotNull(light.cookie, "A soft cookie, like the flashlight's.");
            var torch = context.tools.OfType<FlashlightTool>().First().beam;
            Assert.Greater(light.spotAngle, torch.spotAngle, "Wider than the hand flashlight...");
            Assert.Less(light.intensity, torch.intensity, "...and dimmer.");
            Assert.LessOrEqual(light.range, torch.range);

            var button = lamp.wristButton;
            Assert.IsNotNull(button, "A LAMP button on the wrist.");
            Assert.IsTrue(button.transform.IsChildOf(context.wristDisplay), "It travels with the wrist display.");
            StringAssert.Contains("LAMP", button.Text);
            Assert.IsFalse(button.gameObject.activeSelf, "Not there until the lamp is bought.");
            Assert.AreEqual(GameLayers.Button, button.gameObject.layer, "Fingers and pointers can press it.");
        }

        [Test]
        public void VisorQuads_AreUnlit_SoNoLampCanGlareOnThem()
        {
            var rig = AssetDatabase.LoadAssetAtPath<GameObject>(RigPath);
            var visor = rig.GetComponent<PlayerContext>().visor;
            foreach (var r in visor.cracks.Concat(new[] { visor.flash }))
                StringAssert.Contains("Unlit", r.sharedMaterial.shader.name, r.name + " is not lit by any light.");
        }

        [Test]
        public void RemoteCrew_HasAHelmetLamp_OnTheHelmet_DarkUntilSwitchedOn_WithItsLightLeftToTheBudget()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(RemotePath);
            Assert.IsNotNull(prefab);
            var remote = prefab.GetComponent<RemoteAvatar>();
            var rig = remote.tools;
            Assert.IsNotNull(rig.headLamp, "The tool rig knows the helmet lamp.");
            var lamp = rig.headLamp;
            Assert.IsTrue(lamp.transform.IsChildOf(remote.suit.helmet), "It turns with the helmet.");
            Vector3 lens = new Vector3(0.146f, 0.075f, 0.087f);
            Assert.Less(Vector3.Distance(lens, lamp.transform.localPosition), 0.01f, "At the lamp housing's lens.");
            Assert.IsNotNull(lamp.spot);
            Assert.IsFalse(lamp.spot.enabled, "The real light waits for the shared budget.");
            Assert.AreEqual(LightShadows.None, lamp.spot.shadows);
            Assert.IsFalse(lamp.lensGlow.activeSelf);
            Assert.IsFalse(lamp.cone.activeSelf);
            foreach (var c in lamp.GetComponentsInChildren<Component>(true))
                Assert.IsFalse(c is Collider || c is Rigidbody, "Look, don't touch: " + c.GetType().Name);
        }
    }
}

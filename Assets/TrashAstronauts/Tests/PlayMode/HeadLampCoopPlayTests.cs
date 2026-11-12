using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Net;
using TrashAstronauts.Tools;
using UnityEngine;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Crew mates' helmet lamps: the HeadLampOn bit on a stand-in shows the lens glow and the cone on its helmet, and
    /// helmet lamps and flashlights share the one budget of real lights (nearest first). Also that the local lamp goes
    /// out to the crew through the real session.
    /// </summary>
    public class HeadLampCoopPlayTests : NetPlayFixture
    {
        readonly List<GameObject> m_Spawned = new List<GameObject>();
        GameObject m_Viewer;

        [TearDown]
        public void CleanUpSpawned()
        {
            foreach (var go in m_Spawned) if (go != null) Object.Destroy(go);
            m_Spawned.Clear();
            if (m_Viewer != null) Object.Destroy(m_Viewer);
            RemoteToolRig.ViewerOverride = null;
            RemoteToolRig.MaxRealLights = 2;
            HeadLamp.OwnedOverride = null;
        }

        static AvatarSnapshot Pose(Vector3 head, float yaw, byte flags)
        {
            var s = AvatarSnapshot.Default;
            Quaternion face = Quaternion.Euler(0f, yaw, 0f);
            s.headPosition = head.ToNet();
            s.headRotation = face.ToNet();
            s.leftHandPosition = (head + face * new Vector3(-0.22f, -0.4f, 0.3f)).ToNet();
            s.rightHandPosition = (head + face * new Vector3(0.22f, -0.4f, 0.3f)).ToNet();
            s.leftHandRotation = face.ToNet();
            s.rightHandRotation = face.ToNet();
            s.flags = flags;
            return s;
        }

        RemoteAvatar SpawnStandIn(int slot)
        {
            var crew = Object.Instantiate(m_Net.remoteAvatarPrefab);
            crew.Setup(new PlayerId(slot), 0);
            m_Spawned.Add(crew.gameObject);
            return crew;
        }

        static int RealLights(IEnumerable<RemoteAvatar> crews) =>
            crews.Sum(c => (c.tools.HasRealLight ? 1 : 0) + (c.tools.headLamp.HasRealLight ? 1 : 0));

        [UnityTest]
        public IEnumerator HeadLampFlag_LightsTheLensAndCone_OnTheHelmet_AndGoesOutWithTheFlag()
        {
            yield return null;
            var crew = SpawnStandIn(1);
            var lamp = crew.tools.headLamp;
            Assert.IsNotNull(lamp);
            crew.Apply(Pose(new Vector3(0f, 1.6f, -25f), 0f, 0));
            yield return null;
            Assert.IsFalse(lamp.On);
            Assert.IsFalse(lamp.lensGlow.activeSelf || lamp.cone.activeSelf || lamp.HasRealLight, "Off: nothing shows.");

            crew.Apply(Pose(new Vector3(0f, 1.6f, -25f), 0f, AvatarFlags.HeadLampOn));
            yield return null;
            Assert.IsTrue(lamp.On);
            Assert.IsTrue(lamp.lensGlow.activeInHierarchy, "The lens glows.");
            Assert.IsTrue(lamp.cone.activeInHierarchy, "A soft cone shows where they look.");
            Assert.IsFalse(crew.tools.FlashlightOn, "The helmet lamp is not the flashlight.");

            // It sits on the helmet's lamp housing and turns with the head.
            var helmet = crew.suit.helmet;
            Vector3 lens = helmet.TransformPoint(new Vector3(0.146f, 0.075f, 0.087f));
            Assert.Less(Vector3.Distance(lens, lamp.transform.position), 0.02f);
            crew.Apply(Pose(new Vector3(0f, 1.6f, -25f), 90f, AvatarFlags.HeadLampOn));
            crew.suit.Pose(0f);
            yield return null;
            Assert.Less(Vector3.Angle(Vector3.right, Vector3.ProjectOnPlane(lamp.transform.forward, Vector3.up)), 10f, "It looks where their head looks.");

            crew.Apply(Pose(new Vector3(0f, 1.6f, -25f), 90f, 0));
            yield return null;
            Assert.IsFalse(lamp.lensGlow.activeSelf || lamp.cone.activeSelf || lamp.HasRealLight, "Switched off: glow, cone and light go.");
        }

        [UnityTest]
        public IEnumerator HelmetLampsAndFlashlights_ShareTheSameBudgetOfRealLights_NearestFirst()
        {
            yield return null;
            m_Viewer = new GameObject("Test Viewer");
            m_Viewer.transform.position = new Vector3(0f, 1.6f, -30f);
            RemoteToolRig.ViewerOverride = m_Viewer.transform;
            var crews = new[] { SpawnStandIn(1), SpawnStandIn(2), SpawnStandIn(3) };
            void Place(int i, float d, byte flags) => crews[i].Apply(Pose(new Vector3(i * 0.01f, 1.6f, -30f + d), 180f, flags));

            // Three helmet lamps: the two nearest get the real lights.
            Place(0, 2f, AvatarFlags.HeadLampOn);
            Place(1, 4f, AvatarFlags.HeadLampOn);
            Place(2, 6f, AvatarFlags.HeadLampOn);
            yield return null;
            yield return null;
            Assert.IsTrue(crews.All(c => c.tools.headLamp.lensGlow.activeSelf), "Every lit lamp shows its glow.");
            Assert.IsTrue(crews[0].tools.headLamp.HasRealLight && crews[1].tools.headLamp.HasRealLight);
            Assert.IsFalse(crews[2].tools.headLamp.HasRealLight, "The third keeps only its glow and cone.");
            Assert.AreEqual(2, RealLights(crews));

            // A flashlight that is nearer takes a light from the farther lamp: they compete for the same budget.
            Place(2, 1f, AvatarFlags.FlashlightOn);
            yield return null;
            yield return null;
            Assert.IsTrue(crews[2].tools.HasRealLight, "The nearest light, a flashlight, has one.");
            Assert.IsTrue(crews[0].tools.headLamp.HasRealLight);
            Assert.IsFalse(crews[1].tools.headLamp.HasRealLight, "The farthest lamp gave its light up.");
            Assert.AreEqual(2, RealLights(crews), "Never more than the budget, lamps and flashlights together.");

            // One crew mate with both on, nearest of all: both of theirs win.
            Place(1, 0.6f, (byte)(AvatarFlags.HeadLampOn | AvatarFlags.FlashlightOn));
            yield return null;
            yield return null;
            Assert.IsTrue(crews[1].tools.HasRealLight && crews[1].tools.headLamp.HasRealLight);
            Assert.AreEqual(2, RealLights(crews));

            RemoteToolRig.MaxRealLights = 1;
            yield return null;
            Assert.AreEqual(1, RealLights(crews), "The budget is respected when it changes.");
            RemoteToolRig.MaxRealLights = 0;
            yield return null;
            Assert.AreEqual(0, RealLights(crews));
            Assert.IsTrue(crews[1].tools.headLamp.cone.activeSelf, "Without a real light the cone still shows.");
        }

        [UnityTest]
        public IEnumerator TheLocalLamp_GoesOutToTheCrew_ThroughTheSession()
        {
            Assert.IsTrue(m_Net.Host());
            var bot = AddBot(m_Net.Transport.Local);
            yield return Until(() => m_Net.Crew(1) != null && bot.World.AvatarCount[0] > 0);
            HeadLamp.OwnedOverride = true;
            var lamp = HeadLamp.For(m_Player);
            lamp.SetOn(true, false);
            yield return Until(() => bot.World.Avatars[0].HasFlag(AvatarFlags.HeadLampOn));
            Assert.IsTrue(bot.World.Avatars[0].HasFlag(AvatarFlags.HeadLampOn), "The crew hear the lamp is on.");
            lamp.SetOn(false, false);
            yield return Until(() => !bot.World.Avatars[0].HasFlag(AvatarFlags.HeadLampOn));
            Assert.IsFalse(bot.World.Avatars[0].HasFlag(AvatarFlags.HeadLampOn));
        }
    }
}

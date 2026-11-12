using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.Tools;
using TrashAstronauts.UI;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// The helmet lamp in the real scene: bought at the store counter, switched by the wrist button and by tapping the
    /// helmet, its battery, the jumpscare and the blackout, what the rig tells crew mates, and that a player who never
    /// bought it sees no change at all.
    /// </summary>
    public class HeadLampPlayTests
    {
        MemorySaveStorage m_Storage;
        MissionDirector m_Director;
        PlayerContext m_Player;
        HeadLamp m_Lamp;

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
            HeadLamp.OwnedOverride = null;
            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            yield return null;
            yield return null;
            m_Director = MissionDirector.Active;
            m_Player = m_Director.Players.First();
            m_Lamp = HeadLamp.For(m_Player);
        }

        [TearDown]
        public void TearDown()
        {
            MissionDirector.StorageOverride = null;
            MissionDirector.SeedOverride = null;
            MissionDirector.CreatureOverride = null;
            MissionDirector.MapOverride = null;
            MissionDirector.ConditionOverride = null;
            BlackoutController.DamageOverride = null;
            HeadLamp.OwnedOverride = null;
        }

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        static IEnumerator Seconds(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        void Own()
        {
            m_Director.Profile.Data.headLamp = true;
            m_Director.Profile.NotifyChangedAndSave();
        }

        IEnumerator GoOutIntoTheWreck()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            m_Director.keepPlayersInHub = false;
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 14f));
            yield return Frames(4);
            Assert.IsFalse(m_Player.InSafeZone);
        }

        Vector3 Temple(bool right = true)
        {
            Vector3 spot = m_Lamp.tapSpot;
            if (!right) spot.x = -spot.x;
            return m_Player.head.TransformPoint(spot);
        }

        // ------------------------------------------------------------------ the store

        [UnityTest]
        public IEnumerator Store_SellsTheLampOnce_FromItsOwnDisplayAndButton_AndItIsKept()
        {
            var shop = Object.FindFirstObjectByType<ShopPanel>();
            var lampShop = shop.GetComponent<HeadLampShopButton>();
            Assert.IsNotNull(lampShop, "The store sells the helmet lamp.");
            Assert.IsNotNull(lampShop.button);
            Assert.IsTrue(lampShop.button.transform.IsChildOf(shop.transform));
            Assert.GreaterOrEqual(shop.GetComponentsInChildren<SlowSpin>().Length, 4, "Four things on display now.");
            var display = shop.GetComponentsInChildren<SlowSpin>().FirstOrDefault(s => s.name == "Helmet Lamp");
            Assert.IsNotNull(display, "The lamp has its place on the counter.");
            Assert.IsTrue(display.transform.IsChildOf(shop.GetComponent<ShopFolder>().goods), "It folds away with the other goods.");
            int cost = m_Director.Config.headLampCost;

            yield return null;
            lampShop.Refresh();
            Assert.IsFalse(lampShop.button.Interactable, "Nothing is affordable with no credits.");
            StringAssert.Contains("Need " + cost, lampShop.button.Text);

            m_Director.Profile.Data.totalCredits = cost + 100;
            lampShop.Refresh();
            Assert.IsTrue(lampShop.button.Interactable);
            StringAssert.Contains(cost + " cr", lampShop.button.Text);
            Assert.IsFalse(m_Lamp.wristButton.gameObject.activeSelf, "No LAMP button before it is bought.");

            Assert.IsTrue(lampShop.button.Press(null));
            Assert.AreEqual(100, m_Director.Profile.Data.totalCredits, "Paid once.");
            Assert.IsTrue(m_Director.Profile.Data.headLamp);
            StringAssert.Contains("\"headLamp\": true", m_Storage.Contents, "Saved at once.");
            Assert.IsFalse(lampShop.button.Interactable, "Bought once, never sold twice.");
            StringAssert.Contains("OWNED", lampShop.button.Text);
            yield return new WaitForSeconds(lampShop.button.cooldown + 0.05f);
            Assert.IsFalse(lampShop.button.Press(null));
            Assert.IsFalse(m_Director.PurchaseShopItem(ShopItem.HeadLamp));
            Assert.AreEqual(100, m_Director.Profile.Data.totalCredits);

            yield return null;
            Assert.IsTrue(m_Lamp.Owned);
            Assert.IsTrue(m_Lamp.wristButton.gameObject.activeSelf, "The LAMP button appears on the wrist.");

            // Closed during a contract, like the rest of the store.
            m_Director.Profile.Data.headLamp = false;
            m_Director.Profile.Data.totalCredits = cost * 2;
            m_Director.StartMission(PlayerId.Local);
            yield return null;
            lampShop.Refresh();
            StringAssert.Contains("CLOSED", lampShop.button.Text);
            Assert.IsFalse(m_Director.PurchaseShopItem(ShopItem.HeadLamp), "The shop is closed during a contract.");
        }

        // ------------------------------------------------------------------ without it

        [UnityTest]
        public IEnumerator WithoutOwningIt_TheButtonAndTheTapDoNothing_AndNothingAboutThePlayerChanges()
        {
            Assert.IsNotNull(m_Lamp, "Every rig carries the lamp, unbought.");
            Assert.IsFalse(m_Lamp.Owned);
            int[] layersBefore = m_Player.GetComponentsInChildren<Renderer>(true).Select(r => r.gameObject.layer).ToArray();

            Assert.IsFalse(m_Lamp.wristButton.gameObject.activeSelf, "No wrist button.");
            Assert.IsFalse(m_Lamp.wristButton.Press(m_Player.rightHand), "A hidden button cannot be pressed.");
            Assert.IsFalse(m_Lamp.Toggle(m_Player.rightHand));
            Assert.IsFalse(m_Lamp.SetOn(true, true));

            var tip = m_Player.rightHand.fingertip;
            Vector3 rest = tip.position;
            tip.position = Temple() + m_Player.head.right * 0.3f;
            yield return null;
            tip.position = Temple();
            yield return Frames(3);
            tip.position = rest;
            yield return null;

            Assert.IsFalse(m_Lamp.IsOn);
            Assert.AreEqual(0, m_Lamp.ToggleCount);
            Assert.IsFalse(m_Lamp.beam.enabled);
            var snapshot = AvatarSnapshot.Default;
            AvatarSampler.Sample(m_Player, ref snapshot);
            Assert.IsFalse(snapshot.HasFlag(AvatarFlags.HeadLampOn));
            int[] layersAfter = m_Player.GetComponentsInChildren<Renderer>(true).Select(r => r.gameObject.layer).ToArray();
            CollectionAssert.AreEqual(layersBefore, layersAfter, "The suit is left exactly as it was.");
            Assert.IsFalse(m_Player.GetComponentsInChildren<Renderer>(true).Any(r => r.gameObject.layer == GameLayers.LocalSuit));

            m_Director.StartMission(PlayerId.Local);
            yield return Frames(3);
            var wrist = m_Player.GetComponentInChildren<WristDisplay>(true);
            wrist.Refresh();
            StringAssert.DoesNotContain("LAMP", wrist.salvageText.text, "Nothing about a lamp on the wrist.");
        }

        // ------------------------------------------------------------------ switching

        [UnityTest]
        public IEnumerator WristButton_SwitchesTheLamp_WithAClickAndATick_SeparatelyFromTheFlashlight()
        {
            Own();
            yield return null;
            var torch = m_Player.tools.OfType<FlashlightTool>().First();
            torch.SetOn(false, false);
            var finger = m_Player.rightHand;
            int pulses = finger.PulseCount;

            Assert.IsTrue(m_Lamp.wristButton.Press(finger));
            yield return null;
            Assert.IsTrue(m_Lamp.IsOn);
            Assert.IsTrue(m_Lamp.beam.enabled, "It lights.");
            Assert.Greater(finger.PulseCount, pulses, "A haptic tick on the hand that pressed it.");
            Assert.IsFalse(torch.IsOn, "The flashlight has its own switch.");
            StringAssert.Contains("ON", m_Lamp.wristButton.Text);

            torch.SetOn(true, false);
            yield return null;
            Assert.IsTrue(m_Lamp.beam.enabled && torch.beam.enabled, "Both can be on.");

            // It never lights the wearer's own suit or gloves.
            Assert.AreEqual(0, m_Lamp.beam.cullingMask & (1 << GameLayers.LocalSuit));
            foreach (var r in m_Player.avatar.GetComponentsInChildren<Renderer>(true))
                Assert.AreEqual(GameLayers.LocalSuit, r.gameObject.layer, r.name);
            foreach (var glove in new[] { m_Player.leftHand, m_Player.rightHand }.SelectMany(h => h.GetComponentsInChildren<GloveVisual>(true)))
                foreach (var r in glove.GetComponentsInChildren<Renderer>(true))
                    if (r.GetComponent<Collider>() == null) Assert.AreEqual(GameLayers.LocalSuit, r.gameObject.layer, r.name);
            Assert.AreNotEqual(0, m_Lamp.beam.cullingMask & (1 << GameLayers.Player), "Crew mates' suits are still lit.");

            yield return new WaitForSeconds(m_Lamp.wristButton.cooldown + 0.05f);
            Assert.IsTrue(m_Lamp.wristButton.Press(finger));
            yield return null;
            Assert.IsFalse(m_Lamp.IsOn);
            Assert.IsFalse(m_Lamp.beam.enabled);
            Assert.IsTrue(torch.IsOn && torch.beam.enabled, "Switching the lamp off leaves the flashlight alone.");
        }

        [UnityTest]
        public IEnumerator TappingTheHelmet_TogglesOncePerTap_WithACooldown_AndNotWhileHoldingOrResting()
        {
            Own();
            yield return null;
            var hand = m_Player.rightHand;
            var tip = hand.fingertip;
            Vector3 Away() => Temple() + m_Player.head.right * 0.3f;
            int pulses = hand.PulseCount;

            // A tap: in from outside, quickly.
            tip.position = Away();
            yield return Frames(2);
            tip.position = Temple();
            yield return null;
            Assert.IsTrue(m_Lamp.IsOn, "One tap switches it on.");
            Assert.AreEqual(1, m_Lamp.ToggleCount);
            Assert.Greater(hand.PulseCount, pulses, "A haptic tick on the tapping hand.");

            // Resting there does nothing more.
            yield return Seconds(m_Lamp.tapCooldown + 0.3f);
            Assert.AreEqual(1, m_Lamp.ToggleCount, "Once per tap, however long the finger stays.");

            // Out and straight back in, inside the cooldown: no flicker.
            tip.position = Away();
            yield return null;
            tip.position = Temple();
            yield return null;
            Assert.AreEqual(2, m_Lamp.ToggleCount, "Out and in again is the next tap (the cooldown had passed).");
            Assert.IsFalse(m_Lamp.IsOn);
            tip.position = Away();
            yield return null;
            tip.position = Temple();
            yield return null;
            Assert.AreEqual(2, m_Lamp.ToggleCount, "A second tap within the cooldown is ignored.");
            tip.position = Away();
            yield return Seconds(m_Lamp.tapCooldown + 0.1f);

            // The left side of the helmet works too.
            tip.position = Temple(false);
            yield return null;
            Assert.AreEqual(3, m_Lamp.ToggleCount);
            Assert.IsTrue(m_Lamp.IsOn);
            tip.position = Away();
            yield return Seconds(m_Lamp.tapCooldown + 0.1f);

            // A hand drifting slowly up to the helmet is not a tap.
            Vector3 from = Temple() + m_Player.head.right * 0.12f;
            Vector3 to = Temple();
            tip.position = from;
            yield return null;
            float travelled = 0f;
            while (travelled < 1f)
            {
                travelled = Mathf.Min(1f, travelled + Time.deltaTime * 0.03f / 0.12f);
                tip.position = Vector3.Lerp(from, to, travelled);
                yield return null;
            }
            yield return null;
            Assert.AreEqual(3, m_Lamp.ToggleCount, "Resting a hand by the helmet does not switch it.");
            tip.position = Away();
            yield return Seconds(m_Lamp.tapCooldown + 0.1f);

            // The detector itself: holding something, the tap does nothing.
            Assert.IsFalse(m_Lamp.StepTap(1, Away(), true, 0.014f));
            Assert.IsFalse(m_Lamp.StepTap(1, Temple(), true, 0.014f), "Not with something in that hand.");
        }

        // ------------------------------------------------------------------ the battery

        [UnityTest]
        public IEnumerator Battery_DrainsOnlyOutsideTheTug_DimsWhenLow_GoesFlat_RecoversOff_AndTheTugRechargesIt()
        {
            Own();
            yield return null;
            // In the tug before a contract it does not drain.
            m_Lamp.SetOn(true, false);
            m_Lamp.StepBattery(60f);
            Assert.AreEqual(1f, m_Lamp.Charge, 0.001f, "No drain in the tug.");

            yield return GoOutIntoTheWreck();
            Assert.IsTrue(m_Lamp.IsOn);
            m_Lamp.StepBattery(m_Lamp.batterySeconds * 0.25f);
            Assert.AreEqual(0.75f, m_Lamp.Charge, 0.02f, "On, out in the wreck, it drains.");
            m_Lamp.batterySeconds = 10f;
            float before = m_Lamp.Charge;
            yield return Seconds(1f);
            Assert.Less(m_Lamp.Charge, before - 0.05f, "...in real time too.");

            // Low: dimmer, and it stutters.
            m_Lamp.batterySeconds = 100000f;
            m_Lamp.SetCharge(1f);
            yield return Frames(2);
            float full = m_Lamp.beam.intensity;
            m_Lamp.SetCharge(0.08f);
            float brightest = 0f, darkest = float.MaxValue;
            for (float end = Time.realtimeSinceStartup + 2.5f; Time.realtimeSinceStartup < end;)
            {
                yield return null;
                brightest = Mathf.Max(brightest, m_Lamp.beam.intensity);
                darkest = Mathf.Min(darkest, m_Lamp.beam.intensity);
            }
            Assert.Less(brightest, full * 0.8f, "Low on charge it dims.");
            Assert.Less(darkest, brightest * 0.5f, "...and stutters.");

            // Flat: it goes out, refuses to light, and says so on the wrist.
            m_Lamp.batterySeconds = 10f;
            m_Lamp.SetCharge(0.001f);
            yield return Seconds(0.3f);
            Assert.IsTrue(m_Lamp.IsFlat);
            Assert.IsFalse(m_Lamp.IsOn, "A flat lamp goes out.");
            Assert.IsFalse(m_Lamp.beam.enabled);
            Assert.IsFalse(m_Lamp.SetOn(true, true));
            Assert.IsFalse(m_Lamp.Toggle(m_Player.rightHand));
            Assert.IsFalse(m_Lamp.IsOn, "It will not light while flat.");
            var wrist = m_Player.GetComponentInChildren<WristDisplay>(true);
            wrist.Refresh();
            StringAssert.Contains("LAMP FLAT", wrist.salvageText.text);

            // Left off, it recovers slowly; then it lights again.
            yield return Seconds(1.2f);
            Assert.Greater(m_Lamp.Charge, 0.05f, "Switched off, it charges.");
            Assert.Less(m_Lamp.Charge, 0.5f, "...slowly.");
            Assert.IsTrue(m_Lamp.SetOn(true, false));
            wrist.Refresh();
            StringAssert.Contains("LAMP ", wrist.salvageText.text, "The wrist shows the low charge.");

            // The tug tops it up.
            m_Player.locomotion.TeleportTo(m_Director.hubSpawn.position, m_Director.hubSpawn.rotation);
            yield return Seconds(2.5f);
            Assert.Greater(m_Lamp.Charge, 0.95f, "Back in the tug the lamp recharges.");
        }

        // ------------------------------------------------------------------ blackout and jumpscare

        [UnityTest]
        public IEnumerator Blackout_LeavesTheLampOn_AsItLeavesTheFlashlight()
        {
            Own();
            yield return GoOutIntoTheWreck();
            var torch = m_Player.tools.OfType<FlashlightTool>().First();
            torch.SetOn(true, false);
            m_Lamp.SetOn(true, false);
            yield return null;
            m_Director.blackout.TriggerNow(1f);
            yield return Frames(3);
            Assert.IsTrue(m_Director.blackout.IsBlackout);
            Assert.IsTrue(torch.beam.enabled, "The flashlight still works in a blackout...");
            Assert.IsTrue(m_Lamp.beam.enabled, "...and so does the helmet lamp.");
            Assert.IsTrue(m_Lamp.IsOn);
        }

        [UnityTest]
        public IEnumerator Jumpscare_PutsTheLampOutWithTheFlashlight_AndBothComeBackAfter()
        {
            Own();
            yield return null;
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 19.6f));
            yield return Frames(4);
            var torch = m_Player.tools.OfType<FlashlightTool>().First();
            torch.SetOn(true, false);
            m_Lamp.SetOn(true, false);
            yield return null;
            Assert.IsTrue(m_Lamp.beam.enabled);

            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            Physics.SyncTransforms();
            Vector3 head = m_Player.HeadPosition;
            creature.transform.position = new Vector3(head.x, creature.hoverHeight, head.z + 1.6f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.back);
            for (int i = 0; i < 200 && !creature.IsScaring && m_Director.Phase == MissionPhase.Active; i++) creature.Step(0.05f);
            Assert.IsTrue(creature.IsScaring);

            // The jumpscare kills the torch as it starts...
            Assert.IsFalse(torch.beam.enabled, "The jumpscare puts the torch out.");
            int framesWatched = 0;
            for (float waited = 0f; waited < 9f && creature.IsScaring; waited += Time.unscaledDeltaTime)
            {
                yield return null;
                if (!creature.IsScaring) break;
                framesWatched++;
                // The catch comes within a second of the chase starting, as here: ScareDirector's chase flicker ends on the
                // jumpscare's blackout, and used to hand the torch back mid-scare. Now the scare's darkness holds.
                Assert.IsFalse(torch.beam.enabled, "The torch stays dark for the whole jumpscare, even after a chase-start flicker.");
                Assert.IsFalse(m_Lamp.beam.enabled, "...and the helmet lamp is dark for the whole jumpscare: the dark is unchanged.");
                Assert.IsTrue(m_Lamp.IsOn, "The switch is not touched.");
            }
            Assert.Greater(framesWatched, 10);
            yield return Frames(2);
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            Assert.AreEqual(torch.IsOn, torch.beam.enabled, "The flashlight comes back as it was...");
            Assert.AreEqual(m_Lamp.IsOn, m_Lamp.beam.enabled, "...and so does the lamp.");
            Assert.IsTrue(m_Lamp.beam.enabled);
        }

        [UnityTest]
        public IEnumerator ACatchRightAfterAChaseStarts_KeepsTheTorchDarkThroughTheJumpscare_ThenGivesItBack()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 19.6f));
            yield return Frames(4);
            var torch = m_Player.tools.OfType<FlashlightTool>().First();
            torch.SetOn(true, false);
            yield return null;
            Assert.IsTrue(torch.beam.enabled);
            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            Physics.SyncTransforms();
            Vector3 head = m_Player.HeadPosition;
            // The chase-start flicker (ScareDirector's own 1.1 s one, started exactly as a chase starts it)...
            var scares = Object.FindAnyObjectByType<ScareDirector>();
            typeof(ScareDirector).GetMethod("Flicker", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(scares, new object[] { m_Player, 1.1f });
            yield return Frames(2);
            // ...and well within it the creature catches the player.
            creature.transform.position = new Vector3(head.x, creature.hoverHeight, head.z + 1.6f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.back);
            for (int i = 0; i < 200 && !creature.IsScaring && m_Director.Phase == MissionPhase.Active; i++) creature.Step(0.05f);
            Assert.IsTrue(creature.IsScaring);
            int frames = 0;
            for (float waited = 0f; waited < 9f && creature.IsScaring; waited += Time.unscaledDeltaTime)
            {
                yield return null;
                if (!creature.IsScaring) break;
                frames++;
                Assert.IsFalse(torch.beam.enabled, "the jumpscare's darkness holds: the torch is not lit through it");
            }
            Assert.Greater(frames, 10);
            yield return Frames(2);
            Assert.IsTrue(torch.beam.enabled, "the scare's own end gives the torch back");
        }

        // ------------------------------------------------------------------ co-op

        [UnityTest]
        public IEnumerator AvatarSampler_SetsTheHeadLampBit_FromTheLocalLamp()
        {
            Own();
            yield return null;
            var snapshot = AvatarSnapshot.Default;
            snapshot.flags = AvatarFlags.Away;
            AvatarSampler.Sample(m_Player, ref snapshot);
            Assert.IsFalse(snapshot.HasFlag(AvatarFlags.HeadLampOn));
            m_Lamp.SetOn(true, false);
            AvatarSampler.Sample(m_Player, ref snapshot);
            Assert.IsTrue(snapshot.HasFlag(AvatarFlags.HeadLampOn));
            Assert.IsTrue(snapshot.HasFlag(AvatarFlags.Away), "The session's bit is kept.");
            m_Lamp.SetOn(false, false);
            AvatarSampler.Sample(m_Player, ref snapshot);
            Assert.IsFalse(snapshot.HasFlag(AvatarFlags.HeadLampOn));
        }
    }
}

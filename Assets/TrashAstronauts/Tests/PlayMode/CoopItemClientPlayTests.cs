using System;
using System.Collections;
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
using static TrashAstronauts.Net.CoopConvert;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Co-op stage 3, client half: the scene is a client whose CoopClientApply and CoopItemClient sit behind a
    /// real NetSessionClient (the requests it sends are recorded on their way out). The host is the net core's
    /// scripted host world behind a real NetSessionHost, so every grab is arbitrated by the real
    /// ItemOwnershipTable; another crew member is a bot. The local hand is a simulated controller through
    /// the real XRI interactor. Where a test says so the network has 150 ms of round-trip latency.
    /// </summary>
    public class CoopItemClientPlayTests
    {
        MissionDirector m_Director;
        PlayerContext m_Player;
        CoopClientRig m_Rig;
        SimHands m_Hands;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            MissionDirector.StorageOverride = new MemorySaveStorage();
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
        }

        [TearDown]
        public void TearDown()
        {
            if (m_Hands != null) m_Hands.Dispose();
            m_Hands = null;
            if (m_Rig != null) m_Rig.Dispose();
            m_Rig = null;
            if (MissionDirector.Active != null)
            {
                MissionDirector.Active.Net = null;
                MissionDirector.Active.CoopRideAlong = false;
            }
            MissionDirector.StorageOverride = null;
            MissionDirector.SeedOverride = null;
            MissionDirector.CreatureOverride = null;
            MissionDirector.MapOverride = null;
            MissionDirector.ConditionOverride = null;
            BlackoutController.DamageOverride = null;
        }

        // ------------------------------------------------------------------ helpers

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        static IEnumerator Seconds(float seconds)
        {
            float until = Time.unscaledTime + seconds;
            while (Time.unscaledTime < until) yield return null;
        }

        static IEnumerator Until(Func<bool> done, float seconds, string what)
        {
            float until = Time.unscaledTime + seconds;
            while (!done() && Time.unscaledTime < until) yield return null;
            Assert.IsTrue(done(), "Timed out waiting for: " + what);
        }

        /// <summary>The contract a host would send, built here before this headset joins.</summary>
        RunStartParams HostRun(int runIndex)
        {
            m_Director.CoopRideAlong = true;
            var run = m_Director.BuildRunParams();
            m_Director.CoopRideAlong = false;
            run.runIndex = (ushort)runIndex;
            run.planHash = run.ComputePlanHash();
            return run;
        }

        CoopBot B => m_Rig.Bots[0];
        byte Me => (byte)m_Director.LocalPlayerId.Value;

        /// <summary>This headset joins a host with one other crew member; the host starts a contract; the player floats in the open.</summary>
        IEnumerator ClientWithCrew(double oneWayLatency)
        {
            var run = HostRun(5);
            m_Rig = new CoopClientRig(m_Director, m_Player, oneWayLatency);
            m_Rig.JoinScene();
            m_Rig.AddBot(m_Director.hubSpawn.position + Vector3.up * 1.6f);
            m_Rig.StartRun(in run);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.IsNotNull(m_Director.ItemClient, "The session's item sync is in place.");
            m_Rig.StartPump();
            m_Hands = new SimHands(m_Player);
            m_Director.keepPlayersInHub = false;
            m_Player.locomotion.TeleportTo(new Vector3(0f, 0.2f, 9.2f), Quaternion.identity);
            yield return Frames(3);
            m_Player.locomotion.zeroG.Stop();
            yield return Frames(2);
        }

        Vector3 Spot => m_Player.HeadPosition + new Vector3(0.25f, -0.35f, 0.35f);

        SalvageItem[] Loose(int count) => m_Director.RunItems
            .Where(i => i != null && i.definition != null && !i.IsMounted && !i.definition.bonusSpawn && i.Body.mass <= 2f)
            .Take(count).ToArray();

        /// <summary>The host puts its item here (a reliable rest) and this headset's copy glides there.</summary>
        IEnumerator HostPuts(SalvageItem item, Vector3 at)
        {
            m_Rig.Host.SendItemRest((byte)item.NetIndex, ToNet(at), NetQuat.Identity);
            yield return Until(() => Vector3.Distance(item.transform.position, at) < 0.01f, 2f, "the host's rest to arrive");
        }

        /// <summary>The local right hand reaches the item where it floats and closes on it.</summary>
        IEnumerator Grab(SalvageItem item, Vector3 at)
        {
            yield return SimHands.Pose(m_Hands.Right, at);
            yield return HostPuts(item, at);
            yield return SimHands.Grip(m_Hands.Right, true);
        }

        IEnumerator BotCarries(byte id, Vector3 from, Vector3 to, float seconds)
        {
            float t0 = Time.unscaledTime;
            while (Time.unscaledTime - t0 < seconds)
            {
                m_Rig.BotPose(B, id, Vector3.Lerp(from, to, (Time.unscaledTime - t0) / seconds));
                yield return null;
            }
            float hold = Time.unscaledTime + 0.4f;
            while (Time.unscaledTime < hold)
            {
                m_Rig.BotPose(B, id, to);
                yield return null;
            }
        }

        // ================================================================== tests

        [UnityTest]
        public IEnumerator Client_With150msLatency_AGrabIsInTheHandTheSameFrame_TheGrantLandsWithoutAJump_AndItsPoseIsStreamed()
        {
            yield return ClientWithCrew(0.075);
            var item = Loose(1)[0];
            byte id = (byte)item.NetIndex;
            var sync = item.GetComponent<ItemSync>();
            Assert.IsNotNull(sync);
            Assert.IsFalse(item.HostOnly, "Every crew member can pick salvage up.");
            Assert.IsTrue(item.grab.enabled);
            Assert.IsTrue(item.Body.isKinematic, "Until then it is the host's.");
            Assert.AreEqual(ItemMode.HostFree, sync.Authority.Mode);

            Vector3 at = Spot;
            yield return SimHands.Pose(m_Hands.Right, at);
            yield return HostPuts(item, at);
            bool kinematicAtSelect = true, simulatedAtSelect = false, pendingAtSelect = false;
            int selectFrame = -1;
            item.grab.selectEntered.AddListener(_ =>
            {
                kinematicAtSelect = item.Body.isKinematic;
                simulatedAtSelect = item.SimulatedHere;
                pendingAtSelect = sync.Authority.Pending && sync.Authority.Mode == ItemMode.Mine;
                selectFrame = Time.frameCount;
            });
            string exitTrace = null;
            item.grab.selectExited.AddListener(_ =>
            {
                if (exitTrace == null)
                    exitTrace = "kinematic " + item.Body.isKinematic + ", simulated here " + item.SimulatedHere + ", grip " + m_Hands.Right.hand.Grip + "\n" + Environment.StackTrace;
            });
            int posesBefore = m_Rig.PosesQueued;
            SimHands.SetGrip(m_Hands.Right, true);

            // Hold still and watch the item in the hand, frame by frame, until well after the grant lands.
            float gripAt = Time.unscaledTime, grantAt = -1f;
            int frames = 0;
            Vector3 lastOffset = Vector3.zero;
            float worstStep = 0f;
            bool haveOffset = false;
            while (Time.unscaledTime - gripAt < 0.8f)
            {
                yield return null;
                if (selectFrame < 0) continue;
                frames++;
                Vector3 offset = item.transform.position - m_Hands.Right.hand.interactor.transform.position;
                if (haveOffset) worstStep = Mathf.Max(worstStep, Vector3.Distance(offset, lastOffset));
                lastOffset = offset;
                haveOffset = true;
                if (grantAt < 0f && !sync.Authority.Pending) grantAt = Time.unscaledTime;
            }
            Assert.GreaterOrEqual(selectFrame, 0, "The hand closed on it.");
            Assert.IsFalse(kinematicAtSelect, "Dynamic in the hand the frame it was taken.");
            Assert.IsTrue(simulatedAtSelect);
            Assert.IsTrue(pendingAtSelect, "The hand did not wait for the host: Mine at once, the request still out.");
            Assert.AreEqual(1, m_Rig.Count("grab " + id), "One GrabRequest.");
            Assert.Greater(grantAt, gripAt + 0.12f, "The grant really took a round trip.");
            Assert.IsNull(exitTrace, "Let go while held still: " + exitTrace);
            Assert.AreEqual(ItemMode.Mine, sync.Authority.Mode);
            Assert.IsTrue(m_Rig.Events.Contains(ItemAuthorityEvent.Granted));
            Assert.Less(worstStep, 0.01f, "Nothing moves in the hand when the grant lands (worst frame step " + worstStep + " m).");
            Assert.GreaterOrEqual(m_Rig.PosesQueued - posesBefore, frames - 1, "Streamed every frame while held, even held still.");
            Assert.AreEqual(Me, m_Rig.Host.Items.Owner(id));
            Assert.IsTrue(m_Rig.Host.Items.IsHeld(id));
            Assert.IsTrue(m_Director.Session.TryGetSalvage(item.SalvageId, out var record));
            Assert.AreEqual(new PlayerId(Me), record.CollectedBy, "The host booked the first pickup to this player.");

            // Carried: the other crew member sees it in this hand.
            Vector3 hand = at;
            for (int i = 0; i < 30; i++)
            {
                hand += new Vector3(0.01f, 0.005f, 0f);
                SimHands.Place(m_Hands.Right, hand);
                yield return null;
            }
            yield return Seconds(0.6f);
            Assert.Less(Vector3.Distance(m_Rig.LastQueuedPose, item.transform.position), 0.02f);
            Assert.Less(Vector3.Distance(B.SeenAt(id), item.transform.position), 0.05f, "The other crew member sees it where this hand has it.");
            Assert.AreEqual(ItemMode.RemoteHeld, B.Item(id).Mode);
            yield return SimHands.Grip(m_Hands.Right, false);
        }

        [UnityTest]
        public IEnumerator Client_ADenial_CancelsTheHoldCleanly_NoReleaseNoThrowNoRecoil_ADoubleBuzz_AndItFollowsTheWinner()
        {
            yield return ClientWithCrew(0.075);
            var item = Loose(1)[0];
            byte id = (byte)item.NetIndex;
            var sync = item.GetComponent<ItemSync>();
            var hand = m_Hands.Right.hand;
            Vector3 at = Spot;
            yield return SimHands.Pose(m_Hands.Right, at);
            yield return HostPuts(item, at);
            m_Player.locomotion.zeroG.Stop();
            Vector3 drift = m_Player.locomotion.zeroG.Velocity;

            // The other crew member's hand closes on it a moment before this one: theirs reaches the host first.
            Assert.IsTrue(m_Rig.BotGrab(B, id));
            SimHands.SetGrip(m_Hands.Right, true);
            yield return Until(() => item.grab.isSelected, 1f, "this hand to take it (optimistically)");
            int pulses = hand.PulseCount;
            yield return Until(() => sync.Denials == 1, 2f, "the host's refusal");
            Assert.IsFalse(item.grab.isSelected, "Taken out of this hand.");
            Assert.IsFalse(hand.IsHolding);
            Assert.AreEqual(ItemMode.RemoteHeld, sync.Authority.Mode);
            Assert.AreEqual(new PlayerId(B.Slot), item.RemoteHolder, "It is in the winner's hand here.");
            Assert.IsTrue(item.Body.isKinematic);
            Assert.IsFalse(item.SimulatedHere);
            yield return Seconds(0.4f);
            Assert.AreEqual(0, m_Rig.Count("released " + id), "Not a release: no ItemReleased. Sent: " + string.Join(", ", m_Rig.Sent) + "; events: " + string.Join(", ", m_Rig.Events));
            Assert.AreEqual(0, m_Rig.Count("thrown " + id), "No throw.");
            Assert.IsFalse(item.WasThrown);
            Assert.Less(Vector3.Distance(m_Player.locomotion.zeroG.Velocity, drift), 0.01f, "No recoil.");
            Assert.GreaterOrEqual(hand.PulseCount - pulses, 2, "A short double buzz.");
            Assert.AreEqual(1, m_Rig.Scripted.GrantedItems.Count(i => i == id), "Exactly one winner.");

            // Still gripping, and gripping again: it cannot be taken out of the winner's hand.
            Assert.IsFalse(item.grab.isSelected);
            yield return SimHands.Grip(m_Hands.Right, false);
            yield return SimHands.Grip(m_Hands.Right, true);
            Assert.IsFalse(item.grab.isSelected, "Nothing in another crew member's hand can be selected.");
            yield return SimHands.Grip(m_Hands.Right, false);

            // It follows the winner's hand.
            Vector3 away = at + new Vector3(0.6f, 0.3f, 0.2f);
            yield return BotCarries(id, at, away, 0.6f);
            Assert.Less(Vector3.Distance(item.transform.position, away), 0.05f, "It moves with the winner's hand.");
        }

        [UnityTest]
        public IEnumerator Client_LettingGo_SendsReleasedThenThrown_FliesItForAtMost1_5s_HandsItBack_AndGlidesOntoTheHostsStream()
        {
            yield return ClientWithCrew(0.075);
            var item = Loose(1)[0];
            byte id = (byte)item.NetIndex;
            var sync = item.GetComponent<ItemSync>();
            Vector3 at = Spot;
            yield return Grab(item, at);
            Assert.IsTrue(item.grab.isSelected);
            yield return Until(() => !sync.Authority.Pending, 1f, "the grant");

            // A throw: swing forward and let go mid-swing.
            Vector3 hand = at;
            float swing = Time.time;
            while (Time.time - swing < 0.3f)
            {
                hand = at + Vector3.forward * 3f * (Time.time - swing);
                SimHands.Place(m_Hands.Right, hand);
                yield return null;
            }
            // Let go while the hand still moves (it carries on for a moment, as an arm does).
            SimHands.SetGrip(m_Hands.Right, false);
            float follow = Time.time;
            while (item.IsHeld || Time.time - follow < 0.05f)
            {
                hand = at + Vector3.forward * 3f * (Time.time - swing);
                SimHands.Place(m_Hands.Right, hand);
                yield return null;
                Assert.Less(Time.time - follow, 1f, "the release");
            }
            float releasedAt = Time.unscaledTime;
            Assert.AreEqual(1, m_Rig.Count("released " + id), "ItemReleased at once, unconditionally.");
            yield return Until(() => m_Rig.Count("thrown " + id) == 1, 0.5f, "ItemThrown");
            Assert.Greater(m_Rig.Sent.IndexOf("thrown " + id), m_Rig.Sent.IndexOf("released " + id), "The throw follows the release, once the game has registered it.");
            Assert.IsTrue(item.WasThrown);
            Assert.IsTrue(item.SimulatedHere, "Flown here.");
            Assert.IsFalse(item.Body.isKinematic);
            int posesInFlight = m_Rig.PosesQueued;
            yield return Until(() => m_Rig.Count("handoff " + id) == 1, 2f, "the handoff");
            float handedAt = Time.unscaledTime;
            Assert.LessOrEqual(handedAt - releasedAt, (float)ItemAuthority.HandbackMaxSeconds + 0.15f, "Flown here for at most 1.5 s.");
            Assert.Greater(m_Rig.PosesQueued, posesInFlight, "Its flight was streamed.");
            yield return Until(() => !item.SimulatedHere, 1f, "the host taking it back");
            Assert.IsTrue(m_Rig.Events.Contains(ItemAuthorityEvent.Returned));
            Assert.IsTrue(item.Body.isKinematic);
            Assert.AreEqual(ItemMode.HostFree, sync.Authority.Mode);
            Assert.AreEqual(1, m_Rig.Scripted.Calls[NetMsg.ItemReleased]);
            Assert.AreEqual(1, m_Rig.Scripted.Calls[NetMsg.ItemThrown]);
            Assert.IsTrue(m_Rig.Scripted.LastWasThrown);
            Assert.AreEqual(1, m_Rig.Scripted.Returns.Count(r => r.itemId == id && r.cause == ItemReturnCause.Handoff));

            // The host's simulation has it a little elsewhere: this copy glides there instead of jumping.
            Vector3 here = item.transform.position;
            Vector3 hostHas = here + new Vector3(0.25f, 0.1f, 0f);
            float worstStep = 0f;
            Vector3 last = here;
            float t0 = Time.unscaledTime;
            while (Time.unscaledTime - t0 < 0.7f)
            {
                m_Rig.Host.QueueItemPose(id, ToNet(hostHas), NetQuat.Identity, false, false);
                yield return null;
                worstStep = Mathf.Max(worstStep, Vector3.Distance(item.transform.position, last));
                last = item.transform.position;
            }
            Assert.Less(Vector3.Distance(item.transform.position, hostHas), 0.01f, "On the host's stream.");
            Assert.Less(worstStep, 0.1f, "Glided over, not snapped (worst frame step " + worstStep + " m).");
        }

        [UnityTest]
        public IEnumerator OwnersBug_ACrewMemberLetsGo_ThisPlayerThenGrabsAndCarriesIt_TheyHolderSeesItMove_AndTheReverse()
        {
            yield return ClientWithCrew(0.05);
            var item = Loose(1)[0];
            byte id = (byte)item.NetIndex;
            var sync = item.GetComponent<ItemSync>();
            Vector3 at = Spot;
            yield return SimHands.Pose(m_Hands.Right, at + Vector3.down * 0.4f);
            yield return HostPuts(item, at + Vector3.right * 0.5f);

            // B holds it and brings it over: this headset sees it in B's hand and cannot take it.
            Assert.IsTrue(m_Rig.BotGrab(B, id));
            yield return Until(() => sync.Authority.Mode == ItemMode.RemoteHeld, 1f, "B's hold to show here");
            yield return BotCarries(id, at + Vector3.right * 0.5f, at, 0.5f);
            Assert.Less(Vector3.Distance(item.transform.position, at), 0.05f, "It moved here in B's hand.");
            yield return SimHands.Pose(m_Hands.Right, at);
            yield return SimHands.Grip(m_Hands.Right, true);
            Assert.IsFalse(item.grab.isSelected, "Not out of B's hand.");
            yield return SimHands.Grip(m_Hands.Right, false);

            // B lets go (and its flight ends): now this player can take it.
            Assert.IsTrue(m_Rig.BotRelease(B, id, at, at));
            Assert.IsTrue(m_Rig.BotHandoff(B, id, at, Vector3.zero));
            yield return Until(() => sync.Authority.Mode == ItemMode.HostFree && !sync.Authority.Pending, 1f, "free again here");
            yield return SimHands.Grip(m_Hands.Right, true);
            Assert.IsTrue(item.grab.isSelected, "The owner's bug: after another player let go, this player can grab it.");
            yield return Until(() => !sync.Authority.Pending, 1f, "the grant");
            Assert.AreEqual(ItemMode.Mine, sync.Authority.Mode);
            Assert.AreEqual(0, sync.Denials);
            Assert.IsTrue(item.grab.isSelected, "Still in the hand once the host agreed.");

            // ...and carry it: B sees it move.
            Vector3 hand = at;
            for (int i = 0; i < 40; i++)
            {
                hand += new Vector3(-0.01f, 0.005f, 0f);
                SimHands.Place(m_Hands.Right, hand);
                yield return null;
            }
            yield return Seconds(0.5f);
            Assert.Greater(Vector3.Distance(B.SeenAt(id), at), 0.3f, "B sees it move.");
            Assert.Less(Vector3.Distance(B.SeenAt(id), item.transform.position), 0.05f, "B sees it in this player's hand.");
            Assert.AreEqual(ItemMode.RemoteHeld, B.Item(id).Mode);

            // The reverse: this player lets go (no throw); the hand-back follows; B grabs it and carries it.
            yield return SimHands.Grip(m_Hands.Right, false);
            Assert.AreEqual(1, m_Rig.Count("released " + id));
            yield return Until(() => sync.Authority.Mode == ItemMode.HostFree, 2f, "the hand-back");
            Assert.AreEqual(1, m_Rig.Count("handoff " + id));
            Vector3 dropped = item.transform.position;
            yield return Until(() => B.Item(id).Mode == ItemMode.HostFree, 1f, "B sees it free");
            Assert.IsTrue(m_Rig.BotGrab(B, id), "B can grab it after this player let go.");
            yield return Until(() => B.Item(id).Mode == ItemMode.Mine && !B.Item(id).Pending, 1f, "B's grant");
            yield return Until(() => sync.Authority.Mode == ItemMode.RemoteHeld, 1f, "B's hold to show here");
            Vector3 carried = dropped + new Vector3(0.5f, -0.3f, 0.3f);
            yield return BotCarries(id, dropped, carried, 0.6f);
            Assert.Less(Vector3.Distance(item.transform.position, carried), 0.05f, "This player sees it move in B's hand.");
            Assert.AreEqual(new PlayerId(B.Slot), item.RemoteHolder);
        }

        [UnityTest]
        public IEnumerator Client_ADepositArrivingWhileHolding_CancelsTheHoldWithoutARelease_AndSecuresIt()
        {
            yield return ClientWithCrew(0);
            var item = Loose(1)[0];
            byte id = (byte)item.NetIndex;
            var sync = item.GetComponent<ItemSync>();
            yield return Grab(item, Spot);
            yield return Until(() => !sync.Authority.Pending, 1f, "the grant");
            int pulses = m_Hands.Right.hand.PulseCount;

            m_Rig.Host.SendItemDeposited(id, Me, item.Value, 0, 2);
            yield return Until(() => item.IsSecured, 1f, "the deposit");
            Assert.IsFalse(m_Hands.Right.hand.IsHolding, "The hold was cancelled.");
            Assert.AreSame(m_Director.locker.slots[2], item.transform.parent, "In the slot the host chose.");
            Assert.IsTrue(sync.Authority.Secured);
            Assert.IsTrue(m_Director.Session.TryGetSalvage(item.SalvageId, out var record));
            Assert.AreEqual(SalvageState.Deposited, record.State);
            int poses = m_Rig.PosesQueued;
            yield return Seconds(0.4f);
            Assert.AreEqual(poses, m_Rig.PosesQueued, "No longer streamed.");
            Assert.AreEqual(0, m_Rig.Count("released " + id));
            Assert.AreEqual(0, m_Rig.Count("thrown " + id));
            Assert.AreEqual(0, m_Rig.Count("handoff " + id));
            Assert.AreEqual(0, sync.Denials, "Not a refusal: no double buzz.");
            Assert.LessOrEqual(m_Hands.Right.hand.PulseCount - pulses, 1, "Only the locker's own deposit pulse.");
            Assert.IsFalse(item.WasThrown, "No throw registered, so no recoil.");
            yield return SimHands.Grip(m_Hands.Right, false);
            yield return SimHands.Grip(m_Hands.Right, true);
            Assert.IsFalse(item.grab.isSelected, "Never grabbable again.");
        }

        [UnityTest]
        public IEnumerator Client_AKnockOnAnItemFlownHere_IsReportedToTheHost_AndOnlyTheHostsWordChangesTheBooks()
        {
            yield return ClientWithCrew(0);
            var item = m_Director.RunItems.First(i => i != null && i.definition != null && !i.IsMounted && !i.definition.bonusSpawn && i.definition.fragility > 0f);
            byte id = (byte)item.NetIndex;
            var sync = item.GetComponent<ItemSync>();
            yield return Seconds(1.1f); // past the knock sensor's settle-in time
            yield return Grab(item, Spot);
            yield return Until(() => !sync.Authority.Pending, 1f, "the grant");
            Assert.IsTrue(m_Director.Session.TryGetSalvage(item.SalvageId, out var record));
            int booked = record.Value;
            yield return SimHands.Grip(m_Hands.Right, false);
            Assert.AreEqual(ItemMode.Mine, sync.Authority.Mode, "Still flown here after letting go.");

            // In its flight here it hits a wall hard.
            Vector3 from = new Vector3(0f, 0.9f, 9.2f);
            Vector3 dir = Vector3.forward;
            float clear = 100f;
            foreach (var d in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right, Vector3.up, Vector3.down })
            {
                if (Physics.SphereCast(from, 0.3f, d, out RaycastHit hit, 30f, ~0, QueryTriggerInteraction.Ignore) && hit.distance > 1.1f && hit.distance < clear)
                {
                    clear = hit.distance;
                    dir = d;
                }
            }
            Assume.That(clear, Is.InRange(1.1f, 29f));
            Vector3 near = from + dir * (clear - 0.8f);
            item.Body.position = near;
            item.transform.position = near;
            item.Body.angularVelocity = Vector3.zero;
            item.Body.linearVelocity = dir * 6f;
            Physics.SyncTransforms();
            yield return Until(() => m_Rig.Count("damage " + id) > 0, 1f, "the damage report");
            Assert.Less(item.Value, booked, "The knock shows on the item here at once...");
            Assert.AreEqual(booked, record.Value, "...but the books here are only the host's.");
            CollectionAssert.DoesNotContain(((CoopFakeNet)m_Director.Net).calls, "damage " + id + " " + item.Value, "Reported once, through the item sync.");
            yield return Until(() => m_Rig.Scripted.Calls[NetMsg.ItemDamageReport] > 0, 1f, "the host to hear it");
            Assert.AreEqual(id, m_Rig.Scripted.LastItem);
            Assert.AreEqual(item.Value, m_Rig.Scripted.LastValue);

            // The host's word.
            int decided = item.Value;
            m_Rig.Host.SendItemValue(id, decided);
            yield return Until(() => record.Value == decided, 1f, "ItemValue");
            Assert.AreEqual(decided, item.Value);
        }

        [UnityTest]
        public IEnumerator Client_CatchesAnItemAnotherCrewMemberThrew()
        {
            yield return ClientWithCrew(0.05);
            var item = Loose(1)[0];
            byte id = (byte)item.NetIndex;
            var sync = item.GetComponent<ItemSync>();
            Vector3 at = Spot;
            yield return SimHands.Pose(m_Hands.Right, at);
            Vector3 start = at + new Vector3(0f, 0.3f, 1.4f);
            yield return HostPuts(item, start);

            Assert.IsTrue(m_Rig.BotGrab(B, id));
            yield return Until(() => sync.Authority.Mode == ItemMode.RemoteHeld, 1f, "B's hold");
            Assert.IsTrue(m_Rig.BotRelease(B, id, start, start));
            m_Rig.BotThrown(B, id, (at - start).normalized * 2f);
            yield return Until(() => sync.Authority.Mode == ItemMode.HostFree, 1f, "B's throw to show as a free flight");
            yield return BotCarries(id, start, at, 0.6f);
            Assert.Less(Vector3.Distance(item.transform.position, at), 0.05f, "It flew into this hand.");

            yield return SimHands.Grip(m_Hands.Right, true);
            Assert.IsTrue(item.grab.isSelected, "Caught.");
            yield return Until(() => !sync.Authority.Pending, 1f, "the grant");
            Assert.AreEqual(ItemMode.Mine, sync.Authority.Mode, "The host agreed: this player has it.");
            Assert.AreEqual(0, sync.Denials);
            Assert.IsTrue(item.SimulatedHere);
            Assert.IsFalse(item.Body.isKinematic);
            yield return Until(() => B.World.ItemLastEvent[id] == ItemAuthorityEvent.Revoked, 1f, "B told it was caught");
            Assert.AreEqual(ItemMode.RemoteHeld, B.Item(id).Mode);
            yield return SimHands.Grip(m_Hands.Right, false);
        }
    }
}

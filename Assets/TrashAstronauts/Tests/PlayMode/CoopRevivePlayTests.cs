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
using UnityEngine.InputSystem.XR;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Co-op revive in the real scene with no networking (as CoopCreaturePlayTests): as a host the scene runs the
    /// creature and the revive rules and its broadcasts are recorded; as a client the host's messages go straight into
    /// <see cref="CoopClientApply"/>. Crew members are head-only stand-ins, or the real RemoteCrew prefab where the
    /// suit itself is under test.
    /// </summary>
    public class CoopRevivePlayTests
    {
        MissionDirector m_Director;
        PlayerContext m_Player;
        CoopHostRules m_Rules;

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
            foreach (var scares in Object.FindObjectsByType<ScareDirector>(FindObjectsSortMode.None)) scares.enabled = false;
        }

        [TearDown]
        public void TearDown()
        {
            if (m_Rules != null) m_Rules.Dispose();
            m_Rules = null;
            if (MissionDirector.Active != null)
            {
                MissionDirector.Active.Net = null;
                MissionDirector.Active.CoopRideAlong = false;
                MissionDirector.Active.CoopCreatureEnabled = false;
                MissionDirector.Active.ReviveClient = null;
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

        static IEnumerator WaitFor(System.Func<bool> done, float seconds)
        {
            float until = Time.unscaledTime + seconds;
            while (!done() && Time.unscaledTime < until) yield return null;
        }

        static PlayerContext AddRemotePlayer(int slot, Vector3 feet)
        {
            var go = new GameObject("Remote Crew " + slot);
            go.transform.position = feet;
            var head = new GameObject("Head").transform;
            head.SetParent(go.transform, false);
            head.localPosition = Vector3.up * 1.6f;
            var player = go.AddComponent<PlayerContext>();
            player.head = head;
            player.AssignId(new PlayerId(slot), true);
            return player;
        }

        sealed class FakeNet : IMissionNet
        {
            public bool client;
            public int slot;
            public bool IsClient => client;
            public bool IsHost => !client;
            public int LocalSlot => slot;
            public void RequestStart() { }
            public void RequestExtract() { }
            public void RequestDoor(SlidingDoor door, bool desiredOpen) { }
            public void ReportPointCut(Cuttable panel, int pointIndex) { }
            public void ReportSalvageDamage(SalvageItem item, int newValue) { }
            public void ReportWire(WebTripwire wire, WireChange change) { }
            public void ReportTranqHit(Vector3 origin, Vector3 hitPoint) { }
            public void ForwardNoise(in NoiseEvent noise) { }
            public void ReportPause(bool paused) { }
            public void ReportSettingsChanged(Handedness handedness) { }
        }

        sealed class Recorder : ICoopBroadcast
        {
            public readonly List<string> log = new List<string>();
            public readonly List<(byte slot, float progress, byte rescuer)> progress = new List<(byte, float, byte)>();
            public RunEndedMsg lastEnded;
            public int Count(string prefix) => log.Count(l => l.StartsWith(prefix, System.StringComparison.Ordinal));

            public void RunStart(in RunStartParams parameters) => log.Add("runstart");
            public void WorldState(in WorldStateMsg state) => log.Add("world");
            public void ExtractRefused(byte slot, byte missingSlotsMask) => log.Add("refused " + missingSlotsMask);
            public void RunEnded(in RunEndedMsg result) { lastEnded = result; log.Add("runended " + result.outcome); }
            public void ReturnedToHub() => log.Add("hub");
            public void HostPause(bool on) => log.Add("pause " + on);
            public void ItemCollected(byte itemId, byte by) => log.Add("collected");
            public void ItemValue(byte itemId, int value) => log.Add("value");
            public void ItemDeposited(byte itemId, byte by, int finalValue, int trickBonus, byte lockerSlot) => log.Add("deposited");
            public void ItemRecovered(byte itemId, NetVec3 position) => log.Add("recovered");
            public void ItemRest(byte itemId, NetVec3 position, NetQuat rotation) => log.Add("rest");
            public void DoorState(byte doorId, bool open) => log.Add("door");
            public void PointCut(byte panel, byte index) => log.Add("cut");
            public void PanelOpened(byte panel, byte by, NetVec3 plateImpulse, NetVec3 plateTorque) => log.Add("opened");
            public void CreatureStateChanged(EnemyState state, byte targetSlot) => log.Add("state " + state + " " + targetSlot);
            public void Tranquilized(float seconds) => log.Add("tranquilized");
            public void CatchStarted(byte victimSlot) => log.Add("catch " + victimSlot);
            public void BlackoutStarted(float seconds) => log.Add("blackout on");
            public void BlackoutEnded(bool instant) => log.Add("blackout off");
            public void PlayerDowned(byte slot, NetVec3 position) => log.Add("downed " + slot);
            public void PlayerRevived(byte slot, byte by) => log.Add("revived " + slot + " by " + by);
            public void ReviveProgress(byte slot, float progress, byte rescuerSlot) { this.progress.Add((slot, progress, rescuerSlot)); log.Add("progress " + slot); }
            public bool LocalGrab(byte itemId) => true;
            public void LocalRelease(byte itemId) { }
        }

        Recorder Host()
        {
            m_Director.Net = new FakeNet { client = false, slot = 0 };
            var recorder = new Recorder();
            m_Rules = new CoopHostRules(m_Director, recorder);
            m_Rules.CreatureEnabled = true;
            return recorder;
        }

        IEnumerator StartHostRun()
        {
            Assert.IsTrue(m_Director.StartMission(m_Director.LocalPlayerId));
            yield return Frames(2);
            Assert.IsTrue(m_Director.CreatureAboard);
        }

        /// <summary>Puts the creature in front of the stand-in and steps it until it catches them.</summary>
        EnemyDrone CatchStandIn(PlayerContext crew)
        {
            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            Physics.SyncTransforms();
            Vector3 head = crew.HeadPosition;
            creature.transform.position = new Vector3(head.x, creature.hoverHeight, head.z + 1.6f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.back);
            for (int i = 0; i < 300 && !creature.IsScaring && m_Director.Phase == MissionPhase.Active; i++) creature.Step(0.05f);
            Assert.IsTrue(creature.IsScaring, "It should have caught the stand-in.");
            return creature;
        }

        /// <summary>The local hands follow the test, not the (absent) controllers.</summary>
        void FreeHands()
        {
            foreach (var driver in m_Player.GetComponentsInChildren<TrackedPoseDriver>()) driver.enabled = false;
            foreach (var hand in new[] { m_Player.leftHand, m_Player.rightHand })
            {
                hand.GripOverride = 0f;
                hand.TriggerOverride = 0f;
            }
        }

        /// <summary>The host's right glove on (or 1 m off) a downed crew mate's beacon, gripping or not.</summary>
        void RightHandOnBeacon(PlayerContext downed, bool on, bool grip = true)
        {
            var hand = m_Player.rightHand;
            Vector3 target = CoopRevive.BeaconOf(downed) + (on ? Vector3.zero : Vector3.up * 1f);
            hand.transform.position += target - CoopRevive.Palm(hand);
            hand.GripOverride = grip ? 1f : 0f;
        }

        /// <summary>A stand-in a step in front of the host's head (so the host's own hand can reach their beacon).</summary>
        PlayerContext StandInBesideHost(int slot)
        {
            Vector3 head = m_Player.HeadPosition;
            return AddRemotePlayer(slot, head + m_Player.head.forward * 0.9f + Vector3.down * 1.6f);
        }

        // ================================================================== host half

        [UnityTest]
        public IEnumerator Host_ACrewMemberCaughtWhileTheHostIsUp_TheJumpscarePlays_TheContractGoesOn_DownedWhereCaught_AndTheSpiderLeaves()
        {
            var rec = Host();
            yield return StartHostRun();
            var crew = AddRemotePlayer(1, MapPoints.Freighter(0f, 0f, 19.6f));
            yield return Frames(3);
            Vector3 caughtHead = crew.HeadPosition;
            var creature = CatchStandIn(crew);
            Assert.AreEqual(1, rec.Count("catch 1"));

            // The jumpscare plays exactly as before: on the crew member's suit, to its end.
            yield return WaitFor(() => creature.ScarePhase >= 2, 3f);
            yield return Seconds(0.4f);
            Assert.AreEqual(2, creature.ScarePhase);
            Assert.Less(Vector3.Distance(creature.transform.position, crew.HeadPosition), 0.8f);
            Assert.IsFalse(crew.IsDowned, "Not downed while it is still on them.");
            yield return WaitFor(() => !creature.IsScaring, 8f);
            yield return null;

            Assert.AreEqual(MissionPhase.Active, m_Director.Phase, "The contract does NOT end.");
            Assert.IsTrue(crew.IsDowned, "Downed.");
            Assert.Less(Vector3.Distance(crew.DownedPosition, caughtHead), 0.05f, "Where they were caught.");
            Assert.AreEqual(1, rec.Count("downed 1"));
            Assert.AreEqual(0, rec.Count("runended"));
            Assert.AreEqual(1, m_Rules.Revive.DownsOf(1));
            Assert.AreEqual(EnemyState.Patrol, creature.State, "It gives up the hunt and goes back on its rounds.");
            Assert.AreNotEqual(new PlayerId(1), creature.Quarry, "Not after the body.");
            Assert.AreEqual(0, m_Director.MissingAboardMask(), "A downed player is not waited for (the host is aboard).");

            // It does not come back for the body, and it never catches it again.
            float nearest = float.MaxValue;
            for (int i = 0; i < 400; i++)
            {
                creature.Step(0.05f);
                Assert.IsFalse(creature.IsScaring, "It never catches a downed player.");
                Assert.AreNotEqual(new PlayerId(1), creature.Quarry, "It never hunts a downed player.");
                if (i > 200) nearest = Mathf.Min(nearest, Vector3.Distance(creature.transform.position, crew.DownedPosition));
            }
            Assert.Greater(nearest, ReviveRules.GraceRadius, "It does not camp on the body.");
            Assert.IsTrue(crew.IsDowned);
            Object.Destroy(crew.gameObject);
        }

        [UnityTest]
        public IEnumerator Host_NearTheBodyItCannotCatchAnyoneForTheGrace_ButFarFromItCan_AndADownedPlayersShoutIsAWhisperToIt()
        {
            Host();
            yield return StartHostRun();
            var downed = AddRemotePlayer(1, MapPoints.Freighter(0f, 0f, 19.6f));
            var rescuer = AddRemotePlayer(2, MapPoints.Freighter(1.5f, 0f, 19.6f));
            yield return Frames(3);
            var creature = m_Director.ActiveEnemy;
            Assert.IsTrue(m_Rules.Revive.TryDown(new PlayerId(1), creature));
            Assert.IsTrue(m_Director.CatchBlocked(rescuer), "A rescuer 1.5 m from the body is safe for the grace.");
            var far = AddRemotePlayer(3, MapPoints.Freighter(0f, 0f, 12f));
            yield return null;
            Assert.IsFalse(m_Director.CatchBlocked(far), "Someone far from the body is not.");

            // Its senses are unchanged: it still chases the rescuer it sees, but cannot catch them yet.
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            Physics.SyncTransforms();
            Object.Destroy(far.gameObject);
            yield return null;
            Vector3 head = rescuer.HeadPosition;
            creature.transform.position = new Vector3(head.x, creature.hoverHeight, head.z + 1.2f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.back);
            for (int i = 0; i < 60; i++)
            {
                creature.Step(0.05f);
                Assert.IsFalse(creature.IsScaring, "No catch near the body during the grace.");
            }
            Assert.Greater(m_Rules.Revive.CatchesBlocked, 0);

            Object.Destroy(rescuer.gameObject);
            yield return null;
            downed.SetDowned(false, downed.DownedPosition);

            // A downed player's shout carries only a whisper's way to it.
            var other = AddRemotePlayer(2, MapPoints.Freighter(0f, 0f, 12f));
            yield return null;
            Assert.IsTrue(m_Rules.Revive.TryDown(new PlayerId(1), creature));
            creature.WakeNow();
            creature.transform.position = downed.DownedPosition + Vector3.forward * 6f;
            for (int i = 0; i < 3; i++) creature.Step(0.05f);
            Assert.AreEqual(EnemyState.Patrol, creature.State);
            float heardBefore = creature.LastHeardNoiseTime;
            m_Director.noise.Emit(new NoiseEvent(downed.DownedPosition, 1f, NoiseKind.Voice, new PlayerId(1)));
            Assert.AreEqual(heardBefore, creature.LastHeardNoiseTime, "A downed player's full shout from 6 m away is not heard.");
            Assert.AreEqual(EnemyState.Patrol, creature.State);
            m_Director.noise.Emit(new NoiseEvent(downed.DownedPosition, 1f, NoiseKind.Voice, new PlayerId(2)));
            Assert.AreEqual(EnemyState.Investigate, creature.State, "The same shout from someone who is up is heard.");
            Object.Destroy(other.gameObject);
            Object.Destroy(downed.gameObject);
        }

        [UnityTest]
        public IEnumerator Host_TheHostsHandOnTheBeacon_ForTheFullTime_Revives_LettingGoEarlyDrains_AndTheRevivedIsSafeAWhile()
        {
            var rec = Host();
            yield return StartHostRun();
            FreeHands();
            var crew = StandInBesideHost(1);
            yield return Frames(2);
            Assert.IsTrue(m_Rules.Revive.TryDown(new PlayerId(1), m_Director.ActiveEnemy));
            Assert.IsTrue(crew.IsDowned);

            // About 2 s on the beacon, then let go: the ring rises, then drains away, and nothing happens.
            RightHandOnBeacon(crew, true);
            yield return Seconds(2f);
            float reached = crew.ReviveProgress;
            Assert.That(reached, Is.InRange(0.25f, 0.6f), "Two seconds of five.");
            Assert.AreEqual(1, m_Rules.Revive.HoldOf(0), "The host's own hand counts as holding crew 2.");
            Assert.IsTrue(rec.progress.Count > 0 && rec.progress.All(p => p.slot == 1 && p.rescuer == 0), "Progress goes out naming the rescuer.");
            Assert.LessOrEqual(rec.progress.Count, Mathf.CeilToInt(2f / (float)ReviveRules.ProgressInterval) + 3, "About 5 a second, no more.");
            RightHandOnBeacon(crew, true, grip: false);
            yield return Seconds(ReviveRules.DrainSeconds * reached + 0.4f);
            Assert.AreEqual(0f, crew.ReviveProgress, "Let go: it drains away.");
            Assert.IsTrue(crew.IsDowned, "Releasing early does not revive.");
            Assert.AreEqual(0f, rec.progress.Last().progress, "...and the crew are told it is empty.");
            Assert.AreEqual(0, rec.Count("revived"));

            // Moving the hand away does nothing either.
            RightHandOnBeacon(crew, false);
            yield return Seconds(0.5f);
            Assert.AreEqual(0f, crew.ReviveProgress);

            // The full time on it: up again where they lay.
            RightHandOnBeacon(crew, true);
            float start = Time.unscaledTime;
            yield return WaitFor(() => !crew.IsDowned, 9f);
            Assert.IsFalse(crew.IsDowned, "Revived.");
            Assert.That(Time.unscaledTime - start, Is.InRange(ReviveRules.ReviveSeconds - 0.3f, ReviveRules.ReviveSeconds + 1.2f));
            Assert.AreEqual(1, rec.Count("revived 1 by 0"));
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.IsTrue(m_Director.CatchBlocked(crew), "A few seconds of protection.");
            yield return Seconds(ReviveRules.ProtectionSeconds + 0.2f);
            Assert.IsFalse(m_Director.CatchBlocked(crew), "Protection (and the grace near the body) wear off.");
            Assert.Greater(m_Rules.Revive.RescuerNoises, 0, "The rescuer's work made a little noise.");
            m_Player.rightHand.GripOverride = 0f;
            Object.Destroy(crew.gameObject);
        }

        [UnityTest]
        public IEnumerator Host_ASecondCrewMemberRevivesThroughTheirMessages_TwoRescuersAreAQuarterFaster_AndOneTooFarAwayDoesNotCount()
        {
            var rec = Host();
            yield return StartHostRun();
            FreeHands();
            var downed = StandInBesideHost(1);
            var rescuer = AddRemotePlayer(2, downed.transform.position + Vector3.right * 0.8f);
            var distant = AddRemotePlayer(3, downed.transform.position + Vector3.right * 6f);
            yield return Frames(2);
            Assert.IsTrue(m_Rules.Revive.TryDown(new PlayerId(1), m_Director.ActiveEnemy));

            // Too far from the body: the host does not believe the hold.
            m_Rules.HostReviveHold(3, 1, true);
            yield return Seconds(0.6f);
            Assert.AreEqual(0f, downed.ReviveProgress, "A hold from 6 m away does not count.");

            // The second crew member's hold, as their ReviveHold message would bring it.
            m_Rules.HostReviveHold(2, 1, true);
            float start = Time.unscaledTime;
            yield return WaitFor(() => downed.ReviveProgress > 0.3f, 4f);
            Assert.AreEqual(new PlayerId(2), downed.ReviveRescuer);
            // The host joins in: two hands on the beacon.
            RightHandOnBeacon(downed, true);
            yield return WaitFor(() => !downed.IsDowned, 8f);
            float took = Time.unscaledTime - start;
            Assert.IsFalse(downed.IsDowned);
            Assert.Less(took, ReviveRules.ReviveSeconds - 0.4f, "Two rescuers are faster than one...");
            Assert.Greater(took, ReviveRules.ReviveSeconds / (1f + ReviveRules.ExtraRescuerBonus) - 0.3f, "...but do not stack beyond the bonus.");
            Assert.AreEqual(1, rec.Count("revived 1 by "), "One revive, naming a rescuer.");
            Assert.IsTrue(rec.Count("revived 1 by 0") + rec.Count("revived 1 by 2") == 1);
            Assert.AreEqual(NetProtocol.NoSlot, m_Rules.Revive.HoldOf(2), "Holds end with the revive.");

            // A rescuer who leaves mid-revive just stops it.
            Assert.IsTrue(m_Rules.Revive.TryDown(new PlayerId(1), m_Director.ActiveEnemy));
            m_Player.rightHand.GripOverride = 0f;
            m_Rules.HostReviveHold(2, 1, true);
            yield return WaitFor(() => downed.ReviveProgress > 0.2f, 3f);
            m_Rules.HostPlayerLeft(2);
            Object.Destroy(rescuer.gameObject);
            yield return Seconds(ReviveRules.DrainSeconds + 0.3f);
            Assert.AreEqual(0f, downed.ReviveProgress);
            Assert.IsTrue(downed.IsDowned);
            Assert.AreEqual(2, m_Rules.Revive.DownsOf(1), "Downed twice this contract.");
            Object.Destroy(distant.gameObject);
            Object.Destroy(downed.gameObject);
        }

        [UnityTest]
        public IEnumerator Host_TheHostDowned_IsLockedButLooksAround_KeepsSimulating_AndIsRevivedByTheCrew()
        {
            var rec = Host();
            yield return StartHostRun();
            var crew = StandInBesideHost(1);
            yield return Frames(2);
            var creature = m_Director.ActiveEnemy;
            var zeroG = m_Player.locomotion.zeroG;
            Assert.IsTrue(m_Rules.Revive.TryDown(m_Director.LocalPlayerId, creature));
            Assert.IsTrue(m_Player.IsDowned);
            Assert.AreEqual(1, rec.Count("downed 0"));
            var lockdown = m_Player.GetComponent<DownedLock>();
            Assert.IsTrue(lockdown.On);
            Assert.IsFalse(zeroG.enabled, "No thruster, swimming or wall grabs.");
            Assert.IsFalse(m_Player.rightHand.interactor.enabled, "No grabbing or tools.");
            Assert.IsTrue(m_Player.GetComponentsInChildren<HandButtonInteractor>().All(b => !b.enabled), "No buttons.");
            Assert.IsTrue(m_Player.GetComponentsInChildren<TeleportAimer>().All(a => !a.enabled), "No teleport.");
            Assert.IsTrue(lockdown.VignetteShown, "The dim downed view.");
            Assert.AreEqual(m_Player.visor.cracks.Length, m_Player.visor.Stage, "The cracked visor.");
            Assert.IsTrue(m_Player.head.GetComponent<Camera>().enabled, "The head still looks around.");
            var wrist = m_Player.GetComponentInChildren<WristDisplay>(true);
            wrist.Refresh();
            StringAssert.Contains("DOWNED - CREW MUST REVIVE YOU", wrist.crewText.text);

            // The host's machine keeps simulating: the creature, the clock.
            float elapsed = m_Director.Session.ElapsedSeconds;
            creature.WakeNow();
            for (int i = 0; i < 5; i++) creature.Step(0.05f);
            yield return Frames(10);
            Assert.Greater(m_Director.Session.ElapsedSeconds, elapsed);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);

            m_Rules.HostReviveHold(1, 0, true);
            yield return WaitFor(() => !m_Player.IsDowned, 8f);
            Assert.IsFalse(m_Player.IsDowned, "Revived by the crew member.");
            Assert.IsTrue(zeroG.enabled);
            Assert.IsTrue(m_Player.rightHand.interactor.enabled);
            Assert.IsTrue(m_Player.GetComponentsInChildren<HandButtonInteractor>().All(b => b.enabled));
            Assert.IsFalse(lockdown.VignetteShown);
            Assert.Greater(m_Player.visor.Stage, 0, "The visor is still cracked.");
            Assert.AreEqual(1, rec.Count("revived 0 by 1"));
            Object.Destroy(crew.gameObject);
        }

        [UnityTest]
        public IEnumerator Host_TheLastOneStandingCaught_FailsTheContractForEveryone()
        {
            var rec = Host();
            yield return StartHostRun();
            var crew = AddRemotePlayer(1, MapPoints.Freighter(0f, 0f, 19.6f));
            yield return Frames(3);
            // The host is already down: the crew member is the last one up.
            Assert.IsTrue(m_Rules.Revive.TryDown(m_Director.LocalPlayerId, m_Director.ActiveEnemy));
            var creature = CatchStandIn(crew);
            yield return WaitFor(() => m_Director.Phase != MissionPhase.Active, 9f);
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            Assert.AreEqual(MissionOutcome.FailedCaught, m_Director.Session.Result.Outcome, "Nobody left up: failed for everyone.");
            Assert.AreEqual(1, rec.Count("runended FailedCaught"));
            Assert.AreEqual(1, rec.Count("downed "), "Only the host was ever downed.");
            Assert.IsFalse(m_Player.IsDowned, "Everyone is up again once the contract is over.");
            Assert.AreEqual(1, m_Director.Session.Result.DownsOf(0));
            Assert.IsFalse(creature.IsScaring);
            Object.Destroy(crew.gameObject);
        }

        [UnityTest]
        public IEnumerator Host_ALonePlayerInASession_IsCaughtAndTheContractFailsAsToday()
        {
            var rec = Host();
            yield return StartHostRun();
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 19.6f));
            yield return Frames(4);
            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            Physics.SyncTransforms();
            Vector3 head = m_Player.HeadPosition;
            creature.transform.position = new Vector3(head.x, creature.hoverHeight, head.z + 1.6f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.back);
            for (int i = 0; i < 200 && !creature.IsScaring && m_Director.Phase == MissionPhase.Active; i++) creature.Step(0.05f);
            Assert.IsTrue(creature.IsScaring);
            yield return WaitFor(() => creature.ScarePhase >= 2, 3f);
            yield return Seconds(0.3f);
            Assert.Greater(m_Player.visor.Stage, 0, "The player's own jumpscare.");
            yield return WaitFor(() => m_Director.Phase != MissionPhase.Active, 9f);
            Assert.AreEqual(MissionOutcome.FailedCaught, m_Director.Session.Result.Outcome);
            Assert.AreEqual(0, rec.Count("downed"));
            Assert.IsFalse(m_Player.IsDowned);
        }

        [UnityTest]
        public IEnumerator Host_ExtractingWithACrewMemberDown_Succeeds_TheyAreLeftBehindAtAPrice_AndEachIsPaidTheirShare()
        {
            var rec = Host();
            yield return StartHostRun();
            var crew = AddRemotePlayer(1, MapPoints.Freighter(0f, 0f, 19.6f));
            yield return Frames(3);
            Assert.AreNotEqual(0, m_Director.MissingAboardMask(), "Up and outside: waited for.");
            Assert.IsTrue(m_Rules.Revive.TryDown(new PlayerId(1), m_Director.ActiveEnemy));
            yield return null;

            // Something in the locker, so there is a payout to share.
            var session = m_Director.Session;
            var item = m_Director.RunItems.First(i => i != null && !i.IsMounted);
            Assert.IsTrue(session.TryCollect(item.SalvageId, m_Director.LocalPlayerId));
            Assert.AreEqual(DepositResult.Accepted, session.TryDeposit(item.SalvageId, m_Director.LocalPlayerId));

            var board = Object.FindAnyObjectByType<MissionBoard>();
            board.Refresh();
            StringAssert.Contains(MissionBoard.ReviveHelpLine, board.body.text);
            StringAssert.Contains("DOWN: CREW 2", board.body.text, "The board names who is down before the host confirms.");
            Assert.AreEqual(0, m_Director.MissingAboardMask(), "Every UP player is aboard.");

            int credits = m_Director.Profile.Data.totalCredits;
            var result = m_Director.Extract(m_Director.LocalPlayerId);
            Assert.IsNotNull(result, "Extraction is allowed.");
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            Assert.Greater(result.Payout, 0);
            Assert.AreEqual(1 << 1, result.LeftBehindMask);
            Assert.AreEqual(1, result.DownsOf(1));
            Assert.AreEqual(result.Payout, result.PayoutFor(0), "The host was never down.");
            Assert.AreEqual(ReviveRules.PayoutFor(result.Payout, 1, true), result.PayoutFor(1));
            Assert.Less(result.PayoutFor(1), result.Payout);
            Assert.AreEqual(credits + result.Payout, m_Director.Profile.Data.totalCredits, "The host banks its full share.");
            Assert.AreEqual(1 << 1, rec.lastEnded.leftBehindMask, "RunEnded carries who was left behind.");
            Assert.AreEqual(1, rec.lastEnded.ToResult().DownsOf(1));
            Assert.IsFalse(crew.IsDowned, "Home with the crew.");
            board.Refresh();
            StringAssert.Contains("CREW 2: downed 1x, LEFT BEHIND, recovered by the company  -" + ReviveRules.PenaltyPercent(1, true) + "%", board.body.text);
            Object.Destroy(crew.gameObject);
        }

        /// <summary>A downing keeps the visor cracked for the rest of the contract, and only that long.</summary>
        [UnityTest]
        public IEnumerator Host_TheVisorADowningCracked_StaysCrackedAfterTheRevive_AndIsClearInTheHubAndTheNextContract()
        {
            Host();
            yield return StartHostRun();
            var crew = StandInBesideHost(1);
            yield return Frames(2);
            Assert.AreEqual(0, m_Player.visor.Stage);
            Assert.IsTrue(m_Rules.Revive.TryDown(m_Director.LocalPlayerId, m_Director.ActiveEnemy));
            Assert.AreEqual(m_Player.visor.cracks.Length, m_Player.visor.Stage);
            m_Rules.HostReviveHold(1, 0, true);
            yield return WaitFor(() => !m_Player.IsDowned, 8f);
            Assert.IsFalse(m_Player.IsDowned);
            Assert.Greater(m_Player.visor.Stage, 0, "Revived: still cracked for the rest of the contract.");

            m_Director.AbortRun();
            yield return Frames(3);
            Assert.AreNotEqual(MissionPhase.Active, m_Director.Phase);
            Assert.AreEqual(0, m_Player.visor.Stage, "Clear again once the contract is over.");
            if (m_Director.Phase == MissionPhase.Results) m_Director.ReturnToHub();
            yield return Frames(3);
            Assert.IsTrue(m_Director.StartMission(m_Director.LocalPlayerId));
            yield return Frames(3);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.AreEqual(0, m_Player.visor.Stage, "...and in the next contract.");
            Object.Destroy(crew.gameObject);
        }

        /// <summary>The same on a client, where the host's RunEnded ends the contract.</summary>
        [UnityTest]
        public IEnumerator Client_TheVisorADowningCracked_IsClearAfterTheHostsRunEnded()
        {
            BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            yield return StartClientRun(apply);
            Assert.IsTrue(apply.ApplyPlayerDowned(1, CoopConvert.ToNet(m_Player.HeadPosition)));
            yield return Frames(2);
            Assert.AreEqual(m_Player.visor.cracks.Length, m_Player.visor.Stage);
            Assert.IsTrue(apply.ApplyPlayerRevived(1, 0));
            Assert.Greater(m_Player.visor.Stage, 0);
            var ended = new RunEndedMsg { outcome = MissionOutcome.FailedOxygen, quota = m_Director.Session.Quota, elapsedSeconds = 30f };
            apply.ApplyRunEnded(in ended);
            yield return Frames(3);
            Assert.AreNotEqual(MissionPhase.Active, m_Director.Phase);
            Assert.AreEqual(0, m_Player.visor.Stage, "Clear once the host ended the contract.");
        }

        [UnityTest]
        public IEnumerator Host_TheSharedTank_IgnoresADownedPlayersExertion()
        {
            Host();
            yield return StartHostRun();
            var crew = AddRemotePlayer(1, MapPoints.Freighter(0f, 0f, 19.6f));
            yield return Frames(3);
            Assert.Greater(m_Director.Config.exertionOxygenCost, 0f);

            // Working flat out outside: the tank drains faster than real time.
            crew.RemoteExertion = 1f;
            float o0 = m_Director.Session.Oxygen.Remaining, real = 0f;
            yield return null;
            o0 = m_Director.Session.Oxygen.Remaining;
            while (real < 1f) { yield return null; real += Time.deltaTime; }
            float upRate = (o0 - m_Director.Session.Oxygen.Remaining) / real;
            Assert.AreEqual(1f + m_Director.Config.exertionOxygenCost, upRate, 0.1f);

            // Downed: still outside (the suit breathes) but their exertion is not counted.
            Assert.IsTrue(m_Rules.Revive.TryDown(new PlayerId(1), m_Director.ActiveEnemy));
            crew.RemoteExertion = 1f;
            yield return null;
            o0 = m_Director.Session.Oxygen.Remaining;
            real = 0f;
            while (real < 1f) { yield return null; real += Time.deltaTime; }
            float downRate = (o0 - m_Director.Session.Oxygen.Remaining) / real;
            Assert.AreEqual(1f, downRate, 0.06f);

            // Oxygen running out still fails everyone.
            m_Director.TickMission(m_Director.Session.Oxygen.Remaining + 5f, false);
            Assert.AreEqual(MissionOutcome.FailedOxygen, m_Director.Session.Result.Outcome);
            Assert.IsFalse(crew.IsDowned);
            Object.Destroy(crew.gameObject);
        }

        // ================================================================== client half

        FakeNet BecomeClient(int slot)
        {
            var net = new FakeNet { client = true, slot = slot };
            m_Player.AssignId(new PlayerId(slot), false);
            m_Director.Net = net;
            return net;
        }

        IEnumerator StartClientRun(CoopClientApply apply, int runIndex = 5)
        {
            var net = m_Director.Net;
            m_Director.Net = null;
            m_Director.CoopRideAlong = true;
            m_Director.CoopCreatureEnabled = true;
            var run = m_Director.BuildRunParams();
            m_Director.CoopRideAlong = false;
            m_Director.CoopCreatureEnabled = false;
            run.runIndex = (ushort)runIndex;
            run.planHash = run.ComputePlanHash();
            m_Director.Net = net;
            Assert.IsTrue(apply.ApplyRunStart(in run));
            yield return Frames(2);
            Assert.IsTrue(m_Director.ActiveEnemy.IsReplica);
        }

        RemoteAvatar AddRemoteCrew(int slot, Vector3 head)
        {
            var prefab = m_Director.GetComponent<NetSession>().remoteAvatarPrefab;
            var crew = Object.Instantiate(prefab);
            crew.Setup(new PlayerId(slot), 0);
            var s = AvatarSnapshot.Default;
            s.headPosition = CoopConvert.ToNet(head);
            s.headRotation = CoopConvert.ToNet(Quaternion.identity);
            s.leftHandPosition = CoopConvert.ToNet(head + new Vector3(-0.25f, -0.4f, 0.3f));
            s.rightHandPosition = CoopConvert.ToNet(head + new Vector3(0.25f, -0.4f, 0.3f));
            crew.Push(1.0, in s);
            crew.Apply(in s);
            return crew;
        }

        [UnityTest]
        public IEnumerator Client_DownedAfterItsOwnJumpscare_IsImmobileInADimView_LooksAround_TheRunGoesOn_AndRevivedGetsEverythingBack()
        {
            BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            var sent = new List<(byte slot, bool on)>();
            yield return StartClientRun(apply);
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 19.6f));
            yield return Frames(4);
            var creature = m_Director.ActiveEnemy;
            var zeroG = m_Player.locomotion.zeroG;

            Assert.IsTrue(apply.ApplyCatchStarted(1));
            yield return WaitFor(() => creature.ScarePhase >= 2, 3f);
            // The host's word arrives while the jumpscare is still playing here: it waits for it.
            Assert.IsTrue(apply.ApplyPlayerDowned(1, CoopConvert.ToNet(m_Player.HeadPosition)));
            Assert.IsFalse(m_Player.IsDowned, "Not while the jumpscare plays: it is not cut short.");
            Assert.IsTrue(creature.HoldingAfterScare);
            yield return WaitFor(() => !creature.IsScaring, 8f);
            yield return Frames(2);
            Assert.IsTrue(m_Player.IsDowned, "Downed once the jumpscare is over.");

            var lockdown = m_Player.GetComponent<DownedLock>();
            Assert.IsTrue(lockdown.VignetteShown, "A dim, red-edged view...");
            yield return WaitFor(() => m_Player.fader.Alpha <= 0f, 2f);
            Assert.AreEqual(0f, m_Player.fader.Alpha, "...after the black lifts.");
            Assert.AreEqual(m_Player.visor.cracks.Length, m_Player.visor.Stage, "The cracked visor stays.");
            Assert.IsFalse(zeroG.enabled);
            Assert.IsFalse(m_Player.leftHand.interactor.enabled);
            Assert.IsFalse(m_Player.rightHand.interactor.enabled);
            Assert.IsTrue(m_Player.GetComponentsInChildren<ReachAssist>().All(r => !r.enabled), "No reaching for salvage.");
            Assert.IsTrue(m_Player.GetComponentsInChildren<HandButtonInteractor>().All(b => !b.enabled));
            Assert.IsTrue(m_Player.GetComponentsInChildren<TeleportAimer>().All(a => !a.enabled));
            Assert.IsTrue(m_Player.head.GetComponent<Camera>().enabled);
            Assert.IsTrue(m_Player.head.GetComponents<TrackedPoseDriver>().All(d => d.enabled), "The head is still tracked: looking around works.");

            // Nothing moves the body: a push of the thruster's size does nothing.
            Vector3 rig = m_Player.transform.position;
            zeroG.AddImpulse(Vector3.forward * 2f);
            yield return Frames(20);
            Assert.Less(Vector3.Distance(rig, m_Player.transform.position), 0.001f, "Held where they were caught.");

            // The run goes on: no RunEnded comes, and it is not taken for a lost link.
            yield return Seconds(EnemyDrone.RunEndWaitSeconds + 0.5f);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.AreEqual(0, creature.RunEndLinkLosses);

            var wrist = m_Player.GetComponentInChildren<WristDisplay>(true);
            Assert.IsTrue(apply.ApplyReviveProgress(1, 0.5f, 0));
            wrist.Refresh();
            StringAssert.Contains("DOWNED - CREW MUST REVIVE YOU", wrist.crewText.text);
            StringAssert.Contains("[#####-----]", wrist.crewText.text, "The ring on the wrist.");

            Assert.IsTrue(apply.ApplyPlayerRevived(1, 0));
            Assert.IsFalse(m_Player.IsDowned);
            Assert.IsTrue(zeroG.enabled);
            Assert.IsTrue(m_Player.leftHand.interactor.enabled && m_Player.rightHand.interactor.enabled);
            Assert.IsTrue(m_Player.GetComponentsInChildren<ReachAssist>().All(r => r.enabled));
            Assert.IsTrue(m_Player.GetComponentsInChildren<HandButtonInteractor>().All(b => b.enabled));
            Assert.IsFalse(lockdown.VignetteShown);
            Assert.Greater(m_Player.visor.Stage, 0, "Still cracked.");
            wrist.Refresh();
            Assert.AreEqual("", wrist.crewText.text);
            Assert.IsFalse(apply.ApplyPlayerRevived(1, 0), "A repeat changes nothing.");
        }

        [UnityTest]
        public IEnumerator Client_SomeoneElseDowned_TheirSuitSlumpsWhereTheyWereCaught_WithTheBeacon_AndStandsUpWhenRevived()
        {
            BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            yield return StartClientRun(apply);
            Vector3 head = MapPoints.Freighter(0.5f, 1.6f, 19.6f);
            var crew = AddRemoteCrew(2, head);
            yield return Frames(2);
            Assert.IsFalse(crew.beacon.Shown);

            Assert.IsTrue(apply.ApplyPlayerDowned(2, CoopConvert.ToNet(head)));
            yield return Frames(2);
            Assert.IsTrue(crew.context.IsDowned);
            Assert.IsTrue(crew.ShownDowned);
            Assert.IsFalse(crew.Lying, "In zero-g it hangs, it does not lie.");
            Assert.IsTrue(crew.beacon.Shown && crew.beacon.visuals.activeSelf && crew.beacon.lamp.enabled, "The beacon shows, with its light.");
            Assert.Less(Vector3.Distance(crew.head.position, head), 0.01f, "Where they were caught.");
            Assert.Less(crew.head.forward.y, -0.4f, "Head hanging forward.");
            Vector3 beacon = crew.BeaconPosition;
            Assert.Less(beacon.y, head.y - 0.2f, "On the chest.");
            Assert.Less(Vector3.Distance(CoopRevive.BeaconOf(crew.context), beacon), 0.001f);
            Assert.IsNotNull(crew.head.Find(Net.Voice.VoiceTalkLight.ChildName), "The talk light is still on the helmet.");

            // Their own samples do not move the suit while they are down.
            var moved = AvatarSnapshot.Default;
            moved.headPosition = CoopConvert.ToNet(head + Vector3.forward * 2f);
            moved.headRotation = CoopConvert.ToNet(Quaternion.identity);
            crew.Push(2.0, in moved);
            crew.Render(2.0);
            yield return Frames(2);
            Assert.Less(Vector3.Distance(crew.head.position, head), 0.01f, "Still and limp.");

            // The ring on the beacon, and the line on this player's wrist.
            Assert.IsTrue(apply.ApplyReviveProgress(2, 0.5f, 1));
            yield return Frames(1);
            Assert.AreEqual(crew.beacon.ring.Length / 2, crew.beacon.LitSegments);
            var wrist = m_Player.GetComponentInChildren<WristDisplay>(true);
            wrist.Refresh();
            StringAssert.Contains("CREW 3 DOWN", wrist.crewText.text);
            StringAssert.Contains("[#####-----]", wrist.crewText.text);

            Assert.IsTrue(apply.ApplyPlayerRevived(2, 1));
            yield return Frames(2);
            Assert.IsFalse(crew.ShownDowned);
            Assert.IsFalse(crew.beacon.Shown);
            Assert.IsFalse(crew.beacon.lamp.enabled);
            Assert.Less(Vector3.Distance(crew.head.position, head + Vector3.forward * 2f), 0.05f, "Standing again, following them.");
            Object.Destroy(crew.gameObject);
        }

        /// <summary>
        /// The reviewer's blocker: a client that only WATCHES the host get caught and downed must stay in the contract.
        /// Its replica waits for a RunEnded after every jumpscare; the downing settles that wait on every headset.
        /// </summary>
        [UnityTest]
        public IEnumerator Client_WatchingTheHostCaughtAndDowned_StaysInTheContract_NoLinkLossAfterTheWait()
        {
            BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            yield return StartClientRun(apply);
            Vector3 head = MapPoints.Freighter(0.5f, 1.6f, 19.6f);
            var host = AddRemoteCrew(0, head);
            yield return Frames(2);
            var creature = m_Director.ActiveEnemy;

            Assert.IsTrue(apply.ApplyCatchStarted(0), "the host is caught");
            yield return WaitFor(() => creature.ScarePhase >= 2, 3f);
            Assert.IsTrue(apply.ApplyPlayerDowned(0, CoopConvert.ToNet(head)));
            yield return WaitFor(() => !creature.IsScaring, 10f);
            yield return Frames(2);
            Assert.IsTrue(host.context.IsDowned, "The host's suit is downed here.");
            Assert.IsFalse(creature.AwaitingRunEnd, "Nothing to wait for: the catch was settled by the downing.");
            yield return Seconds(EnemyDrone.RunEndWaitSeconds + 0.5f);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase, "Still in the contract.");
            Assert.AreEqual(0, creature.RunEndLinkLosses, "Not taken for a lost link.");
            Assert.IsFalse(m_Player.IsDowned);

            // The same when the downing arrives after the jumpscare has already ended here.
            Assert.IsTrue(apply.ApplyPlayerRevived(0, 1));
            Assert.IsTrue(apply.ApplyCatchStarted(0));
            yield return WaitFor(() => !creature.IsScaring, 12f);
            Assert.IsTrue(creature.AwaitingRunEnd);
            Assert.IsTrue(apply.ApplyPlayerDowned(0, CoopConvert.ToNet(head)));
            Assert.IsFalse(creature.AwaitingRunEnd);
            yield return Seconds(EnemyDrone.RunEndWaitSeconds + 0.5f);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.AreEqual(0, creature.RunEndLinkLosses);
            Object.Destroy(host.gameObject);
        }

        [UnityTest]
        public IEnumerator Client_InTheCave_ADownedSuitLiesOnTheFloor_ChestOverTheCatchPoint()
        {
            int cave = System.Array.FindIndex(m_Director.maps, m => m.mapId == "lava_tube");
            Assert.GreaterOrEqual(cave, 0);
            MissionDirector.MapOverride = cave;
            BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            yield return StartClientRun(apply);
            yield return new WaitForFixedUpdate();
            Physics.SyncTransforms();
            Vector3 head = new Vector3(0f, 1.7f, 24f);
            Assert.AreNotEqual(Vector3.zero, MapGravity.GravityAt(head), "A point of the cave with gravity.");
            Assert.IsTrue(Physics.Raycast(head, Vector3.down, out RaycastHit floor, 4f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore));
            var crew = AddRemoteCrew(2, head);
            yield return Frames(2);

            Assert.IsTrue(apply.ApplyPlayerDowned(2, CoopConvert.ToNet(head)));
            yield return Frames(2);
            Assert.IsTrue(crew.Lying, "Under gravity it lies down.");
            Assert.Less(crew.head.position.y - floor.point.y, 0.6f, "The helmet is down by the floor.");
            Assert.Less(crew.suit.torso.position.y - floor.point.y, 0.6f, "So is the body.");
            Vector3 beacon = crew.BeaconPosition;
            Assert.Less(new Vector2(beacon.x - head.x, beacon.z - head.z).magnitude, 0.6f, "The chest is over where they were caught.");
            Assert.That(beacon.y - floor.point.y, Is.InRange(0.05f, 0.7f));
            Object.Destroy(crew.gameObject);
        }

        [UnityTest]
        public IEnumerator Client_ItsHandOnADownedCrewMatesBeacon_SendsReviveHoldOnceOnAndOnceOff()
        {
            BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            var holds = new List<(byte, bool)>();
            apply.Revive.Requests = new Holds(holds);
            yield return StartClientRun(apply);
            FreeHands();
            Vector3 head = m_Player.HeadPosition + m_Player.head.forward * 0.9f;
            var crew = AddRemoteCrew(2, head);
            yield return Frames(2);
            Assert.IsTrue(apply.ApplyPlayerDowned(2, CoopConvert.ToNet(head)));
            yield return Frames(2);

            RightHandOnBeacon(crew.context, true, grip: false);
            yield return Frames(5);
            Assert.AreEqual(0, holds.Count, "An open hand on it is not a hold.");
            RightHandOnBeacon(crew.context, true);
            yield return Frames(5);
            CollectionAssert.AreEqual(new[] { ((byte)2, true) }, holds);
            Assert.AreEqual(2, apply.Revive.SentHold);
            RightHandOnBeacon(crew.context, false);
            yield return Frames(5);
            CollectionAssert.AreEqual(new[] { ((byte)2, true), ((byte)2, false) }, holds);
            Assert.AreEqual(0f, crew.context.ReviveProgress, "A client never fills the ring itself: the host does.");
            m_Player.rightHand.GripOverride = 0f;
            Object.Destroy(crew.gameObject);
        }

        sealed class Holds : ICoopReviveRequests
        {
            readonly List<(byte, bool)> m_List;
            public Holds(List<(byte, bool)> list) { m_List = list; }
            public void ReviveHold(byte slot, bool on) => m_List.Add((slot, on));
        }
    }
}

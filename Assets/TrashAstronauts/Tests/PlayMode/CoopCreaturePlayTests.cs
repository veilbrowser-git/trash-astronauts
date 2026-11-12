using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.Tools;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Co-op stage 4, the shared creature, in the real scene with no networking. As a host the scene runs the
    /// creature (stepped by hand, as every creature test does) and its broadcasts are recorded; as a client the
    /// host's messages go straight into <see cref="CoopClientApply"/>. Crew members are head-only stand-ins and the
    /// session is a stub that records what the game asked of it.
    /// </summary>
    public class CoopCreaturePlayTests
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
            // Private scares on this headset are not what these tests are about (and they make sounds of their own).
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
            public readonly List<string> calls = new List<string>();
            public readonly List<NoiseEvent> noises = new List<NoiseEvent>();
            public readonly List<(Vector3 origin, Vector3 hit)> darts = new List<(Vector3, Vector3)>();
            public bool IsClient => client;
            public bool IsHost => !client;
            public int LocalSlot => slot;
            public void RequestStart() => calls.Add("start");
            public void RequestExtract() => calls.Add("extract");
            public void RequestDoor(SlidingDoor door, bool desiredOpen) => calls.Add("door " + door.doorId + (desiredOpen ? " open" : " close"));
            public void ReportPointCut(Cuttable panel, int pointIndex) => calls.Add("cut " + panel.panelId + " " + pointIndex);
            public void ReportSalvageDamage(SalvageItem item, int newValue) => calls.Add("damage " + item.NetIndex + " " + newValue);
            public void ReportWire(WebTripwire wire, WireChange change) => calls.Add("wire " + wire.wireId + " " + change);
            public void ReportTranqHit(Vector3 origin, Vector3 hitPoint) { calls.Add("tranq"); darts.Add((origin, hitPoint)); }
            public void ForwardNoise(in NoiseEvent noise) { calls.Add("noise " + noise.Kind); noises.Add(noise); }
            public void ReportPause(bool paused) => calls.Add("pause " + paused);
            public void ReportSettingsChanged(Handedness handedness) => calls.Add("settings " + handedness);
        }

        /// <summary>Writes down what the host broadcasts (the creature's messages in full, the rest by name).</summary>
        sealed class Recorder : ICoopBroadcast
        {
            public readonly List<string> log = new List<string>();
            public readonly List<float> tranquilized = new List<float>();
            public readonly List<float> blackouts = new List<float>();
            public int Count(string prefix) => log.Count(l => l.StartsWith(prefix, System.StringComparison.Ordinal));
            public List<string> States => log.Where(l => l.StartsWith("state ", System.StringComparison.Ordinal)).ToList();

            public void RunStart(in RunStartParams parameters) => log.Add("runstart " + parameters.creatureIndex);
            public void WorldState(in WorldStateMsg state) => log.Add("world");
            public void ExtractRefused(byte slot, byte missingSlotsMask) => log.Add("refused");
            public void RunEnded(in RunEndedMsg result) => log.Add("runended " + result.outcome);
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
            public void Tranquilized(float seconds) { tranquilized.Add(seconds); log.Add("tranquilized"); }
            public void CatchStarted(byte victimSlot) => log.Add("catch " + victimSlot);
            public void BlackoutStarted(float seconds) { blackouts.Add(seconds); log.Add("blackout on"); }
            public void BlackoutEnded(bool instant) => log.Add("blackout off " + instant);
            public bool LocalGrab(byte itemId) => true;
            public void LocalRelease(byte itemId) { }
        }

        Recorder Host(bool creature = true)
        {
            m_Director.Net = new FakeNet { client = false, slot = 0 };
            var recorder = new Recorder();
            m_Rules = new CoopHostRules(m_Director, recorder);
            m_Rules.CreatureEnabled = creature;
            return recorder;
        }

        /// <summary>The run a host with the creature switched on would send, built here offline.</summary>
        RunStartParams HostRunWithCreature(int runIndex)
        {
            m_Director.CoopRideAlong = true;
            m_Director.CoopCreatureEnabled = true;
            var run = m_Director.BuildRunParams();
            m_Director.CoopRideAlong = false;
            m_Director.CoopCreatureEnabled = false;
            run.runIndex = (ushort)runIndex;
            run.planHash = run.ComputePlanHash();
            return run;
        }

        FakeNet BecomeClient(int slot)
        {
            var net = new FakeNet { client = true, slot = slot };
            m_Player.AssignId(new PlayerId(slot), false);
            m_Director.Net = net;
            return net;
        }

        /// <summary>A client in a contract with the host's creature aboard: its replica, waiting for the host.</summary>
        IEnumerator StartClientRun(CoopClientApply apply, int runIndex = 5)
        {
            var net = m_Director.Net;
            m_Director.Net = null; // built as the host would build it
            var run = HostRunWithCreature(runIndex);
            m_Director.Net = net;
            Assert.AreNotEqual(RunStartParams.NoCreature, run.creatureIndex);
            Assert.IsTrue(apply.ApplyRunStart(in run));
            yield return Frames(2);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.IsTrue(m_Director.CreatureAboard);
            Assert.IsTrue(m_Director.ActiveEnemy.IsReplica);
        }

        int LocalPulses() => (m_Player.leftHand != null ? m_Player.leftHand.PulseCount : 0) + (m_Player.rightHand != null ? m_Player.rightHand.PulseCount : 0);

        static CreatureSnapshot Sample(Vector3 at, float yaw, EnemyState state, byte flags = CreatureFlags.Running, byte climbStep = 0)
        {
            return new CreatureSnapshot
            {
                position = CoopConvert.ToNet(at), yawDegrees = yaw, state = state, targetSlot = NetProtocol.NoSlot,
                climbStep = climbStep, flags = flags,
            };
        }

        static RunEndedMsg Caught(MissionDirector director) => new RunEndedMsg
        {
            outcome = MissionOutcome.FailedCaught, quota = director.Session.Quota, elapsedSeconds = 30f, oxygenRemainingSeconds = 100f,
        };

        static float GapToSurface(Vector3 tip, Vector3 normal)
        {
            if (!Physics.Raycast(tip + normal * 0.3f, -normal, out RaycastHit hit, 0.8f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore)) return 99f;
            return Mathf.Abs(hit.distance - 0.3f);
        }

        // ================================================================== host half

        [UnityTest]
        public IEnumerator Host_TheCreatureSwitch_BringsItAndTheBlackoutsAboard_AnySpeciesTheMimicToo_AndOffIsTheRideAlong()
        {
            var rec = Host(false);
            yield return null;
            int mimic = Enumerable.Range(0, m_Director.enemies.Length).First(i => m_Director.enemies[i].GetComponent<VoiceMimic>() != null);

            // Off (the default until the wiring turns it on): the ride-along contract, exactly as before.
            Assert.IsFalse(m_Rules.CreatureEnabled);
            var run = m_Director.BuildRunParams();
            Assert.AreEqual(RunStartParams.NoCreature, run.creatureIndex);
            Assert.AreEqual(0, run.hazardFlags);
            Assert.AreEqual(0u, run.wireArmedMask);

            // On: a creature every time, any species (the Mimic too, since it hears the whole crew), blackouts on, no webs.
            m_Rules.CreatureEnabled = true;
            MissionDirector.CreatureOverride = null;
            var picks = new HashSet<int>();
            for (int seed = 1; seed <= 60; seed++)
            {
                MissionDirector.SeedOverride = seed;
                run = m_Director.BuildRunParams();
                picks.Add(run.creatureIndex);
                Assert.AreEqual(RunStartParams.HazardBlackouts, run.hazardFlags & RunStartParams.HazardBlackouts);
                Assert.AreEqual(0u, run.wireArmedMask);
            }
            CollectionAssert.DoesNotContain(picks, (int)RunStartParams.NoCreature);
            CollectionAssert.Contains(picks, mimic);
            Assert.AreEqual(m_Director.enemies.Length, picks.Count);
            // Asked for by name, the Mimic comes aboard.
            MissionDirector.CreatureOverride = mimic;
            MissionDirector.SeedOverride = 7;
            run = m_Director.BuildRunParams();
            Assert.AreEqual(mimic, (int)run.creatureIndex);
            Assert.AreEqual(run.ComputePlanHash(), run.planHash);

            // The contract it starts has it awake on the host, simulated here, with a blackout to come.
            MissionDirector.CreatureOverride = 0;
            MissionDirector.SeedOverride = 44;
            Assert.IsTrue(m_Director.StartMission(m_Director.LocalPlayerId));
            yield return Frames(2);
            Assert.IsTrue(m_Director.CreatureAboard);
            Assert.IsTrue(m_Director.ActiveEnemy.IsRunning);
            Assert.IsFalse(m_Director.ActiveEnemy.IsReplica);
            Assert.AreEqual(m_Director.Config.blackoutsEnabled, m_Director.blackout.IsScheduled);
            StringAssert.StartsWith("runstart " + m_Director.CurrentRun.creatureIndex, rec.log.First(l => l.StartsWith("runstart")));

            // Disposing the rules turns it off again.
            m_Rules.Dispose();
            m_Rules = null;
            Assert.IsFalse(m_Director.CoopCreatureEnabled);
        }

        [UnityTest]
        public IEnumerator Host_ItHuntsAndCatchesACrewMembersStandIn_WhileTheHostIsSafeInTheTug_AndTheRunEndsWithTheJumpscare()
        {
            var rec = Host();
            Assert.IsTrue(m_Director.StartMission(m_Director.LocalPlayerId));
            yield return Frames(2);
            var crew = AddRemotePlayer(1, MapPoints.Freighter(0f, 0f, 19.6f));
            yield return Frames(3);
            Assert.IsTrue(m_Player.InSafeZone, "The host watches from the tug.");
            Assert.IsFalse(crew.InSafeZone);

            var creature = m_Director.ActiveEnemy;
            var brainChanges = new List<string>();
            creature.Brain.StateChanged += (from, to) => brainChanges.Add(to.ToString());
            rec.log.Clear();
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            Physics.SyncTransforms();
            Vector3 head = crew.HeadPosition;
            creature.transform.position = new Vector3(head.x, creature.hoverHeight, head.z + 3f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.back);
            bool chasedTheCrew = false;
            for (int i = 0; i < 300 && !creature.IsScaring && m_Director.Phase == MissionPhase.Active; i++)
            {
                creature.Step(0.05f);
                if (creature.State == EnemyState.Chase)
                {
                    Assert.AreEqual(new PlayerId(1), creature.Quarry, "Its quarry is the crew member in the wreck, not the host in the tug.");
                    chasedTheCrew = true;
                    Assert.IsTrue(m_Rules.Creature.SampleCreature(out var s));
                    Assert.AreEqual(1, s.targetSlot, "The stream names the quarry.");
                }
            }
            Assert.IsTrue(chasedTheCrew, "It should have chased the crew member.");
            Assert.IsTrue(creature.IsScaring, "It should have caught the crew member's stand-in.");
            float caughtAt = Time.unscaledTime;
            Assert.AreEqual(new PlayerId(1), creature.CatchVictim);

            // Everyone is told who was caught, once; the state messages are the brain's own changes, one each.
            Assert.AreEqual(1, rec.Count("catch "));
            Assert.AreEqual(1, rec.Count("catch 1"));
            Assert.Greater(brainChanges.Count, 0);
            CollectionAssert.AreEqual(brainChanges, rec.States.Select(l => l.Split(' ')[1]).ToList());
            Assert.Contains("state Chase 1", rec.log);

            // The host is a bystander: nothing of the victim's happens to it, and nothing plays in its ears.
            // (The suit's own "a chase is on" buzz, felt the frame after the chase began, is not the jumpscare's.)
            yield return Frames(2);
            int ui = Sfx.UiPlayCount, pulses = LocalPulses();
            for (float t = 0f; t < 3f && creature.ScarePhase < 2; t += Time.unscaledDeltaTime) yield return null;
            yield return Seconds(0.4f);
            Assert.AreEqual(2, creature.ScarePhase);
            Assert.Less(Vector3.Distance(creature.transform.position, crew.HeadPosition), 0.8f, "It is on its victim.");
            Assert.IsTrue(m_Director.blackout.IsBlackout, "The wreck's lights die for everyone.");
            Assert.AreEqual(0, rec.Count("blackout"), "The jumpscare's darkness is every machine's own, never broadcast.");
            Assert.AreEqual(0, m_Player.visor.Stage);
            Assert.AreEqual(0f, m_Player.fader.Alpha);
            Assert.AreEqual(pulses, LocalPulses());
            Assert.AreEqual(ui, Sfx.UiPlayCount);
            // Mid-jumpscare it cannot be darted or knocked off balance.
            Assert.IsFalse(m_Rules.HostTranqHitReport(CoopConvert.ToNet(creature.transform.position + Vector3.back * 3f), CoopConvert.ToNet(creature.transform.position), 1));
            Assert.IsFalse(creature.Stagger(1f));
            Assert.IsFalse(creature.Tranquilize(5f));

            float endedAt = -1f;
            for (float waited = 0f; waited < 10f; waited += Time.unscaledDeltaTime)
            {
                if (m_Director.Phase != MissionPhase.Active) { endedAt = Time.unscaledTime; break; }
                Assert.AreEqual(ui, Sfx.UiPlayCount, "Nothing in the host's ears while someone else is caught.");
                Assert.AreEqual(0f, m_Player.fader.Alpha);
                yield return null;
            }
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            Assert.AreEqual(MissionOutcome.FailedCaught, m_Director.Session.Result.Outcome);
            Assert.That(endedAt - caughtAt, Is.InRange(creature.ScareTotalSeconds - 0.05f, creature.ScareTotalSeconds + 0.75f), "The run ends when the jumpscare does.");
            Assert.AreEqual(1, rec.Count("runended FailedCaught"));
            Assert.Less(rec.log.IndexOf("catch 1"), rec.log.IndexOf("runended FailedCaught"));
            Assert.AreEqual(0, rec.Count("blackout"));
            Assert.IsFalse(m_Director.blackout.IsBlackout);
            Object.Destroy(crew.gameObject);
        }

        [UnityTest]
        public IEnumerator Host_ACrewMemberWhoDropsOutTheMomentTheyAreCaught_StillEndsTheRunAtTheDeadline()
        {
            var rec = Host();
            Assert.IsTrue(m_Director.StartMission(m_Director.LocalPlayerId));
            yield return Frames(2);
            var crew = AddRemotePlayer(2, MapPoints.Freighter(0f, 0f, 19.6f));
            yield return Frames(3);
            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            Physics.SyncTransforms();
            Vector3 head = crew.HeadPosition;
            creature.transform.position = new Vector3(head.x, creature.hoverHeight, head.z + 1.6f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.back);
            for (int i = 0; i < 200 && !creature.IsScaring && m_Director.Phase == MissionPhase.Active; i++) creature.Step(0.05f);
            Assert.IsTrue(creature.IsScaring);
            float caughtAt = Time.unscaledTime;
            Assert.AreEqual(1, rec.Count("catch 2"));

            Object.Destroy(crew.gameObject);
            float deadline = creature.ScareTotalSeconds + EnemyDrone.CatchDeadlineSlack;
            float endedAt = -1f;
            for (float waited = 0f; waited < 10f; waited += Time.unscaledDeltaTime)
            {
                if (m_Director.Phase != MissionPhase.Active) { endedAt = Time.unscaledTime; break; }
                yield return null;
            }
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase, "The host ends the run even though its victim is gone.");
            Assert.That(endedAt - caughtAt, Is.InRange(deadline - 0.05f, deadline + 0.75f));
            Assert.AreEqual(1, rec.Count("runended FailedCaught"));
            Assert.IsFalse(creature.IsScaring);
            Assert.IsFalse(m_Director.blackout.IsBlackout);
        }

        [UnityTest]
        public IEnumerator Host_ACrewMembersNoise_DrawsTheCreature_CreditedToThem_ClampedToItsKind_AndRateLimited()
        {
            Host();
            Assert.IsTrue(m_Director.StartMission(m_Director.LocalPlayerId));
            yield return Frames(2);
            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            creature.patrolRoute = new EnemyWaypoint[0];
            Vector3 at = MapPoints.Freighter(0f, creature.hoverHeight, 14f);
            creature.transform.position = at;
            for (int i = 0; i < 4; i++) creature.Step(0.05f);
            Assert.AreEqual(EnemyState.Patrol, creature.State);
            var noise = m_Director.noise;

            // Kinds the host makes itself are not taken from a client.
            Assert.IsFalse(m_Rules.HostNoise(CoopConvert.ToNet(at + Vector3.forward), 1f, NoiseKind.Door, 2));
            Assert.IsFalse(m_Rules.HostNoise(CoopConvert.ToNet(at + Vector3.forward), 1f, NoiseKind.PanelBreak, 2));
            Assert.IsFalse(m_Rules.HostNoise(CoopConvert.ToNet(at + Vector3.forward), 1f, NoiseKind.Web, 2));
            Assert.AreEqual(EnemyState.Patrol, creature.State);

            // A crash two metres away, credited to slot 2 and clamped to the loudest an impact may be.
            Vector3 crash = MapPoints.Freighter(0f, 1.2f, 15.5f);
            Assert.IsTrue(m_Rules.HostNoise(CoopConvert.ToNet(crash), 2.5f, NoiseKind.Impact, 2));
            Assert.AreEqual(new PlayerId(2), noise.LastNoise.Source);
            Assert.AreEqual(1f, noise.LastNoise.Loudness, 1e-4f);
            Assert.AreEqual(NoiseKind.Impact, noise.LastNoise.Kind);
            Assert.Less(Vector3.Distance(crash, noise.LastNoise.Position), 0.02f);
            Assert.AreEqual(EnemyState.Investigate, creature.State, "It goes to see what that was.");
            Assert.AreEqual(new PlayerId(2), creature.LastHeardNoiseBy);
            Assert.Less(Vector3.Distance(crash, creature.Brain.PointOfInterest), 0.02f);
            Assert.IsTrue(m_Rules.Creature.SampleCreature(out var heard));
            Assert.IsTrue(heard.HasFlag(CreatureFlags.HeardNoiseToggle), "The stream tells the crew it heard something.");

            // At most eight a second from one crew member; another crew member has their own eight.
            int accepted = 0;
            for (int i = 0; i < 12; i++) if (m_Rules.HostNoise(CoopConvert.ToNet(crash), 0.3f, NoiseKind.Thruster, 3)) accepted++;
            Assert.AreEqual(NetProtocol.MaxNoisePerSecond, accepted);
            Assert.IsTrue(m_Rules.HostNoise(CoopConvert.ToNet(crash), 0.3f, NoiseKind.Voice, 1));
            Assert.AreEqual(new PlayerId(1), noise.LastNoise.Source);
            yield return Seconds(1.05f);
            Assert.IsTrue(m_Rules.HostNoise(CoopConvert.ToNet(crash), 0.3f, NoiseKind.Thruster, 3), "A new second, a new allowance.");

            // Nothing is heard outside a contract.
            m_Director.FailMission(FailureReason.OxygenDepleted, PlayerId.Local);
            Assert.IsFalse(m_Rules.HostNoise(CoopConvert.ToNet(crash), 1f, NoiseKind.Impact, 2));
        }

        [UnityTest]
        public IEnumerator Host_ADartReport_SedatesInRange_AndIsRefusedOutOfRangeOrWideOfIt()
        {
            var rec = Host();
            Assert.IsTrue(m_Director.StartMission(m_Director.LocalPlayerId));
            yield return Frames(2);
            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            creature.patrolRoute = new EnemyWaypoint[0];
            creature.transform.position = MapPoints.Freighter(0f, creature.hoverHeight, 14f);
            for (int i = 0; i < 4; i++) creature.Step(0.05f);
            yield return null;
            Vector3 body = creature.Walker.surface.position;
            float range = m_Rules.Creature.TranqRange();
            Assert.AreEqual(m_Player.tools.OfType<TranqTool>().First().range, range);

            // Too far for the tool, even allowing for a slightly old view.
            Vector3 far = body + Vector3.back * (range + CoopCreatureHost.TranqRangeSlack + 1f);
            Assert.IsFalse(m_Rules.HostTranqHitReport(CoopConvert.ToNet(far), CoopConvert.ToNet(body), 1));
            // A hit point nowhere near it.
            Vector3 near = body + Vector3.back * 4f;
            Assert.IsFalse(m_Rules.HostTranqHitReport(CoopConvert.ToNet(near), CoopConvert.ToNet(body + Vector3.left * 4f), 1));
            Assert.IsFalse(creature.IsSedated);
            CollectionAssert.IsEmpty(rec.tranquilized);
            Assert.AreEqual(2, m_Rules.Creature.TranqRefused);

            // In range, on it: asleep, exactly as the host's own dart would leave it, and the crew is told for how long.
            Vector3 inRange = body + Vector3.back * (range + 1f);
            Assert.IsTrue(m_Rules.HostTranqHitReport(CoopConvert.ToNet(inRange), CoopConvert.ToNet(body + Vector3.up * 0.3f), 1));
            Assert.IsTrue(creature.IsSedated);
            Assert.AreEqual(EnemyState.Dormant, creature.State);
            Assert.AreEqual(1, rec.tranquilized.Count);
            Assert.AreEqual(creature.SedatedSecondsLeft, rec.tranquilized[0], 1e-3f);
            Assert.AreEqual(m_Director.Config.tranqSleepSeconds * creature.sedationMultiplier, rec.tranquilized[0], 1e-3f);
            Assert.IsTrue(m_Rules.Creature.SampleCreature(out var s));
            Assert.IsTrue(s.HasFlag(CreatureFlags.Sedated));
            Assert.GreaterOrEqual(s.sedationSecondsLeft, creature.SedatedSecondsLeft - 1e-3f);
            Assert.GreaterOrEqual(rec.Count("state Dormant"), 1, "Falling asleep is a state change the crew hears.");

            // The host's own dart goes out the same way.
            Assert.IsTrue(creature.Tranquilize(2f));
            Assert.AreEqual(2, rec.tranquilized.Count);

            // No creature running, nothing to dart.
            m_Director.FailMission(FailureReason.OxygenDepleted, PlayerId.Local);
            Assert.IsFalse(m_Rules.HostTranqHitReport(CoopConvert.ToNet(inRange), CoopConvert.ToNet(body), 1));
        }

        [UnityTest]
        public IEnumerator Host_AnAwayCrewMemberIsNeverTargeted_NorOneInTheTug_WhileSomeoneIsOutside()
        {
            Host();
            Assert.IsTrue(m_Director.StartMission(m_Director.LocalPlayerId));
            yield return Frames(2);
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 19.6f));
            var away = AddRemotePlayer(1, MapPoints.Freighter(0f, 0f, 12f));
            var safe = AddRemotePlayer(2, m_Director.hubSpawn.position);
            yield return Frames(4);
            Assert.IsFalse(m_Player.InSafeZone);
            Assert.IsTrue(safe.InSafeZone);
            away.IsAway = true;

            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            creature.patrolRoute = new EnemyWaypoint[0];
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            Physics.SyncTransforms();
            // Right in front of the away player, who would be seen and caught at once if they were there.
            Vector3 head = away.HeadPosition;
            creature.transform.position = new Vector3(head.x, creature.hoverHeight, head.z + 1.5f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.back);
            for (int i = 0; i < 80; i++)
            {
                creature.transform.position = new Vector3(head.x, creature.hoverHeight, head.z + 1.5f);
                creature.Step(0.05f);
                Assert.IsFalse(creature.IsScaring, "An away player is never caught (step " + i + ").");
                Assert.AreNotEqual(new PlayerId(1), creature.Quarry, "An away player is never the quarry.");
                Assert.AreNotEqual(new PlayerId(2), creature.Quarry, "Someone safe in the tug is not the quarry while the host is out.");
                Assert.IsTrue(m_Rules.Creature.SampleCreature(out var s));
                Assert.AreNotEqual(1, s.targetSlot);
                Assert.AreNotEqual(2, s.targetSlot);
            }
            Assert.AreEqual(m_Director.LocalPlayerId, creature.Quarry, "The one out in the wreck is who it is after.");
            Object.Destroy(away.gameObject);
            Object.Destroy(safe.gameObject);
        }

        [UnityTest]
        public IEnumerator Host_TheStreamGoesOut10TimesASecond_20WhileAlert_AndBlackoutsAreBroadcast()
        {
            var rec = Host();
            Assert.IsFalse(m_Rules.TrySampleCreature(0.0, out _), "No contract, no stream.");
            Assert.IsTrue(m_Director.StartMission(m_Director.LocalPlayerId));
            yield return Frames(2);
            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            creature.patrolRoute = new EnemyWaypoint[0];
            creature.transform.SetPositionAndRotation(MapPoints.Freighter(0f, creature.hoverHeight, 14f), Quaternion.Euler(0f, 90f, 0f));
            for (int i = 0; i < 4; i++) creature.Step(0.05f);
            Assert.AreEqual(EnemyState.Patrol, creature.State);

            int Count(double from)
            {
                int n = 0;
                for (int tick = 0; tick < 20; tick++)
                {
                    if (m_Rules.TrySampleCreature(from + tick * 0.05, out var snapshot))
                    {
                        n++;
                        Assert.Less(Vector3.Distance(CoopConvert.ToUnity(snapshot.position), creature.transform.position), 0.02f);
                        Assert.LessOrEqual(Quant.YawDelta(snapshot.yawDegrees, creature.transform.eulerAngles.y), 0.01f);
                        Assert.AreEqual(creature.State, snapshot.state);
                        Assert.IsTrue(snapshot.HasFlag(CreatureFlags.Running));
                        Assert.AreEqual(CreatureSnapshot.ClimbToStep(creature.Walker.TargetClimb), snapshot.climbStep);
                        Assert.AreEqual(creature.Walker.Side > 0f, snapshot.HasFlag(CreatureFlags.WallSide));
                    }
                }
                return n;
            }
            Assert.AreEqual(10, Count(100.0), "10 Hz while it roams.");
            Assert.IsTrue(m_Rules.HostNoise(CoopConvert.ToNet(MapPoints.Freighter(0f, 1.2f, 15.5f)), 1f, NoiseKind.Impact, 1));
            Assert.AreEqual(EnemyState.Investigate, creature.State);
            Assert.AreEqual(20, Count(200.0), "20 Hz while it investigates.");

            // The host's blackouts go to the crew; the schedule is the host's alone.
            rec.log.Clear();
            m_Director.blackout.TriggerNow(4f);
            Assert.AreEqual(1, rec.Count("blackout on"));
            Assert.AreEqual(4f, rec.blackouts[0], 1e-3f);
            m_Director.blackout.EndBlackout(false);
            Assert.AreEqual(1, rec.Count("blackout off False"));
        }

        // ================================================================== client half

        [UnityTest]
        public IEnumerator Client_TheReplicaFollowsTheHostsSamples_AFewCentimetresBehind_HoldsStillWithoutThem_AndItsFeetStillPlant()
        {
            var net = BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            yield return StartClientRun(apply);
            var creature = m_Director.ActiveEnemy;
            var walker = creature.Walker;
            var animator = creature.GetComponent<CreatureAnimator>();
            Assert.IsFalse(m_Director.blackout.IsScheduled, "No blackout is scheduled on a client.");

            // Nothing from the host: it stays where it is, and stepping it by hand does nothing either.
            Vector3 dock = creature.transform.position;
            for (int i = 0; i < 20; i++) creature.Step(0.05f);
            yield return Seconds(0.5f);
            Assert.AreEqual(dock, creature.transform.position);
            Assert.AreEqual(EnemyState.Dormant, creature.State);
            Assert.IsTrue(apply.ApplyCreatureStateChanged(EnemyState.Patrol, NetProtocol.NoSlot));

            // The host's samples, 20 a second, of it walking along the deck at 0.9 m/s.
            Vector3 start = MapPoints.Freighter(0f, creature.hoverHeight, 12f);
            double offset = 500.0; // the host's clock is somewhere else entirely
            double t0 = Time.unscaledTimeAsDouble, lastPush = double.NegativeInfinity;
            Vector3 Path(double t) => start + Vector3.forward * (float)(0.9 * System.Math.Max(0.0, t - t0));
            float worst = 0f;
            int checks = 0;
            while (Time.unscaledTimeAsDouble - t0 < 2.0)
            {
                double now = Time.unscaledTimeAsDouble;
                if (now - lastPush >= 0.05)
                {
                    Assert.IsTrue(apply.ApplyCreatureSnapshot(Sample(Path(now), 0f, EnemyState.Patrol), now + offset));
                    lastPush = now;
                }
                yield return null;
                double render = Time.unscaledTimeAsDouble - SnapshotBuffer<CreatureSnapshot>.InterpolationDelay;
                if (render - t0 > 0.15 && Time.unscaledTimeAsDouble - lastPush < 0.06)
                {
                    worst = Mathf.Max(worst, Vector3.Distance(creature.transform.position, Path(render)));
                    checks++;
                }
            }
            Assert.Greater(checks, 10);
            Assert.Less(worst, 0.05f, "The replica should be where the host's creature was 100 ms ago, within a few centimetres.");
            Assert.LessOrEqual(Quant.YawDelta(creature.transform.eulerAngles.y, 0f), 1f);
            Assert.AreEqual(0f, walker.ClimbOverride);
            Assert.AreEqual(EnemyState.Patrol, creature.State);
            Assert.Greater(creature.SnapshotsApplied, 30);

            // The samples stop: it glides on for at most a quarter of a second, then holds still.
            yield return Seconds(0.5f);
            Vector3 held = creature.transform.position;
            Assert.Less(Vector3.Distance(held, Path(lastPush)), 0.9f * 0.26f + 0.02f);
            yield return Seconds(0.6f);
            Assert.AreEqual(held, creature.transform.position, "No motion without samples.");

            // Standing still on the deck, its feet are planted on it exactly as on the host.
            for (float t = 0f; t < 12f && walker.Climb > 0.001f; t += Time.deltaTime) yield return null;
            yield return Seconds(2f);
            Assert.AreEqual(CrawlSurface.Floor, walker.Current);
            Assert.IsTrue(animator.HasFootIk);
            Assert.Greater(animator.FootIkWeight, 0.99f);
            for (int i = 0; i < animator.LegCount; i++)
            {
                Assert.IsTrue(animator.IsFootPlanted(i), "Leg " + i + " should be planted. " + animator.DescribeLeg(i));
                Assert.Less(GapToSurface(animator.FootTip(i), walker.SurfaceNormal), 0.04f, "Leg " + i + " should touch the deck. " + animator.DescribeLeg(i));
            }

            // Up the wall on its left, as the host says: the walker takes both, so the dart volume is where the host's is.
            Assert.IsTrue(apply.ApplyCreatureSnapshot(Sample(held, 0f, EnemyState.Patrol, CreatureFlags.Running, 1), Time.unscaledTimeAsDouble + offset));
            yield return Frames(2);
            Assert.AreEqual(0.5f, walker.ClimbOverride);
            Assert.AreEqual(-1f, walker.SideOverride);
            Assert.AreEqual(-1f, walker.Side);
            CollectionAssert.IsEmpty(net.calls);
        }

        [UnityTest]
        public IEnumerator Client_StateCuesFireOncePerHostMessage_WithNoSamples_AndTheReadersShowTheHostsCreature()
        {
            BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            yield return StartClientRun(apply);
            var creature = m_Director.ActiveEnemy;
            int cues = creature.StateCuesPresented;

            var sequence = new[] { EnemyState.Patrol, EnemyState.Investigate, EnemyState.Chase, EnemyState.Search, EnemyState.Patrol };
            foreach (var state in sequence)
            {
                Assert.IsTrue(apply.ApplyCreatureStateChanged(state, state == EnemyState.Chase ? (byte)1 : NetProtocol.NoSlot));
                Assert.AreEqual(state, creature.State);
                yield return null;
                if (state == EnemyState.Chase)
                {
                    Assert.AreEqual(new PlayerId(1), creature.Quarry);
                    if (Sfx.Library != null) Assert.AreEqual(Sfx.Library.droneChase, creature.voice.clip, "The chase loop is on.");
                    Assert.IsTrue(creature.eyeLight.enabled);
                }
            }
            Assert.AreEqual(cues + sequence.Length, creature.StateCuesPresented, "One cue per message, no more, no fewer.");
            for (int i = 0; i < 10; i++) yield return null;
            Assert.AreEqual(cues + sequence.Length, creature.StateCuesPresented, "Nothing repeats a cue between messages.");

            // A sample that disagrees about the state does not change it (or cue anything): only the messages do.
            double offset = 70.0;
            Vector3 at = MapPoints.Freighter(0f, creature.hoverHeight, 13f);
            apply.ApplyCreatureSnapshot(Sample(at, 0f, EnemyState.Chase, CreatureFlags.Running | CreatureFlags.Lurking), Time.unscaledTimeAsDouble + offset);
            yield return Seconds(0.25f);
            Assert.AreEqual(EnemyState.Patrol, creature.State);
            Assert.AreEqual(cues + sequence.Length, creature.StateCuesPresented);
            Assert.IsTrue(creature.IsLurking, "Lying in wait, as the host says.");
            Assert.IsFalse(creature.hum != null && creature.hum.isPlaying);

            // It heard something on the host: the wrist's "IT HEARD THAT" goes by the flip.
            float heardBefore = creature.LastHeardNoiseTime;
            apply.ApplyCreatureSnapshot(Sample(at, 0f, EnemyState.Patrol, CreatureFlags.Running | CreatureFlags.HeardNoiseToggle | CreatureFlags.Staggered), Time.unscaledTimeAsDouble + offset);
            yield return Seconds(0.25f);
            Assert.Greater(creature.LastHeardNoiseTime, heardBefore);
            Assert.AreEqual(Time.time, creature.LastHeardNoiseTime, 0.3f);
            Assert.IsTrue(creature.IsStaggered);
            Assert.IsFalse(creature.IsLurking);

            // Darted on the host: asleep here for as long as the host says, until it wakes there.
            Assert.IsTrue(apply.ApplyCreatureStateChanged(EnemyState.Dormant, NetProtocol.NoSlot));
            Assert.IsTrue(apply.ApplyTranquilized(6f));
            Assert.IsTrue(creature.IsSedated);
            Assert.AreEqual(6f, creature.SedatedSecondsLeft, 0.1f);
            yield return Seconds(0.3f);
            Assert.Less(creature.SedatedSecondsLeft, 5.9f, "It counts down here between messages.");
            Assert.IsTrue(apply.ApplyCreatureStateChanged(EnemyState.Patrol, NetProtocol.NoSlot));
            Assert.IsFalse(creature.IsSedated);

            // The replica is never darted, staggered or sent to sleep by anything here.
            Assert.IsFalse(creature.Tranquilize(5f));
            Assert.IsFalse(creature.Stagger(1f));
            Assert.IsFalse(creature.IsSedated);
        }

        [UnityTest]
        public IEnumerator Client_CaughtHere_PlaysTheWholeJumpscare_AndStaysInTheDarkUntilTheHostEndsTheRun()
        {
            BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            yield return StartClientRun(apply);
            var creature = m_Director.ActiveEnemy;
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 19.6f));
            yield return Frames(4);
            int ui = Sfx.UiPlayCount, pulses = LocalPulses();

            Assert.IsTrue(apply.ApplyCatchStarted(1));
            float caughtAt = Time.unscaledTime;
            Assert.IsTrue(creature.IsScaring);
            Assert.AreEqual(new PlayerId(1), creature.CatchVictim);
            Assert.IsFalse(apply.ApplyCatchStarted(1), "One catch at a time.");
            Assert.IsFalse(apply.ApplyCreatureSnapshot(Sample(MapPoints.Freighter(0f, 1f, 10f), 0f, EnemyState.Chase), double.NaN), "Samples are ignored while it plays.");
            Assert.IsFalse(apply.ApplyTranquilized(5f), "Not darted mid-jumpscare.");

            // Today's jumpscare, on this headset's own camera.
            for (float t = 0f; t < 3f && creature.ScarePhase < 2; t += Time.unscaledDeltaTime) yield return null;
            yield return Seconds(0.4f);
            Assert.AreEqual(2, creature.ScarePhase);
            Assert.Greater(m_Player.visor.Stage, 0, "The visor cracks.");
            Assert.IsTrue(m_Director.blackout.IsBlackout);
            Assert.Greater(Sfx.UiPlayCount, ui, "The sound is in the victim's ears.");
            Assert.Greater(LocalPulses(), pulses, "The controllers kick.");
            Assert.Less(Vector3.Distance(creature.transform.position, m_Player.HeadPosition), 0.8f);

            // It ends on time, and the screen stays black: only the host ends the run.
            while (creature.IsScaring && Time.unscaledTime - caughtAt < 8f) yield return null;
            Assert.IsFalse(creature.IsScaring);
            Assert.That(Time.unscaledTime - caughtAt, Is.InRange(creature.ScareTotalSeconds - 0.05f, creature.ScareTotalSeconds + 0.5f), "Today's timing.");
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.IsTrue(creature.AwaitingRunEnd);
            yield return Seconds(1.5f);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase, "The phase does not change until the host's RunEnded.");
            Assert.AreEqual(1f, m_Player.fader.Alpha, 1e-3f, "In the dark until then.");

            var message = Caught(m_Director);
            Assert.IsNotNull(apply.ApplyRunEnded(in message));
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            Assert.AreEqual(MissionOutcome.FailedCaught, m_Director.Session.Result.Outcome);
            Assert.IsFalse(creature.AwaitingRunEnd);
            for (float until = Time.unscaledTime + 4f; Time.unscaledTime < until && (m_Player.fader.Alpha > 0f || !m_Player.InSafeZone);) yield return null;
            Assert.AreEqual(0f, m_Player.fader.Alpha, "Home, and the light comes back.");
            Assert.IsTrue(m_Player.InSafeZone);
            Assert.AreEqual(0, m_Player.visor.Stage);
            Assert.IsFalse(m_Director.blackout.IsBlackout);
            Assert.AreEqual(0, creature.RunEndLinkLosses);
        }

        [UnityTest]
        public IEnumerator Client_ARunEndedThatArrivesMidJumpscare_WaitsForIt_ButNoLongerThanASecond()
        {
            BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            yield return StartClientRun(apply);
            var creature = m_Director.ActiveEnemy;
            creature.scareMaulSeconds = 0.4f; // a short one, to keep the test short

            Assert.IsTrue(apply.ApplyCatchStarted(1));
            for (float t = 0f; t < 3f && creature.ScarePhase < 2; t += Time.unscaledDeltaTime) yield return null;
            var message = Caught(m_Director);
            Assert.IsNull(apply.ApplyRunEnded(in message), "Held while the jumpscare plays.");
            Assert.IsTrue(creature.HoldingRunEnd);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            while (creature.IsScaring) yield return null;
            yield return Frames(2);
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase, "Applied as soon as it is over.");
            Assert.IsFalse(creature.HoldingRunEnd);
            Assert.IsNull(apply.ApplyRunEnded(in message), "Once.");

            // Next contract: a jumpscare that would run long is cut off a second after the host's word.
            Assert.IsTrue(apply.ApplyReturnedToHub());
            yield return StartClientRun(apply, 6);
            creature = m_Director.ActiveEnemy;
            creature.scareMaulSeconds = 0.4f;
            creature.scareAfterSeconds = 4f;
            Assert.IsTrue(apply.ApplyCatchStarted(1));
            for (float t = 0f; t < 3f && creature.ScarePhase < 3; t += Time.unscaledDeltaTime) yield return null;
            message = Caught(m_Director);
            float heldAt = Time.unscaledTime;
            Assert.IsNull(apply.ApplyRunEnded(in message));
            while (m_Director.Phase == MissionPhase.Active && Time.unscaledTime - heldAt < 3f) yield return null;
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            Assert.That(Time.unscaledTime - heldAt, Is.InRange(EnemyDrone.RunEndHoldSeconds - 0.05f, EnemyDrone.RunEndHoldSeconds + 0.3f));
            Assert.IsFalse(creature.IsScaring, "What was left of it stopped with the run.");
            Assert.AreEqual(0, m_Player.visor.Stage);
            Assert.IsFalse(m_Director.blackout.IsBlackout);
        }

        [UnityTest]
        public IEnumerator Client_CaughtHere_WithNoRunEndedAfterwards_DropsTheRunAsALostLink_AndLetsTheLightBackIn()
        {
            BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            yield return StartClientRun(apply);
            var creature = m_Director.ActiveEnemy;
            creature.scareMaulSeconds = 0.4f;
            Assert.IsTrue(m_Player.InSafeZone);
            int credits = m_Director.Profile.Data.totalCredits;

            Assert.IsTrue(apply.ApplyCatchStarted(1));
            while (creature.IsScaring) yield return null;
            float endedAt = Time.unscaledTime;
            Assert.AreEqual(1f, m_Player.fader.Alpha, 1e-3f);
            while (m_Director.Phase == MissionPhase.Active && Time.unscaledTime - endedAt < 6f) yield return null;
            Assert.AreEqual(MissionPhase.Hub, m_Director.Phase, "No word from the host: the link is gone, back to the hub.");
            Assert.That(Time.unscaledTime - endedAt, Is.InRange(EnemyDrone.RunEndWaitSeconds - 0.05f, EnemyDrone.RunEndWaitSeconds + 0.3f));
            Assert.AreEqual(1, creature.RunEndLinkLosses);
            Assert.AreEqual(credits, m_Director.Profile.Data.totalCredits, "Nothing banked.");
            yield return Seconds(1f);
            Assert.AreEqual(0f, m_Player.fader.Alpha, "The light comes back.");
        }

        [UnityTest]
        public IEnumerator Client_SomeoneElseCaught_PlaysOnTheirAvatar_WithNoneOfTheVictimsEffectsHere()
        {
            BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            yield return StartClientRun(apply);
            var crew = AddRemotePlayer(2, MapPoints.Freighter(0f, 0f, 19.6f));
            yield return Frames(3);
            var creature = m_Director.ActiveEnemy;
            int ui = Sfx.UiPlayCount, pulses = LocalPulses();

            Assert.IsTrue(apply.ApplyCatchStarted(2));
            float caughtAt = Time.unscaledTime;
            Assert.AreEqual(new PlayerId(2), creature.CatchVictim);
            for (float t = 0f; t < 3f && creature.ScarePhase < 2; t += Time.unscaledDeltaTime) yield return null;
            yield return Seconds(0.4f);
            Assert.AreEqual(2, creature.ScarePhase);
            Assert.Less(Vector3.Distance(creature.transform.position, crew.HeadPosition), 0.8f, "Same motion, on the victim's avatar.");
            Assert.IsTrue(m_Director.blackout.IsBlackout);
            Assert.IsFalse(apply.ApplyBlackoutEnded(true), "The jumpscare keeps its own darkness.");
            Assert.IsTrue(m_Director.blackout.IsBlackout);
            while (creature.IsScaring && Time.unscaledTime - caughtAt < 8f)
            {
                Assert.AreEqual(0, m_Player.visor.Stage, "This visor stays whole.");
                Assert.AreEqual(0f, m_Player.fader.Alpha, "No fade here.");
                Assert.AreEqual(pulses, LocalPulses(), "No haptics here.");
                Assert.AreEqual(ui, Sfx.UiPlayCount, "Nothing in this player's ears: the sounds come from the creature.");
                yield return null;
            }
            Assert.That(Time.unscaledTime - caughtAt, Is.InRange(creature.ScareTotalSeconds - 0.05f, creature.ScareTotalSeconds + 0.5f));
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.AreEqual(0f, m_Player.fader.Alpha);
            Assert.IsFalse(m_Director.blackout.IsBlackout);

            var message = Caught(m_Director);
            Assert.IsNotNull(apply.ApplyRunEnded(in message));
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            Object.Destroy(crew.gameObject);
        }

        [UnityTest]
        public IEnumerator Client_NoBlackoutIsScheduledHere_TheHostsSwitchTheLights_AndALocalNoiseIsForwardedOnce()
        {
            var net = BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            Assert.IsFalse(apply.ApplyBlackoutStarted(1f), "No contract, no blackout.");
            yield return StartClientRun(apply);
            var creature = m_Director.ActiveEnemy;
            var blackout = m_Director.blackout;
            Assert.AreNotEqual(0, m_Director.CurrentRun.hazardFlags & RunStartParams.HazardBlackouts, "The host's contract has blackouts...");
            Assert.IsFalse(blackout.IsScheduled, "...but they are the host's to schedule.");
            var light = blackout.lights.First(l => l != null && l.enabled);

            Assert.IsTrue(apply.ApplyBlackoutStarted(0.2f));
            Assert.IsTrue(blackout.IsBlackout);
            Assert.IsFalse(light.enabled);
            Assert.IsFalse(apply.ApplyBlackoutStarted(0.2f));
            yield return Seconds(0.6f);
            Assert.IsTrue(blackout.IsBlackout, "It lasts until the host says it is over.");
            Assert.IsTrue(apply.ApplyBlackoutEnded(true));
            Assert.IsFalse(blackout.IsBlackout);
            Assert.IsTrue(light.enabled);
            Assert.IsFalse(apply.ApplyBlackoutEnded(true));

            // What the local player does is the host creature's to hear, sent once; the replica is deaf to it.
            Assert.IsTrue(apply.ApplyCreatureStateChanged(EnemyState.Patrol, NetProtocol.NoSlot));
            Vector3 at = creature.transform.position + Vector3.forward;
            float heard = creature.LastHeardNoiseTime;
            int sent = net.noises.Count, forwarded = m_Director.noise.ForwardedCount;
            m_Director.noise.Emit(new NoiseEvent(at, 0.77f, NoiseKind.Cutting, m_Director.LocalPlayerId));
            Assert.AreEqual(1, net.calls.Count(c => c == "noise Cutting"));
            Assert.AreEqual(sent + 1, net.noises.Count);
            Assert.AreEqual(m_Director.LocalPlayerId, net.noises[sent].Source);
            Assert.AreEqual(0.77f, net.noises[sent].Loudness, 1e-4f);
            Assert.AreEqual(forwarded + 1, m_Director.noise.ForwardedCount);
            Assert.AreEqual(EnemyState.Patrol, creature.State, "The replica does not hear it.");
            Assert.AreEqual(heard, creature.LastHeardNoiseTime);
            // Someone else's noise is not this headset's to send.
            m_Director.noise.Emit(new NoiseEvent(at, 1f, NoiseKind.Cutting, new PlayerId(2)));
            Assert.AreEqual(sent + 1, net.noises.Count);
            Assert.AreEqual(EnemyState.Patrol, creature.State);
        }

        [UnityTest]
        public IEnumerator Client_FiringTheTranq_ReportsTheHit_SpendsTheShootersOwnDart_AndDoesNotSedateHere()
        {
            var net = BecomeClient(1);
            var apply = new CoopClientApply(m_Director);
            yield return StartClientRun(apply);
            var creature = m_Director.ActiveEnemy;
            Assert.IsTrue(apply.ApplyCreatureStateChanged(EnemyState.Patrol, NetProtocol.NoSlot));
            Vector3 at = MapPoints.Freighter(0f, creature.hoverHeight, 13f);
            for (int i = 0; i < 6; i++)
            {
                apply.ApplyCreatureSnapshot(Sample(at, 0f, EnemyState.Patrol), Time.unscaledTimeAsDouble + 40.0);
                yield return Seconds(0.06f);
            }
            yield return Seconds(1.5f);
            Assert.Less(Vector3.Distance(creature.transform.position, at), 0.02f);

            m_Director.Profile.Data.tranqDarts = 2;
            int impacts = net.noises.Count(n => n.Kind == NoiseKind.Impact && n.Source == m_Director.LocalPlayerId);
            var tranq = m_Player.tools.OfType<TranqTool>().First();
            tranq.transform.SetParent(null);
            Vector3 bodyAt = creature.GetComponentInChildren<SphereCollider>().bounds.center;
            Vector3 from = bodyAt + Vector3.back * 2f + Vector3.up * 0.3f;
            tranq.transform.SetPositionAndRotation(from, Quaternion.LookRotation(bodyAt - from));
            Physics.SyncTransforms();
            Assert.IsTrue(tranq.Fire(), "The dart hits the replica where the host's creature is.");
            Assert.AreEqual(1, m_Director.Profile.Data.tranqDarts, "The dart came out of the shooter's own save.");
            Assert.AreEqual(1, tranq.HitsReported);
            Assert.AreEqual(1, net.darts.Count);
            Assert.Less(Vector3.Distance(net.darts[0].origin, tranq.muzzle.position), 0.01f);
            Assert.Less(Vector3.Distance(net.darts[0].hit, bodyAt), 0.6f);
            Assert.IsFalse(creature.IsSedated, "Only the host puts it to sleep.");
            Assert.AreEqual(EnemyState.Patrol, creature.State);
            // The quiet thump of the shot goes to the host as this player's noise.
            Assert.AreEqual(impacts + 1, net.noises.Count(n => n.Kind == NoiseKind.Impact && n.Source == m_Director.LocalPlayerId));

            // The host's word puts it to sleep here.
            Assert.IsTrue(apply.ApplyCreatureStateChanged(EnemyState.Dormant, NetProtocol.NoSlot));
            Assert.IsTrue(apply.ApplyTranquilized(m_Director.Config.tranqSleepSeconds));
            Assert.IsTrue(creature.IsSedated);
        }
    }
}

using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.Tools;
using TrashAstronauts.UI;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>Spiders that crawl on surfaces, web tripwires, spiderling swarms, slamming doors, contract conditions, best haul.</summary>
    public class CrawlWebsConditionsPlayTests
    {
        MissionDirector m_Director;
        PlayerContext m_Player;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            MissionDirector.StorageOverride = new MemorySaveStorage();
            MissionDirector.SeedOverride = 91;
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
            MissionDirector.StorageOverride = null;
            MissionDirector.SeedOverride = null;
            MissionDirector.CreatureOverride = null;
            MissionDirector.MapOverride = null;
            MissionDirector.ConditionOverride = null;
        }

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        static IEnumerator Seconds(float seconds)
        {
            for (float t = 0f; t < seconds; t += Time.deltaTime) yield return null;
        }

        [UnityTest]
        public IEnumerator Spiders_WalkOnTheFloor_ClimbTheWall_AndCrossTheCeiling_InsteadOfFloating()
        {
            for (int kind = 0; kind < 4; kind++)
            {
                MissionDirector.CreatureOverride = kind;
                if (m_Director.Phase == MissionPhase.Active) m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
                m_Director.StartMission(PlayerId.Local);
                yield return Frames(3);
                var creature = m_Director.ActiveEnemy;
                var walker = creature.GetComponent<SurfaceWalker>();
                Assert.IsNotNull(walker, creature.displayName + " should walk on surfaces.");
                Transform body = walker.surface;
                int mask = GameLayers.EnvironmentMask;

                // Dormant, it lurks on the ceiling above its dock.
                yield return Seconds(0.3f);
                Assert.AreEqual(CrawlSurface.Ceiling, walker.Current, creature.displayName + " should wait on the ceiling.");
                Assert.IsTrue(Physics.Raycast(body.position, Vector3.up, out RaycastHit above, 1.2f, mask), creature.displayName + " should be right under the ceiling.");
                Assert.Less(above.distance, walker.bodyHeight + 0.2f);
                Assert.Less(Vector3.Dot(body.up, Vector3.up), -0.8f, "On the ceiling it hangs upside down.");

                // Put it in the middle of the long corridor and bring it down to the floor.
                creature.WakeNow();
                creature.transform.position = MapPoints.Freighter(0f, creature.hoverHeight, 15f);
                creature.transform.rotation = Quaternion.LookRotation(Vector3.forward);
                walker.ClimbOverride = 0f;
                yield return Seconds(0.2f);                    // it is moved: the body snaps to the new place
                for (float t = 0f; t < 12f && walker.Climb > 0.001f; t += Time.deltaTime) yield return null;
                yield return Seconds(0.6f);
                Assert.AreEqual(CrawlSurface.Floor, walker.Current);
                Assert.IsTrue(Physics.Raycast(body.position, Vector3.down, out RaycastHit floor, 1.5f, mask), creature.displayName + " should have a floor under it.");
                Assert.Less(floor.distance, walker.bodyHeight + 0.15f, creature.displayName + " should be standing on the floor, not floating. Height above floor: " + floor.distance);
                Assert.Greater(Vector3.Dot(body.up, Vector3.up), 0.9f);
                Assert.Less(body.position.y, creature.transform.position.y - 0.5f, "Its body should be well below the height it used to float at.");

                // Up the wall.
                walker.ClimbOverride = 0.5f;
                bool onWall = false;
                for (float t = 0f; t < 12f && !onWall; t += Time.deltaTime)
                {
                    onWall = walker.Current == CrawlSurface.Wall && Mathf.Abs(walker.Climb - 0.5f) < 0.01f;
                    yield return null;
                }
                yield return Seconds(0.6f);
                Assert.IsTrue(onWall, creature.displayName + " never reached the wall. Climb " + walker.Climb + " on " + walker.Current);
                Assert.Less(Mathf.Abs(Vector3.Dot(body.up, Vector3.up)), 0.35f, "On a wall it stands sideways.");
                Assert.IsTrue(Physics.Raycast(body.position, -body.up, out RaycastHit wall, 1.2f, mask), creature.displayName + " should have the wall under its feet.");
                Assert.Less(wall.distance, walker.bodyHeight + 0.2f);

                // And over onto the ceiling.
                walker.ClimbOverride = 1f;
                for (float t = 0f; t < 12f && walker.Climb < 0.999f; t += Time.deltaTime) yield return null;
                yield return Seconds(0.6f);
                Assert.AreEqual(CrawlSurface.Ceiling, walker.Current);
                Assert.Less(Vector3.Dot(body.up, Vector3.up), -0.8f);
                Assert.Greater(body.position.y, creature.transform.position.y + 0.3f);

                // The dart has to be aimed at where it actually is.
                var collider = creature.GetComponentInChildren<SphereCollider>();
                Assert.Less(Vector3.Distance(collider.bounds.center, body.position), 0.1f, "Its hit volume should travel with its body.");
                walker.ClimbOverride = null;
            }
        }

        [UnityTest]
        public IEnumerator ChasingSpider_ComesDownToTheFloor_AndStillCatchesYou()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 19.6f));
            yield return Frames(4);
            var creature = m_Director.ActiveEnemy;
            var walker = creature.GetComponent<SurfaceWalker>();
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            Physics.SyncTransforms();
            Vector3 head = m_Player.HeadPosition;
            creature.transform.position = new Vector3(head.x, creature.hoverHeight, head.z + 6f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.back);
            walker.ClimbOverride = 1f;
            yield return Seconds(0.4f);
            walker.ClimbOverride = null;

            bool chased = false, cameDown = false;
            for (int i = 0; i < 3000 && !creature.IsScaring && m_Director.Phase == MissionPhase.Active; i++)
            {
                // Make a noise now and then, so it comes looking instead of wandering off on its rounds.
                if (i % 60 == 0) m_Director.noise.Emit(new NoiseEvent(m_Player.HeadPosition, 1f, NoiseKind.Impact, PlayerId.Local));
                creature.Step(0.03f);
                chased |= creature.State == EnemyState.Chase;
                if (creature.State == EnemyState.Chase && walker.Current == CrawlSurface.Floor) cameDown = true;
                yield return null;
            }
            Assert.IsTrue(chased, "It should have seen a player standing in the corridor.");
            Assert.IsTrue(creature.IsScaring || m_Director.Phase != MissionPhase.Active, "It should have caught them.");
            Assert.IsTrue(cameDown || walker.TargetClimb < 0.01f, "Hunting, it heads for the floor.");
        }

        [UnityTest]
        public IEnumerator WebTripwire_SlowsYou_AndTellsTheSpiderWhereYouAre_UnlessYouBurnIt()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(3);
            var wires = Object.FindObjectsByType<WebTripwire>(FindObjectsSortMode.None);
            Assert.IsTrue(wires.All(w => w.visual.activeSelf == w.Armed));
            Assert.IsTrue(wires.All(w => !w.Armed), "No web is strung across a route any more; only the corner webs remain.");
            Assert.IsEmpty(Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Where(t => t.name.StartsWith("Cocoon")).ToArray(), "The hanging cocoons are gone.");

            var wire = wires.First(w => w.wireId == "a_corridor_mid");
            wire.SetArmed(true);
            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            // Bring the spider within earshot but out of sight, facing away.
            Vector3 mid = (wire.endA.position + wire.endB.position) * 0.5f;
            creature.transform.position = new Vector3(mid.x, creature.hoverHeight, mid.z + 9f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.forward);

            // Drift down the corridor into it.
            m_Player.locomotion.TeleportTo(new Vector3(mid.x, 0f, mid.z - 1.6f), Quaternion.identity);
            yield return Frames(4);
            var zeroG = m_Player.locomotion.zeroG;
            zeroG.Stop();
            zeroG.AddImpulse(Vector3.forward * 1.6f);
            int noiseBefore = m_Director.noise.EmittedCount;
            float speedBefore = 0f;
            for (float t = 0f; t < 4f && !wire.Snapped; t += Time.deltaTime)
            {
                speedBefore = zeroG.Speed;
                yield return null;
            }
            Assert.IsTrue(wire.Snapped, "Floating through a web should break it.");
            Assert.IsFalse(wire.Armed);
            Assert.IsFalse(wire.visual.activeSelf, "A broken web is gone.");
            Assert.Less(zeroG.Speed, speedBefore * 0.5f, "The web should drag at the player. Before " + speedBefore + ", after " + zeroG.Speed);
            Assert.Greater(m_Director.noise.EmittedCount, noiseBefore);
            Assert.AreEqual(NoiseKind.Web, m_Director.noise.LastNoise.Kind);
            Assert.That(creature.State, Is.EqualTo(EnemyState.Investigate).Or.EqualTo(EnemyState.Chase), "The spider should feel the web go and come to look.");

            // A cutter flame held on a web burns it away without a sound.
            var other = wires.First(w => w.wireId == "a_corridor_far");
            other.SetArmed(true);
            var cutter = m_Player.tools.OfType<PlasmaCutterTool>().First();
            int noiseNow = m_Director.noise.EmittedCount;
            other.BurnAway();
            Assert.IsTrue(other.Burnt);
            Assert.IsFalse(other.Armed);
            Assert.IsFalse(other.Snapped);
            Assert.AreEqual(noiseNow, m_Director.noise.EmittedCount, "Burning a web is silent.");
            Assert.IsNotNull(cutter.tip);

            // Even an infested contract strings none: the young are what changes.
            MissionDirector.ConditionOverride = ContractCondition.Infested;
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            // Back in the tug (otherwise the player is still standing in the web they broke, and breaks it again).
            m_Player.locomotion.TeleportTo(m_Director.hubSpawn.position, m_Director.hubSpawn.rotation);
            yield return Frames(4);
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            yield return Frames(3);
            wires = Object.FindObjectsByType<WebTripwire>(FindObjectsSortMode.None);
            Assert.IsTrue(wires.All(w => !w.Armed), "No webs across the routes, infested or not.");
            Assert.Greater(ContractConditions.SwarmMultiplier(ContractCondition.Infested), ContractConditions.SwarmMultiplier(ContractCondition.Standard));
        }

        [UnityTest]
        public IEnumerator YouHearItWalking_UntilItStopsToListen()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(3);
            var creature = m_Director.ActiveEnemy;
            var walker = creature.GetComponent<SurfaceWalker>();
            Assert.IsNotNull(walker.steps, "It should have footsteps.");
            Assert.AreEqual(1f, walker.steps.spatialBlend, "Footsteps come from where it is.");
            Assert.IsTrue(walker.steps.transform.IsChildOf(walker.surface), "They come from its body, not from mid-air.");
            yield return Seconds(0.4f);
            Assert.AreEqual(0f, walker.StepLoudness, "Dormant, it makes no sound.");

            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            creature.lurkChance = 0f;
            float loudest = 0f;
            for (int i = 0; i < 240; i++)
            {
                creature.Step(Time.deltaTime > 0f ? Time.deltaTime : 0.02f);
                loudest = Mathf.Max(loudest, walker.StepLoudness);
                yield return null;
            }
            Assert.Greater(loudest, 0.1f, "Walking its rounds, its feet can be heard.");
            Assert.IsTrue(walker.steps.isPlaying || walker.steps.volume < 0.02f);

            // Sooner or later it stops dead and listens: no footsteps, no hum, and it does not move.
            creature.lurkChance = 1f;
            bool lurked = false;
            for (int i = 0; i < 6000 && !lurked; i++)
            {
                creature.Step(0.05f);
                lurked = creature.IsLurking;
                if (i % 200 == 0) yield return null;
            }
            Assert.IsTrue(lurked, "It should sometimes stop on its rounds.");
            Vector3 where = creature.transform.position;
            for (int i = 0; i < 20; i++) creature.Step(0.05f);
            Assert.AreEqual(where, creature.transform.position, "Lying in wait, it does not move.");
            yield return Seconds(1.2f);
            Assert.Less(walker.StepLoudness, 0.01f, "And it makes no sound.");

            // A noise ends the wait at once.
            m_Director.noise.Emit(new NoiseEvent(creature.transform.position + Vector3.back * 3f, 1f, NoiseKind.Impact, PlayerId.Local));
            Assert.IsFalse(creature.IsLurking);
            Assert.AreEqual(EnemyState.Investigate, creature.State);
        }

        [UnityTest]
        public IEnumerator SomeOfTheWrecksLightsAreDead_DifferentOnesEachContract_AndTheAirlockStaysLit()
        {
            BlackoutController.DamageOverride = 1f;
            var blackout = m_Director.blackout;
            var patterns = new System.Collections.Generic.HashSet<string>();
            for (int seed = 1; seed <= 6; seed++)
            {
                MissionDirector.SeedOverride = seed;
                if (m_Director.Phase == MissionPhase.Active) m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
                m_Director.StartMission(PlayerId.Local);
                yield return Frames(2);
                var lights = m_Director.ActiveMap.lights;
                foreach (var l in lights)
                {
                    if (blackout.IsDead(l)) Assert.IsFalse(l.enabled, l.name + " should be dark.");
                    if (l.transform.position.z < 7.75f) Assert.IsFalse(blackout.IsDead(l), "The airlock light must always work.");
                }
                Assert.Less(blackout.DeadLightCount, lights.Length * 0.6f, "Most of the wreck still has light.");
                patterns.Add(string.Join(",", lights.Select(l => blackout.IsDead(l) ? "x" : "o")));

                // A blackout and its recovery leave the dead ones dead.
                blackout.TriggerNow(2f);
                Assert.IsTrue(lights.All(l => !l.enabled));
                blackout.EndBlackout(true);
                foreach (var l in lights) if (blackout.IsDead(l)) Assert.IsFalse(l.enabled);
            }
            Assert.Greater(patterns.Count, 2, "Which lights are dead should change from contract to contract.");

            // Back in the hub everything is restored.
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            yield return Frames(2);
            Assert.AreEqual(0, blackout.DeadLightCount);
        }

        [UnityTest]
        public IEnumerator Flashlight_RunsDown_Recovers_AndTheTugRechargesIt()
        {
            var torch = m_Player.tools.OfType<FlashlightTool>().First();
            Assert.AreEqual(1f, torch.Charge, 0.001f);
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            m_Director.keepPlayersInHub = false;
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 14f));
            yield return Frames(4);
            Assert.IsFalse(m_Player.InSafeZone);

            // Out in the wreck with the light on, it drains.
            torch.SetOn(true, false);
            torch.batterySeconds = 10f;
            float before = torch.Charge;
            yield return Seconds(1f);
            Assert.Less(torch.Charge, before - 0.05f, "The flashlight should run down while it is on.");

            // Flat: it goes out, says so on the wrist, and will not come back on straight away.
            torch.SetCharge(0.001f);
            yield return Seconds(0.3f);
            Assert.IsTrue(torch.IsFlat);
            Assert.IsFalse(torch.IsOn, "A flat flashlight goes out.");
            Assert.IsFalse(torch.beam.enabled);
            torch.SetOn(true, false);
            Assert.IsFalse(torch.IsOn, "It cannot be switched on while flat.");
            var wrist = m_Player.GetComponentInChildren<WristDisplay>(true);
            wrist.Refresh();
            StringAssert.Contains("LIGHT FLAT", wrist.salvageText.text);

            // Left off, it recovers; then it lights again.
            yield return Seconds(1.2f);
            Assert.Greater(torch.Charge, 0.05f, "Switched off, it charges.");
            torch.SetOn(true, false);
            Assert.IsTrue(torch.IsOn);

            // The tug tops it up.
            m_Player.locomotion.TeleportTo(m_Director.hubSpawn.position, m_Director.hubSpawn.rotation);
            yield return Seconds(2.5f);
            Assert.Greater(torch.Charge, 0.95f, "Back in the tug the flashlight recharges.");
        }

        [UnityTest]
        public IEnumerator ContractConditions_ChangeTheRun_AndAreShownOnTheBoard()
        {
            float standardOxygen = m_Director.NextRunOxygenCapacity;

            MissionDirector.ConditionOverride = ContractCondition.ThinAir;
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(3);
            Assert.AreEqual(ContractCondition.ThinAir, m_Director.Condition);
            Assert.AreEqual(standardOxygen * 0.8f, m_Director.Session.Oxygen.Capacity, 0.5f, "Thin air means a fifth less oxygen.");
            Assert.AreEqual(1.5f, m_Director.Session.BonusMultiplier, 0.001f, "Hazard pay.");
            var board = Object.FindFirstObjectByType<MissionBoard>();
            StringAssert.Contains("THIN AIR", board.body.text);
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);

            MissionDirector.ConditionOverride = ContractCondition.RichWreck;
            MissionDirector.SeedOverride = 91;
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(3);
            foreach (var item in m_Director.Items.Where(i => i.definition != null && !i.definition.bonusSpawn))
                Assert.AreEqual(Mathf.RoundToInt(item.definition.creditValue * 1.3f), item.Value, item.name + " should be worth 30% more on a rich wreck.");
            Assert.AreEqual(standardOxygen, m_Director.Session.Oxygen.Capacity, 0.5f);
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);

            MissionDirector.ConditionOverride = ContractCondition.LightSleeper;
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            var creature = m_Director.ActiveEnemy;
            for (float t = 0f; t < 6f && creature.State == EnemyState.Dormant; t += Time.deltaTime) yield return null;
            Assert.AreNotEqual(EnemyState.Dormant, creature.State, "A light sleeper is up within a few seconds.");
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);

            // Left to chance, different seeds give different conditions, and the same seed gives the same one.
            MissionDirector.ConditionOverride = null;
            BlackoutController.DamageOverride = null;
            var seen = new System.Collections.Generic.HashSet<ContractCondition>();
            for (int seed = 1; seed <= 40; seed++) seen.Add(ContractConditions.Pick(seed));
            Assert.GreaterOrEqual(seen.Count, 6, "Conditions should vary from contract to contract.");
            Assert.AreEqual(ContractConditions.Pick(17), ContractConditions.Pick(17));
        }

        [UnityTest]
        public IEnumerator Spiderlings_WanderOnTheWall_AndBoltWhenYouComeClose()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(3);
            var swarms = Object.FindObjectsByType<SpiderlingSwarm>(FindObjectsSortMode.None);
            Assert.GreaterOrEqual(swarms.Length, 2, "There should be young around the egg sacs.");
            var swarm = swarms[0];
            Assert.GreaterOrEqual(swarm.spiders.Length, 6);
            yield return Seconds(0.5f);
            Assert.IsFalse(swarm.Alarmed);
            Assert.IsTrue(swarm.spiders.All(s => s.gameObject.activeSelf), "Left alone, they are out on the wall.");
            foreach (var spider in swarm.spiders)
            {
                var bounds = spider.GetComponent<MeshRenderer>().bounds;
                Assert.Less(Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z), 0.5f, "They are small.");
                Assert.Less(Vector3.Distance(spider.position, swarm.transform.position), 1.2f);
            }
            Vector3 before = swarm.spiders[0].localPosition;
            yield return Seconds(3f);
            Assert.AreNotEqual(before, swarm.spiders[0].localPosition, "They should be wandering about.");

            // Walk up to them.
            m_Player.transform.position += swarm.transform.position - m_Player.HeadPosition + Vector3.back * 0.2f;
            yield return Seconds(1.5f);
            Assert.IsTrue(swarm.Alarmed);
            Assert.AreEqual(1, swarm.ScatterCount);
            Assert.Greater(swarm.Hidden, 0.9f, "They should bolt into the sacs.");
            Assert.IsTrue(swarm.spiders.All(s => !s.gameObject.activeSelf || s.localScale.x < 0.02f));
        }

        [UnityTest]
        public IEnumerator DoorSlam_ShutsAnOpenDoorBehindYou()
        {
            var scares = m_Director.GetComponent<ScareDirector>();
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(3);
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            // Stand in the corridor with your back to the airlock door, a few metres from it.
            var airlockDoor = m_Director.sharedDoors[0];
            m_Player.locomotion.TeleportTo(airlockDoor.transform.position + Vector3.forward * 5f, Quaternion.identity);
            yield return Frames(4);
            Assert.IsTrue(airlockDoor.IsOpen);
            Assert.IsTrue(scares.Trigger(ScareKind.DoorSlam), "There is an open door behind the player to slam.");
            Assert.IsNotNull(scares.LastSlammedDoor);
            Assert.IsFalse(scares.LastSlammedDoor.IsOpen, "It should be shutting.");
            Vector3 toDoor = scares.LastSlammedDoor.transform.position - m_Player.HeadPosition;
            Assert.Greater(Vector3.Angle(m_Player.head.forward, toDoor), 70f, "It should be a door they are not looking at.");
            // Nothing to slam if every door is already shut.
            foreach (var door in m_Director.wreckDoors) door.SetOpen(false, true);
            Assert.IsFalse(scares.Trigger(ScareKind.DoorSlam));
        }

        [UnityTest]
        public IEnumerator BestHaul_IsRemembered_AndOnlyBeatenByABiggerOne()
        {
            Assert.AreEqual(0, m_Director.Profile.Data.bestHaul);
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            foreach (var item in m_Director.Items.Where(i => !i.IsMounted && i.definition != null).Take(4)) m_Director.DepositSalvage(item, PlayerId.Local);
            var first = m_Director.Extract(PlayerId.Local);
            Assert.Greater(first.Payout, 0);
            Assert.AreEqual(first.Payout, m_Director.Profile.Data.bestHaul);
            Assert.IsTrue(m_Director.Profile.LastRunWasRecord);

            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            m_Director.DepositSalvage(m_Director.Items.Where(i => !i.IsMounted && i.definition != null).OrderBy(i => i.Value).First(), PlayerId.Local);
            var second = m_Director.Extract(PlayerId.Local);
            Assert.Less(second.Payout, first.Payout);
            Assert.AreEqual(first.Payout, m_Director.Profile.Data.bestHaul, "A smaller haul does not replace the record.");
            Assert.IsFalse(m_Director.Profile.LastRunWasRecord);
        }
    }
}

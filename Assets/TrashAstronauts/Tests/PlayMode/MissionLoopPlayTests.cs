using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.Tools;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Runs the real scene in play mode and drives the whole mission loop through the same public
    /// methods the VR interactions call. No headset is involved: this verifies rules, wiring and
    /// physics, not comfort or performance.
    /// </summary>
    public class MissionLoopPlayTests
    {
        MemorySaveStorage m_Storage;
        MissionDirector m_Director;
        PlayerContext m_Player;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            m_Storage = new MemorySaveStorage();
            MissionDirector.StorageOverride = m_Storage;
            MissionDirector.SeedOverride = 20261111;
            MissionDirector.CreatureOverride = 0;
            MissionDirector.MapOverride = 0;
            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            yield return null;
            yield return null;
            m_Director = MissionDirector.Active;
            Assert.IsNotNull(m_Director, "Scene has no MissionDirector");
            m_Player = m_Director.Players.FirstOrDefault();
        }

        [TearDown]
        public void TearDown()
        {
            MissionDirector.StorageOverride = null;
            MissionDirector.SeedOverride = null;
            MissionDirector.CreatureOverride = null;
            MissionDirector.MapOverride = null;
        }

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        static IEnumerator Physics(int steps)
        {
            for (int i = 0; i < steps; i++) yield return new WaitForFixedUpdate();
        }

        /// <summary>Opens the wreck's doors instantly so drone-movement tests do not depend on door animation timing.</summary>
        void OpenWreckDoors()
        {
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            UnityEngine.Physics.SyncTransforms();
        }

        SalvageItem[] LooseItems() => m_Director.Items.Where(i => i != null && !i.IsSecured && !i.IsMounted && i.definition != null).ToArray();

        // ------------------------------------------------------------------ fresh launch

        [UnityTest]
        public IEnumerator FreshLaunch_IsASafeHub_WithAWorkingRig()
        {
            Assert.AreEqual(MissionPhase.Hub, m_Director.Phase);
            Assert.IsNotNull(m_Player, "The player rig did not register with the mission.");
            Assert.IsNotNull(m_Player.leftHand.interactor);
            Assert.IsNotNull(m_Player.rightHand.interactor);
            Assert.IsNotNull(Camera.main);
            Assert.IsTrue(m_Player.InSafeZone, "The player should start inside the tug.");
            Assert.IsFalse(m_Director.tugHatch.IsOpen, "The hatch is sealed until a contract starts.");
            Assert.AreEqual(EnemyState.Dormant, m_Director.enemies[0].State);
            Assert.AreEqual(4, m_Player.tools.Length);
            yield return Frames(3);
            foreach (var tool in m_Player.tools) Assert.IsTrue(tool.IsDocked, tool.toolName + " should start holstered");

            // No oxygen pressure before a contract.
            float before = m_Director.Session.Oxygen.Remaining;
            yield return Frames(10);
            Assert.AreEqual(before, m_Director.Session.Oxygen.Remaining);
        }

        [UnityTest]
        public IEnumerator Settings_HandednessAndComfort_ApplyAndPersist()
        {
            m_Director.Profile.Data.Handedness = Handedness.Left;
            m_Director.Profile.Data.snapTurnDegrees = 60f;
            m_Director.Profile.Data.seatedHeightOffset = 0.3f;
            m_Director.ApplyAndSaveSettings();
            yield return null;

            Assert.AreEqual(Handedness.Left, m_Player.Handedness);
            Assert.AreSame(m_Player.leftHand, m_Player.DominantHand);
            Assert.IsTrue(m_Player.wristDisplay.IsChildOf(m_Player.rightHand.transform), "Wrist display moves to the off hand.");
            Assert.AreEqual(60f, m_Player.locomotion.snapTurn.turnAmount);
            Assert.AreEqual(0.3f, m_Player.locomotion.SeatedHeightOffset, 0.001f);

            var reloaded = new ProfileService(m_Storage);
            reloaded.Load();
            Assert.AreEqual(Handedness.Left, reloaded.Data.Handedness);
            Assert.AreEqual(60f, reloaded.Data.snapTurnDegrees);
        }

        // ------------------------------------------------------------------ loot and reachability

        [UnityTest]
        public IEnumerator StartingAContract_SpawnsReachableLootWorthMoreThanTheQuota()
        {
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            yield return Frames(2);

            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.IsTrue(m_Director.tugHatch.IsOpen);
            Assert.Greater(m_Director.Session.TotalRegisteredValue, m_Director.Session.Quota);
            Assert.AreEqual(m_Director.Items.Count, m_Director.Session.Items.Count);
            Assert.AreEqual(m_Director.Items.Count, m_Director.Items.Select(i => i.SalvageId).Distinct().Count());

            foreach (var item in m_Director.Items)
                Assert.IsTrue(m_Director.bounds.Contains(item.transform.position), item.name + " spawned outside the level");

            foreach (SalvageCategory category in System.Enum.GetValues(typeof(SalvageCategory)))
                Assert.IsTrue(m_Director.Items.Any(i => i.definition.category == category), "No " + category + " this run");
        }

        [UnityTest]
        public IEnumerator ComfortTeleport_CanReachEverySpawnPoint_TheLockerAndTheBoard()
        {
            yield return null;
            var surfaces = Object.FindObjectsByType<TeleportSurface>(FindObjectsSortMode.None);
            Assert.Greater(surfaces.Length, 5);

            // Sample every teleport surface and keep the spots a player could actually land on.
            var landing = new System.Collections.Generic.List<Vector3>();
            foreach (var surface in surfaces)
            {
                var box = surface.GetComponent<BoxCollider>();
                Bounds b = box.bounds;
                for (float x = b.min.x; x <= b.max.x + 0.001f; x += 0.2f)
                {
                    for (float z = b.min.z; z <= b.max.z + 0.001f; z += 0.2f)
                    {
                        var p = new Vector3(x, 0f, z);
                        bool blocked = UnityEngine.Physics.CheckCapsule(p + Vector3.up * 0.34f, p + Vector3.up * 1.28f, 0.22f,
                            GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore);
                        if (!blocked) landing.Add(p);
                    }
                }
            }
            Assert.Greater(landing.Count, 200);

            float Nearest(Vector3 target)
            {
                float best = float.MaxValue;
                foreach (var p in landing)
                {
                    float d = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(target.x, target.z));
                    if (d < best) best = d;
                }
                return best;
            }

            foreach (var point in m_Director.spawnPoints)
            {
                Assert.Less(Nearest(point.transform.position), 0.95f, "Cannot stand within arm's reach of spawn point " + point.pointId);
                Assert.That(point.transform.position.y, Is.InRange(0.8f, 1.6f), point.pointId + " should float at a comfortable height");
            }
            Assert.Less(Nearest(m_Director.locker.zone.position), 0.95f, "Cannot reach the collection locker by teleport");
            foreach (var anchor in RecoveryAnchor.All)
                Assert.Less(Nearest(anchor.transform.position), 0.25f, anchor.name + " is not on a teleport surface");
        }

        [UnityTest]
        public IEnumerator Teleport_And_Unstuck_MoveTheRigThroughTheLocomotionSystem()
        {
            var anchor = RecoveryAnchor.All.First(a => a.name.Contains("Corridor Mid"));
            Assert.IsTrue(m_Player.locomotion.TeleportTo(anchor.transform.position));
            yield return Frames(4);
            Vector3 head = m_Player.HeadPosition;
            Assert.Less(Vector2.Distance(new Vector2(head.x, head.z), new Vector2(anchor.transform.position.x, anchor.transform.position.z)), 0.3f);

            // Shove the rig somewhere invalid, then ask for an unstuck.
            m_Player.transform.position += new Vector3(40f, 0f, 0f);
            Assert.IsTrue(m_Player.locomotion.Unstuck());
            yield return Frames(4);
            Assert.IsTrue(m_Director.bounds.Contains(m_Player.HeadPosition));
        }

        // ------------------------------------------------------------------ depositing

        [UnityTest]
        public IEnumerator DroppingSalvageInTheLocker_CountsEachItemExactlyOnce()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            var item = LooseItems().First(i => i.definition.category == SalvageCategory.SmallScrap);
            int value = item.Value;

            item.RecoverTo(m_Director.locker.zone.position);
            yield return Physics(3);
            m_Director.locker.ScanZone();

            Assert.IsTrue(item.IsSecured, "The locker did not accept released salvage.");
            Assert.AreEqual(value, m_Director.Session.DepositedValue);
            Assert.AreEqual(1, m_Director.Session.DepositedCount);

            // Every way of trying to deposit it again must be refused.
            m_Director.locker.ScanZone();
            Assert.IsFalse(m_Director.locker.TryAccept(item));
            Assert.AreEqual(DepositResult.AlreadyDeposited, m_Director.DepositSalvage(item, PlayerId.Local));
            yield return Frames(5);
            Assert.AreEqual(value, m_Director.Session.DepositedValue);
            Assert.AreEqual(1, m_Director.Session.DepositedCount);
            Assert.IsFalse(item.grab.enabled, "Secured salvage must not be grabbable.");
        }

        [UnityTest]
        public IEnumerator Locker_RefusesSalvage_WhenNoContractIsRunning()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            var item = LooseItems().First();
            m_Director.Extract(PlayerId.Local);
            Assert.IsFalse(m_Director.locker.TryAccept(item));
            Assert.IsFalse(item.IsSecured);
        }

        // ------------------------------------------------------------------ cutting

        [UnityTest]
        public IEnumerator CuttingThePanel_OpensTheCompartment_AndTheLootInsideCanBeRecovered()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);

            var reactorPoint = m_Director.spawnPoints.First(p => p.pointId == "reactor_housing");
            var panel = reactorPoint.behindPanel;
            var core = m_Director.Items.First(i => i.definition.category == SalvageCategory.ReactorComponent);
            Assert.Less(Vector3.Distance(core.transform.position, reactorPoint.transform.position), 0.3f);

            // Sealed: the plate blocks line of sight, so a scanner outside cannot see it.
            Vector3 outside = panel.transform.position + panel.transform.forward * 1.2f;
            Assert.IsTrue(UnityEngine.Physics.Linecast(outside, core.transform.position, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore),
                "The reactor core should be hidden behind its panel.");

            // Burn every weld but one: still closed.
            var points = panel.cutPoints;
            for (int i = 0; i < points.Length - 1; i++) panel.ApplyCut(points[i], 5f, 0.5f, m_Player);
            Assert.IsFalse(panel.IsOpen);
            Assert.AreEqual(points.Length - 1, panel.CutCount);
            panel.ApplyCut(points[points.Length - 1], 5f, 0.5f, m_Player);

            Assert.IsTrue(panel.IsOpen);
            Assert.IsTrue(m_Director.Session.IsPanelOpen(panel.panelId));
            Assert.IsFalse(panel.plate.isKinematic, "The cut plate should float free.");
            yield return Physics(60);
            Assert.IsFalse(UnityEngine.Physics.Linecast(outside + Vector3.up * 0.2f, core.transform.position, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore),
                "With the plate gone the loot should be exposed.");

            Assert.IsTrue(core.grab.enabled);
            Assert.AreEqual(DepositResult.Accepted, m_Director.DepositSalvage(core, PlayerId.Local));
            Assert.AreEqual(core.Value, m_Director.Session.DepositedValue);
        }

        [UnityTest]
        public IEnumerator MountedSalvage_IsLockedInPlace_UntilItsBracketIsCut()
        {
            // Find a seed that puts something on the rack mount.
            SalvageItem mounted = null;
            for (int seed = 1; seed < 40 && mounted == null; seed++)
            {
                MissionDirector.SeedOverride = seed;
                if (m_Director.Phase == MissionPhase.Active) m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
                m_Director.StartMission(PlayerId.Local);
                yield return Frames(2);
                mounted = m_Director.Items.FirstOrDefault(i => i.IsMounted);
            }
            Assert.IsNotNull(mounted, "No run placed salvage on the mount.");
            var mount = m_Director.spawnPoints.First(p => p.pointId == "maint_rack").mount;

            Assert.IsFalse(mounted.grab.enabled, "Mounted salvage must not be grabbable.");
            Assert.IsTrue(mounted.Body.isKinematic);
            Assert.IsFalse(m_Director.locker.TryAccept(mounted), "Mounted salvage cannot be deposited.");

            foreach (var p in mount.cutPoints) mount.ApplyCut(p, 5f, 0.5f, m_Player);
            Assert.IsTrue(mount.IsOpen);
            Assert.IsFalse(mounted.IsMounted);
            Assert.IsTrue(mounted.grab.enabled);
            Assert.IsFalse(mounted.Body.isKinematic);
        }

        [UnityTest]
        public IEnumerator PlasmaCutter_Cuts_WhenItsFlameTouchesAWeld_AndMakesNoise()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            var panel = m_Director.cuttables.First(c => c.panelId == "panel_cage");
            var point = panel.cutPoints[0];
            // The cut-point trigger must be findable by the cutter's overlap query.
            var hits = UnityEngine.Physics.OverlapSphere(point.transform.position, 0.07f, GameLayers.CutPointMask, QueryTriggerInteraction.Collide);
            Assert.IsTrue(hits.Any(h => h.GetComponent<CutPoint>() == point));

            int noiseBefore = m_Director.noise.EmittedCount;
            foreach (var p in panel.cutPoints) panel.ApplyCut(p, 5f, 0.5f, m_Player);
            Assert.IsTrue(panel.IsOpen);
            Assert.Greater(m_Director.noise.EmittedCount, noiseBefore, "Breaking a panel open should be loud.");
        }

        // ------------------------------------------------------------------ enemy

        [UnityTest]
        public IEnumerator Drone_InvestigatesNoise_ThenReturnsToPatrol()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            var drone = m_Director.enemies[0];
            Assert.AreEqual(EnemyState.Dormant, drone.State, "The drone gets a grace period at the start of a run.");
            drone.WakeNow();
            Assert.AreEqual(EnemyState.Patrol, drone.State);
            OpenWreckDoors();

            var noiseAt = new Vector3(0.3f, 1.2f, 25f); // in the spine corridor, well away from the reactor dock but within earshot
            float startDistance = Vector3.Distance(drone.transform.position, noiseAt);
            m_Director.noise.Emit(new NoiseEvent(noiseAt, 1f, NoiseKind.Cutting, PlayerId.Local));
            Assert.AreEqual(EnemyState.Investigate, drone.State);

            bool arrived = false;
            for (int i = 0; i < 2400 && !arrived; i++)
            {
                drone.Step(0.05f);
                Vector3 flat = drone.transform.position - noiseAt;
                flat.y = 0f;
                arrived = flat.magnitude < 1.0f;
                Assert.IsFalse(UnityEngine.Physics.CheckSphere(drone.transform.position, 0.1f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore) && !m_Director.wreckDoors.Any(d => d.IsMoving),
                    "The drone moved inside solid geometry at " + drone.transform.position);
            }
            Assert.IsTrue(arrived, "The drone never reached the noise (started " + startDistance + " m away, ended at " + drone.transform.position + ").");

            for (int i = 0; i < 400 && drone.State != EnemyState.Patrol; i++) drone.Step(0.05f);
            Assert.AreEqual(EnemyState.Patrol, drone.State);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
        }

        [UnityTest]
        public IEnumerator Drone_IgnoresQuietNoise_AndNeverEntersTheTugOrAirlock()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            var drone = m_Director.enemies[0];
            drone.WakeNow();
            OpenWreckDoors();

            // A faint tap at the far end of the ship is not heard.
            m_Director.noise.Emit(new NoiseEvent(new Vector3(0f, 1.2f, 9f), 0.2f, NoiseKind.Impact, PlayerId.Local));
            Assert.AreEqual(EnemyState.Patrol, drone.State);

            // Be as loud as possible right at the airlock door and inside the tug, for five minutes.
            var zones = Object.FindObjectsByType<EnemyExclusionZone>(FindObjectsSortMode.None);
            for (int i = 0; i < 6000; i++)
            {
                if (i % 200 == 0)
                {
                    m_Director.noise.Emit(new NoiseEvent(new Vector3(0f, 1.2f, 6.5f), 1.5f, NoiseKind.Cutting, PlayerId.Local));
                    m_Director.noise.Emit(new NoiseEvent(m_Player.HeadPosition, 1.5f, NoiseKind.Cutting, PlayerId.Local));
                }
                drone.Step(0.05f);
                if (i % 300 == 0) yield return null;
                Assert.IsFalse(m_Director.safeZone.Contains(drone.transform.position), "The drone entered the tug.");
                foreach (var zone in zones)
                    Assert.IsFalse(zone.Contains(drone.transform.position), "The drone entered " + zone.name);
                Assert.AreNotEqual(EnemyState.Chase, drone.State, "A player inside the tug must never be chased.");
            }
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase, "The player was safe in the tug the whole time.");
        }

        [UnityTest]
        public IEnumerator Drone_ChasesAndCatches_APlayerInTheOpen_AndTheRunFailsWithoutLosingBankedCredits()
        {
            m_Director.Profile.Data.totalCredits = 500;
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            var deposit = LooseItems().First();
            m_Director.DepositSalvage(deposit, PlayerId.Local);

            // Stand in the corridor, in plain view.
            var anchor = RecoveryAnchor.All.First(a => a.name.Contains("Corridor North"));
            m_Player.locomotion.TeleportTo(anchor.transform.position);
            yield return Frames(4);
            Assert.IsFalse(m_Player.InSafeZone);

            var drone = m_Director.enemies[0];
            drone.WakeNow();
            OpenWreckDoors();
            bool sawChase = false;
            bool sawScare = false;
            float closest = float.MaxValue;
            for (int i = 0; i < 6000 && m_Director.Phase == MissionPhase.Active && !drone.IsScaring; i++)
            {
                if (i % 100 == 0) m_Director.noise.Emit(new NoiseEvent(m_Player.HeadPosition, 1f, NoiseKind.Impact, PlayerId.Local));
                drone.Step(0.05f);
                sawChase |= drone.State == EnemyState.Chase;
                if (i % 50 == 0) yield return null;
            }

            Assert.IsTrue(sawChase, "The drone never spotted a player standing in the corridor.");

            // The catch plays a short jumpscare before the run ends: the creature lunges to the player's face.
            for (float waited = 0f; waited < 4f && m_Director.Phase == MissionPhase.Active; waited += Time.unscaledDeltaTime)
            {
                if (drone.IsScaring)
                {
                    sawScare = true;
                    closest = Mathf.Min(closest, Vector3.Distance(drone.transform.position, m_Player.HeadPosition));
                }
                yield return null;
            }
            Assert.IsTrue(sawScare, "Being caught should trigger the jumpscare.");
            Assert.Less(closest, 1.0f, "During the jumpscare the creature should be right in front of the player.");
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            Assert.AreEqual(MissionOutcome.FailedCaught, m_Director.Session.Result.Outcome);
            Assert.AreEqual(0, m_Director.Session.Result.Payout);
            Assert.AreEqual(500, m_Director.Profile.Data.totalCredits, "Being caught must not touch banked credits.");
            Assert.AreEqual(EnemyState.Dormant, drone.State);

            // The player is brought home behind a fade.
            yield return new WaitForSecondsRealtime(2.5f);
            Assert.IsTrue(m_Director.safeZone.Contains(m_Player.HeadPosition), "The player should be returned to the tug after failing.");
        }

        [UnityTest]
        public IEnumerator HazardPause_FreezesOxygenAndTheDrone()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            var anchor = RecoveryAnchor.All.First(a => a.name.Contains("Airlock"));
            m_Player.locomotion.TeleportTo(anchor.transform.position);
            yield return Frames(4);
            Assert.IsFalse(m_Player.InSafeZone);

            float before = m_Director.Session.Oxygen.Remaining;
            yield return Frames(20);
            Assert.Less(m_Director.Session.Oxygen.Remaining, before, "Oxygen should drain outside the tug.");

            m_Director.SetHazardPause(HazardPauseReason.Scripted, true);
            float paused = m_Director.Session.Oxygen.Remaining;
            var drone = m_Director.enemies[0];
            drone.WakeNow();
            Vector3 dronePos = drone.transform.position;
            yield return Frames(20);
            Assert.AreEqual(paused, m_Director.Session.Oxygen.Remaining, "Oxygen must not drain while hazards are paused.");
            Assert.AreEqual(dronePos, drone.transform.position, "The drone must not move while hazards are paused.");

            m_Director.SetHazardPause(HazardPauseReason.Scripted, false);
            yield return Frames(10);
            Assert.Less(m_Director.Session.Oxygen.Remaining, paused);
        }

        // ------------------------------------------------------------------ oxygen

        [UnityTest]
        public IEnumerator Oxygen_WarnsAtThresholds_ThenFailsTheRun()
        {
            m_Director.Profile.Data.totalCredits = 240;
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            var tank = m_Director.Session.Oxygen;
            float capacity = tank.Capacity;
            Assert.AreEqual(m_Director.Config.baseOxygenSeconds, capacity, 0.01f);
            m_Director.DepositSalvage(LooseItems().First(), PlayerId.Local);

            Assert.IsNull(m_Director.TickMission(capacity * 0.5f, true), "No drain in the tug.");
            Assert.AreEqual(OxygenWarningLevel.Normal, tank.Level);

            m_Director.TickMission(capacity * 0.72f, false);
            Assert.AreEqual(OxygenWarningLevel.Low, tank.Level);
            m_Director.TickMission(capacity * 0.19f, false);
            Assert.AreEqual(OxygenWarningLevel.Critical, tank.Level);
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);

            var result = m_Director.TickMission(capacity, false);
            Assert.IsNotNull(result);
            Assert.AreEqual(MissionOutcome.FailedOxygen, result.Outcome);
            Assert.AreEqual(0, result.Payout);
            Assert.AreEqual(240, m_Director.Profile.Data.totalCredits);
        }

        // ------------------------------------------------------------------ extraction and payout

        [UnityTest]
        public IEnumerator Extraction_AboveQuota_PaysSalvagePlusBonus_ExactlyOnce()
        {
            m_Director.Profile.Data.totalCredits = 100;
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);

            // Open everything so every item can be deposited, then deposit the most valuable first.
            foreach (var c in m_Director.cuttables)
                foreach (var p in c.cutPoints) c.ApplyCut(p, 5f, 0.5f, m_Player);
            int deposited = 0;
            foreach (var item in m_Director.Items.OrderByDescending(i => i.Value))
            {
                if (deposited >= m_Director.Session.Quota) break;
                Assert.AreEqual(DepositResult.Accepted, m_Director.DepositSalvage(item, PlayerId.Local));
                deposited += item.Value;
            }
            Assert.IsTrue(m_Director.Session.QuotaMet);

            var result = m_Director.Extract(PlayerId.Local);
            Assert.IsNotNull(result, "Extraction should work from inside the tug.");
            Assert.AreEqual(MissionOutcome.ContractComplete, result.Outcome);
            Assert.AreEqual(deposited + m_Director.Config.contractBonusCredits, result.Payout);
            Assert.AreEqual(100 + result.Payout, m_Director.Profile.Data.totalCredits);

            Assert.IsNull(m_Director.Extract(PlayerId.Local));
            Assert.AreEqual(0, m_Director.Session.BankInto(m_Director.Profile));
            Assert.AreEqual(100 + result.Payout, m_Director.Profile.Data.totalCredits, "Payout must be banked exactly once.");
            yield return new WaitForSeconds(1.5f);
            Assert.IsFalse(m_Director.tugHatch.IsOpen, "The hatch seals after extraction.");

            var saved = new ProfileService(m_Storage);
            saved.Load();
            Assert.AreEqual(100 + result.Payout, saved.Data.totalCredits, "Credits must be written to the save.");
        }

        [UnityTest]
        public IEnumerator Extraction_BelowQuota_IsQuotaMissed_AndPaysOnlyWhatWasRecovered()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            var item = LooseItems().OrderBy(i => i.Value).First();
            m_Director.DepositSalvage(item, PlayerId.Local);

            var result = m_Director.Extract(PlayerId.Local);
            Assert.AreEqual(MissionOutcome.QuotaMissed, result.Outcome);
            Assert.AreEqual(item.Value, result.Payout);
            Assert.AreEqual(item.Value, m_Director.Profile.Data.totalCredits);
        }

        [UnityTest]
        public IEnumerator Extraction_IsRefused_WhenThePlayerIsNotInTheTug()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            m_Player.locomotion.TeleportTo(RecoveryAnchor.All.First(a => a.name.Contains("Airlock")).transform.position);
            yield return Frames(4);
            Assert.IsNull(m_Director.Extract(PlayerId.Local));
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
        }

        // ------------------------------------------------------------------ restart and progression

        [UnityTest]
        public IEnumerator Restart_AfterARun_IsCompletelyClean()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            var firstIds = m_Director.Items.Select(i => i.SalvageId).ToArray();
            var panel = m_Director.cuttables.First(c => c.panelId == "panel_reactor");
            foreach (var p in panel.cutPoints) panel.ApplyCut(p, 5f, 0.5f, m_Player);
            var item = LooseItems().First();
            item.RecoverTo(m_Director.locker.zone.position);
            yield return Physics(3);
            m_Director.locker.ScanZone();
            m_Director.wreckDoors[0].SetOpen(true, true);
            m_Director.enemies[0].WakeNow();
            m_Director.TickMission(200f, false);
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);

            // Immediate restart straight from the results screen.
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            yield return Frames(3);

            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.AreEqual(0, m_Director.Session.DepositedValue);
            Assert.AreEqual(0, m_Director.Session.DepositedCount);
            Assert.IsFalse(m_Director.Session.Banked);
            Assert.AreEqual(m_Director.Session.Oxygen.Capacity, m_Director.Session.Oxygen.Remaining, 0.5f);
            Assert.AreEqual(OxygenWarningLevel.Normal, m_Director.Session.Oxygen.Level);

            foreach (var c in m_Director.cuttables)
            {
                Assert.IsFalse(c.IsOpen, c.panelId + " should be welded shut again");
                Assert.AreEqual(0, c.CutCount);
                Assert.IsTrue(c.plate.isKinematic);
                Assert.IsFalse(m_Director.Session.IsPanelOpen(c.panelId));
            }
            foreach (var door in m_Director.wreckDoors) Assert.IsFalse(door.IsOpen, door.doorId + " should be closed again");

            CollectionAssert.IsEmpty(m_Director.Items.Select(i => i.SalvageId).Intersect(firstIds), "Old salvage ids must not be reused.");
            Assert.IsTrue(m_Director.Items.All(i => i != null && !i.IsSecured));
            Assert.AreEqual(m_Director.Items.Count, Object.FindObjectsByType<SalvageItem>(FindObjectsSortMode.None).Length,
                "Salvage from the previous run should be gone from the scene, including from the locker.");
            Assert.AreEqual(EnemyState.Dormant, m_Director.enemies[0].State);
            Assert.Less(Vector3.Distance(m_Director.enemies[0].transform.position, m_Director.enemies[0].dock.position), 0.05f);
        }

        [UnityTest]
        public IEnumerator OxygenUpgrade_IsBought_Persists_AndGivesMoreOxygenNextRun()
        {
            var config = m_Director.Config;
            Assert.IsFalse(m_Director.PurchaseOxygenUpgrade(), "Cannot buy with no credits.");

            m_Director.Profile.Data.totalCredits = config.oxygenUpgradeCost + 25;
            Assert.IsTrue(m_Director.PurchaseOxygenUpgrade());
            Assert.AreEqual(25, m_Director.Profile.Data.totalCredits);
            Assert.AreEqual(1, m_Director.Profile.Data.oxygenUpgradeLevel);
            Assert.IsFalse(m_Director.PurchaseOxygenUpgrade(), "Only one level exists.");

            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            Assert.AreEqual(config.baseOxygenSeconds + config.oxygenUpgradeBonusSeconds, m_Director.Session.Oxygen.Capacity, 0.01f);
            Assert.IsFalse(m_Director.PurchaseOxygenUpgrade(), "The shop is closed during a contract.");

            // A brand new session reading the same save still has the upgrade.
            var later = new ProfileService(m_Storage, config.maxOxygenUpgradeLevel);
            later.Load();
            Assert.AreEqual(1, later.Data.oxygenUpgradeLevel);
            Assert.AreEqual(25, later.Data.totalCredits);
        }

        // ------------------------------------------------------------------ tools

        [UnityTest]
        public IEnumerator Tools_RecallToTheirHolsters_AndTheScannerOnlySeesVisibleLoot()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(3);
            var scanner = m_Player.tools.OfType<ScannerTool>().First();
            var flashlight = m_Player.tools.OfType<FlashlightTool>().First();

            // A ping never reports more than the loot that exists, and then needs to recharge.
            Assert.LessOrEqual(scanner.Ping(), m_Director.Items.Count);
            Assert.AreEqual(0, scanner.Ping(), "The scanner has a cooldown between pings.");

            // Lose a tool far away: recall brings it straight back.
            flashlight.transform.SetParent(null);
            flashlight.transform.position = new Vector3(0f, 1.2f, 25f);
            m_Player.RecallTools();
            yield return Frames(2);
            Assert.IsTrue(flashlight.IsDocked);
            Assert.Less(Vector3.Distance(flashlight.transform.position, flashlight.holster.SocketPosition), 0.05f);
            Assert.IsTrue(flashlight.IsOn || !flashlight.startsOn);
        }
    }
}

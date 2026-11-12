using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>The three wrecks (one docked per contract) and the creature hearing the player's voice.</summary>
    public class MapsAndVoicePlayTests
    {
        MissionDirector m_Director;
        PlayerContext m_Player;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            MissionDirector.StorageOverride = new MemorySaveStorage();
            MissionDirector.SeedOverride = 12;
            MissionDirector.CreatureOverride = 0;
            MissionDirector.MapOverride = null;
            MissionDirector.ConditionOverride = ContractCondition.Standard;
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

        IEnumerator Dock(int map, int seed = 5)
        {
            MissionDirector.MapOverride = map;
            MissionDirector.SeedOverride = seed;
            if (m_Director.Phase == MissionPhase.Active) m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            yield return Frames(2);
            yield return new WaitForFixedUpdate();
            Physics.SyncTransforms();
        }

        [UnityTest]
        public IEnumerator EachContract_DocksOneWreck_ChosenAtRandom_AndCleansUpTheLast()
        {
            Assert.AreEqual(3, m_Director.maps.Length);
            Assert.AreSame(m_Director.maps[0], m_Director.ActiveMap, "The first wreck is docked while in the hub.");

            var seen = new HashSet<string>();
            for (int seed = 1; seed <= 24; seed++)
            {
                MissionDirector.SeedOverride = seed;
                if (m_Director.Phase == MissionPhase.Active) m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
                Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
                yield return null;
                var map = m_Director.ActiveMap;
                seen.Add(map.mapId);
                Assert.AreEqual(1, m_Director.maps.Count(m => m.gameObject.activeSelf), "Exactly one wreck is docked.");
                Assert.IsTrue(map.gameObject.activeSelf);
                Assert.AreSame(map.dock, m_Director.ActiveEnemy.dock);
                Assert.Less(Vector3.Distance(m_Director.ActiveEnemy.transform.position, map.dock.position), 0.1f, "The creature starts at this wreck's dock.");
                Assert.IsTrue(map.spawnPoints.SequenceEqual(m_Director.spawnPoints));
                Assert.IsTrue(map.cuttables.All(c => m_Director.cuttables.Contains(c)));
                Assert.IsTrue(m_Director.sharedCuttables.All(c => m_Director.cuttables.Contains(c)), "The practice plate is always there.");
                Assert.IsTrue(m_Director.wreckDoors.Contains(m_Director.sharedDoors[0]) && map.doors.All(d => m_Director.wreckDoors.Contains(d)));
                Assert.Greater(m_Director.Session.TotalRegisteredValue, m_Director.Session.Quota);
                foreach (var item in m_Director.Items)
                {
                    Assert.IsTrue(m_Director.bounds.Contains(item.transform.position), item.name + " outside the level in " + map.displayName);
                    if (item.definition != null && !item.definition.bonusSpawn)
                        Assert.IsTrue(map.spawnPoints.Any(p => Vector3.Distance(p.transform.position, item.transform.position) < 0.6f), item.name + " did not spawn at one of this wreck's spots");
                }
                Assert.AreEqual(1, Object.FindObjectsByType<WreckMap>(FindObjectsSortMode.None).Length);
            }
            Assert.AreEqual(3, seen.Count, "Over 24 contracts every wreck should come up: " + string.Join(", ", seen));
        }

        [UnityTest]
        public IEnumerator EveryWreck_CanBePlayedByTeleport_AndItsLootIsWithinReach()
        {
            for (int index = 0; index < 3; index++)
            {
                yield return Dock(index);
                var map = m_Director.ActiveMap;
                var landing = new List<Vector3>();
                foreach (var surface in Object.FindObjectsByType<TeleportSurface>(FindObjectsSortMode.None))
                {
                    Bounds b = surface.GetComponent<BoxCollider>().bounds;
                    for (float x = b.min.x; x <= b.max.x + 0.001f; x += 0.2f)
                    {
                        for (float z = b.min.z; z <= b.max.z + 0.001f; z += 0.2f)
                        {
                            var p = new Vector3(x, 0f, z);
                            if (!Physics.CheckCapsule(p + Vector3.up * 0.34f, p + Vector3.up * 1.28f, 0.22f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore))
                                landing.Add(p);
                        }
                    }
                }
                Assert.Greater(landing.Count, 400, map.displayName);

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

                foreach (var point in map.spawnPoints)
                {
                    Assert.Less(Nearest(point.transform.position), 0.95f, map.displayName + ": cannot stand within arm's reach of " + point.pointId);
                    Assert.That(point.transform.position.y, Is.InRange(0.8f, 1.6f), point.pointId);
                    if (point.Gate == null)
                        Assert.IsFalse(Physics.CheckSphere(point.transform.position, 0.12f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore),
                            map.displayName + ": " + point.pointId + " is inside something solid.");
                }
                foreach (var anchor in RecoveryAnchor.All)
                    Assert.Less(Nearest(anchor.transform.position), 0.25f, map.displayName + ": " + anchor.name + " is not on a teleport surface");
                foreach (var cuttable in map.cuttables)
                    Assert.Less(Nearest(cuttable.transform.position), 1.3f, map.displayName + ": cannot get to " + cuttable.panelId);
            }
        }

        [UnityTest]
        public IEnumerator EveryWreck_TheCreaturePatrolsItsWholeRoute_WithoutPassingThroughWalls_OrEnteringNoGoZones()
        {
            for (int index = 0; index < 3; index++)
            {
                yield return Dock(index);
                var map = m_Director.ActiveMap;
                var creature = m_Director.ActiveEnemy;
                creature.WakeNow();
                foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
                Physics.SyncTransforms();
                var zones = Object.FindObjectsByType<EnemyExclusionZone>(FindObjectsSortMode.None);
                Assert.GreaterOrEqual(zones.Length, 2, map.displayName);

                var visited = new HashSet<EnemyWaypoint>();
                for (int i = 0; i < 12000 && visited.Count < map.patrolRoute.Distinct().Count(); i++)
                {
                    creature.Step(0.05f);
                    Vector3 p = creature.transform.position;
                    foreach (var node in map.patrolRoute)
                    {
                        Vector3 flat = node.transform.position - p;
                        flat.y = 0f;
                        if (flat.magnitude < 0.5f) visited.Add(node);
                    }
                    Assert.IsFalse(Physics.CheckSphere(p, 0.1f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore),
                        map.displayName + ": the creature moved inside solid geometry at " + p);
                    Assert.IsFalse(m_Director.safeZone.Contains(p), map.displayName + ": the creature entered the tug.");
                    foreach (var zone in zones) Assert.IsFalse(zone.Contains(p), map.displayName + ": the creature entered " + zone.name);
                    Assert.IsTrue(m_Director.bounds.Contains(p));
                    if (i % 400 == 0) yield return null;
                }
                Assert.AreEqual(map.patrolRoute.Distinct().Count(), visited.Count, map.displayName + ": the creature never reached "
                    + string.Join(", ", map.patrolRoute.Distinct().Where(n => !visited.Contains(n)).Select(n => n.name)));
            }
        }

        [UnityTest]
        public IEnumerator EveryWreck_BlacksOutItsOwnLights_AndItsPanelsOpen()
        {
            for (int index = 0; index < 3; index++)
            {
                yield return Dock(index);
                var map = m_Director.ActiveMap;
                var blackout = m_Director.blackout;
                Assert.IsTrue(map.lights.All(l => l.enabled), map.displayName);
                blackout.TriggerNow(4f);
                Assert.IsTrue(map.lights.All(l => !l.enabled), map.displayName + ": every wreck light should go out.");
                blackout.EndBlackout(true);
                Assert.IsTrue(map.lights.All(l => l.enabled));

                var gated = map.spawnPoints.First(p => p.behindPanel != null);
                Assert.IsTrue(m_Director.RequestOpenPanel(gated.behindPanel, PlayerId.Local), map.displayName + ": could not open " + gated.behindPanel.panelId);
                yield return Frames(2);
                Assert.IsTrue(m_Director.Session.IsPanelOpen(gated.behindPanel.panelId));
            }
        }

        [UnityTest]
        public IEnumerator TheCreature_HearsYouTalk_ButNotAWhisper_AndNotWithTheMicOff()
        {
            yield return Dock(0);
            var voice = m_Player.voice;
            Assert.IsNotNull(voice, "The suit should have a voice sensor.");
            Assert.IsTrue(m_Director.Profile.Data.voiceDetection, "Voice detection is on by default.");
            var creature = m_Director.ActiveEnemy;

            // Stand in the corridor outside the creature's room (its door is shut) and stay quiet.
            // The creature is still dormant, so this part only checks what the microphone reports.
            Vector3 corridor = TrashAstronauts.Tests.MapPoints.Freighter(0f, 0f, 20.9f);
            m_Player.locomotion.TeleportTo(corridor);
            yield return Frames(3);
            float quiet = Mathf.Pow(10f, -60f / 20f), whisper = Mathf.Pow(10f, -50f / 20f), talking = Mathf.Pow(10f, -20f / 20f);
            voice.LevelOverride = quiet;
            for (float t = 0f; t < 1f; t += Time.unscaledDeltaTime) yield return null;
            Assert.IsTrue(voice.Listening);
            Assert.AreEqual(0, voice.EmitCount);

            voice.LevelOverride = whisper;
            for (float t = 0f; t < 1f; t += Time.unscaledDeltaTime) yield return null;
            Assert.AreEqual(0, voice.EmitCount, "A whisper should be safe.");

            // Switch the microphone off in the suit menu: talking does nothing.
            m_Director.Profile.Data.voiceDetection = false;
            m_Director.ApplyAndSaveSettings();
            voice.LevelOverride = talking;
            for (float t = 0f; t < 0.8f; t += Time.unscaledDeltaTime) yield return null;
            Assert.AreEqual(0, voice.EmitCount);
            Assert.IsFalse(voice.Listening);

            // Back on: now it hears.
            m_Director.Profile.Data.voiceDetection = true;
            m_Director.ApplyAndSaveSettings();
            voice.LevelOverride = quiet;
            for (float t = 0f; t < 0.5f; t += Time.unscaledDeltaTime) yield return null;
            creature.WakeNow();
            Assert.AreEqual(EnemyState.Patrol, creature.State);
            Assert.Less(Vector3.Distance(creature.transform.position, m_Player.HeadPosition), 13.5f, "Test set-up: the player should be within earshot.");
            voice.LevelOverride = talking;
            for (float t = 0f; t < 1f && voice.EmitCount == 0; t += Time.unscaledDeltaTime) yield return null;
            Assert.Greater(voice.EmitCount, 0, "Talking should raise a noise.");
            Assert.IsTrue(voice.HeardRecently);
            Assert.That(creature.State, Is.EqualTo(EnemyState.Investigate).Or.EqualTo(EnemyState.Chase), "Talking near the creature should bring it over.");

            // In the tug's hub (no contract) the microphone raises nothing.
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            yield return Frames(2);
            int before = voice.EmitCount;
            for (float t = 0f; t < 0.8f; t += Time.unscaledDeltaTime) yield return null;
            Assert.AreEqual(before, voice.EmitCount);
        }
    }
}

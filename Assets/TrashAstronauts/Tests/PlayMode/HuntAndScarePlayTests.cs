using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.UI;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>Spawn safety, real pursuit, the wrist noise meter, breakable valuables and staged scares.</summary>
    public class HuntAndScarePlayTests
    {
        MissionDirector m_Director;
        PlayerContext m_Player;

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
            MissionDirector.StorageOverride = null;
            MissionDirector.SeedOverride = null;
            MissionDirector.CreatureOverride = null;
            MissionDirector.MapOverride = null;
            MissionDirector.ConditionOverride = null;
            BlackoutController.DamageOverride = null;
        }

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        bool HeadInTug()
        {
            Vector3 head = m_Player.HeadPosition;
            head.y = m_Director.safeZone.transform.position.y;
            return m_Director.safeZone.Contains(head);
        }

        [UnityTest]
        public IEnumerator Spawn_PutsTheHeadInTheLobby_EvenWhenThePlayerStandsFarFromTheirRoomCentre()
        {
            // Someone standing 3 m from the middle of their play space: without correction that is outside the tug.
            m_Player.head.localPosition = new Vector3(3f, 1.6f, 0.5f);
            for (float t = 0f; t < 1.5f; t += Time.unscaledDeltaTime) yield return null;
            Assert.IsTrue(HeadInTug(), "The player's head should start inside the lobby. Head at " + m_Player.HeadPosition);
            Assert.Less(Vector2.Distance(new Vector2(m_Player.HeadPosition.x, m_Player.HeadPosition.z),
                new Vector2(m_Director.hubSpawn.position.x, m_Director.hubSpawn.position.z)), 0.3f, "They should be standing on the spawn point.");

            // If they end up outside before a contract starts (walked through a wall, lost tracking), bring them back.
            m_Player.transform.position += new Vector3(6f, 0f, 0f);
            yield return Frames(2);
            Assert.IsFalse(HeadInTug());
            for (float t = 0f; t < 2f && !HeadInTug(); t += Time.unscaledDeltaTime) yield return null;
            Assert.IsTrue(HeadInTug(), "A player outside the lobby with no contract running should be put back.");
            Assert.AreEqual(1, m_Director.HubRescueCount);

            // During a contract they are free to leave.
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 12f));
            for (float t = 0f; t < 1.5f; t += Time.unscaledDeltaTime) yield return null;
            Assert.IsFalse(HeadInTug());
            Assert.AreEqual(1, m_Director.HubRescueCount);
        }

        [UnityTest]
        public IEnumerator TheCreature_LeavesItsRoute_ToRunDownAPlayerInTheMiddleOfABigRoom()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            // Deep in the cargo bay, as far from any of its route points as the room allows.
            Vector3 spot = Vector3.zero;
            float bestGap = 0f;
            var map = m_Director.ActiveMap;
            for (float x = -14f; x <= -3f; x += 0.5f)
            {
                for (float z = 12f; z <= 23f; z += 0.5f)
                {
                    var p = new Vector3(x, 0f, z);
                    if (Physics.CheckCapsule(p + Vector3.up * 0.7f, p + Vector3.up * 1.7f, 0.45f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore)) continue;
                    float gap = map.graph.Min(n => Vector2.Distance(new Vector2(n.Position.x, n.Position.z), new Vector2(x, z)));
                    if (gap <= bestGap) continue;
                    // In the open: visible from the nearest route point (behind a container is a hiding place, and hiding is meant to work).
                    var nearest = map.graph.OrderBy(n => Vector2.Distance(new Vector2(n.Position.x, n.Position.z), new Vector2(x, z))).First();
                    if (Physics.Linecast(new Vector3(nearest.Position.x, 1.45f, nearest.Position.z), p + Vector3.up * 1.6f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore)) continue;
                    bestGap = gap;
                    spot = p;
                }
            }
            Assert.Greater(bestGap, 2.5f, "Test set-up: expected open floor well away from the route points.");
            m_Player.locomotion.TeleportTo(spot);
            yield return Frames(4);
            Assert.IsFalse(m_Player.InSafeZone);

            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            Physics.SyncTransforms();

            bool sawChase = false, wentDirect = false;
            float closest = float.MaxValue;
            for (int i = 0; i < 4000 && m_Director.Phase == MissionPhase.Active && !creature.IsScaring; i++)
            {
                if (i % 60 == 0) m_Director.noise.Emit(new NoiseEvent(m_Player.HeadPosition, 1f, NoiseKind.Impact, PlayerId.Local));
                creature.Step(0.05f);
                sawChase |= creature.State == EnemyState.Chase;
                wentDirect |= creature.IsPursuingDirectly;
                Vector3 flat = creature.transform.position - m_Player.HeadPosition;
                flat.y = 0f;
                closest = Mathf.Min(closest, flat.magnitude);
                // (Once the catch begins it is repositioned for the jumpscare, unseen in the dark; that is not flying.)
                if (!creature.IsScaring)
                    Assert.IsFalse(Physics.CheckSphere(creature.transform.position, 0.1f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore),
                        "The creature flew into solid geometry at " + creature.transform.position);
                if (i % 50 == 0) yield return null;
            }
            Assert.IsTrue(sawChase, "It never started chasing. Closest it got: " + closest.ToString("0.0") + " m; gap from route " + bestGap.ToString("0.0") + " m.");
            Assert.IsTrue(wentDirect, "With a clear run at the player it should leave its route and go straight for them.");
            Assert.IsTrue(creature.IsScaring || m_Director.Phase != MissionPhase.Active,
                "It should catch a player who just stands there. Closest it got: " + closest.ToString("0.0") + " m.");
        }

        [UnityTest]
        public IEnumerator TheCreature_IsFasterThanADriftingPlayer_AndRisesToMeetOneFloatingHigh()
        {
            var creature = m_Director.enemies[0];
            float chase = m_Director.Config.enemy.chaseSpeed;
            Assert.Greater(chase, m_Player.locomotion.zeroG.thrusterMaxSpeed, "It must outrun the wrist thruster.");
            Assert.Less(chase, m_Player.locomotion.zeroG.maxSpeed, "A hard swim or a push-off should still get away.");

            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            creature = m_Director.ActiveEnemy;
            // Float near the ceiling of the corridor, in plain view, a few metres from it.
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            Physics.SyncTransforms();
            Vector3 corridor = MapPoints.Freighter(0f, 0f, 19.6f);
            m_Player.locomotion.TeleportTo(corridor + Vector3.up * 0.75f);
            yield return Frames(4);
            creature.transform.position = new Vector3(corridor.x, creature.hoverHeight, corridor.z - 6f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.forward);
            float startHeight = creature.transform.position.y;
            float highest = startHeight;
            for (int i = 0; i < 80 && !creature.IsScaring && m_Director.Phase == MissionPhase.Active; i++)
            {
                creature.Step(0.05f);
                highest = Mathf.Max(highest, creature.transform.position.y);
            }
            Assert.Greater(highest, startHeight + 0.3f, "It should rise toward a player floating above it.");
        }

        [UnityTest]
        public IEnumerator WristNoiseMeter_ShowsYourVoice_AndTheLoudThingsYouDo_ThenFades()
        {
            var wrist = m_Player.GetComponentInChildren<WristDisplay>(true);
            Assert.IsNotNull(wrist.noiseBar, "The wrist display should have a noise bar.");
            Assert.IsNotNull(wrist.noiseBarRenderer);
            m_Director.StartMission(PlayerId.Local);
            m_Player.voice.LevelOverride = 0.0005f;
            yield return Frames(5);
            Assert.Less(wrist.NoiseLevel, 0.05f);
            Assert.Less(wrist.noiseBar.localScale.x, 0.06f);
            StringAssert.Contains("quiet", wrist.voiceText.text);

            // Something this player did: a hard impact.
            m_Director.noise.Emit(new NoiseEvent(m_Player.HeadPosition, 0.8f, NoiseKind.Impact, m_Player.Id));
            yield return Frames(2);
            Assert.Greater(wrist.NoiseLevel, 0.7f);
            Assert.AreEqual("IMPACT", wrist.NoiseLabel);
            Assert.Greater(wrist.noiseBar.localScale.x, 0.7f);

            // Somebody else's noise (the creature opening a door) is not shown.
            float before = wrist.NoiseLevel;
            m_Director.noise.Emit(new NoiseEvent(m_Player.HeadPosition, 1f, NoiseKind.Door, PlayerId.None));
            yield return Frames(2);
            Assert.LessOrEqual(wrist.NoiseLevel, before);
            Assert.AreEqual("IMPACT", wrist.NoiseLabel);

            for (float t = 0f; t < 2.6f; t += Time.unscaledDeltaTime) yield return null;
            Assert.Less(wrist.NoiseLevel, 0.1f, "The meter should fade once you are quiet again.");

            // Talking moves the bar and names the cause.
            m_Player.voice.LevelOverride = 0.06f;
            for (float t = 0f; t < 0.6f; t += Time.unscaledDeltaTime) yield return null;
            Assert.Greater(wrist.NoiseLevel, 0.4f);
            Assert.AreEqual("VOICE", wrist.NoiseLabel);
        }

        [UnityTest]
        public IEnumerator DelicateSalvage_LosesValueWhenBanged_ScrapDoesNot_AndTheLockerPaysWhatIsLeft()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            var delicate = m_Director.Items.First(i => !i.IsMounted && i.definition != null && i.definition.fragility > 0.4f);
            var scrap = m_Director.Items.First(i => !i.IsMounted && i.definition != null && i.definition.category == SalvageCategory.SmallScrap);
            Assert.AreEqual(0f, scrap.definition.fragility);
            int full = delicate.FullValue;
            int registered = m_Director.Session.TotalRegisteredValue;

            Assert.AreEqual(0, delicate.ApplyImpact(1.2f), "A gentle bump is free.");
            Assert.AreEqual(full, delicate.Value);
            int lost = delicate.ApplyImpact(5f);
            Assert.Greater(lost, 0, "A hard knock costs value.");
            Assert.AreEqual(full - lost, delicate.Value);
            Assert.AreEqual(registered - lost, m_Director.Session.TotalRegisteredValue, "The contract's books follow the damage.");
            Assert.AreEqual(0, scrap.ApplyImpact(9f), "Scrap cannot be damaged.");

            for (int i = 0; i < 30; i++) delicate.ApplyImpact(9f);
            Assert.AreEqual(Mathf.CeilToInt(full / 3f), delicate.Value, "It never drops below a third of its value.");

            Assert.IsTrue(m_Director.locker.TryAccept(delicate));
            Assert.AreEqual(delicate.Value, m_Director.Session.DepositedValue);
            Assert.AreEqual(0, delicate.ApplyImpact(9f), "Safe in the locker.");
        }

        [UnityTest]
        public IEnumerator Jumpscare_KillsTheLights_ThenPutsItInYourFace_ThenRestoresEverything()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 19.6f));
            yield return Frames(4);
            var creature = m_Director.ActiveEnemy;
            creature.WakeNow();
            foreach (var door in m_Director.wreckDoors) door.SetOpen(true, true);
            Physics.SyncTransforms();
            // Put it right in front of the player, looking at them.
            Vector3 head = m_Player.HeadPosition;
            creature.transform.position = new Vector3(head.x, creature.hoverHeight, head.z + 1.6f);
            creature.transform.rotation = Quaternion.LookRotation(Vector3.back);
            for (int i = 0; i < 200 && !creature.IsScaring && m_Director.Phase == MissionPhase.Active; i++) creature.Step(0.05f);
            Assert.IsTrue(creature.IsScaring, "It should have caught a player standing right in front of it.");

            bool sawDark = false, sawFace = false, lightsWentOut = false, sawAfter = false;
            int mostCracks = 0;
            float brightestFlash = 0f;
            var visor = m_Player.visor;
            Assert.IsNotNull(visor, "The helmet should have a visor that can crack.");
            Assert.AreEqual(0, visor.Stage);
            float closest = float.MaxValue, darkSeconds = 0f, faceSeconds = 0f;
            var lights = m_Director.ActiveMap.lights;
            for (float waited = 0f; waited < 9f && m_Director.Phase == MissionPhase.Active; waited += Time.unscaledDeltaTime)
            {
                mostCracks = Mathf.Max(mostCracks, visor.Stage);
                brightestFlash = Mathf.Max(brightestFlash, visor.FlashLevel);
                sawAfter |= creature.ScarePhase == 3;
                if (creature.ScarePhase == 1)
                {
                    sawDark = true;
                    darkSeconds += Time.unscaledDeltaTime;
                    lightsWentOut |= lights.All(l => !l.enabled);
                    Assert.Greater(Vector3.Distance(creature.transform.position, m_Player.HeadPosition), 1.0f, "It should hold back during the dark and the glimpse.");
                }
                else if (creature.ScarePhase == 2)
                {
                    Assert.IsTrue(sawDark, "The dark comes first.");
                    sawFace = true;
                    faceSeconds += Time.unscaledDeltaTime;
                    closest = Mathf.Min(closest, Vector3.Distance(creature.transform.position, m_Player.HeadPosition));
                    Assert.IsTrue(lights.All(l => !l.enabled), "The only light during the strike is its eyes.");
                    Vector3 toCreature = creature.transform.position - m_Player.HeadPosition;
                    if (faceSeconds > 0.3f) Assert.Less(Vector3.Angle(m_Player.head.forward, toCreature), 25f, "It should be squarely in the player's view.");
                }
                yield return null;
            }
            Assert.IsTrue(sawDark && sawFace);
            Assert.IsTrue(lightsWentOut, "Every wreck light should go out before the strike.");
            Assert.Greater(darkSeconds, 0.3f);
            Assert.Greater(faceSeconds, 1.2f, "It should stay in the player's face long enough to register.");
            Assert.Less(closest, 0.4f, "It should be on the visor: within 40 cm of the player's eyes.");
            Assert.GreaterOrEqual(mostCracks, 3, "The visor should crack further with each bite.");
            Assert.GreaterOrEqual(creature.ScareBites, 3, "It should bite several times.");
            Assert.Greater(brightestFlash, 0.4f, "Each bite should flash the view red.");
            Assert.IsTrue(sawAfter, "It should end in blackness before the run is over.");
            Assert.AreEqual(0, visor.Stage, "The visor is whole again for the next contract.");
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            Assert.AreEqual(0, creature.ScarePhase);
            Assert.IsFalse(m_Director.blackout.IsBlackout, "The lights must be restored afterwards.");
        }

        [UnityTest]
        public IEnumerator Scares_CanAllBeStaged_AndTheGlimpseVanishesWhenLookedAt()
        {
            var scares = m_Director.GetComponent<ScareDirector>();
            Assert.IsNotNull(scares);
            Assert.IsNotNull(scares.apparition);
            Assert.IsFalse(scares.GlimpseVisible);
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            // Stand at the near end of the long corridor looking down it.
            m_Player.locomotion.TeleportTo(MapPoints.Freighter(0f, 0f, 8.6f), Quaternion.identity);
            yield return Frames(4);

            foreach (ScareKind kind in new[] { ScareKind.DistantBang, ScareKind.HullGroan, ScareKind.Scuttle, ScareKind.BreathBehind, ScareKind.LightsFlicker })
                Assert.IsTrue(scares.Trigger(kind), kind.ToString());
            Assert.AreEqual(5, scares.EventCount);

            // The lights come back after a flicker.
            for (float t = 0f; t < 2.5f; t += Time.deltaTime) yield return null;
            Assert.IsTrue(m_Director.ActiveMap.lights.All(l => l.enabled), "Lights must be restored after a flicker.");

            Assert.IsTrue(scares.Trigger(ScareKind.Glimpse), "There should be somewhere down the corridor for a shape to stand.");
            Assert.IsTrue(scares.GlimpseVisible);
            float distance = Vector3.Distance(scares.apparition.transform.position, m_Player.HeadPosition);
            Assert.That(distance, Is.InRange(6f, 18f));
            Assert.IsFalse(Physics.Linecast(m_Player.HeadPosition, scares.apparition.transform.position + Vector3.up * 1.5f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore), "It should be in plain view.");

            // Look straight at it: it is gone within a moment.
            Vector3 to = scares.apparition.transform.position + Vector3.up * 1.5f - m_Player.HeadPosition;
            m_Player.head.rotation = Quaternion.LookRotation(to);
            for (float t = 0f; t < 1.5f && scares.GlimpseVisible; t += Time.deltaTime) yield return null;
            Assert.IsFalse(scares.GlimpseVisible, "It should vanish when looked at directly.");

            // The fake-out: it stands there, then runs at the player and is gone before it reaches them.
            m_Player.head.localRotation = Quaternion.identity;
            yield return Frames(2);
            Assert.IsTrue(scares.Trigger(ScareKind.Charge));
            Assert.IsTrue(scares.GlimpseVisible);
            float nearest = float.MaxValue;
            for (float t = 0f; t < 4f && scares.GlimpseVisible; t += Time.deltaTime)
            {
                nearest = Mathf.Min(nearest, Vector3.Distance(scares.apparition.transform.position, new Vector3(m_Player.HeadPosition.x, 0f, m_Player.HeadPosition.z)));
                yield return null;
            }
            Assert.IsFalse(scares.GlimpseVisible, "The charge should end with it gone.");
            Assert.That(nearest, Is.InRange(1.0f, 2.5f), "It should get close, but never reach the player.");
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase, "The fake-out cannot hurt the player.");

            // Nothing is staged in the hub.
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            yield return Frames(3);
            Assert.IsFalse(scares.GlimpseVisible);
        }
    }
}

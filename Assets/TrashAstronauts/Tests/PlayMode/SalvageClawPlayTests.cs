using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// The SALVAGE CLAW lobby mini-game in the real scene: where it stands, that the controls move the claw
    /// toward cargo, that a closed claw grips and the intake pays once into the save, misses pay nothing, the
    /// per-shift cap and its reset when a contract ends, the seeded drift, the clean-up when nobody operates it,
    /// that it is locked during a contract, and that none of it touches the network.
    /// The station is stepped by hand (runInUpdate off) so every test is deterministic.
    /// </summary>
    public class SalvageClawPlayTests : NetPlayFixture
    {
        const float Dt = 0.02f;
        SalvageClawStation m_Station;

        SalvageClawStation Station()
        {
            if (m_Station != null) return m_Station;
            m_Station = Object.FindAnyObjectByType<SalvageClawStation>();
            Assert.IsNotNull(m_Station, "The tug has a SALVAGE CLAW station.");
            m_Station.runInUpdate = false;
            m_Station.OperatorPresentOverride = true;
            m_Station.ClearCargo();
            return m_Station;
        }

        void Run(float seconds)
        {
            int steps = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < steps; i++) m_Station.Step(Dt);
        }

        /// <summary>Flies the claw tip toward a station-space point with the stick and EXTEND/RETRACT, as a player would.</summary>
        float DriveTo(Vector3 target, float maxSeconds, float stopAt = 0.35f)
        {
            var s = m_Station;
            int steps = Mathf.CeilToInt(maxSeconds / Dt);
            for (int i = 0; i < steps; i++)
            {
                Vector3 tip = s.TipLocal;
                if (Vector3.Distance(tip, target) < stopAt) break;
                Vector3 now = s.PoseFor(tip), want = s.PoseFor(target);
                float lateral = Mathf.DeltaAngle(now.x, want.x) * Mathf.Deg2Rad * now.y;
                float vertical = want.z - now.z;
                float reach = want.y - now.y;
                // Pull back raises the arm, push forward lowers it.
                s.StickOverride = new Vector2(Mathf.Clamp(lateral * 1.5f, -1f, 1f), Mathf.Clamp(-vertical * 1.5f, -1f, 1f));
                s.ReachOverride = Mathf.Clamp(reach * 1.5f, -1f, 1f);
                s.Step(Dt);
            }
            s.StickOverride = Vector2.zero;
            s.ReachOverride = 0f;
            Run(0.6f); // let the heavy arm settle
            s.StickOverride = null;
            s.ReachOverride = null;
            return Vector3.Distance(s.TipLocal, target);
        }

        /// <summary>Puts a thing right at the claw, closes on it and delivers it to the hatch. Returns what it paid.</summary>
        int CatchAndDeliver(ClawCargoKind kind)
        {
            var s = m_Station;
            s.SetClaw(false);
            s.SetPose(0f, 4.5f, 1.5f); // away from the hatch
            Run(0.5f);
            var cargo = s.SpawnAt(kind, s.TipLocal, Vector3.zero);
            s.SetClaw(true);
            Run(0.4f);
            Assert.AreSame(cargo, s.Held, "the claw closed on the " + kind);
            float left = DriveTo(s.IntakeLocal, 20f, 0.3f);
            Assert.IsNull(s.Held, "delivered to the hatch (ended " + left.ToString("0.00") + " m away)");
            Run(1.2f); // the hatch swallows it
            Assert.IsFalse(cargo.gameObject.activeSelf, "swallowed");
            return s.LastPay;
        }

        static string Widest(GameObject go)
        {
            var r = go.GetComponentsInChildren<Renderer>(true).OrderByDescending(x => x.bounds.size.magnitude).FirstOrDefault();
            return r == null ? "" : r.name + " " + r.bounds;
        }

        static Bounds Bounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            var b = renderers.Length > 0 ? renderers[0].bounds : new Bounds(go.transform.position, Vector3.zero);
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            return b;
        }

        // ------------------------------------------------------------------ the station in the tug

        [UnityTest]
        public IEnumerator Station_IsInTheTug_ClearOfSpawnsDoorwayAndPanels_AndTheWindowKeepsEveryoneIn()
        {
            var s = Station();
            yield return null;
            Vector3 stand = s.standPoint.position;
            Vector3 eyes = stand + Vector3.up * 1.6f;
            Assert.IsTrue(m_Director.safeZone.Contains(eyes), "the operator stands inside the tug's safe area");
            for (int slot = 0; slot < NetProtocol.MaxPlayers; slot++)
                Assert.Greater(Vector3.Distance(m_Director.CrewSpot(slot), stand), 2f, "nobody spawns at the claw station");

            var console = s.transform.Find("Claw Console").gameObject;
            var bounds = Bounds(console);
            bounds.Expand(-0.01f);
            foreach (string name in new[] { "Company Store", "Lockers", "Co-op Crew Panel", "Mission Board", "Tutorial Board", "Suit Settings Console", "Collection Locker", "Crew Record", "Suit Locker" })
            {
                foreach (var go in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Where(t => t.name == name).Select(t => t.gameObject))
                    Assert.IsFalse(bounds.Intersects(Bounds(go)), "The claw console " + bounds + " overlaps " + name + " " + Bounds(go) + " " + Widest(go));
            }
            var doorway = new Bounds(new Vector3(-2f, 1.05f, -4.8f), new Vector3(1.4f, 2.1f, 1.6f));
            Assert.IsFalse(bounds.Intersects(doorway), "clear of the doorway to the flight deck");
            Assert.IsFalse(Bounds(s.transform.Find("Window Frame").gameObject).Intersects(doorway), "the window is clear of the doorway");

            var blocking = Physics.OverlapCapsule(stand + Vector3.up * 0.3f, stand + Vector3.up * 1.6f, 0.2f, ~0, QueryTriggerInteraction.Ignore)
                .Where(c => !c.transform.IsChildOf(m_Player.transform)).ToArray();
            Assert.IsEmpty(blocking, "Something stands where the operator stands: " + string.Join(", ", blocking.Select(c => c.name)));

            // The stick, the buttons and the monitor are within arm's reach of the operator.
            foreach (var part in new Component[] { s.stick, s.extendButton, s.retractButton, s.clawButton, s.monitor })
                Assert.Less(Vector3.Distance(part.transform.position, eyes), 1.25f, part.name + " is within reach");

            // Looking out of the window hits the invisible pane (environment), so nobody floats out into space.
            Vector3 windowCentre = s.transform.TransformPoint(new Vector3(0f, 1.7f, 0f));
            Assert.IsTrue(Physics.Raycast(eyes, (windowCentre - eyes).normalized, out var hit, 3f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore), "the window is closed");
            Assert.Less(Vector3.Distance(hit.point, windowCentre), 0.3f, "and it is the window that stops you");

            // Everything that moves is outside the tug: the arm, the hatch and the cargo never enter the safe zone.
            Assert.IsFalse(m_Director.safeZone.Contains(s.TipWorld), "the claw is outside");
            Assert.IsFalse(m_Director.safeZone.Contains(s.turret.position), "the arm is mounted outside");
            Assert.IsFalse(m_Director.safeZone.Contains(s.intakeMouth.position), "the hatch is outside");
            Assert.AreEqual(1, s.GetComponentsInChildren<Light>(true).Length, "one light: the claw's work light");
            Assert.AreEqual(LightShadows.None, s.workLight.shadows);
        }

        [UnityTest]
        public IEnumerator Station_IsLockedDuringAContract_AndOpensAgainAfterIt()
        {
            var s = Station();
            yield return null;
            s.SetPose(10f, 5f, 1.5f);
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local), "a contract starts with the claw in the tug");
            Assert.AreEqual(MissionPhase.Active, m_Director.Phase);
            Assert.IsFalse(s.Operable);

            Vector3 before = s.TipLocal;
            s.StickOverride = new Vector2(1f, 0f);
            Run(2f);
            Vector3 towardPark = s.TipLocal;
            Assert.IsFalse(s.extendButton.Interactable || s.retractButton.Interactable || s.clawButton.Interactable, "the buttons are off");
            Assert.IsFalse(s.stick.interactable.enabled, "the stick cannot be taken");
            s.ToggleClaw();
            Assert.IsFalse(s.ClawClosed, "the claw does not respond");
            Run(10f);
            Assert.AreEqual(0, s.Cargo.Count, "nothing drifts past during a contract");
            Assert.AreEqual(0, s.SpawnedCount);
            StringAssert.Contains("LOCKED", s.MonitorText);
            Vector3 park = s.TipLocal;
            Vector3 parkPose = s.PoseFor(park);
            Assert.AreEqual(SalvageClawStation.ParkPose.x, parkPose.x, 2f, "parked (yaw), not where the stick pushed it");
            Assert.AreEqual(SalvageClawStation.ParkPose.y, parkPose.y, 0.3f, "parked (reach)");
            Assert.AreNotEqual(before, towardPark);

            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            Assert.AreEqual(MissionPhase.Results, m_Director.Phase);
            Run(0.1f);
            Assert.IsTrue(s.Operable, "usable on the results screen");
            Assert.IsTrue(s.clawButton.Interactable);
            Run(3f);
            Assert.Greater(s.Cargo.Count, 0, "cargo drifts past again");
            s.StickOverride = null;
        }

        // ------------------------------------------------------------------ the real stick, through XRI

        static IEnumerator Trigger(SimHands.Controller c, bool down)
        {
            c.state.trigger = down ? 1f : 0f;
            c.state = c.state.WithButton(ControllerButton.TriggerButton, down);
            InputSystem.QueueStateEvent(c.device, c.state);
            yield return SimHands.Settle();
        }

        [UnityTest]
        public IEnumerator TheStick_IsTakenWithGrip_TiltsWithTheHand_TriggerWorksTheClaw_AndItSpringsBack()
        {
            var s = Station();
            yield return null;
            s.SetPose(10f, 5f, 1.5f);
            // Step up to the console, facing the window.
            Assert.IsTrue(m_Player.locomotion.TeleportTo(s.standPoint.position, Quaternion.LookRotation(s.transform.forward)), "the operator spot can be teleported to");
            yield return SimHands.Settle(5);
            s.OperatorPresentOverride = null;
            Assert.IsTrue(s.OperatorPresent, "standing at the console counts as operating it");
            Vector3 knob = s.stick.interactable.transform.position;
            Vector3 right = s.transform.right, away = s.transform.forward;
            using (var hands = new SimHands(m_Player))
            {
                var c = hands.Right;
                yield return SimHands.Pose(c, knob);
                yield return SimHands.Grip(c, true);
                Assert.IsTrue(s.stick.Held, "squeezing GRIP on the knob takes the stick (hovering " + c.hand.interactor.interactablesHovered.Count + ")");
                Assert.AreSame(m_Player.rightHand, s.stick.HeldBy);
                Assert.IsTrue(m_Player.rightHand.IsHolding, "the hand counts as holding something, so it does not grab the wall or paddle");
                Vector3 rig = m_Player.transform.position;
                Run(0.1f);
                Assert.AreEqual(Vector2.zero, s.stick.Axis, "taking hold does not move it");

                yield return SimHands.Pose(c, knob + right * 0.07f);
                Run(0.1f);
                Assert.Greater(s.stick.Axis.x, 0.5f, "moving the hand right tilts the stick right");
                Assert.AreEqual(0f, s.stick.Axis.y, 0.15f);
                Assert.Greater(Vector3.Angle(s.stick.pivot.up, s.stick.transform.up), 10f, "the stick visibly leans");
                Vector3 tip = s.TipLocal;
                Run(1f);
                Assert.Greater(s.TipLocal.x, tip.x + 0.4f, "and the claw follows");

                yield return SimHands.Pose(c, knob - away * 0.07f);
                Run(0.1f);
                Assert.Less(s.stick.Axis.y, -0.5f, "pulling the hand back pulls the stick back");
                tip = s.TipLocal;
                Run(1f);
                Assert.Greater(s.TipLocal.y, tip.y + 0.3f, "and the claw rises");
                Assert.Less(Vector3.Distance(m_Player.transform.position, rig), 0.02f, "working the stick does not move the player");

                Assert.IsFalse(s.ClawClosed);
                yield return Trigger(c, true);
                Assert.IsTrue(s.ClawClosed, "the trigger on the stick hand closes the claw");
                yield return Trigger(c, false);
                yield return Trigger(c, true);
                Assert.IsFalse(s.ClawClosed, "and opens it again");
                yield return Trigger(c, false);

                yield return SimHands.Grip(c, false);
                Assert.IsFalse(s.stick.Held, "letting go releases it");
                Run(0.5f);
                Assert.AreEqual(Vector2.zero, s.stick.Axis, "and it springs back to centre");
                Assert.Less(Vector3.Angle(s.stick.pivot.up, s.stick.transform.up), 1f);
                yield return SimHands.Pose(c, m_Player.HeadPosition + new Vector3(0.2f, -0.4f, 0.3f));
            }
        }

        // ------------------------------------------------------------------ playing it

        [UnityTest]
        public IEnumerator Controls_MoveTheClaw_TheWayTheStickAndButtonsSay_AndReachAContainer()
        {
            var s = Station();
            yield return null;
            s.SetPose(10f, 5f, 1.5f);
            Run(0.2f);

            Vector3 a = s.TipLocal;
            s.StickOverride = new Vector2(1f, 0f);
            Run(1f);
            Assert.Greater(s.TipLocal.x, a.x + 0.5f, "stick right swings the claw to the operator's right");
            s.StickOverride = new Vector2(0f, -1f);
            a = s.TipLocal;
            Run(1f);
            Assert.Greater(s.TipLocal.y, a.y + 0.5f, "pulling the stick back raises it");
            s.StickOverride = new Vector2(0f, 1f);
            a = s.TipLocal;
            Run(1f);
            Assert.Less(s.TipLocal.y, a.y - 0.5f, "pushing it lowers it");
            s.StickOverride = Vector2.zero;
            Run(1f);
            float reach = s.PoseFor(s.TipLocal).y;
            s.ReachOverride = 1f;
            Run(1f);
            Assert.Greater(s.PoseFor(s.TipLocal).y, reach + 0.5f, "EXTEND pushes it out");
            s.ReachOverride = -1f;
            Run(1f);
            Assert.Less(s.PoseFor(s.TipLocal).y, reach + 0.5f, "RETRACT pulls it in");
            s.StickOverride = null;
            s.ReachOverride = null;

            // The arm is heavy: it does not stop dead when the stick is released.
            s.StickOverride = new Vector2(1f, 0f);
            Run(1f);
            s.StickOverride = Vector2.zero;
            a = s.TipLocal;
            s.Step(Dt);
            Assert.Greater(Vector3.Distance(s.TipLocal, a), 0.005f, "it keeps moving a moment after the stick is centred");
            s.StickOverride = null;

            // The real buttons and the stick's trigger are wired.
            reach = s.PoseFor(s.TipLocal).y;
            Assert.IsTrue(s.extendButton.Press(null));
            Run(0.6f);
            Assert.Greater(s.PoseFor(s.TipLocal).y, reach + 0.05f, "a press of EXTEND moves it out a little");
            Assert.IsFalse(s.ClawClosed);
            s.stick.PullTrigger();
            Assert.IsTrue(s.ClawClosed, "the trigger on the stick closes the claw");
            s.stick.PullTrigger();
            Assert.IsFalse(s.ClawClosed, "and opens it");
            Assert.IsTrue(s.clawButton.Press(null));
            Assert.IsTrue(s.ClawClosed, "the CLAW button closes it");
            s.SetClaw(false);
            Assert.AreEqual(Vector2.zero, s.stick.Axis, "an untouched stick sits centred");

            // Fly it to a container drifting outside.
            var crate = s.SpawnAt(ClawCargoKind.Crate, new Vector3(-1.5f, 2.5f, 7.5f), Vector3.zero);
            float start = Vector3.Distance(s.TipLocal, crate.transform.localPosition);
            float end = DriveTo(crate.transform.localPosition, 20f);
            Assert.Less(end, crate.GripRadius, "the claw reached the container (from " + start.ToString("0.0") + " m)");
        }

        [UnityTest]
        public IEnumerator ClosingOnAContainer_GripsIt_AndTheHatchPaysOnce_IntoTheSave()
        {
            var s = Station();
            yield return null;
            s.SetPose(0f, 5f, 1.5f);
            Run(0.2f);
            var crate = s.SpawnAt(ClawCargoKind.Crate, s.TipLocal + new Vector3(0.5f, 0.2f, 0.4f), new Vector3(0.1f, 0f, 0f));
            int credits = m_Director.Profile.Data.totalCredits;
            int writes = m_Storage.WriteCount;

            s.SetClaw(true);
            Run(0.5f);
            Assert.AreSame(crate, s.Held, "closing the claw within reach grips it");
            Assert.AreEqual(SalvageClawCargo.State.Gripped, crate.Current);
            Assert.Less(Vector3.Distance(crate.transform.localPosition, s.TipLocal), 0.05f, "it is pulled into the claw");
            s.StickOverride = new Vector2(-1f, 0f);
            Run(0.5f);
            s.StickOverride = null;
            Assert.Less(Vector3.Distance(crate.transform.localPosition, s.TipLocal), 0.05f, "and moves with it");
            Assert.AreEqual(credits, m_Director.Profile.Data.totalCredits, "nothing is paid until it is in the hatch");

            float left = DriveTo(s.IntakeLocal, 25f, 0.3f);
            Assert.IsNull(s.Held, "taken by the hatch (" + left.ToString("0.00") + " m)");
            Assert.AreEqual(SalvageClawConfig.CratePay, s.LastPay);
            Assert.AreEqual(credits + SalvageClawConfig.CratePay, m_Director.Profile.Data.totalCredits, "paid into this player's own save");
            Assert.AreEqual(SalvageClawConfig.CratePay, m_Director.Profile.Data.clawShiftCredits);
            Assert.Greater(m_Storage.WriteCount, writes, "saved at once");
            StringAssert.Contains("\"totalCredits\": " + (credits + SalvageClawConfig.CratePay), m_Storage.Contents);
            StringAssert.Contains("\"clawShiftCredits\": " + SalvageClawConfig.CratePay, m_Storage.Contents);
            Assert.IsFalse(s.ClawClosed, "the claw lets go into the hatch");

            Run(3f);
            Assert.IsFalse(crate.gameObject.activeSelf, "swallowed");
            Assert.AreEqual(credits + SalvageClawConfig.CratePay, m_Director.Profile.Data.totalCredits, "paid once");
            StringAssert.Contains("HAUL " + SalvageClawConfig.CratePay + " / " + SalvageClawConfig.ShiftCap, s.MonitorText);
        }

        [UnityTest]
        public IEnumerator AMiss_PaysNothing_AndClippingAContainer_KnocksItAway()
        {
            var s = Station();
            yield return null;
            s.SetPose(0f, 5f, 1.5f);
            Run(0.2f);
            int credits = m_Director.Profile.Data.totalCredits;
            var far = s.SpawnAt(ClawCargoKind.Crate, s.TipLocal + new Vector3(2.5f, 0f, 1f), Vector3.zero);
            s.SetClaw(true);
            Run(1f);
            Assert.IsNull(s.Held, "too far away to grip");
            StringAssert.Contains("MISSED", s.MonitorText);
            far.transform.localPosition = s.TipLocal + new Vector3(0.3f, 0f, 0f);
            Run(1f);
            Assert.IsNull(s.Held, "a claw already shut does not grab what drifts into it");
            s.SetClaw(false);
            Run(0.5f);

            // Ram an open claw into a container: it tumbles away, unpaid.
            s.ClearCargo();
            s.SetPose(-10f, 5f, 1.5f);
            Run(0.2f);
            Vector3 ahead = s.TipLocal + new Vector3(1.6f, 0f, 0f);
            var hit = s.SpawnAt(ClawCargoKind.Crate, ahead, Vector3.zero);
            s.StickOverride = new Vector2(1f, 0f);
            Run(2.5f);
            s.StickOverride = null;
            Assert.IsNull(s.Held);
            Assert.AreEqual(SalvageClawCargo.State.Drifting, hit.Current);
            Assert.Greater(hit.Velocity.magnitude, 0.3f, "knocked tumbling away");
            Assert.Greater(hit.Spin.magnitude, 1f);
            Assert.AreEqual(credits, m_Director.Profile.Data.totalCredits, "a miss pays nothing");
            Assert.AreEqual(0, m_Director.Profile.Data.clawShiftCredits);
        }

        [UnityTest]
        public IEnumerator TheShiftCap_StopsPay_AndResetsWhenAContractEnds()
        {
            var s = Station();
            yield return null;
            s.SetPose(0f, 4.5f, 1.5f);
            Run(0.2f);
            m_Director.Profile.Data.clawShiftCredits = SalvageClawConfig.ShiftCap - 10;
            int credits = m_Director.Profile.Data.totalCredits;

            Assert.AreEqual(10, CatchAndDeliver(ClawCargoKind.Strongbox), "the strongbox pays only up to the cap");
            Assert.AreEqual(credits + 10, m_Director.Profile.Data.totalCredits);
            Assert.AreEqual(SalvageClawConfig.ShiftCap, m_Director.Profile.Data.clawShiftCredits);
            Run(5f);
            StringAssert.Contains(SalvageClawConfig.ShiftOverText, s.MonitorText);

            Assert.AreEqual(0, CatchAndDeliver(ClawCargoKind.LongContainer), "after the cap it is just for fun");
            Assert.AreEqual(credits + 10, m_Director.Profile.Data.totalCredits);

            // Fly a contract (and fail it): the shift starts again.
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            Assert.AreEqual(0, m_Director.Profile.Data.clawShiftCredits, "the cap resets when the contract ends");
            StringAssert.Contains("\"clawShiftCredits\": 0", m_Storage.Contents, "and that is saved");
            Run(0.5f);
            s.ClearCargo();
            int after = m_Director.Profile.Data.totalCredits;
            Assert.AreEqual(SalvageClawConfig.StrongboxPay, CatchAndDeliver(ClawCargoKind.Strongbox), "paying again");
            Assert.AreEqual(after + SalvageClawConfig.StrongboxPay, m_Director.Profile.Data.totalCredits);
            Assert.LessOrEqual(SalvageClawConfig.ShiftCap, 200, "pocket money, not a contract");
        }

        // ------------------------------------------------------------------ the drift

        [UnityTest]
        public IEnumerator Cargo_IsSeeded_RespectsTheLimit_DriftsAway_AndIsClearedWhenNobodyOperates()
        {
            var s = Station();
            yield return null;

            List<string> Pattern(int seed)
            {
                s.ResetRandom(seed);
                var list = new List<string>();
                for (int i = 0; i < 8; i++)
                {
                    var c = s.SpawnNext();
                    list.Add(c.kind + " " + c.transform.localPosition.ToString("F3") + " " + c.Velocity.ToString("F3"));
                }
                s.ClearCargo();
                return list;
            }
            var first = Pattern(123);
            CollectionAssert.AreEqual(first, Pattern(123), "the same seed drifts the same things the same way");
            CollectionAssert.AreNotEqual(first, Pattern(456));

            // Many draws: every kind turns up, the strongbox is the rare one, all of them start outside and cross the arm's reach.
            s.ResetRandom(9);
            var counts = new Dictionary<ClawCargoKind, int>();
            for (int i = 0; i < 400; i++)
            {
                var c = s.SpawnNext();
                counts[c.kind] = counts.TryGetValue(c.kind, out int n) ? n + 1 : 1;
                Assert.Greater(c.transform.localPosition.z, 1.5f, "starts outside the hull");
                Assert.Greater(c.PathCentre.z, 4f);
                Assert.Less(new Vector2(c.PathCentre.x - s.turret.localPosition.x, c.PathCentre.z - s.turret.localPosition.z).magnitude, SalvageClawConfig.MaxReach, "its path crosses the arm's reach");
                if (i % 5 == 4) s.ClearCargo();
            }
            s.ClearCargo();
            Assert.AreEqual(4, counts.Count, "crates, scrap, long containers and strongboxes");
            Assert.Less(counts[ClawCargoKind.Strongbox], counts[ClawCargoKind.Crate] / 2, "the strongbox is rare");

            // Operated for a while: a handful at a time.
            s.ResetRandom(77);
            int most = 0;
            for (int i = 0; i < 2500; i++)
            {
                s.Step(Dt);
                most = Mathf.Max(most, s.Cargo.Count);
            }
            Assert.GreaterOrEqual(s.SpawnedCount, 6, "things keep drifting past");
            Assert.LessOrEqual(most, SalvageClawConfig.MaxCargo, "never more than a handful");
            Assert.Less(s.Cargo.Count, s.SpawnedCount, "and they drift out of range and are removed");

            var runaway = s.SpawnAt(ClawCargoKind.Scrap, new Vector3(0f, 1f, 8f), new Vector3(3f, 0f, 0f));
            Run(6f);
            Assert.IsFalse(s.Cargo.Contains(runaway), "removed once it is out of range");

            // Nobody at the station: everything is cleared and nothing new comes.
            Assert.Greater(s.Cargo.Count + 1, 0);
            s.SpawnAt(ClawCargoKind.Crate, new Vector3(0f, 1f, 6f), Vector3.zero);
            s.OperatorPresentOverride = false;
            Run(SalvageClawConfig.AbandonDelay + 0.5f);
            Assert.AreEqual(0, s.Cargo.Count, "cleaned up when nobody is at the station");
            int spawned = s.SpawnedCount;
            Run(15f);
            Assert.AreEqual(spawned, s.SpawnedCount, "nothing spawns for an empty station");

            // The real check: the local player's head, far away in the cabin, does not count as operating it.
            s.OperatorPresentOverride = null;
            Assert.IsFalse(s.OperatorPresent, "the player at the hub spawn is not at the claw");
        }

        // ------------------------------------------------------------------ co-op and contracts untouched

        [UnityTest]
        public IEnumerator TheClaw_SendsNothing_AndAContractStillRunsNormallyAroundIt()
        {
            var s = Station();
            yield return null;
            s.SetPose(0f, 4.5f, 1.5f);
            Run(0.2f);
            Assert.AreEqual(SalvageClawConfig.CratePay, CatchAndDeliver(ClawCargoKind.Crate));
            s.ResetRandom(5);
            Run(10f);
            Assert.IsNull(m_Director.Net, "no co-op session was opened");
            Assert.AreEqual(0, UdpTransport.LiveCount, "no socket");
            Assert.IsTrue(m_Net.IsOffline, "the network session stays idle");

            // A contract starts and ends exactly as before; its pay is untouched by the claw.
            int credits = m_Director.Profile.Data.totalCredits;
            s.SetPose(0f, 4.5f, 1.5f);
            Run(0.2f);
            var held = s.SpawnAt(ClawCargoKind.Crate, s.TipLocal, Vector3.zero);
            s.SetClaw(true);
            Run(0.4f);
            Assert.AreSame(held, s.Held);
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            Run(0.2f);
            Assert.IsNull(s.Held, "the claw lets go when a contract starts");
            Assert.AreEqual(0, s.Cargo.Count);
            var result = m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
            Assert.IsNotNull(result);
            Assert.AreEqual(credits + Mathf.Max(0, result.Payout), m_Director.Profile.Data.totalCredits, "the contract pays what it always paid");
        }
    }
}

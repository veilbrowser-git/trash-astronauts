using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Utilities;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Picking things up off the floor, through the real input path (simulated tracked controllers, real
    /// bindings, real XRI interactors), in a zero-g ship and in the Moon cave. A standing player's hand
    /// comfortably reaches about 0.75 m above the floor; these tests measure what the direct grab can and
    /// cannot reach from there.
    /// </summary>
    public class PickupReachPlayTests
    {
        MissionDirector m_Director;
        PlayerContext m_Player;
        InputSettings.BackgroundBehavior m_OldBackground;
        InputSettings.EditorInputBehaviorInPlayMode m_OldEditorBehavior;

        sealed class Controller
        {
            public XRSimulatedController device;
            public XRSimulatedControllerState state;
            public HandRig hand;
        }

        Controller m_Right, m_Left;
        Vector3 m_Floor;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            m_OldBackground = InputSystem.settings.backgroundBehavior;
            m_OldEditorBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;

            MissionDirector.StorageOverride = new MemorySaveStorage();
            MissionDirector.SeedOverride = 4242;
            MissionDirector.CreatureOverride = 0;
            MissionDirector.MapOverride = 0;
            MissionDirector.ConditionOverride = ContractCondition.Standard;
            BlackoutController.DamageOverride = 0f;
            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            yield return null;
            m_Director = MissionDirector.Active;
            m_Player = m_Director.Players.First();

            m_Right = AddController("Test Right Controller", UnityEngine.InputSystem.CommonUsages.RightHand, m_Player.rightHand);
            m_Left = AddController("Test Left Controller", UnityEngine.InputSystem.CommonUsages.LeftHand, m_Player.leftHand);
            // One physics step per frame at the game's 72 Hz, so timings are the same every run.
            Time.captureDeltaTime = RealPhysics.FixedTimestep;
            yield return null;
        }

        [TearDown]
        public void TearDown()
        {
            Time.captureDeltaTime = 0f;
            if (m_Right != null && m_Right.device != null) InputSystem.RemoveDevice(m_Right.device);
            if (m_Left != null && m_Left.device != null) InputSystem.RemoveDevice(m_Left.device);
            InputSystem.settings.backgroundBehavior = m_OldBackground;
            InputSystem.settings.editorInputBehaviorInPlayMode = m_OldEditorBehavior;
            MissionDirector.StorageOverride = null;
            MissionDirector.SeedOverride = null;
            MissionDirector.CreatureOverride = null;
            MissionDirector.MapOverride = null;
            MissionDirector.ConditionOverride = null;
            BlackoutController.DamageOverride = null;
        }

        static Controller AddController(string name, InternedString usage, HandRig hand)
        {
            var device = InputSystem.AddDevice<XRSimulatedController>(name);
            InputSystem.SetDeviceUsage(device, usage);
            return new Controller
            {
                device = device,
                hand = hand,
                state = new XRSimulatedControllerState { isTracked = true, trackingState = 3, deviceRotation = Quaternion.identity },
            };
        }

        static IEnumerator Settle(int frames = 3)
        {
            for (int i = 0; i < frames; i++)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
            }
        }

        // ------------------------------------------------------------------ places

        int MapIndex(string id)
        {
            int index = System.Array.FindIndex(m_Director.maps, m => m.mapId == id);
            Assert.GreaterOrEqual(index, 0, "No map " + id);
            return index;
        }

        /// <summary>Docks at a wreck and stands the player on its floor at <paramref name="feet"/>, facing +Z.</summary>
        IEnumerator StandIn(string mapId, Vector3 feet)
        {
            MissionDirector.MapOverride = MapIndex(mapId);
            MissionDirector.SeedOverride = 5;
            Assert.IsTrue(m_Director.StartMission(PlayerId.Local));
            m_Director.keepPlayersInHub = false;
            yield return Settle(2);
            Assert.IsTrue(m_Player.locomotion.TeleportTo(feet, Quaternion.identity));
            yield return Settle(2);
            m_Player.locomotion.zeroG.Stop();
            Assert.IsTrue(Physics.Raycast(feet + Vector3.up * 0.6f, Vector3.down, out RaycastHit hit, 2f, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore),
                "There should be a floor under " + feet);
            m_Floor = hit.point;
            yield return Settle(4);
        }

        IEnumerator StandInShip() => StandIn("freighter", new Vector3(0f, 0f, 9.2f));
        IEnumerator StandInCave() => StandIn("lava_tube", new Vector3(0f, 0f, 24f));

        bool InCave => MapGravity.GravityAt(m_Floor + Vector3.up * 0.3f) != Vector3.zero;

        /// <summary>A point relative to where the player stands: x right, y up from the floor, z forward.</summary>
        Vector3 At(float x, float up, float forward) => new Vector3(m_Floor.x + x, m_Floor.y + up, m_Floor.z + forward);

        static float HalfHeight(SalvageItem item)
        {
            float half = 0f;
            foreach (var c in item.GetComponentsInChildren<Collider>())
                if (!c.isTrigger) half = Mathf.Max(half, c.bounds.max.y - item.transform.position.y);
            return Mathf.Max(0.02f, half);
        }

        /// <summary>Lays an item flat on the floor at (x, z) and lets it come to rest there.</summary>
        IEnumerator LayOnFloor(SalvageItem item, float x, float forward)
        {
            item.transform.rotation = Quaternion.identity;
            item.Body.rotation = Quaternion.identity;
            Physics.SyncTransforms();
            Vector3 at = At(x, HalfHeight(item) + 0.004f, forward);
            if (!item.Body.isKinematic)
            {
                item.Body.linearVelocity = Vector3.zero;
                item.Body.angularVelocity = Vector3.zero;
            }
            item.transform.position = at;
            item.Body.position = at;
            Physics.SyncTransforms();
            if (InCave) yield return Settle(90); // lunar gravity settles it the rest of the way
            else yield return Settle(4);         // weightless: it stays against the deck where it drifted
            if (!item.Body.isKinematic)
            {
                item.Body.linearVelocity = Vector3.zero;
                item.Body.angularVelocity = Vector3.zero;
            }
        }

        IEnumerable<SalvageItem> LooseItems() => m_Director.Items.Where(i => i != null && i.definition != null && !i.IsMounted && !i.IsSecured && i.grab != null && i.grab.enabled);

        /// <summary>One loose item of each kind.</summary>
        List<SalvageItem> OneOfEachKind() => LooseItems().GroupBy(i => i.definition.id).Select(g => g.First()).OrderBy(i => i.Body.mass).ToList();

        /// <summary>Moves every other loose item well away, so only the item under test is near the hands.</summary>
        void ClearAround(params SalvageItem[] keep)
        {
            int n = 0;
            foreach (var item in LooseItems())
            {
                if (keep.Contains(item) || item.Body.isKinematic) continue;
                Vector3 away = new Vector3(40f + (n % 10) * 1.5f, 40f + (n / 10) * 1.5f, -40f);
                n++;
                item.Body.linearVelocity = Vector3.zero;
                item.transform.position = away;
                item.Body.position = away;
            }
            Physics.SyncTransforms();
        }

        // ------------------------------------------------------------------ hands

        /// <summary>Queues a controller pose so its grab point (the palm) is at a world position, pointing along <paramref name="pointing"/>.</summary>
        void Place(Controller c, Vector3 palm, Vector3 pointing)
        {
            Transform space = c.hand.transform.parent;
            Vector3 up = Mathf.Abs(Vector3.Dot(pointing.normalized, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up;
            Quaternion rotation = Quaternion.LookRotation(pointing, up);
            Vector3 grabOffset = c.hand.transform.InverseTransformPoint(c.hand.interactor.transform.position);
            Vector3 controllerWorld = palm - rotation * grabOffset;
            c.state.devicePosition = space.InverseTransformPoint(controllerWorld);
            c.state.deviceRotation = Quaternion.Inverse(space.rotation) * rotation;
            InputSystem.QueueStateEvent(c.device, c.state);
        }

        IEnumerator Pose(Controller c, Vector3 palm, Vector3 pointing, int frames = 3)
        {
            Place(c, palm, pointing);
            yield return Settle(frames);
        }

        /// <summary>Moves the palm in a straight line at a calm hand speed (slow enough not to swat or swim).</summary>
        IEnumerator MoveTo(Controller c, Vector3 palm, Vector3 pointing, float speed = 0.4f)
        {
            Vector3 from = c.hand.interactor.transform.position;
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(from, palm) / (speed * RealPhysics.FixedTimestep)));
            for (int i = 1; i <= steps; i++)
            {
                Place(c, Vector3.Lerp(from, palm, i / (float)steps), pointing);
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            yield return Settle(2);
        }

        static void SetGrip(Controller c, bool down)
        {
            c.state.grip = down ? 1f : 0f;
            c.state = c.state.WithButton(ControllerButton.GripButton, down);
            InputSystem.QueueStateEvent(c.device, c.state);
        }

        IEnumerator Grip(Controller c, bool down, int frames = 3)
        {
            SetGrip(c, down);
            yield return Settle(frames);
        }

        static bool Hovers(Controller c, SalvageItem item) => c.hand.interactor.interactablesHovered.Any(h => ReferenceEquals(h, item.grab));

        static string Report(Controller c, SalvageItem item)
        {
            var interactor = c.hand.interactor;
            return " [" + item.name + " " + item.Body.mass + " kg at " + item.transform.position.ToString("F2")
                + ", palm " + interactor.transform.position.ToString("F2")
                + ", hovering " + interactor.interactablesHovered.Count + ", selecting " + interactor.interactablesSelected.Count
                + ", held " + item.IsHeld + ", grip " + c.hand.Grip.ToString("F2") + "]";
        }

        // ------------------------------------------------------------------ diagnosis

        /// <summary>
        /// The measured problem. For each kind of loose salvage lying on the floor: lower an open hand straight
        /// down over it at a calm speed and record how high the palm is when the direct grab first touches it;
        /// then hold the hand at a comfortable 0.75 m above the floor, point at the item and squeeze.
        /// Prints a table; asserts only that the measurements were possible.
        /// </summary>
        [UnityTest]
        public IEnumerator Diagnose_DirectGrab_FromTheFloor_ShipAndCave()
        {
            var report = new StringBuilder();
            var sphere = m_Right.hand.interactor.GetComponent<SphereCollider>();
            report.AppendLine("Direct grab volume: sphere radius " + (sphere.radius * sphere.transform.lossyScale.x).ToString("F3")
                + " m, centred " + sphere.center.ToString("F3") + " from the attach point " + m_Right.hand.interactor.attachTransform.localPosition.ToString("F3")
                + " (palm space).");
            foreach (string map in new[] { "freighter", "lava_tube" })
            {
                if (map == "freighter") yield return StandInShip();
                else
                {
                    m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
                    yield return Settle(2);
                    yield return StandInCave();
                }
                report.AppendLine("== " + map + " (floor y " + m_Floor.y.ToString("F2") + ", gravity " + MapGravity.GravityAt(m_Floor + Vector3.up * 0.3f).y.ToString("F2") + ")");
                report.AppendLine("item | kg | top above floor | palm height at first touch | gap palm-to-top | from 0.75 m: hovered / picked up");
                foreach (var item in OneOfEachKind())
                {
                    ClearAround(item);
                    yield return Pose(m_Right, At(0.2f, 1.0f, 0.3f), Vector3.forward);
                    yield return LayOnFloor(item, 0.2f, 0.65f);
                    float top = item.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger).Max(c => c.bounds.max.y) - m_Floor.y;
                    Vector3 over = item.Body.worldCenterOfMass;

                    // Lower the palm straight down over it, 1 cm per physics step at most.
                    yield return MoveTo(m_Right, new Vector3(over.x, m_Floor.y + 0.9f, over.z), Vector3.down);
                    float touch = -1f;
                    for (float h = 0.9f; h >= 0.06f; h -= 0.005f)
                    {
                        Place(m_Right, new Vector3(over.x, m_Floor.y + h, over.z), Vector3.down);
                        yield return new WaitForFixedUpdate();
                        yield return null;
                        if (Hovers(m_Right, item)) { touch = m_Right.hand.interactor.transform.position.y - m_Floor.y; break; }
                    }

                    // A comfortable low reach: 0.75 m above the floor, pointing at it, squeeze.
                    yield return MoveTo(m_Right, At(0.2f, 0.75f, 0.3f), item.transform.position - At(0.2f, 0.75f, 0.3f));
                    yield return Settle(2);
                    bool hovered = Hovers(m_Right, item);
                    yield return Grip(m_Right, true, 6);
                    bool picked = item.IsHeld;
                    yield return Grip(m_Right, false, 3);
                    report.AppendLine(item.definition.id + " | " + item.Body.mass.ToString("F1") + " | " + top.ToString("F3") + " | "
                        + (touch < 0f ? "never" : touch.ToString("F3")) + " | " + (touch < 0f ? "-" : (touch - top).ToString("F3")) + " | " + hovered + " / " + picked);
                    Assert.Greater(touch, 0f, "A hand lowered onto " + item.name + " should touch it before reaching the floor." + Report(m_Right, item));
                }
            }
            Debug.Log("[PickupReach] " + report);
            TestContext.Progress.WriteLine(report.ToString());
        }
    
        // ------------------------------------------------------------------ the fix: helpers

        SalvageItem Kind(string id)
        {
            var item = LooseItems().FirstOrDefault(i => i.definition.id == id && !i.Body.isKinematic);
            Assert.IsNotNull(item, "Need a loose " + id);
            return item;
        }

        /// <summary>The comfortable low reach of a standing player: palm 0.75 m above the floor, a little ahead and to the right.</summary>
        Vector3 LowReach => At(0.2f, 0.75f, 0.3f);

        /// <summary>How far the item's nearest surface is from the palm's attach point (0 = touching it).</summary>
        static float SeatGap(Controller c, SalvageItem item)
        {
            Vector3 attach = c.hand.interactor.attachTransform.position;
            float best = float.MaxValue;
            foreach (var collider in item.GetComponentsInChildren<Collider>())
                if (!collider.isTrigger) best = Mathf.Min(best, Vector3.Distance(collider.ClosestPoint(attach), attach));
            return best;
        }

        static bool Seated(Controller c, SalvageItem item) => item.grab.interactorsSelecting.Contains(c.hand.interactor)
            && !c.hand.Reach.IsPulling && SeatGap(c, item) < 0.05f;

        /// <summary>Holds the hand at the low reach pointing at the item, checks it is shown, squeezes, and waits until it is seated in the palm. Returns the seconds taken (or -1).</summary>
        IEnumerator TakeFromLowReach(Controller c, SalvageItem item, float[] seconds, float timeout = 1.5f)
        {
            yield return MoveTo(c, LowReach, item.Body.worldCenterOfMass - LowReach);
            yield return Settle(2);
            var reach = c.hand.Reach;
            Assert.IsNotNull(reach, "Every hand should have a reach assist.");
            Assert.AreSame(item, reach.Candidate, "An item on the floor ahead of a low, pointing hand should be picked out." + Report(c, item));
            Assert.IsTrue(item.highlight.IsHovered, "The picked-out item should glow with its hover highlight.");
            Assert.IsTrue(reach.line.enabled, "A faint line should run from the palm to the picked-out item.");

            int pulses = c.hand.PulseCount;
            int assisted = reach.AssistedGrabs;
            SetGrip(c, true);
            float start = Time.time;
            seconds[0] = -1f;
            while (Time.time - start < timeout)
            {
                Place(c, LowReach, item.Body.worldCenterOfMass - LowReach);
                yield return new WaitForFixedUpdate();
                yield return null;
                if (Seated(c, item)) { seconds[0] = Time.time - start; break; }
            }
            Assert.AreEqual(assisted + 1, reach.AssistedGrabs, "The squeeze should have been taken by the reach assist." + Report(c, item));
            Assert.Greater(c.hand.PulseCount, pulses, "An assisted pickup gives the normal grab haptic.");
            Assert.IsFalse(reach.line.enabled, "The line goes once the item is taken.");
        }

        /// <summary>Swings the hand along <paramref name="direction"/> and lets go mid-swing. Returns the item's velocity a few steps later.</summary>
        IEnumerator Throw(Controller c, SalvageItem item, Vector3 direction, float speed, Vector3[] velocity)
        {
            Vector3 hand = c.hand.interactor.transform.position;
            float step = speed * RealPhysics.FixedTimestep;
            for (int i = 0; i < 16; i++)
            {
                hand += direction * step;
                Place(c, hand, Vector3.forward);
                if (i == 12) SetGrip(c, false);
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            yield return new WaitForFixedUpdate();
            velocity[0] = item.Body.linearVelocity;
        }

        IEnumerator TakeCarryAndThrow(SalvageItem item)
        {
            ClearAround(item);
            yield return MoveTo(m_Right, At(0.2f, 1.0f, 0.2f), Vector3.forward);
            yield return LayOnFloor(item, 0.2f, 0.75f);

            var seconds = new float[1];
            yield return TakeFromLowReach(m_Right, item, seconds);
            Assert.Greater(seconds[0], 0f, "The item should arrive seated in the palm." + Report(m_Right, item) + " gap " + SeatGap(m_Right, item).ToString("F3"));
            Assert.LessOrEqual(seconds[0], 0.55f, "A light item should be in the palm within about half a second." + Report(m_Right, item));
            Assert.IsTrue(item.IsHeld && item.grab.interactorsSelecting.Count == 1 && item.grab.interactorsSelecting[0] == (IXRSelectInteractor)m_Right.hand.interactor,
                "It is held by the hand's own interactor, an ordinary XRI selection.");
            Assert.IsTrue(item.HasBeenCollected, "An assisted pickup counts as collecting it, as a direct grab does.");
            Assert.IsFalse(m_Player.locomotion.zeroG.IsHandAnchored(m_Right.hand), "The squeeze took the item, not the deck.");
            Assert.AreEqual(0, item.GetComponent<HeldWeight>().SlipCount, "The grip never slipped during the pull.");

            // Carry it: it follows the hand like anything picked up directly.
            Vector3 carried = m_Player.HeadPosition + new Vector3(0.3f, -0.35f, 0.35f);
            yield return MoveTo(m_Right, carried, Vector3.forward, 0.6f);
            yield return Settle(20);
            Assert.Less(SeatGap(m_Right, item), 0.06f, "Carried, it stays seated in the palm." + Report(m_Right, item));

            // Throw it.
            var velocity = new Vector3[1];
            yield return Throw(m_Right, item, Vector3.forward, 4f, velocity);
            Assert.IsFalse(item.IsHeld, "Letting go throws it." + Report(m_Right, item));
            Assert.Greater(Vector3.Dot(velocity[0], Vector3.forward), 1.5f, "It leaves the hand with the swing: " + velocity[0].ToString("F2"));
        }

        // ------------------------------------------------------------------ the fix: tests

        [UnityTest]
        public IEnumerator ReachAssist_InAShip_TakesAnItemOffTheDeck_FromALowReach_SeatsCarriesAndThrows()
        {
            yield return StandInShip();
            Assert.IsFalse(InCave);
            yield return TakeCarryAndThrow(Kind("elec_nav_board"));
        }

        [UnityTest]
        public IEnumerator ReachAssist_InTheMoonCave_TakesAnItemOffTheFloor_FromALowReach_SeatsCarriesAndThrows()
        {
            yield return StandInCave();
            Assert.IsTrue(InCave);
            yield return TakeCarryAndThrow(Kind("novelty_rubber_duck"));
        }

        [UnityTest]
        public IEnumerator ReachAssist_ChoosesTheNearerOfTwoItems()
        {
            yield return StandInShip();
            var near = Kind("elec_nav_board");
            var far = Kind("scrap_hull_plate");
            ClearAround(near, far);
            yield return MoveTo(m_Right, At(0.2f, 1.0f, 0.2f), Vector3.forward);
            yield return LayOnFloor(near, 0.2f, 0.6f);
            yield return LayOnFloor(far, 0.2f, 1.15f);
            // Point between the two.
            Vector3 between = At(0.2f, 0f, 0.85f);
            yield return MoveTo(m_Right, LowReach, between - LowReach);
            yield return Settle(2);
            Assert.AreSame(near, m_Right.hand.Reach.Candidate, "The nearer item is the one shown." + Report(m_Right, near));
            Assert.IsFalse(far.highlight.IsHovered, "Only the item a squeeze would take glows.");
            yield return Grip(m_Right, true, 40);
            Assert.IsTrue(near.IsHeld, "The squeeze takes the nearer item." + Report(m_Right, near));
            Assert.IsFalse(far.IsHeld);
            yield return Grip(m_Right, false);
        }

        [UnityTest]
        public IEnumerator ReachAssist_NeverPullsThroughAWall_OrFromBeyondItsRange()
        {
            yield return StandInShip();
            var item = Kind("elec_nav_board");
            ClearAround(item);
            yield return MoveTo(m_Right, At(0.2f, 1.0f, 0.2f), Vector3.forward);
            yield return LayOnFloor(item, 0.2f, 0.75f);

            // A low wall between the hand and the item.
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.layer = GameLayers.Environment;
            wall.transform.position = At(0.2f, 0.3f, 0.52f);
            wall.transform.localScale = new Vector3(1f, 0.6f, 0.04f);
            Physics.SyncTransforms();
            yield return MoveTo(m_Right, LowReach, item.Body.worldCenterOfMass - LowReach);
            yield return Settle(2);
            Assert.IsNull(m_Right.hand.Reach.Candidate, "Nothing is shown through a wall." + Report(m_Right, item));
            yield return Grip(m_Right, true, 30);
            Assert.IsFalse(item.IsHeld, "Nothing is pulled through a wall." + Report(m_Right, item));
            yield return Grip(m_Right, false);
            Object.Destroy(wall);
            yield return Settle(3);
            Assert.AreSame(item, m_Right.hand.Reach.Candidate, "With the wall gone the same item is shown (so the wall was what stopped it).");

            // Too far: 1.5 m along the floor.
            yield return LayOnFloor(item, 0.2f, 1.75f);
            yield return MoveTo(m_Right, LowReach, item.Body.worldCenterOfMass - LowReach);
            yield return Settle(2);
            Assert.IsNull(m_Right.hand.Reach.Candidate, "Nothing beyond the assist's range is shown." + Report(m_Right, item));
            yield return Grip(m_Right, true, 30);
            Assert.IsFalse(item.IsHeld, "Nothing beyond the range is taken.");
            Assert.AreEqual(0, m_Right.hand.Reach.AssistedGrabs);
            yield return Grip(m_Right, false);
        }

        [UnityTest]
        public IEnumerator ReachAssist_NeverTakesAnItemFromAnotherHand_OrOneACrewMemberHolds()
        {
            yield return StandInShip();
            var item = Kind("elec_nav_board");
            ClearAround(item);
            yield return MoveTo(m_Right, At(0.2f, 1.0f, 0.2f), Vector3.forward);
            yield return LayOnFloor(item, 0.2f, 0.75f);

            // A crew member on another machine holds it: the grab's own rule says no, and the assist obeys it.
            item.SetRemoteHolder(new PlayerId(1));
            yield return MoveTo(m_Right, LowReach, item.Body.worldCenterOfMass - LowReach);
            yield return Settle(2);
            Assert.IsFalse(item.grab.IsSelectableBy((IXRSelectInteractor)m_Right.hand.interactor), "The grab itself refuses an item in a crew member's hand.");
            Assert.IsNull(m_Right.hand.Reach.Candidate, "An item in someone's hand is not shown.");
            yield return Grip(m_Right, true, 30);
            Assert.IsFalse(item.grab.isSelected, "Nor taken.");
            yield return Grip(m_Right, false);
            item.SetRemoteHolder(PlayerId.None);
            yield return Settle(3);
            Assert.AreSame(item, m_Right.hand.Reach.Candidate, "Once they let go it can be taken again.");

            // The other hand holds it: still not taken.
            yield return MoveTo(m_Left, At(-0.2f, 1.0f, 0.4f), Vector3.forward);
            item.Body.linearVelocity = Vector3.zero;
            item.transform.position = m_Left.hand.interactor.transform.position;
            item.Body.position = item.transform.position;
            Physics.SyncTransforms();
            yield return Settle(2);
            yield return Grip(m_Left, true, 4);
            Assert.IsTrue(item.grab.IsSelected((IXRSelectInteractor)m_Left.hand.interactor), "Set-up: the left hand holds it." + Report(m_Left, item));
            yield return MoveTo(m_Right, LowReach, item.Body.worldCenterOfMass - LowReach);
            Assert.AreNotSame(item, m_Right.hand.Reach.Candidate, "An item in the other hand is not shown.");
            yield return Grip(m_Right, true, 30);
            Assert.IsFalse(item.grab.IsSelected((IXRSelectInteractor)m_Right.hand.interactor), "Nor taken from it." + Report(m_Right, item));
            Assert.IsTrue(item.grab.IsSelected((IXRSelectInteractor)m_Left.hand.interactor));
            yield return Grip(m_Right, false);
            yield return Grip(m_Left, false);
        }

        [UnityTest]
        public IEnumerator ReachAssist_LeavesWallGrabsAndSwimStrokesAlone_WhenNoItemIsInReach()
        {
            yield return StandInShip();
            var zeroG = m_Player.locomotion.zeroG;
            ClearAround();
            yield return Settle(2);

            // The palm on the deck, nothing near: the squeeze holds on to the ship.
            Vector3 deck = At(0.25f, 0.12f, 0.45f);
            yield return MoveTo(m_Right, At(0.25f, 0.5f, 0.45f), Vector3.down);
            yield return MoveTo(m_Right, deck, Vector3.down, 0.3f);
            Assert.IsNull(m_Right.hand.Reach.Candidate);
            yield return Grip(m_Right, true, 4);
            Assert.IsTrue(zeroG.IsHandAnchored(m_Right.hand), "Gripping at the deck with no item about should hold on to the ship." + Report(m_Right, Kind("elec_nav_board")));
            yield return Grip(m_Right, false, 4);
            Assert.IsFalse(zeroG.IsHandAnchored(m_Right.hand));

            // Also with an item far along the deck: a palm pressed to the deck means "hold on".
            var item = Kind("elec_nav_board");
            ClearAround(item);
            yield return LayOnFloor(item, 0.25f, 1.25f);
            yield return MoveTo(m_Right, At(0.25f, 0.08f, 0.45f), item.Body.worldCenterOfMass - At(0.25f, 0.08f, 0.45f), 0.3f);
            Assert.IsNull(m_Right.hand.Reach.Candidate, "With the palm on the deck, an item 0.8 m away is left for a deliberate reach.");
            yield return Grip(m_Right, true, 4);
            Assert.IsTrue(zeroG.IsHandAnchored(m_Right.hand), "...and the squeeze grabs the deck." + Report(m_Right, item));
            Assert.IsFalse(item.IsHeld);
            yield return Grip(m_Right, false, 4);
            ClearAround();

            // In open space: grip and sweep the hand back, a paddle stroke.
            m_Player.locomotion.zeroG.Stop();
            Vector3 open = At(0.25f, 1.1f, 0.5f);
            yield return MoveTo(m_Right, open, Vector3.forward);
            float before = zeroG.SwimSpeedGained;
            yield return Grip(m_Right, true, 2);
            Assert.IsNull(m_Right.hand.Reach.Candidate);
            Vector3 hand = open;
            for (int i = 0; i < 12; i++)
            {
                hand += Vector3.back * 0.03f;
                Place(m_Right, hand, Vector3.forward);
                yield return new WaitForFixedUpdate();
                yield return null;
            }
            Assert.Greater(zeroG.SwimSpeedGained, before + 0.05f, "A gripped stroke in open space should still paddle the player along.");
            yield return Grip(m_Right, false, 4);
        }

        [UnityTest]
        public IEnumerator ReachAssist_IsOff_WhileTheHandHoldsATool()
        {
            yield return StandInShip();
            var item = Kind("elec_nav_board");
            ClearAround(item);
            yield return LayOnFloor(item, 0.2f, 0.75f);
            var cutter = m_Player.tools.OfType<TrashAstronauts.Tools.PlasmaCutterTool>().First();
            yield return MoveTo(m_Right, cutter.transform.position, Vector3.forward, 0.6f);
            yield return Grip(m_Right, true, 6);
            Assert.IsTrue(cutter.IsHeld, "Set-up: the cutter is drawn into the right hand.");
            yield return MoveTo(m_Right, LowReach, item.Body.worldCenterOfMass - LowReach);
            yield return Settle(4);
            Assert.IsNull(m_Right.hand.Reach.Candidate, "A hand holding a tool shows nothing.");
            Assert.IsFalse(m_Right.hand.Reach.line.enabled);
            Assert.IsFalse(item.IsHeld);
            Assert.AreEqual(0, m_Right.hand.Reach.AssistedGrabs);
            Assert.IsTrue(cutter.IsHeld, "The tool stays in the hand.");
            yield return Grip(m_Right, false, 6);
        }

        [UnityTest]
        public IEnumerator ReachAssist_HeavyItem_ComesSlowly_LagsBehind_AndNeverSlipsDuringThePull()
        {
            yield return StandInShip();
            var heavy = Kind("reactor_regulator_core");
            var weight = heavy.GetComponent<HeldWeight>();
            ClearAround(heavy);
            yield return MoveTo(m_Right, At(0.2f, 1.0f, 0.2f), Vector3.forward);
            yield return LayOnFloor(heavy, 0.2f, 0.8f);
            yield return MoveTo(m_Right, LowReach, heavy.Body.worldCenterOfMass - LowReach);
            yield return Settle(2);
            Assert.AreSame(heavy, m_Right.hand.Reach.Candidate, Report(m_Right, heavy));
            SetGrip(m_Right, true);
            float start = Time.time, maxGap = 0f, seated = -1f;
            while (Time.time - start < 5f)
            {
                yield return new WaitForFixedUpdate();
                yield return null;
                maxGap = Mathf.Max(maxGap, weight.GripGap);
                Assert.AreEqual(0, weight.SlipCount, "The grip-slip rule must not fire during an assisted pull.");
                if (Seated(m_Right, heavy)) { seated = Time.time - start; break; }
            }
            Assert.Greater(seated, 0f, "The heavy item should still arrive in the palm." + Report(m_Right, heavy) + " gap " + SeatGap(m_Right, heavy).ToString("F3"));
            Assert.Greater(seated, 0.7f, "A 40 kg item comes much more slowly than a light one.");
            Assert.Greater(maxGap, 0.01f, "It trails its grip point on the way (mass lag).");
            Assert.AreEqual(0, m_Right.hand.Reach.StalledPulls);

            // Held, it still lags a quick move of the hand, as any heavy item does.
            Vector3 from = m_Right.hand.interactor.transform.position;
            float lag = 0f;
            for (int i = 1; i <= 8; i++)
            {
                Place(m_Right, from + Vector3.right * (0.04f * i), Vector3.forward);
                yield return new WaitForFixedUpdate();
                yield return null;
                lag = Mathf.Max(lag, weight.GripGap);
            }
            Assert.Greater(lag, 0.02f, "A heavy item still lags the hand once held.");
            Assert.AreEqual(0, weight.SlipCount);
            yield return Grip(m_Right, false);
        }

        [UnityTest]
        public IEnumerator ReachAssist_TakesAReplicaAnotherMachineSimulates_ThroughTheNormalSelection()
        {
            yield return StandInShip();
            var item = Kind("novelty_rubber_duck");
            ClearAround(item);
            yield return MoveTo(m_Right, At(0.2f, 1.0f, 0.2f), Vector3.forward);
            yield return LayOnFloor(item, 0.2f, 0.75f);
            item.SetSimulatedHere(false);
            Assert.IsTrue(item.Body.isKinematic, "Set-up: a replica is kinematic here.");
            var grab = (SalvageGrabInteractable)item.grab;
            int replicaGrabs = grab.ReplicaGrabs;
            var seconds = new float[1];
            yield return TakeFromLowReach(m_Right, item, seconds);
            Assert.AreEqual(replicaGrabs + 1, grab.ReplicaGrabs, "The co-op grab rules ran: the replica became simulated here as it was taken.");
            Assert.IsTrue(item.SimulatedHere && !item.Body.isKinematic);
            Assert.Greater(seconds[0], 0f, "It arrives in the palm like any other." + Report(m_Right, item));
            yield return Grip(m_Right, false);
        }

        /// <summary>A gesture-driven grab button, as HandTrackingDriver plugs into the interactor for a tracked hand.</summary>
        sealed class FistReader : IXRInputButtonReader
        {
            public bool pressed;
            public int pressedFrame = -1, releasedFrame = -1;
            public bool ReadIsPerformed() => pressed;
            public bool ReadWasPerformedThisFrame() => pressedFrame == Time.frameCount;
            public bool ReadWasCompletedThisFrame() => releasedFrame == Time.frameCount;
            public float ReadValue() => pressed ? 1f : 0f;
            public bool TryReadValue(out float value) { value = ReadValue(); return true; }
        }

        [UnityTest]
        public IEnumerator ReachAssist_WorksWithATrackedHandsFist()
        {
            yield return StandInShip();
            var item = Kind("elec_nav_board");
            ClearAround(item);
            yield return MoveTo(m_Right, At(0.2f, 1.0f, 0.2f), Vector3.forward);
            yield return LayOnFloor(item, 0.2f, 0.75f);
            yield return MoveTo(m_Right, LowReach, item.Body.worldCenterOfMass - LowReach);
            yield return Settle(2);
            Assert.AreSame(item, m_Right.hand.Reach.Candidate);

            // The same seams HandTrackingDriver uses: the grip value override and the interactor's select bypass.
            var fist = new FistReader();
            m_Right.hand.interactor.selectInput.bypass = fist;
            m_Right.hand.IsTrackedHand = true;
            try
            {
                fist.pressed = true;
                fist.pressedFrame = Time.frameCount + 1;
                m_Right.hand.GripOverride = 1f;
                float start = Time.time, seated = -1f;
                while (Time.time - start < 1.5f)
                {
                    yield return new WaitForFixedUpdate();
                    yield return null;
                    if (Seated(m_Right, item)) { seated = Time.time - start; break; }
                }
                Assert.Greater(seated, 0f, "A closed fist takes the shown item and it arrives in the palm." + Report(m_Right, item));
                fist.pressed = false;
                fist.releasedFrame = Time.frameCount + 1;
                m_Right.hand.GripOverride = 0f;
                yield return Settle(3);
                Assert.IsFalse(item.IsHeld, "Opening the hand lets it go.");
            }
            finally
            {
                m_Right.hand.interactor.selectInput.bypass = null;
                m_Right.hand.GripOverride = null;
                m_Right.hand.IsTrackedHand = false;
            }
        }

        [UnityTest]
        public IEnumerator DirectGrab_TakesTheItemNearestThePalm_WhenTwoAreInReach()
        {
            yield return StandInShip();
            var near = Kind("elec_nav_board");
            var other = Kind("scrap_hull_plate");
            ClearAround(near, other);
            Vector3 palm = m_Player.HeadPosition + new Vector3(0.25f, -0.4f, 0.35f);
            yield return MoveTo(m_Right, palm, Vector3.forward);
            var filter = m_Right.hand.interactor.GetComponent<NearestGrabFilter>();
            Assert.IsNotNull(filter, "The grab interactor should order its targets by distance to the palm.");
            Vector3 centre = filter.PalmPoint(m_Right.hand.interactor);
            // One item just under the grab volume's centre, the other just above it but further away.
            void Put(SalvageItem item, Vector3 at)
            {
                item.Body.linearVelocity = Vector3.zero;
                item.Body.angularVelocity = Vector3.zero;
                item.transform.SetPositionAndRotation(at, Quaternion.identity);
                item.Body.position = at;
                item.Body.rotation = Quaternion.identity;
            }
            Put(near, centre + Vector3.down * (0.025f + 0.03f));
            Put(other, centre + Vector3.up * (0.025f + 0.075f));
            Physics.SyncTransforms();
            yield return Settle(2);
            Assert.IsTrue(Hovers(m_Right, near) && Hovers(m_Right, other), "Set-up: both are inside the grab volume." + Report(m_Right, near));
            yield return Grip(m_Right, true, 4);
            Assert.IsTrue(near.grab.IsSelected((IXRSelectInteractor)m_Right.hand.interactor), "The squeeze takes the one nearer the palm." + Report(m_Right, near));
            Assert.IsFalse(other.IsHeld);
            Assert.AreEqual(0, m_Right.hand.Reach.AssistedGrabs, "A direct grab, not an assisted one.");
            yield return Grip(m_Right, false);
        }
    
        // ------------------------------------------------------------------ where items come to rest

        /// <summary>
        /// The cave has fallen rocks along the foot of its walls. Salvage sliding along the floor into one must
        /// stop against it, in sight, not end up inside the rock where no hand can see or reach it.
        /// </summary>
        [UnityTest]
        public IEnumerator InTheCave_SalvageSlidingIntoAFallenRock_StopsAgainstIt_NotInsideIt()
        {
            yield return StandInCave();
            var cave = m_Director.maps.First(m => m.mapId == "lava_tube");
            var gravity = cave.GetComponent<MapGravity>();
            // The fallen rocks at the foot of the main tunnel's walls (x = +-1.35), by the hulls loose things meet.
            var rocks = cave.GetComponentsInChildren<MeshCollider>(true)
                .Where(c => c.name == "Rubble Hull" && gravity.Contains(c.bounds.center) && Mathf.Abs(Mathf.Abs(c.bounds.center.x) - 1.35f) < 0.2f)
                .ToList();
            Assert.GreaterOrEqual(rocks.Count, 3, "The main tunnel's fallen rocks should each have a hull that loose things rest against.");
            Assert.IsTrue(rocks.All(r => r.convex && r.gameObject.layer == GameLayers.Rubble));
            var item = Kind("scrap_hull_plate");
            var itemCollider = item.GetComponentsInChildren<Collider>().First(c => !c.isTrigger);
            ClearAround(item);
            var report = new StringBuilder();
            int inside = 0;
            foreach (var rock in rocks)
            {
                Bounds b = rock.bounds;
                Vector3 inward = new Vector3(-Mathf.Sign(b.center.x), 0f, 0f);
                Vector3 start = new Vector3(b.center.x, 0f, b.center.z) + inward * (b.extents.x + 0.45f);
                start.y = HalfHeight(item) + 0.01f;
                item.Body.linearVelocity = Vector3.zero;
                item.Body.angularVelocity = Vector3.zero;
                item.transform.SetPositionAndRotation(start, Quaternion.identity);
                item.Body.position = start;
                item.Body.rotation = Quaternion.identity;
                Physics.SyncTransforms();
                yield return Settle(10);
                item.Body.linearVelocity = -inward * 2.4f; // slides toward the rock and the wall behind it
                for (int i = 0; i < 72 * 4; i++) { yield return new WaitForFixedUpdate(); yield return null; }

                // How deep inside the rock (as an ellipsoid fitted to it) the item's middle is: < 1 is inside.
                Vector3 d = item.Body.worldCenterOfMass - b.center;
                float ellipsoid = Mathf.Sqrt(Sq(d.x / b.extents.x) + Sq(d.y / b.extents.y) + Sq(d.z / b.extents.z));
                float penetration = Physics.ComputePenetration(itemCollider, itemCollider.transform.position, itemCollider.transform.rotation,
                    rock, rock.transform.position, rock.transform.rotation, out _, out float depth) ? depth : 0f;
                report.AppendLine(rock.transform.position.ToString("F2") + " size " + b.size.ToString("F2") + ": item at " + item.transform.position.ToString("F2")
                    + ", ellipsoid distance " + ellipsoid.ToString("F2") + ", overlap " + (penetration * 100f).ToString("F1") + " cm");
                if (ellipsoid < 0.9f || penetration > 0.01f) inside++;
            }
            Debug.Log("[RestingPlace] " + report);
            Assert.AreEqual(0, inside, "Salvage slid into fallen rock and came to rest inside it:\n" + report);
        }

        static float Sq(float x) => x * x;
    }
}

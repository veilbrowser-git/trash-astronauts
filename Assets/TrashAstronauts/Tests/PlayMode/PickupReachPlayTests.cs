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
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;

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
    }
}

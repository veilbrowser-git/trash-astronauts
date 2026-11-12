using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.Tools;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>The player's suit: a torso, arms and legs posed from the head and the two hands, in the real scene.</summary>
    public class AvatarPlayTests
    {
        MissionDirector m_Director;
        PlayerContext m_Player;
        PlayerAvatar m_Avatar;
        Transform m_Head, m_Left, m_Right;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            MissionDirector.StorageOverride = new MemorySaveStorage();
            MissionDirector.SeedOverride = 17;
            MissionDirector.CreatureOverride = 0;
            MissionDirector.MapOverride = 0;
            MissionDirector.ConditionOverride = ContractCondition.Standard;
            BlackoutController.DamageOverride = 0f;
            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            yield return null;
            yield return null;
            m_Director = MissionDirector.Active;
            m_Player = m_Director.Players.First();
            m_Avatar = m_Player.avatar;
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
            foreach (var t in new[] { m_Head, m_Left, m_Right })
            {
                if (t != null) Object.Destroy(t.gameObject);
            }
        }

        /// <summary>
        /// Drives the suit the way a remote player's would be driven: three bare transforms and nothing else.
        /// The component is switched off so only the test steps it.
        /// </summary>
        void UseBareSources(Vector3 headPosition)
        {
            m_Head = new GameObject("Test Head").transform;
            m_Left = new GameObject("Test Left Hand").transform;
            m_Right = new GameObject("Test Right Hand").transform;
            m_Head.position = headPosition;
            m_Left.position = headPosition + new Vector3(-0.3f, -0.5f, 0.3f);
            m_Right.position = headPosition + new Vector3(0.3f, -0.5f, 0.3f);
            m_Avatar.enabled = false;
            m_Avatar.head = m_Head;
            m_Avatar.leftHand = m_Left;
            m_Avatar.rightHand = m_Right;
            m_Avatar.yawSource = null;
            m_Avatar.SnapToHead();
            m_Avatar.Pose(0.02f);
        }

        /// <summary>A property block may hand a colour back as stored (linear) or as set; accept either, exactly.</summary>
        static bool Same(Color actual, Color expected)
        {
            bool Close(Color a, Color b) => Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f && Mathf.Abs(a.b - b.b) < 0.01f;
            return Close(actual, expected) || Close(actual, expected.linear);
        }

        static Vector3 WristEnd(Transform forearm) => forearm.position + forearm.forward * forearm.localScale.z;
        static Vector3 ElbowEnd(Transform upperArm) => upperArm.position + upperArm.forward * upperArm.localScale.z;

        [UnityTest]
        public IEnumerator LocalPlayer_WearsTheSuit_TorsoShown_HelmetHidden()
        {
            Assert.IsNotNull(m_Avatar, "The local player should have an avatar.");
            Assert.AreSame(m_Player.head, m_Avatar.head, "The suit follows the camera.");
            Assert.AreSame(m_Player.leftHand.transform, m_Avatar.leftHand);
            Assert.AreSame(m_Player.rightHand.transform, m_Avatar.rightHand);
            foreach (var part in new[] { m_Avatar.helmet, m_Avatar.torso, m_Avatar.legs, m_Avatar.leftUpperArm, m_Avatar.leftForearm, m_Avatar.rightUpperArm, m_Avatar.rightForearm })
                Assert.IsNotNull(part, "Every part of the suit should be assigned.");
            yield return null;

            var torsoRenderer = m_Avatar.torso.GetComponent<Renderer>();
            Assert.IsNotNull(torsoRenderer);
            Assert.IsTrue(torsoRenderer.enabled && torsoRenderer.gameObject.activeInHierarchy, "The torso should be visible.");
            Assert.IsNotNull(torsoRenderer.GetComponent<MeshFilter>().sharedMesh);
            Assert.Greater(torsoRenderer.GetComponent<MeshFilter>().sharedMesh.triangles.Length / 3, 500, "The torso should be the Blender model, not a stand-in.");
            Assert.IsTrue(m_Avatar.BodyVisible);
            Assert.IsTrue(m_Avatar.bodyRenderers.Length >= 7 && m_Avatar.bodyRenderers.All(r => r != null && r.enabled), "Torso, arms and legs are all shown.");

            Assert.IsFalse(m_Avatar.HelmetVisible, "The wearer's own helmet would block the view.");
            Assert.IsTrue(m_Avatar.helmetRenderers.Length >= 1 && m_Avatar.helmetRenderers.All(r => r != null && !r.enabled));

            // It hangs under the camera, well clear of the near clip plane.
            Vector3 head = m_Player.head.position;
            Vector3 torso = m_Avatar.torso.position;
            Assert.Less(Vector2.Distance(new Vector2(head.x, head.z), new Vector2(torso.x, torso.z)), 0.25f);
            Assert.Less(torso.y, head.y - 0.1f);
            Assert.Greater(torso.y, head.y - 0.4f);
            float near = m_Player.head.GetComponent<Camera>().nearClipPlane;
            foreach (var r in m_Avatar.bodyRenderers)
                Assert.Greater(Mathf.Sqrt(r.bounds.SqrDistance(head)), near + 0.02f, r.name + " is too close to the camera.");

            // Purely visual: nothing to collide with or to simulate.
            Assert.AreEqual(0, m_Avatar.GetComponentsInChildren<Collider>(true).Length);
            Assert.AreEqual(0, m_Avatar.GetComponentsInChildren<Rigidbody>(true).Length);
        }

        [UnityTest]
        public IEnumerator MovingTheHead_CarriesTheTorsoBelowIt_AndTheLegsTrail()
        {
            UseBareSources(new Vector3(40f, 5f, 40f));
            Vector3 before = m_Avatar.torso.position;
            Assert.Less(before.y, m_Head.position.y - 0.1f, "The torso hangs below the head.");
            Assert.Less(Vector2.Distance(new Vector2(before.x, before.z), new Vector2(40f, 40f)), 0.2f);
            Assert.Less(Vector3.Distance(m_Avatar.helmet.position, m_Head.TransformPoint(m_Avatar.headCentreOffset)), 0.001f, "The helmet sits on the head.");
            Assert.Less(Quaternion.Angle(m_Avatar.helmet.rotation, m_Head.rotation), 0.01f);

            Vector3 move = new Vector3(1.5f, 0.4f, -2f);
            m_Head.position += move;
            m_Avatar.SnapToHead();
            m_Avatar.Pose(0.02f);
            Assert.Less(Vector3.Distance(m_Avatar.torso.position, before + move), 0.01f, "The torso moves with the head.");
            Assert.Less(Vector3.Distance(m_Avatar.legs.position, m_Avatar.torso.TransformPoint(m_Avatar.hipOffset)), 0.001f, "The legs hang from the hips.");

            // Turning the head a little does not drag the body round; turning it far does.
            Quaternion facing = m_Avatar.torso.rotation;
            m_Head.rotation = Quaternion.Euler(0f, 15f, 0f);
            for (int i = 0; i < 30; i++) m_Avatar.Pose(0.02f);
            Assert.Less(Quaternion.Angle(facing, m_Avatar.torso.rotation), 1f);
            m_Head.rotation = Quaternion.Euler(0f, 120f, 0f);
            for (int i = 0; i < 60; i++) m_Avatar.Pose(0.02f);
            Assert.Greater(Vector3.Dot(m_Avatar.torso.forward, m_Head.forward), 0.8f, "The body turns to follow where the head faces.");
            // Looking down leans the torso only slightly.
            m_Head.rotation = Quaternion.Euler(70f, 120f, 0f);
            m_Avatar.Pose(0.02f);
            Assert.Less(Vector3.Angle(m_Avatar.torso.up, Vector3.up), 12f);
            Assert.Less(m_Avatar.torso.position.y, m_Head.position.y - 0.12f, "Looking down must not bring the collar up to the eyes.");

            // Gliding forward: the feet lag behind the hips.
            m_Head.rotation = Quaternion.identity;
            m_Avatar.SnapToHead();
            m_Avatar.Pose(0.02f);
            for (int i = 0; i < 60; i++)
            {
                m_Head.position += Vector3.forward * (1.5f * 0.02f);
                m_Avatar.Pose(0.02f);
            }
            Vector3 feet = m_Avatar.legs.rotation * Vector3.down;
            Assert.Less(feet.z, -0.1f, "Moving forward should swing the legs back.");
            Assert.Less(Vector3.Angle(feet, Vector3.down), m_Avatar.maxLegSwing + 12f);
            for (int i = 0; i < 120; i++) m_Avatar.Pose(0.02f);
            Assert.Less(Vector3.Angle(m_Avatar.legs.rotation * Vector3.down, Vector3.down), 3f, "At rest the legs hang straight again.");
            yield return null;
        }

        [UnityTest]
        public IEnumerator Arm_ReachesAHandInReach_AndStaysOnTheShoulderWhenItIsNot()
        {
            UseBareSources(new Vector3(40f, 5f, 40f));
            foreach (bool left in new[] { false, true })
            {
                Transform hand = left ? m_Left : m_Right;
                Transform upper = left ? m_Avatar.leftUpperArm : m_Avatar.rightUpperArm;
                Transform fore = left ? m_Avatar.leftForearm : m_Avatar.rightForearm;
                float side = left ? -1f : 1f;
                string label = left ? "left" : "right";

                // --- in reach: with no wrist offset the forearm must end at the hand itself
                m_Avatar.leftWristOffset = m_Avatar.rightWristOffset = Vector3.zero;
                Vector3[] offsets = { new Vector3(0.08f * side, -0.25f, 0.3f), new Vector3(0.25f * side, 0.1f, 0.25f), new Vector3(-0.15f * side, -0.2f, 0.35f), new Vector3(0.05f * side, -0.45f, -0.1f) };
                foreach (var offset in offsets)
                {
                    m_Avatar.Pose(0.02f);
                    Vector3 shoulder = m_Avatar.GetShoulder(left);
                    hand.position = shoulder + offset;
                    hand.rotation = Quaternion.Euler(20f, 30f * side, 10f);
                    m_Avatar.Pose(0.02f);
                    shoulder = m_Avatar.GetShoulder(left);
                    Assert.Less(Vector3.Distance(hand.position, shoulder), m_Avatar.ArmReach, "Test point should be in reach.");
                    Assert.Less(Vector3.Distance(WristEnd(fore), hand.position), 0.03f, "The " + label + " wrist should reach a hand at " + offset);
                    Assert.Less(Vector3.Distance(m_Avatar.GetWrist(left), WristEnd(fore)), 0.002f);
                    Assert.Less(Vector3.Distance(upper.position, shoulder), 0.001f, "The upper arm starts at the shoulder.");
                    Assert.Less(Vector3.Distance(ElbowEnd(upper), fore.position), 0.002f, "The arm is joined at the elbow.");
                    Assert.Less(Vector3.Distance(fore.position, m_Avatar.GetElbow(left)), 0.001f);
                    Assert.AreEqual(m_Avatar.upperArmLength, upper.localScale.z, 0.0001f, "Segments keep their length.");
                    Assert.AreEqual(m_Avatar.forearmLength, fore.localScale.z, 0.0001f);
                    // the elbow bends outward and down, never up through the shoulder or in through the chest
                    Vector3 elbowLocal = m_Avatar.torso.InverseTransformPoint(m_Avatar.GetElbow(left)) - new Vector3(m_Avatar.shoulderOffset.x * side, m_Avatar.shoulderOffset.y, m_Avatar.shoulderOffset.z);
                    Vector3 midLocal = m_Avatar.torso.InverseTransformPoint((shoulder + hand.position) * 0.5f) - new Vector3(m_Avatar.shoulderOffset.x * side, m_Avatar.shoulderOffset.y, m_Avatar.shoulderOffset.z);
                    Vector3 bend = elbowLocal - midLocal;
                    Assert.Greater(bend.x * side * 0.75f - bend.y, 0f, "The " + label + " elbow should bend outward and down for a hand at " + offset);
                }

                // --- with the game's wrist offset the forearm ends where the glove's cuff is
                m_Avatar.leftWristOffset = new Vector3(-0.035f, -0.035f, -0.18f);
                m_Avatar.rightWristOffset = new Vector3(0.035f, -0.035f, -0.18f);
                hand.position = m_Avatar.GetShoulder(left) + new Vector3(0.05f * side, -0.3f, 0.35f);
                hand.rotation = Quaternion.identity;
                m_Avatar.Pose(0.02f);
                Vector3 cuff = hand.TransformPoint(left ? m_Avatar.leftWristOffset : m_Avatar.rightWristOffset);
                Assert.Less(Vector3.Distance(m_Avatar.GetWristTarget(left), cuff), 0.0001f);
                Assert.Less(Vector3.Distance(WristEnd(fore), cuff), 0.03f, "The forearm should end at the glove's cuff.");

                // --- out of reach: the arm stays whole, on the shoulder, pointing at the hand
                hand.position = m_Avatar.GetShoulder(left) + new Vector3(1.2f * side, 0.8f, 2.5f);
                m_Avatar.Pose(0.02f);
                Vector3 s = m_Avatar.GetShoulder(left);
                Vector3 target = m_Avatar.GetWristTarget(left);
                Assert.Greater(Vector3.Distance(s, target), m_Avatar.ArmReach + 1f);
                Assert.Less(Vector3.Distance(upper.position, s), 0.001f, "Out of reach, the " + label + " arm must stay on the shoulder.");
                Assert.Less(Vector3.Distance(ElbowEnd(upper), fore.position), 0.002f);
                Assert.LessOrEqual(Vector3.Distance(WristEnd(fore), s), m_Avatar.ArmReach + 0.001f, "The arm must not stretch.");
                Assert.Greater(Vector3.Distance(WristEnd(fore), s), m_Avatar.ArmReach - 0.01f, "The arm should be straight.");
                Assert.Less(Vector3.Angle(WristEnd(fore) - s, target - s), 1f, "The arm should point at the hand.");
                Assert.AreEqual(m_Avatar.upperArmLength, upper.localScale.z, 0.0001f);
                Assert.AreEqual(m_Avatar.forearmLength, fore.localScale.z, 0.0001f);
            }

            // Missing sources are tolerated: an arm with no hand hangs at rest, and no head leaves the suit where it was.
            m_Avatar.leftHand = null;
            m_Avatar.rightHand = null;
            Assert.DoesNotThrow(() => m_Avatar.Pose(0.02f));
            Assert.Less(Vector3.Distance(m_Avatar.rightUpperArm.position, m_Avatar.GetShoulder(false)), 0.001f);
            Assert.Less(m_Avatar.GetWrist(false).y, m_Avatar.GetShoulder(false).y - 0.3f, "A hand with no source rests below its shoulder.");
            Vector3 torsoBefore = m_Avatar.torso.position;
            m_Avatar.head = null;
            Assert.DoesNotThrow(() => m_Avatar.Pose(0.02f));
            Assert.AreEqual(torsoBefore, m_Avatar.torso.position);
            yield return null;
        }

        static bool Finite(Vector3 v) => !float.IsNaN(v.x + v.y + v.z) && !float.IsInfinity(v.x + v.y + v.z);
        static bool Finite(Quaternion q) => !float.IsNaN(q.x + q.y + q.z + q.w) && !float.IsInfinity(q.x + q.y + q.z + q.w);

        /// <summary>Both arms are finite, on their shoulders, joined at the elbow, and not stretched.</summary>
        void AssertArmsWhole(string when)
        {
            foreach (bool left in new[] { false, true })
            {
                Transform upper = left ? m_Avatar.leftUpperArm : m_Avatar.rightUpperArm;
                Transform fore = left ? m_Avatar.leftForearm : m_Avatar.rightForearm;
                string label = (left ? "left" : "right") + " arm, " + when;
                Assert.IsTrue(Finite(upper.position) && Finite(upper.rotation) && Finite(fore.position) && Finite(fore.rotation), "Not a number in the " + label);
                Assert.IsTrue(Finite(m_Avatar.GetElbow(left)) && Finite(m_Avatar.GetWrist(left)), "Not a number in the " + label);
                Assert.Less(Mathf.Abs(Quaternion.Dot(upper.rotation, upper.rotation) - 1f), 0.001f, "Bad rotation on the " + label);
                Assert.Less(Vector3.Distance(upper.position, m_Avatar.GetShoulder(left)), 0.001f, "Off the shoulder: " + label);
                Assert.Less(Vector3.Distance(ElbowEnd(upper), fore.position), 0.002f, "Broken at the elbow: " + label);
                Assert.LessOrEqual(Vector3.Distance(WristEnd(fore), m_Avatar.GetShoulder(left)), m_Avatar.ArmReach + 0.001f, "Stretched: " + label);
                Assert.AreEqual(m_Avatar.upperArmLength, upper.localScale.z, 0.0001f, label);
                Assert.AreEqual(m_Avatar.forearmLength, fore.localScale.z, 0.0001f, label);
            }
            foreach (var part in new[] { m_Avatar.helmet, m_Avatar.torso, m_Avatar.legs })
                Assert.IsTrue(Finite(part.position) && Finite(part.rotation), "Not a number in " + part.name + ", " + when);
        }

        [UnityTest]
        public IEnumerator Arms_SurviveAwkwardTargets_HandOnTheShoulder_AlongTheElbowLine_ZeroTimeStep()
        {
            UseBareSources(new Vector3(40f, 5f, 40f));
            m_Avatar.leftWristOffset = m_Avatar.rightWristOffset = Vector3.zero;
            m_Avatar.Pose(0.02f);

            // a hand exactly on its shoulder: there is no direction to aim along
            m_Left.position = m_Avatar.GetShoulder(true);
            m_Right.position = m_Avatar.GetShoulder(false);
            m_Avatar.Pose(0.02f);
            AssertArmsWhole("hand on the shoulder");

            // a hand exactly along the line the elbow bends towards: there is no sideways direction left to bend in
            Vector3 pole = m_Avatar.elbowDirection.normalized;
            m_Right.position = m_Avatar.GetShoulder(false) + m_Avatar.torso.rotation * pole * 0.4f;
            m_Left.position = m_Avatar.GetShoulder(true) + m_Avatar.torso.rotation * new Vector3(-pole.x, pole.y, pole.z) * 0.4f;
            m_Avatar.Pose(0.02f);
            AssertArmsWhole("hand along the elbow line");
            Assert.Less(Vector3.Distance(WristEnd(m_Avatar.rightForearm), m_Right.position), 0.03f, "It is in reach, so the wrist should still get there.");
            Assert.Less(Vector3.Distance(WristEnd(m_Avatar.leftForearm), m_Left.position), 0.03f);

            // closer than the arm can fold, and a hand a long way off
            m_Right.position = m_Avatar.GetShoulder(false) + new Vector3(0f, 0f, 0.005f);
            m_Left.position = m_Avatar.GetShoulder(true) + new Vector3(-300f, 40f, 900f);
            m_Avatar.Pose(0.02f);
            AssertArmsWhole("hand too close and hand far away");

            // no time passing, time running backwards, a very long frame; head upside down and straight up
            foreach (float dt in new[] { 0f, -0.5f, 5f })
            {
                m_Head.position += new Vector3(0.3f, 0.1f, -0.2f);
                m_Avatar.Pose(dt);
                AssertArmsWhole("time step " + dt);
            }
            foreach (var rotation in new[] { Quaternion.Euler(-90f, 0f, 0f), Quaternion.Euler(90f, 0f, 0f), Quaternion.Euler(0f, 0f, 180f), Quaternion.Euler(179f, 30f, 0f) })
            {
                m_Head.rotation = rotation;
                m_Avatar.SnapToHead();
                m_Avatar.Pose(0.02f);
                AssertArmsWhole("head at " + rotation.eulerAngles);
            }

            // switched off, it leaves the suit alone
            m_Head.rotation = Quaternion.identity;
            m_Avatar.Pose(0.02f);
            Vector3 torsoBefore = m_Avatar.torso.position;
            m_Head.position += Vector3.right * 3f;
            Assert.IsFalse(m_Avatar.enabled);
            yield return null;
            yield return null;
            Assert.AreEqual(torsoBefore, m_Avatar.torso.position, "A disabled avatar must not pose itself.");
        }

        [UnityTest]
        public IEnumerator SeenFromOutside_TheCollarStaysUnderTheHelmet_AndTheBodyKeepsItsHeadingWhenTheHeadNods()
        {
            UseBareSources(new Vector3(40f, 5f, 40f));

            // --- a crew mate's suit (helmet shown): however the head nods or tilts, the neck ring stays seated under the helmet
            m_Avatar.SetHelmetVisible(true);
            foreach (var euler in new[] { Vector3.zero, new Vector3(35f, 0f, 0f), new Vector3(70f, 40f, 0f), new Vector3(-40f, 10f, 0f), new Vector3(0f, 20f, 30f), new Vector3(89f, 0f, 0f), new Vector3(60f, 200f, 20f) })
            {
                m_Head.rotation = Quaternion.Euler(euler);
                m_Avatar.SnapToHead();
                m_Avatar.Pose(0.02f);
                Vector3 centre = m_Head.TransformPoint(m_Avatar.headCentreOffset);
                Assert.Less(Vector3.Distance(m_Avatar.helmet.position, centre), 0.001f, "The helmet follows the head exactly, head at " + euler);
                Assert.Less(Quaternion.Angle(m_Avatar.helmet.rotation, m_Head.rotation), 0.01f);
                Assert.Less(Vector3.Distance(m_Avatar.torso.position, centre + Vector3.down * m_Avatar.neckDrop), 0.001f, "The collar should sit right under the helmet, head at " + euler);
                Assert.Less(Vector3.Angle(m_Avatar.torso.up, Vector3.up), 12f, "The torso only leans slightly, head at " + euler);
            }

            // --- the wearer's own suit (helmet hidden): the collar keeps clear of the eyes instead, and well behind them
            m_Avatar.SetHelmetVisible(false);
            foreach (float pitch in new[] { 0f, 30f, 60f, 89f, -60f })
            {
                m_Head.rotation = Quaternion.Euler(pitch, 0f, 0f);
                m_Avatar.SnapToHead();
                m_Avatar.Pose(0.02f);
                Vector3 fromEyes = m_Avatar.torso.position - m_Head.position;
                Assert.Less(fromEyes.y, -0.15f, "Looking " + pitch + " degrees down must not bring the collar up to the eyes.");
                Assert.Less(fromEyes.z, -0.08f, "Looking " + pitch + " degrees down, the neck stays behind the eyes.");
            }

            // --- heading: nodding right down, through straight down and past it, must not swing the body round.
            // (The flattened forward direction shrinks to nothing at straight down and flips beyond it.)
            m_Avatar.SetHelmetVisible(true);
            Quaternion facingEast = Quaternion.Euler(0f, 90f, 0f);
            m_Head.rotation = facingEast;
            m_Avatar.SnapToHead();
            m_Avatar.Pose(0.02f);
            Assert.Less(Vector3.Angle(m_Avatar.torso.forward, Vector3.right), 2f);
            for (int pitch = 0; pitch <= 104; pitch += 2)
            {
                m_Head.rotation = facingEast * Quaternion.Euler(pitch, 0f, 0f) * Quaternion.Euler(0f, 0f, 6f * Mathf.Sin(pitch));
                for (int i = 0; i < 5; i++) m_Avatar.Pose(0.02f);
                Vector3 flat = Vector3.ProjectOnPlane(m_Avatar.torso.forward, Vector3.up);
                Assert.Less(Vector3.Angle(flat, Vector3.right), m_Avatar.yawDeadzone + 5f, "The body swung round when the head nodded " + pitch + " degrees down.");
            }
            for (int pitch = 0; pitch >= -104; pitch -= 2)
            {
                m_Head.rotation = facingEast * Quaternion.Euler(pitch, 0f, 0f);
                for (int i = 0; i < 5; i++) m_Avatar.Pose(0.02f);
                Vector3 flat = Vector3.ProjectOnPlane(m_Avatar.torso.forward, Vector3.up);
                Assert.Less(Vector3.Angle(flat, Vector3.right), m_Avatar.yawDeadzone + 5f, "The body swung round when the head tipped " + (-pitch) + " degrees back.");
            }
            // A suit that first appears with its head straight down still faces the way the wearer does.
            m_Head.rotation = Quaternion.Euler(0f, -90f, 0f);
            m_Avatar.SnapToHead();
            m_Avatar.Pose(0.02f);
            m_Head.rotation = facingEast * Quaternion.Euler(90f, 0f, 0f);
            m_Avatar.SnapToHead();
            m_Avatar.Pose(0.02f);
            Assert.Less(Vector3.Angle(Vector3.ProjectOnPlane(m_Avatar.torso.forward, Vector3.up), Vector3.right), 5f, "Head straight down: the body should face the way the top of the head points.");
            yield return null;
        }

        /// <summary>True if the straight line from a to b passes through the mesh.</summary>
        static bool Blocks(MeshFilter filter, Vector3 a, Vector3 b)
        {
            Transform t = filter.transform;
            Vector3 origin = t.InverseTransformPoint(a);
            Vector3 d = t.InverseTransformPoint(b) - origin;
            var vertices = filter.sharedMesh.vertices;
            var triangles = filter.sharedMesh.triangles;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 p0 = vertices[triangles[i]];
                Vector3 e1 = vertices[triangles[i + 1]] - p0, e2 = vertices[triangles[i + 2]] - p0;
                Vector3 h = Vector3.Cross(d, e2);
                float det = Vector3.Dot(e1, h);
                if (Mathf.Abs(det) < 1e-12f) continue;
                Vector3 s = origin - p0;
                float u = Vector3.Dot(s, h) / det;
                if (u < 0f || u > 1f) continue;
                Vector3 q = Vector3.Cross(s, e1);
                float v = Vector3.Dot(d, q) / det;
                if (v < 0f || u + v > 1f) continue;
                float along = Vector3.Dot(e2, q) / det;
                if (along > 0.001f && along < 0.999f) return true;
            }
            return false;
        }

        [UnityTest]
        public IEnumerator LookingDown_TheWearerSeesEveryHolsteredTool_PastTheirOwnChest()
        {
            Assert.IsNotNull(m_Avatar);
            Assert.GreaterOrEqual(m_Player.tools.Length, 4);
            var body = new[] { m_Avatar.torso, m_Avatar.legs }.SelectMany(part => part.GetComponentsInChildren<MeshFilter>(true)).Where(f => f.GetComponent<Renderer>().enabled).ToArray();
            Assert.GreaterOrEqual(body.Length, 2);
            float near = m_Player.head.GetComponent<Camera>().nearClipPlane;
            // No headset in a test run: stop the pose drivers so the head stays where the test turns it.
            foreach (var driver in m_Player.GetComponentsInChildren<UnityEngine.InputSystem.XR.TrackedPoseDriver>()) driver.enabled = false;
            // A tool counts as "in view" when it is within 50 degrees of where the wearer is looking.
            var seen = new System.Collections.Generic.HashSet<SuitTool>();
            var report = new System.Text.StringBuilder();
            foreach (float pitch in new[] { 0f, 30f, 45f, 60f, 75f, 88f })
            {
                m_Player.head.localRotation = Quaternion.Euler(pitch, 0f, 0f);
                yield return null;
                yield return null;
                Assert.AreEqual(-Mathf.Sin(pitch * Mathf.Deg2Rad), m_Player.head.forward.y, 0.02f, "The test could not turn the head.");
                Assert.IsFalse(m_Avatar.HelmetVisible);
                Vector3 eyes = m_Player.head.position;
                foreach (var tool in m_Player.tools)
                {
                    Assert.IsFalse(tool.IsHeld, "Tools start in their holsters.");
                    Vector3 at = tool.transform.position;
                    float offAxis = Vector3.Angle(m_Player.head.forward, at - eyes);
                    if (offAxis > 50f) continue;
                    var hiding = body.FirstOrDefault(filter => Blocks(filter, eyes, at));
                    report.Append(tool.name).Append(" at ").Append(pitch).Append(" deg: ").Append(hiding != null ? "hidden by " + hiding.name : "seen").Append("; ");
                    Assert.IsNull(hiding, "Looking " + pitch + " degrees down, the suit's " + (hiding != null ? hiding.name : "") + " hides the holstered " + tool.name + ". " + report);
                    seen.Add(tool);
                }
                // Nothing of the torso or legs comes anywhere near the near clip plane (measured to the surface itself;
                // a bounding box is far too loose once the torso leans).
                foreach (var filter in body)
                {
                    float nearest = float.MaxValue;
                    foreach (var vertex in filter.sharedMesh.vertices)
                        nearest = Mathf.Min(nearest, Vector3.Distance(filter.transform.TransformPoint(vertex), eyes));
                    report.Append(filter.name).Append(" nearest ").Append(nearest.ToString("F3")).Append("; ");
                    Assert.Greater(nearest, near + 0.07f, "Looking " + pitch + " degrees down, the suit's " + filter.name + " is at the camera's near plane. " + report);
                }
            }
            foreach (var tool in m_Player.tools)
                Assert.IsTrue(seen.Contains(tool), "The holstered " + tool.name + " never came into view when looking down. " + report);
            Debug.Log("[AvatarPlayTests] holsters: " + report);
        }

        [UnityTest]
        public IEnumerator Tint_And_Visibility_CanBeSetPerPlayer()
        {
            Assert.AreEqual(4, PlayerAvatar.CrewColors.Length);
            for (int i = 0; i < 4; i++)
            {
                for (int j = i + 1; j < 4; j++)
                {
                    Color a = PlayerAvatar.CrewColors[i], b = PlayerAvatar.CrewColors[j];
                    Assert.Greater(Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b), 0.6f, "Crew colours " + i + " and " + j + " look alike.");
                }
            }

            Assert.GreaterOrEqual(m_Avatar.accentRenderers.Length, 5, "Helmet stripe, chest stripe, arm bands and leg bands.");
            int baseColor = Shader.PropertyToID("_BaseColor");
            var block = new MaterialPropertyBlock();
            m_Avatar.accentRenderers[0].GetPropertyBlock(block);
            Color before = block.GetColor(baseColor);
            Assert.IsTrue(Same(before, PlayerAvatar.CrewColors[0]), "The suit starts in the first crew colour, got " + before);
            Color blue = PlayerAvatar.CrewColors[1];
            m_Avatar.SetTint(blue);
            Assert.AreEqual(blue, m_Avatar.Tint);
            foreach (var r in m_Avatar.accentRenderers)
            {
                Assert.IsTrue(r.sharedMaterial.HasProperty(baseColor), r.name + " needs a material with a base colour.");
                r.GetPropertyBlock(block);
                Assert.IsTrue(Same(block.GetColor(baseColor), blue), r.name + " did not take the tint: " + block.GetColor(baseColor));
                Assert.IsFalse(Same(block.GetColor(baseColor), before), r.name + " kept its old colour.");
            }
            // The white cloth must not be tinted along with the accents.
            var accentMaterial = m_Avatar.accentRenderers[0].sharedMaterial;
            foreach (var r in m_Avatar.bodyRenderers.Concat(m_Avatar.helmetRenderers).Except(m_Avatar.accentRenderers))
                Assert.AreNotSame(accentMaterial, r.sharedMaterial, r.name + " shares the accent material.");
            m_Avatar.SetTint(PlayerAvatar.CrewColors[3]);
            m_Avatar.accentRenderers[0].GetPropertyBlock(block);
            Assert.IsTrue(Same(block.GetColor(baseColor), PlayerAvatar.CrewColors[3]));

            m_Avatar.SetHelmetVisible(true);
            Assert.IsTrue(m_Avatar.HelmetVisible);
            Assert.IsTrue(m_Avatar.helmetRenderers.All(r => r.enabled), "SetHelmetVisible(true) should show the helmet.");
            Assert.IsTrue(m_Avatar.bodyRenderers.All(r => r.enabled));
            yield return null;
            Assert.Less(Vector3.Distance(m_Avatar.helmet.position, m_Player.head.TransformPoint(m_Avatar.headCentreOffset)), 0.01f, "The helmet sits on the head.");

            m_Avatar.SetBodyVisible(false);
            Assert.IsTrue(m_Avatar.bodyRenderers.All(r => !r.enabled));
            Assert.IsTrue(m_Avatar.helmetRenderers.All(r => r.enabled), "Hiding the body leaves the helmet alone.");
            m_Avatar.SetHelmetVisible(false);
            m_Avatar.SetBodyVisible(true);
            Assert.IsTrue(m_Avatar.helmetRenderers.All(r => !r.enabled));
            Assert.IsTrue(m_Avatar.bodyRenderers.All(r => r.enabled));
        }
    }
}

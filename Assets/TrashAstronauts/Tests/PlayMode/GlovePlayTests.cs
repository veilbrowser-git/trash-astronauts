using System.Collections;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>The EVA gloves in the real scene: finger parts on both hands, and how the fingers follow the inputs.</summary>
    public class GlovePlayTests
    {
        MissionDirector m_Director;
        PlayerContext m_Player;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            MissionDirector.StorageOverride = new MemorySaveStorage();
            MissionDirector.SeedOverride = 23;
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
            if (m_Player != null)
            {
                foreach (var hand in Hands())
                {
                    hand.GripOverride = null;
                    hand.TriggerOverride = null;
                }
            }
            MissionDirector.StorageOverride = null;
            MissionDirector.SeedOverride = null;
            MissionDirector.CreatureOverride = null;
            MissionDirector.MapOverride = null;
            MissionDirector.ConditionOverride = null;
            BlackoutController.DamageOverride = null;
        }

        HandRig[] Hands() => new[] { m_Player.leftHand, m_Player.rightHand };

        static GloveVisual Glove(HandRig hand) => hand.GetComponentInChildren<GloveVisual>(true);

        /// <summary>How far a joint is curled from straight, in degrees about its own X (toward the palm is positive).</summary>
        static float Curl(Transform joint, Quaternion straight)
        {
            Vector3 f = (Quaternion.Inverse(straight) * joint.localRotation) * Vector3.forward;
            return Mathf.Atan2(-f.y, f.z) * Mathf.Rad2Deg;
        }

        static float FingerCurl(GloveVisual glove, int finger, int segment) => Curl(glove.fingerJoints[finger * 3 + segment], glove.fingerBase[finger * 3 + segment]);
        static float ThumbCurl(GloveVisual glove, int segment) => Curl(glove.thumbJoints[segment], glove.thumbBase[segment]);

        IEnumerator Drive(float trigger, float grip)
        {
            foreach (var hand in Hands())
            {
                hand.TriggerOverride = trigger;
                hand.GripOverride = grip;
            }
            yield return new WaitForSecondsRealtime(0.6f);
            yield return null;
        }

        [UnityTest]
        public IEnumerator BothHands_HaveEveryFingerSegment_AsVisibleRigidParts()
        {
            yield return null;
            foreach (var hand in Hands())
            {
                var glove = Glove(hand);
                Assert.IsNotNull(glove, hand.name);
                Assert.IsTrue(glove.IsModelled, hand.name + " should wear the modelled glove.");
                Assert.AreSame(hand, glove.hand);
                Assert.AreEqual(12, glove.fingerJoints.Length);
                Assert.AreEqual(3, glove.thumbJoints.Length);
                foreach (var joint in glove.fingerJoints.Concat(glove.thumbJoints))
                {
                    Assert.IsNotNull(joint);
                    Assert.IsTrue(joint.gameObject.activeInHierarchy, joint.name);
                    var renderer = joint.GetComponent<MeshRenderer>();
                    Assert.IsTrue(renderer != null && renderer.enabled, joint.name + " should be drawn.");
                    Assert.Greater(joint.GetComponent<MeshFilter>().sharedMesh.triangles.Length, 0, joint.name);
                    Assert.IsTrue(joint.IsChildOf(glove.transform));
                }
                // Each segment hangs off the one before it, so curling a knuckle carries the rest of the finger.
                for (int f = 0; f < 4; f++)
                {
                    Assert.AreSame(glove.fingerJoints[f * 3], glove.fingerJoints[f * 3 + 1].parent);
                    Assert.AreSame(glove.fingerJoints[f * 3 + 1], glove.fingerJoints[f * 3 + 2].parent);
                }
            }
        }

        [UnityTest]
        public IEnumerator Idle_HandsRestRelaxed_FingersSlightlyCurled_ThumbOut()
        {
            yield return Drive(0f, 0f);
            foreach (var hand in Hands())
            {
                var glove = Glove(hand);
                for (int f = 0; f < 4; f++)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        float curl = FingerCurl(glove, f, k);
                        Assert.AreEqual(glove.restCurl[k], curl, 1f, hand.name + " finger " + f + " segment " + k + " should be at rest.");
                        Assert.Greater(curl, 3f, "A relaxed hand is slightly curled, not flat.");
                        Assert.Less(curl, 25f, "A relaxed hand is open.");
                    }
                }
                Assert.Less(ThumbCurl(glove, 0), 0f, hand.name + ": a relaxed thumb sits out from the palm.");
                Assert.AreEqual(0f, glove.IndexAmount, 0.01f);
                Assert.AreEqual(0f, glove.GripAmount, 0.01f);
            }
        }

        [UnityTest]
        public IEnumerator Trigger_CurlsOnlyTheIndexFinger()
        {
            yield return Drive(1f, 0f);
            foreach (var hand in Hands())
            {
                var glove = Glove(hand);
                Assert.Greater(FingerCurl(glove, 0, 0), 60f, hand.name + ": the trigger should curl the index finger.");
                Assert.Greater(FingerCurl(glove, 0, 1), 80f);
                for (int f = 1; f < 4; f++)
                    for (int k = 0; k < 3; k++)
                        Assert.AreEqual(glove.restCurl[k], FingerCurl(glove, f, k), 1f, hand.name + ": the trigger must not move finger " + f + ".");
                for (int k = 0; k < 3; k++)
                    Assert.AreEqual(glove.thumbRestCurl[k], ThumbCurl(glove, k), 1f, hand.name + ": the trigger must not move the thumb.");
            }
        }

        [UnityTest]
        public IEnumerator Grip_CurlsTheOtherFingers_AndClosesTheThumb_SmoothlyAndWithinLimits()
        {
            yield return Drive(0f, 0f);
            foreach (var hand in Hands()) hand.GripOverride = 1f;
            yield return null;
            yield return null;
            // Part way: blending, not snapping.
            var right = Glove(m_Player.rightHand);
            float early = FingerCurl(right, 2, 1);
            Assert.Less(early, right.fistCurl[1] - 1f, "The fingers should blend toward the fist, not snap.");

            yield return Drive(0f, 1f);
            foreach (var hand in Hands())
            {
                var glove = Glove(hand);
                for (int f = 1; f < 4; f++)
                {
                    Assert.Greater(FingerCurl(glove, f, 0), 70f, hand.name + ": grip should curl finger " + f + ".");
                    Assert.Greater(FingerCurl(glove, f, 1), 90f);
                    for (int k = 0; k < 3; k++) Assert.LessOrEqual(FingerCurl(glove, f, k), glove.maxCurl + 0.5f, "Per-joint limit.");
                }
                Assert.Greater(ThumbCurl(glove, 0) - glove.thumbRestCurl[0], 25f, hand.name + ": grip should close the thumb.");
                Assert.Greater(ThumbCurl(glove, 2) - glove.thumbRestCurl[2], 20f);
                for (int k = 0; k < 3; k++)
                    Assert.AreEqual(glove.restCurl[k], FingerCurl(glove, 0, k), 1f, hand.name + ": grip alone leaves the index on the trigger.");

                // A closed fist: the fingertips come back toward the palm.
                Vector3 tip = glove.transform.InverseTransformPoint(glove.fingerJoints[5].TransformPoint(new Vector3(0f, 0f, 0.02f)));
                Assert.Less(tip.z, 0f, hand.name + ": the middle fingertip should fold back over the palm.");
            }

            // Released: back to rest.
            yield return Drive(0f, 0f);
            foreach (var hand in Hands())
                Assert.AreEqual(Glove(hand).restCurl[0], FingerCurl(Glove(hand), 2, 0), 1f);
        }

        /// <summary>A box in the glove's own space (palm -Y, fingers +Z), with a collider, for the fingers to close on.</summary>
        static BoxCollider BoxInGlove(GloveVisual glove, Vector3 centre, Vector3 size)
        {
            var box = new GameObject("Held Box").AddComponent<BoxCollider>();
            box.transform.SetPositionAndRotation(glove.transform.TransformPoint(centre), glove.transform.rotation);
            box.size = size;
            return box;
        }

        static float NearestFingerGap(GloveVisual glove, int finger, Collider col)
        {
            float best = float.MaxValue;
            for (int k = 0; k < 3; k++)
            {
                Transform joint = finger < 4 ? glove.fingerJoints[finger * 3 + k] : glove.thumbJoints[k];
                for (float z = 0f; z <= 0.03f; z += 0.005f)
                {
                    Vector3 p = joint.TransformPoint(new Vector3(0f, -0.004f, z));
                    best = Mathf.Min(best, Vector3.Distance(col.ClosestPoint(p), p));
                }
            }
            return best;
        }

        [UnityTest]
        public IEnumerator Holding_FingersCloseUntilTheyMeetTheObject_AndMakeAFirmFistOnATool()
        {
            yield return Drive(0f, 0f);
            var glove = Glove(m_Player.rightHand);

            // A big block against the palm: the fingers stop on it, part closed.
            var big = BoxInGlove(glove, new Vector3(0f, -0.1f, -0.04f), new Vector3(0.1f, 0.05f, 0.14f));
            Physics.SyncTransforms();
            glove.FitTo(new Collider[] { big }, false);
            float[] bigHold = new float[5];
            for (int f = 0; f < 5; f++) bigHold[f] = glove.HoldAmount(f);
            string held = string.Join(", ", bigHold);
            for (int f = 0; f < 4; f++)
            {
                Assert.Greater(bigHold[f], 0f, "Finger " + f + " should close some way onto the block (" + held + ").");
                Assert.Less(bigHold[f], 0.95f, "Finger " + f + " should stop on the block, not close into a fist through it.");
            }
            glove.ApplyFingers(bigHold);
            for (int f = 0; f < 4; f++)
                Assert.Less(NearestFingerGap(glove, f, big), 0.02f, "Finger " + f + " should end up touching the block.");
            Object.Destroy(big.gameObject);

            // A thin rod across the fingers closes them further than the block does.
            var rod = BoxInGlove(glove, new Vector3(0f, -0.08f, -0.035f), new Vector3(0.1f, 0.02f, 0.02f));
            Physics.SyncTransforms();
            glove.FitTo(new Collider[] { rod }, false);
            Assert.Greater(glove.HoldAmount(1), bigHold[1] + 0.05f, "A thin handle lets the fingers close further.");
            Object.Destroy(rod.gameObject);

            // A tool: a firm fist, whatever its shape.
            glove.FitTo(null, true);
            for (int f = 0; f < 5; f++) Assert.AreEqual(1f, glove.HoldAmount(f));
            yield return null;
        }

        [UnityTest]
        public IEnumerator GrabPoint_StaysAtThePalm_OfTheModelledGlove()
        {
            yield return Drive(0f, 1f);
            foreach (var hand in Hands())
            {
                var glove = Glove(hand);
                Transform palm = glove.transform.Find("Model/palm");
                Assert.IsNotNull(palm, hand.name + " should have a palm part.");
                var bounds = palm.GetComponent<MeshRenderer>().bounds;
                Vector3 attach = hand.interactor.attachTransform.position;
                Vector3 palmCentre = glove.transform.TransformPoint(new Vector3(0f, -0.03f, -0.075f));
                Assert.Less(Vector3.Distance(hand.interactor.transform.position, palmCentre), 0.06f, hand.name + ": the grab point should be at the palm.");
                Assert.Less(Vector3.Distance(attach, bounds.ClosestPoint(attach)), 0.05f, hand.name + ": the attach point should be against the palm.");
                Vector3 local = hand.transform.InverseTransformPoint(hand.interactor.transform.position);
                Assert.Less(local.z, -0.06f, "The grab point should be back at the palm, behind the finger joints.");
                Assert.AreEqual(new Vector3(0f, -0.035f, -0.075f), hand.interactor.transform.localPosition);
            }
        }
    }
}

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Player;
using UnityEditor;
using UnityEngine;

namespace TrashAstronauts.Tests
{
    /// <summary>The EVA gloves: the files Blender exports (ModelSource/Player/build_hands.py) and the hands built from them.</summary>
    public class GloveModelTests
    {
        const string Dir = "ModelSource/Player";
        const string Id = "glove";

        static readonly string[] k_Segments =
        {
            "index_1", "index_2", "index_3", "middle_1", "middle_2", "middle_3", "ring_1", "ring_2", "ring_3",
            "little_1", "little_2", "little_3", "thumb_1", "thumb_2", "thumb_3",
        };

        [Serializable]
        class PartInfo
        {
            public string name, parent;
            public float[] pivot, rotation;
            public int vertexStart, vertexCount, indexStart, indexCount;
        }

        [Serializable]
        class ModelInfo
        {
            public string id;
            public int vertexTotal, indexTotal, triangles;
            public PartInfo[] parts;
        }

        static ModelInfo Load()
        {
            string path = Dir + "/" + Id + ".mesh.json";
            Assert.IsTrue(File.Exists(path), path + " is missing. Run Blender on " + Dir + "/build_hands.py.");
            return JsonUtility.FromJson<ModelInfo>(File.ReadAllText(path));
        }

        [Test]
        public void GloveModel_IsExported_WithEveryFingerSegment_WithinTheTriangleBudget()
        {
            foreach (string file in new[] { "build_hands.py", Id + ".blend", Id + ".mesh.json", Id + ".mesh.bin", Id + "_atlas.png" })
                Assert.IsTrue(File.Exists(Dir + "/" + file), Dir + "/" + file + " is missing.");

            var info = Load();
            Assert.AreEqual(Id, info.id);
            var names = info.parts.Select(p => p.name).ToArray();
            CollectionAssert.Contains(names, "palm");
            foreach (string part in k_Segments) CollectionAssert.Contains(names, part, "The glove needs a part called " + part);
            Assert.AreEqual(names.Length, names.Distinct().Count(), "Part names must be unique.");
            foreach (var part in info.parts)
            {
                Assert.Greater(part.indexCount, 0, part.name + " is empty.");
                Assert.AreEqual(0, part.indexCount % 3);
                Assert.AreEqual(3, part.pivot.Length);
                Assert.AreEqual(4, part.rotation.Length);
                if (!string.IsNullOrEmpty(part.parent)) CollectionAssert.Contains(names, part.parent);
            }
            // Each finger is a chain: segment 2 hangs off segment 1, segment 3 off segment 2, segment 1 off the palm.
            foreach (string finger in new[] { "index", "middle", "ring", "little", "thumb" })
            {
                Assert.AreEqual("palm", info.parts.First(p => p.name == finger + "_1").parent);
                Assert.AreEqual(finger + "_1", info.parts.First(p => p.name == finger + "_2").parent);
                Assert.AreEqual(finger + "_2", info.parts.First(p => p.name == finger + "_3").parent);
            }

            Assert.AreEqual(info.triangles, info.parts.Sum(p => p.indexCount) / 3);
            Assert.LessOrEqual(info.triangles, 4600, "One glove is over its triangle budget.");
            Assert.GreaterOrEqual(info.triangles, 2400, "The glove has lost most of its detail.");
            long size = new FileInfo(Dir + "/" + Id + ".mesh.bin").Length;
            Assert.AreEqual((long)info.vertexTotal * 32 + (long)info.indexTotal * 4, size, "The .bin does not match the .json.");

            var atlas = new Texture2D(2, 2);
            Assert.IsTrue(atlas.LoadImage(File.ReadAllBytes(Dir + "/" + Id + "_atlas.png")));
            Assert.LessOrEqual(atlas.width, 1024);
            Assert.AreEqual(atlas.width, atlas.height);
            UnityEngine.Object.DestroyImmediate(atlas);
        }

        [Test]
        public void GloveModel_IsNotInsideOut_AndItsNormalsAreSmoothAndOutward()
        {
            var info = Load();
            byte[] bytes = File.ReadAllBytes(Dir + "/" + Id + ".mesh.bin");
            int v = info.vertexTotal;
            var pos = new float[v * 3];
            var nrm = new float[v * 3];
            var idx = new int[info.indexTotal];
            Buffer.BlockCopy(bytes, 0, pos, 0, v * 12);
            Buffer.BlockCopy(bytes, v * 12, nrm, 0, v * 12);
            Buffer.BlockCopy(bytes, v * 32, idx, 0, info.indexTotal * 4);
            Vector3 P(int i) => new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
            Vector3 N(int i) => new Vector3(nrm[i * 3], nrm[i * 3 + 1], nrm[i * 3 + 2]);

            foreach (var part in info.parts)
            {
                Vector3 centre = Vector3.zero;
                for (int i = 0; i < part.vertexCount; i++) centre += P(part.vertexStart + i);
                centre /= part.vertexCount;
                double outward = 0;
                int agree = 0, tris = part.indexCount / 3;
                for (int t = 0; t < tris; t++)
                {
                    int a = part.vertexStart + idx[part.indexStart + t * 3];
                    int b = part.vertexStart + idx[part.indexStart + t * 3 + 1];
                    int c = part.vertexStart + idx[part.indexStart + t * 3 + 2];
                    Vector3 face = Vector3.Cross(P(b) - P(a), P(c) - P(a));
                    outward += Vector3.Dot(face, (P(a) + P(b) + P(c)) / 3f - centre);
                    if (Vector3.Dot(face, N(a) + N(b) + N(c)) > 0f) agree++;
                }
                Assert.Greater(outward, 0.0, part.name + " is inside out (its triangles face inward).");
                Assert.Greater(agree, tris * 0.97f, part.name + ": the stored normals disagree with the triangle winding.");
                for (int i = 0; i < part.vertexCount; i++)
                    Assert.AreEqual(1f, N(part.vertexStart + i).magnitude, 0.01f, part.name + " has an unnormalised normal.");
            }
        }

        [Test]
        public void PlayerRig_WearsTheModelledGloves_RightWayRound()
        {
            var rig = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TrashAstronauts/Prefabs/PlayerRig.prefab");
            var hands = rig.GetComponentsInChildren<HandRig>(true);
            Assert.AreEqual(2, hands.Length);
            foreach (var hand in hands)
            {
                bool left = hand.side == TrashAstronauts.Core.Handedness.Left;
                var visual = hand.GetComponentInChildren<GloveVisual>(true);
                Assert.IsTrue(visual.IsModelled, hand.name + " should wear the Blender glove, not the stand-in.");
                Assert.AreEqual(3, visual.thumbJoints.Length);
                foreach (var joint in visual.fingerJoints.Concat(visual.thumbJoints))
                {
                    Assert.IsNotNull(joint);
                    Assert.IsNotNull(joint.GetComponent<MeshFilter>().sharedMesh, joint.name);
                    Assert.IsNotNull(joint.GetComponent<MeshRenderer>().sharedMaterial, joint.name);
                }
                var model = visual.transform.Find("Model");
                Assert.IsNotNull(model);
                Assert.AreEqual(left ? -1f : 1f, model.localScale.x, "The left glove is the right one mirrored.");

                // In the controller's space the hand is held like a handshake: thumb on top, fingers forward,
                // the palm toward the other hand, the cuff behind.
                Transform h = hand.transform;
                Vector3 palm = h.InverseTransformPoint(visual.transform.TransformPoint(new Vector3(0f, -0.03f, -0.075f)));
                Vector3 thumbTip = h.InverseTransformPoint(visual.thumbJoints[2].position);
                Vector3 littleBase = h.InverseTransformPoint(visual.fingerJoints[9].position);
                Vector3 indexBase = h.InverseTransformPoint(visual.fingerJoints[0].position);
                Vector3 middleTip = h.InverseTransformPoint(visual.fingerJoints[5].TransformPoint(new Vector3(0f, 0f, 0.02f)));
                Assert.Greater(indexBase.y - littleBase.y, 0.04f, hand.name + ": the index should be above the little finger.");
                Assert.Greater(thumbTip.y - palm.y, 0.02f, hand.name + ": the thumb should be on top.");
                Assert.Greater(middleTip.z - palm.z, 0.09f, hand.name + ": the fingers should point forward.");
                Vector3 palmDown = h.InverseTransformDirection(visual.transform.TransformDirection(Vector3.down));
                Assert.Greater(Vector3.Dot(palmDown, left ? Vector3.right : Vector3.left), 0.95f);
                Assert.IsTrue(hand.fingertip.IsChildOf(visual.transform));
            }
        }
    }
}

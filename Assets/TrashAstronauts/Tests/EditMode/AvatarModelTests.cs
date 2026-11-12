using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Player;
using UnityEditor;
using UnityEngine;

namespace TrashAstronauts.Tests
{
    /// <summary>The player's suit: the files Blender exports (ModelSource/Player) and the prefab built from them.</summary>
    public class AvatarModelTests
    {
        const string Dir = "ModelSource/Player";
        const string Id = "astronaut";

        [Serializable]
        class PartInfo
        {
            public string name, parent, material;
            public float length;
            public int vertexStart, vertexCount, indexStart, indexCount;
        }

        [Serializable]
        class RigInfo
        {
            public float[] headCentre, neck, shoulder, hips;
            public float upperArmLength, forearmLength;
        }

        [Serializable]
        class ModelInfo
        {
            public string id;
            public int vertexTotal, indexTotal, triangles;
            public RigInfo rig;
            public PartInfo[] parts;
        }

        static ModelInfo Load()
        {
            string path = Dir + "/" + Id + ".mesh.json";
            Assert.IsTrue(File.Exists(path), path + " is missing. Run Blender on " + Dir + "/build_player.py.");
            return JsonUtility.FromJson<ModelInfo>(File.ReadAllText(path));
        }

        [Test]
        public void SuitModel_IsExported_WithItsNamedParts_WithinTheTriangleBudget()
        {
            foreach (string file in new[] { "build_player.py", Id + ".blend", Id + ".mesh.json", Id + ".mesh.bin", Id + "_atlas.png" })
                Assert.IsTrue(File.Exists(Dir + "/" + file), Dir + "/" + file + " is missing.");

            var info = Load();
            Assert.AreEqual(Id, info.id);
            var names = info.parts.Select(p => p.name).ToArray();
            foreach (string part in new[] { "helmet", "torso", "upperarm", "forearm", "legs", "helmet_accent", "torso_accent" })
                CollectionAssert.Contains(names, part, "The suit needs a part called " + part);
            Assert.AreEqual(names.Length, names.Distinct().Count(), "Part names must be unique.");
            foreach (var part in info.parts)
            {
                Assert.Greater(part.vertexCount, 0, part.name + " is empty.");
                Assert.Greater(part.indexCount, 0, part.name + " is empty.");
                Assert.AreEqual(0, part.indexCount % 3);
                if (part.name.EndsWith("_accent")) Assert.AreEqual("accent", part.material, part.name + " should use the accent material.");
                if (!string.IsNullOrEmpty(part.parent)) CollectionAssert.Contains(names, part.parent);
            }

            // In the file each arm segment is stored once; the game draws it twice.
            int inFile = info.parts.Sum(p => p.indexCount) / 3;
            int inGame = inFile + info.parts.Where(p => p.name.StartsWith("upperarm") || p.name.StartsWith("forearm")).Sum(p => p.indexCount) / 3;
            Assert.AreEqual(info.triangles, inFile);
            Assert.LessOrEqual(inGame, 13000, "The suit is over its triangle budget.");
            Assert.GreaterOrEqual(inGame, 6000, "The suit has lost most of its detail.");

            long size = new FileInfo(Dir + "/" + Id + ".mesh.bin").Length;
            Assert.AreEqual((long)info.vertexTotal * 32 + (long)info.indexTotal * 4, size, "The .bin does not match the .json.");
            Assert.AreEqual(info.vertexTotal, info.parts.Sum(p => p.vertexCount));
            Assert.AreEqual(info.indexTotal, info.parts.Sum(p => p.indexCount));
        }

        [Test]
        public void SuitModel_ArmSegments_AreUnitLengthAlongZ_AndTheRigNumbersAreHuman()
        {
            var info = Load();
            byte[] bytes = File.ReadAllBytes(Dir + "/" + Id + ".mesh.bin");
            var positions = new float[info.vertexTotal * 3];
            Buffer.BlockCopy(bytes, 0, positions, 0, positions.Length * 4);
            var indices = new int[info.indexTotal];
            Buffer.BlockCopy(bytes, info.vertexTotal * 32, indices, 0, indices.Length * 4);

            foreach (var part in info.parts)
            {
                for (int i = 0; i < part.indexCount; i++)
                {
                    int index = indices[part.indexStart + i];
                    Assert.IsTrue(index >= 0 && index < part.vertexCount, part.name + " has an index outside its own vertices.");
                }
                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = -min;
                for (int i = 0; i < part.vertexCount; i++)
                {
                    int k = (part.vertexStart + i) * 3;
                    var p = new Vector3(positions[k], positions[k + 1], positions[k + 2]);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
                if (part.name == "upperarm" || part.name == "forearm")
                {
                    // joint to joint is z = 0..1; the rounded ends overlap the neighbouring joint a little
                    Assert.Greater(part.length, 0.2f, part.name + " should say how long it really is.");
                    Assert.That(min.z, Is.InRange(-0.25f, 0.02f), part.name + " should start at its joint.");
                    Assert.That(max.z, Is.InRange(0.98f, 1.15f), part.name + " should be one unit long.");
                    Assert.Less(Mathf.Max(max.x, -min.x, max.y, -min.y), 0.1f, part.name + " is thicker than an arm.");
                }
                else if (part.name == "helmet")
                {
                    Assert.That(max.y - min.y, Is.InRange(0.28f, 0.4f), "The helmet should be head sized, centred on the head.");
                    Assert.Less(Mathf.Abs(max.x + min.x), 0.08f);
                }
                else if (part.name == "torso")
                {
                    Assert.That(-min.y, Is.InRange(0.5f, 0.75f), "The torso hangs from the base of the neck to the hips.");
                    Assert.Less(max.y, 0.12f);
                    Assert.Less(min.z, -0.2f, "The backpack is on the back (-Z).");
                }
                else if (part.name == "legs")
                {
                    Assert.That(-min.y, Is.InRange(0.75f, 1.1f), "The legs hang from the hips.");
                    Assert.Less(max.y, 0.1f);
                }
            }

            Assert.IsNotNull(info.rig);
            Assert.That(info.rig.upperArmLength, Is.InRange(0.26f, 0.36f));
            Assert.That(info.rig.forearmLength, Is.InRange(0.22f, 0.32f));
            Assert.That(info.rig.shoulder[0], Is.InRange(0.18f, 0.28f));
            // eyes to soles, standing straight, for someone about 1.75 m tall
            float eyeToHip = -info.rig.neck[1] - info.rig.hips[1];
            Assert.That(eyeToHip, Is.InRange(0.6f, 0.8f));
        }

        [Test]
        public void SuitModel_FacesOutward_WithUnitNormals_AndUVsInsideTheAtlas()
        {
            var info = Load();
            byte[] bytes = File.ReadAllBytes(Dir + "/" + Id + ".mesh.bin");
            int v = info.vertexTotal;
            var positions = new float[v * 3];
            var normals = new float[v * 3];
            var uvs = new float[v * 2];
            var indices = new int[info.indexTotal];
            Buffer.BlockCopy(bytes, 0, positions, 0, v * 12);
            Buffer.BlockCopy(bytes, v * 12, normals, 0, v * 12);
            Buffer.BlockCopy(bytes, v * 24, uvs, 0, v * 8);
            Buffer.BlockCopy(bytes, v * 32, indices, 0, indices.Length * 4);

            for (int i = 0; i < v; i++)
            {
                var n = new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]);
                float sum = positions[i * 3] + positions[i * 3 + 1] + positions[i * 3 + 2];
                Assert.IsFalse(float.IsNaN(sum) || float.IsInfinity(sum), "Vertex " + i + " is not a number.");
                Assert.AreEqual(1f, n.magnitude, 0.01f, "Normal " + i + " is not unit length.");
                Assert.That(uvs[i * 2], Is.InRange(0f, 1f), "UV " + i + " is off the atlas.");
                Assert.That(uvs[i * 2 + 1], Is.InRange(0f, 1f), "UV " + i + " is off the atlas.");
            }

            // Unity draws a triangle from the side its corners run clockwise, which is the side cross(b - a, c - a)
            // points to. If the export mirrored an axis without flipping the winding, that would point into the suit
            // and every part would render inside out.
            foreach (var part in info.parts)
            {
                int agree = 0, counted = 0;
                for (int i = 0; i < part.indexCount; i += 3)
                {
                    int a = part.vertexStart + indices[part.indexStart + i], b = part.vertexStart + indices[part.indexStart + i + 1], c = part.vertexStart + indices[part.indexStart + i + 2];
                    Vector3 pa = new Vector3(positions[a * 3], positions[a * 3 + 1], positions[a * 3 + 2]);
                    Vector3 pb = new Vector3(positions[b * 3], positions[b * 3 + 1], positions[b * 3 + 2]);
                    Vector3 pc = new Vector3(positions[c * 3], positions[c * 3 + 1], positions[c * 3 + 2]);
                    Vector3 face = Vector3.Cross(pb - pa, pc - pa);
                    if (face.sqrMagnitude < 1e-16f) continue;
                    Vector3 smooth = new Vector3(normals[a * 3] + normals[b * 3] + normals[c * 3], normals[a * 3 + 1] + normals[b * 3 + 1] + normals[c * 3 + 1], normals[a * 3 + 2] + normals[b * 3 + 2] + normals[c * 3 + 2]);
                    counted++;
                    if (Vector3.Dot(face, smooth) > 0f) agree++;
                }
                Assert.Greater(counted, 0, part.name);
                Assert.GreaterOrEqual(agree, counted * 0.99f, part.name + " is wound inside out (or its normals point inwards).");
            }

            // And the normals really point out of the suit, not in: on the helmet they point away from the head.
            var helmet = info.parts.First(p => p.name == "helmet_visor");
            int outward = 0;
            for (int i = 0; i < helmet.vertexCount; i++)
            {
                int k = (helmet.vertexStart + i) * 3;
                if (positions[k] * normals[k] + positions[k + 1] * normals[k + 1] + positions[k + 2] * normals[k + 2] > 0f) outward++;
            }
            Assert.GreaterOrEqual(outward, helmet.vertexCount * 0.95f, "The visor's normals point into the helmet.");
            Assert.Greater(positions[helmet.vertexStart * 3 + 2], 0f, "The visor is on the front of the helmet (+Z).");
        }

        [Test]
        public void PlayerRig_HasTheSuit_WiredToTheCameraAndHands()
        {
            var rig = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TrashAstronauts/Prefabs/PlayerRig.prefab");
            Assert.IsNotNull(rig);
            var context = rig.GetComponent<PlayerContext>();
            var avatar = context.avatar;
            Assert.IsNotNull(avatar, "The player rig should carry a PlayerAvatar.");
            Assert.AreSame(context.head, avatar.head);
            Assert.AreSame(context.leftHand.transform, avatar.leftHand);
            Assert.AreSame(context.rightHand.transform, avatar.rightHand);
            foreach (var part in new[] { avatar.helmet, avatar.torso, avatar.legs, avatar.leftUpperArm, avatar.leftForearm, avatar.rightUpperArm, avatar.rightForearm })
                Assert.IsNotNull(part);
            Assert.IsFalse(avatar.HelmetVisible);
            Assert.IsTrue(avatar.helmetRenderers.Length > 0 && avatar.helmetRenderers.All(r => !r.enabled));
            Assert.IsTrue(avatar.bodyRenderers.Length > 0 && avatar.bodyRenderers.All(r => r.enabled));
            Assert.IsTrue(avatar.accentRenderers.Length > 0);
            foreach (var r in avatar.accentRenderers)
            {
                Assert.IsTrue(r.sharedMaterial.HasProperty("_BaseColor"));
                Assert.IsNotNull(r.sharedMaterial.mainTexture, "Accent pieces share the suit's atlas.");
            }
            foreach (var r in avatar.helmetRenderers.Concat(avatar.bodyRenderers))
            {
                Assert.IsNotNull(r.GetComponent<MeshFilter>().sharedMesh, r.name + " has no mesh.");
                Assert.IsNotNull(r.sharedMaterial, r.name + " has no material.");
            }
            int triangles = avatar.helmetRenderers.Concat(avatar.bodyRenderers).Sum(r => r.GetComponent<MeshFilter>().sharedMesh.triangles.Length / 3);
            Assert.That(triangles, Is.InRange(6000, 13000), "Triangles drawn for one suit.");
            Assert.AreEqual(0, avatar.GetComponentsInChildren<Collider>(true).Length);
            Assert.AreEqual(0, avatar.GetComponentsInChildren<Rigidbody>(true).Length);
            // the prefab is saved already posed: each forearm ends at its glove's cuff
            foreach (bool left in new[] { true, false })
            {
                Transform fore = left ? avatar.leftForearm : avatar.rightForearm;
                Transform hand = left ? avatar.leftHand : avatar.rightHand;
                Vector3 wrist = fore.position + fore.forward * fore.localScale.z;
                Vector3 cuff = hand.TransformPoint(left ? avatar.leftWristOffset : avatar.rightWristOffset);
                Assert.Less(Vector3.Distance(wrist, cuff), 0.03f, "The " + (left ? "left" : "right") + " forearm should already reach its glove in the prefab.");
            }
            // the left arm is the right arm mirrored
            Assert.Less(avatar.leftUpperArm.GetComponentInChildren<MeshFilter>().transform.localScale.x, 0f);
            Assert.Greater(avatar.rightUpperArm.GetComponentInChildren<MeshFilter>().transform.localScale.x, 0f);
        }
    }
}

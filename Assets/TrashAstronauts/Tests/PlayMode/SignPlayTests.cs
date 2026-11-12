using System.Collections;
using System.Linq;
using NUnit.Framework;
using TMPro;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>Wall signs and panel labels are drawn from the front only, so from behind they never read mirrored.</summary>
    public class SignPlayTests
    {
        [UnitySetUp]
        public IEnumerator SetUp()
        {
            MissionDirector.StorageOverride = new MemorySaveStorage();
            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            yield return null;
        }

        [TearDown]
        public void TearDown() => MissionDirector.StorageOverride = null;

        [UnityTest]
        public IEnumerator WallSignsAndPanelLabels_AreSingleSided()
        {
            yield return null;
            var bad = Object.FindObjectsByType<TextMeshPro>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(t => t.transform.parent != null && t.transform.parent.name == "Label" && t.GetComponentInParent<Cuttable>(true) != null
                    && t.fontSharedMaterial.GetFloat("_CullMode") != 2f).Select(t => t.transform.parent.parent.name + "/" + t.text).ToArray();
            Assert.IsEmpty(bad, "panel labels drawn from both sides: " + string.Join(", ", bad));
            var signs = Object.FindObjectsByType<TextMeshPro>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(t => t.transform.parent != null && (t.transform.parent.name.StartsWith("Sign ") || (t.transform.parent.name == "Label" && t.GetComponentInParent<Cuttable>(true) != null))).ToArray();
            Assert.Greater(signs.Length, 20, "every wreck and the tug have signs");
            Assert.IsTrue(signs.Any(t => t.transform.parent.name == "Label"), "panel labels are among them");
            foreach (var t in signs)
            {
                var m = t.fontSharedMaterial;
                Assert.IsTrue(m.HasProperty("_CullMode"), t.transform.parent.name);
                Assert.AreEqual((float)UnityEngine.Rendering.CullMode.Back, m.GetFloat("_CullMode"), t.transform.parent.name + " is drawn from the front only");
            }
        }
    }
}

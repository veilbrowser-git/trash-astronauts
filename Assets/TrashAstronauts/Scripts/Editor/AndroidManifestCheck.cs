using System.Collections.Generic;
using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// Fails the Android build when the app would not get android.permission.INTERNET, which same-Wi-Fi co-op
    /// needs for its sockets. Without it every HOST / FIND / JOIN would fail on the headset with no other clue.
    ///
    /// Runs after Unity and the XR packages have written the Gradle project (callbackOrder 1000, they use 1),
    /// so it sees the manifests Gradle will merge: the unityLibrary manifest, the launcher manifest and any
    /// plugin manifests under the project. The build fails if none of them asks for INTERNET, or if any of them
    /// tells the merger to remove it (tools:node="remove").
    /// </summary>
    public sealed class AndroidManifestCheck : IPostGenerateGradleAndroidProject
    {
        public const string InternetPermission = "android.permission.INTERNET";
        const string AndroidNs = "http://schemas.android.com/apk/res/android";
        const string ToolsNs = "http://schemas.android.com/tools";

        public int callbackOrder => 1000;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            string problem = Check(path);
            if (problem != null) throw new BuildFailedException("[AndroidManifestCheck] " + problem);
            Debug.Log("[AndroidManifestCheck] " + InternetPermission + " is requested.");
        }

        /// <summary>
        /// Null when the Gradle project at <paramref name="unityLibraryPath"/> (Unity passes the unityLibrary
        /// folder) will request INTERNET; otherwise what is wrong.
        /// </summary>
        public static string Check(string unityLibraryPath)
        {
            if (string.IsNullOrEmpty(unityLibraryPath) || !Directory.Exists(unityLibraryPath)) return "Gradle project not found at " + unityLibraryPath;
            string root = Directory.GetParent(unityLibraryPath.TrimEnd('/', '\\'))?.FullName ?? unityLibraryPath;
            var manifests = new List<string>();
            foreach (string file in Directory.GetFiles(root, "AndroidManifest.xml", SearchOption.AllDirectories))
            {
                // Build outputs (intermediates) are not inputs to the merge.
                string normal = file.Replace('\\', '/');
                if (normal.Contains("/build/")) continue;
                manifests.Add(file);
            }
            if (manifests.Count == 0) return "no AndroidManifest.xml under " + root;

            bool requested = false;
            foreach (string file in manifests)
            {
                var doc = new XmlDocument();
                try { doc.Load(file); }
                catch (XmlException e) { return "could not read " + file + ": " + e.Message; }
                foreach (XmlNode node in doc.GetElementsByTagName("uses-permission"))
                {
                    if (!(node is XmlElement element)) continue;
                    if (element.GetAttribute("name", AndroidNs) != InternetPermission) continue;
                    if (element.GetAttribute("node", ToolsNs) == "remove") return file + " removes " + InternetPermission + " (tools:node=\"remove\"). Co-op cannot open a socket without it.";
                    requested = true;
                }
            }
            return requested ? null : "no manifest requests " + InternetPermission + " (checked " + manifests.Count + "). Co-op cannot open a socket without it: see Builder.ConfigureProject.";
        }
    }
}

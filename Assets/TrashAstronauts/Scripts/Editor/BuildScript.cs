using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// Builds the Quest APK. Menu: Trash Astronauts > Build Quest APK.
    /// Batch: -executeMethod TrashAstronauts.EditorTools.BuildScript.BuildQuestApk
    /// Requires the Android Build Support module (with OpenJDK and SDK/NDK tools) for this editor.
    /// </summary>
    public static class BuildScript
    {
        public const string OutputPath = "Builds/Quest/TrashAstronauts.apk";

        [MenuItem("Trash Astronauts/Build Quest APK", priority = 20)]
        public static void BuildQuestApkMenu() => Build(false);

        [MenuItem("Trash Astronauts/Build Quest APK (Development)", priority = 21)]
        public static void BuildQuestApkDevelopmentMenu() => Build(true);

        public static void BuildQuestApk() => ExitWith(Build(false));
        public static void BuildQuestApkDevelopment() => ExitWith(Build(true));

        static void ExitWith(bool ok)
        {
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        static bool Build(bool development)
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
            {
                Debug.LogError("[Build] Android Build Support is not installed for this editor. Install it from Unity Hub "
                    + "(Installs > this editor > Add modules > Android Build Support, including OpenJDK and SDK & NDK Tools).");
                return false;
            }

            if (!File.Exists(Builder.ScenePath)) Builder.BuildAll();
            Builder.ConfigureProject();

            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
            var options = new BuildPlayerOptions
            {
                scenes = new[] { Builder.ScenePath },
                locationPathName = OutputPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = development ? BuildOptions.Development | BuildOptions.AllowDebugging : BuildOptions.None,
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log("[Build] Quest APK built: " + Path.GetFullPath(OutputPath) + " (" + (summary.totalSize / (1024 * 1024)) + " MB)");
                return true;
            }
            Debug.LogError("[Build] Quest build failed: " + summary.result + ", errors: " + summary.totalErrors);
            return false;
        }
    }
}

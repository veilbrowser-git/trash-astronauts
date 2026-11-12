using System;
using System.IO;
using System.Linq;
using TrashAstronauts.World;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace TrashAstronauts.EditorTools
{
    public static partial class Builder
    {
        public const string AndroidPackageId = "com.trashastronauts.salvage";
        const string OpenXRLoaderType = "UnityEngine.XR.OpenXR.OpenXRLoader";

        [MenuItem("Trash Astronauts/Advanced/Configure Project Settings Only", priority = 50)]
        public static void ConfigureProject()
        {
            ConfigureLayers();
            GameLayers.ApplyCollisionMatrix();
            // One physics step per frame at the Quest's base 72 Hz. At 90 Hz the steps no longer line up
            // with frames, so every loose body is interpolated (see RealPhysics.ConfigureLooseBody):
            // held and thrown things move smoothly without paying for a faster physics rate.
            Time.fixedDeltaTime = RealPhysics.FixedTimestep; // also applied at startup by RealPhysics
            // Sturdier collisions for floating objects: more solver passes, and even slow taps bounce.
            Physics.defaultSolverIterations = 10;
            Physics.defaultSolverVelocityIterations = 4;
            Physics.bounceThreshold = 0.25f;
            // Overlapping bodies ease apart instead of shooting off; spins are not clipped early.
            Physics.defaultMaxDepenetrationVelocity = RealPhysics.MaxDepenetrationVelocity;
            Physics.defaultMaxAngularSpeed = RealPhysics.MaxAngularVelocity;

            Try("player settings", ConfigurePlayerSettings);
            Try("cloud project link", ConfigureCloudProject); // Builder.Online.cs
            Try("render pipeline", ConfigureRenderPipeline);
            Try("XR plug-in management", ConfigureXR);
            AssetDatabase.SaveAssets();
        }

        static void Try(string what, Action action)
        {
            try { action(); }
            catch (Exception e) { Debug.LogWarning("[Builder] Could not fully configure " + what + ": " + e.Message); }
        }

        static void ConfigureLayers()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0) return;
            var tagManager = new SerializedObject(assets[0]);
            var layers = tagManager.FindProperty("layers");
            if (layers == null) return;
            foreach (var (index, name) in GameLayers.Names)
            {
                if (index >= layers.arraySize) continue;
                layers.GetArrayElementAtIndex(index).stringValue = name;
            }
            tagManager.ApplyModifiedPropertiesWithoutUndo();
        }

        static void ConfigurePlayerSettings()
        {
            PlayerSettings.companyName = "Trash Astronauts";
            PlayerSettings.productName = "Trash Astronauts";
            PlayerSettings.bundleVersion = "0.11.3";
            Try("Android version code", () => PlayerSettings.Android.bundleVersionCode = 21);
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;

            // Co-op on the same Wi-Fi opens UDP sockets, which needs android.permission.INTERNET. The game only
            // opens one after HOST CO-OP, FIND CREW or ENTER CODE is pressed (see NetSession).
            Try("Android INTERNET permission", () => PlayerSettings.Android.forceInternetPermission = true);
            Try("Android identifier", () => PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, AndroidPackageId));
            Try("Android scripting backend", () => PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP));
            Try("Android architecture", () => PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64);
            // Quest 3 / 3S run Android 12L+ (API 32).
            Try("Android min SDK", () => PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)32);
            Try("Android graphics API", () =>
            {
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            });
        }

        static void ConfigureRenderPipeline()
        {
            // The Mobile URP asset from the Unity URP template, tuned for standalone VR.
            var asset = AssetDatabase.LoadMainAssetAtPath("Assets/Settings/Mobile_RPAsset.asset");
            if (asset != null)
            {
                var so = new SerializedObject(asset);
                Set(so, "m_MSAA", 4);
                Set(so, "m_SupportsHDR", false);
                Set(so, "m_RenderScale", 1f);
                Set(so, "m_MainLightShadowsSupported", false);
                Set(so, "m_AdditionalLightShadowsSupported", false);
                Set(so, "m_AdditionalLightsRenderingMode", 1);
                Set(so, "m_AdditionalLightsPerObjectLimit", 4);
                Set(so, "m_SoftShadowsSupported", false);
                Set(so, "m_SupportsLightCookies", false);
                Set(so, "m_ReflectionProbeBlending", false);
                Set(so, "m_ReflectionProbeBoxProjection", false);
                Set(so, "m_RequireDepthTexture", false);
                Set(so, "m_RequireOpaqueTexture", false);
                Set(so, "m_UseSRPBatcher", true);
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(asset);
            }

            // Use the same (mobile) quality level in the editor so what you see matches the headset.
            var quality = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset");
            if (quality != null && quality.Length > 0)
            {
                var so = new SerializedObject(quality[0]);
                var perPlatform = so.FindProperty("m_PerPlatformDefaultQuality");
                if (perPlatform != null)
                {
                    for (int i = 0; i < perPlatform.arraySize; i++)
                    {
                        var entry = perPlatform.GetArrayElementAtIndex(i);
                        var second = entry.FindPropertyRelative("second");
                        if (second != null) second.intValue = 0;
                    }
                }
                Set(so, "m_CurrentQuality", 0);
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            QualitySettings.SetQualityLevel(0, true);
        }

        static void Set(SerializedObject so, string property, object value)
        {
            var p = so.FindProperty(property);
            if (p == null) return;
            switch (value)
            {
                case bool b: p.boolValue = b; break;
                case int i:
                    if (p.propertyType == SerializedPropertyType.Boolean) p.boolValue = i != 0;
                    else p.intValue = i;
                    break;
                case float f: p.floatValue = f; break;
            }
        }

        static void ConfigureXR()
        {
            // 1. XR Plug-in Management: OpenXR loader for Android (Quest).
            if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey, out XRGeneralSettingsPerBuildTarget perTarget) || perTarget == null)
            {
                EnsureFolder("Assets/XR");
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey, perTarget, true);
            }

            foreach (var group in new[] { BuildTargetGroup.Android })
            {
                if (!perTarget.HasSettingsForBuildTarget(group)) perTarget.CreateDefaultSettingsForBuildTarget(group);
                if (!perTarget.HasManagerSettingsForBuildTarget(group)) perTarget.CreateDefaultManagerSettingsForBuildTarget(group);
                var manager = perTarget.ManagerSettingsForBuildTarget(group);
                bool hasLoader = manager.activeLoaders.Any(l => l != null && l.GetType().FullName == OpenXRLoaderType);
                if (!hasLoader && !XRPackageMetadataStore.AssignLoader(manager, OpenXRLoaderType, group))
                    Debug.LogWarning("[Builder] Could not assign the OpenXR loader for " + group + ".");
                var general = perTarget.SettingsForBuildTarget(group);
                if (general != null) general.InitManagerOnStart = true;
                EditorUtility.SetDirty(manager);
            }
            EditorUtility.SetDirty(perTarget);

            // 2. OpenXR features: Meta Quest support plus the Touch controller interaction profiles
            //    (Touch Plus is the Quest 3 / 3S controller).
            FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
            string[] featureIds =
            {
                "com.unity.openxr.feature.metaquest",
                "com.unity.openxr.feature.input.metaquestplus",
                "com.unity.openxr.feature.input.oculustouch",
                "com.unity.openxr.feature.input.metaquestpro",
                // Controller-free play (XR Hands package): hand joints, plus Meta's aim pose.
                "com.unity.openxr.feature.input.handtrackingsubsystem",
                "com.unity.openxr.feature.input.metahandtrackingaim",
            };
            foreach (string id in featureIds)
            {
                var feature = FeatureHelpers.GetFeatureWithIdForBuildTarget(BuildTargetGroup.Android, id);
                if (feature == null)
                {
                    Debug.LogWarning("[Builder] OpenXR feature not found: " + id);
                    continue;
                }
                feature.enabled = true;
                if (id == "com.unity.openxr.feature.metaquest")
                {
                    // Same-Wi-Fi co-op needs sockets, so the APK must keep the INTERNET permission: this feature
                    // can strip it from the manifest, so make sure it does not. (AndroidManifestCheck fails the
                    // build if the permission goes missing anyway.)
                    var noInternet = feature.GetType().GetProperty("ForceRemoveInternetPermission");
                    if (noInternet != null && noInternet.CanWrite) noInternet.SetValue(feature, false);
                    else Debug.LogWarning("[Builder] MetaQuestFeature.ForceRemoveInternetPermission not found: check the INTERNET permission by hand.");
                }
                EditorUtility.SetDirty(feature);
            }

            var openxr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (openxr != null)
            {
                openxr.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced; // multiview on Quest
                EditorUtility.SetDirty(openxr);
            }
        }

        // ------------------------------------------------------------------ editor simulation

        const string SimulatorSampleSource = "Packages/com.unity.xr.interaction.toolkit/Samples~/XR Interaction Simulator";
        const string SimulatorSampleDest = "Assets/Samples/XR Interaction Toolkit/XR Interaction Simulator";

        /// <summary>
        /// Copies XRI's "XR Interaction Simulator" sample into the project (the same thing Package
        /// Manager's Import button does) so the game can be driven with mouse and keyboard in the editor.
        /// </summary>
        static void ImportSimulatorSample()
        {
            if (Directory.Exists(SimulatorSampleDest)) return;
            string source = Path.GetFullPath(SimulatorSampleSource);
            if (!Directory.Exists(source))
            {
                Debug.LogWarning("[Builder] XR Interaction Simulator sample not found at " + source);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(SimulatorSampleDest));
            CopyDirectory(source, SimulatorSampleDest);
            AssetDatabase.Refresh();
        }

        static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
            foreach (string dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }

        /// <summary>Turns on "use the simulator in the editor" in XRI's project settings.</summary>
        static void ConfigureSimulator()
        {
            Try("XR Interaction Simulator", () =>
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SimulatorSampleDest + "/XR Interaction Simulator.prefab");
                if (prefab == null)
                {
                    Debug.LogWarning("[Builder] Simulator prefab missing; editor simulation is not enabled.");
                    return;
                }
                var type = Type.GetType("UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation.XRDeviceSimulatorSettings, Unity.XR.Interaction.Toolkit");
                if (type == null) return;

                const string path = "Assets/XRI/Settings/Resources/XRDeviceSimulatorSettings.asset";
                var settings = AssetDatabase.LoadAssetAtPath(path, type) as ScriptableObject;
                if (settings == null)
                {
                    EnsureFolder("Assets/XRI/Settings/Resources");
                    settings = ScriptableObject.CreateInstance(type);
                    AssetDatabase.CreateAsset(settings, path);
                }
                var so = new SerializedObject(settings);
                Set(so, "m_AutomaticallyInstantiateSimulatorPrefab", true);
                Set(so, "m_AutomaticallyInstantiateInEditorOnly", true);
                Set(so, "m_UseClassic", false);
                var prefabProp = so.FindProperty("m_SimulatorPrefab");
                if (prefabProp != null) prefabProp.objectReferenceValue = prefab;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(settings);
            });
        }
    }
}

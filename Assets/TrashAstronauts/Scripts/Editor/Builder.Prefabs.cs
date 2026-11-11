using System.Collections.Generic;
using TMPro;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.Tools;
using TrashAstronauts.UI;
using TrashAstronauts.World;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Inputs;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Readers;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Climbing;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;

namespace TrashAstronauts.EditorTools
{
    public static partial class Builder
    {
        static MissionConfig s_Config;
        static AudioLibrary s_Audio;
        static readonly Dictionary<string, SalvageDefinition> s_Defs = new Dictionary<string, SalvageDefinition>();
        static GameObject s_TrainingScrapPrefab, s_JunkCanPrefab, s_JunkCratePrefab, s_DronePrefab, s_RigPrefab;

        // ------------------------------------------------------------------ data assets

        static void BuildData()
        {
            s_Config = LoadOrCreate<MissionConfig>(DataDir + "/MissionConfig.asset", out _);
            s_Audio = AssetDatabase.LoadAssetAtPath<AudioLibrary>(DataDir + "/AudioLibrary.asset");

            s_Defs.Clear();
            Def("scrap_hull_plate", "Bent Hull Plate", SalvageCategory.SmallScrap, 40, 1.2f, false, "Scrap metal. Sold by weight.");
            Def("scrap_cable_coil", "Copper Cable Coil", SalvageCategory.SmallScrap, 55, 1f, false, "Copper. Mildly chewed.");
            Def("elec_nav_board", "Nav Computer Board", SalvageCategory.Electronics, 150, 1.5f, false, "Still knows the way home.");
            Def("elec_comms_relay", "Comms Relay Module", SalvageCategory.Electronics, 190, 2.5f, false, "Receives. Does not listen.");
            Def("reactor_regulator_core", "Fusion Regulator Core", SalvageCategory.ReactorComponent, 600, 14f, true, "Reactor part. Heavy. Do not lick.");
            Def("novelty_orbital_toaster", "Deluxe Orbital Toaster", SalvageCategory.Novelty, 350, 2f, false, "MSRP 349.99. Why? Nobody knows.");
            AssetDatabase.SaveAssets();
        }

        static void Def(string id, string displayName, SalvageCategory category, int value, float mass, bool bulky, string blurb)
        {
            var def = LoadOrCreate<SalvageDefinition>(DataDir + "/Salvage/" + id + ".asset", out bool created);
            if (created)
            {
                // Only written the first time: balancing edits made in the Inspector are preserved.
                def.id = id;
                def.displayName = displayName;
                def.category = category;
                def.creditValue = value;
                def.mass = mass;
                def.bulky = bulky;
                def.scannerBlurb = blurb;
                EditorUtility.SetDirty(def);
            }
            s_Defs[id] = def;
        }

        // ------------------------------------------------------------------ prefabs

        static void BuildPrefabs()
        {
            BuildSalvagePrefabs();
            s_DronePrefab = BuildDronePrefab();
            s_RigPrefab = BuildPlayerRigPrefab();
            AssetDatabase.SaveAssets();
        }

        static GameObject FloatingRoot(string name, float mass, int layer)
        {
            var go = new GameObject(name) { layer = layer };
            var rb = go.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.mass = mass;
            rb.linearDamping = 0.35f;
            rb.angularDamping = 0.4f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            return go;
        }

        static void MakeGrabbable(GameObject go, bool heavy, out XRGrabInteractable grab, out HoverHighlight highlight, out ImpactNoiseEmitter impact)
        {
            grab = go.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.VelocityTracking;
            grab.useDynamicAttach = true;
            grab.throwOnDetach = true;
            grab.retainTransformParent = false;
            grab.selectMode = InteractableSelectMode.Single;
            if (heavy)
            {
                // Bulky items lag behind the hand a little, which reads as weight.
                grab.smoothPosition = true;
                grab.smoothPositionAmount = 6f;
                grab.smoothRotation = true;
                grab.smoothRotationAmount = 6f;
                grab.throwVelocityScale = 0.7f;
            }

            highlight = go.AddComponent<HoverHighlight>();
            highlight.renderers = go.GetComponentsInChildren<MeshRenderer>();

            impact = go.AddComponent<ImpactNoiseEmitter>();
            impact.heavy = heavy;
            impact.loudnessScale = heavy ? 1.2f : 1f;
        }

        static GameObject FinishSalvage(GameObject go, string defId, bool training = false)
        {
            SalvageDefinition def = defId != null ? s_Defs[defId] : null;
            bool heavy = def != null && def.bulky;
            MakeGrabbable(go, heavy, out var grab, out var highlight, out var impact);

            var tagText = Text(go.transform, "Scan Tag", "", new Vector3(0, 0.22f, 0), Quaternion.identity, 0.35f, new Vector2(0.6f, 0.15f), new Color(0.6f, 1f, 0.85f));
            tagText.fontStyle = FontStyles.Bold;
            var tag = tagText.transform.parent.gameObject.AddComponent<ScanTag>();
            tag.text = tagText;

            var item = go.AddComponent<SalvageItem>();
            item.definition = def;
            item.grab = grab;
            item.highlight = highlight;
            item.scanTag = tag;
            item.impact = impact;
            item.isTraining = training;

            var prefab = SavePrefab(go, go.name);
            if (def != null && def.prefab != prefab)
            {
                def.prefab = prefab;
                EditorUtility.SetDirty(def);
            }
            return prefab;
        }

        static void BuildSalvagePrefabs()
        {
            // Small scrap: bent hull plate
            var go = FloatingRoot("Salvage_HullPlate", 1.2f, GameLayers.Salvage);
            Prim(go.transform, "Plate", PrimitiveType.Cube, Vector3.zero, new Vector3(0.24f, 0.02f, 0.17f), M("Scrap"), true);
            Prim(go.transform, "Bend", PrimitiveType.Cube, new Vector3(0.1f, 0.03f, 0), new Vector3(0.09f, 0.02f, 0.17f), M("Scrap"), true, new Vector3(0, 0, 35));
            FinishSalvage(go, "scrap_hull_plate");

            // Small scrap: cable coil
            go = FloatingRoot("Salvage_CableCoil", 1f, GameLayers.Salvage);
            Prim(go.transform, "Coil", PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.2f, 0.03f, 0.2f), M("Copper"), true);
            Prim(go.transform, "Hub", PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.08f, 0.04f, 0.08f), M("Cable"));
            FinishSalvage(go, "scrap_cable_coil");

            // Electronics: nav board
            go = FloatingRoot("Salvage_NavBoard", 1.5f, GameLayers.Salvage);
            Prim(go.transform, "Board", PrimitiveType.Cube, Vector3.zero, new Vector3(0.22f, 0.015f, 0.15f), M("Pcb"), true);
            Prim(go.transform, "ChipA", PrimitiveType.Cube, new Vector3(-0.05f, 0.014f, 0.02f), new Vector3(0.05f, 0.012f, 0.05f), M("Chip"));
            Prim(go.transform, "ChipB", PrimitiveType.Cube, new Vector3(0.05f, 0.014f, -0.03f), new Vector3(0.07f, 0.012f, 0.03f), M("Chip"));
            Prim(go.transform, "Led", PrimitiveType.Cube, new Vector3(0.08f, 0.014f, 0.05f), new Vector3(0.012f, 0.01f, 0.012f), M("Led"));
            FinishSalvage(go, "elec_nav_board");

            // Electronics: comms relay
            go = FloatingRoot("Salvage_CommsRelay", 2.5f, GameLayers.Salvage);
            Prim(go.transform, "Body", PrimitiveType.Cube, Vector3.zero, new Vector3(0.16f, 0.12f, 0.2f), M("ToolBody"), true);
            Prim(go.transform, "Panel", PrimitiveType.Cube, new Vector3(0, 0, -0.101f), new Vector3(0.12f, 0.08f, 0.004f), M("Pcb"));
            Prim(go.transform, "Antenna", PrimitiveType.Cylinder, new Vector3(0.05f, 0.11f, 0.06f), new Vector3(0.012f, 0.05f, 0.012f), M("Chrome"));
            Prim(go.transform, "Led", PrimitiveType.Cube, new Vector3(-0.04f, 0.03f, -0.104f), new Vector3(0.015f, 0.015f, 0.004f), M("Led"));
            FinishSalvage(go, "elec_comms_relay");

            // Reactor component: bulky regulator core
            go = FloatingRoot("Salvage_ReactorCore", 14f, GameLayers.Salvage);
            Prim(go.transform, "Shell", PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.26f, 0.2f, 0.26f), M("ReactorShell"), true);
            Prim(go.transform, "Band", PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.275f, 0.045f, 0.275f), M("ReactorGlow"));
            Prim(go.transform, "CapTop", PrimitiveType.Cylinder, new Vector3(0, 0.21f, 0), new Vector3(0.2f, 0.015f, 0.2f), M("Chrome"));
            Prim(go.transform, "CapBottom", PrimitiveType.Cylinder, new Vector3(0, -0.21f, 0), new Vector3(0.2f, 0.015f, 0.2f), M("Chrome"));
            Prim(go.transform, "Handle", PrimitiveType.Cube, new Vector3(0, 0.25f, 0), new Vector3(0.16f, 0.02f, 0.03f), M("ToolAccent"), true);
            FinishSalvage(go, "reactor_regulator_core");

            // Novelty: the suspiciously expensive toaster
            go = FloatingRoot("Salvage_OrbitalToaster", 2f, GameLayers.Salvage);
            Prim(go.transform, "Body", PrimitiveType.Cube, Vector3.zero, new Vector3(0.26f, 0.17f, 0.15f), M("Chrome"), true);
            Prim(go.transform, "SlotA", PrimitiveType.Cube, new Vector3(0, 0.083f, 0.03f), new Vector3(0.19f, 0.008f, 0.025f), M("Chip"));
            Prim(go.transform, "SlotB", PrimitiveType.Cube, new Vector3(0, 0.083f, -0.03f), new Vector3(0.19f, 0.008f, 0.025f), M("Chip"));
            Prim(go.transform, "Toast", PrimitiveType.Cube, new Vector3(0.02f, 0.1f, 0.03f), new Vector3(0.12f, 0.09f, 0.018f), M("Toast"));
            Prim(go.transform, "Lever", PrimitiveType.Cube, new Vector3(0.137f, 0.02f, 0), new Vector3(0.02f, 0.03f, 0.04f), M("ToolAccent"));
            Prim(go.transform, "Dial", PrimitiveType.Cylinder, new Vector3(0, -0.03f, -0.078f), new Vector3(0.04f, 0.004f, 0.04f), M("Chip"), false, new Vector3(90, 0, 0));
            Prim(go.transform, "Badge", PrimitiveType.Cube, new Vector3(-0.07f, 0.04f, -0.076f), new Vector3(0.06f, 0.02f, 0.003f), M("GlowWarm"));
            FinishSalvage(go, "novelty_orbital_toaster");

            // Tutorial practice scrap (worth nothing)
            go = FloatingRoot("Salvage_TrainingScrap", 1f, GameLayers.Salvage);
            Prim(go.transform, "Plate", PrimitiveType.Cube, Vector3.zero, new Vector3(0.2f, 0.03f, 0.14f), M("ScrapTraining"), true);
            s_TrainingScrapPrefab = FinishSalvage(go, null, true);

            // Worthless junk: good for throwing as a distraction.
            go = FloatingRoot("Junk_Can", 0.6f, GameLayers.Salvage);
            Prim(go.transform, "Can", PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.09f, 0.07f, 0.09f), M("Junk"), true);
            MakeGrabbable(go, false, out _, out _, out _);
            go.AddComponent<FloatingDebris>();
            s_JunkCanPrefab = SavePrefab(go, go.name);

            go = FloatingRoot("Junk_Crate", 2.5f, GameLayers.Salvage);
            Prim(go.transform, "Crate", PrimitiveType.Cube, Vector3.zero, new Vector3(0.28f, 0.22f, 0.22f), M("Container"), true);
            Prim(go.transform, "Strap", PrimitiveType.Cube, Vector3.zero, new Vector3(0.05f, 0.225f, 0.225f), M("Trim"));
            MakeGrabbable(go, false, out _, out _, out _);
            go.AddComponent<FloatingDebris>();
            s_JunkCratePrefab = SavePrefab(go, go.name);
        }

        // ------------------------------------------------------------------ drone

        static GameObject BuildDronePrefab()
        {
            var go = new GameObject("Drone_Janitor7") { layer = GameLayers.Enemy };
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            go.AddComponent<SphereCollider>().radius = 0.28f;

            Prim(go.transform, "Body", PrimitiveType.Sphere, Vector3.zero, new Vector3(0.5f, 0.42f, 0.5f), M("DroneBody"));
            Prim(go.transform, "Ring", PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.62f, 0.02f, 0.62f), M("Hazard"));
            var eye = Prim(go.transform, "Eye", PrimitiveType.Sphere, new Vector3(0, 0.02f, 0.21f), new Vector3(0.14f, 0.14f, 0.1f), M("DroneEye"));
            Prim(go.transform, "ArmL", PrimitiveType.Cube, new Vector3(-0.18f, -0.3f, 0.05f), new Vector3(0.03f, 0.28f, 0.03f), M("Trim"));
            Prim(go.transform, "ArmR", PrimitiveType.Cube, new Vector3(0.18f, -0.3f, 0.05f), new Vector3(0.03f, 0.28f, 0.03f), M("Trim"));
            Prim(go.transform, "Brush", PrimitiveType.Cube, new Vector3(0, -0.45f, 0.05f), new Vector3(0.42f, 0.05f, 0.08f), M("ToolAccent"));
            var sign = Prim(go.transform, "Sign", PrimitiveType.Cube, new Vector3(0, -0.02f, -0.255f), new Vector3(0.2f, 0.12f, 0.01f), M("ButtonYellow"));
            Text(go.transform, "SignText", "CAUTION\nWET FLOOR", new Vector3(0, -0.02f, -0.262f), Quaternion.identity, 0.3f, new Vector2(0.2f, 0.12f), Color.black).fontStyle = FontStyles.Bold;

            var eyePoint = Empty(go.transform, "Eye Point", new Vector3(0, 0.02f, 0.3f));
            var lightGo = Empty(go.transform, "Eye Light", new Vector3(0, 0.02f, 0.27f));
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Spot;
            light.spotAngle = 70f;
            light.innerSpotAngle = 40f;
            light.range = 9f;
            light.intensity = 3f;
            light.shadows = LightShadows.None;
            light.color = new Color(1f, 0.6f, 0.1f);

            var glyph = Text(go.transform, "Glyph", "", new Vector3(0, 0.45f, 0), Quaternion.identity, 3f, new Vector2(0.5f, 0.5f), Color.white);
            glyph.fontStyle = FontStyles.Bold;
            glyph.transform.parent.gameObject.AddComponent<Billboard>();

            var hum = Empty(go.transform, "Hum", Vector3.zero).AddComponent<AudioSource>();
            ConfigureSpatial(hum, s_Audio != null ? s_Audio.droneHum : null, true, 0.55f, 1.5f, 16f);
            var voice = Empty(go.transform, "Voice", Vector3.zero).AddComponent<AudioSource>();
            ConfigureSpatial(voice, null, true, 0.75f, 2f, 26f);

            var drone = go.AddComponent<EnemyDrone>();
            drone.eye = eyePoint.transform;
            drone.eyeLight = light;
            drone.eyeRenderer = eye.GetComponent<MeshRenderer>();
            drone.glyph = glyph;
            drone.hum = hum;
            drone.voice = voice;
            return SavePrefab(go, go.name);
        }

        static void ConfigureSpatial(AudioSource source, AudioClip clip, bool loop, float volume, float minDistance, float maxDistance)
        {
            source.clip = clip;
            source.loop = loop;
            source.playOnAwake = false;
            source.volume = volume;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = minDistance;
            source.maxDistance = maxDistance;
            source.dopplerLevel = 0f;
        }

        // ------------------------------------------------------------------ tools

        static GameObject ToolRoot(string name)
        {
            var go = new GameObject(name) { layer = GameLayers.Tool };
            var rb = go.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.mass = 0.8f;
            rb.linearDamping = 1.2f;
            rb.angularDamping = 1.2f;
            rb.isKinematic = true;
            return go;
        }

        static XRGrabInteractable ToolGrab(GameObject go)
        {
            var grip = Empty(go.transform, "Grip", Vector3.zero);
            var grab = go.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
            grab.useDynamicAttach = false;
            grab.attachTransform = grip.transform;
            grab.throwOnDetach = false;
            grab.retainTransformParent = false;
            grab.selectMode = InteractableSelectMode.Single;
            var highlight = go.AddComponent<HoverHighlight>();
            highlight.renderers = go.GetComponentsInChildren<MeshRenderer>();
            return grab;
        }

        static FlashlightTool BuildFlashlight(Transform parent)
        {
            var go = ToolRoot("Tool_Flashlight");
            go.transform.SetParent(parent, false);
            var body = Prim(go.transform, "Body", PrimitiveType.Cylinder, new Vector3(0, 0, 0.0f), new Vector3(0.038f, 0.075f, 0.038f), M("ToolBody"), true, new Vector3(90, 0, 0));
            Prim(go.transform, "Head", PrimitiveType.Cylinder, new Vector3(0, 0, 0.09f), new Vector3(0.058f, 0.02f, 0.058f), M("ToolAccent"), false, new Vector3(90, 0, 0));
            var lens = Prim(go.transform, "Lens", PrimitiveType.Cylinder, new Vector3(0, 0, 0.111f), new Vector3(0.048f, 0.002f, 0.048f), M("Lens"), false, new Vector3(90, 0, 0));
            var grab = ToolGrab(go);

            var lightGo = Empty(go.transform, "Beam", new Vector3(0, 0, 0.115f));
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Spot;
            light.spotAngle = 62f;
            light.innerSpotAngle = 34f;
            light.range = 15f;
            light.intensity = 9f;
            light.color = new Color(1f, 0.96f, 0.88f);
            light.shadows = LightShadows.None;

            var tool = go.AddComponent<FlashlightTool>();
            tool.toolName = "Flashlight";
            tool.grab = grab;
            tool.beam = light;
            tool.lens = lens.GetComponent<MeshRenderer>();
            return tool;
        }

        static ScannerTool BuildScanner(Transform parent)
        {
            var go = ToolRoot("Tool_Scanner");
            go.transform.SetParent(parent, false);
            Prim(go.transform, "Body", PrimitiveType.Cube, new Vector3(0, 0, 0.02f), new Vector3(0.07f, 0.035f, 0.16f), M("ToolBody"), true);
            Prim(go.transform, "Dish", PrimitiveType.Cylinder, new Vector3(0, 0, 0.105f), new Vector3(0.07f, 0.006f, 0.07f), M("ToolAccent"), false, new Vector3(90, 0, 0));
            var grab = ToolGrab(go);

            var screenRoot = Empty(go.transform, "Screen", new Vector3(0, 0.05f, -0.01f), Quaternion.Euler(40f, 0f, 0f));
            Prim(screenRoot.transform, "Back", PrimitiveType.Cube, new Vector3(0, 0, 0.004f), new Vector3(0.12f, 0.075f, 0.006f), M("Screen"));
            var text = Text(screenRoot.transform, "Text", "SCANNER\nSTANDBY", new Vector3(0, 0, -0.001f), Quaternion.identity, 0.085f, new Vector2(0.115f, 0.07f), new Color(0.5f, 1f, 0.8f));
            var emitter = Empty(go.transform, "Emitter", new Vector3(0, 0, 0.11f));

            var tool = go.AddComponent<ScannerTool>();
            tool.toolName = "Scanner";
            tool.grab = grab;
            tool.screen = text;
            tool.emitter = emitter.transform;
            return tool;
        }

        static PlasmaCutterTool BuildCutter(Transform parent)
        {
            var go = ToolRoot("Tool_PlasmaCutter");
            go.transform.SetParent(parent, false);
            Prim(go.transform, "Body", PrimitiveType.Cube, new Vector3(0, 0, 0.03f), new Vector3(0.05f, 0.06f, 0.18f), M("ToolBody"), true);
            Prim(go.transform, "Tank", PrimitiveType.Cylinder, new Vector3(0, -0.045f, 0.0f), new Vector3(0.04f, 0.05f, 0.04f), M("ToolAccent"), false, new Vector3(90, 0, 0));
            Prim(go.transform, "Nozzle", PrimitiveType.Cylinder, new Vector3(0, 0, 0.14f), new Vector3(0.022f, 0.03f, 0.022f), M("Chrome"), false, new Vector3(90, 0, 0));
            var grab = ToolGrab(go);

            var tip = Empty(go.transform, "Tip", new Vector3(0, 0, 0.215f));
            var flame = Prim(go.transform, "Flame", PrimitiveType.Capsule, new Vector3(0, 0, 0.195f), new Vector3(0.014f, 0.03f, 0.014f), M("Flame"), false, new Vector3(90, 0, 0));
            var flameLight = Empty(go.transform, "Flame Light", new Vector3(0, 0, 0.2f)).AddComponent<Light>();
            flameLight.type = LightType.Point;
            flameLight.range = 2.2f;
            flameLight.intensity = 2.5f;
            flameLight.color = new Color(0.5f, 0.8f, 1f);
            flameLight.shadows = LightShadows.None;

            var readoutRoot = Empty(go.transform, "Readout", new Vector3(0, 0.055f, -0.03f), Quaternion.Euler(40f, 0f, 0f));
            Prim(readoutRoot.transform, "Back", PrimitiveType.Cube, new Vector3(0, 0, 0.004f), new Vector3(0.1f, 0.06f, 0.006f), M("Screen"));
            var readout = Text(readoutRoot.transform, "Text", "HEAT 0%\nREADY", new Vector3(0, 0.006f, -0.001f), Quaternion.identity, 0.085f, new Vector2(0.095f, 0.04f), new Color(1f, 0.85f, 0.5f));
            // Heat bar: a pivot at the left edge scaled along X.
            var barPivot = Empty(readoutRoot.transform, "Heat Bar", new Vector3(-0.045f, -0.022f, -0.001f));
            var barScale = Empty(barPivot.transform, "Scale", Vector3.zero);
            var bar = Prim(barScale.transform, "Fill", PrimitiveType.Cube, new Vector3(0.045f, 0, 0), new Vector3(0.09f, 0.008f, 0.002f), M("GlowWhite"));

            var sparksGo = Empty(go.transform, "Sparks", new Vector3(0, 0, 0.215f));
            var sparks = sparksGo.AddComponent<ParticleSystem>();
            var main = sparks.main;
            main.playOnAwake = false;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.2f, 0.45f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 2.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.008f, 0.018f);
            main.startColor = new Color(1f, 0.75f, 0.3f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 60;
            main.gravityModifier = 0f;
            var emission = sparks.emission;
            emission.enabled = false;
            var shape = sparks.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.01f;
            var psr = sparksGo.GetComponent<ParticleSystemRenderer>();
            psr.sharedMaterial = M("Spark");
            Cheap(psr);

            var loop = Empty(go.transform, "Loop Audio", new Vector3(0, 0, 0.15f)).AddComponent<AudioSource>();
            ConfigureSpatial(loop, s_Audio != null ? s_Audio.cutterLoop : null, true, 0.6f, 0.5f, 16f);

            var tool = go.AddComponent<PlasmaCutterTool>();
            tool.toolName = "Plasma Cutter";
            tool.grab = grab;
            tool.tip = tip.transform;
            tool.flame = flame;
            tool.flameLight = flameLight;
            tool.sparks = sparks;
            tool.loopSource = loop;
            tool.readout = readout;
            tool.heatBar = barScale.transform;
            tool.heatBarRenderer = bar.GetComponent<MeshRenderer>();
            flame.SetActive(false);
            flameLight.enabled = false;
            return tool;
        }

        // ------------------------------------------------------------------ settings panel (shared by tug console and suit menu)

        static SettingsPanel BuildSettingsPanel(Transform parent, string name, Vector3 localPos, Quaternion localRot, float scale)
        {
            var root = Empty(parent, name, localPos, localRot, GameLayers.Default);
            root.transform.localScale = Vector3.one * scale;
            Prim(root.transform, "Back", PrimitiveType.Cube, new Vector3(0, 0, 0.012f), new Vector3(0.56f, 0.42f, 0.02f), M("Screen"));
            var summary = Text(root.transform, "Summary", "SUIT SETTINGS", new Vector3(0, 0.125f, -0.002f), Quaternion.identity, 0.26f, new Vector2(0.52f, 0.14f), new Color(0.75f, 0.95f, 1f));

            var size = new Vector2(0.24f, 0.07f);
            var panel = root.AddComponent<SettingsPanel>();
            panel.summary = summary;
            panel.heightUp = Button(root.transform, "Height Up", new Vector3(-0.13f, 0.02f, 0), Quaternion.identity, size, "HEIGHT +", M("ButtonBlue"), 0.22f);
            panel.heightDown = Button(root.transform, "Height Down", new Vector3(0.13f, 0.02f, 0), Quaternion.identity, size, "HEIGHT -", M("ButtonBlue"), 0.22f);
            panel.snapTurn = Button(root.transform, "Snap Turn", new Vector3(-0.13f, -0.065f, 0), Quaternion.identity, size, "TURN 45", M("ButtonBlue"), 0.22f);
            panel.handedness = Button(root.transform, "Handedness", new Vector3(0.13f, -0.065f, 0), Quaternion.identity, size, "HAND: RIGHT", M("ButtonBlue"), 0.22f);
            panel.unstuck = Button(root.transform, "Unstuck", new Vector3(-0.13f, -0.15f, 0), Quaternion.identity, size, "UNSTUCK", M("ButtonAmber"), 0.22f);
            panel.recallTools = Button(root.transform, "Recall Tools", new Vector3(0.13f, -0.15f, 0), Quaternion.identity, size, "RECALL TOOLS", M("ButtonAmber"), 0.22f);
            return panel;
        }

        // ------------------------------------------------------------------ player rig

        static void ConfigureButtonReader(XRInputButtonReader reader, string performed, string value)
        {
            reader.inputSourceMode = XRInputButtonReader.InputSourceMode.InputActionReference;
            reader.inputActionReferencePerformed = Ref(performed);
            reader.inputActionReferenceValue = Ref(value);
        }

        static GameObject BuildPlayerRigPrefab()
        {
            var rig = new GameObject("Player Rig") { layer = GameLayers.Player };

            var controller = rig.AddComponent<CharacterController>();
            controller.height = 1.7f;
            controller.radius = 0.2f;
            controller.center = new Vector3(0, 0.85f, 0);
            controller.skinWidth = 0.02f;
            controller.stepOffset = 0.15f;
            controller.minMoveDistance = 0f;

            var heightAdjust = Empty(rig.transform, "Seated Height Adjust", Vector3.zero, null, GameLayers.Player);
            var cameraOffset = Empty(heightAdjust.transform, "Camera Offset", Vector3.zero, null, GameLayers.Player);

            // --- head
            var camGo = Empty(cameraOffset.transform, "Main Camera", new Vector3(0, 1.6f, 0), null, GameLayers.Default);
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 60f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.allowHDR = false;
            camGo.AddComponent<AudioListener>();
            var headDriver = camGo.AddComponent<TrackedPoseDriver>();
            headDriver.positionInput = new InputActionProperty(Ref("Head/Position"));
            headDriver.rotationInput = new InputActionProperty(Ref("Head/Rotation"));
            headDriver.trackingStateInput = new InputActionProperty(Ref("Head/TrackingState"));

            var fadeQuad = Prim(camGo.transform, "Fade Quad", PrimitiveType.Quad, new Vector3(0, 0, 0.1f), new Vector3(1.2f, 1.2f, 1f), M("Fade"));
            fadeQuad.GetComponent<MeshRenderer>().enabled = false;
            var fader = camGo.AddComponent<ScreenFader>();
            fader.quad = fadeQuad.GetComponent<MeshRenderer>();

            // --- XR origin + locomotion (all movement goes through the mediator)
            var origin = rig.AddComponent<XROrigin>();
            origin.Origin = rig;
            origin.CameraFloorOffsetObject = cameraOffset;
            origin.Camera = cam;
            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;
            origin.CameraYOffset = 1.6f;

            var mediator = rig.AddComponent<LocomotionMediator>();
            var bodyTransformer = rig.GetComponent<XRBodyTransformer>();
            if (bodyTransformer == null) bodyTransformer = rig.AddComponent<XRBodyTransformer>();
            bodyTransformer.xrOrigin = origin;
            bodyTransformer.useCharacterControllerIfExists = true;
            mediator.xrOrigin = origin;

            var teleport = rig.AddComponent<TeleportationProvider>();
            teleport.mediator = mediator;
            var snap = rig.AddComponent<SnapTurnProvider>();
            snap.mediator = mediator;
            snap.turnAmount = 45f;
            snap.enableTurnLeftRight = true;
            snap.enableTurnAround = false;
            snap.leftHandTurnInput.inputSourceMode = XRInputValueReader.InputSourceMode.InputActionReference;
            snap.leftHandTurnInput.inputActionReference = Ref("LeftHand/Thumbstick");
            snap.rightHandTurnInput.inputSourceMode = XRInputValueReader.InputSourceMode.InputActionReference;
            snap.rightHandTurnInput.inputActionReference = Ref("RightHand/Thumbstick");
            var climb = rig.AddComponent<ClimbProvider>();
            climb.mediator = mediator;

            var actionManager = rig.AddComponent<InputActionManager>();
            actionManager.actionAssets = new List<InputActionAsset> { AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputPath) };

            // --- body anchor + holsters
            var bodyGo = Empty(rig.transform, "Body Anchor", new Vector3(0, 1.2f, 0), null, GameLayers.Tool);
            var body = bodyGo.AddComponent<BodyAnchor>();
            body.head = camGo.transform;
            var chest = Empty(bodyGo.transform, "Chest Holster", body.chestOffset, null, GameLayers.Tool);
            var domHip = Empty(bodyGo.transform, "Main Hip Holster", body.hipOffset, null, GameLayers.Tool);
            var offHip = Empty(bodyGo.transform, "Off Hip Holster", new Vector3(-body.hipOffset.x, body.hipOffset.y, body.hipOffset.z), null, GameLayers.Tool);
            body.chestSocket = chest.transform;
            body.dominantHipSocket = domHip.transform;
            body.offHipSocket = offHip.transform;

            var locomotion = rig.AddComponent<PlayerLocomotion>();
            locomotion.origin = origin;
            locomotion.teleportation = teleport;
            locomotion.snapTurn = snap;
            locomotion.climb = climb;
            locomotion.heightAdjust = heightAdjust.transform;
            locomotion.bodyAnchor = body;

            var context = rig.AddComponent<PlayerContext>();
            context.head = camGo.transform;
            context.locomotion = locomotion;
            context.fader = fader;
            context.bodyAnchor = body;

            // --- hands
            var reticle = Prim(rig.transform, "Teleport Reticle", PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.4f, 0.004f, 0.4f), M("Reticle"), false, null, GameLayers.Default);
            reticle.SetActive(false);
            context.leftHand = BuildHand(cameraOffset.transform, true, locomotion, reticle.transform);
            context.rightHand = BuildHand(cameraOffset.transform, false, locomotion, reticle.transform);

            // --- tools in their holsters
            var flashlight = BuildFlashlight(null);
            var scanner = BuildScanner(null);
            var cutter = BuildCutter(null);
            MakeHolster(chest, flashlight, Quaternion.Euler(8f, 0f, 0f));
            MakeHolster(domHip, cutter, Quaternion.Euler(80f, 0f, 0f));
            MakeHolster(offHip, scanner, Quaternion.Euler(80f, 0f, 0f));
            context.tools = new SuitTool[] { flashlight, scanner, cutter };

            // --- wrist display
            var wrist = Empty(rig.transform, "Wrist Display", Vector3.zero, null, GameLayers.Default);
            Prim(wrist.transform, "Back", PrimitiveType.Cube, new Vector3(0, 0, 0.004f), new Vector3(0.125f, 0.092f, 0.006f), M("Screen"));
            var display = wrist.AddComponent<WristDisplay>();
            display.player = context;
            display.oxygenText = Text(wrist.transform, "Oxygen", "O2 100%", new Vector3(0, 0.028f, -0.001f), Quaternion.identity, 0.085f, new Vector2(0.12f, 0.026f), new Color(0.8f, 1f, 1f));
            var barPivot = Empty(wrist.transform, "Oxygen Bar", new Vector3(-0.056f, 0.008f, -0.001f));
            var barScale = Empty(barPivot.transform, "Scale", Vector3.zero);
            var bar = Prim(barScale.transform, "Fill", PrimitiveType.Cube, new Vector3(0.056f, 0, 0), new Vector3(0.112f, 0.007f, 0.002f), M("GlowWhite"));
            display.oxygenBar = barScale.transform;
            display.oxygenBarRenderer = bar.GetComponent<MeshRenderer>();
            display.salvageText = Text(wrist.transform, "Salvage", "NO CONTRACT", new Vector3(0, -0.012f, -0.001f), Quaternion.identity, 0.07f, new Vector2(0.12f, 0.024f), new Color(1f, 0.9f, 0.6f));
            display.threatText = Text(wrist.transform, "Threat", "", new Vector3(0, -0.035f, -0.001f), Quaternion.identity, 0.065f, new Vector2(0.12f, 0.014f), new Color(1f, 0.6f, 0.5f));
            context.wristDisplay = wrist.transform;

            // --- suit menu
            var menuRoot = Empty(rig.transform, "Suit Menu", Vector3.zero, null, GameLayers.Default);
            var menuPanel = BuildSettingsPanel(menuRoot.transform, "Panel", Vector3.zero, Quaternion.identity, 1f);
            var menu = rig.AddComponent<WristMenu>();
            menu.player = context;
            menu.panel = menuRoot;

            var alerts = rig.AddComponent<SuitAlerts>();
            alerts.player = context;

            return SavePrefab(rig, "PlayerRig");
        }

        static void MakeHolster(GameObject holsterGo, SuitTool tool, Quaternion dockRotation)
        {
            var trigger = holsterGo.AddComponent<SphereCollider>();
            trigger.isTrigger = true;
            trigger.radius = 0.1f;
            var interactable = holsterGo.AddComponent<XRSimpleInteractable>();
            interactable.selectMode = InteractableSelectMode.Single;

            var dock = Empty(holsterGo.transform, "Dock", Vector3.zero, dockRotation, GameLayers.Tool);
            var marker = Prim(holsterGo.transform, "Empty Marker", PrimitiveType.Cube, Vector3.zero, new Vector3(0.07f, 0.07f, 0.07f), M("Ghost"), false, new Vector3(45, 45, 0));
            marker.SetActive(false);

            var holster = holsterGo.AddComponent<ToolHolster>();
            holster.tool = tool;
            holster.socket = dock.transform;
            holster.interactable = interactable;
            holster.emptyMarker = marker;

            tool.holster = holster;
            tool.transform.SetParent(dock.transform, false);
            tool.transform.localPosition = Vector3.zero;
            tool.transform.localRotation = Quaternion.identity;
        }

        static HandRig BuildHand(Transform cameraOffset, bool left, PlayerLocomotion locomotion, Transform reticle)
        {
            string side = left ? "LeftHand" : "RightHand";
            float s = left ? 1f : -1f; // direction of the thumb (inner) side

            var go = Empty(cameraOffset, left ? "Left Controller" : "Right Controller", new Vector3(left ? -0.2f : 0.2f, 1.1f, 0.3f), null, GameLayers.Hand);
            var driver = go.AddComponent<TrackedPoseDriver>();
            driver.positionInput = new InputActionProperty(Ref(side + "/Position"));
            driver.rotationInput = new InputActionProperty(Ref(side + "/Rotation"));
            driver.trackingStateInput = new InputActionProperty(Ref(side + "/TrackingState"));

            var haptics = go.AddComponent<HapticImpulsePlayer>();
            haptics.hapticOutput.inputSourceMode = XRInputHapticImpulseProvider.InputSourceMode.InputActionReference;
            haptics.hapticOutput.inputActionReference = Ref(side + "/Haptic");

            // Grab interactor: one sphere, no rigidbody, so XRI uses its accurate overlap mode.
            var grabGo = Empty(go.transform, "Grab Interactor", new Vector3(0, -0.025f, -0.05f), null, GameLayers.Hand);
            var sphere = grabGo.AddComponent<SphereCollider>();
            sphere.isTrigger = true;
            sphere.radius = 0.1f;
            var attach = Empty(grabGo.transform, "Attach", Vector3.zero, null, GameLayers.Hand);
            var interactor = grabGo.AddComponent<XRDirectInteractor>();
            ConfigureButtonReader(interactor.selectInput, side + "/Select", side + "/SelectValue");
            ConfigureButtonReader(interactor.activateInput, side + "/Activate", side + "/ActivateValue");
            interactor.handedness = left ? InteractorHandedness.Left : InteractorHandedness.Right;
            interactor.physicsLayerMask = GameLayers.GrabbableMask;
            interactor.physicsTriggerInteraction = QueryTriggerInteraction.Collide;
            interactor.attachTransform = attach.transform;

            // Glove
            var glove = Empty(go.transform, "Glove", Vector3.zero, null, GameLayers.Hand);
            Prim(glove.transform, "Palm", PrimitiveType.Cube, new Vector3(0, -0.03f, -0.075f), new Vector3(0.085f, 0.032f, 0.1f), M("Glove"));
            Prim(glove.transform, "Cuff", PrimitiveType.Cube, new Vector3(0, -0.03f, -0.15f), new Vector3(0.095f, 0.05f, 0.055f), M("GloveAccent"));
            var pivots = new List<Transform>();
            Transform indexPivot = null;
            float[] fingerX = { 0.03f, 0.01f, -0.01f, -0.03f };
            for (int i = 0; i < 4; i++)
            {
                var pivot = Empty(glove.transform, "Finger" + i, new Vector3(s * fingerX[i], -0.03f, -0.025f), null, GameLayers.Hand);
                Prim(pivot.transform, "Segment", PrimitiveType.Cube, new Vector3(0, 0, 0.035f), new Vector3(0.017f, 0.02f, 0.07f), M("Glove"));
                if (i == 0) indexPivot = pivot.transform;
                else pivots.Add(pivot.transform);
            }
            var thumbPivot = Empty(glove.transform, "Thumb", new Vector3(s * 0.048f, -0.03f, -0.09f), null, GameLayers.Hand);
            Prim(thumbPivot.transform, "Segment", PrimitiveType.Cube, new Vector3(s * 0.012f, 0, 0.03f), new Vector3(0.02f, 0.02f, 0.06f), M("Glove"), false, new Vector3(0, s * 25f, 0));
            var fingertip = Empty(go.transform, "Fingertip", new Vector3(s * 0.03f, -0.03f, 0.05f), null, GameLayers.Hand);
            var wristMount = Empty(go.transform, "Wrist Mount", new Vector3(0, 0.0f, -0.2f),
                Quaternion.LookRotation(Vector3.down, left ? Vector3.left : Vector3.right), GameLayers.Hand);

            var hand = go.AddComponent<HandRig>();
            hand.side = left ? Handedness.Left : Handedness.Right;
            hand.interactor = interactor;
            hand.haptics = haptics;
            hand.fingertip = fingertip.transform;
            hand.wristMount = wristMount.transform;
            hand.thumbstick = Ref(side + "/Thumbstick");
            hand.gripValue = Ref(side + "/SelectValue");
            hand.triggerValue = Ref(side + "/ActivateValue");
            hand.triggerButton = Ref(side + "/Activate");
            hand.menuButton = Ref(side + "/Menu");

            var gloveVisual = glove.AddComponent<GloveVisual>();
            gloveVisual.hand = hand;
            gloveVisual.indexPivot = indexPivot;
            gloveVisual.gripPivots = pivots.ToArray();
            gloveVisual.thumbPivot = thumbPivot.transform;

            var aimer = go.AddComponent<TeleportAimer>();
            aimer.hand = hand;
            aimer.locomotion = locomotion;
            aimer.line = MakeLine(go.transform, "Teleport Line", 0.012f);
            aimer.reticle = reticle;

            var buttons = go.AddComponent<HandButtonInteractor>();
            buttons.hand = hand;
            buttons.pointerLine = MakeLine(go.transform, "Pointer Line", 0.004f);
            return hand;
        }

        static LineRenderer MakeLine(Transform parent, string name, float width)
        {
            var go = Empty(parent, name, Vector3.zero, null, GameLayers.Default);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.sharedMaterial = M("Line");
            line.widthMultiplier = width;
            line.positionCount = 0;
            line.numCapVertices = 2;
            line.startColor = line.endColor = new Color(0.3f, 0.95f, 1f);
            line.enabled = false;
            Cheap(line);
            return line;
        }
    }
}

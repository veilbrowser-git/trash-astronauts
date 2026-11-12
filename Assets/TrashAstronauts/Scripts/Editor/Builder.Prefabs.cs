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
        static GameObject[] s_CreaturePrefabs;

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
            Def("novelty_rubber_duck", "Emergency Rubber Duck", SalvageCategory.Novelty, 75, 0.15f, false, "Squeeze the trigger. It is LOUD.");
            if (!s_Defs["novelty_rubber_duck"].bonusSpawn)
            {
                s_Defs["novelty_rubber_duck"].bonusSpawn = true;
                EditorUtility.SetDirty(s_Defs["novelty_rubber_duck"]);
            }
            // Real masses (kg), written every build so they win over the first-build values above.
            SetMass("scrap_hull_plate", 3f);          // aluminium hull skin with steel ribs
            SetMass("scrap_cable_coil", 6f);          // a spool of copper is dense
            SetMass("elec_nav_board", 0.6f);          // fibreglass board, chips and a heatsink
            SetMass("elec_comms_relay", 3.5f);        // steel case full of electronics
            SetMass("reactor_regulator_core", 40f);   // steel shell and cage round a dense core
            SetMass("novelty_orbital_toaster", 1.8f); // a chrome kitchen toaster
            SetMass("novelty_rubber_duck", 0.1f);     // hollow vinyl
            // How easily each thing is damaged by being banged about (scrap and the duck do not care).
            SetFragility("elec_nav_board", 0.6f);
            SetFragility("elec_comms_relay", 0.7f);
            SetFragility("reactor_regulator_core", 0.5f);
            SetFragility("novelty_orbital_toaster", 0.8f);
            // The creature has to be able to catch someone who is only drifting.
            // A suited person with a life-support pack: throws, shoves and thrust are worked out against this.
            if (!Mathf.Approximately(s_Config.playerMass, RealPhysics.PlayerBodyMass)) s_Config.playerMass = RealPhysics.PlayerBodyMass;
            if (s_Config.enemy.chaseSpeed < 3f) s_Config.enemy.chaseSpeed = 3f;
            if (s_Config.enemy.chaseLoseSightSeconds < 5f) s_Config.enemy.chaseLoseSightSeconds = 5f;
            EditorUtility.SetDirty(s_Config);
            AssetDatabase.SaveAssets();
        }

        static void SetMass(string id, float kg)
        {
            if (!s_Defs.TryGetValue(id, out var def) || Mathf.Approximately(def.mass, kg)) return;
            def.mass = kg;
            EditorUtility.SetDirty(def);
        }

        static void SetFragility(string id, float value)
        {
            if (!s_Defs.TryGetValue(id, out var def) || Mathf.Approximately(def.fragility, value)) return;
            def.fragility = value;
            EditorUtility.SetDirty(def);
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
            s_CreaturePrefabs = new[] { BuildCreaturePrefab(0), BuildCreaturePrefab(1), BuildCreaturePrefab(2), BuildCreaturePrefab(3) };
            s_DronePrefab = s_CreaturePrefabs[0];
            s_RigPrefab = BuildPlayerRigPrefab();
            AssetDatabase.SaveAssets();
        }

        static GameObject FloatingRoot(string name, float mass, int layer)
        {
            var go = new GameObject(name) { layer = layer };
            var rb = go.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.mass = mass;
            RealPhysics.ConfigureLooseBody(rb);
            return go;
        }

        static void MakeGrabbable(GameObject go, bool heavy, SurfaceKind surface, out XRGrabInteractable grab, out HoverHighlight highlight, out ImpactNoiseEmitter impact)
        {
            grab = go.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.VelocityTracking;
            grab.useDynamicAttach = true;
            grab.throwOnDetach = true;
            grab.retainTransformParent = false;
            grab.selectMode = InteractableSelectMode.Single;
            // Throws follow the last fifth of a second of hand movement, so a flick of the wrist counts.
            // The release keeps the hand's true velocity and spin (no boost); HeldWeight limits it to what
            // an arm can give the object's mass, and makes heavy things lag and swing while held.
            grab.throwSmoothingDuration = 0.18f;
            grab.throwVelocityScale = 1f;
            grab.throwAngularVelocityScale = 1f;
            go.AddComponent<HeldWeight>();

            highlight = go.AddComponent<HoverHighlight>();
            highlight.renderers = go.GetComponentsInChildren<MeshRenderer>();
            foreach (var collider in go.GetComponentsInChildren<Collider>()) collider.sharedMaterial = SurfaceMaterial(surface);

            impact = go.AddComponent<ImpactNoiseEmitter>();
            impact.surface = surface;
            impact.heavy = heavy;
            impact.loudnessScale = heavy ? 1.2f : 1f;
        }

        static GameObject FinishSalvage(GameObject go, string defId, SurfaceKind surface, bool training = false)
        {
            SalvageDefinition def = defId != null ? s_Defs[defId] : null;
            bool heavy = def != null && def.bulky;
            MakeGrabbable(go, heavy, surface, out var grab, out var highlight, out var impact);

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
            GameObject go;
            Transform t;

            // Small scrap: bent hull plate with stiffening ribs and bolts
            go = FloatingRoot("Salvage_HullPlate", 3f, GameLayers.Salvage);
            t = go.transform;
            go.AddComponent<BoxCollider>().size = new Vector3(0.27f, 0.05f, 0.19f);
            CBox(t, "Plate", Vector3.zero, new Vector3(0.26f, 0.018f, 0.18f), M("Scrap"), 0.004f);
            CBox(t, "Bent Flap", new Vector3(0.105f, 0.026f, 0f), new Vector3(0.09f, 0.016f, 0.18f), M("Scrap"), 0.004f, new Vector3(0, 0, 38));
            for (int i = 0; i < 3; i++) CBox(t, "Rib", new Vector3(-0.03f, 0.014f, -0.06f + i * 0.06f), new Vector3(0.18f, 0.012f, 0.012f), M("Steel"), 0.003f);
            foreach (float x in new[] { -0.11f, 0.02f })
                foreach (float z in new[] { -0.075f, 0.075f })
                    Part(t, "Bolt", Tube(0.008f, 0.01f, 6), new Vector3(x, 0.014f, z), M("Brass"));
            CBox(t, "Scorch", new Vector3(-0.06f, 0.0095f, 0.02f), new Vector3(0.07f, 0.002f, 0.05f), M("Chip"), 0.0005f, new Vector3(0, 25, 0));
            FinishSalvage(go, "scrap_hull_plate", SurfaceKind.BareMetal);

            // Small scrap: spool of copper cable
            go = FloatingRoot("Salvage_CableCoil", 6f, GameLayers.Salvage);
            t = go.transform;
            go.AddComponent<BoxCollider>().size = new Vector3(0.23f, 0.09f, 0.23f);
            for (int i = 0; i < 3; i++) Part(t, "Winding", Torus(0.078f, 0.016f), new Vector3(0, -0.026f + i * 0.026f, 0), M("Copper"));
            Part(t, "Hub", Tube(0.05f, 0.09f), Vector3.zero, M("Rubber"));
            Part(t, "Flange Top", Tube(0.108f, 0.007f, 18), new Vector3(0, 0.043f, 0), M("Steel"));
            Part(t, "Flange Bottom", Tube(0.108f, 0.007f, 18), new Vector3(0, -0.043f, 0), M("Steel"));
            Part(t, "Loose End", Tube(0.008f, 0.09f, 6), new Vector3(0.11f, 0.02f, 0.03f), M("Copper"), new Vector3(70, 20, 0));
            FinishSalvage(go, "scrap_cable_coil", SurfaceKind.BareMetal);

            // Electronics: navigation board
            go = FloatingRoot("Salvage_NavBoard", 0.6f, GameLayers.Salvage);
            t = go.transform;
            go.AddComponent<BoxCollider>().size = new Vector3(0.25f, 0.05f, 0.17f);
            CBox(t, "Board", Vector3.zero, new Vector3(0.24f, 0.008f, 0.16f), M("Pcb"), 0.002f);
            CBox(t, "Processor", new Vector3(-0.05f, 0.012f, 0.015f), new Vector3(0.055f, 0.014f, 0.055f), M("Chip"), 0.003f);
            for (int i = 0; i < 5; i++) CBox(t, "Heat Fin", new Vector3(-0.07f + i * 0.01f, 0.03f, 0.015f), new Vector3(0.004f, 0.022f, 0.05f), M("Steel"), 0.001f);
            CBox(t, "Memory A", new Vector3(0.04f, 0.01f, -0.035f), new Vector3(0.07f, 0.01f, 0.022f), M("Chip"), 0.002f);
            CBox(t, "Memory B", new Vector3(0.04f, 0.01f, -0.005f), new Vector3(0.07f, 0.01f, 0.022f), M("Chip"), 0.002f);
            CBox(t, "Connector", new Vector3(0f, 0.008f, 0.07f), new Vector3(0.18f, 0.012f, 0.014f), M("Brass"), 0.002f);
            for (int i = 0; i < 4; i++) Part(t, "Capacitor", Tube(0.008f, 0.022f, 8), new Vector3(0.075f + (i % 2) * 0.022f, 0.015f, 0.02f + (i / 2) * 0.024f), M("Chrome"));
            CBox(t, "Led A", new Vector3(-0.1f, 0.008f, -0.06f), new Vector3(0.01f, 0.008f, 0.01f), M("Led"), 0.002f);
            CBox(t, "Led B", new Vector3(-0.08f, 0.008f, -0.06f), new Vector3(0.01f, 0.008f, 0.01f), M("GlowRed"), 0.002f);
            FinishSalvage(go, "elec_nav_board", SurfaceKind.GlassCeramic);

            // Electronics: comms relay
            go = FloatingRoot("Salvage_CommsRelay", 3.5f, GameLayers.Salvage);
            t = go.transform;
            go.AddComponent<BoxCollider>().size = new Vector3(0.18f, 0.15f, 0.22f);
            CBox(t, "Body", Vector3.zero, new Vector3(0.17f, 0.12f, 0.2f), M("ToolBody"), 0.014f);
            CBox(t, "Front Panel", new Vector3(0, 0, -0.1f), new Vector3(0.14f, 0.09f, 0.008f), M("Steel"), 0.003f);
            CBox(t, "Screen", new Vector3(-0.025f, 0.012f, -0.105f), new Vector3(0.07f, 0.04f, 0.003f), M("ScreenGlow"), 0.001f);
            Part(t, "Knob A", Tube(0.012f, 0.012f, 10), new Vector3(0.04f, 0.02f, -0.108f), M("Brass"), new Vector3(90, 0, 0));
            Part(t, "Knob B", Tube(0.012f, 0.012f, 10), new Vector3(0.04f, -0.015f, -0.108f), M("Brass"), new Vector3(90, 0, 0));
            for (int i = 0; i < 5; i++) CBox(t, "Vent", new Vector3(0.086f, 0.03f - i * 0.015f, 0.01f), new Vector3(0.004f, 0.006f, 0.12f), M("Chip"), 0.001f);
            Part(t, "Antenna", Tube(0.005f, 0.14f, 6), new Vector3(0.05f, 0.13f, 0.06f), M("Chrome"));
            Part(t, "Antenna Base", Cone(0.014f, 0.006f, 0.02f), new Vector3(0.05f, 0.07f, 0.06f), M("Steel"));
            CBox(t, "Handle", new Vector3(-0.02f, 0.075f, 0f), new Vector3(0.02f, 0.016f, 0.13f), M("PaintOrange"), 0.005f);
            CBox(t, "Handle Post A", new Vector3(-0.02f, 0.066f, 0.055f), new Vector3(0.018f, 0.02f, 0.018f), M("Steel"), 0.003f);
            CBox(t, "Handle Post B", new Vector3(-0.02f, 0.066f, -0.055f), new Vector3(0.018f, 0.02f, 0.018f), M("Steel"), 0.003f);
            FinishSalvage(go, "elec_comms_relay", SurfaceKind.PaintedMetal);

            // Reactor component: bulky regulator core in a protective cage
            go = FloatingRoot("Salvage_ReactorCore", 40f, GameLayers.Salvage);
            t = go.transform;
            var capsule = go.AddComponent<CapsuleCollider>();
            capsule.radius = 0.15f;
            capsule.height = 0.54f;
            Part(t, "Shell", Lathe("ReactorCore", 18,
                new Vector2(0.09f, -0.25f), new Vector2(0.145f, -0.25f), new Vector2(0.145f, -0.21f), new Vector2(0.118f, -0.19f),
                new Vector2(0.118f, -0.075f), new Vector2(0.1f, -0.06f), new Vector2(0.1f, 0.06f), new Vector2(0.118f, 0.075f),
                new Vector2(0.118f, 0.19f), new Vector2(0.145f, 0.21f), new Vector2(0.145f, 0.25f), new Vector2(0.09f, 0.25f)), Vector3.zero, M("ReactorShell"));
            Part(t, "Glow Core", Tube(0.103f, 0.118f, 18), Vector3.zero, M("ReactorGlow"));
            Part(t, "Ring A", Torus(0.122f, 0.01f), new Vector3(0, 0.13f, 0), M("Chrome"));
            Part(t, "Ring B", Torus(0.122f, 0.01f), new Vector3(0, -0.13f, 0), M("Chrome"));
            for (int i = 0; i < 4; i++)
            {
                float a = (i * 90f + 45f) * Mathf.Deg2Rad;
                CBox(t, "Cage Bar", new Vector3(Mathf.Cos(a) * 0.138f, 0, Mathf.Sin(a) * 0.138f), new Vector3(0.016f, 0.44f, 0.016f), M("Steel"), 0.004f, new Vector3(0, -i * 90f - 45f, 0));
            }
            CBox(t, "Handle", new Vector3(0, 0.31f, 0), new Vector3(0.2f, 0.022f, 0.03f), M("PaintOrange"), 0.008f);
            CBox(t, "Handle Post A", new Vector3(0.09f, 0.28f, 0), new Vector3(0.022f, 0.06f, 0.026f), M("Steel"), 0.005f);
            CBox(t, "Handle Post B", new Vector3(-0.09f, 0.28f, 0), new Vector3(0.022f, 0.06f, 0.026f), M("Steel"), 0.005f);
            CBox(t, "Warning Plate", new Vector3(0, 0.13f, -0.119f), new Vector3(0.07f, 0.04f, 0.004f), M("Hazard"), 0.001f);
            FinishSalvage(go, "reactor_regulator_core", SurfaceKind.PaintedMetal);

            // Novelty: the suspiciously expensive toaster
            go = FloatingRoot("Salvage_OrbitalToaster", 1.8f, GameLayers.Salvage);
            t = go.transform;
            go.AddComponent<BoxCollider>().size = new Vector3(0.28f, 0.2f, 0.17f);
            CBox(t, "Body", Vector3.zero, new Vector3(0.26f, 0.16f, 0.15f), M("Chrome"), 0.03f);
            CBox(t, "Base", new Vector3(0, -0.085f, 0), new Vector3(0.265f, 0.02f, 0.155f), M("Rubber"), 0.006f);
            CBox(t, "Top Plate", new Vector3(0, 0.079f, 0), new Vector3(0.21f, 0.006f, 0.1f), M("Steel"), 0.002f);
            CBox(t, "Slot A", new Vector3(0, 0.082f, 0.026f), new Vector3(0.18f, 0.004f, 0.022f), M("Chip"), 0.001f);
            CBox(t, "Slot B", new Vector3(0, 0.082f, -0.026f), new Vector3(0.18f, 0.004f, 0.022f), M("Chip"), 0.001f);
            CBox(t, "Toast", new Vector3(0.015f, 0.105f, 0.026f), new Vector3(0.12f, 0.085f, 0.016f), M("Toast"), 0.006f, new Vector3(0, 0, 4));
            CBox(t, "Lever Track", new Vector3(0.132f, 0f, 0), new Vector3(0.004f, 0.1f, 0.012f), M("Chip"), 0.001f);
            CBox(t, "Lever", new Vector3(0.142f, 0.03f, 0), new Vector3(0.022f, 0.016f, 0.045f), M("PaintOrange"), 0.005f);
            Part(t, "Dial", Lathe("ToasterDial", 14, new Vector2(0.024f, 0f), new Vector2(0.02f, 0.012f), new Vector2(0.012f, 0.016f)), new Vector3(-0.07f, -0.03f, -0.075f), M("Brass"), new Vector3(-90, 0, 0));
            CBox(t, "Badge", new Vector3(0.04f, 0.035f, -0.0765f), new Vector3(0.08f, 0.022f, 0.003f), M("GlowWarm"), 0.001f);
            foreach (float x in new[] { -0.1f, 0.1f })
                foreach (float z in new[] { -0.055f, 0.055f })
                    Part(t, "Foot", Tube(0.012f, 0.012f, 8), new Vector3(x, -0.1f, z), M("Rubber"));
            Part(t, "Cord Stub", Tube(0.007f, 0.06f, 6), new Vector3(-0.08f, -0.05f, 0.09f), M("Rubber"), new Vector3(70, 0, 0));
            FinishSalvage(go, "novelty_orbital_toaster", SurfaceKind.BareMetal);

            // The hidden bonus: a rubber duck that squeaks (loudly) when its trigger is squeezed.
            go = FloatingRoot("Salvage_RubberDuck", 0.1f, GameLayers.Salvage);
            t = go.transform;
            go.AddComponent<SphereCollider>().radius = 0.065f;
            var duck = Empty(t, "Visual", Vector3.zero).transform;
            Prim(duck, "Body", PrimitiveType.Sphere, new Vector3(0, -0.01f, -0.005f), new Vector3(0.11f, 0.085f, 0.13f), M("DuckYellow"));
            Prim(duck, "Head", PrimitiveType.Sphere, new Vector3(0, 0.05f, 0.035f), new Vector3(0.07f, 0.065f, 0.07f), M("DuckYellow"));
            Part(duck, "Tail", Cone(0.022f, 0.002f, 0.045f, 8), new Vector3(0, 0.012f, -0.068f), M("DuckYellow"), new Vector3(-55, 0, 0));
            CBox(duck, "Beak", new Vector3(0, 0.043f, 0.072f), new Vector3(0.032f, 0.012f, 0.03f), M("PaintOrange"), 0.005f);
            foreach (float side in new[] { -1f, 1f })
            {
                Prim(duck, "Eye", PrimitiveType.Sphere, new Vector3(side * 0.022f, 0.062f, 0.058f), Vector3.one * 0.011f, M("Claw"));
                Prim(duck, "Wing", PrimitiveType.Sphere, new Vector3(side * 0.05f, -0.005f, -0.01f), new Vector3(0.025f, 0.045f, 0.07f), M("DuckYellow"));
            }
            var squeaky = go.AddComponent<SqueakyToy>();
            squeaky.squashTarget = duck;
            FinishSalvage(go, "novelty_rubber_duck", SurfaceKind.RubberPlastic);

            // Tutorial practice scrap (worth nothing)
            go = FloatingRoot("Salvage_TrainingScrap", 1.5f, GameLayers.Salvage);
            t = go.transform;
            go.AddComponent<BoxCollider>().size = new Vector3(0.21f, 0.04f, 0.15f);
            CBox(t, "Plate", Vector3.zero, new Vector3(0.2f, 0.028f, 0.14f), M("ScrapTraining"), 0.006f);
            CBox(t, "Grip Bar", new Vector3(0, 0.022f, 0), new Vector3(0.12f, 0.014f, 0.02f), M("Steel"), 0.004f);
            s_TrainingScrapPrefab = FinishSalvage(go, null, SurfaceKind.PaintedMetal, true);

            // Worthless junk: good for throwing as a distraction.
            go = FloatingRoot("Junk_Can", 0.35f, GameLayers.Salvage);
            t = go.transform;
            var canCollider = go.AddComponent<CapsuleCollider>();
            canCollider.radius = 0.048f;
            canCollider.height = 0.15f;
            Part(t, "Can", Lathe("Can", 14, new Vector2(0.04f, -0.07f), new Vector2(0.046f, -0.066f), new Vector2(0.046f, 0.066f), new Vector2(0.04f, 0.07f)), Vector3.zero, M("Junk"));
            Part(t, "Label", Tube(0.0468f, 0.07f, 14), Vector3.zero, M("PaintOrange"));
            Part(t, "Rim", Torus(0.043f, 0.004f, 14, 6), new Vector3(0, 0.068f, 0), M("Steel"));
            MakeGrabbable(go, false, SurfaceKind.PaintedMetal, out _, out _, out _);
            go.AddComponent<FloatingDebris>();
            s_JunkCanPrefab = SavePrefab(go, go.name);

            go = FloatingRoot("Junk_Crate", 5f, GameLayers.Salvage);
            t = go.transform;
            go.AddComponent<BoxCollider>().size = new Vector3(0.29f, 0.23f, 0.23f);
            CBox(t, "Crate", Vector3.zero, new Vector3(0.28f, 0.22f, 0.22f), M("Container"), 0.012f);
            CBox(t, "Strap A", new Vector3(-0.07f, 0, 0), new Vector3(0.03f, 0.226f, 0.226f), M("Rubber"), 0.004f);
            CBox(t, "Strap B", new Vector3(0.07f, 0, 0), new Vector3(0.03f, 0.226f, 0.226f), M("Rubber"), 0.004f);
            CBox(t, "Lid Seam", new Vector3(0, 0.06f, 0), new Vector3(0.284f, 0.006f, 0.224f), M("Chip"), 0.001f);
            CBox(t, "Label", new Vector3(0, -0.02f, -0.111f), new Vector3(0.09f, 0.05f, 0.003f), M("PaintWhite"), 0.001f);
            MakeGrabbable(go, false, SurfaceKind.RubberPlastic, out _, out _, out _);
            go.AddComponent<FloatingDebris>();
            s_JunkCratePrefab = SavePrefab(go, go.name);
        }

        // ------------------------------------------------------------------ the creature

        /// <summary>
        /// What is left of maintenance unit JANITOR-7: a hunched black carapace with too many eyes, a
        /// jaw full of scavenged spikes, four long clawed limbs and trailing cables. No gore.
        /// </summary>
        /// <summary>
        /// One of the things that can be aboard. 0 = JANITOR-7 (balanced), 1 = the Listener (blind,
        /// sharp hearing), 2 = the Warden (slow, long sight), 3 = the Mimic (plays the player's voice back).
        /// They share a skeleton so one animator drives all.
        /// </summary>
        static GameObject BuildCreaturePrefab(int variant)
        {
            bool listener = variant == 1, warden = variant == 2, mimic = variant == 3;
            string hide = listener ? "PaleHide" : warden ? "WardenPlate" : mimic ? "MimicHide" : "Carapace";
            var go = new GameObject(variant == 0 ? "Drone_Janitor7" : listener ? "Creature_Listener" : warden ? "Creature_Warden" : "Creature_Mimic") { layer = GameLayers.Enemy };
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;

            // "Surface" is moved onto the floor, wall or ceiling by SurfaceWalker; the animated body hangs
            // from it. The collider goes with it, so a dart has to be aimed at where the spider actually is.
            var surfaceGo = Empty(go.transform, "Surface", Vector3.zero, null, GameLayers.Enemy);
            surfaceGo.AddComponent<SphereCollider>().radius = warden ? 0.36f : 0.3f;
            var body = Empty(surfaceGo.transform, "Body", Vector3.zero).transform;

            // The model itself is built in Blender (ModelSource/Spiders/build_spiders.py): one real spider
            // species per creature, with eight segmented legs, fangs, pedipalps, eight eyes and fur.
            var spider = LoadSpider(variant == 0 ? "janitor" : listener ? "listener" : warden ? "warden" : "mimic", body);
            Transform head = spider.head, jaw = spider.jaw;
            GameObject mainEye = spider.eyes[0];
            var extraEyes = new List<Renderer>();
            for (int i = 1; i < spider.eyes.Count; i++) extraEyes.Add(spider.eyes[i].GetComponent<MeshRenderer>());
            var shoulders = spider.legs;
            var elbows = spider.lowerLegs;
            var tendrils = new List<Transform>();

            var eyePoint = Empty(go.transform, "Eye Point", new Vector3(0, 0.05f, 0.3f));
            var lightGo = Empty(head, "Eye Light", new Vector3(0, 0.08f, 0.24f));
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Spot;
            light.spotAngle = 80f;
            light.innerSpotAngle = 35f;
            light.range = 9f;
            light.intensity = 3f;
            light.shadows = LightShadows.None;
            light.color = new Color(0.75f, 0.12f, 0.05f);

            var hum = Empty(go.transform, "Hum", Vector3.zero).AddComponent<AudioSource>();
            ConfigureSpatial(hum, s_Audio != null ? s_Audio.droneHum : null, true, 0.26f, 1.2f, 12f);
            var voice = Empty(go.transform, "Voice", Vector3.zero).AddComponent<AudioSource>();
            ConfigureSpatial(voice, null, true, 0.32f, 1.5f, 16f);

            var drone = go.AddComponent<EnemyDrone>();
            drone.eye = eyePoint.transform;
            drone.eyeLight = light;
            drone.eyeRenderer = mainEye.GetComponent<MeshRenderer>();
            drone.extraEyes = extraEyes.ToArray();
            drone.hum = hum;
            drone.voice = voice;
            drone.hoverHeight = listener ? 1.15f : warden ? 1.55f : 1.45f;
            hum.pitch = listener ? 1.3f : warden ? 0.72f : 1f;
            if (listener)
            {
                drone.displayName = "THE LISTENER";
                drone.trait = "Blind. Hears everything. Go quietly.";
                drone.overrideTuning = true;
                drone.tuning = new EnemyTuning
                {
                    sightRange = 1.6f, sightHalfAngle = 35f, sightAcquireSeconds = 0.5f, hearingRadiusAtFullLoudness = 27f,
                    patrolSpeed = 1.0f, investigateSpeed = 2.3f, chaseSpeed = 3.0f, searchSpeed = 1.5f,
                    investigateLingerSeconds = 6f, chaseLoseSightSeconds = 3.5f, searchSeconds = 6f, catchRadius = 0.9f, wakeDelaySeconds = 20f,
                };
                drone.sedationMultiplier = 1.3f;
            }
            else if (warden)
            {
                drone.displayName = "THE WARDEN";
                drone.trait = "Slow and half deaf. Sees far. Break line of sight.";
                drone.overrideTuning = true;
                drone.tuning = new EnemyTuning
                {
                    sightRange = 14f, sightHalfAngle = 38f, sightAcquireSeconds = 0.7f, hearingRadiusAtFullLoudness = 9f,
                    patrolSpeed = 0.7f, investigateSpeed = 1.2f, chaseSpeed = 2.3f, searchSpeed = 1.0f,
                    investigateLingerSeconds = 4f, chaseLoseSightSeconds = 5f, searchSeconds = 10f, catchRadius = 1.2f, wakeDelaySeconds = 20f,
                };
                drone.sedationMultiplier = 0.6f;
            }
            else if (mimic)
            {
                drone.displayName = "THE MIMIC";
                drone.trait = "Says your own words back to you. Do not answer it.";
                drone.overrideTuning = true;
                drone.tuning = new EnemyTuning
                {
                    sightRange = 8f, sightHalfAngle = 55f, sightAcquireSeconds = 0.9f, hearingRadiusAtFullLoudness = 21f,
                    patrolSpeed = 0.9f, investigateSpeed = 1.8f, chaseSpeed = 2.8f, searchSpeed = 1.3f,
                    investigateLingerSeconds = 5f, chaseLoseSightSeconds = 5f, searchSeconds = 8f, catchRadius = 0.95f, wakeDelaySeconds = 20f,
                };
                var speaker = Empty(head, "Mimic Speaker", new Vector3(0, -0.12f, 0.16f)).AddComponent<AudioSource>();
                ConfigureSpatial(speaker, null, false, 0.85f, 2.5f, 26f);
                var voiceMimic = go.AddComponent<VoiceMimic>();
                voiceMimic.drone = drone;
                voiceMimic.speaker = speaker;
            }
            else
            {
                drone.displayName = "JANITOR-7";
                drone.trait = "Sees and hears about equally.";
            }

            var animator = go.AddComponent<CreatureAnimator>();
            animator.drone = drone;
            animator.body = body;
            animator.head = head;
            animator.jaw = jaw;
            animator.shoulders = shoulders.ToArray();
            animator.elbows = elbows.ToArray();
            animator.tendrils = tendrils.ToArray();
            animator.spider = true;
            animator.abdomen = spider.abdomen;
            animator.palps = spider.palps.ToArray();

            var walker = go.AddComponent<SurfaceWalker>();
            walker.drone = drone;
            walker.surface = surfaceGo.transform;
            walker.bodyHeight = spider.footDrop * 0.92f;
            walker.thread = MakeLine(go.transform, "Thread", 0.004f);
            walker.thread.startColor = walker.thread.endColor = new Color(0.85f, 0.87f, 0.9f);
            var stepSource = Empty(surfaceGo.transform, "Footsteps", Vector3.zero).AddComponent<AudioSource>();
            ConfigureSpatial(stepSource, null, true, 0f, 1.2f, warden ? 18f : 15f);
            walker.steps = stepSource;
            walker.stepVolume = listener ? 0.35f : warden ? 0.75f : mimic ? 0.4f : 0.55f;
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

        /// <summary>
        /// Makes a tool grabbable. <paramref name="gripLocalPos"/> is the point on the handle that sits in the
        /// middle of the fist; the hand's attach point is in the curl of the fingers, so the handle ends up
        /// inside the closed hand and the body of the tool above it, pointing where the controller points.
        /// </summary>
        static XRGrabInteractable ToolGrab(GameObject go, Vector3 gripLocalPos)
        {
            var grip = Empty(go.transform, "Grip", gripLocalPos);
            var grab = go.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.VelocityTracking;
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
            Transform t = go.transform;
            // An angle-head torch: the handle is held upright in the fist and the lamp points forward.
            var col = go.AddComponent<CapsuleCollider>();
            col.direction = 1;
            col.radius = 0.032f;
            col.height = 0.2f;
            col.center = new Vector3(0, -0.03f, 0.008f);
            var x90 = new Vector3(90, 0, 0);
            Part(t, "Handle", Tube(0.019f, 0.13f), new Vector3(0, -0.05f, 0), M("ToolBody"));
            for (int i = 0; i < 4; i++) Part(t, "Grip Ring", Torus(0.0195f, 0.0035f, 14, 6), new Vector3(0, -0.095f + i * 0.022f, 0), M("Rubber"));
            Part(t, "Tail Cap", Tube(0.021f, 0.014f), new Vector3(0, -0.12f, 0), M("Steel"));
            Prim(t, "Elbow", PrimitiveType.Sphere, new Vector3(0, 0.03f, 0), Vector3.one * 0.05f, M("ToolBody"));
            Part(t, "Head", Cone(0.022f, 0.03f, 0.05f, 14), new Vector3(0, 0.032f, 0.03f), M("PaintOrange"), x90);
            Part(t, "Bezel", Tube(0.033f, 0.012f, 16), new Vector3(0, 0.032f, 0.06f), M("Steel"), x90);
            var lens = Part(t, "Lens", Tube(0.028f, 0.003f, 16), new Vector3(0, 0.032f, 0.0665f), M("Lens"), x90);
            CBox(t, "Switch", new Vector3(0, 0.058f, 0.005f), new Vector3(0.014f, 0.008f, 0.022f), M("PaintOrange"), 0.003f);
            CBox(t, "Clip", new Vector3(0, -0.04f, -0.023f), new Vector3(0.012f, 0.08f, 0.004f), M("Steel"), 0.0015f);
            var grab = ToolGrab(go, new Vector3(0, -0.055f, 0));

            var lightGo = Empty(t, "Beam", new Vector3(0, 0.032f, 0.07f));
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
            Transform t = go.transform;
            var col = go.AddComponent<BoxCollider>();
            col.size = new Vector3(0.08f, 0.09f, 0.2f);
            col.center = new Vector3(0, -0.01f, 0.02f);
            CBox(t, "Body", new Vector3(0, 0, 0.02f), new Vector3(0.07f, 0.038f, 0.16f), M("ToolBody"), 0.01f);
            CBox(t, "Grip", new Vector3(0, -0.05f, -0.035f), new Vector3(0.034f, 0.085f, 0.042f), M("Rubber"), 0.01f, new Vector3(15, 0, 0));
            CBox(t, "Trigger Guard", new Vector3(0, -0.035f, 0.005f), new Vector3(0.012f, 0.008f, 0.05f), M("Steel"), 0.002f);
            CBox(t, "Side Stripe L", new Vector3(-0.0355f, 0, 0.02f), new Vector3(0.002f, 0.014f, 0.12f), M("Hazard"), 0.0005f);
            CBox(t, "Side Stripe R", new Vector3(0.0355f, 0, 0.02f), new Vector3(0.002f, 0.014f, 0.12f), M("Hazard"), 0.0005f);
            Part(t, "Dish", Lathe("ScannerDish", 16, new Vector2(0.012f, 0f), new Vector2(0.044f, 0.03f), new Vector2(0.047f, 0.03f), new Vector2(0.016f, -0.004f)), new Vector3(0, 0, 0.1f), M("PaintOrange"), new Vector3(90, 0, 0));
            Part(t, "Emitter Pin", Cone(0.006f, 0.002f, 0.035f, 8), new Vector3(0, 0, 0.125f), M("Chrome"), new Vector3(90, 0, 0));
            Part(t, "Antenna", Tube(0.003f, 0.07f, 6), new Vector3(0.028f, 0.05f, -0.04f), M("Chrome"));
            var grab = ToolGrab(go, new Vector3(0, -0.062f, -0.035f));

            var screenRoot = Empty(t, "Screen", new Vector3(0, 0.052f, -0.01f), Quaternion.Euler(40f, 0f, 0f));
            CBox(screenRoot.transform, "Housing", new Vector3(0, 0, 0.006f), new Vector3(0.13f, 0.085f, 0.012f), M("ToolBody"), 0.004f);
            Prim(screenRoot.transform, "Glass", PrimitiveType.Cube, new Vector3(0, 0, 0.0005f), new Vector3(0.12f, 0.075f, 0.002f), M("Screen"));
            var text = Text(screenRoot.transform, "Text", "SCANNER\nSTANDBY", new Vector3(0, 0, -0.001f), Quaternion.identity, 0.085f, new Vector2(0.115f, 0.07f), new Color(0.5f, 1f, 0.8f));
            var emitter = Empty(t, "Emitter", new Vector3(0, 0, 0.13f));

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
            Transform t = go.transform;
            var col = go.AddComponent<BoxCollider>();
            col.size = new Vector3(0.06f, 0.12f, 0.24f);
            col.center = new Vector3(0, -0.02f, 0.04f);
            var x90 = new Vector3(90, 0, 0);
            CBox(t, "Body", new Vector3(0, 0, 0.03f), new Vector3(0.05f, 0.06f, 0.17f), M("ToolBody"), 0.012f);
            CBox(t, "Grip", new Vector3(0, -0.06f, -0.03f), new Vector3(0.034f, 0.09f, 0.044f), M("Rubber"), 0.01f, new Vector3(15, 0, 0));
            Part(t, "Fuel Tank", Lathe("CutterTank", 12, new Vector2(0f, -0.055f), new Vector2(0.018f, -0.05f), new Vector2(0.022f, -0.035f), new Vector2(0.022f, 0.035f), new Vector2(0.018f, 0.05f), new Vector2(0f, 0.055f)),
                new Vector3(0, -0.05f, 0.06f), M("PaintOrange"), x90);
            Part(t, "Barrel", Tube(0.014f, 0.06f, 12), new Vector3(0, 0, 0.14f), M("Steel"), x90);
            for (int i = 0; i < 4; i++) Part(t, "Heat Fin", Tube(0.022f, 0.004f, 12), new Vector3(0, 0, 0.122f + i * 0.012f), M("Steel"), x90);
            Part(t, "Nozzle", Cone(0.014f, 0.006f, 0.04f, 12), new Vector3(0, 0, 0.19f), M("Brass"), x90);
            Part(t, "Guard Ring", Torus(0.026f, 0.003f, 14, 6), new Vector3(0, 0, 0.165f), M("Chrome"), x90);
            CBox(t, "Top Rail", new Vector3(0, 0.034f, 0.04f), new Vector3(0.016f, 0.008f, 0.12f), M("Steel"), 0.002f);
            CBox(t, "Hazard Band", new Vector3(0, 0, 0.1f), new Vector3(0.052f, 0.062f, 0.012f), M("Hazard"), 0.002f);
            var grab = ToolGrab(go, new Vector3(0, -0.072f, -0.03f));

            var tip = Empty(t, "Tip", new Vector3(0, 0, 0.225f));
            var flame = Part(t, "Flame", Cone(0.007f, 0.001f, 0.05f, 8), new Vector3(0, 0, 0.228f), M("Flame"), x90);
            var flameLight = Empty(t, "Flame Light", new Vector3(0, 0, 0.21f)).AddComponent<Light>();
            flameLight.type = LightType.Point;
            flameLight.range = 2.2f;
            flameLight.intensity = 2.5f;
            flameLight.color = new Color(0.5f, 0.8f, 1f);
            flameLight.shadows = LightShadows.None;

            var readoutRoot = Empty(t, "Readout", new Vector3(0, 0.058f, -0.03f), Quaternion.Euler(40f, 0f, 0f));
            CBox(readoutRoot.transform, "Housing", new Vector3(0, 0, 0.006f), new Vector3(0.108f, 0.068f, 0.012f), M("ToolBody"), 0.004f);
            Prim(readoutRoot.transform, "Glass", PrimitiveType.Cube, new Vector3(0, 0, 0.0005f), new Vector3(0.1f, 0.06f, 0.002f), M("Screen"));
            var readout = Text(readoutRoot.transform, "Text", "HEAT 0%\nREADY", new Vector3(0, 0.006f, -0.001f), Quaternion.identity, 0.085f, new Vector2(0.095f, 0.04f), new Color(1f, 0.85f, 0.5f));
            // Heat bar: a pivot at the left edge scaled along X.
            var barPivot = Empty(readoutRoot.transform, "Heat Bar", new Vector3(-0.045f, -0.022f, -0.001f));
            var barScale = Empty(barPivot.transform, "Scale", Vector3.zero);
            var bar = Prim(barScale.transform, "Fill", PrimitiveType.Cube, new Vector3(0.045f, 0, 0), new Vector3(0.09f, 0.008f, 0.002f), M("GlowWhite"));

            var sparksGo = Empty(t, "Sparks", new Vector3(0, 0, 0.225f));
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

            var loop = Empty(t, "Loop Audio", new Vector3(0, 0, 0.15f)).AddComponent<AudioSource>();
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

        static TranqTool BuildTranq(Transform parent)
        {
            var go = ToolRoot("Tool_TranqLauncher");
            go.transform.SetParent(parent, false);
            Transform t = go.transform;
            var col = go.AddComponent<BoxCollider>();
            col.size = new Vector3(0.05f, 0.12f, 0.2f);
            col.center = new Vector3(0, -0.02f, 0.03f);
            var x90 = new Vector3(90, 0, 0);
            CBox(t, "Body", new Vector3(0, 0, 0.02f), new Vector3(0.04f, 0.05f, 0.13f), M("TranqBody"), 0.01f);
            CBox(t, "Grip", new Vector3(0, -0.055f, -0.03f), new Vector3(0.032f, 0.085f, 0.04f), M("Rubber"), 0.01f, new Vector3(15, 0, 0));
            Part(t, "Barrel", Tube(0.011f, 0.11f, 10), new Vector3(0, 0.005f, 0.13f), M("Steel"), x90);
            Part(t, "Muzzle Ring", Torus(0.013f, 0.003f, 12, 6), new Vector3(0, 0.005f, 0.185f), M("Chrome"), x90);
            // Three spare darts in a clip along the side, with glowing tails.
            for (int i = 0; i < 3; i++)
            {
                Part(t, "Dart", Cone(0.005f, 0.001f, 0.045f, 6), new Vector3(0.025f, 0.012f - i * 0.014f, 0.03f), M("Chrome"), x90);
                Part(t, "Dart Tail", Tube(0.006f, 0.012f, 6), new Vector3(0.025f, 0.012f - i * 0.014f, 0.003f), M("DartGlow"), x90);
            }
            CBox(t, "Sight", new Vector3(0, 0.032f, 0.06f), new Vector3(0.008f, 0.012f, 0.03f), M("Steel"), 0.002f);
            var grab = ToolGrab(go, new Vector3(0, -0.067f, -0.03f));

            var muzzle = Empty(t, "Muzzle", new Vector3(0, 0.005f, 0.19f));
            var readoutRoot = Empty(t, "Readout", new Vector3(0, 0.05f, -0.03f), Quaternion.Euler(40f, 0f, 0f));
            CBox(readoutRoot.transform, "Housing", new Vector3(0, 0, 0.006f), new Vector3(0.1f, 0.055f, 0.012f), M("ToolBody"), 0.004f);
            Prim(readoutRoot.transform, "Glass", PrimitiveType.Cube, new Vector3(0, 0, 0.0005f), new Vector3(0.092f, 0.047f, 0.002f), M("Screen"));
            var readout = Text(readoutRoot.transform, "Text", "TRANQ\nDARTS: 0", new Vector3(0, 0, -0.001f), Quaternion.identity, 0.08f, new Vector2(0.09f, 0.045f), new Color(0.6f, 1f, 0.7f));

            var tool = go.AddComponent<TranqTool>();
            tool.toolName = "Tranquilizer";
            tool.grab = grab;
            tool.muzzle = muzzle.transform;
            tool.readout = readout;
            tool.tracer = MakeLine(t, "Tracer", 0.006f);
            tool.tracer.startColor = tool.tracer.endColor = new Color(0.5f, 1f, 0.6f);
            return tool;
        }

        // ------------------------------------------------------------------ settings panel (shared by tug console and suit menu)

        static SettingsPanel BuildSettingsPanel(Transform parent, string name, Vector3 localPos, Quaternion localRot, float scale)
        {
            var root = Empty(parent, name, localPos, localRot, GameLayers.Default);
            root.transform.localScale = Vector3.one * scale;
            Prim(root.transform, "Back", PrimitiveType.Cube, new Vector3(0, -0.03f, 0.012f), new Vector3(0.56f, 0.54f, 0.02f), M("Screen"));
            var summary = Text(root.transform, "Summary", "SUIT SETTINGS", new Vector3(0, 0.15f, -0.002f), Quaternion.identity, 0.2f, new Vector2(0.52f, 0.17f), new Color(0.75f, 0.95f, 1f));

            var size = new Vector2(0.24f, 0.07f);
            var panel = root.AddComponent<SettingsPanel>();
            panel.summary = summary;
            panel.heightUp = Button(root.transform, "Height Up", new Vector3(-0.13f, 0.02f, 0), Quaternion.identity, size, "HEIGHT +", M("ButtonBlue"), 0.22f);
            panel.heightDown = Button(root.transform, "Height Down", new Vector3(0.13f, 0.02f, 0), Quaternion.identity, size, "HEIGHT -", M("ButtonBlue"), 0.22f);
            panel.snapTurn = Button(root.transform, "Snap Turn", new Vector3(-0.13f, -0.065f, 0), Quaternion.identity, size, "TURN 45", M("ButtonBlue"), 0.22f);
            panel.handedness = Button(root.transform, "Handedness", new Vector3(0.13f, -0.065f, 0), Quaternion.identity, size, "HAND: RIGHT", M("ButtonBlue"), 0.22f);
            panel.drift = Button(root.transform, "Drift", new Vector3(-0.13f, -0.15f, 0), Quaternion.identity, size, "DRIFT: ON", M("ButtonBlue"), 0.22f);
            panel.voice = Button(root.transform, "Voice", new Vector3(0.13f, -0.15f, 0), Quaternion.identity, size, "MIC: ON", M("ButtonBlue"), 0.22f);
            panel.unstuck = Button(root.transform, "Unstuck", new Vector3(-0.13f, -0.235f, 0), Quaternion.identity, size, "UNSTUCK", M("ButtonAmber"), 0.22f);
            panel.recallTools = Button(root.transform, "Recall Tools", new Vector3(0.13f, -0.235f, 0), Quaternion.identity, size, "RECALL TOOLS", M("ButtonAmber"), 0.2f);
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
            // Everything is heard from inside a helmet: the top end is taken off all of it.
            camGo.AddComponent<AudioLowPassFilter>().cutoffFrequency = 6200f;
            var headDriver = camGo.AddComponent<TrackedPoseDriver>();
            headDriver.positionInput = new InputActionProperty(Ref("Head/Position"));
            headDriver.rotationInput = new InputActionProperty(Ref("Head/Rotation"));
            headDriver.trackingStateInput = new InputActionProperty(Ref("Head/TrackingState"));

            var fadeQuad = Prim(camGo.transform, "Fade Quad", PrimitiveType.Quad, new Vector3(0, 0, 0.1f), new Vector3(1.2f, 1.2f, 1f), M("Fade"));
            fadeQuad.GetComponent<MeshRenderer>().enabled = false;
            // The visor: crack overlays and a red flash, a forearm's length in front of the eyes.
            var visor = camGo.AddComponent<VisorDamage>();
            var crackRenderers = new List<Renderer>();
            for (int i = 1; i <= 4; i++)
            {
                var crack = Prim(camGo.transform, "Visor Crack " + i, PrimitiveType.Quad, new Vector3(0, 0, 0.3f + i * 0.0015f), new Vector3(0.98f, 0.98f, 1f), M("VisorCrack" + i));
                var r = crack.GetComponent<MeshRenderer>();
                r.enabled = false;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                crackRenderers.Add(r);
            }
            var flashQuad = Prim(camGo.transform, "Visor Flash", PrimitiveType.Quad, new Vector3(0, 0, 0.29f), new Vector3(1.1f, 1.1f, 1f), M("VisorFlash"));
            flashQuad.GetComponent<MeshRenderer>().enabled = false;
            visor.cracks = crackRenderers.ToArray();
            visor.flash = flashQuad.GetComponent<MeshRenderer>();

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
            var zeroG = rig.AddComponent<ZeroGLocomotion>();
            zeroG.mediator = mediator;

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
            var sideChest = Empty(bodyGo.transform, "Side Chest Holster", new Vector3(-body.sideChestOffset.x, body.sideChestOffset.y, body.sideChestOffset.z), null, GameLayers.Tool);
            body.sideChestSocket = sideChest.transform;

            var locomotion = rig.AddComponent<PlayerLocomotion>();
            locomotion.origin = origin;
            locomotion.teleportation = teleport;
            locomotion.snapTurn = snap;
            locomotion.zeroG = zeroG;
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
            zeroG.leftHand = context.leftHand;
            zeroG.rightHand = context.rightHand;
            zeroG.head = camGo.transform;

            // --- the suit: torso, arms and legs follow the camera and the two hands. The helmet is hidden for the
            // wearer (it would block the view); the torso turns with the body anchor so it never covers the holsters.
            var avatar = BuildAvatar(rig.transform, "Avatar", false);
            avatar.head = camGo.transform;
            avatar.leftHand = context.leftHand.transform;
            avatar.rightHand = context.rightHand.transform;
            avatar.yawSource = bodyGo.transform;
            avatar.lookDownShift = 0.07f; // first person: sit the chest far enough back to see past it to the hip holsters
            avatar.Pose(0f); // save the prefab with the arms already on the gloves, not hanging in the reference pose
            context.avatar = avatar;

            // --- tools in their holsters
            var flashlight = BuildFlashlight(null);
            var scanner = BuildScanner(null);
            var cutter = BuildCutter(null);
            MakeHolster(chest, flashlight, Quaternion.Euler(8f, 0f, 0f));
            MakeHolster(domHip, cutter, Quaternion.Euler(80f, 0f, 0f));
            MakeHolster(offHip, scanner, Quaternion.Euler(80f, 0f, 0f));
            var tranq = BuildTranq(null);
            MakeHolster(sideChest, tranq, Quaternion.Euler(70f, 0f, 0f));
            context.tools = new SuitTool[] { flashlight, scanner, cutter, tranq };

            // --- wrist display
            var wrist = Empty(rig.transform, "Wrist Display", Vector3.zero, null, GameLayers.Default);
            Prim(wrist.transform, "Back", PrimitiveType.Cube, new Vector3(0, -0.013f, 0.004f), new Vector3(0.125f, 0.118f, 0.006f), M("Screen"));
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
            // Noise meter: how loud you are being, from your voice or the last loud thing you did.
            var noisePivot = Empty(wrist.transform, "Noise Bar", new Vector3(-0.056f, -0.05f, -0.001f));
            Prim(noisePivot.transform, "Track", PrimitiveType.Cube, new Vector3(0.056f, 0, 0.0008f), new Vector3(0.112f, 0.009f, 0.001f), M("Trim"));
            var noiseScale = Empty(noisePivot.transform, "Scale", Vector3.zero);
            var noiseFill = Prim(noiseScale.transform, "Fill", PrimitiveType.Cube, new Vector3(0.056f, 0, 0), new Vector3(0.112f, 0.008f, 0.002f), M("GlowWhite"));
            noiseScale.transform.localScale = new Vector3(0.001f, 1f, 1f);
            display.noiseBar = noiseScale.transform;
            display.noiseBarRenderer = noiseFill.GetComponent<MeshRenderer>();
            display.voiceText = Text(wrist.transform, "Voice", "NOISE: quiet", new Vector3(0, -0.063f, -0.001f), Quaternion.identity, 0.055f, new Vector2(0.12f, 0.012f), new Color(0.6f, 1f, 0.7f));
            context.wristDisplay = wrist.transform;

            context.visor = visor;

            // --- one-way pushers: the chest shoulders floating junk aside, an open hand can swat it
            context.bumpers = new[]
            {
                MakeBumper(rig.transform, "Body Bumper", chest.transform, new Vector3(0, -0.2f, -0.12f), 0.24f, 0.9f, 0f, null),
                MakeBumper(rig.transform, "Left Hand Bumper", context.leftHand.interactor.transform, Vector3.zero, 0.09f, 0f, 0.9f, context.leftHand),
                MakeBumper(rig.transform, "Right Hand Bumper", context.rightHand.interactor.transform, Vector3.zero, 0.09f, 0f, 0.9f, context.rightHand),
            };

            // --- suit menu
            var menuRoot = Empty(rig.transform, "Suit Menu", Vector3.zero, null, GameLayers.Default);
            var menuPanel = BuildSettingsPanel(menuRoot.transform, "Panel", Vector3.zero, Quaternion.identity, 1f);
            var menu = rig.AddComponent<WristMenu>();
            menu.player = context;
            menu.panel = menuRoot;

            var promptRoot = Empty(rig.transform, "Tutorial Prompt", new Vector3(0, 1.2f, 1f), null, GameLayers.Default);
            var promptVisuals = Empty(promptRoot.transform, "Visuals", Vector3.zero, null, GameLayers.Default);
            CBox(promptVisuals.transform, "Back", new Vector3(0, 0, 0.008f), new Vector3(0.74f, 0.27f, 0.012f), M("Screen"), 0.005f);
            CBox(promptVisuals.transform, "Accent", new Vector3(0, 0.128f, 0.006f), new Vector3(0.74f, 0.012f, 0.014f), M("GlowWarm"), 0.003f);
            var prompt = promptRoot.AddComponent<TutorialPrompt>();
            prompt.head = camGo.transform;
            prompt.visuals = promptVisuals;
            prompt.title = Text(promptVisuals.transform, "Title", "WELCOME ABOARD", new Vector3(0, 0.092f, -0.001f), Quaternion.identity, 0.34f, new Vector2(0.7f, 0.05f), new Color(1f, 0.85f, 0.3f));
            prompt.title.fontStyle = FontStyles.Bold;
            prompt.body = Text(promptVisuals.transform, "Body", "", new Vector3(0, -0.02f, -0.001f), Quaternion.identity, 0.25f, new Vector2(0.7f, 0.17f), new Color(0.88f, 0.96f, 1f));
            promptVisuals.SetActive(false);

            var voice = rig.AddComponent<VoiceSensor>();
            voice.player = context;
            voice.head = camGo.transform;
            context.voice = voice;

            var suitAudio = rig.AddComponent<SuitAudio>();
            suitAudio.player = context;
            suitAudio.breathing = HelmetSource(camGo.transform, "Breathing", s_Audio != null ? s_Audio.breathing : null);
            suitAudio.heartbeat = HelmetSource(camGo.transform, "Heartbeat", s_Audio != null ? s_Audio.heartbeat : null);

            var alerts = rig.AddComponent<SuitAlerts>();
            alerts.player = context;

            return SavePrefab(rig, "PlayerRig");
        }

        /// <summary>A non-positional looping source for sounds inside the helmet.</summary>
        static AudioSource HelmetSource(Transform parent, string name, AudioClip clip)
        {
            var source = Empty(parent, name, Vector3.zero).AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = true;
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.volume = 0f;
            return source;
        }

        static Bumper MakeBumper(Transform rigRoot, string name, Transform target, Vector3 offset, float radius, float height, float minSpeed, HandRig hand)
        {
            var bumper = Empty(rigRoot, name, Vector3.zero, null, GameLayers.Default).AddComponent<Bumper>();
            bumper.target = target;
            bumper.localOffset = offset;
            bumper.radius = radius;
            bumper.height = height;
            bumper.minSpeed = minSpeed;
            bumper.hand = hand;
            return bumper;
        }

        static void MakeHolster(GameObject holsterGo, SuitTool tool, Quaternion dockRotation)
        {
            var trigger = holsterGo.AddComponent<SphereCollider>();
            trigger.isTrigger = true;
            trigger.radius = 0.1f;
            var interactable = holsterGo.AddComponent<XRSimpleInteractable>();
            interactable.selectMode = InteractableSelectMode.Single;
            // Only the holster's own trigger belongs to it. Left empty, XRI would also claim the docked
            // tool's colliders (the tool is parented under the holster) and the tool could not be grabbed directly.
            interactable.colliders.Add(trigger);

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

            // The controller is held like a handshake: thumb up, palm facing the other hand. The glove is
            // modelled flat (palm down, fingers forward) and rolled a quarter turn here so it matches the
            // real hand, sitting beside the controller's centre line instead of on top of it.
            // Grab interactor: one sphere, no rigidbody, so XRI uses its accurate overlap mode. It sits just
            // in front of the palm, where a closed fist holds things.
            var grabGo = Empty(go.transform, "Grab Interactor", new Vector3(0, -0.035f, -0.075f), null, GameLayers.Hand);
            var sphere = grabGo.AddComponent<SphereCollider>();
            sphere.isTrigger = true;
            sphere.radius = 0.09f;
            // Tool handles go a little further forward, in the curl of the fingers.
            var attach = Empty(grabGo.transform, "Attach", new Vector3(0, 0, 0.03f), null, GameLayers.Hand);
            var interactor = grabGo.AddComponent<XRDirectInteractor>();
            ConfigureButtonReader(interactor.selectInput, side + "/Select", side + "/SelectValue");
            ConfigureButtonReader(interactor.activateInput, side + "/Activate", side + "/ActivateValue");
            interactor.handedness = left ? InteractorHandedness.Left : InteractorHandedness.Right;
            // Find grab targets with overlap queries against the grabbable layers. The default mode
            // relies on physics trigger events, which the Hand layer does not receive (its collisions are
            // switched off so hands can never shove the player or salvage), and it cannot see holsters,
            // which have no rigidbody.
            interactor.improveAccuracyWithSphereCollider = true;
            interactor.physicsLayerMask = GameLayers.GrabbableMask;
            interactor.physicsTriggerInteraction = QueryTriggerInteraction.Collide;
            interactor.attachTransform = attach.transform;

            // Glove: the EVA glove modelled in Blender (build_hands.py), its finger segments following grip and
            // trigger. If the model files are missing, the old padded stand-in glove made of boxes is built instead.
            var glove = Empty(go.transform, "Glove", new Vector3(left ? -0.065f : 0.065f, -0.035f, 0f), Quaternion.Euler(0f, 0f, left ? 90f : -90f), GameLayers.Hand);
            Transform g = glove.transform;
            var gloveVisual = glove.AddComponent<GloveVisual>();
            if (!BuildModelGlove(g, left, gloveVisual))
                BuildStandInGlove(g, s, gloveVisual);
            var fingertip = Empty(g, "Fingertip", new Vector3(s * 0.031f, -0.03f, 0.055f), null, GameLayers.Hand);

            // Suit thruster on the back of the wrist: a nozzle, and a flame that shows while it fires.
            Part(g, "Thruster Nozzle", Cone(0.013f, 0.009f, 0.03f, 10), new Vector3(0, 0.006f, -0.165f), M("Steel"), new Vector3(-90, 0, 0), GameLayers.Hand);
            var thrusterFx = Empty(g, "Thruster Flame", new Vector3(0, 0.006f, -0.18f), null, GameLayers.Hand);
            Part(thrusterFx.transform, "Flame", Cone(0.009f, 0.001f, 0.09f, 8), new Vector3(0, 0, -0.045f), M("Flame"), new Vector3(-90, 0, 0), GameLayers.Hand);
            thrusterFx.SetActive(false);
            var thrusterAudio = Empty(go.transform, "Thruster Audio", Vector3.zero, null, GameLayers.Hand).AddComponent<AudioSource>();
            ConfigureSpatial(thrusterAudio, s_Audio != null ? s_Audio.thruster : null, true, 0.3f, 0.3f, 6f);

            var wristMount = Empty(go.transform, "Wrist Mount", new Vector3(left ? -0.035f : 0.035f, 0.032f, -0.19f),
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
            hand.thumbstickClick = Ref(side + "/ThumbstickClick");
            hand.thrusterVisual = thrusterFx;
            hand.thrusterAudio = thrusterAudio;

            gloveVisual.hand = hand;

            var clamp = glove.AddComponent<HandWallClamp>();
            clamp.hand = hand;
            clamp.head = cameraOffset.Find("Main Camera");

            var tracking = go.AddComponent<HandTrackingDriver>();
            tracking.hand = hand;
            tracking.glove = glove.transform;
            tracking.poseDriver = driver;
            tracking.trackingSpace = cameraOffset;
            tracking.head = cameraOffset.Find("Main Camera");

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

        /// <summary>The old glove made of rounded boxes: built only when the Blender glove model is missing.</summary>
        static void BuildStandInGlove(Transform g, float s, GloveVisual gloveVisual)
        {
            CBox(g, "Palm", new Vector3(0, -0.03f, -0.075f), new Vector3(0.086f, 0.032f, 0.098f), M("PaintWhite"), 0.011f);
            CBox(g, "Back Plate", new Vector3(0, -0.011f, -0.078f), new Vector3(0.062f, 0.01f, 0.064f), M("PaintOrange"), 0.004f);
            CBox(g, "Knuckle Guard", new Vector3(0, -0.018f, -0.03f), new Vector3(0.082f, 0.016f, 0.02f), M("Rubber"), 0.006f);
            CBox(g, "Cuff", new Vector3(0, -0.03f, -0.152f), new Vector3(0.096f, 0.052f, 0.06f), M("PaintWhite"), 0.014f);
            Part(g, "Cuff Ring", Torus(0.05f, 0.008f, 16, 6), new Vector3(0, -0.03f, -0.125f), M("Steel"), new Vector3(90, 0, 0));
            Part(g, "Wrist Seal", Torus(0.048f, 0.006f, 16, 6), new Vector3(0, -0.03f, -0.18f), M("PaintOrange"), new Vector3(90, 0, 0));
            var indexPivots = new List<Transform>();
            var pivots = new List<Transform>();
            float[] fingerX = { 0.031f, 0.0105f, -0.0105f, -0.031f };
            float[] fingerLen = { 0.04f, 0.044f, 0.041f, 0.034f };
            for (int i = 0; i < 4; i++)
            {
                var p1 = Empty(g, "Finger" + i, new Vector3(s * fingerX[i], -0.03f, -0.026f), null, GameLayers.Hand).transform;
                CBox(p1, "Segment", new Vector3(0, 0, fingerLen[i] * 0.5f), new Vector3(0.0185f, 0.021f, fingerLen[i]), M("PaintWhite"), 0.006f);
                var p2 = Empty(p1, "Joint2", new Vector3(0, 0, fingerLen[i]), null, GameLayers.Hand).transform;
                CBox(p2, "Segment", new Vector3(0, 0, 0.017f), new Vector3(0.017f, 0.019f, 0.036f), M("PaintWhite"), 0.006f);
                CBox(p2, "Pad", new Vector3(0, -0.006f, 0.022f), new Vector3(0.014f, 0.01f, 0.02f), M("Rubber"), 0.004f);
                var list = i == 0 ? indexPivots : pivots;
                list.Add(p1);
                list.Add(p2);
            }
            var thumbPivot = Empty(g, "Thumb", new Vector3(s * 0.046f, -0.034f, -0.095f), null, GameLayers.Hand);
            CBox(thumbPivot.transform, "Segment", new Vector3(s * 0.014f, 0, 0.026f), new Vector3(0.022f, 0.022f, 0.05f), M("PaintWhite"), 0.007f, new Vector3(0, s * 28f, 0));
            CBox(thumbPivot.transform, "Tip", new Vector3(s * 0.03f, 0, 0.058f), new Vector3(0.02f, 0.02f, 0.034f), M("Rubber"), 0.007f, new Vector3(0, s * 18f, 0));
            gloveVisual.indexPivots = indexPivots.ToArray();
            gloveVisual.gripPivots = pivots.ToArray();
            gloveVisual.thumbPivot = thumbPivot.transform;
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

using System.Collections.Generic;
using TMPro;
using TrashAstronauts.Mission;
using TrashAstronauts.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// The SALVAGE CLAW (see <see cref="SalvageClawStation"/>): an observation window cut into the supply bay's south
    /// wall above the two cargo crates, a console bolted on top of the crates, and outside on the hull a huge arm
    /// (turret, two booms with hydraulic rams, a four-finger claw with a work light), an intake hatch beside the
    /// window, the cargo that drifts past and a star field behind it all.
    ///
    /// Station space: origin on the floor below the window centre in the wall's mid-plane, +Z out through the
    /// window, +X to the operator's right. The crates (Builder.Maps BuildCockpit) are the console's base.
    /// </summary>
    public static partial class Builder
    {
        /// <summary>The station origin (world) and the window opening in station space (x half-width, sill and head heights).</summary>
        public static readonly Vector3 ClawStationOrigin = new Vector3(-3.175f, 0f, -6.6f);
        public const float ClawWindowHalfWidth = 0.875f, ClawWindowSill = 1.1f, ClawWindowHead = 2.3f;
        /// <summary>Where the operator stands (station space).</summary>
        public static readonly Vector3 ClawStandLocal = new Vector3(0f, 0f, -1.35f);

        static SalvageClawStation BuildSalvageClaw(MissionDirector director)
        {
            var root = Empty(s_Props, "Salvage Claw Station", ClawStationOrigin, Quaternion.LookRotation(Vector3.back), GameLayers.Default);
            Transform t = root.transform;
            Vector3 W(Vector3 local) => t.TransformPoint(local);

            // --- materials: outside there is only ambient light and the work light, so the paint carries a little emission
            var hazardTex = M("Hazard").GetTexture("_BaseMap") as Texture2D;
            var containerTex = M("Container").GetTexture("_BaseMap") as Texture2D;
            var plateTex = M("Plate").GetTexture("_BaseMap") as Texture2D;
            var starsTex = M("Stars").GetTexture("_BaseMap") as Texture2D;
            var paint = Lit("ClawPaint", new Color(0.95f, 0.55f, 0.12f), 0.35f, null, new Color(0.2f, 0.1f, 0.025f));
            var steel = Lit("ClawSteel", new Color(0.5f, 0.52f, 0.56f), 0.5f, null, new Color(0.06f, 0.065f, 0.075f));
            var dark = Lit("ClawDark", new Color(0.16f, 0.17f, 0.19f), 0.4f, null, new Color(0.02f, 0.02f, 0.025f));
            var chrome = Lit("ClawChrome", new Color(0.82f, 0.84f, 0.88f), 0.85f, null, new Color(0.12f, 0.12f, 0.13f));
            var hazard = Lit("ClawHazard", Color.white, 0.3f, hazardTex, new Color(0.1f, 0.09f, 0.05f));
            var crate = Lit("ClawCrate", new Color(0.9f, 0.55f, 0.2f), 0.3f, containerTex, new Color(0.14f, 0.08f, 0.025f));
            var longMat = Lit("ClawLongContainer", new Color(0.3f, 0.5f, 0.85f), 0.3f, containerTex, new Color(0.03f, 0.06f, 0.12f));
            var scrap = Lit("ClawScrap", new Color(0.55f, 0.53f, 0.5f), 0.3f, plateTex, new Color(0.06f, 0.06f, 0.06f));
            var strong = Lit("ClawStrongbox", new Color(0.13f, 0.13f, 0.15f), 0.6f, null, new Color(0.02f, 0.02f, 0.025f));
            var gold = Lit("ClawGold", new Color(1f, 0.78f, 0.25f), 0.7f, null, new Color(0.5f, 0.33f, 0.06f));
            var glass = Unlit("ClawGlass", new Color(0.55f, 0.75f, 0.95f, 0.07f), true);
            var sky = Unlit("ClawSky", new Color(3.2f, 3.2f, 3.4f), false, starsTex);
            sky.SetTextureScale("_BaseMap", new Vector2(5f, 3.5f));
            EditorUtility.SetDirty(sky);

            CutClawWindow();

            // --- window frame, glass and the sill stripe
            float hw = ClawWindowHalfWidth, sill = ClawWindowSill, head = ClawWindowHead, midY = (sill + head) * 0.5f, hh = (head - sill) * 0.5f;
            var frame = Empty(t, "Window Frame", Vector3.zero, null, GameLayers.Default).transform;
            CBox(frame, "Head", new Vector3(0, head + 0.05f, 0), new Vector3(hw * 2f + 0.24f, 0.1f, 0.3f), M("Trim"), 0.02f);
            CBox(frame, "Sill", new Vector3(0, sill - 0.05f, -0.02f), new Vector3(hw * 2f + 0.24f, 0.1f, 0.34f), M("Trim"), 0.02f);
            foreach (float s in new[] { -1f, 1f })
                CBox(frame, "Jamb", new Vector3(s * (hw + 0.06f), midY, 0), new Vector3(0.12f, head - sill + 0.2f, 0.3f), M("Trim"), 0.02f);
            CBox(frame, "Sill Stripe", new Vector3(0, sill - 0.13f, -0.105f), new Vector3(hw * 2f + 0.2f, 0.06f, 0.012f), M("Hazard"), 0.004f);
            Prim(frame, "Glass", PrimitiveType.Quad, new Vector3(0, midY, 0.04f), new Vector3(hw * 2f, head - sill, 1f), glass);

            // --- the console, bolted on top of the cargo crates
            var console = Empty(t, "Claw Console", Vector3.zero, null, GameLayers.Default).transform;
            CBox(console, "Riser Left", new Vector3(-0.425f, 0.85f, -0.6f), new Vector3(0.8f, 0.1f, 0.5f), M("Steel"), 0.015f);
            CBox(console, "Riser Right", new Vector3(0.4f, 0.75f, -0.55f), new Vector3(0.3f, 0.3f, 0.5f), M("Steel"), 0.015f);
            const float deskY = 0.95f;
            Box(console, "Desk", W(new Vector3(-0.13f, deskY - 0.025f, -0.7f)), new Vector3(1.38f, 0.05f, 0.52f), M("Trim"), GameLayers.Environment, true, false);
            CBox(console, "Fascia", new Vector3(-0.13f, 0.84f, -0.955f), new Vector3(1.38f, 0.2f, 0.03f), M("Screen"), 0.006f);
            CBox(console, "Fascia Stripe", new Vector3(-0.13f, 0.75f, -0.972f), new Vector3(1.38f, 0.03f, 0.006f), M("Hazard"), 0.002f);
            var title = Text(console, "Title", "SALVAGE CLAW", new Vector3(-0.38f, 0.85f, -0.973f), Quaternion.identity, 0.7f, new Vector2(0.8f, 0.14f), new Color(1f, 0.78f, 0.3f));
            title.fontStyle = FontStyles.Bold;
            Text(console, "Pay Note", "PAID PER CATCH\nUP TO " + SalvageClawConfig.ShiftCap + " cr PER SHIFT", new Vector3(0.28f, 0.85f, -0.973f), Quaternion.identity, 0.2f, new Vector2(0.5f, 0.1f), new Color(0.75f, 0.9f, 1f));

            // Monitor: low and tilted back so the window stays clear above it.
            var mon = Empty(console, "Monitor", new Vector3(0.0f, deskY + 0.16f, -0.56f), Quaternion.Euler(28f, 0, 0), GameLayers.Default).transform;
            CBox(mon, "Housing", new Vector3(0, 0, 0.02f), new Vector3(0.5f, 0.31f, 0.04f), M("Trim"), 0.01f);
            CBox(mon, "Screen", new Vector3(0, 0, -0.001f), new Vector3(0.46f, 0.27f, 0.004f), M("Screen"), 0.001f);
            CBox(mon, "Stand", new Vector3(0, -0.13f, 0.06f), new Vector3(0.08f, 0.16f, 0.04f), M("Steel"), 0.008f, new Vector3(-28f, 0, 0));
            var monitor = Text(mon, "Text", "SALVAGE CLAW", new Vector3(0, 0, -0.006f), Quaternion.identity, 0.26f, new Vector2(0.43f, 0.25f), new Color(0.55f, 1f, 0.7f), TextAlignmentOptions.TopLeft);

            // Buttons lie flat on the desk (their +Z points down into it); labels read from the operator's side.
            Quaternion flat = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            var extend = Button(console, "Extend Button", new Vector3(-0.6f, deskY + 0.001f, -0.6f), flat, new Vector2(0.17f, 0.12f), "EXTEND", M("ButtonGreen"), 0.42f);
            var retract = Button(console, "Retract Button", new Vector3(-0.6f, deskY + 0.001f, -0.84f), flat, new Vector2(0.17f, 0.12f), "RETRACT", M("ButtonAmber"), 0.42f);
            var clawButton = Button(console, "Claw Button", new Vector3(-0.3f, deskY + 0.001f, -0.8f), flat, new Vector2(0.18f, 0.16f), "CLAW", M("GlowRed"), 0.5f);
            extend.cooldown = retract.cooldown = 0.2f;
            Text(console, "Stick Note", "GRIP STICK - TRIGGER = CLAW\nPULL BACK = UP  PUSH = DOWN", new Vector3(0.36f, deskY + 0.003f, -0.9f), flat, 0.15f, new Vector2(0.34f, 0.07f), new Color(1f, 0.85f, 0.4f));
            Text(console, "Reach Note", "HOLD TO MOVE", new Vector3(-0.6f, deskY + 0.003f, -0.72f), flat, 0.2f, new Vector2(0.2f, 0.04f), new Color(0.75f, 0.9f, 1f));

            // The stick: a rubber boot, a steel shaft and a chunky grip with a red cap.
            var stickRoot = Empty(console, "Claw Stick", new Vector3(0.38f, deskY, -0.74f), null, GameLayers.Default).transform;
            CBox(stickRoot, "Base Plate", new Vector3(0, 0.01f, 0), new Vector3(0.16f, 0.02f, 0.16f), M("Steel"), 0.006f);
            Part(stickRoot, "Boot", Cone(0.055f, 0.022f, 0.06f, 12), new Vector3(0, 0.05f, 0), M("Rubber"));
            var pivot = Empty(stickRoot, "Pivot", new Vector3(0, 0.03f, 0), null, GameLayers.Default).transform;
            Part(pivot, "Shaft", Tube(0.011f, 0.15f, 8), new Vector3(0, 0.075f, 0), M("Chrome"));
            var knob = Empty(pivot, "Knob", new Vector3(0, 0.19f, 0), null, GameLayers.Tool);
            CBox(knob.transform, "Grip", Vector3.zero, new Vector3(0.048f, 0.1f, 0.052f), M("Rubber"), 0.016f);
            CBox(knob.transform, "Cap", new Vector3(0, 0.055f, 0.004f), new Vector3(0.04f, 0.014f, 0.044f), M("GlowRed"), 0.005f);
            var knobTrigger = knob.AddComponent<SphereCollider>();
            knobTrigger.isTrigger = true;
            knobTrigger.radius = 0.07f;
            var interactable = knob.AddComponent<XRSimpleInteractable>();
            interactable.selectMode = InteractableSelectMode.Single;
            interactable.colliders.Add(knobTrigger);
            var stick = stickRoot.gameObject.AddComponent<SalvageClawStick>();
            stick.pivot = pivot;
            stick.interactable = interactable;
            stick.knobHeight = 0.19f;

            var stand = Empty(t, "Operator Spot", ClawStandLocal, null, GameLayers.Default).transform;
            var servo = Empty(console, "Servo Audio", new Vector3(0, deskY, -0.5f), null, GameLayers.Default).AddComponent<AudioSource>();
            ConfigureSpatial(servo, s_Audio != null ? s_Audio.doorMove : null, true, 0.3f, 1f, 6f);
            servo.playOnAwake = false;

            // --- the arm, on a bracket off the hull to the operator's left
            var arm = Empty(t, "Claw Arm", Vector3.zero, null, GameLayers.Default).transform;
            Vector3 axis = new Vector3(-1.45f, 0f, 1.05f);
            CBox(arm, "Hull Plate", new Vector3(axis.x, 0.45f, 0.16f), new Vector3(1.5f, 1.7f, 0.12f), steel, 0.03f);
            CBox(arm, "Bracket", new Vector3(axis.x, 0.16f, 0.62f), new Vector3(1.15f, 0.32f, 1.05f), steel, 0.04f);
            CBox(arm, "Bracket Stripe", new Vector3(axis.x, 0.16f, 1.155f), new Vector3(1.16f, 0.2f, 0.02f), hazard, 0.004f);
            CBox(arm, "Brace", new Vector3(axis.x, -0.45f, 0.55f), new Vector3(0.22f, 0.22f, 1.25f), steel, 0.03f, new Vector3(-48f, 0, 0));
            Part(arm, "Turret Ring", Tube(0.56f, 0.12f, 20), new Vector3(axis.x, 0.38f, axis.z), dark);
            var turret = Empty(arm, "Turret", new Vector3(axis.x, 0.44f, axis.z), null, GameLayers.Default).transform;
            CBox(turret, "Housing", new Vector3(0, 0.26f, 0), new Vector3(0.95f, 0.5f, 0.95f), paint, 0.06f);
            CBox(turret, "Housing Band", new Vector3(0, 0.07f, 0), new Vector3(0.97f, 0.1f, 0.97f), hazard, 0.01f);
            foreach (float s in new[] { -1f, 1f })
                CBox(turret, "Yoke", new Vector3(s * 0.36f, 0.72f, 0f), new Vector3(0.12f, 0.6f, 0.62f), steel, 0.03f);
            CBox(turret, "Ram Mount", new Vector3(0, 0.36f, 0.5f), new Vector3(0.62f, 0.2f, 0.16f), steel, 0.02f);

            const float L1 = SalvageClawConfig.UpperBoomLength, L2 = SalvageClawConfig.ForeBoomLength;
            var shoulder = Empty(turret, "Shoulder", new Vector3(0, 0.85f, 0), null, GameLayers.Default).transform;
            Part(shoulder, "Shoulder Hub", Tube(0.23f, 0.84f, 16), Vector3.zero, dark, new Vector3(0, 0, 90f));
            CBox(shoulder, "Upper Boom", new Vector3(0, 0, L1 * 0.5f), new Vector3(0.38f, 0.42f, L1 - 0.25f), paint, 0.04f);
            CBox(shoulder, "Upper Stripe A", new Vector3(0, 0, 0.55f), new Vector3(0.4f, 0.44f, 0.3f), hazard, 0.02f);
            CBox(shoulder, "Upper Stripe B", new Vector3(0, 0, L1 - 0.55f), new Vector3(0.4f, 0.44f, 0.3f), hazard, 0.02f);
            Part(shoulder, "Upper Cable", Tube(0.035f, L1 - 1.0f, 6), new Vector3(0.13f, 0.24f, L1 * 0.5f), dark, new Vector3(90f, 0, 0));
            var ramA1 = Empty(turret, "Ram Anchor L", new Vector3(-0.26f, 0.38f, 0.5f), null, GameLayers.Default).transform;
            var ramA2 = Empty(turret, "Ram Anchor R", new Vector3(0.26f, 0.38f, 0.5f), null, GameLayers.Default).transform;
            var ramB1 = Empty(shoulder, "Ram Lug L", new Vector3(-0.26f, -0.12f, 2.1f), null, GameLayers.Default).transform;
            var ramB2 = Empty(shoulder, "Ram Lug R", new Vector3(0.26f, -0.12f, 2.1f), null, GameLayers.Default).transform;
            CBox(shoulder, "Ram Lug Plate", new Vector3(0, -0.12f, 2.1f), new Vector3(0.64f, 0.12f, 0.22f), steel, 0.02f);

            var elbow = Empty(shoulder, "Elbow", new Vector3(0, 0, L1), null, GameLayers.Default).transform;
            Part(elbow, "Elbow Hub", Tube(0.22f, 0.66f, 16), Vector3.zero, dark, new Vector3(0, 0, 90f));
            CBox(elbow, "Fore Boom", new Vector3(0, 0, (L2 - 1.3f) * 0.5f + 0.1f), new Vector3(0.32f, 0.36f, L2 - 1.3f), paint, 0.035f);
            CBox(elbow, "Fore Sleeve", new Vector3(0, 0, L2 - 0.72f), new Vector3(0.24f, 0.27f, 1.3f), steel, 0.03f);
            CBox(elbow, "Fore Stripe", new Vector3(0, 0, L2 - 1.25f), new Vector3(0.34f, 0.38f, 0.22f), hazard, 0.02f);
            CBox(shoulder, "Elbow Ram Lug", new Vector3(0, 0.27f, L1 - 1.7f), new Vector3(0.16f, 0.14f, 0.2f), steel, 0.02f);
            var ramC1 = Empty(shoulder, "Elbow Ram Anchor", new Vector3(0, 0.3f, L1 - 1.7f), null, GameLayers.Default).transform;
            var ramC2 = Empty(elbow, "Elbow Ram Lug", new Vector3(0, 0.25f, 0.75f), null, GameLayers.Default).transform;
            CBox(elbow, "Elbow Ram Plate", new Vector3(0, 0.22f, 0.75f), new Vector3(0.16f, 0.1f, 0.2f), steel, 0.02f);

            var wrist = Empty(elbow, "Wrist", new Vector3(0, 0, L2), null, GameLayers.Default).transform;
            Part(wrist, "Wrist Hub", Tube(0.17f, 0.44f, 14), Vector3.zero, dark, new Vector3(0, 0, 90f));
            CBox(wrist, "Palm", new Vector3(0, 0, 0.27f), new Vector3(0.56f, 0.56f, 0.36f), paint, 0.05f);
            CBox(wrist, "Palm Band", new Vector3(0, 0, 0.42f), new Vector3(0.58f, 0.58f, 0.08f), hazard, 0.01f);
            CBox(wrist, "Lamp Housing", new Vector3(0, 0.34f, 0.3f), new Vector3(0.2f, 0.12f, 0.16f), dark, 0.02f);
            CBox(wrist, "Lamp Lens", new Vector3(0, 0.34f, 0.385f), new Vector3(0.15f, 0.07f, 0.01f), M("GlowWhite"), 0.003f);
            var lightGo = Empty(wrist, "Work Light", new Vector3(0, 0.34f, 0.4f), Quaternion.Euler(6f, 0, 0), GameLayers.Default);
            var work = lightGo.AddComponent<Light>();
            work.type = LightType.Spot;
            work.color = new Color(1f, 0.93f, 0.8f);
            work.intensity = 7f;
            work.range = 13f;
            work.spotAngle = 62f;
            work.innerSpotAngle = 30f;
            work.shadows = LightShadows.None;

            var fingers = new List<Transform>();
            for (int i = 0; i < 4; i++)
            {
                float a = (45f + i * 90f) * Mathf.Deg2Rad;
                Vector3 radial = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                var mount = Empty(wrist, "Finger Mount", radial * 0.2f + new Vector3(0, 0, 0.44f), Quaternion.LookRotation(Vector3.forward, radial), GameLayers.Default).transform;
                var hinge = Empty(mount, "Finger", Vector3.zero, null, GameLayers.Default).transform;
                CBox(hinge, "Knuckle", Vector3.zero, new Vector3(0.14f, 0.12f, 0.12f), dark, 0.02f);
                CBox(hinge, "Segment", new Vector3(0, 0.02f, 0.28f), new Vector3(0.11f, 0.1f, 0.5f), steel, 0.02f);
                var tip = Empty(hinge, "Tip", new Vector3(0, 0.02f, 0.52f), Quaternion.Euler(38f, 0, 0), GameLayers.Default).transform;
                CBox(tip, "Hook", new Vector3(0, 0, 0.2f), new Vector3(0.1f, 0.09f, 0.42f), steel, 0.02f);
                CBox(tip, "Pad", new Vector3(0, -0.045f, 0.3f), new Vector3(0.1f, 0.03f, 0.2f), M("Rubber"), 0.008f);
                fingers.Add(hinge);
            }

            // Hydraulic rams: a barrel from one anchor and a chrome rod from the other, re-aimed every frame.
            SalvageClawStation.Piston Ram(Transform a, Transform b, float length, float radius, string name)
            {
                var barrel = Empty(arm, name + " Barrel", Vector3.zero, null, GameLayers.Default).transform;
                Part(barrel, "Cylinder", Tube(radius, length, 10), new Vector3(0, 0, length * 0.5f), dark, new Vector3(90f, 0, 0));
                var rod = Empty(arm, name + " Rod", Vector3.zero, null, GameLayers.Default).transform;
                Part(rod, "Rod", Tube(radius * 0.55f, length, 8), new Vector3(0, 0, length * 0.5f), chrome, new Vector3(90f, 0, 0));
                return new SalvageClawStation.Piston { barrel = barrel, rod = rod, anchorA = a, anchorB = b };
            }
            var pistons = new[]
            {
                Ram(ramA1, ramB1, 1.45f, 0.085f, "Shoulder Ram L"),
                Ram(ramA2, ramB2, 1.45f, 0.085f, "Shoulder Ram R"),
                Ram(ramC1, ramC2, 1.25f, 0.075f, "Elbow Ram"),
            };

            // A pair of hull floodlights (emissive only: the work light is the one real light).
            foreach (float s in new[] { -1f, 1f })
                CBox(arm, "Hull Flood", new Vector3(axis.x + s * 0.55f, 1.15f, 0.24f), new Vector3(0.22f, 0.12f, 0.08f), M("GlowWarm"), 0.01f);

            // --- the intake hatch, to the operator's right, its mouth turned toward the window
            Vector3 mouthNormal = new Vector3(-0.94f, 0f, -0.34f).normalized;
            var intake = Empty(t, "Intake Hatch", new Vector3(2.2f, 0.7f, 2.5f), Quaternion.LookRotation(mouthNormal, Vector3.up), GameLayers.Default).transform;
            CBox(intake, "Body", new Vector3(0, 0.1f, -0.2f), new Vector3(1.9f, 2.0f, 1.4f), steel, 0.05f);
            CBox(intake, "Mouth Back", new Vector3(0, 0, 0.52f), new Vector3(1.2f, 1.0f, 0.04f), M("Vent"), 0.005f);
            CBox(intake, "Frame Top", new Vector3(0, 0.6f, 0.64f), new Vector3(1.6f, 0.2f, 0.12f), hazard, 0.02f);
            CBox(intake, "Frame Bottom", new Vector3(0, -0.6f, 0.64f), new Vector3(1.6f, 0.2f, 0.12f), hazard, 0.02f);
            foreach (float s in new[] { -1f, 1f })
                CBox(intake, "Frame Side", new Vector3(s * 0.7f, 0, 0.64f), new Vector3(0.2f, 1.0f, 0.12f), hazard, 0.02f);
            var doorL = Empty(intake, "Door Left", new Vector3(-0.29f, 0, 0.66f), null, GameLayers.Default).transform;
            CBox(doorL, "Leaf", Vector3.zero, new Vector3(0.58f, 1.0f, 0.06f), paint, 0.01f);
            var doorR = Empty(intake, "Door Right", new Vector3(0.29f, 0, 0.66f), null, GameLayers.Default).transform;
            CBox(doorR, "Leaf", Vector3.zero, new Vector3(0.58f, 1.0f, 0.06f), paint, 0.01f);
            var lamp = CBox(intake, "Intake Lamp", new Vector3(0, -0.8f, 0.54f), new Vector3(0.5f, 0.08f, 0.04f), M("GlowTeal"), 0.01f);
            CBox(intake, "Lamp Off", new Vector3(0, -0.8f, 0.515f), new Vector3(0.48f, 0.07f, 0.02f), dark, 0.005f);
            var intakeLabel = Text(intake, "Label", "INTAKE", new Vector3(0, 0.88f, 0.51f), Quaternion.LookRotation(Vector3.back), 1.5f, new Vector2(1.6f, 0.25f), new Color(1f, 0.8f, 0.3f));
            intakeLabel.fontStyle = FontStyles.Bold;
            var mouth = Empty(intake, "Mouth", new Vector3(0, 0, 1.25f), null, GameLayers.Default).transform;
            var inside = Empty(intake, "Inside", new Vector3(0, 0, -0.2f), null, GameLayers.Default).transform;
            CBox(t, "Intake Duct", new Vector3(2.5f, 0.7f, 0.9f), new Vector3(0.8f, 0.8f, 1.6f), dark, 0.04f);

            // --- cargo templates (inactive; the station clones them)
            var templates = Empty(t, "Cargo Templates", Vector3.zero, null, GameLayers.Default);
            var cargoRoot = Empty(t, "Cargo (runtime)", Vector3.zero, null, GameLayers.Default).transform;
            SalvageClawCargo Template(ClawCargoKind kind, string name)
            {
                var go = Empty(templates.transform, name, Vector3.zero, null, GameLayers.Default);
                var c = go.AddComponent<SalvageClawCargo>();
                c.kind = kind;
                return c;
            }
            var crateT = Template(ClawCargoKind.Crate, "Cargo Crate");
            CBox(crateT.transform, "Body", Vector3.zero, new Vector3(0.9f, 0.7f, 0.7f), crate, 0.04f);
            foreach (float s in new[] { -1f, 1f })
                CBox(crateT.transform, "Band", new Vector3(s * 0.3f, 0, 0), new Vector3(0.08f, 0.74f, 0.74f), M("Trim"), 0.015f);
            var scrapT = Template(ClawCargoKind.Scrap, "Cargo Scrap");
            CBox(scrapT.transform, "Plate A", new Vector3(0, 0, 0), new Vector3(0.9f, 0.06f, 0.6f), scrap, 0.01f, new Vector3(12f, 20f, 8f));
            CBox(scrapT.transform, "Plate B", new Vector3(0.18f, 0.16f, 0.05f), new Vector3(0.6f, 0.05f, 0.45f), scrap, 0.01f, new Vector3(-30f, -15f, 40f));
            CBox(scrapT.transform, "Girder", new Vector3(-0.1f, 0.05f, -0.1f), new Vector3(0.12f, 0.12f, 1.0f), steel, 0.02f, new Vector3(0f, 50f, 15f));
            Part(scrapT.transform, "Pipe", Tube(0.06f, 0.8f, 8), new Vector3(0.15f, -0.08f, 0.12f), M("Pipe"), new Vector3(70f, 30f, 0f));
            var longT = Template(ClawCargoKind.LongContainer, "Cargo Long Container");
            CBox(longT.transform, "Body", Vector3.zero, new Vector3(2.4f, 0.8f, 0.8f), longMat, 0.04f);
            foreach (float s in new[] { -1f, 1f })
                CBox(longT.transform, "End Frame", new Vector3(s * 1.17f, 0, 0), new Vector3(0.08f, 0.84f, 0.84f), M("Trim"), 0.015f);
            CBox(longT.transform, "Grip Band", Vector3.zero, new Vector3(0.5f, 0.84f, 0.84f), hazard, 0.015f);
            var boxT = Template(ClawCargoKind.Strongbox, "Cargo Strongbox");
            CBox(boxT.transform, "Body", Vector3.zero, new Vector3(0.62f, 0.46f, 0.46f), strong, 0.04f);
            foreach (float s in new[] { -1f, 1f })
                CBox(boxT.transform, "Gold Stripe", new Vector3(s * 0.2f, 0, 0), new Vector3(0.07f, 0.48f, 0.48f), gold, 0.01f);
            CBox(boxT.transform, "Beacon", new Vector3(0, 0.25f, 0), new Vector3(0.1f, 0.05f, 0.1f), M("GlowWarm"), 0.01f);
            templates.SetActive(false);

            // --- space behind it all: a box of stars (faces inward), bright enough to show through the fog
            var backdrop = Empty(t, "Space Backdrop", Vector3.zero, null, GameLayers.Default).transform;
            Prim(backdrop, "Far", PrimitiveType.Quad, new Vector3(0, 3f, 32f), new Vector3(96f, 66f, 1f), sky);
            Prim(backdrop, "Left", PrimitiveType.Quad, new Vector3(-48f, 3f, 16f), new Vector3(32f, 66f, 1f), sky, false, new Vector3(0, -90f, 0));
            Prim(backdrop, "Right", PrimitiveType.Quad, new Vector3(48f, 3f, 16f), new Vector3(32f, 66f, 1f), sky, false, new Vector3(0, 90f, 0));
            Prim(backdrop, "Top", PrimitiveType.Quad, new Vector3(0, 36f, 16f), new Vector3(96f, 32f, 1f), sky, false, new Vector3(-90f, 0, 0));
            Prim(backdrop, "Bottom", PrimitiveType.Quad, new Vector3(0, -30f, 16f), new Vector3(96f, 32f, 1f), sky, false, new Vector3(90f, 0, 0));

            // --- wire the station
            var station = root.AddComponent<SalvageClawStation>();
            station.director = director;
            station.turret = turret;
            station.shoulder = shoulder;
            station.elbow = elbow;
            station.wrist = wrist;
            station.fingers = fingers.ToArray();
            station.pistons = pistons;
            station.workLight = work;
            station.intakeMouth = mouth;
            station.intakeInside = inside;
            station.intakeDoorLeft = doorL;
            station.intakeDoorRight = doorR;
            station.intakeDoorTravel = 0.58f;
            station.intakeLamp = lamp.GetComponent<Renderer>();
            station.stick = stick;
            station.extendButton = extend;
            station.retractButton = retract;
            station.clawButton = clawButton;
            station.monitor = monitor;
            station.standPoint = stand;
            station.servo = servo;
            station.cargoTemplates = new[] { crateT, scrapT, longT, boxT };
            station.cargoRoot = cargoRoot;
            return station;
        }

        /// <summary>
        /// Opens the observation window in the supply bay's south wall: the wall's one segment is replaced by four
        /// around the opening, and an invisible pane (environment layer) keeps everyone inside.
        /// </summary>
        static void CutClawWindow()
        {
            var wall = s_Level.Find("Supply Bay S");
            if (wall == null)
            {
                Debug.LogError("[Builder] Salvage claw: the supply bay's south wall was not found.");
                return;
            }
            var mat = M("WallTug");
            for (int i = wall.childCount - 1; i >= 0; i--)
            {
                var child = wall.GetChild(i);
                if (child.name == "Segment") Object.DestroyImmediate(child.gameObject);
            }
            const float CH = 2.5f, x0 = -6f - (T * 0.5f - 0.01f), x1 = -2f + (T * 0.5f - 0.01f);
            float z = ClawStationOrigin.z;
            float wx0 = ClawStationOrigin.x - ClawWindowHalfWidth, wx1 = ClawStationOrigin.x + ClawWindowHalfWidth;
            void Seg(float a, float b, float y0, float y1) =>
                Box(wall, "Segment", new Vector3((a + b) * 0.5f, (y0 + y1) * 0.5f, z), new Vector3(b - a, y1 - y0, T), mat);
            Seg(x0, wx0, 0f, CH);
            Seg(wx1, x1, 0f, CH);
            Seg(wx0, wx1, 0f, ClawWindowSill);
            Seg(wx0, wx1, ClawWindowHead, CH);
            var pane = Empty(wall, "Claw Window Pane", new Vector3(ClawStationOrigin.x, (ClawWindowSill + ClawWindowHead) * 0.5f, z), null, GameLayers.Environment);
            pane.AddComponent<BoxCollider>().size = new Vector3(ClawWindowHalfWidth * 2f, ClawWindowHead - ClawWindowSill, T);
            pane.isStatic = true;
        }
    }
}

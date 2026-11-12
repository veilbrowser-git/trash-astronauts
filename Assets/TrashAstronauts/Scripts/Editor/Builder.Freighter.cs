using System.Collections.Generic;
using System.Linq;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// Wreck A: the hauler MARGIN CALL. One long spine corridor from the airlock to the reactor, the cargo bay
    /// off its west side, maintenance off its east side and a service duct from maintenance to the reactor.
    ///
    /// The layout is written in the coordinates of the game's first, small freighter and built at
    /// FreighterScale (2.25) of it about the airlock join: 1.5x the stretched freighter it replaces. It used
    /// to be built small and stretched by transform, which widened its doors and thickened its rails; now
    /// every room, wall and route is built at full size and every fixture at its real size.
    ///
    ///   Corridor     x[-2.9,2.9]    z[7.6,39.1]   (was x[-1.3,1.3] z[7.6,21.6] in the first layout)
    ///   Cargo bay    x[-23.2,-2.9]  z[12.1,32.4]
    ///   Maintenance  x[2.9,18.7]    z[9.9,25.6]
    ///   Reactor      x[-10.1,10.1]  z[39.1,57.1]
    ///   Service duct maintenance north wall -> reactor east wall (1.8 m wide, 2.2 m high)
    /// </summary>
    public static partial class Builder
    {
        static WreckMap BuildFreighter(Light[] sharedLights, Renderer[] sharedGlow)
        {
            var map = BeginMap("Wreck A - Freighter", "freighter", "Hauler MARGIN CALL", "One long spine corridor with rooms off each side.");
            s_Plan = FreighterScale;
            Transform root = map.transform;
            var wall = M("Wall");
            const float LY = WH - 0.4f;
            const SalvageCategoryMask S = SalvageCategoryMask.SmallScrap;
            const SalvageCategoryMask E = SalvageCategoryMask.Electronics;
            const SalvageCategoryMask N = SalvageCategoryMask.Novelty;

            // Walls of the first layout, and the fixtures set against them.
            float cW = X(-1.3f), cE = X(1.3f), cN = Z(21.6f);                    // corridor
            float gW = X(-10.3f), gS = Z(9.6f), gN = Z(18.6f);                   // cargo bay (east wall = corridor west wall)
            float mE = X(8.3f), mS = Z(8.6f), mN = Z(15.6f);                     // maintenance (west wall = corridor east wall)
            float rW = X(-4.5f), rE = X(4.5f), rS = cN, rN = Z(29.6f);          // reactor
            float hz = Z(25.9f);                                                 // reactor housing
            float ductX = X(6.8f), ductZ = Z(24f), dw = 0.9f;                    // service duct centre lines and half width
            Vector3 lockerAt = new Vector3(gW + 1.0f, 0, gN - 1.0f);             // secure cargo locker
            float maintLockersX = X(6.9f), cabinetX = X(3.6f), benchX = X(5.2f), galleyX = X(2.0f), shelfX = cW - 0.9f;
            float reactorConsoleX = X(-3.6f), reactorConsoleZ = rN - 1.2f;

            // --- structure
            JoinWall("A Join", cW, cE, WH, wall);
            Room("A Corridor", cW, cE, JoinZ, cN, WH, "WE", wall, new Opening('S', 0, 1.4f, 2.1f), new Opening('N', 0, 1.6f, 2.1f),
                new Opening('W', Z(12f), 1.8f, 2.2f), new Opening('E', Z(10.5f), 1.6f, 2.2f));
            Room("A Cargo Bay", gW, cW, gS, gN, WH, "SNW", wall, new Opening('E', Z(12f), 1.8f, 2.2f),
                RailGap('W', lockerAt.z, 2.0f), RailGap('N', lockerAt.x, 2.0f), RailGap('N', shelfX, 1.6f));
            Room("A Maintenance", cE, mE, mS, mN, WH, "SNE", wall, new Opening('W', Z(10.5f), 1.6f, 2.2f), new Opening('N', ductX, 1.0f, 2.0f),
                RailGap('S', maintLockersX, 2.3f), RailGap('S', cabinetX, 1.5f), RailGap('N', benchX, 2.5f), RailGap('N', galleyX, 1.3f), RailGap('E', Z(12.05f), 1.0f));
            Room("A Reactor", rW, rE, rS, rN, WH, "SNWE", wall, new Opening('S', 0, 1.6f, 2.1f), new Opening('E', ductZ, 1.0f, 2.0f));
            var reactorDoor = BuildDoor("Reactor Door", "door_reactor", P(0, 0, 21.6f), 1.6f, 2.1f, false, false, true);

            // Service duct (the loop / shortcut, sealed by a cuttable cover): still a crawlway.
            FloorAndCeiling("A Duct A Slabs", ductX - dw, ductX + dw, mN, ductZ + dw, 2.2f, M("Floor"));
            FloorAndCeiling("A Duct B Slabs", rE, ductX - dw, ductZ - dw, ductZ + dw, 2.2f, M("Floor"));
            Wall("A Duct A W", new Vector2(ductX - dw, mN), new Vector2(ductX - dw, ductZ - dw), 2.2f, M("Trim"));
            Wall("A Duct A E", new Vector2(ductX + dw, mN), new Vector2(ductX + dw, ductZ + dw), 2.2f, M("Trim"));
            Wall("A Duct N", new Vector2(rE, ductZ + dw), new Vector2(ductX + dw, ductZ + dw), 2.2f, M("Trim"));
            Wall("A Duct B S", new Vector2(rE, ductZ - dw), new Vector2(ductX - dw, ductZ - dw), 2.2f, M("Trim"));
            TeleportArea("A Duct Entrance", ductX - 0.3f, ductX + 0.3f, mN - 0.5f, mN + 0.6f);
            TeleportArea("A Duct A", ductX - dw + 0.3f, ductX + dw - 0.3f, mN + 0.3f, ductZ + dw - 0.3f);
            TeleportArea("A Duct B", rE - 0.3f, ductX + dw - 0.3f, ductZ - dw + 0.3f, ductZ + dw - 0.3f);
            RailLine(new Vector2(ductX + dw, mN), new Vector2(ductX + dw, ductZ + dw), Vector2.left);
            RailLine(new Vector2(ductX - dw, mN), new Vector2(ductX - dw, ductZ - dw), Vector2.right);
            RailLine(new Vector2(rE, ductZ + dw), new Vector2(ductX + dw, ductZ + dw), Vector2.down);
            Exclusion(root, "No Drone: Duct A", new Vector3(ductX, 1.2f, (mN + 0.05f + ductZ + dw - 0.05f) * 0.5f), new Vector3(dw * 2f + 0.1f, 2.6f, ductZ + dw - mN - 0.1f));
            Exclusion(root, "No Drone: Duct B", new Vector3((rE - 0.05f + ductX - dw + 0.05f) * 0.5f, 1.2f, ductZ), new Vector3(ductX - dw - rE + 0.1f, 2.6f, dw * 2f + 0.1f));

            // --- panels and loot
            var reactorPanel = SealedCompartment("Reactor Housing", "panel_reactor", "REACTOR ACCESS", new Vector3(0, 0, hz), new Vector3(1.8f, 2.4f, 1.8f), M("ReactorShell"), out Vector3 reactorCavity);
            var cagePanel = SealedCompartment("Secure Cargo Locker", "panel_cage", "SECURE CARGO", lockerAt, new Vector3(1.7f, 2.2f, 1.7f), M("ContainerBlue"), out Vector3 cageCavity);
            MakeCuttable("Service Duct Cover", "panel_duct", "SERVICE DUCT", new Vector3(ductX, 1.02f, mN - T * 0.5f - 0.03f), Vector3.back, new Vector2(1.12f, 2.04f), s_HatchCuts);
            Spawn("reactor_housing", reactorCavity, SalvageCategoryMask.ReactorComponent, reactorPanel);
            Spawn("cargo_locker", cageCavity, E | N, cagePanel);
            RackMount("mount_rack", new Vector3(mE, 0, Z(12.05f)), Vector3.left, "maint_rack");

            Spawn("airlock_corner", new Vector3(1.3f, 1.0f, 6.8f), S);
            Spawn("corridor_south", P(-0.7f, 1.2f, 14.6f), S);
            Spawn("corridor_north", P(0.7f, 1.3f, 18.6f), S | E);
            Spawn("cargo_entry", P(-3.4f, 1.0f, 10.6f), S);
            Spawn("cargo_mid", P(-7.3f, 1.3f, 13.2f), S | E);
            Spawn("cargo_far", P(-9.4f, 1.1f, 10.6f), S | N);
            Spawn("cargo_north", P(-5.2f, 1.2f, 17.6f), S);
            Spawn("cargo_shelf", new Vector3(shelfX, 1.25f, gN - 1.0f), E | N);
            Spawn("maint_floor", P(3.0f, 1.1f, 9.6f), S);
            Spawn("maint_bench", new Vector3(benchX, 1.2f, mN - 1.3f), E | S);
            Spawn("maint_galley", new Vector3(X(2.3f), 1.3f, mN - 0.9f), N | S);
            Spawn("maint_corner", new Vector3(mE - 0.8f, 1.1f, mS + 0.8f), S | E);   // the far corner of maintenance
            Spawn("duct_bend", new Vector3(ductX, 1.0f, Z(22.6f)), S | E);
            Spawn("reactor_west", P(-3.6f, 1.1f, 22.8f), S);
            Spawn("reactor_console", new Vector3(reactorConsoleX + 0.2f, 1.25f, reactorConsoleZ - 0.8f), E | N);
            Spawn("reactor_back", new Vector3(X(1.6f), 1.2f, rN - 0.8f), S | E);
            Spawn("reactor_east", P(3.7f, 1.3f, 22.6f), S | N);

            // --- corridor: signage, ribs, pipes, cables
            Sign("CARGO BAY", new Vector3(cW + T * 0.5f + 0.02f, 2.45f, Z(12f)), Vector3.right, 1.3f);
            Sign("MAINTENANCE", new Vector3(cE - T * 0.5f - 0.02f, 2.45f, Z(10.5f)), Vector3.left, 1.3f);
            Sign("REACTOR", new Vector3(0, 2.45f, cN - T * 0.5f - 0.02f), Vector3.back, 1.5f, new Color(0.4f, 1f, 0.9f));
            Sign("<< EXIT TO TUG", new Vector3(0, 2.45f, JoinZ + T * 0.5f + 0.02f), Vector3.forward, 1.2f, new Color(0.5f, 1f, 0.6f));
            var westItems = new List<float> { Z(12f), Z(15.2f), Z(19.4f) };   // door, vent, poster
            var eastItems = new List<float> { Z(10.5f), Z(18.6f), Z(13.6f), Z(17.0f) };
            for (float z = JoinZ + 1.0f; z < cN - 0.5f; z += 2.4f)
            {
                if (westItems.All(w => Mathf.Abs(z - w) > 1.2f)) Box(s_Props, "Rib W", new Vector3(cW + T * 0.5f + 0.04f, WH * 0.5f, z), new Vector3(0.08f, WH, 0.16f), M("Trim"), GameLayers.Default, false);
                if (eastItems.All(w => Mathf.Abs(z - w) > 1.2f)) Box(s_Props, "Rib E", new Vector3(cE - T * 0.5f - 0.04f, WH * 0.5f, z), new Vector3(0.08f, WH, 0.16f), M("Trim"), GameLayers.Default, false);
            }
            Pipe("Corridor Pipe A", new Vector3(X(-0.85f), WH - 0.2f, JoinZ + 0.2f), new Vector3(X(-0.85f), WH - 0.2f, cN - 0.2f), 0.06f, "Pipe");
            Pipe("Corridor Pipe B", new Vector3(X(-0.66f), WH - 0.15f, JoinZ + 0.2f), new Vector3(X(-0.66f), WH - 0.15f, cN - 0.2f), 0.035f, "Steel");
            Pipe("Corridor Cable Run", new Vector3(X(0.9f), WH - 0.13f, JoinZ + 0.2f), new Vector3(X(0.9f), WH - 0.13f, cN - 0.2f), 0.028f, "Cable");
            Pipe("Hanging Cable A", new Vector3(X(0.4f), WH, Z(13.4f)), new Vector3(X(0.4f) + 0.35f, WH - 0.65f, Z(13.4f) + 0.3f), 0.012f, "Cable");
            Pipe("Hanging Cable B", new Vector3(X(-0.3f), WH, Z(17.2f)), new Vector3(X(-0.3f) - 0.6f, WH - 0.8f, Z(17.2f) - 0.2f), 0.012f, "Cable");
            Walkway(-0.9f, 0.9f, JoinZ + 0.3f, cN - 0.3f);
            Vent(new Vector3(cW + T * 0.5f + 0.01f, 2.25f, Z(15.2f)), Vector3.right);
            Vent(new Vector3(cE - T * 0.5f - 0.01f, 2.25f, Z(18.6f)), Vector3.left);
            Poster("DAYS SINCE LAST\nINCIDENT:  0", new Vector3(cE - T * 0.5f - 0.012f, 1.55f, Z(13.6f)), Vector3.left);
            Poster("OXYGEN IS A\nPRIVILEGE", new Vector3(cW + T * 0.5f + 0.012f, 1.55f, Z(19.4f)), Vector3.right);
            Poster("LOG 41\nSomething is in the vents.\nMaintenance says rats.\nThere are no rats on a ship.", new Vector3(cE - T * 0.5f - 0.012f, 1.5f, Z(17.0f)), Vector3.left, 0.95f, 0.6f, 0.36f);
            Dust("Dust Corridor", P(0, 1.6f, 14.6f), new Vector3(5f, 2.6f, 30f), 70);
            Tripwire("a_corridor_mid", new Vector3(cW + 0.1f, 1.3f, Z(13.5f)), new Vector3(cE - 0.1f, 1.1f, Z(13.5f)));
            Tripwire("a_corridor_far", new Vector3(cW + 0.1f, 0.7f, Z(18.2f)), new Vector3(cE - 0.1f, 1.8f, Z(18.2f)));
            Stencil("CARGO", new Vector3(cW - 1.7f, 0.016f, Z(12f) + 0.7f), 90f);
            Stencil("MAINT", new Vector3(cE + 1.7f, 0.016f, Z(10.5f)), -90f);

            // --- cargo bay: containers make cover to hide behind
            Container(P(-4.6f, 0, 11.2f), new Vector3(1.3f, 2.1f, 2.2f));
            Container(P(-4.6f, 0, 15.3f), new Vector3(1.3f, 2.1f, 2.4f), "ContainerBlue");
            Container(P(-8.2f, 0, 12.2f), new Vector3(2.2f, 1.4f, 1.3f), "ContainerBlue");
            Container(P(-8.2f, 0, 14.6f), new Vector3(2.2f, 2.1f, 1.3f));
            Container(new Vector3(cW - 1.1f, 0, Z(15.8f)), new Vector3(1.0f, 1.0f, 1.0f));
            Container(new Vector3(X(-6.3f), 0, gN - 0.6f), new Vector3(1.4f, 0.9f, 0.9f), "ContainerBlue");
            Container(new Vector3(gW + 0.65f, 0, gS + 1.4f), new Vector3(0.9f, 0.9f, 0.9f));
            Container(new Vector3(X(-8.6f), 0, gS + 0.75f), new Vector3(1.6f, 1.2f, 1.0f));
            Container(new Vector3(gW + 0.8f, 0, Z(14.6f)), new Vector3(1.0f, 1.3f, 1.6f), "ContainerBlue");
            Box(s_Props, "Cargo Shelf", new Vector3(shelfX, 1.05f, gN - 0.4f), new Vector3(1.4f, 0.05f, 0.5f), M("Trim"));
            Walkway(gW + 0.7f, cW - 0.3f, Z(12f) - 0.1f, Z(12f) + 1.5f);
            Box(s_Props, "Cargo Hazard Line", new Vector3(X(-5.8f), 0.006f, Z(12f) + 2.2f), new Vector3(cW - gW - 1.2f, 0.01f, 0.14f), M("Hazard"), GameLayers.Default, false);
            Rail(new Vector3(X(-8.2f) + 1.25f, RailY, Z(14.6f) - 0.6f), new Vector3(X(-8.2f) + 1.25f, RailY, Z(14.6f) + 0.6f)); // on the side of a container
            Barrel(new Vector3(gW + 0.8f, 0, Z(16.6f)));
            Barrel(new Vector3(gW + 1.4f, 0, Z(16.6f) + 0.35f), "BarrelBlue");
            Barrel(new Vector3(cW - 1.3f, 0, gS + 0.8f));
            Pipe("Cargo Pipe", new Vector3(gW + 0.3f, WH - 0.25f, gS + 0.3f), new Vector3(gW + 0.3f, WH - 0.25f, gN - 0.3f), 0.07f, "Pipe");
            Pipe("Wall Conduit Cargo", new Vector3(gW + T * 0.5f + 0.06f, 2.3f, Z(14.2f)), new Vector3(gW + T * 0.5f + 0.06f, 2.3f, gN - 0.3f), 0.03f, "Steel");
            Pipe("Hanging Cable C", new Vector3(X(-7.2f), WH, Z(16.6f)), new Vector3(X(-7.2f) - 0.3f, WH - 0.9f, Z(16.6f) - 0.4f), 0.014f, "Cable");
            Pipe("Hanging Cable D", new Vector3(X(-3.0f), WH, Z(11.2f)), new Vector3(X(-3.0f) + 0.3f, WH - 0.75f, Z(11.2f) + 0.4f), 0.012f, "Cable");
            Vent(new Vector3(X(-6f), 2.25f, gS + T * 0.5f + 0.01f), Vector3.forward, 1.0f);
            Vent(new Vector3(gW + T * 0.5f + 0.01f, 0.5f, Z(16.0f)), Vector3.right, 0.6f, 0.4f);
            Window(new Vector3(gW + T * 0.5f + 0.015f, 1.65f, Z(12.6f)), Vector3.right, 2.6f, 1.0f);
            Window(new Vector3(gW + T * 0.5f + 0.015f, 1.65f, Z(15.2f)), Vector3.right, 2.0f, 1.0f);
            Poster("LOST? FOLLOW THE\nYELLOW RAILS", new Vector3(X(-5.8f), 1.6f, gN - T * 0.5f - 0.012f), Vector3.back, 1.0f);
            Poster("LOG 44\nOkafor went to check the reactor.\nThat was two shifts ago.", new Vector3(gW + T * 0.5f + 0.012f, 1.5f, Z(13.9f)), Vector3.right, 0.95f, 0.55f, 0.36f);
            Web(new Vector3(gW + 0.1f, WH - 0.05f, gN - 0.1f), Vector3.right, Vector3.back, Vector3.down, 1.9f);
            Dust("Dust Cargo", P(-5.8f, 1.6f, 14.1f), new Vector3(18f, 2.6f, 18f), 90);

            // --- maintenance: bench, cabinet, galley, pillar, lockers
            Box(s_Props, "Workbench", new Vector3(benchX, 0.45f, mN - 0.7f), new Vector3(2.2f, 0.9f, 0.8f), M("Trim"));
            ConsoleDetail(new Vector3(benchX, 0.9f, mN - 0.7f), 2.0f, Vector3.back);
            Box(s_Props, "Tool Cabinet", new Vector3(cabinetX, 1.0f, mS + 0.35f), new Vector3(1.2f, 2.0f, 0.5f), M("ContainerBlue"));
            Box(s_Props, "Galley Counter", new Vector3(galleyX, 0.45f, mN - 0.7f), new Vector3(1.0f, 0.9f, 0.8f), M("WallTug"));
            Box(s_Props, "Pillar", P(4.6f, WH * 0.5f, 11.8f), new Vector3(0.5f, WH, 0.5f), M("Trim"));
            LockerRow(new Vector3(maintLockersX, 0, mS + T * 0.5f + 0.24f), Vector3.forward, 4);
            Barrel(new Vector3(mE - 0.6f, 0, mN - 0.7f), "BarrelBlue");
            Container(new Vector3(mE - 0.8f, 0, Z(10.6f)), new Vector3(1.0f, 1.1f, 1.4f), "ContainerBlue");
            Sign("GALLEY\nTOAST IS A PRIVILEGE", new Vector3(galleyX, 1.9f, mN - T * 0.5f - 0.02f), Vector3.back, 0.8f);
            Sign("SERVICE DUCT\nTO REACTOR", new Vector3(ductX - 1.2f, 1.9f, mN - T * 0.5f - 0.02f), Vector3.back, 1.0f);
            Poster("REPORT STRANGE\nNOISES. QUIETLY.", new Vector3(X(4.1f), 1.6f, mN - T * 0.5f - 0.012f), Vector3.back, 0.9f);
            Pipe("Maintenance Pipe", new Vector3(cE + 0.3f, WH - 0.22f, mS + 0.3f), new Vector3(mE - 0.3f, WH - 0.22f, mS + 0.3f), 0.05f, "Pipe");
            Pipe("Hanging Cable E", new Vector3(X(5.8f), WH, Z(12.8f)), new Vector3(X(5.8f) + 0.4f, WH - 0.7f, Z(12.8f) + 0.3f), 0.012f, "Cable");
            Vent(new Vector3(mE - T * 0.5f - 0.01f, 2.25f, Z(10.2f)), Vector3.left);
            Window(new Vector3(mE - T * 0.5f - 0.015f, 1.7f, Z(13.9f)), Vector3.left, 1.4f, 0.8f);
            Walkway(cE + 0.3f, X(6.6f), Z(10.5f) - 0.8f, Z(10.5f) + 0.8f);
            EggSacs(new Vector3(mE - T * 0.5f, 1.8f, Z(10.4f) + 1.4f), Vector3.left, 6, 63);
            Dust("Dust Maintenance", P(4.8f, 1.6f, 12.1f), new Vector3(14f, 2.6f, 14f), 55);

            // --- reactor: the housing, coolant tanks (hiding spots), a console, the creature's dock
            for (int i = 0; i < 3; i++)
                Box(s_Props, "Containment Band", new Vector3(0, 0.45f + i * 0.85f, hz), new Vector3(1.9f, 0.09f, 1.9f), M("Steel"), GameLayers.Default, false);
            foreach (float sx in new[] { -1f, 1f })
                Box(s_Props, "Reactor Beacon", new Vector3(sx * 0.78f, 2.05f, hz - 0.93f), new Vector3(0.1f, 0.1f, 0.05f), M("GlowRed"), GameLayers.Default, false);
            Part(s_Props, "Reactor Column", Tube(0.32f, WH - 2.4f, 16), new Vector3(0, (WH + 2.4f) * 0.5f, hz), M("ReactorGlow")).isStatic = true;
            for (int i = 0; i < 2; i++)
                Part(s_Props, "Reactor Column Ring", Torus(0.36f, 0.04f, 18, 6), new Vector3(0, 2.48f + i * 0.2f, hz), M("Steel")).isStatic = true;
            Box(s_Props, "Reactor Floor Ring", new Vector3(0, 0.006f, hz - 1.3f), new Vector3(2.6f, 0.01f, 0.14f), M("Hazard"), GameLayers.Default, false);
            Sign("FUSION REGULATOR\nCUT PANEL TO ACCESS", new Vector3(0, 2.25f, hz - 0.92f), Vector3.back, 0.8f, new Color(0.4f, 1f, 0.9f));
            Rail(new Vector3(-1.25f, RailY, hz - 1f), new Vector3(-1.25f, RailY, hz + 1f)); // around the housing
            Rail(new Vector3(1.25f, RailY, hz - 1f), new Vector3(1.25f, RailY, hz + 1f));
            Vector3 tankW = P(-3.3f, 1.0f, 25.2f), tankE = P(3.3f, 1.0f, 26.4f);
            Box(s_Props, "Coolant Tank W", tankW, new Vector3(1.0f, 2.0f, 1.0f), M("ReactorShell"));
            Box(s_Props, "Coolant Tank E", tankE, new Vector3(1.0f, 2.0f, 1.0f), M("ReactorShell"));
            Pipe("Reactor Coolant W", new Vector3(tankW.x, 2.0f, tankW.z), new Vector3(tankW.x, WH, tankW.z), 0.09f, "Steel");
            Pipe("Reactor Coolant E", new Vector3(tankE.x, 2.0f, tankE.z), new Vector3(tankE.x, WH, tankE.z), 0.09f, "Steel");
            Pipe("Reactor Feed W", new Vector3(-0.9f, 2.25f, hz), new Vector3(tankW.x, 2.25f, tankW.z + 0.2f), 0.05f, "Pipe");
            Pipe("Reactor Feed E", new Vector3(0.9f, 2.25f, hz), new Vector3(tankE.x, 2.25f, tankE.z - 0.2f), 0.05f, "Pipe");
            Box(s_Props, "Reactor Console", new Vector3(reactorConsoleX, 0.5f, reactorConsoleZ), new Vector3(1.2f, 1.0f, 0.7f), M("Trim"));
            ConsoleDetail(new Vector3(reactorConsoleX, 1.0f, reactorConsoleZ), 1.1f, Vector3.back);
            Barrel(new Vector3(rW + 0.6f, 0, rS + 0.6f));
            Barrel(new Vector3(X(-1.0f), 0, rN - 0.5f), "BarrelBlue");
            Container(new Vector3(rE - 0.8f, 0, rN - 0.9f), new Vector3(1.0f, 1.2f, 1.2f));
            Window(new Vector3(X(-1.6f), 1.7f, rN - T * 0.5f - 0.015f), Vector3.back, 2.4f, 0.9f);
            Window(new Vector3(X(1.6f), 1.7f, rN - T * 0.5f - 0.015f), Vector3.back, 2.4f, 0.9f);
            Vent(new Vector3(rW + T * 0.5f + 0.01f, 2.25f, Z(23.2f)), Vector3.right);
            Vent(new Vector3(rE - T * 0.5f - 0.01f, 2.25f, Z(28.0f)), Vector3.left);
            Pipe("Wall Conduit Reactor", new Vector3(rW + 0.4f, 2.4f, rN - T * 0.5f - 0.07f), new Vector3(rW + 1.6f, 2.4f, rN - T * 0.5f - 0.07f), 0.035f, "Steel");
            Pipe("Hanging Cable F", new Vector3(X(-2.9f), WH, Z(27.0f)), new Vector3(X(-2.9f) + 0.4f, WH - 0.8f, Z(27.0f) - 0.4f), 0.014f, "Cable");
            Walkway(-0.9f, 0.9f, rS + 0.3f, hz - 1.5f);
            Stencil("REACTOR", new Vector3(0, 0.016f, Z(22.8f)), 0f);
            Sign("SERVICE DUCT\nTO MAINTENANCE", new Vector3(rE - T * 0.5f - 0.02f, 2.35f, ductZ), Vector3.left, 0.9f);
            Vector3 dockAt = new Vector3(X(2.9f), 1.45f, Z(25.4f));
            Sign("MAINTENANCE UNIT DOCK\nDO NOT WAKE IT", new Vector3(rE - T * 0.5f - 0.02f, 2.2f, dockAt.z + 1.6f), Vector3.left, 0.9f, new Color(1f, 0.5f, 0.4f));
            Poster("LOG 47\nKeep your voice down.\nIt comes to the noise.", new Vector3(X(2.0f), 1.5f, rS + T * 0.5f + 0.012f), Vector3.forward, 0.9f, 0.5f, 0.38f);
            Web(new Vector3(rW + 0.1f, WH - 0.05f, rN - 0.1f), Vector3.right, Vector3.back, Vector3.down, 2.0f);
            EggSacs(new Vector3(rW + T * 0.5f, 1.9f, Z(27.6f)), Vector3.right, 7, 61);
            Tripwire("a_reactor_entry", new Vector3(-0.9f, 1.2f, Z(22.4f)), new Vector3(0.9f, 1.4f, Z(22.4f)));
            Dust("Dust Reactor", P(0, 1.6f, 25.6f), new Vector3(18f, 2.6f, 16f), 80);

            // --- lights: more of them for 2.25x the floor, in the same colours; the strips are the corridor's lamps
            for (int i = 0; i < 4; i++)
                RoomLight("Corridor Light " + i, 0, LY, Z(9.0f + i * 3.5f), new Color(0.7f, 0.85f, 1f), 2.6f, 8f);
            RoomLight("Cargo Light A", X(-4.2f), LY, Z(12.5f), new Color(1f, 0.6f, 0.3f), 3.2f, 9f);
            RoomLight("Cargo Light B", X(-8f), LY, Z(16f), new Color(1f, 0.3f, 0.2f), 2.8f, 9f);
            RoomLight("Cargo Light C", X(-8f), LY, Z(11.4f), new Color(1f, 0.55f, 0.3f), 2.8f, 9f);
            RoomLight("Cargo Light D", X(-4.2f), LY, Z(16.6f), new Color(1f, 0.6f, 0.3f), 2.8f, 9f);
            RoomLight("Cargo Light E", X(-6.1f), LY, Z(14.1f), new Color(1f, 0.5f, 0.28f), 2.4f, 8f);
            RoomLight("Maintenance Light", X(4.8f), LY, Z(12f), new Color(1f, 0.85f, 0.6f), 3.6f, 9.5f);
            RoomLight("Maintenance Light SW", X(2.6f), LY, Z(10.0f), new Color(1f, 0.85f, 0.6f), 2.8f, 8f);
            RoomLight("Maintenance Light NE", X(7.0f), LY, Z(14.2f), new Color(1f, 0.8f, 0.55f), 2.8f, 8f);
            RoomLight("Maintenance Light SE", X(7.0f), LY, Z(10.0f), new Color(1f, 0.85f, 0.6f), 2.6f, 8f);
            RoomLight("Reactor Light", 0, LY - 0.1f, Z(23.6f), new Color(0.3f, 1f, 0.85f), 3.6f, 10f);
            RoomLight("Reactor Light Back", 0, LY - 0.1f, Z(28.4f), new Color(0.3f, 0.9f, 1f), 2.8f, 9f);
            RoomLight("Reactor Light W", X(-3f), LY - 0.1f, Z(26.2f), new Color(0.3f, 1f, 0.85f), 2.8f, 9f);
            RoomLight("Reactor Light E", X(3f), LY - 0.1f, Z(26.2f), new Color(0.3f, 0.9f, 1f), 2.8f, 9f);
            RoomLight("Reactor Light SW", X(-3f), LY - 0.1f, Z(22.8f), new Color(0.3f, 1f, 0.85f), 2.4f, 8f);
            RoomLight("Reactor Light SE", X(3f), LY - 0.1f, Z(22.8f), new Color(0.3f, 0.9f, 1f), 2.4f, 8f);
            RoomLight("Duct Light", ductX, 1.9f, Z(18.4f), new Color(1f, 0.5f, 0.2f), 1.8f, 5.5f);
            RoomLight("Duct Light N", ductX, 1.9f, Z(22.2f), new Color(1f, 0.5f, 0.2f), 1.8f, 5.5f);
            RoomLight("Duct Light B", (rE + ductX) * 0.5f, 1.9f, ductZ, new Color(1f, 0.5f, 0.2f), 1.6f, 5f);
            for (int i = 0; i < 8; i++)
                Strip("Corridor Strip " + i, new Vector3(0, WH - 0.03f, JoinZ + 2.0f + i * 3.95f), new Vector3(0.1f, 0.03f, 1.6f), i == 2 || i == 5 ? "GlowRed" : "GlowWhite");
            Strip("Cargo Strip", new Vector3(X(-5.8f), WH - 0.03f, Z(14.1f)), new Vector3(10f, 0.03f, 0.1f), "GlowWarm");
            Strip("Maintenance Strip", new Vector3(X(4.8f), WH - 0.03f, Z(12.1f)), new Vector3(0.1f, 0.03f, 7.9f), "GlowWarm");
            Strip("Reactor Strip W", new Vector3(X(-2.6f), WH - 0.03f, Z(25.6f)), new Vector3(0.1f, 0.03f, 10f), "GlowTeal");
            Strip("Reactor Strip E", new Vector3(X(2.6f), WH - 0.03f, Z(25.6f)), new Vector3(0.1f, 0.03f, 10f), "GlowTeal");

            // --- recovery points and floating junk (throwable distractions, a small bounded count)
            Anchor("Corridor South", 0f, Z(9.4f));
            Anchor("Corridor Mid", 0f, Z(15f));
            Anchor("Corridor North", 0f, Z(20f));
            Anchor("Cargo", X(-3.4f), Z(12f));
            Anchor("Cargo Far", X(-7.2f), Z(16.6f));
            Anchor("Maintenance", X(3.2f), Z(10.6f));
            Anchor("Maintenance Far", X(6.4f), Z(12.4f));
            Anchor("Reactor", 0f, Z(23.2f));
            Anchor("Duct", ductX, Z(19f));
            Junk(s_JunkCanPrefab, P(0.6f, 1.5f, 12.8f));
            Junk(s_JunkCanPrefab, P(-6.2f, 1.6f, 11.0f));
            Junk(s_JunkCanPrefab, P(3.9f, 1.4f, 13.2f));
            Junk(s_JunkCanPrefab, P(-2.6f, 1.5f, 23.4f));
            Junk(s_JunkCratePrefab, P(-3.0f, 1.3f, 14.0f));
            Junk(s_JunkCratePrefab, P(6.6f, 1.2f, 10.8f));
            Junk(s_JunkCratePrefab, P(2.4f, 1.4f, 27.4f));

            // --- the creature's routes (Link() puts extra waypoints on any leg longer than MaxLeg)
            Waypoint("C0", 0, Z(8.7f)); Waypoint("C1", 0, Z(10.5f)); Waypoint("C2", 0, Z(12f)); Waypoint("C3", 0, Z(16f)); Waypoint("C4", 0, Z(20.6f));
            Waypoint("M0", X(2.4f), Z(10.5f)); Waypoint("M1", X(3.4f), Z(12.6f)); Waypoint("M2", X(6.4f), Z(13.4f));
            Waypoint("G0", X(-2.4f), Z(12f)); Waypoint("G1", X(-3.3f), Z(13.2f)); Waypoint("G4", X(-6.5f), Z(13.2f)); Waypoint("G2", X(-6.5f), Z(10.6f)); Waypoint("G3", X(-6.5f), Z(17.2f));
            Waypoint("R0", 0, Z(22.7f)); Waypoint("R1", X(-2.3f), Z(23.6f)); Waypoint("R2", X(-2.3f), Z(28.3f)); Waypoint("R3", X(2.3f), Z(28.3f)); Waypoint("R4", X(2.3f), Z(23.6f));
            Link("C0", "C1", "C2", "C3", "C4", "R0");
            Link("C1", "M0", "M1", "M2");
            Link("C2", "G0", "G1", "G4");
            Link("G4", "G2");
            Link("G4", "G3");
            Link("R0", "R1", "R2", "R3", "R4", "R0");

            var dock = Empty(root, "Creature Dock", dockAt, Quaternion.Euler(0, 200f, 0)).transform;
            var hum = MapHum("A Reactor Hum", new Vector3(0, 1.4f, hz), 0.5f);
            EndMap(map, dock, new[] { reactorDoor }, new[] { hum }, sharedLights, sharedGlow,
                "R3", "R4", "R0", "C4", "C3", "C2", "G0", "G1", "G4", "G3", "G4", "G2", "G4", "G1", "G0", "C2", "C1",
                "M0", "M1", "M2", "M1", "M0", "C1", "C0", "C1", "C2", "C3", "C4", "R0", "R1", "R2");
            return map;
        }
    }
}

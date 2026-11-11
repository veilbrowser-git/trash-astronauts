# Trash Astronauts

A single-player, fully offline VR salvage game for standalone Meta Quest (Quest 3 / 3S, tracked
controllers). You are an underpaid astronaut: board a derelict hauler from your salvage tug, pull
yourself along handrails, cut open panels, haul junk back to the tug's collection locker, and get out
before your oxygen runs out or the ship's malfunctioning janitor drone finds you.

> **Verification status, read this first.** The project compiles, the scene and all assets are
> generated, 66 automated tests pass in the Unity editor on macOS, and a Quest APK builds
> (`Builds/Quest/TrashAstronauts.apk`). It has **not** been installed or run on a headset. Comfort,
> hand/controller alignment, text legibility in the headset, haptic strength and frame rate are all
> **unverified on hardware**. See [What was and was not tested](#what-was-and-was-not-tested).

## Versions

| | Version |
|---|---|
| Unity Editor | **6000.6.8f1** (Unity 6.6, Apple silicon). It was the only editor installed; Unity Hub does not flag it as LTS. |
| Universal RP | 17.6.0 |
| XR Interaction Toolkit | 3.6.1 |
| OpenXR Plugin | 1.18.0 |
| XR Plug-in Management | 4.7.0 |
| Input System | 1.20.1 |
| XR Core Utilities | 2.6.0 (dependency) |
| uGUI / TextMeshPro | 2.6.0 |
| Test Framework | 1.8.0 |

Exact resolved versions are in `Packages/packages-lock.json`.

## Open and play

1. In Unity Hub choose **Add > Add project from disk**, pick this folder, and open it with 6000.6.8f1.
2. Open the starting scene: `Assets/TrashAstronauts/Scenes/TrashAstronauts.unity` (it is the only scene in the build list).
3. Press **Play**.

Everything is already generated and wired. If you ever need to regenerate it, use
**Trash Astronauts > 2. Build Everything**. That tool is idempotent: it rebuilds materials, textures,
audio, prefabs and the scene in place, and it never overwrites the balancing assets in
`Assets/TrashAstronauts/Data`.

## The loop

1. **Tug (safe hub).** Read the mission board for the quota, bonus and oxygen. A short orientation
   checklist is on the wall. No oxygen drains and the drone is powered down until you start.
2. **START CONTRACT** opens the hatch. Your flashlight (chest), scanner and plasma cutter (hips) are already on your suit.
3. **Airlock > corridor > cargo bay / maintenance / reactor.** Loot is placed at random among 20 curated spots each run.
4. **Cut** the marked welds on access panels: the reactor housing (the high-value core), the secure
   cargo locker, the rack clamp (mounted electronics) and the service-duct cover (a shortcut from
   maintenance to the reactor that the drone cannot use).
5. **Carry salvage back** and let go of it in the collection locker. It is counted once and locked away.
6. **Risk another trip or EXTRACT** (press twice to confirm) from the mission board.
7. **Results:** contract complete (salvage + bonus), quota missed (salvage only), or failed (nothing).
8. **Buy the O2 TANK+ upgrade** (600 cr, +3:00 oxygen, permanent) and start the next contract.

## Controls (Quest Touch controllers, either hand)

| Action | Input |
|---|---|
| Grab / hold / drop / throw | **Grip**. Works on salvage, junk, tools and handrails. Release while moving your hand to throw. |
| Handrail traversal | Grip a **yellow rail** and move your hand; you are pulled the opposite way. Movement stops when you let go. |
| Teleport (comfort) | Push a **thumbstick forward** to aim an arc, release to teleport. Works while carrying things. |
| Snap turn | **Thumbstick left / right** (30 / 45 / 60 / 90 degrees, set in the suit menu). |
| Use tool | **Trigger** while holding it: flashlight on/off, scanner ping, cutter flame. |
| Draw / recall a tool | Grip at the **chest** (flashlight) or **hips** (cutter on your main-hand side, scanner on the other). Gripping an empty holster pulls the tool to your hand from anywhere. Dropped tools reel themselves back after a few seconds. |
| Press buttons | Touch with a fingertip, **or** point at a button and pull the trigger (up to 3.5 m). |
| Suit menu | **B / Y** (or the left menu button): height +/-, snap-turn angle, left/right hand, unstuck, recall tools. The same panel is on the tug's rear wall. |

Left-handed play: switch **HAND** in the suit menu. Holsters mirror and the wrist display moves to the other wrist. The setting is saved.

## Testing in the editor without a headset

- **XR Interaction Simulator.** XRI's simulator sample is imported and set to appear automatically in
  Play mode in the editor only. It shows its own key bindings on screen. I enabled it but did not
  drive it by hand, so treat it as untested.
- **Dev hotkeys** (editor and development builds only), handy for exercising the loop quickly:

  | Key | Action | Key | Action |
  |---|---|---|---|
  | F1 | Start contract | F6 | Loud noise at your head |
  | F2 | Extract | F7 | Wake the drone now |
  | F3 | Teleport to next recovery anchor | F8 | +500 credits |
  | F4 | Move nearest salvage into the locker | F9 | Erase save |
  | F5 | Drain 60 s of oxygen | F10 | Toggle hazard pause |

- **Preview images** of the generated level are in `Docs/Previews` (regenerate with **Trash Astronauts > Advanced > Render Preview Images**).

### Automated tests

Window > General > Test Runner, or from a terminal:

```bash
/Applications/Unity/Hub/Editor/6000.6.8f1/Unity.app/Contents/MacOS/Unity -batchmode -projectPath . -runTests -testPlatform EditMode -testResults Logs/results-editmode.xml
```

```bash
/Applications/Unity/Hub/Editor/6000.6.8f1/Unity.app/Contents/MacOS/Unity -batchmode -projectPath . -runTests -testPlatform PlayMode -testResults Logs/results-playmode.xml
```

Close the Unity editor first; two editors cannot open the same project.

## Building for Quest

A release APK has been built: **`Builds/Quest/TrashAstronauts.apk`** (about 32 MB, ARM64, IL2CPP,
Vulkan, OpenXR, minimum API 32, signed with the Android debug key). Its manifest declares the Quest VR
category and lists Quest 3 and Quest 3S as supported devices. It has not been installed on a headset.

Android Build Support (with OpenJDK and the SDK & NDK tools) is installed for editor 6000.6.8f1.

**To install on a Quest 3 / 3S** (developer mode on, headset connected by USB, USB debugging allowed in the headset):

```bash
/Applications/Unity/Hub/Editor/6000.6.8f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb install -r Builds/Quest/TrashAstronauts.apk
```

The game then appears under **Unknown Sources** in the headset's app library.

**To rebuild** (close the Unity editor first if it has this project open, or use the menu inside it):

- In the editor: **Trash Astronauts > Build Quest APK** (or **Build Quest APK (Development)** for the
  dev hotkeys and profiler). If the editor was already open when Android support was installed,
  restart it first so it sees the module.
- From a terminal:

  ```bash
  /Applications/Unity/Hub/Editor/6000.6.8f1/Unity.app/Contents/MacOS/Unity -batchmode -quit -buildTarget Android -projectPath . -executeMethod TrashAstronauts.EditorTools.BuildScript.BuildQuestApk
  ```

The build step applies the Quest settings itself every time: OpenXR loader for Android, the
**Meta Quest Support** feature, the **Meta Quest Touch Plus**, **Oculus Touch** and **Touch Pro**
controller profiles, single-pass instanced (multiview) rendering, and no internet permission. Also
configured: package id `com.trashastronauts.salvage`, IL2CPP, ARM64, Vulkan, linear colour, Input
System as the active input handler, 72 Hz physics step.

Before a store submission you would still need a release keystore (the APK is debug-signed), a check
of the target API level against Meta's current requirement (this build targets API 36), store assets,
and performance testing on the headset.

## What was and was not tested

| Kind | Status |
|---|---|
| **Compilation** | All assemblies compile with no errors in 6000.6.8f1. |
| **Automated tests, rules (EditMode, 45)** | Pass. Mission outcomes, duplicate-deposit prevention, bank-exactly-once, failure keeps old credits, restart cleanup, save/load including missing and corrupt files, oxygen upgrade, loot generation over hundreds of seeds, drone state machine, noise thresholds, cutter heat. |
| **Automated tests, real scene (PlayMode, 21)** | Pass. Fresh launch state, settings and handedness, loot worth more than the quota, every spawn point and the locker reachable from a teleport landing spot, teleport and unstuck, locker counts each item once, cutting opens the reactor compartment and the core can be recovered, mounted salvage releases when cut, drone investigates noise and returns to patrol, drone never enters the tug/airlock/duct, drone catches an exposed player and the run fails without touching banked credits, hazard pause, oxygen warnings and failure, both extraction payouts, clean restart, oxygen upgrade persists and applies. |
| **Editor simulation** | Simulator enabled; not exercised by hand. The PlayMode tests call the same public methods the VR interactions call, but they do **not** simulate controller input, so grabbing, poking, rail pulling and trigger use through XRI are unverified. |
| **Android build** | Succeeds. `Builds/Quest/TrashAstronauts.apk` was built in batch mode; its manifest, ARM64 libraries, OpenXR loader and signature were inspected. It was not installed or launched on a device. |
| **Headset** | Not tested. No claims about comfort, performance or feel. |

## Assumptions and design decisions

- **New project.** The session started with no project folder, so this is a fresh project built from Unity's bundled URP template.
- **Zero-g feel, fixed "up".** The player has no gravity and can drift up while pulling on rails; teleporting puts feet back on the floor. No camera roll, no camera shake, fades for forced moves.
- **Menus are physical buttons**, not a canvas UI: fingertip poke or point-and-trigger.
- **Tools are tethered** to the suit so an essential tool can never be lost or left behind a barrier.
- **The drone travels a hand-authored waypoint graph.** It cannot enter the tug, airlock or service duct, and it opens the reactor door slowly and audibly.
- **One oxygen upgrade level**, as specified. Costs and values are in `Assets/TrashAstronauts/Data/MissionConfig.asset`.
- **Saves** are a JSON file in `Application.persistentDataPath` with temp-file writes and a backup copy. Nothing is saved mid-mission.
- **Performance choices made up front:** Simple Lit materials, MSAA 4x, no HDR, no shadows, about a dozen unshadowed point lights, no post-processing, primitive colliders on everything that moves, around 25 active rigidbodies, pooled audio. Lighting is realtime, not baked.

## Known limitations

- Controller-relative offsets (glove, grab point, wrist display, holsters) were set by reasoning, not by trying them on. Expect to tune them on the first headset session.
- Target is a sustained 72 FPS on Quest 3/3S; this has not been measured.
- Art is Unity primitives with procedural textures; all audio is synthesized placeholder.
- Audio uses Unity's built-in 3D panning, no spatializer plugin.
- Held tools are kinematic and can poke through walls; held salvage is physical and cannot.
- The drone passes over loose junk rather than steering around it.
- Hand tracking, continuous locomotion and free 6-DoF flight are out of scope.
- Co-op is not implemented. See `Docs/MULTIPLAYER_NOTES.md` for where it would attach.

## Terms of Use

*Draft written for this prototype. It is not legal advice and has not been reviewed by a lawyer; have
it reviewed and replace the bracketed placeholders before you distribute the game.*

**1. Acceptance.** By installing or playing Trash Astronauts ("the Game") you agree to these terms. If
you do not agree, do not install or play it.

**2. Prototype software.** The Game is an early prototype. It may be incomplete, contain defects, lose
saved progress, or change without notice.

**3. Licence.** [Owner name] grants you a personal, non-exclusive, non-transferable, revocable licence
to install and play the Game for non-commercial purposes. You may not sell, sublicense or redistribute
it without written permission. All rights not granted here are reserved.

**4. Health and safety in VR.** Virtual reality can cause motion sickness, disorientation, eye strain
and loss of balance, and you can collide with real objects while wearing a headset.
- Follow your headset manufacturer's health and safety guidance and age recommendations.
- Play in a clear area, set up your play boundary, and consider playing seated.
- Stop immediately and rest if you feel unwell, dizzy or uncomfortable.
- Do not play if you are prone to seizures or have a condition that VR may aggravate without first consulting a doctor.

You play at your own risk.

**5. Privacy.** The Game runs fully offline. It has no accounts and no online services, and it does not
collect, transmit or share personal data. Progress (credits, the oxygen upgrade and comfort settings)
is stored only in a file on your own device and is removed when you uninstall the Game.

**6. Third-party software.** The Game is built with Unity and Unity packages, which are subject to
their own licences. See `Docs/ASSET_LICENSES.md`. "Meta Quest" is a trademark of its owner; the Game is
not affiliated with or endorsed by Meta or Unity.

**7. No warranty.** The Game is provided "as is" and "as available", without warranties of any kind,
express or implied, including fitness for a particular purpose and non-infringement, to the fullest
extent permitted by law.

**8. Limitation of liability.** To the fullest extent permitted by law, [Owner name] is not liable for
any indirect, incidental or consequential damages, or for any injury, loss of data or damage to
property or equipment, arising from use of the Game. Nothing in these terms limits liability that
cannot be limited by law.

**9. Changes and termination.** These terms may be updated with new versions of the Game. Your licence
ends automatically if you breach these terms.

**10. Governing law and contact.** These terms are governed by the laws of [jurisdiction]. Questions:
[contact email].

## Project layout

```
Assets/TrashAstronauts/
  Scripts/Core      rules only: mission session, oxygen, saves, loot planner, drone brain (no scene access)
  Scripts/Runtime   Mission/ Player/ Tools/ World/ UI/ Audio/ (scene components)
  Scripts/Editor    Builder.* (generates everything), BuildScript (Quest APK), PreviewRenderer
  Tests/EditMode, Tests/PlayMode
  Data              MissionConfig, salvage definitions, audio library (edit these to rebalance)
  Prefabs, Scenes, Input, Generated (materials, textures, audio)
Docs/               MULTIPLAYER_NOTES.md, ASSET_LICENSES.md, Previews/
```

Asset licenses: `Docs/ASSET_LICENSES.md` (everything is generated in-project or ships with Unity packages).

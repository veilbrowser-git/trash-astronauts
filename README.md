# Trash Astronauts

A single-player, fully offline VR salvage game for standalone Meta Quest (Quest 3 / 3S, tracked
controllers). You are an underpaid astronaut: board a derelict hauler from your salvage tug, float
through it by pushing off the walls and swimming, cut open panels, haul junk back to the tug's
collection locker, and get out before your oxygen runs out or the thing that used to be the ship's
janitor finds you.

## Download

**[Download TrashAstronauts.apk](https://github.com/veilbrowser-git/trash-astronauts/raw/builds/TrashAstronauts.apk)** (version 0.4.0, about 33 MB, Meta Quest 3 / 3S)

That one file is the whole game. To install it:

1. Turn on **Developer Mode** for your headset in the Meta Horizon phone app (required for any app that is not from the Meta store).
2. Connect the headset to a computer by USB and install the APK with [Meta Quest Developer Hub](https://developers.meta.com/horizon/documentation/unity/ts-mqdh/), SideQuest, or `adb install -r TrashAstronauts.apk`.
3. In the headset, open the app library and look under **Unknown Sources**.

Version 0.4.0 puts one of three different creatures aboard at random each contract, turns the hands to a natural handshake pose so tools are held by their handles, makes swimming stronger and adds a wrist thruster and a brake so moving is easier, and adds trick-shot bonuses, a hidden squeaky rubber duck, throw recoil, breathing and a heartbeat. It has not been run on a headset yet.

Version 0.3.0 moved the grab point into the palm, made swimming work with open hands, made hands and walls solid, quieted the creature and gave it a jumpscare, and added a shop, a tranquilizer, random blackouts, a map art pass and experimental hand tracking. Versions 0.2.0 and 0.3.0 were played on a Quest by the project owner.

> **Verification status, read this first.** The project compiles, the scene and all assets are
> generated, 100 automated tests pass in the Unity editor on macOS, and a Quest APK builds
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

1. **Tug (safe hub) and tutorial.** A guided tutorial teaches one thing at a time and only moves on
   when you have done it: push off a wall, swim, stop, draw a tool, bank the practice scrap, cut the
   practice plate. The current step shows on the wall board and on a small panel that floats with
   you. Any step, or all of it, can be skipped, and it can be replayed later from the board. No
   oxygen drains and the creature is dormant until you start a contract.
2. **START CONTRACT** opens the hatch and puts **one of three creatures** aboard, chosen at random: **JANITOR-7** (sees and hears about equally), **THE LISTENER** (blind, but hears everything, including your thruster) or **THE WARDEN** (slow and half deaf, but sees a long way). The mission board tells you which one and how it hunts. Your flashlight (chest), scanner and plasma cutter (hips) are already on your suit.
3. **Airlock > corridor > cargo bay / maintenance / reactor.** Loot is placed at random among 20 curated spots each run.
4. **Cut** the marked welds on access panels: the reactor housing (the high-value core), the secure
   cargo locker, the rack clamp (mounted electronics) and the service-duct cover (a shortcut from
   maintenance to the reactor that the drone cannot use).
5. **Carry salvage back** and let go of it in the collection locker. It is counted once and locked away. **Throw it in from more than 2.2 m away for a TRICK SHOT**: +25% of its value, and the bonus counts toward the quota.
   Somewhere in the wreck there is also a **rubber duck**. Squeeze the trigger while holding it and it squeaks, loudly enough to draw the creature, so it can be thrown as a decoy. It is worth 75 cr at the locker.
6. **Risk another trip or EXTRACT** (press twice to confirm) from the mission board.
7. **Results:** contract complete (salvage + bonus), quota missed (salvage only), or failed (nothing).
8. **Buy the O2 TANK+ upgrade** (600 cr, +3:00 oxygen, permanent) and start the next contract.

## Controls (Quest Touch controllers, either hand)

| Action | Input |
|---|---|
| Grab / hold / drop / throw | **Grip**. Works on salvage, junk, tools and handrails. Release while moving your hand to throw. Throwing pushes you back the other way: barely for a scrap plate, a firm shove for the 14 kg reactor core. |
| Float: grab and push off | You are weightless. Hold **grip** with an empty hand next to any wall, floor, ceiling, prop or yellow rail to grab it. Move your hand to pull your body the other way; **let go while moving** and you keep gliding. Grabbing a surface stops a drift. |
| Float: thruster | Point an **empty hand** where you want to go and hold **trigger**. A small wrist thruster pushes you that way, up to 2.4 m/s. It hisses quietly: most things will not notice, the Listener will. |
| Brake | **Click a thumbstick in** and hold. You slow to a stop in under a second. |
| Float: swim | Look where you want to go and sweep your arms back with **open hands**, like swimming. No button. Strokes are stronger and easier to trigger than in 0.3.0. Open-hand strokes never push you backward, so reaching for things is safe. Hold **grip** in open space to paddle in any direction, including backward. A hand that is carrying something cannot paddle. |
| Solid hands and walls | Your hands stop at walls instead of passing through. Put an open hand on a wall and push, or slap it, and you are shoved away. Your body bounces off the hull. |
| Teleport (comfort) | Push a **thumbstick forward** to aim an arc, release to teleport. Works while carrying things, and the whole mission can be done this way. |
| Drift on/off (comfort) | **DRIFT** in the suit menu. Off = you stop almost as soon as you let go, with no gliding. |
| Snap turn | **Thumbstick left / right** (30 / 45 / 60 / 90 degrees, set in the suit menu). |
| Use tool | **Trigger** while holding it: flashlight on/off, scanner ping, cutter flame, tranquilizer dart. |
| Shop | The **COMPANY STORE** panel on the tug wall: tranquilizer darts (each puts the creature to sleep for about 8 seconds), a scanner range upgrade and a cutter coolant upgrade. Closed during a contract. |
| Hand tracking (experimental) | Put the controllers down. **Close your fist** = grip, **pinch thumb and index** = trigger, **left palm toward your face + pinch** = suit menu. Teleport, snap turn and the brake need a thumbstick, so with bare hands you move by pushing off, swimming and the thruster (point and pinch). Written without a headset to test on. |
| Draw / recall a tool | Grip at the **chest** (flashlight in the middle, tranquilizer launcher beside it) or **hips** (cutter on your main-hand side, scanner on the other). Gripping an empty holster pulls the tool to your hand from anywhere. Dropped tools reel themselves back after a few seconds. |
| Press buttons | Touch with a fingertip, **or** point at a button and pull the trigger (up to 3.5 m). |
| Suit menu | **B / Y** (or the left menu button): height +/-, snap-turn angle, left/right hand, drift on/off, unstuck, recall tools. The same panel is on the tug's rear wall. |

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
| **Automated tests, rules and content (EditMode, 59)** | Pass. New in 0.4.0: trick-shot bonus rules, the three creature prefabs and their different senses, the handshake hand pose with tools held by their handles, and the rubber duck. Includes the shop rules, the cutter coolant upgrade and the hand-tracking gesture maths. Mission outcomes, duplicate-deposit prevention, bank-exactly-once, failure keeps old credits, restart cleanup, save/load including missing and corrupt files, oxygen upgrade, loot generation over hundreds of seeds, drone state machine, noise thresholds, cutter heat. |
| **Automated tests, real scene (PlayMode, 41)** | Pass. New in 0.4.0: one creature per contract chosen from three by the run's seed (all three turn up over 24 contracts), each creature wakes, patrols and hears with its own senses, the thruster pushes where the hand points and tops out, the brake stops you, a hand holding something does not thrust, trick shots pay only for real throws from range, throw recoil conserves momentum, and the duck is hidden every run, squeaks, and draws the creature. From 0.3.0: open-hand swimming at realistic stroke speeds, solid hands pushing off a wall, the shop, the tranquilizer putting the creature to sleep and it waking again, the blackout and its recovery, the jumpscare on being caught, and held items resting in the palm. Three of these drive the game through a simulated tracked controller with the real input bindings and grab interactor: the hand follows the controller, salvage can be picked up, carried and released, and the cutter can be drawn from its holster, fired and used to cut a panel open. Zero-g drift keeps momentum and is stopped by the hull, grabbing a wall pulls the body and letting go pushes off, swim strokes propel the player, the drift comfort option, the tutorial advancing on real actions and being replayable. Also: fresh launch state, settings and handedness, loot worth more than the quota, every spawn point and the locker reachable from a teleport landing spot, teleport and unstuck, locker counts each item once, cutting opens the reactor compartment and the core can be recovered, mounted salvage releases when cut, drone investigates noise and returns to patrol, drone never enters the tug/airlock/duct, drone catches an exposed player and the run fails without touching banked credits, hazard pause, oxygen warnings and failure, both extraction payouts, clean restart, oxygen upgrade persists and applies. |
| **Editor simulation** | Simulator enabled; not exercised by hand. The PlayMode tests call the same public methods the VR interactions call, most of them do **not** simulate controller input (the zero-g tests move the hand transforms directly). Grabbing and tool use are covered by the simulated-controller tests; button poking and how the floating feels are unverified. |
| **Android build** | Succeeds. `Builds/Quest/TrashAstronauts.apk` was built in batch mode; its manifest, ARM64 libraries, OpenXR loader and signature were inspected. It was not installed or launched on a device. |
| **Headset** | Version 0.4.0 has not been run on a headset. Version 0.3.0 was played on a Quest by the project owner, who reported that tools were still held oddly, that moving around was hard and that the game needed more to do; 0.4.0 is the response, but the new hand pose, thruster feel and creature balance are set by reasoning and by tests, not by trying them on. Version 0.2.0 was played on a Quest by the project owner: grabbing worked but held items sat out at the fingers, and swimming did nothing. Both are fixed in 0.3.0 (the swim bug was a rest threshold that wiped out the small speed each frame of a stroke adds). Version 0.3.0 itself has not been run on a headset yet, and hand tracking has never been run on one. Earlier: version 0.1.0 was played on a Quest by the project owner: it launched and tracked, but nothing could be grabbed, so tools and cutting were unusable. That was a bug in the grab detector's setup and is fixed in 0.2.0, verified with the simulated-controller tests. Version 0.2.0 itself has not been run on a headset yet. No claims about comfort or performance. |

## Assumptions and design decisions

- **New project.** The session started with no project folder, so this is a fresh project built from Unity's bundled URP template.
- **Weightless movement, fixed "up".** The player floats with momentum: pushing off surfaces and swimming strokes set a drift that decays slowly, capped at 3.6 m/s, and stops on contact with the hull. Teleport and a no-drift option remain for comfort. No camera roll, no camera shake, fades for forced moves.
- **Menus are physical buttons**, not a canvas UI: fingertip poke or point-and-trigger.
- **Tools are tethered** to the suit so an essential tool can never be lost or left behind a barrier.
- **Blackouts.** At a random point in a contract the wreck loses power for 12 to 20 seconds: every ship light dies and the ambience and the creature go silent. Floor guide lights under the rails, your flashlight and the creature's eyes still show. The tug keeps its lights.
- **Catching.** Being caught now plays a jumpscare: the creature lunges to arm's length in front of you, jaws open, with one short sting, then the view fades. The camera is never moved or shaken.
- **The creature travels a hand-authored waypoint graph.** It cannot enter the tug, airlock or service duct, and it opens the reactor door slowly and audibly. It is a spined black carapace with a cluster of red eyes, a jaw, four long clawed limbs and trailing cables, all animated procedurally; ship lights stutter when it is close. No gore.
- **One oxygen upgrade level**, as specified. Costs and values are in `Assets/TrashAstronauts/Data/MissionConfig.asset`.
- **Saves** are a JSON file in `Application.persistentDataPath` with temp-file writes and a backup copy. Nothing is saved mid-mission.
- **Performance choices made up front:** Simple Lit materials, MSAA 4x, no HDR, no shadows, about a dozen unshadowed point lights, no post-processing, primitive colliders on everything that moves, around 25 active rigidbodies, pooled audio. Lighting is realtime, not baked.

## Known limitations

- Controller-relative offsets (glove, grab point, wrist display, holsters) were set by reasoning, not by trying them on. Expect to tune them on the first headset session.
- Target is a sustained 72 FPS on Quest 3/3S; this has not been measured.
- Art is generated in-project: chamfered and lathed meshes with normal-mapped procedural textures. It is a clear step up from raw primitives but still programmer art; all audio is synthesized placeholder.
- Free-floating movement with momentum is more likely to cause motion sickness than teleporting. This has not been comfort-tested on a headset; the drift toggle and teleport are there for that reason.
- Audio uses Unity's built-in 3D panning, no spatializer plugin.
- Held tools are kinematic and can poke through walls; held salvage is physical and cannot.
- The creature passes over loose junk rather than steering around it, and its long limbs can clip through door frames.
- The three creatures share one skeleton and one patrol route; they differ in senses, speed, size and look. Their balance has not been play-tested.
- Working hard (swimming, thrusting) uses oxygen up to 40% faster. Breathing and heartbeat audio are synthesized and may want tuning or an off switch.
- Hand tracking is implemented but experimental: the gesture thresholds and palm alignment are untested on a headset. Thumbstick-driven continuous locomotion is out of scope.
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

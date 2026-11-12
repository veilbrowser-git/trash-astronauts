# Trash Astronauts

A VR salvage game for standalone Meta Quest (Quest 3 / 3S, tracked controllers). It is a
single-player game; an early co-op preview lets 2 to 4 headsets on the same Wi-Fi meet in the tug and see
each other (see **Co-op on the same Wi-Fi** below). Nothing goes over the network unless you press one of
the co-op buttons. You are an underpaid astronaut: board a derelict hauler from your salvage tug, float
through it by pushing off the walls and swimming, cut open panels, haul junk back to the tug's
collection locker, and get out before your oxygen runs out or the thing that used to be the ship's
janitor finds you.

## Download

**[Download TrashAstronauts.apk](https://github.com/veilbrowser-git/trash-astronauts/raw/builds/TrashAstronauts.apk)** (version 0.10.0, about 48 MB, Meta Quest 3 / 3S)

That one file is the whole game. To install it:

1. Turn on **Developer Mode** for your headset in the Meta Horizon phone app (required for any app that is not from the Meta store).
2. Connect the headset to a computer by USB and install the APK with [Meta Quest Developer Hub](https://developers.meta.com/horizon/documentation/unity/ts-mqdh/), SideQuest, or `adb install -r TrashAstronauts.apk`.
3. In the headset, open the app library and look under **Unknown Sources**.

Version 0.10.0 is the **first co-op build**, and it is deliberately small: on the same Wi-Fi, one headset presses **HOST CO-OP** on the new CO-OP CREW panel in the tug, the others press **FIND CREW** and **JOIN** (or ENTER CODE), and up to four players **see each other** in the tug as suited astronauts with moving heads, arms and gloved hands, each in their own colour. Contracts cannot be started while a crew link is open yet; the shared contract comes next, and online play after that. The game now asks for the network permission, and contacts nothing until one of those buttons is pressed. It has never been tried on two real headsets: whether they find each other on your network is exactly what this build is for.

Version 0.9.3 is about the spider's legs and what you hear. **The spider's feet are planted**: each foot stays fixed on the floor, wall or ceiling while the body moves over it, the legs step in two alternating groups of four with the knees up, and it stands still when it stops. **Breathing and a heartbeat** follow your effort and your fear (the spider near or chasing). **Sounds are muffled through walls** and clear in line of sight, and **rooms echo according to their size**. The new light fixtures **go dark with their lamp** when it is dead or during a blackout. It has not been run on a headset yet.

Version 0.9.2 continues the physics work. **Heavy items can be carried with two hands**, which steadies them and lets you throw them harder. **The spider is a solid body**: it knocks floating junk aside as it walks, and a heavy item thrown hard enough makes it **flinch** for a moment (not more than once every six seconds, and never during the jumpscare). **Delicate items crack** and shed a few shards when they take a damaging hit. In the Moon cave the **thruster now gives a real hop** instead of barely cancelling your weight. More wall detail in the ships (junction boxes, structural ribs). It has not been run on a headset yet.

Version 0.9.1 adds a fourth place to salvage, the **Lava Tube Outpost**: an abandoned mining camp inside a cave on the Moon, with **lunar gravity** (loose things fall slowly and come to rest, and you settle to the ground and hop with the thruster). The three ships have been **redecorated**: panelled, riveted walls and tread-plate floors that catch the flashlight, pipe runs, light fixtures, soft-edged flashlight beam, dust in the air, and a planet outside the windows. The gloves are **rounder** (tapered fingers with knuckles, arched back of the hand). It has not been run on a headset yet; the cave's gravity in particular is untried for comfort.

Version 0.9.0 is about **realism and physics**. The hands are now **space-suit gloves modelled in Blender** with fingers that bend at each knuckle, curl with the trigger and grip, and close around what you hold. Every loose object has a **real mass**: heavy things lag and swing in the hand, held objects stop at walls instead of passing through them, throws carry the true speed and spin of your hand, throwing or shoving something heavy **pushes you back**, the thruster is slower when you haul a heavy load, and closing doors ease loose objects out of the way. See the Physics section below. It has not been run on a headset yet, so the feel of the weights is untuned.

Version 0.8.3 **removes the webs strung across the corridors and the hanging cocoons**. Only the corner webs remain. An INFESTED contract now means the egg-sac young notice you from farther off and move faster, instead of more webs. The code for co-op groundwork is also in this build, but it opens no network connection and co-op is not playable yet. It has not been run on a headset yet.

Version 0.8.2 gives the player a **head and a body**: a salvage-crew space suit modelled in Blender (helmet, torso with a life-support pack, arms that follow your hands, legs). You see your own body when you look down, but not your own helmet. It is the suit other players will wear when co-op arrives; co-op itself is not in this build. It has not been run on a headset yet.

Version 0.8.1 is about dread rather than shocks, following what horror developers say actually frightens players (vulnerability, not knowing when it will appear, limited light, and hearing something you cannot see). You can now **hear the spider walking**, from where its body is, through walls and overhead; and sometimes it **stops dead and goes silent**, and you lose track of it. A share of each wreck's **lights are dead or failing**, different ones each contract. The **flashlight has a battery** (about five minutes; it recovers when switched off and the tug recharges it). Everything is heard **through the helmet**, slightly muffled. The crew left **log entries** on the walls. There are **far fewer webs**. The **inventory has been removed**, and the jumpscare is unchanged from 0.8.0. Staged scares are no more frequent than before. It has not been run on a headset yet.

Version 0.8.0: the spiders **no longer float**. They walk on the floor, climb the walls and cross the ceiling, lurk overhead while dormant, lower themselves on a thread where there is no wall to climb, and come down to the ground to run you down. The **jumpscare** is rebuilt again: darkness, one flash that shows it hanging above you, darkness, then it lands on your visor and bites four times, each bite **cracking the visor** further and flashing the view red, and it ends in blackness with only the sound of it feeding. New things to deal with: **web tripwires** strung across routes (blunder through one and it drags at you and tells the spider exactly where you are; burn it with the cutter and it goes quietly), **swarms of small spiders** around the egg sacs that bolt when you come close, **doors that slam** behind you, a random **condition** on every contract (rich wreck, thin air, power fault, infested, light sleeper, dead calm), and a **best haul** record. It has not been run on a headset yet.

Version 0.7.0 added a wrist inventory (removed again in 0.8.1), a bigger lobby with a **supply bay**, a **3D company store** that folds itself into a crate when a contract starts, **webs, cocoons and egg sacs** in the wrecks, a scare where a **spider drops on a thread** in front of your face, and more physical handling: open hands can **swat** loose things, your body **shoulders** floating junk aside, and throws follow the flick of your wrist. It has not been run on a headset yet.

Version 0.6.0 (second build) fixes **spawning outside the lobby** (the game now puts your head, not your play-space centre, on the spawn point, and pulls you back in if you are ever outside the tug with no contract running). It also makes the creatures **hunt properly** (they leave their routes to fly straight at you, cut corners, predict where you went, follow sounds after losing sight, and are faster than the thruster), replaces the creatures with **realistic giant spiders modelled in Blender** (a furred tarantula, a pale long-legged cave spider, a stocky big-eyed hunter and a glossy widow, each with eight seven-segment legs, fangs, pedipalps, eight eyes and thousands of individual hairs, walking with a real spider's alternating gait), adds a **noise meter** to the wrist display, makes **delicate salvage lose value when it is banged about**, and adds staged **scares** and a darker ship. Being caught is now a full **jumpscare**: every light dies and the ship goes silent for half a second, then the spider is in your face under a red strobe, thrashing and lunging to bite with layered shrieks and the controllers at full rumble. There is also a fake-out where a tall shape sprints at you and vanishes a metre short.

Version 0.6.0 adds a fourth creature, **the Mimic**: when it is aboard it keeps the last few things you said and says them back to you, lower and slightly wrong, from wherever it is. It has not been run on a headset yet.

Version 0.5.0 lets the creature hear your real voice through the headset microphone, adds two new wrecks (three in all, one picked at random per contract) and makes the original about 1.5 times bigger, adds a flight deck to the tug, and re-synthesizes every sound effect. It asks for microphone permission the first time; the microphone is only measured for loudness on the headset, nothing is recorded or sent anywhere, and it can be switched off (MIC in the suit menu). It has not been run on a headset yet.

Version 0.4.0 put one of three different creatures aboard at random each contract, turned the hands to a natural handshake pose so tools are held by their handles, made swimming stronger and added a wrist thruster and a brake, and added trick-shot bonuses, a hidden squeaky rubber duck, throw recoil, breathing and a heartbeat.

Version 0.3.0 moved the grab point into the palm, made swimming work with open hands, made hands and walls solid, quieted the creature and gave it a jumpscare, and added a shop, a tranquilizer, random blackouts, a map art pass and experimental hand tracking. Versions 0.2.0 and 0.3.0 were played on a Quest by the project owner.

> **Verification status, read this first.** The project compiles, the scene and all assets are
> generated, 401 automated tests pass in the Unity editor on macOS, and a Quest APK builds
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

1. **Tug (safe hub) and tutorial.** The tug has a cabin (mission board, tutorial, collection locker, suit settings), a flight deck through the bulkhead with seats, a windscreen and a crew record screen, and off the flight deck a supply bay with the company store: a stall with the goods turning on the counter above their buy buttons. When a contract starts the stall folds up (awning down, shutter closed) and shrinks to a crate; it unpacks when you are back. A guided tutorial teaches one thing at a time and only moves on
   when you have done it: push off a wall, swim, stop, draw a tool, bank the practice scrap, cut the
   practice plate. The current step shows on the wall board and on a small panel that floats with
   you. Any step, or all of it, can be skipped, and it can be replayed later from the board. No
   oxygen drains and the creature is dormant until you start a contract.
2. **START CONTRACT** docks **one of three wrecks**, chosen at random: the hauler *Margin Call* (a long spine corridor with rooms off it), the ore barge *Slow Dividend* (a tall central hold you can float over, rooms on three sides) or the survey ship *Due Diligence* (a ring corridor around the lab and reactor, so there are always two ways round). It opens the hatch and puts **one of four creatures** aboard, chosen at random: **JANITOR-7** (sees and hears about equally), **THE LISTENER** (blind, but hears everything, including your thruster), **THE WARDEN** (slow and half deaf, but sees a long way) or **THE MIMIC** (answers you with your own voice a couple of seconds after you speak, and repeats older phrases while it wanders; it needs MIC on, otherwise it is silent). The mission board tells you which one and how it hunts. Your flashlight (chest), scanner and plasma cutter (hips) are already on your suit.
3. **Airlock > the wreck.** Every wreck has a reactor or drive core sealed in a housing, a locked store, a clamped electronics rack and a sealed shortcut the creature cannot use. Loot is placed at random among 20 curated spots per wreck each run.
4. **Cut** the marked welds on access panels: the reactor housing (the high-value core), the secure
   cargo locker, the rack clamp (mounted electronics) and the service-duct cover (a shortcut from
   maintenance to the reactor that the drone cannot use).
5. **Handle it carefully.** Electronics, the toaster and the reactor core are delicate: every hard knock (above about 2 m/s) takes value off, down to a third of what they started at. Scrap does not care. The scan tag shows the loss.
6. **Carry salvage back** and let go of it in the collection locker. It is counted once and locked away. **Throw it in from more than 2.2 m away for a TRICK SHOT**: +25% of its value, and the bonus counts toward the quota.
   Somewhere in the wreck there is also a **rubber duck**. Squeeze the trigger while holding it and it squeaks, loudly enough to draw the creature, so it can be thrown as a decoy. It is worth 75 cr at the locker.
7. **Risk another trip or EXTRACT** (press twice to confirm) from the mission board.
8. **Results:** contract complete (salvage + bonus), quota missed (salvage only), or failed (nothing).
9. **Buy the O2 TANK+ upgrade** (600 cr, +3:00 oxygen, permanent) and start the next contract.

## Controls (Quest Touch controllers, either hand)

| Action | Input |
|---|---|
| Grab / hold / drop / throw | **Grip**. Works on salvage, junk, tools and handrails. Release while moving your hand to throw. Throwing pushes you back the other way: barely for a scrap plate, a firm shove for the 14 kg reactor core. |
| Float: grab and push off | You are weightless. Hold **grip** with an empty hand next to any wall, floor, ceiling, prop or yellow rail to grab it. Move your hand to pull your body the other way; **let go while moving** and you keep gliding. Grabbing a surface stops a drift. |
| Swat and shove | Swing an **open hand** fast at loose salvage to bat it away (a slow reach never knocks things about). Drifting into floating junk nudges it aside. Nothing can push you back. |
| Flashlight battery | The light lasts about five minutes. Under 30% the wrist display shows the charge and the beam dims and stutters; flat, it goes out. Switch it off and it recovers (faster than it drains), and the tug recharges it in a couple of seconds. |
| Listening | The spider's footsteps come from where its body is, so you can hear it on the other side of a wall or above you. Fast footsteps mean it is hunting. No footsteps does not mean it has gone: sometimes it stops and waits. |
| Noise meter | The wrist display has a **NOISE bar**: green, amber, red. It shows how loud you are being right now, from your voice or the last loud thing you did (IMPACT, CUTTER, PANEL, DOOR, THRUSTER), and fades as you go quiet. It flashes **IT HEARD THAT** when the creature reacted. |
| Your voice | **The creature can hear you.** Talking near it brings it over, shouting carries a long way, whispering is safe. The wrist display shows a MIC level and tells you when you were loud enough to be heard. Switch it off with **MIC** in the suit menu. The headset will ask for microphone permission once; without it the game plays as before. |
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

## Co-op on the same Wi-Fi (preview)

This first co-op version only lets the crew **see each other**: every player's suit, helmet, head and both
hands (fingers included) move in the tug, each suit in its own colour (orange is the host, then blue, green
and magenta). **Contracts are not shared yet**: while a crew is linked, no headset can start a contract, and
the mission board says so. Press LEAVE to play a contract alone.

The **CO-OP CREW** panel is on the tug wall opposite the mission board, between the collection locker and
the practice plate:

1. On one headset press **HOST CO-OP**. The panel shows the tug number and the crew code, for example
   `TUG 4821` and `C 001  D 042`.
2. On each other headset press **FIND CREW**. Tugs that answer are listed with a **JOIN** button each;
   press **JOIN** next to the right tug number.
3. If FIND CREW finds nobody, press **ENTER CODE**, type the host's crew code on the keypad (C is filled in
   for you; usually only D needs typing) and press **JOIN**.
4. **LEAVE** on any headset goes back to playing alone. If the host leaves, everyone is put back to solo.

All headsets need the same game version and the same Wi-Fi network. Some routers stop devices on the same
Wi-Fi from seeing each other (called client or AP isolation; common on guest networks), and then neither
FIND CREW nor ENTER CODE can work. Co-op has not been tried with two real headsets yet: whether FIND CREW
finds a host on a Quest, and whether a Quest shows its own crew code correctly, are still to be confirmed.

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
controller profiles, single-pass instanced (multiview) rendering, and the INTERNET permission (for
co-op on the same Wi-Fi; a check in the build fails it if the permission is missing). Also
configured: package id `com.trashastronauts.salvage`, IL2CPP, ARM64, Vulkan, linear colour, Input
System as the active input handler, 72 Hz physics step.

Before a store submission you would still need a release keystore (the APK is debug-signed), a check
of the target API level against Meta's current requirement (this build targets API 36), store assets,
and performance testing on the headset.

## What was and was not tested

| Kind | Status |
|---|---|
| **Compilation** | All assemblies compile with no errors in 6000.6.8f1. |
| **Automated tests, rules and content (EditMode, 262)** | Pass. New for the co-op preview: the crew stand-in has the helmeted suit, the modelled gloves and a door-blocking body but no camera, listener, interactor, input, light or tool/salvage layer; the scene has exactly one offline network session and a CO-OP CREW panel clear of the other tug furniture; the Android build keeps the INTERNET permission, and the build check fails a manifest without it. New in 0.9.1: the cave stays within its triangle and renderer budget with its lights registered; ship walls and floors have normal maps, decoration has no colliders and stays within a per-ship budget. New in 0.9.0: the glove model has its named parts, stays in budget, is not inside-out, and both hands wear it the right way round. New in 0.8.3: the networking core for co-op (message formats, reliable delivery under packet loss, joining and item ownership rules), tested without opening a real connection to anyone. New in 0.8.2: the Blender suit export has every named part, stays within its triangle budget and is not inside-out. Second 0.6.0 build: a chasing creature that has lost sight follows sounds and can be pointed at a better guess; damaged salvage is worth less, never recovers, and is safe once in the locker. New in 0.6.0: speech is cut into phrases correctly (short noises dropped, long speech capped, only the last four kept, all forgotten on clear) and the Mimic prefab is the only creature that records. From 0.5.0: the voice detector (silence and steady background noise are ignored, a whisper is safe, talking is heard, shouting carries further, a single click is ignored), and all three wrecks being complete, inside the playable volume and always able to pay the quota for 300 seeds each. From 0.4.0: trick-shot bonus rules, the three creature prefabs and their different senses, the handshake hand pose with tools held by their handles, and the rubber duck. Includes the shop rules, the cutter coolant upgrade and the hand-tracking gesture maths. Mission outcomes, duplicate-deposit prevention, bank-exactly-once, failure keeps old credits, restart cleanup, save/load including missing and corrupt files, oxygen upgrade, loot generation over hundreds of seeds, drone state machine, noise thresholds, cutter heat. |
| **Automated tests, real scene (PlayMode, 139)** | Pass. New for the co-op preview: the scene hosts and simulated headsets join over an in-memory network; their suits appear in their slot colours, follow the stream about 100 ms behind, curl their fingers, go away after silence and disappear on LEAVE or a timeout; the scene joins a simulated host, takes slot 1 and sees the host and another joiner, and LEAVE makes it solo again; the same over real sockets on 127.0.0.1; the panel's HOST CO-OP, FIND CREW with JOIN, ENTER CODE keypad and LEAVE buttons work and every refusal is shown in words; no contract starts while a crew is linked; a whole offline contract opens no socket. Co-op has not been tried on two real headsets. New in 0.9.3: on all four species planted feet slide less than 2 cm and stay on the surface on floor, wall and ceiling, the two leg groups are never in the air together, and the foot placement is off while dormant and in the jumpscare; breathing and heartbeat rise and settle smoothly and are calm in the tug; a wall between you and a sound muffles it; a small and a large room get different reverb; fixture lenses follow their lamps through dead lights and blackouts. New in 0.9.2: two hands hold a heavy item and it lags less, one hand can let go without dropping it; a 40 kg core thrown at 4 m/s staggers the spider once and not again inside the cooldown, a can does nothing, and it cannot be staggered asleep or mid-jumpscare; it knocks a floating crate ahead of it; delicate items shed at most twelve shards that fade; the cave thruster lifts you well clear of the ground. New in 0.9.1: in the cave a released item falls and rests on the floor while in a ship it keeps drifting, the player comes to rest on the ground and can leave it with the thruster, the tug stays weightless, and the spider patrols the cave. New in 0.9.0: thirteen tests that run real physics (nothing tunnels through a wall at 25 m/s, recoil conserves momentum, thrust scales with load, heavy items lag then catch up, held items stop at a wall and the grip slips, throw speed limits by mass, doors clear loose items) and six on the gloves (finger parts on both hands, rest pose, trigger moves only the index finger, fingers stop on a held object). New in 0.8.3: no web is strung across a route on any contract, infested or not, and the cocoons are gone; co-op groundwork seams behave identically offline. New in 0.8.2: the player has a suit with a visible torso and a hidden helmet, the torso follows the head, the arms reach the hands and stay on the shoulders, every holstered tool is still visible past the body, and the crew colour can be set. New in 0.8.1: the spider's footsteps play from its body and are silent while it is dormant or lying in wait, it stops dead on its rounds and a noise ends the wait; some lights are dead, different ones per contract, never the airlock's, and stay dead through a blackout; the flashlight drains, goes out when flat, refuses to light, recovers when off and is recharged by the tug. New in 0.8.0: all four spiders rest on the ceiling when dormant, stand on the floor (not floating), climb the wall standing sideways, and cross to the ceiling upside down, with their hit volume following the body; a hunting spider comes down and still catches the player; a web tripwire slows the player, raises a noise and draws the spider, burning one is silent, and an infested contract strings them all; conditions change oxygen, values, bonus and wake time and appear on the board; spiderlings wander and bolt; a door behind the player slams; the best haul is remembered. The jumpscare test now also checks the glimpse, at least three bites, the visor cracking and the blackout ending. New in 0.7.0: the shop is a 3D stall in the supply bay (part of the safe tug), still sells, folds to a small crate when a contract starts and unfolds afterwards; a slow reach does not knock salvage away but a fast open-hand swing bats it and drifting into it pushes it, without moving or slowing the player; the dropping spider appears at arm's length and leaves, harmlessly. Second 0.6.0 build: the jumpscare kills every light first, holds the creature back unseen, then puts it within half a metre of the player's eyes and squarely in view for over a second, and restores the lights afterwards; the charging fake-out gets close but never reaches or harms the player; a player whose head is 3 m from their play-space centre still starts inside the lobby, and one found outside it before a contract is put back; the creature leaves its route to run down a player standing in the middle of a big room without passing through anything, is faster than the thruster and rises toward a player floating high; the wrist noise meter shows impacts and voice and fades; delicate salvage loses value when banged and the locker pays what is left; every staged scare can be triggered, the lights come back, and the glimpse vanishes when looked at. New in 0.6.0: the Mimic answers with the phrase it was given, from its own position, lower and slower; the microphone ignores the Mimic's voice; phrases are kept only while a Mimic is aboard and are forgotten when the contract ends or MIC is switched off; it goes quiet in a blackout and when tranquilized. These use injected test phrases, not a real microphone. From 0.5.0: one wreck docked per contract, chosen by the run's seed, with the previous one cleaned up; in every wreck all loot spots, panels and recovery points are reachable by teleport, the creature completes its whole patrol without passing through walls or entering the tug or the sealed shortcuts, the blackout takes out that wreck's lights and its sealed panels open; the creature hears talking but not a whisper, and not with MIC off (driven with a simulated microphone level, not a real microphone). From 0.4.0: one creature per contract chosen from three by the run's seed (all three turn up over 24 contracts), each creature wakes, patrols and hears with its own senses, the thruster pushes where the hand points and tops out, the brake stops you, a hand holding something does not thrust, trick shots pay only for real throws from range, throw recoil conserves momentum, and the duck is hidden every run, squeaks, and draws the creature. From 0.3.0: open-hand swimming at realistic stroke speeds, solid hands pushing off a wall, the shop, the tranquilizer putting the creature to sleep and it waking again, the blackout and its recovery, the jumpscare on being caught, and held items resting in the palm. Three of these drive the game through a simulated tracked controller with the real input bindings and grab interactor: the hand follows the controller, salvage can be picked up, carried and released, and the cutter can be drawn from its holster, fired and used to cut a panel open. Zero-g drift keeps momentum and is stopped by the hull, grabbing a wall pulls the body and letting go pushes off, swim strokes propel the player, the drift comfort option, the tutorial advancing on real actions and being replayable. Also: fresh launch state, settings and handedness, loot worth more than the quota, every spawn point and the locker reachable from a teleport landing spot, teleport and unstuck, locker counts each item once, cutting opens the reactor compartment and the core can be recovered, mounted salvage releases when cut, drone investigates noise and returns to patrol, drone never enters the tug/airlock/duct, drone catches an exposed player and the run fails without touching banked credits, hazard pause, oxygen warnings and failure, both extraction payouts, clean restart, oxygen upgrade persists and applies. |
| **Editor simulation** | Simulator enabled; not exercised by hand. The PlayMode tests call the same public methods the VR interactions call, most of them do **not** simulate controller input (the zero-g tests move the hand transforms directly). Grabbing and tool use are covered by the simulated-controller tests; button poking and how the floating feels are unverified. |
| **Android build** | Succeeds. `Builds/Quest/TrashAstronauts.apk` was built in batch mode; its manifest, ARM64 libraries, OpenXR loader and signature were inspected. It was not installed or launched on a device. |
| **Headset** | Version 0.6.0 has not been run on a headset: the Mimic has never recorded or played back a real voice. Version 0.5.0 has not been run on a headset by me either: the microphone, the two new wrecks, the enlarged first wreck, the flight deck and the new sound effects are all unheard and unseen in VR. The sound effects were checked numerically (levels, clipping, loop seams), not by ear. Version 0.4.0 has not been run on a headset by me either. Version 0.3.0 was played on a Quest by the project owner, who reported that tools were still held oddly, that moving around was hard and that the game needed more to do; 0.4.0 is the response, but the new hand pose, thruster feel and creature balance are set by reasoning and by tests, not by trying them on. Version 0.2.0 was played on a Quest by the project owner: grabbing worked but held items sat out at the fingers, and swimming did nothing. Both are fixed in 0.3.0 (the swim bug was a rest threshold that wiped out the small speed each frame of a stroke adds). Version 0.3.0 itself has not been run on a headset yet, and hand tracking has never been run on one. Earlier: version 0.1.0 was played on a Quest by the project owner: it launched and tracked, but nothing could be grabbed, so tools and cutting were unusable. That was a bug in the grab detector's setup and is fixed in 0.2.0, verified with the simulated-controller tests. Version 0.2.0 itself has not been run on a headset yet. No claims about comfort or performance. |

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

## Physics

Everything loose is a real mass obeying Newton's laws, tuned to stay comfortable in VR. The numbers live in one file, `Scripts/Runtime/World/RealPhysics.cs`.

- **Real weights.** You are a suited person of about 150 kg. Salvage weighs what it would: the rubber duck 0.1 kg, the nav board 0.6 kg, the toaster 1.8 kg, the hull plate 3 kg, the comms relay 3.5 kg, the copper cable spool 6 kg and the reactor regulator core 40 kg. Junk cans are 0.35 kg, junk crates 5 kg, and cut panels about 18 kg per square metre of face (4 to 30 kg).
- **Materials.** Bare metal (hull plate, cable spool, toaster), painted metal (relay, reactor core, panels, cans), rubber and plastic (duck, crates) and glass and ceramic (circuit boards) each have their own friction and bounce. Rubber bounces most, glass least.
- **Near-vacuum.** Things keep drifting and spinning, losing only about 2% of their speed and 4% of their spin per second. Every throwable uses continuous collision detection, so even a 25 m/s throw does not pass through a wall or the locker. Objects that start out overlapping ease apart at no more than 1 m/s instead of shooting off.
- **Smooth at 72 to 90 Hz.** Physics runs at the Quest's base 72 Hz and every loose body is interpolated, so held and thrown things do not judder at 90 Hz either.
- **Every push pushes back.** A throw kicks you the other way in proportion to the object's mass (momentum is conserved, measured against how you were already moving). Shouldering a floating can costs you nothing, but shouldering the reactor core slows you right down. The suit thruster is a fixed force, so you accelerate more slowly while hauling something heavy. The camera is never rotated by any of this, and the speed caps are unchanged.
- **Weight in the hand.** Anything up to 2 kg follows your hand exactly. Heavier things trail it slightly and swing a little past it when you stop. The reactor core lags most, by several centimetres in a brisk move, and settles within about a second. A held object still collides with walls and other objects. If you pull your hand more than about 45 cm from where the object is stuck, your grip slips and the controller ticks. A throw keeps your hand's real speed and spin, up to what an arm could give that mass: about 14 m/s for the duck, 10 m/s for the relay and 4 m/s for the core.
- **Two hands on heavy things.** Anything over 2 kg can be held in both hands at once. The pair carries it as if it weighed a quarter as much, so it lags far less, and a throw made with both hands can be faster (as if one arm threw half the mass). Let go with one hand and the other keeps hold, back to the one-handed feel. Light things are unchanged.
- **The spider is solid.** Its body (legs included) knocks floating junk aside in proportion to its speed and to the junk's mass, instead of passing through it. Nothing can push it off its path. Something heavy thrown hard that hits it squarely (20 kg m/s or more, for example the 40 kg core at 4 m/s, but never a tossed can) makes it stagger for 0.5 to 1 second: it stops advancing and cannot catch anyone, but still sees, hears and keeps chasing. It cannot be staggered again for 6 seconds, nor while asleep or during a jumpscare. Only the machine that runs the creature decides this.
- **Delicate things crack.** When a fragile item (circuit boards, the relay, the toaster, the reactor core) takes a damaging knock, a few small shards fly off it with a glassy crack, tumble away and shrink to nothing within about a second. This is purely cosmetic. There is one shared pool of 12 shards with no colliders, so it costs almost nothing.
- **Contacts.** How loud a knock is, both to you and to the spider, depends on how hard it really was (the collision impulse) and on what the object is made of. A 40 kg core slamming into a wall is nearly twice as loud as the 3.5 kg relay at the same speed. The duck bumping a wall is not a noise the spider hears. A closing door gently pushes loose objects out of the doorway, to whichever side they are on, and waits until the doorway is clear. As before, it never closes on the player.

## Sound

- **Breathing and heartbeat.** Your breathing inside the helmet quickens and grows louder when you work hard (pulling, thrusting) and when you are afraid (the spider close by, or chasing you), and it settles back over several seconds once things calm down. A soft heartbeat fades in only when the spider is chasing or very near, and beats faster the more frightened you are. In the tug, with no creature awake, both stay calm. The logic is in `Scripts/Runtime/Player/SuitAudio.cs`.
- **Sound through walls.** When ship walls stand between you and the spider, its footsteps, hum and voice come through muffled and quieter, and they clear up smoothly when it comes into view. Each of those sounds checks its line to your head about five times a second against the ship's walls (crates and loose junk do not count). This only changes what you hear; how much noise the spider hears from you is unchanged (`Scripts/Runtime/Audio/SoundOcclusion.cs`).
- **Rooms that sound their size.** Twice a second the suit casts ten short rays from your head to the walls and smooths the result into a room size. A single reverb follows your head: a narrow duct gets a short, quiet tail and a big cargo hold a longer one (0.3 to 2.2 seconds), kept low so footsteps and warnings stay clear. Sounds inside your helmet (breathing, heartbeat) stay dry (`Scripts/Runtime/Audio/RoomAcoustics.cs`).

## Known limitations

- Controller-relative offsets (glove, grab point, wrist display, holsters) were set by reasoning, not by trying them on. Expect to tune them on the first headset session.
- Target is a sustained 72 FPS on Quest 3/3S; this has not been measured.
- Art is generated in-project: chamfered and lathed meshes with normal-mapped procedural textures. It is a clear step up from raw primitives but still programmer art; all audio is synthesized placeholder.
- Free-floating movement with momentum is more likely to cause motion sickness than teleporting. This has not been comfort-tested on a headset; the drift toggle and teleport are there for that reason.
- Audio uses Unity's built-in 3D panning, no spatializer plugin.
- Held tools are kinematic and can poke through walls; held salvage is physical and cannot.
- The creature passes over loose junk rather than steering around it, and its long limbs can clip through door frames.
- **The spiders' feet are planted.** Each foot stays fixed on the floor, wall or ceiling while the body moves over it, and when the body has moved on, the leg lifts and steps to a new spot just ahead, found on the real surface; the legs step in two alternating groups of four, as real spiders do, and faster when it runs. Each leg is bent by simple inverse kinematics so the knee rises above the body and the tip touches the surface; asleep, hanging on its thread or in the jumpscare the legs go back to their own poses. Only one point under each foot is checked when it steps, so a foot can still overhang the edge of a crate or a door frame.
- The speed needed for a swat is set by reasoning and tests, not by trying it on.
- The jumpscare is deliberately intense (sudden darkness, a strobing red light, loud shrieks, something very close to the face). There is no setting to soften it yet, and none of it has been judged in a headset. It could be too strong for some players, especially anyone sensitive to flashing light.
- The game is darker than before and stages scares (distant bangs, groans, scuttling, a breath behind you, flickering lights, a shape at the end of a corridor). None of this has been judged in a headset; it may be too much or too little.
- The spiders are built by a Blender script (`ModelSource/Spiders/build_spiders.py`; the `.blend` files are in the same folder and can be opened and edited). They are anatomically real and furred, but they are generated rather than hand-sculpted, and each has 27,000 to 65,000 triangles, which has not been measured for frame rate on a Quest. If a spider makes the game stutter, the hair density in that script is the thing to lower.
- The Mimic plays your voice out of the headset speakers, which the microphone can hear. The game ignores the microphone while the Mimic is talking, but echoes after it stops could still be picked up and repeated.
- Voice detection is untested on a headset. The game's own sounds coming out of the headset speakers may be picked up by the microphone; the detector tracks the background level to reduce this, but if the creature seems to hear things you did not say, switch MIC off.
- The first wreck was enlarged by stretching its plan 1.5 times: walls, doors and furniture are wider than before, and some angled props are slightly distorted. The two new wrecks were built at full size.
- The three creatures share one skeleton and one patrol route; they differ in senses, speed, size and look. Their balance has not been play-tested.
- Working hard (swimming, thrusting) uses oxygen up to 40% faster. Breathing and heartbeat audio are synthesized and may want tuning or an off switch.
- Hand tracking is implemented but experimental: the gesture thresholds and palm alignment are untested on a headset. Thumbstick-driven continuous locomotion is out of scope.
- Co-op is a first slice only: players on the same Wi-Fi can see each other in the tug, nothing else is shared, and no contract can start while a crew is linked. It has never been run on two real headsets. See `Docs/MULTIPLAYER_NOTES.md`.

## Online play (groundwork, not in the game yet)

Co-op over the internet ("play anywhere") is being added next to same-Wi-Fi co-op. The code is in `Assets/TrashAstronauts/Scripts/Runtime/Net/Online/` (its own assembly, `TrashAstronauts.Online`); the entry point is `OnlineLink` (its header comment shows how a lobby board uses it).

- It uses **Unity Relay** to carry the game's own datagrams between headsets and **Unity Lobby** to list open games (`TUG 4821  2/4  IN HUB`, newest first, at most 5), both from Unity Gaming Services (package `com.unity.services.multiplayer` 2.4.1, on Unity Transport). It does not use Netcode for GameObjects. A host also gets a 6-character code friends can type if the list fails.
- Players sign in **anonymously** (no account, no name, no email). Unity gives the device a random player id.
- It contacts Unity's servers **only after you choose to host or find an online game**. A player who never does that never initialises Unity Services; an automated test checks this in the real game scene.
- No microphone audio is sent, online or on Wi-Fi: the game's network messages carry poses, items and game events only.
- **Not yet wired to the in-game board**, so this build cannot start an online game yet. It has **not been run on a headset**.
- Tested on a Mac only: one automated test signs in two anonymous players, hosts a game through Relay, finds it in the Lobby list, joins it, and runs the game's real host and client sessions over it (handshake, 200 reliable messages each way, avatar snapshots both ways). It passed against the live service. Measured over Relay with DTLS encryption, one datagram can carry 1315 bytes; the game's datagrams are at most 1200.
- Relay drops a connection that stays silent for a while, so a headset that sleeps for long may lose an online game where it would keep a Wi-Fi one; this is untested.

## Privacy

The game is single-player and works without any network. The app does hold Android's network (INTERNET)
permission, because of the co-op preview, but it **opens a network connection only after you press HOST
CO-OP, FIND CREW or ENTER CODE** on the tug's CO-OP CREW panel, and closes it when you press LEAVE. It then
talks only to other headsets on the same local network: FIND CREW sends a small "is anyone hosting?" message
to the addresses of your local network (port 47800), and in a crew each headset sends the others (through the host) its head and
hand positions, finger curl, handedness, a few suit states (thruster, flashlight, cutter) and a fingerprint
of the game version. There are no accounts, no servers and nothing goes to the internet. **No microphone
audio and no recorded phrase is ever sent**, by co-op or anything else.

If you allow microphone access, the microphone is normally read only to measure how loud you are, so the creature can react to your voice: up to one second of audio is held in memory at a time and overwritten.

**The Mimic is the exception.** Only while a Mimic is the creature aboard, the game also keeps the last four things you said (up to four seconds each) in memory, so that it can play them back to you. The wrist display shows **MIC REC** while this is happening. Those phrases are erased when the contract ends, when you switch MIC off, and when the app closes.

In every case the audio is never written to storage, never analysed for words, and never leaves the headset. Refusing the permission, or switching MIC off in the suit menu, disables this and nothing else. Because the game uses the microphone, Android also lists the audio-settings and Bluetooth permissions (they let a Bluetooth headset's microphone work); the game does nothing else with them.

Online play (see above; not in this build yet) will be the only part of the game that contacts the internet, and only after you choose to host or find an online game. It then signs in to Unity Gaming Services anonymously and sends game data, never audio, through Unity's Relay servers.

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

**5. Privacy.** The Game has no accounts and no online services, and it does not collect or share
personal data. It uses the network only for its optional co-op preview, only after you press HOST CO-OP,
FIND CREW or ENTER CODE, and then only with other headsets on your local network: it sends the position of
your head and hands, finger curl, handedness, a few suit states and a game-version fingerprint, never
microphone audio or recordings. Progress (credits, the oxygen upgrade and comfort settings)
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

## Rebuilding the spider models

The four spiders are made in Blender (tested with 5.4) by one script. To change them, edit the species table at the top of the script (sizes, leg length, fur density and length, colours, markings), then run:

```bash
/Applications/Blender.app/Contents/MacOS/Blender -b --factory-startup -P ModelSource/Spiders/build_spiders.py
```

and then **Trash Astronauts > Build Everything** in Unity. The script writes a `.blend` per species (to look at or edit by hand), plus the mesh data and colour texture the Unity generator loads. Species: JANITOR-7 is the tarantula, the Listener the cave spider, the Warden the hunter, the Mimic the widow.

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

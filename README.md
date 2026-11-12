# Trash Astronauts

A VR salvage game for standalone Meta Quest (Quest 3 / 3S, tracked controllers). It is a
single-player game with an early co-op preview: 2 to 4 headsets, on the same Wi-Fi or online, form a crew,
see each other in the tug and take a shared contract together (see **Co-op** below). Nothing goes over the
network unless you press one of the co-op buttons. You are an underpaid astronaut: board a derelict hauler from your salvage tug, float
through it by pushing off the walls and swimming, cut open panels, haul junk back to the tug's
collection locker, and get out before your oxygen runs out or the thing that used to be the ship's
janitor finds you.

## Download

**[Download TrashAstronauts.apk](https://github.com/veilbrowser-git/trash-astronauts/raw/builds/TrashAstronauts.apk)** (version 0.12.0, about 58 MB, Meta Quest 3 / 3S)

That one file is the whole game. To install it:

1. Turn on **Developer Mode** for your headset in the Meta Horizon phone app (required for any app that is not from the Meta store).
2. Connect the headset to a computer by USB and install the APK with [Meta Quest Developer Hub](https://developers.meta.com/horizon/documentation/unity/ts-mqdh/), SideQuest, or `adb install -r TrashAstronauts.apk`.
3. In the headset, open the app library and look under **Unknown Sources**.

Version 0.12.0 finishes the co-op basics. **Every player can grab, carry, throw, catch and deposit salvage**: the item is in your hand the moment you grab it, and when one player lets go another can take it. **Picking things up is easier** everywhere: the grab reaches a little past your fingertips, and an open empty hand pointed at salvage within about a metre highlights it; squeeze and it comes to your palm. A **helmet lamp** is for sale in the company store (250 cr): a wide soft light with its own battery, switched from the wrist or by tapping the side of your helmet, and visible to your crew. A flashlight that could relight during the jumpscare when caught right as a chase began now stays dark as intended; the jumpscare is otherwise unchanged. Everything from 0.11.x is included (online play, shared contracts, the co-op spider, voice chat, visible tools, suit colours). The new parts have been tested with simulated players and controllers only.

Version 0.11.4 lets you **see what your crew is doing** and **choose your suit colour**. Other players' tools now show in their hands or in the holsters on their suits; a crew member's flashlight has a visible beam and really lights what it points at (for the nearest one or two); their cutter shows its flame and sparks and you watch the weld burn down; dart shots draw a tracer. A SUIT LOCKER panel in the supply bay offers twelve suit colours; the choice is saved and your crew sees it. Only the host can carry salvage in this version. Not tried on headsets yet.

Version 0.11.3 adds **proximity voice chat** in co-op: you hear each crew member from their helmet, clear when close, fading with distance and muffled through walls. Your voice is sent compressed, only while you speak and only while you are in a crew, to the players in that crew (through the host, and through Unity's relay when online); nothing is recorded. VOICE CHAT on the suit settings panel turns it off; with MIC off nothing is sent but you still hear the others. A small light on a helmet shows who is talking and your wrist shows MIC while you transmit. Tested with synthetic voices only: echo, loudness and delay on real headsets are unknown.

Version 0.11.2 puts **the spider into co-op contracts**: one spider for the whole crew, simulated on the host and seen by everyone. It can hunt any player, hears every player's noise, and anyone's tranquilizer works on it. If it catches someone, that player gets the jumpscare (unchanged), the others watch it happen to that player's suit, and the contract fails for everyone. Blackouts are shared too. If one player's online connection drops, the host's game now carries on without them. Only the host can carry salvage in this version. The shared spider has been tested with simulated players only, not on two headsets.

Version 0.11.1 fixes what an independent review of the co-op code found: a dropped online connection is now noticed at once (LINK LOST) instead of after a minute; after a failed run everyone returns to their own spot in the tug rather than on top of the host; leaving on the results screen no longer leaves that contract's salvage frozen; other players and salvage are smoother online; slowly drifting salvage no longer jumps; door and weld requests are limited by distance and rate. No new features.

Version 0.11.0 lets a crew **play a contract together, on the same Wi-Fi or online from anywhere**. The CO-OP CREW panel gains **HOST ONLINE** and **FIND ONLINE** (through Unity's relay, with a silent anonymous sign-in; nothing is contacted until you press one). Once the crew is together, START CONTRACT on the mission board starts it for everyone: the same wreck and salvage, one oxygen tank, shared doors, cut panels and locker, everyone aboard to extract, and everyone paid. In this version **only the host can carry salvage** and there is **no spider in a co-op contract** yet; voice chat is not in yet either. Same-Wi-Fi joining was confirmed on two real headsets with 0.10.0; online play and the shared contract have only been tested from a computer.

Version 0.10.0 is the **first co-op build**, and it is deliberately small: on the same Wi-Fi, one headset presses **HOST CO-OP** on the new CO-OP CREW panel in the tug, the others press **FIND CREW** and **JOIN** (or ENTER CODE), and up to four players **see each other** in the tug as suited astronauts with moving heads, arms and gloved hands, each in their own colour. Contracts cannot be started while a crew link is open yet; the shared contract comes next, and online play after that. The game now asks for the network permission, and contacts nothing until one of those buttons is pressed. It has never been tried on two real headsets: whether they find each other on your network is exactly what this build is for.

The source in this repository has moved on from the 0.10.0 download: crews can now also form **online**, and the host can start a **shared contract** that the whole crew rides along in (see **Co-op** below). No build of that has been published yet.

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

Version 0.5.0 lets the creature hear your real voice through the headset microphone, adds two new wrecks (three in all, one picked at random per contract) and makes the original about 1.5 times bigger, adds a flight deck to the tug, and re-synthesizes every sound effect. It asks for microphone permission the first time; the microphone is only measured for loudness on the headset, nothing is recorded, and it can be switched off (MIC in the suit menu). (Since co-op voice chat, your voice is sent to your crew while you talk in a co-op game; see **Privacy**.) It has not been run on a headset yet.

Version 0.4.0 put one of three different creatures aboard at random each contract, turned the hands to a natural handshake pose so tools are held by their handles, made swimming stronger and added a wrist thruster and a brake, and added trick-shot bonuses, a hidden squeaky rubber duck, throw recoil, breathing and a heartbeat.

Version 0.3.0 moved the grab point into the palm, made swimming work with open hands, made hands and walls solid, quieted the creature and gave it a jumpscare, and added a shop, a tranquilizer, random blackouts, a map art pass and experimental hand tracking. Versions 0.2.0 and 0.3.0 were played on a Quest by the project owner.

> **Verification status, read this first.** The project compiles, the scene and all assets are
> generated, 635 automated tests pass in the Unity editor on macOS, and a Quest APK builds
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
| Pick up from a distance | Point an **open, empty hand** at salvage up to about 1.2 m away (it glows and a faint line runs to it from your palm) and squeeze **grip**: it travels into your palm in a fraction of a second, heavy things more slowly. Nothing comes through a wall, out of someone else's hand or while you hold a tool. The direct grab is more forgiving too: it reaches a little past the fingertips and under the palm, and when it touches several things it takes the one nearest the palm. In the Moon cave, salvage stops against the fallen rocks along the walls instead of sliding inside them. |
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
| Shop | The **COMPANY STORE** panel on the tug wall: tranquilizer darts (each puts the creature to sleep for about 8 seconds), a scanner range upgrade, a cutter coolant upgrade and a **HELMET LAMP** (250 credits, bought once and kept). Closed during a contract. |
| Helmet lamp | Once bought: a wide, soft light on the right of your helmet that follows your head and leaves both hands free. Switch it with the yellow **LAMP** button on your wrist display, or by tapping the side of your helmet with a fingertip. It has its own battery that drains only on a contract outside the tug, recharges in the tug (or slowly while off), dims and stutters when low, and is shown on the wrist next to the flashlight's note. It stays on in a blackout, as the flashlight does, and goes dark with the flashlight when the creature strikes. Crew mates see it glowing with a soft cone. |
| Hand tracking (experimental) | Put the controllers down. **Close your fist** = grip, **pinch thumb and index** = trigger, **left palm toward your face + pinch** = suit menu. Teleport, snap turn and the brake need a thumbstick, so with bare hands you move by pushing off, swimming and the thruster (point and pinch). Written without a headset to test on. |
| Draw / recall a tool | Grip at the **chest** (flashlight in the middle, tranquilizer launcher beside it) or **hips** (cutter on your main-hand side, scanner on the other). Gripping an empty holster pulls the tool to your hand from anywhere. Dropped tools reel themselves back after a few seconds. |
| Press buttons | Touch with a fingertip, **or** point at a button and pull the trigger (up to 3.5 m). |
| Suit menu | **B / Y** (or the left menu button): height +/-, snap-turn angle, left/right hand, drift on/off, unstuck, recall tools. The same panel is on the tug's rear wall. |

## Co-op (preview): same Wi-Fi or online

2 to 4 headsets form a crew in the tug. Everyone sees everyone's suit, helmet, head and both hands (fingers
included), each suit in its own colour (by default orange is the host, then blue, green and magenta; see **Suit colours** below), and **the host
can start a real contract that the whole crew rides along in**.

The **CO-OP CREW** panel is on the tug wall opposite the mission board, between the collection locker and
the practice plate. Its upper row of buttons is for the same Wi-Fi, the lower row for online play, and
**LEAVE** (beside both rows) ends either kind.

**On the same Wi-Fi**

1. On one headset press **HOST CO-OP**. The panel shows the tug number and the crew code, for example
   `TUG 4821` and `C 001  D 042`.
2. On each other headset press **FIND CREW**. Tugs that answer are listed with a **JOIN** button each;
   press **JOIN** next to the right tug number.
3. If FIND CREW finds nobody, press **ENTER CODE**, type the host's crew code on the keypad (C is filled in
   for you; usually only D needs typing) and press **JOIN**.

Some routers stop devices on the same Wi-Fi from seeing each other (called client or AP isolation; common on
guest networks), and then neither FIND CREW nor ENTER CODE can work: use online play instead.

**Online (anywhere with internet)**

1. On one headset press **HOST ONLINE**. After a few seconds (signing in, opening a connection) the panel
   shows `HOSTING  TUG 4821  ONLINE` and a 6-character code.
2. On each other headset press **FIND ONLINE**. Up to five open games of the same game version are listed,
   for example `TUG 4821  1/4  IN HUB`, each with a **JOIN** button; press **JOIN** next to the host's tug
   number. Full crews and crews out on a contract cannot be joined.
3. There is no keypad for the 6-character code on the panel (36 letter and digit keys do not fit it at a
   size a gloved hand can hit), so online games are joined from the FIND ONLINE list. The code is shown so a
   later version can use it.

If something goes wrong the panel says what, in words (for example `NO INTERNET`, `SERVICE BUSY - TRY
AGAIN`, `GAME GONE`, `CREW FULL`).

**The shared contract.** Once the crew is together, START CONTRACT on the host's mission board launches a
contract for everyone (on another headset START asks the host). Shared: the wreck and the salvage (the same
items with the same values everywhere; the host's game moves them and everyone sees them where the host has
them), one oxygen tank (the host's size; it drains while anyone who is not away is outside the tug), doors,
cut panels (anyone's cutter counts; the host opens the panel), the collection locker, and the contract
clock. EXTRACT works only when everyone who is not away is inside the tug; otherwise the board says who is
missing, for example `NOT ABOARD: CREW 2, 3`. When the contract ends everyone is brought back to the tug
and **everyone is paid the full payout into their own save**. Shop, settings and the crew record stay on
each headset.

**The spider is shared.** There is **one spider for the whole crew** (never the Mimic in co-op), and the
blackouts come with it. It lives on the host's headset and everyone sees the same spider in the same
place. It can hunt **anyone**: noises any player makes draw it, and the hunted player's suit buzzes and their
wrist says `DRONE HUNTING YOU` while the others' wrists say who it is after (`DRONE HUNTING CREW 2`).
**Anyone's tranquilizer works on it.** **If it catches one of you, the contract fails for everyone**: the
caught player sees the usual jumpscare, the others see it happen to that player's suit, and everyone is
brought home with nothing banked. Webs are not strung in co-op contracts.

**Suit colours.** Pick the colour of your suit at the **SUIT LOCKER** in the supply bay, next to the company
store: press one of twelve colour swatches (or **CREW COLOUR** to go back to your crew slot's colour) and
your stripes, a light wash over the suit fabric and the bands on your gloves change at once. The choice is
saved with your profile and kept between sessions. In co-op the rest of the crew sees your colour too (also
if you change it in the middle of a session), and the CO-OP CREW panel names everyone by their colour.

**Seeing each other's tools.** Each crew member's suit shows its four tools on the chest and hips, and a tool
someone draws appears in that glove as it sits in their own hand. A crew mate's flashlight shows a lit lens and
a soft beam (the nearest one or two also really light the walls), their plasma cutter shows its flame, glow and
sparks with its sound coming from their hand, a weld they are burning visibly heats up on everyone's headset
before it gives, and everyone sees the trail of the host's tranquilizer darts and of any other crew member's dart that hits
the spider (a crew member's miss is seen only by that player). These copies
are only for looking at: they cannot be grabbed, collide or cut anything, and finishing a weld still goes
through the host.

**Voice chat.** In a crew you can talk to each other. A crew member's voice comes from their helmet:
clear up close, quieter with distance (silent beyond about 18 m) and muffled through walls. A small teal
light on top of their helmet glows while they talk, and your own wrist shows `MIC` while your voice is going
out. **VOICE CHAT** in the suit settings switches it on or off (on by default; off means you send nothing and
hear nobody); **MIC** off means the game uses no microphone at all. Your microphone also hears your friends
from your speakers; the game tries not to send that back or let the spider hear it as you, so talking very
quietly while someone else talks can be cut until they stop. The spider hears your real voice wherever you
are, on the host's headset or your own. Sound quality is like a suit radio, and nothing is recorded.

**Everyone grabs.** Every crew member can grab, carry, hand over, throw and deposit salvage, and the item
is in your hand the moment you close it, without waiting for the host. If two of you reach for the same
item at once, whoever reached the host first gets it, and the other hand gives two short buzzes and comes
away empty. Nobody can take an item out of someone else's hand, but once they let go or throw it anyone
can grab or catch it, and the host still decides every deposit and trick shot. If a crew member's headset
goes to sleep or they leave while holding something, it is dropped where they last had it and anyone can
pick it up. An item in a crew member's hand or still flying from their throw cannot knock the spider off
balance until it settles.

**Not in this version:** there are no webs in a co-op contract. The tutorial pauses while a crew is linked.

**Pauses and lost links.** If a crew member takes the headset off, they count as away (not waited for at
extraction, not using oxygen). If the host pauses, everyone's board shows `HOST PAUSED`. If nothing is
heard from the host for a second and a half, the boards show `HOST NOT RESPONDING`; after 60 seconds (or
if the host leaves) the others are put back to playing alone and a contract that was running is abandoned
with nothing banked. If the host presses LEAVE during a contract, the contract is abandoned for everyone.
Nobody can join while a contract is running.

All headsets need the same game version; a different version is refused with `DIFFERENT VERSION`, on Wi-Fi
and online alike. Co-op has not been tried with two real headsets yet (see **What was and was not
tested**).

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
| **Automated tests, rules and content (EditMode, 349)** | Pass. New for online play and the shared contract: the online module (sign-in, Relay and Lobby steps, every service error mapped to board words, the lobby listing rules) against fake services, and a scene-less copy of the shared contract's books that stays identical to the host's after every message, with every message applied twice changing nothing. New for the co-op preview: the crew stand-in has the helmeted suit, the modelled gloves and a door-blocking body but no camera, listener, interactor, input, light or tool/salvage layer; the scene has exactly one offline network session and a CO-OP CREW panel clear of the other tug furniture; the Android build keeps the INTERNET permission, and the build check fails a manifest without it. New in 0.9.1: the cave stays within its triangle and renderer budget with its lights registered; ship walls and floors have normal maps, decoration has no colliders and stays within a per-ship budget. New in 0.9.0: the glove model has its named parts, stays in budget, is not inside-out, and both hands wear it the right way round. New in 0.8.3: the networking core for co-op (message formats, reliable delivery under packet loss, joining and item ownership rules), tested without opening a real connection to anyone. New in 0.8.2: the Blender suit export has every named part, stays within its triangle budget and is not inside-out. Second 0.6.0 build: a chasing creature that has lost sight follows sounds and can be pointed at a better guess; damaged salvage is worth less, never recovers, and is safe once in the locker. New in 0.6.0: speech is cut into phrases correctly (short noises dropped, long speech capped, only the last four kept, all forgotten on clear) and the Mimic prefab is the only creature that records. From 0.5.0: the voice detector (silence and steady background noise are ignored, a whisper is safe, talking is heard, shouting carries further, a single click is ignored), and all three wrecks being complete, inside the playable volume and always able to pay the quota for 300 seeds each. From 0.4.0: trick-shot bonus rules, the three creature prefabs and their different senses, the handshake hand pose with tools held by their handles, and the rubber duck. Includes the shop rules, the cutter coolant upgrade and the hand-tracking gesture maths. Mission outcomes, duplicate-deposit prevention, bank-exactly-once, failure keeps old credits, restart cleanup, save/load including missing and corrupt files, oxygen upgrade, loot generation over hundreds of seeds, drone state machine, noise thresholds, cutter heat. |
| **Automated tests, real scene (PlayMode, 286)** | Pass. New for everyone grabbing, the helmet lamp and easier pickup: the owner's bug both ways (the host lets go and a crew member grabs and carries it, then the crew member lets go and the host's controller grabs it), two crew members grabbing one item in the same instant get one grant and one refusal, a crew member's throw into the locker from range scores the trick shot and everyone hears the deposit, an item held by someone who goes away or leaves is dropped where they had it and can be taken, a whole contract over real sockets in which a crew member does all the collecting and depositing; the helmet lamp (store, switch, battery, blackout, jumpscare, crew mates' view); the reach assist (floor pickups in ship and cave, never through walls or out of a hand, and as a co-op client it asks the host like any grab); a catch right after a chase starts keeps the torch dark through the jumpscare. New for suit colours and crew tools: a joiner sees each crew member's chosen colour (suit and gloves) from the first frame the suit is shown, a colour picked at the SUIT LOCKER mid-session reaches the crew at once and the panel names crew by colour; crew members' tools, flashlight beams, cutter flames and sparks and weld progress show on everyone's headset, and a crew member's dart that hits the spider is drawn for the others; tool copies keep their own colours and the voice talk light stays on the helmet. New for voice chat: it switches itself on with every host or client session and is gone after LEAVE or when the host closes (single-player has none of it); a simulated crew member's voice plays from its helmet with the talk light on, and the host's voice reaches it, with MIC shown on the wrist while sending; VOICE CHAT off sends nothing; with three players a client's voice is relayed to the other client and never back; a client's own voice reaches the host's spider as that player, but a crew voice leaking from the speakers does not; the same both ways over real sockets on 127.0.0.1 (the voice codec, jitter buffer, echo suppression and 3D playback have their own tests with synthetic voices). New for the shared spider: the scene hosts and a simulated headset sees the spider's samples (10 a second) and state changes, its noise makes the real spider investigate and its dart sedates it; the spider catches its suit, the catch names it, the run fails for everyone and the end of the run reaches it even after a silence during the jumpscare; a caught player who leaves mid-jumpscare keeps their suit until it ends. As a client: the host's samples move the spider, each state message gives one cue, only the hunted player's suit buzzes and the others' wrists say who is hunted, being caught plays this player's own jumpscare and the run ends only when the host says so, and someone else being caught plays against their suit with no visor crack here. A whole contract over real sockets has the spider aboard from start to extraction. A crew member whose Relay connection drops is let go at once while the online game stays open and listed. New after review: a dropped Relay connection or dead allocation ends a client's game at once and closes and unlists a host's game with LINK LOST (simulated connection); a pause from before joining becomes Away and goes back to a pause on leaving, with nothing left stuck; the online buttons wait for a cancelled online step to finish; leaving on the results screen leaves every item grabbable; a player brought home lands on their own crew spot; door and weld requests from far away or too fast are ignored; a slowly drifting item is not declared at rest and a rested item that creeps away is streamed again; the panel reads the lobby listing live; the stripping list (link.xml) names real assemblies. New for the shared contract: the scene hosts and a simulated headset's START starts the real contract, which it hears about (contract, world state, clock and shared oxygen twice a second, item positions); its door and panel requests open the real door and panel and each change is sent once; its EXTRACT is refused naming it while its head is outside the tug and ends the run when it is aboard, with the same payout on both sides; a second contract starts from the results screen; nobody can join mid-contract; the host's pause is sent. The scene joins a simulated host: START on its board asks the host, the host's contract is rebuilt with the same salvage ids, items follow the host's stream and settle where the host says, a deposit lands in the host's locker slot, EXTRACT shows who is not aboard, the end of the run pays this headset's save once and brings the player home, HOST PAUSED and HOST NOT RESPONDING are shown, and 60 s of silence (on a test clock) abandons the contract with nothing banked. A whole contract over real sockets on 127.0.0.1: a simulated headset rides along while salvage goes into the host's locker, then extraction pays both. New for online play: HOST ONLINE, FIND ONLINE with a JOIN per game, and LEAVE on the panel through a stand-in online link, every online problem shown in words, a different version refused with the same words as on Wi-Fi, a request cancelled by LEAVE or by the scene closing has its connection closed when it arrives; and one test against Unity's live servers (category Online; it reports Inconclusive instead of failing when the service cannot be reached): the scene hosts through its own panel, a second simulated player finds it in the real Lobby list, joins through Relay, sees the crew and the start of a contract, and LEAVE closes every Relay connection. It passed from a Mac. A whole offline contract still opens no socket, no Relay connection and never asks Unity's services to start. New for the co-op preview: the scene hosts and simulated headsets join over an in-memory network; their suits appear in their slot colours, follow the stream about 100 ms behind, curl their fingers, go away after silence and disappear on LEAVE or a timeout; the scene joins a simulated host, takes slot 1 and sees the host and another joiner, and LEAVE makes it solo again; the same over real sockets on 127.0.0.1; the panel's HOST CO-OP, FIND CREW with JOIN, ENTER CODE keypad and LEAVE buttons work and every refusal is shown in words; a whole offline contract opens no socket. Co-op has not been tried on two real headsets. New in 0.9.3: on all four species planted feet slide less than 2 cm and stay on the surface on floor, wall and ceiling, the two leg groups are never in the air together, and the foot placement is off while dormant and in the jumpscare; breathing and heartbeat rise and settle smoothly and are calm in the tug; a wall between you and a sound muffles it; a small and a large room get different reverb; fixture lenses follow their lamps through dead lights and blackouts. New in 0.9.2: two hands hold a heavy item and it lags less, one hand can let go without dropping it; a 40 kg core thrown at 4 m/s staggers the spider once and not again inside the cooldown, a can does nothing, and it cannot be staggered asleep or mid-jumpscare; it knocks a floating crate ahead of it; delicate items shed at most twelve shards that fade; the cave thruster lifts you well clear of the ground. New in 0.9.1: in the cave a released item falls and rests on the floor while in a ship it keeps drifting, the player comes to rest on the ground and can leave it with the thruster, the tug stays weightless, and the spider patrols the cave. New in 0.9.0: thirteen tests that run real physics (nothing tunnels through a wall at 25 m/s, recoil conserves momentum, thrust scales with load, heavy items lag then catch up, held items stop at a wall and the grip slips, throw speed limits by mass, doors clear loose items) and six on the gloves (finger parts on both hands, rest pose, trigger moves only the index finger, fingers stop on a held object). New in 0.8.3: no web is strung across a route on any contract, infested or not, and the cocoons are gone; co-op groundwork seams behave identically offline. New in 0.8.2: the player has a suit with a visible torso and a hidden helmet, the torso follows the head, the arms reach the hands and stay on the shoulders, every holstered tool is still visible past the body, and the crew colour can be set. New in 0.8.1: the spider's footsteps play from its body and are silent while it is dormant or lying in wait, it stops dead on its rounds and a noise ends the wait; some lights are dead, different ones per contract, never the airlock's, and stay dead through a blackout; the flashlight drains, goes out when flat, refuses to light, recovers when off and is recharged by the tug. New in 0.8.0: all four spiders rest on the ceiling when dormant, stand on the floor (not floating), climb the wall standing sideways, and cross to the ceiling upside down, with their hit volume following the body; a hunting spider comes down and still catches the player; a web tripwire slows the player, raises a noise and draws the spider, burning one is silent, and an infested contract strings them all; conditions change oxygen, values, bonus and wake time and appear on the board; spiderlings wander and bolt; a door behind the player slams; the best haul is remembered. The jumpscare test now also checks the glimpse, at least three bites, the visor cracking and the blackout ending. New in 0.7.0: the shop is a 3D stall in the supply bay (part of the safe tug), still sells, folds to a small crate when a contract starts and unfolds afterwards; a slow reach does not knock salvage away but a fast open-hand swing bats it and drifting into it pushes it, without moving or slowing the player; the dropping spider appears at arm's length and leaves, harmlessly. Second 0.6.0 build: the jumpscare kills every light first, holds the creature back unseen, then puts it within half a metre of the player's eyes and squarely in view for over a second, and restores the lights afterwards; the charging fake-out gets close but never reaches or harms the player; a player whose head is 3 m from their play-space centre still starts inside the lobby, and one found outside it before a contract is put back; the creature leaves its route to run down a player standing in the middle of a big room without passing through anything, is faster than the thruster and rises toward a player floating high; the wrist noise meter shows impacts and voice and fades; delicate salvage loses value when banged and the locker pays what is left; every staged scare can be triggered, the lights come back, and the glimpse vanishes when looked at. New in 0.6.0: the Mimic answers with the phrase it was given, from its own position, lower and slower; the microphone ignores the Mimic's voice; phrases are kept only while a Mimic is aboard and are forgotten when the contract ends or MIC is switched off; it goes quiet in a blackout and when tranquilized. These use injected test phrases, not a real microphone. From 0.5.0: one wreck docked per contract, chosen by the run's seed, with the previous one cleaned up; in every wreck all loot spots, panels and recovery points are reachable by teleport, the creature completes its whole patrol without passing through walls or entering the tug or the sealed shortcuts, the blackout takes out that wreck's lights and its sealed panels open; the creature hears talking but not a whisper, and not with MIC off (driven with a simulated microphone level, not a real microphone). From 0.4.0: one creature per contract chosen from three by the run's seed (all three turn up over 24 contracts), each creature wakes, patrols and hears with its own senses, the thruster pushes where the hand points and tops out, the brake stops you, a hand holding something does not thrust, trick shots pay only for real throws from range, throw recoil conserves momentum, and the duck is hidden every run, squeaks, and draws the creature. From 0.3.0: open-hand swimming at realistic stroke speeds, solid hands pushing off a wall, the shop, the tranquilizer putting the creature to sleep and it waking again, the blackout and its recovery, the jumpscare on being caught, and held items resting in the palm. Three of these drive the game through a simulated tracked controller with the real input bindings and grab interactor: the hand follows the controller, salvage can be picked up, carried and released, and the cutter can be drawn from its holster, fired and used to cut a panel open. Zero-g drift keeps momentum and is stopped by the hull, grabbing a wall pulls the body and letting go pushes off, swim strokes propel the player, the drift comfort option, the tutorial advancing on real actions and being replayable. Also: fresh launch state, settings and handedness, loot worth more than the quota, every spawn point and the locker reachable from a teleport landing spot, teleport and unstuck, locker counts each item once, cutting opens the reactor compartment and the core can be recovered, mounted salvage releases when cut, drone investigates noise and returns to patrol, drone never enters the tug/airlock/duct, drone catches an exposed player and the run fails without touching banked credits, hazard pause, oxygen warnings and failure, both extraction payouts, clean restart, oxygen upgrade persists and applies. |
| **Editor simulation** | Simulator enabled; not exercised by hand. The PlayMode tests call the same public methods the VR interactions call, most of them do **not** simulate controller input (the zero-g tests move the hand transforms directly). Grabbing and tool use are covered by the simulated-controller tests; button poking and how the floating feels are unverified. |
| **Android build** | Succeeds. `Builds/Quest/TrashAstronauts.apk` was built in batch mode; its manifest, ARM64 libraries, OpenXR loader and signature were inspected. It was not installed or launched on a device. |
| **Headset** | Co-op (same Wi-Fi and online, presence and the shared contract) has never been run on two real headsets: finding each other on a real network, Relay from a Quest, how the shared salvage looks while it moves, and what happens when a headset sleeps are all unconfirmed. Version 0.6.0 has not been run on a headset: the Mimic has never recorded or played back a real voice. Version 0.5.0 has not been run on a headset by me either: the microphone, the two new wrecks, the enlarged first wreck, the flight deck and the new sound effects are all unheard and unseen in VR. The sound effects were checked numerically (levels, clipping, loop seams), not by ear. Version 0.4.0 has not been run on a headset by me either. Version 0.3.0 was played on a Quest by the project owner, who reported that tools were still held oddly, that moving around was hard and that the game needed more to do; 0.4.0 is the response, but the new hand pose, thruster feel and creature balance are set by reasoning and by tests, not by trying them on. Version 0.2.0 was played on a Quest by the project owner: grabbing worked but held items sat out at the fingers, and swimming did nothing. Both are fixed in 0.3.0 (the swim bug was a rest threshold that wiped out the small speed each frame of a stroke adds). Version 0.3.0 itself has not been run on a headset yet, and hand tracking has never been run on one. Earlier: version 0.1.0 was played on a Quest by the project owner: it launched and tracked, but nothing could be grabbed, so tools and cutting were unusable. That was a bug in the grab detector's setup and is fixed in 0.2.0, verified with the simulated-controller tests. Version 0.2.0 itself has not been run on a headset yet. No claims about comfort or performance. |

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
- Co-op is a preview: a crew (same Wi-Fi or online) can share a contract, with one shared spider and everyone carrying salvage, but there are no webs in a co-op contract. Nobody can join a crew that is out on a contract. It has never been run on two real headsets. See `Docs/MULTIPLAYER_NOTES.md`.

## Online play: how it works

The online buttons are in `Assets/TrashAstronauts/Scripts/Runtime/Net/Online/` (its own assembly, `TrashAstronauts.Online`); the entry point is `OnlineLink`, which the CO-OP CREW panel drives through `NetSession`.

- It uses **Unity Relay** to carry the game's own datagrams between headsets and **Unity Lobby** to list open games (`TUG 4821  2/4  IN HUB`, newest first, at most 5), both from Unity Gaming Services (package `com.unity.services.multiplayer` 2.4.1, on Unity Transport). It does not use Netcode for GameObjects: the same host and client sessions as on Wi-Fi run over the Relay connection.
- Players sign in **anonymously** (no account, no name, no email). Unity gives the device a random player id.
- It contacts Unity's servers **only after you press HOST ONLINE or FIND ONLINE**. Automated tests in the editor check that a player who never does that never asks Unity Services to start, never initialises them and opens no Relay connection while playing a whole contract in the real game scene. This has not been verified by capturing network traffic on a headset. LEAVE closes the Relay connection and removes a hosted game from the list.
- The lobby entry carries the tug number, the crew count, whether the crew is in the hub, on the results screen or on a contract, and a fingerprint of the game version (so different versions never see each other). The Relay join code is readable only by players who join the lobby.
- Voice chat goes the same way as every other co-op message: online, your compressed voice passes through Unity's Relay servers while you talk (see **Privacy**). Nothing else from the microphone is sent, and nothing is recorded.
- Tested on a Mac only, against the live service: the game scene hosts through its own panel, a second simulated player finds it in the Lobby list, joins through Relay, sees the crew and the start of a contract, and everything closes on LEAVE. It has **not been run on a headset**. Measured over Relay with DTLS encryption, one datagram can carry 1315 bytes; the game's datagrams are at most 1200.
- Relay severs a connection that sends nothing for 10 seconds (the game pings it every 2 seconds while running, but a headset that is taken off or asleep sends nothing). An online game therefore does not survive a pause much longer than that, where a Wi-Fi one survives 45 seconds. When it happens the game notices at once: a player whose own connection dropped is told `LINK LOST` and plays on alone instead of waiting 60 seconds; the host lets that one player go at once and keeps the game open and listed for the rest; a host whose own connection died closes the game and takes it off the list. This has been tested with a simulated connection, not on a headset.
- Release builds keep the Unity Services code the online buttons need from being stripped (`Assets/TrashAstronauts/link.xml`); whether anything else is missing in a stripped Quest build can only be confirmed on a device.
- Over the internet, other players' suits and the shared salvage are drawn 190 ms in the past (100 ms on Wi-Fi) to smooth out uneven delays.

## Privacy

The game is single-player and works without any network. The app does hold Android's network (INTERNET)
permission, because of the co-op preview, but it **opens a network connection only after you press one of
the buttons on the tug's CO-OP CREW panel**, and closes it when you press LEAVE.

- **Same Wi-Fi (HOST CO-OP, FIND CREW, ENTER CODE):** the game talks only to other headsets on your local
  network. FIND CREW sends a small "is anyone hosting?" message to the addresses of your local network
  (port 47800). Nothing goes to the internet.
- **Online (HOST ONLINE, FIND ONLINE):** only after you press one of these, the game signs in to Unity
  Gaming Services **anonymously** (Unity assigns the device a random player id; no account, name or email),
  lists or reads open games in **Unity Lobby** (tug number, crew count, hub/contract state and a version
  fingerprint) and sends the crew's game data through **Unity Relay** servers. Like any internet service,
  Unity necessarily sees this device's **IP address** while it does so. Anyone who looks up the game list can
  also see the anonymous player ids of a listed game's members (the lobby shows them; they are random and
  carry no name). Unity's handling of this data is covered by Unity's own privacy policy. The Unity Services SDK may keep its sign-in token on the device
  for next time; the game itself stores nothing about it.

In a crew, each headset sends the others (through the host) its head and hand positions, finger curl,
handedness, a few suit states (thruster, flashlight, cutter, away) and a fingerprint of the game version,
and the host sends the contract (wreck, salvage, positions of the items it moves, oxygen, doors, panels,
locker, results). Your save, credits and settings are never sent.

**Voice chat.** In a crew (and only in a crew), while you talk, your voice is compressed (about 6 to 7.5 kB
per second of talking) and sent to the other players in your crew: through the host's headset, and in an
online game also through Unity's Relay servers. When you are quiet nothing is sent. It is played from your
suit's helmet on their headsets and is **not recorded or stored** anywhere, on either end. **MIC off** (the
game uses no microphone at all) or **VOICE CHAT off** (in the suit settings) stops it; with VOICE CHAT off you
also hear nobody. Phrases recorded for the Mimic are never sent. In single-player nothing of voice chat
exists.

If you allow microphone access, the microphone is normally read only to measure how loud you are, so the creature can react to your voice: up to one second of audio is held in memory at a time and overwritten.

**The Mimic is the exception.** Only while a Mimic is the creature aboard, the game also keeps the last four things you said (up to four seconds each) in memory, so that it can play them back to you. The wrist display shows **MIC REC** while this is happening. Those phrases are erased when the contract ends, when you switch MIC off, and when the app closes.

In every case the audio is never written to storage and never analysed for words, and outside voice chat in a co-op crew it never leaves the headset. Refusing the permission, or switching MIC off in the suit menu, disables this and nothing else. Because the game uses the microphone, Android also lists the audio-settings and Bluetooth permissions (they let a Bluetooth headset's microphone work); the game does nothing else with them.

Online co-op is the only part of the game that contacts the internet, and only after you press HOST ONLINE or FIND ONLINE.

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

**5. Privacy and online services.** The Game has no accounts of its own and does not collect personal
data. It uses the network only for its optional co-op preview, and only after you press one of the buttons
on the CO-OP CREW panel. Same-Wi-Fi co-op talks only to other headsets on your local network. Online co-op
(HOST ONLINE, FIND ONLINE) uses third-party services, Unity Gaming Services (anonymous sign-in, Lobby and
Relay), which receive your device's IP address (as any internet service does), a random player id assigned
by Unity, the listing of a hosted game and the game data exchanged with your crew; other players looking up
the game list can see the random player ids of a listed game's members. Their use is subject to Unity's
terms and privacy policy. Co-op sends the position
of your head and hands, finger curl, handedness, a few suit states, a game-version fingerprint and the
shared contract's state and, while you talk in a crew with voice chat on, your compressed voice (to the
players in your crew only, through the host and, online, Unity's Relay; never recorded or stored; MIC off or
VOICE CHAT off stops it). It never sends Mimic recordings, your save or your settings. Online services may be
unavailable, changed or withdrawn at any time. Progress (credits, the oxygen upgrade and comfort settings)
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

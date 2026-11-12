# Preparing for co-op (2–4 players)

**Status (co-op stage 1):** the first co-op slice is in: on the same Wi-Fi, one headset presses HOST CO-OP
on the tug's CO-OP CREW panel, the others FIND CREW (or ENTER CODE) and JOIN, and everyone sees everyone's
suit, head and hands move in the tug. Nothing else is shared yet and no contract can start while a crew is
linked. The code is a small UDP netcode of our own (`Scripts/Core/Net`, adapters in `Scripts/Runtime/Net`,
no networking package). A socket is opened only after one of those buttons is pressed; single-player opens
none. The design and the remaining stages are in the co-op implementation plan; the notes below are the
original seam survey and are kept for reference until the full rewrite planned for the last stage.

## Boundaries that already exist

| Concern | Lives in | Notes |
|---|---|---|
| Mission rules: salvage ledger, panels, oxygen, outcome, payout | `Core/MissionSession.cs` | Pure C#, no scene, input or headset access. Unit tested. |
| Loot placement | `Core/SalvageSpawnPlanner.cs` | Deterministic from a seed. |
| Drone decisions | `Core/EnemyBrain.cs` | Pure state machine fed a percept each tick. |
| Persistent profile | `Core/SaveSystem.cs` | Per-player; stays local. |
| Scene authority | `Runtime/Mission/MissionDirector.cs` | The only thing that mutates the session. |
| Local input, haptics, hands, HUD | `Runtime/Player/*`, `Runtime/UI/WristDisplay.cs` | Presentation only. |

Every shared-state change goes through one method on `MissionDirector`:
`StartMission`, `NotifySalvageCollected`, `DepositSalvage`, `RequestOpenPanel`, `Extract`,
`FailMission`, `PurchaseOxygenUpgrade`. Each takes a `PlayerId`. World objects receive a
`PlayerContext` / `PlayerId` with the interaction (grab, cut, button press) instead of looking up a
global player. Mission objects have stable ids: `SalvageItem.SalvageId` (`run<N>/<spawnPointId>`),
`Cuttable.panelId`, `SlidingDoor.doorId`, `SalvageSpawnPoint.pointId`.

## Where networking would have to be introduced

1. **Host authority.** The host owns the only writable `MissionSession`. The seven director methods
   above become requests from clients (RPCs) that the host validates and then broadcasts as events
   (`SalvageDeposited`, `PanelOpened`, `MissionStarted`, `MissionEnded`). The duplicate-deposit and
   bank-once guards already live in the session, so they would run on the host unchanged.
2. **Shared mission state.** Replicate: phase, run index, seed, quota, deposited value/count, the set of
   open panel ids, per-item state. Because loot placement is deterministic, sending the seed and run
   index is enough for clients to spawn identical salvage with identical ids. Late join would need a
   snapshot of item states and open panels.
3. **Oxygen.** `MissionSession` currently holds one tank and `MissionDirector.Update` treats "everyone
   in the tug" as safe. Co-op needs a design decision (per-player tanks are the likely answer) and a
   per-player tank keyed by `PlayerId`; failure rules for one player running out need defining.
4. **Object ownership and grab sync.** Salvage, junk, cut plates and tools are local rigidbodies driven
   by XRI's `XRGrabInteractable`. Co-op needs network transforms with ownership transfer on grab
   (`SalvageItem.OnGrabbed` is the hook), authority hand-off on throw, and conflict rules when two
   players grab the same item. Tools are per-player and can stay owner-authoritative.
5. **Cutting.** Per-weld progress (`CutPoint.Progress`) is local today; only "panel opened" goes through
   the director. For two players cutting one seam, weld progress must be replicated or made
   host-authoritative.
6. **Drone.** `EnemyBrain` should tick on the host only. `EnemyDrone.Perceive` already loops over all
   registered `PlayerContext`s and picks a target, but line-of-sight uses local physics, so the host
   needs every player's head position. Clients would receive state + position and run only the
   presentation half of `EnemyDrone` (light, glyph, audio).
7. **Noise.** `NoiseSystem.Emit` is the single choke point; client noises would be forwarded to the host.
8. **Doors.** `SlidingDoor.SetOpen` needs to become a host-validated request.
9. **Remote players.** A remote avatar needs a `PlayerContext` without local input: head and hand poses
   streamed in, no `HandRig` input, no haptics. `PlayerContext.From(interactor)` assumes interactors sit
   under the player's hierarchy, which holds for remote proxies too if they are built the same way.
10. **Extraction and failure.** Rules for "one player caught", "one player still in the wreck at
    extraction" and payout splitting are undefined and need design before code.

## Known single-player assumptions to revisit

- `MissionDirector.Active` is a scene singleton (one mission per scene; fine for co-op).
- `MissionDirector.FindPlayer` falls back to the first player when an id is unknown.
- `SettingsPanel`, `TutorialController`, `DevHotkeys` and `WristMenu` use the first/local player.
- `Camera.main` is used for billboards and scan tags (correct: those are local presentation).
- The save file is a single local profile.

## What multiplayer still needs beyond these seams

Choosing a transport and netcode stack, lobby/session flow, voice, reconnection, latency handling for
physics hand-offs, per-platform entitlement checks, and a full round of multi-headset testing. None of
that exists here, and the seams above are untested against a real network.

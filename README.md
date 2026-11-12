# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.13.0, built from commit c66d7ea on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `a3d4b55d1b610f76760665419c7a52019f2891c205c4f292072748e501a02451`
- 0.13.0: every map 1.5 times bigger; in co-op a caught player is downed and can be revived instead of the contract ending; you can shove other players; cutting is now dragging the flame along a seam; the Mimic is in co-op; a salvage claw mini-game in the supply bay earns a little credit. Tested with simulated players only.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

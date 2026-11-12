# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.11.1, built from commit a6765e6 on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `d9f72b7e65cb447590fb2b960b7a46da1ffe1cf2f7d92e3ac45b7d8cbeafec9c`
- 0.11.1: fixes from a review of the co-op code. A dropped online connection shows LINK LOST at once; after a failed run everyone returns to their own spot; leaving on the results screen no longer freezes salvage; smoother players and salvage online; door and weld requests limited by distance and rate. Same features as 0.11.0.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

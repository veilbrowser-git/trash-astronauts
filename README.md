# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.10.0, built from commit 1cb121e on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `51619a8f992e9114a184a1b003df345d735be523fd6f8fdf8f505c9c683f0f15`
- 0.10.0: first co-op build. On the same Wi-Fi one headset presses HOST CO-OP, the others FIND CREW and JOIN, and up to four players see each other as suited astronauts in the tug. Contracts are not shared yet. The game now requests network permission and contacts nothing until one of those buttons is pressed. Never tried on two real headsets.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.1.0, built from commit 9d67edd on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `373f9a0d903194d03862b9efa26e303bc9de393cf53aa160fa7c9107e45e60a2`
- Not yet tested on a headset

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

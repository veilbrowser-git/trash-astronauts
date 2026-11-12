# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.4.0, built from commit 5770ac0 on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `492268d3fb0327fbcc8c74c4b48a9dcca1cde5f0918c4d0dd6a0257bc6e8410b`
- 0.4.0: a random creature each contract (three kinds), natural hand pose with tools held by their handles, stronger swimming, wrist thruster and brake, trick shots, a hidden rubber duck, throw recoil, breathing and heartbeat. Not yet run on a headset.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

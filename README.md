# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.6.0 (build 2), built from commit 8880478 on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `78f361b91e4448adeaa714df475f8ebfa447839553b031cd9a2b8b6942aecda8`
- 0.6.0 build 2: realistic furred spiders modelled in Blender (four species), creatures that actually hunt you down, a fix for spawning outside the lobby, a far more intense jumpscare, a noise meter on the wrist, delicate salvage that loses value when banged, staged scares. Also: the Mimic (plays your voice back), voice detection, three wrecks. Not yet run on a headset.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

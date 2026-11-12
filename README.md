# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.5.0, built from commit b3789cf on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `21ffd44a92897f8ab8746a77c3c75ef2814f8ff29d0217af28bfedd82d1035b3`
- 0.5.0: the creature can hear your real voice (asks for microphone permission; loudness only, nothing recorded or sent; MIC off in the suit menu), three wrecks picked at random, maps about 1.5x bigger, a flight deck on the tug, all sound effects redone. Not yet run on a headset.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

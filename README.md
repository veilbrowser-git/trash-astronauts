# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.6.0, built from commit 5b4eb4b on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `67b72d684a0fa84fc8980bdb11ec4058cb9ede6ed7d52e7328452cb9718ad0c5`
- 0.6.0: adds the Mimic, a fourth creature that plays your own voice back at you (phrases are held in memory only while it is aboard, never saved or sent). Also in this build from 0.5.0: the creature hears your voice (asks for microphone permission; MIC off in the suit menu), three wrecks picked at random, bigger maps, a flight deck, all sound effects redone. Not yet run on a headset.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

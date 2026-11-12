# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.8.1, built from commit c654e79 on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `aa935cd3b8f7aa1d50b41262f6f9a97e1b8155a1ae6c5bd5afa3b7aa7ee36920`
- 0.8.1: you can hear the spider walking and it sometimes stops dead to listen; some of each wreck's lights are dead or failing; the flashlight has a battery that the tug recharges; muffled helmet sound; far fewer webs, cocoons and tripwires; the wrist inventory is gone. The jumpscare is unchanged from 0.8.0. Not yet run on a headset.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.9.1, built from commit 78cada0 on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `0041cfdfe03d7c200ec8eab4796e92ed624a8b2932902b0b2b38ddc5dc85ca24`
- 0.9.1: a fourth map, the Lava Tube Outpost, a mining camp in a Moon cave with lunar gravity; the three ships redecorated (riveted panels, tread-plate floors, pipes, light fixtures, dust, a planet outside); rounder gloves. Includes the 0.9.0 physics. Not yet run on a headset.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

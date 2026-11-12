# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.8.0, built from commit 676fef3 on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `332c12238701eb5405463626a818a6475312c72b3e3e711d6ae6327cede82401`
- 0.8.0: the spiders walk on the floor, walls and ceiling instead of floating; a harsher jumpscare that cracks your visor; web tripwires that give you away; swarms of small spiders; doors that slam; a random condition on each contract; a best-haul record. Also everything from 0.7.0 (wrist inventory, supply bay, folding 3D shop). Not yet run on a headset.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

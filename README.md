# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.9.0, built from commit 575a50c on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `e8b871901cd6c59fbbdfb4bf1f5f11cd1fe8455d9fa26b050d501934726ac081`
- 0.9.0: realistic space-suit gloves with fingers that bend and close around what you hold; real masses for every object, heavy things lag in the hand, held items stop at walls, throwing or shoving something heavy pushes you back, slower thrust under load, doors ease objects aside. Not yet run on a headset.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

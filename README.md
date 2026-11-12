# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.7.0, built from commit 930a565 on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `f97a3926beacfe717ea1193b965beed0aab8a721bc0403308774f16c0c7a0bb0`
- 0.7.0: a wrist inventory (touch INV on the wrist display), a bigger lobby with a supply bay, a 3D shop that folds into a crate when a contract starts, webs and cocoons in the wrecks, a spider that drops on a thread in front of you, and hands and body that can push loose things. Also everything from 0.6.0: realistic Blender spiders that hunt you, the big jumpscare, the Mimic, voice detection, three wrecks. Not yet run on a headset.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

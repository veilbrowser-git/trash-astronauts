# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.8.2, built from commit a20fa5f on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `f9d1feaa606c055b5b5abf674f70a62bb30c48599e58ff0f7e539ddf1a0e3258`
- 0.8.2: the player now has a head and a body, a space suit modelled in Blender (helmet, torso with a life-support pack, arms that follow your hands, legs). You see your own body but not your own helmet. Co-op is not in this build. Not yet run on a headset.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

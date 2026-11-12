# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.8.3, built from commit b5f6a8d on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `8ac0ae74676a8e4c2371006be9b15335c6d2e85a5dc3ed47b673e9e6af56dc81`
- 0.8.3: the webs strung across corridors and the hanging cocoons are gone; only the corner webs remain. Infested contracts now mean livelier small spiders instead of more webs. Not yet run on a headset.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

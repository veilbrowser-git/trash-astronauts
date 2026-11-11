# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.2.0, built from commit ed493c4 on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `82576a6926b819265e20ab8fae4f1470c09638d27982a7e574b8e46b38790399`
- 0.2.0 fixes grabbing and cutting, which did not work in 0.1.0. Not yet run on a headset.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

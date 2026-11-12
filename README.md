# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.4.0, built from commit 5963e91 on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `70fe552ca6378ccb78c4f5b4079d22d45e64d26082400bbfcd3f9b05781166e4`
- 0.4.0: palm grab, working swimming, solid hands, shop, tranquilizer, blackouts, jumpscare, experimental hand tracking. Not yet run on a headset.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

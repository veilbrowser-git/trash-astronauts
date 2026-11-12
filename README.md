# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.9.3, built from commit 3038566 on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `89d7d58f9d442e5a39c2a1df218dacd5b93b3886fb0974ea9bba949f8c14224d`
- 0.9.3: the spider's feet are planted and it steps with a real gait on floors, walls and ceilings; breathing and a heartbeat follow effort and fear; sounds muffle through walls and rooms echo by size; light fixtures go dark with their lamps. Includes everything in 0.9.0 to 0.9.2. Not yet run on a headset.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

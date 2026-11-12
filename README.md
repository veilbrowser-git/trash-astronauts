# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.11.2, built from commit d9bb8ab on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `8fb6d96cc538e1e4f3d4322970c17c489d5a0b491cd76dbd6b298a01b48d1172`
- 0.11.2: the spider is now in co-op contracts: one for the whole crew, it can hunt anyone, anyone's tranquilizer works on it, and being caught fails the contract for everyone. Shared blackouts. A dropped online player no longer closes the host's game. Only the host can carry salvage. Tested with simulated players only.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

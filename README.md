# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.11.0, built from commit 3e1c62b on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `de2ca342034964cbb2feb7854aba85d833cb19e451e6bfffe9c5f7af10e5969c`
- 0.11.0: play a contract together on the same Wi-Fi or online from anywhere (HOST ONLINE / FIND ONLINE on the CO-OP CREW panel). Shared wreck, salvage, oxygen, doors, panels, locker and payout. Only the host can carry salvage and there is no spider in co-op contracts yet. Online play and the shared contract are tested from a computer only.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

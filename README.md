# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.11.3, built from commit 0e74e05 on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `5a108b48a04833ffe80bff3436e5691ddc8f5110d221309da643dcb05d66f28e`
- 0.11.3: proximity voice chat in co-op. You hear crew members from their helmets, fading with distance and muffled by walls, on Wi-Fi and online. Voice is sent only while you speak and only to your crew; VOICE CHAT or MIC off stops it. Includes the co-op spider from 0.11.2. Only the host can carry salvage. Voice tested with synthetic audio only.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

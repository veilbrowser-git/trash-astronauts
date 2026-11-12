# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.11.4, built from commit 3017cca on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `ec73bedbe2280b73058772583fcb26f2222c32970408330d6bb81db01002f068`
- 0.11.4: other players' tools show in their hands or holsters, their flashlights have beams that light the walls, their cutters show flame and sparks and welds burn down as they cut; a Suit Locker in the supply bay offers twelve suit colours that your crew sees. Includes voice chat and the co-op spider. Only the host can carry salvage. Not tried on headsets.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.12.0, built from commit 5d276b7 on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `c868298dd02f7c8ce940a323baa43e311c890bd39a799ee3862c0029b1786c85`
- 0.12.0: every player can grab, carry, throw, catch and deposit salvage; picking up is easier (larger grab reach and a short-range pull to the palm); a helmet lamp is for sale in the company store. Includes online play, shared contracts, the co-op spider, voice chat, visible crew tools and suit colours. New parts tested with simulated players only.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

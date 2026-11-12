# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.12.1, built from commit 9f2d514 on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `406006e914d9c22994bf3291eddffd1bf57e7fd20cc4275759c148c9af04c7b1`
- 0.12.1: fixes for 0.12.0. The pickup pull no longer steals rail or wall grabs or swim strokes, and thrown salvage keeps flying forward instead of jumping or sliding back when the host takes it over. Same features as 0.12.0.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

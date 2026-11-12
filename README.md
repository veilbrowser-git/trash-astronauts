# Trash Astronauts - Quest builds

This branch holds only the built APK, so the `main` branch stays source-only.

- `TrashAstronauts.apk` - version 0.9.2, built from commit 903843a on `main`
- Meta Quest 3 / 3S, ARM64, OpenXR, minimum Android API 32
- Signed with the Android debug key (fine for sideloading, not for a store)
- SHA-256: `a241ddc9fba661112f8da9ca6c967467e4c340eda25b909359aa9355eb3109ff`
- 0.9.2: carry heavy items with two hands; the spider knocks junk aside and flinches when hit hard with something heavy; delicate items crack; a real thruster hop in the Moon cave; more ship wall detail. Includes the 0.9.1 cave and interiors and the 0.9.0 gloves and physics. Not yet run on a headset.

Install with developer mode on and the headset connected by USB:

    adb install -r TrashAstronauts.apk

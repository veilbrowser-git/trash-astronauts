# Asset licenses

| Asset | Source | License |
|---|---|---|
| All sound effects (`Assets/TrashAstronauts/Generated/Audio/*.wav`) | Synthesized by `Scripts/Editor/Builder.Audio.cs` in this project | Original work, part of this project |
| All textures (`Assets/TrashAstronauts/Generated/Textures/*.png`) | Generated procedurally by `Scripts/Editor/Builder.Core.cs` | Original work, part of this project |
| All models, props, gloves, tools, drone | Built from Unity primitives by the builder | Original work, part of this project |
| `Assets/TextMesh Pro/*` (font LiberationSans SDF, shaders, settings) | TextMesh Pro Essential Resources shipped inside Unity's `com.unity.ugui` package | Unity Companion License; Liberation Sans is under the SIL Open Font License 1.1 |
| `Assets/Samples/XR Interaction Toolkit/XR Interaction Simulator/*` | Sample shipped inside `com.unity.xr.interaction.toolkit` 3.6.1 | Unity Companion License |
| `Assets/Settings/*` (URP assets) | Unity's URP Blank project template | Unity Companion License |

No third-party art, audio, fonts or code were downloaded for this project.

## Packages for online play

Online play (`Scripts/Runtime/Net/Online/`) adds these Unity packages through `Packages/manifest.json`. They are fetched by the Unity Package Manager from Unity's registry and are not copied into the project.

| Package | Version | License |
|---|---|---|
| `com.unity.services.multiplayer` (Relay, Lobby) | 2.4.1 | Unity Terms of Service; its third-party notices list Newtonsoft.Json (MIT) and unity-websocket-webgl (Apache 2.0) |
| `com.unity.services.core` | 1.18.0 | Unity Terms of Service |
| `com.unity.services.authentication` | 3.8.0 | Unity Terms of Service; notices list Newtonsoft.Json (MIT) and moq4 (BSD 3-Clause) |
| `com.unity.services.qos` | 1.5.1 | Unity Terms of Service |
| `com.unity.services.wire` | 1.6.1 | Unity Terms of Service; notices list websocket-sharp (MIT) and unity-websocket-webgl (Apache 2.0) |
| `com.unity.services.deployment`, `com.unity.services.deployment.api` (editor tooling pulled in by the above) | 1.7.2, 1.1.3 | Unity Terms of Service |
| `com.unity.nuget.newtonsoft-json` | 3.2.2 | Newtonsoft.Json and its Unity forks, MIT |
| `com.unity.transport` (built into Unity 6.6) | 6.6.0 | Unity Companion License |

Using Relay and Lobby is also subject to the Unity Gaming Services terms for the cloud project the game is linked to.

## Spider models (version 0.6.0)

The four spider creatures are original work generated for this project by `ModelSource/Spiders/build_spiders.py` running in Blender. No third-party models, scans or textures are used; the colour textures are painted by the same script.

# Suit colours

## For the README

Every player can pick the colour of their suit at the SUIT LOCKER in the supply bay, next to the company store: press one of twelve colour swatches (or CREW COLOUR to go back to your crew slot's colour) and your stripes, a light wash over the suit fabric and the bands on your gloves change at once. The choice is saved with your profile and kept between sessions. In co-op the rest of the crew sees your colour too.

## How it fits together (for the co-op code)

- Palette: `PlayerAvatar.SuitColors` / `SuitColorNames` (12; the four crew colours first). Saved as `SaveData.suitColor`: 0 = the slot's colour (default, and what old saves load as), 1..12 = palette entry `suitColor - 1`. The network uses the same value (`Core.Net.SuitLook`).
- Wire: the colour rides in bits 4..7 of the handedness byte of Hello, SettingsChanged and RosterChanged (bit 0 is still the handedness) and in bits 4..7 of each slot's byte of the Welcome roster. `NetProtocol.Version` is 2, so builds without it are refused with VersionMismatch.
- Sessions: `NetHostConfig.suitColour`, `NetClientConfig.suitColour`, `NetSessionHost.SuitColour(slot)` / `SetLocalLook(handedness, colour)`, `NetSessionClient.SuitColour(slot)` / `LocalSuitColour` / `LocalHandedness` / `SendSettingsChanged(handedness, colour)`, `WelcomeMsg.SuitColour(slot)` / `SetSlot(..., suitColour)`. The `IHostWorld` / `IClientWorld` signatures are unchanged; a world may also implement the optional `ICrewLookListener.OnCrewLook(slot, colour)`, which the sessions call after the matching join / settings / Welcome / roster callback (never for the local slot).
- Look: `Net.CrewLook.ApplyLook(PlayerContext | RemoteAvatar | suit + gloves, slot, colour)` resolves "default by slot" against a chosen colour; `CrewLook.SavedColour(director)`, `CrewLook.ColourOf(net, slot)`, `CrewLook.CrewName(net, slot)`.
- Until the lines below are added, `Net.CrewLookSync` (on the SUIT LOCKER) does the job by comparing once a frame: it dresses the local suit, tells the session the saved colour when it differs, and dresses every remote stand-in. With the lines added it finds nothing to do and can stay or go.

## Hookup lines (files owned by the co-op work, not edited here)

`Scripts/Runtime/Net/NetSession.cs`

- `StartHosting`, in `new NetHostConfig { ... }` after `handedness = LocalHandedness(),`:
  `suitColour = CrewLook.SavedColour(director),`
- `StartClient`, in `new NetClientConfig { ... }` after `handedness = LocalHandedness(),`:
  `suitColour = CrewLook.SavedColour(director),`
- `ApplyLocalSlot`, replace the `local.avatar.SetTint(...)` line with:
  `CrewLook.ApplyLook(local, slot, CrewLook.SavedColour(director));`
- `AddCrew`, after the `if (crew == null ...) { ... } else if (crew != null) { ... }` block:
  `if (crew != null) CrewLook.ApplyLook(crew, slot, CrewLook.ColourOf(this, slot));`
- `SetCrewHandedness`, after `crew.SetHandedness(handedness);`:
  `if (crew != null) CrewLook.ApplyLook(crew, slot, CrewLook.ColourOf(this, slot));`
- New method, called by `SuitLockerPanel.Choose` (or keep CrewLookSync):
  `public void ReportLookChanged(byte suitColour) { byte h = LocalHandedness(); if (m_Client != null) m_Client.SendSettingsChanged(h, suitColour); if (m_Host != null) m_Host.SetLocalLook(h, suitColour); }`

`Scripts/Runtime/Net/RemoteAvatar.cs`

- `Setup`, replace `suit.SetTint(PlayerAvatar.CrewColors[...])` with `CrewLook.ApplyLook(this, id.Value, SuitLook.Default);` (the gloves then take the slot colour too; `using TrashAstronauts.Core.Net;` is already there).

`Scripts/Runtime/UI/NetLobbyBoard.cs` (the crew list)

- `RosterLine`: `line += "  " + ColourNames[slot];` becomes `line += "  " + CrewLook.CrewName(session, slot);`
- The JOINED status: `"\nYOUR SUIT IS " + ColourNames[...]` becomes `"\nYOUR SUIT IS " + CrewLook.CrewName(session, session.LocalSlot)`

Optional instead of the `AddCrew` / `SetCrewHandedness` lines: let `SceneHostWorld` and `SceneClientWorld` implement `ICrewLookListener` with `public void OnCrewLook(byte slot, byte suitColour) { var crew = m_Net.Crew(slot); if (crew != null) CrewLook.ApplyLook(crew, slot, suitColour); }`.

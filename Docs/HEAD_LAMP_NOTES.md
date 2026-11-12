# Helmet lamp

For the README (three sentences):

The company store sells a HELMET LAMP for 250 credits, bought once and kept in your save: a wide, soft light in the lamp housing on the right of your helmet that follows your head and leaves both hands free. Switch it with the yellow LAMP button that appears on your wrist display, or by tapping the side of your helmet with a fingertip; it has its own battery that drains only on a contract outside the tug, recharges in the tug (or slowly while off), dims and stutters when low, and is shown on the wrist next to the flashlight's note. Crew mates see your lamp glowing with a soft cone, and it goes dark with the flashlight when the creature strikes; it stays on in a blackout, as the flashlight does.

## For whoever integrates

- `AvatarSnapshot.flags` now carries all eight bits: bit 0x80 is `AvatarFlags.HeadLampOn`. The message is still 35 bytes, but an older build masks that bit off, so bump `NetProtocol.Version` when this goes in.
- Remote helmet lamps and remote flashlights share one budget of real lights (`RemoteToolRig.MaxRealLights`, nearest first, with the same hysteresis). The rest show only a lens glow and a cone.
- While the lamp is lit, the wearer's own suit and glove meshes are on layer 18 `LocalSuit`, which the lamp does not light, so it never glares on them. A player who never lit a lamp is not changed at all.
- Previews 63-65a: the store with the lamp, the corridor lit only by the lamp (with the flashlight for comparison), and a crew mate's lamp.

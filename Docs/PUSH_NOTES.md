# Crew shoves

## For the README (three sentences)

In co-op you can shove a crew mate by swinging an empty hand (open or a fist) into their suit, or nudge them along with a slow press; they drift away and you drift back a little, as two people in zero-g would, and you both feel a thud. A shove can give the other player at most 1.5 m/s and the pusher at most 0.8 m/s back, works inside the tug and (along the ground) in the Moon cave, and never goes through walls. If being pushed around is not for you, turn CREW SHOVES off in the suit settings: then you neither shove nor get shoved.

## How it works

- `Scripts/Runtime/Net/CrewShove.cs` sits next to the NetSession (added by `CoopToolSync.Attach` when the first crew stand-in appears, removed when the session closes). On the pusher's headset it tests each free hand against a few spheres and capsules worked out from the stand-in's posed suit (helmet, torso, backpack, upper arms; plain maths, no colliders and no new layer). Hand speed relative to the other player >= 1.0 m/s is a shove (each suit gets (1 + 0.3) / 2 of the closing speed), slower down to 0.08 m/s is a press (half the closing speed). Both hands within 0.05 s add up; one target once per 0.35 s.
- `Scripts/Core/Net/CrewPushMsg.cs` is stream message 41 (sender, target, sequence, velocity change in mm/s, contact point in cm, shove flag; 17 bytes). Every shove is written on 3 consecutive send ticks with the same sequence and de-duplicated on arrival (`CrewPushDedup`).
- The host (`NetSessionHost.CrewPush.cs`) checks a client's shove: both players connected and not Away, heads within 2 m, at most 4 a second per sender, velocity clamped to 1.5 m/s, and the game's own check (`CrewPushAllowed`: neither player downed). It then raises `CrewPushReceived` (the host is the target) or passes it on to the target client only.
- The target's headset applies it with `ZeroGLocomotion.AddImpulse` (so the usual 3.6 m/s cap holds and the camera never turns), with a haptic bump on both controllers and a low thud. Under gravity the vertical part is dropped.
- Downed players: `Scripts/Runtime/Player/CrewDowned.cs`. Set `CrewDowned.Check = p => p.IsDowned` when the revive feature is merged (until then a public bool `PlayerContext.IsDowned` is picked up by reflection, otherwise nobody is downed).
- CREW SHOVES is `SaveData.crewShoves` (default on), a button beside VOICE CHAT on the tug's settings console and the wrist menu.

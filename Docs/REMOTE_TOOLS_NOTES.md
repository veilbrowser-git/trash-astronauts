# Crew tools (notes for the README)

In co-op you now see what your crew mates are carrying: each suit shows its four tools on the chest and hips, and a tool someone draws appears in that glove exactly as it sits in their own hand.
A crew mate's flashlight shows a lit lens and a soft beam (the nearest one or two also really light the walls), their plasma cutter shows its flame, glow and sparks with its sound coming from their hand, and a weld they are burning visibly heats up on everyone's headset before it gives.
None of this exists in single-player, the copies are for looking at only (they cannot be grabbed, collide or cut anything), and finishing a weld still goes through the host exactly as before.

## Hookup notes (for whoever owns NetSession / SceneClientWorld)

- Weld progress needs no NetSession change: `CoopToolSync` (added beside the NetSession by the first crew stand-in) uses `HostSession` / `ClientSession` and their new `SendCutProgress(panel, index, progress)` and `CutProgressReceived` (stream id `CutProgressMsg.Id` = 40, defined in `Core/Net/CutProgressMsg.cs`, not in `NetMsg`). If NetSession should own it instead, see the report: subscribe where the session is created, unsubscribe where it is disposed, and send from LateUpdate.
- Tranquilizer tracer: the host already sends `TranqTracer` for its own darts. Clients show it once `SceneClientWorld.OnTranqTracer` calls `CoopToolSync.ShowTranqTracer(m_Net, by, from, to)`. A client's own darts have no message to the host yet (only hits are reported); the host could send `SendTranqTracer(slot, origin, hitPoint)` from `OnTranqHitReport` for hits.

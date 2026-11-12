namespace TrashAstronauts.Core.Net
{
    /// <summary>Why a client's session ended.</summary>
    public enum SessionEndReason : byte
    {
        None = 0,
        /// <summary>The local player pressed LEAVE.</summary>
        LocalLeave = 1,
        /// <summary>The host answered the Hello with Refuse (see NetSessionClient.LastRefusal).</summary>
        Refused = 2,
        /// <summary>No answer to Hello within 5 s.</summary>
        ConnectTimeout = 3,
        /// <summary>The host closed the session (SessionClosed).</summary>
        HostClosed = 4,
        /// <summary>The host dropped this client (45 s of silence or backlog overflow).</summary>
        Dropped = 5,
        /// <summary>Nothing from the host for 60 s.</summary>
        HostTimeout = 6,
        /// <summary>
        /// More than 512 of our own reliable requests were waiting for the host (it was unreachable for too long
        /// while we kept playing). One could not be queued, so the ordered stream would have had a hole: the
        /// client leaves instead, like the host drops a client in the same state.
        /// </summary>
        BacklogOverflow = 7,
    }

    /// <summary>
    /// Everything <see cref="NetSessionClient"/> tells the game, and the ONLY way it touches the game.
    /// Same id rules as <see cref="IHostWorld"/>. All methods are called from inside NetSessionClient.Poll (or
    /// ApplyRecorded during a tape replay), on the main thread. Every reliable callback arrives exactly once and
    /// in the order the host sent it; every apply must still be idempotent (the plan's mirror rule).
    /// The scene implementation is Runtime/Net/SceneClientWorld; BotClientWorld and FakeWorld.Client are the doubles.
    /// </summary>
    public interface IClientWorld
    {
        // ---------------------------------------------------------------- session

        /// <summary>
        /// First callback after joining: nothing else (no reliable message, no avatar, clock or pose) is
        /// delivered before it. Call MissionDirector.ResetForSession, take PlayerId(localSlot), create a proxy
        /// for every present slot other than your own, then apply the world state.
        /// </summary>
        void OnWelcome(byte localSlot, in WelcomeMsg welcome);

        /// <summary>A slot joined, left, went Away, came back or changed handedness. Never reports the local slot joining.</summary>
        void OnRosterChanged(byte slot, RosterChange change, byte handedness);

        /// <summary>Latest-value world state (after RunStart; idempotent).</summary>
        void OnWorldState(in WorldStateMsg state);

        /// <summary>The host's hazard pause went on or off.</summary>
        void OnHostPause(bool on);

        /// <summary>
        /// True after 1.5 s without a datagram from the host: show HOST PAUSED and freeze the local oxygen
        /// extrapolation and the creature replica. False when the host is heard again.
        /// </summary>
        void OnHostSilent(bool silent);

        /// <summary>
        /// The session is over (refused, connect timeout, host closed, dropped, 60 s host timeout). Call
        /// MissionDirector.LeaveSession. Not called for a local Leave().
        /// </summary>
        void OnSessionEnded(SessionEndReason reason);

        // ---------------------------------------------------------------- streams (unreliable)

        /// <summary>Another player's avatar (the host's own, or one relayed by the host). <paramref name="hostTime"/> is the host clock when it was sent; push into that proxy's SnapshotBuffer.</summary>
        void OnRemoteAvatar(byte slot, in AvatarSnapshot avatar, double hostTime);

        /// <summary>A creature sample, 10-20 Hz. Ignore while a jumpscare is playing.</summary>
        void OnCreatureState(in CreatureSnapshot creature, double hostTime);

        /// <summary>An item pose from whoever simulates it. The implementation asks ItemAuthority.AcceptPose (stale epochs and items simulated here are dropped).</summary>
        void OnItemPose(in ItemPoseEntry pose, double hostTime);

        /// <summary>Mission clock and shared oxygen, 2 Hz. MissionSession.ApplyClock.</summary>
        void OnClock(float elapsedSeconds, float oxygenRemainingSeconds, byte flags);

        // ---------------------------------------------------------------- run

        /// <summary>The host started a contract: MissionDirector.BeginRun(in parameters). The plan hash has NOT been checked; see RunStartCodec.IsPlanHashValid.</summary>
        void OnRunStart(in RunStartParams parameters);

        /// <summary>Answer to this client's ExtractRequest: these slots are not aboard (bit n = slot n).</summary>
        void OnExtractRefused(byte missingSlotsMask);

        /// <summary>The contract ended: MissionDirector.ApplyRunEnded.</summary>
        void OnRunEnded(in RunEndedMsg result);

        /// <summary>The host left the results screen.</summary>
        void OnReturnedToHub();

        // ---------------------------------------------------------------- items

        /// <summary>
        /// Who simulates an item now. Feed ItemAuthority.OnOwner and act on its event (Granted, Denied, Revoked,
        /// and GrantedNotHeld: send ItemReleased with ItemAuthority.WireEpoch at once).
        /// <paramref name="denied"/> marks the direct "no" to this client's own GrabRequest.
        /// </summary>
        void OnItemOwner(byte itemId, byte ownerSlot, byte epoch, bool held, bool denied);

        /// <summary>First pickup: MissionSession.ApplyRemoteCollect.</summary>
        void OnItemCollected(byte itemId, byte by);

        /// <summary>A host-simulated item settled here (heals a displaced copy).</summary>
        void OnItemRest(byte itemId, byte epoch, NetVec3 position, NetQuat rotation);

        /// <summary>MissionSession.ApplyRemoteValue.</summary>
        void OnItemValue(byte itemId, int value);

        /// <summary>The locker accepted the item: cancel any local selection, apply the mirror, ApplyAccepted with the same slot.</summary>
        void OnItemDeposited(byte itemId, byte by, int finalValue, int trickBonus, byte lockerSlot);

        /// <summary>A lost item was put back at this position.</summary>
        void OnItemRecovered(byte itemId, NetVec3 position);

        // ---------------------------------------------------------------- world

        /// <summary>SlidingDoor.ApplyReplicated(open).</summary>
        void OnDoorState(byte doorId, bool open);

        /// <summary>Cuttable.ApplyRemotePointCut(index).</summary>
        void OnPointCut(byte panel, byte index);

        /// <summary>Cuttable.ApplyOpened(impulse, torque) and the mirror's TryOpenPanel.</summary>
        void OnPanelOpened(byte panel, byte by, NetVec3 plateImpulse, NetVec3 plateTorque);

        // ---------------------------------------------------------------- creature and hazards (Stage 4)

        /// <summary>Presentation half of the creature's state change (eye light, hum, cues). Reliable, so never lost with snapshots.</summary>
        void OnCreatureStateChanged(EnemyState state, byte targetSlot);

        void OnTranquilized(float seconds);

        /// <summary>Run the local side of the jumpscare (victim effects only when victimSlot is the local slot).</summary>
        void OnCatchStarted(byte victimSlot);

        /// <summary>Another player's dart tracer (Stage 5).</summary>
        void OnTranqTracer(byte by, NetVec3 from, NetVec3 to);

        void OnBlackoutStarted(float seconds);

        void OnBlackoutEnded(bool instant);

        /// <summary>WebTripwire.ApplyState.</summary>
        void OnWireChanged(byte index, NetWireChange change, byte by);

        // ---------------------------------------------------------------- co-op revive (protocol 5)

        /// <summary>The crew member in <paramref name="slot"/> was caught and is downed at <paramref name="position"/> (the local slot: this player). After their jumpscare.</summary>
        void OnPlayerDowned(byte slot, NetVec3 position);

        /// <summary>The crew member in <paramref name="slot"/> is up again, revived by <paramref name="by"/> (255 = nobody named).</summary>
        void OnPlayerRevived(byte slot, byte by);

        /// <summary>How far a downed crew member's revive ring is (0..1) and who is holding it (255 = nobody).</summary>
        void OnReviveProgress(byte slot, float progress, byte rescuerSlot);
    }
}

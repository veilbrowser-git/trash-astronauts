namespace TrashAstronauts.Core.Net
{
    /// <summary>Why a client's slot was freed on the host.</summary>
    public enum PlayerLeaveReason : byte
    {
        /// <summary>The client pressed LEAVE (Bye).</summary>
        Bye = 0,
        /// <summary>45 s of silence.</summary>
        Timeout = 1,
        /// <summary>More than 512 reliable messages were waiting for it.</summary>
        BacklogOverflow = 2,
        /// <summary>The same address started a new join; the old connection was replaced.</summary>
        Replaced = 3,
        /// <summary>The host closed the session.</summary>
        HostClosed = 4,
    }

    /// <summary>Why the host took an item back from a client.</summary>
    public enum ItemReturnCause : byte
    {
        /// <summary>The owner sent ItemHandoff (the normal end of a throw). Pose and velocities are the owner's.</summary>
        Handoff = 0,
        /// <summary>
        /// Owned but not held for 3 s with no handoff, or "held" by a client that has not streamed it for 3 s
        /// (it does not know it holds it).
        /// </summary>
        Backstop = 1,
        OwnerAway = 2,
        OwnerLeft = 3,
        /// <summary>The host itself needed it (deposit, host closing the session).</summary>
        HostTookOver = 4,
    }

    /// <summary>State handed to <see cref="IHostWorld.OnItemReturned"/>.</summary>
    public struct ItemReturn
    {
        public byte itemId;
        /// <summary>The client slot that owned it.</summary>
        public byte fromSlot;
        public ItemReturnCause cause;
        /// <summary>False when the owner never streamed a pose: leave the item where the host last had it.</summary>
        public bool hasPose;
        public NetVec3 position;
        public NetQuat rotation;
        /// <summary>Zero except for <see cref="ItemReturnCause.Handoff"/>.</summary>
        public NetVec3 linearVelocity;
        public NetVec3 angularVelocity;
    }

    /// <summary>
    /// Everything <see cref="NetSessionHost"/> needs from the game, and the ONLY way it touches the game.
    /// World objects are named by the small numbers of NetIdTable (door, panel, wire indices) and by item net id
    /// (index in RunStartParams.items); players by session slot (0 = host, 1..3 = clients; PlayerId.Value == slot).
    ///
    /// All methods are called from inside NetSessionHost.Poll, on the main thread, never re-entrantly.
    /// Implementations may call the session's Send* methods from inside any of them.
    /// The scene implementation is Runtime/Net/SceneHostWorld; ScriptedHostWorld and FakeWorld.Host are the doubles.
    /// </summary>
    public interface IHostWorld
    {
        // ---------------------------------------------------------------- lobby

        /// <summary>
        /// The host's current mission phase. Read when a Probe is answered (HostInfo.phase) and when a Hello
        /// arrives: a join is accepted only in Hub or Results, otherwise it is refused ContractInProgress.
        /// </summary>
        MissionPhase Phase { get; }

        /// <summary>
        /// Called when a client's join is confirmed, to fill the WorldState carried in its Welcome (door and panel
        /// masks, done welds, wire states). <paramref name="state"/> arrives cleared.
        /// </summary>
        void FillWorldState(ref WorldStateMsg state);

        // ---------------------------------------------------------------- roster

        /// <summary>
        /// A client finished the handshake (its first session datagram arrived). Create its RemoteAvatar proxy
        /// with PlayerId(slot). Called once per join, after <see cref="FillWorldState"/> and after its Welcome
        /// has been queued: anything broadcast from inside this method reaches the joiner after its Welcome
        /// (and so survives the reset the joiner does on Welcome). The other clients are told RosterChanged(Joined)
        /// right after this returns.
        /// </summary>
        void OnPlayerJoined(byte slot, byte handedness);

        /// <summary>
        /// The slot is free again (Bye, 45 s timeout, backlog overflow, replaced, host closing). Destroy the proxy
        /// (not while a creature is scaring). Any item the client owned has already been returned through
        /// <see cref="OnItemReturned"/>.
        /// </summary>
        void OnPlayerLeft(byte slot, PlayerLeaveReason reason);

        /// <summary>
        /// The slot became Away or came back. Away = the client said so (PauseNotice or the avatar's Away flag) or
        /// the host heard nothing from it for 1.5 s. An Away player is not targeted and not counted for oxygen or
        /// extraction. Items it owned have already been returned through <see cref="OnItemReturned"/>.
        /// </summary>
        void OnPlayerAway(byte slot, bool away);

        /// <summary>The client changed handedness (SettingsChanged). The session relays it to the other clients.</summary>
        void OnPlayerSettings(byte slot, byte handedness);

        // ---------------------------------------------------------------- streams (unreliable, 20 Hz)

        /// <summary>
        /// A fresh avatar sample from a client. <paramref name="hostTime"/> is the host clock at arrival; push it
        /// into that proxy's SnapshotBuffer and keep the newest head position for gameplay tests.
        /// </summary>
        void OnAvatar(byte slot, in AvatarSnapshot avatar, double hostTime);

        /// <summary>
        /// A gameplay noise made by a client. Already rate-limited (8 per second) and clamped to the kind's maximum;
        /// the source is <paramref name="slot"/> whatever the client claimed. Feed NoiseSystem with PlayerId(slot).
        /// </summary>
        void OnNoise(byte slot, NetVec3 position, float loudness, NoiseKind kind);

        // ---------------------------------------------------------------- run (reliable)

        /// <summary>A client pressed START. Decide as if the host had pressed it; on success call SendRunStart.</summary>
        void OnStartRequest(byte slot);

        /// <summary>
        /// A client pressed EXTRACT. Apply the all-aboard rule; answer with SendExtractRefused(slot, mask) or end the
        /// run and call SendRunEnded.
        /// </summary>
        void OnExtractRequest(byte slot);

        // ---------------------------------------------------------------- items (reliable unless noted)

        /// <summary>
        /// Asked while arbitrating a GrabRequest: may this item be picked up at all (exists, not secured in the
        /// locker, not still mounted behind a panel)? Ownership, epochs and "somebody already holds it" are the
        /// session's business (ItemOwnershipTable), not this method's.
        /// </summary>
        bool CanGrabItem(byte itemId, byte slot);

        /// <summary>
        /// The client now holds the item (grant already broadcast). Set SalvageItem.RemoteHolder, RefreshBodyMode,
        /// and on a first pickup run NotifySalvageCollected and call SendItemCollected. The hold ends with
        /// <see cref="OnItemReleased"/> or, without one, with <see cref="OnItemReturned"/>.
        /// </summary>
        void OnItemGranted(byte itemId, byte slot, byte hand);

        /// <summary>
        /// Unreliable: the owning client's pose for an item it holds or is flying. Stale epochs and non-owners
        /// were already dropped. <paramref name="hostTime"/> is the host clock at arrival.
        /// </summary>
        void OnItemPose(byte itemId, byte slot, NetVec3 position, NetQuat rotation, bool held, double hostTime);

        /// <summary>
        /// The owner let go (ItemReleased). Clear RemoteHolder; the client keeps simulating the flight until it
        /// hands the item back. <paramref name="releasePosition"/> is the hand position for trick-shot distance.
        /// </summary>
        void OnItemReleased(byte itemId, byte slot, NetVec3 position, NetQuat rotation, NetVec3 releasePosition);

        /// <summary>Follow-up to a release that was a real throw: call RegisterThrow(velocity, proxyPlayer, releasePosition).</summary>
        void OnItemThrown(byte itemId, byte slot, NetVec3 linearVelocity, NetVec3 angularVelocity, float throwSpeed, bool wasThrown);

        /// <summary>
        /// The host simulates this item again (handoff, 3 s backstop, owner Away or gone, host take-over). Make
        /// the body dynamic at the given pose and velocity and clear RemoteHolder: the item may still have been
        /// in the client's hand as far as the host knew (no <see cref="OnItemReleased"/> came first), also for a
        /// Handoff. The new ownership has already been broadcast.
        /// </summary>
        void OnItemReturned(in ItemReturn item);

        /// <summary>
        /// A client says an item it simulates was damaged. Validate (lower than now, not below a third of full,
        /// the sender really simulates it is already checked) and, if accepted, call SendItemValue.
        /// </summary>
        void OnItemDamageReport(byte itemId, byte slot, int newValue);

        // ---------------------------------------------------------------- world (reliable)

        /// <summary>A client pressed a door button and wants this state. Call SlidingDoor.SetOpen(desired, false, PlayerId(slot)); the door's Changed event then calls SendDoorState.</summary>
        void OnDoorRequest(byte slot, byte doorId, bool desiredOpen);

        /// <summary>A client finished a cut point. ForceCut it, call SendPointCut, and open the panel (SendPanelOpened) when all are done.</summary>
        void OnPointCutReport(byte slot, byte panel, byte index);

        // ---------------------------------------------------------------- creature and hazards (reliable, Stage 4)

        /// <summary>A client's dart hit the creature on its screen. Check running, not scaring, within tool range + 2 m; then Tranquilize and call SendTranquilized.</summary>
        void OnTranqHitReport(byte slot, NetVec3 origin, NetVec3 hitPoint);

        /// <summary>A client touched or burnt a web wire. Apply the state, emit the Web noise, call SendWireChanged.</summary>
        void OnWireReport(byte slot, byte index, NetWireChange change);
    }
}

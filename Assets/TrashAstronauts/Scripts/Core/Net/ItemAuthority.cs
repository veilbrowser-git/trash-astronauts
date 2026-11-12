namespace TrashAstronauts.Core.Net
{
    /// <summary>Who simulates an item, from one client's point of view.</summary>
    public enum ItemMode : byte
    {
        /// <summary>Simulated elsewhere and nobody holds it: body kinematic, grab enabled, pose from the stream and ItemRest.</summary>
        HostFree = 0,
        /// <summary>Simulated here: body dynamic, XRI and local physics in control, this peer streams the pose.</summary>
        Mine = 1,
        /// <summary>In somebody else's hand: body kinematic, SalvageItem.RemoteHolder set, cannot be selected.</summary>
        RemoteHeld = 2,
    }

    /// <summary>What an ownership message meant for the local player.</summary>
    public enum ItemAuthorityEvent : byte
    {
        None = 0,
        /// <summary>The host confirmed our grab.</summary>
        Granted = 1,
        /// <summary>The host refused our grab: cancel the selection (no release, no throw, no recoil) and buzz.</summary>
        Denied = 2,
        /// <summary>The host took an item we were simulating (backstop, we were Away, somebody caught it).</summary>
        Revoked = 3,
        /// <summary>Our handoff was confirmed: the item is the host's again.</summary>
        Returned = 4,
        /// <summary>Somebody else's ownership changed (mode may have switched between HostFree and RemoteHeld).</summary>
        RemoteChanged = 5,
        /// <summary>
        /// The host made us the holder of an item our hand is no longer on (a grab it granted after we had
        /// already been told "no" for an earlier one, or let go). Send ItemReleased now, with
        /// <see cref="ItemAuthority.WireEpoch"/> and the item's pose, so nobody sees it stuck in our hand; the
        /// hand-back timer is already running and <see cref="ItemAuthority.Tick"/> will ask for the ItemHandoff.
        /// Do not select the item and do not buzz.
        /// </summary>
        GrantedNotHeld = 6,
    }

    /// <summary>
    /// One client's view of one salvage item: the mode state machine and the hand-back timer. Pure; ItemSync
    /// (and the bots) drive it and do what it says.
    ///
    /// Life of a grab: <see cref="BeginGrab"/> (XRI selected locally: Mode becomes Mine at once, a GrabRequest goes
    /// out) -> <see cref="OnOwner"/> answers Granted or Denied -> <see cref="Release"/> (ItemReleased goes out,
    /// the timer starts) -> <see cref="Tick"/> returns true once when the item has been slower than 0.25 m/s for
    /// 0.3 s, or 1.5 s after the release (ItemHandoff goes out) -> OnOwner(host) answers Returned and the item is
    /// HostFree again with a 0.2 s blend. The timer does not depend on any throw having been registered.
    ///
    /// Everything sent while a request is unanswered carries the epoch the grant WILL have (<see cref="WireEpoch"/>),
    /// so a quick grab-release-handoff inside one round trip is still accepted by the host in order.
    ///
    /// With two requests out at once (catch, then re-grab or pass to the other hand inside one round trip) the
    /// host can refuse the first and grant the second. The refusal cancels the local hold, so the grant then
    /// makes this peer the holder of an item nobody holds: <see cref="OnOwner"/> answers
    /// <see cref="ItemAuthorityEvent.GrantedNotHeld"/> and the item is released and handed straight back.
    /// </summary>
    public sealed class ItemAuthority
    {
        public const float HandbackSpeed = 0.25f;
        public const double HandbackSlowSeconds = 0.3;
        public const double HandbackMaxSeconds = 3.0; // most throws hit something or are caught before this; a later hand-back means fewer corrections
        public const double BlendSeconds = 0.2;

        readonly byte m_ItemId;
        byte m_LocalSlot;
        int m_PendingGrabs;
        byte m_WireEpoch;
        double m_ReleasedAt;
        double m_SlowSince;

        public byte ItemId => m_ItemId;
        public ItemMode Mode { get; private set; }
        /// <summary>A GrabRequest is out and unanswered.</summary>
        public bool Pending => m_PendingGrabs > 0;
        /// <summary>The local player's hand is on it right now.</summary>
        public bool HeldLocally { get; private set; }
        /// <summary>ItemHandoff has been sent and not yet confirmed. The body stays dynamic here until it is.</summary>
        public bool HandoffSent { get; private set; }
        /// <summary>Deposited in the locker: never grabbable again.</summary>
        public bool Secured { get; private set; }
        /// <summary>Last ownership epoch confirmed by the host.</summary>
        public byte Epoch { get; private set; }
        /// <summary>Last owner confirmed by the host (0 = host).</summary>
        public byte OwnerSlot { get; private set; }
        /// <summary>The epoch to put in ItemPose, ItemReleased, ItemThrown and ItemHandoff right now.</summary>
        public byte WireEpoch => m_WireEpoch;
        /// <summary>Until this time, blend from the local pose to the streamed one instead of snapping.</summary>
        public double BlendUntil { get; private set; }

        /// <summary>SalvageItem.SimulatedHere on a client.</summary>
        public bool SimulatedHere => Mode == ItemMode.Mine;
        /// <summary>
        /// True while this peer should put the item in its ItemPose stream. Queue it on every send tick while
        /// this is true, moving or not: the host takes back an item whose holder has streamed nothing for 3 s.
        /// </summary>
        public bool ShouldStream => Mode == ItemMode.Mine && !HandoffSent;
        /// <summary>False while somebody else holds it or it is in the locker (SalvageGrabInteractable.IsSelectableBy).</summary>
        public bool CanGrab => Mode != ItemMode.RemoteHeld && !Secured;

        public ItemAuthority(byte itemId, byte localSlot)
        {
            m_ItemId = itemId;
            m_LocalSlot = localSlot;
            Reset(localSlot);
        }

        /// <summary>Back to the start-of-run state: host-simulated, free, epoch 0.</summary>
        public void Reset(byte localSlot)
        {
            m_LocalSlot = localSlot;
            Mode = ItemMode.HostFree;
            m_PendingGrabs = 0;
            m_WireEpoch = 0;
            HeldLocally = false;
            HandoffSent = false;
            Secured = false;
            Epoch = 0;
            OwnerSlot = 0;
            BlendUntil = 0;
            m_ReleasedAt = 0;
            m_SlowSince = -1;
        }

        /// <summary>
        /// XRI has selected the item locally. Returns true when a GrabRequest must be sent, with
        /// <paramref name="epochSeen"/>; false when the grab is not allowed (cancel the selection).
        /// </summary>
        public bool BeginGrab(out byte epochSeen)
        {
            epochSeen = m_WireEpoch;
            if (!CanGrab || HeldLocally) return false;
            Mode = ItemMode.Mine;
            HeldLocally = true;
            HandoffSent = false;
            m_PendingGrabs++;
            m_WireEpoch++; // the epoch our grant will carry
            m_SlowSince = -1;
            return true;
        }

        /// <summary>
        /// The local hand let go (selectExited). Returns true when ItemReleased must be sent (with
        /// <see cref="WireEpoch"/>); false when there is nothing to release (the grab was denied or revoked).
        /// </summary>
        public bool Release(double now)
        {
            if (!HeldLocally) return false;
            HeldLocally = false;
            if (Mode != ItemMode.Mine) return false;
            m_ReleasedAt = now;
            m_SlowSince = -1;
            return true;
        }

        /// <summary>
        /// Advances the hand-back timer of an item this peer released and still simulates. Returns true exactly
        /// once per release, when ItemHandoff must be sent with <paramref name="handoffEpoch"/>. Safe to call
        /// every frame in any state.
        /// </summary>
        public bool Tick(double now, float speed, out byte handoffEpoch)
        {
            handoffEpoch = m_WireEpoch;
            if (Mode != ItemMode.Mine || HeldLocally || HandoffSent) return false;

            bool slow = speed < HandbackSpeed; // false for NaN, which then waits for the 1.5 s limit
            if (slow)
            {
                if (m_SlowSince < 0) m_SlowSince = now;
            }
            else m_SlowSince = -1;

            bool due = (slow && now - m_SlowSince >= HandbackSlowSeconds) || now - m_ReleasedAt >= HandbackMaxSeconds;
            if (!due) return false;
            HandoffSent = true;
            m_WireEpoch++; // the host raises the epoch when it takes the item back
            return true;
        }

        /// <summary>Feeds an ItemOwner message. Returns what it meant for the local player.</summary>
        public ItemAuthorityEvent OnOwner(byte ownerSlot, byte epoch, bool held, bool denied, double now)
        {
            if (denied)
            {
                // The direct "no" to one of our requests. The other fields are the item's current state.
                if (m_PendingGrabs > 0) m_PendingGrabs--;
                bool wasMine = Mode == ItemMode.Mine;
                Epoch = epoch;
                OwnerSlot = ownerSlot;
                if (ownerSlot == m_LocalSlot && m_PendingGrabs == 0 && !HandoffSent)
                {
                    // We already own it (an earlier request of ours won). Nothing to cancel.
                    m_WireEpoch = epoch;
                    return ItemAuthorityEvent.None;
                }
                Mode = held && ownerSlot != m_LocalSlot ? ItemMode.RemoteHeld : ItemMode.HostFree;
                HeldLocally = false;
                HandoffSent = false;
                if (m_PendingGrabs == 0) m_WireEpoch = epoch;
                if (wasMine) BlendUntil = now + BlendSeconds;
                return ItemAuthorityEvent.Denied;
            }

            if (ownerSlot == m_LocalSlot)
            {
                Epoch = epoch;
                OwnerSlot = ownerSlot;
                if (held && m_PendingGrabs > 0)
                {
                    m_PendingGrabs--;
                    if (Mode != ItemMode.Mine)
                    {
                        // Granted after an earlier denial put us back: the host has us holding an item our hand
                        // is not on. Release it (the caller sends ItemReleased) and hand it back.
                        Mode = ItemMode.Mine;
                        HeldLocally = false;
                        HandoffSent = false;
                        m_WireEpoch = epoch;
                        m_ReleasedAt = now;
                        m_SlowSince = -1;
                        return ItemAuthorityEvent.GrantedNotHeld;
                    }
                    if (m_PendingGrabs == 0 && !HandoffSent) m_WireEpoch = epoch;
                    return ItemAuthorityEvent.Granted;
                }
                if (Mode != ItemMode.Mine && m_PendingGrabs == 0)
                {
                    // The host says we simulate it but we do not think so (should not happen): give it back soon.
                    Mode = ItemMode.Mine;
                    HeldLocally = false;
                    HandoffSent = false;
                    m_WireEpoch = epoch;
                    m_ReleasedAt = now;
                    m_SlowSince = -1;
                    return held ? ItemAuthorityEvent.GrantedNotHeld : ItemAuthorityEvent.None;
                }
                return ItemAuthorityEvent.None;
            }

            // Somebody else (or the host) simulates it.
            Epoch = epoch;
            OwnerSlot = ownerSlot;
            if (m_PendingGrabs > 0)
            {
                // History from before our request is answered (for example the confirmation of our own earlier
                // handoff). The answer itself is always a grant naming us or a denied reply.
                return ItemAuthorityEvent.None;
            }

            ItemMode before = Mode;
            bool wasHandoff = HandoffSent;
            Mode = held ? ItemMode.RemoteHeld : ItemMode.HostFree;
            m_WireEpoch = epoch;
            HandoffSent = false;
            if (before == ItemMode.Mine)
            {
                HeldLocally = false;
                BlendUntil = now + BlendSeconds;
                return wasHandoff && ownerSlot == 0 && !held ? ItemAuthorityEvent.Returned : ItemAuthorityEvent.Revoked;
            }
            return ItemAuthorityEvent.RemoteChanged;
        }

        /// <summary>True when a streamed pose should move the local copy: not simulated here, and not from a stale epoch.</summary>
        public bool AcceptPose(byte epoch6)
        {
            return Mode != ItemMode.Mine && (epoch6 & ItemPoseEntry.EpochMask) == (Epoch & ItemPoseEntry.EpochMask);
        }

        /// <summary>True when an ItemRest should snap/heal the local copy.</summary>
        public bool AcceptRest(byte epoch)
        {
            return Mode != ItemMode.Mine && epoch == Epoch;
        }

        /// <summary>ItemDeposited arrived: the item is in the locker for good.</summary>
        public void OnDeposited()
        {
            Secured = true;
            HeldLocally = false;
            HandoffSent = false;
            if (Mode == ItemMode.Mine || Mode == ItemMode.RemoteHeld) Mode = ItemMode.HostFree;
        }
    }
}

namespace TrashAstronauts.Core.Net
{
    /// <summary>Outcome of a grab request on the host.</summary>
    public enum GrabResult : byte
    {
        Granted = 0,
        UnknownItem = 1,
        /// <summary>The requester grabbed against an ownership state that has since changed: it lost the race.</summary>
        StaleEpoch = 2,
        /// <summary>Somebody is holding it.</summary>
        Held = 3,
        /// <summary>The game says no (secured in the locker, still mounted), or the item is locked here.</summary>
        NotGrantable = 4,
    }

    /// <summary>
    /// Host-side arbitration of who simulates each salvage item. Pure bookkeeping, no scene access.
    ///
    /// Rules:
    ///  - Owner 0 is the host. An item is "free" when nobody holds it, whoever simulates it.
    ///  - A grab is granted when the item exists, is not locked, the game allows it, nobody holds it and the
    ///    requester's epoch equals the current epoch. First to arrive wins; the grant raises the epoch, so a
    ///    second request made against the same state is stale. Host-local grabs go through
    ///    <see cref="HostGrab"/> the moment they happen, which is how the host wins ties.
    ///  - The epoch rises on every grant and every return to the host. A release keeps it.
    ///  - Poses, releases, throws and handoffs are accepted only from the current owner at the current epoch.
    ///  - A handoff is taken from the current owner at the current epoch even when the table still says
    ///    "held": the owner is the one who knows it let go (its ItemReleased can have been turned down while
    ///    an earlier request of its own was being refused).
    ///  - Backstops: an item owned by a client but not held for 3 s without a handoff, an item a client is
    ///    said to hold but has not streamed for 3 s (<see cref="FindUnstreamed"/>), or one whose owner goes
    ///    Away or leaves, is returned to the host at its last streamed pose.
    /// </summary>
    public sealed class ItemOwnershipTable
    {
        public struct Entry
        {
            /// <summary>Slot that simulates the item (0 = host).</summary>
            public byte owner;
            public byte epoch;
            public bool held;
            /// <summary>Secured in the locker: can never be grabbed again this run.</summary>
            public bool locked;
            /// <summary>When a client owner let go (start of the 3 s backstop).</summary>
            public double releasedAt;
            /// <summary>True once the current client owner has streamed a pose.</summary>
            public bool hasPose;
            /// <summary>When the current client owner was granted the item or last had a streamed pose accepted (see <see cref="Touch"/>).</summary>
            public double streamedAt;
            public NetVec3 position;
            public NetQuat rotation;
        }

        public const int MaxItems = 256;

        readonly Entry[] m_Items = new Entry[MaxItems];
        int m_Count;

        public int Count => m_Count;

        /// <summary>Starts a run: every item free, host-simulated, epoch 0.</summary>
        public void Reset(int itemCount)
        {
            if (itemCount < 0) itemCount = 0;
            if (itemCount > MaxItems) itemCount = MaxItems;
            m_Count = itemCount;
            for (int i = 0; i < MaxItems; i++) m_Items[i] = default(Entry);
        }

        public bool Contains(int itemId) => itemId >= 0 && itemId < m_Count;

        /// <summary>A copy of the entry (default for an unknown id).</summary>
        public Entry Get(int itemId) => Contains(itemId) ? m_Items[itemId] : default(Entry);
        public byte Owner(int itemId) => Contains(itemId) ? m_Items[itemId].owner : (byte)0;
        public byte Epoch(int itemId) => Contains(itemId) ? m_Items[itemId].epoch : (byte)0;
        public bool IsHeld(int itemId) => Contains(itemId) && m_Items[itemId].held;
        public bool IsLocked(int itemId) => Contains(itemId) && m_Items[itemId].locked;
        /// <summary>True when a client (not the host) simulates the item.</summary>
        public bool IsClientOwned(int itemId) => Contains(itemId) && m_Items[itemId].owner != 0;

        /// <summary>True when <paramref name="slot"/> owns the item at exactly this epoch (all 8 bits).</summary>
        public bool IsCurrent(int itemId, byte slot, byte epoch)
        {
            return Contains(itemId) && m_Items[itemId].owner == slot && m_Items[itemId].epoch == epoch;
        }

        /// <summary>A client asks to hold an item. <paramref name="grantable"/> is the game's answer (IHostWorld.CanGrabItem).</summary>
        public GrabResult TryGrab(int itemId, byte slot, byte epochSeen, bool grantable)
        {
            if (!Contains(itemId)) return GrabResult.UnknownItem;
            if (m_Items[itemId].locked || !grantable) return GrabResult.NotGrantable;
            if (m_Items[itemId].epoch != epochSeen) return GrabResult.StaleEpoch;
            if (m_Items[itemId].held) return GrabResult.Held;
            m_Items[itemId].owner = slot;
            m_Items[itemId].held = true;
            m_Items[itemId].epoch++;
            m_Items[itemId].hasPose = false;
            m_Items[itemId].releasedAt = 0;
            return GrabResult.Granted;
        }

        /// <summary>The host's own hand took the item. Succeeds unless a client is holding it or it is locked.</summary>
        public bool HostGrab(int itemId)
        {
            if (!Contains(itemId) || m_Items[itemId].locked) return false;
            if (m_Items[itemId].held) return false;
            m_Items[itemId].owner = 0;
            m_Items[itemId].held = true;
            m_Items[itemId].epoch++;
            m_Items[itemId].hasPose = false;
            return true;
        }

        /// <summary>The host's own hand let go. The item stays host-simulated; the epoch does not change.</summary>
        public bool HostRelease(int itemId)
        {
            if (!Contains(itemId) || m_Items[itemId].owner != 0 || !m_Items[itemId].held) return false;
            m_Items[itemId].held = false;
            return true;
        }

        /// <summary>A client let go. It keeps simulating the flight; the backstop clock starts.</summary>
        public bool Release(int itemId, byte slot, byte epoch, double now)
        {
            if (slot == 0 || !IsCurrent(itemId, slot, epoch) || !m_Items[itemId].held) return false;
            m_Items[itemId].held = false;
            m_Items[itemId].releasedAt = now;
            return true;
        }

        /// <summary>
        /// A streamed pose from a client. Accepted (and remembered as the last pose) only from the owner at the
        /// current epoch (low 6 bits). <paramref name="now"/> restarts the held-item backstop clock.
        /// </summary>
        public bool AcceptPose(int itemId, byte slot, byte epoch6, NetVec3 position, NetQuat rotation, double now = 0)
        {
            if (slot == 0 || !Contains(itemId) || m_Items[itemId].owner != slot) return false;
            if ((m_Items[itemId].epoch & ItemPoseEntry.EpochMask) != (epoch6 & ItemPoseEntry.EpochMask)) return false;
            m_Items[itemId].hasPose = true;
            m_Items[itemId].position = position;
            m_Items[itemId].rotation = rotation;
            m_Items[itemId].streamedAt = now;
            return true;
        }

        /// <summary>Restarts the held-item backstop clock of an item (the host session calls it when it grants a grab).</summary>
        public void Touch(int itemId, double now)
        {
            if (Contains(itemId)) m_Items[itemId].streamedAt = now;
        }

        /// <summary>Remembers the pose carried by a reliable item message (release, handoff) from the current owner.</summary>
        public void RememberPose(int itemId, NetVec3 position, NetQuat rotation)
        {
            if (!Contains(itemId)) return;
            m_Items[itemId].hasPose = true;
            m_Items[itemId].position = position;
            m_Items[itemId].rotation = rotation;
        }

        /// <summary>
        /// The owner hands the item back. False for a non-owner or a stale epoch. An item the table still has as
        /// held is handed back all the same: the owner says it is not holding it.
        /// </summary>
        public bool Handoff(int itemId, byte slot, byte epoch)
        {
            if (slot == 0 || !IsCurrent(itemId, slot, epoch)) return false;
            ToHost(itemId);
            return true;
        }

        /// <summary>Takes a client-owned item back to the host whatever its state. False if the host already had it.</summary>
        public bool ReturnToHost(int itemId, out byte previousOwner)
        {
            previousOwner = 0;
            if (!Contains(itemId) || m_Items[itemId].owner == 0) return false;
            previousOwner = m_Items[itemId].owner;
            ToHost(itemId);
            return true;
        }

        void ToHost(int itemId)
        {
            m_Items[itemId].owner = 0;
            m_Items[itemId].held = false;
            m_Items[itemId].epoch++;
            m_Items[itemId].releasedAt = 0;
        }

        /// <summary>
        /// Marks an item secured in the locker. Returns true when that changed who simulates or holds it (the
        /// epoch then rose and the caller must broadcast the new ownership).
        /// </summary>
        public bool Lock(int itemId)
        {
            if (!Contains(itemId)) return false;
            m_Items[itemId].locked = true;
            if (m_Items[itemId].owner == 0 && !m_Items[itemId].held) return false;
            ToHost(itemId);
            return true;
        }

        /// <summary>Lists items a client owns, is not holding, and released at least <paramref name="seconds"/> ago. Changes nothing.</summary>
        public int FindExpired(double now, double seconds, byte[] results)
        {
            int n = 0;
            if (results == null) return 0;
            for (int i = 0; i < m_Count && n < results.Length; i++)
                if (m_Items[i].owner != 0 && !m_Items[i].held && now - m_Items[i].releasedAt >= seconds) results[n++] = (byte)i;
            return n;
        }

        /// <summary>
        /// Lists items a client is said to be holding but has neither been granted nor streamed a pose for in
        /// the last <paramref name="seconds"/>. A real holder streams the item 20 times a second, so this only
        /// finds holds the client does not know it has. Changes nothing.
        /// </summary>
        public int FindUnstreamed(double now, double seconds, byte[] results)
        {
            int n = 0;
            if (results == null) return 0;
            for (int i = 0; i < m_Count && n < results.Length; i++)
                if (m_Items[i].owner != 0 && m_Items[i].held && now - m_Items[i].streamedAt >= seconds) results[n++] = (byte)i;
            return n;
        }

        /// <summary>Lists items simulated by <paramref name="slot"/>. Changes nothing.</summary>
        public int FindOwnedBy(byte slot, byte[] results)
        {
            int n = 0;
            if (results == null) return 0;
            for (int i = 0; i < m_Count && n < results.Length; i++)
                if (m_Items[i].owner == slot) results[n++] = (byte)i;
            return n;
        }
    }
}

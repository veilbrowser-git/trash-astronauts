using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// The ledger half of a co-op client with no scene: a <see cref="MissionSession"/> that follows a host by
    /// applying what it broadcasts, exactly as the client director's mirror does (ids from the run's number,
    /// the same idempotent MissionSession.ApplyRemote* calls, its own profile paid once at the end). Used to
    /// prove that what <see cref="CoopHostRules"/> broadcasts is enough to rebuild the host's books
    /// (SessionDigest equal after every step), and usable as a scene-less bot's ledger.
    /// Messages that only move things in the scene are counted and otherwise ignored.
    /// </summary>
    public sealed class CoopSessionMirror : ICoopBroadcast
    {
        readonly NetIdTable m_Ids;
        string[] m_ItemIds = new string[0];

        public MissionSession Session { get; }
        /// <summary>The save this mirror banks into (null: nothing is banked).</summary>
        public ProfileService Profile { get; }
        public int MessageCount { get; private set; }
        public int CreditsBanked { get; private set; }
        public int ExtractRefusals { get; private set; }
        public int LastMissingMask { get; private set; }
        public bool HostPaused { get; private set; }

        public CoopSessionMirror(NetIdTable ids, MissionSession session, ProfileService profile = null)
        {
            m_Ids = ids;
            Session = session;
            Profile = profile;
        }

        /// <summary>The salvage id this mirror uses for an item net id (null if unknown).</summary>
        public string SalvageIdOf(int itemId) => itemId >= 0 && itemId < m_ItemIds.Length ? m_ItemIds[itemId] : null;

        static PlayerId Who(byte slot) => CoopClientApply.Who(slot);

        public void RunStart(in RunStartParams parameters)
        {
            MessageCount++;
            if (Session.Phase == MissionPhase.Active) Session.AbortToHub();
            if (!Session.Begin(parameters.seed, parameters.quota, parameters.oxygenCapacitySeconds, parameters.runIndex)) return;
            Session.BonusMultiplier = parameters.bonusMultiplier;
            int count = parameters.items != null ? parameters.items.Length : 0;
            m_ItemIds = new string[count];
            for (int i = 0; i < count; i++)
            {
                var entry = parameters.items[i];
                string id = m_Ids.SalvageIdFor(Session.RunIndex, entry);
                string definition = m_Ids.Definitions[entry.definitionIndex];
                m_ItemIds[i] = id;
                if (id != null && definition != null) Session.RegisterSalvage(id, definition, entry.value);
            }
        }

        public void WorldState(in WorldStateMsg state) => MessageCount++;

        public void ExtractRefused(byte slot, byte missingSlotsMask)
        {
            MessageCount++;
            ExtractRefusals++;
            LastMissingMask = missingSlotsMask;
        }

        public void RunEnded(in RunEndedMsg result)
        {
            MessageCount++;
            if (Session.ApplyRemoteEnd(result.ToResult()) != null && Profile != null) CreditsBanked += Session.BankInto(Profile);
        }

        public void ReturnedToHub()
        {
            MessageCount++;
            Session.ReturnToHub();
        }

        public void HostPause(bool on)
        {
            MessageCount++;
            HostPaused = on;
        }

        public void ItemCollected(byte itemId, byte by)
        {
            MessageCount++;
            Session.ApplyRemoteCollect(SalvageIdOf(itemId), Who(by));
        }

        public void ItemValue(byte itemId, int value)
        {
            MessageCount++;
            Session.ApplyRemoteValue(SalvageIdOf(itemId), value);
        }

        public void ItemDeposited(byte itemId, byte by, int finalValue, int trickBonus, byte lockerSlot)
        {
            MessageCount++;
            Session.ApplyRemoteDeposit(SalvageIdOf(itemId), Who(by), finalValue, trickBonus);
        }

        public void ItemRecovered(byte itemId, NetVec3 position) => MessageCount++;

        public void ItemRest(byte itemId, NetVec3 position, NetQuat rotation) => MessageCount++;

        public void DoorState(byte doorId, bool open) => MessageCount++;

        public void PointCut(byte panel, byte index) => MessageCount++;

        public void PanelOpened(byte panel, byte by, NetVec3 plateImpulse, NetVec3 plateTorque)
        {
            MessageCount++;
            string panelId = m_Ids.Panels[panel];
            if (panelId != null) Session.TryOpenPanel(panelId, Who(by));
        }

        /// <summary>The host's Clock (not a broadcast: the session samples it).</summary>
        public bool ApplyClock(float elapsedSeconds, float oxygenRemainingSeconds) => Session.ApplyClock(elapsedSeconds, oxygenRemainingSeconds);
    }
}

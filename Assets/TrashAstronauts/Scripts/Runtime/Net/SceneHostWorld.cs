using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// The real scene as the host session sees it. Presence (stage 1): crew members appear, move, go away and
    /// leave. The shared ride-along contract (stage 2): every request from the crew goes to the session's
    /// <see cref="CoopHostRules"/>, which decides it with the game's own rules exactly as if the host had
    /// pressed the button, and whose broadcasts go back out through NetSessionHost.
    /// Item grabs, carries, throws and hand-backs (stage 3) go to the session's <see cref="CoopItemHost"/>.
    /// </summary>
    public sealed class SceneHostWorld : IHostWorld
    {
        readonly NetSession m_Net;
        readonly MissionDirector m_Director;
        readonly CoopHostRules m_Rules;
        readonly CoopItemHost m_Items;

        public SceneHostWorld(NetSession net, MissionDirector director, CoopHostRules rules, CoopItemHost items)
        {
            m_Net = net;
            m_Director = director;
            m_Rules = rules;
            m_Items = items;
        }

        public CoopHostRules Rules => m_Rules;

        /// <summary>Reported in HostInfo and checked on every Hello: a join is refused ContractInProgress while a contract runs.</summary>
        public MissionPhase Phase => m_Director != null ? m_Director.Phase : MissionPhase.Hub;

        // ------------------------------------------------------------------ presence

        public void FillWorldState(ref WorldStateMsg state)
        {
            if (m_Rules != null) m_Rules.FillWorldState(ref state);
        }

        public void OnPlayerJoined(byte slot, byte handedness) => m_Net.AddCrew(slot, handedness, false);

        public void OnPlayerLeft(byte slot, PlayerLeaveReason reason) => m_Net.RemoveCrew(slot);

        public void OnPlayerAway(byte slot, bool away)
        {
            m_Net.SetCrewAway(slot, away);
            if (m_Rules != null) m_Rules.HostPlayerAway(slot, away);
        }

        public void OnPlayerSettings(byte slot, byte handedness) => m_Net.SetCrewHandedness(slot, handedness);

        public void OnAvatar(byte slot, in AvatarSnapshot avatar, double hostTime) => m_Net.PushCrewAvatar(slot, hostTime, in avatar);

        // ------------------------------------------------------------------ the shared contract

        public void OnStartRequest(byte slot)
        {
            if (m_Rules != null) m_Rules.HostStartRequest(slot);
        }

        public void OnExtractRequest(byte slot)
        {
            if (m_Rules != null) m_Rules.HostExtractRequest(slot);
        }

        public void OnDoorRequest(byte slot, byte doorId, bool desiredOpen)
        {
            if (m_Rules != null) m_Rules.HostDoorRequest(doorId, desiredOpen, slot);
        }

        public void OnPointCutReport(byte slot, byte panel, byte index)
        {
            if (m_Rules != null) m_Rules.HostPointCutReport(panel, index, slot);
        }

        // ------------------------------------------------------------------ items: everyone grabs (stage 3)

        public bool CanGrabItem(byte itemId, byte slot) => m_Items != null && m_Items.CanGrabItem(itemId, slot);

        public void OnItemGranted(byte itemId, byte slot, byte hand) { if (m_Items != null) m_Items.OnItemGranted(itemId, slot, hand); }

        public void OnItemPose(byte itemId, byte slot, NetVec3 position, NetQuat rotation, bool held, double hostTime) { if (m_Items != null) m_Items.OnItemPose(itemId, slot, position, rotation, held, hostTime); }

        public void OnItemReleased(byte itemId, byte slot, NetVec3 position, NetQuat rotation, NetVec3 releasePosition) { if (m_Items != null) m_Items.OnItemReleased(itemId, slot, position, rotation, releasePosition); }

        public void OnItemThrown(byte itemId, byte slot, NetVec3 linearVelocity, NetVec3 angularVelocity, float throwSpeed, bool wasThrown) { if (m_Items != null) m_Items.OnItemThrown(itemId, slot, linearVelocity, angularVelocity, throwSpeed, wasThrown); }

        /// <summary>The host simulates the item again (handoff, backstop, owner away or gone); may arrive for an item the host still had as held.</summary>
        public void OnItemReturned(in ItemReturn item) { if (m_Items != null) m_Items.OnItemReturned(in item); }

        public void OnItemDamageReport(byte itemId, byte slot, int newValue) { if (m_Items != null) m_Items.OnItemDamageReport(itemId, slot, newValue); }

        // ------------------------------------------------------------------ creature and hazards

        public void OnNoise(byte slot, NetVec3 position, float loudness, NoiseKind kind)
        {
            if (m_Rules != null) m_Rules.HostNoise(position, loudness, kind, slot);
        }

        public void OnTranqHitReport(byte slot, NetVec3 origin, NetVec3 hitPoint)
        {
            if (m_Rules == null || !m_Rules.HostTranqHitReport(origin, hitPoint, slot)) return;
            // Everyone sees the crew member's shot: the host draws it and tells the others (the shooter drew its own).
            CoopToolSync.ShowTranqTracer(m_Net, slot, origin, hitPoint);
            var host = m_Net.HostSession;
            if (host != null) host.SendTranqTracer(slot, origin, hitPoint);
        }

        public void OnWireReport(byte slot, byte index, NetWireChange change) { /* Stage 4: webs are never armed in a co-op contract. */ }
    }
}

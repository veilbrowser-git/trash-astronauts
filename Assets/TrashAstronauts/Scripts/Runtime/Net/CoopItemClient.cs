using System;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// Co-op stage 3 on a client: every crew member grabs, carries, hands over, throws and deposits salvage.
    /// One per client session. While it exists the director's run items are grabbable here (no longer
    /// HOST ONLY): when a contract starts each gets an <see cref="ItemSync"/>, which grabs optimistically
    /// and talks to the host through <see cref="ICoopItemRequests"/>. The host's answers come in through
    /// <see cref="ApplyItemOwner"/>; poses, rests, values and deposits keep arriving through
    /// <see cref="CoopClientApply"/> as before (they now respect each item's ownership epoch).
    /// Plain C#; the session adapter creates it with its request sink and disposes it when the session ends.
    /// </summary>
    public sealed class CoopItemClient : IDisposable
    {
        readonly MissionDirector m_Director;
        readonly ICoopItemRequests m_Requests;
        bool m_Disposed;

        /// <summary>Seconds on this machine's clock, for the hand-back timer and blends. Unscaled time by default.</summary>
        public Func<double> Clock = () => Time.unscaledTimeAsDouble;

        public CoopItemClient(MissionDirector director, ICoopItemRequests requests)
        {
            m_Director = director ?? throw new ArgumentNullException(nameof(director));
            m_Requests = requests ?? throw new ArgumentNullException(nameof(requests));
            director.ItemClient = this;
            // A contract that is already running (joined between RunStart and now): take it over as it is.
            if (director.Phase == MissionPhase.Active && director.Net != null && director.Net.IsClient) BeginRun();
        }

        public MissionDirector Director => m_Director;
        public ICoopItemRequests Requests => m_Requests;
        public double Now => Clock != null ? Clock() : Time.unscaledTimeAsDouble;
        public bool Disposed => m_Disposed;

        /// <summary>This client's slot (the director's local player).</summary>
        byte LocalSlot => (byte)Mathf.Clamp(m_Director.Net != null ? m_Director.Net.LocalSlot : m_Director.LocalPlayerId.Value, 0, 255);

        /// <summary>
        /// The host's contract has just begun here (MissionDirector.ApplyRunStart): every item starts
        /// host-simulated and free at epoch 0, and can be grabbed.
        /// </summary>
        public void BeginRun()
        {
            if (m_Disposed) return;
            var items = m_Director.RunItems;
            byte slot = LocalSlot;
            for (int i = 0; i < items.Count && i < ItemOwnershipTable.MaxItems; i++)
            {
                var item = items[i];
                if (item == null) continue;
                item.SetHostOnly(false);
                ItemSync.For(item).Begin(this, (byte)i, slot);
            }
        }

        /// <summary>The ItemSync of a run item, or null.</summary>
        public ItemSync SyncOf(byte itemId)
        {
            var item = m_Director.RunItemAt(itemId);
            return item != null ? item.GetComponent<ItemSync>() : null;
        }

        /// <summary>
        /// IClientWorld.OnItemOwner: who simulates the item now. Feeds the item's ItemAuthority and acts on it
        /// (a denial cancels the local hold and buzzes; a grant needs nothing). Returns what it meant here.
        /// </summary>
        public ItemAuthorityEvent ApplyItemOwner(byte itemId, byte ownerSlot, byte epoch, bool held, bool denied)
        {
            if (m_Disposed) return ItemAuthorityEvent.None;
            var sync = SyncOf(itemId);
            return sync != null ? sync.ApplyOwner(ownerSlot, epoch, held, denied) : ItemAuthorityEvent.None;
        }

        /// <summary>
        /// An item simulated here took a knock (MissionDirector.ReportSalvageDamage on a client): the host is
        /// told and decides; the ledger here only ever changes through ItemValue.
        /// </summary>
        public bool ReportDamage(SalvageItem item, int newValue)
        {
            if (m_Disposed || item == null || item.NetIndex < 0 || item.NetIndex > 255 || !item.SimulatedHere) return false;
            m_Requests.ItemDamageReport((byte)item.NetIndex, Mathf.Max(0, newValue));
            return true;
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            var items = m_Director.RunItems;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;
                var sync = item.GetComponent<ItemSync>();
                if (sync != null) sync.End();
            }
            if (m_Director.ItemClient == this) m_Director.ItemClient = null;
        }
    }
}

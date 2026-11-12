using System;
using System.Collections.Generic;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using static TrashAstronauts.Net.CoopConvert;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// Co-op stage 3 on the host: the game's side of "everyone grabs". Who may hold what is arbitrated by the
    /// session's ItemOwnershipTable (first to arrive wins, the host's own hand through the same table); this
    /// class answers the session's item questions with the scene and applies its decisions to the real items:
    ///  - CanGrabItem: the item exists, is loose (not mounted, not secured), nobody here holds it, and the
    ///    requester's head is within reach of it.
    ///  - OnItemGranted: the item is in that crew member's hand: RemoteHolder set (so it counts as held for the
    ///    locker, the bumpers and lost-item recovery), kinematic, following their streamed poses. A first
    ///    pickup is booked once (SalvageItem.SetRemoteHolder -> NotifySalvageCollected -> ItemCollected).
    ///  - OnItemReleased / OnItemThrown: out of the hand, still flown by them; the throw is registered from the
    ///    release data for trick-shot scoring (no recoil here: that stays on the thrower's machine).
    ///  - OnItemReturned: the host simulates it again, at the pose and velocity given (handoff) or at its last
    ///    streamed pose (backstop, owner Away or gone).
    ///  - OnItemDamageReport: a knock on the owner's machine, clamped (lower than now, never below a third of
    ///    full) and booked here, which the rules broadcast as ItemValue.
    /// The host player's own hands go through the session too (<see cref="ICoopBroadcast.LocalGrab"/> /
    /// <see cref="ICoopBroadcast.LocalRelease"/>) so the crew sees ItemOwner for them.
    /// Plain C#; the session adapter creates it next to CoopHostRules and disposes it with them.
    /// </summary>
    public sealed class CoopItemHost : IDisposable
    {
        readonly MissionDirector m_Director;
        readonly ICoopBroadcast m_Out;
        readonly List<SalvageItem> m_Hooked = new List<SalvageItem>();
        Vector3[] m_ReleasedAt = new Vector3[0];
        bool m_Disposed;

        /// <summary>A grab counts only from a head this close to the item (metres): an arm, plus where a thrown item may be on the host a moment later.</summary>
        public const float GrabReach = 4f;

        /// <summary>Grabs refused because the requester's head was too far away (a modified client, or a desync).</summary>
        public int TooFar { get; private set; }
        /// <summary>Damage reports refused (not lower than now, or an item nobody may damage) and clamped.</summary>
        public int DamageRefused { get; private set; }
        public int DamageClamped { get; private set; }
        /// <summary>The host's own grabs the session refused (someone else had it), cancelled without a release.</summary>
        public int LocalGrabsRefused { get; private set; }

        /// <summary>
        /// Seconds a datagram takes from a crew member to the host (half the session's measured round trip). Null or
        /// NaN: <see cref="DefaultOneWaySeconds"/>. NetSession sets it.
        /// </summary>
        public Func<byte, double> OneWayLatency;
        public const double DefaultOneWaySeconds = 0.05;
        /// <summary>How long the host's copy of a handed-back flying item takes to catch up with where it really is.</summary>
        public const float CatchUpSeconds = 0.3f;
        /// <summary>Hand-backs taken over with a catch-up (no snap) so far.</summary>
        public int CatchUps { get; private set; }

        public CoopItemHost(MissionDirector director, ICoopBroadcast broadcast)
        {
            m_Director = director ?? throw new ArgumentNullException(nameof(director));
            m_Out = broadcast ?? throw new ArgumentNullException(nameof(broadcast));
            director.MissionStarted += OnMissionStarted;
            if (director.Phase == MissionPhase.Active) HookRunItems();
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            m_Director.MissionStarted -= OnMissionStarted;
            UnhookRunItems();
        }

        SalvageItem Item(byte itemId)
        {
            if (m_Disposed) return null;
            var item = m_Director.RunItemAt(itemId);
            return item != null && item.NetIndex == itemId ? item : null;
        }

        // ------------------------------------------------------------------ IHostWorld (one method per call, same parameters)

        /// <summary>IHostWorld.CanGrabItem: may this item be picked up at all by this crew member?</summary>
        public bool CanGrabItem(byte itemId, byte slot)
        {
            var item = Item(itemId);
            if (item == null || m_Director.Phase != MissionPhase.Active) return false;
            if (item.IsSecured || item.IsMounted || item.isTraining) return false;
            // The host's own hand has it (the session knows too; this is the scene's word on it).
            if (item.grab != null && item.grab.isSelected) return false;
            var player = m_Director.FindPlayer(new PlayerId(slot));
            if (player == null || (player.HeadPosition - item.transform.position).sqrMagnitude > GrabReach * GrabReach)
            {
                TooFar++;
                return false;
            }
            return true;
        }

        /// <summary>IHostWorld.OnItemGranted: the crew member in <paramref name="slot"/> holds it now (the grant is already broadcast).</summary>
        public void OnItemGranted(byte itemId, byte slot, byte hand)
        {
            var item = Item(itemId);
            if (item == null) return;
            var replica = CoopItemReplica.For(item);
            replica.physicsDriven = true;
            replica.Seed(item.transform.position, item.transform.rotation, 0, 0);
            item.SetSimulatedHere(false);
            // Counts as held everywhere from now on; a first pickup is booked (and broadcast by the rules) once.
            item.SetRemoteHolder(new PlayerId(slot));
        }

        /// <summary>IHostWorld.OnItemPose: the owner's stream (stale epochs and non-owners already dropped).</summary>
        public void OnItemPose(byte itemId, byte slot, NetVec3 position, NetQuat rotation, bool held, double hostTime)
        {
            var item = Item(itemId);
            if (item == null || item.SimulatedHere || item.IsSecured) return;
            CoopItemReplica.For(item).Follow(ToUnity(position), ToUnity(rotation), held);
        }

        /// <summary>IHostWorld.OnItemReleased: out of their hand; they keep flying it until the handoff.</summary>
        public void OnItemReleased(byte itemId, byte slot, NetVec3 position, NetQuat rotation, NetVec3 releasePosition)
        {
            var item = Item(itemId);
            if (item == null) return;
            if (itemId < m_ReleasedAt.Length) m_ReleasedAt[itemId] = ToUnity(releasePosition);
            if (item.RemoteHolder.IsValid) item.SetRemoteHolder(PlayerId.None);
            if (!item.SimulatedHere && !item.IsSecured) CoopItemReplica.For(item).Follow(ToUnity(position), ToUnity(rotation), false);
        }

        /// <summary>
        /// IHostWorld.OnItemThrown: the release was a throw. Registered here from the release data, so a throw
        /// into the locker from across the cabin is a trick shot; there is no recoil (the proxy has no body to
        /// push, and the thrower's own machine already pushed them back).
        /// </summary>
        public void OnItemThrown(byte itemId, byte slot, NetVec3 linearVelocity, NetVec3 angularVelocity, float throwSpeed, bool wasThrown)
        {
            var item = Item(itemId);
            if (item == null || item.IsSecured || item.SimulatedHere) return;
            Vector3 releasedAt = itemId < m_ReleasedAt.Length ? m_ReleasedAt[itemId] : item.transform.position;
            item.RegisterThrow(ToUnity(linearVelocity), null, releasedAt);
        }

        /// <summary>
        /// IHostWorld.OnItemReturned: the host simulates the item again (handoff, 3 s backstop, owner Away or
        /// gone, host take-over). It may still have been in their hand as far as the host knew.
        /// </summary>
        public void OnItemReturned(in ItemReturn returned)
        {
            var item = Item(returned.itemId);
            if (item == null) return;
            if (item.RemoteHolder.IsValid) item.SetRemoteHolder(PlayerId.None);
            var replica = item.GetComponent<CoopItemReplica>();
            if (replica != null) replica.ClearSamples();
            if (item.IsSecured || item.IsMounted)
            {
                item.SetSimulatedHere(true);
                return;
            }
            // The host's own hand just took it out of a client's flight (LocalGrab): it is already where the hand has it.
            bool inHand = item.grab != null && item.grab.isSelected;
            Vector3 velocity = ToUnity(returned.linearVelocity);
            Vector3 catchUp = Vector3.zero;
            if (returned.hasPose && !inHand)
            {
                Vector3 position = ToUnity(returned.position);
                Quaternion rotation = ToUnity(returned.rotation);
                // A flight handed back while still moving: the pose is as old as the trip here, and the copy shown here
                // was drawn further behind still. Take over from where it is drawn and catch up with where it really is
                // now (the pose advanced by the trip) over a moment, so nobody sees it jump or go backwards.
                Vector3 drawn = item.transform.position;
                double oneWay = OneWayLatency != null ? OneWayLatency(returned.fromSlot) : double.NaN;
                if (double.IsNaN(oneWay) || oneWay < 0 || oneWay > 1.0) oneWay = DefaultOneWaySeconds;
                Vector3 now = position + velocity * (float)oneWay;
                Vector3 gap = now - drawn;
                bool flying = returned.cause == ItemReturnCause.Handoff && velocity.magnitude > 0.2f;
                if (flying && gap.magnitude < 3f && Vector3.Dot(gap, velocity) > -0.05f)
                {
                    position = drawn;
                    catchUp = gap / CatchUpSeconds;
                    CatchUps++;
                }
                item.transform.SetPositionAndRotation(position, rotation);
                item.Body.position = position;
                item.Body.rotation = rotation;
            }
            item.SetSimulatedHere(true);
            if (!inHand && !item.Body.isKinematic)
            {
                item.Body.linearVelocity = velocity + catchUp;
                item.Body.angularVelocity = ToUnity(returned.angularVelocity);
                if (catchUp != Vector3.zero) HandbackCatchUp.Begin(item.Body, catchUp, CatchUpSeconds);
            }
        }

        /// <summary>
        /// IHostWorld.OnItemDamageReport: the owner's machine says the item took a knock. Accepted only as a lower
        /// value, clamped to no less than a third of full; booked here, and the rules broadcast it as ItemValue.
        /// Returns true if the item's value changed.
        /// </summary>
        public bool OnItemDamageReport(byte itemId, byte slot, int newValue)
        {
            var item = Item(itemId);
            if (item == null || item.IsSecured || item.isTraining || m_Director.Phase != MissionPhase.Active)
            {
                DamageRefused++;
                return false;
            }
            int full = item.FullValue;
            int floor = Mathf.CeilToInt(full / 3f);
            int value = Mathf.Clamp(newValue, floor, full);
            if (value != newValue) DamageClamped++;
            if (value >= item.Value)
            {
                DamageRefused++;
                return false;
            }
            item.SetRemoteValue(value);
            m_Director.ReportSalvageDamage(item, value);
            return true;
        }

        // ------------------------------------------------------------------ the host's own hands

        void OnMissionStarted() => HookRunItems();

        void HookRunItems()
        {
            UnhookRunItems();
            var items = m_Director.RunItems;
            m_ReleasedAt = new Vector3[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.grab == null) continue;
                item.grab.selectEntered.AddListener(OnHostSelected);
                item.Released += OnHostReleased;
                m_Hooked.Add(item);
            }
        }

        void UnhookRunItems()
        {
            foreach (var item in m_Hooked)
            {
                if (item == null) continue;
                if (item.grab != null) item.grab.selectEntered.RemoveListener(OnHostSelected);
                item.Released -= OnHostReleased;
            }
            m_Hooked.Clear();
        }

        bool Hosting => !m_Disposed && m_Director.Net != null && m_Director.Net.IsHost;

        void OnHostSelected(SelectEnterEventArgs args)
        {
            if (!Hosting) return;
            var grab = args.interactableObject as XRGrabInteractable;
            if (grab == null || grab.interactorsSelecting.Count > 1) return;
            var item = grab.GetComponent<SalvageItem>();
            if (item == null || item.NetIndex < 0 || item.NetIndex > 255) return;
            // The moment the hand closes: this is how the host wins a tie.
            if (m_Out.LocalGrab((byte)item.NetIndex)) return;
            LocalGrabsRefused++;
            if (grab.interactionManager == null) return;
            item.NetSuppressRelease = true;
            grab.interactionManager.CancelInteractableSelection((IXRSelectInteractable)grab);
        }

        void OnHostReleased(SalvageItem item)
        {
            if (!Hosting || item == null || item.NetIndex < 0 || item.NetIndex > 255) return;
            m_Out.LocalRelease((byte)item.NetIndex);
        }
    }
}

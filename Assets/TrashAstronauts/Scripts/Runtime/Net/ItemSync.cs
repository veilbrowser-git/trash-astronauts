using System.Collections;
using System.Collections.Generic;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using static TrashAstronauts.Net.CoopConvert;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// On a co-op client, one salvage item's side of "everyone grabs" (co-op stage 3, plan 4.2). It owns the
    /// item's <see cref="ItemAuthority"/> (HostFree / Mine / RemoteHeld and the hand-back timer) and does what
    /// it says:
    ///  - A local hand closes on it: it is already in the hand (SalvageGrabInteractable made it simulated here
    ///    this frame). The item becomes Mine at once and a GrabRequest goes out.
    ///  - While Mine its pose goes into the stream every frame, moving or not.
    ///  - The host names someone else: the hold is cancelled without a release (no throw, no recoil, no
    ///    ItemReleased), the hand buzzes twice, and the item follows that player's hand.
    ///  - The last local hand lets go: ItemReleased at once, ItemThrown once the game has registered the
    ///    throw. This machine keeps flying it (at most 1.5 s, or until slow for 0.3 s), then ItemHandoff, and
    ///    when the host confirms it glides back onto the host's stream.
    /// Created by <see cref="CoopItemClient"/> when a contract starts; never present offline or on a host.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ItemSync : MonoBehaviour
    {
        SalvageItem m_Item;
        CoopItemClient m_Client;
        bool m_Hooked;

        public ItemAuthority Authority { get; private set; }
        public byte ItemId { get; private set; }
        public SalvageItem Item => m_Item;
        public bool Active => m_Client != null && Authority != null;

        /// <summary>How many denials this item buzzed for (tests).</summary>
        public int Denials { get; private set; }
        /// <summary>The last ownership event and when (local clock).</summary>
        public ItemAuthorityEvent LastEvent { get; private set; }

        /// <summary>Amplitude and length of each of the two "no" pulses, and the gap between them.</summary>
        public const float DenyPulse = 0.5f, DenyPulseSeconds = 0.05f, DenyGapSeconds = 0.11f;

        public static ItemSync For(SalvageItem item)
        {
            var sync = item.GetComponent<ItemSync>();
            if (sync == null) sync = item.gameObject.AddComponent<ItemSync>();
            sync.m_Item = item;
            return sync;
        }

        /// <summary>A contract started: host-simulated, free, epoch 0.</summary>
        public void Begin(CoopItemClient client, byte itemId, byte localSlot)
        {
            m_Client = client;
            ItemId = itemId;
            if (m_Item == null) m_Item = GetComponent<SalvageItem>();
            if (Authority == null || Authority.ItemId != itemId) Authority = new ItemAuthority(itemId, localSlot);
            else Authority.Reset(localSlot);
            Denials = 0;
            LastEvent = ItemAuthorityEvent.None;
            Hook();
        }

        /// <summary>The session is over: this item is an ordinary local one again (MissionDirector restores its body).</summary>
        public void End()
        {
            Unhook();
            m_Client = null;
        }

        void OnEnable() { if (m_Client != null) Hook(); }
        void OnDisable() => Unhook();
        void OnDestroy() => Unhook();

        void Hook()
        {
            if (m_Hooked || m_Item == null || m_Item.grab == null) return;
            m_Item.grab.selectEntered.AddListener(OnSelectEntered);
            m_Item.Released += OnReleased;
            m_Item.Thrown += OnThrown;
            m_Hooked = true;
        }

        void Unhook()
        {
            if (!m_Hooked) return;
            m_Hooked = false;
            if (m_Item == null) return;
            if (m_Item.grab != null) m_Item.grab.selectEntered.RemoveListener(OnSelectEntered);
            m_Item.Released -= OnReleased;
            m_Item.Thrown -= OnThrown;
        }

        double Now => m_Client != null ? m_Client.Now : Time.unscaledTimeAsDouble;
        ICoopItemRequests Requests => m_Client != null ? m_Client.Requests : null;

        // ------------------------------------------------------------------ local hands

        void OnSelectEntered(SelectEnterEventArgs args)
        {
            if (!Active || m_Item == null) return;
            // A second hand on a heavy item, or a pass between hands that XRI does as one selection: same hold.
            if (m_Item.grab.interactorsSelecting.Count > 1) return;
            if (!Authority.BeginGrab(out byte epochSeen))
            {
                // Not ours to take (secured, or someone else's hand got there in the same frame): never held.
                CancelLocalHold(false);
                ApplyBodyMode(false);
                return;
            }
            var hand = args.interactorObject != null ? args.interactorObject.transform.GetComponentInParent<HandRig>() : null;
            byte handByte = hand != null && hand.side == Handedness.Left ? (byte)0 : (byte)1;
            ApplyBodyMode(false);
            Requests?.GrabRequest(ItemId, epochSeen, handByte);
        }

        void OnReleased(SalvageItem item)
        {
            if (!Active) return;
            if (!Authority.Release(Now)) return;
            Requests?.ItemReleased(ItemId, Authority.WireEpoch, ToNet(transform.position), ToNet(transform.rotation), ToNet(item.ReleasePosition));
        }

        void OnThrown(SalvageItem item)
        {
            if (!Active || Authority.Mode != ItemMode.Mine || Authority.HeldLocally || Authority.HandoffSent) return;
            var body = item.Body;
            Requests?.ItemThrown(ItemId, Authority.WireEpoch, ToNet(body.linearVelocity), ToNet(body.angularVelocity), item.LastThrowSpeed, item.WasThrown);
        }

        void Update()
        {
            if (!Active || m_Item == null) return;
            if (m_Item.IsSecured && !Authority.Secured) OnDeposited();
            var body = m_Item.Body;
            float speed = body != null && !body.isKinematic ? body.linearVelocity.magnitude : 0f;
            if (Authority.Tick(Now, speed, out byte epoch))
            {
                Requests?.ItemHandoff(ItemId, epoch, ToNet(transform.position), ToNet(transform.rotation),
                    ToNet(body != null ? body.linearVelocity : Vector3.zero), ToNet(body != null ? body.angularVelocity : Vector3.zero));
            }
        }

        void LateUpdate()
        {
            // Every frame while it is simulated here, held still or not: the host takes back an item whose
            // holder has streamed nothing for 3 s. The session sends the newest one each tick.
            if (!Active || m_Item == null || !Authority.ShouldStream) return;
            Requests?.QueueItemPose(ItemId, ToNet(transform.position), ToNet(transform.rotation), Authority.WireEpoch, Authority.HeldLocally);
        }

        // ------------------------------------------------------------------ the host's word

        /// <summary>ItemOwner from the host. Returns what it meant here.</summary>
        public ItemAuthorityEvent ApplyOwner(byte ownerSlot, byte epoch, bool held, bool denied)
        {
            if (!Active) return ItemAuthorityEvent.None;
            bool wasSimulated = m_Item != null && m_Item.SimulatedHere;
            var e = Authority.OnOwner(ownerSlot, epoch, held, denied, Now);
            LastEvent = e;
            switch (e)
            {
                case ItemAuthorityEvent.Denied:
                    Denials++;
                    CancelLocalHold(true);
                    break;
                case ItemAuthorityEvent.Revoked:
                    // Taken back while ours (we went Away, the backstop, or someone caught it): no release here.
                    CancelLocalHold(false);
                    break;
                case ItemAuthorityEvent.GrantedNotHeld:
                    // Nobody's hand is on it here: say so at once, then the hand-back timer returns it.
                    Requests?.ItemReleased(ItemId, Authority.WireEpoch, ToNet(transform.position), ToNet(transform.rotation), ToNet(transform.position));
                    break;
            }
            ApplyBodyMode(wasSimulated);
            return e;
        }

        /// <summary>A pose for this item may move it here (not simulated here, current epoch).</summary>
        public bool AcceptPose(byte epoch6) => !Active || Authority.AcceptPose(epoch6);

        /// <summary>An ItemRest for this item may move it here.</summary>
        public bool AcceptRest(byte epoch) => !Active || Authority.AcceptRest(epoch);

        /// <summary>The host's locker took it: it is nobody's to hold or fly any more.</summary>
        public void OnDeposited()
        {
            if (Authority == null) return;
            Authority.OnDeposited();
            if (m_Item == null) return;
            m_Item.SetRemoteHolder(PlayerId.None);
            m_Item.SetSimulatedHere(false);
        }

        /// <summary>Makes the body match the mode: simulated here (Mine), or a replica following someone else's stream.</summary>
        void ApplyBodyMode(bool wasSimulated)
        {
            if (m_Item == null || Authority == null || m_Item.IsSecured) return;
            switch (Authority.Mode)
            {
                case ItemMode.Mine:
                    if (m_Item.RemoteHolder.IsValid) m_Item.SetRemoteHolder(PlayerId.None);
                    if (!m_Item.SimulatedHere) m_Item.SetSimulatedHere(true);
                    var replica = m_Item.GetComponent<CoopItemReplica>();
                    if (replica != null) replica.ClearSamples();
                    break;
                case ItemMode.RemoteHeld:
                case ItemMode.HostFree:
                    if (m_Item.SimulatedHere)
                    {
                        m_Item.SetSimulatedHere(false);
                        if (wasSimulated) CoopItemReplica.For(m_Item).Seed(transform.position, transform.rotation, Authority.Epoch);
                    }
                    var holder = Authority.Mode == ItemMode.RemoteHeld ? new PlayerId(Authority.OwnerSlot) : PlayerId.None;
                    if (m_Item.RemoteHolder != holder) m_Item.SetRemoteHolder(holder);
                    break;
            }
        }

        readonly List<HandRig> m_Hands = new List<HandRig>(2);

        /// <summary>Takes the item out of the local hands without it counting as a release.</summary>
        void CancelLocalHold(bool buzz)
        {
            var grab = m_Item != null ? m_Item.grab : null;
            if (grab == null || !grab.isSelected) return;
            m_Hands.Clear();
            for (int i = 0; i < grab.interactorsSelecting.Count; i++)
            {
                var interactor = grab.interactorsSelecting[i];
                var hand = interactor != null ? interactor.transform.GetComponentInParent<HandRig>() : null;
                if (hand != null && !m_Hands.Contains(hand)) m_Hands.Add(hand);
            }
            if (grab.interactionManager != null)
            {
                m_Item.NetSuppressRelease = true;
                grab.interactionManager.CancelInteractableSelection((IXRSelectInteractable)grab);
            }
            if (buzz && m_Hands.Count > 0 && isActiveAndEnabled) StartCoroutine(DoubleBuzz(m_Hands.ToArray()));
        }

        static IEnumerator DoubleBuzz(HandRig[] hands)
        {
            foreach (var hand in hands) if (hand != null) hand.Pulse(DenyPulse, DenyPulseSeconds);
            float until = Time.unscaledTime + DenyGapSeconds;
            while (Time.unscaledTime < until) yield return null;
            foreach (var hand in hands) if (hand != null) hand.Pulse(DenyPulse, DenyPulseSeconds);
        }
    }
}

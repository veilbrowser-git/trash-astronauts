using System;
using System.Collections;
using TrashAstronauts.Core;
using TrashAstronauts.Player;
using TrashAstronauts.UI;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// A physical piece of salvage. Holds its stable id and definition, reports first pickup to the
    /// mission, and knows how to be clamped to a mount, scanned, recovered, and secured in the locker.
    /// The rules-side truth (deposited or not, value) lives in MissionSession, keyed by SalvageId.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class SalvageItem : MonoBehaviour
    {
        public SalvageDefinition definition;
        public XRGrabInteractable grab;
        public HoverHighlight highlight;
        public ScanTag scanTag;
        public ImpactNoiseEmitter impact;
        [Tooltip("Training items teach the locker in the tutorial and are worth nothing.")]
        public bool isTraining;

        Rigidbody m_Body;
        MissionDirector m_Director;
        Cuttable m_Mount;
        Collider[] m_Colliders;

        public string SalvageId { get; private set; }
        public PlayerId LastHolder { get; private set; } = PlayerId.None;
        public bool IsSecured { get; private set; }
        public bool IsMounted { get; private set; }
        public Vector3 SpawnPosition { get; private set; }
        public bool HasBeenCollected { get; private set; }
        /// <summary>In a hand: this headset's, or (in co-op) another crew member's.</summary>
        public bool IsHeld => (grab != null && grab.isSelected) || RemoteHolder.IsValid;
        public Rigidbody Body => m_Body;

        /// <summary>This run's net id: the item's index in RunStartParams.items. -1 for anything else (the tutorial's practice item).</summary>
        public int NetIndex { get; set; } = -1;
        /// <summary>
        /// The crew member on another machine who is holding this, or <see cref="PlayerId.None"/>. While
        /// set the item counts as held everywhere (the locker, the bumpers and lost-item recovery leave it
        /// alone) and its body just follows the poses that arrive. Always None offline.
        /// </summary>
        public PlayerId RemoteHolder { get; private set; } = PlayerId.None;
        /// <summary>
        /// True on the one machine whose physics moves this body right now. Only that machine turns its
        /// knocks into damage and gameplay noise; everywhere else the body is kinematic and follows.
        /// Always true offline.
        /// </summary>
        public bool SimulatedHere { get; private set; } = true;
        /// <summary>
        /// Set just before a grab is cancelled from outside (the host gave the item to someone else): the
        /// release that follows is not a real one, so no throw, no recoil and no <see cref="Released"/>.
        /// Cleared by that release.
        /// </summary>
        public bool NetSuppressRelease { get; set; }

        /// <summary>The last hand let go (not raised when it is passed from hand to hand, or for a suppressed release).</summary>
        public event Action<SalvageItem> Released;
        /// <summary>Two physics steps after a release, once the throw is known: read WasThrown, LastThrowSpeed, ReleasePosition and the body's velocity.</summary>
        public event Action<SalvageItem> Thrown;

        /// <summary>
        /// A crew member on another machine took this in hand (or, with <see cref="PlayerId.None"/>, let
        /// go). Taking it leaves the same traces a grab on this headset does: they are the last holder
        /// (so a deposit, the locker's buzz and the item's knocks are theirs), any earlier throw is
        /// forgotten, and the item counts as collected, which on a host also goes into the ledger.
        /// </summary>
        public void SetRemoteHolder(PlayerId holder)
        {
            RemoteHolder = holder;
            // Nothing clamped to a mount or locked in the locker is in anybody's hand.
            if (holder.IsValid && !IsMounted && !IsSecured)
            {
                LastHolder = holder;
                WasThrown = false;
                if (impact != null) impact.LastHandler = holder;
                if (!HasBeenCollected && !isTraining)
                {
                    HasBeenCollected = true;
                    // A client's director ignores this (its ledger is a mirror); a host's records it once.
                    if (m_Director != null) m_Director.NotifySalvageCollected(this, holder);
                }
            }
            RefreshBodyMode();
        }

        public void SetSimulatedHere(bool simulatedHere)
        {
            SimulatedHere = simulatedHere;
            RefreshBodyMode();
        }

        /// <summary>
        /// True on a co-op client whose session does not share grabbing (no item sync): the item cannot be
        /// grabbed here. With item sync (co-op stage 3) every crew member grabs, so this stays false; always
        /// false offline and on a host.
        /// </summary>
        public bool HostOnly { get; private set; }

        public void SetHostOnly(bool hostOnly)
        {
            HostOnly = hostOnly;
            RefreshBodyMode();
        }

        /// <summary>Raised after <see cref="RecoverTo"/> brought the item back (a co-op host tells the crew where to).</summary>
        public event Action<SalvageItem> Recovered;

        /// <summary>The host's word on what this item is worth now (after a knock on someone else's machine).</summary>
        public void SetRemoteValue(int value)
        {
            value = Mathf.Max(0, value);
            m_DamagedValue = value >= FullValue ? -1 : value;
        }

        /// <summary>
        /// The one place that decides whether the body is kinematic and whether it can be grabbed, from
        /// everything that has a say: clamped to a mount, secured in the locker, held on another machine,
        /// or simulated somewhere else. A copy that another machine simulates is also not interpolated: it is
        /// placed exactly where the stream says, and when this machine takes it over (a hand closes on it) the
        /// physics starts from where it is shown, not from a stale interpolation pose. Offline (always
        /// simulated here) the body keeps the interpolation it was built with.
        /// </summary>
        public void RefreshBodyMode()
        {
            if (m_Body == null) m_Body = GetComponent<Rigidbody>();
            if (!m_InterpolationKnown)
            {
                m_Interpolation = m_Body.interpolation;
                m_InterpolationKnown = true;
            }
            // Grab first: switching it off drops the item from a hand, and that drop restores the body's
            // own idea of kinematic. What is decided here has to be the last word.
            if (grab != null) grab.enabled = !IsMounted && !IsSecured && !HostOnly;
            bool wasKinematic = m_Body.isKinematic;
            bool kinematic = IsMounted || IsSecured || RemoteHolder.IsValid || !SimulatedHere;
            if (wasKinematic && !kinematic && m_Body.interpolation == RigidbodyInterpolation.None && m_Interpolation != RigidbodyInterpolation.None)
            {
                // Leaving a replica's life: the body starts exactly where it was placed.
                m_Body.position = transform.position;
                m_Body.rotation = transform.rotation;
            }
            m_Body.isKinematic = kinematic;
            var interpolation = SimulatedHere ? m_Interpolation : RigidbodyInterpolation.None;
            if (m_Body.interpolation != interpolation) m_Body.interpolation = interpolation;
        }

        RigidbodyInterpolation m_Interpolation;
        bool m_InterpolationKnown;

        /// <summary>What it is worth now. Delicate things lose value when they are banged about.</summary>
        public int Value => m_DamagedValue >= 0 ? m_DamagedValue : FullValue;
        public int FullValue => m_BaseValue >= 0 ? m_BaseValue : (definition != null ? definition.creditValue : 0);
        int m_BaseValue = -1;

        /// <summary>What it is worth undamaged on this contract (a rich wreck pays more than list price).</summary>
        public void SetBaseValue(int value) => m_BaseValue = Mathf.Max(0, value);
        public int DamageCount { get; private set; }
        int m_DamagedValue = -1;

        /// <summary>
        /// Called when it hits something. A hard enough knock costs a delicate item some of its value,
        /// down to a third of what it started at. Scrap does not care.
        /// </summary>
        public int ApplyImpact(float speed)
        {
            if (definition == null || definition.fragility <= 0f || IsSecured || isTraining) return 0;
            var director = m_Director != null ? m_Director : MissionDirector.Active;
            if (director == null || director.Session == null || director.Phase != MissionPhase.Active) return 0;
            const float safeSpeed = 2.0f, wreckingSpeed = 7f;
            float severity = Mathf.Clamp01((speed - safeSpeed) / (wreckingSpeed - safeSpeed));
            if (severity <= 0f) return 0;

            int full = FullValue;
            int floor = Mathf.CeilToInt(full / 3f);
            int loss = Mathf.Max(1, Mathf.RoundToInt(full * definition.fragility * 0.35f * (0.3f + 0.7f * severity)));
            int now = Mathf.Max(floor, Value - loss);
            loss = Value - now;
            if (loss <= 0) return 0;
            m_DamagedValue = now;
            DamageCount++;
            director.ReportSalvageDamage(this, now);
            if (scanTag != null) scanTag.Show(definition.displayName + "  DAMAGED  -" + loss, now, 2.5f);
            return loss;
        }

        void Awake()
        {
            m_Body = GetComponent<Rigidbody>();
            if (!m_InterpolationKnown)
            {
                m_Interpolation = m_Body.interpolation;
                m_InterpolationKnown = true;
            }
            m_Colliders = GetComponentsInChildren<Collider>(true);
            if (grab == null) grab = GetComponent<XRGrabInteractable>();
            SpawnPosition = transform.position;
        }

        void OnEnable()
        {
            if (grab != null)
            {
                grab.selectEntered.AddListener(OnGrabbed);
                grab.selectExited.AddListener(OnReleased);
            }
        }

        void OnDisable()
        {
            if (grab != null)
            {
                grab.selectEntered.RemoveListener(OnGrabbed);
                grab.selectExited.RemoveListener(OnReleased);
            }
        }

        /// <summary>Where the item was when it was last let go, and whether it was thrown rather than placed.</summary>
        public Vector3 ReleasePosition { get; private set; }
        public bool WasThrown { get; private set; }
        public float LastThrowSpeed { get; private set; }

        void OnReleased(SelectExitEventArgs args)
        {
            if (grab.isSelected) return; // passed to the other hand
            if (NetSuppressRelease)
            {
                // The grab was taken back by the session, not let go: nothing was thrown.
                NetSuppressRelease = false;
                return;
            }
            ReleasePosition = transform.position;
            WasThrown = false;
            var player = PlayerContext.From(args.interactorObject);
            Released?.Invoke(this);
            // Not while the object is being torn down (scene unload, end of a run).
            if (isActiveAndEnabled && gameObject.activeInHierarchy && gameObject.scene.isLoaded) StartCoroutine(AfterRelease(player));
        }

        IEnumerator AfterRelease(PlayerContext player)
        {
            // XRI applies the throw velocity as it detaches; read it once physics has it.
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            if (IsHeld || IsSecured || m_Body.isKinematic) yield break;
            RegisterThrow(m_Body.linearVelocity, player);
            Thrown?.Invoke(this);
        }

        /// <summary>
        /// Records a throw and kicks the thrower back the other way (momentum is conserved: hurling the
        /// 14 kg reactor core shoves you noticeably, tossing a scrap plate barely at all).
        /// </summary>
        public void RegisterThrow(Vector3 velocity, PlayerContext player, Vector3? releasedAt = null)
        {
            if (releasedAt.HasValue) ReleasePosition = releasedAt.Value;
            LastThrowSpeed = velocity.magnitude;
            WasThrown = LastThrowSpeed > 0.8f;
            if (!WasThrown || player == null || player.locomotion == null || player.locomotion.zeroG == null) return;
            var zeroG = player.locomotion.zeroG;
            // Momentum is conserved in the thrower's own frame: what the item gained relative to the body
            // that threw it comes back as body speed the other way, against the body plus whatever the
            // hands still hold (the thrown item has already left them). Arm limits on throw speed
            // (RealPhysics.MaxThrowSpeed) keep this within reason; the clamp is only a safety net.
            Vector3 relative = velocity - zeroG.Velocity;
            Vector3 recoil = -relative * (m_Body.mass / Mathf.Max(20f, zeroG.TotalMass));
            zeroG.AddImpulse(Vector3.ClampMagnitude(recoil, 2.5f));
        }

        /// <summary>Called by the mission director right after spawning.</summary>
        public void Initialize(MissionDirector director, string salvageId, SalvageDefinition def, Cuttable mount)
        {
            m_Director = director;
            m_DamagedValue = -1;
            m_BaseValue = -1;
            DamageCount = 0;
            NetIndex = -1;
            RemoteHolder = PlayerId.None;
            SimulatedHere = true;
            HostOnly = false;
            NetSuppressRelease = false;
            SalvageId = salvageId;
            if (def != null) definition = def;
            SpawnPosition = transform.position;
            name = "Salvage " + salvageId;
            if (m_Body == null) m_Body = GetComponent<Rigidbody>();
            if (definition != null) m_Body.mass = definition.mass;

            m_Mount = mount;
            SetMounted(mount != null && !mount.IsOpen);
            if (mount != null) mount.Opened += OnMountOpened;
        }

        void OnDestroy()
        {
            if (m_Mount != null) m_Mount.Opened -= OnMountOpened;
        }

        void OnMountOpened(Cuttable mount)
        {
            if (!IsMounted) return;
            SetMounted(false);
            // A gentle shove so the freed part visibly drifts out of its bracket. Only where its physics runs.
            if (SimulatedHere) m_Body.AddForce(mount.transform.forward * 0.25f, ForceMode.VelocityChange);
        }

        void SetMounted(bool mounted)
        {
            IsMounted = mounted;
            RefreshBodyMode();
        }

        void OnGrabbed(SelectEnterEventArgs args)
        {
            var player = PlayerContext.From(args.interactorObject);
            var director = m_Director != null ? m_Director : MissionDirector.Active;
            LastHolder = player != null ? player.Id : director != null ? director.LocalPlayerId : PlayerId.Local;
            WasThrown = false;
            if (impact != null) impact.LastHandler = LastHolder;
            if (scanTag != null && definition != null) scanTag.Show(definition.displayName, Value, 2.5f);

            if (!HasBeenCollected && !isTraining)
            {
                HasBeenCollected = true;
                if (m_Director != null) m_Director.NotifySalvageCollected(this, LastHolder);
            }
        }

        /// <summary>Scanner hit: pulse and show the value tag.</summary>
        public void ShowScan(float seconds)
        {
            if (highlight != null) highlight.Pulse(seconds);
            if (scanTag != null && definition != null)
                scanTag.Show(definition.displayName, definition.creditValue, seconds);
        }

        /// <summary>Brings a lost item back into the playable area with no velocity.</summary>
        public void RecoverTo(Vector3 position)
        {
            if (IsSecured || IsMounted) return;
            m_Body.linearVelocity = Vector3.zero;
            m_Body.angularVelocity = Vector3.zero;
            m_Body.position = position;
            transform.position = position;
            Recovered?.Invoke(this);
        }

        /// <summary>Locks the item into a locker slot. It can no longer be grabbed or moved this run.</summary>
        public void Secure(Transform slot)
        {
            if (IsSecured) return;
            IsSecured = true;
            RemoteHolder = PlayerId.None;
            RefreshBodyMode();
            if (highlight != null) highlight.StopPulse();
            foreach (var c in m_Colliders)
            {
                if (c != null) c.enabled = false;
            }

            if (slot == null) return;
            transform.SetParent(slot, true);
            if (isActiveAndEnabled) StartCoroutine(SettleIntoSlot());
            else SnapIntoSlot();
        }

        float SlotScale => definition != null && definition.bulky ? 0.45f : 0.7f;

        void SnapIntoSlot()
        {
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            transform.localScale *= SlotScale;
        }

        IEnumerator SettleIntoSlot()
        {
            Vector3 startPos = transform.localPosition;
            Quaternion startRot = transform.localRotation;
            Vector3 startScale = transform.localScale;
            Vector3 endScale = startScale * SlotScale;
            float t = 0f;
            const float duration = 0.35f;
            while (t < duration)
            {
                t += Time.deltaTime;
                float k = Mathf.SmoothStep(0f, 1f, t / duration);
                transform.localPosition = Vector3.Lerp(startPos, Vector3.zero, k);
                transform.localRotation = Quaternion.Slerp(startRot, Quaternion.identity, k);
                transform.localScale = Vector3.Lerp(startScale, endScale, k);
                yield return null;
            }
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            transform.localScale = endScale;
        }
    }
}

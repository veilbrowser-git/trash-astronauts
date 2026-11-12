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
        public bool IsHeld => grab != null && grab.isSelected;
        public Rigidbody Body => m_Body;
        public int Value => definition != null ? definition.creditValue : 0;

        void Awake()
        {
            m_Body = GetComponent<Rigidbody>();
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
            ReleasePosition = transform.position;
            WasThrown = false;
            var player = PlayerContext.From(args.interactorObject);
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
            var director = m_Director != null ? m_Director : MissionDirector.Active;
            float playerMass = director != null ? Mathf.Max(20f, director.Config.playerMass) : 90f;
            Vector3 recoil = -velocity * (m_Body.mass / playerMass);
            player.locomotion.zeroG.AddImpulse(Vector3.ClampMagnitude(recoil, 1.2f));
        }

        /// <summary>Called by the mission director right after spawning.</summary>
        public void Initialize(MissionDirector director, string salvageId, SalvageDefinition def, Cuttable mount)
        {
            m_Director = director;
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
            // A gentle shove so the freed part visibly drifts out of its bracket.
            m_Body.AddForce(mount.transform.forward * 0.25f, ForceMode.VelocityChange);
        }

        void SetMounted(bool mounted)
        {
            IsMounted = mounted;
            m_Body.isKinematic = mounted;
            if (grab != null) grab.enabled = !mounted && !IsSecured;
        }

        void OnGrabbed(SelectEnterEventArgs args)
        {
            var player = PlayerContext.From(args.interactorObject);
            LastHolder = player != null ? player.Id : PlayerId.Local;
            WasThrown = false;
            if (impact != null) impact.LastHandler = LastHolder;
            if (scanTag != null && definition != null) scanTag.Show(definition.displayName, definition.creditValue, 2.5f);

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
            if (scanTag != null && definition != null) scanTag.Show(definition.displayName, definition.creditValue, seconds);
        }

        /// <summary>Brings a lost item back into the playable area with no velocity.</summary>
        public void RecoverTo(Vector3 position)
        {
            if (IsSecured || IsMounted) return;
            m_Body.linearVelocity = Vector3.zero;
            m_Body.angularVelocity = Vector3.zero;
            m_Body.position = position;
            transform.position = position;
        }

        /// <summary>Locks the item into a locker slot. It can no longer be grabbed or moved this run.</summary>
        public void Secure(Transform slot)
        {
            if (IsSecured) return;
            IsSecured = true;
            if (grab != null) grab.enabled = false;
            if (highlight != null) highlight.StopPulse();
            m_Body.isKinematic = true;
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

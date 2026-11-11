using TrashAstronauts.Audio;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace TrashAstronauts.Tools
{
    /// <summary>
    /// Base for the three essential suit tools. A tool lives in a holster on the player's body, can be
    /// drawn with either hand, and can never be lost: let go near the holster and it docks, let go
    /// anywhere else and it floats for a few seconds and then reels itself back in. Reaching for an
    /// empty holster and gripping pulls the tool straight into that hand.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public abstract class SuitTool : MonoBehaviour
    {
        public string toolName = "Tool";
        public XRGrabInteractable grab;
        public ToolHolster holster;
        [Tooltip("Releasing within this distance of the holster docks the tool immediately.")]
        public float holsterSnapRadius = 0.32f;
        [Tooltip("A dropped tool floats this long before reeling itself back to the holster.")]
        public float autoRecallSeconds = 8f;
        public float recallDistance = 5f;

        Rigidbody m_Body;
        bool m_DockRequested;
        float m_FloatTimer;

        public bool IsDocked { get; private set; }
        public bool IsHeld => grab != null && grab.isSelected;
        public bool TriggerHeld { get; private set; }
        public HandRig HoldingHand { get; private set; }
        protected Rigidbody Body => m_Body;

        protected virtual void Awake()
        {
            m_Body = GetComponent<Rigidbody>();
            if (grab == null) grab = GetComponent<XRGrabInteractable>();
        }

        protected virtual void OnEnable()
        {
            if (grab == null) return;
            grab.selectEntered.AddListener(OnSelectEntered);
            grab.selectExited.AddListener(OnSelectExited);
            grab.activated.AddListener(OnActivated);
            grab.deactivated.AddListener(OnDeactivated);
        }

        protected virtual void OnDisable()
        {
            if (grab == null) return;
            grab.selectEntered.RemoveListener(OnSelectEntered);
            grab.selectExited.RemoveListener(OnSelectExited);
            grab.activated.RemoveListener(OnActivated);
            grab.deactivated.RemoveListener(OnDeactivated);
        }

        protected virtual void Start()
        {
            if (holster != null) Dock(false);
        }

        void OnSelectEntered(SelectEnterEventArgs args)
        {
            IsDocked = false;
            m_DockRequested = false;
            m_FloatTimer = 0f;
            HoldingHand = PlayerContext.HandFrom(args.interactorObject);
            OnDrawn();
        }

        void OnSelectExited(SelectExitEventArgs args)
        {
            if (grab.isSelected) return; // still held by the other hand
            if (TriggerHeld)
            {
                TriggerHeld = false;
                OnTriggerUp();
            }
            HoldingHand = null;
            if (holster != null && Vector3.Distance(transform.position, holster.SocketPosition) <= holsterSnapRadius)
                m_DockRequested = true;
            OnReleased();
        }

        void OnActivated(ActivateEventArgs args)
        {
            if (!IsHeld || TriggerHeld) return;
            TriggerHeld = true;
            OnTriggerDown();
        }

        void OnDeactivated(DeactivateEventArgs args)
        {
            if (!TriggerHeld) return;
            TriggerHeld = false;
            OnTriggerUp();
        }

        protected virtual void Update()
        {
            float dt = Time.deltaTime;

            if (IsHeld)
            {
                Tick(dt);
                return;
            }

            if (m_DockRequested) Dock(true);

            if (IsDocked)
            {
                if (!m_Body.isKinematic) m_Body.isKinematic = true;
            }
            else
            {
                if (m_Body.isKinematic) m_Body.isKinematic = false;
                m_FloatTimer += dt;
                if (holster != null && ShouldAutoRecall()) Dock(true);
            }

            Tick(dt);
        }

        bool ShouldAutoRecall()
        {
            if (m_FloatTimer >= autoRecallSeconds) return true;
            if (Vector3.Distance(transform.position, holster.SocketPosition) > recallDistance) return true;
            var director = MissionDirector.Active;
            return director != null && director.bounds != null && !director.bounds.Contains(transform.position);
        }

        /// <summary>Returns the tool to its holster. Does nothing while it is in a hand.</summary>
        public void Recall()
        {
            if (!IsHeld) Dock(true);
        }

        void Dock(bool withFeedback)
        {
            m_DockRequested = false;
            m_FloatTimer = 0f;
            if (holster == null || holster.socket == null) return;

            if (!m_Body.isKinematic)
            {
                m_Body.linearVelocity = Vector3.zero;
                m_Body.angularVelocity = Vector3.zero;
            }
            m_Body.isKinematic = true;
            transform.SetParent(holster.socket, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            bool wasDocked = IsDocked;
            IsDocked = true;

            if (withFeedback && !wasDocked && Sfx.Library != null)
                Sfx.PlayAt(Sfx.Library.toolHolster, transform.position, 0.5f, 1f, 5f);
            OnDocked();
        }

        /// <summary>Moves the tool to a hand position ready to be force-selected by the holster.</summary>
        internal void PrepareHandOver(Vector3 position, Quaternion rotation)
        {
            IsDocked = false;
            m_DockRequested = false;
            m_FloatTimer = 0f;
            transform.SetParent(null, true);
            transform.SetPositionAndRotation(position, rotation);
        }

        protected virtual void Tick(float dt) { }
        protected virtual void OnDrawn() { }
        protected virtual void OnReleased() { }
        protected virtual void OnDocked() { }
        protected virtual void OnTriggerDown() { }
        protected virtual void OnTriggerUp() { }
    }
}

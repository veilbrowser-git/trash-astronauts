using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace TrashAstronauts.World
{
    /// <summary>
    /// A toy that squeaks when squeezed (trigger while holding it). It is silly, and it is also useful:
    /// the squeak is loud enough to draw the creature, so it can be squeezed and thrown as a decoy.
    /// </summary>
    public sealed class SqueakyToy : MonoBehaviour
    {
        public XRGrabInteractable grab;
        public Transform squashTarget;
        [Range(0f, 1.5f)] public float loudness = 0.7f;
        public float cooldown = 0.35f;

        float m_LastSqueak = -10f;
        float m_Squash;
        Vector3 m_RestScale = Vector3.one;

        public int SqueakCount { get; private set; }

        void Awake()
        {
            if (grab == null) grab = GetComponent<XRGrabInteractable>();
            if (squashTarget != null) m_RestScale = squashTarget.localScale;
        }

        void OnEnable()
        {
            if (grab != null) grab.activated.AddListener(OnActivated);
        }

        void OnDisable()
        {
            if (grab != null) grab.activated.RemoveListener(OnActivated);
        }

        void OnActivated(ActivateEventArgs args)
        {
            var hand = PlayerContext.HandFrom(args.interactorObject);
            Squeak(hand);
        }

        /// <summary>Squeaks. Returns false while it is still recovering from the last squeeze.</summary>
        public bool Squeak(HandRig hand = null)
        {
            if (Time.time - m_LastSqueak < cooldown) return false;
            m_LastSqueak = Time.time;
            m_Squash = 1f;
            SqueakCount++;
            if (hand != null) hand.Pulse(0.5f, 0.06f);
            if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.squeak, transform.position, 0.8f, Random.Range(0.92f, 1.12f), 18f);

            var director = MissionDirector.Active;
            if (director != null && director.noise != null)
                director.noise.Emit(new NoiseEvent(transform.position, loudness, NoiseKind.Impact,
                    hand != null && hand.Player != null ? hand.Player.Id : PlayerId.Local));
            return true;
        }

        void Update()
        {
            if (squashTarget == null || (m_Squash <= 0f && squashTarget.localScale == m_RestScale)) return;
            m_Squash = Mathf.Max(0f, m_Squash - Time.deltaTime * 5f);
            float k = m_Squash * 0.3f;
            squashTarget.localScale = new Vector3(m_RestScale.x * (1f + k * 0.5f), m_RestScale.y * (1f - k), m_RestScale.z * (1f + k * 0.5f));
        }
    }
}

using System;
using TMPro;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>
    /// A sliding bulkhead door. Player-operated doors toggle from wall buttons; the tug hatch is locked
    /// and driven by the mission. A door will not close on a player standing in the doorway.
    /// </summary>
    public sealed class SlidingDoor : MonoBehaviour
    {
        public string doorId = "door";
        public Transform leaf;
        [Tooltip("Local offset of the leaf when fully open.")]
        public Vector3 openOffset = new Vector3(0f, 2.2f, 0f);
        public float duration = 1.1f;
        public bool startsOpen;
        [Tooltip("Locked doors ignore their buttons; only the mission moves them.")]
        public bool locked;
        public bool makesGameplayNoise = true;
        public bool airlock;
        public PushButton[] buttons;
        public TMP_Text statusLabel;
        [Tooltip("Size of the doorway volume checked before closing.")]
        public Vector3 doorwaySize = new Vector3(1.4f, 2.1f, 0.5f);

        Vector3 m_ClosedPosition;
        bool m_Cached;
        bool m_TargetOpen;
        bool m_PendingClose;
        float m_Openness;
        readonly Collider[] m_Hits = new Collider[4];

        public bool IsOpen => m_TargetOpen;
        public bool IsFullyOpen => m_Openness >= 0.999f;
        public bool IsMoving => m_TargetOpen ? m_Openness < 1f : m_Openness > 0f;
        public event Action<SlidingDoor> Changed;

        void Awake()
        {
            Cache();
            SetOpen(startsOpen, true);
        }

        void Cache()
        {
            if (m_Cached || leaf == null) return;
            m_ClosedPosition = leaf.localPosition;
            m_Cached = true;
        }

        void OnEnable()
        {
            if (buttons == null) return;
            foreach (var b in buttons)
            {
                if (b != null) b.Pressed += OnButtonPressed;
            }
        }

        void OnDisable()
        {
            if (buttons == null) return;
            foreach (var b in buttons)
            {
                if (b != null) b.Pressed -= OnButtonPressed;
            }
        }

        void OnButtonPressed(PushButton button, HandRig hand)
        {
            if (locked) return;
            SetOpen(!m_TargetOpen, false, hand != null && hand.Player != null ? hand.Player.Id : PlayerId.Local);
        }

        public void ResetForRun() => SetOpen(startsOpen, true);

        public void SetLocked(bool value)
        {
            locked = value;
            RefreshLabel();
        }

        public void SetOpen(bool open, bool instant = false) => SetOpen(open, instant, PlayerId.None);

        public void SetOpen(bool open, bool instant, PlayerId by)
        {
            Cache();
            if (!open && !instant && IsDoorwayOccupied())
            {
                // Try again once the doorway is clear rather than closing on someone.
                m_PendingClose = true;
                return;
            }
            m_PendingClose = false;

            bool changed = m_TargetOpen != open;
            m_TargetOpen = open;
            if (instant)
            {
                m_Openness = open ? 1f : 0f;
                ApplyLeaf();
            }
            else if (changed)
            {
                if (Sfx.Library != null)
                    Sfx.PlayAt(airlock ? Sfx.Library.airlockHiss : Sfx.Library.doorMove, transform.position, 0.9f);
                var director = MissionDirector.Active;
                if (makesGameplayNoise && director != null && director.noise != null)
                    director.noise.Emit(new NoiseEvent(transform.position, director.Config.noise.doorLoudness, NoiseKind.Door, by));
            }

            RefreshLabel();
            if (changed) Changed?.Invoke(this);
        }

        bool IsDoorwayOccupied()
        {
            Vector3 center = transform.position + Vector3.up * (doorwaySize.y * 0.5f);
            int count = Physics.OverlapBoxNonAlloc(center, doorwaySize * 0.5f, m_Hits, transform.rotation,
                1 << GameLayers.Player, QueryTriggerInteraction.Ignore);
            return count > 0;
        }

        void Update()
        {
            if (m_PendingClose && !IsDoorwayOccupied()) SetOpen(false);

            float target = m_TargetOpen ? 1f : 0f;
            if (Mathf.Approximately(m_Openness, target)) return;
            m_Openness = Mathf.MoveTowards(m_Openness, target, Time.deltaTime / Mathf.Max(0.05f, duration));
            ApplyLeaf();
        }

        void ApplyLeaf()
        {
            if (leaf == null) return;
            leaf.localPosition = m_ClosedPosition + openOffset * Mathf.SmoothStep(0f, 1f, m_Openness);
        }

        void RefreshLabel()
        {
            if (statusLabel != null)
                statusLabel.text = locked ? (m_TargetOpen ? "OPEN" : "SEALED") : (m_TargetOpen ? "OPEN" : "CLOSED");
            if (buttons == null) return;
            foreach (var b in buttons)
            {
                if (b == null) continue;
                b.SetInteractable(!locked);
                b.SetLabel(locked ? "LOCKED" : (m_TargetOpen ? "CLOSE" : "OPEN"));
            }
        }

        /// <summary>True if a straight move from a to b would pass through this door while it is shut.</summary>
        public bool BlocksSegment(Vector3 a, Vector3 b)
        {
            if (m_Openness > 0.85f) return false;
            Vector3 center = transform.position + Vector3.up * (doorwaySize.y * 0.5f);
            var bounds = new Bounds(Vector3.zero, new Vector3(doorwaySize.x + 0.4f, doorwaySize.y + 1f, 0.6f));
            Vector3 la = Quaternion.Inverse(transform.rotation) * (a - center);
            Vector3 lb = Quaternion.Inverse(transform.rotation) * (b - center);
            Vector3 dir = lb - la;
            float length = dir.magnitude;
            if (length < 0.0001f) return bounds.Contains(la);
            return bounds.IntersectRay(new Ray(la, dir / length), out float distance) && distance <= length;
        }
    }
}

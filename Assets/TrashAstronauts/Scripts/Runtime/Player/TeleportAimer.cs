using TrashAstronauts.Audio;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// Comfort movement: push the thumbstick forward to aim an arc, release to teleport to a valid
    /// floor surface. Works with either hand and while carrying salvage, so the whole mission can be
    /// completed without using the handrails.
    /// </summary>
    public sealed class TeleportAimer : MonoBehaviour
    {
        public HandRig hand;
        public PlayerLocomotion locomotion;
        public LineRenderer line;
        public Transform reticle;

        [Header("Input")]
        [Range(0.3f, 0.95f)] public float aimThreshold = 0.65f;
        [Range(0.05f, 0.6f)] public float releaseThreshold = 0.3f;

        [Header("Arc")]
        public float launchSpeed = 7.5f;
        public float arcGravity = 9.8f;
        public int segments = 24;
        public float segmentTime = 0.06f;
        [Tooltip("Capsule radius that must be clear of walls and props at the destination.")]
        public float clearanceRadius = 0.22f;
        public float clearanceHeight = 1.5f;

        [Header("Look (validity is shown by shape as well as colour)")]
        public Color validColor = new Color(0.3f, 0.95f, 1f, 1f);
        public Color invalidColor = new Color(1f, 0.35f, 0.25f, 1f);
        public float validWidth = 0.012f;
        public float invalidWidth = 0.004f;

        bool m_Aiming;
        bool m_Valid;
        Vector3 m_Target;
        Vector3[] m_Points;

        public bool IsAiming => m_Aiming;

        void Awake()
        {
            m_Points = new Vector3[Mathf.Max(2, segments + 1)];
            SetVisible(false);
        }

        void OnDisable()
        {
            m_Aiming = false;
            SetVisible(false);
        }

        void Update()
        {
            if (hand == null || locomotion == null) return;

            Vector2 stick = hand.Thumbstick;
            bool forward = stick.y > aimThreshold && Mathf.Abs(stick.x) < stick.y;

            if (!m_Aiming)
            {
                if (forward && !hand.IsAnchored)
                {
                    m_Aiming = true;
                    SetVisible(true);
                }
                else return;
            }

            if (hand.IsAnchored)
            {
                Cancel();
                return;
            }

            if (stick.magnitude < releaseThreshold)
            {
                if (m_Valid && locomotion.TeleportTo(m_Target))
                {
                    hand.Pulse(0.3f, 0.05f);
                    if (Sfx.Library != null) Sfx.PlayUi(Sfx.Library.teleport, 0.5f);
                }
                Cancel();
                return;
            }

            UpdateArc();
        }

        void Cancel()
        {
            m_Aiming = false;
            m_Valid = false;
            SetVisible(false);
        }

        void UpdateArc()
        {
            Vector3 position = hand.transform.position;
            Vector3 velocity = hand.transform.forward * launchSpeed;
            int mask = GameLayers.EnvironmentMask | GameLayers.TeleportSurfaceMask;

            m_Valid = false;
            int count = 1;
            m_Points[0] = position;

            for (int i = 0; i < segments; i++)
            {
                Vector3 next = position + velocity * segmentTime + 0.5f * arcGravity * segmentTime * segmentTime * Vector3.down;
                velocity += arcGravity * segmentTime * Vector3.down;
                Vector3 delta = next - position;

                if (Physics.Raycast(position, delta.normalized, out RaycastHit hit, delta.magnitude, mask, QueryTriggerInteraction.Collide))
                {
                    m_Points[count++] = hit.point;
                    m_Valid = IsValidDestination(hit, out m_Target);
                    break;
                }

                m_Points[count++] = next;
                position = next;
            }

            if (line != null)
            {
                line.positionCount = count;
                for (int i = 0; i < count; i++) line.SetPosition(i, m_Points[i]);
                Color color = m_Valid ? validColor : invalidColor;
                line.startColor = color;
                line.endColor = color;
                line.widthMultiplier = m_Valid ? validWidth : invalidWidth;
            }

            if (reticle != null)
            {
                reticle.gameObject.SetActive(m_Valid);
                if (m_Valid) reticle.SetPositionAndRotation(m_Target + Vector3.up * 0.02f, Quaternion.identity);
            }
        }

        bool IsValidDestination(RaycastHit hit, out Vector3 target)
        {
            target = hit.point;
            if (hit.collider.gameObject.layer != GameLayers.TeleportSurface) return false;
            if (hit.normal.y < 0.7f) return false;

            var surface = hit.collider.GetComponent<TeleportSurface>();
            if (surface != null) target = surface.ResolveTarget(hit.point);

            Vector3 bottom = target + Vector3.up * (clearanceRadius + 0.12f);
            Vector3 top = target + Vector3.up * Mathf.Max(clearanceRadius + 0.13f, clearanceHeight - clearanceRadius);
            return !Physics.CheckCapsule(bottom, top, clearanceRadius, GameLayers.EnvironmentMask, QueryTriggerInteraction.Ignore);
        }

        void SetVisible(bool visible)
        {
            if (line != null) line.enabled = visible;
            if (reticle != null && !visible) reticle.gameObject.SetActive(false);
        }
    }
}

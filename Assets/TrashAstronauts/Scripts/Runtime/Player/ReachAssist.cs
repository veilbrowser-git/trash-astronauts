using System.Collections.Generic;
using TrashAstronauts.Mission;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// Reach assist: picking salvage up off the floor (or from just beyond the fingertips) without bending
    /// down. While a hand is empty and open and nothing is in its direct grab, it looks for the best loose
    /// salvage item within <see cref="range"/> of the palm, inside a cone along the way the hand points or
    /// the palm faces, favouring things below the hand and lying on the floor, with nothing solid in
    /// between. The item it would take glows with its normal hover highlight and a faint line runs to it
    /// from the palm, so the player knows exactly what a squeeze will do.
    ///
    /// Squeezing then takes it through an ordinary XRI selection by this hand's own interactor (so the
    /// co-op ownership rules, the grab sounds, haptics, noise, fragility and mass all apply as for a
    /// direct grab), and the item travels to the palm physically: the interactor's attach point starts on
    /// the item's surface, so the grab's dynamic attach records a grip right there, and then eases back
    /// into the palm over a fraction of a second. The item follows by the grab's own velocity tracking, so
    /// it collides on the way and heavy things trail behind as they do in the hand. The ease waits whenever
    /// the item lags (so the grip-slip rule never fires during a pull), and lets go if the item is stuck.
    ///
    /// Never: through walls, out of anyone's hand, mounted or secured items, tools, shop goods, or while the
    /// hand holds something, is anchored to the ship or points at a button. A hand pressed against a
    /// surface prefers grabbing the surface unless the item is close.
    /// </summary>
    [DefaultExecutionOrder(-60)] // after hand tracking (-200), before ZeroGLocomotion asks whether a squeeze is ours
    public sealed class ReachAssist : MonoBehaviour
    {
        public HandRig hand;
        [Tooltip("Faint line from the palm to the item a squeeze would take.")]
        public LineRenderer line;

        [Header("Finding")]
        [Tooltip("Farthest an item may be from the palm (to its nearest surface), metres.")]
        public float range = 1.2f;
        [Tooltip("Half-angle of the cone along the pointing direction, degrees.")]
        public float pointCone = 35f;
        [Tooltip("Half-angle of the cone out of the palm, degrees.")]
        public float palmCone = 25f;
        [Tooltip("Cost discount for an item below the hand (0..1).")]
        [Range(0f, 0.9f)] public float belowBonus = 0.2f;
        [Tooltip("Cost discount for an item lying on the floor (0..1).")]
        [Range(0f, 0.9f)] public float floorBonus = 0.15f;
        [Tooltip("A new item must be this much cheaper than the shown one to replace it (stops flicker), fraction.")]
        [Range(0f, 0.5f)] public float switchMargin = 0.15f;
        [Tooltip("Palm this close to a wall or floor counts as touching it.")]
        public float touchRadius = 0.1f;
        [Tooltip("While the palm touches a surface, only items closer than this are taken (farther, the squeeze grabs the surface).")]
        public float wallYieldDistance = 0.45f;

        [Header("Pull")]
        [Tooltip("Average speed of the pull for a light item, m/s.")]
        public float pullSpeed = 2.4f;
        [Tooltip("Shortest pull, seconds.")]
        public float minPullSeconds = 0.2f;
        [Tooltip("Up to this mass (kg) the pull runs at full speed; heavier, it slows with the square root of the mass.")]
        public float freeMass = 2f;
        [Tooltip("The pull waits while the item trails its grip point by more than this (m). Well inside HeldWeight's slip distance.")]
        public float maxLag = 0.2f;
        [Tooltip("A pull that makes no progress for this long lets go (the item is stuck).")]
        public float stallSeconds = 0.5f;
        [Range(0.1f, 0.9f)] public float gripPress = 0.6f;
        [Range(0.05f, 0.8f)] public float gripRelease = 0.35f;
        [Tooltip("Haptic tick when a new item is picked out.")]
        [Range(0f, 1f)] public float targetHaptic = 0.05f;

        static readonly Collider[] s_Hits = new Collider[64];
        static readonly HashSet<SalvageItem> s_Seen = new HashSet<SalvageItem>();
        static readonly Dictionary<HoverHighlight, int> s_Lit = new Dictionary<HoverHighlight, int>();

        Transform m_Attach;
        Vector3 m_AttachRest;
        bool m_GripHeld;

        SalvageItem m_Candidate;
        float m_CandidateCost;
        Vector3 m_CandidatePoint;
        HoverHighlight m_Lit;

        SalvageItem m_PressCandidate;
        int m_PressFrame = -1;

        SalvageItem m_Pulling;
        XRBaseInteractable m_PullGrab;
        HeldWeight m_PullWeight;
        Vector3 m_PullFrom;
        float m_Progress;
        float m_Duration;
        float m_Stall;

        /// <summary>The item a squeeze would take right now (null when nothing is shown).</summary>
        public SalvageItem Candidate => m_Candidate;
        /// <summary>Where on the candidate the line points and the grip will be.</summary>
        public Vector3 CandidatePoint => m_CandidatePoint;
        /// <summary>True while a squeeze of this hand belongs to the reach assist (ZeroGLocomotion then neither grabs the wall nor paddles).</summary>
        public bool ClaimsGrip => m_Candidate != null || (m_PressCandidate != null && m_PressFrame == Time.frameCount);
        /// <summary>True while an assisted item is travelling to the palm.</summary>
        public bool IsPulling => m_Pulling != null;
        /// <summary>The item being pulled (null when none).</summary>
        public SalvageItem Pulling => m_Pulling;
        /// <summary>How many items this hand has taken with the assist.</summary>
        public int AssistedGrabs { get; private set; }
        /// <summary>How many assisted pulls let go because the item was stuck.</summary>
        public int StalledPulls { get; private set; }
        /// <summary>Seconds the last completed pull took, from the squeeze to the item seated in the palm.</summary>
        public float LastPullSeconds { get; private set; }
        float m_PullStarted;

        /// <summary>The palm: the centre of the hand's grab, where everything is measured from.</summary>
        public Vector3 PalmPoint => hand != null && hand.interactor != null ? hand.interactor.transform.position : transform.position;

        /// <summary>The way the palm faces (the glove is held like a handshake: the left palm faces +X of the controller, the right -X).</summary>
        public Vector3 PalmNormal => hand == null ? transform.right : (hand.side == TrashAstronauts.Core.Handedness.Left ? hand.transform.right : -hand.transform.right);

        void Awake()
        {
            if (hand == null) hand = GetComponent<HandRig>();
            if (hand != null && hand.interactor != null)
            {
                m_Attach = hand.interactor.attachTransform;
                if (m_Attach != null) m_AttachRest = m_Attach.localPosition;
            }
            if (line != null) line.enabled = false;
        }

        void OnDisable()
        {
            SetCandidate(null, 0f, Vector3.zero);
            EndPull();
            m_PressCandidate = null;
            m_GripHeld = false;
            if (line != null) line.enabled = false;
        }

        bool Active => hand != null && hand.interactor != null && m_Attach != null && hand.interactor.isActiveAndEnabled
            && (hand.Player == null || !hand.Player.IsRemote);

        void Update()
        {
            if (!Active)
            {
                SetCandidate(null, 0f, Vector3.zero);
                return;
            }

            float grip = hand.Grip;
            bool pressed = false;
            if (!m_GripHeld && grip >= gripPress) { m_GripHeld = true; pressed = true; }
            else if (m_GripHeld && grip <= gripRelease) m_GripHeld = false;

            if (m_Pulling != null) StepPull(Time.deltaTime);

            // The squeeze takes what was shown: decided now, so ZeroGLocomotion (later this frame) leaves it alone;
            // the selection itself happens in LateUpdate, after XRI has had its chance at a direct grab.
            if (pressed && m_Candidate != null && HandFree() && Takeable(m_Candidate))
            {
                m_PressCandidate = m_Candidate;
                m_PressFrame = Time.frameCount;
            }

            if (!m_GripHeld && m_Pulling == null && HandFree()) Search();
            else SetCandidate(null, 0f, Vector3.zero);
        }

        void LateUpdate()
        {
            if (m_PressCandidate != null)
            {
                var item = m_PressCandidate;
                m_PressCandidate = null;
                if (Active && m_PressFrame == Time.frameCount && m_GripHeld && !hand.IsHolding && !hand.IsAnchored && Takeable(item))
                    StartPull(item);
            }
            UpdateLine();
        }

        /// <summary>Empty, free hand with nothing in its direct grab.</summary>
        bool HandFree()
        {
            return !hand.IsHolding && !hand.IsAnchored && !hand.IsPointingAtButton
                && hand.interactor.interactablesHovered.Count == 0;
        }

        /// <summary>Could this hand take the item at all right now (ignoring where it is)?</summary>
        bool Takeable(SalvageItem item)
        {
            if (item == null || !item.isActiveAndEnabled || item.grab == null || !item.grab.isActiveAndEnabled) return false;
            if (item.IsHeld || item.IsMounted || item.IsSecured || item.Body == null) return false;
            var manager = item.grab.interactionManager;
            // The same test XRI makes before any selection: interaction layers, filters, this interactor's own
            // rules and the item's IsSelectableBy (in co-op: not while another crew member holds it).
            return manager != null && manager.IsSelectPossible(hand.interactor, item.grab);
        }

        /// <summary>Nearest point of the item's solid colliders to <paramref name="from"/>.</summary>
        static Vector3 NearestPoint(SalvageItem item, Vector3 from, out Bounds bounds)
        {
            Vector3 best = item.transform.position;
            float bestSq = float.MaxValue;
            bounds = new Bounds(item.transform.position, Vector3.zero);
            bool first = true;
            IList<Collider> colliders = item.grab.colliders;
            if (colliders.Count == 0) colliders = item.GetComponentsInChildren<Collider>(); // not registered yet (edit-mode previews)
            for (int i = 0; i < colliders.Count; i++)
            {
                var c = colliders[i];
                if (c == null || !c.enabled || c.isTrigger) continue;
                if (first) { bounds = c.bounds; first = false; }
                else bounds.Encapsulate(c.bounds);
                Vector3 p = NearestGrabFilter.ClosestPoint(c, from);
                float sq = (p - from).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = p; }
            }
            return best;
        }

        /// <summary>
        /// How good a target the item is from this palm: lower is better, infinity when it is not one.
        /// Distance, stretched up to 1.5x toward the edge of the cone, discounted for items below the hand and on the floor.
        /// </summary>
        public float Cost(SalvageItem item, out Vector3 gripPoint)
        {
            Vector3 palm = PalmPoint;
            gripPoint = NearestPoint(item, palm, out Bounds bounds);
            Vector3 to = gripPoint - palm;
            float distance = to.magnitude;
            if (distance > range) return float.PositiveInfinity;
            Vector3 dir = distance > 1e-4f ? to / distance : hand.transform.forward;

            float pointAngle = Vector3.Angle(dir, hand.transform.forward);
            float palmAngle = Vector3.Angle(dir, PalmNormal);
            float off = Mathf.Min(pointAngle / pointCone, palmAngle / palmCone);
            if (off > 1f) return float.PositiveInfinity;

            // Pressed against a wall or the deck, a squeeze means "hold on" unless the item is right there.
            if (distance > wallYieldDistance && Physics.CheckSphere(palm, touchRadius, GameLayers.EnvironmentMask | GameLayers.RailMask, QueryTriggerInteraction.Ignore))
                return float.PositiveInfinity;

            // Nothing solid in between: the line from the palm to just short of the grip point must be clear.
            if (distance > 0.02f && Physics.Linecast(palm, gripPoint - dir * 0.015f, GameLayers.RestingMask, QueryTriggerInteraction.Ignore))
                return float.PositiveInfinity;

            float cost = Mathf.Max(distance, 0.01f) * (1f + 0.5f * off);
            Vector3 down = Vector3.down;
            Vector3 gravity = MapGravity.GravityAt(bounds.center);
            if (gravity != Vector3.zero) down = gravity.normalized;
            if (Vector3.Dot(dir, down) > 0.35f) cost *= 1f - belowBonus;
            // Lying on the floor (or drifted down against the deck).
            float below = Vector3.Dot(bounds.extents, new Vector3(Mathf.Abs(down.x), Mathf.Abs(down.y), Mathf.Abs(down.z)));
            if (Physics.Raycast(bounds.center, down, below + 0.1f, GameLayers.RestingMask, QueryTriggerInteraction.Ignore)) cost *= 1f - floorBonus;
            return cost;
        }

        void Search()
        {
            Vector3 palm = PalmPoint;
            int count = Physics.OverlapSphereNonAlloc(palm, range, s_Hits, GameLayers.SalvageMask, QueryTriggerInteraction.Ignore);
            s_Seen.Clear();
            SalvageItem best = null;
            float bestCost = float.PositiveInfinity;
            Vector3 bestPoint = Vector3.zero;
            float currentCost = float.PositiveInfinity;
            Vector3 currentPoint = Vector3.zero;
            for (int i = 0; i < count; i++)
            {
                var body = s_Hits[i] != null ? s_Hits[i].attachedRigidbody : null;
                if (body == null) continue;
                var item = body.GetComponent<SalvageItem>();
                if (item == null || !s_Seen.Add(item) || !Takeable(item)) continue;
                float cost = Cost(item, out Vector3 point);
                if (item == m_Candidate) { currentCost = cost; currentPoint = point; }
                if (cost < bestCost) { bestCost = cost; best = item; bestPoint = point; }
            }
            s_Seen.Clear();

            // Keep the item already shown unless another is clearly better: no flicker between two neighbours.
            if (m_Candidate != null && !float.IsPositiveInfinity(currentCost) && best != m_Candidate && bestCost > currentCost * (1f - switchMargin))
            {
                best = m_Candidate;
                bestCost = currentCost;
                bestPoint = currentPoint;
            }
            if (float.IsPositiveInfinity(bestCost)) best = null;
            SetCandidate(best, bestCost, bestPoint);
        }

        void SetCandidate(SalvageItem item, float cost, Vector3 point)
        {
            if (item != m_Candidate)
            {
                Light(m_Lit, false);
                m_Lit = item != null ? item.highlight : null;
                Light(m_Lit, true);
                if (item != null && hand != null) hand.Pulse(targetHaptic, 0.015f);
            }
            m_Candidate = item;
            m_CandidateCost = cost;
            m_CandidatePoint = point;
        }

        /// <summary>The item's own hover glow, counted per item so two hands showing the same item do not fight.</summary>
        static void Light(HoverHighlight highlight, bool on)
        {
            if (highlight == null) return;
            s_Lit.TryGetValue(highlight, out int n);
            n = Mathf.Max(0, n + (on ? 1 : -1));
            if (n == 0) s_Lit.Remove(highlight);
            else s_Lit[highlight] = n;
            highlight.SetManualHover(n > 0);
        }

        void UpdateLine()
        {
            if (line == null) return;
            bool show = m_Candidate != null;
            if (line.enabled != show) line.enabled = show;
            if (!show) return;
            if (line.positionCount != 2) line.positionCount = 2;
            Vector3 palm = PalmPoint;
            // Start a little out from the palm so the line does not sit inside the glove.
            Vector3 dir = m_CandidatePoint - palm;
            float length = dir.magnitude;
            Vector3 start = length > 0.12f ? palm + dir * (0.06f / length) : palm;
            line.SetPosition(0, start);
            line.SetPosition(1, m_CandidatePoint);
        }

        /// <summary>Shows the cue (line and glow) for an item without any of the runtime checks: for edit-mode preview images only.</summary>
        public void ShowCueForPreview(SalvageItem item)
        {
            if (hand == null) hand = GetComponent<HandRig>();
            m_Candidate = item;
            m_CandidatePoint = item != null ? NearestPoint(item, PalmPoint, out _) : Vector3.zero;
            UpdateLine();
        }

        // ------------------------------------------------------------------ the pull

        void StartPull(SalvageItem item)
        {
            var interactor = hand.interactor;
            var manager = item.grab.interactionManager;
            if (manager == null) return;
            Vector3 grip = NearestPoint(item, PalmPoint, out _);

            // The grip is where the item's surface is: put the interactor's attach point there for the moment
            // of the selection, so the grab's dynamic attach records it and HeldWeight measures from it.
            m_Attach.position = grip;
            manager.SelectEnter((IXRSelectInteractor)interactor, (IXRSelectInteractable)item.grab);
            if (!interactor.IsSelecting(item.grab))
            {
                m_Attach.localPosition = m_AttachRest;
                return;
            }

            m_Pulling = item;
            m_PullGrab = item.grab;
            m_PullWeight = item.GetComponent<HeldWeight>();
            m_PullFrom = m_Attach.localPosition;
            m_Progress = 0f;
            m_Stall = 0f;
            float distance = Vector3.Distance(m_PullFrom, m_AttachRest);
            float mass = item.Body.mass;
            m_Duration = Mathf.Max(minPullSeconds, distance / Mathf.Max(0.1f, pullSpeed));
            if (mass > freeMass) m_Duration *= Mathf.Sqrt(mass / freeMass);
            m_PullStarted = Time.time;
            AssistedGrabs++;
        }

        void StepPull(float dt)
        {
            if (m_PullGrab == null || !hand.interactor.IsSelecting(m_PullGrab))
            {
                // Let go, slipped or taken away mid-pull: the hand is a hand again at once.
                EndPull();
                return;
            }
            // How far the item trails its grip point. While it is well behind, the grip waits for it.
            float lag = m_PullWeight != null ? m_PullWeight.GripGap
                : Vector3.Distance(m_Attach.position, m_PullGrab.GetAttachTransform(hand.interactor).position);
            if (lag <= maxLag)
            {
                m_Progress = Mathf.Min(1f, m_Progress + dt / Mathf.Max(0.01f, m_Duration));
                m_Stall = 0f;
            }
            else
            {
                m_Stall += dt;
                if (m_Stall >= stallSeconds)
                {
                    // Stuck on something: let go rather than drag it through the ship (and never hover it at a distance).
                    var manager = m_PullGrab.interactionManager;
                    var grab = m_PullGrab;
                    EndPull();
                    StalledPulls++;
                    if (manager != null) manager.SelectExit((IXRSelectInteractor)hand.interactor, (IXRSelectInteractable)grab);
                    hand.Pulse(0.5f, 0.05f);
                    return;
                }
            }
            float t = m_Progress * m_Progress * (3f - 2f * m_Progress);
            m_Attach.localPosition = Vector3.Lerp(m_PullFrom, m_AttachRest, t);
            if (m_Progress >= 1f)
            {
                LastPullSeconds = Time.time - m_PullStarted;
                EndPull();
            }
        }

        void EndPull()
        {
            if (m_Attach != null) m_Attach.localPosition = m_AttachRest;
            m_Pulling = null;
            m_PullGrab = null;
            m_PullWeight = null;
        }
    }
}

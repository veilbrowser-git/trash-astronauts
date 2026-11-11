using System.Collections.Generic;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// Lets a hand operate <see cref="PushButton"/>s two ways: poking with the fingertip, or pointing
    /// from up to a few metres away and pulling the trigger. The pointer line only appears while it is
    /// actually aimed at a button, so it never clutters normal play.
    /// </summary>
    public sealed class HandButtonInteractor : MonoBehaviour
    {
        public HandRig hand;
        public LineRenderer pointerLine;
        public float pokeRadius = 0.028f;
        public float pointerRange = 3.5f;

        readonly Collider[] m_Hits = new Collider[8];
        readonly HashSet<PushButton> m_Touching = new HashSet<PushButton>();
        readonly HashSet<PushButton> m_TouchingNow = new HashSet<PushButton>();
        PushButton m_Pointed;

        void OnDisable()
        {
            SetPointed(null);
            m_Touching.Clear();
            if (pointerLine != null) pointerLine.enabled = false;
        }

        void Update()
        {
            if (hand == null) return;
            UpdatePoke();
            UpdatePointer();
        }

        void UpdatePoke()
        {
            m_TouchingNow.Clear();
            if (hand.fingertip != null)
            {
                int count = Physics.OverlapSphereNonAlloc(hand.fingertip.position, pokeRadius, m_Hits,
                    GameLayers.ButtonMask, QueryTriggerInteraction.Collide);
                for (int i = 0; i < count; i++)
                {
                    var button = m_Hits[i].GetComponentInParent<PushButton>();
                    if (button == null) continue;
                    m_TouchingNow.Add(button);
                    // Press on first contact only; the finger must leave before it can press again.
                    if (!m_Touching.Contains(button)) button.Press(hand);
                }
            }
            m_Touching.Clear();
            foreach (var button in m_TouchingNow) m_Touching.Add(button);
        }

        void UpdatePointer()
        {
            PushButton target = null;
            Vector3 origin = hand.transform.position;
            Vector3 direction = hand.transform.forward;
            Vector3 end = origin;

            if (!hand.IsHolding)
            {
                int mask = GameLayers.ButtonMask | GameLayers.EnvironmentMask;
                if (Physics.Raycast(origin, direction, out RaycastHit hit, pointerRange, mask, QueryTriggerInteraction.Collide)
                    && hit.collider.gameObject.layer == GameLayers.Button)
                {
                    target = hit.collider.GetComponentInParent<PushButton>();
                    end = hit.point;
                }
            }

            if (target != null && !target.Interactable) target = null;
            SetPointed(target);

            if (pointerLine != null)
            {
                pointerLine.enabled = target != null;
                if (target != null)
                {
                    pointerLine.positionCount = 2;
                    pointerLine.SetPosition(0, origin);
                    pointerLine.SetPosition(1, end);
                }
            }

            if (target != null && hand.TriggerPressedThisFrame && !m_Touching.Contains(target)) target.Press(hand);
        }

        void SetPointed(PushButton button)
        {
            if (m_Pointed == button) return;
            if (m_Pointed != null) m_Pointed.SetHover(false);
            m_Pointed = button;
            if (m_Pointed != null)
            {
                m_Pointed.SetHover(true);
                hand.Pulse(0.1f, 0.02f);
            }
        }
    }
}

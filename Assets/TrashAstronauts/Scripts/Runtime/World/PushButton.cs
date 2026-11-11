using System;
using TMPro;
using TrashAstronauts.Audio;
using TrashAstronauts.Player;
using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>
    /// A chunky physical button. Press it by touching it with a fingertip, or by pointing at it and
    /// pulling the trigger (so seated players and players who dislike reaching can use every menu).
    /// All in-headset menus are built from these.
    /// </summary>
    public sealed class PushButton : MonoBehaviour
    {
        public TMP_Text label;
        public Transform cap;
        public HoverHighlight highlight;
        public Renderer capRenderer;
        public float pressDepth = 0.012f;
        public float cooldown = 0.45f;

        [SerializeField] bool m_Interactable = true;

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        MaterialPropertyBlock m_Block;
        Vector3 m_CapRest;
        float m_LastPress = -10f;
        float m_PressAnim;
        string m_Text;

        /// <summary>Raised on a successful press with the hand that pressed it (null for scripted presses).</summary>
        public event Action<PushButton, HandRig> Pressed;

        public bool Interactable => m_Interactable;
        public string Text => m_Text ?? (label != null ? label.text : string.Empty);

        void Awake()
        {
            if (cap != null) m_CapRest = cap.localPosition;
            ApplyInteractableLook();
        }

        public void SetLabel(string text)
        {
            m_Text = text;
            if (label != null && label.text != text) label.text = text;
        }

        public void SetInteractable(bool value)
        {
            if (m_Interactable == value) return;
            m_Interactable = value;
            ApplyInteractableLook();
        }

        void ApplyInteractableLook()
        {
            // Disabled buttons are dimmed AND their label is greyed, so state is not conveyed by colour alone.
            if (label != null) label.alpha = m_Interactable ? 1f : 0.35f;
            if (capRenderer == null) return;
            if (m_Block == null) m_Block = new MaterialPropertyBlock();
            if (m_Interactable)
            {
                capRenderer.SetPropertyBlock(null);
            }
            else
            {
                capRenderer.GetPropertyBlock(m_Block);
                m_Block.SetColor(k_BaseColor, new Color(0.18f, 0.18f, 0.2f, 1f));
                capRenderer.SetPropertyBlock(m_Block);
            }
        }

        public void SetHover(bool hovered)
        {
            if (highlight != null) highlight.SetManualHover(hovered && m_Interactable);
        }

        /// <summary>Presses the button. Returns false if it is disabled or still in its debounce window.</summary>
        public bool Press(HandRig by)
        {
            if (!m_Interactable || !isActiveAndEnabled) return false;
            if (Time.unscaledTime - m_LastPress < cooldown) return false;

            m_LastPress = Time.unscaledTime;
            m_PressAnim = 1f;
            if (by != null) by.Pulse(0.5f, 0.05f);
            if (Sfx.Library != null) Sfx.PlayAt(Sfx.Library.buttonClick, transform.position, 0.7f, 1f, 6f);
            Pressed?.Invoke(this, by);
            return true;
        }

        void Update()
        {
            if (cap == null || (m_PressAnim <= 0f && cap.localPosition == m_CapRest)) return;
            m_PressAnim = Mathf.Max(0f, m_PressAnim - Time.unscaledDeltaTime * 5f);
            cap.localPosition = m_CapRest + Vector3.forward * (pressDepth * m_PressAnim);
        }
    }
}

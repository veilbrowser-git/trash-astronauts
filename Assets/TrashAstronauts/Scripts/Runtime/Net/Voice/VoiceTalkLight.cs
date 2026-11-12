using UnityEngine;

namespace TrashAstronauts.Net.Voice
{
    /// <summary>
    /// The talk light on a crew member's helmet: the small glowing dot the RemoteCrew prefab carries (named
    /// <see cref="ChildName"/>, hidden by default) is shown while voice chat hears that player talking. Added by
    /// <see cref="VoiceChat"/> when it gives a helmet its voice source, and gone with it. One renderer toggle per
    /// change; nothing per frame beyond reading a bool.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VoiceTalkLight : MonoBehaviour
    {
        /// <summary>The name of the light's object under the helmet in the RemoteCrew prefab.</summary>
        public const string ChildName = "Talk Light";

        VoiceChat m_Chat;
        int m_Slot;
        Renderer m_Light;

        /// <summary>True while the light is showing.</summary>
        public bool IsOn => m_Light != null && m_Light.enabled;
        public Renderer Light => m_Light;

        /// <summary>Puts the indicator on a helmet (once). Null when the helmet has no talk light.</summary>
        public static VoiceTalkLight Attach(VoiceChat chat, Transform helmet, int slot)
        {
            if (chat == null || helmet == null) return null;
            Transform child = helmet.Find(ChildName);
            var light = child != null ? child.GetComponent<Renderer>() : null;
            if (light == null) return null;
            var indicator = helmet.GetComponent<VoiceTalkLight>();
            if (indicator == null) indicator = helmet.gameObject.AddComponent<VoiceTalkLight>();
            indicator.m_Chat = chat;
            indicator.m_Slot = slot;
            indicator.m_Light = light;
            light.enabled = false;
            return indicator;
        }

        void Update()
        {
            if (m_Light == null) return;
            bool on = m_Chat != null && m_Chat.isActiveAndEnabled && m_Chat.IsSpeaking(m_Slot);
            if (m_Light.enabled != on) m_Light.enabled = on;
            if (m_Chat == null) Destroy(this); // the session (and its voice chat) is gone
        }

        void OnDestroy()
        {
            if (m_Light != null) m_Light.enabled = false;
        }
    }
}

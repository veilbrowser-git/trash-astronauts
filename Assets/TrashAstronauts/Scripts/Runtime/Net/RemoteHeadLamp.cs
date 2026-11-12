using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// A crew mate's helmet lamp, for looking at only: a glow at the lamp housing on their helmet and a soft wide cone
    /// while <see cref="Core.Net.AvatarFlags.HeadLampOn"/> is set, and a real spot light only when the shared budget
    /// in <see cref="RemoteToolRig.UpdateLightBudget"/> hands it one (helmet lamps and flashlights compete for the
    /// same one or two lights). It sits on the helmet, so it turns with their head. Part of the RemoteCrew prefab only.
    /// </summary>
    public sealed class RemoteHeadLamp : MonoBehaviour
    {
        public GameObject lensGlow;
        public GameObject cone;
        public Light spot;

        public bool On { get; private set; }
        /// <summary>True while this lamp has one of the real spot lights.</summary>
        public bool HasRealLight => spot != null && spot.enabled;

        public void Set(bool on, bool force)
        {
            if (on == On && !force) return;
            On = on;
            if (lensGlow != null) lensGlow.SetActive(on);
            if (cone != null) cone.SetActive(on);
            if (!on) SetRealLight(false);
        }

        /// <summary>Gives or takes the real spot light (the budget calls it; previews may too).</summary>
        public void SetRealLight(bool on)
        {
            if (spot == null) return;
            on &= On;
            if (spot.enabled != on) spot.enabled = on;
        }

        void OnDisable() => SetRealLight(false);
    }
}

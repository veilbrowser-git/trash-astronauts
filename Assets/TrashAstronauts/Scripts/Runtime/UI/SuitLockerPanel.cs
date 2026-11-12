using TMPro;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.UI
{
    /// <summary>
    /// The SUIT LOCKER in the supply bay: twelve colour swatches (PlayerAvatar.SuitColors) and a CREW COLOUR button that
    /// goes back to the default, the slot's own colour. Pressing one dresses the local suit and gloves at once, saves the
    /// choice in the profile (SaveData.suitColor) and, in a session, the crew sees it within a frame (CrewLookSync sends
    /// it through the settings path). Works at any time in the hub, alone or in a crew. A display suit beside the swatches
    /// shows the whole look, since the wearer only sees their own arms, chest and gloves.
    /// </summary>
    public sealed class SuitLockerPanel : MonoBehaviour
    {
        public MissionDirector director;
        public TMP_Text status;
        [Tooltip("One button per suit palette colour, in palette order.")]
        public PushButton[] swatches;
        [Tooltip("Back to the default: wear the crew slot's colour.")]
        public PushButton crewColourButton;
        [Tooltip("Frame put around the chosen swatch.")]
        public Transform swatchMark;
        [Tooltip("Frame put around the CREW COLOUR button while the default is chosen.")]
        public Transform crewMark;
        [Tooltip("Optional display suit, dressed in the current choice.")]
        public PlayerAvatar dummy;

        int m_ShownColour = -1;
        int m_ShownSlot = -1;

        /// <summary>The colour the panel shows as chosen (0 = crew colour, 1..12 = palette).</summary>
        public int ShownColour => m_ShownColour;

        void OnEnable()
        {
            if (director == null) director = MissionDirector.Active;
            if (swatches != null) foreach (var b in swatches) if (b != null) b.Pressed += OnPressed;
            if (crewColourButton != null) crewColourButton.Pressed += OnPressed;
            if (director != null) director.ProfileChanged += ForceRefresh;
            ForceRefresh();
        }

        void OnDisable()
        {
            if (swatches != null) foreach (var b in swatches) if (b != null) b.Pressed -= OnPressed;
            if (crewColourButton != null) crewColourButton.Pressed -= OnPressed;
            if (director != null) director.ProfileChanged -= ForceRefresh;
        }

        void Start() => ForceRefresh();

        void Update()
        {
            // The slot changes when a session starts or ends; the default colour follows it.
            if (director == null) director = MissionDirector.Active;
            if (CrewLook.SavedColour(director) != m_ShownColour || CrewLook.LocalSlot(director) != m_ShownSlot) Refresh();
        }

        void OnPressed(PushButton button, HandRig hand)
        {
            if (button == crewColourButton) { Choose(SuitLook.Default); return; }
            if (swatches == null) return;
            for (int i = 0; i < swatches.Length; i++)
            {
                if (swatches[i] == button) { Choose(i + 1); return; }
            }
        }

        /// <summary>Wears and saves a colour: 0 = crew colour, 1..12 = PlayerAvatar.SuitColors[colour - 1]. Out of range = 0.</summary>
        public void Choose(int colour)
        {
            if (director == null) director = MissionDirector.Active;
            if (director == null || director.Profile == null) return;
            colour = SuitLook.Clamp(colour);
            director.Profile.Data.suitColor = colour;
            int slot = CrewLook.LocalSlot(director);
            CrewLook.ApplyLook(director.LocalPlayer, slot, colour);
            director.ApplyAndSaveSettings(); // saves, and refreshes this panel through ProfileChanged
            Refresh();
        }

        void ForceRefresh()
        {
            m_ShownColour = -1;
            Refresh();
        }

        public void Refresh()
        {
            int colour = CrewLook.SavedColour(director);
            int slot = CrewLook.LocalSlot(director);
            if (colour == m_ShownColour && slot == m_ShownSlot) return;
            ShowChoice(slot, colour);
        }

        /// <summary>Marks a choice, names it and dresses the display suit in it. Refresh calls it with the saved choice.</summary>
        public void ShowChoice(int slot, int colour)
        {
            colour = SuitLook.Clamp(colour);
            m_ShownColour = colour;
            m_ShownSlot = slot;

            if (status != null) status.text = "WEARING: " + CrewLook.NameOf(slot, colour);
            bool crew = colour == SuitLook.Default;
            if (crewMark != null)
            {
                crewMark.gameObject.SetActive(crew);
            }
            if (swatchMark != null)
            {
                PushButton chosen = !crew && swatches != null && colour - 1 < swatches.Length ? swatches[colour - 1] : null;
                swatchMark.gameObject.SetActive(chosen != null);
                if (chosen != null)
                {
                    Vector3 at = swatchMark.parent != null ? swatchMark.parent.InverseTransformPoint(chosen.transform.position) : chosen.transform.position;
                    swatchMark.localPosition = new Vector3(at.x, at.y, swatchMark.localPosition.z);
                }
            }
            if (dummy != null) CrewLook.ApplyLook(dummy, null, null, slot, colour);
        }
    }
}

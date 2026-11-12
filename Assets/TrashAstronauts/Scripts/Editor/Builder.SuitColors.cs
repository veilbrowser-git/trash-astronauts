using TMPro;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.UI;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// The SUIT LOCKER: where a player picks the colour of their suit (see <see cref="SuitLockerPanel"/>, CrewLook and
    /// PlayerAvatar.SuitColors). In the supply bay, on the north wall between the company store (west wall) and the row
    /// of crew lockers, facing into the bay: clear of the store's counter and awning, the lockers, the doorway to the
    /// flight deck and every crew spot (those are all in the cabin). Left: a display suit dressed in the current choice.
    /// Right: twelve swatches (3 x 4, palette order, each named so the choice never depends on colour alone), the CREW
    /// COLOUR button (back to the default) and what is being worn. Buttons are pressed with a finger like every other.
    /// </summary>
    public static partial class Builder
    {
        /// <summary>Where the panel hangs (centre of its face) and which way its face looks.</summary>
        public static readonly Vector3 SuitLockerPosition = new Vector3(-4.96f, 1.62f, -3f - T * 0.5f - 0.02f);
        public static readonly Vector3 SuitLockerOutward = Vector3.back;
        public static readonly Vector2 SuitLockerSize = new Vector2(1.04f, 1.0f);
        public static readonly Vector2 SuitSwatchSize = new Vector2(0.14f, 0.095f);

        static SuitLockerPanel BuildSuitLocker(MissionDirector director)
        {
            var root = Empty(s_Props, "Suit Locker", SuitLockerPosition, Facing(SuitLockerOutward), GameLayers.Default);
            Transform t = root.transform;
            float halfH = SuitLockerSize.y * 0.5f;
            Prim(t, "Back", PrimitiveType.Cube, new Vector3(0, 0, 0.012f), new Vector3(SuitLockerSize.x, SuitLockerSize.y, 0.02f), M("Screen"));
            CBox(t, "Header Bar", new Vector3(0, halfH - 0.015f, 0.006f), new Vector3(SuitLockerSize.x, 0.03f, 0.03f), M("GlowTeal"), 0.006f);
            var title = Text(t, "Title", "SUIT LOCKER", new Vector3(0, 0.415f, -0.002f), Quaternion.identity, 0.62f, new Vector2(1.0f, 0.09f), new Color(0.6f, 1f, 0.9f));
            title.fontStyle = FontStyles.Bold;
            Text(t, "Subtitle", "Pick your suit colour. Your crew sees it too.", new Vector3(0, 0.345f, -0.002f), Quaternion.identity, 0.26f,
                new Vector2(1.0f, 0.06f), new Color(0.85f, 0.95f, 1f));

            var panel = root.AddComponent<SuitLockerPanel>();
            panel.director = director;

            // Twelve swatches, three across and four down, in palette order.
            int count = PlayerAvatar.SuitColors.Length;
            var swatches = new PushButton[count];
            for (int i = 0; i < count; i++)
            {
                Color c = PlayerAvatar.SuitColors[i];
                var mat = Lit("Swatch_" + PlayerAvatar.SuitColorNames[i], c, 0.35f);
                var button = Button(t, "Swatch " + PlayerAvatar.SuitColorNames[i], SuitSwatchCentre(i), Quaternion.identity, SuitSwatchSize,
                    PlayerAvatar.SuitColorNames[i], mat, 0.19f);
                // Dark lettering on the light colours, white on the rest.
                float luma = 0.3f * c.r + 0.59f * c.g + 0.11f * c.b;
                button.label.color = luma > 0.55f ? new Color(0.06f, 0.06f, 0.07f) : Color.white;
                swatches[i] = button;
            }
            panel.swatches = swatches;
            panel.crewColourButton = Button(t, "Crew Colour", new Vector3(0.21f, -0.32f, 0), Quaternion.identity, new Vector2(0.51f, 0.085f),
                "CREW COLOUR", M("ButtonBlue"), 0.24f);
            panel.status = Text(t, "Status", "", new Vector3(0.21f, -0.44f, -0.002f), Quaternion.identity, 0.24f, new Vector2(0.54f, 0.07f), new Color(1f, 0.85f, 0.4f));

            // The marks: a white frame drawn on the chosen swatch's face, and one on CREW COLOUR while the default is worn.
            panel.swatchMark = SuitMark(t, "Chosen Mark", SuitSwatchCentre(0), SuitSwatchSize);
            panel.crewMark = SuitMark(t, "Crew Colour Mark", new Vector3(0.21f, -0.32f, 0), new Vector2(0.51f, 0.085f));
            panel.swatchMark.gameObject.SetActive(false);

            // The display suit: the same model as everyone's, about a third of the size, standing on a ledge and facing the room.
            Prim(t, "Display Ledge", PrimitiveType.Cube, new Vector3(-0.33f, -0.415f, -0.07f), new Vector3(0.3f, 0.02f, 0.14f), M("Trim"));
            var dummy = BuildAvatar(t, "Display Suit", true);
            dummy.transform.localPosition = new Vector3(-0.33f, -0.405f, -0.07f);
            dummy.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            dummy.transform.localScale = Vector3.one * 0.37f;
            dummy.head = null; // posed once here, never again (no sources)
            dummy.SetTint(PlayerAvatar.CrewColors[0]);
            panel.dummy = dummy;

            var sync = root.AddComponent<CrewLookSync>();
            sync.director = director;
            return panel;
        }

        /// <summary>Centre of swatch <paramref name="index"/> on the panel (panel space).</summary>
        public static Vector3 SuitSwatchCentre(int index)
        {
            int col = index % 3, row = index / 3;
            return new Vector3(0.03f + col * 0.18f, 0.24f - row * 0.135f, 0f);
        }

        /// <summary>A thin white frame lying on a button's face, inset from its edges.</summary>
        static Transform SuitMark(Transform parent, string name, Vector3 centre, Vector2 size)
        {
            var mark = Empty(parent, name, new Vector3(centre.x, centre.y, -0.026f), null, GameLayers.Default).transform;
            float bar = 0.011f, w = size.x - 0.01f, h = size.y - 0.01f;
            var mat = M("GlowWhite");
            Prim(mark, "Top", PrimitiveType.Cube, new Vector3(0, h * 0.5f - bar * 0.5f, 0), new Vector3(w, bar, 0.003f), mat);
            Prim(mark, "Bottom", PrimitiveType.Cube, new Vector3(0, -h * 0.5f + bar * 0.5f, 0), new Vector3(w, bar, 0.003f), mat);
            Prim(mark, "Left", PrimitiveType.Cube, new Vector3(-w * 0.5f + bar * 0.5f, 0, 0), new Vector3(bar, h, 0.003f), mat);
            Prim(mark, "Right", PrimitiveType.Cube, new Vector3(w * 0.5f - bar * 0.5f, 0, 0), new Vector3(bar, h, 0.003f), mat);
            return mark;
        }
    }
}

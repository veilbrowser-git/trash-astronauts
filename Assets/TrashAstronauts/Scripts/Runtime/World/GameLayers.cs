using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>
    /// Physics layers used by the game. Indices are fixed here and written into the project's
    /// Tags and Layers settings by the project builder, so code and data cannot drift apart.
    /// </summary>
    public static class GameLayers
    {
        public const int Default = 0;
        public const int Environment = 6;
        public const int Salvage = 7;
        public const int Tool = 8;
        public const int Player = 9;
        public const int Hand = 10;
        public const int CutPoint = 11;
        public const int Button = 12;
        public const int Rail = 13;
        public const int TeleportSurface = 14;
        public const int Zone = 15;
        public const int Enemy = 16;
        /// <summary>The wearer's own suit and glove meshes while the helmet lamp is lit (it does not light this layer). No colliders.</summary>
        public const int LocalSuit = 18;


        public static readonly (int index, string name)[] Names =
        {
            (Environment, "Environment"),
            (Salvage, "Salvage"),
            (Tool, "Tool"),
            (Player, "Player"),
            (Hand, "Hand"),
            (CutPoint, "CutPoint"),
            (Button, "Button"),
            (Rail, "Rail"),
            (TeleportSurface, "TeleportSurface"),
            (Zone, "Zone"),
            (Enemy, "Enemy"),
            (LocalSuit, "LocalSuit"),
        };

        public const int EnvironmentMask = 1 << Environment;
        public const int SalvageMask = 1 << Salvage;
        public const int ToolMask = 1 << Tool;
        public const int CutPointMask = 1 << CutPoint;
        public const int ButtonMask = 1 << Button;
        public const int RailMask = 1 << Rail;
        public const int TeleportSurfaceMask = 1 << TeleportSurface;
        /// <summary>What a hand can reach out and grab.</summary>
        public const int GrabbableMask = SalvageMask | ToolMask | RailMask;

        static bool s_Applied;

        /// <summary>
        /// Sets the layer collision matrix. The player body only collides with the environment, so a held
        /// or floating object can never shove or launch the player.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void ApplyCollisionMatrix()
        {
            if (s_Applied && Application.isPlaying) return;
            s_Applied = true;

            int[] all = { Default, Environment, Salvage, Tool, Player, Hand, CutPoint, Button, Rail, TeleportSurface, Zone, Enemy };

            // Start from "nothing collides" for our layers, then enable the pairs we want.
            foreach (int a in all)
            {
                foreach (int b in all)
                {
                    if (a == Default && b == Default) continue;
                    Physics.IgnoreLayerCollision(a, b, true);
                }
            }

            Allow(Player, Environment);
            Allow(Player, Default);
            Allow(Salvage, Environment);
            Allow(Salvage, Salvage);
            Allow(Salvage, Tool);
            Allow(Salvage, Enemy);
            Allow(Salvage, Default);
            Allow(Tool, Environment);
            Allow(Tool, Default);
            Allow(Enemy, Default);
            Allow(Default, Environment);
        }

        static void Allow(int a, int b) => Physics.IgnoreLayerCollision(a, b, false);

        public static void SetLayerRecursively(GameObject root, int layer)
        {
            root.layer = layer;
            foreach (Transform child in root.transform) SetLayerRecursively(child.gameObject, layer);
        }
    }
}

using UnityEngine;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Where a point of a wreck's layout, as the tests were first written against it, is in the built wreck.
    /// Every wreck is built bigger than its first layout about the airlock join (x = 0, z = 7.6), which does
    /// not move; the airlock and the tug (z below 7.6) are the same as ever. Mirrors Builder.FreighterPoint and
    /// Builder.WreckPoint (the play-mode tests do not reference the editor assembly).
    /// </summary>
    public static class MapPoints
    {
        /// <summary>The freighter: 1.5x its first layout (a transform stretch), then 1.5x again (the bigger-maps update).</summary>
        public const float FreighterScale = 2.25f;
        /// <summary>The ore barge, the survey ship and the lava tube: 1.5x (the bigger-maps update).</summary>
        public const float WreckScale = 1.5f;
        public const float JoinZ = 7.6f;

        static Vector3 Plan(float x, float y, float z, float k) => z <= JoinZ ? new Vector3(x, y, z) : new Vector3(x * k, y, JoinZ + (z - JoinZ) * k);

        public static Vector3 Freighter(float x, float y, float z) => Plan(x, y, z, FreighterScale);
        public static Vector3 Wreck(float x, float y, float z) => Plan(x, y, z, WreckScale);
    }
}

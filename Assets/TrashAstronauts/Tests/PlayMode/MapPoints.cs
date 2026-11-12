using UnityEngine;

namespace TrashAstronauts.Tests
{
    /// <summary>Where points of the original freighter layout are now that the wreck is stretched 1.5x about the airlock door.</summary>
    public static class MapPoints
    {
        public static Vector3 Freighter(float x, float y, float z) => z <= 7.6f ? new Vector3(x, y, z) : new Vector3(x * 1.5f, y, 7.6f + (z - 7.6f) * 1.5f);
    }
}

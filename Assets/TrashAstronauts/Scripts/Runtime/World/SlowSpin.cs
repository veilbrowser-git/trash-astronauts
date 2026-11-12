using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>Turns something slowly on the spot (goods on display).</summary>
    public sealed class SlowSpin : MonoBehaviour
    {
        public Vector3 degreesPerSecond = new Vector3(0f, 25f, 0f);
        void Update() => transform.Rotate(degreesPerSecond * Time.deltaTime, Space.Self);
    }
}

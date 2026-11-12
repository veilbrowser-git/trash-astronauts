using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>
    /// Keeps one small particle system of floating dust centred on the player's head. The particles live
    /// in world space, so they hang in the air as the player moves; they are lit, so they only show where
    /// a lamp or the flashlight catches them. Purely visual: no collision, no allocation per frame.
    /// </summary>
    [RequireComponent(typeof(ParticleSystem))]
    public sealed class DustMotes : MonoBehaviour
    {
        [Tooltip("Followed each frame; the main camera if left empty.")]
        public Transform target;

        void LateUpdate()
        {
            if (target == null)
            {
                var cam = Camera.main;
                if (cam == null) return;
                target = cam.transform;
            }
            transform.position = target.position;
        }
    }
}

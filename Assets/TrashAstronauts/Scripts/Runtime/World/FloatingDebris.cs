using UnityEngine;

namespace TrashAstronauts.World
{
    /// <summary>Gives a loose object a slow drift and tumble so the wreck feels weightless.</summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class FloatingDebris : MonoBehaviour
    {
        public float drift = 0.05f;
        public float tumble = 0.25f;

        void Start()
        {
            var body = GetComponent<Rigidbody>();
            if (body.isKinematic) return;
            body.AddForce(Random.insideUnitSphere * drift, ForceMode.VelocityChange);
            body.AddTorque(Random.insideUnitSphere * tumble, ForceMode.VelocityChange);
        }
    }
}

using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Audio
{
    /// <summary>
    /// Makes rooms sound their size. Twice a second it casts a few rays from the listener to the walls, smooths
    /// the mean distance into a room size, and drives one AudioReverbZone that follows the listener: a narrow
    /// duct gets a short, quiet tail, a big cargo hold a longer one. Kept subtle so cues stay readable.
    /// Helmet sounds bypass reverb zones, so breathing stays dry.
    /// </summary>
    public sealed class RoomAcoustics : MonoBehaviour
    {
        public AudioReverbZone zone;
        public LayerMask walls = GameLayers.EnvironmentMask;
        public float checkInterval = 0.5f;
        [Tooltip("Ray length; a ray that hits nothing counts as this far.")]
        public float maxDistance = 25f;
        [Tooltip("How fast the smoothed size follows a change (fraction per second).")]
        public float smoothing = 2f;
        [Tooltip("Room sizes (mean wall distance, m) mapped to the smallest and largest reverb.")]
        public float smallSize = 1f, largeSize = 12f;
        public float minDecay = 0.3f, maxDecay = 2.2f;
        [Tooltip("Reverb send level in millibels at small and large sizes (0 is full, -10000 silent).")]
        public int minRoom = -2200, maxRoom = -1100;

        static readonly Vector3[] k_Directions =
        {
            Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back,
            new Vector3(0.577f, 0.577f, 0.577f), new Vector3(-0.577f, -0.577f, 0.577f),
            new Vector3(0.577f, -0.577f, -0.577f), new Vector3(-0.577f, 0.577f, -0.577f),
        };

        /// <summary>The latest raw measurement: mean distance to the walls in metres.</summary>
        public float MeasuredSize { get; private set; }
        /// <summary>The smoothed room size the reverb follows.</summary>
        public float RoomSize { get; private set; } = -1f;
        /// <summary>0 for a small space, 1 for a large one.</summary>
        public float Openness => Mathf.InverseLerp(smallSize, largeSize, RoomSize);
        public float DecayTime => Mathf.Lerp(minDecay, maxDecay, Openness);
        public int RoomLevel => Mathf.RoundToInt(Mathf.Lerp(minRoom, maxRoom, Openness));

        float m_NextCheck;

        void Update() => Step(Time.deltaTime);

        /// <summary>Advances the measurement timer and smoothing by dt.</summary>
        public void Step(float dt)
        {
            m_NextCheck -= dt;
            if (m_NextCheck <= 0f || RoomSize < 0f)
            {
                m_NextCheck = checkInterval;
                Measure();
                if (RoomSize < 0f) RoomSize = MeasuredSize;
            }
            RoomSize = Mathf.Lerp(RoomSize, MeasuredSize, 1f - Mathf.Exp(-smoothing * dt));
            Apply();
        }

        /// <summary>Casts the rays now and stores the mean distance.</summary>
        public void Measure()
        {
            Vector3 origin = transform.position;
            float sum = 0f;
            for (int i = 0; i < k_Directions.Length; i++)
            {
                sum += Physics.Raycast(origin, k_Directions[i], out var hit, maxDistance, walls, QueryTriggerInteraction.Ignore)
                    ? hit.distance : maxDistance;
            }
            MeasuredSize = sum / k_Directions.Length;
        }

        void Apply()
        {
            if (zone == null) return;
            zone.transform.position = transform.position;
            zone.decayTime = DecayTime;
            zone.room = RoomLevel;
        }
    }
}

using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.Audio
{
    /// <summary>
    /// Muffles a positional sound when ship geometry stands between it and the listener. A few times a second
    /// it linecasts from the source to the listener against the wall layers; the result is smoothed into an
    /// occlusion amount that lowers the cutoff of the AudioLowPassFilter beside it and scales the signal down.
    /// Purely what the player hears: the gameplay noise the creature hears is untouched.
    /// The volume scale is applied in the audio filter chain, so scripts that set AudioSource.volume keep working.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class SoundOcclusion : MonoBehaviour
    {
        public AudioLowPassFilter lowPass;
        public LayerMask walls = GameLayers.EnvironmentMask;
        [Tooltip("Seconds between line-of-sight checks.")]
        public float checkInterval = 0.2f;
        [Tooltip("How fast the occlusion amount follows a change (per second).")]
        public float smoothing = 5f;
        public float clearCutoff = 22000f;
        public float occludedCutoff = 850f;
        [Range(0f, 1f)] public float occludedVolume = 0.45f;
        [Tooltip("The linecast starts and ends this far inside each end, so a source on a wall is not hidden by that wall.")]
        public float endInset = 0.25f;
        /// <summary>Optional listener for tests; otherwise the scene's AudioListener.</summary>
        public Transform listenerOverride;

        /// <summary>True when the last check found a wall in the way.</summary>
        public bool Blocked { get; private set; }
        /// <summary>Smoothed occlusion, 0 clear .. 1 behind a wall.</summary>
        public float Occlusion { get; private set; }
        public float Cutoff => Mathf.Exp(Mathf.Lerp(Mathf.Log(clearCutoff), Mathf.Log(occludedCutoff), Occlusion));
        public float VolumeFactor => Mathf.Lerp(1f, occludedVolume, Occlusion);

        static AudioListener s_Listener;
        AudioSource m_Source;
        float m_NextCheck;
        volatile float m_Gain = 1f;
        float m_AppliedGain = 1f;

        void Awake()
        {
            m_Source = GetComponent<AudioSource>();
            if (lowPass == null) lowPass = GetComponent<AudioLowPassFilter>();
            m_NextCheck = Random.value * checkInterval;
        }

        void OnEnable() => Apply();

        Transform Listener()
        {
            if (listenerOverride != null) return listenerOverride;
            if (s_Listener == null || !s_Listener.isActiveAndEnabled) s_Listener = FindFirstObjectByType<AudioListener>();
            return s_Listener != null ? s_Listener.transform : null;
        }

        void Update() => Step(Time.deltaTime);

        /// <summary>Advances the check timer and smoothing by dt.</summary>
        public void Step(float dt)
        {
            m_NextCheck -= dt;
            if (m_NextCheck <= 0f)
            {
                m_NextCheck += checkInterval;
                if (m_NextCheck < 0f) m_NextCheck = checkInterval;
                Check();
            }
            Occlusion = Mathf.MoveTowards(Occlusion, Blocked ? 1f : 0f, smoothing * dt);
            Apply();
        }

        /// <summary>Runs the line-of-sight check now.</summary>
        public void Check()
        {
            var listener = Listener();
            if (listener == null) { Blocked = false; return; }
            Vector3 from = transform.position, to = listener.position;
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            float range = m_Source != null ? m_Source.maxDistance : 50f;
            if (distance > range) return; // inaudible anyway; keep the last state
            if (distance <= endInset * 2f + 0.05f) { Blocked = false; return; }
            Vector3 dir = delta / distance;
            Blocked = Physics.Linecast(from + dir * endInset, to - dir * endInset, walls, QueryTriggerInteraction.Ignore);
        }

        void Apply()
        {
            if (lowPass != null)
            {
                lowPass.enabled = Occlusion > 0.001f;
                lowPass.cutoffFrequency = Cutoff;
            }
            m_Gain = VolumeFactor;
        }

        // Scales the signal after the low-pass; ramps across each buffer so changes never click.
        void OnAudioFilterRead(float[] data, int channels)
        {
            float target = m_Gain, start = m_AppliedGain;
            if (Mathf.Approximately(start, 1f) && Mathf.Approximately(target, 1f)) return;
            int frames = data.Length / Mathf.Max(1, channels);
            float step = frames > 0 ? (target - start) / frames : 0f;
            float g = start;
            for (int i = 0, f = 0; f < frames; f++)
            {
                g += step;
                for (int c = 0; c < channels; c++, i++) data[i] *= g;
            }
            m_AppliedGain = target;
        }
    }
}

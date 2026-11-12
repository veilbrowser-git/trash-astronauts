using System.Collections.Generic;
using UnityEngine;

namespace TrashAstronauts.Audio
{
    /// <summary>
    /// Small pooled one-shot player. Keeps the number of live AudioSources bounded, which matters on Quest.
    ///
    /// Positional one-shots get a little natural variation so repeated sounds never play back identically:
    /// a small random pitch offset around the requested pitch, a slightly lower random level, and (for
    /// clips that have them) a random alternative take. Clips the library marks as tonal (alarms, jingles,
    /// scanner tones) and everything played through <see cref="PlayUi"/> are left exactly as requested.
    /// </summary>
    public sealed class Sfx : MonoBehaviour
    {
        const int k_PoolSize = 12;

        /// <summary>Random pitch spread for positional one-shots, as a fraction (0.04 = +/-4%). 0 disables it.</summary>
        public static float PitchJitter = 0.04f;

        /// <summary>Random level reduction for positional one-shots, as a fraction (0.08 = up to 8% quieter). 0 disables it.</summary>
        public static float VolumeJitter = 0.08f;

        static Sfx s_Instance;
        // Private generator: variation must not consume UnityEngine.Random, which gameplay may have seeded.
        static uint s_Random = 0x9E3779B9u;
        readonly List<AudioSource> m_Pool = new List<AudioSource>();
        int m_Next;

        public static AudioLibrary Library { get; private set; }

        public static void SetLibrary(AudioLibrary library) => Library = library;

        static Sfx Instance
        {
            get
            {
                if (s_Instance == null)
                {
                    var go = new GameObject("Sfx Pool");
                    s_Instance = go.AddComponent<Sfx>();
                    for (int i = 0; i < k_PoolSize; i++)
                    {
                        var child = new GameObject("Sfx " + i);
                        child.transform.SetParent(go.transform, false);
                        var source = child.AddComponent<AudioSource>();
                        source.playOnAwake = false;
                        source.rolloffMode = AudioRolloffMode.Linear;
                        source.minDistance = 1f;
                        source.maxDistance = 22f;
                        source.dopplerLevel = 0f;
                        s_Instance.m_Pool.Add(source);
                    }
                }
                return s_Instance;
            }
        }

        void OnDestroy()
        {
            if (s_Instance == this) s_Instance = null;
        }

        /// <summary>
        /// Plays a positional one-shot. Non-tonal clips are varied slightly (see <see cref="PitchJitter"/>,
        /// <see cref="VolumeJitter"/>); the pitch stays within a few percent of the one requested.
        /// </summary>
        public static void PlayAt(AudioClip clip, Vector3 position, float volume = 1f, float pitch = 1f, float maxDistance = 22f)
        {
            if (clip == null) return;
            bool vary = Library == null || !Library.IsTonal(clip);
            PlayVaried(clip, position, volume, pitch, maxDistance, vary ? PitchJitter : 0f, vary ? VolumeJitter : 0f);
        }

        /// <summary>
        /// Plays a positional one-shot with explicit variation amounts: pitch is multiplied by a random
        /// factor in 1 +/- pitchJitter, volume by a random factor in (1 - volumeJitter)..1. Pass 0 for
        /// both to play the clip exactly as given (no alternative take either).
        /// </summary>
        public static void PlayVaried(AudioClip clip, Vector3 position, float volume, float pitch, float maxDistance, float pitchJitter, float volumeJitter)
        {
            if (clip == null || !Application.isPlaying) return;
            pitchJitter = Mathf.Clamp(pitchJitter, 0f, 0.5f);
            volumeJitter = Mathf.Clamp01(volumeJitter);
            if (Library != null && (pitchJitter > 0f || volumeJitter > 0f))
            {
                var take = Library.Variant(clip);
                if (take != null) clip = take;
            }
            var source = Instance.NextSource();
            source.transform.position = position;
            source.spatialBlend = 1f;
            source.maxDistance = maxDistance;
            source.pitch = pitch * (1f + pitchJitter * (2f * Next01() - 1f));
            source.volume = Mathf.Clamp01(volume * (1f - volumeJitter * Next01()));
            source.clip = clip;
            source.Play();
        }

        /// <summary>Plays a non-positional one-shot (suit alarms, UI). Never varied.</summary>
        /// <summary>The sound library, or null before one is set (never throws).</summary>
        public static AudioLibrary LibraryOrNull => Library;

        /// <summary>How many non-positional one-shots have been played (test seam: "nothing in your ears").</summary>
        public static int UiPlayCount { get; private set; }

        public static void PlayUi(AudioClip clip, float volume = 1f, float pitch = 1f)
        {
            if (clip == null || !Application.isPlaying) return;
            UiPlayCount++;
            var source = Instance.NextSource();
            source.spatialBlend = 0f;
            source.pitch = pitch;
            source.volume = Mathf.Clamp01(volume);
            source.clip = clip;
            source.Play();
        }

        AudioSource NextSource()
        {
            for (int i = 0; i < m_Pool.Count; i++)
            {
                int index = (m_Next + i) % m_Pool.Count;
                if (!m_Pool[index].isPlaying)
                {
                    m_Next = (index + 1) % m_Pool.Count;
                    return m_Pool[index];
                }
            }
            var fallback = m_Pool[m_Next];
            m_Next = (m_Next + 1) % m_Pool.Count;
            return fallback;
        }

        /// <summary>Uniform 0..1 from a private xorshift32 generator.</summary>
        static float Next01()
        {
            s_Random ^= s_Random << 13;
            s_Random ^= s_Random >> 17;
            s_Random ^= s_Random << 5;
            return (s_Random & 0xFFFFFFu) / 16777215f;
        }
    }
}

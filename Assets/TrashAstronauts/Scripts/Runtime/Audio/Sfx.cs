using System.Collections.Generic;
using UnityEngine;

namespace TrashAstronauts.Audio
{
    /// <summary>
    /// Small pooled one-shot player. Keeps the number of live AudioSources bounded, which matters on Quest.
    /// </summary>
    public sealed class Sfx : MonoBehaviour
    {
        const int k_PoolSize = 12;

        static Sfx s_Instance;
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

        /// <summary>Plays a positional one-shot.</summary>
        public static void PlayAt(AudioClip clip, Vector3 position, float volume = 1f, float pitch = 1f, float maxDistance = 22f)
        {
            if (clip == null || !Application.isPlaying) return;
            var source = Instance.NextSource();
            source.transform.position = position;
            source.spatialBlend = 1f;
            source.maxDistance = maxDistance;
            source.pitch = pitch;
            source.volume = Mathf.Clamp01(volume);
            source.clip = clip;
            source.Play();
        }

        /// <summary>Plays a non-positional one-shot (suit alarms, UI).</summary>
        public static void PlayUi(AudioClip clip, float volume = 1f, float pitch = 1f)
        {
            if (clip == null || !Application.isPlaying) return;
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
    }
}

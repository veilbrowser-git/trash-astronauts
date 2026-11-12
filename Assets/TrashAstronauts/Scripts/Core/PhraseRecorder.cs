using System.Collections.Generic;
using UnityEngine;

namespace TrashAstronauts.Core
{
    /// <summary>
    /// Cuts a stream of microphone samples into short spoken phrases, for the Mimic to play back.
    /// Everything stays in memory: a phrase is a float array, at most a few are kept, and
    /// <see cref="Clear"/> throws them all away. Pure logic, no microphone, so it can be tested directly.
    /// </summary>
    public sealed class PhraseRecorder
    {
        public readonly int sampleRate;
        public float preRollSeconds = 0.25f;
        public float endSilenceSeconds = 0.4f;
        public float maxSeconds = 4f;
        public float minVoicedSeconds = 0.3f;
        public int maxPhrases = 4;

        readonly float[] m_PreRoll;
        int m_PreWrite, m_PreCount;
        readonly List<float> m_Current = new List<float>();
        readonly List<float[]> m_Phrases = new List<float[]>();
        bool m_Recording;
        int m_SilentSamples, m_VoicedSamples;

        /// <summary>Goes up by one every time a new phrase is finished.</summary>
        public int Version { get; private set; }
        public int Count => m_Phrases.Count;
        public bool IsRecording => m_Recording;
        public float[] Newest => m_Phrases.Count > 0 ? m_Phrases[m_Phrases.Count - 1] : null;
        public float[] Get(int index) => m_Phrases[Mathf.Clamp(index, 0, m_Phrases.Count - 1)];

        public PhraseRecorder(int sampleRate)
        {
            this.sampleRate = Mathf.Max(1000, sampleRate);
            m_PreRoll = new float[Mathf.Max(1, Mathf.RoundToInt(this.sampleRate * preRollSeconds))];
        }

        /// <summary>Feeds the next block of samples. <paramref name="speaking"/> says whether this block is above the background.</summary>
        public void Feed(float[] samples, int count, bool speaking)
        {
            if (samples == null || count <= 0) return;
            count = Mathf.Min(count, samples.Length);

            if (!m_Recording)
            {
                if (!speaking)
                {
                    for (int i = 0; i < count; i++)
                    {
                        m_PreRoll[m_PreWrite] = samples[i];
                        m_PreWrite = (m_PreWrite + 1) % m_PreRoll.Length;
                        if (m_PreCount < m_PreRoll.Length) m_PreCount++;
                    }
                    return;
                }
                // Speech starts: begin with the moment just before it, so the first syllable is not clipped.
                m_Recording = true;
                m_Current.Clear();
                int start = (m_PreWrite - m_PreCount + m_PreRoll.Length) % m_PreRoll.Length;
                for (int i = 0; i < m_PreCount; i++) m_Current.Add(m_PreRoll[(start + i) % m_PreRoll.Length]);
                m_PreCount = 0;
                m_SilentSamples = 0;
                m_VoicedSamples = 0;
            }

            for (int i = 0; i < count; i++) m_Current.Add(samples[i]);
            if (speaking)
            {
                m_VoicedSamples += count;
                m_SilentSamples = 0;
            }
            else m_SilentSamples += count;

            if (m_SilentSamples >= endSilenceSeconds * sampleRate || m_Current.Count >= maxSeconds * sampleRate) Finish();
        }

        void Finish()
        {
            m_Recording = false;
            if (m_VoicedSamples >= minVoicedSeconds * sampleRate)
            {
                // Keep a short tail of the closing silence, drop the rest.
                int keep = m_Current.Count - Mathf.Max(0, m_SilentSamples - Mathf.RoundToInt(0.1f * sampleRate));
                Add(m_Current.GetRange(0, Mathf.Clamp(keep, 1, m_Current.Count)).ToArray());
            }
            m_Current.Clear();
        }

        /// <summary>Stores a finished phrase (levelled, with short fades so it does not click).</summary>
        public void Add(float[] phrase)
        {
            if (phrase == null || phrase.Length < 16) return;
            var copy = (float[])phrase.Clone();
            float peak = 0f;
            for (int i = 0; i < copy.Length; i++) peak = Mathf.Max(peak, Mathf.Abs(copy[i]));
            if (peak > 1e-4f)
            {
                float gain = Mathf.Min(0.9f / peak, 30f);
                for (int i = 0; i < copy.Length; i++) copy[i] *= gain;
            }
            int fade = Mathf.Min(copy.Length / 2, Mathf.RoundToInt(0.008f * sampleRate));
            for (int i = 0; i < fade; i++)
            {
                float k = i / (float)fade;
                copy[i] *= k;
                copy[copy.Length - 1 - i] *= k;
            }
            m_Phrases.Add(copy);
            while (m_Phrases.Count > Mathf.Max(1, maxPhrases)) m_Phrases.RemoveAt(0);
            Version++;
        }

        /// <summary>Abandons a phrase in progress (used while the Mimic itself is talking).</summary>
        public void Abort()
        {
            m_Recording = false;
            m_Current.Clear();
            m_PreCount = 0;
        }

        /// <summary>Forgets every phrase.</summary>
        public void Clear()
        {
            Abort();
            m_Phrases.Clear();
        }
    }
}

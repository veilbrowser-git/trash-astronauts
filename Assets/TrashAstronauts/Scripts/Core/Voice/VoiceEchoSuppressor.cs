using System;

namespace TrashAstronauts.Core.Voice
{
    /// <summary>
    /// A deliberately simple echo suppressor for voice coming out of the headset's own speakers. While a crew
    /// member's voice is being played (and for a hold time after it, which covers the output latency and the
    /// room's tail) the microphone only counts as the player when it is clearly louder than what the playback
    /// explains: <c>mic &gt; playback + coupling + margin</c>. Anything quieter is treated as echo: it is not sent
    /// (so a friend never hears themselves back and two headsets can never build up a feedback howl) and it is
    /// not reported to the creature as the player's voice.
    ///
    /// "Playback" is the level being sent to the speakers (each remote voice frame's level times its distance
    /// and wall attenuation), held at its peak over the hold time. "Coupling" is how loud the speakers are at the
    /// microphone relative to that; it starts at a conservative 0 dB and is learned as the upper envelope of
    /// mic-minus-playback over frames where the player is probably not talking (their mic is not near their own
    /// talking level), clamped to a safe range and decaying slowly, so it adapts to the headset volume without a
    /// way to talk itself into letting echo through. Double talk works when the player speaks clearly louder than
    /// the echo, which a microphone a few centimetres from the mouth normally does.
    /// </summary>
    public sealed class VoiceEchoSuppressor
    {
        /// <summary>How far above the explained echo the microphone must be to count as the player (dB).</summary>
        public float marginDb = 6f;
        /// <summary>The playback level is held at its peak for this long (s).</summary>
        public float holdSeconds = 0.35f;
        /// <summary>Playback quieter than this (dBFS) does not count.</summary>
        public float activeDb = -55f;
        public float initialCouplingDb = 0f;
        public float minCouplingDb = -30f;
        public float maxCouplingDb = 6f;
        /// <summary>How fast the learned coupling relaxes downward (dB per second).</summary>
        public float couplingDecayDbPerSecond = 0.5f;
        /// <summary>Largest step up of the learned coupling per frame (dB).</summary>
        public float couplingRiseDbPerFrame = 1.5f;
        /// <summary>A frame within this many dB of the player's usual talking level is not used to learn the coupling.</summary>
        public float talkGuardDb = 8f;

        const int k_Buckets = 64; // 20 ms each: 1.28 s, longer than any hold time used
        readonly float[] m_BucketDb = new float[k_Buckets];
        readonly long[] m_BucketStamp = new long[k_Buckets];
        float m_Coupling;
        double m_LastLearn = double.NaN;

        /// <summary>The player's own talking level (dBFS), learned from frames sent while nothing was playing.</summary>
        public float TalkLevelDb { get; private set; } = -28f;
        public float CouplingDb => m_Coupling;
        /// <summary>Frames judged to be echo since creation.</summary>
        public long EchoFrames { get; private set; }

        public VoiceEchoSuppressor()
        {
            Reset();
        }

        public void Reset()
        {
            m_Coupling = initialCouplingDb;
            for (int i = 0; i < k_Buckets; i++)
            {
                m_BucketDb[i] = -100f;
                m_BucketStamp[i] = long.MinValue;
            }
            m_LastLearn = double.NaN;
        }

        static long Bucket(double now) => (long)Math.Floor(now / VoiceFormat.FrameSeconds);

        /// <summary>Reports that sound of this level (dBFS, all remote voices together) is going to the speakers now.</summary>
        public void AddPlayback(float levelDb, double now)
        {
            if (float.IsNaN(levelDb)) return;
            long b = Bucket(now);
            int i = (int)(((b % k_Buckets) + k_Buckets) % k_Buckets);
            if (m_BucketStamp[i] != b)
            {
                m_BucketStamp[i] = b;
                m_BucketDb[i] = levelDb;
            }
            else if (levelDb > m_BucketDb[i]) m_BucketDb[i] = levelDb;
        }

        /// <summary>The loudest playback within the hold time (dBFS; -100 when none).</summary>
        public float ReferenceDb(double now)
        {
            long newest = Bucket(now);
            long oldest = newest - Math.Max(1, (long)Math.Ceiling(holdSeconds / VoiceFormat.FrameSeconds));
            float best = -100f;
            for (int i = 0; i < k_Buckets; i++)
            {
                long stamp = m_BucketStamp[i];
                if (stamp >= oldest && stamp <= newest && m_BucketDb[i] > best) best = m_BucketDb[i];
            }
            return best;
        }

        /// <summary>True while remote voice is playing or was within the hold time.</summary>
        public bool PlaybackActive(double now) => ReferenceDb(now) > activeDb;

        /// <summary>The microphone level the playback can explain right now (dBFS), or -100 when nothing plays.</summary>
        public float ExplainedDb(double now)
        {
            float reference = ReferenceDb(now);
            return reference > activeDb ? reference + m_Coupling : -100f;
        }

        /// <summary>True when a microphone level of <paramref name="micDb"/> is not clearly above what the playback explains.</summary>
        public bool IsEcho(float micDb, double now)
        {
            float reference = ReferenceDb(now);
            if (!(reference > activeDb)) return false;
            return !(micDb > reference + m_Coupling + marginDb);
        }

        /// <summary>
        /// Called once per captured frame: learns the coupling while playback is active and the player is
        /// probably quiet, and the player's own talking level from frames sent while nothing plays.
        /// Returns <see cref="IsEcho"/> for this frame.
        /// </summary>
        public bool ProcessFrame(float micDb, bool gateVoiced, double now)
        {
            float reference = ReferenceDb(now);
            bool active = reference > activeDb;
            double dt = double.IsNaN(m_LastLearn) ? VoiceFormat.FrameSeconds : Math.Max(0.0, Math.Min(0.5, now - m_LastLearn));
            m_LastLearn = now;

            if (!active)
            {
                if (gateVoiced && micDb > -60f)
                {
                    TalkLevelDb += (micDb - TalkLevelDb) * 0.05f;
                    TalkLevelDb = Math.Max(-50f, Math.Min(-3f, TalkLevelDb));
                }
                return false;
            }

            bool echo = !(micDb > reference + m_Coupling + marginDb);
            if (micDb < TalkLevelDb - talkGuardDb && micDb > -90f)
            {
                float sample = micDb - reference;
                if (sample > m_Coupling) m_Coupling = Math.Min(sample, m_Coupling + couplingRiseDbPerFrame);
                else m_Coupling = Math.Max(sample, m_Coupling - couplingDecayDbPerSecond * (float)dt);
                m_Coupling = Math.Max(minCouplingDb, Math.Min(maxCouplingDb, m_Coupling));
            }
            if (echo) EchoFrames++;
            return echo;
        }
    }
}

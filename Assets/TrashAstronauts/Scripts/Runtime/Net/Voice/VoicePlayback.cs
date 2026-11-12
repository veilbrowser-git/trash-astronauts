using TrashAstronauts.Audio;
using TrashAstronauts.Core.Voice;
using UnityEngine;

namespace TrashAstronauts.Net.Voice
{
    /// <summary>
    /// One crew member's voice, coming out of their helmet. A child of the RemoteAvatar's head (so it moves with
    /// it and is destroyed with it) holding a fully 3D AudioSource with the voice rolloff (clear within 2.5 m,
    /// quiet by 10 m, silent from 18 m; see <see cref="VoiceRolloff"/>), the game's own <see cref="SoundOcclusion"/>
    /// (a wall between you muffles and quietens it) and, like every world sound, the helmet low-pass on the
    /// listener. Room reverb zones apply too.
    ///
    /// The main thread pulls 20 ms frames from the talker's jitter buffer as time passes (<see cref="Pump"/>),
    /// converts them from 16 kHz to the output rate and writes them into a lock-free ring; the audio thread reads
    /// the ring in <see cref="OnAudioFilterRead"/> without locking or allocating. The source plays a looping clip of
    /// constant 1.0 and the filter multiplies it by the voice, so Unity's own 3D panning and distance attenuation
    /// shape the voice whether they are applied before or after script filters.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class VoicePlayback : MonoBehaviour
    {
        /// <summary>Most audio waiting between the main thread and the audio thread before frames are dropped (s).</summary>
        public const float MaxRingSeconds = 0.12f;
        /// <summary>Silence put in front of a talk spurt so the audio thread never catches up with the main thread.</summary>
        public const float CushionSeconds = 0.03f;

        public AudioSource source;
        public SoundOcclusion occlusion;
        public AudioLowPassFilter lowPass;

        static AudioClip s_Carrier;
        static int s_CarrierRate;

        VoiceRing m_Ring;
        VoiceResampler m_Resampler;
        readonly float[] m_Frame = new float[VoiceFormat.FrameSamples];
        float[] m_Out;
        readonly float[] m_Scratch = new float[8192];
        int m_OutputRate;
        double m_LastPump = double.NaN;
        double m_Owed;
        bool m_WasSounding;

        /// <summary>Crew slot this voice belongs to (VoiceMsg.CreatureSlot for the co-op Mimic's).</summary>
        public int Slot { get; private set; }
        /// <summary>20 ms slots pulled from the stream (played, concealed or silent).</summary>
        public long SlotsPulled { get; private set; }
        public long FramesPlayed { get; private set; }
        public long FramesConcealed { get; private set; }
        public long FramesDropped { get; private set; }
        /// <summary>Output-rate samples handed to the audio thread so far.</summary>
        public long SamplesQueued { get; private set; }
        /// <summary>Level of the last frame pulled, before distance and walls (dBFS; -100 for silence).</summary>
        public float LastFrameDb { get; private set; } = -100f;
        /// <summary>Samples the audio thread has taken (0 where no audio runs, as in batch mode).</summary>
        public long SamplesConsumed => m_Ring != null ? m_Ring.TotalRead : 0;
        public int OutputRate => m_OutputRate;

        /// <summary>Makes the voice source for crew slot <paramref name="slot"/> on <paramref name="helmet"/>.</summary>
        public static VoicePlayback Create(Transform helmet, int slot)
        {
            var go = new GameObject("Crew Voice " + slot);
            go.transform.SetParent(helmet, false);
            go.transform.localPosition = new Vector3(0f, -0.06f, 0.06f); // the mouth, inside the helmet
            var source = go.AddComponent<AudioSource>();
            // Order matters: the voice must be written before the low-pass and the occlusion gain run.
            var playback = go.AddComponent<VoicePlayback>();
            var lowPass = go.AddComponent<AudioLowPassFilter>();
            lowPass.cutoffFrequency = 22000f;
            lowPass.lowpassResonanceQ = 1f;
            lowPass.enabled = false;
            var occlusion = go.AddComponent<SoundOcclusion>();
            occlusion.lowPass = lowPass;
            playback.source = source;
            playback.lowPass = lowPass;
            playback.occlusion = occlusion;
            playback.Slot = slot;
            ConfigureSource(source);
            playback.Begin();
            return playback;
        }

        /// <summary>
        /// Makes the co-op Mimic's voice source on the creature (<paramref name="mouth"/>, its speaker): set up like
        /// the creature's own speaker <paramref name="like"/> (its rolloff and distances; fully 3D), at
        /// <paramref name="volume"/> (the Mimic's playback volume, so it is never louder than in single-player),
        /// with the same wall occlusion as a crew voice. No talk light: this is not a crew member.
        /// </summary>
        public static VoicePlayback CreateCreature(Transform mouth, AudioSource like, float volume)
        {
            var go = new GameObject("Creature Voice");
            go.transform.SetParent(mouth, false);
            var source = go.AddComponent<AudioSource>();
            var playback = go.AddComponent<VoicePlayback>();
            var lowPass = go.AddComponent<AudioLowPassFilter>();
            lowPass.cutoffFrequency = 22000f;
            lowPass.lowpassResonanceQ = 1f;
            lowPass.enabled = false;
            var occlusion = go.AddComponent<SoundOcclusion>();
            occlusion.lowPass = lowPass;
            playback.source = source;
            playback.lowPass = lowPass;
            playback.occlusion = occlusion;
            playback.Slot = Core.Net.VoiceMsg.CreatureSlot;
            ConfigureSource(source);
            source.priority = 64;
            source.volume = Mathf.Clamp01(volume);
            if (like != null)
            {
                source.rolloffMode = like.rolloffMode;
                source.minDistance = like.minDistance;
                source.maxDistance = like.maxDistance;
                if (like.rolloffMode == AudioRolloffMode.Custom)
                    source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, like.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
            }
            playback.Begin();
            return playback;
        }

        /// <summary>The voice rolloff as an AudioSource custom curve (distance / maxDistance on x), sampled every 25 cm.</summary>
        public static AnimationCurve RolloffCurve()
        {
            const float step = 0.25f;
            int count = Mathf.RoundToInt(VoiceRolloff.MaxDistance / step) + 1;
            var keys = new Keyframe[count];
            for (int i = 0; i < count; i++)
            {
                float d = i * step;
                float before = VoiceRolloff.Gain(Mathf.Max(0f, d - step * 0.5f));
                float after = VoiceRolloff.Gain(Mathf.Min(VoiceRolloff.MaxDistance, d + step * 0.5f));
                float slope = (after - before) / (step / VoiceRolloff.MaxDistance);
                keys[i] = new Keyframe(d / VoiceRolloff.MaxDistance, VoiceRolloff.Gain(d), slope, slope);
            }
            return new AnimationCurve(keys);
        }

        static void ConfigureSource(AudioSource source)
        {
            source.playOnAwake = false;
            source.loop = true;
            source.volume = 1f;
            source.spatialBlend = 1f;
            source.dopplerLevel = 0f;
            source.rolloffMode = AudioRolloffMode.Custom;
            source.minDistance = 1f;
            source.maxDistance = VoiceRolloff.MaxDistance;
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, RolloffCurve());
            source.priority = 32; // voices matter more than most effects when channels run short
            source.clip = Carrier();
        }

        static AudioClip Carrier()
        {
            int rate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            if (s_Carrier != null && s_CarrierRate == rate) return s_Carrier;
            var ones = new float[2048];
            for (int i = 0; i < ones.Length; i++) ones[i] = 1f;
            s_Carrier = AudioClip.Create("Voice Carrier", ones.Length, 1, rate, false);
            s_Carrier.SetData(ones, 0);
            s_CarrierRate = rate;
            return s_Carrier;
        }

        void Begin()
        {
            m_OutputRate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            m_Resampler = new VoiceResampler(VoiceFormat.SampleRate, m_OutputRate, VoiceFormat.FrameSamples);
            m_Out = new float[m_Resampler.MaxOutput(VoiceFormat.FrameSamples) + 8];
            m_Ring = new VoiceRing(Mathf.CeilToInt(m_OutputRate * 0.5f));
            if (isActiveAndEnabled && source != null && !source.isPlaying) source.Play();
        }

        void OnEnable()
        {
            if (source != null && m_Ring != null && !source.isPlaying) source.Play();
        }

        // A disabled filter is skipped by Unity and the carrier would be heard as a click: stop the source with it.
        void OnDisable()
        {
            if (source != null) source.Stop();
        }

        /// <summary>
        /// Plays the talker's stream up to <paramref name="now"/> (seconds, any clock that advances in real time):
        /// one 20 ms frame per 20 ms elapsed. Returns the level (dBFS) of the loudest frame pulled, or -100.
        /// </summary>
        public float Pump(VoiceStream stream, double now)
        {
            if (stream == null || m_Ring == null) return -100f;
            if (double.IsNaN(m_LastPump)) m_LastPump = now;
            double elapsed = now - m_LastPump;
            m_LastPump = now;
            // A long hitch (loading, the headset taken off) does not make it race through a backlog afterwards.
            m_Owed += elapsed < 0 ? 0 : elapsed > 0.25 ? 0.25 : elapsed;
            float loudest = -100f;
            while (m_Owed >= VoiceFormat.FrameSeconds)
            {
                m_Owed -= VoiceFormat.FrameSeconds;
                VoicePull result = stream.Pull(m_Frame, 0, now);
                SlotsPulled++;
                if (result == VoicePull.Played) FramesPlayed++;
                else if (result == VoicePull.Concealed) FramesConcealed++;
                LastFrameDb = stream.LevelDb;
                if (LastFrameDb > loudest) loudest = LastFrameDb;
                bool sounding = result != VoicePull.Silent;
                if (sounding)
                {
                    if (!m_WasSounding && m_Ring.Count == 0) WriteSilence(Mathf.RoundToInt(CushionSeconds * m_OutputRate));
                    if (m_Ring.Count > MaxRingSeconds * m_OutputRate) FramesDropped++; // the audio thread is not keeping up (or not running)
                    else
                    {
                        int produced = m_Resampler.Process(m_Frame, 0, VoiceFormat.FrameSamples, m_Out, 0, m_Out.Length);
                        SamplesQueued += m_Ring.Write(m_Out, 0, produced);
                    }
                }
                m_WasSounding = sounding;
            }
            return loudest;
        }

        void WriteSilence(int samples)
        {
            System.Array.Clear(m_Out, 0, m_Out.Length);
            while (samples > 0)
            {
                int n = Mathf.Min(samples, m_Out.Length);
                m_Ring.Write(m_Out, 0, n);
                samples -= n;
            }
        }

        /// <summary>The fraction of the voice's level that reaches a listener at <paramref name="listener"/>: volume, distance and walls.</summary>
        public float AudibleGain(Vector3 listener)
        {
            if (source == null) return 0f;
            float distance = Vector3.Distance(listener, transform.position);
            float rolloff;
            if (source.rolloffMode == AudioRolloffMode.Linear)
                rolloff = distance <= source.minDistance ? 1f : Mathf.Clamp01((source.maxDistance - distance) / Mathf.Max(0.01f, source.maxDistance - source.minDistance));
            else if (source.rolloffMode == AudioRolloffMode.Logarithmic)
                rolloff = distance > source.maxDistance ? 0f : Mathf.Min(1f, source.minDistance / Mathf.Max(0.01f, distance));
            else rolloff = VoiceRolloff.Gain(distance);
            float gain = source.volume * rolloff;
            if (occlusion != null) gain *= occlusion.VolumeFactor;
            return gain;
        }

        // Audio thread: no allocation, no locks, no Unity API.
        void OnAudioFilterRead(float[] data, int channels)
        {
            VoiceRing ring = m_Ring;
            if (ring == null || channels <= 0)
            {
                System.Array.Clear(data, 0, data.Length);
                return;
            }
            int frames = data.Length / channels;
            if (frames > m_Scratch.Length) frames = m_Scratch.Length;
            int got = ring.Read(m_Scratch, 0, frames);
            int i = 0;
            for (int f = 0; f < frames; f++)
            {
                float v = f < got ? m_Scratch[f] : 0f;
                for (int c = 0; c < channels; c++, i++) data[i] *= v;
            }
            for (; i < data.Length; i++) data[i] = 0f;
        }
    }
}

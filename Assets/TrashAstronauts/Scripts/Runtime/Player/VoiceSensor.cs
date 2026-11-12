using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using UnityEngine;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// Lets the creature hear the player's real voice. Normally the headset microphone is read for LEVEL
    /// ONLY: each fraction of a second of audio is measured for loudness and discarded. Talking raises a
    /// noise at the player's head, exactly like dropping something would; whispering does not.
    ///
    /// The one exception is the Mimic. Only while a Mimic is aboard and asking for it, the last few
    /// spoken phrases (a few seconds each) are also kept in memory so it can play them back. They are
    /// forgotten when the contract ends. Nothing is ever written to disk and nothing leaves the headset
    /// (the app has no network permission at all). Can be switched off in the suit menu (MIC).
    /// </summary>
    public sealed class VoiceSensor : MonoBehaviour
    {
        public PlayerContext player;
        public Transform head;
        public int sampleRate = 16000;
        [Tooltip("Scales how loud speech is to the creature. 1 = a shout carries as far as a panel breaking.")]
        [Range(0f, 1.5f)] public float loudnessScale = 0.9f;

        /// <summary>Test seam: a microphone level (RMS, 0..1) to use instead of a real microphone.</summary>
        public float? LevelOverride { get; set; }

        public VoiceMeter Meter { get; } = new VoiceMeter();
        /// <summary>Spoken phrases held for the Mimic. Empty unless one is aboard.</summary>
        public PhraseRecorder Recorder { get; private set; }
        /// <summary>True while a microphone (or the test level) is being read.</summary>
        public bool Listening { get; private set; }
        public float Loudness => Listening ? Meter.Loudness : 0f;
        public int EmitCount { get; private set; }
        public float LastHeardTime { get; private set; } = -100f;
        /// <summary>True for a moment after the creature could have heard you.</summary>
        public bool HeardRecently => Time.unscaledTime - LastHeardTime < 1.2f;
        /// <summary>Why the microphone is not being used, for the wrist display. Empty when it is.</summary>
        public string Status { get; private set; } = "MIC OFF";
        /// <summary>True while phrases are being kept for a Mimic.</summary>
        public bool CaptureActive => Wanted && Time.unscaledTime - m_CaptureRequestedAt < 0.5f;
        /// <summary>True while the game is ignoring the microphone because the Mimic is talking.</summary>
        public bool IsSuppressed => Time.unscaledTime < m_SuppressUntil;

        AudioClip m_Clip;
        string m_Device;
        bool m_Started;
        bool m_AskedPermission;
        float m_NextAttempt;
        int m_LastPosition;
        float m_CaptureRequestedAt = -100f;
        float m_SuppressUntil;
        readonly float[] m_Chunk = new float[256];

        bool Wanted
        {
            get
            {
                var director = MissionDirector.Active;
                return director != null && director.Profile != null && director.Profile.Data.voiceDetection;
            }
        }

        void Awake() => Recorder = new PhraseRecorder(sampleRate);

        void OnDisable() => StopMicrophone();

        void OnApplicationPause(bool paused)
        {
            if (paused) StopMicrophone();
        }

        /// <summary>Called every frame by a Mimic that is aboard. Phrases are only kept while it keeps asking.</summary>
        public void RequestCapture() => m_CaptureRequestedAt = Time.unscaledTime;

        /// <summary>Ignore the microphone for a while (the Mimic is playing a voice out of the headset speakers).</summary>
        public void Suppress(float seconds)
        {
            m_SuppressUntil = Mathf.Max(m_SuppressUntil, Time.unscaledTime + Mathf.Max(0f, seconds));
            if (Recorder != null) Recorder.Abort();
        }

        /// <summary>Test seam: hands the recorder a phrase as if it had just been spoken.</summary>
        public void InjectPhrase(float[] samples)
        {
            if (Recorder == null) Recorder = new PhraseRecorder(sampleRate);
            Recorder.Add(samples);
        }

        void Update()
        {
            if (Recorder == null) Recorder = new PhraseRecorder(sampleRate);

            // Phrases exist only for a Mimic. Once nothing is asking for them, forget them.
            if (!CaptureActive && Recorder.Count > 0 && Time.unscaledTime - m_CaptureRequestedAt > 1f) Recorder.Clear();

            if (!Wanted)
            {
                if (m_Started) StopMicrophone();
                Recorder.Clear();
                Listening = false;
                Status = "MIC OFF";
                return;
            }

            if (LevelOverride.HasValue)
            {
                Listening = true;
                Status = "";
                if (!IsSuppressed) Hear(LevelOverride.Value, Time.unscaledDeltaTime);
                return;
            }

            if (!m_Started) TryStartMicrophone();
            if (!m_Started || m_Clip == null || !Microphone.IsRecording(m_Device))
            {
                // The system took the microphone away (headset slept, another app): start again shortly.
                if (m_Started) m_Started = false;
                Listening = false;
                return;
            }
            Listening = true;
            Status = "";

            // Work through whatever arrived since last frame, a small block at a time.
            int total = m_Clip.samples;
            if (total < m_Chunk.Length) return;
            int fresh = Microphone.GetPosition(m_Device) - m_LastPosition;
            if (fresh < 0) fresh += total;
            float blockSeconds = m_Chunk.Length / (float)sampleRate;
            int guard = 0;
            while (fresh >= m_Chunk.Length && guard++ < 64)
            {
                if (!m_Clip.GetData(m_Chunk, m_LastPosition)) break;
                m_LastPosition = (m_LastPosition + m_Chunk.Length) % total;
                fresh -= m_Chunk.Length;
                if (IsSuppressed) continue;

                double sum = 0.0;
                for (int i = 0; i < m_Chunk.Length; i++) sum += m_Chunk[i] * m_Chunk[i];
                Hear(Mathf.Sqrt((float)(sum / m_Chunk.Length)), blockSeconds);
                if (CaptureActive) Recorder.Feed(m_Chunk, m_Chunk.Length, Meter.Loudness >= Meter.minLoudness);
            }
        }

        void Hear(float rms, float seconds)
        {
            if (!Meter.Tick(rms, seconds, out float loudness)) return;
            var director = MissionDirector.Active;
            if (director == null || director.Phase != MissionPhase.Active || director.HazardsPaused || director.noise == null) return;
            Vector3 at = head != null ? head.position : transform.position;
            director.noise.Emit(new NoiseEvent(at, loudness * loudnessScale, NoiseKind.Voice, player != null ? player.Id : PlayerId.Local));
            EmitCount++;
            LastHeardTime = Time.unscaledTime;
        }

        void TryStartMicrophone()
        {
            // Never touch a real microphone in automated runs.
            if (Application.isBatchMode) { Status = "NO MIC"; return; }
            if (Time.unscaledTime < m_NextAttempt) return;
            m_NextAttempt = Time.unscaledTime + 2f;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                Status = "MIC: ALLOW IN HEADSET";
                if (!m_AskedPermission)
                {
                    m_AskedPermission = true;
                    UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
                }
                return;
            }
#endif
            var devices = Microphone.devices;
            if (devices == null || devices.Length == 0) { Status = "NO MIC"; return; }
            m_Device = devices[0];
            m_Clip = Microphone.Start(m_Device, true, 1, sampleRate);
            m_Started = m_Clip != null;
            m_LastPosition = 0;
            if (m_Started) Meter.Reset();
            else Status = "NO MIC";
        }

        void StopMicrophone()
        {
            if (m_Started && Microphone.IsRecording(m_Device)) Microphone.End(m_Device);
            if (m_Clip != null) Destroy(m_Clip);
            m_Clip = null;
            m_Started = false;
            Listening = false;
            if (Recorder != null) Recorder.Abort();
        }
    }
}

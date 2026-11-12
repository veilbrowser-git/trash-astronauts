using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using UnityEngine;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// Lets the creature hear the player's real voice. The headset microphone is read for LEVEL ONLY:
    /// a quarter of a second of audio is held in memory, measured for loudness and overwritten. Nothing
    /// is recorded to disk and nothing leaves the headset (the app has no network permission at all).
    /// Talking raises a noise at the player's head, exactly like dropping something would; whispering does not.
    /// Can be switched off in the suit menu (MIC).
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
        /// <summary>True while a microphone (or the test level) is being read.</summary>
        public bool Listening { get; private set; }
        public float Loudness => Listening ? Meter.Loudness : 0f;
        public int EmitCount { get; private set; }
        public float LastHeardTime { get; private set; } = -100f;
        /// <summary>True for a moment after the creature could have heard you.</summary>
        public bool HeardRecently => Time.unscaledTime - LastHeardTime < 1.2f;
        /// <summary>Why the microphone is not being used, for the wrist display. Empty when it is.</summary>
        public string Status { get; private set; } = "MIC OFF";

        AudioClip m_Clip;
        string m_Device;
        bool m_Started;
        bool m_AskedPermission;
        float m_NextAttempt;
        readonly float[] m_Buffer = new float[512];

        bool Wanted
        {
            get
            {
                var director = MissionDirector.Active;
                return director != null && director.Profile != null && director.Profile.Data.voiceDetection;
            }
        }

        void OnDisable() => StopMicrophone();

        void OnApplicationPause(bool paused)
        {
            if (paused) StopMicrophone();
        }

        void Update()
        {
            if (!Wanted)
            {
                if (m_Started) StopMicrophone();
                Listening = false;
                Status = "MIC OFF";
                return;
            }

            float rms;
            if (LevelOverride.HasValue)
            {
                rms = LevelOverride.Value;
            }
            else
            {
                if (!m_Started) TryStartMicrophone();
                if (!m_Started || !ReadMicrophone(out rms))
                {
                    Listening = false;
                    return;
                }
            }

            Listening = true;
            Status = "";
            if (!Meter.Tick(rms, Time.unscaledDeltaTime, out float loudness)) return;

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
        }

        bool ReadMicrophone(out float rms)
        {
            rms = 0f;
            if (m_Clip == null || !Microphone.IsRecording(m_Device))
            {
                // The system took the microphone away (headset slept, another app): start again shortly.
                m_Started = false;
                return false;
            }
            int position = Microphone.GetPosition(m_Device);
            int total = m_Clip.samples;
            if (total < m_Buffer.Length) return false;
            int start = position - m_Buffer.Length;
            if (start < 0) start += total;
            if (!m_Clip.GetData(m_Buffer, start)) return false;

            double sum = 0.0;
            for (int i = 0; i < m_Buffer.Length; i++) sum += m_Buffer[i] * m_Buffer[i];
            rms = Mathf.Sqrt((float)(sum / m_Buffer.Length));
            return true;
        }
    }
}

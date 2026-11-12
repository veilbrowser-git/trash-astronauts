using TrashAstronauts.Core;
using TrashAstronauts.Player;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// The Mimic's trick: it remembers the last few things the player said and says them back, lower and
    /// slightly wrong, from wherever it happens to be. A moment after you speak it answers with your own
    /// words, and while it wanders it repeats older ones. It needs the microphone to be on; with it off
    /// (or permission refused) the Mimic is simply silent.
    /// </summary>
    public sealed class VoiceMimic : MonoBehaviour
    {
        public EnemyDrone drone;
        public AudioSource speaker;
        [Tooltip("How long after hearing a new phrase it answers with it (seconds).")]
        public Vector2 echoDelay = new Vector2(1.2f, 2.8f);
        [Tooltip("How often it repeats an older phrase while nothing new has been said (seconds).")]
        public Vector2 idleRepeat = new Vector2(11f, 22f);
        [Tooltip("Playback speed. Below 1 makes the voice lower and slower.")]
        [Range(0.5f, 1.2f)] public float pitch = 0.86f;
        [Range(0f, 1f)] public float volume = 0.85f;
        [Tooltip("Depth of the wobble added to the voice (0 = a clean copy).")]
        [Range(0f, 1f)] public float warble = 0.35f;

        public int PlaybackCount { get; private set; }
        public bool IsSpeaking => speaker != null && speaker.isPlaying;
        /// <summary>Length of the last thing it said, in seconds of real time.</summary>
        public float LastPlaybackSeconds { get; private set; }

        int m_SeenVersion;
        bool m_AnswerNewest;
        float m_NextTime = -1f;
        AudioClip m_Clip;
        System.Random m_Random = new System.Random(977);

        VoiceSensor Sensor
        {
            get
            {
                var director = drone != null && drone.director != null ? drone.director : MissionDirector.Active;
                if (director == null || director.Players.Count == 0 || director.Players[0] == null) return null;
                return director.Players[0].voice;
            }
        }

        void OnDisable()
        {
            if (speaker != null) speaker.Stop();
            m_NextTime = -1f;
        }

        void OnDestroy()
        {
            if (m_Clip != null) Destroy(m_Clip);
        }

        float Range(Vector2 range) => Mathf.Lerp(range.x, range.y, (float)m_Random.NextDouble());

        void Update()
        {
            var sensor = Sensor;
            if (sensor == null || drone == null) return;
            bool aboard = drone.IsRunning;
            if (aboard) sensor.RequestCapture();

            bool quiet = !aboard || drone.State == EnemyState.Dormant || drone.IsSedated || drone.Silenced || drone.IsScaring;
            if (quiet)
            {
                if (speaker != null && speaker.isPlaying && (drone.Silenced || drone.IsSedated || !aboard)) speaker.Stop();
                return;
            }

            var recorder = sensor.Recorder;
            if (recorder == null || recorder.Count == 0) return;

            if (recorder.Version != m_SeenVersion)
            {
                // Something new was said: answer with it shortly.
                m_SeenVersion = recorder.Version;
                m_AnswerNewest = true;
                m_NextTime = Time.time + Range(echoDelay);
            }
            else if (m_NextTime < 0f) m_NextTime = Time.time + Range(idleRepeat);

            if (Time.time < m_NextTime || IsSpeaking) return;
            float[] phrase = m_AnswerNewest ? recorder.Newest : recorder.Get(m_Random.Next(recorder.Count));
            m_AnswerNewest = false;
            Say(phrase, recorder.sampleRate, sensor);
            m_NextTime = Time.time + Range(idleRepeat);
        }

        /// <summary>Plays a phrase from the creature's position. Returns false if it could not.</summary>
        public bool Say(float[] phrase, int sampleRate, VoiceSensor sensor = null)
        {
            if (speaker == null || phrase == null || phrase.Length < 16) return false;

            // A copy with a slow wobble, so it is recognisably the player's voice but not quite right.
            var warped = new float[phrase.Length];
            for (int i = 0; i < phrase.Length; i++)
            {
                float t = i / (float)sampleRate;
                float wobble = 1f - warble * (0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * 37f * t)) * (0.6f + 0.4f * Mathf.Sin(2f * Mathf.PI * 3.1f * t));
                warped[i] = Mathf.Clamp(phrase[i] * wobble, -1f, 1f);
            }

            if (m_Clip != null) Destroy(m_Clip);
            m_Clip = AudioClip.Create("Mimic Phrase", warped.Length, 1, sampleRate, false);
            m_Clip.SetData(warped, 0);
            speaker.Stop();
            speaker.clip = m_Clip;
            speaker.loop = false;
            speaker.pitch = pitch;
            speaker.volume = volume;
            speaker.Play();

            LastPlaybackSeconds = phrase.Length / (float)sampleRate / Mathf.Max(0.1f, pitch);
            PlaybackCount++;
            // The headset microphone will pick this up from the speakers: it must not count as the player talking.
            if (sensor == null) sensor = Sensor;
            if (sensor != null) sensor.Suppress(LastPlaybackSeconds + 0.4f);
            return true;
        }
    }
}

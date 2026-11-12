using TrashAstronauts.Core;
using TrashAstronauts.Core.Voice;
using TrashAstronauts.Player;
using UnityEngine;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// The Mimic's trick: it remembers the last few things the player said and says them back, lower and
    /// slightly wrong, from wherever it happens to be. A moment after you speak it answers with your own
    /// words, and while it wanders it repeats older ones. It needs the microphone to be on; with it off
    /// (or permission refused) the Mimic is simply silent.
    ///
    /// In co-op the host's creature does the same with the whole crew's voices (see <see cref="ICrewVoice"/>,
    /// implemented by Net/Voice/MimicCrewVoice.cs): phrases come from the host's microphone and from the crew's voice
    /// chat, it prefers someone who is not near the player it is stalking, and what it says is played from the
    /// creature on every headset. A client's copy of the creature (a replica) never listens or speaks by itself.
    /// </summary>
    public sealed class VoiceMimic : MonoBehaviour
    {
        /// <summary>
        /// The co-op host's crew voice: where the phrases come from and how the creature's voice reaches every
        /// headset. Set while a host session is open; null offline and on a client.
        /// </summary>
        public interface ICrewVoice
        {
            /// <summary>Called every frame by a Mimic that is aboard. Crew phrases are kept only while it keeps asking.</summary>
            void RequestCapture();
            /// <summary>Goes up when anybody's new phrase is finished.</summary>
            int Version { get; }
            /// <summary>Phrases held, the whole crew together.</summary>
            int Count { get; }
            /// <summary>True while the creature's voice is still being played out.</summary>
            bool IsSpeaking { get; }
            /// <summary>Chooses a phrase, preferring someone who is not near <paramref name="stalked"/>.</summary>
            bool TryPick(bool newest, PlayerId stalked, System.Random random, out float[] phrase, out int sampleRate, out int speaker);
            /// <summary>Says <paramref name="warped"/> (the wobbled phrase) from <paramref name="mimic"/>, lowered by its pitch, on every headset.</summary>
            void Speak(VoiceMimic mimic, float[] warped, int sampleRate, int speaker);
            /// <summary>Stops the creature's voice at once.</summary>
            void Stop();
        }

        /// <summary>The co-op seam (see <see cref="ICrewVoice"/>).</summary>
        public static ICrewVoice Crew { get; set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Crew = null;

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
        public bool IsSpeaking => (speaker != null && speaker.isPlaying) || (m_CrewSpeaking && Crew != null && Crew.IsSpeaking);
        /// <summary>Co-op: whose voice it used last (crew slot), or -1.</summary>
        public int LastSpeaker { get; private set; } = -1;
        /// <summary>Length of the last thing it said, in seconds of real time.</summary>
        public float LastPlaybackSeconds { get; private set; }

        int m_SeenVersion;
        bool m_AnswerNewest;
        float m_NextTime = -1f;
        AudioClip m_Clip;
        System.Random m_Random = new System.Random(977);
        bool m_CrewSpeaking;

        VoiceSensor Sensor
        {
            get
            {
                var director = drone != null && drone.director != null ? drone.director : MissionDirector.Active;
                // It only ever hears, and repeats, the player wearing this headset.
                var local = director != null ? director.LocalPlayer : null;
                return local != null ? local.voice : null;
            }
        }

        void OnDisable()
        {
            StopSpeaking();
            m_NextTime = -1f;
        }

        void StopSpeaking()
        {
            if (speaker != null) speaker.Stop();
            if (m_CrewSpeaking && Crew != null) Crew.Stop();
            m_CrewSpeaking = false;
        }

        void OnDestroy()
        {
            if (m_Clip != null) Destroy(m_Clip);
        }

        float Range(Vector2 range) => Mathf.Lerp(range.x, range.y, (float)m_Random.NextDouble());

        void Update()
        {
            if (drone == null) return;
            if (drone.IsReplica)
            {
                // A client's copy of the host's creature: it neither listens nor talks here. Its voice, when the
                // host's creature speaks, arrives with the crew's voice chat and plays from this body.
                if (speaker != null && speaker.isPlaying) speaker.Stop();
                return;
            }
            if (Crew != null)
            {
                CrewUpdate(Crew);
                return;
            }
            if (m_CrewSpeaking) m_CrewSpeaking = false;
            var sensor = Sensor;
            if (sensor == null) return;
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

        /// <summary>
        /// The co-op host: the same triggers and pacing as single-player (it answers a new phrase after a moment and
        /// repeats older ones while nothing new is said; silent while dormant, darted, in a blackout or a jumpscare),
        /// with the whole crew's phrases, preferring someone who is not near the player it is stalking.
        /// </summary>
        void CrewUpdate(ICrewVoice crew)
        {
            var sensor = Sensor;
            bool aboard = drone.IsRunning;
            if (aboard)
            {
                if (sensor != null) sensor.RequestCapture();
                crew.RequestCapture();
            }
            if (m_CrewSpeaking && !crew.IsSpeaking) m_CrewSpeaking = false;

            bool quiet = !aboard || drone.State == EnemyState.Dormant || drone.IsSedated || drone.Silenced || drone.IsScaring;
            if (quiet)
            {
                if (IsSpeaking && (drone.Silenced || drone.IsSedated || !aboard)) StopSpeaking();
                return;
            }
            if (crew.Count == 0) return;

            if (crew.Version != m_SeenVersion)
            {
                m_SeenVersion = crew.Version;
                m_AnswerNewest = true;
                m_NextTime = Time.time + Range(echoDelay);
            }
            else if (m_NextTime < 0f) m_NextTime = Time.time + Range(idleRepeat);

            if (Time.time < m_NextTime || IsSpeaking) return;
            bool newest = m_AnswerNewest;
            m_AnswerNewest = false;
            if (crew.TryPick(newest, drone.Quarry, m_Random, out float[] phrase, out int rate, out int who)) SayToCrew(crew, phrase, rate, who, sensor);
            m_NextTime = Time.time + Range(idleRepeat);
        }

        /// <summary>Co-op host: says a crew member's phrase from the creature, on every headset. False if it could not.</summary>
        public bool SayToCrew(ICrewVoice crew, float[] phrase, int sampleRate, int who, VoiceSensor sensor = null)
        {
            if (crew == null || phrase == null || phrase.Length < 16) return false;
            if (speaker != null) speaker.Stop();
            float[] warped = MimicVoiceRender.Warble(phrase, sampleRate, warble);
            crew.Speak(this, warped, sampleRate, who);
            m_CrewSpeaking = true;
            LastSpeaker = who;
            LastPlaybackSeconds = phrase.Length / (float)sampleRate / Mathf.Max(0.1f, pitch);
            PlaybackCount++;
            // This headset's microphone will hear it from the speakers: not the player talking (other headsets do the same).
            if (sensor == null) sensor = Sensor;
            if (sensor != null) sensor.Suppress(LastPlaybackSeconds + 0.4f);
            return true;
        }

        /// <summary>Plays a phrase from the creature's position. Returns false if it could not.</summary>
        public bool Say(float[] phrase, int sampleRate, VoiceSensor sensor = null)
        {
            if (speaker == null || phrase == null || phrase.Length < 16) return false;

            // A copy with a slow wobble, so it is recognisably the player's voice but not quite right.
            var warped = MimicVoiceRender.Warble(phrase, sampleRate, warble);

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

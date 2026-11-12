using System;
using TrashAstronauts.Core.Net;

namespace TrashAstronauts.Core.Voice
{
    /// <summary>
    /// The co-op Mimic's memory, on the host: the last few short phrases of each crew member, by slot. A client's
    /// phrases are cut from the voice-chat frames the host already receives to relay them (decoded here through
    /// that member's own jitter buffer, in real time, into a <see cref="PhraseRecorder"/>); the host player's own
    /// come from its microphone's recorder, which is attached as it is (the VoiceSensor keeps that one, under the
    /// single-player rules). Everything is in memory: <see cref="Forget"/> and <see cref="Clear"/> drop it, and
    /// nothing here is ever written or sent. Pure logic, no Unity objects.
    /// </summary>
    public sealed class CrewPhraseBank
    {
        public const int Slots = NetProtocol.MaxPlayers;
        /// <summary>Phrases kept per crew member (the oldest goes first).</summary>
        public const int PhrasesPerSpeaker = 4;
        /// <summary>Longest phrase kept (s).</summary>
        public const float MaxPhraseSeconds = 4f;
        /// <summary>A frame at least this loud (dBFS) counts as speech when cutting phrases.</summary>
        public const float SpeakingDb = -50f;

        sealed class Speaker
        {
            public PhraseRecorder recorder;
            public bool external;
            public VoiceStream stream;
            public double lastPump = double.NaN;
            public double owed;
            public int seenVersion;
            public long newestStamp;
        }

        readonly Speaker[] m_Speakers = new Speaker[Slots];
        readonly float[] m_Frame = new float[VoiceFormat.FrameSamples];
        long m_Stamp;

        /// <summary>Goes up by one for every new phrase of anybody.</summary>
        public int Version { get; private set; }
        /// <summary>Who said the newest phrase (-1: nobody yet).</summary>
        public int NewestSlot { get; private set; } = -1;
        /// <summary>Frames taken in for recording since creation.</summary>
        public long FramesTaken { get; private set; }

        /// <summary>Every phrase held, everyone together.</summary>
        public int Count
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Slots; i++) n += CountOf(i);
                return n;
            }
        }

        public int CountOf(int slot)
        {
            var s = At(slot);
            return s != null && s.recorder != null ? s.recorder.Count : 0;
        }

        /// <summary>The recorder of crew member <paramref name="slot"/>, or null.</summary>
        public PhraseRecorder Recorder(int slot) => At(slot)?.recorder;

        Speaker At(int slot) => slot >= 0 && slot < Slots ? m_Speakers[slot] : null;

        /// <summary>
        /// Uses <paramref name="recorder"/> (kept by someone else, such as the host's own VoiceSensor) as the phrases
        /// of <paramref name="slot"/>. The bank never clears an attached recorder; null detaches it.
        /// </summary>
        public void Attach(int slot, PhraseRecorder recorder)
        {
            if (slot < 0 || slot >= Slots) return;
            var s = m_Speakers[slot];
            if (recorder == null)
            {
                if (s != null && s.external) m_Speakers[slot] = null;
                return;
            }
            if (s != null && s.external && ReferenceEquals(s.recorder, recorder)) return;
            m_Speakers[slot] = new Speaker { recorder = recorder, external = true, seenVersion = recorder.Version };
        }

        /// <summary>
        /// One received voice frame of crew member <paramref name="slot"/>, to be cut into phrases. False (and nothing
        /// kept) for an attached slot or a bad frame.
        /// </summary>
        public bool Push(int slot, ushort sequence, byte[] buffer, int offset, int length, double now)
        {
            if (slot < 0 || slot >= Slots) return false;
            var s = m_Speakers[slot];
            if (s != null && s.external) return false;
            if (s == null)
            {
                s = m_Speakers[slot] = new Speaker
                {
                    recorder = new PhraseRecorder(VoiceFormat.SampleRate) { maxPhrases = PhrasesPerSpeaker, maxSeconds = MaxPhraseSeconds },
                    stream = new VoiceStream(),
                };
            }
            if (!s.stream.Push(sequence, buffer, offset, length, now)) return false;
            FramesTaken++;
            return true;
        }

        /// <summary>
        /// Plays every member's received voice into their recorder up to <paramref name="now"/> (one 20 ms frame per
        /// 20 ms, silence included, so a phrase ends when they stop talking) and notes new phrases, attached
        /// recorders included.
        /// </summary>
        public void Pump(double now)
        {
            for (int slot = 0; slot < Slots; slot++)
            {
                var s = m_Speakers[slot];
                if (s == null) continue;
                if (!s.external && s.stream != null)
                {
                    if (double.IsNaN(s.lastPump)) s.lastPump = now;
                    double elapsed = now - s.lastPump;
                    s.lastPump = now;
                    s.owed += elapsed < 0 ? 0 : elapsed > 0.25 ? 0.25 : elapsed;
                    while (s.owed >= VoiceFormat.FrameSeconds)
                    {
                        s.owed -= VoiceFormat.FrameSeconds;
                        VoicePull result = s.stream.Pull(m_Frame, 0, now);
                        if (result == VoicePull.Silent) Array.Clear(m_Frame, 0, m_Frame.Length);
                        bool speaking = result == VoicePull.Played && s.stream.LevelDb >= SpeakingDb;
                        s.recorder.Feed(m_Frame, m_Frame.Length, speaking);
                    }
                }
                if (s.recorder.Version != s.seenVersion)
                {
                    s.seenVersion = s.recorder.Version;
                    if (s.recorder.Count > 0)
                    {
                        Version++;
                        NewestSlot = slot;
                        s.newestStamp = ++m_Stamp;
                    }
                }
            }
        }

        /// <summary>Forgets everything of crew member <paramref name="slot"/> (an attached recorder is only let go).</summary>
        public void Forget(int slot)
        {
            var s = At(slot);
            if (s == null) return;
            if (!s.external)
            {
                s.recorder.Clear();
                s.stream.Clear();
            }
            m_Speakers[slot] = null;
            if (NewestSlot == slot) NewestSlot = -1;
        }

        /// <summary>Forgets everyone.</summary>
        public void Clear()
        {
            for (int slot = 0; slot < Slots; slot++) Forget(slot);
            NewestSlot = -1;
        }

        /// <summary>
        /// Chooses a phrase. Speakers for whom <paramref name="preferred"/> says true are used when any of them has
        /// a phrase; otherwise anybody's. <paramref name="newest"/>: that speaker's newest phrase, from whoever spoke
        /// last among them; otherwise a random phrase of theirs. False when nothing is held.
        /// </summary>
        public bool TryPick(bool newest, Predicate<int> preferred, Random random, out float[] phrase, out int sampleRate, out int slot)
        {
            phrase = null;
            sampleRate = VoiceFormat.SampleRate;
            slot = -1;
            bool anyPreferred = false;
            for (int i = 0; i < Slots; i++)
            {
                if (CountOf(i) > 0 && (preferred == null || preferred(i))) { anyPreferred = true; break; }
            }
            bool Use(int i) => CountOf(i) > 0 && (!anyPreferred || preferred == null || preferred(i));

            if (newest)
            {
                long best = long.MinValue;
                for (int i = 0; i < Slots; i++)
                {
                    if (!Use(i)) continue;
                    long stamp = m_Speakers[i].newestStamp;
                    if (stamp > best) { best = stamp; slot = i; }
                }
                if (slot < 0) return false;
                phrase = m_Speakers[slot].recorder.Newest;
            }
            else
            {
                int total = 0;
                for (int i = 0; i < Slots; i++) if (Use(i)) total += CountOf(i);
                if (total == 0) return false;
                int pick = random != null ? random.Next(total) : 0;
                for (int i = 0; i < Slots && phrase == null; i++)
                {
                    if (!Use(i)) continue;
                    int n = CountOf(i);
                    if (pick < n)
                    {
                        slot = i;
                        phrase = m_Speakers[i].recorder.Get(pick);
                    }
                    else pick -= n;
                }
            }
            if (phrase == null) return false;
            sampleRate = m_Speakers[slot].recorder.sampleRate;
            return true;
        }
    }
}

using System;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Core.Voice;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TrashAstronauts.Net.Voice
{
    /// <summary>
    /// The Mimic in a co-op crew. Owned by <see cref="VoiceChat"/> (made when it binds to a session, disposed with
    /// the binding, so LEAVE, a lost link or a closed session forget everything at once).
    ///
    /// On the HOST it is the creature's <see cref="VoiceMimic.ICrewVoice"/>:
    /// - Phrases. Only while a Mimic is aboard and asking for them (VoiceMimic calls <see cref="RequestCapture"/>
    ///   every frame), the voice-chat frames the host already receives to relay are also decoded here into a
    ///   <see cref="CrewPhraseBank"/>: at most <see cref="CrewPhraseBank.PhrasesPerSpeaker"/> phrases of at most
    ///   <see cref="CrewPhraseBank.MaxPhraseSeconds"/> s per crew member, in memory. The host player's own phrases are
    ///   its microphone's recorder (VoiceSensor, single-player rules: MIC on, and only while a Mimic asks). A crew
    ///   member's phrases are forgotten when no Mimic asks any more (contract over), when they leave, and when they
    ///   switch MIC or VOICE CHAT off (their headset sends a "voice off" notice, VoiceMsg.OffNotice). A client with
    ///   MIC or VOICE CHAT off sends no voice, so nothing of theirs is ever kept.
    /// - Choice. A phrase of someone who is not the player the creature is stalking and is more than
    ///   <see cref="NearMeters"/> from them is preferred; anybody's otherwise.
    /// - Playback. The phrase, wobbled, lowered and slowed exactly as in single-player, is encoded and streamed in
    ///   real time to every client as the creature's voice (Voice slot 7), and played here the same way: decoded
    ///   from the same frames, through a <see cref="VoicePlayback"/> on the creature's speaker (3D, its rolloff, wall
    ///   occlusion, the Mimic's own volume).
    ///
    /// On a CLIENT it only plays: creature-voice frames go through a jitter buffer to a voice source on the creature
    /// replica's speaker. Never a talk light (the creature is not a crew member).
    ///
    /// Both sides: while the creature's voice is audible here, this headset's microphone is suppressed
    /// (VoiceSensor.Suppress), so it is neither sent as this player's voice nor heard by the creature as their noise,
    /// and its level is reported to the echo suppressor. Silent in a blackout and while the creature is darted.
    /// A headset with VOICE CHAT off does not play the creature's voice of another crew member.
    /// </summary>
    public sealed class MimicCrewVoice : VoiceMimic.ICrewVoice, IDisposable
    {
        /// <summary>A crew member within this distance of the player being stalked is "near" them (m).</summary>
        public const float NearMeters = 8f;
        /// <summary>Crew phrases are kept only while a Mimic asked within this long (s).</summary>
        public const float CaptureHoldSeconds = 0.5f;
        /// <summary>The microphone stays suppressed this long after the creature's voice was last audible (s).</summary>
        public const float SuppressHoldSeconds = 0.4f;

        readonly VoiceChat m_Chat;
        readonly NetSessionHost m_Host;
        readonly CrewPhraseBank m_Bank = new CrewPhraseBank();
        readonly CreatureVoiceSender m_Sender = new CreatureVoiceSender(Environment.TickCount & 0x3FFF);
        readonly VoiceStream m_Stream = new VoiceStream();
        readonly VoicePacketSink m_Sink;
        VoicePlayback m_Playback;
        Transform m_Mouth;
        EnemyDrone m_CachedEnemy;
        VoiceMimic m_CachedMimic;
        float m_CaptureRequestedAt = -100f;
        bool m_LoopBack;
        int m_SpeakingSlot = -1;
        bool m_Disposed;

        public MimicCrewVoice(VoiceChat chat, IVoiceLink link, bool online)
        {
            m_Chat = chat;
            m_Host = link is HostVoiceLink host ? host.Session : null;
            m_Sink = SendFrames;
            if (online && m_Host == null)
            {
                m_Stream.Jitter.minDelayFrames = VoiceChat.OnlineMinDelayFrames;
                m_Stream.Jitter.maxDelayFrames = VoiceChat.OnlineMaxDelayFrames;
            }
            if (m_Host != null) VoiceMimic.Crew = this;
        }

        // ------------------------------------------------------------------ state, for tests and the debug line

        public bool IsHost => m_Host != null;
        /// <summary>The crew's phrases (host).</summary>
        public CrewPhraseBank Bank => m_Bank;
        /// <summary>The creature's voice source on this headset, or null while it has nothing to say.</summary>
        public VoicePlayback Playback => m_Playback;
        /// <summary>The creature's voice as received (client) or looped back (host).</summary>
        public VoiceStream Stream => m_Stream;
        public CreatureVoiceSender Sender => m_Sender;
        /// <summary>True while crew phrases are being kept for a Mimic (host).</summary>
        public bool CaptureActive => m_Host != null && !m_Disposed && Time.unscaledTime - m_CaptureRequestedAt < CaptureHoldSeconds;
        /// <summary>Whose phrase the creature said last (crew slot), or -1.</summary>
        public int LastSpeaker { get; private set; } = -1;
        public long CreatureFramesReceived { get; private set; }
        public long CreatureFramesIgnored { get; private set; }
        /// <summary>
        /// Client: a Mimic is aboard and this player's voice chat is on (MIC and VOICE CHAT), so the host may be keeping
        /// short phrases of what they say. For the wrist's REC.
        /// </summary>
        public bool HostMayKeepMyVoice
        {
            get
            {
                if (m_Host != null || m_Disposed || m_Chat == null || !m_Chat.IsLive) return false;
                var director = Director;
                if (director == null || (director.Profile != null && !director.Profile.Data.voiceDetection)) return false;
                return FindMimic(director) != null;
            }
        }
        /// <summary>"Voice off" notices heard (host).</summary>
        public int OffNoticesHeard { get; private set; }

        // ------------------------------------------------------------------ VoiceMimic.ICrewVoice (host)

        public void RequestCapture() => m_CaptureRequestedAt = Time.unscaledTime;
        public int Version => m_Bank.Version;
        public int Count => CaptureActive ? m_Bank.Count : 0;
        public bool IsSpeaking => m_Sender.Active || (m_LoopBack && m_Playback != null && (m_Stream.Jitter.Count > 0 || m_Stream.Jitter.IsPlaying));

        public bool TryPick(bool newest, PlayerId stalked, System.Random random, out float[] phrase, out int sampleRate, out int speaker)
        {
            var director = Director;
            PlayerContext quarry = director != null && stalked != PlayerId.None ? director.FindPlayer(stalked) : null;
            Predicate<int> preferred = null;
            if (quarry != null)
            {
                Vector3 at = quarry.HeadPosition;
                preferred = slot =>
                {
                    if (slot == stalked.Value) return false;
                    var other = director.FindPlayer(new PlayerId(slot));
                    return other != null && Vector3.Distance(other.HeadPosition, at) > NearMeters;
                };
            }
            return m_Bank.TryPick(newest, preferred, random, out phrase, out sampleRate, out speaker);
        }

        public void Speak(VoiceMimic mimic, float[] warped, int sampleRate, int speaker)
        {
            if (m_Host == null || mimic == null || warped == null) return;
            float[] voice = MimicVoiceRender.AtPitch(warped, sampleRate, mimic.pitch);
            m_Sender.Stop();
            m_Stream.Clear();
            // Played here as on every headset; the host player with VOICE CHAT off hears only their own voice from it.
            m_LoopBack = speaker == NetProtocol.HostSlot || m_Chat == null || m_Chat.SettingOn;
            if (m_LoopBack) EnsurePlayback(mimic);
            else DropPlayback();
            m_SpeakingSlot = speaker;
            LastSpeaker = speaker;
            double now = NetSession.Now;
            m_Sender.Begin(voice, now);
            m_Sender.Pump(now, m_Sink);
        }

        public void Stop()
        {
            m_Sender.Stop();
            m_Stream.Clear();
            DropPlayback();
            m_SpeakingSlot = -1;
        }

        bool SendFrames(ushort firstSequence, byte[] frames, int offset, int frameBytes, int frameCount)
        {
            if (m_Host == null || m_Host.IsClosed) return false;
            m_Host.SendCreatureVoice(firstSequence, frames, offset, frameBytes, frameCount);
            if (m_LoopBack)
            {
                double now = NetSession.Now;
                for (int i = 0; i < frameCount; i++) m_Stream.Push((ushort)(firstSequence + i), frames, offset + i * frameBytes, frameBytes, now);
            }
            return true;
        }

        // ------------------------------------------------------------------ frames from VoiceChat

        /// <summary>A crew member's voice frame (host): kept for the Mimic only while one is asking.</summary>
        public void OnCrewFrame(byte slot, ushort sequence, byte[] buffer, int offset, int length)
        {
            if (m_Host == null || !CaptureActive || slot == NetProtocol.HostSlot || slot >= NetProtocol.MaxPlayers) return;
            m_Bank.Push(slot, sequence, buffer, offset, length, NetSession.Now);
        }

        /// <summary>Crew member <paramref name="slot"/> switched MIC or VOICE CHAT off: nothing of their voice is kept.</summary>
        public void OnVoiceOff(byte slot)
        {
            if (m_Host == null || slot >= NetProtocol.MaxPlayers) return;
            OffNoticesHeard++;
            Forget(slot);
        }

        /// <summary>A frame of the creature's voice from the host (client).</summary>
        public void OnCreatureFrame(ushort sequence, byte[] buffer, int offset, int length)
        {
            if (m_Host != null || FindMimic(Director) == null)
            {
                CreatureFramesIgnored++;
                return;
            }
            if (m_Stream.Push(sequence, buffer, offset, length, NetSession.Now)) CreatureFramesReceived++;
            else CreatureFramesIgnored++;
        }

        void Forget(int slot)
        {
            m_Bank.Forget(slot);
            if (slot == m_SpeakingSlot) Stop(); // not even the rest of a sentence of someone who has gone or opted out
        }

        // ------------------------------------------------------------------ per frame

        /// <summary>
        /// Runs the creature's voice on this headset (VoiceChat.Update). Returns its level at the listener (dBFS) for
        /// the echo suppressor, or -100 when nothing of it is audible.
        /// </summary>
        public float Tick(double now, Vector3 listener)
        {
            if (m_Disposed) return -100f;
            var director = Director;
            VoiceMimic mimic = FindMimic(director);

            if (m_Host != null)
            {
                if (!CaptureActive || mimic == null) m_Bank.Clear();
                else
                {
                    var local = director.LocalPlayer;
                    m_Bank.Attach(NetProtocol.HostSlot, local != null && local.voice != null ? local.voice.Recorder : null);
                    for (int slot = 1; slot < NetProtocol.MaxPlayers; slot++)
                    {
                        if (!m_Host.IsConnected(slot) && (m_Bank.CountOf(slot) > 0 || m_Bank.Recorder(slot) != null)) Forget(slot);
                    }
                    m_Bank.Pump(now);
                }
                if (m_Sender.Active) m_Sender.Pump(now, m_Sink);
            }

            if (mimic == null || mimic.drone == null || mimic.drone.Silenced || mimic.drone.IsSedated)
            {
                // Gone, in a blackout, or asleep: quiet at once.
                if (m_Host != null && (m_Sender.Active || m_Playback != null)) Stop();
                else if (m_Host == null)
                {
                    m_Stream.Clear();
                    DropPlayback();
                }
                return -100f;
            }

            if (m_Playback == null)
            {
                if (m_Host != null || m_Stream.Jitter.Count == 0) return -100f;
                EnsurePlayback(mimic);
            }
            else if (m_Mouth == null || m_Mouth != MouthOf(mimic))
            {
                DropPlayback();
                if (m_Host == null) EnsurePlayback(mimic);
                else return -100f;
            }

            float frameDb = m_Playback.Pump(m_Stream, now);
            if (frameDb <= -90f) return -100f;
            float gain = m_Playback.AudibleGain(listener);
            if (gain <= 1e-4f) return -100f;
            // The speakers are playing the creature: this headset's microphone is not the player now.
            var sensor = director != null && director.LocalPlayer != null ? director.LocalPlayer.voice : null;
            if (sensor != null) sensor.Suppress(SuppressHoldSeconds);
            return frameDb + 20f * Mathf.Log10(gain);
        }

        MissionDirector Director => m_Chat != null && m_Chat.director != null ? m_Chat.director : MissionDirector.Active;

        /// <summary>The Mimic of the contract in progress on this side (the host's real one, or a client's replica), or null.</summary>
        VoiceMimic FindMimic(MissionDirector director)
        {
            if (director == null || director.Phase != MissionPhase.Active || !director.CreatureAboard) return null;
            var enemy = director.ActiveEnemy;
            if (enemy == null || enemy.IsReplica != (m_Host == null)) return null;
            if (enemy != m_CachedEnemy)
            {
                m_CachedEnemy = enemy;
                m_CachedMimic = enemy.GetComponent<VoiceMimic>();
            }
            return m_CachedMimic != null && m_CachedMimic.isActiveAndEnabled ? m_CachedMimic : null;
        }

        static Transform MouthOf(VoiceMimic mimic) => mimic.speaker != null ? mimic.speaker.transform : mimic.transform;

        void EnsurePlayback(VoiceMimic mimic)
        {
            Transform mouth = MouthOf(mimic);
            if (m_Playback != null && m_Mouth == mouth) return;
            DropPlayback();
            m_Mouth = mouth;
            m_Playback = VoicePlayback.CreateCreature(mouth, mimic.speaker, mimic.volume);
        }

        void DropPlayback()
        {
            if (m_Playback != null) Object.Destroy(m_Playback.gameObject);
            m_Playback = null;
            m_Mouth = null;
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            Stop();
            m_Bank.Clear();
            m_Disposed = true;
            if (ReferenceEquals(VoiceMimic.Crew, this)) VoiceMimic.Crew = null;
        }
    }
}

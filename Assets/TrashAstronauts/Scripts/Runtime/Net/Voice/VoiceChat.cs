using System;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Core.Voice;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using UnityEngine;

namespace TrashAstronauts.Net.Voice
{
    /// <summary>
    /// Proximity voice chat for one co-op session. Exists only while a session is open (single-player never makes
    /// one): <see cref="Attach"/> adds it next to the NetSession when a session starts, and it removes itself when
    /// the session is gone.
    ///
    /// Sending: it taps the microphone capture <see cref="VoiceSensor"/> already runs for the creature (there is
    /// one Microphone.Start in the game; with MIC off nothing is captured), and feeds every block to a
    /// <see cref="VoiceCapture"/>: resampled to 16 kHz, gated so silence sends nothing, checked against the echo
    /// suppressor, encoded and handed to the <see cref="IVoiceLink"/> two frames at a time. Echo blocks are also
    /// reported back to the sensor, so the creature does not hear a crew member's voice coming out of these
    /// speakers as this player talking. While the Mimic is using the speakers nothing is sent.
    ///
    /// Receiving: one <see cref="VoiceStream"/> (jitter buffer and decoder) per crew member and a
    /// <see cref="VoicePlayback"/> on their stand-in's helmet, made when the stand-in appears and gone with it.
    ///
    /// VOICE CHAT off in the suit settings (separate from MIC) stops both directions at once.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(310)] // after NetSession (300) has polled the session this frame
    public sealed class VoiceChat : MonoBehaviour, VoiceSensor.IMicTap
    {
        sealed class Remote
        {
            public readonly VoiceStream stream = new VoiceStream();
            public VoicePlayback playback;
            public Transform helmet;
        }

        /// <summary>The voice chat of the open session, or null (single-player, or no session yet).</summary>
        public static VoiceChat Active { get; private set; }

        /// <summary>
        /// Jitter buffer limits over an online (Relay) link, in 20 ms frames: 100 to 200 ms, where Wi-Fi uses
        /// VoiceJitterBuffer's own 80 to 120 ms. Internet delay varies far more than a home network's.
        /// </summary>
        public const int OnlineMinDelayFrames = 5, OnlineMaxDelayFrames = 10;

        /// <summary>Test seam: overrides the VOICE CHAT setting (null = the profile's).</summary>
        public static bool? EnabledOverride;

        public MissionDirector director;

        NetSession m_Session;
        object m_BoundSession;
        IVoiceLink m_Link;
        IVoiceCrew m_Crew;
        VoiceCapture m_Capture;
        VoiceSensor m_Sensor;
        AudioListener m_Listener;
        readonly Remote[] m_Remotes = new Remote[NetProtocol.MaxPlayers];
        VoicePacketSink m_Sink;
        VoiceFrameHandler m_OnFrame;

        // ------------------------------------------------------------------ state for the wrist display, avatars and tests

        /// <summary>True while this player's voice is going out.</summary>
        public bool IsTransmitting => m_Capture != null && m_Capture.IsTransmitting && IsLive;
        /// <summary>This player's own microphone level for a talk meter (0..1).</summary>
        public float TransmitLevel => m_Capture != null && IsLive ? Mathf.Clamp01((m_Capture.LevelDb + 50f) / 40f) : 0f;
        /// <summary>True while crew member <paramref name="slot"/> is being heard talking.</summary>
        public bool IsSpeaking(int slot)
        {
            Remote r = RemoteAt(slot);
            return r != null && r.playback != null && r.stream.IsSpeaking(Now);
        }
        /// <summary>Smoothed level of crew member <paramref name="slot"/>'s voice (0..1), for a talk light.</summary>
        public float Level(int slot)
        {
            Remote r = RemoteAt(slot);
            return r != null && r.playback != null ? r.stream.Level01 : 0f;
        }
        /// <summary>The source playing crew member <paramref name="slot"/>'s voice, or null.</summary>
        public VoicePlayback Playback(int slot)
        {
            Remote r = RemoteAt(slot);
            return r != null ? r.playback : null;
        }
        /// <summary>Crew member <paramref name="slot"/>'s receive stream (jitter buffer statistics), or null.</summary>
        public VoiceStream Stream(int slot) => RemoteAt(slot)?.stream;
        public VoiceCapture Capture => m_Capture;
        public IVoiceLink Link => m_Link;
        /// <summary>True when bound to an open link with VOICE CHAT on.</summary>
        public bool IsLive => m_Link != null && m_Link.IsOpen && SettingOn;
        /// <summary>The VOICE CHAT setting.</summary>
        public bool SettingOn
        {
            get
            {
                if (EnabledOverride.HasValue) return EnabledOverride.Value;
                var d = director != null ? director : MissionDirector.Active;
                return d == null || d.Profile == null || d.Profile.Data.voiceChat;
            }
        }
        public long FramesReceived { get; private set; }
        public long FramesIgnored { get; private set; }

        static double Now => NetSession.Now;

        Remote RemoteAt(int slot) => slot >= 0 && slot < m_Remotes.Length ? m_Remotes[slot] : null;

        // ------------------------------------------------------------------ binding

        /// <summary>
        /// The hookup: call when a host or client session has just been created. Adds voice chat beside the
        /// session (once); it follows <see cref="NetSession.HostSession"/> / <see cref="NetSession.ClientSession"/>
        /// and <see cref="NetSession.Crew"/> by itself and removes itself when no session is left.
        /// </summary>
        public static VoiceChat Attach(NetSession session)
        {
            if (session == null) return null;
            var chat = session.GetComponent<VoiceChat>();
            if (chat == null) chat = session.gameObject.AddComponent<VoiceChat>();
            chat.m_Session = session;
            if (chat.director == null) chat.director = session.director;
            chat.Follow();
            return chat;
        }

        /// <summary>Binds to an explicit link and crew (tests, or a session other than a NetSession). Replaces any binding.</summary>
        public void Bind(IVoiceLink link, IVoiceCrew crew)
        {
            Unbind();
            if (link == null) return;
            m_Link = link;
            m_Crew = crew;
            m_Capture = new VoiceCapture(Environment.TickCount & 0xFFFF);
            if (m_Sink == null) m_Sink = SendPacket;
            if (m_OnFrame == null) m_OnFrame = OnFrame;
            m_Link.Received += m_OnFrame;
            Active = this;
        }

        /// <summary>Drops the link, every crew voice source and the microphone tap.</summary>
        public void Unbind()
        {
            if (m_Link != null)
            {
                m_Link.Received -= m_OnFrame;
                if (m_Link is IDisposable disposable) disposable.Dispose();
            }
            m_Link = null;
            m_Crew = null;
            m_BoundSession = null;
            m_Capture = null;
            ReleaseTap();
            for (int i = 0; i < m_Remotes.Length; i++) DropRemote(i);
            if (Active == this) Active = null;
        }

        /// <summary>Keeps the binding on the NetSession's current session. False when it has none.</summary>
        bool Follow()
        {
            if (m_Session == null) return m_Link != null;
            object current = (object)m_Session.HostSession ?? m_Session.ClientSession;
            if (current == null)
            {
                Unbind();
                return false;
            }
            if (!ReferenceEquals(current, m_BoundSession))
            {
                IVoiceLink link = m_Session.HostSession != null
                    ? (IVoiceLink)new HostVoiceLink(m_Session.HostSession)
                    : new ClientVoiceLink(m_Session.ClientSession);
                Bind(link, new NetSessionVoiceCrew(m_Session));
                m_BoundSession = current;
            }
            return true;
        }

        void OnDestroy()
        {
            Unbind();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Active = null;
            EnabledOverride = null;
        }

        // ------------------------------------------------------------------ per frame

        void Update()
        {
            if (!Follow())
            {
                // The session is over: back to single-player, with nothing of voice chat left behind.
                if (m_Session != null) Destroy(this);
                return;
            }
            if (director == null) director = MissionDirector.Active;
            bool live = IsLive;
            UpdateTap(live);
            double now = Now;
            Vector3 listener = ListenerPosition();
            float playbackPower = 0f;
            bool anyPlayback = false;
            for (int slot = 0; slot < m_Remotes.Length; slot++)
            {
                Transform helmet = live && m_Crew != null && slot != m_Link.LocalSlot ? m_Crew.Helmet(slot) : null;
                Remote r = m_Remotes[slot];
                if (r != null && (r.playback == null || r.helmet != helmet))
                {
                    // Its stand-in is gone (destroyed with it, mid-sentence or not) or was replaced.
                    DropRemote(slot);
                    r = null;
                }
                if (helmet == null) continue;
                if (r == null)
                {
                    r = m_Remotes[slot] = new Remote { helmet = helmet };
                    if (m_Session != null && m_Session.IsOnline)
                    {
                        r.stream.Jitter.minDelayFrames = OnlineMinDelayFrames;
                        r.stream.Jitter.maxDelayFrames = OnlineMaxDelayFrames;
                    }
                    r.playback = VoicePlayback.Create(helmet, slot);
                    VoiceTalkLight.Attach(this, helmet, slot);
                }
                float frameDb = r.playback.Pump(r.stream, now);
                if (frameDb > -90f)
                {
                    float gain = r.playback.AudibleGain(listener);
                    if (gain > 1e-4f)
                    {
                        float atListener = frameDb + 20f * Mathf.Log10(gain);
                        playbackPower += Mathf.Pow(10f, atListener / 10f);
                        anyPlayback = true;
                    }
                }
            }
            // What is going to this headset's speakers now, for the echo suppressor.
            if (anyPlayback && m_Capture != null) m_Capture.AddPlayback(10f * Mathf.Log10(playbackPower), now);
        }

        Vector3 ListenerPosition()
        {
            if (m_Listener == null || !m_Listener.isActiveAndEnabled) m_Listener = FindFirstObjectByType<AudioListener>();
            if (m_Listener != null) return m_Listener.transform.position;
            var d = director != null ? director : MissionDirector.Active;
            var local = d != null ? d.LocalPlayer : null;
            return local != null && local.head != null ? local.head.position : Vector3.zero;
        }

        void DropRemote(int slot)
        {
            Remote r = m_Remotes[slot];
            if (r == null) return;
            if (r.playback != null) Destroy(r.playback.gameObject);
            var light = r.helmet != null ? r.helmet.GetComponent<VoiceTalkLight>() : null;
            if (light != null) Destroy(light);
            r.stream.Clear();
            m_Remotes[slot] = null;
        }

        void OnFrame(byte slot, ushort sequence, byte[] buffer, int offset, int length)
        {
            Remote r = RemoteAt(slot);
            if (!IsLive || r == null || r.playback == null || m_Link == null || slot == m_Link.LocalSlot)
            {
                FramesIgnored++; // VOICE CHAT off, or no stand-in to speak from yet
                return;
            }
            FramesReceived++;
            r.stream.Push(sequence, buffer, offset, length, Now);
        }

        // ------------------------------------------------------------------ microphone

        void UpdateTap(bool live)
        {
            var d = director != null ? director : MissionDirector.Active;
            var local = d != null ? d.LocalPlayer : null;
            VoiceSensor sensor = local != null ? local.voice : null;
            if (sensor != m_Sensor) ReleaseTap();
            m_Sensor = sensor;
            if (m_Sensor == null) return;
            if (live)
            {
                if (m_Sensor.MicTap == null) m_Sensor.MicTap = this;
            }
            else
            {
                ReleaseTap();
                m_Capture?.Stop();
            }
        }

        void ReleaseTap()
        {
            if (m_Sensor != null && ReferenceEquals(m_Sensor.MicTap, this)) m_Sensor.MicTap = null;
        }

        /// <summary>Called by the VoiceSensor with every microphone block (main thread). True = echo, not the player.</summary>
        public bool OnMicBlock(float[] block, int count, int sampleRate, bool suppressed)
        {
            if (m_Capture == null || this == null) return false;
            bool allow = IsLive && !suppressed;
            return m_Capture.Feed(block, 0, count, sampleRate, Now, allow, m_Sink);
        }

        bool SendPacket(ushort firstSequence, byte[] frames, int offset, int frameBytes, int frameCount)
        {
            return m_Link != null && m_Link.IsOpen && m_Link.Send(firstSequence, frames, offset, frameBytes, frameCount);
        }
    }
}

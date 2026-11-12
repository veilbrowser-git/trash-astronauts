using System;

namespace TrashAstronauts.Core.Voice
{
    /// <summary>
    /// Decides, one 20 ms frame at a time, whether the player is talking, so silence sends nothing. It tracks the
    /// room's background level (falls quickly to a quieter room, creeps up slowly so speech does not drag it up,
    /// but rises quickly to a sound that stays steady, like a fan switching on: speech never holds that still),
    /// opens after two loud frames in a row (a single click does not open it) and stays open for a hang-over after
    /// the last loud frame so word endings and short pauses are not clipped. The sender adds two frames of
    /// pre-roll when it opens, so the start of the first syllable is kept as well.
    /// </summary>
    public sealed class VoiceActivityGate
    {
        /// <summary>How far above the background a frame must be to count as speech (dB).</summary>
        public float marginDb = 9f;
        /// <summary>Quieter than this (dBFS) is never speech, however quiet the room.</summary>
        public float minDb = -54f;
        /// <summary>Loud frames in a row needed to open.</summary>
        public int onsetFrames = 2;
        /// <summary>Frames the gate stays open after the last loud one (15 = 300 ms).</summary>
        public int hangoverFrames = 15;
        public float floorRiseDbPerSecond = 1f;
        /// <summary>Rise rate while the level is steady (a fan, an engine hum).</summary>
        public float steadyRiseDbPerSecond = 30f;
        /// <summary>Average frame-to-frame level change (dB) under which a sound counts as steady.</summary>
        public float steadyDb = 1.5f;
        public float floorFallDbPerSecond = 24f;
        public float minFloorDb = -90f;
        public float maxFloorDb = -30f;

        int m_Run;
        int m_Hang;
        float m_Previous = -100f;
        float m_Variability = 10f;

        public float FloorDb { get; private set; } = -60f;
        /// <summary>True while frames should be sent.</summary>
        public bool IsOpen { get; private set; }
        /// <summary>True for the one frame on which the gate opened.</summary>
        public bool JustOpened { get; private set; }
        /// <summary>True when the last frame itself was loud (not just held open by the hang-over).</summary>
        public bool Voiced { get; private set; }

        public void Reset(float floorDb = -60f)
        {
            FloorDb = Math.Max(minFloorDb, Math.Min(maxFloorDb, floorDb));
            m_Run = 0;
            m_Hang = 0;
            IsOpen = false;
            JustOpened = false;
            Voiced = false;
            m_Previous = -100f;
            m_Variability = 10f;
        }

        /// <summary>Feeds one frame's level (dBFS). Returns <see cref="IsOpen"/>.</summary>
        public bool Process(float levelDb)
        {
            const float dt = (float)VoiceFormat.FrameSeconds;
            if (float.IsNaN(levelDb)) levelDb = -100f;
            m_Variability += (Math.Min(30f, Math.Abs(levelDb - m_Previous)) - m_Variability) * 0.1f;
            m_Previous = levelDb;
            float rise = m_Variability < steadyDb ? steadyRiseDbPerSecond : floorRiseDbPerSecond;
            if (levelDb < FloorDb) FloorDb = Math.Max(levelDb, FloorDb - floorFallDbPerSecond * dt);
            else FloorDb = Math.Min(levelDb, FloorDb + rise * dt);
            FloorDb = Math.Max(minFloorDb, Math.Min(maxFloorDb, FloorDb));

            Voiced = levelDb >= Math.Max(FloorDb + marginDb, minDb);
            m_Run = Voiced ? m_Run + 1 : 0;
            bool was = IsOpen;
            if (Voiced && (IsOpen || m_Run >= Math.Max(1, onsetFrames)))
            {
                IsOpen = true;
                m_Hang = hangoverFrames;
            }
            else if (IsOpen)
            {
                if (m_Hang > 0) m_Hang--;
                else IsOpen = false;
            }
            JustOpened = IsOpen && !was;
            return IsOpen;
        }

        /// <summary>Closes at once (the frame was the speakers, not the player).</summary>
        public void Close()
        {
            IsOpen = false;
            m_Hang = 0;
            m_Run = 0;
        }
    }
}

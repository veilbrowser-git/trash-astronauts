using System;

namespace TrashAstronauts.Core.Net
{
    /// <summary>
    /// One crew member shoving another with their hands (unreliable stream). The pusher's machine sees its hand go into
    /// the other player's suit and sends this; the host checks it and passes it on to the target only (or applies it
    /// itself when the host is the target); the TARGET's machine moves its own player. Everybody else just sees both move.
    ///
    /// <see cref="velocity"/> is the velocity change for the target (m/s, the argument of ZeroGLocomotion.AddImpulse),
    /// sent in mm/s; <see cref="contact"/> is where the hand met the suit (world, cm), for the thud. The same message
    /// goes out on <see cref="Repeats"/> consecutive send ticks with the same <see cref="seq"/>, so a lost datagram
    /// rarely drops a shove; receivers drop the copies with <see cref="CrewPushDedup"/>.
    ///
    /// Client -> host carries the client's own slot as <see cref="sender"/> (the host overwrites it with the sender's
    /// real slot); host -> target keeps the original sender and sequence. 17 bytes after the type byte.
    ///
    /// The id lives here, not in <see cref="NetMsg"/>, so this stream could be added without touching the shared
    /// tables; NetCrewPushTests fails if <see cref="NetMsg.All"/> (or the weld progress stream) ever takes the same number.
    /// </summary>
    public struct CrewPushMsg
    {
        /// <summary>Unreliable stream id (below <see cref="NetMsg.FirstReliable"/>; 40 is <see cref="CutProgressMsg"/>).</summary>
        public const byte Id = 41;
        public const int Size = 17;

        /// <summary>Send ticks each shove goes out on (same sequence each time).</summary>
        public const int Repeats = 3;
        /// <summary>Most velocity a shove may give the pushed player (m/s).</summary>
        public const float MaxTargetSpeed = 1.5f;
        /// <summary>Most velocity the pusher gets back from one shove (m/s).</summary>
        public const float MaxPusherSpeed = 0.8f;
        /// <summary>The host refuses a shove when the two heads are further apart than this (metres).</summary>
        public const float MaxHeadDistance = 2f;
        /// <summary>Shoves the host accepts from one sender in any one second.</summary>
        public const int MaxPerSecond = 4;

        /// <summary>Bit of <see cref="flags"/>: a hard shove (a swing), not a slow press.</summary>
        public const byte FlagShove = 1;

        public byte sender;
        public byte target;
        public ushort seq;
        public NetVec3 velocity;
        public NetVec3 contact;
        public byte flags;

        public bool IsShove => (flags & FlagShove) != 0;

        /// <summary>The same velocity no longer than <see cref="MaxTargetSpeed"/> (NaN becomes zero).</summary>
        public static NetVec3 Clamp(NetVec3 v, float max = MaxTargetSpeed)
        {
            float m = v.Magnitude;
            if (float.IsNaN(m) || float.IsInfinity(m)) return NetVec3.Zero;
            return m > max ? v * (max / m) : v;
        }

        public void Write(ref NetWriter w)
        {
            w.U8(sender);
            w.U8(target);
            w.U16(seq);
            Quant.WriteVec16(ref w, Clamp(velocity), 1000f);
            Quant.WritePosCm(ref w, contact);
            w.U8(flags);
        }

        public static bool Read(ref NetReader r, out CrewPushMsg m)
        {
            m = new CrewPushMsg { sender = r.U8(), target = r.U8(), seq = r.U16() };
            m.velocity = Quant.ReadVec16(ref r, 0.001f);
            m.contact = Quant.ReadPosCm(ref r);
            m.flags = r.U8();
            return !r.Failed && NetProtocol.IsValidSlot(m.sender) && NetProtocol.IsValidSlot(m.target) && m.sender != m.target;
        }
    }

    /// <summary>
    /// Remembers which (sender, sequence) shoves were already taken in the last <see cref="Window"/> seconds, so the
    /// repeated copies of one shove count once. A sender that rejoins and starts its sequence again is fine once the
    /// window has passed.
    /// </summary>
    public sealed class CrewPushDedup
    {
        public const double Window = 2.0;
        const int k_PerSender = 16;

        readonly ushort[] m_Seq = new ushort[NetProtocol.MaxPlayers * k_PerSender];
        readonly double[] m_Time = new double[NetProtocol.MaxPlayers * k_PerSender];
        readonly int[] m_Next = new int[NetProtocol.MaxPlayers];

        public CrewPushDedup() => Reset();

        public void Reset()
        {
            for (int i = 0; i < m_Time.Length; i++) m_Time[i] = double.NegativeInfinity;
            for (int i = 0; i < m_Next.Length; i++) m_Next[i] = 0;
        }

        /// <summary>True the first time this shove is seen (and remembers it); false for a copy.</summary>
        public bool TryTake(byte sender, ushort seq, double now)
        {
            if (!NetProtocol.IsValidSlot(sender)) return false;
            int start = sender * k_PerSender;
            for (int i = start; i < start + k_PerSender; i++)
                if (m_Seq[i] == seq && now - m_Time[i] < Window) return false;
            int at = start + m_Next[sender];
            m_Seq[at] = seq;
            m_Time[at] = now;
            m_Next[sender] = (m_Next[sender] + 1) % k_PerSender;
            return true;
        }
    }

    /// <summary>Why the host turned a shove down (counters in <see cref="CrewPushStats"/>).</summary>
    public enum CrewPushRefusal : byte
    {
        None,
        Duplicate,
        Away,
        NotConnected,
        TooFar,
        RateLimited,
        Refused,
    }

    /// <summary>Shove counters of one session (tests and the debug overlay).</summary>
    public sealed class CrewPushStats
    {
        public int sent;
        public int received;
        public int duplicates;
        public int relayed;
        public int heardHere;
        public int away;
        public int notConnected;
        public int tooFar;
        public int rateLimited;
        public int refused;
        public CrewPushRefusal lastRefusal;

        public int Rejected => away + notConnected + tooFar + rateLimited + refused;

        internal void Count(CrewPushRefusal why)
        {
            lastRefusal = why;
            switch (why)
            {
                case CrewPushRefusal.Duplicate: duplicates++; break;
                case CrewPushRefusal.Away: away++; break;
                case CrewPushRefusal.NotConnected: notConnected++; break;
                case CrewPushRefusal.TooFar: tooFar++; break;
                case CrewPushRefusal.RateLimited: rateLimited++; break;
                case CrewPushRefusal.Refused: refused++; break;
            }
        }
    }

    /// <summary>A shove for this machine's player: who pushed, the velocity change (m/s), where the hand met the suit.</summary>
    public delegate void CrewPushHandler(byte sender, NetVec3 velocity, NetVec3 contact, byte flags);
}

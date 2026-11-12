using System;

namespace TrashAstronauts.Core.Net
{
    /// <summary>
    /// One reliable, ordered message stream to one peer, plus the datagram sequence/ack bookkeeping that
    /// rides in every datagram header.
    ///
    /// How it works:
    ///  - Every datagram has a 16-bit sequence. Every header carries "ack" (newest datagram sequence received
    ///    from the peer) and 32 "ackBits" (the 32 before it). A datagram seen twice, or more than 32 behind
    ///    the newest, is rejected whole by <see cref="OnDatagramReceived"/>, which also makes the unreliable
    ///    streams duplicate-free.
    ///  - A reliable message has its own 16-bit relSeq. It is put into datagrams until one of the datagrams
    ///    that carried it is acked. It is re-sent <see cref="ResendSeconds"/> (100 ms) after its last send
    ///    for the first <see cref="FastResends"/> (4) resends, which is what repairs loss quickly; after that
    ///    the wait doubles up to <see cref="MaxResendSeconds"/> (200, 400, 800, 800 ... ms), so a peer that
    ///    has stopped listening is not sent the whole window ten times a second. The channel remembers the
    ///    last <see cref="SequenceMemory"/> (16) datagrams that carried each message, about 8 s of sends, so
    ///    an ack that takes far longer than one resend period to come back is still recognised (a round trip
    ///    of many hundred ms costs extra copies; it never stalls the stream). <see cref="ResetBackoff"/> makes
    ///    everything unacked due on the fast interval again; the sessions call it when a peer is heard again
    ///    after a quarter of a second or more of silence.
    ///  - At most <see cref="MaxInFlight"/> (64) messages are in flight; up to <see cref="MaxBacklog"/> (512)
    ///    wait in total. Past that <see cref="Overflowed"/> latches and the session drops the peer.
    ///  - The receiver buffers out-of-order messages (window 64) and hands them out strictly in order,
    ///    exactly once. All sequence maths is wrap-safe.
    ///
    /// Wire form of one reliable message inside a datagram: [type u8][relSeq u16][length u16][payload].
    /// No allocation after warm-up: payload buffers are kept per slot and reused.
    /// </summary>
    public sealed class ReliableChannel
    {
        public const int MaxInFlight = 64;
        public const int MaxBacklog = 512;
        public const int MaxMessageBytes = 1000;
        /// <summary>Bytes a reliable message adds around its payload: type, relSeq, length.</summary>
        public const int Overhead = 5;
        public const double DefaultResendSeconds = 0.1;
        /// <summary>Longest wait between two sends of the same unacked message.</summary>
        public const double MaxResendSeconds = 0.8;
        /// <summary>Resends of one message that go out on the plain <see cref="ResendSeconds"/> interval before the wait starts doubling.</summary>
        public const int FastResends = 4;
        /// <summary>How many of the datagrams that carried a message are remembered for matching acks.</summary>
        public const int SequenceMemory = 16;
        /// <summary>Tolerance on the resend timer, so "100 ms later" is not missed by a floating-point hair.</summary>
        const double k_TimeSlack = 1e-6;

        struct Outgoing
        {
            public byte type;
            public ushort relSeq;
            public byte[] data;
            public int length;
            public double lastSent;
            /// <summary>Wait before the next resend; doubles from the fourth resend on, up to MaxResendSeconds.</summary>
            public double interval;
            /// <summary>Timed resends so far.</summary>
            public int resends;
            public bool sent;
            public bool acked;
            /// <summary>Datagrams that have carried it so far; the last SequenceMemory of them are in m_OutSeqs.</summary>
            public int seqCount;
        }

        struct Incoming
        {
            public bool present;
            public byte type;
            public byte[] data;
            public int length;
        }

        readonly Outgoing[] m_Out = new Outgoing[MaxBacklog];
        /// <summary>Row i holds the sequences of the last SequenceMemory datagrams that carried m_Out[i] (a ring indexed by seqCount).</summary>
        readonly ushort[] m_OutSeqs = new ushort[MaxBacklog * SequenceMemory];
        int m_OutHead;
        int m_OutCount;
        ushort m_NextRelSeq;
        ushort m_NextSendSeq;

        bool m_HasReceived;
        ushort m_ReceivedLatest;
        uint m_ReceivedBits;

        readonly Incoming[] m_In = new Incoming[MaxInFlight];
        ushort m_NextDeliver;

        /// <summary>Seconds after its first send before an unacked message goes out again (later resends back off).</summary>
        public double ResendSeconds = DefaultResendSeconds;

        /// <summary>Messages queued and not yet acked (in flight + waiting).</summary>
        public int Backlog => m_OutCount;
        /// <summary>Latched when a message could not be queued because 512 were already waiting.</summary>
        public bool Overflowed { get; private set; }
        public long SentMessages { get; private set; }
        public long ResentMessages { get; private set; }
        public long DeliveredMessages { get; private set; }
        public long DuplicateMessages { get; private set; }
        public long DuplicateDatagrams { get; private set; }

        /// <param name="firstSequence">
        /// Starting value of both the datagram sequence and relSeq. Both ends of a connection must use the same
        /// value (the sessions use the connection id), which also exercises wrap-around in ordinary play.
        /// </param>
        public ReliableChannel(ushort firstSequence = 0)
        {
            Reset(firstSequence);
        }

        public void Reset(ushort firstSequence)
        {
            for (int i = 0; i < m_Out.Length; i++) { m_Out[i].sent = false; m_Out[i].acked = false; m_Out[i].seqCount = 0; m_Out[i].length = 0; }
            for (int i = 0; i < m_In.Length; i++) m_In[i].present = false;
            m_OutHead = 0;
            m_OutCount = 0;
            m_NextRelSeq = firstSequence;
            m_NextSendSeq = firstSequence;
            m_NextDeliver = firstSequence;
            m_HasReceived = false;
            m_ReceivedLatest = (ushort)(firstSequence - 1);
            m_ReceivedBits = 0u;
            Overflowed = false;
            for (int i = 0; i < k_TimedSlots; i++) m_SentValid[i] = false;
            m_HasTimedAck = false;
            RoundTripSeconds = double.NaN;
        }

        // ------------------------------------------------------------------ sending

        /// <summary>Queues a message. False if it is too big or the backlog is full (which latches <see cref="Overflowed"/>).</summary>
        public bool Enqueue(byte type, byte[] payload, int offset, int length)
        {
            if (length < 0 || length > MaxMessageBytes) return false;
            if (length > 0 && (payload == null || offset < 0 || offset + length > payload.Length)) return false;
            if (m_OutCount >= MaxBacklog) { Overflowed = true; return false; }

            int index = (m_OutHead + m_OutCount) % MaxBacklog;
            m_OutCount++;
            if (m_Out[index].data == null || m_Out[index].data.Length < length)
                m_Out[index].data = new byte[length <= 32 ? 32 : length <= 128 ? 128 : MaxMessageBytes];
            if (length > 0) Buffer.BlockCopy(payload, offset, m_Out[index].data, 0, length);
            m_Out[index].type = type;
            m_Out[index].relSeq = m_NextRelSeq++;
            m_Out[index].length = length;
            m_Out[index].sent = false;
            m_Out[index].acked = false;
            m_Out[index].seqCount = 0;
            m_Out[index].lastSent = 0;
            m_Out[index].interval = ResendSeconds;
            m_Out[index].resends = 0;
            return true;
        }

        /// <summary>Takes the sequence number for the datagram about to be built.</summary>
        public ushort NextSendSequence() { return m_NextSendSeq++; }

        // ------------------------------------------------------------------ round trip (no wire change: every header acks)

        const int k_TimedSlots = 64;
        readonly double[] m_SentAt = new double[k_TimedSlots];
        readonly ushort[] m_SentSeq = new ushort[k_TimedSlots];
        readonly bool[] m_SentValid = new bool[k_TimedSlots];
        ushort m_LastTimedAck;
        bool m_HasTimedAck;

        /// <summary>
        /// Smoothed time from sending a datagram to receiving the first header that acknowledges it (seconds). It
        /// includes up to one send interval the peer waits before its next datagram. NaN before the first sample.
        /// </summary>
        public double RoundTripSeconds { get; private set; } = double.NaN;

        /// <summary>The datagram with this sequence went out now.</summary>
        public void NoteSent(ushort sequence, double now)
        {
            int i = sequence % k_TimedSlots;
            m_SentAt[i] = now;
            m_SentSeq[i] = sequence;
            m_SentValid[i] = true;
        }

        /// <summary>A header from the peer acknowledged datagrams up to <paramref name="ack"/> (call once per received datagram).</summary>
        public void NoteAck(ushort ack, double now)
        {
            if (m_HasTimedAck && ack == m_LastTimedAck) return;
            int i = ack % k_TimedSlots;
            if (!m_SentValid[i] || m_SentSeq[i] != ack) return;
            m_HasTimedAck = true;
            m_LastTimedAck = ack;
            double sample = now - m_SentAt[i];
            if (sample < 0 || sample > 5.0) return;
            RoundTripSeconds = double.IsNaN(RoundTripSeconds) ? sample : RoundTripSeconds + (sample - RoundTripSeconds) * 0.1;
        }

        /// <summary>The ack fields for the header of the datagram about to be built.</summary>
        public void GetAck(out ushort ack, out uint ackBits)
        {
            ack = m_ReceivedLatest;
            ackBits = m_ReceivedBits;
        }

        /// <summary>True when at least one in-flight message has never been sent or is due for a resend.</summary>
        public bool HasDue(double now)
        {
            int n = m_OutCount < MaxInFlight ? m_OutCount : MaxInFlight;
            for (int i = 0; i < n; i++)
            {
                int index = (m_OutHead + i) % MaxBacklog;
                if (m_Out[index].acked) continue;
                if (!m_Out[index].sent || now - m_Out[index].lastSent >= m_Out[index].interval - k_TimeSlack) return true;
            }
            return false;
        }

        /// <summary>
        /// Puts every unacked in-flight message back on the fast resend interval, so anything last sent more
        /// than <see cref="ResendSeconds"/> ago goes out with the next datagram. The sessions call it when a
        /// peer is heard again after a gap (a burst of loss ending, a headset waking up): it should not have
        /// to wait out an 800 ms backoff.
        /// </summary>
        public void ResetBackoff()
        {
            int n = m_OutCount < MaxInFlight ? m_OutCount : MaxInFlight;
            for (int i = 0; i < n; i++)
            {
                int index = (m_OutHead + i) % MaxBacklog;
                m_Out[index].interval = ResendSeconds;
                m_Out[index].resends = 0;
            }
        }

        /// <summary>
        /// Appends every in-flight message that is due (never sent, or its resend interval has passed; all of
        /// them when <paramref name="force"/>) and still fits. Returns how many were written.
        /// </summary>
        public int WriteDue(ref NetWriter writer, ushort datagramSequence, double now, bool force = false)
        {
            int written = 0;
            int n = m_OutCount < MaxInFlight ? m_OutCount : MaxInFlight;
            for (int i = 0; i < n; i++)
            {
                int index = (m_OutHead + i) % MaxBacklog;
                if (m_Out[index].acked) continue;
                bool resend = m_Out[index].sent;
                bool due = !resend || now - m_Out[index].lastSent >= m_Out[index].interval - k_TimeSlack;
                if (!due && !force) continue;
                if (writer.Remaining < Overhead + m_Out[index].length) continue;

                writer.U8(m_Out[index].type);
                writer.U16(m_Out[index].relSeq);
                writer.U16((ushort)m_Out[index].length);
                writer.Bytes(m_Out[index].data, 0, m_Out[index].length);

                m_OutSeqs[index * SequenceMemory + (m_Out[index].seqCount & (SequenceMemory - 1))] = datagramSequence;
                m_Out[index].seqCount++;
                if (resend && due && ++m_Out[index].resends >= FastResends)
                {
                    // from the fourth timed resend on (forced extra copies do not count) the wait doubles
                    double cap = ResendSeconds > MaxResendSeconds ? ResendSeconds : MaxResendSeconds;
                    double next = m_Out[index].interval * 2;
                    m_Out[index].interval = next < cap ? next : cap;
                }
                m_Out[index].sent = true;
                m_Out[index].lastSent = now;
                written++;
                SentMessages++;
                if (resend) ResentMessages++;
            }
            return written;
        }

        /// <summary>Applies the ack fields of a datagram header received from the peer.</summary>
        public void OnAck(ushort ack, uint ackBits)
        {
            int n = m_OutCount < MaxInFlight ? m_OutCount : MaxInFlight;
            for (int i = 0; i < n; i++)
            {
                int index = (m_OutHead + i) % MaxBacklog;
                if (m_Out[index].acked || !m_Out[index].sent) continue;
                int recorded = m_Out[index].seqCount < SequenceMemory ? m_Out[index].seqCount : SequenceMemory;
                int row = index * SequenceMemory;
                for (int k = 0; k < recorded; k++)
                {
                    if (!IsAcked(m_OutSeqs[row + k], ack, ackBits)) continue;
                    m_Out[index].acked = true;
                    break;
                }
            }
            while (m_OutCount > 0 && m_Out[m_OutHead].acked)
            {
                m_Out[m_OutHead].acked = false;
                m_Out[m_OutHead].sent = false;
                m_OutHead = (m_OutHead + 1) % MaxBacklog;
                m_OutCount--;
            }
        }

        static bool IsAcked(ushort sequence, ushort ack, uint ackBits)
        {
            int behind = (ushort)(ack - sequence);
            if (behind == 0) return true;
            return behind <= 32 && (ackBits & (1u << (behind - 1))) != 0;
        }

        // ------------------------------------------------------------------ receiving

        /// <summary>
        /// True when a datagram with this sequence has not been registered yet and is not more than 32 behind the
        /// newest. Changes nothing: the sessions check this first, validate the whole datagram, and only then
        /// call <see cref="OnDatagramReceived"/>, so a truncated datagram is never acknowledged.
        /// </summary>
        public bool IsNewDatagram(ushort sequence)
        {
            if (!m_HasReceived) return true;
            int delta = (short)(sequence - m_ReceivedLatest);
            if (delta > 0) return true;
            if (delta == 0 || delta < -32) return false;
            return (m_ReceivedBits & (1u << (-delta - 1))) == 0;
        }

        /// <summary>
        /// Registers a datagram sequence from the peer (it will be acknowledged from now on). False means "drop
        /// this datagram": it is a duplicate or more than 32 behind the newest (anything reliable in it will be
        /// re-sent).
        /// </summary>
        public bool OnDatagramReceived(ushort sequence)
        {
            if (!m_HasReceived)
            {
                m_HasReceived = true;
                m_ReceivedLatest = sequence;
                m_ReceivedBits = 0u;
                return true;
            }
            int delta = (short)(sequence - m_ReceivedLatest);
            if (delta > 0)
            {
                // the old newest becomes bit (delta - 1); everything else shifts further back
                if (delta > 32) m_ReceivedBits = 0u;
                else if (delta == 32) m_ReceivedBits = 1u << 31;
                else m_ReceivedBits = (m_ReceivedBits << delta) | (1u << (delta - 1));
                m_ReceivedLatest = sequence;
                return true;
            }
            if (delta == 0) { DuplicateDatagrams++; return false; }
            int back = -delta;
            if (back > 32) { DuplicateDatagrams++; return false; }
            uint bit = 1u << (back - 1);
            if ((m_ReceivedBits & bit) != 0) { DuplicateDatagrams++; return false; }
            m_ReceivedBits |= bit;
            return true;
        }

        /// <summary>
        /// Stores one reliable message read from a datagram. Duplicates and anything outside the 64-message
        /// window are ignored (false). Call <see cref="TryDeliver"/> afterwards.
        /// </summary>
        public bool Accept(ushort relSeq, byte type, byte[] buffer, int offset, int length)
        {
            if (length < 0 || length > MaxMessageBytes) return false;
            if (length > 0 && (buffer == null || offset < 0 || offset + length > buffer.Length)) return false;
            int ahead = (short)(relSeq - m_NextDeliver);
            if (ahead < 0) { DuplicateMessages++; return false; }
            if (ahead >= MaxInFlight) return false;
            int index = relSeq & (MaxInFlight - 1);
            if (m_In[index].present) { DuplicateMessages++; return false; }
            if (m_In[index].data == null || m_In[index].data.Length < length)
                m_In[index].data = new byte[length <= 32 ? 32 : length <= 128 ? 128 : MaxMessageBytes];
            if (length > 0) Buffer.BlockCopy(buffer, offset, m_In[index].data, 0, length);
            m_In[index].type = type;
            m_In[index].length = length;
            m_In[index].present = true;
            return true;
        }

        /// <summary>
        /// Hands out the next message in order, if it has arrived. The returned buffer belongs to the channel
        /// and is valid until the next <see cref="Accept"/>.
        /// </summary>
        public bool TryDeliver(out byte type, out byte[] data, out int length)
        {
            int index = m_NextDeliver & (MaxInFlight - 1);
            if (!m_In[index].present)
            {
                type = 0;
                data = null;
                length = 0;
                return false;
            }
            m_In[index].present = false;
            m_NextDeliver++;
            type = m_In[index].type;
            data = m_In[index].data;
            length = m_In[index].length;
            DeliveredMessages++;
            return true;
        }
    }
}

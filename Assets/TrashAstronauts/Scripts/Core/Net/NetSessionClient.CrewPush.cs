namespace TrashAstronauts.Core.Net
{
    /// <summary>
    /// Crew shoves on a client (<see cref="CrewPushMsg"/>): this player's shoves go to the host on the next
    /// <see cref="CrewPushMsg.Repeats"/> send ticks (same sequence each time); a shove the host passes on to this
    /// player is reported once through <see cref="CrewPushReceived"/>, its copies dropped. Kept out of IClientWorld so
    /// no world has to change.
    /// </summary>
    public sealed partial class NetSessionClient
    {
        const int k_PushOutQueue = 8;
        readonly CrewPushMsg[] m_PushOut = new CrewPushMsg[k_PushOutQueue];
        readonly int[] m_PushOutLeft = new int[k_PushOutQueue];
        int m_PushOutCount;
        ushort m_PushSeq;
        readonly CrewPushDedup m_PushDedup = new CrewPushDedup();
        readonly CrewPushStats m_PushStats = new CrewPushStats();

        /// <summary>A crew member shoved this player (sender's slot, velocity change m/s, contact point), heard during <see cref="Poll"/>.</summary>
        public event CrewPushHandler CrewPushReceived;

        public CrewPushStats CrewPushStats => m_PushStats;

        /// <summary>
        /// This player shoved crew member <paramref name="target"/>: goes to the host (which checks it and passes it on)
        /// on the next <see cref="CrewPushMsg.Repeats"/> send ticks. False before the Welcome, for this player's own
        /// slot, or when too many shoves are already waiting.
        /// </summary>
        public bool SendCrewPush(byte target, NetVec3 velocity, NetVec3 contact, byte flags)
        {
            if (State != ClientState.Connected || !Welcomed) return false;
            if (!NetProtocol.IsValidSlot(target) || target == LocalSlot || m_PushOutCount >= k_PushOutQueue) return false;
            m_PushOut[m_PushOutCount] = new CrewPushMsg
            {
                sender = LocalSlot,
                target = target,
                seq = ++m_PushSeq,
                velocity = CrewPushMsg.Clamp(velocity),
                contact = contact,
                flags = flags,
            };
            m_PushOutLeft[m_PushOutCount] = CrewPushMsg.Repeats;
            m_PushOutCount++;
            m_PushStats.sent++;
            return true;
        }

        void WriteCrewPush(ref NetWriter w, double time)
        {
            int kept = 0;
            for (int i = 0; i < m_PushOutCount; i++)
            {
                w.U8(CrewPushMsg.Id);
                int start = w.Position;
                m_PushOut[i].Write(ref w);
                Tap?.Invoke(true, 0, CrewPushMsg.Id, m_Send, start, w.Position - start, time);
                if (--m_PushOutLeft[i] > 0)
                {
                    m_PushOut[kept] = m_PushOut[i];
                    m_PushOutLeft[kept] = m_PushOutLeft[i];
                    kept++;
                }
            }
            m_PushOutCount = kept;
        }

        bool DispatchCrewPush(ref NetReader r)
        {
            if (!CrewPushMsg.Read(ref r, out CrewPushMsg m)) return false;
            if (m.target != LocalSlot) { m_Stats.rejected++; return true; } // the host only sends a shove to its target
            if (!m_PushDedup.TryTake(m.sender, m.seq, m_Now))
            {
                m_PushStats.Count(CrewPushRefusal.Duplicate);
                return true;
            }
            m_PushStats.received++;
            m_PushStats.heardHere++;
            CrewPushReceived?.Invoke(m.sender, CrewPushMsg.Clamp(m.velocity), m.contact, m.flags);
            return true;
        }
    }
}

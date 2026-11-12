using System;

namespace TrashAstronauts.Core.Net
{
    /// <summary>
    /// Crew shoves on the host (<see cref="CrewPushMsg"/>). A client's shove is checked here (both players connected and
    /// not Away, heads within <see cref="CrewPushMsg.MaxHeadDistance"/>, at most <see cref="CrewPushMsg.MaxPerSecond"/>
    /// a second from one sender, the velocity clamped, and <see cref="CrewPushAllowed"/> if the game set it), then
    /// either reported through <see cref="CrewPushReceived"/> (the host is the target) or passed on to the target
    /// client only. The host's own shoves go straight to their target. Every outgoing shove is written on
    /// <see cref="CrewPushMsg.Repeats"/> consecutive send ticks with the same sequence. Kept out of IHostWorld so no
    /// world has to change.
    /// </summary>
    public sealed partial class NetSessionHost
    {
        struct PendingPush
        {
            public CrewPushMsg msg;
            public int left;
        }

        const int k_PushQueue = 8;
        readonly PendingPush[][] m_PushOut = NewPushQueues();
        readonly int[] m_PushOutCount = new int[NetProtocol.MaxPlayers];
        readonly CrewPushDedup m_PushDedup = new CrewPushDedup();
        readonly double[][] m_PushTimes = NewPushTimes();
        readonly int[] m_PushTimeIndex = new int[NetProtocol.MaxPlayers];
        readonly CrewPushStats m_PushStats = new CrewPushStats();
        ushort m_PushSeq;

        /// <summary>A shove for the host's own player (sender's slot, velocity change m/s, contact point), heard during <see cref="Poll"/>.</summary>
        public event CrewPushHandler CrewPushReceived;

        /// <summary>
        /// Optional extra check by the game, (sender, target) -> allowed: for example neither player is downed.
        /// Null allows everything that passed the session's own checks.
        /// </summary>
        public Func<byte, byte, bool> CrewPushAllowed;

        public CrewPushStats CrewPushStats => m_PushStats;

        static PendingPush[][] NewPushQueues()
        {
            var q = new PendingPush[NetProtocol.MaxPlayers][];
            for (int i = 0; i < q.Length; i++) q[i] = new PendingPush[k_PushQueue];
            return q;
        }

        static double[][] NewPushTimes()
        {
            var t = new double[NetProtocol.MaxPlayers][];
            for (int i = 0; i < t.Length; i++)
            {
                t[i] = new double[CrewPushMsg.MaxPerSecond];
                for (int n = 0; n < t[i].Length; n++) t[i][n] = double.NegativeInfinity;
            }
            return t;
        }

        /// <summary>
        /// The host player shoved crew member <paramref name="target"/>: sent to that client only, on the next
        /// <see cref="CrewPushMsg.Repeats"/> send ticks. False (nothing sent) for the host itself, an empty or Away slot.
        /// </summary>
        public bool SendCrewPush(byte target, NetVec3 velocity, NetVec3 contact, byte flags)
        {
            if (m_Closed || m_Disposed) return false;
            if (target == NetProtocol.HostSlot || !IsConnected(target) || IsAway(target)) return false;
            var m = new CrewPushMsg
            {
                sender = NetProtocol.HostSlot,
                target = target,
                seq = ++m_PushSeq,
                velocity = CrewPushMsg.Clamp(velocity),
                contact = contact,
                flags = flags,
            };
            if (!QueuePush(target, in m)) return false;
            m_PushStats.sent++;
            return true;
        }

        bool QueuePush(byte to, in CrewPushMsg m)
        {
            int n = m_PushOutCount[to];
            if (n >= k_PushQueue) return false;
            m_PushOut[to][n] = new PendingPush { msg = m, left = CrewPushMsg.Repeats };
            m_PushOutCount[to] = n + 1;
            return true;
        }

        bool HeadOf(byte slot, out NetVec3 head)
        {
            if (slot == NetProtocol.HostSlot)
            {
                head = m_LocalAvatar.headPosition;
                return m_HasLocalAvatar;
            }
            Peer p = m_Peers[slot];
            head = p.avatar.headPosition;
            return p.used && p.hasAvatar;
        }

        /// <summary>The host's rules for a client's shove (sender already set to the peer's real slot).</summary>
        CrewPushRefusal CheckPush(Peer from, in CrewPushMsg m)
        {
            if (from.away) return CrewPushRefusal.Away;
            if (m.target == m.sender) return CrewPushRefusal.Refused;
            if (!IsConnected(m.target)) return CrewPushRefusal.NotConnected;
            if (IsAway(m.target)) return CrewPushRefusal.Away;
            if (!HeadOf(m.sender, out NetVec3 a) || !HeadOf(m.target, out NetVec3 b)
                || NetVec3.Distance(a, b) > CrewPushMsg.MaxHeadDistance) return CrewPushRefusal.TooFar;
            double[] times = m_PushTimes[m.sender];
            int i = m_PushTimeIndex[m.sender];
            if (m_Now - times[i] < 1.0) return CrewPushRefusal.RateLimited; // already MaxPerSecond this second
            if (CrewPushAllowed != null && !CrewPushAllowed(m.sender, m.target)) return CrewPushRefusal.Refused;
            times[i] = m_Now;
            m_PushTimeIndex[m.sender] = (i + 1) % times.Length;
            return CrewPushRefusal.None;
        }

        bool HandleCrewPush(Peer peer, ref NetReader r)
        {
            int start = r.Position;
            if (!CrewPushMsg.Read(ref r, out CrewPushMsg m)) return false;
            Tap?.Invoke(false, peer.slot, CrewPushMsg.Id, m_Receive, start, r.Position - start, m_Now);
            m.sender = peer.slot; // whatever the client wrote, it can only speak for itself
            if (!m_PushDedup.TryTake(m.sender, m.seq, m_Now))
            {
                m_PushStats.Count(CrewPushRefusal.Duplicate);
                return true;
            }
            m_PushStats.received++;
            CrewPushRefusal why = CheckPush(peer, in m);
            if (why != CrewPushRefusal.None)
            {
                m_PushStats.Count(why);
                m_Stats.rejected++;
                return true;
            }
            m.velocity = CrewPushMsg.Clamp(m.velocity);
            if (m.target == NetProtocol.HostSlot)
            {
                m_PushStats.heardHere++;
                CrewPushReceived?.Invoke(m.sender, m.velocity, m.contact, m.flags);
            }
            else if (QueuePush(m.target, in m)) m_PushStats.relayed++;
            return true;
        }

        void WriteCrewPush(Peer to, ref NetWriter w)
        {
            PendingPush[] queue = m_PushOut[to.slot];
            for (int i = 0; i < m_PushOutCount[to.slot]; i++)
            {
                w.U8(CrewPushMsg.Id);
                int start = w.Position;
                queue[i].msg.Write(ref w);
                Tap?.Invoke(true, to.slot, CrewPushMsg.Id, m_Send, start, w.Position - start, m_Now);
            }
        }

        /// <summary>After a send tick: every queued shove has gone out once more; the ones sent often enough are dropped.</summary>
        void ClearCrewPush()
        {
            for (int slot = 0; slot < m_PushOut.Length; slot++)
            {
                PendingPush[] queue = m_PushOut[slot];
                int kept = 0;
                for (int i = 0; i < m_PushOutCount[slot]; i++)
                {
                    queue[i].left--;
                    if (queue[i].left > 0) queue[kept++] = queue[i];
                }
                m_PushOutCount[slot] = kept;
            }
        }
    }
}

using System;

namespace TrashAstronauts.Core.Net
{
    /// <summary>
    /// Weld progress stream on the host (<see cref="CutProgressMsg"/>): the host's own cutting goes out with the next
    /// send tick, a client's is heard, reported through <see cref="CutProgressReceived"/> and relayed to the other
    /// clients with the next send tick. At most one message per cutter per tick (the newest wins); nothing is queued
    /// beyond that and nothing is kept once the tick has gone out. Kept out of IHostWorld so no world has to change.
    /// </summary>
    public sealed partial class NetSessionHost
    {
        readonly CutProgressMsg[] m_Cut = new CutProgressMsg[NetProtocol.MaxPlayers];
        readonly bool[] m_CutFresh = new bool[NetProtocol.MaxPlayers];

        /// <summary>A client's weld progress (slot, panel, index, progress 0..255), heard during <see cref="Poll"/>.</summary>
        public event Action<byte, byte, byte, byte> CutProgressReceived;

        /// <summary>The host player's own weld progress, sent to every client with the next send tick.</summary>
        public void SendCutProgress(byte panel, byte index, byte progress)
        {
            if (m_Closed || m_Disposed) return;
            m_Cut[NetProtocol.HostSlot] = new CutProgressMsg { slot = NetProtocol.HostSlot, panel = panel, index = index, progress = progress };
            m_CutFresh[NetProtocol.HostSlot] = true;
        }

        bool HandleCutProgress(Peer peer, ref NetReader r)
        {
            int start = r.Position;
            if (!CutProgressMsg.Read(ref r, out CutProgressMsg m)) return false;
            Tap?.Invoke(false, peer.slot, CutProgressMsg.Id, m_Receive, start, r.Position - start, m_Now);
            if (peer.away) { m_Stats.rejected++; return true; }
            m.slot = peer.slot; // whatever the client wrote, it can only speak for itself
            m_Cut[peer.slot] = m;
            m_CutFresh[peer.slot] = true;
            CutProgressReceived?.Invoke(m.slot, m.panel, m.index, m.progress);
            return true;
        }

        void WriteCutProgress(Peer to, ref NetWriter w)
        {
            for (int slot = 0; slot < m_Cut.Length; slot++)
            {
                if (!m_CutFresh[slot] || slot == to.slot) continue;
                if (slot != NetProtocol.HostSlot && (!m_Peers[slot].used || !m_Peers[slot].confirmed)) continue;
                w.U8(CutProgressMsg.Id);
                int start = w.Position;
                m_Cut[slot].Write(ref w);
                Tap?.Invoke(true, to.slot, CutProgressMsg.Id, m_Send, start, w.Position - start, m_Now);
            }
        }

        void ClearCutProgress()
        {
            for (int slot = 0; slot < m_CutFresh.Length; slot++) m_CutFresh[slot] = false;
        }
    }
}

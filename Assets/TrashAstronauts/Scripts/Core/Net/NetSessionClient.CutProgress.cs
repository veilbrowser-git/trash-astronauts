using System;

namespace TrashAstronauts.Core.Net
{
    /// <summary>
    /// Weld progress stream on a client (<see cref="CutProgressMsg"/>): this player's own cutting goes to the host
    /// with the next send tick; other crew members' cutting (relayed by the host) is reported through
    /// <see cref="CutProgressReceived"/>. Kept out of IClientWorld so no world has to change.
    /// </summary>
    public sealed partial class NetSessionClient
    {
        CutProgressMsg m_CutOut;
        bool m_CutOutFresh;

        /// <summary>Another crew member's weld progress (slot, panel, index, progress 0..255), heard during <see cref="Poll"/>.</summary>
        public event Action<byte, byte, byte, byte> CutProgressReceived;

        /// <summary>This player's weld progress, sent to the host with the next send tick (a later call replaces it).</summary>
        public void SendCutProgress(byte panel, byte index, byte progress)
        {
            if (State != ClientState.Connected) return;
            m_CutOut = new CutProgressMsg { slot = LocalSlot, panel = panel, index = index, progress = progress };
            m_CutOutFresh = true;
        }

        void WriteCutProgress(ref NetWriter w, double time)
        {
            if (!m_CutOutFresh) return;
            m_CutOutFresh = false;
            w.U8(CutProgressMsg.Id);
            int start = w.Position;
            m_CutOut.Write(ref w);
            Tap?.Invoke(true, 0, CutProgressMsg.Id, m_Send, start, w.Position - start, time);
        }

        bool DispatchCutProgress(ref NetReader r)
        {
            if (!CutProgressMsg.Read(ref r, out CutProgressMsg m)) return false;
            if (m.slot != LocalSlot) CutProgressReceived?.Invoke(m.slot, m.panel, m.index, m.progress);
            return true;
        }
    }
}

using System;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Tools;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// Weld progress for the whole crew (<see cref="CutProgressMsg"/>). While this player's cutter flame is on a weld
    /// (one stretch of a seam), its progress goes out about 5 times a second, and once more when the flame moves on
    /// to the next stretch, through the open session (client -> host -> the others, or host ->
    /// clients); progress heard from the others is shown on the same weld with <see cref="Cuttable.ApplyRemoteProgress"/>,
    /// which never completes it (a finished weld still travels the reliable PointCut path).
    ///
    /// Lives next to the NetSession on the Mission object, and only while a session with crew is open: the first
    /// RemoteAvatar made under a NetSession adds it (<see cref="Attach"/>), and it removes itself once the session has
    /// closed. Single-player never creates it. It uses only the session's public surface (HostSession, ClientSession,
    /// Crew), so NetSession itself does not change.
    /// </summary>
    public sealed class CoopToolSync : MonoBehaviour
    {
        public NetSession session;

        NetSessionHost m_Host;
        NetSessionClient m_Client;
        CutPoint m_LastPoint;
        byte m_LastSentProgress;
        double m_NextSend;
        Action<byte, byte, byte, byte> m_OnProgress;

        /// <summary>Tracers of the host's own darts sent to the crew (tests).</summary>
        public int TracersSent { get; private set; }
        /// <summary>Progress messages this machine sent (tests).</summary>
        public int Sent { get; private set; }
        /// <summary>Progress messages shown on a weld here (tests).</summary>
        public int Applied { get; private set; }

        /// <summary>The sync for this session, added on first use. Null without a session.</summary>
        public static CoopToolSync Attach(NetSession session)
        {
            if (session == null) return null;
            var sync = session.GetComponent<CoopToolSync>();
            if (sync == null) sync = session.gameObject.AddComponent<CoopToolSync>();
            sync.session = session;
            sync.Bind();
            return sync;
        }

        /// <summary>
        /// Shows a tranquilizer tracer on crew member <paramref name="by"/>'s stand-in (the IClientWorld.OnTranqTracer
        /// hookup). False if that slot has no stand-in here.
        /// </summary>
        public static bool ShowTranqTracer(NetSession session, byte by, NetVec3 from, NetVec3 to)
        {
            var crew = session != null ? session.Crew(by) : null;
            if (crew == null || crew.tools == null) return false;
            crew.tools.ShowTracer(from.ToUnity(), to.ToUnity());
            return true;
        }

        void OnEnable() => TranqTool.Fired += OnTranqFired;

        void OnDisable() => TranqTool.Fired -= OnTranqFired;

        /// <summary>
        /// The host's own dart: the crew see its tracer (TranqTracer, sent to every client). A client's shots have no
        /// message of their own yet; see Docs/REMOTE_TOOLS_NOTES.md.
        /// </summary>
        void OnTranqFired(TranqTool tool, Vector3 from, Vector3 to)
        {
            Bind();
            var local = session != null && session.director != null ? session.director.LocalPlayer : null;
            if (m_Host == null || tool == null || local == null || local.tools == null || System.Array.IndexOf(local.tools, tool) < 0) return;
            m_Host.SendTranqTracer(NetProtocol.HostSlot, from.ToNet(), to.ToNet());
            TracersSent++;
        }

        void Bind()
        {
            if (m_OnProgress == null) m_OnProgress = OnCutProgress;
            var host = session != null ? session.HostSession : null;
            var client = session != null ? session.ClientSession : null;
            if (host != m_Host)
            {
                if (m_Host != null) m_Host.CutProgressReceived -= m_OnProgress;
                m_Host = host;
                if (m_Host != null) m_Host.CutProgressReceived += m_OnProgress;
            }
            if (client != m_Client)
            {
                if (m_Client != null) m_Client.CutProgressReceived -= m_OnProgress;
                m_Client = client;
                if (m_Client != null) m_Client.CutProgressReceived += m_OnProgress;
            }
        }

        void Update()
        {
            Bind();
            if (m_Host == null && m_Client == null)
            {
                Destroy(this); // the session closed: nothing of co-op stays behind
                return;
            }
            SendOwnProgress(NetSession.Now);
        }

        void OnDestroy()
        {
            if (m_Host != null) m_Host.CutProgressReceived -= m_OnProgress;
            if (m_Client != null) m_Client.CutProgressReceived -= m_OnProgress;
            m_Host = null;
            m_Client = null;
        }

        void SendOwnProgress(double now)
        {
            var director = session.director;
            var local = director != null ? director.LocalPlayer : null;
            CutPoint point = null;
            if (local != null && local.tools != null)
            {
                for (int i = 0; i < local.tools.Length && point == null; i++)
                {
                    if (local.tools[i] is PlasmaCutterTool cutter && cutter.IsCutting) point = cutter.CurrentPoint;
                }
            }
            if (point != m_LastPoint && m_LastPoint != null)
            {
                // The flame moved on along the seam: the stretch it left gets its last progress, so the crew see the
                // line reach where it really stopped (a finished stretch goes by the reliable PointCut instead).
                var left = m_LastPoint;
                byte final = CutProgressMsg.ToByte(left.Progress);
                if (!left.IsCut && left.Owner != null && !left.Owner.IsOpen && final != m_LastSentProgress) Send(director, left, final);
                m_LastPoint = null;
            }
            if (point == null || point.IsCut || point.Owner == null || point.Owner.isTraining || point.Owner.IsOpen)
            {
                m_LastPoint = null;
                return;
            }
            if (point == m_LastPoint && now < m_NextSend) return;

            byte progress = CutProgressMsg.ToByte(point.Progress);
            if (!Send(director, point, progress)) return;
            m_LastPoint = point;
            m_LastSentProgress = progress;
            m_NextSend = now + CutProgressMsg.Interval;
        }

        bool Send(MissionDirector director, CutPoint point, byte progress)
        {
            if (point.Owner == null || point.Owner.isTraining) return false;
            int panel = director.NetIds.Panels.IndexOf(point.Owner.panelId);
            int index = Array.IndexOf(point.Owner.cutPoints, point);
            if (panel < 0 || panel > 255 || index < 0 || index > 255) return false;
            if (m_Host != null) m_Host.SendCutProgress((byte)panel, (byte)index, progress);
            else if (m_Client != null && m_Client.Welcomed) m_Client.SendCutProgress((byte)panel, (byte)index, progress);
            else return false;
            Sent++;
            return true;
        }

        void OnCutProgress(byte slot, byte panel, byte index, byte progress)
        {
            var director = session != null ? session.director : null;
            var cuttable = director != null ? director.PanelAt(panel) : null;
            if (cuttable != null && cuttable.ApplyRemoteProgress(index, CutProgressMsg.FromByte(progress))) Applied++;
        }
    }
}

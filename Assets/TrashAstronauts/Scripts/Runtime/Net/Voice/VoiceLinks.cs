using System;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Core.Voice;
using UnityEngine;

namespace TrashAstronauts.Net.Voice
{
    /// <summary>Voice chat over the host session: sends the host player's voice to every client, hears every client.</summary>
    public sealed class HostVoiceLink : IVoiceLink, IDisposable
    {
        readonly NetSessionHost m_Session;
        bool m_Disposed;

        public HostVoiceLink(NetSessionHost session)
        {
            m_Session = session ?? throw new ArgumentNullException(nameof(session));
            m_Session.VoiceReceived += OnVoice;
        }

        public NetSessionHost Session => m_Session;
        public bool IsOpen => !m_Disposed && !m_Session.IsClosed;
        public byte LocalSlot => NetProtocol.HostSlot;
        public event VoiceFrameHandler Received;

        public bool Send(ushort firstSequence, byte[] frames, int offset, int frameBytes, int frameCount)
        {
            if (!IsOpen) return false;
            m_Session.SendVoice(firstSequence, frames, offset, frameBytes, frameCount);
            return true; // also when nobody is listening yet: the frames simply had no one to go to
        }

        void OnVoice(byte slot, ushort sequence, byte[] buffer, int offset, int length) => Received?.Invoke(slot, sequence, buffer, offset, length);

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            m_Session.VoiceReceived -= OnVoice;
        }
    }

    /// <summary>Voice chat over a client session: sends this player's voice to the host (who relays it), hears everyone else.</summary>
    public sealed class ClientVoiceLink : IVoiceLink, IDisposable
    {
        readonly NetSessionClient m_Session;
        bool m_Disposed;

        public ClientVoiceLink(NetSessionClient session)
        {
            m_Session = session ?? throw new ArgumentNullException(nameof(session));
            m_Session.VoiceReceived += OnVoice;
        }

        public NetSessionClient Session => m_Session;
        public bool IsOpen => !m_Disposed && m_Session.State == ClientState.Connected && m_Session.Welcomed;
        public byte LocalSlot => m_Session.LocalSlot;
        public event VoiceFrameHandler Received;

        public bool Send(ushort firstSequence, byte[] frames, int offset, int frameBytes, int frameCount)
        {
            if (!IsOpen) return false;
            return m_Session.SendVoice(firstSequence, frames, offset, frameBytes, frameCount);
        }

        void OnVoice(byte slot, ushort sequence, byte[] buffer, int offset, int length) => Received?.Invoke(slot, sequence, buffer, offset, length);

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            m_Session.VoiceReceived -= OnVoice;
        }
    }

    /// <summary>Where each crew member's voice comes from: the helmet (head) of their stand-in, or null while they have none.</summary>
    public interface IVoiceCrew
    {
        Transform Helmet(int slot);
    }

    /// <summary>The crew of a <see cref="NetSession"/>: its RemoteAvatar stand-ins' heads.</summary>
    public sealed class NetSessionVoiceCrew : IVoiceCrew
    {
        readonly NetSession m_Session;

        public NetSessionVoiceCrew(NetSession session) { m_Session = session; }

        public Transform Helmet(int slot)
        {
            if (m_Session == null || slot == m_Session.LocalSlot) return null;
            RemoteAvatar crew = m_Session.Crew(slot);
            // Not before its first pose: until then the suit is hidden and the head stands at the origin.
            return crew != null && crew.head != null && crew.HasPose ? crew.head : null;
        }
    }
}

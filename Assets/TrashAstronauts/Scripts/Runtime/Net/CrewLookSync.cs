using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// Keeps every suit in the tug dressed as its wearer chose, without the session code having to know about suit colours:
    /// once a frame it compares (no allocations, a handful of fields) and only acts on a difference.
    ///   - the local suit and gloves wear the saved colour (restored on launch; re-applied after a session puts this
    ///     player in a new slot and tints them with that slot's colour);
    ///   - in a session, the host / client session is told the saved colour when it does not have it yet (the host's
    ///     SetLocalLook, the client's SendSettingsChanged), so it reaches everyone through the existing settings path;
    ///   - every remote crew member's stand-in wears the colour their session reported (Welcome, RosterChanged).
    /// When NetSession and RemoteAvatar apply the look themselves (see CrewLook and Docs/SUIT_COLOUR_NOTES.md) this finds
    /// nothing to do. Sits on the SUIT LOCKER panel, which is always in the scene.
    /// </summary>
    public sealed class CrewLookSync : MonoBehaviour
    {
        public MissionDirector director;

        PlayerContext m_Local;
        GloveVisual m_LocalLeft, m_LocalRight;

        void Update() => Sync();

        /// <summary>One pass of the comparison above. Update calls it; tests can call it directly.</summary>
        public void Sync()
        {
            if (director == null) director = MissionDirector.Active;
            if (director == null) return;
            byte colour = CrewLook.SavedColour(director);
            int localSlot = CrewLook.LocalSlot(director);

            PlayerContext local = director.LocalPlayer;
            if (local != m_Local)
            {
                m_Local = local;
                m_LocalLeft = local != null ? CrewLook.GloveOf(local.leftHand) : null;
                m_LocalRight = local != null ? CrewLook.GloveOf(local.rightHand) : null;
            }
            if (local != null && !CrewLook.HasLook(local.avatar, m_LocalLeft, m_LocalRight, localSlot, colour))
                CrewLook.ApplyLook(local.avatar, m_LocalLeft, m_LocalRight, localSlot, colour);

            if (!(director.Net is NetSession net)) return;
            NetSessionHost host = net.HostSession;
            NetSessionClient client = net.ClientSession;
            if (host != null)
            {
                if (host.SuitColour(NetProtocol.HostSlot) != colour) host.SetLocalLook(host.Handedness(NetProtocol.HostSlot), colour);
            }
            else if (client != null && client.State == ClientState.Connected && client.Welcomed)
            {
                if (client.LocalSuitColour != colour) client.SendSettingsChanged(client.LocalHandedness, colour);
            }

            for (int slot = 0; slot < NetProtocol.MaxPlayers; slot++)
            {
                if (slot == net.LocalSlot) continue;
                RemoteAvatar crew = net.Crew(slot);
                if (crew == null) continue;
                int theirs = host != null ? host.SuitColour(slot) : client != null ? client.SuitColour(slot) : SuitLook.Default;
                if (!CrewLook.HasLook(crew.suit, crew.leftGlove, crew.rightGlove, slot, theirs)) CrewLook.ApplyLook(crew, slot, theirs);
            }
        }
    }
}

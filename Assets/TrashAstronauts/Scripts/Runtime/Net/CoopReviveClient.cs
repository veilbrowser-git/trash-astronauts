using System;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>
    /// Co-op revive on a client: the host's word applied to this scene, and this player's own hand on a crew mate's
    /// beacon reported to the host. Nothing here decides anything. Owned by <see cref="CoopClientApply"/>; plain C#;
    /// ticked by the director every frame.
    /// </summary>
    public sealed class CoopReviveClient
    {
        readonly MissionDirector m_Director;
        byte m_SentHold = NetProtocol.NoSlot;

        /// <summary>Where ReviveHold goes (the session; null: nowhere, as in a test with no session).</summary>
        public ICoopReviveRequests Requests { get; set; }
        /// <summary>The downed crew mate this player's hand is on, as last told to the host (255 = none).</summary>
        public byte SentHold => m_SentHold;
        public int HoldMessages { get; private set; }

        public CoopReviveClient(MissionDirector director)
        {
            m_Director = director ?? throw new ArgumentNullException(nameof(director));
            director.ReviveClient = this;
        }

        PlayerContext Find(byte slot) => slot < NetProtocol.MaxPlayers ? m_Director.FindPlayer(new PlayerId(slot)) : null;

        /// <summary>
        /// IClientWorld.OnPlayerDowned. While a jumpscare is still playing here it waits for it (the suit must not slump
        /// under the creature mid-bite, and the victim's own view lifts only once the scare is over).
        /// </summary>
        public bool ApplyPlayerDowned(byte slot, NetVec3 position)
        {
            var player = Find(slot);
            if (player == null || m_Director.Phase != MissionPhase.Active) return false;
            Vector3 at = CoopConvert.ToUnity(position);
            var creature = m_Director.ActiveEnemy;
            if (creature != null && creature.HoldAfterScare(() => Down(player, at))) return true;
            Down(player, at);
            return true;
        }

        void Down(PlayerContext player, Vector3 at)
        {
            if (player == null || m_Director.Phase != MissionPhase.Active) return;
            // This player's own catch is settled without ending the run: stop waiting for a RunEnded.
            var creature = m_Director.ActiveEnemy;
            if (!player.IsRemote && creature != null) creature.CatchSettledHere();
            player.SetDowned(true, at);
        }

        /// <summary>IClientWorld.OnPlayerRevived.</summary>
        public bool ApplyPlayerRevived(byte slot, byte by)
        {
            var player = Find(slot);
            if (player == null || !player.IsDowned) return false;
            Vector3 at = player.DownedPosition;
            player.SetDowned(false, at);
            CoopReviveHost.PlayRevived(player, at);
            return true;
        }

        /// <summary>IClientWorld.OnReviveProgress.</summary>
        public bool ApplyReviveProgress(byte slot, float progress, byte rescuerSlot)
        {
            var player = Find(slot);
            if (player == null || !player.IsDowned) return false;
            player.ReviveProgress = Mathf.Clamp01(progress);
            player.ReviveRescuer = CoopClientApply.Who(rescuerSlot);
            return true;
        }

        /// <summary>The downed crew in a WorldState: whoever it says is down is shown down where it says; nobody else is.</summary>
        public void ApplyWorldState(in WorldStateMsg state)
        {
            if (m_Director.Phase != MissionPhase.Active) return;
            for (byte slot = 0; slot < NetProtocol.MaxPlayers; slot++)
            {
                var player = Find(slot);
                if (player == null) continue;
                if (state.IsDowned(slot)) player.SetDowned(true, CoopConvert.ToUnity(state.DownedAt(slot)));
                else if (player.IsDowned) player.SetDowned(false, player.DownedPosition);
            }
        }

        /// <summary>A crew member left: if this player's hand was on them, that hold is over.</summary>
        public void PlayerLeft(byte slot)
        {
            if (m_SentHold == slot) m_SentHold = NetProtocol.NoSlot;
        }

        /// <summary>One frame: tell the host when this player's gripping hand lands on, or leaves, a downed crew mate's beacon.</summary>
        public void Tick()
        {
            var target = CoopRevive.HeldTarget(m_Director, m_Director.LocalPlayer);
            byte slot = target != null && target.Id.Value >= 0 && target.Id.Value < NetProtocol.MaxPlayers ? (byte)target.Id.Value : NetProtocol.NoSlot;
            if (slot == m_SentHold) return;
            if (m_SentHold != NetProtocol.NoSlot) Send(m_SentHold, false);
            if (slot != NetProtocol.NoSlot) Send(slot, true);
            m_SentHold = slot;
        }

        void Send(byte slot, bool on)
        {
            HoldMessages++;
            if (Requests != null) Requests.ReviveHold(slot, on);
        }
    }
}

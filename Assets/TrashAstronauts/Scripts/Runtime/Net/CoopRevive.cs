using System.Text;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using UnityEngine;

namespace TrashAstronauts.Net
{
    /// <summary>What a client tells the host about reviving (the session forwards it to NetSessionClient.SendReviveHold).</summary>
    public interface ICoopReviveRequests
    {
        /// <summary>This player's hand closed on (on) or let go of (off) the beacon of the downed crew member in <paramref name="slot"/>.</summary>
        void ReviveHold(byte slot, bool on);
    }

    /// <summary>
    /// Co-op revive helpers every headset uses the same way: where a downed crew mate's beacon is, whether this player's
    /// hand is on it, and the wrist lines.
    /// </summary>
    public static class CoopRevive
    {
        /// <summary>Where the revive beacon of a downed player is: on their suit's chest (standing or lying), or just below where they were caught.</summary>
        public static Vector3 BeaconOf(PlayerContext player)
        {
            if (player == null) return Vector3.zero;
            var avatar = player.GetComponent<RemoteAvatar>();
            if (avatar != null && avatar.ShownDowned) return avatar.BeaconPosition;
            return player.DownedPosition + Vector3.down * 0.45f;
        }

        /// <summary>The palm of a hand: where its grab interactor is (the glove centre).</summary>
        public static Vector3 Palm(HandRig hand) => hand.interactor != null ? hand.interactor.transform.position : hand.transform.position;

        static bool HandOn(HandRig hand, Vector3 beacon)
        {
            return hand != null && hand.Grip >= ReviveRules.GripToHold
                && (Palm(hand) - beacon).sqrMagnitude <= ReviveRules.HandReach * ReviveRules.HandReach;
        }

        /// <summary>
        /// The downed crew mate whose beacon <paramref name="local"/> has a gripping hand on right now (the nearest if two),
        /// or null. A downed or away player revives nobody.
        /// </summary>
        public static PlayerContext HeldTarget(MissionDirector director, PlayerContext local)
        {
            if (director == null || local == null || local.IsDowned || local.IsAway || director.Phase != MissionPhase.Active) return null;
            PlayerContext best = null;
            float bestDistance = float.MaxValue;
            var players = director.Players;
            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player == null || player == local || !player.IsDowned) continue;
                Vector3 beacon = BeaconOf(player);
                if (!HandOn(local.leftHand, beacon) && !HandOn(local.rightHand, beacon)) continue;
                float d = (player.DownedPosition - local.HeadPosition).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = player; }
            }
            return best;
        }

        /// <summary>A ten-step bar, "[#####-----]".</summary>
        public static string Bar(float progress)
        {
            int filled = Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(progress) * 10f + 0.001f), 0, 10);
            return "[" + new string('#', filled) + new string('-', 10 - filled) + "]";
        }

        /// <summary>
        /// The revive lines on <paramref name="local"/>'s wrist, or "" when nobody is down. Downed: "DOWNED - CREW MUST
        /// REVIVE YOU" with the ring as a bar. Up: one line per downed crew mate, "CREW 2 DOWN 12 m", with the bar while
        /// someone is reviving them. Crew numbers are slot + 1, as on the CO-OP CREW panel.
        /// </summary>
        public static string WristLines(MissionDirector director, PlayerContext local)
        {
            if (director == null || local == null || director.Net == null || director.Phase != MissionPhase.Active) return "";
            if (local.IsDowned)
            {
                return "DOWNED - CREW MUST REVIVE YOU\nREVIVE " + Bar(local.ReviveProgress)
                    + (local.ReviveProgress > 0f ? " " + Mathf.FloorToInt(local.ReviveProgress * 100f) + "%" : "");
            }
            StringBuilder text = null;
            var players = director.Players;
            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player == null || player == local || !player.IsDowned) continue;
                if (text == null) text = new StringBuilder();
                else text.Append('\n');
                int metres = Mathf.RoundToInt(Vector3.Distance(local.HeadPosition, player.DownedPosition));
                text.Append("CREW ").Append(player.Id.Value + 1).Append(" DOWN ").Append(metres).Append(" m");
                if (player.ReviveProgress > 0f) text.Append(' ').Append(Bar(player.ReviveProgress));
            }
            return text != null ? text.ToString() : "";
        }
    }
}

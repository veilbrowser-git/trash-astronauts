using TrashAstronauts.Core;
using TrashAstronauts.Player;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// Keyboard shortcuts for iterating without a headset. Active only in the editor and in
    /// development builds; in release builds this component does nothing.
    ///
    /// F1 start contract   F2 extract            F3 next recovery anchor   F4 deposit nearest salvage
    /// F5 drain 60s oxygen F6 loud noise at head F7 wake drone now         F8 +500 credits
    /// F9 erase save       F10 toggle hazard pause   F11 blackout on/off       F12 sedate the creature
    /// </summary>
    public sealed class DevHotkeys : MonoBehaviour
    {
        public MissionDirector director;
        int m_AnchorIndex;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || director == null) return;

            if (keyboard.f1Key.wasPressedThisFrame) director.StartMission(PlayerId.Local);
            if (keyboard.f2Key.wasPressedThisFrame) director.Extract(PlayerId.Local);
            if (keyboard.f3Key.wasPressedThisFrame) NextAnchor();
            if (keyboard.f4Key.wasPressedThisFrame) DepositNearest();
            if (keyboard.f5Key.wasPressedThisFrame) director.TickMission(60f, false);
            if (keyboard.f6Key.wasPressedThisFrame && director.Players.Count > 0 && director.noise != null)
                director.noise.Emit(new NoiseEvent(director.Players[0].HeadPosition, 1f, NoiseKind.Impact, PlayerId.Local));
            if (keyboard.f7Key.wasPressedThisFrame && director.enemies != null)
            {
                foreach (var enemy in director.enemies)
                {
                    if (enemy != null && director.Phase == MissionPhase.Active) enemy.WakeNow();
                }
            }
            if (keyboard.f8Key.wasPressedThisFrame)
            {
                director.Profile.Data.totalCredits += 500;
                director.Profile.NotifyChangedAndSave();
            }
            if (keyboard.f9Key.wasPressedThisFrame)
            {
                director.Profile.ResetAll();
                director.ApplyAndSaveSettings();
            }
            if (keyboard.f10Key.wasPressedThisFrame)
                director.SetHazardPause(HazardPauseReason.Scripted, !director.HazardsPaused);
            if (keyboard.f11Key.wasPressedThisFrame)
            {
                var blackout = director.GetComponent<BlackoutController>();
                if (blackout != null)
                {
                    if (blackout.IsBlackout) blackout.EndBlackout(false);
                    else blackout.TriggerNow(15f);
                }
            }
            if (keyboard.f12Key.wasPressedThisFrame && director.enemies != null)
            {
                foreach (var enemy in director.enemies)
                {
                    if (enemy != null) enemy.Tranquilize(director.Config.tranqSleepSeconds);
                }
            }
        }

        void NextAnchor()
        {
            var anchors = RecoveryAnchor.All;
            if (anchors.Count == 0 || director.Players.Count == 0) return;
            m_AnchorIndex = (m_AnchorIndex + 1) % anchors.Count;
            var locomotion = director.Players[0].locomotion;
            if (locomotion != null) locomotion.TeleportTo(anchors[m_AnchorIndex].transform.position);
        }

        void DepositNearest()
        {
            if (director.locker == null || director.locker.zone == null || director.Players.Count == 0) return;
            Vector3 from = director.Players[0].HeadPosition;
            SalvageItem best = null;
            float bestSqr = float.MaxValue;
            foreach (var item in director.Items)
            {
                if (item == null || item.IsSecured || item.IsMounted) continue;
                float sqr = (item.transform.position - from).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = item;
                }
            }
            if (best != null) best.RecoverTo(director.locker.zone.position);
        }
#endif
    }
}

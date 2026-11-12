using UnityEngine;
using UnityEngine.InputSystem;

namespace TrashAstronauts.Mission
{
    /// <summary>
    /// Pauses mission hazards (oxygen drain and the drone) whenever the player cannot fairly respond:
    /// the app lost focus or was paused (headset taken off, system menu open) or head tracking dropped out.
    /// In a co-op session only the host's pause freezes hazards; on a client the same signals mark the
    /// local player Away (not counted for oxygen or extraction) and tell the host.
    /// </summary>
    public sealed class HazardPauseMonitor : MonoBehaviour
    {
        public MissionDirector director;
        [Tooltip("Head 'is tracked' action. Ignored when no headset is connected (editor without simulator).")]
        public InputActionReference headIsTracked;
        public float trackingLossGraceSeconds = 0.75f;

        float m_UntrackedTime;
        bool m_EverTracked;

        void OnApplicationFocus(bool hasFocus)
        {
            // In the editor, clicking another window should not freeze the game during iteration.
            if (Application.isEditor) return;
            if (director != null) Pause(HazardPauseReason.ApplicationFocus, !hasFocus);
        }

        void OnApplicationPause(bool paused)
        {
            if (director != null) Pause(HazardPauseReason.ApplicationFocus, paused);
        }

        void Pause(HazardPauseReason reason, bool paused)
        {
            if (director.Net != null && director.Net.IsClient) director.SetLocalAway(reason, paused);
            else director.SetHazardPause(reason, paused);
        }

        void Update()
        {
            if (director == null || headIsTracked == null || headIsTracked.action == null) return;
            var action = headIsTracked.action;
            if (!action.enabled || action.controls.Count == 0)
            {
                // No headset bound: nothing to lose.
                Pause(HazardPauseReason.TrackingLost, false);
                return;
            }

            bool tracked = action.IsPressed();
            if (tracked)
            {
                m_EverTracked = true;
                m_UntrackedTime = 0f;
                Pause(HazardPauseReason.TrackingLost, false);
            }
            else if (m_EverTracked)
            {
                m_UntrackedTime += Time.unscaledDeltaTime;
                if (m_UntrackedTime >= trackingLossGraceSeconds)
                    Pause(HazardPauseReason.TrackingLost, true);
            }
        }
    }
}

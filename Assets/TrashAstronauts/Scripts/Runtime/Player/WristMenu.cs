using UnityEngine;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// Pop-up suit menu. Press the menu button (or B / Y) on either controller to summon it in front of
    /// you; press again to dismiss. It stays fixed in the world while open so it is easy to press, and
    /// closes itself if you move away from it.
    /// </summary>
    public sealed class WristMenu : MonoBehaviour
    {
        public PlayerContext player;
        public GameObject panel;
        public float distance = 0.55f;
        public float drop = 0.28f;
        public float autoCloseDistance = 1.6f;

        public bool IsOpen => panel != null && panel.activeSelf;

        void Start()
        {
            if (panel != null) panel.SetActive(false);
        }

        void Update()
        {
            if (player == null || panel == null) return;

            bool pressed = (player.leftHand != null && player.leftHand.MenuPressedThisFrame)
                || (player.rightHand != null && player.rightHand.MenuPressedThisFrame);
            if (pressed) Toggle();

            if (IsOpen && Vector3.Distance(panel.transform.position, player.HeadPosition) > autoCloseDistance)
                panel.SetActive(false);
        }

        public void Toggle()
        {
            if (IsOpen)
            {
                panel.SetActive(false);
                return;
            }
            Open();
        }

        public void Open()
        {
            if (panel == null || player == null || player.head == null) return;
            Vector3 forward = Vector3.ProjectOnPlane(player.head.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 position = player.head.position + forward * distance + Vector3.down * drop;
            // Text and buttons are read looking along the panel's +Z, so +Z points away from the player.
            panel.transform.SetPositionAndRotation(position, Quaternion.LookRotation(forward, Vector3.up));
            panel.SetActive(true);
        }
    }
}

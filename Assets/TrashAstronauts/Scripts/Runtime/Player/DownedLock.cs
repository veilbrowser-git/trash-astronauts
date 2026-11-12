using System.Collections.Generic;
using UnityEngine;

namespace TrashAstronauts.Player
{
    /// <summary>
    /// Co-op revive, on the downed player's own headset. While on: no thruster, swimming, wall grabs or teleport (the
    /// zero-g locomotion and the teleport aimers are switched off, so the body stays exactly where it was caught, in
    /// zero-g as on the cave floor), no grabbing or tools (both grab interactors and reach assists are off, which lets go
    /// of anything in the hands where it is), no buttons (the finger and pointer press nothing), no shoving (bumpers off).
    /// The head is never moved: the player looks around freely, talks and hears. The view behind the visor is dimmed and
    /// tinted dark red toward the edges (<see cref="VignetteName"/>, steady, no pulsing) and the visor stays fully
    /// cracked. Everything switched off is switched back on exactly as it was when the lock lifts.
    /// Added on demand by <see cref="PlayerContext.SetDowned"/>; nothing in single-player ever turns it on.
    /// </summary>
    public sealed class DownedLock : MonoBehaviour
    {
        /// <summary>The quad in front of the eyes that dims the downed view (built by the Builder under the camera).</summary>
        public const string VignetteName = "Downed Vignette";
        /// <summary>How long the darkness of the jumpscare takes to lift into the downed view.</summary>
        public const float LiftSeconds = 1.2f;

        readonly List<Behaviour> m_Switched = new List<Behaviour>();
        PlayerContext m_Player;
        Renderer m_Vignette;
        bool m_LookedForVignette;

        /// <summary>True while the player is locked down.</summary>
        public bool On { get; private set; }
        /// <summary>
        /// The visor was kept cracked by a downing this contract (it stays cracked after a revive, as a scar of the
        /// contract). MissionDirector.ClearCrewDowned clears it when the contract is over.
        /// </summary>
        public bool VisorKeptCracked { get; set; }
        /// <summary>The dimmed view is showing.</summary>
        public bool VignetteShown => m_Vignette != null && m_Vignette.enabled;

        public static DownedLock For(PlayerContext player)
        {
            var lockComponent = player.GetComponent<DownedLock>();
            if (lockComponent == null) lockComponent = player.gameObject.AddComponent<DownedLock>();
            lockComponent.m_Player = player;
            return lockComponent;
        }

        Renderer Vignette
        {
            get
            {
                if (!m_LookedForVignette && m_Player != null && m_Player.head != null)
                {
                    m_LookedForVignette = true;
                    var t = m_Player.head.Find(VignetteName);
                    if (t != null) m_Vignette = t.GetComponent<Renderer>();
                }
                return m_Vignette;
            }
        }

        public void Set(bool on)
        {
            if (m_Player == null) m_Player = GetComponent<PlayerContext>();
            if (on == On || m_Player == null) return;
            On = on;
            if (on) Engage();
            else Release();
        }

        void Engage()
        {
            var locomotion = m_Player.locomotion;
            if (locomotion != null && locomotion.zeroG != null)
            {
                locomotion.zeroG.Stop();
                Switch(locomotion.zeroG);
            }
            foreach (var aimer in m_Player.GetComponentsInChildren<TeleportAimer>(true)) Switch(aimer);
            foreach (var buttons in m_Player.GetComponentsInChildren<HandButtonInteractor>(true)) Switch(buttons);
            foreach (var reach in m_Player.GetComponentsInChildren<ReachAssist>(true)) Switch(reach);
            if (m_Player.leftHand != null) Switch(m_Player.leftHand.interactor);
            if (m_Player.rightHand != null) Switch(m_Player.rightHand.interactor);
            if (m_Player.bumpers != null) foreach (var bumper in m_Player.bumpers) Switch(bumper);

            // The visor the jumpscare cracked stays cracked (the jumpscare clears it as it ends; put every crack back).
            var visor = m_Player.visor;
            if (visor != null && visor.cracks != null)
            {
                for (int i = visor.Stage; i < visor.cracks.Length; i++) visor.Crack(0f);
                VisorKeptCracked = true;
            }
            var vignette = Vignette;
            if (vignette != null) vignette.enabled = true;
            // The one control left: GIVE UP on the wrist (a held fingertip, not a button), for a crew that wandered off.
            TrashAstronauts.Net.DownedGiveUp.For(m_Player);
            // Out of the jumpscare's black, slowly, into the dim view.
            if (m_Player.fader != null && m_Player.fader.Alpha > 0f) m_Player.fader.FadeIn(LiftSeconds);
        }

        void Release()
        {
            for (int i = 0; i < m_Switched.Count; i++)
            {
                if (m_Switched[i] != null) m_Switched[i].enabled = true;
            }
            m_Switched.Clear();
            var vignette = Vignette;
            if (vignette != null) vignette.enabled = false;
        }

        void Switch(Behaviour behaviour)
        {
            if (behaviour == null || !behaviour.enabled) return;
            behaviour.enabled = false;
            m_Switched.Add(behaviour);
        }
    }
}

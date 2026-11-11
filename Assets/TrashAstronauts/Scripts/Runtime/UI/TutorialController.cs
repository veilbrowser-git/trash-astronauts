using TMPro;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using UnityEngine;

namespace TrashAstronauts.UI
{
    /// <summary>
    /// A short, safe tutorial in the tug. It runs before any contract, so oxygen is not draining and the
    /// drone is powered down. It is a checklist, not a gate: the player can start a contract at any time.
    /// </summary>
    public sealed class TutorialController : MonoBehaviour
    {
        public MissionDirector director;
        public CollectionLocker locker;
        public Cuttable practicePanel;
        public TMP_Text text;

        PlayerLocomotion m_Locomotion;
        bool m_Moved;
        bool m_Deposited;
        bool m_DrewTool;
        bool m_Cut;

        public bool AllStepsDone => m_Moved && m_Deposited && m_DrewTool && m_Cut;

        void OnEnable()
        {
            if (director == null) director = MissionDirector.Active;
            if (locker != null) locker.TrainingItemDeposited += OnTrainingDeposited;
            if (practicePanel != null) practicePanel.Opened += OnPracticeCut;
        }

        void OnDisable()
        {
            if (locker != null) locker.TrainingItemDeposited -= OnTrainingDeposited;
            if (practicePanel != null) practicePanel.Opened -= OnPracticeCut;
            if (m_Locomotion != null) m_Locomotion.Moved -= OnMoved;
        }

        void OnTrainingDeposited() => m_Deposited = true;
        void OnPracticeCut(Cuttable c) => m_Cut = true;
        void OnMoved() => m_Moved = true;

        void Update()
        {
            if (director == null || text == null) return;

            if (m_Locomotion == null && director.Players.Count > 0 && director.Players[0].locomotion != null)
            {
                m_Locomotion = director.Players[0].locomotion;
                m_Locomotion.Moved += OnMoved;
            }
            if (!m_DrewTool && director.Players.Count > 0 && director.Players[0].IsHoldingAnyTool) m_DrewTool = true;

            string content;
            if (director.Phase == MissionPhase.Hub)
            {
                content = "ORIENTATION (safe: no oxygen drain, no drone)\n\n"
                    + Step(m_Moved, "MOVE: grip a yellow rail and pull, or push a\n     thumbstick forward and release to teleport.\n     Thumbstick left/right turns.")
                    + Step(m_DrewTool, "TOOLS: grip at your chest (flashlight) or hips\n     (scanner, cutter). Trigger uses the tool.")
                    + Step(m_Deposited, "SALVAGE: grab the practice scrap and let go of\n     it inside the collection locker.")
                    + Step(m_Cut, "CUT: hold the cutter flame on each orange weld\n     of the practice plate. Watch the heat.")
                    + "\nSuit menu: B or Y button. Ready? Press START CONTRACT.";
            }
            else
            {
                content = "REMINDERS\n\n"
                    + "Grip = grab.  Trigger = use tool / press.\n"
                    + "Rails or thumbstick-forward teleport to move.\n"
                    + "Noise attracts the drone: cutting, slammed\n"
                    + "doors, thrown junk. Throw junk to distract it.\n"
                    + "Break line of sight to lose it. It cannot\n"
                    + "enter the tug, the airlock or the service duct.\n"
                    + "Suit menu (unstuck, recall tools): B or Y.";
            }
            if (text.text != content) text.text = content;
        }

        static string Step(bool done, string description)
        {
            return (done ? "[X] " : "[  ] ") + description + "\n";
        }
    }
}

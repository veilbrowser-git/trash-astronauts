using System;
using TMPro;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.UI
{
    public enum TutorialStep
    {
        Welcome = 0,
        GrabAndPushOff = 1,
        Swim = 2,
        Stop = 3,
        Tools = 4,
        Salvage = 5,
        Cut = 6,
        Done = 7,
    }

    /// <summary>
    /// A guided tutorial that runs in the tug before any contract. It teaches one thing at a time and
    /// only moves on when the player has actually done it: push off a wall, swim, stop, draw a tool,
    /// bank salvage, cut a weld. Oxygen does not drain and the creature is dormant throughout.
    /// The current instruction is shown on the wall board and on a small panel that floats with the
    /// player. Any step, or the whole thing, can be skipped, and it can be replayed later.
    /// </summary>
    public sealed class TutorialController : MonoBehaviour
    {
        public MissionDirector director;
        public CollectionLocker locker;
        public Cuttable practicePanel;
        public TMP_Text boardText;
        public TutorialPrompt prompt;
        public PushButton skipStepButton;
        public PushButton skipAllButton;
        [Tooltip("Prefab and location used to bring the practice scrap back when the tutorial is replayed.")]
        public GameObject practiceScrapPrefab;
        public Transform practiceScrapSpawn;
        public float welcomeSeconds = 5f;

        PlayerContext m_Player;
        ZeroGLocomotion m_ZeroG;
        float m_StepTime;
        float m_SwimAtStepStart;
        bool m_Started;

        public TutorialStep Step { get; private set; } = TutorialStep.Welcome;
        public bool IsComplete => Step == TutorialStep.Done;
        public event Action<TutorialStep> StepChanged;

        static readonly string[] k_Titles =
        {
            "WELCOME ABOARD",
            "1. GRAB AND PUSH OFF",
            "2. SWIM",
            "3. STOP",
            "4. YOUR TOOLS",
            "5. BANK SALVAGE",
            "6. CUT",
            "READY",
        };

        static readonly string[] k_Instructions =
        {
            "You are weightless. Nothing here can hurt you:\nno oxygen drain, and nothing is awake.\nLook at your hands, then follow these steps.",
            "Reach out to a wall or a yellow rail.\nHOLD GRIP to grab it, pull yourself along,\nthen LET GO while moving to glide away.",
            "Float clear of the walls and LOOK where you want\nto go. Sweep your arms back quickly, like swimming.\nNo button needed. (Hold GRIP to paddle backward.)",
            "Drifting? GRAB any surface to stop dead, or just\nput a hand on a wall: hands are solid here.\nThumbstick FORWARD teleports. Left / right turns.",
            "Grip at your CHEST: flashlight, and the tranq\nlauncher beside it. HIPS: scanner and cutter.\nTRIGGER uses a tool. Let go and it returns.",
            "Grab the yellow PRACTICE SCRAP floating nearby.\nCarry it to the collection locker and\nLET GO of it inside the open hopper.",
            "Draw the PLASMA CUTTER from your hip.\nHold TRIGGER and keep the flame on each orange\nweld of the PRACTICE PLATE. Watch the heat gauge.",
            "Orientation complete. The COMPANY STORE on the\nwall sells tranq darts and tool upgrades.\nPress START CONTRACT when you are ready.",
        };

        void OnEnable()
        {
            if (director == null) director = MissionDirector.Active;
            if (locker != null) locker.TrainingItemDeposited += OnTrainingDeposited;
            if (practicePanel != null) practicePanel.Opened += OnPracticeCut;
            if (skipStepButton != null) skipStepButton.Pressed += OnSkipStep;
            if (skipAllButton != null) skipAllButton.Pressed += OnSkipAll;
            if (director != null) director.MissionStarted += OnMissionStarted;
        }

        void OnDisable()
        {
            if (locker != null) locker.TrainingItemDeposited -= OnTrainingDeposited;
            if (practicePanel != null) practicePanel.Opened -= OnPracticeCut;
            if (skipStepButton != null) skipStepButton.Pressed -= OnSkipStep;
            if (skipAllButton != null) skipAllButton.Pressed -= OnSkipAll;
            if (director != null) director.MissionStarted -= OnMissionStarted;
            UnhookPlayer();
        }

        void Start()
        {
            // Returning players are not forced through it again; they can replay it from the board.
            bool seen = director != null && director.Profile != null && director.Profile.Data.tutorialCompleted;
            SetStep(seen ? TutorialStep.Done : TutorialStep.Welcome, false);
            m_Started = true;
        }

        void HookPlayer()
        {
            if (m_Player != null || director == null || director.Players.Count == 0) return;
            m_Player = director.Players[0];
            m_ZeroG = m_Player.locomotion != null ? m_Player.locomotion.zeroG : null;
            if (m_ZeroG != null)
            {
                m_ZeroG.PushedOff += OnPushedOff;
                m_ZeroG.Grabbed += OnGrabbed;
                m_SwimAtStepStart = m_ZeroG.SwimSpeedGained;
            }
            if (m_Player.locomotion != null) m_Player.locomotion.teleportation.locomotionEnded += OnTeleported;
        }

        void UnhookPlayer()
        {
            if (m_ZeroG != null)
            {
                m_ZeroG.PushedOff -= OnPushedOff;
                m_ZeroG.Grabbed -= OnGrabbed;
            }
            if (m_Player != null && m_Player.locomotion != null && m_Player.locomotion.teleportation != null)
                m_Player.locomotion.teleportation.locomotionEnded -= OnTeleported;
            m_Player = null;
            m_ZeroG = null;
        }

        // ------------------------------------------------------------------ step completion

        void OnPushedOff(float speed)
        {
            if (Step == TutorialStep.GrabAndPushOff && speed >= 0.25f) Advance();
        }

        void OnGrabbed(float speedBefore)
        {
            if (Step == TutorialStep.Stop && speedBefore >= 0.15f) Advance();
        }

        void OnTeleported(UnityEngine.XR.Interaction.Toolkit.Locomotion.LocomotionProvider provider)
        {
            if (Step == TutorialStep.Stop) Advance();
        }

        void OnTrainingDeposited()
        {
            if (Step == TutorialStep.Salvage) Advance();
        }

        void OnPracticeCut(Cuttable cuttable)
        {
            if (Step == TutorialStep.Cut) Advance();
        }

        void OnMissionStarted()
        {
            // Starting a contract ends the tutorial for good.
            if (Step != TutorialStep.Done) SetStep(TutorialStep.Done, true);
        }

        void OnSkipStep(PushButton button, HandRig hand)
        {
            if (Step == TutorialStep.Done) Replay();
            else Advance();
        }

        void OnSkipAll(PushButton button, HandRig hand) => SkipAll();

        void Update()
        {
            if (director == null) return;
            HookPlayer();
            m_StepTime += Time.deltaTime;

            switch (Step)
            {
                case TutorialStep.Welcome:
                    if (m_StepTime >= welcomeSeconds) Advance();
                    break;
                case TutorialStep.Swim:
                    if (m_ZeroG != null && m_ZeroG.SwimSpeedGained - m_SwimAtStepStart >= 0.8f) Advance();
                    break;
                case TutorialStep.Tools:
                    if (m_Player != null && m_Player.IsHoldingAnyTool) Advance();
                    break;
            }

            // The floating prompt is only shown while the tutorial is actually in progress in the hub.
            bool active = director.Phase == MissionPhase.Hub && Step != TutorialStep.Done;
            if (prompt != null) prompt.SetVisible(active);
        }

        // ------------------------------------------------------------------ flow

        /// <summary>Moves to the next step. Public so the board buttons, hotkeys and tests can drive it.</summary>
        public void Advance()
        {
            if (Step == TutorialStep.Done) return;
            SetStep(Step + 1, true);
        }

        public void SkipAll()
        {
            if (Step != TutorialStep.Done) SetStep(TutorialStep.Done, true);
        }

        /// <summary>Restarts the tutorial from the beginning. Only available outside a contract.</summary>
        public bool Replay()
        {
            if (director != null && director.Phase == MissionPhase.Active) return false;
            if (practicePanel != null) practicePanel.ResetForRun();
            if (practiceScrapPrefab != null && practiceScrapSpawn != null && director != null && !PracticeScrapAvailable())
                Instantiate(practiceScrapPrefab, practiceScrapSpawn.position, practiceScrapSpawn.rotation, director.salvageRoot);
            SetStep(TutorialStep.Welcome, true);
            return true;
        }

        bool PracticeScrapAvailable()
        {
            if (director.salvageRoot == null) return false;
            foreach (var item in director.salvageRoot.GetComponentsInChildren<SalvageItem>())
            {
                if (item.isTraining && !item.IsSecured) return true;
            }
            return false;
        }

        void SetStep(TutorialStep step, bool feedback)
        {
            Step = step;
            m_StepTime = 0f;
            if (m_ZeroG != null) m_SwimAtStepStart = m_ZeroG.SwimSpeedGained;

            if (step == TutorialStep.Done && m_Started && director != null && director.Profile != null
                && !director.Profile.Data.tutorialCompleted)
            {
                director.Profile.Data.tutorialCompleted = true;
                director.Profile.NotifyChangedAndSave();
            }

            if (feedback)
            {
                if (m_Player != null) m_Player.PulseBoth(0.3f, 0.08f);
                if (Audio.Sfx.Library != null) Audio.Sfx.PlayUi(Audio.Sfx.Library.scannerHit, 0.5f, step == TutorialStep.Done ? 1.3f : 1f);
            }
            Refresh();
            StepChanged?.Invoke(step);
        }

        void Refresh()
        {
            int index = (int)Step;
            string title = k_Titles[index];
            string instruction = k_Instructions[index];

            if (boardText != null)
            {
                string header = Step == TutorialStep.Done
                    ? "ORIENTATION COMPLETE"
                    : "ORIENTATION  (safe: no oxygen drain, nothing awake)";
                string progress = "";
                for (int i = 1; i <= 6; i++) progress += i < index || Step == TutorialStep.Done ? "[X] " : i == index ? "[>] " : "[  ] ";
                boardText.text = header + "\n" + progress + "\n\n" + title + "\n\n" + instruction
                    + (Step == TutorialStep.Done
                        ? "\n\nIn the wreck: noise attracts the thing that lives there.\nBreak its line of sight to lose it, or put it to sleep with\na tranq dart. It cannot enter the tug, airlock or duct.\nThe power fails sometimes. Follow the floor lights."
                        : "");
            }
            if (prompt != null) prompt.SetText(title, instruction);
            if (skipStepButton != null) skipStepButton.SetLabel(Step == TutorialStep.Done ? "REPLAY TUTORIAL" : "SKIP STEP");
            if (skipAllButton != null)
            {
                skipAllButton.SetLabel("SKIP ALL");
                skipAllButton.SetInteractable(Step != TutorialStep.Done);
            }
        }
    }
}

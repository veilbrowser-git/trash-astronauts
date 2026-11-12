using TMPro;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Player;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.UI
{
    /// <summary>
    /// Comfort and suit settings, built from physical buttons: seated height adjustment, snap-turn angle,
    /// handedness, zero-g drift on/off, unstuck and tool recall. The same component drives the wall console in the tug and
    /// the pop-up wrist menu, and every change is saved immediately.
    /// </summary>
    public sealed class SettingsPanel : MonoBehaviour
    {
        public MissionDirector director;
        public TMP_Text summary;
        public PushButton heightUp;
        public PushButton heightDown;
        public PushButton snapTurn;
        public PushButton handedness;
        public PushButton drift;
        public PushButton voice;
        [Tooltip("VOICE CHAT on/off: co-op crew hear you and you hear them (separate from MIC).")]
        public PushButton voiceChat;
        public PushButton unstuck;
        public PushButton recallTools;
        public PushButton crewShoves;
        public float heightStep = 0.1f;

        void OnEnable()
        {
            if (director == null) director = MissionDirector.Active;
            Hook(heightUp, OnHeightUp, true);
            Hook(heightDown, OnHeightDown, true);
            Hook(snapTurn, OnSnapTurn, true);
            Hook(handedness, OnHandedness, true);
            Hook(drift, OnDrift, true);
            Hook(voice, OnVoice, true);
            Hook(voiceChat, OnVoiceChat, true);
            Hook(unstuck, OnUnstuck, true);
            Hook(recallTools, OnRecall, true);
            Hook(crewShoves, OnCrewShoves, true);
            if (director != null) director.ProfileChanged += Refresh;
            Refresh();
        }

        void OnDisable()
        {
            Hook(heightUp, OnHeightUp, false);
            Hook(heightDown, OnHeightDown, false);
            Hook(snapTurn, OnSnapTurn, false);
            Hook(handedness, OnHandedness, false);
            Hook(drift, OnDrift, false);
            Hook(voice, OnVoice, false);
            Hook(voiceChat, OnVoiceChat, false);
            Hook(unstuck, OnUnstuck, false);
            Hook(recallTools, OnRecall, false);
            Hook(crewShoves, OnCrewShoves, false);
            if (director != null) director.ProfileChanged -= Refresh;
        }

        static void Hook(PushButton button, System.Action<PushButton, HandRig> handler, bool add)
        {
            if (button == null) return;
            if (add) button.Pressed += handler;
            else button.Pressed -= handler;
        }

        void Start() => Refresh();

        SaveData Data => director != null && director.Profile != null ? director.Profile.Data : null;

        PlayerContext PlayerFor(HandRig hand)
        {
            if (hand != null && hand.Player != null) return hand.Player;
            return director != null ? director.LocalPlayer : null;
        }

        void OnHeightUp(PushButton b, HandRig hand) => ChangeHeight(heightStep);
        void OnHeightDown(PushButton b, HandRig hand) => ChangeHeight(-heightStep);

        void ChangeHeight(float delta)
        {
            if (Data == null) return;
            Data.seatedHeightOffset = Mathf.Clamp(Data.seatedHeightOffset + delta, SaveData.MinHeightOffset, SaveData.MaxHeightOffset);
            director.ApplyAndSaveSettings();
        }

        void OnSnapTurn(PushButton b, HandRig hand)
        {
            if (Data == null) return;
            var options = SaveData.SnapTurnOptions;
            int index = 0;
            for (int i = 0; i < options.Length; i++)
            {
                if (Mathf.Approximately(options[i], Data.snapTurnDegrees)) index = i;
            }
            Data.snapTurnDegrees = options[(index + 1) % options.Length];
            director.ApplyAndSaveSettings();
        }

        void OnHandedness(PushButton b, HandRig hand)
        {
            if (Data == null) return;
            Data.Handedness = Data.Handedness == Handedness.Right ? Handedness.Left : Handedness.Right;
            director.ApplyAndSaveSettings();
        }

        void OnDrift(PushButton b, HandRig hand)
        {
            if (Data == null) return;
            Data.zeroGDrift = !Data.zeroGDrift;
            director.ApplyAndSaveSettings();
        }

        void OnVoice(PushButton b, HandRig hand)
        {
            if (Data == null) return;
            Data.voiceDetection = !Data.voiceDetection;
            director.ApplyAndSaveSettings();
        }

        void OnVoiceChat(PushButton b, HandRig hand)
        {
            if (Data == null) return;
            Data.voiceChat = !Data.voiceChat;
            director.ApplyAndSaveSettings();
        }

        void OnUnstuck(PushButton b, HandRig hand)
        {
            var player = PlayerFor(hand);
            if (player != null && player.locomotion != null) player.locomotion.Unstuck();
        }

        void OnRecall(PushButton b, HandRig hand)
        {
            var player = PlayerFor(hand);
            if (player != null) player.RecallTools();
        }

        void OnCrewShoves(PushButton b, HandRig hand) { if (Data == null) return; Data.crewShoves = !Data.crewShoves; director.ApplyAndSaveSettings(); }

        public void Refresh()
        {
            var data = Data;
            if (data == null) return;
            int cm = Mathf.RoundToInt(data.seatedHeightOffset * 100f);
            if (summary != null)
            {
                summary.text = "SUIT SETTINGS\n"
                    + "Height boost: " + (cm >= 0 ? "+" : "") + cm + " cm\n"
                    + "Snap turn: " + Mathf.RoundToInt(data.snapTurnDegrees) + " deg\n"
                    + "Main hand: " + (data.Handedness == Handedness.Left ? "LEFT" : "RIGHT") + "\n"
                    + "Drift: " + (data.zeroGDrift ? "ON (keep gliding)" : "OFF (stop on release)") + "\n"
                    + "Mic: " + (data.voiceDetection ? "ON (it can hear you talk)" : "OFF") + "\n"
                    + "Voice chat: " + (data.voiceChat ? "ON (crew hear you nearby)" : "OFF");
            }
            if (heightUp != null) heightUp.SetLabel("HEIGHT +");
            if (heightDown != null) heightDown.SetLabel("HEIGHT -");
            if (snapTurn != null) snapTurn.SetLabel("TURN " + Mathf.RoundToInt(data.snapTurnDegrees));
            if (handedness != null) handedness.SetLabel(data.Handedness == Handedness.Left ? "HAND: LEFT" : "HAND: RIGHT");
            if (drift != null) drift.SetLabel(data.zeroGDrift ? "DRIFT: ON" : "DRIFT: OFF");
            if (voice != null) voice.SetLabel(data.voiceDetection ? "MIC: ON" : "MIC: OFF");
            if (voiceChat != null) voiceChat.SetLabel(data.voiceChat ? "VOICE CHAT: ON" : "VOICE CHAT: OFF");
            if (unstuck != null) unstuck.SetLabel("UNSTUCK");
            if (recallTools != null) recallTools.SetLabel("RECALL TOOLS");
            if (crewShoves != null) crewShoves.SetLabel(data.crewShoves ? "CREW SHOVES: ON" : "CREW SHOVES: OFF");
        }
    }
}

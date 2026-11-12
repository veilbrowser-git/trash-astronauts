using TMPro;
using TrashAstronauts.Mission;
using UnityEngine;

namespace TrashAstronauts.UI
{
    /// <summary>A wall screen in the tug's flight deck that shows the saved career: launches, contracts, credits, kit.</summary>
    public sealed class CrewRecordBoard : MonoBehaviour
    {
        public MissionDirector director;
        public TMP_Text body;

        bool m_Hooked;

        void OnEnable() => Hook();
        void Start() { Hook(); Refresh(); }

        void Hook()
        {
            if (m_Hooked) return;
            if (director == null) director = MissionDirector.Active;
            if (director == null) return;
            director.ProfileChanged += Refresh;
            m_Hooked = true;
        }

        void OnDisable()
        {
            if (m_Hooked && director != null) director.ProfileChanged -= Refresh;
            m_Hooked = false;
        }

        public void Refresh()
        {
            if (body == null || director == null || director.Profile == null) return;
            var data = director.Profile.Data;
            body.text = "Launches: " + data.runsStarted + "\n"
                + "Contracts completed: " + data.contractsCompleted + "\n"
                + "Best haul: " + data.bestHaul + " cr\n"
                + "Banked: " + data.totalCredits + " cr\n"
                + "O2 tank: Mk " + (data.oxygenUpgradeLevel + 1) + "\n"
                + "Tranq darts: " + data.tranqDarts
                + (data.scannerUpgrade ? "\nScanner: long range" : "")
                + (data.cutterUpgrade ? "\nCutter: coolant kit" : "");
        }
    }
}

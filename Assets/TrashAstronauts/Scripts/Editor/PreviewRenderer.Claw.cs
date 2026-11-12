using System.IO;
using TrashAstronauts.World;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    public static partial class PreviewRenderer
    {
        /// <summary>Only the salvage claw shots (batch: -executeMethod TrashAstronauts.EditorTools.PreviewRenderer.RenderClawPreviews).</summary>
        public static void RenderClawPreviews()
        {
            EditorSceneManager.OpenScene(Builder.ScenePath, OpenSceneMode.Single);
            Directory.CreateDirectory("Docs/Previews");
            UnityEditor.ShaderUtil.allowAsyncCompilation = false; // the first shot of a run must not catch shaders mid-compile
            ClawShots();
        }

        /// <summary>The salvage claw: the operator's view with the arm reaching for a crate, the arm from outside, the console, and a catch at the hatch.</summary>
        static void ClawShots()
        {
            var station = Object.FindAnyObjectByType<SalvageClawStation>();
            if (station == null) return;
            Transform t = station.transform;
            Vector3 W(float x, float y, float z) => t.TransformPoint(new Vector3(x, y, z));
            station.Init();

            var crate = station.SpawnAt(ClawCargoKind.Crate, new Vector3(0.9f, 1.5f, 6.4f), Vector3.zero);
            var longBox = station.SpawnAt(ClawCargoKind.LongContainer, new Vector3(-3.2f, 3.3f, 10.5f), Vector3.zero);
            var strongbox = station.SpawnAt(ClawCargoKind.Strongbox, new Vector3(4.2f, 0.6f, 9.5f), Vector3.zero);
            var scrap = station.SpawnAt(ClawCargoKind.Scrap, new Vector3(-5.5f, 0.2f, 7.5f), Vector3.zero);
            if (crate != null) crate.transform.localRotation = Quaternion.Euler(12f, 30f, 8f);
            if (longBox != null) longBox.transform.localRotation = Quaternion.Euler(10f, 65f, 20f);
            if (strongbox != null) strongbox.transform.localRotation = Quaternion.Euler(-15f, 20f, 10f);
            if (scrap != null) scrap.transform.localRotation = Quaternion.Euler(20f, 120f, 0f);

            // Reaching for the crate, claw open.
            station.PoseTo(new Vector3(0.75f, 1.6f, 5.3f));
            station.SetClawAmount(0f);
            Shot("60_claw_operator_view", W(0f, 1.62f, -1.45f), W(0.25f, 1.45f, 4f), false, 90f);
            Shot("61_claw_arm_outside_wide", W(7.5f, 5.5f, 13f), W(-0.4f, 1.6f, 2f), false, 70f);
            Shot("62_claw_arm_outside_side", W(-9f, 3.0f, 8.5f), W(0f, 2.2f, 2.5f), false, 70f);
            Shot("63_claw_console", W(0.1f, 1.55f, -1.35f), W(0f, 0.95f, -0.7f), false, 75f);

            // A catch carried to the intake hatch, claw closed.
            Vector3 mouth = station.IntakeLocal;
            station.PoseTo(mouth + new Vector3(-0.6f, 0.2f, 0.4f));
            station.SetClawAmount(1f);
            if (crate != null) crate.transform.position = station.TipWorld;
            Shot("64_claw_feeding_hatch", W(0f, 1.62f, -1.45f), W(0.8f, 1.1f, 3f), false, 90f);

            station.ClearCargo();
            foreach (var c in t.GetComponentsInChildren<SalvageClawCargo>(true))
                if (c.transform.parent == station.cargoRoot) Object.DestroyImmediate(c.gameObject);
            station.SetPose(SalvageClawStation.ParkPose.x, SalvageClawStation.ParkPose.y, SalvageClawStation.ParkPose.z);
            station.SetClawAmount(0f);
        }
    }
}

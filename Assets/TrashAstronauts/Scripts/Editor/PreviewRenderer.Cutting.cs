using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TrashAstronauts.EditorTools
{
    public static partial class PreviewRenderer
    {
        /// <summary>Only the seam-cutting shots (quick to iterate on): -executeMethod ...PreviewRenderer.RenderCutPreviews.</summary>
        public static void RenderCutPreviews()
        {
            EditorSceneManager.OpenScene(Builder.ScenePath, OpenSceneMode.Single);
            System.IO.Directory.CreateDirectory("Docs/Previews");
            CutSeamShots();
        }

        /// <summary>
        /// A locker panel half cut open: the flame of a crew member's cutter part way along the seam, the molten line
        /// behind it white at the flame, orange, dull red and dark further back, a kerf where it has cooled, the uncut
        /// rest of the seam a dim amber bead, sparks and droplets spraying off the cut, and the cut side of the plate
        /// standing a little off the face. A close-up, and the same from a metre away. The scene is not saved.
        /// </summary>
        static void CutSeamShots()
        {
            var remotePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Builder.PrefabDir + "/" + Builder.RemoteCrewPrefabName + ".prefab");
            var director = Object.FindFirstObjectByType<MissionDirector>();
            if (remotePrefab == null || director == null || director.maps == null) return;
            Cuttable panel = null;
            foreach (var map in director.maps)
            {
                if (map == null || !map.gameObject.activeInHierarchy || map.cuttables == null) continue;
                foreach (var c in map.cuttables)
                    if (panel == null && c != null && c.TotalCuts >= 12) panel = c;
            }
            if (panel == null) return;
            foreach (var enemy in director.enemies) if (enemy != null) enemy.gameObject.SetActive(false);
            panel.EnsureSeam();
            Physics.SyncTransforms();

            // Burn along the seam as a steady hand would: stretches 0..5 and part of 6, at 0.36 m/s.
            const float dt = 1f / 72f, speed = 0.36f;
            var points = panel.cutPoints;
            Vector3 head = points[0].transform.position;
            for (int i = 0; i <= 6; i++)
            {
                var stretch = points[i];
                Vector3 a = stretch.transform.position - stretch.Axis * (stretch.SeamLength * 0.5f);
                Vector3 b = stretch.transform.position + stretch.Axis * (stretch.SeamLength * (i == 6 ? -0.05f : 0.5f));
                int steps = Mathf.Max(1, Mathf.RoundToInt(Vector3.Distance(a, b) / (speed * dt)));
                for (int k = 0; k <= steps; k++)
                {
                    head = Vector3.Lerp(a, b, (float)k / steps);
                    for (int j = Mathf.Max(0, i - 1); j <= Mathf.Min(points.Length - 1, i + 1); j++)
                    {
                        var s = points[j];
                        Vector3 local = head - s.transform.position;
                        float along = Vector3.Dot(local, s.Axis);
                        Vector3 across = local - s.Axis * along - s.Normal * Vector3.Dot(local, s.Normal);
                        if (across.magnitude > 0.03f) continue;
                        panel.ApplySweep(s, along, 1f / SeamCut.CellBurnSeconds, dt, SeamCut.PassDepth(speed), null, out _);
                    }
                    panel.StepVisuals(dt);
                }
            }

            Vector3 n = points[6].Normal;
            Vector3 along6 = points[6].Axis;
            Vector3 up = Vector3.Cross(along6, n).normalized;
            if (Vector3.Dot(up, Vector3.up) < 0f) up = -up;
            Vector3 side = Vector3.Cross(up, n).normalized;

            // A crew member's cutter with its flame on the head of the line.
            var go = Object.Instantiate(remotePrefab);
            go.hideFlags = HideFlags.HideAndDontSave;
            var crew = go.GetComponent<RemoteAvatar>();
            crew.Setup(new TrashAstronauts.Core.PlayerId(1), 0);
            Vector3 eye = head + n * 0.6f + side * 0.28f + Vector3.up * 0.3f;
            Quaternion face = Quaternion.LookRotation(head - eye, Vector3.up);
            Vector3 hand = eye + face * new Vector3(0.12f, -0.3f, 0.3f);
            var snap = AvatarSnapshot.Default;
            snap.headPosition = NetUnity.ToNet(eye);
            snap.headRotation = NetUnity.ToNet(face);
            snap.leftHandPosition = NetUnity.ToNet(eye + face * new Vector3(-0.25f, -0.55f, 0.15f));
            snap.leftHandRotation = NetUnity.ToNet(face);
            snap.rightHandPosition = NetUnity.ToNet(hand);
            snap.rightHandRotation = NetUnity.ToNet(Quaternion.LookRotation(-n * 0.85f + Vector3.down * 0.1f - side * 0.2f, Vector3.up));
            snap.rightGripCurl = 1f;
            snap.leftGripCurl = 0.2f;
            snap.rightTool = AvatarSampler.Cutter;
            snap.flags = (byte)(AvatarFlags.CutterFiring | AvatarFlags.Cutting);
            crew.Apply(in snap);
            crew.rightGlove.Apply(0.6f, 1f);
            crew.leftGlove.Apply(0.1f, 0.2f);
            crew.suit.SnapToHead();
            crew.suit.Pose(0f);
            var tools = crew.tools;
            Vector3 miss = head + n * 0.03f - tools.cutterTip.position;
            snap.rightHandPosition = NetUnity.ToNet(crew.rightHand.transform.position + miss);
            crew.Apply(in snap);
            crew.suit.Pose(0f);
            Physics.SyncTransforms();
            for (int i = 0; i < 30; i++)
            {
                tools.StepSparks(0.02f);
                if (tools.cutterSparks != null) tools.cutterSparks.Simulate(0.02f, true, false, true);
            }

            // The flame's one flickering light, warm while it bites (what the cutter's own light does).
            var flameLight = new GameObject("Preview Flame Light") { hideFlags = HideFlags.HideAndDontSave }.AddComponent<Light>();
            flameLight.type = LightType.Point;
            flameLight.transform.position = head + n * 0.3f;
            flameLight.range = 2.2f;
            flameLight.intensity = 0.7f; // what the cutter's own light does while cutting (0.5 to 0.8, 30 cm off the metal)
            flameLight.color = Color.Lerp(new Color(0.5f, 0.8f, 1f), new Color(1f, 0.62f, 0.3f), 0.6f);
            flameLight.shadows = LightShadows.None;

            Vector3 centre = panel.transform.position;
            Vector3 back = -along6; // toward the line already cut
            Shot("80_seam_cut_closeup", head + n * 0.35f - back * 0.25f + up * 0.2f, head + back * 0.1f, false, 55f);
            Shot("80b_seam_cut_closeup_behind", head + n * 0.4f + back * 0.25f + up * 0.25f, head + back * 0.1f, false, 55f);
            Shot("81_seam_cut_one_metre", centre + n * 1.05f - side * 0.25f + Vector3.up * 0.1f, centre + n * 0.05f, false, 65f);
            Vector3 corner = points[2].transform.position + points[2].Axis * (points[2].SeamLength * 0.5f);
            Shot("82_seam_cut_corner_kerf", corner + n * 0.3f - points[2].Axis * 0.12f + up * 0.04f, corner - points[2].Axis * 0.05f, false, 55f);

            // The cutter's own screen with the longest seam read-out it shows.
            var cutter = Object.FindFirstObjectByType<TrashAstronauts.Tools.PlasmaCutterTool>();
            if (cutter != null && cutter.readout != null)
            {
                string old = cutter.readout.text;
                cutter.readout.text = "HEAT 100% HOT\nSEAM 12/12 TOO FAST";
                cutter.readout.ForceMeshUpdate();
                Transform screen = cutter.readout.transform;
                Shot("83_cutter_screen_seam", screen.position - screen.forward * 0.16f + screen.up * 0.02f, screen.position, false, 40f);
                cutter.readout.text = old;
            }

            Object.DestroyImmediate(flameLight.gameObject);
            Object.DestroyImmediate(go);
            panel.ResetForRun();
        }
    }
}

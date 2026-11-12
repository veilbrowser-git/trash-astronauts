using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.Tools;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Cutting a panel open along its seam with the real plasma cutter in the real scene. The cutter is driven through
    /// its manual-drive seam (<see cref="PlasmaCutterTool.ManualDrive"/>): its transform is put where a steady hand
    /// would hold it, frame by frame at a fixed 72 Hz, and it steps exactly as it does when held.
    /// </summary>
    public class CuttingSeamPlayTests
    {
        const float Dt = 1f / 72f;

        MemorySaveStorage m_Storage;
        MissionDirector m_Director;
        PlayerContext m_Player;
        PlasmaCutterTool m_Cutter;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            m_Storage = new MemorySaveStorage();
            MissionDirector.StorageOverride = m_Storage;
            MissionDirector.SeedOverride = 20261111;
            MissionDirector.CreatureOverride = 0;
            MissionDirector.MapOverride = 0;
            MissionDirector.ConditionOverride = ContractCondition.Standard;
            BlackoutController.DamageOverride = 0f;
            Cuttable.ReportClock = null;
            Cuttable.ResetReportPacing();
            yield return SceneManager.LoadSceneAsync("TrashAstronauts", LoadSceneMode.Single);
            yield return null;
            yield return null;
            m_Director = MissionDirector.Active;
            m_Player = m_Director.Players.First();
            m_Cutter = m_Player.tools.OfType<PlasmaCutterTool>().First();
        }

        [TearDown]
        public void TearDown()
        {
            if (m_Cutter != null) m_Cutter.ManualDrive = false;
            if (MissionDirector.Active != null) MissionDirector.Active.Net = null;
            Cuttable.ReportClock = null;
            Cuttable.ResetReportPacing();
            MissionDirector.StorageOverride = null;
            MissionDirector.SeedOverride = null;
            MissionDirector.CreatureOverride = null;
            MissionDirector.MapOverride = null;
            MissionDirector.ConditionOverride = null;
            BlackoutController.DamageOverride = null;
        }

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        IEnumerator StartContract()
        {
            m_Director.StartMission(PlayerId.Local);
            yield return Frames(2);
            m_Cutter.ManualDrive = true;
        }

        Cuttable Panel(string id) => m_Director.cuttables.First(c => c != null && c.panelId == id);

        static Vector3 StartOf(CutPoint p) => p.transform.position - p.Axis * (p.SeamLength * 0.5f);
        static Vector3 EndOf(CutPoint p) => p.transform.position + p.Axis * (p.SeamLength * 0.5f);

        /// <summary>Puts the cutter so its flame meets the metal at <paramref name="contact"/> from <paramref name="standoff"/> away.</summary>
        void Place(Vector3 contact, Vector3 normal, float standoff) => PlaceNozzle(contact + normal * standoff, -normal);

        /// <summary>Puts the cutter's nozzle at a point with its flame along a direction.</summary>
        void PlaceNozzle(Vector3 nozzle, Vector3 flame)
        {
            Vector3 dir = flame.normalized;
            Quaternion tipLocal = Quaternion.Inverse(m_Cutter.transform.rotation) * m_Cutter.tip.rotation;
            Vector3 up = Mathf.Abs(Vector3.Dot(dir, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up;
            m_Cutter.transform.rotation = Quaternion.LookRotation(dir, up) * Quaternion.Inverse(tipLocal);
            m_Cutter.transform.position += nozzle - m_Cutter.tip.position;
        }

        /// <summary>
        /// Draws the flame steadily along stretches <paramref name="first"/>..<paramref name="last"/> at
        /// <paramref name="speed"/>, starting <paramref name="lead"/> before the first and running on as far past the
        /// last; <paramref name="each"/> runs after every step with the seconds so far. Returns the seconds taken.
        /// </summary>
        float Sweep(Cuttable panel, int first, int last, float speed, float standoff = 0.03f, float lead = 0.03f, Action<float> each = null, bool trigger = true)
        {
            var path = new List<Vector3>();
            var normals = new List<Vector3>();
            var a = panel.cutPoints[first];
            path.Add(StartOf(a) - a.Axis * lead);
            normals.Add(a.Normal);
            for (int i = first; i <= last; i++)
            {
                path.Add(EndOf(panel.cutPoints[i]));
                normals.Add(panel.cutPoints[i].Normal);
            }
            var z = panel.cutPoints[last];
            path[path.Count - 1] = EndOf(z) + z.Axis * lead;
            float seconds = 0f;
            for (int leg = 0; leg + 1 < path.Count; leg++)
            {
                float length = Vector3.Distance(path[leg], path[leg + 1]);
                int steps = Mathf.Max(1, Mathf.RoundToInt(length / (speed * Dt)));
                for (int k = leg == 0 ? 0 : 1; k <= steps; k++)
                {
                    Place(Vector3.Lerp(path[leg], path[leg + 1], (float)k / steps), normals[leg + 1], standoff);
                    m_Cutter.Step(Dt, trigger);
                    panel.StepVisuals(Dt);
                    seconds += Dt;
                    each?.Invoke(seconds);
                }
            }
            return seconds;
        }

        void Hold(Vector3 contact, Vector3 normal, float standoff, float seconds, Vector3? flame = null, bool trigger = true, Cuttable panel = null)
        {
            for (float t = 0f; t < seconds; t += Dt)
            {
                if (flame.HasValue) PlaceNozzle(contact + normal * standoff, flame.Value);
                else Place(contact, normal, standoff);
                m_Cutter.Step(Dt, trigger);
                if (panel != null) panel.StepVisuals(Dt);
            }
        }

        void Cool()
        {
            for (int i = 0; i < 72 * 8 && m_Cutter.Heat.Heat > 0f; i++) m_Cutter.Step(Dt, false);
        }

        // ------------------------------------------------------------------ the seams the panels have

        [UnityTest]
        public IEnumerator EveryPanel_IsHeldByASeamOfShortStretches_InsideTheProtocolLimits()
        {
            yield return null;
            var all = m_Director.maps.Where(m => m != null && m.cuttables != null).SelectMany(m => m.cuttables)
                .Concat(m_Director.cuttables).Concat(m_Director.sharedCuttables ?? new Cuttable[0]).Where(c => c != null).Distinct().ToArray();
            Assert.Greater(all.Length, 4);
            foreach (var panel in all)
            {
                Assert.GreaterOrEqual(panel.TotalCuts, 2, panel.panelId);
                Assert.LessOrEqual(panel.TotalCuts, 12, panel.panelId + ": few enough stretches to stay well under the weld report limit.");
                Assert.LessOrEqual(panel.TotalCuts, NetIdTable.MaxCutPointsPerPanel);
                Assert.IsNotNull(panel.seamMaterial, panel.panelId + " draws its seam.");
                foreach (var p in panel.cutPoints)
                {
                    Assert.Greater(p.length, 0.05f, panel.panelId);
                    Assert.LessOrEqual(p.length, 0.341f, panel.panelId);
                    Assert.Greater(Vector3.Dot(p.Normal, panel.transform.forward), 0.99f, panel.panelId + ": the seam lies on the plate's face.");
                }
            }
            var cage = Panel("panel_cage");
            Assert.AreEqual(12, cage.TotalCuts, "A locker panel is cut all round: three stretches a side.");
            Assert.AreEqual(3.92f, cage.SeamLength, 0.02f);
            Assert.AreEqual(8, m_Director.sharedCuttables.First(c => c.isTraining).TotalCuts, "The practice plate: two stretches a side.");
            Assert.LessOrEqual(m_Director.NetIds.TotalCutPoints, NetProtocol.MaxCutPoints, "The weld bitset holds every stretch of every panel.");
        }

        // ------------------------------------------------------------------ cutting

        [UnityTest]
        public IEnumerator ASteadyPassAlongTheSeam_CutsItsStretchesInOrder_AndThePanelOpensOnlyWhenTheLastIsThrough()
        {
            yield return StartContract();
            var panel = Panel("panel_cage");
            var order = new List<int>();
            bool openedEarly = false, overheated = false, sawSeamReadout = false;
            float looseBeforeLast = 0f;
            Vector3 home = panel.plate.transform.position;
            panel.PointCut += (p, index, by) =>
            {
                order.Add(index);
                if (panel.IsOpen && order.Count < panel.TotalCuts) openedEarly = true;
            };
            int noiseBefore = m_Director.noise.EmittedCount;
            float seconds = Sweep(panel, 0, panel.TotalCuts - 1, 0.36f, 0.03f, 0.03f, t =>
            {
                if (m_Cutter.Heat.Overheated) overheated = true;
                if (m_Cutter.readout != null && m_Cutter.readout.text.Contains("SEAM " + panel.CutCount + "/12")) sawSeamReadout = true;
                if (panel.CutCount == panel.TotalCuts - 1) looseBeforeLast = Mathf.Max(looseBeforeLast, panel.Looseness);
                if (!panel.IsOpen && panel.CutCount < panel.TotalCuts) Assert.IsFalse(panel.IsOpen);
            });
            Debug.Log("[CUT] steady pass at 0.36 m/s: " + seconds.ToString("F1") + " s, heat " + m_Cutter.Heat.Heat.ToString("F2"));

            CollectionAssert.AreEqual(Enumerable.Range(0, 12), order, "Stretches part in order along the seam.");
            Assert.IsFalse(openedEarly, "Uncut stretches hold the plate.");
            Assert.IsTrue(panel.IsOpen, "The last stretch frees the plate.");
            Assert.IsFalse(overheated, "A careful steady cut of a whole locker panel fits in one heat bar.");
            Assert.That(seconds, Is.InRange(9f, 14f));
            Assert.IsTrue(sawSeamReadout, "The cutter's screen shows the seam's progress.");
            Assert.Greater(looseBeforeLast, 0.2f, "The plate works loose as the seam parts.");
            Assert.Greater(m_Director.noise.EmittedCount, noiseBefore, "Cutting is heard.");
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            Assert.Greater(Vector3.Distance(panel.plate.transform.position, home), 0.005f, "The plate drifts off.");
        }

        [UnityTest]
        public IEnumerator ThePlate_LoosensOnTheCutSide_ButStaysPut_UntilTheLastStretch()
        {
            yield return StartContract();
            var panel = Panel("panel_cage");
            Vector3 homePos = panel.plate.transform.localPosition;
            Quaternion homeRot = panel.plate.transform.localRotation;
            // Cut the top and right sides only (six of twelve).
            Sweep(panel, 0, 5, 0.34f);
            for (int i = 0; i < 300; i++) panel.StepVisuals(Dt);
            Assert.AreEqual(6, panel.CutCount);
            Assert.IsFalse(panel.IsOpen);
            Assert.IsTrue(panel.plate.isKinematic, "Still held by the uncut side.");
            Assert.AreEqual(6f / 11f, panel.Looseness, 0.01f);
            // The corner between the cut sides stands off the face; the opposite corner stays flush.
            Vector3 cutCorner = panel.transform.InverseTransformPoint(EndOf(panel.cutPoints[2]));
            Vector3 heldCorner = -cutCorner;
            Vector3 Moved(Vector3 local)
            {
                Vector3 plateLocal = Quaternion.Inverse(homeRot) * (local - homePos);
                Vector3 now = panel.plate.transform.localPosition + panel.plate.transform.localRotation * plateLocal;
                return now - local;
            }
            float cutGap = Moved(cutCorner).z, heldGap = Moved(heldCorner).z;
            Assert.That(cutGap, Is.InRange(0.003f, Cuttable.MaxLooseGap * 1.3f), "The cut side gapes.");
            Assert.That(heldGap, Is.InRange(-0.001f, cutGap * 0.5f), "The welded side stays flush.");
            panel.ResetForRun();
            Assert.AreEqual(homePos, panel.plate.transform.localPosition);
            Assert.AreEqual(0f, panel.Looseness);
        }

        [UnityTest]
        public IEnumerator ATooFastPass_OnlyScoresTheMetal_WithAThinnerGlow_AndASecondPassFinishesIt()
        {
            yield return StartContract();
            var panel = Panel("panel_cage");
            Sweep(panel, 0, 2, 0.6f);
            float fastWidth = panel.Seam.GlowWidth(2 * CutPoint.Cells + CutPoint.Cells - 1);
            for (int i = 0; i < 3; i++)
            {
                Assert.IsFalse(panel.cutPoints[i].IsCut, "Too fast: stretch " + i + " is only scored.");
                Assert.That(panel.cutPoints[i].Progress, Is.InRange(0.15f, 0.75f), "Partial progress on stretch " + i);
            }
            Assert.AreEqual(PlasmaCutterTool.CutQuality.TooFast, m_Cutter.Quality);

            // The same stretches again at a steady pace go through.
            Cool();
            Sweep(panel, 0, 2, 0.34f);
            for (int i = 0; i < 3; i++) Assert.IsTrue(panel.cutPoints[i].IsCut, "The second pass finishes stretch " + i);

            // A clean pass on fresh metal leaves a wider molten line than the skim did.
            Cool();
            Sweep(panel, 3, 3, 0.34f);
            float cleanWidth = panel.Seam.GlowWidth(3 * CutPoint.Cells + CutPoint.Cells - 1);
            Assert.Greater(cleanWidth, fastWidth * 1.4f, "A skim glows thinner than a clean cut (" + fastWidth + " vs " + cleanWidth + ").");
            Assert.Less(SeamCut.PassDepth(0.9f), 0.5f);
            Assert.AreEqual(1f, SeamCut.PassDepth(SeamCut.CleanSpeed), 1e-4f);
        }

        [UnityTest]
        public IEnumerator AFlameOffTheMetal_FacingAway_OrOffTheLine_DoesNothing_AndHoldingStillOnlyBurnsHeat()
        {
            yield return StartContract();
            var panel = Panel("panel_cage");
            var stretch = panel.cutPoints[4];
            Vector3 mid = stretch.transform.position, n = stretch.Normal;
            Vector3 across = Vector3.Cross(n, stretch.Axis);

            Hold(mid, n, 0.12f, 1f);
            Assert.AreEqual(0f, stretch.Progress, "12 cm off: the flame does not reach.");
            Assert.IsFalse(m_Cutter.IsCutting);
            Assert.AreEqual(PlasmaCutterTool.CutQuality.Air, m_Cutter.Quality);

            Hold(mid, n, 0.03f, 1f, n);
            Assert.AreEqual(0f, stretch.Progress, "Facing away from the metal.");
            Assert.IsFalse(m_Cutter.IsCutting);

            Hold(mid + across * 0.07f, n, 0.03f, 1f);
            Assert.AreEqual(0f, stretch.Progress, "7 cm beside the seam.");

            // Control: the same flame on the line cuts, and holding it there only burns the spot under it.
            Cool();
            float heatBefore = m_Cutter.Heat.Heat;
            Hold(mid, n, 0.03f, 1f);
            Assert.Greater(stretch.Progress, 0.05f);
            Assert.Less(stretch.Progress, 0.3f, "Holding still cuts only the spot under the flame.");
            Assert.IsFalse(stretch.IsCut);
            Assert.AreEqual(PlasmaCutterTool.CutQuality.Dwelling, m_Cutter.Quality);
            float dwellHeat = m_Cutter.Heat.Heat - heatBefore;
            Assert.Greater(dwellHeat, 0.7f / m_Director.Config.cutter.secondsToOverheat, "Dwelling heats the cutter at about the full rate.");

            // A clean steady cut heats it much more slowly.
            Cool();
            heatBefore = m_Cutter.Heat.Heat;
            float seconds = Sweep(panel, 6, 8, SeamCut.CleanSpeed * 0.95f);
            float cleanRate = (m_Cutter.Heat.Heat - heatBefore) / seconds;
            Assert.Less(cleanRate, dwellHeat * 0.6f, "Cutting cleanly takes less heat a second than dwelling.");
        }

        [UnityTest]
        public IEnumerator Overheating_StopsTheCut_UntilTheCutterCools()
        {
            yield return StartContract();
            var panel = Panel("panel_cage");
            var first = panel.cutPoints[0];
            float t = 0f;
            for (; t < 12f && !m_Cutter.Heat.Overheated; t += Dt) Hold(first.transform.position, first.Normal, 0.03f, Dt);
            Assert.IsTrue(m_Cutter.Heat.Overheated, "Dwelling on the metal overheats the cutter.");
            Assert.That(t, Is.InRange(6f, 7.6f), "In about the seven seconds a flame in the air takes.");

            var fresh = panel.cutPoints[6];
            Hold(fresh.transform.position, fresh.Normal, 0.03f, 1f);
            Assert.IsFalse(m_Cutter.IsFiring, "Locked out while overheated.");
            Assert.AreEqual(0f, fresh.Progress, "No flame, no cut.");
            StringAssert.Contains("OVERHEAT", m_Cutter.readout.text);

            Cool();
            Assert.IsFalse(m_Cutter.Heat.Overheated);
            Hold(fresh.transform.position, fresh.Normal, 0.03f, 0.3f);
            Assert.Greater(fresh.Progress, 0f, "Cooled, it cuts again.");
        }

        [UnityTest]
        public IEnumerator TheMoltenLine_FollowsTheFlame_AndCoolsFromWhiteThroughOrangeAndRedToDark_LeavingAKerf()
        {
            yield return StartContract();
            var panel = Panel("panel_cage");
            var seam = panel.Seam;
            Assert.IsNotNull(seam, "The panel draws its seam line.");
            int uncutCell = 5 * CutPoint.Cells + 8;
            Color32 bead = seam.BeadColor(uncutCell);
            Assert.Greater(bead.r, 100, "An uncut seam shows as a dim amber weld line.");
            Assert.AreEqual(0, seam.GlowColor(uncutCell).a);

            Sweep(panel, 0, 0, 0.34f, 0.03f, 0.01f);
            Assert.IsTrue(panel.cutPoints[0].IsCut);
            int head = CutPoint.Cells - 1, tail = 0;
            Assert.Greater(panel.cutPoints[0].GlowAt(head), panel.cutPoints[0].GlowAt(tail), "The line is hottest where the flame is and cooler behind it.");
            Color32 hot = seam.GlowColor(head);
            Assert.Greater(hot.g, 200, "White-yellow under the flame: " + hot);
            Assert.Greater(hot.a, 200);
            Color32 kerf = seam.BeadColor(head);
            Assert.Less(kerf.r, 30, "Cut through: a dark kerf.");

            panel.StepVisuals(1.2f);
            Color32 orange = seam.GlowColor(head);
            Assert.Greater(orange.r, 200);
            Assert.That((int)orange.g, Is.InRange(60, 190), "Orange after a second: " + orange);
            panel.StepVisuals(1.0f);
            Color32 red = seam.GlowColor(head);
            Assert.Less(red.g, orange.g);
            Assert.Less(red.r, orange.r + 1, "Dull red: " + red);
            panel.StepVisuals(1.5f);
            Assert.AreEqual(0, seam.GlowColor(head).a, "Dark after a few seconds.");
            Assert.AreEqual(0f, panel.cutPoints[0].MaxGlow);
            Assert.IsFalse(panel.cutPoints[0].VisualActive);
            Assert.Less(seam.BeadColor(head).r, 30, "The kerf stays.");
        }

        [UnityTest]
        public IEnumerator TheLastStretch_PopsThePlate_WithTheChosenPush_EdgesStillHot_AndFreesTheMountedSalvage()
        {
            SalvageItem mounted = null;
            for (int seed = 1; seed < 40 && mounted == null; seed++)
            {
                MissionDirector.SeedOverride = seed;
                if (m_Director.Phase == MissionPhase.Active) m_Director.FailMission(FailureReason.Caught, PlayerId.Local);
                m_Director.StartMission(PlayerId.Local);
                yield return Frames(2);
                mounted = m_Director.Items.FirstOrDefault(i => i.IsMounted);
            }
            Assert.IsNotNull(mounted, "No run placed salvage on the mount.");
            m_Cutter.ManualDrive = true;
            var mount = m_Director.spawnPoints.First(p => p.pointId == "maint_rack").mount;
            Assert.AreEqual(2, mount.TotalCuts, "The rack clamp is cut through at both ends.");
            int noise = m_Director.noise.EmittedCount;

            Sweep(mount, 0, 0, 0.3f, 0.03f, 0.03f);
            Assert.IsTrue(mount.cutPoints[0].IsCut);
            Assert.IsFalse(mount.IsOpen, "One clamp still holds it.");
            Assert.IsTrue(mounted.IsMounted);
            Cool();
            Sweep(mount, 1, 1, 0.3f, 0.03f, 0.03f);
            Assert.IsTrue(mount.IsOpen);
            Assert.IsFalse(mounted.IsMounted, "The salvage comes free.");
            Assert.IsTrue(mounted.grab.enabled);
            Assert.IsFalse(mount.plate.isKinematic, "The plate is a loose physical object.");
            Assert.AreEqual(GameLayers.Salvage, mount.plate.gameObject.layer);
            Assert.Greater(Vector3.Dot(mount.LastPlateImpulse, mount.transform.forward), 0.3f, "Pushed out of the face.");
            Assert.Greater(mount.cutPoints.Max(p => p.MaxGlow), 0.6f, "Its edges are still hot.");
            Assert.Greater(m_Director.noise.EmittedCount, noise, "Cutting and the plate breaking free are heard.");
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            Assert.Less(Vector3.Distance(mount.plate.linearVelocity, mount.LastPlateImpulse), 0.15f, "It leaves with the push it was given.");
        }

        [UnityTest]
        public IEnumerator ThePracticePlate_InTheTug_IsCutTheSameWay()
        {
            yield return null;
            m_Cutter.ManualDrive = true;
            var practice = m_Director.sharedCuttables.First(c => c.isTraining);
            float seconds = Sweep(practice, 0, practice.TotalCuts - 1, 0.32f);
            Assert.IsTrue(practice.IsOpen, "The practice plate opens with the same cut.");
            Debug.Log("[CUT] practice plate at 0.32 m/s: " + seconds.ToString("F1") + " s");
            Assert.Less(seconds, 7f);
        }

        // ------------------------------------------------------------------ co-op client

        sealed class FakeNet : IMissionNet
        {
            public Func<double> clock;
            public readonly List<(double time, string panel, int index)> reports = new List<(double, string, int)>();
            public bool IsClient => true;
            public bool IsHost => false;
            public int LocalSlot => 1;
            public void RequestStart() { }
            public void RequestExtract() { }
            public void RequestDoor(SlidingDoor door, bool desiredOpen) { }
            public void ReportPointCut(Cuttable panel, int pointIndex) => reports.Add((clock(), panel.panelId, pointIndex));
            public void ReportSalvageDamage(SalvageItem item, int newValue) { }
            public void ReportWire(WebTripwire wire, WireChange change) { }
            public void ReportTranqHit(Vector3 origin, Vector3 hitPoint) { }
            public void ForwardNoise(in NoiseEvent noise) { }
            public void ReportPause(bool paused) { }
            public void ReportSettingsChanged(Handedness handedness) { }
        }

        /// <summary>True if the host's per-player bucket (CoopHostRules.TakeToken) would take every one of these reports.</summary>
        static bool HostWouldTakeAll(IEnumerable<double> times, out string why)
        {
            float tokens = CoopHostRules.RequestBurst;
            double last = double.NaN;
            int n = 0;
            foreach (double t in times)
            {
                if (!double.IsNaN(last)) tokens = Mathf.Min(CoopHostRules.RequestBurst, tokens + (float)((t - last) * CoopHostRules.RequestsPerSecond));
                last = t;
                if (tokens < 1f) { why = "report " + n + " at " + t.ToString("F2") + " s would be dropped"; return false; }
                tokens -= 1f;
                n++;
            }
            why = null;
            return true;
        }

        [UnityTest]
        public IEnumerator AsAClient_TheSamePass_ReportsEachStretch_UnderTheHostsRateLimit_AndNeverOpensThePanelHere()
        {
            yield return StartContract();
            double now = 0.0;
            var net = new FakeNet { clock = () => now };
            m_Player.AssignId(new PlayerId(1), false);
            m_Director.Net = net;
            Cuttable.ReportClock = () => now;
            Cuttable.ResetReportPacing();

            // A fast clean cut all round a locker panel.
            var panel = Panel("panel_cage");
            Sweep(panel, 0, panel.TotalCuts - 1, SeamCut.CleanSpeed, 0.03f, 0.03f, t =>
            {
                now = t;
                panel.PumpReports();
            });
            for (int i = 0; i < 72 * 5 && panel.PendingReports > 0; i++) { now += Dt; panel.PumpReports(); }
            Assert.AreEqual(12, panel.CutCount, "Every stretch is cut here at once.");
            Assert.IsFalse(panel.IsOpen, "A client never opens a shared panel itself: it waits for the host.");
            CollectionAssert.AreEqual(Enumerable.Range(0, 12), net.reports.Select(r => r.index), "One report per stretch, in order.");
            Assert.IsTrue(net.reports.All(r => r.panel == panel.panelId));
            Assert.IsTrue(HostWouldTakeAll(net.reports.Select(r => r.time), out string why), why);
            double span = net.reports.Last().time - net.reports.First().time;
            Debug.Log("[CUT] client clean cut: 12 reports over " + span.ToString("F1") + " s (" + (11 / span).ToString("F2") + " per second)");
            Assert.Less(11 / span, CoopHostRules.RequestsPerSecond, "Well under the host's refill rate.");

            // A frantic finish: every stretch of a second panel scored nearly through, then swept at 1.2 m/s.
            var duct = Panel("panel_reactor");
            foreach (var p in duct.cutPoints) p.Burn(0.9f, 1f);
            Assert.AreEqual(0, duct.CutCount);
            Cool();
            int before = net.reports.Count;
            Sweep(duct, 0, duct.TotalCuts - 1, 1.2f, 0.03f, 0.03f, t =>
            {
                now += Dt;
                duct.PumpReports();
            });
            for (int i = 0; i < 72 * 8 && duct.PendingReports > 0; i++) { now += Dt; duct.PumpReports(); }
            var burst = net.reports.Skip(before).ToList();
            Assert.AreEqual(duct.TotalCuts, burst.Count, "Every stretch is reported in the end.");
            Assert.IsTrue(HostWouldTakeAll(net.reports.Select(r => r.time), out why), "Paced under the host's limit: " + why);
            Assert.IsFalse(duct.IsOpen);
        }
    }

    /// <summary>A crew mate's cutting, as this headset shows it: the same glowing line, drawn along the right stretch.</summary>
    public class CuttingSeamNetPlayTests : NetPlayFixture
    {
        static Cuttable FirstPanel(MissionDirector director, out byte netId)
        {
            for (int i = 0; i < 256; i++)
            {
                var panel = director.PanelAt(i);
                if (panel == null) break;
                if (!panel.isTraining && panel.gameObject.activeInHierarchy && !panel.IsOpen && panel.TotalCuts >= 8) { netId = (byte)i; return panel; }
            }
            netId = 0;
            return null;
        }

        static AvatarSnapshot Pose(Vector3 head)
        {
            var s = AvatarSnapshot.Default;
            s.headPosition = head.ToNet();
            s.headRotation = Quaternion.identity.ToNet();
            s.leftHandPosition = (head + new Vector3(-0.22f, -0.4f, 0.3f)).ToNet();
            s.rightHandPosition = (head + new Vector3(0.22f, -0.4f, 0.3f)).ToNet();
            s.leftHandRotation = Quaternion.identity.ToNet();
            s.rightHandRotation = Quaternion.identity.ToNet();
            return s;
        }

        static int CellsCut(CutPoint p)
        {
            int n = 0;
            for (int c = 0; c < CutPoint.Cells; c++) if (p.DisplayDepthAt(c) >= 0.95f) n++;
            return n;
        }

        /// <summary>Half a second of game time, so a line being drawn in over a message interval is finished.</summary>
        IEnumerator Drawn()
        {
            float until = Time.time + 0.5f;
            while (Time.time < until)
            {
                Tick();
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator TwoCrewMatesCutting_DrawTheirLinesOnTheirOwnStretches_AndTheyAddUp()
        {
            Assert.IsTrue(m_Net.Host());
            var a = AddBot(m_Net.Transport.Local);
            var b = AddBot(m_Net.Transport.Local);
            a.Avatar = Pose(new Vector3(0.4f, 1.6f, -1.6f));
            b.Avatar = Pose(new Vector3(-0.4f, 1.6f, -1.6f));
            yield return Until(() => m_Net.Crew(1) != null && m_Net.Crew(2) != null);
            var sync = m_Net.GetComponent<CoopToolSync>();
            Assert.IsNotNull(sync);
            var panel = FirstPanel(m_Director, out byte netId);
            Assert.IsNotNull(panel, "Test set-up: a contract panel with a long seam.");
            var seam = panel.Seam;
            Assert.IsNotNull(seam);

            a.Session.SendCutProgress(netId, 4, CutProgressMsg.ToByte(0.5f));
            b.Session.SendCutProgress(netId, 7, CutProgressMsg.ToByte(0.25f));
            yield return Until(() => sync.Applied >= 2);
            yield return Drawn();

            Assert.AreEqual(8, CellsCut(panel.cutPoints[4]), "Half of stretch 4 shows cut.");
            Assert.AreEqual(4, CellsCut(panel.cutPoints[7]), "A quarter of stretch 7.");
            for (int i = 0; i < panel.TotalCuts; i++)
            {
                if (i == 4 || i == 7) continue;
                Assert.AreEqual(0, CellsCut(panel.cutPoints[i]), "Nothing drawn on stretch " + i);
                Assert.AreEqual(0f, panel.cutPoints[i].MaxGlow, "No glow on stretch " + i);
            }
            Assert.Greater(panel.cutPoints[4].MaxGlow, 0.5f, "Their line glows as it is drawn.");
            int drawn = 4 * CutPoint.Cells + 2, untouched = 4 * CutPoint.Cells + 13;
            Assert.Less(seam.BeadColor(drawn).r, seam.BeadColor(untouched).r, "The crossed part of the seam darkens toward a kerf.");
            Assert.AreEqual(0, panel.CutCount, "Display only: nothing is cut here.");

            // More progress draws the line on along the same stretch.
            a.Session.SendCutProgress(netId, 4, CutProgressMsg.ToByte(0.75f));
            int applied = sync.Applied;
            yield return Until(() => sync.Applied > applied);
            yield return Drawn();
            Assert.AreEqual(12, CellsCut(panel.cutPoints[4]));

            // The host's word that it is through: the whole stretch is a kerf, glowing where it was not yet drawn.
            Assert.IsTrue(panel.ApplyRemotePointCut(4, new PlayerId(1)));
            Assert.AreEqual(16, CellsCut(panel.cutPoints[4]));
            Assert.Greater(panel.cutPoints[4].GlowAt(15), 0.4f);
            Assert.AreEqual(1, panel.CutCount);
        }
    }
}

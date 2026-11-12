using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TMPro;
using TrashAstronauts.Audio;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.Tools;
using TrashAstronauts.World;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace TrashAstronauts.Tests
{
    /// <summary>
    /// Crew mates' tools: what a stand-in shows in its gloves and holsters, the flashlight beam and the light budget,
    /// the cutter's flame, sparks and sound, weld progress from other players, what the local rig streams, and that
    /// none of it exists or lingers outside a session. Remote stand-ins are driven by a scene-less bot over the
    /// in-memory transport (as the presence tests do) or by synthetic snapshots straight into RemoteAvatar.Apply.
    /// </summary>
    public class RemoteToolPlayTests : NetPlayFixture
    {
        readonly List<GameObject> m_Spawned = new List<GameObject>();
        GameObject m_Viewer;

        [TearDown]
        public void CleanUpSpawned()
        {
            foreach (var go in m_Spawned) if (go != null) Object.Destroy(go);
            m_Spawned.Clear();
            if (m_Viewer != null) Object.Destroy(m_Viewer);
            RemoteToolRig.ViewerOverride = null;
            RemoteToolRig.MaxRealLights = 2;
        }

        static AvatarSnapshot Pose(Vector3 head, float yaw = 0f)
        {
            var s = AvatarSnapshot.Default;
            Quaternion face = Quaternion.Euler(0f, yaw, 0f);
            s.headPosition = head.ToNet();
            s.headRotation = face.ToNet();
            s.leftHandPosition = (head + face * new Vector3(-0.22f, -0.4f, 0.3f)).ToNet();
            s.rightHandPosition = (head + face * new Vector3(0.22f, -0.4f, 0.3f)).ToNet();
            s.leftHandRotation = face.ToNet();
            s.rightHandRotation = face.ToNet();
            return s;
        }

        static void SetBot(Bot bot, Vector3 head, byte flags = 0, byte left = 0, byte right = 0)
        {
            bot.Avatar = Pose(head, 90f);
            bot.Avatar.flags = flags;
            bot.Avatar.leftTool = left;
            bot.Avatar.rightTool = right;
        }

        RemoteAvatar SpawnStandIn(int slot)
        {
            Assert.IsNotNull(m_Net.remoteAvatarPrefab, "The NetSession carries the RemoteCrew prefab.");
            var crew = Object.Instantiate(m_Net.remoteAvatarPrefab);
            crew.Setup(new PlayerId(slot), 0);
            m_Spawned.Add(crew.gameObject);
            return crew;
        }

        static Vector3 GripWorld(RemoteToolRig rig, byte id) => rig.Tool(id).TransformPoint(rig.grips[id]);

        // ------------------------------------------------------------------ in hand and holstered

        [UnityTest]
        public IEnumerator HeldToolIds_PutTheToolInThatGlove_SeatedAsInTheOwnersHand_AndBackInItsHolster()
        {
            Assert.IsTrue(m_Net.Host());
            var bot = AddBot(m_Net.Transport.Local);
            SetBot(bot, new Vector3(0.4f, 1.6f, -1.6f), 0, AvatarSampler.Flashlight, AvatarSampler.Cutter);
            yield return Until(() => m_Net.Crew(1) != null && m_Net.Crew(1).HasPose && m_Net.Crew(1).tools.HolderOf(AvatarSampler.Cutter) == 2);
            var crew = m_Net.Crew(1);
            var rig = crew.tools;
            Assert.IsNotNull(rig, "The stand-in carries the tool rig.");

            Assert.AreEqual(2, rig.HolderOf(AvatarSampler.Cutter), "The cutter is in the right glove.");
            Assert.AreEqual(1, rig.HolderOf(AvatarSampler.Flashlight), "The flashlight is in the left glove.");
            Assert.AreSame(rig.rightAttach, rig.Tool(AvatarSampler.Cutter).parent);
            Assert.AreSame(rig.leftAttach, rig.Tool(AvatarSampler.Flashlight).parent);
            Assert.Less(Vector3.Distance(GripWorld(rig, AvatarSampler.Cutter), rig.rightAttach.position), 0.001f, "The grip sits in the fist.");
            Assert.Less(Quaternion.Angle(rig.Tool(AvatarSampler.Cutter).rotation, crew.rightHand.transform.rotation), 0.5f, "The tool points where the hand points.");

            // Exactly where the owner's own grab puts it: the same attach point in controller space.
            foreach (var (local, remote, attach) in new[] { (m_Player.leftHand, crew.leftHand, rig.leftAttach), (m_Player.rightHand, crew.rightHand, rig.rightAttach) })
            {
                Vector3 own = local.transform.InverseTransformPoint(local.interactor.attachTransform.position);
                Vector3 theirs = remote.transform.InverseTransformPoint(attach.position);
                Assert.Less(Vector3.Distance(own, theirs), 0.001f, "Attach point " + own.ToString("F3") + " vs " + theirs.ToString("F3"));
            }
            // And the grip offset is the real tool's.
            var realCutter = m_Player.tools.OfType<PlasmaCutterTool>().First();
            Assert.Less(Vector3.Distance(rig.grips[AvatarSampler.Cutter], realCutter.grab.attachTransform.localPosition), 1e-5f);

            Assert.AreEqual(0, rig.HolderOf(AvatarSampler.Scanner));
            Assert.AreSame(rig.holsters[AvatarSampler.Scanner], rig.Tool(AvatarSampler.Scanner).parent, "Tools not in a hand sit in their holsters.");
            Assert.AreSame(rig.holsters[AvatarSampler.Tranq], rig.Tool(AvatarSampler.Tranq).parent);
            foreach (var holster in rig.holsters.Skip(1)) Assert.IsTrue(holster.IsChildOf(crew.suit.torso), "Holsters are on the suit's torso.");

            // Swap hands, then put everything away.
            SetBot(bot, new Vector3(0.4f, 1.6f, -1.6f), 0, AvatarSampler.Scanner, AvatarSampler.Tranq);
            yield return Until(() => rig.HolderOf(AvatarSampler.Tranq) == 2);
            Assert.AreEqual(1, rig.HolderOf(AvatarSampler.Scanner));
            Assert.AreEqual(0, rig.HolderOf(AvatarSampler.Cutter), "The cutter went back to its holster.");
            Assert.AreSame(rig.holsters[AvatarSampler.Cutter], rig.Tool(AvatarSampler.Cutter).parent);
            Assert.AreEqual(Vector3.zero, rig.Tool(AvatarSampler.Cutter).localPosition);
            Assert.IsTrue(rig.ScannerHeld);
            Assert.IsTrue(rig.scannerScreenLit.activeSelf, "A held scanner's screen is lit.");

            SetBot(bot, new Vector3(0.4f, 1.6f, -1.6f));
            yield return Until(() => rig.HolderOf(AvatarSampler.Tranq) == 0 && rig.HolderOf(AvatarSampler.Scanner) == 0);
            for (byte id = 1; id <= 4; id++) Assert.AreSame(rig.holsters[id], rig.Tool(id).parent, "Tool " + id + " is holstered.");
            Assert.IsFalse(rig.scannerScreenLit.activeSelf);

            // Right-handed: the cutter's holster is on the right hip. Left-handed: the left, as on their own suit.
            Vector3 rightHanded = rig.holsters[AvatarSampler.Cutter].localPosition;
            Assert.Greater(rightHanded.x, 0.1f, "Dominant hip, right-handed: " + rightHanded);
            Assert.Less(rig.holsters[AvatarSampler.Tranq].localPosition.x, -0.05f, "Side chest is on the off-hand side.");
            SetBot(bot, new Vector3(0.4f, 1.6f, -1.6f), AvatarFlags.LeftHanded);
            yield return Until(() => rig.holsters[AvatarSampler.Cutter].localPosition.x < 0f);
            Assert.Less(rig.holsters[AvatarSampler.Cutter].localPosition.x, -0.1f, "Left-handed: the cutter sits on the left hip.");
            Assert.Greater(rig.holsters[AvatarSampler.Tranq].localPosition.x, 0.05f);
            Assert.AreEqual(Handedness.Left, crew.context.Handedness);
        }

        [UnityTest]
        public IEnumerator ToolCopies_AreTheRealMeshesAndMaterials_WithNoCollider_Interactor_Rigidbody_OrGameScript_OnHarmlessLayers()
        {
            yield return null;
            var crew = SpawnStandIn(1);
            var rig = crew.tools;
            Assert.IsNotNull(rig);
            var pose = Pose(new Vector3(0f, 1.6f, -20f));
            crew.Apply(in pose);
            var real = new SuitTool[] { null, m_Player.tools.OfType<FlashlightTool>().First(), m_Player.tools.OfType<ScannerTool>().First(),
                m_Player.tools.OfType<PlasmaCutterTool>().First(), m_Player.tools.OfType<TranqTool>().First() };
            for (byte id = 1; id <= 4; id++)
            {
                var copy = rig.Tool(id);
                Assert.IsNotNull(copy, "Copy " + id);
                Assert.IsEmpty(copy.GetComponentsInChildren<Collider>(true), real[id].toolName + ": no colliders.");
                Assert.IsEmpty(copy.GetComponentsInChildren<Rigidbody>(true), real[id].toolName + ": no rigidbody.");
                Assert.IsEmpty(copy.GetComponentsInChildren<XRBaseInteractable>(true), real[id].toolName + ": nothing grabbable.");
                Assert.IsEmpty(copy.GetComponentsInChildren<SuitTool>(true), real[id].toolName + ": no tool script.");
                Assert.IsEmpty(copy.GetComponentsInChildren<HoverHighlight>(true));
                foreach (var script in copy.GetComponentsInChildren<MonoBehaviour>(true))
                    Assert.IsTrue(script is TMP_Text || script is SoundOcclusion, real[id].toolName + " carries " + script.GetType().Name);
                foreach (var t in copy.GetComponentsInChildren<Transform>(true))
                    Assert.AreEqual(GameLayers.Hand, t.gameObject.layer, t.name + " is on layer " + LayerMask.LayerToName(t.gameObject.layer));

                // Same look: every mesh and material of the real tool is on the copy.
                var copyParts = copy.GetComponentsInChildren<MeshFilter>(true).Select(f => (f.sharedMesh, f.GetComponent<MeshRenderer>().sharedMaterial)).ToList();
                foreach (var f in real[id].GetComponentsInChildren<MeshFilter>(true))
                {
                    if (f.GetComponent<TMP_Text>() != null) continue; // text meshes are made per object at runtime
                    var part = (f.sharedMesh, f.GetComponent<MeshRenderer>().sharedMaterial);
                    Assert.IsTrue(copyParts.Contains(part), real[id].toolName + " part " + f.name + " is missing on the copy.");
                }
            }
            foreach (var t in crew.GetComponentsInChildren<Transform>(true))
                Assert.IsTrue(t.gameObject.layer != GameLayers.Tool && t.gameObject.layer != GameLayers.Salvage && t.gameObject.layer != GameLayers.CutPoint, t.name);
            Assert.AreEqual(0, crew.GetComponentsInChildren<Light>(true).Count(l => l.enabled), "No light is on before anything is lit.");
            // Co-op revive: the downed-suit locator is the one light outside the budget (off unless that player is down).
            var budgeted = crew.GetComponentsInChildren<Light>(true).Where(l => crew.beacon == null || l != crew.beacon.lamp).ToArray();
            Assert.AreEqual(2, budgeted.Length, "Two lights on a stand-in: the flashlight's spot and the helmet lamp's, from one budget (the cutter has a glow instead).");
            Assert.IsTrue(budgeted.All(l => l == rig.flashlightSpot || l == rig.headLamp.spot));
            Assert.AreEqual(LightShadows.None, rig.flashlightSpot.shadows);
            int renderers = 0, alwaysOn = 0;
            for (byte id = 1; id <= 4; id++)
            {
                foreach (var r in rig.Tool(id).GetComponentsInChildren<Renderer>(true))
                {
                    renderers++;
                    if (r.gameObject.activeInHierarchy && r.enabled) alwaysOn++;
                }
            }
            Debug.Log("[RemoteTools] renderers on one stand-in's tools: " + renderers + " (" + alwaysOn + " drawn when nothing is lit)");
            Assert.LessOrEqual(renderers, 70, "A stand-in's tools stay within a modest renderer count.");
            Assert.IsFalse(crew.context.tools != null && crew.context.tools.Length > 0, "The stand-in's player has no tools the game would act on.");
        }

        // ------------------------------------------------------------------ flashlight

        [UnityTest]
        public IEnumerator FlashlightFlag_ShowsTheBeam_AndOnlyTheNearestTwoGetARealLight_WithHysteresis()
        {
            yield return null;
            m_Viewer = new GameObject("Test Viewer");
            m_Viewer.transform.position = new Vector3(0f, 1.6f, -30f);
            RemoteToolRig.ViewerOverride = m_Viewer.transform;
            var crews = new[] { SpawnStandIn(1), SpawnStandIn(2), SpawnStandIn(3) };
            float[] distance = { 2f, 4f, 6f };
            void Place(int i, float d, bool on)
            {
                var s = Pose(new Vector3(i * 0.01f, 1.6f, -30f + d), 180f);
                s.flags = on ? AvatarFlags.FlashlightOn : (byte)0;
                crews[i].Apply(in s);
            }
            for (int i = 0; i < 3; i++) Place(i, distance[i], false);
            yield return null;
            Assert.IsTrue(crews.All(c => !c.tools.flashlightBeam.activeSelf && !c.tools.HasRealLight), "Off: no beam, no light.");

            for (int i = 0; i < 3; i++) Place(i, distance[i], true);
            yield return null;
            yield return null;
            Assert.IsTrue(crews.All(c => c.tools.FlashlightOn && c.tools.flashlightBeam.activeSelf), "Every lit flashlight shows its beam.");
            Assert.IsTrue(crews[0].tools.HasRealLight && crews[1].tools.HasRealLight, "The two nearest light the walls.");
            Assert.IsFalse(crews[2].tools.HasRealLight, "The third keeps only its cone.");
            Assert.AreEqual(2, crews.Count(c => c.tools.HasRealLight));
            var block = new MaterialPropertyBlock();
            crews[2].tools.flashlightLens.GetPropertyBlock(block);
            Assert.Greater(block.GetColor("_EmissionColor").r, 1f, "The lens is lit.");

            // A little closer than the second is not enough to take its light (no flicker between near equals)...
            Place(2, 3.6f, true);
            yield return null;
            yield return null;
            Assert.IsFalse(crews[2].tools.HasRealLight, "Hysteresis: 3.6 m does not beat a lit 4 m.");
            // ...clearly closer is.
            Place(2, 1f, true);
            yield return null;
            yield return null;
            Assert.IsTrue(crews[2].tools.HasRealLight);
            Assert.IsTrue(crews[0].tools.HasRealLight);
            Assert.IsFalse(crews[1].tools.HasRealLight, "The farthest lit one gave its light up.");

            RemoteToolRig.MaxRealLights = 1;
            yield return null;
            Assert.AreEqual(1, crews.Count(c => c.tools.HasRealLight), "The budget is respected when it changes.");

            Place(2, 1f, false);
            yield return null;
            Assert.IsFalse(crews[2].tools.flashlightBeam.activeSelf);
            Assert.IsFalse(crews[2].tools.HasRealLight, "Switched off: beam and light go.");
            crews[2].tools.flashlightLens.GetPropertyBlock(block);
            Assert.AreEqual(Color.black, block.GetColor("_EmissionColor"));
        }

        // ------------------------------------------------------------------ cutter

        [UnityTest]
        public IEnumerator CutterFlags_ShowFlameGlowAndPositionalSound_AndSparksOnlyOnAWeld()
        {
            yield return null;
            var crew = SpawnStandIn(1);
            var rig = crew.tools;
            var s = Pose(new Vector3(0f, 1.6f, -20f));
            s.rightTool = AvatarSampler.Cutter;
            crew.Apply(in s);
            yield return null;
            Assert.IsFalse(rig.cutterFlame.activeSelf);
            Assert.IsFalse(rig.cutterGlow.activeSelf);
            Assert.IsFalse(rig.LoopOn);

            s.flags = AvatarFlags.CutterFiring;
            crew.Apply(in s);
            int sparksBefore = rig.SparksEmitted;
            for (int i = 0; i < 5; i++) yield return null;
            Assert.IsTrue(rig.cutterFlame.activeSelf, "The flame shows at the nozzle.");
            Assert.IsTrue(rig.cutterGlow.activeSelf, "...with its glow.");
            Assert.IsTrue(rig.LoopOn, "The cutter loop plays.");
            Assert.AreEqual(sparksBefore, rig.SparksEmitted, "No sparks in the air: only on a weld.");
            var loop = rig.cutterLoop;
            Assert.AreEqual(1f, loop.spatialBlend, "The cutter is heard from where it is, not inside the helmet.");
            Assert.IsTrue(loop.transform.IsChildOf(rig.Tool(AvatarSampler.Cutter)), "From the cutter in that hand.");
            Assert.IsTrue(loop.transform.IsChildOf(crew.rightHand.transform));
            Assert.IsNotNull(loop.GetComponent<SoundOcclusion>(), "Walls muffle it.");
            Assert.IsNotNull(loop.GetComponent<AudioLowPassFilter>());
            Assert.AreEqual(s_Cutter().loopSource.clip, loop.clip, "The same sound as the real cutter.");

            s.flags = AvatarFlags.CutterFiring | AvatarFlags.Cutting;
            crew.Apply(in s);
            yield return new WaitForSeconds(0.2f);
            Assert.IsTrue(rig.Cutting);
            Assert.Greater(rig.SparksEmitted, sparksBefore, "Sparks fly at the tip on a weld.");
            Assert.Greater(rig.cutterSparks.particleCount, 0);
            Assert.Less(Vector3.Distance(rig.cutterSparks.transform.position, rig.cutterTip.position), 0.01f);

            s.flags = 0;
            crew.Apply(in s);
            yield return null;
            Assert.IsFalse(rig.cutterFlame.activeSelf);
            Assert.IsFalse(rig.cutterGlow.activeSelf);
            Assert.IsFalse(rig.LoopOn);
            Assert.IsFalse(rig.Cutting);

            // A tranquilizer shot: a brief tracer from that stand-in.
            rig.ShowTracer(rig.tranqMuzzle.position, rig.tranqMuzzle.position + Vector3.forward * 5f);
            Assert.IsTrue(rig.TracerShown);
            yield return new WaitForSeconds(rig.tracerSeconds + 0.1f);
            Assert.IsFalse(rig.TracerShown, "The tracer is brief.");
        }

        PlasmaCutterTool s_Cutter() => m_Player.tools.OfType<PlasmaCutterTool>().First();

        // ------------------------------------------------------------------ weld progress

        static Cuttable FirstPanel(MissionDirector director, out byte netId)
        {
            for (int i = 0; i < 256; i++)
            {
                var panel = director.PanelAt(i);
                if (panel == null) break;
                if (!panel.isTraining && panel.gameObject.activeInHierarchy && !panel.IsOpen && panel.cutPoints != null && panel.cutPoints.Length > 1) { netId = (byte)i; return panel; }
            }
            netId = 0;
            return null;
        }

        [UnityTest]
        public IEnumerator CutProgressFromACrewMate_ShowsOnTheWeld_AndNeverCompletesIt()
        {
            Assert.IsTrue(m_Net.Host());
            var bot = AddBot(m_Net.Transport.Local);
            SetBot(bot, new Vector3(0.4f, 1.6f, -1.6f));
            yield return Until(() => m_Net.Crew(1) != null && m_Net.Crew(1).HasPose);
            yield return Frames(2);
            var sync = m_Net.GetComponent<CoopToolSync>();
            Assert.IsNotNull(sync, "The first crew stand-in starts the weld progress sync.");
            var panel = FirstPanel(m_Director, out byte netId);
            Assert.IsNotNull(panel, "Test set-up: a contract panel with welds.");
            var point = panel.cutPoints[1];
            var marker = point.markerTransform;
            Vector3 scaleBefore = marker != null ? marker.localScale : Vector3.one;

            bot.Session.SendCutProgress(netId, 1, 128);
            yield return Until(() => sync.Applied > 0);
            Assert.AreEqual(0.5f, point.RemoteProgress, 0.01f, "The weld shows how far the crew mate has burnt it.");
            Assert.AreEqual(0.5f, point.DisplayProgress, 0.01f);
            Assert.AreEqual(0f, point.Progress, "This machine's own burn is untouched.");
            if (marker != null) Assert.Greater(marker.localScale.x, scaleBefore.x * 1.1f, "It glows and swells as a weld being cut does.");

            bot.Session.SendCutProgress(netId, 1, 255);
            int applied = sync.Applied;
            yield return Until(() => sync.Applied > applied);
            Assert.IsFalse(point.IsCut, "Progress never completes a weld: that is the reliable PointCut's job.");
            Assert.Less(point.DisplayProgress, 1f);
            Assert.AreEqual(0, panel.CutCount);
            Assert.IsFalse(panel.IsOpen);

            // Completion still comes through the reliable path, and progress after it changes nothing.
            Assert.IsTrue(panel.ApplyRemotePointCut(1, new PlayerId(1)));
            Assert.IsTrue(point.IsCut);
            Assert.IsFalse(panel.ApplyRemoteProgress(1, 0.3f), "A finished weld ignores progress.");
            Assert.AreEqual(1f, point.DisplayProgress);

            // A new run welds it back, progress and all.
            panel.ResetForRun();
            Assert.AreEqual(0f, point.RemoteProgress);
        }

        [UnityTest]
        public IEnumerator TheHostsOwnCutting_StreamsWeldProgressToTheCrew_AboutFiveTimesASecond()
        {
            Assert.IsTrue(m_Net.Host());
            var bot = AddBot(m_Net.Transport.Local);
            SetBot(bot, new Vector3(0.4f, 1.6f, -1.6f));
            yield return Until(() => m_Net.Crew(1) != null && m_Net.Crew(1).HasPose);
            var heard = new List<(byte slot, byte panel, byte index, byte progress)>();
            bot.Session.CutProgressReceived += (slot, panel, index, progress) => heard.Add((slot, panel, index, progress));
            var panel = FirstPanel(m_Director, out byte netId);
            var point = panel.cutPoints[0];

            // Draw the cutter into the right hand and hold its trigger (the grab and activate seams of the tool).
            var cutter = s_Cutter();
            var hand = m_Player.rightHand.interactor;
            hand.interactionManager.SelectEnter((IXRSelectInteractor)hand, (IXRSelectInteractable)cutter.grab);
            cutter.grab.activated.Invoke(new ActivateEventArgs());
            Assert.IsTrue(cutter.TriggerHeld, "Test set-up: trigger held.");
            for (int i = 0; i < 40 && !point.IsCut; i++)
            {
                // Keep the weld on the flame tip (the held cutter follows the hand).
                point.transform.position = cutter.tip.position;
                Physics.SyncTransforms();
                Tick();
                yield return null;
                if (cutter.IsCutting) Assert.AreSame(point, cutter.CurrentPoint, "The cutter reports the weld it is on.");
            }
            yield return Frames(5);
            Assert.Greater(heard.Count, 0, "The crew heard the host's weld progress.");
            Assert.IsTrue(heard.All(h => h.slot == 0 && h.panel == netId && h.index == 0), "From slot 0, on this weld.");
            Assert.IsTrue(heard.Last().progress > 0);
            float fakeSeconds = 45 * 0.02f;
            Assert.LessOrEqual(heard.Count, Mathf.CeilToInt(fakeSeconds / (float)CutProgressMsg.Interval) + 1, "At about 5 Hz, not every frame.");

            cutter.grab.deactivated.Invoke(new DeactivateEventArgs());
            hand.interactionManager.SelectExit((IXRSelectInteractor)hand, (IXRSelectInteractable)cutter.grab);
        }

        [UnityTest]
        public IEnumerator TheHostsDart_GoesToTheCrewAsATracer_AndAStandInCanDrawOne()
        {
            Assert.IsTrue(m_Net.Host());
            var bot = AddBot(m_Net.Transport.Local);
            SetBot(bot, new Vector3(0.4f, 1.6f, -1.6f), 0, 0, AvatarSampler.Tranq);
            yield return Until(() => m_Net.Crew(1) != null && m_Net.Crew(1).HasPose);
            yield return Frames(2);
            var sync = m_Net.GetComponent<CoopToolSync>();
            Assert.IsNotNull(sync);
            m_Director.Profile.Data.tranqDarts = 3;
            int before = bot.World.Calls[NetMsg.TranqTracer];
            var tranq = m_Player.tools.OfType<TranqTool>().First();
            tranq.Fire();
            Assert.AreEqual(1, sync.TracersSent, "The host's own shot goes to the crew.");
            yield return Until(() => bot.World.Calls[NetMsg.TranqTracer] > before);
            Assert.AreEqual(before + 1, bot.World.Calls[NetMsg.TranqTracer], "The crew mate's session heard the tracer.");

            // What a client does with it (the IClientWorld.OnTranqTracer hookup): the shooter's stand-in draws it.
            var muzzle = m_Net.Crew(1).tools.tranqMuzzle.position;
            Assert.IsTrue(CoopToolSync.ShowTranqTracer(m_Net, 1, muzzle.ToNet(), (muzzle + Vector3.forward * 4f).ToNet()));
            Assert.IsTrue(m_Net.Crew(1).tools.TracerShown);
            Assert.IsFalse(CoopToolSync.ShowTranqTracer(m_Net, 2, muzzle.ToNet(), muzzle.ToNet()), "Nobody in slot 2.");
        }

        // ------------------------------------------------------------------ what the local rig streams

        [UnityTest]
        public IEnumerator AvatarSampler_FillsHeldToolIds_FlashlightCutterAndHandednessFlags_FromTheLocalRig()
        {
            yield return null;
            var snapshot = AvatarSnapshot.Default;
            var flashlight = m_Player.tools.OfType<FlashlightTool>().First();
            var cutter = s_Cutter();
            var manager = m_Player.rightHand.interactor.interactionManager;
            flashlight.SetOn(false, false);
            AvatarSampler.Sample(m_Player, ref snapshot);
            Assert.AreEqual(AvatarSampler.NoTool, snapshot.leftTool);
            Assert.AreEqual(AvatarSampler.NoTool, snapshot.rightTool);
            Assert.IsFalse(snapshot.HasFlag(AvatarFlags.FlashlightOn));

            // Each tool in each hand.
            foreach (var tool in m_Player.tools)
            {
                byte id = tool is FlashlightTool ? AvatarSampler.Flashlight : tool is ScannerTool ? AvatarSampler.Scanner
                    : tool is PlasmaCutterTool ? AvatarSampler.Cutter : AvatarSampler.Tranq;
                foreach (var hand in new[] { m_Player.leftHand, m_Player.rightHand })
                {
                    manager.SelectEnter((IXRSelectInteractor)hand.interactor, (IXRSelectInteractable)tool.grab);
                    Assert.IsTrue(tool.IsHeld, "Test set-up: " + tool.toolName + " held.");
                    AvatarSampler.Sample(m_Player, ref snapshot);
                    bool left = hand == m_Player.leftHand;
                    Assert.AreEqual(left ? id : AvatarSampler.NoTool, snapshot.leftTool, tool.toolName + " in the " + (left ? "left" : "right"));
                    Assert.AreEqual(left ? AvatarSampler.NoTool : id, snapshot.rightTool, tool.toolName + " in the " + (left ? "left" : "right"));
                    Assert.AreEqual(1f, left ? snapshot.leftGripCurl : snapshot.rightGripCurl, "A hand holding a tool is a fist.");
                    manager.SelectExit((IXRSelectInteractor)hand.interactor, (IXRSelectInteractable)tool.grab);
                    yield return null;
                }
            }

            // Flashlight on (it shines from the holster too).
            flashlight.SetOn(true, false);
            AvatarSampler.Sample(m_Player, ref snapshot);
            Assert.IsTrue(snapshot.HasFlag(AvatarFlags.FlashlightOn));
            flashlight.SetOn(false, false);
            AvatarSampler.Sample(m_Player, ref snapshot);
            Assert.IsFalse(snapshot.HasFlag(AvatarFlags.FlashlightOn));

            // Cutter in the right hand, trigger held: firing; flame on a weld: cutting.
            var hand2 = m_Player.rightHand.interactor;
            manager.SelectEnter((IXRSelectInteractor)hand2, (IXRSelectInteractable)cutter.grab);
            cutter.grab.activated.Invoke(new ActivateEventArgs());
            yield return null;
            yield return null;
            AvatarSampler.Sample(m_Player, ref snapshot);
            Assert.IsTrue(cutter.IsFiring);
            Assert.IsTrue(snapshot.HasFlag(AvatarFlags.CutterFiring), "Firing is streamed.");
            Assert.AreEqual(AvatarSampler.Cutter, snapshot.rightTool);
            var panel = FirstPanel(m_Director, out _);
            var point = panel.cutPoints[0];
            bool sawCutting = false;
            for (int i = 0; i < 20 && !sawCutting; i++)
            {
                point.transform.position = cutter.tip.position;
                Physics.SyncTransforms();
                yield return null;
                AvatarSampler.Sample(m_Player, ref snapshot);
                sawCutting = snapshot.HasFlag(AvatarFlags.Cutting) && cutter.IsCutting;
            }
            Assert.IsTrue(sawCutting, "The flame on a weld is streamed as Cutting.");
            cutter.grab.deactivated.Invoke(new DeactivateEventArgs());
            yield return null;
            yield return null;
            AvatarSampler.Sample(m_Player, ref snapshot);
            Assert.IsFalse(snapshot.HasFlag(AvatarFlags.CutterFiring));
            Assert.IsFalse(snapshot.HasFlag(AvatarFlags.Cutting));
            manager.SelectExit((IXRSelectInteractor)hand2, (IXRSelectInteractable)cutter.grab);

            // Handedness.
            m_Player.SetHandedness(Handedness.Left);
            AvatarSampler.Sample(m_Player, ref snapshot);
            Assert.IsTrue(snapshot.HasFlag(AvatarFlags.LeftHanded));
            m_Player.SetHandedness(Handedness.Right);
            AvatarSampler.Sample(m_Player, ref snapshot);
            Assert.IsFalse(snapshot.HasFlag(AvatarFlags.LeftHanded));
        }

        // ------------------------------------------------------------------ lifetime

        [UnityTest]
        public IEnumerator RemovingTheStandIn_TakesItsToolsLightsAndSoundWithIt_AndClosingTheSessionEndsTheSync()
        {
            m_Viewer = new GameObject("Test Viewer");
            m_Viewer.transform.position = new Vector3(0.4f, 1.6f, -3f);
            RemoteToolRig.ViewerOverride = m_Viewer.transform;
            Assert.IsTrue(m_Net.Host());
            var bot = AddBot(m_Net.Transport.Local);
            SetBot(bot, new Vector3(0.4f, 1.6f, -1.6f), (byte)(AvatarFlags.FlashlightOn | AvatarFlags.CutterFiring), AvatarSampler.Flashlight, AvatarSampler.Cutter);
            yield return Until(() => m_Net.Crew(1) != null && m_Net.Crew(1).tools.HasRealLight && m_Net.Crew(1).tools.LoopOn);
            Assert.IsTrue(m_Net.Crew(1).tools.HasRealLight);
            Assert.AreEqual(1, RemoteToolRig.Live.Count);
            Assert.IsNotNull(m_Net.GetComponent<CoopToolSync>());

            bot.Session.Leave(Now);
            bot.Frozen = true;
            yield return Until(() => m_Net.Crew(1) == null);
            yield return null;
            Assert.AreEqual(0, RemoteToolRig.Live.Count, "No tool rig is left behind.");
            Assert.AreEqual(0, Object.FindObjectsByType<RemoteToolRig>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length);
            Assert.IsFalse(Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Any(l => l.name == "Beam" && l.GetComponentInParent<SuitTool>() == null),
                "No crew flashlight light remains.");

            Assert.IsTrue(m_Net.Leave());
            yield return Frames(2);
            Assert.IsNull(m_Net.GetComponent<CoopToolSync>(), "Closing the session ends the weld sync.");
        }

        [UnityTest]
        public IEnumerator SinglePlayer_NeverCreatesAnyOfThis_EvenWhileCuttingAndShining()
        {
            yield return null;
            var flashlight = m_Player.tools.OfType<FlashlightTool>().First();
            flashlight.SetOn(true, false);
            var cutter = s_Cutter();
            var hand = m_Player.rightHand.interactor;
            hand.interactionManager.SelectEnter((IXRSelectInteractor)hand, (IXRSelectInteractable)cutter.grab);
            cutter.grab.activated.Invoke(new ActivateEventArgs());
            var point = FirstPanel(m_Director, out _).cutPoints[0];
            for (int i = 0; i < 10; i++)
            {
                point.transform.position = cutter.tip.position;
                Physics.SyncTransforms();
                yield return null;
            }
            Assert.IsNull(m_Director.Net);
            Assert.IsTrue(m_Net.IsOffline);
            Assert.AreEqual(0, Object.FindObjectsByType<RemoteToolRig>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length, "No crew tools offline.");
            Assert.AreEqual(0, Object.FindObjectsByType<CoopToolSync>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length, "No weld sync offline.");
            Assert.AreEqual(0, RemoteToolRig.Live.Count);
            Assert.AreEqual(0f, point.RemoteProgress);
            cutter.grab.deactivated.Invoke(new DeactivateEventArgs());
            hand.interactionManager.SelectExit((IXRSelectInteractor)hand, (IXRSelectInteractable)cutter.grab);
        }
    }
}

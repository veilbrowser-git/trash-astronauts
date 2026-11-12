using System.Collections.Generic;
using TMPro;
using TrashAstronauts.Audio;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.Tools;
using TrashAstronauts.World;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// The suit tools on another crew member's stand-in (RemoteToolRig on the RemoteCrew prefab). Each copy is built
    /// by the very same BuildFlashlight / BuildScanner / BuildCutter / BuildTranq as the player's own tool and then
    /// stripped to its looks by <see cref="StripToVisual"/>. Hand attach points and holster positions are read from
    /// the PlayerRig prefab built just before, so a tool sits in a crew mate's glove and on their suit exactly where
    /// it sits for its owner.
    /// </summary>
    public static partial class Builder
    {
        const string RemoteAdditiveMaterialName = "RemoteAdditive";

        static RemoteToolRig BuildRemoteTools(GameObject root, RemoteAvatar remote, PlayerAvatar suit)
        {
            RemoteAdditiveMaterial();
            var rig = root.AddComponent<RemoteToolRig>();
            rig.avatar = remote;
            var local = s_RigPrefab != null ? s_RigPrefab.GetComponent<PlayerContext>() : null;

            // --- where a held tool's grip sits: the local rig's grab interactor attach point, in controller space
            rig.leftAttach = RemoteAttach(remote.leftHand, remote.leftGlove, local != null ? local.leftHand : null, "Left");
            rig.rightAttach = RemoteAttach(remote.rightHand, remote.rightGlove, local != null ? local.rightHand : null, "Right");

            // --- the copies
            var flashlight = BuildFlashlight(null);
            var scanner = BuildScanner(null);
            var cutter = BuildCutter(null);
            var tranq = BuildTranq(null);
            var real = new SuitTool[] { null, flashlight, scanner, cutter, tranq };
            for (int id = 1; id < RemoteToolRig.Slots; id++)
            {
                Transform grip = real[id].grab != null ? real[id].grab.attachTransform : null;
                rig.grips[id] = grip != null ? grip.localPosition : Vector3.zero;
                if (grip != null && Quaternion.Angle(grip.localRotation, Quaternion.identity) > 0.01f)
                    Debug.LogWarning("Remote tools: " + real[id].toolName + "'s grip is rotated; the in-hand pose ignores that.");
            }

            // Flashlight: the real spot light (off until the budget hands it out), its lens, and a soft cone.
            rig.flashlightLens = flashlight.lens;
            rig.flashlightSpot = flashlight.beam;
            flashlight.beam.enabled = false;
            var beam = Empty(flashlight.beam.transform, "Beam Cone", Vector3.zero, null, GameLayers.Hand);
            AddColoredMesh(beam, RemoteBeamMesh(), M(RemoteAdditiveMaterialName));
            var flare = Empty(beam.transform, "Lens Glow", new Vector3(0f, 0f, -0.002f), null, GameLayers.Hand);
            AddColoredMesh(flare, RemoteGlowMesh("RemoteGlowWarm", 0.045f, new Color(1f, 0.95f, 0.8f, 0.7f)), M(RemoteAdditiveMaterialName));
            beam.SetActive(false);
            rig.flashlightBeam = beam;

            // Cutter: flame and sparks are the real ones; its point light is swapped for a glow (no light cost).
            rig.cutterTip = cutter.tip;
            rig.cutterFlame = cutter.flame;
            rig.cutterSparks = cutter.sparks;
            rig.cutterLoop = cutter.loopSource;
            if (cutter.flameLight != null) Object.DestroyImmediate(cutter.flameLight.gameObject);
            var glow = Empty(cutter.transform, "Flame Glow", cutter.tip.localPosition + new Vector3(0f, 0f, 0.012f), null, GameLayers.Hand);
            AddColoredMesh(glow, RemoteGlowMesh("RemoteGlowBlue", 0.07f, new Color(0.45f, 0.75f, 1f, 0.8f)), M(RemoteAdditiveMaterialName));
            glow.SetActive(false);
            rig.cutterGlow = glow;

            // Scanner: a lit screen while it is in a hand.
            Transform screen = scanner.screen != null ? scanner.screen.transform.parent : scanner.transform;
            var lit = Prim(screen, "Lit Screen", PrimitiveType.Cube, new Vector3(0f, 0f, -0.0007f), new Vector3(0.118f, 0.073f, 0.0002f), M("ScreenGlow"));
            lit.SetActive(false);
            rig.scannerScreenLit = lit;
            rig.scannerText = scanner.screen;

            // Tranquilizer: its tracer line, for a shot this crew mate fires.
            rig.tranqTracer = tranq.tracer;
            rig.tranqMuzzle = tranq.muzzle;

            // --- holsters on the suit's torso, where the PlayerRig's holsters sit on its body anchor
            BuildRemoteHolsters(rig, remote, suit, local, real);

            string[] names = { null, "Flashlight", "Scanner", "Plasma Cutter", "Tranquilizer" };
            for (int id = 1; id < RemoteToolRig.Slots; id++)
            {
                var copy = StripToVisual(real[id].gameObject);
                copy.name = "Visual " + names[id];
                rig.tools[id] = copy.transform;
            }
            // Occlusion is added after the strip (it is a script, but one that only changes what is heard).
            AddOcclusion(rig.cutterLoop);

            for (int id = 1; id < RemoteToolRig.Slots; id++)
            {
                var socket = rig.holsters[id];
                if (socket == null) continue;
                rig.tools[id].SetParent(socket, false);
                rig.tools[id].localPosition = Vector3.zero;
                rig.tools[id].localRotation = Quaternion.identity;
            }
            return rig;
        }

        /// <summary>An attach point on a remote hand at the same controller-space pose as the local rig's, kept under the glove so it hides with it.</summary>
        static Transform RemoteAttach(HandRig remoteHand, GloveVisual glove, HandRig localHand, string side)
        {
            Vector3 position = new Vector3(0f, -0.035f, -0.045f); // BuildHand: grab interactor plus its attach offset
            Quaternion rotation = Quaternion.identity;
            if (localHand != null && localHand.interactor != null && localHand.interactor.attachTransform != null)
            {
                Transform attach = localHand.interactor.attachTransform;
                position = localHand.transform.InverseTransformPoint(attach.position);
                rotation = Quaternion.Inverse(localHand.transform.rotation) * attach.rotation;
            }
            var go = Empty(remoteHand.transform, side + " Tool Attach", position, rotation, GameLayers.Hand);
            if (glove != null) go.transform.SetParent(glove.transform, true);
            return go.transform;
        }

        static void BuildRemoteHolsters(RemoteToolRig rig, RemoteAvatar remote, PlayerAvatar suit, PlayerContext local, SuitTool[] copies)
        {
            Transform torso = suit != null && suit.torso != null ? suit.torso : remote.transform;
            Vector3 head = remote.head != null ? remote.head.position : new Vector3(0f, 1.6f, 0f);
            float chestDrop = local != null && local.bodyAnchor != null ? local.bodyAnchor.chestDrop : 0.38f;
            Vector3 anchor = head + Vector3.down * chestDrop;
            for (int id = 1; id < RemoteToolRig.Slots; id++)
            {
                // The same tool on the local rig: its holster's place on the body anchor (right-handed) and its dock turn.
                ToolHolster holster = null;
                if (local != null && local.tools != null)
                {
                    foreach (var tool in local.tools)
                        if (tool != null && tool.GetType() == copies[id].GetType()) holster = tool.holster;
                }
                Vector3 offset = holster != null ? holster.transform.localPosition : Vector3.zero;
                Quaternion dock = holster != null && holster.socket != null ? holster.socket.localRotation : Quaternion.identity;
                var socket = Empty(torso, "Holster " + copies[id].toolName, Vector3.zero, null, GameLayers.Hand);
                rig.holsterRight[id] = torso.InverseTransformPoint(anchor + offset);
                rig.holsterLeft[id] = torso.InverseTransformPoint(anchor + new Vector3(-offset.x, offset.y, offset.z));
                socket.transform.localPosition = rig.holsterRight[id];
                socket.transform.rotation = dock; // the anchor faces +Z at build time, as the suit does
                rig.holsters[id] = socket.transform;
            }
        }

        /// <summary>
        /// Strips a freshly built tool down to what it looks and sounds like: every script except text and sound
        /// occlusion, every collider and the rigidbody go, and everything moves to the Hand layer (off the Tool layer).
        /// </summary>
        static GameObject StripToVisual(GameObject go)
        {
            // Scripts that need others go first: the tool script (needs the rigidbody), then the highlight, then the grab.
            foreach (var c in go.GetComponentsInChildren<SuitTool>(true)) Object.DestroyImmediate(c);
            foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (!(c is TMP_Text) && !(c is SoundOcclusion) && !(c is XRBaseInteractable)) Object.DestroyImmediate(c);
            foreach (var c in go.GetComponentsInChildren<XRBaseInteractable>(true)) Object.DestroyImmediate(c);
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
            foreach (var c in go.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(c);
            GameLayers.SetLayerRecursively(go, GameLayers.Hand);
            return go;
        }

        static void AddColoredMesh(GameObject go, Mesh mesh, Material material)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            Cheap(r);
        }

        /// <summary>Additive, unlit, double-sided, coloured by vertex colour: the beam cone and the glows.</summary>
        static Material RemoteAdditiveMaterial()
        {
            string path = MaterialDir + "/" + RemoteAdditiveMaterialName + ".mat";
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }
            else m.shader = shader;
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 2f); // additive
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.One);
            m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            m.SetFloat("_DstBlendAlpha", (float)BlendMode.One);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_Cull", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            EditorUtility.SetDirty(m);
            s_Mats[RemoteAdditiveMaterialName] = m;
            return m;
        }

        /// <summary>
        /// The visible beam of a crew mate's flashlight: two nested open cones along +Z from the lens, a narrow brighter
        /// core and a wide faint skirt, fading to nothing over 2.6 m (alpha in the vertex colour).
        /// </summary>
        static Mesh RemoteBeamMesh()
        {
            var verts = new List<Vector3>();
            var colors = new List<Color>();
            var tris = new List<int>();
            const float length = 2.6f;
            const int rings = 8, segments = 14;
            void ConeShell(float startRadius, float halfAngle, float alpha)
            {
                float spread = Mathf.Tan(halfAngle * Mathf.Deg2Rad);
                int start = verts.Count;
                for (int j = 0; j < rings; j++)
                {
                    float t = j / (float)(rings - 1);
                    float z = length * t;
                    float r = startRadius + z * spread;
                    float a = alpha * Mathf.Pow(1f - t, 1.7f);
                    for (int i = 0; i < segments; i++)
                    {
                        float angle = i / (float)segments * Mathf.PI * 2f;
                        verts.Add(new Vector3(Mathf.Cos(angle) * r, Mathf.Sin(angle) * r, z));
                        colors.Add(new Color(1f, 0.95f, 0.82f, a));
                    }
                }
                for (int j = 0; j < rings - 1; j++)
                    for (int i = 0; i < segments; i++)
                    {
                        int a0 = start + j * segments + i, a1 = start + j * segments + (i + 1) % segments;
                        int b0 = a0 + segments, b1 = a1 + segments;
                        tris.Add(a0); tris.Add(b0); tris.Add(a1);
                        tris.Add(a1); tris.Add(b0); tris.Add(b1);
                    }
            }
            ConeShell(0.02f, 9f, 0.12f);
            ConeShell(0.028f, 17f, 0.05f);
            return SaveColoredMesh("RemoteBeamCone", verts, colors, tris);
        }

        /// <summary>A soft glow: three crossed discs, bright in the middle and clear at the rim, so it reads from any side.</summary>
        static Mesh RemoteGlowMesh(string key, float radius, Color tint)
        {
            var verts = new List<Vector3>();
            var colors = new List<Color>();
            var tris = new List<int>();
            const int segments = 12;
            for (int plane = 0; plane < 3; plane++)
            {
                int centre = verts.Count;
                verts.Add(Vector3.zero);
                colors.Add(tint);
                for (int i = 0; i < segments; i++)
                {
                    float angle = i / (float)segments * Mathf.PI * 2f;
                    float c = Mathf.Cos(angle) * radius, s = Mathf.Sin(angle) * radius;
                    verts.Add(plane == 0 ? new Vector3(c, s, 0f) : plane == 1 ? new Vector3(0f, c, s) : new Vector3(c, 0f, s));
                    colors.Add(new Color(tint.r, tint.g, tint.b, 0f));
                }
                for (int i = 0; i < segments; i++)
                {
                    tris.Add(centre);
                    tris.Add(centre + 1 + i);
                    tris.Add(centre + 1 + (i + 1) % segments);
                }
            }
            return SaveColoredMesh(key, verts, colors, tris);
        }

        static Mesh SaveColoredMesh(string key, List<Vector3> verts, List<Color> colors, List<int> tris)
        {
            EnsureFolder(MeshDir);
            string path = MeshDir + "/" + key + ".asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            bool created = mesh == null;
            if (created) mesh = new Mesh { name = key };
            else mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetColors(colors);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            if (created) AssetDatabase.CreateAsset(mesh, path);
            else EditorUtility.SetDirty(mesh);
            return mesh;
        }
    }
}

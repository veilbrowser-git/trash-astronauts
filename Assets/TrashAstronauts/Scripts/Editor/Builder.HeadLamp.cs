using System.Collections.Generic;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.Tools;
using TrashAstronauts.UI;
using TrashAstronauts.World;
using UnityEditor;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// The helmet lamp: the light on the player's own helmet with its wrist button (PlayerRig), the lamp on a crew
    /// mate's helmet (RemoteCrew), and its place on the company store counter. Each is added by a one-line call from
    /// the builder that makes the thing it belongs to.
    ///
    /// The lamp housing on the suit's helmet (ModelSource/Player/build_player.py, "helmet_lamp") is on the right of the
    /// shell; its lens faces forward at (0.146, 0.075, 0.087) from the centre of the head (right, up, forward), and
    /// the centre of the head is <see cref="PlayerAvatar.headCentreOffset"/> from the eyes.
    /// </summary>
    public static partial class Builder
    {
        /// <summary>The lens of the helmet's lamp housing, from the centre of the head (right, up, forward).</summary>
        static readonly Vector3 k_HelmetLampLens = new Vector3(0.146f, 0.075f, 0.087f);
        /// <summary>The lamp points a touch down and in, so the middle of its pool is where the wearer looks a few metres on.</summary>
        static readonly Quaternion k_HelmetLampAim = Quaternion.Euler(5f, -2.5f, 0f);

        /// <summary>The player's own helmet lamp: a light under the camera at the lamp housing, and the LAMP button on the wrist.</summary>
        static HeadLamp BuildHeadLamp(PlayerContext context, Transform camera, Transform wrist)
        {
            Vector3 headCentre = context.avatar != null ? context.avatar.headCentreOffset : new Vector3(0f, 0f, -0.085f);
            var go = Empty(camera, "Head Lamp", headCentre + k_HelmetLampLens, k_HelmetLampAim, GameLayers.Player);
            var light = go.AddComponent<Light>();
            ConfigureHelmetLampLight(light);
            light.enabled = false;

            var lamp = go.AddComponent<HeadLamp>();
            lamp.player = context;
            lamp.beam = light;

            // On the wrist, toward the elbow from the readout, where the other hand's finger finds it.
            var button = Button(wrist, "Lamp Button", new Vector3(-0.098f, -0.013f, 0f), Quaternion.identity, new Vector2(0.056f, 0.07f), "LAMP\nOFF", M("ButtonYellow"), 0.1f);
            button.cooldown = 0.35f;
            button.gameObject.SetActive(false); // shown once the lamp is owned
            lamp.wristButton = button;
            return lamp;
        }

        /// <summary>Wider and dimmer than the hand flashlight, soft-edged, no shadows; the same kind of cookie.</summary>
        static void ConfigureHelmetLampLight(Light light)
        {
            light.type = LightType.Spot;
            light.spotAngle = 92f;
            light.innerSpotAngle = 40f;
            light.range = 11f;
            light.intensity = 5.5f;
            light.color = new Color(1f, 0.94f, 0.84f);
            light.cookie = HeadLampCookie();
            light.shadows = LightShadows.None;
            light.renderMode = LightRenderMode.Auto;
        }

        /// <summary>
        /// A crew mate's helmet lamp: a glow at the lens of their helmet's lamp housing, a wide soft cone, and a spot light
        /// that the shared remote light budget switches on for the nearest few.
        /// </summary>
        static void BuildRemoteHeadLamp(RemoteToolRig rig, PlayerAvatar suit)
        {
            if (rig == null || suit == null || suit.helmet == null) return;
            var go = Empty(suit.helmet, "Helmet Lamp", k_HelmetLampLens + new Vector3(0f, 0f, 0.004f), k_HelmetLampAim, GameLayers.Hand);
            var lamp = go.AddComponent<RemoteHeadLamp>();

            var spot = Empty(go.transform, "Spot", Vector3.zero, null, GameLayers.Hand).AddComponent<Light>();
            ConfigureHelmetLampLight(spot);
            spot.enabled = false;
            lamp.spot = spot;

            var glow = Empty(go.transform, "Lens Glow", Vector3.zero, null, GameLayers.Hand);
            AddColoredMesh(glow, RemoteGlowMesh("RemoteGlowLamp", 0.05f, new Color(1f, 0.93f, 0.78f, 0.75f)), M(RemoteAdditiveMaterialName));
            glow.SetActive(false);
            lamp.lensGlow = glow;

            var cone = Empty(go.transform, "Lamp Cone", new Vector3(0f, 0f, 0.003f), null, GameLayers.Hand);
            AddColoredMesh(cone, RemoteLampConeMesh(), M(RemoteAdditiveMaterialName));
            cone.SetActive(false);
            lamp.cone = cone;

            rig.headLamp = lamp;
        }

        /// <summary>The visible spill of a helmet lamp: one wide, very faint open cone along +Z, fading out over 1.8 m.</summary>
        static Mesh RemoteLampConeMesh()
        {
            var verts = new List<Vector3>();
            var colors = new List<Color>();
            var tris = new List<int>();
            const float length = 1.8f, halfAngle = 27f, startRadius = 0.022f, alpha = 0.014f;
            const int rings = 7, segments = 14;
            float spread = Mathf.Tan(halfAngle * Mathf.Deg2Rad);
            for (int j = 0; j < rings; j++)
            {
                float t = j / (float)(rings - 1);
                float z = length * t;
                float r = startRadius + z * spread;
                float a = alpha * Mathf.Pow(1f - t, 2.4f);
                for (int i = 0; i < segments; i++)
                {
                    float angle = i / (float)segments * Mathf.PI * 2f;
                    verts.Add(new Vector3(Mathf.Cos(angle) * r, Mathf.Sin(angle) * r, z));
                    colors.Add(new Color(1f, 0.94f, 0.82f, a));
                }
            }
            for (int j = 0; j < rings - 1; j++)
                for (int i = 0; i < segments; i++)
                {
                    int a0 = j * segments + i, a1 = j * segments + (i + 1) % segments;
                    int b0 = a0 + segments, b1 = a1 + segments;
                    tris.Add(a0); tris.Add(b0); tris.Add(a1);
                    tris.Add(a1); tris.Add(b0); tris.Add(b1);
                }
            return SaveColoredMesh("RemoteLampCone", verts, colors, tris);
        }

        /// <summary>
        /// Like the flashlight's cookie but broader and softer: an even pool that rolls off gently to the edge, a faint
        /// reflector ring and a little grain, so it reads as a lamp and not a disc.
        /// </summary>
        static Texture2D HeadLampCookie()
        {
            const string name = "HeadLampCookie";
            MakeTexture(name, 256, (px, n, rng) =>
            {
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float dx = (x + 0.5f) / n - 0.5f, dy = (y + 0.5f) / n - 0.5f;
                        float r = Mathf.Sqrt(dx * dx + dy * dy) * 2f;
                        float v = Mathf.Lerp(1f, 0.55f, Mathf.SmoothStep(0.05f, 0.75f, r));
                        v += 0.06f * Mathf.Exp(-Mathf.Pow((r - 0.5f) / 0.06f, 2f));
                        v *= 1f - Mathf.SmoothStep(0.6f, 0.98f, r);
                        v *= 0.96f + 0.04f * Mathf.PerlinNoise(x * 0.07f + 3f, y * 0.07f);
                        px[y * n + x] = new Color32(B(v), B(v), B(v), B(v));
                    }
            });
            string path = TextureDir + "/" + name + ".png";
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.sRGBTexture = false;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>
        /// Makes room on the store counter for a fourth display and puts the helmet lamp there: the three goods and their
        /// buttons close up into four columns, and the lamp turns on its plinth above a HELMET LAMP button.
        /// </summary>
        static HeadLampShopButton BuildHeadLampShelf(Transform stall, Transform goods, ShopPanel panel)
        {
            // Four columns across the 2.4 m counter.
            float[] columns = { -0.9f, -0.3f, 0.3f, 0.9f };
            var buttonSize = new Vector2(0.54f, 0.26f);
            string[] names = { "Tranq Darts", "Scanner Range", "Cutter Coolant" };
            PushButton[] buttons = { panel.tranqButton, panel.scannerButton, panel.cutterButton };
            for (int i = 0; i < names.Length; i++)
            {
                MoveX(goods.Find(names[i] + " Plinth"), columns[i]);
                MoveX(goods.Find(names[i]), columns[i]);
                if (buttons[i] != null)
                {
                    MoveX(buttons[i].transform, columns[i]);
                    ResizeButton(buttons[i], buttonSize, 0.24f);
                }
            }

            // The lamp on display: the suit's own helmet, small, turning on its plinth so the lamp on its side comes round,
            // the lens lit once the lamp is yours. (Stand-in shapes if the suit model is missing.)
            float x = columns[3];
            Part(goods, "Helmet Lamp Plinth", Lathe("ShopPlinth", 16, new Vector2(0.17f, 0f), new Vector2(0.15f, 0.04f), new Vector2(0.12f, 0.05f)), new Vector3(x, 0, 0), M("Steel"));
            var turn = Empty(goods, "Helmet Lamp", new Vector3(x, 0.26f, 0), null, GameLayers.Default);
            turn.AddComponent<SlowSpin>();
            Transform t = turn.transform;
            const float scale = 0.72f;
            var model = LoadAvatarModel();
            GameObject lens;
            if (model != null && model.Has("helmet"))
            {
                var scaled = Empty(t, "Scale", new Vector3(0f, -0.05f, 0f), Quaternion.Euler(-8f, 0f, 0f), GameLayers.Default);
                scaled.transform.localScale = Vector3.one * scale;
                var renderers = new List<Renderer>();
                AvatarAttach(model, Empty(scaled.transform, "Helmet", Vector3.zero, null, GameLayers.Default), "helmet", 1f, renderers, renderers, renderers);
                lens = Part(scaled.transform, "Lamp Lens", Tube(0.019f, 0.004f, 16), k_HelmetLampLens + new Vector3(0f, 0f, 0.002f), M("Lens"), new Vector3(90, 0, 0));
            }
            else
            {
                var dome = Prim(t, "Helmet Dome", PrimitiveType.Sphere, new Vector3(0f, -0.05f, 0f), new Vector3(0.23f, 0.24f, 0.25f), M("ToolBody"));
                Object.DestroyImmediate(dome.GetComponent<Collider>());
                CBox(t, "Lamp Housing", new Vector3(0.105f, -0.0f, 0.03f), new Vector3(0.035f, 0.04f, 0.065f), M("PaintOrange"), 0.008f);
                lens = Part(t, "Lamp Lens", Tube(0.015f, 0.004f, 16), new Vector3(0.105f, 0f, 0.064f), M("Lens"), new Vector3(90, 0, 0));
            }
            // The slogan behind the goods moves up out of their way now there are four.
            var slogan = stall.Find("Slogan");
            if (slogan != null) slogan.localPosition = new Vector3(slogan.localPosition.x, 1.56f, slogan.localPosition.z);

            var button = Button(stall, "Buy Helmet Lamp", new Vector3(x, 0.5f, -0.325f), Quaternion.identity, buttonSize, "HELMET LAMP", M("ButtonYellow"), 0.24f);
            var shop = panel.gameObject.AddComponent<HeadLampShopButton>();
            shop.director = panel.director;
            shop.button = button;
            shop.displayLens = lens.GetComponent<MeshRenderer>();
            return shop;
        }

        static void MoveX(Transform t, float x)
        {
            if (t == null) return;
            var p = t.localPosition;
            t.localPosition = new Vector3(x, p.y, p.z);
        }

        /// <summary>Resizes a button made by <see cref="Button"/>: its base, cap, collider and label area.</summary>
        static void ResizeButton(PushButton button, Vector2 size, float fontSize)
        {
            Transform t = button.transform;
            var baseTf = t.Find("Base");
            if (baseTf != null) baseTf.localScale = new Vector3(size.x + 0.03f, size.y + 0.03f, baseTf.localScale.z);
            if (button.cap != null) button.cap.localScale = new Vector3(size.x, size.y, button.cap.localScale.z);
            var col = button.GetComponent<BoxCollider>();
            if (col != null) col.size = new Vector3(size.x, size.y, col.size.z);
            if (button.label != null)
            {
                button.label.rectTransform.sizeDelta = size;
                button.label.fontSize = fontSize;
            }
        }
    }
}

using System.Collections.Generic;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.Tools;
using TrashAstronauts.UI;
using TrashAstronauts.World;
using UnityEditor;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    public static partial class PreviewRenderer
    {
        /// <summary>
        /// The helmet lamp: on the store counter, lighting the corridor in first person with every other light out,
        /// and on a crew mate's helmet.
        /// </summary>
        static void HeadLampShots()
        {
            // 1. The store with its fourth column. Labels as a player with 400 cr would see them.
            var shop = Object.FindFirstObjectByType<ShopPanel>();
            var lampShop = shop != null ? shop.GetComponent<HeadLampShopButton>() : null;
            if (shop != null && lampShop != null)
            {
                var saved = new List<(PushButton, string)>();
                void Label(PushButton b, string text)
                {
                    if (b == null) return;
                    saved.Add((b, b.Text));
                    b.SetLabel(text);
                }
                Label(shop.tranqButton, "TRANQ DART\nsleeps it 8s - 120 cr");
                Label(shop.scannerButton, "SCANNER RANGE+\nx1.6 range - 300 cr");
                Label(shop.cutterButton, "CUTTER COOLANT\nNeed 350 cr");
                Label(lampShop.button, "HELMET LAMP\nhands free - 250 cr");
                Shot("63_store_helmet_lamp", new Vector3(-2.7f, 1.6f, -4.5f), new Vector3(-5.4f, 1.2f, -4.6f), false, 70f);
                Shot("63a_store_helmet_lamp_closeup", new Vector3(-4.35f, 1.45f, -4.3f), new Vector3(-5.42f, 1.12f, -3.9f), false, 55f);
                foreach (var (b, text) in saved) b.SetLabel(text);
            }

            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Builder.PrefabDir + "/PlayerRig.prefab");
            var remotePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Builder.PrefabDir + "/" + Builder.RemoteCrewPrefabName + ".prefab");
            if (rigPrefab == null) return;

            // Every light in the scene out: what is lit is the helmet lamp's doing (and the scene's faint ambient).
            var lights = new List<Light>();
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.enabled) { l.enabled = false; lights.Add(l); }

            Vector3 eye = Builder.FreighterPoint(0f, 1.6f, 9.4f);
            Vector3 look = Builder.FreighterPoint(0.15f, 1.0f, 15f);
            var rig = Object.Instantiate(rigPrefab, eye - new Vector3(0f, 1.6f, 0f), Quaternion.LookRotation(Vector3.ProjectOnPlane(look - eye, Vector3.up)));
            rig.hideFlags = HideFlags.HideAndDontSave;
            foreach (var cam in rig.GetComponentsInChildren<Camera>(true)) cam.enabled = false;
            foreach (var listener in rig.GetComponentsInChildren<AudioListener>(true)) listener.enabled = false;
            foreach (var l in rig.GetComponentsInChildren<Light>(true)) l.enabled = false;
            var context = rig.GetComponent<PlayerContext>();
            context.head.position = eye;
            context.head.rotation = Quaternion.LookRotation(look - eye, Vector3.up);
            // Hands up in front, as when reaching for something: they must not glare in the lamp.
            foreach (var hand in new[] { context.leftHand, context.rightHand })
            {
                bool left = hand == context.leftHand;
                hand.transform.position = context.head.TransformPoint(new Vector3(left ? -0.18f : 0.2f, -0.28f, 0.38f));
                hand.transform.rotation = context.head.rotation * Quaternion.Euler(-15f, left ? 10f : -10f, 0f);
                hand.GetComponentInChildren<GloveVisual>(true).Apply(0.2f, 0.4f);
            }
            if (context.avatar != null)
            {
                context.avatar.SnapToHead();
                context.avatar.Pose(0f);
            }
            var lamp = rig.GetComponentInChildren<HeadLamp>(true);
            HeadLamp.OwnedOverride = true;
            if (lamp != null) lamp.SetOn(true, false);
            Shot("64_helmet_lamp_corridor", eye, look, false, 90f);
            // Turned toward the wall a couple of metres away: the pool on the plating.
            Vector3 wall = Builder.FreighterPoint(1.4f, 1.2f, 11.6f);
            context.head.rotation = Quaternion.LookRotation(wall - eye, Vector3.up);
            Shot("64b_helmet_lamp_wall", eye, wall, false, 90f);
            context.head.rotation = Quaternion.LookRotation(look - eye, Vector3.up);
            // The same view lit by the hand flashlight, for comparison (the lamp is wider and dimmer).
            var torch = rig.GetComponentInChildren<FlashlightTool>(true);
            if (lamp != null) lamp.SetOn(false, false);
            if (torch != null && torch.beam != null)
            {
                var beam = torch.beam;
                var parent = beam.transform.parent;
                beam.transform.SetParent(null, true);
                beam.transform.SetPositionAndRotation(context.head.TransformPoint(new Vector3(0.2f, -0.35f, 0.25f)), Quaternion.LookRotation(look - context.head.TransformPoint(new Vector3(0.2f, -0.35f, 0.25f))));
                beam.enabled = true;
                Shot("64a_flashlight_corridor_compare", eye, look, false, 90f);
                beam.enabled = false;
                beam.transform.SetParent(parent, true);
            }
            HeadLamp.OwnedOverride = null;
            Object.DestroyImmediate(rig);

            // 2. A crew mate with the helmet lamp on, down the corridor, facing us.
            if (remotePrefab != null)
            {
                Vector3 theirHead = Builder.FreighterPoint(0.3f, 1.62f, 12.6f);
                Vector3 viewer = Builder.FreighterPoint(-0.35f, 1.6f, 9.6f);
                var go = Object.Instantiate(remotePrefab);
                go.hideFlags = HideFlags.HideAndDontSave;
                var remote = go.GetComponent<RemoteAvatar>();
                remote.Setup(new Core.PlayerId(1), 0);
                Quaternion face = Quaternion.LookRotation(Vector3.ProjectOnPlane(viewer - theirHead, Vector3.up) + Vector3.down * 0.4f, Vector3.up);
                var s = AvatarSnapshot.Default;
                s.headPosition = NetUnity.ToNet(theirHead);
                s.headRotation = NetUnity.ToNet(face);
                s.leftHandPosition = NetUnity.ToNet(theirHead + face * new Vector3(-0.25f, -0.55f, 0.15f));
                s.leftHandRotation = NetUnity.ToNet(face);
                s.rightHandPosition = NetUnity.ToNet(theirHead + face * new Vector3(0.25f, -0.55f, 0.15f));
                s.rightHandRotation = NetUnity.ToNet(face);
                s.flags = AvatarFlags.HeadLampOn;
                remote.Apply(in s);
                remote.suit.SnapToHead();
                remote.suit.Pose(0f);
                if (remote.tools != null && remote.tools.headLamp != null) remote.tools.headLamp.SetRealLight(true);
                Shot("65_remote_helmet_lamp", viewer, theirHead + Vector3.down * 0.35f, false, 70f);
                Vector3 side = theirHead + face * new Vector3(0.95f, 0.15f, 0.2f);
                Shot("65a_remote_helmet_lamp_closeup", side, theirHead + face * new Vector3(0.1f, -0.05f, 0.05f), false, 50f);
                Object.DestroyImmediate(go);
            }

            foreach (var l in lights) if (l != null) l.enabled = true;
        }
    }
}

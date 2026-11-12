using System.Collections.Generic;
using TrashAstronauts.Core;
using TrashAstronauts.Core.Net;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.UI;
using UnityEditor;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    public static partial class PreviewRenderer
    {
        /// <summary>
        /// Co-op revive: a downed crew mate hanging limp in the freighter with the chest beacon half filled (lit, and
        /// with every light out, as after a jumpscare), the beacon close up, the same suit lying on the cave floor, and
        /// the downed player's own dimmed view with the cracked visor and the wrist line.
        /// </summary>
        static void ReviveShots()
        {
            var remotePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Builder.PrefabDir + "/" + Builder.RemoteCrewPrefabName + ".prefab");
            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Builder.PrefabDir + "/PlayerRig.prefab");
            if (remotePrefab == null || rigPrefab == null) return;

            RemoteAvatar Downed(Vector3 head, Quaternion face, float progress)
            {
                var go = Object.Instantiate(remotePrefab);
                go.hideFlags = HideFlags.HideAndDontSave;
                var remote = go.GetComponent<RemoteAvatar>();
                remote.Setup(new PlayerId(2), 0);
                var s = AvatarSnapshot.Default;
                s.headPosition = NetUnity.ToNet(head);
                s.headRotation = NetUnity.ToNet(face);
                s.leftHandPosition = NetUnity.ToNet(head + face * new Vector3(-0.25f, -0.5f, 0.2f));
                s.leftHandRotation = NetUnity.ToNet(face);
                s.rightHandPosition = NetUnity.ToNet(head + face * new Vector3(0.25f, -0.5f, 0.2f));
                s.rightHandRotation = NetUnity.ToNet(face);
                remote.Apply(in s);
                remote.suit.SnapToHead();
                remote.suit.Pose(0f);
                remote.context.SetDowned(true, head);
                remote.ApplyDownedState();
                remote.context.ReviveProgress = progress;
                remote.ApplyDownedState();
                if (remote.beacon != null)
                {
                    remote.beacon.SetProgress(progress);
                    if (remote.beacon.halo != null) remote.beacon.halo.localScale = Vector3.one * 1.1f;
                }
                return remote;
            }

            // 1. Zero-g, in the corridor, facing us: lit as the wreck is, then with every light out.
            Vector3 theirHead = Builder.FreighterPoint(0.3f, 1.55f, 12.6f);
            Vector3 viewer = Builder.FreighterPoint(-0.4f, 1.6f, 9.8f);
            Quaternion face = Quaternion.LookRotation(Vector3.ProjectOnPlane(viewer - theirHead, Vector3.up), Vector3.up);
            var floating = Downed(theirHead, face, 0.5f);
            Shot("66_revive_downed_zero_g", viewer, theirHead + Vector3.down * 0.45f, false, 70f);
            var lights = new List<Light>();
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.enabled && (floating.beacon == null || l != floating.beacon.lamp)) { l.enabled = false; lights.Add(l); }
            Shot("66a_revive_downed_in_the_dark", viewer, theirHead + Vector3.down * 0.45f, false, 70f);
            foreach (var l in lights) if (l != null) l.enabled = true;
            Vector3 beacon = floating.BeaconPosition;
            Shot("66b_revive_beacon_closeup", beacon + face * new Vector3(0.15f, 0.12f, 0.55f), beacon, false, 50f);
            Object.DestroyImmediate(floating.gameObject);

            // 2. The cave: lying on its back on the floor, chest over where it was caught.
            var director = Object.FindFirstObjectByType<MissionDirector>();
            int cave = director != null && director.maps != null ? System.Array.FindIndex(director.maps, m => m.mapId == "lava_tube") : -1;
            if (cave >= 0)
            {
                var active = new List<GameObject>();
                for (int i = 0; i < director.maps.Length; i++)
                {
                    if (director.maps[i].gameObject.activeSelf) active.Add(director.maps[i].gameObject);
                    director.maps[i].gameObject.SetActive(i == cave);
                }
                Physics.SyncTransforms();
                // Gravity volumes register themselves when enabled in play; in the editor do it by hand for this shot.
                var volumes = director.maps[cave].GetComponentsInChildren<MapGravity>(true);
                foreach (var volume in volumes) volume.SendMessage("OnEnable", SendMessageOptions.DontRequireReceiver);
                Vector3 caveHead = new Vector3(0f, 1.6f, 24f);
                Vector3 caveViewer = caveHead + new Vector3(1.4f, 0.2f, -1.8f);
                var lying = Downed(caveHead, Quaternion.LookRotation(Vector3.forward), 0.25f);
                Shot("67_revive_downed_cave_floor", caveViewer, caveHead + Vector3.down * 1.3f, false, 70f);
                Object.DestroyImmediate(lying.gameObject);
                foreach (var volume in volumes) volume.SendMessage("OnDisable", SendMessageOptions.DontRequireReceiver);
                for (int i = 0; i < director.maps.Length; i++) director.maps[i].gameObject.SetActive(active.Contains(director.maps[i].gameObject));
            }

            // 3. The downed player's own view: dimmed and red at the edges, visor cracked, the wrist line readable.
            Vector3 eye = Builder.FreighterPoint(0f, 1.6f, 12.2f);
            Vector3 look = Builder.FreighterPoint(0.3f, 1.0f, 15.5f);
            var rig = Object.Instantiate(rigPrefab, eye - new Vector3(0f, 1.6f, 0f), Quaternion.LookRotation(Vector3.ProjectOnPlane(look - eye, Vector3.up)));
            rig.hideFlags = HideFlags.HideAndDontSave;
            foreach (var cam in rig.GetComponentsInChildren<Camera>(true)) cam.enabled = false;
            foreach (var listener in rig.GetComponentsInChildren<AudioListener>(true)) listener.enabled = false;
            var context = rig.GetComponent<PlayerContext>();
            context.head.position = eye;
            context.head.rotation = Quaternion.LookRotation(look - eye, Vector3.up);
            // The off hand raised to read the wrist.
            var off = context.leftHand;
            off.transform.position = context.head.TransformPoint(new Vector3(-0.05f, -0.2f, 0.34f));
            off.transform.rotation = context.head.rotation * Quaternion.Euler(-60f, 0f, 80f);
            var wrist = rig.GetComponentInChildren<WristDisplay>(true);
            if (wrist != null)
            {
                // Held up in front of the eyes, as when the player turns the wrist to read it.
                Vector3 at = context.head.TransformPoint(new Vector3(-0.04f, -0.1f, 0.3f));
                wrist.transform.SetParent(null, false);
                wrist.transform.SetPositionAndRotation(at, Quaternion.LookRotation(at - eye, context.head.up));
                if (wrist.crewText != null) wrist.crewText.text = "DOWNED - CREW MUST REVIVE YOU\nREVIVE " + CoopRevive.Bar(0.3f) + " 30%";
            }
            if (context.avatar != null)
            {
                context.avatar.SnapToHead();
                context.avatar.Pose(0f);
            }
            context.SetDowned(true, eye);
            Shot("68_revive_downed_own_view", eye, look, false, 90f);
            Vector3 wristAt = wrist != null ? wrist.transform.position : off.transform.position;
            Shot("68a_revive_downed_wrist", eye, wristAt, false, 60f);
            Object.DestroyImmediate(rig);
        }
    }
}

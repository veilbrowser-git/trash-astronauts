using System.Collections.Generic;
using TMPro;
using TrashAstronauts.Core;
using TrashAstronauts.Mission;
using TrashAstronauts.Net;
using TrashAstronauts.Player;
using TrashAstronauts.UI;
using TrashAstronauts.World;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// Co-op pieces: the stand-in for another crew member (RemoteCrew prefab), the CO-OP CREW panel in the tug,
    /// and the NetSession on the Mission object. Nothing networked goes into salvage or creature prefabs.
    /// </summary>
    public static partial class Builder
    {
        public const string RemoteCrewPrefabName = "RemoteCrew";
        static GameObject s_RemoteCrewPrefab;

        /// <summary>
        /// Another crew member: the full suit with the helmet on (tinted per slot at runtime), the same modelled
        /// gloves as the local rig with fingers curled from the streamed grip and trigger, thruster flames, and a
        /// kinematic capsule on the Player layer. No camera, audio listener, XR origin, interactor, input, haptics,
        /// and nothing on the Tool or Salvage layers.
        /// </summary>
        static GameObject BuildRemoteCrewPrefab()
        {
            var root = new GameObject("Remote Crew") { layer = GameLayers.Player };
            var context = root.AddComponent<PlayerContext>();
            var remote = root.AddComponent<RemoteAvatar>();

            var head = Empty(root.transform, "Head", new Vector3(0f, 1.6f, 0f), null, GameLayers.Player);
            context.head = head.transform;
            var left = BuildRemoteHand(root.transform, true);
            var right = BuildRemoteHand(root.transform, false);
            context.leftHand = left;
            context.rightHand = right;

            var avatar = BuildAvatar(root.transform, "Avatar", true);
            avatar.head = head.transform;
            avatar.leftHand = left.transform;
            avatar.rightHand = right.transform;
            avatar.Pose(0f);
            context.avatar = avatar;

            // Kinematic body on the Player layer: doors see someone standing in them. The Player layer only meets the
            // environment, so it never shoves the local player or the salvage.
            var bodyGo = Empty(root.transform, "Body", new Vector3(0f, 0.75f, 0f), null, GameLayers.Player);
            var capsule = bodyGo.AddComponent<CapsuleCollider>();
            capsule.radius = 0.2f;
            capsule.height = 1.7f;
            capsule.direction = 1;
            var body = bodyGo.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.None;

            remote.context = context;
            remote.suit = avatar;
            remote.head = head.transform;
            remote.leftHand = left;
            remote.rightHand = right;
            remote.leftGlove = left.GetComponentInChildren<GloveVisual>(true);
            remote.rightGlove = right.GetComponentInChildren<GloveVisual>(true);
            remote.body = body;
            remote.bodyDrop = 0.85f;
            return SavePrefab(root, RemoteCrewPrefabName);
        }

        /// <summary>A glove on a bare transform: the local rig's glove model and thruster, without controller, input or interactor.</summary>
        static HandRig BuildRemoteHand(Transform parent, bool left)
        {
            float s = left ? 1f : -1f;
            var go = Empty(parent, left ? "Left Hand" : "Right Hand", new Vector3(left ? -0.2f : 0.2f, 1.1f, 0.3f), null, GameLayers.Hand);
            // Same offset and roll as BuildHand, so a streamed controller pose puts the glove where the player sees their own.
            var glove = Empty(go.transform, "Glove", new Vector3(left ? -0.065f : 0.065f, -0.035f, 0f), Quaternion.Euler(0f, 0f, left ? 90f : -90f), GameLayers.Hand);
            var visual = glove.AddComponent<GloveVisual>();
            if (!BuildModelGlove(glove.transform, left, visual)) BuildStandInGlove(glove.transform, s, visual);
            Part(glove.transform, "Thruster Nozzle", Cone(0.013f, 0.009f, 0.03f, 10), new Vector3(0, 0.006f, -0.165f), M("Steel"), new Vector3(-90, 0, 0), GameLayers.Hand);
            var flame = Empty(glove.transform, "Thruster Flame", new Vector3(0, 0.006f, -0.18f), null, GameLayers.Hand);
            Part(flame.transform, "Flame", Cone(0.009f, 0.001f, 0.09f, 8), new Vector3(0, 0, -0.045f), M("Flame"), new Vector3(-90, 0, 0), GameLayers.Hand);
            flame.SetActive(false);

            var hand = go.AddComponent<HandRig>();
            hand.side = left ? Handedness.Left : Handedness.Right;
            hand.thrusterVisual = flame;
            visual.hand = hand;
            return hand;
        }

        /// <summary>NetSession on the Mission object: offline until the crew panel is used.</summary>
        static NetSession AddNetSession(GameObject missionGo, MissionDirector director)
        {
            var session = missionGo.AddComponent<NetSession>();
            session.director = director;
            if (s_RemoteCrewPrefab != null) session.remoteAvatarPrefab = s_RemoteCrewPrefab.GetComponent<RemoteAvatar>();
            return session;
        }

        /// <summary>
        /// The CO-OP CREW panel. On the tug's east wall, facing into the cabin, between the collection locker
        /// (south of it) and the practice plate (north of it), above the hand rail: clear of the mission board and
        /// the tutorial board (west wall), the locker, the store (supply bay) and the hatch to the airlock (north wall).
        /// Top to bottom: title, status, notice; two rows of buttons (same Wi-Fi above, online below, with a tall
        /// LEAVE beside both); then one lower area that shows the help text, the FIND results (up to 5 rows, each
        /// with its own JOIN) or the crew-code keypad. Every button is at least 0.26 x 0.10 m.
        /// </summary>
        static NetLobbyBoard BuildLobbyBoard(NetSession session)
        {
            var root = Empty(s_Props, "Co-op Crew Panel", new Vector3(2f - T * 0.5f - 0.02f, 1.82f, 0.22f), Facing(Vector3.left), GameLayers.Default);
            Transform t = root.transform;
            Prim(t, "Back", PrimitiveType.Cube, new Vector3(0, 0, 0.012f), new Vector3(1.36f, 1.36f, 0.02f), M("Screen"));
            CBox(t, "Header Bar", new Vector3(0, 0.665f, 0.006f), new Vector3(1.36f, 0.03f, 0.03f), M("GlowTeal"), 0.006f);

            var board = root.AddComponent<NetLobbyBoard>();
            board.session = session;
            board.title = Text(t, "Title", "CO-OP CREW", new Vector3(0, 0.615f, -0.002f), Quaternion.identity, 0.7f, new Vector2(1.3f, 0.08f), new Color(0.6f, 1f, 0.9f));
            board.title.fontStyle = FontStyles.Bold;
            board.status = Text(t, "Status", "", new Vector3(0, 0.47f, -0.002f), Quaternion.identity, 0.36f, new Vector2(1.28f, 0.2f), new Color(0.85f, 0.95f, 1f), TextAlignmentOptions.TopLeft);
            board.notice = Text(t, "Notice", "", new Vector3(0, 0.3f, -0.002f), Quaternion.identity, 0.3f, new Vector2(1.28f, 0.1f), new Color(1f, 0.8f, 0.35f), TextAlignmentOptions.TopLeft);

            // Same Wi-Fi on the upper row, online on the lower row, LEAVE (for both) beside them.
            var size = new Vector2(0.3f, 0.12f);
            board.hostButton = Button(t, "Host Co-op", new Vector3(-0.495f, 0.17f, 0), Quaternion.identity, size, "HOST\nCO-OP", M("ButtonGreen"), 0.3f);
            board.findButton = Button(t, "Find Crew", new Vector3(-0.165f, 0.17f, 0), Quaternion.identity, size, "FIND\nCREW", M("ButtonBlue"), 0.3f);
            board.codeButton = Button(t, "Enter Code", new Vector3(0.165f, 0.17f, 0), Quaternion.identity, size, "ENTER\nCODE", M("ButtonBlue"), 0.3f);
            board.hostOnlineButton = Button(t, "Host Online", new Vector3(-0.495f, 0.02f, 0), Quaternion.identity, size, "HOST\nONLINE", M("ButtonGreen"), 0.3f);
            board.findOnlineButton = Button(t, "Find Online", new Vector3(-0.165f, 0.02f, 0), Quaternion.identity, size, "FIND\nONLINE", M("ButtonBlue"), 0.3f);
            Text(t, "Online Caption", "ONLINE: through\nUnity's servers", new Vector3(0.165f, 0.02f, -0.002f), Quaternion.identity, 0.24f, new Vector2(0.3f, 0.12f), new Color(0.6f, 0.75f, 0.85f));
            board.leaveButton = Button(t, "Leave", new Vector3(0.495f, 0.095f, 0), Quaternion.identity, new Vector2(0.3f, 0.27f), "LEAVE", M("ButtonAmber"), 0.34f);

            // What the lower area shows when neither the results nor the keypad are up.
            var infoPanel = Empty(t, "Info", Vector3.zero, null, GameLayers.Default);
            board.infoPanel = infoPanel;
            board.info = Text(infoPanel.transform, "Text", "", new Vector3(0, -0.33f, -0.002f), Quaternion.identity, 0.3f, new Vector2(1.28f, 0.42f), new Color(0.75f, 0.85f, 0.95f), TextAlignmentOptions.TopLeft);

            // FIND CREW (up to three tugs) or FIND ONLINE (up to five games) results, each row with its own JOIN.
            var results = Empty(t, "Results", Vector3.zero, null, GameLayers.Default);
            board.resultsPanel = results;
            var rows = new List<GameObject>();
            var labels = new List<TMP_Text>();
            var joins = new List<PushButton>();
            for (int i = 0; i < NetLobbyBoard.ResultRowCount; i++)
            {
                float y = -0.14f - i * 0.115f;
                var row = Empty(results.transform, "Result " + (i + 1), new Vector3(0, y, 0), null, GameLayers.Default);
                labels.Add(Text(row.transform, "Label", "", new Vector3(-0.17f, 0, -0.002f), Quaternion.identity, 0.32f, new Vector2(0.92f, 0.1f), Color.white, TextAlignmentOptions.Left));
                joins.Add(Button(row.transform, "Join", new Vector3(0.5f, 0, 0), Quaternion.identity, new Vector2(0.26f, 0.1f), "JOIN", M("ButtonGreen"), 0.34f));
                rows.Add(row);
            }
            board.resultRows = rows.ToArray();
            board.resultLabels = labels.ToArray();
            board.joinButtons = joins.ToArray();
            results.SetActive(false);

            // ENTER CODE keypad: C and D fields, digits 0-9, back, join.
            var keypad = Empty(t, "Keypad", Vector3.zero, null, GameLayers.Default);
            board.keypadPanel = keypad;
            board.codeEntry = Text(keypad.transform, "Code", "C ___  D ___", new Vector3(0.15f, -0.12f, -0.002f), Quaternion.identity, 0.5f, new Vector2(0.62f, 0.07f), new Color(1f, 0.95f, 0.6f));
            var side = new Vector2(0.28f, 0.1f);
            board.fieldCButton = Button(keypad.transform, "Field C", new Vector3(-0.5f, -0.22f, 0), Quaternion.identity, side, "EDIT C", M("ButtonBlue"), 0.3f);
            board.fieldDButton = Button(keypad.transform, "Field D", new Vector3(-0.5f, -0.335f, 0), Quaternion.identity, side, "EDIT D", M("ButtonBlue"), 0.3f);
            board.backButton = Button(keypad.transform, "Back", new Vector3(-0.5f, -0.45f, 0), Quaternion.identity, side, "BACK", M("ButtonAmber"), 0.3f);
            board.joinCodeButton = Button(keypad.transform, "Join Code", new Vector3(-0.5f, -0.565f, 0), Quaternion.identity, side, "JOIN", M("ButtonGreen"), 0.34f);
            var digits = new PushButton[10];
            var key = new Vector2(0.17f, 0.1f);
            for (int d = 1; d <= 9; d++)
            {
                int col = (d - 1) % 3, row = (d - 1) / 3;
                digits[d] = Button(keypad.transform, "Digit " + d, new Vector3(-0.05f + col * 0.2f, -0.22f - row * 0.115f, 0), Quaternion.identity, key, d.ToString(), M("Screen"), 0.46f);
            }
            digits[0] = Button(keypad.transform, "Digit 0", new Vector3(0.15f, -0.565f, 0), Quaternion.identity, key, "0", M("Screen"), 0.46f);
            board.digitButtons = digits;
            keypad.SetActive(false);
            return board;
        }
    }
}

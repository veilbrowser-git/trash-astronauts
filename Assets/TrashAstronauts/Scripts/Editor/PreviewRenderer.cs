using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TrashAstronauts.EditorTools
{
    /// <summary>
    /// Renders a few still images of the generated scene to Docs/Previews so the level can be eyeballed
    /// without a headset. Menu: Trash Astronauts > Advanced > Render Preview Images.
    /// </summary>
    public static class PreviewRenderer
    {
        [MenuItem("Trash Astronauts/Advanced/Render Preview Images", priority = 60)]
        public static void RenderPreviews()
        {
            EditorSceneManager.OpenScene(Builder.ScenePath, OpenSceneMode.Single);
            Directory.CreateDirectory("Docs/Previews");

            var lineup = new GameObject("Preview Lineup") { hideFlags = HideFlags.HideAndDontSave };
            string[] names = { "Salvage_HullPlate", "Salvage_CableCoil", "Salvage_NavBoard", "Salvage_CommsRelay", "Salvage_ReactorCore", "Salvage_OrbitalToaster", "Junk_Can", "Junk_Crate" };
            for (int i = 0; i < names.Length; i++)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Builder.PrefabDir + "/" + names[i] + ".prefab");
                if (prefab == null) continue;
                var instance = Object.Instantiate(prefab, new Vector3(-1.05f + i * 0.3f, 1.2f, -40f), Quaternion.Euler(20f, 200f + i * 15f, 0f), lineup.transform);
                instance.hideFlags = HideFlags.HideAndDontSave;
            }
            var lineupLight = new GameObject("Lineup Light") { hideFlags = HideFlags.HideAndDontSave }.AddComponent<Light>();
            lineupLight.transform.SetParent(lineup.transform);
            lineupLight.transform.position = new Vector3(0.3f, 1.9f, -40.9f);
            lineupLight.type = LightType.Point;
            lineupLight.range = 5f;
            lineupLight.intensity = 6f;

            Shot("01_tug_mission_board", new Vector3(1.0f, 1.6f, -0.6f), new Vector3(-2f, 1.45f, -0.4f));
            Shot("02_tug_locker_and_hatch", new Vector3(-1.2f, 1.6f, -1.6f), new Vector3(1.6f, 1.3f, 0.6f));
            Shot("03_airlock", new Vector3(0f, 1.6f, 4.0f), new Vector3(0.3f, 1.4f, 7.6f));
            Shot("04_corridor", Builder.FreighterPoint(0f, 1.6f, 8.2f), Builder.FreighterPoint(0f, 1.4f, 21f));
            Shot("05_cargo_bay", Builder.FreighterPoint(-2.0f, 1.7f, 12.2f), Builder.FreighterPoint(-8.5f, 1.2f, 16.5f));
            Shot("06_maintenance", Builder.FreighterPoint(2.2f, 1.6f, 10.2f), Builder.FreighterPoint(7.4f, 1.3f, 14.6f));
            Shot("07_reactor", Builder.FreighterPoint(0f, 1.6f, 22.2f), Builder.FreighterPoint(0f, 1.3f, 25.5f));
            Shot("09_board_closeup", new Vector3(-0.2f, 1.5f, -1.0f), new Vector3(-2f, 1.5f, -1.0f));
            Shot("10_creature", Builder.FreighterPoint(2.1f, 1.35f, 26.9f), Builder.FreighterPoint(3.4f, 1.3f, 28.4f));
            Shot("11_creature_side", Builder.FreighterPoint(1.9f, 1.5f, 28.9f), Builder.FreighterPoint(3.4f, 1.3f, 28.4f));
            Shot("12_gloves_and_tools", new Vector3(-0.35f, 1.45f, -0.55f), new Vector3(0.4f, 1.1f, -0.9f));
            Shot("13_salvage_lineup", new Vector3(0f, 1.25f, -40.9f), new Vector3(0f, 1.2f, -40f));
            // The four spiders side by side on an empty stage, then close-ups.
            var roster = new GameObject("Preview Roster") { hideFlags = HideFlags.HideAndDontSave };
            string[] creatureNames = { "Drone_Janitor7", "Creature_Listener", "Creature_Warden", "Creature_Mimic" };
            for (int i = 0; i < creatureNames.Length; i++)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Builder.PrefabDir + "/" + creatureNames[i] + ".prefab");
                if (prefab == null) continue;
                var instance = Object.Instantiate(prefab, new Vector3(-3.9f + i * 2.6f, 1.4f, -60f), Quaternion.Euler(0f, 160f + i * 14f, 0f), roster.transform);
                instance.hideFlags = HideFlags.HideAndDontSave;
                foreach (var l in instance.GetComponentsInChildren<Light>(true)) l.enabled = false;
                PlantPreviewSpider(instance);
            }
            foreach (float x in new[] { -3.5f, 0f, 3.5f })
            {
                var rosterLight = new GameObject("Roster Light") { hideFlags = HideFlags.HideAndDontSave }.AddComponent<Light>();
                rosterLight.transform.SetParent(roster.transform);
                rosterLight.transform.position = new Vector3(x, 2.6f, -61.8f);
                rosterLight.type = LightType.Point;
                rosterLight.range = 8f;
                rosterLight.intensity = 9f;
            }
            Shot("14_creature_roster", new Vector3(0f, 2.3f, -64.6f), new Vector3(0f, 1.2f, -60f));
            Shot("14a_spider_tarantula", new Vector3(-4.5f, 1.75f, -61.1f), new Vector3(-3.9f, 1.38f, -60f));
            Shot("14b_spider_tarantula_top", new Vector3(-3.9f, 2.5f, -60.6f), new Vector3(-3.9f, 1.4f, -60f));
            Shot("14c_spider_cave", new Vector3(-1.9f, 1.8f, -61.3f), new Vector3(-1.3f, 1.38f, -60f));
            Shot("14d_spider_hunter", new Vector3(0.7f, 1.7f, -61.0f), new Vector3(1.3f, 1.38f, -60f));
            Shot("14e_spider_widow", new Vector3(3.3f, 1.8f, -61.2f), new Vector3(3.9f, 1.4f, -60f));
            Shot("14f_spider_face", new Vector3(-4.15f, 1.38f, -60.75f), new Vector3(-3.9f, 1.36f, -60f));
            Object.DestroyImmediate(roster);
            SpiderOnSurfaceShots();

            // Hands holding tools, posed exactly as the grab system will hold them.
            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Builder.PrefabDir + "/PlayerRig.prefab");
            if (rigPrefab != null)
            {
                var rig = Object.Instantiate(rigPrefab, new Vector3(0f, 0f, -50f), Quaternion.identity);
                rig.hideFlags = HideFlags.HideAndDontSave;
                foreach (var cam in rig.GetComponentsInChildren<Camera>(true)) cam.enabled = false;
                foreach (var listener in rig.GetComponentsInChildren<AudioListener>(true)) listener.enabled = false;
                var tools = rig.GetComponentsInChildren<TrashAstronauts.Tools.SuitTool>(true);
                foreach (var hand in rig.GetComponentsInChildren<TrashAstronauts.Player.HandRig>(true))
                {
                    bool left = hand.side == TrashAstronauts.Core.Handedness.Left;
                    hand.transform.localPosition = new Vector3(left ? -0.16f : 0.16f, 1.25f, 0.35f);
                    hand.transform.localRotation = Quaternion.Euler(-10f, left ? 8f : -8f, 0f);
                    TrashAstronauts.Tools.SuitTool tool = null;
                    foreach (var candidate in tools)
                        if (candidate.toolName == (left ? "Flashlight" : "Plasma Cutter")) tool = candidate;
                    if (tool != null)
                    {
                        var attach = hand.interactor.attachTransform;
                        var grip = tool.grab.attachTransform;
                        tool.transform.SetParent(rig.transform, true);
                        tool.transform.rotation = attach.rotation * Quaternion.Inverse(grip.localRotation);
                        tool.transform.position = attach.position - tool.transform.rotation * grip.localPosition;
                    }
                    hand.GetComponentInChildren<TrashAstronauts.Player.GloveVisual>(true).Apply(0.3f, 1f);
                }
                var handLight = new GameObject("Hand Light") { hideFlags = HideFlags.HideAndDontSave }.AddComponent<Light>();
                handLight.transform.SetParent(rig.transform);
                handLight.transform.position = new Vector3(0.3f, 1.9f, -50.2f);
                handLight.type = LightType.Point;
                handLight.range = 5f;
                handLight.intensity = 5f;
                foreach (var l in rig.GetComponentsInChildren<Light>(true)) if (l != handLight) l.enabled = false;
                // The hands moved: bring the suit's arms onto the glove cuffs again.
                var ownAvatar = rig.GetComponentInChildren<TrashAstronauts.Player.PlayerAvatar>(true);
                if (ownAvatar != null)
                {
                    ownAvatar.SnapToHead();
                    ownAvatar.Pose(0f);
                }
                Shot("15_hands_player_view", new Vector3(0f, 1.55f, -49.9f), new Vector3(0f, 1.2f, -49.6f));
                Shot("16_right_hand_side", new Vector3(0.55f, 1.27f, -49.62f), new Vector3(0.16f, 1.25f, -49.64f));
                Shot("17_left_hand_side", new Vector3(-0.55f, 1.27f, -49.62f), new Vector3(-0.16f, 1.25f, -49.64f));
                // Close-ups of the gloves with nothing in them: open (back of the right, palm of the left), then a fist.
                foreach (var tool in tools) tool.gameObject.SetActive(false);
                var gloves = rig.GetComponentsInChildren<TrashAstronauts.Player.GloveVisual>(true);
                foreach (var glove in gloves) glove.Apply(0f, 0f);
                Shot("37_glove_open_back", new Vector3(0.42f, 1.34f, -49.42f), new Vector3(0.16f, 1.24f, -49.68f), false, 40f);
                Shot("38_glove_open_palm", new Vector3(0.14f, 1.32f, -49.4f), new Vector3(-0.16f, 1.24f, -49.68f), false, 40f);
                foreach (var glove in gloves) glove.Apply(1f, 1f);
                Shot("39_glove_fist", new Vector3(0.42f, 1.34f, -49.42f), new Vector3(0.16f, 1.24f, -49.68f), false, 40f);
                Object.DestroyImmediate(rig);
            }

            // The player's suit as a crew mate sees it: helmet on, arms posed from the head and the two hands.
            if (rigPrefab != null)
            {
                var rig = Object.Instantiate(rigPrefab, new Vector3(0f, 0f, -70f), Quaternion.identity);
                rig.hideFlags = HideFlags.HideAndDontSave;
                foreach (var cam in rig.GetComponentsInChildren<Camera>(true)) cam.enabled = false;
                foreach (var listener in rig.GetComponentsInChildren<AudioListener>(true)) listener.enabled = false;
                foreach (var l in rig.GetComponentsInChildren<Light>(true)) l.enabled = false;
                var avatar = rig.GetComponentInChildren<TrashAstronauts.Player.PlayerAvatar>(true);
                if (avatar != null)
                {
                    foreach (var hand in rig.GetComponentsInChildren<TrashAstronauts.Player.HandRig>(true))
                    {
                        bool left = hand.side == TrashAstronauts.Core.Handedness.Left;
                        hand.transform.localPosition = left ? new Vector3(-0.34f, 1.02f, 0.3f) : new Vector3(0.27f, 1.36f, 0.42f);
                        hand.transform.localRotation = Quaternion.Euler(left ? 20f : -35f, left ? 15f : -20f, 0f);
                    }
                    if (avatar.head != null) avatar.head.localRotation = Quaternion.Euler(4f, 12f, 0f);
                    avatar.SetHelmetVisible(true);
                    avatar.SetTint(TrashAstronauts.Player.PlayerAvatar.CrewColors[0]);
                    avatar.SnapToHead();
                    avatar.Pose(0f);
                    foreach (var at in new[] { new Vector3(1.6f, 2.4f, -67.8f), new Vector3(-1.8f, 1.2f, -68.4f), new Vector3(-0.6f, 2.2f, -72.2f), new Vector3(1.8f, 0.9f, -71.6f) })
                    {
                        var suitLight = new GameObject("Suit Light") { hideFlags = HideFlags.HideAndDontSave }.AddComponent<Light>();
                        suitLight.transform.SetParent(rig.transform);
                        suitLight.transform.position = at;
                        suitLight.type = LightType.Point;
                        suitLight.range = 7f;
                        suitLight.intensity = 5f;
                    }
                    Shot("32_avatar_suit", new Vector3(0.75f, 1.25f, -68.05f), new Vector3(0f, 0.92f, -70f), false, 50f);
                    Shot("33_avatar_suit_back", new Vector3(-0.95f, 1.45f, -71.9f), new Vector3(0f, 0.92f, -70f), false, 50f);
                    Shot("34_avatar_suit_bust", new Vector3(-0.42f, 1.5f, -69.05f), new Vector3(0f, 1.36f, -70f), false, 50f);
                    // What the wearer sees looking down: no helmet, the chest and its holsters, arms and legs.
                    avatar.SetHelmetVisible(false);
                    if (avatar.head != null)
                    {
                        avatar.head.localRotation = Quaternion.Euler(55f, 0f, 0f);
                        avatar.SnapToHead();
                        avatar.Pose(0f);
                        Shot("35_avatar_own_view_down", avatar.head.position, avatar.head.position + avatar.head.forward, false, 95f);
                        avatar.head.localRotation = Quaternion.Euler(89f, 0f, 0f);
                        avatar.SnapToHead();
                        avatar.Pose(0f);
                        Shot("36_avatar_own_view_straight_down", avatar.head.position, avatar.head.position + avatar.head.forward, false, 95f);
                    }
                }
                Object.DestroyImmediate(rig);
            }

            CoopShots();

            Shot("08_top_down", new Vector3(-1f, 46f, 22f), new Vector3(-1f, 0f, 22.01f), true);
            Shot("18_flight_deck", new Vector3(-0.8f, 1.6f, -3.3f), new Vector3(0.3f, 1.3f, -6.2f));
            Shot("30_supply_bay_shop", new Vector3(-2.5f, 1.6f, -4.3f), new Vector3(-5.4f, 1.3f, -4.8f));
            Shot("31_supply_bay_from_flight_deck", new Vector3(0.8f, 1.6f, -4.4f), new Vector3(-5f, 1.2f, -4.9f));
            Shot("19_cabin_to_flight_deck", new Vector3(0.6f, 1.6f, 1.2f), new Vector3(-0.8f, 1.3f, -5f));

            // The other two wrecks: dock each in turn.
            var mapDirector = Object.FindFirstObjectByType<TrashAstronauts.Mission.MissionDirector>();
            if (mapDirector != null && mapDirector.maps != null && mapDirector.maps.Length >= 3)
            {
                void Dock(int index)
                {
                    for (int i = 0; i < mapDirector.maps.Length; i++) mapDirector.maps[i].gameObject.SetActive(i == index);
                    foreach (var enemy in mapDirector.enemies) enemy.gameObject.SetActive(false);
                }
                Dock(1);
                Shot("20_barge_hold", new Vector3(-0.4f, 1.7f, 12.4f), new Vector3(1.5f, 1.9f, 22f));
                Shot("21_barge_hold_from_above", new Vector3(-7f, 3.6f, 12.6f), new Vector3(3f, 0.8f, 23f));
                Shot("22_barge_quarters", new Vector3(-8.8f, 1.6f, 19f), new Vector3(-14f, 1.2f, 17f));
                Shot("23_barge_drive_room", new Vector3(0f, 1.6f, 27.4f), new Vector3(0f, 1.3f, 33f));
                Shot("24_barge_top_down", new Vector3(-1f, 46f, 22f), new Vector3(-1f, 0f, 22.01f), true);
                Dock(2);
                Shot("25_survey_ring_south", new Vector3(-0.5f, 1.6f, 8.2f), new Vector3(-8f, 1.4f, 11f));
                Shot("26_survey_ring_west", new Vector3(-7f, 1.6f, 11f), new Vector3(-7f, 1.4f, 28f));
                Shot("27_survey_lab", new Vector3(0f, 1.6f, 12.2f), new Vector3(1.5f, 1.2f, 19f));
                Shot("28_survey_observation_deck", new Vector3(0f, 1.6f, 32f), new Vector3(-1f, 1.5f, 36.4f));
                Shot("29_survey_top_down", new Vector3(-1f, 46f, 22f), new Vector3(-1f, 0f, 22.01f), true);
                int cave = System.Array.FindIndex(mapDirector.maps, m => m.mapId == "lava_tube");
                if (cave >= 0)
                {
                    Dock(cave);
                    Shot("37_cave_mouth", new Vector3(-4.6f, 1.7f, 8.3f), new Vector3(6f, 1.2f, 13.2f));
                    Shot("38_cave_main_tunnel", new Vector3(0f, 1.6f, 14.2f), new Vector3(0f, 1.3f, 33f));
                    Shot("39_cave_skylight", new Vector3(-2.2f, 1.5f, 19.2f), new Vector3(-7f, 3.6f, 23.5f));
                    Shot("40_cave_habitat", new Vector3(2.3f, 1.6f, 14.3f), new Vector3(8.5f, 1.2f, 18f));
                    Shot("41_cave_drill_chamber", new Vector3(-0.4f, 1.7f, 34.0f), new Vector3(2f, 1.4f, 41f));
                    Shot("42_cave_back_passage", new Vector3(-8.1f, 1.6f, 28.4f), new Vector3(-8.1f, 1.2f, 38f));
                    Shot("43_cave_top_down", new Vector3(-1f, 46f, 24f), new Vector3(-1f, 0f, 24.01f), true);
                }
                Dock(0);
                mapDirector.enemies[0].gameObject.SetActive(true);
            }
            Object.DestroyImmediate(lineup);
        }

        /// <summary>
        /// The CO-OP CREW panel as it looks offline, with the keypad up and with search results, where it hangs in
        /// the tug, and two crew stand-ins (RemoteCrew prefab) posed in the cabin. Nothing here opens a socket:
        /// the panel's text is filled in by hand for the keypad and results views. The scene is not saved.
        /// </summary>
        static void CoopShots()
        {
            var board = Object.FindAnyObjectByType<TrashAstronauts.UI.NetLobbyBoard>();
            if (board != null)
            {
                board.Refresh();
                Shot("50_coop_crew_panel", new Vector3(0.7f, 1.72f, 0.22f), new Vector3(1.9f, 1.8f, 0.22f), false, 70f);
                Shot("51_coop_panel_in_tug", new Vector3(-1.5f, 1.65f, -1.9f), new Vector3(1.9f, 1.5f, 0.35f));

                string status = board.status.text, notice = board.notice.text;
                board.infoPanel.SetActive(false);
                board.keypadPanel.SetActive(true);
                board.codeEntry.text = "C 001  D 04_";
                board.status.text = "TYPE THE HOST'S CREW CODE\n(shown on the host's CO-OP CREW panel)\nTHIS HEADSET  C 001  D 017";
                board.fieldDButton.SetLabel("> D <");
                Shot("52_coop_keypad", new Vector3(0.7f, 1.72f, 0.22f), new Vector3(1.9f, 1.8f, 0.22f), false, 70f);

                board.keypadPanel.SetActive(false);
                board.resultsPanel.SetActive(true);
                board.resultLabels[0].text = "TUG 4821  2/4  IN HUB";
                board.resultLabels[1].text = "TUG 1307  4/4  IN HUB  FULL";
                for (int i = 2; i < board.resultRows.Length; i++) board.resultRows[i].SetActive(false);
                board.status.text = "FOUND 2: PRESS JOIN\nTHIS HEADSET  C 001  D 017";
                board.notice.text = TrashAstronauts.UI.NetLobbyBoard.NoticeText(TrashAstronauts.Core.Net.LobbyNotice.NobodyFound);
                Shot("53_coop_find_results", new Vector3(0.7f, 1.72f, 0.22f), new Vector3(1.9f, 1.8f, 0.22f), false, 70f);

                // FIND ONLINE with a full list of five games, one of them out on a contract.
                string[] games = { "TUG 4821  2/4  IN HUB", "TUG 1307  4/4  IN HUB  FULL", "TUG 5150  1/4  RESULTS", "TUG 2718  3/4  ON CONTRACT", "TUG 9001  1/4  IN HUB" };
                for (int i = 0; i < board.resultRows.Length; i++)
                {
                    board.resultRows[i].SetActive(i < games.Length);
                    if (i < games.Length) board.resultLabels[i].text = games[i];
                }
                board.status.text = "ONLINE: FOUND 5: PRESS JOIN";
                board.notice.text = TrashAstronauts.UI.NetLobbyBoard.OnlineNoticeText(TrashAstronauts.Net.Online.OnlineProblem.GameGone);
                board.joinButtons[1].SetInteractable(false); // full
                board.joinButtons[3].SetInteractable(false); // on a contract
                Shot("56_coop_find_online", new Vector3(0.7f, 1.72f, 0.22f), new Vector3(1.9f, 1.8f, 0.22f), false, 70f);

                // Hosting online: the tug number, ONLINE, and the 6-character code.
                board.resultsPanel.SetActive(false);
                board.infoPanel.SetActive(true);
                string info = board.info.text;
                board.status.text = "HOSTING  TUG 4821  ONLINE\nCODE  K7Q2ZD\nCREW 2/4:  ORANGE (YOU)  BLUE";
                board.notice.text = "";
                board.info.text = "Contracts are shared: the host launches one and the\nwhole crew rides along, with one oxygen tank and one\nlocker, and everyone is paid. In this version only the\nhost can carry salvage and there is no creature.\nNothing from your microphone is ever sent.";
                board.joinButtons[1].SetInteractable(true);
                board.joinButtons[3].SetInteractable(true);
                Shot("57_coop_hosting_online", new Vector3(0.7f, 1.72f, 0.22f), new Vector3(1.9f, 1.8f, 0.22f), false, 70f);

                for (int i = 0; i < board.resultRows.Length; i++) board.resultRows[i].SetActive(true);
                board.info.text = info;
                board.status.text = status;
                board.notice.text = notice;
            }

            var remotePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(Builder.PrefabDir + "/" + Builder.RemoteCrewPrefabName + ".prefab");
            if (remotePrefab == null) return;
            var crew = new System.Collections.Generic.List<GameObject>();
            var poses = new[]
            {
                (slot: 1, head: new Vector3(-0.7f, 1.62f, -2.1f), yaw: 20f, wave: true),
                (slot: 2, head: new Vector3(0.35f, 1.5f, -1.9f), yaw: -40f, wave: false),
            };
            foreach (var p in poses)
            {
                var go = Object.Instantiate(remotePrefab);
                go.hideFlags = HideFlags.HideAndDontSave;
                crew.Add(go);
                var remote = go.GetComponent<TrashAstronauts.Net.RemoteAvatar>();
                remote.Setup(new TrashAstronauts.Core.PlayerId(p.slot), 0);
                Quaternion face = Quaternion.Euler(5f, p.yaw, 0f);
                var s = TrashAstronauts.Core.Net.AvatarSnapshot.Default;
                Vector3 right = face * Vector3.right, fwd = face * Vector3.forward;
                s.headPosition = TrashAstronauts.Net.NetUnity.ToNet(p.head);
                s.headRotation = TrashAstronauts.Net.NetUnity.ToNet(face);
                Vector3 l = p.head - right * 0.25f + fwd * 0.3f + Vector3.down * 0.45f;
                Vector3 r = p.wave ? p.head + right * 0.32f + fwd * 0.15f + Vector3.up * 0.12f : p.head + right * 0.22f + fwd * 0.35f + Vector3.down * 0.4f;
                s.leftHandPosition = TrashAstronauts.Net.NetUnity.ToNet(l);
                s.rightHandPosition = TrashAstronauts.Net.NetUnity.ToNet(r);
                s.leftHandRotation = TrashAstronauts.Net.NetUnity.ToNet(face * Quaternion.Euler(10f, 10f, 0f));
                s.rightHandRotation = TrashAstronauts.Net.NetUnity.ToNet(face * Quaternion.Euler(p.wave ? -70f : 10f, -10f, 0f));
                s.leftGripCurl = p.wave ? 0.2f : 1f;
                s.leftIndexCurl = p.wave ? 0.1f : 1f;
                remote.Apply(in s);
                remote.leftGlove.Apply(s.leftIndexCurl, s.leftGripCurl);
                remote.rightGlove.Apply(0f, 0f);
                remote.suit.SnapToHead();
                remote.suit.Pose(0f);
            }
            Shot("54_remote_crew_in_tug", new Vector3(-1.3f, 1.6f, 0.7f), new Vector3(-0.1f, 1.3f, -2.0f), false, 70f);
            foreach (var go in crew) Object.DestroyImmediate(go);
        }

        static void Shot(string name, Vector3 from, Vector3 lookAt, bool topDown = false, float fieldOfView = 80f)
        {
            var go = new GameObject("Preview Camera") { hideFlags = HideFlags.HideAndDontSave };
            var cam = go.AddComponent<Camera>();
            cam.transform.position = from;
            cam.transform.LookAt(lookAt, topDown ? Vector3.forward : Vector3.up);
            cam.fieldOfView = topDown ? 60f : fieldOfView;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 80f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;

            // Top-down view: hide ceilings and drop the fog so the layout is readable.
            bool fog = RenderSettings.fog;
            Color ambient = RenderSettings.ambientLight;
            var hidden = new System.Collections.Generic.List<Renderer>();
            if (topDown)
            {
                RenderSettings.fog = false;
                RenderSettings.ambientLight = new Color(0.6f, 0.6f, 0.6f);
                foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
                {
                    if (r.enabled && (r.name == "Ceiling" || r.name.EndsWith("Ceiling") || r.bounds.min.y > 2.1f))
                    {
                        r.enabled = false;
                        hidden.Add(r);
                    }
                }
            }

            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            File.WriteAllBytes("Docs/Previews/" + name + ".png", tex.EncodeToPNG());

            RenderTexture.active = null;
            cam.targetTexture = null;
            foreach (var r in hidden) r.enabled = true;
            RenderSettings.fog = fog;
            RenderSettings.ambientLight = ambient;
            Object.DestroyImmediate(tex);
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(go);
        }
    
        /// <summary>Stands a spider instance on whatever is under its feet (or on its own plane) with the planted-foot IK.</summary>
        static void PlantPreviewSpider(GameObject instance)
        {
            var animator = instance.GetComponent<TrashAstronauts.Mission.CreatureAnimator>();
            if (animator == null) return;
            Physics.SyncTransforms();
            animator.PoseFeetOnSurface();
        }

        /// <summary>A spider on the corridor wall and one on the ceiling, feet planted by the IK.</summary>
        static void SpiderOnSurfaceShots()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Builder.PrefabDir + "/Drone_Janitor7.prefab");
            if (prefab == null) return;
            Physics.SyncTransforms();
            Vector3 centre = Builder.FreighterPoint(0f, 1.3f, 15f);
            int mask = TrashAstronauts.World.GameLayers.EnvironmentMask;
            var holder = new GameObject("Preview Surface Spiders") { hideFlags = HideFlags.HideAndDontSave };
            var light = new GameObject("Preview Light") { hideFlags = HideFlags.HideAndDontSave }.AddComponent<Light>();
            light.transform.SetParent(holder.transform);
            light.type = LightType.Point;
            light.range = 7f;
            light.intensity = 8f;

            Vector3[] directions = { Vector3.right, Vector3.left, Vector3.up };
            string[] names = { "14g_spider_wall", null, "14h_spider_ceiling" };
            bool wallDone = false;
            for (int d = 0; d < directions.Length; d++)
            {
                if (d == 1 && wallDone) continue;
                if (!Physics.Raycast(centre, directions[d], out RaycastHit hit, 6f, mask, QueryTriggerInteraction.Ignore)) continue;
                string name = names[d] ?? names[0];
                var instance = Object.Instantiate(prefab, holder.transform);
                instance.hideFlags = HideFlags.HideAndDontSave;
                foreach (var l in instance.GetComponentsInChildren<Light>(true)) l.enabled = false;
                var walker = instance.GetComponent<TrashAstronauts.Mission.SurfaceWalker>();
                float height = walker != null ? walker.bodyHeight : 0.45f;
                Vector3 normal = hit.normal;
                Vector3 forward = Vector3.ProjectOnPlane(Vector3.forward, normal).normalized;
                instance.transform.SetPositionAndRotation(hit.point + normal * height, Quaternion.LookRotation(forward, normal));
                PlantPreviewSpider(instance);
                Vector3 body = instance.transform.position;
                Vector3 side = Vector3.Cross(normal, forward);
                Vector3 eye = centre - directions[d] * 0.6f + Vector3.back * 1.6f + Vector3.up * (d == 2 ? -0.3f : 0.2f);
                light.transform.position = Vector3.Lerp(eye, body, 0.4f) + side * 0.2f;
                Shot(name, eye, body);
                Object.DestroyImmediate(instance);
                if (d < 2) wallDone = true;
            }
            Object.DestroyImmediate(holder);
        }
    }
}

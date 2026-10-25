using Client.MirControls;
using Client.MirObjects;
using Client.MirScenes;
using Client.MirScenes.Modern;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Hud;
using Client.MirScenes.Modern.Windows;
using S = ServerPackets;

namespace Client.RenderTest
{
    /// <summary>
    /// Phase 2 area "chat / map" steps (modern only): the chat box (lines of every channel, an item link, the input, the channel
    /// prefixes, whisper tabs, the name menu, the settings popover, the announcement ticker), the minimap (a map with a minimap,
    /// objects, group members, map info NPCs / exits, zoom, fold), the quest tracker and the big map window (map record, map
    /// details, world map, search).
    /// </summary>
    internal static partial class UiTestRunner
    {
        private static ModernUi ModernUiNow => GameScene.Ui as ModernUi;
        private static ChatHud ChatNow => ModernUiNow?.Chat;

        static partial void AddChatSteps(List<Step> steps)
        {
            Modern(steps, "chat_lines", ChatLines, 900);
            Modern(steps, "chat_hover", () => { MouseAt(Centre(ChatNow)); LogChat(); }, 500);
            Modern(steps, "chat_open_enter", () => { MouseAt(new Point(-1000, -1000)); MirScene.ActiveScene.OnKeyPress(new KeyPressEventArgs('\r')); LogChat(); });
            Modern(steps, "chat_type_say", () => { ChatNow?.TestType("hello world"); LogChat(); });
            Modern(steps, "chat_send_say", () => { ChatNow?.TestEnter(); LogChat(); });
            Modern(steps, "chat_prefix_shout", () => { MirScene.ActiveScene.OnKeyPress(new KeyPressEventArgs('\r')); ChatNow?.TestType("! "); LogChat(); });
            Modern(steps, "chat_send_shout", () => { ChatNow?.TestType("selling sword"); ChatNow?.TestEnter(); LogChat(); });
            Modern(steps, "chat_whisper_prefix", () => { MirScene.ActiveScene.OnKeyPress(new KeyPressEventArgs('\r')); ChatNow?.TestType("/UiAlly "); LogChat(); });
            Modern(steps, "chat_send_whisper", () => { ChatNow?.TestType("meet at the gate"); ChatNow?.TestEnter(); Feed(new S.Chat { Message = "/UiAlly meet at the gate", Type = ChatType.WhisperOut }); LogChat(); });
            Modern(steps, "chat_autocomplete", () => { MirScene.ActiveScene.OnKeyPress(new KeyPressEventArgs('\r')); ChatNow?.TestType("/Ui"); LogChat(); });
            Modern(steps, "chat_autocomplete_pick", () => { ChatNow?.TestPopPick(0); LogChat(); });
            Modern(steps, "chat_escape", () => { ChatNow?.TestEscape(); ChatNow?.TestEscape(); LogChat(); });
            Modern(steps, "chat_link_item", () =>
            {
                MirScene.ActiveScene.OnKeyPress(new KeyPressEventArgs('\r'));
                ModernUiNow?.ItemChatLink(new ItemSlotRef(MirGridType.Inventory, 9));
                LogChat();
            });
            Modern(steps, "chat_link_send", () => { ChatNow?.TestEnter(); LogChat(); });
            Modern(steps, "chat_tab_whisper", () => { ChatNow?.TestTab("w:UiAlly"); MouseAt(Centre(ChatNow)); LogChat(); });
            Modern(steps, "chat_tab_all", () => { ChatNow?.TestTab("all"); LogChat(); });
            Modern(steps, "chat_name_menu", () => { ChatNow?.TestNameMenu("UiAlly"); LogChat(); });
            Modern(steps, "chat_name_add_friend", () => { ChatNow?.TestPopPick(2); LogChat(); }); // Whisper, Invite?, Add friend… (no group → index 1 = Add friend)
            Modern(steps, "chat_settings", () => { ChatNow?.TestSettings(); LogChat(); });
            Modern(steps, "chat_settings_close", () => { ChatNow?.TestSettings(); MouseAt(new Point(-1000, -1000)); LogChat(); }, 400);
            Modern(steps, "chat_scroll_up", () => { for (int i = 0; i < 30; i++) Feed(new S.Chat { Message = "UiAlly: filler line " + i, Type = ChatType.Normal }); ChatNow?.TestScroll(-200); Feed(new S.Chat { Message = "UiAlly: one more", Type = ChatType.Normal }); MouseAt(Centre(ChatNow)); LogChat(); });
            Modern(steps, "chat_faded", () => { MouseAt(new Point(-1000, -1000)); LogChat(); }, 400);

            Modern(steps, "minimap_map", MinimapMap, 900);
            Modern(steps, "minimap_objects", MinimapObjects, 700);
            Modern(steps, "minimap_zoom_in", () => { Wheel(Centre(ModernUiNow?.Minimap?.Disc), 120); Wheel(Centre(ModernUiNow?.Minimap?.Disc), 120); LogMinimap(); });
            Modern(steps, "minimap_hover", () => { MouseAt(Offset(ModernUiNow?.Minimap?.Disc, 0.5f, 0.5f, ModernRoot.S(6))); LogMinimap(); });
            Modern(steps, "minimap_fold", () => { MouseAt(new Point(-1000, -1000)); PressBind(KeybindOptions.Minimap); LogMinimap(); });
            Modern(steps, "minimap_unfold", () => { PressBind(KeybindOptions.Minimap); Wheel(Centre(ModernUiNow?.Minimap?.Disc), -120); LogMinimap(); });

            Modern(steps, "tracker_quests", TrackerQuests, 700);

            Modern(steps, "bigmap_open", () => { PressBind(KeybindOptions.Bigmap); LogBigMap(); }, 700);
            Modern(steps, "bigmap_details", BigMapDetails, 900);
            Modern(steps, "bigmap_monsters_tab", () => { BigMap?.TestTab(2); LogBigMap(); }, 700);
            Modern(steps, "bigmap_layers", () => { BigMap?.TestLayer(3, true); BigMap?.TestLayer(4, true); LogBigMap(); _log.Add("   marks " + BigMap?.TestMarks()); }, 700);
            Modern(steps, "bigmap_zoom", () => { BigMap?.ZoomBy(3f); LogBigMap(); }, 900);
            Modern(steps, "bigmap_mob_row", () => { BigMap?.TestRow(0); LogBigMap(); _log.Add("   marks " + BigMap?.TestMarks()); }, 900);
            Modern(steps, "bigmap_waypoint", () => { BigMap?.TestRightClickCentre(); BigMap?.TestTab(0); LogBigMap(); }, 700);
            Modern(steps, "bigmap_hover_token", () =>
            {
                BigMapWindow w = BigMap;
                if (w == null) return;
                foreach (BmMarker m in w.Marks) if (m.Kind == BmKind.Mob) { MouseAt(Centre(m)); break; }
                LogBigMap();
            }, 600);
            Modern(steps, "bigmap_world", BigMapWorld, 900);
            Modern(steps, "bigmap_search", () => { BigMap?.TestSearch("Herbalist"); Feed(new S.SearchMapResult { MapIndex = -1, NPCIndex = 0 }); LogBigMap(); }, 600);
            Modern(steps, "bigmap_close", () => { CloseAllPrompts(); Root?.Popups.CloseAll(); Root?.Wm.Close(BigMapWindow.WindowKey); LogBigMap(); });
        }

        private static BigMapWindow BigMap => Root?.Wm.Get(BigMapWindow.WindowKey) as BigMapWindow;

        private static void LogChat()
        {
            ChatHud c = ChatNow;
            _log.Add("   chat " + (c != null ? c.TestState : "none"));
        }

        private static void LogMinimap()
        {
            MinimapHud m = ModernUiNow?.Minimap;
            _log.Add($"   minimap {(m == null ? "none" : $"radius={MinimapHud.Radius} shut={m.Shut} small={m.SmallMode} markers={m.Markers.Count} at={m.Location} size={m.Size}")}");
        }

        private static void LogBigMap()
        {
            BigMapWindow w = BigMap;
            _log.Add($"   bigmap {(w == null ? "none" : $"open={w.IsOpen} view={w.View} title=\"{w.TitleText}\" marks={w.MarkerCount} rows={w.ListRows} loading=\"{w.LoadingText}\" z={w.Z:0.###} fit={w.FitZ:0.###} pic={w.Pic}")}");
        }

        private static void ChatLines()
        {
            UserItem linked = Item("UiTestSword");
            GameScene.ChatItemList.Add(linked);
            Feed(new S.Chat { Message = "UiAlly: hello there, anyone up for the cave?", Type = ChatType.Normal });
            Feed(new S.Chat { Message = "UiAlly=>psst, over here", Type = ChatType.WhisperIn });
            Feed(new S.Chat { Message = "(!)Shouter: WTS <UiTestSword/" + linked.UniqueID + "> cheap", Type = ChatType.Shout });
            Feed(new S.Chat { Message = "UiAlly: group line", Type = ChatType.Group });
            Feed(new S.Chat { Message = "Guildie: guild line", Type = ChatType.Guild });
            Feed(new S.Chat { Message = "You have reached level 31.", Type = ChatType.LevelUp });
            Feed(new S.Chat { Message = "UiAlly: hey UiTester, follow me", Type = ChatType.Normal });
            Feed(new S.Chat { Message = "The server restarts in 10 minutes. Please find a safe place.", Type = ChatType.Announcement });
            Feed(new S.Chat { Message = "Same line twice", Type = ChatType.Hint });
            Feed(new S.Chat { Message = "Same line twice", Type = ChatType.Hint });
            Feed(new S.Chat { Message = "A very long system line that has to wrap over more than one row of the chat box because it goes on and on and on.", Type = ChatType.System });
            LogChat();
        }

        private static void MinimapMap()
        {
            UserObject user = GameScene.User;
            if (user == null) return;
            Feed(new S.MapChanged
            {
                MapIndex = 2, FileName = "0", Title = "BichonProvince", MiniMap = 101, BigMap = 101, Lights = LightSetting.Night,
                Location = user.CurrentLocation, Direction = MirDirection.Right,
            });
            Point c = user.CurrentLocation;
            var info = new ClientMapInfo { Title = "BichonProvince", Width = Scene.MapControl.Width, Height = Scene.MapControl.Height, BigMap = 101 };
            info.NPCs.Add(new ClientNPCInfo { ObjectID = 9001, Name = "BichonProvince_Merchant_Herbalist", Location = new Point(c.X + 6, c.Y - 4), ShowOnBigMap = true, Icon = 1, CanTeleportTo = true });
            info.NPCs.Add(new ClientNPCInfo { ObjectID = 9002, Name = "BichonProvince_Blacksmith", Location = new Point(c.X - 8, c.Y + 5), ShowOnBigMap = true });
            info.Movements.Add(new ClientMovementInfo { Destination = 3, Title = "BichonWall", Location = new Point(c.X + 10, c.Y + 10) });
            info.Movements.Add(new ClientMovementInfo { Destination = 4, Title = "DeadForest", Location = new Point(c.X - 60, c.Y - 2) });
            Feed(new S.NewMapInfo { MapIndex = 2, Info = info });
            LogMinimap();
        }

        private static void MinimapObjects()
        {
            UserObject user = GameScene.User;
            if (user == null) return;
            Point c = user.CurrentLocation;
            Feed(new S.ObjectMonster { ObjectID = 501, Name = "Hen", NameColour = Color.White, Location = new Point(c.X + 4, c.Y + 2), Image = Monster.Hen, Direction = MirDirection.Left });
            Feed(new S.ObjectMonster { ObjectID = 502, Name = "Deer", NameColour = Color.White, Location = new Point(c.X - 5, c.Y - 3), Image = Monster.Deer, Direction = MirDirection.Up });
            Feed(new S.ObjectNPC { ObjectID = 9001, Name = "BichonProvince_Merchant_Herbalist", NameColour = Color.Lime, Image = 0, Location = new Point(c.X + 6, c.Y - 4), Direction = MirDirection.Down });
            Feed(new S.AddMember { Name = "UiTester" });
            Feed(new S.AddMember { Name = "UiAlly" });
            Feed(new S.GroupMembersMap { PlayerName = "UiAlly", PlayerMap = "BichonProvince" });
            Feed(new S.SendMemberLocation { MemberName = "UiAlly", MemberLocation = new Point(c.X - 3, c.Y + 7) });
            LogMinimap();
        }

        private static void TrackerQuests()
        {
            UserObject user = GameScene.User;
            ModernUi ui = ModernUiNow;
            if (user == null || ui == null) return;
            // quest V2: the login picture settles the model, then two accepted runs with TrackHint
            Feed(new S.QuestHistory { Full = true });
            var herb = new S.QuestDefinition { Quest = 7001, Name = "The Herbalist's Errand" };
            herb.Endpoints.Add(new S.QuestDefinition.Endpoint { Role = QuestEndpointRole.Start, Kind = QuestEndpointKind.Npc, TargetId = 90, Name = "BichonProvince_Merchant_Herbalist" });
            herb.Steps.Add(new S.QuestDefinition.Step { Index = 70011, Order = 1 });
            herb.Objectives.Add(new S.QuestDefinition.Objective { Index = 700111, Step = 70011, Type = QuestObjectiveType.Kill, TargetKind = QuestTargetKind.MonsterGroup, TargetName = "Hen", Count = 5 });
            herb.Objectives.Add(new S.QuestDefinition.Objective { Index = 700112, Step = 70011, Type = QuestObjectiveType.Collect, TargetKind = QuestTargetKind.ItemGroup, TargetName = "Hen Feather", Count = 2 });
            var letter = new S.QuestDefinition { Quest = 7002, Name = "A Letter for the Blacksmith of the Bichon Province" };
            letter.Endpoints.Add(new S.QuestDefinition.Endpoint { Role = QuestEndpointRole.Start, Kind = QuestEndpointKind.Npc, TargetId = 90, Name = "BichonProvince_Merchant_Herbalist" });
            letter.Steps.Add(new S.QuestDefinition.Step { Index = 70021, Order = 1, NextStep = -1 });
            Feed(herb);
            Feed(letter);
            Feed(new S.QuestStates { States = new List<S.QuestStates.Entry>
            {
                new S.QuestStates.Entry { Quest = 7001, Status = QuestRunStatus.Active, Step = 70011, TrackHint = true },
                new S.QuestStates.Entry { Quest = 7002, Status = QuestRunStatus.Ready, Step = -1, TrackHint = true },
            } });
            Feed(new S.QuestProgress { Progress = new List<S.QuestProgress.Entry>
            {
                new S.QuestProgress.Entry { Quest = 7001, Objective = 700111, Value = 3 },
                new S.QuestProgress.Entry { Quest = 7001, Objective = 700112, Value = 2 },
            } });
            _log.Add($"   tracker visible={ui.Tracker?.Visible} tracked=[{string.Join(",", ui.Tracker?.Tracked ?? new List<int>())}]");
        }

        private static void BigMapDetails()
        {
            UserObject user = GameScene.User;
            if (user == null) return;
            Point c = user.CurrentLocation;
            Feed(new S.ServerRules { MapDetailsEnabled = true });
            var p = new S.MapDetails { MapIndex = 2, Part = 0, PartCount = 1 };
            p.Respawns.Add(new ClientMapRespawn { MonsterIndex = 1, Location = new Point(c.X + 20, c.Y + 15), Spread = 10, Count = 12 });
            p.Respawns.Add(new ClientMapRespawn { MonsterIndex = 2, Location = new Point(c.X - 30, c.Y - 20), Spread = 6, Count = 4 });
            p.SafeZones.Add(new ClientMapSafeZone { Location = c, Size = 8, StartPoint = true });
            Feed(p);
            if (GameScene.MonsterInfoList.Find(m => m.Index == 1) == null)
            {
                GameScene.UpsertMonsterInfo(new ClientMonsterInfo { Index = 1, Name = "Hen", Image = Monster.Hen, Level = 2 });
                GameScene.UpsertMonsterInfo(new ClientMonsterInfo { Index = 2, Name = "Deer", Image = Monster.Deer, Level = 48 });
            }
            BigMap?.TestTab(0);
            LogBigMap();
        }

        private static void BigMapWorld()
        {
            var setup = new WorldMapSetup { Enabled = true };
            setup.Icons.Add(new WorldMapIcon { ImageIndex = 1, Title = "BichonProvince", MapIndex = 2 });
            setup.Icons.Add(new WorldMapIcon { ImageIndex = 2, Title = "Serpent Valley", MapIndex = 5 });
            Feed(new S.WorldMapSetupInfo { Setup = setup, TeleportToNPCCost = 500 });
            BigMap?.SetView(BigMapData.World, true);
            LogBigMap();
        }
    }
}

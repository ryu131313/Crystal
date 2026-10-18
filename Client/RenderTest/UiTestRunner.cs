using System.Drawing.Imaging;
using System.Reflection;
using System.Text;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirObjects;
using Client.MirScenes;
using Client.MirScenes.Dialogs;
using S = ServerPackets;

namespace Client.RenderTest
{
    /// <summary>
    /// Offline UI test (Desktop-Modern-UI-Plan phase 1a). <c>Client.exe --uitest --out &lt;dir&gt; [--uistyle Classic|Modern]</c> runs
    /// the real game loop, builds a real GameScene with no server, feeds it synthetic server packets, presses keybinds and clicks
    /// classic item cells, records every packet the client tries to send, and saves one screenshot + one state dump per step
    /// (<c>NN_name.png</c>, <c>uitest.log</c>). Test mode only: never connects (Network.Enqueue is captured), never saves settings.
    /// The script uses only APIs that exist both before and after the UI seam (reflection for the rest), so the same file
    /// runs against an older build and the two logs / screenshot sets can be diffed (tools\uitest.ps1).
    /// </summary>
    internal static partial class UiTestRunner
    {
        public static bool Enabled { get; private set; }
        public static string UiStyle = "Classic";

        private static readonly List<string> _log = new List<string>();
        private static readonly List<string> _sent = new List<string>();
        private static List<Step> _steps;
        private static int _next;
        private static long _nextAt = -1;
        private static bool _awaitShot;
        private static int _failures;
        private static string _outDir;

        private sealed class Step
        {
            public string Name;
            public Action Act;
            public int DelayMs = 500;
            public bool ClassicOnly;
            public bool ModernOnly;
        }

        /// <summary>Steps that need the modern kit (UiTestRunner.Modern.cs; absent in older builds, so this compiles away there).</summary>
        static partial void AddModernSteps(List<Step> steps);
        static partial void DumpModern(List<string> log);

        public static bool ParseArg(string arg, string[] args, ref int i)
        {
            switch (arg)
            {
                case "--uitest": Enabled = true; return true;
                case "--uistyle": if (i + 1 < args.Length) UiStyle = args[++i]; return true;
            }
            return false;
        }

        /// <summary>After Settings.Load: offline, no connect attempts, the chosen UI style (reflection: older builds have none).</summary>
        public static void ApplySettings()
        {
            MirNetwork.Network.ConnectAttempt = MirNetwork.Network.MaxAttempts;
            MirNetwork.Network.ErrorShown = true;
            FieldInfo style = typeof(Settings).GetField("UiStyle", BindingFlags.Public | BindingFlags.Static);
            if (style != null) style.SetValue(null, Enum.Parse(style.FieldType, UiStyle, true));
            _outDir = RenderTestRunner.OutDir;
        }

        private static bool IsModern => string.Equals(UiStyle, "Modern", StringComparison.OrdinalIgnoreCase);

        /// <summary>Network.Enqueue in test mode: record instead of send.</summary>
        public static void Sent(Packet p)
        {
            if (p == null) return;
            string name = p.GetType().Name;
            if (name == "KeepAlive") return;
            _sent.Add(Describe(p));
        }

        /// <summary>Called every spin from CMain's loop. Returns true when the run is over (the caller closes the form).</summary>
        public static bool Tick(long time)
        {
            try
            {
                if (_steps == null)
                {
                    if (!Libraries.Loaded || time < 2000) return false;
                    _steps = BuildSteps();
                    _nextAt = time;
                }

                if (_awaitShot)
                {
                    if (time < _nextAt) return false;
                    Capture(_steps[_next - 1]);
                    _awaitShot = false;
                }

                if (_next >= _steps.Count)
                {
                    Finish();
                    return true;
                }

                if (time < _nextAt) return false;

                Step step = _steps[_next++];
                _sent.Clear();
                if ((step.ClassicOnly && IsModern) || (step.ModernOnly && !IsModern))
                {
                    _log.Add($"== {_next:00} {step.Name}: skipped ({(step.ClassicOnly ? "classic" : "modern")} only)");
                    return false;
                }
                try
                {
                    step.Act();
                }
                catch (Exception ex)
                {
                    _failures++;
                    _log.Add($"!! {_next:00} {step.Name} threw: {ex}");
                }
                _awaitShot = true;
                _nextAt = time + step.DelayMs;
            }
            catch (Exception ex)
            {
                _failures++;
                _log.Add("!! runner: " + ex);
            }
            return false;
        }

        private static void Capture(Step step)
        {
            string tag = $"{_next:00}_{step.Name}";
            try
            {
                Directory.CreateDirectory(_outDir);
                using (var bmp = DXManager.CaptureBackBuffer())
                    bmp.Save(Path.Combine(_outDir, tag + ".png"), ImageFormat.Png);
            }
            catch (Exception ex)
            {
                _log.Add($"!! {tag} screenshot: {ex.Message}");
            }

            _log.Add("== " + tag);
            for (int i = 0; i < _sent.Count; i++) _log.Add("   sent " + _sent[i]);
            try { DumpState(); } catch (Exception ex) { _failures++; _log.Add("!! dump: " + ex); }
        }

        private static void Finish()
        {
            string errors = File.Exists("Error.txt") ? File.ReadAllText("Error.txt") : "";
            _log.Add(errors.Length == 0 ? "Error.txt: none" : "Error.txt:\n" + errors);
            _log.Add(_failures == 0 ? "uitest RESULT: PASS" : $"uitest RESULT: FAIL ({_failures} step exceptions)");
            Environment.ExitCode = _failures == 0 ? 0 : 1;
            try { File.WriteAllLines(Path.Combine(_outDir, "uitest.log"), _log); } catch { }
        }

        // ------------------------------------------------------------------ helpers

        private static GameScene Scene => GameScene.Scene;
        private static void Feed(Packet p) => Scene.ProcessPacket(p);

        private static void PressBind(KeybindOptions function)
        {
            KeyBind bind = null;
            foreach (KeyBind k in CMain.InputKeys.Keylist)
                if (k.function == function && k.Key != Keys.None) { bind = k; break; }
            if (bind == null) { _log.Add("   (no key bound for " + function + ")"); return; }

            CMain.Alt = bind.RequireAlt == 1;
            CMain.Shift = bind.RequireShift == 1;
            CMain.Ctrl = bind.RequireCtrl == 1;
            CMain.Tilde = bind.RequireTilde == 1;
            Keys modifiers = (CMain.Alt ? Keys.Alt : 0) | (CMain.Shift ? Keys.Shift : 0) | (CMain.Ctrl ? Keys.Control : 0);
            MirScene.ActiveScene.OnKeyDown(new KeyEventArgs(bind.Key | modifiers));
            CMain.Alt = CMain.Shift = CMain.Ctrl = CMain.Tilde = false;
        }

        private static MirItemCell InvCell(int slot)
        {
            int belt = GameScene.User.BeltIdx;
            return slot < belt ? Scene.BeltDialog.Grid[slot] : Scene.InventoryDialog.Grid[slot - belt];
        }

        private static void Click(MirItemCell cell, MouseButtons button = MouseButtons.Left, bool shift = false, bool alt = false)
        {
            CMain.Shift = shift;
            CMain.Alt = alt;
            cell.OnMouseClick(new MouseEventArgs(button, 1, 0, 0, 0));
            CMain.Shift = CMain.Alt = false;
        }

        private static void DoubleClick(MirItemCell cell) => cell.OnMouseDoubleClick(new MouseEventArgs(MouseButtons.Left, 2, 0, 0, 0));

        /// <summary>Closes the newest message / amount box (the classic prompts), like pressing its cancel button.</summary>
        private static void CloseTopPrompt()
        {
            for (int i = Scene.Controls.Count - 1; i >= 0; i--)
            {
                MirControl c = Scene.Controls[i];
                if (c is MirAmountBox || c is MirMessageBox || c is MirInputBox)
                {
                    c.Dispose();
                    return;
                }
            }
        }

        /// <summary>Closes every open classic prompt (a modal message box takes the keyboard).</summary>
        private static void CloseAllPrompts()
        {
            for (int guard = 0; guard < 20; guard++)
            {
                int before = Scene.Controls.Count;
                CloseTopPrompt();
                if (Scene.Controls.Count == before) return;
            }
        }

        // ------------------------------------------------------------------ synthetic data

        private static ulong _uid = 1000;
        private static readonly Dictionary<string, ItemInfo> _infos = new Dictionary<string, ItemInfo>();

        private static ItemInfo Info(string name)
        {
            if (_infos.Count == 0)
            {
                AddInfo(new ItemInfo { Index = 9001, Name = "UiTestPotion", Type = ItemType.Potion, Image = 1, StackSize = 50, Price = 10, Shape = 0, Stats = new Stats() });
                AddInfo(new ItemInfo { Index = 9002, Name = "UiTestSword", Type = ItemType.Weapon, Image = 40, Durability = 5000, Price = 500, Shape = 1, Stats = new Stats() });
                AddInfo(new ItemInfo { Index = 9003, Name = "UiTestDress", Type = ItemType.Armour, Image = 60, Durability = 5000, Price = 400, Stats = new Stats() });
                AddInfo(new ItemInfo { Index = 9004, Name = "UiTestRing", Type = ItemType.Ring, Image = 120, Durability = 3000, Price = 300, Stats = new Stats() });
                AddInfo(new ItemInfo { Index = 9005, Name = "UiTestScroll", Type = ItemType.Scroll, Image = 200, StackSize = 20, Price = 50, Shape = 1, Stats = new Stats() });
            }
            return _infos[name];
        }

        private static void AddInfo(ItemInfo info)
        {
            _infos[info.Name] = info;
            GameScene.ItemInfoList.Add(info);
        }

        private static UserItem Item(string name, ushort count = 1)
        {
            ItemInfo info = Info(name);
            return new UserItem(info) { UniqueID = _uid++, Count = count, CurrentDura = info.Durability, MaxDura = info.Durability };
        }

        private static void EnterGame()
        {
            MapReader map = new MapReader(Path.Combine(Settings.MapPath, "0.map"));
            Point centre = new Point(map.Width / 2, map.Height / 2);

            MirScene old = MirScene.ActiveScene;
            MirScene.ActiveScene = new GameScene();
            if (old != null && old != MirScene.ActiveScene) old.Dispose();

            Feed(new S.MapInformation { MapIndex = 1, FileName = "0", Title = "UiTest", Lights = LightSetting.Day });

            UserItem[] inventory = new UserItem[46];
            inventory[8] = Item("UiTestPotion", 20);
            inventory[9] = Item("UiTestSword");
            inventory[10] = Item("UiTestRing");
            inventory[11] = Item("UiTestScroll", 5);
            inventory[12] = Item("UiTestPotion", 7);
            UserItem[] equipment = new UserItem[Enum.GetNames(typeof(EquipmentSlot)).Length];
            equipment[(int)EquipmentSlot.Armour] = Item("UiTestDress");

            Feed(new S.UserInformation
            {
                ObjectID = 1,
                RealId = 1,
                Name = "UiTester",
                NameColour = Color.White,
                Class = MirClass.Warrior,
                Gender = MirGender.Male,
                Level = 30,
                Location = centre,
                Direction = MirDirection.Down,
                HP = 300,
                MP = 100,
                Experience = 10,
                MaxExperience = 1000,
                Inventory = inventory,
                Equipment = equipment,
                QuestInventory = new UserItem[40],
                Gold = 100000,
                Credit = 0,
            });
        }

        private static ClientBuff Buff(BuffType type, int[] values = null)
        {
            return new ClientBuff
            {
                Type = type,
                Caster = "UiTest",
                Visible = true,
                ObjectID = GameScene.User.ObjectID,
                ExpireTime = 600000,
                Infinite = false,
                Stats = new Stats(),
                Values = values ?? new int[0],
            };
        }

        // ------------------------------------------------------------------ the script

        private static List<Step> BuildSteps()
        {
            var steps = new List<Step>
            {
                new Step { Name = "enter_game", Act = EnterGame, DelayMs = 1500 },
                new Step { Name = "inventory_open", Act = () => PressBind(KeybindOptions.Inventory), ClassicOnly = true },
                new Step { Name = "pick_potion", Act = () => Click(InvCell(8)), ClassicOnly = true },
                new Step { Name = "drop_on_empty", Act = () => Click(InvCell(15)), ClassicOnly = true },
                new Step { Name = "move_reply", Act = () => Feed(new S.MoveItem { Grid = MirGridType.Inventory, From = 8, To = 15, Success = true }) },
                new Step { Name = "pick_to_belt", Act = () => { Click(InvCell(15)); Click(InvCell(0)); }, ClassicOnly = true },
                new Step { Name = "belt_reply", Act = () => Feed(new S.MoveItem { Grid = MirGridType.Inventory, From = 15, To = 0, Success = true }) },
                new Step { Name = "belt_key_use", Act = () => PressBind(KeybindOptions.Belt1) },
                new Step { Name = "use_reply", Act = () => Feed(new S.UseItem { Grid = MirGridType.Inventory, UniqueID = GameScene.User.Inventory[0]?.UniqueID ?? 0, Success = true }) },
                new Step { Name = "equip_sword", Act = () => DoubleClick(InvCell(9)), ClassicOnly = true },
                new Step { Name = "equip_reply", Act = () => Feed(new S.EquipItem { Grid = MirGridType.Inventory, UniqueID = GameScene.User.Inventory[9]?.UniqueID ?? 0, To = (int)EquipmentSlot.Weapon, Success = true }) },
                new Step { Name = "character_open", Act = () => PressBind(KeybindOptions.Equipment), ClassicOnly = true },
                new Step { Name = "split_prompt", Act = () => Click(InvCell(12), MouseButtons.Left, shift: true), ClassicOnly = true },
                new Step { Name = "split_cancel", Act = CloseTopPrompt, ClassicOnly = true },
                new Step { Name = "lock_then_fail", Act = () => { Click(InvCell(10)); Click(InvCell(16)); Feed(new S.MoveItem { Grid = MirGridType.Inventory, From = 10, To = 16, Success = false }); }, ClassicOnly = true },
                new Step { Name = "buff_swiftfeet", Act = () => Feed(new S.AddBuff { Buff = Buff(BuffType.SwiftFeet) }) },
                new Step { Name = "buff_transform", Act = () => Feed(new S.AddBuff { Buff = Buff(BuffType.Transform, new[] { 3 }) }) },
                new Step { Name = "buff_remove_swiftfeet", Act = () => Feed(new S.RemoveBuff { ObjectID = GameScene.User.ObjectID, Type = BuffType.SwiftFeet }) },
                new Step { Name = "buff_remove_transform", Act = () => Feed(new S.RemoveBuff { ObjectID = GameScene.User.ObjectID, Type = BuffType.Transform }) },
                new Step { Name = "chat_lines", Act = () =>
                    {
                        Feed(new S.Chat { Message = "UiTest system line", Type = ChatType.System });
                        Feed(new S.Chat { Message = "UiTester: hello", Type = ChatType.Normal });
                        Feed(new S.Chat { Message = "UiTest shout", Type = ChatType.Shout });
                    } },
                new Step { Name = "group_add", Act = () => { Feed(new S.AddMember { Name = "UiTester" }); Feed(new S.AddMember { Name = "UiAlly" }); } },
                new Step { Name = "group_open", Act = () => PressBind(KeybindOptions.Group), ClassicOnly = true },
                new Step { Name = "group_delete_member", Act = () => Feed(new S.DeleteMember { Name = "UiAlly" }) },
                new Step { Name = "group_delete", Act = () => Feed(new S.DeleteGroup()) },
                new Step { Name = "guild_status", Act = () => Feed(new S.GuildStatus
                    {
                        GuildName = "UiGuild", GuildRankName = "Leader", Level = 3, Experience = 50, MaxExperience = 1000, Gold = 5000,
                        SparePoints = 2, MemberCount = 2, MaxMembers = 20, MyOptions = (GuildRankOptions)255, MyRankId = 0,
                    }) },
                new Step { Name = "guild_members", Act = () =>
                    {
                        var leader = new GuildRank { Name = "Leader", Index = 0, Options = (GuildRankOptions)255 };
                        leader.Members.Add(new GuildMember { Name = "UiTester", Id = 1, Online = true });
                        var member = new GuildRank { Name = "Members", Index = 1 };
                        member.Members.Add(new GuildMember { Name = "UiAlly", Id = 2, Online = false });
                        Feed(new S.GuildMemberChange { Status = 255, Ranks = new List<GuildRank> { leader, member } });
                    } },
                new Step { Name = "guild_buffs", Act = () =>
                    {
                        var stats = new Stats();
                        stats[Stat.MaxDC] = 5;
                        var info = new GuildBuffInfo { Id = 1, Icon = 1, Name = "UiBuff", Stats = stats };
                        var p = new S.GuildBuffList();
                        p.GuildBuffs.Add(info);
                        p.ActiveBuffs.Add(new GuildBuff { Id = 1, Active = true, ActiveTimeRemaining = 60 });
                        Feed(p);
                    } },
                new Step { Name = "guild_open", Act = () => PressBind(KeybindOptions.Guilds), ClassicOnly = true },
                new Step { Name = "guild_close", Act = () => PressBind(KeybindOptions.Guilds), ClassicOnly = true },
                new Step { Name = "trade_accept", Act = () => Feed(new S.TradeAccept { Name = "UiAlly" }) },
                new Step { Name = "trade_guest_item", Act = () =>
                    {
                        UserItem[] items = new UserItem[10];
                        items[0] = Item("UiTestScroll", 3);
                        Feed(new S.TradeItem { TradeItems = items });
                        Feed(new S.TradeGold { Amount = 1234 });
                    } },
                new Step { Name = "trade_cancel", Act = () => Feed(new S.TradeCancel { Unlock = false }) },
                new Step { Name = "mail_receive", Act = () =>
                    {
                        var mail = new ClientMail { MailID = 1, SenderName = "UiAlly", Message = "UiTest letter", DateSent = new DateTime(2026, 10, 18), Gold = 0 };
                        Feed(new S.ReceiveMail { Mail = new List<ClientMail> { mail } });
                    } },
                new Step { Name = "npc_page", Act = () => Feed(new S.NPCResponse { Page = new List<string> { "Welcome, UiTester.", "<Buy/@buy>  <Sell/@sell>  <Storage/@storage>" } }) },
                new Step { Name = "npc_goods", Act = () => Feed(new S.NPCGoods { List = new List<UserItem> { Item("UiTestPotion"), Item("UiTestScroll") }, Rate = 1F, Type = PanelType.Buy }) },
                new Step { Name = "npc_sell_panel", Act = () => Feed(new S.NPCSell()) },
                new Step { Name = "sell_pick_drop", Act = () => { Click(InvCell(11)); Click(Scene.NPCDropDialog.ItemCell); }, ClassicOnly = true },
                new Step { Name = "sell_confirm", Act = () => Scene.NPCDropDialog.ConfirmButton.OnMouseClick(new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0)), ClassicOnly = true },
                new Step { Name = "sell_reply", Act = () => Feed(new S.SellItem { UniqueID = GameScene.User.Inventory[11]?.UniqueID ?? 0, Count = 5, Success = true }) },
                new Step { Name = "storage_open", Act = () =>
                    {
                        UserItem[] storage = new UserItem[80];
                        storage[0] = Item("UiTestRing");
                        Feed(new S.UserStorage { Storage = storage });
                        Feed(new S.NPCStorage());
                    } },
                new Step { Name = "store_click", Act = () => { Click(InvCell(10)); Click(Scene.StorageDialog.Grid[5]); }, ClassicOnly = true },
                new Step { Name = "store_reply", Act = () => Feed(new S.StoreItem { From = 10, To = 5, Success = true }) },
                new Step { Name = "refine_open", Act = () => Feed(new S.NPCRefine { Rate = 1F, Refining = false }) },
                new Step { Name = "close_all", Act = () => { CloseAllPrompts(); PressBind(KeybindOptions.Closeall); } },
                new Step { Name = "options_open", Act = () => PressBind(KeybindOptions.Options), ClassicOnly = true },
                new Step { Name = "options_close", Act = () => PressBind(KeybindOptions.Options), ClassicOnly = true },
                new Step { Name = "logout", Act = () => Feed(new S.LogOutSuccess { Characters = new List<SelectInfo>() }), DelayMs = 800 },
                new Step { Name = "relog", Act = EnterGame, DelayMs = 1500 },
                new Step { Name = "relog_inventory", Act = () => PressBind(KeybindOptions.Inventory), ClassicOnly = true },
            };
            AddModernSteps(steps);
            return steps;
        }

        // ------------------------------------------------------------------ state dump (same text before and after the seam)

        private static void DumpState()
        {
            UserObject user = GameScene.User;
            if (Scene == null || user == null) { _log.Add("   (no scene / user)"); return; }

            _log.Add($"   gold={GameScene.Gold} sprint={user.Sprint} transform={user.TransformType} hp={user.Stats[Stat.HP]} maxDC={user.Stats[Stat.MaxDC]} weapon={user.Weapon} armour={user.Armour}");
            _log.Add("   inventory " + Items(user.Inventory));
            _log.Add("   equipment " + Items(user.Equipment));
            _log.Add("   storage " + Items(GameScene.Storage));
            _log.Add("   trade guest " + Items(GuestTradeDialog.GuestItems));
            _log.Add("   selected " + SelectedDescription());
            _log.Add("   group [" + string.Join(",", GroupDialog.GroupList) + "]");
            _log.Add($"   guild={user.GuildName} rank={user.GuildRankName} myOptions={(int)GuildDialog.MyOptions}");
            _log.Add("   mail " + user.Mail.Count);

            if (IsModern)
            {
                DumpModern(_log);
                return;
            }

            _log.Add("   locked " + LockedCells());
            _log.Add($"   chat lines={Scene.ChatDialog.FullHistory.Count} last=\"{(Scene.ChatDialog.FullHistory.Count > 0 ? Scene.ChatDialog.FullHistory[^1].Text : "")}\"");
            _log.Add($"   buffs={Scene.BuffsDialog.Buffs.Count} guildBuffs={Scene.GuildDialog.EnabledBuffs.Count} guildMembers={Scene.GuildDialog.MemberCount}");
            var visible = new List<string>();
            for (int i = 0; i < Scene.Controls.Count; i++)
                if (Scene.Controls[i].Visible) visible.Add(Scene.Controls[i].GetType().Name);
            _log.Add("   visible " + string.Join(",", visible));
        }

        private static string Items(UserItem[] items)
        {
            if (items == null) return "null";
            var sb = new StringBuilder();
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i] == null) continue;
                sb.Append(i).Append(':').Append(items[i].Info?.Name ?? "?").Append('x').Append(items[i].Count).Append(' ');
            }
            return sb.ToString();
        }

        private static string LockedCells()
        {
            var sb = new StringBuilder();
            void Scan(string name, MirItemCell[] grid)
            {
                if (grid == null) return;
                for (int i = 0; i < grid.Length; i++)
                    if (grid[i] != null && grid[i].Locked) sb.Append(name).Append('[').Append(grid[i].ItemSlot).Append("] ");
            }
            Scan("bag", Scene.InventoryDialog.Grid);
            Scan("belt", Scene.BeltDialog.Grid);
            Scan("equip", Scene.CharacterDialog.Grid);
            Scan("storage", Scene.StorageDialog.Grid);
            return sb.ToString();
        }

        /// <summary>The picked-up item: GameScene.SelectedCell before the seam, ItemMoves.Selected after (reflection, so both compile).</summary>
        private static string SelectedDescription()
        {
            FieldInfo cellField = typeof(GameScene).GetField("SelectedCell", BindingFlags.Public | BindingFlags.Static);
            object picked = cellField != null
                ? cellField.GetValue(null)
                : typeof(GameScene).Assembly.GetType("Client.MirScenes.ItemMoves")?.GetProperty("Selected", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (picked == null) return "none";
            Type t = picked.GetType();
            object grid = t.GetProperty("GridType")?.GetValue(picked) ?? t.GetField("GridType")?.GetValue(picked);
            object slot = t.GetProperty("ItemSlot")?.GetValue(picked);
            return $"{grid}[{slot}]";
        }

        private static string Describe(Packet p)
        {
            var sb = new StringBuilder(p.GetType().Name);
            foreach (FieldInfo f in p.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object v = f.GetValue(p);
                string text = v switch
                {
                    null => "null",
                    string s => "\"" + s + "\"",
                    UserItem item => item.Info?.Name ?? item.UniqueID.ToString(),
                    Array a => "[" + a.Length + "]",
                    _ => v.GetType().IsPrimitive || v.GetType().IsEnum ? v.ToString() : v.GetType().Name,
                };
                sb.Append(' ').Append(f.Name).Append('=').Append(text);
            }
            return sb.ToString();
        }
    }
}

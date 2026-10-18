using Client.MirControls;
using Client.MirObjects;
using Client.MirScenes;
using Client.MirScenes.Modern;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Hud;
using S = ServerPackets;

namespace Client.RenderTest
{
    /// <summary>
    /// Phase 2 area "HUD" steps (run with tools\uitest.ps1 -UiStyle Modern): the bottom HUD fed by synthetic packets (HP / MP
    /// with the trail and the low pulse, EXP gain and level up, buffs, poisons, magics with a cooldown and an armed toggle,
    /// the Ctrl / Shift skill views, a slot click), the mode list and a pick, the plate picker, the gold plate, the durability
    /// strip and its card, the belt (player and hero cells), the hero frame and its behaviour chip, party frames, the game
    /// menu (Esc, arrows, search, close), a strip button, the Log out corner, the classic switch prompt, HUD scale 1.5 and a
    /// theme. Everything opened is closed at the end (the other areas' steps run after these in the same process).
    /// </summary>
    internal static partial class UiTestRunner
    {
        private static ModernUi Mui => GameScene.Ui as ModernUi;
        private static HudRig Rig => Mui?.HudRig;

        static partial void AddHudSteps(List<Step> steps)
        {
            Modern(steps, "hud_initial", () =>
            {
                SetScale(1f, true);
                ModernTheme.Apply(ModernTheme.DefaultTheme, null);
                // two potions onto the belt (cells 1 and 2) through the real move replies
                Feed(new S.MoveItem { Grid = MirGridType.Inventory, From = 8, To = 0, Success = true });
                Feed(new S.MoveItem { Grid = MirGridType.Inventory, From = 12, To = 1, Success = true });
                Feed(new S.ChangeAMode { Mode = AttackMode.All });
                Feed(new S.ChangePMode { Mode = PetMode.Both });
                LogHud("initial");
            }, 900);
            Modern(steps, "hud_hit", () =>
            {
                Feed(new S.HealthChanged { HP = 180, MP = 70 });
                LogHud("hit");
            }, 350);
            Modern(steps, "hud_hit_trail", () => LogHud("trail"), 900);
            Modern(steps, "hud_low_hp", () =>
            {
                Feed(new S.HealthChanged { HP = 60, MP = 20 });
                LogHud("low");
            }, 1500);
            Modern(steps, "hud_exp_gain", () =>
            {
                Feed(new S.GainExperience { Amount = 255 });
                LogHud("exp");
            }, 400);
            Modern(steps, "hud_level_up", () =>
            {
                Feed(new S.LevelChanged { Level = 31, Experience = 400, MaxExperience = 2000 });
                LogHud("level");
            }, 450);
            Modern(steps, "hud_buffs", () =>
            {
                Feed(new S.AddBuff { Buff = Buff(BuffType.SwiftFeet) });
                ClientBuff haste = Buff(BuffType.Haste);
                haste.Infinite = true;
                Feed(new S.AddBuff { Buff = haste });
                ClientBuff ending = Buff(BuffType.Exp);
                ending.ExpireTime = 6000;
                Feed(new S.AddBuff { Buff = ending });
                Feed(new S.Poisoned { Poison = PoisonType.Green | PoisonType.Slow });
                LogHud("buffs");
            }, 700);
            Modern(steps, "hud_buff_hover", () =>
            {
                MirControl cell = FindHud<BuffCell>();
                MouseAt(Centre(cell));
                _log.Add("   hud buff hint=\"" + (cell?.Hint ?? "").Replace("\n", " | ") + "\"");
            });
            Modern(steps, "hud_magics", () =>
            {
                MouseAt(new Point(-1000, -1000));
                Feed(new S.NewMagic { Magic = new ClientMagic { Name = "Fencing", Spell = Spell.Fencing, Icon = 0, Key = 1, Level = 2, BaseCost = 0, LevelCost = 0, Delay = 0 } });
                Feed(new S.NewMagic { Magic = new ClientMagic { Name = "Slaying", Spell = Spell.Slaying, Icon = 1, Key = 2, Level = 3, BaseCost = 3, LevelCost = 1, Delay = 0 } });
                Feed(new S.NewMagic { Magic = new ClientMagic { Name = "Thrusting", Spell = Spell.Thrusting, Icon = 6, Key = 3, Level = 1, BaseCost = 4, LevelCost = 0, Delay = 4000, CastTime = CMain.Time } });
                Feed(new S.NewMagic { Magic = new ClientMagic { Name = "HalfMoon", Spell = Spell.HalfMoon, Icon = 7, Key = 5, Level = 0, BaseCost = 90, LevelCost = 2, Delay = 0 } });
                Feed(new S.NewMagic { Magic = new ClientMagic { Name = "FlamingSword", Spell = Spell.FlamingSword, Icon = 24, Key = 9, Level = 1, BaseCost = 7, LevelCost = 1, Delay = 0 } });
                GameScene.User.Slaying = true;
                LogHud("magics");
            }, 1200);
            Modern(steps, "hud_skill_ctrl", () =>
            {
                CMain.Ctrl = true;
                LogHud("ctrl");
            });
            Modern(steps, "hud_skill_shift", () =>
            {
                CMain.Ctrl = false;
                CMain.Shift = true;
                LogHud("shift");
            });
            Modern(steps, "hud_skill_click", () =>
            {
                CMain.Shift = false;
                Rig?.Process();
                ClickAt(Centre(Rig?.Skills.Slots[0]));
                LogHud("click");
            });
            Modern(steps, "hud_mode_list", () =>
            {
                MouseAt(new Point(-1000, -1000));
                ClickAt(Centre(Rig?.AttackSlot));
                _log.Add($"   hud popover open={Mui?.HudPopover?.IsOpen}");
            });
            Modern(steps, "hud_mode_pick", () =>
            {
                GameScene.ChangeAModeTime = 0;
                MirControl card = Mui?.HudPopover?.Card;
                MirControl row = card != null && card.Controls.Count > 2 ? card.Controls[2] : null; // Guild
                ClickAt(Centre(row));
                Feed(new S.ChangeAMode { Mode = AttackMode.Guild });
                _log.Add($"   hud popover open={Mui?.HudPopover?.IsOpen}");
                LogHud("mode");
            });
            Modern(steps, "hud_plate_list", () =>
            {
                ClickAt(Centre(Rig?.Plates.Plates[0]));
                _log.Add($"   hud popover open={Mui?.HudPopover?.IsOpen}");
            });
            Modern(steps, "hud_plate_swap", () =>
            {
                MirControl card = Mui?.HudPopover?.Card;
                PopRow luck = null;
                if (card != null)
                    foreach (MirControl c in card.Controls)
                        if (c is PopRow r && r.Hint == GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernHudStatLuck)) luck = r;
                ClickAt(Centre(luck));
                _log.Add($"   hud plates=[{string.Join(",", Rig?.Plates.Picks ?? Array.Empty<string>())}] saved=\"{HudPlateStore.Saved}\" popover={Mui?.HudPopover?.IsOpen}");
            });
            Modern(steps, "hud_plate_outside", () =>
            {
                ClickAt(Centre(Rig?.Plates.Plates[3]));
                bool open = Mui?.HudPopover?.IsOpen == true;
                ClickAt(new Point(Settings.ScreenWidth / 2, Settings.ScreenHeight / 3));
                _log.Add($"   hud list opened={open} after outside click open={Mui?.HudPopover?.IsOpen}");
            });
            Modern(steps, "hud_plate_gold", () =>
            {
                Rig?.Plates.Pick(5, "Gold");
                ClickAt(Centre(Rig?.Plates.Plates[5]));
                _log.Add($"   hud picked gold={GameScene.PickedUpGold}");
                GameScene.PickedUpGold = false;
            });
            Modern(steps, "hud_dura", () =>
            {
                UserItem armour = GameScene.User.Equipment[(int)EquipmentSlot.Armour];
                if (armour != null) Feed(new S.DuraChanged { UniqueID = armour.UniqueID, CurrentDura = 900 });
                UserItem ring = Item("UiTestRing");
                ring.CurrentDura = 0;
                GameScene.User.Equipment[(int)EquipmentSlot.RingL] = ring;
                ModernEvents.Durability.Emit(null);
            }, 700);
            Modern(steps, "hud_dura_hover", () =>
            {
                MouseAt(Centre(Rig?.Dura));
                _log.Add($"   hud dura cells={Rig?.Dura?.Count} card={Mui?.HudPopover?.IsOpen}");
            });
            Modern(steps, "hud_belt_hover", () =>
            {
                MouseAt(Centre(Rig?.Belt.Cells[0]));
                _log.Add($"   hud tooltip={(ModernTooltip.Shared != null && ModernTooltip.Shared.IsShown ? ModernTooltip.Shared.ShownItem?.Info?.Name : "none")} popover={Mui?.HudPopover?.IsOpen}");
            });
            Modern(steps, "hud_belt_use_key", () =>
            {
                MouseAt(new Point(-1000, -1000));
                PressBind(KeybindOptions.Belt2);
            });
            Modern(steps, "hud_hero_off_click", () =>
            {
                ClickAt(Centre(Rig?.Belt.Cells[8]));
                _log.Add($"   hud hero off={Belt.HeroOff()} selected={(ItemMoves.Selected != null ? ItemMoves.Selected.GridType + "[" + ItemMoves.Selected.ItemSlot + "]" : "none")}");
            });
            Modern(steps, "hud_hero_summon", () =>
            {
                MouseAt(new Point(-1000, -1000));
                UserItem[] inv = new UserItem[10];
                inv[0] = Item("UiTestPotion", 5);
                Feed(new S.HeroInformation
                {
                    ObjectID = 2, RealId = 2, Name = "UiHero", NameColour = Color.White, Class = MirClass.Taoist, Gender = MirGender.Female, Level = 22,
                    Location = GameScene.User.CurrentLocation, HP = 80, MP = 60, Experience = 300, MaxExperience = 1000,
                    Inventory = inv, Equipment = new UserItem[Enum.GetNames(typeof(EquipmentSlot)).Length], QuestInventory = new UserItem[40],
                    Magics = new List<ClientMagic> { new ClientMagic { Name = "Healing", Spell = Spell.Healing, Icon = 1, Key = 17, Level = 2, BaseCost = 5, LevelCost = 1 } },
                });
                Feed(new S.UpdateHeroSpawnState { State = HeroSpawnState.Summoned });
                Feed(new S.SetHeroBehaviour { Behaviour = HeroBehaviour.Follow });
            }, 800);
            Modern(steps, "hud_hero_chip", () =>
            {
                HeroFrame f = Mui?.HeroFrame;
                HeroChip chip = f != null ? FindIn<HeroChip>(f) : null;
                ClickAt(Centre(chip));
                _log.Add($"   hud hero shown={f?.Visible} behaviour={f?.Behaviour} waiting={f?.Waiting} heroOff={Belt.HeroOff()}");
            });
            Modern(steps, "hud_hero_answer", () =>
            {
                Feed(new S.SetHeroBehaviour { Behaviour = HeroBehaviour.Custom });
                CMain.Shift = true;
                LogHud("hero view");
                _log.Add($"   hud hero behaviour={Mui?.HeroFrame?.Behaviour} waiting={Mui?.HeroFrame?.Waiting}");
            });
            Modern(steps, "hud_party", () =>
            {
                CMain.Shift = false;
                Feed(new S.AddMember { Name = "UiLeader" });
                Feed(new S.AddMember { Name = "UiTester" });
                Feed(new S.AddMember { Name = "UiAlly" });
                Feed(new S.AddMember { Name = "UiFar" });
                Point at = GameScene.User.CurrentLocation;
                Feed(new S.ObjectPlayer { ObjectID = 9100, Name = "UiAlly", NameColour = Color.White, Class = MirClass.Wizard, Gender = MirGender.Male, Level = 28,
                    Location = new Point(at.X + 2, at.Y - 1), Direction = MirDirection.Down, Weapon = -1, Armour = 0 });
                Feed(new S.ObjectHealth { ObjectID = 9100, Percent = 22, Expire = 30 });
                Feed(new S.SendMemberLocation { MemberName = "UiAlly", MemberLocation = new Point(at.X + 2, at.Y - 1) });
                Feed(new S.GroupMembersMap { PlayerName = "UiFar", PlayerMap = "Bichon Province" });
                Feed(new S.SendMemberLocation { MemberName = "UiLeader", MemberLocation = new Point(at.X - 12, at.Y + 5) });
            }, 900);
            Modern(steps, "hud_party_check", () =>
            {
                PartyFrames p = Mui?.PartyFrames;
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < (p?.FrameCount ?? 0); i++) sb.Append(p.Frame(i).Name).Append('=').Append(p.Frame(i).Where).Append('/').Append(p.Frame(i).HealthPercent).Append(' ');
                _log.Add($"   hud party shown={p?.Visible} frames={sb}");
            });
            Modern(steps, "hud_plates_off", () =>
            {
                Settings.UiPlates = false;
                _log.Add($"   hud outputs={Mui?.HudOutputs?.LineCount}");
            }, 1300);
            Modern(steps, "hud_plates_on", () => Settings.UiPlates = true, 1300);
            Modern(steps, "hud_menu_esc", () =>
            {
                // MIR2_HUD_FAKE_WINDOWS=1 (HUD worker's private runs only): stand-in windows for the keys nothing registers yet,
                // so the launcher tiles and the strip can be looked at before the window areas land
                if (Environment.GetEnvironmentVariable("MIR2_HUD_FAKE_WINDOWS") == "1")
                    foreach (string key in new[] { "inventory", "character", "skills", "options", "keybinds", "help", "bigmap", "mail" })
                        if (Root != null && !Root.Wm.IsRegistered(key)) Root.Wm.Register(key, () => new KitTestWindow());
                Key(Keys.Escape);
                _log.Add($"   hud menu open={Mui?.GameMenu?.IsOpen} acts={Mui?.GameMenu?.ActCount} tiles={Mui?.GameMenu?.TileCount}");
            }, 700);
            Modern(steps, "hud_menu_arrows", () =>
            {
                Key(Keys.Down);
                Key(Keys.Down);
                _log.Add($"   hud menu selected={Mui?.GameMenu?.Selected} label=\"{Mui?.GameMenu?.Act(Mui.GameMenu.Selected)?.Label}\"");
            });
            Modern(steps, "hud_menu_search", () =>
            {
                Mui?.GameMenu?.SearchField?.SetQuery("char");
                LauncherTile t = Mui?.GameMenu?.Tile("character");
                LauncherTile inv = Mui?.GameMenu?.Tile("inventory");
                _log.Add($"   hud menu character hit={t?.Hit} inventory dim={inv?.Dimmed}");
            });
            Modern(steps, "hud_menu_close", () =>
            {
                Mui?.GameMenu?.SearchField?.SetQuery("");
                Key(Keys.Escape);
                _log.Add($"   hud menu open={Mui?.GameMenu?.IsOpen}");
            });
            Modern(steps, "hud_menu_launch", () =>
            {
                Key(Keys.Escape);
                LauncherTile t = Mui?.GameMenu?.Tile("inventory");
                ClickAt(Centre(t));
                _log.Add($"   hud menu open={Mui?.GameMenu?.IsOpen} inventory open={Root?.Wm.IsOpen("inventory")} (registered={Root?.Wm.IsRegistered("inventory")})");
            }, 700);
            Modern(steps, "hud_strip_toggle", () =>
            {
                MenuStripButton inv = null;
                if (Rig != null) foreach (MenuStripButton b in Rig.Strip) if (b.Entry.Key == "inventory") inv = b;
                ClickAt(Centre(inv));
                _log.Add($"   hud strip inventory visible={inv?.Visible} open={Root?.Wm.IsOpen("inventory")}");
            });
            Modern(steps, "hud_logout_corner", () =>
            {
                Root?.Wm.CloseAll();
                MouseAt(new Point(-1000, -1000));
                Point p = Rig?.LogoutCorner != null ? new Point(Rig.LogoutCorner.DisplayLocation.X + ModernRoot.S(8), Rig.LogoutCorner.DisplayLocation.Y + Rig.LogoutCorner.Size.Height - ModernRoot.S(6)) : new Point(-1000, -1000);
                ClickAt(p);
                _log.Add($"   hud logout popup={Root?.Popups.IsOpen}");
            });
            Modern(steps, "hud_logout_no", () => Key(Keys.N));
            Modern(steps, "hud_switch_classic", () =>
            {
                Key(Keys.Escape);
                ActRow sw = null;
                for (int i = 0; i < (Mui?.GameMenu?.ActCount ?? 0); i++)
                    if (Mui.GameMenu.Act(i).Label == GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernHudMenuSwitchClassic)) sw = Mui.GameMenu.Act(i);
                ClickAt(Centre(sw));
                _log.Add($"   hud switch style={Settings.UiStyle} popup={Root?.Popups.IsOpen} menu={Mui?.GameMenu?.IsOpen}");
            });
            Modern(steps, "hud_switch_no", () =>
            {
                Key(Keys.N);
                Settings.UiStyle = Client.MirScenes.UiStyle.Modern;
            });
            // Enter on a popup / the game menu must not leak its KeyPress ('\r') into the chat input (WinForms skips KeyPress when
            // KeyDown set SuppressKeyPress: mirrored here)
            Modern(steps, "hud_ask_enter", () =>
            {
                MouseAt(new Point(-1000, -1000));
                bool answered = false;
                Root?.Popups.Ask("Enter test", yes => answered = true);
                bool open = Root?.Popups.IsOpen == true;
                bool suppressed = EnterKey();
                _log.Add($"   hud ask enter popup={open}->{Root?.Popups.IsOpen} answered={answered} suppressed={suppressed} chatInput={Mui?.Chat?.Typing}");
                LeaveChat();
            });
            Modern(steps, "hud_menu_enter", () =>
            {
                Mui?.GameMenu?.Show();
                bool open = Mui?.GameMenu?.IsOpen == true;
                bool suppressed = EnterKey(); // runs Resume (the first action)
                _log.Add($"   hud menu enter menu={open}->{Mui?.GameMenu?.IsOpen} suppressed={suppressed} chatInput={Mui?.Chat?.Typing}");
                LeaveChat();
            });
            Modern(steps, "hud_scale_150", () =>
            {
                SetScale(1.5f, false);
                LogHud("scale150");
            }, 900);
            Modern(steps, "hud_theme_red", () =>
            {
                ModernTheme.Apply("Lost Ark red", null);
                LogHud("red");
            }, 900);
            Modern(steps, "hud_scale_075", () =>
            {
                ModernTheme.Apply(ModernTheme.DefaultTheme, null);
                SetScale(0.75f, false);
            }, 900);
            Modern(steps, "hud_cleanup", () =>
            {
                SetScale(1f, true);
                MouseAt(new Point(-1000, -1000));
                CMain.Ctrl = CMain.Shift = false;
                Mui?.HudPopover?.Close();
                Mui?.GameMenu?.Close();
                Root?.Wm.CloseAll();
                Feed(new S.DeleteGroup());
                Feed(new S.RemoveBuff { ObjectID = GameScene.User.ObjectID, Type = BuffType.SwiftFeet });
                Feed(new S.RemoveBuff { ObjectID = GameScene.User.ObjectID, Type = BuffType.Haste });
                Feed(new S.RemoveBuff { ObjectID = GameScene.User.ObjectID, Type = BuffType.Exp });
                Feed(new S.Poisoned { Poison = PoisonType.None });
                Feed(new S.UpdateHeroSpawnState { State = HeroSpawnState.Unsummoned });
                Feed(new S.HealthChanged { HP = 300, MP = 100 });
                GameScene.User.Slaying = false;
                LogHud("cleanup");
            }, 900);
        }

        /// <summary>Enter as WinForms delivers it: KeyDown, then KeyPress '\r' only when KeyDown did not suppress it. True: suppressed.</summary>
        private static bool EnterKey()
        {
            var e = new KeyEventArgs(Keys.Enter);
            MirScene.ActiveScene.OnKeyDown(e);
            if (!e.SuppressKeyPress) MirScene.ActiveScene.OnKeyPress(new KeyPressEventArgs('\r'));
            return e.SuppressKeyPress;
        }

        /// <summary>Leaves the chat input if a leaked key opened it (so later steps start clean).</summary>
        private static void LeaveChat()
        {
            if (Mui?.Chat?.Typing == true) MirScene.ActiveScene.OnKeyDown(new KeyEventArgs(Keys.Escape));
        }

        private static void LogHud(string tag)
        {
            HudRig r = Rig;
            if (r == null) { _log.Add("   hud " + tag + ": no rig"); return; }
            r.Process();
            UserObject u = GameScene.User;
            var slots = new System.Text.StringBuilder();
            for (int i = 0; i < r.Skills.Slots.Length; i++)
            {
                SkillSlot s = r.Skills.Slots[i];
                slots.Append(s.Magic != null ? s.Magic.Name : "-");
                if (s.Cooling) slots.Append("(cd)");
                if (s.NoMp) slots.Append("(nomp)");
                if (s.Armed) slots.Append("(armed)");
                slots.Append(' ');
            }
            _log.Add($"   hud {tag}: hp={u?.HP}/{u?.Stats[Stat.HP]} bar=\"{r.HpBar.Text}\" fill={r.HpBar.Fill:0.00} low={r.HpBar.Low} mp=\"{r.MpBar.Text}\" exp={r.ExpPercent:0.00}% floats={r.FloatCount}" +
                $" buffs={r.BuffCount} poisons={r.PoisonCount} view={r.Skills.View} amode={r.AttackSlot.ShownMode} pmode={r.PetSlot.ShownMode} dura={r.Dura?.Count}");
            _log.Add("   hud slots " + slots);
        }

        private static T FindHud<T>() where T : MirControl => Rig == null ? null : FindIn<T>(Rig);

        private static T FindIn<T>(MirControl root) where T : MirControl
        {
            if (root == null) return null;
            for (int i = 0; i < root.Controls.Count; i++)
            {
                if (root.Controls[i] is T t && t.Visible) return t;
                T deep = FindIn<T>(root.Controls[i]);
                if (deep != null) return deep;
            }
            return null;
        }
    }
}

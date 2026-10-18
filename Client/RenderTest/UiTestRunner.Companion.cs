using Client.MirControls;
using Client.MirObjects;
using Client.MirScenes;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using Client.MirScenes.Modern.Windows;
using S = ServerPackets;

namespace Client.RenderTest
{
    /// <summary>
    /// Phase 4 area "companion" steps (modern only): the hero window (no hero, S.HeroCreateRequest → the New hero panel, the name
    /// popup and C.NewHero, S.NewHero, S.ManageHeroes → the roster, S.HeroInformation + summoned: buffs, doll, behaviour cards and
    /// S.SetHeroBehaviour, the auto-pot slider and S.SetAutoPotValue, the Bag / Skills / Stats tabs, make active → C.ChangeHero and
    /// S.ChangeHero, dismiss), the creatures window (list, select, refused release, rename grant + rename, filter / grade / mode
    /// updates, dismiss + the list answer, a new creature's name prompt), the mount window (refused without a mount, a mount with
    /// gear, fit from the side list + S.EquipSlotItem, ride, take off, restore, closed by S.MountUpdate) and the fishing window
    /// (refused without a rod, a rod with parts, cast / bite / reel in / a catch, the part picker, auto cast, Esc exit reels in).
    /// Starts from a fresh game and closes everything at the end.
    /// </summary>
    internal static partial class UiTestRunner
    {
        static partial void AddCompanionSteps(List<Step> steps)
        {
            CStep(steps, "comp_logout", () => { Root?.Popups.CloseAll(); Feed(new S.LogOutSuccess { Characters = new List<SelectInfo>() }); }, 800);
            CStep(steps, "comp_enter", () => { EnterGame(); SetScale(1f, false); CompInfos(); }, 1500);

            // ---- hero
            CStep(steps, "hero_open_none", () => PressBind(KeybindOptions.HeroInventory), 800);
            CStep(steps, "hero_create_request", () => Feed(new S.HeroCreateRequest { CanCreateClass = new[] { true, true, true, false, false } }), 800);
            CStep(steps, "hero_create_class", () => ClickAt(Offset(Find<Segmented<MirClass>>(HeroWin?.Roster), 0.5f, 0.5f)));
            CStep(steps, "hero_create_name", () => ClickAt(Centre(FindButton(HeroWin, W(ClientTextKeys.ModernCompCreateHero)))), 700);
            CStep(steps, "hero_create_send", () => { ModernTextField f = Find<ModernTextField>(Root?.Popups); if (f != null) f.Text = "UiHeroTwo"; Key(Keys.Enter); }, 700);
            CStep(steps, "hero_create_result", () => Feed(new S.NewHero { Result = 10 }), 800);
            CStep(steps, "hero_create_result_close", () => Key(Keys.Enter));
            CStep(steps, "hero_manage", () =>
            {
                var cur = new ClientHeroInformation { Index = 501, Name = "UiHero", Level = 22, Class = MirClass.Taoist, Gender = MirGender.Female };
                var stored = new ClientHeroInformation { Index = 502, Name = "UiBlade", Level = 18, Class = MirClass.Warrior, Gender = MirGender.Male };
                Feed(new S.ManageHeroes { MaximumCount = 4, CurrentHero = cur, Heroes = new[] { stored, null, null } });
            }, 800);
            CStep(steps, "hero_summoned", () =>
            {
                UserItem[] inv = new UserItem[26];
                inv[2] = Item("UiTestPotion", 9);
                inv[3] = Item("UiTestRing");
                inv[5] = Item("UiTestScroll", 2);
                UserItem[] eq = new UserItem[Enum.GetNames(typeof(EquipmentSlot)).Length];
                eq[(int)EquipmentSlot.Armour] = Item("UiTestDress");
                eq[(int)EquipmentSlot.Weapon] = Item("UiTestSword");
                Feed(new S.HeroInformation
                {
                    ObjectID = 77, RealId = 77, Name = "UiHero", NameColour = Color.White, Class = MirClass.Taoist, Gender = MirGender.Female, Level = 22,
                    Location = GameScene.User.CurrentLocation, Direction = MirDirection.Down, HP = 90, MP = 60, Experience = 120, MaxExperience = 500,
                    Inventory = inv, Equipment = eq, QuestInventory = new UserItem[40],
                    Magics = new List<ClientMagic>
                    {
                        new ClientMagic { Name = "Healing", Spell = Spell.Healing, Icon = 1, Key = 17, Level = 2, BaseCost = 5, LevelCost = 1 },
                        new ClientMagic { Name = "Poisoning", Spell = Spell.Poisoning, Icon = 5, Key = 0, Level = 1, BaseCost = 5, LevelCost = 1 },
                    },
                    AutoPot = true, AutoHPPercent = 40, AutoMPPercent = 20, HPItemIndex = Info("UiTestPotion").Index,
                });
                Feed(new S.UpdateHeroSpawnState { State = HeroSpawnState.Summoned });
                Feed(new S.SetHeroBehaviour { Behaviour = HeroBehaviour.Attack });
                ClientBuff b = Buff(BuffType.Haste);
                b.ObjectID = 77;
                Feed(new S.AddBuff { Buff = b });
                Root?.Wm.Open(HeroWindow.WindowKey);
                ClickTabOf(HeroWin, 0);
            }, 1000);
            CStep(steps, "hero_behaviour_follow", () => ClickAt(Centre(HeroWin?.BehaviourCard(2))));
            CStep(steps, "hero_behaviour_answer", () => Feed(new S.SetHeroBehaviour { Behaviour = HeroBehaviour.Follow }));
            CStep(steps, "hero_pot_slider", () =>
            {
                Slider s = HeroWin?.PotSlider(0);
                Press(Offset(s, 0.75f, 0.5f));
                Release(Offset(s, 0.75f, 0.5f));
                MouseAt(new Point(-1000, -1000));
            }, 900);
            CStep(steps, "hero_pot_answer", () =>
            {
                Feed(new S.SetAutoPotValue { Stat = Stat.HP, Value = 74 });
                _log.Add($"   hero hp% now={GameScene.Hero?.AutoHPPercent}");
            });
            CStep(steps, "hero_tab_bag", () => ClickTabOf(HeroWin, 1), 700);
            CStep(steps, "hero_tab_skills", () => ClickTabOf(HeroWin, 2), 700);
            CStep(steps, "hero_tab_stats", () => ClickTabOf(HeroWin, 3), 700);
            CStep(steps, "hero_key_skills", () => PressBind(KeybindOptions.HeroSkills), 700);
            CStep(steps, "hero_change_ask", () => ClickAt(Centre(CompRosterCard(0))), 700);
            CStep(steps, "hero_change_yes", () => Key(Keys.Enter));
            CStep(steps, "hero_change_answer", () =>
            {
                Feed(new S.ChangeHero { FromIndex = 0 });
                _log.Add($"   hero current={GameScene.CurrentHeroInfo?.Name} stored0={GameScene.HeroStorage[0]?.Name} changing={HeroWin?.Changing}");
            }, 800);
            CStep(steps, "hero_dismiss", () => ClickAt(Centre(HeroWin?.SummonButton)));
            CStep(steps, "hero_dismissed", () => Feed(new S.UpdateHeroSpawnState { State = HeroSpawnState.Unsummoned }), 800);
            CStep(steps, "hero_close", () => Root?.Wm.Close(HeroWindow.WindowKey));

            // ---- creatures
            CStep(steps, "creatures_open", () =>
            {
                Feed(new S.UpdateIntelligentCreatureList { CreatureList = new List<ClientIntelligentCreature> { Creature(IntelligentCreatureType.BabyPig, "Oinky", 7400), Creature(IntelligentCreatureType.Kitten, "Mittens", 900) }, CreatureSummoned = true, SummonedCreatureType = IntelligentCreatureType.BabyPig, PearlCount = 12 });
                PressBind(KeybindOptions.Creature);
            }, 1000);
            CStep(steps, "creatures_select", () => ClickAt(Centre(CreatureWin?.Card(1))), 800);
            CStep(steps, "creatures_release_ask", () => ClickAt(Centre(CreatureWin?.ReleaseButton)), 700);
            CStep(steps, "creatures_release_wrong", () => { ModernTextField f = Find<ModernTextField>(Root?.Popups); if (f != null) f.Text = "Kitty"; Key(Keys.Enter); });
            CStep(steps, "creatures_rename_grant", () => Feed(new S.IntelligentCreatureEnableRename()), 700);
            CStep(steps, "creatures_rename_ask", () => ClickAt(Centre(CreatureWin?.RenameButton)), 700);
            CStep(steps, "creatures_rename_send", () => { ModernTextField f = Find<ModernTextField>(Root?.Popups); if (f != null) f.Text = "Whiskers"; Key(Keys.Enter); }, 700);
            CStep(steps, "creatures_filter_gold", () => ClickAt(ChipCentre(CreatureWin?.FilterChips, 1)));
            CStep(steps, "creatures_grade_rare", () => ClickAt(ChipCentre(CreatureWin?.GradeChips, 2)));
            CStep(steps, "creatures_mode_auto", () => ClickAt(Offset(CreatureWin?.ModeControl, 0.25f, 0.5f)));
            CStep(steps, "creatures_select_out", () => ClickAt(Centre(CreatureWin?.Card(0))), 700);
            CStep(steps, "creatures_dismiss", () => ClickAt(Centre(CreatureWin?.SummonButton)));
            CStep(steps, "creatures_dismiss_answer", () =>
            {
                Feed(new S.UpdateIntelligentCreatureList { CreatureList = new List<ClientIntelligentCreature>(GameScene.User.IntelligentCreatures), CreatureSummoned = false, SummonedCreatureType = IntelligentCreatureType.None, PearlCount = 12 });
                _log.Add($"   creatures waiting={CreatureWin?.Waiting} names={string.Join(",", GameScene.User.IntelligentCreatures.ConvertAll(c => c.CustomName))}");
            }, 800);
            CStep(steps, "creatures_new", () => Feed(new S.NewIntelligentCreature { Creature = Creature(IntelligentCreatureType.Chick, "Chick", 10000) }), 800);
            CStep(steps, "creatures_new_name", () => { ModernTextField f = Find<ModernTextField>(Root?.Popups); if (f != null) f.Text = "Peep"; Key(Keys.Enter); }, 800);
            CStep(steps, "creatures_close", () => PressBind(KeybindOptions.Creature));

            // ---- mount
            CStep(steps, "mount_refused", () => PressBind(KeybindOptions.MountWindow), 700);
            CStep(steps, "mount_setup", () =>
            {
                Root?.Popups.CloseAll();
                UserItem mount = Item("UiCompMount");
                mount.CurrentDura = 300;
                mount.MaxDura = 1000;
                mount.Slots[(int)MountSlot.Reins] = Item("UiCompReins");
                GameScene.User.Equipment[(int)EquipmentSlot.Mount] = mount;
                GameScene.User.Inventory[14] = Item("UiCompBells");
                GameScene.User.Inventory[15] = Item("UiCompHay", 3);
                GameScene.User.Inventory[16] = Item("UiCompMount");
                Feed(new S.MountUpdate { ObjectID = GameScene.User.ObjectID, MountType = 2, RidingMount = false });
                PressBind(KeybindOptions.MountWindow);
            }, 1000);
            CStep(steps, "mount_pick_bells", () => ClickAt(Centre(CompCellIn(MountWin, MirGridType.Mount, (int)MountSlot.Bells))), 700);
            CStep(steps, "mount_fit_bells", () => ClickAt(Offset(CompSideRow(MountWin?.Side, 0), 0.6f, 0.5f)), 700);
            CStep(steps, "mount_fit_answer", () => Feed(new S.EquipSlotItem { Grid = MirGridType.Inventory, UniqueID = UidAt(MirGridType.Inventory, 14), To = (int)MountSlot.Bells, GridTo = MirGridType.Mount, Success = true }), 800);
            CStep(steps, "mount_ride", () => ClickAt(Centre(MountWin?.RideButton)), 700);
            CStep(steps, "mount_take_off", () => ClickAt(Centre(MountWin?.TakeOff((int)MountSlot.Reins))), 700);
            CStep(steps, "mount_restore", () => ClickAt(Centre(MountWin?.RestoreButton)), 800);
            CStep(steps, "mount_gone", () =>
            {
                Feed(new S.MountUpdate { ObjectID = GameScene.User.ObjectID, MountType = -1, RidingMount = false });
                _log.Add($"   mount open={Root?.Wm.IsOpen(MountWindow.WindowKey)}");
                GameScene.User.Equipment[(int)EquipmentSlot.Mount] = null;
                ItemMoves.Reset();
            }, 700);

            // ---- fishing
            CStep(steps, "fishing_refused", () => PressBind(KeybindOptions.Fishing), 700);
            CStep(steps, "fishing_setup", () =>
            {
                Root?.Popups.CloseAll();
                UserItem rod = Item("UiCompRod");
                rod.Slots[(int)FishingSlot.Hook] = Item("UiCompHook");
                rod.Slots[(int)FishingSlot.Bait] = Item("UiCompBait", 12);
                rod.Slots[(int)FishingSlot.Reel] = Item("UiCompReel");
                GameScene.User.Equipment[(int)EquipmentSlot.Weapon] = rod;
                GameScene.User.Inventory[17] = Item("UiCompFloat");
                GameScene.User.Inventory[18] = Item("UiCompFloat2");
                GameScene.User.RefreshStats();
                PressBind(KeybindOptions.Fishing);
                _log.Add($"   fishing rod={GameScene.User.HasFishingRod} open={Root?.Wm.IsOpen(FishingWindow.WindowKey)}");
            }, 1000);
            CStep(steps, "fishing_cast_out", () => Feed(new S.FishingUpdate { ObjectID = GameScene.User.ObjectID, Fishing = true, ProgressPercent = 15, ChancePercent = 42, FishingPoint = GameScene.User.CurrentLocation }), 1200);
            CStep(steps, "fishing_bite", () => Feed(new S.FishingUpdate { ObjectID = GameScene.User.ObjectID, Fishing = true, ProgressPercent = 55, ChancePercent = 42, FishingPoint = GameScene.User.CurrentLocation, FoundFish = true }), 900);
            CStep(steps, "fishing_reel_in", () => ClickAt(Centre(FishWin?.ReelButton)), 500);
            CStep(steps, "fishing_caught", () =>
            {
                UserItem fish = Item("UiCompFish");
                Feed(new S.GainedItem { Item = fish });
                Feed(new S.FishingUpdate { ObjectID = GameScene.User.ObjectID, Fishing = false, ProgressPercent = 0, ChancePercent = 42, FishingPoint = GameScene.User.CurrentLocation });
            }, 1300);
            CStep(steps, "fishing_picker", () => ClickAt(Offset(FishWin?.PartRow(1), 0.7f, 0.5f)), 800);
            CStep(steps, "fishing_picker_fit", () => ClickAt(Offset(CompSideRow(Find<ScrollView>(FishWin?.Picker), 0), 0.6f, 0.5f)), 700);
            CStep(steps, "fishing_auto_cast", () => ClickAt(Centre(FishWin?.AutoCastSwitch)));
            CStep(steps, "fishing_esc_exit", () =>
            {
                ClickAt(Centre(FishWin?.EscExitSwitch));
                Feed(new S.FishingUpdate { ObjectID = GameScene.User.ObjectID, Fishing = true, ProgressPercent = 5, ChancePercent = 44, FishingPoint = GameScene.User.CurrentLocation });
            }, 900);
            CStep(steps, "fishing_close_reels_in", () => Key(Keys.Escape), 700);
            CStep(steps, "comp_close_all", () =>
            {
                Feed(new S.FishingUpdate { ObjectID = GameScene.User.ObjectID, Fishing = false, FishingPoint = GameScene.User.CurrentLocation });
                Root?.Popups.CloseAll();
                PressBind(KeybindOptions.Closeall);
                GameScene.User.Equipment[(int)EquipmentSlot.Weapon] = null;
                GameScene.User.RefreshStats();
                ItemMoves.Reset();
            });
        }

        // ------------------------------------------------------------------ helpers

        private static HeroWindow HeroWin => Win<HeroWindow>(HeroWindow.WindowKey);
        private static CreaturesWindow CreatureWin => Win<CreaturesWindow>(CreaturesWindow.WindowKey);
        private static MountWindow MountWin => Win<MountWindow>(MountWindow.WindowKey);
        private static FishingWindow FishWin => Win<FishingWindow>(FishingWindow.WindowKey);

        /// <summary>A modern-only step that first logs the companion windows' state after the previous step.</summary>
        private static void CStep(List<Step> steps, string name, Action act, int delayMs = 600) =>
            steps.Add(new Step { Name = name, Act = () => { CompDump(); act(); }, DelayMs = delayMs, ModernOnly = true });

        private static void CompDump()
        {
            ModernRoot root = Root;
            if (root == null) return;
            string open = string.Empty;
            foreach (string k in new[] { HeroWindow.WindowKey, CreaturesWindow.WindowKey, MountWindow.WindowKey, FishingWindow.WindowKey })
                if (root.Wm.IsOpen(k)) open += k + " ";
            _log.Add($"   comp open=[{open.Trim()}] popup={root.Popups.IsOpen} heroTab={HeroWin?.TabShown} creatureSel={CreatureWin?.Selected} mountSlot={MountWin?.PickedSlot} picker={FishWin?.PickerSlot} fishPhase={FishWin?.Scene?.Current}");
        }

        private static void CompInfos()
        {
            if (_infos.ContainsKey("UiCompMount")) return;
            AddInfo(new ItemInfo { Index = 9101, Name = "UiCompMount", Type = ItemType.Mount, Shape = 8, Image = 300, Durability = 1000, Grade = ItemGrade.Rare, Stats = new Stats() });
            var reins = new Stats(); reins[Stat.MaxDC] = 3;
            AddInfo(new ItemInfo { Index = 9102, Name = "UiCompReins", Type = ItemType.Reins, Image = 310, Durability = 1000, Stats = reins });
            var bells = new Stats(); bells[Stat.MaxAC] = 2; bells[Stat.HP] = 30;
            AddInfo(new ItemInfo { Index = 9103, Name = "UiCompBells", Type = ItemType.Bells, Image = 311, Durability = 1000, Grade = ItemGrade.Common, Stats = bells });
            AddInfo(new ItemInfo { Index = 9104, Name = "UiCompHay", Type = ItemType.Food, Shape = 1, Image = 312, StackSize = 10, Durability = 200, Stats = new Stats() });
            AddInfo(new ItemInfo { Index = 9105, Name = "UiCompRod", Type = ItemType.Weapon, Shape = 49, Image = 45, Durability = 10000, Stats = new Stats() });
            var hook = new Stats(); hook[Stat.CriticalRate] = 4;
            AddInfo(new ItemInfo { Index = 9106, Name = "UiCompHook", Type = ItemType.Hook, Image = 320, Durability = 5000, Stats = hook });
            AddInfo(new ItemInfo { Index = 9107, Name = "UiCompBait", Type = ItemType.Bait, Image = 321, StackSize = 50, Stats = new Stats() });
            var reel = new Stats(); reel[Stat.MaxMAC] = 5;
            AddInfo(new ItemInfo { Index = 9108, Name = "UiCompReel", Type = ItemType.Reel, Image = 322, Durability = 5000, Stats = reel });
            var fl = new Stats(); fl[Stat.MinAC] = 2; fl[Stat.MaxAC] = 6;
            AddInfo(new ItemInfo { Index = 9109, Name = "UiCompFloat", Type = ItemType.Float, Image = 323, Durability = 5000, Stats = fl });
            var fl2 = new Stats(); fl2[Stat.MinAC] = 4; fl2[Stat.MaxAC] = 9;
            AddInfo(new ItemInfo { Index = 9110, Name = "UiCompFloat2", Type = ItemType.Float, Image = 324, Durability = 5000, Grade = ItemGrade.Rare, Stats = fl2 });
            AddInfo(new ItemInfo { Index = 9111, Name = "UiCompFish", Type = ItemType.Nothing, Image = 330, StackSize = 1, Stats = new Stats() });
        }

        private static ClientIntelligentCreature Creature(IntelligentCreatureType type, string name, int fullness) => new ClientIntelligentCreature
        {
            PetType = type, Icon = 0, CustomName = name, Fullness = fullness, SlotIndex = (int)type, Expire = type == IntelligentCreatureType.Kitten ? CMain.Now.AddDays(3) : DateTime.MinValue,
            BlackstoneTime = 4200, MaintainFoodTime = 0, petMode = IntelligentCreaturePickupMode.SemiAutomatic,
            CreatureRules = new IntelligentCreatureRules { MinimalFullness = 1000, MousePickupEnabled = true, MousePickupRange = 11, AutoPickupEnabled = true, AutoPickupRange = 7, SemiAutoPickupEnabled = true, SemiAutoPickupRange = 7, CanProduceBlackStone = true },
            Filter = new IntelligentCreatureItemFilter(),
        };

        /// <summary>The hero roster's i-th stored-hero card (the clickable PaintBoxes in the roster).</summary>
        private static MirControl CompRosterCard(int i)
        {
            ScrollView v = HeroWin?.Roster;
            if (v == null) return null;
            int n = 0;
            foreach (MirControl c in v.Content.Controls)
                if (c is PaintBox p && p.Index >= 0 && !string.IsNullOrEmpty(p.Hint) && p.Size.Height > ModernRoot.S(45) && n++ == i) return c;
            return null;
        }

        /// <summary>The i-th clickable row of a scroll view (PaintBoxes that take the mouse).</summary>
        private static MirControl CompSideRow(ScrollView v, int i)
        {
            if (v == null) return null;
            int n = 0;
            foreach (MirControl c in v.Content.Controls)
                if (c is PaintBox p && p.Size.Height > ModernRoot.S(40) && n++ == i) return c;
            return null;
        }

        private static ModernItemCell CompCellIn(MirControl root, MirGridType grid, int slot)
        {
            if (root == null) return null;
            for (int i = 0; i < root.Controls.Count; i++)
            {
                if (root.Controls[i] is ModernItemCell c && c.Grid == grid && c.Slot == slot) return c;
                ModernItemCell deep = CompCellIn(root.Controls[i], grid, slot);
                if (deep != null) return deep;
            }
            return null;
        }
    }
}

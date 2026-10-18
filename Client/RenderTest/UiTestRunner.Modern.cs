using Client.MirControls;
using Client.MirGraphics;
using Client.MirScenes;
using Client.MirScenes.Modern;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;

namespace Client.RenderTest
{
    /// <summary>
    /// The offline UI test's modern-kit steps (Desktop-Modern-UI-Plan phase 1b): open the kit test window, drag it by the title,
    /// switch theme and HUD scale, survive a device reset, close it with Esc; then the widgets (buttons, confirm-twice, hold,
    /// segmented, chips, toggles, slider, ring, search, tabs, the list), every popup kind, the HUD scale at 1.5, item cells
    /// (hover tooltip, pick, drop) and the send pacer. Run with tools\uitest.ps1 -UiStyle Modern. Kept in its own file so
    /// UiTestRunner.cs still compiles against builds without the kit (the partial method compiles away there).
    /// </summary>
    internal static partial class UiTestRunner
    {
        static partial void AddModernSteps(List<Step> steps)
        {
            steps.Add(new Step { Name = "kit_open", Act = () => PressBind(KeybindOptions.Options), ModernOnly = true });
            steps.Add(new Step { Name = "kit_drag", Act = () => DragWindow(KitTestWindow.WindowKey, 120, 40), ModernOnly = true });
            steps.Add(new Step { Name = "kit_theme_red", Act = () => ModernTheme.Apply("Lost Ark red", null), ModernOnly = true });
            steps.Add(new Step { Name = "kit_scale_150", Act = () => SetScale(1.5f, false), ModernOnly = true });
            steps.Add(new Step { Name = "kit_device_reset", Act = ResetLikeFullScreenToggle, DelayMs = 1000, ModernOnly = true });
            steps.Add(new Step { Name = "kit_scale_auto", Act = () => { ModernTheme.Apply(ModernTheme.DefaultTheme, null); SetScale(1f, true); }, ModernOnly = true });
            steps.Add(new Step { Name = "kit_escape", Act = () => MirScene.ActiveScene.OnKeyDown(new KeyEventArgs(Keys.Escape)), ModernOnly = true });

            AddWidgetSteps(steps);
        }

        private static void Modern(List<Step> steps, string name, Action act, int delayMs = 600) =>
            steps.Add(new Step { Name = name, Act = act, DelayMs = delayMs, ModernOnly = true });

        /// <summary>Phase 1b widget, popup, item cell and pacer steps (after the window steps above).</summary>
        private static void AddWidgetSteps(List<Step> steps)
        {
            Modern(steps, "w_open", () => { SetScale(1f, false); PressBind(KeybindOptions.Options); });
            Modern(steps, "w_hover_main", () => MouseAt(Centre(Kit.MainButton)));
            Modern(steps, "w_click_main", () => ClickAt(Centre(Kit.MainButton)));
            Modern(steps, "w_disabled_hover_click", () => ClickAt(Centre(Kit.DisabledButton)));
            Modern(steps, "w_confirm_arm", () => ClickAt(Centre(Kit.ConfirmButton)), 700);
            Modern(steps, "w_confirm_confirm", () => ClickAt(Centre(Kit.ConfirmButton)), 700);
            Modern(steps, "w_hold_press", () => Press(Centre(Kit.HoldControl)), 600);
            Modern(steps, "w_hold_done", () => { }, 900);
            Modern(steps, "w_hold_release", () => Release(Centre(Kit.HoldControl)));
            Modern(steps, "w_segment_mine", () => ClickAt(Offset(Kit.SegmentedControl, 0.5f, 0.5f)));
            Modern(steps, "w_chip_armour", () => ClickAt(ChipCentre(Kit.ChipsControl, 1)));
            Modern(steps, "w_toggle", () => ClickAt(Centre(Kit.Toggle)));
            Modern(steps, "w_locked_toggle", () => ClickAt(Centre(Kit.LockedToggle)));
            Modern(steps, "w_slider_drag", () =>
            {
                Press(Offset(Kit.SliderControl, 0.2f, 0.5f));
                MouseDrag(Offset(Kit.SliderControl, 0.6f, 0.5f));
                Release(Offset(Kit.SliderControl, 0.6f, 0.5f));
            });
            Modern(steps, "w_slider_wheel", () => { Wheel(Centre(Kit.SliderControl), 120); Wheel(Centre(Kit.SliderControl), 120); });
            Modern(steps, "w_ring_plus", () => { ClickAt(Centre(FindButton(Kit, "+10%"))); ClickAt(Centre(FindButton(Kit, "Badge +1"))); });
            Modern(steps, "w_search_type", () =>
            {
                ClickAt(Centre(Kit.Search));
                Kit.Search.Text = "swo";
                MirScene.ActiveScene.OnKeyPress(new KeyPressEventArgs('r'));
            }, 500);
            Modern(steps, "w_search_clear", () => ClickAt(Offset(Kit.Search, 1f, 0.5f, -ModernRoot.S(10))));
            Modern(steps, "w_tab_list", () => ClickTab(1));
            Modern(steps, "w_list_wheel", () => { MouseAt(Offset(Kit.List, 0.3f, 0.3f)); Wheel(Offset(Kit.List, 0.3f, 0.3f), -360); });
            Modern(steps, "w_list_select", () => ClickAt(Offset(Kit.List, 0.3f, 0.2f)));
            Modern(steps, "w_list_thumb_drag", () =>
            {
                Rectangle t = Kit.List.ThumbBounds;
                Point p = new Point(Kit.List.DisplayLocation.X + t.X + t.Width / 2, Kit.List.DisplayLocation.Y + t.Y + t.Height / 2);
                Press(p);
                MouseDrag(new Point(p.X, p.Y + ModernRoot.S(120)));
                Release(new Point(p.X, p.Y + ModernRoot.S(120)));
            });
            Modern(steps, "w_list_filter", () => Kit.ListSearch.SetQuery("ring"));
            Modern(steps, "w_list_filter_none", () => Kit.ListSearch.SetQuery("zzz"));
            Modern(steps, "w_tab_popups", () => { Kit.ListSearch.SetQuery(""); ClickTab(2); });

            Modern(steps, "p_confirm_open", () => ClickAt(Centre(Kit.NamedButtons["Confirm"])));
            Modern(steps, "p_confirm_tick_esc", () =>
            {
                ModernCheckBox chk = Find<ModernCheckBox>(Root.Popups.Card);
                if (chk != null) ClickAt(Centre(chk));
                Key(Keys.Escape);
            });
            Modern(steps, "p_danger_open", () => ClickAt(Centre(Kit.NamedButtons["Danger"])));
            Modern(steps, "p_danger_hold", () => Press(Centre(Find<HoldButton>(Root.Popups.Card))), 1400);
            Modern(steps, "p_danger_release", () => Release(CMain.MPoint));
            Modern(steps, "p_ask_open", () => ClickAt(Centre(Kit.NamedButtons["Ask"])));
            Modern(steps, "p_ask_enter", () => Key(Keys.Enter));
            Modern(steps, "p_input_open", () => ClickAt(Centre(Kit.NamedButtons["Input"])));
            Modern(steps, "p_input_type", () => Find<ModernTextField>(Root.Popups.Card).Text = "Newname");
            Modern(steps, "p_input_ok", () => ClickAt(Centre(Root.Popups.Card.ButtonRow[^1])));
            Modern(steps, "p_amount_open", () => ClickAt(Centre(Kit.NamedButtons["Amount"])));
            Modern(steps, "p_amount_plus", () =>
            {
                Press(Centre(FindButton(Root.Popups.Card, "+")));
                Release(Centre(FindButton(Root.Popups.Card, "+")));
                Key(Keys.Up);
            });
            Modern(steps, "p_amount_max_ok", () =>
            {
                ClickAt(Centre(FindButton(Root.Popups.Card, GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernMax))));
                ClickAt(Centre(Root.Popups.Card.ButtonRow[^1]));
            });
            Modern(steps, "p_gold_open", () => ClickAt(Centre(Kit.NamedButtons["Gold"])));
            Modern(steps, "p_gold_type_esc", () =>
            {
                Find<ModernTextField>(Root.Popups.Card).Text = "1.5k";
                Key(Keys.Escape);
            });
            Modern(steps, "p_queue", () =>
            {
                Root.Popups.Ask("First box", yes => _log.Add("   ask1 " + yes));
                Root.Popups.Ask("Second box (queued)", yes => _log.Add("   ask2 " + yes));
            });
            Modern(steps, "p_queue_answer", () => { Key(Keys.Y); Key(Keys.N); });
            Modern(steps, "p_request_open", () =>
            {
                ClickAt(Centre(Kit.NamedButtons["Request"]));
                Root.Popups.Request("Another", "wants to trade with you. A longer line that wraps onto a second row.", 3, () => _log.Add("   req2 accepted"), () => _log.Add("   req2 declined"));
            });
            Modern(steps, "p_request_decline", () => ClickAt(Centre(FindButton(FirstToast(), GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernDecline)))), 3500);

            Modern(steps, "c_hover_cell8", () => MouseAt(Centre(Kit.Cell8)));
            Modern(steps, "c_hover_cell9_shift", () =>
            {
                // a worn weapon to compare with, and added stats on both (green lines)
                UserItem worn = Item("UiTestSword");
                worn.AddedStats ??= new Stats();
                worn.AddedStats[Stat.MaxDC] = 3;
                worn.AddedStats[Stat.Accuracy] = 1;
                GameScene.User.Equipment[(int)EquipmentSlot.Weapon] = worn;
                UserItem sword = GameScene.User.Inventory[9];
                if (sword != null)
                {
                    sword.AddedStats ??= new Stats();
                    sword.AddedStats[Stat.MinDC] = 1;
                    sword.AddedStats[Stat.MaxDC] = 7;
                    sword.AddedStats[Stat.Luck] = 1;
                }
                CMain.Shift = true;
                MouseAt(Centre(Kit.Cell9));
            });
            Modern(steps, "c_shift_release", () => CMain.Shift = false);
            Modern(steps, "c_pick_cell8", () => { CMain.Shift = false; ClickAt(Centre(Kit.Cell8)); });
            Modern(steps, "c_drop_cell9", () => ClickAt(Centre(Kit.Cell9)));
            Modern(steps, "c_move_reply", () => Feed(new ServerPackets.MoveItem { Grid = MirGridType.Inventory, From = 8, To = 9, Success = true }));

            Modern(steps, "s_scale_150", () => ClickAt(Centre(Kit.NamedButtons["1.5x"])), 800);
            Modern(steps, "s_input_150", () =>
            {
                Root.Popups.Input("Scaled input", "", 20, s => _log.Add("   input150 " + (s ?? "(cancelled)")), "Typing at HUD scale 1.5.");
            });
            Modern(steps, "s_type_150", () =>
            {
                Find<ModernTextField>(Root.Popups.Card).Text = "Scaled text";
                MirScene.ActiveScene.OnKeyPress(new KeyPressEventArgs('!'));
            });
            Modern(steps, "s_esc_150", () => Key(Keys.Escape));
            Modern(steps, "s_controls_150", () => ClickTab(0));
            Modern(steps, "s_list_150", () => ClickTab(1));
            Modern(steps, "s_theme_emerald", () => { ClickTab(2); ClickAt(ChipCentre(FindChips(Kit), 5)); }, 800);

            Modern(steps, "pacer", PacerTest, 1200);
            Modern(steps, "pacer_drain", PacerDrain);
            Modern(steps, "w_close", () =>
            {
                ModernTheme.Apply(ModernTheme.DefaultTheme, null);
                SetScale(1f, true);
                Key(Keys.Escape);
            });
        }

        private static ModernRoot Root => ModernRoot.Instance;

        private static KitTestWindow Kit => Root?.Wm.Get(KitTestWindow.WindowKey) as KitTestWindow;

        /// <summary>A device reset as CMain.ToggleFullScreen does it (reset, then the map floor and the scene texture rebuild).</summary>
        private static void ResetLikeFullScreenToggle()
        {
            DXManager.ResetDevice();
            if (MirScene.ActiveScene == GameScene.Scene)
            {
                GameScene.Scene.MapControl.FloorValid = false;
                GameScene.Scene.TextureValid = false;
            }
        }

        private static void SetScale(float scale, bool auto)
        {
            Settings.UiScale = scale;
            Settings.UiAutoScale = auto;
            Root?.ScreenChanged();
        }

        /// <summary>Presses on the window's title bar, moves by (dx, dy) device px and releases, through the scene's routing.</summary>
        private static void DragWindow(string key, int dx, int dy)
        {
            ModernWindow w = Root?.Wm.Get(key);
            if (w == null) { _log.Add("   (no window " + key + ")"); return; }
            Point start = new Point(w.DisplayLocation.X + w.Size.Width / 3, w.DisplayLocation.Y + ModernRoot.S(10));
            MouseEventArgs e = new MouseEventArgs(MouseButtons.Left, 1, start.X, start.Y, 0);
            CMain.MPoint = start;
            MirScene.ActiveScene.OnMouseMove(e);
            MirScene.ActiveScene.OnMouseDown(e);
            CMain.MPoint = new Point(start.X + dx, start.Y + dy);
            MirScene.ActiveScene.OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, CMain.MPoint.X, CMain.MPoint.Y, 0));
            MirScene.ActiveScene.OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, CMain.MPoint.X, CMain.MPoint.Y, 0));
            CMain.MPoint = new Point(-1000, -1000);
        }

        // ---- input through the scene's routing (as CMain's handlers do: down, click, up) ----

        private static Point Centre(MirControl c) => c == null ? new Point(-1000, -1000) : new Point(c.DisplayLocation.X + c.Size.Width / 2, c.DisplayLocation.Y + c.Size.Height / 2);

        private static Point Offset(MirControl c, float fx, float fy, int dx = 0) =>
            c == null ? new Point(-1000, -1000) : new Point(c.DisplayLocation.X + (int)(c.Size.Width * fx) + dx, c.DisplayLocation.Y + (int)(c.Size.Height * fy));

        private static void MouseAt(Point p)
        {
            CMain.MPoint = p;
            MirScene.ActiveScene.OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, p.X, p.Y, 0));
        }

        private static void MouseDrag(Point p)
        {
            CMain.MPoint = p;
            MirScene.ActiveScene.OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, p.X, p.Y, 0));
        }

        private static void Press(Point p)
        {
            MouseAt(p);
            MirScene.ActiveScene.OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0));
        }

        private static void Release(Point p)
        {
            CMain.MPoint = p;
            MirScene.ActiveScene.OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0));
        }

        private static void ClickAt(Point p, MouseButtons button = MouseButtons.Left)
        {
            MouseAt(p);
            var e = new MouseEventArgs(button, 1, p.X, p.Y, 0);
            MirScene.ActiveScene.OnMouseDown(e);
            MirScene.ActiveScene.OnMouseClick(e);
            MirScene.ActiveScene.OnMouseUp(e);
        }

        private static void Wheel(Point p, int delta)
        {
            MouseAt(p);
            MirScene.ActiveScene.OnMouseWheel(new MouseEventArgs(MouseButtons.None, 0, p.X, p.Y, delta));
        }

        private static void Key(Keys key) => MirScene.ActiveScene.OnKeyDown(new KeyEventArgs(key));

        private static void ClickTab(int i)
        {
            Tabs t = Kit?.TabsControl;
            if (t == null) return;
            Rectangle r = t.TabBounds(i);
            ClickAt(new Point(t.DisplayLocation.X + r.X + r.Width / 2, t.DisplayLocation.Y + r.Y + r.Height / 2));
        }

        /// <summary>The centre of a chip (Chips lays its chips out left to right, wrapping): found by probing its row.</summary>
        private static Point ChipCentre<T>(Chips<T> chips, int index)
        {
            if (chips == null) return new Point(-1000, -1000);
            // chips are 20 HUD px high with a 4 px gap; probe each row left to right for the index-th chip start
            int found = -1, h = ModernRoot.S(20), gap = ModernRoot.S(4);
            for (int y = h / 2; y < chips.Size.Height; y += h + gap)
            {
                bool inChip = false;
                for (int x = 0; x < chips.Size.Width; x++)
                {
                    bool hit = chips.HitTest(new Point(x, y)) >= 0;
                    if (hit && !inChip)
                    {
                        found++;
                        if (found == index) return new Point(chips.DisplayLocation.X + x + ModernRoot.S(10), chips.DisplayLocation.Y + y);
                    }
                    inChip = hit;
                }
            }
            return new Point(-1000, -1000);
        }

        private static T Find<T>(MirControl root) where T : MirControl
        {
            if (root == null) return null;
            for (int i = 0; i < root.Controls.Count; i++)
            {
                if (root.Controls[i] is T t) return t;
                T deep = Find<T>(root.Controls[i]);
                if (deep != null) return deep;
            }
            return null;
        }

        private static Chips<string> FindChips(MirControl root)
        {
            // the theme chips: the Chips<string> on the visible third page
            Chips<string> last = null;
            void Walk(MirControl c)
            {
                for (int i = 0; i < c.Controls.Count; i++)
                {
                    if (c.Controls[i] is Chips<string> ch && ch.Visible) last = ch;
                    Walk(c.Controls[i]);
                }
            }
            if (root != null) Walk(root);
            return last;
        }

        private static ModernButton FindButton(MirControl root, string text)
        {
            if (root == null) return null;
            for (int i = 0; i < root.Controls.Count; i++)
            {
                if (root.Controls[i] is ModernButton b && b.Text == text && b.Visible) return b;
                ModernButton deep = FindButton(root.Controls[i], text);
                if (deep != null) return deep;
            }
            return null;
        }

        private static MirControl FirstToast()
        {
            Popups pop = Root?.Popups;
            if (pop == null) return null;
            for (int i = 0; i < pop.Controls.Count; i++)
                if (pop.Controls[i].GetType().Name == "RequestToast") return pop.Controls[i];
            return null;
        }

        // ---- send pacer ----

        private static SendPacer _pacer;
        private static int _pacerSent;

        private static void PacerTest()
        {
            _pacerSent = 0;
            _pacer = new SendPacer(5, 3, p => _pacerSent++);
            for (int i = 0; i < 8; i++) _pacer.Send(new ClientPackets.Chat { Message = "pacer " + i });
            _log.Add($"   pacer burst: sent={_pacerSent} queued={_pacer.Queued}");
        }

        private static void PacerDrain()
        {
            _pacer?.Process();
            _log.Add($"   pacer after ~1.2 s: sent={_pacerSent} queued={_pacer?.Queued}");
        }

        /// <summary>Extra state lines in modern runs.</summary>
        static partial void DumpModern(List<string> log)
        {
            ModernRoot root = Root;
            if (root == null) { log.Add("   modern root: none"); return; }
            var open = new List<string>();
            foreach (MirControl c in root.Windows.Controls)
                if (c is ModernWindow w && w.IsOpen) open.Add($"{w.Key}@{w.Location.X},{w.Location.Y} {w.Size.Width}x{w.Size.Height}");
            log.Add($"   modern k={ModernRoot.K:0.###} theme={ModernTheme.Name} front={root.Wm.Front?.Key ?? "none"} open=[{string.Join("; ", open)}] saved=\"{Settings.UiWindows}\"");

            KitTestWindow kit = root.Wm.IsOpen(KitTestWindow.WindowKey) ? Kit : null;
            if (kit != null)
            {
                log.Add($"   kit status=\"{kit.Status}\" tab={kit.TabsControl?.Active} confirmArmed={kit.ConfirmButton?.Armed} segment={kit.SegmentedControl?.Value} chips=[{string.Join(",", kit.ChipsControl?.Selected ?? (IReadOnlyCollection<string>)Array.Empty<string>())}] toggle={kit.Toggle?.On} locked={kit.LockedToggle?.On} slider={kit.SliderControl?.Value} ring={kit.Ring?.Step}/64");
                log.Add($"   kit search=\"{kit.LastSearch}\" list items={kit.List?.Items.Count} scroll={kit.List?.ScrollY}/{kit.List?.MaxScroll} selected={kit.List?.SelectedIndex} popup=\"{kit.LastPopup}\"");
            }
            Popups pop = root.Popups;
            ModernTooltip tip = ModernTooltip.Shared;
            log.Add($"   popups open={pop.IsOpen} queued={pop.Queued} requests={pop.Requests} card={(pop.Card != null ? pop.Card.Kind.ToString() : "none")} tooltip={(tip != null && tip.IsShown ? tip.ShownItem?.Info?.Name + (tip.ShowsWorn ? "+worn" : "") : "none")} ticks={ModernTick.Count}");
        }
    }
}

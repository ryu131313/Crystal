// Ported from WebClient/src/ui/modern/windows/help.ts (HelpWindow): the new UI's help window, a modern take on the classic
// HelpDialog (three shortcut tables, then Help.Lib pictures 0-41 under 22 titles). A home page (a tip, popular topics, a
// new-player checklist ticked by reading topics), a sectioned topic list with read marks and page counts, search across
// topics, shortcuts and chat commands, and topic pages with the Help.Lib pictures in a framed viewer (arrows, page dots,
// click to enlarge), a summary, an "Open ..." button (the real window by key; hidden while that window is not registered;
// "Trading" sends C.TradeRequest) and related topics. Shortcuts and every key named in a summary are the live bindings.
// The page arrows go through WindowManager.HandleKey (only while this is the front window); Esc closes the lightbox first.
// Desktop changes: read marks are kept for the session (no per-viewer storage on the desktop); "Copy" uses the clipboard.
using Client.MirControls;
using Client.MirGraphics;
using Client.MirNetwork;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class HelpWindow : ModernWindow
    {
        public const string WindowKey = "help";
        private const int WidthHud = 940, NavW = 220;
        /// <summary>Content height: 630, or 570 on a short screen (help.ts narrow layout: 600 with the title).</summary>
        private int HeightHud = 630;
        private bool Compact => HeightHud < 600;

        private sealed class Topic
        {
            public string Id;
            public ClientTextKeys Title, Summary;
            public int Pages = 1, First = -1, Icon;
            public string Open;
            public ClientTextKeys OpenLabel;
            public string Special;
            public string[] Related;
            public KeybindOptions[] Keys = Array.Empty<KeybindOptions>();

            public string SummaryText()
            {
                if (Keys.Length == 0) return W.T(Summary);
                var args = new object[Keys.Length];
                for (int i = 0; i < Keys.Length; i++) args[i] = KeyText(Keys[i]);
                return W.T(Summary, args);
            }
        }

        // HelpDialog's titles and Help.Lib picture counts in order (the pictures are numbered on from 0).
        private static readonly (string Id, int Count)[] Order =
        {
            ("movements", 1), ("attacking", 1), ("collecting", 1), ("health", 1), ("skills", 2), ("mana", 1), ("chatting", 1), ("groups", 1), ("durability", 1),
            ("purchasing", 1), ("selling", 1), ("repairing", 1), ("trading", 1), ("inspecting", 1), ("statistics", 6), ("quests", 4), ("mounts", 2), ("fishing", 1),
            ("gems", 1), ("heroes", 5), ("guildbuffs", 3), ("awakening", 5),
        };

        private static Topic T(string id, ClientTextKeys title, ClientTextKeys summary, int icon, string[] related, string open = null, ClientTextKeys openLabel = default, string special = null, params KeybindOptions[] keys)
        {
            var t = new Topic { Id = id, Title = title, Summary = summary, Icon = icon, Related = related, Open = open, OpenLabel = openLabel, Special = special, Keys = keys };
            if (special == null)
            {
                int n = 0;
                foreach (var (oid, count) in Order)
                {
                    if (oid == id) { t.First = n; t.Pages = count; break; }
                    n += count;
                }
            }
            return t;
        }

        private static readonly (ClientTextKeys Name, Topic[] Topics)[] Sections =
        {
            (ClientTextKeys.ModernWinHelpSecStart, new[]
            {
                T("shortcuts", ClientTextKeys.ModernWinHelpShortcuts, ClientTextKeys.ModernWinHelpShortcutsSum, ModernIcons.Key, new[] { "chat", "movements" }, KeybindsWindow.WindowKey, ClientTextKeys.ModernWinKeyBindings, "shortcuts"),
                T("chat", ClientTextKeys.ModernWinHelpChat, ClientTextKeys.ModernWinHelpChatSum, ModernIcons.Chat, new[] { "chatting", "groups" }, null, default, "chat"),
                T("movements", ClientTextKeys.ModernWinHelpMovements, ClientTextKeys.ModernWinHelpMovementsSum, ModernIcons.Walk, new[] { "attacking", "mounts" }),
                T("attacking", ClientTextKeys.ModernWinHelpAttacking, ClientTextKeys.ModernWinHelpAttackingSum, ModernIcons.Sword, new[] { "movements", "health", "skills" }),
                T("collecting", ClientTextKeys.ModernWinHelpCollecting, ClientTextKeys.ModernWinHelpCollectingSum, ModernIcons.Bag, new[] { "purchasing", "selling" }, InventoryWindow.WindowKey, ClientTextKeys.ModernWinInventory, null, KeybindOptions.Pickup),
                T("health", ClientTextKeys.ModernWinHelpHealth, ClientTextKeys.ModernWinHelpHealthSum, ModernIcons.Heart, new[] { "mana", "attacking" }, null, default, null, KeybindOptions.Belt1, KeybindOptions.Belt8),
            }),
            (ClientTextKeys.ModernWinHelpSecSkills, new[]
            {
                T("skills", ClientTextKeys.ModernWinHelpSkills, ClientTextKeys.ModernWinHelpSkillsSum, ModernIcons.Star, new[] { "mana", "statistics" }, SkillsWindow.WindowKey, ClientTextKeys.ModernWinSkills, null, KeybindOptions.Bar1Skill1, KeybindOptions.Bar1Skill8),
                T("mana", ClientTextKeys.ModernWinHelpMana, ClientTextKeys.ModernWinHelpManaSum, ModernIcons.Drop, new[] { "skills", "health" }),
                T("statistics", ClientTextKeys.ModernWinHelpStatistics, ClientTextKeys.ModernWinHelpStatisticsSum, ModernIcons.Chart, new[] { "skills", "durability" }, CharacterWindow.WindowKey, ClientTextKeys.ModernWinCharacter),
            }),
            (ClientTextKeys.ModernWinHelpSecTogether, new[]
            {
                T("chatting", ClientTextKeys.ModernWinHelpChatting, ClientTextKeys.ModernWinHelpChattingSum, ModernIcons.Chat, new[] { "chat", "groups" }),
                T("groups", ClientTextKeys.ModernWinHelpGroups, ClientTextKeys.ModernWinHelpGroupsSum, ModernIcons.Group, new[] { "chatting", "trading" }, "group", ClientTextKeys.ModernWinHelpGroupWindow),
                T("trading", ClientTextKeys.ModernWinHelpTrading, ClientTextKeys.ModernWinHelpTradingSum, ModernIcons.Swap, new[] { "groups", "inspecting" }, "trade", ClientTextKeys.ModernWinHelpTradeRequest, null, KeybindOptions.Trade),
                T("inspecting", ClientTextKeys.ModernWinHelpInspecting, ClientTextKeys.ModernWinHelpInspectingSum, ModernIcons.Eye, new[] { "trading", "statistics" }),
            }),
            (ClientTextKeys.ModernWinHelpSecMerchants, new[]
            {
                T("purchasing", ClientTextKeys.ModernWinHelpPurchasing, ClientTextKeys.ModernWinHelpPurchasingSum, ModernIcons.Coin, new[] { "selling", "repairing" }),
                T("selling", ClientTextKeys.ModernWinHelpSelling, ClientTextKeys.ModernWinHelpSellingSum, ModernIcons.Tag, new[] { "purchasing", "collecting" }),
                T("repairing", ClientTextKeys.ModernWinHelpRepairing, ClientTextKeys.ModernWinHelpRepairingSum, ModernIcons.Wrench, new[] { "durability", "purchasing" }),
                T("durability", ClientTextKeys.ModernWinHelpDurability, ClientTextKeys.ModernWinHelpDurabilitySum, ModernIcons.Shield, new[] { "repairing", "statistics" }),
                T("gems", ClientTextKeys.ModernWinHelpGems, ClientTextKeys.ModernWinHelpGemsSum, ModernIcons.Gem, new[] { "awakening", "statistics" }),
                T("awakening", ClientTextKeys.ModernWinHelpAwakening, ClientTextKeys.ModernWinHelpAwakeningSum, ModernIcons.Spark, new[] { "gems", "statistics" }),
            }),
            (ClientTextKeys.ModernWinHelpSecAdventures, new[]
            {
                T("quests", ClientTextKeys.ModernWinHelpQuests, ClientTextKeys.ModernWinHelpQuestsSum, ModernIcons.Scroll, new[] { "groups", "heroes" }, "quests", ClientTextKeys.ModernWinHelpQuestsWindow),
                T("mounts", ClientTextKeys.ModernWinHelpMounts, ClientTextKeys.ModernWinHelpMountsSum, ModernIcons.Horse, new[] { "movements", "fishing" }, "mount", ClientTextKeys.ModernWinHelpMountWindow, null, KeybindOptions.Mount),
                T("fishing", ClientTextKeys.ModernWinHelpFishing, ClientTextKeys.ModernWinHelpFishingSum, ModernIcons.Fish, new[] { "mounts", "quests" }, "fishing", ClientTextKeys.ModernWinHelpFishingWindow),
                T("heroes", ClientTextKeys.ModernWinHelpHeroes, ClientTextKeys.ModernWinHelpHeroesSum, ModernIcons.Hero, new[] { "groups", "skills" }, "hero", ClientTextKeys.ModernWinHelpHeroWindow),
                T("guildbuffs", ClientTextKeys.ModernWinHelpGuildBuffs, ClientTextKeys.ModernWinHelpGuildBuffsSum, ModernIcons.Flag, new[] { "groups", "heroes" }, "guild", ClientTextKeys.ModernWinHelpGuildWindow),
            }),
        };

        private static readonly (string Id, ClientTextKeys Label)[] Checklist =
        {
            ("movements", ClientTextKeys.ModernWinHelpCheckMove), ("attacking", ClientTextKeys.ModernWinHelpCheckFight), ("collecting", ClientTextKeys.ModernWinHelpCheckLoot),
            ("health", ClientTextKeys.ModernWinHelpCheckPotion), ("skills", ClientTextKeys.ModernWinHelpCheckSkill), ("groups", ClientTextKeys.ModernWinHelpCheckGroup),
            ("purchasing", ClientTextKeys.ModernWinHelpCheckBuy), ("quests", ClientTextKeys.ModernWinHelpCheckQuest),
        };

        private static readonly (ClientTextKeys Text, KeybindOptions[] Keys)[] Tips =
        {
            (ClientTextKeys.ModernWinHelpTip1, Array.Empty<KeybindOptions>()), (ClientTextKeys.ModernWinHelpTip2, Array.Empty<KeybindOptions>()),
            (ClientTextKeys.ModernWinHelpTip3, Array.Empty<KeybindOptions>()), (ClientTextKeys.ModernWinHelpTip4, new[] { KeybindOptions.Belt1, KeybindOptions.Belt8 }),
            (ClientTextKeys.ModernWinHelpTip5, Array.Empty<KeybindOptions>()), (ClientTextKeys.ModernWinHelpTip6, Array.Empty<KeybindOptions>()),
            (ClientTextKeys.ModernWinHelpTip7, new[] { KeybindOptions.Bar2Skill1, KeybindOptions.Bar2Skill8 }), (ClientTextKeys.ModernWinHelpTip8, Array.Empty<KeybindOptions>()),
        };

        /// <summary>Shortcut rows: label + one bind, or a range (first to last).</summary>
        private static readonly (ClientTextKeys Group, (ClientTextKeys Label, KeybindOptions[] Keys)[] Rows)[] Shortcuts =
        {
            (ClientTextKeys.ModernWinHelpScWindows, new[]
            {
                (ClientTextKeys.ModernWinInventory, new[] { KeybindOptions.Inventory }), (ClientTextKeys.ModernWinCharacter, new[] { KeybindOptions.Equipment }),
                (ClientTextKeys.ModernWinSkills, new[] { KeybindOptions.Skills }), (ClientTextKeys.ModernWinHelpScQuests, new[] { KeybindOptions.Quests }),
                (ClientTextKeys.ModernWinHelpScGroup, new[] { KeybindOptions.Group }), (ClientTextKeys.ModernWinHelpScFriends, new[] { KeybindOptions.Friends }),
                (ClientTextKeys.ModernWinHelpScGuild, new[] { KeybindOptions.Guilds }), (ClientTextKeys.ModernWinHelpScBigMap, new[] { KeybindOptions.Bigmap }),
                (ClientTextKeys.ModernWinHelpScMinimap, new[] { KeybindOptions.Minimap }), (ClientTextKeys.ModernWinHelpScGameShop, new[] { KeybindOptions.GameShop }),
                (ClientTextKeys.ModernWinOptions, new[] { KeybindOptions.Options }), (ClientTextKeys.ModernWinHelp, new[] { KeybindOptions.Help }),
                (ClientTextKeys.ModernWinHelpScMount, new[] { KeybindOptions.MountWindow }), (ClientTextKeys.ModernWinHelpScFishing, new[] { KeybindOptions.Fishing }),
                (ClientTextKeys.ModernWinHelpScCreatures, new[] { KeybindOptions.Creature }), (ClientTextKeys.ModernWinHelpScRanking, new[] { KeybindOptions.Ranking }),
                (ClientTextKeys.ModernWinKeyBindings, new[] { KeybindOptions.Keybind }), (ClientTextKeys.ModernWinHelpScCloseAll, new[] { KeybindOptions.Closeall }),
            }),
            (ClientTextKeys.ModernWinHelpScCombat, new[]
            {
                (ClientTextKeys.ModernWinHelpScSkillBar, new[] { KeybindOptions.Bar1Skill1, KeybindOptions.Bar1Skill8 }),
                (ClientTextKeys.ModernWinHelpScSkillBar2, new[] { KeybindOptions.Bar2Skill1, KeybindOptions.Bar2Skill8 }),
                (ClientTextKeys.ModernWinHelpScHeroSkillBar, new[] { KeybindOptions.HeroSkill1, KeybindOptions.HeroSkill8 }),
                (ClientTextKeys.ModernWinHelpScAttackMode, new[] { KeybindOptions.ChangeAttackmode }), (ClientTextKeys.ModernWinHelpScPetMode, new[] { KeybindOptions.ChangePetmode }),
                (ClientTextKeys.ModernWinHelpScLockOn, new[] { KeybindOptions.TargetSpellLockOn }),
            }),
            (ClientTextKeys.ModernWinHelpScItems, new[]
            {
                (ClientTextKeys.ModernWinHelpScBelt, new[] { KeybindOptions.Belt1, KeybindOptions.Belt8 }), (ClientTextKeys.ModernWinHelpScHeroBelt, new[] { KeybindOptions.HeroBelt1, KeybindOptions.HeroBelt2 }),
                (ClientTextKeys.ModernWinHelpScPickUp, new[] { KeybindOptions.Pickup }), (ClientTextKeys.ModernWinHelpScRide, new[] { KeybindOptions.Mount }),
                (ClientTextKeys.ModernWinHelpScAutoRun, new[] { KeybindOptions.Autorun }), (ClientTextKeys.ModernWinHelpScTrade, new[] { KeybindOptions.Trade }),
                (ClientTextKeys.ModernWinHelpScCreaturePickup, new[] { KeybindOptions.CreaturePickup }), (ClientTextKeys.ModernWinHelpScHideUi, new[] { KeybindOptions.Cameramode }),
                (ClientTextKeys.ModernWinHelpScScreenshot, new[] { KeybindOptions.Screenshot }),
            }),
            (ClientTextKeys.ModernWinHelpScSession, new[] { (ClientTextKeys.ModernWinHelpScLogOut, new[] { KeybindOptions.Logout }), (ClientTextKeys.ModernWinHelpScExit, new[] { KeybindOptions.Exit }) }),
        };

        private static readonly (string Cmd, ClientTextKeys Info)[] Prefixes =
        {
            ("/name text", ClientTextKeys.ModernWinHelpPfxWhisper), ("!!text", ClientTextKeys.ModernWinHelpPfxGroup), ("!~text", ClientTextKeys.ModernWinHelpPfxGuild),
            ("!#text", ClientTextKeys.ModernWinHelpPfxMentor), ("!text", ClientTextKeys.ModernWinHelpPfxShout), (":)text", ClientTextKeys.ModernWinHelpPfxLover),
            ("@command", ClientTextKeys.ModernWinHelpPfxCommand),
        };

        private static readonly (string Cmd, ClientTextKeys Info)[] Commands =
        {
            ("@allowtrade", ClientTextKeys.ModernWinHelpCmdAllowTrade), ("@allowguild", ClientTextKeys.ModernWinHelpCmdAllowGuild), ("@allowobserve", ClientTextKeys.ModernWinHelpCmdAllowObserve),
            ("@enablegrouprecall", ClientTextKeys.ModernWinHelpCmdEnableRecall), ("@grouprecall", ClientTextKeys.ModernWinHelpCmdGroupRecall), ("@recalllover", ClientTextKeys.ModernWinHelpCmdRecallLover),
            ("@ride", ClientTextKeys.ModernWinHelpCmdRide), ("@toggletransform", ClientTextKeys.ModernWinHelpCmdTransform), ("@time", ClientTextKeys.ModernWinHelpCmdTime),
            ("@roll", ClientTextKeys.ModernWinHelpCmdRoll), ("@map", ClientTextKeys.ModernWinHelpCmdMap), ("@leaveguild", ClientTextKeys.ModernWinHelpCmdLeaveGuild),
        };

        /// <summary>Read topics for the session (the web keeps them per browser).</summary>
        private static readonly HashSet<string> Read = new HashSet<string>();

        private string _topic;
        private int _pageNo;
        private int _tip = Environment.TickCount % 8;
        private string _query = string.Empty;
        private SearchBox _search;
        private ProgressRing _ring;
        private ModernLabel _progText;
        private ScrollView _nav, _page;
        private ModernPanel _lightbox;
        private int _lightboxImage = -1;
        private bool _dirty = true;

        public HelpWindow() : base(WindowKey, W.T(ClientTextKeys.ModernWinHelp), 70, 12)
        {
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
        }

        internal string TopicShown => _topic;
        internal int PageNo => _pageNo;
        internal bool LightboxOpen => _lightbox != null && _lightbox.Visible;

        private static string KeyText(KeybindOptions o)
        {
            string s = W.CapLabel(W.Key(o));
            return s.Length > 0 ? s : W.T(ClientTextKeys.ModernWinUnboundParen);
        }

        private static IEnumerable<Topic> AllTopics()
        {
            foreach (var s in Sections) foreach (Topic t in s.Topics) yield return t;
        }

        private static Topic ById(string id)
        {
            foreach (Topic t in AllTopics()) if (t.Id == id) return t;
            return null;
        }

        private static int TopicCount
        {
            get { int n = 0; foreach (var s in Sections) n += s.Topics.Length; return n; }
        }

        /// <summary>Opens the window on a topic id (null = home): help.ts showTopic.</summary>
        public void Show(string topic)
        {
            Go(topic);
            Open();
        }

        protected override void Build()
        {
            const int top = TitleHeight;
            HeightHud = Settings.ScreenHeight / ModernRoot.K < 820 ? 570 : 630;
            ContentHeight = HeightHud;
            _search = W.At(new SearchBox(WidthHud - 28 - 150, W.T(ClientTextKeys.ModernWinSearchHelp)), this, 14, top + 13);
            if (_query.Length > 0) _search.Text = _query;
            _search.Search += q => { _query = q; RenderPage(); };
            _ring = W.At(new ProgressRing(34, 4), this, WidthHud - 14 - 140, top + 8);
            _progText = W.Label(this, string.Empty, WidthHud - 14 - 100, top + 9, 100, 16, 13, true, Color.White);
            W.Label(this, W.T(ClientTextKeys.ModernWinTopicsRead), WidthHud - 14 - 100, top + 25, 100, 14, 11, false, ModernTheme.Dim);
            W.Divider(this, 0, top + 50, WidthHud);
            _nav = new ScrollView(NavW, HeightHud - 51) { Parent = this, Location = new Point(0, S(top + 51)), Background = W.Page };
            _nav.Framed = false;
            W.VDivider(this, NavW, top + 51, HeightHud - 51);
            _page = new ScrollView(WidthHud - NavW - 1, HeightHud - 51) { Parent = this, Location = new Point(S(NavW + 1), S(top + 51)), Background = W.Page };
            _page.Framed = false;
            _page.WheelStep = S(60);
            _lightbox = new ModernPanel { Parent = this, Location = new Point(0, S(top)), Size = new Size(S(WidthHud), S(HeightHud)), Visible = false };
            var lb = new PaintBox((g, b) => { using var br = new SolidBrush(Color.FromArgb(235, 0, 0, 0)); g.FillRectangle(br, 0, 0, b.Size.Width, b.Size.Height); })
            { Parent = _lightbox, Size = _lightbox.Size, Hint = W.T(ClientTextKeys.ModernWinClickOrEscClose) };
            lb.AfterPaint = b => DrawPicture(_lightboxImage, new Rectangle(b.DisplayLocation.X + b.Size.Width * 2 / 100, b.DisplayLocation.Y + b.Size.Height * 4 / 100, b.Size.Width * 96 / 100, b.Size.Height * 92 / 100), true);
            lb.Click += (o, e) => CloseLightbox();
            _dirty = true;
            Render();
        }

        protected override void Closed() => CloseLightbox();

        public override void Process()
        {
            base.Process();
            if (_dirty) Render();
        }

        /// <summary>Front window keys: Esc closes the lightbox / clears the search; the arrows turn the pages.</summary>
        public override bool HandleKey(KeyEventArgs e)
        {
            if (e.Control || e.Alt) return false;
            if (e.KeyCode == Keys.Escape)
            {
                if (LightboxOpen) { CloseLightbox(); return true; }
                if (_search != null && _search.Focused && _search.Text.Length > 0) { _search.SetQuery(string.Empty); return true; }
                return false;
            }
            if ((_search != null && _search.Focused) || _query.Length > 0 || _topic == null || e.Shift) return false;
            if (e.KeyCode != Keys.Left && e.KeyCode != Keys.Right) return false;
            Topic t = ById(_topic);
            if (t == null || t.Pages < 2) return false;
            Turn(e.KeyCode == Keys.Left ? -1 : 1);
            return true;
        }

        private void Go(string id, int page = 0)
        {
            _topic = id != null && ById(id) != null ? id : null;
            _pageNo = page;
            if (_topic != null) Read.Add(_topic);
            _query = string.Empty;
            if (_search != null && !_search.IsDisposed) _search.Text = string.Empty;
            _dirty = true;
            if (_page != null && !_page.IsDisposed) _page.ScrollTo(0);
        }

        private void Turn(int d)
        {
            Topic t = _topic != null ? ById(_topic) : null;
            if (t == null || t.Pages < 2) return;
            _pageNo = (_pageNo + d + t.Pages) % t.Pages;
            RenderPage();
        }

        private void Render()
        {
            _dirty = false;
            if (_nav == null || _nav.IsDisposed) return;
            int n = 0;
            foreach (Topic t in AllTopics()) if (Read.Contains(t.Id)) n++;
            _ring.Set(n / (float)TopicCount);
            _progText.Text = n + " / " + TopicCount;
            RenderNav();
            RenderPage();
        }

        // ------------------------------------------------------------------ nav

        private void RenderNav()
        {
            int keep = _nav.ScrollY;
            for (int i = _nav.Content.Controls.Count - 1; i >= 0; i--) _nav.Content.Controls[i].Dispose();
            int y = 8;
            y = NavItem(null, W.T(ClientTextKeys.ModernWinHome), ModernIcons.Home, string.Empty, true, y);
            foreach (var s in Sections)
            {
                W.Caption(_nav.Content, W.T(s.Name), 16, y + 8, NavW - 24);
                y += 28;
                foreach (Topic t in s.Topics)
                    y = NavItem(t.Id, W.T(t.Title), t.Icon, t.Pages > 1 ? W.T(ClientTextKeys.ModernWinPages, t.Pages) : string.Empty, Read.Contains(t.Id), y);
            }
            _nav.ContentHeight = S(y + 8);
            _nav.ScrollTo(keep);
        }

        private int NavItem(string id, string title, int icon, string pages, bool read, int y)
        {
            bool on = _topic == id;
            var it = new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                W.Row(g, b.Size, false, b.Hovered, on);
                ModernSkin.Text(g, title, ModernSkin.Font(12), on ? Color.White : ModernTheme.Text, new RectangleF(32 * k, 0, b.Size.Width - 90 * k, b.Size.Height));
                if (pages.Length > 0) ModernSkin.Text(g, pages, ModernSkin.Font(9), ModernTheme.Dim, new RectangleF(0, 0, b.Size.Width - 22 * k, b.Size.Height), StringAlignment.Far);
                if (!read)
                    using (var br = new SolidBrush(ModernTheme.Accent))
                        g.FillEllipse(br, b.Size.Width - 14 * k, b.Size.Height / 2f - 3 * k, 6 * k, 6 * k);
            })
            { Parent = _nav.Content, Location = new Point(S(8), S(y)), Size = new Size(_nav.InnerWidth - S(8), S(28)), Hint = id == null ? null : W.T(read ? ClientTextKeys.ModernWinRead : ClientTextKeys.ModernWinNotReadYet) };
            it.AfterPaint = b => W.DrawIcon(icon, new Rectangle(b.DisplayLocation.X + S(9), b.DisplayLocation.Y + S(6), S(16), S(16)), on ? ModernTheme.Accent : ModernTheme.Dim);
            it.Click += (o, e) => Go(id);
            return y + 30;
        }

        // ------------------------------------------------------------------ pages

        private int PageW => (int)Math.Round(_page.InnerWidth / ModernRoot.K) - 36;

        private void RenderPage()
        {
            if (_page == null || _page.IsDisposed) return;
            CloseLightbox();
            int keep = _topic != null && _query.Length == 0 ? _page.ScrollY : 0;
            for (int i = _page.Content.Controls.Count - 1; i >= 0; i--) _page.Content.Controls[i].Dispose();
            int y;
            string q = _query.Trim().ToLowerInvariant();
            if (q.Length > 0) y = SearchPage(q);
            else if (_topic == null) y = HomePage();
            else y = TopicPage(ById(_topic));
            _page.ContentHeight = S(y + 18);
            _page.ScrollTo(keep);
        }

        private ModernLabel L(string text, int x, int y, int w, int h, float size = 12, bool bold = false, Color? colour = null, StringAlignment align = StringAlignment.Near, bool wrap = false) =>
            W.Label(_page.Content, text, x, y, w, h, size, bold, colour, align, wrap);

        private int Wrapped(string text, int x, int y, int w, float size, Color? colour = null, bool bold = false)
        {
            SizeF s = ModernSkin.MeasureWrapped(text, ModernSkin.Font(size, bold), S(w));
            int h = (int)Math.Ceiling(s.Height / ModernRoot.K) + 4;
            L(text, x, y, w, h, size, bold, colour, StringAlignment.Near, true);
            return y + h;
        }

        private int Heading(int icon, string title, int y)
        {
            new IconView(icon, 22, ModernTheme.Accent) { Parent = _page.Content, Location = new Point(S(18), S(y + 3)) };
            L(title, 48, y, PageW - 30, 28, 20, true, Color.White);
            return y + 32;
        }

        private int Crumb(string text, int y, bool link)
        {
            if (!link)
            {
                L(text.ToUpperInvariant(), 18, y, PageW, 14, 10, false, ModernTheme.Dim);
                return y + 16;
            }
            string home = W.T(ClientTextKeys.ModernWinHelp).ToUpperInvariant();
            var a = new PaintBox((g, b) => ModernSkin.Text(g, home, ModernSkin.Font(10), b.Hovered ? Color.White : ModernTheme.Accent, new RectangleF(0, 0, b.Size.Width, b.Size.Height)))
            { Parent = _page.Content, Location = new Point(S(18), S(y)), Size = new Size((int)Math.Ceiling(ModernSkin.Measure(home, ModernSkin.Font(10))) + 2, S(14)) };
            a.Click += (o, e) => Go(null);
            L("›  " + text.ToUpperInvariant(), 18 + (int)Math.Ceiling(a.Size.Width / ModernRoot.K) + 6, y, PageW - 80, 14, 10, false, ModernTheme.Dim);
            return y + 16;
        }

        private int HomePage()
        {
            int w = PageW, y = 14;
            y = Crumb(W.T(ClientTextKeys.ModernWinHelp), y, false);
            y = Heading(ModernIcons.Book, W.T(ClientTextKeys.ModernWinHelpWelcome), y);
            y = Wrapped(W.T(ClientTextKeys.ModernWinHelpWelcomeSum), 18, y, w, 13) + 8;

            // the tip card
            var (tipKey, tipKeys) = Tips[_tip % Tips.Length];
            string tip = TipText(tipKey, tipKeys);
            new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1, b.Size.Height - 1);
                using var p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
                using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(r, W.Over(ModernTheme.Accent, 0.22f), ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, 0.7f), 20f)) g.FillPath(br, p);
                ModernSkin.Stroke(g, p, W.Over(ModernTheme.Accent, 0.5f));
                using (var br = new SolidBrush(ModernTheme.Mix(ModernTheme.Accent, Color.Black, 0.4f))) g.FillEllipse(br, 16 * k, b.Size.Height / 2f - 20 * k, 40 * k, 40 * k);
            }) { Parent = _page.Content, Location = new Point(S(18), S(y)), Size = new Size(S(w), S(68)), NotControl = true };
            new IconView(ModernIcons.Info, 20, Color.White) { Parent = _page.Content, Location = new Point(S(18 + 26), S(y + 24)) };
            L(W.T(ClientTextKeys.ModernWinTip).ToUpperInvariant(), 18 + 70, y + 12, w - 200, 14, 10, true, ModernTheme.Accent);
            L(tip, 18 + 70, y + 28, w - 200, 34, 13, false, Color.White, StringAlignment.Near, true);
            var next = new ModernButton(W.T(ClientTextKeys.ModernWinNextTip)) { Parent = _page.Content };
            next.AutoWidth(80);
            next.Location = new Point(S(18 + w - 14) - next.Size.Width, S(y + 23));
            next.Click += (o, e) => { _tip = (_tip + 1) % Tips.Length; RenderPage(); };
            y += 68;

            // popular topics
            y = Caption2(W.T(ClientTextKeys.ModernWinPopularTopics), y);
            string[] popular = { "shortcuts", "attacking", "skills", "quests", "heroes", "chat" };
            int cw = (w - 16) / 3;
            for (int i = 0; i < popular.Length; i++)
            {
                Topic t = ById(popular[i]);
                int cx = 18 + i % 3 * (cw + 8), cy = y + i / 3 * 72;
                string title = W.T(t.Title), sum = t.SummaryText();
                var card = new PaintBox((g, b) =>
                {
                    float k = ModernRoot.K;
                    W.Card(g, b.Size, b.Hovered ? ModernTheme.Accent : null);
                    ModernSkin.Text(g, title, ModernSkin.Font(12, true), Color.White, new RectangleF(44 * k, 8 * k, b.Size.Width - 52 * k, 16 * k));
                    ModernSkin.Text(g, sum, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(44 * k, 25 * k, b.Size.Width - 52 * k, b.Size.Height - 30 * k), StringAlignment.Near, StringAlignment.Near, true, true);
                }) { Parent = _page.Content, Location = new Point(S(cx), S(cy)), Size = new Size(S(cw), S(64)) };
                int icon = t.Icon;
                card.AfterPaint = b => W.DrawIcon(icon, new Rectangle(b.DisplayLocation.X + S(10), b.DisplayLocation.Y + S(10), S(24), S(24)), ModernTheme.Accent);
                string id = t.Id;
                card.Click += (o, e) => Go(id);
            }
            y += 2 * 72;

            // checklist
            int done = 0;
            foreach (var c in Checklist) if (Read.Contains(c.Id)) done++;
            y = Caption2(W.T(ClientTextKeys.ModernWinChecklist, done, Checklist.Length), y);
            int hw = (w - 6) / 2;
            for (int i = 0; i < Checklist.Length; i++)
            {
                var (id, label) = Checklist[i];
                bool ok = Read.Contains(id);
                string text = W.T(label);
                var ck = new PaintBox((g, b) =>
                {
                    float k = ModernRoot.K;
                    RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1, b.Size.Height - 1);
                    using (var p = ModernSkin.RoundRect(r, ModernTheme.Radius * k)) ModernSkin.Stroke(g, p, b.Hovered ? ModernTheme.Accent : W.Over(ModernTheme.Frame, 0.45f));
                    RectangleF c = new RectangleF(10 * k, b.Size.Height / 2f - 8 * k, 16 * k, 16 * k);
                    if (ok) using (var br = new SolidBrush(ModernTheme.Accent)) g.FillEllipse(br, c);
                    using (var pen = new Pen(ok ? ModernTheme.Accent : ModernTheme.Frame, 1f)) g.DrawEllipse(pen, c);
                    if (ok) ModernSkin.Text(g, "✓", ModernSkin.Font(10, true), Color.White, c, StringAlignment.Center);
                    ModernSkin.Text(g, text, ModernSkin.Font(12), ok ? ModernTheme.Dim : ModernTheme.Text, new RectangleF(34 * k, 0, b.Size.Width - 40 * k, b.Size.Height));
                }) { Parent = _page.Content, Location = new Point(S(18 + i % 2 * (hw + 6)), S(y + i / 2 * 36)), Size = new Size(S(hw), S(30)) };
                ck.Click += (o, e) => Go(id);
            }
            return y + (Checklist.Length + 1) / 2 * 36;
        }

        private static string TipText(ClientTextKeys key, KeybindOptions[] keys)
        {
            if (keys.Length == 0) return W.T(key);
            var args = new object[keys.Length];
            for (int i = 0; i < keys.Length; i++) args[i] = KeyText(keys[i]);
            return W.T(key, args);
        }

        private int Caption2(string text, int y)
        {
            W.Caption(_page.Content, text, 18, y + 16, PageW);
            return y + 36;
        }

        private int TopicPage(Topic t)
        {
            if (t == null) return HomePage();
            int w = PageW, y = 14;
            string sec = string.Empty;
            foreach (var s in Sections) if (Array.IndexOf(s.Topics, t) >= 0) sec = W.T(s.Name);
            y = Crumb(sec, y, true);
            y = Heading(t.Icon, W.T(t.Title), y);
            y = Wrapped(t.SummaryText(), 18, y, w, 13) + 6;
            if (t.Special == "shortcuts") y = ShortcutTable(null, y, false);
            else if (t.Special == "chat") y = ChatTable(null, y, false);
            else y = Viewer(t, y);

            // Open ... + related
            y += 8;
            y = Caption2(W.T(ClientTextKeys.ModernWinRelated), y - 8);
            int x = 18;
            Action open = Opener(t.Open);
            if (open != null)
            {
                var b = new ModernButton(W.T(ClientTextKeys.ModernWinOpenX, W.T(t.OpenLabel)), ButtonKind.Main) { Parent = _page.Content, Location = new Point(S(x), S(y)) };
                b.AutoWidth(100);
                b.Click += (o, e) => open();
                x = W.Right(b) + 8;
            }
            foreach (string id in t.Related)
            {
                Topic r = ById(id);
                if (r == null) continue;
                string title = W.T(r.Title);
                int cw = (int)Math.Ceiling(ModernSkin.Measure(title, ModernSkin.Font(11)) / ModernRoot.K) + 40;
                var chip = new PaintBox((g, b) =>
                {
                    float k = ModernRoot.K;
                    RectangleF rr = new RectangleF(0.5f, 0.5f, b.Size.Width - 1, b.Size.Height - 1);
                    using var p = ModernSkin.RoundRect(rr, rr.Height / 2);
                    using (var br = new SolidBrush(ModernSkin.Opaque(ModernTheme.PanelB))) g.FillPath(br, p);
                    ModernSkin.Stroke(g, p, b.Hovered ? ModernTheme.Accent : ModernTheme.Frame);
                    ModernSkin.Text(g, title, ModernSkin.Font(11), b.Hovered ? Color.White : ModernTheme.Text, new RectangleF(26 * k, 0, b.Size.Width - 30 * k, b.Size.Height));
                }) { Parent = _page.Content, Location = new Point(S(x), S(y)), Size = new Size(S(cw), S(22)) };
                int icon = r.Icon;
                chip.AfterPaint = b => W.DrawIcon(icon, new Rectangle(b.DisplayLocation.X + S(9), b.DisplayLocation.Y + S(4), S(13), S(13)), ModernTheme.Accent);
                chip.Click += (o, e) => Go(id);
                x += cw + 6;
            }
            return y + 26;
        }

        /// <summary>The "Open" action of a topic, or null when that window is not registered.</summary>
        private static Action Opener(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;
            if (target == "trade") return () => Network.Enqueue(new C.TradeRequest());
            WindowManager wm = ModernRoot.Instance?.Wm;
            if (wm == null || !wm.IsRegistered(target)) return null;
            return () => wm.Open(target);
        }

        /// <summary>The Help.Lib picture viewer: arrows, page badge, dots; a click enlarges it.</summary>
        private int Viewer(Topic t, int y)
        {
            int w = PageW, h = Compact ? 340 : 412, index = t.First + _pageNo;
            var view = new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1, b.Size.Height - 1);
                using var p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
                using (var pp = new System.Drawing.Drawing2D.GraphicsPath())
                {
                    pp.AddEllipse(-b.Size.Width * 0.2f, -b.Size.Height * 0.3f, b.Size.Width * 1.4f, b.Size.Height * 1.4f);
                    using var br = new System.Drawing.Drawing2D.PathGradientBrush(pp) { CenterColor = ModernTheme.Mix(ModernTheme.Accent, ModernTheme.Hex("#05070c"), 0.1f), SurroundColors = new[] { ModernTheme.Hex("#020305") } };
                    br.CenterPoint = new PointF(b.Size.Width / 2f, b.Size.Height * 0.4f);
                    g.FillPath(br, p);
                }
                ModernSkin.Stroke(g, p, ModernTheme.Frame);
                if (Libraries.Help == null || Libraries.Help.GetSize(index).IsEmpty)
                    ModernSkin.Text(g, W.T(ClientTextKeys.ModernWinPictureNotFound), ModernSkin.Font(12), ModernTheme.Dim, r, StringAlignment.Center);
            }) { Parent = _page.Content, Location = new Point(S(18), S(y)), Size = new Size(S(w), S(h)) };
            view.AfterPaint = b => DrawPicture(index, new Rectangle(b.DisplayLocation.X + S(10), b.DisplayLocation.Y + S(10), b.Size.Width - S(20), b.Size.Height - S(20)), false);
            view.Click += (o, e) => OpenLightbox(index);
            if (t.Pages > 1)
            {
                for (int d = -1; d <= 1; d += 2)
                {
                    int dir = d;
                    string glyph = d < 0 ? "‹" : "›";
                    var arrow = new PaintBox((g, b) =>
                    {
                        float k = ModernRoot.K;
                        RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1, b.Size.Height - 1);
                        using var p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
                        using (var br = new SolidBrush(Color.FromArgb(b.Hovered ? 230 : 160, 0, 0, 0))) g.FillPath(br, p);
                        ModernSkin.Stroke(g, p, b.Hovered ? ModernTheme.Accent : ModernTheme.Frame);
                        ModernSkin.Text(g, glyph, ModernSkin.Font(22), Color.White, r, StringAlignment.Center);
                    }) { Parent = view, Size = new Size(S(32), S(52)), Hint = W.T(d < 0 ? ClientTextKeys.ModernWinPrevPage : ClientTextKeys.ModernWinNextPage) };
                    arrow.Location = new Point(d < 0 ? S(8) : view.Size.Width - S(40), (view.Size.Height - arrow.Size.Height) / 2);
                    arrow.Click += (o, e) => Turn(dir);
                }
                string badge = (_pageNo + 1) + " / " + t.Pages;
                new PaintBox((g, b) =>
                {
                    using var p = ModernSkin.RoundRect(new RectangleF(0, 0, b.Size.Width - 1, b.Size.Height - 1), b.Size.Height / 2f);
                    using (var br = new SolidBrush(Color.FromArgb(200, 0, 0, 0))) g.FillPath(br, p);
                    ModernSkin.Text(g, badge, ModernSkin.Font(10, true), Color.White, new RectangleF(0, 0, b.Size.Width, b.Size.Height), StringAlignment.Center);
                }) { Parent = view, Location = new Point(view.Size.Width - S(60), S(8)), Size = new Size(S(50), S(18)), NotControl = true };
                // dots
                int dx = 18 + w / 2 - (t.Pages * 14 + 14) / 2;
                for (int i = 0; i < t.Pages; i++)
                {
                    int page = i;
                    bool on = i == _pageNo;
                    var dot = new PaintBox((g, b) =>
                    {
                        using var p = ModernSkin.RoundRect(new RectangleF(0, 0, b.Size.Width - 1, b.Size.Height - 1), 4 * ModernRoot.K);
                        using var br = new SolidBrush(on ? ModernTheme.Accent : b.Hovered ? ModernTheme.FrameLight : ModernTheme.Frame);
                        g.FillPath(br, p);
                    }) { Parent = _page.Content, Location = new Point(S(dx), S(y + h + 8)), Size = new Size(S(on ? 22 : 8), S(8)), Hint = W.T(ClientTextKeys.ModernWinPageN, i + 1) };
                    dot.Click += (o, e) => { _pageNo = page; RenderPage(); };
                    dx += on ? 28 : 14;
                }
                y += 20;
            }
            return y + h + 6;
        }

        /// <summary>Draws a Help.Lib picture fitted in a rectangle (shrunk only, unless <paramref name="grow"/>).</summary>
        private static void DrawPicture(int index, Rectangle box, bool grow)
        {
            if (index < 0 || Libraries.Help == null) return;
            Size s = Libraries.Help.GetSize(index);
            if (s.IsEmpty) return;
            float scale = Math.Min(box.Width / (float)s.Width, box.Height / (float)s.Height);
            if (!grow) scale = Math.Min(1f, scale);
            var size = new Size(Math.Max(1, (int)(s.Width * scale)), Math.Max(1, (int)(s.Height * scale)));
            Libraries.Help.Draw(index, new Point(box.X + (box.Width - size.Width) / 2, box.Y + (box.Height - size.Height) / 2), size, Color.White);
        }

        private void OpenLightbox(int index)
        {
            if (_lightbox == null || _lightbox.IsDisposed) return;
            _lightboxImage = index;
            _lightbox.Visible = true;
            _lightbox.BringToFront();
            for (int i = 0; i < _lightbox.Controls.Count; i++) _lightbox.Controls[i].Redraw();
            Redraw();
        }

        private void CloseLightbox()
        {
            if (_lightbox == null || _lightbox.IsDisposed || !_lightbox.Visible) return;
            _lightbox.Visible = false;
            Redraw();
        }

        /// <summary>The shortcut tables from the current key binds; with a query only the matching rows (the search page).</summary>
        private int ShortcutTable(string q, int y, bool onlyHits)
        {
            int w = PageW, cw = (w - 18) / 2;
            foreach (var (group, rows) in Shortcuts)
            {
                var hits = new List<(string, string)>();
                foreach (var (label, keys) in rows)
                {
                    string a = W.CapLabel(W.Key(keys[0]));
                    string caps = a.Length == 0 ? string.Empty : keys.Length == 2 ? a + "  " + W.T(ClientTextKeys.ModernWinTo) + "  " + LastPart(W.CapLabel(W.Key(keys[1]))) : a;
                    string text = W.T(label);
                    bool hit = q != null && (text.ToLowerInvariant().Contains(q) || a.ToLowerInvariant().Contains(q));
                    if (onlyHits && !hit) continue;
                    hits.Add((text, caps));
                }
                if (hits.Count == 0) continue;
                if (!onlyHits) y = Caption2(W.T(group), y);
                for (int i = 0; i < hits.Count; i++)
                {
                    var (text, caps) = hits[i];
                    bool shade = i / 2 % 2 == 0;
                    new PaintBox((g, b) => PaintShortcut(g, b, text, caps, shade)) { Parent = _page.Content, Location = new Point(S(18 + i % 2 * (cw + 18)), S(y + i / 2 * 30)), Size = new Size(S(cw), S(28)), NotControl = true };
                }
                y += (hits.Count + 1) / 2 * 30;
            }
            return y;
        }

        private static string LastPart(string label)
        {
            int i = label.LastIndexOf(" + ", StringComparison.Ordinal);
            return i >= 0 ? label.Substring(i + 3) : label;
        }

        private static void PaintShortcut(Graphics g, PaintBox b, string text, string caps, bool shade)
        {
            float k = ModernRoot.K;
            if (shade)
                using (var p = ModernSkin.RoundRect(new RectangleF(0, 0, b.Size.Width, b.Size.Height), ModernTheme.Radius * k))
                using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.04f)))
                    g.FillPath(br, p);
            ModernSkin.Text(g, text, ModernSkin.Font(12), ModernTheme.Text, new RectangleF(8 * k, 0, b.Size.Width * 0.5f, b.Size.Height));
            Font cf = ModernSkin.Font(10, true);
            if (caps.Length == 0)
            {
                using var f = new Font(ModernSkin.Font(11).FontFamily, ModernSkin.Font(11).Size, FontStyle.Italic, GraphicsUnit.Pixel);
                ModernSkin.Text(g, W.T(ClientTextKeys.ModernWinUnbound), f, ModernTheme.Dim, new RectangleF(0, 0, b.Size.Width - 8 * k, b.Size.Height), StringAlignment.Far);
                return;
            }
            // keycaps right-aligned ("to" between a range's ends)
            string[] parts = caps.Split(new[] { " + ", "  " }, StringSplitOptions.RemoveEmptyEntries);
            float x = b.Size.Width - 8 * k;
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                bool word = parts[i] == W.T(ClientTextKeys.ModernWinTo);
                float cw = word ? ModernSkin.Measure(parts[i], ModernSkin.Font(10)) + 6 * k : Math.Max(22 * k, ModernSkin.Measure(parts[i], cf) + 12 * k);
                RectangleF c = new RectangleF(x - cw, 4 * k, cw, 20 * k);
                if (word) ModernSkin.Text(g, parts[i], ModernSkin.Font(10), ModernTheme.Dim, c, StringAlignment.Center);
                else
                    using (var p = ModernSkin.RoundRect(c, 4 * k))
                    {
                        ModernSkin.FillVertical(g, p, c, new[] { ModernSkin.Opaque(ModernTheme.PanelC), ModernSkin.Opaque(ModernTheme.PanelB) });
                        ModernSkin.Stroke(g, p, ModernTheme.Frame);
                        bool mod = parts[i] == "Ctrl" || parts[i] == "Alt" || parts[i] == "Shift" || parts[i] == "~";
                        ModernSkin.Text(g, parts[i], cf, mod ? ModernTheme.Accent : Color.White, c, StringAlignment.Center);
                    }
                x -= cw + 3 * k;
            }
        }

        private int ChatTable(string q, int y, bool onlyHits)
        {
            int w = PageW, cw = (w - 18) / 2;
            int Block(ClientTextKeys caption, (string Cmd, ClientTextKeys Info)[] rows, int yy)
            {
                var hits = new List<(string, string)>();
                foreach (var (cmd, info) in rows)
                {
                    string text = W.T(info);
                    if (onlyHits && !(cmd.ToLowerInvariant().Contains(q) || text.ToLowerInvariant().Contains(q))) continue;
                    hits.Add((cmd, text));
                }
                if (hits.Count == 0) return yy;
                if (!onlyHits) yy = Caption2(W.T(caption), yy);
                for (int i = 0; i < hits.Count; i++)
                {
                    var (cmd, text) = hits[i];
                    bool shade = i / 2 % 2 == 0;
                    int x = 18 + i % 2 * (cw + 18), ry = yy + i / 2 * 30;
                    new PaintBox((g, b) =>
                    {
                        float k = ModernRoot.K;
                        if (shade)
                            using (var p = ModernSkin.RoundRect(new RectangleF(0, 0, b.Size.Width, b.Size.Height), ModernTheme.Radius * k))
                            using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.04f)))
                                g.FillPath(br, p);
                        Font cf = new Font("Consolas", ModernSkin.Font(12).Size, FontStyle.Bold, GraphicsUnit.Pixel);
                        try
                        {
                            float tw = ModernSkin.Measure(cmd, cf);
                            ModernSkin.Text(g, cmd, cf, ModernTheme.Accent, new RectangleF(8 * k, 0, tw + 4, b.Size.Height));
                            ModernSkin.Text(g, text, ModernSkin.Font(11), ModernTheme.Text, new RectangleF(16 * k + tw, 0, b.Size.Width - tw - 80 * k, b.Size.Height));
                        }
                        finally { cf.Dispose(); }
                    }) { Parent = _page.Content, Location = new Point(S(x), S(ry)), Size = new Size(S(cw), S(28)), NotControl = true };
                    string copy = cmd.Split(' ')[0];
                    if (copy.EndsWith("text", StringComparison.Ordinal)) copy = copy.Substring(0, copy.Length - 4);
                    var cp = new PaintBox((g, b) =>
                    {
                        RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1, b.Size.Height - 1);
                        using var p = ModernSkin.RoundRect(r, r.Height / 2);
                        bool ok = b.Index > 0;
                        ModernSkin.Stroke(g, p, ok ? ModernTheme.Ok : b.Hovered ? ModernTheme.Accent : ModernTheme.Frame);
                        ModernSkin.Text(g, W.T(ok ? ClientTextKeys.ModernWinCopied : ClientTextKeys.ModernWinCopy), ModernSkin.Font(10), ok ? ModernTheme.Ok : b.Hovered ? Color.White : ModernTheme.Dim, r, StringAlignment.Center);
                    }) { Parent = _page.Content, Location = new Point(S(x + cw - 62), S(ry + 5)), Size = new Size(S(56), S(18)), Hint = W.T(ClientTextKeys.ModernWinCopyHint) };
                    cp.Click += (o, e) =>
                    {
                        try { Clipboard.SetText(copy); } catch (Exception ex) { ModernLog.Once("[ModernUi] help copy", ex); }
                        cp.Index = 1;
                        cp.Invalidate();
                    };
                }
                return yy + (hits.Count + 1) / 2 * 30;
            }
            y = Block(ClientTextKeys.ModernWinHelpChatPrefixes, Prefixes, y);
            y = Block(ClientTextKeys.ModernWinHelpPlayerCommands, Commands, y);
            return y;
        }

        private int SearchPage(string q)
        {
            int w = PageW, y = 14;
            y = Crumb(W.T(ClientTextKeys.ModernWinSearch), y, false);
            L(W.T(ClientTextKeys.ModernWinResultsFor, _query.Trim()), 18, y, w, 28, 20, true, Color.White);
            y += 32;
            y = Caption2(W.T(ClientTextKeys.ModernWinTopics), y);
            int found = 0;
            foreach (Topic t in AllTopics())
            {
                string title = W.T(t.Title), sum = t.SummaryText();
                if (!(title + " " + sum).ToLowerInvariant().Contains(q)) continue;
                found++;
                var r = new PaintBox((g, b) =>
                {
                    float k = ModernRoot.K;
                    RectangleF rr = new RectangleF(0.5f, 0.5f, b.Size.Width - 1, b.Size.Height - 1);
                    using (var p = ModernSkin.RoundRect(rr, ModernTheme.Radius * k)) ModernSkin.Stroke(g, p, b.Hovered ? ModernTheme.Accent : W.Over(ModernTheme.Frame, 0.5f));
                    ModernSkin.Text(g, title, ModernSkin.Font(12, true), Color.White, new RectangleF(40 * k, 6 * k, b.Size.Width - 50 * k, 16 * k));
                    ModernSkin.Text(g, sum, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(40 * k, 22 * k, b.Size.Width - 50 * k, 16 * k));
                }) { Parent = _page.Content, Location = new Point(S(18), S(y)), Size = new Size(S(w), S(44)) };
                int icon = t.Icon;
                r.AfterPaint = b => W.DrawIcon(icon, new Rectangle(b.DisplayLocation.X + S(10), b.DisplayLocation.Y + S(12), S(20), S(20)), ModernTheme.Accent);
                string id = t.Id;
                r.Click += (o, e) => Go(id);
                y += 50;
            }
            if (found == 0) { L(W.T(ClientTextKeys.ModernWinNoTopicsMatch), 18, y + 10, w, 20, 12, false, ModernTheme.Dim, StringAlignment.Center); y += 40; }
            int before = y;
            int after = ShortcutTable(q, y + 36, true);
            if (after > y + 36) { W.Caption(_page.Content, W.T(ClientTextKeys.ModernWinHelpShortcuts), 18, y + 16, w); y = after; }
            int after2 = ChatTable(q, y + 36, true);
            if (after2 > y + 36) { W.Caption(_page.Content, W.T(ClientTextKeys.ModernWinHelpChat), 18, y + 16, w); y = after2; }
            return Math.Max(before, y);
        }
    }
}

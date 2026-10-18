// Ported from WebClient/src/ui/modern/windows/skills.ts (SkillsWindow): the new UI's skills window over User.Magics (and
// Hero.Magics on the Hero tab while the user has a hero). A filterable list (search, All / On a key / No key) with level pips,
// the training bar, the key badge and a quantised cooldown sweep; the detail (big icon, type badge, the classic hint text,
// MP cost / cooldown / range / skill level / next level / recharge, training) and the key assignment: bar 1 (keys 1-8), bar 2
// (keys 9-16), "No key" — the classic AssignKeyPanel rule (C.MagicKey, any other spell on that key loses it). The hero tab
// assigns keys 17-24 and has a per-spell auto-cast switch (C.HeroSpellToggle; locked until the server answers, at most 3 s).
// Desktop changes: the desktop keeps no S.HeroAutoCast state (GameScene has no handler for it), so the switch shows the last
// state asked for in this session (the server default is on) — an integrator follow-up; the key labels are the live
// bindings (CMain.InputKeys) like the web's.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class SkillsWindow : ModernWindow
    {
        public const string WindowKey = "skills";
        private const int WidthHud = 640, HeightHud = 446, LeftW = 330, HeroKeyOffset = 16, AutoCastWaitMs = 3000;

        private static readonly HashSet<Spell> Passive = new HashSet<Spell> { Spell.Fencing, Spell.FatalSword, Spell.MPEater, Spell.Hemorrhage, Spell.SpiritSword, Spell.Slaying, Spell.Focus, Spell.Meditation };
        /// <summary>Hero spells with auto-cast switched off (GameScene.Models.HeroAutoCast, from S.HeroAutoCast).</summary>
        private static HashSet<Spell> HeroManual => GameScene.Models?.HeroAutoCast.Disabled ?? EmptySpells;
        private static readonly HashSet<Spell> EmptySpells = new HashSet<Spell>();

        private int _tab;
        private string _query = string.Empty;
        private string _filter = "all";
        private readonly Spell?[] _picked = new Spell?[2];
        private readonly Dictionary<Spell, bool> _pending = new Dictionary<Spell, bool>();
        private long _pendingUntil;

        private Tabs _tabs;
        private bool _builtHero;
        private ScrollView _list;
        private ModernLabel _foot, _footWho;
        private ScrollView _detail;
        private readonly List<(ClientMagic Magic, CooldownVeil Veil)> _rows = new List<(ClientMagic, CooldownVeil)>();
        private CooldownVeil _bigVeil;
        private ModernLabel _recharge;
        private ClientMagic _shown;
        private string _rechargeText = string.Empty;

        private bool _dirty = true;
        private long _sig = long.MinValue, _nextPoll;
        private readonly Action _changed;
        private readonly Action<bool> _hasHero;

        public SkillsWindow() : base(WindowKey, W.T(ClientTextKeys.ModernWinSkills), 380, 60)
        {
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
            _changed = () => _dirty = true;
            _hasHero = _ => _dirty = true;
        }

        private static bool HasHeroTab => GameScene.Hero != null || (GameScene.Scene?.HasHero ?? false);
        private UserObject Actor => _tab == 1 ? GameScene.Hero : GameScene.User;
        private List<ClientMagic> Spells => Actor?.Magics ?? (List<ClientMagic>)null;

        internal int TabShown => _tab;
        internal ClientMagic ShownMagic => _shown;
        internal static bool HeroAutoCast(Spell s) => !HeroManual.Contains(s);

        public static bool IsPassive(Spell s) => Passive.Contains(s);

        // ---- the skill text helpers (skills.ts nextLevel / need / cost / describe) ----

        private static int NextLevel(ClientMagic m) => m.Level == 0 ? m.Level1 : m.Level == 1 ? m.Level2 : m.Level == 2 ? m.Level3 : 0;
        private static int Need(ClientMagic m) => m.Level == 0 ? m.Need1 : m.Level == 1 ? m.Need2 : m.Level == 2 ? m.Need3 : 0;
        private static int Cost(ClientMagic m) => m.BaseCost + m.LevelCost * m.Level;

        /// <summary>The classic MagicButton hint (ClientTextKeys "SpellSkillDescription"), filled in; "" when there is none.</summary>
        private static string RawDescription(ClientMagic m)
        {
            if (!Enum.TryParse(m.Spell + "SkillDescription", out ClientTextKeys key)) return string.Empty;
            try { return W.T(key, m.Level, NextLevel(m), m.BaseCost); }
            catch (FormatException) { return W.T(key); }
        }

        private static List<string> Describe(ClientMagic m)
        {
            var lines = new List<string>();
            foreach (string l in RawDescription(m).Split('\n'))
            {
                string t = l.Trim();
                if (t.Length > 0) lines.Add(t);
            }
            if (lines.Count > 0 && lines[0] == m.Name) lines.RemoveAt(0);
            return lines;
        }

        /// <summary>The type badge: Passive / Toggle / Buff / Active (from the hint's type line).</summary>
        private static string SpellType(ClientMagic m)
        {
            if (Passive.Contains(m.Spell)) return W.T(ClientTextKeys.ModernWinSpellPassive);
            foreach (string l in RawDescription(m).Split('\n'))
            {
                string t = l.Trim();
                if (t.Length >= 24 || !t.Contains("Skill")) continue;
                if (t.Contains("Passive")) return W.T(ClientTextKeys.ModernWinSpellPassive);
                if (t.Contains("Toggle")) return W.T(ClientTextKeys.ModernWinSpellToggle);
                if (t.Contains("Buff")) return W.T(ClientTextKeys.ModernWinSpellBuff);
                break;
            }
            return W.T(ClientTextKeys.ModernWinSpellActive);
        }

        /// <summary>The binding behind a magic key (1-8 bar 1, 9-16 bar 2, 17-24 hero).</summary>
        private static KeybindOptions KeyOption(int key) => key > HeroKeyOffset ? KeybindOptions.HeroSkill1 + (key - 1) % 8 : KeybindOptions.Bar1Skill1 + (key - 1);

        private static string KeyText(int key)
        {
            string f = "F" + ((key - 1) % 8 + 1);
            string fallback = key > HeroKeyOffset ? W.T(ClientTextKeys.ModernWinShiftCombo, f) : key > 8 ? W.T(ClientTextKeys.ModernWinCtrlCombo, f) : f;
            string label = W.Key(KeyOption(key));
            return label.Length > 0 ? W.CapLabel(label) : fallback;
        }

        private static string KeyShort(int key) => KeyText(key).Replace("Ctrl + ", "^").Replace("Shift + ", "⇧").Replace(" + ", "+");

        private static string Cooldown(long ms) => ms <= 0 ? W.T(ClientTextKeys.ModernWinNone) : ms < 60000 ? W.T(ClientTextKeys.ModernWinSeconds, W.N1(ms / 1000d)) : W.Duration(ms / 1000);

        // ------------------------------------------------------------------ layout

        protected override void Build()
        {
            const int top = TitleHeight;
            _builtHero = HasHeroTab;
            if (!_builtHero) _tab = 0;
            var names = new List<string> { W.T(ClientTextKeys.ModernWinSkills) };
            if (_builtHero) names.Add(W.T(ClientTextKeys.ModernWinHeroTab));
            _tabs = new Tabs(names, _tab, WidthHud - 28) { Parent = this, Location = new Point(S(14), S(top + 6)) };
            _tabs.Changed += i => { _tab = i; _dirty = true; };

            int cols = top + 38;
            W.Divider(this, 0, cols, WidthHud);
            W.VDivider(this, LeftW, cols, HeightHud - 38);
            var search = W.At(new SearchBox(LeftW - 22, W.T(ClientTextKeys.ModernWinSearchSkills)), this, 12, cols + 10);
            if (_query.Length > 0) search.Text = _query;
            search.Search += q => { _query = q.ToLowerInvariant(); _dirty = true; };
            var filter = W.At(new Chips<string>(new[]
            {
                new Choice<string>("all", W.T(ClientTextKeys.ModernWinFilterAll)),
                new Choice<string>("keyed", W.T(ClientTextKeys.ModernWinFilterKeyed), null, W.T(ClientTextKeys.ModernWinFilterKeyedHint)),
                new Choice<string>("free", W.T(ClientTextKeys.ModernWinFilterFree), null, W.T(ClientTextKeys.ModernWinFilterFreeHint)),
            }, new[] { _filter }, LeftW - 22, false, false), this, 12, cols + 40);
            filter.Changed += sel => { foreach (string s in sel) { _filter = s; _dirty = true; } };
            int listTop = cols + 66, listH = HeightHud - 38 - 66 - 26;
            _list = new ScrollView(LeftW - 18, listH) { Parent = this, Location = new Point(S(10), S(listTop)), Background = W.Page };
            _list.Framed = false;
            _foot = W.Label(this, string.Empty, 12, listTop + listH + 4, 160, 16, 11, false, ModernTheme.Dim);
            _footWho = W.Label(this, string.Empty, 170, listTop + listH + 4, LeftW - 182, 16, 11, false, ModernTheme.Dim, StringAlignment.Far);
            _detail = new ScrollView(WidthHud - LeftW - 2, HeightHud - 40) { Parent = this, Location = new Point(S(LeftW + 1), S(cols + 1)), Background = W.Page };
            _detail.Framed = false;
            _dirty = true;
            _sig = long.MinValue;
            Render();
        }

        protected override void Shown()
        {
            ModernEvents.SkillBars.On(_changed);
            ModernEvents.UserInformation.On(_changed);
            ModernEvents.HeroInformation.On(_changed);
            GameScene.Models?.HeroAutoCast.Changed.On(_changed);
            ModernEvents.HasHero.On(_hasHero);
            _dirty = true;
        }

        protected override void Closed()
        {
            ModernEvents.SkillBars.Off(_changed);
            ModernEvents.UserInformation.Off(_changed);
            ModernEvents.HeroInformation.Off(_changed);
            GameScene.Models?.HeroAutoCast.Changed.Off(_changed);
            ModernEvents.HasHero.Off(_hasHero);
        }

        public override void Process()
        {
            base.Process();
            if (HasHeroTab != _builtHero) { Layout(); return; }
            if (CMain.Time >= _nextPoll)
            {
                _nextPoll = CMain.Time + 250;
                long sig = Signature();
                if (sig != _sig) { _sig = sig; _dirty = true; }
                if (_pending.Count > 0 && CMain.Time >= _pendingUntil) { _pending.Clear(); _dirty = true; }
            }
            if (_dirty) Render();
            Sweep();
        }

        /// <summary>Levels, keys, experience and the character level (no model event for them): re-render when this moves.</summary>
        private long Signature()
        {
            long h = (GameScene.User?.Level ?? 0) * 31L + (GameScene.Hero?.Level ?? 0) + CMain.InputKeys.Keylist.Count;
            List<ClientMagic> list = Spells;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    ClientMagic m = list[i];
                    h = h * 1000003 + (int)m.Spell * 7 + m.Level * 131 + m.Key * 17 + m.Experience;
                }
            for (int i = 0; i < CMain.InputKeys.Keylist.Count; i++)
            {
                KeyBind k = CMain.InputKeys.Keylist[i];
                if (k.function <= KeybindOptions.Bar2Skill8 || (k.function >= KeybindOptions.HeroSkill1 && k.function <= KeybindOptions.HeroSkill8))
                    h = h * 31 + (int)k.Key + k.RequireCtrl * 3 + k.RequireShift * 5 + k.RequireAlt * 7 + k.RequireTilde * 11;
            }
            return h;
        }

        private List<ClientMagic> Filtered()
        {
            var output = new List<ClientMagic>();
            List<ClientMagic> list = Spells;
            if (list == null) return output;
            foreach (ClientMagic m in list)
            {
                if (_filter == "keyed" && m.Key == 0) continue;
                if (_filter == "free" && m.Key != 0) continue;
                if (_query.Length > 0 && !(m.Name ?? string.Empty).ToLowerInvariant().Contains(_query) && !SpellType(m).ToLowerInvariant().Contains(_query)) continue;
                output.Add(m);
            }
            return output;
        }

        private ClientMagic Current(List<ClientMagic> items)
        {
            Spell? want = _picked[_tab];
            if (want.HasValue)
                foreach (ClientMagic m in items) if (m.Spell == want.Value) return m;
            return items.Count > 0 ? items[0] : null;
        }

        private void Render()
        {
            _dirty = false;
            if (_list == null || _list.IsDisposed) return;
            if (_tab == 1 && !HasHeroTab) _tab = 0;
            _tabs.Select(_tab);
            List<ClientMagic> items = Filtered();
            ClientMagic sel = Current(items);
            RenderList(items, sel);
            RenderDetail(sel);
        }

        // ------------------------------------------------------------------ list

        private void RenderList(List<ClientMagic> items, ClientMagic sel)
        {
            int keep = _list.ScrollY;
            for (int i = _list.Content.Controls.Count - 1; i >= 0; i--) _list.Content.Controls[i].Dispose();
            _rows.Clear();
            List<ClientMagic> all = Spells ?? new List<ClientMagic>();
            bool hero = _tab == 1;
            int w = (int)Math.Round(_list.InnerWidth / ModernRoot.K);
            if (items.Count == 0)
            {
                ClientTextKeys text = hero && GameScene.Hero == null ? ClientTextKeys.ModernWinSummonHeroSkills
                    : all.Count > 0 ? ClientTextKeys.ModernWinNoSkillsMatch : hero ? ClientTextKeys.ModernWinHeroNoSkills : ClientTextKeys.ModernWinNoSkills;
                W.Label(_list.Content, W.T(text), 10, 30, w - 20, 40, 12, false, ModernTheme.Dim, StringAlignment.Center, true);
            }
            int y = 0;
            for (int i = 0; i < items.Count; i++)
            {
                ClientMagic m = items[i];
                bool odd = i % 2 == 0;
                string type = SpellType(m);
                bool manual = hero && !Passive.Contains(m.Spell) && !HeroAutoCast(m.Spell);
                int cost = Cost(m), need = Need(m);
                float train = m.Level >= 3 ? 1f : need > 0 ? Math.Min(1f, m.Experience / (float)need) : 0f;
                string right1 = m.Level >= 3 ? W.T(ClientTextKeys.ModernWinMax) : W.T(ClientTextKeys.ModernWinLv, m.Level);
                string right2 = cost > 0 ? W.T(ClientTextKeys.ModernWinMpAmount, W.N(cost)) : W.T(ClientTextKeys.ModernWinNoMp);
                var row = new PaintBox((g, b) => PaintRow(g, b, m, type, manual, train, right1, right2, odd))
                {
                    Parent = _list.Content,
                    Location = new Point(0, S(y)),
                    Size = new Size(_list.InnerWidth, S(46)),
                    Selected = m == sel,
                    Hint = m.Key != 0 ? m.Name + ": " + KeyText(m.Key) : W.T(ClientTextKeys.ModernWinNoKeyFor, m.Name),
                };
                row.Click += (o, e) => { if (_picked[_tab] != m.Spell) { _picked[_tab] = m.Spell; _dirty = true; } };
                var icon = new SpellIcon(m.Icon, 38, 36) { Parent = row, Location = new Point(S(4), S(5)) };
                var veil = new CooldownVeil { Parent = icon, Size = icon.Size };
                if (m.Key != 0) new KeyBadge(KeyShort(m.Key)) { Parent = icon, Location = new Point(1, 1) };
                _rows.Add((m, veil));
                y += 49;
            }
            _list.ContentHeight = S(y);
            _list.ScrollTo(keep);
            int keyed = 0;
            foreach (ClientMagic m in all) if (m.Key != 0) keyed++;
            _foot.Text = W.T(ClientTextKeys.ModernWinLearnedKeyed, all.Count, keyed);
            UserObject a = Actor;
            _footWho.Text = a == null ? W.T(ClientTextKeys.ModernWinHeroTab) : (hero ? a.Name + " · " : string.Empty) + W.T(ClientTextKeys.ModernWinLevelClass, a.Level, a.Class.ToLocalizedString());
        }

        private static void PaintRow(Graphics g, PaintBox b, ClientMagic m, string type, bool manual, float train, string right1, string right2, bool odd)
        {
            float k = ModernRoot.K;
            W.Row(g, b.Size, odd, b.Hovered, b.Selected);
            float x = 51 * k, rw = 58 * k;
            Font nf = ModernSkin.Font(12, true), bf = ModernSkin.Font(9, true);
            float nameW = Math.Min(ModernSkin.Measure(m.Name, nf) + 2, b.Size.Width - x - rw - 90 * k);
            ModernSkin.Text(g, m.Name, nf, ModernTheme.Text, new RectangleF(x, 6 * k, nameW, 16 * k));
            float bx = x + nameW + 6 * k;
            bx = Badge(g, bx, 7 * k, type, false);
            if (manual) Badge(g, bx, 7 * k, W.T(ClientTextKeys.ModernWinManual), true);
            // pips + training bar
            float py = 28 * k;
            for (int i = 0; i < 3; i++)
                using (var br = new SolidBrush(i < m.Level ? ModernTheme.Accent : W.Over(ModernTheme.Frame, 0.6f)))
                    g.FillRectangle(br, x + i * 11 * k, py, 9 * k, 4 * k);
            float tx = x + 38 * k, tw = b.Size.Width - tx - rw - 8 * k;
            using (var br = new SolidBrush(ModernTheme.Field)) g.FillRectangle(br, tx, py, tw, 4 * k);
            if (train > 0)
                using (var br = new LinearGradientBrush(new RectangleF(tx - 1, py, tw + 2, 4 * k), ModernTheme.Mix(ModernTheme.Accent, Color.Black, 0.45f), ModernTheme.Accent, 0f))
                    g.FillRectangle(br, tx, py, tw * train, 4 * k);
            RectangleF rr = new RectangleF(b.Size.Width - rw - 8 * k, 6 * k, rw, 16 * k);
            ModernSkin.Text(g, right1, ModernSkin.Font(11, true), ModernTheme.Text, rr, StringAlignment.Far);
            ModernSkin.Text(g, right2, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(rr.X, 22 * k, rw, 14 * k), StringAlignment.Far);
        }

        /// <summary>A .badge chip; returns the x after it.</summary>
        private static float Badge(Graphics g, float x, float y, string text, bool off)
        {
            float k = ModernRoot.K;
            Font f = ModernSkin.Font(9, true);
            string t = text.ToUpperInvariant();
            float w = ModernSkin.Measure(t, f) + 10 * k;
            RectangleF r = new RectangleF(x, y, w, 13 * k);
            using (var p = ModernSkin.RoundRect(r, 6 * k))
                ModernSkin.Stroke(g, p, off ? ModernTheme.Frame : W.Over(ModernTheme.Accent, 0.4f));
            ModernSkin.Text(g, t, f, off ? ModernTheme.Dim : ModernTheme.AccentDim, r, StringAlignment.Center);
            return x + w + 5 * k;
        }

        // ------------------------------------------------------------------ detail

        private void RenderDetail(ClientMagic m)
        {
            int keep = m != null && _shown != null && _shown.Spell == m.Spell ? _detail.ScrollY : 0; // the same spell keeps its scroll
            for (int i = _detail.Content.Controls.Count - 1; i >= 0; i--) _detail.Content.Controls[i].Dispose();
            _shown = m;
            _bigVeil = null;
            _recharge = null;
            _rechargeText = string.Empty;
            _lastTenth = -1;
            if (m == null)
            {
                _detail.ContentHeight = 0;
                return;
            }
            bool hero = _tab == 1, passive = Passive.Contains(m.Spell);
            int dw = WidthHud - LeftW - 2 - 28 - 8;
            MirControl d = _detail.Content;

            var big = new SpellIcon(m.Icon, 76, 72, true) { Parent = d, Location = new Point(S(14), S(14)) };
            _bigVeil = new CooldownVeil { Parent = big, Size = big.Size };
            W.Label(d, m.Name, 102, 16, dw - 90, 22, 16, true, Color.White);
            string type = SpellType(m);
            int level = m.Level;
            new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                Badge(g, 0, 0, type, false);
                for (int i = 0; i < 3; i++)
                    using (var br = new SolidBrush(i < level ? ModernTheme.Accent : W.Over(ModernTheme.Frame, 0.6f)))
                        g.FillRectangle(br, i * 16 * k, 22 * k, 14 * k, 5 * k);
            }) { Parent = d, Location = new Point(S(102), S(42)), Size = new Size(S(dw - 90), S(30)), NotControl = true };

            int y = 96;
            List<string> lines = Describe(m);
            string desc = lines.Count > 0 ? string.Join("\n", lines) : W.T(ClientTextKeys.ModernWinNoDescription);
            SizeF ds = ModernSkin.MeasureWrapped(desc, ModernSkin.Font(12), S(dw));
            int dh = Math.Min(120, (int)Math.Ceiling(ds.Height / ModernRoot.K) + 4);
            W.Label(d, desc, 14, y, dw, dh, 12, false, ModernTheme.Mix(ModernTheme.Text, ModernTheme.Dim, 0.85f), StringAlignment.Near, true);
            y += dh + 8;

            UserObject owner = Actor;
            int nl = NextLevel(m), cost = Cost(m);
            string next = m.Level >= 3 ? W.T(ClientTextKeys.ModernWinMastered) : W.T(ClientTextKeys.ModernWinLv, nl);
            bool warn = m.Level < 3 && nl > (owner?.Level ?? 0);
            (string, string, bool)[] facts =
            {
                (W.T(ClientTextKeys.ModernWinMpCost), cost > 0 ? W.N(cost) : W.T(ClientTextKeys.ModernWinNone), false),
                (W.T(ClientTextKeys.ModernWinCooldown), Cooldown(m.Delay), false),
                (W.T(ClientTextKeys.ModernWinRange), m.Range > 0 ? W.T(ClientTextKeys.ModernWinCells, m.Range) : W.T(ClientTextKeys.ModernWinSelfMelee), false),
                (W.T(ClientTextKeys.ModernWinSkillLevel), m.Level + " / 3", false),
                (W.T(ClientTextKeys.ModernWinNextLevel), next, warn),
                (W.T(ClientTextKeys.ModernWinRecharge), W.T(ClientTextKeys.ModernWinReady), false),
            };
            int fw = (dw - 10) / 2;
            for (int i = 0; i < facts.Length; i++)
            {
                var (k1, v1, w1) = facts[i];
                int fx = 14 + i % 2 * (fw + 10), fy = y + i / 2 * 26;
                new PaintBox((g, b) => { using var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.06f)); using var p = ModernSkin.RoundRect(new RectangleF(0, 0, b.Size.Width, b.Size.Height), ModernTheme.Radius * 0.5f * ModernRoot.K); g.FillPath(br, p); })
                { Parent = d, Location = new Point(S(fx), S(fy)), Size = new Size(S(fw), S(22)), NotControl = true };
                int lw = fw * 2 / 5;
                W.Label(d, k1, fx + 7, fy, lw, 22, 11, false, ModernTheme.Dim);
                var val = W.Label(d, v1, fx + lw, fy, fw - lw - 7, 22, 11, true, w1 ? ModernTheme.Warn : Color.Empty, StringAlignment.Far);
                if (i == 4 && m.Level < 3) val.Hint = W.T(hero ? ClientTextKeys.ModernWinHeroNeedsLevel : ClientTextKeys.ModernWinYouNeedLevel, nl, m.Level + 1);
                if (i == 5) _recharge = val;
            }
            y += 3 * 26 + 6;

            if (m.Level < 3)
            {
                int need = Need(m);
                W.Label(d, W.T(ClientTextKeys.ModernWinTraining, W.N(m.Experience), W.N(need)), 14, y, dw, 14, 11, false, ModernTheme.Dim);
                var bar = W.At(new ModernProgressBar(dw, 6), d, 14, y + 16);
                bar.Set(need > 0 ? Math.Min(1f, m.Experience / (float)need) : 0f);
                y += 30;
            }

            if (hero) HeroKeys(d, m, passive, y, dw);
            else UserKeys(d, m, passive, y, dw);
            _detail.FitContent(12);
            _detail.ScrollTo(keep);
        }

        private void UserKeys(MirControl d, ClientMagic m, bool passive, int y, int dw)
        {
            W.Caption(d, W.T(ClientTextKeys.ModernWinSkillBarKeys), 14, y + 4, dw - 90);
            var none = new ModernButton(W.T(ClientTextKeys.ModernWinNoKey)) { Parent = d, Tooltip = W.T(ClientTextKeys.ModernWinTakeOffKey, m.Name) };
            none.AutoWidth(70);
            none.Location = new Point(S(14 + dw) - none.Size.Width, S(y));
            none.SetDisabled(m.Key == 0, W.T(ClientTextKeys.ModernWinHasNoKey));
            none.Click += (o, e) => Assign(GameScene.User, m, 0);
            y += 26;
            if (passive)
            {
                W.Label(d, W.T(ClientTextKeys.ModernWinPassiveKeyNote), 14, y, dw, 28, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
                y += 30;
            }
            KeyRow(d, m, GameScene.User, 1, W.T(ClientTextKeys.ModernWinBar1), y, dw, null);
            KeyRow(d, m, GameScene.User, 9, W.T(ClientTextKeys.ModernWinBar2), y + 44, dw, null);
        }

        private void HeroKeys(MirControl d, ClientMagic m, bool passive, int y, int dw)
        {
            UserHeroObject heroObj = GameScene.Hero;
            string why = heroObj == null ? W.T(ClientTextKeys.ModernWinSummonHeroFirst) : heroObj.Dead ? W.T(ClientTextKeys.ModernWinHeroDead) : null;
            W.Caption(d, W.T(ClientTextKeys.ModernWinHeroSkillKeys), 14, y + 4, dw - 90);
            var none = new ModernButton(W.T(ClientTextKeys.ModernWinNoKey)) { Parent = d, Tooltip = W.T(ClientTextKeys.ModernWinTakeOffKey, m.Name) };
            none.AutoWidth(70);
            none.Location = new Point(S(14 + dw) - none.Size.Width, S(y));
            none.SetDisabled(m.Key == 0 || why != null, why ?? W.T(ClientTextKeys.ModernWinHasNoKey));
            none.Click += (o, e) => Assign(GameScene.Hero, m, 0);
            y += 26;
            KeyRow(d, m, heroObj, HeroKeyOffset + 1, null, y, dw, why);
            y += 30;
            if (passive)
            {
                W.Label(d, W.T(ClientTextKeys.ModernWinPassiveAlwaysOn), 14, y, dw, 16, 11, false, ModernTheme.Dim);
                return;
            }
            bool waiting = _pending.TryGetValue(m.Spell, out bool asked);
            bool on = waiting ? asked : HeroAutoCast(m.Spell);
            W.Caption(d, W.T(ClientTextKeys.ModernWinAutoCast), 14, y, dw);
            W.Label(d, W.T(ClientTextKeys.ModernWinHeroCastsAlone), 14, y + 16, dw - 50, 22, 12);
            var sw = new ToggleSwitch(on, true) { Parent = d };
            sw.Location = new Point(S(14 + dw) - sw.Size.Width, S(y + 19));
            if (waiting) sw.Lock(true, W.T(ClientTextKeys.ModernWinWaitingServer));
            else if (heroObj == null) sw.Lock(true, W.T(ClientTextKeys.ModernWinSummonHeroFirst));
            sw.Changed += v => SetAutoCast(m.Spell, v);
            ClientTextKeys note = !on ? ClientTextKeys.ModernWinManualNote : m.Key == 0 ? ClientTextKeys.ModernWinNeedsKeyNote : ClientTextKeys.ModernWinAutoNote;
            W.Label(d, W.T(note), 14, y + 42, dw, 28, 11, false, !on || m.Key != 0 ? ModernTheme.Dim : ModernTheme.Warn, StringAlignment.Near, true);
        }

        private void KeyRow(MirControl d, ClientMagic m, UserObject actor, int first, string label, int y, int dw, string why)
        {
            if (label != null)
            {
                W.Caption(d, label, 14, y, dw);
                y += 16;
            }
            int bw = (dw - 7 * 4) / 8;
            for (int k = first; k < first + 8; k++)
            {
                int key = k;
                ClientMagic holder = null;
                if (actor != null)
                    foreach (ClientMagic x in actor.Magics) if (x.Key == key && x != m) { holder = x; break; }
                string text = KeyText(key);
                bool on = m.Key == key;
                var b = new ModernButton(KeyShort(key), on ? ButtonKind.Main : ButtonKind.Normal) { Parent = d, Location = new Point(S(14 + (k - first) * (bw + 4)), S(y)), FontSize = 10 };
                b.Size = new Size(S(bw), b.Size.Height);
                b.Tooltip = on ? W.T(ClientTextKeys.ModernWinIsOnKey, m.Name, text) : W.T(ClientTextKeys.ModernWinPutOnKey, m.Name, text) + (holder != null ? " " + W.T(ClientTextKeys.ModernWinReplaces, holder.Name) : string.Empty);
                if (why != null) b.SetDisabled(true, why);
                b.Click += (o, e) => { if (m.Key != key) Assign(actor, m, (byte)key); };
            }
        }

        /// <summary>AssignKeyPanel.SaveButton: any spell on that key loses it, then C.MagicKey (the hero's keys carry the 16 offset).</summary>
        private void Assign(UserObject actor, ClientMagic magic, byte key)
        {
            if (actor == null || magic == null) return;
            if (key != 0)
                for (int i = 0; i < actor.Magics.Count; i++)
                    if (actor.Magics[i].Key == key) actor.Magics[i].Key = 0;
            Network.Enqueue(new C.MagicKey { Spell = magic.Spell, Key = key, OldKey = magic.Key });
            magic.Key = key;
            ModernEvents.SkillBars.Emit();
            _dirty = true;
        }

        private void SetAutoCast(Spell spell, bool on)
        {
            if (GameScene.Hero == null || _pending.ContainsKey(spell)) { _dirty = true; return; }
            _pending[spell] = on;
            _pendingUntil = CMain.Time + AutoCastWaitMs;
            Network.Enqueue(new C.HeroSpellToggle { Spell = spell, AutoCast = on });
            _dirty = true; // shown from _pending until the server's S.HeroAutoCast updates Models.HeroAutoCast
        }

        // ------------------------------------------------------------------ cooldown sweeps (quantised, repaint on a step change)

        private static int SweepStep(ClientMagic m, out long left)
        {
            left = m.Delay > 0 ? m.CastTime + m.Delay - CMain.Time : 0;
            if (left < 100) { left = 0; return 0; }
            return (int)Math.Ceiling(Math.Min(1d, left / (double)m.Delay) * CooldownVeil.Steps);
        }

        private void Sweep()
        {
            for (int i = 0; i < _rows.Count; i++) _rows[i].Veil.SetStep(SweepStep(_rows[i].Magic, out _));
            if (_shown == null || _bigVeil == null) return;
            _bigVeil.SetStep(SweepStep(_shown, out long left));
            if (_recharge == null) return;
            // the remaining time in tenths: the text is rebuilt only when the tenth changes
            long tenth = left > 0 ? (left + 99) / 100 : 0;
            if (tenth == _lastTenth) return;
            _lastTenth = tenth;
            string text = tenth > 0 ? W.T(ClientTextKeys.ModernWinSeconds, W.N1(tenth / 10d)) : W.T(ClientTextKeys.ModernWinReady);
            if (text == _rechargeText) return;
            _rechargeText = text;
            _recharge.Text = text;
        }

        private long _lastTenth = -1;

        /// <summary>A MagIcon2 picture (Icon * 2) in a framed well.</summary>
        private sealed class SpellIcon : ModernControl
        {
            private readonly int _icon;
            private readonly bool _big;

            public SpellIcon(int icon, int wHud, int hHud, bool big = false)
            {
                _icon = icon;
                _big = big;
                Size = new Size(S(wHud), S(hHud));
                NotControl = true;
            }

            protected override void Paint(Graphics g)
            {
                float k = K;
                RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
                using var p = ModernSkin.RoundRect(r, ModernTheme.Radius * (_big ? 1f : 0.8f) * k);
                using (var b = new SolidBrush(W.Over(ModernTheme.PanelB, 0.7f))) g.FillPath(b, p);
                ModernSkin.Stroke(g, p, _big ? ModernTheme.Accent : ModernTheme.Frame);
            }

            protected internal override void DrawControl()
            {
                base.DrawControl();
                if (Libraries.MagIcon2 == null) return;
                int index = _icon * 2;
                Size s = Libraries.MagIcon2.GetSize(index);
                if (s.IsEmpty) return;
                int w = Size.Width - 2, h = Size.Height - 2;
                float scale = Math.Min(w / (float)s.Width, h / (float)s.Height);
                if (!_big) scale = Math.Min(scale, Math.Max(1f, K));
                var size = new Size((int)(s.Width * scale), (int)(s.Height * scale));
                Libraries.MagIcon2.Draw(index, new Point(DisplayLocation.X + (Size.Width - size.Width) / 2, DisplayLocation.Y + (Size.Height - size.Height) / 2), size, Color.White);
            }
        }

        /// <summary>The key badge on a list icon.</summary>
        private sealed class KeyBadge : ModernControl
        {
            private readonly string _text;

            public KeyBadge(string text)
            {
                _text = text;
                NotControl = true;
                Size = new Size((int)Math.Ceiling(ModernSkin.Measure(text, ModernSkin.Font(9, true))) + S(7), S(12));
            }

            protected override void Paint(Graphics g)
            {
                using (var p = ModernSkin.RoundRect(new RectangleF(0, 0, Size.Width - 1, Size.Height - 1), 3 * K))
                using (var b = new SolidBrush(ModernTheme.Mix(ModernTheme.Accent, Color.Black, 0.7f)))
                    g.FillPath(b, p);
                ModernSkin.Text(g, _text, ModernSkin.Font(9, true), Color.White, new RectangleF(0, 0, Size.Width, Size.Height), StringAlignment.Center, StringAlignment.Center, false);
            }
        }
    }

    /// <summary>The cooldown sweep over a spell icon (rgba(0,0,0,.62) from the bottom), 64 steps: repaints only on a step change.</summary>
    internal sealed class CooldownVeil : ModernControl
    {
        public const int Steps = 64;
        private int _step;

        public CooldownVeil()
        {
            NotControl = true;
        }

        public void SetStep(int step)
        {
            step = Math.Clamp(step, 0, Steps);
            if (step == _step) return;
            _step = step;
            Invalidate();
        }

        protected override void Paint(Graphics g)
        {
            if (_step <= 0) return;
            float h = Size.Height * _step / (float)Steps;
            using var b = new SolidBrush(Color.FromArgb(158, 0, 0, 0));
            g.FillRectangle(b, 0, Size.Height - h, Size.Width, h);
        }
    }
}

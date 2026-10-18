// Ported from WebClient/src/ui/modern/windows/keybinds.ts (KeybindsWindow): the new UI's key binding editor over
// CMain.InputKeys (KeyBindSettings), in the role of the classic KeyboardLayoutDialog. The full keyboard (main block, navigation
// cluster, numpad) lights every bind on the shown modifier layer (none / ~ / Ctrl / Alt / Shift, combinable: the chips or the
// board's modifier keys) with a short action name per key and conflicts in red; hovering a key lists everything on it in the
// info strip; groups with counts, filters (All / Not default / Conflicts / Unbound), a search, the bind rows with keycap chips,
// a reset arrow per row and a conflict note that can unbind the other bind. Every change is applied and saved at once
// (KeyBindSettings.Save). The Assign Rule of the classic window: held modifiers become "must be held", the others "must not be
// held" (Strict) or "don't care" (Relaxed). While waiting for a key every key comes to this window (WindowManager.HandleKey →
// HandleKey): Esc cancels, Delete clears the bind (CheckNewInput), modifier keys alone are ignored; a click on the board takes
// that key with the shown layer. The wait ends on close and when another window comes to the front.
using System.Drawing.Drawing2D;
using System.Text;
using Client.MirControls;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class KeybindsWindow : ModernWindow
    {
        public const string WindowKey = "keybinds";
        private const int WidthHud = 1000;
        /// <summary>Content height: 610, or 570 on a short screen (the web's narrow layout height).</summary>
        private int HeightHud = 610;

        [Flags]
        internal enum Mod { None = 0, Tilde = 1, Ctrl = 2, Alt = 4, Shift = 8 }

        private static readonly Mod[] Mods = { Mod.Tilde, Mod.Ctrl, Mod.Alt, Mod.Shift };
        private static readonly string[] Filters = { "All", "Changed", "Conflicts", "Unbound" };

        private readonly List<KeyBind> _binds = new List<KeyBind>();
        private readonly List<string> _groups = new List<string>();
        private Mod _held;
        private string _group = "All", _filter = "All", _query = string.Empty, _note = string.Empty;
        private KeyBind _waiting, _sel;
        private string _focusKey, _mark, _hover;
        private Keys _swallow = Keys.None;
        private long _swallowUntil;
        private bool _enforce = true;
        private readonly Dictionary<KeybindOptions, List<KeyBind>> _conflicts = new Dictionary<KeybindOptions, List<KeyBind>>();

        private KeyboardBoard _board;
        private readonly PaintBox[] _layerChips = new PaintBox[5];
        private ModernLabel _layerStat, _info, _status;
        private PaintBox _infoBox;
        private ScrollView _side, _list;
        private readonly List<PaintBox> _groupItems = new List<PaintBox>();
        private readonly PaintBox[] _filterChips = new PaintBox[4];
        private readonly int[] _filterCounts = new int[4];
        private bool _dirty = true;

        public KeybindsWindow() : base(WindowKey, W.T(ClientTextKeys.ModernWinKeyBindings), Math.Max(0, (int)((Settings.ScreenWidth / ModernRoot.K - WidthHud) / 2)), 24)
        {
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
        }

        internal bool Waiting => _waiting != null;
        private static KeybindsWindow _capturing;
        /// <summary>Whether an open keybinds window waits for a key (the chat HUD then leaves Enter / Space / PageUp / … alone).</summary>
        internal static bool CapturingKey => _capturing != null && !_capturing.IsDisposed && _capturing.IsOpen && _capturing._waiting != null;
        internal KeyboardBoard Board => _board;
        internal string StatusText => _status?.Text ?? string.Empty;
        internal string InfoKeys => "hover=" + _hover + " focus=" + _focusKey + " mark=" + _mark;
        internal int ConflictCount => _conflicts.Count;
        /// <summary>The bind area of each listed row (tests click them).</summary>
        internal readonly Dictionary<KeybindOptions, PaintBox> BindAreas = new Dictionary<KeybindOptions, PaintBox>();
        internal readonly Dictionary<KeybindOptions, PaintBox> ResetArrows = new Dictionary<KeybindOptions, PaintBox>();

        private static List<KeyBind> Live => CMain.InputKeys.Keylist;

        private static KeyBind DefaultOf(KeybindOptions f)
        {
            foreach (KeyBind k in CMain.InputKeys.DefaultKeylist) if (k.function == f) return k;
            return null;
        }

        internal static byte Req(KeyBind b, Mod m) => m == Mod.Alt ? b.RequireAlt : m == Mod.Shift ? b.RequireShift : m == Mod.Ctrl ? b.RequireCtrl : b.RequireTilde;

        /// <summary>Whether a bind fires with exactly these modifiers held.</summary>
        internal static bool FiresOn(KeyBind b, Mod held)
        {
            if (b.Key == Keys.None) return false;
            foreach (Mod m in Mods)
            {
                byte r = Req(b, m);
                if (r != 2 && (r == 1) != held.HasFlag(m)) return false;
            }
            return true;
        }

        private static bool Same(KeyBind a, KeyBind b) => a.Key == b.Key && a.RequireAlt == b.RequireAlt && a.RequireShift == b.RequireShift && a.RequireCtrl == b.RequireCtrl && a.RequireTilde == b.RequireTilde;

        /// <summary>keybinds.ts overlapping: two binds on one key fire together when some modifier state satisfies both.</summary>
        private static bool Overlap(KeyBind a, KeyBind b)
        {
            if (a.Key == Keys.None || a.Key != b.Key || a.function == b.function) return false;
            foreach (Mod m in Mods)
            {
                byte x = Req(a, m), y = Req(b, m);
                if (x != 2 && y != 2 && x != y) return false;
            }
            return true;
        }

        private bool IsChanged(KeyBind b)
        {
            KeyBind d = DefaultOf(b.function);
            return d != null && !Same(b, d);
        }

        /// <summary>A short action name for a keycap (keybinds.ts short): the web's abbreviations, as UI strings.</summary>
        internal static string Short(KeyBind b)
        {
            KeybindOptions f = b.function;
            if (f >= KeybindOptions.Bar1Skill1 && f <= KeybindOptions.Bar1Skill8) return W.T(ClientTextKeys.ModernWinKeySkill, f - KeybindOptions.Bar1Skill1 + 1);
            if (f >= KeybindOptions.Bar2Skill1 && f <= KeybindOptions.Bar2Skill8) return W.T(ClientTextKeys.ModernWinKeyBar2, f - KeybindOptions.Bar2Skill1 + 1);
            if (f >= KeybindOptions.HeroSkill1 && f <= KeybindOptions.HeroSkill8) return W.T(ClientTextKeys.ModernWinKeyHero, f - KeybindOptions.HeroSkill1 + 1);
            if (f >= KeybindOptions.Belt1 && f <= KeybindOptions.Belt8Alt) return W.T(ClientTextKeys.ModernWinKeyBelt, (f - KeybindOptions.Belt1) / 2 + 1);
            switch (f)
            {
                case KeybindOptions.HeroBelt1:
                case KeybindOptions.HeroBelt1Alt: return W.T(ClientTextKeys.ModernWinKeyHeroBelt, 1);
                case KeybindOptions.HeroBelt2:
                case KeybindOptions.HeroBelt2Alt: return W.T(ClientTextKeys.ModernWinKeyHeroBelt, 2);
                case KeybindOptions.Inventory:
                case KeybindOptions.Inventory2: return W.T(ClientTextKeys.ModernWinBag);
                case KeybindOptions.Equipment:
                case KeybindOptions.Equipment2: return W.T(ClientTextKeys.ModernWinCatGear);
                case KeybindOptions.HeroInventory: return W.T(ClientTextKeys.ModernWinHeroBag);
                case KeybindOptions.HeroEquipment: return W.T(ClientTextKeys.ModernWinKeyHeroGear);
                case KeybindOptions.Creature: return W.T(ClientTextKeys.ModernWinKeyPets);
                case KeybindOptions.GameShop: return W.T(ClientTextKeys.ModernWinKeyShop);
                case KeybindOptions.Keybind: return W.T(ClientTextKeys.ModernWinKeyKeys);
                case KeybindOptions.Relationship: return W.T(ClientTextKeys.ModernWinKeySpouse);
                case KeybindOptions.Minimap: return W.T(ClientTextKeys.ModernWinKeyMiniMap);
                case KeybindOptions.Screenshot: return W.T(ClientTextKeys.ModernWinHelpScScreenshot);
                case KeybindOptions.Closeall: return W.T(ClientTextKeys.ModernWinHelpScCloseAll);
                case KeybindOptions.Pickup: return W.T(ClientTextKeys.ModernWinHelpScPickUp);
                case KeybindOptions.CreaturePickup: return W.T(ClientTextKeys.ModernWinKeyPetPickup);
                case KeybindOptions.CreatureAutoPickup: return W.T(ClientTextKeys.ModernWinKeyAutoPickup);
                case KeybindOptions.AddGroupMember: return W.T(ClientTextKeys.ModernWinKeyRecruit);
                case KeybindOptions.TargetSpellLockOn: return W.T(ClientTextKeys.ModernWinKeyLockOn);
                case KeybindOptions.Trade: return W.T(ClientTextKeys.ModernWinHelpScTrade);
                case KeybindOptions.Quests: return W.T(ClientTextKeys.ModernWinHelpScQuests);
                case KeybindOptions.Mount: return W.T(ClientTextKeys.ModernWinKeyRide);
            }
            // the rest: the description without the "Open/Close" / "Toggle" / mode prefixes
            return b.Description.Replace(" Open/Close Alt", "").Replace(" Open/Close", "").Replace("Toggle ", "").Replace("Set Attack Mode : ", "").Replace("Set Pet Mode : ", "");
        }

        /// <summary>A bind as keycap text ("Ctrl + F1"; "" = unbound).</summary>
        private static string Caps(KeyBind b)
        {
            if (b.Key == Keys.None) return string.Empty;
            var sb = new StringBuilder();
            if (b.RequireAlt == 1) sb.Append("Alt + ");
            if (b.RequireCtrl == 1) sb.Append("Ctrl + ");
            if (b.RequireShift == 1) sb.Append("Shift + ");
            if (b.RequireTilde == 1) sb.Append("~ + ");
            sb.Append(W.Cap(b.Key.ToString()));
            return sb.ToString();
        }

        // ------------------------------------------------------------------ layout

        protected override void Build()
        {
            const int top = TitleHeight;
            HeightHud = Settings.ScreenHeight / ModernRoot.K < 820 ? 570 : 610;
            ContentHeight = HeightHud;
            _binds.Clear();
            _binds.AddRange(Live);
            _binds.Sort((a, b) => { int c = string.CompareOrdinal(a.Group, b.Group); return c != 0 ? c : string.CompareOrdinal(a.Description, b.Description); });
            _groups.Clear();
            foreach (KeyBind b in _binds) if (!_groups.Contains(b.Group)) _groups.Add(b.Group);

            // layer bar
            W.Caption(this, W.T(ClientTextKeys.ModernWinLayer), 14, top + 12, 50);
            int x = 62;
            string[] chipText = { W.T(ClientTextKeys.ModernWinNoModifier), W.T(ClientTextKeys.ModernWinTildeHeld), "Ctrl", "Alt", "Shift" };
            for (int i = 0; i < 5; i++)
            {
                int idx = i;
                string text = chipText[i];
                int w = (int)Math.Ceiling(ModernSkin.Measure(text, ModernSkin.Font(11, true)) / ModernRoot.K) + 22;
                _layerChips[i] = new PaintBox((g, b) => Chip(g, b, text, b.Selected, false)) { Parent = this, Location = new Point(S(x), S(top + 8)), Size = new Size(S(w), S(22)) };
                _layerChips[i].Click += (o, e) =>
                {
                    if (idx == 0) _held = Mod.None;
                    else _held ^= Mods[idx - 1];
                    _dirty = true;
                };
                x += w + 6;
            }
            _layerStat = W.Label(this, string.Empty, x + 10, top + 8, WidthHud - x - 24, 22, 11, true, ModernTheme.Dim, StringAlignment.Far);

            // keyboard
            _board = new KeyboardBoard(this) { Parent = this };
            _board.Location = new Point((S(WidthHud) - _board.Size.Width) / 2, S(top + 40));
            int boardBottom = (int)Math.Round((_board.Location.Y + _board.Size.Height) / ModernRoot.K);

            // info strip
            _infoBox = new PaintBox((g, b) => W.Card(g, b.Size, _waiting != null ? ModernTheme.Accent : W.Over(ModernTheme.Frame, 0.5f))) { Parent = this, Location = new Point(S(14), S(boardBottom + 6)), Size = new Size(S(WidthHud - 28), S(30)), NotControl = true };
            _info = W.Label(_infoBox, string.Empty, 10, 0, WidthHud - 48, 30, 11, false, ModernTheme.Dim);

            // lower: groups + search | filters + list
            int low = boardBottom + 44, lowH = HeightHud + top - 40 - low;
            W.Divider(this, 0, low, WidthHud);
            var search = W.At(new SearchBox(170, W.T(ClientTextKeys.ModernWinSearchBinds)), this, 10, low + 8);
            if (_query.Length > 0) search.Text = _query;
            search.Search += q => { _query = q.ToLowerInvariant(); _focusKey = null; _dirty = true; };
            _side = new ScrollView(176, lowH - 40) { Parent = this, Location = new Point(S(8), S(low + 38)), Background = W.Page };
            _side.Framed = false;
            _groupItems.Clear();
            int gy = 0;
            var groupNames = new List<string> { "All" };
            groupNames.AddRange(_groups);
            foreach (string g in groupNames)
            {
                string name = g;
                int count = 0;
                foreach (KeyBind b in _binds) if (name == "All" || b.Group == name) count++;
                string label = name == "All" ? W.T(ClientTextKeys.ModernWinAllGroups) : name;
                var it = new PaintBox((gr, b) =>
                {
                    float k = ModernRoot.K;
                    W.Row(gr, b.Size, false, b.Hovered, b.Selected);
                    ModernSkin.Text(gr, label, ModernSkin.Font(12), b.Selected ? Color.White : ModernTheme.Text, new RectangleF(10 * k, 0, b.Size.Width - 50 * k, b.Size.Height));
                    ModernSkin.Text(gr, count.ToString(), ModernSkin.Font(10), b.Index > 0 ? ModernTheme.BadSoft : ModernTheme.Dim, new RectangleF(0, 0, b.Size.Width - 8 * k, b.Size.Height), StringAlignment.Far);
                })
                { Parent = _side.Content, Location = new Point(0, S(gy)), Size = new Size(_side.InnerWidth, S(26)), Tag2 = name };
                it.Click += (o, e) => { _group = name; _focusKey = null; _dirty = true; };
                _groupItems.Add(it);
                gy += 28;
            }
            _side.ContentHeight = S(gy);
            W.VDivider(this, 192, low, lowH);

            int fx = 202;
            string[] filterText = { W.T(ClientTextKeys.ModernWinAllBinds), W.T(ClientTextKeys.ModernWinNotDefault), W.T(ClientTextKeys.ModernWinConflicts), W.T(ClientTextKeys.ModernWinUnbound) };
            ClientTextKeys[] filterHint = { ClientTextKeys.ModernWinAllBindsHint, ClientTextKeys.ModernWinNotDefaultHint, ClientTextKeys.ModernWinConflictsHint, ClientTextKeys.ModernWinUnboundHint };
            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                string text = filterText[i];
                int w = (int)Math.Ceiling(ModernSkin.Measure(text, ModernSkin.Font(11, true)) / ModernRoot.K) + 46;
                _filterChips[i] = new PaintBox((g, b) => Chip(g, b, text, b.Selected, idx == 2 && _filterCounts[2] > 0, _filterCounts[idx].ToString()))
                { Parent = this, Location = new Point(S(fx), S(low + 8)), Size = new Size(S(w), S(22)), Hint = W.T(filterHint[i]) };
                _filterChips[i].Click += (o, e) => { _filter = Filters[idx]; _focusKey = null; _dirty = true; };
                fx += w + 6;
            }
            _list = new ScrollView(WidthHud - 200 - 10, lowH - 40) { Parent = this, Location = new Point(S(200), S(low + 36)), Background = W.Page };
            _list.Framed = false;

            // footer: assign rule, status, reset all
            int foot = top + HeightHud - 38;
            W.Divider(this, 0, foot, WidthHud);
            W.Caption(this, W.T(ClientTextKeys.ModernWinAssignRule), 14, foot + 13, 90);
            var rule = W.At(new Segmented<bool>(new[]
            {
                new Choice<bool>(true, W.T(ClientTextKeys.ModernWinStrict), null, W.T(ClientTextKeys.ModernWinStrictHint)),
                new Choice<bool>(false, W.T(ClientTextKeys.ModernWinRelaxed), null, W.T(ClientTextKeys.ModernWinRelaxedHint)),
            }, _enforce, 160), this, 104, foot + 8);
            rule.Changed += v => { _enforce = v; _dirty = true; };
            var reset = new ModernButton(W.T(ClientTextKeys.ModernWinResetAll), ButtonKind.Warn) { Parent = this, Tooltip = W.T(ClientTextKeys.ModernWinResetAllHint) };
            reset.AutoWidth(90);
            reset.Location = new Point(S(WidthHud - 14) - reset.Size.Width, S(foot + 9));
            reset.Click += (o, e) => ResetAll();
            _status = W.Label(this, string.Empty, 276, foot + 8, WidthHud - 276 - 130, 24, 11, false, ModernTheme.Dim);
            _dirty = true;
            Render();
        }

        private static void Chip(Graphics g, PaintBox b, string text, bool on, bool warn, string count = null)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using var p = ModernSkin.RoundRect(r, r.Height / 2);
            using (var br = new SolidBrush(on ? W.Over(ModernTheme.Accent, 0.35f) : ModernSkin.Opaque(ModernTheme.PanelB))) g.FillPath(br, p);
            ModernSkin.Stroke(g, p, warn ? ModernTheme.Bad : on || b.Hovered ? ModernTheme.Accent : ModernTheme.Frame);
            Font f = ModernSkin.Font(11, true);
            if (count == null)
            {
                ModernSkin.Text(g, text, f, on ? Color.White : ModernTheme.Text, r, StringAlignment.Center);
                return;
            }
            float tw = ModernSkin.Measure(text, f);
            ModernSkin.Text(g, text, f, on ? Color.White : ModernTheme.Text, new RectangleF(10 * k, 0, tw + 2, b.Size.Height));
            ModernSkin.Text(g, count, ModernSkin.Font(10), on ? Color.White : warn ? ModernTheme.BadSoft : ModernTheme.Dim, new RectangleF(10 * k + tw + 6 * k, 0, 30 * k, b.Size.Height));
        }

        // ------------------------------------------------------------------ lifecycle / keys

        protected override void Closed()
        {
            CancelWait();
            _hover = null;
            _swallow = Keys.None;
        }

        public override void Process()
        {
            base.Process();
            if (_waiting != null && ModernRoot.Instance?.Wm.Front != this) CancelWait(); // another window came in front
            if (_dirty) Render();
        }

        /// <summary>Keys while this is the front window: while waiting every key is taken here (CheckNewInput).</summary>
        public override bool HandleKey(KeyEventArgs e)
        {
            if (_waiting == null)
            {
                if (_swallow != Keys.None)
                {
                    if (e.KeyCode == _swallow && CMain.Time < _swallowUntil) return true; // the just-assigned key's auto-repeat
                    _swallow = Keys.None;
                }
                return false;
            }
            Keys key = e.KeyCode;
            if (key == Keys.ControlKey || key == Keys.Menu || key == Keys.ShiftKey || key == Keys.Oem8 || key == Keys.None) return true;
            KeyBind fn = _waiting;
            if (key == Keys.Escape) { CancelWait(); return true; }
            if (key == Keys.Delete) { CancelWait(); Apply(fn, Keys.None, Mod.None); return true; }
            CancelWait();
            _swallow = key;
            _swallowUntil = CMain.Time + 600;
            Mod held = (e.Alt || CMain.Alt ? Mod.Alt : 0) | (e.Shift || CMain.Shift ? Mod.Shift : 0) | (e.Control || CMain.Ctrl ? Mod.Ctrl : 0) | (CMain.Tilde ? Mod.Tilde : 0);
            Apply(fn, key, held);
            return true;
        }

        private void StartWait(KeyBind b)
        {
            if (_waiting == b) { CancelWait(); return; }
            _waiting = b;
            _capturing = this;
            _sel = b;
            _note = string.Empty;
            ModernRoot.Instance?.Wm.Open(WindowKey); // stay in front: the keys come to the front window
            _dirty = true;
        }

        private void CancelWait()
        {
            if (_waiting == null) return;
            _waiting = null;
            _dirty = true;
        }

        /// <summary>Assigns a key with these modifiers held under the Assign Rule; Keys.None clears the bind. Saved at once.</summary>
        private void Apply(KeyBind b, Keys key, Mod held)
        {
            _sel = b;
            _note = string.Empty;
            if (key == Keys.None)
            {
                b.Key = Keys.None;
                b.RequireAlt = b.RequireShift = b.RequireCtrl = b.RequireTilde = 2;
            }
            else
            {
                byte R(Mod m) => (byte)(held.HasFlag(m) ? 1 : _enforce ? 0 : 2);
                b.Key = key;
                b.RequireAlt = R(Mod.Alt);
                b.RequireShift = R(Mod.Shift);
                b.RequireCtrl = R(Mod.Ctrl);
                b.RequireTilde = R(Mod.Tilde);
                _mark = key.ToString();
                _held = held;
            }
            Save();
        }

        private static void Save()
        {
            try { CMain.InputKeys.Save(CMain.InputKeys.Keylist); }
            catch (Exception ex) { ModernLog.Once("[ModernUi] keybinds save", ex); }
            ModernEvents.SkillBars.Emit(); // the skill rows read their key labels
        }

        internal void BoardClick(string name, Mod mod)
        {
            if (mod != Mod.None) { _held ^= mod; _dirty = true; return; }
            if (_waiting != null)
            {
                KeyBind fn = _waiting;
                CancelWait();
                if (Enum.TryParse(name, out Keys key)) Apply(fn, key, _held);
                return;
            }
            _focusKey = _focusKey == name ? null : name;
            if (_focusKey != null) { _group = "All"; _filter = "All"; }
            _dirty = true;
        }

        internal void BoardHover(string name)
        {
            if (name == _hover) return;
            _hover = name;
            RenderInfo();
        }

        private void ResetAll()
        {
            CancelWait();
            ModernRoot.Instance?.Popups.Confirm(W.T(ClientTextKeys.ModernWinResetAllAsk), PopupButtons.YesNo, PopupKind.Warning, (a, _) =>
            {
                if (a != PopupAnswer.Yes || IsDisposed) return;
                foreach (KeyBind b in Live)
                {
                    KeyBind d = DefaultOf(b.function);
                    if (d == null) continue;
                    b.Key = d.Key;
                    b.RequireAlt = d.RequireAlt;
                    b.RequireCtrl = d.RequireCtrl;
                    b.RequireShift = d.RequireShift;
                    b.RequireTilde = d.RequireTilde;
                }
                Save();
                _note = W.T(ClientTextKeys.KeyboardSettingsResetDefault);
                _dirty = true;
            }, false, W.T(ClientTextKeys.ModernWinResetKeyBindings));
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            _dirty = false;
            if (_board == null || _board.IsDisposed) return;
            _conflicts.Clear();
            foreach (KeyBind b in _binds)
            {
                if (b.Key == Keys.None) continue;
                List<KeyBind> o = null;
                foreach (KeyBind c in _binds)
                    if (Overlap(b, c)) (o ??= new List<KeyBind>()).Add(c);
                if (o != null) _conflicts[b.function] = o;
            }
            // layer chips + count
            _layerChips[0].Selected = _held == Mod.None;
            for (int i = 0; i < 4; i++) _layerChips[i + 1].Selected = _held.HasFlag(Mods[i]);
            int lit = 0;
            foreach (KeyBind b in _binds) if (FiresOn(b, _held)) lit++;
            _layerStat.Text = W.T(ClientTextKeys.ModernWinLayerStat, lit, _conflicts.Count);
            _layerStat.Colour = _conflicts.Count > 0 ? ModernTheme.BadSoft : ModernTheme.Dim;
            _board.Update(_binds, _held, _conflicts, _focusKey, _mark, _waiting != null);
            _infoKey = "\0";
            RenderInfo();
            // groups
            foreach (PaintBox it in _groupItems)
            {
                it.Selected = it.Tag2 == _group;
                int n = 0;
                foreach (KeyBind b in _binds) if ((it.Tag2 == "All" || b.Group == it.Tag2) && _conflicts.ContainsKey(b.function)) n++;
                if (it.Index != n) { it.Index = n; it.Invalidate(); }
                it.Hint = n > 0 ? W.T(ClientTextKeys.ModernWinBindsInConflict, n) : null;
            }
            // filters
            _filterCounts[0] = _binds.Count;
            _filterCounts[1] = 0;
            _filterCounts[2] = _conflicts.Count;
            _filterCounts[3] = 0;
            foreach (KeyBind b in _binds) { if (IsChanged(b)) _filterCounts[1]++; if (b.Key == Keys.None) _filterCounts[3]++; }
            for (int i = 0; i < 4; i++) { _filterChips[i].Selected = Filters[i] == _filter; _filterChips[i].Invalidate(); }
            RenderList();
            // footer
            int changed = _filterCounts[1];
            _status.Colour = _note.Length > 0 ? ModernTheme.Ok : ModernTheme.Dim;
            _status.Text = _note.Length > 0 ? _note : _waiting != null ? W.T(ClientTextKeys.ModernWinWaitingKey)
                : changed > 0 ? W.T(ClientTextKeys.ModernWinBindsDiffer, changed) : W.T(ClientTextKeys.ModernWinDefaultBinds);
        }

        private string _infoKey = "\0";

        private void RenderInfo()
        {
            if (_info == null || _info.IsDisposed) return;
            string key = _waiting != null ? "\u0001" : _hover ?? _focusKey ?? _mark ?? string.Empty;
            if (key == _infoKey) return;
            _infoKey = key;
            _infoBox.Invalidate();
            _info.Colour = _waiting != null ? Color.White : ModernTheme.Dim;
            if (_waiting != null)
            {
                _info.Text = W.T(ClientTextKeys.ModernWinRebinding, _waiting.Description, W.T(_enforce ? ClientTextKeys.ModernWinStrict : ClientTextKeys.ModernWinRelaxed));
                return;
            }
            if (key.Length == 0) { _info.Text = W.T(ClientTextKeys.ModernWinBoardHint); return; }
            var sb = new StringBuilder();
            foreach (KeyBind b in _binds)
            {
                if (b.Key.ToString() != key) continue;
                if (sb.Length > 0) sb.Append("   ·   ");
                sb.Append('[').Append(Caps(b)).Append("] ").Append(b.Description);
            }
            _info.Text = sb.Length > 0 ? sb.ToString() : "[" + W.Cap(key) + "] " + W.T(ClientTextKeys.ModernWinNothingBound);
        }

        private bool Listed(KeyBind b)
        {
            if (_focusKey != null) return b.Key.ToString() == _focusKey;
            if (_group != "All" && b.Group != _group) return false;
            if (_filter == "Changed" && !IsChanged(b)) return false;
            if (_filter == "Conflicts" && !_conflicts.ContainsKey(b.function)) return false;
            if (_filter == "Unbound" && b.Key != Keys.None) return false;
            if (_query.Length > 0 && !b.Description.ToLowerInvariant().Contains(_query) && !(b.Key != Keys.None && Caps(b).ToLowerInvariant().Contains(_query)) && !b.Group.ToLowerInvariant().Contains(_query))
                return false;
            return true;
        }

        private void RenderList()
        {
            int keep = _list.ScrollY;
            for (int i = _list.Content.Controls.Count - 1; i >= 0; i--) _list.Content.Controls[i].Dispose();
            BindAreas.Clear();
            ResetArrows.Clear();
            int w = (int)Math.Round(_list.InnerWidth / ModernRoot.K), y = 0, count = 0, odd = 0;
            if (_focusKey != null)
            {
                W.Label(_list.Content, W.T(ClientTextKeys.ModernWinBoundTo, W.Cap(_focusKey)).ToUpperInvariant(), 4, y + 6, w - 100, 16, 10, true, ModernTheme.Accent);
                var all = new PaintBox((g, b) => ModernSkin.Text(g, W.T(ClientTextKeys.ModernWinShowAll), ModernSkin.Font(11, false), b.Hovered ? Color.White : ModernTheme.Accent, new RectangleF(0, 0, b.Size.Width, b.Size.Height)))
                { Parent = _list.Content, Location = new Point(S(w - 90), S(y + 4)), Size = new Size(S(86), S(20)) };
                all.Click += (o, e) => { _focusKey = null; _dirty = true; };
                y += 26;
            }
            string group = null;
            foreach (KeyBind b in _binds)
            {
                if (!Listed(b)) continue;
                if (_focusKey == null && b.Group != group)
                {
                    group = b.Group;
                    W.Label(_list.Content, group.ToUpperInvariant(), 4, y + 6, w, 16, 10, true, ModernTheme.Accent);
                    y += 26;
                }
                y = Row(b, y, w, odd++ % 2 == 1);
                count++;
            }
            if (count == 0)
                W.Label(_list.Content, W.T(_filter == "Conflicts" && _focusKey == null ? ClientTextKeys.ModernWinNoConflicts : ClientTextKeys.ModernWinNothingMatches), 0, y + 30, w, 20, 12, false, ModernTheme.Dim, StringAlignment.Center);
            _list.ContentHeight = S(y + 6);
            _list.ScrollTo(keep);
        }

        private int Row(KeyBind b, int y, int w, bool shade)
        {
            bool waiting = _waiting == b;
            _conflicts.TryGetValue(b.function, out List<KeyBind> clash);
            bool changed = IsChanged(b);
            int h = clash != null ? 40 : 30;
            string desc = b.Description;
            var row = new PaintBox((g, bx) =>
            {
                float k = ModernRoot.K;
                RectangleF r = new RectangleF(0.5f, 0.5f, bx.Size.Width - 1f, bx.Size.Height - 1f);
                using var p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
                if (shade || bx.Selected)
                    using (var br = new SolidBrush(bx.Selected ? W.Over(ModernTheme.Accent, 0.1f) : W.Over(ModernTheme.FrameLight, 0.04f))) g.FillPath(br, p);
                if (bx.Selected || bx.Hovered) ModernSkin.Stroke(g, p, bx.Selected ? ModernTheme.Accent : W.Over(ModernTheme.Frame, 0.8f));
                float x = 8 * k;
                if (changed)
                {
                    using (var br = new SolidBrush(ModernTheme.Accent)) g.FillEllipse(br, x, 11 * k, 6 * k, 6 * k);
                    x += 12 * k;
                }
                ModernSkin.Text(g, desc, ModernSkin.Font(12), ModernTheme.Text, new RectangleF(x, 0, bx.Size.Width - 230 * k, 28 * k));
            })
            { Parent = _list.Content, Location = new Point(0, S(y)), Size = new Size(S(w), S(h)), Selected = _sel == b };
            row.Click += (o, e) =>
            {
                _sel = b;
                if (b.Key != Keys.None)
                {
                    _held = Mod.None;
                    foreach (Mod m in Mods) if (Req(b, m) == 1) _held |= m;
                    _mark = b.Key.ToString();
                }
                _dirty = true;
            };
            if (clash != null)
            {
                KeyBind other = clash[0];
                string text = W.T(ClientTextKeys.ModernWinAlsoFires, other.Description) + (clash.Count > 1 ? " (+" + (clash.Count - 1) + ")" : string.Empty);
                W.Label(row, text, changed ? 20 : 8, 22, w - 340, 14, 10, false, ModernTheme.BadSoft);
                var fix = new PaintBox((g, bx) => ModernSkin.Text(g, W.T(ClientTextKeys.ModernWinUnbindIt), ModernSkin.Font(10), bx.Hovered ? Color.White : ModernTheme.Accent, new RectangleF(0, 0, bx.Size.Width, bx.Size.Height)))
                { Parent = row, Location = new Point(S(w - 330), S(22)), Size = new Size(S(70), S(14)) };
                fix.Click += (o, e) => { CancelWait(); _note = string.Empty; other.Key = Keys.None; other.RequireAlt = other.RequireShift = other.RequireCtrl = other.RequireTilde = 2; Save(); _dirty = true; };
            }
            KeyBind def = DefaultOf(b.function);
            var rst = new PaintBox((g, bx) =>
            {
                if (!changed) return;
                ModernSkin.Text(g, "↺", ModernSkin.Font(13, true), bx.Hovered ? ModernTheme.Accent : ModernTheme.Dim, new RectangleF(0, 0, bx.Size.Width, bx.Size.Height), StringAlignment.Center);
            })
            { Parent = row, Location = new Point(S(w - 196), S(4)), Size = new Size(S(20), S(22)), Hint = W.T(ClientTextKeys.ModernWinBackToDefaultKey, def != null && def.Key != Keys.None ? Caps(def) : W.T(ClientTextKeys.ModernWinUnbound)) };
            rst.Click += (o, e) =>
            {
                if (def == null || !changed) return;
                CancelWait();
                _sel = b;
                _note = string.Empty;
                b.Key = def.Key; b.RequireAlt = def.RequireAlt; b.RequireShift = def.RequireShift; b.RequireCtrl = def.RequireCtrl; b.RequireTilde = def.RequireTilde;
                Save();
                _dirty = true;
            };
            string caps = Caps(b);
            bool conflict = clash != null;
            var bind = new PaintBox((g, bx) => PaintBind(g, bx, caps, waiting, conflict))
            { Parent = row, Location = new Point(S(w - 170), S(3)), Size = new Size(S(164), S(24)), Hint = W.T(waiting ? ClientTextKeys.ModernWinClickToCancel : ClientTextKeys.ModernWinClickToBind) };
            bind.Click += (o, e) => StartWait(b);
            BindAreas[b.function] = bind;
            ResetArrows[b.function] = rst;
            return y + h + 2;
        }

        private static void PaintBind(Graphics g, PaintBox b, string caps, bool waiting, bool conflict)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using (var p = ModernSkin.RoundRect(r, ModernTheme.Radius * 0.7f * k))
            {
                if (waiting) ModernSkin.Stroke(g, p, ModernTheme.Accent);
                else if (b.Hovered) using (var pen = new Pen(ModernTheme.Frame, 1f) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, p);
            }
            if (waiting)
            {
                ModernSkin.Text(g, W.T(ClientTextKeys.ModernWinPressAKey), ModernSkin.Font(11), Color.White, r, StringAlignment.Far);
                return;
            }
            if (caps.Length == 0)
            {
                using var f = new Font(ModernSkin.Font(11).FontFamily, ModernSkin.Font(11).Size, FontStyle.Italic, GraphicsUnit.Pixel);
                ModernSkin.Text(g, W.T(ClientTextKeys.ModernWinUnbound), f, ModernTheme.Dim, new RectangleF(0, 0, b.Size.Width - 6 * k, b.Size.Height), StringAlignment.Far);
                return;
            }
            // keycap chips right-aligned: modifiers in the accent, the key in white (red when in conflict)
            string[] parts = caps.Split(" + ");
            Font cf = ModernSkin.Font(10, true), pf = ModernSkin.Font(10);
            float x = b.Size.Width - 4 * k;
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                float cw = Math.Max(22 * k, ModernSkin.Measure(parts[i], cf) + 12 * k);
                RectangleF c = new RectangleF(x - cw, 2 * k, cw, 19 * k);
                using (var p = ModernSkin.RoundRect(c, 4 * k))
                {
                    ModernSkin.FillVertical(g, p, c, new[] { ModernSkin.Opaque(ModernTheme.PanelC), ModernSkin.Opaque(ModernTheme.PanelB) });
                    bool key = i == parts.Length - 1;
                    ModernSkin.Stroke(g, p, key && conflict ? ModernTheme.Bad : ModernTheme.Frame);
                    ModernSkin.Text(g, parts[i], cf, key ? (conflict ? ModernTheme.Hex("#ffb0a6") : Color.White) : ModernTheme.Accent, c, StringAlignment.Center);
                }
                x -= cw;
                if (i > 0)
                {
                    ModernSkin.Text(g, "+", pf, ModernTheme.Dim, new RectangleF(x - 10 * k, 0, 10 * k, b.Size.Height), StringAlignment.Center);
                    x -= 12 * k;
                }
            }
        }
    }

    /// <summary>
    /// The keyboard of the key binding editor (keybinds.ts board): main block, navigation cluster and numpad, painted as one
    /// texture and repainted when the binds, the layer, the focus or the hovered key change. A click on a key focuses its binds
    /// (or assigns it while waiting); a modifier key toggles that layer.
    /// </summary>
    internal sealed class KeyboardBoard : RegionWidget
    {
        private readonly struct KeyCap
        {
            public readonly string Name;
            public readonly KeybindsWindow.Mod Mod;
            public readonly RectangleF Rect;
            public readonly Keys Key;

            public KeyCap(string name, KeybindsWindow.Mod mod, RectangleF rect)
            {
                Name = name;
                Mod = mod;
                Rect = rect;
                Key = Enum.TryParse(name, out Keys k) ? k : Keys.None;
            }
        }

        private const float U = 36, G = 3;
        private readonly KeybindsWindow _owner;
        private readonly List<KeyCap> _keys = new List<KeyCap>();
        private readonly string[] _labels, _counts;
        private readonly bool[] _on, _clash, _focus;
        private KeybindsWindow.Mod _held;
        private bool _waiting;
        private int _lastHot = -2;

        public KeyboardBoard(KeybindsWindow owner)
        {
            _owner = owner;
            Sound = MirSounds.SoundList.None;
            float x = 0;
            float w = Block(x, MainRows());
            x += w + 14;
            w = Block(x, Nav());
            x += w + 14;
            w = Block(x, Pad());
            x += w;
            Size = new Size(S(x), S(6 * U + 5 * G + 6));
            _labels = new string[_keys.Count];
            _counts = new string[_keys.Count];
            _on = new bool[_keys.Count];
            _clash = new bool[_keys.Count];
            _focus = new bool[_keys.Count];
        }

        internal int KeyCount => _keys.Count;

        /// <summary>The centre of a named key (device px, relative to the board), for tests.</summary>
        internal Point KeyCentre(string name)
        {
            foreach (KeyCap k in _keys)
                if (k.Name == name && k.Mod == KeybindsWindow.Mod.None) return new Point(S(k.Rect.X + k.Rect.Width / 2), S(k.Rect.Y + k.Rect.Height / 2));
            return Point.Empty;
        }

        private static (string, float, KeybindsWindow.Mod)[][] MainRows()
        {
            var f = new List<(string, float, KeybindsWindow.Mod)> { ("Escape", 1, 0), ("", 1, 0) };
            for (int i = 1; i <= 12; i++) { f.Add(("F" + i, 1, 0)); if (i == 4 || i == 8) f.Add(("", 0.5f, 0)); }
            (string, float, KeybindsWindow.Mod)[] Letters(string s)
            {
                var l = new (string, float, KeybindsWindow.Mod)[s.Length];
                for (int i = 0; i < s.Length; i++) l[i] = (char.IsDigit(s[i]) ? "D" + s[i] : s[i].ToString(), 1, 0);
                return l;
            }
            var r1 = new List<(string, float, KeybindsWindow.Mod)> { ("Oem8", 1, KeybindsWindow.Mod.Tilde) };
            r1.AddRange(Letters("1234567890"));
            r1.AddRange(new (string, float, KeybindsWindow.Mod)[] { ("OemMinus", 1, 0), ("Oemplus", 1, 0), ("Back", 2, 0) });
            var r2 = new List<(string, float, KeybindsWindow.Mod)> { ("Tab", 1.5f, 0) };
            r2.AddRange(Letters("QWERTYUIOP"));
            r2.AddRange(new (string, float, KeybindsWindow.Mod)[] { ("OemOpenBrackets", 1, 0), ("Oem6", 1, 0), ("Oem5", 1.5f, 0) });
            var r3 = new List<(string, float, KeybindsWindow.Mod)> { ("Capital", 1.75f, 0) };
            r3.AddRange(Letters("ASDFGHJKL"));
            r3.AddRange(new (string, float, KeybindsWindow.Mod)[] { ("Oem1", 1, 0), ("Oem7", 1, 0), ("Return", 2.25f, 0) });
            var r4 = new List<(string, float, KeybindsWindow.Mod)> { ("ShiftKey", 2.25f, KeybindsWindow.Mod.Shift) };
            r4.AddRange(Letters("ZXCVBNM"));
            r4.AddRange(new (string, float, KeybindsWindow.Mod)[] { ("Oemcomma", 1, 0), ("OemPeriod", 1, 0), ("OemQuestion", 1, 0), ("ShiftKey", 2.75f, KeybindsWindow.Mod.Shift) });
            var r5 = new (string, float, KeybindsWindow.Mod)[]
            {
                ("ControlKey", 1.25f, KeybindsWindow.Mod.Ctrl), ("LWin", 1.25f, 0), ("Menu", 1.25f, KeybindsWindow.Mod.Alt), ("Space", 6.25f, 0),
                ("Menu", 1.25f, KeybindsWindow.Mod.Alt), ("RWin", 1.25f, 0), ("Apps", 1.25f, 0), ("ControlKey", 1.25f, KeybindsWindow.Mod.Ctrl),
            };
            return new[] { f.ToArray(), r1.ToArray(), r2.ToArray(), r3.ToArray(), r4.ToArray(), r5 };
        }

        private static (string, float, KeybindsWindow.Mod)[][] Nav() => new[]
        {
            new (string, float, KeybindsWindow.Mod)[] { ("PrintScreen", 1, 0), ("Scroll", 1, 0), ("Pause", 1, 0) },
            new (string, float, KeybindsWindow.Mod)[] { ("Insert", 1, 0), ("Home", 1, 0), ("PageUp", 1, 0) },
            new (string, float, KeybindsWindow.Mod)[] { ("Delete", 1, 0), ("End", 1, 0), ("Next", 1, 0) },
            new (string, float, KeybindsWindow.Mod)[0],
            new (string, float, KeybindsWindow.Mod)[] { ("", 1, 0), ("Up", 1, 0), ("", 1, 0) },
            new (string, float, KeybindsWindow.Mod)[] { ("Left", 1, 0), ("Down", 1, 0), ("Right", 1, 0) },
        };

        private static (string, float, KeybindsWindow.Mod)[][] Pad() => new[]
        {
            new (string, float, KeybindsWindow.Mod)[0],
            new (string, float, KeybindsWindow.Mod)[] { ("NumLock", 1, 0), ("Divide", 1, 0), ("Multiply", 1, 0), ("Subtract", 1, 0) },
            new (string, float, KeybindsWindow.Mod)[] { ("NumPad7", 1, 0), ("NumPad8", 1, 0), ("NumPad9", 1, 0), ("Add", 1, 0) },
            new (string, float, KeybindsWindow.Mod)[] { ("NumPad4", 1, 0), ("NumPad5", 1, 0), ("NumPad6", 1, 0), ("", 1, 0) },
            new (string, float, KeybindsWindow.Mod)[] { ("NumPad1", 1, 0), ("NumPad2", 1, 0), ("NumPad3", 1, 0), ("Return", 1, 0) },
            new (string, float, KeybindsWindow.Mod)[] { ("NumPad0", 2, 0), ("Decimal", 1, 0), ("", 1, 0) },
        };

        /// <summary>Lays out a block at x (HUD px); returns its width.</summary>
        private float Block(float x0, (string Name, float W, KeybindsWindow.Mod Mod)[][] rows)
        {
            float max = 0, y = 0;
            for (int r = 0; r < rows.Length; r++)
            {
                float x = x0;
                foreach (var (name, w, mod) in rows[r])
                {
                    float kw = U * w + G * (w - 1);
                    if (name.Length > 0) _keys.Add(new KeyCap(name, mod, new RectangleF(x, y, kw, U)));
                    x += kw + G;
                }
                max = Math.Max(max, x - G - x0);
                y += U + G + (r == 0 ? 6 : 0);
            }
            return max;
        }

        /// <summary>The binds on the shown layer, conflicts, focus (keybinds.ts renderBoard). Repaints once.</summary>
        public void Update(List<KeyBind> binds, KeybindsWindow.Mod held, Dictionary<KeybindOptions, List<KeyBind>> conflicts, string focus, string mark, bool waiting)
        {
            _held = held;
            _waiting = waiting;
            for (int i = 0; i < _keys.Count; i++)
            {
                KeyCap k = _keys[i];
                _labels[i] = string.Empty;
                _counts[i] = string.Empty;
                _on[i] = _clash[i] = false;
                _focus[i] = k.Name == focus || k.Name == mark;
                if (k.Mod != KeybindsWindow.Mod.None || k.Key == Keys.None) continue;
                int n = 0;
                var sb = new StringBuilder();
                foreach (KeyBind b in binds)
                {
                    if (b.Key != k.Key || !KeybindsWindow.FiresOn(b, held)) continue;
                    if (n++ > 0) sb.Append(" · ");
                    sb.Append(KeybindsWindow.Short(b));
                    if (conflicts.ContainsKey(b.function)) _clash[i] = true;
                }
                _on[i] = n > 0;
                _labels[i] = sb.ToString();
                _counts[i] = n > 1 ? "×" + n : string.Empty;
            }
            Invalidate();
        }

        protected override int RegionAt(Point local)
        {
            float k = K;
            for (int i = 0; i < _keys.Count; i++)
            {
                RectangleF r = _keys[i].Rect;
                if (local.X >= r.X * k && local.X < r.Right * k && local.Y >= r.Y * k && local.Y < r.Bottom * k) return i;
            }
            return -1;
        }

        protected override void RegionClicked(int index)
        {
            KeyCap k = _keys[index];
            _owner.BoardClick(k.Name, k.Mod);
        }

        public override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            ReportHot();
        }

        protected override void OnMouseLeave()
        {
            base.OnMouseLeave();
            _lastHot = -1;
            _owner.BoardHover(null);
        }

        private void ReportHot()
        {
            int hot = Hot;
            if (hot == _lastHot) return;
            _lastHot = hot;
            _owner.BoardHover(hot >= 0 && _keys[hot].Mod == KeybindsWindow.Mod.None ? _keys[hot].Name : null);
        }

        protected override void Paint(Graphics g)
        {
            float k = K;
            Font cap = ModernSkin.Font(10, true), lab = ModernSkin.Font(8, true), cnt = ModernSkin.Font(8, true);
            int hot = Hot;
            for (int i = 0; i < _keys.Count; i++)
            {
                KeyCap key = _keys[i];
                RectangleF r = new RectangleF(key.Rect.X * k + 0.5f, key.Rect.Y * k + 0.5f, key.Rect.Width * k - 1f, key.Rect.Height * k - 1f);
                using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * 0.6f * k);
                bool modKey = key.Mod != KeybindsWindow.Mod.None, held = modKey && _held.HasFlag(key.Mod);
                Color top, bottom, border;
                if (held) { top = bottom = W.Over(ModernTheme.Accent, 0.45f); border = ModernTheme.Accent; }
                else if (_clash[i]) { top = ModernTheme.Hex("#5a1a18"); bottom = ModernTheme.Hex("#2a0c0c"); border = ModernTheme.Bad; }
                else if (_on[i]) { top = W.Over(ModernTheme.Accent, 0.3f); bottom = W.Over(ModernTheme.Accent, 0.12f); border = ModernTheme.Mix(ModernTheme.Accent, W.Page, 0.7f); }
                else
                {
                    top = ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelC), Color.Black, 0.8f);
                    bottom = ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, 0.8f);
                    border = _waiting && !modKey ? ModernTheme.Mix(ModernTheme.Accent, ModernTheme.Frame, 0.45f) : W.Over(ModernTheme.Frame, modKey ? 0.9f : 0.8f);
                }
                if (i == hot) border = Color.White;
                using (var shadow = new SolidBrush(Color.FromArgb(180, 0, 0, 0))) g.FillRectangle(shadow, r.X + 1, r.Bottom - 1, r.Width - 2, 2 * k);
                ModernSkin.FillVertical(g, p, r, new[] { top, bottom });
                ModernSkin.Stroke(g, p, border);
                if (_focus[i])
                    using (GraphicsPath fp = ModernSkin.RoundRect(RectangleF.Inflate(r, 2 * k, 2 * k), ModernTheme.Radius * 0.6f * k + 2 * k))
                        ModernSkin.Stroke(g, fp, Color.White, Math.Max(1.5f, 2 * k));
                string text = key.Name.StartsWith("NumPad", StringComparison.Ordinal) ? key.Name.Substring(6) : W.Cap(key.Name);
                ModernSkin.Text(g, text, cap, held || _on[i] ? Color.White : modKey ? ModernTheme.Text : ModernTheme.Dim, new RectangleF(r.X + 4 * k, r.Y + 1 * k, r.Width - 6 * k, 13 * k), StringAlignment.Near, StringAlignment.Near);
                if (_counts[i].Length > 0)
                    ModernSkin.Text(g, _counts[i], cnt, ModernTheme.Accent, new RectangleF(r.X, r.Y + 1 * k, r.Width - 3 * k, 11 * k), StringAlignment.Far, StringAlignment.Near, false);
                if (_labels[i].Length > 0)
                {
                    RectangleF lr = new RectangleF(r.X + 2 * k, r.Bottom - 20 * k, r.Width - 4 * k, 19 * k);
                    ModernSkin.Text(g, _labels[i], lab, Color.FromArgb(150, 0, 0, 0), new RectangleF(lr.X, lr.Y + 1, lr.Width, lr.Height), StringAlignment.Center, StringAlignment.Far, true, true);
                    ModernSkin.Text(g, _labels[i], lab, Color.White, lr, StringAlignment.Center, StringAlignment.Far, true, true);
                }
            }
        }
    }
}

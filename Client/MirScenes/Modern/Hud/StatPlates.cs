// Ported from WebClient/src/ui/modern/hud/statPlates.ts (the six stat plates above the HP / MP bars, the plate picker and
// the HUD's one small popover). Desktop idiom: the popover is a ModernSurface on ModernRoot.Menu (above the windows, the web
// "lifts" the rig instead); while a list is open it takes every mouse press, and a press outside the list closes it (the web's
// capture pointerdown). Plate values are read ~4x a second as numbers and formatted only when they changed.
// Settings.UiPlates is a bool on the desktop (plates on / off); the picks live in HudPlateStore (session only until the
// integrator adds a Settings string: see HudPlateStore).
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;

namespace Client.MirScenes.Modern.Hud
{
    // ------------------------------------------------------------------ popover

    /// <summary>The HUD's one popover (statPlates.ts Popover): one open list at a time, above an anchor.</summary>
    internal sealed class HudPopover : ModernSurface
    {
        private MirControl _anchor;
        private Action _after;
        private bool _catch;

        private readonly Action<bool> _onRelayout;

        public HudPopover()
        {
            Visible = false;
            PassThrough = true;
            _onRelayout = _ => Close();
            if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout += _onRelayout;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && ModernRoot.Instance != null) ModernRoot.Instance.Relayout -= _onRelayout;
            base.Dispose(disposing);
        }

        public bool IsOpen => Visible && !IsDisposed;
        public bool IsFor(MirControl anchor) => IsOpen && _anchor == anchor;
        public MirControl Card { get; private set; }

        /// <summary>
        /// Shows <paramref name="card"/> (device px size set) above <paramref name="anchor"/>: its bottom 6 HUD px over the anchor's
        /// top, its left edge at <paramref name="alignX"/> (or its right edge there when <paramref name="right"/>). With
        /// <paramref name="catchMouse"/> every press goes to the popover (outside the card = close); without, it is display only.
        /// </summary>
        public void Show(MirControl anchor, MirControl card, int alignX, bool right, bool catchMouse, Action after = null)
        {
            Close();
            ModernRoot root = ModernRoot.Instance;
            if (root == null || card == null) return;
            if (Parent != root.Menu) Parent = root.Menu;
            for (int i = Controls.Count - 1; i >= 0; i--) Controls[i].Dispose();
            Size = card.Size;
            card.Parent = this;
            card.Location = Point.Empty;
            Card = card;
            int x = right ? alignX - card.Size.Width : alignX;
            int y = anchor.DisplayLocation.Y - S(6) - card.Size.Height;
            x = Math.Max(S(4), Math.Min(Settings.ScreenWidth - card.Size.Width - S(4), x));
            y = Math.Max(S(4), y);
            Location = new Point(x, y);
            _anchor = anchor;
            _after = after;
            _catch = catchMouse;
            PassThrough = !catchMouse;
            Visible = true;
            BringToFront();
            TextureValid = false;
        }

        public void Close()
        {
            if (!Visible) return;
            Visible = false;
            Action after = _after;
            _anchor = null;
            _after = null;
            Card = null;
            for (int i = Controls.Count - 1; i >= 0; i--) Controls[i].Dispose();
            after?.Invoke();
        }

        /// <summary>A click list takes every press while open (an outside press closes it); a hover card takes none.</summary>
        public override bool IsMouseOver(Point p)
        {
            if (!Visible || IsDisposed) return false;
            if (_catch) return true;
            return false;
        }

        public override void OnMouseDown(MouseEventArgs e)
        {
            // reached only for a press outside the card (the card and its rows take their own presses); activating first keeps
            // the following click away from the control that was active before (the anchor would reopen the list)
            base.OnMouseDown(e);
            Close();
        }

        public override void OnKeyDown(KeyEventArgs e)
        {
            // only a click list (it catches input) takes Esc; a display-only hover card lets it through to the windows / menu
            if (Visible && _catch && e.KeyCode == Keys.Escape && ModernRoot.Instance?.Popups?.IsOpen != true)
            {
                Close();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }
    }

    /// <summary>The popover's card: the panel look of .hm-pop (pa→pb, frame, black ring). Children are its rows.</summary>
    internal sealed class PopCard : ModernControl
    {
        protected override void Paint(Graphics g)
        {
            float k = K, rad = ModernTheme.Radius * k;
            RectangleF outer = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            RectangleF inner = RectangleF.Inflate(outer, -1f, -1f);
            using (GraphicsPath ring = ModernSkin.RoundRect(outer, rad + 1)) ModernSkin.Stroke(g, ring, Color.Black);
            using GraphicsPath p = ModernSkin.RoundRect(inner, rad);
            ModernSkin.FillVertical(g, p, inner, new[] { ModernSkin.Opaque(ModernTheme.PanelA), ModernSkin.Opaque(ModernTheme.PanelB) });
            ModernSkin.Stroke(g, p, ModernTheme.Frame);
        }
    }

    /// <summary>A row of a popover list: text left, a value or key hint right; hover = accent tint, on = accent bar.</summary>
    internal sealed class PopRow : ModernControl
    {
        private readonly string _text, _value;
        private readonly bool _on, _used, _valueIsKey;

        public PopRow(string text, string value, bool on, bool used, bool valueIsKey, int widthHud, int heightHud)
        {
            _text = text ?? string.Empty;
            _value = value ?? string.Empty;
            _on = on;
            _used = used;
            _valueIsKey = valueIsKey;
            Size = new Size(S(widthHud), S(heightHud));
        }

        protected override void Paint(Graphics g)
        {
            float k = K;
            RectangleF r = new RectangleF(0, 0, Size.Width, Size.Height);
            if (Hovered || _on)
            {
                using GraphicsPath p = ModernSkin.RoundRect(RectangleF.Inflate(r, -0.5f, -0.5f), ModernTheme.Radius * 0.6f * k);
                // blended toward the card (opaque): a translucent fill would let the world show through the popover
                Color card = ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelA), ModernSkin.Opaque(ModernTheme.PanelB), 0.5f);
                using var b = new SolidBrush(ModernTheme.Mix(ModernTheme.Accent, card, Hovered ? 0.22f : 0.14f));
                g.FillPath(b, p);
            }
            if (_on)
                using (var b = new SolidBrush(ModernTheme.Accent)) g.FillRectangle(b, 0, 0, 2 * k, Size.Height);
            Font f = ModernSkin.Font(11);
            float pad = 6 * k;
            string text = _text;
            float valueW = _value.Length > 0 ? ModernSkin.Measure(_value, _valueIsKey ? ModernSkin.Font(9) : ModernSkin.Font(11, true)) + 4 * k : 0;
            ModernSkin.Text(g, text, f, ModernTheme.Text, new RectangleF(pad, 0, Size.Width - pad * 2 - valueW, Size.Height));
            if (_used)
            {
                // the bullet in the accent colour (drawn over the plain one)
                float w = ModernSkin.Measure(_text + " ", f);
                ModernSkin.Text(g, "•", f, ModernTheme.Accent, new RectangleF(pad + w, 0, 12 * k, Size.Height), StringAlignment.Near, StringAlignment.Center, false);
            }
            if (_value.Length > 0)
                ModernSkin.Text(g, _value, _valueIsKey ? ModernSkin.Font(9) : ModernSkin.Font(11, true), _valueIsKey ? ModernTheme.Dim : Color.White,
                    new RectangleF(pad, 0, Size.Width - pad * 2, Size.Height), StringAlignment.Far, StringAlignment.Center, false);
        }
    }

    // ------------------------------------------------------------------ plate stats

    internal enum PlateFormat { Pair, Plus, Num, Pct, PlusPct, Slash, Compact }

    /// <summary>A stat a plate can track (statPlates.ts PlateStat). Read returns two numbers; the text is built from them.</summary>
    internal sealed class PlateStat
    {
        public readonly string Key;
        public readonly ClientTextKeys Name, Short, Group;
        public readonly PlateFormat Format;
        public readonly Func<UserObject, (long A, long B)> Read;
        /// <summary>0 = normal, 1 = warn, 2 = full (weights, bag).</summary>
        public readonly Func<long, long, int> Level;
        /// <summary>Gold: a left click picks the gold up (MainDialog.GoldLabel); the list opens on a right click.</summary>
        public readonly bool HasAction;

        public PlateStat(string key, ClientTextKeys name, ClientTextKeys shortName, ClientTextKeys group, PlateFormat format,
            Func<UserObject, (long, long)> read, Func<long, long, int> level = null, bool action = false)
        {
            Key = key;
            Name = name;
            Short = shortName;
            Group = group;
            Format = format;
            Read = read;
            Level = level;
            HasAction = action;
        }

        public string Text(long a, long b)
        {
            switch (Format)
            {
                case PlateFormat.Pair: return a + "-" + b;
                case PlateFormat.Plus: return "+" + a;
                case PlateFormat.Pct: return a + "%";
                case PlateFormat.PlusPct: return "+" + a + "%";
                case PlateFormat.Slash: return a + "/" + b;
                case PlateFormat.Compact: return HudKit.FmtCompact(a);
                default: return a.ToString();
            }
        }

        public string ValueText(UserObject u)
        {
            if (u == null) return string.Empty;
            (long a, long b) = Read(u);
            return Text(a, b);
        }

        // ---- the table (statPlates.ts PLATE_STATS) ----

        private static long St(UserObject u, Stat s) => u.Stats != null ? u.Stats[s] : 0;
        private static (long, long) Pair(UserObject u, Stat min, Stat max) => (St(u, min), St(u, max));

        private static (long, long) BagUse(UserObject u)
        {
            UserItem[] inv = u.Inventory;
            if (inv == null) return (0, 0);
            int used = 0, belt = Math.Min(u.BeltIdx, inv.Length);
            for (int i = belt; i < inv.Length; i++) if (inv[i] != null) used++;
            return (used, Math.Max(0, inv.Length - belt));
        }

        private static int WeightLevel(long cur, long max) => max <= 0 ? 0 : cur >= max * 0.95 ? 2 : cur >= max * 0.8 ? 1 : 0;

        public static readonly PlateStat[] All =
        {
            new PlateStat("Bag", ClientTextKeys.ModernHudStatBag, ClientTextKeys.ModernHudStatBagShort, ClientTextKeys.ModernHudGroupInventory, PlateFormat.Slash,
                BagUse, (u, t) => t > 0 && u >= t ? 2 : 0),
            new PlateStat("Weight", ClientTextKeys.ModernHudStatWeight, ClientTextKeys.ModernHudStatWeightShort, ClientTextKeys.ModernHudGroupInventory, PlateFormat.Slash,
                u => (u.CurrentBagWeight, St(u, Stat.BagWeight)), WeightLevel),
            new PlateStat("Gold", ClientTextKeys.ModernHudStatGold, ClientTextKeys.ModernHudStatGoldShort, ClientTextKeys.ModernHudGroupInventory, PlateFormat.Compact,
                u => (GameScene.Gold, 0), null, true),
            new PlateStat("AC", ClientTextKeys.ModernHudStatAC, ClientTextKeys.ModernHudStatACShort, ClientTextKeys.ModernHudGroupDefence, PlateFormat.Pair, u => Pair(u, Stat.MinAC, Stat.MaxAC)),
            new PlateStat("MAC", ClientTextKeys.ModernHudStatMAC, ClientTextKeys.ModernHudStatMACShort, ClientTextKeys.ModernHudGroupDefence, PlateFormat.Pair, u => Pair(u, Stat.MinMAC, Stat.MaxMAC)),
            new PlateStat("DC", ClientTextKeys.ModernHudStatDC, ClientTextKeys.ModernHudStatDCShort, ClientTextKeys.ModernHudGroupDefence, PlateFormat.Pair, u => Pair(u, Stat.MinDC, Stat.MaxDC)),
            new PlateStat("MC", ClientTextKeys.ModernHudStatMC, ClientTextKeys.ModernHudStatMCShort, ClientTextKeys.ModernHudGroupDefence, PlateFormat.Pair, u => Pair(u, Stat.MinMC, Stat.MaxMC)),
            new PlateStat("SC", ClientTextKeys.ModernHudStatSC, ClientTextKeys.ModernHudStatSCShort, ClientTextKeys.ModernHudGroupDefence, PlateFormat.Pair, u => Pair(u, Stat.MinSC, Stat.MaxSC)),
            new PlateStat("Accuracy", ClientTextKeys.ModernHudStatAccuracy, ClientTextKeys.ModernHudStatAccuracyShort, ClientTextKeys.ModernHudGroupCombat, PlateFormat.Plus, u => (St(u, Stat.Accuracy), 0)),
            new PlateStat("Agility", ClientTextKeys.ModernHudStatAgility, ClientTextKeys.ModernHudStatAgilityShort, ClientTextKeys.ModernHudGroupCombat, PlateFormat.Plus, u => (St(u, Stat.Agility), 0)),
            new PlateStat("AttackSpeed", ClientTextKeys.ModernHudStatAttackSpeed, ClientTextKeys.ModernHudStatAttackSpeedShort, ClientTextKeys.ModernHudGroupCombat, PlateFormat.Num, u => (St(u, Stat.AttackSpeed), 0)),
            new PlateStat("Luck", ClientTextKeys.ModernHudStatLuck, ClientTextKeys.ModernHudStatLuckShort, ClientTextKeys.ModernHudGroupCombat, PlateFormat.Num, u => (St(u, Stat.Luck), 0)),
            new PlateStat("CriticalRate", ClientTextKeys.ModernHudStatCriticalRate, ClientTextKeys.ModernHudStatCriticalRateShort, ClientTextKeys.ModernHudGroupCombat, PlateFormat.Pct, u => (St(u, Stat.CriticalRate), 0)),
            new PlateStat("CriticalDamage", ClientTextKeys.ModernHudStatCriticalDamage, ClientTextKeys.ModernHudStatCriticalDamageShort, ClientTextKeys.ModernHudGroupCombat, PlateFormat.Num, u => (St(u, Stat.CriticalDamage), 0)),
            new PlateStat("MagicResist", ClientTextKeys.ModernHudStatMagicResist, ClientTextKeys.ModernHudStatMagicResistShort, ClientTextKeys.ModernHudGroupResist, PlateFormat.Plus, u => (St(u, Stat.MagicResist), 0)),
            new PlateStat("PoisonResist", ClientTextKeys.ModernHudStatPoisonResist, ClientTextKeys.ModernHudStatPoisonResistShort, ClientTextKeys.ModernHudGroupResist, PlateFormat.Plus, u => (St(u, Stat.PoisonResist), 0)),
            new PlateStat("HealthRecovery", ClientTextKeys.ModernHudStatHealthRecovery, ClientTextKeys.ModernHudStatHealthRecoveryShort, ClientTextKeys.ModernHudGroupResist, PlateFormat.Plus, u => (St(u, Stat.HealthRecovery), 0)),
            new PlateStat("SpellRecovery", ClientTextKeys.ModernHudStatSpellRecovery, ClientTextKeys.ModernHudStatSpellRecoveryShort, ClientTextKeys.ModernHudGroupResist, PlateFormat.Plus, u => (St(u, Stat.SpellRecovery), 0)),
            new PlateStat("PoisonRecovery", ClientTextKeys.ModernHudStatPoisonRecovery, ClientTextKeys.ModernHudStatPoisonRecoveryShort, ClientTextKeys.ModernHudGroupResist, PlateFormat.Plus, u => (St(u, Stat.PoisonRecovery), 0)),
            new PlateStat("Holy", ClientTextKeys.ModernHudStatHoly, ClientTextKeys.ModernHudStatHolyShort, ClientTextKeys.ModernHudGroupResist, PlateFormat.Num, u => (St(u, Stat.Holy), 0)),
            new PlateStat("Freezing", ClientTextKeys.ModernHudStatFreezing, ClientTextKeys.ModernHudStatFreezingShort, ClientTextKeys.ModernHudGroupResist, PlateFormat.Num, u => (St(u, Stat.Freezing), 0)),
            new PlateStat("PoisonAttack", ClientTextKeys.ModernHudStatPoisonAttack, ClientTextKeys.ModernHudStatPoisonAttackShort, ClientTextKeys.ModernHudGroupResist, PlateFormat.Num, u => (St(u, Stat.PoisonAttack), 0)),
            new PlateStat("WearWeight", ClientTextKeys.ModernHudStatWearWeight, ClientTextKeys.ModernHudStatWearWeightShort, ClientTextKeys.ModernHudGroupWeight, PlateFormat.Slash,
                u => (u.CurrentWearWeight, St(u, Stat.WearWeight)), WeightLevel),
            new PlateStat("HandWeight", ClientTextKeys.ModernHudStatHandWeight, ClientTextKeys.ModernHudStatHandWeightShort, ClientTextKeys.ModernHudGroupWeight, PlateFormat.Slash,
                u => (u.CurrentHandWeight, St(u, Stat.HandWeight)), WeightLevel),
            new PlateStat("ExpRate", ClientTextKeys.ModernHudStatExpRate, ClientTextKeys.ModernHudStatExpRateShort, ClientTextKeys.ModernHudGroupRates, PlateFormat.PlusPct, u => (St(u, Stat.ExpRatePercent), 0)),
            new PlateStat("DropRate", ClientTextKeys.ModernHudStatDropRate, ClientTextKeys.ModernHudStatDropRateShort, ClientTextKeys.ModernHudGroupRates, PlateFormat.PlusPct, u => (St(u, Stat.ItemDropRatePercent), 0)),
            new PlateStat("GoldRate", ClientTextKeys.ModernHudStatGoldRate, ClientTextKeys.ModernHudStatGoldRateShort, ClientTextKeys.ModernHudGroupRates, PlateFormat.PlusPct, u => (St(u, Stat.GoldDropRatePercent), 0)),
        };

        public static PlateStat ByKey(string key)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i].Key == key) return All[i];
            return null;
        }
    }

    /// <summary>
    /// The plate picks (statPlates.ts parsePlates / formatPlates). The web saves them in its UiPlates string; the desktop's
    /// Settings.UiPlates is the plates on / off switch, so the picks are saved separately in Settings.UiPlateStats.
    /// </summary>
    internal static class HudPlateStore
    {
        public static readonly string[] Defaults = { "AC", "MAC", "DC", "Bag", "Weight", "Gold" };

        /// <summary>"AC,MAC,DC,Bag,Weight,Gold": Settings.UiPlateStats ([Interface] UiPlateStats; empty = the defaults).</summary>
        public static string Saved
        {
            get => Settings.UiPlateStats;
            set => Settings.UiPlateStats = value ?? string.Empty;
        }

        /// <summary>Six known, distinct keys; the defaults otherwise.</summary>
        public static string[] Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return (string[])Defaults.Clone();
            string[] keys = s.Split(',');
            if (keys.Length != Defaults.Length) return (string[])Defaults.Clone();
            var seen = new HashSet<string>();
            for (int i = 0; i < keys.Length; i++)
            {
                keys[i] = keys[i].Trim();
                if (PlateStat.ByKey(keys[i]) == null || !seen.Add(keys[i])) return (string[])Defaults.Clone();
            }
            return keys;
        }

        public static string Format(string[] keys) => string.Join(",", keys);
    }

    // ------------------------------------------------------------------ plates

    /// <summary>One plate: the short name left, the value right, an angled hexagon (.hm-stat).</summary>
    internal sealed class PlateControl : ModernControl
    {
        private string _label = string.Empty, _value = string.Empty;
        private int _level;
        private bool _picking;
        public readonly PlateGlow Glow;

        public PlateControl()
        {
            Glow = new PlateGlow { Parent = this, Visible = false };
        }

        public void SetLabel(string label)
        {
            if (_label == label) return;
            _label = label;
            Invalidate();
        }

        public void SetValue(string value)
        {
            if (_value == value) return;
            _value = value;
            Invalidate();
        }

        public int Level
        {
            get => _level;
            set
            {
                if (_level == value) return;
                _level = value;
                Glow.Visible = value == 2;
                Invalidate();
            }
        }

        public bool Picking
        {
            get => _picking;
            set
            {
                if (_picking == value) return;
                _picking = value;
                Invalidate();
            }
        }

        internal static GraphicsPath Shape(float w, float h, float k)
        {
            float c = 6 * k;
            return HudKit.Polygon(new PointF(c, 0), new PointF(w - c, 0), new PointF(w, h / 2f), new PointF(w - c, h), new PointF(c, h), new PointF(0, h / 2f));
        }

        protected override void Paint(Graphics g)
        {
            float k = K, w = Size.Width, h = Size.Height;
            using GraphicsPath p = Shape(w, h, k);
            Color top = Hovered || _picking ? HudKit.Mix(ModernTheme.PanelA, ModernTheme.Accent, 0.7f) : ModernTheme.PanelA;
            ModernSkin.FillVertical(g, p, new RectangleF(0, 0, w, h), new[] { HudKit.Sq(top), HudKit.Sq(ModernTheme.PanelB) });
            using (var pen = new Pen(ModernTheme.Alpha(ModernTheme.FrameLight, 0.25f), 1f)) g.DrawLine(pen, 6 * k, 1, w - 6 * k, 1);
            ModernSkin.Stroke(g, p, ModernTheme.Frame);
            Color labelC = _level == 1 ? ModernTheme.Accent : _level == 2 ? Color.White : ModernTheme.Dim;
            Color valueC = _level == 1 ? ModernTheme.Accent : _level == 2 ? Color.White : ModernTheme.Text;
            float pad = 8 * k;
            Font lf = ModernSkin.Font(11, true);
            float lw = ModernSkin.Measure(_label, lf);
            ModernSkin.Text(g, _label, lf, labelC, new RectangleF(pad, 0, lw + 2, h), StringAlignment.Near, StringAlignment.Center, false);
            ModernSkin.Text(g, _value, ModernSkin.Font(11, true), valueC, new RectangleF(pad + lw + 6 * k, 0, w - pad * 2 - lw - 6 * k, h), StringAlignment.Far);
        }
    }

    /// <summary>The "full" pulse over a plate (an accent overlay whose Opacity the HUD steps).</summary>
    internal sealed class PlateGlow : ModernControl
    {
        public PlateGlow()
        {
            NotControl = true;
        }

        protected override void Paint(Graphics g)
        {
            using GraphicsPath p = PlateControl.Shape(Size.Width, Size.Height, K);
            using var b = new SolidBrush(ModernTheme.Alpha(ModernTheme.Accent, 0.35f));
            g.FillPath(b, p);
        }
    }

    /// <summary>The six plates (statPlates.ts StatPlates): three per wing, fed ~4x a second by the HUD.</summary>
    internal sealed class StatPlates
    {
        public readonly PlateControl[] Plates = new PlateControl[6];
        private readonly HudPopover _pop;
        private readonly Func<int, int> _alignX;
        private string[] _picks;
        private readonly long[] _a = new long[6], _b = new long[6];
        private readonly string[] _shownKey = new string[6];
        private int _pulse = -1;

        /// <param name="alignX">Plate slot → the screen x the list aligns to (left wing: its left edge, right wing: its right edge).</param>
        public StatPlates(MirControl parent, HudPopover pop, Func<int, int> alignX)
        {
            _pop = pop;
            _alignX = alignX;
            _picks = HudPlateStore.Parse(HudPlateStore.Saved);
            for (int slot = 0; slot < Plates.Length; slot++)
            {
                int s = slot;
                var p = new PlateControl { Parent = parent };
                p.Click += (o, e) => OnClick(s, e as MouseEventArgs);
                Plates[slot] = p;
                _a[slot] = long.MinValue;
            }
            for (int i = 0; i < Plates.Length; i++) Label(i);
        }

        public string[] Picks => _picks;

        /// <summary>Places a plate (device px) and sizes its glow.</summary>
        public void Place(int slot, Rectangle r)
        {
            PlateControl p = Plates[slot];
            p.Location = r.Location;
            p.Size = r.Size;
            p.Glow.Size = r.Size;
        }

        public void SetVisible(bool visible)
        {
            for (int i = 0; i < Plates.Length; i++) Plates[i].Visible = visible;
        }

        /// <summary>Writes the values that changed (numbers compared first; text built only on a change).</summary>
        public void Update()
        {
            UserObject u = GameScene.User;
            if (u == null) return;
            for (int slot = 0; slot < Plates.Length; slot++)
            {
                PlateStat s = PlateStat.ByKey(_picks[slot]);
                if (s == null) continue;
                (long a, long b) = s.Read(u);
                if (a != _a[slot] || b != _b[slot])
                {
                    _a[slot] = a;
                    _b[slot] = b;
                    Plates[slot].SetValue(s.Text(a, b));
                }
                Plates[slot].Level = s.Level != null ? s.Level(a, b) : 0;
            }
        }

        /// <summary>The "full" pulse (1 s, hudm-halo): opacity stepped 8 times per second, only while a plate is full.</summary>
        public void Pulse()
        {
            bool any = false;
            for (int i = 0; i < Plates.Length; i++) if (Plates[i].Level == 2 && Plates[i].Visible) any = true;
            if (!any)
            {
                _pulse = -1;
                return;
            }
            int step = (int)(CMain.Time % 1000 / 125);
            if (step == _pulse) return;
            _pulse = step;
            float o = step < 4 ? step / 4f : (8 - step) / 4f;
            for (int i = 0; i < Plates.Length; i++)
                if (Plates[i].Level == 2) Plates[i].Glow.Opacity = o;
        }

        /// <summary>The picks were changed elsewhere (Options): re-read the store.</summary>
        public void Reload()
        {
            _picks = HudPlateStore.Parse(HudPlateStore.Saved);
            for (int i = 0; i < Plates.Length; i++)
            {
                _a[i] = long.MinValue;
                Label(i);
            }
            Update();
        }

        private void Label(int slot)
        {
            PlateStat s = PlateStat.ByKey(_picks[slot]);
            if (s == null || _shownKey[slot] == s.Key) return;
            _shownKey[slot] = s.Key;
            PlateControl p = Plates[slot];
            p.SetLabel(HudKit.T(s.Short));
            string name = HudKit.T(s.Name);
            p.Hint = s.HasAction
                ? HudKit.T(ClientTextKeys.ModernHudPlateActionHint, name, HudKit.T(ClientTextKeys.ModernHudPlateDropGold))
                : HudKit.T(ClientTextKeys.ModernHudPlateHint, name);
        }

        private void OnClick(int slot, MouseEventArgs e)
        {
            PlateStat s = PlateStat.ByKey(_picks[slot]);
            if (s == null) return;
            bool right = e != null && e.Button == MouseButtons.Right;
            if (s.HasAction && !right)
            {
                _pop.Close();
                // MainDialog.GoldLabel: pick the gold up (never while an item is picked); a map click drops it
                if (ItemMoves.Selected == null)
                    GameScene.PickedUpGold = !GameScene.PickedUpGold && GameScene.Gold > 0;
                return;
            }
            if (!s.HasAction && right) return;
            Toggle(slot);
        }

        /// <summary>Opens / closes the stat list over a plate.</summary>
        internal void Toggle(int slot)
        {
            PlateControl p = Plates[slot];
            if (_pop.IsFor(p))
            {
                _pop.Close();
                return;
            }
            const int width = 300, pad = 6, rowH = 20, colGap = 4;
            int colW = (width - pad * 2 - colGap) / 2;
            UserObject u = GameScene.User;
            var card = new PopCard();
            int y = pad;
            var head = new ModernLabel { Parent = card, Location = new Point(ModernRoot.S(pad + 4), ModernRoot.S(y)), Size = new Size(ModernRoot.S(150), ModernRoot.S(16)),
                Text = HudKit.T(ClientTextKeys.ModernHudPlatePickTitle).ToUpperInvariant(), FontSize = 10, Bold = true, Colour = ModernTheme.Accent };
            var sub = new ModernLabel { Parent = card, Location = new Point(ModernRoot.S(width - pad - 4 - 120), ModernRoot.S(y)), Size = new Size(ModernRoot.S(120), ModernRoot.S(16)),
                Text = HudKit.T(ClientTextKeys.ModernHudPlatePickSub), FontSize = 10, Colour = ModernTheme.Dim, Align = StringAlignment.Far };
            y += 20;
            ClientTextKeys group = (ClientTextKeys)(-1);
            int col = 0;
            for (int i = 0; i < PlateStat.All.Length; i++)
            {
                PlateStat s = PlateStat.All[i];
                if (s.Group != group)
                {
                    if (col != 0) y += rowH + 1;
                    col = 0;
                    group = s.Group;
                    new ModernLabel { Parent = card, Location = new Point(ModernRoot.S(pad + 4), ModernRoot.S(y + 3)), Size = new Size(ModernRoot.S(width - pad * 2), ModernRoot.S(13)),
                        Text = HudKit.T(s.Group).ToUpperInvariant(), FontSize = 9, Bold = true, Colour = ModernTheme.Dim };
                    y += 18;
                }
                int at = Array.IndexOf(_picks, s.Key);
                string name = HudKit.T(s.Name);
                var row = new PopRow(name, s.ValueText(u), at == slot, at >= 0 && at != slot, false, colW, rowH)
                {
                    Parent = card,
                    Location = new Point(ModernRoot.S(pad + col * (colW + colGap)), ModernRoot.S(y)),
                    Hint = at == slot ? HudKit.T(ClientTextKeys.ModernHudPlateOn, name) : at >= 0 ? HudKit.T(ClientTextKeys.ModernHudPlateSwap, name) : name,
                };
                string key = s.Key;
                row.Click += (o, e) =>
                {
                    _pop.Close();
                    Pick(slot, key);
                };
                col++;
                if (col == 2)
                {
                    col = 0;
                    y += rowH + 1;
                }
            }
            if (col != 0) y += rowH + 1;
            new ModernLabel { Parent = card, Location = new Point(ModernRoot.S(pad + 4), ModernRoot.S(y + 3)), Size = new Size(ModernRoot.S(width - pad * 2), ModernRoot.S(14)),
                Text = HudKit.T(ClientTextKeys.ModernHudPlatePickFoot), FontSize = 10, Colour = ModernTheme.Dim };
            y += 20;
            card.Size = new Size(ModernRoot.S(width), ModernRoot.S(y + pad - 2));
            p.Picking = true;
            _pop.Show(p, card, _alignX(slot), slot >= 3, true, () => { if (!p.IsDisposed) p.Picking = false; });
        }

        /// <summary>Plate <paramref name="slot"/> tracks <paramref name="key"/>; a plate already on it takes this plate's old stat.</summary>
        internal void Pick(int slot, string key)
        {
            int other = Array.IndexOf(_picks, key);
            if (other == slot) return;
            if (other >= 0)
            {
                _picks[other] = _picks[slot];
                _a[other] = long.MinValue;
                Label(other);
            }
            _picks[slot] = key;
            _a[slot] = long.MinValue;
            Label(slot);
            Update();
            HudPlateStore.Saved = HudPlateStore.Format(_picks);
        }
    }
}

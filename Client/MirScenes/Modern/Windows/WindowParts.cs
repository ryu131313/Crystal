// Shared pieces of the phase 2 core windows (WebClient/src/ui/modern/core/dom.ts + format.ts helpers and the small .card /
// .lbl / .icell decorations the window CSS repeats), in desktop form: text and number helpers, painted boxes, an icon view
// over ModernIcons.Lib, the item cell decoration (empty-slot glyph, search highlight / dim, unlock glow) and a cheap grid
// signature poll for item grids whose packet handlers change items without an ItemMoves.SlotChanged.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirScenes.Modern.Core;
using DxMatrix = SlimDX.Matrix;

namespace Client.MirScenes.Modern.Windows
{
    /// <summary>Text, number and layout helpers (format.ts fmt / fmtCompact / range / signed; dom.ts h for labels).</summary>
    internal static class W
    {
        public static string T(ClientTextKeys key) => GameLanguage.ClientTextMap.GetLocalization(key);
        public static string T(ClientTextKeys key, params object[] args) => GameLanguage.ClientTextMap.GetLocalization(key, args);

        public static int S(float hud) => ModernRoot.S(hud);

        /// <summary>fmt: a whole number with thousands separators.</summary>
        public static string N(long v) => v.ToString("#,##0");

        /// <summary>fmt1: one decimal at most.</summary>
        public static string N1(double v) => v.ToString("0.#");

        /// <summary>fmtCompact: 950, 1.5k, 2.3m, 4.1b.</summary>
        public static string Compact(long v)
        {
            long a = Math.Abs(v);
            if (a < 10000) return N(v);
            double d = a < 1000000 ? 1e3 : a < 1000000000 ? 1e6 : 1e9;
            string s = a < 1000000 ? "k" : a < 1000000000 ? "m" : "b";
            double x = v / d;
            return (Math.Abs(x) < 100 ? N1(x) : N((long)Math.Round(x))) + s;
        }

        public static string Range(int min, int max) => min == max ? N(min) : N(min) + "-" + N(max);
        public static string Signed(int v) => v > 0 ? "+" + N(v) : N(v);

        /// <summary>fmtDuration: 2d 3h, 1h 05m, 4m 09s, 12s.</summary>
        public static string Duration(long secs)
        {
            long s = Math.Max(0, secs);
            long d = s / 86400, h = s / 3600 % 24, m = s / 60 % 60, ss = s % 60;
            if (d > 0) return d + "d " + h + "h";
            if (h > 0) return h + "h " + m.ToString("00") + "m";
            if (m > 0) return m + "m " + ss.ToString("00") + "s";
            return ss + "s";
        }

        /// <summary>The live key binding as text ("Ctrl + F1"); "" when unbound.</summary>
        public static string Key(KeybindOptions option) => CMain.InputKeys?.GetKey(option) ?? string.Empty;

        /// <summary>The window page colour the boxes blend toward (opaque: translucent pixels in a surface show the world).</summary>
        public static Color Page => ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, 0.5f);

        /// <summary>color-mix(in srgb, c a, page): an opaque tint of the page.</summary>
        public static Color Over(Color c, float a) => ModernTheme.Mix(ModernSkin.Opaque(c), Page, a);

        public static ModernLabel Label(MirControl parent, string text, int x, int y, int w, int h, float size = 12, bool bold = false,
            Color? colour = null, StringAlignment align = StringAlignment.Near, bool wrap = false)
        {
            return new ModernLabel
            {
                Parent = parent,
                Location = new Point(S(x), S(y)),
                Size = new Size(Math.Max(1, S(w)), Math.Max(1, S(h))),
                Text = text ?? string.Empty,
                FontSize = size,
                Bold = bold,
                Colour = colour ?? Color.Empty,
                Align = align,
                Wrap = wrap,
            };
        }

        /// <summary>The .lbl caption: small, bold, upper case, dim.</summary>
        public static ModernLabel Caption(MirControl parent, string text, int x, int y, int w, Color? colour = null) =>
            Label(parent, (text ?? string.Empty).ToUpperInvariant(), x, y, w, 14, 10, true, colour ?? ModernTheme.Dim);

        public static T At<T>(T c, MirControl parent, int x, int y) where T : MirControl
        {
            c.Parent = parent;
            c.Location = new Point(S(x), S(y));
            return c;
        }

        /// <summary>Right edge of a control in HUD px (for placing the next one).</summary>
        public static int Right(MirControl c) => (int)Math.Round((c.Location.X + c.Size.Width) / ModernRoot.K);

        /// <summary>Draws a ModernIcons stroke icon tinted, scaled into a device rectangle (render time only).</summary>
        public static void DrawIcon(int index, Rectangle r, Color colour)
        {
            if (index < 0 || Libraries.ModernIcons == null || r.Width <= 0) return;
            bool big = r.Width > 30;
            int idx = big ? index + 1 : index, native = big ? ModernIcons.IconSize * 2 : ModernIcons.IconSize;
            float s = r.Width / (float)native;
            if (Math.Abs(s - 1f) < 0.01f)
            {
                Libraries.ModernIcons.Draw(idx, r.Location, colour, false);
                return;
            }
            DXManager.SetSpriteTransform(DxMatrix.Scaling(s, s, 0));
            try
            {
                Libraries.ModernIcons.Draw(idx, new Point((int)Math.Round(r.X / s), (int)Math.Round(r.Y / s)), colour, false);
            }
            finally
            {
                DXManager.SetSpriteTransform(DxMatrix.Identity);
            }
        }

        /// <summary>The .card box: a faint frame-light fill and a soft frame border.</summary>
        public static void Card(Graphics g, Size size, Color? border = null, float fill = 0.05f, bool dashed = false)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, size.Width - 1f, size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
            using (var b = new SolidBrush(Over(ModernTheme.FrameLight, fill)))
                g.FillPath(b, p);
            using var pen = new Pen(border ?? Over(ModernTheme.Frame, 0.55f), 1f);
            if (dashed) pen.DashStyle = DashStyle.Dash;
            g.DrawPath(pen, p);
        }

        /// <summary>A row background: odd rows a faint tint, hover / selected in the accent (the list .row look).</summary>
        public static void Row(Graphics g, Size size, bool odd, bool hot, bool selected, bool leftBar = true)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, size.Width - 1f, size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
            Color fill = selected ? Over(ModernTheme.Accent, 0.14f) : hot ? Over(ModernTheme.Accent, 0.08f) : odd ? Over(ModernTheme.FrameLight, 0.05f) : Color.Empty;
            if (!fill.IsEmpty)
                using (var b = new SolidBrush(fill))
                    g.FillPath(b, p);
            if (selected || hot)
                ModernSkin.Stroke(g, p, selected ? ModernTheme.Accent : Over(ModernTheme.Accent, 0.5f));
            if (selected && leftBar)
                using (var b = new SolidBrush(ModernTheme.Accent))
                    g.FillRectangle(b, 1, 2 * k, 3 * k, size.Height - 4 * k);
        }

        /// <summary>A thin horizontal divider (border-bottom: color-mix(fr 60%)).</summary>
        public static PaintBox Divider(MirControl parent, int x, int y, int w) =>
            new PaintBox((g, b) => { using var pen = new Pen(Over(ModernTheme.Frame, 0.6f), 1f); g.DrawLine(pen, 0, 0.5f, b.Size.Width, 0.5f); })
            { Parent = parent, Location = new Point(S(x), S(y)), Size = new Size(Math.Max(1, S(w)), 1), NotControl = true };

        /// <summary>A thin vertical divider.</summary>
        public static PaintBox VDivider(MirControl parent, int x, int y, int h) =>
            new PaintBox((g, b) => { using var pen = new Pen(Over(ModernTheme.Frame, 0.6f), 1f); g.DrawLine(pen, 0.5f, 0, 0.5f, b.Size.Height); })
            { Parent = parent, Location = new Point(S(x), S(y)), Size = new Size(1, Math.Max(1, S(h))), NotControl = true };

        /// <summary>Keycap text of a Keys name (keybinds.ts cap: D1 → 1, NumPad1 → Num 1, Oem names → their glyph).</summary>
        public static string Cap(string key)
        {
            switch (key)
            {
                case "Escape": return T(ClientTextKeys.ModernWinCapEsc);
                case "PrintScreen": return T(ClientTextKeys.ModernWinCapPrtSc);
                case "Scroll": return T(ClientTextKeys.ModernWinCapScrLk);
                case "Oem8": return "`";
                case "OemMinus": return "-";
                case "Oemplus": return "=";
                case "Back": return T(ClientTextKeys.ModernWinCapBackspace);
                case "OemOpenBrackets": return "[";
                case "Oem6": return "]";
                case "Oem5": return "\\";
                case "Capital": return T(ClientTextKeys.ModernWinCapCaps);
                case "Oem1": return ";";
                case "Oem7": return "'";
                case "Return": return T(ClientTextKeys.ModernWinCapEnter);
                case "ShiftKey": return "Shift";
                case "Oemcomma": return ",";
                case "OemPeriod": return ".";
                case "OemQuestion": return "/";
                case "ControlKey": return "Ctrl";
                case "LWin":
                case "RWin": return T(ClientTextKeys.ModernWinCapWin);
                case "Menu": return "Alt";
                case "Apps": return T(ClientTextKeys.ModernWinCapMenu);
                case "Insert": return T(ClientTextKeys.ModernWinCapIns);
                case "PageUp": return T(ClientTextKeys.ModernWinCapPgUp);
                case "Delete": return T(ClientTextKeys.ModernWinCapDel);
                case "Next": return T(ClientTextKeys.ModernWinCapPgDn);
                case "Up": return "↑";
                case "Down": return "↓";
                case "Left": return "←";
                case "Right": return "→";
                case "NumLock": return T(ClientTextKeys.ModernWinCapNumLock);
                case "Divide": return "/";
                case "Multiply": return "*";
                case "Subtract": return "-";
                case "Add": return "+";
                case "Decimal": return ".";
                case "OemBackslash": return "\\";
            }
            if (key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1])) return key.Substring(1);
            if (key.StartsWith("NumPad", StringComparison.Ordinal)) return T(ClientTextKeys.ModernWinCapNumPad, key.Substring(6));
            return key;
        }

        /// <summary>A key label ("Ctrl + D1") with keycap names ("Ctrl + 1").</summary>
        public static string CapLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return string.Empty;
            string[] parts = label.Split(" + ");
            for (int i = 0; i < parts.Length; i++) parts[i] = Cap(parts[i]);
            return string.Join(" + ", parts);
        }
    }

    /// <summary>A control painted by a delegate (rows, cards, chips, bars): repaints on hover / press and on <see cref="ModernControl.Invalidate"/>.</summary>
    internal sealed class PaintBox : ModernControl
    {
        private readonly Action<Graphics, PaintBox> _painter;
        private bool _selected;

        public PaintBox(Action<Graphics, PaintBox> painter)
        {
            _painter = painter;
        }

        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                Invalidate();
            }
        }

        /// <summary>A free value for the owner (row index, key name, ...).</summary>
        public int Index;
        public string Tag2 = string.Empty;

        /// <summary>Draws at render time (library pictures) after the painted texture.</summary>
        public Action<PaintBox> AfterPaint;

        protected override void Paint(Graphics g) => _painter?.Invoke(g, this);

        protected internal override void DrawControl()
        {
            base.DrawControl();
            AfterPaint?.Invoke(this);
        }

        public override void OnMouseDoubleClick(MouseEventArgs e) => OnMouseClick(e);
    }

    /// <summary>A ModernIcons stroke icon (core/icons.ts svg) at a size, tinted.</summary>
    internal sealed class IconView : ModernControl
    {
        private int _index;
        private Color _colour;

        public IconView(int index, int sizeHud, Color colour)
        {
            _index = index;
            _colour = colour;
            Size = new Size(S(sizeHud), S(sizeHud));
            NotControl = true;
        }

        public void Set(int index, Color colour)
        {
            if (index == _index && colour == _colour) return;
            _index = index;
            _colour = colour;
            Redraw();
        }

        protected override void Paint(Graphics g) { }

        protected internal override void DrawControl()
        {
            base.DrawControl();
            W.DrawIcon(_index, new Rectangle(DisplayLocation, Size), _colour);
        }
    }

    /// <summary>
    /// Drawn over a ModernItemCell (its child, so it paints after the item picture): the empty-slot glyph or name, the search
    /// highlight (.match: accent border) or dim veil (.nomatch: opacity .22), and the unlock wave's gold glow (0-1). Repaints only
    /// when one of these changes.
    /// </summary>
    internal sealed class CellDecor : ModernControl
    {
        private readonly ModernItemCell _cell;
        private int _icon = -1;
        private string _text = string.Empty;
        private bool _empty;
        private int _mode; // 0 none, 1 match, 2 dim
        private int _glow; // 0-16

        public CellDecor(ModernItemCell cell, int emptyIcon = -1, string emptyText = null)
        {
            _cell = cell;
            _icon = emptyIcon;
            _text = emptyText ?? string.Empty;
            NotControl = true;
            Parent = cell;
            Size = cell.Size;
            _empty = cell.Item == null;
        }

        /// <summary>Re-reads whether the cell is empty (call after the grid changed).</summary>
        public void Sync()
        {
            bool empty = _cell.Item == null;
            if (empty == _empty) return;
            _empty = empty;
            Invalidate();
        }

        public void SetMode(int mode)
        {
            if (mode == _mode) return;
            _mode = mode;
            Invalidate();
        }

        public void SetGlow(int level)
        {
            level = Math.Clamp(level, 0, 16);
            if (level == _glow) return;
            _glow = level;
            Invalidate();
        }

        protected override void Paint(Graphics g)
        {
            float k = K, rad = ModernTheme.Radius * k;
            RectangleF r = new RectangleF(1.5f, 1.5f, Size.Width - 3f, Size.Height - 3f);
            if (_empty && _text.Length > 0)
                ModernSkin.Text(g, _text.ToUpperInvariant(), ModernSkin.Font(_text.Length > 6 ? 6.5f : 7.5f, true), ModernSkin.Dim(ModernTheme.Dim, 0.6f), new RectangleF(-2, 0, Size.Width + 4, Size.Height), StringAlignment.Center);
            if (_mode == 2)
            {
                using GraphicsPath p = ModernSkin.RoundRect(r, rad);
                using var b = new SolidBrush(Color.FromArgb(200, W.Page));
                g.FillPath(b, p);
            }
            else if (_mode == 1)
            {
                using GraphicsPath p = ModernSkin.RoundRect(RectangleF.Inflate(r, 0.5f, 0.5f), rad);
                ModernSkin.Stroke(g, p, ModernTheme.Accent, Math.Max(1f, 1.5f * k));
            }
            if (_glow > 0)
            {
                float a = _glow / 16f;
                using GraphicsPath p = ModernSkin.RoundRect(r, rad);
                ModernSkin.Stroke(g, p, ModernTheme.Alpha(ModernTheme.Gold, a), Math.Max(2f, 2f * k));
                using var b = new SolidBrush(ModernTheme.Alpha(ModernTheme.Gold, 0.25f * a));
                g.FillPath(b, p);
            }
        }

        protected internal override void DrawControl()
        {
            base.DrawControl();
            if (_empty && _icon >= 0)
            {
                int s = (int)(Size.Width * 0.45f);
                W.DrawIcon(_icon, new Rectangle(DisplayLocation.X + (Size.Width - s) / 2, DisplayLocation.Y + (Size.Height - s) / 2, s, s), ModernSkin.Dim(ModernTheme.Dim, 0.55f));
            }
        }
    }

    /// <summary>
    /// A cheap signature of an item grid (ids, counts, durability, locks), polled at most every <see cref="IntervalMs"/>: some
    /// packet handlers (S.DeleteItem, S.UserSlotsRefresh, S.UserStorage, gains) change the arrays without
    /// ItemMoves.SlotChanged, so the windows re-read their cells when the signature moves. Allocation-free.
    /// </summary>
    internal sealed class GridWatch
    {
        public const int IntervalMs = 250;
        private long _sig = long.MinValue, _next;

        /// <summary>True when the grid changed since the last call (gated to the interval unless <paramref name="force"/>).</summary>
        public bool Poll(MirGridType grid, UserItem[] items, int from = 0, int to = int.MaxValue, bool force = false)
        {
            if (!force && CMain.Time < _next) return false;
            _next = CMain.Time + IntervalMs;
            long h = items == null ? -1 : items.Length;
            if (items != null)
            {
                int end = Math.Min(to, items.Length);
                for (int i = Math.Max(0, from); i < end; i++)
                {
                    UserItem it = items[i];
                    long v = it == null ? 0 : (long)it.UniqueID * 31 + it.Count * 7 + it.CurrentDura + (it.Info == null ? 0 : it.Info.Index * 3);
                    if (ItemMoves.IsLocked(grid, i)) v += 1000003;
                    h = h * 1099511628211L + v + i;
                }
            }
            if (h == _sig) return false;
            _sig = h;
            return true;
        }

        public void Reset() => _sig = long.MinValue;
    }

    /// <summary>The shared Sort / Stack cooldown (inventory.ts sortItems: 2 s across the bag and the storage).</summary>
    internal static class SortGate
    {
        public const int CooldownMs = 2000;
        private static long _until;

        public static long Until => _until;

        /// <summary>C.SortItems through the pacer; false = refused (a cell of the grid waits for the server, or the cooldown).</summary>
        public static bool TrySort(MirGridType grid, UserItem[] items, bool stack)
        {
            if (CMain.Time < _until || items == null) return false;
            for (int i = 0; i < items.Length; i++)
                if (items[i] != null && ItemMoves.IsLocked(grid, i)) return false;
            ModernUi ui = GameScene.Ui as ModernUi;
            if (ui == null) return false;
            _until = CMain.Time + CooldownMs;
            ui.Pacer.Send(new ClientPackets.SortItems { Grid = grid, Mode = (byte)(stack ? 1 : 0) });
            return true;
        }

        /// <summary>Why Sort is off now ("" = it is on).</summary>
        public static string Why(MirGridType grid, UserItem[] items)
        {
            if (CMain.Time < _until) return W.T(ClientTextKeys.ModernWinSortWait);
            if (items != null)
                for (int i = 0; i < items.Length; i++)
                    if (items[i] != null && ItemMoves.IsLocked(grid, i)) return W.T(ClientTextKeys.ModernWinWaitingServer);
            return string.Empty;
        }
    }
}

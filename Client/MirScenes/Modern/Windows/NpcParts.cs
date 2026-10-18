// Shared pieces of the phase 3 NPC windows (npc.ts, npcBox.ts, merchant.ts, craft.ts): the format.ts npcName / pretty helpers,
// the item price helpers of game/items.ts (UserItem.Price / RepairPrice x the NPC rate, truncated like the server's uint casts),
// an item cell for items that live in no ItemMoves grid (goods, cart lines, recipe parts, picks: core/itemCells.ts custom grids
// "x:…", display only, with a click event) and the rich text view the NPC page is drawn with (NPCDialog.NewText's labels:
// links, colours, web links, info links, word wrap, the typewriter).
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text.RegularExpressions;
using Client.MirGraphics;
using Client.MirScenes.Modern.Core;
using Client.MirSounds;

namespace Client.MirScenes.Modern.Windows
{
    /// <summary>format.ts npcName / pretty and the item helpers the NPC windows share.</summary>
    internal static class NpcFmt
    {
        private static readonly Regex Camel = new Regex("([a-z])([A-Z])", RegexOptions.Compiled);
        private static readonly Regex Paren = new Regex(@"([A-Za-z])\(", RegexOptions.Compiled);
        private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>pretty: "BichonProvince" → "Bichon Province", underscores to spaces.</summary>
        public static string Pretty(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            string s = name.Replace('_', ' ');
            s = Camel.Replace(s, "$1 $2");
            s = Paren.Replace(s, "$1 (");
            return Spaces.Replace(s, " ").Trim();
        }

        /// <summary>npcName: "BichonProvince_Merchant_Herbalist" → ("Herbalist", "Bichon Province · Merchant").</summary>
        public static (string Name, string Place) Split(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return (string.Empty, string.Empty);
            var parts = new List<string>();
            foreach (string p in raw.Split('_')) if (p.Length > 0) parts.Add(p);
            if (parts.Count == 0) return (Pretty(raw), string.Empty);
            string name = Pretty(parts[^1]);
            parts.RemoveAt(parts.Count - 1);
            for (int i = 0; i < parts.Count; i++) parts[i] = Pretty(parts[i]);
            return (name, string.Join(" · ", parts));
        }

        public static string ItemName(UserItem item) =>
            item?.Info == null ? (item == null ? string.Empty : "#" + item.ItemIndex) : (string.IsNullOrEmpty(item.Info.FriendlyName) ? item.Info.Name : item.Info.FriendlyName);

        public static bool IsStack(ItemInfo info) => info != null && info.StackSize > 1;

        /// <summary>Color.FromName (an unknown name draws transparent on the desktop, NPCDialog.NewColour).</summary>
        public static Color Named(string name, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(name)) return fallback;
            Color c = Color.FromName(name.Trim());
            return c.IsKnownColor || c.A > 0 ? c : Color.Transparent;
        }

        /// <summary>(uint)(goods.Price() x rate) with goods.Count = count (the server's BuyItem price).</summary>
        public static uint BuyPrice(UserItem item, ushort count, float rate)
        {
            if (item?.Info == null) return 0;
            ushort old = item.Count;
            item.Count = count;
            uint p = item.Price();
            item.Count = old;
            return (uint)(p * rate);
        }

        /// <summary>(uint)(RepairPrice() x rate), x3 for a special repair.</summary>
        public static uint RepairFee(UserItem item, float rate, bool special) =>
            item?.Info == null ? 0 : (uint)(item.RepairPrice() * (special ? 3 : 1) * rate);

        /// <summary>The merchant pays half the price (NPCDropDialog Sell: Price() / 2).</summary>
        public static uint SellPrice(UserItem item) => item?.Info == null ? 0 : item.Price() / 2;

        /// <summary>Durability in the client's units (ItemLabel: thousandths).</summary>
        public static string Dura(int v) => W.N1(v / 1000.0);

        public static ModernUi Ui => GameScene.Ui as ModernUi;

        public static void Chat(string text) => GameScene.Models.Chat.Add(text, ChatType.System);

        public static UserItem FindBag(ulong id, out int slot)
        {
            slot = -1;
            UserItem[] inv = GameScene.User?.Inventory;
            if (inv == null) return null;
            for (int i = 0; i < inv.Length; i++)
                if (inv[i] != null && inv[i].UniqueID == id) { slot = i; return inv[i]; }
            return null;
        }

        public static int FreeSlots()
        {
            UserItem[] inv = GameScene.User?.Inventory;
            if (inv == null) return 0;
            int n = 0;
            for (int i = 0; i < inv.Length; i++) if (inv[i] == null) n++;
            return n;
        }
    }

    /// <summary>
    /// An item cell for an item outside the ItemMoves grids (goods, cart, picks, recipe parts) or a bag slot shown read-only with a
    /// click of its own (the sell bag, the npc box bag): the .icell look, the item picture, the stack count, picked (accent ring),
    /// dimmed (veil), locked (dashed) and a small tag ("junk"). Hover shows the item tooltip; Click is raised for every button.
    /// Repaints only when what it shows changed (<see cref="Refresh"/>).
    /// </summary>
    internal sealed class NpcItemCell : ModernControl
    {
        private Func<UserItem> _source;
        private UserItem _item;
        private string _key = string.Empty;
        private int _image = -1;
        private string _count = string.Empty;
        private bool _picked, _dim, _locked, _veil;
        private string _tag = string.Empty;
        private int _bagSlot = -1;

        public NpcItemCell(int sizeHud, Func<UserItem> source = null)
        {
            Size = new Size(S(sizeHud), S(sizeHud));
            _over = new CellText { Parent = this, Size = Size, NotControl = true };
            _source = source;
            Refresh();
        }

        /// <summary>A bag slot (lock state from ItemMoves, item from the inventory).</summary>
        public NpcItemCell Bag(int slot)
        {
            _bagSlot = slot;
            _source = () => ItemMoves.GetItem(MirGridType.Inventory, slot);
            Refresh();
            return this;
        }

        public int BagSlot => _bagSlot;
        public UserItem Item => _item;
        /// <summary>A count shown instead of the item's own (cart lines).</summary>
        public ushort CountOverride;
        /// <summary>The picture shown when empty (-1 = none).</summary>
        public int EmptyIcon = -1;

        public void SetSource(Func<UserItem> source)
        {
            _source = source;
            Refresh();
        }

        public void SetState(bool picked, bool dim, string tag = null, bool veil = false)
        {
            tag ??= string.Empty;
            if (picked == _picked && dim == _dim && tag == _tag && veil == _veil) return;
            _picked = picked;
            _dim = dim;
            _tag = tag;
            _veil = veil;
            Invalidate();
            _over.Sync(_count, _tag, _veil);
        }

        public void Refresh()
        {
            UserItem item = null;
            try { item = _source?.Invoke(); } catch (Exception) { item = null; }
            bool locked = _bagSlot >= 0 && ItemMoves.IsLocked(MirGridType.Inventory, _bagSlot);
            ItemInfo info = item?.Info;
            ushort count = CountOverride > 0 ? CountOverride : item?.Count ?? 0;
            string c = info != null && (info.StackSize > 1 || CountOverride > 1) && count > 1 ? count.ToString() : string.Empty;
            int image = info?.Image ?? -1;
            string key = item == null ? "-" : item.UniqueID + "|" + image + "|" + c + "|" + (locked ? "L" : "") + "|" + (int)(info?.Grade ?? 0);
            _item = item;
            if (key == _key) return;
            _key = key;
            _image = image;
            _count = c;
            _locked = locked;
            Invalidate();
            _over?.Sync(_count, _tag, _veil);
            if (Hovered) ShowTip();
        }

        protected override void OnMouseEnter()
        {
            base.OnMouseEnter();
            ShowTip();
        }

        protected override void OnMouseLeave()
        {
            base.OnMouseLeave();
            ModernTooltip.HideFor(this);
        }

        private void ShowTip()
        {
            if (_item?.Info == null || ItemMoves.Selected != null) { ModernTooltip.HideFor(this); return; }
            try { ModernTooltip.Show(_item, this, ModernTooltip.WornFor(_item)); }
            catch (Exception ex) { ModernLog.Once("[ModernUi] npc item tooltip", ex); }
        }

        public override void OnMouseDoubleClick(MouseEventArgs e) => OnMouseClick(e);

        protected override void Paint(Graphics g)
        {
            float k = K, rad = ModernTheme.Radius * k;
            RectangleF outer = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            RectangleF cell = RectangleF.Inflate(outer, -1f, -1f);
            using (GraphicsPath ring = ModernSkin.RoundRect(outer, rad + 1))
                ModernSkin.Stroke(g, ring, Color.Black);
            using GraphicsPath p = ModernSkin.RoundRect(cell, rad);
            using (var bp = new GraphicsPath())
            {
                bp.AddEllipse(cell.X - cell.Width * 0.25f, cell.Y - cell.Height * 0.4f, cell.Width * 1.5f, cell.Height * 1.5f);
                using var brush = new PathGradientBrush(bp) { CenterColor = ModernSkin.Opaque(ModernTheme.PanelC), SurroundColors = new[] { ModernSkin.Opaque(ModernTheme.PanelB) } };
                brush.CenterPoint = new PointF(cell.X + cell.Width / 2f, cell.Y + cell.Height * 0.35f);
                g.FillPath(brush, p);
            }
            Color border = ModernTheme.Frame;
            ItemInfo info = _item?.Info;
            if (info != null && info.Grade != ItemGrade.None) border = ModernTheme.Mix(ModernTheme.Grade(info.Grade), ModernTheme.Frame, 0.7f);
            if (_picked) border = ModernTheme.Accent;
            else if (Hovered && Enabled) border = W.Over(ModernTheme.Accent, 0.7f);
            ModernSkin.Stroke(g, p, border, _picked ? Math.Max(1.5f, 2f * k) : 1f);
            if (_locked)
            {
                using var pen = new Pen(ModernTheme.Warn, 1f) { DashStyle = DashStyle.Dash };
                g.DrawRectangle(pen, 2.5f, 2.5f, Size.Width - 5f, Size.Height - 5f);
            }
        }

        protected internal override void DrawControl()
        {
            base.DrawControl();
            if (Libraries.Items == null) return;
            if (_image >= 0)
            {
                Size s = Libraries.Items.GetTrueSize(_image);
                if (!s.IsEmpty)
                {
                    Point at = new Point(DisplayLocation.X + (Size.Width - s.Width) / 2, DisplayLocation.Y + (Size.Height - s.Height) / 2);
                    Libraries.Items.Draw(_image, at, Color.White, false, _dim || _locked ? 0.35f : 1f);
                }
            }
            else if (EmptyIcon >= 0)
            {
                int s = (int)(Size.Width * 0.45f);
                W.DrawIcon(EmptyIcon, new Rectangle(DisplayLocation.X + (Size.Width - s) / 2, DisplayLocation.Y + (Size.Height - s) / 2, s, s), ModernSkin.Dim(ModernTheme.Dim, 0.55f));
            }
        }

        private readonly CellText _over;

        protected override void Dispose(bool disposing)
        {
            if (disposing) ModernTooltip.HideFor(this);
            base.Dispose(disposing);
        }

        /// <summary>The count / tag / veil over the picture (a child: paints after the item).</summary>
        private sealed class CellText : ModernControl
        {
            private string _count = string.Empty, _tag = string.Empty;
            private bool _veil;

            public void Sync(string count, string tag, bool veil)
            {
                if (count == _count && tag == _tag && veil == _veil) return;
                _count = count;
                _tag = tag;
                _veil = veil;
                Invalidate();
            }

            protected override void Paint(Graphics g)
            {
                float k = K;
                if (_veil)
                    using (var b = new SolidBrush(Color.FromArgb(185, W.Page)))
                        g.FillRectangle(b, 1, 1, Size.Width - 2, Size.Height - 2);
                if (_tag.Length > 0)
                {
                    RectangleF r = new RectangleF(1, Size.Height - 10 * k, Size.Width - 2, 9 * k);
                    using (var b = new SolidBrush(Color.FromArgb(190, 110, 110, 125)))
                        g.FillRectangle(b, r);
                    ModernSkin.Text(g, _tag, ModernSkin.Font(7.5f, true), Color.White, r, StringAlignment.Center);
                }
                if (_count.Length > 0)
                {
                    Font f = ModernSkin.Font(10, true);
                    RectangleF r = new RectangleF(0, 0, Size.Width - 3 * k, Size.Height - 1 * k);
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                            if (dx != 0 || dy != 0)
                                ModernSkin.Text(g, _count, f, Color.FromArgb(200, 0, 0, 0), new RectangleF(r.X + dx, r.Y + dy, r.Width, r.Height), StringAlignment.Far, StringAlignment.Far, false);
                    ModernSkin.Text(g, _count, f, Color.White, r, StringAlignment.Far, StringAlignment.Far, false);
                }
            }
        }
    }

    /// <summary>One run of rich text: plain / coloured text, a page link, a web link or an info link.</summary>
    internal sealed class NpcRun
    {
        public string Text = string.Empty;
        public Color Colour = Color.Empty;
        public NpcSegKind Kind;
        public string Key = string.Empty;
        public int Index = -1;
        public bool Bold;
    }

    /// <summary>
    /// The NPC page's text (NPCDialog.NewText's labels as one painted control): runs laid out with word wrap, links underlined and
    /// lit on hover, the typewriter (only the first <see cref="Shown"/> characters paint). Layout happens on <see cref="SetRuns"/>
    /// only; hover / typing repaint on a change only.
    /// </summary>
    internal sealed class NpcRichText : ModernControl
    {
        private readonly List<NpcRun> _runs = new List<NpcRun>();
        private readonly List<(RectangleF R, int Run, int Start, string Text)> _pieces = new List<(RectangleF, int, int, string)>();
        private int _hot = -1, _shown = int.MaxValue, _total;
        private readonly float _fontHud;

        public NpcRichText(int widthHud, float fontHud = 13)
        {
            _fontHud = fontHud;
            Size = new Size(S(widthHud), S(20));
            Sound = SoundList.ButtonC;
        }

        public event Action<int> RunClicked;
        /// <summary>The hovered run changed (index, its first piece's rectangle relative to the control; -1 = none).</summary>
        public event Action<int, Rectangle> RunHover;

        public IReadOnlyList<NpcRun> Runs => _runs;
        public int TotalChars => _total;
        public int Shown => _shown;

        public void SetShown(int n)
        {
            n = Math.Max(0, n);
            if (n >= _total) n = int.MaxValue;
            if (n == _shown) return;
            _shown = n;
            Invalidate();
        }

        /// <summary>Lays the runs out in the control's width; the height follows the text.</summary>
        public void SetRuns(List<NpcRun> runs)
        {
            _runs.Clear();
            _runs.AddRange(runs);
            _hot = -1;
            Layout();
        }

        public void Relayout() => Layout();

        private void Layout()
        {
            _pieces.Clear();
            _total = 0;
            float width = Size.Width, lh = (float)Math.Round(_fontHud * 1.6f * K);
            float x = 0, y = 0;
            using var bmp = new Bitmap(1, 1);
            using Graphics g = Graphics.FromImage(bmp);
            ModernSkin.Prepare(g);
            using var fmt = new StringFormat(StringFormat.GenericTypographic);
            fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            for (int r = 0; r < _runs.Count; r++)
            {
                NpcRun run = _runs[r];
                Font f = ModernSkin.Font(_fontHud, run.Bold || run.Kind == NpcSegKind.Link || run.Kind == NpcSegKind.Info);
                string text = run.Text ?? string.Empty;
                int i = 0;
                while (i < text.Length)
                {
                    if (text[i] == '\n')
                    {
                        x = 0;
                        y += lh;
                        i++;
                        _total++;
                        continue;
                    }
                    // one word (with its trailing spaces)
                    int j = i;
                    while (j < text.Length && text[j] != ' ' && text[j] != '\n') j++;
                    while (j < text.Length && text[j] == ' ') j++;
                    string word = text.Substring(i, j - i);
                    float w = g.MeasureString(word, f, PointF.Empty, fmt).Width;
                    if (x > 0 && x + w.TrimEnd(word, g, f, fmt) > width)
                    {
                        x = 0;
                        y += lh;
                    }
                    // a word longer than the line: break it by characters
                    if (w > width && x == 0)
                    {
                        int a = i;
                        while (a < j)
                        {
                            int b = a + 1;
                            while (b < j && g.MeasureString(text.Substring(a, b - a + 1), f, PointF.Empty, fmt).Width <= width) b++;
                            string part = text.Substring(a, b - a);
                            float pw = g.MeasureString(part, f, PointF.Empty, fmt).Width;
                            _pieces.Add((new RectangleF(x, y, pw, lh), r, _total, part));
                            _total += part.Length;
                            a = b;
                            if (a < j) { x = 0; y += lh; } else x += pw;
                        }
                    }
                    else
                    {
                        _pieces.Add((new RectangleF(x, y, w, lh), r, _total, word));
                        _total += word.Length;
                        x += w;
                    }
                    i = j;
                }
            }
            int h = (int)Math.Ceiling(y + lh);
            if (_pieces.Count == 0) h = (int)lh;
            if (Size.Height != h) Size = new Size(Size.Width, h);
            Invalidate();
        }

        protected override void Paint(Graphics g)
        {
            float k = K;
            int shown = _shown;
            for (int i = 0; i < _pieces.Count; i++)
            {
                var p = _pieces[i];
                if (p.Start >= shown) break;
                string text = p.Start + p.Text.Length > shown ? p.Text.Substring(0, shown - p.Start) : p.Text;
                NpcRun run = _runs[p.Run];
                bool hot = p.Run == _hot;
                Color c;
                switch (run.Kind)
                {
                    case NpcSegKind.Link:
                    case NpcSegKind.Url:
                        c = hot ? Color.White : ModernTheme.Accent;
                        break;
                    case NpcSegKind.Info:
                        c = hot ? ModernTheme.Warn : ModernTheme.Info;
                        break;
                    default:
                        c = run.Colour.IsEmpty ? Color.FromArgb(0xe9, 0xee, 0xf8) : run.Colour;
                        break;
                }
                if (c.A == 0) continue;
                Font f = ModernSkin.Font(_fontHud, run.Bold || run.Kind == NpcSegKind.Link || run.Kind == NpcSegKind.Info);
                RectangleF r = new RectangleF(p.R.X, p.R.Y, p.R.Width + 4 * k, p.R.Height);
                ModernSkin.Text(g, text, f, c, r, StringAlignment.Near, StringAlignment.Center, false);
                if (run.Kind == NpcSegKind.Link || run.Kind == NpcSegKind.Url)
                {
                    float tw = ModernSkin.Measure(text.TrimEnd(), f);
                    float uy = p.R.Y + p.R.Height / 2f + f.GetHeight() / 2f;
                    using var pen = new Pen(c, Math.Max(1f, k));
                    g.DrawLine(pen, p.R.X, uy, p.R.X + tw, uy);
                }
            }
        }

        private int HitRun(Point mouse)
        {
            float lx = mouse.X - DisplayLocation.X, ly = mouse.Y - DisplayLocation.Y;
            for (int i = 0; i < _pieces.Count; i++)
            {
                var p = _pieces[i];
                if (p.Start >= _shown) break;
                if (_runs[p.Run].Kind == NpcSegKind.Text) continue;
                if (p.R.Contains(lx, ly)) return p.Run;
            }
            return -1;
        }

        public override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            SetHot(HitRun(CMain.MPoint));
        }

        protected override void OnMouseLeave()
        {
            base.OnMouseLeave();
            SetHot(-1);
        }

        private void SetHot(int run)
        {
            if (run == _hot) return;
            _hot = run;
            Invalidate();
            Rectangle at = Rectangle.Empty;
            if (run >= 0)
                for (int i = 0; i < _pieces.Count; i++)
                    if (_pieces[i].Run == run) { at = Rectangle.Round(_pieces[i].R); break; }
            RunHover?.Invoke(run, at);
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            RunClicked?.Invoke(HitRun(CMain.MPoint));
        }

        public override void OnMouseDoubleClick(MouseEventArgs e) => OnMouseClick(e);
    }

    internal static class NpcMeasureExt
    {
        /// <summary>The width of a word without its trailing spaces (a line may end in spaces).</summary>
        public static float TrimEnd(this float width, string word, Graphics g, Font f, StringFormat fmt)
        {
            int n = word.Length;
            while (n > 0 && word[n - 1] == ' ') n--;
            if (n == word.Length) return width;
            return n == 0 ? 0 : g.MeasureString(word.Substring(0, n), f, PointF.Empty, fmt).Width;
        }
    }
}

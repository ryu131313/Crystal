// Shared pieces of the phase 3 economy windows (trust merchant, game shop, mail): an item picture that is not in an ItemMoves
// grid (a market listing, a mail attachment, a shop product, the trust merchant's own sell cell and side bag) drawn like a
// ModernItemCell with the item tooltip on hover, and a few text helpers. Lives in a Trust* file because the area's files are
// Trust* / GameShop* / Mail*; the mail and game shop windows use it too.
using System.Drawing.Drawing2D;
using Client.MirGraphics;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;
using DxMatrix = SlimDX.Matrix;

namespace Client.MirScenes.Modern.Windows
{
    /// <summary>
    /// An item picture bound to a getter (itemCells.ts readOnly cell / a custom grid "x:market", "x:mailRead"): the .icell well,
    /// the grade frame, the picture from Items.Lib (optionally scaled up, as the web's art boxes), the stack count, a dashed
    /// outline while <see cref="Locked"/> says so, the item tooltip on hover. Clicks raise <see cref="Clicked"/> with the button.
    /// Call <see cref="Sync"/> after the data changed: it repaints only when what it shows changed.
    /// </summary>
    internal sealed class EcoItemView : ModernControl
    {
        private readonly Func<UserItem> _get;
        private readonly EcoItemOverlay _overlay;
        private string _shown = "?";
        private int _image = -1;
        private bool _dim;
        private bool _well = true;
        private float _scale = 1f;

        public EcoItemView(int sizeHud, Func<UserItem> get)
        {
            _get = get;
            Size = new Size(S(sizeHud), S(sizeHud));
            _overlay = new EcoItemOverlay { Parent = this, Size = Size };
            Sync();
        }

        /// <summary>Is the item waiting (dimmed, dashed outline)?</summary>
        public Func<bool> Locked;

        /// <summary>Accent frame (the selected listing, the picked sell item).</summary>
        public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; Invalidate(); } }
        private bool _selected;

        /// <summary>Draw the cell well (false = only the picture, for the shop's art boxes).</summary>
        public bool Well { get => _well; set { if (_well == value) return; _well = value; Invalidate(); } }

        /// <summary>Picture scale (1 = native Items.Lib size).</summary>
        public float Scale { get => _scale; set { _scale = Math.Max(0.25f, value); } }

        /// <summary>Show the count overlay (stack size; the shop shows its own "x5").</summary>
        public bool ShowCount { get => _showCount; set { if (_showCount == value) return; _showCount = value; _shown = "?"; Sync(); } }
        private bool _showCount = true;

        /// <summary>Left / right click on the picture.</summary>
        public event Action<MouseButtons> Clicked;

        public UserItem Item => _get?.Invoke();

        public void Sync()
        {
            if (_overlay.Size != Size) _overlay.Size = Size; // the owner may resize the view after building it
            UserItem item = Item;
            ItemInfo info = item?.Info;
            bool locked = item != null && Locked != null && Locked();
            string key = item == null ? "-" : string.Concat(item.UniqueID.ToString(), "|", info?.Index.ToString() ?? "?", "|", item.Count.ToString(), locked ? "L" : "");
            if (key == _shown) return;
            _shown = key;
            _image = info != null ? info.Image : -1;
            _dim = locked;
            _overlay.Set(_showCount && info != null && info.StackSize > 1 && item.Count > 1 ? item.Count.ToString() : string.Empty, locked);
            Invalidate();
            if (Hovered) ShowTip();
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            Clicked?.Invoke(e.Button);
        }

        public override void OnMouseDoubleClick(MouseEventArgs e) => OnMouseClick(e);

        protected override void OnMouseEnter()
        {
            base.OnMouseEnter();
            ShowTip();
            if (_well) Invalidate();
        }

        protected override void OnMouseLeave()
        {
            base.OnMouseLeave();
            ModernTooltip.HideFor(this);
            if (_well) Invalidate();
        }

        private void ShowTip()
        {
            UserItem item = Item;
            if (item?.Info == null || ItemMoves.Selected != null)
            {
                ModernTooltip.HideFor(this);
                return;
            }
            try
            {
                ModernTooltip.Show(item, this, ModernTooltip.WornFor(item));
            }
            catch (Exception ex)
            {
                ModernLog.Once("[ModernUi] eco item tooltip", ex);
            }
        }

        protected override void Paint(Graphics g)
        {
            if (!_well) return;
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
            using (GraphicsPath ip = ModernSkin.RoundRect(RectangleF.Inflate(cell, -1f, -1f), Math.Max(0, rad - 1)))
                ModernSkin.Stroke(g, ip, Color.FromArgb(230, 0, 0, 0));
            Color border = ModernTheme.Frame;
            ItemInfo info = Item?.Info;
            if (info != null && info.Grade != ItemGrade.None) border = ModernTheme.Mix(ModernTheme.Grade(info.Grade), ModernTheme.Frame, 0.7f);
            if (_selected || Hovered) border = ModernTheme.Accent;
            ModernSkin.Stroke(g, p, border);
        }

        protected internal override void DrawControl()
        {
            base.DrawControl();
            if (_image >= 0) Eco.DrawItem(_image, DisplayLocation, Size, _scale, _dim ? 0.4f : 1f);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ModernTooltip.HideFor(this);
            base.Dispose(disposing);
        }

        private sealed class EcoItemOverlay : ModernControl
        {
            private string _count = string.Empty;
            private bool _locked;

            public EcoItemOverlay() { NotControl = true; }

            public void Set(string count, bool locked)
            {
                if (count == _count && locked == _locked) return;
                _count = count;
                _locked = locked;
                Invalidate();
            }

            protected override void Paint(Graphics g)
            {
                float k = K;
                if (_locked)
                {
                    using var pen = new Pen(ModernTheme.Warn, 1f) { DashStyle = DashStyle.Dash };
                    g.DrawRectangle(pen, 2.5f, 2.5f, Size.Width - 5f, Size.Height - 5f);
                }
                if (_count.Length == 0) return;
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

    /// <summary>Economy text and drawing helpers.</summary>
    internal static class Eco
    {
        /// <summary>UserItem.FriendlyName with the count (trust.ts name()).</summary>
        public static string Name(UserItem item)
        {
            if (item == null) return string.Empty;
            string n = item.Info != null ? item.Info.FriendlyName : W.T(ClientTextKeys.ModernEcoItemN, item.ItemIndex);
            return item.Count > 1 ? n + " (" + item.Count + ")" : n;
        }

        public static Color GradeColour(ItemInfo info) => info == null || info.Grade == ItemGrade.None ? Color.White : ModernTheme.Grade(info.Grade);

        public static string GradeName(ItemGrade g) => g switch
        {
            ItemGrade.Common => W.T(ClientTextKeys.ItemGradeCommon),
            ItemGrade.Rare => W.T(ClientTextKeys.ItemGradeRare),
            ItemGrade.Legendary => W.T(ClientTextKeys.ItemGradeLegendary),
            ItemGrade.Mythical => W.T(ClientTextKeys.ItemGradeMythical),
            ItemGrade.Heroic => W.T(ClientTextKeys.ItemGradeHeroic),
            _ => string.Empty,
        };

        /// <summary>"5 min ago" / "3 h ago" / "2 d ago" (fmtAgo).</summary>
        public static string Ago(DateTime when)
        {
            double s = Math.Max(0, (CMain.Now - when).TotalSeconds);
            if (s < 60) return W.T(ClientTextKeys.ModernEcoJustNow);
            if (s < 3600) return W.T(ClientTextKeys.ModernEcoMinAgo, (int)(s / 60));
            if (s < 86400) return W.T(ClientTextKeys.ModernEcoHoursAgo, (int)(s / 3600));
            return W.T(ClientTextKeys.ModernEcoDaysAgo, (int)(s / 86400));
        }

        /// <summary>Draws an Items.Lib picture centred in a device rectangle, scaled (render time only).</summary>
        public static void DrawItem(int image, Point at, Size box, float scale, float opacity = 1f)
        {
            if (image < 0 || Libraries.Items == null) return;
            Size s = Libraries.Items.GetTrueSize(image);
            if (s.IsEmpty) return;
            if (Math.Abs(scale - 1f) < 0.01f)
            {
                Libraries.Items.Draw(image, new Point(at.X + (box.Width - s.Width) / 2, at.Y + (box.Height - s.Height) / 2), Color.White, false, opacity);
                return;
            }
            float x = at.X + (box.Width - s.Width * scale) / 2f, y = at.Y + (box.Height - s.Height * scale) / 2f;
            DXManager.SetSpriteTransform(DxMatrix.Scaling(scale, scale, 0));
            try
            {
                Libraries.Items.Draw(image, new Point((int)Math.Round(x / scale), (int)Math.Round(y / scale)), Color.White, false, opacity);
            }
            finally
            {
                DXManager.SetSpriteTransform(DxMatrix.Identity);
            }
        }

        /// <summary>A display copy of an ItemInfo (GameShopCell.ShowItem: full durability, the pack count) for the tooltip.</summary>
        public static UserItem Show(ItemInfo info, ushort count)
        {
            if (info == null) return null;
            return new UserItem(info) { Count = count, CurrentDura = info.Durability, MaxDura = info.Durability };
        }

        /// <summary>A parsed amount (1,000 / 1.5k / 2m): Popups.TryParseAmount; false when not a number.</summary>
        public static bool Amount(string text, out uint value) => Popups.TryParseAmount(text ?? string.Empty, out value);

        /// <summary>A sender / recipient badge letter.</summary>
        public static string Initial(string name)
        {
            string t = (name ?? string.Empty).Trim();
            return t.Length == 0 ? "?" : char.ToUpperInvariant(t[0]).ToString();
        }

        /// <summary>Draws the round sender badge (.av): a disc with the initial, or the mail icon colour ring for system mail.</summary>
        public static void Badge(Graphics g, RectangleF r, string text, bool system)
        {
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(r);
                using var b = new PathGradientBrush(path) { CenterColor = ModernSkin.Opaque(ModernTheme.PanelC), SurroundColors = new[] { ModernSkin.Opaque(ModernTheme.PanelB) } };
                g.FillPath(b, path);
                ModernSkin.Stroke(g, path, system ? ModernTheme.Mix(ModernTheme.Accent, ModernTheme.Frame, 0.6f) : ModernTheme.Frame);
            }
            ModernSkin.Text(g, text, ModernSkin.Font(r.Height * 0.38f / ModernRoot.K, true), system ? ModernTheme.Accent : ModernTheme.Text, r, StringAlignment.Center);
        }
    }
}

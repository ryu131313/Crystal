using System.Drawing.Drawing2D;
using Client.MirGraphics;
using Client.MirObjects;

namespace Client.MirScenes.Modern.Core
{
    /// <summary>
    /// The new UI's item cell (port of core/itemCells.ts ItemCell + its .icell look), bound to (grid, slot) of
    /// <see cref="ItemMoves"/> — the same state a classic MirItemCell shows, so both always agree. Paints the slot (rounded well,
    /// frame: accent on hover / pick, the grade colour for graded items), the item picture from Items.Lib (dimmed while picked or
    /// locked), the stack count, a low / broken durability bar, a dashed outline while locked and a dot while sealed. Redraws only
    /// when what it shows changed (ItemMoves.SlotChanged for its slot or grid, diffed). Clicks go through ItemMoves exactly as
    /// MirItemCell does (BeginClick → base click → Click; BeginDoubleClick → DoubleClick); hover shows <see cref="ModernTooltip"/>.
    /// <para>Not ported yet (phase 2): drag (drag.ts), the craft shadow item, the hero-grid greying, key hints, drop filters.</para>
    /// </summary>
    public sealed class ModernItemCell : ModernControl
    {
        private MirGridType _grid;
        private int _slot;
        private readonly Action<ItemSlotKey> _slotChanged;
        private readonly CellOverlay _overlay;
        private string _shown = string.Empty;
        private int _image = -1;
        private bool _dim;

        public ModernItemCell(MirGridType grid, int slot, int sizeHud = 40)
        {
            _grid = grid;
            _slot = slot;
            Size = new Size(S(sizeHud), S(sizeHud));
            _overlay = new CellOverlay(this) { Parent = this, Size = Size };
            _slotChanged = OnSlotChanged;
            ItemMoves.SlotChanged.On(_slotChanged);
            Refresh(true);
        }

        public MirGridType Grid => _grid;
        public int Slot => _slot;

        /// <summary>Display only: no pick, use or double-click.</summary>
        public bool ReadOnly { get; set; }

        /// <summary>Re-points the cell (paged grids) and redraws it.</summary>
        public void SetSlot(MirGridType grid, int slot)
        {
            if (grid == _grid && slot == _slot) return;
            _grid = grid;
            _slot = slot;
            Refresh(true);
        }

        public ItemSlotRef Ref => new ItemSlotRef(_grid, _slot);

        /// <summary>The item in the slot now (read from ItemMoves, never cached); null when the grid has no data yet.</summary>
        public UserItem Item => SafeItem(_grid, _slot);

        internal static UserItem SafeItem(MirGridType grid, int slot)
        {
            if (MapObject.User == null || GameScene.Scene == null) return null;
            switch (grid)
            {
                case MirGridType.HeroEquipment:
                case MirGridType.HeroInventory:
                case MirGridType.HeroHPItem:
                case MirGridType.HeroMPItem:
                    if (MapObject.Hero == null) return null;
                    break;
                case MirGridType.Mount:
                case MirGridType.Fishing:
                    UserItem holder = MapObject.User.Equipment[grid == MirGridType.Mount ? (int)EquipmentSlot.Mount : (int)EquipmentSlot.Weapon];
                    if (holder == null || holder.Slots == null) return null;
                    break;
                case MirGridType.None:
                    return null;
            }
            try
            {
                return ItemMoves.GetItem(grid, slot);
            }
            catch (NotImplementedException)
            {
                return null; // a grid with no array (ItemMoves.GetArray default)
            }
        }

        private void OnSlotChanged(ItemSlotKey key)
        {
            if (IsDisposed || key.Grid != _grid) return;
            if (key.Slot != -1 && key.Slot != _slot && !ItemMoves.IsSingleSlot(key.Grid)) return;
            Refresh(false);
        }

        /// <summary>Recomputes what the cell shows and repaints only when it changed (call after S.NewItemInfo / a theme change
        /// with force).</summary>
        public void Refresh(bool force)
        {
            UserItem item = Item;
            ItemInfo info = item?.Info;
            int image = info != null ? info.Image : -1;
            bool stack = info != null && info.StackSize > 1;
            int dura = 0;
            if (info != null && !stack && item.MaxDura > 0) dura = item.CurrentDura <= 0 ? 2 : item.CurrentDura * 5 < item.MaxDura ? 1 : 0;
            bool locked = ItemMoves.IsLocked(_grid, _slot);
            bool picked = ItemMoves.IsSelected(_grid, _slot);
            bool sealedItem = item?.SealedInfo != null && item.SealedInfo.ExpiryDate > CMain.Now;
            int grade = info != null ? (int)info.Grade : 0;
            // a short diff key, built only on a slot change (never per frame)
            string key = item == null ? "-" : string.Concat(item.UniqueID.ToString(), "|", image.ToString(), "|", stack ? item.Count.ToString() : "",
                "|", dura.ToString(), "|", locked ? "L" : "", picked ? "P" : "", sealedItem ? "S" : "", "|", grade.ToString());
            if (!force && key == _shown) return;
            _shown = key;
            _image = image;
            _dim = locked || picked;
            _overlay.Set(stack ? item.Count.ToString() : string.Empty, dura, locked, sealedItem);
            Invalidate();
            if (Hovered) ShowTip();
        }

        // ---- input (MirItemCell.OnMouseClick / OnMouseDoubleClick, same calls in the same order) ----

        public override void OnMouseClick(MouseEventArgs e)
        {
            if (ReadOnly) return;
            ItemSlotRef slot = Ref;
            if (!ItemMoves.BeginClick(slot, e)) return;
            base.OnMouseClick(e);
            Redraw();
            ItemMoves.Click(slot, e);
        }

        public override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (ReadOnly) return;
            ItemSlotRef slot = Ref;
            if (!ItemMoves.BeginDoubleClick(slot)) return;
            base.OnMouseClick(e);
            Redraw();
            ItemMoves.DoubleClick(slot);
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
            GameScene.HoverItem = null;
        }

        private void ShowTip()
        {
            UserItem item = Item;
            // no tooltip while an item is picked up (itemCells.ts: hovers are suppressed while an item is held)
            if (item == null || item.Info == null || ItemMoves.Selected != null)
            {
                ModernTooltip.HideFor(this);
                return;
            }
            GameScene.HoverItem = item;
            try
            {
                ModernTooltip.Show(item, this, _grid == MirGridType.Equipment || _grid == MirGridType.HeroEquipment ? null : ModernTooltip.WornFor(item));
            }
            catch (Exception ex)
            {
                ModernLog.Once("[ModernUi] item tooltip", ex); // runs inside ItemMoves.SlotChanged, i.e. inside packet handlers
            }
        }

        // ---- drawing ----

        protected override void Paint(Graphics g)
        {
            float k = K, rad = ModernTheme.Radius * k;
            UserItem item = Item;
            RectangleF outer = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            RectangleF cell = RectangleF.Inflate(outer, -1f, -1f);
            using (GraphicsPath ring = ModernSkin.RoundRect(outer, rad + 1))
                ModernSkin.Stroke(g, ring, Color.Black);
            using GraphicsPath p = ModernSkin.RoundRect(cell, rad);
            using (var bp = new GraphicsPath())
            {
                bp.AddEllipse(cell.X - cell.Width * 0.25f, cell.Y - cell.Height * 0.4f, cell.Width * 1.5f, cell.Height * 1.5f);
                using var brush = new PathGradientBrush(bp) { CenterColor = ModernTheme.PanelC, SurroundColors = new[] { ModernTheme.PanelB } };
                brush.CenterPoint = new PointF(cell.X + cell.Width / 2f, cell.Y + cell.Height * 0.35f);
                g.FillPath(brush, p);
            }
            // inner shadow (::after inset 0 0 6px 1px rgba(0,0,0,.75))
            using (GraphicsPath ip = ModernSkin.RoundRect(RectangleF.Inflate(cell, -1f, -1f), Math.Max(0, rad - 1)))
            {
                ModernSkin.Stroke(g, ip, Color.FromArgb(230, 0, 0, 0));
                Region old = g.Clip;
                g.SetClip(ip, CombineMode.Intersect);
                ModernSkin.Stroke(g, ip, Color.FromArgb(90, 0, 0, 0), Math.Max(3f, 6f * k));
                g.Clip = old;
                old.Dispose();
            }
            Color border = ModernTheme.Frame;
            ItemInfo info = item?.Info;
            if (info != null && info.Grade != ItemGrade.None) border = ModernTheme.Mix(ModernTheme.Grade(info.Grade), ModernTheme.Frame, 0.7f);
            if (ItemMoves.IsSelected(_grid, _slot) || (Hovered && !ReadOnly)) border = ModernTheme.Accent;
            ModernSkin.Stroke(g, p, border);
        }

        protected internal override void DrawControl()
        {
            base.DrawControl();
            if (_image < 0 || Libraries.Items == null) return;
            Size s = Libraries.Items.GetTrueSize(_image);
            if (s.IsEmpty) return;
            Point at = new Point(DisplayLocation.X + (Size.Width - s.Width) / 2, DisplayLocation.Y + (Size.Height - s.Height) / 2);
            Libraries.Items.Draw(_image, at, Color.White, false, _dim ? 0.4f : 1f);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ItemMoves.SlotChanged.Off(_slotChanged);
                ModernTooltip.HideFor(this);
            }
            base.Dispose(disposing);
        }

        /// <summary>Count, durability bar, lock outline and sealed dot, drawn over the item picture.</summary>
        private sealed class CellOverlay : ModernControl
        {
            private readonly ModernItemCell _cell;
            private string _count = string.Empty;
            private int _dura;
            private bool _locked, _sealed;

            public CellOverlay(ModernItemCell cell)
            {
                _cell = cell;
                NotControl = true;
            }

            public void Set(string count, int dura, bool locked, bool sealedItem)
            {
                if (count == _count && dura == _dura && locked == _locked && sealedItem == _sealed) return;
                _count = count;
                _dura = dura;
                _locked = locked;
                _sealed = sealedItem;
                Invalidate();
            }

            protected override void Paint(Graphics g)
            {
                float k = K;
                if (_dura > 0)
                    using (var b = new SolidBrush(_dura == 2 ? ModernTheme.Bad : ModernTheme.Warn))
                        g.FillRectangle(b, 2f, Size.Height - 2f - 2f * k, Size.Width - 4f, 2f * k);
                if (_sealed)
                {
                    RectangleF d = new RectangleF(3 * k, Size.Height - 9 * k, 6 * k, 6 * k);
                    using (var b = new SolidBrush(ModernTheme.Info))
                        g.FillEllipse(b, d);
                    using (var pen = new Pen(Color.Black, 1f))
                        g.DrawEllipse(pen, d);
                }
                if (_locked)
                {
                    using var pen = new Pen(ModernTheme.Warn, 1f) { DashStyle = DashStyle.Dash };
                    g.DrawRectangle(pen, 2.5f, 2.5f, Size.Width - 5f, Size.Height - 5f);
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
}

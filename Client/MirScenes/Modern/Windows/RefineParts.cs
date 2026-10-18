// Shared pieces of the phase 3 upgrade windows (refine, awaken, socket, rent): small text / colour helpers, the bag column
// (bag cells with a click-to-place hook and a dim veil for items with no use here, the web's .bag / .bgrid columns), a single
// item slot shown from a model field (the refine anvil: NPCDropDialog.ItemCell) and a painted odds bar (the .odds strip).
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;

namespace Client.MirScenes.Modern.Windows
{
    /// <summary>Text, colour and session helpers of the upgrade windows.</summary>
    internal static class Up
    {
        public static ModernUi Ui => GameScene.Ui as ModernUi;
        public static Popups Pop => ModernRoot.Instance?.Popups;
        public static UserObject User => GameScene.User;
        public static int Belt => GameScene.User?.BeltIdx ?? UserObject.BeltSize;

        /// <summary>A chat system line (the classic dialogs' ReceiveChat(..., ChatType.System)).</summary>
        public static void Say(string text) => GameScene.Models?.Chat.Add(text, ChatType.System);

        /// <summary>The item's name without the stack count (UserItem.FriendlyName appends it).</summary>
        public static string Name(UserItem item) => Name(item?.Info);
        public static string Name(ItemInfo info) => info == null ? string.Empty : (info.FriendlyName ?? info.Name);

        /// <summary>The grade colour for names (no grade = white).</summary>
        public static Color Grade(ItemGrade grade) => grade == ItemGrade.None ? Color.White : ModernTheme.Grade(grade);

        /// <summary>Enum display names from the client JSON "Enum" object (as the classic dialogs show them).</summary>
        public static string GradeName(ItemGrade grade)
        {
            switch (grade)
            {
                case ItemGrade.Common: return W.T(ClientTextKeys.ItemGradeCommon);
                case ItemGrade.Rare: return W.T(ClientTextKeys.ItemGradeRare);
                case ItemGrade.Legendary: return W.T(ClientTextKeys.ItemGradeLegendary);
                case ItemGrade.Mythical: return W.T(ClientTextKeys.ItemGradeMythical);
                case ItemGrade.Heroic: return W.T(ClientTextKeys.ItemGradeHeroic);
            }
            return string.Empty;
        }

        public static string Pct(double fraction) => Math.Round(fraction * 100) + "%";

        /// <summary>The bag slot holding the item with this id (from the belt end), or -1.</summary>
        public static int BagSlot(ulong id)
        {
            UserItem[] inv = User?.Inventory;
            if (inv == null) return -1;
            for (int i = 0; i < inv.Length; i++)
                if (inv[i] != null && inv[i].UniqueID == id) return i;
            return -1;
        }

        /// <summary>Lays controls out top to bottom (visible ones only), gap HUD px apart; returns the bottom in HUD px.</summary>
        public static int Flow(int y, int gap, params MirControl[] items)
        {
            for (int i = 0; i < items.Length; i++)
            {
                MirControl c = items[i];
                if (c == null || !c.Visible) continue;
                Point at = new Point(c.Location.X, W.S(y));
                if (c.Location != at) c.Location = at;
                y += (int)Math.Round(c.Size.Height / ModernRoot.K) + gap;
            }
            return y;
        }

        public static void SetText(ModernLabel l, string text)
        {
            if (l != null && l.Text != (text ?? string.Empty)) l.Text = text ?? string.Empty;
        }

        public static void Show(MirControl c, bool on)
        {
            if (c != null && c.Visible != on) c.Visible = on;
        }
    }

    /// <summary>
    /// The bag beside an upgrade bench (the web's .bag column): ModernItemCells of User.Inventory[belt..] in a scroll view, a dim
    /// veil over items with no use here, and a left click on an item calling <c>place(slot)</c> (true = placed: the click is
    /// consumed, the item is not picked up). Picking with Shift / Ctrl / Alt or while an item is held works as everywhere else.
    /// </summary>
    internal sealed class BagColumn
    {
        private readonly ScrollView _view;
        private readonly int _cols, _cell;
        private readonly Func<int, bool> _place;
        private readonly Func<UserItem, bool> _useful;
        private readonly List<ModernItemCell> _cells = new List<ModernItemCell>();
        private readonly List<CellDecor> _decor = new List<CellDecor>();
        private int _built = -1;

        public BagColumn(MirControl parent, int x, int y, int cols, int cellHud, int heightHud, Func<int, bool> place, Func<UserItem, bool> useful)
        {
            _cols = cols;
            _cell = cellHud;
            _place = place;
            _useful = useful;
            _view = new ScrollView(cols * (cellHud + 4) + 10, heightHud) { Parent = parent, Location = new Point(W.S(x), W.S(y)), Background = W.Page };
            _view.Framed = false;
        }

        public ScrollView View => _view;
        public IReadOnlyList<ModernItemCell> Cells => _cells;

        /// <summary>Rebuilds the cells when the bag size changed, then re-dims (call on a grid change, never per frame).</summary>
        public void Sync()
        {
            UserItem[] inv = Up.User?.Inventory;
            if (inv == null) return;
            int belt = Up.Belt, n = Math.Max(0, inv.Length - belt);
            if (n != _built)
            {
                for (int i = 0; i < _cells.Count; i++) _cells[i].Dispose();
                _cells.Clear();
                _decor.Clear();
                for (int i = 0; i < n; i++)
                {
                    var c = new ModernItemCell(MirGridType.Inventory, belt + i, _cell)
                    {
                        Parent = _view.Content,
                        Location = new Point(W.S(i % _cols * (_cell + 4)), W.S(i / _cols * (_cell + 4))),
                    };
                    c.Click += OnCellClick;
                    _cells.Add(c);
                    _decor.Add(new CellDecor(c));
                }
                _view.FitContent(0);
                _built = n;
            }
            for (int i = 0; i < _cells.Count; i++)
            {
                _cells[i].Refresh(false);
                UserItem it = _cells[i].Item;
                _decor[i].SetMode(it != null && _useful != null && !_useful(it) ? 2 : 0);
            }
        }

        /// <summary>
        /// Raised by ModernItemCell.OnMouseClick after ItemMoves.BeginClick passed and before ItemMoves.Click. When the item is
        /// placed, the slot is marked as the pick so the ItemMoves.Click that follows only clears it (MoveItem: a click on the
        /// picked cell drops the pick) instead of picking the item up.
        /// </summary>
        private void OnCellClick(object sender, EventArgs e)
        {
            if (!(sender is ModernItemCell cell) || !(e is MouseEventArgs me) || me.Button != MouseButtons.Left) return;
            if (CMain.Shift || CMain.Ctrl || CMain.Alt || ItemMoves.Selected != null || _place == null) return;
            if (cell.Item == null || ItemMoves.IsLocked(MirGridType.Inventory, cell.Slot)) return;
            if (_place(cell.Slot)) ItemMoves.Selected = new ItemSlotRef(MirGridType.Inventory, cell.Slot);
        }
    }

    /// <summary>
    /// One item slot shown from a model field (NPCDropDialog.ItemCell: the refine anvil): a read-only ModernItemCell for the look,
    /// this control on top for the clicks (the owner decides what a click does) and the item tooltip.
    /// </summary>
    internal sealed class SlotView : ModernControl
    {
        private readonly ModernItemCell _cell;

        public SlotView(MirGridType grid, int slot, int sizeHud)
        {
            Size = new Size(W.S(sizeHud), W.S(sizeHud));
            _cell = new ModernItemCell(grid, slot, sizeHud) { Parent = this, ReadOnly = true, NotControl = true };
        }

        public UserItem Item => _cell.Item;
        public ModernItemCell Cell => _cell;

        /// <summary>Left / right click with the button.</summary>
        public event Action<MouseButtons> Pressed2;

        public void Refresh() => _cell.Refresh(false);

        protected override void Paint(Graphics g) { }

        public override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            Pressed2?.Invoke(e.Button);
        }

        public override void OnMouseDoubleClick(MouseEventArgs e) => OnMouseClick(e);

        protected override void OnMouseEnter()
        {
            base.OnMouseEnter();
            UserItem item = Item;
            if (item?.Info == null || ItemMoves.Selected != null) return;
            try
            {
                ModernTooltip.Show(item, this);
            }
            catch (Exception ex)
            {
                ModernLog.Once("[ModernUi] slot tooltip", ex);
            }
        }

        protected override void OnMouseLeave()
        {
            base.OnMouseLeave();
            ModernTooltip.HideFor(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ModernTooltip.HideFor(this);
            base.Dispose(disposing);
        }
    }

    /// <summary>The .odds strip: up to three coloured parts (fractions 0-1) in a rounded dark track.</summary>
    internal sealed class OddsBar : ModernControl
    {
        private float _a, _b, _c;
        private Color _ca, _cb, _cc;

        public OddsBar(int widthHud, int heightHud, Color a, Color b, Color c)
        {
            Size = new Size(W.S(widthHud), W.S(heightHud));
            _ca = a;
            _cb = b;
            _cc = c;
            NotControl = true;
        }

        public void Set(float a, float b, float c)
        {
            // quantised to 0.5 %: repaint only on a visible change
            a = (float)Math.Round(a * 200) / 200f;
            b = (float)Math.Round(b * 200) / 200f;
            c = (float)Math.Round(c * 200) / 200f;
            if (a == _a && b == _b && c == _c) return;
            _a = a;
            _b = b;
            _c = c;
            Invalidate();
        }

        protected override void Paint(Graphics g)
        {
            RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, r.Height / 2f);
            using (var b = new SolidBrush(ModernTheme.Field))
                g.FillPath(b, p);
            Region old = g.Clip;
            g.SetClip(p, CombineMode.Intersect);
            float x = r.X;
            void Part(float f, Color c)
            {
                if (f <= 0) return;
                float w = r.Width * Math.Clamp(f, 0f, 1f);
                using var b = new SolidBrush(c);
                g.FillRectangle(b, x, r.Y, w, r.Height);
                x += w;
            }
            Part(_a, _ca);
            Part(_b, _cb);
            Part(_c, _cc);
            g.Clip = old;
            old.Dispose();
            ModernSkin.Stroke(g, p, ModernTheme.Frame);
        }
    }
}

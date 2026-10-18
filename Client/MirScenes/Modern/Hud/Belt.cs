// Ported from WebClient/src/ui/modern/hud/belt.ts: the HUD's belt, two rows of five under the HP / MP bars. Cells 1-8 are
// the player's belt (Inventory 0-7, keys Belt1..Belt8), cells 9 / 0 the hero's belt (HeroInventory 0-1, keys HeroBelt1 /
// HeroBelt2) with an H badge, greyed while no hero is summoned (or it has fallen) and taking potions only (a client-side
// check; the server does not restrict the cell). The cells are the kit's ModernItemCell (ItemMoves clicks, tooltips);
// the key hints, the H badge and the grey veil are an overlay drawn above each cell, and a guard over each hero cell takes
// the press while the cell refuses it. Keys are GameScene's / ModernUi.KeyBind's; this file only shows their labels and
// re-reads them every two seconds (no key-binding change event on the desktop).
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirScenes.Modern.Core;

namespace Client.MirScenes.Modern.Hud
{
    internal sealed class Belt
    {
        public const int PlayerCells = 8, HeroCells = 2, Count = PlayerCells + HeroCells;

        public readonly ModernItemCell[] Cells = new ModernItemCell[Count];
        private readonly BeltBadge[] _badges = new BeltBadge[Count];
        private readonly HeroBeltGuard[] _guards = new HeroBeltGuard[HeroCells];
        private readonly KeybindOptions[] _options = new KeybindOptions[Count];
        private readonly string[] _labels = new string[Count];
        private long _nextLabels;
        private bool _off = true;

        public Belt(MirControl parent)
        {
            for (int i = 0; i < Count; i++)
            {
                bool hero = i >= PlayerCells;
                _options[i] = hero ? (i == PlayerCells ? KeybindOptions.HeroBelt1 : KeybindOptions.HeroBelt2) : KeybindOptions.Belt1 + i * 2;
                Cells[i] = new ModernItemCell(hero ? MirGridType.HeroInventory : MirGridType.Inventory, hero ? i - PlayerCells : i) { Parent = parent };
                _badges[i] = new BeltBadge(hero) { Parent = parent };
                if (hero)
                {
                    _guards[i - PlayerCells] = new HeroBeltGuard(this) { Parent = parent, Hint = HudKit.T(ClientTextKeys.ModernHudHeroBeltHint) };
                    Cells[i].Hint = HudKit.T(ClientTextKeys.ModernHudHeroBeltHint);
                }
            }
            Relabel(true);
            SetOff(HeroOff());
        }

        /// <summary>Places cell i (device px); its overlay and guard follow.</summary>
        public void Place(int i, Point at)
        {
            Cells[i].Location = at;
            _badges[i].Location = at;
            _badges[i].Size = Cells[i].Size;
            if (i >= PlayerCells)
            {
                _guards[i - PlayerCells].Location = at;
                _guards[i - PlayerCells].Size = Cells[i].Size;
            }
        }

        /// <summary>No hero summoned (or it has fallen): the hero cells are greyed and refuse presses (itemCells.ts default for hero grids).</summary>
        internal static bool HeroOff()
        {
            GameScene scene = GameScene.Scene;
            return GameScene.Hero == null || scene == null || scene.HeroSpawnState != HeroSpawnState.Summoned || GameScene.Hero.Dead;
        }

        /// <summary>Per frame from the HUD: the hero state (one compare) and the key hints every 2 s.</summary>
        public void Process()
        {
            bool off = HeroOff();
            if (off != _off) SetOff(off);
            if (CMain.Time >= _nextLabels) Relabel(false);
        }

        private void SetOff(bool off)
        {
            _off = off;
            for (int i = PlayerCells; i < Count; i++)
            {
                _badges[i].Off = off;
                Cells[i].Refresh(true);
            }
        }

        /// <summary>Re-reads the key hints (throttled: the labels are strings).</summary>
        public void Relabel(bool force)
        {
            _nextLabels = CMain.Time + 2000;
            for (int i = 0; i < Count; i++)
            {
                string label = HudKit.ShortKey(HudKit.KeyLabel(_options[i]));
                if (!force && label == _labels[i]) continue;
                _labels[i] = label;
                _badges[i].Key = label;
            }
        }

        internal bool Off => _off;
    }

    /// <summary>A belt cell's overlay: the key hint (top left), the hero H badge (bottom left) and the grey veil while off.</summary>
    internal sealed class BeltBadge : ModernControl
    {
        private readonly bool _hero;
        private string _key = string.Empty;
        private bool _off;

        public BeltBadge(bool hero)
        {
            _hero = hero;
            NotControl = true;
        }

        public string Key
        {
            get => _key;
            set
            {
                value ??= string.Empty;
                if (_key == value) return;
                _key = value;
                Invalidate();
            }
        }

        public bool Off
        {
            get => _off;
            set
            {
                if (_off == value) return;
                _off = value;
                Invalidate();
            }
        }

        protected override void Paint(Graphics g)
        {
            float k = K;
            if (_off)
            {
                // .icell.off: opacity .35 + grayscale — a dark veil over the cell (blended, the HUD is translucent anyway)
                using GraphicsPath p = ModernSkin.RoundRect(new RectangleF(1, 1, Size.Width - 2, Size.Height - 2), ModernTheme.Radius * k);
                using var b = new SolidBrush(Color.FromArgb(165, 8, 10, 14));
                g.FillPath(b, p);
            }
            if (_key.Length > 0)
                HudKit.ShadowText(g, _key, ModernSkin.Font(9, true), ModernTheme.Text, new RectangleF(3 * k, 1 * k, Size.Width - 4 * k, 11 * k), StringAlignment.Near, StringAlignment.Near);
            if (_hero)
            {
                Font f = ModernSkin.Font(8, true);
                RectangleF r = new RectangleF(2 * k, Size.Height - 2 * k - 10 * k, 9 * k, 10 * k);
                using (GraphicsPath p = ModernSkin.RoundRect(r, 2 * k))
                using (var b = new SolidBrush(_off ? ModernTheme.Dim : ModernTheme.Accent))
                    g.FillPath(b, p);
                ModernSkin.Text(g, HudKit.T(ClientTextKeys.ModernHudHeroBadge), f, Color.Black, r, StringAlignment.Center, StringAlignment.Center, false);
            }
        }
    }

    /// <summary>
    /// Over a hero belt cell: takes the press only while the cell refuses it (no hero, or a picked item that is not a potion:
    /// belt.ts potionsOnly), so the item cell below never sees it; otherwise it is transparent to the mouse.
    /// </summary>
    internal sealed class HeroBeltGuard : MirControl
    {
        private readonly Belt _belt;

        public HeroBeltGuard(Belt belt)
        {
            _belt = belt;
            BackColour = Color.Transparent;
        }

        private static bool RefusesPick()
        {
            ItemSlotRef sel = ItemMoves.Selected;
            if (sel == null) return false;
            UserItem item = sel.Item;
            return item?.Info != null && item.Info.Type != ItemType.Potion && sel.GridType != MirGridType.HeroInventory;
        }

        public override bool IsMouseOver(Point p)
        {
            if (!Visible || !DisplayRectangle.Contains(p)) return false;
            return Belt.HeroOff() || RefusesPick();
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            // refused: a hint line instead of a silent nothing
            if (!Belt.HeroOff() && RefusesPick())
                GameScene.Models?.Chat.Add(HudKit.T(ClientTextKeys.ModernHudPotionsOnly), ChatType.Hint);
        }

        public override void OnMouseDoubleClick(MouseEventArgs e) { }

        public override void Redraw() { }
    }
}

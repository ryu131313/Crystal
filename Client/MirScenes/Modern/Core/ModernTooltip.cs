using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirObjects;
using Client.MirScenes.Modern.Core.Widgets;

namespace Client.MirScenes.Modern.Core
{
    /// <summary>
    /// The new UI's item tooltip (port of the essentials of core/tooltip.ts): a card in the grade colour with the item picture,
    /// name, grade · type, a durability bar + weight, the stats (added stats in green) and the requirements (met / not met), on
    /// ModernRoot.Tips beside the hovered cell (or at the cursor), clamped on screen. While Shift is held and a worn item exists
    /// for the slot the item would go to, the worn item's card (with the ModernEquipped tag) shows beside it. Built once per show
    /// (never per frame); a held-Shift change rebuilds it.
    /// <para>Left for phase 2 (the classic GameScene.CreateItemLabel has them; tooltip.ts renders itemLabelSections): sockets and
    /// socketed gems, binding / soulbound / notice chips, story text, GM-only lines, awakening, overlap / gem stats, expiry and
    /// rental lines, sealed time, set items, the "vs worn" stat-change row and the Shift hint footer, price / action hints, the
    /// Inspect (other player's level / class) path, the type-specific durability wording beyond weapon-style / amulet use, and
    /// the per-stat wording of the classic label (rarer stats show as "Name + value").</para>
    /// </summary>
    public sealed class ModernTooltip : ModernControl, IModernTick
    {
        private const int CardWidth = 304;
        private static ModernTooltip _shared;

        private MirControl _anchor;
        private UserItem _item, _worn;
        private bool _shift;
        private Card _main, _wornCard;

        private ModernTooltip()
        {
            NotControl = true;
            Visible = false;
            Size = new Size(S(CardWidth), S(60));
        }

        /// <summary>The tooltip of the live root (built on first use in its Tips layer), or null without a root.</summary>
        public static ModernTooltip Shared
        {
            get
            {
                ModernRoot root = ModernRoot.Instance;
                if (root == null || root.IsDisposed) return null;
                if (_shared == null || _shared.IsDisposed || _shared.Parent != root.Tips)
                    _shared = new ModernTooltip { Parent = root.Tips };
                return _shared;
            }
        }

        /// <summary>The root went away.</summary>
        internal static void Forget()
        {
            if (_shared != null) ModernTick.Remove(_shared);
            _shared = null;
        }

        public bool IsShown => Visible && _item != null;
        public UserItem ShownItem => _item;
        public bool ShowsWorn => _wornCard != null;

        /// <summary>Shows the item's card beside <paramref name="anchor"/> (null = at the cursor). <paramref name="worn"/> is the
        /// equipped item to compare with while Shift is held (null = none).</summary>
        public static void Show(UserItem item, MirControl anchor, UserItem worn = null) => Shared?.ShowItem(item, anchor, worn);

        /// <summary>Hides the tooltip if it belongs to <paramref name="anchor"/>.</summary>
        public static void HideFor(MirControl anchor)
        {
            if (_shared != null && !_shared.IsDisposed && _shared._anchor == anchor) _shared.HideTip();
        }

        public static void HideAll()
        {
            if (_shared != null && !_shared.IsDisposed) _shared.HideTip();
        }

        /// <summary>The worn item in the equipment slot an item would go to (null = none / not equipment).</summary>
        public static UserItem WornFor(UserItem item)
        {
            if (item?.Info == null || MapObject.User == null) return null;
            int slot = item.Info.Type switch
            {
                ItemType.Weapon => (int)EquipmentSlot.Weapon,
                ItemType.Armour => (int)EquipmentSlot.Armour,
                ItemType.Helmet => (int)EquipmentSlot.Helmet,
                ItemType.Torch => (int)EquipmentSlot.Torch,
                ItemType.Necklace => (int)EquipmentSlot.Necklace,
                ItemType.Bracelet => (int)EquipmentSlot.BraceletL,
                ItemType.Ring => (int)EquipmentSlot.RingL,
                ItemType.Amulet => (int)EquipmentSlot.Amulet,
                ItemType.Belt => (int)EquipmentSlot.Belt,
                ItemType.Boots => (int)EquipmentSlot.Boots,
                ItemType.Stone => (int)EquipmentSlot.Stone,
                ItemType.Mount => (int)EquipmentSlot.Mount,
                _ => -1,
            };
            UserItem[] eq = MapObject.User.Equipment;
            if (slot < 0 || eq == null || slot >= eq.Length) return null;
            UserItem worn = eq[slot];
            if (worn == null && (slot == (int)EquipmentSlot.BraceletL || slot == (int)EquipmentSlot.RingL) && slot + 1 < eq.Length) worn = eq[slot + 1];
            return worn != null && worn != item && worn.Info != null ? worn : null;
        }

        private void ShowItem(UserItem item, MirControl anchor, UserItem worn)
        {
            if (item?.Info == null)
            {
                HideTip();
                return;
            }
            bool same = item == _item && worn == _worn && anchor == _anchor && Visible;
            _anchor = anchor;
            _item = item;
            _worn = worn;
            _shift = CMain.Shift;
            if (_worn != null) ModernTick.Add(this);
            else ModernTick.Remove(this);
            if (same && _main != null) { Place(); return; }
            Rebuild();
        }

        private void HideTip()
        {
            ModernTick.Remove(this);
            _anchor = null;
            _item = _worn = null;
            _main = _wornCard = null;
            if (Visible) Visible = false;
        }

        void IModernTick.Tick()
        {
            if (IsDisposed || !Visible || _worn == null)
            {
                ModernTick.Remove(this);
                return;
            }
            if (_anchor != null && (_anchor.IsDisposed || !_anchor.Visible))
            {
                HideTip();
                return;
            }
            if (CMain.Shift == _shift) return;
            _shift = CMain.Shift;
            Rebuild();
        }

        private void Rebuild()
        {
            _main = BuildCard(_item, false);
            _wornCard = _shift && _worn != null ? BuildCard(_worn, true) : null;
            int w = S(CardWidth), gap = S(6);
            int h = _main.Height;
            if (_wornCard != null) h = Math.Max(h, _wornCard.Height);
            Size = new Size(_wornCard != null ? w * 2 + gap : w, h);
            Place();
            Visible = true;
            BringToFront();
            Invalidate();
        }

        private void Place()
        {
            int W = Settings.ScreenWidth, H = Settings.ScreenHeight, pad = S(4);
            int x, y;
            if (_anchor != null && !_anchor.IsDisposed)
            {
                Rectangle a = _anchor.DisplayRectangle;
                x = a.Right + S(6);
                if (x + Size.Width > W - pad) x = a.Left - Size.Width - S(6);
                y = a.Top;
            }
            else
            {
                x = CMain.MPoint.X + S(18);
                if (x + Size.Width > W - pad) x = CMain.MPoint.X - Size.Width - S(14);
                y = CMain.MPoint.Y + S(18);
            }
            if (y + Size.Height > H - pad) y = H - Size.Height - pad;
            x = Math.Max(pad, Math.Min(x, W - Size.Width - pad));
            y = Math.Max(pad, y);
            Location = new Point(x, y);
        }

        // ------------------------------------------------------------------ content

        private struct Line
        {
            public string Text;
            public Color Colour;
            public bool Bold;
            public int Y, H;
        }

        private sealed class Card
        {
            public bool Worn;
            public ItemGrade Grade;
            public int Image = -1;
            public string Name = string.Empty, Sub = string.Empty, Dura = string.Empty;
            public float DuraFrac = -1;
            public int DuraY;
            public readonly List<Line> Lines = new List<Line>();
            public readonly List<int> Separators = new List<int>();
            public int Height;
        }

        private static string L(ClientTextKeys key) => GameLanguage.ClientTextMap.GetLocalization(key);
        private static string L(ClientTextKeys key, params object[] args) => GameLanguage.ClientTextMap.GetLocalization(key, args);

        private Card BuildCard(UserItem item, bool worn)
        {
            ItemInfo info = item.Info;
            var c = new Card { Worn = worn, Grade = info.Grade, Image = info.Image };
            c.Name = (item.RefineAdded > 0 ? "(*)" : string.Empty) + item.FriendlyName;
            string grade = GradeText(info.Grade), type = TypeText(info.Type);
            c.Sub = grade.Length > 0 && type.Length > 0 ? grade + " · " + type : grade + type;

            int y = S(10 + 50 + 8); // header: icon 50 + padding
            float textW = S(CardWidth - 24);
            Font font = ModernSkin.Font(12);

            // durability / weight
            string dura = DuraText(item);
            string weight = item.Weight > 0 ? L(ClientTextKeys.Weight) + " " + item.Weight : string.Empty;
            if (dura.Length > 0 || weight.Length > 0)
            {
                c.Separators.Add(y);
                c.DuraY = y + S(6);
                if (info.Type != ItemType.Amulet && item.MaxDura > 0 && dura.Length > 0) c.DuraFrac = Math.Clamp(item.CurrentDura / (float)item.MaxDura, 0f, 1f);
                c.Dura = dura.Length > 0 && weight.Length > 0 ? dura + "   " + weight : dura + weight;
                y += S(6 + 16 + 6);
            }

            // stats
            var stats = new List<(string, Color)>();
            AddStats(item, stats);
            y = AddSection(c, stats, y, font, textW);

            // requirements
            var need = new List<(string, Color)>();
            AddNeeds(item, need);
            y = AddSection(c, need, y, font, textW);

            c.Height = y + S(4);
            return c;
        }

        private static int AddSection(Card c, List<(string, Color)> lines, int y, Font font, float width)
        {
            if (lines.Count == 0) return y;
            c.Separators.Add(y);
            y += S(6);
            for (int i = 0; i < lines.Count; i++)
            {
                (string text, Color colour) = lines[i];
                int h = Math.Max(S(16), (int)Math.Ceiling(ModernSkin.MeasureWrapped(text, font, width).Height) + 2);
                c.Lines.Add(new Line { Text = text, Colour = colour, Y = y, H = h });
                y += h;
            }
            return y + S(6);
        }

        private static string GradeText(ItemGrade grade) => grade switch
        {
            ItemGrade.Common => L(ClientTextKeys.ItemGradeCommon),
            ItemGrade.Rare => L(ClientTextKeys.ItemGradeRare),
            ItemGrade.Legendary => L(ClientTextKeys.ItemGradeLegendary),
            ItemGrade.Mythical => L(ClientTextKeys.ItemGradeMythical),
            ItemGrade.Heroic => L(ClientTextKeys.ItemGradeHeroic),
            _ => string.Empty,
        };

        /// <summary>The classic label's ItemTypeX keys (looked up by name, at show time only).</summary>
        private static string TypeText(ItemType type)
        {
            if (type == ItemType.Nothing) return string.Empty;
            return Enum.TryParse("ItemType" + type, out ClientTextKeys key) ? L(key) : string.Empty;
        }

        private static string DuraText(UserItem item)
        {
            ItemInfo info = item.Info;
            if (info.Durability <= 0) return string.Empty;
            switch (info.Type)
            {
                case ItemType.Amulet:
                    return item.CurrentDura > 0 || item.MaxDura > 0 ? L(ClientTextKeys.UsageCurrentMax, item.CurrentDura, item.MaxDura).Trim() : string.Empty;
                case ItemType.Gem:
                case ItemType.Potion:
                case ItemType.Transform:
                case ItemType.SealedHero:
                case ItemType.Ore:
                case ItemType.Meat:
                case ItemType.Mount:
                case ItemType.Food:
                case ItemType.Pets:
                    return string.Empty; // their own wording: phase 2
                default:
                    int current = (int)Math.Floor(item.CurrentDura / 1000M), maximum = (int)Math.Floor(item.MaxDura / 1000M);
                    return current > 0 || maximum > 0 ? L(ClientTextKeys.Durability) + " " + current + "/" + maximum : string.Empty;
            }
        }

        private static readonly (Stat Min, Stat Max, ClientTextKeys Key)[] Ranges =
        {
            (Stat.MinDC, Stat.MaxDC, ClientTextKeys.DC),
            (Stat.MinMC, Stat.MaxMC, ClientTextKeys.MC),
            (Stat.MinSC, Stat.MaxSC, ClientTextKeys.SC),
            (Stat.MinAC, Stat.MaxAC, ClientTextKeys.AC),
            (Stat.MinMAC, Stat.MaxMAC, ClientTextKeys.MAC),
        };

        private static void AddStats(UserItem item, List<(string, Color)> lines)
        {
            Stats baseStats = item.Info.Stats ?? new Stats();
            Stats added = item.AddedStats ?? new Stats();
            foreach ((Stat min, Stat max, ClientTextKeys key) in Ranges)
            {
                int lo = baseStats[min] + added[min], hi = baseStats[max] + added[max];
                if (lo == 0 && hi == 0) continue;
                bool plus = added[min] != 0 || added[max] != 0;
                lines.Add((L(key, lo, hi), plus ? ModernTheme.Ok : ModernTheme.Text));
            }

            var seen = new HashSet<Stat>();
            foreach (Stats s in new[] { baseStats, added })
            {
                foreach (KeyValuePair<Stat, int> kv in s.Values)
                {
                    Stat stat = kv.Key;
                    if (IsRangeStat(stat) || !seen.Add(stat)) continue;
                    int v = baseStats[stat] + added[stat];
                    if (v == 0) continue;
                    string text = stat switch
                    {
                        Stat.Accuracy => L(ClientTextKeys.Accuracy, v),
                        Stat.Agility => L(ClientTextKeys.Agility, v),
                        Stat.Luck => L(ClientTextKeys.Luck, v),
                        _ => stat.ToLocalizedString() + (v > 0 ? " + " : " ") + v,
                    };
                    lines.Add((text, added[stat] != 0 ? ModernTheme.Ok : ModernTheme.Text));
                }
            }
        }

        private static bool IsRangeStat(Stat s)
        {
            for (int i = 0; i < Ranges.Length; i++)
                if (Ranges[i].Min == s || Ranges[i].Max == s) return true;
            return false;
        }

        private static void AddNeeds(UserItem item, List<(string, Color)> lines)
        {
            UserObject user = MapObject.User;
            if (user == null) return;
            ItemInfo real = Functions.GetRealItem(item.Info, user.Level, user.Class, GameScene.ItemInfoList) ?? item.Info;
            if (real.RequiredAmount > 0)
            {
                int amount = real.RequiredAmount;
                (string text, bool ok) = real.RequiredType switch
                {
                    RequiredType.Level => (L(ClientTextKeys.RequiredLevel, amount), user.Level >= amount),
                    RequiredType.MaxAC => (L(ClientTextKeys.RequiredAC, amount), user.Stats[Stat.MaxAC] >= amount),
                    RequiredType.MaxMAC => (L(ClientTextKeys.RequiredMAC, amount), user.Stats[Stat.MaxMAC] >= amount),
                    RequiredType.MaxDC => (L(ClientTextKeys.RequiredDC, amount), user.Stats[Stat.MaxDC] >= amount),
                    RequiredType.MaxMC => (L(ClientTextKeys.RequiredMC, amount), user.Stats[Stat.MaxMC] >= amount),
                    RequiredType.MaxSC => (L(ClientTextKeys.RequiredSC, amount), user.Stats[Stat.MaxSC] >= amount),
                    RequiredType.MaxLevel => (L(ClientTextKeys.MaximumLevel, amount), user.Level <= amount),
                    RequiredType.MinAC => (L(ClientTextKeys.RequiredBaseAC, amount), user.Stats[Stat.MinAC] >= amount),
                    RequiredType.MinMAC => (L(ClientTextKeys.RequiredBaseMAC, amount), user.Stats[Stat.MinMAC] >= amount),
                    RequiredType.MinDC => (L(ClientTextKeys.RequiredBaseDC, amount), user.Stats[Stat.MinDC] >= amount),
                    RequiredType.MinMC => (L(ClientTextKeys.RequiredBaseMC, amount), user.Stats[Stat.MinMC] >= amount),
                    RequiredType.MinSC => (L(ClientTextKeys.RequiredBaseSC, amount), user.Stats[Stat.MinSC] >= amount),
                    _ => (L(ClientTextKeys.UnknownTypeRequired), true),
                };
                lines.Add(((ok ? "✓ " : "✕ ") + text, ok ? ModernTheme.Text : ModernTheme.BadSoft));
            }
            if (real.RequiredClass != RequiredClass.None)
            {
                RequiredClass mine = user.Class switch
                {
                    MirClass.Warrior => RequiredClass.Warrior,
                    MirClass.Wizard => RequiredClass.Wizard,
                    MirClass.Taoist => RequiredClass.Taoist,
                    MirClass.Assassin => RequiredClass.Assassin,
                    MirClass.Archer => RequiredClass.Archer,
                    _ => RequiredClass.None,
                };
                bool ok = real.RequiredClass.HasFlag(mine);
                lines.Add(((ok ? "✓ " : "✕ ") + L(ClientTextKeys.ClassRequired, real.RequiredClass.ToLocalizedString()), ok ? ModernTheme.Text : ModernTheme.BadSoft));
            }
        }

        // ------------------------------------------------------------------ drawing

        protected override void Paint(Graphics g)
        {
            if (_main == null) return;
            PaintCard(g, _main, 0);
            if (_wornCard != null) PaintCard(g, _wornCard, S(CardWidth) + S(6));
        }

        private void PaintCard(Graphics g, Card c, int x0)
        {
            float k = K, rad = ModernTheme.Radius * k, w = S(CardWidth);
            Color gc = ModernTheme.Grade(c.Grade);
            RectangleF outer = new RectangleF(x0 + 0.5f, 0.5f, w - 1f, c.Height - 1f);
            RectangleF panel = RectangleF.Inflate(outer, -1f, -1f);
            using (GraphicsPath ring = ModernSkin.RoundRect(outer, rad + 1))
                ModernSkin.Stroke(g, ring, Color.Black);
            using GraphicsPath p = ModernSkin.RoundRect(panel, rad);
            Color top = Opaque(ModernTheme.Mix(ModernTheme.PanelB, Color.Black, 0.94f)), bottom = Opaque(ModernTheme.Mix(ModernTheme.PanelB, Color.Black, 0.7f));
            ModernSkin.FillVertical(g, p, panel, new[] { top, bottom });

            Region old = g.Clip;
            g.SetClip(p, CombineMode.Intersect);
            // header band in the grade colour
            RectangleF band = new RectangleF(panel.X, panel.Y, panel.Width, 68 * k);
            using (var b = new LinearGradientBrush(new RectangleF(band.X, band.Y - 1, band.Width, band.Height + 2), ModernTheme.Alpha(gc, 0.26f), Color.FromArgb(0, gc), LinearGradientMode.Vertical))
                g.FillRectangle(b, band);
            using (var pen = new Pen(ModernTheme.Alpha(ModernTheme.Frame, 0.45f), 1f))
                foreach (int sy in c.Separators)
                    g.DrawLine(pen, panel.X, sy + 0.5f, panel.Right, sy + 0.5f);
            g.Clip = old;
            old.Dispose();

            // icon box
            RectangleF ic = new RectangleF(x0 + 10 * k, 10 * k, 50 * k, 50 * k);
            using (GraphicsPath ip = ModernSkin.RoundRect(ic, ModernTheme.Radius * 0.8f * k))
            {
                using (var b = new SolidBrush(Color.Black))
                    g.FillPath(b, ip);
                ModernSkin.Stroke(g, ip, gc);
            }
            float nx = x0 + 70 * k, nw = w - 80 * k;
            ModernSkin.Text(g, c.Name, ModernSkin.Font(15, true), gc, new RectangleF(nx, 12 * k, nw, 22 * k));
            if (c.Sub.Length > 0)
                ModernSkin.Text(g, c.Sub, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(nx, 36 * k, nw, 16 * k));

            if (c.Worn)
            {
                string tag = L(ClientTextKeys.ModernEquipped).ToUpperInvariant();
                Font tf = ModernSkin.Font(9, true);
                float tw = ModernSkin.Measure(tag, tf) + 16 * k;
                RectangleF tr = new RectangleF(x0 + w - tw - 10 * k, 0, tw, 14 * k);
                using (GraphicsPath tp = ModernSkin.RoundRect(tr, 4 * k))
                using (var b = new SolidBrush(ModernTheme.Dim))
                    g.FillPath(b, tp);
                ModernSkin.Text(g, tag, tf, Color.Black, tr, StringAlignment.Center, StringAlignment.Center, false);
            }

            if (c.Dura.Length > 0)
            {
                float dx = x0 + 12 * k, dy = c.DuraY, dw = w - 24 * k;
                Font f = ModernSkin.Font(11);
                float textW = ModernSkin.Measure(c.Dura, f) + 4;
                if (c.DuraFrac >= 0)
                {
                    float bw = Math.Max(20 * k, dw - textW - 8 * k);
                    RectangleF bar = new RectangleF(dx, dy + 5.5f * k, bw, 5 * k);
                    using (GraphicsPath bp = ModernSkin.RoundRect(bar, 2.5f * k))
                    using (var b = new SolidBrush(Color.FromArgb(0x05, 0x07, 0x0c)))
                        g.FillPath(b, bp);
                    if (c.DuraFrac > 0)
                        using (GraphicsPath fp = ModernSkin.RoundRect(new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * c.DuraFrac), bar.Height), 2.5f * k))
                        using (var b = new SolidBrush(c.DuraFrac < 0.3f ? ModernTheme.Bad : ModernTheme.Accent))
                            g.FillPath(b, fp);
                    dx += bw + 8 * k;
                }
                ModernSkin.Text(g, c.Dura, f, ModernTheme.Dim, new RectangleF(dx, dy, x0 + w - 12 * k - dx, 16 * k));
            }

            Font lf = ModernSkin.Font(12);
            for (int i = 0; i < c.Lines.Count; i++)
            {
                Line l = c.Lines[i];
                ModernSkin.Text(g, l.Text, lf, l.Colour, new RectangleF(x0 + 12 * k, l.Y, w - 24 * k, l.H), StringAlignment.Near, StringAlignment.Near, false, true);
            }
        }

        protected internal override void DrawControl()
        {
            base.DrawControl();
            if (Libraries.Items == null) return;
            DrawIcon(_main, 0);
            if (_wornCard != null) DrawIcon(_wornCard, S(CardWidth) + S(6));
        }

        private void DrawIcon(Card c, int x0)
        {
            if (c == null || c.Image < 0) return;
            Size s = Libraries.Items.GetSize(c.Image);
            if (s.IsEmpty) return;
            int cx = DisplayLocation.X + x0 + S(35), cy = DisplayLocation.Y + S(35);
            Libraries.Items.Draw(c.Image, new Point(cx - s.Width / 2, cy - s.Height / 2), Color.White, false, 1f);
        }

        private static Color Opaque(Color c) => Color.FromArgb(255, c.R, c.G, c.B);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ModernTick.Remove(this);
                if (_shared == this) _shared = null;
            }
            base.Dispose(disposing);
        }
    }
}

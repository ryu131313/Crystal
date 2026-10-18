// Ported from WebClient/src/ui/modern/windows/inventory.ts (InventoryWindow): the new UI's bag window. One 10-wide grid of
// the bag slots (User.Inventory after the belt; the belt cells are the HUD's), a locked row of 10 with an "Unlock row" button
// that asks the @ADDINVENTORY price and plays a golden wave over the new row when S.ResizeInventory grows the bag, a Quest
// tab (QuestInventory, display only), search + category chips that highlight in place, weight / free cells, gold (click to
// pick it up and drop it on the map, the classic GoldLabel), credit, Sort / Stack (C.SortItems through the pacer, 2 s
// cooldown, off while a bag cell waits for the server) and a delete bin.
// Desktop idiom: cells are ModernItemCell (click to pick / drop through ItemMoves, right-click use); the web's drop on the bin
// is "click the bin with a bag item picked up", and a click on the empty bin switches the classic delete mode (the next click
// on a bag item asks; right-click stops: ModernUi.ItemDeleteModeClick).
using Client.MirControls;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class InventoryWindow : ModernWindow
    {
        public const string WindowKey = "inventory";

        private const int Cols = 10, BagMax = 80, Cell = 40, Gap = 4, PadX = 14, QuestCells = 40;
        private const int WaveStep = 70, WaveMs = 1500;

        private static readonly ItemType[] Gear = { ItemType.Weapon, ItemType.Armour, ItemType.Helmet, ItemType.Necklace, ItemType.Bracelet, ItemType.Ring, ItemType.Belt, ItemType.Boots, ItemType.Stone, ItemType.Torch, ItemType.Mask };
        private static readonly ItemType[] Use = { ItemType.Potion, ItemType.Scroll, ItemType.Amulet, ItemType.Food, ItemType.Script, ItemType.Book, ItemType.Transform, ItemType.Pets, ItemType.MonsterSpawn };
        private static readonly ItemType[] Craft = { ItemType.Ore, ItemType.Meat, ItemType.CraftingMaterial, ItemType.Gem, ItemType.Awakening, ItemType.Socket };

        // state kept across a rebuild (resize, scale, theme)
        private int _tab;
        private string _query = string.Empty;
        private readonly HashSet<string> _cats = new HashSet<string>();
        private int _builtLen = -1;
        private bool _deleteMode;

        // built per layout
        private Tabs _tabs;
        private SearchBox _search;
        private ModernPanel _bagPage, _questPage;
        private readonly List<ModernItemCell> _cells = new List<ModernItemCell>();
        private readonly List<CellDecor> _decor = new List<CellDecor>();
        private readonly List<ModernItemCell> _questCells = new List<ModernItemCell>();
        private readonly List<CellDecor> _questDecor = new List<CellDecor>();
        private ModernProgressBar _weightBar;
        private ModernLabel _weightText, _freeText, _creditText;
        private PaintBox _gold, _bin, _unlock;
        private ModernButton _sortBtn, _stackBtn;

        private readonly GridWatch _bagWatch = new GridWatch(), _questWatch = new GridWatch();
        private bool _dirty = true;
        private string _goldText = string.Empty, _sortWhy = "-";
        private uint _goldShown, _creditShown;
        private long _weightKey = -1;
        private bool _dead;
        private long _nextTick;
        private readonly Action<ItemSlotKey> _slotChanged;
        private readonly Action _resized;
        private readonly Action<UserItem> _itemInfo;

        // the unlock wave: slots [from, to) since start (CMain.Time)
        private int _waveFrom = -1, _waveTo;
        private long _waveStart;

        public InventoryWindow() : base(WindowKey, W.T(ClientTextKeys.ModernWinInventory), -1, 70)
        {
            ContentWidth = PadX * 2 + Cols * Cell + (Cols - 1) * Gap;
            _slotChanged = k => { if (k.Grid == MirGridType.Inventory || k.Grid == MirGridType.QuestInventory || k.Grid == MirGridType.None) _dirty = true; };
            _resized = OnResized;
            _itemInfo = _ => _dirty = true;
        }

        private static UserObject User => GameScene.User;

        private int Belt => User?.BeltIdx ?? UserObject.BeltSize;

        /// <summary>CharacterInfo.ResizeInventory: the length after the next purchase (the end of the next row of 10, capped).</summary>
        private int NextLength(int len) => Math.Min(Belt + (Math.Max(0, len - Belt) / Cols + 1) * Cols, Belt + BagMax);

        /// <summary>PlayerObject "ADDINVENTORY": 2.5m, 5m, 10m, 20m for the 1st to 4th row past the base bag.</summary>
        private int UnlockPrice(int len) => 2500000 << Math.Min(3, Math.Max(0, (len - (Belt + 40)) / Cols));

        // ------------------------------------------------------------------ layout

        protected override void Build()
        {
            int len = User?.Inventory?.Length ?? Belt + 40;
            _builtLen = len;
            int bag = Math.Max(0, Math.Min(BagMax, len - Belt));
            bool canBuy = len < Belt + BagMax;
            int next = NextLength(len);
            int shownSlots = canBuy ? next - Belt : bag;
            int rows = Math.Max(4, (shownSlots + Cols - 1) / Cols);
            int gridH = rows * (Cell + Gap) - Gap;
            const int top = TitleHeight;

            _tabs = new Tabs(new[] { W.T(ClientTextKeys.ModernWinBag), W.T(ClientTextKeys.ModernWinQuest) }, _tab, ContentWidth - PadX * 2) { Parent = this, Location = new Point(S(PadX), S(top + 6)) };
            _tabs.Changed += i => { _tab = i; ModernTooltip.HideAll(); ShowTab(); };

            _search = W.At(new SearchBox(220, W.T(ClientTextKeys.ModernWinSearchBag)), this, PadX, top + 40);
            if (_query.Length > 0) _search.Text = _query;
            _search.Search += q => { _query = q.ToLowerInvariant(); ApplyFilter(); };
            var chips = W.At(new Chips<string>(new[]
            {
                new Choice<string>("gear", W.T(ClientTextKeys.ModernWinCatGear), null, W.T(ClientTextKeys.ModernWinCatGearHint)),
                new Choice<string>("use", W.T(ClientTextKeys.ModernWinCatUse), null, W.T(ClientTextKeys.ModernWinCatUseHint)),
                new Choice<string>("craft", W.T(ClientTextKeys.ModernWinCatCraft), null, W.T(ClientTextKeys.ModernWinCatCraftHint)),
                new Choice<string>("other", W.T(ClientTextKeys.ModernWinCatOther), null, W.T(ClientTextKeys.ModernWinCatOtherHint)),
            }, _cats, ContentWidth - PadX * 2 - 228, true, true), this, PadX + 228, top + 42);
            chips.Changed += sel =>
            {
                _cats.Clear();
                foreach (string s in sel) _cats.Add(s);
                ApplyFilter();
            };

            int gridTop = top + 74;
            _bagPage = new ModernPanel { Parent = this, Location = new Point(S(PadX), S(gridTop)), Size = new Size(S(ContentWidth - PadX * 2), S(gridH)) };
            _questPage = new ModernPanel { Parent = this, Location = _bagPage.Location, Size = _bagPage.Size, Visible = false };

            _cells.Clear();
            _decor.Clear();
            for (int p = 0; p < bag; p++)
            {
                var c = new ModernItemCell(MirGridType.Inventory, Belt + p, Cell) { Parent = _bagPage, Location = CellAt(p) };
                _cells.Add(c);
                _decor.Add(new CellDecor(c));
            }
            if (canBuy)
            {
                int price = UnlockPrice(len);
                for (int slot = len; slot < next; slot++)
                {
                    var lk = new PaintBox(PaintLock) { Parent = _bagPage, Location = CellAt(slot - Belt), Size = new Size(S(Cell), S(Cell)), Hint = W.T(ClientTextKeys.ModernWinUnlockCell, W.N(price)) };
                    lk.Click += (o, e) => AskUnlock();
                }
                int span = Math.Max(1, next - len), col = bag % Cols, row = bag / Cols;
                string text = span >= 5 ? W.T(ClientTextKeys.ModernWinUnlockRowFor, W.Compact(price)) : W.Compact(price);
                Font f = ModernSkin.Font(12, true);
                int w = (int)Math.Ceiling(ModernSkin.Measure(text, f)) + S(26);
                int cx = S(col * (Cell + Gap) + (span * (Cell + Gap) - Gap) / 2f), cy = S(row * (Cell + Gap) + Cell / 2f);
                _unlock = new PaintBox((g, b) => PaintUnlock(g, b, text))
                {
                    Parent = _bagPage,
                    Size = new Size(w, S(26)),
                    Location = new Point(cx - w / 2, cy - S(13)),
                    Hint = W.T(ClientTextKeys.ModernWinUnlockHint, span, W.N(price)),
                };
                _unlock.Click += (o, e) => AskUnlock();
            }

            _questCells.Clear();
            _questDecor.Clear();
            for (int p = 0; p < QuestCells; p++)
            {
                var c = new ModernItemCell(MirGridType.QuestInventory, p, Cell) { Parent = _questPage, Location = CellAt(p), ReadOnly = true };
                _questCells.Add(c);
                _questDecor.Add(new CellDecor(c));
            }
            W.Label(_questPage, W.T(ClientTextKeys.ModernWinQuestNote), 0, 4 * (Cell + Gap) + 2, ContentWidth - PadX * 2, 16, 11, false, ModernTheme.Dim);

            // foot: weight + free, then gold / credit / Sort / Stack / bin
            int y = gridTop + gridH + 12;
            var wt = W.Label(this, W.T(ClientTextKeys.ModernWinWeight).ToUpperInvariant(), PadX, y, 52, 16, 11, true, ModernTheme.Dim);
            wt.Hint = W.T(ClientTextKeys.ModernWinWeightHint);
            _weightBar = W.At(new ModernProgressBar(170, 8), this, PadX + 56, y + 4);
            _weightText = W.Label(this, string.Empty, PadX + 232, y, 96, 16, 11, true);
            W.Label(this, W.T(ClientTextKeys.ModernWinFree).ToUpperInvariant(), PadX + 332, y, 34, 16, 11, true, ModernTheme.Dim);
            _freeText = W.Label(this, string.Empty, PadX + 368, y, 70, 16, 11, true);

            y += 25;
            _gold = new PaintBox(PaintGold) { Parent = this, Location = new Point(S(PadX - 4), S(y)), Size = new Size(S(150), S(26)), Hint = W.T(ClientTextKeys.ModernWinGoldHint) };
            _gold.Click += (o, e) =>
            {
                if (ItemMoves.Selected == null) GameScene.PickedUpGold = !GameScene.PickedUpGold && GameScene.Gold > 0;
                _gold.Invalidate();
            };
            W.Label(this, W.T(ClientTextKeys.ModernWinCredit), PadX + 154, y, 44, 26, 12, true, ModernTheme.Dim);
            _creditText = W.Label(this, string.Empty, PadX + 200, y, 70, 26, 12, true);

            _bin = new PaintBox(PaintBin) { Parent = this, Size = new Size(S(26), S(26)) };
            _bin.Location = new Point(S(ContentWidth - PadX) - _bin.Size.Width, S(y));
            _bin.Click += (o, e) => BinClick();
            _stackBtn = new ModernButton(W.T(ClientTextKeys.ModernWinStack)) { Parent = this, Tooltip = W.T(ClientTextKeys.ModernWinStackHint) };
            _stackBtn.AutoWidth(52);
            _stackBtn.Location = new Point(_bin.Location.X - S(5) - _stackBtn.Size.Width, S(y + 2));
            _stackBtn.Click += (o, e) => SortBag(true);
            _sortBtn = new ModernButton(W.T(ClientTextKeys.ModernWinSort)) { Parent = this, Tooltip = W.T(ClientTextKeys.ModernWinSortHint) };
            _sortBtn.AutoWidth(52);
            _sortBtn.Location = new Point(_stackBtn.Location.X - S(5) - _sortBtn.Size.Width, S(y + 2));
            _sortBtn.Click += (o, e) => SortBag(false);

            ContentHeight = y + 26 + 12 - top;

            _goldText = string.Empty;
            _sortWhy = "-";
            _weightKey = -1;
            _bagWatch.Reset();
            _questWatch.Reset();
            ShowTab();
            _dirty = true;
            Render();
            UpdateBin();
        }

        private static Point CellAt(int p) => new Point(ModernRoot.S(p % Cols * (Cell + Gap)), ModernRoot.S(p / Cols * (Cell + Gap)));

        private void ShowTab()
        {
            bool quest = _tab == 1;
            _bagPage.Visible = !quest;
            _questPage.Visible = quest;
            ApplyFilter();
        }

        // ------------------------------------------------------------------ painting

        private static void PaintLock(Graphics g, PaintBox b)
        {
            W.Card(g, b.Size, b.Hovered ? ModernTheme.Accent : ModernTheme.Frame, 0f, true);
        }

        private static void PaintUnlock(Graphics g, PaintBox b, string text)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using var p = ModernSkin.RoundRect(r, ModernTheme.Radius * 0.8f * k);
            using (var br = new SolidBrush(W.Over(ModernTheme.PanelB, 0.88f)))
                g.FillPath(br, p);
            ModernSkin.Stroke(g, p, b.Hovered ? ModernTheme.Gold : ModernTheme.Mix(ModernTheme.Gold, ModernTheme.Frame, 0.7f));
            ModernSkin.Text(g, text, ModernSkin.Font(12, true), b.Hovered ? Color.White : ModernTheme.Gold, new RectangleF(0, 0, b.Size.Width, b.Size.Height), StringAlignment.Center);
        }

        private void PaintGold(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            bool on = GameScene.PickedUpGold;
            if (b.Hovered || on)
            {
                using var p = ModernSkin.RoundRect(new RectangleF(0.5f, 2.5f, b.Size.Width - 1f, b.Size.Height - 5f), ModernTheme.Radius * 0.8f * k);
                ModernSkin.Stroke(g, p, ModernTheme.Gold);
            }
            Font label = ModernSkin.Font(12, true);
            float lw = ModernSkin.Measure(W.T(ClientTextKeys.ModernWinGold), label);
            ModernSkin.Text(g, W.T(ClientTextKeys.ModernWinGold), label, ModernTheme.Dim, new RectangleF(4 * k, 0, lw + 2, b.Size.Height));
            ModernSkin.Text(g, _goldText, label, ModernTheme.Gold, new RectangleF(4 * k + lw + 5 * k, 0, b.Size.Width - lw - 10 * k, b.Size.Height));
        }

        private void PaintBin(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using var p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
            using (var br = new SolidBrush(ModernTheme.PanelB))
                g.FillPath(br, p);
            Color border = _dead ? ModernTheme.Frame : _deleteMode ? ModernTheme.Bad : b.Hovered ? ModernTheme.Accent : ModernTheme.Frame;
            ModernSkin.Stroke(g, p, border, _deleteMode ? 1.5f : 1f);
        }

        private void UpdateBin()
        {
            if (_bin == null || _bin.IsDisposed) return;
            _bin.AfterPaint = bx =>
            {
                int s = (int)(bx.Size.Width * 0.55f);
                Color c = _dead ? ModernSkin.Dim(ModernTheme.Dim, 0.5f) : _deleteMode ? ModernTheme.Bad : ModernTheme.Dim;
                W.DrawIcon(ModernIcons.Trash, new Rectangle(bx.DisplayLocation.X + (bx.Size.Width - s) / 2, bx.DisplayLocation.Y + (bx.Size.Height - s) / 2, s, s), c);
            };
            _bin.Hint = _dead ? W.T(ClientTextKeys.ModernWinBinDead) : _deleteMode ? W.T(ClientTextKeys.ModernWinDeleteModeOn) : W.T(ClientTextKeys.ModernWinBinHint);
            _bin.Invalidate();
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void Shown()
        {
            ItemMoves.SlotChanged.On(_slotChanged);
            ModernEvents.InventoryResized.On(_resized);
            ModernEvents.ItemInfo.On(_itemInfo);
            int len = User?.Inventory?.Length ?? -1;
            if (len > 0 && len != _builtLen) OnResized(); // the bag grew while the window was closed
            _dirty = true;
            Tick(true);
        }

        protected override void Closed()
        {
            ItemMoves.SlotChanged.Off(_slotChanged);
            ModernEvents.InventoryResized.Off(_resized);
            ModernEvents.ItemInfo.Off(_itemInfo);
            // InventoryDialog.Hide: the bag's pick and the picked gold go with it (belt picks stay: the belt is on the HUD)
            ItemSlotRef pick = ItemMoves.Selected;
            if (pick != null && pick.GridType == MirGridType.Inventory && pick.ItemSlot >= Belt) ItemMoves.Selected = null;
            GameScene.PickedUpGold = false;
            SetDeleteMode(false);
            ModernTooltip.HideAll();
            ClearWave();
        }

        private void OnResized()
        {
            int len = User?.Inventory?.Length ?? 0;
            int old = _builtLen;
            if (IsDisposed || len == old) return;
            Layout(); // the grid grows by a row
            if (old > 0 && len > old) StartWave(old, len);
        }

        public override void Process()
        {
            base.Process();
            if (User == null) return;
            Tick(false);
            if (_bagWatch.Poll(MirGridType.Inventory, User.Inventory, Belt) | _questWatch.Poll(MirGridType.QuestInventory, User.QuestInventory))
            {
                for (int i = 0; i < _cells.Count; i++) _cells[i].Refresh(false);
                for (int i = 0; i < _questCells.Count; i++) _questCells[i].Refresh(false);
                _dirty = true;
            }
            if (_dirty) Render();
            if (_waveFrom >= 0) StepWave();
        }

        /// <summary>The polled values (inventory.ts tick: weight, dead, the sort cooldown; gold and credit).</summary>
        private void Tick(bool force)
        {
            if (!force && CMain.Time < _nextTick) return;
            _nextTick = CMain.Time + 250;
            UserObject u = User;
            if (u == null) return;

            bool dead = u.Dead;
            if (dead != _dead)
            {
                _dead = dead;
                if (dead) SetDeleteMode(false);
                UpdateBin();
            }

            int max = u.Stats != null ? u.Stats[Stat.BagWeight] : 0, w = u.CurrentBagWeight;
            long key = ((long)w << 32) | (uint)max;
            if (key != _weightKey && _weightBar != null)
            {
                _weightKey = key;
                float f = max > 0 ? w / (float)max : 0;
                _weightBar.Colour = f >= 1 ? ModernTheme.Bad : f > 0.75f ? ModernTheme.Warn : Color.Empty;
                _weightBar.Set(Math.Min(1f, f));
                _weightText.Text = W.N(w) + " / " + W.N(max);
            }

            // strings are built only when the number changed (never per poll)
            if (GameScene.Gold != _goldShown || _goldText.Length == 0)
            {
                _goldShown = GameScene.Gold;
                _goldText = W.N(_goldShown);
                _gold?.Invalidate();
            }
            if (_creditText != null && (GameScene.Credit != _creditShown || _creditText.Text.Length == 0))
            {
                _creditShown = GameScene.Credit;
                _creditText.Text = W.N(_creditShown);
            }

            string why = SortGate.Why(MirGridType.Inventory, u.Inventory);
            if (why != _sortWhy && _sortBtn != null)
            {
                _sortWhy = why;
                _sortBtn.SetDisabled(why.Length > 0, why);
                _stackBtn.SetDisabled(why.Length > 0, why);
            }
        }

        /// <summary>Free cells, empties, the filter (inventory.ts render), once per change.</summary>
        private void Render()
        {
            _dirty = false;
            UserObject u = User;
            if (u == null || _freeText == null) return;
            UserItem[] list = u.Inventory;
            int free = 0;
            for (int s = Belt; s < list.Length; s++) if (list[s] == null) free++;
            _freeText.Text = free + " / " + Math.Max(0, list.Length - Belt);
            for (int i = 0; i < _decor.Count; i++) _decor[i].Sync();
            _gold?.Invalidate();
            ApplyFilter();
        }

        // ------------------------------------------------------------------ search / categories

        private bool Filtering => _query.Length > 0 || _cats.Count > 0;

        private string CategoryOf(ItemInfo info)
        {
            if (Array.IndexOf(Gear, info.Type) >= 0) return "gear";
            if (Array.IndexOf(Use, info.Type) >= 0) return "use";
            if (Array.IndexOf(Craft, info.Type) >= 0) return "craft";
            return "other";
        }

        private bool Matches(UserItem item)
        {
            ItemInfo info = item?.Info;
            if (info == null) return false;
            if (_cats.Count > 0 && !_cats.Contains(CategoryOf(info))) return false;
            return _query.Length == 0 || (info.Name ?? string.Empty).ToLowerInvariant().Contains(_query) || (item.FriendlyName ?? string.Empty).ToLowerInvariant().Contains(_query);
        }

        private void ApplyFilter()
        {
            if (_tabs == null || _tabs.IsDisposed) return;
            bool filtering = Filtering;
            int bagHits = Mark(_cells, _decor, filtering);
            int questHits = Mark(_questCells, _questDecor, filtering);
            _tabs.Badge(0, filtering && _tab == 1 ? bagHits : 0);
            _tabs.Badge(1, filtering && _tab == 0 ? questHits : 0);
        }

        private int Mark(List<ModernItemCell> cells, List<CellDecor> decor, bool filtering)
        {
            int hits = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                UserItem item = cells[i].Item;
                bool hit = filtering && item != null && Matches(item);
                if (hit) hits++;
                decor[i].SetMode(!filtering || item == null ? 0 : hit ? 1 : 2);
            }
            return hits;
        }

        // ------------------------------------------------------------------ @ADDINVENTORY

        private void AskUnlock()
        {
            UserObject u = User;
            Popups pop = ModernRoot.Instance?.Popups;
            if (u == null || pop == null) return;
            int len = u.Inventory.Length;
            if (len >= Belt + BagMax) return;
            int price = UnlockPrice(len), after = NextLength(len);
            string text = W.T(ClientTextKeys.ModernWinUnlockAsk, after - len, W.N(price)) +
                (after < Belt + BagMax ? W.T(ClientTextKeys.ModernWinUnlockNext, W.N(UnlockPrice(after))) : W.T(ClientTextKeys.ModernWinUnlockLast));
            pop.Ask(text, yes => { if (yes && !IsDisposed) Network.Enqueue(new C.Chat { Message = "@ADDINVENTORY" }); }, false, PopupKind.Question, W.T(ClientTextKeys.ModernWinUnlockTitle));
        }

        private void StartWave(int from, int to)
        {
            ClearWave();
            _waveFrom = from;
            _waveTo = to;
            _waveStart = CMain.Time;
        }

        private void StepWave()
        {
            long t = CMain.Time - _waveStart;
            bool any = false;
            for (int slot = _waveFrom; slot < _waveTo; slot++)
            {
                int p = slot - Belt;
                if (p < 0 || p >= _decor.Count) continue;
                long local = t - (slot - _waveFrom) * WaveStep;
                int level = 0;
                if (local >= 0 && local < WaveMs)
                {
                    float f = local / (float)WaveMs; // inv-glow: up to full at 15%, then fades out
                    level = (int)Math.Round(16 * (f < 0.15f ? f / 0.15f : 1f - (f - 0.15f) / 0.85f));
                    any = true;
                }
                else if (local < 0) any = true;
                _decor[p].SetGlow(level);
            }
            if (!any) ClearWave();
        }

        private void ClearWave()
        {
            if (_waveFrom < 0) return;
            for (int slot = _waveFrom; slot < _waveTo; slot++)
            {
                int p = slot - Belt;
                if (p >= 0 && p < _decor.Count) _decor[p].SetGlow(0);
            }
            _waveFrom = -1;
        }

        // ------------------------------------------------------------------ sort / delete

        private void SortBag(bool stack)
        {
            UserObject u = User;
            if (u == null) return;
            if (!SortGate.TrySort(MirGridType.Inventory, u.Inventory, stack))
                GameScene.Models.Chat.Add(SortGate.Why(MirGridType.Inventory, u.Inventory) is { Length: > 0 } why ? why : W.T(ClientTextKeys.ModernWinSortWait), ChatType.Hint);
            _sortWhy = "-";
            Tick(true);
        }

        private void BinClick()
        {
            if (_dead) return;
            ItemSlotRef pick = ItemMoves.Selected;
            if (pick != null && pick.GridType == MirGridType.Inventory && pick.Item != null)
            {
                AskDelete(pick.ItemSlot);
                return;
            }
            SetDeleteMode(!_deleteMode);
        }

        private void SetDeleteMode(bool on)
        {
            if (_deleteMode == on) return;
            _deleteMode = on;
            UpdateBin();
        }

        /// <summary>ModernUi.ItemDeleteModeClick: in delete mode a left click on a bag / belt item asks, a right click stops.</summary>
        internal bool DeleteModeClick(ItemSlotRef cell, MouseButtons button)
        {
            if (!_deleteMode || !IsOpen) return false;
            if (button == MouseButtons.Right)
            {
                SetDeleteMode(false);
                return true;
            }
            if (button == MouseButtons.Left && cell.GridType == MirGridType.Inventory && cell.Item != null) AskDelete(cell.ItemSlot);
            return true;
        }

        /// <summary>inventory.ts askDelete: an amount first for a stack, then a hold-to-confirm question; nothing is sent when the
        /// item moved, got locked or you died in between. C.DeleteItem as the classic bin sends it (no lock: S.DeleteItem's handler
        /// clears none).</summary>
        private void AskDelete(int slot)
        {
            UserObject u = User;
            Popups pop = ModernRoot.Instance?.Popups;
            if (u == null || pop == null || slot < 0 || slot >= u.Inventory.Length) return;
            UserItem item = u.Inventory[slot];
            if (item == null || item.Info == null) return;
            ulong id = item.UniqueID;
            string name = item.FriendlyName;
            ItemMoves.Selected = null;
            SetDeleteMode(false);

            UserItem Still()
            {
                UserObject now = User;
                if (now == null || slot >= now.Inventory.Length || now.Dead) return null;
                UserItem it = now.Inventory[slot];
                return it != null && it.UniqueID == id && !ItemMoves.IsLocked(MirGridType.Inventory, slot) ? it : null;
            }

            void Confirm(ushort count)
            {
                string label = count > 1 ? name + " (" + W.N(count) + ")" : name;
                pop.Confirm(W.T(ClientTextKeys.ModernWinDeleteAsk, label), PopupButtons.YesNo, PopupKind.Danger, (a, _) =>
                {
                    if (a != PopupAnswer.Yes || IsDisposed) return;
                    UserItem now = Still();
                    if (now == null)
                    {
                        GameScene.Models.Chat.Add(W.T(ClientTextKeys.ModernWinNoLongerThere), ChatType.Hint);
                        return;
                    }
                    Network.Enqueue(new C.DeleteItem { UniqueID = id, Count = (ushort)Math.Min(count, now.Count), HeroInventory = false });
                }, false, W.T(ClientTextKeys.ModernWinDeleteTitle), 600);
            }

            if (item.Count <= 1)
            {
                Confirm(1);
                return;
            }
            pop.Amount(W.T(ClientTextKeys.ModernWinDeleteHowMany), item.Info.Image, item.Count, n =>
            {
                if (n == 0 || IsDisposed) return;
                UserItem now = Still();
                if (now != null) Confirm((ushort)Math.Min(n, now.Count));
            }, item.Count);
        }
    }
}

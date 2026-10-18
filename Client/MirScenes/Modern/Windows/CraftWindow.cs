// Ported from WebClient/src/ui/modern/windows/craft.ts (CraftWindow): the new UI's crafting window. Desktop counterpart:
// NPCCraftGoodsDialog + CraftDialog (Client/MirScenes/Dialogs/NPCDialogs.cs) + GameScene.NPCGoods (PanelType.Craft) / CraftItem.
// Server rules (NPCScript.Craft): C.CraftItem { UniqueID of the recipe's goods item, Count, Slots } where Slots are the bag slots of
// the tools, then the ingredients, in recipe order. Each tool needs floor(CurrentDura / 1000) >= Count; each ingredient comes from
// ONE bag stack holding Count x the recipe amount (and its purity when the recipe asks for one); Count is capped by the result's
// StackSize / amount and each ingredient's StackSize / amount; Gold x Count. One roll per batch (Chance + Stat.CraftRatePercent).
//  - opens only when the server lists this NPC's recipes (S.NPCGoods PanelType.Craft while the NPC page is open) and closes with
//    the NPC session or the next page; recipes = the craft goods + GameScene.RecipeInfoList; the slots are chosen like
//    CraftDialog.AutoFill (the fullest tool / biggest stack), shown through Models.Npc.CraftItems / CraftShadowItems;
//  - Craft (an OK / Cancel question below 100%) sends C.CraftItem and locks those bag cells until S.CraftItem (CraftItemDone; a
//    refusal sends nothing back, so the locks lift after 8 s); the outcome = whether the result item arrived in the bag.
// HIDDEN (no data): locked recipes, "find on the market", the mock's crafting bar and sparks.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;
using Client.MirSounds;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class CraftWindow : ModernWindow
    {
        public const string WindowKey = "craft";
        private const int WidthHud = 960, HeightHud = 530, CatsW = 160, DetW = 330, MaxCraft = 99, AnswerMs = 8000, LogMax = 20, ToolSlots = 3;
        private const int GridX = CatsW, GridW = WidthHud - CatsW - DetW, DetX = WidthHud - DetW;

        private static readonly (string Name, ClientTextKeys Label)[] Cats =
        {
            ("All", ClientTextKeys.ModernWinCatAll), ("Potions", ClientTextKeys.ModernWinCatPotions), ("Gems", ClientTextKeys.ModernNpcCatGems),
            ("Jewellery", ClientTextKeys.ModernWinCatJewellery), ("Gear", ClientTextKeys.ModernNpcCatGear), ("Materials", ClientTextKeys.ModernWinCatMaterials),
            ("Other", ClientTextKeys.ModernWinCatOther),
        };

        /// <summary>One tool or ingredient and the bag slot chosen for it (-1 = none).</summary>
        private sealed class Need
        {
            public UserItem Shadow;
            public int Slot = -1, Have, Total;
            public string Why = string.Empty;
        }

        private sealed class Plan
        {
            public readonly List<Need> Tools = new List<Need>(), Ings = new List<Need>();
            public int[] Slots = Array.Empty<int>();
            public int Max;
            public string Why = string.Empty;
        }

        private readonly NpcState _state;
        private string _cat = "All", _query = string.Empty;
        private bool _canOnly, _dirty = true;
        private ulong _sel;
        private int _qty = 1, _shownSeq = -1;
        // the request out
        private bool _pending;
        private int[] _pendSlots = Array.Empty<int>();
        private int _pendIndex, _pendBefore, _pendCount;
        private string _pendName = string.Empty;
        private long _pendUntil;
        private string _result = string.Empty, _resultKind = string.Empty;
        private readonly List<(bool Ok, string Text)> _log = new List<(bool, string)>();
        private int _visitDone, _visitOk;
        private string _catsKey = string.Empty, _gridKey = string.Empty, _detailKey = string.Empty;
        private readonly List<Plan> _plans = new List<Plan>();

        private ModernPanel _catBox;
        private ScrollView _grid;
        private ModernPanel _det, _none;
        private ModernLabel _noneText, _resName, _resSub, _resGold, _ingsFor, _qtyLabel, _makes, _costs, _resultLabel, _whyLabel, _footGold, _footRate, _footVisit, _footLast;
        private NpcItemCell _resCell;
        private ProgressRing _ring;
        private ModernLabel _toolsLbl;
        private ModernPanel _toolsBox, _ingsBox, _lower;
        private ModernButton _minus, _plus, _max, _go;
        private readonly GridWatch _bagWatch = new GridWatch();
        private readonly Action _changed;
        private readonly Action<ItemSlotKey> _slotChanged;

        public CraftWindow(NpcState state) : base(WindowKey, W.T(ClientTextKeys.ModernNpcCrafting), 24, 24)
        {
            _state = state;
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
            _changed = () => _dirty = true;
            _slotChanged = k => { if (k.Grid == MirGridType.Inventory || k.Grid == MirGridType.None) _dirty = true; };
        }

        internal bool Pending => _pending;
        internal string ResultText => _result;
        internal int Quantity => _qty;
        internal ModernButton CraftButton => _go;
        internal ScrollView Grid => _grid;
        internal ModernButton MaxButton => _max;

        private static UserObject User => GameScene.User;
        private static int Rate => User?.Stats[Stat.CraftRatePercent] ?? 0;

        protected override void Build()
        {
            int top = TitleHeight;
            // ---- categories column
            var search = W.At(new SearchBox(CatsW - 16, W.T(ClientTextKeys.ModernNpcSearchRecipes)), this, 8, top + 8);
            if (_query.Length > 0) search.Text = _query;
            search.Search += q => { _query = q.ToLowerInvariant(); _dirty = true; };
            _catBox = new ModernPanel { Parent = this, Location = new Point(S(8), S(top + 40)), Size = new Size(S(CatsW - 16), S(7 * 26)) };
            var can = W.At(new ToggleSwitch(_canOnly, true), this, 8, top + 236);
            can.Changed += on => { _canOnly = on; _dirty = true; };
            W.Label(this, W.T(ClientTextKeys.ModernNpcCanCraftNow), 8 + 36, top + 234, CatsW - 52, 20, 11, false, ModernTheme.Text);
            W.VDivider(this, CatsW, top, HeightHud - 30);

            // ---- recipe grid
            _grid = new ScrollView(GridW - 1, HeightHud - 31) { Parent = this, Location = new Point(S(GridX + 1), S(top)), Background = W.Page };
            _grid.Framed = false;
            W.VDivider(this, DetX, top, HeightHud - 30);

            // ---- detail
            _none = new ModernPanel { Parent = this, Location = new Point(S(DetX + 1), S(top)), Size = new Size(S(DetW - 1), S(HeightHud - 31)) };
            _noneText = W.Label(_none, W.T(ClientTextKeys.ModernNpcPickRecipe), 0, 200, DetW, 20, 12, false, ModernTheme.Dim, StringAlignment.Center);
            _det = new ModernPanel { Parent = this, Location = new Point(S(DetX + 1), S(top)), Size = new Size(S(DetW - 1), S(HeightHud - 31)), Visible = false };
            MirControl d = _det;
            int dw = DetW - 28;
            _resCell = new NpcItemCell(56, () => SelectedGoods()) { Parent = d, Location = new Point(S(14), S(12)) };
            _resName = W.Label(d, string.Empty, 80, 14, dw - 140, 20, 15, true, Color.White);
            _resSub = W.Label(d, string.Empty, 80, 36, dw - 140, 14, 10.5f, false, ModernTheme.Dim);
            _resGold = W.Label(d, string.Empty, 80, 52, dw - 140, 14, 10.5f, false, ModernTheme.Dim);
            _ring = W.At(new ProgressRing(62, 6), d, DetW - 76, 8);
            _ring.Hint = W.T(ClientTextKeys.ModernNpcChanceHint);
            _toolsLbl = W.Caption(d, W.T(ClientTextKeys.ModernNpcTools) + "  ·  " + W.T(ClientTextKeys.ModernNpcToolDura), 14, 80, dw);
            _toolsBox = new ModernPanel { Parent = d, Location = new Point(S(14), S(98)), Size = new Size(S(dw), S(10)) };
            _lower = new ModernPanel { Parent = d, Location = new Point(0, S(120)), Size = new Size(S(DetW - 1), S(600)) };
            MirControl l = _lower;
            W.Caption(l, W.T(ClientTextKeys.ModernNpcIngredients), 14, 0, 140);
            _ingsFor = W.Label(l, string.Empty, 150, 0, dw - 136, 14, 10.5f, true, ModernTheme.Text, StringAlignment.Far);
            _ingsBox = new ModernPanel { Parent = l, Location = new Point(S(14), S(18)), Size = new Size(S(dw), S(10)) };
            _shownSeq = -1;
            BuildFoot(top);
            _dirty = true;
            _catsKey = _gridKey = _detailKey = string.Empty;
            Render();
        }

        private void BuildFoot(int top)
        {
            W.Divider(this, 0, top + HeightHud - 30, WidthHud);
            int y = top + HeightHud - 24;
            W.Label(this, W.T(ClientTextKeys.ModernWinGold), 14, y, 40, 18, 11, false, ModernTheme.Dim);
            _footGold = W.Label(this, string.Empty, 50, y, 120, 18, 11, true, ModernTheme.Gold);
            W.Label(this, W.T(ClientTextKeys.ModernNpcRateBonus), 170, y, 120, 18, 11, false, ModernTheme.Dim);
            _footRate = W.Label(this, string.Empty, 290, y, 60, 18, 11, true, Color.White);
            W.Label(this, W.T(ClientTextKeys.ModernNpcThisVisit), 360, y, 80, 18, 11, false, ModernTheme.Dim);
            _footVisit = W.Label(this, string.Empty, 440, y, 160, 18, 11, true, Color.White);
            _footLast = W.Label(this, string.Empty, 600, y, WidthHud - 614, 18, 11, false, ModernTheme.Dim, StringAlignment.Far);
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void Shown()
        {
            _state.Changed += _changed;
            ItemMoves.SlotChanged.On(_slotChanged);
            _bagWatch.Reset();
            _dirty = true;
            Render();
        }

        protected override void Closed()
        {
            _state.Changed -= _changed;
            ItemMoves.SlotChanged.Off(_slotChanged);
            Release(); // a request still out: its cells unlock (the late answer is ignored)
            ClearModel();
        }

        public override void Process()
        {
            base.Process();
            if (User == null) return;
            if (_bagWatch.Poll(MirGridType.Inventory, User.Inventory)) _dirty = true;
            if (_pending && CMain.Time >= _pendUntil)
            {
                Release();
                _resultKind = "refused";
                _result = W.T(ClientTextKeys.ModernNpcSeeChat);
                _dirty = true;
            }
            if (_dirty) Render();
        }

        // ------------------------------------------------------------------ data

        private List<UserItem> Goods => _state.CraftGoods;

        private static ClientRecipeInfo Recipe(UserItem goods)
        {
            if (goods == null) return null;
            foreach (ClientRecipeInfo r in GameScene.RecipeInfoList) if (r.Item != null && r.Item.ItemIndex == goods.ItemIndex) return r;
            return null;
        }

        private UserItem SelectedGoods()
        {
            foreach (UserItem g in Goods) if (g.UniqueID == _sel) return g;
            return null;
        }

        private static string CatOf(ItemInfo info)
        {
            if (info == null) return "Other";
            switch (info.Type)
            {
                case ItemType.Potion: return "Potions";
                case ItemType.Gem: return "Gems";
                case ItemType.Necklace:
                case ItemType.Ring:
                case ItemType.Bracelet: return "Jewellery";
                case ItemType.Weapon:
                case ItemType.Armour:
                case ItemType.Helmet:
                case ItemType.Belt:
                case ItemType.Boots:
                case ItemType.Torch:
                case ItemType.Stone: return "Gear";
                case ItemType.CraftingMaterial:
                case ItemType.Ore:
                case ItemType.Meat: return "Materials";
            }
            return "Other";
        }

        private static int Chance(ClientRecipeInfo r) => Math.Max(0, Math.Min(100, r.Chance + Rate));

        private static ItemInfo InfoOf(UserItem shadow)
        {
            if (shadow == null) return null;
            if (shadow.Info != null) return shadow.Info;
            foreach (ItemInfo i in GameScene.ItemInfoList) if (i.Index == shadow.ItemIndex) return i;
            return null;
        }

        /// <summary>CraftDialog.AutoFill + HasCraftItems + CraftItem's maximum, over the bag as it is now.</summary>
        private Plan MakePlan(UserItem goods, ClientRecipeInfo r)
        {
            var plan = new Plan();
            UserItem[] bag = User.Inventory;
            var used = new HashSet<int>();
            int max = MaxCraft;
            string why = string.Empty;
            bool Locked(int i) => ItemMoves.IsLocked(MirGridType.Inventory, i) && !(_pending && Array.IndexOf(_pendSlots, i) >= 0);
            foreach (UserItem tool in r.Tools)
            {
                int best = -1;
                bool any = false;
                for (int i = 0; i < bag.Length; i++)
                {
                    UserItem s = bag[i];
                    if (s == null || s.ItemIndex != tool.ItemIndex || used.Contains(i) || Locked(i)) continue;
                    any = true;
                    if (s.CurrentDura < 1000) continue;
                    if (best < 0 || s.CurrentDura > bag[best].CurrentDura) best = i;
                }
                var need = new Need { Shadow = tool, Slot = best, Have = best >= 0 ? bag[best].CurrentDura / 1000 : 0 };
                need.Total = need.Have;
                need.Why = best < 0 ? W.T(any ? ClientTextKeys.ModernNpcWornOut : ClientTextKeys.ModernNpcNoTool) : string.Empty;
                if (best >= 0) used.Add(best);
                plan.Tools.Add(need);
                max = Math.Min(max, need.Have);
                if (need.Why.Length > 0 && why.Length == 0) why = W.T(ClientTextKeys.ModernNpcNeedTool, NpcFmt.ItemName(tool));
            }
            foreach (UserItem ing in r.Ingredients)
            {
                int best = -1, total = 0;
                bool pure = false;
                int minPurity = ing.CurrentDura < ing.MaxDura ? ing.CurrentDura : -1;
                for (int i = 0; i < bag.Length; i++)
                {
                    UserItem s = bag[i];
                    if (s == null || s.ItemIndex != ing.ItemIndex || used.Contains(i) || Locked(i)) continue;
                    total += s.Count;
                    if (minPurity >= 0 && s.CurrentDura < minPurity) continue;
                    pure = true;
                    if (s.Count < ing.Count) continue;
                    if (best < 0 || s.Count > bag[best].Count) best = i;
                }
                var need = new Need { Shadow = ing, Slot = best, Have = best >= 0 ? bag[best].Count : 0, Total = total };
                ClientTextKeys? miss = best >= 0 ? null : total == 0 ? ClientTextKeys.ModernNpcNoneInBag : !pure ? ClientTextKeys.ModernNpcPurityLow : ClientTextKeys.ModernNpcOneStack;
                need.Why = miss.HasValue ? W.T(miss.Value) : string.Empty;
                if (best >= 0) used.Add(best);
                plan.Ings.Add(need);
                max = Math.Min(max, need.Have / Math.Max(1, (int)ing.Count));
                ItemInfo ii = InfoOf(ing);
                if (ii != null && ii.StackSize > 0) max = Math.Min(max, ii.StackSize / Math.Max(1, (int)ing.Count));
                if (miss.HasValue && why.Length == 0)
                    why = miss == ClientTextKeys.ModernNpcOneStack ? W.T(ClientTextKeys.ModernNpcMergeStacks, NpcFmt.ItemName(ing))
                        : miss == ClientTextKeys.ModernNpcPurityLow ? W.T(ClientTextKeys.ModernNpcNotPure, NpcFmt.ItemName(ing)) : W.T(ClientTextKeys.YouDoNotHaveRequiredToolsOrIngredients);
            }
            ItemInfo ri = goods.Info;
            if (ri != null && ri.StackSize > 0) max = Math.Min(max, Math.Min(ri.StackSize, ri.StackSize / Math.Max(1, (int)goods.Count)));
            if (r.Gold > 0)
            {
                int afford = (int)Math.Min(int.MaxValue, GameScene.Gold / r.Gold);
                if (afford == 0 && why.Length == 0) why = W.T(ClientTextKeys.YouDoNotHaveEnoughGold);
                max = Math.Min(max, afford);
            }
            max = Math.Max(0, max);
            if (max == 0 && why.Length == 0) why = W.T(ClientTextKeys.YouDoNotHaveRequiredToolsOrIngredients);
            bool ok = true;
            foreach (Need n in plan.Tools) if (n.Slot < 0) ok = false;
            foreach (Need n in plan.Ings) if (n.Slot < 0) ok = false;
            if (ok)
            {
                var slots = new List<int>();
                foreach (Need n in plan.Tools) slots.Add(n.Slot);
                foreach (Need n in plan.Ings) slots.Add(n.Slot);
                plan.Slots = slots.ToArray();
            }
            plan.Max = ok ? max : 0;
            plan.Why = plan.Max > 0 ? string.Empty : why;
            return plan;
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            _dirty = false;
            if (_grid == null || _grid.IsDisposed || User == null) return;
            List<UserItem> goods = Goods;
            if (_shownSeq != _state.CraftSeq)
            {
                // a new list = a new visit
                _shownSeq = _state.CraftSeq;
                _sel = 0;
                _qty = 1;
                _result = _resultKind = string.Empty;
                _log.Clear();
                _visitDone = _visitOk = 0;
                _catsKey = _gridKey = _detailKey = string.Empty;
            }
            string npc = _state.Name.Name;
            SetTitle(npc.Length > 0 ? W.T(ClientTextKeys.ModernNpcCrafting) + " · " + npc.ToUpperInvariant() : W.T(ClientTextKeys.ModernNpcCrafting));
            _plans.Clear();
            foreach (UserItem g in goods) { ClientRecipeInfo r = Recipe(g); _plans.Add(r != null ? MakePlan(g, r) : null); }
            bool found = false;
            foreach (UserItem g in goods) if (g.UniqueID == _sel) found = true;
            if (!found) _sel = goods.Count > 0 ? goods[0].UniqueID : 0;
            RenderCats(goods);
            RenderGrid(goods);
            RenderDetail(goods);
            _footGold.Text = W.N(GameScene.Gold);
            _footRate.Text = Rate > 0 ? "+" + Rate + "%" : W.T(ClientTextKeys.ModernNpcNone);
            _footVisit.Text = _visitDone > 0 ? W.T(ClientTextKeys.ModernNpcSucceeded, _visitOk, _visitDone) : W.T(ClientTextKeys.ModernNpcNoCrafts);
            _footLast.Text = _log.Count > 0 ? _log[0].Text : string.Empty;
        }

        private void RenderCats(List<UserItem> goods)
        {
            int[] n = new int[Cats.Length], can = new int[Cats.Length];
            for (int i = 0; i < goods.Count; i++)
            {
                bool c = (_plans[i]?.Max ?? 0) > 0;
                string cat = CatOf(goods[i].Info);
                for (int j = 0; j < Cats.Length; j++)
                    if (j == 0 || Cats[j].Name == cat) { n[j]++; if (c) can[j]++; }
            }
            string key = _cat + "|" + string.Join(",", n) + "|" + string.Join(",", can);
            if (key == _catsKey) return;
            _catsKey = key;
            for (int i = _catBox.Controls.Count - 1; i >= 0; i--) _catBox.Controls[i].Dispose();
            int y = 0;
            for (int j = 0; j < Cats.Length; j++)
            {
                if (j > 0 && n[j] == 0) continue;
                string name = Cats[j].Name, label = W.T(Cats[j].Label), count = (can[j] > 0 ? can[j] + " " : string.Empty) + "/ " + n[j];
                bool hasCan = can[j] > 0;
                var row = new PaintBox((g, b) =>
                {
                    float k = ModernRoot.K;
                    W.Row(g, b.Size, false, b.Hovered, name == _cat);
                    ModernSkin.Text(g, label, ModernSkin.Font(12), name == _cat ? Color.White : ModernTheme.Text, new RectangleF(10 * k, 0, b.Size.Width * 0.6f, b.Size.Height));
                    ModernSkin.Text(g, count, ModernSkin.Font(10, hasCan), hasCan ? ModernTheme.Ok : ModernTheme.Dim, new RectangleF(0, 0, b.Size.Width - 8 * k, b.Size.Height), StringAlignment.Far);
                })
                { Parent = _catBox, Location = new Point(0, S(y)), Size = new Size(_catBox.Size.Width, S(24)), Hint = W.T(ClientTextKeys.ModernNpcCanCraftN, can[j]), Sound = SoundList.ButtonA };
                row.Click += (o, e) => { if (_cat != name) { _cat = name; _dirty = true; } };
                y += 26;
            }
        }

        private void RenderGrid(List<UserItem> goods)
        {
            var list = new List<int>();
            for (int i = 0; i < goods.Count; i++)
            {
                UserItem g = goods[i];
                if (_cat != "All" && CatOf(g.Info) != _cat) continue;
                ClientRecipeInfo r = Recipe(g);
                if (_query.Length > 0 && !NpcFmt.ItemName(g).ToLowerInvariant().Contains(_query))
                {
                    bool ing = false;
                    if (r != null) foreach (UserItem x in r.Ingredients) if (NpcFmt.ItemName(x).ToLowerInvariant().Contains(_query)) ing = true;
                    if (!ing) continue;
                }
                if (_canOnly && (_plans[i]?.Max ?? 0) <= 0) continue;
                list.Add(i);
            }
            list.Sort((a, b) =>
            {
                int ca = (_plans[a]?.Max ?? 0) > 0 ? 1 : 0, cb = (_plans[b]?.Max ?? 0) > 0 ? 1 : 0;
                if (ca != cb) return cb - ca;
                return string.Compare(NpcFmt.ItemName(goods[a]), NpcFmt.ItemName(goods[b]), StringComparison.CurrentCulture);
            });
            var sb = new System.Text.StringBuilder();
            foreach (int i in list) sb.Append(goods[i].UniqueID).Append(':').Append(_plans[i]?.Max ?? -1).Append(':').Append(Recipe(goods[i])?.Chance ?? -1).Append(',');
            string key = sb + "|" + _sel + "|" + Rate;
            if (key == _gridKey) return;
            _gridKey = key;
            int keep = _grid.ScrollY;
            for (int i = _grid.Content.Controls.Count - 1; i >= 0; i--) _grid.Content.Controls[i].Dispose();
            int inner = (int)Math.Round(_grid.InnerWidth / ModernRoot.K) - 16, cols = Math.Max(1, inner / 150), cw = (inner - (cols - 1) * 6) / cols, n = 0;
            foreach (int i in list)
            {
                UserItem g = goods[i];
                ClientRecipeInfo r = Recipe(g);
                Plan plan = _plans[i];
                string nm = NpcFmt.ItemName(g) + (g.Count > 1 ? " ×" + g.Count : string.Empty);
                string status = r == null ? W.T(ClientTextKeys.ModernNpcLoadingRecipe) : plan != null && plan.Max > 0 ? W.T(ClientTextKeys.ModernNpcCanMake, plan.Max) : W.T(ClientTextKeys.ModernNpcMissing);
                Color sc = r == null ? ModernTheme.Dim : plan != null && plan.Max > 0 ? ModernTheme.Ok : ModernTheme.BadSoft;
                int ch = r != null ? Chance(r) : -1;
                bool sel = g.UniqueID == _sel;
                var card = new PaintBox((gr, b) =>
                {
                    float k = ModernRoot.K;
                    RectangleF rr = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
                    using GraphicsPath p = ModernSkin.RoundRect(rr, ModernTheme.Radius * k);
                    using (var br = new SolidBrush(sel ? W.Over(ModernTheme.Accent, 0.14f) : W.Over(ModernTheme.FrameLight, 0.05f))) gr.FillPath(br, p);
                    ModernSkin.Stroke(gr, p, sel ? ModernTheme.Accent : b.Hovered ? W.Over(ModernTheme.Accent, 0.6f) : W.Over(ModernTheme.Frame, 0.55f));
                    float tx = 54 * k, tw = b.Size.Width - tx - 6 * k;
                    ModernSkin.Text(gr, nm, ModernSkin.Font(12, true), Color.White, new RectangleF(tx, 14 * k, tw, 16 * k));
                    ModernSkin.Text(gr, status, ModernSkin.Font(10), sc, new RectangleF(tx, 31 * k, tw, 14 * k));
                    if (ch >= 0)
                    {
                        string t = ch + "%";
                        Font f = ModernSkin.Font(9, true);
                        float w = ModernSkin.Measure(t, f) + 10 * k;
                        RectangleF cr = new RectangleF(b.Size.Width - w - 6 * k, 4 * k, w, 12 * k);
                        using GraphicsPath cp = ModernSkin.RoundRect(cr, 6 * k);
                        using (var br = new SolidBrush(ch >= 100 ? ModernTheme.Ok : ch >= 50 ? ModernTheme.Gold : ModernTheme.BadSoft)) gr.FillPath(br, cp);
                        ModernSkin.Text(gr, t, f, Color.Black, cr, StringAlignment.Center);
                    }
                })
                {
                    Parent = _grid.Content,
                    Location = new Point(S(8 + n % cols * (cw + 6)), S(10 + n / cols * 60)),
                    Size = new Size(S(cw), S(54)),
                    Sound = SoundList.ButtonA,
                };
                var cell = new NpcItemCell(40, () => g) { Parent = card, Location = new Point(S(7), S(7)) };
                ulong id = g.UniqueID;
                void Pick()
                {
                    if (_sel == id) return;
                    _sel = id;
                    _qty = 1;
                    _result = _resultKind = string.Empty;
                    _dirty = true;
                }
                card.Click += (o, e) => Pick();
                cell.Click += (o, e) => Pick();
                n++;
            }
            if (n == 0) W.Label(_grid.Content, W.T(goods.Count > 0 ? ClientTextKeys.ModernNpcNoRecipesMatch : ClientTextKeys.ModernNpcNoRecipes), 0, 30, inner + 16, 20, 12, false, ModernTheme.Dim, StringAlignment.Center);
            _grid.ContentHeight = S(10 + (n + cols - 1) / cols * 60 + 6);
            _grid.ScrollTo(keep);
        }

        private void RenderDetail(List<UserItem> goods)
        {
            int i = -1;
            for (int j = 0; j < goods.Count; j++) if (goods[j].UniqueID == _sel) i = j;
            UserItem g = i >= 0 ? goods[i] : null;
            ClientRecipeInfo r = Recipe(g);
            Plan plan = i >= 0 ? _plans[i] : null;
            bool show = g != null && r != null && plan != null;
            _none.Visible = !show;
            _det.Visible = show;
            if (!show)
            {
                _noteNone(g != null ? W.T(ClientTextKeys.ModernNpcRecipeNotYet) : W.T(ClientTextKeys.ModernNpcPickRecipe));
                ClearModel();
                return;
            }
            int max = plan.Max;
            _qty = Math.Max(1, Math.Min(_qty, Math.Max(1, max)));
            int q = _qty;
            int chance = Chance(r);
            _resCell.Refresh();
            _resName.Text = NpcFmt.ItemName(g);
            _resSub.Text = W.T(Array.Find(Cats, c => c.Name == CatOf(g.Info)).Label) + (g.Count > 1 ? " · " + W.T(ClientTextKeys.ModernNpcMakesN, g.Count) : string.Empty);
            _resGold.Text = r.Gold > 0 ? W.T(ClientTextKeys.ModernNpcGoldPerCraft, W.N(r.Gold)) : W.T(ClientTextKeys.ModernNpcNoGoldCost);
            _ring.Set(chance / 100f, chance + "%", W.T(ClientTextKeys.ModernNpcSuccess));
            _ring.Hint = Rate > 0 ? W.T(ClientTextKeys.ModernNpcChanceRate, r.Chance, Rate) : W.T(ClientTextKeys.ModernNpcChanceRecipe, r.Chance);
            FillModel(r, plan);

            var sb = new System.Text.StringBuilder().Append(g.UniqueID).Append('|').Append(q).Append('|').Append(_pending).Append('|').Append(_result).Append('|');
            foreach (Need n in plan.Tools) sb.Append(n.Slot).Append(':').Append(n.Have).Append(',');
            foreach (Need n in plan.Ings) sb.Append(n.Slot).Append(':').Append(n.Have).Append(':').Append(n.Total).Append(',');
            sb.Append(max).Append('|').Append(GameScene.Gold);
            string key = sb.ToString();
            if (key == _detailKey) return;
            _detailKey = key;
            int dw = DetW - 28;
            // tools
            for (int k = _toolsBox.Controls.Count - 1; k >= 0; k--) _toolsBox.Controls[k].Dispose();
            int ty = 0;
            foreach (Need t in plan.Tools) { NeedRow(_toolsBox, t, ty, dw, q, true); ty += 44; }
            _toolsLbl.Visible = _toolsBox.Visible = plan.Tools.Count > 0;
            _toolsBox.Size = new Size(_toolsBox.Size.Width, Math.Max(1, S(ty)));
            _lower.Location = new Point(0, plan.Tools.Count > 0 ? _toolsBox.Location.Y + S(ty + 6) : S(80));
            // ingredients
            for (int k = _ingsBox.Controls.Count - 1; k >= 0; k--) _ingsBox.Controls[k].Dispose();
            int iy = 0;
            foreach (Need n in plan.Ings) { NeedRow(_ingsBox, n, iy, dw, q, false); iy += 44; }
            _ingsBox.Size = new Size(_ingsBox.Size.Width, Math.Max(1, S(iy)));
            _ingsFor.Text = W.T(q == 1 ? ClientTextKeys.ModernNpcForOneCraft : ClientTextKeys.ModernNpcForNCrafts, q);
            // quantity, totals, the button
            for (int k = _lower.Controls.Count - 1; k >= 0; k--)
                if (_lower.Controls[k].Location.Y > _ingsBox.Location.Y + S(2)) _lower.Controls[k].Dispose();
            int y = 18 + iy + 8;
            W.Caption(_lower, W.T(ClientTextKeys.ModernNpcQuantity), 14, y, 120);
            y += 18;
            _minus = W.At(new ModernButton("−") { Tooltip = W.T(ClientTextKeys.ModernNpcOneLess) }, _lower, 14, y);
            _minus.Size = new Size(S(26), S(26));
            _minus.Click += (o, e) => { _qty--; _dirty = true; };
            _qtyLabel = W.Label(_lower, q.ToString(), 44, y, 50, 26, 14, true, Color.White, StringAlignment.Center);
            _plus = W.At(new ModernButton("+") { Tooltip = max <= 1 ? string.Empty : W.T(ClientTextKeys.ModernNpcAtMost, max) }, _lower, 98, y);
            _plus.Size = new Size(S(26), S(26));
            _plus.Click += (o, e) => { _qty++; _dirty = true; };
            _max = W.At(new ModernButton(W.T(ClientTextKeys.ModernNpcMaxN, max)) { Tooltip = W.T(ClientTextKeys.ModernNpcMaxHint) }, _lower, 130, y);
            _max.AutoWidth(50);
            _max.Size = new Size(_max.Size.Width, S(26));
            _max.Click += (o, e) => { _qty = MaxCraft; _dirty = true; };
            _makes = W.Label(_lower, W.T(ClientTextKeys.ModernNpcMakes, W.N(g.Count * q)), 200, y - 2, dw - 186, 14, 10.5f, false, ModernTheme.Dim, StringAlignment.Far);
            _costs = W.Label(_lower, W.T(ClientTextKeys.ModernNpcCosts, W.N(r.Gold * (uint)q)), 200, y + 13, dw - 186, 14, 10.5f, true, ModernTheme.Gold, StringAlignment.Far);
            _minus.SetDisabled(_pending || q <= 1);
            _plus.SetDisabled(_pending || q >= max);
            _max.SetDisabled(_pending || max <= 1);
            y += 36;
            _go = W.At(new ModernButton(_pending ? W.T(ClientTextKeys.ModernNpcCraftingBusy) : W.T(ClientTextKeys.ModernNpcCraftN, q), ButtonKind.Main, true), _lower, 14, y);
            _go.Size = new Size(S(dw), S(38));
            _go.FontSize = 14;
            _go.SetDisabled(_pending || max <= 0, _pending ? W.T(ClientTextKeys.ModernNpcWaitingCraftsman) : plan.Why);
            _go.Click += (o, e) => DoCraft();
            y += 44;
            _resultLabel = W.Label(_lower, _result, 14, y, dw, 16, 11, _resultKind != "ok" && _resultKind.Length > 0,
                _resultKind == "ok" ? ModernTheme.Ok : ModernTheme.BadSoft, StringAlignment.Center);
            y += 18;
            string why = _pending ? string.Empty : max <= 0 ? plan.Why : chance < 100 ? W.T(ClientTextKeys.ModernNpcFailChance, 100 - chance) : string.Empty;
            _whyLabel = W.Label(_lower, why, 14, y, dw, 34, 10.5f, false, max > 0 ? ModernTheme.Dim : ModernTheme.BadSoft, StringAlignment.Center, true);
        }

        private void _noteNone(string text) => _noneText.Text = text;

        private void NeedRow(ModernPanel box, Need n, int y, int w, int q, bool tool)
        {
            UserItem bagItem = n.Slot >= 0 ? User.Inventory[n.Slot] : null;
            bool ok = tool ? n.Slot >= 0 && n.Have >= q : n.Slot >= 0 && n.Have >= n.Shadow.Count * q;
            string nm = NpcFmt.ItemName(n.Shadow);
            string note;
            if (tool) note = n.Slot >= 0 ? string.Empty : n.Why;
            else
            {
                var parts = new List<string>();
                if (n.Shadow.CurrentDura < n.Shadow.MaxDura) parts.Add(W.T(ClientTextKeys.ModernNpcPurity, W.N1(n.Shadow.CurrentDura / 1000.0)));
                if (n.Slot < 0) parts.Add(n.Why);
                else if (n.Total > n.Have) parts.Add(W.T(ClientTextKeys.ModernNpcInBagOneStack, W.N(n.Total)));
                note = string.Join(" · ", parts);
            }
            string right = tool ? (n.Slot >= 0 ? W.T(n.Have == 1 ? ClientTextKeys.ModernNpcCraftLeft : ClientTextKeys.ModernNpcCraftsLeft, n.Have) : "0") : W.N(n.Have) + " / " + W.N(n.Shadow.Count * q);
            float dura = tool && bagItem != null && bagItem.MaxDura > 0 ? Math.Min(1f, bagItem.CurrentDura / (float)bagItem.MaxDura) : -1f;
            var row = new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                RectangleF rr = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
                using GraphicsPath p = ModernSkin.RoundRect(rr, ModernTheme.Radius * k);
                if (!ok) using (var br = new SolidBrush(W.Over(ModernTheme.Bad, 0.06f))) g.FillPath(br, p);
                ModernSkin.Stroke(g, p, ok ? W.Over(ModernTheme.Frame, 0.4f) : W.Over(ModernTheme.Bad, 0.55f));
                float tx = 46 * k, rw = 96 * k;
                ModernSkin.Text(g, nm, ModernSkin.Font(11.5f), ModernTheme.Text, new RectangleF(tx, note.Length > 0 || dura >= 0 ? 5 * k : 0, b.Size.Width - tx - rw, note.Length > 0 || dura >= 0 ? 16 * k : b.Size.Height));
                if (dura >= 0)
                {
                    RectangleF bar = new RectangleF(tx, 26 * k, b.Size.Width - tx - rw - 8 * k, 4 * k);
                    using (var br = new SolidBrush(Color.FromArgb(255, 5, 7, 12))) g.FillRectangle(br, bar);
                    using (var br = new SolidBrush(ModernTheme.Accent)) g.FillRectangle(br, bar.X, bar.Y, bar.Width * dura, bar.Height);
                }
                else if (note.Length > 0) ModernSkin.Text(g, note, ModernSkin.Font(9.5f), ModernTheme.Dim, new RectangleF(tx, 22 * k, b.Size.Width - tx - rw, 14 * k));
                ModernSkin.Text(g, right, ModernSkin.Font(11.5f, true), ok ? ModernTheme.Ok : ModernTheme.BadSoft, new RectangleF(b.Size.Width - rw, 0, rw - 8 * k, b.Size.Height), StringAlignment.Far);
            })
            { Parent = box, Location = new Point(0, S(y)), Size = new Size(S(w), S(40)), NotControl = true };
            UserItem shadow = n.Shadow;
            new NpcItemCell(34, () => shadow) { Parent = row, Location = new Point(S(4), S(3)) };
        }

        /// <summary>Models.Npc.CraftItems / CraftShadowItems: the recipe (slots 0-2 tools, 3-8 ingredients) and the bag items chosen.</summary>
        private static void FillModel(ClientRecipeInfo r, Plan plan)
        {
            UserItem[] slots = GameScene.Models.Npc.CraftItems, shadows = GameScene.Models.Npc.CraftShadowItems;
            Array.Clear(slots);
            Array.Clear(shadows);
            for (int i = 0; i < plan.Tools.Count && i < ToolSlots; i++)
            {
                shadows[i] = r.Tools[i];
                if (plan.Tools[i].Slot >= 0) slots[i] = User.Inventory[plan.Tools[i].Slot];
            }
            for (int i = 0; i < plan.Ings.Count && ToolSlots + i < slots.Length; i++)
            {
                shadows[ToolSlots + i] = r.Ingredients[i];
                if (plan.Ings[i].Slot >= 0) slots[ToolSlots + i] = User.Inventory[plan.Ings[i].Slot];
            }
        }

        private static void ClearModel()
        {
            Array.Clear(GameScene.Models.Npc.CraftItems);
            Array.Clear(GameScene.Models.Npc.CraftShadowItems);
        }

        // ------------------------------------------------------------------ craft (C.CraftItem; nothing changes until the server answers)

        private void DoCraft()
        {
            if (_pending) return;
            UserItem g = SelectedGoods();
            ClientRecipeInfo r = Recipe(g);
            if (g == null || r == null) return;
            Plan plan = MakePlan(g, r);
            int q = Math.Max(1, Math.Min(_qty, plan.Max));
            if (plan.Max <= 0) { NpcFmt.Chat(plan.Why.Length > 0 ? plan.Why : W.T(ClientTextKeys.YouDoNotHaveRequiredToolsOrIngredients)); return; }
            int chance = Chance(r);
            if (chance < 100)
            {
                ModernRoot.Instance?.Popups.Ask(W.T(ClientTextKeys.ModernNpcCraftAsk, q, NpcFmt.ItemName(g), W.N(r.Gold * (uint)q), chance), ok =>
                {
                    if (!ok || !IsOpen || _pending) return;
                    Plan again = MakePlan(g, r); // the bag may have changed while asking
                    if (again.Max < q) { NpcFmt.Chat(again.Why.Length > 0 ? again.Why : W.T(ClientTextKeys.YouDoNotHaveRequiredToolsOrIngredients)); _dirty = true; return; }
                    Send(g, r, again, q);
                }, true, PopupKind.Warning, W.T(ClientTextKeys.ModernNpcCrafting));
                return;
            }
            Send(g, r, plan, q);
        }

        private void Send(UserItem g, ClientRecipeInfo r, Plan plan, int q)
        {
            if (r.Gold * (uint)q > GameScene.Gold) { NpcFmt.Chat(W.T(ClientTextKeys.YouDoNotHaveEnoughGold)); return; }
            if (!_state.Open) return;
            Network.Enqueue(new C.CraftItem { UniqueID = g.UniqueID, Count = (ushort)q, Slots = (int[])plan.Slots.Clone() });
            foreach (int s in plan.Slots) ItemMoves.SetLocked(MirGridType.Inventory, s, true);
            _pending = true;
            _pendSlots = (int[])plan.Slots.Clone();
            _pendIndex = g.ItemIndex;
            _pendBefore = CountOf(g.ItemIndex);
            _pendCount = g.Count * q;
            _pendName = NpcFmt.ItemName(g);
            _pendUntil = CMain.Time + AnswerMs;
            _result = _resultKind = string.Empty;
            _dirty = true;
        }

        private static int CountOf(int itemIndex)
        {
            int n = 0;
            foreach (UserItem it in User.Inventory) if (it != null && it.ItemIndex == itemIndex) n += it.Count;
            return n;
        }

        /// <summary>Unlocks the request's cells (answer, timeout or close).</summary>
        private void Release()
        {
            if (!_pending) return;
            _pending = false;
            foreach (int s in _pendSlots) ItemMoves.SetLocked(MirGridType.Inventory, s, false);
            _pendSlots = Array.Empty<int>();
        }

        /// <summary>S.CraftItem succeeded (IGameUi.CraftItemDone; the crafted stack, if any, arrived before it).</summary>
        internal void Done()
        {
            if (!_pending) return;
            bool made = CountOf(_pendIndex) > _pendBefore;
            Release();
            _visitDone++;
            if (made)
            {
                _visitOk++;
                _resultKind = "ok";
                _result = W.T(ClientTextKeys.ModernNpcCrafted, W.N(_pendCount), _pendName);
                _log.Insert(0, (true, _result));
            }
            else
            {
                _resultKind = "fail";
                _result = W.T(ClientTextKeys.ModernNpcCraftFailed);
                _log.Insert(0, (false, W.T(ClientTextKeys.ModernNpcCraftFailedLog, _pendName)));
            }
            if (_log.Count > LogMax) _log.RemoveRange(LogMax, _log.Count - LogMax);
            _dirty = true;
        }
    }
}

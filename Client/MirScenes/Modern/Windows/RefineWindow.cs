// Ported from WebClient/src/ui/modern/windows/refine.ts (RefineSession + refineCalc / testOdds + RefineWindow): the new UI's
// refine window (the smith). Desktop counterparts: RefineDialog + NPCDropDialog PanelType.Refine / CheckRefine
// (MirScenes/Dialogs/NPCDialogs.cs) and GameScene.NPCRefine / NPCCheckRefine / NPCCollectRefine / RefineItem.
// Crystal refines in three NPC pages, so the window has two modes and collecting has none:
//  - [@REFINE] (S.NPCRefine, not refining → ModernUi.NpcRefine): "refine" mode = the anvil (the drop panel's cell,
//    Models.Npc.DropTarget, a local lock on the bag slot like NPCDropDialog.OldCell) and the 16-cell refine grid
//    (GameScene.Refine through ModernItemCell(MirGridType.Refine): C.DepositRefineItem / C.RetrieveRefineItem / C.MoveItem
//    via ItemMoves). Refine = C.RefineItem after a question (NPCDropDialog.Confirm's checks; the weapon stays locked until
//    S.RepairItem). Closing the window sends C.RefineCancel (RefineDialog.Hide) and frees the anvil (NPCDropDialog.Hide);
//  - [@REFINECOLLECT] has no window (ModernUi.NpcCollectRefine closes the NPC dialog);
//  - [@REFINECHECK] (S.NPCCheckRefine → NpcDropPanel(CheckRefine), area A's member: ModernUi.UpgradeDropPanel opens this
//    window in "check" mode when the integrator routes it here): the anvil takes a refined item, Test = C.CheckRefine after a
//    question; S.ItemUpgraded = passed (crit when it gained more than its RefineAdded), the item gone with S.RefineItem =
//    smashed (the server closes the dialog: the result is a popup). The test keeps the item locked until the answer (6 s max).
// The odds come from S.ServerRules (Models.ServerRules) with the server's integer divisions; without the rules the formula is
// hidden. Desktop idiom: a click on a bag item in the window's bag column places it (weapons on the anvil, jewellery / ore in
// the grid; the check page: the anvil); the grid cells pick / drop through ItemMoves like the classic cells; a click on the
// anvil with a picked bag item puts it there, a right click (or Take back) frees it.
// HIDDEN (no data): the live forge countdown (CollectTime is never sent) and the mock's history; the result animation.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirNetwork;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    public enum RefineMode
    {
        None,
        Refine,
        Check,
    }

    /// <summary>One part of PlayerObject.RefineItem's success chance, for the explanation.</summary>
    internal readonly struct RefinePart
    {
        public readonly string Name, Why;
        public readonly int Value, Max;

        public RefinePart(string name, string why, int value, int max)
        {
            Name = name;
            Why = why;
            Value = value;
            Max = Math.Max(1, max);
        }
    }

    /// <summary>PlayerObject.RefineItem's tally over the refine grid and the chance it would store on the weapon.</summary>
    internal sealed class RefineCalc
    {
        public int Dc, Mc, Sc, Value, Chance, WeaponsLost, Unknown;
        public string Zero = string.Empty;
        public readonly List<RefinePart> Parts = new List<RefinePart>();

        private static readonly string[] Refined = { string.Empty, "DC", "MC", "SC" };

        public static string StatName(int value) => value switch
        {
            1 => W.T(ClientTextKeys.ModernUpDC),
            2 => W.T(ClientTextKeys.ModernUpMC),
            3 => W.T(ClientTextKeys.ModernUpSC),
            _ => string.Empty,
        };

        public static Stat MaxStat(int value) => value switch { 1 => Stat.MaxDC, 2 => Stat.MaxMC, 3 => Stat.MaxSC, _ => Stat.Unknown };

        private static int Get(Stats s, Stat stat) => s == null ? 0 : s[stat];

        /// <summary>refine.ts refineCalc: the server's formula with its types (short totals, integer divisions, the byte cast).</summary>
        public static RefineCalc Run(UserItem weapon, UserItem[] grid, ServerPackets.ServerRules rules)
        {
            var c = new RefineCalc();
            int lv = 0, items = 0, durability = 0, currentDura = 0, ores = 0, purity = 0;
            if (grid != null)
                for (int i = 0; i < grid.Length; i++)
                {
                    UserItem it = grid[i];
                    if (it == null) continue;
                    ItemInfo ii = it.Info;
                    if (ii == null) { c.Unknown++; continue; }
                    if (ii.Type == ItemType.Weapon) { c.WeaponsLost++; continue; }
                    Stats s = ii.Stats, a = it.AddedStats;
                    if (Get(s, Stat.MaxDC) > 0 || Get(s, Stat.MaxMC) > 0 || Get(s, Stat.MaxSC) > 0)
                    {
                        c.Dc += Get(s, Stat.MinDC) + Get(s, Stat.MaxDC) + Get(a, Stat.MaxDC);
                        c.Mc += Get(s, Stat.MinMC) + Get(s, Stat.MaxMC) + Get(a, Stat.MaxMC);
                        c.Sc += Get(s, Stat.MinSC) + Get(s, Stat.MaxSC) + Get(a, Stat.MaxSC);
                        lv += ii.RequiredAmount;
                        if (it.MaxDura / 1000 == ii.Durability / 1000) durability++;
                        if (it.CurrentDura / 1000 == it.MaxDura / 1000) currentDura++;
                        items++;
                    }
                    if (Up.Name(ii) == rules.RefineOreName) { purity += it.CurrentDura / 1000; ores++; }
                }
            int dc = c.Dc, mc = c.Mc, sc = c.Sc;
            c.Value = dc > mc && dc > sc ? 1 : mc > dc && mc > sc ? 2 : sc > dc && sc > mc ? 3 : 0;
            int stat = c.Value == 1 ? dc : c.Value == 2 ? mc : c.Value == 3 ? sc : 0;
            int wlv = weapon?.Info?.RequiredAmount ?? 0;
            c.Zero = dc == 0 && mc == 0 && sc == 0 ? W.T(ClientTextKeys.ModernUpZeroNoJewellery)
                : ores == 0 ? W.T(ClientTextKeys.ModernUpZeroNoOre, rules.RefineOreName) : string.Empty;
            int itemS = Math.Clamp(stat * 5 - wlv + 5, 0, 10);
            int avgLv = items > 0 ? lv / items : 0, avgPur = ores > 0 ? purity / ores : 0, avgStat = items > 0 ? stat / items : 0;
            int lvB = items > 0 && avgLv > wlv - 5 ? 10 : 0, maxB = items > 0 && durability == items ? 10 : 0, curB = items > 0 && currentDura == items ? 5 : 0;
            int oreA = items > 0 && ores >= items ? 15 : 0, oreP = ores > 0 && items > 0 && avgPur >= avgStat ? 15 : 0, oreE = ores > 0 && purity == stat ? 5 : 0;
            Stats wa = weapon?.AddedStats;
            int luck = Math.Clamp(Get(wa, Stat.Luck) + 5, 0, 10);
            int addedRaw = (Get(wa, Stat.MaxDC) + Get(wa, Stat.MaxMC) + Get(wa, Stat.MaxSC)) & 0xff; // the server's (byte) cast
            int reduce = weapon?.Info?.Type == ItemType.Weapon ? rules.RefineWepStatReduce : rules.RefineItemStatReduce;
            int penalty = Math.Min(50, addedRaw * reduce);
            string sName = c.Value == 0 ? W.T(ClientTextKeys.ModernUpStat) : StatName(c.Value);
            c.Parts.Add(new RefinePart(W.T(ClientTextKeys.ModernUpPartStrength, sName), W.T(ClientTextKeys.ModernUpPartStrengthWhy, sName, stat, wlv), itemS, 10));
            c.Parts.Add(new RefinePart(W.T(ClientTextKeys.ModernUpPartLevel), W.T(ClientTextKeys.ModernUpPartLevelWhy, avgLv, wlv - 5), lvB, 10));
            c.Parts.Add(new RefinePart(W.T(ClientTextKeys.ModernUpPartMaxDura), W.T(ClientTextKeys.ModernUpPartMaxDuraWhy, durability, items), maxB, 10));
            c.Parts.Add(new RefinePart(W.T(ClientTextKeys.ModernUpPartDura), W.T(ClientTextKeys.ModernUpPartDuraWhy, currentDura, items), curB, 5));
            c.Parts.Add(new RefinePart(W.T(ClientTextKeys.ModernUpPartOreAmount), W.T(ClientTextKeys.ModernUpPartOreAmountWhy, ores, items), oreA, 15));
            c.Parts.Add(new RefinePart(W.T(ClientTextKeys.ModernUpPartOrePurity), W.T(ClientTextKeys.ModernUpPartOrePurityWhy, avgPur, sName, avgStat), oreP, 15));
            c.Parts.Add(new RefinePart(W.T(ClientTextKeys.ModernUpPartExact), W.T(ClientTextKeys.ModernUpPartExactWhy, purity, sName, stat), oreE, 5));
            c.Parts.Add(new RefinePart(W.T(ClientTextKeys.ModernUpPartLuck), W.T(ClientTextKeys.ModernUpPartLuckWhy, Get(wa, Stat.Luck)), luck, 10));
            c.Parts.Add(new RefinePart(W.T(ClientTextKeys.ModernUpPartBase), W.T(ClientTextKeys.ModernUpPartBaseWhy), rules.RefineBaseChance, Math.Max(1, (int)rules.RefineBaseChance)));
            if (penalty > 0) c.Parts.Add(new RefinePart(W.T(ClientTextKeys.ModernUpPartRefined), W.T(ClientTextKeys.ModernUpPartRefinedWhy, addedRaw, reduce), -penalty, 50));
            c.Chance = c.Zero.Length > 0 ? 0 : itemS + lvB + maxB + curB + oreA + oreP + oreE + luck + rules.RefineBaseChance - penalty;
            return c;
        }

        /// <summary>testOdds: CheckRefine's rolls (Random(1, 100) = 1..99): passed when roll &lt;= chance (and a stat was chosen), crit when
        /// a second roll &lt; RefineCritChance (unknown without the rules: counted as a plain pass).</summary>
        public static (double Win, double Crit, double Smash) Odds(int chance, bool hasStat, int critChance)
        {
            double pass = hasStat ? Math.Clamp(chance, 0, 99) / 99d : 0;
            double c = critChance < 0 ? 0 : Math.Clamp(critChance - 1, 0, 99) / 99d;
            return (pass * (1 - c), pass * c, 1 - pass);
        }
    }

    /// <summary>
    /// The refine pages' state outside the window (refine.ts RefineSession): the open page's mode, the anvil (DropTarget + the bag
    /// slot it locks), the test waiting for its answer and the last result. One per modern UI (ModernUi.Refine), so a smash (which
    /// closes the NPC dialog and the window) still shows. UI thread only.
    /// </summary>
    public sealed class RefineSession
    {
        public const int AnswerMs = 6000;

        private RefineMode _mode;
        private int _dropSlot = -1;
        private ulong _testId;
        private string _testName = string.Empty;
        private int _testValue, _testBefore, _testAdded;
        private long _testUntil;
        private readonly Action<ItemSlotKey> _slotChanged;

        public RefineSession()
        {
            _slotChanged = OnSlotChanged;
        }

        public event Action Changed;

        public RefineMode Mode => _mode;
        public bool Testing => _testId != 0;
        /// <summary>The last test's result (0 none, 1 passed, 2 crit, 3 smashed) and its text.</summary>
        public int ResultKind { get; private set; }
        public string ResultText { get; private set; } = string.Empty;

        public static UserItem Target => GameScene.Models?.Npc.DropTarget;

        private void Emit() => Changed?.Invoke();

        /// <summary>The server opened the page (NpcRefine / UpgradeDropPanel).</summary>
        public void Open(RefineMode mode)
        {
            if (_mode != mode) ClearDrop();
            _mode = mode;
            ResultKind = 0;
            ResultText = string.Empty;
            Emit();
        }

        /// <summary>The window closed (RefineDialog.Hide + NPCDropDialog.Hide).</summary>
        public void End()
        {
            ClearDrop();
            if (_testId != 0 && Up.BagSlot(_testId) >= 0) Unlock(_testId); // the answer may still come: the item stays usable meanwhile
            _mode = RefineMode.None;
            Emit();
        }

        /// <summary>Why the anvil refuses an item ("" = it takes it): RefineItem's / CheckRefine's own refusals, before the request.</summary>
        public string Refusal(UserItem item)
        {
            if (item == null) return string.Empty;
            if (_mode == RefineMode.Check) return item.RefineAdded == 0 ? W.T(ClientTextKeys.ModernUpNotRefined) : string.Empty;
            if (item.RefineAdded != 0) return W.T(ClientTextKeys.ModernUpCheckFirst);
            ItemInfo info = item.Info;
            if (info == null) return string.Empty;
            if (info.Durability == 0) return W.T(ClientTextKeys.ModernUpCantRefine);
            if ((GameScene.Models?.ServerRules.Rules?.OnlyRefineWeapon ?? true) && info.Type != ItemType.Weapon) return W.T(ClientTextKeys.ModernUpOnlyWeapons);
            if (info.Bind.HasFlag(BindMode.DontUpgrade)) return W.T(ClientTextKeys.ModernUpCantRefine);
            if (item.RentalInformation != null && item.RentalInformation.BindingFlags.HasFlag(BindMode.DontUpgrade)) return W.T(ClientTextKeys.ModernUpCantRefine);
            return string.Empty;
        }

        /// <summary>NPCDropDialog.ItemCell_Click with a bag item: it goes on the anvil (a local lock). Returns a refusal ("" = done).</summary>
        public string Take(int bagSlot)
        {
            if (_mode == RefineMode.None || Up.User == null) return string.Empty;
            UserItem item = ItemMoves.GetItem(MirGridType.Inventory, bagSlot);
            if (item?.Info == null || ItemMoves.IsLocked(MirGridType.Inventory, bagSlot)) return string.Empty;
            string why = item.Info.Durability == 0 ? W.T(ClientTextKeys.ModernUpCantRefine) : Refusal(item);
            if (why.Length > 0) return why;
            ClearDrop();
            _dropSlot = bagSlot;
            ItemMoves.SetLocked(MirGridType.Inventory, bagSlot, true);
            ItemMoves.SetItem(MirGridType.DropPanel, 0, item);
            Emit();
            return string.Empty;
        }

        /// <summary>The anvil gives its item back (NPCDropDialog.Hide / a second click).</summary>
        public void ClearDrop()
        {
            int slot = _dropSlot;
            _dropSlot = -1;
            if (slot >= 0) ItemMoves.SetLocked(MirGridType.Inventory, slot, false);
            if (Target != null) ItemMoves.SetItem(MirGridType.DropPanel, 0, null);
            Emit();
        }

        /// <summary>NPCDropDialog.Confirm for PanelType.Refine: false when refused (the chat says why).</summary>
        public bool CanRefine(out string why)
        {
            why = string.Empty;
            UserItem item = Target;
            if (_mode != RefineMode.Refine || item?.Info == null) return false;
            why = Refusal(item);
            if (why.Length > 0) return false;
            bool any = false;
            for (int i = 0; i < GameScene.Refine.Length && !any; i++) any = GameScene.Refine[i] != null;
            if (!any) { why = W.T(ClientTextKeys.YouHaventDepositedItemsToRefine, item.FriendlyName); return false; }
            if (GameScene.Gold < Cost(item)) { why = W.T(ClientTextKeys.YouDontHaveEnoughGoldToRefine, item.FriendlyName); return false; }
            return true;
        }

        public static uint Cost(UserItem item) => item?.Info == null ? 0 : (uint)(item.Info.RequiredAmount * 10 * GameScene.NPCRate);

        /// <summary>C.RefineItem (NPCDropDialog.Confirm): the anvil empties, the bag slot stays locked until S.RepairItem.</summary>
        public bool SendRefine()
        {
            if (!CanRefine(out string why))
            {
                if (why.Length > 0) Up.Say(why);
                return false;
            }
            UserItem item = Target;
            Network.Enqueue(new C.RefineItem { UniqueID = item.UniqueID });
            _dropSlot = -1; // its lock is the server's now (S.RepairItem / S.RefineItem)
            ItemMoves.SetItem(MirGridType.DropPanel, 0, null);
            Emit();
            return true;
        }

        /// <summary>Test = C.CheckRefine (NPCDropDialog.Confirm for CheckRefine); the item stays locked until the answer.</summary>
        public bool SendTest()
        {
            UserItem item = Target;
            if (_mode != RefineMode.Check || item == null || _testId != 0) return false;
            if (item.RefineAdded == 0)
            {
                Up.Say(W.T(ClientTextKeys.ItemHasntBeenRefinedNoChecking, item.FriendlyName));
                return false;
            }
            Network.Enqueue(new C.CheckRefine { UniqueID = item.UniqueID });
            Stat stat = RefineCalc.MaxStat((int)item.RefinedValue);
            _testId = item.UniqueID;
            _testName = item.FriendlyName;
            _testValue = (int)item.RefinedValue;
            _testBefore = stat == Stat.Unknown ? 0 : item.AddedStats[stat];
            _testAdded = item.RefineAdded;
            _testUntil = CMain.Time + AnswerMs;
            _dropSlot = -1; // the anvil's lock becomes the test's lock
            ItemMoves.SetItem(MirGridType.DropPanel, 0, null);
            int slot = Up.BagSlot(_testId);
            if (slot >= 0) ItemMoves.SetLocked(MirGridType.Inventory, slot, true);
            ItemMoves.SlotChanged.On(_slotChanged); // S.RefineItem (the smash) empties the refine grid first: GameScene.RefineReset
            ResultKind = 0;
            ResultText = string.Empty;
            Emit();
            return true;
        }

        /// <summary>S.ItemUpgraded (ModernUi.ItemUpgraded): the test passed when it is the tested item.</summary>
        public void Upgraded(UserItem item)
        {
            if (_testId == 0 || item == null || item.UniqueID != _testId) return;
            Stat stat = RefineCalc.MaxStat(_testValue);
            int gain = stat == Stat.Unknown ? 0 : item.AddedStats[stat] - _testBefore;
            bool crit = gain > _testAdded;
            string what = _testValue == 0 ? W.T(ClientTextKeys.ModernUpStat) : RefineCalc.StatName(_testValue);
            string name = _testName;
            EndTest();
            ResultKind = crit ? 2 : 1;
            ResultText = crit ? W.T(ClientTextKeys.ModernUpTestCrit, name, gain, what) : W.T(ClientTextKeys.ModernUpTestWin, name, gain, what);
            Emit();
        }

        private void OnSlotChanged(ItemSlotKey key)
        {
            if (_testId == 0 || key.Grid != MirGridType.Refine) return;
            if (Up.BagSlot(_testId) >= 0) return;
            string name = _testName;
            EndTest();
            ResultKind = 3;
            ResultText = W.T(ClientTextKeys.ModernUpTestSmash, name);
            Emit();
            // GameScene.RefineItem closes the NPC dialog (and this window): say it in a popup
            Up.Pop?.Confirm(ResultText, PopupButtons.Ok, PopupKind.Danger, null, false, W.T(ClientTextKeys.CheckRefine));
        }

        /// <summary>The test's lock is released if the server never answers (it returns silently off-page). Cheap; from Process.</summary>
        public void Poll()
        {
            if (_testId != 0 && CMain.Time >= _testUntil) EndTest();
        }

        private void EndTest()
        {
            ulong id = _testId;
            if (id == 0) return;
            _testId = 0;
            ItemMoves.SlotChanged.Off(_slotChanged);
            Unlock(id);
            Emit();
        }

        private static void Unlock(ulong id)
        {
            int slot = Up.BagSlot(id);
            if (slot >= 0) ItemMoves.SetLocked(MirGridType.Inventory, slot, false);
        }

        public void Dispose()
        {
            ItemMoves.SlotChanged.Off(_slotChanged);
            _testId = 0;
            _mode = RefineMode.None;
        }
    }

    public sealed class RefineWindow : ModernWindow
    {
        public const string WindowKey = "refine";
        private const int WidthHud = 940, HeightHud = 560, BagW = 228, ForgeX = 229, ForgeW = 400, SideX = 631, SideW = 309, GridSlots = 16;

        private readonly RefineSession _session;
        private RefineMode _mode = RefineMode.Refine;
        private ulong _sentId;
        private string _note = string.Empty;
        private bool _dirty = true;
        private string _partsKey = string.Empty;
        private bool _npcSeen;
        private long _nextPoll;
        private uint _gold;

        private BagColumn _bag;
        private ModernLabel _bagHint, _anvilName, _anvilSub, _gridCount, _warn, _note2, _ringNote, _cost, _steps;
        private SlotView _anvil;
        private ModernButton _takeBack, _go;
        private ModernPanel _gridBox;
        private ModernLabel _gridCaption;
        private readonly List<ModernItemCell> _gridCells = new List<ModernItemCell>();
        private readonly PaintBox[] _statBoxes = new PaintBox[3];
        private readonly int[] _statVals = new int[3];
        private readonly int[] _statMode = new int[3]; // 0 plain, 1 win, 2 tie
        private PaintBox _card;
        private readonly List<(string K, string V)> _cardRows = new List<(string, string)>();
        private ScrollView _side;
        private ProgressRing _ring;
        private ModernLabel _oddsCaption, _partsCaption;
        private OddsBar _odds;
        private PaintBox _oddsText, _res, _parts;
        private readonly string[] _oddsParts = new string[6];
        private readonly List<RefinePart> _shownParts = new List<RefinePart>();

        private readonly GridWatch _bagWatch = new GridWatch(), _gridWatch = new GridWatch();
        private readonly Action _changed;
        private readonly Action<ItemSlotKey> _slotChanged;
        private readonly Action<UserItem> _itemInfo;

        public RefineWindow(RefineSession session) : base(WindowKey, W.T(ClientTextKeys.ModernUpRefineTitle), 40, 30)
        {
            _session = session;
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
            _changed = () => _dirty = true;
            _slotChanged = k => { if (k.Grid == MirGridType.Inventory || k.Grid == MirGridType.Refine || k.Grid == MirGridType.DropPanel) _dirty = true; };
            _itemInfo = _ => { _partsKey = string.Empty; _dirty = true; };
        }

        internal RefineMode ModeShown => _mode;
        internal SlotView Anvil => _anvil;
        internal ModernButton GoButton => _go;
        internal BagColumn Bag => _bag;
        internal IReadOnlyList<ModernItemCell> GridCells => _gridCells;
        internal string Note => _note;

        protected override void Build()
        {
            const int top = TitleHeight;
            // ---- bag column
            W.Caption(this, W.T(ClientTextKeys.ModernWinYourBag), 10, top + 10, 120);
            W.Label(this, W.T(ClientTextKeys.ModernUpClickToAdd), 110, top + 10, BagW - 120, 14, 10, false, ModernTheme.Dim, StringAlignment.Far);
            _bag = new BagColumn(this, 6, top + 30, 5, 38, 392, Place, Useful);
            _bagHint = W.Label(this, string.Empty, 10, top + 430, BagW - 18, 120, 10, false, ModernTheme.Dim, StringAlignment.Near, true);
            W.VDivider(this, BagW, top, HeightHud);

            // ---- forge: the anvil, the grid, the totals / the check card, warnings
            new PaintBox(PaintForge) { Parent = this, Location = new Point(S(ForgeX), S(top)), Size = new Size(S(ForgeW + 1), S(HeightHud)), NotControl = true };
            int cx = ForgeX + ForgeW / 2;
            _anvil = new SlotView(MirGridType.DropPanel, 0, 64) { Parent = this, Location = new Point(S(cx - 32), S(top + 16)) };
            _anvil.Pressed2 += AnvilClick;
            new PaintBox((g, b) =>
            {
                RectangleF r = new RectangleF(1, 1, b.Size.Width - 2, b.Size.Height - 2);
                using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * ModernRoot.K + 2);
                ModernSkin.Stroke(g, p, ModernTheme.Accent, Math.Max(1.5f, 1.5f * ModernRoot.K));
            }) { Parent = this, Location = new Point(S(cx - 34), S(top + 14)), Size = new Size(S(68), S(68)), NotControl = true };
            _anvilName = W.Label(this, string.Empty, ForgeX + 10, top + 86, ForgeW - 20, 20, 15, true, Color.White, StringAlignment.Center);
            _anvilSub = W.Label(this, string.Empty, ForgeX + 10, top + 106, ForgeW - 20, 16, 11, false, ModernTheme.Dim, StringAlignment.Center);
            _takeBack = new ModernButton(W.T(ClientTextKeys.ModernUpTakeBack)) { Parent = this, Tooltip = W.T(ClientTextKeys.ModernUpTakeBackHint) };
            _takeBack.AutoWidth(90);
            _takeBack.Location = new Point(S(cx) - _takeBack.Size.Width / 2, S(top + 126));
            _takeBack.Click += (o, e) => { _session.ClearDrop(); _dirty = true; };

            int gw = 8 * 44 - 4 + 20, gx = ForgeX + (ForgeW - gw) / 2;
            _gridCaption = W.Caption(this, W.T(ClientTextKeys.ModernUpRefineGrid), gx, top + 160, 200);
            _gridCount = W.Label(this, string.Empty, gx + 200, top + 160, gw - 200, 14, 11, true, Color.Empty, StringAlignment.Far);
            _gridBox = new ModernPanel { Parent = this, Location = new Point(S(gx), S(top + 178)), Size = new Size(S(gw), S(2 * 44 - 4 + 20)) };
            new PaintBox(PaintCrucible) { Parent = _gridBox, Size = _gridBox.Size, NotControl = true };
            _gridCells.Clear();
            for (int i = 0; i < GridSlots; i++)
                _gridCells.Add(new ModernItemCell(MirGridType.Refine, i, 40) { Parent = _gridBox, Location = new Point(S(10 + i % 8 * 44), S(10 + i / 8 * 44)) });
            int bw = 104, bx = ForgeX + (ForgeW - (3 * bw + 16)) / 2;
            for (int i = 0; i < 3; i++)
            {
                int n = i;
                _statBoxes[i] = new PaintBox((g, b) => PaintStat(g, b, n)) { Parent = this, Location = new Point(S(bx + i * (bw + 8)), S(top + 294)), Size = new Size(S(bw), S(48)), NotControl = true };
            }
            _card = new PaintBox(PaintCard) { Parent = this, Location = new Point(S(ForgeX + 20), S(top + 160)), Size = new Size(S(ForgeW - 40), S(112)), NotControl = true };
            _warn = W.Label(this, string.Empty, ForgeX + 10, top + 352, ForgeW - 20, 120, 11, false, ModernTheme.BadSoft, StringAlignment.Center, true);
            _note2 = W.Label(this, string.Empty, ForgeX + 10, top + HeightHud - 28, ForgeW - 20, 16, 11, false, ModernTheme.Accent, StringAlignment.Center);
            W.VDivider(this, SideX - 1, top, HeightHud);

            // ---- side: the chance ring, the odds, the button, the cost, the result, how it adds up
            _side = new ScrollView(SideW, HeightHud) { Parent = this, Location = new Point(S(SideX), S(top)), Background = W.Page };
            _side.Framed = false;
            MirControl sc = _side.Content;
            int iw = SideW - 38;
            _ring = new ProgressRing(110, 10) { Parent = sc, Location = new Point(S(14 + (iw - 110) / 2), S(12)) };
            _ringNote = W.Label(sc, string.Empty, 14, 0, iw, 30, 11, false, ModernTheme.Dim, StringAlignment.Center, true);
            _oddsCaption = W.Caption(sc, W.T(ClientTextKeys.ModernUpOddsCaption), 14, 0, iw);
            _odds = new OddsBar(iw, 12, ModernTheme.Ok, ModernTheme.Gold, ModernTheme.Bad) { Parent = sc, Location = new Point(S(14), 0) };
            _oddsText = new PaintBox(PaintOddsText) { Parent = sc, Location = new Point(S(14), 0), Size = new Size(S(iw), S(14)), NotControl = true };
            _go = new ModernButton(W.T(ClientTextKeys.ModernUpRefineBtn), ButtonKind.Main, true) { Parent = sc, Location = new Point(S(14), 0), FontSize = 14 };
            _go.Size = new Size(S(iw), S(38));
            _go.Click += (o, e) => Go();
            _cost = W.Label(sc, string.Empty, 14, 0, iw, 44, 11, false, ModernTheme.Dim, StringAlignment.Center, true);
            _res = new PaintBox(PaintResult) { Parent = sc, Location = new Point(S(14), 0), Size = new Size(S(iw), S(34)), NotControl = true };
            _partsCaption = W.Caption(sc, W.T(ClientTextKeys.ModernUpHowItAddsUp), 14, 0, iw);
            _parts = new PaintBox(PaintParts) { Parent = sc, Location = new Point(S(14), 0), Size = new Size(S(iw), S(28)), NotControl = true };
            _steps = W.Label(sc, string.Empty, 14, 0, iw, 44, 11, false, ModernTheme.Dim, StringAlignment.Center, true);

            _partsKey = string.Empty;
            _bagWatch.Reset();
            _gridWatch.Reset();
            _dirty = true;
            Render();
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void Shown()
        {
            _mode = _session.Mode == RefineMode.None ? RefineMode.Refine : _session.Mode;
            _sentId = 0;
            _note = string.Empty;
            _partsKey = string.Empty;
            _npcSeen = Up.Ui?.IsNpcDialogOpen() ?? false;
            _session.Changed += _changed;
            ItemMoves.SlotChanged.On(_slotChanged);
            ModernEvents.ItemInfo.On(_itemInfo);
            ModernEvents.InventoryResized.On(_changed);
            GameScene.Models?.ServerRules.Changed.On(_changed);
            _dirty = true;
            Render();
        }

        protected override void Closed()
        {
            _session.Changed -= _changed;
            ItemMoves.SlotChanged.Off(_slotChanged);
            ModernEvents.ItemInfo.Off(_itemInfo);
            ModernEvents.InventoryResized.Off(_changed);
            GameScene.Models?.ServerRules.Changed.Off(_changed);
            ItemSlotRef pick = ItemMoves.Selected;
            if (pick != null && pick.GridType == MirGridType.Refine) ItemMoves.Selected = null;
            bool refine = _session.Mode == RefineMode.Refine || (_session.Mode == RefineMode.None && _mode == RefineMode.Refine);
            _session.End(); // NPCDropDialog.Hide: the anvil's item is unlocked
            if (refine) Network.Enqueue(new C.RefineCancel()); // RefineDialog.Hide: the server hands the grid back
        }

        public override void Process()
        {
            base.Process();
            if (Up.User == null) return;
            if (CMain.Time >= _nextPoll)
            {
                _nextPoll = CMain.Time + GridWatch.IntervalMs;
                _session.Poll();
                // NPCDialog.Hide hides the refine dialogs: close when the NPC dialog goes from open to closed
                bool npc = Up.Ui?.IsNpcDialogOpen() ?? false;
                if (_npcSeen && !npc) { Close(); return; }
                _npcSeen |= npc;
                if (_gold != GameScene.Gold) { _gold = GameScene.Gold; _dirty = true; }
                if (_bagWatch.Poll(MirGridType.Inventory, Up.User.Inventory, Up.Belt, int.MaxValue, true) | _gridWatch.Poll(MirGridType.Refine, GameScene.Refine, 0, int.MaxValue, true))
                {
                    for (int i = 0; i < _gridCells.Count; i++) _gridCells[i].Refresh(false);
                    _dirty = true;
                }
            }
            if (_dirty) Render();
        }

        // ------------------------------------------------------------------ helpers

        private static ServerPackets.ServerRules Rules => GameScene.Models?.ServerRules.Rules;

        private bool Check => _mode == RefineMode.Check;

        /// <summary>Whether a bag item has any use here (the bag column dims the rest).</summary>
        private bool Useful(UserItem item)
        {
            if (Check) return item.RefineAdded != 0;
            ItemInfo info = item.Info;
            if (info == null) return true;
            if (info.Type == ItemType.Weapon) return _session.Refusal(item).Length == 0;
            string ore = Rules?.RefineOreName;
            return info.Stats[Stat.MaxDC] > 0 || info.Stats[Stat.MaxMC] > 0 || info.Stats[Stat.MaxSC] > 0 || (!string.IsNullOrEmpty(ore) && Up.Name(info) == ore);
        }

        private void Say(string text)
        {
            _note = text ?? string.Empty;
            Up.SetText(_note2, _note);
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            _dirty = false;
            if (_side == null || _side.IsDisposed || Up.User == null) return;
            if (_session.Mode != RefineMode.None) _mode = _session.Mode;
            UserItem item = _session.Mode != RefineMode.None ? RefineSession.Target : null;
            ItemInfo info = item?.Info;
            ServerPackets.ServerRules rules = Rules;
            bool check = Check;
            SetTitle(W.T(check ? ClientTextKeys.ModernUpCheckTitle : ClientTextKeys.ModernUpRefineTitle));

            _bag.Sync();
            Up.SetText(_bagHint, W.T(check ? ClientTextKeys.ModernUpBagHintCheck : ClientTextKeys.ModernUpBagHintRefine));

            // the anvil
            bool sending = _sentId != 0 && Up.BagSlot(_sentId) is int s && s >= 0 && ItemMoves.IsLocked(MirGridType.Inventory, s);
            if (!sending) _sentId = 0;
            _anvil.Refresh();
            Up.SetText(_anvilName, item != null ? Up.Name(item) : W.T(check ? ClientTextKeys.ModernUpPutRefined : ClientTextKeys.ModernUpPutWeapon));
            _anvilName.Colour = info != null ? Up.Grade(info.Grade) : Color.White;
            string sub;
            if (item != null && info != null)
            {
                Stats wa = item.AddedStats;
                string added = string.Empty;
                for (int j = 1; j <= 3; j++)
                {
                    int v = wa[RefineCalc.MaxStat(j)];
                    if (v > 0) added += (added.Length > 0 ? " " : string.Empty) + "+" + v + " " + RefineCalc.StatName(j);
                }
                sub = W.T(ClientTextKeys.ModernUpLevelN, info.RequiredAmount);
                if (added.Length > 0) sub += " · " + W.T(ClientTextKeys.ModernUpRefinedN, added);
                if (wa[Stat.Luck] > 0) sub += " · " + W.T(ClientTextKeys.ModernUpLuckN, wa[Stat.Luck]);
            }
            else sub = W.T(check ? ClientTextKeys.ModernUpCheckSub : (rules?.OnlyRefineWeapon ?? true) ? ClientTextKeys.ModernUpOnlyWeapons : ClientTextKeys.ModernUpGearToo);
            Up.SetText(_anvilSub, sub);
            Up.Show(_takeBack, item != null);

            // the grid (refine page) or the card (check page)
            Up.Show(_gridCaption, !check);
            Up.Show(_gridCount, !check);
            Up.Show(_gridBox, !check);
            for (int i = 0; i < 3; i++) Up.Show(_statBoxes[i], !check);
            Up.Show(_card, check);
            int used = 0;
            for (int i = 0; i < GameScene.Refine.Length; i++) if (GameScene.Refine[i] != null) used++;
            Up.SetText(_gridCount, used + " / " + GridSlots);

            RefineCalc calc = !check && item != null && rules != null ? RefineCalc.Run(item, GameScene.Refine, rules) : null;
            if (!check) RenderTotals(calc);
            else RenderCard(item, rules);

            // warnings
            string warn = string.Empty;
            if (!check)
            {
                void Add(string t) { if (!string.IsNullOrEmpty(t)) warn += (warn.Length > 0 ? "\n" : string.Empty) + t; }
                if (item != null) Add(_session.Refusal(item));
                if (calc != null && calc.WeaponsLost > 0) Add(W.T(ClientTextKeys.ModernUpWarnWeapons, calc.WeaponsLost));
                if (calc != null && calc.Zero.Length == 0 && calc.Value == 0) Add(W.T(ClientTextKeys.ModernUpWarnTie));
                if (calc != null) Add(calc.Zero);
                if (calc != null && calc.Unknown > 0) Add(W.T(ClientTextKeys.ModernUpWarnUnknown));
            }
            Up.SetText(_warn, warn);
            Up.SetText(_note2, _note);

            RenderSide(item, info, calc, rules, sending);
        }

        /// <summary>DC / MC / SC from the jewellery (the same tally as RefineCalc; before a weapon is on the anvil too).</summary>
        private void RenderTotals(RefineCalc calc)
        {
            int dc = 0, mc = 0, sc = 0;
            if (calc != null) { dc = calc.Dc; mc = calc.Mc; sc = calc.Sc; }
            else
                for (int i = 0; i < GameScene.Refine.Length; i++)
                {
                    UserItem it = GameScene.Refine[i];
                    ItemInfo ii = it?.Info;
                    if (ii == null || ii.Type == ItemType.Weapon) continue;
                    if (ii.Stats[Stat.MaxDC] <= 0 && ii.Stats[Stat.MaxMC] <= 0 && ii.Stats[Stat.MaxSC] <= 0) continue;
                    dc += ii.Stats[Stat.MinDC] + ii.Stats[Stat.MaxDC] + it.AddedStats[Stat.MaxDC];
                    mc += ii.Stats[Stat.MinMC] + ii.Stats[Stat.MaxMC] + it.AddedStats[Stat.MaxMC];
                    sc += ii.Stats[Stat.MinSC] + ii.Stats[Stat.MaxSC] + it.AddedStats[Stat.MaxSC];
                }
            int top = Math.Max(dc, Math.Max(mc, sc));
            int tops = (dc == top ? 1 : 0) + (mc == top ? 1 : 0) + (sc == top ? 1 : 0);
            int[] vals = { dc, mc, sc };
            for (int j = 0; j < 3; j++)
            {
                int mode = calc != null && calc.Value == j + 1 ? 1 : (calc == null || calc.Value == 0) && top > 0 && vals[j] == top && tops > 1 ? 2 : 0;
                if (_statVals[j] == vals[j] && _statMode[j] == mode) continue;
                _statVals[j] = vals[j];
                _statMode[j] = mode;
                _statBoxes[j].Invalidate();
            }
        }

        /// <summary>The check page: what the refine stored on the item (RefinedValue, RefineAdded, RefineSuccessChance).</summary>
        private void RenderCard(UserItem item, ServerPackets.ServerRules rules)
        {
            var rows = new List<(string, string)>();
            if (item == null) rows.Add((W.T(ClientTextKeys.ModernUpNoItemOnAnvil), string.Empty));
            else
            {
                int v = (int)item.RefinedValue;
                string stat = RefineCalc.StatName(v);
                rows.Add((W.T(ClientTextKeys.ModernUpRefinedStat), v == 0 ? W.T(ClientTextKeys.ModernUpNoneSmashes) : stat));
                rows.Add((W.T(ClientTextKeys.ModernUpBonusIfPasses), item.RefineAdded > 0 ? ("+" + item.RefineAdded + " " + stat).Trim() : "—"));
                rows.Add((W.T(ClientTextKeys.ModernUpCritBonus), rules != null && item.RefineAdded > 0 ? ("+" + item.RefineAdded * rules.RefineCritIncrease + " " + stat).Trim() : "—"));
                rows.Add((W.T(ClientTextKeys.ModernUpStoredChance), item.RefineSuccessChance + "%"));
            }
            bool same = rows.Count == _cardRows.Count;
            for (int i = 0; same && i < rows.Count; i++) same = rows[i] == _cardRows[i];
            if (same) return;
            _cardRows.Clear();
            _cardRows.AddRange(rows);
            _card.Invalidate();
        }

        private void RenderSide(UserItem item, ItemInfo info, RefineCalc calc, ServerPackets.ServerRules rules, bool sending)
        {
            bool check = Check;
            int chance = 0;
            bool known = false, hasStat = false;
            if (check && item != null) { chance = item.RefineSuccessChance; hasStat = item.RefinedValue != RefinedValue.None; known = true; }
            else if (calc != null) { chance = calc.Chance; hasStat = calc.Value != 0; known = true; }
            _ring.Set(known ? Math.Clamp(chance, 0, 100) / 100f : 0f, known ? Math.Max(0, chance) + "%" : "—", W.T(ClientTextKeys.ModernUpSuccessChance));
            Up.SetText(_ringNote, !known
                ? W.T(check ? ClientTextKeys.ModernUpRingCheckEmpty : rules != null ? ClientTextKeys.ModernUpRingRefineEmpty : ClientTextKeys.ModernUpRulesMissing)
                : W.T(check ? ClientTextKeys.ModernUpRingCheck : ClientTextKeys.ModernUpRingRefine));
            Up.Show(_oddsCaption, known);
            Up.Show(_odds, known);
            Up.Show(_oddsText, known);
            if (known)
            {
                var o = RefineCalc.Odds(chance, hasStat, rules?.RefineCritChance ?? -1);
                _odds.Set((float)o.Win, (float)o.Crit, (float)o.Smash);
                int inc = check ? item.RefineAdded : rules?.RefineIncrease ?? 0;
                int v = check ? (int)item.RefinedValue : calc?.Value ?? 0;
                string stat = v == 0 ? W.T(ClientTextKeys.ModernUpStat) : RefineCalc.StatName(v);
                SetOdds(Up.Pct(o.Win), " +" + inc + " " + stat, rules != null ? Up.Pct(o.Crit) : "?",
                    " " + W.T(ClientTextKeys.ModernUpCritPlus, rules != null ? (inc * rules.RefineCritIncrease).ToString() : "?"),
                    Up.Pct(o.Smash), " " + W.T(ClientTextKeys.ModernUpSmashed));
            }

            // the button
            if (check)
            {
                bool testing = _session.Testing;
                _go.Text = W.T(testing ? ClientTextKeys.ModernUpTesting : ClientTextKeys.ModernUpTestBtn);
                _go.SetDisabled(testing || item == null || item.RefineAdded == 0,
                    W.T(testing ? ClientTextKeys.ModernUpWaitingSmith : item == null ? ClientTextKeys.ModernUpPutRefined : ClientTextKeys.ModernUpNotRefined));
                Up.SetText(_cost, W.T(ClientTextKeys.ModernUpTestFree));
            }
            else
            {
                uint cost = RefineSession.Cost(item);
                string why = string.Empty;
                if (sending) why = W.T(ClientTextKeys.ModernUpWaitingSmith);
                else if (item == null) why = W.T(ClientTextKeys.ModernUpPutWeapon);
                else _session.CanRefine(out why);
                _go.Text = sending ? W.T(ClientTextKeys.ModernUpRefining) : item != null ? W.T(ClientTextKeys.ModernUpRefineCost, W.N(cost)) : W.T(ClientTextKeys.ModernUpRefineBtn);
                _go.SetDisabled(why.Length > 0, why);
                string text = string.Empty;
                if (item != null)
                {
                    text = W.T(ClientTextKeys.ModernUpCostRule, W.N1(GameScene.NPCRate));
                    if (rules != null) text += " " + (rules.RefineTime > 0 ? W.T(ClientTextKeys.ModernUpKeepsFor, rules.RefineTime) : W.T(ClientTextKeys.ModernUpReadyAtOnce));
                }
                Up.SetText(_cost, text);
            }

            // the last test's result (check page)
            bool res = check && _session.ResultKind != 0;
            Up.Show(_res, res);
            if (res) _res.Invalidate();

            // how it adds up (refine page with the rules)
            List<RefinePart> parts = calc != null && calc.Zero.Length == 0 ? calc.Parts : null;
            Up.Show(_partsCaption, parts != null);
            Up.Show(_parts, parts != null);
            if (parts != null)
            {
                string key = string.Empty;
                for (int i = 0; i < parts.Count; i++) key += parts[i].Name + "|" + parts[i].Why + "|" + parts[i].Value + ",";
                if (key != _partsKey)
                {
                    _partsKey = key;
                    _shownParts.Clear();
                    _shownParts.AddRange(parts);
                    Size sz = new Size(_parts.Size.Width, S(parts.Count * 28));
                    if (_parts.Size != sz) _parts.Size = sz;
                    _parts.Invalidate();
                }
            }
            Up.SetText(_steps, W.T(check ? ClientTextKeys.ModernUpStepsCheck : ClientTextKeys.ModernUpStepsRefine));

            int y = Up.Flow(12, 8, _ring, _ringNote, _oddsCaption, _odds, _oddsText, _go, _cost, _res, _partsCaption, _parts, _steps);
            int h = S(y + 8);
            if (_side.ContentHeight != h) _side.ContentHeight = h;
        }

        private void SetOdds(params string[] parts)
        {
            bool same = true;
            for (int i = 0; i < 6; i++) if (_oddsParts[i] != parts[i]) { same = false; _oddsParts[i] = parts[i]; }
            if (!same) _oddsText.Invalidate();
        }

        // ------------------------------------------------------------------ painting

        private void PaintForge(Graphics g, PaintBox b)
        {
            // radial-gradient(ellipse 55% 45% at 50% 62%, rgba(255,120,40,.12), transparent 70%) over the page
            using (var bg = new SolidBrush(W.Page)) g.FillRectangle(bg, 0, 0, b.Size.Width, b.Size.Height);
            float w = b.Size.Width * 1.1f, h = b.Size.Height * 0.9f;
            using var path = new GraphicsPath();
            path.AddEllipse(b.Size.Width / 2f - w / 2f, b.Size.Height * 0.62f - h / 2f, w, h);
            using var brush = new PathGradientBrush(path)
            {
                CenterColor = ModernTheme.Mix(Color.FromArgb(255, 255, 120, 40), W.Page, 0.12f),
                SurroundColors = new[] { W.Page },
            };
            g.FillPath(brush, path);
        }

        private void PaintCrucible(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
            using (var lb = new LinearGradientBrush(new RectangleF(0, 0, b.Size.Width, b.Size.Height + 1), ModernTheme.Mix(Color.FromArgb(255, 255, 120, 40), W.Page, 0.08f), ModernTheme.Mix(Color.Black, W.Page, 0.3f), 90f))
                g.FillPath(lb, p);
            ModernSkin.Stroke(g, p, ModernTheme.Mix(Color.FromArgb(255, 255, 140, 40), W.Page, 0.4f));
        }

        private void PaintStat(Graphics g, PaintBox b, int j)
        {
            float k = ModernRoot.K;
            int mode = _statMode[j];
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
            using (var bg = new SolidBrush(mode == 1 ? W.Over(ModernTheme.Accent, 0.16f) : W.Page)) g.FillPath(bg, p);
            ModernSkin.Stroke(g, p, mode == 1 ? ModernTheme.Accent : mode == 2 ? ModernTheme.Bad : ModernTheme.Frame);
            ModernSkin.Text(g, _statVals[j].ToString(), ModernSkin.Font(18, true), mode == 1 ? ModernTheme.Accent : Color.White, new RectangleF(0, 4 * k, b.Size.Width, 22 * k), StringAlignment.Center);
            ModernSkin.Text(g, W.T(ClientTextKeys.ModernUpFromJewellery, RefineCalc.StatName(j + 1)), ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(0, 28 * k, b.Size.Width, 14 * k), StringAlignment.Center);
        }

        private void PaintCard(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            W.Card(g, b.Size);
            float y = 10 * k;
            for (int i = 0; i < _cardRows.Count; i++)
            {
                RectangleF row = new RectangleF(12 * k, y, b.Size.Width - 24 * k, 20 * k);
                ModernSkin.Text(g, _cardRows[i].K, ModernSkin.Font(12), ModernTheme.Text, row);
                ModernSkin.Text(g, _cardRows[i].V, ModernSkin.Font(12, true), Color.White, row, StringAlignment.Far);
                y += 23 * k;
            }
        }

        private void PaintOddsText(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K, w = b.Size.Width / 3f;
            for (int i = 0; i < 3; i++)
            {
                string num = _oddsParts[i * 2] ?? string.Empty, rest = _oddsParts[i * 2 + 1] ?? string.Empty;
                Font bold = ModernSkin.Font(10, true), plain = ModernSkin.Font(10);
                float nw = ModernSkin.Measure(num, bold);
                float total = nw + ModernSkin.Measure(rest, plain);
                float x = i == 0 ? 0 : i == 1 ? w + (w - total) / 2f : b.Size.Width - total;
                ModernSkin.Text(g, num, bold, Color.White, new RectangleF(x, 0, nw + 4 * k, b.Size.Height), StringAlignment.Near, StringAlignment.Center, false);
                ModernSkin.Text(g, rest, plain, ModernTheme.Dim, new RectangleF(x + nw, 0, w + 20 * k, b.Size.Height), StringAlignment.Near, StringAlignment.Center, false);
            }
        }

        private void PaintResult(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            int kind = _session.ResultKind;
            Color c = kind == 2 ? ModernTheme.Gold : kind == 3 ? ModernTheme.BadSoft : ModernTheme.Ok;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
            using (var bg = new SolidBrush(W.Over(kind == 3 ? ModernTheme.Bad : c, 0.12f))) g.FillPath(bg, p);
            ModernSkin.Text(g, _session.ResultText, ModernSkin.Font(12, true), c, new RectangleF(6 * k, 0, b.Size.Width - 12 * k, b.Size.Height), StringAlignment.Center, StringAlignment.Center);
        }

        private void PaintParts(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K, barW = 80 * k, valW = 30 * k, gap = 8 * k;
            float nameW = b.Size.Width - barW - valW - 2 * gap;
            for (int i = 0; i < _shownParts.Count; i++)
            {
                RefinePart p = _shownParts[i];
                float y = i * 28 * k;
                bool neg = p.Value < 0;
                ModernSkin.Text(g, p.Name, ModernSkin.Font(11), ModernTheme.Text, new RectangleF(0, y, nameW, 14 * k), StringAlignment.Near, StringAlignment.Near, false);
                ModernSkin.Text(g, p.Why, ModernSkin.Font(9), ModernTheme.Dim, new RectangleF(0, y + 13 * k, nameW, 12 * k), StringAlignment.Near, StringAlignment.Near, false);
                RectangleF bar = new RectangleF(nameW + gap, y + 10 * k, barW, 6 * k);
                using (GraphicsPath track = ModernSkin.RoundRect(bar, 3 * k))
                using (var tb = new SolidBrush(ModernTheme.Field))
                    g.FillPath(tb, track);
                float f = Math.Min(1f, Math.Abs(p.Value) / (float)p.Max);
                if (f > 0)
                    using (GraphicsPath fill = ModernSkin.RoundRect(new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * f), bar.Height), 3 * k))
                    using (var fb = new SolidBrush(neg ? ModernTheme.Bad : ModernTheme.Accent))
                        g.FillPath(fb, fill);
                ModernSkin.Text(g, (p.Value > 0 ? "+" : string.Empty) + p.Value, ModernSkin.Font(11, true), neg ? ModernTheme.BadSoft : Color.White,
                    new RectangleF(b.Size.Width - valW, y, valW, 26 * k), StringAlignment.Far, StringAlignment.Center, false);
            }
        }

        // ------------------------------------------------------------------ actions (nothing moves until the server answers)

        /// <summary>A click on a bag item of the bag column: the anvil (weapons, or anything on the check page), else the grid (a
        /// matching stack with room, else the first free cell) through ItemMoves. True = handled.</summary>
        private bool Place(int bagSlot)
        {
            if (!IsOpen || _session.Mode == RefineMode.None) return false;
            UserItem item = ItemMoves.GetItem(MirGridType.Inventory, bagSlot);
            if (item == null) return false;
            if (Check || item.Info?.Type == ItemType.Weapon)
            {
                string why = _session.Take(bagSlot);
                Say(why);
                _dirty = true;
                return true;
            }
            UserItem[] grid = GameScene.Refine;
            int to = -1;
            for (int i = 0; i < grid.Length && to < 0; i++)
            {
                UserItem g = grid[i];
                if (g != null && g.Info == item.Info && item.Info != null && item.Info.StackSize > 1 && g.Count < item.Info.StackSize && !ItemMoves.IsLocked(MirGridType.Refine, i)) to = i;
            }
            for (int i = 0; i < grid.Length && to < 0; i++) if (grid[i] == null && !ItemMoves.IsLocked(MirGridType.Refine, i)) to = i;
            if (to < 0) { Say(W.T(ClientTextKeys.ModernUpGridFull)); return true; }
            ItemMoves.Selected = new ItemSlotRef(MirGridType.Inventory, bagSlot);
            ItemMoves.Click(new ItemSlotRef(MirGridType.Refine, to), new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
            if (ItemMoves.Selected != null && ItemMoves.IsSelected(MirGridType.Inventory, bagSlot)) ItemMoves.Selected = null;
            Say(string.Empty);
            return true;
        }

        /// <summary>The anvil: with a picked bag item, put it there (NPCDropDialog.ItemCell_Click); a right click frees it.</summary>
        private void AnvilClick(MouseButtons button)
        {
            if (_session.Mode == RefineMode.None) return;
            if (button == MouseButtons.Right) { _session.ClearDrop(); _dirty = true; return; }
            if (button != MouseButtons.Left) return;
            ItemSlotRef pick = ItemMoves.Selected;
            if (pick == null)
            {
                if (RefineSession.Target != null) { _session.ClearDrop(); _dirty = true; }
                return;
            }
            ItemMoves.Selected = null;
            if (pick.GridType != MirGridType.Inventory) return;
            Say(_session.Take(pick.ItemSlot));
            _dirty = true;
        }

        /// <summary>Refine (C.RefineItem after a question) or Test (C.CheckRefine after a question).</summary>
        private void Go()
        {
            UserItem item = RefineSession.Target;
            Popups pop = Up.Pop;
            if (item == null || pop == null) return;
            ServerPackets.ServerRules rules = Rules;
            string name = Up.Name(item);
            if (Check)
            {
                if (_session.Testing) return;
                if (item.RefineAdded == 0) { Up.Say(W.T(ClientTextKeys.ItemHasntBeenRefinedNoChecking, item.FriendlyName)); return; }
                var o = RefineCalc.Odds(item.RefineSuccessChance, item.RefinedValue != RefinedValue.None, rules?.RefineCritChance ?? -1);
                string text = W.T(ClientTextKeys.ModernUpTestAsk, name) + "\n" + (item.RefinedValue == RefinedValue.None
                    ? W.T(ClientTextKeys.ModernUpTestAskNoStat)
                    : W.T(ClientTextKeys.ModernUpTestAskOdds, Up.Pct(o.Smash), RefineCalc.StatName((int)item.RefinedValue)));
                ulong id = item.UniqueID;
                pop.Ask(text, ok =>
                {
                    UserItem now = RefineSession.Target;
                    if (!ok || !IsOpen || _session.Mode != RefineMode.Check || now == null || now.UniqueID != id) return;
                    _session.SendTest();
                    _dirty = true;
                }, true, PopupKind.Warning, W.T(ClientTextKeys.CheckRefine));
                return;
            }
            if (_sentId != 0) return;
            if (!_session.CanRefine(out string why)) { if (why.Length > 0) Up.Say(why); return; }
            string ask = W.T(ClientTextKeys.ModernUpRefineAsk, name);
            if (rules != null && rules.RefineTime > 0) ask += "\n" + W.T(ClientTextKeys.ModernUpRefineAskTime, rules.RefineTime);
            ask += "\n" + W.T(ClientTextKeys.ModernUpCostN, W.N(RefineSession.Cost(item)));
            ulong uid = item.UniqueID;
            pop.Ask(ask, ok =>
            {
                UserItem now = RefineSession.Target;
                if (!ok || !IsOpen || _session.Mode != RefineMode.Refine || now == null || now.UniqueID != uid) return;
                if (_session.SendRefine()) _sentId = uid;
                _dirty = true;
            }, true, PopupKind.Question, W.T(ClientTextKeys.ModernUpRefineTitle));
        }
    }
}

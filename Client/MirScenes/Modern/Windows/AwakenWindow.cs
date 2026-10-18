// Ported from WebClient/src/ui/modern/windows/awaken.ts (AwakenSession + AwakenWindow): the new UI's awakening window.
// Desktop counterpart: NPCAwakeDialog (MirScenes/Dialogs/NPCDialogs.cs) + GameScene.NPCAwakening / AwakeningNeedMaterials /
// AwakeningLockedItem / Awakening. Server rules (PlayerObject.Awakening, Shared Awake): the item and its materials are
// searched in the BAG only; materials are the item's grade in the counts the server sends; the first awakening fixes the
// type; a failure deletes the item.
//  - opens when the server opens it (S.NPCAwakening → ModernUi.NpcAwakening, with the bag) and closes on a new NPC page, when
//    the NPC dialog closes and on every S.Awakening answer (ModernUi.ItemAwakeningClosing, as GameScene.Awakening hides the
//    dialog); closing it on any path is NPCAwakeDialog.Hide: every cell's item is handed back (C.AwakeningLockedItem Locked
//    false, echoed by the server, which unlocks the bag cell);
//  - the gear list is the bag's weapons / helmets / armour with a grade (the reasons are the server's refusals); a click puts
//    the item in the base cell through ItemMoves (the "To Awakening" route: a local lock, ItemAwakeItemChanged +
//    ItemAwakeTypeSelect(0)); the type chips are NPCAwakeDialog.SelectAwakeType (C.AwakeningNeedMaterials); the material
//    counts and the price come from the server (S.AwakeningNeedMaterials → AwakeItems 1-2, UserItem.AwakeningPrice), the
//    odds from S.ServerRules;
//  - the material cells 3-4 (ModernItemCell(MirGridType.AwakenItem): click a bag stack in, click it back out) are filled
//    from the bag before the hold-to-confirm Awaken sends C.Awakening, as NPCAwakeDialog.CheckNeedMaterials requires; the
//    window then waits for the answer (10 s at most); success / destroyed / refusals are popups.
// HIDDEN: the hit orbs and the roll animation (the server plays them as effects), the extra cells 5-6 (C.Awakening ignores
// them) and Downgrade (only the NPC's [@DOWNGRADE] drop panel does it; this window names its price). Per-level gains (the
// desktop UserItem keeps only the level count and the total) show as the total.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirNetwork;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;
using S = ServerPackets;

namespace Client.MirScenes.Modern.Windows
{
    /// <summary>
    /// NPCAwakeDialog's state outside the window (awaken.ts AwakenSession + the classic SelectAwakeType / CurrentAwakeType): the
    /// type choice and the C.Awakening in flight (for the result popups after the window has closed). One per modern UI.
    /// </summary>
    public sealed class AwakenSession
    {
        public const int Base = 0, AnswerMs = 10000;
        public static readonly int[] MatCells = { 3, 4 };

        /// <summary>SelectAwakeType's list (index 0 = "pick a type" for a new item; the item's own type when awakened).</summary>
        public readonly List<AwakeType> Types = new List<AwakeType>();
        public int Selected;
        public AwakeType Current = AwakeType.None;
        public uint? Price;

        public const int ChipGapMs = 300;
        // the last C.AwakeningNeedMaterials (item + type) since the base item last changed: ItemMoves' new-item route calls
        // ItemChanged (→ TypeSelect(0) for an awakened item) and then ItemAwakeTypeSelect(0), which must not ask twice
        private ulong _askedId;
        private AwakeType _askedType = AwakeType.None;
        private long _nextChip;
        private ulong _pendingId;
        private string _pendingName = string.Empty;
        private int _pendingLevel;
        private long _pendingUntil;

        public event Action Changed;

        public static UserItem[] Items => GameScene.Models.Npc.AwakeItems;
        public static int[] Idx => GameScene.Models.Npc.AwakeItemsIdx;
        public bool Busy => _pendingId != 0 && CMain.Time < _pendingUntil;

        private void Emit() => Changed?.Invoke();

        public static string TypeName(AwakeType t) => t switch
        {
            AwakeType.DC => W.T(ClientTextKeys.ModernUpDC),
            AwakeType.MC => W.T(ClientTextKeys.ModernUpMC),
            AwakeType.SC => W.T(ClientTextKeys.ModernUpSC),
            AwakeType.AC => W.T(ClientTextKeys.ModernUpAC),
            AwakeType.MAC => W.T(ClientTextKeys.ModernUpMAC),
            AwakeType.HPMP => W.T(ClientTextKeys.ModernUpHPMP),
            _ => string.Empty,
        };

        /// <summary>The glyph names of NPCAwakeDialog.getAwakeTypeText (the chips' hints).</summary>
        public static string Glyph(AwakeType t) => t switch
        {
            AwakeType.DC => W.T(ClientTextKeys.BraveryGlyph),
            AwakeType.MC => W.T(ClientTextKeys.MagicGlyph),
            AwakeType.SC => W.T(ClientTextKeys.SoulGlyph),
            AwakeType.AC => W.T(ClientTextKeys.ProtectionGlyph),
            AwakeType.MAC => W.T(ClientTextKeys.EvilSlayerGlyph),
            AwakeType.HPMP => W.T(ClientTextKeys.BodyGlyph),
            _ => W.T(ClientTextKeys.SelectUpgradeItem),
        };

        /// <summary>NPCAwakeDialog.ItemCellClear + ItemCell_Click: the material view cells clear, the type list follows the base.</summary>
        public void ItemChanged()
        {
            ItemMoves.SetItem(MirGridType.AwakenItem, 1, null);
            ItemMoves.SetItem(MirGridType.AwakenItem, 2, null);
            Price = null;
            Types.Clear();
            _askedId = 0; // the material view cleared: the next type choice asks again
            _askedType = AwakeType.None;
            UserItem b = Items[Base];
            if (b?.Info == null)
            {
                Types.Add(AwakeType.None);
                Selected = 0;
                Current = AwakeType.None;
            }
            else if (b.Awake.GetAwakeLevel() == 0)
            {
                Types.Add(AwakeType.None);
                if (b.Info.Type == ItemType.Weapon) { Types.Add(AwakeType.DC); Types.Add(AwakeType.MC); Types.Add(AwakeType.SC); }
                else if (b.Info.Type == ItemType.Helmet) { Types.Add(AwakeType.AC); Types.Add(AwakeType.MAC); }
                else Types.Add(AwakeType.HPMP);
                if (Selected >= Types.Count) Selected = 0;
            }
            else
            {
                Types.Add(b.Awake.Type);
                if (Current != b.Awake.Type)
                {
                    Current = b.Awake.Type;
                    TypeSelect(0);
                }
            }
            Emit();
        }

        /// <summary>NPCAwakeDialog.OnAwakeTypeSelect: C.AwakeningNeedMaterials for a real type.</summary>
        public void TypeSelect(int index)
        {
            if (index < 0 || index >= Types.Count) index = 0;
            Selected = index;
            AwakeType type = Types.Count > 0 ? Types[index] : AwakeType.None;
            Current = type;
            UserItem b = Items[Base];
            if (type != AwakeType.None && b != null && (b.UniqueID != _askedId || type != _askedType))
            {
                _askedId = b.UniqueID;
                _askedType = type;
                Network.Enqueue(new C.AwakeningNeedMaterials { UniqueID = b.UniqueID, Type = type });
            }
            Emit();
        }

        /// <summary>A type chip click: at most one per ChipGapMs (each one is a C.AwakeningNeedMaterials); false = ignored.</summary>
        public bool TypeClick(int index)
        {
            if (Busy || Selected == index || CMain.Time < _nextChip) return false;
            _nextChip = CMain.Time + ChipGapMs;
            TypeSelect(index);
            return true;
        }

        /// <summary>NPCAwakeDialog.setNeedItems (S.AwakeningNeedMaterials): the two material view cells and the price.</summary>
        public void NeedMaterials(S.AwakeningNeedMaterials p)
        {
            for (int i = 0; i < 2; i++)
            {
                byte count = p.MaterialsCount != null && i < p.MaterialsCount.Length ? p.MaterialsCount[i] : (byte)0;
                ItemInfo info = p.Materials != null && i < p.Materials.Length ? p.Materials[i] : null;
                if (count != 0 && info != null)
                    ItemMoves.SetItem(MirGridType.AwakenItem, 1 + i, new UserItem(info) { Count = count });
                else ItemMoves.SetItem(MirGridType.AwakenItem, 1 + i, null);
            }
            UserItem b = Items[Base];
            Price = b?.Info != null ? b.AwakeningPrice() : null;
            Emit();
        }

        /// <summary>NPCAwakeDialog.CheckNeedMaterials: each needed material sits in a material cell in a big enough stack.</summary>
        public static bool CheckNeedMaterials()
        {
            UserItem[] items = Items;
            int maxEqual = items[1] == null || items[2] == null ? 1 : 2;
            int equal = 0;
            for (int i = 1; i < 3; i++)
            {
                if (items[i]?.Info == null) continue;
                for (int j = 3; j < 5; j++)
                {
                    if (items[j]?.Info == null) continue;
                    if (items[i].Info.Name == items[j].Info.Name && items[i].Count <= items[j].Count) equal++;
                }
            }
            return equal >= maxEqual;
        }

        /// <summary>NPCAwakeDialog.Awakening: C.Awakening when the materials are in place. True = sent (the window waits).</summary>
        public bool Awaken()
        {
            UserItem b = Items[Base];
            if (b?.Info == null || Busy || !CheckNeedMaterials()) return false;
            AwakeType type = Selected >= 0 && Selected < Types.Count ? Types[Selected] : AwakeType.None;
            if (type == AwakeType.None) return false;
            Network.Enqueue(new C.Awakening { UniqueID = b.UniqueID, Type = type });
            MapControl.AwakeningAction = true;
            _pendingId = b.UniqueID;
            _pendingName = Up.Name(b);
            _pendingLevel = b.Awake.GetAwakeLevel();
            _pendingUntil = CMain.Time + AnswerMs;
            Emit();
            return true;
        }

        /// <summary>NPCAwakeDialog.Hide: every cell's item goes back (C.AwakeningLockedItem Locked false; the display cells 1-2
        /// hold the server's material list, not items, and send nothing), the indices reset, the type list follows.</summary>
        public void Hide()
        {
            UserItem[] items = Items;
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i] == null) continue;
                if (i != 1 && i != 2) Network.Enqueue(new C.AwakeningLockedItem { UniqueID = items[i].UniqueID, Locked = false });
                ItemMoves.SetItem(MirGridType.AwakenItem, i, null);
            }
            for (int i = 0; i < Idx.Length; i++) Idx[i] = 0;
            ItemChanged();
        }

        /// <summary>GameScene.Awakening's result (after the window closed): refusals, success, destroyed — as popups.</summary>
        public void Result(int result)
        {
            ulong id = _pendingId;
            string name = _pendingName;
            int level = _pendingLevel;
            _pendingId = 0;
            Popups pop = Up.Pop;
            if (pop == null) return;
            string title = W.T(ClientTextKeys.Awakening);
            switch (result)
            {
                case -4: pop.Confirm(W.T(ClientTextKeys.NotEnoughMaterials), PopupButtons.Ok, PopupKind.Warning, null, false, title); return;
                case -3: pop.Confirm(W.T(ClientTextKeys.LowGold), PopupButtons.Ok, PopupKind.Warning, null, false, title); return;
                case -2: pop.Confirm(W.T(ClientTextKeys.AwakeningMaxLevel), PopupButtons.Ok, PopupKind.Warning, null, false, title); return;
                case -1: pop.Confirm(W.T(ClientTextKeys.CannotAwakenItem), PopupButtons.Ok, PopupKind.Warning, null, false, title); return;
            }
            if (id == 0) return; // not ours (the classic dialog shows nothing for 0 / 1)
            if (result == 1)
            {
                int slot = Up.BagSlot(id);
                UserItem item = slot >= 0 ? Up.User.Inventory[slot] : null;
                bool fresh = item != null && item.Awake.GetAwakeLevel() > level; // the server refreshed the item before S.Awakening
                int now = fresh ? item.Awake.GetAwakeLevel() : level + 1;
                string gain = fresh ? " (" + TypeName(item.Awake.Type) + " +" + item.Awake.GetAwakeValue() + ")" : string.Empty;
                pop.Confirm(W.T(ClientTextKeys.ModernUpAwakeSuccess, name, now) + gain, PopupButtons.Ok, PopupKind.Info, null, false, title);
            }
            else if (result == 0)
                pop.Confirm(W.T(ClientTextKeys.ModernUpAwakeDestroyed, name), PopupButtons.Ok, PopupKind.Danger, null, false, title);
        }

        /// <summary>The server stays silent when the page moved on: free the window after the wait.</summary>
        public void Poll()
        {
            if (_pendingId != 0 && CMain.Time >= _pendingUntil) { _pendingId = 0; Emit(); }
        }

        public void Dispose() => _pendingId = 0;
    }

    public sealed class AwakenWindow : ModernWindow
    {
        public const string WindowKey = "awaken";
        private const int WidthHud = 960, HeightHud = 560, ListW = 230, SideW = 310, StageX = ListW + 1, StageW = WidthHud - ListW - SideW - 2, SideX = WidthHud - SideW;

        private readonly AwakenSession _session;
        private bool _dirty = true, _npcSeen;
        private long _nextPoll;
        private uint _gold;
        private string _listKey = string.Empty, _typesKey = string.Empty, _note = string.Empty;

        private ScrollView _list;
        private ModernLabel _listCount, _listEmpty, _headName, _headSub, _levels, _down, _matLabel, _why, _noteLabel;
        private ModernLabel _oddsSuccess, _oddsHit, _oddsGain, _price;
        private string _dangerText = string.Empty;
        private PaintBox _circle, _blocked, _danger;
        private ModernItemCell _baseCell;
        private ModernPanel _typesBox, _matSection;
        private readonly PaintBox[] _mats = new PaintBox[2];
        private readonly ModernItemCell[] _matShow = new ModernItemCell[2];
        private readonly string[] _matText = new string[2], _matCount = new string[2];
        private readonly bool[] _matOk = new bool[2];
        private readonly ModernItemCell[] _matCells = new ModernItemCell[2];
        private HoldButton _go;
        private string _blockedText = string.Empty;
        private int _pips, _level;
        private readonly List<(PaintBox Row, int Slot, ulong Id)> _rows = new List<(PaintBox, int, ulong)>();

        private readonly GridWatch _bagWatch = new GridWatch();
        private readonly Action _changed;
        private readonly Action<ItemSlotKey> _slotChanged;
        private readonly Action<UserItem> _itemInfo;

        public AwakenWindow(AwakenSession session) : base(WindowKey, W.T(ClientTextKeys.ModernUpAwakenTitle), 20, 30)
        {
            _session = session;
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
            _changed = () => _dirty = true;
            _slotChanged = k => { if (k.Grid == MirGridType.Inventory || k.Grid == MirGridType.AwakenItem || k.Grid == MirGridType.None) _dirty = true; };
            _itemInfo = _ => { _listKey = string.Empty; _dirty = true; };
        }

        internal IReadOnlyList<(PaintBox Row, int Slot, ulong Id)> Rows => _rows;
        internal ModernPanel TypesBox => _typesBox;
        internal HoldButton GoButton => _go;
        internal ModernItemCell BaseCell => _baseCell;

        private static UserItem[] Items => AwakenSession.Items;
        private static S.ServerRules Rules => GameScene.Models?.ServerRules.Rules;

        protected override void Build()
        {
            const int top = TitleHeight;
            // ---- the bag's gear
            W.Caption(this, W.T(ClientTextKeys.ModernWinYourBag), 10, top + 10, 120);
            _listCount = W.Label(this, string.Empty, 130, top + 10, ListW - 140, 14, 11, true, Color.Empty, StringAlignment.Far);
            _list = new ScrollView(ListW - 10, HeightHud - 40) { Parent = this, Location = new Point(S(6), S(top + 30)), Background = W.Page };
            _list.Framed = false;
            _listEmpty = W.Label(this, W.T(ClientTextKeys.ModernUpNoGear), 12, top + 34, ListW - 24, 60, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
            W.VDivider(this, ListW, top, HeightHud);

            // ---- the stage: name, the circle with the base cell and the level pips, the type chips, the levels
            int cx = StageX + StageW / 2;
            new PaintBox(PaintStageBg) { Parent = this, Location = new Point(S(StageX), S(top)), Size = new Size(S(StageW), S(HeightHud)), NotControl = true };
            _headName = W.Label(this, string.Empty, StageX + 10, top + 34, StageW - 20, 24, 18, true, Color.White, StringAlignment.Center);
            _headSub = W.Label(this, string.Empty, StageX + 10, top + 60, StageW - 20, 30, 11, false, ModernTheme.Dim, StringAlignment.Center, true);
            _circle = new PaintBox(PaintCircle) { Parent = this, Location = new Point(S(cx - 125), S(top + 96)), Size = new Size(S(250), S(250)), NotControl = true };
            _baseCell = new ModernItemCell(MirGridType.AwakenItem, AwakenSession.Base, 72) { Parent = this, Location = new Point(S(cx - 36), S(top + 96 + 125 - 36)) };
            _typesBox = new ModernPanel { Parent = this, Location = new Point(S(StageX + 10), S(top + 360)), Size = new Size(S(StageW - 20), S(30)) };
            _levels = W.Label(this, string.Empty, StageX + 10, top + 398, StageW - 20, 34, 11, false, ModernTheme.Dim, StringAlignment.Center, true);
            _down = W.Label(this, string.Empty, StageX + 20, top + 436, StageW - 40, 30, 10, false, ModernTheme.Dim, StringAlignment.Center, true);
            _noteLabel = W.Label(this, string.Empty, StageX + 10, top + HeightHud - 26, StageW - 20, 16, 11, false, ModernTheme.Accent, StringAlignment.Center);
            _typesKey = string.Empty;
            W.VDivider(this, SideX - 1, top, HeightHud);

            // ---- side: the blocked reason, or the materials, odds, price, the warning and the hold-to-confirm Awaken
            int sx = SideX + 14, sw = SideW - 28;
            _blocked = new PaintBox((g, b) => PaintDanger(g, b, _blockedText)) { Parent = this, Location = new Point(S(sx), S(top + 12)), Size = new Size(S(sw), S(54)), NotControl = true };
            _matSection = new ModernPanel { Parent = this, Location = new Point(S(sx), S(top + 12)), Size = new Size(S(sw), S(HeightHud - 24)) };
            MirControl m = _matSection;
            W.Caption(m, W.T(ClientTextKeys.ModernUpMaterials), 0, 0, 120);
            _matLabel = W.Label(m, string.Empty, 110, 0, sw - 110, 14, 11, true, Color.Empty, StringAlignment.Far);
            for (int i = 0; i < 2; i++)
            {
                int n = i;
                _mats[i] = new PaintBox((g, b) => PaintMat(g, b, n)) { Parent = m, Location = new Point(0, S(20 + i * 50)), Size = new Size(S(sw), S(46)), NotControl = true };
                _matShow[i] = new ModernItemCell(MirGridType.AwakenItem, 1 + i, 36) { Parent = _mats[i], Location = new Point(S(5), S(5)), ReadOnly = true };
                _matText[i] = _matCount[i] = string.Empty;
            }
            for (int i = 0; i < 2; i++)
                _matCells[i] = new ModernItemCell(MirGridType.AwakenItem, AwakenSession.MatCells[i], 36) { Parent = m, Location = new Point(S(i * 44), S(124)) };
            W.Label(m, W.T(ClientTextKeys.ModernUpMatCellsHint), 90, 122, sw - 90, 42, 10, false, ModernTheme.Dim, StringAlignment.Near, true);
            W.Caption(m, W.T(ClientTextKeys.ModernUpOdds), 0, 172, 120);
            int ky = 190;
            ModernLabel Kv(ClientTextKeys key, int y)
            {
                W.Label(m, W.T(key), 0, y, sw - 120, 18, 12, false, ModernTheme.Dim);
                return W.Label(m, string.Empty, sw - 160, y, 160, 18, 12, true, Color.White, StringAlignment.Far);
            }
            _oddsSuccess = Kv(ClientTextKeys.ModernUpOddsSuccess, ky);
            _oddsHit = Kv(ClientTextKeys.ModernUpOddsHit, ky + 20);
            _oddsGain = Kv(ClientTextKeys.ModernUpOddsGain, ky + 40);
            _price = Kv(ClientTextKeys.ModernUpPrice, ky + 68);
            _danger = new PaintBox((g, b) => PaintDanger(g, b, _dangerText + " " + W.T(ClientTextKeys.ModernUpAwakeDanger))) { Parent = m, Location = new Point(0, S(ky + 96)), Size = new Size(S(sw), S(58)), NotControl = true };
            _go = new HoldButton(W.T(ClientTextKeys.ModernUpHoldToAwaken), true, true) { Parent = m, Location = new Point(0, S(ky + 164)), Tooltip = W.T(ClientTextKeys.ModernUpHoldToAwakenHint) };
            _go.Size = new Size(S(sw), S(40));
            _go.FontSize = 14;
            _go.Done += Awaken;
            _why = W.Label(m, string.Empty, 0, ky + 210, sw, 32, 11, false, ModernTheme.BadSoft, StringAlignment.Center, true);

            _listKey = string.Empty;
            _bagWatch.Reset();
            _dirty = true;
            Render();
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void Shown()
        {
            _note = string.Empty;
            _npcSeen = Up.Ui?.IsNpcDialogOpen() ?? false;
            _session.Changed += _changed;
            ItemMoves.SlotChanged.On(_slotChanged);
            ModernEvents.ItemInfo.On(_itemInfo);
            ModernEvents.InventoryResized.On(_changed);
            GameScene.Models?.ServerRules.Changed.On(_changed);
            _session.ItemChanged();
            ModernRoot.Instance?.Wm.Open(InventoryWindow.WindowKey); // NPCAwakeDialog.Show opens the bag too
            _dirty = true;
        }

        /// <summary>Every close path: NPCAwakeDialog.Hide (the cells' items go back through the server).</summary>
        protected override void Closed()
        {
            _session.Changed -= _changed;
            ItemMoves.SlotChanged.Off(_slotChanged);
            ModernEvents.ItemInfo.Off(_itemInfo);
            ModernEvents.InventoryResized.Off(_changed);
            GameScene.Models?.ServerRules.Changed.Off(_changed);
            ItemSlotRef pick = ItemMoves.Selected;
            if (pick != null && pick.GridType == MirGridType.AwakenItem) ItemMoves.Selected = null;
            _session.Hide();
        }

        public override void Process()
        {
            base.Process();
            if (Up.User == null) return;
            if (CMain.Time >= _nextPoll)
            {
                _nextPoll = CMain.Time + GridWatch.IntervalMs;
                bool busy = _session.Busy;
                _session.Poll();
                if (busy != _session.Busy) _dirty = true;
                bool npc = Up.Ui?.IsNpcDialogOpen() ?? false;
                if (_npcSeen && !npc) { Close(); return; }
                _npcSeen |= npc;
                if (_gold != GameScene.Gold) { _gold = GameScene.Gold; _dirty = true; }
                if (_bagWatch.Poll(MirGridType.Inventory, Up.User.Inventory, 0, int.MaxValue, true)) _dirty = true;
            }
            if (_dirty) Render();
        }

        // ------------------------------------------------------------------ rules

        private static bool IsGear(ItemInfo info) => info != null && (info.Type == ItemType.Weapon || info.Type == ItemType.Helmet || info.Type == ItemType.Armour);

        /// <summary>The server's refusals for an item as the base (PlayerObject.Awakening / Awake.CheckAwakening), "" = it may try.</summary>
        private static string Blocked(UserItem item)
        {
            ItemInfo info = item?.Info;
            if (!IsGear(info)) return W.T(ClientTextKeys.ModernUpAwOnlyGear);
            if (info.Grade == ItemGrade.None) return W.T(ClientTextKeys.ModernUpAwNoGrade);
            if (info.Bind.HasFlag(BindMode.DontUpgrade)) return W.T(ClientTextKeys.ModernUpAwCantUpgrade);
            if (item.RentalInformation != null && item.RentalInformation.BindingFlags.HasFlag(BindMode.DontUpgrade)) return W.T(ClientTextKeys.ModernUpAwRented);
            if (!info.CanAwakening) return W.T(ClientTextKeys.ModernUpAwCant);
            S.ServerRules rules = Rules;
            if (rules != null && item.Awake.GetAwakeLevel() >= rules.MaxAwakeLevel) return W.T(ClientTextKeys.ModernUpAwFull);
            return string.Empty;
        }

        private static string TypeLabel(ItemType t) => W.T(t == ItemType.Weapon ? ClientTextKeys.ModernUpTypeWeapon : t == ItemType.Helmet ? ClientTextKeys.ModernUpTypeHelmet : ClientTextKeys.ModernUpTypeArmour);

        /// <summary>Bag stacks of a material: same name (NPCAwakeDialog compares names) and the base item's grade.</summary>
        private static (int Total, int Best, int BestCount) Stacks(ItemInfo need)
        {
            UserItem[] inv = Up.User?.Inventory;
            int total = 0, best = -1, bestCount = 0;
            if (inv == null || need == null) return (0, -1, 0);
            for (int i = 0; i < inv.Length; i++)
            {
                UserItem it = inv[i];
                if (it?.Info == null || it.Info.Name != need.Name || it.Info.Grade != need.Grade) continue;
                total += it.Count;
                bool inCell = false;
                for (int c = 0; c < AwakenSession.MatCells.Length; c++) inCell |= Items[AwakenSession.MatCells[c]]?.UniqueID == it.UniqueID;
                bool free = !ItemMoves.IsLocked(MirGridType.Inventory, i) || inCell;
                if (free && it.Count > bestCount) { best = i; bestCount = it.Count; }
            }
            return (total, best, bestCount);
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            _dirty = false;
            if (_go == null || _go.IsDisposed || Up.User == null) return;
            _baseCell.Refresh(false);
            RenderList();
            RenderStage();
            RenderSide();
            Up.SetText(_noteLabel, _note);
        }

        /// <summary>The bag's weapons / helmets / armour (rows rebuilt only when the set of items changed).</summary>
        private void RenderList()
        {
            UserItem[] inv = Up.User.Inventory;
            string key = string.Empty;
            int found = 0;
            for (int i = 0; i < inv.Length; i++)
            {
                if (inv[i] == null || !IsGear(inv[i].Info)) continue;
                found++;
                key += inv[i].UniqueID + "@" + i + ":" + Blocked(inv[i]) + ":" + inv[i].Awake.GetAwakeLevel() + ":" + ItemMoves.IsLocked(MirGridType.Inventory, i) + ",";
            }
            key += "|" + (Items[AwakenSession.Base]?.UniqueID ?? 0);
            Up.Show(_listEmpty, found == 0);
            Up.SetText(_listCount, found.ToString());
            if (key == _listKey) return;
            _listKey = key;
            for (int i = 0; i < _rows.Count; i++) _rows[i].Row.Dispose();
            _rows.Clear();
            int y = 0, w = ListW - 22;
            for (int i = 0; i < inv.Length; i++)
            {
                UserItem it = inv[i];
                if (it == null || !IsGear(it.Info)) continue;
                int slot = i;
                ulong id = it.UniqueID;
                var row = new PaintBox((g, b) => PaintGearRow(g, b, slot)) { Parent = _list.Content, Location = new Point(0, S(y)), Size = new Size(S(w), S(50)) };
                new ModernItemCell(MirGridType.Inventory, slot, 38) { Parent = row, Location = new Point(S(6), S(6)), ReadOnly = true, NotControl = true };
                row.Click += (o, e) => SelectGear(slot, id);
                _rows.Add((row, slot, id));
                y += 55;
            }
            _list.ContentHeight = S(y);
        }

        private void RenderStage()
        {
            UserItem b = Items[AwakenSession.Base];
            ItemInfo info = b?.Info;
            S.ServerRules rules = Rules;
            int level = b?.Awake.GetAwakeLevel() ?? 0;
            if (info == null)
            {
                Up.SetText(_headName, W.T(ClientTextKeys.ModernUpChooseItem));
                _headName.Colour = Color.White;
                Up.SetText(_headSub, W.T(ClientTextKeys.ModernUpChooseItemHint));
            }
            else
            {
                Up.SetText(_headName, Up.Name(info));
                _headName.Colour = Up.Grade(info.Grade);
                string sub = Up.GradeName(info.Grade) + " " + TypeLabel(info.Type) + " · " + W.T(ClientTextKeys.ModernUpAwakeningN, level + (rules != null ? "/" + rules.MaxAwakeLevel : string.Empty));
                if (level > 0) sub += " · " + AwakenSession.TypeName(b.Awake.Type) + " +" + b.Awake.GetAwakeValue();
                Up.SetText(_headSub, sub);
            }
            int pips = Math.Max(level, rules?.MaxAwakeLevel ?? 0);
            if (pips != _pips || level != _level) { _pips = pips; _level = level; _circle.Invalidate(); }
            RenderTypes(b);
            Up.SetText(_levels, b == null ? string.Empty : level > 0 ? W.T(ClientTextKeys.ModernUpAwakeTotal, level, AwakenSession.TypeName(b.Awake.Type), b.Awake.GetAwakeValue()) : W.T(ClientTextKeys.ModernUpNotAwakened));
            Up.SetText(_down, b != null && level > 0 && info != null ? W.T(ClientTextKeys.ModernUpDowngradeNote, W.N(b.DowngradePrice())) : string.Empty);
        }

        /// <summary>The type chips (SelectAwakeType): a new item picks one (C.AwakeningNeedMaterials), an awakened one shows its own.</summary>
        private void RenderTypes(UserItem b)
        {
            bool busy = _session.Busy;
            string key = (b?.UniqueID ?? 0) + "|" + (b?.Awake.Type ?? AwakeType.None) + "|" + string.Join("/", _session.Types) + "|" + _session.Selected + "|" + busy;
            if (key == _typesKey) return;
            _typesKey = key;
            for (int i = _typesBox.Controls.Count - 1; i >= 0; i--) _typesBox.Controls[i].Dispose();
            var made = new List<MirControl>();
            if (b?.Info == null)
                made.Add(W.Label(_typesBox, W.T(ClientTextKeys.ModernUpTypesHint), 0, 6, StageW - 20, 16, 11, false, ModernTheme.Dim, StringAlignment.Center));
            else if (b.Awake.GetAwakeLevel() == 0)
            {
                for (int i = 1; i < _session.Types.Count; i++)
                {
                    int idx = i;
                    AwakeType t = _session.Types[i];
                    var btn = new ModernButton(AwakenSession.TypeName(t), _session.Selected == idx ? ButtonKind.Main : ButtonKind.Normal) { Parent = _typesBox, Tooltip = AwakenSession.Glyph(t) };
                    btn.AutoWidth(64);
                    btn.SetDisabled(busy, W.T(ClientTextKeys.ModernWinWaitingServer));
                    btn.Click += (o, e) => _session.TypeClick(idx);
                    made.Add(btn);
                }
                if (_session.Selected == 0)
                {
                    var hint = W.Label(_typesBox, W.T(ClientTextKeys.ModernUpPickStat), 0, 6, 150, 16, 11, false, ModernTheme.Dim);
                    hint.AutoWidth(4);
                    made.Add(hint);
                }
            }
            else
            {
                var btn = new ModernButton(AwakenSession.TypeName(b.Awake.Type), ButtonKind.Main) { Parent = _typesBox, Tooltip = W.T(ClientTextKeys.ModernUpTypeFixed, AwakenSession.Glyph(b.Awake.Type)) };
                btn.AutoWidth(64);
                made.Add(btn);
            }
            // centre the row
            int total = 0;
            for (int i = 0; i < made.Count; i++) total += made[i].Size.Width + (i > 0 ? S(6) : 0);
            int x = (_typesBox.Size.Width - total) / 2;
            for (int i = 0; i < made.Count; i++)
            {
                made[i].Location = new Point(x, made[i] is ModernLabel ? S(6) : S(3));
                x += made[i].Size.Width + S(6);
            }
        }

        private void RenderSide()
        {
            UserItem b = Items[AwakenSession.Base];
            ItemInfo info = b?.Info;
            S.ServerRules rules = Rules;
            string why = b == null ? W.T(ClientTextKeys.ModernUpChooseToAwaken) : Blocked(b);
            if (why != _blockedText) { _blockedText = why; _blocked.Invalidate(); }
            Up.Show(_blocked, why.Length > 0);
            Up.Show(_matSection, why.Length == 0);
            if (why.Length > 0 || info == null) return;

            // the server's materials with what the bag holds of each
            UserItem[] items = Items;
            bool any = items[1] != null || items[2] != null;
            Up.SetText(_matLabel, any ? W.T(ClientTextKeys.ModernUpGradeOf, Up.GradeName(info.Grade))
                : _session.Selected == 0 && b.Awake.GetAwakeLevel() == 0 ? W.T(ClientTextKeys.ModernUpPickType) : W.T(ClientTextKeys.ModernUpAskingServer));
            bool haveAll = any;
            for (int i = 0; i < 2; i++)
            {
                UserItem need = items[1 + i];
                Up.Show(_mats[i], need != null);
                if (need?.Info == null) continue;
                var st = Stacks(need.Info);
                bool ok = st.Total >= need.Count;
                if (!ok) haveAll = false;
                string text = Up.Name(need.Info), count = W.N(st.Total) + " / " + W.N(need.Count);
                if (text != _matText[i] || count != _matCount[i] || ok != _matOk[i])
                {
                    _matText[i] = text;
                    _matCount[i] = count;
                    _matOk[i] = ok;
                    _mats[i].Invalidate();
                }
            }

            // odds (S.ServerRules; hidden until it arrives)
            if (rules != null)
            {
                int success = Math.Min(100, rules.AwakeSuccessRate + 1); // Random(0, 100) <= rate
                int g = (int)info.Grade - 1;
                int maxV = Math.Max(1, rules.AwakeChanceMax != null && g >= 0 && g < rules.AwakeChanceMax.Length ? rules.AwakeChanceMax[g] : 1);
                int rate = info.Type == ItemType.Weapon ? rules.Awake_WeaponRate : info.Type == ItemType.Helmet ? rules.Awake_HelmetRate : rules.Awake_ArmorRate;
                Up.SetText(_oddsSuccess, success + "%");
                Up.SetText(_oddsHit, Math.Min(100, (int)rules.AwakeHitRate) + "% · +" + (maxV / 5d).ToString("0.0"));
                Up.SetText(_oddsGain, "1–" + maxV + " ×" + rate);
                _dangerText = W.T(ClientTextKeys.ModernUpAwakeRisk, 100 - success, Up.Name(info));
            }
            else
            {
                Up.SetText(_oddsSuccess, "—");
                Up.SetText(_oddsHit, "—");
                Up.SetText(_oddsGain, "—");
                _dangerText = W.T(ClientTextKeys.ModernUpAwakeRiskUnknown, Up.Name(info));
            }
            _danger.Invalidate();
            uint? price = _session.Price;
            Up.SetText(_price, price == null ? "—" : W.T(ClientTextKeys.ModernUpGoldN, W.N(price.Value)));
            _price.Colour = price != null && GameScene.Gold < price.Value ? ModernTheme.BadSoft : ModernTheme.Gold;

            bool typeChosen = _session.Selected != 0 || b.Awake.GetAwakeLevel() > 0;
            string reason = string.Empty;
            if (_session.Busy) reason = W.T(ClientTextKeys.ModernUpAwakening);
            else if (!typeChosen) reason = W.T(ClientTextKeys.ModernUpPickStat);
            else if (!any) reason = W.T(ClientTextKeys.ModernUpWaitMaterials);
            else if (!haveAll) reason = W.T(ClientTextKeys.ModernUpNotEnoughMaterials);
            else if (price != null && GameScene.Gold < price.Value) reason = W.T(ClientTextKeys.LowGold);
            else if (!Fillable()) reason = W.T(ClientTextKeys.ModernUpOneStack);
            Up.SetText(_why, reason);
            _why.Colour = _session.Busy ? ModernTheme.Ok : ModernTheme.BadSoft;
            _go.SetDisabled(reason.Length > 0, reason);
            _go.Text = W.T(_session.Busy ? ClientTextKeys.ModernUpAwakeningBtn : ClientTextKeys.ModernUpHoldToAwaken);
        }

        /// <summary>Whether each needed material has one stack big enough (CheckNeedMaterials checks single cells).</summary>
        private static bool Fillable()
        {
            for (int i = 1; i < 3; i++)
            {
                UserItem need = Items[i];
                if (need?.Info != null && Stacks(need.Info).BestCount < need.Count) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ painting

        private void PaintStageBg(Graphics g, PaintBox b)
        {
            using (var bg = new SolidBrush(W.Page)) g.FillRectangle(bg, 0, 0, b.Size.Width, b.Size.Height);
            float d = b.Size.Width * 1.1f;
            using var path = new GraphicsPath();
            path.AddEllipse(b.Size.Width / 2f - d / 2f, b.Size.Height * 0.45f - d / 2f, d, d);
            using var brush = new PathGradientBrush(path) { CenterColor = W.Over(ModernTheme.Accent, 0.16f), SurroundColors = new[] { W.Page } };
            g.FillPath(brush, path);
        }

        /// <summary>The rune circle (rings, two triangles, the level pips around the base cell).</summary>
        private void PaintCircle(Graphics g, PaintBox b)
        {
            float k = b.Size.Width / 250f;
            float c = b.Size.Width / 2f;
            Color ac = ModernTheme.Accent;
            void Ring(float r, float width, float alpha, float[] dash = null)
            {
                using var pen = new Pen(W.Over(ac, alpha), Math.Max(1f, width * k));
                if (dash != null) pen.DashPattern = dash;
                g.DrawEllipse(pen, c - r * k, c - r * k, 2 * r * k, 2 * r * k);
            }
            Ring(118, 1, 0.5f);
            Ring(108, 2, 0.8f, new[] { 2f, 5f });
            Ring(90, 1.5f, 0.6f, new[] { 13f, 4f, 1.5f, 4f });
            Ring(62, 1, 0.4f);
            using (var pen = new Pen(W.Over(ac, 0.35f), Math.Max(1f, k)))
            {
                g.DrawPolygon(pen, new[] { new PointF(125 * k, 12 * k), new PointF(223 * k, 182 * k), new PointF(27 * k, 182 * k) });
                g.DrawPolygon(pen, new[] { new PointF(125 * k, 238 * k), new PointF(27 * k, 68 * k), new PointF(223 * k, 68 * k) });
            }
            // the base cell's glow
            using (var gp = new GraphicsPath())
            {
                gp.AddEllipse(c - 56 * k, c - 56 * k, 112 * k, 112 * k);
                using var gb = new PathGradientBrush(gp) { CenterColor = W.Over(ModernTheme.GlowColour, 0.5f), SurroundColors = new[] { W.Over(ac, 0.0f) } };
                g.FillPath(gb, gp);
            }
            for (int i = 0; i < _pips; i++)
            {
                double a = -Math.PI / 2 + i * 2 * Math.PI / _pips;
                float px = c + (float)Math.Cos(a) * 104 * k, py = c + (float)Math.Sin(a) * 104 * k, r = 11 * k;
                bool on = i < _level;
                RectangleF pr = new RectangleF(px - r, py - r, 2 * r, 2 * r);
                using (var pb = new SolidBrush(on ? ModernTheme.Gold : ModernTheme.Field)) g.FillEllipse(pb, pr);
                using (var pen = new Pen(on ? Color.White : ModernTheme.Frame, 1f)) g.DrawEllipse(pen, pr);
                ModernSkin.Text(g, (i + 1).ToString(), ModernSkin.Font(10, true), on ? Color.Black : ModernTheme.Dim, pr, StringAlignment.Center, StringAlignment.Center, false);
            }
        }

        private void PaintGearRow(Graphics g, PaintBox b, int slot)
        {
            float k = ModernRoot.K;
            UserItem it = Up.User?.Inventory != null && slot < Up.User.Inventory.Length ? Up.User.Inventory[slot] : null;
            ItemInfo info = it?.Info;
            if (info == null) return;
            UserItem sel = Items[AwakenSession.Base];
            bool selected = sel != null && sel.UniqueID == it.UniqueID;
            string why = Blocked(it);
            bool locked = ItemMoves.IsLocked(MirGridType.Inventory, slot) && !selected;
            bool no = (why.Length > 0 && why != W.T(ClientTextKeys.ModernUpAwFull)) || locked;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
            using (var bg = new SolidBrush(selected ? W.Over(ModernTheme.Accent, 0.14f) : W.Over(ModernTheme.FrameLight, 0.05f))) g.FillPath(bg, p);
            ModernSkin.Stroke(g, p, selected ? ModernTheme.Accent : b.Hovered && !no ? W.Over(ModernTheme.Accent, 0.55f) : W.Over(ModernTheme.Frame, 0.55f));
            float tx = 52 * k, tw = b.Size.Width - tx - 6 * k;
            Color nameC = Up.Grade(info.Grade);
            if (no) nameC = ModernSkin.Dim(nameC, 0.55f);
            ModernSkin.Text(g, Up.Name(info), ModernSkin.Font(12, true), nameC, new RectangleF(tx, 8 * k, tw, 16 * k));
            int level = it.Awake.GetAwakeLevel(), max = Rules?.MaxAwakeLevel ?? 0;
            string sub = level > 0 ? new string('★', level) + (max > level ? new string('☆', max - level) : string.Empty)
                : locked ? W.T(ClientTextKeys.ModernWinWaitingServer) : why.Length > 0 ? why : Up.GradeName(info.Grade) + " " + TypeLabel(info.Type);
            ModernSkin.Text(g, sub, ModernSkin.Font(10), level > 0 ? ModernTheme.Gold : ModernTheme.Dim, new RectangleF(tx, 26 * k, tw, 14 * k));
        }

        private void PaintMat(Graphics g, PaintBox b, int i)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
            using (var bg = new SolidBrush(W.Page)) g.FillPath(bg, p);
            ModernSkin.Stroke(g, p, _matOk[i] ? W.Over(ModernTheme.Frame, 0.45f) : W.Over(ModernTheme.Bad, 0.5f));
            ModernSkin.Text(g, _matText[i], ModernSkin.Font(12), ModernTheme.Text, new RectangleF(50 * k, 0, b.Size.Width - 130 * k, b.Size.Height));
            ModernSkin.Text(g, _matCount[i], ModernSkin.Font(12, true), _matOk[i] ? ModernTheme.Ok : ModernTheme.BadSoft, new RectangleF(b.Size.Width - 90 * k, 0, 82 * k, b.Size.Height), StringAlignment.Far);
        }

        private static void PaintDanger(Graphics g, PaintBox b, string text)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
            using (var bg = new SolidBrush(W.Over(ModernTheme.Bad, 0.12f))) g.FillPath(bg, p);
            ModernSkin.Stroke(g, p, ModernTheme.Bad);
            ModernSkin.Text(g, text, ModernSkin.Font(11), ModernTheme.Hex("#ffb3a6"), new RectangleF(10 * k, 6 * k, b.Size.Width - 20 * k, b.Size.Height - 12 * k), StringAlignment.Near, StringAlignment.Center, true, true);
        }

        // ------------------------------------------------------------------ actions

        /// <summary>A gear row: the item goes into the base cell (the ItemMoves "To Awakening" route; the previous base is handed back).</summary>
        private void SelectGear(int slot, ulong id)
        {
            if (_session.Busy || ItemMoves.Selected != null) return;
            UserItem item = Up.User.Inventory[slot];
            if (item == null || item.UniqueID != id || Items[AwakenSession.Base]?.UniqueID == id) return;
            string why = Blocked(item);
            if (why.Length > 0 && why != W.T(ClientTextKeys.ModernUpAwFull)) { _note = why; _dirty = true; return; }
            if (ItemMoves.IsLocked(MirGridType.Inventory, slot)) { _note = W.T(ClientTextKeys.ModernWinWaitingServer); _dirty = true; return; }
            _note = string.Empty;
            Put(slot, AwakenSession.Base);
        }

        private static void Put(int bagSlot, int cell)
        {
            ItemMoves.Selected = new ItemSlotRef(MirGridType.Inventory, bagSlot);
            ItemMoves.Click(new ItemSlotRef(MirGridType.AwakenItem, cell), new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
            if (ItemMoves.IsSelected(MirGridType.Inventory, bagSlot)) ItemMoves.Selected = null;
        }

        /// <summary>A material cell back to the bag (the "To Inventory from AwakenItem" route: C.MoveItem onto its own slot).</summary>
        private static void TakeBack(int cell)
        {
            UserItem it = Items[cell];
            if (it == null) return;
            ItemMoves.Selected = new ItemSlotRef(MirGridType.AwakenItem, cell);
            ItemMoves.Click(new ItemSlotRef(MirGridType.Inventory, AwakenSession.Idx[cell]), new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
            if (ItemMoves.IsSelected(MirGridType.AwakenItem, cell)) ItemMoves.Selected = null;
        }

        /// <summary>The hold finished: fill the material cells from the bag (largest stack of each), then C.Awakening when
        /// NPCAwakeDialog.CheckNeedMaterials passes. The window waits for S.Awakening, which closes it.</summary>
        private void Awaken()
        {
            if (_session.Busy) return;
            UserItem b = Items[AwakenSession.Base];
            if (b?.Info == null || Blocked(b).Length > 0) return;
            UserItem n1 = Items[1], n2 = Items[2];
            if (n1 == null && n2 == null) return;
            bool Needed(UserItem it)
            {
                if (it?.Info == null) return false;
                return (n1?.Info != null && n1.Info.Name == it.Info.Name && it.Count >= n1.Count) || (n2?.Info != null && n2.Info.Name == it.Info.Name && it.Count >= n2.Count);
            }
            foreach (int cell in AwakenSession.MatCells)
                if (Items[cell] != null && !Needed(Items[cell])) TakeBack(cell);
            foreach (UserItem need in new[] { n1, n2 })
            {
                if (need?.Info == null) continue;
                bool placed = false;
                foreach (int cell in AwakenSession.MatCells) placed |= Items[cell]?.Info?.Name == need.Info.Name;
                if (placed) continue;
                var st = Stacks(need.Info);
                int free = -1;
                foreach (int cell in AwakenSession.MatCells) if (free < 0 && Items[cell] == null && AwakenSession.Idx[cell] == 0) free = cell;
                if (st.Best < 0 || st.BestCount < need.Count || free < 0) { _dirty = true; return; }
                Put(st.Best, free);
            }
            if (!AwakenSession.CheckNeedMaterials()) { _dirty = true; return; }
            _session.Awaken();
            _dirty = true;
        }
    }
}

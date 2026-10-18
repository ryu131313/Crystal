// Ported from WebClient/src/ui/modern/windows/socket.ts (gemPlan + SocketWindow): the new UI's sockets & gems window. Two
// benches:
//  - Gems & orbs: PlayerObject.CombineItem for gems (ItemType.Gem Shape 3), orbs (Shape 4) and socket stones (Shape 7) on an
//    item in your BAG (C.CombineItem Grid Inventory through the pacer; both cells locked until S.CombineItem, as the classic
//    Ctrl + drop combine). The rules shown are the server's, read from the gem's ItemInfo (ValidGemForItem by the gem's Unique
//    flags; Stats: CriticalRate = base chance, Reflect = minus per stat point / gem already added, CriticalDamage = most gems
//    per item, HPDrainRatePercent = most of that stat). The result comes from the server: S.ItemUpgraded (ModernUi.ItemUpgraded:
//    the stat took), a new socket (Slots grew), the item gone (destroyed), the gem used without either (no effect).
//  - Sockets: SocketDialog (MirScenes/Dialogs/SocketDialog.cs) for the item opened with Ctrl + right-click (ItemMoves.OpenItem →
//    ModernUi.ItemShowSockets: GameScene.SelectedItem): its Slots as ModernItemCell(MirGridType.Socket) cells (6 x 2, only as many
//    as the item has; right-click / pick to the bag removes one: C.RemoveSlotItem through ItemMoves), the socket items
//    (ItemType.Socket) in your bag (click one: ItemMoves.UseSlotItem → C.EquipSlotItem into ModernUi.ItemFreeSocketSlot), and
//    the stats the socketed items add. Closing the window clears GameScene.SelectedItem (SocketDialog.Hide).
// HIDDEN: the Sockets tab while no item with sockets is open; worn gear in the gem list (bag items only); the exact chance
// until S.ServerRules arrives (GemStatIndependent decides what the penalty multiplies); the result animations.
// No menu entry or hotkey: Ctrl + right-click opens it (ModernUi.ItemShowSockets).
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;
using S = ServerPackets;

namespace Client.MirScenes.Modern.Windows
{
    /// <summary>What applying a gem to an item would do, by the server's rules (no roll).</summary>
    internal sealed class GemPlan
    {
        public const int Gem = 3, Orb = 4, SlotStone = 7;

        public string Why = string.Empty;
        public int Shape;
        /// <summary>The stat it adds; Stat.Unknown with <see cref="Dura"/> = a durability gem; null = a socket stone / unknown.</summary>
        public Stat? StatAdded;
        public bool Dura;
        public int Gain, Gems, MaxGems, Have, MaxStat, Base, Penalty, Times, GemRate;
        /// <summary>The success chance in % (null = unknown: S.ServerRules not received).</summary>
        public int? Chance;
        public double? Destroy;

        private static readonly Stat[] CombineOrder =
        {
            Stat.MaxDC, Stat.MaxMC, Stat.MaxSC, Stat.MaxAC, Stat.MaxMAC, Stat.Unknown /* durability */, Stat.AttackSpeed, Stat.Agility, Stat.Accuracy,
            Stat.PoisonAttack, Stat.Freezing, Stat.MagicResist, Stat.PoisonResist, Stat.Luck,
        };
        private static readonly Stat[] GemTypeOrder =
        {
            Stat.MaxDC, Stat.MaxMC, Stat.MaxSC, Stat.MaxAC, Stat.MaxMAC, Stat.AttackSpeed, Stat.Agility, Stat.Accuracy, Stat.PoisonAttack,
            Stat.Freezing, Stat.MagicResist, Stat.PoisonResist, Stat.Luck, Stat.PoisonRecovery, Stat.HP, Stat.MP, Stat.HealthRecovery,
        };

        /// <summary>ValidGemForItem: the target's ItemType → the SpecialItemMode flag the gem must carry.</summary>
        private static SpecialItemMode Fit(ItemType t) => t switch
        {
            ItemType.Weapon => SpecialItemMode.Paralize,
            ItemType.Armour => SpecialItemMode.Teleport,
            ItemType.Helmet => SpecialItemMode.ClearRing,
            ItemType.Necklace => SpecialItemMode.Protection,
            ItemType.Bracelet => SpecialItemMode.Revival,
            ItemType.Ring => SpecialItemMode.Muscle,
            ItemType.Amulet => SpecialItemMode.Flame,
            ItemType.Belt => SpecialItemMode.Healing,
            ItemType.Boots => SpecialItemMode.Probe,
            ItemType.Stone => SpecialItemMode.Skill,
            _ => SpecialItemMode.None,
        };

        private static int Total(UserItem item, Stat s) => (item.Info?.Stats[s] ?? 0) + item.AddedStats[s]; // UserItem.GetTotal

        public string StatText => Dura ? W.T(ClientTextKeys.ModernUpMaxDura) : StatAdded is Stat s ? s.ToLocalizedString() : W.T(ClientTextKeys.ModernUpStat);

        /// <summary>PlayerObject.CombineItem's checks and chance for a gem / orb / socket stone on a bag item.</summary>
        public static GemPlan Make(UserItem gem, UserItem item, S.ServerRules rules, int gemRate)
        {
            ItemInfo gi = gem.Info, info = item.Info;
            var p = new GemPlan
            {
                Shape = gi.Shape, Gems = item.GemCount, GemRate = gemRate,
                Base = gi.Stats[Stat.CriticalRate], Penalty = gi.Stats[Stat.Reflect], MaxGems = gi.Stats[Stat.CriticalDamage], MaxStat = gi.Stats[Stat.HPDrainRatePercent],
            };
            if (info.Type < ItemType.Weapon || info.Type > ItemType.Stone) { p.Why = W.T(ClientTextKeys.ModernUpGemNotKind); return p; }
            if (info.Bind.HasFlag(BindMode.DontUpgrade) || info.Unique != SpecialItemMode.None) { p.Why = W.T(ClientTextKeys.ModernUpCantUpgrade); return p; }
            if (item.RentalInformation != null && item.RentalInformation.BindingFlags.HasFlag(BindMode.DontUpgrade))
            {
                p.Why = W.T(ClientTextKeys.ModernUpRentedNoUpgrade, item.RentalInformation.OwnerName);
                return p;
            }
            SpecialItemMode fit = Fit(info.Type);
            bool fits = fit != SpecialItemMode.None && (gi.Unique & fit) != 0;
            string kind = info.Type.ToLocalizedString();
            if (p.Shape == SlotStone)
            {
                if (!fits) { p.Why = W.T(ClientTextKeys.ModernUpGemDoesntFit, kind); return p; }
                p.Chance = 100; // the server also checks the item's socket limit (RandomStats.SlotMaxStat)
                p.Destroy = 0;
                return p;
            }
            for (int i = 0; i < CombineOrder.Length && p.StatAdded == null; i++)
            {
                Stat s = CombineOrder[i];
                if (s == Stat.Unknown) { if (gi.Durability > 0) { p.StatAdded = s; p.Dura = true; } }
                else if (Total(gem, s) > 0) p.StatAdded = s;
            }
            if (p.StatAdded is Stat st)
            {
                p.Have = p.Dura ? (info.Durability > item.MaxDura ? 0 : (item.MaxDura - info.Durability) / 1000) : item.AddedStats[st];
                p.Gain = p.Dura ? gem.MaxDura : Total(gem, st);
            }
            if (item.GemCount >= p.MaxGems || p.Have >= p.MaxStat) { p.Why = W.T(ClientTextKeys.ModernUpGemFull); return p; }
            if (!fits) { p.Why = W.T(ClientTextKeys.ModernUpGemDoesntFit, kind); return p; }
            if (p.StatAdded == null) { p.Why = W.T(ClientTextKeys.ModernUpCantCombine); return p; }
            if (rules == null) return p;
            // successchance = Reflect x (GemStatIndependent ? the item's added points of GetGemType : GemCount)
            int times = item.GemCount;
            if (rules.GemStatIndependent)
                for (int i = 0; i < GemTypeOrder.Length; i++)
                    if (Total(gem, GemTypeOrder[i]) > 0) { times = item.AddedStats[GemTypeOrder[i]]; break; }
            p.Times = times;
            int chance = p.Penalty * times >= p.Base ? 0 : p.Base - p.Penalty * times + gemRate;
            int c = Math.Clamp(chance, 0, 100);
            p.Chance = chance;
            p.Destroy = p.Shape == Gem ? (100 - c) * 3 / 15d : 0;
            return p;
        }
    }

    public sealed class SocketWindow : ModernWindow
    {
        public const string WindowKey = "socket";
        public const int SocketCells = 12;
        private const int WidthHud = 900, BodyH = 470, LeftW = 220, RightW = 250, MidX = LeftW + 1, MidW = WidthHud - LeftW - RightW - 2, RightX = WidthHud - RightW;

        private int _tab;
        private ulong _targetId;
        private int _gemIndex = -1; // ItemInfo.Index of the picked gem kind
        private (ulong Gem, ulong To, string GemName, string ItemName, int Slots, int GemCount, bool Upgraded, long Until) _pending;
        private string _msg = string.Empty;
        private bool _msgGood;
        private readonly List<string> _log = new List<string>();
        private string _listKey = string.Empty, _sockKey = string.Empty, _goKey = string.Empty;
        private bool _dirty = true;
        private long _nextPoll;

        private Tabs _tabs;
        private bool _tabsSockets;
        private ModernPanel _gemsView, _sockView, _gearCol, _gemCol, _goBox, _sockRight;
        private ScrollView _gearList, _gemList, _sockList;
        private ModernItemCell _gemCell, _targetCell, _sockItemCell;
        private ModernLabel _gemName, _gemSub, _targetName, _targetSub, _added, _cap, _big, _bigSmall, _calc, _warn, _msgLabel, _logLabel, _sockTitle, _sockStats;
        private OddsBar _odds;
        private PaintBox _oddsText;
        private readonly string[] _oddsParts = new string[6];
        private ModernButton _go;
        private readonly List<ModernItemCell> _sockCells = new List<ModernItemCell>();

        private readonly GridWatch _bagWatch = new GridWatch();
        private readonly Action _changed;
        private readonly Action<ItemSlotKey> _slotChanged;
        private readonly Action<UserItem> _itemInfo;

        public SocketWindow() : base(WindowKey, W.T(ClientTextKeys.ModernUpSocketTitle), 120, 40)
        {
            ContentWidth = WidthHud;
            ContentHeight = BodyH + 38;
            _changed = () => _dirty = true;
            _slotChanged = k => { if (k.Grid == MirGridType.Inventory || k.Grid == MirGridType.Socket || k.Grid == MirGridType.Equipment) _dirty = true; };
            _itemInfo = _ => { _listKey = _sockKey = string.Empty; _dirty = true; };
        }

        internal int TabShown => _tab;
        internal IReadOnlyList<ModernItemCell> SocketCellsShown => _sockCells;
        internal ModernButton GoButton => _go;
        internal string Message => _msg;

        private static UserItem SockItem => GameScene.SelectedItem;
        private static bool HasSockets => SockItem != null && SockItem.Slots != null && SockItem.Slots.Length > 0;

        protected override void Build()
        {
            const int top = TitleHeight;
            _tabsSockets = HasSockets;
            var names = new List<string> { W.T(ClientTextKeys.ModernUpGemsTab) };
            if (_tabsSockets) names.Add(W.T(ClientTextKeys.ModernUpSocketsTab));
            if (!_tabsSockets) _tab = 0;
            _tabs = new Tabs(names, _tab, WidthHud - 24) { Parent = this, Location = new Point(S(12), S(top + 6)) };
            _tabs.Changed += i => { _tab = i; _dirty = true; };
            int body = top + 38;
            W.Divider(this, 0, body, WidthHud);

            // ---- gems bench
            _gemsView = new ModernPanel { Parent = this, Location = new Point(0, S(body + 1)), Size = new Size(S(WidthHud), S(BodyH - 1)) };
            MirControl g = _gemsView;
            _gearCol = new ModernPanel { Parent = g, Size = new Size(S(LeftW), S(BodyH - 1)) };
            W.Caption(_gearCol, W.T(ClientTextKeys.ModernWinYourBag), 10, 8, 120);
            W.Label(_gearCol, W.T(ClientTextKeys.ModernUpGemsUsed), 100, 8, LeftW - 110, 14, 10, false, ModernTheme.Dim, StringAlignment.Far);
            _gearList = new ScrollView(LeftW - 8, BodyH - 36) { Parent = _gearCol, Location = new Point(S(4), S(28)), Background = W.Page };
            _gearList.Framed = false;
            W.VDivider(g, LeftW, 0, BodyH);
            int cx = MidX + MidW / 2;
            new PaintBox(PaintBenchBg) { Parent = g, Location = new Point(S(MidX), 0), Size = new Size(S(MidW), S(BodyH - 1)), NotControl = true };
            _gemCell = new ModernItemCell(MirGridType.Inventory, -1, 64) { Parent = g, Location = new Point(S(cx - 32 - 90), S(24)), ReadOnly = true };
            _targetCell = new ModernItemCell(MirGridType.Inventory, -1, 74) { Parent = g, Location = new Point(S(cx + 90 - 37), S(19)) };
            _targetCell.ReadOnly = true;
            new IconView(ModernIcons.ArrowRight, 28, ModernTheme.Accent) { Parent = g, Location = new Point(S(cx - 14), S(42)) };
            _gemName = W.Label(g, string.Empty, cx - 90 - 75, 96, 150, 16, 13, true, Color.White, StringAlignment.Center);
            _gemSub = W.Label(g, string.Empty, cx - 90 - 75, 112, 150, 14, 11, false, ModernTheme.Dim, StringAlignment.Center);
            _targetName = W.Label(g, string.Empty, cx + 90 - 80, 96, 160, 16, 13, true, Color.White, StringAlignment.Center);
            _targetSub = W.Label(g, string.Empty, cx + 90 - 80, 112, 160, 28, 11, false, ModernTheme.Dim, StringAlignment.Center, true);
            _added = W.Label(g, string.Empty, MidX + 12, 146, MidW - 24, 32, 11, false, ModernTheme.Text, StringAlignment.Center, true);
            _cap = W.Label(g, string.Empty, MidX + 12, 182, MidW - 24, 14, 11, false, ModernTheme.Dim, StringAlignment.Center);
            _big = W.Label(g, string.Empty, MidX + 12, 202, MidW - 24, 34, 30, true, Color.White, StringAlignment.Center);
            _bigSmall = W.Label(g, string.Empty, MidX + 12, 236, MidW - 24, 12, 9, true, ModernTheme.Dim, StringAlignment.Center);
            _calc = W.Label(g, string.Empty, MidX + 12, 252, MidW - 24, 14, 11, false, ModernTheme.Dim, StringAlignment.Center);
            int ow = Math.Min(360, MidW - 24), ox = MidX + (MidW - ow) / 2;
            _odds = new OddsBar(ow, 12, ModernTheme.Hex("#4fbf6a"), ModernTheme.Hex("#556066"), ModernTheme.Hex("#bb3333")) { Parent = g, Location = new Point(S(ox), S(274)) };
            _oddsText = new PaintBox(PaintOddsText) { Parent = g, Location = new Point(S(ox), S(290)), Size = new Size(S(ow), S(14)), NotControl = true };
            _goBox = new ModernPanel { Parent = g, Location = new Point(S(ox), S(312)), Size = new Size(S(ow), S(36)) };
            _goKey = string.Empty;
            _warn = W.Label(g, string.Empty, MidX + 12, 354, MidW - 24, 16, 11, false, ModernTheme.BadSoft, StringAlignment.Center);
            _msgLabel = W.Label(g, string.Empty, MidX + 12, 372, MidW - 24, 16, 11, false, ModernTheme.BadSoft, StringAlignment.Center);
            _logLabel = W.Label(g, string.Empty, ox, 394, ow, 64, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
            W.VDivider(g, RightX - 1, 0, BodyH);
            _gemCol = new ModernPanel { Parent = g, Location = new Point(S(RightX), 0), Size = new Size(S(RightW), S(BodyH - 1)) };
            W.Caption(_gemCol, W.T(ClientTextKeys.ModernUpGemsOrbs), 10, 8, 140);
            W.Label(_gemCol, W.T(ClientTextKeys.ModernUpClickToPick), 120, 8, RightW - 130, 14, 10, false, ModernTheme.Dim, StringAlignment.Far);
            _gemList = new ScrollView(RightW - 8, BodyH - 110) { Parent = _gemCol, Location = new Point(S(4), S(28)), Background = W.Page };
            _gemList.Framed = false;
            new PaintBox((gg, b) => { W.Card(gg, b.Size, null, 0f, true); }) { Parent = _gemCol, Location = new Point(S(8), S(BodyH - 78)), Size = new Size(S(RightW - 16), S(70)), NotControl = true };
            W.Label(_gemCol, W.T(ClientTextKeys.ModernUpGemNote), 16, BodyH - 74, RightW - 32, 62, 11, false, ModernTheme.Dim, StringAlignment.Near, true);

            // ---- sockets bench
            _sockView = new ModernPanel { Parent = this, Location = new Point(0, S(body + 1)), Size = new Size(S(WidthHud), S(BodyH - 1)) };
            MirControl s = _sockView;
            W.Caption(s, W.T(ClientTextKeys.ModernUpSocketedItem), 10, 8, LeftW - 20);
            _sockItemCell = new ModernItemCell(MirGridType.Inventory, -1, 64) { Parent = s, Location = new Point(S((LeftW - 64) / 2), S(32)), ReadOnly = true };
            _sockTitle = W.Label(s, string.Empty, 10, 102, LeftW - 20, 16, 13, true, Color.White, StringAlignment.Center);
            new PaintBox((gg, b) => { W.Card(gg, b.Size, null, 0f, true); }) { Parent = s, Location = new Point(S(10), S(130)), Size = new Size(S(LeftW - 20), S(110)), NotControl = true };
            W.Label(s, W.T(ClientTextKeys.ModernUpSocketNote), 18, 136, LeftW - 36, 100, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
            W.VDivider(s, LeftW, 0, BodyH);
            W.Caption(s, W.T(ClientTextKeys.ModernUpSockets), MidX + 12, 8, 160);
            int gx = MidX + (MidW - (6 * 50 - 6)) / 2;
            _sockCells.Clear();
            for (int i = 0; i < SocketCells; i++)
                _sockCells.Add(new ModernItemCell(MirGridType.Socket, i, 44) { Parent = s, Location = new Point(S(gx + i % 6 * 50), S(32 + i / 6 * 50)) });
            W.Caption(s, W.T(ClientTextKeys.ModernUpFromSockets), MidX + 12, 142, 160);
            _sockStats = W.Label(s, string.Empty, MidX + 12, 162, MidW - 24, 80, 11, false, ModernTheme.Text, StringAlignment.Near, true);
            W.VDivider(s, RightX - 1, 0, BodyH);
            _sockRight = new ModernPanel { Parent = s, Location = new Point(S(RightX), 0), Size = new Size(S(RightW), S(BodyH - 1)) };
            W.Caption(_sockRight, W.T(ClientTextKeys.ModernUpSocketItems), 10, 8, 140);
            W.Label(_sockRight, W.T(ClientTextKeys.ModernUpInYourBag), 120, 8, RightW - 130, 14, 10, false, ModernTheme.Dim, StringAlignment.Far);
            _sockList = new ScrollView(RightW - 8, BodyH - 36) { Parent = _sockRight, Location = new Point(S(4), S(28)), Background = W.Page };
            _sockList.Framed = false;

            _listKey = _sockKey = string.Empty;
            _bagWatch.Reset();
            _dirty = true;
            Render();
        }

        // ------------------------------------------------------------------ open / close

        /// <summary>Ctrl + right-click opened an item's sockets (ModernUi.ItemShowSockets): the Sockets bench, and the item selected
        /// on the gem bench when it is in the bag.</summary>
        public void ShowSockets()
        {
            UserItem item = SockItem;
            if (item != null && Up.BagSlot(item.UniqueID) >= 0) _targetId = item.UniqueID;
            _tab = HasSockets ? 1 : 0;
            _sockKey = string.Empty;
            _dirty = true;
        }

        /// <summary>The gem bench, with a bag item selected.</summary>
        public void ShowGems(UserItem item)
        {
            if (item != null) _targetId = item.UniqueID;
            _tab = 0;
            _dirty = true;
        }

        protected override void Shown()
        {
            ItemMoves.SlotChanged.On(_slotChanged);
            ModernEvents.ItemInfo.On(_itemInfo);
            ModernEvents.InventoryResized.On(_changed);
            GameScene.Models?.ServerRules.Changed.On(_changed);
            _dirty = true;
        }

        /// <summary>SocketDialog.Hide: GameScene.SelectedItem = null (the socket cells go with it).</summary>
        protected override void Closed()
        {
            ItemMoves.SlotChanged.Off(_slotChanged);
            ModernEvents.ItemInfo.Off(_itemInfo);
            ModernEvents.InventoryResized.Off(_changed);
            GameScene.Models?.ServerRules.Changed.Off(_changed);
            ItemSlotRef pick = ItemMoves.Selected;
            if (pick != null && pick.GridType == MirGridType.Socket) ItemMoves.Selected = null;
            GameScene.SelectedItem = null;
            ItemMoves.GridChanged(MirGridType.Socket);
            _msg = string.Empty;
        }

        /// <summary>SocketDialog.Grid visibility: the first free socket the item has (ItemFreeSocketSlot), -1 when none / closed.</summary>
        public int FreeSocket()
        {
            UserItem item = SockItem;
            if (!IsOpen || item?.Slots == null) return -1;
            int n = Math.Min(item.Slots.Length, SocketCells);
            for (int i = 0; i < n; i++) if (item.Slots[i] == null) return i;
            return -1;
        }

        /// <summary>S.ItemUpgraded for the pending combine's target.</summary>
        public void Upgraded(UserItem item)
        {
            if (_pending.To != 0 && item != null && item.UniqueID == _pending.To) _pending.Upgraded = true;
            _dirty = true;
        }

        public override void Process()
        {
            base.Process();
            if (Up.User == null) return;
            if (HasSockets != _tabsSockets) { Layout(); return; }
            if (CMain.Time >= _nextPoll)
            {
                _nextPoll = CMain.Time + GridWatch.IntervalMs;
                if (_bagWatch.Poll(MirGridType.Inventory, Up.User.Inventory, 0, int.MaxValue, true)) _dirty = true;
                if (_pending.To != 0) Answer();
            }
            if (_dirty) Render();
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            _dirty = false;
            if (_tabs == null || _tabs.IsDisposed || Up.User == null) return;
            if (!HasSockets && _tab == 1) _tab = 0;
            if (_tabs.Active != _tab) _tabs.Select(_tab);
            Up.Show(_gemsView, _tab == 0);
            Up.Show(_sockView, _tab == 1);
            if (_tab == 0) RenderGems();
            else RenderSockets(SockItem);
        }

        /// <summary>The bag's upgradable items and its gems (ItemType.Gem shapes 3 / 4 / 7, grouped by ItemInfo).</summary>
        private static void Scan(List<int> gear, Dictionary<int, (int Index, int Count)> gems)
        {
            UserItem[] inv = Up.User.Inventory;
            for (int i = 0; i < inv.Length; i++)
            {
                UserItem it = inv[i];
                ItemInfo info = it?.Info;
                if (info == null) continue;
                if (info.Type >= ItemType.Weapon && info.Type <= ItemType.Stone) gear.Add(i);
                else if (info.Type == ItemType.Gem && (info.Shape == GemPlan.Gem || info.Shape == GemPlan.Orb || info.Shape == GemPlan.SlotStone))
                {
                    if (!gems.TryGetValue(info.Index, out var g)) gems[info.Index] = (i, it.Count);
                    else
                    {
                        int idx = g.Index;
                        if (ItemMoves.IsLocked(MirGridType.Inventory, idx) && !ItemMoves.IsLocked(MirGridType.Inventory, i)) idx = i;
                        gems[info.Index] = (idx, g.Count + it.Count);
                    }
                }
            }
        }

        private int GemRate => Up.User?.Stats != null ? Up.User.Stats[Stat.GemRatePercent] : 0;

        private void RenderGems()
        {
            UserItem[] inv = Up.User.Inventory;
            var gear = new List<int>();
            var gems = new Dictionary<int, (int Index, int Count)>();
            Scan(gear, gems);
            int tIndex = _targetId == 0 ? -1 : Up.BagSlot(_targetId);
            if (tIndex < 0 || !gear.Contains(tIndex)) { tIndex = gear.Count > 0 ? gear[0] : -1; _targetId = tIndex >= 0 ? inv[tIndex].UniqueID : 0; }
            if (_gemIndex >= 0 && !gems.ContainsKey(_gemIndex)) _gemIndex = -1;
            UserItem target = tIndex >= 0 ? inv[tIndex] : null;
            bool hasGem = _gemIndex >= 0 && gems.TryGetValue(_gemIndex, out _);
            int gemSlot = hasGem ? gems[_gemIndex].Index : -1;
            UserItem gem = gemSlot >= 0 ? inv[gemSlot] : null;
            S.ServerRules rules = GameScene.Models?.ServerRules.Rules;
            GemPlan plan = gem?.Info != null && target?.Info != null ? GemPlan.Make(gem, target, rules, GemRate) : null;

            RenderLists(gear, gems, tIndex, target, rules);

            _gemCell.SetSlot(MirGridType.Inventory, gemSlot);
            _targetCell.SetSlot(MirGridType.Inventory, tIndex);
            _gemCell.Refresh(false); // GameScene.CombineItem changes counts without ItemMoves.SlotChanged
            _targetCell.Refresh(false);
            Up.SetText(_gemName, gem != null ? Up.Name(gem) : W.T(ClientTextKeys.ModernUpPickGem));
            Up.SetText(_gemSub, gem?.Info != null ? W.T(gem.Info.Shape == GemPlan.Orb ? ClientTextKeys.ModernUpOrb : gem.Info.Shape == GemPlan.SlotStone ? ClientTextKeys.ModernUpSocketStone : ClientTextKeys.ModernUpGem) : W.T(ClientTextKeys.ModernUpFromTheRight));
            Up.SetText(_targetName, target != null ? Up.Name(target) : W.T(ClientTextKeys.ModernUpNoUpgradable));
            _targetName.Colour = target?.Info != null ? Up.Grade(target.Info.Grade) : Color.White;
            Up.SetText(_targetSub, target?.Info != null ? W.T(ClientTextKeys.ModernUpTargetSub, target.Info.Type.ToLocalizedString(), target.GemCount, target.Slots?.Length ?? 0) : W.T(ClientTextKeys.ModernUpPutUpgradable));

            // the item's added stats
            string added = string.Empty;
            if (target != null)
            {
                foreach (var kv in target.AddedStats.Values)
                    if (kv.Value != 0) added += (added.Length > 0 ? "   " : string.Empty) + kv.Key.ToLocalizedString() + " +" + W.N(kv.Value);
                if (added.Length == 0) added = W.T(ClientTextKeys.ModernUpNoAddedStats);
            }
            Up.SetText(_added, added);

            // caps, chance, odds
            bool showOdds = plan != null && plan.Why.Length == 0 && plan.Chance != null;
            bool stone = plan != null && plan.Shape == GemPlan.SlotStone;
            Up.Show(_cap, plan != null && !stone);
            if (plan != null && !stone)
                Up.SetText(_cap, W.T(ClientTextKeys.ModernUpGemCaps, plan.Gems + "/" + plan.MaxGems, plan.StatText, plan.Have + "/" + plan.MaxStat));
            bool big = plan != null && plan.Why.Length == 0;
            Up.Show(_big, big);
            Up.Show(_bigSmall, big);
            Up.Show(_calc, big);
            if (big)
            {
                if (stone)
                {
                    Up.SetText(_big, "+1");
                    Up.SetText(_bigSmall, W.T(ClientTextKeys.ModernUpSocketCaps));
                    Up.SetText(_calc, W.T(ClientTextKeys.ModernUpStoneCalc));
                }
                else if (plan.Chance == null)
                {
                    Up.SetText(_big, "?");
                    Up.SetText(_bigSmall, W.T(ClientTextKeys.ModernUpSuccessChance));
                    Up.SetText(_calc, W.T(ClientTextKeys.ModernUpGemRulesMissing));
                }
                else
                {
                    Up.SetText(_big, Math.Max(0, plan.Chance.Value) + "%");
                    Up.SetText(_bigSmall, W.T(ClientTextKeys.ModernUpSuccessChance));
                    string per = rules != null && rules.GemStatIndependent && !plan.Dura ? W.T(ClientTextKeys.ModernUpPerPoints, plan.StatText) : W.T(ClientTextKeys.ModernUpPerGems);
                    string calc = W.T(ClientTextKeys.ModernUpGemCalc, plan.Base, plan.Penalty, plan.Times, per);
                    if (plan.GemRate != 0) calc += " " + W.T(ClientTextKeys.ModernUpGemRateN, plan.GemRate);
                    Up.SetText(_calc, calc);
                }
            }
            bool odds = showOdds && !stone;
            Up.Show(_odds, odds);
            Up.Show(_oddsText, odds);
            if (odds)
            {
                double w = Math.Clamp(plan.Chance.Value, 0, 100), d = plan.Destroy ?? 0, n = Math.Max(0, 100 - w - d);
                _odds.Set((float)(w / 100), (float)(n / 100), (float)(d / 100));
                SetOdds(w.ToString("0") + "%", " +" + W.N(plan.Gain) + " " + plan.StatText, n.ToString("0") + "%", " " + W.T(ClientTextKeys.ModernUpNoEffect),
                    d.ToString("0") + "%", " " + W.T(ClientTextKeys.ModernUpDestroyed));
            }

            // apply: hold for a gem (it can destroy the item), a click + question for orbs / socket stones (YouWantCombineItems)
            bool risky = gem?.Info != null && gem.Info.Shape == GemPlan.Gem;
            string key = risky ? "hold" : "click";
            if (key != _goKey || _go == null || _go.IsDisposed)
            {
                _goKey = key;
                _go?.Dispose();
                if (risky)
                {
                    var hb = new HoldButton(W.T(ClientTextKeys.ModernUpHoldToApply), true) { Parent = _goBox, HoldMs = 1100 };
                    hb.Done += Apply;
                    _go = hb;
                }
                else
                {
                    _go = new ModernButton(W.T(ClientTextKeys.ModernUpApply), ButtonKind.Main) { Parent = _goBox };
                    _go.Click += (o, e) => ConfirmApply();
                }
                _go.Size = _goBox.Size;
            }
            string why = string.Empty;
            if (_pending.To != 0) why = W.T(ClientTextKeys.ModernWinWaitingServer);
            else if (target == null) why = W.T(ClientTextKeys.ModernUpPutUpgradable);
            else if (gem == null) why = W.T(ClientTextKeys.ModernUpPickGem);
            else if (plan != null && plan.Why.Length > 0) why = plan.Why;
            else if (ItemMoves.IsLocked(MirGridType.Inventory, gemSlot) || ItemMoves.IsLocked(MirGridType.Inventory, tIndex)) why = W.T(ClientTextKeys.ModernWinWaitingServer);
            _go.SetDisabled(why.Length > 0, why);
            if (risky) _go.Text = plan != null && plan.Why.Length == 0 && plan.Destroy != null ? W.T(ClientTextKeys.ModernUpHoldToApplyRisk, plan.Destroy.Value.ToString("0")) : W.T(ClientTextKeys.ModernUpHoldToApply);
            Up.SetText(_warn, plan != null ? plan.Why : gem != null || target == null ? string.Empty : W.T(ClientTextKeys.ModernUpPickFitting));
            Up.SetText(_msgLabel, _msg);
            _msgLabel.Colour = _msgGood ? ModernTheme.Ok : ModernTheme.BadSoft;
            string log = string.Empty;
            for (int i = 0; i < _log.Count && i < 4; i++) log += (i > 0 ? "\n" : string.Empty) + _log[i];
            Up.SetText(_logLabel, log);
        }

        /// <summary>The gear and gem lists (rebuilt only when what they show changed).</summary>
        private void RenderLists(List<int> gear, Dictionary<int, (int Index, int Count)> gems, int tIndex, UserItem target, S.ServerRules rules)
        {
            UserItem[] inv = Up.User.Inventory;
            string key = string.Empty;
            for (int i = 0; i < gear.Count; i++)
            {
                UserItem it = inv[gear[i]];
                key += gear[i] + ":" + it.UniqueID + ":" + it.GemCount + ":" + it.MaxDura + ":" + it.AddedStats.Count + ",";
            }
            key += "|";
            foreach (var kv in gems) key += kv.Key + ":" + kv.Value.Index + ":" + kv.Value.Count + ",";
            key += "|" + tIndex + "|" + _gemIndex + "|" + (rules != null);
            if (key == _listKey) return;
            _listKey = key;

            Clear(_gearList);
            int y = 0, w = LeftW - 20;
            for (int i = 0; i < gear.Count; i++)
            {
                int slot = gear[i];
                UserItem it = inv[slot];
                bool cant = it.Info.Bind.HasFlag(BindMode.DontUpgrade) || it.Info.Unique != SpecialItemMode.None;
                ulong id = it.UniqueID;
                string name = Up.Name(it), sub = it.Info.Type.ToLocalizedString(), right = "◆" + it.GemCount;
                Color nc = Up.Grade(it.Info.Grade);
                var row = Row(_gearList, slot, y, w, slot == tIndex, cant, () => (name, sub, right, nc));
                if (cant) row.Hint = W.T(ClientTextKeys.ModernUpCantUpgrade);
                row.Click += (o, e) => { if (_pending.To != 0) return; _targetId = id; _msg = string.Empty; _dirty = true; };
                y += 46;
            }
            if (gear.Count == 0) y = EmptyNote(_gearList, W.T(ClientTextKeys.ModernUpNoGearGems), w);
            _gearList.ContentHeight = S(y);

            Clear(_gemList);
            y = 0;
            w = RightW - 20;
            var sorted = new List<int>(gems.Keys);
            sorted.Sort((a, b) =>
            {
                ItemInfo ia = inv[gems[a].Index].Info, ib = inv[gems[b].Index].Info;
                int c = ia.Shape.CompareTo(ib.Shape);
                return c != 0 ? c : string.CompareOrdinal(ia.Name, ib.Name);
            });
            for (int i = 0; i < sorted.Count; i++)
            {
                int index = sorted[i];
                var gg = gems[index];
                UserItem gem = inv[gg.Index];
                GemPlan p = target?.Info != null ? GemPlan.Make(gem, target, rules, GemRate) : null;
                string tag = gem.Info.Shape == GemPlan.Orb ? W.T(ClientTextKeys.ModernUpOrbTag) : gem.Info.Shape == GemPlan.SlotStone ? W.T(ClientTextKeys.ModernUpSocketTag) : string.Empty;
                string name = Up.Name(gem), sub = p == null ? string.Empty : p.Why.Length > 0 ? p.Why : W.T(ClientTextKeys.ModernUpFits);
                string right = (tag.Length > 0 ? tag + "  " : string.Empty) + "×" + W.N(gg.Count);
                bool no = p != null && p.Why.Length > 0;
                var row = Row(_gemList, gg.Index, y, w, index == _gemIndex, no, () => (name, sub, right, Color.White));
                if (no) row.Hint = p.Why;
                row.Click += (o, e) => { if (_pending.To != 0) return; _gemIndex = index; _msg = string.Empty; _dirty = true; };
                y += 46;
            }
            if (sorted.Count == 0) y = EmptyNote(_gemList, W.T(ClientTextKeys.ModernUpNoGems), w);
            _gemList.ContentHeight = S(y);
        }

        private static void Clear(ScrollView list)
        {
            for (int i = list.Content.Controls.Count - 1; i >= 0; i--) list.Content.Controls[i].Dispose();
        }

        private static int EmptyNote(ScrollView list, string text, int w)
        {
            W.Label(list.Content, text, 6, 6, w - 12, 48, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
            return 56;
        }

        /// <summary>A list row (the .pc rows): a read-only bag cell, name / sub, a right-hand note.</summary>
        private static PaintBox Row(ScrollView list, int bagSlot, int y, int w, bool selected, bool no, Func<(string Name, string Sub, string Right, Color NameColour)> text)
        {
            var row = new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                var t = text();
                RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
                using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
                using (var bg = new SolidBrush(selected ? W.Over(ModernTheme.Accent, 0.14f) : W.Page)) g.FillPath(bg, p);
                ModernSkin.Stroke(g, p, selected ? ModernTheme.Accent : b.Hovered ? W.Over(ModernTheme.Accent, 0.55f) : W.Over(ModernTheme.Frame, 0.5f));
                float rw = ModernSkin.Measure(t.Right, ModernSkin.Font(12, true)) + 6 * k;
                float tx = 48 * k, tw = b.Size.Width - tx - rw - 6 * k;
                Color nc = no ? ModernSkin.Dim(t.NameColour, 0.6f) : t.NameColour;
                ModernSkin.Text(g, t.Name, ModernSkin.Font(12, true), nc, new RectangleF(tx, 5 * k, tw, 16 * k));
                ModernSkin.Text(g, t.Sub, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(tx, 22 * k, tw, 14 * k));
                ModernSkin.Text(g, t.Right, ModernSkin.Font(12, true), no ? ModernTheme.Dim : ModernTheme.Text, new RectangleF(b.Size.Width - rw - 4 * k, 0, rw, b.Size.Height), StringAlignment.Far);
            })
            { Parent = list.Content, Location = new Point(0, W.S(y)), Size = new Size(W.S(w), W.S(42)) };
            new ModernItemCell(MirGridType.Inventory, bagSlot, 36) { Parent = row, Location = new Point(W.S(4), W.S(3)), ReadOnly = true, NotControl = true };
            return row;
        }

        private void RenderSockets(UserItem item)
        {
            if (item == null) return;
            int n = Math.Min(item.Slots.Length, SocketCells);
            int bag = Up.BagSlot(item.UniqueID);
            int eq = -1;
            if (bag < 0 && Up.User.Equipment != null)
                for (int i = 0; i < Up.User.Equipment.Length; i++) if (Up.User.Equipment[i]?.UniqueID == item.UniqueID) eq = i;
            if (bag >= 0) _sockItemCell.SetSlot(MirGridType.Inventory, bag);
            else _sockItemCell.SetSlot(MirGridType.Equipment, eq);
            _sockItemCell.Refresh(false);
            Up.SetText(_sockTitle, Up.Name(item));
            _sockTitle.Colour = item.Info != null ? Up.Grade(item.Info.Grade) : Color.White;
            for (int i = 0; i < _sockCells.Count; i++)
            {
                Up.Show(_sockCells[i], i < n);
                _sockCells[i].Refresh(false);
            }

            // stats from the socketed items
            var sum = new Dictionary<Stat, int>();
            for (int i = 0; i < item.Slots.Length; i++)
            {
                UserItem s = item.Slots[i];
                if (s?.Info == null) continue;
                foreach (var kv in s.Info.Stats.Values) if (kv.Value != 0) sum[kv.Key] = (sum.TryGetValue(kv.Key, out int v) ? v : 0) + kv.Value;
                foreach (var kv in s.AddedStats.Values) if (kv.Value != 0) sum[kv.Key] = (sum.TryGetValue(kv.Key, out int v) ? v : 0) + kv.Value;
            }
            string stats = string.Empty;
            foreach (var kv in sum) stats += (stats.Length > 0 ? "   " : string.Empty) + kv.Key.ToLocalizedString() + " +" + W.N(kv.Value);
            Up.SetText(_sockStats, stats.Length > 0 ? stats : W.T(ClientTextKeys.ModernUpEmptySockets));

            // the socket items in your bag (shape 1 weapons, 2 armour, 3 jewellery: ItemMoves.UseSlotItem)
            UserItem[] inv = Up.User.Inventory;
            string key = item.UniqueID + "|";
            for (int i = 0; i < inv.Length; i++) if (inv[i]?.Info != null && inv[i].Info.Type == ItemType.Socket) key += i + ":" + inv[i].UniqueID + ",";
            if (key == _sockKey) return;
            _sockKey = key;
            Clear(_sockList);
            int y = 0, w = RightW - 20, count = 0;
            for (int i = 0; i < inv.Length; i++)
            {
                UserItem it = inv[i];
                if (it?.Info == null || it.Info.Type != ItemType.Socket) continue;
                count++;
                int slot = i;
                string why = SocketFit(it.Info, item.Info);
                string name = Up.Name(it), sub = why.Length > 0 ? why : W.T(ClientTextKeys.ModernUpClickToSocket);
                var row = Row(_sockList, slot, y, w, false, why.Length > 0, () => (name, sub, string.Empty, Color.White));
                row.Click += (o, e) =>
                {
                    if (why.Length > 0) { _msg = why; _msgGood = false; _dirty = true; return; }
                    if (ItemMoves.Selected != null || ItemMoves.IsLocked(MirGridType.Inventory, slot)) return;
                    ItemMoves.UseSlotItem(new ItemSlotRef(MirGridType.Inventory, slot));
                };
                y += 46;
            }
            if (count == 0) y = EmptyNote(_sockList, W.T(ClientTextKeys.ModernUpNoSocketItems), w);
            _sockList.ContentHeight = S(y);
        }

        /// <summary>ItemMoves.UseSlotItem's shape check for a socket item: "" = it fits the open item.</summary>
        private static string SocketFit(ItemInfo socket, ItemInfo target)
        {
            if (target == null) return string.Empty;
            if (socket.Shape == 1 && target.Type != ItemType.Weapon) return W.T(ClientTextKeys.ModernUpWeaponsOnly);
            if (socket.Shape == 2 && target.Type != ItemType.Armour) return W.T(ClientTextKeys.ModernUpArmourOnly);
            if (socket.Shape == 3 && target.Type != ItemType.Ring && target.Type != ItemType.Bracelet && target.Type != ItemType.Necklace) return W.T(ClientTextKeys.ModernUpJewelleryOnly);
            return string.Empty;
        }

        private void SetOdds(params string[] parts)
        {
            bool same = true;
            for (int i = 0; i < 6; i++) if (_oddsParts[i] != parts[i]) { same = false; _oddsParts[i] = parts[i]; }
            if (!same) _oddsText.Invalidate();
        }

        private void PaintOddsText(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K, w = b.Size.Width / 3f;
            for (int i = 0; i < 3; i++)
            {
                string num = _oddsParts[i * 2] ?? string.Empty, rest = _oddsParts[i * 2 + 1] ?? string.Empty;
                Font bold = ModernSkin.Font(10, true), plain = ModernSkin.Font(10);
                float nw = ModernSkin.Measure(num, bold), total = nw + ModernSkin.Measure(rest, plain);
                float x = i == 0 ? 0 : i == 1 ? w + (w - total) / 2f : b.Size.Width - total;
                ModernSkin.Text(g, num, bold, Color.White, new RectangleF(x, 0, nw + 4 * k, b.Size.Height), StringAlignment.Near, StringAlignment.Center, false);
                ModernSkin.Text(g, rest, plain, ModernTheme.Dim, new RectangleF(x + nw, 0, w + 20 * k, b.Size.Height), StringAlignment.Near, StringAlignment.Center, false);
            }
        }

        private void PaintBenchBg(Graphics g, PaintBox b)
        {
            using (var bg = new SolidBrush(W.Page)) g.FillRectangle(bg, 0, 0, b.Size.Width, b.Size.Height);
            float d = b.Size.Width * 1.1f;
            using var path = new GraphicsPath();
            path.AddEllipse(b.Size.Width / 2f - d / 2f, b.Size.Height * 0.42f - d / 2f, d, d);
            using var brush = new PathGradientBrush(path) { CenterColor = W.Over(ModernTheme.Accent, 0.14f), SurroundColors = new[] { W.Page } };
            g.FillPath(brush, path);
        }

        // ------------------------------------------------------------------ actions

        /// <summary>Orbs / socket stones: the desktop's question (YouWantCombineItems), then apply.</summary>
        private void ConfirmApply()
        {
            Up.Pop?.Ask(W.T(ClientTextKeys.YouWantCombineItems), ok => { if (ok && IsOpen) Apply(); });
        }

        /// <summary>C.CombineItem (Grid Inventory) through the pacer; both cells locked until S.CombineItem.</summary>
        private void Apply()
        {
            if (_pending.To != 0 || Up.User == null) return;
            var gear = new List<int>();
            var gems = new Dictionary<int, (int Index, int Count)>();
            Scan(gear, gems);
            int from = _gemIndex >= 0 && gems.TryGetValue(_gemIndex, out var g) ? g.Index : -1;
            int to = _targetId == 0 ? -1 : Up.BagSlot(_targetId);
            UserItem gem = from >= 0 ? Up.User.Inventory[from] : null, target = to >= 0 ? Up.User.Inventory[to] : null;
            if (gem?.Info == null || target?.Info == null) return;
            if (ItemMoves.IsLocked(MirGridType.Inventory, from) || ItemMoves.IsLocked(MirGridType.Inventory, to)) return;
            GemPlan plan = GemPlan.Make(gem, target, GameScene.Models?.ServerRules.Rules, GemRate);
            if (plan.Why.Length > 0) { _msg = plan.Why; _msgGood = false; _dirty = true; return; }
            ModernUi ui = Up.Ui;
            if (ui == null) return;
            ui.Pacer.Send(new C.CombineItem { Grid = MirGridType.Inventory, IDFrom = gem.UniqueID, IDTo = target.UniqueID });
            ItemMoves.SetLocked(MirGridType.Inventory, from, true);
            ItemMoves.SetLocked(MirGridType.Inventory, to, true);
            int gemCount = 0;
            foreach (var kv in gems) if (kv.Key == gem.Info.Index) gemCount = kv.Value.Count;
            _pending = (gem.UniqueID, target.UniqueID, Up.Name(gem), Up.Name(target), target.Slots?.Length ?? 0, gemCount, false, CMain.Time + 6000);
            _msg = string.Empty;
            _dirty = true;
        }

        /// <summary>The server's answer to the pending combine (GameScene.CombineItem unlocked both cells and applied it).</summary>
        private void Answer()
        {
            var pd = _pending;
            int t = Up.BagSlot(pd.To), f = Up.BagSlot(pd.Gem);
            bool waiting = (t >= 0 && ItemMoves.IsLocked(MirGridType.Inventory, t)) || (f >= 0 && ItemMoves.IsLocked(MirGridType.Inventory, f));
            if (waiting && CMain.Time < pd.Until) return;
            _pending = default;
            int gemsLeft = 0;
            UserItem[] inv = Up.User.Inventory;
            UserItem target = t >= 0 ? inv[t] : null;
            for (int i = 0; i < inv.Length; i++)
                if (inv[i]?.Info != null && inv[i].Info.Index == _gemIndex) gemsLeft += inv[i].Count;
            string text;
            bool good = false;
            if (waiting) text = W.T(ClientTextKeys.ModernUpNoAnswer);
            else if (target == null) { text = W.T(ClientTextKeys.ModernUpGemBroke, pd.GemName, pd.ItemName); _targetId = 0; }
            else if ((target.Slots?.Length ?? 0) > pd.Slots) { text = W.T(ClientTextKeys.ModernUpGainedSocket, pd.ItemName); good = true; }
            else if (pd.Upgraded) { text = W.T(ClientTextKeys.ModernUpUpgradedBy, pd.ItemName, pd.GemName); good = true; }
            else if (gemsLeft < pd.GemCount) text = W.T(ClientTextKeys.ModernUpNoEffectMsg, pd.GemName);
            else text = W.T(ClientTextKeys.ModernUpRefused);
            if (waiting)
            {
                if (t >= 0) ItemMoves.SetLocked(MirGridType.Inventory, t, false);
                if (f >= 0) ItemMoves.SetLocked(MirGridType.Inventory, f, false);
            }
            _msg = text;
            _msgGood = good;
            _log.Insert(0, text);
            if (_log.Count > 8) _log.RemoveAt(_log.Count - 1);
            _listKey = string.Empty;
            _dirty = true;
        }
    }
}

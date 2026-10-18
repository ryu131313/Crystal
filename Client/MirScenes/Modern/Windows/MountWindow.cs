// Ported from WebClient/src/ui/modern/windows/mount.ts (MountWindow): the new UI's mount window (the desktop's MountDialog is
// the classic reference). The equipped mount (EquipmentSlot.Mount) animated big on a pedestal (Prguse StartIndex + MountType x
// 20, 16 frames at 100 ms: StartIndex 1330 for 5-slot mounts, 1170 for 4-slot), its gear slots placed round it (Reins / Bells /
// Saddle / Ribbon / Mask; 4-slot mounts have no Mask), Ride / Dismount (C.Chat "@ride" when MountDialog.CanRide holds), the
// loyalty bar (CurrentDura / MaxDura) with Restore, and a side panel: the stats the fitted gear gives, the bag items that fit the
// picked slot, the food in the bag and the other mounts in the bag.
//  - opens only with a mount type (User.MountType >= 0), else the desktop's "You do not own a mount." box (MountDialog.Show /
//    ClientTextKeys.NoMount); closes on CloseMountWindow (S.MountUpdate with MountType < 0);
//  - the gear slots are ModernItemCell on the Mount grid (index = MountSlot): pick / drop / right-click through ItemMoves (the
//    cell locks until the server answers). Fitting from the gear list = ItemMoves.Use on the bag slot (UseSlotItem: C.EquipSlotItem
//    into the item type's slot), the take-off cross = ItemMoves.Use on the mount slot (RemoveItem);
//  - Restore = ItemMoves.Use of a Food item in the bag (the server raises the loyalty and answers S.ItemRepaired); "equip" on
//    another Mount in the bag = ItemMoves.Use (C.EquipItem into the Mount slot).
// Desktop changes: the frames are fitted by the union of their offset rectangles (the desktop cannot read pixels; the web trims
// by an alpha histogram); refusals said up front go to chat as hints instead of a toast; the pedestal ring does not pulse.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using Client.MirScenes.Modern.Hud;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class MountWindow : ModernWindow
    {
        public const string WindowKey = "mount";
        private const int WidthHud = 740, HeightHud = 440, ShowW = 404, ArenaW = 380, ArenaH = 260, BoxW = 260, BoxH = 190, Frames = 16, FrameMs = 100, SideX = 416, SideW = 312;

        /// <summary>[MountSlot, name, the bag ItemType that fits it, empty-slot icon].</summary>
        private static readonly (MountSlot Slot, ClientTextKeys Name, ItemType Type, int Icon)[] Slots =
        {
            (MountSlot.Reins, ClientTextKeys.ModernCompReins, ItemType.Reins, ModernIcons.Link), (MountSlot.Bells, ClientTextKeys.ModernCompBells, ItemType.Bells, ModernIcons.Spark),
            (MountSlot.Saddle, ClientTextKeys.ModernCompSaddle, ItemType.Saddle, ModernIcons.Horse), (MountSlot.Ribbon, ClientTextKeys.ModernCompRibbon, ItemType.Ribbon, ModernIcons.Tag),
            (MountSlot.Mask, ClientTextKeys.ModernCompMask, ItemType.Mask, ModernIcons.Eye),
        };
        /// <summary>Gear slot positions round the arena (HUD px in the arena).</summary>
        private static readonly Point[] SlotPos = { new Point(12, 14), new Point(322, 14), new Point(12, 132), new Point(322, 132), new Point(167, 196) };

        private static string T(ClientTextKeys key) => W.T(key);
        private static string T(ClientTextKeys key, params object[] args) => W.T(key, args);

        private PaintBox _arena;
        private readonly ModernItemCell[] _cells = new ModernItemCell[5];
        private readonly ModernPanel[] _slotBoxes = new ModernPanel[5];
        private readonly ModernLabel[] _slotLabels = new ModernLabel[5];
        private readonly PaintBox[] _takeOff = new PaintBox[5];
        private ModernLabel _name, _sub, _loyText;
        private CompMeter _loyalty;
        private ModernButton _ride, _restore;
        private ScrollView _side;
        private MountSlot _slot = MountSlot.Reins;
        private long _sideSig = long.MinValue, _sig = long.MinValue, _nextPoll, _nextFrame;
        private int _start = -1, _frame;
        private RectangleF _bounds;
        private readonly GridWatch _bagWatch = new GridWatch(), _gearWatch = new GridWatch();
        private readonly Action _mountChanged;

        public MountWindow() : base(WindowKey, W.T(ClientTextKeys.ModernHudWinMount), 60, 60)
        {
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
            _mountChanged = () => { _sig = _sideSig = long.MinValue; _nextPoll = 0; };
        }

        internal ModernButton RideButton => _ride;
        internal ModernButton RestoreButton => _restore;
        internal ScrollView Side => _side;
        internal PaintBox TakeOff(int slot) => slot >= 0 && slot < 5 ? _takeOff[slot] : null;
        internal MountSlot PickedSlot => _slot;

        private static UserObject User => GameScene.User;
        private static UserItem MountItem => User?.Equipment != null && (int)EquipmentSlot.Mount < User.Equipment.Length ? User.Equipment[(int)EquipmentSlot.Mount] : null;

        /// <summary>MountDialog.CanRide.</summary>
        public static bool CanRide()
        {
            UserObject u = User;
            if (u == null || u.MountType < 0 || u.MountTime + 500 > CMain.Time) return false;
            return u.CurrentAction == MirAction.Standing || u.CurrentAction == MirAction.MountStanding;
        }

        /// <summary>MountDialog.Ride (the M key does the same).</summary>
        public static void Ride() => Network.Enqueue(new C.Chat { Message = "@ride" });

        // ------------------------------------------------------------------ build

        protected override void Build()
        {
            ContentWidth = WidthHud;
            ContentHeight = HeightHud;
            const int top = TitleHeight;
            int ax = 12, ay = top + 12;
            _arena = new PaintBox(PaintArena) { Parent = this, Location = new Point(S(ax), S(ay)), Size = new Size(S(ArenaW), S(ArenaH)) };
            _arena.AfterPaint = DrawMount;
            for (int i = 0; i < Slots.Length; i++)
            {
                var d = Slots[i];
                int slot = (int)d.Slot;
                var box = new ModernPanel { Parent = _arena, Location = new Point(S(SlotPos[i].X), S(SlotPos[i].Y)), Size = new Size(S(46), S(60)) };
                var cell = new ModernItemCell(MirGridType.Mount, slot, 46) { Parent = box, Location = Point.Empty };
                new CellDecor(cell, d.Icon);
                cell.Click += (o, e) => PickSlot(d.Slot);
                _slotLabels[i] = W.Label(box, T(d.Name).ToUpperInvariant(), -10, 47, 66, 12, 8, true, ModernTheme.Dim, StringAlignment.Center);
                _slotLabels[i].Location = new Point(box.Size.Width / 2 - _slotLabels[i].Size.Width / 2, S(47));
                var x = new PaintBox(PaintCross) { Parent = box, Location = new Point(S(32), 0), Size = new Size(S(14), S(14)), Visible = false, Hint = T(ClientTextKeys.ModernCompTakeOff) };
                x.Click += (o, e) => TakeOffSlot(slot);
                _slotBoxes[i] = box;
                _cells[i] = cell;
                _takeOff[i] = x;
            }

            int y = ay + ArenaH + 10;
            _name = W.Label(this, string.Empty, ax, y, ArenaW, 22, 18, true, Color.White, StringAlignment.Center);
            _sub = W.Label(this, string.Empty, ax, y + 22, ArenaW, 15, 11, false, ModernTheme.Dim, StringAlignment.Center);
            y += 46;
            W.Caption(this, T(ClientTextKeys.ModernCompLoyalty), ax, y, 200);
            _loyText = W.Label(this, string.Empty, ax + 200, y, ArenaW - 200, 14, 11, true, ModernTheme.Text, StringAlignment.Far);
            _loyalty = W.At(new CompMeter(ArenaW, 10, ModernTheme.Hex("#8a2a5a"), ModernTheme.Hex("#ff6aa8"), true), this, ax, y + 18);
            y += 40;
            int bw = (ArenaW - 30 - 12) / 2;
            _ride = W.At(new ModernButton(T(ClientTextKeys.ModernCompRide), ButtonKind.Main, true), this, ax, y);
            _ride.Size = new Size(S(bw), _ride.Size.Height);
            _ride.Click += (o, e) => { if (CanRide()) Ride(); };
            _restore = W.At(new ModernButton(T(ClientTextKeys.ModernCompRestore), ButtonKind.Normal, true), this, ax + bw + 6, y);
            _restore.Size = new Size(S(bw), _restore.Size.Height);
            _restore.Click += (o, e) => Restore();
            var help = W.At(new ModernButton("?", ButtonKind.Normal, true), this, ax + ArenaW - 30, y);
            help.Size = new Size(S(30), help.Size.Height);
            help.Tooltip = T(ClientTextKeys.ModernCompHelpMounts);
            help.Click += (o, e) => (ModernRoot.Instance?.Wm.Get(HelpWindow.WindowKey) as HelpWindow)?.Show("mounts");

            W.VDivider(this, ShowW, top, HeightHud);
            _side = new ScrollView(SideW, HeightHud - 24) { Parent = this, Location = new Point(S(SideX), S(top + 12)), Background = W.Page };
            _side.Framed = false;
            _sideSig = _sig = long.MinValue;
            _bagWatch.Reset();
            _gearWatch.Reset();
            _start = -1;
            _nextPoll = 0;
        }

        // ------------------------------------------------------------------ painters

        private static void PaintArena(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            Color pb = ModernSkin.Opaque(ModernTheme.PanelB);
            using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
            using (var br = new LinearGradientBrush(new RectangleF(0, -1, b.Size.Width, b.Size.Height + 2), ModernTheme.Mix(pb, Color.Black, 0.6f), ModernTheme.Mix(pb, Color.Black, 0.9f), LinearGradientMode.Vertical))
                g.FillPath(br, p);
            Region old = g.Clip;
            g.SetClip(p, CombineMode.Intersect);
            using (var gp = new GraphicsPath())
            {
                RectangleF e = new RectangleF(b.Size.Width * 0.2f, b.Size.Height * 0.2f, b.Size.Width * 0.6f, b.Size.Height * 1.0f);
                gp.AddEllipse(e);
                using var br = new PathGradientBrush(gp) { CenterPoint = new PointF(b.Size.Width / 2f, b.Size.Height * 0.7f), CenterColor = ModernTheme.Alpha(ModernTheme.Accent, 0.25f), SurroundColors = new[] { Color.FromArgb(0, ModernTheme.Accent) } };
                g.FillPath(br, gp);
            }
            using (var gp = new GraphicsPath())
            {
                RectangleF e = new RectangleF(b.Size.Width / 2f - 115 * k, b.Size.Height - 76 * k, 230 * k, 46 * k);
                gp.AddEllipse(e);
                using var br = new PathGradientBrush(gp) { CenterColor = ModernTheme.Alpha(ModernTheme.Accent, 0.45f), SurroundColors = new[] { Color.FromArgb(0, ModernTheme.Accent) } };
                g.FillPath(br, gp);
            }
            using (var pen = new Pen(ModernTheme.Alpha(ModernTheme.Accent, 0.6f), 1f))
                g.DrawEllipse(pen, b.Size.Width / 2f - 100 * k, b.Size.Height - 64 * k, 200 * k, 22 * k);
            g.Clip = old;
            old.Dispose();
            ModernSkin.Stroke(g, p, ModernTheme.Frame);
        }

        private static void PaintCross(Graphics g, PaintBox b)
        {
            using (var br = new SolidBrush(b.Hovered ? ModernTheme.BadSoft : ModernTheme.Bad)) g.FillEllipse(br, 0, 0, b.Size.Width - 1, b.Size.Height - 1);
            ModernSkin.Text(g, "×", ModernSkin.Font(11, true), Color.White, new RectangleF(0, -1, b.Size.Width, b.Size.Height), StringAlignment.Center, StringAlignment.Center, false);
        }

        /// <summary>MountDialog.DrawMountAnimation: the frame fitted into the mount box (union of the 16 frames' offset rectangles).</summary>
        private void DrawMount(PaintBox b)
        {
            if (_start < 0 || Libraries.Prguse == null || _bounds.Width <= 0) return;
            float k = ModernRoot.K;
            float scale = Math.Min(2.5f, Math.Min(BoxW / _bounds.Width, BoxH / _bounds.Height)) * k;
            float boxX = b.DisplayLocation.X + (ArenaW - BoxW) / 2f * k, boxY = b.DisplayLocation.Y + (ArenaH - BoxH - 40) * k;
            float ox = boxX + (BoxW * k - _bounds.Width * scale) / 2f, oy = boxY + BoxH * k - _bounds.Height * scale;
            int index = _start + _frame;
            Size s = Libraries.Prguse.GetSize(index);
            if (s.IsEmpty) return;
            Point o = Libraries.Prguse.GetOffSet(index);
            HudKit.DrawScaled(Libraries.Prguse, index, new RectangleF(ox + (o.X - _bounds.X) * scale, oy + (o.Y - _bounds.Y) * scale, s.Width * scale, s.Height * scale), Color.White);
        }

        private void SetMount(int start)
        {
            if (start == _start) return;
            _start = start;
            _frame = 0;
            _bounds = RectangleF.Empty;
            if (start < 0 || Libraries.Prguse == null) { _arena.Redraw(); return; }
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < Frames; i++)
            {
                Size s = Libraries.Prguse.GetSize(start + i);
                if (s.IsEmpty) continue;
                Point o = Libraries.Prguse.GetOffSet(start + i);
                x0 = Math.Min(x0, o.X); y0 = Math.Min(y0, o.Y); x1 = Math.Max(x1, o.X + s.Width); y1 = Math.Max(y1, o.Y + s.Height);
            }
            if (x1 > x0 && y1 > y0) _bounds = new RectangleF(x0, y0, x1 - x0, y1 - y0);
            _arena.Redraw();
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void Shown()
        {
            // MountDialog.Show: refused with NoMount without a mount type
            if ((User?.MountType ?? -1) < 0)
            {
                Close();
                ModernRoot.Instance?.Popups.Confirm(T(ClientTextKeys.NoMount), PopupButtons.Ok, PopupKind.Info, null, false, T(ClientTextKeys.ModernHudWinMount));
                return;
            }
            ModernEvents.Mount.On(_mountChanged);
            ModernEvents.ItemInfo.On(OnItemInfo);
            _sig = _sideSig = long.MinValue;
            _nextPoll = 0;
        }

        private void OnItemInfo(UserItem _) { _sig = _sideSig = long.MinValue; }

        protected override void Closed()
        {
            ModernEvents.Mount.Off(_mountChanged);
            ModernEvents.ItemInfo.Off(OnItemInfo);
            _sideSig = long.MinValue;
        }

        public override void Process()
        {
            base.Process();
            long now = CMain.Time;
            if (_start >= 0 && now >= _nextFrame)
            {
                _nextFrame = now + FrameMs;
                _frame = (_frame + 1) % Frames;
                _arena.Redraw();
            }
            if (now < _nextPoll) return;
            _nextPoll = now + 250;
            if (_name == null || _name.IsDisposed) return;
            UserItem mount = MountItem;
            if (mount?.Slots != null && _gearWatch.Poll(MirGridType.Mount, mount.Slots, 0, int.MaxValue, true))
            {
                for (int i = 0; i < _cells.Length; i++) _cells[i].Refresh(false);
                _sig = _sideSig = long.MinValue;
            }
            if (_bagWatch.Poll(MirGridType.Inventory, User?.Inventory, 0, int.MaxValue, true)) _sig = _sideSig = long.MinValue;
            Refresh();
            RenderRide();
        }

        // ------------------------------------------------------------------ actions

        private void PickSlot(MountSlot s)
        {
            if (_slot == s) return;
            _slot = s;
            _sideSig = _sig = long.MinValue;
            _nextPoll = 0;
        }

        private static void ChatHint(ClientTextKeys key) => GameScene.Models?.Chat.Add(T(key), ChatType.Hint);

        /// <summary>The take-off cross: ItemMoves.Use on the mount slot (RemoveItem), refusals said up front.</summary>
        private void TakeOffSlot(int slot)
        {
            UserItem mount = MountItem;
            if (mount?.Slots == null || slot >= mount.Slots.Length || mount.Slots[slot] == null) return;
            if (User.Fishing) { ChatHint(ClientTextKeys.ModernCompNotWhileFishing); return; }
            if (User.RidingMount) { ChatHint(ClientTextKeys.ModernCompDismountFirst); return; }
            if (!HasFreeBagSlot()) { ChatHint(ClientTextKeys.ModernCompBagFull); return; }
            if (ItemMoves.IsLocked(MirGridType.Mount, slot)) return;
            ItemMoves.Use(MirGridType.Mount, slot);
        }

        private static bool HasFreeBagSlot()
        {
            UserItem[] inv = User?.Inventory;
            if (inv == null) return false;
            for (int i = 0; i < inv.Length; i++) if (inv[i] == null) return true;
            return false;
        }

        /// <summary>A bag row: ItemMoves.Use on the bag slot (re-found by its id: the bag may have moved meanwhile).</summary>
        private static void UseBag(ulong uid)
        {
            ItemSlotRef at = ItemMoves.Find(MirGridType.Inventory, uid);
            if (at == null || at.Locked) return;
            ItemMoves.Use(MirGridType.Inventory, at.ItemSlot);
        }

        /// <summary>The Restore button: the first food that keeps the max loyalty (Shape != 0), else the first food.</summary>
        private void Restore()
        {
            UserItem mount = MountItem;
            if (mount == null || mount.CurrentDura >= mount.MaxDura) return;
            UserItem pick = null;
            UserItem[] inv = User.Inventory;
            for (int i = 0; i < inv.Length; i++)
            {
                if (!IsFood(inv[i], i)) continue;
                if (inv[i].Info.Shape != 0) { pick = inv[i]; break; }
                pick ??= inv[i];
            }
            if (pick != null) UseBag(pick.UniqueID);
        }

        private static bool IsFood(UserItem it, int i) => it?.Info != null && it.Info.Type == ItemType.Food && !ItemMoves.IsLocked(MirGridType.Inventory, i);

        // ------------------------------------------------------------------ values

        private long Signature()
        {
            UserObject u = User;
            UserItem m = MountItem;
            long h = (u?.MountType ?? -1) * 7L + (int)_slot;
            if (m != null)
            {
                h = h * 31 + (long)m.UniqueID + m.CurrentDura * 3L + m.MaxDura * 5L + (m.Info?.Index ?? 0);
                if (m.Slots != null) for (int i = 0; i < m.Slots.Length; i++) h = h * 31 + (long)(m.Slots[i]?.UniqueID ?? 0) + (m.Slots[i]?.Info != null ? 1 : 0);
            }
            return h;
        }

        private void Refresh()
        {
            long sig = Signature();
            if (sig != _sig)
            {
                _sig = sig;
                UserItem mount = MountItem;
                ItemInfo info = mount?.Info;
                UserItem[] slots = mount?.Slots ?? Array.Empty<UserItem>();
                if (mount != null && info == null) GameScene.RequestItemInfo(mount.ItemIndex);
                for (int i = 0; i < slots.Length; i++) if (slots[i] != null && slots[i].Info == null) GameScene.RequestItemInfo(slots[i].ItemIndex);
                bool four = slots.Length == 4;
                if (four && _slot == MountSlot.Mask) _slot = MountSlot.Reins;

                _name.Text = mount != null ? NameOf(mount) : T(ClientTextKeys.ModernCompNoMountEquipped);
                _name.Colour = info != null && info.Grade != ItemGrade.None ? ModernTheme.Grade(info.Grade) : Color.White;
                _sub.Text = mount != null ? T(slots.Length == 1 ? ClientTextKeys.ModernCompOneGearSlot : ClientTextKeys.ModernCompGearSlots, slots.Length) : T(ClientTextKeys.ModernCompEquipMountHint);
                for (int i = 0; i < Slots.Length; i++)
                {
                    int s = (int)Slots[i].Slot;
                    bool show = mount != null && s < slots.Length;
                    if (_slotBoxes[i].Visible != show) _slotBoxes[i].Visible = show;
                    bool has = show && slots[s] != null;
                    if (_takeOff[i].Visible != has) _takeOff[i].Visible = has;
                    _slotLabels[i].Colour = Slots[i].Slot == _slot ? ModernTheme.Accent : ModernTheme.Dim;
                }

                int cur = mount?.CurrentDura ?? 0, max = mount?.MaxDura ?? 0;
                _loyText.Text = mount != null ? W.N(cur) + " / " + W.N(max) : string.Empty;
                _loyalty.Set(max > 0 ? cur / (float)max : 0, null, null);
                bool food = false;
                UserItem[] inv = User?.Inventory;
                if (inv != null) for (int i = 0; i < inv.Length; i++) if (IsFood(inv[i], i)) { food = true; break; }
                if (mount == null) _restore.SetDisabled(true, T(ClientTextKeys.ModernCompNoMountEquipped));
                else if (cur >= max) _restore.SetDisabled(true, T(ClientTextKeys.ModernCompLoyaltyFull));
                else if (!food) _restore.SetDisabled(true, T(ClientTextKeys.ModernCompNoMountFood));
                else _restore.SetDisabled(false);

                int mountType = User?.MountType ?? -1;
                SetMount(mount != null && mountType >= 0 ? (four ? 1170 : 1330) + mountType * 20 : -1);
            }
            RefreshSide();
        }

        /// <summary>Ride / Dismount (MountDialog.CanRide).</summary>
        private void RenderRide()
        {
            UserObject u = User;
            bool riding = u != null && u.RidingMount;
            _ride.Text = T(riding ? ClientTextKeys.ModernCompDismount : ClientTextKeys.ModernCompRide);
            if (CanRide()) _ride.SetDisabled(false);
            else _ride.SetDisabled(true, T(u == null || u.MountType < 0 ? ClientTextKeys.ModernCompNoMount : ClientTextKeys.ModernCompStandStill));
        }

        private static string NameOf(UserItem item) =>
            item.Info != null ? (string.IsNullOrEmpty(item.Info.FriendlyName) ? item.Info.Name : item.Info.FriendlyName) + (item.Count > 1 ? " (" + item.Count + ")" : string.Empty) : "#" + item.ItemIndex;

        // ------------------------------------------------------------------ side panel

        private long SideSignature()
        {
            UserItem m = MountItem;
            long h = (long)(m?.UniqueID ?? 0) * 7 + (int)_slot + (User != null && User.RidingMount ? 3 : 0) + (m != null && m.CurrentDura >= m.MaxDura ? 5 : 0);
            if (m?.Slots != null) for (int i = 0; i < m.Slots.Length; i++) h = h * 31 + (long)(m.Slots[i]?.UniqueID ?? 0) + (m.Slots[i]?.Info != null ? 1 : 0);
            UserItem[] inv = User?.Inventory;
            if (inv != null)
                for (int i = 0; i < inv.Length; i++)
                {
                    UserItem it = inv[i];
                    if (it?.Info == null) continue;
                    ItemType t = it.Info.Type;
                    if (t == ItemType.Food || t == ItemType.Mount || t == Slots[(int)_slot].Type)
                        h = h * 31 + (long)it.UniqueID + it.Count * 3 + i * 7 + it.CurrentDura + (ItemMoves.IsLocked(MirGridType.Inventory, i) ? 11 : 0);
                }
            return h;
        }

        private void RefreshSide()
        {
            long sig = SideSignature();
            if (sig == _sideSig) return;
            _sideSig = sig;
            ScrollView v = _side;
            for (int i = v.Content.Controls.Count - 1; i >= 0; i--) v.Content.Controls[i].Dispose();
            int w = (int)Math.Round(v.InnerWidth / ModernRoot.K) - 4, y = 0;
            UserItem mount = MountItem;
            UserItem[] slots = mount?.Slots ?? Array.Empty<UserItem>();
            UserItem[] inv = User?.Inventory ?? Array.Empty<UserItem>();
            var def = Slots[(int)_slot];

            // what the fitted gear gives (ItemInfo.Stats + the items' added stats)
            W.Caption(v.Content, T(ClientTextKeys.ModernCompGearBonuses), 0, y, w);
            y += 18;
            var sum = new Stats();
            bool any = false;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i]?.Info == null) continue;
                if (slots[i].Info.Stats != null) sum.Add(slots[i].Info.Stats);
                if (slots[i].AddedStats != null) sum.Add(slots[i].AddedStats);
            }
            int cx = 0;
            foreach (var kv in sum.Values)
            {
                if (kv.Value == 0) continue;
                any = true;
                string a = kv.Key.ToLocalizedString() + " ", b = kv.Value > 0 ? "+" + W.N(kv.Value) : W.N(kv.Value);
                int cw = (int)Math.Ceiling((ModernSkin.Measure(a, ModernSkin.Font(11)) + ModernSkin.Measure(b, ModernSkin.Font(11, true))) / ModernRoot.K) + 16;
                if (cx > 0 && cx + cw > w) { cx = 0; y += 26; }
                new PaintBox((g, pb) =>
                {
                    W.Card(g, pb.Size, Color.Empty, 0.07f);
                    float k = ModernRoot.K, aw = ModernSkin.Measure(a, ModernSkin.Font(11));
                    ModernSkin.Text(g, a, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(7 * k, 0, aw + 2, pb.Size.Height), StringAlignment.Near, StringAlignment.Center, false);
                    ModernSkin.Text(g, b, ModernSkin.Font(11, true), ModernTheme.Accent, new RectangleF(7 * k + aw, 0, pb.Size.Width, pb.Size.Height), StringAlignment.Near, StringAlignment.Center, false);
                })
                { Parent = v.Content, Location = new Point(S(cx), S(y)), Size = new Size(S(cw), S(22)), NotControl = true };
                cx += cw + 4;
            }
            if (any) y += 30;
            else { W.Label(v.Content, T(ClientTextKeys.ModernCompNoGearStats), 0, y, w, 16, 11, false, ModernTheme.Dim); y += 22; }

            // the picked slot's gear in the bag
            int fits = 0;
            for (int i = 0; i < inv.Length; i++) if (inv[i]?.Info != null && inv[i].Info.Type == def.Type) fits++;
            y += 4;
            W.Caption(v.Content, T(ClientTextKeys.ModernCompInYourBag, T(def.Name)), 0, y, w - 30);
            W.Label(v.Content, fits.ToString(), w - 30, y, 30, 14, 11, true, ModernTheme.Text, StringAlignment.Far);
            y += 18;
            if (mount == null) { W.Label(v.Content, T(ClientTextKeys.ModernCompEquipToFit), 0, y, w, 16, 11, false, ModernTheme.Dim); y += 22; }
            else
            {
                bool riding = User.RidingMount;
                UserItem current = (int)_slot < slots.Length ? slots[(int)_slot] : null;
                for (int i = 0; i < inv.Length; i++)
                {
                    UserItem it = inv[i];
                    if (it?.Info == null || it.Info.Type != def.Type) continue;
                    string statLine = StatLine(it.Info.Stats);
                    string why = current != null ? T(ClientTextKeys.ModernCompTakeOffFirst, T(def.Name).ToLowerInvariant()) : riding ? T(ClientTextKeys.ModernCompDismountFirst) : string.Empty;
                    ulong uid = it.UniqueID;
                    y = Row(v.Content, y, w, i, it, statLine.Length > 0 ? statLine : T(ClientTextKeys.ModernCompClickToFit), why, why.Length > 0 ? null : () => UseBag(uid));
                }
                if (fits == 0) { W.Label(v.Content, T(ClientTextKeys.ModernCompNoneInBag, T(def.Name).ToLowerInvariant()), 8, y, w - 8, 16, 11, false, ModernTheme.Dim); y += 24; }
            }

            // food (Restore)
            if (mount != null)
            {
                int foods = 0;
                for (int i = 0; i < inv.Length; i++) if (IsFood(inv[i], i)) foods++;
                y += 4;
                W.Caption(v.Content, T(ClientTextKeys.ModernCompFoodRestores), 0, y, w - 30);
                W.Label(v.Content, foods.ToString(), w - 30, y, 30, 14, 11, true, ModernTheme.Text, StringAlignment.Far);
                y += 18;
                bool full = mount.CurrentDura >= mount.MaxDura;
                for (int i = 0; i < inv.Length; i++)
                {
                    if (!IsFood(inv[i], i)) continue;
                    UserItem it = inv[i];
                    string line = T(ClientTextKeys.ModernCompPlusLoyalty, W.N(it.CurrentDura)) + (it.Info.Shape == 0 ? T(ClientTextKeys.ModernCompLowersMax) : string.Empty);
                    ulong uid = it.UniqueID;
                    y = Row(v.Content, y, w, i, it, line, full ? T(ClientTextKeys.ModernCompLoyaltyFull) : string.Empty, full ? null : () => UseBag(uid));
                }
                if (foods == 0) { W.Label(v.Content, T(ClientTextKeys.ModernCompNoMountFood), 8, y, w - 8, 16, 11, false, ModernTheme.Dim); y += 24; }
            }

            // other mounts in the bag (one Mount slot: equipping swaps)
            int mounts = 0;
            for (int i = 0; i < inv.Length; i++) if (inv[i]?.Info != null && inv[i].Info.Type == ItemType.Mount) mounts++;
            if (mounts > 0)
            {
                y += 4;
                W.Caption(v.Content, T(ClientTextKeys.ModernCompOtherMounts), 0, y, w - 30);
                W.Label(v.Content, mounts.ToString(), w - 30, y, 30, 14, 11, true, ModernTheme.Text, StringAlignment.Far);
                y += 18;
                for (int i = 0; i < inv.Length; i++)
                {
                    UserItem it = inv[i];
                    if (it?.Info == null || it.Info.Type != ItemType.Mount) continue;
                    ulong uid = it.UniqueID;
                    string line = T(ClientTextKeys.ModernCompOtherMountLine, it.Slots?.Length ?? 0, W.N(it.CurrentDura), W.N(it.MaxDura));
                    y = Row(v.Content, y, w, i, it, line, string.Empty, () => UseBag(uid));
                }
            }
            v.ContentHeight = S(y + 6);
        }

        private static string StatLine(Stats st)
        {
            if (st == null) return string.Empty;
            string s = string.Empty;
            foreach (var kv in st.Values)
            {
                if (kv.Value == 0) continue;
                if (s.Length > 0) s += ", ";
                s += kv.Key.ToLocalizedString() + " " + (kv.Value > 0 ? "+" : string.Empty) + kv.Value;
            }
            return s;
        }

        /// <summary>One .gl row: a read-only bag cell, the item name in its grade colour, a line (or the refusal in warn colour).</summary>
        private int Row(MirControl parent, int y, int w, int bagSlot, UserItem item, string line, string why, Action act)
        {
            string name = NameOf(item);
            Color gc = item.Info != null && item.Info.Grade != ItemGrade.None ? ModernTheme.Grade(item.Info.Grade) : Color.White;
            bool can = act != null;
            var row = new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
                using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
                using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.05f))) g.FillPath(br, p);
                if (can && b.Hovered) ModernSkin.Stroke(g, p, ModernTheme.Accent);
                float a = can ? 1f : 0.6f;
                ModernSkin.Text(g, name, ModernSkin.Font(12, true), ModernSkin.Dim(gc, a), new RectangleF(48 * k, 5 * k, b.Size.Width - 54 * k, 17 * k));
                ModernSkin.Text(g, why.Length > 0 ? why : line, ModernSkin.Font(10), why.Length > 0 ? ModernTheme.Warn : ModernSkin.Dim(ModernTheme.Dim, a), new RectangleF(48 * k, 22 * k, b.Size.Width - 54 * k, 16 * k));
            })
            { Parent = parent, Location = new Point(0, S(y)), Size = new Size(S(w), S(44)) };
            new ModernItemCell(MirGridType.Inventory, bagSlot, 36) { Parent = row, Location = new Point(S(4), S(4)), ReadOnly = true };
            if (can) row.Click += (o, e) => act();
            return y + 48;
        }
    }
}

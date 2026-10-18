// Ported from WebClient/src/ui/modern/windows/mail.ts (MailWindow): the new UI's mail window over the mailbox the desktop already
// keeps (GameScene.User.Mail from S.ReceiveMail, GameScene.Models.Mail for the parcel being composed and the sent folder).
// Classic reference: MailListDialog / MailReadLetterDialog / MailReadParcelDialog / MailComposeLetterDialog /
// MailComposeParcelDialog (Client/MirScenes/Dialogs/MailDialogs.cs) + GameScene's mail handlers.
//  - inbox / unread / attachments / locked folders over User.Mail (newest first, locked last, as GameScene sorts); a click
//    reads the mail (C.ReadMail when unopened; the server answers with a fresh list); "Mark all read" is one C.ReadMails;
//  - Lock / Unlock = C.LockMail (flipped locally first, like MailReadLetterDialog); Delete = C.DeleteMail after a popup (a
//    parcel holding gold or items asks ParcelContainsItemsOrGoldDeleteConfirm); locked mail can't be deleted;
//  - parcels: ClientMail.Collected = ready (MailReadParcelDialog enables Collect only then): C.CollectParcel, "Collect all"
//    = one C.CollectAllParcels; both wait for S.ParcelCollected / S.ReceiveMail (ModernUi.ParcelCollected / MailReceived);
//  - "New mail" composes a LETTER anywhere (C.SendMail with no gold or items, as MailComposeLetterDialog); a PARCEL only when
//    the server asks (S.MailSendRequest → ModernUi.MailSendRequest: an NPC's post office page), with the bag window open.
//    Parcel cells are ModernItemCell(MirGridType.Mail): ItemMoves' "To Mail" route attaches a picked bag item (locks its bag
//    cell, fills Models.Mail.ParcelItemsIdx, ItemMailPostageChanged → C.MailCost). "x" takes one out (C.MailLockedItem
//    Locked = false, MailComposeParcelDialog.ResetLockedCells). One cell without a stamp, five with one (an ItemType.Nothing
//    Shape 1 item in the bag). Gold through the amount box (not taken locally: the server charges it). Postage = the server's
//    S.MailCost answer; the breakdown shows the S.ServerRules numbers. Every close path of a parcel hands its items back;
//  - the Sent folder: C.RequestSentMail → S.SentMail (Models.Mail.Sent), "Remove from Sent" = C.HideSentMail;
//  - ComposeTo(name, message) opens a letter to someone (inspect, friends, … can call it).
// HIDDEN (no data): the subject line (the message's first line is shown as the subject), the recipient suggestions list.
// Desktop: the narrow layout (740 px) below a 1040 px wide screen, as the web's narrow layer.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Models;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using Client.MirSounds;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class MailWindow : ModernWindow
    {
        public const string WindowKey = "mail";
        // SendCooldownMs: the server's NextMailTime (10 s, PlayerObject.SendMail) plus 1 s for the trip and clock drift.
        // CollectAllMs: the server answers a collect-all only when something was collected, so the wait ends after this.
        private const int MaxMessage = 500, SendCooldownMs = 11000, SentDelayMs = 2000, HideDelayMs = 500, MaxReadMails = 100, H = 530, CollectAllMs = 5000;

        private enum Folder { Inbox, Unread, Attachments, Locked, Sent }
        private enum Mode { Read, Letter, Parcel }

        private static readonly (Folder F, ClientTextKeys Name, int Icon)[] Folders =
        {
            (Folder.Inbox, ClientTextKeys.ModernEcoMailInbox, ModernIcons.Inbox), (Folder.Unread, ClientTextKeys.ModernEcoMailUnread, ModernIcons.Mail),
            (Folder.Attachments, ClientTextKeys.ModernEcoMailAttachments, ModernIcons.Clip), (Folder.Locked, ClientTextKeys.ModernEcoMailLocked, ModernIcons.Lock),
            (Folder.Sent, ClientTextKeys.ModernEcoMailSent, ModernIcons.Send),
        };

        private Folder _folder = Folder.Inbox;
        private Mode _mode = Mode.Read;
        private ulong _picked, _pickedSent;
        private bool _hasPicked, _hasPickedSent;
        private string _query = string.Empty, _note = string.Empty, _err = string.Empty;
        private readonly HashSet<ulong> _busy = new HashSet<ulong>();
        private bool _collectingAll, _readingAll;
        private long _collectAllUntil;
        private string _listKey = string.Empty, _readKey = string.Empty, _navKey = string.Empty, _compKey = string.Empty;
        private bool _dirty = true;

        // composer
        private uint _gold;
        private bool _stamped;
        private long _cost = -1; // the server's quote; -1 = asked / not asked
        private bool _sending;
        private string _sendTo = string.Empty;
        private long _nextSend, _sendWaitUntil, _sentTime, _hideTime, _sentRetryAt;
        private int _cooldownShown = -1;

        // layout
        private bool _narrow;
        private int _navW, _listW, _readW;

        // controls
        private readonly PaintBox[] _folderRows = new PaintBox[Folders.Length];
        private readonly int[] _counts = new int[Folders.Length];
        private PaintBox _cap;
        private string _capText = string.Empty;
        private float _capFill;
        private ModernButton _collectAllBtn, _markReadBtn, _refreshSentBtn;
        private ScrollView _list;
        private ModernLabel _listHint;
        private ModernPanel _read, _comp, _parcelBox;
        private ScrollView _readScroll;
        private ModernPanel _readBar;
        private ModernLabel _compTitle, _compMeta, _count, _slotLbl, _slotHint, _goldAmt, _stampLbl, _errLbl;
        private ModernTextField _to, _body;
        private readonly ModernItemCell[] _slots = new ModernItemCell[MailState.ParcelSlots];
        private readonly CellDecor[] _slotDecor = new CellDecor[MailState.ParcelSlots];
        private readonly ModernButton[] _slotX = new ModernButton[MailState.ParcelSlots];
        private ModernButton _goldBtn, _sendBtn;
        private ToggleSwitch _stampSw;
        private readonly ModernLabel[] _costL = new ModernLabel[3], _costR = new ModernLabel[3];
        private readonly GridWatch _bagWatch = new GridWatch();

        private readonly Action _mailChanged, _sentChanged, _rulesChanged;
        private readonly Action<ItemSlotKey> _slotChanged;

        public MailWindow() : base(WindowKey, W.T(ClientTextKeys.ModernEcoMail), 52, 40)
        {
            _mailChanged = () => _dirty = true;
            _sentChanged = () => _dirty = true;
            _rulesChanged = () => _dirty = true;
            _slotChanged = k => { if (k.Grid == MirGridType.Mail || k.Grid == MirGridType.Inventory) _dirty = true; };
        }

        private static UserObject User => GameScene.User;
        private static List<ClientMail> Inbox => GameScene.User?.Mail ?? new List<ClientMail>();
        private static ServerPackets.ServerRules Rules => GameScene.Models?.ServerRules.Rules;
        private static MailState Model => GameScene.Models.Mail;

        // test hooks
        internal string FolderShown => _folder.ToString();
        internal string ModeShown => _mode.ToString();
        internal string Note => _note;
        internal string Error => _err;
        internal long CostShown => _cost;
        internal uint GoldShown => _gold;
        internal bool StampedShown => _stamped;
        internal bool Sending => _sending;
        internal ModernTextField ToField => _to;
        internal ModernTextField BodyField => _body;
        internal ScrollView List => _list;
        internal ModernPanel ReadBar => _readBar;
        internal PaintBox FolderRow(int i) => _folderRows[i];
        internal ModernButton SlotX(int i) => _slotX[i];

        // ------------------------------------------------------------------ build

        protected override void Build()
        {
            _narrow = Settings.ScreenWidth < S(1040);
            int width = _narrow ? 740 : 900;
            _navW = _narrow ? 146 : 160;
            _listW = _narrow ? 244 : 320;
            _readW = width - _navW - _listW;
            ContentWidth = width;
            ContentHeight = H;
            int top = TitleHeight;
            MirControl m = new ModernPanel { Parent = this, Location = new Point(0, S(top)), Size = new Size(S(width), S(H)) };

            // ---- folders
            var compose = new ModernButton(W.T(ClientTextKeys.ModernEcoMailNew), ButtonKind.Main) { Parent = m, Location = new Point(S(8), S(10)), Tooltip = W.T(ClientTextKeys.ModernEcoMailNewHint) };
            compose.Size = new Size(S(_navW - 16), S(30));
            compose.Click += (o, e) => ComposeTo(string.Empty);
            for (int i = 0; i < Folders.Length; i++)
            {
                int idx = i;
                _folderRows[i] = new PaintBox((g, b) => PaintFolder(g, b, idx)) { Parent = m, Location = new Point(S(8), S(48 + i * 33)), Size = new Size(S(_navW - 16), S(30)) };
                _folderRows[i].Click += (o, e) => PickFolder(Folders[idx].F);
                _folderRows[i].AfterPaint = b => W.DrawIcon(Folders[idx].Icon, new Rectangle(b.DisplayLocation.X + S(10), b.DisplayLocation.Y + S(8), S(14), S(14)), _folder == Folders[idx].F ? ModernTheme.Accent : ModernTheme.Dim);
            }
            _cap = new PaintBox(PaintCap) { Parent = m, Location = new Point(S(14), S(H - 34)), Size = new Size(S(_navW - 28), S(24)), NotControl = true };
            W.VDivider(m, _navW, 0, H);

            // ---- list
            int lx = _navW + 10, lw = _listW - 18;
            var search = W.At(new SearchBox(lw, W.T(ClientTextKeys.ModernEcoMailSearch)), m, lx, 10);
            if (_query.Length > 0) search.Text = _query;
            search.Search += q => { _query = q.ToLowerInvariant(); _dirty = true; };
            _collectAllBtn = SmallButton(m, W.T(ClientTextKeys.ModernEcoMailCollectAll), lx, 42, CollectAll, W.T(ClientTextKeys.ModernEcoMailCollectAllHint));
            _markReadBtn = SmallButton(m, W.T(ClientTextKeys.ModernEcoMailMarkRead), 0, 42, MarkAllRead, null);
            _markReadBtn.Location = new Point(_collectAllBtn.Location.X + _collectAllBtn.Size.Width + S(5), S(42));
            _refreshSentBtn = SmallButton(m, W.T(ClientTextKeys.ModernEcoRefresh), lx, 42, RequestSent, W.T(ClientTextKeys.ModernEcoMailRefreshHint));
            _list = new ScrollView(lw + 4, H - 112) { Parent = m, Location = new Point(S(lx), S(72)), Background = W.Page };
            _list.Framed = false;
            _listHint = W.Label(m, string.Empty, lx, H - 36, lw, 32, 10, false, ModernTheme.Dim, StringAlignment.Near, true);
            W.VDivider(m, _navW + _listW, 0, H);

            // ---- read pane
            int rx = _navW + _listW + 1;
            _read = new ModernPanel { Parent = m, Location = new Point(S(rx), 0), Size = new Size(S(_readW - 1), S(H)) };
            _readScroll = new ScrollView(_readW - 1, H - 50) { Parent = _read, Location = Point.Empty, Background = W.Page };
            _readScroll.Framed = false;
            W.Divider(_read, 0, H - 50, _readW);
            _readBar = new ModernPanel { Parent = _read, Location = new Point(S(14), S(H - 40)), Size = new Size(S(_readW - 30), S(28)) };

            // ---- composer
            _comp = new ModernPanel { Parent = m, Location = new Point(S(rx), 0), Size = new Size(S(_readW - 1), S(H)), Visible = false };
            BuildComposer(_comp, _readW - 1);

            _listKey = _readKey = _navKey = _compKey = string.Empty;
            _dirty = true;
            Render();
        }

        private static ModernButton SmallButton(MirControl parent, string text, int x, int y, Action run, string hint)
        {
            var b = new ModernButton(text) { Parent = parent, Location = new Point(S(x), S(y)), FontSize = 10 };
            if (hint != null) b.Tooltip = hint;
            b.AutoWidth(40);
            b.Click += (o, e) => run();
            return b;
        }

        private void BuildComposer(MirControl c, int w)
        {
            int x = 16, iw = w - 32;
            new PaintBox((g, b) => Eco.Badge(g, new RectangleF(1, 1, b.Size.Width - 2, b.Size.Height - 2), string.Empty, true))
            { Parent = c, Location = new Point(S(x), S(14)), Size = new Size(S(42), S(42)), NotControl = true, AfterPaint = BadgeIcon(ModernIcons.Compose, 0, 0, 42) };
            _compTitle = W.Label(c, string.Empty, x + 54, 16, iw - 54, 20, 15, true, Color.White);
            _compMeta = W.Label(c, string.Empty, x + 54, 36, iw - 54, 16, 11, false, ModernTheme.Dim);
            W.Caption(c, W.T(ClientTextKeys.ModernEcoMailTo), x, 66, 80);
            _to = W.At(new ModernTextField(iw, 26) { MaxLength = 30, Placeholder = W.T(ClientTextKeys.ModernEcoMailRecipient) }, c, x, 82);
            _to.TextChanged += _ => _dirty = true;
            W.Caption(c, W.T(ClientTextKeys.ModernEcoMailMessage), x, 116, 80);
            _body = W.At(new ModernTextField(iw, 108), c, x, 132);
            _body.Box.MultiLine();
            _body.Box.Size = new Size(_body.Size.Width - S(14), _body.Size.Height - S(10));
            _body.Box.Location = new Point(S(7), S(5));
            _body.MaxLength = MaxMessage;
            _body.Placeholder = W.T(ClientTextKeys.ModernEcoMailWrite);
            _body.TextChanged += _ => _dirty = true;
            _body.Submitted += _ => { if (!_body.Box.IsDisposed) _body.Box.TextBox.SelectedText = "\r\n"; }; // Enter = a new line
            _count = W.Label(c, string.Empty, x, 242, iw, 12, 10, false, ModernTheme.Dim, StringAlignment.Far);

            // parcel: cells, gold, stamp, postage
            _parcelBox = new ModernPanel { Parent = c, Location = new Point(S(x), S(256)), Size = new Size(S(iw), S(196)) };
            MirControl p = _parcelBox;
            _slotLbl = W.Label(p, string.Empty, 0, 0, iw, 16, 11, true, ModernTheme.AccentDim);
            for (int i = 0; i < MailState.ParcelSlots; i++)
            {
                int idx = i;
                _slots[i] = new ModernItemCell(MirGridType.Mail, i, 40) { Parent = p, Location = new Point(S(i * 48), S(22)) };
                _slotDecor[i] = new CellDecor(_slots[i], ModernIcons.Clip);
                _slotX[i] = new ModernButton("×") { Parent = p, Size = new Size(S(16), S(16)), Location = new Point(S(i * 48 + 30), S(16)), Tooltip = W.T(ClientTextKeys.ModernEcoMailTakeOut), FontSize = 11 };
                _slotX[i].Click += (o, e) => Detach(idx, true);
            }
            _slotHint = W.Label(p, W.T(ClientTextKeys.ModernEcoMailSlotHint), 0, 66, iw, 14, 10, false, ModernTheme.Dim);
            W.Caption(p, W.T(ClientTextKeys.ModernEcoGold), 0, 86, 120);
            _goldAmt = W.Label(p, string.Empty, 0, 102, 86, 22, 12, true, ModernTheme.Gold);
            _goldBtn = new ModernButton(W.T(ClientTextKeys.ModernEcoMailAddGold)) { Parent = p, Location = new Point(S(90), S(102)), Tooltip = W.T(ClientTextKeys.ModernEcoMailAddGoldHint) };
            _goldBtn.AutoWidth(70);
            _goldBtn.Click += (o, e) => PickGold();
            int sx = Math.Max(178, Math.Min(iw - 150, 230));
            W.Caption(p, W.T(ClientTextKeys.ModernEcoMailStamp), sx, 86, 140);
            _stampSw = W.At(new ToggleSwitch(false, true), p, sx, 104);
            _stampSw.Changed += SetStamp;
            _stampLbl = W.Label(p, string.Empty, sx + 40, 102, iw - sx - 40, 22, 11, false, ModernTheme.Dim);
            new PaintBox((g, b) => W.Card(g, b.Size)) { Parent = p, Location = new Point(0, S(130)), Size = new Size(S(iw), S(62)), NotControl = true };
            for (int i = 0; i < 3; i++)
            {
                // the value column: short numbers on the first two rows, the total (maybe "… free with a stamp") on the last
                int vw = i == 2 ? 200 : 60;
                _costL[i] = W.Label(p, string.Empty, 10, 135 + i * 18, iw - 20 - vw - 6, 16, 11, false, ModernTheme.Dim);
                _costR[i] = W.Label(p, string.Empty, iw - 10 - vw, 135 + i * 18, vw, 16, i == 2 ? 13 : 11, i == 2, i == 2 ? ModernTheme.Gold : Color.Empty, StringAlignment.Far);
            }
            _errLbl = W.Label(c, string.Empty, x, H - 70, iw, 16, 11, false, ModernTheme.BadSoft);
            W.Divider(c, 0, H - 50, w);
            var discard = new ModernButton(W.T(ClientTextKeys.ModernEcoMailDiscard)) { Parent = c, Location = new Point(S(x), S(H - 38)) };
            discard.AutoWidth(70);
            discard.Click += (o, e) => Discard();
            _sendBtn = new ModernButton(W.T(ClientTextKeys.ModernEcoMailSend), ButtonKind.Main) { Parent = c };
            _sendBtn.AutoWidth(90);
            _sendBtn.Location = new Point(S(w - x) - _sendBtn.Size.Width, S(H - 38));
            _sendBtn.Click += (o, e) => Send();
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void Shown()
        {
            Model.SentChanged.On(_sentChanged);
            Model.Changed.On(_mailChanged);
            GameScene.Models.ServerRules.Changed.On(_rulesChanged);
            ItemMoves.SlotChanged.On(_slotChanged);
            _listKey = _readKey = _navKey = string.Empty; // the ages are redrawn on every open
            _dirty = true;
            if (_folder == Folder.Sent) RequestSent();
            Render();
        }

        protected override void Closed()
        {
            Model.SentChanged.Off(_sentChanged);
            Model.Changed.Off(_mailChanged);
            GameScene.Models.ServerRules.Changed.Off(_rulesChanged);
            ItemMoves.SlotChanged.Off(_slotChanged);
            if (_mode == Mode.Parcel) { ResetParcel(); _mode = Mode.Read; }
            _note = string.Empty;
            _to?.Blur();
            _body?.Blur();
        }

        public override void Process()
        {
            base.Process();
            if (User == null) return;
            long now = CMain.Time;
            if (_sending && now >= _sendWaitUntil) { _sending = false; _dirty = true; } // no S.MailSent for a chat-only refusal
            if (_collectingAll && now >= _collectAllUntil) { _collectingAll = false; _dirty = true; } // nothing collected: no answer
            if (_sentRetryAt > 0 && now >= _sentRetryAt) { _sentRetryAt = 0; if (_folder == Folder.Sent) RequestSent(); }
            if (_mode != Mode.Read)
            {
                int left = _nextSend > now ? (int)((_nextSend - now + 999) / 1000) : 0;
                if (left != _cooldownShown) { _cooldownShown = left; _dirty = true; }
                if (_mode == Mode.Parcel && _bagWatch.Poll(MirGridType.Inventory, User.Inventory)) _dirty = true; // stamps in the bag
            }
            if (_dirty) Render();
        }

        public override bool HandleKey(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && _mode != Mode.Read && !e.Shift)
            {
                Discard();
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ seam (ModernUi.Economy)

        /// <summary>S.ReceiveMail: the read / lock / delete / collect / mark-all-read waits end.</summary>
        internal void MailChanged()
        {
            _busy.Clear();
            _collectingAll = _readingAll = false;
            _dirty = true;
        }

        /// <summary>S.ParcelCollected (-1 none, 0 all collected, 1 collected).</summary>
        internal void ParcelCollected(sbyte result)
        {
            _busy.Clear();
            _collectingAll = false;
            Say(result == 1 ? W.T(ClientTextKeys.ModernEcoMailCollected) : result == 0 ? W.T(ClientTextKeys.AllParcelsCollected) : W.T(ClientTextKeys.NoParcelsToCollect));
        }

        /// <summary>S.MailCost: the postage quote (MailComposeParcelDialog.ParcelCostLabel).</summary>
        internal void CostArrived(uint cost)
        {
            if (_mode != Mode.Parcel) return;
            if (cost > 0) SoundManager.PlaySound(SoundList.Gold);
            _cost = cost;
            _dirty = true;
        }

        /// <summary>ItemMailPostageChanged: an item went into a parcel cell (ItemMoves "To Mail").</summary>
        internal void PostageChanged()
        {
            if (_mode == Mode.Parcel) Quote();
        }

        /// <summary>MailComposeLetterDialog.ComposeMail: a letter to <paramref name="name"/> ("" = type it). A parcel being
        /// composed is discarded (its items are handed back).</summary>
        public void ComposeTo(string name, string message = "")
        {
            if (_mode == Mode.Parcel) ResetParcel();
            _mode = Mode.Letter;
            Open();
            ClearDraft(name, message);
            _dirty = true;
            Render();
            (string.IsNullOrEmpty(name) ? _to : _body)?.Focus();
        }

        /// <summary>S.MailSendRequest (an NPC's post office page): the parcel composer and the bag.</summary>
        public void ComposeParcel()
        {
            if (_mode == Mode.Parcel) ResetParcel();
            _mode = Mode.Parcel;
            Open();
            ClearDraft(string.Empty, string.Empty);
            ModernRoot.Instance?.Wm.Open(InventoryWindow.WindowKey); // the desktop shows the bag with the parcel dialog
            Open(); // keep the mail window in front
            Quote();
            _dirty = true;
            Render();
            _to?.Focus();
        }

        /// <summary>S.MailSent (GameScene unlocked the bag cells): the parcel / letter went (MailComposeParcelDialog.Hide).</summary>
        internal void SentResult(sbyte result)
        {
            bool was = _sending;
            _sending = false;
            if (was && result < 0)
            {
                // refused (gold / an item that can't be mailed; the server may have said why in chat): keep the composer,
                // and lock the attached items again (GameScene unlocked every bag cell)
                Say(W.T(ClientTextKeys.ModernEcoMailRefused));
                was = false;
            }
            if (!was)
            {
                // not our send (a letter's late answer): GameScene unlocked every bag cell, so lock the attached items again
                if (_mode == Mode.Parcel)
                    for (int i = 0; i < MailState.ParcelSlots; i++)
                    {
                        UserItem it = Model.ParcelItems[i];
                        ItemSlotRef cell = it != null ? ItemMoves.FindBag(it.UniqueID) : null;
                        if (cell != null) cell.Locked = true;
                    }
                _dirty = true;
                return;
            }
            if (_mode == Mode.Parcel)
            {
                for (int i = 0; i < MailState.ParcelSlots; i++)
                {
                    UserItem it = Model.ParcelItems[i];
                    if (it != null && ItemMoves.FindBag(it.UniqueID) != null) Network.Enqueue(new C.MailLockedItem { UniqueID = it.UniqueID, Locked = false });
                    ItemMoves.SetItem(MirGridType.Mail, i, null);
                    Model.ParcelItemsIdx[i] = 0;
                }
                _gold = 0;
                _stamped = false;
                _cost = -1;
            }
            _mode = Mode.Read;
            ClearDraft(string.Empty, string.Empty);
            _note = _sendTo.Length > 0 ? W.T(ClientTextKeys.ModernEcoMailSentTo, _sendTo) : string.Empty;
            if (_folder == Folder.Sent) RequestSent();
            _dirty = true;
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            _dirty = false;
            if (_list == null || _list.IsDisposed || User == null) return;
            RenderNav();
            RenderBulk();
            RenderList();
            _read.Visible = _mode == Mode.Read;
            _comp.Visible = _mode != Mode.Read;
            if (_mode == Mode.Read) RenderRead();
            else RenderComposer();
        }

        private static bool IsParcel(ClientMail m) => m.Gold > 0 || (m.Items != null && m.Items.Count > 0);
        private static bool Ready(ClientMail m) => m.Collected && IsParcel(m);

        private ClientMail Current()
        {
            if (_folder == Folder.Sent || !_hasPicked) return null;
            foreach (ClientMail m in Inbox) if (m.MailID == _picked) return m;
            return null;
        }

        private ClientSentMail CurrentSent()
        {
            if (_folder != Folder.Sent || !_hasPickedSent) return null;
            foreach (ClientSentMail s in Model.Sent) if (s.MailID == _pickedSent) return s;
            return null;
        }

        private void PaintFolder(Graphics g, PaintBox b, int i)
        {
            float k = ModernRoot.K;
            bool on = _folder == Folders[i].F;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using (GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k))
            {
                if (on || b.Hovered)
                    using (var br = new SolidBrush(W.Over(ModernTheme.Accent, on ? 0.16f : 0.08f)))
                        g.FillPath(br, p);
                if (on) ModernSkin.Stroke(g, p, W.Over(ModernTheme.Accent, 0.6f));
            }
            if (on)
                using (var br = new SolidBrush(ModernTheme.Accent))
                    g.FillRectangle(br, 1, 2 * k, 3 * k, b.Size.Height - 4 * k);
            ModernSkin.Text(g, W.T(Folders[i].Name), ModernSkin.Font(_narrow ? 11 : 12, true), on ? Color.White : b.Hovered ? ModernTheme.Text : ModernTheme.Dim, new RectangleF(32 * k, 0, b.Size.Width - 70 * k, b.Size.Height));
            string n = _counts[i].ToString();
            float w = ModernSkin.Measure(n, ModernSkin.Font(10, true)) + 12 * k;
            RectangleF pill = new RectangleF(b.Size.Width - w - 8 * k, (b.Size.Height - 15 * k) / 2f, w, 15 * k);
            bool hot = Folders[i].F == Folder.Unread && _counts[i] > 0;
            using (GraphicsPath pp = ModernSkin.RoundRect(pill, 7 * k))
            using (var br = new SolidBrush(hot ? ModernTheme.Accent : W.Over(ModernTheme.Frame, 0.5f)))
                g.FillPath(br, pp);
            ModernSkin.Text(g, n, ModernSkin.Font(10, true), hot ? Color.White : ModernTheme.Text, pill, StringAlignment.Center);
        }

        private void PaintCap(Graphics g, PaintBox b)
        {
            if (_capText.Length == 0) return;
            float k = ModernRoot.K;
            ModernSkin.Text(g, _capText, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(0, 0, b.Size.Width, 13 * k));
            RectangleF bar = new RectangleF(0, 17 * k, b.Size.Width, 4 * k);
            using (var br = new SolidBrush(Color.FromArgb(255, 4, 6, 10))) g.FillRectangle(br, bar);
            if (_capFill > 0)
                using (var br = new LinearGradientBrush(bar, ModernTheme.Mix(ModernTheme.Accent, Color.Black, 0.45f), ModernTheme.Accent, LinearGradientMode.Horizontal))
                    g.FillRectangle(br, bar.X, bar.Y, bar.Width * _capFill, bar.Height);
        }

        private void RenderNav()
        {
            List<ClientMail> list = Inbox;
            int unread = 0, att = 0, locked = 0;
            foreach (ClientMail m in list) { if (!m.Opened) unread++; if (IsParcel(m)) att++; if (m.Locked) locked++; }
            uint cap = Rules?.MailCapacity ?? 0;
            string key = _folder + "|" + list.Count + "|" + unread + "|" + att + "|" + locked + "|" + Model.Sent.Count + "|" + cap;
            if (key == _navKey) return;
            _navKey = key;
            int[] counts = { list.Count, unread, att, locked, Model.Sent.Count };
            for (int i = 0; i < Folders.Length; i++)
            {
                _counts[i] = counts[i];
                _folderRows[i].Invalidate();
            }
            _capText = cap > 0 ? W.T(ClientTextKeys.ModernEcoMailCapacity, list.Count, cap) : string.Empty;
            _capFill = cap > 0 ? Math.Min(1f, list.Count / (float)cap) : 0f;
            _cap.Invalidate();
        }

        private void RenderBulk()
        {
            bool sent = _folder == Folder.Sent;
            _collectAllBtn.Visible = _markReadBtn.Visible = !sent;
            _refreshSentBtn.Visible = sent;
            if (sent) return;
            int ready = 0, waiting = 0, unread = 0;
            foreach (ClientMail m in Inbox)
            {
                if (Ready(m)) ready++;
                else if (IsParcel(m)) waiting++;
                if (!m.Opened) unread++;
            }
            _collectAllBtn.Text = ready > 0 ? W.T(ClientTextKeys.ModernEcoMailCollectAllN, ready) : W.T(ClientTextKeys.ModernEcoMailCollectAll);
            _collectAllBtn.AutoWidth(40);
            _markReadBtn.Location = new Point(_collectAllBtn.Location.X + _collectAllBtn.Size.Width + S(5), _markReadBtn.Location.Y);
            _collectAllBtn.SetDisabled(_collectingAll || ready == 0,
                _collectingAll ? W.T(ClientTextKeys.ModernWinWaitingServer) : waiting > 0 ? W.T(ClientTextKeys.ModernEcoMailVisitSafeN, waiting) : W.T(ClientTextKeys.NoParcelsToCollect));
            _markReadBtn.SetDisabled(_readingAll || unread == 0, _readingAll ? W.T(ClientTextKeys.ModernWinWaitingServer) : W.T(ClientTextKeys.ModernEcoMailNothingUnread));
        }

        private bool Matches(ClientMail m)
        {
            switch (_folder)
            {
                case Folder.Unread: if (m.Opened) return false; break;
                case Folder.Attachments: if (!IsParcel(m)) return false; break;
                case Folder.Locked: if (!m.Locked) return false; break;
            }
            if (_query.Length == 0) return true;
            if ((m.SenderName ?? string.Empty).ToLowerInvariant().Contains(_query) || (m.Message ?? string.Empty).ToLowerInvariant().Contains(_query)) return true;
            if (m.Items != null)
                foreach (UserItem it in m.Items)
                    if (it?.Info != null && it.Info.Name.ToLowerInvariant().Contains(_query)) return true;
            return false;
        }

        /// <summary>The first line of a message (the subject) and the rest on one line (the preview).</summary>
        private static (string Subject, string Preview) Split(string msg)
        {
            string text = (msg ?? string.Empty).Replace("\\r\\n", "\n").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            int nl = text.IndexOf('\n');
            if (nl < 0) return (text, string.Empty);
            return (text.Substring(0, nl).Trim(), text.Substring(nl + 1).Replace('\n', ' ').Trim());
        }

        private static string Body(string msg) => (msg ?? string.Empty).Replace("\\r\\n", "\n").Replace("\r\n", "\n").Replace('\r', '\n');

        private sealed class Tag
        {
            public string Text;
            public Color Colour;
            public int Icon = -1;
            public bool Done;
        }

        private void RenderList()
        {
            if (_folder == Folder.Sent) { RenderSentList(); return; }
            var rows = new List<ClientMail>();
            foreach (ClientMail m in Inbox) if (Matches(m)) rows.Add(m);
            bool hasSel = _mode == Mode.Read && _hasPicked;
            var sb = new System.Text.StringBuilder("in|").Append(_folder).Append('|').Append(_query).Append('|').Append(hasSel ? _picked : 0UL).Append('|');
            foreach (ulong b in _busy) sb.Append(b).Append(',');
            sb.Append('|');
            foreach (ClientMail m in rows) sb.Append(m.MailID).Append(':').Append(m.Opened ? 1 : 0).Append(m.Locked ? 1 : 0).Append(m.Collected ? 1 : 0).Append(m.Gold).Append(':').Append(m.Items?.Count ?? 0).Append(',');
            string key = sb.ToString();
            if (key == _listKey) return;
            _listKey = key;
            ClearList();
            int y = 0, w = _list.InnerWidth;
            foreach (ClientMail m in rows)
            {
                var (subj, pv) = Split(m.Message);
                var tags = new List<Tag>();
                if (m.Gold > 0) tags.Add(new Tag { Text = W.N(m.Gold), Colour = ModernTheme.Gold, Icon = ModernIcons.Coin });
                if (m.Items != null && m.Items.Count > 0) tags.Add(new Tag { Text = W.T(m.Items.Count == 1 ? ClientTextKeys.ModernEcoOneItem : ClientTextKeys.ModernEcoNItems, m.Items.Count), Colour = ModernTheme.AccentDim, Icon = ModernIcons.Clip });
                if (IsParcel(m) && !m.Collected) tags.Add(new Tag { Text = W.T(ClientTextKeys.ModernEcoMailWaiting), Colour = ModernTheme.Warn });
                if (m.Locked) tags.Add(new Tag { Text = W.T(ClientTextKeys.ModernEcoMailLocked), Colour = ModernTheme.AccentDim, Icon = ModernIcons.Lock });
                if (!m.CanReply) tags.Add(new Tag { Text = W.T(ClientTextKeys.ModernEcoMailNoReply), Colour = ModernTheme.Dim, Done = true });
                ulong id = m.MailID;
                bool sel = hasSel && id == _picked, isNew = !m.Opened, busy = _busy.Contains(id), system = !m.CanReply;
                string sender = m.SenderName ?? string.Empty, ago = Eco.Ago(m.DateSent);
                int h = RowHeight(pv.Length > 0, tags.Count > 0);
                var row = new PaintBox((g, b) => PaintMailRow(g, b, sender, ago, subj, pv, tags, sel, isNew, busy, system))
                { Parent = _list.Content, Location = new Point(0, y), Size = new Size(w, S(h)), AfterPaint = system ? BadgeIcon(ModernIcons.Mail, 12, 8, 32) : null };
                row.Click += (o, e) => PickRow(id);
                y += S(h + 3);
            }
            if (rows.Count == 0)
                W.Label(_list.Content, W.T(_query.Length > 0 ? ClientTextKeys.ModernEcoNoMatch : _folder == Folder.Unread ? ClientTextKeys.ModernEcoMailCaughtUp : ClientTextKeys.ModernNothingHere),
                    0, 30, (int)(w / ModernRoot.K), 20, 12, false, ModernTheme.Dim, StringAlignment.Center);
            _list.FitContent(0);
            bool waiting = false;
            foreach (ClientMail m in Inbox) if (IsParcel(m) && !m.Collected) waiting = true;
            _listHint.Text = waiting ? W.T(ClientTextKeys.ModernEcoMailWaitingHint) : string.Empty;
        }

        /// <summary>Draws a stroke icon in the middle of a badge disc at (x, y, size) HUD px of a PaintBox (render time).</summary>
        private static Action<PaintBox> BadgeIcon(int icon, int x, int y, int size) => b =>
        {
            int s = S(size * 0.45f);
            W.DrawIcon(icon, new Rectangle(b.DisplayLocation.X + S(x) + (S(size) - s) / 2, b.DisplayLocation.Y + S(y) + (S(size) - s) / 2, s, s), ModernTheme.Accent);
        };

        private static int RowHeight(bool preview, bool tags) => 8 + 16 + 16 + (preview ? 15 : 0) + (tags ? 21 : 0) + 6;

        private void ClearList()
        {
            for (int i = _list.Content.Controls.Count - 1; i >= 0; i--) _list.Content.Controls[i].Dispose();
        }

        private void RenderSentList()
        {
            var rows = new List<ClientSentMail>();
            foreach (ClientSentMail s in Model.Sent)
                if (_query.Length == 0 || (s.RecipientName ?? string.Empty).ToLowerInvariant().Contains(_query) || (s.Message ?? string.Empty).ToLowerInvariant().Contains(_query)) rows.Add(s);
            bool hasSel = _mode == Mode.Read && _hasPickedSent;
            var sb = new System.Text.StringBuilder("sent|").Append(_query).Append('|').Append(hasSel ? _pickedSent : 0UL).Append('|').Append(Model.SentKnown).Append('|');
            foreach (ClientSentMail s in rows) sb.Append(s.MailID).Append(':').Append(s.Opened ? 1 : 0).Append(s.Collected ? 1 : 0).Append(',');
            string key = sb.ToString();
            if (key == _listKey) return;
            _listKey = key;
            ClearList();
            int y = 0, w = _list.InnerWidth;
            foreach (ClientSentMail s in rows)
            {
                var (subj, pv) = Split(s.Message);
                var tags = new List<Tag>();
                if (s.Gold > 0) tags.Add(new Tag { Text = W.N(s.Gold), Colour = ModernTheme.Gold, Icon = ModernIcons.Coin, Done = true });
                if (s.ItemCount > 0) tags.Add(new Tag { Text = W.T(s.ItemCount == 1 ? ClientTextKeys.ModernEcoOneItem : ClientTextKeys.ModernEcoNItems, s.ItemCount), Colour = ModernTheme.AccentDim, Icon = ModernIcons.Clip, Done = true });
                tags.Add(new Tag { Text = W.T(s.Opened ? ClientTextKeys.ModernEcoMailRead : ClientTextKeys.ModernEcoMailNotRead), Colour = s.Opened ? ModernTheme.Dim : ModernTheme.AccentDim, Done = s.Opened });
                ulong id = s.MailID;
                bool sel = hasSel && id == _pickedSent;
                string who = W.T(ClientTextKeys.ModernEcoMailToName, s.RecipientName ?? string.Empty), ago = Eco.Ago(DateTime.FromBinary(s.DateSent)), initial = s.RecipientName ?? string.Empty;
                int h = RowHeight(pv.Length > 0, true);
                var row = new PaintBox((g, b) => PaintMailRow(g, b, who, ago, subj, pv, tags, sel, false, false, false, initial))
                { Parent = _list.Content, Location = new Point(0, y), Size = new Size(w, S(h)) };
                row.Click += (o, e) => PickRow(id);
                y += S(h + 3);
            }
            if (rows.Count == 0)
                W.Label(_list.Content, W.T(!Model.SentKnown ? ClientTextKeys.ModernEcoLoading : _query.Length > 0 ? ClientTextKeys.ModernEcoNoMatch : ClientTextKeys.ModernEcoMailNoSent),
                    0, 30, (int)(w / ModernRoot.K), 20, 12, false, ModernTheme.Dim, StringAlignment.Center);
            _list.FitContent(0);
            _listHint.Text = W.T(ClientTextKeys.ModernEcoMailSentHint);
        }

        private static void PaintMailRow(Graphics g, PaintBox b, string sender, string ago, string subj, string pv, List<Tag> tags, bool sel, bool isNew, bool busy, bool system, string initial = null)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using (GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k))
            {
                Color fill = sel ? W.Over(ModernTheme.Accent, 0.14f) : b.Hovered ? W.Over(ModernTheme.Accent, 0.07f) : W.Over(ModernTheme.FrameLight, 0.04f);
                using (var br = new SolidBrush(fill)) g.FillPath(br, p);
                if (sel || b.Hovered) ModernSkin.Stroke(g, p, sel ? ModernTheme.Accent : W.Over(ModernTheme.Accent, 0.45f));
            }
            if (isNew)
                using (var br = new SolidBrush(ModernTheme.Accent))
                    g.FillEllipse(br, 3 * k, b.Size.Height / 2f - 2.5f * k, 5 * k, 5 * k);
            float dim = busy ? 0.45f : 0f;
            Color C(Color c) => dim > 0 ? ModernSkin.Dim(c, dim) : c;
            Eco.Badge(g, new RectangleF(12 * k, 8 * k, 32 * k, 32 * k), system ? string.Empty : Eco.Initial(initial ?? sender), system);
            float x = 53 * k, w = b.Size.Width - x - 8 * k, y = 7 * k;
            float agoW = ModernSkin.Measure(ago, ModernSkin.Font(10)) + 2;
            ModernSkin.Text(g, sender, ModernSkin.Font(12, isNew), C(isNew ? Color.White : ModernTheme.Text), new RectangleF(x, y, w - agoW - 6 * k, 16 * k));
            ModernSkin.Text(g, ago, ModernSkin.Font(10), C(ModernTheme.Dim), new RectangleF(x + w - agoW, y, agoW, 16 * k), StringAlignment.Far);
            y += 16 * k;
            string s = subj.Length > 0 ? subj : W.T(ClientTextKeys.ModernEcoMailNoMessage);
            ModernSkin.Text(g, s, ModernSkin.Font(12, isNew), C(isNew ? Color.White : ModernTheme.Mix(ModernTheme.Text, ModernTheme.Dim, 0.85f)), new RectangleF(x, y, w, 16 * k));
            y += 16 * k;
            if (pv.Length > 0)
            {
                ModernSkin.Text(g, pv, ModernSkin.Font(11), C(ModernTheme.Dim), new RectangleF(x, y, w, 15 * k));
                y += 15 * k;
            }
            if (tags.Count == 0) return;
            y += 3 * k;
            float tx = x;
            Font f = ModernSkin.Font(10, true);
            foreach (Tag t in tags)
            {
                float tw = ModernSkin.Measure(t.Text, f) + (t.Icon >= 0 ? 14 * k : 0) + 12 * k;
                if (tx + tw > x + w) break;
                RectangleF pill = new RectangleF(tx, y, tw, 16 * k);
                using (GraphicsPath pp = ModernSkin.RoundRect(pill, 8 * k))
                    ModernSkin.Stroke(g, pp, t.Done ? W.Over(ModernTheme.Frame, 0.6f) : ModernTheme.Mix(t.Colour, W.Page, 0.45f));
                if (t.Icon >= 0)
                {
                    // the tag icon is a small dot in the tag's colour (stroke icons need render time; rows are painted)
                    using var br = new SolidBrush(C(t.Done ? ModernTheme.Dim : t.Colour));
                    g.FillEllipse(br, tx + 6 * k, y + 5.5f * k, 5 * k, 5 * k);
                }
                ModernSkin.Text(g, t.Text, f, C(t.Done ? ModernTheme.Dim : t.Colour), new RectangleF(tx + (t.Icon >= 0 ? 14 * k : 6 * k), y, tw, 16 * k));
                tx += tw + 5 * k;
            }
        }

        private void RenderRead()
        {
            if (_folder == Folder.Sent) { RenderSentRead(); return; }
            ClientMail m = Current();
            string key = m == null ? "none|" + _note
                : m.MailID + "|" + m.Opened + "|" + m.Locked + "|" + m.Collected + "|" + m.Gold + "|" + (m.Items?.Count ?? 0) + "|" + _busy.Contains(m.MailID) + "|" + _note;
            if (key == _readKey) return;
            _readKey = key;
            ClearRead();
            if (m == null)
            {
                Empty();
                return;
            }
            var (subj, _) = Split(m.Message);
            int y = ReadHeader(m.SenderName, subj, W.T(ClientTextKeys.ModernEcoMailFrom, m.SenderName ?? string.Empty, m.DateSent.ToString("g")) + (m.CanReply ? string.Empty : " · " + W.T(ClientTextKeys.ModernEcoMailSystem)), !m.CanReply);
            y = ReadBody(y, m.Message);
            int iw = _readW - 34;
            if (IsParcel(m))
            {
                W.Label(_readScroll.Content, W.T(m.Collected ? ClientTextKeys.ModernEcoMailAttachments : ClientTextKeys.ModernEcoMailAttachmentsWaiting), 16, y, iw, 16, 11, true, ModernTheme.AccentDim);
                y += 20;
                int x = 16;
                if (m.Gold > 0)
                {
                    string gold = W.T(ClientTextKeys.ModernEcoGoldN, W.N(m.Gold));
                    int gw = (int)(ModernSkin.Measure(gold, ModernSkin.Font(14, true)) / ModernRoot.K) + 44;
                    new PaintBox((g, b) => PaintGoldChip(g, b, gold)) { Parent = _readScroll.Content, Location = new Point(S(x), S(y)), Size = new Size(S(gw), S(40)), NotControl = true };
                    x += gw + 6;
                }
                if (m.Items != null)
                    for (int i = 0; i < m.Items.Count; i++)
                    {
                        UserItem it = m.Items[i];
                        new EcoItemView(40, () => it) { Parent = _readScroll.Content, Location = new Point(S(x), S(y)) };
                        x += 46;
                    }
                y += 48;
                if (!m.Collected)
                {
                    W.Label(_readScroll.Content, W.T(ClientTextKeys.ModernEcoMailVisitSafe) + " " + W.T(ClientTextKeys.ModernEcoMailHandedOver), 16, y, iw, 30, 11, false, ModernTheme.Warn, StringAlignment.Near, true);
                    y += 34;
                }
            }
            if (_note.Length > 0) W.Label(_readScroll.Content, _note, 16, y, iw, 16, 11, false, ModernTheme.Accent);
            _readScroll.FitContent(10);

            // actions
            bool busy = _busy.Contains(m.MailID);
            ulong id = m.MailID;
            var reply = BarButton(W.T(ClientTextKeys.ModernEcoMailReply), () => Reply(id), ButtonKind.Normal);
            reply.SetDisabled(!m.CanReply, W.T(ClientTextKeys.ModernEcoMailNoReplyHint));
            var lockBtn = BarButton(W.T(m.Locked ? ClientTextKeys.ModernEcoMailUnlock : ClientTextKeys.ModernEcoMailLock), () => ToggleLock(id), ButtonKind.Normal);
            lockBtn.Tooltip = W.T(ClientTextKeys.ModernEcoMailLockHint);
            var del = BarButton(W.T(ClientTextKeys.ModernEcoMailDelete), () => Remove(id), ButtonKind.Normal);
            if (IsParcel(m)) del.Tooltip = W.T(ClientTextKeys.ModernEcoMailDeleteParcelHint);
            del.SetDisabled(m.Locked || busy, m.Locked ? W.T(ClientTextKeys.ModernEcoMailUnlockFirst) : W.T(ClientTextKeys.ModernWinWaitingServer));
            if (Ready(m))
            {
                string what = (m.Gold > 0 ? W.T(ClientTextKeys.ModernEcoGoldN, W.N(m.Gold)) : string.Empty)
                    + (m.Gold > 0 && m.Items.Count > 0 ? " + " : string.Empty)
                    + (m.Items.Count > 0 ? W.T(m.Items.Count == 1 ? ClientTextKeys.ModernEcoOneItem : ClientTextKeys.ModernEcoNItems, m.Items.Count) : string.Empty);
                var collect = BarButton(W.T(ClientTextKeys.ModernEcoMailCollectWhat, what), () => Collect(id), ButtonKind.Main, true);
                if (collect.Location.X < _barX + S(6))
                {
                    // no room beside Reply / Lock / Delete (narrow layout): the short label, the details in the hint
                    collect.Text = W.T(ClientTextKeys.ModernEcoMailCollect);
                    collect.Tooltip = W.T(ClientTextKeys.ModernEcoMailCollectWhat, what);
                    collect.AutoWidth(56);
                    collect.Location = new Point(_readBar.Size.Width - collect.Size.Width, S(3));
                }
                collect.SetDisabled(busy, W.T(ClientTextKeys.ModernWinWaitingServer));
            }
            else if (IsParcel(m))
            {
                var hint = W.Label(_readBar, W.T(ClientTextKeys.ModernEcoMailEnterSafe), 0, 0, 140, 28, 11, false, ModernTheme.Dim, StringAlignment.Far);
                hint.Location = new Point(_readBar.Size.Width - hint.Size.Width, 0);
            }
        }

        private int _barX;

        private ModernButton BarButton(string text, Action run, ButtonKind kind, bool right = false)
        {
            var b = new ModernButton(text, kind) { Parent = _readBar };
            b.AutoWidth(56);
            if (right) b.Location = new Point(_readBar.Size.Width - b.Size.Width, S(3));
            else
            {
                b.Location = new Point(_barX, S(3));
                _barX += b.Size.Width + S(5);
            }
            b.Click += (o, e) => run();
            return b;
        }

        private void ClearRead()
        {
            for (int i = _readScroll.Content.Controls.Count - 1; i >= 0; i--) _readScroll.Content.Controls[i].Dispose();
            for (int i = _readBar.Controls.Count - 1; i >= 0; i--) _readBar.Controls[i].Dispose();
            _barX = 0;
            _readScroll.ScrollTo(0);
        }

        private void Empty()
        {
            W.Label(_readScroll.Content, W.T(ClientTextKeys.ModernEcoMailPick), 0, 60, _readW - 12, 20, 12, false, ModernTheme.Dim, StringAlignment.Center);
            if (_note.Length > 0) W.Label(_readScroll.Content, _note, 0, 86, _readW - 12, 18, 11, false, ModernTheme.Accent, StringAlignment.Center);
            _readScroll.FitContent(0);
        }

        private int ReadHeader(string name, string subj, string meta, bool system)
        {
            string initial = system ? string.Empty : Eco.Initial(name);
            new PaintBox((g, b) => Eco.Badge(g, new RectangleF(1, 1, b.Size.Width - 2, b.Size.Height - 2), initial, system))
            { Parent = _readScroll.Content, Location = new Point(S(16), S(14)), Size = new Size(S(42), S(42)), NotControl = true, AfterPaint = system ? BadgeIcon(ModernIcons.Mail, 0, 0, 42) : null };
            string s = subj.Length > 0 ? subj : W.T(ClientTextKeys.ModernEcoMailNoMessage);
            if (s.Length > 80) s = s.Substring(0, 79) + "…";
            W.Label(_readScroll.Content, s, 70, 14, _readW - 100, 20, 15, true, Color.White);
            W.Label(_readScroll.Content, meta, 70, 36, _readW - 100, 16, 11, false, ModernTheme.Dim);
            return 66;
        }

        private int ReadBody(int y, string message)
        {
            int iw = _readW - 34;
            string text = Body(message);
            Font f = ModernSkin.Font(13);
            float hpx = ModernSkin.MeasureWrapped(text.Length > 0 ? text : " ", f, S(iw - 28)).Height;
            int h = Math.Max(40, (int)Math.Ceiling(hpx / ModernRoot.K) + 26);
            var card = new PaintBox((g, b) => W.Card(g, b.Size)) { Parent = _readScroll.Content, Location = new Point(S(16), S(y)), Size = new Size(S(iw), S(h)), NotControl = true };
            W.Label(card, text, 14, 12, iw - 28, h - 20, 13, false, ModernTheme.Mix(ModernTheme.Text, ModernTheme.Dim, 0.92f), StringAlignment.Near, true);
            return y + h + 12;
        }

        private static void PaintGoldChip(Graphics g, PaintBox b, string text)
        {
            float k = ModernRoot.K;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using GraphicsPath p = ModernSkin.RoundRect(r, ModernTheme.Radius * k);
            using (var br = new SolidBrush(W.Over(ModernTheme.Gold, 0.08f))) g.FillPath(br, p);
            ModernSkin.Stroke(g, p, W.Over(ModernTheme.Gold, 0.4f));
            using (var br = new SolidBrush(ModernTheme.Gold)) g.FillEllipse(br, 10 * k, b.Size.Height / 2f - 6 * k, 12 * k, 12 * k);
            ModernSkin.Text(g, text, ModernSkin.Font(14, true), ModernTheme.Gold, new RectangleF(28 * k, 0, b.Size.Width - 30 * k, b.Size.Height));
        }

        private void RenderSentRead()
        {
            ClientSentMail s = CurrentSent();
            string key = s == null ? "sentnone|" + _note : "sent|" + s.MailID + "|" + s.Opened + "|" + s.Collected + "|" + _note;
            if (key == _readKey) return;
            _readKey = key;
            ClearRead();
            if (s == null)
            {
                Empty();
                return;
            }
            var (subj, _) = Split(s.Message);
            int y = ReadHeader(s.RecipientName, subj, W.T(ClientTextKeys.ModernEcoMailToMeta, s.RecipientName ?? string.Empty, DateTime.FromBinary(s.DateSent).ToString("g")), false);
            y = ReadBody(y, s.Message);
            int iw = _readW - 34;
            if (s.Gold > 0 || s.ItemCount > 0)
            {
                string with = (s.Gold > 0 ? W.T(ClientTextKeys.ModernEcoGoldN, W.N(s.Gold)) : string.Empty)
                    + (s.Gold > 0 && s.ItemCount > 0 ? " · " : string.Empty)
                    + (s.ItemCount > 0 ? W.T(s.ItemCount == 1 ? ClientTextKeys.ModernEcoOneItem : ClientTextKeys.ModernEcoNItems, s.ItemCount) : string.Empty);
                W.Label(_readScroll.Content, W.T(ClientTextKeys.ModernEcoMailSentWith), 16, y, iw, 16, 11, true, ModernTheme.AccentDim);
                W.Label(_readScroll.Content, with + (s.Collected ? string.Empty : " · " + W.T(ClientTextKeys.ModernEcoMailSentWaiting)), 16, y + 18, iw, 16, 11, false, ModernTheme.Dim);
                y += 42;
            }
            if (_note.Length > 0) W.Label(_readScroll.Content, _note, 16, y, iw, 16, 11, false, ModernTheme.Accent);
            _readScroll.FitContent(10);
            W.Label(_readBar, W.T(s.Opened ? ClientTextKeys.ModernEcoMailHasRead : ClientTextKeys.ModernEcoMailNotRead, s.RecipientName ?? string.Empty), 0, 0, 200, 28, 11, false, ModernTheme.Dim);
            ulong id = s.MailID;
            var hide = new ConfirmTwiceButton(W.T(ClientTextKeys.ModernEcoMailHideSent)) { Parent = _readBar, Tooltip = W.T(ClientTextKeys.ModernEcoMailHideSentHint) };
            hide.AutoWidth(90);
            hide.Location = new Point(_readBar.Size.Width - hide.Size.Width, S(3));
            hide.Confirmed += () => HideSent(id);
        }

        private void RenderComposer()
        {
            bool parcel = _mode == Mode.Parcel;
            _compTitle.Text = W.T(parcel ? ClientTextKeys.ModernEcoMailNewParcel : ClientTextKeys.ModernEcoMailNewLetter);
            _compMeta.Text = W.T(parcel ? ClientTextKeys.ModernEcoMailParcelMeta : ClientTextKeys.ModernEcoMailLetterMeta);
            _parcelBox.Visible = parcel;
            _count.Text = Crlf(_body.Text).Length + " / " + MaxMessage;
            if (parcel) RenderParcel();
            string why = SendProblem();
            _sendBtn.Text = W.T(_sending ? ClientTextKeys.ModernEcoMailSending : ClientTextKeys.ModernEcoMailSend);
            _sendBtn.SetDisabled(why.Length > 0, why);
            _errLbl.Text = _err;
        }

        private int StampCount()
        {
            int n = 0;
            UserItem[] inv = User?.Inventory;
            if (inv == null) return 0;
            foreach (UserItem it in inv)
                if (it?.Info != null && it.Info.Type == ItemType.Nothing && it.Info.Shape == 1) n += it.Count;
            return n;
        }

        private void RenderParcel()
        {
            int open = _stamped ? MailState.ParcelSlots : 1, used = 0;
            for (int i = 0; i < MailState.ParcelSlots; i++)
            {
                bool has = Model.ParcelItems[i] != null;
                if (has) used++;
                bool off = i >= open;
                _slots[i].Enabled = !off && !_sending;
                _slotDecor[i].SetMode(off ? 2 : 0);
                _slotDecor[i].Sync();
                _slotX[i].Visible = has && !_sending;
            }
            _slotLbl.Text = W.T(ClientTextKeys.ModernEcoMailItemsN, used, open) + (_stamped ? string.Empty : " " + W.T(ClientTextKeys.ModernEcoMailFiveWithStamp));
            _goldAmt.Text = W.T(ClientTextKeys.ModernEcoGoldN, W.N(_gold));
            _goldBtn.Text = W.T(_gold > 0 ? ClientTextKeys.ModernWinChange : ClientTextKeys.ModernEcoMailAddGold);
            int stamps = StampCount();
            _stampSw.Set(_stamped);
            _stampSw.Lock(!_stamped && stamps <= 0, W.T(ClientTextKeys.ModernEcoMailNeedStamp));
            _stampLbl.Text = stamps > 0 ? W.T(ClientTextKeys.ModernEcoMailStampsN, stamps) : W.T(ClientTextKeys.ModernEcoMailNoStamps);

            // postage: the server's quote, split by the S.ServerRules numbers
            ServerPackets.ServerRules r = Rules;
            bool free = _stamped && r != null && r.MailFreeWithStamp;
            long goldPart = r != null && !free ? _gold / 1000 * (long)r.MailCostPer1KGold : 0;
            long ins = _cost >= 0 && r != null ? Math.Max(0, _cost - goldPart) : -1;
            if (r != null)
            {
                _costL[0].Text = W.T(ClientTextKeys.ModernEcoMailGoldPostage, W.N(r.MailCostPer1KGold));
                _costR[0].Text = free ? "0" : W.N(goldPart);
                _costL[1].Text = W.T(ClientTextKeys.ModernEcoMailInsurance, r.MailItemInsurancePercentage);
                _costR[1].Text = free ? "0" : ins < 0 ? "…" : W.N(ins);
            }
            else
            {
                _costL[0].Text = _costR[0].Text = _costL[1].Text = _costR[1].Text = string.Empty;
            }
            _costL[2].Text = W.T(ClientTextKeys.ModernEcoMailTotal);
            if (free && _cost == 0)
            {
                _costR[2].Text = _gold > 0 ? W.T(ClientTextKeys.ModernEcoMailFreeWithGold, W.N(_gold)) : W.T(ClientTextKeys.ModernEcoMailFree);
                _costR[2].Colour = ModernTheme.Accent;
            }
            else
            {
                _costR[2].Text = _cost < 0 ? "…" : W.T(ClientTextKeys.ModernEcoGoldN, W.N(_gold + _cost));
                _costR[2].Colour = ModernTheme.Gold;
            }
        }

        private static string Crlf(string s) => (s ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "\r\n");

        /// <summary>Why Send is off ("" = it may send).</summary>
        private string SendProblem()
        {
            if (_sending) return W.T(ClientTextKeys.ModernWinWaitingServer);
            long wait = _nextSend - CMain.Time;
            if (wait > 0) return W.T(ClientTextKeys.ModernEcoMailSendAgainIn, (wait + 999) / 1000);
            if (_to.Text.Trim().Length == 0) return W.T(ClientTextKeys.ModernEcoMailWho);
            if (Crlf(_body.Text).Length > MaxMessage) return W.T(ClientTextKeys.ModernEcoMailTooLong, MaxMessage);
            if (_mode == Mode.Letter) return _body.Text.Trim().Length > 0 ? string.Empty : W.T(ClientTextKeys.ModernEcoMailWriteFirst);
            bool any = false;
            for (int i = 0; i < MailState.ParcelSlots; i++) if (Model.ParcelItems[i] != null) any = true;
            if (_body.Text.Trim().Length == 0 && _gold == 0 && !any) return W.T(ClientTextKeys.ModernEcoMailWriteOrAdd);
            if (_cost < 0) return W.T(ClientTextKeys.ModernEcoMailPostageWait);
            if (_gold + _cost > GameScene.Gold) return W.T(ClientTextKeys.ModernEcoMailNotEnoughGold);
            return string.Empty;
        }

        // ------------------------------------------------------------------ actions: list / reading

        private void Say(string text)
        {
            _note = text ?? string.Empty;
            _dirty = true;
        }

        private void PickFolder(Folder f)
        {
            if (_folder == f && _mode == Mode.Read) return;
            if (_mode == Mode.Parcel) ResetParcel();
            _folder = f;
            _mode = Mode.Read;
            _note = string.Empty;
            _list.ScrollTo(0);
            _dirty = true;
            if (f == Folder.Sent) RequestSent();
        }

        private void PickRow(ulong id)
        {
            if (_mode == Mode.Parcel) ResetParcel();
            _mode = Mode.Read;
            _note = string.Empty;
            _dirty = true;
            if (_folder == Folder.Sent)
            {
                _pickedSent = id;
                _hasPickedSent = true;
                return;
            }
            _picked = id;
            _hasPicked = true;
            foreach (ClientMail m in Inbox)
                if (m.MailID == id && !m.Opened) Network.Enqueue(new C.ReadMail { MailID = id }); // the server answers with the list
        }

        private void MarkAllRead()
        {
            var ids = new List<ulong>();
            foreach (ClientMail m in Inbox)
                if (!m.Opened && ids.Count < MaxReadMails) ids.Add(m.MailID);
            if (ids.Count == 0) return;
            Network.Enqueue(new C.ReadMails { MailIDs = ids });
            _readingAll = true;
            _dirty = true;
        }

        private void CollectAll()
        {
            if (_collectingAll) return; // one C.CollectAllParcels at a time (until the server's answer or CollectAllMs)
            bool any = false;
            foreach (ClientMail m in Inbox) if (Ready(m)) any = true;
            if (!any) { Say(W.T(ClientTextKeys.NoParcelsToCollect)); return; }
            Network.Enqueue(new C.CollectAllParcels());
            _collectingAll = true;
            _collectAllUntil = CMain.Time + CollectAllMs;
            _dirty = true;
            Say(string.Empty);
        }

        private void Collect(ulong id)
        {
            ClientMail m = Find(id);
            if (m == null || !Ready(m) || _busy.Contains(id)) return;
            _busy.Add(id);
            Network.Enqueue(new C.CollectParcel { MailID = id });
            Say(string.Empty);
        }

        private static ClientMail Find(ulong id)
        {
            foreach (ClientMail m in Inbox) if (m.MailID == id) return m;
            return null;
        }

        private void ToggleLock(ulong id)
        {
            ClientMail m = Find(id);
            if (m == null) return;
            m.Locked = !m.Locked; // MailReadLetterDialog.LockButton (the server answers with the list)
            Network.Enqueue(new C.LockMail { MailID = id, Lock = m.Locked });
            _readKey = _listKey = _navKey = string.Empty;
            _dirty = true;
        }

        /// <summary>DeleteButton: locked mail is protected; a parcel with gold / items asks first (they are lost).</summary>
        private void Remove(ulong id)
        {
            ClientMail m = Find(id);
            Popups pop = ModernRoot.Instance?.Popups;
            if (m == null || m.Locked || _busy.Contains(id) || pop == null) return;
            bool parcel = IsParcel(m);
            pop.Ask(parcel ? W.T(ClientTextKeys.ParcelContainsItemsOrGoldDeleteConfirm) : W.T(ClientTextKeys.ModernEcoMailDeleteAsk, m.SenderName ?? string.Empty), ok =>
            {
                ClientMail now = Find(id);
                if (!ok || now == null || now.Locked || !IsOpen) return;
                _busy.Add(id);
                Network.Enqueue(new C.DeleteMail { MailID = id });
                _dirty = true;
            }, true, parcel ? PopupKind.Danger : PopupKind.Warning, W.T(ClientTextKeys.ModernEcoMailDeleteTitle));
        }

        private void Reply(ulong id)
        {
            ClientMail m = Find(id);
            if (m == null || !m.CanReply) return;
            string quote = "\r\n\r\n— " + W.T(ClientTextKeys.ModernEcoMailWrote, m.SenderName) + " —\r\n" + Body(m.Message).Replace("\n", "\r\n");
            if (quote.Length > MaxMessage - 20) quote = quote.Substring(0, MaxMessage - 20);
            ComposeTo(m.SenderName, quote);
            if (_body != null && !_body.Box.IsDisposed) _body.Box.TextBox.SelectionStart = 0;
        }

        private void RequestSent()
        {
            long now = CMain.Time;
            if (now < _sentTime)
            {
                _sentRetryAt = _sentTime; // too soon: ask once more when it allows
                return;
            }
            _sentTime = now + SentDelayMs;
            Network.Enqueue(new C.RequestSentMail());
        }

        private void HideSent(ulong id)
        {
            long now = CMain.Time;
            if (now < _hideTime) { Say(W.T(ClientTextKeys.ModernEcoTryAgain)); return; }
            int i = Model.Sent.FindIndex(s => s.MailID == id);
            if (i < 0) return;
            _hideTime = now + HideDelayMs;
            Model.Sent.RemoveAt(i); // gone at once; the server answers with a fresh S.SentMail
            Network.Enqueue(new C.HideSentMail { MailID = id });
            if (_pickedSent == id) _hasPickedSent = false;
            _dirty = true;
        }

        // ------------------------------------------------------------------ actions: composer

        private void ClearDraft(string to, string message)
        {
            if (_to == null) return;
            _to.Text = to ?? string.Empty;
            _body.Text = message ?? string.Empty;
            _err = string.Empty;
            _note = string.Empty;
        }

        private void Discard()
        {
            if (_mode == Mode.Parcel) ResetParcel();
            _mode = Mode.Read;
            _to?.Blur();
            _body?.Blur();
            _dirty = true;
        }

        /// <summary>StampParcel: needs a stamp in the bag; without one only the first cell goes, so cells 2-5 are handed back.</summary>
        private void SetStamp(bool on)
        {
            if (_mode != Mode.Parcel) return;
            if (on && StampCount() <= 0)
            {
                _stampSw.Set(false);
                _err = W.T(ClientTextKeys.ModernEcoMailNeedStamp);
                _dirty = true;
                return;
            }
            _stamped = on;
            if (!on) for (int i = 1; i < MailState.ParcelSlots; i++) Detach(i, false);
            _err = string.Empty;
            Quote();
        }

        /// <summary>GoldSendLabel: the amount box up to the gold in the bag.</summary>
        private void PickGold()
        {
            Popups pop = ModernRoot.Instance?.Popups;
            if (_mode != Mode.Parcel || pop == null) return;
            uint max = GameScene.Gold;
            if (max == 0 && _gold == 0) { _err = W.T(ClientTextKeys.ModernEcoMailNoGold); _dirty = true; return; }
            pop.Amount(W.T(ClientTextKeys.SendAmount), -1, max, v =>
            {
                if (_mode != Mode.Parcel) return;
                _gold = Math.Min(v, GameScene.Gold);
                Quote();
            }, Math.Min(_gold, max), 0);
        }

        /// <summary>Takes an item out of the parcel (ResetLockedCells for one cell: the server echo unlocks the bag cell).</summary>
        private void Detach(int i, bool requote)
        {
            UserItem it = Model.ParcelItems[i];
            if (it == null || _sending) return;
            Network.Enqueue(new C.MailLockedItem { UniqueID = it.UniqueID, Locked = false });
            ItemMoves.SetItem(MirGridType.Mail, i, null);
            Model.ParcelItemsIdx[i] = 0;
            if (requote) Quote();
            _dirty = true;
        }

        /// <summary>MailComposeParcelDialog.Reset: every parcel item is handed back, gold and stamp cleared.</summary>
        private void ResetParcel()
        {
            for (int i = 0; i < MailState.ParcelSlots; i++)
            {
                UserItem it = Model.ParcelItems[i];
                if (it != null)
                {
                    Network.Enqueue(new C.MailLockedItem { UniqueID = it.UniqueID, Locked = false });
                    ItemMoves.SetItem(MirGridType.Mail, i, null);
                }
                Model.ParcelItemsIdx[i] = 0;
            }
            ItemSlotRef pick = ItemMoves.Selected;
            if (pick != null && pick.GridType == MirGridType.Mail) ItemMoves.Selected = null;
            _gold = 0;
            _stamped = false;
            _cost = -1;
        }

        /// <summary>CalculatePostage: C.MailCost (S.MailCost answers into the quote).</summary>
        private void Quote()
        {
            if (_mode != Mode.Parcel) return;
            _cost = -1;
            Network.Enqueue(new C.MailCost { Gold = _gold, ItemsIdx = (ulong[])Model.ParcelItemsIdx.Clone(), Stamped = _stamped });
            _dirty = true;
        }

        private void Send()
        {
            string why = SendProblem();
            if (why.Length > 0) { _err = why; _dirty = true; return; }
            string to = _to.Text.Trim(), msg = Crlf(_body.Text);
            if (_mode == Mode.Parcel)
                Network.Enqueue(new C.SendMail { Name = to, Message = msg, Gold = _gold, ItemsIdx = (ulong[])Model.ParcelItemsIdx.Clone(), Stamped = _stamped });
            else
                Network.Enqueue(new C.SendMail { Name = to, Message = msg });
            _sending = true;
            _sendTo = to;
            _nextSend = CMain.Time + SendCooldownMs;
            _sendWaitUntil = _nextSend; // no S.MailSent for a chat-only refusal: Send comes back after the server's cooldown
            _err = string.Empty;
            if (_mode == Mode.Letter)
            {
                // a letter gets no S.MailSent unless the server takes the parcel path: the composer closes as the classic one does
                _sending = false;
                _mode = Mode.Read;
                ClearDraft(string.Empty, string.Empty);
                _note = W.T(ClientTextKeys.ModernEcoMailSentTo, to);
                if (_folder == Folder.Sent) RequestSent();
            }
            _dirty = true;
        }
    }
}

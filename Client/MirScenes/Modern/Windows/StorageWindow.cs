// Ported from WebClient/src/ui/modern/windows/storage.ts (StorageSession + StorageWindow): the new UI's NPC storage (bank)
// window. Opened only by the server (S.NPCStorage → ModernUi.NpcStorage), closed with the NPC session (a new NPC page).
// The lock screen sends C.UnlockStorage; a first password is forced (popups) before the storage opens; set / change
// (C.SetStoragePassword) and remove (C.RemoveStoragePassword) live in the side column's Protection card; closing the window
// re-locks it (StorageDialog.Hide). Pages of 80 (page 2 while the expanded storage is rented: Rent = C.Chat @ADDSTORAGE),
// search + category chips highlight in place, Sort / Auto-stack (C.SortItems through the pacer), your bag beside it.
// Desktop idiom: every cell is a ModernItemCell (click to pick / drop through ItemMoves); a click on a bag cell in the side
// column deposits it (a matching stack with room on this page, else this page's first free cell — ModernUi.ItemDeleteModeClick
// routes that click here); a double-click on a stored item uses it as the classic cell does (the web withdraws).
// HIDDEN (no data): "stored by", the activity log, "shared by". The narrow layout's "Your bag" tab is not needed at 1024+.
using Client.MirControls;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;
using S = ServerPackets;

namespace Client.MirScenes.Modern.Windows
{
    /// <summary>The storage view (storage.ts StorageView).</summary>
    public enum StorageView
    {
        Closed,
        Store,
        Lock,
        Setup,
    }

    /// <summary>
    /// StorageDialog's state outside the window (storage.ts StorageSession): whether the server opened the storage, the unlock,
    /// the forced first-password setup and the password prompts. One per modern UI (ModernUi.Storage), so the server can open the
    /// storage before the window exists. UI thread only.
    /// </summary>
    public sealed class StorageSession
    {
        private bool _active, _unlocked, _pendingOpenAfterPasswordSet, _forcingPasswordSetup;
        private string _error = string.Empty;
        private readonly Action _npcPage;

        public StorageSession()
        {
            _npcPage = End;
            GameScene.Models.Npc.PageChanged.On(_npcPage); // GameScene.NPCResponse (a new page from any NPC) hides the storage
        }

        public event Action Changed;

        public string Error => _error;
        public bool Active => _active;

        private static UserObject User => GameScene.User;

        public StorageView View
        {
            get
            {
                if (!_active || User == null) return StorageView.Closed;
                if (!User.RequireStoragePassword) return StorageView.Store;
                if (!User.HasStoragePassword) return StorageView.Setup;
                return _unlocked ? StorageView.Store : StorageView.Lock;
            }
        }

        private void Emit() => Changed?.Invoke();

        private static WindowManager Wm => ModernRoot.Instance?.Wm;

        /// <summary>S.NPCStorage (GameScene.NPCStorage).</summary>
        public void Opened()
        {
            _active = true;
            Show();
        }

        /// <summary>StorageDialog.Show: open straight away, force a first password, or open on the lock screen.</summary>
        private void Show()
        {
            if (!_active || User == null) return;
            if (User.RequireStoragePassword)
            {
                if (!User.HasStoragePassword)
                {
                    // ForceStoragePasswordSetup: the bag closes, the prompts ask, the storage opens on success
                    Wm?.Close(InventoryWindow.WindowKey);
                    _forcingPasswordSetup = _pendingOpenAfterPasswordSet = true;
                    SetPassword(true);
                    Emit();
                    return;
                }
            }
            else _unlocked = true;
            _error = string.Empty;
            Emit();
            Wm?.Open(StorageWindow.WindowKey); // locked: the window shows its lock screen (PromptStorageUnlock)
        }

        /// <summary>The window closed (StorageDialog.Hide): re-lock; the session ends unless the forced setup is still asking.</summary>
        public void WindowClosed()
        {
            _unlocked = false;
            if (!_forcingPasswordSetup) _active = false;
            ItemSlotRef pick = ItemMoves.Selected;
            if (pick != null && pick.GridType == MirGridType.Storage) ItemMoves.Selected = null;
        }

        /// <summary>The NPC session moved to another page: hide the storage.</summary>
        public void End()
        {
            _pendingOpenAfterPasswordSet = _forcingPasswordSetup = false;
            if (!_active && !(Wm?.IsOpen(StorageWindow.WindowKey) ?? false)) return;
            _active = false;
            _unlocked = false;
            Wm?.Close(StorageWindow.WindowKey);
            Emit();
        }

        public static string PasswordProblem(string v)
        {
            bool ok = v != null && v.Length >= Globals.MinPasswordLength && v.Length <= Globals.MaxPasswordLength;
            if (ok)
                for (int i = 0; i < v.Length; i++)
                    if (!char.IsAsciiLetterOrDigit(v[i])) { ok = false; break; }
            return ok ? string.Empty : Rules;
        }

        private static string Rules => W.T(ClientTextKeys.StoragePasswordRules, Globals.MinPasswordLength, Globals.MaxPasswordLength);

        /// <summary>PromptStorageUnlock's OK: C.UnlockStorage. Returns a refusal ("" = sent).</summary>
        public string Unlock(string password)
        {
            string why = PasswordProblem(password);
            if (why.Length > 0) return why;
            _error = string.Empty;
            Network.Enqueue(new C.UnlockStorage { Password = password });
            return string.Empty;
        }

        /// <summary>The Rent button: OK / Cancel, then C.Chat "@ADDSTORAGE" (answered by S.ResizeStorage).</summary>
        public void Rent()
        {
            Popups pop = ModernRoot.Instance?.Popups;
            if (pop == null || User == null) return;
            bool has = User.HasExpandedStorage;
            pop.Ask(W.T(has ? ClientTextKeys.ExtendYourRentalPeriod : ClientTextKeys.ExtraStorage), ok =>
            {
                if (ok && _active) Network.Enqueue(new C.Chat { Message = "@ADDSTORAGE" });
            }, true, PopupKind.Question, W.T(has ? ClientTextKeys.ModernWinStorageExtend : ClientTextKeys.ModernWinStorageRent));
        }

        /// <summary>The masked input with the length / character rules (PromptStoragePassword); null = cancelled.</summary>
        private void Ask(ClientTextKeys prompt, Action<string> done)
        {
            Popups pop = ModernRoot.Instance?.Popups;
            if (pop == null) { done(null); return; }
            pop.Input(W.T(ClientTextKeys.ModernWinStoragePassword), string.Empty, Globals.MaxPasswordLength, v =>
            {
                if (v == null) { done(null); return; }
                string why = PasswordProblem(v);
                if (why.Length > 0)
                {
                    System(why);
                    Ask(prompt, done); // ask again
                    return;
                }
                done(v);
            }, W.T(prompt) + "\n" + Rules, true);
        }

        /// <summary>BeginSetStoragePassword: new, then confirm, then C.SetStoragePassword. <paramref name="force"/> = the forced first
        /// setup (cancel closes the storage; a mismatch starts over).</summary>
        public void SetPassword(bool force = false)
        {
            Ask(ClientTextKeys.StoragePasswordNewPrompt, pw =>
            {
                if (pw == null) { if (force) CancelSetup(); return; }
                ConfirmNew(pw, force);
            });
        }

        private void ConfirmNew(string pw, bool force)
        {
            Ask(ClientTextKeys.StoragePasswordConfirmPrompt, again =>
            {
                if (again == null)
                {
                    if (force) CancelSetup();
                    else _pendingOpenAfterPasswordSet = false;
                    return;
                }
                if (again == pw)
                {
                    Network.Enqueue(new C.SetStoragePassword { CurrentPassword = string.Empty, NewPassword = pw });
                    return;
                }
                System(W.T(ClientTextKeys.StoragePasswordMismatch));
                if (force) SetPassword(true); // the forced setup asks for a new password again
                else ConfirmNew(pw, false);
            });
        }

        /// <summary>ManageStoragePassword with a password set: confirm (with the date it was set), then current, new, confirm.</summary>
        public void ChangePassword()
        {
            Popups pop = ModernRoot.Instance?.Popups;
            if (pop == null || User == null) return;
            string text = W.T(ClientTextKeys.StoragePasswordChangePrompt);
            if (User.StoragePasswordLastSet != DateTime.MinValue && User.StoragePasswordLastSet.Year > 1)
                text += "\n" + W.T(ClientTextKeys.StoragePasswordLastSetLabel, User.StoragePasswordLastSet.ToString("g"));
            pop.Ask(text, ok =>
            {
                if (!ok) return;
                Ask(ClientTextKeys.StoragePasswordCurrentPrompt, current =>
                {
                    if (current == null) return;
                    Ask(ClientTextKeys.StoragePasswordNewPrompt, pw =>
                    {
                        if (pw == null) return;
                        ConfirmChange(current, pw);
                    });
                });
            }, true, PopupKind.Question, W.T(ClientTextKeys.ModernWinStoragePassword));
        }

        private void ConfirmChange(string current, string pw)
        {
            Ask(ClientTextKeys.StoragePasswordConfirmPrompt, again =>
            {
                if (again == null) return;
                if (again != pw)
                {
                    System(W.T(ClientTextKeys.StoragePasswordMismatch));
                    ConfirmChange(current, pw);
                    return;
                }
                Network.Enqueue(new C.SetStoragePassword { CurrentPassword = current, NewPassword = pw });
            });
        }

        /// <summary>C.RemoveStoragePassword after the current password.</summary>
        public void RemovePassword()
        {
            Popups pop = ModernRoot.Instance?.Popups;
            if (pop == null || User == null) return;
            string text = W.T(ClientTextKeys.ModernWinStorageRemoveAsk) + (User.RequireStoragePassword ? "\n" + W.T(ClientTextKeys.ModernWinStorageRemoveRequired) : string.Empty);
            pop.Ask(text, ok =>
            {
                if (!ok) return;
                Ask(ClientTextKeys.StoragePasswordCurrentPrompt, current =>
                {
                    if (current == null) return;
                    if (User == null || !User.HasStoragePassword)
                    {
                        System(W.T(ClientTextKeys.StoragePasswordNoPasswordSet));
                        return;
                    }
                    Network.Enqueue(new C.RemoveStoragePassword { CurrentPassword = current });
                });
            }, true, PopupKind.Warning, W.T(ClientTextKeys.ModernWinStoragePassword));
        }

        /// <summary>CancelStoragePasswordSetup.</summary>
        private void CancelSetup()
        {
            _pendingOpenAfterPasswordSet = _forcingPasswordSetup = false;
            _active = false;
            Wm?.Close(StorageWindow.WindowKey);
            Emit();
        }

        /// <summary>StorageDialog.HandleStorageUnlockResult (the classic dialog also keeps HasPassword).</summary>
        public void UnlockResult(S.StorageUnlockResult p)
        {
            if (User != null) User.HasStoragePassword = p.HasPassword;
            switch (p.Result)
            {
                case 0:
                case 4:
                    _unlocked = true;
                    _error = string.Empty;
                    Show();
                    return;
                case 1: Refuse(W.T(ClientTextKeys.StoragePasswordNotAcceptable)); return;
                case 2: Refuse(W.T(ClientTextKeys.StoragePasswordWrong)); return;
                case 3: Refuse(W.T(ClientTextKeys.StoragePasswordUnavailable)); return;
            }
        }

        private void Refuse(string text)
        {
            _error = text;
            System(text);
            Emit();
        }

        /// <summary>StorageDialog.HandleStoragePasswordResult (the classic dialog also keeps HasPassword / LastSetTime).</summary>
        public void PasswordResult(S.StoragePasswordResult p)
        {
            bool hadPassword = User != null && User.HasStoragePassword;
            if (User != null)
            {
                User.HasStoragePassword = p.HasPassword;
                User.StoragePasswordLastSet = p.LastSetTime;
            }
            switch (p.Result)
            {
                case 0: System(W.T(ClientTextKeys.StoragePasswordUnavailable)); break;
                case 1:
                case 3: System(W.T(ClientTextKeys.StoragePasswordNotAcceptable)); break;
                case 2: System(W.T(ClientTextKeys.StoragePasswordWrong)); break;
                case 5: System(W.T(ClientTextKeys.StoragePasswordNoPasswordSet)); break;
                case 4:
                    _unlocked = true; // the server unlocks on set / change / remove
                    if (p.Removing)
                    {
                        _pendingOpenAfterPasswordSet = _forcingPasswordSetup = false;
                        System(W.T(ClientTextKeys.StoragePasswordRemoveSuccess));
                        _unlocked = false;
                        _active = false;
                        Wm?.Close(StorageWindow.WindowKey);
                        Emit();
                        return;
                    }
                    System(W.T(hadPassword ? ClientTextKeys.StoragePasswordChangeSuccess : ClientTextKeys.StoragePasswordSetSuccess));
                    if (_pendingOpenAfterPasswordSet)
                    {
                        _pendingOpenAfterPasswordSet = _forcingPasswordSetup = false;
                        Show();
                    }
                    else Emit();
                    return;
            }
            _pendingOpenAfterPasswordSet = _forcingPasswordSetup = false;
            Emit();
        }

        private static void System(string text) => GameScene.Models.Chat.Add(text, ChatType.System);

        public void Dispose()
        {
            GameScene.Models.Npc.PageChanged.Off(_npcPage);
            _active = _unlocked = false;
        }
    }

    public sealed class StorageWindow : ModernWindow
    {
        public const string WindowKey = "storage";
        private const int Page = 80, Cols = 10, Cell = 40, Gap = 4, WidthHud = 800, StoreX = 12, SideX = 472;

        private static readonly (string Name, ClientTextKeys Label)[] Cats =
        {
            ("All", ClientTextKeys.ModernWinCatAll), ("Weapons", ClientTextKeys.ModernWinCatWeapons), ("Armour", ClientTextKeys.ModernWinCatArmour),
            ("Jewellery", ClientTextKeys.ModernWinCatJewellery), ("Potions", ClientTextKeys.ModernWinCatPotions), ("Scrolls", ClientTextKeys.ModernWinCatScrolls),
            ("Materials", ClientTextKeys.ModernWinCatMaterials), ("Books", ClientTextKeys.ModernWinCatBooks), ("Other", ClientTextKeys.ModernWinCatOther),
        };

        private readonly StorageSession _session;
        private int _page;
        private string _query = string.Empty, _cat = "All", _note = string.Empty;
        private string _chipsKey = string.Empty;

        private ModernPanel _lockView, _mainView;
        private ModernLabel _lockTitle, _lockText, _lockErr, _used, _bagCount, _noteLabel, _protText;
        private ModernTextField _lockField;
        private ModernButton _lockBtn, _rentBtn, _lockStoreBtn, _sortBtn, _stackBtn;
        private ModernProgressBar _usedBar;
        private readonly PaintBox[] _pageBtns = new PaintBox[2];
        private readonly string[] _pageCount = new string[2], _pageSub = new string[2];
        private ModernPanel _chipsBox, _gridBox, _p2lock, _protRow;
        private PaintBox _protCard;
        private string _protKey = string.Empty;
        private ScrollView _bagView;
        private readonly List<ModernItemCell> _storeCells = new List<ModernItemCell>();
        private readonly List<CellDecor> _storeDecor = new List<CellDecor>();
        private readonly List<ModernItemCell> _bagCells = new List<ModernItemCell>();
        private int _bagBuilt = -1;

        private readonly GridWatch _storeWatch = new GridWatch(), _bagWatch = new GridWatch();
        private bool _dirty = true;
        private readonly Action _changed;
        private readonly Action<ItemSlotKey> _slotChanged;
        private readonly Action _resized;

        public StorageWindow(StorageSession session) : base(WindowKey, W.T(ClientTextKeys.ModernWinStorage), 32, 30)
        {
            _session = session;
            ContentWidth = WidthHud;
            _changed = () => _dirty = true;
            _slotChanged = k => { if (k.Grid == MirGridType.Storage || k.Grid == MirGridType.Inventory || k.Grid == MirGridType.None) _dirty = true; };
            _resized = () => { _dirty = true; _storeWatch.Reset(); };
        }

        private static UserObject User => GameScene.User;
        private static UserItem[] Store => GameScene.Storage;
        private int Belt => User?.BeltIdx ?? UserObject.BeltSize;

        internal StorageView ViewNow => _session.View;
        internal int PageShown => _page;
        internal IReadOnlyList<ModernItemCell> StoreCells => _storeCells;
        internal IReadOnlyList<ModernItemCell> BagCells => _bagCells;
        internal ModernTextField LockField => _lockField;
        internal ModernButton LockButton => _lockBtn;
        internal string Note => _note;

        protected override void Build()
        {
            const int top = TitleHeight;
            int h = 540;

            // ---- lock screen
            _lockView = new ModernPanel { Parent = this, Location = new Point(0, S(top)), Size = new Size(S(WidthHud), S(h)) };
            int cw = 340, ch = 236, cx = (WidthHud - cw) / 2, cy = (h - ch) / 2;
            new PaintBox((g, b) => W.Card(g, b.Size)) { Parent = _lockView, Location = new Point(S(cx), S(cy)), Size = new Size(S(cw), S(ch)), NotControl = true };
            new IconView(ModernIcons.Lock, 46, ModernTheme.Accent) { Parent = _lockView, Location = new Point(S(cx + (cw - 46) / 2), S(cy + 18)) };
            _lockTitle = W.Label(_lockView, string.Empty, cx, cy + 70, cw, 22, 17, true, Color.White, StringAlignment.Center);
            _lockText = W.Label(_lockView, string.Empty, cx + 20, cy + 94, cw - 40, 46, 12, false, ModernTheme.Dim, StringAlignment.Center, true);
            _lockField = W.At(new ModernTextField(cw - 48, 32, 14, 10, 10, true) { Password = true, MaxLength = Globals.MaxPasswordLength }, _lockView, cx + 24, cy + 144);
            _lockField.Submitted += _ => SubmitUnlock();
            _lockBtn = W.At(new ModernButton(W.T(ClientTextKeys.ModernWinUnlock), ButtonKind.Main, true), _lockView, cx + 24, cy + 182);
            _lockBtn.Size = new Size(S(cw - 48), _lockBtn.Size.Height);
            _lockBtn.Click += (o, e) => SubmitUnlock();
            _lockErr = W.Label(_lockView, string.Empty, cx, cy + 214, cw, 16, 11, false, ModernTheme.BadSoft, StringAlignment.Center);

            // ---- the store
            _mainView = new ModernPanel { Parent = this, Location = new Point(0, S(top)), Size = new Size(S(WidthHud), S(h)) };
            MirControl m = _mainView;
            W.Label(m, W.T(ClientTextKeys.ModernWinAccountStorage), 14, 9, 220, 20, 15, true, Color.White);
            W.Label(m, W.T(ClientTextKeys.ModernWinAccountStorageSub), 14, 29, 220, 14, 11, false, ModernTheme.Dim);
            W.Label(m, W.T(ClientTextKeys.ModernWinUsed), 248, 12, 60, 12, 10, false, ModernTheme.Dim);
            _used = W.Label(m, string.Empty, 298, 12, 100, 12, 10, true, Color.Empty, StringAlignment.Far);
            _usedBar = W.At(new ModernProgressBar(150, 6), m, 248, 28);
            for (int i = 0; i < 2; i++)
            {
                int page = i;
                _pageBtns[i] = new PaintBox((g, b) => PaintPage(g, b, page)) { Parent = m, Location = new Point(S(408 + i * 104), S(10)), Size = new Size(S(104), S(30)) };
                _pageBtns[i].Click += (o, e) => { if (_page != page) { _page = page; _dirty = true; RePoint(); } };
                _pageCount[i] = _pageSub[i] = string.Empty;
            }
            _lockStoreBtn = new ModernButton(W.T(ClientTextKeys.ModernWinLock)) { Parent = m, Tooltip = W.T(ClientTextKeys.ModernWinLockHint) };
            _lockStoreBtn.AutoWidth(60);
            _lockStoreBtn.Location = new Point(S(WidthHud - 14) - _lockStoreBtn.Size.Width, S(14));
            _lockStoreBtn.Click += (o, e) => Close();
            _rentBtn = new ModernButton(W.T(ClientTextKeys.ModernWinRentPage2)) { Parent = m, Tooltip = W.T(ClientTextKeys.ModernWinRentHint) };
            _rentBtn.AutoWidth(100);
            _rentBtn.Location = new Point(_lockStoreBtn.Location.X - S(6) - _rentBtn.Size.Width, S(14));
            _rentBtn.Click += (o, e) => _session.Rent();
            W.Divider(m, 0, 50, WidthHud);

            // store column: search, sort / stack, chips, the page's 80 cells (or the rent panel), the hint
            var search = W.At(new SearchBox(190, W.T(ClientTextKeys.ModernWinFindItem)), m, StoreX, 60);
            if (_query.Length > 0) search.Text = _query;
            search.Search += q => { _query = q.ToLowerInvariant(); PaintFilter(); };
            _sortBtn = new ModernButton(W.T(ClientTextKeys.ModernWinSort)) { Parent = m, Tooltip = W.T(ClientTextKeys.ModernWinStorageSortHint) };
            _sortBtn.AutoWidth(56);
            _sortBtn.Location = new Point(S(StoreX + 196), S(61));
            _sortBtn.Click += (o, e) => SortStore(false);
            _stackBtn = new ModernButton(W.T(ClientTextKeys.ModernWinAutoStack)) { Parent = m, Tooltip = W.T(ClientTextKeys.ModernWinAutoStackHint) };
            _stackBtn.AutoWidth(76);
            _stackBtn.Location = new Point(_sortBtn.Location.X + _sortBtn.Size.Width + S(6), S(61));
            _stackBtn.Click += (o, e) => SortStore(true);
            _chipsBox = new ModernPanel { Parent = m, Location = new Point(S(StoreX), S(92)), Size = new Size(S(Cols * (Cell + Gap) - Gap), S(44)) };
            _chipsKey = string.Empty;

            int gridTop = 142, gridW = Cols * (Cell + Gap) - Gap, gridH = 8 * (Cell + Gap) - Gap;
            _gridBox = new ModernPanel { Parent = m, Location = new Point(S(StoreX), S(gridTop)), Size = new Size(S(gridW), S(gridH)) };
            _storeCells.Clear();
            _storeDecor.Clear();
            for (int i = 0; i < Page; i++)
            {
                var c = new ModernItemCell(MirGridType.Storage, _page * Page + i, Cell) { Parent = _gridBox, Location = new Point(S(i % Cols * (Cell + Gap)), S(i / Cols * (Cell + Gap))) };
                _storeCells.Add(c);
                _storeDecor.Add(new CellDecor(c));
            }
            _p2lock = new ModernPanel { Parent = m, Location = _gridBox.Location, Size = _gridBox.Size, Visible = false };
            new PaintBox((g, b) => W.Card(g, b.Size, W.Over(ModernTheme.Frame, 0.8f), 0f, true)) { Parent = _p2lock, Size = _p2lock.Size, NotControl = true };
            new IconView(ModernIcons.Lock, 34, ModernTheme.Accent) { Parent = _p2lock, Location = new Point(S((gridW - 34) / 2), S(110)) };
            W.Label(_p2lock, W.T(ClientTextKeys.ModernWinExpandedLocked), 0, 152, gridW, 20, 14, true, Color.White, StringAlignment.Center);
            W.Label(_p2lock, W.T(ClientTextKeys.ModernWinExpandedLockedText), 0, 174, gridW, 16, 12, false, ModernTheme.Dim, StringAlignment.Center);
            var rent2 = new ModernButton(W.T(ClientTextKeys.ModernWinRentExtra), ButtonKind.Main) { Parent = _p2lock };
            rent2.AutoWidth(120);
            rent2.Location = new Point((_p2lock.Size.Width - rent2.Size.Width) / 2, S(200));
            rent2.Click += (o, e) => _session.Rent();
            W.Label(m, W.T(ClientTextKeys.ModernWinStorageHint), StoreX, gridTop + gridH + 8, gridW, 28, 10, false, ModernTheme.Dim, StringAlignment.Near, true);

            // side column: your bag (click to deposit), the note, password protection
            W.VDivider(m, SideX - 12, 51, h - 51);
            int sw = WidthHud - SideX - 12;
            W.Caption(m, W.T(ClientTextKeys.ModernWinYourBag), SideX, 60, 120);
            _bagCount = W.Label(m, string.Empty, SideX + 120, 60, sw - 120, 14, 11, true, Color.Empty, StringAlignment.Far);
            _bagView = new ScrollView(6 * (Cell + Gap) + 10, 268) { Parent = m, Location = new Point(S(SideX), S(80)), Background = W.Page };
            _bagView.Framed = false;
            _bagCells.Clear();
            _bagBuilt = -1;
            _noteLabel = W.Label(m, string.Empty, SideX, 356, sw, 16, 11, false, ModernTheme.Accent);
            _protCard = new PaintBox((g, b) => W.Card(g, b.Size)) { Parent = m, Location = new Point(S(SideX), S(380)), Size = new Size(S(sw), S(92)), NotControl = true };
            W.Caption(_protCard, W.T(ClientTextKeys.ModernWinProtection), 12, 10, sw - 24);
            _protText = W.Label(_protCard, string.Empty, 12, 28, sw - 24, 28, 11, false, ModernTheme.Dim, StringAlignment.Near, true);
            _protRow = new ModernPanel { Parent = _protCard, Location = new Point(S(12), S(60)), Size = new Size(S(sw - 24), S(24)) };
            _protKey = string.Empty;

            ContentHeight = h;
            _storeWatch.Reset();
            _bagWatch.Reset();
            _dirty = true;
            Render();
        }

        private void PaintPage(Graphics g, PaintBox b, int page)
        {
            float k = ModernRoot.K;
            bool on = page == _page;
            RectangleF r = new RectangleF(0.5f, 0.5f, b.Size.Width - 1f, b.Size.Height - 1f);
            using (var br = new SolidBrush(on ? W.Over(ModernTheme.Accent, 0.26f) : W.Over(ModernTheme.PanelB, 0.6f)))
                g.FillRectangle(br, r);
            if (on)
                using (var br = new SolidBrush(ModernTheme.Accent))
                    g.FillRectangle(br, r.X, r.Bottom - 2 * k, r.Width, 2 * k);
            using (var pen = new Pen(ModernTheme.Frame, 1f))
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            Color text = on || b.Hovered ? Color.White : ModernTheme.Dim;
            ModernSkin.Text(g, W.T(ClientTextKeys.ModernWinPageN, page + 1) + " · " + _pageCount[page], ModernSkin.Font(10.5f, true), text, new RectangleF(10 * k, 2 * k, r.Width - 14 * k, 14 * k));
            bool bad = page == 1 && !(User?.HasExpandedStorage ?? false);
            ModernSkin.Text(g, _pageSub[page], ModernSkin.Font(8.5f), bad ? ModernTheme.BadSoft : ModernTheme.AccentDim, new RectangleF(10 * k, 16 * k, r.Width - 14 * k, 12 * k));
        }

        // ------------------------------------------------------------------ lifecycle

        protected override void Shown()
        {
            _session.Changed += _changed;
            ItemMoves.SlotChanged.On(_slotChanged);
            ModernEvents.StorageResized.On(_resized);
            ModernEvents.InventoryResized.On(_resized);
            _note = string.Empty;
            _dirty = true;
            Render();
        }

        protected override void Closed()
        {
            _session.Changed -= _changed;
            ItemMoves.SlotChanged.Off(_slotChanged);
            ModernEvents.StorageResized.Off(_resized);
            ModernEvents.InventoryResized.Off(_resized);
            if (_lockField != null) _lockField.Text = string.Empty; // nothing typed is kept
            _session.WindowClosed();
        }

        public override void Process()
        {
            base.Process();
            if (User == null) return;
            if (_storeWatch.Poll(MirGridType.Storage, Store) | _bagWatch.Poll(MirGridType.Inventory, User.Inventory, Belt))
            {
                for (int i = 0; i < _storeCells.Count; i++) _storeCells[i].Refresh(false);
                for (int i = 0; i < _bagCells.Count; i++) _bagCells[i].Refresh(false);
                _dirty = true;
            }
            if (_dirty) Render();
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            _dirty = false;
            if (_mainView == null || _mainView.IsDisposed || User == null) return;
            StorageView view = _session.View;
            _lockView.Visible = view != StorageView.Store;
            _mainView.Visible = view == StorageView.Store;
            if (view != StorageView.Store)
            {
                RenderLock(view);
                return;
            }
            UserObject u = User;
            UserItem[] store = Store ?? Array.Empty<UserItem>();
            bool expanded = u.HasExpandedStorage;
            int used0 = 0, used1 = 0;
            for (int i = 0; i < store.Length && i < Page * 2; i++)
                if (store[i] != null) { if (i < Page) used0++; else used1++; }
            int total = expanded ? Page * 2 : Page;
            _used.Text = (used0 + used1) + " / " + total;
            _usedBar.Set(Math.Min(1f, (used0 + used1) / (float)total));
            SetPageText(0, used0 + "/" + Page, string.Empty);
            if (expanded)
            {
                DateTime exp = u.ExpandedStorageExpiryTime;
                int days = Math.Max(0, (int)Math.Ceiling((exp - CMain.Now).TotalDays));
                SetPageText(1, used1 + "/" + Page, W.T(ClientTextKeys.ModernWinExpiresIn, exp.ToString("g"), days));
            }
            else SetPageText(1, W.T(ClientTextKeys.ModernWinLocked), W.T(ClientTextKeys.ModernWinNotRented));
            _rentBtn.Text = W.T(expanded ? ClientTextKeys.ModernWinExtendRental : ClientTextKeys.ModernWinRentPage2);
            _lockStoreBtn.Visible = u.RequireStoragePassword && u.HasStoragePassword;

            bool locked = _page == 1 && (!expanded || store.Length <= Page);
            _gridBox.Visible = !locked;
            _p2lock.Visible = locked;
            string why = locked ? W.T(ClientTextKeys.ModernWinRentFirst) : string.Empty;
            _sortBtn.SetDisabled(locked, why);
            _stackBtn.SetDisabled(locked, why);
            for (int i = 0; i < _storeDecor.Count; i++) _storeDecor[i].Sync();
            RenderChips();
            PaintFilter();
            RenderBag();
            RenderProtection();
            _noteLabel.Text = _note;
        }

        private void SetPageText(int i, string count, string sub)
        {
            if (_pageCount[i] == count && _pageSub[i] == sub) return;
            _pageCount[i] = count;
            _pageSub[i] = sub;
            _pageBtns[i].Invalidate();
        }

        private void RePoint()
        {
            int baseSlot = _page * Page;
            for (int i = 0; i < _storeCells.Count; i++) _storeCells[i].SetSlot(MirGridType.Storage, baseSlot + i);
            for (int i = 0; i < 2; i++) _pageBtns[i].Invalidate();
            _chipsKey = string.Empty;
            Render();
        }

        private void RenderLock(StorageView view)
        {
            UserObject u = User;
            if (view == StorageView.Closed)
            {
                _lockTitle.Text = W.T(ClientTextKeys.ModernWinStorageClosed);
                _lockText.Text = W.T(ClientTextKeys.ModernWinStorageClosedText);
            }
            else if (view == StorageView.Setup)
            {
                _lockTitle.Text = W.T(ClientTextKeys.ModernWinStorageSetup);
                _lockText.Text = W.T(ClientTextKeys.ModernWinStorageSetupText);
            }
            else
            {
                _lockTitle.Text = W.T(ClientTextKeys.ModernWinStorageLocked);
                string text = W.T(ClientTextKeys.StoragePasswordPrompt) + " " + W.T(ClientTextKeys.ModernWinStorageLockedText);
                if (u != null && u.StoragePasswordLastSet.Year > 1) text += "\n" + W.T(ClientTextKeys.StoragePasswordLastSetLabel, u.StoragePasswordLastSet.ToString("g"));
                _lockText.Text = text;
            }
            bool form = view == StorageView.Lock;
            _lockField.Visible = form;
            _lockBtn.Visible = form;
            _lockErr.Text = form ? _session.Error : string.Empty;
        }

        private void SubmitUnlock()
        {
            string why = _session.Unlock(_lockField.Text);
            _lockField.Text = string.Empty;
            _lockErr.Text = why;
        }

        private static string CategoryOf(ItemInfo info)
        {
            if (info == null) return "Other";
            switch (info.Type)
            {
                case ItemType.Weapon: return "Weapons";
                case ItemType.Armour:
                case ItemType.Helmet:
                case ItemType.Boots:
                case ItemType.Belt: return "Armour";
                case ItemType.Necklace:
                case ItemType.Bracelet:
                case ItemType.Ring: return "Jewellery";
                case ItemType.Potion: return "Potions";
                case ItemType.Scroll: return "Scrolls";
                case ItemType.Gem:
                case ItemType.Ore:
                case ItemType.CraftingMaterial:
                case ItemType.Meat: return "Materials";
                case ItemType.Book: return "Books";
            }
            return "Other";
        }

        /// <summary>Category chips with this page's counts (rebuilt only when a count changed).</summary>
        private void RenderChips()
        {
            UserItem[] store = Store ?? Array.Empty<UserItem>();
            int[] counts = new int[Cats.Length];
            int baseSlot = _page * Page;
            for (int i = baseSlot; i < baseSlot + Page && i < store.Length; i++)
            {
                if (store[i] == null) continue;
                string c = CategoryOf(store[i].Info);
                counts[0]++;
                for (int j = 1; j < Cats.Length; j++) if (Cats[j].Name == c) counts[j]++;
            }
            string key = _cat + ":" + string.Join(",", counts);
            if (key == _chipsKey) return;
            _chipsKey = key;
            for (int i = _chipsBox.Controls.Count - 1; i >= 0; i--) _chipsBox.Controls[i].Dispose();
            var choices = new List<Choice<string>>();
            for (int j = 0; j < Cats.Length; j++) choices.Add(new Choice<string>(Cats[j].Name, W.T(Cats[j].Label) + " " + counts[j]));
            var chips = new Chips<string>(choices, new[] { _cat }, Cols * (Cell + Gap) - Gap, false, false) { Parent = _chipsBox, Location = Point.Empty };
            chips.Changed += sel =>
            {
                foreach (string s in sel)
                {
                    if (s == _cat) return;
                    _cat = s;
                    _chipsKey = string.Empty;
                    _dirty = true;
                    return;
                }
            };
        }

        /// <summary>Search / category: matching cells light, the rest dim (slots never move).</summary>
        private void PaintFilter()
        {
            bool filtering = _query.Length > 0 || _cat != "All";
            for (int i = 0; i < _storeCells.Count; i++)
            {
                UserItem item = _storeCells[i].Item;
                int mode = 0;
                if (item != null && filtering)
                {
                    string cat = CategoryOf(item.Info);
                    bool ok = (_cat == "All" || cat == _cat) &&
                        (_query.Length == 0 || (item.Info?.Name ?? string.Empty).ToLowerInvariant().Contains(_query) || cat.ToLowerInvariant().Contains(_query));
                    mode = ok ? 1 : 2;
                }
                _storeDecor[i].SetMode(mode);
            }
        }

        /// <summary>The bag beside the storage; cells rebuilt only when the bag size changed.</summary>
        private void RenderBag()
        {
            UserItem[] inv = User.Inventory;
            int n = Math.Max(0, inv.Length - Belt);
            if (_bagBuilt != n)
            {
                for (int i = 0; i < _bagCells.Count; i++) _bagCells[i].Dispose();
                _bagCells.Clear();
                for (int i = 0; i < n; i++)
                    _bagCells.Add(new ModernItemCell(MirGridType.Inventory, Belt + i, Cell) { Parent = _bagView.Content, Location = new Point(S(i % 6 * (Cell + Gap)), S(i / 6 * (Cell + Gap))) });
                _bagView.FitContent(0);
                _bagBuilt = n;
            }
            int usedBag = 0;
            for (int i = Belt; i < inv.Length; i++) if (inv[i] != null) usedBag++;
            _bagCount.Text = usedBag + " / " + n;
        }

        private void RenderProtection()
        {
            UserObject u = User;
            bool show = u.RequireStoragePassword || u.HasStoragePassword;
            _protCard.Visible = show;
            if (!show) return;
            _protText.Text = u.HasStoragePassword
                ? (u.StoragePasswordLastSet.Year > 1 ? W.T(ClientTextKeys.ModernWinPasswordSetOn, u.StoragePasswordLastSet.ToString("g")) : W.T(ClientTextKeys.ModernWinPasswordSet))
                : W.T(ClientTextKeys.StoragePasswordNoPasswordSet);
            string key = u.HasStoragePassword + "|" + u.RequireStoragePassword;
            if (key == _protKey) return;
            _protKey = key;
            for (int i = _protRow.Controls.Count - 1; i >= 0; i--) _protRow.Controls[i].Dispose();
            int x = 0;
            void Add(ClientTextKeys text, Action run)
            {
                var b = new ModernButton(W.T(text)) { Parent = _protRow, Location = new Point(S(x), 0) };
                b.AutoWidth(64);
                b.Click += (o, e) => run();
                x = W.Right(b) + 6;
            }
            if (!u.HasStoragePassword)
            {
                if (u.RequireStoragePassword) Add(ClientTextKeys.ModernWinSetPassword, () => _session.SetPassword());
            }
            else
            {
                if (u.RequireStoragePassword) Add(ClientTextKeys.ModernWinChange, _session.ChangePassword);
                Add(ClientTextKeys.ModernWinRemove, _session.RemovePassword);
            }
        }

        // ------------------------------------------------------------------ actions (nothing moves until the server answers)

        private void Say(string text)
        {
            _note = text ?? string.Empty;
            if (_noteLabel != null && !_noteLabel.IsDisposed) _noteLabel.Text = _note;
        }

        /// <summary>A left click on a bag cell of the side column: deposit (ModernUi.ItemDeleteModeClick). True = handled.</summary>
        internal bool SideClick(ItemSlotRef cell, MouseButtons button)
        {
            if (!IsOpen || button != MouseButtons.Left || _session.View != StorageView.Store || ItemMoves.Selected != null) return false;
            if (cell.GridType != MirGridType.Inventory || !(MouseControl is ModernItemCell mc) || !_bagCells.Contains(mc)) return false;
            if (cell.Item == null || cell.Locked || CMain.Shift || CMain.Ctrl || CMain.Alt) return false;
            Deposit(cell.ItemSlot);
            return true;
        }

        /// <summary>storage.ts deposit: a matching stack with room on this page, else this page's first free cell; the drop runs the
        /// desktop route (ItemMoves: store / merge, the occupied-cell redirect, the refusals).</summary>
        private void Deposit(int bagSlot)
        {
            UserItem item = User.Inventory[bagSlot];
            UserItem[] store = Store;
            if (item?.Info == null || store == null) return;
            if (item.Info.Bind.HasFlag(BindMode.DontStore))
            {
                GameScene.Models.Chat.Add(W.T(ClientTextKeys.ModernWinCantStore, item.FriendlyName), ChatType.Hint);
                return;
            }
            if (_page == 1 && !User.HasExpandedStorage) { Say(W.T(ClientTextKeys.ModernWinRentSecondFirst)); return; }
            int baseSlot = _page * Page, end = Math.Min(baseSlot + Page, store.Length), to = -1;
            for (int i = baseSlot; i < end && to < 0; i++)
            {
                UserItem s = store[i];
                if (s != null && s.Info == item.Info && item.Info.StackSize > 1 && s.Count < item.Info.StackSize) to = i;
            }
            for (int i = baseSlot; i < end && to < 0; i++) if (store[i] == null) to = i;
            if (to < 0) to = baseSlot; // full page: the route sends it to the first free cell elsewhere, or refuses
            ItemMoves.Selected = new ItemSlotRef(MirGridType.Inventory, bagSlot);
            ItemMoves.Click(new ItemSlotRef(MirGridType.Storage, to), new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
            if (ItemMoves.Selected != null && ItemMoves.Selected.GridType == MirGridType.Inventory && ItemMoves.Selected.ItemSlot == bagSlot) ItemMoves.Selected = null;
            Say(W.T(ClientTextKeys.ModernWinStored, item.FriendlyName));
        }

        private void SortStore(bool stack)
        {
            if (SortGate.TrySort(MirGridType.Storage, Store, stack)) Say(W.T(stack ? ClientTextKeys.ModernWinStacking : ClientTextKeys.ModernWinSorting));
            else Say(W.T(ClientTextKeys.ModernWinSortRefused));
        }
    }
}

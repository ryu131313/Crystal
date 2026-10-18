using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Windows;
using S = ServerPackets;

namespace Client.MirScenes.Modern
{
    /// <summary>
    /// Phase 3 area "upgrades / rental" (worker B): refine (+ the refine check), awaken, sockets & gems, item rental. Windows live
    /// in Client\MirScenes\Modern\Windows\ (Refine*, Awaken*, Socket*, Rent*); their sessions are kept here so the server can
    /// answer while a window is closed. NPC-gated pages open only while the NPC dialog is open (the classic NPCDialog.Visible
    /// gates), and close with the NPC page (Models.Npc.PageChanged = GameScene.NPCResponse, as ClassicNpcResponse hides them).
    /// </summary>
    public sealed partial class ModernUi
    {
        internal RefineSession Refine { get; private set; }
        internal AwakenSession Awaken { get; private set; }
        internal RentSession Rent { get; private set; }
        private Action _upgradeNpcPage;

        partial void RegisterUpgradeWindows()
        {
            DisposeUpgrade();
            Refine = new RefineSession();
            Awaken = new AwakenSession();
            Rent = new RentSession();
            RefineSession refine = Refine;
            AwakenSession awaken = Awaken;
            RentSession rent = Rent;
            Root.Wm.Register(RefineWindow.WindowKey, () => new RefineWindow(refine));
            Root.Wm.Register(AwakenWindow.WindowKey, () => new AwakenWindow(awaken));
            Root.Wm.Register(SocketWindow.WindowKey, () => new SocketWindow());
            Root.Wm.Register(RentWindow.WindowKey, () => new RentWindow(rent));
            _upgradeNpcPage = () =>
            {
                // GameScene.NPCResponse → ClassicNpcResponse hides NPCDropDialog, NPCAwakeDialog and RefineDialog
                Root?.Wm.Close(RefineWindow.WindowKey);
                Root?.Wm.Close(AwakenWindow.WindowKey);
            };
            GameScene.Models.Npc.PageChanged.On(_upgradeNpcPage);
        }

        /// <summary>Called by Dispose through the sessions' owners; safe to call twice.</summary>
        private void DisposeUpgrade()
        {
            if (_upgradeNpcPage != null) GameScene.Models?.Npc.PageChanged.Off(_upgradeNpcPage);
            _upgradeNpcPage = null;
            Refine?.Dispose();
            Awaken?.Dispose();
        }

        private partial bool UpgradeKeyBind(KeybindOptions function)
        {
            switch (function)
            {
                case KeybindOptions.Rental:
                    Root.Wm.Toggle(RentWindow.WindowKey);
                    return true;
            }
            return false;
        }

        private T UpgradeWindow<T>(string key) where T : ModernWindow => Root == null || Root.IsDisposed ? null : Root.Wm.Get(key) as T;

        // ---- refine

        /// <summary>S.NPCRefine (ClassicNpcRefine): Refining = the smith took it (the NPC dialog closes), else the refine page opens.</summary>
        public void NpcRefine(bool refining)
        {
            if (Root == null || Refine == null || !IsNpcDialogOpen()) return;
            if (refining)
            {
                Root.Wm.Close(RefineWindow.WindowKey);
                CloseNpcDialog();
                return;
            }
            Refine.Open(RefineMode.Refine);
            Root.Wm.Open(RefineWindow.WindowKey);
        }

        /// <summary>
        /// The refine check page ([@REFINECHECK]: S.NPCCheckRefine → NpcDropPanel(PanelType.CheckRefine), area A's member). Returns
        /// true when it opened this area's refine window in check mode; area A's NpcDropPanel may call it first and return on true
        /// (integrator hook — until then the generic drop panel handles CheckRefine).
        /// </summary>
        internal bool UpgradeDropPanel(PanelType type)
        {
            if (type != PanelType.CheckRefine || Root == null || Refine == null || !IsNpcDialogOpen()) return false;
            Refine.Open(RefineMode.Check);
            Root.Wm.Open(RefineWindow.WindowKey);
            return true;
        }

        /// <summary>S.NPCCollectRefine (ClassicNpcCollectRefine): the item comes back with S.GainedItem; the NPC dialog closes.</summary>
        public void NpcCollectRefine()
        {
            if (!IsNpcDialogOpen()) return;
            CloseNpcDialog();
        }

        /// <summary>S.ItemUpgraded (after GameScene updated the item): the refine test's result, the gem bench's pending combine.
        /// (The classic grid flash has no modern counterpart yet; the tooltip re-reads the item on its next hover.)</summary>
        public void ItemUpgraded(MirGridType grid, UserItem item)
        {
            Refine?.Upgraded(item);
            UpgradeWindow<SocketWindow>(SocketWindow.WindowKey)?.Upgraded(item);
            ModernEvents.ItemInfo.Emit(item);
        }

        // ---- awaken

        /// <summary>S.NPCAwakening (ClassicNpcAwakening): the awakening window (and the bag).</summary>
        public void NpcAwakening()
        {
            if (Root == null || Awaken == null) return;
            Root.Wm.Open(AwakenWindow.WindowKey);
        }

        public void AwakeningNeedMaterials(S.AwakeningNeedMaterials p) => Awaken?.NeedMaterials(p);

        /// <summary>ItemMoves put / took the base item (NPCAwakeDialog.ItemCell_Click).</summary>
        public void ItemAwakeItemChanged() => Awaken?.ItemChanged();

        /// <summary>ItemMoves put a new base item (NPCAwakeDialog.OnAwakeTypeSelect(0)).</summary>
        public void ItemAwakeTypeSelect(int index) => Awaken?.TypeSelect(index);

        /// <summary>S.Awakening arrived (ClassicItemAwakeningClosing): the window closes, which hands the cells back.</summary>
        public void ItemAwakeningClosing() => Root?.Wm.Close(AwakenWindow.WindowKey);

        public void ItemAwakeningResult(int result) => Awaken?.Result(result);

        // ---- sockets

        /// <summary>Ctrl + right-click on an item (ItemMoves.OpenItem → SocketDialog.Show): the socket bench for an item with sockets.</summary>
        public void ItemShowSockets(MirGridType grid, UserItem item)
        {
            if (Root == null || item == null) return;
            if (item.Slots == null || item.Slots.Length == 0)
            {
                GameScene.SelectedItem = null;
                Root.Wm.Close(SocketWindow.WindowKey);
                return;
            }
            GameScene.SelectedItem = item;
            ItemMoves.GridChanged(MirGridType.Socket);
            Root.Wm.Open(SocketWindow.WindowKey);
            UpgradeWindow<SocketWindow>(SocketWindow.WindowKey)?.ShowSockets();
        }

        /// <summary>The first free socket of the open item (ClassicItemFreeSocketSlot: the first visible empty socket cell), or -1.</summary>
        public int ItemFreeSocketSlot()
        {
            if (Root == null || !Root.Wm.IsOpen(SocketWindow.WindowKey)) return -1;
            return UpgradeWindow<SocketWindow>(SocketWindow.WindowKey)?.FreeSocket() ?? -1;
        }

        // ---- item rental

        public void RentedItems(S.GetRentedItems p) => Rent?.List(p.RentedItems);

        /// <summary>S.ItemRentalRequest (ClassicItemRentalRequest): a deal opens on the rental window's Deal tab, with the bag.</summary>
        public void ItemRentalRequest(S.ItemRentalRequest p)
        {
            if (Root == null || Rent == null) return;
            Rent.Opened(p);

            UpgradeWindow<RentWindow>(RentWindow.WindowKey)?.ShowDeal();
            Root.Wm.Open(RentWindow.WindowKey);
            Root.Wm.Open(InventoryWindow.WindowKey);
        }

        /// <summary>S.ItemRentalFee: the partner added this much (the server sends the increment).</summary>
        public void ItemRentalFee(S.ItemRentalFee p)
        {
            if (Rent == null) return;
            Rent.GuestFee += p.Amount;
            Rent.Emit();
        }

        public void ItemRentalPeriod(S.ItemRentalPeriod p)
        {
            if (Rent == null) return;
            Rent.GuestPeriod = p.Days;
            Rent.Emit();
        }

        /// <summary>S.UpdateRentalItem (Models.Rental already changed and emitted).</summary>
        public void RentalGuestItemChanged() => Rent?.Emit();

        /// <summary>
        /// S.CancelItemRental / S.ConfirmItemRental (ClassicItemRentalReset): the deal data resets like ItemRentingDialog.Reset /
        /// ItemRentDialog.Reset (Models.Rental items, User.RentalGoldAmount, the period); a cancel we did not ask for shows the
        /// face-the-other-party box.
        /// </summary>
        public void ItemRentalReset(bool cancelled)
        {
            if (Rent == null) return;
            bool self = Rent.SelfCancel;
            Rent.SelfCancel = false;
            Rent.Reset(cancelled);
            if (cancelled && !self)
                Root?.Popups.Confirm(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ItemRentalCancelledFaceOtherParty), PopupButtons.Ok, PopupKind.Warning, null, false,
                    GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernUpRentTitle));
        }

        /// <summary>S.ItemRentalLock (GameScene set User.RentalGoldLocked / RentalItemLocked).</summary>
        public void ItemRentalLocked() => Rent?.Emit();

        public void ItemRentalPartnerLock(S.ItemRentalPartnerLock p)
        {
            if (Rent == null) return;
            if (p.GoldLocked) Rent.GuestGoldLocked = true;
            if (p.ItemLocked) Rent.GuestItemLocked = true;
            Rent.Emit();
        }

        public void CanConfirmItemRental()
        {
            if (Rent == null) return;
            Rent.CanConfirm = true;
            Rent.Emit();
        }

        /// <summary>S.DepositRentalItem succeeded (ClassicItemRentalDeposited): with no period yet, ask for it.</summary>
        public void ItemRentalDeposited()
        {
            if (Rent == null) return;
            Rent.Emit();
            if (Rent.RentalPeriod == 0) UpgradeWindow<RentWindow>(RentWindow.WindowKey)?.AskPeriod();
        }
    }
}

using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Windows;
using S = ServerPackets;

namespace Client.MirScenes.Modern
{
    /// <summary>
    /// Phase 3 area "NPC" (worker A): the NPC dialog (+ the item box npcBox), merchant (buy / sell / repair / special repair /
    /// buy back, bulk sends through the pacer), craft. Windows live in Client\MirScenes\Modern\Windows\ (Npc*, Merchant*, Craft*);
    /// the session state they share is <see cref="Npc"/> (NpcState). NPC-gated actions only from server-opened pages: every
    /// window opens from a server packet and closes with the NPC session.
    /// </summary>
    public sealed partial class ModernUi
    {
        /// <summary>The NPC session the NPC windows share (page, log, merchant / drop / craft panels); null before Build.</summary>
        internal NpcState Npc { get; private set; }

        partial void RegisterNpcWindows()
        {
            Npc?.Dispose();
            Npc = new NpcState();
            NpcState state = Npc;
            Root.Wm.Register(NpcWindow.WindowKey, () => new NpcWindow(state));
            Root.Wm.Register(MerchantWindow.WindowKey, () => new MerchantWindow(state));
            Root.Wm.Register(NpcBoxWindow.WindowKey, () => new NpcBoxWindow(state));
            Root.Wm.Register(CraftWindow.WindowKey, () => new CraftWindow(state));
        }

        /// <summary>No NPC window has a keybind: the server opens them all.</summary>
        private partial bool NpcKeyBind(KeybindOptions function) => false;

        /// <summary>MapControl's NPC click and S.NPCReplaceWedRing ask this (NPCDialog.Visible).</summary>
        public bool IsNpcDialogOpen() => Npc != null && Npc.Open;

        /// <summary>NPCDialog.Hide (MapControl.ResetMap, the NPC leaving the view): every NPC window closes, no packet.</summary>
        public void CloseNpcDialog() => Npc?.Close();

        public void NpcResponse(S.NPCResponse p) => Npc?.Response(p?.Page);

        public void NpcGoods(S.NPCGoods p) => Npc?.GoodsArrived(p?.List, false);

        public void NpcPearlGoods(S.NPCPearlGoods p) => Npc?.GoodsArrived(p?.List, true);

        public void NpcDropPanel(PanelType type)
        {
            if (UpgradeDropPanel(type)) return; // check-refine belongs to the refine window (refine.ts), like the web
            Npc?.DropPanel(type);
        }

        /// <summary>S.NPCRequestInput: the NPC page asks for a value (classic: a MirInputBox).</summary>
        public void NpcRequestInput(S.NPCRequestInput p)
        {
            if (Root == null || Root.IsDisposed || p == null) return;
            uint npc = p.NPCID;
            string page = p.PageName;
            Root.Popups.Input(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PleaseEnterRequiredInformation), string.Empty, 0, text =>
            {
                if (text == null) return;
                MirNetwork.Network.Enqueue(new ClientPackets.NPCConfirmInput { Value = text, NPCID = npc, PageName = page });
            });
        }

        public void CraftItemDone()
        {
            if (Root != null && Root.Wm.IsOpen(CraftWindow.WindowKey) && Root.Wm.Get(CraftWindow.WindowKey) is CraftWindow w) w.Done();
        }

        public bool IsNpcDropOpen => Npc != null && Npc.DropOpen;

        /// <summary>
        /// Alt + click on a bag item while a drop panel is open (ItemMoves picked it up first): the classic drop + confirm. Sell /
        /// repair panels sell or repair it at once (the merchant's checks); the item-service box puts it on the altar (the box's
        /// confirm stays the player's: destructive services are hold-to-confirm).
        /// </summary>
        public void ItemNpcDropQuickSell(MouseEventArgs e)
        {
            ItemSlotRef pick = ItemMoves.Selected;
            ItemMoves.Selected = null;
            if (pick == null || pick.GridType != MirGridType.Inventory || Npc == null || Root == null || !Npc.DropOpen) return;
            PanelType t = Npc.DropType;
            if (t == PanelType.Sell || t == PanelType.Repair || t == PanelType.SpecialRepair)
            {
                if (Root.Wm.Get(MerchantWindow.WindowKey) is MerchantWindow m) m.QuickSell(pick.ItemSlot);
                return;
            }
            if (NpcBoxWindow.Serves(t) && Root.Wm.Get(NpcBoxWindow.WindowKey) is NpcBoxWindow box) box.QuickPut(pick.ItemSlot);
        }
    }
}

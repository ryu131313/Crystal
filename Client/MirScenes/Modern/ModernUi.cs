using Client.MirScenes.Modern.Core;
using S = ServerPackets;

namespace Client.MirScenes.Modern
{
    /// <summary>
    /// The new UI (Desktop-Modern-UI-Plan). Phase 1a: an empty shell behind Settings.UiStyle = Modern (ini only, not selectable
    /// yet), so the seam can be exercised with no classic dialog constructed. Phases 1b-4 fill it window by window.
    /// Every IGameUi method not ported yet calls <see cref="NotPorted"/>: a no-op that logs once per method.
    /// </summary>
    public sealed partial class ModernUi : IGameUi
    {
        private readonly GameScene _scene;
        private static readonly HashSet<string> _notPortedLogged = new HashSet<string>();

        public ModernUi(GameScene scene)
        {
            _scene = scene;
        }

        public UiStyle Style => UiStyle.Modern;

        /// <summary>Logs "[ModernUi] not ported: name" the first time per method and process; never throws.</summary>
        public static void NotPorted(string name)
        {
            if (!_notPortedLogged.Add(name)) return;
            if (Settings.LogErrors) CMain.SaveError("[ModernUi] not ported: " + name);
        }

        /// <summary>The kit's root (windows, popups, …); null before Build and after Dispose.</summary>
        public ModernRoot Root { get; private set; }

        public void Build()
        {
            ModernTheme.ApplySettings();
            Root = new ModernRoot(_scene);
            RegisterWindows();
        }

        /// <summary>The window catalog (window keys → factories). Phase 1b: the kit test window only.</summary>
        private void RegisterWindows()
        {
            Root.Wm.Register(KitTestWindow.WindowKey, () => new KitTestWindow());
        }

        /// <summary>Spaces the modern UI's bulk sends (item moves, sorts, sell junk, …) under the server's MaxPacket.</summary>
        public readonly SendPacer Pacer = new SendPacer();

        public void Process()
        {
            if (Root == null || Root.IsDisposed) return;
            Root.KeepOnTop();
            Root.Process();
            Root.Wm.Process();
            Pacer.Process();
        }

        /// <summary>From GameScene.DrawControl after the scene texture: the windows, each from its own render target.</summary>
        public void DrawOverlay() => Root?.DrawOverlay();

        public void KeyDown(KeyEventArgs e)
        {
        }

        public void Dispose()
        {
            Pacer.Dispose();
            Root = null; // a child of the scene: disposed with it
        }

        public void BuffAdded(bool hero, ClientBuff buff) => NotPorted(nameof(BuffAdded));
        public void BuffRemoved(bool hero, int index) => NotPorted(nameof(BuffRemoved));
        public void GuildActiveStatsChanged() => NotPorted(nameof(GuildActiveStatsChanged));
        public void GuildStatusChanging(S.GuildStatus p, bool joined) => NotPorted(nameof(GuildStatusChanging));
        public void GuildStatusChanged(S.GuildStatus p) => NotPorted(nameof(GuildStatusChanged));
        public void GuildMembersChanged() => NotPorted(nameof(GuildMembersChanged));

        // ---- seam step 4 (sweep) BEGIN: generated, keep in this order ----
        /// <summary>Per frame: a silent no-op until ported (NotPorted is not called per spin).</summary>
        public void DrawOutputLines()
        {
        }
        public void HasHeroChanged(bool value) => NotPorted(nameof(HasHeroChanged));
        /// <summary>Esc closes the front window, Shift+Esc all; the front window's own keys first (wm.ts handleKey).</summary>
        public bool CapturesKey(KeyEventArgs e) => Root != null && !Root.IsDisposed && Root.Wm.HandleKey(e);
        public void KeyBind(KeybindOptions function)
        {
            switch (function)
            {
                case KeybindOptions.Options:
                case KeybindOptions.Options2:
                    // Phase 1b: the options key opens the kit test window (the modern options window comes in phase 2).
                    Root?.Wm.Toggle(KitTestWindow.WindowKey);
                    return;
                case KeybindOptions.Closeall:
                    Root?.Wm.CloseAll();
                    return;
            }
            NotPorted(nameof(KeyBind));
        }
        public void SkillModeChanged(bool ctrl) => NotPorted(nameof(SkillModeChanged));
        public void ObjectPositionsChanged() { } // called per object move: silent like the per-frame members
        /// <summary>Per frame: a silent no-op until ported (NotPorted is not called per spin).</summary>
        public void ProcessBeforeMap()
        {
        }
        /// <summary>Per frame: a silent no-op until ported (NotPorted is not called per spin).</summary>
        public void DialogProcess()
        {
        }
        /// <summary>Per frame: a silent no-op until ported (NotPorted is not called per spin).</summary>
        public void ProcessOutput()
        {
        }
        public void GameDataRefreshed(bool magicsChanged, bool shopChanged, bool hoverChanged) => NotPorted(nameof(GameDataRefreshed));
        public void MapInfoInvalidated(int mapIndex) => NotPorted(nameof(MapInfoInvalidated));
        public void WorldMapSetup(WorldMapSetup setup) => NotPorted(nameof(WorldMapSetup));
        public void MapInfoReceived(S.NewMapInfo info) => NotPorted(nameof(MapInfoReceived));
        public void SearchMapResult(S.SearchMapResult info) => NotPorted(nameof(SearchMapResult));
        public void UserLoaded() => NotPorted(nameof(UserLoaded));
        public void HeroBehaviourChanged(HeroBehaviour behaviour) => NotPorted(nameof(HeroBehaviourChanged));
        public void UserInformationReceived() => NotPorted(nameof(UserInformationReceived));
        public bool IsNpcDialogOpen()
        {
            NotPorted(nameof(IsNpcDialogOpen));
            return false;
        }
        public void CloseNpcDialog() => NotPorted(nameof(CloseNpcDialog));
        public void PlayerInspected(bool isHero) => NotPorted(nameof(PlayerInspected));
        public void MountChanged() => NotPorted(nameof(MountChanged));
        public void FishingUpdated(S.FishingUpdate p) => NotPorted(nameof(FishingUpdated));
        public void QuestAdded(ClientQuestProgress quest, bool track) => NotPorted(nameof(QuestAdded));
        public void QuestRemoved(ClientQuestProgress quest) => NotPorted(nameof(QuestRemoved));
        public void QuestsChanged() => NotPorted(nameof(QuestsChanged));
        public void LightsChanged() => NotPorted(nameof(LightsChanged));
        public void UserItemDuraChanged(UserItem item) => NotPorted(nameof(UserItemDuraChanged));
        public void HeroInfoChanged() => NotPorted(nameof(HeroInfoChanged));
        public void NpcResponse(S.NPCResponse p) => NotPorted(nameof(NpcResponse));
        public void NpcGoods(S.NPCGoods p) => NotPorted(nameof(NpcGoods));
        public void NpcPearlGoods(S.NPCPearlGoods p) => NotPorted(nameof(NpcPearlGoods));
        public void NpcDropPanel(PanelType type) => NotPorted(nameof(NpcDropPanel));
        public void NpcStorage() => NotPorted(nameof(NpcStorage));
        public void StorageUnlockResult(S.StorageUnlockResult p) => NotPorted(nameof(StorageUnlockResult));
        public void StoragePasswordResult(S.StoragePasswordResult p) => NotPorted(nameof(StoragePasswordResult));
        public void NpcRefine(bool refining) => NotPorted(nameof(NpcRefine));
        public void NpcCollectRefine() => NotPorted(nameof(NpcCollectRefine));
        public void CraftItemDone() => NotPorted(nameof(CraftItemDone));
        public void ItemUpgraded(MirGridType grid, UserItem item) => NotPorted(nameof(ItemUpgraded));
        public void SkillBarsChanged() => NotPorted(nameof(SkillBarsChanged));
        public void GroupInvite(S.GroupInvite p) => NotPorted(nameof(GroupInvite));
        public void ItemInfoChanged(UserItem item) => NotPorted(nameof(ItemInfoChanged));
        public void MarketOpening() => NotPorted(nameof(MarketOpening));
        public void MarketListed(int pages) => NotPorted(nameof(MarketListed));
        public bool IsMarketOpen()
        {
            NotPorted(nameof(IsMarketOpen));
            return false;
        }
        public void MarketPageAdded() => NotPorted(nameof(MarketPageAdded));
        public void GuildNoticeChange(S.GuildNoticeChange p) => NotPorted(nameof(GuildNoticeChange));
        public void GuildMemberChange(S.GuildMemberChange p) => NotPorted(nameof(GuildMemberChange));
        public void GuildTerritoryPage(S.GuildTerritoryPage p) => NotPorted(nameof(GuildTerritoryPage));
        public void HeroCreateRequest(S.HeroCreateRequest p) => NotPorted(nameof(HeroCreateRequest));
        public void ManageHeroes(ClientHeroInformation currentHero) => NotPorted(nameof(ManageHeroes));
        public void ChangeHero(S.ChangeHero p) => NotPorted(nameof(ChangeHero));
        public void HeroAutoPotChanged() => NotPorted(nameof(HeroAutoPotChanged));
        public void NewHeroResult(S.NewHero p) => NotPorted(nameof(NewHeroResult));
        public void HeroInformationReceived() => NotPorted(nameof(HeroInformationReceived));
        public void HeroSpawnStateChanged(HeroSpawnState state) => NotPorted(nameof(HeroSpawnStateChanged));
        public void TradeAccepted() => NotPorted(nameof(TradeAccepted));
        public void TradeGuestChanged() => NotPorted(nameof(TradeGuestChanged));
        public void TradeConfirmed() => NotPorted(nameof(TradeConfirmed));
        public void TradeCancelled(bool unlock) => NotPorted(nameof(TradeCancelled));
        public void NpcAwakening() => NotPorted(nameof(NpcAwakening));
        public void AwakeningNeedMaterials(S.AwakeningNeedMaterials p) => NotPorted(nameof(AwakeningNeedMaterials));
        public void MailReceived() => NotPorted(nameof(MailReceived));
        public void MailSendRequest() => NotPorted(nameof(MailSendRequest));
        public void ParcelCollected(sbyte result) => NotPorted(nameof(ParcelCollected));
        public void InventoryResized() => NotPorted(nameof(InventoryResized));
        public void StorageResized() => NotPorted(nameof(StorageResized));
        public void MailCost(uint cost) => NotPorted(nameof(MailCost));
        public void NewIntelligentCreature() => NotPorted(nameof(NewIntelligentCreature));
        public void IntelligentCreaturesChanged(bool listReplaced) => NotPorted(nameof(IntelligentCreaturesChanged));
        public void IntelligentCreatureEnableRename() => NotPorted(nameof(IntelligentCreatureEnableRename));
        public void FriendsChanged() => NotPorted(nameof(FriendsChanged));
        public void LoverUpdate(S.LoverUpdate p) => NotPorted(nameof(LoverUpdate));
        public void MentorUpdate(S.MentorUpdate p) => NotPorted(nameof(MentorUpdate));
        public void GameShopNewItems() => NotPorted(nameof(GameShopNewItems));
        public void GameShopStockChanged() => NotPorted(nameof(GameShopStockChanged));
        public void RankingsReceived(List<RankCharacterInfo> listings, S.Rankings p) => NotPorted(nameof(RankingsReceived));
        public void RentedItems(S.GetRentedItems p) => NotPorted(nameof(RentedItems));
        public void ItemRentalRequest(S.ItemRentalRequest p) => NotPorted(nameof(ItemRentalRequest));
        public void ItemRentalFee(S.ItemRentalFee p) => NotPorted(nameof(ItemRentalFee));
        public void ItemRentalPeriod(S.ItemRentalPeriod p) => NotPorted(nameof(ItemRentalPeriod));
        public void RentalGuestItemChanged() => NotPorted(nameof(RentalGuestItemChanged));
        public void ItemRentalReset(bool cancelled) => NotPorted(nameof(ItemRentalReset));
        public void ItemRentalLocked() => NotPorted(nameof(ItemRentalLocked));
        public void ItemRentalPartnerLock(S.ItemRentalPartnerLock p) => NotPorted(nameof(ItemRentalPartnerLock));
        public void CanConfirmItemRental() => NotPorted(nameof(CanConfirmItemRental));
        public void SetTimer(S.SetTimer p) => NotPorted(nameof(SetTimer));
        public void ExpireTimer(string key) => NotPorted(nameof(ExpireTimer));
        public void SetCompass(Point location) => NotPorted(nameof(SetCompass));
        public void Roll(S.Roll p) => NotPorted(nameof(Roll));
        public void ShowNotice(S.UpdateNotice p) => NotPorted(nameof(ShowNotice));
        public void CloseMountWindow() => NotPorted(nameof(CloseMountWindow));
        public void CloseFishingWindow() => NotPorted(nameof(CloseFishingWindow));
        public void FishingCast() => NotPorted(nameof(FishingCast));
        public void FishingWait(bool foundFish) => NotPorted(nameof(FishingWait));
        // ---- seam step 4 (sweep) END ----
    }
}

using S = ServerPackets;

namespace Client.MirScenes.Classic
{
    /// <summary>
    /// The classic desktop UI behind IGameUi (Desktop-Modern-UI-Plan seam step 2). Every member forwards to the classic half of
    /// GameScene (Classic\GameScene.Classic*.cs), where the code moved out of GameScene.cs sits unchanged and in its original
    /// order, so Classic plays exactly as before.
    /// </summary>
    public sealed partial class ClassicUi : IGameUi
    {
        private readonly GameScene _scene;

        public ClassicUi(GameScene scene)
        {
            _scene = scene;
        }

        public UiStyle Style => UiStyle.Classic;

        public void Build()
        {
            _scene.ClassicBuild();
            _scene.ClassicBuildLate();
        }

        public void Process() => _scene.ClassicProcess();

        public void KeyDown(KeyEventArgs e)
        {
        }

        public void Dispose() => _scene.ClassicDispose();
        public void DrawOverlay() { } // the classic dialogs are scene children, drawn in the scene texture
        public void MarketMessage(string text, bool fail) => MirControls.MirMessageBox.Show(text); // as GameScene did
        public void NpcRequestInput(S.NPCRequestInput p) => _scene.ClassicNpcRequestInput(p);

        public void BuffAdded(bool hero, ClientBuff buff) => _scene.ClassicBuffAdded(hero, buff);
        public void BuffRemoved(bool hero, int index) => _scene.ClassicBuffRemoved(hero, index);
        public void GuildActiveStatsChanged() => _scene.ClassicGuildActiveStatsChanged();
        public void GuildStatusChanging(S.GuildStatus p, bool joined) => _scene.ClassicGuildStatusChanging(p, joined);
        public void GuildStatusChanged(S.GuildStatus p) => _scene.ClassicGuildStatusChanged(p);
        public void GuildMembersChanged() => _scene.ClassicGuildMembersChanged();

        // ---- seam step 4 (sweep) BEGIN: generated, keep in this order ----
        public void DrawOutputLines() => _scene.ClassicDrawOutputLines();
        public void HasHeroChanged(bool value) => _scene.ClassicHasHeroChanged(value);
        public bool CapturesKey(KeyEventArgs e) => _scene.ClassicCapturesKey(e);
        public void KeyBind(KeybindOptions function) => _scene.ClassicKeyBind(function);
        public void SkillModeChanged(bool ctrl) => _scene.ClassicSkillModeChanged(ctrl);
        public void ObjectPositionsChanged() => _scene.ClassicObjectPositionsChanged();
        public void ProcessBeforeMap() => _scene.ClassicProcessBeforeMap();
        public void DialogProcess() => _scene.ClassicDialogProcess();
        public void ProcessOutput() => _scene.ClassicProcessOutput();
        public void GameDataRefreshed(bool magicsChanged, bool shopChanged, bool hoverChanged) => _scene.ClassicGameDataRefreshed(magicsChanged, shopChanged, hoverChanged);
        public void MapInfoInvalidated(int mapIndex) => _scene.ClassicMapInfoInvalidated(mapIndex);
        public void WorldMapSetup(WorldMapSetup setup) => _scene.ClassicWorldMapSetup(setup);
        public void MapInfoReceived(S.NewMapInfo info) => _scene.ClassicMapInfoReceived(info);
        public void SearchMapResult(S.SearchMapResult info) => _scene.ClassicSearchMapResult(info);
        public void UserLoaded() => _scene.ClassicUserLoaded();
        public void HeroBehaviourChanged(HeroBehaviour behaviour) => _scene.ClassicHeroBehaviourChanged(behaviour);
        public void UserInformationReceived() => _scene.ClassicUserInformationReceived();
        public bool IsNpcDialogOpen() => _scene.ClassicIsNpcDialogOpen();
        public void CloseNpcDialog() => _scene.ClassicCloseNpcDialog();
        public void PlayerInspected(bool isHero) => _scene.ClassicPlayerInspected(isHero);
        public void MountChanged() => _scene.ClassicMountChanged();
        public void FishingUpdated(S.FishingUpdate p) => _scene.ClassicFishingUpdated(p);
        public void QuestAdded(ClientQuestProgress quest, bool track) => _scene.ClassicQuestAdded(quest, track);
        public void QuestRemoved(ClientQuestProgress quest) => _scene.ClassicQuestRemoved(quest);
        public void QuestsChanged() => _scene.ClassicQuestsChanged();
        public void LightsChanged() => _scene.ClassicLightsChanged();
        public void UserItemDuraChanged(UserItem item) => _scene.ClassicUserItemDuraChanged(item);
        public void HeroInfoChanged() => _scene.ClassicHeroInfoChanged();
        public void NpcResponse(S.NPCResponse p) => _scene.ClassicNpcResponse(p);
        public void NpcGoods(S.NPCGoods p) => _scene.ClassicNpcGoods(p);
        public void NpcPearlGoods(S.NPCPearlGoods p) => _scene.ClassicNpcPearlGoods(p);
        public void NpcDropPanel(PanelType type) => _scene.ClassicNpcDropPanel(type);
        public void NpcStorage() => _scene.ClassicNpcStorage();
        public void StorageUnlockResult(S.StorageUnlockResult p) => _scene.ClassicStorageUnlockResult(p);
        public void StoragePasswordResult(S.StoragePasswordResult p) => _scene.ClassicStoragePasswordResult(p);
        public void NpcRefine(bool refining) => _scene.ClassicNpcRefine(refining);
        public void NpcCollectRefine() => _scene.ClassicNpcCollectRefine();
        public void CraftItemDone() => _scene.ClassicCraftItemDone();
        public void ItemUpgraded(MirGridType grid, UserItem item) => _scene.ClassicItemUpgraded(grid, item);
        public void SkillBarsChanged() => _scene.ClassicSkillBarsChanged();
        public void GroupInvite(S.GroupInvite p) => _scene.ClassicGroupInvite(p);
        public void ItemInfoChanged(UserItem item) => _scene.ClassicItemInfoChanged(item);
        public void MarketOpening() => _scene.ClassicMarketOpening();
        public void MarketListed(int pages) => _scene.ClassicMarketListed(pages);
        public bool IsMarketOpen() => _scene.ClassicIsMarketOpen();
        public void MarketPageAdded() => _scene.ClassicMarketPageAdded();
        public void GuildNoticeChange(S.GuildNoticeChange p) => _scene.ClassicGuildNoticeChange(p);
        public void GuildMemberChange(S.GuildMemberChange p) => _scene.ClassicGuildMemberChange(p);
        public void GuildTerritoryPage(S.GuildTerritoryPage p) => _scene.ClassicGuildTerritoryPage(p);
        public void HeroCreateRequest(S.HeroCreateRequest p) => _scene.ClassicHeroCreateRequest(p);
        public void ManageHeroes(ClientHeroInformation currentHero) => _scene.ClassicManageHeroes(currentHero);
        public void ChangeHero(S.ChangeHero p) => _scene.ClassicChangeHero(p);
        public void HeroAutoPotChanged() => _scene.ClassicHeroAutoPotChanged();
        public void NewHeroResult(S.NewHero p) => _scene.ClassicNewHeroResult(p);
        public void HeroInformationReceived() => _scene.ClassicHeroInformationReceived();
        public void HeroSpawnStateChanged(HeroSpawnState state) => _scene.ClassicHeroSpawnStateChanged(state);
        public void TradeAccepted() => _scene.ClassicTradeAccepted();
        public void TradeGuestChanged() => _scene.ClassicTradeGuestChanged();
        public void TradeConfirmed() => _scene.ClassicTradeConfirmed();
        public void TradeCancelled(bool unlock) => _scene.ClassicTradeCancelled(unlock);
        public void NpcAwakening() => _scene.ClassicNpcAwakening();
        public void AwakeningNeedMaterials(S.AwakeningNeedMaterials p) => _scene.ClassicAwakeningNeedMaterials(p);
        public void MailReceived() => _scene.ClassicMailReceived();
        public void MailSendRequest() => _scene.ClassicMailSendRequest();
        public void ParcelCollected(sbyte result) => _scene.ClassicParcelCollected(result);
        public void InventoryResized() => _scene.ClassicInventoryResized();
        public void StorageResized() => _scene.ClassicStorageResized();
        public void MailCost(uint cost) => _scene.ClassicMailCost(cost);
        public void NewIntelligentCreature() => _scene.ClassicNewIntelligentCreature();
        public void IntelligentCreaturesChanged(bool listReplaced) => _scene.ClassicIntelligentCreaturesChanged(listReplaced);
        public void IntelligentCreatureEnableRename() => _scene.ClassicIntelligentCreatureEnableRename();
        public void FriendsChanged() => _scene.ClassicFriendsChanged();
        public void LoverUpdate(S.LoverUpdate p) => _scene.ClassicLoverUpdate(p);
        public void MentorUpdate(S.MentorUpdate p) => _scene.ClassicMentorUpdate(p);
        public void GameShopNewItems() => _scene.ClassicGameShopNewItems();
        public void GameShopStockChanged() => _scene.ClassicGameShopStockChanged();
        public void RankingsReceived(List<RankCharacterInfo> listings, S.Rankings p) => _scene.ClassicRankingsReceived(listings, p);
        public void RentedItems(S.GetRentedItems p) => _scene.ClassicRentedItems(p);
        public void ItemRentalRequest(S.ItemRentalRequest p) => _scene.ClassicItemRentalRequest(p);
        public void ItemRentalFee(S.ItemRentalFee p) => _scene.ClassicItemRentalFee(p);
        public void ItemRentalPeriod(S.ItemRentalPeriod p) => _scene.ClassicItemRentalPeriod(p);
        public void RentalGuestItemChanged() => _scene.ClassicRentalGuestItemChanged();
        public void ItemRentalReset(bool cancelled) => _scene.ClassicItemRentalReset(cancelled);
        public void ItemRentalLocked() => _scene.ClassicItemRentalLocked();
        public void ItemRentalPartnerLock(S.ItemRentalPartnerLock p) => _scene.ClassicItemRentalPartnerLock(p);
        public void CanConfirmItemRental() => _scene.ClassicCanConfirmItemRental();
        public void SetTimer(S.SetTimer p) => _scene.ClassicSetTimer(p);
        public void ExpireTimer(string key) => _scene.ClassicExpireTimer(key);
        public void SetCompass(Point location) => _scene.ClassicSetCompass(location);
        public void Roll(S.Roll p) => _scene.ClassicRoll(p);
        public void ShowNotice(S.UpdateNotice p) => _scene.ClassicShowNotice(p);
        public void CloseMountWindow() => _scene.ClassicCloseMountWindow();
        public void CloseFishingWindow() => _scene.ClassicCloseFishingWindow();
        public void FishingCast() => _scene.ClassicFishingCast();
        public void FishingWait(bool foundFish) => _scene.ClassicFishingWait(foundFish);
        // ---- seam step 4 (sweep) END ----
    }
}

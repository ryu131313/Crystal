using S = ServerPackets;

namespace Client.MirScenes
{
    /// <summary>[Interface] UiStyle (Settings.UiStyle; Desktop-Modern-UI-Plan D6).</summary>
    public enum UiStyle : byte
    {
        /// <summary>The build's default: Modern (since 2026-10-20).</summary>
        Default = 0,
        Modern = 1,
        Classic = 2,
    }

    public static class UiStyles
    {
        /// <summary>Resolves Settings.UiStyle: Default = Modern (since 2026-10-20); a saved Classic always wins.</summary>
        public static bool IsModern => Settings.UiStyle != UiStyle.Classic;
    }

    /// <summary>
    /// The game scene's UI seam (Desktop-Modern-UI-Plan seam step 2; the desktop form of WebClient/src/ui/gameUi.ts GameUi).
    /// GameScene builds exactly one: <see cref="Classic.ClassicUi"/> (the classic dialogs, which are never constructed in modern
    /// mode) or <see cref="Modern.ModernUi"/>. Game state goes into GameScene.Models first; this interface carries only the
    /// leftovers that are not model changes: building and per-frame work of the windows, opening / closing windows, prompts,
    /// key handling, and the UI half of packets whose state is shown but not kept by a model.
    /// Rules: called on the UI thread only; ClassicUi keeps the moved code in its original order; a ModernUi method a phase has
    /// not ported yet is a no-op that logs once (ModernUi.NotPorted), never per spin.
    /// </summary>
    public partial interface IGameUi
    {
        /// <summary>UiStyle.Classic or UiStyle.Modern (never Default).</summary>
        UiStyle Style { get; }

        /// <summary>Builds the windows (end of the GameScene constructor; GameScene.Models already exists).</summary>
        void Build();

        /// <summary>Per-frame UI work, from GameScene.Process right after MapControl.Process (see also ProcessBeforeMap, DialogProcess, ProcessOutput).</summary>
        void Process();

        /// <summary>Not called yet (phase 1a): window keys go through CapturesKey / KeyBind from GameScene_KeyDown.</summary>
        void KeyDown(KeyEventArgs e);

        /// <summary>From GameScene.Dispose(true): drop window references and unsubscribe from the models.</summary>
        void Dispose();

        // ---- Buffs (models: Buffs, HeroBuffs, Guild) ----

        /// <summary>A new buff was appended to Models.Buffs (hero = false) or Models.HeroBuffs.</summary>
        void BuffAdded(bool hero, ClientBuff buff);
        /// <summary>The buff at index is about to be removed from Models.Buffs / HeroBuffs (still in the list).</summary>
        void BuffRemoved(bool hero, int index);
        /// <summary>The active guild buffs changed (classic: GuildDialog.UpdateActiveStats).</summary>
        void GuildActiveStatsChanged();

        // ---- Guild (model: Guild) ----

        /// <summary>S.GuildStatus, before the model update; joined = the user had no guild and now has one.</summary>
        void GuildStatusChanging(S.GuildStatus p, bool joined);
        /// <summary>S.GuildStatus, after the status values and MyOptions reached Models.Guild (MyRankId not yet).</summary>
        void GuildStatusChanged(S.GuildStatus p);
        /// <summary>The member list or counts in Models.Guild changed (classic: GuildDialog.UpdateMembers).</summary>
        void GuildMembersChanged();

        // ---- seam step 4 (sweep) BEGIN: generated, keep in this order ----
        /// <summary>From GameScene.DrawControl after the scene is drawn: the classic output message lines (per frame; Modern must not log).</summary>
        void DrawOutputLines();
        /// <summary>GameScene.DrawControl, right after the scene texture and before the picked-up item: the modern windows (classic: nothing).</summary>
        void DrawOverlay();
        /// <summary>S.MarketSuccess / S.MarketFail: the trust merchant's answer text (classic: MirMessageBox.Show); fail = S.MarketFail.</summary>
        void MarketMessage(string text, bool fail);
        /// <summary>S.NPCRequestInput: the NPC page asks for a value; answer with C.NPCConfirmInput (classic: a MirInputBox).</summary>
        void NpcRequestInput(S.NPCRequestInput p);
        /// <summary>GameScene.HasHero changed (classic: the hero summon button).</summary>
        void HasHeroChanged(bool value);
        /// <summary>GameScene.KeyDown, first: true when a window takes the key itself (classic: the keybind dialog waiting for a new binding); GameScene then stops.</summary>
        bool CapturesKey(KeyEventArgs e);
        /// <summary>GameScene.KeyDown, a matched keybind GameScene does not handle itself (windows, belts drawn by a window, camera mode, add group member).</summary>
        void KeyBind(KeybindOptions function);
        /// <summary>GameScene.ChangeSkillMode switched Settings.SkillMode (ctrl = Ctrl mode now; classic: the option dialog's skill buttons).</summary>
        void SkillModeChanged(bool ctrl);
        /// <summary>An object moved or died and Settings.RebuildOptimisations is on: redraw only what shows positions (classic: minimap, big map).</summary>
        void ObjectPositionsChanged();
        /// <summary>GameScene.Process, every spin, after the move / ping timers and before MapControl.Process (classic: timers, tooltips, the revive prompt, buffs).</summary>
        void ProcessBeforeMap();
        /// <summary>GameScene.DialogProcess, every spin after the particle engines (classic: skill bar visibility / placement, the dura panel).</summary>
        void DialogProcess();
        /// <summary>GameScene.ProcessOuput, every spin after expired OutputMessages were dropped (classic: the output lines show OutputMessages).</summary>
        void ProcessOutput();
        /// <summary>S.GameDataChanged, last chunk, after the user's and hero's stats were refreshed.</summary>
        void GameDataRefreshed(bool magicsChanged, bool shopChanged, bool hoverChanged);
        /// <summary>S.GameDataChanged invalidated a map's info (classic: drops the cached big-map record and its controls).</summary>
        void MapInfoInvalidated(int mapIndex);
        /// <summary>S.WorldMapSetupInfo (before TeleportToNPCCost is stored).</summary>
        void WorldMapSetup(WorldMapSetup setup);
        /// <summary>S.NewMapInfo (classic: the big-map record cache GameScene.MapInfoList and its buttons).</summary>
        void MapInfoReceived(S.NewMapInfo info);
        /// <summary>S.SearchMapResult (classic: the big map's search result, or a "nothing found" box).</summary>
        void SearchMapResult(S.SearchMapResult info);
        /// <summary>S.UserInformation, right after the new UserObject loaded (before HasHero, Gold and Credit are set).</summary>
        void UserLoaded();
        /// <summary>The hero's behaviour arrived (S.UserInformation, S.SetHeroBehaviour).</summary>
        void HeroBehaviourChanged(HeroBehaviour behaviour);
        /// <summary>S.UserInformation, after User, HasHero, Gold and Credit are set (before AllowObserve / Observing).</summary>
        void UserInformationReceived();
        /// <summary>MapControl left click on the current NPC: true while its conversation window is open (the click is then ignored).</summary>
        bool IsNpcDialogOpen();
        /// <summary>Close the NPC conversation window (MapControl.ResetMap, MapObject removal of the NPC).</summary>
        void CloseNpcDialog();
        /// <summary>S.PlayerInspect, after Models.Inspect holds the items and values.</summary>
        void PlayerInspected(bool isHero);
        /// <summary>S.MountUpdate for the user, after User.RefreshStats (before the scene redraw).</summary>
        void MountChanged();
        /// <summary>S.FishingUpdate for the user (before the scene redraw; classic: the fishing status window keeps the percentages).</summary>
        void FishingUpdated(S.FishingUpdate p);
        /// <summary>S.QuestStates added a run after login (Models.Quests holds it); track = the server's TrackHint (auto-track it).</summary>
        void QuestAccepted(int quest, bool track);
        /// <summary>Any quest V2 packet changed Models.Quests (definitions, runs, progress, history, NPC availability).</summary>
        void QuestsChanged();
        /// <summary>S.TimeOfDay, after GameScene.Lights was set.</summary>
        void LightsChanged();
        /// <summary>S.DuraChanged, after the item's durability changed and any warning reached chat (classic: hovered tooltip rebuild, the dura panel).</summary>
        void UserItemDuraChanged(UserItem item);
        /// <summary>The hero's level / experience / health shown in the hero info panel changed (classic: MainDialog.HeroInfoPanel.Update).</summary>
        void HeroInfoChanged();
        /// <summary>S.NPCResponse, after NPCTime was reset and Models.Npc.Page set (classic: the NPC window shows the page and the NPC panels close).</summary>
        void NpcResponse(S.NPCResponse p);
        /// <summary>S.NPCGoods, after NPCRate / NPCPanelType / HideAddedStoreStats were set (only shown while the NPC window is open).</summary>
        void NpcGoods(S.NPCGoods p);
        /// <summary>S.NPCPearlGoods, after NPCRate / NPCPanelType were set (only shown while the NPC window is open).</summary>
        void NpcPearlGoods(S.NPCPearlGoods p);
        /// <summary>The NPC opened the item drop panel for sell / repair / special repair / check refine / replace wedding ring (only while the NPC window is open).</summary>
        void NpcDropPanel(PanelType type);
        /// <summary>S.NPCStorage (only while the NPC window is open).</summary>
        void NpcStorage();
        /// <summary>S.StorageUnlockResult (the storage password prompt).</summary>
        void StorageUnlockResult(S.StorageUnlockResult p);
        /// <summary>S.StoragePasswordResult (the storage password prompt).</summary>
        void StoragePasswordResult(S.StoragePasswordResult p);
        /// <summary>S.NPCRefine, after NPCRate was set (only while the NPC window is open; refining = the item went in, close the windows).</summary>
        void NpcRefine(bool refining);
        /// <summary>S.NPCCollectRefine (closes the NPC window while it is open).</summary>
        void NpcCollectRefine();
        /// <summary>S.CraftItem succeeded (before User.RefreshStats; classic: the craft cells refresh).</summary>
        void CraftItemDone();
        /// <summary>S.ItemUpgraded, after the item's stats changed (grid = Inventory or HeroInventory; classic: grid effect, hovered tooltip rebuild).</summary>
        void ItemUpgraded(MirGridType grid, UserItem item);
        /// <summary>The user's or hero's magics changed (new / removed spell, key change, …): the skill bars refresh.</summary>
        void SkillBarsChanged();
        /// <summary>S.GroupInvite: ask the user to join (Yes sends C.GroupInvite accept and opens the group window, No declines).</summary>
        void GroupInvite(S.GroupInvite p);
        /// <summary>An item's shown values changed in place (S.ItemRepaired, S.ItemSealChanged): classic rebuilds its tooltip if it is the hovered item.</summary>
        void ItemInfoChanged(UserItem item);
        /// <summary>S.NPCMarket, before Models.Market takes the user mode and listings (classic: shows the trust merchant window first).</summary>
        void MarketOpening();
        /// <summary>S.NPCMarket, after Models.Market holds the new listings (pages = the server's page count).</summary>
        void MarketListed(int pages);
        /// <summary>S.NPCMarketPage: the next page is only taken while the market window is open.</summary>
        bool IsMarketOpen();
        /// <summary>S.NPCMarketPage, after the page was appended to Models.Market.Listings.</summary>
        void MarketPageAdded();
        /// <summary>S.GuildNoticeChange (update -1 = the notice changed and is fetched when shown; else the new notice).</summary>
        void GuildNoticeChange(S.GuildNoticeChange p);
        /// <summary>S.GuildMemberChange, after the chat line and Models.Guild (MemberCount, MembersStale, and the member list for statuses 0, 1, 6, 7, 8, 255); edit = what statuses 7 / 8 changed.</summary>
        void GuildMemberChange(S.GuildMemberChange p, Models.GuildMemberEdit edit);
        /// <summary>S.GuildTerritoryPage (classic: the guild territory window shows the page).</summary>
        void GuildTerritoryPage(S.GuildTerritoryPage p);
        /// <summary>S.HeroCreateRequest: open hero creation with the classes the server allows.</summary>
        void HeroCreateRequest(S.HeroCreateRequest p);
        /// <summary>S.ManageHeroes, after HeroInfoList / HeroStorage and MaximumHeroCount were updated.</summary>
        void ManageHeroes(ClientHeroInformation currentHero);
        /// <summary>S.ChangeHero, after GameScene.CurrentHeroInfo was swapped with HeroStorage[p.FromIndex].</summary>
        void ChangeHero(S.ChangeHero p);
        /// <summary>The hero's auto-potion switch or percentages changed (Hero.AutoPot / AutoHPPercent / AutoMPPercent).</summary>
        void HeroAutoPotChanged();
        /// <summary>S.NewHero: the hero creation result (message, close or refocus the creation window).</summary>
        void NewHeroResult(S.NewHero p);
        /// <summary>S.HeroInformation, after the new hero loaded and Models.HeroBuffs was cleared (before Hero.RefreshStats; classic: builds the hero windows).</summary>
        void HeroInformationReceived();
        /// <summary>S.UpdateHeroSpawnState, after HeroSpawnState and HasHero were set (classic: hero panels; below Summoned the hero windows are disposed).</summary>
        void HeroSpawnStateChanged(HeroSpawnState state);
        /// <summary>S.TradeAccept, after Models.Trade.GuestName was set (classic: opens the trade windows).</summary>
        void TradeAccepted();
        /// <summary>S.TradeGold / S.TradeItem, after Models.Trade took the guest's offer and User.TradeLocked was cleared (classic: unlocks the user's side and refreshes).</summary>
        void TradeGuestChanged();
        /// <summary>S.TradeConfirm, after Models.Trade.Reset cleared both sides (User.Trade / TradeGoldAmount / TradeLocked; classic: closes the windows).</summary>
        void TradeConfirmed();
        /// <summary>S.TradeCancel, after User.TradeLocked was cleared (unlock) or Models.Trade.Reset ran (else: also tell the user).</summary>
        void TradeCancelled(bool unlock);
        /// <summary>S.NPCAwakening: open the awakening window.</summary>
        void NpcAwakening();
        /// <summary>S.AwakeningNeedMaterials: the materials the awakening window shows.</summary>
        void AwakeningNeedMaterials(S.AwakeningNeedMaterials p);
        /// <summary>S.ReceiveMail, after User.Mail and NewMail were updated.</summary>
        void MailReceived();
        /// <summary>S.MailSendRequest: ask for the recipient, then start a parcel to them.</summary>
        void MailSendRequest();
        /// <summary>S.ParcelCollected (-1 none to collect, 0 all collected, 1 collected: close the parcel).</summary>
        void ParcelCollected(sbyte result);
        /// <summary>S.ResizeInventory, after User.Inventory was resized.</summary>
        void InventoryResized();
        /// <summary>S.ResizeStorage, after GameScene.Storage and the expanded-storage values were updated.</summary>
        void StorageResized();
        /// <summary>S.MailCost: the postage of the parcel being composed (classic: shown, with a gold sound, while the parcel window is open).</summary>
        void MailCost(uint cost);
        /// <summary>S.NewIntelligentCreature, after the creature was added to User.IntelligentCreatures: ask for its name (OK sends C.UpdateIntelligentCreature).</summary>
        void NewIntelligentCreature();
        /// <summary>S.UpdateIntelligentCreatureList, after User.IntelligentCreatures was updated (listReplaced = the count changed and the list was rebuilt).</summary>
        void IntelligentCreaturesChanged(bool listReplaced);
        /// <summary>S.IntelligentCreatureEnableRename: the creature may be renamed.</summary>
        void IntelligentCreatureEnableRename();
        /// <summary>S.FriendUpdate, after Models.Friends was replaced.</summary>
        void FriendsChanged();
        /// <summary>S.LoverUpdate, after Models.Relationship took the lover's name, date, map and days.</summary>
        void LoverUpdate(S.LoverUpdate p);
        /// <summary>S.MentorUpdate, after Models.Relationship took the mentor's name, level, online state and mentee exp.</summary>
        void MentorUpdate(S.MentorUpdate p);
        /// <summary>S.GameShopInfo added an item dated within the last 7 days (classic: the shop's "new" marker).</summary>
        void GameShopNewItems();
        /// <summary>S.GameShopStock changed (or removed) a GameShopInfoList entry; called once per matching entry, as before.</summary>
        void GameShopStockChanged();
        /// <summary>S.Rankings, after RankingList took the details and Models.Rankings the page (listings = the page in order).</summary>
        void RankingsReceived(List<RankCharacterInfo> listings, S.Rankings p);
        /// <summary>S.GetRentedItems: the user's rented-out items (classic: the item rental window).</summary>
        void RentedItems(S.GetRentedItems p);
        /// <summary>S.ItemRentalRequest: a rental with p.Name starts (Renting = the user rents an item from them).</summary>
        void ItemRentalRequest(S.ItemRentalRequest p);
        /// <summary>S.ItemRentalFee: the partner's fee (classic: kept by the guest rent window).</summary>
        void ItemRentalFee(S.ItemRentalFee p);
        /// <summary>S.ItemRentalPeriod: the partner's rental days (classic: kept by the guest renting window).</summary>
        void ItemRentalPeriod(S.ItemRentalPeriod p);
        /// <summary>S.UpdateRentalItem, after Models.Rental.GuestLoanItem was set.</summary>
        void RentalGuestItemChanged();
        /// <summary>S.CancelItemRental (cancelled = true: also tell the user) / S.ConfirmItemRental, after the user's rental locks were cleared.</summary>
        void ItemRentalReset(bool cancelled);
        /// <summary>S.ItemRentalLock succeeded, after User.RentalGoldLocked / RentalItemLocked were set.</summary>
        void ItemRentalLocked();
        /// <summary>S.ItemRentalPartnerLock: the partner locked their gold or item.</summary>
        void ItemRentalPartnerLock(S.ItemRentalPartnerLock p);
        /// <summary>S.CanConfirmItemRental: both sides locked; the rental can be confirmed.</summary>
        void CanConfirmItemRental();
        /// <summary>S.SetTimer: start or replace an on-screen timer.</summary>
        void SetTimer(S.SetTimer p);
        /// <summary>S.ExpireTimer: remove an on-screen timer.</summary>
        void ExpireTimer(string key);
        /// <summary>S.SetCompass: point the compass at a map location.</summary>
        void SetCompass(Point location);
        /// <summary>S.Roll: show a dice / roll result.</summary>
        void Roll(S.Roll p);
        /// <summary>S.UpdateNotice: the server notice.</summary>
        void ShowNotice(S.UpdateNotice p);
        /// <summary>PlayerObject.MountUpdate: the player has no mount any more (MountType below 0).</summary>
        void CloseMountWindow();
        /// <summary>PlayerObject.FishingUpdate: the player holds no fishing rod.</summary>
        void CloseFishingWindow();
        /// <summary>PlayerObject frame 1 of FishingCast (after the throw sound): the fish button hides.</summary>
        void FishingCast();
        /// <summary>PlayerObject frame 1 of FishingWait, after the splash effects: the fish button shows when a fish bit (and animates faster).</summary>
        void FishingWait(bool foundFish);
        // ---- seam step 4 (sweep) END ----
    }
}

using Client.MirControls;
using Client.MirGraphics;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Dialogs;
using Client.MirScenes.Models;
using Client.MirSounds;
using S = ServerPackets;
using C = ClientPackets;

namespace Client.MirScenes
{
    /// <summary>
    /// The classic UI half of GameScene's packet handlers and game events (Desktop-Modern-UI-Plan seam step 4): the dialog
    /// updates moved verbatim out of GameScene.cs, called only through Classic.ClassicUi. The model half stays in GameScene.cs
    /// and runs first.
    /// </summary>
    public sealed partial class GameScene
    {
        /// <summary>Was GameScene.NPCRequestInput (moved verbatim).</summary>
        internal void ClassicNpcRequestInput(S.NPCRequestInput p)
        {
            MirInputBox inputBox = new MirInputBox(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PleaseEnterRequiredInformation));

            inputBox.OKButton.Click += (o1, e1) =>
            {
                Network.Enqueue(new C.NPCConfirmInput { Value = inputBox.InputTextBox.Text, NPCID = p.NPCID, PageName = p.PageName });
                inputBox.Dispose();
            };
            inputBox.Show();
        }

        internal void ClassicBuffAdded(bool hero, ClientBuff buff)
        {
            (hero ? HeroBuffsDialog : BuffsDialog).CreateBuff(buff);
        }

        internal void ClassicBuffRemoved(bool hero, int index)
        {
            (hero ? HeroBuffsDialog : BuffsDialog).RemoveBuff(index);
        }

        internal void ClassicGuildActiveStatsChanged()
        {
            GuildDialog.UpdateActiveStats();
        }

        internal void ClassicGuildStatusChanging(S.GuildStatus p, bool joined)
        {
            if (joined)
            {
                GuildDialog.NoticeChanged = true;
                GuildDialog.MembersChanged = true;
            }
            if (p.GuildName == "")
            {
                GuildDialog.Hide();
            }
        }

        internal void ClassicGuildStatusChanged(S.GuildStatus p)
        {
            GuildDialog.StatusChanged(p.MyOptions);
        }

        internal void ClassicGuildMembersChanged()
        {
            GuildDialog.UpdateMembers();
        }

        // ---- seam step 4 (sweep) BEGIN: generated, keep in this order ----
        public static Dictionary<int, BigMapRecord> MapInfoList = new Dictionary<int, BigMapRecord>();

        internal void ClassicGameDataRefreshed(bool magicsChanged, bool shopChanged, bool hoverChanged)
        {
            if (magicsChanged)
                for (int i = 0; i < SkillBarDialogs.Count; i++) SkillBarDialogs[i].Update();

            if (shopChanged && GameShopDialog != null && GameShopDialog.Visible) GameShopDialog.UpdateShop();

            // The open tooltip was built from the old values; the next hover builds a new one.
            if (hoverChanged) DisposeItemLabel();
        }

        internal void ClassicMapInfoInvalidated(int mapIndex)
        {
            InvalidateMapInfo(mapIndex);
        }

        /// <summary>Drops a cached big-map record (and its controls); it is requested again when the big map or a dialog needs it.</summary>
        private void InvalidateMapInfo(int mapIndex)
        {
            if (!MapInfoList.TryGetValue(mapIndex, out BigMapRecord record)) return;

            // Unhook the dialog first: its CurrentRecord setter hides the old record's buttons, which must still exist then.
            if (BigMapDialog != null && BigMapDialog.CurrentRecord == record) BigMapDialog.CurrentRecord = null;

            MapInfoList.Remove(mapIndex);
            foreach (MirButton button in record.MovementButtons.Values)
                if (button != null && !button.IsDisposed) button.Dispose();
            record.MovementButtons.Clear();
            for (int i = 0; i < record.NPCButtons.Count; i++)
                if (record.NPCButtons[i] != null && !record.NPCButtons[i].IsDisposed) record.NPCButtons[i].Dispose();
            record.NPCButtons.Clear();

            if (BigMapDialog != null && BigMapDialog.Visible && BigMapDialog.TargetMapIndex == mapIndex)
                Network.Enqueue(new C.RequestMapInfo { MapIndex = mapIndex });
        }

        internal void ClassicWorldMapSetup(WorldMapSetup setup)
        {
            BigMapDialog.WorldMapSetup(setup);
        }

        internal void ClassicMapInfoReceived(S.NewMapInfo info)
        {
            BigMapRecord newRecord = new BigMapRecord() { Index = info.MapIndex, MapInfo = info.Info };
            CreateBigMapButtons(newRecord);
            MapInfoList.Add(info.MapIndex, newRecord);
        }

        private void CreateBigMapButtons(BigMapRecord record)
        {
            record.MovementButtons.Clear();
            record.NPCButtons.Clear();

            foreach (ClientMovementInfo mInfo in record.MapInfo.Movements)
            {
                MirButton button = new MirButton()
                {
                    Library = Libraries.MapLinkIcon,
                    Index = mInfo.Icon,
                    PressedIndex = mInfo.Icon,
                    Sound = SoundList.ButtonA,
                    Parent = BigMapDialog.ViewPort,
                    Location = new Point(20, 38),
                    Hint = mInfo.Title,
                    Visible = false
                };
                button.MouseEnter += (o, e) =>
                {
                    BigMapDialog.MouseLocation = mInfo.Location;
                };

                button.Click += (o, e) =>
                {
                    BigMapDialog.SetTargetMap(mInfo.Destination);
                };
                record.MovementButtons.Add(mInfo, button);
            }

            foreach (ClientNPCInfo npcInfo in record.MapInfo.NPCs)
            {
                BigMapNPCRow row = new BigMapNPCRow(npcInfo) { Parent = BigMapDialog };
                record.NPCButtons.Add(row);
            }
        }

        private void RecreateBigMapButtons()
        {
            foreach (var record in MapInfoList.Values)
                CreateBigMapButtons(record);
        }

        internal void ClassicSearchMapResult(S.SearchMapResult info)
        {
            if (info.MapIndex == -1 && info.NPCIndex == 0)
            {
                MirMessageBox messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.NothingFound), MirMessageBoxButtons.OK);
                messageBox.OKButton.Click += (o, a) => { BigMapDialog.SearchTextBox.SetFocus(); };
                messageBox.Show();
                return;
            }

            BigMapDialog.SetTargetMap(info.MapIndex);
            BigMapDialog.SetTargetNPC(info.NPCIndex);
        }

        internal void ClassicUserLoaded()
        {
            MainDialog.PModeLabel.Visible = User.Class == MirClass.Wizard || User.Class == MirClass.Taoist;
        }

        internal void ClassicHeroBehaviourChanged(HeroBehaviour behaviour)
        {
            HeroBehaviourPanel.UpdateBehaviour(behaviour);
        }

        internal void ClassicUserInformationReceived()
        {
            CharacterDialog = new CharacterDialog(MirGridType.Equipment, User) { Parent = this, Visible = false };
            InventoryDialog.RefreshInventory();
            foreach (SkillBarDialog Bar in SkillBarDialogs)
                Bar.Update();
        }

        internal bool ClassicIsNpcDialogOpen()
        {
            return GameScene.Scene.NPCDialog.Visible;
        }

        internal void ClassicCloseNpcDialog()
        {
            GameScene.Scene.NPCDialog.Hide();
        }

        internal void ClassicPlayerInspected(bool isHero)
        {
            InspectDialog.RefreshInferface(isHero);
            InspectDialog.Show();
        }

        internal void ClassicMountChanged()
        {
            MountDialog.RefreshDialog();
        }

        internal void ClassicFishingUpdated(S.FishingUpdate p)
        {
            FishingStatusDialog.ProgressPercent = p.ProgressPercent;
            FishingStatusDialog.ChancePercent = p.ChancePercent;
            FishingStatusDialog.ChanceLabel.Text = string.Format("{0}%", FishingStatusDialog.ChancePercent);

            if (p.Fishing)
                FishingStatusDialog.Show();
            else
                FishingStatusDialog.Hide();
        }

        internal void ClassicQuestAdded(ClientQuestProgress quest, bool track)
        {
            if (Settings.TrackedQuests.Contains(quest.Id))
            {
                GameScene.Scene.QuestTrackingDialog.AddQuest(quest, true);
            }

            if (track)
            {
                GameScene.Scene.QuestTrackingDialog.AddQuest(quest);
            }
        }

        internal void ClassicQuestRemoved(ClientQuestProgress quest)
        {
            GameScene.Scene.QuestTrackingDialog.RemoveQuest(quest);
        }

        internal void ClassicQuestsChanged()
        {
            GameScene.Scene.QuestTrackingDialog.DisplayQuests();

            if (Scene.QuestListDialog.Visible)
            {
                Scene.QuestListDialog.DisplayInfo();
            }

            if (Scene.QuestLogDialog.Visible)
            {
                Scene.QuestLogDialog.DisplayQuests();
            }
        }

        internal void ClassicLightsChanged()
        {
            switch (Lights)
            {
                case LightSetting.Day:
                case LightSetting.Normal:
                    MiniMapDialog.LightSetting.Index = 2093;
                    break;
                case LightSetting.Dawn:
                    MiniMapDialog.LightSetting.Index = 2095;
                    break;
                case LightSetting.Evening:
                    MiniMapDialog.LightSetting.Index = 2094;
                    break;
                case LightSetting.Night:
                    MiniMapDialog.LightSetting.Index = 2092;
                    break;
            }
        }

        internal void ClassicUserItemDuraChanged(UserItem item)
        {
            if (HoverItem == item)
            {
                DisposeItemLabel();
                CreateItemLabel(item);
            }

            CharacterDuraPanel.UpdateCharacterDura(item);
        }

        internal void ClassicHeroInfoChanged()
        {
            MainDialog.HeroInfoPanel.Update();
        }

        internal void ClassicNpcResponse(S.NPCResponse p)
        {
            NPCDialog.BigButtons.Clear();
            NPCDialog.BigButtonDialog.Hide();
            NPCDialog.NewText(p.Page);

            if (p.Page.Count > 0 || NPCDialog.BigButtons.Count > 0)
                NPCDialog.Show();
            else
                NPCDialog.Hide();

            NPCGoodsDialog.Hide();
            NPCSubGoodsDialog.Hide();
            NPCCraftGoodsDialog.Hide();
            NPCDropDialog.Hide();
            StorageDialog.Hide();
            NPCAwakeDialog.Hide();
            RefineDialog.Hide();
            StorageDialog.Hide();
            TrustMerchantDialog.Hide();
            QuestListDialog.Hide();
        }

        internal void ClassicNpcGoods(S.NPCGoods p)
        {
            if (!NPCDialog.Visible) return;

            switch (NPCPanelType)
            {
                case PanelType.Buy:
                    NPCGoodsDialog.UsePearls = false;
                    NPCGoodsDialog.NewGoods(p.List);
                    NPCGoodsDialog.Show();
                    break;
                case PanelType.BuySub:
                    NPCSubGoodsDialog.UsePearls = false;
                    NPCSubGoodsDialog.NewGoods(p.List);
                    NPCSubGoodsDialog.Show();
                    break;
                case PanelType.Craft:
                    NPCCraftGoodsDialog.UsePearls = false;
                    NPCCraftGoodsDialog.NewGoods(p.List);
                    NPCCraftGoodsDialog.Show();
                    CraftDialog.Show();
                    break;
            }
        }

        internal void ClassicNpcPearlGoods(S.NPCPearlGoods p)
        {
            if (!NPCDialog.Visible) return;

            NPCGoodsDialog.UsePearls = true;
            NPCGoodsDialog.NewGoods(p.List);
            NPCGoodsDialog.Show();
        }

        internal void ClassicNpcDropPanel(PanelType type)
        {
            if (!NPCDialog.Visible) return;
            NPCDropDialog.PType = type;
            NPCDropDialog.Show();
        }

        internal void ClassicNpcStorage()
        {
            if (NPCDialog.Visible)
                StorageDialog.Show();
        }

        internal void ClassicStorageUnlockResult(S.StorageUnlockResult p)
        {
            StorageDialog?.HandleStorageUnlockResult(p);
        }

        internal void ClassicStoragePasswordResult(S.StoragePasswordResult p)
        {
            StorageDialog?.HandleStoragePasswordResult(p);
        }

        internal void ClassicNpcRefine(bool refining)
        {
            if (!NPCDialog.Visible) return;
            NPCDropDialog.PType = PanelType.Refine;
            if (refining)
            {
                NPCDropDialog.Hide();
                NPCDialog.Hide();
            }
            else
                NPCDropDialog.Show();
        }

        internal void ClassicNpcCollectRefine()
        {
            if (!NPCDialog.Visible) return;
            NPCDialog.Hide();
        }

        internal void ClassicCraftItemDone()
        {
            CraftDialog.UpdateCraftCells();
        }

        internal void ClassicItemUpgraded(MirGridType grid, UserItem item)
        {
            switch (grid)
            {
                case MirGridType.Inventory:
                    InventoryDialog.DisplayItemGridEffect(item.UniqueID, 0);
                    break;
                case MirGridType.HeroInventory:
                    HeroInventoryDialog.DisplayItemGridEffect(item.UniqueID, 0);
                    break;
            }


            if (HoverItem == item)
            {
                DisposeItemLabel();
                CreateItemLabel(item);
            }
        }

        internal void ClassicSkillBarsChanged()
        {
            foreach (SkillBarDialog Bar in SkillBarDialogs)
            {
                Bar.Update();
            }
        }

        internal void ClassicGroupInvite(S.GroupInvite p)
        {
            MirMessageBox messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization((ClientTextKeys.DoYouWantGroupWithPlayer), p.Name), MirMessageBoxButtons.YesNo);

            messageBox.YesButton.Click += (o, e) =>
            {
                Network.Enqueue(new C.GroupInvite { AcceptInvite = true });
                GroupDialog.Show();
            };
            messageBox.NoButton.Click += (o, e) => Network.Enqueue(new C.GroupInvite { AcceptInvite = false });
            messageBox.Show();
        }

        internal void ClassicItemInfoChanged(UserItem item)
        {
            if (HoverItem == item)
            {
                DisposeItemLabel();
                CreateItemLabel(item);
            }
        }

        internal void ClassicMarketOpening()
        {
            TrustMerchantDialog.Show();
        }

        internal void ClassicMarketListed(int pages)
        {
            TrustMerchantDialog.Page = 0;
            TrustMerchantDialog.PageCount = pages;
            TrustMerchantDialog.UpdateInterface();
        }

        internal bool ClassicIsMarketOpen()
        {
            return TrustMerchantDialog.Visible;
        }

        internal void ClassicMarketPageAdded()
        {
            TrustMerchantDialog.Page = (Models.Market.Listings.Count - 1) / 10;
            TrustMerchantDialog.UpdateInterface();
        }

        internal void ClassicGuildNoticeChange(S.GuildNoticeChange p)
        {
            if (p.update == -1)
                GuildDialog.NoticeChanged = true;
            else
                GuildDialog.NoticeChange(p.notice);
        }

        internal void ClassicGuildMemberChange(S.GuildMemberChange p)
        {
            switch (p.Status)
            {
                case 0: // logged of
                    GuildDialog.MemberStatusChange(p.Name, false);
                    break;
                case 1: // logged on
                    GuildDialog.MemberStatusChange(p.Name, true);
                    break;
                case 2: //new member
                    GuildDialog.MembersChanged = true;
                    break;
                case 3: //kicked member
                    GuildDialog.MembersChanged = true;
                    break;
                case 4: //member left
                    GuildDialog.MembersChanged = true;
                    break;
                case 5://rank change (name or different rank)
                    GuildDialog.MembersChanged = true;
                    break;
                case 6: //new rank
                    if (p.Ranks.Count > 0)
                        GuildDialog.NewRankRecieved(p.Ranks[0]);
                    break;
                case 7: //rank option changed
                    if (p.Ranks.Count > 0)
                        GuildDialog.RankChangeRecieved(p.Ranks[0]);
                    break;
                case 8: //my rank changed
                    if (p.Ranks.Count > 0)
                        GuildDialog.MyRankChanged(p.Ranks[0]);
                    break;
                case 255:
                    GuildDialog.NewMembersList(p.Ranks);
                    break;
            }
        }

        internal void ClassicGuildTerritoryPage(S.GuildTerritoryPage p)
        {
            if (!GuildTerritoryDialog.Visible)
            {
                GuildTerritoryDialog.Show();
            }

            GuildTerritoryDialog.GTMapList = p.Listings;
            GuildTerritoryDialog.Lenght = p.length;
            GuildTerritoryDialog.UpdateInterface();
        }

        internal void ClassicHeroCreateRequest(S.HeroCreateRequest p)
        {
            NewHeroDialog.WarriorButton.Visible = p.CanCreateClass[(int)MirClass.Warrior];
            NewHeroDialog.WizardButton.Visible = p.CanCreateClass[(int)MirClass.Wizard];
            NewHeroDialog.TaoistButton.Visible = p.CanCreateClass[(int)MirClass.Taoist];
            NewHeroDialog.AssassinButton.Visible = p.CanCreateClass[(int)MirClass.Assassin];
            NewHeroDialog.ArcherButton.Visible = p.CanCreateClass[(int)MirClass.Archer];

            NewHeroDialog.Show();
        }

        internal void ClassicManageHeroes(ClientHeroInformation currentHero)
        {
            HeroManageDialog.SetCurrentHero(currentHero);
            HeroManageDialog.Show();
        }

        internal void ClassicChangeHero(S.ChangeHero p)
        {
            ClientHeroInformation temp = HeroStorage[p.FromIndex];
            HeroStorage[p.FromIndex] = HeroManageDialog.CurrentAvatar.Info;
            HeroManageDialog.SetCurrentHero(temp);
            HeroManageDialog.RefreshInterface();
        }

        internal void ClassicHeroAutoPotChanged()
        {
            HeroInventoryDialog.RefreshInterface();
        }

        internal void ClassicNewHeroResult(S.NewHero p)
        {
            NewHeroDialog.OKButton.Enabled = true;

            switch (p.Result)
            {
                case 0:
                    MirMessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.CreatingNewHeroesDisabled));
                    NewHeroDialog.Hide();
                    break;
                case 1:
                    MirMessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.YourHeroNameNotAcceptable));
                    NewHeroDialog.NameTextBox.SetFocus();
                    break;
                case 2:
                    MirMessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.SelectedGenderNotExist));
                    break;
                case 3:
                    MirMessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.SelectedClassNotExist));
                    break;
                case 4:
                    MirMessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.CannotMakeMoreHeroes));
                    NewHeroDialog.Hide();
                    break;
                case 5:
                    MirMessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.CharacterNameAlreadyExists));
                    NewHeroDialog.NameTextBox.SetFocus();
                    break;
                case 6:
                    MirMessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.NoBagSpace));
                    NewHeroDialog.Hide();
                    break;
                case 10:
                    MirMessageBox.Show(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.HeroCreatedSuccessfully));
                    NewHeroDialog.Hide();
                    break;
            }
        }

        internal void ClassicHeroInformationReceived()
        {
            HeroDialog = new CharacterDialog(MirGridType.HeroEquipment, Hero) { Parent = this, Visible = false };
            HeroInventoryDialog = new HeroInventoryDialog { Parent = this };
            HeroBeltDialog = new HeroBeltDialog { Parent = this };
            HeroBuffsDialog = new BuffDialog
            {
                Parent = this,
                Visible = true,
                Buffs = Models.HeroBuffs.Items,
                Location = new Point(Settings.ScreenWidth - 170, 80),
                GetExpandedParameter = () => { return Settings.ExpandedHeroBuffWindow; },
                SetExpandedParameter = (value) => { Settings.ExpandedHeroBuffWindow = value; }
            };
            MainDialog.HeroInfoPanel.Update();
        }

        internal void ClassicHeroSpawnStateChanged(HeroSpawnState state)
        {
            MainDialog.HeroInfoPanel.Visible = state > HeroSpawnState.Unsummoned;
            MainDialog.HeroMenuButton.Visible = state > HeroSpawnState.Unsummoned;
            HeroBehaviourPanel.Visible = state > HeroSpawnState.Unsummoned;
            HeroMenuPanel.Visible = HeroMenuPanel.Visible && MainDialog.HeroMenuButton.Visible;

            if (state < HeroSpawnState.Summoned)
            {
                HeroInventoryDialog.Dispose();
                HeroDialog.Dispose();
                HeroBeltDialog.Dispose();
                HeroBuffsDialog.Dispose();
            }
        }

        internal void ClassicTradeAccepted()
        {
            TradeDialog.TradeAccept();
        }

        internal void ClassicTradeGuestChanged()
        {
            TradeDialog.ChangeLockState(false);
            TradeDialog.RefreshInterface();
        }

        internal void ClassicTradeConfirmed()
        {
            TradeDialog.TradeReset();
        }

        internal void ClassicTradeCancelled(bool unlock)
        {
            if (unlock)
            {
                TradeDialog.ChangeLockState(false);
            }
            else
            {
                TradeDialog.TradeReset();

                MirMessageBox messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DealCancelledMustFace), MirMessageBoxButtons.OK);
                messageBox.Show();
            }
        }

        internal void ClassicNpcAwakening()
        {
            if (NPCAwakeDialog.Visible != true)
                NPCAwakeDialog.Show();
        }

        internal void ClassicAwakeningNeedMaterials(S.AwakeningNeedMaterials p)
        {
            NPCAwakeDialog.setNeedItems(p.Materials, p.MaterialsCount);
        }

        internal void ClassicMailReceived()
        {
            GameScene.Scene.MailListDialog.UpdateInterface();
        }

        internal void ClassicMailSendRequest()
        {
            MirInputBox inputBox = new MirInputBox(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.EnterMailRecipientName));

            inputBox.OKButton.Click += (o1, e1) =>
            {
                GameScene.Scene.MailComposeParcelDialog.ComposeMail(inputBox.InputTextBox.Text);
                GameScene.Scene.InventoryDialog.Show();

                //open letter dialog, pass in name
                inputBox.Dispose();
            };

            inputBox.Show();
        }

        internal void ClassicParcelCollected(sbyte result)
        {
            switch (result)
            {
                case -1:
                    MirMessageBox messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.NoParcelsToCollect), MirMessageBoxButtons.OK);
                    messageBox.Show();
                    break;
                case 0:
                    messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.AllParcelsCollected), MirMessageBoxButtons.OK);
                    messageBox.Show();
                    break;
                case 1:
                    GameScene.Scene.MailReadParcelDialog.Hide();
                    break;
            }
        }

        internal void ClassicInventoryResized()
        {
            InventoryDialog.RefreshInventory2();
        }

        internal void ClassicStorageResized()
        {
            StorageDialog.RefreshStorage2();
        }

        internal void ClassicMailCost(uint cost)
        {
            if (GameScene.Scene.MailComposeParcelDialog.Visible)
            {
                if (cost > 0)
                    SoundManager.PlaySound(SoundList.Gold);

                GameScene.Scene.MailComposeParcelDialog.ParcelCostLabel.Text = cost.ToString();
            }
        }

        internal void ClassicNewIntelligentCreature()
        {
            MirInputBox inputBox = new MirInputBox(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PleaseGiveYourCreatureName));
            inputBox.InputTextBox.Text = GameScene.User.IntelligentCreatures[User.IntelligentCreatures.Count - 1].CustomName;
            inputBox.OKButton.Click += (o1, e1) =>
            {
                if (IntelligentCreatureDialog.Visible) IntelligentCreatureDialog.Update();//refresh changes
                GameScene.User.IntelligentCreatures[User.IntelligentCreatures.Count - 1].CustomName = inputBox.InputTextBox.Text;
                Network.Enqueue(new C.UpdateIntelligentCreature { Creature = GameScene.User.IntelligentCreatures[User.IntelligentCreatures.Count - 1] });
                inputBox.Dispose();
            };
            inputBox.Show();
        }

        internal void ClassicIntelligentCreaturesChanged(bool listReplaced)
        {
            if (listReplaced)
            {
                for (int i = 0; i < IntelligentCreatureDialog.CreatureButtons.Length; i++)
                    IntelligentCreatureDialog.CreatureButtons[i].Clear();

                IntelligentCreatureDialog.Hide();
            }
            else
            {
                if (IntelligentCreatureDialog.Visible) IntelligentCreatureDialog.Update();
            }
        }

        internal void ClassicIntelligentCreatureEnableRename()
        {
            IntelligentCreatureDialog.CreatureRenameButton.Visible = true;
            if (IntelligentCreatureDialog.Visible) IntelligentCreatureDialog.Update();
        }

        internal void ClassicFriendsChanged()
        {
            if (FriendDialog.Visible)
                FriendDialog.Update(false);
        }

        internal void ClassicLoverUpdate(S.LoverUpdate p)
        {
            RelationshipDialog.LoverName = p.Name;
            RelationshipDialog.Date = p.Date;
            RelationshipDialog.MapName = p.MapName;
            RelationshipDialog.MarriedDays = p.MarriedDays;
            RelationshipDialog.UpdateInterface();
        }

        internal void ClassicMentorUpdate(S.MentorUpdate p)
        {
            MentorDialog.MentorName = p.Name;
            MentorDialog.MentorLevel = p.Level;
            MentorDialog.MentorOnline = p.Online;
            MentorDialog.MenteeEXP = p.MenteeEXP;
            MentorDialog.UpdateInterface();
        }

        internal void ClassicGameShopNewItems()
        {
            GameShopDialog.New.Visible = true;
        }

        internal void ClassicGameShopStockChanged()
        {
            if (GameShopDialog.Visible) GameShopDialog.UpdateShop();
        }

        internal void ClassicRankingsReceived(List<RankCharacterInfo> listings, S.Rankings p)
        {
            RankingDialog.RecieveRanks(listings, p.RankType, p.MyRank, p.Count);
        }

        internal void ClassicRentedItems(S.GetRentedItems p)
        {
            ItemRentalDialog.ReceiveRentedItems(p.RentedItems);
        }

        internal void ClassicItemRentalRequest(S.ItemRentalRequest p)
        {
            if (!p.Renting)
            {
                GuestItemRentDialog.SetGuestName(p.Name);
                ItemRentingDialog.OpenItemRentalDialog();
            }
            else
            {
                GuestItemRentingDialog.SetGuestName(p.Name);
                ItemRentDialog.OpenItemRentDialog();
            }

            ItemRentalDialog.Visible = false;
        }

        internal void ClassicItemRentalFee(S.ItemRentalFee p)
        {
            GuestItemRentDialog.SetGuestFee(p.Amount);
            ItemRentDialog.RefreshInterface();
        }

        internal void ClassicItemRentalPeriod(S.ItemRentalPeriod p)
        {
            GuestItemRentingDialog.GuestRentalPeriod = p.Days;
            ItemRentingDialog.RefreshInterface();
        }

        internal void ClassicRentalGuestItemChanged()
        {
            ItemRentDialog.RefreshInterface();
        }

        internal void ClassicItemRentalReset(bool cancelled)
        {
            ItemRentingDialog.Reset();
            ItemRentDialog.Reset();

            if (!cancelled) return;

            var messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ItemRentalCancelledFaceOtherParty));
            messageBox.Show();
        }

        internal void ClassicItemRentalLocked()
        {
            if (User.RentalGoldLocked)
                ItemRentDialog.Lock();
            else if (User.RentalItemLocked)
                ItemRentingDialog.Lock();
        }

        internal void ClassicItemRentalPartnerLock(S.ItemRentalPartnerLock p)
        {
            if (p.GoldLocked)
                GuestItemRentDialog.Lock();
            else if (p.ItemLocked)
                GuestItemRentingDialog.Lock();
        }

        internal void ClassicCanConfirmItemRental()
        {
            ItemRentingDialog.EnableConfirmButton();
        }

        internal void ClassicSetTimer(S.SetTimer p)
        {
            GameScene.Scene.TimerControl.AddTimer(p);
        }

        internal void ClassicExpireTimer(string key)
        {
            GameScene.Scene.TimerControl.ExpireTimer(key);
        }

        internal void ClassicSetCompass(Point location)
        {
            GameScene.Scene.CompassControl.SetPoint(location);
        }

        internal void ClassicRoll(S.Roll p)
        {
            GameScene.Scene.RollControl.Setup(p.Type, p.Page, p.Result, p.AutoRoll);
        }

        internal void ClassicShowNotice(S.UpdateNotice p)
        {
            NoticeDialog.Update(p.Notice);
        }

        internal void ClassicCloseMountWindow()
        {
            GameScene.Scene.MountDialog.Hide();
        }

        internal void ClassicCloseFishingWindow()
        {
            GameScene.Scene.FishingDialog.Hide();
        }

        internal void ClassicFishingCast()
        {
            ((MirAnimatedButton)GameScene.Scene.FishingStatusDialog.FishButton).Visible = false;
        }

        internal void ClassicFishingWait(bool foundFish)
        {
            if (foundFish)
            {
                ((MirAnimatedButton)GameScene.Scene.FishingStatusDialog.FishButton).Visible = true;
            }
            else
            {
                ((MirAnimatedButton)GameScene.Scene.FishingStatusDialog.FishButton).Visible = false;
            }

            ((MirAnimatedButton)GameScene.Scene.FishingStatusDialog.FishButton).AnimationCount = foundFish ? 10 : 1;
        }
        // ---- seam step 4 (sweep) END ----
    }
}

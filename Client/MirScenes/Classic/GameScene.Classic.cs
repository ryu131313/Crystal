using Client.MirControls;
using Client.MirGraphics;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Dialogs;
using C = ClientPackets;

namespace Client.MirScenes
{
    /// <summary>
    /// The classic UI half of GameScene (Desktop-Modern-UI-Plan seam step 4): the classic dialog fields and the code moved
    /// verbatim out of GameScene.cs that builds and drives them. Only Classic.ClassicUi calls into this file; in modern mode
    /// none of these fields is ever set. tools\modern-ui-gate.ps1 keeps these names out of every other non-classic file.
    /// </summary>
    public sealed partial class GameScene
    {
        public MainDialog MainDialog;
        public ChatDialog ChatDialog;
        public ChatControlBar ChatControl;
        public InventoryDialog InventoryDialog;
        public CharacterDialog CharacterDialog;
        public CharacterDialog HeroDialog;
        public HeroInventoryDialog HeroInventoryDialog;
        public HeroManageDialog HeroManageDialog;
        public CraftDialog CraftDialog;
        public StorageDialog StorageDialog;
        public BeltDialog BeltDialog;
        public MiniMapDialog MiniMapDialog;
        public InspectDialog InspectDialog;
        public OptionDialog OptionDialog;
        public MenuDialog MenuDialog;
        public NPCDialog NPCDialog;
        public NPCGoodsDialog NPCGoodsDialog;
        public NPCGoodsDialog NPCSubGoodsDialog;
        public NPCGoodsDialog NPCCraftGoodsDialog;
        public NPCDropDialog NPCDropDialog;
        public NPCAwakeDialog NPCAwakeDialog;
        public HelpDialog HelpDialog;
        public MountDialog MountDialog;
        public FishingDialog FishingDialog;
        public FishingStatusDialog FishingStatusDialog;
        public RefineDialog RefineDialog;

        public GroupDialog GroupDialog;
        public GuildDialog GuildDialog;
        public GuildTerritoryDialog GuildTerritoryDialog;
        public NewCharacterDialog NewHeroDialog;
        public HeroBeltDialog HeroBeltDialog;

        public BigMapDialog BigMapDialog;
        public TrustMerchantDialog TrustMerchantDialog;
        public CharacterDuraPanel CharacterDuraPanel;
        public DuraStatusDialog DuraStatusPanel;
        public TradeDialog TradeDialog;
        public GuestTradeDialog GuestTradeDialog;

        public HeroMenuPanel HeroMenuPanel;
        public HeroBehaviourPanel HeroBehaviourPanel;
        public SocketDialog SocketDialog;

        public List<SkillBarDialog> SkillBarDialogs = new List<SkillBarDialog>();
        public ChatOptionDialog ChatOptionDialog;
        public ChatNoticeDialog ChatNoticeDialog;

        public QuestListDialog QuestListDialog;
        public QuestDetailDialog QuestDetailDialog;
        public QuestDiaryDialog QuestLogDialog;
        public QuestTrackingDialog QuestTrackingDialog;

        public RankingDialog RankingDialog;

        public MailListDialog MailListDialog;
        public MailComposeLetterDialog MailComposeLetterDialog;
        public MailComposeParcelDialog MailComposeParcelDialog;
        public MailReadLetterDialog MailReadLetterDialog;
        public MailReadParcelDialog MailReadParcelDialog;

        public IntelligentCreatureDialog IntelligentCreatureDialog;
        public IntelligentCreatureOptionsDialog IntelligentCreatureOptionsDialog;
        public IntelligentCreatureOptionsGradeDialog IntelligentCreatureOptionsGradeDialog;

        public FriendDialog FriendDialog;
        public MemoDialog MemoDialog;
        public RelationshipDialog RelationshipDialog;
        public MentorDialog MentorDialog;
        public GameShopDialog GameShopDialog;

        public ReportDialog ReportDialog;

        public ItemRentingDialog ItemRentingDialog;
        public ItemRentDialog ItemRentDialog;
        public GuestItemRentingDialog GuestItemRentingDialog;
        public GuestItemRentDialog GuestItemRentDialog;
        public ItemRentalDialog ItemRentalDialog;

        public BuffDialog BuffsDialog;
        public BuffDialog HeroBuffsDialog;

        public KeyboardLayoutDialog KeyboardLayoutDialog;
        public NoticeDialog NoticeDialog;

        public TimerDialog TimerControl;
        public CompassDialog CompassControl;
        public RollDialog RollControl;

        public MirControl ItemLabel, MailLabel, MemoLabel, GuildBuffLabel;

        /// <summary>Was the dialog half of the GameScene constructor (same order).</summary>
        internal void ClassicBuild()
        {
            MainDialog = new MainDialog { Parent = this };
            ChatDialog = new ChatDialog { Parent = this };
            ChatControl = new ChatControlBar { Parent = this };
            InventoryDialog = new InventoryDialog { Parent = this };
            BeltDialog = new BeltDialog { Parent = this };
            StorageDialog = new StorageDialog { Parent = this, Visible = false };
            CraftDialog = new CraftDialog { Parent = this, Visible = false };
            MiniMapDialog = new MiniMapDialog { Parent = this };
            InspectDialog = new InspectDialog { Parent = this, Visible = false };
            OptionDialog = new OptionDialog { Parent = this, Visible = false };
            MenuDialog = new MenuDialog { Parent = this, Visible = false };
            NPCDialog = new NPCDialog { Parent = this, Visible = false };
            NPCGoodsDialog = new NPCGoodsDialog(PanelType.Buy) { Parent = this, Visible = false };
            NPCSubGoodsDialog = new NPCGoodsDialog(PanelType.BuySub) { Parent = this, Visible = false };
            NPCCraftGoodsDialog = new NPCGoodsDialog(PanelType.Craft) { Parent = this, Visible = false };
            NPCDropDialog = new NPCDropDialog { Parent = this, Visible = false };
            NPCAwakeDialog = new NPCAwakeDialog { Parent = this, Visible = false, Location = new Point(0, GameScene.Scene.NPCDialog.Size.Height) };

            HelpDialog = new HelpDialog { Parent = this, Visible = false };
            KeyboardLayoutDialog = new KeyboardLayoutDialog { Parent = this, Visible = false };
            NoticeDialog = new NoticeDialog { Parent = this, Visible = false };

            MountDialog = new MountDialog { Parent = this, Visible = false };
            FishingDialog = new FishingDialog { Parent = this, Visible = false };
            FishingStatusDialog = new FishingStatusDialog { Parent = this, Visible = false };

            GroupDialog = new GroupDialog { Parent = this, Visible = false };
            GuildDialog = new GuildDialog { Parent = this, Visible = false };
            GuildTerritoryDialog = new GuildTerritoryDialog { Parent = this, Visible = false };
            NewHeroDialog = new NewCharacterDialog { Parent = this, Visible = false };
            NewHeroDialog.TitleLabel.Index = 847;
            NewHeroDialog.TitleLabel.Location = new Point(246, 11);
            NewHeroDialog.OnCreateCharacter += (o, e) =>
            {
                Network.Enqueue(new C.NewHero
                {
                    Name = NewHeroDialog.NameTextBox.Text,
                    Class = NewHeroDialog.Class,
                    Gender = NewHeroDialog.Gender
                });
            };

            HeroMenuPanel = new HeroMenuPanel(this) { Visible = false };
            HeroBehaviourPanel = new HeroBehaviourPanel { Parent = this, Visible = false };
            HeroManageDialog = new HeroManageDialog { Parent = this, Visible = false };

            BigMapDialog = new BigMapDialog { Parent = this, Visible = false };
            TrustMerchantDialog = new TrustMerchantDialog { Parent = this, Visible = false };
            CharacterDuraPanel = new CharacterDuraPanel { Parent = this, Visible = false };
            DuraStatusPanel = new DuraStatusDialog { Parent = this, Visible = true };
            TradeDialog = new TradeDialog { Parent = this, Visible = false };
            GuestTradeDialog = new GuestTradeDialog { Parent = this, Visible = false };

            SocketDialog = new SocketDialog { Parent = this, Visible = false };

            SkillBarDialog Bar1 = new SkillBarDialog { Parent = this, Visible = false, BarIndex = 0 };
            SkillBarDialogs.Add(Bar1);
            SkillBarDialog Bar2 = new SkillBarDialog { Parent = this, Visible = false, BarIndex = 1 };
            SkillBarDialogs.Add(Bar2);

            // Cooldown sweeps and minimap dots change many times a second; as live overlays they re-render only themselves.
            AddLiveOverlay(Bar1);
            AddLiveOverlay(Bar2);
            AddLiveOverlay(MiniMapDialog);
            ChatOptionDialog = new ChatOptionDialog { Parent = this, Visible = false };
            ChatNoticeDialog = new ChatNoticeDialog { Parent = this, Visible = false };

            QuestListDialog = new QuestListDialog { Parent = this, Visible = false };
            QuestDetailDialog = new QuestDetailDialog { Parent = this, Visible = false };
            QuestTrackingDialog = new QuestTrackingDialog { Parent = this, Visible = false };
            QuestLogDialog = new QuestDiaryDialog { Parent = this, Visible = false };

            RankingDialog = new RankingDialog { Parent = this, Visible = false };

            MailListDialog = new MailListDialog { Parent = this, Visible = false };
            MailComposeLetterDialog = new MailComposeLetterDialog { Parent = this, Visible = false };
            MailComposeParcelDialog = new MailComposeParcelDialog { Parent = this, Visible = false };
            MailReadLetterDialog = new MailReadLetterDialog { Parent = this, Visible = false };
            MailReadParcelDialog = new MailReadParcelDialog { Parent = this, Visible = false };

            IntelligentCreatureDialog = new IntelligentCreatureDialog { Parent = this, Visible = false };
            IntelligentCreatureOptionsDialog = new IntelligentCreatureOptionsDialog { Parent = this, Visible = false };
            IntelligentCreatureOptionsGradeDialog = new IntelligentCreatureOptionsGradeDialog { Parent = this, Visible = false };

            RefineDialog = new RefineDialog { Parent = this, Visible = false };
            RelationshipDialog = new RelationshipDialog { Parent = this, Visible = false };
            FriendDialog = new FriendDialog { Parent = this, Visible = false };
            MemoDialog = new MemoDialog { Parent = this, Visible = false };
            MentorDialog = new MentorDialog { Parent = this, Visible = false };
            GameShopDialog = new GameShopDialog { Parent = this, Visible = false };
            ReportDialog = new ReportDialog { Parent = this, Visible = false };

            ItemRentingDialog = new ItemRentingDialog { Parent = this, Visible = false };
            ItemRentDialog = new ItemRentDialog { Parent = this, Visible = false };
            GuestItemRentingDialog = new GuestItemRentingDialog { Parent = this, Visible = false };
            GuestItemRentDialog = new GuestItemRentDialog { Parent = this, Visible = false };
            ItemRentalDialog = new ItemRentalDialog { Parent = this, Visible = false };

            BuffsDialog = new BuffDialog
            {
                Parent = this,
                Visible = true,
                Buffs = Models.Buffs.Items,
                GetExpandedParameter = () => { return Settings.ExpandedBuffWindow; },
                SetExpandedParameter = (value) => { Settings.ExpandedBuffWindow = value; }
            };

            KeyboardLayoutDialog = new KeyboardLayoutDialog { Parent = this, Visible = false };

            TimerControl = new TimerDialog { Parent = this, Visible = false };
            CompassControl = new CompassDialog { Parent = this, Visible = false };
            RollControl = new RollDialog { Parent = this, Visible = false };
        }
    }
}

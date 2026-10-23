using Client.MirControls;
using Client.MirGraphics;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Dialogs;
using Client.MirSounds;
using Font = System.Drawing.Font;
using S = ServerPackets;
using C = ClientPackets;

namespace Client.MirScenes
{
    /// <summary>
    /// The classic UI half of GameScene's frame work (Desktop-Modern-UI-Plan seam step 4): output lines, per-frame dialog work,
    /// window keybinds and disposal, moved verbatim out of GameScene.cs and called only through Classic.ClassicUi.
    /// </summary>
    public sealed partial class GameScene
    {
        public MirLabel[] OutputLines = new MirLabel[10];

        /// <summary>The end of the GameScene constructor, after ClassicBuild (same order as before the seam).</summary>
        internal void ClassicBuildLate()
        {
            for (int i = 0; i < OutputLines.Length; i++)
                OutputLines[i] = new MirLabel
                {
                    AutoSize = true,
                    BackColour = Color.Transparent,
                    Font = new Font(Settings.FontName, 10F),
                    ForeColour = Color.LimeGreen,
                    Location = new Point(20, 25 + i * 13),
                    OutLine = true,
                };

            if (MapInfoList.Count > 0)
                RecreateBigMapButtons();
        }

        internal void ClassicDrawOutputLines()
        {
            for (int i = 0; i < OutputLines.Length; i++)
                OutputLines[i].Draw();
        }

        internal void ClassicHasHeroChanged(bool value)
        {
            MainDialog.HeroSummonButton.Visible = value;
        }

        internal bool ClassicCapturesKey(KeyEventArgs e)
        {
            if (GameScene.Scene.KeyboardLayoutDialog.WaitingForBind != null)
            {
                GameScene.Scene.KeyboardLayoutDialog.CheckNewInput(e);
                return true;
            }

            return false;
        }

        internal void ClassicKeyBind(KeybindOptions function)
        {
            switch (function)
            {
                case KeybindOptions.Inventory:
                case KeybindOptions.Inventory2:
                    if (!InventoryDialog.Visible) InventoryDialog.Show();
                    else InventoryDialog.Hide();
                    break;
                case KeybindOptions.Equipment:
                case KeybindOptions.Equipment2:
                    if (!CharacterDialog.Visible || !CharacterDialog.CharacterPage.Visible)
                    {
                        CharacterDialog.Show();
                        CharacterDialog.ShowCharacterPage();
                    }
                    else CharacterDialog.Hide();
                    break;
                case KeybindOptions.Skills:
                case KeybindOptions.Skills2:
                    if (!CharacterDialog.Visible || !CharacterDialog.SkillPage.Visible)
                    {
                        CharacterDialog.Show();
                        CharacterDialog.ShowSkillPage();
                    }
                    else CharacterDialog.Hide();
                    break;
                case KeybindOptions.HeroInventory:
                    if (Hero == null)
                        break;
                    if (!HeroInventoryDialog.Visible) HeroInventoryDialog.Show();
                    else HeroInventoryDialog.Hide();
                    break;
                case KeybindOptions.HeroEquipment:
                    if (Hero == null)
                        break;
                    if (!HeroDialog.Visible || !HeroDialog.CharacterPage.Visible)
                    {
                        HeroDialog.Show();
                        HeroDialog.ShowCharacterPage();
                    }
                    else HeroDialog.Hide();
                    break;
                case KeybindOptions.HeroSkills:
                    if (Hero == null)
                        break;
                    if (!HeroDialog.Visible || !HeroDialog.SkillPage.Visible)
                    {
                        HeroDialog.Show();
                        HeroDialog.ShowSkillPage();
                    }
                    else HeroDialog.Hide();
                    break;
                case KeybindOptions.Creature:
                    if (!IntelligentCreatureDialog.Visible) IntelligentCreatureDialog.Show();
                    else IntelligentCreatureDialog.Hide();
                    break;
                case KeybindOptions.MountWindow:
                    if (!MountDialog.Visible) MountDialog.Show();
                    else MountDialog.Hide();
                    break;

                case KeybindOptions.GameShop:
                    if (!GameShopDialog.Visible) GameShopDialog.Show();
                    else GameShopDialog.Hide();
                    break;
                case KeybindOptions.Fishing:
                    if (!FishingDialog.Visible) FishingDialog.Show();
                    else FishingDialog.Hide();
                    break;
                case KeybindOptions.Skillbar:
                    if (!Settings.SkillBar)
                        foreach (SkillBarDialog Bar in SkillBarDialogs)
                            Bar.Show();
                    else
                        foreach (SkillBarDialog Bar in SkillBarDialogs)
                            Bar.Hide();
                    break;
                case KeybindOptions.Mount:
                    if (GameScene.Scene.MountDialog.CanRide())
                        GameScene.Scene.MountDialog.Ride();
                    break;
                case KeybindOptions.Mentor:
                    if (!MentorDialog.Visible) MentorDialog.Show();
                    else MentorDialog.Hide();
                    break;
                case KeybindOptions.Relationship:
                    if (!RelationshipDialog.Visible) RelationshipDialog.Show();
                    else RelationshipDialog.Hide();
                    break;
                case KeybindOptions.Friends:
                    if (!FriendDialog.Visible) FriendDialog.Show();
                    else FriendDialog.Hide();
                    break;
                case KeybindOptions.Guilds:
                    if (!GuildDialog.Visible) GuildDialog.Show();
                    else
                    {
                        GuildDialog.Hide();
                    }
                    break;

                case KeybindOptions.Ranking:
                    if (!RankingDialog.Visible) RankingDialog.Show();
                    else RankingDialog.Hide();
                    break;
                case KeybindOptions.Quests:
                    if (!QuestLogDialog.Visible) QuestLogDialog.Show();
                    else QuestLogDialog.Hide();
                    break;

                case KeybindOptions.Closeall:
                    InventoryDialog.Hide();
                    CharacterDialog.Hide();
                    OptionDialog.Hide();
                    MenuDialog.Hide();
                    if (NPCDialog.Visible) NPCDialog.Hide();
                    HelpDialog.Hide();
                    KeyboardLayoutDialog.Hide();
                    RankingDialog.Hide();
                    IntelligentCreatureDialog.Hide();
                    IntelligentCreatureOptionsDialog.Hide();
                    IntelligentCreatureOptionsGradeDialog.Hide();
                    MountDialog.Hide();
                    FishingDialog.Hide();
                    FriendDialog.Hide();
                    RelationshipDialog.Hide();
                    MentorDialog.Hide();
                    GameShopDialog.Hide();
                    GroupDialog.Hide();
                    GuildDialog.Hide();
                    InspectDialog.Hide();
                    StorageDialog.Hide();
                    TrustMerchantDialog.Hide();
                    //CharacterDuraPanel.Hide();
                    QuestListDialog.Hide();
                    QuestDetailDialog.Hide();
                    QuestLogDialog.Hide();
                    NPCAwakeDialog.Hide();
                    RefineDialog.Hide();
                    BigMapDialog.Hide();
                    if (FishingStatusDialog.bEscExit) FishingStatusDialog.Cancel();
                    MailComposeLetterDialog.Hide();
                    MailComposeParcelDialog.Hide();
                    MailListDialog.Hide();
                    MailReadLetterDialog.Hide();
                    MailReadParcelDialog.Hide();
                    ItemRentalDialog.Hide();
                    NoticeDialog.Hide();
                    HeroInventoryDialog?.Hide();
                    HeroManageDialog?.Hide();
                    HeroDialog?.Hide();

                    GameScene.Scene.DisposeItemLabel();
                    break;
                case KeybindOptions.Options:
                case KeybindOptions.Options2:
                    if (!OptionDialog.Visible) OptionDialog.Show();
                    else OptionDialog.Hide();
                    break;
                case KeybindOptions.Group:
                    if (!GroupDialog.Visible) GroupDialog.Show();
                    else GroupDialog.Hide();
                    break;
                case KeybindOptions.Belt:
                    if (!BeltDialog.Visible) BeltDialog.Show();
                    else BeltDialog.Hide();
                    break;
                case KeybindOptions.BeltFlip:
                    BeltDialog.Flip();
                    break;
                case KeybindOptions.Belt7:
                case KeybindOptions.Belt7Alt:
                    if (HeroBeltDialog != null) ItemMoves.Use(HeroBeltDialog.Grid[0].GridType, HeroBeltDialog.Grid[0].ItemSlot);
                    break;
                case KeybindOptions.Belt8:
                case KeybindOptions.Belt8Alt:
                    if (HeroBeltDialog != null) ItemMoves.Use(HeroBeltDialog.Grid[1].GridType, HeroBeltDialog.Grid[1].ItemSlot);
                    break;
                case KeybindOptions.Minimap:
                    MiniMapDialog.Toggle();
                    break;
                case KeybindOptions.Bigmap:
                    BigMapDialog.Toggle();
                    break;
                case KeybindOptions.Rental:
                    ItemRentalDialog.Toggle();
                    break;

                case KeybindOptions.Help:
                    if (!HelpDialog.Visible) HelpDialog.Show();
                    else HelpDialog.Hide();
                    break;
                case KeybindOptions.Keybind:
                    if (!KeyboardLayoutDialog.Visible) KeyboardLayoutDialog.Show();
                    else KeyboardLayoutDialog.Hide();
                    break;
                case KeybindOptions.Cameramode:

                    if (!MainDialog.Visible)
                    {
                        MainDialog.Show();
                        ChatDialog.Show();
                        BeltDialog.Show();
                        ChatControl.Show();
                        MiniMapDialog.Show();
                        CharacterDuraPanel.Show();
                        DuraStatusPanel.Show();
                        BuffsDialog.Show();
                    }
                    else
                    {
                        MainDialog.Hide();
                        ChatDialog.Hide();
                        BeltDialog.Hide();
                        ChatControl.Hide();
                        MiniMapDialog.Hide();
                        CharacterDuraPanel.Hide();
                        DuraStatusPanel.Hide();
                        BuffsDialog.Hide();
                    }
                    break;
                case KeybindOptions.AddGroupMember:
                    if (MapObject.MouseObject == null) break;
                    if (MapObject.MouseObject.Race != ObjectType.Player) break;

                    GameScene.Scene.GroupDialog.AddMember(MapObject.MouseObject.Name);
                    break;
            }
        }

        internal void ClassicSkillModeChanged(bool ctrl)
        {
            GameScene.Scene.OptionDialog.ToggleSkillButtons(ctrl);
        }

        internal void ClassicObjectPositionsChanged()
        {
            if (MiniMapDialog != null && MiniMapDialog.Visible && MapControl != null && (MapControl.MiniMap > 0 || GeneratedMapPicture.IsFor(MapControl))) MiniMapDialog.Redraw();
            if (BigMapDialog != null && BigMapDialog.Visible) BigMapDialog.Redraw();
        }

        internal void ClassicProcessBeforeMap()
        {
            TimerControl.Process();
            CompassControl.Process();
            RankingDialog.Process();

            MirItemCell cell = MouseControl as MirItemCell;

            if (cell != null && HoverItem != cell.Item && HoverItem != cell.ShadowItem)
            {
                DisposeItemLabel();
                HoverItem = null;
                CreateItemLabel(cell.Item);
            }

            if (ItemLabel != null && !ItemLabel.IsDisposed)
            {
                ItemLabel.BringToFront();

                int x = CMain.MPoint.X + 28, y = CMain.MPoint.Y + 28;
                if (x + ItemLabel.Size.Width > Settings.ScreenWidth)
                    x = Settings.ScreenWidth - ItemLabel.Size.Width;

                if (y + ItemLabel.Size.Height > Settings.ScreenHeight)
                    y = Settings.ScreenHeight - ItemLabel.Size.Height;
                ItemLabel.Location = new Point(x, y);
            }

            if (MailLabel != null && !MailLabel.IsDisposed)
            {
                MailLabel.BringToFront();

                int x = CMain.MPoint.X + 15, y = CMain.MPoint.Y;
                if (x + MailLabel.Size.Width > Settings.ScreenWidth)
                    x = Settings.ScreenWidth - MailLabel.Size.Width;

                if (y + MailLabel.Size.Height > Settings.ScreenHeight)
                    y = Settings.ScreenHeight - MailLabel.Size.Height;
                MailLabel.Location = new Point(x, y);
            }

            if (MemoLabel != null && !MemoLabel.IsDisposed)
            {
                MemoLabel.BringToFront();

                int x = CMain.MPoint.X + 15, y = CMain.MPoint.Y;
                if (x + MemoLabel.Size.Width > Settings.ScreenWidth)
                    x = Settings.ScreenWidth - MemoLabel.Size.Width;

                if (y + MemoLabel.Size.Height > Settings.ScreenHeight)
                    y = Settings.ScreenHeight - MemoLabel.Size.Height;
                MemoLabel.Location = new Point(x, y);
            }

            if (GuildBuffLabel != null && !GuildBuffLabel.IsDisposed)
            {
                GuildBuffLabel.BringToFront();

                int x = CMain.MPoint.X + 15, y = CMain.MPoint.Y;
                if (x + GuildBuffLabel.Size.Width > Settings.ScreenWidth)
                    x = Settings.ScreenWidth - GuildBuffLabel.Size.Width;

                if (y + GuildBuffLabel.Size.Height > Settings.ScreenHeight)
                    y = Settings.ScreenHeight - GuildBuffLabel.Size.Height;
                GuildBuffLabel.Location = new Point(x, y);
            }

            if (!User.Dead) ShowReviveMessage = false;

            if (ShowReviveMessage && CMain.Time > User.DeadTime && User.CurrentAction == MirAction.Dead)
            {
                ShowReviveMessage = false;
                MirMessageBox messageBox = new MirMessageBox(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.DiedTip), MirMessageBoxButtons.YesNo, false);

                messageBox.YesButton.Click += (o, e) =>
                {
                    if (User.Dead) Network.Enqueue(new C.TownRevive());
                };

                messageBox.AfterDraw += (o, e) =>
                {
                    if (!User.Dead) messageBox.Dispose();
                };

                messageBox.Show();
            }

            BuffsDialog.Process();
            HeroBuffsDialog?.Process();
        }

        /// <summary>IGameUi.Process: GameScene.Process right after MapControl.Process.</summary>
        internal void ClassicProcess()
        {
            MainDialog.Process();
            InventoryDialog.Process();
            GameShopDialog.Process();
            MiniMapDialog.Process();

            foreach (SkillBarDialog Bar in Scene.SkillBarDialogs)
                Bar.Process();
        }

        internal void ClassicDialogProcess()
        {
            if (Settings.SkillBar)
            {
                foreach (SkillBarDialog Bar in Scene.SkillBarDialogs)
                    Bar.Show();
            }
            else
            {
                foreach (SkillBarDialog Bar in Scene.SkillBarDialogs)
                    Bar.Hide();
            }

            for (int i = 0; i < Scene.SkillBarDialogs.Count; i++)
            {
                if (i * 2 > Settings.SkillbarLocation.Length) break;
                if ((Settings.SkillbarLocation[i, 0] > Settings.Resolution - 100) || (Settings.SkillbarLocation[i, 1] > 700))
                    continue; //in theory you'd want the y coord to be validated based on resolution, but since client only allows for wider screens and not higher :(
                Scene.SkillBarDialogs[i].Location = new Point(Settings.SkillbarLocation[i, 0], Settings.SkillbarLocation[i, 1]);
            }

            if (Settings.DuraView)
                CharacterDuraPanel.Show();
            else
                CharacterDuraPanel.Hide();
        }

        internal void ClassicProcessOutput()
        {
            for (int i = 0; i < OutputLines.Length; i++)
            {
                if (OutputMessages.Count > i)
                {
                    Color color;
                    switch (OutputMessages[i].Type)
                    {
                        case OutputMessageType.Quest:
                            color = Color.Gold;
                            break;
                        case OutputMessageType.Guild:
                            color = Color.DeepPink;
                            break;
                        default:
                            color = Color.LimeGreen;
                            break;
                    }

                    OutputLines[i].Text = OutputMessages[i].Message;
                    OutputLines[i].ForeColour = color;
                    OutputLines[i].Visible = true;
                }
                else
                {
                    OutputLines[i].Text = string.Empty;
                    OutputLines[i].Visible = false;
                }
            }
        }

        /// <summary>IGameUi.Dispose: GameScene.Dispose(true), right after MapControl = null (the dialogs themselves are disposed with the scene's controls).</summary>
        internal void ClassicDispose()
        {
            MainDialog = null;
            ChatDialog = null;
            ChatControl = null;
            InventoryDialog = null;
            CharacterDialog = null;
            StorageDialog = null;
            BeltDialog = null;
            MiniMapDialog = null;
            PatchStrip = null;
            InspectDialog = null;
            OptionDialog = null;
            MenuDialog = null;
            NPCDialog = null;
            QuestDetailDialog = null;
            QuestListDialog = null;
            QuestLogDialog = null;
            QuestTrackingDialog = null;
            GameShopDialog = null;
            MentorDialog = null;

            NewHeroDialog = null;
            HeroInventoryDialog = null;
            HeroDialog = null;
            HeroBeltDialog = null;

            RelationshipDialog = null;
            CharacterDuraPanel = null;
            DuraStatusPanel = null;

            for (int i = 0; i < OutputLines.Length; i++)
                if (OutputLines[i] != null && OutputLines[i].IsDisposed)
                    OutputLines[i].Dispose();
        }
    }
}

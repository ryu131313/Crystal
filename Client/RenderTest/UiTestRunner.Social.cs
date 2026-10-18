using Client.MirControls;
using Client.MirObjects;
using Client.MirScenes;
using Client.MirScenes.Modern;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using Client.MirScenes.Modern.Windows;
using S = ServerPackets;

namespace Client.RenderTest
{
    /// <summary>
    /// Phase 4 area "social" steps (modern only): the group window (invite by name, the roster with status / map / location,
    /// pick, kick confirm-twice, leave through Allow group), the trade window (S.TradeAccept opens it, offer from its bag, the
    /// deposit reply, add gold, the guest's offer, confirm, the room-check refusal, S.TradeConfirm's summary, a remote cancel with
    /// the "must face" box, Esc cancelling), friends (C.RefreshFriends on open, bonds strip, pick, memo, a friend request and its
    /// result, an incoming request accepted on the Requests tab, unblock), mentor (finder, candidates, request, the bond with
    /// details, the allow switch, end), relationship (single with a player faced, propose; married, divorce question) and the quest
    /// log (active / available from an NPC met / completed / items, track, abandon). Starts from a fresh game and closes
    /// everything at the end.
    /// </summary>
    internal static partial class UiTestRunner
    {
        static partial void AddSocialSteps(List<Step> steps)
        {
            SStep(steps, "soc_logout", () => { Root?.Popups.CloseAll(); Feed(new S.LogOutSuccess { Characters = new List<SelectInfo>() }); }, 800);
            SStep(steps, "soc_enter", () =>
            {
                EnterGame();
                SetScale(1f, false);
                Feed(new S.ServerRules
                {
                    MentorLevelGap = 10, MentorLength = 7, MentorDamageBoost = 10, MentorExpBoost = 10, MenteeExpBank = 5, MentorSkillBoost = true,
                    LoverEXPBonus = 5, MarriageCooldown = 7, MarriageLevelRequired = 22, WeddingRingRecall = true, FriendRequestsEnabled = true, MaxFriends = 50,
                });
                Feed(new S.AllowToggles { Flags = 0x1ff });
                Feed(new S.SwitchGroup { AllowGroup = true });
            }, 1500);

            // ---- group
            SStep(steps, "group_open", () => PressBind(KeybindOptions.Group), 800);
            SStep(steps, "group_invite", () =>
            {
                GroupWindow w = GroupWin;
                if (w != null) w.InviteField.Text = "UiAlly";
                ClickAt(Centre(w?.InviteButton));
            });
            SStep(steps, "group_roster", () =>
            {
                Point at = GameScene.User.CurrentLocation;
                Feed(new S.AddMember { Name = "UiTester" });
                Feed(new S.AddMember { Name = "UiAlly" });
                Feed(new S.AddMember { Name = "UiFar" });
                Feed(new S.AddMember { Name = "UiNear" });
                Feed(new S.ObjectPlayer { ObjectID = 9200, Name = "UiNear", NameColour = Color.White, Class = MirClass.Taoist, Gender = MirGender.Female, Level = 24,
                    Location = new Point(at.X - 2, at.Y + 1), Direction = MirDirection.Up, Weapon = -1, Armour = 0 });
                Feed(new S.ObjectHealth { ObjectID = 9200, Percent = 64, Expire = 30 });
                Feed(new S.SendMemberLocation { MemberName = "UiNear", MemberLocation = new Point(at.X - 2, at.Y + 1) });
                Feed(new S.GroupMemberStatus { Name = "UiAlly", Class = MirClass.Wizard, Level = 31, HPPercent = 80, MPPercent = 45, Buffs = new List<BuffType> { BuffType.Haste, BuffType.SoulShield } });
                Feed(new S.SendMemberLocation { MemberName = "UiAlly", MemberLocation = new Point(at.X + 9, at.Y - 4) });
                Feed(new S.GroupMembersMap { PlayerName = "UiFar", PlayerMap = "Bichon Province" });
                Feed(new S.GroupMemberStatus { Name = "UiFar", Class = MirClass.Archer, Level = 40, HPPercent = 0, MPPercent = 10 });
            }, 1300);
            SStep(steps, "group_pick_ally", () => ClickAt(Centre(GroupWin?.CardBox(1))));
            SStep(steps, "group_kick_arm", () => ClickAt(Centre(GroupWin?.KickButton)));
            SStep(steps, "group_kick_confirm", () => ClickAt(Centre(GroupWin?.KickButton)));
            SStep(steps, "group_member_left", () => Feed(new S.DeleteMember { Name = "UiAlly" }), 700);
            SStep(steps, "group_pick_me", () => ClickAt(Centre(GroupWin?.CardBox(0))));
            SStep(steps, "group_leave_ask", () => ClickAt(Centre(GroupWin?.LeaveButton)));
            SStep(steps, "group_leave_yes", () => Key(Keys.Enter));
            SStep(steps, "group_switch_echo", () => Feed(new S.SwitchGroup { AllowGroup = false }), 700);
            SStep(steps, "group_allow_on", () => ClickAt(Centre(GroupWin?.AllowSwitch)));
            SStep(steps, "group_close", () => { Feed(new S.SwitchGroup { AllowGroup = true }); PressBind(KeybindOptions.Group); });

            // ---- trade
            SStep(steps, "trade_request", () => Feed(new S.TradeRequest { Name = "UiTrader" }), 700);
            SStep(steps, "trade_accept", () =>
            {
                Point at = GameScene.User.CurrentLocation;
                Feed(new S.ObjectPlayer { ObjectID = 9300, Name = "UiTrader", NameColour = Color.White, Class = MirClass.Warrior, Gender = MirGender.Male, Level = 35, GuildName = "Wolves",
                    Location = new Point(at.X, at.Y + 1), Direction = MirDirection.Up, Weapon = -1, Armour = 0 });
                Feed(new S.TradeAccept { Name = "UiTrader" });
            }, 900);
            SStep(steps, "trade_offer_sword", () => ClickAt(Centre(TradeBag(9))));
            SStep(steps, "trade_deposit_reply", () => Feed(new S.DepositTradeItem { From = 9, To = 0, Success = true }), 700);
            SStep(steps, "trade_offer_potion", () => ClickAt(Centre(TradeBag(8))));
            SStep(steps, "trade_deposit_reply2", () => Feed(new S.DepositTradeItem { From = 8, To = 1, Success = true }), 700);
            SStep(steps, "trade_take_back", () => SocDoubleClickAt(Centre(TradeWin?.MineCells[0])));
            SStep(steps, "trade_retrieve_reply", () => Feed(new S.RetrieveTradeItem { From = 0, To = _socBackTo, Success = true }), 700);
            SStep(steps, "trade_gold_open", () => ClickAt(Centre(TradeWin?.AddGoldButton)));
            SStep(steps, "trade_gold_ok", () =>
            {
                ModernTextField f = Find<ModernTextField>(Root.Popups.Card);
                if (f != null) f.Text = "2500";
                ClickAt(Centre(Root.Popups.Card?.ButtonRow.Count > 0 ? Root.Popups.Card.ButtonRow[^1] : null));
            });
            SStep(steps, "trade_gold_taken", () => Feed(new S.LoseGold { Gold = 2500 }));
            SStep(steps, "trade_guest_offer", () =>
            {
                var items = new UserItem[10];
                items[0] = Item("UiTestRing");
                items[1] = Item("UiTestScroll", 3);
                Feed(new S.TradeItem { TradeItems = items });
                Feed(new S.TradeGold { Amount = 12000 });
            }, 800);
            SStep(steps, "trade_confirm", () => ClickAt(Centre(TradeWin?.ConfirmButton)));
            SStep(steps, "trade_room_check", () => Feed(new S.TradeCancel { Unlock = true }), 700);
            SStep(steps, "trade_confirm_again", () => ClickAt(Centre(TradeWin?.ConfirmButton)));
            SStep(steps, "trade_done", () => Feed(new S.TradeConfirm()), 900);
            SStep(steps, "trade_summary_close", () => ClickAt(Centre(TradeWin?.SummaryClose)));
            SStep(steps, "trade2_open", () => Feed(new S.TradeAccept { Name = "UiTrader" }), 700);
            SStep(steps, "trade2_remote_cancel", () => Feed(new S.TradeCancel { Unlock = false }), 800);
            SStep(steps, "trade2_box_ok", () => Key(Keys.Enter));
            SStep(steps, "trade3_open", () => Feed(new S.TradeAccept { Name = "UiTrader" }), 700);
            SStep(steps, "trade3_escape", () => Key(Keys.Escape), 700);

            // ---- friends
            SStep(steps, "friends_open", () =>
            {
                long seen = CMain.Now.AddHours(-5).ToBinary();
                Feed(new S.FriendUpdate { Friends = new List<ClientFriend>
                {
                    new ClientFriend { Index = 2, Name = "UiAlly", Online = true, Class = MirClass.Wizard, Level = 31, GuildName = "Wolves", Memo = "Healer for the raids" },
                    new ClientFriend { Index = 3, Name = "Zed", Online = false, Class = MirClass.Archer, Level = 18, LastSeen = seen },
                    new ClientFriend { Index = 4, Name = "Brigand", Blocked = true, Class = MirClass.Assassin, Level = 40, Memo = "Spammer" },
                } });
                Feed(new S.LoverUpdate { Name = "UiAlly", Date = CMain.Now.AddDays(-30), MapName = "BichonProvince", MarriedDays = 30 });
                Feed(new S.MentorUpdate { Name = "UiSensei", Level = 60, Online = true, MenteeEXP = 0 });
                PressBind(KeybindOptions.Friends);
            }, 900);
            SStep(steps, "friends_pick_offline", () => ClickAt(Centre(SocFriendRow("Zed"))));
            SStep(steps, "friends_memo", () =>
            {
                FriendsWindow w = FriendsWin;
                if (w?.MemoField != null) w.MemoField.Text = "Met in the mines";
                ClickAt(Centre(w?.SaveButton));
            });
            SStep(steps, "friends_request_send", () =>
            {
                FriendsWindow w = FriendsWin;
                if (w?.AddField != null) w.AddField.Text = "Newbie";
                ClickAt(Centre(w?.AddButton));
            });
            SStep(steps, "friends_result", () => Feed(new S.FriendRequestResult { Name = "Newbie", Result = S.FriendRequestResult.NotAccepting }), 700);
            SStep(steps, "friends_request_in", () => Feed(new S.FriendRequest { Name = "Stranger", Class = MirClass.Taoist, Level = 27 }), 900);
            SStep(steps, "friends_requests_tab", () => ClickTabOf(FriendsWin, 1), 700);
            SStep(steps, "friends_accept", () => ClickAt(Centre(FindButton(FriendsWin?.RequestRow(0), W(ClientTextKeys.ModernAccept)))));
            SStep(steps, "friends_blocked_tab", () => ClickTabOf(FriendsWin, 2), 700);
            SStep(steps, "friends_unblock_ask", () => ClickAt(Centre(FindButton(FriendsWin?.Detail, W(ClientTextKeys.ModernSocialUnblock)))));
            SStep(steps, "friends_unblock_yes", () => Key(Keys.Enter));
            SStep(steps, "friends_close", () => { Root?.Popups.CloseAll(); PressBind(KeybindOptions.Friends); });

            // ---- mentor
            SStep(steps, "mentor_free", () =>
            {
                Feed(new S.MentorUpdate { Name = string.Empty, Level = 0, Online = false });
                Root?.Wm.Open(MentorWindow.WindowKey);
            }, 900);
            SStep(steps, "mentor_search", () => ClickAt(Centre(MentorWin?.SearchButton)));
            SStep(steps, "mentor_candidates", () => Feed(new S.MentorCandidates { Candidates = new List<ClientMentorCandidate>
            {
                new ClientMentorCandidate { Name = "UiSensei", Class = MirClass.Warrior, Level = 60 },
                new ClientMentorCandidate { Name = "OldHand", Class = MirClass.Warrior, Level = 48 },
            } }), 700);
            SStep(steps, "mentor_request", () => ClickAt(Centre(MentorWin?.RequestButton(0))));
            SStep(steps, "mentor_bond", () =>
            {
                Feed(new S.MentorUpdate { Name = "UiSensei", Level = 60, Online = true, MenteeEXP = 0 });
                Feed(new S.MentorDetails { PartnerClass = MirClass.Warrior, MentorDate = CMain.Now.AddDays(-2).ToBinary(), EndsAt = CMain.Now.AddDays(5).ToBinary() });
            }, 900);
            SStep(steps, "mentor_allow_off", () => ClickAt(Centre(MentorWin?.AllowSwitch)));
            SStep(steps, "mentor_allow_echo", () => Feed(new S.AllowToggles { Flags = (ushort)(0x1ff & ~(1 << (int)AllowToggle.Mentor)) }), 700);
            SStep(steps, "mentor_end_ask", () => ClickAt(Centre(MentorWin?.EndButton)));
            SStep(steps, "mentor_end_yes", () => Key(Keys.Enter));
            SStep(steps, "mentor_close", () => Root?.Wm.Close(MentorWindow.WindowKey));

            // ---- relationship
            SStep(steps, "rel_single", () =>
            {
                Feed(new S.LoverUpdate { Name = string.Empty, Date = DateTime.MinValue, MapName = string.Empty, MarriedDays = 0 });
                Root?.Wm.Open(RelationshipWindow.WindowKey);
            }, 1300);
            SStep(steps, "rel_propose", () => ClickAt(Centre(RelWin?.ProposeButton)));
            SStep(steps, "rel_married", () => Feed(new S.LoverUpdate { Name = "UiTrader", Date = CMain.Now.AddDays(-12), MapName = "BichonProvince", MarriedDays = 12 }), 900);
            SStep(steps, "rel_divorce_ask", () => ClickAt(Centre(RelWin?.DivorceButton)));
            SStep(steps, "rel_divorce_no", () => Key(Keys.Escape));
            SStep(steps, "rel_close", () => Root?.Wm.Close(RelationshipWindow.WindowKey));

            // ---- quests
            SStep(steps, "quests_open", () =>
            {
                Point at = GameScene.User.CurrentLocation;
                Feed(new S.ObjectNPC { ObjectID = 7001, Name = "BichonProvince_Guard_Captain", NameColour = Color.Lime, Image = 1, Colour = Color.White, Location = new Point(at.X + 3, at.Y - 2), Direction = MirDirection.Down });
                foreach (ClientQuestInfo q in Quests()) if (!GameScene.QuestInfoList.Exists(x => x.Index == q.Index)) GameScene.QuestInfoList.Add(q);
                Feed(new S.ChangeQuest { QuestState = QuestState.Add, TrackQuest = true, Quest = new ClientQuestProgress { Id = 801, TaskList = new List<string> { "Kill hens: 3/10" }, Taken = true } });
                Feed(new S.ChangeQuest { QuestState = QuestState.Add, Quest = new ClientQuestProgress { Id = 802, TaskList = new List<string> { "Bring 5 potions: 5/5" }, Taken = true, Completed = true } });
                Feed(new S.CompleteQuest { CompletedQuests = new List<int> { 800 } });
                PressBind(KeybindOptions.Quests);
            }, 1000);
            SStep(steps, "quests_pick_ready", () => ClickAt(Centre(QuestsWin?.Row(1))), 700);
            SStep(steps, "quests_track", () => ClickAt(Centre(FindButton(QuestsWin?.Actions, W(ClientTextKeys.ModernSocialQTrack)))));
            SStep(steps, "quests_abandon_ask", () => ClickAt(Centre(FindButton(QuestsWin?.Actions, W(ClientTextKeys.ModernSocialQAbandon)))));
            SStep(steps, "quests_abandon_yes", () => Key(Keys.Enter));
            SStep(steps, "quests_abandon_reply", () => Feed(new S.ChangeQuest { QuestState = QuestState.Remove, Quest = new ClientQuestProgress { Id = 802 } }), 700);
            SStep(steps, "quests_available", () => ClickAt(Centre(QuestsWin?.Seg(1))), 800);
            SStep(steps, "quests_completed", () => ClickAt(Centre(QuestsWin?.Seg(2))), 700);
            SStep(steps, "quests_items", () =>
            {
                GameScene.User.QuestInventory[0] = Item("UiTestScroll", 2);
                ClickAt(Centre(QuestsWin?.Seg(3)));
            }, 700);
            SStep(steps, "soc_close_all", () => { Root?.Popups.CloseAll(); CloseAllPrompts(); PressBind(KeybindOptions.Closeall); DumpWindows(); });
        }

        // ------------------------------------------------------------------ helpers

        private static GroupWindow GroupWin => Win<GroupWindow>(GroupWindow.WindowKey);
        private static TradeWindow TradeWin => Win<TradeWindow>(TradeWindow.WindowKey);
        private static FriendsWindow FriendsWin => Win<FriendsWindow>(FriendsWindow.WindowKey);
        private static MentorWindow MentorWin => Win<MentorWindow>(MentorWindow.WindowKey);
        private static RelationshipWindow RelWin => Win<RelationshipWindow>(RelationshipWindow.WindowKey);
        private static QuestsWindow QuestsWin => Win<QuestsWindow>(QuestsWindow.WindowKey);

        private static void SStep(List<Step> steps, string name, Action act, int delayMs = 600) =>
            steps.Add(new Step { Name = name, Act = () => { DumpSocialState(_log); act(); }, DelayMs = delayMs, ModernOnly = true });

        private static MirControl TradeBag(int slot)
        {
            IReadOnlyList<ModernItemCell> cells = TradeWin?.BagCells;
            if (cells == null) return null;
            foreach (ModernItemCell c in cells) if (c.Slot == slot) return c;
            return null;
        }

        /// <summary>A friend row in the friends list (the row painter is a PaintBox; the friend is found by its index order).</summary>
        private static MirControl SocFriendRow(string name)
        {
            FriendsWindow w = FriendsWin;
            if (w == null) return null;
            // rows follow the groups: Online (UiAlly), Offline (Zed); heads are 24 px high, rows 44
            int want = name == "Zed" ? 3 : 1, n = 0;
            foreach (MirControl c in w.List.Content.Controls)
                if (c is PaintBox && n++ == want) return c;
            return null;
        }

        private static string SocLocks()
        {
            var l = new List<string>();
            UserItem[] inv = GameScene.User?.Inventory, trade = GameScene.User?.Trade;
            if (inv != null) for (int i = 0; i < inv.Length; i++) if (ItemMoves.IsLocked(MirGridType.Inventory, i)) l.Add("bag" + i);
            if (trade != null) for (int i = 0; i < trade.Length; i++) if (ItemMoves.IsLocked(MirGridType.Trade, i)) l.Add("trade" + i);
            return string.Join(",", l);
        }

        private static int _socBackTo = -1;

        private static void SocDoubleClickAt(Point p)
        {
            // where the take-back will land (the first free bag cell: the sword does not stack), for the reply step
            UserItem[] inv = GameScene.User.Inventory;
            for (int i = GameScene.User.BeltIdx; i < inv.Length && _socBackTo < 0; i++) if (inv[i] == null) _socBackTo = i;
            MouseAt(p);
            var one = new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0);
            MirScene.ActiveScene.OnMouseDown(one);
            MirScene.ActiveScene.OnMouseClick(one);
            MirScene.ActiveScene.OnMouseUp(one);
            var two = new MouseEventArgs(MouseButtons.Left, 2, p.X, p.Y, 0);
            MirScene.ActiveScene.OnMouseDown(two);
            MirScene.ActiveScene.OnMouseDoubleClick(two);
            MirScene.ActiveScene.OnMouseUp(two);
        }

        private static List<ClientQuestInfo> Quests()
        {
            ClientQuestInfo Q(int index, string name, QuestType type, uint npc, int min, string[] desc, string[] tasks, uint exp, uint gold)
            {
                var q = new ClientQuestInfo
                {
                    Index = index, Name = name, Group = "Bichon", Type = type, NPCIndex = npc, FinishNPCIndex = npc, MinLevelNeeded = min, MaxLevelNeeded = 99,
                    ClassNeeded = RequiredClass.None, RewardExp = exp, RewardGold = gold,
                };
                q.Description.AddRange(desc);
                q.TaskDescription.AddRange(tasks);
                q.ReturnDescription.Add("Return to the {Guard Captain/Yellow} when you are done.");
                return q;
            }
            var hens = Q(801, "Hens of the Plains", QuestType.General, 7001, 10,
                new[] { "The farmers are tired of the hens eating their crops.", "Thin them out and bring me a [ITEM:9005]." }, new[] { "Kill 10 hens", "Bring a scroll" }, 1500, 300);
            hens.RewardsFixedItem.Add(new QuestItemReward { Item = Info("UiTestPotion"), Count = 5 });
            hens.RewardsSelectItem.Add(new QuestItemReward { Item = Info("UiTestSword"), Count = 1 });
            hens.RewardsSelectItem.Add(new QuestItemReward { Item = Info("UiTestRing"), Count = 1 });
            return new List<ClientQuestInfo>
            {
                Q(800, "First Steps", QuestType.Story, 7001, 1, new[] { "Speak to the captain." }, new[] { "Talk to the Guard Captain" }, 100, 0),
                hens,
                Q(802, "Potion Run", QuestType.Daily, 7001, 5, new[] { "The healer needs potions." }, new[] { "Bring 5 potions" }, 800, 1200),
                Q(803, "The Old Mine", QuestType.Story, 7001, 20, new[] { "Strange noises come from the old mine." }, new[] { "Explore the mine" }, 5000, 2000),
                Q(804, "Wolf Pelts", QuestType.Repeatable, 7001, 25, new[] { "Pelts fetch a good price." }, new[] { "Bring 3 wolf pelts" }, 900, 400),
            };
        }

        /// <summary>Area B (social) state lines.</summary>
        private static void DumpSocialState(List<string> log)
        {
            ModernRoot root = Root;
            if (root == null || GameScene.Scene == null || GameScene.User == null) return;
            var g = GameScene.Models.Group;
            if (GroupWin is GroupWindow gw)
                log.Add($"   [social] group members=[{string.Join(",", g.Members)}] allow={g.AllowGroup} pending={gw.AllowPending} picked={gw.Picked} note=\"{gw.Note}\" card1=\"{gw.CardText(1)}\"");
            if (TradeWin is TradeWindow tw)
                log.Add($"   [social] trade active={tw.Active} locked={GameScene.User.TradeLocked} mine=[{Items(GameScene.User.Trade)}] gold={GameScene.User.TradeGoldAmount} guest=[{Items(GameScene.Models.Trade.GuestItems)}] guestGold={GameScene.Models.Trade.GuestGold} summary={tw.SummaryShown} note=\"{tw.NoteShown}\" locks=[{SocLocks()}]");
            if (FriendsWin is FriendsWindow fw)
                log.Add($"   [social] friends tab={fw.TabShown} picked={fw.PickedIndex} note=\"{fw.NoteShown}\" requests={GameScene.Models.Friends.Requests.Count} pending3={fw.IsPending(3)}");
            if (MentorWin is MentorWindow mw)
                log.Add($"   [social] mentor name={GameScene.Models.Relationship.MentorName} level={GameScene.Models.Relationship.MentorLevel} details={GameScene.Models.Relationship.HasMentorDetails} sent={mw.SentTo} board=\"{mw.BoardNote}\" allow={GameScene.Models.AllowToggles.Get(AllowToggle.Mentor)}");
            if (RelWin is RelationshipWindow)
                log.Add($"   [social] lover={GameScene.Models.Relationship.LoverName} map={GameScene.Models.Relationship.LoverMap} days={GameScene.Models.Relationship.MarriedDays}");
            if (QuestsWin is QuestsWindow qw)
                log.Add($"   [social] quests tab={qw.TabShown} picked={qw.PickedShown} current=[{string.Join(",", GameScene.User.CurrentQuests.ConvertAll(q => q.Id))}] tracked=[{string.Join(",", Settings.TrackedQuests)}] pending802={qw.IsPending(802)} npcs={QuestNpcs.Seen.Count}");
        }
    }
}

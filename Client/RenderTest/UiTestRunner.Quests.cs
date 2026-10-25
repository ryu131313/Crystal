using Client.MirControls;
using Client.MirObjects;
using Client.MirScenes;
using Client.MirScenes.Models;
using Client.MirScenes.Modern;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Hud;
using Client.MirScenes.Modern.Windows;
using S = ServerPackets;

namespace Client.RenderTest
{
    /// <summary>
    /// Quest V2 steps (Part B phase 3, lane C), for both UIs: a fresh game gets the login picture (S.QuestDefinition, S.QuestStates
    /// Full, S.QuestProgress, S.QuestHistory Full, S.QuestAvailability for an NPC on screen), then a shared quest offer (the accept
    /// prompt → C.QuestAccept { NpcObjectID = 0 }), the accept reply with TrackHint (the tracker / classic tracking), progress (the
    /// toast and the bars), the log (active with steps and bars, a Ready quest, a failed quest, available, completed with a repeat
    /// timer and a history quest without a definition → C.QuestDefinitionRequest), and the NPC's quest list (accept → C.QuestAccept
    /// with the NPC's ObjectID, turn in with a choice-group pick → C.QuestTurnIn, the reply and the completion banner).
    /// Each send is checked (Expect). Runs last, so the earlier step numbers stay put.
    /// </summary>
    internal static partial class UiTestRunner
    {
        private const uint QNpc = 7701;
        private const int QHens = 801, QPotion = 802, QMine = 803, QPelts = 804, QWolves = 805, QOld = 806, QShared = 807, QFirst = 800;

        static partial void AddQuestSteps(List<Step> steps)
        {
            QStep(steps, "qv2_logout", () => { _qMark = _log.Count; Root?.Popups.CloseAll(); CloseAllPrompts(); Feed(new S.LogOutSuccess { Characters = new List<SelectInfo>() }); }, 800);
            QStep(steps, "qv2_enter", () => { EnterGame(); if (IsModern) SetScale(1f, false); }, 1500);
            QStep(steps, "qv2_login_picture", () =>
            {
                QLoginPicture();
                Expect(GameScene.Models.Quests.Runs.Count == 3, "three runs after the login picture");
                NPCObject npc = MapControl.Objects.TryGetValue(QNpc, out MapObject ob) ? ob as NPCObject : null;
                Expect(npc != null && npc.QuestIcon == QuestIcon.QuestionYellow, "the NPC to take the server's icon");
            }, 900);
            QStep(steps, "qv2_offer_prompt", () =>
            {
                Feed(QDefShared());
                Feed(new S.QuestOffer { Quest = QShared, Kind = QuestEndpointKind.Npc, SharedBy = "UiAlly" });
            }, 900);
            QStep(steps, "qv2_offer_accept", () =>
            {
                if (IsModern) ClickAt(Centre(FindButton(FirstToast(), W(ClientTextKeys.ModernAccept))));
                else TopMessageBox()?.YesButton.InvokeMouseClick(null);
                Expect(_sent.Exists(s => s.StartsWith("QuestAccept ") && s.Contains("Quest=807") && s.Contains("NpcObjectID=0")), "C.QuestAccept { 807, 0 }");
            });
            QStep(steps, "qv2_accept_reply", () =>
            {
                Feed(new S.QuestStates { States = new List<S.QuestStates.Entry> { new S.QuestStates.Entry { Quest = QShared, Status = QuestRunStatus.Active, Step = 8071, TrackHint = true } } });
                Feed(new S.QuestProgress { Progress = new List<S.QuestProgress.Entry> { new S.QuestProgress.Entry { Quest = QShared, Objective = 80711, Value = 0 } } });
                if (IsModern) Expect(ModernUiNow?.Tracker?.IsTracked(QShared) == true, "the tracker to track the accepted quest (TrackHint)");
                else Expect(ClassicTracks(QShared), "classic tracking to add the accepted quest");
            }, 900);
            QStep(steps, "qv2_progress", () =>
            {
                Feed(new S.QuestProgress { Progress = new List<S.QuestProgress.Entry>
                {
                    new S.QuestProgress.Entry { Quest = QShared, Objective = 80711, Value = 4 },
                    new S.QuestProgress.Entry { Quest = QHens, Objective = 80111, Value = 7 },
                } });
                Expect(GameScene.Models.Quests.Value(QHens, 80111) == 7, "the hens value");
            }, 700);
            QStep(steps, "qv2_log_open", () => PressBind(KeybindOptions.Quests), 900);
            QStep(steps, "qv2_log_ready", () =>
            {
                if (IsModern) ClickAt(Centre(QRow(QPotion)));
                else ClickClassicDiary(QPotion);
            }, 800);
            QStep(steps, "qv2_log_failed", () =>
            {
                if (IsModern) ClickAt(Centre(QRow(QWolves)));
                else ClickClassicDiary(QWolves);
            }, 800);
            QModern(steps, "qv2_log_scrolled", () => QuestsWin?.Detail?.ScrollTo(int.MaxValue), 600);
            QModern(steps, "qv2_log_available", () => ClickAt(Centre(QuestsWin?.Seg(1))), 800);
            QModern(steps, "qv2_log_completed", () => ClickAt(Centre(QuestsWin?.Seg(2))), 800);
            QModern(steps, "qv2_log_repeat_timer", () =>
            {
                // the newest history row (806, no definition) was picked first: one C.QuestDefinitionRequest (sent when the detail drew)
                Expect(SentSinceMark("QuestDefinitionRequest ", "Quest=806"), "C.QuestDefinitionRequest { 806 }");
                ClickAt(Centre(QRow(QPelts)));
                _log.Add("   [quests] timer=\"" + QuestsWin?.TimerShown + "\"");
            }, 800);
            QStep(steps, "qv2_close_log", () => { Root?.Popups.CloseAll(); CloseAllPrompts(); PressBind(KeybindOptions.Closeall); }, 600);
            QStep(steps, "qv2_npc_open", () =>
            {
                GameScene.NPCID = QNpc;
                Feed(new S.NPCResponse { Page = new List<string> { "Greetings, UiTester. The hens are at it again.", "<Leave/@exit>" } });
                if (!IsModern) ClassicNpcQuestButton();
            }, 900);
            QModern(steps, "qv2_npc_quests_tab", () => { Key(Keys.Space); ClickTabOf(Win<NpcWindow>(NpcWindow.WindowKey), 1); }, 800);
            QModern(steps, "qv2_npc_scroll_offer", () => QNpcScrollTo(QMine), 600);
            QStep(steps, "qv2_npc_accept", () =>
            {
                if (IsModern) { ClickAt(Centre(QNpcButton(QMine, W(ClientTextKeys.ModernNpcAccept)))); QNpcScrollTo(QPotion); }
                else ClickClassicNpcQuest(QMine, accept: true);
            }, 700);
            QStep(steps, "qv2_npc_pick_reward", () =>
            {
                Expect(SentSinceMark("QuestAccept ", "Quest=803", "NpcObjectID=" + QNpc), "C.QuestAccept { 803, the NPC }");
                if (IsModern) ClickAt(Centre(QNpcRewardCell(QPotion, 1)));
                else ClickClassicNpcQuest(QPotion, accept: false, pickOnly: true);
            }, 700);
            QStep(steps, "qv2_npc_turn_in", () =>
            {
                if (IsModern) ClickAt(Centre(QNpcButton(QPotion, W(ClientTextKeys.ModernNpcHandIn))));
                else ClickClassicNpcQuest(QPotion, accept: false);
            }, 700);
            QStep(steps, "qv2_turn_in_reply", () =>
            {
                // the second choice of group 1 is the ring: reward position 3 in the definition
                Expect(SentSinceMark("QuestTurnIn ", "Quest=802", "NpcObjectID=" + QNpc, "RewardPicks=[3]"), "C.QuestTurnIn { 802, the NPC, [3] }");
                Feed(new S.QuestStates { States = new List<S.QuestStates.Entry> { new S.QuestStates.Entry { Quest = QPotion, Status = QuestRunStatus.None } } });
                Feed(new S.QuestHistory { History = new List<S.QuestHistory.Entry> { new S.QuestHistory.Entry { Quest = QPotion, Name = "Potion Run", Times = 1, LastAt = CMain.Now.ToBinary(), AvailableAt = CMain.Now.AddHours(20).ToBinary() } } });
                Feed(new S.QuestAvailability { Npcs = new List<S.QuestAvailability.Entry> { new S.QuestAvailability.Entry { ObjectID = QNpc, Icon = QuestIcon.QuestionWhite, Finishing = new List<int> { QHens } } } });
                Expect(GameScene.Models.Quests.Run(QPotion) == null, "the turned-in run to go");
            }, 900);
            QStep(steps, "qv2_close_all", () => { Root?.Popups.CloseAll(); CloseAllPrompts(); PressBind(KeybindOptions.Closeall); }, 600);
        }

        private static int _qMark;

        /// <summary>A send recorded since the quest steps began (the step logs list "sent ..." lines; a send can land a frame after the act).</summary>
        private static bool SentSinceMark(string prefix, params string[] parts)
        {
            var lines = new List<string>(_sent);
            for (int i = Math.Min(_qMark, _log.Count); i < _log.Count; i++) if (_log[i].StartsWith("   sent ")) lines.Add(_log[i].Substring(8));
            foreach (string l in lines)
            {
                if (!l.StartsWith(prefix)) continue;
                bool all = true;
                foreach (string p in parts) if (!l.Contains(p)) { all = false; break; }
                if (all) return true;
            }
            return false;
        }

        /// <summary>Scrolls the NPC window's quest list to a quest's card.</summary>
        private static void QNpcScrollTo(int quest)
        {
            NpcWindow w = Win<NpcWindow>(NpcWindow.WindowKey);
            MirControl card = QNpcCard(quest);
            if (w?.QuestList == null || card == null) return;
            w.QuestList.EnsureVisible(card.Location.Y, card.Size.Height);
        }

        private static void QStep(List<Step> steps, string name, Action act, int delayMs = 600) =>
            steps.Add(new Step { Name = name, Act = () => { DumpQuestState(_log); act(); }, DelayMs = delayMs });

        private static void QModern(List<Step> steps, string name, Action act, int delayMs = 600) =>
            steps.Add(new Step { Name = name, Act = () => { DumpQuestState(_log); act(); }, DelayMs = delayMs, ModernOnly = true });

        // ------------------------------------------------------------------ fixtures

        private static S.QuestDefinition.Endpoint QNpcEnd(QuestEndpointRole role) =>
            new S.QuestDefinition.Endpoint { Role = role, Kind = QuestEndpointKind.Npc, TargetId = 77, Name = "BichonProvince_Guard_Captain" };

        private static S.QuestDefinition QDef(int quest, string name, QuestCategory cat, int level, string summary)
        {
            var d = new S.QuestDefinition { Quest = quest, Name = name, Category = cat, LevelHint = level, Summary = summary, AbandonAllowed = true, GroupName = "Bichon" };
            d.Endpoints.Add(QNpcEnd(QuestEndpointRole.Start));
            return d;
        }

        private static S.QuestDefinition QDefShared()
        {
            S.QuestDefinition d = QDef(QShared, "Wolf Cull", QuestCategory.Side, 18, "UiAlly wants help with the wolves.");
            d.Steps.Add(new S.QuestDefinition.Step { Index = 8071, Order = 1, Title = "Cull the pack" });
            d.Objectives.Add(new S.QuestDefinition.Objective { Index = 80711, Step = 8071, Type = QuestObjectiveType.Kill, TargetKind = QuestTargetKind.MonsterGroup, TargetId = 3, TargetName = "Wolf", Count = 8 });
            d.Rewards.Add(new S.QuestDefinition.Reward { Scope = QuestRewardScope.Quest, Kind = QuestRewardKind.Exp, Amount = 2400 });
            return d;
        }

        private static void QLoginPicture()
        {
            Point at = GameScene.User.CurrentLocation;
            GameScene.UpsertMonsterInfo(new ClientMonsterInfo { Index = 1, Name = "Hen", GameName = "Hen", Image = Monster.Hen, Level = 2 });
            GameScene.UpsertNPCInfo(new ClientNPCInfo { Index = 77, ObjectID = QNpc, Name = "BichonProvince_Guard_Captain", MapIndex = 1, Location = new Point(at.X + 3, at.Y - 2) });
            Feed(new S.ObjectNPC { ObjectID = QNpc, Name = "BichonProvince_Guard_Captain", NameColour = Color.Lime, Image = 1, Colour = Color.White, Location = new Point(at.X + 3, at.Y - 2), Direction = MirDirection.Down });

            // 801 Hens: two steps, kill + collect, then talk; fixed and EXP rewards
            S.QuestDefinition hens = QDef(QHens, "Hens of the Plains", QuestCategory.Side, 10, "The farmers are tired of the hens eating their crops.\nThin them out and bring me a [ITEM:9005].");
            hens.Steps.Add(new S.QuestDefinition.Step { Index = 8011, Order = 1, Title = "Thin the flock", Text = "The hens roam the fields east of town." });
            hens.Steps.Add(new S.QuestDefinition.Step { Index = 8012, Order = 2, Title = "Report to the captain" });
            hens.Objectives.Add(new S.QuestDefinition.Objective { Index = 80111, Step = 8011, Order = 1, Type = QuestObjectiveType.Kill, TargetKind = QuestTargetKind.Monster, TargetId = 1, Count = 10 });
            hens.Objectives.Add(new S.QuestDefinition.Objective { Index = 80112, Step = 8011, Order = 2, Type = QuestObjectiveType.Collect, TargetKind = QuestTargetKind.Item, TargetId = 9005, Count = 1 });
            hens.Objectives.Add(new S.QuestDefinition.Objective { Index = 80113, Step = 8011, Order = 3, Type = QuestObjectiveType.Collect, TargetKind = QuestTargetKind.Item, TargetId = 9001, Count = 3, Optional = true });
            hens.Objectives.Add(new S.QuestDefinition.Objective { Index = 80121, Step = 8012, Order = 1, Type = QuestObjectiveType.Talk, TargetKind = QuestTargetKind.Npc, TargetId = 77, Count = 1 });
            hens.Rewards.Add(new S.QuestDefinition.Reward { Scope = QuestRewardScope.Quest, Kind = QuestRewardKind.Exp, Amount = 1500 });
            hens.Rewards.Add(new S.QuestDefinition.Reward { Scope = QuestRewardScope.Quest, Kind = QuestRewardKind.Gold, Amount = 300 });
            hens.Rewards.Add(new S.QuestDefinition.Reward { Scope = QuestRewardScope.Quest, Kind = QuestRewardKind.Item, TargetId = Info("UiTestPotion").Index, Amount = 5 });
            hens.TimeLimitSeconds = 3600;

            // 802 Potion Run: Ready, with a choice group (sword or ring) at turn-in
            S.QuestDefinition potion = QDef(QPotion, "Potion Run", QuestCategory.Daily, 5, "The healer needs potions.");
            potion.RepeatPolicy = QuestRepeatPolicy.Daily;
            potion.CompleteText = "Thank you! These will save lives.";
            potion.Steps.Add(new S.QuestDefinition.Step { Index = 8021, Order = 1, Title = "Gather potions", NextStep = -1 });
            potion.Objectives.Add(new S.QuestDefinition.Objective { Index = 80211, Step = 8021, Type = QuestObjectiveType.Deliver, TargetKind = QuestTargetKind.Item, TargetId = Info("UiTestPotion").Index, Count = 5 });
            potion.Rewards.Add(new S.QuestDefinition.Reward { Scope = QuestRewardScope.Quest, Kind = QuestRewardKind.Exp, Amount = 800 });
            potion.Rewards.Add(new S.QuestDefinition.Reward { Scope = QuestRewardScope.Quest, Kind = QuestRewardKind.Gold, Amount = 1200 });
            potion.Rewards.Add(new S.QuestDefinition.Reward { Scope = QuestRewardScope.Quest, Kind = QuestRewardKind.Item, TargetId = Info("UiTestSword").Index, Amount = 1, ChoiceGroup = 1 });
            potion.Rewards.Add(new S.QuestDefinition.Reward { Scope = QuestRewardScope.Quest, Kind = QuestRewardKind.Item, TargetId = Info("UiTestRing").Index, Amount = 1, ChoiceGroup = 1 });

            // 803 The Old Mine: offered by the NPC; a time limit
            S.QuestDefinition mine = QDef(QMine, "The Old Mine", QuestCategory.Story, 20, "Strange noises come from the old mine.");
            mine.TimeLimitSeconds = 1800;
            mine.Steps.Add(new S.QuestDefinition.Step { Index = 8031, Order = 1, Title = "Enter the mine" });
            mine.Steps.Add(new S.QuestDefinition.Step { Index = 8032, Order = 2, Title = "Find the source" });
            mine.Objectives.Add(new S.QuestDefinition.Objective { Index = 80311, Step = 8031, Type = QuestObjectiveType.ReachArea, TargetKind = QuestTargetKind.Map, TargetId = 2, TargetName = "Old Mine", X = 120, Y = 88, Radius = 5, Count = 1 });
            mine.Rewards.Add(new S.QuestDefinition.Reward { Scope = QuestRewardScope.Quest, Kind = QuestRewardKind.Exp, Amount = 5000 });

            // 805 Wolf Hunt: failed (the time ran out)
            S.QuestDefinition wolves = QDef(QWolves, "Wolf Hunt", QuestCategory.Side, 15, "Hunt the wolves before nightfall.");
            wolves.FailPolicy = QuestFailPolicy.Fail;
            wolves.Steps.Add(new S.QuestDefinition.Step { Index = 8051, Order = 1, Title = "Hunt" });
            wolves.Objectives.Add(new S.QuestDefinition.Objective { Index = 80511, Step = 8051, Type = QuestObjectiveType.Kill, TargetKind = QuestTargetKind.MonsterGroup, TargetId = 3, TargetName = "Wolf", Count = 6 });

            // 804 Wolf Pelts: in history only, repeatable (its definition is sent: the repeat timer)
            S.QuestDefinition pelts = QDef(QPelts, "Wolf Pelts", QuestCategory.Repeatable, 25, "Pelts fetch a good price.");
            pelts.RepeatPolicy = QuestRepeatPolicy.Cooldown;
            pelts.CooldownSeconds = 7200;
            pelts.Steps.Add(new S.QuestDefinition.Step { Index = 8041, Order = 1, Title = "Bring pelts" });

            foreach (S.QuestDefinition d in new[] { hens, potion, mine, wolves, pelts }) Feed(d);
            long now = CMain.Now.ToBinary();
            Feed(new S.QuestStates
            {
                Full = true,
                States = new List<S.QuestStates.Entry>
                {
                    new S.QuestStates.Entry { Quest = QHens, Status = QuestRunStatus.Active, Step = 8011, ExpiresAt = CMain.Now.AddMinutes(42).ToBinary() },
                    new S.QuestStates.Entry { Quest = QPotion, Status = QuestRunStatus.Ready, Step = -1 },
                    new S.QuestStates.Entry { Quest = QWolves, Status = QuestRunStatus.Failed, Step = 8051, ExpiresAt = CMain.Now.AddMinutes(-3).ToBinary() },
                },
            });
            Feed(new S.QuestProgress { Progress = new List<S.QuestProgress.Entry>
            {
                new S.QuestProgress.Entry { Quest = QHens, Objective = 80111, Value = 3 },
                new S.QuestProgress.Entry { Quest = QHens, Objective = 80112, Value = 1 },
                new S.QuestProgress.Entry { Quest = QPotion, Objective = 80211, Value = 5 },
                new S.QuestProgress.Entry { Quest = QWolves, Objective = 80511, Value = 2 },
            } });
            Feed(new S.QuestHistory
            {
                Full = true,
                History = new List<S.QuestHistory.Entry>
                {
                    new S.QuestHistory.Entry { Quest = QFirst, Name = "First Steps", Times = 1, LastAt = now },
                    new S.QuestHistory.Entry { Quest = QPelts, Name = "Wolf Pelts", Times = 3, LastAt = now, AvailableAt = CMain.Now.AddMinutes(95).ToBinary() },
                    new S.QuestHistory.Entry { Quest = QOld, Name = "A Forgotten Errand", Times = 1, LastAt = now },
                },
            });
            Feed(new S.QuestAvailability { Npcs = new List<S.QuestAvailability.Entry>
            {
                new S.QuestAvailability.Entry { ObjectID = QNpc, Icon = QuestIcon.QuestionYellow, Offered = new List<int> { QMine }, Finishing = new List<int> { QPotion, QHens } },
            } });
            // the tracker: the hens quest tracked (as a saved id would be)
            if (IsModern) ModernUiNow?.Tracker?.Track(QHens, true, false);
            else ClassicTrack(QHens);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>The quest log row of a quest (modern).</summary>
        private static MirControl QRow(int quest)
        {
            QuestsWindow w = QuestsWin;
            if (w == null) return null;
            for (int i = 0; w.Row(i) != null; i++)
                if (w.RowQuest(i) == quest) return w.Row(i);
            return null;
        }

        /// <summary>A button in the NPC window's quest card of a quest (cards are in the order Finishing, then Offered).</summary>
        private static MirControl QNpcButton(int quest, string text)
        {
            MirControl card = QNpcCard(quest);
            return card == null ? null : FindButton(card, text);
        }

        private static MirControl QNpcCard(int quest)
        {
            NpcWindow w = Win<NpcWindow>(NpcWindow.WindowKey);
            if (w?.QuestList == null) return null;
            var order = new List<int>();
            QuestBook book = GameScene.Models.Quests;
            if (book.Npcs.TryGetValue(QNpc, out S.QuestAvailability.Entry e))
            {
                foreach (int q in e.Finishing) if (book.Def(q) != null && book.Run(q) != null) order.Add(q);
                foreach (int q in e.Offered) if (book.Def(q) != null && book.Run(q) == null) order.Add(q);
            }
            int want = order.IndexOf(quest), n = 0;
            foreach (MirControl c in w.QuestList.Content.Controls)
                if (c is PaintBox && n++ == want) return c;
            return null;
        }

        /// <summary>The n-th item cell of a choice group row in an NPC quest card (0-based over the card's item cells after the fixed ones).</summary>
        private static MirControl QNpcRewardCell(int quest, int choice)
        {
            MirControl card = QNpcCard(quest);
            if (card == null) return null;
            var cells = new List<MirControl>();
            foreach (MirControl c in card.Controls) if (c is NpcItemCell) cells.Add(c);
            // 802 has no fixed items: the cells are the choice group's sword, ring
            return choice < cells.Count ? cells[choice] : null;
        }

        private static void DumpQuestState(List<string> log)
        {
            QuestBook book = GameScene.Models?.Quests;
            if (book == null || GameScene.User == null) return;
            var runs = new List<string>();
            foreach (QuestRun r in book.Runs) runs.Add(r.Quest + ":" + r.Status + "@" + r.Step);
            log.Add($"   [quests] runs=[{string.Join(",", runs)}] history={book.History.Count} defs={book.Definitions.Count} npcs={book.Npcs.Count} tracked=[{string.Join(",", Settings.TrackedQuests)}]");
            if (IsModern)
            {
                if (QuestsWin is QuestsWindow qw) log.Add($"   [quests] log tab={qw.TabShown} picked={qw.PickedShown}");
                QuestToastHud toast = null;
                if (Root != null) foreach (MirControl c in Root.Hud.Controls) if (c is QuestToastHud t) toast = t;
                if (toast != null && toast.ShownText.Length > 0) log.Add("   [quests] toast " + toast.ShownText);
                if (ModernUiNow?.Tracker is var tr && tr != null) log.Add($"   [quests] tracker visible={tr.Visible} ids=[{string.Join(",", tr.Tracked)}]");
            }
            else
            {
                log.Add("   [quests] classic " + ClassicQuestDump());
            }
        }
    }
}

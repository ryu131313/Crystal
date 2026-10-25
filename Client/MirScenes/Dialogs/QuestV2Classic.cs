using Client.MirNetwork;
using Client.MirScenes.Models;
using S = ServerPackets;
using C = ClientPackets;

namespace Client.MirScenes.Dialogs
{
    /// <summary>
    /// The classic quest dialogs' minimal quest V2 adapter (Part B phase 3, "Classic UIs"): projects GameScene.Models.Quests into
    /// the ClientQuestProgress / ClientQuestInfo shapes QuestDialogs.cs already draws (the current step's objectives as client-built
    /// text lines, quest-scope rewards), and sends the V2 packets for accept / turn in / abandon / share. No bars, markers, branch
    /// choices or other new classic features. Built on demand (the dialogs rebuild on IGameUi.QuestsChanged only).
    /// </summary>
    public static class QuestV2Classic
    {
        private static QuestBook Book => GameScene.Models?.Quests;
        private static readonly List<S.QuestDefinition.Objective> _objectives = new List<S.QuestDefinition.Objective>();

        /// <summary>The reward positions (in the definition's Rewards list) behind each RewardsSelectItem entry, per quest.</summary>
        private static readonly Dictionary<int, List<int>> _selectPositions = new Dictionary<int, List<int>>();

        /// <summary>The player's runs (Active / Ready / Failed) whose definition has arrived.</summary>
        public static List<ClientQuestProgress> Current()
        {
            var list = new List<ClientQuestProgress>();
            QuestBook book = Book;
            if (book == null) return list;
            for (int i = 0; i < book.Runs.Count; i++)
            {
                ClientQuestProgress p = Project(book.Runs[i].Quest, 0);
                if (p != null) list.Add(p);
            }
            return list;
        }

        /// <summary>What this NPC offers (not taken) and finishes (taken; Completed = Ready) for the player, as the server said.</summary>
        public static List<ClientQuestProgress> ForNpc(uint npc)
        {
            var list = new List<ClientQuestProgress>();
            QuestBook book = Book;
            if (book == null || !book.Npcs.TryGetValue(npc, out S.QuestAvailability.Entry e)) return list;
            for (int i = 0; i < e.Finishing.Count; i++)
            {
                ClientQuestProgress p = Project(e.Finishing[i], npc);
                if (p != null) list.Add(p);
            }
            for (int i = 0; i < e.Offered.Count; i++)
            {
                if (book.Run(e.Offered[i]) != null) continue;
                ClientQuestProgress p = Project(e.Offered[i], npc);
                if (p != null) list.Add(p);
            }
            return list;
        }

        public static int ActiveCount => Book?.ActiveCount ?? 0;

        /// <summary>One quest as the classic shapes. <paramref name="npc"/> = the NPC whose dialog shows it (0 = the log / tracker).</summary>
        private static ClientQuestProgress Project(int quest, uint npc)
        {
            QuestBook book = Book;
            S.QuestDefinition def = book?.Def(quest);
            if (def == null) return null;
            QuestRun run = book.Run(quest);
            bool ready = run != null && run.Status == QuestRunStatus.Ready;
            bool failed = run != null && run.Status == QuestRunStatus.Failed;
            bool completedBefore = book.Done(quest) != null;

            var info = new ClientQuestInfo
            {
                Index = quest,
                Name = def.Name,
                Group = string.IsNullOrEmpty(def.GroupName) ? CategoryName(def.Category) : def.GroupName,
                Type = def.Category switch
                {
                    QuestCategory.Story => QuestType.Story,
                    QuestCategory.Daily => QuestType.Daily,
                    QuestCategory.Weekly => QuestType.Daily,
                    QuestCategory.Repeatable => QuestType.Repeatable,
                    _ => QuestType.General,
                },
                MinLevelNeeded = def.LevelHint,
                MaxLevelNeeded = 0,
                ClassNeeded = RequiredClass.None,
                TimeLimitInSeconds = def.TimeLimitSeconds,
                // a Ready quest at its finishing NPC shows the turn-in text (QuestMessage: Taken, not SameFinishNPC, CurrentNPCID = finish)
                NPCIndex = ready && npc != 0 ? 0 : npc,
                FinishNPCIndex = npc,
            };
            AddLines(info.Description, def.Summary);
            if (!string.IsNullOrEmpty(def.CompleteText)) AddLines(info.CompletionDescription, def.CompleteText);

            var progress = new ClientQuestProgress { Id = quest, QuestInfo = info, Taken = run != null, Completed = ready };

            int step = run != null ? run.Step : FirstStep(def);
            if (failed) progress.TaskList.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.QuestFailedLine));
            else if (ready)
            {
                string finish = QuestText.EndpointName(def, QuestEndpointRole.Finish);
                if (finish.Length > 0) info.ReturnDescription.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.QuestReturnTo, finish));
                progress.TaskList.Add(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.QuestReadyLine));
            }
            if (step >= 0)
            {
                QuestBook.ObjectivesOf(def, step, _objectives);
                for (int i = 0; i < _objectives.Count; i++)
                {
                    S.QuestDefinition.Objective o = _objectives[i];
                    if (!book.Shown(quest, o)) continue;
                    info.TaskDescription.Add(QuestText.Label(o));
                    if (run != null && !failed) progress.TaskList.Add(QuestText.Line(o, book.Value(quest, o.Index)));
                }
            }

            // rewards: quest scope only; currencies as numbers, fixed items, and one choice group as the classic "select one" row
            var positions = new List<int>();
            for (int i = 0; i < def.Rewards.Count; i++)
            {
                S.QuestDefinition.Reward r = def.Rewards[i];
                if (!QuestText.RewardShown(r, completedBefore)) continue;
                uint amount = (uint)Math.Clamp(r.Amount, 0, uint.MaxValue);
                switch (r.Kind)
                {
                    case QuestRewardKind.Exp: if (r.ChoiceGroup == 0) info.RewardExp += amount; break;
                    case QuestRewardKind.Gold: if (r.ChoiceGroup == 0) info.RewardGold += amount; break;
                    case QuestRewardKind.Credit: if (r.ChoiceGroup == 0) info.RewardCredit += amount; break;
                    case QuestRewardKind.Item:
                        ItemInfo item = QuestText.ItemInfo(r.TargetId);
                        if (item == null) break;
                        var reward = new QuestItemReward { Item = item, Count = (ushort)Math.Clamp(r.Amount, 1, ushort.MaxValue) };
                        if (r.ChoiceGroup == 0) info.RewardsFixedItem.Add(reward);
                        else
                        {
                            info.RewardsSelectItem.Add(reward);
                            positions.Add(i);
                        }
                        break;
                }
            }
            _selectPositions[quest] = positions;
            return progress;
        }

        private static int FirstStep(S.QuestDefinition def)
        {
            int best = -1, order = int.MaxValue;
            for (int i = 0; i < def.Steps.Count; i++)
                if (def.Steps[i].Order < order) { order = def.Steps[i].Order; best = def.Steps[i].Index; }
            return best;
        }

        private static void AddLines(List<string> into, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++) into.Add(lines[i]);
        }

        private static string CategoryName(QuestCategory c) => GameLanguage.ClientTextMap.GetLocalization(c switch
        {
            QuestCategory.Story => ClientTextKeys.ModernSocialQTypeStory,
            QuestCategory.Daily => ClientTextKeys.ModernSocialQTypeDaily,
            QuestCategory.Weekly => ClientTextKeys.ModernQuestCatWeekly,
            QuestCategory.Repeatable => ClientTextKeys.ModernSocialQTypeRepeatable,
            QuestCategory.Event => ClientTextKeys.ModernQuestCatEvent,
            QuestCategory.Guild => ClientTextKeys.ModernQuestCatGuild,
            _ => ClientTextKeys.ModernSocialQTypeGeneral,
        });

        // ------------------------------------------------------------------ sends

        public static void Accept(int quest, uint npc) => Network.Enqueue(new C.QuestAccept { Quest = quest, NpcObjectID = npc });

        /// <summary>C.QuestTurnIn; <paramref name="selectIndex"/> = the RewardsSelectItem pick (-1 = none) mapped to its reward position.</summary>
        public static void TurnIn(int quest, uint npc, int selectIndex)
        {
            var picks = new List<int>();
            if (selectIndex >= 0 && _selectPositions.TryGetValue(quest, out List<int> positions) && selectIndex < positions.Count) picks.Add(positions[selectIndex]);
            Network.Enqueue(new C.QuestTurnIn { Quest = quest, NpcObjectID = npc, RewardPicks = picks });
        }

        public static void Abandon(int quest) => Network.Enqueue(new C.QuestAbandon { Quest = quest });

        public static void Share(int quest) => Network.Enqueue(new C.QuestShare { Quest = quest });
    }
}

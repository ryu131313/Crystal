using Server.MirDatabase;
using Server.MirEnvir.Quests;
using S = ServerPackets;

namespace Server.MirObjects
{
    public sealed partial class PlayerQuests
    {
        /// <summary>The quest's display name: its NameKey text, else the legacy name.</summary>
        public string QuestName(QuestRuntime q)
        {
            var name = Catalog.Text(q.Def.NameKey);
            return string.IsNullOrEmpty(name) ? q.Info.Name ?? string.Empty : name;
        }

        /// <summary>Sends S.QuestDefinition once per connection (the sent set, keyed by quest index; Part B "Live edits").</summary>
        public void EnsureDefinition(QuestRuntime q)
        {
            var connection = _player.Connection;
            if (q == null || connection == null || !connection.SentQuestDefinitions.Add(q.Index)) return;
            _player.Enqueue(BuildDefinition(q));
        }

        /// <summary>C.QuestDefinitionRequest: answered only for a quest in this player's runs, history or offers.</summary>
        public void DefinitionRequest(int quest)
        {
            if (!_runs.ContainsKey(quest) && !_history.ContainsKey(quest) && !_offers.Contains(quest)) return;
            var q = Catalog.Get(quest);
            if (q == null) return;
            _player.Connection?.SentQuestDefinitions.Remove(quest);
            EnsureDefinition(q);
        }

        /// <summary>
        /// Class or gender changed: rewards in a definition are filtered by both, so the sent set is cleared and the definitions of
        /// the runs and of givers in view are sent again.
        /// </summary>
        public void ClassOrGenderChanged()
        {
            _player.Connection?.SentQuestDefinitions.Clear();
            foreach (var run in _runs.Values) EnsureDefinition(Catalog.Get(run.Quest));
            _npcSent.Clear();
            MarkAvailabilityDirty();
        }

        /// <summary>
        /// Builds one quest's S.QuestDefinition from copies of the definition values (never Main's records) in the server language;
        /// rewards filtered by this player's class and gender, exp / gold with the rates. Also makes sure the client has the
        /// item / monster / NPC infos the quest names (the existing on-demand rules).
        /// </summary>
        private S.QuestDefinition BuildDefinition(QuestRuntime q)
        {
            var def = q.Def;
            var c = Catalog;
            var p = new S.QuestDefinition
            {
                Quest = q.Index,
                Name = QuestName(q),
                Summary = LinkText(c.Text(def.SummaryKey)),
                CompleteText = LinkText(c.Text(def.CompleteKey)),
                Category = def.Category,
                StorylineIndex = def.StorylineIndex,
                LevelHint = def.LevelHint,
                RepeatPolicy = def.RepeatPolicy,
                CooldownSeconds = def.CooldownSeconds,
                TimeLimitSeconds = def.TimeLimitSeconds,
                FailPolicy = def.FailPolicy,
                AbandonAllowed = def.AbandonAllowed,
                Flags = def.Flags,
                GroupName = def.GroupName ?? string.Empty,
            };

            for (var i = 0; i < q.StepsByOrder.Count; i++)
            {
                var s = q.StepsByOrder[i];
                p.Steps.Add(new S.QuestDefinition.Step
                {
                    Index = s.Index,
                    Order = s.Order,
                    Title = LinkText(c.Text(s.TitleKey)),
                    Text = LinkText(c.Text(s.TextKey)),
                    ReadyText = LinkText(c.Text(s.ReadyTextKey)),
                    CompletionMode = s.CompletionMode,
                    NRequired = s.NRequired,
                    NextStep = s.NextStep,
                    TimeLimitSeconds = s.TimeLimitSeconds,
                });

                var objectives = q.ObjectivesOf(s.Index);
                for (var j = 0; j < objectives.Count; j++)
                {
                    var o = objectives[j];
                    p.Objectives.Add(new S.QuestDefinition.Objective
                    {
                        Index = o.Index,
                        Step = o.Step,
                        Order = o.Order,
                        Type = o.Type,
                        TargetKind = o.TargetKind,
                        TargetId = o.TargetId,
                        TargetName = TargetName(o),
                        Count = Math.Max(1, o.Count),
                        Map = o.Map,
                        X = o.X,
                        Y = o.Y,
                        Radius = o.Radius,
                        Optional = o.Optional,
                        Hidden = o.Hidden,
                        Text = LinkText(c.Text(o.TextKey)),
                    });
                    EnsureTargetInfo(o);
                }

                var branches = q.BranchesOf(s.Index);
                for (var j = 0; j < branches.Count; j++)
                    p.Branches.Add(new S.QuestDefinition.Branch
                    {
                        Step = branches[j].Step,
                        ChoiceKey = branches[j].ChoiceKey ?? string.Empty,
                        Text = LinkText(c.Text(branches[j].TextKey)),
                        Outcome = branches[j].Outcome,
                    });
            }

            var rewards = FilteredRewards(q);
            for (var i = 0; i < rewards.Count; i++)
            {
                var r = rewards[i];
                p.Rewards.Add(new S.QuestDefinition.Reward
                {
                    Scope = r.Scope,
                    ScopeId = r.ScopeId,
                    Kind = r.Kind,
                    TargetId = r.TargetId,
                    Amount = RatedAmount(r),
                    ChoiceGroup = r.ChoiceGroup,
                    FirstTimeOnly = r.FirstTimeOnly,
                });
                if (r.Kind == QuestRewardKind.Item) CheckItemInfo(r.TargetId);
            }

            for (var i = 0; i < def.Endpoints.Count; i++)
            {
                var e = def.Endpoints[i];
                if (e == null) continue;
                var name = string.Empty;
                if (e.Kind == QuestEndpointKind.Npc) name = Envir.GetNPCInfo(e.TargetId)?.Name ?? string.Empty;
                else if (e.Kind == QuestEndpointKind.ItemUse) { name = Envir.GetItemInfo(e.TargetId)?.FriendlyName ?? string.Empty; CheckItemInfo(e.TargetId); }
                p.Endpoints.Add(new S.QuestDefinition.Endpoint { Role = e.Role, Kind = e.Kind, TargetId = e.TargetId, Name = name, Map = e.Map });
            }

            return p;
        }

        /// <summary>A label for targets the client cannot name itself (groups, flags, maps); "" for a single item / monster / NPC.</summary>
        private string TargetName(QuestObjectiveInfo o)
        {
            switch (o.TargetKind)
            {
                case QuestTargetKind.MonsterGroup:
                case QuestTargetKind.ItemGroup:
                    if (!Catalog.Groups.TryGetValue(o.TargetId, out var g) || g.Members.Count == 0) return string.Empty;
                    var first = o.TargetKind == QuestTargetKind.MonsterGroup
                        ? Envir.GetMonsterInfo(g.Members[0].TargetId)?.GameName
                        : Envir.GetItemInfo(g.Members[0].TargetId)?.FriendlyName;
                    return first ?? string.Empty;
                case QuestTargetKind.Flag:
                    return "#" + o.TargetId;
                case QuestTargetKind.Map:
                    return Envir.GetMapInfo(o.TargetId)?.Title ?? string.Empty;
                default:
                    return string.Empty;
            }
        }

        private void EnsureTargetInfo(QuestObjectiveInfo o)
        {
            var connection = _player.Connection;
            if (connection == null) return;
            switch (o.TargetKind)
            {
                case QuestTargetKind.Monster: connection.CheckMonsterInfo(o.TargetId); break;
                case QuestTargetKind.Item: CheckItemInfo(o.TargetId); break;
                case QuestTargetKind.Npc: connection.CheckNPCInfo(o.TargetId); break;
                case QuestTargetKind.MonsterGroup:
                case QuestTargetKind.ItemGroup:
                    if (!Catalog.Groups.TryGetValue(o.TargetId, out var g)) break;
                    for (var i = 0; i < g.Members.Count; i++)
                    {
                        if (o.TargetKind == QuestTargetKind.MonsterGroup) connection.CheckMonsterInfo(g.Members[i].TargetId);
                        else CheckItemInfo(g.Members[i].TargetId);
                    }
                    break;
            }
        }

        private void CheckItemInfo(int itemIndex)
        {
            var info = Envir.GetItemInfo(itemIndex);
            if (info != null) _player.Connection?.CheckItemInfo(info);
        }

        /// <summary>Quest markup links (&lt;$ITEM:n&gt; …) resolved as legacy CreateClientQuestInfo did, sending the linked infos.</summary>
        private string LinkText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var connection = _player.Connection;
            return LinkFormatter.ReplacePlaceholders(text, (type, index) =>
            {
                if (connection == null) return;
                switch (type)
                {
                    case "ITEM":
                        var item = Envir.GetItemInfo(index);
                        if (item != null) connection.CheckItemInfo(item);
                        break;
                    case "MONSTER":
                        connection.CheckMonsterInfo(index);
                        break;
                    case "NPC":
                        connection.CheckNPCInfo(index);
                        break;
                }
            }) ?? string.Empty;
        }

        private S.QuestHistory.Entry HistoryEntry(QuestHistoryEntry h)
        {
            var q = Catalog.Get(h.Quest);
            long availableAt = 0;
            if (q != null && q.Def.RepeatPolicy != QuestRepeatPolicy.Once)
            {
                var at = AvailableAgainAt(q, h);
                if (at != DateTime.MinValue && at != DateTime.MaxValue && at > Envir.Now) availableAt = at.ToBinary();
            }

            return new S.QuestHistory.Entry
            {
                Quest = h.Quest,
                Name = q != null ? QuestName(q) : Envir.GetQuestInfo(h.Quest)?.Name ?? string.Empty,
                Times = h.Times,
                LastAt = h.LastAt,
                AvailableAt = availableAt,
            };
        }
    }
}

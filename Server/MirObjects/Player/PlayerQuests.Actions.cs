using Server.MirDatabase;
using Server.MirEnvir.Quests;
using S = ServerPackets;

namespace Server.MirObjects
{
    public sealed partial class PlayerQuests
    {
        // ------------------------------------------------------------------ availability

        /// <summary>
        /// The repeat policy allows taking <paramref name="q"/> now (Part B "Rules: Availability"): Once → never completed; Daily /
        /// Weekly → last completion before the last reset boundary (<see cref="ResetClock"/>); Cooldown → CooldownSeconds since the last
        /// completion; Unlimited → always; MaxCompletions (when set) caps the repeatable policies.
        /// </summary>
        public bool RepeatAllows(QuestRuntime q, DateTime now)
        {
            if (!_history.TryGetValue(q.Index, out var h) || h.Times <= 0) return true;
            var def = q.Def;
            if (def.RepeatPolicy == QuestRepeatPolicy.Once) return false;
            if (def.MaxCompletions > 0 && h.Times >= def.MaxCompletions) return false;
            return AvailableAgainAt(q, h) <= now;
        }

        /// <summary>When a completed repeatable quest can be taken again (DateTime.MinValue = now; MaxValue = never).</summary>
        private static DateTime AvailableAgainAt(QuestRuntime q, QuestHistoryEntry h)
        {
            var def = q.Def;
            if (def.RepeatPolicy == QuestRepeatPolicy.Once) return DateTime.MaxValue;
            if (def.MaxCompletions > 0 && h.Times >= def.MaxCompletions) return DateTime.MaxValue;
            var last = h.LastAt == 0 ? DateTime.MinValue : DateTime.FromBinary(h.LastAt);
            switch (def.RepeatPolicy)
            {
                case QuestRepeatPolicy.Daily: return last == DateTime.MinValue ? DateTime.MinValue : ResetClock.LastDailyReset(last).AddDays(1);
                case QuestRepeatPolicy.Weekly: return last == DateTime.MinValue ? DateTime.MinValue : ResetClock.LastWeeklyReset(last).AddDays(7);
                case QuestRepeatPolicy.Cooldown: return last == DateTime.MinValue ? DateTime.MinValue : last.AddSeconds(Math.Max(0, def.CooldownSeconds));
                default: return DateTime.MinValue;
            }
        }

        /// <summary>Offered to this player: no run, the repeat policy allows it and the accept condition holds.</summary>
        public bool CanTake(QuestRuntime q)
        {
            if (q == null || _runs.ContainsKey(q.Index)) return false;
            if (!RepeatAllows(q, Envir.Now)) return false;
            return EvaluateCondition(q.Def.AcceptCondition);
        }

        /// <summary>Shown as available ("!"): CanTake, plus the VisibleCondition when one is set.</summary>
        private bool IsOffered(QuestRuntime q)
        {
            if ((q.Def.Flags & QuestFlags.HiddenInLog) != 0 && q.Def.Category == QuestCategory.Hidden) return false;
            if (!CanTake(q)) return false;
            return q.Def.VisibleCondition == 0 || EvaluateCondition(q.Def.VisibleCondition);
        }

        // NPC object id → signature of the entry last sent, so walking past an NPC re-sends nothing unless it changed.
        private readonly Dictionary<uint, long> _npcSent = new Dictionary<uint, long>();

        /// <summary>An NPC's S.ObjectNPC was just sent to this player (it came into view): its availability entry is re-sent.</summary>
        public void NoteNpcInView(NPCObject npc)
        {
            if (npc?.Info == null) return;
            if (Catalog.StartsAt(npc.Info.Index).Count == 0 && Catalog.FinishesAt(npc.Info.Index).Count == 0) return;
            _npcSent.Remove(npc.ObjectID);
            MarkAvailabilityDirty();
        }

        /// <summary>A signature no entry has: the entry is re-sent at the next availability pass.</summary>
        private const long StaleNpcEntry = long.MinValue;

        /// <summary>
        /// The quest definitions changed (the wave 3 quest-replace hook, admin path): every NPC entry the client holds is re-sent at the next
        /// pass, and an NPC that no longer gives or takes any quest gets an empty entry, so its icon and quest list are cleared.
        /// </summary>
        private void MarkNpcEntriesStale()
        {
            foreach (var key in new List<uint>(_npcSent.Keys)) _npcSent[key] = StaleNpcEntry;
        }

        /// <summary>The player changed map (the client drops every object): every entry is re-sent.</summary>
        public void MapChanged()
        {
            _npcSent.Clear();
            MarkAvailabilityDirty();
        }

        /// <summary>S.QuestAvailability for the NPCs in view whose entry changed (game thread, when dirty only).</summary>
        private void SendAvailability()
        {
            _availabilityDirty = false;
            var map = _player.CurrentMap;
            if (map == null) return;
            S.QuestAvailability packet = null;
            var npcs = map.NPCs;
            for (var i = 0; i < npcs.Count; i++)
            {
                var npc = npcs[i];
                if (npc?.Info == null || !npc.Visible) continue;
                if (!Functions.InRange(npc.CurrentLocation, _player.CurrentLocation, Globals.DataRange)) continue;
                if (!npc.VisibleLog.TryGetValue(Info.Index, out var seen) || !seen) continue;
                var starts = Catalog.StartsAt(npc.Info.Index);
                var finishes = Catalog.FinishesAt(npc.Info.Index);
                if (starts.Count == 0 && finishes.Count == 0)
                {
                    // The client still shows an entry from before a definition change: clear it (icon None, no quests).
                    if (_npcSent.Remove(npc.ObjectID))
                        (packet ??= new S.QuestAvailability()).Npcs.Add(new S.QuestAvailability.Entry { ObjectID = npc.ObjectID, Icon = QuestIcon.None });
                    continue;
                }

                var entry = BuildNpcEntry(npc, starts, finishes, out var signature);
                if (_npcSent.TryGetValue(npc.ObjectID, out var old) && old == signature) continue;
                _npcSent[npc.ObjectID] = signature;
                (packet ??= new S.QuestAvailability()).Npcs.Add(entry);
            }

            if (packet != null) _player.Enqueue(packet);
        }

        private S.QuestAvailability.Entry BuildNpcEntry(NPCObject npc, List<QuestRuntime> starts, List<QuestRuntime> finishes, out long signature)
        {
            var entry = new S.QuestAvailability.Entry { ObjectID = npc.ObjectID };
            var best = 0;
            unchecked
            {
                long hash = 17;
                for (var i = 0; i < finishes.Count; i++)
                {
                    var q = finishes[i];
                    if (!_runs.TryGetValue(q.Index, out var run) || run.Status != QuestRunStatus.Active) continue;
                    var ready = IsReady(q, run);
                    entry.Finishing.Add(q.Index);
                    EnsureDefinition(q);
                    hash = hash * 31 + q.Index * 2 + (ready ? 1 : 0);
                    best = Better(best, ready ? IconRank(q, ready: true) : 1, ref entry.Icon, ready ? ReadyIcon(q) : QuestIcon.QuestionWhite);
                }

                for (var i = 0; i < starts.Count; i++)
                {
                    var q = starts[i];
                    if (!IsOffered(q)) continue;
                    entry.Offered.Add(q.Index);
                    EnsureDefinition(q);
                    hash = hash * 31 + q.Index * 2 + 1_000_003;
                    best = Better(best, IconRank(q, ready: false), ref entry.Icon, OfferIcon(q));
                }

                // Running quests whose current step has branches, choosable here (any Start or Finish endpoint, as Choose accepts):
                // a met step shows the Ready icon (a choice is due), an unmet one the in-progress icon.
                hash = AddChoosing(entry, finishes, hash, ref best);
                hash = AddChoosing(entry, starts, hash, ref best);

                signature = hash * 31 + (int)entry.Icon;
            }

            return entry;
        }

        private long AddChoosing(S.QuestAvailability.Entry entry, List<QuestRuntime> list, long hash, ref int best)
        {
            unchecked
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var q = list[i];
                    var due = ChoiceState(q);
                    if (due == 0 || entry.Choosing.Contains(q.Index)) continue;
                    entry.Choosing.Add(q.Index);
                    EnsureDefinition(q);
                    hash = hash * 31 + q.Index * 4 + due + 2_000_029;
                    best = Better(best, due == 2 ? IconRank(q, ready: true) : 1, ref entry.Icon, due == 2 ? ReadyIcon(q) : QuestIcon.QuestionWhite);
                }
            }

            return hash;
        }

        /// <summary>0 = no branch choice on the run's current step (or no Active run); 1 = branches, step not met; 2 = a choice is due.</summary>
        private byte ChoiceState(QuestRuntime q)
        {
            if (!_runs.TryGetValue(q.Index, out var run) || run.Status != QuestRunStatus.Active || run.Step < 0) return 0;
            if (!q.StepsById.TryGetValue(run.Step, out var step) || q.BranchesOf(step.Index).Count == 0) return 0;
            return StepMet(q, run, step) ? (byte)2 : (byte)1;
        }

        // Icon priority (the client used to take its first available quest, UpdateBestQuestIcon): a quest ready to turn in (or a
        // branch choice due), then an offer, then a quest in progress at its finish NPC (or a branch step not met yet).
        private static int IconRank(QuestRuntime q, bool ready) => ready ? 3 : 2;

        private static int Better(int best, int rank, ref QuestIcon icon, QuestIcon candidate)
        {
            if (rank <= best) return best;
            icon = candidate;
            return rank;
        }

        private static QuestIcon OfferIcon(QuestRuntime q)
        {
            switch (q.Def.Category)
            {
                case QuestCategory.Story: return QuestIcon.ExclamationGreen;
                case QuestCategory.Daily:
                case QuestCategory.Weekly: return QuestIcon.ExclamationBlue;
                default: return QuestIcon.ExclamationYellow;
            }
        }

        private static QuestIcon ReadyIcon(QuestRuntime q)
        {
            switch (q.Def.Category)
            {
                case QuestCategory.Story: return QuestIcon.QuestionGreen;
                case QuestCategory.Daily:
                case QuestCategory.Weekly: return QuestIcon.QuestionBlue;
                default: return QuestIcon.QuestionYellow;
            }
        }

        // ------------------------------------------------------------------ endpoints

        /// <summary>The NPC with <paramref name="objectId"/> on the player's map, in DataRange and visible to the player, or null.</summary>
        private NPCObject NpcInReach(uint objectId)
        {
            var map = _player.CurrentMap;
            if (map == null || objectId == 0) return null;
            for (var i = map.NPCs.Count - 1; i >= 0; i--)
            {
                var npc = map.NPCs[i];
                if (npc.ObjectID != objectId) continue;
                if (!Functions.InRange(npc.CurrentLocation, _player.CurrentLocation, Globals.DataRange)) return null;
                if (!npc.Visible || !npc.VisibleLog.TryGetValue(Info.Index, out var seen) || !seen) return null;
                return npc;
            }

            return null;
        }

        // ------------------------------------------------------------------ accept

        /// <summary>
        /// C.QuestAccept: endpoint (an NPC Start endpoint in reach, or a pending offer when <paramref name="npcObjectId"/> is 0), the
        /// accept condition and repeat policy, the concurrent cap, room in the quest bag for carry items; then the run, Accept rewards,
        /// the index and the client updates.
        /// </summary>
        public void Accept(int quest, uint npcObjectId)
        {
            const QuestRequestKind kind = QuestRequestKind.Accept;
            var q = Catalog.Get(quest);
            if (q == null) { Refuse(quest, kind, QuestRefusal.UnknownQuest); return; }
            if (_runs.ContainsKey(quest)) { Refuse(quest, kind, QuestRefusal.AlreadyActive); return; }
            if (_player.Dead) { Refuse(quest, kind, QuestRefusal.Dead); return; }

            var sharedFrom = 0;
            if (npcObjectId != 0)
            {
                var npc = NpcInReach(npcObjectId);
                if (npc == null || !Catalog.IsEndpoint(q, QuestEndpointRole.Start, npc.Info.Index)) { Refuse(quest, kind, QuestRefusal.NotAtNpc); return; }
            }
            else if (!_offers.Contains(quest)) { Refuse(quest, kind, QuestRefusal.NotAvailable); return; }
            else _offerSharedFrom.TryGetValue(quest, out sharedFrom);

            if (ActiveCount >= Globals.MaxConcurrentQuests) { Refuse(quest, kind, QuestRefusal.QuestLogFull); return; }
            if (!RepeatAllows(q, Envir.Now)) { Refuse(quest, kind, QuestRefusal.AlreadyCompleted); return; }
            if (!EvaluateCondition(q.Def.AcceptCondition)) { Refuse(quest, kind, QuestRefusal.ConditionNotMet); return; }

            // Carry items (Scope = Accept) go into the quest bag: check room for all of them first, so nothing is half given.
            var carry = BuildRewardItems(q, QuestRewardScope.Accept, 0, null, out _);
            if (carry.Count > 0 && FreeQuestSlots() < carry.Count) { Refuse(quest, kind, QuestRefusal.QuestBagFull); return; }

            var now = Envir.Now;
            var first = q.FirstStep;
            var run = new QuestRunState
            {
                Quest = quest,
                Status = QuestRunStatus.Active,
                Step = first.Index,
                Revision = q.Def.Revision,
                AcceptedAt = now.ToBinary(),
                ExpiresAt = q.Def.TimeLimitSeconds > 0 ? now.AddSeconds(q.Def.TimeLimitSeconds).ToBinary() : 0,
                SharedFrom = sharedFrom,
            };

            _offers.Remove(quest);
            _offerSharedFrom.Remove(quest);
            _runs[quest] = run;
            Info.QuestRuns.Add(run);

            for (var i = 0; i < carry.Count; i++)
            {
                _player.GainQuestItem(carry[i]);
                _player.Report.ItemChanged(carry[i], carry[i].Count, 2);
            }

            EnsureDefinition(q);
            _sentStatus[quest] = QuestRunStatus.None;
            EnterStep(q, run, first.Index);
            MarkState(quest, trackHint: true);
            Reevaluate(run);
            MarkAvailabilityDirty();

            QuestLog.Info("[Quests] accept " + _player.Name + " quest " + quest + " step " + run.Step);
            _player.CallDefaultNPC(DefaultNPCType.OnAcceptQuest, quest);
        }

        /// <summary>AutoComplete quests refused for a full inventory â†’ the free slot count at that refusal.</summary>
        private readonly Dictionary<int, int> _autoTurnInBlocked = new Dictionary<int, int>();

        private int FreeInventorySlots()
        {
            var free = 0;
            var bag = Info.Inventory;
            for (var i = 0; i < bag.Length; i++) if (bag[i] == null) free++;
            return free;
        }

        private int FreeQuestSlots()
        {
            var free = 0;
            var bag = Info.QuestInventory;
            for (var i = 0; i < bag.Length; i++) if (bag[i] == null) free++;
            return free;
        }

        // ------------------------------------------------------------------ turn in

        /// <summary>
        /// C.QuestTurnIn: the run is Ready, a Finish endpoint is in reach (none needed for AutoComplete), room for the item rewards;
        /// then the objective items are taken (Collect and carry items from the quest bag, Deliver from the inventory), the rewards
        /// given (rates applied), history written, the "quests completed" stat counted and OnFinishQuest called.
        /// <paramref name="rewardPicks"/> = positions in the player's filtered S.QuestDefinition.Rewards list, one per choice group;
        /// a group without a valid pick gives nothing (as legacy FinishQuest without a selected item).
        /// </summary>
        public void TurnIn(int quest, uint npcObjectId, List<int> rewardPicks, bool auto = false)
        {
            // AutoComplete is not a client request: its refusals send no S.QuestRefused (the full-bag chat line below stays).
            const QuestRequestKind kind = QuestRequestKind.TurnIn;
            if (!_runs.TryGetValue(quest, out var run)) { if (!auto) Refuse(quest, kind, QuestRefusal.NotAvailable); return; }
            var q = Catalog.Get(quest);
            if (q == null) { if (!auto) Refuse(quest, kind, QuestRefusal.UnknownQuest); return; }
            if (!IsReady(q, run)) { if (!auto) Refuse(quest, kind, QuestRefusal.NotReady); return; }
            if (_player.Dead) { if (!auto) Refuse(quest, kind, QuestRefusal.Dead); return; }

            if (!auto)
            {
                var npc = NpcInReach(npcObjectId);
                if (npc == null || !Catalog.IsEndpoint(q, QuestEndpointRole.Finish, npc.Info.Index)) { Refuse(quest, kind, QuestRefusal.NotAtNpc); return; }
            }

            // AutoComplete retries on every re-evaluation: while the inventory's free slots are unchanged since a refusal, skip quietly.
            var free = FreeInventorySlots();
            if (auto && _autoTurnInBlocked.TryGetValue(quest, out var blockedAt) && blockedAt == free) return;

            var items = BuildRewardItems(q, QuestRewardScope.Quest, 0, rewardPicks, out var filtered);
            if (items.Count > 0 && !_player.CanGainItems(items.ToArray()))
            {
                if (auto)
                {
                    _autoTurnInBlocked[quest] = free;
                    _player.ReceiveChat(GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.CannotHandInQuestBagFull), ChatType.System);
                }
                else Refuse(quest, kind, QuestRefusal.BagFull);
                return;
            }

            _autoTurnInBlocked.Remove(quest);

            var seconds = SecondsSinceAccept(run);
            var step = run.Step;
            TakeObjectiveItems(q, run);

            var now = Envir.Now;
            var first = !_history.TryGetValue(quest, out var h);
            if (first)
            {
                h = new QuestHistoryEntry { Quest = quest, FirstAt = now.ToBinary() };
                _history[quest] = h;
                Info.QuestHistory.Add(h);
            }

            h.Times++;
            h.LastAt = now.ToBinary();
            h.LastOutcome = run.Branch;
            if (!_dirtyHistory.Contains(quest)) _dirtyHistory.Add(quest);

            RemoveRun(quest);
            ClearRewardsPaid(Info, quest);
            FlushBagTaken(); // other runs collecting the taken items re-read the bag

            for (var i = 0; i < items.Count; i++)
            {
                _player.GainItem(items[i]);
                _player.Report.ItemChanged(items[i], items[i].Count, 2);
            }

            GiveCurrencyRewards(filtered, QuestRewardScope.Quest, 0, first);
            _player.RecalculateQuestBag();

            Info.Statistics.Add(PlayerStat.QuestsCompleted, 1);
            QuestLog.Info("[Quests] turnin " + _player.Name + " quest " + quest + " step " + step + " after " + seconds + "s (times " + h.Times + ")");
            _player.CallDefaultNPC(DefaultNPCType.OnFinishQuest, quest);
        }

        /// <summary>Takes Collect items and carry items from the quest bag and Deliver items from the inventory (current step's objectives).</summary>
        private void TakeObjectiveItems(QuestRuntime q, QuestRunState run)
        {
            if (run.Step > 0)
            {
                var list = q.ObjectivesOf(run.Step);
                for (var i = 0; i < list.Count; i++)
                {
                    var o = list[i];
                    if (o.Type == QuestObjectiveType.Collect) TakeFrom(Info.QuestInventory, o, Math.Max(1, o.Count), true);
                    else if (o.Type == QuestObjectiveType.Deliver) TakeFrom(Info.Inventory, o, Math.Max(1, o.Count), false);
                }
            }

            var rewards = q.Def.Rewards;
            for (var i = 0; i < rewards.Count; i++)
            {
                var r = rewards[i];
                if (r.Scope != QuestRewardScope.Accept || r.Kind != QuestRewardKind.Item) continue;
                var info = Envir.GetItemInfo(r.TargetId);
                if (info != null) _player.TakeQuestItem(info, (ushort)Math.Clamp(r.Amount, 0, ushort.MaxValue));
            }
        }

        /// <summary>Takes the Deliver items of a step that just completed (before moving on), so a non-final step cannot keep them.</summary>
        private void TakeStepDeliveries(QuestRuntime q, int step)
        {
            var list = q.ObjectivesOf(step);
            for (var i = 0; i < list.Count; i++)
                if (list[i].Type == QuestObjectiveType.Deliver) TakeFrom(Info.Inventory, list[i], Math.Max(1, list[i].Count), false);
        }

        private void TakeFrom(UserItem[] grid, QuestObjectiveInfo o, int count, bool questBag)
        {
            for (var j = 0; j < grid.Length && count > 0; j++)
            {
                var item = grid[j];
                if (item?.Info == null || !TargetsItem(o, item.Info.Index)) continue;
                if (questBag) _bagTaken.Add(item.Info);
                var take = Math.Min(count, (int)item.Count);
                if (questBag) _player.Enqueue(new S.DeleteQuestItem { UniqueID = item.UniqueID, Count = (ushort)take });
                else _player.Enqueue(new S.DeleteItem { UniqueID = item.UniqueID, Count = (ushort)take });
                if (take == item.Count) grid[j] = null;
                else item.Count -= (ushort)take;
                count -= take;
            }

            if (!questBag) _player.RefreshBagWeight(); // inventory items carry weight only (UseItem does the same)
        }

        // Quest-bag items taken by TakeFrom: other runs collecting them re-read the bag afterwards.
        private readonly List<ItemInfo> _bagTaken = new List<ItemInfo>();

        private void FlushBagTaken()
        {
            for (var i = 0; i < _bagTaken.Count; i++) QuestBagChanged(_bagTaken[i]);
            _bagTaken.Clear();
        }

        // ------------------------------------------------------------------ rewards

        /// <summary>
        /// The rewards this player sees and can get (S.QuestDefinition.Rewards order): the quest's reward rows whose Condition holds
        /// (class / gender filters). Exp and gold amounts include the server rates.
        /// </summary>
        public List<QuestRewardInfo> FilteredRewards(QuestRuntime q)
        {
            var list = new List<QuestRewardInfo>();
            var rewards = q.Def.Rewards;
            for (var i = 0; i < rewards.Count; i++)
            {
                var r = rewards[i];
                if (r == null || (r.Condition != 0 && !EvaluateCondition(r.Condition))) continue;
                list.Add(r);
            }

            return list;
        }

        /// <summary>Exp × ExpRate and gold × DropRate (as legacy FinishQuest); every other amount as is.</summary>
        public static long RatedAmount(QuestRewardInfo r)
        {
            switch (r.Kind)
            {
                case QuestRewardKind.Exp: return (long)(r.Amount * Settings.ExpRate);
                case QuestRewardKind.Gold: return (long)(r.Amount * Settings.DropRate);
                default: return r.Amount;
            }
        }

        /// <summary>Fresh item stacks for the item rewards of one scope (fixed ones plus one pick per choice group).</summary>
        private List<UserItem> BuildRewardItems(QuestRuntime q, QuestRewardScope scope, int scopeId, List<int> picks, out List<QuestRewardInfo> filtered)
        {
            filtered = FilteredRewards(q);
            var first = !_history.ContainsKey(q.Index);
            var items = new List<UserItem>();
            for (var i = 0; i < filtered.Count; i++)
            {
                var r = filtered[i];
                if (r.Scope != scope || r.ScopeId != scopeId || r.Kind != QuestRewardKind.Item) continue;
                if (r.FirstTimeOnly && !first) continue;
                if (r.ChoiceGroup != 0 && !Picked(q, filtered, picks, i)) continue;
                var info = Envir.GetItemInfo(r.TargetId);
                if (info == null) continue;
                var count = r.Amount;
                while (count > 0)
                {
                    var item = Envir.CreateFreshItem(info);
                    var stack = Math.Max((ushort)1, info.StackSize);
                    item.Count = (ushort)Math.Min(stack, count);
                    count -= item.Count;
                    items.Add(item);
                }
            }

            return items;
        }

        /// <summary>
        /// True when <c>filtered[position]</c> (the rewards the player can get now) is the player's pick for its choice group. Picks are
        /// positions in the Rewards list of the S.QuestDefinition the client was sent (kept in <see cref="_sentRewards"/>), which can be
        /// older than the current filter (a reward condition on level changed since): a pick is resolved against that sent list, and a
        /// picked reward whose condition no longer holds is not in <paramref name="filtered"/>, so it gives nothing.
        /// </summary>
        private bool Picked(QuestRuntime q, List<QuestRewardInfo> filtered, List<int> picks, int position)
        {
            if (picks == null) return false;
            var basis = _sentRewards.TryGetValue(q.Index, out var sent) ? sent : filtered;
            var reward = filtered[position];
            for (var i = 0; i < picks.Count; i++)
            {
                var p = picks[i];
                if (p < 0 || p >= basis.Count || basis[p].ChoiceGroup != reward.ChoiceGroup) continue;
                return basis[p] == reward; // the first valid pick of the group wins
            }

            return false;
        }

        /// <summary>Per quest: the reward rows of the last S.QuestDefinition sent on this connection, in its order (game thread).</summary>
        private readonly Dictionary<int, List<QuestRewardInfo>> _sentRewards = new Dictionary<int, List<QuestRewardInfo>>();

        /// <summary>What a turn-in of <paramref name="q"/> would give (Scope = Quest), without giving it: item totals, rated exp / gold, credit.</summary>
        public sealed class RewardPreview
        {
            public readonly List<(int Item, long Count)> Items = new List<(int, long)>();
            public long Exp, Gold, Credit;
        }

        /// <summary>
        /// The pure reward selection of <see cref="TurnIn"/> (the differential test compares it with the legacy selection): the same
        /// filters (conditions, FirstTimeOnly, one pick per choice group) and rates as the real turn-in.
        /// </summary>
        public RewardPreview PreviewRewards(QuestRuntime q, List<int> picks)
        {
            var preview = new RewardPreview();
            var filtered = FilteredRewards(q);
            var first = !_history.ContainsKey(q.Index);
            for (var i = 0; i < filtered.Count; i++)
            {
                var r = filtered[i];
                if (r.Scope != QuestRewardScope.Quest || r.ScopeId != 0) continue;
                if (r.FirstTimeOnly && !first) continue;
                if (r.ChoiceGroup != 0 && !Picked(q, filtered, picks, i)) continue;
                var amount = RatedAmount(r);
                switch (r.Kind)
                {
                    case QuestRewardKind.Item: preview.Items.Add((r.TargetId, r.Amount)); break;
                    case QuestRewardKind.Exp: if (r.ChoiceGroup == 0) preview.Exp += amount; break;
                    case QuestRewardKind.Gold: if (r.ChoiceGroup == 0) preview.Gold += amount; break;
                    case QuestRewardKind.Credit: if (r.ChoiceGroup == 0) preview.Credit += amount; break;
                }
            }

            return preview;
        }

        /// <summary>Exp, gold and credit rewards of one scope (rates applied); kinds without a model yet are skipped.</summary>
        private void GiveCurrencyRewards(List<QuestRewardInfo> filtered, QuestRewardScope scope, int scopeId, bool first)
        {
            for (var i = 0; i < filtered.Count; i++)
            {
                var r = filtered[i];
                if (r.Scope != scope || r.ScopeId != scopeId || r.ChoiceGroup != 0) continue;
                if (r.FirstTimeOnly && !first) continue;
                var amount = RatedAmount(r);
                if (amount <= 0) continue;
                var value = (uint)Math.Min(amount, uint.MaxValue);
                switch (r.Kind)
                {
                    case QuestRewardKind.Exp: _player.GainExp(value); break;
                    case QuestRewardKind.Gold: _player.GainGold(value); break;
                    case QuestRewardKind.Credit: _player.GainCredit(value); break;
                }
            }
        }

        /// <summary>Step / objective / outcome rewards (items into the inventory when there is room, else mailed; then currencies).</summary>
        private void GiveScopedRewards(QuestRuntime q, QuestRewardScope scope, int scopeId)
        {
            // Paid once per cycle (saved), so abandon + retake cannot farm Step / Outcome rewards; turn-in starts a new cycle.
            if (RewardPaid(q.Index, scope, scopeId)) return;
            Info.QuestRewardsPaid.Add(new QuestRewardPaid { Quest = q.Index, Scope = scope, ScopeId = scopeId });

            var items = BuildRewardItems(q, scope, scopeId, null, out var filtered);
            for (var i = 0; i < items.Count; i++)
            {
                if (_player.CanGainItem(items[i])) _player.GainItem(items[i]);
                else _player.GainItemMail(items[i], 1);
            }

            GiveCurrencyRewards(filtered, scope, scopeId, !_history.ContainsKey(q.Index));
        }

        private bool RewardPaid(int quest, QuestRewardScope scope, int scopeId)
        {
            var paid = Info.QuestRewardsPaid;
            for (var i = 0; i < paid.Count; i++)
                if (paid[i].Quest == quest && paid[i].Scope == scope && paid[i].ScopeId == scopeId) return true;
            return false;
        }

        /// <summary>Forgets a quest's paid Step / Outcome rewards (turn-in, admin complete / reset); also for offline records.</summary>
        public static void ClearRewardsPaid(CharacterInfo info, int quest) => info.QuestRewardsPaid.RemoveAll(p => p == null || p.Quest == quest);

        // ------------------------------------------------------------------ abandon / share / choose

        /// <summary>C.QuestAbandon (also clears a failed run). Story quests may forbid it (AbandonAllowed).</summary>
        public void Abandon(int quest)
        {
            if (!_runs.TryGetValue(quest, out var run)) { Refuse(quest, QuestRequestKind.Abandon, QuestRefusal.NotAvailable); return; }
            var q = Catalog.Get(quest);
            if (q != null && !q.Def.AbandonAllowed && run.Status != QuestRunStatus.Failed) { Refuse(quest, QuestRequestKind.Abandon, QuestRefusal.AbandonNotAllowed); return; }
            QuestLog.Info("[Quests] abandon " + _player.Name + " quest " + quest + " step " + run.Step + " after " + SecondsSinceAccept(run) + "s");
            RemoveRun(quest);
            _player.RecalculateQuestBag();
        }

        /// <summary>
        /// C.QuestShare: offers the quest to party members in range who could take it (ShareMode Offer or AutoParty; None is refused
        /// with ShareNotAllowed). Each member who gets the offer sees its S.QuestOffer prompt; the sharer hears only of a refusal.
        /// </summary>
        public void Share(int quest)
        {
            const QuestRequestKind kind = QuestRequestKind.Share;
            if (!_runs.TryGetValue(quest, out var run) || run.Status != QuestRunStatus.Active) { Refuse(quest, kind, QuestRefusal.NotAvailable); return; }
            var q = Catalog.Get(quest);
            if (q == null) { Refuse(quest, kind, QuestRefusal.UnknownQuest); return; }
            if (q.Def.ShareMode == QuestShareMode.None) { Refuse(quest, kind, QuestRefusal.ShareNotAllowed); return; }
            var members = _player.GroupMembers;
            if (members == null || members.Count < 2) { Refuse(quest, kind, QuestRefusal.NotInGroup); return; }

            var shared = false;
            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                if (member == null || member == _player || member.Dead || member.Node == null || member.CurrentMap != _player.CurrentMap) continue;
                if (!Functions.InRange(member.CurrentLocation, _player.CurrentLocation, Globals.DataRange)) continue;
                if (!member.Quests.ReceiveOffer(q, Info.Index, _player.Name)) continue;
                shared = true;
            }

            if (!shared) Refuse(quest, kind, QuestRefusal.NoMemberCouldTake);
        }

        /// <summary>A share (or another non-NPC offer) reaches this player: remembered, definition sent, S.QuestOffer prompt.</summary>
        public bool ReceiveOffer(QuestRuntime q, int sharedFrom, string sharedBy, QuestEndpointKind kind = QuestEndpointKind.Npc)
        {
            if (!_loggedIn || !CanTake(q)) return false;
            _offers.Add(q.Index);
            if (sharedFrom != 0) _offerSharedFrom[q.Index] = sharedFrom;
            EnsureDefinition(q);
            _player.Enqueue(new S.QuestOffer { Quest = q.Index, Kind = kind, SharedBy = sharedBy ?? string.Empty });
            return true;
        }

        /// <summary>C.QuestChoose: a branch of the current step, chosen at an NPC that is an endpoint of the quest.</summary>
        public void Choose(int quest, uint npcObjectId, string choiceKey)
        {
            const QuestRequestKind kind = QuestRequestKind.Choose;
            if (string.IsNullOrEmpty(choiceKey)) { Refuse(quest, kind, QuestRefusal.InvalidPick); return; }
            if (!_runs.TryGetValue(quest, out var run) || run.Status != QuestRunStatus.Active || run.Step < 0) { Refuse(quest, kind, QuestRefusal.NotAvailable); return; }
            var q = Catalog.Get(quest);
            if (q == null) { Refuse(quest, kind, QuestRefusal.UnknownQuest); return; }
            if (!q.StepsById.TryGetValue(run.Step, out var step)) { Refuse(quest, kind, QuestRefusal.Other); return; }
            if (!CanChooseAt(q, NpcInReach(npcObjectId))) { Refuse(quest, kind, QuestRefusal.NotAtNpc); return; }
            if (!StepMet(q, run, step)) { Refuse(quest, kind, QuestRefusal.NotReady); return; }

            var branches = q.BranchesOf(step.Index);
            for (var i = 0; i < branches.Count; i++)
            {
                var b = branches[i];
                if (!string.Equals(b.ChoiceKey, choiceKey, StringComparison.Ordinal)) continue;
                if (b.Condition != 0 && !EvaluateCondition(b.Condition)) { Refuse(quest, kind, QuestRefusal.ConditionNotMet); return; }

                TakeStepDeliveries(q, step.Index);
                GiveScopedRewards(q, QuestRewardScope.Step, step.Index);
                if (b.Outcome != 0)
                {
                    run.Branch = b.Outcome;
                    GiveScopedRewards(q, QuestRewardScope.Outcome, b.Outcome);
                }

                QuestLog.Info("[Quests] choose " + _player.Name + " quest " + quest + " step " + step.Index + " branch " + choiceKey + " -> " + b.NextStep);
                var next = b.NextStep > 0 && q.StepsById.ContainsKey(b.NextStep) ? b.NextStep : -1;
                EnterStep(q, run, next);
                RunPage(step.OnCompletePage);
                Reevaluate(run);
                return;
            }

            Refuse(quest, kind, QuestRefusal.InvalidPick);
        }

        /// <summary>A branch of <paramref name="q"/> can be chosen at <paramref name="npc"/>: any Start or Finish endpoint of the quest.</summary>
        private bool CanChooseAt(QuestRuntime q, NPCObject npc) =>
            npc != null && (Catalog.IsEndpoint(q, QuestEndpointRole.Start, npc.Info.Index) || Catalog.IsEndpoint(q, QuestEndpointRole.Finish, npc.Info.Index));

        /// <summary>S.QuestRefused: a quest request from this client was refused (the client shows why and unlocks its buttons).</summary>
        public void Refuse(int quest, QuestRequestKind request, QuestRefusal reason)
        {
            _player.Enqueue(new S.QuestRefused { Quest = quest, Request = request, Reason = reason });
        }
    }
}

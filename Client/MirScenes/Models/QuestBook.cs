using Client.MirNetwork;
using Client.MirObjects;
using S = ServerPackets;
using C = ClientPackets;

namespace Client.MirScenes.Models
{
    /// <summary>One quest run as the server reported it (S.QuestStates): Active, Ready (derived by the server) or Failed.</summary>
    public sealed class QuestRun
    {
        public int Quest;
        public QuestRunStatus Status;
        /// <summary>The current step id, or -1 = every step done (turn-in pending).</summary>
        public int Step;
        /// <summary>DateTime.ToBinary (UTC), 0 = no limit.</summary>
        public long ExpiresAt, StepExpiresAt;
    }

    /// <summary>
    /// The quest V2 model (Knowledgebase/Future Ideas/UserData-and-Quests-Plan.md, Part B "Clients"): the definitions the server
    /// sent (S.QuestDefinition, cached for the session), the runs (S.QuestStates), objective values (S.QuestProgress), the
    /// completed list (S.QuestHistory) and, per NPC in view, the server-picked icon and the quests it offers / finishes
    /// (S.QuestAvailability; cleared on a map change, the server re-sends). Fed by GameScene.QuestsV2.cs; both UIs read it. No
    /// client-side copy of the accept rules: what an NPC offers is what the server says. UI thread only.
    /// </summary>
    public sealed class QuestBook
    {
        public readonly Dictionary<int, S.QuestDefinition> Definitions = new Dictionary<int, S.QuestDefinition>();
        /// <summary>Runs in the order they arrived (login order, then accept order).</summary>
        public readonly List<QuestRun> Runs = new List<QuestRun>();
        public readonly List<S.QuestHistory.Entry> History = new List<S.QuestHistory.Entry>();
        public readonly Dictionary<uint, S.QuestAvailability.Entry> Npcs = new Dictionary<uint, S.QuestAvailability.Entry>();

        private readonly Dictionary<int, QuestRun> _runs = new Dictionary<int, QuestRun>();
        private readonly Dictionary<long, int> _values = new Dictionary<long, int>();
        private readonly Dictionary<int, S.QuestHistory.Entry> _history = new Dictionary<int, S.QuestHistory.Entry>();
        private readonly HashSet<int> _requested = new HashSet<int>();

        /// <summary>Runs added by the last ApplyStates that was not a full list (quest, TrackHint); GameScene hands them to the UI.</summary>
        public readonly List<(int Quest, bool Track)> LastAccepted = new List<(int, bool)>();

        /// <summary>Bumped on every change (cheap "did anything change" check for windows).</summary>
        public int Version { get; private set; }

        /// <summary>Anything changed (runs, values, definitions, history, availability).</summary>
        public readonly Emitter Changed = new Emitter();
        public readonly Emitter AvailabilityChanged = new Emitter();
        /// <summary>An objective value changed after login (Quest, Objective, Old, Value): the progress toast.</summary>
        public readonly Emitter<(int Quest, int Objective, int Old, int Value)> Progressed = new Emitter<(int, int, int, int)>();
        /// <summary>A run became Ready (not at login).</summary>
        public readonly Emitter<int> Readied = new Emitter<int>();
        /// <summary>A run failed (not at login).</summary>
        public readonly Emitter<int> Failed = new Emitter<int>();
        /// <summary>A history row was added or its count went up (not at login): the quest was turned in.</summary>
        public readonly Emitter<int> TurnedIn = new Emitter<int>();
        /// <summary>The server refused a quest request (S.QuestRefused): the UI unlocks the quest's buttons and shows why.</summary>
        public readonly Emitter<S.QuestRefused> Refused = new Emitter<S.QuestRefused>();

        // ------------------------------------------------------------------ packets (GameScene.QuestsV2.cs)

        public void ApplyDefinition(S.QuestDefinition p)
        {
            if (p == null) return;
            Definitions[p.Quest] = p;
            _requested.Remove(p.Quest);
            Touch();
        }

        public void ApplyStates(S.QuestStates p)
        {
            LastAccepted.Clear();
            if (p == null) return;
            if (p.Full)
            {
                _runs.Clear();
                Runs.Clear();
                Settled = false;
            }
            for (int i = 0; i < p.States.Count; i++)
            {
                S.QuestStates.Entry e = p.States[i];
                if (e.Status == QuestRunStatus.None || e.Status == QuestRunStatus.Completed)
                {
                    if (_runs.Remove(e.Quest)) RemoveRun(e.Quest);
                    continue;
                }
                if (_runs.TryGetValue(e.Quest, out QuestRun run))
                {
                    QuestRunStatus old = run.Status;
                    run.Status = e.Status;
                    run.Step = e.Step;
                    run.ExpiresAt = e.ExpiresAt;
                    run.StepExpiresAt = e.StepExpiresAt;
                    if (!p.Full && old != QuestRunStatus.Ready && e.Status == QuestRunStatus.Ready) Readied.Emit(e.Quest);
                    if (!p.Full && old != QuestRunStatus.Failed && e.Status == QuestRunStatus.Failed) Failed.Emit(e.Quest);
                    continue;
                }
                run = new QuestRun { Quest = e.Quest, Status = e.Status, Step = e.Step, ExpiresAt = e.ExpiresAt, StepExpiresAt = e.StepExpiresAt };
                _runs[e.Quest] = run;
                Runs.Add(run);
                if (!p.Full) LastAccepted.Add((e.Quest, e.TrackHint));
            }
            Touch();
        }

        private void RemoveRun(int quest)
        {
            for (int i = Runs.Count - 1; i >= 0; i--)
                if (Runs[i].Quest == quest) Runs.RemoveAt(i);
            // its objective values go with it (a retake starts from the server's new values)
            if (Definitions.TryGetValue(quest, out S.QuestDefinition def))
                for (int i = 0; i < def.Objectives.Count; i++) _values.Remove(Key(quest, def.Objectives[i].Index));
        }

        public void ApplyProgress(S.QuestProgress p)
        {
            if (p == null) return;
            bool any = false;
            for (int i = 0; i < p.Progress.Count; i++)
            {
                S.QuestProgress.Entry e = p.Progress[i];
                long key = Key(e.Quest, e.Objective);
                int old = _values.TryGetValue(key, out int v) ? v : 0;
                bool known = _values.ContainsKey(key);
                _values[key] = e.Value;
                if (known && old == e.Value) continue;
                any = true;
                // the login picture (states, progress, then history) is not "progress"
                if (Settled && e.Value != old) Progressed.Emit((e.Quest, e.Objective, old, e.Value));
            }
            if (any) Touch();
        }

        public void ApplyHistory(S.QuestHistory p)
        {
            if (p == null) return;
            if (p.Full)
            {
                _history.Clear();
                History.Clear();
            }
            for (int i = 0; i < p.History.Count; i++)
            {
                S.QuestHistory.Entry e = p.History[i];
                if (_history.TryGetValue(e.Quest, out S.QuestHistory.Entry old))
                {
                    int at = History.IndexOf(old);
                    if (at >= 0) History[at] = e;
                    else History.Add(e);
                    _history[e.Quest] = e;
                    if (!p.Full && e.Times > old.Times) TurnedIn.Emit(e.Quest);
                    continue;
                }
                _history[e.Quest] = e;
                History.Add(e);
                if (!p.Full) TurnedIn.Emit(e.Quest);
            }
            HistoryKnown = true;
            if (p.Full) Settled = true; // the last packet of the login picture
            Touch();
        }

        public void ApplyRefused(S.QuestRefused p)
        {
            if (p == null) return;
            Refused.Emit(p);
            Touch();
        }

        public void ApplyAvailability(S.QuestAvailability p)
        {
            if (p == null) return;
            for (int i = 0; i < p.Npcs.Count; i++)
            {
                S.QuestAvailability.Entry e = p.Npcs[i];
                if (e.Icon == QuestIcon.None && e.Offered.Count == 0 && e.Finishing.Count == 0 && e.Choosing.Count == 0) Npcs.Remove(e.ObjectID);
                else Npcs[e.ObjectID] = e;
            }
            AvailabilityChanged.Emit();
            Touch();
        }

        /// <summary>The map changed: the client dropped every object and the server re-sends the entries of the new view.</summary>
        public void ClearNpcs()
        {
            if (Npcs.Count == 0) return;
            Npcs.Clear();
            AvailabilityChanged.Emit();
            Touch();
        }

        private void Touch()
        {
            Version++;
            Changed.Emit();
        }

        private static long Key(int quest, int objective) => ((long)quest << 32) | (uint)objective;

        // ------------------------------------------------------------------ queries

        /// <summary>True once the login picture arrived (S.QuestStates Full, S.QuestProgress, S.QuestHistory Full, in that order):
        /// value changes after it are progress (toasts).</summary>
        public bool Settled { get; private set; }
        public bool HistoryKnown { get; private set; }

        public QuestRun Run(int quest) => _runs.TryGetValue(quest, out QuestRun r) ? r : null;

        public S.QuestDefinition Def(int quest) => Definitions.TryGetValue(quest, out S.QuestDefinition d) ? d : null;

        public S.QuestHistory.Entry Done(int quest) => _history.TryGetValue(quest, out S.QuestHistory.Entry h) ? h : null;

        public int Value(int quest, int objective) => _values.TryGetValue(Key(quest, objective), out int v) ? v : 0;

        /// <summary>Runs that count against Globals.MaxConcurrentQuests (Active and Ready).</summary>
        public int ActiveCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Runs.Count; i++) if (Runs[i].Status == QuestRunStatus.Active || Runs[i].Status == QuestRunStatus.Ready) n++;
                return n;
            }
        }

        /// <summary>Asks the server for a quest's definition once (C.QuestDefinitionRequest; a history quest opened in the log).</summary>
        public void RequestDefinition(int quest)
        {
            if (quest <= 0 || Definitions.ContainsKey(quest) || !_requested.Add(quest)) return;
            Network.Enqueue(new C.QuestDefinitionRequest { Quest = quest });
        }

        public static S.QuestDefinition.Step StepOf(S.QuestDefinition def, int step)
        {
            if (def == null) return null;
            for (int i = 0; i < def.Steps.Count; i++) if (def.Steps[i].Index == step) return def.Steps[i];
            return null;
        }

        /// <summary>The step's objectives in definition order, into <paramref name="into"/> (cleared first).</summary>
        public static void ObjectivesOf(S.QuestDefinition def, int step, List<S.QuestDefinition.Objective> into)
        {
            into.Clear();
            if (def == null) return;
            for (int i = 0; i < def.Objectives.Count; i++) if (def.Objectives[i].Step == step) into.Add(def.Objectives[i]);
        }

        /// <summary>Hidden objectives show once they have progress.</summary>
        public bool Shown(int quest, S.QuestDefinition.Objective o) => !o.Hidden || Value(quest, o.Index) > 0;

        public bool ObjectiveDone(int quest, S.QuestDefinition.Objective o) => Value(quest, o.Index) >= Math.Max(1, o.Count);

        /// <summary>The step's required objectives are met (All / Any / N of), as the server's StepMet.</summary>
        public bool StepMet(S.QuestDefinition def, int quest, int step)
        {
            S.QuestDefinition.Step s = StepOf(def, step);
            if (s == null) return false;
            int required = 0, done = 0;
            for (int i = 0; i < def.Objectives.Count; i++)
            {
                S.QuestDefinition.Objective o = def.Objectives[i];
                if (o.Step != step || o.Optional) continue;
                required++;
                if (ObjectiveDone(quest, o)) done++;
            }
            switch (s.CompletionMode)
            {
                case QuestStepCompletion.Any: return required == 0 || done > 0;
                case QuestStepCompletion.NOf: return done >= Math.Min(required, Math.Max(1, s.NRequired));
                default: return done >= required;
            }
        }

        /// <summary>The branches offered at the end of a step (empty list when none).</summary>
        public static void BranchesOf(S.QuestDefinition def, int step, List<S.QuestDefinition.Branch> into)
        {
            into.Clear();
            if (def == null) return;
            for (int i = 0; i < def.Branches.Count; i++) if (def.Branches[i].Step == step) into.Add(def.Branches[i]);
        }

        /// <summary>A branch choice is due: an Active run whose current step is met and has branches.</summary>
        public bool ChoiceDue(int quest)
        {
            QuestRun run = Run(quest);
            S.QuestDefinition def = Def(quest);
            if (run == null || def == null || run.Status != QuestRunStatus.Active || run.Step < 0) return false;
            bool has = false;
            for (int i = 0; i < def.Branches.Count; i++) if (def.Branches[i].Step == run.Step) { has = true; break; }
            return has && StepMet(def, quest, run.Step);
        }

        /// <summary>The NPC (ObjectID) that offers this quest, preferring one on screen; 0 = none known.</summary>
        public uint NpcOffering(int quest) => FindNpc(quest, 0);

        /// <summary>The NPC (ObjectID) where this run is turned in, preferring one on screen; 0 = none known.</summary>
        public uint NpcFinishing(int quest) => FindNpc(quest, 1);

        /// <summary>The NPC (ObjectID) where this run's branch can be chosen (its Choosing list, else a finishing NPC); 0 = none known.</summary>
        public uint NpcChoosing(int quest)
        {
            uint npc = FindNpc(quest, 2);
            return npc != 0 ? npc : FindNpc(quest, 1);
        }

        private uint FindNpc(int quest, int which)
        {
            uint any = 0;
            foreach (KeyValuePair<uint, S.QuestAvailability.Entry> kv in Npcs)
            {
                List<int> list = which == 0 ? kv.Value.Offered : which == 1 ? kv.Value.Finishing : kv.Value.Choosing;
                if (!list.Contains(quest)) continue;
                if (MapControl.Objects != null && MapControl.Objects.ContainsKey(kv.Key)) return kv.Key;
                if (any == 0) any = kv.Key;
            }
            return any;
        }

        public QuestIcon IconFor(uint npc) => Npcs.TryGetValue(npc, out S.QuestAvailability.Entry e) ? e.Icon : QuestIcon.None;

        /// <summary>True when the NPC offers, finishes or takes a branch choice of anything for this player (the classic NPC dialog's quest button).</summary>
        public bool NpcHasQuests(uint npc) => Npcs.TryGetValue(npc, out S.QuestAvailability.Entry e) && (e.Offered.Count > 0 || e.Finishing.Count > 0 || e.Choosing.Count > 0);

        /// <summary>Every quest offered by the NPCs known on this map, without repeats and without quests already taken.</summary>
        public void Offered(List<int> into)
        {
            into.Clear();
            foreach (KeyValuePair<uint, S.QuestAvailability.Entry> kv in Npcs)
                for (int i = 0; i < kv.Value.Offered.Count; i++)
                {
                    int q = kv.Value.Offered[i];
                    if (!into.Contains(q) && !_runs.ContainsKey(q)) into.Add(q);
                }
        }
    }

    /// <summary>
    /// Client-built quest text (Part B "Clients": objective lines from Type + target + Count + value via ClientTextKeys unless the
    /// objective carries its own Text), shared by both UIs. Names come from the info lists the server sent with the definition.
    /// </summary>
    public static class QuestText
    {
        private static string T(ClientTextKeys k) => GameLanguage.ClientTextMap.GetLocalization(k);
        private static string T(ClientTextKeys k, params object[] a) => GameLanguage.ClientTextMap.GetLocalization(k, a);

        /// <summary>Types whose value is a count worth showing as n/m (the rest are done / not done).</summary>
        public static bool Countable(S.QuestDefinition.Objective o)
        {
            switch (o.Type)
            {
                case QuestObjectiveType.Talk:
                case QuestObjectiveType.ReachArea:
                case QuestObjectiveType.ReachLevel:
                case QuestObjectiveType.Flag:
                    return false;
                default:
                    return o.Count > 1;
            }
        }

        /// <summary>The objective without its count ("Kill Hen"), or the objective's own Text.</summary>
        public static string Label(S.QuestDefinition.Objective o)
        {
            if (o == null) return string.Empty;
            string text;
            if (!string.IsNullOrEmpty(o.Text)) text = o.Text;
            else
            {
                string name = TargetName(o);
                switch (o.Type)
                {
                    case QuestObjectiveType.Kill: text = T(ClientTextKeys.QuestObjKill, name); break;
                    case QuestObjectiveType.Collect: text = T(ClientTextKeys.QuestObjCollect, name); break;
                    case QuestObjectiveType.Deliver: text = T(ClientTextKeys.QuestObjDeliver, name); break;
                    case QuestObjectiveType.Talk: text = T(ClientTextKeys.QuestObjTalk, name); break;
                    case QuestObjectiveType.ReachArea:
                        text = o.X > 0 || o.Y > 0 ? T(ClientTextKeys.QuestObjReachAreaAt, name, o.X, o.Y) : T(ClientTextKeys.QuestObjReachArea, name);
                        break;
                    case QuestObjectiveType.UseItem: text = T(ClientTextKeys.QuestObjUseItem, name); break;
                    case QuestObjectiveType.Craft: text = T(ClientTextKeys.QuestObjCraft, name); break;
                    case QuestObjectiveType.Gather: text = T(ClientTextKeys.QuestObjGather, name); break;
                    case QuestObjectiveType.ReachLevel: text = T(ClientTextKeys.QuestObjReachLevel, Math.Max(1, o.Count)); break;
                    case QuestObjectiveType.UseSkill: text = T(ClientTextKeys.QuestObjUseSkill, name); break;
                    default: text = T(ClientTextKeys.QuestObjTask); break;
                }
            }
            return o.Optional ? T(ClientTextKeys.QuestObjOptional, text) : text;
        }

        /// <summary>The full line with its count when it has one ("Kill Hen: 3/10"), for the classic lists.</summary>
        public static string Line(S.QuestDefinition.Objective o, int value)
        {
            string label = Label(o);
            if (!Countable(o)) return value >= Math.Max(1, o.Count) ? T(ClientTextKeys.QuestObjDone, label) : label;
            return T(ClientTextKeys.QuestObjProgress, label, Math.Min(value, o.Count), o.Count);
        }

        /// <summary>
        /// The objective's target: for a group with several members their names ("Wolf, WolfKing", at most three, then "…") once
        /// their infos are known, else the server's label (groups, flags, maps), else the item / monster / NPC / skill name.
        /// </summary>
        public static string TargetName(S.QuestDefinition.Objective o)
        {
            if (o.TargetMembers.Count > 1 && (o.TargetKind == QuestTargetKind.MonsterGroup || o.TargetKind == QuestTargetKind.ItemGroup))
            {
                string members = MemberNames(o);
                if (members != null) return members;
            }
            if (!string.IsNullOrEmpty(o.TargetName)) return o.TargetName;
            switch (o.TargetKind)
            {
                case QuestTargetKind.Monster:
                    for (int i = 0; i < GameScene.MonsterInfoList.Count; i++)
                        if (GameScene.MonsterInfoList[i].Index == o.TargetId)
                        {
                            ClientMonsterInfo m = GameScene.MonsterInfoList[i];
                            return !string.IsNullOrEmpty(m.GameName) ? m.GameName : m.Name;
                        }
                    GameScene.RequestMonsterInfo(o.TargetId);
                    break;
                case QuestTargetKind.Item:
                    for (int i = 0; i < GameScene.ItemInfoList.Count; i++)
                        if (GameScene.ItemInfoList[i].Index == o.TargetId) return GameScene.ItemInfoList[i].FriendlyName;
                    GameScene.RequestItemInfo(o.TargetId);
                    break;
                case QuestTargetKind.Npc:
                    for (int i = 0; i < GameScene.NPCInfoList.Count; i++)
                        if (GameScene.NPCInfoList[i].Index == o.TargetId) return NpcName(GameScene.NPCInfoList[i].Name);
                    GameScene.RequestNPCInfo(o.TargetId);
                    break;
                case QuestTargetKind.Spell:
                    UserObject user = GameScene.User;
                    if (user != null)
                        for (int i = 0; i < user.Magics.Count; i++)
                            if ((int)user.Magics[i].Spell == o.TargetId) return user.Magics[i].Name;
                    if (Enum.IsDefined(typeof(Spell), (byte)Math.Clamp(o.TargetId, 0, 255))) return ((Spell)o.TargetId).ToString();
                    break;
            }
            return T(ClientTextKeys.QuestTargetUnknown);
        }

        /// <summary>The group members' names (distinct, at most three, then "…"), or null while an info is missing.</summary>
        private static string MemberNames(S.QuestDefinition.Objective o)
        {
            const int Shown = 3;
            var names = new List<string>(Shown);
            bool item = o.TargetKind == QuestTargetKind.ItemGroup;
            for (int i = 0; i < o.TargetMembers.Count; i++)
            {
                int id = o.TargetMembers[i];
                string name = null;
                if (item)
                {
                    for (int j = 0; j < GameScene.ItemInfoList.Count; j++)
                        if (GameScene.ItemInfoList[j].Index == id) { name = GameScene.ItemInfoList[j].FriendlyName; break; }
                }
                else
                {
                    for (int j = 0; j < GameScene.MonsterInfoList.Count; j++)
                        if (GameScene.MonsterInfoList[j].Index == id)
                        {
                            ClientMonsterInfo m = GameScene.MonsterInfoList[j];
                            name = !string.IsNullOrEmpty(m.GameName) ? m.GameName : m.Name;
                            break;
                        }
                }
                if (string.IsNullOrEmpty(name)) return null; // the server sends every member's info with the definition
                if (names.Contains(name)) continue;
                if (names.Count == Shown) return T(ClientTextKeys.QuestTargetMore, string.Join(", ", names));
                names.Add(name);
            }
            return string.Join(", ", names);
        }

        /// <summary>A refusal as one line: "Cannot hand in "Hen Trouble": The quest is not ready to hand in."</summary>
        public static string Refusal(S.QuestRefused p, out string title, out string reason)
        {
            string name = GameScene.Models?.Quests.Def(p.Quest)?.Name;
            if (string.IsNullOrEmpty(name)) name = GameScene.Models?.Quests.Done(p.Quest)?.Name ?? string.Empty;
            title = T(p.Request switch
            {
                QuestRequestKind.TurnIn => ClientTextKeys.QuestRefusedTurnIn,
                QuestRequestKind.Abandon => ClientTextKeys.QuestRefusedAbandon,
                QuestRequestKind.Share => ClientTextKeys.QuestRefusedShare,
                QuestRequestKind.Choose => ClientTextKeys.QuestRefusedChoose,
                _ => ClientTextKeys.QuestRefusedAccept,
            }, name);
            reason = T(p.Reason switch
            {
                QuestRefusal.NotAvailable => ClientTextKeys.QuestRefusalNotAvailable,
                QuestRefusal.NotAtNpc => ClientTextKeys.QuestRefusalNotAtNpc,
                QuestRefusal.QuestLogFull => ClientTextKeys.QuestRefusalQuestLogFull,
                QuestRefusal.BagFull => ClientTextKeys.QuestRefusalBagFull,
                QuestRefusal.NotReady => ClientTextKeys.QuestRefusalNotReady,
                QuestRefusal.InvalidPick => ClientTextKeys.QuestRefusalInvalidPick,
                QuestRefusal.AbandonNotAllowed => ClientTextKeys.QuestRefusalAbandonNotAllowed,
                QuestRefusal.NotInGroup => ClientTextKeys.QuestRefusalNotInGroup,
                QuestRefusal.ShareNotAllowed => ClientTextKeys.QuestRefusalShareNotAllowed,
                QuestRefusal.UnknownQuest => ClientTextKeys.QuestRefusalUnknownQuest,
                QuestRefusal.ConditionNotMet => ClientTextKeys.QuestRefusalConditionNotMet,
                QuestRefusal.AlreadyActive => ClientTextKeys.QuestRefusalAlreadyActive,
                QuestRefusal.Dead => ClientTextKeys.QuestRefusalDead,
                QuestRefusal.AlreadyCompleted => ClientTextKeys.QuestRefusalAlreadyCompleted,
                QuestRefusal.QuestBagFull => ClientTextKeys.QuestRefusalQuestBagFull,
                QuestRefusal.NoMemberCouldTake => ClientTextKeys.QuestRefusalNoMemberCouldTake,
                _ => ClientTextKeys.QuestRefusalOther,
            });
            return T(ClientTextKeys.QuestRefusedLine, title, reason);
        }

        /// <summary>The quest can be shared at all (its ShareMode; the server refuses C.QuestShare when None).</summary>
        public static bool Shareable(S.QuestDefinition def) => def != null && def.ShareMode != QuestShareMode.None;

        /// <summary>How the UI shows an NPC's raw name; set by the UI at build (the new UI: the last '_' part, ChatGfx.NpcName;
        /// null = the classic NPC dialog's rule, the part before the first '_').</summary>
        public static Func<string, string> NpcNameFormat;

        /// <summary>An NPC's display name in the current UI's style.</summary>
        public static string NpcName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            if (NpcNameFormat != null) return NpcNameFormat(raw);
            int cut = raw.IndexOf('_');
            return cut > 0 ? raw.Substring(0, cut) : raw;
        }

        /// <summary>The NPC endpoint of a role (Finish falls back to Start: a quest without a Finish endpoint is turned in at its giver).</summary>
        public static S.QuestDefinition.Endpoint Endpoint(S.QuestDefinition def, QuestEndpointRole role)
        {
            if (def == null) return null;
            for (int i = 0; i < def.Endpoints.Count; i++)
                if (def.Endpoints[i].Role == role && def.Endpoints[i].Kind == QuestEndpointKind.Npc) return def.Endpoints[i];
            if (role == QuestEndpointRole.Finish)
            {
                for (int i = 0; i < def.Endpoints.Count; i++)
                    if (def.Endpoints[i].Role == QuestEndpointRole.Finish) return null; // a non-NPC finish (auto / item)
                return Endpoint(def, QuestEndpointRole.Start);
            }
            return null;
        }

        /// <summary>The endpoint NPC's name, or "" when the quest has no NPC endpoint for the role.</summary>
        public static string EndpointName(S.QuestDefinition def, QuestEndpointRole role)
        {
            S.QuestDefinition.Endpoint e = Endpoint(def, role);
            return e == null ? string.Empty : NpcName(e.Name);
        }

        /// <summary>Seconds until a DateTime.ToBinary (UTC) time, 0 when past, -1 when there is none.</summary>
        public static long SecondsLeft(long binary)
        {
            if (binary == 0) return -1;
            DateTime at;
            try { at = DateTime.FromBinary(binary); }
            catch (ArgumentException) { return -1; }
            double s = (at - CMain.Now).TotalSeconds;
            return s <= 0 ? 0 : (long)Math.Ceiling(s);
        }

        /// <summary>Quest-scope rewards that are given for this player (FirstTimeOnly ones only before the first completion).</summary>
        public static bool RewardShown(S.QuestDefinition.Reward r, bool completedBefore) =>
            r.Scope == QuestRewardScope.Quest && (!r.FirstTimeOnly || !completedBefore)
            && (r.Kind == QuestRewardKind.Exp || r.Kind == QuestRewardKind.Gold || r.Kind == QuestRewardKind.Credit || r.Kind == QuestRewardKind.Item);

        public static ItemInfo ItemInfo(int index)
        {
            for (int i = 0; i < GameScene.ItemInfoList.Count; i++)
                if (GameScene.ItemInfoList[i].Index == index) return GameScene.ItemInfoList[i];
            GameScene.RequestItemInfo(index);
            return null;
        }
    }
}

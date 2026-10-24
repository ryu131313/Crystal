using System.Drawing;
using S = ServerPackets;

namespace StressClient;

/// <summary>One quest run as the server reported it (S.QuestStates / S.QuestProgress).</summary>
internal sealed class QuestRun
{
    public int Quest;
    public QuestRunStatus Status;
    public int Step;
    public readonly Dictionary<int, int> Values = new();

    /// <summary>When the run was accepted (or first seen this session) and when it last moved (step or objective value).</summary>
    public long SeenAt, LastProgressAt;

    /// <summary>Set once C.QuestAbandon was sent; a run the server keeps (AbandonAllowed false) is not asked again.</summary>
    public bool AbandonSent;
}

/// <summary>Where a bot last saw an NPC that gives or finishes a quest (its availability entry named the quest).</summary>
internal struct QuestNpcSpot
{
    public uint ObjectId;
    public int MapIndex;
    public Point Location;
}

/// <summary>
/// One bot's V2 quest mirror: definitions (S.QuestDefinition, on demand), runs (S.QuestStates + S.QuestProgress), history
/// (S.QuestHistory), per-NPC availability (S.QuestAvailability) and pending offers (S.QuestOffer). Owned by the bot's worker
/// thread like the rest of <see cref="WorldModel"/>; the only value that leaves it is the immutable
/// <see cref="TargetMonsterIndexes"/> array handed to the Director.
/// </summary>
internal sealed class QuestBook
{
    /// <summary>Bots keep at most this many runs at once (the server's cap is Globals.MaxConcurrentQuests).</summary>
    public const int MaxActive = 3;

    public readonly Dictionary<int, S.QuestDefinition> Definitions = new();
    public readonly Dictionary<int, QuestRun> Runs = new();
    public readonly HashSet<int> History = new();
    public readonly Dictionary<uint, S.QuestAvailability.Entry> Npcs = new();
    public readonly Dictionary<int, QuestNpcSpot> FinishSpots = new();
    public readonly HashSet<int> Offers = new();

    /// <summary>Monster info index → in-game name (S.NewMonsterInfo GameName, which is what S.ObjectMonster.Name carries).</summary>
    public readonly Dictionary<int, string> MonsterNames = new();

    // Requests sent and not yet answered: quest → when sent. The answer (S.QuestStates) clears them and counts the metric.
    public readonly Dictionary<int, long> PendingAccepts = new(), PendingTurnIns = new(), PendingAbandons = new();

    private readonly HashSet<string> _targetNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _targetsDirty = true;
    private int[] _targetIndexes = Array.Empty<int>();

    public void Clear()
    {
        Definitions.Clear();
        Runs.Clear();
        History.Clear();
        Npcs.Clear();
        FinishSpots.Clear();
        Offers.Clear();
        MonsterNames.Clear();
        PendingAccepts.Clear();
        PendingTurnIns.Clear();
        PendingAbandons.Clear();
        _targetsDirty = true;
    }

    /// <summary>Forgets requests the server never answered (a refused accept or turn-in gets no reply) so they can be retried.</summary>
    public void ExpirePending(long now, long afterMs)
    {
        Expire(PendingAccepts, now, afterMs);
        Expire(PendingTurnIns, now, afterMs);
        Expire(PendingAbandons, now, afterMs);
    }

    private static void Expire(Dictionary<int, long> pending, long now, long afterMs)
    {
        if (pending.Count == 0) return;
        List<int> old = null;
        foreach (var pair in pending)
            if (now - pair.Value >= afterMs) (old ??= new List<int>()).Add(pair.Key);
        if (old == null) return;
        foreach (var quest in old) pending.Remove(quest);
    }

    // ------------------------------------------------------------------ packets

    public void ApplyDefinition(S.QuestDefinition def)
    {
        if (def == null) return;
        Definitions[def.Quest] = def;
        _targetsDirty = true;
    }

    /// <summary>
    /// Applies S.QuestStates. Returns the quests whose run ended (status None) so the caller can count turn-ins and abandons,
    /// and the quests that became active from an accept we sent.
    /// </summary>
    public void ApplyStates(S.QuestStates packet, long now, List<int> accepted, List<int> turnedIn, List<int> abandoned)
    {
        if (packet == null) return;
        if (packet.Full) Runs.Clear();

        foreach (var e in packet.States)
        {
            if (e.Status == QuestRunStatus.None || e.Status == QuestRunStatus.Completed)
            {
                Runs.Remove(e.Quest);
                if (PendingTurnIns.Remove(e.Quest)) turnedIn?.Add(e.Quest);
                else if (PendingAbandons.Remove(e.Quest)) abandoned?.Add(e.Quest);
                continue;
            }

            if (!Runs.TryGetValue(e.Quest, out var run))
            {
                run = new QuestRun { Quest = e.Quest, SeenAt = now, LastProgressAt = now };
                Runs[e.Quest] = run;
                if (PendingAccepts.Remove(e.Quest)) accepted?.Add(e.Quest);
            }

            if (run.Step != e.Step || run.Status != e.Status) run.LastProgressAt = now;
            run.Status = e.Status;
            run.Step = e.Step;
        }

        _targetsDirty = true;
    }

    public void ApplyProgress(S.QuestProgress packet, long now)
    {
        if (packet == null) return;
        foreach (var e in packet.Progress)
        {
            if (!Runs.TryGetValue(e.Quest, out var run)) continue;
            if (run.Values.TryGetValue(e.Objective, out var old) && old == e.Value) continue;
            run.Values[e.Objective] = e.Value;
            run.LastProgressAt = now;
            _targetsDirty = true;
        }
    }

    public void ApplyHistory(S.QuestHistory packet)
    {
        if (packet == null) return;
        if (packet.Full) History.Clear();
        foreach (var e in packet.History) History.Add(e.Quest);
    }

    /// <summary>S.QuestAvailability: one entry per NPC in view whose list changed (an upsert). Remembers where each finish NPC stood.</summary>
    public void ApplyAvailability(S.QuestAvailability packet, WorldModel world)
    {
        if (packet == null) return;
        foreach (var e in packet.Npcs)
        {
            Npcs[e.ObjectID] = e;
            if (world == null || !world.Npcs.TryGetValue(e.ObjectID, out var npc)) continue;
            var spot = new QuestNpcSpot { ObjectId = e.ObjectID, MapIndex = world.MapIndex, Location = npc.Location };
            foreach (var q in e.Finishing) FinishSpots[q] = spot;
            // A quest without a Finish endpoint is turned in at its Start NPC: remember the giver until a finisher shows up.
            foreach (var q in e.Offered) FinishSpots.TryAdd(q, spot);
        }
    }

    /// <summary>An NPC left view or the map changed: the server re-sends its entry when it is seen again.</summary>
    public void ForgetNpc(uint objectId) => Npcs.Remove(objectId);

    public void ForgetAllNpcs() => Npcs.Clear();

    public void NoteMonsterInfo(ClientMonsterInfo info)
    {
        if (info == null) return;
        MonsterNames[info.Index] = string.IsNullOrEmpty(info.GameName) ? info.Name ?? string.Empty : info.GameName;
        _targetsDirty = true;
    }

    // ------------------------------------------------------------------ decisions (thin wrappers over QuestLogic)

    /// <summary>True when <paramref name="monsterName"/> is the target of an unmet Kill objective of a current step.</summary>
    public bool IsTarget(string monsterName)
    {
        RefreshTargets();
        return _targetNames.Count > 0 && monsterName != null && _targetNames.Contains(monsterName);
    }

    public bool HasTargets
    {
        get { RefreshTargets(); return _targetNames.Count > 0; }
    }

    /// <summary>Monster info indexes the bot is hunting for quests (immutable snapshot; safe to hand to the Director thread).</summary>
    public int[] TargetMonsterIndexes
    {
        get { RefreshTargets(); return _targetIndexes; }
    }

    private void RefreshTargets()
    {
        if (!_targetsDirty) return;
        _targetsDirty = false;
        _targetNames.Clear();
        var indexes = new HashSet<int>();
        QuestLogic.KillTargets(Definitions, Runs.Values, MonsterNames, _targetNames, indexes);
        _targetIndexes = indexes.Count == 0 ? Array.Empty<int>() : indexes.ToArray();
    }

    /// <summary>The quest to accept from <paramref name="npcId"/>'s offers, or 0.</summary>
    public int ChooseOfferAt(uint npcId, int level)
    {
        if (!Npcs.TryGetValue(npcId, out var entry)) return 0;
        return QuestLogic.ChooseOffer(entry.Offered, Definitions, Runs, PendingAccepts, level, MaxActive);
    }

    /// <summary>A Ready run that <paramref name="npcId"/> finishes (its availability entry lists it), or 0.</summary>
    public int ReadyAt(uint npcId)
    {
        if (!Npcs.TryGetValue(npcId, out var entry)) return 0;
        foreach (var q in entry.Finishing)
            if (Runs.TryGetValue(q, out var run) && run.Status == QuestRunStatus.Ready && !PendingTurnIns.ContainsKey(q)) return q;
        return 0;
    }

    public bool AnyReady
    {
        get
        {
            foreach (var run in Runs.Values)
                if (run.Status == QuestRunStatus.Ready && !PendingTurnIns.ContainsKey(run.Quest)) return true;
            return false;
        }
    }

    /// <summary>True when an NPC in view offers a quest this bot would take.</summary>
    public bool AnyOfferInView(int level)
    {
        if (Runs.Count + PendingAccepts.Count >= MaxActive) return false;
        foreach (var entry in Npcs.Values)
            if (QuestLogic.ChooseOffer(entry.Offered, Definitions, Runs, PendingAccepts, level, MaxActive) != 0) return true;
        return false;
    }

    /// <summary>A pending S.QuestOffer (item / area / auto / share) this bot would take, or 0.</summary>
    public int ChooseNonNpcOffer(int level) =>
        QuestLogic.ChooseOffer(Offers, Definitions, Runs, PendingAccepts, level, MaxActive);
}

/// <summary>Pure quest decisions (no I/O, no bot state), so StressClient.Tests can pin them down.</summary>
internal static class QuestLogic
{
    /// <summary>
    /// Objectives a bot can make progress on: kills of a monster or a monster group (it hunts monsters by name), collecting
    /// quest drops (they come from hunting; the server counts the quest bag) and reaching a level (it comes with hunting).
    /// Everything else (talk, areas, use item, craft, gather, skills, counters, flags, reserved types) is skipped.
    /// </summary>
    public static bool CanDo(S.QuestDefinition.Objective o)
    {
        if (o == null) return false;
        switch (o.Type)
        {
            case QuestObjectiveType.Kill:
                return o.TargetKind is QuestTargetKind.Monster or QuestTargetKind.MonsterGroup;
            case QuestObjectiveType.Collect:
                return o.TargetKind is QuestTargetKind.Item or QuestTargetKind.ItemGroup;
            case QuestObjectiveType.ReachLevel:
                return true;
            default:
                return false;
        }
    }

    /// <summary>True when every step can be completed with objectives the bot can do (honouring All / Any / N-of steps).</summary>
    public static bool IsDoable(S.QuestDefinition def)
    {
        if (def == null || def.Steps.Count == 0) return false;
        foreach (var step in def.Steps)
        {
            int required = 0, requiredDoable = 0, doable = 0;
            foreach (var o in def.Objectives)
            {
                if (o.Step != step.Index) continue;
                bool can = CanDo(o);
                if (can) doable++;
                if (o.Optional) continue;
                required++;
                if (can) requiredDoable++;
            }

            switch (step.CompletionMode)
            {
                case QuestStepCompletion.Any:
                    if (required > 0 && doable == 0) return false;
                    break;
                case QuestStepCompletion.NOf:
                    if (doable < Math.Min(Math.Max(1, step.NRequired), Math.Max(1, required))) return false;
                    break;
                default:
                    if (requiredDoable < required) return false;
                    break;
            }
        }

        return true;
    }

    /// <summary>
    /// The offer to accept: a quest with a known definition, not already run or being accepted, doable, and not far above the
    /// bot's level (LevelHint ≤ level + 5; 0 = no hint). Lowest LevelHint first, then lowest id. 0 = none, or at the cap.
    /// </summary>
    public static int ChooseOffer(IEnumerable<int> offered, IReadOnlyDictionary<int, S.QuestDefinition> definitions,
        IReadOnlyDictionary<int, QuestRun> runs, IReadOnlyDictionary<int, long> pending, int level, int maxActive)
    {
        if (offered == null || runs.Count + (pending?.Count ?? 0) >= maxActive) return 0;

        int best = 0, bestHint = int.MaxValue;
        foreach (var quest in offered)
        {
            if (runs.ContainsKey(quest) || (pending != null && pending.ContainsKey(quest))) continue;
            if (!definitions.TryGetValue(quest, out var def) || !IsDoable(def)) continue;
            if (def.LevelHint > level + 5) continue;
            if (def.LevelHint < bestHint || (def.LevelHint == bestHint && quest < best))
            {
                best = quest;
                bestHint = def.LevelHint;
            }
        }

        return best;
    }

    /// <summary>
    /// Monster names (and info indexes) of the unmet Kill objectives on each Active run's current step. A Monster target is
    /// resolved through <paramref name="monsterNames"/>; a MonsterGroup target matches every known monster with the group's
    /// label name (the server names the group after its first member and sends every member's info with the definition).
    /// </summary>
    public static void KillTargets(IReadOnlyDictionary<int, S.QuestDefinition> definitions, IEnumerable<QuestRun> runs,
        IReadOnlyDictionary<int, string> monsterNames, ISet<string> names, ISet<int> indexes)
    {
        foreach (var run in runs)
        {
            if (run.Status != QuestRunStatus.Active || !definitions.TryGetValue(run.Quest, out var def)) continue;
            foreach (var o in def.Objectives)
            {
                if (o.Step != run.Step || o.Type != QuestObjectiveType.Kill) continue;
                if (run.Values.TryGetValue(o.Index, out var value) && value >= Math.Max(1, o.Count)) continue;

                if (o.TargetKind == QuestTargetKind.Monster)
                {
                    indexes?.Add(o.TargetId);
                    if (monsterNames.TryGetValue(o.TargetId, out var name) && !string.IsNullOrEmpty(name)) names.Add(name);
                }
                else if (o.TargetKind == QuestTargetKind.MonsterGroup && !string.IsNullOrEmpty(o.TargetName))
                {
                    names.Add(o.TargetName);
                    foreach (var pair in monsterNames)
                        if (string.Equals(pair.Value, o.TargetName, StringComparison.OrdinalIgnoreCase)) indexes?.Add(pair.Key);
                }
            }
        }
    }

    /// <summary>
    /// C.QuestTurnIn reward picks: one position per choice group of the Quest-scope rewards, in the definition's Rewards order
    /// (the server's filtered list). Within a group an item reward wins over a currency, then the larger amount, then the first.
    /// </summary>
    public static List<int> RewardPicks(S.QuestDefinition def)
    {
        var picks = new List<int>();
        if (def == null) return picks;

        var bestByGroup = new Dictionary<int, int>();
        var order = new List<int>();
        for (int i = 0; i < def.Rewards.Count; i++)
        {
            var r = def.Rewards[i];
            if (r.Scope != QuestRewardScope.Quest || r.ChoiceGroup == 0) continue;
            if (!bestByGroup.TryGetValue(r.ChoiceGroup, out var current))
            {
                bestByGroup[r.ChoiceGroup] = i;
                order.Add(r.ChoiceGroup);
                continue;
            }

            if (Better(r, def.Rewards[current])) bestByGroup[r.ChoiceGroup] = i;
        }

        foreach (var group in order) picks.Add(bestByGroup[group]);
        return picks;
    }

    private static bool Better(S.QuestDefinition.Reward a, S.QuestDefinition.Reward b)
    {
        bool aItem = a.Kind == QuestRewardKind.Item, bItem = b.Kind == QuestRewardKind.Item;
        if (aItem != bItem) return aItem;
        return a.Amount > b.Amount;
    }

    /// <summary>
    /// The run to abandon now, or 0: a Failed run at once, an Active run with no progress for <paramref name="stuckMs"/>, or an
    /// Active run whose current step the bot cannot do (e.g. a shared quest it took, or a step reached later). Ready runs
    /// never, nor runs the definition forbids abandoning (story quests), nor runs already asked once.
    /// </summary>
    public static int StuckRun(IReadOnlyDictionary<int, S.QuestDefinition> definitions, IEnumerable<QuestRun> runs, long now, long stuckMs)
    {
        foreach (var run in runs)
        {
            if (run.AbandonSent) continue;
            if (run.Status == QuestRunStatus.Failed) return run.Quest;
            if (run.Status != QuestRunStatus.Active) continue;
            definitions.TryGetValue(run.Quest, out var def);
            if (def != null && !def.AbandonAllowed) continue;
            if (now - run.LastProgressAt >= stuckMs) return run.Quest;
            if (def != null && !StepDoable(def, run.Step)) return run.Quest;
        }

        return 0;
    }

    private static bool StepDoable(S.QuestDefinition def, int stepIndex)
    {
        foreach (var step in def.Steps)
        {
            if (step.Index != stepIndex) continue;
            var single = new S.QuestDefinition { Quest = def.Quest };
            single.Steps.Add(step);
            foreach (var o in def.Objectives) if (o.Step == stepIndex) single.Objectives.Add(o);
            return IsDoable(single);
        }

        return true; // unknown step (definition edited): let the stuck timer decide
    }
}

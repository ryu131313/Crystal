using System.Drawing;

namespace StressClient;

/// <summary>
/// "What would a player do" priority FSM. Every branch is early-returning and paced by the bot's action timers:
///   potion → quest housekeeping (abandon stuck runs, take non-NPC offers) → town run (quests / buy / sell / repair) →
///   equip an upgrade → loot → hunt (quest targets first) → social → Director → roam.
/// Movement and packet ownership stay in <see cref="VirtualPlayer"/>.
/// </summary>
internal sealed class BotBrain
{
    private enum TownState { None, ToNpc, Greet, BuySell, Repair, Finish }

    private readonly VirtualPlayer _v;
    private readonly Random _rng;

    private uint _targetId, _pathTargetId;
    private Point _pathTargetPos;
    private long _ignoreTargetUntil, _nextRepathAt, _nextAttackAt;
    private long _nextRoamPickAt, _nextDirectiveAskAt;

    private Point? _anchor;
    private Point? _home;
    private int _lastMapIndex = -1;

    private MirDirection _greedyHeading;
    private int _greedySteps;

    // town / economy
    private TownState _town;
    private uint _townNpc, _townPathNpc;
    private Point? _townGoal;
    private long _townUntil, _townStepAt, _nextTownAt;
    private int _townStage, _buyPotions, _questActions;

    // quests (V2: the state lives in WorldModel.Quests)
    private long _nextQuestCheckAt;

    /// <summary>Quest requests (turn in / accept) at one NPC per visit, so a refused request cannot loop.</summary>
    private const int MaxQuestActionsPerVisit = 6;

    /// <summary>A quest target counts as this many cells closer when picking what to hunt.</summary>
    private const int QuestTargetBias = 6;

    // loot / social
    private uint _lootId;
    private long _lootIgnoreUntil, _nextLootAt;
    private long _nextChatAt, _nextInviteAt;

    private static readonly string[] Phrases = { "hello", "good hunting", "anyone around?", "need a group?" };

    public BotBrain(VirtualPlayer bot, Random rng)
    {
        _v = bot;
        _rng = rng;
        _greedyHeading = (MirDirection)rng.Next(8);
        _nextChatAt = 15000 + rng.Next(20000);
        _nextInviteAt = 30000 + rng.Next(30000);
        _nextTownAt = 20000 + rng.Next(20000);
    }

    public void Reset()
    {
        _targetId = _pathTargetId = 0;
        _ignoreTargetUntil = _nextRepathAt = _nextAttackAt = 0;
        _nextRoamPickAt = _nextDirectiveAskAt = 0;
        _anchor = null;
        _home = null;
        _lastMapIndex = -1;
        _greedySteps = 0;

        _town = TownState.None;
        _townNpc = _townPathNpc = 0;
        _townGoal = null;
        _townUntil = _townStepAt = 0;
        _townStage = _buyPotions = _questActions = 0;
        _nextQuestCheckAt = 0;
        _nextTownAt = 20000 + _rng.Next(20000);

        _lootId = 0;
        _lootIgnoreUntil = _nextLootAt = 0;
        _nextChatAt = 15000 + _rng.Next(20000);
        _nextInviteAt = 30000 + _rng.Next(30000);
    }

    public void Tick(long now)
    {
        var world = _v.World;

        if (world.MapIndex != _lastMapIndex)
        {
            _lastMapIndex = world.MapIndex;
            _v.Nav.Clear();
            _targetId = _pathTargetId = 0;
            _anchor = world.Location;
            _home = world.Location;
            _town = TownState.None;
            _lootId = 0;
        }

        if (_v.MoveOutstanding) return;
        if (now < _v.NextActionAt) return;

        var o = _v.Options;
        if (o.Potions && _v.TryDrinkPotion(now)) return;
        if (_town != TownState.None) { TownTick(now); return; }

        if (o.Quests && TryQuestHousekeeping(now)) return;
        if ((o.Town || o.Quests) && TryStartTown(now)) return;
        if (o.Equip && _v.TryEquipBetter(now)) return;
        if (o.Loot && TryLoot(now)) return;
        if (o.Hunt && TryHunt(now)) return;
        if ((o.Chat || o.Party) && TrySocial(now)) return;
        if (TryDirective(now)) return;
        Roam(now);
    }

    // ------------------------------------------------------------------ town runs

    private bool NeedsTown(long now)
    {
        var o = _v.Options;
        if (o.Town)
        {
            if (_v.World.FirstJunk() != null) return true;
            if (_v.World.HasDamagedEquipment(out _)) return true;
            if (_v.World.PotionCount() < 3) return true;
        }

        // A Ready quest needs a trip to its finish NPC; an NPC in view offering a quest the bot would take is worth a visit.
        if (o.Quests && (_v.World.Quests.AnyReady || _v.World.Quests.AnyOfferInView(_v.World.Level))) return true;

        return now - _nextTownAt > 180_000;
    }

    private bool TryStartTown(long now)
    {
        if (now < _nextTownAt) return false;

        if (!NeedsTown(now))
        {
            _nextTownAt = now + 20_000;
            return false;
        }

        NpcState npc = PreferredNpc();
        Point? goal = npc?.Location ?? ReadyQuestSpot() ?? _home;
        if (goal == null)
        {
            _nextTownAt = now + 20_000;
            return false;
        }

        _town = TownState.ToNpc;
        _townNpc = npc?.Id ?? 0;
        _townGoal = goal;
        _townPathNpc = 0;
        _townUntil = now + 90_000; // the walk home can be long after a Director excursion
        _townStepAt = now;
        _townStage = _buyPotions = _questActions = 0;
        _v.Nav.Clear();
        return true;
    }

    private void TownTick(long now)
    {
        if (now > _townUntil) { EndTown(now); return; }

        var world = _v.World;

        if (_townNpc == 0)
        {
            var found = PreferredNpc();
            if (found != null)
            {
                _townNpc = found.Id;
                _townGoal = found.Location;
                _townPathNpc = 0;
                _v.Nav.Clear();
                return;
            }

            if (_townGoal == null || Functions.MaxDistance(world.Location, _townGoal.Value) <= 2)
            {
                EndTown(now);
                return;
            }

            if (!_v.Nav.HasPath)
            {
                var path = _v.FindPath(_townGoal.Value, 0, now);
                if (path == null) { EndTown(now); return; }
                _v.Nav.SetPath(path, world.Location);
            }

            _v.MoveAlongPath(now);
            return;
        }

        if (!world.Npcs.TryGetValue(_townNpc, out var npc))
        {
            // Merchant left our view: walk back / re-acquire it instead of giving up.
            _townNpc = 0;
            _townStepAt = now;
            return;
        }

        switch (_town)
        {
            case TownState.ToNpc:
                if (Functions.MaxDistance(world.Location, npc.Location) <= 6)
                {
                    _v.Nav.Clear();
                    _v.CallNpc(_townNpc, "[@MAIN]", now);
                    _town = TownState.Greet;
                    _townStepAt = now + 600;
                    return;
                }

                if (!_v.Nav.HasPath || _townPathNpc != _townNpc)
                {
                    var goal = PickCellNear(npc.Location, world.Location);
                    if (goal == null) { EndTown(now); return; }

                    var path = _v.FindPath(goal.Value, 0, now);
                    if (path == null) { EndTown(now); return; }

                    Metrics.Inc(ref _v.Metrics.PathFound);
                    _v.Nav.SetPath(path, world.Location);
                    _townPathNpc = _townNpc;
                }

                _v.MoveAlongPath(now);
                return;

            case TownState.Greet:
                if (now < _townStepAt) return;
                // Turn in, then accept, one request per step: the NPC's S.QuestAvailability entry is refreshed in between.
                if (_v.Options.Quests && TryQuestAtNpc(npc.Id, now))
                {
                    _townStepAt = now + 700;
                    return;
                }
                // Quest-only trips (--no-town) skip the shop and the smith.
                _town = _v.Options.Town ? TownState.BuySell : TownState.Finish;
                _townStage = 0;
                _townStepAt = now + 300;
                return;

            case TownState.BuySell:
                if (_townStage == 0)
                {
                    _v.CallNpc(_townNpc, "[@BUYSELL]", now);
                    _townStage = 1;
                    _townStepAt = now + 600;
                    return;
                }
                if (now < _townStepAt) return;

                var junk = world.FirstJunk();
                if (junk != null && _v.Sell(junk, now))
                {
                    _townStepAt = now + 350;
                    return;
                }

                if (_buyPotions < 3 && TryBuyPotion(now))
                {
                    _buyPotions++;
                    _townStepAt = now + 500;
                    return;
                }

                _town = TownState.Repair;
                _townStage = 0;
                _townStepAt = now + 200;
                return;

            case TownState.Repair:
                if (_townStage == 0)
                {
                    _v.CallNpc(_townNpc, "[@REPAIR]", now);
                    _townStage = 1;
                    _townStepAt = now + 500;
                    return;
                }
                if (now < _townStepAt) return;

                if (world.HasDamagedEquipment(out var damaged) && _v.Repair(damaged, now))
                {
                    _townStepAt = now + 350;
                    return;
                }

                _town = TownState.Finish;
                _townStepAt = now + 200;
                return;

            case TownState.Finish:
                if (now < _townStepAt) return;
                EndTown(now);
                return;
        }

        EndTown(now);
    }

    // ------------------------------------------------------------------ quests

    /// <summary>One quest request at <paramref name="npcId"/>: turn in a Ready run it finishes, else accept an offer. False = nothing to do.</summary>
    private bool TryQuestAtNpc(uint npcId, long now)
    {
        if (_questActions >= MaxQuestActionsPerVisit) return false;
        var quests = _v.World.Quests;

        int quest = quests.ReadyAt(npcId);
        if (quest != 0)
        {
            _v.TurnInQuest(quest, npcId, now);
            _questActions++;
            return true;
        }

        quest = quests.ChooseOfferAt(npcId, _v.World.Level);
        if (quest != 0)
        {
            _v.AcceptQuest(quest, npcId, now);
            _questActions++;
            return true;
        }

        return false;
    }

    /// <summary>Every few seconds: drop unanswered requests, abandon a failed or stuck run, take an offer that came without an NPC.</summary>
    private bool TryQuestHousekeeping(long now)
    {
        if (now < _nextQuestCheckAt) return false;
        _nextQuestCheckAt = now + 5000;

        var quests = _v.World.Quests;
        quests.ExpirePending(now, 15_000);

        int stuck = QuestLogic.StuckRun(quests.Definitions, quests.Runs.Values, now, _v.Options.QuestStuckSeconds * 1000L);
        if (stuck != 0)
        {
            _v.AbandonQuest(stuck, now);
            return true;
        }

        int offer = quests.ChooseNonNpcOffer(_v.World.Level);
        if (offer != 0)
        {
            _v.AcceptQuest(offer, 0, now);
            return true;
        }

        return false;
    }

    /// <summary>The NPC in view to visit: one that finishes a Ready run, then one offering a quest the bot would take, else the nearest.</summary>
    private NpcState PreferredNpc()
    {
        if (_v.Options.Quests)
        {
            var quests = _v.World.Quests;
            NpcState offering = null;
            foreach (var npc in _v.World.Npcs.Values)
            {
                if (quests.ReadyAt(npc.Id) != 0) return npc;
                if (offering == null && quests.ChooseOfferAt(npc.Id, _v.World.Level) != 0) offering = npc;
            }
            if (offering != null) return offering;
        }

        return NearestNpc();
    }

    /// <summary>Where a Ready run's finish NPC stood when last seen on this map (it may be out of view now), or null.</summary>
    private Point? ReadyQuestSpot()
    {
        if (!_v.Options.Quests) return null;
        var quests = _v.World.Quests;
        foreach (var run in quests.Runs.Values)
        {
            if (run.Status != QuestRunStatus.Ready) continue;
            if (quests.FinishSpots.TryGetValue(run.Quest, out var spot) && spot.MapIndex == _v.World.MapIndex) return spot.Location;
        }
        return null;
    }

    private bool TryBuyPotion(long now)
    {
        var world = _v.World;
        var goods = _v.FindGoods(info => info.Type == ItemType.Potion && info.Stats[Stat.HP] > 0);
        if (goods == null) return false;
        if (goods.Info != null && world.Gold < goods.Info.Price) return false;
        return _v.Buy(goods, 1, now);
    }

    private void EndTown(long now)
    {
        _town = TownState.None;
        _townNpc = _townPathNpc = 0;
        _townGoal = null;
        _townStage = _buyPotions = _questActions = 0;
        _nextTownAt = now + 45_000 + _rng.Next(30_000);
        _v.Nav.Clear();
    }

    private NpcState NearestNpc()
    {
        NpcState best = null;
        int bestDistance = int.MaxValue;
        foreach (var npc in _v.World.Npcs.Values)
        {
            int distance = Functions.MaxDistance(_v.World.Location, npc.Location);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = npc;
        }
        return best;
    }

    private Point? PickCellNear(Point target, Point from)
    {
        Point? best = null;
        int bestDistance = int.MaxValue;

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int x = target.X + dx, y = target.Y + dy;

                if (_v.Map is { } grid && (!grid.InBounds(x, y) || grid.IsBlocked(x, y))) continue;
                if (_v.World.IsBlockedByObject(x, y, 0)) continue;

                int distance = Functions.MaxDistance(from, new Point(x, y));
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = new Point(x, y);
            }
        }

        return best;
    }

    // ------------------------------------------------------------------ loot

    private bool TryLoot(long now)
    {
        var world = _v.World;
        if (world.BagFull || now < _nextLootAt || now < _lootIgnoreUntil) return false;

        uint id = 0;
        Point goal = default;
        int bestDistance = int.MaxValue;

        foreach (var item in world.Items.Values)
        {
            int distance = Functions.MaxDistance(world.Location, item.Location);
            if (distance > 8 || distance >= bestDistance) continue;
            bestDistance = distance;
            id = item.Id;
            goal = item.Location;
        }

        foreach (var gold in world.GoldPiles.Values)
        {
            int distance = Functions.MaxDistance(world.Location, gold.Location);
            if (distance > 8 || distance >= bestDistance) continue;
            bestDistance = distance;
            id = gold.Id;
            goal = gold.Location;
        }

        if (id == 0) { _lootId = 0; return false; }

        if (world.Location == goal)
        {
            _v.Nav.Clear();
            _v.PickUpItem(now);
            _lootId = 0;
            _nextLootAt = now + 800;
            return true;
        }

        if (_lootId != id || !_v.Nav.HasPath)
        {
            var path = _v.FindPath(goal, 0, now);
            if (path == null)
            {
                _lootIgnoreUntil = now + 5000;
                _lootId = 0;
                return false;
            }

            Metrics.Inc(ref _v.Metrics.PathFound);
            _v.Nav.SetPath(path, world.Location);
            _lootId = id;
        }

        _v.MoveAlongPath(now);
        _nextLootAt = now + 300;
        return true;
    }

    // ------------------------------------------------------------------ social

    private bool TrySocial(long now)
    {
        if (_v.Options.Chat && now >= _nextChatAt)
        {
            _v.SendChat(Phrases[_rng.Next(Phrases.Length)], now);
            _nextChatAt = now + 45_000 + _rng.Next(45_000);
            return true;
        }

        if (_v.Options.Party && !_v.World.InGroup && now >= _nextInviteAt)
        {
            _nextInviteAt = now + 90_000 + _rng.Next(60_000);

            PlayerState best = null;
            int bestDistance = int.MaxValue;
            foreach (var player in _v.World.Players.Values)
            {
                int distance = Functions.MaxDistance(_v.World.Location, player.Location);
                if (distance > 8 || distance >= bestDistance) continue;
                bestDistance = distance;
                best = player;
            }

            if (best != null)
            {
                _v.InvitePlayer(best.Name, now);
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ hunt

    private bool TryHunt(long now)
    {
        var world = _v.World;
        var monster = PickTarget(now);
        if (monster == null) return false;

        int distance = Functions.MaxDistance(world.Location, monster.Location);

        // Cast when the target is out of melee reach (and never while standing on it).
        if (distance > 1 && _v.TryCastAt(monster, now)) return true;

        if (distance <= 1)
        {
            _v.Nav.Clear();
            if (now >= _nextAttackAt)
            {
                _v.Attack(Functions.DirectionFromPoint(world.Location, monster.Location), monster.Id, now);
                _nextAttackAt = now + _v.Options.AttackDelayMs;
                _pathTargetId = monster.Id;
            }
            return true;
        }

        if (_v.Map == null)
        {
            // No terrain data: the old greedy chase (walk in the target's direction until blocked).
            _pathTargetId = monster.Id;
            var dir = Functions.DirectionFromPoint(world.Location, monster.Location);
            _v.WalkGreedy(dir, now);
            return true;
        }

        bool repath = !_v.Nav.HasPath
                      || _pathTargetId != monster.Id
                      || _pathTargetPos != monster.Location
                      || Functions.MaxDistance(_v.Nav.Goal, monster.Location) > 1;

        if (repath)
        {
            if (now < _nextRepathAt) return true;
            _nextRepathAt = now + 250;

            Point? goal = ChooseAdjacentCell(monster);
            if (goal == null)
            {
                IgnoreTarget(now);
                return false;
            }

            var path = _v.FindPath(goal.Value, monster.Id, now);
            if (path == null)
            {
                Metrics.Inc(ref _v.Metrics.PathFail);
                IgnoreTarget(now);
                return false;
            }

            Metrics.Inc(ref _v.Metrics.PathFound);
            _v.Nav.SetPath(path, world.Location);
            _pathTargetId = monster.Id;
            _pathTargetPos = monster.Location;
        }

        _v.MoveAlongPath(now);
        return true;
    }

    private MonsterState PickTarget(long now)
    {
        if (now < _ignoreTargetUntil) return null;

        var world = _v.World;

        // Fight back first: a monster that just hit us outranks the nearest one.
        if (world.LastAttackerId != 0 && now - world.LastAttackerAt < 10_000 &&
            world.Monsters.TryGetValue(world.LastAttackerId, out var attacker) && attacker.Huntable &&
            Functions.MaxDistance(world.Location, attacker.Location) <= _v.Options.SeekRange + 2)
        {
            _targetId = attacker.Id;
            return attacker;
        }

        if (_targetId != 0 && world.Monsters.TryGetValue(_targetId, out var current) && current.Huntable &&
            Functions.MaxDistance(world.Location, current.Location) <= _v.Options.SeekRange)
            return current;

        uint bestId = 0;
        MonsterState best = null;
        int bestDistance = int.MaxValue;
        int levelBand = world.Level + 5;
        bool questTargets = _v.Options.Quests && world.Quests.HasTargets;

        foreach (var mon in world.Monsters.Values)
        {
            if (!mon.Huntable) continue;                       // corpses, pets and town guards
            if (mon.Level > levelBand) continue;               // do not grind monsters far above our level

            int distance = Functions.MaxDistance(world.Location, mon.Location);
            if (distance > _v.Options.SeekRange) continue;
            if (questTargets && world.Quests.IsTarget(mon.Name)) distance -= QuestTargetBias; // quest kills first
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            bestId = mon.Id;
            best = mon;
        }

        _targetId = bestId;
        return best;
    }

    private Point? ChooseAdjacentCell(MonsterState monster)
    {
        var world = _v.World;
        Point? best = null;
        int bestDistance = int.MaxValue;

        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int x = monster.Location.X + dx;
                int y = monster.Location.Y + dy;

                if (_v.Map is { } grid && (!grid.InBounds(x, y) || grid.IsBlocked(x, y))) continue;
                if (world.IsBlockedByObject(x, y, monster.Id)) continue;

                int distance = Functions.MaxDistance(world.Location, new Point(x, y));
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = new Point(x, y);
            }
        }

        return best;
    }

    private void IgnoreTarget(long now)
    {
        _ignoreTargetUntil = now + 3000;
        _targetId = 0;
        _pathTargetId = 0;
        _v.Nav.Clear();
    }

    // ------------------------------------------------------------------ director

    private bool TryDirective(long now)
    {
        var directive = _v.TakeDirective();
        if (directive == null) return false;

        _anchor = directive.Location;
        _targetId = _pathTargetId = 0;
        _v.Nav.Clear();
        return WalkToAnchor(now, directive.Location, force: true);
    }

    // ------------------------------------------------------------------ roam

    private void Roam(long now)
    {
        if (_anchor == null)
        {
            _anchor = _v.World.Location;
            AskDirector(now);
        }

        var world = _v.World;
        if (world.Location == _anchor.Value && !_v.Nav.HasPath)
            AskDirector(now);

        if (_v.Map == null)
        {
            GreedyRoam(now);
            return;
        }

        if (_v.Nav.HasPath)
        {
            _v.MoveAlongPath(now);
            return;
        }

        if (now < _nextRoamPickAt) return;
        _nextRoamPickAt = now + 400;

        WalkToAnchor(now, _anchor.Value, force: false);
    }

    private bool WalkToAnchor(long now, Point anchor, bool force)
    {
        var world = _v.World;

        if (_v.Map == null)
        {
            GreedyRoam(now);
            return true;
        }

        int radius = Math.Max(1, _v.Options.RoamRadius);
        for (int attempt = 0; attempt < 12; attempt++)
        {
            int x = anchor.X + _rng.Next(-radius, radius + 1);
            int y = anchor.Y + _rng.Next(-radius, radius + 1);
            var goal = new Point(x, y);

            if (!_v.Map.InBounds(x, y) || _v.Map.IsBlocked(x, y)) continue;
            if (world.IsBlockedByObject(x, y, 0)) continue;
            if (Functions.MaxDistance(world.Location, goal) <= 1 && !force) return false;

            var path = _v.FindPath(goal, 0, now);
            if (path == null) continue;

            Metrics.Inc(ref _v.Metrics.PathFound);
            _v.Nav.SetPath(path, world.Location);
            _v.MoveAlongPath(now);
            return true;
        }

        Metrics.Inc(ref _v.Metrics.PathFail);
        AskDirector(now);
        return false;
    }

    private void AskDirector(long now)
    {
        if (!_v.Options.DirectorEnabled || now < _nextDirectiveAskAt) return;
        _nextDirectiveAskAt = now + 2000;
        _v.RequestDirective();
    }

    /// <summary>The pre-pathfinding behaviour, kept for runs without map data so a scratch server still gets load.</summary>
    private void GreedyRoam(long now)
    {
        if (_greedySteps <= 0 || _v.LastMoveBlocked)
        {
            var old = _greedyHeading;
            _greedyHeading = (MirDirection)_rng.Next(8);
            _greedySteps = 3 + _rng.Next(10);

            if (_v.LastMoveBlocked || _greedyHeading != old)
            {
                _v.Turn(_greedyHeading, now);
                return;
            }
        }

        _greedySteps--;
        _v.WalkGreedy(_greedyHeading, now);
    }
}

using System.Collections.Concurrent;
using System.Drawing;
using C = ClientPackets;
using S = ServerPackets;

namespace StressClient;

/// <summary>
/// One headless player. Unlike the first incarnation it has no loop of its own: a worker thread calls
/// <see cref="Tick"/> (login FSM + behaviour), while a per-session task owns the socket: the receive loop decodes and
/// queues, the send loop drains the outbox through the token bucket. The login sequence mirrors the real client
/// (Client/MirScenes/LoginScene.cs, SelectScene.cs):
///   S.Connected → C.ClientVersion → S.ClientVersion(1) → C.Login
///     → S.Login(3: no account) → C.NewAccount → S.NewAccount(8 or 7) → C.Login
///     → S.LoginSuccess(chars) → [C.NewCharacter → S.NewCharacterSuccess] → C.StartGame
///     → S.StartGame(4) + S.MapChanged + S.UserInformation → in game.
/// </summary>
internal sealed class VirtualPlayer
{
    private enum State { Idle, Connecting, AwaitConnected, AwaitVersion, AwaitLogin, AwaitNewAccount, AwaitNewCharacter, AwaitStartGame, AwaitUserInfo, InGame }

    private static long Now => MonoClock.Now;

    private readonly int _index;
    private readonly Options _o;

    // Classes the server lets new characters have (S.ServerRules, or learnt from a Result=3 refusal).
    private bool _assassinAllowed = true, _archerAllowed = true;
    private MirClass _triedClass;
    private readonly Metrics _m;
    private readonly SemaphoreSlim _connectBudget;
    private readonly WorldDirectory _directory;
    private readonly MapManager _maps;
    private readonly Director _director;
    private readonly CancellationToken _stop;
    private readonly Random _rng;
    private readonly string _account, _character;

    internal readonly WorldModel World = new();
    internal readonly Navigator Nav = new();
    private readonly BotBrain _brain;

    private readonly ConcurrentQueue<Packet> _outbox = new();
    private readonly SemaphoreSlim _sendSignal = new(0);
    private readonly Dictionary<int, long> _avoid = new();

    private BotSocket _socket;
    private CancellationTokenSource _sessionCts;
    private Task<BotSocket.ConnectResult> _connectTask;
    private Task _sendTask;
    private TokenBucket _bucket;

    private State _state = State.Idle;
    private long _stateSince;
    private bool _holdingBudget, _countedConnected, _inGameCounted, _accountCreateTried;
    private bool _reachedGameOnce, _reachedThisSession, _done;
    private volatile bool _stopRequested;
    private long _nextConnectAt;
    private int _failures, _sessionsStarted;

    // movement
    private bool _moveOutstanding, _lastMoveBlocked;
    private Point _moveFrom, _moveExpected;
    private int _advanceOnAck = 1;
    private long _moveSentAt, _nextDoorAt;
    private int _runStreak;
    private uint _lastAttackedId;
    private ulong _lastEquipId, _pendingSaleId, _saleBlockedId;
    private long _nextEquipAt, _pendingSaleAt, _saleBlockedAt;

    // NPC / economy / social
    private readonly List<UserItem> _npcGoods = new();
    internal IReadOnlyList<UserItem> NpcGoods => _npcGoods;
    internal PanelType NpcPanel;
    internal long NextPotionAt, NextPickupAt, NextGroupReplyAt;
    internal readonly Dictionary<Spell, long> SpellReadyAt = new();
    internal long NextMagicAt;

    // pacing
    private long _nextActionAt, _lastKeepAlive;

    // death
    private long _deadSince, _reviveSentAt;

    // map details
    private int _detailsMapIndex = -1;
    private long _detailsRequestedAt;

    private volatile Directive _directive;

    // S.QuestStates outcomes, reused per packet
    private readonly List<int> _questAccepted = new(), _questTurnedIn = new(), _questAbandoned = new();

    /// <summary>Set by the ramp: an unreleased bot stays idle and never opens a socket.</summary>
    internal volatile bool Released;

    public VirtualPlayer(int index, Options options, Metrics metrics, SemaphoreSlim connectBudget,
        WorldDirectory directory, MapManager maps, Director director, CancellationToken stop)
    {
        _index = index;
        _o = options;
        _m = metrics;
        _connectBudget = connectBudget;
        _directory = directory;
        _maps = maps;
        _director = director;
        _stop = stop;
        _rng = new Random(options.Seed + index);
        _account = $"{options.Prefix}{index:D5}";
        _character = $"{options.Prefix}c{index:D5}";
        _brain = new BotBrain(this, _rng);
    }

    public bool ReachedGame => _reachedGameOnce;
    public int Index => _index;

    /// <summary>Session liveness: when the socket last decoded a packet / completed a write.</summary>
    public long LastReceivedAt => _socket?.LastReceivedAt ?? 0;
    public long LastSentAt => _socket?.LastSentAt ?? 0;

    internal Options Options => _o;
    internal Metrics Metrics => _m;
    internal Random Rng => _rng;
    internal bool IsPlaying => _state == State.InGame;
    internal bool MoveOutstanding => _moveOutstanding;
    internal long NextActionAt => _nextActionAt;
    internal bool LastMoveBlocked => _lastMoveBlocked;

    /// <summary>The current map's collision grid, or null when the map file was not found (greedy fallback).</summary>
    internal MapGrid Map => string.IsNullOrEmpty(World.MapFileName) ? null : _maps.Get(World.MapFileName);

    // ================================================================== lifecycle

    public void Tick(long now)
    {
        DrainReceived();

        if (_state == State.Connecting)
        {
            if (!_connectTask.IsCompleted) return;
            CompleteConnect(now);
            return;
        }

        if (_socket != null && _socket.IsClosed && _state != State.Idle)
        {
            EndSession(now);
            return;
        }

        if (_sendTask is { IsCompleted: true } && _socket is { IsClosed: false } && _state != State.Idle)
        {
            // The send task must outlive the session; if it stopped, the bot is deaf and silent.
            _socket.Close("send loop stopped");
            return;
        }

        if (_state == State.Idle)
        {
            if (Released && !_done && now >= _nextConnectAt) StartConnect(now);
            return;
        }

        if (_state != State.InGame && now - _stateSince > _o.LoginTimeoutSeconds * 1000L)
        {
            Metrics.Inc(ref _m.LoginTimeouts);
            Log($"timed out in {_state}");
            _socket.Close($"timeout in {_state}");
            return;
        }

        if (now - _lastKeepAlive >= 5000)
        {
            _lastKeepAlive = now;
            Send(new C.KeepAlive { Time = now });
        }

        if (NextGroupReplyAt != 0 && now >= NextGroupReplyAt)
        {
            NextGroupReplyAt = 0;
            ReplyGroup(_o.Party, now);
        }

        if (_state != State.InGame) return;

        if (World.Dead)
        {
            // Like a player pressing "town revive" after a moment; resend if nothing came back.
            if (now - _deadSince > 2000 && (_reviveSentAt == 0 || now - _reviveSentAt > 10_000))
            {
                _reviveSentAt = now;
                Send(new C.TownRevive());
            }
            return;
        }

        if (_moveOutstanding && now - _moveSentAt > 3000)
        {
            _moveOutstanding = false; // lost reply: give up waiting
            Metrics.Inc(ref _m.MoveTimeouts);
            Metrics.Inc(ref _m.Repaths);
            Nav.Clear();
        }

        if (now < _nextActionAt) return;
        _brain.Tick(now);
    }

    private void StartConnect(long now)
    {
        if (!_connectBudget.Wait(0)) return;
        _holdingBudget = true;

        if (++_sessionsStarted > 1) Metrics.Inc(ref _m.Reconnects);
        Metrics.Inc(ref _m.ConnectAttempts);
        Interlocked.Increment(ref _m.Connecting);

        World.ClearSession();
        Nav.Clear();
        _brain.Reset();
        while (_outbox.TryDequeue(out _)) { }
        while (_sendSignal.Wait(0)) { }
        _avoid.Clear();
        _directive = null;
        _moveOutstanding = false;
        _reachedThisSession = false;
        _accountCreateTried = false;
        _detailsMapIndex = -1;
        _sendTask = null;
        _stopRequested = false;
        _pendingSaleId = 0;
        _saleBlockedId = 0;
        _saleBlockedAt = 0;

        // Not linked to the run token: Stop() must be able to flush C.Disconnect after the run is cancelled,
        // and only then cancel the session so the send loop ends.
        _sessionCts = new CancellationTokenSource();
        _socket = new BotSocket(_m);
        _bucket = new TokenBucket(_o.ServerMaxPacket, _o.PacketBudgetFraction, now);
        SetState(State.Connecting, now);

        _connectTask = _socket.ConnectAsync(_o.Host, _o.Port, 5000, _sessionCts.Token);
    }

    private void CompleteConnect(long now)
    {
        BotSocket.ConnectResult result;
        try { result = _connectTask.GetAwaiter().GetResult(); }
        catch { result = BotSocket.ConnectResult.Failed; }

        if (result != BotSocket.ConnectResult.Ok)
        {
            if (result == BotSocket.ConnectResult.Refused) Metrics.Inc(ref _m.Refused);
            else if (!_stop.IsCancellationRequested) Metrics.Inc(ref _m.Errors);

            ReleaseBudget();
            SetState(State.Idle, now);
            ScheduleReconnect(now);
            _socket = null;
            _sessionCts?.Cancel();
            return;
        }

        _socket.StartReceiving(_sessionCts.Token);
        BotSocket socket = _socket;
        TokenBucket bucket = _bucket;
        CancellationToken token = _sessionCts.Token;
        _sendTask = Task.Run(() => SendLoopAsync(socket, bucket, token));
        _lastKeepAlive = now;
        SetState(State.AwaitConnected, now);
    }

    private void EndSession(long now)
    {
        bool reached = _reachedThisSession;

        if (!_stop.IsCancellationRequested)
        {
            if (_state == State.AwaitConnected) Metrics.Inc(ref _m.Refused); // MaxUser, IP block, ...
            else Metrics.Inc(ref _m.Disconnects);
            if (!_o.Quiet && _state != State.AwaitConnected)
                Log($"disconnected in {_state}: {_socket?.CloseReason}");
        }

        _socket?.Close(_stop.IsCancellationRequested ? "duration over" : (_socket.CloseReason ?? "closed"));
        _sessionCts?.Cancel();

        if (_countedConnected) Interlocked.Decrement(ref _m.Connected);
        if (_inGameCounted) _m.LeaveGame();
        ReleaseBudget();

        _countedConnected = _inGameCounted = false;
        _moveOutstanding = false;
        _sendTask = null;
        World.ClearSession();
        Nav.Clear();
        _brain.Reset();
        _directive = null;

        SetState(State.Idle, now);
        _failures = reached ? 0 : _failures + 1;
        if (!_o.Reconnect && _reachedGameOnce) _done = true;
        ScheduleReconnect(now);
    }

    private void ScheduleReconnect(long now)
    {
        int backoff = Math.Min(30_000, 1000 * (1 << Math.Min(_failures, 5))) + _rng.Next(500);
        _nextConnectAt = now + backoff;
    }

    /// <summary>Called by the orchestrator after the worker threads have stopped: ask for a polite disconnect and let
    /// the send loop close the socket once the queue is empty. Never blocks the orchestrator.</summary>
    public void Stop()
    {
        _done = true;
        _stopRequested = true;

        if (_socket is { IsClosed: false })
        {
            if (_sendTask != null) Send(new C.Disconnect()); // send loop flushes it, then closes
            else _socket.Close("duration over");             // still connecting: nothing to say
        }
        else
        {
            try { _sessionCts?.Cancel(); } catch { }
        }
    }

    /// <summary>Last resort if a session did not close within the orchestrator's grace period.</summary>
    public void ForceStop()
    {
        try { _sessionCts?.Cancel(); } catch { }
        _socket?.Close("duration over");
    }

    public bool SessionClosed => _socket == null || _socket.IsClosed;

    private void ReleaseBudget()
    {
        if (!_holdingBudget) return;
        _holdingBudget = false;
        _connectBudget.Release();
    }

    private void SetState(State state, long now)
    {
        _state = state;
        _stateSince = now;
    }

    // ================================================================== outbound

    internal void Send(Packet packet)
    {
        _outbox.Enqueue(packet);
        try { _sendSignal.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task SendLoopAsync(BotSocket socket, TokenBucket bucket, CancellationToken ct)
    {
        try
        {
            await SendLoopCoreAsync(socket, bucket, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Metrics.Inc(ref _m.Errors);
            if (!_o.Quiet) Console.WriteLine($"  bot {_index} send loop error: {ex.GetType().Name}: {ex.Message}");
            socket.Close("send loop error");
        }
    }

    private async Task SendLoopCoreAsync(BotSocket socket, TokenBucket bucket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _sendSignal.WaitAsync(250, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }

            if (_outbox.IsEmpty)
            {
                if (_stopRequested)
                {
                    socket.Close("duration over");
                    return;
                }
                continue;
            }

            long now = Now;
            if (!bucket.TryTake(now))
            {
                Metrics.Inc(ref _m.Throttled);
                continue; // keep the packets; try again on the next signal/timeout
            }

            var batch = new List<Packet>(16);
            while (batch.Count < 256 && _outbox.TryDequeue(out var packet)) batch.Add(packet);
            if (batch.Count == 0) continue;

            if (!await socket.SendAsync(batch, ct).ConfigureAwait(false)) return;

            if (_stopRequested && _outbox.IsEmpty)
            {
                socket.Close("duration over");
                return;
            }

            if (!_outbox.IsEmpty) _sendSignal.Release();
        }
    }

    // ================================================================== inbound

    private void DrainReceived()
    {
        if (_socket == null) return;
        while (_socket.Received.TryDequeue(out var packet))
        {
            Handle(packet);
            if (_socket.IsClosed) break;
        }
    }

    private void Handle(Packet p)
    {
        long now = Now;

        switch (p)
        {
            case S.Connected:
                if (_state != State.AwaitConnected) break;
                Interlocked.Decrement(ref _m.Connecting);
                Interlocked.Increment(ref _m.Connected);
                _countedConnected = true;
                SetState(State.AwaitVersion, now);
                // CheckVersion is off on scratch servers; the hash content does not matter then (the codec needs a non-null array).
                Send(new C.ClientVersion { VersionHash = new byte[16] });
                break;

            case S.MapInformation mi:
                _m.MapEntries.AddOrUpdate(mi.FileName ?? "?", 1, (_, n) => n + 1);
                World.MapIndex = mi.MapIndex;
                World.MapFileName = mi.FileName ?? string.Empty;
                World.MapTitle = mi.Title ?? string.Empty;
                World.MapChangedAt = now;
                _directory.MapChanged(mi.MapIndex, mi.FileName, mi.Title);
                RequestMapDetails(now, mi.MapIndex);
                break;

            case S.ClientVersion v:
                if (v.Result != 1) { Fail("wrong client version (set [General] CheckVersion=False on the scratch server)"); break; }
                SendLogin();
                break;

            case S.Login l:
                if (l.Result == 3 && !_accountCreateTried)
                {
                    _accountCreateTried = true;
                    SetState(State.AwaitNewAccount, now);
                    Send(new C.NewAccount
                    {
                        AccountID = _account,
                        Password = _o.Password,
                        BirthDate = new DateTime(2000, 1, 1),
                        UserName = _account,
                        SecretQuestion = "bot",
                        SecretAnswer = "bot",
                        EMailAddress = _account + "@stress.test",
                    });
                }
                else if (l.Result == 4)
                    GiveUp("wrong password for an existing account (made by an earlier run with another --password): use that password, or another --prefix / --first-index");
                else Fail($"login refused (S.Login Result={l.Result})");
                break;

            case S.LoginBanned b:
                GiveUp($"login banned until {b.ExpiryDate:u}: {b.Reason}");
                break;

            case S.NewAccount na:
                if (na.Result == 8) Metrics.Inc(ref _m.AccountsCreated);
                if (na.Result is 8 or 7) SendLogin();
                else Fail($"account creation refused (S.NewAccount Result={na.Result})");
                break;

            case S.LoginSuccess ls:
                Metrics.Inc(ref _m.Logins);
                SelectInfo existing = null;
                foreach (var c in ls.Characters)
                    if (string.Equals(c.Name, _character, StringComparison.OrdinalIgnoreCase)) { existing = c; break; }
                existing ??= ls.Characters.Count > 0 ? ls.Characters[0] : null;
                if (existing != null) SendStartGame(existing.Index);
                else
                {
                    SetState(State.AwaitNewCharacter, now);
                    SendNewCharacter();
                }
                break;

            case S.ServerRules rules:
                _assassinAllowed = rules.AllowCreateAssassin;
                _archerAllowed = rules.AllowCreateArcher;
                break;

            case S.NewCharacter nc:
                // Result 3 = class disabled ([Permission] AllowCreateAssassin/Archer): pick an allowed class and retry.
                if (nc.Result == 3 && _state == State.AwaitNewCharacter &&
                    (_triedClass == MirClass.Assassin || _triedClass == MirClass.Archer))
                {
                    if (_triedClass == MirClass.Assassin) _assassinAllowed = false; else _archerAllowed = false;
                    if (ClassToCreate() != _triedClass)
                    {
                        SendNewCharacter();
                        break;
                    }
                }
                Fail($"character creation refused (S.NewCharacter Result={nc.Result})");
                break;

            case S.NewCharacterSuccess ncs:
                Metrics.Inc(ref _m.CharactersCreated);
                SendStartGame(ncs.CharInfo.Index);
                break;

            case S.StartGame sg:
                if (sg.Result == 4) SetState(State.AwaitUserInfo, now);
                else Fail($"start game refused (S.StartGame Result={sg.Result}{(sg.Result == 0 ? ": set [Permission] AllowStartGame=True" : "")})");
                break;

            case S.StartGameBanned sgb:
                Fail($"character banned: {sgb.Reason}");
                break;

            case S.StartGameDelay sgd:
                Fail($"start game delayed {sgd.Milliseconds} ms");
                break;

            case S.NewItemInfo ni:
                World.ItemInfos[ni.Info.Index] = ni.Info;
                World.ResolveAllItems();
                foreach (var goods in _npcGoods) World.ResolveItem(goods);
                break;

            case S.BaseStatsInfo bs:
                World.ApplyBaseStats(bs.Stats);
                break;

            case S.NewMagic nm:
                if (!nm.Hero && nm.Magic != null) World.Magics[nm.Magic.Spell] = nm.Magic;
                break;

            case S.MagicLeveled ml:
                if (World.Magics.TryGetValue(ml.Spell, out var leveled))
                {
                    leveled.Level = ml.Level;
                    leveled.Experience = ml.Experience;
                }
                break;

            case S.MagicDelay md:
                if (md.ObjectID == World.ObjectId || md.ObjectID == 0)
                    SpellReadyAt[md.Spell] = now + Math.Max(0, md.Delay);
                break;

            case S.AddBuff ab:
                if (ab.Buff != null && (ab.Buff.ObjectID == World.ObjectId || ab.Buff.ObjectID == 0))
                    World.Buffs.Add(ab.Buff.Type);
                break;

            case S.RemoveBuff rb:
                if (rb.ObjectID == World.ObjectId || rb.ObjectID == 0) World.Buffs.Remove(rb.Type);
                break;

            case S.Struck struck:
                World.LastAttackerId = struck.AttackerID;
                World.LastAttackerAt = now;
                break;

            case S.ObjectStruck ost:
                if (ost.ObjectID == World.ObjectId)
                {
                    World.LastAttackerId = ost.AttackerID;
                    World.LastAttackerAt = now;
                }
                break;

            case S.GainedItem gained:
                World.AddItem(gained.Item);
                break;

            case S.MoveItem moved:
                if (moved.Success && moved.Grid == MirGridType.Inventory) World.MoveInventory(moved.From, moved.To);
                break;

            case S.EquipItem equipped:
                if (equipped.Success && equipped.Grid == MirGridType.Inventory) World.ApplyEquip(equipped.UniqueID, equipped.To);
                break;

            case S.DeleteItem deleted:
                World.RemoveItem(deleted.UniqueID, deleted.Count);
                break;

            case S.ItemRepaired repaired:
                {
                    var item = World.FindInventory(repaired.UniqueID);
                    if (item == null)
                        foreach (var slot in World.Equipment)
                            if (slot != null && slot.UniqueID == repaired.UniqueID) { item = slot; break; }
                    if (item != null)
                    {
                        item.MaxDura = repaired.MaxDura;
                        item.CurrentDura = repaired.CurrentDura;
                    }
                }
                break;

            case S.SellItem sold:
                if (_pendingSaleId == sold.UniqueID)
                {
                    if (sold.Success)
                    {
                        Metrics.Inc(ref _m.SellSuccess);
                        World.RemoveItem(sold.UniqueID, sold.Count);
                    }
                    else
                    {
                        Metrics.Inc(ref _m.SellFailure);
                        _saleBlockedId = sold.UniqueID;
                        _saleBlockedAt = now + 5000;
                    }
                    _pendingSaleId = 0;
                }
                break;

            case S.NPCGoods goodsList:
                Metrics.Inc(ref _m.NpcGoods);
                _npcGoods.Clear();
                if (goodsList.List != null)
                {
                    foreach (var goods in goodsList.List)
                    {
                        World.ResolveItem(goods);
                        _npcGoods.Add(goods);
                    }
                }
                NpcPanel = goodsList.Type;
                break;

            case S.NPCSell:
                Metrics.Inc(ref _m.NpcSellPages);
                break;

            case S.NPCResponse:
                Metrics.Inc(ref _m.NpcPages);
                break;

            // Quest system V2: offers come from S.QuestAvailability (per NPC in view) + S.QuestDefinition (on demand).
            case S.QuestDefinition qd:
                World.Quests.ApplyDefinition(qd);
                break;

            case S.QuestAvailability qa:
                World.Quests.ApplyAvailability(qa, World);
                break;

            case S.QuestStates qs:
                _questAccepted.Clear();
                _questTurnedIn.Clear();
                _questAbandoned.Clear();
                World.Quests.ApplyStates(qs, now, _questAccepted, _questTurnedIn, _questAbandoned);
                for (int i = 0; i < _questAccepted.Count; i++) Metrics.Inc(ref _m.QuestsAccepted);
                for (int i = 0; i < _questTurnedIn.Count; i++) Metrics.Inc(ref _m.QuestsFinished);
                for (int i = 0; i < _questAbandoned.Count; i++) Metrics.Inc(ref _m.QuestsAbandoned);
                break;

            case S.QuestProgress qp:
                World.Quests.ApplyProgress(qp, now);
                break;

            case S.QuestHistory qh:
                World.Quests.ApplyHistory(qh);
                break;

            case S.QuestOffer qo:
                World.Quests.Offers.Add(qo.Quest);
                break;

            case S.QuestRefused qr:
                World.Quests.ApplyRefused(qr, now);
                Metrics.Inc(ref _m.QuestsRefused);
                break;

            case S.GroupInvite:
                NextGroupReplyAt = now + 800;
                break;

            case S.AddMember:
                World.InGroup = true;
                Metrics.Inc(ref _m.GroupJoins);
                break;

            case S.DeleteMember member:
                if (string.Equals(member.Name, World.CharacterName, StringComparison.OrdinalIgnoreCase)) World.InGroup = false;
                break;

            case S.DeleteGroup:
                World.InGroup = false;
                break;

            case S.NewMonsterInfo nmi:
                World.MonsterInfo[nmi.Info.Name ?? "?"] = nmi.Info;
                World.Quests.NoteMonsterInfo(nmi.Info);
                _directory.MonsterInfo(nmi.Info);
                foreach (var mon in World.Monsters.Values)
                    if (string.Equals(mon.Name, nmi.Info.Name, StringComparison.OrdinalIgnoreCase)) mon.Level = nmi.Info.Level;
                break;

            case S.UserInformation ui:
                World.ObjectId = ui.ObjectID;
                World.CharacterName = ui.Name ?? string.Empty;
                World.Class = ui.Class;
                World.Level = ui.Level;
                World.Location = ui.Location;
                World.Direction = ui.Direction;
                World.HP = ui.HP;
                World.MP = ui.MP;
                World.Experience = ui.Experience;
                World.MaxExperience = ui.MaxExperience;
                World.Gold = ui.Gold;
                World.Dead = ui.HP <= 0;
                World.SetItems(ui.Inventory, ui.Equipment);
                World.RecomputeMaxVitals();
                if (_state == State.AwaitUserInfo) EnterGame(now);
                break;

            case S.MapChanged mc:
                World.MapIndex = mc.MapIndex;
                World.MapFileName = mc.FileName ?? string.Empty;
                World.MapTitle = mc.Title ?? string.Empty;
                World.MapChangedAt = now;
                World.Location = mc.Location;
                World.Direction = mc.Direction;
                World.ClearObjects();
                Nav.Clear();
                _moveOutstanding = false;
                _directory.MapChanged(mc.MapIndex, mc.FileName, mc.Title);
                if (World.Dead) { World.Dead = false; Metrics.Inc(ref _m.Revives); }
                RequestMapDetails(now, mc.MapIndex);
                break;

            case S.NewMapInfo nmiMap:
                if (nmiMap.Info != null)
                {
                    World.MapWidth = nmiMap.Info.Width;
                    World.MapHeight = nmiMap.Info.Height;
                    if (!string.IsNullOrEmpty(nmiMap.Info.Title)) World.MapTitle = nmiMap.Info.Title;
                    _directory.MapInfo(nmiMap.MapIndex, nmiMap.Info);
                }
                break;

            case S.MapDetails md:
                _directory.MapDetails(md.MapIndex, md.Part, md.Respawns, md.SafeZones);
                break;

            case S.Opendoor od:
                if (od.Close) World.OpenDoors.Remove(od.DoorIndex);
                else World.OpenDoors.Add(od.DoorIndex);
                break;

            case S.UserLocation loc:
                HandleMoveAck(loc, now);
                break;

            case S.ObjectMonster om:
                if (om.Dead || om.MasterObjectId != 0) break; // ignore corpses and pets
                if (!World.Monsters.ContainsKey(om.ObjectID)) Metrics.Inc(ref _m.MonstersSeen);
                World.AddMonster(om, now);
                break;

            case S.ObjectPlayer op:
                World.AddPlayer(new PlayerState
                {
                    Id = op.ObjectID,
                    Name = op.Name ?? string.Empty,
                    Level = op.Level,
                    Location = op.Location,
                    Direction = op.Direction,
                    Dead = op.Dead,
                });
                break;

            case S.ObjectNPC on:
                World.AddNpc(new NpcState { Id = on.ObjectID, Name = on.Name ?? string.Empty, Location = on.Location });
                break;

            case S.ObjectItem oi:
                World.Items[oi.ObjectID] = new GroundItemState { Id = oi.ObjectID, Name = oi.Name ?? string.Empty, Location = oi.Location };
                break;

            case S.ObjectGold og:
                World.GoldPiles[og.ObjectID] = new GroundGoldState { Id = og.ObjectID, Gold = og.Gold, Location = og.Location };
                break;

            case S.ObjectWalk ow: World.Track(ow.ObjectID, ow.Location, ow.Direction); break;
            case S.ObjectRun orn: World.Track(orn.ObjectID, orn.Location, orn.Direction); break;
            case S.ObjectTurn ot: World.Track(ot.ObjectID, ot.Location, ot.Direction); break;
            case S.ObjectMoveBatch mb: // P9-08: server-side batch of turn/walk/run, same effect as the single packets
                for (int i = 0; i < mb.Moves.Count; i++) World.Track(mb.Moves[i].ObjectID, mb.Moves[i].Location, mb.Moves[i].Direction);
                break;

            case S.ObjectHealth oh:
                if (World.Monsters.TryGetValue(oh.ObjectID, out var hurt)) hurt.HPPercent = oh.Percent;
                break;

            case S.ObjectDied od2:
                bool killed = od2.ObjectID == _lastAttackedId;
                World.Remove(od2.ObjectID);
                if (killed)
                {
                    Metrics.Inc(ref _m.Kills);
                    _lastAttackedId = 0;
                }
                break;

            case S.ObjectRemove orm:
                World.Remove(orm.ObjectID);
                break;

            case S.HealthChanged hc:
                World.HP = hc.HP;
                World.MP = hc.MP;
                break;

            case S.GainedGold gg:
                World.Gold += gg.Gold;
                break;

            case S.LoseGold lg:
                World.Gold = lg.Gold > World.Gold ? 0 : World.Gold - lg.Gold;
                break;

            case S.GainExperience ge:
                World.Experience += ge.Amount;
                break;

            case S.LevelChanged lc:
                World.Level = lc.Level;
                World.Experience = lc.Experience;
                World.MaxExperience = lc.MaxExperience;
                World.RecomputeMaxVitals();
                break;

            case S.Death d:
                if (!World.Dead) Metrics.Inc(ref _m.Deaths);
                World.Dead = true;
                _deadSince = now;
                _reviveSentAt = 0;
                World.Location = d.Location;
                _moveOutstanding = false;
                Nav.Clear();
                break;

            case S.Revived:
                if (World.Dead) { World.Dead = false; Metrics.Inc(ref _m.Revives); }
                break;

            case S.Disconnect dc:
                _socket.Close($"server sent Disconnect (Reason={dc.Reason})");
                break;
        }
    }

    private void RequestMapDetails(long now, int mapIndex)
    {
        if (mapIndex < 0 || _detailsMapIndex == mapIndex || now - _detailsRequestedAt < 1000) return;
        _detailsMapIndex = mapIndex;
        _detailsRequestedAt = now;
        Send(new C.RequestMapDetails { MapIndex = mapIndex });
    }

    private void HandleMoveAck(S.UserLocation loc, long now)
    {
        if (_moveOutstanding)
        {
            _moveOutstanding = false;

            if (loc.Location == _moveFrom)
            {
                // Refused move: the server echoes the pre-move position. Remember the cell briefly so the next
                // path goes around whatever is standing there.
                Metrics.Inc(ref _m.Blocked);
                Metrics.Inc(ref _m.Repaths);
                _lastMoveBlocked = true;
                _avoid[AvoidKey(_moveExpected)] = now + 5000;
                Nav.Clear();
            }
            else
            {
                _lastMoveBlocked = false;
                Nav.Advance(_advanceOnAck);
                if (loc.Location != _moveExpected)
                {
                    // Position desync (blocked run, knock-back, ...): drop the path and let the brain repath.
                    Metrics.Inc(ref _m.Repaths);
                    Nav.Clear();
                }
            }
        }

        World.Location = loc.Location;
        World.Direction = loc.Direction;
    }

    private void SendLogin()
    {
        SetState(State.AwaitLogin, Now);
        Send(new C.Login { AccountID = _account, Password = _o.Password });
    }

    private void SendStartGame(int characterIndex)
    {
        SetState(State.AwaitStartGame, Now);
        Send(new C.StartGame { CharacterIndex = characterIndex });
    }

    private void EnterGame(long now)
    {
        SetState(State.InGame, now);
        World.Dead = false;
        _m.EnterGame();
        _inGameCounted = true;
        _reachedThisSession = true;
        Metrics.Inc(ref _m.GamesEntered);
        if (!_reachedGameOnce)
        {
            _reachedGameOnce = true;
            Metrics.Inc(ref _m.BotsReachedGame);
        }
        ReleaseBudget();
        _nextActionAt = now + 500 + _rng.Next(1000); // spread the fleet's first moves
    }

    private void Fail(string why)
    {
        Metrics.Inc(ref _m.Errors);
        Log(why);
        _socket?.Close(why);
    }

    /// <summary>A refusal that a retry would only repeat (wrong password, ban): stop this bot for the rest of the run.
    /// Retrying a wrong password would re-ban the account every few attempts (Envir: 6 wrong passwords = 2 min ban).</summary>
    private void GiveUp(string why)
    {
        _done = true;
        Fail(why + " - this bot stops");
    }

    private void Log(string message)
    {
        if (!_o.Quiet) Console.WriteLine($"  bot {_index} ({_account}): {message}");
    }

    // ================================================================== movement
    internal void Attack(MirDirection direction, uint targetId, long now)
    {
        Send(new C.Attack { Direction = direction, Spell = Spell.None });
        Metrics.Inc(ref _m.Attacks);
        _lastAttackedId = targetId;
        _nextActionAt = now + _o.AttackDelayMs;
    }

    internal void Turn(MirDirection direction, long now)
    {
        Send(new C.Turn { Direction = direction });
        Metrics.Inc(ref _m.Turns);
        _nextActionAt = now + _o.MoveDelayMs;
    }

    /// <summary>One walk step, used by the greedy fallback (no map data).</summary>
    internal void WalkGreedy(MirDirection direction, long now)
    {
        Send(new C.Walk { Direction = direction });
        Metrics.Inc(ref _m.Walks);
        _moveFrom = World.Location;
        _moveExpected = Functions.PointMove(World.Location, direction, 1);
        _advanceOnAck = 1;
        _moveOutstanding = true;
        _moveSentAt = now;
        _lastMoveBlocked = false;
        _nextActionAt = now + _o.MoveDelayMs;
    }

    /// <summary>Walks (or runs) one step along the current path. Returns true when a move or a door request was sent.</summary>
    internal bool MoveAlongPath(long now)
    {
        if (_moveOutstanding || !Nav.HasPath || now < _nextActionAt) return false;

        Point next = Nav.Next;
        Point location = World.Location;
        if (Functions.MaxDistance(location, next) != 1)
        {
            Metrics.Inc(ref _m.Repaths);
            Nav.Clear();
            return false;
        }

        MapGrid grid = Map;
        byte door = grid?.DoorAt(next.X, next.Y) ?? 0;
        if (door != 0 && !World.OpenDoors.Contains(door))
        {
            if (now >= _nextDoorAt)
            {
                _nextDoorAt = now + 500;
                Send(new C.Opendoor { DoorIndex = door });
                Metrics.Inc(ref _m.DoorsOpened);
            }
            return true;
        }

        MirDirection direction = Functions.DirectionFromPoint(location, next);
        bool run = false;

        if (_o.AllowRun && _runStreak < 8 && Nav.CellsAhead(2) && _rng.Next(100) < _o.RunPercent)
        {
            Point second = Nav.CellAt(1);
            run = Functions.MaxDistance(next, second) == 1
                  && Functions.DirectionFromPoint(next, second) == direction
                  && (grid == null || (!grid.IsBlocked(second.X, second.Y) && grid.DoorAt(second.X, second.Y) == 0));
        }

        if (run)
        {
            Send(new C.Run { Direction = direction });
            Metrics.Inc(ref _m.Runs);
            _runStreak++;
            _moveExpected = Functions.PointMove(location, direction, 2);
            _advanceOnAck = 2;
        }
        else
        {
            Send(new C.Walk { Direction = direction });
            Metrics.Inc(ref _m.Walks);
            _runStreak = 0;
            _moveExpected = Functions.PointMove(location, direction, 1);
            _advanceOnAck = 1;
        }

        _moveFrom = location;
        _moveOutstanding = true;
        _moveSentAt = now;
        _lastMoveBlocked = false;
        _nextActionAt = now + _o.MoveDelayMs;
        return true;
    }

    /// <summary>A* over the current map plus dynamic objects and recently refused cells.</summary>
    internal List<Point> FindPath(Point goal, uint ignoreMonster, long now)
    {
        MapGrid grid = Map;
        if (grid == null) return null;

        if (_avoid.Count > 64)
        {
            foreach (var key in _avoid.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) _avoid.Remove(key);
        }

        return PathFinder.FindPath(grid.Width, grid.Height,
            (x, y) => grid.IsBlocked(x, y) || World.IsBlockedByObject(x, y, ignoreMonster) || IsAvoided(x, y, now),
            World.Location, goal, _o.PathNodes);
    }

    private bool IsAvoided(int x, int y, long now) =>
        _avoid.TryGetValue(AvoidKey(new Point(x, y)), out long until) && until > now;

    private int AvoidKey(Point p) => (p.Y << 16) ^ (p.X & 0xFFFF);

    // ================================================================== survival, items, NPC, social

    /// <summary>Drinks an HP potion when below 55 % HP. Returns true when a C.UseItem went out.</summary>
    internal bool TryDrinkPotion(long now)
    {
        if (World.Dead || now < NextPotionAt) return false;

        int max = World.MaxHP > 0 ? World.MaxHP : 100;
        if (World.HP * 100 >= max * 55) return false;
        if (!World.HasPotion(out var potion)) return false;

        Send(new C.UseItem { UniqueID = potion.UniqueID, Grid = MirGridType.Inventory });
        NextPotionAt = now + 1500;
        _nextActionAt = now + 900;
        Metrics.Inc(ref _m.Potions);
        return true;
    }

    /// <summary>Casts the cheapest learned offensive spell that can reach the target, if MP and cooldown allow.</summary>
    internal bool TryCastAt(MonsterState target, long now)
    {
        if (World.Dead || target == null || World.Magics.Count == 0 || now < NextMagicAt) return false;

        int distance = Functions.MaxDistance(World.Location, target.Location);
        ClientMagic chosen = null;
        int chosenCost = int.MaxValue;

        foreach (var magic in World.Magics.Values)
        {
            if (magic == null || magic.Spell == Spell.None) continue;
            if (magic.Spell is Spell.Healing or Spell.MassHealing or Spell.Hiding or Spell.ProtectionField or Spell.MagicShield) continue;
            if (SpellReadyAt.TryGetValue(magic.Spell, out long ready) && ready > now) continue;

            int range = Math.Max(1, (int)magic.Range);
            if (distance > range) continue;

            int cost = magic.BaseCost + magic.LevelCost * magic.Level;
            if (World.MaxMP > 0 && World.MP < cost) continue;
            if (cost >= chosenCost) continue;

            chosen = magic;
            chosenCost = cost;
        }

        if (chosen == null) return false;

        Send(new C.Magic
        {
            Spell = chosen.Spell,
            Direction = Functions.DirectionFromPoint(World.Location, target.Location),
            TargetID = target.Id,
            Location = target.Location,
        });

        long cooldown = Math.Max(1200, chosen.Delay);
        SpellReadyAt[chosen.Spell] = now + cooldown;
        NextMagicAt = now + cooldown;
        _nextActionAt = now + cooldown;
        Metrics.Inc(ref _m.Casts);
        return true;
    }

    /// <summary>Equips a bag weapon/armour that beats what is worn. Returns true when a C.EquipItem went out.</summary>
    internal bool TryEquipBetter(long now)
    {
        if (World.Dead) return false;

        foreach (var item in World.Inventory)
        {
            var info = World.ResolveItem(item);
            if (info == null) continue;

            EquipmentSlot slot;
            if (info.Type == ItemType.Weapon) slot = EquipmentSlot.Weapon;
            else if (info.Type == ItemType.Armour) slot = EquipmentSlot.Armour;
            else continue;

            var worn = World.Equipped(slot);
            if (worn != null && WorldModel.GearScore(worn) >= WorldModel.GearScore(item)) continue;

            if (_lastEquipId == item.UniqueID && now < _nextEquipAt) continue;
            _lastEquipId = item.UniqueID;
            _nextEquipAt = now + 5000;

            Send(new C.EquipItem { Grid = MirGridType.Inventory, UniqueID = item.UniqueID, To = (int)slot });
            Metrics.Inc(ref _m.Equips);
            _nextActionAt = now + 700;
            return true;
        }

        return false;
    }

    /// <summary>C.PickUp has no fields: the server takes the item pile under the player's feet.</summary>
    internal void PickUpItem(long now)
    {
        Send(new C.PickUp());
        NextPickupAt = now + 700;
        _nextActionAt = now + 700;
        Metrics.Inc(ref _m.Pickups);
    }

    internal void CallNpc(uint objectId, string key, long now)
    {
        // The server only opens the dialog for [@MAIN]; every other key must be a button of the current page.
        if (string.IsNullOrEmpty(key)) key = "[@MAIN]";

        Send(new C.CallNPC { ObjectID = objectId, Key = key });
        Metrics.Inc(ref _m.NpcCalls);
        if (string.Equals(key, "[@MAIN]", StringComparison.OrdinalIgnoreCase))
            _npcGoods.Clear();
        _nextActionAt = now + 500;
    }

    internal bool Sell(UserItem item, long now)
    {
        if (item == null || item.Count == 0) return false;

        // One sale at a time: the server answers with S.SellItem and removes the item from the bag itself.
        if (_pendingSaleId != 0 && now - _pendingSaleAt < 2000) return false;
        if (now < _saleBlockedAt && item.UniqueID == _saleBlockedId) return false;

        Send(new C.SellItem { UniqueID = item.UniqueID, Count = item.Count });
        _pendingSaleId = item.UniqueID;
        _pendingSaleAt = now;
        Metrics.Inc(ref _m.Sold);
        _nextActionAt = now + 400;
        return true;
    }

    internal bool Buy(UserItem goods, ushort count, long now)
    {
        if (goods == null || count == 0) return false;
        Send(new C.BuyItem { ItemIndex = goods.UniqueID, Count = count, Type = NpcPanel });
        Metrics.Inc(ref _m.Bought);
        _nextActionAt = now + 400;
        return true;
    }

    internal bool Repair(UserItem item, long now)
    {
        if (item == null) return false;
        Send(new C.RepairItem { UniqueID = item.UniqueID });
        Metrics.Inc(ref _m.Repairs);
        _nextActionAt = now + 400;
        return true;
    }

    /// <summary>C.QuestAccept at <paramref name="npcObjectId"/> (0 = a pending S.QuestOffer). Counted when S.QuestStates confirms it.</summary>
    internal void AcceptQuest(int quest, uint npcObjectId, long now)
    {
        World.Quests.PendingAccepts[quest] = now;
        World.Quests.Offers.Remove(quest);
        Send(new C.QuestAccept { Quest = quest, NpcObjectID = npcObjectId });
        Metrics.Inc(ref _m.QuestAcceptSent);
        _nextActionAt = now + 600;
    }

    /// <summary>C.QuestTurnIn with one reward pick per choice group. Counted as done when S.QuestStates removes the run.</summary>
    internal void TurnInQuest(int quest, uint npcObjectId, long now)
    {
        World.Quests.Definitions.TryGetValue(quest, out var def);
        World.Quests.PendingTurnIns[quest] = now;
        Send(new C.QuestTurnIn { Quest = quest, NpcObjectID = npcObjectId, RewardPicks = QuestLogic.RewardPicks(def) });
        Metrics.Inc(ref _m.QuestFinishSent);
        _nextActionAt = now + 600;
    }

    internal void AbandonQuest(int quest, long now)
    {
        if (World.Quests.Runs.TryGetValue(quest, out var run)) run.AbandonSent = true;
        World.Quests.PendingAbandons[quest] = now;
        Send(new C.QuestAbandon { Quest = quest });
        _nextActionAt = now + 400;
    }

    internal void SendChat(string text, long now)
    {
        Send(new C.Chat { Message = text, LinkedItems = new List<ChatItem>() });
        Metrics.Inc(ref _m.Chats);
        _nextActionAt = now + 500;
    }

    /// <summary>The configured class, or Warrior/Wizard/Taoist (by index) when the server disables Assassin/Archer.</summary>
    private MirClass ClassToCreate()
    {
        var c = _o.ClassFor(_index);
        if (c == MirClass.Assassin && !_assassinAllowed || c == MirClass.Archer && !_archerAllowed)
            c = _o.Class == null ? (MirClass)(_index % 3) : MirClass.Warrior;
        return c;
    }

    private void SendNewCharacter()
    {
        _triedClass = ClassToCreate();
        Send(new C.NewCharacter { Name = _character, Class = _triedClass, Gender = (_index & 1) == 0 ? MirGender.Male : MirGender.Female });
    }

    internal void InvitePlayer(string name, long now)
    {
        if (string.IsNullOrEmpty(name)) return;
        Send(new C.AddMember { Name = name });
        Metrics.Inc(ref _m.Invites);
        _nextActionAt = now + 800;
    }

    internal void ReplyGroup(bool accept, long now)
    {
        Send(new C.GroupInvite { AcceptInvite = accept });
        _nextActionAt = now + 500;
    }

    /// <summary>Finds the NPC goods entry for a potion (or null). Goods carry the shop UniqueID used by C.BuyItem.</summary>
    internal UserItem FindGoods(Func<ItemInfo, bool> predicate)
    {
        foreach (var goods in _npcGoods)
        {
            var info = World.ResolveItem(goods);
            if (info != null && predicate(info)) return goods;
        }
        return null;
    }

    // ================================================================== director

    internal void AssignDirective(Directive directive) => _directive = directive;

    internal Directive TakeDirective() => Interlocked.Exchange(ref _directive, null);

    internal void RequestDirective()
    {
        _director?.Request(new IdleRequest
        {
            Bot = this,
            MapIndex = World.MapIndex,
            MapFileName = World.MapFileName,
            Location = World.Location,
            QuestMonsters = _o.Quests ? World.Quests.TargetMonsterIndexes : Array.Empty<int>(),
            At = Now,
        });
    }
}

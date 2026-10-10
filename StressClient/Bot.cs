
using System.Drawing;
using C = ClientPackets;
using S = ServerPackets;

namespace StressClient;

/// <summary>
/// One headless player. Runs its own async loop: connect, then every tick drain received packets, think, and flush
/// queued packets through the token bucket. The login sequence mirrors the real client
/// (Client/MirScenes/LoginScene.cs, SelectScene.cs):
///   S.Connected → C.ClientVersion → S.ClientVersion(1) → C.Login
///     → S.Login(3: no account) → C.NewAccount → S.NewAccount(8 or 7) → C.Login
///     → S.LoginSuccess(chars) → [C.NewCharacter → S.NewCharacterSuccess] → C.StartGame
///     → S.StartGame(4) + S.MapInformation + S.UserInformation → in game.
/// In game it roams (C.Walk, one move outstanding at a time and never faster than the server's MoveDelay, so
/// MirConnection's retry queue stays empty) and attacks monsters it sees (S.ObjectMonster → walk next to it →
/// C.Attack facing it). It answers death with C.TownRevive.
/// </summary>
internal sealed class Bot
{
    private enum State { Idle, Connecting, AwaitConnected, AwaitVersion, AwaitLogin, AwaitNewAccount, AwaitSelect, AwaitNewCharacter, AwaitStartGame, AwaitUserInfo, InGame, Closed }

    private sealed class Monster
    {
        public Point Location;
    }

    private static long Now => MonoClock.Now;

    private readonly int _index;
    private readonly Options _o;
    private readonly Metrics _m;
    private readonly SemaphoreSlim _connectBudget;
    private readonly Random _rng;
    private readonly string _account, _character;
    private readonly List<Packet> _outbox = new();
    private readonly Dictionary<uint, Monster> _monsters = new();
    private readonly Dictionary<int, ItemInfo> _itemInfos = new();   // from S.NewItemInfo
    private readonly List<UserItem> _unequipped = new();             // start items still in the bag

    private BotSocket _socket;
    private TokenBucket _bucket;
    private State _state = State.Idle;
    private bool _holdingBudget, _reachedGameOnce, _accountCreateTried, _countedConnected, _inGameCounted;
    private long _stateSince, _lastKeepAlive;

    // in-game state
    private uint _objectId;
    private Point _location;
    private bool _dead, _moveOutstanding, _lastMoveBlocked;
    private long _moveSentAt, _nextActionAt, _deadSince, _reviveSentAt, _ignoreTargetUntil;
    private Point _locationBeforeMove;
    private MirDirection _heading;
    private int _stepsLeft;
    private uint _targetId, _lastAttackedId;

    public bool ReachedGame => _reachedGameOnce;

    public Bot(int index, Options options, Metrics metrics, SemaphoreSlim connectBudget)
    {
        _index = index;
        _o = options;
        _m = metrics;
        _connectBudget = connectBudget;
        _rng = new Random(options.Seed + index);
        _account = $"{options.Prefix}{index:D5}";
        _character = $"{options.Prefix}c{index:D5}";
        _heading = (MirDirection)_rng.Next(8);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        int failures = 0;
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            if (!first) Metrics.Inc(ref _m.Reconnects);
            first = false;

            bool reached = await RunSessionAsync(ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) break;
            if (!_o.Reconnect && _reachedGameOnce) break;

            failures = reached ? 0 : failures + 1;
            int backoffMs = Math.Min(30_000, 1000 * (1 << Math.Min(failures, 5))) + _rng.Next(500);
            try { await Task.Delay(backoffMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One connection from connect to close. Returns true if it reached the game.</summary>
    private async Task<bool> RunSessionAsync(CancellationToken ct)
    {
        ResetSession();
        bool reachedThisSession = false;

        try
        {
            await _connectBudget.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return false; }
        _holdingBudget = true;

        _socket = new BotSocket(_m);
        _bucket = new TokenBucket(_o.ServerMaxPacket, _o.PacketBudgetFraction, Now);
        SetState(State.Connecting);
        Interlocked.Increment(ref _m.Connecting);
        Metrics.Inc(ref _m.ConnectAttempts);

        var result = await _socket.ConnectAsync(_o.Host, _o.Port, 5000, ct).ConfigureAwait(false);
        if (result != BotSocket.ConnectResult.Ok)
        {
            Interlocked.Decrement(ref _m.Connecting);
            if (result == BotSocket.ConnectResult.Refused) Metrics.Inc(ref _m.Refused);
            else if (!ct.IsCancellationRequested) Metrics.Inc(ref _m.Errors);
            ReleaseBudget();
            SetState(State.Closed);
            return false;
        }

        SetState(State.AwaitConnected);
        _socket.StartReceiving(ct);
        _lastKeepAlive = Now;

        try
        {
            while (!ct.IsCancellationRequested && !_socket.IsClosed)
            {
                while (_socket.Received.TryDequeue(out var p))
                {
                    Handle(p);
                    if (_socket.IsClosed) break;
                }
                if (_state == State.InGame) reachedThisSession = true;

                Think();
                await FlushAsync(ct).ConfigureAwait(false);
                await Task.Delay(_o.TickMs, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* duration over */ }

        // ---- teardown ----
        if (ct.IsCancellationRequested && !_socket.IsClosed)
        {
            // Polite exit so the server logs the player out at once instead of waiting for its 10 s timeout.
            try
            {
                using var bye = new CancellationTokenSource(1000);
                if (await _socket.SendAsync(new List<Packet> { new C.Disconnect() }, bye.Token).ConfigureAwait(false))
                    await Task.Delay(300).ConfigureAwait(false); // let the server process it before the socket closes
            }
            catch { /* best effort */ }
        }
        else if (!ct.IsCancellationRequested)
        {
            // The server closed us (or we gave up on a timeout/protocol error).
            if (_state == State.AwaitConnected) Metrics.Inc(ref _m.Refused); // closed before S.Connected: MaxUser, IP block, ...
            else Metrics.Inc(ref _m.Disconnects);
            if (!_o.Quiet && _state != State.AwaitConnected)
                Log($"disconnected in {_state}: {_socket.CloseReason}");
        }

        _socket.Close(ct.IsCancellationRequested ? "duration over" : _socket.CloseReason ?? "closed");
        if (_state == State.AwaitConnected) Interlocked.Decrement(ref _m.Connecting);
        if (_countedConnected) Interlocked.Decrement(ref _m.Connected);
        if (_inGameCounted) _m.LeaveGame();
        ReleaseBudget();
        SetState(State.Closed);
        return reachedThisSession;
    }

    private void ResetSession()
    {
        _outbox.Clear();
        _monsters.Clear();
        _unequipped.Clear();
        _countedConnected = _inGameCounted = false;
        _accountCreateTried = false;
        _dead = _moveOutstanding = _lastMoveBlocked = false;
        _targetId = _lastAttackedId = 0;
        _nextActionAt = 0;
        _stepsLeft = 0;
    }

    private void SetState(State s)
    {
        _state = s;
        _stateSince = Now;
    }

    private void ReleaseBudget()
    {
        if (!_holdingBudget) return;
        _holdingBudget = false;
        _connectBudget.Release();
    }

    private void Send(Packet p) => _outbox.Add(p);

    private async Task FlushAsync(CancellationToken ct)
    {
        long now = Now;
        // KeepAlive: the server drops a connection after Settings.TimeOut (10 s) without a packet; the client sends one
        // whenever it has been quiet. Sending one every 5 s also gives an RTT sample (the server echoes Time).
        if (now - _lastKeepAlive >= 5000)
        {
            _lastKeepAlive = now;
            Send(new C.KeepAlive { Time = now });
        }

        if (_outbox.Count == 0) return;
        if (!_bucket.TryTake(now))
        {
            Metrics.Inc(ref _m.Throttled);
            return; // keep the packets; the next tick tries again
        }

        var batch = new List<Packet>(_outbox);
        _outbox.Clear();
        await _socket.SendAsync(batch, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ packets

    private void Handle(Packet p)
    {
        switch (p)
        {
            case S.Connected:
                if (_state != State.AwaitConnected) break;
                Interlocked.Decrement(ref _m.Connecting);
                Interlocked.Increment(ref _m.Connected);
                _countedConnected = true;
                SetState(State.AwaitVersion);
                // CheckVersion is off on scratch servers; the hash content does not matter then (the codec needs a non-null array).
                Send(new C.ClientVersion { VersionHash = new byte[16] });
                break;

            case S.MapInformation mi:
                _m.MapEntries.AddOrUpdate(mi.FileName ?? "?", 1, (_, n) => n + 1);
                break;

            case S.ClientVersion v:
                if (v.Result != 1) { Fail("wrong client version (set [General] CheckVersion=False on the scratch server)"); break; }
                SendLogin();
                break;

            case S.Login l:
                if (l.Result == 3 && !_accountCreateTried)
                {
                    _accountCreateTried = true;
                    SetState(State.AwaitNewAccount);
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
                else Fail($"login refused (S.Login Result={l.Result}{(l.Result == 4 ? ": wrong password" : "")})");
                break;

            case S.LoginBanned b:
                Fail($"login banned until {b.ExpiryDate:u}: {b.Reason}");
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
                    SetState(State.AwaitNewCharacter);
                    Send(new C.NewCharacter { Name = _character, Class = MirClass.Warrior, Gender = (_index & 1) == 0 ? MirGender.Male : MirGender.Female });
                }
                break;

            case S.NewCharacter nc:
                Fail($"character creation refused (S.NewCharacter Result={nc.Result})");
                break;

            case S.NewCharacterSuccess ncs:
                Metrics.Inc(ref _m.CharactersCreated);
                SendStartGame(ncs.CharInfo.Index);
                break;

            case S.StartGame sg:
                if (sg.Result == 4) SetState(State.AwaitUserInfo);
                else Fail($"start game refused (S.StartGame Result={sg.Result}{(sg.Result == 0 ? ": set [Permission] AllowStartGame=True" : "")})");
                break;

            case S.StartGameBanned sgb:
                Fail($"character banned: {sgb.Reason}");
                break;

            case S.StartGameDelay sgd:
                Fail($"start game delayed {sgd.Milliseconds} ms");
                break;

            case S.NewItemInfo ni:
                _itemInfos[ni.Info.Index] = ni.Info;
                break;

            case S.UserInformation ui:
                _objectId = ui.ObjectID;
                _location = ui.Location;
                _dead = ui.HP <= 0;
                _unequipped.Clear();
                // A level-1 character has 0 DC: without the start weapon its attacks do no damage, so equip it.
                if (ui.Inventory != null && ui.Equipment != null && ui.Equipment.Length > (int)EquipmentSlot.Armour &&
                    (ui.Equipment[(int)EquipmentSlot.Weapon] == null || ui.Equipment[(int)EquipmentSlot.Armour] == null))
                    foreach (var item in ui.Inventory)
                        if (item != null) _unequipped.Add(item);
                if (_state == State.AwaitUserInfo) EnterGame();
                break;

            case S.MapChanged mc:
                _location = mc.Location;
                _monsters.Clear();
                _moveOutstanding = false;
                if (_dead) { _dead = false; Metrics.Inc(ref _m.Revives); }
                break;

            case S.UserLocation loc:
                if (_moveOutstanding)
                {
                    _moveOutstanding = false;
                    _lastMoveBlocked = loc.Location == _locationBeforeMove;
                    if (_lastMoveBlocked) Metrics.Inc(ref _m.Blocked);
                }
                _location = loc.Location;
                break;

            case S.ObjectMonster om:
                if (om.Dead || om.MasterObjectId != 0) break; // ignore corpses and pets
                if (_monsters.TryAdd(om.ObjectID, new Monster { Location = om.Location })) Metrics.Inc(ref _m.MonstersSeen);
                else _monsters[om.ObjectID].Location = om.Location;
                break;

            case S.ObjectWalk ow: Track(ow.ObjectID, ow.Location); break;
            case S.ObjectRun orn: Track(orn.ObjectID, orn.Location); break;
            case S.ObjectTurn ot: Track(ot.ObjectID, ot.Location); break;

            case S.ObjectDied od:
                if (_monsters.Remove(od.ObjectID) && od.ObjectID == _lastAttackedId) Metrics.Inc(ref _m.Kills);
                if (od.ObjectID == _targetId) _targetId = 0;
                break;

            case S.ObjectRemove orm:
                _monsters.Remove(orm.ObjectID);
                if (orm.ObjectID == _targetId) _targetId = 0;
                break;

            case S.Death d:
                if (!_dead) Metrics.Inc(ref _m.Deaths);
                _dead = true;
                _deadSince = Now;
                _reviveSentAt = 0;
                _location = d.Location;
                _moveOutstanding = false;
                break;

            case S.Revived:
                if (_dead) { _dead = false; Metrics.Inc(ref _m.Revives); }
                break;


            case S.Disconnect dc:
                _socket.Close($"server sent Disconnect (Reason={dc.Reason})");
                break;
        }
    }

    private void Track(uint id, Point location)
    {
        if (_monsters.TryGetValue(id, out var mon)) mon.Location = location;
    }

    private void SendLogin()
    {
        SetState(State.AwaitLogin);
        Send(new C.Login { AccountID = _account, Password = _o.Password });
    }

    private void SendStartGame(int characterIndex)
    {
        SetState(State.AwaitStartGame);
        Send(new C.StartGame { CharacterIndex = characterIndex });
    }

    private void EnterGame()
    {
        SetState(State.InGame);
        _m.EnterGame();
        _inGameCounted = true;
        Metrics.Inc(ref _m.GamesEntered);
        if (!_reachedGameOnce)
        {
            _reachedGameOnce = true;
            Metrics.Inc(ref _m.BotsReachedGame);
        }
        ReleaseBudget();
        _nextActionAt = Now + 500 + _rng.Next(1000); // spread the fleet's first moves
    }

    private void Fail(string why)
    {
        Metrics.Inc(ref _m.Errors);
        Log(why);
        _socket.Close(why);
    }

    private void Log(string message)
    {
        if (!_o.Quiet) Console.WriteLine($"  bot {_index} ({_account}): {message}");
    }

    // ------------------------------------------------------------------ brain

    private void Think()
    {
        long now = Now;

        if (_state != State.InGame)
        {
            if (_state is > State.Connecting and < State.InGame && now - _stateSince > _o.LoginTimeoutSeconds * 1000L)
            {
                Metrics.Inc(ref _m.LoginTimeouts);
                Log($"timed out in {_state}");
                _socket.Close($"timeout in {_state}");
            }
            return;
        }

        if (_dead)
        {
            // Like a player pressing "town revive" after a moment; resend if nothing came back.
            if (now - _deadSince > 2000 && (_reviveSentAt == 0 || now - _reviveSentAt > 10_000))
            {
                _reviveSentAt = now;
                Send(new C.TownRevive());
            }
            return;
        }

        if (_unequipped.Count > 0 && TryEquip()) return;

        if (_moveOutstanding)
        {
            if (now - _moveSentAt < 3000) return; // wait for S.UserLocation: never more than one move in flight
            _moveOutstanding = false;               // lost reply: give up waiting
        }
        if (now < _nextActionAt) return;

        var target = PickTarget(now);
        if (target != null)
        {
            var (id, mon) = target.Value;
            int distance = Functions.MaxDistance(_location, mon.Location);
            var dir = Functions.DirectionFromPoint(_location, mon.Location);
            if (distance <= 1)
            {
                Send(new C.Attack { Direction = dir, Spell = Spell.None });
                Metrics.Inc(ref _m.Attacks);
                _lastAttackedId = id;
                _nextActionAt = now + _o.AttackDelayMs;
                return;
            }

            if (_lastMoveBlocked && _targetId == id)
            {
                // Path blocked (another bot or monster in the way): forget this target for a while and roam.
                _ignoreTargetUntil = now + 3000;
                _targetId = 0;
            }
            else
            {
                Walk(dir, now);
                return;
            }
        }

        Roam(now);
    }

    /// <summary>Equips one start weapon or armour per call once its S.NewItemInfo is known; drops other items from
    /// the list. Returns true if it sent a C.EquipItem.</summary>
    private bool TryEquip()
    {
        for (int i = _unequipped.Count - 1; i >= 0; i--)
        {
            var item = _unequipped[i];
            if (!_itemInfos.TryGetValue(item.ItemIndex, out var info)) continue; // info not received yet: retry next tick
            _unequipped.RemoveAt(i);
            if (info.Type != ItemType.Weapon && info.Type != ItemType.Armour) continue;
            var slot = info.Type == ItemType.Weapon ? EquipmentSlot.Weapon : EquipmentSlot.Armour;
            Send(new C.EquipItem { Grid = MirGridType.Inventory, UniqueID = item.UniqueID, To = (int)slot });
            Metrics.Inc(ref _m.Equips);
            return true;
        }
        return false;
    }

    private (uint id, Monster mon)? PickTarget(long now)
    {
        if (now < _ignoreTargetUntil) return null;

        if (_targetId != 0 && _monsters.TryGetValue(_targetId, out var current) &&
            Functions.MaxDistance(_location, current.Location) <= _o.SeekRange)
            return (_targetId, current);

        uint bestId = 0;
        Monster best = null;
        int bestDistance = int.MaxValue;
        foreach (var (id, mon) in _monsters)
        {
            int d = Functions.MaxDistance(_location, mon.Location);
            if (d > _o.SeekRange || d >= bestDistance) continue;
            bestDistance = d;
            bestId = id;
            best = mon;
        }
        _targetId = bestId;
        return best == null ? null : (bestId, best);
    }

    private void Roam(long now)
    {
        if (_stepsLeft <= 0 || _lastMoveBlocked)
        {
            var old = _heading;
            _heading = (MirDirection)_rng.Next(8);
            _stepsLeft = 3 + _rng.Next(10);
            if (_lastMoveBlocked || _heading != old)
            {
                // Face the new heading first, as a player would after bumping into something.
                Send(new C.Turn { Direction = _heading });
                Metrics.Inc(ref _m.Turns);
                _lastMoveBlocked = false;
                _nextActionAt = now + _o.MoveDelayMs;
                return;
            }
        }
        _stepsLeft--;
        Walk(_heading, now);
    }

    private void Walk(MirDirection dir, long now)
    {
        Send(new C.Walk { Direction = dir });
        Metrics.Inc(ref _m.Walks);
        _locationBeforeMove = _location;
        _moveOutstanding = true;
        _moveSentAt = now;
        _lastMoveBlocked = false;
        _nextActionAt = now + _o.MoveDelayMs;
    }
}

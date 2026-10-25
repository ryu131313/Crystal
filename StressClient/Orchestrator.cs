using System.Diagnostics;

namespace StressClient;

/// <summary>
/// Owns the bot population and the fleet's threads:
///   - the ramp releases bots at --ramp per second (reconnects go straight to the connect budget),
///   - worker threads sweep a partition of the population every --tick (blocking behaviour ticks),
///   - the reporter prints the 1 Hz metrics line / CSV row,
///   - the Director routes idle bots toward respawn regions and known map exits.
/// Socket I/O stays on per-session tasks owned by each VirtualPlayer, so a slow connection never stalls a worker.
/// </summary>
internal sealed class Orchestrator
{
    private readonly Options _o;
    private readonly Metrics _m;
    private readonly CancellationToken _ct;
    private readonly SemaphoreSlim _connectBudget;
    private readonly MapManager _maps;
    private readonly WorldDirectory _directory;
    private readonly Director _director;
    private readonly VirtualPlayer[] _bots;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public Orchestrator(Options options, Metrics metrics, CancellationToken ct)
    {
        _o = options;
        _m = metrics;
        _ct = ct;

        _directory = new WorldDirectory();
        _maps = new MapManager(options, metrics);
        _director = options.DirectorEnabled ? new Director(options, _directory, _maps, metrics) : null;
        _connectBudget = new SemaphoreSlim(options.ConnectBudget, options.ConnectBudget);

        _bots = new VirtualPlayer[options.Bots];
        for (int i = 0; i < _bots.Length; i++)
            _bots[i] = new VirtualPlayer(options.FirstIndex + i, options, metrics, _connectBudget, _directory, _maps, _director, ct);
    }

    public int Run(StreamWriter csv)
    {
        Console.WriteLine($"StressClient: {_o.Bots} bots -> {_o.Host}:{_o.Port}, ramp {_o.RampPerSecond}/s, duration {_o.DurationSeconds}s, " +
                          $"accounts {_o.Prefix}{_o.FirstIndex:D5}..{_o.Prefix}{_o.FirstIndex + _o.Bots - 1:D5}, " +
                          $"send budget {_o.ServerMaxPacket * _o.PacketBudgetFraction:0}/5s per bot (server MaxPacket {_o.ServerMaxPacket})");
        Console.WriteLine($"workers {Math.Min(_o.WorkerThreads, _o.Bots)}, map folders: {string.Join(", ", _o.MapFolders)}, " +
                          $"director {( _o.DirectorEnabled ? $"on ({_o.DirectorIntervalSeconds}s)" : "off")}");
        Console.WriteLine($"class {(_o.Class?.ToString() ?? "mixed")}, behaviours: {BehaviourList()}");

        _director?.Start();

        var workers = StartWorkers();
        var ramp = StartRamp();
        Task reporter = Task.Run(() => ReportLoopAsync(csv));

        try { Task.Delay(Timeout.Infinite, _ct).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }

        _director?.Stop();

        // Workers stop touching bots first; then every bot gets a polite disconnect and its send loop closes the socket.
        long deadline = MonoClock.Now + 10_000;
        foreach (var worker in workers)
        {
            int wait = (int)Math.Max(0, deadline - MonoClock.Now);
            if (!worker.Join(wait)) Console.WriteLine("warning: a worker did not stop within 10 s");
        }
        ramp.Join(2000);

        foreach (var bot in _bots) bot.Stop();

        long closeDeadline = MonoClock.Now + 3000;
        while (MonoClock.Now < closeDeadline && !AllSessionsClosed()) Thread.Sleep(20);
        foreach (var bot in _bots)
            if (!bot.SessionClosed) bot.ForceStop();

        try { reporter.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }

        return PrintSummary();
    }

    private bool AllSessionsClosed()
    {
        foreach (var bot in _bots)
            if (!bot.SessionClosed) return false;
        return true;
    }

    private Thread[] StartWorkers()
    {
        int count = Math.Min(_o.WorkerThreads, _bots.Length);
        var threads = new Thread[count];

        for (int w = 0; w < count; w++)
        {
            int worker = w;
            threads[w] = new Thread(() => WorkerLoop(worker, count))
            {
                IsBackground = true,
                Name = $"StressClient-Worker{worker}",
            };
            threads[w].Start();
        }

        return threads;
    }

    private void WorkerLoop(int worker, int workerCount)
    {
        while (!_ct.IsCancellationRequested)
        {
            long start = MonoClock.Now;
            for (int i = worker; i < _bots.Length; i += workerCount)
            {
                try
                {
                    _bots[i].Tick(start);
                }
                catch (Exception ex)
                {
                    // One bot must never take a worker (and the bots it shares it with) down.
                    Metrics.Inc(ref _m.Errors);
                    Console.WriteLine($"  bot {_bots[i].Index} tick error: {ex.GetType().Name}: {ex.Message}");
                }
            }

            long spent = MonoClock.Now - start;
            int wait = (int)Math.Max(1, _o.TickMs - spent);
            if (_ct.WaitHandle.WaitOne(wait)) break;
        }
    }

    private Thread StartRamp()
    {
        var thread = new Thread(() =>
        {
            double interval = 1000.0 / _o.RampPerSecond;
            long start = MonoClock.Now;

            for (int i = 0; i < _bots.Length; i++)
            {
                if (_ct.IsCancellationRequested) break;
                _bots[i].Released = true;

                long due = start + (long)((i + 1) * interval);
                int wait = (int)Math.Max(1, due - MonoClock.Now);
                if (_ct.WaitHandle.WaitOne(wait)) break;
            }
        })
        {
            IsBackground = true,
            Name = "StressClient-Ramp",
        };

        thread.Start();
        return thread;
    }

    private async Task ReportLoopAsync(StreamWriter csv)
    {
        var last = _m.Take();
        double lastT = 0;

        while (!_ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, _ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            var now = _m.Take();
            double t = _clock.Elapsed.TotalSeconds;
            var (line, row) = Metrics.Format(t, last, now, Math.Max(0.001, t - lastT));

            if (!_o.Quiet) Console.WriteLine(line);
            csv?.WriteLine(row);

            long nowMs = MonoClock.Now;
            int silentIn = _bots.Count(b => b.IsPlaying && nowMs - b.LastReceivedAt > 5000);
            int silentOut = _bots.Count(b => b.IsPlaying && nowMs - b.LastSentAt > 5000);
            if ((silentIn > 0 || silentOut > 0) && !_o.Quiet)
                Console.WriteLine($"  liveness: {silentIn} in-game bot(s) without a received packet for >5 s, {silentOut} without a completed write");
            if (_o.StatusJson)
                Console.WriteLine("@status " + Metrics.FormatJson(t, last, now, Math.Max(0.001, t - lastT), _bots.Length, Volatile.Read(ref _m.MaxInGame), silentIn));

            last = now;
            lastT = t;
        }
    }

    private string BehaviourList()
    {
        var on = new List<string>();
        if (_o.Hunt) on.Add("hunt");
        if (_o.Loot) on.Add("loot");
        if (_o.Town) on.Add("town");
        if (_o.Quests) on.Add("quests");
        if (_o.Potions) on.Add("potions");
        if (_o.Equip) on.Add("equip");
        if (_o.Chat) on.Add("chat");
        if (_o.Party) on.Add("party");
        if (_o.DirectorEnabled) on.Add("director");
        if (_o.AllowRun) on.Add("run");
        return on.Count == 0 ? "none (roam only)" : string.Join(" ", on);
    }

    private int PrintSummary()
    {
        var s = _m.Take();
        int reached = _bots.Count(b => b.ReachedGame);
        double secs = Math.Max(1, _clock.Elapsed.TotalSeconds);
        double avgRtt = s.RttCount > 0 ? (double)s.RttTotal / s.RttCount : double.NaN;

        Console.WriteLine();
        Console.WriteLine("==== StressClient summary ====");
        Console.WriteLine($"bots started        {_bots.Length} / {_o.Bots}");
        Console.WriteLine($"reached the game    {reached} / {_o.Bots}   (max concurrently in game {Volatile.Read(ref _m.MaxInGame)})");
        Console.WriteLine($"accounts created    {Interlocked.Read(ref _m.AccountsCreated)}   characters created {Interlocked.Read(ref _m.CharactersCreated)}   logins {Interlocked.Read(ref _m.Logins)}   games entered {Interlocked.Read(ref _m.GamesEntered)}");
        Console.WriteLine($"packets sent        {s.PacketsSent} ({s.PacketsSent / secs:0}/s, {s.Sends} socket writes, {s.Throttled} throttled ticks)");
        Console.WriteLine($"packets received    {s.PacketsReceived} ({s.PacketsReceived / secs:0}/s, {s.BytesReceived / 1024.0 / secs:0.0} KB/s)");
        Console.WriteLine($"behaviour           walks {s.Walks}  runs {s.Runs}  turns {s.Turns}  attacks {s.Attacks}  blocked {s.Blocked}  monsters seen {Interlocked.Read(ref _m.MonstersSeen)}  kills {s.Kills}  deaths {s.Deaths}  revives {Interlocked.Read(ref _m.Revives)}  equips {Interlocked.Read(ref _m.Equips)}");
        Console.WriteLine($"movement            maps loaded {s.MapsLoaded} (types {string.Join(",", _m.MapTypes.OrderBy(kv => kv.Key).Select(kv => kv.Key + "x" + kv.Value))})  missing {s.MapsMissing}  paths {s.PathFound} found / {s.PathFail} failed  repaths {s.Repaths}  doors {s.DoorsOpened}  move timeouts {s.MoveTimeouts}");
        Console.WriteLine($"director            {s.Directives} directives  ({_directory.MapCount} maps known)");
        Console.WriteLine($"economy             potions {s.Potions}  casts {s.Casts}  pickups {s.Pickups}  sold {s.Sold}  bought {s.Bought}  repairs {s.Repairs}  quests {s.QuestsAccepted} accepted ({s.QuestAcceptSent} sent)/{s.QuestFinishSent} handed in/{s.QuestsFinished} done/{s.QuestsAbandoned} abandoned/{s.QuestsRefused} refused  chats {s.Chats}  invites {s.Invites}  grouped {s.GroupJoins}");
        Console.WriteLine($"npc                 calls {s.NpcCalls}  pages {s.NpcPages}  goods {s.NpcGoods}  sell pages {s.NpcSellPages}  sales ok {s.SellSuccess}/failed {s.SellFailure}");
        Console.WriteLine($"map entries         {string.Join("  ", _m.MapEntries.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value))}");
        Console.WriteLine($"keepalive rtt       {(double.IsNaN(avgRtt) ? "-" : avgRtt.ToString("0.0"))} ms avg over {s.RttCount} samples");
        Console.WriteLine($"problems            errors {s.Errors}  decode errors {s.DecodeErrors}  unknown packets {s.UnknownPackets}  refused {s.Refused}  disconnects {s.Disconnects}  login timeouts {s.LoginTimeouts}  reconnects {Interlocked.Read(ref _m.Reconnects)}");

        bool ok = reached == _o.Bots && s.DecodeErrors == 0;
        Console.WriteLine(ok ? "RESULT: PASS (every bot reached the game, no decode errors)" : "RESULT: FAIL");
        return ok ? 0 : 1;
    }
}

using System.Diagnostics;

namespace StressClient;

/// <summary>
/// Entry point: parses options, ramps the fleet up, prints a metrics line every second, and stops every bot when
/// --duration ends (or on Ctrl+C). See Knowledgebase/StressClient/stressclient.md.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var o = Options.Parse(args, out var error);
        if (o == null)
        {
            if (!string.IsNullOrEmpty(error)) Console.Error.WriteLine("error: " + error);
            Console.Error.WriteLine(Options.Usage);
            return string.IsNullOrEmpty(error) ? 0 : 2;
        }

        Packet.IsServer = false; // decode server packets, as the client does (Client/Program.cs)
        ThreadPool.GetMinThreads(out int workers, out int io);
        ThreadPool.SetMinThreads(Math.Max(workers, 32), Math.Max(io, 32));

        var metrics = new Metrics();
        var budget = new SemaphoreSlim(o.ConnectBudget, o.ConnectBudget);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        Console.WriteLine($"StressClient: {o.Bots} bots -> {o.Host}:{o.Port}, ramp {o.RampPerSecond}/s, duration {o.DurationSeconds}s, " +
                          $"accounts {o.Prefix}{o.FirstIndex:D5}..{o.Prefix}{o.FirstIndex + o.Bots - 1:D5}, " +
                          $"send budget {o.ServerMaxPacket * o.PacketBudgetFraction:0}/5s per bot (server MaxPacket {o.ServerMaxPacket})");

        StreamWriter csv = null;
        if (!string.IsNullOrEmpty(o.CsvPath))
        {
            csv = new StreamWriter(o.CsvPath, append: false) { AutoFlush = true };
            csv.WriteLine(Metrics.CsvHeader);
        }

        var clock = Stopwatch.StartNew();
        cts.CancelAfter(TimeSpan.FromSeconds(o.DurationSeconds));
        var bots = new List<Bot>(o.Bots);
        var tasks = new List<Task>(o.Bots);

        var reporter = Task.Run(() => ReportLoopAsync(o, metrics, clock, csv, cts.Token));

        // Ramp: start bots at --ramp per second (a fleet connect budget additionally limits concurrent logins).
        double intervalMs = 1000.0 / o.RampPerSecond;
        for (int i = 0; i < o.Bots && !cts.IsCancellationRequested; i++)
        {
            var bot = new Bot(o.FirstIndex + i, o, metrics, budget);
            bots.Add(bot);
            tasks.Add(Task.Run(() => bot.RunAsync(cts.Token)));

            double due = (i + 1) * intervalMs;
            double wait = due - clock.Elapsed.TotalMilliseconds;
            if (wait > 1)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(wait), cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }

        try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }

        // Shutdown: bots send C.Disconnect and close; give them a few seconds.
        var all = Task.WhenAll(tasks);
        if (await Task.WhenAny(all, Task.Delay(10_000)) != all)
            Console.WriteLine("warning: some bots did not stop within 10 s");
        try { await reporter; } catch (OperationCanceledException) { }
        csv?.Dispose();

        return PrintSummary(o, metrics, bots, clock.Elapsed);
    }

    private static async Task ReportLoopAsync(Options o, Metrics m, Stopwatch clock, StreamWriter csv, CancellationToken ct)
    {
        var last = m.Take();
        double lastT = clock.Elapsed.TotalSeconds;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            var now = m.Take();
            double t = clock.Elapsed.TotalSeconds;
            var (line, row) = Metrics.Format(t, last, now, Math.Max(0.001, t - lastT));
            if (!o.Quiet) Console.WriteLine(line);
            csv?.WriteLine(row);
            last = now;
            lastT = t;
        }
    }

    private static int PrintSummary(Options o, Metrics m, List<Bot> bots, TimeSpan elapsed)
    {
        var s = m.Take();
        int reached = bots.Count(b => b.ReachedGame);
        double secs = Math.Max(1, elapsed.TotalSeconds);
        double avgRtt = s.RttCount > 0 ? (double)s.RttTotal / s.RttCount : double.NaN;

        Console.WriteLine();
        Console.WriteLine("==== StressClient summary ====");
        Console.WriteLine($"bots started        {bots.Count} / {o.Bots}");
        Console.WriteLine($"reached the game    {reached} / {o.Bots}   (max concurrently in game {Volatile.Read(ref m.MaxInGame)})");
        Console.WriteLine($"accounts created    {Interlocked.Read(ref m.AccountsCreated)}   characters created {Interlocked.Read(ref m.CharactersCreated)}   logins {Interlocked.Read(ref m.Logins)}   games entered {Interlocked.Read(ref m.GamesEntered)}");
        Console.WriteLine($"packets sent        {s.PacketsSent} ({s.PacketsSent / secs:0}/s, {s.Sends} socket writes, {s.Throttled} throttled ticks)");
        Console.WriteLine($"packets received    {s.PacketsReceived} ({s.PacketsReceived / secs:0}/s, {s.BytesReceived / 1024.0 / secs:0.0} KB/s)");
        Console.WriteLine($"behaviour           walks {s.Walks}  turns {s.Turns}  attacks {s.Attacks}  blocked {s.Blocked}  monsters seen {Interlocked.Read(ref m.MonstersSeen)}  kills {s.Kills}  deaths {s.Deaths}  revives {Interlocked.Read(ref m.Revives)}  equips {Interlocked.Read(ref m.Equips)}");
        Console.WriteLine($"keepalive rtt       {(double.IsNaN(avgRtt) ? "-" : avgRtt.ToString("0.0"))} ms avg over {s.RttCount} samples");
        Console.WriteLine($"problems            errors {s.Errors}  decode errors {s.DecodeErrors}  unknown packets {s.UnknownPackets}  refused {s.Refused}  disconnects {s.Disconnects}  login timeouts {s.LoginTimeouts}  reconnects {Interlocked.Read(ref m.Reconnects)}");

        bool ok = reached == o.Bots && s.DecodeErrors == 0;
        Console.WriteLine(ok ? "RESULT: PASS (every bot reached the game, no decode errors)" : "RESULT: FAIL");
        return ok ? 0 : 1;
    }
}

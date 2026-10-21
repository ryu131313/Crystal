namespace StressClient;

/// <summary>
/// Entry point: parses options, dispatches --check-maps, otherwise runs the Orchestrator and prints the summary.
/// See Knowledgebase/StressClient/stressclient.md.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var o = Options.Parse(args, out var error);
        if (o == null)
        {
            if (!string.IsNullOrEmpty(error)) Console.Error.WriteLine("error: " + error);
            Console.Error.WriteLine(Options.Usage);
            return string.IsNullOrEmpty(error) ? 0 : 2;
        }

        if (!string.IsNullOrEmpty(o.CheckMapsFolder)) return MapCheck.Run(o.CheckMapsFolder);

        Packet.IsServer = false; // decode server packets, as the client does (Client/Program.cs)
        ThreadPool.GetMinThreads(out int workers, out int io);
        ThreadPool.SetMinThreads(Math.Max(workers, 32), Math.Max(io, 32));

        var metrics = new Metrics();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        cts.CancelAfter(TimeSpan.FromSeconds(o.DurationSeconds));

        StreamWriter csv = null;
        if (!string.IsNullOrEmpty(o.CsvPath))
        {
            csv = new StreamWriter(o.CsvPath, append: false) { AutoFlush = true };
            csv.WriteLine(Metrics.CsvHeader);
        }

        try
        {
            return new Orchestrator(o, metrics, cts.Token).Run(csv);
        }
        finally
        {
            csv?.Dispose();
        }
    }
}

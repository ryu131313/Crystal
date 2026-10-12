using System.Security.Cryptography;

namespace WebRelay;

/// <summary>
/// Entry point. Modes:
///   (default)                 run the relay (see <see cref="RelayOptions"/> for the switches)
///   --emit-ts &lt;file&gt;          write the TS enum module (packet ids + the shared enums the web client uses)
///   --emit-fixtures &lt;dir&gt;     write codec golden files (bytes from the real Shared WritePacket) for WebClient/tools/codec-test.ts
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--emit-ts") return TsEmitter.Emit(args[1]);
        if (args.Length == 2 && args[0] == "--emit-fixtures") return Fixtures.Emit(args[1]);

        RelayOptions options;
        try { options = RelayOptions.Parse(args); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(RelayOptions.Usage);
            return 2;
        }

        byte[]? versionHash = null;
        if (!string.IsNullOrEmpty(options.VersionPath))
        {
            if (!File.Exists(options.VersionPath))
            {
                Console.Error.WriteLine($"--version-path '{options.VersionPath}' does not exist.");
                return 2;
            }
            using var stream = File.Open(options.VersionPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            versionHash = MD5.HashData(stream);
        }
        else
        {
            Console.WriteLine("No --version-path: C.ClientVersion is forwarded with the browser's zero hash (needs [General] CheckVersion=False).");
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var host = new RelayHost(options, versionHash);
        return await host.RunAsync(cts.Token);
    }
}

internal sealed class RelayOptions
{
    public string Listen = "127.0.0.1:5690";
    public string UpstreamHost = "127.0.0.1";
    public int UpstreamPort = 7000;
    /// <summary>The client folder (Data\, Map\, Sound\). Read-only: files are opened FileAccess.Read, FileShare.ReadWrite.</summary>
    public string ClientDataPath = "";
    /// <summary>Extra read-only folders searched for maps after ClientDataPath\Map (e.g. a scratch server's Maps\).</summary>
    public List<string> ExtraMapPaths = new();
    public string VersionPath = "";
    public string WebRoot = "";
    public int MaxPerIp = 8;
    public int MaxTotal = 256;
    public int ConnectsPerMinutePerIp = 30;
    /// <summary>Allowed Origin headers for /ws. Empty = only the relay's own origin.</summary>
    public List<string> Origins = new();

    public const string Usage =
        "WebRelay --client-data <client folder> [--listen 127.0.0.1:5690] [--upstream 127.0.0.1:7000] [--version-path <Client.exe>]\n" +
        "         [--web-root <WebClient folder>] [--map-path <extra Maps folder>]... [--origin <url>]... [--max-per-ip 8] [--max-total 256]";

    public static RelayOptions Parse(string[] args)
    {
        var o = new RelayOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--listen": o.Listen = Next(); break;
                case "--upstream":
                    {
                        var v = Next();
                        int colon = v.LastIndexOf(':');
                        if (colon <= 0 || !int.TryParse(v[(colon + 1)..], out o.UpstreamPort)) throw new ArgumentException("--upstream must be host:port");
                        o.UpstreamHost = v[..colon];
                        break;
                    }
                case "--client-data": o.ClientDataPath = Next(); break;
                case "--map-path": o.ExtraMapPaths.Add(Next()); break;
                case "--version-path": o.VersionPath = Next(); break;
                case "--web-root": o.WebRoot = Next(); break;
                case "--origin": o.Origins.Add(Next().TrimEnd('/')); break;
                case "--max-per-ip": o.MaxPerIp = int.Parse(Next()); break;
                case "--max-total": o.MaxTotal = int.Parse(Next()); break;
                default: throw new ArgumentException($"Unknown argument {args[i]}");
            }
        }
        if (string.IsNullOrEmpty(o.ClientDataPath) || !Directory.Exists(o.ClientDataPath))
            throw new ArgumentException("--client-data <client folder> is required and must exist");
        if (string.IsNullOrEmpty(o.WebRoot))
            o.WebRoot = FindWebRoot();
        if (!Directory.Exists(o.WebRoot))
            throw new ArgumentException($"Web root '{o.WebRoot}' does not exist (pass --web-root)");
        return o;
    }

    /// <summary>Dev default: the repo's WebClient\ folder, found by walking up from the exe (bin\Debug\... or a verify scratch dir).</summary>
    private static string FindWebRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "WebClient");
                if (File.Exists(Path.Combine(candidate, "play", "index.html"))) return candidate;
            }
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "WebClient");
    }
}

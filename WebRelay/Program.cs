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
    /// <summary>URL prefix the whole site lives under (e.g. /portal/mir2webclient); the tunnel forwards paths unstripped.</summary>
    public string Mount = "";
    /// <summary>Header carrying the player's address (CF-Connecting-IP behind the Cloudflare tunnel); trusted only from a loopback peer.</summary>
    public string TrustProxyHeader = "";
    /// <summary>Public mode: needs --origin and --web-root, and turns on <see cref="PlayerGuard"/>.</summary>
    public bool Public;
    public int AccountsPerHourPerIp = 3;
    public int CharactersPerHourPerIp = 5;
    /// <summary>Desktop patch host (<see cref="PatchIndex"/>): a clean distributable copy of the client. Empty = off.</summary>
    public string PatchRoot = "";
    public string PatchTiersPath = "";
    public PatchTiers.Rule[] PatchTierRules = Array.Empty<PatchTiers.Rule>();
    /// <summary>The SHA-256 cache; never inside the patch root (the relay does not write to client folders).</summary>
    public string PatchCachePath = "";
    /// <summary>Brotli copies of the compressible patch files, named by sha256: next to the hash cache, never inside the patch root.</summary>
    public string PatchCompressedPath = "";
    /// <summary>Slots for /api/patch + /patch/, separate from the 64 general HTTP slots so long downloads never starve the web client.</summary>
    public int PatchSlots = 256;
    /// <summary>--patch-auto: publish whenever the patch root changed and then held still (no PatchRevision.txt needed).</summary>
    public bool PatchAuto;

    public const string Usage =
        "WebRelay --client-data <client folder> [--listen 127.0.0.1:5690] [--upstream 127.0.0.1:7000] [--version-path <Client.exe>]\n" +
        "         [--web-root <WebClient folder>] [--map-path <extra Maps folder>]... [--origin <url>]... [--max-per-ip 8] [--max-total 256]\n" +
        "         [--connects-per-minute 30] [--mount /portal/mir2webclient] [--trust-proxy-header CF-Connecting-IP]\n" +
        "         [--public [--accounts-per-hour 3] [--characters-per-hour 5]]\n" +
        "         [--patch-root <clean client copy> [--patch-tiers <PatchTiers.txt>] [--patch-cache <file>] [--patch-slots 256] [--patch-auto]]";

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
                case "--connects-per-minute": o.ConnectsPerMinutePerIp = int.Parse(Next()); break;
                case "--mount":
                    o.Mount = "/" + Next().Trim('/');
                    if (o.Mount == "/" || o.Mount.Contains("..")) throw new ArgumentException("--mount must be a path such as /portal/mir2webclient");
                    break;
                case "--trust-proxy-header": o.TrustProxyHeader = Next(); break;
                case "--public": o.Public = true; break;
                case "--accounts-per-hour": o.AccountsPerHourPerIp = int.Parse(Next()); break;
                case "--characters-per-hour": o.CharactersPerHourPerIp = int.Parse(Next()); break;
                case "--patch-root": o.PatchRoot = Next(); break;
                case "--patch-tiers": o.PatchTiersPath = Next(); break;
                case "--patch-cache": o.PatchCachePath = Next(); break;
                case "--patch-slots": o.PatchSlots = int.Parse(Next()); break;
                case "--patch-auto": o.PatchAuto = true; break;
                default: throw new ArgumentException($"Unknown argument {args[i]}");
            }
        }
        if (string.IsNullOrEmpty(o.ClientDataPath) || !Directory.Exists(o.ClientDataPath))
            throw new ArgumentException("--client-data <client folder> is required and must exist");
        if (o.Public)
        {
            // A public relay never falls back to the dev defaults: an explicit origin allowlist and a published web root.
            if (o.Origins.Count == 0 || o.Origins.Contains("*")) throw new ArgumentException("--public needs --origin <https://your.site>");
            if (string.IsNullOrEmpty(o.WebRoot)) throw new ArgumentException("--public needs --web-root (the published copy, tools\\publish-webclient.ps1)");
        }
        if (o.PatchRoot.Length > 0)
        {
            if (!Directory.Exists(o.PatchRoot)) throw new ArgumentException($"--patch-root '{o.PatchRoot}' does not exist");
            if (o.PatchTiersPath.Length > 0 && !File.Exists(o.PatchTiersPath)) throw new ArgumentException($"--patch-tiers '{o.PatchTiersPath}' does not exist");
            if (o.PatchSlots < 1) throw new ArgumentException("--patch-slots must be at least 1");
            o.PatchTierRules = PatchTiers.Load(o.PatchTiersPath);
            if (o.PatchCachePath.Length == 0) o.PatchCachePath = Path.Combine(AppContext.BaseDirectory, "patch-hashes.bin");
            o.PatchCachePath = Path.GetFullPath(o.PatchCachePath);
            string root = Path.GetFullPath(o.PatchRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (o.PatchCachePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--patch-cache must be outside --patch-root (the relay never writes to a client folder)");
            o.PatchCompressedPath = Path.Combine(Path.GetDirectoryName(o.PatchCachePath)!, Path.GetFileNameWithoutExtension(o.PatchCachePath) + "-br");
            if ((o.PatchCompressedPath + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || root.StartsWith(o.PatchCompressedPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"the compressed copies folder '{o.PatchCompressedPath}' (next to --patch-cache) must not overlap --patch-root");
        }
        else if (o.PatchTiersPath.Length > 0 || o.PatchCachePath.Length > 0 || o.PatchAuto)
            throw new ArgumentException("--patch-tiers / --patch-cache / --patch-auto need --patch-root");
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

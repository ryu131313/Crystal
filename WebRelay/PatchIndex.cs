using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebRelay;

/// <summary>
/// The desktop patch host (in-game patcher plan phase 1, `Knowledgebase/Future Ideas/InGame-Patcher-Plan.md` §4.1), on
/// with --patch-root. Serves a clean distributable copy of the client folder as raw files:
///   /api/patch              {"revision","files":[{"p","s","h","t","v"}]}: path, size, sha256 (hex), tier, version
///                           (= <see cref="AssetHost.VersionOf"/>); ETag + compression, no-cache; 503 until the first index
///   /patch/&lt;path&gt;?v=        an indexed file only; stale v (or a file changed since the index) -> 412, else immutable.
///                           Small .map/.wav/.lst/.json/.txt go through <see cref="Compression"/>; everything else and
///                           every Range streams raw in 64 KB chunks (a 450 MB .Lib never sits in memory)
/// Publishing: a new index is built only when PatchRevision.txt in the root changes (the operator writes it LAST, after
/// copying files in). The pass hashes only files whose size or mtime differ from the hash cache (kept outside the root),
/// then re-checks the folder; if anything moved during the pass it waits 30 s and goes again, and only a stable pass is
/// swapped in. Until then the old index stays published.
/// Read-only on the root (FileAccess.Read, FileShare.ReadWrite | Delete); the only file written is the hash cache.
/// Threads: one background task builds; requests read the published <see cref="Snapshot"/> through a volatile field.
/// </summary>
internal sealed class PatchIndex
{
    public const string MarkerName = "PatchRevision.txt";
    private const int MaxCompressed = 32 * 1024 * 1024;
    private static readonly TimeSpan MarkerPoll = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(30);
    private static readonly HashSet<string> CompressedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".map", ".wav", ".lst", ".json", ".txt" };

    /// <summary>Never served, whatever PatchTiers.txt says: per-user files of a played-from folder and the patcher's own.</summary>
    private static readonly PatchTiers.Rule[] Deny = PatchTiers.Parse(new[]
    {
        "*.ini = x", "Error.txt = x", "RebuildProfile.txt = x", "Data/UserData/** = x", "Screenshots/** = x",
        "DirectX/** = x", "Patch/** = x", "Old__* = x",
    }, "deny list");

    internal sealed record Entry(string Path, string FullPath, long Size, long Ticks, string Sha256, int Tier, string Version);

    internal sealed class Snapshot
    {
        public required string Revision;
        public required Dictionary<string, Entry> Files;
        public required byte[] Json;
        public required string ETag;
    }

    private readonly string _root;
    private readonly PatchTiers.Rule[] _tiers;
    private readonly string _cachePath;
    private readonly Compression _compression;
    private readonly StaticFiles _files;
    private readonly Dictionary<string, (long Size, long Ticks, string Sha256)> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private volatile Snapshot? _published;
    private (long Length, long Ticks)? _publishedMarker;

    public PatchIndex(RelayOptions options, Compression compression)
    {
        _root = Path.GetFullPath(options.PatchRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _tiers = options.PatchTierRules;
        _cachePath = options.PatchCachePath;
        _compression = compression;
        _files = new StaticFiles(new[] { _root }, cacheImmutable: true);
        LoadCache();
    }

    /// <summary>The background builder: a pass at start, then one whenever the marker changes.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        bool warnedMissing = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var marker = MarkerStamp();
                if (marker == null)
                {
                    if (!warnedMissing) Console.WriteLine($"[patch] no {MarkerName} in the patch root: nothing published until it is written");
                    warnedMissing = true;
                }
                else if (marker != _publishedMarker)
                {
                    warnedMissing = false;
                    await BuildAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[patch] index pass failed: {ex.GetType().Name}: {ex.Message}");
            }
            try { await Task.Delay(MarkerPoll, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task BuildAsync(CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            var started = DateTime.UtcNow;
            var marker = MarkerStamp();
            if (marker == null) return;
            string revision = ReadMarker();
            var before = Scan();
            int hashed = 0;
            long hashedBytes = 0;
            var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (rel, info) in before)
            {
                ct.ThrowIfCancellationRequested();
                int tier = PatchTiers.TierOf(_tiers, rel);
                if (tier < 0) continue;
                long ticks = info.LastWriteTimeUtc.Ticks;
                if (!_hashes.TryGetValue(rel, out var cached) || cached.Size != info.Length || cached.Ticks != ticks)
                {
                    string? sha = await HashAsync(info.FullName, ct).ConfigureAwait(false);
                    if (sha == null) continue; // vanished or unreadable: the stability check below catches it
                    cached = (info.Length, ticks, sha);
                    _hashes[rel] = cached;
                    hashed++;
                    hashedBytes += info.Length;
                }
                entries[rel] = new Entry(rel, info.FullName, info.Length, ticks, cached.Sha256, tier, AssetHost.VersionOf(info));
            }

            if (!Stable(before, marker.Value))
            {
                Console.WriteLine($"[patch] the patch root changed during the pass (attempt {attempt}): retrying in {SettleDelay.TotalSeconds:0} s");
                await Task.Delay(SettleDelay, ct).ConfigureAwait(false);
                continue;
            }

            foreach (var stale in _hashes.Keys.Where(k => !before.ContainsKey(k)).ToList()) _hashes.Remove(stale);
            SaveCache();
            Publish(revision, entries);
            _publishedMarker = marker;
            long total = entries.Values.Sum(e => e.Size);
            Console.WriteLine($"[patch] published revision '{revision}': {entries.Count} files, {total / (1024.0 * 1024 * 1024):0.00} GB " +
                              $"({hashed} hashed, {hashedBytes / (1024.0 * 1024):0} MB) in {(DateTime.UtcNow - started).TotalSeconds:0.0} s");
            return;
        }
    }

    /// <summary>Every file under the root that the deny list allows (reparse points, hidden and system files skipped).</summary>
    private SortedDictionary<string, FileInfo> Scan()
    {
        var result = new SortedDictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
        };
        foreach (var info in new DirectoryInfo(_root).EnumerateFiles("*", options))
        {
            string rel = Path.GetRelativePath(_root, info.FullName).Replace('\\', '/');
            if (rel.StartsWith("../", StringComparison.Ordinal) || rel.Contains(':')) continue;
            if (string.Equals(rel, MarkerName, StringComparison.OrdinalIgnoreCase)) continue;
            if (PatchTiers.TierOf(Deny, rel) < 0) continue;
            result[rel] = info;
        }
        return result;
    }

    /// <summary>True when the folder still holds exactly the files of <paramref name="before"/>, unchanged, and the marker too.</summary>
    private bool Stable(SortedDictionary<string, FileInfo> before, (long, long) marker)
    {
        if (MarkerStamp() != marker) return false;
        var after = Scan();
        if (after.Count != before.Count) return false;
        foreach (var (rel, a) in after)
        {
            if (!before.TryGetValue(rel, out var b)) return false;
            if (a.Length != b.Length || a.LastWriteTimeUtc.Ticks != b.LastWriteTimeUtc.Ticks) return false;
        }
        return true;
    }

    private void Publish(string revision, Dictionary<string, Entry> entries)
    {
        var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("revision", revision);
            w.WriteStartArray("files");
            foreach (var e in entries.Values.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
            {
                w.WriteStartObject();
                w.WriteString("p", e.Path);
                w.WriteNumber("s", e.Size);
                w.WriteString("h", e.Sha256);
                w.WriteNumber("t", e.Tier);
                w.WriteString("v", e.Version);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        byte[] json = stream.ToArray();
        string etag = "\"" + Convert.ToHexString(SHA256.HashData(json), 0, 8).ToLowerInvariant() + "\"";
        _published = new Snapshot { Revision = revision, Files = entries, Json = json, ETag = etag };
    }

    // — requests —

    public Task ServeManifestAsync(HttpListenerContext ctx)
    {
        var snap = _published;
        if (snap == null)
        {
            ctx.Response.Headers["Retry-After"] = "5";
            RelayHost.Respond(ctx, 503, "patch index not ready");
            return Task.CompletedTask;
        }
        return _compression.WriteAsync(ctx, "patch-manifest|" + snap.ETag, () => snap.Json, "application/json", "no-cache", snap.ETag);
    }

    /// <summary>/patch/&lt;path&gt;?v=: <paramref name="urlPath"/> is the part after /patch/, still percent-encoded.</summary>
    public async Task ServeFileAsync(HttpListenerContext ctx, string urlPath)
    {
        var snap = _published;
        string rel = Uri.UnescapeDataString(urlPath).Replace('\\', '/');
        if (snap == null || !snap.Files.TryGetValue(rel, out var entry)) { RelayHost.Respond(ctx, 404, "not found"); return; }
        string? v = ctx.Request.QueryString["v"];
        if (string.IsNullOrEmpty(v)) { RelayHost.Respond(ctx, 400, "v required"); return; }
        var info = new FileInfo(entry.FullPath);
        // A stale v, or the file changed in the root after this index was built (a copy in progress): never serve
        // bytes the manifest's hash does not describe under an immutable URL.
        if (v != entry.Version || !info.Exists || AssetHost.VersionOf(info) != entry.Version)
        {
            RelayHost.Respond(ctx, 412, "stale version");
            return;
        }

        if (ctx.Request.Headers["Range"] == null && entry.Size <= MaxCompressed
            && CompressedExtensions.Contains(Path.GetExtension(entry.Path)))
        {
            await _compression.WriteAsync(ctx, "patch|" + entry.Path + "|" + entry.Version, () => ReadAll(entry.FullPath),
                StaticFiles.ContentType(entry.FullPath), AssetHost.Immutable, "\"" + entry.Version + "\"").ConfigureAwait(false);
            return;
        }
        await _files.ServeFileAsync(ctx, entry.FullPath).ConfigureAwait(false);
    }

    private static byte[] ReadAll(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[fs.Length];
        fs.ReadExactly(bytes);
        return bytes;
    }

    // — marker, hashing, cache —

    private (long Length, long Ticks)? MarkerStamp()
    {
        var info = new FileInfo(Path.Combine(_root, MarkerName));
        return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : null;
    }

    private string ReadMarker()
    {
        try
        {
            using var fs = new FileStream(Path.Combine(_root, MarkerName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            string text = reader.ReadToEnd().Trim();
            if (text.Length > 200) text = text[..200];
            return text.Length > 0 ? text : "unnamed";
        }
        catch (IOException) { return "unnamed"; }
    }

    private static async Task<string?> HashAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, useAsync: true);
            byte[] hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private const uint CacheMagic = 0x43485043; // "CPHC"

    private void LoadCache()
    {
        if (!File.Exists(_cachePath)) return;
        try
        {
            using var reader = new BinaryReader(File.OpenRead(_cachePath), Encoding.UTF8);
            if (reader.ReadUInt32() != CacheMagic || reader.ReadByte() != 1) return;
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string rel = reader.ReadString();
                long size = reader.ReadInt64(), ticks = reader.ReadInt64();
                _hashes[rel] = (size, ticks, Convert.ToHexString(reader.ReadBytes(32)).ToLowerInvariant());
            }
            Console.WriteLine($"[patch] hash cache: {_hashes.Count} files from {_cachePath}");
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or FormatException)
        {
            Console.Error.WriteLine($"[patch] hash cache unreadable ({ex.Message}): rehashing everything");
            _hashes.Clear();
        }
    }

    /// <summary>Atomic: a temp file next to the cache, then a replace.</summary>
    private void SaveCache()
    {
        string temp = _cachePath + ".tmp";
        using (var writer = new BinaryWriter(File.Create(temp), Encoding.UTF8))
        {
            writer.Write(CacheMagic);
            writer.Write((byte)1);
            writer.Write(_hashes.Count);
            foreach (var (rel, h) in _hashes)
            {
                writer.Write(rel);
                writer.Write(h.Size);
                writer.Write(h.Ticks);
                writer.Write(Convert.FromHexString(h.Sha256));
            }
        }
        File.Move(temp, _cachePath, overwrite: true);
    }
}

/// <summary>
/// PatchTiers.txt: "glob = tier" per line, first match wins, '#' starts a comment. Tiers: 0 core (before launch),
/// 1 starter (before launch), 2 background, 3 on demand only, x never served. Globs are relative to the patch root, use
/// '/', and ignore case: '*' and '?' stay inside one folder, '**' crosses folders; a glob with no '/' matches the file
/// name in any folder (so "*.pdb = x" covers every .pdb). A path no rule matches is tier 2.
/// </summary>
internal static class PatchTiers
{
    internal sealed record Rule(string Glob, Regex Pattern, bool NameOnly, int Tier);

    public const string Default = """
        Client.exe = 0
        *.dll = 0
        Client.deps.json = 0
        Client.runtimeconfig.json = 0
        runtimes/** = 0
        Localization/** = 0
        Resources/** = 0
        Data/Prguse*.Lib = 0
        Data/ChrSel.Lib = 0
        Data/Title.Lib = 0
        Data/UI_32bit.Lib = 0
        Data/ModernIcons.Lib = 0
        Data/Shaders/** = 0
        Data/Cursors/** = 0
        Sound/SoundList.lst = 0
        Map/0.map = 1
        Data/Map/WemadeMir2/** = 1
        *.pdb = x
        *.xml = x
        ** = 2
        """;

    public static Rule[] Load(string? path) =>
        string.IsNullOrEmpty(path) ? Parse(Default.Split('\n'), "built-in tiers") : Parse(File.ReadAllLines(path), path);

    public static Rule[] Parse(IEnumerable<string> lines, string source)
    {
        var rules = new List<Rule>();
        int lineNo = 0;
        foreach (var raw in lines)
        {
            lineNo++;
            string line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;
            int eq = line.LastIndexOf('=');
            string glob = eq > 0 ? line[..eq].Trim().Replace('\\', '/').TrimStart('/') : "";
            string value = eq > 0 ? line[(eq + 1)..].Trim() : "";
            int tier = value switch { "0" => 0, "1" => 1, "2" => 2, "3" => 3, "x" or "X" => -1, _ => -2 };
            if (glob.Length == 0 || tier == -2)
                throw new ArgumentException($"{source} line {lineNo}: expected '<glob> = 0|1|2|3|x', got '{raw.Trim()}'");
            rules.Add(new Rule(glob, ToRegex(glob), !glob.Contains('/'), tier));
        }
        return rules.ToArray();
    }

    /// <summary>The first matching rule's tier (-1 = never served); 2 when nothing matches.</summary>
    public static int TierOf(Rule[] rules, string relPath)
    {
        string name = relPath[(relPath.LastIndexOf('/') + 1)..];
        foreach (var rule in rules)
            if (rule.Pattern.IsMatch(rule.NameOnly ? name : relPath)) return rule.Tier;
        return 2;
    }

    private static Regex ToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                bool slash = i + 2 < glob.Length && glob[i + 2] == '/';
                sb.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

using System.Net;
using System.Text.Json;

namespace WebRelay;

/// <summary>
/// Versioned, cache-forever client assets (after WoOOLcraft's AssetHost: libindex sidecars, checked ?v= URLs).
///   /api/manifest                 {"data":{path:v},"map":{..},"sound":{..}} for every file of the client folder's Data\,
///                                 Sound\ and the map folders (keys lower-case, '/'); no-cache + ETag. v = hash of size + mtime.
///   /libindex/&lt;path&gt;.Lib?v=      one .Lib's index in ONE request instead of HEAD + header + offset table + frame table
///                                 (4 round trips): "CLIX" u8 1, i64 file length, i32 n + file[0, tableEnd), i32 n + file[frameSeek, EOF)
///   /data/&lt;path&gt;?v=&amp;o=&amp;n=        exactly n bytes at offset o, as a 200 (each sprite read is its own cacheable URL; Range 206s
///                                 cache badly and Cloudflare will not edge-cache them)
///   /map/&lt;file&gt;.map?v=, /sound/&lt;file&gt;?v=   the whole file
/// Every ?v= URL is checked against the file's live version and answered 412 when it differs (a patched client folder must
/// never be cached under the old version); a match is "public, max-age=31536000, immutable". Read-only file access only.
/// </summary>
internal sealed class AssetHost
{
    public const string Immutable = "public, max-age=31536000, immutable";
    private static readonly TimeSpan ManifestTtl = TimeSpan.FromSeconds(10);
    private const int MaxSlice = 16 * 1024 * 1024;

    private readonly RelayOptions _options;
    private readonly StaticFiles _clientData;
    private readonly StaticFiles[] _extraMaps;
    private readonly Compression _compression;
    private readonly object _lock = new();
    private (DateTime Built, byte[] Json, string ETag)? _manifest;

    public AssetHost(RelayOptions options, StaticFiles clientData, Compression compression)
    {
        _options = options;
        _clientData = clientData;
        _extraMaps = options.ExtraMapPaths.Select(p => new StaticFiles(new[] { p }, cacheImmutable: true)).ToArray();
        _compression = compression;
    }

    /// <summary>A file's version: 10 hex digits of FNV-1a over its length and last-write ticks.</summary>
    internal static string VersionOf(FileInfo info)
    {
        ulong h = 14695981039346656037UL;
        foreach (long value in new[] { info.Length, info.LastWriteTimeUtc.Ticks })
            for (int i = 0; i < 8; i++) { h ^= (byte)(value >> (i * 8)); h *= 1099511628211UL; }
        return (h & 0xFF_FFFF_FFFFUL).ToString("x10");
    }

    // — manifest —

    public Task ServeManifestAsync(HttpListenerContext ctx)
    {
        var (json, etag) = Manifest();
        return _compression.WriteAsync(ctx, "manifest|" + etag, () => json, "application/json", "no-cache", etag);
    }

    private (byte[] Json, string ETag) Manifest()
    {
        lock (_lock)
        {
            if (_manifest is { } m && DateTime.UtcNow - m.Built < ManifestTtl) return (m.Json, m.ETag);
        }
        var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            WriteFolder(w, "data", new[] { Path.Combine(_options.ClientDataPath, "Data") });
            WriteFolder(w, "map", new[] { Path.Combine(_options.ClientDataPath, "Map") }.Concat(_options.ExtraMapPaths));
            WriteFolder(w, "sound", new[] { Path.Combine(_options.ClientDataPath, "Sound") });
            w.WriteEndObject();
        }
        byte[] json = stream.ToArray();
        string etag = "\"" + Fnv(json) + "\"";
        lock (_lock) _manifest = (DateTime.UtcNow, json, etag);
        return (json, etag);
    }

    /// <summary>Every file under the folders (the first folder holding a name wins, as the map lookup does).</summary>
    private static void WriteFolder(Utf8JsonWriter w, string name, IEnumerable<string> folders)
    {
        w.WriteStartObject(name);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            var root = new DirectoryInfo(folder);
            foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                string key = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/').ToLowerInvariant();
                if (seen.Add(key)) w.WriteString(key, VersionOf(file));
            }
        }
        w.WriteEndObject();
    }

    private static string Fnv(byte[] bytes)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in bytes) { h ^= b; h *= 1099511628211UL; }
        return h.ToString("x16");
    }

    // — versioned files —

    /// <summary>A /data/, /map/ or /sound/ request carrying ?v=. False when the file does not exist (caller 404s).</summary>
    public async Task<bool> TryServeVersionedAsync(HttpListenerContext ctx, string path, string v)
    {
        string? full = ResolveAsset(path);
        if (full == null) return false;
        var info = new FileInfo(full);
        string current = VersionOf(info);
        if (!string.Equals(v, current, StringComparison.Ordinal)) { Stale(ctx, current); return true; }

        var q = ctx.Request.QueryString;
        if (q["o"] != null || q["n"] != null)
        {
            if (!long.TryParse(q["o"], out long o) || !int.TryParse(q["n"], out int n) || o < 0 || n <= 0 || n > MaxSlice || o + n > info.Length)
            {
                RelayHost.Respond(ctx, 416, "bad slice");
                return true;
            }
            string sliceTag = $"\"{current}-{o:x}-{n:x}\"";
            var res = ctx.Response;
            res.Headers["Cache-Control"] = Immutable;
            res.Headers["ETag"] = sliceTag;
            res.ContentType = "application/octet-stream";
            if (ctx.Request.Headers["If-None-Match"] == sliceTag) { res.StatusCode = 304; res.Close(); return true; }
            byte[] buffer = new byte[n];
            await using (var fs = OpenRead(full))
            {
                fs.Seek(o, SeekOrigin.Begin);
                await fs.ReadExactlyAsync(buffer).ConfigureAwait(false);
            }
            res.StatusCode = 200;
            res.ContentLength64 = n;
            if (ctx.Request.HttpMethod != "HEAD") await res.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
            res.Close();
            return true;
        }

        // Whole file (maps compress ~20x; sounds a little).
        await _compression.WriteAsync(ctx, $"file|{full}|{current}", () => File.ReadAllBytes(full), StaticFiles.ContentType(full), Immutable, $"\"{current}\"").ConfigureAwait(false);
        return true;
    }

    // — library index —

    /// <summary>/libindex/&lt;path&gt;?v=: the index blob (see the class summary). False when the library does not exist.</summary>
    public async Task<bool> TryServeLibIndexAsync(HttpListenerContext ctx, string path, string? v)
    {
        string? full = _clientData.Resolve("/data/" + path);
        if (full == null) return false;
        var info = new FileInfo(full);
        string current = VersionOf(info);
        if (v == null || !string.Equals(v, current, StringComparison.Ordinal)) { Stale(ctx, current); return true; }
        await _compression.WriteAsync(ctx, $"lix|{full}|{current}", () => LibIndex(full), "application/octet-stream", Immutable, $"\"lix-{current}\"").ConfigureAwait(false);
        return true;
    }

    /// <summary>The header + offset table and the v3 frame table of a .Lib (crystalLib.ts reads the same fields).</summary>
    internal static byte[] LibIndex(string full)
    {
        using var fs = OpenRead(full);
        long length = fs.Length;
        var reader = new BinaryReader(fs);
        byte[] head = Array.Empty<byte>(), frames = Array.Empty<byte>();
        if (length >= 12)
        {
            int version = reader.ReadInt32(), count = reader.ReadInt32();
            int frameSeek = version >= 3 ? reader.ReadInt32() : 0;
            long tableEnd = (version >= 3 ? 12L : 8L) + (long)Math.Max(0, count) * 4;
            if (version >= 2 && count >= 0 && tableEnd <= length)
            {
                fs.Seek(0, SeekOrigin.Begin);
                head = reader.ReadBytes((int)tableEnd);
                if (version >= 3 && frameSeek > 0 && frameSeek + 4L <= length)
                {
                    fs.Seek(frameSeek, SeekOrigin.Begin);
                    frames = reader.ReadBytes((int)(length - frameSeek));
                }
            }
            else
            {
                fs.Seek(0, SeekOrigin.Begin);
                head = reader.ReadBytes((int)Math.Min(length, 16)); // the client reports the format error
            }
        }
        var output = new MemoryStream(head.Length + frames.Length + 32);
        var w = new BinaryWriter(output);
        w.Write("CLIX"u8);
        w.Write((byte)1);
        w.Write(length);
        w.Write(head.Length);
        w.Write(head);
        w.Write(frames.Length);
        w.Write(frames);
        return output.ToArray();
    }

    // — helpers —

    /// <summary>"/data/x", "/map/x", "/sound/x" to a file under the client folder (maps also under each --map-path).</summary>
    private string? ResolveAsset(string path)
    {
        string? full = _clientData.Resolve(path);
        if (full != null || !path.StartsWith("/map/", StringComparison.OrdinalIgnoreCase)) return full;
        string name = "/" + path["/map/".Length..];
        foreach (var extra in _extraMaps)
            if ((full = extra.Resolve(name)) != null) return full;
        return null;
    }

    /// <summary>412: the URL names another version of the file (the client folder was patched). Never cached.</summary>
    private static void Stale(HttpListenerContext ctx, string current)
    {
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.Headers["X-Asset-Version"] = current;
        RelayHost.Respond(ctx, 412, "asset version changed: reload the page");
    }

    private static FileStream OpenRead(string full) =>
        new(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
}

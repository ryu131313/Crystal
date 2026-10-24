using System.Buffers;
using System.Net;

namespace WebRelay;

/// <summary>
/// Read-only file serving under a set of roots: traversal guard, ETag/Last-Modified + 304, and a single
/// "bytes=a-b" Range (the .Lib reader fetches index windows and single images by offset).
/// Files are opened FileAccess.Read + FileShare.ReadWrite: never written, never locked against the real client.
/// </summary>
internal sealed class StaticFiles
{
    private readonly string[] _roots;
    private readonly bool _cacheImmutable;
    private readonly Func<string, bool>? _deny;

    /// <param name="deny">Optional: true for a root-relative path ('/'-separated, from the resolved full path) that is
    /// never served (answered as missing).</param>
    public StaticFiles(IEnumerable<string> roots, bool cacheImmutable, Func<string, bool>? deny = null)
    {
        _roots = roots.Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToArray();
        _cacheImmutable = cacheImmutable;
        _deny = deny;
    }

    public async Task ServeAsync(HttpListenerContext ctx, string urlPath)
    {
        if (!await TryServeAsync(ctx, urlPath).ConfigureAwait(false)) RelayHost.Respond(ctx, 404, "not found");
    }

    public async Task<bool> TryServeAsync(HttpListenerContext ctx, string urlPath)
    {
        string? full = Resolve(urlPath);
        if (full == null) return false;
        await ServeFileAsync(ctx, full).ConfigureAwait(false);
        return true;
    }

    /// <summary>Streams an already resolved file (the caller has checked it lies inside a root). <paramref name="unchanged"/>,
    /// when given, runs after the last read and before the last chunk goes out: false (the file changed while it streamed)
    /// aborts the connection, so no client or cache ever holds a complete body that the caller's version does not describe.</summary>
    public async Task ServeFileAsync(HttpListenerContext ctx, string full, Func<bool>? unchanged = null)
    {
        var info = new FileInfo(full);
        string etag = $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";
        var res = ctx.Response;
        res.Headers["ETag"] = etag;
        res.Headers["Last-Modified"] = info.LastWriteTimeUtc.ToString("R");
        res.Headers["Accept-Ranges"] = "bytes";
        // Client data: versioned by ?v=<etag> from the page, so it may be cached hard; without ?v it revalidates.
        bool versioned = _cacheImmutable && ctx.Request.QueryString["v"] != null;
        res.Headers["Cache-Control"] = versioned ? "public, max-age=31536000, immutable" : "no-cache";
        res.ContentType = ContentType(full);

        if (ctx.Request.Headers["If-None-Match"] == etag)
        {
            res.StatusCode = 304;
            res.Close();
            return;
        }

        long start = 0, end = info.Length - 1;
        string? range = ctx.Request.Headers["Range"];
        bool partial = false;
        if (range != null && TryParseRange(range, info.Length, out start, out end))
        {
            partial = true;
        }
        else if (range != null)
        {
            res.StatusCode = 416;
            res.Headers["Content-Range"] = $"bytes */{info.Length}";
            res.Close();
            return;
        }

        long length = info.Length == 0 ? 0 : end - start + 1;
        res.StatusCode = partial ? 206 : 200;
        if (partial) res.Headers["Content-Range"] = $"bytes {start}-{end}/{info.Length}";
        res.ContentLength64 = length;
        if (ctx.Request.HttpMethod == "HEAD") { res.Close(); return; }

        await using (var fs = OpenRead(full))
        {
            fs.Seek(start, SeekOrigin.Begin);
            if (!await CopyAsync(fs, res.OutputStream, length, unchanged).ConfigureAwait(false))
            {
                res.Abort();
                return;
            }
        }
        res.Close();
    }

    private const int ChunkSize = 1024 * 1024;

    /// <summary>Unbuffered async read: <see cref="CopyAsync"/> reads in 1 MB chunks, so a FileStream buffer would only copy twice.</summary>
    internal static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0, useAsync: true);

    /// <summary>Copies <paramref name="length"/> bytes in chunks of up to 1 MB through a pooled buffer. <paramref name="verify"/>
    /// (optional) runs after the last read, before the last write: false from it, or a source that ends early while it is
    /// set, returns false with the last chunk unsent.</summary>
    internal static async Task<bool> CopyAsync(Stream source, Stream output, long length, Func<bool>? verify = null)
    {
        if (length <= 0) return verify?.Invoke() ?? true;
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(ChunkSize, length));
        try
        {
            long left = length;
            while (left > 0)
            {
                int n = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(Math.Min(buffer.Length, ChunkSize), left))).ConfigureAwait(false);
                if (n == 0) return verify == null;
                if (n == left && verify != null && !verify()) return false;
                await output.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
                left -= n;
            }
            return true;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>Maps "/a/b.c" onto the first root holding it; null for traversal attempts, denied or missing files.</summary>
    internal string? Resolve(string urlPath)
    {
        string rel = Uri.UnescapeDataString(urlPath).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        if (rel.Length == 0 || rel.Contains(':') || rel.Contains("..")) return null;
        foreach (var root in _roots)
        {
            string full = Path.GetFullPath(Path.Combine(root, rel));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(full)) continue;
            if (_deny != null && _deny(full[root.Length..].Replace(Path.DirectorySeparatorChar, '/'))) return null;
            return full;
        }
        return null;
    }

    private static bool TryParseRange(string header, long size, out long start, out long end)
    {
        start = 0; end = size - 1;
        if (!header.StartsWith("bytes=", StringComparison.Ordinal) || header.Contains(',')) return false;
        var spec = header[6..].Split('-');
        if (spec.Length != 2) return false;
        if (spec[0].Length == 0)
        {
            if (!long.TryParse(spec[1], out long suffix) || suffix <= 0) return false;
            start = Math.Max(0, size - suffix);
        }
        else
        {
            if (!long.TryParse(spec[0], out start)) return false;
            if (spec[1].Length > 0 && !long.TryParse(spec[1], out end)) return false;
        }
        if (end >= size) end = size - 1;
        return start >= 0 && start <= end;
    }

    internal static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json",
        ".png" => "image/png",
        ".map" when path.Contains($"{Path.DirectorySeparatorChar}js{Path.DirectorySeparatorChar}") => "application/json",
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".ogg" => "audio/ogg",
        _ => "application/octet-stream",
    };
}

using System.IO.Compression;
using System.Net;

namespace WebRelay;

/// <summary>
/// Response compression (after WoOOLcraft's ResponseCompression.cs, load-time item LT2): Brotli (quality 5) when the
/// request accepts br, else gzip. It has to happen here: Cloudflare compresses text at its edge AFTER the bytes have
/// crossed the home upload, and passes application/octet-stream (maps) through as is (a 12.7 MB map is 0.57 MB gzipped).
/// Encoded bodies live in an LRU (default 256 MB) keyed by a caller key that includes the file version, so each body is
/// compressed once. A body under 1 KB, or one that shrinks by less than 5 %, is sent raw (and remembered as such).
/// Only full 200 bodies are compressed; never ranges or .Lib slices (their images are gzip already).
/// Thread-safe: one lock around the LRU bookkeeping; compression itself runs outside the lock.
/// </summary>
internal sealed class Compression
{
    private const int MinSize = 1024;
    private readonly long _budget;
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, byte[]? Body)>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, byte[]? Body)> _lru = new();
    private long _bytes;

    public Compression(long budgetBytes) => _budget = budgetBytes;

    /// <summary>"br", "gzip" or null from an Accept-Encoding header.</summary>
    public static string? Choose(string? acceptEncoding)
    {
        if (string.IsNullOrEmpty(acceptEncoding)) return null;
        if (acceptEncoding.Contains("br", StringComparison.OrdinalIgnoreCase)) return "br";
        if (acceptEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase)) return "gzip";
        return null;
    }

    /// <summary>
    /// Writes a full 200 body: encoded when the request allows and it pays, else raw. <paramref name="key"/> must change
    /// whenever the bytes do (include the version or ETag). <paramref name="raw"/> is only called on a cache miss or
    /// for a client that takes no encoding.
    /// </summary>
    public async Task WriteAsync(HttpListenerContext ctx, string key, Func<byte[]> raw, string contentType, string cacheControl, string etag)
    {
        var res = ctx.Response;
        res.ContentType = contentType;
        res.Headers["Cache-Control"] = cacheControl;
        res.Headers["Vary"] = "Accept-Encoding";
        string? encoding = Choose(ctx.Request.Headers["Accept-Encoding"]);
        byte[]? encoded = encoding == null ? null : Encoded(key, encoding, raw);
        // The same version under two encodings must not share a strong ETag.
        res.Headers["ETag"] = encoded != null ? "W/" + etag : etag;

        string? inm = ctx.Request.Headers["If-None-Match"];
        if (inm != null && (inm == etag || inm == "W/" + etag))
        {
            res.StatusCode = 304;
            res.Close();
            return;
        }

        byte[] body = encoded ?? raw();
        if (encoded != null) res.Headers["Content-Encoding"] = encoding;
        res.StatusCode = 200;
        res.ContentLength64 = body.Length;
        if (ctx.Request.HttpMethod != "HEAD") await res.OutputStream.WriteAsync(body).ConfigureAwait(false);
        res.Close();
    }

    /// <summary>The encoded body, or null when it is not worth encoding.</summary>
    private byte[]? Encoded(string key, string encoding, Func<byte[]> raw)
    {
        string k = encoding + "|" + key;
        lock (_lock)
        {
            if (_map.TryGetValue(k, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Body;
            }
        }

        byte[] source = raw();
        byte[]? body = null;
        if (source.Length >= MinSize)
        {
            var output = new MemoryStream(source.Length / 4 + 64);
            using (Stream z = encoding == "br"
                ? new BrotliStream(output, new BrotliCompressionOptions { Quality = 5 }, leaveOpen: true)
                : new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                z.Write(source);
            }
            if (output.Length <= source.Length * 95 / 100) body = output.ToArray();
        }

        lock (_lock)
        {
            if (!_map.ContainsKey(k))
            {
                _map[k] = _lru.AddFirst((k, body));
                _bytes += (body?.Length ?? 0) + k.Length;
                while (_bytes > _budget && _lru.Last != null)
                {
                    var last = _lru.Last;
                    _lru.RemoveLast();
                    _map.Remove(last.Value.Key);
                    _bytes -= (last.Value.Body?.Length ?? 0) + last.Value.Key.Length;
                }
            }
        }
        return body;
    }
}

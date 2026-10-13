using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WebRelay;

/// <summary>
/// Content-hashed module URLs (after WoOOLcraft's PageBuild.cs, load-time item LT3).
///   build id  = 12 hex digits of SHA-256 over every js\**\*.js of the web root (path + bytes); recomputed when the file
///               count, total size or newest write time changes, checked at most every 2 s.
///   /js/&lt;id&gt;/x.js  the current id: x.js immutable (Cloudflare and the browser keep it until the next publish).
///                   another 12-hex id: 307 to the current one, no-store (a page that raced a publish lands on one build).
///   /play/        index.html rewritten: ../js/ -> ../js/&lt;id&gt;/, plus one &lt;link rel="modulepreload"&gt; per module, so the
///                 browser fetches all ~100 modules in parallel instead of walking the import graph level by level.
///                 no-cache + an ETag of the rewritten bytes (Cloudflare's Browser Cache TTL may still hold the page up to
///                 4 h: harmless, its old id redirects to the current build).
/// The old /js/x.js path keeps working (no-cache), for the dev relay workflow and old pages.
/// </summary>
internal sealed partial class PageBuild
{
    private readonly string _webRoot;
    private readonly Compression _compression;
    private readonly object _lock = new();
    private DateTime _checked;
    private (int Count, long Size, long Newest) _stamp;
    private string _id = "";
    private string[] _modules = Array.Empty<string>();

    public PageBuild(string webRoot, Compression compression)
    {
        _webRoot = Path.GetFullPath(webRoot);
        _compression = compression;
    }

    [GeneratedRegex("^[0-9a-f]{12}$")]
    private static partial Regex BuildIdPattern();

    /// <summary>True when <paramref name="segment"/> looks like a build id (the first path segment after /js/).</summary>
    public static bool IsBuildId(string segment) => BuildIdPattern().IsMatch(segment);

    /// <summary>The current build id and the module paths relative to js\ ('/' separated), refreshed when js\ changed.</summary>
    public (string Id, string[] Modules) Current()
    {
        lock (_lock)
        {
            if (_id.Length > 0 && DateTime.UtcNow - _checked < TimeSpan.FromSeconds(2)) return (_id, _modules);
            _checked = DateTime.UtcNow;
            string js = Path.Combine(_webRoot, "js");
            var files = Directory.Exists(js)
                ? new DirectoryInfo(js).EnumerateFiles("*.js", SearchOption.AllDirectories).OrderBy(f => f.FullName, StringComparer.Ordinal).ToArray()
                : Array.Empty<FileInfo>();
            var stamp = (files.Length, files.Sum(f => f.Length), files.Length == 0 ? 0 : files.Max(f => f.LastWriteTimeUtc.Ticks));
            if (_id.Length > 0 && stamp == _stamp) return (_id, _modules);

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var modules = new string[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                modules[i] = Path.GetRelativePath(js, files[i].FullName).Replace('\\', '/');
                sha.AppendData(Encoding.UTF8.GetBytes(modules[i]));
                sha.AppendData(File.ReadAllBytes(files[i].FullName));
            }
            _id = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant()[..12];
            _modules = modules;
            _stamp = stamp;
            return (_id, _modules);
        }
    }

    /// <summary>/js/&lt;id&gt;/rest: the module (immutable) or a 307 to the current build.</summary>
    public async Task ServeModuleAsync(HttpListenerContext ctx, string id, string rest, StaticFiles web)
    {
        var (current, _) = Current();
        if (id != current)
        {
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.Redirect(RelayHostPrefix(ctx, id) + current + "/" + rest);
            ctx.Response.StatusCode = 307;
            ctx.Response.Close();
            return;
        }
        string? full = web.Resolve("/js/" + rest);
        if (full == null) { RelayHost.Respond(ctx, 404, "not found"); return; }
        await _compression.WriteAsync(ctx, $"js|{current}|{rest}", () => File.ReadAllBytes(full), StaticFiles.ContentType(full), AssetHost.Immutable, $"\"{current}\"").ConfigureAwait(false);
    }

    /// <summary>The request path up to and including "/js/" (keeps the mount), for the redirect.</summary>
    private static string RelayHostPrefix(HttpListenerContext ctx, string id)
    {
        string full = ctx.Request.Url!.AbsolutePath;
        int at = full.IndexOf("/js/" + id + "/", StringComparison.Ordinal);
        return full[..(at + 4)];
    }

    /// <summary>/play/ (index.html): rewritten with the build id and the modulepreload list.</summary>
    public async Task ServePageAsync(HttpListenerContext ctx, string indexPath)
    {
        var (id, modules) = Current();
        string html = await File.ReadAllTextAsync(indexPath).ConfigureAwait(false);
        var preload = new StringBuilder();
        foreach (var m in modules)
            if (!m.StartsWith("tools/", StringComparison.Ordinal))
                preload.Append("  <link rel=\"modulepreload\" href=\"../js/").Append(id).Append('/').Append(m).Append("\">\n");
        html = html.Replace("\"../js/", "\"../js/" + id + "/", StringComparison.Ordinal)
                   .Replace("</head>", preload + "</head>", StringComparison.Ordinal);
        byte[] bytes = Encoding.UTF8.GetBytes(html);
        string etag = "\"" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16] + "\"";
        await _compression.WriteAsync(ctx, "page|" + etag, () => bytes, "text/html; charset=utf-8", "no-cache", etag).ConfigureAwait(false);
    }
}

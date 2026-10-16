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
///   /play/        index.html rewritten: ../js/ -> ../js/&lt;id&gt;/, plus one &lt;link rel="modulepreload"&gt; per module of
///                 play/main.js's STATIC import graph (import / export ... from, side-effect imports; parsed from the emitted
///                 JS, followed transitively), so the browser fetches those modules in parallel instead of walking the graph
///                 level by level. Dynamic import() targets are left out: the game UI trees (src/ui/uiLoader.ts loads
///                 classicUi.js or modern/modernUi.js) download only when used, so a player never fetches both. Without
///                 js\play\main.js every module except tools/ is preloaded (the old behaviour).
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
    private string[] _preload = Array.Empty<string>();

    public PageBuild(string webRoot, Compression compression)
    {
        _webRoot = Path.GetFullPath(webRoot);
        _compression = compression;
    }

    [GeneratedRegex("^[0-9a-f]{12}$")]
    private static partial Regex BuildIdPattern();

    /// <summary>Static module specifiers of emitted ES module JS: <c>import ... from "x"</c>, <c>export ... from "x"</c> and
    /// <c>import "x"</c> at a statement start. The clause class has no quotes, parentheses or '=', so a dynamic
    /// <c>import("x")</c>, a string or an <c>export const</c> never matches.</summary>
    [GeneratedRegex("""(?:^|[;}\n])[ \t]*(?:import|export)[ \t]*(?:[\w$*{},\s]*?\bfrom[ \t]*)?["']([^"'\n]+)["']""")]
    private static partial Regex StaticImportPattern();

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
            _preload = StaticGraph(js, modules);
            _stamp = stamp;
            return (_id, _modules);
        }
    }

    /// <summary>play/main.js's static import graph (paths relative to js\, '/' separated, main.js first), or every module
    /// except tools/ when there is no main.js. Only relative specifiers that name a module of the build are followed.</summary>
    private static string[] StaticGraph(string js, string[] modules)
    {
        const string entry = "play/main.js";
        var known = new HashSet<string>(modules, StringComparer.Ordinal);
        if (!known.Contains(entry)) return modules.Where(m => !m.StartsWith("tools/", StringComparison.Ordinal)).ToArray();
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { entry };
        var queue = new Queue<string>();
        queue.Enqueue(entry);
        while (queue.Count > 0)
        {
            string module = queue.Dequeue();
            order.Add(module);
            string text;
            try { text = File.ReadAllText(Path.Combine(js, module.Replace('/', Path.DirectorySeparatorChar))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            int slash = module.LastIndexOf('/');
            string dir = slash < 0 ? "" : module[..(slash + 1)];
            foreach (Match match in StaticImportPattern().Matches(text))
            {
                string spec = match.Groups[1].Value;
                if (!spec.StartsWith("./", StringComparison.Ordinal) && !spec.StartsWith("../", StringComparison.Ordinal)) continue;
                string? target = Resolve(dir, spec);
                if (target != null && known.Contains(target) && seen.Add(target)) queue.Enqueue(target);
            }
        }
        return order.ToArray();
    }

    /// <summary>A relative specifier against a module's directory (both '/' separated, relative to js\); null when it
    /// leaves js\ or carries a query / hash.</summary>
    private static string? Resolve(string dir, string spec)
    {
        if (spec.IndexOfAny(['?', '#']) >= 0) return null;
        var parts = new List<string>(dir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (string part in spec.Split('/'))
        {
            if (part == "." || part.Length == 0) continue;
            if (part == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        return string.Join('/', parts);
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
        Current(); // refresh, then read the id and its preload list together (another request may refresh meanwhile)
        string id;
        string[] modules;
        lock (_lock) { id = _id; modules = _preload; }
        string html = await File.ReadAllTextAsync(indexPath).ConfigureAwait(false);
        var preload = new StringBuilder();
        foreach (var m in modules)
            preload.Append("  <link rel=\"modulepreload\" href=\"../js/").Append(id).Append('/').Append(m).Append("\">\n");
        html = html.Replace("\"../js/", "\"../js/" + id + "/", StringComparison.Ordinal)
                   .Replace("</head>", preload + "</head>", StringComparison.Ordinal);
        byte[] bytes = Encoding.UTF8.GetBytes(html);
        string etag = "\"" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16] + "\"";
        await _compression.WriteAsync(ctx, "page|" + etag, () => bytes, "text/html; charset=utf-8", "no-cache", etag).ConfigureAwait(false);
    }
}

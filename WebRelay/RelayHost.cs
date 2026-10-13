using System.Net;

namespace WebRelay;

/// <summary>
/// The HTTP front door (HttpListener, so no hand-rolled RFC 6455):
///   /ws                    WebSocket upgrade -> <see cref="RelaySession"/> (Origin check + per-IP budget first)
///   /play/*, /js/*         the web client from WebRoot (no-cache: edit, tsc, refresh); /play/ and /js/<build id>/* via
///                          <see cref="PageBuild"/> (hashed, immutable module URLs + modulepreload)
///   /data/*, /map/*, /sound/*  read-only client files from ClientDataPath (GET + single Range, ETag); with ?v= the
///                          checked, immutable <see cref="AssetHost"/> forms; /api/manifest and /libindex/* likewise
/// With --mount (public relay behind the Cloudflare tunnel, which forwards paths unstripped) all of these live under the
/// mount, e.g. /portal/mir2webclient/ws, and the mount itself redirects to its /play/.
/// Every handler is async on pool threads; nothing here touches game state (there is none in the relay).
/// </summary>
internal sealed class RelayHost
{
    private readonly RelayOptions _options;
    private readonly byte[]? _versionHash;
    private readonly ConnectionBudget _budget;
    private readonly PlayerGuard? _guard;
    private readonly StaticFiles _web;
    private readonly StaticFiles _clientData;
    private readonly Compression _compression = new(256L * 1024 * 1024);
    private readonly AssetHost _assets;
    private readonly PageBuild _page;
    private readonly SemaphoreSlim _httpSlots = new(64, 64);
    private int _sessionSeq;

    public RelayHost(RelayOptions options, byte[]? versionHash)
    {
        _options = options;
        _versionHash = versionHash;
        _budget = new ConnectionBudget(options.MaxPerIp, options.MaxTotal, options.ConnectsPerMinutePerIp);
        if (options.Public) _guard = new PlayerGuard(options.AccountsPerHourPerIp, options.CharactersPerHourPerIp);
        _web = new StaticFiles(new[] { options.WebRoot }, cacheImmutable: false);
        _clientData = new StaticFiles(new[] { options.ClientDataPath }, cacheImmutable: true);
        _assets = new AssetHost(options, _clientData, _compression);
        _page = new PageBuild(options.WebRoot, _compression);
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var listener = new HttpListener();
        string prefix = $"http://{_options.Listen}/";
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch (HttpListenerException ex)
        {
            Console.Error.WriteLine($"Cannot listen on {prefix}: {ex.Message} (non-loopback binds need: netsh http add urlacl url={prefix} user=Everyone)");
            return 1;
        }
        Console.WriteLine($"WebRelay listening on {prefix.TrimEnd('/')}{_options.Mount}/play/  ->  upstream {_options.UpstreamHost}:{_options.UpstreamPort}");
        Console.WriteLine($"  web root:    {_options.WebRoot}");
        Console.WriteLine($"  client data: {_options.ClientDataPath} (read-only)");
        Console.WriteLine($"  origins:     {(_options.Origins.Count > 0 ? string.Join(", ", _options.Origins) : "the relay's own origin")}");
        if (_options.Mount.Length > 0) Console.WriteLine($"  mount:       {_options.Mount}");
        if (_options.TrustProxyHeader.Length > 0) Console.WriteLine($"  client IP:   {_options.TrustProxyHeader} (from loopback peers only)");
        if (_guard != null) Console.WriteLine($"  public:      per-IP limits on (failed logins {PlayerGuard.FailedLoginLimit}/min, accounts {_options.AccountsPerHourPerIp}/h, characters {_options.CharactersPerHourPerIp}/h); the server needs [LoadTest] ExemptLoopback=True");

        using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
                _budget.PruneExpired();
                _guard?.PruneExpired();
            }
        }, ct);

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => HandleAsync(ctx, ct));
        }
        Console.WriteLine("WebRelay stopped.");
        return 0;
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        string path = ctx.Request.Url?.AbsolutePath ?? "/";
        try
        {
            string mount = _options.Mount;
            if (mount.Length > 0)
            {
                if (path == mount || path == mount + "/")
                {
                    ctx.Response.Redirect(mount + "/play/");
                    ctx.Response.Close();
                    return;
                }
                if (!path.StartsWith(mount + "/", StringComparison.Ordinal)) { Respond(ctx, 404, "not found"); return; }
                path = path[mount.Length..];
            }

            if (path == "/ws")
            {
                await HandleWebSocketAsync(ctx, ct).ConfigureAwait(false);
                return;
            }

            if (!await _httpSlots.WaitAsync(0, ct).ConfigureAwait(false))
            {
                Respond(ctx, 503, "busy");
                return;
            }
            try
            {
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                if (path == "/" || path == "/play")
                {
                    ctx.Response.Redirect(mount + "/play/");
                    ctx.Response.Close();
                }
                else if (path == "/play/" || path == "/play/index.html")
                {
                    string? index = _web.Resolve("/play/index.html");
                    if (index == null) Respond(ctx, 404, "not found");
                    else await _page.ServePageAsync(ctx, index).ConfigureAwait(false);
                }
                else if (path.StartsWith("/js/", StringComparison.Ordinal) && path.IndexOf('/', 4) is int slash && slash > 4
                         && PageBuild.IsBuildId(path[4..slash]))
                {
                    await _page.ServeModuleAsync(ctx, path[4..slash], path[(slash + 1)..], _web).ConfigureAwait(false);
                }
                else if (path.StartsWith("/play/", StringComparison.Ordinal) || path.StartsWith("/js/", StringComparison.Ordinal))
                {
                    await _web.ServeAsync(ctx, path).ConfigureAwait(false);
                }
                else if (path == "/api/manifest")
                {
                    await _assets.ServeManifestAsync(ctx).ConfigureAwait(false);
                }
                else if (path.StartsWith("/libindex/", StringComparison.OrdinalIgnoreCase))
                {
                    if (!await _assets.TryServeLibIndexAsync(ctx, path["/libindex/".Length..], ctx.Request.QueryString["v"]).ConfigureAwait(false))
                        Respond(ctx, 404, "not found");
                }
                else if (ctx.Request.QueryString["v"] is string v && (path.StartsWith("/data/", StringComparison.OrdinalIgnoreCase)
                         || path.StartsWith("/sound/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/map/", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!await _assets.TryServeVersionedAsync(ctx, path, v).ConfigureAwait(false)) Respond(ctx, 404, "not found");
                }
                else if (path.StartsWith("/data/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/sound/", StringComparison.OrdinalIgnoreCase))
                {
                    await _clientData.ServeAsync(ctx, path).ConfigureAwait(false);
                }
                else if (path.StartsWith("/map/", StringComparison.OrdinalIgnoreCase))
                {
                    await ServeMapAsync(ctx, path).ConfigureAwait(false);
                }
                else Respond(ctx, 404, "not found");
            }
            finally { _httpSlots.Release(); }
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            // client went away mid-response
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[http] {path}: {ex.GetType().Name}: {ex.Message}");
            try { Respond(ctx, 500, "error"); } catch { }
        }
    }

    /// <summary>Maps: the client's Map\ folder first, then each --map-path (a scratch server's synthetic maps).</summary>
    private async Task ServeMapAsync(HttpListenerContext ctx, string path)
    {
        if (await _clientData.TryServeAsync(ctx, path).ConfigureAwait(false)) return;
        string name = path["/map/".Length..];
        foreach (var extra in _options.ExtraMapPaths)
        {
            var files = new StaticFiles(new[] { extra }, cacheImmutable: true);
            if (await files.TryServeAsync(ctx, "/" + name).ConfigureAwait(false)) return;
        }
        Respond(ctx, 404, "not found");
    }

    private async Task HandleWebSocketAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        if (!ctx.Request.IsWebSocketRequest) { Respond(ctx, 400, "websocket only"); return; }
        if (!OriginAllowed(ctx.Request)) { Respond(ctx, 403, "origin not allowed"); return; }

        string ip = ResolveClientIp(ctx.Request.RemoteEndPoint?.Address, _options.TrustProxyHeader.Length > 0 ? ctx.Request.Headers[_options.TrustProxyHeader] : null);
        string? refusal = _guard?.CheckBlocked(ip) ?? _budget.TryClaim(ip);
        if (refusal != null) { Respond(ctx, 503, refusal); return; }

        int id = Interlocked.Increment(ref _sessionSeq);
        try
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null, receiveBufferSize: 16 * 1024, keepAliveInterval: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Console.WriteLine($"[ws {id}] open from {ip}");
            string reason = await new RelaySession(id, wsCtx.WebSocket, _options, _versionHash, ip, _guard).RunAsync(ct).ConfigureAwait(false);
            Console.WriteLine($"[ws {id}] closed: {reason}");
        }
        finally { _budget.Release(ip); }
    }

    /// <summary>
    /// The player's address: the proxy header (CF-Connecting-IP) when the peer is loopback (cloudflared on this machine) and
    /// the header is one valid address, else the peer itself. A remote peer can never pick its own address this way.
    /// </summary>
    internal static string ResolveClientIp(IPAddress? peer, string? proxyHeader)
    {
        string peerIp = peer?.ToString() ?? "?";
        if (peer == null || !IPAddress.IsLoopback(peer) || string.IsNullOrWhiteSpace(proxyHeader)) return peerIp;
        return IPAddress.TryParse(proxyHeader.Trim(), out var real) ? real.ToString() : peerIp;
    }

    private bool OriginAllowed(HttpListenerRequest request)
    {
        string? origin = request.Headers["Origin"];
        if (string.IsNullOrEmpty(origin)) return false;
        origin = origin.TrimEnd('/');
        if (_options.Origins.Count > 0) return _options.Origins.Contains(origin, StringComparer.OrdinalIgnoreCase);
        string? host = request.Headers["Host"];
        return host != null && (string.Equals(origin, "http://" + host, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(origin, "https://" + host, StringComparison.OrdinalIgnoreCase));
    }

    internal static void Respond(HttpListenerContext ctx, int status, string text)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }
}

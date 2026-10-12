using System.Net;

namespace WebRelay;

/// <summary>
/// The HTTP front door (HttpListener, so no hand-rolled RFC 6455):
///   /ws                    WebSocket upgrade -> <see cref="RelaySession"/> (Origin check + per-IP budget first)
///   /play/*, /js/*         the web client from WebRoot (no-cache: edit, tsc, refresh)
///   /data/*, /map/*, /sound/*  read-only client files from ClientDataPath (GET + single Range, ETag)
/// Every handler is async on pool threads; nothing here touches game state (there is none in the relay).
/// </summary>
internal sealed class RelayHost
{
    private readonly RelayOptions _options;
    private readonly byte[]? _versionHash;
    private readonly ConnectionBudget _budget;
    private readonly StaticFiles _web;
    private readonly StaticFiles _clientData;
    private readonly SemaphoreSlim _httpSlots = new(64, 64);
    private int _sessionSeq;

    public RelayHost(RelayOptions options, byte[]? versionHash)
    {
        _options = options;
        _versionHash = versionHash;
        _budget = new ConnectionBudget(options.MaxPerIp, options.MaxTotal, options.ConnectsPerMinutePerIp);
        _web = new StaticFiles(new[] { options.WebRoot }, cacheImmutable: false);
        _clientData = new StaticFiles(new[] { options.ClientDataPath }, cacheImmutable: true);
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
        Console.WriteLine($"WebRelay listening on {prefix}play/  ->  upstream {_options.UpstreamHost}:{_options.UpstreamPort}");
        Console.WriteLine($"  web root:    {_options.WebRoot}");
        Console.WriteLine($"  client data: {_options.ClientDataPath} (read-only)");

        using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
                _budget.PruneExpired();
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
                    ctx.Response.Redirect("/play/");
                    ctx.Response.Close();
                }
                else if (path.StartsWith("/play/", StringComparison.Ordinal) || path.StartsWith("/js/", StringComparison.Ordinal))
                {
                    string rel = path.EndsWith('/') ? path + "index.html" : path;
                    await _web.ServeAsync(ctx, rel).ConfigureAwait(false);
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

        string ip = ctx.Request.RemoteEndPoint?.Address.ToString() ?? "?";
        string? refusal = _budget.TryClaim(ip);
        if (refusal != null) { Respond(ctx, 503, refusal); return; }

        int id = Interlocked.Increment(ref _sessionSeq);
        try
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null, receiveBufferSize: 16 * 1024, keepAliveInterval: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Console.WriteLine($"[ws {id}] open from {ip}");
            string reason = await new RelaySession(id, wsCtx.WebSocket, _options, _versionHash).RunAsync(ct).ConfigureAwait(false);
            Console.WriteLine($"[ws {id}] closed: {reason}");
        }
        finally { _budget.Release(ip); }
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

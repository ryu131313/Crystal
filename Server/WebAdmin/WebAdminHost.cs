using System.Globalization;
using System.Net;

namespace Server.WebAdmin
{
    /// <summary>Static file path resolution + MIME map (pure functions; unit tested).</summary>
    public static class WebAdminStaticFiles
    {
        private static readonly Dictionary<string, string> Mime = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html; charset=utf-8",
            [".htm"] = "text/html; charset=utf-8",
            [".js"] = "application/javascript; charset=utf-8",
            [".mjs"] = "application/javascript; charset=utf-8",
            [".css"] = "text/css; charset=utf-8",
            [".json"] = "application/json; charset=utf-8",
            [".map"] = "application/json; charset=utf-8",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".svg"] = "image/svg+xml",
            [".ico"] = "image/x-icon",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
            [".ttf"] = "font/ttf",
            [".txt"] = "text/plain; charset=utf-8",
            [".md"] = "text/markdown; charset=utf-8",
        };

        public static string GetMime(string pathOrExtension)
        {
            var ext = pathOrExtension == null ? string.Empty : (pathOrExtension.StartsWith(".") ? pathOrExtension : Path.GetExtension(pathOrExtension));
            return Mime.TryGetValue(ext ?? string.Empty, out var mime) ? mime : "application/octet-stream";
        }

        /// <summary>html/js/css (and other text) must revalidate on every load so deployments show up immediately.</summary>
        public static bool IsNoCache(string path)
        {
            var ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return ext == ".html" || ext == ".htm" || ext == ".js" || ext == ".mjs" || ext == ".css" || ext == ".json" || ext == ".md" || ext == ".txt" || ext == ".map";
        }

        /// <summary>
        /// Maps a raw (percent-encoded) URL path to a full file path under <paramref name="root"/>, or null when it is unsafe:
        /// each segment is decoded individually and rejected if it is ".", "..", contains '/', '\', ':', NUL or invalid file
        /// name characters; the final full path must stay inside root (prefix check WITH a trailing separator).
        /// A path ending in '/' maps to index.html. The file is not required to exist.
        /// </summary>
        public static string Resolve(string root, string rawUrlPath)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrEmpty(rawUrlPath)) return null;

            var q = rawUrlPath.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) rawUrlPath = rawUrlPath.Substring(0, q);

            if (rawUrlPath[0] != '/') return null;

            var rawSegments = rawUrlPath.Substring(1).Split('/');
            var segments = new List<string>();
            var invalid = Path.GetInvalidFileNameChars();

            for (var i = 0; i < rawSegments.Length; i++)
            {
                var raw = rawSegments[i];

                if (raw.Length == 0)
                {
                    if (i == rawSegments.Length - 1) continue; // trailing slash → index.html
                    return null; // "//" is never legitimate
                }

                string seg;
                try
                {
                    seg = Uri.UnescapeDataString(raw);
                }
                catch
                {
                    return null;
                }

                if (seg.Length == 0 || seg == "." || seg == "..") return null;
                if (seg.IndexOf('/') >= 0 || seg.IndexOf('\\') >= 0 || seg.IndexOf(':') >= 0 || seg.IndexOf('\0') >= 0) return null;
                if (seg.IndexOfAny(invalid) >= 0) return null;
                if (seg.Trim().Length != seg.Length || seg.EndsWith(".")) return null; // Windows silently strips these: refuse
                if (seg.StartsWith("~")) return null;
                if (IsReservedDeviceName(seg)) return null; // CON, NUL, COM1.js, ... map to devices on Windows 10

                segments.Add(seg);
            }

            if (rawUrlPath.EndsWith("/") || segments.Count == 0)
                segments.Add("index.html");

            string fullRoot;
            string full;
            try
            {
                fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                full = Path.GetFullPath(Path.Combine(fullRoot, Path.Combine(segments.ToArray())));
            }
            catch
            {
                return null;
            }

            if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return null;
            return full;
        }

        /// <summary>
        /// True for Windows DOS device names (CON, PRN, AUX, NUL, CLOCK$, CONIN$, CONOUT$, COM0-9, LPT0-9 incl. superscript digits),
        /// with or without an extension ("nul.txt", "COM1.js"), case-insensitive, ignoring trailing spaces before the extension.
        /// </summary>
        public static bool IsReservedDeviceName(string segment)
        {
            if (string.IsNullOrEmpty(segment)) return false;

            var dot = segment.IndexOf('.');
            var stem = (dot >= 0 ? segment.Substring(0, dot) : segment).TrimEnd(' ').ToUpperInvariant();

            switch (stem)
            {
                case "CON":
                case "PRN":
                case "AUX":
                case "NUL":
                case "CLOCK$":
                case "CONIN$":
                case "CONOUT$":
                    return true;
            }

            if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)))
            {
                var c = stem[3];
                return (c >= '0' && c <= '9') || c == '\u00B9' || c == '\u00B2' || c == '\u00B3';
            }

            return false;
        }

        /// <summary>A plain on-disk file: not a directory, reparse point (symlink/junction) or device.</summary>
        public static bool IsOrdinaryFile(FileInfo file)
        {
            try
            {
                var attributes = file.Attributes;
                if ((int)attributes == -1) return false;
                return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Weak ETag from last-write time and length.</summary>
        public static string ETag(FileInfo file)
        {
            return "W/\"" + file.LastWriteTimeUtc.Ticks.ToString("x", CultureInfo.InvariantCulture) + "-" + file.Length.ToString("x", CultureInfo.InvariantCulture) + "\"";
        }
    }

    /// <summary>
    /// The MirCraft web admin HTTP host (System.Net.HttpListener). Independent of the world lifecycle: it keeps running while the
    /// world is stopped/rebooted, so the panel can start it again. Start failures are logged, never thrown.
    /// </summary>
    public static class WebAdminHost
    {
        private static readonly object Gate = new object();
        private static HttpListener _listener;
        private static CancellationTokenSource _cts;
        private static Task _acceptTask;
        private static SemaphoreSlim _slots;
        private static int _slotCount;
        private static WebAdminOptions _options;

        /// <summary>How long Stop() waits for in-flight requests (e.g. a Server.MirDB or Setup.ini write) before closing.</summary>
        internal static int DrainTimeoutMs = 15000;

        /// <summary>Raised (on a thread-pool thread) when an admin asks to shut Server.exe down. The WinForms host closes its main form.</summary>
        public static event Action ShutdownRequested;

        public static bool IsRunning
        {
            get
            {
                lock (Gate) return _listener != null && _listener.IsListening;
            }
        }

        /// <summary>"http://bind:port/admin/" while running, else null.</summary>
        public static string Url { get; private set; }

        public static WebAdminRouter Router { get; private set; }

        public static WebAdminOptions Options => _options;

        /// <summary>Raises <see cref="ShutdownRequested"/> asynchronously on the thread pool.</summary>
        public static void RequestShutdown()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var handler = ShutdownRequested;
                if (handler == null)
                {
                    WebAdminLog.Info("Shutdown requested but no host handler is registered.");
                    return;
                }

                foreach (var d in handler.GetInvocationList())
                {
                    try
                    {
                        ((Action)d)();
                    }
                    catch (Exception ex)
                    {
                        WebAdminLog.Error(ex, "ShutdownRequested handler failed");
                    }
                }
            });
        }

        /// <summary>Starts with Settings.WebAdmin* values. Returns false (after logging) on failure.</summary>
        public static bool Start()
        {
            WebAdminOptions options;
            try
            {
                options = WebAdminOptions.FromSettings();
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Could not read the [WebAdmin] settings; web admin not started.");
                return false;
            }

            return Start(options);
        }

        public static bool Start(WebAdminOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            lock (Gate)
            {
                if (_listener != null) return true;

                HttpListener listener = null;

                try
                {
                    WebAdminOptions.Current = options;
                    _options = options;
                    WebAdminSessions.Store.Lifetime = options.SessionLifetime;

                    WebAdminGame.EnsureHooks();
                    WebAdminPending.EnsureHooks();
                    WebAdminAdmins.EnsureHooks();
                    WebAdminAdmins.Load();

                    var router = new WebAdminRouter();
                    var count = WebAdminRouter.RegisterAll(router, typeof(WebAdminHost).Assembly);
                    Router = router;

                    if (!Directory.Exists(options.WebRoot))
                        WebAdminLog.Info("Web root not found: " + options.WebRoot + " (static pages will 404).");

                    listener = new HttpListener();
                    listener.Prefixes.Add(options.Prefix);
                    listener.IgnoreWriteExceptions = true;
                    listener.Start();

                    _listener = listener;
                    _slotCount = Math.Clamp(options.MaxConcurrentRequests, 4, 512);
                    _slots = new SemaphoreSlim(_slotCount);
                    _cts = new CancellationTokenSource();

                    var token = _cts.Token;
                    var slots = _slots;
                    _acceptTask = Task.Run(() => AcceptLoopAsync(listener, slots, token));

                    Url = options.DisplayUrl;
                    WebAdminLog.Info($"MirCraft web admin listening on {options.DisplayUrl} ({count} route modules, {router.Routes.Count} routes)");
                    return true;
                }
                catch (HttpListenerException ex)
                {
                    SafeClose(listener);
                    _listener = null;

                    var hint = $"netsh http add urlacl url=http://{(options.Prefix.Contains("://+:") ? "+" : WebAdminOptions.ListenerHost(options.Bind))}:{options.Port}/ user=Everyone";
                    WebAdminLog.Info($"MirCraft web admin could not listen on {options.Prefix} (error {ex.ErrorCode}: {ex.Message}). " +
                                     $"Binding to a non-loopback address needs a URL reservation (run as admin: {hint}) or use Bind=127.0.0.1; also check the port is free.");
                    return false;
                }
                catch (Exception ex)
                {
                    SafeClose(listener);
                    _listener = null;
                    WebAdminLog.Error(ex, "MirCraft web admin failed to start.");
                    return false;
                }
            }
        }

        public static void Stop()
        {
            Task accept;
            HttpListener listener;
            SemaphoreSlim slots;
            int slotCount;

            lock (Gate)
            {
                if (_listener == null) return;

                try
                {
                    _cts?.Cancel(); // the accept loop now rejects new requests with 503
                }
                catch
                {
                }

                listener = _listener;
                slots = _slots;
                slotCount = _slotCount;
                _listener = null;
                accept = _acceptTask;
                _acceptTask = null;
                Url = null;
            }

            // Let in-flight handlers finish before the process exits: one may be writing Server.MirDB, Server.MirADB, Setup.ini or a
            // restore swap, and the host process ends right after Stop() (thread-pool threads die mid-write).
            DrainInFlight(slots, slotCount);

            SafeClose(listener);

            try
            {
                accept?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // accept loop faults are irrelevant at shutdown
            }

            WebAdminLog.Info("MirCraft web admin stopped.");
        }

        private static void DrainInFlight(SemaphoreSlim slots, int slotCount)
        {
            if (slots == null || slotCount <= 0) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (slots.CurrentCount < slotCount)
            {
                if (sw.ElapsedMilliseconds >= DrainTimeoutMs)
                {
                    WebAdminLog.Info("Web admin stop: " + (slotCount - slots.CurrentCount) + " request(s) still running after " + DrainTimeoutMs + " ms; closing anyway.");
                    return;
                }

                Thread.Sleep(25);
            }
        }

        private static void SafeClose(HttpListener listener)
        {
            if (listener == null) return;

            try
            {
                if (listener.IsListening) listener.Stop();
            }
            catch
            {
            }

            try
            {
                // Without Close() http.sys keeps the prefix registered.
                listener.Close();
            }
            catch
            {
            }
        }

        private static async Task AcceptLoopAsync(HttpListener listener, SemaphoreSlim slots, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context;

                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    if (token.IsCancellationRequested || !listener.IsListening) break;
                    await Task.Delay(50).ConfigureAwait(false);
                    continue;
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) break;
                    WebAdminLog.Error(ex, "Web admin accept loop error");
                    await Task.Delay(250).ConfigureAwait(false);
                    continue;
                }

                if (token.IsCancellationRequested)
                {
                    // Shutting down: no new work (Stop() is draining the requests already running).
                    RejectBusy(context);
                    break;
                }

                if (!slots.Wait(0))
                {
                    RejectBusy(context);
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessAsync(context, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        slots.Release();
                    }
                });
            }
        }

        private static void RejectBusy(HttpListenerContext context)
        {
            try
            {
                var bytes = WebAdminJson.SerializeToUtf8Bytes(new WebAdminRequest.ApiResult { Ok = false, Message = "Server busy, retry shortly" });
                context.Response.StatusCode = 503;
                context.Response.Headers["Retry-After"] = "1";
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch
            {
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch
                {
                }
            }
        }

        // ------------------------------------------------------------------------------------------- dispatch

        private static async Task ProcessAsync(HttpListenerContext context, CancellationToken token)
        {
            WebAdminRequest req = null;

            try
            {
                var options = _options ?? new WebAdminOptions();
                req = new WebAdminRequest(context, options.MaxBodyBytes, token);

                ApplySecurityHeaders(context.Response);

                var path = req.RawPath;

                if (req.Method == "OPTIONS")
                {
                    req.SetHeader("Allow", "GET, POST, PUT, PATCH, DELETE");
                    await req.Status(204);
                    return;
                }

                if (path == "/" || path.Length == 0)
                {
                    await req.Redirect("/admin/");
                    return;
                }

                if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || string.Equals(path, "/api", StringComparison.OrdinalIgnoreCase))
                {
                    req.SetHeader("Cache-Control", "no-store");
                    await HandleApiAsync(req).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(path, "/admin", StringComparison.OrdinalIgnoreCase))
                {
                    await req.Redirect("/admin/");
                    return;
                }

                if (path.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/shared/", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, "/favicon.ico", StringComparison.OrdinalIgnoreCase))
                {
                    await ServeStaticAsync(req, options).ConfigureAwait(false);
                    return;
                }

                await req.Text("Not found", status: 404);
            }
            catch (WebAdminException wex)
            {
                if (req != null && !req.Responded)
                {
                    if (wex.Status == 429 || wex.Status == 503) req.SetHeader("Retry-After", "1");
                    await req.Error(wex.Status, wex.Message, wex.Payload);
                }
            }
            catch (HttpListenerException)
            {
                // client disconnected
            }
            catch (ObjectDisposedException)
            {
                // listener stopped
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, $"Web admin request failed: {req?.Method} {req?.Path}");

                if (req != null && !req.Responded)
                {
                    try
                    {
                        await req.Error(500, "Internal error, see server log");
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                if (req == null)
                {
                    try
                    {
                        context.Response.StatusCode = 500;
                        context.Response.Close();
                    }
                    catch
                    {
                    }
                }
                else if (!req.Responded)
                {
                    try
                    {
                        await req.Status(204);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void ApplySecurityHeaders(HttpListenerResponse response)
        {
            try
            {
                response.Headers["X-Content-Type-Options"] = "nosniff";
                response.Headers["X-Frame-Options"] = "DENY";
                response.Headers["Referrer-Policy"] = "same-origin";
                response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
            }
            catch
            {
            }
        }

        private readonly struct SessionResolution
        {
            public SessionResolution(WebAdminSession session, bool revokedNotAdmin)
            {
                Session = session;
                RevokedNotAdmin = revokedNotAdmin;
            }

            public WebAdminSession Session { get; }
            public bool RevokedNotAdmin { get; }
        }

        /// <summary>
        /// Resolves the session from the cookie and re-checks that the account is still an active admin (directory + periodic live
        /// AccountInfo check while the world runs). A demoted/banned account loses every session.
        /// </summary>
        private static async Task<SessionResolution> ResolveSessionAsync(WebAdminRequest req)
        {
            var token = req.Cookie(WebAdminSessions.CookieName);
            if (string.IsNullOrEmpty(token)) return new SessionResolution(null, false);

            var store = WebAdminSessions.Store;
            var session = store.Validate(token);
            if (session == null) return new SessionResolution(null, false);

            if (!await WebAdminAdmins.IsSessionStillAdminAsync(session, DateTime.UtcNow).ConfigureAwait(false))
            {
                store.RevokeAccount(session.AccountIndex);
                store.RevokeToken(token);
                return new SessionResolution(null, true);
            }

            if (store.NeedsCookieRefresh(session))
                WebAdminSessions.WriteSessionCookies(req, session, store.Lifetime);

            return new SessionResolution(session, false);
        }

        private static async Task HandleApiAsync(WebAdminRequest req)
        {
            var router = Router;
            if (router == null)
            {
                await req.Error(503, "Web admin is starting");
                return;
            }

            var match = router.Match(req.Method, req.RawPath);
            if (!match.Success)
            {
                if (match.MethodNotAllowed)
                {
                    req.SetHeader("Allow", string.Join(", ", match.AllowedMethods));
                    await req.Error(405, "Method not allowed");
                }
                else
                {
                    await req.Error(404, "API route not found");
                }

                return;
            }

            req.Route = match.Values;
            var resolved = await ResolveSessionAsync(req).ConfigureAwait(false);
            req.Session = resolved.Session;
            var revokedNotAdmin = resolved.RevokedNotAdmin;

            if (match.Route.Auth == RouteAuth.Admin)
            {
                if (req.Session == null)
                {
                    if (revokedNotAdmin)
                    {
                        WebAdminSessions.WriteClearCookies(req);
                        await req.Error(403, "Admin privileges required");
                    }
                    else
                    {
                        await req.Error(401, "Not signed in");
                    }

                    return;
                }

                if (req.IsMutating &&
                    !WebAdminSessionStore.ValidateCsrf(req.Session, req.Header(WebAdminSessions.CsrfHeaderName), req.Cookie(WebAdminSessions.CsrfCookieName)))
                {
                    await req.Error(403, "CSRF validation failed");
                    return;
                }
            }

            await InvokeRouteAsync(match.Route.Auth, req.IsMutating, () => match.Route.Handler(req)).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs a matched route's handler. An admin write (POST/PUT/PATCH/DELETE) runs with <see cref="WebAdminGame.InMutatingRequest"/>
        /// set, so every game-thread or inline delegate it runs invalidates the offline account cache (P9-12). Async, so the flag
        /// stays scoped to this request's flow.
        /// </summary>
        internal static async Task InvokeRouteAsync(RouteAuth auth, bool mutating, Func<Task> handler)
        {
            if (!(auth == RouteAuth.Admin && mutating))
            {
                await handler().ConfigureAwait(false);
                return;
            }

            WebAdminGame.InMutatingRequest = true;
            try
            {
                await handler().ConfigureAwait(false);
            }
            finally
            {
                WebAdminGame.InMutatingRequest = false; // also ends it for timers / Task.Run callbacks the handler started
            }
        }

        private static async Task ServeStaticAsync(WebAdminRequest req, WebAdminOptions options)
        {
            if (req.Method != "GET" && req.Method != "HEAD")
            {
                req.SetHeader("Allow", "GET, HEAD");
                await req.Text("Method not allowed", status: 405);
                return;
            }

            var raw = req.RawPath;

            if (raw.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase) && !IsAnonymousAdminAsset(raw))
            {
                var session = (await ResolveSessionAsync(req).ConfigureAwait(false)).Session;
                if (session == null)
                {
                    var next = raw + (req.Context.Request.Url?.Query ?? string.Empty);
                    await req.Redirect("/admin/login.html?next=" + Uri.EscapeDataString(next));
                    return;
                }
            }

            var full = WebAdminStaticFiles.Resolve(options.WebRoot, raw);
            if (full == null)
            {
                await req.Text("Forbidden", status: 403);
                return;
            }

            var file = new FileInfo(full);
            if (!file.Exists || !WebAdminStaticFiles.IsOrdinaryFile(file))
            {
                await req.Text("Not found", status: 404);
                return;
            }

            var etag = WebAdminStaticFiles.ETag(file);
            req.SetHeader("ETag", etag);
            req.SetHeader("Last-Modified", file.LastWriteTimeUtc.ToString("R", CultureInfo.InvariantCulture));
            req.SetHeader("Cache-Control", WebAdminStaticFiles.IsNoCache(full) ? "no-cache" : "public, max-age=3600");

            var ifNoneMatch = req.Header("If-None-Match");
            if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch.Split(',').Any(t => t.Trim() == etag || t.Trim() == "*"))
            {
                await req.Status(304);
                return;
            }

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(full, req.CancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                await req.Text("Not found", status: 404);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                await req.Text("Forbidden", status: 403);
                return;
            }

            req.WriteBody(200, WebAdminStaticFiles.GetMime(full), bytes, null);
        }

        /// <summary>/admin/login.html is reachable without a session (everything else under /admin/ needs one).</summary>
        internal static bool IsAnonymousAdminAsset(string rawPath)
        {
            var q = rawPath.IndexOf('?');
            if (q >= 0) rawPath = rawPath.Substring(0, q);
            return string.Equals(rawPath, "/admin/login.html", StringComparison.OrdinalIgnoreCase);
        }
    }
}

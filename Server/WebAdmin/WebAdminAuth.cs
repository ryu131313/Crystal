using System.Text.Json;
using Server.MirEnvir;

namespace Server.WebAdmin
{
    /// <summary>
    /// /api/auth/login, /api/auth/logout, /api/auth/me. Only admin accounts (AccountInfo.AdminAccount, not banned) can sign in.
    /// Never touches the game's WrongPasswordCount lockout.
    /// </summary>
    public sealed class WebAdminAuth : IWebAdminRoutes
    {
        public const int MaxAccountIdLength = 64;
        public const int MaxPasswordLength = 128;

        private const string InvalidCredentials = "Invalid account or password";

        private static int _hooked;

        public void Register(WebAdminRouter router)
        {
            if (Interlocked.Exchange(ref _hooked, 1) == 0)
                WebAdminAdmins.EnsureHooks();

            router.Post("/api/auth/login", LoginAsync, RouteAuth.Anonymous);
            router.Post("/api/auth/logout", LogoutAsync, RouteAuth.Anonymous);
            router.Get("/api/auth/me", MeAsync, RouteAuth.Anonymous);
        }

        /// <summary>Crystal's password check: PBKDF2(password, salt) compared ordinally with the stored hash string.</summary>
        public static bool VerifyPassword(string password, string storedHash, byte[] salt)
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash) || salt == null) return false;

            try
            {
                return string.CompareOrdinal(storedHash, global::Server.Utils.Crypto.HashPassword(password, salt)) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Login CSRF / cross-site guard. Null when the request may proceed, else "content-type" (415) or "cross-site" (403).
        /// Requires Content-Type: application/json (an HTML form cannot send it cross-site without a CORS preflight, which this host
        /// never grants). Browsers that send Sec-Fetch-Site must report same-origin (or none: typed/bookmarked); otherwise an
        /// Origin header, when present, must match the Host header.
        /// </summary>
        internal static string CheckLoginRequest(string contentType, string secFetchSite, string origin, string host)
        {
            var mediaType = (contentType ?? string.Empty).Split(';')[0].Trim();
            if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)) return "content-type";

            if (!string.IsNullOrWhiteSpace(secFetchSite))
            {
                var site = secFetchSite.Trim();
                return string.Equals(site, "same-origin", StringComparison.OrdinalIgnoreCase) || string.Equals(site, "none", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : "cross-site";
            }

            if (!string.IsNullOrWhiteSpace(origin))
            {
                if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var o) || string.IsNullOrWhiteSpace(host)) return "cross-site";
                if (!string.Equals(o.Authority, host.Trim(), StringComparison.OrdinalIgnoreCase)) return "cross-site";
            }

            return null;
        }

        private sealed class LoginBody
        {
            public string AccountId { get; set; }
            public string Password { get; set; }
        }

        private sealed class OnlineCheck
        {
            public string Reason;
            public WebAdminAdminEntry Entry;
        }

        private static async Task LoginAsync(WebAdminRequest req)
        {
            var options = WebAdminOptions.Current ?? new WebAdminOptions();
            var limiter = WebAdminRateLimiter.Shared;

            var refusal = CheckLoginRequest(req.Header("Content-Type"), req.Header("Sec-Fetch-Site"), req.Header("Origin"), req.Header("Host"));
            if (refusal == "content-type")
            {
                await req.Error(415, "Content-Type must be application/json");
                return;
            }

            if (refusal != null)
            {
                WebAdminAudit.Record("anonymous", req.ClientIp, "auth.login-cross-site", null);
                await req.Error(403, "Cross-site sign-in refused");
                return;
            }

            if (!limiter.TryAcquire("login|" + req.ClientIp, options.LoginPerIpPerMinute, TimeSpan.FromMinutes(1), out var retry) ||
                !limiter.TryAcquire("login-global", options.LoginGlobalPerMinute, TimeSpan.FromMinutes(1), out retry))
            {
                req.SetHeader("Retry-After", retry.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await req.Error(429, "Too many sign-in attempts. Try again shortly.", new { retryAfter = retry });
                return;
            }

            var body = await req.ReadJsonAsync<LoginBody>();
            var accountId = body?.AccountId?.Trim();
            var password = body?.Password;

            if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(password))
            {
                await req.Error(400, "Account and password are required");
                return;
            }

            if (accountId.Length > MaxAccountIdLength || password.Length > MaxPasswordLength)
            {
                await req.Error(401, InvalidCredentials);
                return;
            }

            // Keyed per (account, IP): an attacker can only lock their own IP out of an account, never the real admin.
            var failKey = "login-fail|" + accountId.ToLowerInvariant() + "|" + req.ClientIp;
            var failWindow = TimeSpan.FromMinutes(Math.Max(1, options.LoginFailureWindowMinutes));

            if (limiter.IsExhausted(failKey, options.LoginFailuresPerAccount, failWindow, out retry))
            {
                req.SetHeader("Retry-After", retry.ToString(System.Globalization.CultureInfo.InvariantCulture));
                WebAdminAudit.Record(accountId, req.ClientIp, "auth.login-throttled", accountId);
                await req.Error(429, "Too many failed sign-in attempts for this account. Try again later.", new { retryAfter = retry });
                return;
            }

            var nowUtc = DateTime.UtcNow;
            WebAdminAdminEntry entry = null;
            string reason = null;
            var checkedOnline = false;

            if (WebAdminGame.World.QueueOpen)
            {
                try
                {
                    // Snapshot on the game thread; hash off-thread.
                    var check = await WebAdminGame.RunAsync(() =>
                    {
                        var account = Envir.Main.GetAccount(accountId);
                        if (account == null) return new OnlineCheck { Reason = "no-account" };
                        if (!account.AdminAccount) return new OnlineCheck { Reason = "not-admin" };
                        if (account.Banned && account.ExpiryDate > Envir.Main.Now) return new OnlineCheck { Reason = "banned" };
                        if (account.RequirePasswordChange) return new OnlineCheck { Reason = "password-change-required" };
                        return new OnlineCheck { Entry = WebAdminAdminEntry.From(account) };
                    });

                    checkedOnline = true;
                    reason = check.Reason;

                    if (check.Entry != null)
                    {
                        if (VerifyPassword(password, check.Entry.PasswordHash, check.Entry.Salt)) entry = check.Entry;
                        else reason = "bad-password";
                    }
                }
                catch (WorldNotRunningException)
                {
                    checkedOnline = false; // stopped meanwhile: fall back to the offline directory
                }
            }

            if (!checkedOnline)
                entry = WebAdminAdmins.TryAuthenticate(accountId, password, nowUtc, out reason);

            if (entry == null)
            {
                limiter.Hit(failKey, failWindow);
                WebAdminAudit.Record(accountId, req.ClientIp, "auth.login-failed", accountId, new { reason = reason ?? "unknown", online = checkedOnline });
                await req.Error(401, InvalidCredentials);
                return;
            }

            limiter.Reset(failKey);

            if (checkedOnline) WebAdminAdmins.Upsert(entry); // keeps the per-request admin check in sync for freshly promoted admins

            var store = WebAdminSessions.Store;
            var session = store.Create(entry.Index, entry.AccountId, req.ClientIp, req.UserAgent);
            WebAdminSessions.WriteSessionCookies(req, session, store.Lifetime);
            req.Session = session;

            WebAdminAudit.Record(entry.AccountId, req.ClientIp, "auth.login", entry.AccountId, new { online = checkedOnline, sessionId = session.SessionId });

            await req.Ok(new { accountId = entry.AccountId, csrf = session.Csrf });
        }

        private static async Task LogoutAsync(WebAdminRequest req)
        {
            var token = req.Cookie(WebAdminSessions.CookieName);
            var session = WebAdminSessions.Store.Validate(token, touch: false);

            if (session != null)
            {
                var csrfOk = WebAdminSessionStore.ValidateCsrf(session, req.Header(WebAdminSessions.CsrfHeaderName), req.Cookie(WebAdminSessions.CsrfCookieName));
                if (!csrfOk)
                {
                    await req.Error(403, "CSRF validation failed");
                    return;
                }

                WebAdminSessions.Store.RevokeToken(token);
                WebAdminAudit.Record(session.AccountId, req.ClientIp, "auth.logout", session.AccountId);
            }

            WebAdminSessions.WriteClearCookies(req);
            await req.Ok(null, "Signed out");
        }

        private static Task MeAsync(WebAdminRequest req)
        {
            var session = req.Session;

            if (session == null)
            {
                return req.Json(new
                {
                    authenticated = false,
                    admin = false,
                    world = WebAdminGame.WorldState,
                });
            }

            return req.Json(new
            {
                authenticated = true,
                admin = true,
                accountId = session.AccountId,
                csrf = session.Csrf,
                world = WebAdminGame.WorldState,
            });
        }
    }
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Server.WebAdmin
{
    public sealed class WebAdminSession
    {
        internal WebAdminSession(string sessionId, string token, string csrf, int accountIndex, string accountId, string ip, string userAgent, DateTime nowUtc, TimeSpan lifetime)
        {
            SessionId = sessionId;
            Token = token;
            Csrf = csrf;
            AccountIndex = accountIndex;
            AccountId = accountId;
            Ip = ip;
            UserAgent = userAgent;
            CreatedUtc = nowUtc;
            LastSeenUtc = nowUtc;
            CookieRefreshedUtc = nowUtc;
            ExpiresUtc = nowUtc + lifetime;
        }

        /// <summary>Public, non-secret id (safe to list / revoke by). The cookie token is never exposed.</summary>
        public string SessionId { get; }

        /// <summary>Secret cookie value.</summary>
        internal string Token { get; }

        /// <summary>CSRF token bound to this session (also in the MirCraftAdminCsrf cookie).</summary>
        public string Csrf { get; }

        public int AccountIndex { get; }
        public string AccountId { get; }
        public string Ip { get; }
        public string UserAgent { get; }
        public DateTime CreatedUtc { get; }
        public DateTime LastSeenUtc { get; internal set; }
        public DateTime ExpiresUtc { get; internal set; }
        internal DateTime CookieRefreshedUtc { get; set; }

        /// <summary>Last time the admin flags were re-checked against the live AccountInfo (ticks, UTC; 0 = never).</summary>
        internal long LiveCheckedTicks;
    }

    /// <summary>
    /// In-memory session store (a process restart signs everyone out). Sliding expiry (<see cref="Lifetime"/>) capped by an absolute
    /// maximum age (<see cref="AbsoluteLifetime"/>, Lifetime x 3) so a polling tab or a replayed cookie cannot keep a session alive forever.
    /// Thread-safe.
    /// </summary>
    public sealed class WebAdminSessionStore
    {
        private readonly ConcurrentDictionary<string, WebAdminSession> _byToken = new ConcurrentDictionary<string, WebAdminSession>(StringComparer.Ordinal);
        private readonly Func<DateTime> _clock;
        private long _lifetimeTicks;

        public const int MaxSessionsPerAccount = 10;
        public const int MaxSessions = 1000;

        public WebAdminSessionStore(TimeSpan lifetime, Func<DateTime> clock = null)
        {
            Lifetime = lifetime;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public TimeSpan Lifetime
        {
            get => TimeSpan.FromTicks(Interlocked.Read(ref _lifetimeTicks));
            set => Interlocked.Exchange(ref _lifetimeTicks, (value <= TimeSpan.Zero ? TimeSpan.FromHours(8) : value).Ticks);
        }

        /// <summary>Hard cap on a session's total age regardless of activity.</summary>
        public TimeSpan AbsoluteLifetime => TimeSpan.FromTicks(Lifetime.Ticks * 3);

        public int Count
        {
            get
            {
                Sweep();
                return _byToken.Count;
            }
        }

        public WebAdminSession Create(int accountIndex, string accountId, string ip, string userAgent = null)
        {
            Sweep();

            var now = _clock();
            var session = new WebAdminSession(NewToken(12), NewToken(32), NewToken(32), accountIndex, accountId, ip, Truncate(userAgent, 200), now, Lifetime);

            // Cap per-account sessions: drop the oldest beyond the limit.
            var mine = _byToken.Values.Where(s => s.AccountIndex == accountIndex).OrderBy(s => s.LastSeenUtc).ToList();
            for (var i = 0; i <= mine.Count - MaxSessionsPerAccount; i++)
                _byToken.TryRemove(mine[i].Token, out _);

            // Global cap (defensive).
            if (_byToken.Count >= MaxSessions)
            {
                foreach (var old in _byToken.Values.OrderBy(s => s.LastSeenUtc).Take(_byToken.Count - MaxSessions + 1).ToList())
                    _byToken.TryRemove(old.Token, out _);
            }

            _byToken[session.Token] = session;
            return session;
        }

        /// <summary>Returns the live session for a cookie token, or null (unknown/expired; expired ones are removed). Slides expiry when touch is true.</summary>
        public WebAdminSession Validate(string token, bool touch = true)
        {
            if (string.IsNullOrEmpty(token) || token.Length > 256) return null;
            if (!_byToken.TryGetValue(token, out var session)) return null;

            var now = _clock();
            var hardExpiry = session.CreatedUtc + AbsoluteLifetime;
            if (session.ExpiresUtc <= now || hardExpiry <= now)
            {
                _byToken.TryRemove(token, out _);
                return null;
            }

            if (touch)
            {
                session.LastSeenUtc = now;
                var slid = now + Lifetime;
                session.ExpiresUtc = slid < hardExpiry ? slid : hardExpiry;
            }

            return session;
        }

        /// <summary>True when the cookies should be re-sent so the browser Max-Age slides too (at most every 10 minutes).</summary>
        internal bool NeedsCookieRefresh(WebAdminSession session)
        {
            if (session == null) return false;

            var now = _clock();
            if (now - session.CookieRefreshedUtc < TimeSpan.FromMinutes(10)) return false;

            session.CookieRefreshedUtc = now;
            return true;
        }

        public WebAdminSession FindById(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return null;
            return _byToken.Values.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
        }

        public bool Revoke(string sessionId)
        {
            var session = FindById(sessionId);
            return session != null && _byToken.TryRemove(session.Token, out _);
        }

        public bool RevokeToken(string token)
        {
            return !string.IsNullOrEmpty(token) && _byToken.TryRemove(token, out _);
        }

        public int RevokeAccount(int accountIndex)
        {
            var count = 0;
            foreach (var s in _byToken.Values.Where(s => s.AccountIndex == accountIndex).ToList())
            {
                if (_byToken.TryRemove(s.Token, out _)) count++;
            }

            return count;
        }

        /// <summary>Snapshot of live sessions, most recently seen first.</summary>
        public IReadOnlyList<WebAdminSession> List()
        {
            Sweep();
            return _byToken.Values.OrderByDescending(s => s.LastSeenUtc).ToList();
        }

        public void Sweep()
        {
            var now = _clock();
            var absolute = AbsoluteLifetime;
            foreach (var pair in _byToken)
            {
                if (pair.Value.ExpiresUtc <= now || pair.Value.CreatedUtc + absolute <= now)
                    _byToken.TryRemove(pair.Key, out _);
            }
        }

        /// <summary>CSRF double-submit check: header == cookie == session token (all non-empty), constant-time.</summary>
        public static bool ValidateCsrf(WebAdminSession session, string headerValue, string cookieValue)
        {
            if (session == null || string.IsNullOrEmpty(session.Csrf)) return false;
            if (string.IsNullOrEmpty(headerValue) || string.IsNullOrEmpty(cookieValue)) return false;

            return FixedEquals(headerValue, cookieValue) && FixedEquals(headerValue, session.Csrf);
        }

        /// <summary>Cryptographically random token, lowercase hex (2 chars per byte).</summary>
        public static string NewToken(int bytes = 32)
        {
            return Convert.ToHexString(RandomNumberGenerator.GetBytes(Math.Max(8, bytes))).ToLowerInvariant();
        }

        private static bool FixedEquals(string a, string b)
        {
            var ab = Encoding.UTF8.GetBytes(a);
            var bb = Encoding.UTF8.GetBytes(b);
            return ab.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ab, bb);
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }

    /// <summary>Process-wide session store + cookie helpers.</summary>
    public static class WebAdminSessions
    {
        public const string CookieName = "MirCraftAdmin";
        public const string CsrfCookieName = "MirCraftAdminCsrf";
        public const string CsrfHeaderName = "X-MirCraft-CSRF";

        public static WebAdminSessionStore Store { get; internal set; } = new WebAdminSessionStore(TimeSpan.FromHours(8));

        /// <summary>"name=value; Path=/; [HttpOnly; ]SameSite=Strict[; Max-Age=n]"</summary>
        public static string BuildCookie(string name, string value, bool httpOnly, int? maxAgeSeconds = null)
        {
            var sb = new StringBuilder();
            sb.Append(name).Append('=').Append(Uri.EscapeDataString(value ?? string.Empty)).Append("; Path=/");
            if (httpOnly) sb.Append("; HttpOnly");
            sb.Append("; SameSite=Strict");
            if (maxAgeSeconds.HasValue) sb.Append("; Max-Age=").Append(Math.Max(0, maxAgeSeconds.Value));
            return sb.ToString();
        }

        public static string ClearCookie(string name, bool httpOnly)
        {
            return name + "=; Path=/" + (httpOnly ? "; HttpOnly" : string.Empty) + "; SameSite=Strict; Max-Age=0";
        }

        /// <summary>Parses a Cookie request header; returns the (unescaped) value of the first cookie with that name, or null.</summary>
        public static string GetCookie(string cookieHeader, string name)
        {
            if (string.IsNullOrEmpty(cookieHeader) || string.IsNullOrEmpty(name)) return null;

            foreach (var part in cookieHeader.Split(';'))
            {
                var p = part.Trim();
                var eq = p.IndexOf('=');
                if (eq <= 0) continue;

                if (!string.Equals(p.Substring(0, eq).Trim(), name, StringComparison.Ordinal)) continue;

                var value = p.Substring(eq + 1).Trim();
                if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"') value = value.Substring(1, value.Length - 2);

                try
                {
                    return Uri.UnescapeDataString(value);
                }
                catch
                {
                    return value;
                }
            }

            return null;
        }

        /// <summary>Adds Set-Cookie headers for a freshly created/refreshed session.</summary>
        internal static void WriteSessionCookies(WebAdminRequest req, WebAdminSession session, TimeSpan lifetime)
        {
            var maxAge = (int)Math.Min(int.MaxValue, lifetime.TotalSeconds);
            req.AddCookie(BuildCookie(CookieName, session.Token, true, maxAge));
            req.AddCookie(BuildCookie(CsrfCookieName, session.Csrf, false, maxAge));
        }

        internal static void WriteClearCookies(WebAdminRequest req)
        {
            req.AddCookie(ClearCookie(CookieName, true));
            req.AddCookie(ClearCookie(CsrfCookieName, false));
        }
    }
}

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Detached copy of one AccountInfo (taken on the game thread / under AccountLock). Never holds live objects.
    /// </summary>
    public sealed class AccountSnapshot
    {
        public int Index { get; set; }
        public string AccountId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool Admin { get; set; }

        /// <summary>Effective ban: Banned flag set and the expiry is still in the future.</summary>
        public bool Banned { get; set; }

        public string BanReason { get; set; } = string.Empty;
        public DateTime BanExpiry { get; set; }
        public uint Gold { get; set; }
        public uint Credit { get; set; }
        public string CreationIp { get; set; } = string.Empty;
        public DateTime CreationDate { get; set; }
        public string LastIp { get; set; } = string.Empty;
        public DateTime LastDate { get; set; }

        /// <summary>Characters that are not deleted.</summary>
        public int CharacterCount { get; set; }

        public int DeletedCount { get; set; }

        /// <summary>A character of this account is in the game.</summary>
        public bool Online { get; set; }

        /// <summary>The account has a client connection (character select or in game).</summary>
        public bool Connected { get; set; }

        public string OnlineCharacter { get; set; }
        public bool RequirePasswordChange { get; set; }

        [JsonIgnore]
        public List<string> CharacterNames { get; set; } = new List<string>();
    }

    /// <summary>Search / filter / sort / page options for the account list.</summary>
    public sealed class AccountQueryOptions
    {
        public string Q { get; set; }

        /// <summary>account | player | ip</summary>
        public string Mode { get; set; } = "account";

        public bool Exact { get; set; }

        /// <summary>all | banned | admin | online | requirepw</summary>
        public string Filter { get; set; } = "all";

        /// <summary>index | accountId | userName | email | gold | credit | characters | created | lastDate | lastIp | creationIp | status</summary>
        public string Sort { get; set; } = "index";

        public bool Desc { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 50;
    }

    public sealed class AccountQueryResult
    {
        public List<AccountSnapshot> Rows { get; set; } = new List<AccountSnapshot>();
        public int Total { get; set; }
        public int Page { get; set; }
        public int Pages { get; set; }
        public int PageSize { get; set; }
    }

    public sealed class SharedIpAccount
    {
        public int Index { get; set; }
        public string AccountId { get; set; }
        public bool Banned { get; set; }
        public bool Admin { get; set; }

        /// <summary>"last", "creation" or "both".</summary>
        public string Via { get; set; }

        public DateTime LastDate { get; set; }
    }

    public sealed class SharedIpGroup
    {
        public string Ip { get; set; }
        public int Count => Accounts.Count;
        public List<SharedIpAccount> Accounts { get; set; } = new List<SharedIpAccount>();
    }

    /// <summary>Pure account rules and computations (no Envir / Settings access; unit-tested).</summary>
    public static class AccountLogic
    {
        public const int MaxPageSize = 500;
        public const int MaxUserNameLength = 20;
        public const int MaxEmailLength = 50;
        public const int MaxSecretLength = 30;
        public const int MaxBanReasonLength = 200;
        public const long MaxBanMinutes = 100L * 365 * 24 * 60;

        // Same expressions as Envir's private AccountIDReg / PasswordReg / EMailReg (Envir.cs static ctor).
        public static readonly Regex AccountIdRegex = new Regex(@"^[A-Za-z0-9]{" + Globals.MinAccountIDLength + "," + Globals.MaxAccountIDLength + "}$");
        public static readonly Regex PasswordRegex = new Regex(@"^[A-Za-z0-9]{" + Globals.MinPasswordLength + "," + Globals.MaxPasswordLength + "}$");
        public static readonly Regex EmailRegex = new Regex(@"\w+([-+.]\w+)*@\w+([-.]\w+)*\.\w+([-.]\w+)*");

        public static string ValidateAccountId(string accountId)
        {
            if (string.IsNullOrEmpty(accountId)) return "Account ID is required.";
            if (!AccountIdRegex.IsMatch(accountId))
                return "Account ID must be " + Globals.MinAccountIDLength + "-" + Globals.MaxAccountIDLength + " letters or digits.";
            return null;
        }

        public static string ValidatePassword(string password)
        {
            if (string.IsNullOrEmpty(password)) return "Password is required.";
            if (!PasswordRegex.IsMatch(password))
                return "Password must be " + Globals.MinPasswordLength + "-" + Globals.MaxPasswordLength + " letters or digits.";
            return null;
        }

        /// <summary>Empty is allowed (Crystal accepts a blank address); otherwise the game's e-mail pattern and length.</summary>
        public static string ValidateEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return null;
            if (email.Length > MaxEmailLength) return "E-mail must be at most " + MaxEmailLength + " characters.";
            if (!EmailRegex.IsMatch(email)) return "E-mail address is not valid.";
            return null;
        }

        public static string ValidateLength(string value, int max, string label)
        {
            if (value != null && value.Length > max) return label + " must be at most " + max + " characters.";
            return null;
        }

        /// <summary>
        /// Parses a birth date ("yyyy-MM-dd" or any ISO date). Empty → DateTime.MinValue (not set). Returns an error or null.
        /// </summary>
        public static string ParseBirthDate(string text, out DateTime value)
        {
            value = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(text)) return null;

            text = text.Trim();
            if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ||
                DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
            {
                if (d.Year < 1900 || d.Year > 2100) return "Birth date is out of range.";
                value = DateTime.SpecifyKind(d.Date, DateTimeKind.Unspecified);
                return null;
            }

            return "Birth date must look like 1990-12-31.";
        }

        /// <summary>
        /// Ban expiry from exactly one of: permanent, days, minutes, until (ISO date/time, UTC unless it carries an offset).
        /// Permanent = DateTime.MaxValue (as the WinForms Perm Ban). Returns an error or null.
        /// </summary>
        public static string ResolveBanExpiry(DateTime nowUtc, bool permanent, double? days, long? minutes, string until, out DateTime expiry)
        {
            expiry = DateTime.MinValue;

            var chosen = (permanent ? 1 : 0) + (days.HasValue ? 1 : 0) + (minutes.HasValue ? 1 : 0) + (string.IsNullOrWhiteSpace(until) ? 0 : 1);
            if (chosen == 0) return "Choose a ban duration (days, minutes, until or permanent).";
            if (chosen > 1) return "Give only one of days, minutes, until or permanent.";

            if (permanent)
            {
                expiry = DateTime.MaxValue;
                return null;
            }

            if (days.HasValue)
            {
                var d = days.Value;
                if (double.IsNaN(d) || double.IsInfinity(d) || d <= 0) return "Days must be greater than zero.";
                if (d * 24 * 60 > MaxBanMinutes) return "Use permanent for bans longer than 100 years.";
                expiry = nowUtc.AddMinutes(Math.Round(d * 24 * 60));
                return null;
            }

            if (minutes.HasValue)
            {
                var m = minutes.Value;
                if (m <= 0) return "Minutes must be greater than zero.";
                if (m > MaxBanMinutes) return "Use permanent for bans longer than 100 years.";
                expiry = nowUtc.AddMinutes(m);
                return null;
            }

            if (!DateTime.TryParse(until.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var u))
                return "Until must be an ISO date/time.";

            u = DateTime.SpecifyKind(u, DateTimeKind.Utc);
            if (u <= nowUtc) return "Until must be in the future.";
            expiry = u;
            return null;
        }

        /// <summary>The reason stored on the account (shown to the player by the client). Blank → a default text; clipped to 200 chars.</summary>
        public static string NormalizeBanReason(string reason)
        {
            reason = (reason ?? string.Empty).Trim();
            if (reason.Length == 0) reason = "Banned by an administrator";
            return reason.Length <= MaxBanReasonLength ? reason : reason.Substring(0, MaxBanReasonLength);
        }

        /// <summary>
        /// New balance for mode "set" (value = new balance) or "add" (value = signed delta). Clamped to [0, uint.MaxValue] for add;
        /// out-of-range set values are an error. Returns an error or null.
        /// </summary>
        public static string ApplyCurrency(uint current, long value, string mode, out uint next)
        {
            next = current;
            var m = (mode ?? "add").Trim().ToLowerInvariant();

            if (m == "set")
            {
                if (value < 0 || value > uint.MaxValue) return "Value must be between 0 and " + uint.MaxValue.ToString("N0", CultureInfo.InvariantCulture) + ".";
                next = (uint)value;
                return null;
            }

            if (m == "add")
            {
                var target = (long)current + value;
                if (target < 0) target = 0;
                if (target > uint.MaxValue) target = uint.MaxValue;
                next = (uint)target;
                return null;
            }

            return "Mode must be set or add.";
        }

        /// <summary>True when the stored ban is active at <paramref name="nowUtc"/> (Envir.Login: Banned and ExpiryDate &gt; Now).</summary>
        public static bool IsBanActive(bool banned, DateTime expiry, DateTime nowUtc) => banned && expiry > nowUtc;

        /// <summary>Filters, sorts and pages snapshots.</summary>
        public static AccountQueryResult Query(IEnumerable<AccountSnapshot> accounts, AccountQueryOptions o)
        {
            o ??= new AccountQueryOptions();
            var list = (accounts ?? Enumerable.Empty<AccountSnapshot>()).Where(a => a != null);

            var q = (o.Q ?? string.Empty).Trim();
            if (q.Length > 0)
            {
                var mode = (o.Mode ?? "account").Trim().ToLowerInvariant();
                list = list.Where(a => MatchesSearch(a, q, mode, o.Exact));
            }

            switch ((o.Filter ?? "all").Trim().ToLowerInvariant())
            {
                case "banned":
                    list = list.Where(a => a.Banned);
                    break;
                case "admin":
                    list = list.Where(a => a.Admin);
                    break;
                case "online":
                    list = list.Where(a => a.Online || a.Connected);
                    break;
                case "requirepw":
                    list = list.Where(a => a.RequirePasswordChange);
                    break;
            }

            var sorted = Sort(list, o.Sort, o.Desc).ToList();

            var pageSize = Math.Clamp(o.PageSize <= 0 ? 50 : o.PageSize, 1, MaxPageSize);
            var total = sorted.Count;
            var pages = Math.Max(1, (total + pageSize - 1) / pageSize);
            var page = Math.Clamp(o.Page <= 0 ? 1 : o.Page, 1, pages);

            return new AccountQueryResult
            {
                Rows = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
                Total = total,
                Page = page,
                Pages = pages,
                PageSize = pageSize,
            };
        }

        /// <summary>Envir.MatchAccounts / MatchAccountsByPlayer / MatchAccountsByIP semantics (case-insensitive contains, or equals when exact); IP mode checks LastIP and CreationIP.</summary>
        public static bool MatchesSearch(AccountSnapshot a, string q, string mode, bool exact)
        {
            if (string.IsNullOrEmpty(q)) return true;

            switch (mode)
            {
                case "player":
                case "character":
                    return a.CharacterNames != null && a.CharacterNames.Any(n => Match(n, q, exact));
                case "ip":
                    return Match(a.LastIp, q, exact) || Match(a.CreationIp, q, exact);
                default:
                    return Match(a.AccountId, q, exact);
            }
        }

        private static bool Match(string value, string q, bool exact)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return exact ? value.Equals(q, StringComparison.OrdinalIgnoreCase) : value.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IEnumerable<AccountSnapshot> Sort(IEnumerable<AccountSnapshot> list, string sort, bool desc)
        {
            switch ((sort ?? "index").Trim().ToLowerInvariant())
            {
                case "accountid":
                case "account":
                    return Order(list, a => a.AccountId ?? string.Empty, StringComparer.OrdinalIgnoreCase, desc);
                case "username":
                    return Order(list, a => a.UserName ?? string.Empty, StringComparer.OrdinalIgnoreCase, desc);
                case "email":
                    return Order(list, a => a.Email ?? string.Empty, StringComparer.OrdinalIgnoreCase, desc);
                case "gold":
                    return Order(list, a => a.Gold, Comparer<uint>.Default, desc);
                case "credit":
                    return Order(list, a => a.Credit, Comparer<uint>.Default, desc);
                case "characters":
                case "charactercount":
                    return Order(list, a => a.CharacterCount, Comparer<int>.Default, desc);
                case "created":
                case "creationdate":
                    return Order(list, a => a.CreationDate, Comparer<DateTime>.Default, desc);
                case "lastdate":
                case "lastlogin":
                    return Order(list, a => a.LastDate, Comparer<DateTime>.Default, desc);
                case "lastip":
                    return Order(list, a => a.LastIp ?? string.Empty, StringComparer.OrdinalIgnoreCase, desc);
                case "creationip":
                    return Order(list, a => a.CreationIp ?? string.Empty, StringComparer.OrdinalIgnoreCase, desc);
                case "status":
                    return Order(list, a => (a.Banned ? 4 : 0) + (a.Admin ? 2 : 0) + (a.Online ? 1 : 0), Comparer<int>.Default, desc);
                default:
                    return desc ? list.OrderByDescending(a => a.Index) : list.OrderBy(a => a.Index);
            }
        }

        private static IEnumerable<AccountSnapshot> Order<TKey>(IEnumerable<AccountSnapshot> list, Func<AccountSnapshot, TKey> key, IComparer<TKey> comparer, bool desc)
        {
            // Index is the stable tie-breaker so paging never shuffles equal rows.
            return desc
                ? list.OrderByDescending(key, comparer).ThenBy(a => a.Index)
                : list.OrderBy(key, comparer).ThenBy(a => a.Index);
        }

        /// <summary>Gold / credit totals over non-admin, non-banned accounts (AccountInfoForm's server totals).</summary>
        public static (ulong Gold, ulong Credit) Totals(IEnumerable<AccountSnapshot> accounts)
        {
            ulong gold = 0, credit = 0;
            foreach (var a in accounts ?? Enumerable.Empty<AccountSnapshot>())
            {
                if (a == null || a.Admin || a.Banned) continue;
                gold += a.Gold;
                credit += a.Credit;
            }

            return (gold, credit);
        }

        /// <summary>IPs (LastIP or CreationIP) used by at least <paramref name="minAccounts"/> distinct accounts, largest groups first.</summary>
        public static List<SharedIpGroup> SharedIps(IEnumerable<AccountSnapshot> accounts, int minAccounts = 2, int max = 100)
        {
            var groups = new Dictionary<string, SharedIpGroup>(StringComparer.OrdinalIgnoreCase);

            void Add(string ip, AccountSnapshot a, string via)
            {
                ip = NormalizeIp(ip);
                if (ip == null) return;

                if (!groups.TryGetValue(ip, out var g)) groups[ip] = g = new SharedIpGroup { Ip = ip };

                var existing = g.Accounts.FirstOrDefault(x => x.Index == a.Index);
                if (existing != null)
                {
                    if (existing.Via != via) existing.Via = "both";
                    return;
                }

                g.Accounts.Add(new SharedIpAccount { Index = a.Index, AccountId = a.AccountId, Banned = a.Banned, Admin = a.Admin, Via = via, LastDate = a.LastDate });
            }

            foreach (var a in accounts ?? Enumerable.Empty<AccountSnapshot>())
            {
                if (a == null) continue;
                Add(a.LastIp, a, "last");
                Add(a.CreationIp, a, "creation");
            }

            var min = Math.Max(2, minAccounts);
            return groups.Values
                .Where(g => g.Accounts.Count >= min)
                .Select(g =>
                {
                    g.Accounts = g.Accounts.OrderByDescending(x => x.LastDate).ThenBy(x => x.Index).ToList();
                    return g;
                })
                .OrderByDescending(g => g.Accounts.Count)
                .ThenBy(g => g.Ip, StringComparer.Ordinal)
                .Take(Math.Max(1, max))
                .ToList();
        }

        /// <summary>
        /// An IPv4 address in the form Crystal stores (RemoteEndPoint text before ':'), or null when the text is not one.
        /// IPv4-mapped IPv6 is unwrapped. Rejects anything else (the game listener only sees IPv4 text).
        /// </summary>
        public static string NormalizeIp(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (text.Length > 45) return null;
            if (!IPAddress.TryParse(text, out var addr)) return null;
            if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
            if (addr.AddressFamily != AddressFamily.InterNetwork) return null;

            // IPAddress.TryParse accepts shorthand like "10" or "1.2"; require the dotted-quad form.
            var s = addr.ToString();
            return text.Count(c => c == '.') == 3 || text.Contains(':') ? s : null;
        }

        /// <summary>Non-secret, stable key for a web session id (what the security overview lists and revoke accepts).</summary>
        public static string SessionKey(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return string.Empty;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("mircraft-session:" + sessionId));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        /// <summary>DateTime for JSON: null for unset (MinValue) and "never" (MaxValue); unspecified kinds are Crystal's UTC clock.</summary>
        public static DateTime? Iso(DateTime value)
        {
            if (value == DateTime.MinValue || value == DateTime.MaxValue) return null;
            if (value.Kind == DateTimeKind.Unspecified) return DateTime.SpecifyKind(value, DateTimeKind.Utc);
            return value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        }

        /// <summary>A ban expiry that is effectively permanent (MaxValue, or more than 50 years away).</summary>
        public static bool IsPermanent(DateTime expiry, DateTime nowUtc) => expiry == DateTime.MaxValue || expiry > nowUtc.AddYears(50);
    }
}

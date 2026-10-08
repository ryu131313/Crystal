using System.Globalization;
using System.Text;
using Server.MirDatabase;
using Server.MirEnvir;

namespace Server.WebAdmin
{
    /// <summary>What the web login needs to know about an admin account (never the plain password).</summary>
    public sealed class WebAdminAdminEntry
    {
        public int Index { get; set; }
        public string AccountId { get; set; }

        /// <summary>AccountInfo.Password (PBKDF2 hash string).</summary>
        internal string PasswordHash { get; set; }

        internal byte[] Salt { get; set; }

        public bool AdminAccount { get; set; }
        public bool Banned { get; set; }
        public DateTime ExpiryDate { get; set; }

        /// <summary>AccountInfo.RequirePasswordChange: Envir.Login refuses such accounts (Result 5), so the web panel does too.</summary>
        public bool RequirePasswordChange { get; set; }

        public bool IsBannedAt(DateTime nowUtc) => Banned && ExpiryDate > nowUtc;

        public bool IsActiveAdminAt(DateTime nowUtc) => AdminAccount && !RequirePasswordChange && !IsBannedAt(nowUtc);

        /// <summary>True when everything the admin checks depend on is identical.</summary>
        internal bool SameAs(WebAdminAdminEntry other)
        {
            return other != null && other.Index == Index && other.AdminAccount == AdminAccount && other.Banned == Banned &&
                   other.ExpiryDate == ExpiryDate && other.RequirePasswordChange == RequirePasswordChange &&
                   string.Equals(other.AccountId, AccountId, StringComparison.Ordinal) &&
                   string.Equals(other.PasswordHash, PasswordHash, StringComparison.Ordinal) &&
                   (other.Salt ?? Array.Empty<byte>()).SequenceEqual(Salt ?? Array.Empty<byte>());
        }

        internal static WebAdminAdminEntry From(AccountInfo account)
        {
            return new WebAdminAdminEntry
            {
                Index = account.Index,
                AccountId = account.AccountID,
                PasswordHash = account.Password,
                Salt = account.Salt == null ? Array.Empty<byte>() : (byte[])account.Salt.Clone(),
                AdminAccount = account.AdminAccount,
                Banned = account.Banned,
                ExpiryDate = account.ExpiryDate,
                RequirePasswordChange = account.RequirePasswordChange,
            };
        }
    }

    /// <summary>
    /// Offline admin directory: a snapshot of admin accounts (index, id, password hash, salt, admin flag, ban) captured on the game
    /// thread when accounts load/save, persisted atomically to WebAdmin\admins.dat next to the exe so the panel can sign in while the
    /// world is stopped (even in a fresh process). Also used for the per-request "still admin and not banned" check, which
    /// <see cref="IsSessionStillAdminAsync"/> backs with a live AccountInfo re-check (every few seconds) while the world runs.
    /// Features that change admin/ban flags should still call <see cref="Refresh"/> (and revoke sessions) for an immediate effect.
    /// <para>
    /// admins.dat records when it was captured; it is ignored when the account database (Server.MirADB) is missing or was written
    /// noticeably later (e.g. restored or edited while the server was down), until the world loads the accounts again.
    /// Deleting WebAdmin\admins.dat revokes all offline (world stopped) web sign-ins.
    /// </para>
    /// </summary>
    public static class WebAdminAdmins
    {
        private const string FileHeader = "MirCraft WebAdmins v1";
        private const string CapturedPrefix = "@captured=";

        private static volatile Dictionary<int, WebAdminAdminEntry> _byIndex = new Dictionary<int, WebAdminAdminEntry>();
        private static readonly object _writeGate = new object();
        private static readonly object _snapshotGate = new object();
        private static string _lastPersisted;
        private static int _hooked;
        private static int _persistVersion;

        internal static string FilePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "WebAdmin", "admins.dat");

        /// <summary>Path of the account database admins.dat is checked against (Envir.AccountPath, relative to the working directory).</summary>
        internal static Func<string> AccountDbPath { get; set; } = () => Envir.AccountPath;

        /// <summary>The account DB may be written this long after a capture without invalidating admins.dat (async/shutdown saves).</summary>
        internal static readonly TimeSpan StaleGrace = TimeSpan.FromMinutes(2);

        /// <summary>How often a session's admin flags are re-read from the live AccountInfo while the world runs.</summary>
        internal static TimeSpan LiveCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Game-thread lookup of the live account by id (tests replace it).</summary>
        internal static Func<string, WebAdminAdminEntry> LiveLookup { get; set; } = LookupLive;

        private static DateTime _capturedUtc;

        /// <summary>False disables writing admins.dat (tests).</summary>
        internal static bool PersistEnabled { get; set; } = true;

        public static int Count => _byIndex.Count;

        /// <summary>Subscribes AccountsLoaded / AccountsSaved capture (idempotent).</summary>
        internal static void EnsureHooks()
        {
            if (Interlocked.Exchange(ref _hooked, 1) != 0) return;

            WebAdminHooks.AccountsLoaded += CaptureFromMain;
            WebAdminHooks.AccountsSaved += CaptureFromMain;
        }

        public static IReadOnlyList<WebAdminAdminEntry> All() => _byIndex.Values.OrderBy(e => e.Index).ToList();

        public static WebAdminAdminEntry Find(int index) => _byIndex.TryGetValue(index, out var e) ? e : null;

        public static WebAdminAdminEntry FindByAccountId(string accountId)
        {
            if (string.IsNullOrEmpty(accountId)) return null;
            return _byIndex.Values.FirstOrDefault(e => string.Equals(e.AccountId, accountId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>True when the account is in the directory, still an admin, not banned, and the account id still matches.</summary>
        public static bool IsActiveAdmin(int index, string accountId, DateTime nowUtc)
        {
            var e = Find(index);
            return e != null && e.IsActiveAdminAt(nowUtc) && string.Equals(e.AccountId, accountId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Offline credential check against the directory. Returns the entry or null; <paramref name="reason"/> = no-account / not-admin / banned / bad-password.</summary>
        public static WebAdminAdminEntry TryAuthenticate(string accountId, string password, DateTime nowUtc, out string reason)
        {
            var e = FindByAccountId(accountId);
            if (e == null)
            {
                reason = "no-account";
                return null;
            }

            if (!e.AdminAccount)
            {
                reason = "not-admin";
                return null;
            }

            if (e.IsBannedAt(nowUtc))
            {
                reason = "banned";
                return null;
            }

            if (e.RequirePasswordChange)
            {
                reason = "password-change-required";
                return null;
            }

            if (!WebAdminAuth.VerifyPassword(password, e.PasswordHash, e.Salt))
            {
                reason = "bad-password";
                return null;
            }

            reason = null;
            return e;
        }

        /// <summary>Replaces the snapshot with the admin accounts in <paramref name="accounts"/> (call on the game thread or under AccountLock).</summary>
        public static void Capture(IList<AccountInfo> accounts)
        {
            if (accounts == null) return;

            var entries = new List<WebAdminAdminEntry>();
            for (var i = 0; i < accounts.Count; i++)
            {
                var a = accounts[i];
                if (a == null || !a.AdminAccount) continue;
                entries.Add(WebAdminAdminEntry.From(a));
            }

            SetSnapshot(entries, persist: true);
        }

        /// <summary>Adds or replaces one entry (e.g. after a successful online login of a freshly promoted admin).</summary>
        public static void Upsert(WebAdminAdminEntry entry)
        {
            if (entry == null) return;

            lock (_snapshotGate)
            {
                var copy = new Dictionary<int, WebAdminAdminEntry>(_byIndex);
                if (entry.AdminAccount) copy[entry.Index] = entry;
                else copy.Remove(entry.Index);
                _byIndex = copy;
            }

            PersistAsync();
        }

        /// <summary>Removes one account from the directory (e.g. it no longer exists).</summary>
        public static void Remove(int index)
        {
            lock (_snapshotGate)
            {
                if (!_byIndex.ContainsKey(index)) return;
                var copy = new Dictionary<int, WebAdminAdminEntry>(_byIndex);
                copy.Remove(index);
                _byIndex = copy;
            }

            PersistAsync();
        }

        /// <summary>
        /// Per-request check that a session's account is still an active admin. Checks the directory first; while the world's admin
        /// queue is open it also re-reads the live AccountInfo on the game thread (at most every <see cref="LiveCheckInterval"/> per
        /// session), so a demotion, ban or RequirePasswordChange made anywhere (WinForms account editor, GM commands, other features)
        /// cuts web sessions off within seconds and updates the directory. Falls back to the directory when the game thread is
        /// unavailable (stopped meanwhile / timeout).
        /// </summary>
        public static async Task<bool> IsSessionStillAdminAsync(WebAdminSession session, DateTime nowUtc)
        {
            if (session == null) return false;
            if (!IsActiveAdmin(session.AccountIndex, session.AccountId, nowUtc)) return false;

            if (!WebAdminGame.World.QueueOpen) return true;

            var last = Interlocked.Read(ref session.LiveCheckedTicks);
            var age = nowUtc.Ticks - last;
            if (last != 0 && age >= 0 && age < LiveCheckInterval.Ticks) return true;

            WebAdminAdminEntry live;
            try
            {
                var lookup = LiveLookup;
                var accountId = session.AccountId;
                live = await WebAdminGame.RunAsync(() => lookup(accountId), 2000).ConfigureAwait(false);
            }
            catch (WorldNotRunningException)
            {
                return IsActiveAdmin(session.AccountIndex, session.AccountId, nowUtc);
            }
            catch (GameThreadTimeoutException)
            {
                return IsActiveAdmin(session.AccountIndex, session.AccountId, nowUtc);
            }

            if (live == null || live.Index != session.AccountIndex)
            {
                Remove(session.AccountIndex); // deleted, or the id now belongs to another account
                return false;
            }

            if (!live.SameAs(Find(live.Index))) Upsert(live);

            if (!live.IsActiveAdminAt(nowUtc)) return false;

            Interlocked.Exchange(ref session.LiveCheckedTicks, nowUtc.Ticks);
            return true;
        }

        private static WebAdminAdminEntry LookupLive(string accountId)
        {
            var account = Envir.Main.GetAccount(accountId);
            return account == null ? null : WebAdminAdminEntry.From(account);
        }

        /// <summary>Re-captures from Envir.Main: queued on the game thread while running, inline when fully stopped (keeps the file snapshot if no accounts are loaded).</summary>
        public static void Refresh()
        {
            try
            {
                var w = WebAdminGame.World;

                if (w.QueueOpen)
                {
                    w.TryQueue(CaptureFromMain, null);
                    return;
                }

                if (!WebAdminGame.IsFullyStopped(w)) return; // starting/stopping: the AccountsLoaded/AccountsSaved hooks will capture

                lock (w.AccountLock)
                    CaptureFromMain();
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Admin directory refresh failed");
            }
        }

        private static void CaptureFromMain()
        {
            var list = Envir.Main.AccountList;
            if (list == null || list.Count == 0) return; // never loaded in this process: keep the persisted snapshot
            Capture(list);
        }

        internal static void SetSnapshot(IEnumerable<WebAdminAdminEntry> entries, bool persist)
        {
            if (persist) _capturedUtc = DateTime.UtcNow;

            var map = new Dictionary<int, WebAdminAdminEntry>();
            foreach (var e in entries)
            {
                if (e == null) continue;
                map[e.Index] = e;
            }

            lock (_snapshotGate)
                _byIndex = map;

            if (persist) PersistAsync();
        }

        /// <summary>Loads WebAdmin\admins.dat into the snapshot when the snapshot is still empty. Never throws.</summary>
        public static void Load()
        {
            try
            {
                if (_byIndex.Count > 0 || !File.Exists(FilePath)) return;

                var text = File.ReadAllText(FilePath, Encoding.UTF8);
                var entries = Deserialize(text, out var capturedUtc);
                if (capturedUtc == DateTime.MinValue) capturedUtc = File.GetLastWriteTimeUtc(FilePath); // file written before the stamp existed

                var staleReason = CheckStale(capturedUtc);
                if (staleReason != null)
                {
                    WebAdminLog.Info("Ignoring " + FilePath + ": " + staleReason + ". Offline web sign-in is unavailable until the world loads the accounts.");
                    return;
                }

                lock (_snapshotGate)
                {
                    if (_byIndex.Count == 0)
                    {
                        var map = new Dictionary<int, WebAdminAdminEntry>();
                        foreach (var e in entries) map[e.Index] = e;
                        _byIndex = map;
                        _lastPersisted = text;
                        _capturedUtc = capturedUtc;
                    }
                }
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Failed to load " + FilePath);
            }
        }

        /// <summary>Null when admins.dat (captured at <paramref name="capturedUtc"/>) may be trusted, else why not.</summary>
        internal static string CheckStale(DateTime capturedUtc)
        {
            string dbPath;
            try
            {
                dbPath = Path.GetFullPath(AccountDbPath());
            }
            catch (Exception)
            {
                return "the account database path is unavailable";
            }

            if (!File.Exists(dbPath)) return "the account database " + dbPath + " was not found";

            var info = new FileInfo(dbPath);
            // CreationTime catches a copied-in (restored) file whose LastWriteTime was preserved from the backup.
            var changed = info.LastWriteTimeUtc > info.CreationTimeUtc ? info.LastWriteTimeUtc : info.CreationTimeUtc;

            return changed > capturedUtc + StaleGrace ? "the account database changed after it was captured" : null;
        }

        private static void PersistAsync()
        {
            if (!PersistEnabled) return;

            var version = Interlocked.Increment(ref _persistVersion);

            Task.Run(() =>
            {
                try
                {
                    lock (_writeGate)
                    {
                        if (version != Volatile.Read(ref _persistVersion)) return; // a newer snapshot will be written

                        var text = Serialize(_byIndex.Values, _capturedUtc);
                        if (text == _lastPersisted) return;

                        var path = FilePath;
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                        var temp = path + ".tmp";
                        File.WriteAllText(temp, text, WebAdminJson.Utf8NoBom);

                        if (File.Exists(path)) File.Replace(temp, path, null, true);
                        else File.Move(temp, path);

                        _lastPersisted = text;
                    }
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "Failed to write " + FilePath);
                }
            });
        }

        internal static string Serialize(IEnumerable<WebAdminAdminEntry> entries) => Serialize(entries, DateTime.MinValue);

        /// <summary>
        /// Header line, optional "@captured=&lt;utc ticks&gt;" line, then one line per entry:
        /// index|b64(accountId)|b64(utf8(hash))|b64(salt)|admin(0/1)|banned(0/1)|expiryBinary|requirePasswordChange(0/1)
        /// </summary>
        internal static string Serialize(IEnumerable<WebAdminAdminEntry> entries, DateTime capturedUtc)
        {
            var sb = new StringBuilder();
            sb.Append(FileHeader).Append('\n');
            if (capturedUtc != DateTime.MinValue)
                sb.Append(CapturedPrefix).Append(capturedUtc.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');

            foreach (var e in entries.OrderBy(x => x.Index))
            {
                sb.Append(e.Index.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(B64(e.AccountId ?? string.Empty)).Append('|')
                  .Append(B64(e.PasswordHash ?? string.Empty)).Append('|')
                  .Append(Convert.ToBase64String(e.Salt ?? Array.Empty<byte>())).Append('|')
                  .Append(e.AdminAccount ? '1' : '0').Append('|')
                  .Append(e.Banned ? '1' : '0').Append('|')
                  .Append(e.ExpiryDate.ToBinary().ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(e.RequirePasswordChange ? '1' : '0')
                  .Append('\n');
            }

            return sb.ToString();
        }

        internal static List<WebAdminAdminEntry> Deserialize(string text) => Deserialize(text, out _);

        internal static List<WebAdminAdminEntry> Deserialize(string text, out DateTime capturedUtc)
        {
            capturedUtc = DateTime.MinValue;
            var result = new List<WebAdminAdminEntry>();
            if (string.IsNullOrEmpty(text)) return result;

            var lines = text.Replace("\r", string.Empty).Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != FileHeader) return result;

            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith(CapturedPrefix, StringComparison.Ordinal))
                {
                    if (long.TryParse(line.Substring(CapturedPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) &&
                        ticks > 0 && ticks <= DateTime.MaxValue.Ticks)
                        capturedUtc = new DateTime(ticks, DateTimeKind.Utc);
                    continue;
                }

                var parts = line.Split('|');
                if (parts.Length != 7 && parts.Length != 8) continue;

                try
                {
                    result.Add(new WebAdminAdminEntry
                    {
                        Index = int.Parse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture),
                        AccountId = UnB64(parts[1]),
                        PasswordHash = UnB64(parts[2]),
                        Salt = Convert.FromBase64String(parts[3]),
                        AdminAccount = parts[4] == "1",
                        Banned = parts[5] == "1",
                        ExpiryDate = DateTime.FromBinary(long.Parse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture)),
                        RequirePasswordChange = parts.Length > 7 && parts[7] == "1",
                    });
                }
                catch (FormatException)
                {
                    // skip a corrupt line
                }
                catch (ArgumentException)
                {
                }
                catch (OverflowException)
                {
                }
            }

            return result;
        }

        private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

        private static string UnB64(string s) => Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }
}

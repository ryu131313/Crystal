using System.Text.Json;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirNetwork;
using Server.MirObjects;
using S = ServerPackets;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Accounts (B3): /api/admin/accounts list / detail / create and the per-account mutations (mirror AccountInfoForm, fixing the
    /// quirks listed in crystal-winforms-features.md §9: bans disconnect online players, delete cleans characters + auctions).
    /// All reads/writes run on the game thread while the world runs, inline under Envir.AccountLock while it is fully stopped
    /// (then the account file is saved immediately, like AccountInfoForm does on close).
    /// </summary>
    public sealed class AccountRoutes : IWebAdminRoutes
    {
        /// <summary>Crystal's "account banned" disconnect reason (PlayerInfoForm Account Ban).</summary>
        public const byte BannedDisconnectReason = 6;

        private static int _hooked;
        private static volatile bool _accountsLoaded;

        public void Register(WebAdminRouter router)
        {
            if (Interlocked.Exchange(ref _hooked, 1) == 0)
                WebAdminHooks.AccountsLoaded += () => _accountsLoaded = true;

            router.Get("/api/admin/accounts", ListAsync);
            router.Post("/api/admin/accounts", CreateAsync);
            router.Get("/api/admin/accounts/{index:int}", DetailAsync);
            router.Post("/api/admin/accounts/{index:int}/update", UpdateAsync);
            router.Post("/api/admin/accounts/{index:int}/ban", BanAsync);
            router.Post("/api/admin/accounts/{index:int}/unban", UnbanAsync);
            router.Post("/api/admin/accounts/{index:int}/admin", AdminAsync);
            router.Post("/api/admin/accounts/{index:int}/require-password-change", RequirePasswordChangeAsync);
            router.Post("/api/admin/accounts/{index:int}/password", PasswordAsync);
            router.Post("/api/admin/accounts/{index:int}/clear-storage-password", ClearStoragePasswordAsync);
            router.Post("/api/admin/accounts/{index:int}/currency", CurrencyAsync);
            router.Post("/api/admin/accounts/{index:int}/delete", DeleteAsync);
        }

        // ------------------------------------------------------------------ shared helpers (game thread / inline)

        /// <summary>Envir's clock while the loop runs (ban expiries are compared with Envir.Now, which is UTC based); UTC otherwise.</summary>
        internal static DateTime GameNow() => WebAdminGame.World.QueueOpen ? Envir.Main.Now : DateTime.UtcNow;

        /// <summary>409 when the world is stopped and accounts were never loaded in this process (AccountList is empty until the first start).</summary>
        /// <summary>The account list in memory no longer reflects Server.MirADB (a failed restore cleared it): block edits until a reload.</summary>
        internal static void MarkAccountsUnloaded() => _accountsLoaded = false;

        internal static void EnsureAccountsLoaded()
        {
            if (WebAdminGame.World.QueueOpen) return;
            if (_accountsLoaded || Envir.Main.AccountList.Count > 0) return;

            throw new WebAdminException(409, "Account data is not loaded: start the world once so the server reads the account database.",
                new { world = WebAdminGame.WorldState });
        }

        internal static AccountInfo FindAccount(int index)
        {
            var list = Envir.Main.AccountList;
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].Index == index) return list[i];
            }

            return null;
        }

        internal static AccountSnapshot Snapshot(AccountInfo a, DateTime now)
        {
            var s = new AccountSnapshot
            {
                Index = a.Index,
                AccountId = a.AccountID ?? string.Empty,
                UserName = a.UserName ?? string.Empty,
                Email = a.EMailAddress ?? string.Empty,
                Admin = a.AdminAccount,
                Banned = AccountLogic.IsBanActive(a.Banned, a.ExpiryDate, now),
                BanReason = a.BanReason ?? string.Empty,
                BanExpiry = a.ExpiryDate,
                Gold = a.Gold,
                Credit = a.Credit,
                CreationIp = a.CreationIP ?? string.Empty,
                CreationDate = a.CreationDate,
                LastIp = a.LastIP ?? string.Empty,
                LastDate = a.LastDate,
                RequirePasswordChange = a.RequirePasswordChange,
                Connected = a.Connection != null,
            };

            var chars = a.Characters;
            if (chars != null)
            {
                for (var i = 0; i < chars.Count; i++)
                {
                    var c = chars[i];
                    if (c == null) continue;

                    if (c.Deleted) s.DeletedCount++;
                    else s.CharacterCount++;

                    if (!string.IsNullOrEmpty(c.Name)) s.CharacterNames.Add(c.Name);

                    if (c.Player != null)
                    {
                        s.Online = true;
                        s.OnlineCharacter ??= c.Name;
                    }
                }
            }

            return s;
        }

        internal static object ToRow(AccountSnapshot s, DateTime now)
        {
            return new
            {
                index = s.Index,
                accountId = s.AccountId,
                userName = s.UserName,
                email = s.Email,
                admin = s.Admin,
                banned = s.Banned,
                banReason = s.Banned ? s.BanReason : null,
                banExpiry = s.Banned ? AccountLogic.Iso(s.BanExpiry) : null,
                banPermanent = s.Banned && AccountLogic.IsPermanent(s.BanExpiry, now),
                gold = s.Gold,
                credit = s.Credit,
                creationIp = s.CreationIp,
                creationDate = AccountLogic.Iso(s.CreationDate),
                lastIp = s.LastIp,
                lastDate = AccountLogic.Iso(s.LastDate),
                characterCount = s.CharacterCount,
                deletedCount = s.DeletedCount,
                online = s.Online,
                connected = s.Connected,
                onlineCharacter = s.OnlineCharacter,
                requirePasswordChange = s.RequirePasswordChange,
            };
        }

        /// <summary>Accounts that can sign in to the web panel (admin, not banned, no forced password change), optionally excluding one.</summary>
        internal static int CountActiveAdmins(DateTime now, int exceptIndex)
        {
            var count = 0;
            var list = Envir.Main.AccountList;
            for (var i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (a == null || a.Index == exceptIndex || !a.AdminAccount) continue;
                if (AccountLogic.IsBanActive(a.Banned, a.ExpiryDate, now) || a.RequirePasswordChange) continue;
                count++;
            }

            return count;
        }

        internal static string GuildName(int guildIndex)
        {
            if (guildIndex < 0) return null;
            var list = Envir.Main.GuildList;
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].GuildIndex == guildIndex) return list[i].Name;
            }

            return null;
        }

        /// <summary>Sends S.Disconnect(reason) to the account's connection and to every in-game character's connection. Returns the kicked character names.</summary>
        internal static List<string> DisconnectAccount(AccountInfo a, byte reason)
        {
            var names = new List<string>();
            var connections = new List<MirConnection>();

            foreach (var c in a.Characters)
            {
                if (c?.Player == null) continue;
                names.Add(c.Name);
                var pc = c.Player.Connection;
                if (pc != null && !connections.Contains(pc)) connections.Add(pc);
            }

            if (a.Connection != null && !connections.Contains(a.Connection)) connections.Add(a.Connection);

            foreach (var conn in connections)
            {
                try
                {
                    conn.SendDisconnect(reason);
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "Disconnect of " + a.AccountID + " failed");
                }
            }

            return names;
        }

        internal static PlayerObject FirstOnlinePlayer(AccountInfo a)
        {
            foreach (var c in a.Characters)
            {
                if (c?.Player != null) return c.Player;
            }

            return null;
        }

        /// <summary>
        /// Runs <paramref name="fn"/> against the account on the game thread (or inline while stopped), then saves the account file when the
        /// world is stopped (a restart reloads accounts from disk, so unsaved inline edits would be lost). 404 when the index is unknown.
        /// </summary>
        private static Task<(T Result, string Persist)> OnAccountAsync<T>(WebAdminRequest req, string action, int index, Func<AccountInfo, DateTime, T> fn)
        {
            return AuditIfTimedOutAsync(req, action, "#" + index, new { index }, () => WebAdminGame.RunOrInlineAsync(() =>
            {
                EnsureAccountsLoaded();
                var a = FindAccount(index) ?? throw new WebAdminException(404, "Account not found");
                var result = fn(a, GameNow());
                return (result, PersistIfStopped());
            }));
        }

        /// <summary>
        /// Awaits a game-thread mutation. When it times out after it already started on the game thread, it may still complete, so an audit entry
        /// with an unknown outcome is recorded before the 504 is rethrown ("audit on every mutation").
        /// </summary>
        internal static async Task<T> AuditIfTimedOutAsync<T>(WebAdminRequest req, string action, string target, object details, Func<Task<T>> work)
        {
            try
            {
                return await work();
            }
            catch (GameThreadTimeoutException ex) when (ex.Started)
            {
                WebAdminAudit.Record(req, action, target, new { outcome = "unknown", note = "game thread timed out after the action started; it may still have been applied", request = details });
                throw;
            }
        }

        /// <summary>"autosave" while running (the game loop saves accounts every SaveDelay minutes), "saved" after a synchronous save while stopped.</summary>
        internal static string PersistIfStopped()
        {
            if (WebAdminGame.World.QueueOpen) return "autosave";

            if (Envir.Main.Saving)
            {
                WebAdminLog.Info("Account file not saved after a web admin change: a previous save is still flagged as in progress.");
                return "unsaved";
            }

            // Never write an empty / not-loaded account list over Server.MirADB (e.g. after a restore that could load nothing).
            if (Envir.Main.AccountList.Count == 0)
            {
                WebAdminLog.Info("Account file not saved after a web admin change: the account list is not loaded from disk.");
                return "unsaved";
            }

            Envir.Main.SaveAccounts();
            return "saved";
        }

        private static bool IsSelf(WebAdminRequest req, int accountIndex) => req.Session != null && req.Session.AccountIndex == accountIndex;

        private static void RevokeWebSessions(int accountIndex, string keepSessionId = null)
        {
            var store = WebAdminSessions.Store;
            if (keepSessionId == null)
            {
                store.RevokeAccount(accountIndex);
                return;
            }

            foreach (var s in store.List())
            {
                if (s.AccountIndex == accountIndex && !string.Equals(s.SessionId, keepSessionId, StringComparison.Ordinal))
                    store.Revoke(s.SessionId);
            }
        }

        private static async Task<JsonElement> ReadBodyAsync(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync();
            if (body.ValueKind != JsonValueKind.Object) throw new WebAdminException(400, "A JSON object body is required");
            return body;
        }

        private static bool? OptionalBool(JsonElement body, string name)
        {
            if (!body.TryGetPropertyIgnoreCase(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            return body.GetBool(name);
        }

        private static long? OptionalLong(JsonElement body, string name)
        {
            if (!body.TryGetPropertyIgnoreCase(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())) return null;

            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var dbl) && Math.Abs(dbl) < 9e18) return (long)Math.Round(dbl);
            if (v.ValueKind == JsonValueKind.String &&
                long.TryParse(v.GetString().Replace(",", string.Empty).Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out n))
                return n;

            throw new WebAdminException(400, name + " must be a whole number");
        }

        private static double? OptionalDouble(JsonElement body, string name)
        {
            if (!body.TryGetPropertyIgnoreCase(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())) return null;

            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString().Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d))
                return d;

            throw new WebAdminException(400, name + " must be a number");
        }

        private static string OptionalString(JsonElement body, string name)
        {
            if (!body.TryGetPropertyIgnoreCase(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
        }

        private static void Fail(string error, int status = 400)
        {
            if (error != null) throw new WebAdminException(status, error);
        }

        // ------------------------------------------------------------------ reads

        private static async Task ListAsync(WebAdminRequest req)
        {
            var options = new AccountQueryOptions
            {
                Q = req.Query["q"],
                Mode = req.Query["mode"] ?? "account",
                Exact = req.QueryBool("exact"),
                Filter = req.Query["filter"] ?? "all",
                Sort = req.Query["sort"] ?? "index",
                Desc = string.Equals(req.Query["dir"], "desc", StringComparison.OrdinalIgnoreCase),
                Page = req.QueryInt("page", 1),
                PageSize = req.QueryInt("pageSize", 50),
            };

            var (snapshots, now) = await WebAdminGame.RunOrInlineAsync(() =>
            {
                EnsureAccountsLoaded();
                var t = GameNow();
                var list = Envir.Main.AccountList;
                var result = new List<AccountSnapshot>(list.Count);
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i] != null) result.Add(Snapshot(list[i], t));
                }

                return (result, t);
            });

            var page = AccountLogic.Query(snapshots, options);
            var totals = AccountLogic.Totals(snapshots);

            await req.Json(new
            {
                rows = page.Rows.Select(s => ToRow(s, now)).ToList(),
                total = page.Total,
                page = page.Page,
                pages = page.Pages,
                pageSize = page.PageSize,
                totals = new
                {
                    gold = totals.Gold,
                    credit = totals.Credit,
                    accounts = snapshots.Count,
                    banned = snapshots.Count(s => s.Banned),
                    admins = snapshots.Count(s => s.Admin),
                    online = snapshots.Count(s => s.Online),
                },
                world = WebAdminGame.WorldState,
            });
        }

        private static async Task DetailAsync(WebAdminRequest req)
        {
            var index = req.RouteInt("index");

            var detail = await WebAdminGame.RunOrInlineAsync(() =>
            {
                EnsureAccountsLoaded();
                var a = FindAccount(index) ?? throw new WebAdminException(404, "Account not found");
                var now = GameNow();
                var s = Snapshot(a, now);

                var characters = new List<object>();
                foreach (var c in a.Characters)
                {
                    if (c == null) continue;
                    characters.Add(new
                    {
                        index = c.Index,
                        name = c.Name,
                        className = c.Class.ToString(),
                        gender = c.Gender.ToString(),
                        level = (int)c.Level,
                        pk = c.PKPoints,
                        guild = GuildName(c.GuildIndex),
                        online = c.Player != null,
                        deleted = c.Deleted,
                        deleteDate = c.Deleted ? AccountLogic.Iso(c.DeleteDate) : null,
                        lastLogin = AccountLogic.Iso(c.LastLoginDate),
                        lastLogout = AccountLogic.Iso(c.LastLogoutDate),
                        chatBanned = c.ChatBanned && c.ChatBanExpiryDate > now,
                        chatBanUntil = c.ChatBanned && c.ChatBanExpiryDate > now ? AccountLogic.Iso(c.ChatBanExpiryDate) : null,
                    });
                }

                var storageUsed = 0;
                if (a.Storage != null)
                {
                    for (var i = 0; i < a.Storage.Length; i++)
                    {
                        if (a.Storage[i] != null) storageUsed++;
                    }
                }

                return new
                {
                    account = ToRow(s, now),
                    birthDate = AccountLogic.Iso(a.BirthDate),
                    secretQuestion = a.SecretQuestion ?? string.Empty,
                    secretAnswerSet = !string.IsNullOrEmpty(a.SecretAnswer),
                    hasStoragePassword = a.HasStoragePassword,
                    storagePasswordLastSet = AccountLogic.Iso(a.StoragePasswordLastSet),
                    expandedStorage = a.HasExpandedStorage,
                    expandedStorageExpiry = a.HasExpandedStorage ? AccountLogic.Iso(a.ExpandedStorageExpiryDate) : null,
                    storageUsed,
                    storageSize = a.Storage?.Length ?? 0,
                    auctions = a.Auctions?.Count ?? 0,
                    wrongPasswordCount = a.WrongPasswordCount,
                    characters,
                    accountId = a.AccountID,
                };
            });

            var sessions = WebAdminSessions.Store.List().Count(x => x.AccountIndex == index);
            var recentLogins = WebAdminLoginLog.Query(detail.accountId, null, 30, 200)
                .Where(e => string.Equals(e.AccountId, detail.accountId, StringComparison.OrdinalIgnoreCase))
                .Take(25)
                .Select(e => new { utc = e.Utc, ip = e.Ip, result = e.Result, source = e.Source })
                .ToList();

            await req.Json(new
            {
                detail.account,
                detail.birthDate,
                detail.secretQuestion,
                detail.secretAnswerSet,
                detail.hasStoragePassword,
                detail.storagePasswordLastSet,
                detail.expandedStorage,
                detail.expandedStorageExpiry,
                detail.storageUsed,
                detail.storageSize,
                detail.auctions,
                detail.wrongPasswordCount,
                detail.characters,
                webSessions = sessions,
                self = IsSelf(req, index),
                recentLogins,
                world = WebAdminGame.WorldState,
            });
        }

        // ------------------------------------------------------------------ create / update

        private static async Task CreateAsync(WebAdminRequest req)
        {
            var body = await ReadBodyAsync(req);

            var accountId = (OptionalString(body, "accountId") ?? string.Empty).Trim();
            var password = OptionalString(body, "password") ?? string.Empty;
            var userName = (OptionalString(body, "userName") ?? string.Empty).Trim();
            var email = (OptionalString(body, "email") ?? string.Empty).Trim();
            var requireChange = OptionalBool(body, "requirePasswordChange") ?? false;

            Fail(AccountLogic.ValidateAccountId(accountId));
            Fail(AccountLogic.ValidatePassword(password));
            Fail(AccountLogic.ValidateLength(userName, AccountLogic.MaxUserNameLength, "User name"));
            Fail(AccountLogic.ValidateEmail(email));

            var (created, persist) = await AuditIfTimedOutAsync(req, "account.create", accountId, new { userName, email, requirePasswordChange = requireChange }, () => WebAdminGame.RunOrInlineAsync(() =>
            {
                EnsureAccountsLoaded();

                AccountInfo account;
                lock (Envir.AccountLock)
                {
                    if (Envir.Main.AccountExists(accountId))
                        throw new WebAdminException(409, "An account with that ID already exists.");

                    Envir.Main.CreateAccountInfo();
                    account = Envir.Main.AccountList[Envir.Main.AccountList.Count - 1];

                    account.AccountID = accountId;
                    account.Password = password;
                    account.UserName = userName;
                    account.EMailAddress = email;
                    account.CreationDate = GameNow();
                    account.CreationIP = string.Empty;
                    account.RequirePasswordChange = requireChange;
                }

                return (new { index = account.Index, accountId = account.AccountID }, PersistIfStopped());
            }));

            WebAdminAudit.Record(req, "account.create", created.accountId, new { created.index, userName, email, requirePasswordChange = requireChange, persist });

            await req.Ok(new { created.index, created.accountId, persist },
                "Account " + created.accountId + " created. Note: the server drops accounts without characters when it loads the account file, so create a character before the next restart.");
        }

        private static async Task UpdateAsync(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = await ReadBodyAsync(req);

            var userName = OptionalString(body, "userName");
            var email = OptionalString(body, "email");
            var birth = OptionalString(body, "birthDate");
            var question = OptionalString(body, "secretQuestion");
            var answer = OptionalString(body, "secretAnswer");

            userName = userName?.Trim();
            email = email?.Trim();
            question = question?.Trim();
            answer = answer?.Trim();

            if (userName != null) Fail(AccountLogic.ValidateLength(userName, AccountLogic.MaxUserNameLength, "User name"));
            if (email != null) Fail(AccountLogic.ValidateEmail(email));
            if (question != null) Fail(AccountLogic.ValidateLength(question, AccountLogic.MaxSecretLength, "Secret question"));
            if (answer != null) Fail(AccountLogic.ValidateLength(answer, AccountLogic.MaxSecretLength, "Secret answer"));

            var birthDate = DateTime.MinValue;
            if (birth != null) Fail(AccountLogic.ParseBirthDate(birth, out birthDate));

            if (userName == null && email == null && birth == null && question == null && answer == null)
                throw new WebAdminException(400, "Nothing to update");

            var (result, persist) = await OnAccountAsync(req, "account.update", index, (a, now) =>
            {
                var changes = new Dictionary<string, object>();

                if (userName != null && userName != (a.UserName ?? string.Empty))
                {
                    changes["userName"] = new { from = a.UserName, to = userName };
                    a.UserName = userName;
                }

                if (email != null && email != (a.EMailAddress ?? string.Empty))
                {
                    changes["email"] = new { from = a.EMailAddress, to = email };
                    a.EMailAddress = email;
                }

                if (birth != null && birthDate.Date != a.BirthDate.Date)
                {
                    changes["birthDate"] = new { from = AccountLogic.Iso(a.BirthDate), to = AccountLogic.Iso(birthDate) };
                    a.BirthDate = birthDate;
                }

                if (question != null && question != (a.SecretQuestion ?? string.Empty))
                {
                    changes["secretQuestion"] = new { from = a.SecretQuestion, to = question };
                    a.SecretQuestion = question;
                }

                if (answer != null && answer != (a.SecretAnswer ?? string.Empty))
                {
                    changes["secretAnswer"] = "changed"; // never log the answer itself
                    a.SecretAnswer = answer;
                }

                return new { accountId = a.AccountID, changes };
            });

            if (result.changes.Count == 0)
            {
                await req.Ok(new { index, changed = 0 }, "No changes");
                return;
            }

            WebAdminAudit.Record(req, "account.update", result.accountId, new { index, result.changes, persist });
            await req.Ok(new { index, changed = result.changes.Count, persist }, "Account " + result.accountId + " updated");
        }

        // ------------------------------------------------------------------ moderation

        private static async Task BanAsync(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = await ReadBodyAsync(req);

            var permanent = OptionalBool(body, "permanent") ?? false;
            var days = OptionalDouble(body, "days");
            var minutes = OptionalLong(body, "minutes");
            var until = OptionalString(body, "until");
            var reason = AccountLogic.NormalizeBanReason(OptionalString(body, "reason"));

            // Validate the duration up front (same rules are re-applied with the game clock below).
            Fail(AccountLogic.ResolveBanExpiry(DateTime.UtcNow, permanent, days, minutes, until, out _));

            if (IsSelf(req, index)) throw new WebAdminException(409, "You cannot ban the account you are signed in with.");

            var (result, persist) = await OnAccountAsync(req, "account.ban", index, (a, now) =>
            {
                Fail(AccountLogic.ResolveBanExpiry(now, permanent, days, minutes, until, out var expiry));

                if (a.AdminAccount && CountActiveAdmins(now, a.Index) == 0)
                    throw new WebAdminException(409, "Refused: " + a.AccountID + " is the last active admin account. Promote another admin first.");

                a.Banned = true;
                a.BanReason = reason;
                a.ExpiryDate = expiry;

                var kicked = DisconnectAccount(a, BannedDisconnectReason);
                return new { accountId = a.AccountID, admin = a.AdminAccount, expiry, permanent = AccountLogic.IsPermanent(expiry, now), kicked };
            });

            if (result.admin)
            {
                WebAdminSessions.Store.RevokeAccount(index);
                WebAdminAdmins.Refresh();
            }

            WebAdminAudit.Record(req, "account.ban", result.accountId,
                new { index, until = result.permanent ? null : AccountLogic.Iso(result.expiry), result.permanent, disconnected = result.kicked, persist }, reason);

            var msg = "Banned " + result.accountId + (result.permanent ? " permanently" : " until " + AccountLogic.Iso(result.expiry)?.ToString("u")) +
                      (result.kicked.Count > 0 ? "; disconnected " + string.Join(", ", result.kicked) : string.Empty) + ".";

            await req.Ok(new { index, until = AccountLogic.Iso(result.expiry), result.permanent, disconnected = result.kicked, persist }, msg);
        }

        private static async Task UnbanAsync(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = await req.ReadJsonAsync();
            var reason = body.ValueKind == JsonValueKind.Object ? OptionalString(body, "reason") : null;

            var (result, persist) = await OnAccountAsync(req, "account.unban", index, (a, now) =>
            {
                var was = AccountLogic.IsBanActive(a.Banned, a.ExpiryDate, now);
                var previous = new { reason = a.BanReason, until = AccountLogic.Iso(a.ExpiryDate) };

                a.Banned = false;
                a.BanReason = string.Empty;
                a.ExpiryDate = DateTime.MinValue;

                return new { accountId = a.AccountID, admin = a.AdminAccount, was, previous };
            });

            if (result.admin) WebAdminAdmins.Refresh();

            WebAdminAudit.Record(req, "account.unban", result.accountId, new { index, wasBanned = result.was, result.previous, persist }, reason);
            await req.Ok(new { index, wasBanned = result.was, persist }, result.was ? "Ban lifted for " + result.accountId : result.accountId + " was not banned (flags cleared)");
        }

        private static async Task AdminAsync(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = await ReadBodyAsync(req);
            var value = OptionalBool(body, "value") ?? throw new WebAdminException(400, "value (true/false) is required");
            var confirmSelf = OptionalBool(body, "confirmSelf") ?? false;
            var self = IsSelf(req, index);

            if (!value && self && !confirmSelf)
                throw new WebAdminException(409, "This is your own account: removing its admin flag signs you out of this panel. Confirm to continue.", new { needsConfirmSelf = true });

            var (result, persist) = await OnAccountAsync(req, "account.admin", index, (a, now) =>
            {
                var before = a.AdminAccount;
                if (before == value) return new { accountId = a.AccountID, changed = false, online = new List<string>() };

                if (!value && CountActiveAdmins(now, a.Index) == 0)
                    throw new WebAdminException(409, "Refused: " + a.AccountID + " is the last active admin account.");

                a.AdminAccount = value;

                var online = new List<string>();
                foreach (var c in a.Characters)
                {
                    if (c?.Player == null) continue;
                    online.Add(c.Name);

                    // Demotion takes GM powers away now; a promotion grants IsGM at the next login (PlayerObject ctor).
                    // GameMaster (monsters ignore the player) and Observer (invisible) are switched off too, and the GM buff icon is removed.
                    if (!value)
                    {
                        c.Player.IsGM = false;
                        c.Player.GMNeverDie = false;
                        c.Player.GMGameMaster = false;
                        c.Player.Observer = false; // the setter re-broadcasts the player to nearby clients
                        c.Player.RemoveBuff(BuffType.GameMaster);
                    }
                }

                return new { accountId = a.AccountID, changed = true, online };
            });

            if (!result.changed)
            {
                await req.Ok(new { index, admin = value }, result.accountId + (value ? " is already an admin" : " is not an admin"));
                return;
            }

            if (!value) WebAdminSessions.Store.RevokeAccount(index);
            WebAdminAdmins.Refresh();

            WebAdminAudit.Record(req, "account.admin", result.accountId, new { index, admin = value, self, onlineCharacters = result.online, persist });

            var msg = value
                ? result.accountId + " is now an admin" + (result.online.Count > 0 ? " (in-game GM powers apply after the next login)" : string.Empty)
                : "Admin flag removed from " + result.accountId;

            await req.Ok(new { index, admin = value, persist }, msg);
        }

        private static async Task RequirePasswordChangeAsync(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = await ReadBodyAsync(req);
            var value = OptionalBool(body, "value") ?? throw new WebAdminException(400, "value (true/false) is required");
            var confirmSelf = OptionalBool(body, "confirmSelf") ?? false;
            var self = IsSelf(req, index);

            if (value && self && !confirmSelf)
                throw new WebAdminException(409, "This is your own account: the panel refuses sign-in while a password change is required, so you will be signed out. Confirm to continue.", new { needsConfirmSelf = true });

            var (result, persist) = await OnAccountAsync(req, "account.require-password-change", index, (a, now) =>
            {
                if (value && a.AdminAccount && !a.RequirePasswordChange && CountActiveAdmins(now, a.Index) == 0)
                    throw new WebAdminException(409, "Refused: " + a.AccountID + " is the last active admin account; it could no longer sign in.");

                var before = a.RequirePasswordChange;
                a.RequirePasswordChange = value;
                return new { accountId = a.AccountID, admin = a.AdminAccount, before };
            });

            if (result.admin)
            {
                if (value) WebAdminSessions.Store.RevokeAccount(index);
                WebAdminAdmins.Refresh();
            }

            WebAdminAudit.Record(req, "account.require-password-change", result.accountId, new { index, from = result.before, to = value, persist });
            await req.Ok(new { index, requirePasswordChange = value, persist },
                value ? result.accountId + " must change the password at the next game login" : "Password change requirement cleared for " + result.accountId);
        }

        private static async Task PasswordAsync(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = await ReadBodyAsync(req);
            var password = OptionalString(body, "password") ?? string.Empty;
            var requireChange = OptionalBool(body, "requireChange") ?? false;
            var confirmSelf = OptionalBool(body, "confirmSelf") ?? false;
            var self = IsSelf(req, index);

            Fail(AccountLogic.ValidatePassword(password));

            if (requireChange && self && !confirmSelf)
                throw new WebAdminException(409, "This is your own account: requiring a password change signs you out of this panel. Confirm to continue.", new { needsConfirmSelf = true });

            var (result, persist) = await OnAccountAsync(req, "account.password", index, (a, now) =>
            {
                if (requireChange && a.AdminAccount && !a.RequirePasswordChange && CountActiveAdmins(now, a.Index) == 0)
                    throw new WebAdminException(409, "Refused: " + a.AccountID + " is the last active admin account; requiring a change would lock the panel.");

                a.Password = password; // the setter generates a new salt and stores the hash
                a.RequirePasswordChange = requireChange;
                return new { accountId = a.AccountID, admin = a.AdminAccount };
            });

            if (result.admin)
            {
                // Other browsers signed in with the old password are signed out; the caller keeps the current session when it is theirs.
                RevokeWebSessions(index, self && !requireChange ? req.Session?.SessionId : null);
                WebAdminAdmins.Refresh();
            }

            WebAdminAudit.Record(req, "account.password", result.accountId, new { index, requireChange, self, persist });
            await req.Ok(new { index, requireChange, persist }, "Password changed for " + result.accountId + (requireChange ? " (change required at next login)" : string.Empty));
        }

        private static async Task ClearStoragePasswordAsync(WebAdminRequest req)
        {
            var index = req.RouteInt("index");

            var (result, persist) = await OnAccountAsync(req, "account.clear-storage-password", index, (a, now) =>
            {
                var had = a.HasStoragePassword;
                a.ClearStoragePassword();

                // Mirror AccountInfoForm: lock storage again and tell online clients the password is gone.
                var notified = new List<string>();
                foreach (var c in a.Characters)
                {
                    if (c?.Player == null) continue;

                    c.Player.SetStorageUnlocked(false);
                    c.Player.ResetStorageUnlock();
                    c.Player.Enqueue(new S.StoragePasswordResult
                    {
                        Result = 4,
                        Removing = true,
                        HasPassword = false,
                        LastSetTime = DateTime.MinValue,
                    });
                    notified.Add(c.Name);
                }

                return new { accountId = a.AccountID, had, notified };
            });

            WebAdminAudit.Record(req, "account.clear-storage-password", result.accountId, new { index, hadPassword = result.had, notified = result.notified, persist });
            await req.Ok(new { index, hadPassword = result.had, persist },
                result.had ? "Storage password cleared for " + result.accountId : result.accountId + " had no storage password");
        }

        private static async Task CurrencyAsync(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = await ReadBodyAsync(req);
            var gold = OptionalLong(body, "gold");
            var credit = OptionalLong(body, "credit");
            var mode = (OptionalString(body, "mode") ?? "add").Trim().ToLowerInvariant();
            var reason = OptionalString(body, "reason");

            if (!gold.HasValue && !credit.HasValue) throw new WebAdminException(400, "Give gold and/or credit");
            if (mode != "set" && mode != "add") throw new WebAdminException(400, "Mode must be set or add");

            // Validate ranges before touching anything.
            if (gold.HasValue) Fail(AccountLogic.ApplyCurrency(0, gold.Value, mode, out _));
            if (credit.HasValue) Fail(AccountLogic.ApplyCurrency(0, credit.Value, mode, out _));

            var (result, persist) = await OnAccountAsync(req, "account.currency", index, (a, now) =>
            {
                var goldBefore = a.Gold;
                var creditBefore = a.Credit;
                var goldAfter = goldBefore;
                var creditAfter = creditBefore;

                if (gold.HasValue) Fail(AccountLogic.ApplyCurrency(goldBefore, gold.Value, mode, out goldAfter));
                if (credit.HasValue) Fail(AccountLogic.ApplyCurrency(creditBefore, credit.Value, mode, out creditAfter));

                a.Gold = goldAfter;
                a.Credit = creditAfter;

                // The client keeps its own running totals: send the same delta packets PlayerObject uses (GainGold / LoseGold, GainCredit / LoseCredit).
                var player = FirstOnlinePlayer(a);
                if (player != null)
                {
                    if (goldAfter > goldBefore) player.Enqueue(new S.GainedGold { Gold = goldAfter - goldBefore });
                    else if (goldAfter < goldBefore) player.Enqueue(new S.LoseGold { Gold = goldBefore - goldAfter });

                    if (creditAfter > creditBefore) player.Enqueue(new S.GainedCredit { Credit = creditAfter - creditBefore });
                    else if (creditAfter < creditBefore) player.Enqueue(new S.LoseCredit { Credit = creditBefore - creditAfter });
                }

                return new { accountId = a.AccountID, goldBefore, goldAfter, creditBefore, creditAfter, online = player?.Name };
            });

            WebAdminAudit.Record(req, "account.currency", result.accountId, new
            {
                index,
                mode,
                gold = new { from = result.goldBefore, to = result.goldAfter },
                credit = new { from = result.creditBefore, to = result.creditAfter },
                onlineCharacter = result.online,
                persist,
            }, reason);

            await req.Ok(new { index, gold = result.goldAfter, credit = result.creditAfter, persist },
                result.accountId + ": gold " + result.goldAfter.ToString("N0") + ", credit " + result.creditAfter.ToString("N0"));
        }

        private static async Task DeleteAsync(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = await ReadBodyAsync(req);
            var confirm = (OptionalString(body, "confirm") ?? string.Empty).Trim();
            var reason = OptionalString(body, "reason");

            if (IsSelf(req, index)) throw new WebAdminException(409, "You cannot delete the account you are signed in with.");

            var (result, persist) = await OnAccountAsync(req, "account.delete", index, (a, now) =>
            {
                if (!string.Equals(confirm, a.AccountID, StringComparison.Ordinal))
                    throw new WebAdminException(400, "Type the account ID exactly to confirm the deletion.");

                if (Envir.Main.Saving)
                    throw new WebAdminException(409, "An account save is in progress. Try again in a moment.");

                if (a.Connection != null || a.Characters.Any(c => c?.Player != null))
                    throw new WebAdminException(409, "The account is connected to the game. Disconnect it (kick or ban) first.");

                if (a.AdminAccount && CountActiveAdmins(now, a.Index) == 0)
                    throw new WebAdminException(409, "Refused: " + a.AccountID + " is the last active admin account.");

                var inGuild = a.Characters.Where(c => c != null && c.GuildIndex >= 0 && GuildName(c.GuildIndex) != null).Select(c => c.Name).ToList();
                if (inGuild.Count > 0)
                    throw new WebAdminException(409, "Remove these characters from their guild first: " + string.Join(", ", inGuild) + ".");

                var bidding = Envir.Main.Auctions.Where(x => !x.Sold && !x.Expired && x.CurrentBuyerInfo != null && x.CurrentBuyerInfo.AccountInfo == a).ToList();
                if (bidding.Count > 0)
                    throw new WebAdminException(409, "A character of this account holds the current bid on " + bidding.Count + " market listing(s).");

                // Crystal's own delete only flags characters, so nothing in Crystal expects GetCharacterInfo(Married/Mentor) to return null
                // (GetRelationship / GetMentor would throw at the partner's next login). Break the links first, like Divorce / MentorBreak do.
                var ownIndexes = new HashSet<int>(a.Characters.Where(c => c != null).Select(c => c.Index));
                var unlinked = new List<string>();
                foreach (var c in a.Characters)
                {
                    if (c == null) continue;

                    if (c.Married != 0 && !ownIndexes.Contains(c.Married))
                    {
                        var lover = Envir.Main.GetCharacterInfo(c.Married);
                        if (lover != null && lover.Married == c.Index)
                        {
                            lover.Married = 0;
                            lover.MarriedDate = now;
                            lover.Player?.GetRelationship(false);
                            unlinked.Add(lover.Name + " (spouse of " + c.Name + ")");
                        }
                    }

                    if (c.Mentor != 0 && !ownIndexes.Contains(c.Mentor))
                    {
                        var partner = Envir.Main.GetCharacterInfo(c.Mentor);
                        if (partner != null && partner.Mentor == c.Index)
                        {
                            partner.Mentor = 0;
                            partner.Player?.GetMentor(false);
                            unlinked.Add(partner.Name + (c.IsMentor ? " (mentee of " : " (mentor of ") + c.Name + ")");
                        }
                    }
                }

                // Listings sold by this account that hold another player's bid: the bid gold was taken when it was placed, so refund it
                // the way an outbid refund works (mail with the gold) before the listing is removed.
                var refunded = new List<string>();
                foreach (var x in Envir.Main.Auctions)
                {
                    if (x == null || x.Sold || x.Expired || x.CurrentBuyerInfo == null) continue;
                    if (x.SellerInfo?.AccountInfo != a || x.CurrentBuyerInfo.AccountInfo == a) continue;

                    Envir.Main.MailCharacter(x.CurrentBuyerInfo, gold: x.CurrentBid,
                        customMessage: "The market listing you bid on (" + (x.Item?.FriendlyName ?? "item") + ") was removed by an administrator. Your bid of " + x.CurrentBid.ToString("N0") + " gold is returned.");
                    refunded.Add(x.CurrentBuyerInfo.Name + ": " + x.CurrentBid);
                }

                var characters = new List<string>();
                var heroes = 0;
                foreach (var c in a.Characters)
                {
                    if (c == null) continue;
                    characters.Add(c.Name);
                    Envir.Main.CharacterList.Remove(c);
                    Envir.Main.RemoveRank(c);

                    if (c.Heroes != null)
                    {
                        foreach (var h in c.Heroes)
                        {
                            if (h != null && Envir.Main.HeroList.Remove(h)) heroes++;
                        }
                    }
                }

                var auctions = 0;
                var node = Envir.Main.Auctions.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (node.Value?.SellerInfo?.AccountInfo == a)
                    {
                        Envir.Main.Auctions.Remove(node);
                        auctions++;
                    }

                    node = next;
                }

                a.Auctions.Clear();
                Envir.Main.AccountList.Remove(a);

                return new { accountId = a.AccountID, admin = a.AdminAccount, characters, heroes, auctions, unlinked, refunded };
            });

            if (result.admin)
            {
                WebAdminSessions.Store.RevokeAccount(index);
                WebAdminAdmins.Remove(index);
            }

            WebAdminAudit.Record(req, "account.delete", result.accountId,
                new { index, characters = result.characters, heroes = result.heroes, auctionsRemoved = result.auctions, relationshipsBroken = result.unlinked, bidsRefunded = result.refunded, persist }, reason);

            await req.Ok(new { index, characters = result.characters, auctionsRemoved = result.auctions, relationshipsBroken = result.unlinked, bidsRefunded = result.refunded, persist },
                "Deleted " + result.accountId + (result.characters.Count > 0 ? " and characters " + string.Join(", ", result.characters) : string.Empty) + "." +
                (result.unlinked.Count > 0 ? " Ended links: " + string.Join(", ", result.unlinked) + "." : string.Empty) +
                (result.refunded.Count > 0 ? " Refunded " + result.refunded.Count + " bid(s) by mail." : string.Empty));
        }
    }
}

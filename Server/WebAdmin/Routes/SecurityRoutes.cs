using System.Text.Json;
using Server.MirEnvir;
using Server.MirNetwork;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Security (B3): overview (bans, chat bans, admins, web sessions, IP blocks, shared IPs, recent login failures), the login log,
    /// web session revocation and game IP blocks (Envir.IPBlocks / Envir.UpdateIPBlock).
    /// </summary>
    public sealed class SecurityRoutes : IWebAdminRoutes
    {
        public const int MaxIpBlockMinutes = 365 * 24 * 60;

        /// <summary>Crystal's "kicked by admin" disconnect reason (PlayerInfoForm Kick).</summary>
        public const byte KickedDisconnectReason = 4;

        public void Register(WebAdminRouter router)
        {
            WebAdminLoginLog.EnsureHooks();

            router.Get("/api/admin/security/overview", OverviewAsync);
            router.Get("/api/admin/security/logins", LoginsAsync);
            router.Post("/api/admin/security/sessions/revoke", RevokeSessionAsync);
            router.Post("/api/admin/security/ipblocks/add", AddIpBlockAsync);
            router.Post("/api/admin/security/ipblocks/remove", RemoveIpBlockAsync);
            router.Post("/api/admin/security/ipblocks/clear", ClearIpBlocksAsync);
        }

        private sealed class AccountData
        {
            public List<AccountSnapshot> Accounts;
            public List<object> ChatBanned;
            public DateTime Now;
        }

        private static async Task OverviewAsync(WebAdminRequest req)
        {
            var hours = Math.Clamp(req.QueryInt("hours", 24), 1, 24 * 30);

            AccountData data = null;
            string accountsError = null;

            try
            {
                data = await WebAdminGame.RunOrInlineAsync(() =>
                {
                    AccountRoutes.EnsureAccountsLoaded();
                    var now = AccountRoutes.GameNow();

                    var list = Envir.Main.AccountList;
                    var accounts = new List<AccountSnapshot>(list.Count);
                    for (var i = 0; i < list.Count; i++)
                    {
                        if (list[i] != null) accounts.Add(AccountRoutes.Snapshot(list[i], now));
                    }

                    var chatBanned = new List<object>();
                    var chars = Envir.Main.CharacterList;
                    for (var i = 0; i < chars.Count; i++)
                    {
                        var c = chars[i];
                        if (c == null || !c.ChatBanned || c.ChatBanExpiryDate <= now) continue;

                        chatBanned.Add(new
                        {
                            characterIndex = c.Index,
                            name = c.Name,
                            accountIndex = c.AccountInfo?.Index ?? 0,
                            accountId = c.AccountInfo?.AccountID,
                            until = AccountLogic.Iso(c.ChatBanExpiryDate),
                            permanent = AccountLogic.IsPermanent(c.ChatBanExpiryDate, now),
                            online = c.Player != null,
                        });
                    }

                    return new AccountData { Accounts = accounts, ChatBanned = chatBanned, Now = now };
                });
            }
            catch (WebAdminException ex) when (ex.Status == 409 || ex.Status == 503 || ex.Status == 504)
            {
                accountsError = ex.Message; // sessions, IP blocks and the login log are still useful
            }

            var nowUtc = data?.Now ?? DateTime.UtcNow;
            var accountsList = data?.Accounts ?? new List<AccountSnapshot>();

            var banned = accountsList.Where(a => a.Banned)
                .OrderBy(a => a.BanExpiry)
                .Select(a => (object)new
                {
                    index = a.Index,
                    accountId = a.AccountId,
                    reason = a.BanReason,
                    until = AccountLogic.Iso(a.BanExpiry),
                    permanent = AccountLogic.IsPermanent(a.BanExpiry, nowUtc),
                    admin = a.Admin,
                    lastIp = a.LastIp,
                })
                .ToList();

            var sessionsByAccount = WebAdminSessions.Store.List().GroupBy(s => s.AccountIndex).ToDictionary(g => g.Key, g => g.Count());

            var admins = accountsList.Where(a => a.Admin)
                .OrderBy(a => a.Index)
                .Select(a => (object)new
                {
                    index = a.Index,
                    accountId = a.AccountId,
                    banned = a.Banned,
                    requirePasswordChange = a.RequirePasswordChange,
                    online = a.Online,
                    onlineCharacter = a.OnlineCharacter,
                    lastIp = a.LastIp,
                    lastDate = AccountLogic.Iso(a.LastDate),
                    webSessions = sessionsByAccount.TryGetValue(a.Index, out var n) ? n : 0,
                })
                .ToList();

            var currentId = req.Session?.SessionId;
            var sessions = WebAdminSessions.Store.List().Select(s => (object)new
            {
                id = AccountLogic.SessionKey(s.SessionId),
                accountIndex = s.AccountIndex,
                accountId = s.AccountId,
                ip = s.Ip,
                userAgent = s.UserAgent,
                created = s.CreatedUtc,
                lastSeen = s.LastSeenUtc,
                expires = s.ExpiresUtc,
                current = string.Equals(s.SessionId, currentId, StringComparison.Ordinal),
            }).ToList();

            var blockNow = WebAdminGame.World.QueueOpen ? Envir.Main.Now : DateTime.UtcNow;
            var ipBlocks = Envir.IPBlocks
                .Where(p => p.Value > blockNow)
                .OrderByDescending(p => p.Value)
                .Select(p => (object)new
                {
                    ip = p.Key,
                    until = AccountLogic.Iso(p.Value),
                    remainingSeconds = (long)Math.Max(0, (p.Value - blockNow).TotalSeconds),
                })
                .ToList();

            var since = DateTime.UtcNow.AddHours(-hours);
            var failures = WebAdminLoginLog.QuerySince(null, "failed", since, 2000);

            await req.Json(new
            {
                hours,
                world = WebAdminGame.WorldState,
                accountsAvailable = data != null,
                accountsError,
                banned,
                chatBanned = data?.ChatBanned ?? new List<object>(),
                admins,
                sessions,
                ipBlocks,
                sharedIps = AccountLogic.SharedIps(accountsList),
                recentFailures = failures.Take(100).Select(e => new { utc = e.Utc, accountId = e.AccountId, ip = e.Ip, result = e.Result, source = e.Source }).ToList(),
                failuresByIp = WebAdminLoginLogStore.GroupFailuresByIp(failures, 25),
                counts = new
                {
                    accounts = accountsList.Count,
                    banned = banned.Count,
                    chatBanned = data?.ChatBanned?.Count ?? 0,
                    admins = admins.Count,
                    sessions = sessions.Count,
                    ipBlocks = ipBlocks.Count,
                    failures = failures.Count,
                },
            });
        }

        private static Task LoginsAsync(WebAdminRequest req)
        {
            var days = Math.Clamp(req.QueryInt("days", 1), 1, 30);
            var limit = Math.Clamp(req.QueryInt("limit", 200), 1, 2000);
            var q = req.Query["q"];
            var result = req.Query["result"];
            var source = req.Query["source"];

            var entries = WebAdminLoginLog.Query(q, result, days, limit, source);

            return req.Json(new
            {
                entries = entries.Select(e => new { id = e.Id, utc = e.Utc, accountId = e.AccountId, ip = e.Ip, result = e.Result, source = e.Source }).ToList(),
                count = entries.Count,
                limit,
                days,
            });
        }

        private static async Task RevokeSessionAsync(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync();
            var id = body.ValueKind == JsonValueKind.Object ? (body.GetString("id") ?? string.Empty).Trim() : string.Empty;
            if (id.Length == 0) throw new WebAdminException(400, "id is required");

            var session = WebAdminSessions.Store.List().FirstOrDefault(s => string.Equals(AccountLogic.SessionKey(s.SessionId), id, StringComparison.OrdinalIgnoreCase));
            if (session == null) throw new WebAdminException(404, "Session not found (it may have expired or been revoked already)");

            var self = req.Session != null && string.Equals(session.SessionId, req.Session.SessionId, StringComparison.Ordinal);
            WebAdminSessions.Store.Revoke(session.SessionId);

            WebAdminAudit.Record(req, "security.session-revoke", session.AccountId, new { id, ip = session.Ip, self });

            if (self) WebAdminSessions.WriteClearCookies(req);

            await req.Ok(new { id, self }, self ? "Your session was revoked; you are signed out." : "Session of " + session.AccountId + " revoked");
        }

        private static async Task AddIpBlockAsync(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync();
            if (body.ValueKind != JsonValueKind.Object) throw new WebAdminException(400, "A JSON object body is required");

            var ip = AccountLogic.NormalizeIp(body.GetString("ip"));
            if (ip == null) throw new WebAdminException(400, "Enter an IPv4 address such as 203.0.113.7");

            var minutes = body.GetInt("minutes", 60);
            if (minutes < 1 || minutes > MaxIpBlockMinutes) throw new WebAdminException(400, "Minutes must be between 1 and " + MaxIpBlockMinutes);

            var disconnect = body.GetBool("disconnect");
            var reason = body.GetString("reason");

            var result = await AccountRoutes.AuditIfTimedOutAsync(req, "security.ipblock-add", ip, new { minutes, disconnect, reason }, () => WebAdminGame.RunOrInlineAsync(() =>
            {
                var span = TimeSpan.FromMinutes(minutes);
                DateTime until;

                if (WebAdminGame.World.QueueOpen)
                {
                    Envir.Main.UpdateIPBlock(ip, span);
                    until = Envir.IPBlocks.TryGetValue(ip, out var u) ? u : Envir.Main.Now.Add(span);
                }
                else
                {
                    until = DateTime.UtcNow.Add(span);
                    Envir.IPBlocks[ip] = until;
                }

                var kicked = 0;
                if (disconnect && WebAdminGame.World.QueueOpen)
                {
                    List<MirConnection> targets;
                    lock (Envir.Main.Connections)
                        targets = Envir.Main.Connections.Where(c => c != null && c.IPAddress == ip).ToList();

                    foreach (var c in targets)
                    {
                        try
                        {
                            c.SendDisconnect(KickedDisconnectReason);
                            kicked++;
                        }
                        catch (Exception ex)
                        {
                            WebAdminLog.Error(ex, "Disconnect of " + ip + " failed");
                        }
                    }
                }

                return new { until, kicked };
            }));

            WebAdminAudit.Record(req, "security.ipblock-add", ip, new { minutes, until = AccountLogic.Iso(result.until), disconnected = result.kicked }, reason);

            await req.Ok(new { ip, until = AccountLogic.Iso(result.until), disconnected = result.kicked },
                "Blocked " + ip + " for " + minutes + " minute(s)" + (result.kicked > 0 ? "; disconnected " + result.kicked + " connection(s)" : string.Empty));
        }

        private static async Task RemoveIpBlockAsync(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync();
            var raw = body.ValueKind == JsonValueKind.Object ? body.GetString("ip") : null;
            var ip = AccountLogic.NormalizeIp(raw) ?? raw?.Trim();
            if (string.IsNullOrEmpty(ip)) throw new WebAdminException(400, "ip is required");

            var removed = await AccountRoutes.AuditIfTimedOutAsync(req, "security.ipblock-remove", ip, null, () => WebAdminGame.RunOrInlineAsync(() => Envir.IPBlocks.TryRemove(ip, out _)));
            if (!removed) throw new WebAdminException(404, ip + " is not blocked");

            WebAdminAudit.Record(req, "security.ipblock-remove", ip);
            await req.Ok(new { ip }, "Unblocked " + ip);
        }

        private static async Task ClearIpBlocksAsync(WebAdminRequest req)
        {
            var count = await AccountRoutes.AuditIfTimedOutAsync(req, "security.ipblock-clear", "all", null, () => WebAdminGame.RunOrInlineAsync(() =>
            {
                var n = Envir.IPBlocks.Count;
                Envir.IPBlocks.Clear();
                return n;
            }));

            WebAdminAudit.Record(req, "security.ipblock-clear", "all", new { removed = count });
            await req.Ok(new { removed = count }, count == 0 ? "No IP blocks to clear" : "Cleared " + count + " IP block(s)");
        }
    }
}

using System.Text.Json;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirObjects;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Guilds (B5): list/detail over <c>Envir.Main.GuildList</c> (readable while the world is stopped) and live mutations on the
    /// <see cref="GuildObject"/> (game thread; 503 when the world is not running).
    /// <code>
    /// GET  /api/admin/guilds?q=&amp;sort=&amp;dir=&amp;page=&amp;pageSize=   → {rows, total, page, pages, pageSize, world}
    /// GET  /api/admin/guilds/{index}                         → detail (ranks/members, notice, storage, buffs, wars, conquests)
    /// POST /api/admin/guilds/{index}/notice {lines[]}
    /// POST /api/admin/guilds/{index}/members/remove {name, force?}
    /// POST /api/admin/guilds/{index}/message {text}
    /// POST /api/admin/guilds/{index}/funds {gold?, sparePoints?, mode?: set|add}
    /// </code>
    /// </summary>
    public sealed class GuildRoutes : IWebAdminRoutes
    {
        /// <summary>Same cap the game applies to a player's notice edit (PlayerObject.EditGuildNotice).</summary>
        public const int MaxNoticeLines = 200;
        public const int MaxNoticeLineLength = 500;
        public const int MaxMessageLength = 200;

        public void Register(WebAdminRouter router)
        {
            router.Get("/api/admin/guilds", ListGuilds);
            router.Get("/api/admin/guilds/{index:int}", GetGuild);
            router.Post("/api/admin/guilds/{index:int}/notice", UpdateNotice);
            router.Post("/api/admin/guilds/{index:int}/members/remove", RemoveMember);
            router.Post("/api/admin/guilds/{index:int}/message", SendMessage);
            router.Post("/api/admin/guilds/{index:int}/funds", UpdateFunds);
        }

        // ------------------------------------------------------------------ list

        private static async Task ListGuilds(WebAdminRequest req)
        {
            var running = WebAdminGame.IsRunning;

            var rows = await WebAdminGame.RunOrInlineAsync(() =>
            {
                var envir = Envir.Main;
                var list = new List<GuildRow>(envir.GuildList.Count);

                for (var i = 0; i < envir.GuildList.Count; i++)
                {
                    var info = envir.GuildList[i];
                    if (info == null) continue;
                    list.Add(BuildRow(envir, info, running));
                }

                return list;
            }).ConfigureAwait(false);

            var filtered = GuildQuery.Apply(rows, req.Query["q"], req.Query["sort"], req.Query["dir"]);
            var paging = GuildMarketPaging.Compute(filtered.Count, req.QueryInt("page", 1), req.QueryInt("pageSize", 50));

            await req.Json(new
            {
                rows = filtered.Skip(paging.Skip).Take(paging.PageSize).ToList(),
                total = filtered.Count,
                page = paging.Page,
                pages = paging.Pages,
                pageSize = paging.PageSize,
                world = WebAdminGame.WorldState,
            }).ConfigureAwait(false);
        }

        private static GuildRow BuildRow(Envir envir, GuildInfo info, bool running)
        {
            var online = 0;
            if (running)
            {
                for (var r = 0; r < info.Ranks.Count; r++)
                {
                    var rank = info.Ranks[r];
                    for (var m = 0; m < rank.Members.Count; m++)
                    {
                        if (rank.Members[m].Player != null) online++;
                    }
                }
            }

            var wars = 0;
            if (running)
            {
                for (var w = 0; w < envir.GuildsAtWar.Count; w++)
                {
                    var war = envir.GuildsAtWar[w];
                    if (war?.GuildA?.Info == info || war?.GuildB?.Info == info) wars++;
                }
            }

            string conquest = null;
            for (var c = 0; c < envir.ConquestList.Count; c++)
            {
                var cg = envir.ConquestList[c];
                if (cg != null && cg.Owner == info.GuildIndex && cg.Info != null)
                {
                    conquest = conquest == null ? cg.Info.Name : conquest + ", " + cg.Info.Name;
                }
            }

            return new GuildRow
            {
                Index = info.GuildIndex,
                Name = info.Name ?? string.Empty,
                Leader = LeaderOf(info),
                Members = info.Membercount,
                MemberCap = info.MemberCap,
                Level = info.Level,
                Experience = info.Experience,
                MaxExperience = info.MaxExperience,
                Gold = info.Gold,
                SparePoints = info.SparePoints,
                Gt = info.HasGT ? info.GTRent : (DateTime?)null,
                GtIndex = info.GTIndex,
                WarCount = wars,
                OnlineMembers = online,
                Conquest = conquest,
            };
        }

        /// <summary>Ranks[0].Members[0] or "—" (WinForms showed "DELETED").</summary>
        internal static string LeaderOf(GuildInfo info)
        {
            if (info?.Ranks == null || info.Ranks.Count == 0) return "—";
            var members = info.Ranks[0]?.Members;
            if (members == null || members.Count == 0 || string.IsNullOrEmpty(members[0]?.Name)) return "—";
            return members[0].Name;
        }

        // ------------------------------------------------------------------ detail

        private static async Task GetGuild(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var running = WebAdminGame.IsRunning;

            var detail = await WebAdminGame.RunOrInlineAsync(() =>
            {
                var envir = Envir.Main;
                var info = envir.GuildList.FirstOrDefault(g => g != null && g.GuildIndex == index);
                if (info == null) throw new WebAdminException(404, "Guild not found");

                return BuildDetail(envir, info, running);
            }).ConfigureAwait(false);

            await req.Json(detail).ConfigureAwait(false);
        }

        private static object BuildDetail(Envir envir, GuildInfo info, bool running)
        {
            var row = BuildRow(envir, info, running);

            var characters = new Dictionary<int, CharacterInfo>();
            for (var i = 0; i < envir.CharacterList.Count; i++)
            {
                var c = envir.CharacterList[i];
                if (c != null) characters[c.Index] = c;
            }

            var ranks = new List<object>();
            for (var r = 0; r < info.Ranks.Count; r++)
            {
                var rank = info.Ranks[r];
                if (rank == null) continue;

                var members = new List<object>();
                for (var m = 0; m < rank.Members.Count; m++)
                {
                    var member = rank.Members[m];
                    if (member == null) continue;

                    characters.TryGetValue(member.Id, out var ci);
                    members.Add(new
                    {
                        name = member.Name,
                        characterIndex = member.Id,
                        rank = rank.Name,
                        rankIndex = r,
                        online = running && member.Player != null,
                        lastLogin = member.LastLogin,
                        level = ci != null ? (int?)ci.Level : null,
                        className = ci?.Class.ToString(),
                        accountId = ci?.AccountInfo?.AccountID,
                        accountIndex = ci?.AccountInfo != null ? (int?)ci.AccountInfo.Index : null,
                        deleted = ci != null && ci.Deleted,
                    });
                }

                ranks.Add(new
                {
                    index = r,
                    name = rank.Name,
                    options = rank.Options.ToString(),
                    members,
                });
            }

            var storage = new List<object>();
            if (info.StoredItems != null)
            {
                for (var s = 0; s < info.StoredItems.Length; s++)
                {
                    var stored = info.StoredItems[s];
                    if (stored?.Item == null) continue;

                    string storedBy;
                    if (stored.UserId == -1) storedBy = "Server";
                    else if (stored.UserId >= int.MinValue && stored.UserId <= int.MaxValue && characters.TryGetValue((int)stored.UserId, out var owner)) storedBy = owner.Name;
                    else storedBy = "Unknown";

                    storage.Add(new
                    {
                        slot = s,
                        uniqueId = stored.Item.UniqueID,
                        itemIndex = stored.Item.ItemIndex,
                        name = stored.Item.Info?.FriendlyName ?? ("#" + stored.Item.ItemIndex),
                        count = (int)stored.Item.Count,
                        currentDura = (int)stored.Item.CurrentDura,
                        maxDura = (int)stored.Item.MaxDura,
                        storedBy,
                        storedById = stored.UserId,
                    });
                }
            }

            var buffs = new List<object>();
            var catalogue = Settings.Guild_BuffList ?? new List<GuildBuffInfo>();
            var seen = new HashSet<int>();
            for (var b = 0; b < catalogue.Count; b++)
            {
                var bi = catalogue[b];
                if (bi == null) continue;
                seen.Add(bi.Id);
                var owned = info.BuffList.FirstOrDefault(x => x != null && x.Id == bi.Id);
                buffs.Add(BuffDto(bi.Id, bi, owned));
            }

            for (var b = 0; b < info.BuffList.Count; b++)
            {
                var owned = info.BuffList[b];
                if (owned == null || seen.Contains(owned.Id)) continue;
                buffs.Add(BuffDto(owned.Id, owned.Info, owned));
            }

            var wars = new List<object>();
            if (running)
            {
                for (var w = 0; w < envir.GuildsAtWar.Count; w++)
                {
                    var war = envir.GuildsAtWar[w];
                    if (war == null) continue;

                    GuildObject enemy = null;
                    if (war.GuildA?.Info == info) enemy = war.GuildB;
                    else if (war.GuildB?.Info == info) enemy = war.GuildA;
                    else continue;

                    wars.Add(new
                    {
                        guildIndex = enemy?.Guildindex ?? -1,
                        guild = enemy?.Name ?? "?",
                        remainingMinutes = Math.Max(0, war.TimeRemaining / Settings.Minute),
                    });
                }
            }

            var conquests = new List<object>();
            for (var c = 0; c < envir.ConquestList.Count; c++)
            {
                var cg = envir.ConquestList[c];
                if (cg == null || cg.Info == null) continue;

                var owner = cg.Owner == info.GuildIndex;
                var attacker = cg.AttackerID == info.GuildIndex && cg.AttackerID != 0;
                if (!owner && !attacker) continue;

                var live = running ? envir.Conquests.FirstOrDefault(x => x != null && x.GuildInfo == cg) : null;
                conquests.Add(new
                {
                    index = cg.Info.Index,
                    name = cg.Info.Name,
                    role = owner ? "owner" : "attacker",
                    goldStorage = cg.GoldStorage,
                    npcRate = (int)cg.NPCRate,
                    warOn = live != null && live.WarIsOn,
                });
            }

            return new
            {
                index = row.Index,
                name = row.Name,
                leader = row.Leader,
                members = row.Members,
                memberCap = row.MemberCap,
                level = row.Level,
                experience = row.Experience,
                maxExperience = row.MaxExperience,
                gold = row.Gold,
                sparePoints = row.SparePoints,
                gt = row.Gt,
                gtIndex = info.GTIndex,
                gtPrice = info.GTPrice,
                warCount = row.WarCount,
                onlineMembers = row.OnlineMembers,
                conquest = row.Conquest,
                flagImage = (int)info.FlagImage,
                flagColour = "#" + (info.FlagColour.ToArgb() & 0xFFFFFF).ToString("X6"),
                voting = info.Voting,
                isNewbieGuild = string.Equals(info.Name, Settings.NewbieGuild, StringComparison.Ordinal),
                live = running,
                world = WebAdminGame.WorldState,
                notice = info.Notice != null ? new List<string>(info.Notice) : new List<string>(),
                ranks,
                storage,
                storageSlots = info.StoredItems?.Length ?? 0,
                buffs,
                wars,
                conquests,
            };
        }

        private static object BuffDto(int id, GuildBuffInfo bi, GuildBuff owned)
        {
            var stats = new List<object>();
            if (bi?.Stats?.Values != null)
            {
                foreach (var pair in bi.Stats.Values)
                {
                    if (pair.Value == 0) continue;
                    stats.Add(new { stat = pair.Key.ToString(), value = pair.Value });
                }
            }

            return new
            {
                id,
                name = bi?.Name ?? ("Buff " + id),
                known = bi != null,
                icon = bi?.Icon ?? 0,
                levelRequirement = bi != null ? (int)bi.LevelRequirement : 0,
                pointsRequirement = bi != null ? (int)bi.PointsRequirement : 0,
                timeLimit = bi?.TimeLimit ?? 0,
                activationCost = bi?.ActivationCost ?? 0,
                stats,
                owned = owned != null,
                active = owned != null && owned.Active,
                infinite = bi != null && bi.TimeLimit == 0,
                remainingMinutes = owned != null && owned.Active ? Math.Max(0, owned.ActiveTimeRemaining) : 0,
            };
        }

        // ------------------------------------------------------------------ mutations (live, game thread)

        private static GuildObject LiveGuild(int index)
        {
            var guild = Envir.Main.GetGuild(index);
            if (guild == null) throw new WebAdminException(404, "Guild not found (or not loaded in the running world)");
            return guild;
        }

        private static async Task UpdateNotice(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = await req.ReadJsonAsync().ConfigureAwait(false);

            var lines = GuildQuery.NormaliseNotice(ReadLines(body, "lines"));
            if (lines == null) throw new WebAdminException(400, "Body must contain lines: [string]");
            if (lines.Count > MaxNoticeLines) throw new WebAdminException(422, "A guild notice can have at most " + MaxNoticeLines + " lines");
            if (lines.Any(l => l.Length > MaxNoticeLineLength)) throw new WebAdminException(422, "Notice lines are limited to " + MaxNoticeLineLength + " characters");

            var admin = req.AdminName;
            var result = await AccountRoutes.AuditIfTimedOutAsync(req, "guild.notice", "guild #" + index, new { index, lines = lines.Count }, () => WebAdminGame.RunAsync(() =>
            {
                var guild = LiveGuild(index);
                var before = guild.Info.Notice?.Count ?? 0;

                guild.NewNotice(new List<string>(lines));

                var log = "Guild: '" + guild.Name + "' notice changed by " + admin + " (web admin).";
                MessageQueue.Instance.EnqueueChat(log);

                return new { name = guild.Name, before, after = lines.Count };
            })).ConfigureAwait(false);

            WebAdminAudit.Record(req, "guild.notice", result.name + " (#" + index + ")", new { linesBefore = result.before, linesAfter = result.after, notice = lines.Take(50).ToList() });
            await req.Ok(new { lines = result.after }, "Notice updated for " + result.name + ".").ConfigureAwait(false);
        }

        private static async Task RemoveMember(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var name = (body.GetString("name") ?? string.Empty).Trim();
            var force = body.GetBool("force");
            var reason = body.GetString("reason");
            if (name.Length == 0) throw new WebAdminException(400, "name is required");

            var result = await AccountRoutes.AuditIfTimedOutAsync(req, "guild.member-remove", "guild #" + index, new { index, name, force, reason }, () => WebAdminGame.RunAsync(() =>
            {
                var guild = LiveGuild(index);

                GuildMember member = null;
                GuildRank memberRank = null;
                var rankPosition = -1;

                // exact name first (DeleteMember compares ordinally), then a unique case-insensitive match
                for (var pass = 0; pass < 2 && member == null; pass++)
                {
                    for (var r = 0; r < guild.Ranks.Count && member == null; r++)
                    {
                        var rank = guild.Ranks[r];
                        for (var m = 0; m < rank.Members.Count; m++)
                        {
                            var candidate = rank.Members[m];
                            var match = pass == 0
                                ? string.Equals(candidate.Name, name, StringComparison.Ordinal)
                                : string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase);
                            if (!match) continue;

                            member = candidate;
                            memberRank = rank;
                            rankPosition = r;
                            break;
                        }
                    }
                }

                if (member == null) throw new WebAdminException(404, "'" + name + "' is not a member of " + guild.Name);

                var soleLeader = rankPosition == 0 && memberRank.Members.Count == 1;
                var lastMember = guild.Info.Membercount <= 1;

                if ((soleLeader || lastMember) && !force)
                {
                    throw new WebAdminException(409,
                        lastMember
                            ? member.Name + " is the last member: removing them disbands the guild. Confirm with force."
                            : member.Name + " is the only leader: the guild would have no leader. Confirm with force.",
                        new { needsForce = true, soleLeader, lastMember });
                }

                var wasOnline = member.Player != null;
                var memberName = member.Name;
                var rankName = memberRank.Name;

                // Same steps as GuildObject.DeleteMember(string), but reported as a kick (kickSelf: false): the removed player gets
                // "removed from guild" and the other members see status 3 (kicked) instead of 4 (left voluntarily).
                var formerPlayer = member.Player as PlayerObject;
                guild.MemberDeleted(memberName, formerPlayer, false);
                if (formerPlayer != null) formerPlayer.RefreshStats();
                memberRank.Members.Remove(member);
                guild.NeedSave = true;
                guild.Info.Membercount--;

                var disbanded = false;
                if (lastMember)
                {
                    // Mirror the in-game "last leader leaves" path (GuildObject.DeleteMember(kicker, ..) → Envir.DeleteGuild).
                    Envir.Main.DeleteGuild(guild);
                    disbanded = true;
                }

                MessageQueue.Instance.EnqueueChat("Guild: '" + guild.Name + "' member " + memberName + " removed by web admin.");

                return new { guild = guild.Name, member = memberName, rank = rankName, wasOnline, disbanded };
            })).ConfigureAwait(false);

            WebAdminAudit.Record(req, "guild.member-remove", result.guild + " (#" + index + ")",
                new { member = result.member, rank = result.rank, result.wasOnline, result.disbanded, force },
                string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());

            await req.Ok(result, result.disbanded
                ? result.member + " removed; " + result.guild + " had no members left and was disbanded."
                : result.member + " removed from " + result.guild + ".").ConfigureAwait(false);
        }

        private static async Task SendMessage(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var text = GuildQuery.CleanMessage(body.GetString("text"));
            if (text.Length == 0) throw new WebAdminException(400, "text is required");
            if (text.Length > MaxMessageLength) throw new WebAdminException(422, "Messages are limited to " + MaxMessageLength + " characters");

            var result = await AccountRoutes.AuditIfTimedOutAsync(req, "guild.message", "guild #" + index, new { index, text }, () => WebAdminGame.RunAsync(() =>
            {
                var guild = LiveGuild(index);
                guild.SendMessage("SYSTEM: " + text, ChatType.Guild);
                MessageQueue.Instance.EnqueueChat("SYSTEM to Guild: '" + guild.Name + "': " + text);
                return new { guild = guild.Name, online = guild.GetOnlinePlayers().Count };
            })).ConfigureAwait(false);

            WebAdminAudit.Record(req, "guild.message", result.guild + " (#" + index + ")", new { text, recipients = result.online });
            await req.Ok(result, "Sent to " + result.online + " online member(s) of " + result.guild + ".").ConfigureAwait(false);
        }

        private static async Task UpdateFunds(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = await req.ReadJsonAsync().ConfigureAwait(false);

            var mode = (body.GetString("mode") ?? "set").Trim().ToLowerInvariant();
            if (mode != "set" && mode != "add") throw new WebAdminException(400, "mode must be set or add");

            var hasGold = TryReadLong(body, "gold", out var goldValue);
            var hasPoints = TryReadLong(body, "sparePoints", out var pointsValue);
            if (!hasGold && !hasPoints) throw new WebAdminException(400, "Provide gold and/or sparePoints");

            var reason = body.GetString("reason");

            var result = await AccountRoutes.AuditIfTimedOutAsync(req, "guild.funds", "guild #" + index, new { index, mode, gold = hasGold ? goldValue : (long?)null, sparePoints = hasPoints ? pointsValue : (long?)null, reason }, () => WebAdminGame.RunAsync(() =>
            {
                var guild = LiveGuild(index);
                var info = guild.Info;

                var goldBefore = info.Gold;
                var pointsBefore = info.SparePoints;

                var goldAfter = goldBefore;
                var pointsAfter = pointsBefore;

                if (hasGold)
                {
                    if (!GuildQuery.TryApplyAmount(goldBefore, goldValue, mode == "add", uint.MaxValue, out var g))
                        throw new WebAdminException(422, "Gold must end up between 0 and " + uint.MaxValue.ToString("N0"));
                    goldAfter = (uint)g;
                }

                if (hasPoints)
                {
                    if (!GuildQuery.TryApplyAmount(pointsBefore, pointsValue, mode == "add", byte.MaxValue, out var p))
                        throw new WebAdminException(422, "Spare points must end up between 0 and " + byte.MaxValue);
                    pointsAfter = (byte)p;
                }

                info.Gold = goldAfter;
                info.SparePoints = pointsAfter;
                guild.NeedSave = true;

                var online = guild.GetOnlinePlayers();
                for (var i = 0; i < online.Count; i++)
                    guild.SendGuildStatus(online[i]);

                return new
                {
                    guild = guild.Name,
                    goldBefore,
                    goldAfter,
                    pointsBefore = (int)pointsBefore,
                    pointsAfter = (int)pointsAfter,
                };
            })).ConfigureAwait(false);

            WebAdminAudit.Record(req, "guild.funds", result.guild + " (#" + index + ")", new
            {
                mode,
                gold = hasGold ? new { before = result.goldBefore, after = result.goldAfter } : null,
                sparePoints = hasPoints ? new { before = result.pointsBefore, after = result.pointsAfter } : null,
            }, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());

            await req.Ok(new { gold = result.goldAfter, sparePoints = result.pointsAfter },
                result.guild + ": gold " + result.goldAfter.ToString("N0") + ", spare points " + result.pointsAfter + ".").ConfigureAwait(false);
        }

        // ------------------------------------------------------------------ helpers

        private static List<string> ReadLines(JsonElement body, string name)
        {
            if (!body.TryGetPropertyIgnoreCase(name, out var v)) return null;

            if (v.ValueKind == JsonValueKind.String)
            {
                return (v.GetString() ?? string.Empty).Replace("\r\n", "\n").Split('\n').ToList();
            }

            if (v.ValueKind != JsonValueKind.Array) return null;

            var list = new List<string>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString() ?? string.Empty);
                else if (item.ValueKind == JsonValueKind.Null) list.Add(string.Empty);
                else list.Add(item.GetRawText());
            }

            return list;
        }

        private static bool TryReadLong(JsonElement body, string name, out long value)
        {
            value = 0;
            if (!body.TryGetPropertyIgnoreCase(name, out var v)) return false;

            switch (v.ValueKind)
            {
                case JsonValueKind.Number:
                    if (v.TryGetInt64(out value)) return true;
                    throw new WebAdminException(400, name + " must be a whole number");
                case JsonValueKind.String:
                    var s = v.GetString();
                    if (string.IsNullOrWhiteSpace(s)) return false;
                    if (long.TryParse(s.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value)) return true;
                    throw new WebAdminException(400, name + " must be a whole number");
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return false;
                default:
                    throw new WebAdminException(400, name + " must be a whole number");
            }
        }
    }

    /// <summary>Row of GET /api/admin/guilds.</summary>
    public sealed class GuildRow
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public string Leader { get; set; }
        public int Members { get; set; }
        public int MemberCap { get; set; }
        public int Level { get; set; }
        public long Experience { get; set; }
        public long MaxExperience { get; set; }
        public uint Gold { get; set; }
        public int SparePoints { get; set; }

        /// <summary>Guild territory rent expiry (null = no GT).</summary>
        public DateTime? Gt { get; set; }

        public int GtIndex { get; set; }
        public int WarCount { get; set; }
        public int OnlineMembers { get; set; }

        /// <summary>Names of the conquests the guild owns (null = none).</summary>
        public string Conquest { get; set; }
    }

    /// <summary>Pure guild-list helpers (filter / sort / validation), unit-tested.</summary>
    public static class GuildQuery
    {
        public static readonly string[] SortKeys = { "index", "name", "leader", "members", "level", "experience", "gold", "sparepoints", "gt", "wars", "online" };

        public static List<GuildRow> Apply(IEnumerable<GuildRow> rows, string q, string sort, string dir)
        {
            var list = (rows ?? Enumerable.Empty<GuildRow>()).Where(r => r != null);

            q = q?.Trim();
            if (!string.IsNullOrEmpty(q))
            {
                var isNumber = int.TryParse(q, out var number);
                list = list.Where(r =>
                    (r.Name ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || (r.Leader ?? string.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || (isNumber && r.Index == number));
            }

            var desc = string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);
            var key = (sort ?? "index").Trim().ToLowerInvariant();

            IOrderedEnumerable<GuildRow> ordered;
            switch (key)
            {
                case "name":
                    ordered = Order(list, r => r.Name ?? string.Empty, desc, StringComparer.OrdinalIgnoreCase);
                    break;
                case "leader":
                    ordered = Order(list, r => r.Leader ?? string.Empty, desc, StringComparer.OrdinalIgnoreCase);
                    break;
                case "members":
                    ordered = Order(list, r => r.Members, desc);
                    break;
                case "level":
                    ordered = desc
                        ? list.OrderByDescending(r => r.Level).ThenByDescending(r => r.Experience)
                        : list.OrderBy(r => r.Level).ThenBy(r => r.Experience);
                    break;
                case "experience":
                    ordered = Order(list, r => r.Experience, desc);
                    break;
                case "gold":
                    ordered = Order(list, r => r.Gold, desc);
                    break;
                case "sparepoints":
                    ordered = Order(list, r => r.SparePoints, desc);
                    break;
                case "gt":
                    ordered = Order(list, r => r.Gt ?? DateTime.MinValue, desc);
                    break;
                case "wars":
                    ordered = Order(list, r => r.WarCount, desc);
                    break;
                case "online":
                    ordered = Order(list, r => r.OnlineMembers, desc);
                    break;
                default:
                    ordered = Order(list, r => r.Index, desc);
                    break;
            }

            return ordered.ThenBy(r => r.Index).ToList();
        }

        private static IOrderedEnumerable<GuildRow> Order<TKey>(IEnumerable<GuildRow> rows, Func<GuildRow, TKey> key, bool desc, IComparer<TKey> comparer = null)
        {
            return desc ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);
        }

        /// <summary>Notice lines: CR/LF split, trailing whitespace trimmed, trailing empty lines dropped. Null stays null.</summary>
        public static List<string> NormaliseNotice(IEnumerable<string> lines)
        {
            if (lines == null) return null;

            var list = new List<string>();
            foreach (var raw in lines)
            {
                var text = (raw ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
                foreach (var part in text.Split('\n'))
                    list.Add(StripControl(part).TrimEnd());
            }

            while (list.Count > 0 && list[list.Count - 1].Length == 0)
                list.RemoveAt(list.Count - 1);

            return list;
        }

        /// <summary>One-line chat text: control characters (incl. newlines) become spaces, trimmed.</summary>
        public static string CleanMessage(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var chars = text.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (char.IsControl(chars[i])) chars[i] = ' ';
            }

            return new string(chars).Trim();
        }

        /// <summary>set: result = value; add: result = current + value. False when the result leaves [0, max].</summary>
        public static bool TryApplyAmount(long current, long value, bool add, long max, out long result)
        {
            result = current;
            long next;

            if (add)
            {
                try
                {
                    next = checked(current + value);
                }
                catch (OverflowException)
                {
                    return false;
                }
            }
            else
            {
                next = value;
            }

            if (next < 0 || next > max) return false;

            result = next;
            return true;
        }

        private static string StripControl(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            var chars = s.ToCharArray();
            var changed = false;
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] != '\t' && char.IsControl(chars[i]))
                {
                    chars[i] = ' ';
                    changed = true;
                }
            }

            return changed ? new string(chars) : s;
        }
    }

    /// <summary>Shared page arithmetic for the guild and market lists.</summary>
    public static class GuildMarketPaging
    {
        public const int MaxPageSize = 500;

        public readonly struct Result
        {
            public Result(int page, int pageSize, int pages, int skip)
            {
                Page = page;
                PageSize = pageSize;
                Pages = pages;
                Skip = skip;
            }

            public int Page { get; }
            public int PageSize { get; }
            public int Pages { get; }
            public int Skip { get; }
        }

        /// <summary>Clamps page to [1, pages] (pages ≥ 1) and pageSize to [1, MaxPageSize] (≤ 0 → 50).</summary>
        public static Result Compute(int total, int page, int pageSize)
        {
            if (pageSize <= 0) pageSize = 50;
            if (pageSize > MaxPageSize) pageSize = MaxPageSize;
            if (total < 0) total = 0;

            var pages = Math.Max(1, (total + pageSize - 1) / pageSize);
            if (page < 1) page = 1;
            if (page > pages) page = pages;

            return new Result(page, pageSize, pages, (page - 1) * pageSize);
        }
    }
}

using System.Globalization;
using System.Text.Json;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirObjects;
using S = ServerPackets;

namespace Server.WebAdmin.Routes
{
    /// <summary>Result of a character mutation run on the game thread (or inline while the world is stopped).</summary>
    internal sealed class CharacterMutation
    {
        public string Target;
        public string Message;
        public object Details;
        public object Data;
        public bool Changed = true;
    }

    /// <summary>Shared helpers for the character routes (lookup, persistence while stopped, lenient body parsing).</summary>
    internal static class CharacterRouteUtil
    {
        /// <summary>Envir.Main character by index, or 404. Call on the game thread / inline.</summary>
        public static CharacterInfo Find(int index)
        {
            var ci = Envir.Main.GetCharacterInfo(index);
            if (ci == null) throw new WebAdminException(404, "Character not found");
            return ci;
        }

        public static string Label(CharacterInfo ci) => ci.Name + " (#" + ci.Index + ")";

        /// <summary>
        /// While the world is stopped nothing saves accounts and the next world start reloads UserData.db, so inline edits are written
        /// immediately (accounts + world, like AccountInfoForm does on close). While running the periodic save persists them.
        /// </summary>
        public static void PersistIfStopped()
        {
            if (WebAdminGame.IsRunning) return;

            var envir = Envir.Main;
            if (!envir.UserDataLoaded || envir.AccountList.Count == 0 || envir.Saving) return; // never write a not-loaded / half-saved account DB

            envir.SaveAccounts();
        }

        /// <summary>The online PlayerObject of any character of the account (gold/credit are per account).</summary>
        public static PlayerObject OnlinePlayerOf(AccountInfo acc)
        {
            if (acc?.Characters == null) return null;
            for (var i = 0; i < acc.Characters.Count; i++)
            {
                var p = acc.Characters[i]?.Player;
                if (p != null) return p;
            }

            return null;
        }

        public static JsonElement RequireObject(JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object) throw new WebAdminException(400, "A JSON object body is required");
            return body;
        }

        /// <summary>True when the property exists and is not null.</summary>
        public static bool Has(JsonElement body, string name, out JsonElement value)
        {
            if (body.TryGetPropertyIgnoreCase(name, out value) && value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined)
                return true;
            value = default;
            return false;
        }

        /// <summary>Optional integer property in [min, max]; null when absent; 400 when invalid.</summary>
        public static long? OptionalLong(JsonElement body, string name, long min, long max)
        {
            if (!Has(body, name, out var v)) return null;

            long n = 0;
            var ok = (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out n))
                || (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString().Replace(",", string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n));
            if (!ok) throw new WebAdminException(400, name + " must be a whole number");

            if (n < min || n > max) throw new WebAdminException(422, name + " must be between " + min + " and " + max);
            return n;
        }

        public static string OptionalString(JsonElement body, string name)
        {
            if (!Has(body, name, out var v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
        }

        public static bool? OptionalBool(JsonElement body, string name)
        {
            if (!Has(body, name, out _)) return null;
            return body.GetBool(name);
        }

        public static string Reason(JsonElement body)
        {
            var r = OptionalString(body, "reason");
            if (string.IsNullOrWhiteSpace(r)) return null;
            r = r.Trim();
            return r.Length > 500 ? r.Substring(0, 500) : r;
        }
    }

    /// <summary>Cached flag names from <c>Envir\SET [].txt</c> (reloaded when the file changes).</summary>
    internal static class CharacterFlagNames
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static DateTime _stamp;
        private static Dictionary<int, string> _names = new Dictionary<int, string>();

        public static IReadOnlyDictionary<int, string> Get()
        {
            try
            {
                var path = Path.Combine(Settings.EnvirPath, "SET [].txt");
                if (!File.Exists(path)) return new Dictionary<int, string>();

                var stamp = File.GetLastWriteTimeUtc(path);
                lock (Gate)
                {
                    if (path == _path && stamp == _stamp) return _names;
                }

                var lines = new List<string>();
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) lines.Add(line);
                }

                var names = CharacterLogic.ParseFlagNames(lines);
                lock (Gate)
                {
                    _path = path;
                    _stamp = stamp;
                    _names = names;
                }

                return names;
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Reading flag names failed");
                return new Dictionary<int, string>();
            }
        }
    }

    /// <summary>
    /// Characters: search, detail and edits (B2). Reads and writes go through <see cref="WebAdminGame.RunOrInlineAsync{T}"/>: on the game
    /// thread while the world runs (using PlayerObject methods for online characters so the client updates), inline under AccountLock
    /// while it is stopped (then UserData.db is saved immediately).
    /// </summary>
    public sealed class CharacterRoutes : IWebAdminRoutes
    {
        private const int ListTimeoutMs = 10000;

        public void Register(WebAdminRouter router)
        {
            router.Get("/api/admin/characters", List);
            router.Get("/api/admin/characters/{index:int}", Detail);
            router.Post("/api/admin/characters/{index:int}/update", Update);
            router.Post("/api/admin/characters/{index:int}/delete", Delete);
            router.Post("/api/admin/characters/{index:int}/restore", Restore);
            router.Post("/api/admin/characters/{index:int}/flags", SetFlag);
            router.Post("/api/admin/characters/{index:int}/quests/reset", ResetQuests);
            router.Post("/api/admin/characters/{index:int}/quests/edit", EditQuest);
            router.Post("/api/admin/characters/{index:int}/magics/remove", RemoveMagic);
            router.Post("/api/admin/characters/{index:int}/magics/set", SetMagic);
        }

        // ------------------------------------------------------------------ list / detail

        private static async Task List(WebAdminRequest req)
        {
            var q = (req.Query["q"] ?? string.Empty).Trim();
            var exact = req.QueryBool("exact");
            var account = (req.Query["account"] ?? string.Empty).Trim();
            var item = (req.Query["item"] ?? string.Empty).Trim();
            var deleted = CharacterLogic.NormalizeDeletedFilter(req.Query["deleted"]);
            var onlineOnly = req.QueryBool("online");

            var rows = await WebAdminGame.RunOrInlineAsync(() =>
            {
                var envir = Envir.Main;
                var titles = CharacterProjection.MapTitles(envir);
                var guilds = CharacterProjection.GuildNames(envir);
                var list = new List<CharacterListRow>();
                var characters = envir.CharacterList;

                for (var i = 0; i < characters.Count; i++)
                {
                    var ci = characters[i];
                    if (ci == null) continue;
                    if (!CharacterLogic.PassesDeletedFilter(ci.Deleted, deleted)) continue;
                    if (onlineOnly && ci.Player == null) continue;
                    if (!CharacterLogic.NameMatches(ci.Name, q, exact)) continue;
                    if (account.Length > 0 && !CharacterLogic.AccountMatches(ci.AccountInfo?.Index ?? 0, ci.AccountInfo?.AccountID, account, exact)) continue;
                    if (item.Length > 0 && !CharacterLogic.CharacterHasItem(ci, item, exact)) continue;

                    list.Add(CharacterProjection.ListRow(ci, titles, guilds));
                }

                return list;
            }, ListTimeoutMs);

            var result = CharacterLogic.SortAndPage(rows, req.Query["sort"], req.Query["dir"], req.QueryInt("page", 1), req.QueryInt("pageSize", CharacterLogic.DefaultPageSize));
            result.World = WebAdminGame.WorldState;
            await req.Json(result);
        }

        private static async Task Detail(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var flagNames = CharacterFlagNames.Get(); // file I/O stays off the game thread

            var dto = await WebAdminGame.RunOrInlineAsync(() =>
            {
                var ci = CharacterRouteUtil.Find(index);
                return CharacterProjection.Detail(Envir.Main, ci, flagNames);
            }, ListTimeoutMs);

            await req.Json(dto);
        }

        // ------------------------------------------------------------------ update

        private static async Task Update(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = CharacterRouteUtil.RequireObject(await req.ReadJsonAsync());
            var reason = CharacterRouteUtil.Reason(body);

            var name = CharacterRouteUtil.OptionalString(body, "name");
            if (name != null)
            {
                name = name.Trim();
                var err = CharacterLogic.ValidateName(name);
                if (err != null) throw new WebAdminException(422, err);
            }

            var level = CharacterRouteUtil.OptionalLong(body, "level", 1, ushort.MaxValue);
            var pk = CharacterRouteUtil.OptionalLong(body, "pkPoints", int.MinValue, int.MaxValue);
            var gold = CharacterRouteUtil.OptionalLong(body, "gold", 0, uint.MaxValue);
            var credit = CharacterRouteUtil.OptionalLong(body, "credit", 0, uint.MaxValue);
            var hair = CharacterRouteUtil.OptionalLong(body, "hair", 0, byte.MaxValue);

            MirGender? gender = null;
            var genderText = CharacterRouteUtil.OptionalString(body, "gender");
            if (genderText != null)
            {
                if (!CharacterLogic.TryParseGender(genderText, out var g)) throw new WebAdminException(422, "gender must be Male or Female");
                gender = g;
            }

            if (name == null && level == null && pk == null && gold == null && credit == null && hair == null && gender == null)
                throw new WebAdminException(400, "Nothing to update");

            var result = await WebAdminGame.RunOrInlineAsync(() =>
            {
                var envir = Envir.Main;
                var ci = CharacterRouteUtil.Find(index);
                var player = ci.Player;
                var acc = ci.AccountInfo;
                var changes = new List<object>();
                var fields = new List<string>();
                var notes = new List<string>();

                // ---- checks first: nothing is applied when any of them fails
                var rename = name != null && !string.Equals(name, ci.Name, StringComparison.Ordinal);
                if (rename)
                {
                    if (player != null) throw new WebAdminException(409, ci.Name + " is online. Kick the player before renaming.");
                    if (!string.Equals(name, ci.Name, StringComparison.OrdinalIgnoreCase) && envir.CharacterExists(name))
                        throw new WebAdminException(409, "The name " + name + " is already taken.");
                    // Guild rows are only written by the running world's save (an offline save writes accounts only); a stopped-world rename would leave the
                    // old member name on disk, and at the next login GuildObject.FindRank(Info.Name) would fail and drop the membership.
                    if (!WebAdminGame.IsRunning && IsGuildMember(envir, ci))
                        throw new WebAdminException(409, ci.Name + " is a guild member. Start the world before renaming so the guild member list is saved too.");
                }

                var regender = gender.HasValue && gender.Value != ci.Gender;
                if (regender && player != null)
                    throw new WebAdminException(409, ci.Name + " is online. Kick the player before changing the gender.");

                if ((gold.HasValue || credit.HasValue) && acc == null)
                    throw new WebAdminException(409, "This character has no account; gold and credit cannot be changed.");

                // ---- apply
                if (rename)
                {
                    var old = ci.Name;
                    ci.Name = name;
                    RenameReferences(envir, ci.Index, name);
                    fields.Add("name");
                    changes.Add(new { field = "name", from = old, to = name });
                }

                if (regender)
                {
                    var old = ci.Gender;
                    ci.Gender = gender.Value;
                    fields.Add("gender");
                    changes.Add(new { field = "gender", from = old.ToString(), to = gender.Value.ToString() });
                }

                if (level.HasValue && (ushort)level.Value != ci.Level)
                {
                    var old = ci.Level;
                    if (player != null)
                    {
                        player.Level = (ushort)level.Value;
                        player.LevelUp();
                    }
                    else
                    {
                        ci.Level = (ushort)level.Value;
                    }

                    fields.Add("level");
                    changes.Add(new { field = "level", from = (int)old, to = (int)ci.Level });
                }

                if (pk.HasValue && (int)pk.Value != ci.PKPoints)
                {
                    var old = ci.PKPoints;
                    ci.PKPoints = (int)pk.Value;
                    player?.RefreshNameColour();
                    fields.Add("pkPoints");
                    changes.Add(new { field = "pkPoints", from = old, to = ci.PKPoints });
                }

                if (hair.HasValue && (byte)hair.Value != ci.Hair)
                {
                    var old = ci.Hair;
                    ci.Hair = (byte)hair.Value;
                    if (player != null) notes.Add("hair is visible after the player relogs");
                    fields.Add("hair");
                    changes.Add(new { field = "hair", from = (int)old, to = (int)ci.Hair });
                }

                var accountPlayer = acc != null ? CharacterRouteUtil.OnlinePlayerOf(acc) : null;

                if (gold.HasValue && (uint)gold.Value != acc.Gold)
                {
                    var old = acc.Gold;
                    var delta = CharacterLogic.CurrencyDelta(old, (uint)gold.Value);
                    acc.Gold = (uint)gold.Value;
                    if (accountPlayer != null && delta.Amount > 0)
                        accountPlayer.Enqueue(delta.Gain ? (Packet)new S.GainedGold { Gold = delta.Amount } : new S.LoseGold { Gold = delta.Amount });
                    fields.Add("gold");
                    changes.Add(new { field = "gold", from = old, to = acc.Gold, account = acc.AccountID });
                }

                if (credit.HasValue && (uint)credit.Value != acc.Credit)
                {
                    var old = acc.Credit;
                    var delta = CharacterLogic.CurrencyDelta(old, (uint)credit.Value);
                    acc.Credit = (uint)credit.Value;
                    if (accountPlayer != null && delta.Amount > 0)
                        accountPlayer.Enqueue(delta.Gain ? (Packet)new S.GainedCredit { Credit = delta.Amount } : new S.LoseCredit { Credit = delta.Amount });
                    fields.Add("credit");
                    changes.Add(new { field = "credit", from = old, to = acc.Credit, account = acc.AccountID });
                }

                if (changes.Count > 0) CharacterRouteUtil.PersistIfStopped();

                var message = changes.Count == 0
                    ? "No changes for " + ci.Name + "."
                    : "Updated " + ci.Name + " (" + string.Join(", ", fields) + ")" +
                      (notes.Count > 0 ? "; " + string.Join("; ", notes) : string.Empty) + ".";

                return new CharacterMutation
                {
                    Target = CharacterRouteUtil.Label(ci),
                    Message = message,
                    Details = new { changes, online = player != null },
                    Data = new { changed = changes.Count, fields },
                    Changed = changes.Count > 0,
                };
            });

            if (result.Changed)
                WebAdminAudit.Record(req, "character.update", result.Target, result.Details, reason);

            await req.Ok(result.Data, result.Message);
        }

        private static bool IsGuildMember(Envir envir, CharacterInfo ci)
        {
            if (ci.GuildIndex >= 0) return true;

            foreach (var guild in envir.GuildList)
            {
                if (guild?.Ranks == null) continue;
                foreach (var rank in guild.Ranks)
                {
                    if (rank?.Members == null) continue;
                    foreach (var member in rank.Members)
                        if (member != null && member.Id == ci.Index) return true;
                }
            }

            return false;
        }

        /// <summary>Keeps name copies in sync after a rename: guild member lists and the ranking tables.</summary>
        private static void RenameReferences(Envir envir, int characterIndex, string newName)
        {
            foreach (var guild in envir.GuildList)
            {
                if (guild?.Ranks == null) continue;

                var touched = false;
                foreach (var rank in guild.Ranks)
                {
                    if (rank?.Members == null) continue;
                    foreach (var member in rank.Members)
                    {
                        if (member == null || member.Id != characterIndex) continue;
                        member.Name = newName;
                        touched = true;
                    }
                }

                if (touched) guild.NeedSave = true;
            }

            RenameInRanking(envir.RankTop, characterIndex, newName);
            if (envir.RankClass != null)
                foreach (var list in envir.RankClass)
                    RenameInRanking(list, characterIndex, newName);
        }

        private static void RenameInRanking(List<RankCharacterInfo> list, int characterIndex, string newName)
        {
            if (list == null) return;
            foreach (var r in list)
                if (r != null && r.PlayerId == characterIndex) r.Name = newName;
        }

        // ------------------------------------------------------------------ delete / restore

        private static async Task Delete(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = await req.ReadJsonAsync();
            var reason = body.ValueKind == JsonValueKind.Object ? CharacterRouteUtil.Reason(body) : null;

            var result = await WebAdminGame.RunOrInlineAsync(() =>
            {
                var envir = Envir.Main;
                var ci = CharacterRouteUtil.Find(index);

                if (ci.Player != null) throw new WebAdminException(409, ci.Name + " is online. Kick the player before deleting the character.");
                if (ci.Deleted) throw new WebAdminException(409, ci.Name + " is already deleted.");

                ci.Deleted = true;
                ci.DeleteDate = envir.Now;
                envir.RemoveRank(ci);

                CharacterRouteUtil.PersistIfStopped();

                return new CharacterMutation
                {
                    Target = CharacterRouteUtil.Label(ci),
                    Message = ci.Name + " was deleted (it can be restored).",
                    Details = new { account = ci.AccountInfo?.AccountID, level = (int)ci.Level, className = ci.Class.ToString() },
                };
            });

            WebAdminAudit.Record(req, "character.delete", result.Target, result.Details, reason);
            await req.Ok(null, result.Message);
        }

        private static async Task Restore(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = await req.ReadJsonAsync();
            var reason = body.ValueKind == JsonValueKind.Object ? CharacterRouteUtil.Reason(body) : null;

            var result = await WebAdminGame.RunOrInlineAsync(() =>
            {
                var envir = Envir.Main;
                var ci = CharacterRouteUtil.Find(index);

                if (!ci.Deleted) throw new WebAdminException(409, ci.Name + " is not deleted.");

                var acc = ci.AccountInfo;
                if (acc != null)
                {
                    var active = acc.Characters.Count(c => c != null && !c.Deleted);
                    if (active >= Globals.MaxCharacterCount)
                        throw new WebAdminException(409, "Account " + acc.AccountID + " already has " + active + " characters (the maximum is " + Globals.MaxCharacterCount + ").");
                }

                var deletedOn = ci.DeleteDate;
                ci.Deleted = false;
                ci.DeleteDate = DateTime.MinValue;

                if (acc != null && !acc.AdminAccount && envir.RankClass != null && (int)ci.Class < envir.RankClass.Length && envir.RankClass[(int)ci.Class] != null)
                {
                    try
                    {
                        envir.CheckRankUpdate(ci);
                    }
                    catch (Exception ex)
                    {
                        WebAdminLog.Error(ex, "Rank update after restoring a character failed");
                    }
                }

                CharacterRouteUtil.PersistIfStopped();

                return new CharacterMutation
                {
                    Target = CharacterRouteUtil.Label(ci),
                    Message = ci.Name + " was restored.",
                    Details = new { account = acc?.AccountID, deletedOn = CharacterLogic.NullIfMin(deletedOn) },
                };
            });

            WebAdminAudit.Record(req, "character.restore", result.Target, result.Details, reason);
            await req.Ok(null, result.Message);
        }

        // ------------------------------------------------------------------ flags

        private static async Task SetFlag(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = CharacterRouteUtil.RequireObject(await req.ReadJsonAsync());
            var reason = CharacterRouteUtil.Reason(body);
            var flag = CharacterRouteUtil.OptionalLong(body, "flag", 0, int.MaxValue) ?? throw new WebAdminException(400, "flag is required");
            var value = CharacterRouteUtil.OptionalBool(body, "value") ?? throw new WebAdminException(400, "value (true/false) is required");

            var result = await WebAdminGame.RunOrInlineAsync(() =>
            {
                var ci = CharacterRouteUtil.Find(index);
                if (ci.Flags == null || flag >= ci.Flags.Length)
                    throw new WebAdminException(422, "flag must be between 0 and " + ((ci.Flags?.Length ?? 1) - 1));

                var i = (int)flag;
                var old = ci.Flags[i];
                ci.Flags[i] = value;

                var player = ci.Player;
                if (player != null && old != value && player.CurrentMap != null)
                {
                    // Same as @SETFLAG: NPCs in range re-check their visibility conditions.
                    var npcs = player.CurrentMap.NPCs;
                    for (var n = npcs.Count - 1; n >= 0; n--)
                    {
                        if (Functions.InRange(npcs[n].CurrentLocation, player.CurrentLocation, Globals.DataRange))
                            npcs[n].CheckVisible(player);
                    }
                }

                if (old != value) CharacterRouteUtil.PersistIfStopped();

                return new CharacterMutation
                {
                    Target = CharacterRouteUtil.Label(ci),
                    Message = old == value
                        ? "Flag " + i + " was already " + (value ? "set" : "clear") + " on " + ci.Name + "."
                        : "Flag " + i + " " + (value ? "set" : "cleared") + " on " + ci.Name + ".",
                    Details = new { flag = i, from = old, to = value, online = player != null },
                    Data = new { flag = i, value, changed = old != value },
                };
            });

            WebAdminAudit.Record(req, "character.flag", result.Target, result.Details, reason);
            await req.Ok(result.Data, result.Message);
        }

        // ------------------------------------------------------------------ quests

        private static async Task ResetQuests(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = CharacterRouteUtil.RequireObject(await req.ReadJsonAsync());
            var reason = CharacterRouteUtil.Reason(body);
            var all = CharacterRouteUtil.OptionalBool(body, "all") ?? false;
            var questIndex = CharacterRouteUtil.OptionalLong(body, "questIndex", 1, int.MaxValue);

            if (!all && questIndex == null) throw new WebAdminException(400, "questIndex or all:true is required");

            var result = await WebAdminGame.RunOrInlineAsync(() =>
            {
                var envir = Envir.Main;
                var ci = CharacterRouteUtil.Find(index);
                var player = ci.Player;
                Func<int, bool> match = q => all || q == (int)questIndex.Value;

                // Quest V2 records: runs (active / failed) and completion history.
                var current = new List<int>();
                for (var i = 0; i < ci.QuestRuns.Count; i++)
                    if (ci.QuestRuns[i] != null && match(ci.QuestRuns[i].Quest) && !current.Contains(ci.QuestRuns[i].Quest)) current.Add(ci.QuestRuns[i].Quest);
                var completed = new List<int>();
                for (var i = 0; i < ci.QuestHistory.Count; i++)
                    if (ci.QuestHistory[i] != null && match(ci.QuestHistory[i].Quest) && !completed.Contains(ci.QuestHistory[i].Quest)) completed.Add(ci.QuestHistory[i].Quest);

                if (current.Count == 0 && completed.Count == 0)
                    throw new WebAdminException(404, all ? ci.Name + " has no quest progress." : "Quest " + questIndex + " is neither active nor completed on " + ci.Name + ".");

                if (player != null)
                {
                    // Online: through PlayerQuests, so the index and the client stay in step (game thread).
                    if (all) player.Quests.AdminClearAll();
                    else player.Quests.AdminReset((int)questIndex.Value);
                }
                else
                {
                    Envir.MarkAccountDirty(ci.AccountInfo);
                    ci.QuestRuns.RemoveAll(q => q == null || match(q.Quest));
                    ci.QuestObjectives.RemoveAll(q => q == null || match(q.Quest));
                    ci.QuestVars.RemoveAll(q => q == null || match(q.Quest));
                    ci.QuestHistory.RemoveAll(q => q == null || match(q.Quest));
                    ci.QuestRewardsPaid.RemoveAll(q => q == null || match(q.Quest));
                }

                CharacterRouteUtil.PersistIfStopped();

                var names = current.Concat(completed).Distinct()
                    .Select(q => envir.GetQuestInfo(q)?.Name ?? ("#" + q)).ToList();
                return new CharacterMutation
                {
                    Target = CharacterRouteUtil.Label(ci),
                    Message = "Reset " + (current.Count + completed.Count) + " quest record(s) on " + ci.Name + ".",
                    Details = new
                    {
                        all,
                        questIndex,
                        active = current,
                        completed,
                        quests = names,
                        online = player != null,
                    },
                    Data = new { active = current.Count, completed = completed.Count },
                };
            });

            WebAdminAudit.Record(req, "character.quests-reset", result.Target, result.Details, reason);
            await req.Ok(result.Data, result.Message);
        }

        /// <summary>
        /// Quest V2 edit of one character (lane F): <c>action</c> = start | complete | step | objective, with <c>questIndex</c> and
        /// <c>step</c> / <c>objective</c> + <c>value</c>. Online: the same PlayerQuests methods as GM SETQUEST (game thread), so the
        /// index and the client stay in step. Offline or world stopped: validated against Envir.Edit's definition (copied under
        /// EditLock on this thread), the run first migrated with <c>PlayerQuests.MigrateRecords</c>, then the records edited.
        /// </summary>
        private static async Task EditQuest(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = CharacterRouteUtil.RequireObject(await req.ReadJsonAsync());
            var reason = CharacterRouteUtil.Reason(body);
            var questIndex = (int)(CharacterRouteUtil.OptionalLong(body, "questIndex", 1, int.MaxValue) ?? throw new WebAdminException(400, "questIndex is required"));
            var action = (CharacterRouteUtil.OptionalString(body, "action") ?? string.Empty).Trim().ToLowerInvariant();
            var step = (int)(CharacterRouteUtil.OptionalLong(body, "step", -1, int.MaxValue) ?? 0);
            var objective = (int)(CharacterRouteUtil.OptionalLong(body, "objective", 1, int.MaxValue) ?? 0);
            var value = (int)(CharacterRouteUtil.OptionalLong(body, "value", 0, int.MaxValue) ?? 0);
            if (action != "start" && action != "complete" && action != "step" && action != "objective")
                throw new WebAdminException(400, "action must be start, complete, step or objective");

            // Envir.Edit's definition, copied here (never on the game thread: EditLock).
            Server.MirEnvir.Quests.QuestDefinition def = null;
            lock (WebAdminGame.EditLock)
            {
                var edit = Envir.Edit;
                for (var i = 0; i < edit.QuestInfoList.Count; i++)
                    if (edit.QuestInfoList[i]?.Index == questIndex && edit.QuestInfoList[i].HasDefinition) { def = CopyDefinition(edit.QuestInfoList[i].Definition); break; }
            }

            var result = await WebAdminGame.RunOrInlineAsync(() =>
            {
                var ci = CharacterRouteUtil.Find(index);
                var player = ci.Player != null && ci.Player.Info == ci && WebAdminGame.IsRunning ? ci.Player : null;
                bool done;
                if (player != null)
                {
                    switch (action)
                    {
                        case "start": done = player.Quests.AdminStart(questIndex); break;
                        case "complete": done = player.Quests.AdminComplete(questIndex); break;
                        case "step": done = player.Quests.AdminSetStep(questIndex, step); break;
                        default: done = player.Quests.AdminSetObjective(questIndex, objective, value); break;
                    }
                }
                else
                {
                    if (def == null && action != "complete") throw new WebAdminException(404, "Quest " + questIndex + " has no V2 definition.");
                    Envir.MarkAccountDirty(ci.AccountInfo);
                    done = PlayerQuests.EditOfflineRecords(ci, def, questIndex, action, step, objective, value, Envir.Main.Now);
                }

                if (!done) throw new WebAdminException(409, "Quest " + questIndex + " was not changed on " + ci.Name + " (unknown run, step or objective, or nothing to change).");
                CharacterRouteUtil.PersistIfStopped();
                return new CharacterMutation
                {
                    Target = CharacterRouteUtil.Label(ci),
                    Message = "Quest " + questIndex + ": " + action + " on " + ci.Name + ".",
                    Details = new { questIndex, action, step, objective, value, online = player != null },
                    Data = new { questIndex, action },
                };
            });

            WebAdminAudit.Record(req, "character.quest-edit", result.Target, result.Details, reason);
            await req.Ok(result.Data, result.Message);
        }

        /// <summary>A copy of the steps / objectives / revision / limits of a definition (the parts an offline edit reads).</summary>
        private static Server.MirEnvir.Quests.QuestDefinition CopyDefinition(Server.MirEnvir.Quests.QuestDefinition d)
        {
            var copy = new Server.MirEnvir.Quests.QuestDefinition { Revision = d.Revision, TimeLimitSeconds = d.TimeLimitSeconds };
            foreach (var s in d.Steps)
                if (s != null) copy.Steps.Add(new Server.MirEnvir.Quests.QuestStepInfo { Index = s.Index, Order = s.Order, TimeLimitSeconds = s.TimeLimitSeconds, NextStep = s.NextStep });
            foreach (var o in d.Objectives)
                if (o != null) copy.Objectives.Add(new Server.MirEnvir.Quests.QuestObjectiveInfo { Index = o.Index, Step = o.Step, Type = o.Type, Count = o.Count });
            return copy;
        }

        // ------------------------------------------------------------------ magics

        private static Spell ReadSpell(JsonElement body)
        {
            var text = CharacterRouteUtil.OptionalString(body, "spell");
            if (text == null) throw new WebAdminException(400, "spell is required");
            if (!CharacterLogic.TryParseSpell(text, out var spell)) throw new WebAdminException(422, "Unknown spell: " + text);
            return spell;
        }

        private static async Task RemoveMagic(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = CharacterRouteUtil.RequireObject(await req.ReadJsonAsync());
            var reason = CharacterRouteUtil.Reason(body);
            var spell = ReadSpell(body);

            var result = await WebAdminGame.RunOrInlineAsync(() =>
            {
                var ci = CharacterRouteUtil.Find(index);
                var player = ci.Player;
                var removed = 0;
                byte oldLevel = 0;

                for (var i = ci.Magics.Count - 1; i >= 0; i--)
                {
                    if (ci.Magics[i] == null || ci.Magics[i].Spell != spell) continue;

                    oldLevel = ci.Magics[i].Level;
                    ci.Magics.RemoveAt(i);
                    player?.Enqueue(new S.RemoveMagic { PlaceId = i }); // same packet as @DELETESKILL
                    removed++;
                }

                if (removed == 0) throw new WebAdminException(404, ci.Name + " does not know " + spell + ".");

                player?.RefreshStats();
                CharacterRouteUtil.PersistIfStopped();

                return new CharacterMutation
                {
                    Target = CharacterRouteUtil.Label(ci),
                    Message = "Removed " + spell + " from " + ci.Name + ".",
                    Details = new { spell = spell.ToString(), level = (int)oldLevel, online = player != null },
                };
            });

            WebAdminAudit.Record(req, "character.magic-remove", result.Target, result.Details, reason);
            await req.Ok(null, result.Message);
        }

        private static async Task SetMagic(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = CharacterRouteUtil.RequireObject(await req.ReadJsonAsync());
            var reason = CharacterRouteUtil.Reason(body);
            var spell = ReadSpell(body);
            var level = (byte)(CharacterRouteUtil.OptionalLong(body, "level", 0, 3) ?? 0);

            var result = await WebAdminGame.RunOrInlineAsync(() =>
            {
                var envir = Envir.Main;
                var ci = CharacterRouteUtil.Find(index);
                var player = ci.Player;

                if (!envir.MagicInfoList.Any(m => m != null && m.Spell == spell))
                    throw new WebAdminException(409, "Spell " + spell + " has no MagicInfo in the running database.");

                var existing = ci.Magics.FirstOrDefault(m => m != null && m.Spell == spell);
                int? oldLevel = existing?.Level;

                if (existing != null)
                {
                    existing.Level = level;
                    // @GIVESKILL sends MagicLeveled; it is addressed to the target player here.
                    player?.Enqueue(new S.MagicLeveled { ObjectID = player.ObjectID, Spell = spell, Level = level, Experience = existing.Experience });
                }
                else
                {
                    var magic = new UserMagic(spell) { Level = level };
                    ci.Magics.Add(magic);
                    player?.SendMagicInfo(magic);
                }

                player?.RefreshStats();
                CharacterRouteUtil.PersistIfStopped();

                return new CharacterMutation
                {
                    Target = CharacterRouteUtil.Label(ci),
                    Message = existing != null
                        ? ci.Name + ": " + spell + " set to level " + level + "."
                        : ci.Name + " learned " + spell + " at level " + level + ".",
                    Details = new { spell = spell.ToString(), from = oldLevel, to = (int)level, added = existing == null, online = player != null },
                };
            });

            WebAdminAudit.Record(req, "character.magic-set", result.Target, result.Details, reason);
            await req.Ok(null, result.Message);
        }
    }
}

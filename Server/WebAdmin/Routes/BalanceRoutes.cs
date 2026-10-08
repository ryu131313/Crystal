using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Server.MirEnvir;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Shared plumbing for the Balance + Systems routes (B7): where in-memory Settings objects are read / changed, how INI files
    /// are written, and item / monster name lookups against the Edit database.
    /// </summary>
    internal static class BsGate
    {
        private static readonly SemaphoreSlim Mutations = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Runs <paramref name="fn"/> where Settings objects may be touched safely: on the game thread while the world runs (the game
        /// reads these lists every tick), otherwise inline when no game loop exists (stopped or crashed). Both paths hold
        /// <see cref="WebAdminOptions.SettingsLock"/>. While the world is starting or stopping (Envir.LoadDB / Map.CreateMine index
        /// these lists on the start thread) this throws <see cref="WorldNotRunningException"/> (503), like RunOrInlineAsync.
        /// The bool argument tells the delegate whether it runs on the game thread (so it may also refresh live objects).
        /// </summary>
        public static async Task<T> OnSettingsAsync<T>(Func<bool, T> fn)
        {
            var w = WebAdminGame.World;

            if (w.QueueOpen)
            {
                try
                {
                    return await WebAdminGame.RunAsync(() =>
                    {
                        lock (WebAdminOptions.SettingsLock)
                            return fn(true);
                    }).ConfigureAwait(false);
                }
                catch (WorldNotRunningException)
                {
                    // raced with a stop: fall through to the inline path if the loop is fully gone
                }
            }

            if (!WebAdminGame.IsFullyStopped(w))
                throw new WorldNotRunningException(WebAdminGame.WorldState);

            lock (WebAdminOptions.SettingsLock)
                return fn(false);
        }

        /// <summary>Serialises Balance / Systems mutations (read-modify-write + INI save) across concurrent admins.</summary>
        public static async Task<T> MutateAsync<T>(Func<Task<T>> body)
        {
            await Mutations.WaitAsync().ConfigureAwait(false);
            try
            {
                return await body().ConfigureAwait(false);
            }
            finally
            {
                Mutations.Release();
            }
        }

        /// <summary>
        /// Writes an INI file through a Settings.SaveX method under the settings lock. IO failures are logged and returned as a 500
        /// (not thrown) because the in-memory change is already live: callers record the audit entry first and then throw it via
        /// <see cref="ThrowIfFailed"/>.
        /// </summary>
        public static WebAdminException TrySaveIni(string file, Action save)
        {
            try
            {
                lock (WebAdminOptions.SettingsLock)
                    save();
                return null;
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Failed to write Configs\\" + file);
                return new WebAdminException(500, "The change is live but writing Configs\\" + file + " failed (see the server log). Save again to retry.");
            }
        }

        public static void ThrowIfFailed(WebAdminException failure)
        {
            if (failure != null) throw failure;
        }
    }

    /// <summary>Reflection access to Settings static fields / save methods named by the catalogue.</summary>
    internal static class BsSettings
    {
        public static FieldInfo Field(string name)
        {
            return typeof(Settings).GetField(name, BindingFlags.Public | BindingFlags.Static);
        }

        public static object Get(BsFieldDef def)
        {
            var fi = Field(def.Field);
            var value = fi?.GetValue(null);

            if (def.ArrayIndex >= 0)
            {
                var array = value as bool[];
                return array != null && def.ArrayIndex < array.Length && array[def.ArrayIndex];
            }

            return value;
        }

        public static void Set(BsFieldDef def, object value)
        {
            var fi = Field(def.Field);
            if (fi == null) throw new InvalidOperationException("Unknown Settings field " + def.Field);

            if (def.ArrayIndex >= 0)
            {
                var array = fi.GetValue(null) as bool[] ?? Array.Empty<bool>();
                if (array.Length <= def.ArrayIndex)
                {
                    Array.Resize(ref array, Math.Max(def.ArrayIndex + 1, Enum.GetNames(typeof(MirClass)).Length));
                    fi.SetValue(null, array);
                }

                array[def.ArrayIndex] = (bool)value;
                return;
            }

            fi.SetValue(null, value);
        }

        public static Dictionary<string, object> Values(BsGroupDef group)
        {
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var def in group.Fields)
                values[def.Key] = Get(def);
            return values;
        }

        public static void InvokeSave(string method)
        {
            var mi = typeof(Settings).GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (mi == null) throw new InvalidOperationException("Unknown Settings method " + method);

            try
            {
                mi.Invoke(null, null);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }
    }

    /// <summary>Name indexes over the Edit database (the same lists the WinForms editors validate against).</summary>
    internal static class BsLookups
    {
        public static BsNameIndex Items()
        {
            List<string> names;
            lock (WebAdminGame.EditLock)
                names = Envir.Edit.ItemInfoList.Where(i => i != null).Select(i => i.Name).ToList();
            return new BsNameIndex(names, BsNameMode.ItemStripped);
        }

        public static BsNameIndex Monsters()
        {
            List<string> names;
            lock (WebAdminGame.EditLock)
                names = Envir.Edit.MonsterInfoList.Where(m => m != null).Select(m => m.Name).ToList();
            return new BsNameIndex(names, BsNameMode.Monster);
        }

        public static BsNameIndex Ores()
        {
            List<(string Name, string FriendlyName)> items;
            lock (WebAdminGame.EditLock)
                items = Envir.Edit.ItemInfoList.Where(i => i != null && i.Name != null).Select(i => (i.Name, i.FriendlyName)).ToList();
            return new BsNameIndex(BsNameIndex.RefineOreCandidates(items), BsNameMode.Exact);
        }

        public static BsNameIndex For(string lookup)
        {
            switch ((lookup ?? string.Empty).ToLowerInvariant())
            {
                case "items":
                    return Items();
                case "monsters":
                    return Monsters();
                case "ores":
                    return Ores();
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Balance (mirror of BalanceConfigForm): class / hero base stats + caps, item stat weights, random item stat sets.
    /// Settings objects are changed on the game thread while the world runs and persisted with the matching Settings.SaveX().
    /// </summary>
    public sealed class BalanceRoutes : IWebAdminRoutes
    {
        public void Register(WebAdminRouter router)
        {
            router.Get("/api/admin/balance/meta", GetMeta);
            router.Get("/api/admin/balance/preview", GetPreview);

            router.Get("/api/admin/balance/basestats/{class}", req => GetBaseStats(req, false));
            router.Put("/api/admin/balance/basestats/{class}", req => PutBaseStats(req, false));
            router.Get("/api/admin/balance/herobasestats/{class}", req => GetBaseStats(req, true));
            router.Put("/api/admin/balance/herobasestats/{class}", req => PutBaseStats(req, true));

            router.Get("/api/admin/balance/itemweights", GetItemWeights);
            router.Put("/api/admin/balance/itemweights", PutItemWeights);

            router.Get("/api/admin/balance/randomitemstats", GetRandomItemStats);
            router.Post("/api/admin/balance/randomitemstats", AddRandomItemStat);
            router.Get("/api/admin/balance/randomitemstats/{id:int}", GetRandomItemStat);
            router.Put("/api/admin/balance/randomitemstats/{id:int}", PutRandomItemStat);
            router.Delete("/api/admin/balance/randomitemstats/{id:int}", DeleteRandomItemStat);
        }

        // ------------------------------------------------------------------------------------------------ meta / preview

        private static Task GetMeta(WebAdminRequest req)
        {
            return req.Json(new
            {
                classes = Enum.GetValues(typeof(MirClass)).Cast<MirClass>().Select(c => new { value = (int)c, name = c.ToString() }).ToList(),
                stats = BalanceModel.EditableStats.Select(s => new { value = (int)s, name = s.ToString() }).ToList(),
                formulas = Enum.GetValues(typeof(StatFormula)).Cast<StatFormula>().Select(f => new { value = (int)f, name = f.ToString() }).ToList(),
                randomItemStats = new
                {
                    groups = RandomItemStatCatalog.Groups.Select(g => new { label = g.Label, chance = g.ChanceField, statChance = g.StatChanceField, maxStat = g.MaxStatField }).ToList(),
                    curse = RandomItemStatCatalog.CurseField,
                    templates = RandomItemStatCatalog.Templates.Select(t => t.ToString()).ToList(),
                    maxSets = RandomItemStatCatalog.MaxSets,
                },
                world = WebAdminGame.WorldState,
            });
        }

        /// <summary>GET /api/admin/balance/preview?job=&amp;formula=&amp;base=&amp;gain=&amp;gainRate=&amp;max=&amp;cap=&amp;from=1&amp;to=50 — BaseStat.Calculate per level.</summary>
        private static Task GetPreview(WebAdminRequest req)
        {
            if (!BalanceModel.TryParseClass(req.Query["job"], out var job))
                throw new WebAdminException(400, "Unknown class");
            if (!BsJson.TryEnumText(req.Query["formula"], out StatFormula formula))
                throw new WebAdminException(400, "Unknown formula");

            var @base = ParseInt(req.Query["base"], "base");
            var gain = ParseFloat(req.Query["gain"], "gain");
            var gainRate = string.IsNullOrWhiteSpace(req.Query["gainRate"]) ? 0f : ParseFloat(req.Query["gainRate"], "gainRate");
            var max = string.IsNullOrWhiteSpace(req.Query["max"]) ? 0 : ParseInt(req.Query["max"], "max");
            var cap = string.IsNullOrWhiteSpace(req.Query["cap"]) ? 0 : ParseInt(req.Query["cap"], "cap");
            var from = req.QueryInt("from", 1);
            var to = req.QueryInt("to", 50);

            var values = BalanceModel.Preview(job, formula, @base, gain, gainRate, max, cap, from, to);
            var start = Math.Clamp(from, 1, 1000);

            return req.Json(new
            {
                job = job.ToString(),
                formula = formula.ToString(),
                levels = values.Select((value, i) => new { level = start + i, value }).ToList(),
            });
        }

        private static int ParseInt(string s, string name)
        {
            if (!int.TryParse((s ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                throw new WebAdminException(400, name + " must be a whole number");
            return v;
        }

        private static float ParseFloat(string s, string name)
        {
            if (!float.TryParse((s ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || float.IsNaN(v) || float.IsInfinity(v))
                throw new WebAdminException(400, name + " must be a number");
            return v;
        }

        // ------------------------------------------------------------------------------------------------ base stats

        private static MirClass RouteClass(WebAdminRequest req)
        {
            if (!BalanceModel.TryParseClass(req.Route["class"], out var job))
                throw new WebAdminException(404, "Unknown class '" + req.Route["class"] + "'");
            return job;
        }

        private static BaseStats Find(bool hero, MirClass job)
        {
            var table = hero ? Settings.HeroBaseStats : Settings.ClassBaseStats;
            var stats = table?.FirstOrDefault(b => b != null && b.Job == job);
            if (stats == null) throw new WebAdminException(404, "No base stats are loaded for " + job);
            return stats;
        }

        private static async Task GetBaseStats(WebAdminRequest req, bool hero)
        {
            var job = RouteClass(req);
            var dto = await BsGate.OnSettingsAsync(_ => BalanceModel.ToDto(Find(hero, job), hero)).ConfigureAwait(false);
            await req.Json(dto).ConfigureAwait(false);
        }

        private static async Task PutBaseStats(WebAdminRequest req, bool hero)
        {
            var job = RouteClass(req);
            var body = await req.ReadJsonAsync().ConfigureAwait(false);

            var v = new BsValidation();
            var parsed = BalanceModel.Parse(job, body, v);
            v.ThrowIfInvalid();

            var reason = BsJson.Reason(body);
            var file = (hero ? "HeroBaseStats" : "BaseStats") + job + ".ini";

            WebAdminException iniFailure = null;
            var outcome = await BsGate.MutateAsync(async () =>
            {
                var result = await BsGate.OnSettingsAsync(onGameThread =>
                {
                    var target = Find(hero, job);
                    var before = BalanceModel.ToDto(target, hero);
                    BsRev.Check(body, before.Rev);

                    target.Stats = parsed.Stats;
                    target.Caps = parsed.Caps;

                    // BalanceConfigForm_FormClosed: RequiresBaseStatUpdate / RequiresHeroBaseStatUpdate. The hero variant also calls
                    // RefreshStats; the player variant only flags HasUpdatedBaseStats, so players are refreshed here as well (the
                    // flag makes RefreshStats send the new BaseStatsInfo first) — otherwise they keep old stats until a level-up etc.
                    var refreshed = 0;
                    if (onGameThread)
                    {
                        if (hero)
                        {
                            refreshed = Envir.Main.Heroes.Count;
                            Envir.Main.RequiresHeroBaseStatUpdate();
                        }
                        else
                        {
                            Envir.Main.RequiresBaseStatUpdate();
                            foreach (var player in Envir.Main.Players.ToList())
                            {
                                if (player == null || player.Class != job) continue;
                                player.RefreshStats();
                                refreshed++;
                            }
                        }
                    }

                    return (Before: before, After: BalanceModel.ToDto(target, hero), Refreshed: refreshed, Live: onGameThread);
                }).ConfigureAwait(false);

                iniFailure = BsGate.TrySaveIni(file, () =>
                {
                    if (hero) Settings.SaveHeroBaseStats();
                    else Settings.SaveBaseStats();
                });

                return result;
            }).ConfigureAwait(false);

            WebAdminAudit.Record(req, hero ? "balance.herobasestats" : "balance.basestats", job.ToString(), new
            {
                before = new { outcome.Before.Stats, outcome.Before.Caps },
                after = new { outcome.After.Stats, outcome.After.Caps },
                file,
                refreshed = outcome.Refreshed,
            }, reason);

            var who = hero ? "heroes" : "online " + job + " players";
            var message = "Saved " + job + (hero ? " hero" : string.Empty) + " base stats to Configs\\" + file + "."
                + (outcome.Live ? " " + outcome.Refreshed + " " + who + " had their stats recalculated." : " They apply when the world starts.");

            BsGate.ThrowIfFailed(iniFailure);

            await req.Ok(new { stats = outcome.After, warnings = v.Warnings, refreshed = outcome.Refreshed }, message).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------------------------------------ item weights

        private static object WeightsDto(Dictionary<string, object> values)
        {
            var group = BsCatalog.ItemWeights;
            return new
            {
                key = group.Key,
                label = group.Label,
                file = group.File,
                note = group.Note,
                fields = group.Fields.Select(f => f.Describe(values[f.Key])).ToList(),
                rev = BsRev.Of(values),
            };
        }

        private static async Task GetItemWeights(WebAdminRequest req)
        {
            var values = await BsGate.OnSettingsAsync(_ => BsSettings.Values(BsCatalog.ItemWeights)).ConfigureAwait(false);
            await req.Json(WeightsDto(values)).ConfigureAwait(false);
        }

        private static async Task PutItemWeights(WebAdminRequest req)
        {
            var group = BsCatalog.ItemWeights;
            var body = await req.ReadJsonAsync().ConfigureAwait(false);

            var v = new BsValidation();
            var changes = BsCatalog.ParseValues(group, body, v);
            v.ThrowIfInvalid();

            var reason = BsJson.Reason(body);

            WebAdminException iniFailure = null;
            var outcome = await BsGate.MutateAsync(async () =>
            {
                var result = await BsGate.OnSettingsAsync(_ =>
                {
                    var current = BsSettings.Values(group);
                    BsRev.Check(body, BsRev.Of(current));

                    var diffs = new List<object>();
                    foreach (var pair in changes)
                    {
                        var def = group.Find(pair.Key);
                        var old = current[def.Key];
                        if (Equals(old, pair.Value)) continue;

                        BsSettings.Set(def, pair.Value);
                        diffs.Add(new { field = def.Key, from = old, to = pair.Value });
                    }

                    return (Diffs: diffs, After: BsSettings.Values(group));
                }).ConfigureAwait(false);

                // Fix for quirk 8: the WinForms tab only changed memory; persist [Items] now.
                if (result.Diffs.Count > 0) iniFailure = BsGate.TrySaveIni(group.File, Settings.Save);
                return result;
            }).ConfigureAwait(false);

            if (outcome.Diffs.Count > 0)
                WebAdminAudit.Record(req, "balance.itemweights", "Setup.ini [Items]", new { changes = outcome.Diffs }, reason);

            BsGate.ThrowIfFailed(iniFailure);

            await req.Ok(new { weights = WeightsDto(outcome.After), changed = outcome.Diffs.Count },
                outcome.Diffs.Count == 0 ? "No changes." : "Saved " + outcome.Diffs.Count + " item stat weight(s); they apply immediately.").ConfigureAwait(false);
        }

        // ------------------------------------------------------------------------------------------------ random item stats

        /// <summary>id → (count, sample item names) of Edit items using each random stat set.</summary>
        private static Dictionary<int, (int Count, List<string> Names)> RandomStatUsage()
        {
            var usage = new Dictionary<int, (int Count, List<string> Names)>();

            lock (WebAdminGame.EditLock)
            {
                foreach (var item in Envir.Edit.ItemInfoList)
                {
                    if (item == null || item.RandomStatsId == 255) continue;

                    if (!usage.TryGetValue(item.RandomStatsId, out var entry)) entry = (0, new List<string>());
                    if (entry.Names.Count < 12) entry.Names.Add(item.Name);
                    usage[item.RandomStatsId] = (entry.Count + 1, entry.Names);
                }
            }

            return usage;
        }

        private static object RandomSetDto(int id, Dictionary<string, int> values, Dictionary<int, (int Count, List<string> Names)> usage)
        {
            usage.TryGetValue(id, out var used);
            return new
            {
                id,
                values,
                usedBy = used.Count,
                usedByItems = used.Names ?? new List<string>(),
                rev = BsRev.Of(values),
            };
        }

        private static async Task GetRandomItemStats(WebAdminRequest req)
        {
            var sets = await BsGate.OnSettingsAsync(_ => Settings.RandomItemStatsList.Select(RandomItemStatCatalog.ToValues).ToList()).ConfigureAwait(false);
            var usage = RandomStatUsage();

            await req.Json(new
            {
                sets = sets.Select((values, id) => RandomSetDto(id, values, usage)).ToList(),
                maxSets = RandomItemStatCatalog.MaxSets,
                file = "RandomItemStats.ini",
            }).ConfigureAwait(false);
        }

        private static async Task GetRandomItemStat(WebAdminRequest req)
        {
            var id = req.RouteInt("id");
            var values = await BsGate.OnSettingsAsync(_ => id >= 0 && id < Settings.RandomItemStatsList.Count
                ? RandomItemStatCatalog.ToValues(Settings.RandomItemStatsList[id])
                : null).ConfigureAwait(false);

            if (values == null) throw new WebAdminException(404, "Random item stat set " + id + " does not exist");
            await req.Json(RandomSetDto(id, values, RandomStatUsage())).ConfigureAwait(false);
        }

        private static async Task PutRandomItemStat(WebAdminRequest req)
        {
            var id = req.RouteInt("id");
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var reason = BsJson.Reason(body);

            WebAdminException iniFailure = null;
            var outcome = await BsGate.MutateAsync(async () =>
            {
                var result = await BsGate.OnSettingsAsync(_ =>
                {
                    if (id < 0 || id >= Settings.RandomItemStatsList.Count)
                        throw new WebAdminException(404, "Random item stat set " + id + " does not exist");

                    // Edited in place: live ItemInfo.RandomStats reference these objects, so new drops use the values at once.
                    var target = Settings.RandomItemStatsList[id];
                    var before = RandomItemStatCatalog.ToValues(target);
                    BsRev.Check(body, BsRev.Of(before));

                    var v = new BsValidation();
                    var merged = RandomItemStatCatalog.Parse(body, before, v);
                    v.ThrowIfInvalid();

                    var diffs = merged.Where(p => before[p.Key] != p.Value).Select(p => new { field = p.Key, from = before[p.Key], to = p.Value }).ToList();
                    RandomItemStatCatalog.Apply(target, merged);

                    return (Diffs: diffs, After: RandomItemStatCatalog.ToValues(target));
                }).ConfigureAwait(false);

                if (result.Diffs.Count > 0) iniFailure = BsGate.TrySaveIni("RandomItemStats.ini", Settings.SaveRandomItemStats);
                return result;
            }).ConfigureAwait(false);

            if (outcome.Diffs.Count > 0)
                WebAdminAudit.Record(req, "balance.randomitemstats.update", "set " + id, new { changes = outcome.Diffs }, reason);

            BsGate.ThrowIfFailed(iniFailure);

            await req.Ok(new { set = RandomSetDto(id, outcome.After, RandomStatUsage()), changed = outcome.Diffs.Count },
                outcome.Diffs.Count == 0 ? "No changes." : "Saved random item stat set " + id + "; new item drops use it immediately.").ConfigureAwait(false);
        }

        private static async Task AddRandomItemStat(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var reason = BsJson.Reason(body);

            var template = ItemType.Nothing;
            if (body.ValueKind == JsonValueKind.Object && BsJson.TryGet(body, "template", out var t))
            {
                if (!BsJson.TryEnum(t, out template) || !RandomItemStatCatalog.Templates.Contains(template))
                    throw new WebAdminException(400, "Unknown template; use one of " + string.Join(", ", RandomItemStatCatalog.Templates));
            }

            WebAdminException iniFailure = null;
            var outcome = await BsGate.MutateAsync(async () =>
            {
                var result = await BsGate.OnSettingsAsync(_ =>
                {
                    if (Settings.RandomItemStatsList.Count >= RandomItemStatCatalog.MaxSets)
                        throw new WebAdminException(409, "There are already " + RandomItemStatCatalog.MaxSets + " sets (item RandomStatsId is a byte; 255 means none)");

                    var stat = new RandomItemStat(template);
                    Settings.RandomItemStatsList.Add(stat);
                    return (Id: Settings.RandomItemStatsList.Count - 1, Values: RandomItemStatCatalog.ToValues(stat));
                }).ConfigureAwait(false);

                iniFailure = BsGate.TrySaveIni("RandomItemStats.ini", Settings.SaveRandomItemStats);
                return result;
            }).ConfigureAwait(false);

            WebAdminAudit.Record(req, "balance.randomitemstats.add", "set " + outcome.Id, new { template = template.ToString(), values = outcome.Values }, reason);

            BsGate.ThrowIfFailed(iniFailure);

            await req.Ok(new { set = RandomSetDto(outcome.Id, outcome.Values, RandomStatUsage()) },
                "Added random item stat set " + outcome.Id + ". Items pick it up (RandomStatsId " + outcome.Id + ") after the next world start.").ConfigureAwait(false);
        }

        private static async Task DeleteRandomItemStat(WebAdminRequest req)
        {
            var id = req.RouteInt("id");
            var force = req.QueryBool("force");
            var reason = req.Query["reason"];
            var usage = RandomStatUsage();

            usage.TryGetValue(id, out var used);
            if (used.Count > 0 && !force)
                throw new WebAdminException(409, "Set " + id + " is used by " + used.Count + " item(s). Confirm to remove it anyway (those items lose their random stats after the next world start).",
                    new { usedBy = used.Count, usedByItems = used.Names });

            WebAdminException iniFailure = null;
            var outcome = await BsGate.MutateAsync(async () =>
            {
                var removed = await BsGate.OnSettingsAsync(_ =>
                {
                    var count = Settings.RandomItemStatsList.Count;
                    if (id < 0 || id >= count)
                        throw new WebAdminException(404, "Random item stat set " + id + " does not exist");

                    // Like BalanceConfigForm: only the last set can go (items reference sets by position).
                    if (id != count - 1)
                        throw new WebAdminException(409, "Only the last set (" + (count - 1) + ") can be removed; items reference sets by position");

                    var values = RandomItemStatCatalog.ToValues(Settings.RandomItemStatsList[id]);
                    Settings.RandomItemStatsList.RemoveAt(id);
                    return values;
                }).ConfigureAwait(false);

                iniFailure = BsGate.TrySaveIni("RandomItemStats.ini", Settings.SaveRandomItemStats);
                return removed;
            }).ConfigureAwait(false);

            WebAdminAudit.Record(req, "balance.randomitemstats.remove", "set " + id, new { values = outcome, usedBy = used.Count }, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());

            BsGate.ThrowIfFailed(iniFailure);

            await req.Ok(new { removed = id }, "Removed random item stat set " + id + ".").ConfigureAwait(false);
        }
    }
}

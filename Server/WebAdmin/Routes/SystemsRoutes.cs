using System.Text.Json;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirObjects;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Systems (mirror of SystemInfoForm, GuildInfoForm, MiningInfoForm): scalar system groups, guild settings, mine sets and the
    /// respawn tick. Settings groups are live and persisted to their INI files; the respawn tick lives in the Edit database
    /// (Server.MirDB) and needs a world restart (or the optional live apply).
    /// </summary>
    public sealed class SystemsRoutes : IWebAdminRoutes
    {
        public void Register(WebAdminRouter router)
        {
            router.Get("/api/admin/systems", GetIndex);
            router.Get("/api/admin/systems/names", GetNames);

            router.Get("/api/admin/systems/guilds", GetGuilds);
            router.Put("/api/admin/systems/guilds", PutGuilds);

            router.Get("/api/admin/systems/mining", GetMining);
            router.Put("/api/admin/systems/mining", PutMining);

            router.Get("/api/admin/systems/spawntick", GetSpawnTick);
            router.Put("/api/admin/systems/spawntick", PutSpawnTick);

            router.Get("/api/admin/systems/{group}", GetGroup);
            router.Put("/api/admin/systems/{group}", PutGroup);
        }

        // ------------------------------------------------------------------------------------------------ index / names

        private static Task GetIndex(WebAdminRequest req)
        {
            var groups = BsCatalog.Groups.Select(g => new { key = g.Key, label = g.Label, file = g.File, kind = "settings" }).ToList<object>();
            groups.Add(new { key = "guilds", label = "Guilds", file = "GuildSettings.ini", kind = "guilds" });
            groups.Add(new { key = "mining", label = "Mining", file = "Mines.ini", kind = "mining" });
            groups.Add(new { key = "spawntick", label = "Spawn tick", file = "Server.MirDB", kind = "spawntick" });

            return req.Json(new { groups, world = WebAdminGame.WorldState });
        }

        /// <summary>GET /api/admin/systems/names?kind=items|monsters|ores → names from the Edit database for pickers.</summary>
        private static Task GetNames(WebAdminRequest req)
        {
            var kind = (req.Query["kind"] ?? string.Empty).Trim().ToLowerInvariant();
            var index = BsLookups.For(kind);
            if (index == null) throw new WebAdminException(400, "kind must be items, monsters or ores");

            return req.Json(new { kind, loaded = index.Count > 0, names = index.Names });
        }

        // ------------------------------------------------------------------------------------------------ scalar groups

        private static BsGroupDef RouteGroup(WebAdminRequest req)
        {
            var group = BsCatalog.FindGroup(req.Route["group"]);
            if (group == null) throw new WebAdminException(404, "Unknown system group '" + req.Route["group"] + "'");
            return group;
        }

        private static object GroupDto(BsGroupDef group, Dictionary<string, object> values)
        {
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

        private static async Task GetGroup(WebAdminRequest req)
        {
            var group = RouteGroup(req);
            var values = await BsGate.OnSettingsAsync(_ => BsSettings.Values(group)).ConfigureAwait(false);
            await req.Json(GroupDto(group, values)).ConfigureAwait(false);
        }

        private static async Task PutGroup(WebAdminRequest req)
        {
            var group = RouteGroup(req);
            var body = await req.ReadJsonAsync().ConfigureAwait(false);

            var v = new BsValidation();
            var changes = BsCatalog.ParseValues(group, body, v);

            // Name fields must resolve against the Edit database (SystemInfoForm offered combo boxes of Edit items / monsters).
            foreach (var def in group.Fields.Where(f => f.Lookup != null))
            {
                if (!changes.TryGetValue(def.Key, out var raw) || !(raw is string name)) continue;

                var index = BsLookups.For(def.Lookup);
                if (index == null || index.Count == 0)
                {
                    v.Warn("The " + def.Lookup + " database is not loaded: " + def.Label + " was not checked.");
                    continue;
                }

                var resolved = index.Resolve(name);
                if (resolved == null) v.Error(def.Key, "\"" + name + "\" was not found in the " + def.Lookup + " database");
                else changes[def.Key] = resolved;
            }

            v.ThrowIfInvalid();
            var reason = BsJson.Reason(body);

            WebAdminException iniFailure = null;
            var outcome = await BsGate.MutateAsync(async () =>
            {
                var result = await BsGate.OnSettingsAsync(_ =>
                {
                    var current = BsSettings.Values(group);
                    BsRev.Check(body, BsRev.Of(current));

                    var merged = new Dictionary<string, object>(current, StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in changes) merged[pair.Key] = pair.Value;

                    var cross = new BsValidation();
                    BsCatalog.CrossValidate(group, merged, cross);
                    cross.ThrowIfInvalid();

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

                if (result.Diffs.Count > 0) iniFailure = BsGate.TrySaveIni(group.File, () => BsSettings.InvokeSave(group.SaveMethod));
                return result;
            }).ConfigureAwait(false);

            if (outcome.Diffs.Count > 0)
                WebAdminAudit.Record(req, "systems." + group.Key, group.File, new { changes = outcome.Diffs }, reason);

            BsGate.ThrowIfFailed(iniFailure);

            await req.Ok(new { group = GroupDto(group, outcome.After), changed = outcome.Diffs.Count, warnings = v.Warnings },
                outcome.Diffs.Count == 0 ? "No changes." : "Saved " + outcome.Diffs.Count + " " + group.Label.ToLowerInvariant() + " setting(s) to Configs\\" + group.File + "; they apply immediately.").ConfigureAwait(false);
        }

        // ------------------------------------------------------------------------------------------------ guilds

        private static GuildSettingsDto GuildSnapshot()
        {
            var levels = new List<GuildLevelDto>();
            var count = Math.Max(Settings.Guild_ExperienceList.Count, Settings.Guild_MembercapList.Count);
            for (var i = 0; i < count; i++)
            {
                levels.Add(new GuildLevelDto
                {
                    Level = i,
                    Experience = i < Settings.Guild_ExperienceList.Count ? Settings.Guild_ExperienceList[i] : 0,
                    MemberCap = i < Settings.Guild_MembercapList.Count ? Settings.Guild_MembercapList[i] : 0,
                });
            }

            var dto = new GuildSettingsDto
            {
                RequiredLevel = Settings.Guild_RequiredLevel,
                PointPerLevel = Settings.Guild_PointPerLevel,
                ExpRate = Settings.Guild_ExpRate,
                NewbieGuildBuffEnabled = Settings.NewbieGuildBuffEnabled,
                NewbieGuildExpBuff = Settings.NewbieGuildExpBuff,
                WarTime = Settings.Guild_WarTime,
                WarCost = Settings.Guild_WarCost,
                BuyGTGold = Settings.BuyGTGold,
                ExtendGT = Settings.ExtendGT,
                GTDays = Settings.GTDays,
                CreationCosts = Settings.Guild_CreationCostList.Select(c => new GuildCostDto { ItemName = c.ItemName ?? string.Empty, Amount = c.Amount }).ToList(),
                Levels = levels,
                Buffs = Settings.Guild_BuffList.Select(GuildSettingsModel.ToDto).ToList(),
            };

            dto.Rev = BsRev.Of(dto);
            return dto;
        }

        private static object GuildResponse(GuildSettingsDto dto)
        {
            return new
            {
                settings = dto,
                buffStats = GuildSettingsModel.BuffStats.Select(s => new { stat = s.Stat.ToString(), label = s.Label, max = s.Max }).ToList(),
                file = "GuildSettings.ini",
                notes = new[]
                {
                    "Guild territory (GT) prices and duration are live only: Crystal does not save them to any INI file, so they reset to the defaults when the server process restarts.",
                    "Online players receive buff catalogue changes the next time they log in.",
                },
            };
        }

        private static async Task GetGuilds(WebAdminRequest req)
        {
            var dto = await BsGate.OnSettingsAsync(_ => GuildSnapshot()).ConfigureAwait(false);
            await req.Json(GuildResponse(dto)).ConfigureAwait(false);
        }

        private static async Task PutGuilds(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var items = BsLookups.Items();

            var v = new BsValidation();
            var parsed = GuildSettingsModel.Parse(body, items.Resolve, items.Count > 0, v);
            v.ThrowIfInvalid();

            var reason = BsJson.Reason(body);

            WebAdminException iniFailure = null;
            var outcome = await BsGate.MutateAsync(async () =>
            {
                var result = await BsGate.OnSettingsAsync(onGameThread =>
                {
                    var before = GuildSnapshot();
                    BsRev.Check(body, before.Rev);

                    Settings.Guild_RequiredLevel = (byte)parsed.RequiredLevel;
                    Settings.Guild_PointPerLevel = (byte)parsed.PointPerLevel;
                    Settings.Guild_ExpRate = parsed.ExpRate;
                    Settings.NewbieGuildBuffEnabled = parsed.NewbieGuildBuffEnabled;
                    Settings.NewbieGuildExpBuff = parsed.NewbieGuildExpBuff;
                    Settings.Guild_WarTime = parsed.WarTime;
                    Settings.Guild_WarCost = parsed.WarCost;
                    Settings.BuyGTGold = parsed.BuyGTGold;
                    Settings.ExtendGT = parsed.ExtendGT;
                    Settings.GTDays = parsed.GTDays;

                    // Creation costs: linked to live ItemInfos now (LinkGuildCreationItems runs again on the next world start).
                    var costs = parsed.CreationCosts.Select(c => new GuildItemVolume { ItemName = c.ItemName, Amount = c.Amount }).ToList();
                    if (onGameThread)
                    {
                        foreach (var cost in costs)
                            if (!string.IsNullOrEmpty(cost.ItemName)) cost.Item = Envir.Main.GetItemInfo(cost.ItemName);
                    }

                    Settings.Guild_CreationCostList.Clear();
                    Settings.Guild_CreationCostList.AddRange(costs);

                    Settings.Guild_ExperienceList.Clear();
                    Settings.Guild_ExperienceList.AddRange(parsed.Levels.Select(l => l.Experience));
                    Settings.Guild_MembercapList.Clear();
                    Settings.Guild_MembercapList.AddRange(parsed.Levels.Select(l => l.MemberCap));

                    // Buffs are updated in place by id: active guild buffs keep pointing at the same GuildBuffInfo objects.
                    var existing = Settings.Guild_BuffList.Where(b => b != null).GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
                    var buffs = new List<GuildBuffInfo>();
                    foreach (var dto in parsed.Buffs)
                    {
                        if (!existing.TryGetValue(dto.Id, out var info)) info = new GuildBuffInfo();
                        GuildSettingsModel.ApplyBuff(dto, info);
                        buffs.Add(info);
                    }

                    Settings.Guild_BuffList.Clear();
                    Settings.Guild_BuffList.AddRange(buffs);

                    return (Before: before, After: GuildSnapshot());
                }).ConfigureAwait(false);

                iniFailure = BsGate.TrySaveIni("GuildSettings.ini", Settings.SaveGuildSettings);
                return result;
            }).ConfigureAwait(false);

            WebAdminAudit.Record(req, "systems.guilds", "GuildSettings.ini", new
            {
                changes = GuildDiff(outcome.Before, outcome.After),
                warnings = v.Warnings,
            }, reason);

            BsGate.ThrowIfFailed(iniFailure);

            await req.Ok(new { guilds = GuildResponse(outcome.After), warnings = v.Warnings }, "Saved guild settings to Configs\\GuildSettings.ini; they apply immediately.").ConfigureAwait(false);
        }

        private static List<object> GuildDiff(GuildSettingsDto before, GuildSettingsDto after)
        {
            var diffs = new List<object>();

            void Scalar(string field, object a, object b)
            {
                if (!Equals(a, b)) diffs.Add(new { field, from = a, to = b });
            }

            Scalar("RequiredLevel", before.RequiredLevel, after.RequiredLevel);
            Scalar("PointPerLevel", before.PointPerLevel, after.PointPerLevel);
            Scalar("ExpRate", before.ExpRate, after.ExpRate);
            Scalar("NewbieGuildBuffEnabled", before.NewbieGuildBuffEnabled, after.NewbieGuildBuffEnabled);
            Scalar("NewbieGuildExpBuff", before.NewbieGuildExpBuff, after.NewbieGuildExpBuff);
            Scalar("WarTime", before.WarTime, after.WarTime);
            Scalar("WarCost", before.WarCost, after.WarCost);
            Scalar("BuyGTGold", before.BuyGTGold, after.BuyGTGold);
            Scalar("ExtendGT", before.ExtendGT, after.ExtendGT);
            Scalar("GTDays", before.GTDays, after.GTDays);

            if (BsRev.Of(before.CreationCosts) != BsRev.Of(after.CreationCosts))
                diffs.Add(new { field = "CreationCosts", from = before.CreationCosts, to = after.CreationCosts });
            if (BsRev.Of(before.Levels) != BsRev.Of(after.Levels))
                diffs.Add(new { field = "Levels", from = before.Levels.Count + " level(s)", to = after.Levels });

            var oldBuffs = before.Buffs.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
            var newBuffs = after.Buffs.GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (var b in after.Buffs)
            {
                if (!oldBuffs.TryGetValue(b.Id, out var o)) diffs.Add(new { field = "Buff " + b.Id, from = (object)null, to = b });
                else if (BsRev.Of(o) != BsRev.Of(b)) diffs.Add(new { field = "Buff " + b.Id, from = o, to = b });
            }

            foreach (var b in before.Buffs)
                if (!newBuffs.ContainsKey(b.Id)) diffs.Add(new { field = "Buff " + b.Id, from = b, to = (object)null });

            return diffs;
        }

        // ------------------------------------------------------------------------------------------------ mining

        /// <summary>1-based mine number → "Map title (file)" for maps of the Edit database that use it (MineIndex or a mine zone).</summary>
        private static Dictionary<int, List<string>> MineUsage()
        {
            var usage = new Dictionary<int, List<string>>();

            void Add(int mine, string map)
            {
                if (mine <= 0) return;
                if (!usage.TryGetValue(mine, out var list)) usage[mine] = list = new List<string>();
                if (!list.Contains(map)) list.Add(map);
            }

            lock (WebAdminGame.EditLock)
            {
                foreach (var map in Envir.Edit.MapInfoList)
                {
                    if (map == null) continue;
                    var label = string.IsNullOrEmpty(map.Title) ? map.FileName : map.Title + " (" + map.FileName + ")";

                    Add(map.MineIndex, label);
                    if (map.MineZones == null) continue;
                    foreach (var zone in map.MineZones)
                        if (zone != null) Add(zone.Mine, label);
                }
            }

            return usage;
        }

        private static object MiningResponse(List<MineSetDto> sets)
        {
            return new
            {
                sets,
                rev = MiningRev(sets),
                maxSets = MiningModel.MaxSets,
                file = "Mines.ini",
                notes = new[]
                {
                    "Maps refer to mine sets by number (Map info MineIndex / mine zones), so sets can only be removed from the end.",
                    "Edits to existing sets apply to the running world at once; new sets are used after the next world start.",
                },
            };
        }

        private static List<MineSetDto> MiningSnapshot(Dictionary<int, List<string>> usage)
        {
            return Settings.MineSetList.Select((set, i) =>
            {
                usage.TryGetValue(i + 1, out var maps);
                return MiningModel.ToDto(set, i + 1, maps);
            }).ToList();
        }

        private static async Task GetMining(WebAdminRequest req)
        {
            var usage = MineUsage();
            var sets = await BsGate.OnSettingsAsync(_ => MiningSnapshot(usage)).ConfigureAwait(false);
            await req.Json(MiningResponse(sets)).ConfigureAwait(false);
        }

        private static async Task PutMining(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var usage = MineUsage();
            var items = BsLookups.Items();
            var reason = BsJson.Reason(body);

            WebAdminException iniFailure = null;
            var outcome = await BsGate.MutateAsync(async () =>
            {
                // Only the set count (and the rev) come from live state: the body walk / validation runs here, off the game thread.
                var current = await BsGate.OnSettingsAsync(_ => (Count: Settings.MineSetList.Count, Rev: MiningRev(MiningSnapshot(usage)))).ConfigureAwait(false);
                BsRev.Check(body, current.Rev);

                var v = new BsValidation();
                var parsed = MiningModel.Parse(body, current.Count, usage, items.Resolve, items.Count > 0, v);
                v.ThrowIfInvalid();

                var result = await BsGate.OnSettingsAsync(onGameThread =>
                {
                    var before = MiningSnapshot(usage);
                    if (MiningRev(before) != current.Rev)
                        throw new WebAdminException(409, "The mine sets changed while this save was being checked. Reload and try again.");

                    var list = Settings.MineSetList;
                    for (var i = 0; i < parsed.Count; i++)
                    {
                        var dto = parsed[i];
                        var drops = dto.Drops.Select(MiningModel.ToDrop).ToList();

                        MineSet set;
                        if (onGameThread && i < list.Count)
                        {
                            // Running: maps hold these MineSet objects, so update them in place and link the drop items now.
                            set = list[i];
                            foreach (var drop in drops) drop.Item = Envir.Main.GetItemInfo(drop.ItemName);
                        }
                        else
                        {
                            // Stopped (or a new set): fresh objects so MineSet.SetDrops links the items when the maps load.
                            set = new MineSet();
                        }

                        set.Name = dto.Name;
                        set.SpotRegenRate = (byte)dto.SpotRegenRate;
                        set.MaxStones = (byte)dto.MaxStones;
                        set.HitRate = (byte)dto.HitRate;
                        set.DropRate = (byte)dto.DropRate;
                        set.TotalSlots = (byte)dto.TotalSlots;
                        set.Drops = drops;

                        if (i < list.Count) list[i] = set;
                        else list.Add(set);
                    }

                    while (list.Count > parsed.Count) list.RemoveAt(list.Count - 1);

                    return (Before: before, After: MiningSnapshot(usage), Warnings: v.Warnings, Live: onGameThread);
                }).ConfigureAwait(false);

                iniFailure = BsGate.TrySaveIni("Mines.ini", Settings.SaveMines);
                return result;
            }).ConfigureAwait(false);

            WebAdminAudit.Record(req, "systems.mining", "Mines.ini", new
            {
                before = outcome.Before.Select(s => new { s.Index, s.Name, s.SpotRegenRate, s.MaxStones, s.HitRate, s.DropRate, s.TotalSlots, drops = s.Drops.Count }).ToList(),
                after = outcome.After.Select(s => new { s.Index, s.Name, s.SpotRegenRate, s.MaxStones, s.HitRate, s.DropRate, s.TotalSlots, s.Drops }).ToList(),
                warnings = outcome.Warnings,
            }, reason);

            BsGate.ThrowIfFailed(iniFailure);

            await req.Ok(new { mining = MiningResponse(outcome.After), warnings = outcome.Warnings },
                "Saved " + outcome.After.Count + " mine set(s) to Configs\\Mines.ini" + (outcome.Live ? "; existing sets apply immediately." : ".")).ConfigureAwait(false);
        }

        private static string MiningRev(List<MineSetDto> sets)
        {
            return BsRev.Of(sets.Select(s => new { s.Name, s.SpotRegenRate, s.MaxStones, s.HitRate, s.DropRate, s.TotalSlots, s.Drops }).ToList());
        }

        // ------------------------------------------------------------------------------------------------ spawn tick

        /// <summary>The Edit database is loaded (SaveDB on an empty Edit envir would wipe Server.MirDB).</summary>
        private static bool EditLoaded()
        {
            return Envir.Edit.MapInfoList.Count > 0 || Envir.Edit.ItemInfoList.Count > 0 || Envir.Edit.MonsterInfoList.Count > 0;
        }

        private static async Task<object> LiveSpawnTick()
        {
            if (!WebAdminGame.IsRunning) return null;

            try
            {
                return await WebAdminGame.RunAsync(() =>
                {
                    var timer = Envir.Main.RespawnTick;
                    var dto = SpawnTickModel.ToDto(timer);
                    var players = Envir.Main.PlayerCount;

                    return (object)new
                    {
                        dto.BaseSpawnRate,
                        dto.Options,
                        players,
                        multiplier = SpawnTickModel.Multiplier(dto.Options, players),
                        currentDelayMs = timer.CurrentDelay,
                        tickCounter = timer.CurrentTickcounter,
                    };
                }).ConfigureAwait(false);
            }
            catch (WebAdminException)
            {
                return null;
            }
        }

        private static (SpawnTickDto Dto, string Rev) EditSpawnTick()
        {
            lock (WebAdminGame.EditLock)
            {
                var dto = SpawnTickModel.ToDto(Envir.Edit.RespawnTick);
                return (dto, BsRev.Of(dto));
            }
        }

        private static async Task GetSpawnTick(WebAdminRequest req)
        {
            var edit = EditSpawnTick();
            var live = await LiveSpawnTick().ConfigureAwait(false);

            await req.Json(new
            {
                baseSpawnRate = edit.Dto.BaseSpawnRate,
                options = edit.Dto.Options,
                rev = edit.Rev,
                editLoaded = EditLoaded(),
                live,
                systemDataStale = Envir.SystemDataStale,
                world = WebAdminGame.WorldState,
                notes = new[]
                {
                    "The respawn tick is stored in Server.MirDB. Saving writes the whole editor database, including any unsaved Game Data edits.",
                    "Saved values take effect when the world restarts; tick \"Apply to the running world\" to use them right away.",
                },
            }).ConfigureAwait(false);
        }

        private static async Task PutSpawnTick(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);

            var v = new BsValidation();
            var parsed = SpawnTickModel.Parse(body, v);
            v.ThrowIfInvalid();

            var applyLive = body.ValueKind == JsonValueKind.Object && body.GetBool("applyLive");
            var reason = BsJson.Reason(body);

            var outcome = await BsGate.MutateAsync(async () =>
            {
                SpawnTickDto before;

                lock (WebAdminGame.EditLock)
                {
                    if (!EditLoaded())
                        throw new WebAdminException(409, "The editor database is not loaded, so it cannot be saved. Start or reboot the server first.");

                    before = SpawnTickModel.ToDto(Envir.Edit.RespawnTick);
                    BsRev.Check(body, BsRev.Of(before));

                    var timer = Envir.Edit.RespawnTick;
                    var oldRate = timer.BaseSpawnRate;
                    var oldOptions = timer.Respawn;

                    timer.BaseSpawnRate = (byte)parsed.BaseSpawnRate;
                    timer.Respawn = parsed.Options.Select(o => new RespawnTickOption { UserCount = o.UserCount, DelayLoss = o.DelayLoss }).ToList();

                    try
                    {
                        Envir.Edit.SaveDB();
                    }
                    catch (Exception ex)
                    {
                        timer.BaseSpawnRate = oldRate;
                        timer.Respawn = oldOptions;
                        WebAdminLog.Error(ex, "Edit.SaveDB failed while saving the respawn tick");
                        throw new WebAdminException(500, "Writing Server.MirDB failed (see the server log); nothing was changed.");
                    }
                }

                // Server.MirDB is written from here on: whatever happens to the live apply, the request reports success, records the
                // audit entry and flags a pending reboot (Edit.SaveDB also set Envir.SystemDataStale and may have written other
                // unsaved Game Data edits, which only a world restart picks up).
                var live = false;
                string liveError = null;
                if (applyLive)
                {
                    if (!WebAdminGame.IsRunning)
                    {
                        liveError = "the world is not running";
                    }
                    else
                    {
                        try
                        {
                            await WebAdminGame.RunAsync(() =>
                            {
                                var timer = Envir.Main.RespawnTick;
                                timer.BaseSpawnRate = (byte)parsed.BaseSpawnRate;
                                timer.Respawn = parsed.Options.Select(o => new RespawnTickOption { UserCount = o.UserCount, DelayLoss = o.DelayLoss }).ToList();
                                timer.LastUsercount = -1; // force GetTickSpeed to recompute CurrentDelay on the next tick
                            }).ConfigureAwait(false);
                            live = true;
                        }
                        catch (WebAdminException ex)
                        {
                            // 503 (world stopped meanwhile) / 504 (game thread timeout): the saved values still apply after a restart.
                            liveError = ex.Message;
                        }
                    }
                }

                WebAdminPending.Add("Spawn tick", "Respawn tick saved to Server.MirDB: base " + parsed.BaseSpawnRate + " min, " + parsed.Options.Count + " option(s)"
                    + (live ? " (already applied to the running world)" : string.Empty), req.AdminName);

                return (Before: before, Live: live, LiveError: liveError);
            }).ConfigureAwait(false);

            WebAdminAudit.Record(req, "systems.spawntick", "Server.MirDB", new
            {
                before = outcome.Before,
                after = parsed,
                appliedLive = outcome.Live,
                liveError = outcome.LiveError,
                warnings = v.Warnings,
            }, reason);

            var warnings = v.Warnings.ToList();
            if (outcome.LiveError != null) warnings.Add("Not applied to the running world (" + outcome.LiveError + "); it takes effect after the next world restart.");

            var message = outcome.Live
                ? "Saved the respawn tick to Server.MirDB and applied it to the running world."
                : "Saved the respawn tick to Server.MirDB; it takes effect after the next world restart.";

            await req.Ok(new { appliedLive = outcome.Live, pendingReboot = true, warnings }, message).ConfigureAwait(false);
        }
    }
}

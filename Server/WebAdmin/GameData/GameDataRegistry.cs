using System.Drawing;
using System.Globalization;
using Server.MirDatabase;

namespace Server.WebAdmin.GameData
{
    /// <summary>
    /// The game-data grid tables over the Edit envir (contract §3.4).
    /// <para>Row keys ("Index"):</para>
    /// <list type="bullet">
    /// <item>items / monsters / npcs / quests / maps / conquests: the record's own Index.</item>
    /// <item>magic: <c>(int)MagicInfo.Spell</c> (no insert / delete: the server creates every spell itself).</item>
    /// <item>gameshop: <c>GameShopItem.GIndex</c>.</item>
    /// <item>maprespawns: <c>RespawnInfo.RespawnIndex</c> (globally unique, allocated with <c>++Envir.Edit.RespawnIndex</c>).</item>
    /// <item>mapsafezones / mapmovements / mapminezones: <c>MapIndex * 10000 + position</c> in the map's list (0-based). Positions shift
    /// after a delete, so the row's <c>__rev</c> must always be sent back; a stale key is reported as a conflict, never applied to the
    /// wrong row.</item>
    /// <item>conquest children (guards, gates, walls, sieges, flags, control points): <c>ConquestIndex * 10000 + position</c>; the
    /// record's own per-conquest Index is published read-only as <c>LocalIndex</c>.</item>
    /// <item>dragon: the single DragonInfo, Index 1.</item>
    /// </list>
    /// </summary>
    internal static class GameDataRegistry
    {
        /// <summary>Child-table key stride (key = parent * Stride + position).</summary>
        public const int Stride = 10000;

        private static readonly Lazy<IReadOnlyList<GameDataTable>> AllTables = new Lazy<IReadOnlyList<GameDataTable>>(Build);

        private static readonly Lazy<Dictionary<string, GameDataTable>> ByEndpoint = new Lazy<Dictionary<string, GameDataTable>>(
            () => AllTables.Value.ToDictionary(t => t.Endpoint, StringComparer.OrdinalIgnoreCase));

        public static IReadOnlyList<GameDataTable> Tables => AllTables.Value;

        public static GameDataTable Get(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return null;
            return ByEndpoint.Value.TryGetValue(endpoint.Trim(), out var t) ? t : null;
        }

        private static List<GameDataTable> Build()
        {
            return new List<GameDataTable>
            {
                Items(),
                Monsters(),
                Magic(),
                Npcs(),
                Quests(),
                Maps(),
                MapSafeZones(),
                MapRespawns(),
                MapMovements(),
                MapMineZones(),
                GameShop(),
                Conquests(),
                ConquestChild<ConquestArcherInfo>("conquestguards", "Guards", c => c.ConquestGuards, c => ++c.GuardIndex, "Guard", true,
                    (e, i) => e.Index = i, e => { e.Name = "Guard"; e.RepairCost = 1000; }),
                ConquestChild<ConquestGateInfo>("conquestgates", "Gates", c => c.ConquestGates, c => ++c.GateIndex, "Gate", true,
                    (e, i) => e.Index = i, e => { e.Name = "Gate"; e.RepairCost = 1000; }),
                ConquestChild<ConquestWallInfo>("conquestwalls", "Walls", c => c.ConquestWalls, c => ++c.WallIndex, "Wall", true,
                    (e, i) => e.Index = i, e => { e.Name = "Wall"; e.RepairCost = 1000; }),
                ConquestChild<ConquestSiegeInfo>("conquestsieges", "Sieges", c => c.ConquestSieges, c => ++c.SiegeIndex, "Siege", true,
                    (e, i) => e.Index = i, e => { e.Name = "Siege"; e.RepairCost = 1000; }),
                ConquestChild<ConquestFlagInfo>("conquestflags", "Flags", c => c.ConquestFlags, c => ++c.FlagIndex, "Flag", false,
                    (e, i) => e.Index = i, e => { e.Name = "Flag"; }),
                ConquestChild<ConquestFlagInfo>("conquestcontrolpoints", "Control points", c => c.ControlPoints, c => ++c.ControlPointIndex,
                    "Control Point", false, (e, i) => e.Index = i, e => { e.Name = "Control Point"; }),
                Dragon(),
            };
        }

        // ================================================================== items

        private static GameDataTable Items()
        {
            var opts = new GameDataReflectOptions();
            opts.Exclude.Add(nameof(ItemInfo.Index));
            opts.Exclude.Add(nameof(ItemInfo.RandomStats));
            opts.Multiline.Add(nameof(ItemInfo.ToolTip));

            return new GameDataTable
            {
                Endpoint = "items",
                Label = "Items",
                Group = "items",
                EntityType = typeof(ItemInfo),
                Insertable = true,
                Deletable = true,
                NameColumn = "Name",
                IndexNote = "ItemInfo.Index",
                Columns = GameDataColumns.Reflect(typeof(ItemInfo), opts),
                Rows = ctx => TopRows(ctx.Items, i => i.Index),
                NewDetached = ctx => new GameDataRow { Entity = new ItemInfo { RandomStatsId = 255 } },
                Attach = (ctx, row, parent) =>
                {
                    var item = (ItemInfo)row.Entity;
                    item.Index = ctx.NextItemIndex();
                    row.Index = item.Index;
                    ctx.Items.Add(item);
                },
                Remove = (ctx, row) => ctx.Items.Remove((ItemInfo)row.Entity),
                DeleteGuard = (ctx, row) =>
                {
                    var used = ctx.GameShop.Where(g => g.ItemIndex == row.Index).Select(g => "game shop #" + g.GIndex.ToString(CultureInfo.InvariantCulture));
                    return Describe("Used by", used);
                },
                Validate = (ctx, row, changed) =>
                {
                    var item = (ItemInfo)row.Entity;
                    var errors = new List<GameDataFieldError>();
                    if (changed.Contains("Name")) NameRules(errors, item.Name, ctx.Items.Where(x => x.Index != row.Index).Select(x => x.Name), "item");
                    if (changed.Contains("StackSize") && item.StackSize < 1) errors.Add(new GameDataFieldError("StackSize", "must be at least 1"));
                    return errors;
                },
            };
        }

        // ================================================================== monsters

        private static GameDataTable Monsters()
        {
            var opts = new GameDataReflectOptions { LiveSafeByDefault = true };
            opts.Exclude.Add(nameof(MonsterInfo.Index));
            opts.Exclude.Add(nameof(MonsterInfo.HasSpawnScript));
            opts.Exclude.Add(nameof(MonsterInfo.HasDieScript));
            opts.NotLive.Add(nameof(MonsterInfo.Name));
            opts.NotLive.Add(nameof(MonsterInfo.DropPath));

            return new GameDataTable
            {
                Endpoint = "monsters",
                Label = "Monsters",
                Group = "monsters",
                EntityType = typeof(MonsterInfo),
                Insertable = true,
                Deletable = true,
                NameColumn = "Name",
                IndexNote = "MonsterInfo.Index",
                Columns = GameDataColumns.Reflect(typeof(MonsterInfo), opts),
                Rows = ctx => TopRows(ctx.Monsters, m => m.Index),
                NewDetached = ctx => new GameDataRow { Entity = new MonsterInfo() },
                Attach = (ctx, row, parent) =>
                {
                    var m = (MonsterInfo)row.Entity;
                    m.Index = ctx.NextMonsterIndex();
                    row.Index = m.Index;
                    ctx.Monsters.Add(m);
                },
                Remove = (ctx, row) => ctx.Monsters.Remove((MonsterInfo)row.Entity),
                DeleteGuard = (ctx, row) =>
                {
                    var refs = new List<string>();
                    foreach (var map in ctx.Maps)
                        foreach (var r in map.Respawns)
                            if (r.MonsterIndex == row.Index) refs.Add("respawn #" + r.RespawnIndex.ToString(CultureInfo.InvariantCulture) + " on map " + MapLabel(map));

                    foreach (var c in ctx.Conquests)
                    {
                        if (c.ConquestGuards.Any(x => x.MobIndex == row.Index)) refs.Add("guards of conquest " + c.Index.ToString(CultureInfo.InvariantCulture));
                        if (c.ConquestGates.Any(x => x.MobIndex == row.Index)) refs.Add("gates of conquest " + c.Index.ToString(CultureInfo.InvariantCulture));
                        if (c.ConquestWalls.Any(x => x.MobIndex == row.Index)) refs.Add("walls of conquest " + c.Index.ToString(CultureInfo.InvariantCulture));
                        if (c.ConquestSieges.Any(x => x.MobIndex == row.Index)) refs.Add("sieges of conquest " + c.Index.ToString(CultureInfo.InvariantCulture));
                    }

                    return Describe("Used by", refs);
                },
                Validate = (ctx, row, changed) =>
                {
                    var m = (MonsterInfo)row.Entity;
                    var errors = new List<GameDataFieldError>();
                    if (changed.Contains("Name")) NameRules(errors, m.Name, ctx.Monsters.Where(x => x.Index != row.Index).Select(x => x.Name), "monster");
                    return errors;
                },
            };
        }

        // ================================================================== magic

        private static GameDataTable Magic()
        {
            var opts = new GameDataReflectOptions();
            opts.ReadOnly.Add(nameof(MagicInfo.Spell));

            return new GameDataTable
            {
                Endpoint = "magic",
                Label = "Magic",
                Group = "magic",
                EntityType = typeof(MagicInfo),
                Insertable = false,
                Deletable = false,
                NameColumn = "Name",
                IndexNote = "(int)MagicInfo.Spell",
                Columns = GameDataColumns.Reflect(typeof(MagicInfo), opts),
                Rows = ctx => ctx.Magics.Select((m, i) => new GameDataRow { Index = (int)m.Spell, Entity = m, Position = i }).Where(r => r.Index > 0),
                Validate = (ctx, row, changed) =>
                {
                    var m = (MagicInfo)row.Entity;
                    var errors = new List<GameDataFieldError>();
                    if (changed.Contains("Name") && string.IsNullOrWhiteSpace(m.Name)) errors.Add(new GameDataFieldError("Name", "is required"));
                    return errors;
                },
            };
        }

        // ================================================================== npcs

        private static GameDataTable Npcs()
        {
            var opts = new GameDataReflectOptions();
            opts.Exclude.Add(nameof(NPCInfo.Index));
            opts.Exclude.Add(nameof(NPCInfo.Sabuk)); // not persisted (legacy pre-v66 flag)
            opts.Lookups["MapIndex"] = "maps";
            opts.Required.Add("MapIndex");
            opts.Lookups["Conquest"] = "conquests";
            opts.Lookups["CollectQuestIndexes"] = "quests";
            opts.Lookups["FinishQuestIndexes"] = "quests";

            return new GameDataTable
            {
                Endpoint = "npcs",
                Label = "NPCs",
                Group = "npcs",
                EntityType = typeof(NPCInfo),
                Insertable = true,
                Deletable = true,
                NameColumn = "Name",
                IndexNote = "NPCInfo.Index",
                Columns = GameDataColumns.Reflect(typeof(NPCInfo), opts),
                Rows = ctx => TopRows(ctx.Npcs, n => n.Index),
                NewDetached = ctx => new GameDataRow { Entity = new NPCInfo() },
                Attach = (ctx, row, parent) =>
                {
                    var n = (NPCInfo)row.Entity;
                    n.Index = ctx.NextNpcIndex();
                    row.Index = n.Index;
                    ctx.Npcs.Add(n);
                },
                Remove = (ctx, row) => ctx.Npcs.Remove((NPCInfo)row.Entity),
                Validate = (ctx, row, changed) =>
                {
                    var n = (NPCInfo)row.Entity;
                    var errors = new List<GameDataFieldError>();
                    if (changed.Contains("FileName") && string.IsNullOrWhiteSpace(n.FileName)) errors.Add(new GameDataFieldError("FileName", "is required (the script file under Envir\\NPCs)"));
                    if (changed.Contains("FileName") && !string.IsNullOrWhiteSpace(n.FileName) && !IsSafeRelativeScriptName(n.FileName))
                        errors.Add(new GameDataFieldError("FileName", "must be a relative script name (no drive, no '..')"));
                    if (changed.Contains("HourStart") && n.HourStart > 23) errors.Add(new GameDataFieldError("HourStart", "must be 0-23"));
                    if (changed.Contains("HourEnd") && n.HourEnd > 23) errors.Add(new GameDataFieldError("HourEnd", "must be 0-23"));
                    if (changed.Contains("MinuteStart") && n.MinuteStart > 59) errors.Add(new GameDataFieldError("MinuteStart", "must be 0-59"));
                    if (changed.Contains("MinuteEnd") && n.MinuteEnd > 59) errors.Add(new GameDataFieldError("MinuteEnd", "must be 0-59"));
                    return errors;
                },
            };
        }

        // ================================================================== quests

        private static GameDataTable Quests()
        {
            // Only the persisted fields: tasks / rewards / descriptions live in the quest txt file, NpcIndex is runtime and FinishNPC throws.
            var opts = new GameDataReflectOptions
            {
                Include = new[]
                {
                    nameof(QuestInfo.Name), nameof(QuestInfo.Group), nameof(QuestInfo.FileName), nameof(QuestInfo.RequiredMinLevel),
                    nameof(QuestInfo.RequiredMaxLevel), nameof(QuestInfo.RequiredQuest), nameof(QuestInfo.RequiredClass), nameof(QuestInfo.Type),
                    nameof(QuestInfo.GotoMessage), nameof(QuestInfo.KillMessage), nameof(QuestInfo.ItemMessage), nameof(QuestInfo.FlagMessage),
                    nameof(QuestInfo.TimeLimitInSeconds),
                },
            };
            opts.Lookups["RequiredQuest"] = "quests";

            return new GameDataTable
            {
                Endpoint = "quests",
                Label = "Quests",
                Group = "quests",
                EntityType = typeof(QuestInfo),
                Insertable = true,
                Deletable = true,
                NameColumn = "Name",
                IndexNote = "QuestInfo.Index",
                Columns = GameDataColumns.Reflect(typeof(QuestInfo), opts),
                Rows = ctx => TopRows(ctx.Quests, q => q.Index),
                NewDetached = ctx => new GameDataRow { Entity = new QuestInfo() },
                Attach = (ctx, row, parent) =>
                {
                    var q = (QuestInfo)row.Entity;
                    q.Index = ctx.NextQuestIndex();
                    row.Index = q.Index;
                    ctx.Quests.Add(q);
                },
                Remove = (ctx, row) => ctx.Quests.Remove((QuestInfo)row.Entity),
                DeleteGuard = (ctx, row) =>
                {
                    var refs = new List<string>();
                    foreach (var n in ctx.Npcs)
                        if (n.CollectQuestIndexes.Contains(row.Index) || n.FinishQuestIndexes.Contains(row.Index))
                            refs.Add("NPC #" + n.Index.ToString(CultureInfo.InvariantCulture) + " " + n.Name);

                    foreach (var q in ctx.Quests)
                        if (q.Index != row.Index && q.RequiredQuest == row.Index)
                            refs.Add("quest #" + q.Index.ToString(CultureInfo.InvariantCulture) + " " + q.Name + " (required quest)");

                    return Describe("Used by", refs);
                },
                Validate = (ctx, row, changed) =>
                {
                    var q = (QuestInfo)row.Entity;
                    var errors = new List<GameDataFieldError>();
                    if (changed.Contains("Name") && string.IsNullOrWhiteSpace(q.Name)) errors.Add(new GameDataFieldError("Name", "is required"));
                    if (changed.Contains("RequiredQuest") && q.RequiredQuest != 0 && q.RequiredQuest == row.Index)
                        errors.Add(new GameDataFieldError("RequiredQuest", "a quest cannot require itself"));
                    if ((changed.Contains("RequiredMinLevel") || changed.Contains("RequiredMaxLevel")) && q.RequiredMaxLevel > 0 && q.RequiredMinLevel > q.RequiredMaxLevel)
                        errors.Add(new GameDataFieldError("RequiredMinLevel", "is higher than RequiredMaxLevel"));
                    if (changed.Contains("FileName") && !string.IsNullOrWhiteSpace(q.FileName) && !IsSafeRelativeScriptName(q.FileName))
                        errors.Add(new GameDataFieldError("FileName", "must be a relative file name (no drive, no '..')"));
                    return errors;
                },
            };
        }

        // ================================================================== maps

        private static GameDataTable Maps()
        {
            var opts = new GameDataReflectOptions();
            opts.Exclude.Add(nameof(MapInfo.Index));
            opts.Lookups["MineIndex"] = "minesets";
            opts.ForceFlags.Add(nameof(MapInfo.WeatherParticles)); // bit values, combined by MapInfoForm although the enum lacks [Flags]

            return new GameDataTable
            {
                Endpoint = "maps",
                Label = "Maps",
                Group = "maps",
                EntityType = typeof(MapInfo),
                Insertable = true,
                Deletable = true,
                NameColumn = "Title",
                IndexNote = "MapInfo.Index",
                Columns = GameDataColumns.Reflect(typeof(MapInfo), opts),
                Rows = ctx => TopRows(ctx.Maps, m => m.Index),
                NewDetached = ctx => new GameDataRow { Entity = new MapInfo() },
                Attach = (ctx, row, parent) =>
                {
                    var m = (MapInfo)row.Entity;
                    m.Index = ctx.NextMapIndex();
                    row.Index = m.Index;
                    ctx.Maps.Add(m);
                },
                Remove = (ctx, row) => ctx.Maps.Remove((MapInfo)row.Entity),
                DeleteGuard = (ctx, row) =>
                {
                    var refs = new List<string>();
                    foreach (var n in ctx.Npcs)
                        if (n.MapIndex == row.Index) refs.Add("NPC #" + n.Index.ToString(CultureInfo.InvariantCulture) + " " + n.Name);

                    foreach (var map in ctx.Maps)
                    {
                        if (map.Index == row.Index) continue; // its own movements go with it
                        if (map.Movements.Any(mv => mv.MapIndex == row.Index)) refs.Add("movements on map " + MapLabel(map));
                    }

                    foreach (var c in ctx.Conquests)
                        if (c.MapIndex == row.Index || c.PalaceIndex == row.Index || c.ExtraMaps.Contains(row.Index))
                            refs.Add("conquest #" + c.Index.ToString(CultureInfo.InvariantCulture) + " " + c.Name);

                    return Describe("Used by", refs);
                },
                Validate = (ctx, row, changed) =>
                {
                    var m = (MapInfo)row.Entity;
                    var errors = new List<GameDataFieldError>();
                    if (changed.Contains("FileName") && string.IsNullOrWhiteSpace(m.FileName)) errors.Add(new GameDataFieldError("FileName", "is required (the .map file name)"));
                    if (changed.Contains("FileName") && !string.IsNullOrWhiteSpace(m.FileName) && !IsSafeRelativeScriptName(m.FileName))
                        errors.Add(new GameDataFieldError("FileName", "must be a plain file name (no drive, no '..')"));
                    return errors;
                },
            };
        }

        private static GameDataTable MapSafeZones()
        {
            var opts = new GameDataReflectOptions();
            opts.Exclude.Add(nameof(SafeZoneInfo.Info));

            return MapChild<SafeZoneInfo>("mapsafezones", "Safe zones", m => m.SafeZones, opts, null,
                (ctx, map) => new SafeZoneInfo { Info = map, Size = 10 }, null);
        }

        private static GameDataTable MapRespawns()
        {
            var opts = new GameDataReflectOptions();
            opts.Exclude.Add(nameof(RespawnInfo.RespawnIndex));
            opts.Lookups["MonsterIndex"] = "monsters";
            opts.Required.Add("MonsterIndex");

            return MapChild<RespawnInfo>("maprespawns", "Respawns", m => m.Respawns, opts, r => r.RespawnIndex,
                (ctx, map) => new RespawnInfo { Count = 1, Spread = 0, Delay = 10 },
                (ctx, r) => r.RespawnIndex = ctx.NextRespawnIndex(),
                (ctx, row, changed) =>
                {
                    var r = (RespawnInfo)row.Entity;
                    var errors = new List<GameDataFieldError>();
                    if (changed.Contains("RoutePath") && !string.IsNullOrWhiteSpace(r.RoutePath) && !IsSafeRelativeScriptName(r.RoutePath))
                        errors.Add(new GameDataFieldError("RoutePath", "must be a relative route name (no drive, no '..')"));
                    if (changed.Contains("Direction") && r.Direction > 7) errors.Add(new GameDataFieldError("Direction", "must be 0-7"));
                    return errors;
                },
                "RespawnInfo.RespawnIndex (global)");
        }

        private static GameDataTable MapMovements()
        {
            var opts = new GameDataReflectOptions();
            opts.Rename[nameof(MovementInfo.MapIndex)] = "DestinationMapIndex"; // MapIndex is the owning (source) map's parent column
            opts.Lookups["DestinationMapIndex"] = "maps";
            opts.Required.Add("DestinationMapIndex");
            opts.Lookups["ConquestIndex"] = "conquests";

            return MapChild<MovementInfo>("mapmovements", "Movements", m => m.Movements, opts, null,
                (ctx, map) => new MovementInfo(), null);
        }

        private static GameDataTable MapMineZones()
        {
            var opts = new GameDataReflectOptions();
            opts.Lookups["Mine"] = "minesets";

            return MapChild<MineZone>("mapminezones", "Mine zones", m => m.MineZones, opts, null,
                (ctx, map) => new MineZone { Size = 10 }, null);
        }

        private static GameDataTable MapChild<T>(string endpoint, string label, Func<MapInfo, List<T>> list, GameDataReflectOptions opts,
            Func<T, int> key, Func<GameDataContext, MapInfo, T> create, Action<GameDataContext, T> allocate,
            Func<GameDataContext, GameDataRow, ISet<string>, IEnumerable<GameDataFieldError>> validate = null, string indexNote = null) where T : class
        {
            var columns = new List<GameDataColumn> { ParentColumn("MapIndex", "maps") };
            columns.AddRange(GameDataColumns.Reflect(typeof(T), opts));

            return new GameDataTable
            {
                Endpoint = endpoint,
                Label = label,
                Group = "maps",
                EntityType = typeof(T),
                Insertable = true,
                Deletable = true,
                ParentEndpoint = "maps",
                ParentField = "MapIndex",
                IndexNote = indexNote ?? "MapIndex * " + Stride.ToString(CultureInfo.InvariantCulture) + " + position (0-based) in the map's list",
                PositionKeyed = key == null,
                Columns = columns,
                Rows = ctx => ctx.Maps.SelectMany(map => list(map).Select((e, i) => new GameDataRow
                {
                    Index = key != null ? key(e) : map.Index * Stride + i,
                    Entity = e,
                    Parent = map,
                    ParentIndex = map.Index,
                    Position = i,
                })),
                NewDetached = ctx => new GameDataRow { Entity = create(ctx, null) },
                Attach = (ctx, row, parentIndex) =>
                {
                    var map = ctx.FindMap(parentIndex) ?? throw new InvalidOperationException("map " + parentIndex + " not found");
                    var e = (T)row.Entity;
                    if (e is SafeZoneInfo sz) sz.Info = map;
                    allocate?.Invoke(ctx, e);
                    var items = list(map);
                    items.Add(e);
                    row.Parent = map;
                    row.ParentIndex = map.Index;
                    row.Position = items.Count - 1;
                    row.Index = key != null ? key(e) : map.Index * Stride + row.Position;
                },
                Remove = (ctx, row) => list((MapInfo)row.Parent).Remove((T)row.Entity),
                Validate = validate,
            };
        }

        // ================================================================== game shop

        private static GameDataTable GameShop()
        {
            var opts = new GameDataReflectOptions();
            opts.Exclude.Add(nameof(GameShopItem.GIndex));
            opts.Exclude.Add(nameof(GameShopItem.Info));
            opts.Lookups["ItemIndex"] = "items";
            opts.Required.Add("ItemIndex");

            return new GameDataTable
            {
                Endpoint = "gameshop",
                Label = "Game Shop",
                Group = "gameshop",
                EntityType = typeof(GameShopItem),
                Insertable = true,
                Deletable = true,
                NameColumn = null,
                IndexNote = "GameShopItem.GIndex",
                Columns = GameDataColumns.Reflect(typeof(GameShopItem), opts),
                Rows = ctx => TopRows(ctx.GameShop, g => g.GIndex),
                NewDetached = ctx => new GameDataRow
                {
                    Entity = new GameShopItem
                    {
                        GoldPrice = ctx.DefaultGameShopGoldPrice(),
                        CreditPrice = 1000,
                        Count = 1,
                        Date = ctx.Now(),
                        Class = "All",
                        Category = string.Empty,
                    },
                },
                Attach = (ctx, row, parent) =>
                {
                    var g = (GameShopItem)row.Entity;
                    g.GIndex = ctx.NextGameShopIndex();
                    g.Info = ctx.FindItem(g.ItemIndex);
                    if (string.IsNullOrEmpty(g.Category) && g.Info != null) g.Category = g.Info.Type.ToString();
                    row.Index = g.GIndex;
                    ctx.GameShop.Add(g);
                },
                AfterApply = (ctx, row) =>
                {
                    var g = (GameShopItem)row.Entity;
                    if (g.Info == null || g.Info.Index != g.ItemIndex) g.Info = ctx.FindItem(g.ItemIndex);
                },
                Remove = (ctx, row) =>
                {
                    ctx.GameShop.Remove((GameShopItem)row.Entity);
                    if (ctx.GameShop.Count == 0) ctx.GameShopEmptied();
                },
                Validate = (ctx, row, changed) =>
                {
                    var g = (GameShopItem)row.Entity;
                    var errors = new List<GameDataFieldError>();
                    if (changed.Contains("Count") && g.Count < 1) errors.Add(new GameDataFieldError("Count", "must be at least 1"));
                    return errors;
                },
            };
        }

        // ================================================================== conquests

        private static GameDataTable Conquests()
        {
            var opts = new GameDataReflectOptions();
            opts.Exclude.Add(nameof(ConquestInfo.Index));
            opts.Lookups["MapIndex"] = "maps";
            opts.Required.Add("MapIndex");
            opts.Lookups["PalaceIndex"] = "maps";
            opts.Lookups["ExtraMaps"] = "maps";
            foreach (var counter in new[] { "GuardIndex", "GateIndex", "WallIndex", "SiegeIndex", "FlagIndex", "ControlPointIndex" })
                opts.ReadOnly.Add(counter);

            return new GameDataTable
            {
                Endpoint = "conquests",
                Label = "Conquests",
                Group = "conquests",
                EntityType = typeof(ConquestInfo),
                Insertable = true,
                Deletable = true,
                NameColumn = "Name",
                IndexNote = "ConquestInfo.Index",
                Columns = GameDataColumns.Reflect(typeof(ConquestInfo), opts),
                Rows = ctx => TopRows(ctx.Conquests, c => c.Index),
                NewDetached = ctx => new GameDataRow { Entity = new ConquestInfo { Name = "New Conquest", Size = 10, Location = new Point(0, 0) } },
                Attach = (ctx, row, parent) =>
                {
                    var c = (ConquestInfo)row.Entity;
                    c.Index = ctx.NextConquestIndex();
                    row.Index = c.Index;
                    ctx.Conquests.Add(c);
                },
                Remove = (ctx, row) => ctx.Conquests.Remove((ConquestInfo)row.Entity),
                DeleteGuard = (ctx, row) =>
                {
                    var refs = new List<string>();
                    foreach (var n in ctx.Npcs)
                        if (n.Conquest == row.Index) refs.Add("NPC #" + n.Index.ToString(CultureInfo.InvariantCulture) + " " + n.Name);

                    foreach (var map in ctx.Maps)
                        if (map.Movements.Any(mv => mv.ConquestIndex == row.Index)) refs.Add("movements on map " + MapLabel(map));

                    return Describe("Used by", refs);
                },
                Validate = (ctx, row, changed) =>
                {
                    var c = (ConquestInfo)row.Entity;
                    var errors = new List<GameDataFieldError>();
                    if (changed.Contains("Name") && string.IsNullOrWhiteSpace(c.Name)) errors.Add(new GameDataFieldError("Name", "is required"));
                    if (changed.Contains("StartHour") && c.StartHour > 23) errors.Add(new GameDataFieldError("StartHour", "must be 0-23"));
                    if (changed.Contains("WarLength") && c.WarLength < 1) errors.Add(new GameDataFieldError("WarLength", "must be at least 1 minute"));
                    return errors;
                },
            };
        }

        private static GameDataTable ConquestChild<T>(string endpoint, string label, Func<ConquestInfo, List<T>> list,
            Func<ConquestInfo, int> allocate, string defaultName, bool hasMob, Action<T, int> setLocal,
            Action<T> defaults) where T : class, new()
        {
            var opts = new GameDataReflectOptions();
            opts.Rename["Index"] = "LocalIndex";
            opts.ReadOnly.Add("LocalIndex");
            if (hasMob)
            {
                opts.Lookups["MobIndex"] = "monsters";
                opts.Required.Add("MobIndex");
            }

            var columns = new List<GameDataColumn> { ParentColumn("ConquestIndex", "conquests") };
            columns.AddRange(GameDataColumns.Reflect(typeof(T), opts));

            return new GameDataTable
            {
                Endpoint = endpoint,
                Label = label,
                Group = "conquests",
                EntityType = typeof(T),
                Insertable = true,
                Deletable = true,
                ParentEndpoint = "conquests",
                ParentField = "ConquestIndex",
                NameColumn = "Name",
                IndexNote = "ConquestIndex * " + Stride.ToString(CultureInfo.InvariantCulture) + " + position (0-based); the record's own index is LocalIndex",
                PositionKeyed = true,
                Columns = columns,
                Rows = ctx => ctx.Conquests.SelectMany(c => list(c).Select((e, i) => new GameDataRow
                {
                    Index = c.Index * Stride + i,
                    Entity = e,
                    Parent = c,
                    ParentIndex = c.Index,
                    Position = i,
                })),
                NewDetached = ctx =>
                {
                    var e = new T();
                    defaults(e);
                    return new GameDataRow { Entity = e };
                },
                Attach = (ctx, row, parentIndex) =>
                {
                    var c = ctx.FindConquest(parentIndex) ?? throw new InvalidOperationException("conquest " + parentIndex + " not found");
                    var e = (T)row.Entity;
                    setLocal(e, allocate(c));
                    var items = list(c);
                    items.Add(e);
                    row.Parent = c;
                    row.ParentIndex = c.Index;
                    row.Position = items.Count - 1;
                    row.Index = c.Index * Stride + row.Position;
                },
                Remove = (ctx, row) => list((ConquestInfo)row.Parent).Remove((T)row.Entity),
                Validate = (ctx, row, changed) =>
                {
                    var errors = new List<GameDataFieldError>();
                    var nameCol = changed.Contains("Name") ? typeof(T).GetField("Name") : null;
                    if (nameCol != null && string.IsNullOrWhiteSpace(nameCol.GetValue(row.Entity) as string))
                        errors.Add(new GameDataFieldError("Name", "is required (e.g. " + defaultName + ")"));
                    var file = changed.Contains("FileName") ? typeof(T).GetField("FileName")?.GetValue(row.Entity) as string : null;
                    if (!string.IsNullOrWhiteSpace(file) && !IsSafeRelativeScriptName(file))
                        errors.Add(new GameDataFieldError("FileName", "must be a relative script name (no drive, no '..')"));
                    return errors;
                },
            };
        }

        // ================================================================== dragon

        private static GameDataTable Dragon()
        {
            var opts = new GameDataReflectOptions();
            opts.Exclude.Add(nameof(DragonInfo.Level));      // runtime state, not saved
            opts.Exclude.Add(nameof(DragonInfo.Experience)); // runtime state, not saved

            return new GameDataTable
            {
                Endpoint = "dragon",
                Label = "Dragon",
                Group = "dragon",
                EntityType = typeof(DragonInfo),
                Insertable = false,
                Deletable = false,
                NameColumn = "MonsterName",
                IndexNote = "single record, Index 1",
                Columns = GameDataColumns.Reflect(typeof(DragonInfo), opts),
                Rows = ctx =>
                {
                    var d = ctx.Dragon();
                    return d == null ? Enumerable.Empty<GameDataRow>() : new[] { new GameDataRow { Index = 1, Entity = d } };
                },
                Validate = (ctx, row, changed) =>
                {
                    var d = (DragonInfo)row.Entity;
                    var errors = new List<GameDataFieldError>();
                    if (changed.Contains("MapFileName") && string.IsNullOrWhiteSpace(d.MapFileName)) errors.Add(new GameDataFieldError("MapFileName", "is required"));
                    if (changed.Contains("MonsterName") && string.IsNullOrWhiteSpace(d.MonsterName)) errors.Add(new GameDataFieldError("MonsterName", "is required"));
                    for (var i = 0; i < d.Exps.Length; i++)
                    {
                        var name = "Exps_" + (i + 1).ToString(CultureInfo.InvariantCulture);
                        if (changed.Contains(name) && d.Exps[i] < 0) errors.Add(new GameDataFieldError(name, "cannot be negative"));
                    }

                    return errors;
                },
            };
        }

        // ================================================================== helpers

        private static IEnumerable<GameDataRow> TopRows<T>(List<T> list, Func<T, int> key)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e == null) continue;
                yield return new GameDataRow { Index = key(e), Entity = e, Position = i };
            }
        }

        private static GameDataColumn ParentColumn(string name, string lookup)
        {
            return new GameDataColumn
            {
                Name = name,
                ValueType = typeof(int),
                Get = r => r.ParentIndex,
                Set = null,
                ReadOnly = true,
                IsParentField = true,
                Lookup = lookup,
                ForeignKey = lookup,
                Required = true,
                Group = "Identity",
            };
        }

        private static void NameRules(List<GameDataFieldError> errors, string name, IEnumerable<string> others, string what)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add(new GameDataFieldError("Name", "is required"));
                return;
            }

            if (name.Trim() != name) errors.Add(new GameDataFieldError("Name", "must not start or end with spaces"));
            else if (others.Any(o => string.Equals(o, name, StringComparison.OrdinalIgnoreCase)))
                errors.Add(new GameDataFieldError("Name", "another " + what + " is already named \"" + name + "\" (drop, recipe and quest files refer to " + what + "s by name)"));
        }

        /// <summary>A file name relative to its Envir folder: no rooted path, drive, ".." segment or NUL.</summary>
        internal static bool IsSafeRelativeScriptName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            if (name.IndexOf('\0') >= 0 || name.IndexOf(':') >= 0) return false;
            if (Path.IsPathRooted(name) || name.StartsWith("\\", StringComparison.Ordinal) || name.StartsWith("/", StringComparison.Ordinal)) return false;
            foreach (var part in name.Split('\\', '/'))
                if (part == "..") return false;

            return true;
        }

        internal static string Describe(string prefix, IEnumerable<string> references)
        {
            var list = references.Distinct().ToList();
            if (list.Count == 0) return null;
            var shown = list.Take(5).ToList();
            var text = prefix + " " + string.Join(", ", shown);
            if (list.Count > shown.Count) text += " and " + (list.Count - shown.Count).ToString(CultureInfo.InvariantCulture) + " more";
            return text;
        }

        internal static string MapLabel(MapInfo map)
        {
            if (map == null) return "?";
            var title = string.IsNullOrWhiteSpace(map.Title) ? map.FileName : map.Title;
            return map.Index.ToString(CultureInfo.InvariantCulture) + " (" + (title ?? string.Empty) + ")";
        }
    }
}

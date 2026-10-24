using Server.WebAdmin.GameData;

namespace Server.WebAdmin.LiveDb
{
    /// <summary>How a saved change of one column reaches the running world (Envir.Main).</summary>
    internal enum LiveClass
    {
        /// <summary>Saved to Server.MirDB; Main loads it at the next world start (also: anything unclassified).</summary>
        RestartOnly = 0,

        /// <summary>The value is copied onto Main's record and the server reads it the next time it uses it.</summary>
        ReadThrough = 1,

        /// <summary>The value is copied onto Main's record and a refresh hook runs (monster sweep, drop reload, ...).</summary>
        Hooked = 2,
    }

    /// <summary>When a change becomes visible (schema <c>liveTiming</c>, header tooltips).</summary>
    internal static class LiveTiming
    {
        public const string Now = "now";
        public const string Hook = "hook";
        public const string Spawn = "spawn";
        public const string Clients = "clients";
        public const string Restart = "restart";
    }

    /// <summary>The classification of one column.</summary>
    internal sealed class LiveColumnRule
    {
        public LiveColumnRule(LiveClass liveClass, string timing, string note = null)
        {
            Class = liveClass;
            Timing = timing;
            Note = note;
        }

        public LiveClass Class { get; }
        public string Timing { get; }
        public string Note { get; }

        public bool IsLive => Class != LiveClass.RestartOnly;

        internal static readonly LiveColumnRule Restart = new LiveColumnRule(LiveClass.RestartOnly, LiveTiming.Restart);
    }

    /// <summary>The classification of one game-data table (see the LiveDb plan, "Classification").</summary>
    internal sealed class LiveTableRule
    {
        public string Endpoint;

        /// <summary>
        /// The live step of THIS build applies the table's live columns (monsters, magic; items since Batch B; the world tables since
        /// Batch C — everything except the conquest children). Others save only.
        /// </summary>
        public bool LiveInThisBuild;

        /// <summary>New rows are cloned into Main (only when <see cref="LiveInThisBuild"/>).</summary>
        public bool InsertLive;

        /// <summary>Deletes reach Main (Batch C: respawns, safe zones, movements, mine zones, game shop).</summary>
        public bool DeleteLive;

        /// <summary>The batch of the plan that switches the table on (A, B, C), for the schema and the docs.</summary>
        public string PlannedBatch;

        public readonly Dictionary<string, LiveColumnRule> Columns = new Dictionary<string, LiveColumnRule>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Prefix rules ("Stat_" → every flattened stat column) checked after the exact names.</summary>
        public readonly List<KeyValuePair<string, LiveColumnRule>> Prefixes = new List<KeyValuePair<string, LiveColumnRule>>();

        public LiveColumnRule Planned(string column)
        {
            if (string.IsNullOrEmpty(column)) return null;
            if (Columns.TryGetValue(column, out var rule)) return rule;
            for (var i = 0; i < Prefixes.Count; i++)
                if (column.StartsWith(Prefixes[i].Key, StringComparison.OrdinalIgnoreCase)) return Prefixes[i].Value;
            return null;
        }
    }

    /// <summary>
    /// Per endpoint and column: <see cref="LiveClass"/> + <see cref="LiveTiming"/>. The full plan table is written down for every table, so
    /// later batches only flip <see cref="LiveTableRule.LiveInThisBuild"/> and add hooks. The EFFECTIVE class of a column is its planned
    /// class while its table is live in this build, otherwise <see cref="LiveClass.RestartOnly"/>; anything unclassified is RestartOnly.
    /// <c>LiveDbTests.Classification_CoversEveryColumn</c> fails when a new column has no explicit entry.
    /// </summary>
    internal static class LiveDbClassification
    {
        private static readonly Lazy<Dictionary<string, LiveTableRule>> Rules = new Lazy<Dictionary<string, LiveTableRule>>(Build);

        public static IReadOnlyCollection<LiveTableRule> Tables => Rules.Value.Values;

        public static LiveTableRule Table(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return null;
            return Rules.Value.TryGetValue(endpoint.Trim(), out var t) ? t : null;
        }

        /// <summary>The plan's class for a column (null when unclassified).</summary>
        public static LiveColumnRule Planned(string endpoint, string column) => Table(endpoint)?.Planned(column);

        /// <summary>What this build does with a change of the column: the planned rule on a live table, else RestartOnly.</summary>
        public static LiveColumnRule Effective(string endpoint, string column)
        {
            var table = Table(endpoint);
            if (table == null || !table.LiveInThisBuild) return LiveColumnRule.Restart;
            return table.Planned(column) ?? LiveColumnRule.Restart;
        }

        public static bool IsLiveTable(string endpoint) => Table(endpoint)?.LiveInThisBuild == true;

        public static bool InsertLive(string endpoint)
        {
            var t = Table(endpoint);
            return t != null && t.LiveInThisBuild && t.InsertLive;
        }

        public static bool DeleteLive(string endpoint)
        {
            var t = Table(endpoint);
            return t != null && t.LiveInThisBuild && t.DeleteLive;
        }

        public static IReadOnlyList<string> LiveTables()
        {
            var list = new List<string>();
            foreach (var t in Rules.Value.Values)
                if (t.LiveInThisBuild) list.Add(t.Endpoint);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        // ================================================================== the plan table

        private static LiveColumnRule R(string note = null) => new LiveColumnRule(LiveClass.RestartOnly, LiveTiming.Restart, note);
        private static LiveColumnRule Read(string timing = LiveTiming.Now, string note = null) => new LiveColumnRule(LiveClass.ReadThrough, timing, note);
        private static LiveColumnRule Hook(string timing, string note) => new LiveColumnRule(LiveClass.Hooked, timing, note);

        private static void Set(LiveTableRule t, LiveColumnRule rule, params string[] columns)
        {
            foreach (var c in columns) t.Columns[c] = rule;
        }

        private static Dictionary<string, LiveTableRule> Build()
        {
            var all = new List<LiveTableRule>
            {
                Items(), Monsters(), Magic(), Npcs(), Quests(), Maps(), SafeZones(), Respawns(), Movements(), MineZones(), GameShop(),
                Conquests(), Dragon(),
            };

            foreach (var child in new[] { "conquestguards", "conquestgates", "conquestwalls", "conquestsieges", "conquestflags", "conquestcontrolpoints" })
            {
                // Plan: conquest children are restart-only.
                var t = new LiveTableRule { Endpoint = child, PlannedBatch = "-" };
                Set(t, R("conquest children are created at world start"), "ConquestIndex", "LocalIndex", "Location_X", "Location_Y", "MobIndex", "Name",
                    "RepairCost", "FileName");
                all.Add(t);
            }

            // Quest system V2 (UserData-and-Quests-Plan.md, Part B phase 1): every new table and column is RestartOnly while legacy QuestInfo
            // is the runtime (phases 1-2). Phase 3 moves them to the quest-replace hook; the position-keyed children are never applied row by row.
            foreach (var quest in QuestV2Tables)
            {
                var t = new LiveTableRule { Endpoint = quest, PlannedBatch = "-" };
                t.Prefixes.Add(new KeyValuePair<string, LiveColumnRule>(string.Empty, QuestV2Rule)); // every column of the table
                all.Add(t);
            }

            var map = new Dictionary<string, LiveTableRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in all) map[t.Endpoint] = t;
            return map;
        }

        private static LiveTableRule Items()
        {
            // Batch B: live. Every changed item is also pushed to connected clients that hold it (S.GameDataChanged).
            var t = new LiveTableRule { Endpoint = "items", PlannedBatch = "B", LiveInThisBuild = true, InsertLive = true };
            Set(t, R("drops, recipes and quests refer to items by name"), "Name");
            Set(t, R("item behaviour is chosen by type"), "Type");
            Set(t, Read(note: "read when the item is used, bought, sold or equipped; pushed to clients"), "Price", "RequiredType", "RequiredClass",
                "RequiredGender", "RequiredAmount", "Bind", "Durability", "StackSize", "Grade", "NeedIdentify", "ShowGroupPickup",
                "GlobalDropNotify", "ClassBased", "LevelBased", "CanMine", "CanAwakening", "Slots");
            Set(t, Hook(LiveTiming.Hook, "players and heroes holding the item recalculate their stats"), "Weight", "Light", "Set", "Shape", "Unique",
                "CanFastRun");
            Set(t, Hook(LiveTiming.Hook, "the random-stats table is re-resolved"), "RandomStatsId");
            Set(t, Hook(LiveTiming.Hook, "the start-item list is rebuilt"), "StartItem");
            Set(t, Hook(LiveTiming.Clients, "sent to connected clients"), "Image", "ToolTip");
            Set(t, Hook(LiveTiming.Hook, "holders recalculate their looks; sent to connected clients"), "Effect");
            t.Prefixes.Add(new KeyValuePair<string, LiveColumnRule>(GameDataColumns.StatPrefix,
                Hook(LiveTiming.Hook, "players and heroes holding the item recalculate their stats")));
            return t;
        }

        private static LiveTableRule Monsters()
        {
            var t = new LiveTableRule { Endpoint = "monsters", PlannedBatch = "A", LiveInThisBuild = true, InsertLive = true };
            Set(t, R("drop files and settings refer to monsters by name"), "Name");
            Set(t, R("spawned monsters and clients keep the old value until restart"), "Image", "AI", "Effect", "CoolEye", "Light", "Undead", "AutoRev");
            Set(t, Read(note: "read when the monster uses it (spawn, search, death)"), "Level", "Experience", "ViewRange", "CanTame", "CanPush",
                "CanRecall", "IsBoss");
            Set(t, Hook(LiveTiming.Hook, "spawned monsters of this kind are refreshed a few hundred per tick; HP is clamped to the new maximum"),
                "AttackSpeed", "MoveSpeed");
            Set(t, Hook(LiveTiming.Hook, "the monster's drop file is reloaded"), "DropPath");
            t.Prefixes.Add(new KeyValuePair<string, LiveColumnRule>(GameDataColumns.StatPrefix,
                Hook(LiveTiming.Hook, "spawned monsters of this kind are refreshed a few hundred per tick; HP is clamped to the new maximum")));
            return t;
        }

        private static LiveTableRule Magic()
        {
            var t = new LiveTableRule { Endpoint = "magic", PlannedBatch = "A", LiveInThisBuild = true };
            Set(t, R("the key; spells are created by the server"), "Spell");
            Set(t, Hook(LiveTiming.Now, "copied onto the running world's spell (players' skills use it on the next cast)"), "Name", "BaseCost",
                "LevelCost", "Icon", "Level1", "Level2", "Level3", "Need1", "Need2", "Need3", "DelayBase", "DelayReduction", "PowerBase", "PowerBonus",
                "MPowerBase", "MPowerBonus", "MultiplierBase", "MultiplierBonus", "Range");
            return t;
        }

        private static LiveTableRule Npcs()
        {
            // Batch C: live. Deletes stay restart-only (an NPC object, its script and goods are bound at world start).
            var t = new LiveTableRule { Endpoint = "npcs", PlannedBatch = "C", LiveInThisBuild = true, InsertLive = true };
            Set(t, R("conquest NPCs are bound at world start"), "Conquest", "ConquestVisible");
            Set(t, Hook(LiveTiming.Now, "visibility is re-checked for nearby players"), "TimeVisible", "HourStart", "MinuteStart", "HourEnd",
                "MinuteEnd", "MinLev", "MaxLev", "DayofWeek", "ClassRequired", "FlagNeeded");
            Set(t, Read(note: "read when the NPC is used; pushed to clients"), "Rate", "CanTeleportTo", "CollectQuestIndexes", "FinishQuestIndexes");
            Set(t, Hook(LiveTiming.Clients, "re-broadcast to nearby players; pushed to clients"), "Image", "Name");
            Set(t, Hook(LiveTiming.Hook, "the NPC script is rebound (its quests and speech come from the new script)"), "FileName");
            Set(t, Hook(LiveTiming.Hook, "the NPC is moved (the target must be a walkable cell of a loaded map)"), "MapIndex", "Location_X", "Location_Y");
            Set(t, Hook(LiveTiming.Clients, "map info is invalidated for clients"), "ShowOnBigMap", "BigMapIcon");
            return t;
        }

        /// <summary>The quest V2 tables (all RestartOnly in phases 1-2).</summary>
        internal static readonly string[] QuestV2Tables =
        {
            "queststeps", "questobjectives", "questbranches", "questendpoints", "questrewards", "questvars", "conditions", "texts", "storylines",
            "questgroups", "questgroupmembers", "questpools", "questpoolmembers",
        };

        private static readonly LiveColumnRule QuestV2Rule = new LiveColumnRule(LiveClass.RestartOnly, LiveTiming.Restart,
            "quest V2 definitions are not used at runtime yet (legacy quests run); the phase 3 quest-replace hook applies them");

        private static LiveTableRule Quests()
        {
            // Batch C: live. Inserts / deletes stay restart-only (NPC scripts bind quests at world start).
            var t = new LiveTableRule { Endpoint = "quests", PlannedBatch = "C", LiveInThisBuild = true };
            Set(t, R("quest kind is fixed per quest"), "Type");
            Set(t, Read(LiveTiming.Clients, "requirements and messages, pushed to clients"), "Name", "Group", "RequiredMinLevel", "RequiredMaxLevel",
                "RequiredQuest", "RequiredClass", "GotoMessage", "KillMessage", "ItemMessage", "FlagMessage", "TimeLimitInSeconds");
            Set(t, Hook(LiveTiming.Hook, "the quest file is reloaded (refused when its kill / item / flag tasks change shape)"), "FileName");
            Set(t, QuestV2Rule, GameData.GameDataRegistry.QuestV2Columns.ToArray());
            return t;
        }

        private static LiveTableRule Maps()
        {
            // Batch C: live. Inserts / deletes stay restart-only (map files are loaded at world start).
            var t = new LiveTableRule { Endpoint = "maps", PlannedBatch = "C", LiveInThisBuild = true };
            Set(t, R("the map file is loaded at world start"), "FileName");
            Set(t, R("guild territory maps are bound at world start"), "GTIndex", "GT");
            Set(t, Hook(LiveTiming.Hook, "the mine is rebuilt (mine state resets)"), "MineIndex");
            Set(t, Hook(LiveTiming.Spawn, "sent to clients when they next enter the map; the big-map info is invalidated for clients"), "Title", "BigMap");
            Set(t, Read(LiveTiming.Spawn, "sent to clients when they next enter the map"), "MiniMap", "Music", "Fire", "Lightning");
            Set(t, Read(LiveTiming.Clients, "sent at once to the players on the map (S.MapAtmosphere), to others when they enter it"), "Light",
                "MapDarkLight", "WeatherParticles");
            Set(t, Read(note: "map flag read through the map's info"), "NoTeleport", "NoReconnect", "NoRandom", "NoEscape", "NoRecall", "NoDrug",
                "NoPosition", "NoFight", "NoThrowItem", "NoDropPlayer", "NoDropMonster", "NoNames", "NoMount", "NeedBridle", "Fight", "NeedHole",
                "NoTownTeleport", "NoReincarnation", "NoExperience", "NoGroup", "NoPets", "NoIntelligentCreatures", "NoHero", "RequiredGroup",
                "FireWallLimit", "RequiredGroupSize", "FireWallCount", "NoReconnectMap", "FireDamage", "LightningDamage");
            return t;
        }

        private static LiveTableRule SafeZones()
        {
            var t = new LiveTableRule { Endpoint = "mapsafezones", PlannedBatch = "C", LiveInThisBuild = true, InsertLive = true, DeleteLive = true };
            Set(t, R("parent key"), "MapIndex");
            Set(t, Hook(LiveTiming.Hook, "the zone's spell objects, start points and safe-zone flags are rebuilt"), "Location_X", "Location_Y", "Size",
                "StartPoint");
            return t;
        }

        private static LiveTableRule Respawns()
        {
            var t = new LiveTableRule { Endpoint = "maprespawns", PlannedBatch = "C", LiveInThisBuild = true, InsertLive = true, DeleteLive = true };
            Set(t, R("parent key"), "MapIndex");
            Set(t, Read(LiveTiming.Spawn, "read at the next respawn"), "Count", "Delay", "RandomDelay", "Direction");
            Set(t, Hook(LiveTiming.Spawn, "the saved-respawn bookkeeping is updated"), "SaveRespawnTime", "RespawnTicks");
            Set(t, Hook(LiveTiming.Hook, "the respawn cache is rebuilt (spawned monsters stay)"), "MonsterIndex", "Location_X", "Location_Y", "RoutePath",
                "Spread");
            return t;
        }

        private static LiveTableRule Movements()
        {
            var t = new LiveTableRule { Endpoint = "mapmovements", PlannedBatch = "C", LiveInThisBuild = true, InsertLive = true, DeleteLive = true };
            Set(t, R("parent key"), "MapIndex");
            Set(t, Read(note: "read when a player steps on the cell"), "NeedHole", "NeedMove", "ConquestIndex", "Destination_X", "Destination_Y");
            Set(t, Hook(LiveTiming.Clients, "read when a player steps on the cell; map info is invalidated for clients"), "DestinationMapIndex",
                "Source_X", "Source_Y", "ShowOnBigMap", "Icon");
            return t;
        }

        private static LiveTableRule MineZones()
        {
            var t = new LiveTableRule { Endpoint = "mapminezones", PlannedBatch = "C", LiveInThisBuild = true, InsertLive = true, DeleteLive = true };
            Set(t, R("parent key"), "MapIndex");
            Set(t, Hook(LiveTiming.Hook, "the map's mine is rebuilt (mine state resets)"), "Mine", "Location_X", "Location_Y", "Size");
            return t;
        }

        private static LiveTableRule GameShop()
        {
            var t = new LiveTableRule { Endpoint = "gameshop", PlannedBatch = "C", LiveInThisBuild = true, InsertLive = true, DeleteLive = true };
            Set(t, Read(LiveTiming.Clients, "read when the shop is opened or an item is bought; pushed to clients"), "GoldPrice",
                "CreditPrice", "Count", "Class", "Category", "Stock", "iStock", "Deal", "TopItem", "Date", "CanBuyGold", "CanBuyCredit",
                "DealGoldPrice", "DealCreditPrice");
            Set(t, Hook(LiveTiming.Clients, "bound to the running world's item; pushed to clients"), "ItemIndex");
            return t;
        }

        private static LiveTableRule Conquests()
        {
            // Batch C: live for the schedule / area fields; a row of a conquest whose war is on is saved only (applied after a restart).
            var t = new LiveTableRule { Endpoint = "conquests", PlannedBatch = "C", LiveInThisBuild = true };
            Set(t, R("conquest maps are bound at world start"), "MapIndex", "PalaceIndex", "ExtraMaps", "FullMap");
            Set(t, R("child counters"), "GuardIndex", "GateIndex", "WallIndex", "SiegeIndex", "FlagIndex", "ControlPointIndex");
            Set(t, Read(note: "schedule and area; refused while a war is on"), "Name", "Location_X", "Location_Y", "Size", "StartHour", "WarLength",
                "Type", "Game", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday", "KingLocation_X", "KingLocation_Y",
                "KingSize");
            return t;
        }

        private static LiveTableRule Dragon()
        {
            var t = new LiveTableRule { Endpoint = "dragon", PlannedBatch = "C", LiveInThisBuild = true };
            Set(t, R("the dragon is created at world start"), "Enabled", "MapFileName", "MonsterName", "BodyName", "Location_X", "Location_Y");
            Set(t, Hook(LiveTiming.Hook, "the dragon's drop area is recomputed"), "DropAreaTop_X", "DropAreaTop_Y", "DropAreaBottom_X", "DropAreaBottom_Y");
            t.Prefixes.Add(new KeyValuePair<string, LiveColumnRule>("Exps_", Read(note: "read when the dragon gains experience")));
            return t;
        }
    }
}

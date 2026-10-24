using System.Drawing;
using System.Globalization;
using System.Text.Json;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirObjects;
using S = ServerPackets;

namespace Server.WebAdmin
{
    // ======================================================================================================================
    // Catalog (pure data, published by GET /api/admin/gm/caps)
    // ======================================================================================================================

    /// <summary>A choice for a select-type GM argument.</summary>
    public sealed class GmOption
    {
        public string Value { get; set; }
        public string Label { get; set; }
    }

    /// <summary>One argument of a GM verb.</summary>
    public sealed class GmArgSpec
    {
        /// <summary>JSON body member name.</summary>
        public string Name { get; set; }

        public string Label { get; set; }

        /// <summary>int | text | longtext | map | item | monster | spell | conquest | duration | guild | select (<see cref="Options"/>).</summary>
        public string Type { get; set; }

        public bool Required { get; set; }

        public long? Min { get; set; }

        public long? Max { get; set; }

        /// <summary>Lookup name for pickers (players, maps, items, monsters).</summary>
        public string Lookup { get; set; }

        public string Placeholder { get; set; }

        public object Default { get; set; }

        public List<GmOption> Options { get; set; }

        /// <summary>When set, the UI shows a button with this label under the field that fills in <see cref="Max"/>.</summary>
        public string MaxButton { get; set; }
    }

    /// <summary>One GM verb.</summary>
    public sealed class GmVerbSpec
    {
        public string Action { get; set; }
        public string Label { get; set; }

        /// <summary>Moderation | Movement | Grants | Character | World | Mass.</summary>
        public string Group { get; set; }

        /// <summary>"character" (needs a target) or "world".</summary>
        public string Target { get; set; }

        /// <summary>The target character must be online (409 otherwise).</summary>
        public bool NeedsOnline { get; set; }

        /// <summary>The verb also works while the world is stopped (offline character edit).</summary>
        public bool WorksOffline { get; set; }

        /// <summary>Destructive / disruptive (the UI styles the button as danger).</summary>
        public bool Danger { get; set; }

        public string Description { get; set; }

        public List<GmArgSpec> Args { get; set; } = new List<GmArgSpec>();
    }

    public static class GmCatalog
    {
        public const string Moderation = "Moderation";
        public const string Movement = "Movement";
        public const string Grants = "Grants";
        public const string CharacterGroup = "Character";
        public const string World = "World";
        public const string Mass = "Mass actions";

        public static readonly IReadOnlyList<string> Groups = new[] { Moderation, Movement, Grants, CharacterGroup, World, Mass };

        /// <summary>Monster AIs that belong to conquests (siege gate, gates, archer, wall) — never spawned or cleared by the web GM.</summary>
        public static readonly IReadOnlySet<int> ConquestAIs = new HashSet<int> { 72, 73, 80, 81, 82 };

        public const long MaxCurrency = uint.MaxValue;
        public const int MaxItemCount = 10000;
        public const int MaxMassItemCount = 1000;
        public const int MaxSpawnCount = 50000;

        /// <summary>The spawnmob spread that means "anywhere walkable on the map" (X/Y are then optional).</summary>
        public const int MapWideSpread = 1000;
        public const int MaxChatBanMinutes = 10 * 365 * 24 * 60;
        public const int MaxMessageLength = 500;

        private static readonly Dictionary<string, GmVerbSpec> ByAction;

        public static readonly IReadOnlyList<GmVerbSpec> Verbs = Build();

        static GmCatalog()
        {
            ByAction = new Dictionary<string, GmVerbSpec>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in Verbs) ByAction[v.Action] = v;
        }

        public static GmVerbSpec Find(string action)
        {
            if (string.IsNullOrWhiteSpace(action)) return null;
            return ByAction.TryGetValue(action.Trim(), out var v) ? v : null;
        }

        private static GmArgSpec Int(string name, string label, bool required, long min, long max, object def = null, string placeholder = null)
            => new GmArgSpec { Name = name, Label = label, Type = "int", Required = required, Min = min, Max = max, Default = def, Placeholder = placeholder };

        private static GmArgSpec MapArg(bool required) => new GmArgSpec
        {
            Name = "map", Label = "Map (file name, title or #index)", Type = "map", Required = required, Lookup = "maps", Placeholder = "e.g. 0 or Bichon Province",
        };

        private static GmArgSpec InstanceArg() => Int("instance", "Instance (instanced maps)", false, 1, 1000, null, "1");

        private static List<GmOption> SpellOptions()
        {
            return Enum.GetValues(typeof(Spell)).Cast<Spell>()
                .Where(s => s != Spell.None)
                .Select(s => new GmOption { Value = s.ToString(), Label = s.ToString() + " (" + (int)s + ")" })
                .ToList();
        }

        private static List<GmOption> MassTeleportOptions() => new List<GmOption>
        {
            new GmOption { Value = "all", Label = "Everyone online" },
            new GmOption { Value = "gms", Label = "GMs only" },
            new GmOption { Value = "players", Label = "Non-GM players" },
            new GmOption { Value = "map", Label = "Players already on the target map" },
        };

        private static List<GmOption> MassGrantOptions() => new List<GmOption>
        {
            new GmOption { Value = "all", Label = "Everyone online" },
            new GmOption { Value = "players", Label = "Non-GM players" },
            new GmOption { Value = "gms", Label = "GMs only" },
            new GmOption { Value = "map", Label = "Players on a map" },
        };

        /// <summary>The scope arguments of a mass grant (who, then the verb's own arguments, then map/instance).</summary>
        private static List<GmArgSpec> WithScope(params GmArgSpec[] args)
        {
            var list = new List<GmArgSpec>
            {
                new GmArgSpec { Name = "who", Label = "Who", Type = "select", Required = true, Default = "all", Options = MassGrantOptions() },
            };
            list.AddRange(args);
            list.Add(new GmArgSpec
            {
                Name = "map", Label = "Map (optional; required for \"Players on a map\")", Type = "map", Required = false, Lookup = "maps", Placeholder = "any map",
            });
            list.Add(InstanceArg());
            return list;
        }

        private static IReadOnlyList<GmVerbSpec> Build()
        {
            var list = new List<GmVerbSpec>
            {
                // ---- Moderation
                new GmVerbSpec
                {
                    Action = "kick", Label = "Kick", Group = Moderation, Target = "character", NeedsOnline = true, Danger = true,
                    Description = "Disconnect the player (the client shows \"kicked by an administrator\").",
                },
                new GmVerbSpec
                {
                    Action = "kill", Label = "Kill", Group = Moderation, Target = "character", NeedsOnline = true, Danger = true,
                    Description = "Kill the player (players in GM god mode are refused).",
                },
                new GmVerbSpec
                {
                    Action = "chatban", Label = "Chat ban…", Group = Moderation, Target = "character", WorksOffline = true, Danger = true,
                    Description = "Block the character from chatting until the ban expires (admin accounts are refused).",
                    Args = new List<GmArgSpec>
                    {
                        new GmArgSpec { Name = "minutes", Label = "Duration", Type = "duration", Required = true, Min = 1, Max = MaxChatBanMinutes, Default = 60 },
                    },
                },
                new GmVerbSpec
                {
                    Action = "unchatban", Label = "Lift chat ban", Group = Moderation, Target = "character", WorksOffline = true,
                    Description = "Allow the character to chat again.",
                },
                new GmVerbSpec
                {
                    Action = "message", Label = "Message…", Group = Moderation, Target = "character", NeedsOnline = true,
                    Description = "Send an announcement-style message to this player only.",
                    Args = new List<GmArgSpec>
                    {
                        new GmArgSpec { Name = "text", Label = "Message", Type = "longtext", Required = true, Max = MaxMessageLength },
                    },
                },

                // ---- Movement
                new GmVerbSpec
                {
                    Action = "teleport", Label = "Teleport…", Group = Movement, Target = "character", NeedsOnline = true,
                    Description = "Move the player to a map; a random walkable point when X/Y are left empty.",
                    Args = new List<GmArgSpec>
                    {
                        MapArg(true),
                        Int("x", "X", false, 0, 10000),
                        Int("y", "Y", false, 0, 10000),
                        InstanceArg(),
                    },
                },
                new GmVerbSpec
                {
                    Action = "teleportall", Label = "Mass teleport…", Group = Movement, Target = "world", Danger = true,
                    Description = "Move online players to one cell: everyone, GMs only, non-GM players, or the players already on the target map. Dead players are skipped.",
                    Args = new List<GmArgSpec>
                    {
                        new GmArgSpec { Name = "who", Label = "Who", Type = "select", Required = true, Default = "all", Options = MassTeleportOptions() },
                        MapArg(true),
                        Int("x", "X", true, 0, 10000),
                        Int("y", "Y", true, 0, 10000),
                        Int("spread", "Spread (cells around X/Y)", false, 0, 20, 0),
                        InstanceArg(),
                    },
                },
                new GmVerbSpec
                {
                    Action = "randomteleport", Label = "Random-teleport players on a map…", Group = Movement, Target = "world", Danger = true,
                    Description = "Move every player on a map (GMs included) to their own random walkable spot on the same map, like a random teleport scroll. Dead players are skipped.",
                    Args = new List<GmArgSpec> { MapArg(true), InstanceArg() },
                },
                new GmVerbSpec
                {
                    Action = "safezone", Label = "Send to safe zone", Group = Movement, Target = "character", NeedsOnline = true,
                    Description = "Teleport the player to their bind point (the town they respawn in).",
                },

                // ---- Grants
                new GmVerbSpec
                {
                    Action = "givegold", Label = "Give gold…", Group = Grants, Target = "character", NeedsOnline = true,
                    Description = "Add gold to the account (capped at the 4,294,967,295 maximum).",
                    Args = new List<GmArgSpec> { Int("amount", "Gold", true, 1, MaxCurrency) },
                },
                new GmVerbSpec
                {
                    Action = "givecredit", Label = "Give credit…", Group = Grants, Target = "character", NeedsOnline = true,
                    Description = "Add game shop credit to the account.",
                    Args = new List<GmArgSpec> { Int("amount", "Credit", true, 1, MaxCurrency) },
                },
                new GmVerbSpec
                {
                    Action = "giveitem", Label = "Give item…", Group = Grants, Target = "character", NeedsOnline = true,
                    Description = "Create items in the bag like @MAKE (split by stack size, marked GM-made). Stops when the bag is full.",
                    Args = new List<GmArgSpec>
                    {
                        new GmArgSpec { Name = "item", Label = "Item (index or name)", Type = "item", Required = true, Lookup = "items" },
                        Int("count", "Count", false, 1, MaxItemCount, 1),
                    },
                },
                new GmVerbSpec
                {
                    Action = "giveskill", Label = "Give skill…", Group = Grants, Target = "character", NeedsOnline = true,
                    Description = "Teach a spell (or change its level) like @GIVESKILL.",
                    Args = new List<GmArgSpec>
                    {
                        new GmArgSpec { Name = "spell", Label = "Spell", Type = "spell", Required = true, Options = SpellOptions() },
                        Int("level", "Level (0-3)", false, 0, 3, 0),
                    },
                },
                new GmVerbSpec
                {
                    Action = "giveallskills", Label = "Give all skills…", Group = Grants, Target = "character", NeedsOnline = true,
                    Description = "Teach every spell of the player's class that has a MagicInfo (the class's spell range plus any spell a skill book of that class teaches). Known spells below the level are raised; none are lowered.",
                    Args = new List<GmArgSpec> { Int("level", "Level (0-3)", false, 0, 3, 0) },
                },

                // ---- Character
                new GmVerbSpec
                {
                    Action = "setlevel", Label = "Set level…", Group = CharacterGroup, Target = "character", WorksOffline = true,
                    Description = "Online: the normal level-up path (stats and client refresh). Offline: edits the saved level.",
                    Args = new List<GmArgSpec> { Int("level", "Level", true, 1, ushort.MaxValue) },
                },
                new GmVerbSpec
                {
                    Action = "setpk", Label = "Set PK points…", Group = CharacterGroup, Target = "character", WorksOffline = true,
                    Description = "Set the PK points (name colour follows: 100+ yellow, 200+ red).",
                    Args = new List<GmArgSpec> { Int("points", "PK points", true, 0, 1000000) },
                },
                new GmVerbSpec
                {
                    Action = "revive", Label = "Revive", Group = CharacterGroup, Target = "character", NeedsOnline = true,
                    Description = "Revive a dead player at full health where they stand.",
                },
                new GmVerbSpec
                {
                    Action = "killpets", Label = "Kill pets", Group = CharacterGroup, Target = "character", NeedsOnline = true, Danger = true,
                    Description = "Kill every pet the player has summoned or tamed.",
                },
                new GmVerbSpec
                {
                    Action = "clearbuffs", Label = "Clear buffs", Group = CharacterGroup, Target = "character", NeedsOnline = true,
                    Description = "Remove the player's buffs (the GM buff is kept).",
                },

                // ---- World
                new GmVerbSpec
                {
                    Action = "spawnmob", Label = "Spawn monster…", Group = World, Target = "world",
                    Description = "Spawn monsters like @MOB (GM-made). Conquest guards/gates and intelligent creatures are refused. " +
                                  "Spread " + MapWideSpread + " (Map wide) scatters them over every walkable cell of the map, and X/Y are then not needed.",
                    Args = new List<GmArgSpec>
                    {
                        new GmArgSpec { Name = "monster", Label = "Monster (index or name)", Type = "monster", Required = true, Lookup = "monsters" },
                        MapArg(true),
                        Int("x", "X (not needed map wide)", false, 0, 10000),
                        Int("y", "Y (not needed map wide)", false, 0, 10000),
                        Int("count", "Count", false, 1, MaxSpawnCount, 1),
                        new GmArgSpec
                        {
                            Name = "spread", Label = "Spread (cells around X/Y; " + MapWideSpread + " = map wide)", Type = "int", Required = false,
                            Min = 0, Max = MapWideSpread, Default = 0, MaxButton = "Map wide",
                        },
                        InstanceArg(),
                    },
                },
                new GmVerbSpec
                {
                    Action = "clearmobs", Label = "Clear monsters…", Group = World, Target = "world", Danger = true,
                    Description = "Kill every wild monster on a map like @CLEARMOB (pets and conquest structures are spared).",
                    Args = new List<GmArgSpec> { MapArg(true), InstanceArg() },
                },
                new GmVerbSpec
                {
                    Action = "startconquest", Label = "Start conquest war…", Group = World, Target = "world", Danger = true,
                    Description = "Force a conquest war to start now (like @STARTCONQUEST). Optionally name the attacking guild.",
                    Args = new List<GmArgSpec>
                    {
                        new GmArgSpec { Name = "conquestIndex", Label = "Conquest", Type = "conquest", Required = true, Min = 1, Max = int.MaxValue },
                        new GmArgSpec { Name = "guild", Label = "Attacking guild (optional)", Type = "guild", Required = false, Max = 40 },
                    },
                },
                new GmVerbSpec
                {
                    Action = "stopconquest", Label = "Stop conquest war…", Group = World, Target = "world", Danger = true,
                    Description = "End a running conquest war now.",
                    Args = new List<GmArgSpec>
                    {
                        new GmArgSpec { Name = "conquestIndex", Label = "Conquest", Type = "conquest", Required = true, Min = 1, Max = int.MaxValue },
                    },
                },

                // ---- Mass (online players by scope, dead ones included; a map narrows any scope)
                new GmVerbSpec
                {
                    Action = "massgold", Label = "Give gold to all…", Group = Mass, Target = "world", Danger = true,
                    Description = "Add gold to every matching online player's account (capped at the maximum per account).",
                    Args = WithScope(Int("amount", "Gold each", true, 1, MaxCurrency)),
                },
                new GmVerbSpec
                {
                    Action = "masscredit", Label = "Give credit to all…", Group = Mass, Target = "world", Danger = true,
                    Description = "Add game shop credit to every matching online player's account.",
                    Args = WithScope(Int("amount", "Credit each", true, 1, MaxCurrency)),
                },
                new GmVerbSpec
                {
                    Action = "massitem", Label = "Give item to all…", Group = Mass, Target = "world", Danger = true,
                    Description = "Create items in every matching player's bag like @MAKE (GM-made). A player with a full bag gets what fits.",
                    Args = WithScope(
                        new GmArgSpec { Name = "item", Label = "Item (index or name)", Type = "item", Required = true, Lookup = "items" },
                        Int("count", "Count each", false, 1, MaxMassItemCount, 1)),
                },
                new GmVerbSpec
                {
                    Action = "massskill", Label = "Give skill to all…", Group = Mass, Target = "world", Danger = true,
                    Description = "Teach a spell to every matching player whose class can learn it (others are skipped). Known spells below the level are raised; none are lowered.",
                    Args = WithScope(
                        new GmArgSpec { Name = "spell", Label = "Spell", Type = "spell", Required = true, Options = SpellOptions() },
                        Int("level", "Level (0-3)", false, 0, 3, 0)),
                },
                new GmVerbSpec
                {
                    Action = "massallskills", Label = "Give all skills to all…", Group = Mass, Target = "world", Danger = true,
                    Description = "Teach every matching player all the spells of their own class. Known spells below the level are raised; none are lowered.",
                    Args = WithScope(Int("level", "Level (0-3)", false, 0, 3, 0)),
                },
                new GmVerbSpec
                {
                    Action = "masslevel", Label = "Set level of all…", Group = Mass, Target = "world", Danger = true,
                    Description = "Set every matching online player's level through the normal level-up path (stats and client refresh), like Set level. Players already at that level are skipped.",
                    Args = WithScope(Int("level", "Level", true, 1, ushort.MaxValue)),
                },
            };

            return list;
        }
    }

    // ======================================================================================================================
    // Argument reading (pure)
    // ======================================================================================================================

    /// <summary>Lenient typed access to the JSON body of a GM action; throws 400 <see cref="WebAdminException"/> on bad input.</summary>
    public sealed class GmArgReader
    {
        private readonly JsonElement _body;

        public GmArgReader(JsonElement body)
        {
            _body = body;
        }

        public JsonElement Body => _body;

        /// <summary>The member exists and is not null / an empty string.</summary>
        public bool Has(string name)
        {
            if (!_body.TryGetPropertyIgnoreCase(name, out var v)) return false;
            switch (v.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return false;
                case JsonValueKind.String:
                    return !string.IsNullOrWhiteSpace(v.GetString());
                default:
                    return true;
            }
        }

        public string String(string name, bool required, int maxLength, string label = null)
        {
            if (!Has(name))
            {
                if (required) throw new WebAdminException(400, (label ?? name) + " is required");
                return null;
            }

            var s = _body.GetString(name);
            if (s == null) throw new WebAdminException(400, (label ?? name) + " must be text");

            s = s.Trim();
            if (maxLength > 0 && s.Length > maxLength)
                throw new WebAdminException(400, (label ?? name) + " is too long (max " + maxLength + " characters)");

            return s;
        }

        public long? Long(string name, bool required, long min, long max, string label = null)
        {
            if (!Has(name))
            {
                if (required) throw new WebAdminException(400, (label ?? name) + " is required");
                return null;
            }

            _body.TryGetPropertyIgnoreCase(name, out var v);
            long value;

            if (v.ValueKind == JsonValueKind.Number)
            {
                if (!v.TryGetInt64(out value))
                {
                    if (v.TryGetDouble(out var d) && Math.Abs(d - Math.Round(d)) < 1e-9 && d >= long.MinValue && d <= long.MaxValue) value = (long)Math.Round(d);
                    else throw new WebAdminException(400, (label ?? name) + " must be a whole number");
                }
            }
            else if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString().Trim().Replace(",", string.Empty).Replace("_", string.Empty);
                if (!long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
                    throw new WebAdminException(400, (label ?? name) + " must be a whole number");
            }
            else
            {
                throw new WebAdminException(400, (label ?? name) + " must be a whole number");
            }

            if (value < min || value > max)
                throw new WebAdminException(400, (label ?? name) + " must be between " + min.ToString("N0", CultureInfo.InvariantCulture) + " and " + max.ToString("N0", CultureInfo.InvariantCulture));

            return value;
        }

        public bool Bool(string name, bool fallback) => _body.GetBool(name, fallback);
    }

    /// <summary>Pure GM rules (unit-tested).</summary>
    public static class GmRules
    {
        /// <summary>Spell by name (case-insensitive) or number; never <see cref="Spell.None"/>.</summary>
        public static bool TryParseSpell(string text, out Spell spell)
        {
            spell = Spell.None;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                if (n <= 0 || n > byte.MaxValue || !Enum.IsDefined(typeof(Spell), (byte)n)) return false;
                spell = (Spell)(byte)n;
                return spell != Spell.None;
            }

            if (!Enum.TryParse(text, true, out Spell parsed) || !Enum.IsDefined(typeof(Spell), parsed) || parsed == Spell.None) return false;
            spell = parsed;
            return true;
        }

        /// <summary>Stack sizes for creating <paramref name="count"/> items of a given stack size (like @MAKE).</summary>
        public static List<ushort> SplitStacks(int count, ushort stackSize)
        {
            var result = new List<ushort>();
            if (count <= 0) return result;
            if (stackSize < 1) stackSize = 1;

            var remaining = count;
            while (remaining > 0)
            {
                var n = Math.Min(remaining, (int)stackSize);
                result.Add((ushort)n);
                remaining -= n;
            }

            return result;
        }

        /// <summary>The amount that can still be added without overflowing a uint balance.</summary>
        public static uint ClampGain(uint current, long amount)
        {
            if (amount <= 0) return 0;
            var room = (long)uint.MaxValue - current;
            return (uint)Math.Min(room, amount);
        }

        /// <summary>
        /// Chat-ban expiry from the body: <c>indefinite:true</c> (100 years), <c>until</c> (ISO-8601; a value without a zone is UTC,
        /// like Envir.Now), or <c>minutes</c>. <paramref name="now"/> is Envir.Now (UTC based). 400 when missing/invalid/in the past.
        /// </summary>
        public static DateTime ChatBanUntil(GmArgReader args, DateTime now)
        {
            if (args.Bool("indefinite", false) || args.Bool("permanent", false))
                return now.AddYears(100);

            var until = args.String("until", false, 64);
            if (until != null)
            {
                if (!DateTime.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var u))
                    throw new WebAdminException(400, "until must be an ISO-8601 date/time");
                if (u <= now) throw new WebAdminException(400, "until must be in the future");
                if (u > now.AddMinutes(GmCatalog.MaxChatBanMinutes)) throw new WebAdminException(400, "until is too far in the future (use indefinite)");
                return u;
            }

            var minutes = args.Long("minutes", true, 1, GmCatalog.MaxChatBanMinutes, "Duration (minutes)");
            return now.AddMinutes(minutes.Value);
        }

        /// <summary>Human text for a duration in minutes ("2 hours", "1 day 3 hours").</summary>
        public static string DescribeMinutes(double minutes)
        {
            if (minutes >= 60 * 24 * 365 * 50) return "indefinitely";
            var total = (long)Math.Round(minutes);
            if (total < 1) total = 1;

            var days = total / (60 * 24);
            var hours = total % (60 * 24) / 60;
            var mins = total % 60;
            var parts = new List<string>();
            if (days > 0) parts.Add(days + (days == 1 ? " day" : " days"));
            if (hours > 0) parts.Add(hours + (hours == 1 ? " hour" : " hours"));
            if (mins > 0 && days == 0) parts.Add(mins + (mins == 1 ? " minute" : " minutes"));
            return string.Join(" ", parts);
        }

        /// <summary>
        /// Splits a map reference. "#12" is an explicit MapInfo.Index (name null). Anything else is a file name or title
        /// (Crystal's map file names are numeric, e.g. "0" or "0100"); when it is also a plain integer, the index is
        /// returned too, as a fallback the resolver uses only if no file name or title matches.
        /// </summary>
        public static (int? index, string name) ParseMapRef(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return (null, null);
            text = text.Trim();
            if (text.StartsWith("#", StringComparison.Ordinal))
            {
                var rest = text.Substring(1).Trim();
                if (int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var explicitIndex)) return (explicitIndex, null);
                return (null, text);
            }
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var i)) return (i, text);
            return (null, text);
        }

        /// <summary>The <c>who</c> values of the teleportall verb.</summary>
        public static readonly IReadOnlyList<string> MassTeleportScopes = new[] { "all", "gms", "players", "map" };

        /// <summary>Whether a player is moved by teleportall with scope <paramref name="who"/> (one of <see cref="MassTeleportScopes"/>).</summary>
        public static bool MassTeleportIncludes(string who, bool isGm, bool onTargetMap)
        {
            switch (who)
            {
                case "all": return true;
                case "gms": return isGm;
                case "players": return !isGm;
                case "map": return onTargetMap;
                default: return false;
            }
        }

        /// <summary>The <c>who</c> values of the mass grant verbs.</summary>
        public static readonly IReadOnlyList<string> MassGrantScopes = new[] { "all", "players", "gms", "map" };

        /// <summary>
        /// Whether a mass grant reaches a player: <paramref name="who"/> is one of <see cref="MassGrantScopes"/>; when a map is
        /// given (<paramref name="hasMap"/>) only players on it count, whatever the scope ("map" needs one).
        /// </summary>
        public static bool MassGrantIncludes(string who, bool isGm, bool hasMap, bool onMap)
        {
            if (hasMap && !onMap) return false;
            switch (who)
            {
                case "all": return true;
                case "players": return !isGm;
                case "gms": return isGm;
                case "map": return hasMap;
                default: return false;
            }
        }

        /// <summary>
        /// The class a spell belongs to by its <see cref="Spell"/> band (Warrior 1-30, Wizard 31-60, Taoist 61-90, Assassin 91-120,
        /// Archer 121-150); null for the custom (151+) and map-event spells.
        /// </summary>
        public static MirClass? SpellBandClass(Spell spell)
        {
            var n = (int)spell;
            if (n >= 1 && n <= 30) return MirClass.Warrior;
            if (n >= 31 && n <= 60) return MirClass.Wizard;
            if (n >= 61 && n <= 90) return MirClass.Taoist;
            if (n >= 91 && n <= 120) return MirClass.Assassin;
            if (n >= 121 && n <= 150) return MirClass.Archer;
            return null;
        }

        /// <summary>
        /// The spells a class can learn: those in its band, plus any spell a skill book (<see cref="ItemType.Book"/>, Shape = spell)
        /// for that class teaches. Only spells in <paramref name="known"/> (the MagicInfo list) count. Sorted by spell number.
        /// </summary>
        public static List<Spell> SpellsForClass(MirClass cls, IEnumerable<Spell> known, IEnumerable<(Spell spell, RequiredClass classes)> books)
        {
            var bit = (RequiredClass)(1 << (int)cls);
            var fromBooks = new HashSet<Spell>();
            if (books != null)
                foreach (var b in books)
                    if ((b.classes & bit) != 0) fromBooks.Add(b.spell);

            return known
                .Where(s => s != Spell.None && (SpellBandClass(s) == cls || fromBooks.Contains(s)))
                .Distinct()
                .OrderBy(s => (int)s)
                .ToList();
        }

        /// <summary>Case- and space-insensitive name key ("Wooden Sword" == "woodensword").</summary>
        public static string NameKey(string s) => (s ?? string.Empty).Replace(" ", string.Empty).Trim().ToLowerInvariant();
    }

    // ======================================================================================================================
    // Execution
    // ======================================================================================================================

    /// <summary>What a GM action reports back (message for the toast, data for the UI, audit target/details).</summary>
    public sealed class GmResult
    {
        public string Message { get; set; }
        public object Data { get; set; }
        public string Target { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>The parsed, validated arguments of one GM action (built off the game thread).</summary>
    internal sealed class GmPlan
    {
        public GmVerbSpec Verb;
        public string Admin;
        public string Reason;
        public string CharacterName;
        public int CharacterIndex;
        public GmArgReader Args;

        public long Amount;
        public int Level;
        public int Count = 1;
        public int Spread;
        public int Points;
        public string Text;
        public string MapRef;
        public int? X, Y;
        public int Instance = 1;
        public string ItemRef;
        public string MonsterRef;
        public Spell Spell;
        public byte SpellLevel;
        public int ConquestIndex;
        public string Guild;
        public string Who;
    }

    /// <summary>
    /// Implements POST /api/admin/gm/action with the same primitives the in-game @-commands and PlayerInfoForm use. All live work
    /// runs on the game thread (offline-capable verbs inline under AccountLock while the world is stopped).
    /// </summary>
    public static class GmActions
    {
        public static async Task<GmResult> ExecuteAsync(WebAdminRequest req, JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object) throw new WebAdminException(400, "Expected a JSON object body");

            var args = new GmArgReader(body);
            var action = (args.String("action", true, 40, "action") ?? string.Empty).ToLowerInvariant();
            var verb = GmCatalog.Find(action) ?? throw new WebAdminException(400, "Unknown GM action: " + action);

            var plan = Prepare(verb, args);
            plan.Admin = req?.AdminName ?? "system";

            // A 504 after the action started may still apply it: audited with an unknown outcome (codebase review 2026-10-24 #11).
            var target = plan.CharacterName ?? plan.Who ?? plan.Guild ?? "world";
            var request = new { action = verb.Action, plan.CharacterName, plan.Amount, plan.Level, plan.Count, plan.ItemRef, plan.MonsterRef, plan.MapRef, plan.Text, plan.Reason };
            GmResult result;
            if (verb.WorksOffline)
                result = await Routes.AccountRoutes.AuditIfTimedOutAsync(req, "gm." + verb.Action, target, request, () => WebAdminGame.RunOrInlineAsync(() => Run(plan))).ConfigureAwait(false);
            else
                result = await Routes.AccountRoutes.AuditIfTimedOutAsync(req, "gm." + verb.Action, target, request, () => WebAdminGame.RunAsync(() => Run(plan))).ConfigureAwait(false);

            result.Details["action"] = verb.Action;
            WebAdminAudit.Record(req, "gm." + verb.Action, result.Target, result.Details, plan.Reason);

            try
            {
                MessageQueue.Instance.Enqueue("[Web admin] " + plan.Admin + ": " + result.Message + (string.IsNullOrEmpty(plan.Reason) ? string.Empty : " (reason: " + plan.Reason + ")"));
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "GM action log failed");
            }

            return result;
        }

        /// <summary>Validates and converts the body for a verb (pure; 400 on bad input).</summary>
        internal static GmPlan Prepare(GmVerbSpec verb, GmArgReader args)
        {
            var plan = new GmPlan { Verb = verb, Args = args };
            plan.Reason = args.String("reason", false, 500, "Reason");

            if (verb.Target == "character")
            {
                plan.CharacterName = args.String("character", false, 40, "character");
                plan.CharacterIndex = (int)(args.Long("characterIndex", false, 1, int.MaxValue, "characterIndex") ?? 0);
                if (plan.CharacterIndex <= 0 && string.IsNullOrEmpty(plan.CharacterName))
                    throw new WebAdminException(400, "A target character (character or characterIndex) is required");
            }

            switch (verb.Action)
            {
                case "message":
                    plan.Text = args.String("text", true, GmCatalog.MaxMessageLength, "Message");
                    break;
                case "teleport":
                    plan.MapRef = args.String("map", true, 100, "Map");
                    plan.X = (int?)args.Long("x", false, 0, 10000, "X");
                    plan.Y = (int?)args.Long("y", false, 0, 10000, "Y");
                    if (plan.X.HasValue != plan.Y.HasValue) throw new WebAdminException(400, "Give both X and Y, or neither");
                    plan.Instance = (int)(args.Long("instance", false, 1, 1000, "Instance") ?? 1);
                    break;
                case "givegold":
                case "givecredit":
                    plan.Amount = args.Long("amount", true, 1, GmCatalog.MaxCurrency, "Amount").Value;
                    break;
                case "giveitem":
                    plan.ItemRef = args.String("item", true, 100, "Item");
                    plan.Count = (int)(args.Long("count", false, 1, GmCatalog.MaxItemCount, "Count") ?? 1);
                    break;
                case "giveskill":
                {
                    var s = args.String("spell", true, 60, "Spell");
                    if (!GmRules.TryParseSpell(s, out plan.Spell)) throw new WebAdminException(400, "Unknown spell: " + s);
                    plan.SpellLevel = (byte)(args.Long("level", false, 0, 3, "Level") ?? 0);
                    break;
                }
                case "giveallskills":
                case "massallskills":
                    plan.SpellLevel = (byte)(args.Long("level", false, 0, 3, "Level") ?? 0);
                    break;
                case "massgold":
                case "masscredit":
                    plan.Amount = args.Long("amount", true, 1, GmCatalog.MaxCurrency, "Amount").Value;
                    break;
                case "massitem":
                    plan.ItemRef = args.String("item", true, 100, "Item");
                    plan.Count = (int)(args.Long("count", false, 1, GmCatalog.MaxMassItemCount, "Count") ?? 1);
                    break;
                case "massskill":
                {
                    var s = args.String("spell", true, 60, "Spell");
                    if (!GmRules.TryParseSpell(s, out plan.Spell)) throw new WebAdminException(400, "Unknown spell: " + s);
                    plan.SpellLevel = (byte)(args.Long("level", false, 0, 3, "Level") ?? 0);
                    break;
                }
                case "setlevel":
                case "masslevel":
                    plan.Level = (int)args.Long("level", true, 1, ushort.MaxValue, "Level").Value;
                    break;
                case "setpk":
                    plan.Points = (int)args.Long("points", true, 0, 1000000, "PK points").Value;
                    break;
                case "chatban":
                    // validated now (400 early); the expiry is computed on the game thread against Envir.Now
                    GmRules.ChatBanUntil(args, DateTime.UtcNow);
                    break;
                case "spawnmob":
                    plan.MonsterRef = args.String("monster", true, 100, "Monster");
                    plan.MapRef = args.String("map", true, 100, "Map");
                    plan.Count = (int)(args.Long("count", false, 1, GmCatalog.MaxSpawnCount, "Count") ?? 1);
                    plan.Spread = (int)(args.Long("spread", false, 0, GmCatalog.MapWideSpread, "Spread") ?? 0);
                    plan.X = (int?)args.Long("x", false, 0, 10000, "X");
                    plan.Y = (int?)args.Long("y", false, 0, 10000, "Y");
                    if (plan.X.HasValue != plan.Y.HasValue) throw new WebAdminException(400, "Give both X and Y, or neither");
                    if (!plan.X.HasValue && plan.Spread < GmCatalog.MapWideSpread) throw new WebAdminException(400, "X and Y are required (or use Map wide)");
                    plan.Instance = (int)(args.Long("instance", false, 1, 1000, "Instance") ?? 1);
                    break;
                case "teleportall":
                    plan.Who = (args.String("who", false, 20, "Who") ?? "all").ToLowerInvariant();
                    if (!GmRules.MassTeleportScopes.Contains(plan.Who))
                        throw new WebAdminException(400, "Who must be one of: " + string.Join(", ", GmRules.MassTeleportScopes));
                    plan.MapRef = args.String("map", true, 100, "Map");
                    plan.X = (int)args.Long("x", true, 0, 10000, "X").Value;
                    plan.Y = (int)args.Long("y", true, 0, 10000, "Y").Value;
                    plan.Spread = (int)(args.Long("spread", false, 0, 20, "Spread") ?? 0);
                    plan.Instance = (int)(args.Long("instance", false, 1, 1000, "Instance") ?? 1);
                    break;
                case "randomteleport":
                case "clearmobs":
                    plan.MapRef = args.String("map", true, 100, "Map");
                    plan.Instance = (int)(args.Long("instance", false, 1, 1000, "Instance") ?? 1);
                    break;
                case "startconquest":
                    plan.ConquestIndex = (int)args.Long("conquestIndex", true, 1, int.MaxValue, "Conquest").Value;
                    plan.Guild = args.String("guild", false, 40, "Guild");
                    break;
                case "stopconquest":
                    plan.ConquestIndex = (int)args.Long("conquestIndex", true, 1, int.MaxValue, "Conquest").Value;
                    break;
            }

            if (verb.Group == GmCatalog.Mass)
            {
                plan.Who = (args.String("who", false, 20, "Who") ?? "all").ToLowerInvariant();
                if (!GmRules.MassGrantScopes.Contains(plan.Who))
                    throw new WebAdminException(400, "Who must be one of: " + string.Join(", ", GmRules.MassGrantScopes));
                plan.MapRef = args.String("map", false, 100, "Map");
                plan.Instance = (int)(args.Long("instance", false, 1, 1000, "Instance") ?? 1);
                if (plan.Who == "map" && plan.MapRef == null) throw new WebAdminException(400, "Map is required for \"Players on a map\"");
            }

            return plan;
        }

        // ------------------------------------------------------------------------------------------------- game thread

        private static Envir Main => Envir.Main;

        /// <summary>
        /// The game clock for ban expiries. Envir.Now (UtcNow-based) only advances inside WorkLoop, so it is frozen at the
        /// moment the world stopped; while the world is not running use DateTime.UtcNow, which Envir.Now tracks once the
        /// loop runs again.
        /// </summary>
        internal static DateTime GameNow => WebAdminGame.IsRunning ? Envir.Main.Now : DateTime.UtcNow;

        private static GmResult Run(GmPlan plan)
        {
            if (plan.Verb.Target == "world") return RunWorld(plan);

            var info = FindCharacter(plan.CharacterName, plan.CharacterIndex)
                       ?? throw new WebAdminException(404, "Character not found: " + (plan.CharacterName ?? ("#" + plan.CharacterIndex)));

            var player = OnlinePlayer(info);
            if (plan.Verb.NeedsOnline && player == null)
                throw new WebAdminException(409, info.Name + " is offline", new { characterIndex = info.Index, online = false });

            var result = new GmResult { Target = info.Name + " (#" + info.Index + ")" };
            result.Details["character"] = info.Name;
            result.Details["characterIndex"] = info.Index;
            result.Details["online"] = player != null;
            if (info.AccountInfo != null) result.Details["account"] = info.AccountInfo.AccountID;

            switch (plan.Verb.Action)
            {
                case "kick": Kick(plan, player, result); break;
                case "kill": Kill(player, result); break;
                case "chatban": ChatBan(plan, info, player, result); break;
                case "unchatban": UnChatBan(info, player, result); break;
                case "message": Message(plan, player, result); break;
                case "teleport": Teleport(plan, player, result); break;
                case "safezone": SafeZone(player, result); break;
                case "givegold": GiveGold(plan, player, result); break;
                case "givecredit": GiveCredit(plan, player, result); break;
                case "giveitem": GiveItem(plan, player, result); break;
                case "giveskill": GiveSkill(plan, player, result); break;
                case "giveallskills": GiveAllSkills(plan, player, result); break;
                case "setlevel": SetLevel(plan, info, player, result); break;
                case "setpk": SetPk(plan, info, player, result); break;
                case "revive": Revive(player, result); break;
                case "killpets": KillPets(player, result); break;
                case "clearbuffs": ClearBuffs(player, result); break;
                default: throw new WebAdminException(400, "Unknown GM action: " + plan.Verb.Action);
            }

            if (plan.Verb.WorksOffline && player == null) PersistOfflineEdit();

            result.Data ??= new { characterIndex = info.Index, name = info.Name, online = player != null };
            return result;
        }

        /// <summary>Exact (case-insensitive) name match, preferring a non-deleted character; or by index.</summary>
        internal static CharacterInfo FindCharacter(string name, int index)
        {
            var list = Main.CharacterList;

            if (index > 0)
            {
                for (var i = 0; i < list.Count; i++)
                    if (list[i].Index == index) return list[i];
                return null;
            }

            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();

            CharacterInfo deleted = null;
            for (var i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (!string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (!c.Deleted) return c;
                deleted ??= c;
            }

            return deleted;
        }

        internal static PlayerObject OnlinePlayer(CharacterInfo info)
        {
            if (info == null || !WebAdminGame.IsRunning) return null;
            // CharacterInfo.Player is set in StartGame and cleared in StopGame; fall back to the live list just in case.
            return info.Player ?? Main.GetPlayer((uint)info.Index);
        }

        /// <summary>While the world is stopped nothing saves accounts, and the next start reloads them from disk: save the edit now.</summary>
        private static void PersistOfflineEdit()
        {
            if (WebAdminGame.IsRunning) return; // the periodic save handles it

            var envir = Main;
            if (envir.AccountList == null || envir.AccountList.Count == 0) return; // never loaded: refuse to overwrite the file with nothing

            envir.SaveAccounts();
        }

        private static void Kick(GmPlan plan, PlayerObject player, GmResult result)
        {
            var connection = player.Connection ?? throw new WebAdminException(409, player.Name + " has no connection to close");

            if (!string.IsNullOrEmpty(plan.Reason))
                player.ReceiveChat("You have been kicked by an administrator: " + plan.Reason, ChatType.System);

            connection.SendDisconnect(4);
            result.Message = "Kicked " + player.Name + ".";
        }

        private static void Kill(PlayerObject player, GmResult result)
        {
            if (player.Dead) throw new WebAdminException(409, player.Name + " is already dead");
            if (player.GMNeverDie) throw new WebAdminException(409, player.Name + " is in GM god mode (@SUPERMAN) and cannot die");

            player.Die();
            result.Message = player.Dead ? "Killed " + player.Name + "." : player.Name + " survived (a revival item absorbed the kill).";
            result.Details["died"] = player.Dead;
        }

        private static void ChatBan(GmPlan plan, CharacterInfo info, PlayerObject player, GmResult result)
        {
            if (info.AccountInfo != null && info.AccountInfo.AdminAccount)
                throw new WebAdminException(409, info.Name + " belongs to an admin account and cannot be chat banned");

            var now = GameNow;
            var until = GmRules.ChatBanUntil(plan.Args, now);

            info.ChatBanned = true;
            info.ChatBanExpiryDate = until;

            var span = GmRules.DescribeMinutes((until - now).TotalMinutes);
            if (player != null)
                player.ReceiveChat("You have been banned from chatting " + (span == "indefinitely" ? span : "for " + span) +
                                   (string.IsNullOrEmpty(plan.Reason) ? "." : ": " + plan.Reason), ChatType.System);

            result.Message = "Chat banned " + info.Name + " " + (span == "indefinitely" ? span : "for " + span) + ".";
            result.Details["until"] = until;
            result.Data = new { characterIndex = info.Index, name = info.Name, online = player != null, chatBanned = true, chatBanUntil = until };
        }

        private static void UnChatBan(CharacterInfo info, PlayerObject player, GmResult result)
        {
            var was = info.ChatBanned && info.ChatBanExpiryDate > GameNow;
            info.ChatBanned = false;
            info.ChatBanExpiryDate = DateTime.MinValue;

            if (player != null && was) player.ReceiveChat("Your chat ban has been lifted.", ChatType.System);

            result.Message = was ? "Lifted the chat ban on " + info.Name + "." : info.Name + " was not chat banned.";
            result.Details["wasBanned"] = was;
        }

        private static void Message(GmPlan plan, PlayerObject player, GmResult result)
        {
            player.ReceiveChat(plan.Text, ChatType.Announcement);
            WebAdminChatLog.Append("admin", "[web] " + plan.Admin, plan.Text, player.Name);
            MessageQueue.Instance.EnqueueChat("[Web admin] " + plan.Admin + " -> " + player.Name + ": " + plan.Text);

            result.Message = "Message sent to " + player.Name + ".";
            result.Details["text"] = plan.Text;
        }

        private static void Teleport(GmPlan plan, PlayerObject player, GmResult result)
        {
            var map = ResolveMap(plan.MapRef, plan.Instance);
            bool ok;

            if (plan.X.HasValue)
            {
                var pt = new Point(plan.X.Value, plan.Y.Value);
                RequireWalkable(map, pt);
                ok = player.Teleport(map, pt);
            }
            else
            {
                ok = player.TeleportRandom(200, 0, map);
            }

            if (!ok) throw new WebAdminException(409, "Teleport failed (no free walkable point found on " + MapLabel(map) + ")");

            result.Message = "Teleported " + player.Name + " to " + MapLabel(map) + " (" + player.CurrentLocation.X + ", " + player.CurrentLocation.Y + ").";
            result.Details["map"] = map.Info.FileName;
            result.Details["mapIndex"] = map.Info.Index;
            result.Details["x"] = player.CurrentLocation.X;
            result.Details["y"] = player.CurrentLocation.Y;
            result.Data = new { name = player.Name, map = map.Info.Title, mapFile = map.Info.FileName, x = player.CurrentLocation.X, y = player.CurrentLocation.Y };
        }

        private static void SafeZone(PlayerObject player, GmResult result)
        {
            var info = player.Info;
            var map = Main.GetMap(info.BindMapIndex) ?? throw new WebAdminException(409, player.Name + "'s bind map (#" + info.BindMapIndex + ") is not loaded");

            if (!player.Teleport(map, info.BindLocation))
                throw new WebAdminException(409, "Teleport to the bind point failed");

            result.Message = "Sent " + player.Name + " to the safe zone at " + MapLabel(map) + " (" + info.BindLocation.X + ", " + info.BindLocation.Y + ").";
            result.Details["map"] = map.Info.FileName;
        }

        private static void GiveGold(GmPlan plan, PlayerObject player, GmResult result)
        {
            var account = player.Account ?? throw new WebAdminException(409, player.Name + " has no account loaded");
            var amount = GmRules.ClampGain(account.Gold, plan.Amount);
            if (amount == 0) throw new WebAdminException(409, player.Name + "'s account already holds the maximum gold");

            player.GainGold(amount);
            result.Message = "Gave " + amount.ToString("N0", CultureInfo.InvariantCulture) + " gold to " + player.Name + " (now " + account.Gold.ToString("N0", CultureInfo.InvariantCulture) + ").";
            result.Details["amount"] = amount;
            result.Details["balance"] = account.Gold;
        }

        private static void GiveCredit(GmPlan plan, PlayerObject player, GmResult result)
        {
            var account = player.Account ?? throw new WebAdminException(409, player.Name + " has no account loaded");
            var amount = GmRules.ClampGain(account.Credit, plan.Amount);
            if (amount == 0) throw new WebAdminException(409, player.Name + "'s account already holds the maximum credit");

            player.GainCredit(amount);
            result.Message = "Gave " + amount.ToString("N0", CultureInfo.InvariantCulture) + " credit to " + player.Name + " (now " + account.Credit.ToString("N0", CultureInfo.InvariantCulture) + ").";
            result.Details["amount"] = amount;
            result.Details["balance"] = account.Credit;
        }

        private static void GiveItem(GmPlan plan, PlayerObject player, GmResult result)
        {
            var info = ResolveItem(plan.ItemRef) ?? throw new WebAdminException(404, "Item not found: " + plan.ItemRef);

            var given = 0;
            foreach (var stack in GmRules.SplitStacks(plan.Count, info.StackSize))
            {
                var item = Main.CreateDropItem(info);
                if (item == null) break;

                item.GMMade = true;
                item.Count = stack;

                if (!player.CanGainItem(item)) break;

                player.GainItem(item);
                given += stack;
            }

            if (given == 0) throw new WebAdminException(409, player.Name + "'s bag has no room for " + info.FriendlyName);

            result.Message = "Gave " + given + " x " + info.FriendlyName + " to " + player.Name +
                             (given < plan.Count ? " (bag full: " + (plan.Count - given) + " not given)." : ".");
            result.Details["item"] = info.Name;
            result.Details["itemIndex"] = info.Index;
            result.Details["requested"] = plan.Count;
            result.Details["given"] = given;
            result.Data = new { name = player.Name, item = info.Name, itemIndex = info.Index, given, requested = plan.Count };
        }

        private static void GiveSkill(GmPlan plan, PlayerObject player, GmResult result)
        {
            var magicInfo = Main.MagicInfoList.FirstOrDefault(m => m.Spell == plan.Spell)
                            ?? throw new WebAdminException(404, "Spell " + plan.Spell + " has no MagicInfo in the database");

            var existing = player.Info.Magics.FirstOrDefault(e => e.Spell == plan.Spell);
            if (existing != null)
            {
                var old = existing.Level;
                existing.Level = plan.SpellLevel;
                player.Enqueue(new S.MagicLeveled { ObjectID = player.ObjectID, Spell = existing.Spell, Level = existing.Level, Experience = existing.Experience });
                player.RefreshStats();
                player.ReceiveChat(plan.Spell + " is now level " + plan.SpellLevel + ".", ChatType.Hint);
                result.Message = "Changed " + player.Name + "'s " + plan.Spell + " from level " + old + " to " + plan.SpellLevel + ".";
            }
            else
            {
                var magic = new UserMagic(plan.Spell) { Level = plan.SpellLevel };
                player.Info.Magics.Add(magic);
                player.SendMagicInfo(magic);
                player.RefreshStats();
                player.ReceiveChat("You have learned " + plan.Spell + " at level " + plan.SpellLevel + ".", ChatType.Hint);
                result.Message = "Taught " + player.Name + " " + plan.Spell + " at level " + plan.SpellLevel + ".";
            }

            result.Details["spell"] = plan.Spell.ToString();
            result.Details["level"] = plan.SpellLevel;
            result.Details["magic"] = magicInfo.Name;
        }

        private static void GiveAllSkills(GmPlan plan, PlayerObject player, GmResult result)
        {
            var spells = SpellsForClass(player.Class);
            if (spells.Count == 0) throw new WebAdminException(404, "No " + player.Class + " spell has a MagicInfo in the database");

            var (learned, raised) = TeachSpells(player, spells, plan.SpellLevel);

            result.Message = learned + raised == 0
                ? player.Name + " already knows every " + player.Class + " spell at level " + plan.SpellLevel + " or higher."
                : "Taught " + player.Name + " " + learned + " new " + player.Class + " spell(s)" + (raised > 0 ? " and raised " + raised : string.Empty) + " (level " + plan.SpellLevel + ").";
            result.Details["class"] = player.Class.ToString();
            result.Details["level"] = plan.SpellLevel;
            result.Details["spells"] = spells.Count;
            result.Details["learned"] = learned;
            result.Details["raised"] = raised;
            result.Data = new { name = player.Name, className = player.Class.ToString(), spells = spells.Count, learned, raised };
        }

        /// <summary>The spells <paramref name="cls"/> can learn that have a MagicInfo (its band plus its skill books).</summary>
        private static List<Spell> SpellsForClass(MirClass cls)
        {
            var books = Main.ItemInfoList
                .Where(i => i != null && i.Type == ItemType.Book)
                .Select(i => ((Spell)(byte)i.Shape, i.RequiredClass));
            return GmRules.SpellsForClass(cls, Main.MagicInfoList.Select(m => m.Spell), books);
        }

        /// <summary>
        /// Teaches the spells the player lacks at <paramref name="level"/> and raises known ones below it (never lowers), then one
        /// stat refresh and one chat line. Returns (learned, raised).
        /// </summary>
        private static (int learned, int raised) TeachSpells(PlayerObject player, IReadOnlyList<Spell> spells, byte level)
        {
            int learned = 0, raised = 0;

            foreach (var spell in spells)
            {
                var existing = player.Info.Magics.FirstOrDefault(e => e.Spell == spell);
                if (existing == null)
                {
                    var magic = new UserMagic(spell) { Level = level };
                    player.Info.Magics.Add(magic);
                    player.SendMagicInfo(magic);
                    learned++;
                }
                else if (existing.Level < level)
                {
                    existing.Level = level;
                    player.Enqueue(new S.MagicLeveled { ObjectID = player.ObjectID, Spell = existing.Spell, Level = existing.Level, Experience = existing.Experience });
                    raised++;
                }
            }

            if (learned + raised == 0) return (0, 0);

            player.RefreshStats();
            player.ReceiveChat(spells.Count == 1
                ? (learned == 1 ? "You have learned " : "An administrator raised ") + spells[0] + (learned == 1 ? " at level " : " to level ") + level + "."
                : "An administrator taught you " + learned + " new spell(s)" + (raised > 0 ? " and raised " + raised : string.Empty) + " (level " + level + ").", ChatType.Hint);

            return (learned, raised);
        }

        private static void SetLevel(GmPlan plan, CharacterInfo info, PlayerObject player, GmResult result)
        {
            var level = (ushort)plan.Level;

            if (player != null)
            {
                var old = player.Level;
                player.Level = level;
                player.LevelUp();
                result.Message = "Levelled " + player.Name + " from " + old + " to " + player.Level + ".";
                result.Details["from"] = old;
            }
            else
            {
                var old = info.Level;
                info.Level = level;
                result.Message = "Set offline character " + info.Name + "'s level from " + old + " to " + level + ".";
                result.Details["from"] = old;
            }

            result.Details["to"] = level;
        }

        private static void SetPk(GmPlan plan, CharacterInfo info, PlayerObject player, GmResult result)
        {
            int old;
            if (player != null)
            {
                old = player.PKPoints;
                player.PKPoints = plan.Points;
                player.RefreshNameColour();
            }
            else
            {
                old = info.PKPoints;
                info.PKPoints = plan.Points;
            }

            result.Message = "Set " + info.Name + "'s PK points from " + old + " to " + plan.Points + ".";
            result.Details["from"] = old;
            result.Details["to"] = plan.Points;
        }

        private static void Revive(PlayerObject player, GmResult result)
        {
            if (!player.Dead) throw new WebAdminException(409, player.Name + " is not dead");

            player.Revive(player.MaxHealth, true);
            result.Message = "Revived " + player.Name + ".";
        }

        private static void KillPets(PlayerObject player, GmResult result)
        {
            var pets = player.Pets.ToList();
            var killed = 0;
            foreach (var pet in pets)
            {
                if (pet == null || pet.Dead) continue;
                pet.Die();
                killed++;
            }

            result.Message = killed == 0 ? player.Name + " has no living pets." : "Killed " + killed + " pet(s) of " + player.Name + ".";
            result.Details["killed"] = killed;
        }

        private static void ClearBuffs(PlayerObject player, GmResult result)
        {
            var cleared = 0;
            foreach (var buff in player.Buffs)
            {
                if (buff == null || buff.Type == BuffType.GameMaster || buff.FlagForRemoval) continue;
                buff.FlagForRemoval = true;
                buff.ExpireTime = 0;
                cleared++;
            }

            result.Message = cleared == 0 ? player.Name + " has no buffs to clear." : "Cleared " + cleared + " buff(s) on " + player.Name + ".";
            result.Details["cleared"] = cleared;
        }

        // ------------------------------------------------------------------------------------------------- world verbs

        private static GmResult RunWorld(GmPlan plan)
        {
            var result = new GmResult();

            switch (plan.Verb.Action)
            {
                case "spawnmob": SpawnMob(plan, result); break;
                case "teleportall": TeleportAll(plan, result); break;
                case "randomteleport": RandomTeleport(plan, result); break;
                case "clearmobs": ClearMobs(plan, result); break;
                case "startconquest": StartConquest(plan, result); break;
                case "stopconquest": StopConquest(plan, result); break;
                case "massgold":
                case "masscredit":
                case "massitem":
                case "massskill":
                case "massallskills":
                case "masslevel":
                    MassGrant(plan, result);
                    break;
                default: throw new WebAdminException(400, "Unknown GM action: " + plan.Verb.Action);
            }

            return result;
        }

        private static void TeleportAll(GmPlan plan, GmResult result)
        {
            var map = ResolveMap(plan.MapRef, plan.Instance);
            var centre = new Point(plan.X.Value, plan.Y.Value);
            RequireWalkable(map, centre);

            // A copy: the moves run on later ticks, and a teleport can run map scripts.
            var players = new List<PlayerObject>();
            var dead = 0;
            var names = new List<string>();

            foreach (var p in Main.Players)
            {
                if (p?.Info == null || p.CurrentMap == null) continue;
                if (!GmRules.MassTeleportIncludes(plan.Who, p.IsGM, p.CurrentMap == map)) continue;
                if (p.Dead) { dead++; continue; }

                players.Add(p);
                if (names.Count < 50) names.Add(p.Name);
            }

            if (players.Count == 0 && dead == 0)
                throw new WebAdminException(409, "No online player matches (" + plan.Who + ")");

            // Spread over ticks like randomteleport (MassTeleportPerTick at a time, starting this tick). A player who logged out or
            // died meanwhile is skipped; "map" scope also skips one who left the map.
            var spread = plan.Spread;
            var who = plan.Who;
            var next = 0;
            Main.AddAdminTickJob(() =>
            {
                var end = Math.Min(next + MassTeleportPerTick, players.Count);
                for (; next < end; next++)
                {
                    var p = players[next];
                    if (p.Node == null || p.Info == null || p.Dead || p.CurrentMap == null) continue;
                    if (who == "map" && p.CurrentMap != map) continue;

                    var pt = centre;
                    if (spread > 0)
                    {
                        for (var attempt = 0; attempt < 20; attempt++)
                        {
                            var c = new Point(centre.X + Main.Random.Next(-spread, spread + 1), centre.Y + Main.Random.Next(-spread, spread + 1));
                            if (map.ValidPoint(c)) { pt = c; break; }
                        }
                    }

                    p.Teleport(map, pt);
                }

                return next >= players.Count;
            });

            var ticks = (players.Count + MassTeleportPerTick - 1) / MassTeleportPerTick;
            result.Target = MapLabel(map);
            result.Message = "Teleporting " + players.Count + (players.Count == 1 ? " player" : " players") + " to " + MapLabel(map) + " (" + centre.X + ", " + centre.Y + ")" +
                             (ticks > 1 ? ", " + MassTeleportPerTick + " per tick over " + ticks + " ticks" : string.Empty) +
                             (dead > 0 ? "; " + dead + " dead skipped" : string.Empty) + ".";
            result.Details["who"] = plan.Who;
            result.Details["map"] = map.Info.FileName;
            result.Details["mapIndex"] = map.Info.Index;
            result.Details["x"] = centre.X;
            result.Details["y"] = centre.Y;
            result.Details["queued"] = players.Count;
            result.Details["perTick"] = MassTeleportPerTick;
            result.Details["dead"] = dead;
            result.Details["players"] = names;
            result.Data = new { map = map.Info.Title, mapFile = map.Info.FileName, queued = players.Count, perTick = MassTeleportPerTick, dead };
        }

        /// <summary>Players moved per game tick by the teleportall verb.</summary>
        private const int MassTeleportPerTick = 100;

        private static void RandomTeleport(GmPlan plan, GmResult result)
        {
            var map = ResolveMap(plan.MapRef, plan.Instance);
            if (map.WalkableCells == null || map.WalkableCells.Count == 0)
                throw new WebAdminException(409, MapLabel(map) + " has no walkable cells");

            // A copy: each teleport removes the player from this map's list (and may add them back).
            var players = new List<PlayerObject>();
            var dead = 0;
            var names = new List<string>();

            foreach (var p in map.Players)
            {
                if (p?.Info == null) continue;
                if (p.Dead) { dead++; continue; }

                players.Add(p);
                if (names.Count < 50) names.Add(p.Name);
            }

            if (players.Count == 0 && dead == 0)
                throw new WebAdminException(409, "No player is on " + MapLabel(map));

            // Moving thousands of players in one tick stalls the loop long enough to time clients out, so the moves are spread over
            // ticks, RandomTeleportPerTick at a time, starting this tick. A player who logged out, died or left the map meanwhile is skipped.
            var next = 0;
            Main.AddAdminTickJob(() =>
            {
                var end = Math.Min(next + RandomTeleportPerTick, players.Count);
                for (; next < end; next++)
                {
                    var p = players[next];
                    if (p.Node == null || p.Info == null || p.Dead || p.CurrentMap != map) continue;
                    p.TeleportRandom(200, 0, map);
                }

                return next >= players.Count;
            });

            var ticks = (players.Count + RandomTeleportPerTick - 1) / RandomTeleportPerTick;
            result.Target = MapLabel(map);
            result.Message = "Randomly teleporting " + players.Count + (players.Count == 1 ? " player" : " players") + " on " + MapLabel(map) +
                             (ticks > 1 ? ", " + RandomTeleportPerTick + " per tick over " + ticks + " ticks" : string.Empty) +
                             (dead > 0 ? "; " + dead + " dead skipped" : string.Empty) + ".";
            result.Details["map"] = map.Info.FileName;
            result.Details["mapIndex"] = map.Info.Index;
            result.Details["queued"] = players.Count;
            result.Details["perTick"] = RandomTeleportPerTick;
            result.Details["dead"] = dead;
            result.Details["players"] = names;
            result.Data = new { map = map.Info.Title, mapFile = map.Info.FileName, queued = players.Count, perTick = RandomTeleportPerTick, dead };
        }

        /// <summary>Players moved per game tick by the randomteleport verb.</summary>
        private const int RandomTeleportPerTick = 100;

        private static void SpawnMob(GmPlan plan, GmResult result)
        {
            var info = ResolveMonster(plan.MonsterRef) ?? throw new WebAdminException(404, "Monster not found: " + plan.MonsterRef);
            if (GmCatalog.ConquestAIs.Contains((int)info.AI))
                throw new WebAdminException(422, info.Name + " is a conquest structure and cannot be spawned by hand");

            var map = ResolveMap(plan.MapRef, plan.Instance);
            var mapWide = plan.Spread >= GmCatalog.MapWideSpread;
            var centre = plan.X.HasValue ? new Point(plan.X.Value, plan.Y.Value) : Point.Empty;
            if (plan.X.HasValue && !mapWide) RequireWalkable(map, centre);

            List<Point> cells = null;
            if (mapWide)
            {
                map.GetWalkableCells();
                cells = map.WalkableCells;
                if (cells == null || cells.Count == 0) throw new WebAdminException(409, MapLabel(map) + " has no walkable cells");
            }

            var spawned = 0;
            for (var i = 0; i < plan.Count; i++)
            {
                var monster = MonsterObject.GetMonster(info) ?? throw new WebAdminException(422, info.Name + " cannot be spawned (unknown AI " + info.AI + ")");
                if (monster is IntelligentCreatureObject)
                    throw new WebAdminException(422, info.Name + " is an intelligent creature (pet) and cannot be spawned");

                monster.GMMade = true;

                var placed = false;
                if (mapWide)
                {
                    for (var attempt = 0; attempt < 20 && !placed; attempt++)
                    {
                        var pt = cells[Main.Random.Next(cells.Count)];
                        if (map.ValidPoint(pt)) placed = monster.Spawn(map, pt);
                    }
                }
                else if (plan.Spread <= 0)
                {
                    placed = monster.Spawn(map, centre);
                }
                else
                {
                    for (var attempt = 0; attempt < 20 && !placed; attempt++)
                    {
                        var pt = new Point(centre.X + Main.Random.Next(-plan.Spread, plan.Spread + 1), centre.Y + Main.Random.Next(-plan.Spread, plan.Spread + 1));
                        if (map.ValidPoint(pt)) placed = monster.Spawn(map, pt);
                    }

                    if (!placed) placed = monster.Spawn(map, centre);
                }

                if (placed) spawned++;
            }

            result.Target = MapLabel(map);
            result.Message = "Spawned " + spawned + " x " + info.Name + " on " + MapLabel(map) +
                             (mapWide ? " (map wide)" : " at (" + centre.X + ", " + centre.Y + ")") +
                             (spawned < plan.Count ? "; " + (plan.Count - spawned) + " found no free cell" : string.Empty) + ".";
            result.Details["monster"] = info.Name;
            result.Details["monsterIndex"] = info.Index;
            result.Details["map"] = map.Info.FileName;
            if (mapWide) result.Details["mapWide"] = true;
            else
            {
                result.Details["x"] = centre.X;
                result.Details["y"] = centre.Y;
            }
            result.Details["requested"] = plan.Count;
            result.Details["spawned"] = spawned;
            result.Data = new { monster = info.Name, map = map.Info.Title, mapFile = map.Info.FileName, spawned };
        }

        private static void ClearMobs(GmPlan plan, GmResult result)
        {
            var map = ResolveMap(plan.MapRef, plan.Instance);

            var victims = new List<MonsterObject>();
            var seen = new HashSet<MapObject>();
            if (map.CellData != null)
            {
                foreach (var cell in map.ObjectCells())
                {
                    var objects = cell.Objects;
                    for (var i = 0; i < objects.Count; i++)
                    {
                        if (!(objects[i] is MonsterObject mob) || mob.Race != ObjectType.Monster || mob.Dead) continue;
                        if (mob.Master != null) continue; // pets
                        if (mob.Info != null && GmCatalog.ConquestAIs.Contains((int)mob.Info.AI)) continue;
                        if (seen.Add(mob)) victims.Add(mob);
                    }
                }
            }

            foreach (var mob in victims)
            {
                if (!mob.Dead) mob.Die();
            }

            result.Target = MapLabel(map);
            result.Message = "Killed " + victims.Count + " monster(s) on " + MapLabel(map) + ".";
            result.Details["map"] = map.Info.FileName;
            result.Details["killed"] = victims.Count;
            result.Data = new { map = map.Info.Title, mapFile = map.Info.FileName, killed = victims.Count };
        }

        private static void MassGrant(GmPlan plan, GmResult result)
        {
            var action = plan.Verb.Action;
            var map = plan.MapRef != null ? ResolveMap(plan.MapRef, plan.Instance) : null;

            ItemInfo item = null;
            if (action == "massitem")
                item = ResolveItem(plan.ItemRef) ?? throw new WebAdminException(404, "Item not found: " + plan.ItemRef);

            if (action == "massskill" && !Main.MagicInfoList.Any(m => m.Spell == plan.Spell))
                throw new WebAdminException(404, "Spell " + plan.Spell + " has no MagicInfo in the database");

            // Per class, built once: the spells massallskills teaches, and (massskill) whether the class can learn the spell.
            var classSpells = new Dictionary<MirClass, List<Spell>>();
            List<Spell> SpellsOf(MirClass cls)
            {
                if (!classSpells.TryGetValue(cls, out var list)) classSpells[cls] = list = SpellsForClass(cls);
                return list;
            }
            var single = new[] { plan.Spell };

            // Granting to thousands of players in one tick stalls the loop (bag adds, level-ups and stat refreshes each send packets),
            // so the matching players are copied now and served on later ticks, MassGrantPerTick at a time, starting this tick, like
            // teleportall. A player who logged out meanwhile is skipped, and with a map one who left it.
            var players = new List<PlayerObject>();
            var names = new List<string>();

            foreach (var p in Main.Players)
            {
                if (p?.Info == null || p.CurrentMap == null) continue;
                if (!GmRules.MassGrantIncludes(plan.Who, p.IsGM, map != null, p.CurrentMap == map)) continue;

                players.Add(p);
                if (names.Count < 50) names.Add(p.Name);
            }

            var scope = plan.Who + (map != null ? " on " + MapLabel(map) : string.Empty);
            if (players.Count == 0) throw new WebAdminException(409, "No online player matches (" + scope + ")");

            int next = 0, granted = 0, skipped = 0, partial = 0, gone = 0;
            long total = 0;

            long Grant(PlayerObject p)
            {
                long given = 0;
                switch (action)
                {
                    case "massgold":
                    {
                        var amount = p.Account == null ? 0u : GmRules.ClampGain(p.Account.Gold, plan.Amount);
                        if (amount > 0) p.GainGold(amount);
                        given = amount;
                        break;
                    }
                    case "masscredit":
                    {
                        var amount = p.Account == null ? 0u : GmRules.ClampGain(p.Account.Credit, plan.Amount);
                        if (amount > 0) p.GainCredit(amount);
                        given = amount;
                        break;
                    }
                    case "massitem":
                        foreach (var stack in GmRules.SplitStacks(plan.Count, item.StackSize))
                        {
                            var made = Main.CreateDropItem(item);
                            if (made == null) break;
                            made.GMMade = true;
                            made.Count = stack;
                            if (!p.CanGainItem(made)) break;
                            p.GainItem(made);
                            given += stack;
                        }
                        if (given > 0 && given < plan.Count) partial++;
                        break;
                    case "massskill":
                        if (SpellsOf(p.Class).Contains(plan.Spell))
                        {
                            var (l, r) = TeachSpells(p, single, plan.SpellLevel);
                            given = l + r;
                        }
                        break;
                    case "massallskills":
                    {
                        var (l, r) = TeachSpells(p, SpellsOf(p.Class), plan.SpellLevel);
                        given = l + r;
                        break;
                    }
                    case "masslevel":
                        if (p.Level != plan.Level)
                        {
                            p.Level = (ushort)plan.Level;
                            p.LevelUp();
                            given = 1;
                        }
                        break;
                }
                return given;
            }

            // The outcome is known only after the last batch, so the tally goes to the server log then.
            var label = action + " (" + plan.Who + (map != null ? " on " + map.Info.FileName : string.Empty) + ")";
            Main.AddAdminTickJob(() =>
            {
                var end = Math.Min(next + MassGrantPerTick, players.Count);
                for (; next < end; next++)
                {
                    var p = players[next];
                    if (p.Node == null || p.Info == null || p.CurrentMap == null || (map != null && p.CurrentMap != map)) { gone++; continue; }

                    var given = Grant(p);
                    if (given > 0) { granted++; total += given; }
                    else skipped++;
                }

                if (next < players.Count) return false;

                WebAdminLog.Info(label + " done: " + granted + " granted, " + skipped + " got nothing, " + partial + " partial (bag full), " +
                                 gone + " gone, total " + total + ".");
                return true;
            });

            string what;
            switch (action)
            {
                case "massgold":
                    what = plan.Amount.ToString("N0", CultureInfo.InvariantCulture) + " gold each";
                    break;
                case "masscredit":
                    what = plan.Amount.ToString("N0", CultureInfo.InvariantCulture) + " credit each";
                    break;
                case "massitem":
                    what = plan.Count + " x " + item.FriendlyName + " each";
                    break;
                case "massskill":
                    what = plan.Spell + " at level " + plan.SpellLevel;
                    break;
                case "masslevel":
                    what = "level " + plan.Level;
                    break;
                default:
                    what = "their class spells at level " + plan.SpellLevel;
                    break;
            }

            var ticks = (players.Count + MassGrantPerTick - 1) / MassGrantPerTick;
            result.Target = scope;
            result.Message = "Giving " + what + " to " + players.Count + (players.Count == 1 ? " player" : " players") + " (" + scope + ")" +
                             (ticks > 1 ? ", " + MassGrantPerTick + " per tick over " + ticks + " ticks" : string.Empty) +
                             "; the totals go to the server log when done.";
            result.Details["who"] = plan.Who;
            if (map != null) result.Details["map"] = map.Info.FileName;
            result.Details["queued"] = players.Count;
            result.Details["perTick"] = MassGrantPerTick;
            if (action == "massgold" || action == "masscredit") result.Details["amount"] = plan.Amount;
            if (item != null)
            {
                result.Details["item"] = item.Name;
                result.Details["itemIndex"] = item.Index;
                result.Details["count"] = plan.Count;
            }
            if (action == "massskill") result.Details["spell"] = plan.Spell.ToString();
            if (action == "massskill" || action == "massallskills") result.Details["level"] = plan.SpellLevel;
            if (action == "masslevel") result.Details["level"] = plan.Level;
            result.Details["players"] = names;
            result.Data = new { queued = players.Count, perTick = MassGrantPerTick };
        }

        /// <summary>Players served per game tick by the mass grant verbs.</summary>
        private const int MassGrantPerTick = 50;

        private static ConquestObject FindConquest(int index)
        {
            return Main.Conquests.FirstOrDefault(c => c?.Info != null && c.Info.Index == index)
                   ?? throw new WebAdminException(404, "Conquest #" + index + " is not loaded");
        }

        private static void AnnounceWar(ConquestObject conquest, bool started)
        {
            var text = started
                ? GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.WarStarted, conquest.Info.Name)
                : GameLanguage.ServerTextMap.GetLocalization(ServerTextKeys.WarStopped, conquest.Info.Name);

            MessageQueue.Instance.Enqueue(text);

            foreach (var pl in Main.Players.ToList())
            {
                pl.ReceiveChat(text, ChatType.System);
                pl.BroadcastInfo();
            }
        }

        private static void StartConquest(GmPlan plan, GmResult result)
        {
            var conquest = FindConquest(plan.ConquestIndex);
            if (conquest.WarIsOn) throw new WebAdminException(409, conquest.Info.Name + " is already at war");

            GuildObject guild = null;
            if (!string.IsNullOrEmpty(plan.Guild))
            {
                guild = Main.Guilds.FirstOrDefault(g => string.Equals(g.Name, plan.Guild, StringComparison.OrdinalIgnoreCase))
                        ?? Main.GetGuild(plan.Guild.Replace(" ", string.Empty))
                        ?? throw new WebAdminException(404, "Guild not found: " + plan.Guild);
            }

            conquest.StartType = ConquestType.Forced;
            if (guild != null) conquest.GuildInfo.AttackerID = guild.Guildindex;
            conquest.WarIsOn = true;

            AnnounceWar(conquest, true);

            result.Target = conquest.Info.Name + " (#" + conquest.Info.Index + ")";
            result.Message = "Started the " + conquest.Info.Name + " war" + (guild != null ? " (attacker: " + guild.Name + ")" : string.Empty) + ".";
            result.Details["conquest"] = conquest.Info.Name;
            result.Details["conquestIndex"] = conquest.Info.Index;
            if (guild != null) result.Details["attacker"] = guild.Name;
        }

        private static void StopConquest(GmPlan plan, GmResult result)
        {
            var conquest = FindConquest(plan.ConquestIndex);
            if (!conquest.WarIsOn) throw new WebAdminException(409, conquest.Info.Name + " is not at war");

            // EndWar decides the winner/ownership (ControlPoints) like a natural end of war; then hand the conquest back to
            // its configured schedule (AutoSchedule skips everything while StartType == Forced).
            conquest.EndWar(conquest.GameType);
            conquest.StartType = conquest.Info.Type;
            AnnounceWar(conquest, false);

            result.Target = conquest.Info.Name + " (#" + conquest.Info.Index + ")";
            result.Message = "Stopped the " + conquest.Info.Name + " war.";
            result.Details["conquest"] = conquest.Info.Name;
            result.Details["conquestIndex"] = conquest.Info.Index;
        }

        // ------------------------------------------------------------------------------------------------- resolvers

        internal static string MapLabel(Map map)
        {
            if (map?.Info == null) return "?";
            return string.IsNullOrEmpty(map.Info.Title) ? map.Info.FileName : map.Info.Title + " [" + map.Info.FileName + "]";
        }

        /// <summary>Map by file name (with instance), then title, then index (plain number fallback or "#12"). 404 when nothing matches.</summary>
        internal static Map ResolveMap(string mapRef, int instance)
        {
            var (index, name) = GmRules.ParseMapRef(mapRef);
            if (instance < 1) instance = 1;
            Map map = null;

            // File name first (what the in-game @MOVE / @MAPMOVE / @CLEARMOB use, and what the panel sends), then title,
            // and only then a numeric index ("#12" skips straight to the index).
            if (name != null)
            {
                map = Main.GetMapByNameAndInstance(name, instance)
                      ?? Main.MapList.Where(m => m.Info != null && string.Equals(m.Info.Title, name, StringComparison.OrdinalIgnoreCase)).Skip(instance - 1).FirstOrDefault();
            }
            if (map == null && index.HasValue)
            {
                map = Main.MapList.Where(m => m.Info != null && m.Info.Index == index.Value).Skip(instance - 1).FirstOrDefault();
            }

            return map ?? throw new WebAdminException(404, "Map not found: " + mapRef + (instance > 1 ? " (instance " + instance + ")" : string.Empty));
        }

        private static void RequireWalkable(Map map, Point pt)
        {
            if (pt.X < 0 || pt.Y < 0 || pt.X >= map.Width || pt.Y >= map.Height)
                throw new WebAdminException(422, "(" + pt.X + ", " + pt.Y + ") is outside " + MapLabel(map) + " (" + map.Width + " x " + map.Height + ")");
            if (!map.ValidPoint(pt))
                throw new WebAdminException(422, "(" + pt.X + ", " + pt.Y + ") is not walkable on " + MapLabel(map));
        }

        internal static ItemInfo ResolveItem(string itemRef)
        {
            if (string.IsNullOrWhiteSpace(itemRef)) return null;
            itemRef = itemRef.Trim();
            var list = Main.ItemInfoList;

            if (int.TryParse(itemRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                return list.FirstOrDefault(i => i.Index == index);

            return list.FirstOrDefault(i => string.Equals(i.Name, itemRef, StringComparison.OrdinalIgnoreCase))
                   ?? list.FirstOrDefault(i => GmRules.NameKey(i.Name) == GmRules.NameKey(itemRef));
        }

        internal static MonsterInfo ResolveMonster(string monsterRef)
        {
            if (string.IsNullOrWhiteSpace(monsterRef)) return null;
            monsterRef = monsterRef.Trim();
            var list = Main.MonsterInfoList;

            if (int.TryParse(monsterRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                return list.FirstOrDefault(m => m.Index == index);

            return list.FirstOrDefault(m => string.Equals(m.Name, monsterRef, StringComparison.OrdinalIgnoreCase))
                   ?? list.FirstOrDefault(m => GmRules.NameKey(m.Name) == GmRules.NameKey(monsterRef));
        }
    }
}

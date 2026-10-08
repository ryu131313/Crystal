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

        /// <summary>int | text | longtext | map | item | monster | spell | conquest | duration | guild.</summary>
        public string Type { get; set; }

        public bool Required { get; set; }

        public long? Min { get; set; }

        public long? Max { get; set; }

        /// <summary>Lookup name for pickers (players, maps, items, monsters).</summary>
        public string Lookup { get; set; }

        public string Placeholder { get; set; }

        public object Default { get; set; }

        public List<GmOption> Options { get; set; }
    }

    /// <summary>One GM verb.</summary>
    public sealed class GmVerbSpec
    {
        public string Action { get; set; }
        public string Label { get; set; }

        /// <summary>Moderation | Movement | Grants | Character | World.</summary>
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

        public static readonly IReadOnlyList<string> Groups = new[] { Moderation, Movement, Grants, CharacterGroup, World };

        /// <summary>Monster AIs that belong to conquests (siege gate, gates, archer, wall) — never spawned or cleared by the web GM.</summary>
        public static readonly IReadOnlySet<int> ConquestAIs = new HashSet<int> { 72, 73, 80, 81, 82 };

        public const long MaxCurrency = uint.MaxValue;
        public const int MaxItemCount = 10000;
        public const int MaxSpawnCount = 100;
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
                    Description = "Spawn monsters like @MOB (GM-made). Conquest guards/gates and intelligent creatures are refused.",
                    Args = new List<GmArgSpec>
                    {
                        new GmArgSpec { Name = "monster", Label = "Monster (index or name)", Type = "monster", Required = true, Lookup = "monsters" },
                        MapArg(true),
                        Int("x", "X", true, 0, 10000),
                        Int("y", "Y", true, 0, 10000),
                        Int("count", "Count", false, 1, MaxSpawnCount, 1),
                        Int("spread", "Spread (cells around X/Y)", false, 0, 20, 0),
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

            GmResult result;
            if (verb.WorksOffline)
                result = await WebAdminGame.RunOrInlineAsync(() => Run(plan)).ConfigureAwait(false);
            else
                result = await WebAdminGame.RunAsync(() => Run(plan)).ConfigureAwait(false);

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
                case "setlevel":
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
                    plan.X = (int)args.Long("x", true, 0, 10000, "X").Value;
                    plan.Y = (int)args.Long("y", true, 0, 10000, "Y").Value;
                    plan.Count = (int)(args.Long("count", false, 1, GmCatalog.MaxSpawnCount, "Count") ?? 1);
                    plan.Spread = (int)(args.Long("spread", false, 0, 20, "Spread") ?? 0);
                    plan.Instance = (int)(args.Long("instance", false, 1, 1000, "Instance") ?? 1);
                    break;
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
                case "clearmobs": ClearMobs(plan, result); break;
                case "startconquest": StartConquest(plan, result); break;
                case "stopconquest": StopConquest(plan, result); break;
                default: throw new WebAdminException(400, "Unknown GM action: " + plan.Verb.Action);
            }

            return result;
        }

        private static void SpawnMob(GmPlan plan, GmResult result)
        {
            var info = ResolveMonster(plan.MonsterRef) ?? throw new WebAdminException(404, "Monster not found: " + plan.MonsterRef);
            if (GmCatalog.ConquestAIs.Contains((int)info.AI))
                throw new WebAdminException(422, info.Name + " is a conquest structure and cannot be spawned by hand");

            var map = ResolveMap(plan.MapRef, plan.Instance);
            var centre = new Point(plan.X.Value, plan.Y.Value);
            RequireWalkable(map, centre);

            var spawned = 0;
            for (var i = 0; i < plan.Count; i++)
            {
                var monster = MonsterObject.GetMonster(info) ?? throw new WebAdminException(422, info.Name + " cannot be spawned (unknown AI " + info.AI + ")");
                if (monster is IntelligentCreatureObject)
                    throw new WebAdminException(422, info.Name + " is an intelligent creature (pet) and cannot be spawned");

                monster.GMMade = true;

                var placed = false;
                if (plan.Spread <= 0)
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
            result.Message = "Spawned " + spawned + " x " + info.Name + " on " + MapLabel(map) + " at (" + centre.X + ", " + centre.Y + ").";
            result.Details["monster"] = info.Name;
            result.Details["monsterIndex"] = info.Index;
            result.Details["map"] = map.Info.FileName;
            result.Details["x"] = centre.X;
            result.Details["y"] = centre.Y;
            result.Details["requested"] = plan.Count;
            result.Details["spawned"] = spawned;
            result.Data = new { monster = info.Name, map = map.Info.Title, mapFile = map.Info.FileName, spawned };
        }

        private static void ClearMobs(GmPlan plan, GmResult result)
        {
            var map = ResolveMap(plan.MapRef, plan.Instance);

            var victims = new List<MonsterObject>();
            var seen = new HashSet<MapObject>();
            var cells = map.Cells;

            if (cells != null)
            {
                for (var x = 0; x < map.Width; x++)
                {
                    for (var y = 0; y < map.Height; y++)
                    {
                        var cell = cells[x, y];
                        if (cell?.Objects == null) continue;

                        for (var i = 0; i < cell.Objects.Count; i++)
                        {
                            if (!(cell.Objects[i] is MonsterObject mob) || mob.Race != ObjectType.Monster || mob.Dead) continue;
                            if (mob.Master != null) continue; // pets
                            if (mob.Info != null && GmCatalog.ConquestAIs.Contains((int)mob.Info.AI)) continue;
                            if (seen.Add(mob)) victims.Add(mob);
                        }
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

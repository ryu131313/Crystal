using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Server.MirDatabase;
using Server.MirEnvir;

namespace Server.WebAdmin.Routes
{
    // =====================================================================================================================
    // Pure model / validation code for the Balance + Systems feature (B7). Nothing in this file touches Settings statics,
    // Envir or the game thread, so it is unit-testable; the route classes apply the validated results.
    // =====================================================================================================================

    /// <summary>One validation problem: <c>field</c> is a JSON path such as <c>stats[2].gain</c>.</summary>
    public sealed class BsIssue
    {
        public string Field { get; set; }
        public string Message { get; set; }
    }

    /// <summary>Collects validation errors (block the save) and warnings (reported, do not block).</summary>
    public sealed class BsValidation
    {
        public List<BsIssue> Errors { get; } = new List<BsIssue>();
        public List<string> Warnings { get; } = new List<string>();

        public bool Ok => Errors.Count == 0;

        public void Error(string field, string message)
        {
            Errors.Add(new BsIssue { Field = field ?? string.Empty, Message = message ?? "is invalid" });
        }

        public void Warn(string message)
        {
            if (!string.IsNullOrEmpty(message) && !Warnings.Contains(message)) Warnings.Add(message);
        }

        /// <summary>422 with <c>data:{errors:[{field,message}], warnings:[...]}</c> when there are errors.</summary>
        public void ThrowIfInvalid()
        {
            if (Ok) return;

            var message = Errors.Count == 1
                ? (string.IsNullOrEmpty(Errors[0].Field) ? Errors[0].Message : Errors[0].Field + ": " + Errors[0].Message)
                : Errors.Count + " validation errors - nothing was saved";

            throw new WebAdminException(422, message, new { errors = Errors, warnings = Warnings });
        }
    }

    /// <summary>Strict JSON readers: unlike the lenient WebAdminJson accessors they report bad values instead of falling back.</summary>
    public static class BsJson
    {
        public static bool TryGet(JsonElement obj, string name, out JsonElement value)
        {
            if (obj.TryGetPropertyIgnoreCase(name, out value) && value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined)
                return true;

            value = default;
            return false;
        }

        public static bool TryInteger(JsonElement value, out long result)
        {
            result = 0;

            switch (value.ValueKind)
            {
                case JsonValueKind.Number:
                    if (value.TryGetInt64(out result)) return true;
                    if (value.TryGetDouble(out var d) && !double.IsNaN(d) && !double.IsInfinity(d) && Math.Floor(d) == d && d >= long.MinValue && d <= long.MaxValue)
                    {
                        result = (long)d;
                        return true;
                    }
                    return false;
                case JsonValueKind.String:
                    return long.TryParse((value.GetString() ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
                default:
                    return false;
            }
        }

        public static bool TryNumber(JsonElement value, out double result)
        {
            result = 0;

            switch (value.ValueKind)
            {
                case JsonValueKind.Number:
                    if (!value.TryGetDouble(out result)) return false;
                    break;
                case JsonValueKind.String:
                    if (!double.TryParse((value.GetString() ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result)) return false;
                    break;
                default:
                    return false;
            }

            return !double.IsNaN(result) && !double.IsInfinity(result);
        }

        public static bool TryBool(JsonElement value, out bool result)
        {
            result = false;

            switch (value.ValueKind)
            {
                case JsonValueKind.True:
                    result = true;
                    return true;
                case JsonValueKind.False:
                    return true;
                case JsonValueKind.Number:
                    if (!value.TryGetInt64(out var n) || (n != 0 && n != 1)) return false;
                    result = n == 1;
                    return true;
                case JsonValueKind.String:
                    var s = (value.GetString() ?? string.Empty).Trim();
                    if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) { result = true; return true; }
                    if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) { result = false; return true; }
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>String value (numbers are accepted as their raw text); null for other kinds.</summary>
        public static string TryText(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Number:
                    return value.GetRawText();
                default:
                    return null;
            }
        }

        /// <summary>Reads an integer property within [min, max]. Missing → error only when required. Returns true when a valid value was read.</summary>
        public static bool ReadInt(JsonElement obj, string name, long min, long max, BsValidation v, string field, bool required, out long result)
        {
            result = 0;

            if (!TryGet(obj, name, out var el))
            {
                if (required) v.Error(field, "is required");
                return false;
            }

            if (!TryInteger(el, out result))
            {
                v.Error(field, "must be a whole number");
                return false;
            }

            if (result < min || result > max)
            {
                v.Error(field, "must be between " + min.ToString(CultureInfo.InvariantCulture) + " and " + max.ToString(CultureInfo.InvariantCulture));
                return false;
            }

            return true;
        }

        public static bool ReadNumber(JsonElement obj, string name, double min, double max, BsValidation v, string field, bool required, out double result, bool minExclusive = false)
        {
            result = 0;

            if (!TryGet(obj, name, out var el))
            {
                if (required) v.Error(field, "is required");
                return false;
            }

            if (!TryNumber(el, out result))
            {
                v.Error(field, "must be a number");
                return false;
            }

            if ((minExclusive ? result <= min : result < min) || result > max)
            {
                v.Error(field, "must be " + (minExclusive ? "greater than " : "between ") + min.ToString(CultureInfo.InvariantCulture)
                    + (minExclusive ? " and at most " : " and ") + max.ToString(CultureInfo.InvariantCulture));
                return false;
            }

            return true;
        }

        public static bool ReadBool(JsonElement obj, string name, BsValidation v, string field, bool required, out bool result)
        {
            result = false;

            if (!TryGet(obj, name, out var el))
            {
                if (required) v.Error(field, "is required");
                return false;
            }

            if (!TryBool(el, out result))
            {
                v.Error(field, "must be true or false");
                return false;
            }

            return true;
        }

        /// <summary>Reads a string (trimmed). Missing → "" (or an error when required).</summary>
        public static bool ReadString(JsonElement obj, string name, int maxLength, BsValidation v, string field, bool required, out string result)
        {
            result = string.Empty;

            if (!TryGet(obj, name, out var el))
            {
                if (required) v.Error(field, "is required");
                return false;
            }

            var s = TryText(el);
            if (s == null)
            {
                v.Error(field, "must be text");
                return false;
            }

            s = s.Trim();
            if (s.Length > maxLength)
            {
                v.Error(field, "must be at most " + maxLength + " characters");
                return false;
            }

            if (s.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            {
                v.Error(field, "must be a single line");
                return false;
            }

            result = s;
            return true;
        }

        /// <summary>Byte-backed enum from a name (case-insensitive) or its number; only defined members are accepted.</summary>
        public static bool TryEnum<T>(JsonElement value, out T result) where T : struct, Enum
        {
            result = default;

            if (value.ValueKind == JsonValueKind.Number)
                return value.TryGetInt64(out var n) && TryEnumNumber(n, out result);

            if (value.ValueKind == JsonValueKind.String)
                return TryEnumText(value.GetString(), out result);

            return false;
        }

        public static bool TryEnumText<T>(string text, out T result) where T : struct, Enum
        {
            result = default;
            var s = (text ?? string.Empty).Trim();
            if (s.Length == 0) return false;

            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return TryEnumNumber(n, out result);

            if (s.IndexOf(',') >= 0) return false;
            return Enum.TryParse(s, true, out result) && Enum.IsDefined(typeof(T), result);
        }

        private static bool TryEnumNumber<T>(long n, out T result) where T : struct, Enum
        {
            result = default;
            if (n < 0 || n > 255) return false;

            var boxed = Enum.ToObject(typeof(T), n);
            if (!Enum.IsDefined(typeof(T), boxed)) return false;

            result = (T)boxed;
            return true;
        }

        /// <summary>The optional <c>reason</c> of a mutation body (trimmed, max 500 chars).</summary>
        public static string Reason(JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object || !TryGet(body, "reason", out var el)) return null;
            var s = TryText(el)?.Trim();
            if (string.IsNullOrEmpty(s)) return null;
            return s.Length > 500 ? s.Substring(0, 500) : s;
        }
    }

    /// <summary>Optimistic-concurrency token: FNV-1a 64 over the DTO's canonical JSON.</summary>
    public static class BsRev
    {
        public static string Of(object value)
        {
            var bytes = WebAdminJson.SerializeToUtf8Bytes(value);
            ulong hash = 14695981039346656037UL;

            for (var i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= 1099511628211UL;
            }

            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        /// <summary>409 when the body carries a <c>rev</c> that differs from the current one (a missing rev is accepted).</summary>
        public static void Check(JsonElement body, string currentRev)
        {
            if (body.ValueKind != JsonValueKind.Object || !BsJson.TryGet(body, "rev", out var el)) return;

            var given = BsJson.TryText(el);
            if (string.IsNullOrEmpty(given)) return;

            if (!string.Equals(given.Trim(), currentRev, StringComparison.OrdinalIgnoreCase))
                throw new WebAdminException(409, "These settings were changed since you loaded them. Reload, then re-apply your edits.", new { rev = currentRev });
        }
    }

    /// <summary>How names stored in Settings are matched against the item / monster databases (mirrors Crystal's own lookups).</summary>
    public enum BsNameMode
    {
        /// <summary>Envir.GetItemInfo(string): space-stripped ItemInfo.Name, case-insensitive. Stored form = Name without spaces.</summary>
        ItemStripped,

        /// <summary>Envir.GetMonsterInfo(string): exact or space-stripped, case-insensitive. Stored form = the monster's Name.</summary>
        Monster,

        /// <summary>Exact, case-sensitive (refine ore is compared with <c>ItemInfo.FriendlyName ==</c>).</summary>
        Exact,
    }

    /// <summary>Resolves user-typed names to the canonical form Crystal will later look up successfully.</summary>
    public sealed class BsNameIndex
    {
        private readonly Dictionary<string, string> _map;
        private readonly BsNameMode _mode;

        public BsNameIndex(IEnumerable<string> names, BsNameMode mode)
        {
            _mode = mode;
            _map = new Dictionary<string, string>(mode == BsNameMode.Exact ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

            var display = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in names ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var name = raw.Trim();
                display.Add(name);

                switch (mode)
                {
                    case BsNameMode.ItemStripped:
                        var stripped = name.Replace(" ", string.Empty);
                        if (!_map.ContainsKey(stripped)) _map[stripped] = stripped;
                        break;
                    case BsNameMode.Monster:
                        if (!_map.ContainsKey(name)) _map[name] = name;
                        var key = name.Replace(" ", string.Empty);
                        if (!_map.ContainsKey(key)) _map[key] = name;
                        break;
                    default:
                        if (!_map.ContainsKey(name)) _map[name] = name;
                        break;
                }
            }

            Names = display.ToList();
        }

        /// <summary>
        /// Values Settings.RefineOreName may take. The name must satisfy both of Crystal's checks: refining matches it exactly against
        /// ItemInfo.FriendlyName (PlayerObject.cs refine), and Envir's start-up requirement check calls GetItemInfo(RefineOreName), which
        /// compares the space-stripped ItemInfo.Name case-insensitively — a value failing that stops the server from starting.
        /// </summary>
        public static List<string> RefineOreCandidates(IEnumerable<(string Name, string FriendlyName)> items)
        {
            var list = (items ?? Enumerable.Empty<(string Name, string FriendlyName)>()).Where(i => i.Name != null).ToList();
            var stripped = new HashSet<string>(list.Select(i => i.Name.Replace(" ", string.Empty)), StringComparer.OrdinalIgnoreCase);

            return list
                .Select(i => i.FriendlyName)
                .Where(f => !string.IsNullOrWhiteSpace(f) && f == f.Trim() && stripped.Contains(f))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Distinct display names, sorted.</summary>
        public IReadOnlyList<string> Names { get; }

        public int Count => Names.Count;

        /// <summary>The canonical stored form for <paramref name="input"/>, or null when nothing matches.</summary>
        public string Resolve(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var s = input.Trim();

            switch (_mode)
            {
                case BsNameMode.ItemStripped:
                    return _map.TryGetValue(s.Replace(" ", string.Empty), out var item) ? item : null;
                case BsNameMode.Monster:
                    if (_map.TryGetValue(s, out var mob)) return mob;
                    return _map.TryGetValue(s.Replace(" ", string.Empty), out mob) ? mob : null;
                default:
                    return _map.TryGetValue(s, out var exact) ? exact : null;
            }
        }
    }

    // =====================================================================================================================
    // Balance: base stats (class + hero), live example, item stat weights, random item stats
    // =====================================================================================================================

    public sealed class BaseStatRowDto
    {
        public string Stat { get; set; }
        public int StatValue { get; set; }
        public string Formula { get; set; }
        public int Base { get; set; }
        public float Gain { get; set; }
        public float GainRate { get; set; }
        public int Max { get; set; }
    }

    public sealed class CapRowDto
    {
        public string Stat { get; set; }
        public int StatValue { get; set; }
        public int Value { get; set; }
    }

    public sealed class BaseStatsDto
    {
        public string Job { get; set; }
        public int JobValue { get; set; }
        public bool Hero { get; set; }
        public List<BaseStatRowDto> Stats { get; set; } = new List<BaseStatRowDto>();
        public List<CapRowDto> Caps { get; set; } = new List<CapRowDto>();
        public string Rev { get; set; }
    }

    public static class BalanceModel
    {
        /// <summary>Stats selectable in the base stat / cap grids (every defined Stat except Unknown, like BalanceConfigForm).</summary>
        public static readonly IReadOnlyList<Stat> EditableStats = Enum.GetValues(typeof(Stat)).Cast<Stat>().Where(s => s != Stat.Unknown).ToArray();

        public const int MaxRows = 200;
        public const double MaxGain = 1000000d;

        /// <summary>Class route value: name (case-insensitive) or 0..4.</summary>
        public static bool TryParseClass(string text, out MirClass job)
        {
            return BsJson.TryEnumText(text, out job);
        }

        public static BaseStatsDto ToDto(BaseStats stats, bool hero)
        {
            var dto = new BaseStatsDto
            {
                Job = stats.Job.ToString(),
                JobValue = (int)stats.Job,
                Hero = hero,
                Stats = stats.Stats.Select(s => new BaseStatRowDto
                {
                    Stat = s.Type.ToString(),
                    StatValue = (int)s.Type,
                    Formula = s.FormulaType.ToString(),
                    Base = s.Base,
                    Gain = s.Gain,
                    GainRate = s.GainRate,
                    Max = s.Max,
                }).ToList(),
                Caps = stats.Caps.Values.Select(p => new CapRowDto
                {
                    Stat = p.Key.ToString(),
                    StatValue = (int)p.Key,
                    Value = p.Value,
                }).ToList(),
            };

            dto.Rev = BsRev.Of(new { dto.Stats, dto.Caps });
            return dto;
        }

        /// <summary>
        /// Parses <c>{stats:[{stat, formula, base, gain, gainRate, max}], caps:[{stat, value}]}</c> into a fresh BaseStats for
        /// <paramref name="job"/>. Rules (BalanceConfigForm + fixes): known stat/formula; a stat at most once in each grid (the form
        /// silently dropped duplicates); base/max/cap integers; gain/gainRate finite decimals; max and caps not negative.
        /// </summary>
        public static BaseStats Parse(MirClass job, JsonElement body, BsValidation v)
        {
            var result = new BaseStats(job);
            result.Stats.Clear();
            result.Caps.Clear();

            if (body.ValueKind != JsonValueKind.Object)
            {
                v.Error("body", "must be a JSON object");
                return result;
            }

            if (!BsJson.TryGet(body, "stats", out var stats) || stats.ValueKind != JsonValueKind.Array)
            {
                v.Error("stats", "must be an array");
            }
            else if (stats.GetArrayLength() > MaxRows)
            {
                v.Error("stats", "has too many rows (max " + MaxRows + ")");
            }
            else
            {
                var seen = new HashSet<Stat>();
                var i = -1;

                foreach (var row in stats.EnumerateArray())
                {
                    i++;
                    var f = "stats[" + i + "]";

                    if (row.ValueKind != JsonValueKind.Object)
                    {
                        v.Error(f, "must be an object");
                        continue;
                    }

                    if (!BsJson.TryGet(row, "stat", out var se) || !BsJson.TryEnum(se, out Stat stat) || stat == Stat.Unknown)
                    {
                        v.Error(f + ".stat", "is not a known stat");
                        continue;
                    }

                    if (!seen.Add(stat))
                    {
                        v.Error(f + ".stat", stat + " is listed more than once");
                        continue;
                    }

                    if (!BsJson.TryGet(row, "formula", out var fe) || !BsJson.TryEnum(fe, out StatFormula formula))
                    {
                        v.Error(f + ".formula", "is not a known formula (Health, Mana, Weight, Stat)");
                        continue;
                    }

                    var okBase = BsJson.ReadInt(row, "base", int.MinValue, int.MaxValue, v, f + ".base", true, out var b);
                    var okGain = BsJson.ReadNumber(row, "gain", -MaxGain, MaxGain, v, f + ".gain", true, out var gain);
                    double gainRate = 0;
                    var okRate = !BsJson.TryGet(row, "gainRate", out _) || BsJson.ReadNumber(row, "gainRate", -MaxGain, MaxGain, v, f + ".gainRate", false, out gainRate);
                    long max = 0;
                    var okMax = !BsJson.TryGet(row, "max", out _) || BsJson.ReadInt(row, "max", 0, int.MaxValue, v, f + ".max", false, out max);

                    if (!(okBase && okGain && okRate && okMax)) continue;

                    result.Stats.Add(new BaseStat(stat)
                    {
                        FormulaType = formula,
                        Base = (int)b,
                        Gain = (float)gain,
                        GainRate = (float)gainRate,
                        Max = (int)max,
                    });
                }
            }

            if (!BsJson.TryGet(body, "caps", out var caps))
            {
                // caps omitted → none
            }
            else if (caps.ValueKind != JsonValueKind.Array)
            {
                v.Error("caps", "must be an array");
            }
            else if (caps.GetArrayLength() > MaxRows)
            {
                v.Error("caps", "has too many rows (max " + MaxRows + ")");
            }
            else
            {
                var seen = new HashSet<Stat>();
                var i = -1;

                foreach (var row in caps.EnumerateArray())
                {
                    i++;
                    var f = "caps[" + i + "]";

                    if (row.ValueKind != JsonValueKind.Object)
                    {
                        v.Error(f, "must be an object");
                        continue;
                    }

                    if (!BsJson.TryGet(row, "stat", out var se) || !BsJson.TryEnum(se, out Stat stat) || stat == Stat.Unknown)
                    {
                        v.Error(f + ".stat", "is not a known stat");
                        continue;
                    }

                    if (!seen.Add(stat))
                    {
                        v.Error(f + ".stat", stat + " is capped more than once");
                        continue;
                    }

                    if (!BsJson.ReadInt(row, "value", 0, int.MaxValue, v, f + ".value", true, out var value)) continue;

                    if (value == 0) v.Warn("A cap of 0 means \"no cap\" for " + stat + "; the row is dropped on save.");
                    result.Caps[stat] = (int)value;
                }
            }

            return result;
        }

        /// <summary>Values of one base-stat row for levels from..to, capped exactly like BalanceConfigForm.UpdateClassStatExample.</summary>
        public static List<int> Preview(MirClass job, StatFormula formula, int @base, float gain, float gainRate, int max, int cap, int fromLevel, int toLevel)
        {
            fromLevel = Math.Clamp(fromLevel, 1, 1000);
            toLevel = Math.Clamp(toLevel, fromLevel, Math.Min(1000, fromLevel + 499));

            var stat = new BaseStat(Stat.HP) { FormulaType = formula, Base = @base, Gain = gain, GainRate = gainRate, Max = max };
            var effectiveCap = cap <= 0 ? int.MaxValue : cap;
            var values = new List<int>(toLevel - fromLevel + 1);

            for (var level = fromLevel; level <= toLevel; level++)
                values.Add(Math.Min(effectiveCap, stat.Calculate(job, level)));

            return values;
        }
    }

    /// <summary>A scalar Settings field edited through a generic form (item weights, system groups).</summary>
    public sealed class BsFieldDef
    {
        public BsFieldDef(string field, string label, string kind, long min = 0, long max = 0, string help = null, string lookup = null, int arrayIndex = -1, string arrayLabel = null)
        {
            Field = field;
            Label = label;
            Kind = kind;
            Min = min;
            Max = max;
            Help = help;
            Lookup = lookup;
            ArrayIndex = arrayIndex;
            ArrayLabel = arrayLabel;
        }

        /// <summary>The <c>Settings</c> static field name.</summary>
        public string Field { get; }

        public string Label { get; }

        /// <summary>bool | byte | ushort | int | uint | long | float | string.</summary>
        public string Kind { get; }

        public long Min { get; }
        public long Max { get; }
        public string Help { get; }

        /// <summary>items | monsters | ores (validated against the Edit database), or null.</summary>
        public string Lookup { get; }

        /// <summary>&gt;= 0 for one element of a <c>bool[]</c> field (Hero_CanCreateClass).</summary>
        public int ArrayIndex { get; }

        public string ArrayLabel { get; }

        /// <summary>The key used in JSON (the field name, or <c>Field.Element</c> for array elements).</summary>
        public string Key => ArrayIndex >= 0 ? Field + "." + ArrayLabel : Field;

        /// <summary>The CLR type of the Settings field (element type for array fields).</summary>
        public Type ClrType
        {
            get
            {
                switch (Kind)
                {
                    case "bool": return typeof(bool);
                    case "byte": return typeof(byte);
                    case "ushort": return typeof(ushort);
                    case "int": return typeof(int);
                    case "uint": return typeof(uint);
                    case "long": return typeof(long);
                    case "float": return typeof(float);
                    default: return typeof(string);
                }
            }
        }

        /// <summary>Converts a JSON value to the field's CLR type, enforcing [Min, Max] (string: Max = max length). Null + error on failure.</summary>
        public object Parse(JsonElement value, out string error)
        {
            error = null;

            switch (Kind)
            {
                case "bool":
                    if (BsJson.TryBool(value, out var b)) return b;
                    error = "must be true or false";
                    return null;
                case "float":
                    if (!BsJson.TryNumber(value, out var d))
                    {
                        error = "must be a number";
                        return null;
                    }
                    if (d < Min || d > Max)
                    {
                        error = "must be between " + Min + " and " + Max;
                        return null;
                    }
                    return (float)d;
                case "string":
                    var s = BsJson.TryText(value);
                    if (s == null)
                    {
                        error = "must be text";
                        return null;
                    }
                    s = s.Trim();
                    if (Max > 0 && s.Length > Max)
                    {
                        error = "must be at most " + Max + " characters";
                        return null;
                    }
                    if (Min > 0 && s.Length < Min)
                    {
                        error = "is required";
                        return null;
                    }
                    if (s.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                    {
                        error = "must be a single line";
                        return null;
                    }
                    return s;
                default:
                    if (!BsJson.TryInteger(value, out var n))
                    {
                        error = "must be a whole number";
                        return null;
                    }
                    if (n < Min || n > Max)
                    {
                        error = "must be between " + Min.ToString(CultureInfo.InvariantCulture) + " and " + Max.ToString(CultureInfo.InvariantCulture);
                        return null;
                    }
                    switch (Kind)
                    {
                        case "byte": return (byte)n;
                        case "ushort": return (ushort)n;
                        case "int": return (int)n;
                        case "uint": return (uint)n;
                        default: return n;
                    }
            }
        }

        /// <summary>Metadata for the UI (no value).</summary>
        public object Describe(object value)
        {
            return new
            {
                key = Key,
                field = Field,
                label = Label,
                type = Kind,
                min = Kind == "bool" || Kind == "string" ? (long?)null : Min,
                max = Kind == "bool" ? (long?)null : Max,
                help = Help,
                lookup = Lookup,
                value,
            };
        }
    }

    /// <summary>One editable group of scalar Settings fields persisted by a single <c>Settings.SaveX()</c> method.</summary>
    public sealed class BsGroupDef
    {
        public BsGroupDef(string key, string label, string file, string saveMethod, string note, params BsFieldDef[] fields)
        {
            Key = key;
            Label = label;
            File = file;
            SaveMethod = saveMethod;
            Note = note;
            Fields = fields;
        }

        public string Key { get; }
        public string Label { get; }

        /// <summary>The INI file under Configs\ the save method writes.</summary>
        public string File { get; }

        /// <summary>Name of the parameterless public static <c>Settings</c> method that persists the group.</summary>
        public string SaveMethod { get; }

        public string Note { get; }
        public IReadOnlyList<BsFieldDef> Fields { get; }

        public BsFieldDef Find(string key)
        {
            return Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static class BsCatalog
    {
        private const string ChanceHelp = "Chance roll: higher = rarer.";

        /// <summary>BalanceConfigForm "Itemstats" tab (Setup.ini [Items], persisted with Settings.Save()).</summary>
        public static readonly BsGroupDef ItemWeights = new BsGroupDef("itemweights", "Item stat weights", "Setup.ini", "Save",
            "Weights divide or bound the matching item stat in combat formulas. Changes apply immediately.",
            new BsFieldDef("MagicResistWeight", "Magic resist weight", "byte", 0, 255, "A magic hit is resisted when Random(weight) < MagicResist."),
            new BsFieldDef("PoisonResistWeight", "Poison resist weight", "byte", 0, 255, "Poison is resisted when Random(weight) < PoisonResist."),
            new BsFieldDef("CriticalRateWeight", "Critical rate weight", "byte", 0, 255, "Crit chance % = CriticalRate x weight."),
            new BsFieldDef("CriticalDamageWeight", "Critical damage weight", "byte", 1, 255, "Crit bonus = damage x (CriticalDamage / weight) x 10. Must be at least 1."),
            new BsFieldDef("FreezingAttackWeight", "Freezing weight", "byte", 0, 255, "Freeze lands when Random(weight) < Freezing."),
            new BsFieldDef("PoisonAttackWeight", "Poison attack weight", "byte", 0, 255, "Poison attack lands when Random(weight) < PoisonAttack."),
            new BsFieldDef("HealthRegenWeight", "Health regen weight", "byte", 1, 255, "Regen bonus = regen x (HealthRecovery / weight). Must be at least 1."),
            new BsFieldDef("ManaRegenWeight", "Mana regen weight", "byte", 1, 255, "Regen bonus = regen x (SpellRecovery / weight). Must be at least 1."),
            new BsFieldDef("PvpCanResistMagic", "PvP: players can resist magic", "bool", help: "When off, MagicResist only works against monsters."),
            new BsFieldDef("PvpCanResistPoison", "PvP: players can resist poison", "bool", help: "When off, PoisonResist only works against monsters."),
            new BsFieldDef("PvpCanFreeze", "PvP: freezing works on players", "bool", help: "When off, Freezing only affects monsters."));

        /// <summary>SystemInfoForm tabs (minus Spawn tick, which lives in the Edit database), each with its own INI file.</summary>
        public static readonly IReadOnlyList<BsGroupDef> Groups = new[]
        {
            new BsGroupDef("fishing", "Fishing", "FishingSystem.ini", "SaveFishing", null,
                new BsFieldDef("FishingAttempts", "Attempts", "int", 10, 100, "Casts per fishing session (10-100)."),
                new BsFieldDef("FishingSuccessStart", "Success rate start (%)", "int", 0, 100),
                new BsFieldDef("FishingSuccessMultiplier", "Success rate multiplier", "int", 1, 100),
                new BsFieldDef("FishingDelay", "Delay (ms)", "long", 0, int.MaxValue, "Extra delay between casts."),
                new BsFieldDef("FishingMobSpawnChance", "Monster spawn chance (%)", "int", 0, 100),
                new BsFieldDef("FishingMonster", "Fishing monster", "string", 1, 50, "Monster that can be fished up.", "monsters")),
            new BsGroupDef("mail", "Mail", "MailSystem.ini", "SaveMail", null,
                new BsFieldDef("MailAutoSendGold", "Auto-send gold", "bool"),
                new BsFieldDef("MailAutoSendItems", "Auto-send items", "bool"),
                new BsFieldDef("MailFreeWithStamp", "Free with stamp", "bool"),
                new BsFieldDef("MailCostPer1KGold", "Cost per 1,000 gold", "uint", 0, 1000),
                new BsFieldDef("MailItemInsurancePercentage", "Item insurance (%)", "uint", 0, 100),
                new BsFieldDef("MailCapacity", "Mailbox capacity", "uint", 1, 10000, "Maximum mails kept per character.")),
            new BsGroupDef("goods", "Goods", "GoodsSystem.ini", "SaveGoods", null,
                new BsFieldDef("GoodsOn", "Goods system enabled", "bool"),
                new BsFieldDef("GoodsMaxStored", "Max stored per NPC", "uint", 0, 500),
                new BsFieldDef("GoodsBuyBackTime", "Buy-back time (minutes)", "uint", 0, 1440),
                new BsFieldDef("GoodsBuyBackMaxStored", "Buy-back max stored", "uint", 0, 500),
                new BsFieldDef("GoodsHideAddedStats", "Hide added stats", "bool")),
            new BsGroupDef("refine", "Refining", "RefineSystem.ini", "SaveRefine", null,
                new BsFieldDef("OnlyRefineWeapon", "Weapons only", "bool"),
                new BsFieldDef("RefineBaseChance", "Base chance (%)", "byte", 0, 100),
                new BsFieldDef("RefineTime", "Refine time (minutes)", "int", 0, 1000),
                new BsFieldDef("RefineIncrease", "Normal stat increase", "byte", 0, 255),
                new BsFieldDef("RefineCritChance", "Critical chance (%)", "byte", 0, 100),
                new BsFieldDef("RefineCritIncrease", "Critical multiplier", "byte", 0, 255),
                new BsFieldDef("RefineWepStatReduce", "Weapon diminishing return (%)", "byte", 0, 100),
                new BsFieldDef("RefineItemStatReduce", "Item diminishing return (%)", "byte", 0, 100),
                new BsFieldDef("RefineCost", "Cost", "int", 0, 2000),
                new BsFieldDef("RefineOreName", "Ore name", "string", 1, 50, "Must be an item whose display name (FriendlyName) equals its space-free item name, e.g. BlackIronOre: refining matches the display name and server start-up looks the name up.", "ores")),
            new BsGroupDef("marriage", "Relationship", "MarriageSystem.ini", "SaveMarriage", null,
                new BsFieldDef("WeddingRingRecall", "Lover recall", "bool"),
                new BsFieldDef("LoverEXPBonus", "Lover EXP bonus (%)", "int", 0, 500),
                new BsFieldDef("MarriageCooldown", "Marriage cooldown (days)", "int", 0, 365),
                new BsFieldDef("MarriageLevelRequired", "Required level", "int", 0, 255),
                new BsFieldDef("ReplaceWedRingCost", "Replace ring cost", "int", 0, 2000)),
            new BsGroupDef("mentor", "Mentor", "MentorSystem.ini", "SaveMentor", null,
                new BsFieldDef("MentorSkillBoost", "Mentee skill boost", "bool"),
                new BsFieldDef("MentorLevelGap", "Level gap", "byte", 0, 255),
                new BsFieldDef("MentorLength", "Mentorship length (days)", "byte", 0, 255),
                new BsFieldDef("MentorDamageBoost", "Mentor damage boost (%)", "byte", 0, 255),
                new BsFieldDef("MentorExpBoost", "Mentee EXP boost (%)", "byte", 0, 255),
                new BsFieldDef("MenteeExpBank", "EXP banked to mentor (%)", "byte", 0, 255)),
            new BsGroupDef("gem", "Gems", "GemSystem.ini", "SaveGem", null,
                new BsFieldDef("GemStatIndependent", "Gem stats independent", "bool", help: "Gem bonuses do not count towards the item's max-stat limit.")),
            new BsGroupDef("heroes", "Heroes", "HeroSettings.ini", "SaveHeroSettings", null,
                new BsFieldDef("AllowNewHero", "Allow new heroes", "bool"),
                new BsFieldDef("Hero_CanCreateClass", "Warrior heroes", "bool", arrayIndex: 0, arrayLabel: "Warrior"),
                new BsFieldDef("Hero_CanCreateClass", "Wizard heroes", "bool", arrayIndex: 1, arrayLabel: "Wizard"),
                new BsFieldDef("Hero_CanCreateClass", "Taoist heroes", "bool", arrayIndex: 2, arrayLabel: "Taoist"),
                new BsFieldDef("Hero_CanCreateClass", "Assassin heroes", "bool", arrayIndex: 3, arrayLabel: "Assassin"),
                new BsFieldDef("Hero_CanCreateClass", "Archer heroes", "bool", arrayIndex: 4, arrayLabel: "Archer"),
                new BsFieldDef("HeroMaximumSealCount", "Max sealed heroes", "ushort", 0, 255),
                new BsFieldDef("MaximumHeroCount", "Max heroes per player", "byte", 0, 9, "1-9 while new heroes are allowed."),
                new BsFieldDef("Hero_RequiredLevel", "Minimum player level", "byte", 1, 255),
                new BsFieldDef("HeroSealItemName", "Hero seal item", "string", 1, 50, null, "items")),
        };

        public static BsGroupDef FindGroup(string key)
        {
            return Groups.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Rules spanning several fields of a group (validated on the merged current + changed values).</summary>
        public static void CrossValidate(BsGroupDef group, IReadOnlyDictionary<string, object> merged, BsValidation v)
        {
            if (group == null || merged == null) return;

            if (group.Key == "heroes"
                && merged.TryGetValue("AllowNewHero", out var allow) && allow is bool allowed && allowed
                && merged.TryGetValue("MaximumHeroCount", out var count) && Convert.ToInt64(count, CultureInfo.InvariantCulture) < 1)
            {
                v.Error("MaximumHeroCount", "must be at least 1 while new heroes are allowed");
            }
        }

        /// <summary>
        /// Parses <c>{values:{key:value}}</c> against the group. Unknown keys → error. Returns key → typed value for the keys present.
        /// </summary>
        public static Dictionary<string, object> ParseValues(BsGroupDef group, JsonElement body, BsValidation v)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            if (body.ValueKind != JsonValueKind.Object || !BsJson.TryGet(body, "values", out var values) || values.ValueKind != JsonValueKind.Object)
            {
                v.Error("values", "must be an object of field → value");
                return result;
            }

            foreach (var p in values.EnumerateObject())
            {
                var def = group.Find(p.Name);
                if (def == null)
                {
                    v.Error(p.Name, "is not a field of " + group.Label);
                    continue;
                }

                var parsed = def.Parse(p.Value, out var error);
                if (error != null)
                {
                    v.Error(def.Key, error);
                    continue;
                }

                result[def.Key] = parsed;
            }

            return result;
        }
    }

    /// <summary>RandomItemStat field catalogue (BalanceConfigForm "Random Item Stats" tab).</summary>
    public static class RandomItemStatCatalog
    {
        public sealed class Group
        {
            public Group(string label, string prefix)
            {
                Label = label;
                Prefix = prefix;
            }

            public string Label { get; }
            public string Prefix { get; }
            public string ChanceField => Prefix + "Chance";
            public string StatChanceField => Prefix + "StatChance";
            public string MaxStatField => Prefix + "MaxStat";
        }

        public const string CurseField = "CurseChance";

        /// <summary>RandomItemStatsId is a byte and 255 means "none": at most 255 sets (ids 0..254).</summary>
        public const int MaxSets = 255;

        public static readonly IReadOnlyList<Group> Groups = new[]
        {
            new Group("Max durability", "MaxDura"),
            new Group("Max AC", "MaxAc"),
            new Group("Max MAC", "MaxMac"),
            new Group("Max DC", "MaxDc"),
            new Group("Max MC", "MaxMc"),
            new Group("Max SC", "MaxSc"),
            new Group("Accuracy", "Accuracy"),
            new Group("Agility", "Agility"),
            new Group("HP", "Hp"),
            new Group("MP", "Mp"),
            new Group("Strong", "Strong"),
            new Group("Magic resist", "MagicResist"),
            new Group("Poison resist", "PoisonResist"),
            new Group("HP recovery", "HpRecov"),
            new Group("MP recovery", "MpRecov"),
            new Group("Poison recovery", "PoisonRecov"),
            new Group("Critical rate", "CriticalRate"),
            new Group("Critical damage", "CriticalDamage"),
            new Group("Freezing", "Freeze"),
            new Group("Poison attack", "PoisonAttack"),
            new Group("Attack speed", "AttackSpeed"),
            new Group("Luck", "Luck"),
            new Group("Sockets (slots)", "Slot"),
        };

        /// <summary>Every editable field name, in display order.</summary>
        public static IEnumerable<string> FieldNames()
        {
            foreach (var g in Groups)
            {
                yield return g.ChanceField;
                yield return g.StatChanceField;
                yield return g.MaxStatField;
            }

            yield return CurseField;
        }

        public static FieldInfo Field(string name)
        {
            return typeof(RandomItemStat).GetField(name, BindingFlags.Public | BindingFlags.Instance);
        }

        public static Dictionary<string, int> ToValues(RandomItemStat stat)
        {
            var values = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var name in FieldNames())
                values[name] = (byte)Field(name).GetValue(stat);
            return values;
        }

        public static void Apply(RandomItemStat target, IReadOnlyDictionary<string, int> values)
        {
            foreach (var pair in values)
            {
                var field = Field(pair.Key);
                if (field != null) field.SetValue(target, (byte)pair.Value);
            }
        }

        public static RandomItemStat Clone(RandomItemStat source)
        {
            var copy = new RandomItemStat();
            Apply(copy, ToValues(source));
            return copy;
        }

        /// <summary>
        /// Parses <c>{values:{Field:n}}</c> merged onto <paramref name="current"/> and validates the merged set. Rules (form + loader):
        /// every value 0-255; MaxDuraChance is never 255 (RandomItemStats.ini stops loading at 255); a group with a chance needs a
        /// max stat of at least 1; sockets max out at 12.
        /// </summary>
        public static Dictionary<string, int> Parse(JsonElement body, IReadOnlyDictionary<string, int> current, BsValidation v)
        {
            var merged = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var name in FieldNames())
                merged[name] = current != null && current.TryGetValue(name, out var c) ? c : 0;

            if (body.ValueKind != JsonValueKind.Object || !BsJson.TryGet(body, "values", out var values) || values.ValueKind != JsonValueKind.Object)
            {
                v.Error("values", "must be an object of field → value");
                return merged;
            }

            var known = new HashSet<string>(FieldNames(), StringComparer.OrdinalIgnoreCase);

            foreach (var p in values.EnumerateObject())
            {
                var name = FieldNames().FirstOrDefault(n => string.Equals(n, p.Name, StringComparison.OrdinalIgnoreCase));
                if (name == null || !known.Contains(p.Name))
                {
                    v.Error(p.Name, "is not a random item stat field");
                    continue;
                }

                if (!BsJson.TryInteger(p.Value, out var n) || n < 0 || n > 255)
                {
                    v.Error(name, "must be a whole number between 0 and 255");
                    continue;
                }

                merged[name] = (int)n;
            }

            Validate(merged, v);
            return merged;
        }

        public static void Validate(IReadOnlyDictionary<string, int> values, BsValidation v)
        {
            if (values.TryGetValue("MaxDuraChance", out var dura) && dura == 255)
                v.Error("MaxDuraChance", "cannot be 255 (RandomItemStats.ini treats 255 as the end of the list)");

            foreach (var g in Groups)
            {
                values.TryGetValue(g.ChanceField, out var chance);
                values.TryGetValue(g.MaxStatField, out var maxStat);

                if (chance > 0 && maxStat < 1)
                    v.Error(g.MaxStatField, g.Label + ": max stat must be at least 1 when the chance is set");

                if (g.Prefix == "Slot" && maxStat > 12)
                    v.Error(g.MaxStatField, "Sockets: max stat must be at most 12");
            }
        }

        /// <summary>Template names accepted when adding a set (RandomItemStat(ItemType) presets).</summary>
        public static readonly IReadOnlyList<ItemType> Templates = new[]
        {
            ItemType.Nothing, ItemType.Weapon, ItemType.Armour, ItemType.Helmet, ItemType.Necklace, ItemType.Bracelet, ItemType.Ring,
            ItemType.Belt, ItemType.Boots, ItemType.Mount,
        };
    }

    // =====================================================================================================================
    // Systems: guild settings, mining, spawn tick
    // =====================================================================================================================

    public sealed class GuildCostDto
    {
        /// <summary>"" = gold.</summary>
        public string ItemName { get; set; }

        public uint Amount { get; set; }
    }

    public sealed class GuildLevelDto
    {
        public int Level { get; set; }
        public long Experience { get; set; }
        public int MemberCap { get; set; }
    }

    public sealed class GuildBuffDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int Icon { get; set; }
        public int LevelRequirement { get; set; }
        public int PointsRequirement { get; set; }
        public int TimeLimit { get; set; }
        public int ActivationCost { get; set; }
        public Dictionary<string, int> Stats { get; set; } = new Dictionary<string, int>();
    }

    public sealed class GuildSettingsDto
    {
        public int RequiredLevel { get; set; }
        public int PointPerLevel { get; set; }
        public float ExpRate { get; set; }
        public bool NewbieGuildBuffEnabled { get; set; }
        public int NewbieGuildExpBuff { get; set; }
        public long WarTime { get; set; }
        public uint WarCost { get; set; }
        public int BuyGTGold { get; set; }
        public int ExtendGT { get; set; }
        public int GTDays { get; set; }
        public List<GuildCostDto> CreationCosts { get; set; } = new List<GuildCostDto>();
        public List<GuildLevelDto> Levels { get; set; } = new List<GuildLevelDto>();
        public List<GuildBuffDto> Buffs { get; set; } = new List<GuildBuffDto>();
        public string Rev { get; set; }
    }

    public static class GuildSettingsModel
    {
        /// <summary>The stats GuildInfoForm edits and GuildSettings.ini stores (Buff-n keys), with their INI read limits.</summary>
        public static readonly IReadOnlyList<(Stat Stat, string Label, int Max)> BuffStats = new[]
        {
            (Stat.MaxAC, "AC", 255),
            (Stat.MaxMAC, "MAC", 255),
            (Stat.MaxDC, "DC", 255),
            (Stat.MaxMC, "MC", 255),
            (Stat.MaxSC, "SC", 255),
            (Stat.AttackBonus, "Attack", 255),
            (Stat.HealthRecovery, "HP regen", 255),
            (Stat.SpellRecovery, "MP regen", 255),
            (Stat.HP, "Max HP", int.MaxValue),
            (Stat.MP, "Max MP", int.MaxValue),
            (Stat.MineRatePercent, "Mine rate %", 255),
            (Stat.GemRatePercent, "Gem rate %", 255),
            (Stat.FishRatePercent, "Fish rate %", 255),
            (Stat.ExpRatePercent, "EXP rate %", 255),
            (Stat.CraftRatePercent, "Craft rate %", 255),
            (Stat.SkillGainMultiplier, "Skill gain", 255),
            (Stat.ItemDropRatePercent, "Drop rate %", 255),
            (Stat.GoldDropRatePercent, "Gold rate %", 255),
        };

        /// <summary>GuildSettings.ini stores TotalBuffs as a byte.</summary>
        public const int MaxBuffs = 255;

        public const int MaxLevels = 1000;
        public const int MaxCosts = 50;

        public static GuildBuffDto ToDto(GuildBuffInfo buff)
        {
            var dto = new GuildBuffDto
            {
                Id = buff.Id,
                Name = buff.Name ?? string.Empty,
                Icon = buff.Icon,
                LevelRequirement = buff.LevelRequirement,
                PointsRequirement = buff.PointsRequirement,
                TimeLimit = buff.TimeLimit,
                ActivationCost = buff.ActivationCost,
            };

            foreach (var s in BuffStats)
                dto.Stats[s.Stat.ToString()] = buff.Stats == null ? 0 : buff.Stats[s.Stat];

            return dto;
        }

        /// <summary>Copies a validated DTO onto a (new or live) GuildBuffInfo.</summary>
        public static void ApplyBuff(GuildBuffDto dto, GuildBuffInfo target)
        {
            target.Id = dto.Id;
            target.Name = dto.Name;
            target.Icon = dto.Icon;
            target.LevelRequirement = (byte)dto.LevelRequirement;
            target.PointsRequirement = (byte)dto.PointsRequirement;
            target.TimeLimit = dto.TimeLimit;
            target.ActivationCost = dto.ActivationCost;
            target.Stats ??= new Stats();

            foreach (var s in BuffStats)
                target.Stats[s.Stat] = dto.Stats.TryGetValue(s.Stat.ToString(), out var value) ? value : 0;
        }

        /// <summary>
        /// Parses the full guild settings body. <paramref name="resolveItem"/> maps a typed item name to its stored form (null =
        /// unknown); when <paramref name="itemsKnown"/> is false names are not checked (warning). Rules: GuildInfoForm types plus the
        /// GuildSettings.ini loader's sentinels (a creation cost Amount of 0 ends the list; level exp/cap of -1 ends the lists).
        /// </summary>
        public static GuildSettingsDto Parse(JsonElement body, Func<string, string> resolveItem, bool itemsKnown, BsValidation v)
        {
            var dto = new GuildSettingsDto();

            if (body.ValueKind != JsonValueKind.Object)
            {
                v.Error("body", "must be a JSON object");
                return dto;
            }

            if (BsJson.ReadInt(body, "requiredLevel", 0, 255, v, "requiredLevel", true, out var n)) dto.RequiredLevel = (int)n;
            if (BsJson.ReadInt(body, "pointPerLevel", 0, 255, v, "pointPerLevel", true, out n)) dto.PointPerLevel = (int)n;
            if (BsJson.ReadNumber(body, "expRate", 0, 1000, v, "expRate", true, out var d)) dto.ExpRate = (float)d;
            if (BsJson.ReadBool(body, "newbieGuildBuffEnabled", v, "newbieGuildBuffEnabled", true, out var b)) dto.NewbieGuildBuffEnabled = b;
            if (BsJson.ReadInt(body, "newbieGuildExpBuff", 0, 10000, v, "newbieGuildExpBuff", true, out n)) dto.NewbieGuildExpBuff = (int)n;
            if (BsJson.ReadInt(body, "warTime", 1, int.MaxValue, v, "warTime", true, out n)) dto.WarTime = n;
            if (BsJson.ReadInt(body, "warCost", 0, uint.MaxValue, v, "warCost", true, out n)) dto.WarCost = (uint)n;
            if (BsJson.ReadInt(body, "buyGTGold", 0, int.MaxValue, v, "buyGTGold", true, out n)) dto.BuyGTGold = (int)n;
            if (BsJson.ReadInt(body, "extendGT", 0, int.MaxValue, v, "extendGT", true, out n)) dto.ExtendGT = (int)n;
            if (BsJson.ReadInt(body, "gtDays", 1, 3650, v, "gtDays", true, out n)) dto.GTDays = (int)n;

            // ---- creation costs
            if (!BsJson.TryGet(body, "creationCosts", out var costs) || costs.ValueKind != JsonValueKind.Array)
            {
                v.Error("creationCosts", "must be an array");
            }
            else if (costs.GetArrayLength() > MaxCosts)
            {
                v.Error("creationCosts", "has too many rows (max " + MaxCosts + ")");
            }
            else
            {
                var i = -1;
                var goldRows = 0;

                foreach (var row in costs.EnumerateArray())
                {
                    i++;
                    var f = "creationCosts[" + i + "]";

                    if (row.ValueKind != JsonValueKind.Object)
                    {
                        v.Error(f, "must be an object");
                        continue;
                    }

                    BsJson.ReadString(row, "itemName", 50, v, f + ".itemName", false, out var itemName);
                    var okAmount = BsJson.ReadInt(row, "amount", 1, uint.MaxValue, v, f + ".amount", true, out var amount);

                    var stored = string.Empty;
                    if (!string.IsNullOrEmpty(itemName))
                    {
                        if (!itemsKnown)
                        {
                            stored = itemName.Replace(" ", string.Empty);
                            v.Warn("The item database is not loaded: creation cost item names were not checked.");
                        }
                        else
                        {
                            stored = resolveItem?.Invoke(itemName);
                            if (stored == null)
                            {
                                v.Error(f + ".itemName", "\"" + itemName + "\" is not an item in the database");
                                continue;
                            }
                        }
                    }
                    else
                    {
                        goldRows++;
                    }

                    if (okAmount) dto.CreationCosts.Add(new GuildCostDto { ItemName = stored, Amount = (uint)amount });
                }

                if (goldRows > 1) v.Warn("More than one gold row: the gold costs add up.");
            }

            // ---- levels
            if (!BsJson.TryGet(body, "levels", out var levels) || levels.ValueKind != JsonValueKind.Array)
            {
                v.Error("levels", "must be an array");
            }
            else if (levels.GetArrayLength() > MaxLevels)
            {
                v.Error("levels", "has too many rows (max " + MaxLevels + ")");
            }
            else
            {
                var i = -1;
                long previous = -1;

                foreach (var row in levels.EnumerateArray())
                {
                    i++;
                    var f = "levels[" + i + "]";

                    if (row.ValueKind != JsonValueKind.Object)
                    {
                        v.Error(f, "must be an object");
                        continue;
                    }

                    var okExp = BsJson.ReadInt(row, "experience", 0, long.MaxValue, v, f + ".experience", true, out var exp);
                    var okCap = BsJson.ReadInt(row, "memberCap", 0, int.MaxValue, v, f + ".memberCap", true, out var cap);
                    if (!okExp || !okCap) continue;

                    if (previous >= 0 && exp < previous)
                        v.Warn("Level " + i + " needs less experience than level " + (i - 1) + ".");
                    previous = exp;

                    dto.Levels.Add(new GuildLevelDto { Level = i, Experience = exp, MemberCap = (int)cap });
                }
            }

            // ---- buffs
            if (!BsJson.TryGet(body, "buffs", out var buffs) || buffs.ValueKind != JsonValueKind.Array)
            {
                v.Error("buffs", "must be an array");
            }
            else if (buffs.GetArrayLength() > MaxBuffs)
            {
                v.Error("buffs", "has too many buffs (max " + MaxBuffs + ")");
            }
            else
            {
                var i = -1;
                var ids = new HashSet<int>();
                var parsed = new List<GuildBuffDto>();

                foreach (var row in buffs.EnumerateArray())
                {
                    i++;
                    var f = "buffs[" + i + "]";

                    if (row.ValueKind != JsonValueKind.Object)
                    {
                        v.Error(f, "must be an object");
                        continue;
                    }

                    // A missing / null id marks a new buff (-1 until numbered below). Id 0 is a real id: GuildBuffInfo(InIReader)
                    // defaults a missing INI key to 0 and active guild buffs may reference it, so it must survive a save.
                    var buff = new GuildBuffDto { Id = -1 };
                    var ok = true;

                    if (BsJson.TryGet(row, "id", out _))
                    {
                        if (BsJson.ReadInt(row, "id", 0, int.MaxValue, v, f + ".id", false, out var id)) buff.Id = (int)id;
                        else ok = false;
                    }

                    if (BsJson.ReadString(row, "name", 20, v, f + ".name", true, out var name))
                    {
                        if (name.Length == 0)
                        {
                            v.Error(f + ".name", "is required (1-20 characters)");
                            ok = false;
                        }
                        buff.Name = name;
                    }
                    else ok = false;

                    ok &= Read(row, "icon", 0, int.MaxValue, v, f, x => buff.Icon = x);
                    ok &= Read(row, "levelRequirement", 0, 255, v, f, x => buff.LevelRequirement = x);
                    ok &= Read(row, "pointsRequirement", 0, 255, v, f, x => buff.PointsRequirement = x);
                    ok &= Read(row, "timeLimit", 0, int.MaxValue, v, f, x => buff.TimeLimit = x);
                    ok &= Read(row, "activationCost", 0, int.MaxValue, v, f, x => buff.ActivationCost = x);

                    BsJson.TryGet(row, "stats", out var stats);
                    if (stats.ValueKind != JsonValueKind.Undefined && stats.ValueKind != JsonValueKind.Object)
                    {
                        v.Error(f + ".stats", "must be an object of stat → value");
                        ok = false;
                    }
                    else if (stats.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in stats.EnumerateObject())
                        {
                            var def = BuffStats.FirstOrDefault(s => string.Equals(s.Stat.ToString(), p.Name, StringComparison.OrdinalIgnoreCase));
                            if (def.Label == null)
                            {
                                v.Error(f + ".stats." + p.Name, "is not a guild buff stat");
                                ok = false;
                                continue;
                            }

                            if (!BsJson.TryInteger(p.Value, out var sv) || sv < 0 || sv > def.Max)
                            {
                                v.Error(f + ".stats." + def.Stat, "must be a whole number between 0 and " + def.Max);
                                ok = false;
                                continue;
                            }

                            buff.Stats[def.Stat.ToString()] = (int)sv;
                        }
                    }

                    foreach (var s in BuffStats)
                        if (!buff.Stats.ContainsKey(s.Stat.ToString())) buff.Stats[s.Stat.ToString()] = 0;

                    if (buff.Id >= 0 && !ids.Add(buff.Id))
                    {
                        v.Error(f + ".id", "buff id " + buff.Id + " is used more than once");
                        ok = false;
                    }

                    if (ok) parsed.Add(buff);
                }

                // New buffs (id missing / null) get max(id) + 1, like GuildInfoForm's Add button.
                var next = ids.Count == 0 ? 0 : ids.Max();
                foreach (var buff in parsed)
                {
                    if (buff.Id < 0) buff.Id = ++next;
                    dto.Buffs.Add(buff);
                }
            }

            return dto;
        }

        private static bool Read(JsonElement row, string name, long min, long max, BsValidation v, string prefix, Action<int> set)
        {
            if (!BsJson.TryGet(row, name, out _))
            {
                set(0);
                return true;
            }

            if (!BsJson.ReadInt(row, name, min, max, v, prefix + "." + name, false, out var n)) return false;
            set((int)n);
            return true;
        }
    }

    public sealed class MineDropDto
    {
        public string ItemName { get; set; }
        public int MinSlot { get; set; }
        public int MaxSlot { get; set; }
        public int MinDura { get; set; }
        public int MaxDura { get; set; }
        public int BonusChance { get; set; }
        public int MaxBonusDura { get; set; }
    }

    public sealed class MineSetDto
    {
        /// <summary>1-based mine number as maps reference it (MapInfo.MineIndex / MineZone.Mine).</summary>
        public int Index { get; set; }

        public string Name { get; set; }
        public int SpotRegenRate { get; set; }
        public int MaxStones { get; set; }
        public int HitRate { get; set; }
        public int DropRate { get; set; }
        public int TotalSlots { get; set; }
        public List<MineDropDto> Drops { get; set; } = new List<MineDropDto>();
        public List<string> UsedBy { get; set; }
    }

    public static class MiningModel
    {
        public const int MaxSets = 255;
        public const int MaxDrops = 200;

        public static MineSetDto ToDto(MineSet set, int index, List<string> usedBy)
        {
            return new MineSetDto
            {
                Index = index,
                Name = set.Name ?? string.Empty,
                SpotRegenRate = set.SpotRegenRate,
                MaxStones = set.MaxStones,
                HitRate = set.HitRate,
                DropRate = set.DropRate,
                TotalSlots = set.TotalSlots,
                Drops = set.Drops.Select(d => new MineDropDto
                {
                    ItemName = d.ItemName ?? string.Empty,
                    MinSlot = d.MinSlot,
                    MaxSlot = d.MaxSlot,
                    MinDura = d.MinDura,
                    MaxDura = d.MaxDura,
                    BonusChance = d.BonusChance,
                    MaxBonusDura = d.MaxBonusDura,
                }).ToList(),
                UsedBy = usedBy ?? new List<string>(),
            };
        }

        public static MineDrop ToDrop(MineDropDto d)
        {
            return new MineDrop
            {
                ItemName = d.ItemName,
                MinSlot = (byte)d.MinSlot,
                MaxSlot = (byte)d.MaxSlot,
                MinDura = (byte)d.MinDura,
                MaxDura = (byte)d.MaxDura,
                BonusChance = (byte)d.BonusChance,
                MaxBonusDura = (byte)d.MaxBonusDura,
            };
        }

        /// <summary>
        /// Parses <c>{sets:[{name, spotRegenRate, maxStones, hitRate, dropRate, totalSlots, drops:[...]}]}</c>. Positions are the
        /// mine numbers maps use, so sets can only be removed from the end and never while a map still uses them
        /// (<paramref name="usage"/>: 1-based mine number → map names). Mines.ini sentinels: SpotRegenRate and a drop's MinSlot
        /// cannot be 255. Slot / quality ranges must be ordered.
        /// </summary>
        public static List<MineSetDto> Parse(JsonElement body, int currentCount, IReadOnlyDictionary<int, List<string>> usage,
            Func<string, string> resolveItem, bool itemsKnown, BsValidation v)
        {
            var result = new List<MineSetDto>();

            if (body.ValueKind != JsonValueKind.Object || !BsJson.TryGet(body, "sets", out var sets) || sets.ValueKind != JsonValueKind.Array)
            {
                v.Error("sets", "must be an array");
                return result;
            }

            if (sets.GetArrayLength() > MaxSets)
            {
                v.Error("sets", "has too many mine sets (max " + MaxSets + ")");
                return result;
            }

            var i = -1;
            foreach (var row in sets.EnumerateArray())
            {
                i++;
                var f = "sets[" + i + "]";

                if (row.ValueKind != JsonValueKind.Object)
                {
                    v.Error(f, "must be an object");
                    continue;
                }

                var dto = new MineSetDto { Index = i + 1 };

                if (BsJson.ReadString(row, "name", 50, v, f + ".name", false, out var name)) dto.Name = name;
                else dto.Name = string.Empty;

                if (BsJson.ReadInt(row, "spotRegenRate", 0, 254, v, f + ".spotRegenRate", true, out var n)) dto.SpotRegenRate = (int)n;
                if (BsJson.ReadInt(row, "maxStones", 0, 255, v, f + ".maxStones", true, out n)) dto.MaxStones = (int)n;
                if (BsJson.ReadInt(row, "hitRate", 0, 255, v, f + ".hitRate", true, out n)) dto.HitRate = (int)n;
                if (BsJson.ReadInt(row, "dropRate", 0, 255, v, f + ".dropRate", true, out n)) dto.DropRate = (int)n;
                if (BsJson.ReadInt(row, "totalSlots", 0, 255, v, f + ".totalSlots", true, out n)) dto.TotalSlots = (int)n;

                if (!BsJson.TryGet(row, "drops", out var drops))
                {
                    // no drops
                }
                else if (drops.ValueKind != JsonValueKind.Array)
                {
                    v.Error(f + ".drops", "must be an array");
                }
                else if (drops.GetArrayLength() > MaxDrops)
                {
                    v.Error(f + ".drops", "has too many rows (max " + MaxDrops + ")");
                }
                else
                {
                    var j = -1;
                    foreach (var drow in drops.EnumerateArray())
                    {
                        j++;
                        var df = f + ".drops[" + j + "]";

                        if (drow.ValueKind != JsonValueKind.Object)
                        {
                            v.Error(df, "must be an object");
                            continue;
                        }

                        var drop = new MineDropDto();
                        var ok = BsJson.ReadString(drow, "itemName", 50, v, df + ".itemName", true, out var itemName);

                        if (ok && itemName.Length == 0)
                        {
                            v.Error(df + ".itemName", "is required");
                            ok = false;
                        }
                        else if (ok)
                        {
                            if (!itemsKnown)
                            {
                                drop.ItemName = itemName.Replace(" ", string.Empty);
                                v.Warn("The item database is not loaded: mine drop item names were not checked.");
                            }
                            else
                            {
                                drop.ItemName = resolveItem?.Invoke(itemName);
                                if (drop.ItemName == null)
                                {
                                    v.Error(df + ".itemName", "\"" + itemName + "\" is not an item in the database");
                                    ok = false;
                                }
                            }
                        }

                        ok &= BsJson.ReadInt(drow, "minSlot", 0, 254, v, df + ".minSlot", true, out var minSlot);
                        ok &= BsJson.ReadInt(drow, "maxSlot", 0, 255, v, df + ".maxSlot", true, out var maxSlot);
                        ok &= BsJson.ReadInt(drow, "minDura", 0, 255, v, df + ".minDura", true, out var minDura);
                        ok &= BsJson.ReadInt(drow, "maxDura", 0, 255, v, df + ".maxDura", true, out var maxDura);
                        ok &= BsJson.ReadInt(drow, "bonusChance", 0, 255, v, df + ".bonusChance", true, out var bonus);
                        ok &= BsJson.ReadInt(drow, "maxBonusDura", 0, 255, v, df + ".maxBonusDura", true, out var maxBonus);
                        if (!ok) continue;

                        if (minSlot > maxSlot)
                        {
                            v.Error(df + ".maxSlot", "must be at least the min slot (" + minSlot + ")");
                            continue;
                        }

                        if (minDura > maxDura)
                        {
                            v.Error(df + ".maxDura", "must be at least the min quality (" + minDura + ")");
                            continue;
                        }

                        if (dto.TotalSlots > 0 && minSlot >= dto.TotalSlots)
                            v.Warn("Mine " + dto.Index + " (" + dto.Name + "): drop " + drop.ItemName + " starts at slot " + minSlot + " but only slots 0-" + (dto.TotalSlots - 1) + " are rolled, so it never drops.");

                        drop.MinSlot = (int)minSlot;
                        drop.MaxSlot = (int)maxSlot;
                        drop.MinDura = (int)minDura;
                        drop.MaxDura = (int)maxDura;
                        drop.BonusChance = (int)bonus;
                        drop.MaxBonusDura = (int)maxBonus;
                        dto.Drops.Add(drop);
                    }
                }

                result.Add(dto);
            }

            // Removing sets from the end is allowed only when no map uses them (maps reference mines by position).
            for (var removed = sets.GetArrayLength() + 1; removed <= currentCount; removed++)
            {
                if (usage != null && usage.TryGetValue(removed, out var maps) && maps != null && maps.Count > 0)
                    v.Error("sets", "mine " + removed + " is still used by " + string.Join(", ", maps.Take(5)) + (maps.Count > 5 ? " and " + (maps.Count - 5) + " more" : string.Empty) + "; it cannot be removed");
            }

            return result;
        }
    }

    public sealed class SpawnOptionDto
    {
        public int UserCount { get; set; }
        public double DelayLoss { get; set; }
    }

    public sealed class SpawnTickDto
    {
        public int BaseSpawnRate { get; set; }
        public List<SpawnOptionDto> Options { get; set; } = new List<SpawnOptionDto>();
    }

    public static class SpawnTickModel
    {
        public const int MaxOptions = 100;

        public static SpawnTickDto ToDto(RespawnTimer timer)
        {
            return new SpawnTickDto
            {
                BaseSpawnRate = timer.BaseSpawnRate,
                Options = timer.Respawn.Select(o => new SpawnOptionDto { UserCount = o.UserCount, DelayLoss = o.DelayLoss }).ToList(),
            };
        }

        /// <summary>
        /// The effective respawn delay multiplier for <paramref name="users"/> online, exactly like RespawnTimer.GetTickSpeed
        /// (the smallest DelayLoss among options whose UserCount ≤ users; 1.0 when none apply).
        /// </summary>
        public static double Multiplier(IEnumerable<SpawnOptionDto> options, int users)
        {
            var bonus = 1.0;
            foreach (var o in options ?? Enumerable.Empty<SpawnOptionDto>())
                if (o.UserCount <= users) bonus = Math.Min(bonus, o.DelayLoss);
            return bonus;
        }

        /// <summary>
        /// Parses <c>{baseSpawnRate, options:[{userCount, delayLoss}]}</c>. SystemInfoForm rules (users ≥ 0, delay loss in (0, 1]) plus a
        /// base rate of at least 1 minute (0 makes every game tick a respawn tick).
        /// </summary>
        public static SpawnTickDto Parse(JsonElement body, BsValidation v)
        {
            var dto = new SpawnTickDto();

            if (body.ValueKind != JsonValueKind.Object)
            {
                v.Error("body", "must be a JSON object");
                return dto;
            }

            if (BsJson.ReadInt(body, "baseSpawnRate", 1, 255, v, "baseSpawnRate", true, out var rate)) dto.BaseSpawnRate = (int)rate;

            if (!BsJson.TryGet(body, "options", out var options) || options.ValueKind != JsonValueKind.Array)
            {
                v.Error("options", "must be an array");
                return dto;
            }

            if (options.GetArrayLength() > MaxOptions)
            {
                v.Error("options", "has too many rows (max " + MaxOptions + ")");
                return dto;
            }

            var i = -1;
            var seen = new HashSet<int>();
            foreach (var row in options.EnumerateArray())
            {
                i++;
                var f = "options[" + i + "]";

                if (row.ValueKind != JsonValueKind.Object)
                {
                    v.Error(f, "must be an object");
                    continue;
                }

                var okUsers = BsJson.ReadInt(row, "userCount", 0, int.MaxValue, v, f + ".userCount", true, out var users);
                var okLoss = BsJson.ReadNumber(row, "delayLoss", 0, 1, v, f + ".delayLoss", true, out var loss, minExclusive: true);
                if (!okUsers || !okLoss) continue;

                if (!seen.Add((int)users)) v.Warn("Two options start at " + users + " users; the smaller delay multiplier wins.");
                dto.Options.Add(new SpawnOptionDto { UserCount = (int)users, DelayLoss = loss });
            }

            return dto;
        }
    }
}

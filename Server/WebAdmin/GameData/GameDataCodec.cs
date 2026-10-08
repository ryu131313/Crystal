using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Server.WebAdmin.GameData
{
    /// <summary>
    /// Typed value ⇄ JSON row value conversion, equality, canonical text and the row revision (<c>__rev</c>).
    /// Wire format: enums as numbers ([Flags] enums as the UNSIGNED value of their underlying type, so BindMode.NoHero is 32768 not
    /// -32768 and the grid's bit editor works), List&lt;int&gt; as "1, 2, 3", DateTime as ISO-8601, null strings as "".
    /// </summary>
    internal static class GameDataCodec
    {
        public const string RevKey = "__rev";

        // ------------------------------------------------------------------ typed → wire

        public static object ToWire(GameDataColumn column, object value)
        {
            return ToWire(column.ValueType, value, column.IsFlags);
        }

        public static object ToWire(Type type, object value, bool flags)
        {
            if (type == typeof(List<int>))
            {
                var list = value as List<int>;
                return list == null || list.Count == 0 ? string.Empty : string.Join(", ", list.Select(i => i.ToString(CultureInfo.InvariantCulture)));
            }

            if (type == typeof(string)) return value as string ?? string.Empty;

            if (type.IsEnum)
            {
                if (value == null) return 0L;
                return flags ? (object)EnumUnsigned(type, value) : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }

            if (type == typeof(DateTime))
            {
                var dt = value is DateTime d ? d : DateTime.MinValue;
                return dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            }

            return value;
        }

        /// <summary>Canonical text of a wire value (used for __rev and audit).</summary>
        public static string Canonical(object wire)
        {
            switch (wire)
            {
                case null:
                    return string.Empty;
                case string s:
                    return s;
                case bool b:
                    return b ? "true" : "false";
                case float f:
                    return f.ToString("R", CultureInfo.InvariantCulture);
                case double d:
                    return d.ToString("R", CultureInfo.InvariantCulture);
                case IFormattable fmt:
                    return fmt.ToString(null, CultureInfo.InvariantCulture);
                default:
                    return wire.ToString();
            }
        }

        /// <summary>FNV-1a 64-bit over the row's keys (ordinal order, "__" meta keys skipped) and canonical values, as 16 hex chars.</summary>
        public static string Rev(IReadOnlyDictionary<string, object> row)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            var hash = offset;
            foreach (var key in row.Keys.Where(k => !k.StartsWith("__", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal))
            {
                var text = key + "=" + Canonical(row[key]) + "\u001f";
                foreach (var b in Encoding.UTF8.GetBytes(text))
                {
                    hash ^= b;
                    hash *= prime;
                }
            }

            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ equality

        public static bool ValuesEqual(Type type, object a, object b)
        {
            if (type == typeof(List<int>))
            {
                var la = a as List<int> ?? new List<int>();
                var lb = b as List<int> ?? new List<int>();
                return la.SequenceEqual(lb);
            }

            if (type == typeof(string)) return string.Equals(a as string ?? string.Empty, b as string ?? string.Empty, StringComparison.Ordinal);

            if (type == typeof(float) && a is float fa && b is float fb) return fa.Equals(fb);

            // The wire carries whole seconds: a round-tripped value must not count as a change of its sub-second part.
            if (type == typeof(DateTime) && a is DateTime da && b is DateTime db)
                return da.Ticks / TimeSpan.TicksPerSecond == db.Ticks / TimeSpan.TicksPerSecond;

            return Equals(a, b);
        }

        // ------------------------------------------------------------------ wire → typed

        /// <summary>Converts a JSON value to the column's CLR type with range / enum-membership checks. Error text is user facing.</summary>
        public static bool TryConvert(JsonElement value, GameDataColumn column, out object result, out string error)
        {
            return TryConvert(value, column.ValueType, column.IsFlags, out result, out error);
        }

        public static bool TryConvert(JsonElement value, Type type, bool flags, out object result, out string error)
        {
            result = null;
            error = null;

            if (type == typeof(string))
            {
                switch (value.ValueKind)
                {
                    case JsonValueKind.String:
                        result = value.GetString() ?? string.Empty;
                        return true;
                    case JsonValueKind.Null:
                        result = string.Empty;
                        return true;
                    case JsonValueKind.Number:
                        result = value.GetRawText();
                        return true;
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        result = value.GetRawText();
                        return true;
                    default:
                        error = "must be text";
                        return false;
                }
            }

            if (type == typeof(bool))
            {
                switch (value.ValueKind)
                {
                    case JsonValueKind.True:
                        result = true;
                        return true;
                    case JsonValueKind.False:
                        result = false;
                        return true;
                    case JsonValueKind.Number when value.TryGetInt64(out var n) && (n == 0 || n == 1):
                        result = n == 1;
                        return true;
                    case JsonValueKind.String:
                        var s = (value.GetString() ?? string.Empty).Trim();
                        if (bool.TryParse(s, out var b))
                        {
                            result = b;
                            return true;
                        }

                        if (s == "1" || s == "0")
                        {
                            result = s == "1";
                            return true;
                        }

                        break;
                }

                error = "must be true or false";
                return false;
            }

            if (type == typeof(List<int>))
                return TryConvertList(value, out result, out error);

            if (type == typeof(DateTime))
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    var s = (value.GetString() ?? string.Empty).Trim();
                    if (s.Length == 0)
                    {
                        result = DateTime.MinValue;
                        return true;
                    }

                    if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out var dt))
                    {
                        if (dt.Kind == DateTimeKind.Utc) dt = dt.ToLocalTime();
                        result = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
                        return true;
                    }
                }
                else if (value.ValueKind == JsonValueKind.Null)
                {
                    result = DateTime.MinValue;
                    return true;
                }

                error = "must be a date (yyyy-MM-ddTHH:mm:ss)";
                return false;
            }

            if (type.IsEnum)
                return TryConvertEnum(value, type, flags, out result, out error);

            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            {
                if (!TryGetDouble(value, out var d) || double.IsNaN(d) || double.IsInfinity(d))
                {
                    error = "must be a number";
                    return false;
                }

                if (type == typeof(float))
                {
                    if (Math.Abs(d) > float.MaxValue)
                    {
                        error = "is out of range";
                        return false;
                    }

                    result = (float)d;
                }
                else if (type == typeof(double)) result = d;
                else result = (decimal)d;

                return true;
            }

            if (IsInteger(type))
            {
                if (!TryGetInteger(value, out var n, out var notWhole))
                {
                    error = notWhole ? "must be a whole number" : "must be a number";
                    return false;
                }

                if (!IntegerRange(type, out var min, out var max))
                {
                    error = "unsupported type";
                    return false;
                }

                if (n < min || n > max)
                {
                    error = "must be between " + min.ToString(CultureInfo.InvariantCulture) + " and " + max.ToString(CultureInfo.InvariantCulture) + " (" + type.Name + ")";
                    return false;
                }

                result = Convert.ChangeType(n, type, CultureInfo.InvariantCulture);
                return true;
            }

            error = "unsupported type " + type.Name;
            return false;
        }

        /// <summary>"1, 2 3;4" or [1,2,3] → List&lt;int&gt; (empty text → empty list).</summary>
        public static bool TryConvertList(JsonElement value, out object result, out string error)
        {
            result = null;
            error = null;
            var list = new List<int>();

            if (value.ValueKind == JsonValueKind.Null)
            {
                result = list;
                return true;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (!TryGetInteger(item, out var n, out _) || n < int.MinValue || n > int.MaxValue)
                    {
                        error = "must be a list of whole numbers";
                        return false;
                    }

                    list.Add((int)n);
                }

                result = list;
                return true;
            }

            if (value.ValueKind == JsonValueKind.Number)
            {
                if (!value.TryGetInt32(out var single))
                {
                    error = "must be a list of whole numbers";
                    return false;
                }

                list.Add(single);
                result = list;
                return true;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                error = "must be a list of whole numbers (e.g. 1, 2, 3)";
                return false;
            }

            if (!TryParseIntList(value.GetString(), out list))
            {
                error = "must be a list of whole numbers (e.g. 1, 2, 3)";
                return false;
            }

            result = list;
            return true;
        }

        public static bool TryParseIntList(string text, out List<int> list)
        {
            list = new List<int>();
            if (string.IsNullOrWhiteSpace(text)) return true;

            foreach (var part in text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return false;
                list.Add(n);
            }

            return true;
        }

        private static bool TryConvertEnum(JsonElement value, Type type, bool flags, out object result, out string error)
        {
            result = null;
            error = null;
            var underlying = Enum.GetUnderlyingType(type);
            var bits = BitSize(underlying);
            var signed = underlying == typeof(sbyte) || underlying == typeof(short) || underlying == typeof(int) || underlying == typeof(long);

            long number;
            if (value.ValueKind == JsonValueKind.Number || (value.ValueKind == JsonValueKind.String && LooksNumeric(value.GetString())))
            {
                if (!TryGetInteger(value, out number, out _))
                {
                    error = "must be one of " + MemberList(type);
                    return false;
                }
            }
            else if (value.ValueKind == JsonValueKind.String)
            {
                var text = (value.GetString() ?? string.Empty).Trim();
                if (flags)
                {
                    if (!TryParseFlagNames(type, text, out number))
                    {
                        error = "unknown flag in \"" + text + "\" (members: " + MemberList(type) + ")";
                        return false;
                    }
                }
                else
                {
                    var name = Enum.GetNames(type).FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
                    if (name == null)
                    {
                        error = "\"" + text + "\" is not one of " + MemberList(type);
                        return false;
                    }

                    number = Convert.ToInt64(Enum.Parse(type, name), CultureInfo.InvariantCulture);
                }
            }
            else
            {
                error = "must be one of " + MemberList(type);
                return false;
            }

            // Accept both the signed and the unsigned spelling of the underlying type (flags travel unsigned).
            long signedMin = signed ? -(1L << (bits - 1)) : 0;
            long unsignedMax = bits >= 64 ? long.MaxValue : (1L << bits) - 1;
            if (number < signedMin || number > unsignedMax)
            {
                error = "is out of range for " + type.Name;
                return false;
            }

            var masked = bits >= 64 ? number : number & unsignedMax;

            if (flags)
            {
                var allBits = AllFlagBits(type);
                if ((masked & ~allBits) != 0)
                {
                    error = "contains bits that are not members of " + type.Name + " (" + MemberList(type) + ")";
                    return false;
                }
            }

            var typed = Enum.ToObject(type, ToUnderlying(masked, underlying));
            if (!flags && !Enum.IsDefined(type, typed))
            {
                error = number.ToString(CultureInfo.InvariantCulture) + " is not a member of " + type.Name;
                return false;
            }

            result = typed;
            return true;
        }

        private static bool LooksNumeric(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            return long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        }

        public static bool TryParseFlagNames(Type type, string text, out long value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return true;

            var names = Enum.GetNames(type);
            foreach (var part in text.Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = part.Trim();
                if (p.Length == 0) continue;
                var name = names.FirstOrDefault(n => string.Equals(n, p, StringComparison.OrdinalIgnoreCase));
                if (name == null) return false;
                value |= EnumUnsigned(type, Enum.Parse(type, name));
            }

            return true;
        }

        /// <summary>The OR of every member's unsigned value.</summary>
        public static long AllFlagBits(Type enumType)
        {
            long all = 0;
            foreach (var v in Enum.GetValues(enumType)) all |= EnumUnsigned(enumType, v);
            return all;
        }

        /// <summary>The enum value as the unsigned number of its underlying type (short -32768 → 32768).</summary>
        public static long EnumUnsigned(Type enumType, object value)
        {
            var underlying = Enum.GetUnderlyingType(enumType);
            var bits = BitSize(underlying);
            var n = Convert.ToInt64(Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            return bits >= 64 ? n : n & ((1L << bits) - 1);
        }

        private static object ToUnderlying(long masked, Type underlying)
        {
            unchecked
            {
                if (underlying == typeof(byte)) return (byte)masked;
                if (underlying == typeof(sbyte)) return (sbyte)masked;
                if (underlying == typeof(short)) return (short)masked;
                if (underlying == typeof(ushort)) return (ushort)masked;
                if (underlying == typeof(int)) return (int)masked;
                if (underlying == typeof(uint)) return (uint)masked;
                if (underlying == typeof(ulong)) return (ulong)masked;
                return masked;
            }
        }

        private static int BitSize(Type t)
        {
            if (t == typeof(byte) || t == typeof(sbyte)) return 8;
            if (t == typeof(short) || t == typeof(ushort)) return 16;
            if (t == typeof(int) || t == typeof(uint)) return 32;
            return 64;
        }

        private static string MemberList(Type type)
        {
            var names = Enum.GetNames(type);
            return names.Length > 12 ? string.Join(", ", names.Take(12)) + ", …" : string.Join(", ", names);
        }

        public static bool IsInteger(Type t)
        {
            return t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) || t == typeof(int) ||
                   t == typeof(uint) || t == typeof(long) || t == typeof(ulong);
        }

        public static bool IntegerRange(Type t, out long min, out long max)
        {
            min = 0;
            max = 0;
            if (t == typeof(byte)) { min = byte.MinValue; max = byte.MaxValue; return true; }
            if (t == typeof(sbyte)) { min = sbyte.MinValue; max = sbyte.MaxValue; return true; }
            if (t == typeof(short)) { min = short.MinValue; max = short.MaxValue; return true; }
            if (t == typeof(ushort)) { min = ushort.MinValue; max = ushort.MaxValue; return true; }
            if (t == typeof(int)) { min = int.MinValue; max = int.MaxValue; return true; }
            if (t == typeof(uint)) { min = uint.MinValue; max = uint.MaxValue; return true; }
            if (t == typeof(long)) { min = long.MinValue; max = long.MaxValue; return true; }
            if (t == typeof(ulong)) { min = 0; max = long.MaxValue; return true; }
            return false;
        }

        /// <summary>Whole number from a JSON number or numeric string. <paramref name="notWhole"/> is set for 1.5.</summary>
        public static bool TryGetInteger(JsonElement value, out long number, out bool notWhole)
        {
            number = 0;
            notWhole = false;

            if (value.ValueKind == JsonValueKind.Number)
            {
                if (value.TryGetInt64(out number)) return true;
                if (value.TryGetDouble(out var d))
                {
                    if (Math.Floor(d) == d && d >= long.MinValue && d <= long.MaxValue)
                    {
                        number = (long)d;
                        return true;
                    }

                    notWhole = true;
                }

                return false;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var s = (value.GetString() ?? string.Empty).Trim();
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return true;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    if (Math.Floor(d) == d && d >= long.MinValue && d <= long.MaxValue)
                    {
                        number = (long)d;
                        return true;
                    }

                    notWhole = true;
                }
            }

            return false;
        }

        private static bool TryGetDouble(JsonElement value, out double d)
        {
            d = 0;
            if (value.ValueKind == JsonValueKind.Number) return value.TryGetDouble(out d);
            if (value.ValueKind == JsonValueKind.String)
                return double.TryParse((value.GetString() ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
            return false;
        }

        /// <summary>Whole-number view of a row's Index (or any int key); false when absent / not a number.</summary>
        public static bool TryGetIndex(JsonElement row, string key, out int index)
        {
            index = 0;
            if (!row.TryGetPropertyIgnoreCase(key, out var v)) return false;
            if (!TryGetInteger(v, out var n, out _)) return false;
            if (n < int.MinValue || n > int.MaxValue) return false;
            index = (int)n;
            return true;
        }
    }
}

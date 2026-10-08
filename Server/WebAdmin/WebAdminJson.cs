using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Server.WebAdmin
{
    /// <summary>
    /// Shared System.Text.Json configuration:
    /// camelCase property names for DTOs / anonymous objects, dictionary keys NOT renamed (grid rows keep C# field names),
    /// public fields included, enums as numbers, DateTime as ISO-8601, null DTO members omitted, case-insensitive reads.
    /// Never serialize raw Crystal objects (they contain cycles); project into DTOs / dictionaries first.
    /// </summary>
    public static class WebAdminJson
    {
        public static readonly JsonSerializerOptions Options = CreateOptions();

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DictionaryKeyPolicy = null,
                IncludeFields = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                MaxDepth = 64,
                // Keeps non-ASCII (e.g. CJK names) readable while still escaping HTML-sensitive characters (<, >, &, ').
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            };

            return options;
        }

        public static string Serialize(object value)
        {
            return JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), Options);
        }

        public static byte[] SerializeToUtf8Bytes(object value)
        {
            return JsonSerializer.SerializeToUtf8Bytes(value, value?.GetType() ?? typeof(object), Options);
        }

        /// <summary>Deserializes; throws <see cref="WebAdminException"/> (400) on malformed JSON.</summary>
        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return default;

            try
            {
                return JsonSerializer.Deserialize<T>(json, Options);
            }
            catch (JsonException)
            {
                throw new WebAdminException(400, "Invalid JSON body");
            }
            catch (NotSupportedException)
            {
                throw new WebAdminException(400, "Unsupported JSON body");
            }
        }

        /// <summary>Parses to a detached JsonElement (default/Undefined for an empty body); 400 on malformed JSON.</summary>
        public static JsonElement Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return default;

            try
            {
                using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 }))
                    return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new WebAdminException(400, "Invalid JSON body");
            }
        }

        /// <summary>Serializes any value to a detached JsonElement (used to snapshot audit details).</summary>
        public static JsonElement ToElement(object value)
        {
            if (value == null) return default;
            if (value is JsonElement element) return element.Clone();
            return JsonSerializer.SerializeToElement(value, value.GetType(), Options);
        }

        public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        // ---- JsonElement convenience accessors (missing / wrong kind → fallback) ----

        public static string GetString(this JsonElement obj, string name, string fallback = null)
        {
            if (!TryGetPropertyIgnoreCase(obj, name, out var v)) return fallback;
            switch (v.ValueKind)
            {
                case JsonValueKind.String:
                    return v.GetString();
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return v.GetRawText();
                default:
                    return fallback;
            }
        }

        public static int GetInt(this JsonElement obj, string name, int fallback = 0)
        {
            if (!TryGetPropertyIgnoreCase(obj, name, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out i)) return i;
            return fallback;
        }

        public static long GetLong(this JsonElement obj, string name, long fallback = 0)
        {
            if (!TryGetPropertyIgnoreCase(obj, name, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i)) return i;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out i)) return i;
            return fallback;
        }

        public static bool GetBool(this JsonElement obj, string name, bool fallback = false)
        {
            if (!TryGetPropertyIgnoreCase(obj, name, out var v)) return fallback;
            switch (v.ValueKind)
            {
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Number:
                    return v.TryGetInt64(out var n) ? n != 0 : fallback;
                case JsonValueKind.String:
                    var s = v.GetString();
                    if (bool.TryParse(s, out var b)) return b;
                    if (s == "1") return true;
                    if (s == "0") return false;
                    return fallback;
                default:
                    return fallback;
            }
        }

        /// <summary>Case-insensitive property lookup on an object element.</summary>
        public static bool TryGetPropertyIgnoreCase(this JsonElement obj, string name, out JsonElement value)
        {
            value = default;
            if (obj.ValueKind != JsonValueKind.Object || string.IsNullOrEmpty(name)) return false;

            if (obj.TryGetProperty(name, out value)) return true;

            foreach (var p in obj.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }
    }
}

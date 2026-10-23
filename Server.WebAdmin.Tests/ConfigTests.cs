using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Server.WebAdmin.Routes;

namespace Server.WebAdmin.Tests
{
    /// <summary>
    /// Server Config (B6): the Setup.ini catalogue and the pure value codec. Settings is only inspected through metadata
    /// (typeof(Settings).GetField / GetProperty, never GetValue), so its static constructor — which opens Configs\Setup.ini —
    /// never runs here.
    /// </summary>
    public class ConfigTests
    {
        private const BindingFlags StaticPublic = BindingFlags.Public | BindingFlags.Static;

        private static SettingDescriptor Field(string name)
        {
            var d = SettingsCatalog.ByField(name);
            Assert.NotNull(d);
            return d;
        }

        private static object Convert(SettingDescriptor d, string text)
        {
            Assert.True(SettingsValueCodec.TryConvert(d, text, out var value, out var error), d.Field + " '" + text + "': " + error);
            return value;
        }

        private static string Reject(SettingDescriptor d, string text)
        {
            Assert.False(SettingsValueCodec.TryConvert(d, text, out _, out var error), d.Field + " '" + text + "' should be rejected");
            Assert.False(string.IsNullOrEmpty(error));
            return error;
        }

        private static JsonElement Json(string json)
        {
            using (var doc = JsonDocument.Parse(json))
                return doc.RootElement.Clone();
        }

        // ------------------------------------------------------------------ catalogue vs Settings (metadata only)

        [Fact]
        public void Catalog_EveryMemberExistsOnSettings_WithTheDeclaredType()
        {
            Assert.NotEmpty(SettingsCatalog.All);

            foreach (var d in SettingsCatalog.All)
            {
                Type actual;
                if (d.IsProperty)
                {
                    var p = typeof(Settings).GetProperty(d.Field, StaticPublic);
                    Assert.True(p != null, "Settings." + d.Field + " (property) is missing");
                    Assert.NotNull(p.GetSetMethod(true));
                    actual = p.PropertyType;
                }
                else
                {
                    var f = typeof(Settings).GetField(d.Field, StaticPublic);
                    Assert.True(f != null, "Settings." + d.Field + " (field) is missing");
                    Assert.False(f.IsInitOnly, "Settings." + d.Field + " is readonly");
                    Assert.False(f.IsLiteral, "Settings." + d.Field + " is const");
                    actual = f.FieldType;
                }

                Assert.True(d.Type == actual, d + ": catalogue says " + d.Type.Name + ", Settings has " + actual.Name);
            }
        }

        [Fact]
        public void Catalog_HasNoDuplicateFieldsOrKeys_AndCompleteMetadata()
        {
            var fields = SettingsCatalog.All.Select(d => d.Field).ToList();
            Assert.Equal(fields.Count, fields.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            var keys = SettingsCatalog.All.Select(d => d.Section + "/" + d.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            foreach (var d in SettingsCatalog.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(d.Group), d + " has no group");
                Assert.False(string.IsNullOrWhiteSpace(d.Label), d + " has no label");
                Assert.False(string.IsNullOrWhiteSpace(d.Help), d + " has no help");
                Assert.Contains(d.Kind, new[] { "bool", "int", "float", "string", "enum" }); // enum: [Optimisation] OfflineAccountCache (P9-12)
                if (d.Format != null) Assert.Equal(typeof(string), d.Type);
                if (d.Min.HasValue && d.Max.HasValue) Assert.True(d.Min.Value <= d.Max.Value, d + " min > max");
            }

            Assert.Same(SettingsCatalog.ByField("dropRate"), SettingsCatalog.ByKey("game", "droprate"));
            Assert.Null(SettingsCatalog.ByField("NoSuchSetting"));
            Assert.Null(SettingsCatalog.ByField(null));
        }

        [Fact]
        public void Catalog_RestartAndSecretFlags()
        {
            foreach (var d in SettingsCatalog.All.Where(x => x.Section == "WebAdmin"))
                Assert.Equal(SettingRestart.Process, d.Restart);

            Assert.Equal(SettingRestart.World, Field("IPAddress").Restart);
            Assert.Equal(SettingRestart.World, Field("Port").Restart);
            Assert.Equal(SettingRestart.World, Field("Multithreaded").Restart);
            Assert.Equal(SettingRestart.World, Field("StartHTTPService").Restart);
            Assert.Equal(SettingRestart.World, Field("HTTPIPAddress").Restart);
            Assert.Equal(SettingRestart.Process, Field("ThreadLimit").Restart);
            Assert.Equal(SettingRestart.Process, Field("Language").Restart);

            Assert.False(Field("DropRate").RestartRequired);
            Assert.False(Field("ExpRate").RestartRequired);
            Assert.False(Field("AllowLogin").RestartRequired);
            Assert.Equal("none", Field("AllowLogin").RestartName);
            Assert.Equal("world", Field("Port").RestartName);
            Assert.Equal("process", Field("WebAdminPort").RestartName);

            Assert.True(Field("GMPassword").Secret);
            Assert.Single(SettingsCatalog.All, d => d.Secret);

            // Save() writes these since 2026-10-24 (they were load-only and the route wrote them itself).
            foreach (var name in new[] { "GMPassword", "MaxPacket", "MonsterRecallEnabled", "MonsterRecallRange", "MonsterRecallCooldown" })
                Assert.True(Field(name).SavedBySettingsSave, name);
            Assert.True(Field("DropRate").SavedBySettingsSave);

            Assert.True(SettingsCatalog.IsVersionKey(Field("VersionPath")));
            Assert.True(SettingsCatalog.IsVersionKey(Field("CheckVersion")));
            Assert.False(SettingsCatalog.IsVersionKey(Field("Port")));

            Assert.True(Field("GroupInviteDelay").IsProperty);
            Assert.True(Field("TradeDelay").IsProperty);
            Assert.Equal("Rested", Field("RestedPeriod").Section);
            Assert.Equal("Period", Field("RestedPeriod").Key);
            Assert.Equal("MaxResolution", Field("AllowedResolution").Key);
        }

        [Fact]
        public void Catalog_Groups_AreInCatalogueOrder()
        {
            var groups = SettingsCatalog.Groups();
            Assert.Equal(SettingsCatalog.GroupGeneral, groups[0]);
            Assert.Contains(SettingsCatalog.GroupWebAdmin, groups);
            Assert.Contains(SettingsCatalog.GroupMonsters, groups);
            Assert.Equal(groups.Count, groups.Distinct().Count());
            Assert.All(SettingsCatalog.All, d => Assert.Contains(d.Group, groups));
        }

        /// <summary>
        /// Reads Server\Settings.cs (found by walking up from the test output folder) and checks the catalogue against every
        /// <c>Reader.ReadX("Section", "Key", ...)</c> in Load() and every <c>Reader.Write("Section", "Key", ...)</c> in Save():
        /// same field, nothing missing, SavedBySettingsSave accurate. Skipped silently when the source tree is not present.
        /// </summary>
        [Fact]
        public void Catalog_MatchesSettingsLoadAndSaveSource()
        {
            var source = FindSettingsSource();
            if (source == null) return;

            var text = File.ReadAllText(source);
            var load = Slice(text, "public static void Load()", "public static void LoadNotice()");
            var save = Slice(text, "public static void Save()", "public static void LoadEXP()");
            Assert.False(string.IsNullOrEmpty(load), "Load() not found");
            Assert.False(string.IsNullOrEmpty(save), "Save() not found");

            var readRx = new Regex(@"(\w+)\s*=\s*(?:Math\.Max\([^,]+,\s*)?Reader\.Read\w+\(\s*""(\w+)""\s*,\s*""(\w+)""");
            var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reads = 0;
            foreach (Match m in readRx.Matches(load))
            {
                reads++;
                var field = m.Groups[1].Value;
                var section = m.Groups[2].Value;
                var key = m.Groups[3].Value;
                loaded.Add(section + "/" + key);

                if (SettingsCatalog.ExcludedKeys.Any(e => e.Section == section && e.Key == key)) continue;

                var d = SettingsCatalog.ByKey(section, key);
                Assert.True(d != null, "Setup.ini [" + section + "] " + key + " is read by Settings.Load() but missing from SettingsCatalog");
                Assert.True(string.Equals(d.Field, field, StringComparison.Ordinal), "[" + section + "] " + key + " loads into " + field + " but the catalogue says " + d.Field);
            }

            Assert.True(reads > 100, "suspiciously few Reader.Read calls parsed: " + reads);

            var writeRx = new Regex(@"Reader\.Write\(\s*""(\w+)""\s*,\s*""(\w+)""");
            var saved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in writeRx.Matches(save))
                saved.Add(m.Groups[1].Value + "/" + m.Groups[2].Value);

            foreach (var d in SettingsCatalog.All)
            {
                var id = d.Section + "/" + d.Key;
                Assert.True(loaded.Contains(id), d + " is not read by Settings.Load()");
                Assert.True(saved.Contains(id) == d.SavedBySettingsSave, d + ": SavedBySettingsSave=" + d.SavedBySettingsSave + " but Save() " + (saved.Contains(id) ? "writes" : "does not write") + " it");
            }
        }

        private static string FindSettingsSource()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 10 && dir != null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Server", "Settings.cs");
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        private static string Slice(string text, string start, string end)
        {
            var a = text.IndexOf(start, StringComparison.Ordinal);
            if (a < 0) return null;
            var b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
            return b < 0 ? text.Substring(a) : text.Substring(a, b - a);
        }

        // ------------------------------------------------------------------ codec: numbers / bools

        [Fact]
        public void Bool_AcceptsCommonSpellings_RejectsOthers()
        {
            var d = Field("AllowLogin");
            Assert.Equal(true, Convert(d, "true"));
            Assert.Equal(true, Convert(d, " TRUE "));
            Assert.Equal(true, Convert(d, "1"));
            Assert.Equal(true, Convert(d, "on"));
            Assert.Equal(false, Convert(d, "False"));
            Assert.Equal(false, Convert(d, "0"));
            Assert.Equal(false, Convert(d, "no"));
            Reject(d, "maybe");
            Reject(d, "");

            Assert.True(SettingsValueCodec.TryConvert(d, Json("true"), out var v, out _));
            Assert.Equal(true, v);
            Assert.True(SettingsValueCodec.TryConvert(d, Json("false"), out v, out _));
            Assert.Equal(false, v);
        }

        [Fact]
        public void Integers_UseTheClrRangeAndDescriptorBounds()
        {
            var port = Field("Port");                    // ushort, 1..65535
            Assert.IsType<ushort>(Convert(port, "7000"));
            Assert.Equal((ushort)65535, Convert(port, "65535"));
            Reject(port, "65536");
            Reject(port, "0");
            Reject(port, "-1");
            Reject(port, "70.5");
            Reject(port, "abc");
            Assert.Equal((ushort)7100, Convert(port, "7100.0"));   // a whole number written as decimal is fine

            var weight = Field("RangeAccuracyBonus");    // byte
            Assert.IsType<byte>(Convert(weight, "255"));
            Reject(weight, "256");

            var credx = Field("CredxGold");              // short, min 0
            Assert.IsType<short>(Convert(credx, "30"));
            Reject(credx, "-5");
            Reject(credx, "40000");

            var gold = Field("MaxDropGold");             // uint, min 1 (divisor in MonsterObject)
            Assert.IsType<uint>(Convert(gold, "4000000000"));
            Assert.Contains("between 1", Reject(gold, "0"));

            var delay = Field("GroupInviteDelay");       // long property
            Assert.IsType<long>(Convert(delay, "2000"));
            Reject(delay, "99999999999");

            Assert.Equal((double?)1, Field("CriticalDamageWeight").Min);
            Reject(Field("CriticalDamageWeight"), "0");
            Reject(Field("ThreadLimit"), "0");
            Reject(Field("SaveDelay"), "0");
            Reject(Field("LineMessageTimer"), "0");
            Reject(Field("ArchiveDeletedCharacterAfterMonths"), "0");
            Assert.Equal(0, Convert(Field("MaxBossTames"), "0"));
            Reject(Field("MaxBossTames"), "-1");

            Assert.True(SettingsValueCodec.TryConvert(port, Json("7200"), out var v, out _));
            Assert.Equal((ushort)7200, v);
            Assert.True(SettingsValueCodec.TryConvert(port, Json("\"7300\""), out v, out _));
            Assert.Equal((ushort)7300, v);
            Assert.False(SettingsValueCodec.TryConvert(port, Json("null"), out _, out var err));
            Assert.Equal("Value is required", err);
            Assert.False(SettingsValueCodec.TryConvert(port, Json("[1]"), out _, out _));
        }

        [Fact]
        public void ServerRulesSettings_AreGameplayEntries_WithBounds()
        {
            // [Game] MapDetailsEnabled / MaxFriends (phase 2.2, 2026-10-16): sent to clients in S.ServerRules.
            var map = Field("MapDetailsEnabled");
            Assert.Equal(typeof(bool), map.Type);
            Assert.Equal(SettingsCatalog.GroupGameplay, map.Group);
            Assert.Same(map, SettingsCatalog.ByKey("Game", "MapDetailsEnabled"));
            Assert.Equal(false, Convert(map, "false"));

            var friends = Field("MaxFriends");
            Assert.Equal(typeof(int), friends.Type);
            Assert.Equal(SettingsCatalog.GroupGameplay, friends.Group);
            Assert.Same(friends, SettingsCatalog.ByKey("Game", "MaxFriends"));
            Assert.Equal(0, Convert(friends, "0"));
            Assert.Equal(1000, Convert(friends, "1000"));
            Reject(friends, "-1");
            Reject(friends, "1001");
        }

        [Fact]
        public void Floats_AreInvariant_AndBounded()
        {
            var rate = Field("DropRate");               // float, 0.01..1000 (divisor)
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal(1.5f, Convert(rate, "1.5"));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }

            Assert.IsType<float>(Convert(rate, "2"));
            Reject(rate, "0");
            Reject(rate, "-1");
            Reject(rate, "1001");
            Reject(rate, "NaN");
            Reject(rate, "1e40");
            Reject(rate, "fast");
            Assert.Equal(0f, Convert(Field("ExpRate"), "0"));

            Assert.True(SettingsValueCodec.TryConvert(rate, Json("2.25"), out var v, out _));
            Assert.Equal(2.25f, v);
        }

        // ------------------------------------------------------------------ codec: strings

        [Fact]
        public void Strings_RespectInIReaderLimits()
        {
            var guild = Field("NewbieGuild");
            Assert.Equal("Newbies", Convert(guild, "  Newbies  "));
            Assert.Contains("empty", Reject(guild, "   "));
            Assert.Contains("'='", Reject(guild, "a=b"));
            Reject(guild, "line1\nline2");
            Reject(guild, "tab\there");
            Reject(guild, new string('x', SettingsValueCodec.MaxStringLength + 1));

            // secrets keep their spaces but cannot be blank
            var gm = Field("GMPassword");
            Assert.Equal(" pass word ", Convert(gm, " pass word "));
            Reject(gm, "   ");
            Reject(gm, "p=w");
        }

        [Fact]
        public void IpFormat()
        {
            var ip = Field("IPAddress");
            Assert.Equal("127.0.0.1", Convert(ip, "127.0.0.1"));
            Assert.Equal("0.0.0.0", Convert(ip, "0.0.0.0"));
            Assert.Equal("::1", Convert(ip, "::1"));
            Reject(ip, "127.1");
            Reject(ip, "localhost");
            Reject(ip, "300.1.1.1");
            Assert.Equal("10.0.0.5", Convert(Field("HTTPTrustedIPAddress"), "10.0.0.5"));
        }

        [Fact]
        public void BindFormat()
        {
            var bind = Field("WebAdminBind");
            Assert.Equal("127.0.0.1", Convert(bind, "127.0.0.1"));
            Assert.Equal("*", Convert(bind, "*"));
            Assert.Equal("+", Convert(bind, "+"));
            Assert.Equal("localhost", Convert(bind, "localhost"));
            Assert.Equal("admin.example.com", Convert(bind, "admin.example.com"));
            Reject(bind, "http://127.0.0.1");
            Reject(bind, "two words");
            Reject(bind, "-bad-.com");
        }

        [Fact]
        public void UrlPrefixFormat()
        {
            var prefix = Field("HTTPIPAddress");
            Assert.Equal("http://127.0.0.1:5679/", Convert(prefix, "http://127.0.0.1:5679/"));
            Assert.Equal("http://+:5679/", Convert(prefix, "http://+:5679/"));
            Assert.Equal("https://example.com/mir/", Convert(prefix, "https://example.com/mir/"));
            Assert.Equal("http://[::1]:8080/", Convert(prefix, "http://[::1]:8080/"));
            Reject(prefix, "http://127.0.0.1:5679");      // no trailing slash
            Reject(prefix, "ftp://127.0.0.1/");
            Reject(prefix, "127.0.0.1:5679/");
            Reject(prefix, "http://127.0.0.1:99999/");
            Reject(prefix, "http:///");
        }

        [Fact]
        public void PathFormats()
        {
            var versions = Field("VersionPath");
            Assert.Equal(@".\Mir2.Exe", Convert(versions, @".\Mir2.Exe"));
            Assert.Equal(@".\Mir2.Exe,C:\Client\Mir2.exe", Convert(versions, @".\Mir2.Exe, C:\Client\Mir2.exe"));   // LoadVersion does not trim
            Reject(versions, @".\a.exe,,b.exe");
            Reject(versions, @"C:\bad|name.exe");
            Reject(versions, @"C:\a:b.exe");

            var root = Field("WebAdminWebRoot");
            Assert.Equal(@".\wwwroot", Convert(root, @".\wwwroot"));
            Reject(root, "w*w");
        }

        [Fact]
        public void LanguageFormat_RejectsPathTricks()
        {
            var lang = Field("Language");
            Assert.Equal("English", Convert(lang, "English"));
            Reject(lang, @"..\secrets");
            Reject(lang, "a/b");
        }

        // ------------------------------------------------------------------ codec: other CLR types (synthetic descriptors)

        [Fact]
        public void Enum_TimeSpan_Point_AreSupported()
        {
            var e = new SettingDescriptor { Section = "X", Key = "Day", Field = "Day", Type = typeof(DayOfWeek), Label = "d", Help = "h" };
            Assert.Equal("enum", e.Kind);
            Assert.Equal(DayOfWeek.Friday, Convert(e, "friday"));
            Assert.Equal(DayOfWeek.Monday, Convert(e, "1"));
            Assert.Contains("Sunday", Reject(e, "Funday"));
            Reject(e, "42");
            Assert.Equal("Friday", SettingsValueCodec.ToJsonValue(e, DayOfWeek.Friday));

            var t = new SettingDescriptor { Section = "X", Key = "T", Field = "T", Type = typeof(TimeSpan), Label = "t", Help = "h" };
            Assert.Equal("timespan", t.Kind);
            Assert.Equal(TimeSpan.FromMinutes(90), Convert(t, "01:30:00"));
            Reject(t, "-01:00:00");
            Reject(t, "soon");
            Assert.Equal("01:30:00", SettingsValueCodec.ToJsonValue(t, TimeSpan.FromMinutes(90)));

            var p = new SettingDescriptor { Section = "X", Key = "P", Field = "P", Type = typeof(System.Drawing.Point), Label = "p", Help = "h", Min = 0 };
            Assert.Equal("point", p.Kind);
            Assert.Equal(new System.Drawing.Point(10, 20), Convert(p, "10, 20"));
            Reject(p, "10");
            Reject(p, "-1,5");
            Assert.Equal("10,20", SettingsValueCodec.ToJsonValue(p, new System.Drawing.Point(10, 20)));
        }

        // ------------------------------------------------------------------ formatting / comparison

        [Fact]
        public void ToJsonValue_FloatsHaveNoBinaryNoise_IntegersAreLongs()
        {
            Assert.Equal(0.01, SettingsValueCodec.ToJsonValue(Field("DropRate"), 0.01f));
            Assert.Equal(1.1, SettingsValueCodec.ToJsonValue(Field("ExpRate"), 1.1f));
            Assert.Equal(7000L, SettingsValueCodec.ToJsonValue(Field("Port"), (ushort)7000));
            Assert.Equal(true, SettingsValueCodec.ToJsonValue(Field("AllowLogin"), true));
            Assert.Equal("x", SettingsValueCodec.ToJsonValue(Field("NewbieGuild"), "x"));
            Assert.Null(SettingsValueCodec.ToJsonValue(Field("NewbieGuild"), null));
        }

        [Fact]
        public void ToDisplay_MasksSecrets()
        {
            var gm = Field("GMPassword");
            Assert.Equal(SettingsValueCodec.Mask, SettingsValueCodec.ToDisplay(gm, "hunter2"));
            Assert.DoesNotContain("hunter2", SettingsValueCodec.ToDisplay(gm, "hunter2"));
            Assert.Equal("(not set)", SettingsValueCodec.ToDisplay(gm, ""));

            Assert.Equal("1.5", SettingsValueCodec.ToDisplay(Field("DropRate"), 1.5f));
            Assert.Equal("true", SettingsValueCodec.ToDisplay(Field("AllowLogin"), true));
            Assert.Equal("7000", SettingsValueCodec.ToDisplay(Field("Port"), (ushort)7000));
        }

        [Fact]
        public void ValuesEqual_AndOriginalMatches()
        {
            var rate = Field("DropRate");
            Assert.True(SettingsValueCodec.ValuesEqual(rate, 1.1f, 1.1f));
            Assert.False(SettingsValueCodec.ValuesEqual(rate, 1.1f, 1.2f));
            Assert.True(SettingsValueCodec.OriginalMatches(rate, "1.1", 1.1f));
            Assert.False(SettingsValueCodec.OriginalMatches(rate, "1", 1.1f));
            Assert.True(SettingsValueCodec.OriginalMatches(rate, null, 1.1f));      // no original sent: no stale check

            var port = Field("Port");
            Assert.True(SettingsValueCodec.ValuesEqual(port, (ushort)7000, (ushort)7000));
            Assert.True(SettingsValueCodec.OriginalMatches(port, "7000", (ushort)7000));
            Assert.False(SettingsValueCodec.OriginalMatches(port, "7001", (ushort)7000));
            Assert.False(SettingsValueCodec.OriginalMatches(port, "garbage", (ushort)7000));

            var login = Field("AllowLogin");
            Assert.True(SettingsValueCodec.OriginalMatches(login, "true", true));
            Assert.False(SettingsValueCodec.OriginalMatches(login, "false", true));

            var guild = Field("NewbieGuild");
            Assert.False(SettingsValueCodec.ValuesEqual(guild, "Newbie", "newbie"));   // ordinal: case is a change
            Assert.True(SettingsValueCodec.OriginalMatches(guild, "Newbie", "Newbie"));

            // a secret's original is the mask: never a conflict
            Assert.True(SettingsValueCodec.OriginalMatches(Field("GMPassword"), SettingsValueCodec.Mask, "real"));
        }

        [Fact]
        public void CrossValidate_PortsMustDiffer()
        {
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["Port"] = (ushort)7000,
                ["WebAdminPort"] = (ushort)7000,
            };

            var errors = SettingsValueCodec.CrossValidate(values, new[] { "WebAdminPort" });
            var e = Assert.Single(errors);
            Assert.Equal("WebAdminPort", e.Field);

            errors = SettingsValueCodec.CrossValidate(values, new[] { "Port", "WebAdminPort" });
            Assert.Equal(2, errors.Count);

            values["WebAdminPort"] = (ushort)5680;
            Assert.Empty(SettingsValueCodec.CrossValidate(values, new[] { "Port", "WebAdminPort" }));

            values["Port"] = (ushort)3100;
            Assert.Equal("Port", Assert.Single(SettingsValueCodec.CrossValidate(values, new[] { "Port" })).Field);

            // unchanged fields are never blamed
            Assert.Empty(SettingsValueCodec.CrossValidate(values, new[] { "DropRate" }));
            Assert.Empty(SettingsValueCodec.CrossValidate(values, Array.Empty<string>()));
        }

        [Fact]
        public void EffectiveRange_NarrowsTheClrRange()
        {
            Assert.Equal((1d, 65535d), SettingsValueCodec.EffectiveRange(Field("Port")));
            Assert.Equal((0d, 255d), SettingsValueCodec.EffectiveRange(Field("RangeAccuracyBonus")));
            Assert.Equal((0.01d, 1000d), SettingsValueCodec.EffectiveRange(Field("DropRate")));
            Assert.Null(SettingsValueCodec.EffectiveRange(Field("NewbieGuild")));
            Assert.Null(SettingsValueCodec.EffectiveRange(Field("AllowLogin")));
        }

        // ------------------------------------------------------------------ ini lookup used to verify persistence

        [Fact]
        public void FindIniValue_FollowsInIReaderRules()
        {
            var lines = new[]
            {
                "[General]",
                "VersionPath=.\\Mir2.Exe",
                "CheckVersion=True",
                "",
                "[Game]",
                "DropRate=1.5",
                "Empty=",
                "[Network]",
                "Port=7100",
            };

            Assert.Equal("True", ConfigRoutes.FindIniValue(lines, "General", "CheckVersion"));
            Assert.Equal("1.5", ConfigRoutes.FindIniValue(lines, "Game", "DropRate"));
            Assert.Equal(string.Empty, ConfigRoutes.FindIniValue(lines, "Game", "Empty"));
            Assert.Equal("7100", ConfigRoutes.FindIniValue(lines, "Network", "Port"));
            Assert.Null(ConfigRoutes.FindIniValue(lines, "Game", "Port"));        // stops at the next header
            Assert.Null(ConfigRoutes.FindIniValue(lines, "Missing", "Port"));
            Assert.Null(ConfigRoutes.FindIniValue(lines, "game", "DropRate"));    // section names are case-sensitive, like InIReader
        }
    }
}

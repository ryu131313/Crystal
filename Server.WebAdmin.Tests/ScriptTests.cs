using System.Text;

namespace Server.WebAdmin.Tests
{
    public class ScriptPathTests
    {
        [Theory]
        [InlineData("File.txt", "File.txt")]
        [InlineData("Folder\\File.txt", "Folder/File.txt")]
        [InlineData("A/B/C (1).txt", "A/B/C (1).txt")]
        [InlineData("  SET [].txt ", "SET [].txt")]
        public void Normalize_AcceptsPlainRelativePaths(string raw, string expected)
        {
            Assert.True(ScriptPaths.TryNormalize(raw, out var normalized, out var error), error);
            Assert.Equal(expected, normalized);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("..\\x.txt")]
        [InlineData("a/../b.txt")]
        [InlineData("a/./b.txt")]
        [InlineData("C:\\Windows\\x.txt")]
        [InlineData("C:x.txt")]
        [InlineData("/abs.txt")]
        [InlineData("\\abs.txt")]
        [InlineData("\\\\server\\share\\x.txt")]
        [InlineData("file.txt:stream")]
        [InlineData("CON.txt")]
        [InlineData("folder/nul")]
        [InlineData("com1.txt")]
        [InlineData("LPT9.log.txt")]
        [InlineData("a//b.txt")]
        [InlineData("x.txt.")]
        [InlineData("dir /x.txt")]
        [InlineData("PROGRA~1/x.txt")]
        [InlineData(".hidden.txt")]
        [InlineData("a\0b.txt")]
        [InlineData("a|b.txt")]
        [InlineData("a?.txt")]
        public void Normalize_RejectsUnsafePaths(string raw)
        {
            Assert.False(ScriptPaths.TryNormalize(raw, out _, out var error));
            Assert.False(string.IsNullOrEmpty(error));
            var ex = Assert.Throws<WebAdminException>(() => ScriptPaths.Normalize(raw));
            Assert.Equal(400, ex.Status);
        }

        [Fact]
        public void Normalize_RejectsControlCharacters()
        {
            Assert.False(ScriptPaths.TryNormalize("a" + (char)0 + "b.txt", out _, out _));
            Assert.False(ScriptPaths.TryNormalize("a" + (char)9 + "b.txt", out _, out _));
        }

        [Fact]
        public void Normalize_RejectsOverlongPaths()
        {
            Assert.False(ScriptPaths.TryNormalize(new string('a', ScriptPaths.MaxPathLength + 1) + ".txt", out _, out _));
            Assert.False(ScriptPaths.TryNormalize(new string('a', ScriptPaths.MaxSegmentLength + 1) + ".txt", out _, out _));
        }

        [Theory]
        [InlineData("CON", true)]
        [InlineData("con.txt", true)]
        [InlineData("Aux.tar.txt", true)]
        [InlineData("COM9", true)]
        [InlineData("Console.txt", false)]
        [InlineData("COM10.txt", false)]
        [InlineData("Null.txt", false)]
        public void IsReservedName_MatchesDeviceStems(string name, bool reserved)
        {
            Assert.Equal(reserved, ScriptPaths.IsReservedName(name));
        }

        [Fact]
        public void IsTextFile_OnlyTxt()
        {
            Assert.True(ScriptPaths.IsTextFile("a/b.TXT"));
            Assert.False(ScriptPaths.IsTextFile("a/b.ini"));
            Assert.False(ScriptPaths.IsTextFile(".txt"));
            Assert.False(ScriptPaths.IsTextFile("b.txt.bak"));
        }

        [Fact]
        public void Combine_RequiresTrailingSeparatorPrefix()
        {
            var root = Path.Combine(Path.GetTempPath(), "mircraft-root-" + Guid.NewGuid().ToString("N"), "Envir");
            Assert.Equal(Path.Combine(root, "Drops", "Hen.txt"), ScriptPaths.Combine(root, "Drops/Hen.txt"));
            // A sibling folder sharing the prefix ("EnvirX") is outside the root.
            Assert.Null(ScriptPaths.Combine(root, "../EnvirX/a.txt"));
            Assert.Null(ScriptPaths.Combine(root, "../a.txt"));
        }

        [Fact]
        public void CrossesReparsePoint_FalseForPlainFolders()
        {
            var root = Path.Combine(Path.GetTempPath(), "mircraft-rp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Sub"));
            try
            {
                File.WriteAllText(Path.Combine(root, "Sub", "a.txt"), "x");
                Assert.False(ScriptPaths.CrossesReparsePoint(root, Path.Combine(root, "Sub", "a.txt")));
                Assert.False(ScriptPaths.CrossesReparsePoint(root, Path.Combine(root, "Missing", "b.txt")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }

    public class ScriptTextTests
    {
        [Fact]
        public void Utf8Crlf_RoundTripsExactly()
        {
            var original = Encoding.UTF8.GetBytes("[@MAIN]\r\n#SAY\r\nHello\r\n");
            var decoded = ScriptText.Decode(original);

            Assert.Equal(ScriptText.Utf8, decoded.Encoding);
            Assert.Equal(ScriptText.Crlf, decoded.LineEnding);
            Assert.False(decoded.Mixed);
            Assert.Equal("[@MAIN]\n#SAY\nHello\n", decoded.Content);
            Assert.Equal(original, ScriptText.Encode(decoded.Content, decoded.Encoding, decoded.LineEnding));
        }

        [Fact]
        public void Lf_IsPreserved()
        {
            var original = Encoding.UTF8.GetBytes("a\nb\nc");
            var decoded = ScriptText.Decode(original);
            Assert.Equal(ScriptText.Lf, decoded.LineEnding);
            Assert.Equal(original, ScriptText.Encode(decoded.Content, decoded.Encoding, decoded.LineEnding));
        }

        [Fact]
        public void Bom_IsDetectedAndKept()
        {
            var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("1/10 Sword\r\n")).ToArray();
            var decoded = ScriptText.Decode(original);
            Assert.Equal(ScriptText.Utf8Bom, decoded.Encoding);
            Assert.Equal("1/10 Sword\n", decoded.Content);
            Assert.Equal(original, ScriptText.Encode(decoded.Content, decoded.Encoding, decoded.LineEnding));
        }

        [Fact]
        public void Utf16Le_IsDetectedAndKept()
        {
            var original = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("Ä\r\nB")).ToArray();
            var decoded = ScriptText.Decode(original);
            Assert.Equal(ScriptText.Utf16Le, decoded.Encoding);
            Assert.Equal("Ä\nB", decoded.Content);
            Assert.Equal(original, ScriptText.Encode(decoded.Content, decoded.Encoding, decoded.LineEnding));
        }

        [Fact]
        public void InvalidUtf8_FallsBackToLatin1_ByteExact()
        {
            var original = new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9, 0x0D, 0x0A, 0xB0, 0x0D, 0x0A };
            var decoded = ScriptText.Decode(original);
            Assert.Equal(ScriptText.Latin1, decoded.Encoding);
            Assert.Equal("caf\u00e9\n\u00b0\n", decoded.Content);
            Assert.Equal(original, ScriptText.Encode(decoded.Content, decoded.Encoding, decoded.LineEnding));
        }

        [Fact]
        public void Latin1_RejectsCharactersItCannotStore()
        {
            var ex = Assert.Throws<WebAdminException>(() => ScriptText.Encode("ok\nbad \u4e2d", ScriptText.Latin1, ScriptText.Crlf));
            Assert.Equal(422, ex.Status);
            Assert.Contains("Line 2", ex.Message);
        }

        [Fact]
        public void MixedLineEndings_UsesDominantAndFlagsMixed()
        {
            ScriptText.DetectLineEnding("a\r\nb\r\nc\nd\r\n", out var le, out var mixed);
            Assert.Equal(ScriptText.Crlf, le);
            Assert.True(mixed);

            ScriptText.DetectLineEnding("no newline", out le, out mixed);
            Assert.Equal(ScriptText.Crlf, le);
            Assert.False(mixed);
        }

        [Fact]
        public void Encode_AcceptsClientCrlfText()
        {
            var bytes = ScriptText.Encode("a\r\nb\nc", ScriptText.Utf8, ScriptText.Crlf);
            Assert.Equal("a\r\nb\r\nc", Encoding.UTF8.GetString(bytes));
        }

        [Fact]
        public void Etag_DependsOnContentOnly()
        {
            var a = ScriptText.ComputeEtag(Encoding.UTF8.GetBytes("abc"));
            Assert.Equal(a, ScriptText.ComputeEtag(Encoding.UTF8.GetBytes("abc")));
            Assert.NotEqual(a, ScriptText.ComputeEtag(Encoding.UTF8.GetBytes("abd")));
            Assert.Equal(24, a.Length);
        }
    }

    public sealed class ScriptStoreTests : IDisposable
    {
        private readonly string _dir;
        private readonly ScriptRoot _npcs;
        private readonly ScriptRoot _flat;
        private readonly ScriptFileStore _store;
        private DateTime _now = new DateTime(2026, 10, 8, 12, 30, 45);

        public ScriptStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "mircraft-scripts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_dir, "Envir", "NPCs", "Town"));
            _npcs = new ScriptRoot("npcs", "NPC scripts", Path.Combine(_dir, "Envir", "NPCs"), false);
            _flat = new ScriptRoot("envir", "Envir files", Path.Combine(_dir, "Envir"), true);
            _store = new ScriptFileStore(new[] { _npcs, _flat }, Path.Combine(_dir, "Back Up", "WebAdmin", "Scripts"), () => _now);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
        }

        private string NpcFile(string rel) => Path.Combine(_npcs.Directory, rel.Replace('/', Path.DirectorySeparatorChar));

        [Fact]
        public void GetRoot_UnknownIs404()
        {
            Assert.Same(_npcs, _store.GetRoot("NPCS"));
            Assert.Equal(404, Assert.Throws<WebAdminException>(() => _store.GetRoot("nope")).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => _store.GetRoot("")).Status);
        }

        /// <summary>Creates a directory junction (mklink /J needs no elevation). False when the platform or policy does not allow it.</summary>
        private static bool TryCreateJunction(string link, string target)
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    if (process == null) return false;
                    process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(10000)) return false;
                }
                return Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        [Fact]
        public void Junction_UnderRootIsRejected()
        {
            var outside = Path.Combine(_dir, "Outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "Secret.txt"), "outside the root");
            var link = Path.Combine(_npcs.Directory, "Linked");

            // Junctions need Windows and an NTFS temp folder; nothing to prove where one cannot be made.
            if (!TryCreateJunction(link, outside)) return;

            try
            {
                var full = Path.Combine(link, "Secret.txt");
                Assert.True(File.Exists(full));
                Assert.True(ScriptPaths.CrossesReparsePoint(_npcs.Directory, full));
                Assert.True(ScriptPaths.CrossesReparsePoint(_npcs.Directory, Path.Combine(link, "New.txt")));

                Assert.Equal(400, Assert.Throws<WebAdminException>(() => _store.ResolveFile(_npcs, "Linked/Secret.txt", out _)).Status);
                Assert.Equal(400, Assert.Throws<WebAdminException>(() => _store.ResolveFile(_npcs, "Linked/New.txt", out _)).Status);
                Assert.ThrowsAny<WebAdminException>(() => _store.Read(_npcs, "Linked/Secret.txt"));
                Assert.Equal("outside the root", File.ReadAllText(Path.Combine(outside, "Secret.txt")));
            }
            finally
            {
                try { Directory.Delete(link); } catch (IOException) { }
            }
        }

        [Fact]
        public void ResolveFile_EnforcesTxtAndFlatRoots()
        {
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => _store.ResolveFile(_npcs, "a.ini", out _)).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => _store.ResolveFile(_flat, "NPCs/a.txt", out _)).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => _store.ResolveFile(_npcs, "../LineMessage.txt", out _)).Status);

            var full = _store.ResolveFile(_npcs, "Town\\Guard.txt", out var normalized);
            Assert.Equal("Town/Guard.txt", normalized);
            Assert.Equal(NpcFile("Town/Guard.txt"), full);
        }

        [Fact]
        public void ListFiles_RecursiveForFoldersFlatForEnvir()
        {
            File.WriteAllText(NpcFile("A.txt"), "a");
            File.WriteAllText(NpcFile("Town/B.txt"), "bb");
            File.WriteAllText(NpcFile("Town/C.ini"), "c");
            File.WriteAllText(Path.Combine(_flat.Directory, "LineMessage.txt"), "x");

            var npcFiles = _store.ListFiles(_npcs, out var truncated);
            Assert.False(truncated);
            Assert.Equal(new[] { "A.txt", "Town/B.txt" }, npcFiles.Select(f => f.Path).ToArray());
            Assert.Equal(2, npcFiles[1].Size);

            var envirFiles = _store.ListFiles(_flat, out _);
            Assert.Equal(new[] { "LineMessage.txt" }, envirFiles.Select(f => f.Path).ToArray());
        }

        [Fact]
        public void Save_RejectsStaleEtag_ThenSavesWithBackupAndKeepsFormat()
        {
            var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("[@MAIN]\r\n#SAY\r\nHi\r\n")).ToArray();
            File.WriteAllBytes(NpcFile("Guard.txt"), original);

            var read = _store.Read(_npcs, "Guard.txt");
            Assert.Equal(ScriptText.Utf8Bom, read.Encoding);
            Assert.Equal(ScriptText.Crlf, read.LineEnding);

            var conflict = Assert.Throws<WebAdminException>(() => _store.Save(_npcs, "Guard.txt", "x", "not-the-etag", false));
            Assert.Equal(409, conflict.Status);

            var result = _store.Save(_npcs, "Guard.txt", "[@MAIN]\n#SAY\nHello\n", read.Etag, false);
            Assert.False(result.Unchanged);
            Assert.Equal("Guard.txt.20261008-123045.bak", result.Backup);

            var written = File.ReadAllBytes(NpcFile("Guard.txt"));
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("[@MAIN]\r\n#SAY\r\nHello\r\n")).ToArray(), written);
            Assert.Equal(ScriptText.ComputeEtag(written), result.Etag);

            var backups = _store.ListBackups(_npcs, "Guard.txt");
            Assert.Single(backups);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(_store.BackupFolder(_npcs, "Guard.txt"), backups[0].Name)));

            // the old etag is now stale; force overwrites anyway
            Assert.Equal(409, Assert.Throws<WebAdminException>(() => _store.Save(_npcs, "Guard.txt", "y", read.Etag, false)).Status);
            var forced = _store.Save(_npcs, "Guard.txt", "y", read.Etag, true);
            Assert.Equal("Guard.txt.20261008-123045-2.bak", forced.Backup);
        }

        [Fact]
        public void Save_UnchangedContent_WritesNothing()
        {
            File.WriteAllText(NpcFile("A.txt"), "line\r\n");
            var read = _store.Read(_npcs, "A.txt");

            var result = _store.Save(_npcs, "A.txt", read.Content, read.Etag, false);
            Assert.True(result.Unchanged);
            Assert.Null(result.Backup);
            Assert.Empty(_store.ListBackups(_npcs, "A.txt"));
        }

        [Fact]
        public void Save_MissingFileIs404()
        {
            Assert.Equal(404, Assert.Throws<WebAdminException>(() => _store.Save(_npcs, "Missing.txt", "x", "", true)).Status);
        }

        [Fact]
        public void Create_MakesFoldersAndRefusesDuplicates()
        {
            var created = _store.Create(_npcs, "New/Sub/Shop.txt", "[@MAIN]\n");
            Assert.Equal("New/Sub/Shop.txt", created.Path);
            Assert.Equal("[@MAIN]\r\n", File.ReadAllText(NpcFile("New/Sub/Shop.txt")));

            Assert.Equal(409, Assert.Throws<WebAdminException>(() => _store.Create(_npcs, "New/Sub/Shop.txt")).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => _store.Create(_npcs, "New/Shop.cs")).Status);
        }

        [Fact]
        public void Delete_MovesToBackup_AndRestoreBringsItBack()
        {
            File.WriteAllText(NpcFile("Town/B.txt"), "keep me");

            var backup = _store.Delete(_npcs, "Town/B.txt");
            Assert.False(File.Exists(NpcFile("Town/B.txt")));
            Assert.True(ScriptFileStore.TryParseBackupName("B.txt", backup, out _));
            Assert.True(File.Exists(Path.Combine(_store.BackupBase, "npcs", "Town", backup)));

            var restored = _store.Restore(_npcs, "Town/B.txt", backup);
            Assert.Null(restored.Backup);
            Assert.Equal("keep me", File.ReadAllText(NpcFile("Town/B.txt")));
        }

        [Fact]
        public void Restore_BacksUpTheCurrentVersionFirst()
        {
            File.WriteAllText(NpcFile("A.txt"), "v1");
            var v1 = _store.Read(_npcs, "A.txt");
            var save = _store.Save(_npcs, "A.txt", "v2", v1.Etag, false);

            _now = _now.AddMinutes(1);
            var restored = _store.Restore(_npcs, "A.txt", save.Backup);

            Assert.Equal("v1", File.ReadAllText(NpcFile("A.txt")));
            Assert.Equal("A.txt.20261008-123145.bak", restored.Backup);
            Assert.Equal("v2", _store.ReadBackup(_npcs, "A.txt", restored.Backup).Content);
            Assert.Equal(2, _store.ListBackups(_npcs, "A.txt").Count);
        }

        [Theory]
        [InlineData("..\\..\\secret.txt")]
        [InlineData("B.txt.20261008-123045.bak")]
        [InlineData("A.txt.2026-10-08.bak")]
        [InlineData("A.txt.20261008-123045.bak\\..\\x")]
        public void Restore_RejectsNamesThatAreNotBackupsOfTheFile(string name)
        {
            File.WriteAllText(NpcFile("A.txt"), "v1");
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => _store.Restore(_npcs, "A.txt", name)).Status);
        }

        [Fact]
        public void Rename_MovesAndRefusesToOverwrite()
        {
            File.WriteAllText(NpcFile("A.txt"), "a");
            File.WriteAllText(NpcFile("B.txt"), "b");

            Assert.Equal(409, Assert.Throws<WebAdminException>(() => _store.Rename(_npcs, "A.txt", "B.txt")).Status);
            Assert.Equal(404, Assert.Throws<WebAdminException>(() => _store.Rename(_npcs, "Missing.txt", "C.txt")).Status);

            Assert.Equal("Town/C.txt", _store.Rename(_npcs, "A.txt", "Town\\C.txt"));
            Assert.False(File.Exists(NpcFile("A.txt")));
            Assert.Equal("a", File.ReadAllText(NpcFile("Town/C.txt")));
        }

        [Fact]
        public void Search_LiteralRegexAndCap()
        {
            File.WriteAllText(NpcFile("A.txt"), "[@MAIN]\r\nGOTO @Shop\r\nnothing\r\n");
            File.WriteAllText(NpcFile("Town/B.txt"), "goto @Bank\r\n");

            var literal = _store.Search(_npcs, "GoTo", false);
            Assert.Equal(2, literal.Hits.Count);
            Assert.Equal("A.txt", literal.Hits[0].Path);
            Assert.Equal(2, literal.Hits[0].Line);
            Assert.Equal(1, literal.Hits[0].Column);
            Assert.Equal("Town/B.txt", literal.Hits[1].Path);

            var regex = _store.Search(_npcs, @"@(Shop|Bank)$", true);
            Assert.Equal(2, regex.Hits.Count);
            Assert.Equal(6, regex.Hits[0].Column);

            var capped = _store.Search(_npcs, "o", false, 1);
            Assert.Single(capped.Hits);
            Assert.True(capped.Truncated);

            Assert.Equal(400, Assert.Throws<WebAdminException>(() => _store.Search(_npcs, "(unclosed", true)).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => _store.Search(_npcs, "", false)).Status);
        }

        [Fact]
        public void WriteAtomic_ReplacesWithoutLeavingTempFiles()
        {
            var path = NpcFile("A.txt");
            ScriptFileStore.WriteAtomic(path, Encoding.UTF8.GetBytes("one"));
            ScriptFileStore.WriteAtomic(path, Encoding.UTF8.GetBytes("two"));

            Assert.Equal("two", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(_npcs.Directory, "*.tmp"));
        }
    }

    public class ScriptMappingTests
    {
        [Theory]
        [InlineData("A.txt", "A.txt.20261008-123045.bak", true)]
        [InlineData("A.txt", "a.TXT.20261008-123045-3.bak", true)]
        [InlineData("A.txt", "A.txt.20261008-1230.bak", false)]
        [InlineData("A.txt", "AB.txt.20261008-123045.bak", false)]
        [InlineData("A.txt", "A.txt.20261008-123045.bak.exe", false)]
        [InlineData("A.txt", "A.txt.99999999-123045.bak", false)]
        public void TryParseBackupName(string file, string backup, bool ok)
        {
            Assert.Equal(ok, ScriptFileStore.TryParseBackupName(file, backup, out _));
        }

        [Theory]
        [InlineData("npcs", "Town/Guard.txt", "npcs")]
        [InlineData("systemscripts", "00Default/Login.txt", "npcs")]
        [InlineData("drops", "Hen.txt", "drops")]
        [InlineData("recipes", "Sword.txt", "recipes")]
        [InlineData("envir", "LineMessage.txt", "linemessages")]
        [InlineData("envir", "Notice.txt", null)]
        [InlineData("routes", "Route1.txt", null)]
        [InlineData("values", "V.txt", null)]
        public void ReloadKindFor(string root, string path, string expected)
        {
            Assert.Equal(expected, ScriptFileStore.ReloadKindFor(root, path));
        }

        [Fact]
        public void ValidatorFor()
        {
            Assert.Equal("drops", ScriptFileStore.ValidatorFor("drops"));
            Assert.Equal("npc", ScriptFileStore.ValidatorFor("systemscripts"));
            Assert.Null(ScriptFileStore.ValidatorFor("quests")); // no legacy quest files since quest V2 wave 4
            Assert.Equal("recipe", ScriptFileStore.ValidatorFor("recipes"));
            Assert.Null(ScriptFileStore.ValidatorFor("routes"));
        }
    }

    public class ScriptValidationTests
    {
        private static ScriptValidationContext Ctx(Dictionary<string, string[]> files = null, string fileName = null)
        {
            files ??= new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            return new ScriptValidationContext
            {
                ItemNames = new[] { "Sword", "Candle", "(HP)DrugSmall", "Wooden Sword", "Armour(M)", "Armour(F)" },
                MonsterNames = new[] { "Hen", "Oma Warrior" },
                FileName = fileName,
                ReadDropFile = p => files.TryGetValue("drops:" + p, out var l) ? l : null,
                ReadEnvirFile = p => files.TryGetValue("envir:" + p, out var l) ? l : null,
                NpcScriptExists = p => files.ContainsKey("npc:" + p),
            };
        }

        private static List<ScriptDiagnostic> Run(string kind, string text, ScriptValidationContext ctx = null)
        {
            return ScriptValidation.Validate(kind, text, ctx ?? Ctx());
        }

        private static void AssertHas(List<ScriptDiagnostic> diags, int line, string severity, string fragment)
        {
            Assert.True(diags.Any(d => d.Line == line && d.Severity == severity && d.Message.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0),
                "Expected " + severity + " on line " + line + " containing '" + fragment + "'. Got: " + string.Join(" | ", diags));
        }

        // ---------------------------------------------------------------- drops

        [Fact]
        public void Drops_ValidFileHasNoProblems()
        {
            var diags = Run("drops", ";Pots\r\n1/10 Candle\r\n1/5 Gold 100\r\n1/100 WoodenSword Q\r\n\r\n1/50 GROUP*\r\n{\r\n1/1 Sword\r\n1/2 GROUP\r\n{\r\n1/3 Candle\r\n}\r\n}\r\n");
            Assert.Empty(diags);
        }

        [Fact]
        public void Drops_ReportsLoaderFailures()
        {
            var text = string.Join("\n",
                "1/10 MagicBanana",   // 1 unknown item
                "1/10 Gold",          // 2 missing amount
                "x Gold 5",           // 3 loader throws on Substring(2)
                "1/abc Candle",       // 4 bad chance
                "1/10\tCandle",       // 5 tab
                "1/10 Candle X",      // 6 ignored token
                "}",                  // 7 stray brace
                "  ;indented comment",// 8
                "2/10 Candle",        // 9 numerator ignored
                "1/10 GROUP",         // 10 never closed
                "{",
                "1/1 Candle");
            var diags = Run("drops", text);

            AssertHas(diags, 1, "error", "Unknown item 'MagicBanana'");
            AssertHas(diags, 2, "error", "Gold needs an amount");
            AssertHas(diags, 3, "error", "throw");
            AssertHas(diags, 4, "error", "Invalid chance");
            AssertHas(diags, 5, "error", "tabs");
            AssertHas(diags, 6, "warning", "ignored");
            AssertHas(diags, 7, "error", "without a GROUP");
            AssertHas(diags, 8, "warning", "first column");
            AssertHas(diags, 9, "warning", "1/10");
            AssertHas(diags, 10, "error", "never closed");
        }

        [Fact]
        public void Drops_InsertTargetIsCheckedAndValidated()
        {
            var files = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["drops:Shared\\Common.txt"] = new[] { "1/10 Candle", "1/10 NotAnItem" },
            };

            var diags = Run("drops", "#INSERT [Shared\\Common.txt]\n#INSERT [Missing.txt]\n#INSERT Missing.txt", Ctx(files));

            AssertHas(diags, 1, "error", "1 error(s)");
            AssertHas(diags, 2, "error", "not found");
            AssertHas(diags, 3, "warning", "brackets");
        }

        [Fact]
        public void DragonItem_UsesTheDragonValidator()
        {
            Assert.Equal(ScriptValidation.DragonDropsKind, ScriptValidation.KindFor("drops", "DragonItem.txt"));
            Assert.Equal(ScriptValidation.DragonDropsKind, ScriptValidation.KindFor("drops", "dragonitem.TXT"));
            Assert.Equal("drops", ScriptValidation.KindFor("drops", "Sub/DragonItem.txt"));
            Assert.Equal("drops", ScriptValidation.KindFor("drops", "Hen.txt"));
            Assert.Equal("npc", ScriptValidation.KindFor("npc", "DragonItem.txt"));
        }

        [Fact]
        public void DragonDrops_ValidFileHasNoProblems()
        {
            var diags = Run(ScriptValidation.DragonDropsKind, ";Pots + Other\r\n1/10 Candle 1\r\n\r\n;Weapons\r\n1/5 Gold 100 2\r\n1/100 WoodenSword 13\r\n");
            Assert.Empty(diags);
        }

        [Fact]
        public void DragonDrops_ReportsLoaderFailures()
        {
            var text = string.Join("\n",
                "1/10 Candle",          // 1 no level
                "1/10 Gold 100",        // 2 gold without level
                "1/10 MagicBanana 1",   // 3 unknown item
                "1/10 Candle x",        // 4 bad level
                "1/10 Gold 5 3",        // 5 ok
                "1/10 Gold 6 3",        // 6 second gold at level 3
                "1/10 Candle 1 extra",  // 7 ignored token
                "1/10 Candle 14",       // 8 out of range: stops the loader
                "1/10 Sword 1");        // 9 never loaded
            var diags = Run(ScriptValidation.DragonDropsKind, text);

            AssertHas(diags, 1, "error", "Missing level");
            AssertHas(diags, 2, "error", "amount and a level");
            AssertHas(diags, 3, "error", "Unknown item 'MagicBanana'");
            AssertHas(diags, 4, "error", "Invalid level");
            Assert.DoesNotContain(diags, d => d.Line == 5);
            AssertHas(diags, 6, "error", "second Gold");
            AssertHas(diags, 7, "warning", "ignored");
            AssertHas(diags, 8, "error", "stops");
            AssertHas(diags, 9, "warning", "Not loaded");
        }

        [Fact]
        public void Drops_WithoutItemListSkipsNameChecks()
        {
            var ctx = new ScriptValidationContext();
            var diags = ScriptValidation.Validate("drops", "1/10 Anything", ctx);
            Assert.DoesNotContain(diags, d => d.Severity == "error");
            Assert.Contains(diags, d => d.Severity == "info" && d.Message.Contains("not loaded"));
        }

        // ---------------------------------------------------------------- NPC scripts

        [Fact]
        public void Npc_ValidScriptHasNoProblems()
        {
            var text = string.Join("\r\n",
                "[@MAIN]",
                "#IF",
                "LEVEL > 10",
                "#SAY",
                "Hello <Buy/@BUY> <Talk/@Talk> <Page/@Page(5)> <Bye/@exit>",
                "#ACT",
                "GOTO @Talk",
                "#ELSESAY",
                "Too young.",
                "",
                "[@Talk]",
                "#SAY",
                "Fine.",
                "",
                "[@Page()]",
                "#SAY",
                "Page %ARG(0)",
                "",
                "[TRADE]",
                "Candle",
                "(HP)DrugSmall 5",
                "",
                "[TYPES]",
                "1",
                "2");

            Assert.Empty(Run("npc", text));
        }

        [Fact]
        public void Npc_ReportsBrokenReferencesAndDirectives()
        {
            var text = string.Join("\n",
                "[@MAIN]",                       // 1
                "#SAY",                          // 2
                "<Go/@Nowhere>",                 // 3 undefined page
                "#ACT",                          // 4
                "GOTO @Missing",                 // 5 undefined page
                "CALL Shared\\Teleport",         // 6 missing call target
                "#FOO",                          // 7 unknown directive
                "  #ACT",                        // 8 leading whitespace
                "[@Main2] ",                     // 9 trailing space
                "[@MAIN]",                       // 10 duplicate
                "[WEIRD]",                       // 11 unknown section
                "[TRADE]",                       // 12
                "UnknownThing",                  // 13 unknown item
                "[TYPES]",                       // 14
                "abc");                          // 15 not a number
            var diags = Run("npc", text);

            AssertHas(diags, 3, "warning", "[@Nowhere]");
            AssertHas(diags, 5, "warning", "[@Missing]");
            AssertHas(diags, 6, "warning", "CALL target");
            AssertHas(diags, 7, "error", "#FOO");
            AssertHas(diags, 8, "warning", "leading spaces");
            AssertHas(diags, 9, "warning", "Trailing spaces");
            AssertHas(diags, 10, "warning", "already defined");
            AssertHas(diags, 11, "warning", "Unknown section");
            AssertHas(diags, 13, "warning", "UnknownThing");
            AssertHas(diags, 15, "warning", "not a number");
        }

        [Fact]
        public void Npc_InsertAndIncludeTargets()
        {
            var files = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["envir:SystemScripts\\Shared.txt"] = new[] { "[@SharedPage]", "#SAY", "hi" },
                ["envir:SystemScripts\\Tavern.txt"] = new[] { "[@Main]", "{", "#SAY", "Welcome", "}" },
                ["npc:Shared\\Teleport"] = new string[0],
            };

            var text = string.Join("\n",
                "#INSERT [SystemScripts\\Shared.txt]",          // 1 ok (adds [@SharedPage])
                "#INCLUDE [SystemScripts\\Tavern.txt] @Main",   // 2 ok
                "#INCLUDE [SystemScripts\\Missing.txt] @Main",  // 3 missing: script loads empty
                "#INCLUDE [SystemScripts\\Tavern.txt]",         // 4 too few parts
                "#INCLUDE [SystemScripts\\Tavern.txt] @Nope",   // 5 block not found
                "#INSERT [Nope.txt]",                           // 6 missing insert
                "[@MAIN]",
                "#SAY",
                "<Go/@SharedPage>",
                "#ACT",
                "CALL \"Shared\\Teleport\"");
            var diags = Run("npc", text, Ctx(files));

            Assert.DoesNotContain(diags, d => d.Line == 1 || d.Line == 2 || d.Line == 9 || d.Line == 11);
            AssertHas(diags, 3, "error", "EMPTY");
            AssertHas(diags, 4, "error", "throws");
            AssertHas(diags, 5, "warning", "not found");
            AssertHas(diags, 6, "warning", "not found");
        }

        [Theory]
        [InlineData("[@Page(1,2)]", "[@PAGE()]")]
        [InlineData("[@page]", "[@PAGE]")]
        [InlineData("[@_DIE(12)]", "[@_DIE(12)]")]
        public void PageKey_MatchesLoaderArgumentRule(string key, string expected)
        {
            Assert.Equal(expected, ScriptValidation.PageKey(key));
        }

        // ---------------------------------------------------------------- quests

        [Fact]
        public void Npc_QuestsSection_IsIgnored_WithOneWarning()
        {
            // Quest V2 wave 4: [QUESTS] is no longer read (quest givers are questendpoints rows): one warning, the ids are not checked.
            var text = string.Join("\n", "[@MAIN]", "Hello", "[QUESTS]", "1", "-1", "x");
            var diags = Run("npc", text);
            AssertHas(diags, 4, "warning", "[QUESTS] is ignored");
            Assert.DoesNotContain(diags, d => d.Line == 5 || d.Line == 6);
            Assert.Empty(Run("quest", "[@KILLTASKS]\nDragon 5")); // no legacy quest-file validator any more
        }

        // ---------------------------------------------------------------- recipes

        [Fact]
        public void Recipe_ValidFile()
        {
            var text = "[Recipe]\r\nAmount 1\r\nChance 100\r\nGold 100\r\n\r\n[Tools]\r\n\r\n[Ingredients]\r\nCandle 2\r\n(HP)DrugSmall\r\n\r\n[Criteria]\r\nLevel 10\r\nClass Wizard\r\nGender 1\r\n";
            Assert.Empty(Run("recipe", text, Ctx(fileName: "Sword")));
        }

        [Fact]
        public void Recipe_ReportsLoaderCrashesAndUnknowns()
        {
            var text = string.Join("\n",
                "[Recipe]",          // 1
                "Amount lots",       // 2 throws
                "Chance 300",        // 3 throws (byte)
                "Colour red",        // 4 unknown key
                "[Ingredients]",     // 5
                "Candle",            // 6 ok
                "   ",               // 7 whitespace-only throws
                "Ghost 1",           // 8 unknown item
                "[Extras]",          // 9 unknown section
                "[Criteria]",        // 10
                "Class Paladin");    // 11 unknown class
            var diags = Run("recipe", text, Ctx(fileName: "NotAnItem"));

            AssertHas(diags, 1, "error", "No item is named 'NotAnItem'");
            AssertHas(diags, 2, "error", "Amount");
            AssertHas(diags, 3, "error", "Chance");
            AssertHas(diags, 4, "warning", "Colour");
            AssertHas(diags, 7, "error", "only spaces");
            AssertHas(diags, 8, "warning", "Ghost");
            AssertHas(diags, 9, "warning", "Unknown section");
            AssertHas(diags, 11, "warning", "Paladin");
        }

        [Fact]
        public void UnknownKind_ReturnsNothing()
        {
            Assert.Empty(ScriptValidation.Validate("routes", "anything", Ctx()));
        }
    }

    public class DropClassToggleTests
    {
        private static RequiredClass? ClassOf(string token)
        {
            switch (token.ToLowerInvariant())
            {
                case "assassindagger": return RequiredClass.Assassin;
                case "bow": return RequiredClass.Archer;
                case "candle": return RequiredClass.None;
                case "warsword": return RequiredClass.Warrior;
                case "hybrid": return RequiredClass.Warrior | RequiredClass.Assassin;
                default: return null;
            }
        }

        [Fact]
        public void Disable_CommentsOnlyClassDrops()
        {
            var text = ";Weapons\n1/10 AssassinDagger\n  1/20 Hybrid Q\n1/10 Candle\n1/10 WarSword\n1/10 Bow\n;AssassinDagger header\n1/5 GROUP\n{\n1/1 AssassinDagger\n}";
            var result = DropClassToggle.Apply(text, ClassOf, RequiredClass.Assassin, true, out var changed);

            Assert.Equal(new List<int> { 2, 3, 10 }, changed);
            var lines = result.Split('\n');
            Assert.Equal(";1/10 AssassinDagger", lines[1]);
            Assert.Equal("  ;1/20 Hybrid Q", lines[2]);
            Assert.Equal("1/10 Candle", lines[3]);
            Assert.Equal("1/10 Bow", lines[5]);
            Assert.Equal(";AssassinDagger header", lines[6]);
        }

        [Fact]
        public void Enable_RestoresCommentedClassDrops_AndIsIdempotent()
        {
            var text = ";Weapons\n;1/10 AssassinDagger\n  ; 1/20 Hybrid Q\n;1/10 Bow\n;1/10 Candle";
            var result = DropClassToggle.Apply(text, ClassOf, RequiredClass.Assassin, false, out var changed);

            Assert.Equal(new List<int> { 2, 3 }, changed);
            Assert.Equal(";Weapons\n1/10 AssassinDagger\n  1/20 Hybrid Q\n;1/10 Bow\n;1/10 Candle", result);

            DropClassToggle.Apply(result, ClassOf, RequiredClass.Assassin, false, out var again);
            Assert.Empty(again);
        }

        [Fact]
        public void DisableThenEnable_RoundTrips()
        {
            var text = "1/10 AssassinDagger\n\t1/10 Bow\n1/10 Candle";
            var off = DropClassToggle.Apply(text, ClassOf, RequiredClass.Archer, true, out _);
            var on = DropClassToggle.Apply(off, ClassOf, RequiredClass.Archer, false, out _);
            Assert.Equal(text, on);
        }

        [Theory]
        [InlineData("Assassin", true, RequiredClass.Assassin)]
        [InlineData("archer", true, RequiredClass.Archer)]
        [InlineData("Warrior", false, RequiredClass.None)]
        public void TryParseClass(string text, bool ok, RequiredClass expected)
        {
            Assert.Equal(ok, DropClassToggle.TryParseClass(text, out var cls));
            Assert.Equal(expected, cls);
        }
    }
}

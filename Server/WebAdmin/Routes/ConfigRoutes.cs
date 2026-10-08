using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Server.MirEnvir;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// Server Config (B6): edits the Setup.ini keys that Settings.Load()/Save() handle (catalogue: <see cref="SettingsCatalog"/>).
    ///   GET /api/admin/config  → {ok, file, lastWriteUtc, world, sections:[{name, items:[...]}], pendingRestart, lookups}
    ///   PUT /api/admin/config  {changes:[{field, value, original?}], reason?} (?dryRun=1 validates only)
    ///       → {ok, message, applied:[field], rejected:[{field, error}], unchanged:[field], restartRequired:[field], warnings, persisted}
    /// Settings statics are read and written under <see cref="WebAdminOptions.SettingsLock"/>. Immediate (restart "none") values
    /// are applied to the live statics; world-scope values are staged (Setup.ini only) while a WorkLoop exists and assigned on
    /// WorldStopped; process-scope values are never assigned (Setup.ini only, read by the next process). Everything is persisted
    /// with Settings.Save() (+ direct writes for the keys Save() does not write, and for the staged values Save() overwrote). Every applied key is audited as config.update with before → after (secrets masked).
    /// </summary>
    public sealed class ConfigRoutes : IWebAdminRoutes
    {
        private const int MaxChanges = 500;
        private const string PendingArea = "Server config";

        private static int _subscribed;
        private static readonly object PendingLock = new object();
        private static readonly Dictionary<string, PendingRestart> PendingWorld = new Dictionary<string, PendingRestart>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, PendingRestart> PendingProcess = new Dictionary<string, PendingRestart>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, MemberInfo> Members = new ConcurrentDictionary<string, MemberInfo>(StringComparer.Ordinal);

        private sealed class PendingRestart
        {
            public string Field;
            public string Section;
            public string Key;
            public string Label;
            public string Admin;
            public DateTime Utc;
        }

        /// <summary>
        /// A restart-scope value written to Setup.ini but NOT assigned to the live Settings static, because the game reads that
        /// static at run time with state sized/created at start (Multithreaded → Envir.MobThreads, ThreadLimit → the MobThreads
        /// array and Map.Thread). World-scope entries are assigned on WorldStopped (or at once when no WorkLoop exists);
        /// process-scope entries only take effect when the next process loads Setup.ini. Guarded by SettingsLock.
        /// </summary>
        private sealed class StagedValue
        {
            public SettingDescriptor Descriptor;
            public object Value;
        }

        private static readonly Dictionary<string, StagedValue> Staged = new Dictionary<string, StagedValue>(StringComparer.OrdinalIgnoreCase);

        private sealed class Change
        {
            public SettingDescriptor Descriptor;
            public object Value;
            public string Original;
            public object Before;
        }

        public void Register(WebAdminRouter router)
        {
            router.Get("/api/admin/config", GetConfig);
            router.Put("/api/admin/config", PutConfig);

            if (Interlocked.Exchange(ref _subscribed, 1) == 0)
            {
                WebAdminHooks.WorldStarted += OnWorldStarted;
                WebAdminHooks.WorldStopped += OnWorldStopped;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            }
        }

        /// <summary>
        /// A world (re)start applies every world-scope change that was assigned before it (see <see cref="OnWorldStopped"/>):
        /// forget those. Values still staged (saved in the narrow window after the last WorldStopped) stay pending.
        /// </summary>
        private static void OnWorldStarted()
        {
            HashSet<string> stillStaged;
            lock (WebAdminOptions.SettingsLock)
                stillStaged = new HashSet<string>(Staged.Keys, StringComparer.OrdinalIgnoreCase);

            lock (PendingLock)
            {
                foreach (var field in PendingWorld.Keys.ToList())
                {
                    if (!stillStaged.Contains(field)) PendingWorld.Remove(field);
                }
            }
        }

        /// <summary>
        /// Raised on the WorkLoop thread after StopEnvir, before Envir clears its thread handle (so Envir.Stop() — and therefore a
        /// Reboot's Start() — waits for this). No game loop or monster processing is running: the staged world-scope values can be
        /// assigned to the live Settings statics now, and the next WorkLoop start reads them consistently.
        /// </summary>
        private static void OnWorldStopped()
        {
            lock (WebAdminOptions.SettingsLock)
            {
                foreach (var entry in Staged.Values.Where(s => s.Descriptor.Restart == SettingRestart.World).ToList())
                {
                    try
                    {
                        SetValue(entry.Descriptor, entry.Value);
                        WriteIni(entry.Descriptor, entry.Value);
                    }
                    catch (Exception ex)
                    {
                        WebAdminLog.Error(ex, "Server config: applying staged " + entry.Descriptor.Field + " failed");
                    }

                    Staged.Remove(entry.Descriptor.Field);
                }

                // Something else may have called Settings.Save() meanwhile (WinForms config form): put staged values back.
                RewriteStaged();
            }
        }

        /// <summary>
        /// Server.MirForms' Program calls Settings.Save() on exit, which writes the live statics — i.e. the OLD values of every
        /// staged (process-scope) key. ProcessExit runs after that: write the staged values back so they survive the restart.
        /// </summary>
        private static void OnProcessExit(object sender, EventArgs e)
        {
            var taken = false;
            try
            {
                Monitor.TryEnter(WebAdminOptions.SettingsLock, 2000, ref taken);
                if (taken && Staged.Count > 0) RewriteStaged();
            }
            catch (Exception)
            {
                // best effort at shutdown
            }
            finally
            {
                if (taken) Monitor.Exit(WebAdminOptions.SettingsLock);
            }
        }

        // ------------------------------------------------------------------ GET

        private static Task GetConfig(WebAdminRequest req)
        {
            var languages = ListLanguages();
            var lookups = ReadLookups();
            var sections = new List<object>();

            lock (WebAdminOptions.SettingsLock)
            {
                foreach (var group in SettingsCatalog.Groups())
                {
                    var items = new List<object>();
                    foreach (var d in SettingsCatalog.All)
                    {
                        if (d.Group != group) continue;
                        items.Add(BuildItem(d, EffectiveValue(d), languages));
                    }

                    sections.Add(new { name = group, items });
                }
            }

            string file = null;
            DateTime? lastWriteUtc = null;
            try
            {
                var path = SetupIniPath();
                file = Path.GetFullPath(path);
                if (File.Exists(path)) lastWriteUtc = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception)
            {
                // path info is cosmetic
            }

            return req.Json(new
            {
                ok = true,
                file,
                lastWriteUtc,
                world = WebAdminGame.WorldState,
                sections,
                pendingRestart = PendingSnapshot(),
                lookups = new
                {
                    languages,
                    monsters = lookups.Monsters,
                    maps = lookups.Maps,
                },
            });
        }

        private static object BuildItem(SettingDescriptor d, object value, IReadOnlyList<string> languages)
        {
            var range = SettingsValueCodec.EffectiveRange(d);
            var isSet = d.Secret ? !string.IsNullOrEmpty(value as string) : (bool?)null;

            object shown = d.Secret
                ? (isSet == true ? SettingsValueCodec.Mask : string.Empty)
                : SettingsValueCodec.ToJsonValue(d, value);

            return new
            {
                key = d.Key,
                section = d.Section,
                field = d.Field,
                type = d.Kind,
                clrType = d.Type.Name,
                value = shown,
                restartRequired = d.RestartRequired,
                restart = d.RestartName,
                secret = d.Secret,
                isSet,
                label = d.Label,
                help = d.Help,
                min = range?.Min,
                max = range?.Max,
                format = d.Format,
                options = d.Format == "language" && languages.Count > 0 ? languages : null,
                enumMembers = d.Type.IsEnum
                    ? Enum.GetValues(d.Type).Cast<object>().Select(v => new { name = v.ToString(), value = Convert.ToInt64(v, CultureInfo.InvariantCulture) }).ToList()
                    : null,
            };
        }

        // ------------------------------------------------------------------ PUT

        private static async Task PutConfig(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            if (body.ValueKind != JsonValueKind.Object)
                throw new WebAdminException(400, "Expected a JSON object {changes:[{field, value}]}");

            if (!body.TryGetPropertyIgnoreCase("changes", out var changesEl) || changesEl.ValueKind != JsonValueKind.Array)
                throw new WebAdminException(400, "Expected {changes:[{field, value}]}");

            var count = changesEl.GetArrayLength();
            if (count == 0) throw new WebAdminException(400, "No changes");
            if (count > MaxChanges) throw new WebAdminException(400, "Too many changes (max " + MaxChanges + ")");

            var reason = body.GetString("reason");
            if (reason != null)
            {
                reason = reason.Trim();
                if (reason.Length > 500) reason = reason.Substring(0, 500);
                if (reason.Length == 0) reason = null;
            }

            var dryRun = req.QueryBool("dryRun") || body.GetBool("dryRun");

            var rejected = new List<object>();
            var warnings = new List<object>();
            var accepted = new List<Change>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var languages = ListLanguages();

            void Reject(string rejectedField, string rejectedError)
            {
                rejected.Add(new { field = rejectedField, error = rejectedError });
            }

            // 1) parse + convert + validate (no Settings access yet)
            foreach (var el in changesEl.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                {
                    Reject(null, "Each change must be an object {field, value}");
                    continue;
                }

                var fieldName = el.GetString("field");
                SettingDescriptor d = SettingsCatalog.ByField(fieldName);
                if (d == null && fieldName == null)
                {
                    var section = el.GetString("section");
                    var key = el.GetString("key");
                    d = SettingsCatalog.ByKey(section, key);
                    fieldName = key;
                }

                if (d == null)
                {
                    Reject(fieldName, "Unknown setting");
                    continue;
                }

                if (!seen.Add(d.Field))
                {
                    Reject(d.Field, "Changed more than once in the same request");
                    accepted.RemoveAll(x => x.Descriptor == d);
                    continue;
                }

                if (!el.TryGetPropertyIgnoreCase("value", out var raw))
                {
                    Reject(d.Field, "Value is required");
                    continue;
                }

                if (!SettingsValueCodec.TryConvert(d, raw, out var value, out var convertError))
                {
                    Reject(d.Field, convertError);
                    continue;
                }

                if (d.Format == "language" && languages.Count > 0
                    && !languages.Any(l => string.Equals(l, value as string, StringComparison.OrdinalIgnoreCase)))
                {
                    Reject(d.Field, "No such localization file. Available: " + string.Join(", ", languages));
                    continue;
                }

                if (d.Field == "WebAdminWebRoot" && !WebRootLooksValid(value as string, out var rootError))
                {
                    Reject(d.Field, rootError);
                    continue;
                }

                string original = null;
                if (el.TryGetPropertyIgnoreCase("original", out var origEl))
                    original = SettingsValueCodec.RawText(origEl);

                accepted.Add(new Change { Descriptor = d, Value = value, Original = original });
            }

            // 2) name warnings against the editor database (never blocking: the names are looked up at use time)
            var lookups = ReadLookups();
            foreach (var change in accepted)
            {
                var s = change.Value as string;
                if (s == null) continue;

                if (change.Descriptor.Format == "monster" && lookups.Monsters.Count > 0
                    && !lookups.Monsters.Any(m => NameMatches(m, s)))
                {
                    warnings.Add(new { field = change.Descriptor.Field, message = "No monster named '" + s + "' in the database" });
                }
                else if (change.Descriptor.Format == "map" && lookups.Maps.Count > 0
                    && !lookups.Maps.Any(m => string.Equals(m, s, StringComparison.OrdinalIgnoreCase)))
                {
                    warnings.Add(new { field = change.Descriptor.Field, message = "No map with file name '" + s + "' in the database" });
                }
            }

            var applied = new List<Change>();
            var unchanged = new List<string>();
            var notPersisted = new List<string>();
            var revertedToLive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var versionReloaded = false;

            // 3) stale check, cross-key rules, assign, persist — all under the settings lock
            lock (WebAdminOptions.SettingsLock)
            {
                foreach (var change in accepted)
                {
                    var d = change.Descriptor;
                    change.Before = EffectiveValue(d);

                    if (!SettingsValueCodec.OriginalMatches(d, change.Original, change.Before))
                    {
                        Reject(d.Field, "Changed by someone else since you loaded the page (now " + SettingsValueCodec.ToDisplay(d, change.Before) + "). Reload and try again.");
                        continue;
                    }

                    if (SettingsValueCodec.ValuesEqual(d, change.Value, change.Before))
                    {
                        unchanged.Add(d.Field);
                        continue;
                    }

                    applied.Add(change);
                }

                var finalValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var portField in new[] { "Port", "WebAdminPort" })
                {
                    var pd = SettingsCatalog.ByField(portField);
                    var portChange = applied.FirstOrDefault(x => x.Descriptor == pd);
                    finalValues[portField] = portChange != null ? portChange.Value : EffectiveValue(pd);
                }

                foreach (var (crossField, crossError) in SettingsValueCodec.CrossValidate(finalValues, applied.Select(x => x.Descriptor.Field).ToList()))
                {
                    Reject(crossField, crossError);
                    applied.RemoveAll(x => string.Equals(x.Descriptor.Field, crossField, StringComparison.OrdinalIgnoreCase));
                }

                if (!dryRun && applied.Count > 0)
                {
                    // Restart-scope values must not reach the live statics while a WorkLoop exists: Multithreaded is read on
                    // every tick / spawn against MobThreads created at start, ThreadLimit by Map's constructor against the
                    // MobThreads array sized at process start. Stage them (ini only) instead. With no WorkLoop at all (stopped
                    // or crashed world), world-scope values are assigned at once: the next start reads them from the beginning.
                    var noWorkLoop = WebAdminGame.IsFullyStopped(WebAdminGame.World);

                    foreach (var change in applied)
                    {
                        var d = change.Descriptor;
                        var stage = d.Restart == SettingRestart.Process || (d.Restart == SettingRestart.World && !noWorkLoop);

                        if (stage && !SettingsValueCodec.ValuesEqual(d, change.Value, GetValue(d)))
                        {
                            Staged[d.Field] = new StagedValue { Descriptor = d, Value = change.Value };
                        }
                        else
                        {
                            // back to the live value (un-stage), or a value that applies now
                            if (stage) revertedToLive.Add(d.Field);
                            Staged.Remove(d.Field);
                            if (!stage) SetValue(d, change.Value);
                        }
                    }

                    Settings.Save();

                    foreach (var change in applied)
                    {
                        if (!change.Descriptor.SavedBySettingsSave)
                            WriteIni(change.Descriptor, change.Value);
                    }

                    // Settings.Save() wrote the live (old) value of every staged key: write the staged values back.
                    RewriteStaged();

                    if (applied.Any(x => SettingsCatalog.IsVersionKey(x.Descriptor)))
                    {
                        ReloadVersionHashes();
                        versionReloaded = true;
                    }

                    notPersisted = VerifyPersisted(applied);
                }
            }

            var restartFields = applied.Where(x => x.Descriptor.RestartRequired && !revertedToLive.Contains(x.Descriptor.Field)).Select(x => x.Descriptor.Field).ToList();

            if (!dryRun && applied.Count > 0)
            {
                var now = DateTime.UtcNow;
                var worldKeys = new List<string>();

                lock (PendingLock)
                {
                    foreach (var change in applied)
                    {
                        var d = change.Descriptor;
                        if (!d.RestartRequired) continue;

                        if (revertedToLive.Contains(d.Field))
                        {
                            // a staged change was undone: the running value is what Setup.ini holds again
                            PendingWorld.Remove(d.Field);
                            PendingProcess.Remove(d.Field);
                            continue;
                        }

                        var entry = new PendingRestart { Field = d.Field, Section = d.Section, Key = d.Key, Label = d.Label, Admin = req.AdminName, Utc = now };
                        if (d.Restart == SettingRestart.Process)
                        {
                            PendingProcess[d.Field] = entry;
                        }
                        else
                        {
                            PendingWorld[d.Field] = entry;
                            worldKeys.Add(d.Key);
                        }
                    }
                }

                if (worldKeys.Count > 0)
                    WebAdminPending.Add(PendingArea, "Setup.ini: " + string.Join(", ", worldKeys) + " (applies when the world restarts)", req.AdminName);

                foreach (var change in applied)
                {
                    var d = change.Descriptor;
                    WebAdminAudit.Record(req, "config.update", "Setup.ini [" + d.Section + "] " + d.Key, new
                    {
                        field = d.Field,
                        section = d.Section,
                        key = d.Key,
                        before = SettingsValueCodec.ToDisplay(d, change.Before),
                        after = SettingsValueCodec.ToDisplay(d, change.Value),
                        restart = d.RestartName,
                        secret = d.Secret ? true : (bool?)null,
                    }, reason);
                }
            }

            var appliedFields = applied.Select(x => x.Descriptor.Field).ToList();
            var allOk = rejected.Count == 0 && notPersisted.Count == 0;

            string message;
            if (dryRun)
            {
                message = rejected.Count == 0
                    ? "Valid: " + appliedFields.Count + " change(s) would be saved."
                    : rejected.Count + " change(s) would be rejected.";
            }
            else if (appliedFields.Count == 0)
            {
                message = rejected.Count > 0 ? "Nothing was saved: " + rejected.Count + " change(s) rejected." : "No changes (values already set).";
            }
            else
            {
                message = "Saved " + appliedFields.Count + " setting(s) to Setup.ini.";
                if (rejected.Count > 0) message += " " + rejected.Count + " rejected.";
                if (restartFields.Count > 0) message += " " + restartFields.Count + " need(s) a restart.";
                if (notPersisted.Count > 0) message += " WARNING: Setup.ini could not be written for " + string.Join(", ", notPersisted) + " (applied in memory only).";
            }

            var status = !dryRun && appliedFields.Count == 0 && rejected.Count > 0 ? 422 : 200;

            await req.Json(new
            {
                ok = allOk,
                message,
                dryRun = dryRun ? true : (bool?)null,
                applied = appliedFields,
                rejected,
                unchanged,
                restartRequired = restartFields,
                restartScope = new
                {
                    world = applied.Where(x => x.Descriptor.Restart == SettingRestart.World && !revertedToLive.Contains(x.Descriptor.Field)).Select(x => x.Descriptor.Field).ToList(),
                    process = applied.Where(x => x.Descriptor.Restart == SettingRestart.Process && !revertedToLive.Contains(x.Descriptor.Field)).Select(x => x.Descriptor.Field).ToList(),
                },
                warnings,
                persisted = dryRun ? (bool?)null : notPersisted.Count == 0,
                notPersisted = notPersisted.Count > 0 ? notPersisted : null,
                versionReloaded = versionReloaded ? true : (bool?)null,
                pendingRestart = PendingSnapshot(),
            }, status).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------ Settings access (callers hold SettingsLock)

        private static MemberInfo ResolveMember(SettingDescriptor d)
        {
            return Members.GetOrAdd(d.Field, name =>
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
                MemberInfo member = d.IsProperty
                    ? typeof(Settings).GetProperty(name, flags)
                    : typeof(Settings).GetField(name, flags);

                if (member == null)
                    throw new InvalidOperationException("SettingsCatalog: Settings." + name + " does not exist");

                return member;
            });
        }

        private static object GetValue(SettingDescriptor d)
        {
            var member = ResolveMember(d);
            return member is PropertyInfo p ? p.GetValue(null) : ((FieldInfo)member).GetValue(null);
        }

        /// <summary>What Setup.ini holds for the key: the staged value when one is waiting for a restart, else the live static.</summary>
        private static object EffectiveValue(SettingDescriptor d)
        {
            return Staged.TryGetValue(d.Field, out var staged) ? staged.Value : GetValue(d);
        }

        /// <summary>Writes every staged value to Setup.ini (after anything that wrote the live statics with Settings.Save()).</summary>
        private static void RewriteStaged()
        {
            foreach (var entry in Staged.Values)
                WriteIni(entry.Descriptor, entry.Value);
        }

        /// <summary>
        /// Settings.LoadVersion() replaces VersionHashes with an empty list and then fills it, while MirConnection.ClientVersion
        /// enumerates it on the game thread. Same hashing (VersionPath split on ',', MD5 of each existing file), but built
        /// into a local list and published with one reference assignment.
        /// </summary>
        private static void ReloadVersionHashes()
        {
            var hashes = new List<byte[]>();
            try
            {
                foreach (var path in (Settings.VersionPath ?? string.Empty).Split(','))
                {
                    if (!File.Exists(path)) continue;

                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
                    using (var md5 = System.Security.Cryptography.MD5.Create())
                        hashes.Add(md5.ComputeHash(stream));
                }
            }
            catch (Exception ex)
            {
                MessageQueue.Instance.Enqueue(ex);
            }

            Settings.VersionHashes = hashes;
        }

        private static void SetValue(SettingDescriptor d, object value)
        {
            var member = ResolveMember(d);
            if (member is PropertyInfo p)
            {
                var setter = p.GetSetMethod(true);
                if (setter == null) throw new InvalidOperationException("Settings." + d.Field + " has no setter");
                setter.Invoke(null, new[] { value });
            }
            else
            {
                var f = (FieldInfo)member;
                if (f.IsInitOnly || f.IsLiteral) throw new InvalidOperationException("Settings." + d.Field + " is read-only");
                f.SetValue(null, value);
            }
        }

        /// <summary>Writes a key Settings.Save() does not persist, through the same InIReader instance (so a later Save keeps it).</summary>
        private static void WriteIni(SettingDescriptor d, object value)
        {
            var reader = Settings.Reader;
            switch (value)
            {
                case bool b:
                    reader.Write(d.Section, d.Key, b);
                    break;
                case byte u8:
                    reader.Write(d.Section, d.Key, u8);
                    break;
                case short i16:
                    reader.Write(d.Section, d.Key, i16);
                    break;
                case ushort u16:
                    reader.Write(d.Section, d.Key, u16);
                    break;
                case int i32:
                    reader.Write(d.Section, d.Key, i32);
                    break;
                case uint u32:
                    reader.Write(d.Section, d.Key, u32);
                    break;
                case long i64:
                    reader.Write(d.Section, d.Key, i64);
                    break;
                case float f:
                    reader.Write(d.Section, d.Key, f);
                    break;
                case double dbl:
                    reader.Write(d.Section, d.Key, dbl);
                    break;
                case string s:
                    reader.Write(d.Section, d.Key, s);
                    break;
                default:
                    reader.Write(d.Section, d.Key, Convert.ToString(value, CultureInfo.CurrentCulture));
                    break;
            }
        }

        /// <summary>
        /// InIReader.Save() swallows IO errors, so re-read Setup.ini and check each applied key holds the text InIReader writes
        /// (<c>key + "=" + value</c>, current culture). Returns the fields that are not on disk.
        /// </summary>
        private static List<string> VerifyPersisted(List<Change> applied)
        {
            var missing = new List<string>();
            List<string> lines;
            try
            {
                lines = new List<string>();
                using (var stream = new FileStream(SetupIniPath(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) lines.Add(line);
                }
            }
            catch (Exception ex)
            {
                MessageQueue.Instance.Enqueue(ex);
                return applied.Select(x => x.Descriptor.Field).ToList();
            }

            foreach (var change in applied)
            {
                var d = change.Descriptor;
                var expected = change.Value is bool b ? b.ToString() : Convert.ToString(change.Value, CultureInfo.CurrentCulture);
                var found = FindIniValue(lines, d.Section, d.Key);
                if (!string.Equals(found, expected, StringComparison.Ordinal))
                    missing.Add(d.Field);
            }

            return missing;
        }

        /// <summary>Same lookup rule as InIReader.FindValue: the first "[section]" line, then the first "key=" line before the next header.</summary>
        internal static string FindIniValue(IReadOnlyList<string> lines, string section, string key)
        {
            var header = "[" + section + "]";
            for (var a = 0; a < lines.Count; a++)
            {
                if (!string.Equals(lines[a], header, StringComparison.Ordinal)) continue;

                for (var b = a + 1; b < lines.Count; b++)
                {
                    var parts = lines[b].Split('=');
                    if (string.Equals(parts[0], key, StringComparison.Ordinal))
                        return parts.Length > 1 ? parts[1] : string.Empty;
                    if (lines[b].StartsWith("[") && lines[b].EndsWith("]"))
                        return null;
                }

                return null;
            }

            return null;
        }

        private static string SetupIniPath() => Path.Combine(Settings.ConfigPath, "Setup.ini");

        // ------------------------------------------------------------------ helpers

        private static object PendingSnapshot()
        {
            lock (PendingLock)
            {
                object Map(PendingRestart p) => new { field = p.Field, section = p.Section, key = p.Key, label = p.Label, admin = p.Admin, utc = p.Utc };
                return new
                {
                    world = PendingWorld.Values.OrderBy(p => p.Utc).Select(Map).ToList(),
                    process = PendingProcess.Values.OrderBy(p => p.Utc).Select(Map).ToList(),
                };
            }
        }

        /// <summary>Localization file names (without .json) next to the exe, as Settings.Load() resolves them.</summary>
        private static IReadOnlyList<string> ListLanguages()
        {
            try
            {
                const string dir = @".\Localization\";
                if (!Directory.Exists(dir)) return Array.Empty<string>();
                return Directory.GetFiles(dir, "*.json")
                    .Select(file => Path.GetFileNameWithoutExtension(file))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        private sealed class NameLookups
        {
            public List<string> Monsters = new List<string>();
            public List<string> Maps = new List<string>();
        }

        /// <summary>Monster names and map file names from the editor database (Envir.Edit, under EditLock) for suggestions / warnings.</summary>
        private static NameLookups ReadLookups()
        {
            var result = new NameLookups();
            try
            {
                lock (WebAdminGame.EditLock)
                {
                    var edit = Envir.Edit;
                    foreach (var m in edit.MonsterInfoList)
                    {
                        if (m != null && !string.IsNullOrEmpty(m.Name)) result.Monsters.Add(m.Name);
                    }

                    foreach (var map in edit.MapInfoList)
                    {
                        if (map != null && !string.IsNullOrEmpty(map.FileName)) result.Maps.Add(map.FileName);
                    }
                }
            }
            catch (Exception)
            {
                // the editor database may be mid-load; suggestions are optional
                return new NameLookups();
            }

            result.Monsters = result.Monsters.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            result.Maps = result.Maps.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            return result;
        }

        /// <summary>Envir.GetMonsterInfo's loose match: case-insensitive, spaces ignored.</summary>
        private static bool NameMatches(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(a.Replace(" ", ""), b.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The new web root must resolve to a folder that contains admin\index.html (or the panel would not load after a restart).</summary>
        private static bool WebRootLooksValid(string configured, out string error)
        {
            error = null;
            try
            {
                var full = WebAdminOptions.ResolveWebRoot(configured, AppContext.BaseDirectory);
                if (!Directory.Exists(full))
                {
                    error = "Folder not found: " + full;
                    return false;
                }

                if (!File.Exists(Path.Combine(full, "admin", "index.html")))
                {
                    error = "No admin\\index.html under " + full;
                    return false;
                }

                return true;
            }
            catch (Exception)
            {
                error = "Not a valid folder path";
                return false;
            }
        }
    }
}

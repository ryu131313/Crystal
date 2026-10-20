using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirEnvir.Storage;
using Server.WebAdmin.GameData;

namespace Server.WebAdmin.Routes
{
    /// <summary>
    /// B11 — Backups &amp; History.
    /// GET  /api/admin/backups[?kind=database|accounts|scripts]          → summary of every kind, or one kind's files
    /// POST /api/admin/backups/create   {kind: database|accounts, reason?} → copies the live file to "Manual &lt;ts&gt;.bak"
    /// GET  /api/admin/backups/download?kind=&amp;name=                       → the backup file
    /// POST /api/admin/backups/restore  {kind, name, confirm, reason?}     → world STOPPED only (409 otherwise)
    /// POST /api/admin/backups/prune    {kind, olderThanDays, dryRun, keepNewest?}
    /// </summary>
    public sealed class BackupRoutes : IWebAdminRoutes
    {
        public const string Database = "database";
        public const string Accounts = "accounts";
        public const string Scripts = "scripts";

        /// <summary>Serializes create / restore / prune (one file operation at a time).</summary>
        private static readonly object BackupGate = new object();

        public void Register(WebAdminRouter router)
        {
            router.Get("/api/admin/backups", List);
            router.Post("/api/admin/backups/create", Create);
            router.Get("/api/admin/backups/download", Download);
            router.Post("/api/admin/backups/restore", Restore);
            router.Post("/api/admin/backups/prune", Prune);
        }

        // ------------------------------------------------------------------------------------------------ kinds

        private sealed class KindSpec
        {
            public string Kind;
            public string Label;
            public string Root;
            public bool Recursive;
            public string LiveFile;
            public bool Creatable;
            public bool Restorable;
            public bool HasHeader;
        }

        private static string Full(string path) => Path.GetFullPath(path);

        private static KindSpec Spec(string kind)
        {
            switch ((kind ?? string.Empty).Trim().ToLowerInvariant())
            {
                case Database:
                    return new KindSpec
                    {
                        Kind = Database,
                        // Since roadmap P7 the live system data is SystemData.db and its backups are VACUUM INTO copies taken by the
                        // store's writer thread, named like the old binary ones (older Back Up\Database files may still be binary).
                        Label = "Database (SystemData.db)",
                        Root = Full(Envir.DatabaseBackUpPath),
                        LiveFile = Full(SqliteWorldStore.DatabasePath),
                        Creatable = true,
                        Restorable = true,
                        HasHeader = false, // SQLite files have no Version/CustomVersion header (restore checks schema_version)
                    };
                case Accounts:
                    return new KindSpec
                    {
                        Kind = Accounts,
                        Label = "Accounts (Server.MirADB)",
                        Root = Full(Envir.AccountsBackUpPath),
                        LiveFile = Full(Envir.AccountPath),
                        Creatable = true,
                        Restorable = true,
                        HasHeader = true,
                    };
                case Scripts:
                    return new KindSpec
                    {
                        Kind = Scripts,
                        Label = "Script file backups",
                        Root = ScriptsRoot(),
                        Recursive = true,
                    };
                default:
                    throw new WebAdminException(400, "Unknown backup kind (expected database, accounts or scripts)");
            }
        }

        /// <summary>Back Up\WebAdmin\Scripts (the Scripts editor's backup folder); next to the exe if the cwd copy does not exist.</summary>
        private static string ScriptsRoot()
        {
            var cwd = Full(Path.Combine(Envir.BackUpPath, "WebAdmin", "Scripts"));
            if (Directory.Exists(cwd)) return cwd;

            var exe = Full(Path.Combine(AppContext.BaseDirectory, "Back Up", "WebAdmin", "Scripts"));
            return Directory.Exists(exe) ? exe : cwd;
        }

        private static string KindFromBody(JsonElement body) => body.GetString("kind");

        private static object FileDto(BackupFileEntry f)
        {
            return new
            {
                name = f.Name,
                size = f.Size,
                modifiedUtc = f.ModifiedUtc,
                origin = f.Origin,
                version = f.Version,
                customVersion = f.CustomVersion,
                scriptRoot = f.ScriptRoot,
                scriptPath = f.ScriptPath,
                stamp = f.Stamp,
            };
        }

        private static bool VersionSupported(int version) => version >= Envir.MinVersion && version <= Envir.Version;

        // ------------------------------------------------------------------------------------------------ GET list

        private static Task List(WebAdminRequest req)
        {
            var kind = req.Query["kind"];
            var world = WebAdminGame.WorldState;

            if (string.IsNullOrWhiteSpace(kind))
            {
                var kinds = new List<object>();
                foreach (var k in new[] { Database, Accounts, Scripts })
                {
                    var spec = Spec(k);
                    var files = BackupStore.List(spec.Root, spec.Recursive, false);
                    kinds.Add(new
                    {
                        kind = spec.Kind,
                        label = spec.Label,
                        count = files.Count,
                        totalSize = files.Sum(f => f.Size),
                        newestUtc = files.Count > 0 ? files.Max(f => f.ModifiedUtc) : (DateTime?)null,
                        creatable = spec.Creatable,
                        restorable = spec.Restorable,
                    });
                }

                return req.Json(new { ok = true, world, canRestore = world == "stopped", kinds });
            }

            var s = Spec(kind);
            var list = BackupStore.List(s.Root, s.Recursive, s.HasHeader);
            object live = null;

            if (s.LiveFile != null)
            {
                var fi = new FileInfo(s.LiveFile);
                int? version = null;
                if (fi.Exists)
                {
                    if (s.HasHeader && BackupStore.TryReadHeader(s.LiveFile, out var v, out _)) version = v; // MirADB is swapped by rename: a shared read never blocks a save
                }

                live = new
                {
                    name = fi.Name,
                    exists = fi.Exists,
                    size = fi.Exists ? fi.Length : 0,
                    modifiedUtc = fi.Exists ? fi.LastWriteTimeUtc : (DateTime?)null,
                    version,
                };
            }

            return req.Json(new
            {
                ok = true,
                kind = s.Kind,
                label = s.Label,
                world,
                canRestore = s.Restorable && world == "stopped",
                creatable = s.Creatable,
                restorable = s.Restorable,
                directory = s.Root,
                supportedVersions = s.HasHeader ? new { min = Envir.MinVersion, max = Envir.Version } : null,
                live,
                count = list.Count,
                totalSize = list.Sum(f => f.Size),
                files = list.Select(FileDto).ToList(),
            });
        }

        // ------------------------------------------------------------------------------------------------ POST create

        private static async Task Create(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var spec = Spec(KindFromBody(body));
            var reason = body.GetString("reason");

            if (!spec.Creatable)
                throw new WebAdminException(400, "Backups of this kind are created by the Scripts editor, not here");

            if (!File.Exists(spec.LiveFile))
                throw new WebAdminException(404, Path.GetFileName(spec.LiveFile) + " does not exist yet");

            string name;
            long size;

            if (spec.Kind == Database)
            {
                if (Envir.Store is not SqliteWorldStore store)
                    throw new WebAdminException(500, "The game data store is not SQLite (test or tool configuration)");

                // The writer thread takes the copy between saves, so it is never torn; no EditLock needed.
                name = await Task.Run(() =>
                {
                    lock (BackupGate)
                    {
                        Directory.CreateDirectory(spec.Root);
                        var n = BackupStore.NewName(spec.Root, "Manual", DateTime.Now);
                        store.BackupTo(Path.Combine(spec.Root, n));
                        return n;
                    }
                }).ConfigureAwait(false);
                size = new FileInfo(Path.Combine(spec.Root, name)).Length;
            }
            else
            {
                // Server.MirADB is replaced by rename (BeginSaveAccounts/EndSaveAccounts): a FileShare.Delete read never blocks the save,
                // and StableCopy retries when the file is swapped mid-copy.
                lock (BackupGate)
                {
                    Directory.CreateDirectory(spec.Root);
                    name = BackupStore.NewName(spec.Root, "Manual", DateTime.Now);
                    size = BackupStore.StableCopy(spec.LiveFile, Path.Combine(spec.Root, name));
                }
            }

            int? version = null;
            if (BackupStore.TryReadHeader(Path.Combine(spec.Root, name), out var v, out _)) version = v;

            WebAdminAudit.Record(req, "backup.create", spec.Kind + ":" + name, new { kind = spec.Kind, name, size, version }, reason);

            await req.Ok(new { kind = spec.Kind, name, size, version }, "Backup created: " + name).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------------------------------------ GET download

        public const long MaxDownloadBytes = 1024L * 1024 * 1024;

        private static Task Download(WebAdminRequest req)
        {
            var spec = Spec(req.Query["kind"]);
            var path = BackupStore.Resolve(spec.Root, req.Query["name"], spec.Recursive);

            var fi = new FileInfo(path);
            if (!fi.Exists) throw new WebAdminException(404, "Backup not found");
            if (fi.Length > MaxDownloadBytes) throw new WebAdminException(413, "Backup is too large to download through the web panel");

            byte[] data;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream((int)fi.Length))
            {
                stream.CopyTo(ms);
                data = ms.ToArray();
            }

            var relative = Path.GetRelativePath(spec.Root, path).Replace('\\', '/');
            WebAdminAudit.Record(req, "backup.download", spec.Kind + ":" + relative, new { kind = spec.Kind, name = relative, size = data.Length });

            var downloadName = (spec.Kind == Scripts ? relative.Replace('/', '_') : Path.GetFileName(path));
            return req.Bytes(data, "application/octet-stream", downloadName);
        }

        // ------------------------------------------------------------------------------------------------ POST restore

        private static async Task Restore(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var spec = Spec(KindFromBody(body));
            var name = body.GetString("name");
            var confirm = body.GetString("confirm");
            var reason = body.GetString("reason");

            if (!spec.Restorable)
                throw new WebAdminException(400, "Script backups are restored from the Scripts & Files editor");

            if (string.IsNullOrWhiteSpace(name))
                throw new WebAdminException(400, "Backup name required");

            if (!string.Equals((confirm ?? string.Empty).Trim(), name.Trim(), StringComparison.Ordinal))
                throw new WebAdminException(400, "Type the backup name exactly to confirm the restore");

            var path = BackupStore.Resolve(spec.Root, name, false);
            if (!File.Exists(path)) throw new WebAdminException(404, "Backup not found");

            string preName = null;
            var editReloaded = false;
            var accountsReloaded = false;
            List<MagicInfo> restoredMagic = null;

            lock (BackupGate)
            {
                // Lock order everywhere: EditLock, then Envir.LoadLock (B8 reload-edit and B1 start take EditLock and reach LoadLock
                // through Envir.Edit.LoadDB()), then Envir.AccountLock. Holding LoadLock across the state check and the swap also closes
                // the start race: Main.LoadDB() / LoadAccounts() take LoadLock, so a world started after the check waits for the swap
                // and loads the restored file.
                if (spec.Kind == Database)
                {
                    if (!Monitor.TryEnter(WebAdminGame.EditLock, 10000))
                        throw new WebAdminException(409, "The game database is being saved by another action; try again in a moment");

                    try
                    {
                        lock (Envir.LoadLock)
                        {
                            EnsureStoppedForRestore(spec);
                            CheckBackupHeader(spec, path);
                            preName = WithStoreClosed(() => SwapIn(req, spec, name, path, reason));

                            editReloaded = ReloadEditDatabase();
                            if (!editReloaded)
                            {
                                // Put the previous database back so the editor copy and the file stay consistent.
                                if (preName != null)
                                {
                                    WithStoreClosed(() => { BackupStore.ReplaceFile(Path.Combine(spec.Root, preName), spec.LiveFile); return 0; });
                                    ReloadEditDatabase();
                                }

                                WebAdminAudit.Record(req, "backup.restore-failed", spec.Kind + ":" + name, new { kind = spec.Kind, name, preRestore = preName, rolledBack = preName != null }, reason);
                                throw new WebAdminException(422, "The backup could not be loaded" + (preName != null ? "; the previous database was put back" : string.Empty));
                            }

                            // The editor copy was replaced: its unsaved web edits are gone.
                            DiscardGameDataUnsaved();
                            GameDataState.EditReloadFailed = false;

                            // Main never reloads MagicInfo (Main.LoadDB only adds missing spells) and its periodic SaveDB would write
                            // its old spells over the restored ones: leave the restored spells pending for Main, replacing any older
                            // snapshot (synced below, or by the WorldStarted hook).
                            restoredMagic = GameDataSave.AfterFileChanged(false).Magic;
                        }
                    }
                    finally
                    {
                        Monitor.Exit(WebAdminGame.EditLock);
                    }
                }
                else
                {
                    // AccountLock (after LoadLock, the documented order) across the check, the swap and the reload: inline account and
                    // character edits save Server.MirADB under AccountLock while the world is stopped, and must not write the
                    // pre-restore list over the restored file between the swap and the reload.
                    lock (Envir.LoadLock)
                    lock (Envir.AccountLock)
                    {
                        EnsureStoppedForRestore(spec);
                        CheckBackupHeader(spec, path);
                        preName = SwapIn(req, spec, name, path, reason);

                        // Envir.Main.AccountList still holds the pre-restore accounts, and other features write it back with
                        // SaveAccounts() while the world is stopped: reload it from the restored file (only when it was loaded at all;
                        // loading accounts before Main's database would drop every item that cannot be bound).
                        if (Envir.Main.AccountList.Count > 0)
                        {
                            accountsReloaded = ReloadMainAccounts();
                            if (!accountsReloaded)
                            {
                                if (preName != null)
                                {
                                    BackupStore.ReplaceFile(Path.Combine(spec.Root, preName), spec.LiveFile);
                                    if (!ReloadMainAccounts()) ClearMainAccounts();
                                }
                                else
                                {
                                    ClearMainAccounts();
                                }

                                WebAdminAudit.Record(req, "backup.restore-failed", spec.Kind + ":" + name, new { kind = spec.Kind, name, preRestore = preName, rolledBack = preName != null }, reason);
                                throw new WebAdminException(422, "The backup could not be loaded" + (preName != null ? "; the previous accounts file was put back" : string.Empty));
                            }
                        }
                    }
                }
            }

            var magicApplied = 0;
            if (restoredMagic != null)
                magicApplied = (await GameDataSave.SyncMagicAsync(restoredMagic).ConfigureAwait(false)).Changed;

            WebAdminAudit.Record(req, "backup.restore", spec.Kind + ":" + name, new { kind = spec.Kind, name, preRestore = preName, editReloaded, accountsReloaded }, reason);

            var message = spec.Kind == Database
                ? "Database restored from " + name + ". The editor copy was reloaded and its unsaved changes discarded; start the world to load it." +
                  (magicApplied > 0 ? " The restored spell (magic) settings were also applied to the server's spell list." : string.Empty)
                : "Accounts restored from " + name + "." + (accountsReloaded
                    ? " The account list in memory was reloaded from it."
                    : " Start the world to load them.");

            await req.Ok(new { kind = spec.Kind, name, preRestore = preName, editReloaded, accountsReloaded, magicApplied }, message).ConfigureAwait(false);
        }

        private static void EnsureStoppedForRestore(KindSpec spec)
        {
            var world = WebAdminGame.WorldState;
            if (world != "stopped" || !WebAdminGame.IsFullyStopped(WebAdminGame.World))
                throw new WebAdminException(409, "Stop the world before restoring a backup (it is " + world + ")", new { world });

            if (spec.Kind == Accounts && Envir.Main.Saving)
                throw new WebAdminException(409, "An account save is still being written; try again in a moment");
        }

        /// <summary>
        /// Runs a file swap of the live database. With the SQLite store it runs on the writer thread with the connection closed (the next
        /// load reopens it and runs integrity_check); the writer takes no web admin lock, so waiting here under EditLock + LoadLock is safe.
        /// </summary>
        private static T WithStoreClosed<T>(Func<T> swap)
        {
            if (Envir.Store is not SqliteWorldStore store) return swap();
            return store.Writer.Run(w =>
            {
                w.CloseConnection();
                return swap();
            });
        }

        private static void CheckBackupHeader(KindSpec spec, string path)
        {
            if (spec.Kind == Database)
            {
                if (!SqliteWorldFile.LooksLikeSqlite(path))
                    throw new WebAdminException(422, "The backup is a binary Server.MirDB file; the server stores system data in SystemData.db now. Import it offline with MirDbExport (Knowledgebase/Runbook.md)");
                try
                {
                    using var conn = SqliteWorldFile.Open(path, create: false);
                    SqliteWorldFile.CheckIntegrity(conn);
                    SqliteWorldFile.CheckSchema(conn);
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is Microsoft.Data.Sqlite.SqliteException)
                {
                    throw new WebAdminException(422, "The backup is not a usable SystemData database: " + ex.Message);
                }

                return;
            }

            if (!BackupStore.TryReadHeader(path, out var version, out _))
                throw new WebAdminException(422, "The backup is not a valid " + spec.Kind + " file (unreadable header)");
            if (!VersionSupported(version))
                throw new WebAdminException(422, "The backup has version " + version + "; this server supports " + Envir.MinVersion + "–" + Envir.Version);
        }

        /// <summary>Copies the live file to "Pre-restore &lt;ts&gt;.bak", then replaces it with the backup. File errors are audited (500).</summary>
        private static string SwapIn(WebAdminRequest req, KindSpec spec, string name, string path, string reason)
        {
            string preName = null;

            try
            {
                if (File.Exists(spec.LiveFile))
                {
                    preName = BackupStore.NewName(spec.Root, "Pre-restore", DateTime.Now);
                    BackupStore.StableCopy(spec.LiveFile, Path.Combine(spec.Root, preName));
                }

                // Temp file + move: on failure the live file is untouched.
                BackupStore.ReplaceFile(path, spec.LiveFile);
                return preName;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                WebAdminLog.Error(ex, "Restoring " + spec.Kind + " backup " + name + " failed");
                WebAdminAudit.Record(req, "backup.restore-failed", spec.Kind + ":" + name, new { kind = spec.Kind, name, preRestore = preName, rolledBack = false, error = "file in use or access denied" }, reason);
                throw new WebAdminException(500, "The backup could not be copied over " + Path.GetFileName(spec.LiveFile) + " (file in use or access denied); nothing was changed");
            }
        }

        /// <summary>
        /// Reloads Envir.Edit from Server.MirDB. The caller holds EditLock (then LoadLock; LoadDB takes LoadLock itself, re-entrantly).
        /// LoadDB() does not clear MagicInfoList (it only adds missing spells), so it is cleared first.
        /// </summary>
        private static bool ReloadEditDatabase()
        {
            var edit = Envir.Edit;
            var magicBackup = edit.MagicInfoList.ToList();

            try
            {
                edit.MagicInfoList.Clear();
                return edit.LoadDB();
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Reloading the editor database after a restore failed");
                if (edit.MagicInfoList.Count == 0) edit.MagicInfoList.AddRange(magicBackup);
                return false;
            }
        }

        /// <summary>World fully stopped, caller holds LoadLock: re-reads Server.MirADB into Envir.Main under AccountLock.</summary>
        private static bool ReloadMainAccounts()
        {
            try
            {
                lock (Envir.AccountLock)
                {
                    Envir.Main.LoadAccounts();
                    WebAdminHooks.RaiseAccountsLoaded();
                }

                return true;
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Reloading the accounts after a restore failed");
                return false;
            }
        }

        /// <summary>Last resort when no account file could be loaded: an empty list is never written back by the other features.</summary>
        private static void ClearMainAccounts()
        {
            lock (Envir.AccountLock)
            {
                Envir.Main.AccountList.Clear();
                Envir.Main.CharacterList.Clear();
                AccountRoutes.MarkAccountsUnloaded();
            }
        }

        /// <summary>Clears the Game Data unsaved-change tracker (B8's internal GameDataState) when that feature is part of the build.</summary>
        private static void DiscardGameDataUnsaved()
        {
            try
            {
                var type = typeof(BackupRoutes).Assembly.GetType("Server.WebAdmin.GameData.GameDataState", false);
                var unsaved = type?.GetProperty("Unsaved", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                unsaved?.GetType().GetMethod("Discard", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.Invoke(unsaved, null);
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Clearing the Game Data unsaved-change count after a restore failed");
            }
        }

        // ------------------------------------------------------------------------------------------------ POST prune

        private static async Task Prune(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var spec = Spec(KindFromBody(body));
            var olderThanDays = body.GetInt("olderThanDays", 0);
            var keepNewest = body.GetInt("keepNewest", 1);
            var dryRun = body.GetBool("dryRun", true);
            var reason = body.GetString("reason");

            if (olderThanDays < 1 || olderThanDays > 3650)
                throw new WebAdminException(400, "olderThanDays must be between 1 and 3650");
            if (keepNewest < 1 || keepNewest > 1000)
                throw new WebAdminException(400, "keepNewest must be between 1 and 1000");

            var deleted = new List<string>();
            var failed = new List<object>();
            BackupPrunePlan plan;

            lock (BackupGate)
            {
                var files = BackupStore.List(spec.Root, spec.Recursive, false);
                plan = BackupStore.PlanPrune(files, olderThanDays, DateTime.UtcNow, keepNewest);

                if (!dryRun)
                {
                    foreach (var f in plan.Delete)
                    {
                        try
                        {
                            var path = BackupStore.Resolve(spec.Root, f.Name, spec.Recursive);
                            File.Delete(path);
                            deleted.Add(f.Name);
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is WebAdminException)
                        {
                            failed.Add(new { name = f.Name, error = ex is WebAdminException ? ex.Message : "file in use or access denied" });
                        }
                    }

                    if (spec.Recursive) BackupStore.RemoveEmptyDirectories(spec.Root);
                }
            }

            if (!dryRun)
            {
                WebAdminAudit.Record(req, "backup.prune", spec.Kind, new
                {
                    kind = spec.Kind,
                    olderThanDays,
                    keepNewest,
                    deleted = deleted.Count,
                    bytes = plan.Delete.Where(f => deleted.Contains(f.Name)).Sum(f => f.Size),
                    names = deleted.Take(100).ToList(),
                    failed = failed.Count,
                }, reason);
            }

            var message = dryRun
                ? plan.Delete.Count + " backup(s) older than " + olderThanDays + " day(s) would be deleted"
                : "Deleted " + deleted.Count + " backup(s)" + (failed.Count > 0 ? ", " + failed.Count + " failed" : string.Empty);

            await req.Ok(new
            {
                kind = spec.Kind,
                dryRun,
                olderThanDays,
                keepNewest,
                count = plan.Delete.Count,
                bytes = plan.Delete.Sum(f => f.Size),
                kept = plan.Keep.Count,
                files = plan.Delete.Select(FileDto).ToList(),
                deleted = dryRun ? null : deleted,
                failed = dryRun ? null : failed,
            }, message).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------------------------------------ pure file logic (unit-tested)

    public sealed class BackupFileEntry
    {
        /// <summary>Path relative to the kind's root, '/'-separated.</summary>
        public string Name { get; set; }
        public long Size { get; set; }
        public DateTime ModifiedUtc { get; set; }

        /// <summary>"manual" | "pre-restore" | "auto" | "script" | "other".</summary>
        public string Origin { get; set; }

        public int? Version { get; set; }
        public int? CustomVersion { get; set; }
        public string ScriptRoot { get; set; }
        public string ScriptPath { get; set; }
        public string Stamp { get; set; }
    }

    public sealed class BackupPrunePlan
    {
        public List<BackupFileEntry> Delete { get; set; } = new List<BackupFileEntry>();
        public List<BackupFileEntry> Keep { get; set; } = new List<BackupFileEntry>();
    }

    public static class BackupStore
    {
        public const string Extension = ".bak";

        private static readonly char[] Separators = { '/', '\\' };
        private static readonly char[] Forbidden = { ':', '*', '?', '"', '<', '>', '|', '\0' };

        private static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        private static readonly Regex ScriptBackupPattern = new Regex(
            @"^(?<root>[^/]+)/(?<path>.+)\.(?<stamp>\d{8}-\d{6})\.bak$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Resolves a client-supplied backup name to a full path strictly inside <paramref name="root"/>. Rejects empty names, rooted paths,
        /// '..' / '.', drive or ADS colons, wildcard / invalid characters, reserved device names, trailing dots/spaces, non-.bak files,
        /// sub-folders (unless allowed) and any existing reparse point (symlink / junction) on the way. Throws 400.
        /// </summary>
        public static string Resolve(string root, string name, bool allowSubdirs)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("root required", nameof(root));
            if (string.IsNullOrWhiteSpace(name) || name.Length > 400) throw Invalid();

            if (Path.IsPathRooted(name) || name.StartsWith("/", StringComparison.Ordinal) || name.StartsWith("\\", StringComparison.Ordinal))
                throw Invalid();

            if (name.IndexOfAny(Forbidden) >= 0 || name.Any(c => c < 0x20)) throw Invalid();

            var segments = name.Split(Separators);
            if (segments.Length > 1 && !allowSubdirs) throw Invalid();

            var invalid = Path.GetInvalidFileNameChars();
            foreach (var seg in segments)
            {
                if (seg.Length == 0 || seg == "." || seg == "..") throw Invalid();
                if (seg.IndexOfAny(invalid) >= 0) throw Invalid();
                if (seg.EndsWith(".", StringComparison.Ordinal) || seg.EndsWith(" ", StringComparison.Ordinal) || seg.StartsWith(" ", StringComparison.Ordinal)) throw Invalid();

                var stem = seg.Split('.')[0];
                if (ReservedNames.Contains(stem.Trim())) throw Invalid();
            }

            if (!name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) throw Invalid();

            var rootFull = Path.GetFullPath(root);
            var rootPrefix = rootFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? rootFull : rootFull + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(rootFull, Path.Combine(segments)));

            if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) throw Invalid();

            // No symlink / junction anywhere between the root and the file.
            var current = rootFull;
            foreach (var seg in segments)
            {
                current = Path.Combine(current, seg);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) throw Invalid();
            }

            if (Directory.Exists(rootFull) && (new DirectoryInfo(rootFull).Attributes & FileAttributes.ReparsePoint) != 0) throw Invalid();

            return full;
        }

        private static WebAdminException Invalid() => new WebAdminException(400, "Invalid backup name");

        /// <summary>*.bak files under <paramref name="root"/> (skips reparse points), newest first. Missing folder → empty.</summary>
        public static List<BackupFileEntry> List(string root, bool recursive, bool readHeader)
        {
            var list = new List<BackupFileEntry>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return list;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                MatchCasing = MatchCasing.CaseInsensitive,
            };

            var rootFull = Path.GetFullPath(root);

            foreach (var path in Directory.EnumerateFiles(rootFull, "*" + Extension, options))
            {
                FileInfo fi;
                try
                {
                    fi = new FileInfo(path);
                    if (!fi.Exists) continue;
                }
                catch (Exception)
                {
                    continue;
                }

                // EnumerateFiles' "*.bak" also matches longer extensions on Windows (8.3 quirk); keep exact .bak only.
                if (!fi.Name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) continue;

                var name = Path.GetRelativePath(rootFull, fi.FullName).Replace('\\', '/');
                var entry = new BackupFileEntry
                {
                    Name = name,
                    Size = fi.Length,
                    ModifiedUtc = DateTime.SpecifyKind(fi.LastWriteTimeUtc, DateTimeKind.Utc),
                    Origin = recursive ? "script" : OriginOf(name),
                };

                if (recursive && TryParseScriptBackup(name, out var scriptRoot, out var scriptPath, out var stamp))
                {
                    entry.ScriptRoot = scriptRoot;
                    entry.ScriptPath = scriptPath;
                    entry.Stamp = stamp;
                }

                if (readHeader && TryReadHeader(fi.FullName, out var version, out var custom))
                {
                    entry.Version = version;
                    entry.CustomVersion = custom;
                }

                list.Add(entry);
            }

            return list.OrderByDescending(f => f.ModifiedUtc).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>"Manual …" → manual, "Pre-restore …" → pre-restore, Crystal's "Database …"/"Accounts …" → auto, else other.</summary>
        public static string OriginOf(string name)
        {
            var file = Path.GetFileName(name ?? string.Empty);
            if (file.StartsWith("Manual ", StringComparison.OrdinalIgnoreCase)) return "manual";
            if (file.StartsWith("Pre-restore ", StringComparison.OrdinalIgnoreCase)) return "pre-restore";
            if (file.StartsWith("Database ", StringComparison.OrdinalIgnoreCase) || file.StartsWith("Accounts ", StringComparison.OrdinalIgnoreCase)) return "auto";
            return "other";
        }

        /// <summary>Scripts editor backups: "&lt;root&gt;/&lt;path&gt;.&lt;yyyyMMdd-HHmmss&gt;.bak".</summary>
        public static bool TryParseScriptBackup(string name, out string root, out string path, out string stamp)
        {
            root = path = stamp = null;
            if (string.IsNullOrEmpty(name)) return false;

            var m = ScriptBackupPattern.Match(name.Replace('\\', '/'));
            if (!m.Success) return false;

            root = m.Groups["root"].Value;
            path = m.Groups["path"].Value;
            stamp = m.Groups["stamp"].Value;
            return true;
        }

        /// <summary>"&lt;prefix&gt; yyyy-MM-dd HH-mm-ss.bak" (Crystal's own backup naming), with " (n)" when taken.</summary>
        public static string NewName(string dir, string prefix, DateTime localNow)
        {
            var stamp = localNow.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
            var name = prefix + " " + stamp + Extension;

            for (var n = 2; File.Exists(Path.Combine(dir, name)); n++)
                name = prefix + " " + stamp + " (" + n.ToString(CultureInfo.InvariantCulture) + ")" + Extension;

            return name;
        }

        /// <summary>Reads the two leading Int32s (Version, CustomVersion) of a Crystal binary database / accounts file.</summary>
        public static bool TryReadHeader(string path, out int version, out int customVersion)
        {
            version = customVersion = 0;

            if (SqliteWorldFile.LooksLikeSqlite(path)) return false; // "SQLi" would read as version 1766609235

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 8) return false;
                    version = reader.ReadInt32();
                    customVersion = reader.ReadInt32();
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Copies <paramref name="source"/> to <paramref name="destination"/> (which must not exist) while the source may be rewritten or
        /// swapped by rename: the copy is accepted only if the source's length and timestamp did not change while copying; otherwise it is
        /// retried. The destination appears atomically (temp file + move). Returns the copied byte count.
        /// </summary>
        public static long StableCopy(string source, string destination, int attempts = 5, Action<int> wait = null)
        {
            wait ??= attempt => Thread.Sleep(100 * attempt);
            Exception last = null;

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                var temp = destination + ".tmp-" + Guid.NewGuid().ToString("N");

                try
                {
                    var before = new FileInfo(source);
                    if (!before.Exists) throw new FileNotFoundException("Source file not found", source);
                    var beforeLength = before.Length;
                    var beforeWrite = before.LastWriteTimeUtc;

                    long copied;
                    using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var dst = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        src.CopyTo(dst);
                        copied = dst.Length;
                    }

                    var after = new FileInfo(source);
                    if (after.Exists && after.Length == beforeLength && after.LastWriteTimeUtc == beforeWrite && copied == beforeLength)
                    {
                        File.Move(temp, destination);
                        return copied;
                    }

                    last = new IOException("The source file changed while it was being copied");
                }
                catch (IOException ex)
                {
                    last = ex;
                }
                finally
                {
                    TryDelete(temp);
                }

                if (attempt < attempts) wait(attempt);
            }

            throw last ?? new IOException("Copy failed");
        }

        /// <summary>Writes <paramref name="data"/> to a new file atomically (temp + move). The destination must not exist.</summary>
        public static void WriteAllBytesAtomic(string destination, byte[] data)
        {
            var temp = destination + ".tmp-" + Guid.NewGuid().ToString("N");

            try
            {
                using (var dst = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    dst.Write(data, 0, data.Length);
                    dst.Flush(true);
                }

                File.Move(temp, destination);
            }
            finally
            {
                TryDelete(temp);
            }
        }

        /// <summary>Replaces <paramref name="target"/> with a copy of <paramref name="source"/> (copy to a temp file next to it, then move over).</summary>
        public static void ReplaceFile(string source, string target)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(target));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var temp = target + ".restore-" + Guid.NewGuid().ToString("N");

            try
            {
                using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var dst = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    src.CopyTo(dst);
                    dst.Flush(true);
                }

                File.Move(temp, target, true);
            }
            finally
            {
                TryDelete(temp);
            }
        }

        /// <summary>Files strictly older than <paramref name="olderThanDays"/>, never touching the newest <paramref name="keepNewest"/> files.</summary>
        public static BackupPrunePlan PlanPrune(IEnumerable<BackupFileEntry> files, int olderThanDays, DateTime nowUtc, int keepNewest)
        {
            var plan = new BackupPrunePlan();
            var cutoff = nowUtc.AddDays(-Math.Max(0, olderThanDays));
            var ordered = (files ?? Enumerable.Empty<BackupFileEntry>())
                .OrderByDescending(f => f.ModifiedUtc)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var i = 0; i < ordered.Count; i++)
            {
                var f = ordered[i];
                if (i >= Math.Max(0, keepNewest) && f.ModifiedUtc < cutoff) plan.Delete.Add(f);
                else plan.Keep.Add(f);
            }

            return plan;
        }

        /// <summary>Removes empty sub-folders below <paramref name="root"/> (never the root itself; skips reparse points).</summary>
        public static void RemoveEmptyDirectories(string root)
        {
            if (!Directory.Exists(root)) return;

            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var dirs = Directory.EnumerateDirectories(root, "*", options).OrderByDescending(d => d.Length).ToList();

            foreach (var d in dirs)
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
                }
                catch (Exception)
                {
                    // in use / no access: leave it
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
                // best effort
            }
        }
    }
}

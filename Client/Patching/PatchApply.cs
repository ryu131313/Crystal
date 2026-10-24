using System.Diagnostics;
using System.Text;

namespace Client.Patching
{
    /// <summary>
    /// Program updates (in-game patcher plan §4.2 "Program updates mid-session", phase 4): applying a fully staged set of
    /// program files at exit or on Restart, proving the new build, and rolling back one that never reaches the game.
    ///
    /// BCL ONLY: <see cref="Startup"/> is the first thing Program.Main does, before Settings, any Shared type or any library
    /// is touched, so it still runs when the new Client.dll and an old Shared.dll do not match. Paths come from the exe's
    /// folder, never from Settings.
    ///
    /// Files in Patch\: Applying.bin (text: "starts=N", then one line per file: put|remove, path, new size, new sha, old size,
    /// old sha), Healthy.bin (written when the new build reaches CMain), Rejected.bin (sha256 of builds that failed to
    /// start: never staged again), Staged\ + Staged.bin (PatchService). The replaced files stay as Old__&lt;name&gt; beside the
    /// target until the new build is proven.
    /// </summary>
    internal static class PatchApply
    {
        internal sealed class Entry
        {
            public bool Put;
            public string Path;
            public long NewSize, OldSize;
            public string NewSha = string.Empty, OldSha = string.Empty;
        }

        private static readonly string Root = AppContext.BaseDirectory;
        private static readonly string Dir = Path.Combine(Root, "Patch");
        internal static readonly string ApplyingFile = Path.Combine(Dir, "Applying.bin");
        internal static readonly string HealthyFile = Path.Combine(Dir, "Healthy.bin");
        internal static readonly string RejectedFile = Path.Combine(Dir, "Rejected.bin");
        internal static readonly string StagedDir = Path.Combine(Dir, "Staged");
        internal static readonly string StagedFile = Path.Combine(Dir, "Staged.bin");
        private static readonly string StateFile = Path.Combine(Dir, "State.bin");
        private static readonly string PendingFile = Path.Combine(Dir, "Pending.log");
        private static readonly string LockFile = Path.Combine(Dir, "patch.lock");

        /// <summary>
        /// patch.lock, taken by Startup when Applying.bin exists and held through the proving start: only the holder may
        /// count, roll back or finish a build, so a second instance started meanwhile cannot reject a good one. Handed to
        /// PatchService (<see cref="TakeLock"/>) or released by MarkHealthy.
        /// </summary>
        private static FileStream _lock;
        /// <summary>This process counted the proving start, so its CMain_Load proves the build.</summary>
        private static bool _proving;

        /// <summary>A message for Error.txt once Settings is loaded (Startup runs before logging is possible).</summary>
        public static string PendingLog;

        /// <summary>True when Startup rolled back a failed build and started the old one: Main must return at once.</summary>
        public static bool ExitNow;

        public static bool Applying => File.Exists(ApplyingFile);

        // — start —

        /// <summary>
        /// Program.Main, first: wait for a restarting parent (--wait-pid, up to 30 s) before touching any file, then:
        /// Applying.bin without Healthy.bin and starts = 0 → this is the proving start (count it, run); starts ≥ 1 → the
        /// last start never reached the game → restore the Old__ files, reject the build, start the old build and exit.
        /// All of that only with patch.lock: when another instance holds it (it is proving the build or running the game),
        /// this one leaves Applying.bin alone and just runs.
        /// </summary>
        public static void Startup(string[] args)
        {
            WaitForParent(args);
            try
            {
                if (File.Exists(PendingFile))
                {
                    PendingLog = File.ReadAllText(PendingFile);
                    File.Delete(PendingFile);
                }
                if (!File.Exists(ApplyingFile)) return;
                try { _lock = new FileStream(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { return; } // another instance owns Patch\ (proving the build or in the game)
                if (!File.Exists(ApplyingFile)) { ReleaseLock(); return; } // finished by the instance that just let go
                var (starts, entries) = Read(ApplyingFile);
                string text = File.ReadAllText(ApplyingFile);
                if (File.Exists(HealthyFile) && File.ReadAllText(HealthyFile) == text.Substring(text.IndexOf('\n') + 1))
                {
                    FinishHealthy(entries); // proven last time; the clean-up was interrupted
                    ReleaseLock();
                    return;
                }
                if (starts == 0)
                {
                    Write(ApplyingFile, 1, entries);
                    _proving = true; // keep the lock until MarkHealthy or PatchService takes it
                    return;
                }
                Rollback(entries, reject: true);
                // This process exits at once; the restarted old build logs the message (Startup reads PendingFile).
                File.WriteAllText(PendingFile, "[Patch] the new build never reached the game: rolled back to the previous build");
                ReleaseLock(); // before Restart: the old build waits for this process anyway (--wait-pid)
                Restart(args);
                ExitNow = true;
            }
            catch (Exception ex)
            {
                PendingLog = "[Patch] startup recovery: " + ex.Message;
                if (!_proving) ReleaseLock();
            }
        }

        /// <summary>PatchService.Start: the patch.lock Startup still holds (the proving start), or null. Thread-safe.</summary>
        internal static FileStream TakeLock() => Interlocked.Exchange(ref _lock, null);

        private static void ReleaseLock() => Interlocked.Exchange(ref _lock, null)?.Dispose();

        /// <summary>CMain_Load: the new build reached the game. Record it, then drop the Old__ copies, Applying.bin and Staged\.</summary>
        public static void MarkHealthy()
        {
            if (!_proving) return; // another instance is proving the build (or nothing is applied)
            _proving = false;
            try
            {
                if (!File.Exists(ApplyingFile)) return;
                string text = File.ReadAllText(ApplyingFile);
                File.WriteAllText(HealthyFile, text.Substring(text.IndexOf('\n') + 1));
                FinishHealthy(Read(ApplyingFile).Entries);
            }
            catch (Exception ex)
            {
                PendingLog = "[Patch] could not finish the update: " + ex.Message;
            }
            finally
            {
                ReleaseLock(); // no-op once PatchService took it
            }
        }

        private static void FinishHealthy(List<Entry> entries)
        {
            foreach (var e in entries) TryDelete(OldPath(e.Path));
            TryDeleteDirectory(StagedDir);
            TryDelete(StagedFile);
            TryDelete(ApplyingFile);
            TryDelete(HealthyFile);
        }

        // — apply —

        /// <summary>
        /// At exit (after PatchService.Stop saved State.bin, while patch.lock is still held): Applying.bin first, then each
        /// target → Old__&lt;name&gt; (Windows lets a loaded DLL or the apphost exe be renamed) and the staged file COPIED in
        /// (Staged\ stays complete until the build is proven), then State.bin records the new files. Any failure restores
        /// every Old__ file, removes Applying.bin and keeps Staged\. Returns false on failure.
        /// </summary>
        public static bool Apply(List<Entry> entries)
        {
            if (entries == null || entries.Count == 0) return false;
            var done = new List<Entry>();
            try
            {
                Write(ApplyingFile, 0, entries);
                TryDelete(HealthyFile);
                foreach (var e in entries)
                {
                    string target = Local(e.Path), old = OldPath(e.Path);
                    if (File.Exists(old)) File.Delete(old);
                    if (File.Exists(target)) File.Move(target, old);
                    done.Add(e);
                    if (e.Put)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(Path.Combine(StagedDir, e.Path.Replace('/', Path.DirectorySeparatorChar)), target, overwrite: false);
                    }
                }
                UpdateState(entries, forward: true);
                return true;
            }
            catch (Exception ex)
            {
                Restore(done);
                TryDelete(ApplyingFile);
                PendingLog = "[Patch] the update could not be applied: " + ex.Message;
                return false;
            }
        }

        /// <summary>Starts this exe again with --wait-pid (it waits for this process to exit) and the original arguments.</summary>
        public static void Restart(string[] args)
        {
            var sb = new StringBuilder("--wait-pid ").Append(Environment.ProcessId);
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--wait-pid") { i++; continue; }
                sb.Append(" \"").Append(args[i].Replace("\"", "")).Append('"');
            }
            Process.Start(new ProcessStartInfo(Environment.ProcessPath, sb.ToString()) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory });
        }

        private static void WaitForParent(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] != "--wait-pid" || !int.TryParse(args[i + 1], out int pid)) continue;
                try
                {
                    using var parent = Process.GetProcessById(pid);
                    parent.WaitForExit(30_000);
                }
                catch (ArgumentException) { } // already gone
                catch (InvalidOperationException) { }
                return;
            }
        }

        // — rollback —

        private static void Rollback(List<Entry> entries, bool reject)
        {
            Restore(entries);
            UpdateState(entries, forward: false);
            if (reject)
            {
                var sb = new StringBuilder();
                foreach (var e in entries) if (e.Put) sb.Append(e.NewSha).Append('\n');
                File.AppendAllText(RejectedFile, sb.ToString());
                TryDeleteDirectory(StagedDir);
                TryDelete(StagedFile);
            }
            TryDelete(ApplyingFile);
            TryDelete(HealthyFile);
        }

        /// <summary>
        /// Puts every Old__ copy back (a put whose target did not exist before is removed). The current file is renamed into
        /// Patch\Trash rather than deleted: during a start-up rollback this very process has the new Client.dll loaded, and
        /// Windows renames a loaded file but will not delete it. Trash is emptied at the next start.
        /// </summary>
        private static void Restore(List<Entry> entries)
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                string target = Local(e.Path), old = OldPath(e.Path);
                try
                {
                    if (File.Exists(old))
                    {
                        if (File.Exists(target)) File.Move(target, TrashPath(target));
                        File.Move(old, target);
                    }
                    else if (e.Put && e.OldSha.Length == 0 && File.Exists(target)) File.Move(target, TrashPath(target));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    PendingLog = "[Patch] could not restore " + e.Path + ": " + ex.Message;
                }
            }
        }

        /// <summary>State.bin follows the files on disk: forward = the new set, back = what was recorded before.</summary>
        private static void UpdateState(List<Entry> entries, bool forward)
        {
            var state = PatchState.TryLoad(StateFile) ?? new PatchState();
            foreach (var e in entries)
            {
                bool present = forward ? e.Put : e.OldSha.Length > 0;
                if (present) state.Files[e.Path] = forward ? (e.NewSize, e.NewSha) : (e.OldSize, e.OldSha);
                else state.Files.Remove(e.Path);
            }
            state.Save(StateFile);
        }

        public static HashSet<string> LoadRejected()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(RejectedFile))
                    foreach (var line in File.ReadAllLines(RejectedFile)) if (line.Trim().Length > 0) set.Add(line.Trim());
            }
            catch (IOException) { }
            return set;
        }

        // — files —

        private static string TrashPath(string local)
        {
            string trash = Path.Combine(Dir, "Trash");
            Directory.CreateDirectory(trash);
            return Path.Combine(trash, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(local));
        }

        private static string Local(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

        private static string OldPath(string rel)
        {
            string local = Local(rel);
            return Path.Combine(Path.GetDirectoryName(local), "Old__" + Path.GetFileName(local));
        }

        private static void Write(string file, int starts, List<Entry> entries)
        {
            var sb = new StringBuilder("starts=").Append(starts).Append('\n');
            foreach (var e in entries)
                sb.Append(e.Put ? "put" : "remove").Append('\t').Append(e.Path).Append('\t').Append(e.NewSize).Append('\t').Append(e.NewSha)
                  .Append('\t').Append(e.OldSize).Append('\t').Append(e.OldSha).Append('\n');
            PatchFiles.WriteAtomic(file, Encoding.UTF8.GetBytes(sb.ToString()));
        }

        private static (int Starts, List<Entry> Entries) Read(string file)
        {
            var lines = File.ReadAllText(file).Split('\n');
            int starts = lines.Length > 0 && lines[0].StartsWith("starts=") && int.TryParse(lines[0].Substring(7), out int n) ? n : 1;
            var entries = new List<Entry>();
            for (int i = 1; i < lines.Length; i++)
            {
                var f = lines[i].Split('\t');
                if (f.Length < 6) continue;
                entries.Add(new Entry
                {
                    Put = f[0] == "put", Path = f[1],
                    NewSize = long.TryParse(f[2], out long ns) ? ns : 0, NewSha = f[3],
                    OldSize = long.TryParse(f[4], out long os) ? os : 0, OldSha = f[5],
                });
            }
            return (starts, entries);
        }

        private static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static void TryDeleteDirectory(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace Client.Patching
{
    /// <summary>
    /// The in-game patcher (in-game patcher plan §4.2): downloads the relay's raw client files (WebRelay --patch-root) into
    /// the client folder in the background while the game runs (no launcher window since 2026-10-20: core and starter files
    /// are simply first in the queue, and anything the game draws is demanded).
    ///
    /// Threads (client.md §3.2 exception, approved 2026-10-20): one coordinator thread + 2 priority + <see cref="Settings.P_Concurrency"/>
    /// workers. They only do HTTP and file installs: they never touch game state, <see cref="MirGraphics.MLibrary"/>,
    /// scenes or the device. Installed paths reach the UI thread through <see cref="TryDequeueInstalled"/> (a
    /// ConcurrentQueue drained by <see cref="PatchPickup.Process"/>). Everything else crosses under <see cref="_lock"/>.
    ///
    /// One patcher per folder: Patch\patch.lock (FileShare.None) for the life of the process. Another client in the same
    /// folder runs read-only: no downloads, Demand ignored, but it rescans Patch\State.bin and reopens what changed.
    /// </summary>
    internal static class PatchService
    {
        private static TimeSpan RefreshInterval => TimeSpan.FromMinutes(Settings.P_RefreshMinutes);
        private static readonly TimeSpan MinRefreshGap = TimeSpan.FromSeconds(30);
        private const long LockedRetryMs = 30_000, StaleRetryMs = 35_000;
        // Retry caps: a file in use 20 x 30 s, a busy relay 150 x 2 s, a stale/missing answer 3 times; then it is a fault.
        private const int MaxLater = 20, MaxBusy = 150, MaxStale = 3;

        /// <summary>True once <see cref="Start"/> succeeded (holder or read-only). Read by the UI thread before any call.</summary>
        public static volatile bool Active;
        /// <summary>A complete, current set of program files is staged: Restart (or exit) applies it.</summary>
        public static bool UpdateReady => _updateReady;
        /// <summary>The player chose Restart: Program.Main applies the staged set and starts the new build after the game closes.</summary>
        public static bool RestartRequested { get { lock (_lock) return _restartRequested; } }
        public static void RequestRestart() { lock (_lock) _restartRequested = true; }

        /// <summary>True when this process holds patch.lock and downloads.</summary>
        public static bool Holder { get; private set; }
        /// <summary>Bumped (under the lock) whenever a new manifest is applied; the UI thread grows library arrays on change.</summary>
        public static int ManifestVersion => Volatile.Read(ref _manifestVersion);

        private static readonly object _lock = new object();
        private static PatchManifest _manifest;
        private static PatchState _state;
        private static bool _firstUse, _stateDirty;
        private static int _manifestVersion;
        private static string _etag;
        private static FileStream _lockFile;
        private static HttpClient _http;
        private static Thread _coordinator;
        private static readonly List<Thread> _workers = new List<Thread>();
        private static volatile bool _stopping;
        private static bool _backgroundRunning, _paused, _everything;
        private static DateTime _lastRefresh = DateTime.MinValue, _nextRefresh = DateTime.MinValue;
        private static bool _refreshRequested;
        private static readonly Stopwatch _clock = Stopwatch.StartNew();

        // queues (under _lock): demand first, then background; _jobs holds everything queued or active
        private static readonly LinkedList<string> _demand = new LinkedList<string>();
        private static readonly List<string> _background = new List<string>();
        private static readonly HashSet<string> _queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _demanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<(long Due, string Path)> _delayed = new List<(long, string)>();
        private static readonly Dictionary<string, int> _failures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, int> _highest = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> _retries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Program updates (phase 4): program files download into Patch\Staged\ (Staged.bin), never over the running build;
        // PatchApply swaps the whole set at exit or on Restart. Builds that failed to start (Rejected.bin) are never staged again.
        private static PatchState _staged = new PatchState();
        private static bool _stagedDirty, _restartRequested;
        private static HashSet<string> _rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static volatile bool _updateReady;
        private static bool _replan;
        private static readonly ConcurrentQueue<string> _installed = new ConcurrentQueue<string>();
        private static long _queuedBytes, _doneBytes, _activeBytes, _inflight, _everythingLeft;
        private static int _faults;
        private static long _nextStateSave;
        private static string _current = "";

        // read-only instances
        private static DateTime _stateStamp, _manifestStamp;

        private static readonly string ManifestFile = Path.Combine(PatchPaths.PatchDir, "Manifest.bin");
        private static readonly string StateFile = Path.Combine(PatchPaths.PatchDir, "State.bin");

        public sealed class Snapshot
        {
            public int FilesLeft;
            public long BytesLeft, BytesDone, EverythingLeft;
            public string Current;
            public int Faults;
            public bool Paused, Offline, ReadOnly;
        }

        // — lifetime —

        /// <summary>
        /// Program.Run, before the game window: everything below on a thread of its own, so the game starts at once —
        /// left-over Old__ files, then <see cref="Start"/> (lock, State.bin, Manifest.bin, the live manifest), then
        /// <see cref="StartBackground"/>. Files the game draws before the manifest arrives are demanded once it has
        /// (PatchPickup grows the library arrays on the manifest change).
        /// </summary>
        public static void StartInBackground()
        {
            // The last run's list, synchronously (a small file): the Libraries arrays are sized from it when the game
            // window opens, so NPC / monster numbers have a slot from the first frame.
            lock (_lock)
            {
                _manifest = PatchManifest.TryLoad(ManifestFile);
                if (_manifest != null) _manifestVersion++;
            }
            var thread = new Thread(() =>
            {
                try
                {
                    RepairOldFiles();
                    Start();
                    StartBackground();
                }
                catch (Exception ex)
                {
                    LogOnce("[Patch] start: " + ex.Message);
                }
                finally
                {
                    _started.Set();
                }
            }) { IsBackground = true, Name = "PatchStart" };
            thread.Start();

            // A fresh install has no list yet: wait a few seconds for the first one so the arrays are sized from it (there is
            // no art to show before that anyway). Later starts never wait here.
            bool cached;
            lock (_lock) cached = _manifest != null;
            if (!cached) _started.Wait(TimeSpan.FromSeconds(8));
        }

        private static readonly ManualResetEventSlim _started = new ManualResetEventSlim(false);

        /// <summary>
        /// Left-over Old__ files: put back when the original is missing, else deleted; a locked file is left for the next
        /// start. Skipped while a program update is being proven: its Old__ files are the rollback copy (PatchApply).
        /// </summary>
        private static void RepairOldFiles()
        {
            if (PatchApply.Applying) return;
            try
            {
                foreach (var oldFilename in Directory.GetFiles(Settings.P_Client, "Old__*", SearchOption.AllDirectories))
                {
                    try
                    {
                        string original = Path.Combine(Path.GetDirectoryName(oldFilename), Path.GetFileName(oldFilename).Substring("Old__".Length));
                        if (!File.Exists(original)) File.Move(oldFilename, original);
                        else File.Delete(oldFilename);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogOnce("[Patch] Old__ clean-up: " + ex.Message);
            }
        }

        /// <summary>
        /// Start thread: take the lock (retrying a few seconds, a restarting client may still hold it), load
        /// State.bin + Manifest.bin and fetch the live manifest. Returns false when the relay was not reachable (the client
        /// then runs on what is on disk, sized from the last manifest).
        /// </summary>
        public static bool Start()
        {
            Directory.CreateDirectory(PatchPaths.TempDir);
            Directory.CreateDirectory(PatchPaths.TrashDir);
            for (int attempt = 0; attempt < 6 && _lockFile == null; attempt++)
            {
                try { _lockFile = new FileStream(Path.Combine(PatchPaths.PatchDir, "patch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { Thread.Sleep(500); }
            }
            Holder = _lockFile != null;

            var state = PatchState.TryLoad(StateFile);
            _firstUse = state == null;
            _state = state ?? new PatchState();
            lock (_lock) _manifest ??= PatchManifest.TryLoad(ManifestFile); // StartInBackground loaded it already
            _staged = PatchState.TryLoad(PatchApply.StagedFile) ?? new PatchState();
            _rejected = PatchApply.LoadRejected();
            if (File.Exists(StateFile)) _stateStamp = File.GetLastWriteTimeUtc(StateFile);
            if (File.Exists(ManifestFile)) _manifestStamp = File.GetLastWriteTimeUtc(ManifestFile);

            _http = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            })
            { Timeout = Timeout.InfiniteTimeSpan };

            bool online = false;
            Active = true; // before the first fetch, so the status line can say "Connecting..." while it runs
            if (Holder)
            {
                EmptyTrash();
                online = RefreshManifest(force: true);
                lock (_lock)
                {
                    _nextRefresh = DateTime.UtcNow + RefreshInterval;
                    PruneStaged();
                    RecomputeReady();
                }
            }
            return online;
        }

        /// <summary>After launch: the coordinator (refresh, plan, deletions, state saves) and, for the holder, the workers.</summary>
        public static void StartBackground()
        {
            if (!Active) return;
            lock (_lock)
            {
                if (_backgroundRunning) return;
                _backgroundRunning = true;
                _paused = !Settings.P_BackgroundDownloads;
            }
            if (Holder)
            {
                PlanBackground();
                StartWorkers();
            }
            _coordinator = new Thread(CoordinatorLoop) { IsBackground = true, Name = "PatchCoordinator" };
            _coordinator.Start();
        }

        /// <summary>At exit (or before a restart): stop, save State.bin, release the lock. In-flight parts resume next start.</summary>
        public static void Shutdown()
        {
            Stop();
            ReleaseLock();
        }

        /// <summary>Stops the threads and saves State.bin / Staged.bin, keeping patch.lock (Program.Main applies a staged update next).</summary>
        public static void Stop()
        {
            if (!Active || _stopping) return;
            _stopping = true;
            lock (_lock) Monitor.PulseAll(_lock);
            // Workers check _stopping per 64 KB chunk and before installing; wait so none records after the save.
            foreach (var worker in _workers) worker.Join(3000);
            SaveStateIfDirty(force: true);
        }

        public static void ReleaseLock()
        {
            _lockFile?.Dispose();
            _lockFile = null;
            Active = false;
        }

        /// <summary>
        /// After Stop, holder only: the staged set as PatchApply entries (puts with the recorded old size / hash, and program
        /// files the manifest no longer lists as removes), or false when no complete, current set is staged.
        /// </summary>
        public static bool TakeApplySet(out List<PatchApply.Entry> entries)
        {
            entries = null;
            lock (_lock)
            {
                if (!Holder || _manifest == null) return false;
                RecomputeReady();
                if (!_updateReady) return false;
                entries = new List<PatchApply.Entry>();
                foreach (var file in _manifest.Files.Values)
                {
                    if (!PatchPaths.IsProgramFile(file) || !IsStaged(file)) continue;
                    if (_state.Files.TryGetValue(file.Path, out var rec) && rec.Sha256 == file.Sha256) continue;
                    var e = new PatchApply.Entry { Put = true, Path = file.Path, NewSize = file.Size, NewSha = file.Sha256 };
                    if (rec.Sha256 != null) { e.OldSize = rec.Size; e.OldSha = rec.Sha256; }
                    entries.Add(e);
                }
                foreach (var removed in RemovedProgramFiles())
                {
                    var rec = _state.Files[removed];
                    entries.Add(new PatchApply.Entry { Put = false, Path = removed, OldSize = rec.Size, OldSha = rec.Sha256 });
                }
                return entries.Count > 0;
            }
        }

        // — API (thread-safe) —

        /// <summary>
        /// Puts a file at the head of the queue if the manifest lists it and it is not current. UI thread: called once per
        /// missing library/sound/map. Returns true when the file is (now) on its way.
        /// </summary>
        public static bool Demand(string path)
        {
            if (!Active || !Holder) return false;
            string rel = PatchPaths.Normalize(path);
            PatchFile file;
            (long Size, string Sha256)? rec = null;
            bool firstUse;
            lock (_lock)
            {
                if (_manifest == null || !_manifest.Files.TryGetValue(rel, out file)) return false;
                if (PatchPaths.IsProgramFile(file)) return false;
                if (_active.Contains(file.Path) || DelayedContains(file.Path)) { _demanded.Add(file.Path); return true; }
                if (_queued.Contains(file.Path))
                {
                    _demand.Remove(file.Path);
                    _background.Remove(file.Path);
                    _queued.Remove(file.Path);
                    _queuedBytes -= file.Size;
                    Enqueue(file.Path, demand: true);
                    return true;
                }
                if (_state.Files.TryGetValue(file.Path, out var recorded)) rec = recorded;
                firstUse = _firstUse;
            }
            var standing = Stand(file, rec, firstUse); // one stat, outside the lock
            lock (_lock)
            {
                if (standing == Standing.Trust) { _state.Files[file.Path] = (file.Size, file.Sha256); _stateDirty = true; }
                if (standing == Standing.Current || standing == Standing.Trust) return false;
                if (_queued.Contains(file.Path) || _active.Contains(file.Path)) return true;
                Enqueue(file.Path, demand: true);
                return true;
            }
        }

        /// <summary>A sound named without extension (SoundList): demands whichever of .wav/.mp3 the manifest has.</summary>
        public static bool DemandSound(string fileNameWithoutExtension)
        {
            if (!Active || !Holder) return false;
            string rel = PatchPaths.Normalize(Path.Combine(Settings.SoundPath, fileNameWithoutExtension));
            return Demand(rel + ".wav") || Demand(rel + ".mp3");
        }

        public static void DownloadEverything()
        {
            lock (_lock)
            {
                _everything = true;
                _replan = true; // the coordinator re-plans: never stat the folder on the UI thread
                Monitor.PulseAll(_lock);
            }
        }

        public static void SetPaused(bool paused)
        {
            lock (_lock)
            {
                _paused = paused;
                Monitor.PulseAll(_lock);
            }
        }

        public static Snapshot Progress
        {
            get
            {
                lock (_lock)
                {
                    return new Snapshot
                    {
                        FilesLeft = _queued.Count + _active.Count + _delayed.Count,
                        BytesLeft = Math.Max(0, _queuedBytes + _activeBytes - Interlocked.Read(ref _inflight)),
                        BytesDone = _doneBytes + Interlocked.Read(ref _inflight),
                        EverythingLeft = Math.Max(0, _everythingLeft - Interlocked.Read(ref _inflight)),
                        Current = _current,
                        Faults = _faults,
                        Paused = _paused,
                        Offline = _manifest == null,
                        ReadOnly = !Holder,
                    };
                }
            }
        }

        /// <summary>
        /// The highest NNN of "folder\NNN{suffix}.Lib" the manifest lists, or -1. Libraries.InitLibrary sizes each array
        /// from the larger of this and the disk, so a fresh install has a slot for every monster.
        /// </summary>
        public static int HighestIndex(string folder, string suffix)
        {
            if (_manifest == null) return -1; // also before Start finished: the cached list loaded by StartInBackground
            string dir = PatchPaths.Normalize(folder).TrimEnd('/') + "/";
            string key = dir + "|" + suffix;
            lock (_lock)
            {
                if (_manifest == null) return -1;
                if (_highest.TryGetValue(key, out int cached)) return cached;
                int best = ComputeHighest(_manifest, key);
                _highest[key] = best;
                return best;
            }
        }

        private static int ComputeHighest(PatchManifest manifest, string key)
        {
            int bar = key.IndexOf('|');
            string dir = key.Substring(0, bar), tail = key.Substring(bar + 1) + ".Lib";
            int best = -1;
            foreach (var path in manifest.Files.Keys)
            {
                if (!path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || path.IndexOf('/', dir.Length) >= 0) continue;
                if (!path.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) continue;
                int len = path.Length - dir.Length - tail.Length;
                if (len <= 0) continue;
                bool digits = true;
                for (int i = 0; i < len; i++) if (!char.IsDigit(path[dir.Length + i])) { digits = false; break; }
                if (digits && int.TryParse(path.AsSpan(dir.Length, len), out int n) && n > best) best = n;
            }
            return best;
        }

        /// <summary>
        /// Swaps in a new manifest with the highest indices the UI thread has asked for already worked out (off the lock),
        /// so GrowArrays never scans the manifest on the UI thread.
        /// </summary>
        private static void ApplyManifest(PatchManifest manifest)
        {
            List<string> keys;
            lock (_lock) keys = new List<string>(_highest.Keys);
            var highest = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys) highest[key] = ComputeHighest(manifest, key);
            lock (_lock)
            {
                _manifest = manifest;
                _highest = highest;
                _manifestVersion++;
                DropUnlisted();
            }
        }

        /// <summary>Queued, downloading or waiting to retry. PatchPickup clears MapControl.WaitingForFile once a map is not.</summary>
        public static bool IsPending(string path)
        {
            if (!Active || !Holder) return false;
            string rel = PatchPaths.Normalize(path);
            lock (_lock) return _queued.Contains(rel) || _active.Contains(rel) || DelayedContains(rel);
        }

        /// <summary>UI thread only: the next installed (or removed) relative path, for <see cref="PatchPickup"/>.</summary>
        public static bool TryDequeueInstalled(out string path) => _installed.TryDequeue(out path);

        // — coordinator —

        private static void CoordinatorLoop()
        {
            while (!_stopping)
            {
                try
                {
                    if (Holder)
                    {
                        bool refresh;
                        lock (_lock)
                        {
                            var now = DateTime.UtcNow;
                            refresh = now >= _nextRefresh || (_refreshRequested && now - _lastRefresh >= MinRefreshGap)
                                || (_manifest == null && now - _lastRefresh >= TimeSpan.FromSeconds(15)); // relay down or still indexing
                        }
                        if (refresh)
                        {
                            RefreshManifest(force: false);
                            lock (_lock)
                            {
                                _refreshRequested = false;
                                _nextRefresh = DateTime.UtcNow + RefreshInterval;
                            }
                        }
                        bool replan;
                        lock (_lock)
                        {
                            PumpDelayed();
                            replan = _replan;
                            _replan = false;
                        }
                        if (replan) PlanBackground();
                        SaveStateIfDirty(force: false);
                    }
                    else
                    {
                        WatchHolder();
                    }
                }
                catch (Exception ex)
                {
                    LogOnce("[Patch] coordinator: " + ex.Message);
                }
                lock (_lock) Monitor.Wait(_lock, Holder ? 2000 : 5000);
            }
        }

        /// <summary>Read-only instance: pick up the holder's installs from State.bin (and its manifest) on their change time.</summary>
        private static void WatchHolder()
        {
            if (File.Exists(ManifestFile) && File.GetLastWriteTimeUtc(ManifestFile) != _manifestStamp)
            {
                _manifestStamp = File.GetLastWriteTimeUtc(ManifestFile);
                var manifest = PatchManifest.TryLoad(ManifestFile);
                if (manifest != null) ApplyManifest(manifest);
            }
            if (!File.Exists(StateFile) || File.GetLastWriteTimeUtc(StateFile) == _stateStamp) return;
            _stateStamp = File.GetLastWriteTimeUtc(StateFile);
            var state = PatchState.TryLoad(StateFile);
            if (state == null) return;
            foreach (var pair in state.Files)
                if (!_state.Files.TryGetValue(pair.Key, out var old) || old.Sha256 != pair.Value.Sha256) _installed.Enqueue(pair.Key);
            foreach (var key in _state.Files.Keys)
                if (!state.Files.ContainsKey(key)) _installed.Enqueue(key);
            _state = state;
        }

        /// <summary>GET /api/patch with the last ETag; a new list is saved, applied and planned. False when unreachable.</summary>
        private static bool RefreshManifest(bool force)
        {
            lock (_lock) _lastRefresh = DateTime.UtcNow;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Settings.P_Host + "api/patch");
                if (!force && _etag != null) request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var response = _http.Send(request, HttpCompletionOption.ResponseContentRead, cts.Token);
                if (response.StatusCode == HttpStatusCode.NotModified) return true;
                if (!response.IsSuccessStatusCode)
                {
                    LogOnce($"[Patch] {Settings.P_Host}api/patch answered {(int)response.StatusCode}");
                    return false;
                }
                using var ms = new MemoryStream();
                response.Content.ReadAsStream(cts.Token).CopyTo(ms);
                var manifest = PatchManifest.Parse(ms.ToArray());
                string etag = response.Headers.ETag?.ToString();
                bool changed;
                lock (_lock) changed = _manifest == null || _manifest.Revision != manifest.Revision || !_manifest.Raw.AsSpan().SequenceEqual(manifest.Raw);
                _etag = etag;
                if (!changed) return true;
                manifest.Save(ManifestFile);
                ApplyManifest(manifest);
                lock (_lock)
                {
                    PruneStaged();
                    RecomputeReady();
                }
                bool plan;
                lock (_lock) plan = _backgroundRunning;
                if (plan) PlanBackground();
                RemoveDeleted(manifest);
                CleanTemp(manifest);
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
            {
                LogOnce("[Patch] manifest fetch failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// (Re)builds the background queue; called WITHOUT the lock (the folder is surveyed outside it). Order: files on disk
        /// that are out of date, then missing tiers 0-1, then tier 2 smallest first; tier 3 only after DownloadEverything.
        /// Demanded files keep their place (also after a 412 refresh).
        /// </summary>
        private static void PlanBackground()
        {
            var survey = Survey(f => true);
            bool everything;
            lock (_lock) everything = _everything;
            var program = new List<PatchFile>();
            var stale = new List<PatchFile>();
            var missing = new List<PatchFile>();
            long allLeft = 0;
            foreach (var (file, standing) in survey)
            {
                if (standing == Standing.Current) continue;
                if (PatchPaths.IsProgramFile(file))
                {
                    // Head of the background queue (after demand): staged, never installed over the running build.
                    bool skip;
                    lock (_lock) skip = IsStaged(file) || _rejected.Contains(file.Sha256);
                    if (!skip) program.Add(file);
                    continue;
                }
                allLeft += file.Size;
                if (standing == Standing.Missing) { if (file.Tier <= 2 || everything) missing.Add(file); }
                else stale.Add(file);
            }
            missing.Sort((a, b) => a.Tier != b.Tier && (a.Tier <= 1 || b.Tier <= 1) ? a.Tier.CompareTo(b.Tier) : a.Size.CompareTo(b.Size));
            lock (_lock)
            {
                if (_manifest == null) return;
                foreach (var path in _background) _queued.Remove(path);
                _background.Clear();
                foreach (var file in program) PlanOne(file);
                foreach (var file in stale) PlanOne(file);
                foreach (var file in missing) PlanOne(file);
                RecountQueued();
                _everythingLeft = allLeft;
            }
        }

        private static void PlanOne(PatchFile file)
        {
            if (!_manifest.Files.ContainsKey(file.Path) || _queued.Contains(file.Path) || _active.Contains(file.Path) || DelayedContains(file.Path)) return;
            Enqueue(file.Path, demand: _demanded.Contains(file.Path));
        }

        /// <summary>Under the lock: queued paths the new manifest no longer lists are dropped.</summary>
        private static void DropUnlisted()
        {
            for (var node = _demand.First; node != null;)
            {
                var next = node.Next;
                if (!_manifest.Files.ContainsKey(node.Value)) { _queued.Remove(node.Value); _demand.Remove(node); }
                node = next;
            }
            for (int i = _background.Count - 1; i >= 0; i--)
                if (!_manifest.Files.ContainsKey(_background[i])) { _queued.Remove(_background[i]); _background.RemoveAt(i); }
            RecountQueued();
        }

        /// <summary>Under the lock: _queuedBytes from both queues (bulk changes recount instead of adding and subtracting).</summary>
        private static void RecountQueued()
        {
            long bytes = 0;
            foreach (var path in _demand) if (_manifest.Files.TryGetValue(path, out var f)) bytes += f.Size;
            foreach (var path in _background) if (_manifest.Files.TryGetValue(path, out var f)) bytes += f.Size;
            _queuedBytes = bytes;
        }

        private enum Standing { Current, Trust, Verify, Stale, Missing }

        /// <summary>
        /// Where a file stands, from one stat (no shared state; call outside the lock). Current = recorded with the
        /// manifest's hash and the right size. Trust = first use and the size matches (§7.3). Verify = the right size but
        /// unrecorded or recorded with another hash: the worker hashes it before downloading.
        /// </summary>
        private static Standing Stand(PatchFile file, (long Size, string Sha256)? rec, bool firstUse)
        {
            var info = new FileInfo(PatchPaths.Local(file.Path));
            if (!info.Exists) return Standing.Missing;
            if (info.Length != file.Size) return Standing.Stale;
            if (rec != null) return rec.Value.Sha256 == file.Sha256 && rec.Value.Size == file.Size ? Standing.Current : Standing.Verify;
            return firstUse ? Standing.Trust : Standing.Verify;
        }

        /// <summary>
        /// Stands every manifest file the filter keeps: copies taken under the lock, the stats outside it, then Trust
        /// results recorded (and reported as Current).
        /// </summary>
        private static List<(PatchFile File, Standing Standing)> Survey(Func<PatchFile, bool> filter)
        {
            var files = new List<PatchFile>();
            var records = new Dictionary<string, (long Size, string Sha256)>(StringComparer.OrdinalIgnoreCase);
            bool firstUse;
            lock (_lock)
            {
                if (_manifest == null) return new List<(PatchFile, Standing)>();
                foreach (var file in _manifest.Files.Values) if (filter(file)) files.Add(file);
                foreach (var pair in _state.Files) records[pair.Key] = pair.Value;
                firstUse = _firstUse;
            }
            var result = new List<(PatchFile File, Standing Standing)>(files.Count);
            foreach (var file in files)
                result.Add((file, Stand(file, records.TryGetValue(file.Path, out var rec) ? rec : null, firstUse)));
            lock (_lock)
            {
                for (int i = 0; i < result.Count; i++)
                {
                    if (result[i].Standing != Standing.Trust) continue;
                    _state.Files[result[i].File.Path] = (result[i].File.Size, result[i].File.Sha256);
                    _stateDirty = true;
                    result[i] = (result[i].File, Standing.Current);
                }
            }
            return result;
        }

        /// <summary>
        /// Files this patcher installed or verified that the new manifest no longer lists are moved to Trash (an open .Lib
        /// can move thanks to FileShare.Delete) and queued for the UI thread. Never: unrecorded files, the deny list,
        /// program files (phase 4). Safety valve: a manifest that would remove over 25 % of recorded files is skipped.
        /// </summary>
        private static void RemoveDeleted(PatchManifest manifest)
        {
            var removed = new List<string>();
            lock (_lock)
            {
                foreach (var path in _state.Files.Keys)
                {
                    if (manifest.Files.ContainsKey(path) || PatchPaths.IsDenied(path) || !PatchPaths.IsSafe(path)) continue;
                    string ext = Path.GetExtension(path);
                    bool exeFolder = !path.Contains('/') || path.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase);
                    if (exeFolder && (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) || ext.Equals(".json", StringComparison.OrdinalIgnoreCase))) continue;
                    removed.Add(path);
                }
                if (removed.Count == 0) return;
                if (removed.Count * 4 > _state.Files.Count)
                {
                    LogOnce($"[Patch] revision '{manifest.Revision}' would remove {removed.Count} of {_state.Files.Count} files: deletion skipped");
                    return;
                }
            }
            foreach (var path in removed)
            {
                string local = PatchPaths.Local(path);
                try
                {
                    if (File.Exists(local)) File.Move(local, TrashName(local));
                    lock (_lock) { _state.Files.Remove(path); _stateDirty = true; }
                    _installed.Enqueue(path);
                }
                catch (IOException) { } // in use: tried again at the next manifest change or start
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void CleanTemp(PatchManifest manifest)
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files.Values) wanted.Add(Path.GetFileName(PartPath(file)));
            foreach (var part in Directory.EnumerateFiles(PatchPaths.TempDir, "*.part"))
            {
                if (wanted.Contains(Path.GetFileName(part))) continue;
                try { File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static void EmptyTrash()
        {
            foreach (var file in Directory.EnumerateFiles(PatchPaths.TrashDir))
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        // — queue helpers (under _lock) —

        private static void Enqueue(string path, bool demand)
        {
            if (!_queued.Add(path)) return;
            if (demand) { _demand.AddFirst(path); _demanded.Add(path); }
            else _background.Add(path);
            _queuedBytes += _manifest.Files[path].Size;
            Monitor.PulseAll(_lock);
        }

        /// <summary>Under the lock: retries whose time has come go back on the queue (demanded ones keep their priority).</summary>
        private static void PumpDelayed()
        {
            long now = _clock.ElapsedMilliseconds;
            for (int i = _delayed.Count - 1; i >= 0; i--)
            {
                if (_delayed[i].Due > now) continue;
                string path = _delayed[i].Path;
                _delayed.RemoveAt(i);
                if (_manifest == null || !_manifest.Files.ContainsKey(path)) continue;
                if (!_queued.Contains(path) && !_active.Contains(path)) Enqueue(path, demand: _demanded.Contains(path));
            }
        }

        private static bool DelayedContains(string path)
        {
            for (int i = 0; i < _delayed.Count; i++) if (string.Equals(_delayed[i].Path, path, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Copies the state under the lock and writes it outside, so a UI-thread Demand never waits on the disk.</summary>
        private static void SaveStateIfDirty(bool force)
        {
            if (!Holder) return;
            var copy = new PatchState();
            PatchState staged = null;
            lock (_lock)
            {
                if (!_stateDirty && !_stagedDirty) return;
                long now = _clock.ElapsedMilliseconds;
                if (!force && now < _nextStateSave) return;
                _nextStateSave = now + 2000;
                if (_stagedDirty)
                {
                    staged = new PatchState();
                    foreach (var pair in _staged.Files) staged.Files[pair.Key] = pair.Value;
                    _stagedDirty = false;
                }
                _stateDirty = false;
                foreach (var pair in _state.Files) copy.Files[pair.Key] = pair.Value;
            }
            try
            {
                copy.Save(StateFile);
                staged?.Save(PatchApply.StagedFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (_lock) { _stateDirty = true; if (staged != null) _stagedDirty = true; }
                LogOnce("[Patch] State.bin not saved: " + ex.Message);
            }
        }

        // — workers —

        private const int PriorityWorkers = 2;

        private static void StartWorkers()
        {
            lock (_lock)
            {
                if (_workers.Count > 0) return;
                // PriorityWorkers workers take requested files only (what the game is drawing / about to draw), so a queue of
                // large background files can never hold them; [Launcher] ConcurrentDownloads more do the background queue.
                int count = PriorityWorkers + Math.Clamp(Settings.P_Concurrency, 1, 16);
                for (int i = 0; i < count; i++)
                {
                    bool demandOnly = i < PriorityWorkers;
                    var thread = new Thread(() => WorkerLoop(demandOnly)) { IsBackground = true, Name = demandOnly ? "PatchWorkerDemand" : "PatchWorker" + i };
                    _workers.Add(thread);
                    thread.Start();
                }
            }
        }

        private static void WorkerLoop(bool demandOnly)
        {
            while (!_stopping)
            {
                PatchFile file = null;
                bool program = false;
                lock (_lock)
                {
                    while (!_stopping)
                    {
                        string path = null;
                        if (_demand.First != null) { path = _demand.First.Value; _demand.RemoveFirst(); }
                        else if (!demandOnly && _background.Count > 0 && !_paused) { path = _background[0]; _background.RemoveAt(0); }
                        if (path != null)
                        {
                            _queued.Remove(path);
                            if (_manifest == null || !_manifest.Files.TryGetValue(path, out file)) { file = null; continue; }
                            _queuedBytes -= file.Size;
                            program = PatchPaths.IsProgramFile(file);
                            _active.Add(path);
                            _activeBytes += file.Size;
                            _current = path;
                            break;
                        }
                        Monitor.Wait(_lock, 1000);
                    }
                }
                if (file == null) continue;

                string outcome = "fault";
                try { outcome = Fetch(file, program); }
                catch (Exception ex) { LogOnce("[Patch] " + file.Path + ": " + ex.Message); }

                lock (_lock)
                {
                    _active.Remove(file.Path);
                    _activeBytes -= file.Size;
                    if (_current == file.Path) _current = "";
                    switch (outcome)
                    {
                        case "ok":
                            _doneBytes += file.Size;
                            if (!program) _everythingLeft = Math.Max(0, _everythingLeft - file.Size);
                            _failures.Remove(file.Path);
                            _retries.Remove(file.Path);
                            _demanded.Remove(file.Path);
                            break;
                        case "later":
                            Retry(file.Path, LockedRetryMs, MaxLater);
                            break;
                        case "busy":
                            Retry(file.Path, 2000, MaxBusy);
                            break;
                        case "stale":
                            // A refresh is due; the file comes back after it (and stays demanded if it was).
                            _refreshRequested = true;
                            Monitor.PulseAll(_lock);
                            Retry(file.Path, StaleRetryMs, MaxStale);
                            break;
                        default:
                            _failures.TryGetValue(file.Path, out int n);
                            _failures[file.Path] = ++n;
                            if (n < 2) _delayed.Add((_clock.ElapsedMilliseconds + 5000, file.Path));
                            else Fault(file.Path);
                            break;
                    }
                }
            }
        }

        /// <summary>Under the lock: delay a retry, or count a fault once the cap is reached (nothing retries forever).</summary>
        private static void Retry(string path, long delayMs, int max)
        {
            _retries.TryGetValue(path, out int n);
            _retries[path] = ++n;
            if (n <= max) _delayed.Add((_clock.ElapsedMilliseconds + delayMs, path));
            else Fault(path);
        }

        private static void Fault(string path)
        {
            _faults++;
            _failures.Remove(path);
            _retries.Remove(path);
            _demanded.Remove(path);
        }

        /// <summary>One file: verify an unrecorded copy on disk, else download (resuming a part) and install. Returns the outcome.</summary>
        private static string Fetch(PatchFile file, bool program)
        {
            string local = PatchPaths.Local(file.Path);
            var info = new FileInfo(local);
            if (info.Exists && info.Length == file.Size)
            {
                bool current;
                lock (_lock) current = _state.Files.TryGetValue(file.Path, out var rec) && rec.Sha256 == file.Sha256;
                if (!current && HashFile(local) == file.Sha256)
                {
                    Record(file);
                    return "ok";
                }
            }

            string part = PartPath(file);
            string result = Download(file, part);
            if (result != "ok") return result;
            if (_stopping) return "later";
            // A program file this folder does not have yet is not loaded by anything: install it now, no restart needed.
            // Only one that exists (the running build's) is staged for PatchApply.
            return program && File.Exists(local) ? Stage(file, part) : Install(file, part, local, program);
        }

        private static string Download(PatchFile file, string part)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have > file.Size) { File.Delete(part); have = 0; }
            if (have > 0)
            {
                using var existing = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read);
                HashStream(existing, hash, have);
            }

            if (have < file.Size)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, PatchPaths.Url(Settings.P_Host, file.Path, file.Version));
                if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
                using var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromSeconds(60));
                using var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                switch (response.StatusCode)
                {
                    case HttpStatusCode.PreconditionFailed:
                    case HttpStatusCode.NotFound:
                        return "stale";
                    case HttpStatusCode.ServiceUnavailable:
                        return "busy";
                    case HttpStatusCode.RequestedRangeNotSatisfiable:
                        File.Delete(part);
                        return "fault";
                    case HttpStatusCode.OK:
                        if (have > 0) { have = 0; hash.GetHashAndReset(); }
                        break;
                    case HttpStatusCode.PartialContent:
                        break;
                    default:
                        LogOnce($"[Patch] {file.Path}: HTTP {(int)response.StatusCode}");
                        return "fault";
                }

                // A stalled transfer: the timer disposes the response, the read throws, the part resumes next time.
                using var watchdog = cts.Token.Register(() => response.Dispose());
                using var body = response.Content.ReadAsStream(cts.Token);
                using var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
                var buffer = new byte[64 * 1024];
                long total = have, streamed = 0;
                int n;
                try
                {
                    while ((n = body.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (_stopping) return "later";
                        if (total + n > file.Size) { output.Dispose(); File.Delete(part); return "fault"; }
                        output.Write(buffer, 0, n);
                        hash.AppendData(buffer, 0, n);
                        total += n;
                        streamed += n;
                        Interlocked.Add(ref _inflight, n);
                        cts.CancelAfter(TimeSpan.FromSeconds(60));
                    }
                }
                finally { Interlocked.Add(ref _inflight, -streamed); }
                have = total;
            }

            if (have != file.Size) return "fault";
            if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != file.Sha256)
            {
                File.Delete(part);
                LogOnce("[Patch] " + file.Path + " failed its SHA-256 check");
                return "fault";
            }
            return "ok";
        }

        /// <summary>
        /// Old file → Trash (works while the game has it open: libraries use FileShare.Delete), part → target, record,
        /// queue for the UI thread. A sharing violation (a playing sound) is "later", not a failure. Program files never come
        /// here: they are staged (Stage) and applied by PatchApply.
        /// </summary>
        private static string Install(PatchFile file, string part, string local, bool program)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(local));
            try
            {
                if (File.Exists(local)) File.Move(local, TrashName(local));
                File.Move(part, local);
            }
            catch (IOException) { return "later"; } // in use (a playing sound): retried, capped
            catch (UnauthorizedAccessException ex)
            {
                LogOnce("[Patch] cannot write " + local + ": " + ex.Message);
                return "fault";
            }
            Record(file);
            _installed.Enqueue(file.Path);
            return "ok";
        }

        /// <summary>A program file: part → Patch\Staged\&lt;path&gt; (replacing an older staged copy), recorded in Staged.bin.</summary>
        private static string Stage(PatchFile file, string part)
        {
            string target = StagedPath(file.Path);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Move(part, target, overwrite: true);
            }
            catch (IOException) { return "later"; }
            catch (UnauthorizedAccessException ex)
            {
                LogOnce("[Patch] cannot write " + target + ": " + ex.Message);
                return "fault";
            }
            lock (_lock)
            {
                _staged.Files[file.Path] = (file.Size, file.Sha256);
                _stagedDirty = true;
                RecomputeReady();
            }
            return "ok";
        }

        private static string StagedPath(string rel) => Path.Combine(PatchApply.StagedDir, rel.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>Under the lock: staged with the manifest's hash (and still on disk).</summary>
        private static bool IsStaged(PatchFile file) =>
            _staged.Files.TryGetValue(file.Path, out var rec) && rec.Sha256 == file.Sha256 && File.Exists(StagedPath(file.Path));

        /// <summary>Under the lock: program files this patcher recorded that the manifest no longer lists (removed at the next apply).</summary>
        private static List<string> RemovedProgramFiles()
        {
            var removed = new List<string>();
            foreach (var pair in _state.Files)
            {
                if (_manifest.Files.ContainsKey(pair.Key) || PatchPaths.IsDenied(pair.Key) || !PatchPaths.IsSafe(pair.Key)) continue;
                if (PatchPaths.IsProgramFile(new PatchFile { Path = pair.Key })) removed.Add(pair.Key);
            }
            return removed;
        }

        /// <summary>Under the lock: a newer build replaces the staged one, so staged files the manifest no longer wants are dropped.</summary>
        private static void PruneStaged()
        {
            if (_manifest == null) return;
            foreach (var path in new List<string>(_staged.Files.Keys))
            {
                if (_manifest.Files.TryGetValue(path, out var file) && file.Sha256 == _staged.Files[path].Sha256) continue;
                _staged.Files.Remove(path);
                _stagedDirty = true;
                try { File.Delete(StagedPath(path)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>
        /// Under the lock: ready when every program file the manifest lists is recorded current, staged with its hash, or not
        /// in this folder yet (those are installed directly); none is a rejected build; and at least one changed file the
        /// running build uses is staged (a new-only or removal-only change never asks for a restart).
        /// </summary>
        private static void RecomputeReady()
        {
            bool ready = false;
            if (Holder && _manifest != null)
            {
                int pending = 0;
                ready = true;
                foreach (var file in _manifest.Files.Values)
                {
                    if (!PatchPaths.IsProgramFile(file)) continue;
                    if (_state.Files.TryGetValue(file.Path, out var rec) && rec.Sha256 == file.Sha256) continue;
                    if (!File.Exists(PatchPaths.Local(file.Path))) continue; // new to this folder: installed directly (Fetch), no restart
                    if (_rejected.Contains(file.Sha256) || !IsStaged(file)) { ready = false; break; }
                    pending++;
                }
                // Only a changed program file the running build uses asks for a restart; removals ride along with it.
                if (pending == 0) ready = false;
            }
            _updateReady = ready;
        }

        private static void Record(PatchFile file)
        {
            lock (_lock)
            {
                _state.Files[file.Path] = (file.Size, file.Sha256);
                _stateDirty = true;
            }
        }

        /// <summary>Patch\Temp\&lt;sha&gt;-&lt;path hash&gt;.part: two files with the same bytes (identical sounds) never share a part.</summary>
        private static string PartPath(PatchFile file)
        {
            uint h = 2166136261;
            foreach (char c in file.Path.ToLowerInvariant()) { h ^= c; h *= 16777619; }
            return Path.Combine(PatchPaths.TempDir, file.Sha256 + "-" + h.ToString("x8") + ".part");
        }

        private static string TrashName(string local) =>
            Path.Combine(PatchPaths.TrashDir, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(local));

        private static string HashFile(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024);
                return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        private static void HashStream(Stream stream, IncrementalHash hash, long length)
        {
            var buffer = new byte[1024 * 1024];
            long left = length;
            while (left > 0)
            {
                int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (n <= 0) break;
                hash.AppendData(buffer, 0, n);
                left -= n;
            }
        }

        // — logging: within the shared 100-entry budget, each distinct message once —

        private static readonly HashSet<string> _logged = new HashSet<string>();

        private static void LogOnce(string message)
        {
            lock (_logged)
            {
                if (_logged.Count >= 20 || !_logged.Add(message)) return;
            }
            if (Settings.LogErrors) CMain.SaveError(message);
        }
    }
}

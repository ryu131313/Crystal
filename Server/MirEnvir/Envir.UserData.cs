using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Server.MirDatabase;
using Server.MirEnvir.Storage;
using Server.MirObjects;

namespace Server.MirEnvir
{
    /// <summary>
    /// User data in UserData.db (UserData-and-Quests-Plan.md Part A, phases 1-2): load, the first-start import of Server.MirADB and the
    /// Guilds / Goods / Conquests folders, the one-transaction save, the shutdown save, offline saves while the world is stopped, NPC goods
    /// held in memory and the pending deletes. Everything here runs on the game thread unless a member says otherwise.
    /// </summary>
    public partial class Envir
    {
        /// <summary>The user-data store (cwd-relative, like Server.MirADB was).</summary>
        public static readonly string UserDataPath = UserDataStore.DatabasePath;

        /// <summary>
        /// The store of user data. Pinned to the working directory at first use; tests set one per folder (internal setter) and shut
        /// its writer down afterwards.
        /// </summary>
        internal static UserDataStore UserStore
        {
            get
            {
                var store = Volatile.Read(ref _userStore);
                if (store != null) return store;
                Interlocked.CompareExchange(ref _userStore, new UserDataStore(), null);
                return _userStore;
            }
            set => Volatile.Write(ref _userStore, value);
        }

        private static UserDataStore _userStore;

        /// <summary>Where the loaded user data came from.</summary>
        internal enum UserDataSource : byte
        {
            /// <summary>Nothing loaded yet.</summary>
            None = 0,
            /// <summary>UserData.db.</summary>
            Store = 1,
            /// <summary>Server.MirADB and the side folders, imported into UserData.db once the conquests are loaded.</summary>
            Legacy = 2,
            /// <summary>No data and no evidence of any: an empty UserData.db is created at the same point.</summary>
            New = 3,
        }

        internal UserDataSource LoadedUserDataSource => _userDataSource;
        private UserDataSource _userDataSource;

        /// <summary>
        /// Set when LoadAccounts finished, cleared when it starts (it clears AccountList and can throw half-way) and when the account
        /// list is known to be stale (a failed restore). No save of any kind runs while it is false: a stale head would write old Next*IDs
        /// and later reuse indexes over real rows (Part A, Save: Guard).
        /// </summary>
        public bool UserDataLoaded => _userDataLoaded;
        private volatile bool _userDataLoaded;

        /// <summary>
        /// The next save serialises every account, guild, conquest and NPC's goods. Set by every load (rows carry no version, so the
        /// first save after a load rewrites them all with the current Envir.Version) and by a failed job. Written by the writer thread.
        /// </summary>
        private volatile bool _forceNextSave;

        /// <summary>The last job committed (the writer sets it before it clears Saving).</summary>
        private volatile bool _lastUserDataJobSucceeded = true;

        // Deleted account / guild indexes not yet known to be deleted from the store. Accounts are deleted from web workers and the
        // WinForms UI as well as the game thread, so concurrent sets. A job copies them; only after it succeeded are they removed.
        private readonly ConcurrentDictionary<int, byte> _pendingAccountDeletes = new ConcurrentDictionary<int, byte>();
        private readonly ConcurrentDictionary<int, byte> _pendingGuildDeletes = new ConcurrentDictionary<int, byte>();
        private int[] _postedAccountDeletes, _postedGuildDeletes;

        // The one save job; the game thread fills it only while Saving is false, the writer reads it while Saving is true.
        private readonly UserDataSaveJob _userDataJob = new UserDataSaveJob();

        /// <summary>The legacy Server.MirADB's saved account and guild counts (the import's count checks).</summary>
        private int _legacyAccountCount, _legacyGuildCount;

        private long _nextAccountBackupTime;

        /// <summary>Upper bound of the shutdown save's wait (Part A, Shutdown: a hung writer cannot hang shutdown or Reboot).</summary>
        internal static int ShutdownSaveWaitMs = 60000;

        /// <summary>An account was removed from AccountList (any thread): its row is deleted by the next successful save.</summary>
        public void MarkAccountDeleted(int accountIndex) => _pendingAccountDeletes.TryAdd(accountIndex, 0);

        /// <summary>The account list in memory no longer matches the store (a restore that loaded nothing): no save until a load.</summary>
        public void MarkUserDataUnloaded()
        {
            _userDataLoaded = false;
            _userDataSource = UserDataSource.None;
        }

        /// <summary>
        /// World stopped, after a restore swapped the store: drops every pending delete and the force flag, so a stale delete can never
        /// remove a row the restore brought back. The load that follows sets the force flag again.
        /// </summary>
        public void ResetUserDataAfterRestore()
        {
            _pendingAccountDeletes.Clear();
            _pendingGuildDeletes.Clear();
            _postedAccountDeletes = _postedGuildDeletes = null;
            _forceNextSave = false;
            _lastUserDataJobSucceeded = true;
        }

        // ---------------------------------------------------------------------------------------------------------- serialisation

        /// <summary>world.head: counters and the hero list; with <paramref name="legacyCounts"/> also the guild count (Server.MirADB layout).</summary>
        private void WriteAccountsHead(BinaryWriter writer, bool legacyCounts)
        {
            writer.Write(Version);
            writer.Write(CustomVersion);
            writer.Write(NextAccountID);
            writer.Write(NextCharacterID);
            writer.Write(NextUserItemID);
            writer.Write(NextHeroID);

            if (legacyCounts) writer.Write(GuildList.Count);
            writer.Write(NextGuildID);
            writer.Write(HeroList.Count);
            for (var i = 0; i < HeroList.Count; i++)
                HeroList[i].Save(writer);
        }

        /// <summary>world.tail: auctions, mail id, GameShop log, saved spawns, market sales (everything after the account list).</summary>
        private void WriteAccountsTail(BinaryWriter writer)
        {
            writer.Write(NextAuctionID);
            writer.Write(Auctions.Count);
            foreach (var auction in Auctions)
                auction.Save(writer);

            writer.Write(NextMailID);

            writer.Write(GameshopLog.Count);
            foreach (var item in GameshopLog)
            {
                writer.Write(item.Key);
                writer.Write(item.Value);
            }

            writer.Write(SavedSpawns.Count);
            foreach (var spawn in SavedSpawns)
            {
                var save = new RespawnSave { RespawnIndex = spawn.Info.RespawnIndex, NextSpawnTick = spawn.NextSpawnTick, Spawned = spawn.Count >= spawn.Info.Count * SpawnMultiplier };
                save.Save(writer);
            }

            writer.Write(MarketSales.Count);
            foreach (var sales in MarketSales)
            {
                writer.Write(sales.Key);
                sales.Value.Save(writer);
            }
        }

        /// <summary>The whole Server.MirADB image (head with counts, every account, tail): the rollback format, also used by tests.</summary>
        internal void SaveAccounts(BinaryWriter writer)
        {
            WriteAccountsHead(writer, legacyCounts: true);
            writer.Write(AccountList.Count);
            for (var i = 0; i < AccountList.Count; i++)
                AccountList[i].Save(writer);
            WriteAccountsTail(writer);
        }

        private void WriteGoods(BinaryWriter writer, NPCObject npc)
        {
            writer.Write(9999); // the goods format's header sentinel (NPCScript.LoadGoods)
            writer.Write(Version);
            writer.Write(CustomVersion);
            writer.Write(npc.UsedGoods.Count);
            for (var k = 0; k < npc.UsedGoods.Count; k++)
                npc.UsedGoods[k].Save(writer);
        }

        /// <summary>
        /// Fills <see cref="_userDataJob"/> (game thread, or a stopped world's caller under AccountLock; Saving must be true). Every account
        /// Save call of this pass gets its row written; accounts emitted from the offline cache are skipped. With <paramref name="sides"/>
        /// also the guilds, conquests and NPC goods that changed (all of them when <paramref name="full"/>); their flags are cleared here.
        /// </summary>
        private UserDataSaveJob PrepareUserDataJob(bool full, bool cache, bool sides, string backupPath)
        {
            var job = _userDataJob;
            job.Clear();

            var writer = _accountSaveWriter ??= new BufferBinaryWriter(AccountSaveEncoding, InitialAccountSaveCapacity());
            writer.Reset();

            WriteAccountsHead(writer, legacyCounts: false);
            job.HeadOffset = 0;
            job.HeadLength = writer.Length;

            if (cache)
            {
                _accountCacheUsed = true;
                SaveAccountRowsCached(writer, job.Accounts);
            }
            else
            {
                for (var i = 0; i < AccountList.Count; i++)
                {
                    var account = AccountList[i];
                    var start = writer.Length;
                    account.Save(writer);
                    job.Accounts.Add(new UserRowRef(account.Index, account.AccountID, start, writer.Length - start));
                }
            }

            job.TailOffset = writer.Length;
            WriteAccountsTail(writer);
            job.TailLength = writer.Length - job.TailOffset;

            if (sides)
            {
                for (var i = 0; i < GuildList.Count; i++)
                {
                    var guild = GuildList[i];
                    if (!full && !guild.NeedSave) continue;
                    guild.NeedSave = false;
                    var start = writer.Length;
                    guild.Save(writer);
                    job.Guilds.Add(new UserRowRef(guild.GuildIndex, null, start, writer.Length - start));
                }

                for (var i = 0; i < ConquestList.Count; i++)
                {
                    var conquest = ConquestList[i];
                    if (!full && !conquest.NeedSave) continue;
                    conquest.NeedSave = false;
                    var start = writer.Length;
                    conquest.Save(writer);
                    job.Conquests.Add(new UserRowRef(conquest.Info.Index, null, start, writer.Length - start));
                }

                for (var i = 0; i < MapList.Count; i++)
                {
                    var npcs = MapList[i].NPCs;
                    for (var j = 0; j < npcs.Count; j++)
                    {
                        var npc = npcs[j];
                        // NeedSave was never cleared before UserData.db, so every NPC that ever sold something was rewritten each save.
                        if (!full && !npc.NeedSave) continue;
                        npc.NeedSave = false;
                        var start = writer.Length;
                        WriteGoods(writer, npc);
                        var length = writer.Length - start;
                        job.Goods.Add(new UserRowRef(npc.Info.Index, null, start, length));
                        _goodsBytes[npc.Info.Index] = writer.Buffer.AsSpan(start, length).ToArray(); // LoadGoods reads this from now on
                    }
                }
            }

            job.Buffer = writer.Buffer; // last: the buffer may have grown while writing (the offsets stay valid)
            job.EnvirVersion = Version;
            job.BackupPath = backupPath;
            Volatile.Write(ref _lastAccountSaveBytes, writer.Length);
            return job;
        }

        private static int[] SnapshotKeys(ConcurrentDictionary<int, byte> set)
        {
            if (set.IsEmpty) return null;
            var keys = new List<int>(set.Keys);
            return keys.Count == 0 ? null : keys.ToArray();
        }

        /// <summary>The previous job's deletes are done when it succeeded; otherwise they stay pending and the next job retries them.</summary>
        private void ResolvePostedDeletes()
        {
            if (_postedAccountDeletes == null && _postedGuildDeletes == null) return;
            if (_lastUserDataJobSucceeded)
            {
                if (_postedAccountDeletes != null)
                    for (var i = 0; i < _postedAccountDeletes.Length; i++) _pendingAccountDeletes.TryRemove(_postedAccountDeletes[i], out _);
                if (_postedGuildDeletes != null)
                    for (var i = 0; i < _postedGuildDeletes.Length; i++) _pendingGuildDeletes.TryRemove(_postedGuildDeletes[i], out _);
            }
            _postedAccountDeletes = _postedGuildDeletes = null;
        }

        /// <summary>Runs on the writer thread (or the posting thread when the post was refused), before Saving is cleared.</summary>
        private void OnUserDataJobCompleted(bool ok)
        {
            if (!ok)
            {
                AccountSaveCache.InvalidateAll();
                _forceNextSave = true;
                Interlocked.Increment(ref _accountSaveFailures);
            }

            _lastUserDataJobSucceeded = ok;
            Saving = false;
        }

        private void FailUserDataSerialisation(Exception ex)
        {
            // Some NeedSave flags may already be cleared: the next save writes everything again.
            _forceNextSave = true;
            _lastSaveUsedCache = false;
            AccountSaveCache.InvalidateAll();
            Interlocked.Increment(ref _accountSaveFailures);
            Saving = false;
            MessageQueue.Enqueue(ex);
        }

        internal enum UserDataSaveOutcome : byte { Posted, Skipped, Failed }

        /// <summary>
        /// The periodic / "Save now" save (game thread): one job = one transaction with the changed rows, world, meta and the deletes
        /// (Part A, Save). Skipped (nothing touched, every flag kept) while the previous job runs or the data is not loaded.
        /// </summary>
        internal UserDataSaveOutcome SaveUserData(bool full, bool allowBackup)
        {
            if (!_userDataLoaded || _userDataSource != UserDataSource.Store || Saving) return UserDataSaveOutcome.Skipped;

            ResolvePostedDeletes();
            if (_forceNextSave)
            {
                full = true;
                _forceNextSave = false;
            }

            // P9-12: the offline account cache works only for Main. A full save reuses no blob: it invalidates them all, so every account
            // is serialised fresh (and written as a row) while the cache is warmed for the next save.
            var cache = Settings.OfflineAccountCache != OfflineAccountCacheMode.Off && this == Main;
            if (cache && full) AccountSaveCache.InvalidateAll();
            _lastSaveUsedCache = cache;
            if (!cache && _accountCacheUsed) DropAccountCache();

            string backupPath = null;
            if (allowBackup && Settings.AccountBackupMinutes > 0 && Time >= _nextAccountBackupTime)
            {
                _nextAccountBackupTime = Time + Settings.AccountBackupMinutes * Settings.Minute;
                backupPath = Path.GetFullPath(Path.Combine(AccountsBackUpPath,
                    $"Accounts {Now.Year:0000}-{Now.Month:00}-{Now.Day:00} {Now.Hour:00}-{Now.Minute:00}-{Now.Second:00}.bak"));
            }

            Saving = true;
            UserDataSaveJob job;
            try
            {
                job = PrepareUserDataJob(full, cache, sides: true, backupPath);
                job.AccountDeletes = SnapshotKeys(_pendingAccountDeletes);
                job.GuildDeletes = SnapshotKeys(_pendingGuildDeletes);
                if (this == Main) global::Server.WebAdmin.WebAdminHooks.RaiseAccountsSaved();
            }
            catch (Exception ex)
            {
                FailUserDataSerialisation(ex);
                return UserDataSaveOutcome.Failed;
            }

            _postedAccountDeletes = job.AccountDeletes;
            _postedGuildDeletes = job.GuildDeletes;
            if (!UserStore.TryPostSave(job, OnUserDataJobCompleted))
            {
                MessageQueue.Enqueue("[UserData] the save was refused: the UserData writer has shut down.");
                OnUserDataJobCompleted(false);
                return UserDataSaveOutcome.Failed;
            }

            return UserDataSaveOutcome.Posted;
        }

        /// <summary>
        /// World teardown on the game thread, after StopNetwork (no packet can change state after it): moves buyback items into the goods
        /// (<c>ProcessGoods(true)</c>), then runs the final full save and waits for it, bounded by <see cref="ShutdownSaveWaitMs"/>
        /// (the documented exception to "the game thread only posts"). Runs the save only if this run's load completed.
        /// </summary>
        private void SaveUserDataAtShutdown()
        {
            if (!_userDataLoaded || _userDataSource != UserDataSource.Store)
            {
                MessageQueue.Enqueue("[UserData] No shutdown save: the user data of this run was not loaded.");
                return;
            }

            for (var i = 0; i < MapList.Count; i++)
            {
                var npcs = MapList[i].NPCs;
                for (var j = 0; j < npcs.Count; j++)
                {
                    try { npcs[j].ProcessGoods(true); }
                    catch (Exception ex) { WorldErrors.Record("ProcessGoods at shutdown", ex); }
                }
            }

            var started = Stopwatch.ElapsedMilliseconds;
            while (Saving && Stopwatch.ElapsedMilliseconds - started < ShutdownSaveWaitMs)
                Thread.Sleep(1);
            if (Saving)
            {
                MessageQueue.Enqueue($"[UserData] ERROR: the previous save did not finish within {ShutdownSaveWaitMs / 1000} s; the shutdown save was skipped (the data is as of the last good save).");
                return;
            }

            ResolvePostedDeletes();
            _forceNextSave = false;
            _lastSaveUsedCache = false;
            if (_accountCacheUsed) DropAccountCache();

            Saving = true;
            UserDataSaveJob job;
            try
            {
                job = PrepareUserDataJob(full: true, cache: false, sides: true, backupPath: null);
                job.AccountDeletes = SnapshotKeys(_pendingAccountDeletes);
                job.GuildDeletes = SnapshotKeys(_pendingGuildDeletes);
                if (this == Main) global::Server.WebAdmin.WebAdminHooks.RaiseAccountsSaved();
            }
            catch (Exception ex)
            {
                FailUserDataSerialisation(ex);
                MessageQueue.Enqueue("[UserData] ERROR: the shutdown save could not be serialised; the data is as of the last good save.");
                return;
            }

            _postedAccountDeletes = job.AccountDeletes;
            _postedGuildDeletes = job.GuildDeletes;
            var remaining = (int)Math.Max(1, ShutdownSaveWaitMs - (Stopwatch.ElapsedMilliseconds - started));
            bool finished, ok = false;
            try
            {
                finished = UserStore.TryRunSave(job, remaining, OnUserDataJobCompleted, out ok);
            }
            catch (Exception ex)
            {
                OnUserDataJobCompleted(false);
                MessageQueue.Enqueue("[UserData] ERROR: the shutdown save could not run: " + ex.Message);
                return;
            }

            var ms = Stopwatch.ElapsedMilliseconds - started;
            if (!finished)
                MessageQueue.Enqueue($"[UserData] ERROR: the shutdown save did not finish within {ShutdownSaveWaitMs / 1000} s; shutting down anyway (the data is as of the last good save).");
            else if (ok)
            {
                ResolvePostedDeletes();
                MessageQueue.Enqueue($"[UserData] Shutdown save written in {ms} ms ({job.Accounts.Count} accounts, {job.Guilds.Count} guilds, {job.Goods.Count} goods, {job.Conquests.Count} conquests).");
            }
            else
                MessageQueue.Enqueue($"[UserData] ERROR: the shutdown save failed after {ms} ms (see above); the data is as of the last good save.");

            AccountSaveCache.InvalidateAll();
        }

        /// <summary>
        /// Offline save while the world is stopped (web admin <c>PersistIfStopped</c>, GM and character edits, the WinForms account form):
        /// accounts + world only (after a restore GuildList / ConquestList are stale), plus the pending account deletes. Waits until the
        /// writer is idle, then runs the job and waits for it. Never from the game thread. False (logged) when it did not save.
        /// </summary>
        public bool SaveAccounts()
        {
            if (WorkLoopAlive || Running)
            {
                MessageQueue.Enqueue("[UserData] SaveAccounts is for a stopped world; the running world saves accounts itself.");
                return false;
            }

            lock (AccountLock)
            {
                if (!_userDataLoaded)
                {
                    MessageQueue.Enqueue("[UserData] Accounts not saved: they are not loaded (start the world once, or restore a backup).");
                    return false;
                }

                if (_userDataSource != UserDataSource.Store || !UserStore.Exists)
                {
                    MessageQueue.Enqueue("[UserData] Accounts not saved: " + UserDataPath + " does not exist yet (start the world once to import Server.MirADB).");
                    return false;
                }

                try
                {
                    UserStore.WaitIdle(); // no buffer reuse while a job reads it
                }
                catch (Exception ex)
                {
                    MessageQueue.Enqueue(ex);
                    return false;
                }

                if (Saving)
                {
                    MessageQueue.Enqueue("[UserData] Accounts not saved: a previous save is still flagged as running.");
                    return false;
                }

                ResolvePostedDeletes();
                if (_accountCacheUsed) DropAccountCache();

                Saving = true;
                UserDataSaveJob job;
                try
                {
                    job = PrepareUserDataJob(full: true, cache: false, sides: false, backupPath: null);
                    job.AccountDeletes = SnapshotKeys(_pendingAccountDeletes);
                    if (this == Main) global::Server.WebAdmin.WebAdminHooks.RaiseAccountsSaved();
                }
                catch (Exception ex)
                {
                    FailUserDataSerialisation(ex);
                    return false;
                }

                var deletes = job.AccountDeletes;
                bool ok;
                try
                {
                    ok = UserStore.Writer.Run(w =>
                    {
                        bool result;
                        try
                        {
                            UserDataFile.WriteSave(w.Connection, job);
                            result = true;
                        }
                        catch (Exception ex)
                        {
                            MessageQueue.Enqueue("[UserData] offline save failed: " + ex.Message);
                            MessageQueue.Enqueue(ex);
                            result = false;
                        }
                        OnUserDataJobCompleted(result);
                        return result;
                    });
                }
                catch (Exception ex)
                {
                    OnUserDataJobCompleted(false);
                    MessageQueue.Enqueue(ex);
                    ok = false;
                }

                if (ok && deletes != null)
                    for (var i = 0; i < deletes.Length; i++) _pendingAccountDeletes.TryRemove(deletes[i], out _);

                // P9-12 (review H2): a full save never uses the cache, and afterwards no older blob may be written over it.
                AccountSaveCache.InvalidateAll();
                return ok;
            }
        }

        private static int InitialAccountSaveCapacity()
        {
            try
            {
                var info = new FileInfo(UserDataPath);
                if (!info.Exists) info = new FileInfo(AccountPath);
                if (info.Exists) return (int)Math.Min(info.Length + info.Length / 8 + 64 * 1024, Array.MaxLength);
            }
            catch (Exception) { } // only a size hint
            return 1024 * 1024;
        }

        // ---------------------------------------------------------------------------------------------------------- goods in memory

        // NPC index -> goods bytes (the 9999-header format). Filled at every world start before NPCs spawn; replaced whenever a save
        // serialises an NPC's goods. NPCScript.LoadGoods reads only this: the game thread never queries the store.
        private readonly Dictionary<int, byte[]> _goodsBytes = new Dictionary<int, byte[]>();
        // Import run only: *.msd files whose name is not an NPC index, or that name an index twice (the import's goods check).
        private readonly List<string> _goodsFileProblems = new List<string>();
        private int _goodsFileCount;

        /// <summary>The NPC's goods bytes (game thread; also while scripts load at world start).</summary>
        public bool TryGetGoods(int npcIndex, out byte[] data) => _goodsBytes.TryGetValue(npcIndex, out data);

        /// <summary>
        /// At every world start, before StartEnvir spawns NPCs (so a restore made while stopped is picked up): the goods rows, or, when
        /// UserData.db does not exist yet (the import run), every *.msd file, so NPCs never spawn with empty goods that a save would then
        /// write over the real ones.
        /// </summary>
        internal void LoadGoodsBytes()
        {
            _goodsBytes.Clear();
            _goodsFileProblems.Clear();
            _goodsFileCount = 0;

            if (UserStore.Exists)
            {
                var rows = UserStore.ReadTable("goods");
                for (var i = 0; i < rows.Count; i++) _goodsBytes[rows[i].Key] = rows[i].Data;
                return;
            }

            if (!Directory.Exists(Settings.GoodsPath)) return;
            foreach (var path in Directory.GetFiles(Settings.GoodsPath, "*.msd"))
            {
                _goodsFileCount++;
                var name = Path.GetFileNameWithoutExtension(path);
                if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    _goodsFileProblems.Add(Path.GetFileName(path) + " (not an NPC index)");
                    continue;
                }
                if (_goodsBytes.ContainsKey(index))
                {
                    _goodsFileProblems.Add(Path.GetFileName(path) + " (NPC index " + index.ToString(CultureInfo.InvariantCulture) + " twice)");
                    continue;
                }
                _goodsBytes[index] = File.ReadAllBytes(path);
            }
        }

        /// <summary>
        /// Before ReloadNPCs re-runs every script's LoadGoods: moves buyback items into the goods and puts every NPC's goods into the
        /// dictionary, so the reload keeps unsaved sales. The caller then requests a save.
        /// </summary>
        private void CaptureGoodsForReload()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, AccountSaveEncoding, leaveOpen: true);
            for (var i = 0; i < MapList.Count; i++)
            {
                var npcs = MapList[i].NPCs;
                for (var j = 0; j < npcs.Count; j++)
                {
                    var npc = npcs[j];
                    npc.ProcessGoods(true);
                    ms.SetLength(0);
                    WriteGoods(writer, npc);
                    writer.Flush();
                    _goodsBytes[npc.Info.Index] = ms.ToArray();
                    npc.NeedSave = true;
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------- load

        /// <summary>
        /// Loads the world head, the accounts and the world tail: from UserData.db when it exists; otherwise from Server.MirADB (imported
        /// into UserData.db by <see cref="CompleteUserDataImport"/> once the guilds and conquests are loaded); otherwise, with no evidence of
        /// earlier data, an empty world. Throws (the world does not start) on any doubt (Part A, First start).
        /// </summary>
        public void LoadAccounts()
        {
            _userDataLoaded = false;
            _userDataSource = UserDataSource.None;
            AccountSaveCache.InvalidateAll(); // P9-12: also when the load below throws half-way

            //reset ranking
            for (var i = 0; i < RankClass.Length; i++)
            {
                if (RankClass[i] != null) RankClass[i].Clear();
                else RankClass[i] = new List<RankCharacterInfo>();
            }

            RankTop.Clear();

            lock (LoadLock)
            {
                var store = UserStore;
                if (store.Exists)
                {
                    var rows = store.ReadWorld();
                    using (var head = new BinaryReader(new MemoryStream(rows.Head, writable: false)))
                        ReadAccountsHead(head, legacyCounts: false);

                    AccountList.Clear();
                    CharacterList.Clear();
                    for (var i = 0; i < rows.Accounts.Count; i++)
                    {
                        using var reader = new BinaryReader(new MemoryStream(rows.Accounts[i].Data, writable: false));
                        ReadAccount(reader);
                    }

                    using (var tail = new BinaryReader(new MemoryStream(rows.Tail, writable: false)))
                        FinishAccountsAndReadTail(tail);
                    _userDataSource = UserDataSource.Store;
                }
                else if (File.Exists(AccountPath))
                {
                    if (HasNewerSqliteAccountBackup(AccountPath, AccountsBackUpPath))
                        throw new InvalidOperationException("[Load] " + UserDataPath + " is missing and Back Up\\Accounts holds a UserData backup newer than Server.MirADB. Refusing to import the older Server.MirADB over it: restore the newest backup (Knowledgebase/Runbook.md).");

                    using (var stream = File.OpenRead(AccountPath))
                    using (var reader = new BinaryReader(stream))
                        ReadLegacyAccounts(reader);
                    _userDataSource = UserDataSource.Legacy;
                    MessageQueue.Enqueue($"[UserData] {UserDataPath} does not exist: loaded Server.MirADB (version {LoadVersion}, {AccountList.Count} accounts); it is imported once the world has loaded.");
                }
                else
                {
                    // Fail closed (roadmap P2): never create an empty user database over one that should be restored.
                    if (HasPriorDataEvidence(AccountPath, AccountsBackUpPath))
                        throw new InvalidOperationException("[Load] " + UserDataPath + " and Server.MirADB are missing but Back Up\\Accounts, a *.imported file or folder, or a temp file shows earlier data. Refusing to create an empty user database: restore the newest backup (Knowledgebase/Runbook.md).");

                    // A new world: the current (empty) state, read back through the same reader as a file would be.
                    using var ms = new MemoryStream();
                    using (var w = new BinaryWriter(ms, AccountSaveEncoding, leaveOpen: true)) SaveAccounts(w);
                    ms.Position = 0;
                    using (var reader = new BinaryReader(ms)) ReadLegacyAccounts(reader);
                    _userDataSource = UserDataSource.New;
                }

                BuildSentMailIndex(); // phase 2.6: Sent folder index from the loaded mailboxes
            }

            // P9-12: every list was replaced; the cache is rebuilt from a fresh save, never from the loaded bytes (the load drops archived
            // characters, unbindable items and orphan quests, so those bytes differ).
            AccountSaveCache.InvalidateAll();
            _forceNextSave = true; // rows carry no version: the first save after any load rewrites every row
            _userDataLoaded = true;
        }

        /// <summary>Server.MirADB's layout: head with the guild count, the account count, the accounts, the tail.</summary>
        private void ReadLegacyAccounts(BinaryReader reader)
        {
            ReadAccountsHead(reader, legacyCounts: true);
            var count = reader.ReadInt32();
            _legacyAccountCount = count;

            AccountList.Clear();
            CharacterList.Clear();
            for (var i = 0; i < count; i++) ReadAccount(reader);

            FinishAccountsAndReadTail(reader);
        }

        private void ReadAccountsHead(BinaryReader reader, bool legacyCounts)
        {
            LoadVersion = reader.ReadInt32();
            LoadCustomVersion = reader.ReadInt32();

            if (LoadVersion < MinVersion || LoadVersion > Version)
                throw new InvalidOperationException($"[Load] the user data version {LoadVersion} is outside the supported range {MinVersion}-{Version}. Refusing to load it (a newer file would be misread and then overwritten).");
            NextAccountID = reader.ReadInt32();
            NextCharacterID = reader.ReadInt32();
            NextUserItemID = reader.ReadUInt64();
            if (LoadVersion > 98)
                NextHeroID = reader.ReadInt32();

            if (legacyCounts)
            {
                GuildCount = reader.ReadInt32();
                _legacyGuildCount = GuildCount;
            }
            NextGuildID = reader.ReadInt32();

            if (LoadVersion > 102)
            {
                var count = reader.ReadInt32();
                HeroList.Clear();
                for (var i = 0; i < count; i++)
                    HeroList.Add(new HeroInfo(reader, LoadVersion, LoadCustomVersion));
            }
        }

        private void ReadAccount(BinaryReader reader)
        {
            var account = new AccountInfo(reader);
            // Accounts with no characters used to be dropped silently here (all but the first), and the next save made it permanent.
            // They are kept now (roadmap P5, decision #14); pruning, if wanted, should be an explicit and logged maintenance job.
            AccountList.Add(account);
            CharacterList.AddRange(account.Characters);
            if (LoadVersion > 98 && LoadVersion < 103)
                account.Characters.ForEach(character => HeroList.AddRange(character.Heroes));
        }

        private void FinishAccountsAndReadTail(BinaryReader reader)
        {
            PruneUnresolvedFriends();

            foreach (var auction in Auctions)
            {
                auction.SellerInfo.AccountInfo.Auctions.Remove(auction);
            }

            Auctions.Clear();

            NextAuctionID = reader.ReadUInt64();

            var count = reader.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                var auction = new AuctionInfo(reader, LoadVersion, LoadCustomVersion);

                if (!BindItem(auction.Item) || !BindCharacter(auction)) continue;

                Auctions.AddLast(auction);
                auction.SellerInfo.AccountInfo.Auctions.AddLast(auction);
            }

            NextMailID = reader.ReadUInt64();

            if (LoadVersion <= 80)
            {
                count = reader.ReadInt32();
                for (var i = 0; i < count; i++)
                {
                    var mail = new MailInfo(reader, LoadVersion, LoadCustomVersion);

                    mail.RecipientInfo = GetCharacterInfo(mail.RecipientIndex);

                    if (mail.RecipientInfo != null)
                    {
                        mail.RecipientInfo.Mail.Add(mail); //add to players inbox
                    }
                }
            }

            if (LoadVersion >= 63)
            {
                var logCount = reader.ReadInt32();
                for (var i = 0; i < logCount; i++)
                {
                    GameshopLog.Add(reader.ReadInt32(), reader.ReadInt32());
                }

                if (ResetGS) ClearGameshopLog();
            }

            if (LoadVersion >= 68)
            {
                var saveCount = reader.ReadInt32();
                for (var i = 0; i < saveCount; i++)
                {
                    var saved = new RespawnSave(reader);
                    foreach (var respawn in SavedSpawns)
                    {
                        if (respawn.Info.RespawnIndex != saved.RespawnIndex) continue;

                        respawn.NextSpawnTick = saved.NextSpawnTick;

                        if (!saved.Spawned || respawn.Info.Count * SpawnMultiplier <= respawn.Count)
                        {
                            continue;
                        }

                        var mobcount = respawn.Info.Count * SpawnMultiplier - respawn.Count;
                        for (var j = 0; j < mobcount; j++)
                        {
                            respawn.Spawn();
                        }
                    }
                }
            }

            MarketSales.Clear();
            if (LoadVersion >= 119)
            {
                var salesCount = reader.ReadInt32();
                for (var i = 0; i < salesCount; i++)
                {
                    var itemIndex = reader.ReadInt32();
                    MarketSales[itemIndex] = new MarketSaleHistory(reader);
                }
            }
        }

        public void LoadGuilds()
        {
            lock (LoadLock)
            {
                GuildList.Clear();
                // In-process restarts used to keep the previous run's GuildObjects (StopEnvir never cleared them, and the GuildObject ctor
                // skips an index that is already listed), so a save wrote stale guild data over the freshly loaded rows (roadmap P5).
                Guilds.Clear();

                if (_userDataSource == UserDataSource.Store)
                {
                    var rows = UserStore.ReadTable("guilds");
                    for (var i = 0; i < rows.Count; i++)
                    {
                        GuildInfo guildInfo;
                        using (var reader = new BinaryReader(new MemoryStream(rows[i].Data, writable: false)))
                            guildInfo = new GuildInfo(reader);
                        GuildList.Add(guildInfo);
                        new GuildObject(guildInfo);
                    }
                    GuildCount = GuildList.Count;
                    return;
                }

                if (_userDataSource != UserDataSource.Legacy) return;

                // The import run: the old loader. A re-index interrupted after its marker is finished before anything is read.
                try { CompleteGuildReindex(Settings.GuildPath); }
                catch (Exception ex) { MessageQueue.Enqueue(ex); }

                var count = 0;
                for (var i = 0; i < GuildCount; i++)
                {
                    var path = Path.Combine(Settings.GuildPath, i + ".mgd");
                    if (!File.Exists(path)) continue;

                    GuildInfo guildInfo;
                    using (var stream = File.OpenRead(path))
                    {
                        using var reader = new BinaryReader(stream);
                        guildInfo = new GuildInfo(reader);
                    }

                    GuildList.Add(guildInfo);
                    new GuildObject(guildInfo);
                    count++;
                }

                if (count != GuildCount) GuildCount = count;
            }
        }

        public void LoadConquests()
        {
            lock (LoadLock)
            {
                Conquests.Clear();
                ConquestList.Clear();

                // Store: rows by conquest index. Rows of conquests that are not loaded (their map is missing) stay in the store untouched.
                Dictionary<int, byte[]> rows = null;
                if (_userDataSource == UserDataSource.Store)
                {
                    var list = UserStore.ReadTable("conquests");
                    rows = new Dictionary<int, byte[]>(list.Count);
                    for (var i = 0; i < list.Count; i++) rows[list[i].Key] = list[i].Data;
                }

                for (var i = 0; i < ConquestInfoList.Count; i++)
                {
                    ConquestObject newConquest;
                    ConquestGuildInfo conquestGuildInfo;
                    var tempMap = GetMap(ConquestInfoList[i].MapIndex);

                    if (tempMap == null) continue;

                    byte[] data = null;
                    if (rows != null) rows.TryGetValue(ConquestInfoList[i].Index, out data);
                    else if (_userDataSource == UserDataSource.Legacy)
                    {
                        var path = Path.Combine(Settings.ConquestsPath, ConquestInfoList[i].Index + ".mcd");
                        if (File.Exists(path)) data = File.ReadAllBytes(path);
                    }

                    if (data != null)
                    {
                        using (var reader = new BinaryReader(new MemoryStream(data, writable: false)))
                            conquestGuildInfo = new ConquestGuildInfo(reader) { Info = ConquestInfoList[i] };

                        newConquest = new ConquestObject(conquestGuildInfo)
                        {
                            ConquestMap = tempMap
                        };

                        for (var k = 0; k < Guilds.Count; k++)
                        {
                            if (conquestGuildInfo.Owner != Guilds[k].Guildindex) continue;
                            newConquest.Guild = Guilds[k];
                            Guilds[k].Conquest = newConquest;
                        }
                    }
                    else
                    {
                        conquestGuildInfo = new ConquestGuildInfo { Info = ConquestInfoList[i], NeedSave = true };
                        newConquest = new ConquestObject(conquestGuildInfo)
                        {
                            ConquestMap = tempMap
                        };
                    }

                    ConquestList.Add(conquestGuildInfo);
                    Conquests.Add(newConquest);
                    tempMap.Conquest.Add(newConquest);

                    newConquest.Bind();
                }
            }
        }

        // ---------------------------------------------------------------------------------------------------------- first start

        /// <summary>
        /// StartNetwork, after LoadConquests (conquests need the maps): when the user data came from Server.MirADB, writes it into a new
        /// UserData.db (via .tmp, checked and read back), checks the counts against the sources, then renames Server.MirADB and the
        /// Guilds, Goods and Conquests folders to *.imported. A new world gets an empty store. A count mismatch stops the import with
        /// nothing renamed and each missing or duplicate index logged (Part A, First start). Throws to keep the world from starting.
        /// </summary>
        internal void CompleteUserDataImport()
        {
            if (_userDataSource != UserDataSource.Legacy && _userDataSource != UserDataSource.New) return;
            var legacy = _userDataSource == UserDataSource.Legacy;

            var rows = new UserDataRows();
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, AccountSaveEncoding, leaveOpen: true))
            {
                byte[] Take()
                {
                    w.Flush();
                    var bytes = ms.ToArray();
                    ms.SetLength(0);
                    return bytes;
                }

                WriteAccountsHead(w, legacyCounts: false);
                rows.Head = Take();
                for (var i = 0; i < AccountList.Count; i++)
                {
                    AccountList[i].Save(w);
                    rows.Accounts.Add(new UserDataRow(AccountList[i].Index, AccountList[i].AccountID, Take()));
                }
                WriteAccountsTail(w);
                rows.Tail = Take();
                for (var i = 0; i < GuildList.Count; i++)
                {
                    GuildList[i].Save(w);
                    rows.Guilds.Add(new UserDataRow(GuildList[i].GuildIndex, null, Take()));
                }
                for (var i = 0; i < ConquestList.Count; i++)
                {
                    ConquestList[i].Save(w);
                    rows.Conquests.Add(new UserDataRow(ConquestList[i].Info.Index, null, Take()));
                }
            }

            var problems = new List<string>();
            if (legacy)
            {
                CheckImportCounts(rows, problems);
                // Goods rows come from the files, never from NPC objects (including NPCs that are not spawned).
                foreach (var pair in _goodsBytes) rows.Goods.Add(new UserDataRow(pair.Key, null, pair.Value));
            }

            if (problems.Count > 0)
            {
                for (var i = 0; i < problems.Count; i++) MessageQueue.Enqueue("[UserData] Import check: " + problems[i]);
                throw new InvalidOperationException($"[UserData] The import of Server.MirADB stopped: {problems.Count} count check(s) failed (listed above). Nothing was renamed; fix the files or decide which file wins, then start again (Knowledgebase/Runbook.md).");
            }

            var meta = new Dictionary<string, object> { ["source"] = legacy ? "import" : "new" };
            if (legacy)
            {
                meta["source_file"] = Path.GetFullPath(AccountPath);
                meta["source_envir_version"] = (long)LoadVersion;
                meta["source_sha256"] = SqliteWorldStore.Sha256(AccountPath);
            }

            UserDataStore.Create(UserStore.FullPath, rows, Version, meta);
            _userDataSource = UserDataSource.Store;
            _forceNextSave = true;

            if (!legacy)
            {
                MessageQueue.Enqueue("[UserData] Created an empty " + UserDataPath + " (no earlier user data).");
                return;
            }

            MessageQueue.Enqueue($"[UserData] Imported Server.MirADB into {UserDataPath}: {rows.Accounts.Count} accounts, {rows.Guilds.Count} guilds, {rows.Goods.Count} goods, {rows.Conquests.Count} conquests.");
            RenameImported(AccountPath);
            RenameImported(Settings.GuildPath);
            RenameImported(Settings.GoodsPath);
            RenameImported(Settings.ConquestsPath);
        }

        /// <summary>Accounts against the head's saved count, guilds / conquests / goods against their files; duplicates by key.</summary>
        private void CheckImportCounts(UserDataRows rows, List<string> problems)
        {
            if (AccountList.Count != _legacyAccountCount)
                problems.Add($"accounts: {AccountList.Count} loaded, Server.MirADB says {_legacyAccountCount}");
            ReportDuplicates("account index", rows.Accounts, problems);

            var guildFiles = Directory.Exists(Settings.GuildPath) ? Directory.GetFiles(Settings.GuildPath, "*.mgd") : Array.Empty<string>();
            if (GuildList.Count != guildFiles.Length)
            {
                problems.Add($"guilds: {GuildList.Count} loaded, {guildFiles.Length} *.mgd files (Server.MirADB says {_legacyGuildCount})");
                for (var i = 0; i < guildFiles.Length; i++)
                {
                    var name = Path.GetFileNameWithoutExtension(guildFiles[i]);
                    if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var position) || position >= _legacyGuildCount)
                        problems.Add("guilds: file " + Path.GetFileName(guildFiles[i]) + " is not read by the loader (guild count " + _legacyGuildCount.ToString(CultureInfo.InvariantCulture) + ")");
                }
                for (var i = 0; i < _legacyGuildCount; i++)
                    if (!File.Exists(Path.Combine(Settings.GuildPath, i + ".mgd"))) problems.Add("guilds: file " + i.ToString(CultureInfo.InvariantCulture) + ".mgd is missing");
            }
            ReportDuplicates("guild index", rows.Guilds, problems);

            // Conquests: every file becomes exactly one row. A conquest whose map is missing (or that no longer exists in the design data)
            // is not loaded: its file bytes are kept as a row, unparsed, so the rollback export does not lose it.
            var conquestFiles = Directory.Exists(Settings.ConquestsPath) ? Directory.GetFiles(Settings.ConquestsPath, "*.mcd") : Array.Empty<string>();
            var loaded = new HashSet<int>();
            for (var i = 0; i < rows.Conquests.Count; i++) loaded.Add(rows.Conquests[i].Key);
            var fromFiles = 0;
            var seen = new HashSet<int>();
            for (var i = 0; i < conquestFiles.Length; i++)
            {
                var name = Path.GetFileNameWithoutExtension(conquestFiles[i]);
                if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index.ToString(CultureInfo.InvariantCulture) != name)
                {
                    problems.Add("conquests: file " + Path.GetFileName(conquestFiles[i]) + " is not named by a conquest index");
                    continue;
                }
                if (!seen.Add(index)) { problems.Add("conquests: index " + name + " twice"); continue; }
                fromFiles++;
                if (loaded.Contains(index)) continue;
                rows.Conquests.Add(new UserDataRow(index, null, File.ReadAllBytes(conquestFiles[i])));
                MessageQueue.Enqueue("[UserData] Import: conquest " + name + " is not loaded (its map or design entry is missing); its file is kept as a row, unparsed.");
            }
            if (fromFiles != conquestFiles.Length)
                problems.Add($"conquests: {fromFiles} of {conquestFiles.Length} *.mcd files imported");

            if (_goodsFileProblems.Count > 0)
                for (var i = 0; i < _goodsFileProblems.Count; i++) problems.Add("goods: " + _goodsFileProblems[i]);
            if (_goodsBytes.Count != _goodsFileCount)
                problems.Add($"goods: {_goodsBytes.Count} rows from {_goodsFileCount} *.msd files");
        }

        private static void ReportDuplicates(string what, List<UserDataRow> rows, List<string> problems)
        {
            var seen = new HashSet<int>();
            for (var i = 0; i < rows.Count; i++)
                if (!seen.Add(rows[i].Key)) problems.Add(what + " " + rows[i].Key.ToString(CultureInfo.InvariantCulture) + " twice" + (rows[i].Id != null ? " (" + rows[i].Id + ")" : string.Empty));
        }

        /// <summary>Renames an imported file or folder to "&lt;name&gt;.imported" (a timestamped name when that exists already).</summary>
        private static void RenameImported(string path)
        {
            try
            {
                var isFile = File.Exists(path);
                if (!isFile && !Directory.Exists(path)) return;
                var target = path + ".imported";
                if (File.Exists(target) || Directory.Exists(target))
                    target = path + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".imported";
                if (isFile) File.Move(path, target);
                else Directory.Move(path, target);
                MessageQueue.Enqueue("[UserData] Renamed " + path + " to " + target + ".");
            }
            catch (Exception ex)
            {
                MessageQueue.Enqueue("[UserData] ERROR: could not rename " + path + " after the import (" + ex.Message + "); UserData.db is the live data now, move it aside by hand.");
            }
        }

        /// <summary>
        /// True when a missing live file is more likely lost than new: temp files from a save, a SQLite journal without its database,
        /// backups (legacy binary or SQLite), or anything imported into UserData.db (*.imported). Used to refuse creating an empty user
        /// database over data that should be restored instead (roadmap P2; Part A, First start).
        /// </summary>
        internal static bool HasPriorDataEvidence(string livePath, string backupDir)
        {
            if (File.Exists(livePath + "n") || File.Exists(livePath + "o")) return true;
            if (Directory.Exists(backupDir) && Directory.EnumerateFiles(backupDir, "*.bak").Any()) return true;

            var dir = Path.GetDirectoryName(Path.GetFullPath(livePath));
            if (File.Exists(Path.Combine(dir, Path.GetFileName(UserDataPath) + "-journal"))) return true;
            if (Directory.EnumerateFileSystemEntries(dir, "*.imported").Any()) return true;
            var goodsParent = Path.GetDirectoryName(Path.GetFullPath(Settings.GoodsPath));
            return Directory.Exists(goodsParent) && Directory.EnumerateFileSystemEntries(goodsParent, "*.imported").Any();
        }

        /// <summary>
        /// Server.MirADB is present but UserData.db is not: import only if no SQLite backup in Back Up\Accounts is newer than Server.MirADB
        /// (a roll forward after a D1 rollback); otherwise a store was lost and the operator restores it (P7's rule, HasPriorSqliteEvidence).
        /// </summary>
        internal static bool HasNewerSqliteAccountBackup(string legacyPath, string backupDir)
        {
            if (!Directory.Exists(backupDir)) return false;
            var legacyUtc = File.GetLastWriteTimeUtc(legacyPath);
            foreach (var bak in Directory.EnumerateFiles(backupDir, "*.bak"))
                if (File.GetLastWriteTimeUtc(bak) >= legacyUtc && SqliteWorldFile.LooksLikeSqlite(bak)) return true;
            return false;
        }

        // ---------------------------------------------------------------------------------------------------------- legacy guild re-index

        // Legacy guild files were named by list position and re-indexed through Guilds\Reindex\ with a "done" marker (codebase review
        // 2026-10-24). The import run finishes such a re-index before it reads the files; nothing writes guild files any more.
        private const string GuildReindexDir = "Reindex", GuildReindexMarker = "done";

        /// <summary>
        /// Moves a staged re-index into place and prunes the surplus files (idempotent). Without a marker the staging was incomplete:
        /// it is discarded and the old files stay. Called by the import run's LoadGuilds before it reads anything.
        /// </summary>
        internal static void CompleteGuildReindex(string guildPath)
        {
            var stage = Path.Combine(guildPath, GuildReindexDir);
            if (!Directory.Exists(stage)) return;

            var markerPath = Path.Combine(stage, GuildReindexMarker);
            if (!File.Exists(markerPath) || !int.TryParse(File.ReadAllText(markerPath), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                Directory.Delete(stage, true);
                return;
            }

            for (var i = 0; i < count; i++)
            {
                var staged = Path.Combine(stage, i + ".mgd");
                if (!File.Exists(staged)) continue; // already moved before a crash
                var live = Path.Combine(guildPath, i + ".mgd");
                if (File.Exists(live)) File.Replace(staged, live, null);
                else File.Move(staged, live);
            }

            foreach (var file in Directory.GetFiles(guildPath, "*.mgd"))
            {
                if (int.TryParse(Path.GetFileNameWithoutExtension(file), out var index) && index >= count)
                    File.Delete(file);
            }

            File.Delete(markerPath);
            Directory.Delete(stage, true);
        }
    }
}

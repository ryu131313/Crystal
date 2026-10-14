using System.Drawing;
using System.Globalization;
using Server.MirDatabase;
using Server.WebAdmin.GameData;

namespace Server.WebAdmin.LiveDb
{
    /// <summary>Whether an NPC can stand on a cell of the running world.</summary>
    internal enum LiveDbPlacement
    {
        Valid = 0,

        /// <summary>The map is loaded but the cell is not walkable (or outside the map).</summary>
        Invalid = 1,

        /// <summary>The running world has no such map (a new or failed map): the NPC is saved but not placed until a restart.</summary>
        MapNotLoaded = 2,
    }

    /// <summary>The geometry of one safe zone (what its spell objects were created from).</summary>
    internal struct LiveDbZone
    {
        public Point Location;
        public int Size;
        public bool StartPoint;
    }

    /// <summary>The world-side effects of the live step (production: <see cref="LiveDbWorldHooks"/>; tests: a fake).</summary>
    internal interface ILiveDbHooks
    {
        /// <summary>Reloads one monster's drop file into a NEW list, swaps it in and returns the previous list (for rollback).</summary>
        List<DropInfo> ReloadDrops(MonsterInfo mainInfo);

        /// <summary>A monster clone was added to Main's MonsterInfoList (bump Main's index counter).</summary>
        void MonsterInserted(MonsterInfo mainClone);

        /// <summary>Queue a refresh of every spawned monster of these kinds (the budgeted sweeper).</summary>
        void QueueMonsterRefresh(ICollection<MonsterInfo> mainInfos);

        /// <summary>An item clone was added to Main's ItemInfoList (bump Main's index counter).</summary>
        void ItemInserted(ItemInfo mainClone);

        /// <summary>Re-resolves <c>RandomStats</c> from <c>RandomStatsId</c> (Settings.RandomItemStatsList); returns the previous value.</summary>
        RandomItemStat ResolveRandomStats(ItemInfo mainInfo);

        /// <summary>Rebuilds Main's StartItems from the StartItem flags; returns the previous contents (for rollback).</summary>
        List<ItemInfo> RebuildStartItems();

        /// <summary>Rollback of <see cref="RebuildStartItems"/>.</summary>
        void RestoreStartItems(List<ItemInfo> previous);

        /// <summary>
        /// RefreshStats for every online player and spawned hero holding one of these items (equipped or in the bag). Isolated per
        /// holder: never throws. Returns the number of holders refreshed.
        /// </summary>
        int RefreshItemHolders(ICollection<ItemInfo> mainInfos);

        /// <summary>Queues the client push (S.GameDataChanged) for every connection holding a changed row.</summary>
        LiveDbPushResult Push(LiveDbPushChanges changes);

        /// <summary>
        /// Sends S.MapAtmosphere (Light, MapDarkLight, WeatherParticles) to every player and observer whose current map runs on one of
        /// these Main MapInfos. Returns the number of connections sent to.
        /// </summary>
        int PushMapAtmosphere(ICollection<MapInfo> mainMaps);

        // ---- Batch C: world tables. Hooks returning an Action return the compensation for a rollback (null = nothing to undo).

        /// <summary>Can an NPC stand on this cell of the running world? (Also asked by the engine before the save.)</summary>
        LiveDbPlacement CheckNpcPlacement(int mapIndex, Point location);

        /// <summary>
        /// Moves the NPC object of <paramref name="mainInfo"/> (finding F10): leave its cell (and tell nearby players) while
        /// Info.MapIndex / Info.Location still name the OLD cell, then set them, then enter the new cell and show it to players near
        /// it. The applier never sets those two fields itself. No NPC object (not placed at world start): only the fields change.
        /// </summary>
        void RelocateNpc(NPCInfo mainInfo, int mapIndex, Point location);

        /// <summary>Spawns an NPC object for a new NPC (as a map load does). False = not placed (map not loaded / invalid cell).</summary>
        bool SpawnNpc(NPCInfo mainClone);

        /// <summary>Rollback of <see cref="SpawnNpc"/>: removes the NPC object of this info from the world.</summary>
        void RemoveNpc(NPCInfo mainInfo);

        /// <summary>
        /// Binds the script of the NPC's new FileName (quests and speech come from the new script; unsaved used goods stay). Throws
        /// when the script cannot be loaded, leaving the NPC as it was.
        /// </summary>
        Action RebindNpcScript(NPCInfo mainInfo);

        /// <summary>
        /// Visibility (schedule + per-player conditions) re-checked for nearby players; re-broadcast (remove + info) of NPCs whose
        /// looks or name changed. Isolated per NPC: never throws.
        /// </summary>
        void RefreshNpcs(ICollection<NPCInfo> visibility, ICollection<NPCInfo> rebroadcast);

        /// <summary>The shared quest reload (<see cref="QuestReload"/>) of a changed QuestInfo.FileName.</summary>
        QuestReload.Outcome ReloadQuest(QuestInfo mainInfo);

        /// <summary>Rebuilds the map's mine (mine state resets).</summary>
        Action RebuildMine(MapInfo mainMap);

        /// <summary>
        /// Removes the safe-zone spell objects created from <paramref name="previous"/> (never player spells) and creates them from
        /// the map's current safe zones. Returns the geometry it created (for the rollback rebuild).
        /// </summary>
        List<LiveDbZone> RebuildSafeZones(MapInfo mainMap, List<LiveDbZone> previous);

        /// <summary>Rebuilds Envir.StartPoints from the loaded maps' safe zones; returns the previous contents.</summary>
        List<SafeZoneInfo> RebuildStartPoints();

        /// <summary>Rollback of <see cref="RebuildStartPoints"/>.</summary>
        void RestoreStartPoints(List<SafeZoneInfo> previous);

        /// <summary>InSafeZone recomputed for players, heroes and pets on the map. Isolated per object: never throws.</summary>
        void RecomputeSafeZoneFlags(MapInfo mainMap);

        /// <summary>Rebuilds the respawn's cache (monster, spawn cells, route); spawned monsters stay.</summary>
        Action RebuildRespawn(MapInfo mainMap, RespawnInfo mainInfo);

        /// <summary>A new respawn row in Main's map info: create its MapRespawn (spawns at the next map process).</summary>
        Action RespawnInserted(MapInfo mainMap, RespawnInfo mainClone);

        /// <summary>A respawn row left Main's map info: drop its MapRespawn (spawned monsters stay).</summary>
        Action RespawnDeleted(MapInfo mainMap, RespawnInfo mainInfo);

        /// <summary>Envir.SavedSpawns membership follows SaveRespawnTime / RespawnTicks.</summary>
        Action SyncSavedSpawn(MapInfo mainMap, RespawnInfo mainInfo);

        /// <summary>True while the conquest's war is on (its rows are then saved only).</summary>
        bool ConquestAtWar(ConquestInfo mainInfo);

        /// <summary>The running dragon recomputes its drop area from its info.</summary>
        Action DragonDropAreaChanged();

        /// <summary>A new row got this index in Main (npcs, gameshop, maprespawns): keep Main's counter at least as high.</summary>
        void IndexAllocated(string endpoint, int index);
    }

    /// <summary>One existing row whose live columns are copied onto Main.</summary>
    internal sealed class LiveDbUpdate
    {
        public GameDataTable Table;
        public int Index;

        /// <summary>Child tables: the parent's index (MapIndex).</summary>
        public int ParentIndex;

        /// <summary>Typed new values of the LIVE columns only (restart-only columns never get here).</summary>
        public List<KeyValuePair<GameDataColumn, object>> Values = new List<KeyValuePair<GameDataColumn, object>>();

        /// <summary>
        /// Position-keyed rows: every column's value in the editor copy BEFORE this batch. Main's row at the same position must still
        /// hold exactly these values, otherwise it is another record (positions no longer line up) and the row is saved only.
        /// </summary>
        public List<KeyValuePair<GameDataColumn, object>> Expected;

        public LiveDbRowOutcome Row;

        /// <summary>Filled by the applier: false when Main has no such row.</summary>
        public bool Found;

        /// <summary>Filled by the applier: the whole row was not applied live (saved only), and why.</summary>
        public string RowRefusal;

        /// <summary>Filled by the applier: single fields not applied live (saved only), and why.</summary>
        public Dictionary<string, string> FieldRefusals;

        public void RefuseField(string field, string reason)
        {
            (FieldRefusals ??= new Dictionary<string, string>(StringComparer.Ordinal))[field] = reason;
        }
    }

    /// <summary>One new row cloned into Main.</summary>
    internal sealed class LiveDbInsert
    {
        public GameDataTable Table;

        /// <summary>A deep clone of the Edit record prepared off the game thread (never the Edit object itself).</summary>
        public object Clone;

        /// <summary>Child tables: the parent's index (MapIndex).</summary>
        public int ParentIndex;

        public LiveDbRowOutcome Row;

        /// <summary>Filled by the applier: the row reached Main.</summary>
        public bool Applied;

        /// <summary>Filled by the applier: why the row did not reach Main (saved only).</summary>
        public string Refusal;

        /// <summary>Filled by the applier: a remark on an applied row (e.g. an NPC that is not placed).</summary>
        public string Note;
    }

    /// <summary>One deleted row removed from Main.</summary>
    internal sealed class LiveDbDelete
    {
        public GameDataTable Table;

        /// <summary>The row's key BEFORE the batch (position-keyed rows: the position before any delete of this batch).</summary>
        public int Index;

        public int ParentIndex;

        /// <summary>Position-keyed rows: the editor copy's values before the batch (see <see cref="LiveDbUpdate.Expected"/>).</summary>
        public List<KeyValuePair<GameDataColumn, object>> Expected;

        public LiveDbRowOutcome Row;

        public bool Applied;
        public string Refusal;
    }

    /// <summary>A position-keyed child list touched by the batch: Main's list must have the editor copy's pre-batch length.</summary>
    internal sealed class LiveDbParentCheck
    {
        public GameDataTable Table;
        public int ParentIndex;
        public int ExpectedCount;
    }

    /// <summary>Everything the game-thread step does for one batch.</summary>
    internal sealed class LiveDbLivePlan
    {
        public readonly List<LiveDbUpdate> Updates = new List<LiveDbUpdate>();
        public readonly List<LiveDbInsert> Inserts = new List<LiveDbInsert>();
        public readonly List<LiveDbDelete> Deletes = new List<LiveDbDelete>();
        public readonly List<LiveDbParentCheck> ParentChecks = new List<LiveDbParentCheck>();

        /// <summary>The saved spells (Edit snapshot): copied onto Main's MagicInfo objects, like the legacy save's magic sync.</summary>
        public List<MagicInfo> MagicSnapshot;

        /// <summary>The batch changes more client-visible rows than [LiveDb] MaxPushRows: no client push (item updates were refused live).</summary>
        public bool SkipPush;

        public bool HasWork => Updates.Count > 0 || Inserts.Count > 0 || Deletes.Count > 0 || (MagicSnapshot != null && MagicSnapshot.Count > 0);
    }

    internal sealed class LiveDbApplyResult
    {
        public int AppliedFields;
        public int MonstersQueued;
        public int Inserted;
        public int DeletedLive;
        public int DropsReloaded;
        public int MagicSynced;
        public int ItemHoldersRefreshed;
        public int RandomStatsResolved;
        public bool StartItemsRebuilt;

        // Batch C
        public int NpcsMoved;
        public int NpcsSpawned;
        public int ScriptsRebound;
        public int QuestsReloaded;
        public int SafeZoneMapsRebuilt;
        public int MinesRebuilt;
        public int RespawnsRebuilt;

        /// <summary>Rows / fields the running world refused (saved only).</summary>
        public int Refused;

        /// <summary>Parents whose child list did not line up with the editor copy (the engine marks them diverged).</summary>
        public readonly List<string> DivergedParents = new List<string>();

        /// <summary>What the client push did (null when nothing was pushed).</summary>
        public LiveDbPushResult Push;

        /// <summary>The push was skipped because the batch is larger than [LiveDb] MaxPushRows.</summary>
        public bool PushSkipped;

        /// <summary>The push threw (logged). The world change stays applied; clients see it after a relog.</summary>
        public bool PushFailed;
    }

    /// <summary>
    /// The game-thread half of the live step:
    /// <list type="number">
    /// <item>position-keyed child lists are checked against the editor copy (length; each touched row's values) — a list that does not
    /// line up is never changed by position;</item>
    /// <item>live columns of existing rows are copied onto Main's records (NPC moves go through <see cref="ILiveDbHooks.RelocateNpc"/>,
    /// remove-before-change; a conquest at war, a missing Main row or a wrong position refuse the row: saved only);</item>
    /// <item>prepared clones are inserted, deleted rows removed (resolved first, then removed by reference);</item>
    /// <item>spells are synced;</item>
    /// <item>the throwing hooks run (drop files, random stats, start items, quest files, NPC scripts, respawn caches, saved spawns,
    /// mines, safe-zone spells, start points, dragon area, the monster sweep queue), each recording its compensation;</item>
    /// <item>the notify hooks run (NPC visibility / re-broadcast, safe-zone flags, item holders: isolated per object, never throw);</item>
    /// <item>the client push is queued (S.GameDataChanged with fresh snapshot copies).</item>
    /// </list>
    /// Any exception before the push restores every before-image (values, rows, spells, drop lists, random stats, start items) and
    /// runs the compensations, then is rethrown, so Main is never left half-applied; a push failure is logged and leaves the change in
    /// place. Runs inside an admin action, i.e. after the mob-thread barrier (finding F1: every mob thread parked); takes no lock
    /// except lock(Connections) in the push; plain loops over Main's lists, no LINQ.
    /// </summary>
    internal static class LiveDbMainApplier
    {
        internal const string DivergedReason = "the running world's rows of this parent no longer line up with the saved ones (positions changed); saved only until a restart";
        internal const string MismatchReason = "the running world's row at this position holds different values (positions no longer line up); saved only until a restart";
        internal const string WarReason = "a conquest war is in progress; saved only (takes effect after a restart)";
        internal const string NoRowReason = "the running world has no such row; it takes effect after a restart";

        /// <summary>Monster columns that change what a spawned monster computes in RefreshAll.</summary>
        internal static bool NeedsMonsterRefresh(GameDataColumn column)
        {
            var n = column.Name;
            return n.StartsWith(GameDataColumns.StatPrefix, StringComparison.Ordinal) || n == "AttackSpeed" || n == "MoveSpeed";
        }

        /// <summary>
        /// Item columns that change what a holder computes in HumanObject.RefreshStats (stats, weights, light, sets, looks, special
        /// modes, fast run).
        /// </summary>
        internal static bool NeedsHolderRefresh(GameDataColumn column)
        {
            var n = column.Name;
            return n.StartsWith(GameDataColumns.StatPrefix, StringComparison.Ordinal) || n == "Weight" || n == "Light" || n == "Set" ||
                   n == "Shape" || n == "Effect" || n == "Unique" || n == "CanFastRun";
        }

        /// <summary>NPC columns that decide who sees the NPC (schedule and per-player conditions).</summary>
        internal static bool IsNpcVisibilityColumn(string n) =>
            n == "TimeVisible" || n == "HourStart" || n == "MinuteStart" || n == "HourEnd" || n == "MinuteEnd" || n == "MinLev" || n == "MaxLev" ||
            n == "DayofWeek" || n == "ClassRequired" || n == "FlagNeeded";

        internal static bool IsNpcPlacementColumn(string n) => n == "MapIndex" || n == "Location_X" || n == "Location_Y";

        /// <summary>Respawn columns the MapRespawn cache is built from.</summary>
        internal static bool IsRespawnCacheColumn(string n) =>
            n == "MonsterIndex" || n == "Location_X" || n == "Location_Y" || n == "RoutePath" || n == "Spread";

        /// <summary>
        /// The holder set for a refresh: the changed items plus every class / level based base item whose real item may be one of them
        /// (Functions.GetRealItem picks variants by name prefix, so a holder of the base computes with the variant). One-off loop over
        /// Main's items per batch.
        /// </summary>
        internal static HashSet<ItemInfo> ExpandForRealItems(ICollection<ItemInfo> changed, IList<ItemInfo> allItems)
        {
            var set = new HashSet<ItemInfo>();
            if (changed == null) return set;
            foreach (var info in changed)
                if (info != null) set.Add(info);
            if (allItems == null || set.Count == 0) return set;

            for (var i = 0; i < allItems.Count; i++)
            {
                var b = allItems[i];
                if (b == null || set.Contains(b) || !(b.ClassBased || b.LevelBased) || string.IsNullOrEmpty(b.Name)) continue;
                foreach (var info in changed)
                {
                    if (info == null || info == b || info.Name == null || !info.Name.StartsWith(b.Name, StringComparison.Ordinal)) continue;
                    set.Add(b);
                    break;
                }
            }

            return set;
        }

        /// <summary>The spell fields a client sees (ClientMagic): name, costs, icon, levels, needs, range, delay.</summary>
        internal static bool ClientMagicDiffers(MagicInfo a, MagicInfo b)
        {
            if (a == null || b == null) return a != b;
            return a.Name != b.Name || a.BaseCost != b.BaseCost || a.LevelCost != b.LevelCost || a.Icon != b.Icon || a.Level1 != b.Level1 ||
                   a.Level2 != b.Level2 || a.Level3 != b.Level3 || a.Need1 != b.Need1 || a.Need2 != b.Need2 || a.Need3 != b.Need3 ||
                   a.Range != b.Range || a.DelayBase != b.DelayBase || a.DelayReduction != b.DelayReduction;
        }

        /// <summary>
        /// Off the game thread (worker, under EditLock): a deep clone of a new Edit record for Main, or null when inserts of this table do
        /// not reach Main in this build. Monsters: fresh Stats / lists, empty Drops (loaded by the live step), no script flags.
        /// Items: fresh Stats; RandomStats is re-resolved against Main's settings by the live step. Game shop / safe zones: the item /
        /// map reference is bound to Main's record by the live step.
        /// </summary>
        public static object PrepareInsert(GameDataTable table, object editEntity)
        {
            if (table == null || editEntity == null) return null;
            switch (table.Endpoint)
            {
                case "monsters" when editEntity is MonsterInfo:
                {
                    var clone = (MonsterInfo)GameDataClone.Clone(editEntity);
                    clone.Drops = new List<DropInfo>();
                    clone.HasSpawnScript = false;
                    clone.HasDieScript = false;
                    return clone;
                }
                case "items" when editEntity is ItemInfo:
                {
                    var clone = (ItemInfo)GameDataClone.Clone(editEntity);
                    clone.RandomStats = null;
                    return clone;
                }
                case "npcs" when editEntity is NPCInfo:
                case "maprespawns" when editEntity is RespawnInfo:
                case "mapmovements" when editEntity is MovementInfo:
                case "mapminezones" when editEntity is MineZone:
                    return GameDataClone.Clone(editEntity);
                case "gameshop" when editEntity is GameShopItem:
                {
                    var clone = (GameShopItem)GameDataClone.Clone(editEntity);
                    clone.Info = null; // never an Edit ItemInfo in Main: bound to Main's item by the live step
                    return clone;
                }
                case "mapsafezones" when editEntity is SafeZoneInfo:
                {
                    var clone = (SafeZoneInfo)GameDataClone.Clone(editEntity);
                    clone.Info = null; // bound to Main's MapInfo by the live step
                    return clone;
                }
                default:
                    return null;
            }
        }

        private struct ValueImage
        {
            public GameDataColumn Column;
            public GameDataRow Row;
            public object Before;
        }

        private struct RemovedRow
        {
            public System.Collections.IList List;
            public int Position;
            public object Entity;
        }

        /// <summary>Everything the live step must undo when it fails.</summary>
        private sealed class Undo
        {
            public readonly List<ValueImage> Values = new List<ValueImage>();
            public readonly List<KeyValuePair<System.Collections.IList, object>> Inserted = new List<KeyValuePair<System.Collections.IList, object>>();
            public readonly List<RemovedRow> Removed = new List<RemovedRow>();
            public readonly List<KeyValuePair<MonsterInfo, List<DropInfo>>> Drops = new List<KeyValuePair<MonsterInfo, List<DropInfo>>>();
            public readonly List<KeyValuePair<ItemInfo, RandomItemStat>> RandomStats = new List<KeyValuePair<ItemInfo, RandomItemStat>>();

            /// <summary>World compensations that must run while Main's values are still the NEW ones (NPC moves back, spawned NPCs, ...).</summary>
            public readonly List<Action> Before = new List<Action>();

            /// <summary>World compensations that need the restored values and rows (safe-zone spells rebuilt from the old zones).</summary>
            public readonly List<Action> After = new List<Action>();

            public List<MagicInfo> MagicBefore;
            public bool StartItemsTaken;
            public List<ItemInfo> StartItemsBefore;
            public HashSet<ItemInfo> HoldersTouched;

            public void AddBefore(Action a)
            {
                if (a != null) Before.Add(a);
            }
        }

        /// <summary>Hook work collected while the rows are applied (deduplicated; run in step 4 / 5).</summary>
        private sealed class Work
        {
            public readonly HashSet<MonsterInfo> Refresh = new HashSet<MonsterInfo>();
            public readonly List<MonsterInfo> Reload = new List<MonsterInfo>();
            public readonly HashSet<ItemInfo> Holders = new HashSet<ItemInfo>();
            public readonly List<ItemInfo> Resolve = new List<ItemInfo>();
            public bool StartItems;

            public readonly List<KeyValuePair<QuestInfo, LiveDbUpdate>> QuestReloads = new List<KeyValuePair<QuestInfo, LiveDbUpdate>>();
            public readonly List<KeyValuePair<NPCInfo, LiveDbUpdate>> NpcScripts = new List<KeyValuePair<NPCInfo, LiveDbUpdate>>();
            public readonly HashSet<NPCInfo> NpcVisibility = new HashSet<NPCInfo>();
            public readonly HashSet<NPCInfo> NpcRebroadcast = new HashSet<NPCInfo>();
            public readonly List<KeyValuePair<MapInfo, RespawnInfo>> RespawnRebuilds = new List<KeyValuePair<MapInfo, RespawnInfo>>();
            public readonly List<KeyValuePair<MapInfo, RespawnInfo>> SavedSpawns = new List<KeyValuePair<MapInfo, RespawnInfo>>();
            public readonly List<MapInfo> Mines = new List<MapInfo>();
            public readonly List<MapInfo> SafeZoneMaps = new List<MapInfo>();
            public bool StartPoints;
            public bool DragonArea;

            // Client push: Main records whose client copy changed (snapshots are taken after the hooks).
            public readonly List<ItemInfo> PushItems = new List<ItemInfo>();
            public readonly List<MonsterInfo> PushMonsters = new List<MonsterInfo>();
            public readonly HashSet<Spell> PushSpells = new HashSet<Spell>();
            public readonly List<NPCInfo> PushNpcs = new List<NPCInfo>();
            public readonly List<QuestInfo> PushQuests = new List<QuestInfo>();
            public readonly List<GameShopItem> PushShop = new List<GameShopItem>();
            public readonly List<int> PushShopRemoved = new List<int>();
            public readonly HashSet<MapInfo> MapsInvalidated = new HashSet<MapInfo>();
            /// <summary>Maps whose Light / MapDarkLight / WeatherParticles changed: S.MapAtmosphere to the players on them.</summary>
            public readonly HashSet<MapInfo> MapAtmosphere = new HashSet<MapInfo>();

            public static void AddOnce<T>(List<T> list, T value) where T : class
            {
                if (value != null && !list.Contains(value)) list.Add(value);
            }
        }

        public static LiveDbApplyResult Apply(LiveDbLivePlan plan, GameDataContext main, ILiveDbHooks hooks)
        {
            var result = new LiveDbApplyResult();
            if (plan == null || main == null) return result;

            var undo = new Undo();
            var work = new Work();

            try
            {
                // 0. position-keyed child lists: lengths must still match the editor copy; safe-zone geometry before any change
                var refusedParents = CheckParents(plan, main, result);
                var zonesBefore = CaptureZones(plan, main);

                // 1. live columns of existing rows
                for (var i = 0; i < plan.Updates.Count; i++) ApplyUpdate(plan.Updates[i], main, hooks, undo, work, refusedParents, result);

                // 2. new rows (prepared clones), then deleted rows
                for (var i = 0; i < plan.Inserts.Count; i++) ApplyInsert(plan.Inserts[i], main, hooks, undo, work, refusedParents, result);
                ApplyDeletes(plan, main, hooks, undo, work, refusedParents, result);

                // 3. spells: the saved values onto Main's MagicInfo objects (players' UserMagic.Info point at them)
                if (plan.MagicSnapshot != null && plan.MagicSnapshot.Count > 0)
                {
                    undo.MagicBefore = new List<MagicInfo>(main.Magics.Count);
                    for (var i = 0; i < main.Magics.Count; i++)
                        if (main.Magics[i] != null) undo.MagicBefore.Add((MagicInfo)GameDataClone.Clone(main.Magics[i]));
                    result.MagicSynced = GameDataMagicSync.CopyInto(plan.MagicSnapshot, main.Magics);

                    for (var i = 0; i < undo.MagicBefore.Count; i++)
                    {
                        var before = undo.MagicBefore[i];
                        for (var j = 0; j < main.Magics.Count; j++)
                        {
                            var now = main.Magics[j];
                            if (now == null || now.Spell != before.Spell) continue;
                            if (ClientMagicDiffers(before, now)) work.PushSpells.Add(now.Spell);
                            break;
                        }
                    }
                }

                if (hooks != null)
                {
                    // 4. hooks that can fail (each records its compensation first-to-last)
                    RunThrowingHooks(hooks, main, undo, work, zonesBefore, result);

                    // 5. notify hooks: isolated per object, never throw; they only tell players about the applied state
                    if (work.NpcVisibility.Count > 0 || work.NpcRebroadcast.Count > 0) hooks.RefreshNpcs(work.NpcVisibility, work.NpcRebroadcast);
                    for (var i = 0; i < work.SafeZoneMaps.Count; i++) hooks.RecomputeSafeZoneFlags(work.SafeZoneMaps[i]);
                    if (work.Holders.Count > 0)
                    {
                        undo.HoldersTouched = work.Holders;
                        result.ItemHoldersRefreshed = hooks.RefreshItemHolders(work.Holders);
                    }
                }
            }
            catch (Exception)
            {
                Rollback(undo, main, hooks);
                throw;
            }

            // 6. client push: queued last, so nothing after it can fail; a push failure never undoes the world change.
            if (hooks != null)
            {
                if (plan.SkipPush)
                {
                    result.PushSkipped = true;
                }
                else
                {
                    try
                    {
                        var changes = Snapshot(work.PushItems, work.PushMonsters, work.PushSpells, work.PushNpcs, work.PushQuests, work.PushShop,
                            work.PushShopRemoved, work.MapsInvalidated, main);
                        if (!changes.IsEmpty) result.Push = hooks.Push(changes);
                    }
                    catch (Exception ex)
                    {
                        result.PushFailed = true;
                        WebAdminLog.Error(ex, "[LiveDb] the client push failed; the change is applied, clients see it after a relog");
                    }

                    // the players on a changed map see its new light / weather at once (others get it from S.MapChanged on entry)
                    if (work.MapAtmosphere.Count > 0)
                    {
                        try
                        {
                            hooks.PushMapAtmosphere(work.MapAtmosphere);
                        }
                        catch (Exception ex)
                        {
                            result.PushFailed = true;
                            WebAdminLog.Error(ex, "[LiveDb] the map light / weather push failed; the change is applied, players see it on their next map entry");
                        }
                    }
                }
            }

            return result;
        }

        // ================================================================== step 0

        private static HashSet<string> CheckParents(LiveDbLivePlan plan, GameDataContext main, LiveDbApplyResult result)
        {
            var refused = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < plan.ParentChecks.Count; i++)
            {
                var check = plan.ParentChecks[i];
                var key = LiveDbState.ParentKey(check.Table.ParentEndpoint, check.ParentIndex);
                var list = ChildList(check.Table.Endpoint, FindMainMap(main, check.ParentIndex));
                var count = list?.Count ?? 0;
                if (count == check.ExpectedCount) continue;
                if (refused.Add(Key(check.Table, check.ParentIndex))) result.DivergedParents.Add(key);
            }

            return refused;
        }

        private static string Key(GameDataTable table, int parentIndex) => table.Endpoint + "#" + parentIndex.ToString(CultureInfo.InvariantCulture);

        private static Dictionary<MapInfo, List<LiveDbZone>> CaptureZones(LiveDbLivePlan plan, GameDataContext main)
        {
            var zones = new Dictionary<MapInfo, List<LiveDbZone>>();
            for (var i = 0; i < plan.Updates.Count; i++)
                if (plan.Updates[i].Table.Endpoint == "mapsafezones") CaptureZones(zones, FindMainMap(main, plan.Updates[i].ParentIndex));
            for (var i = 0; i < plan.Inserts.Count; i++)
                if (plan.Inserts[i].Table.Endpoint == "mapsafezones") CaptureZones(zones, FindMainMap(main, plan.Inserts[i].ParentIndex));
            for (var i = 0; i < plan.Deletes.Count; i++)
                if (plan.Deletes[i].Table.Endpoint == "mapsafezones") CaptureZones(zones, FindMainMap(main, plan.Deletes[i].ParentIndex));
            return zones;
        }

        private static void CaptureZones(Dictionary<MapInfo, List<LiveDbZone>> zones, MapInfo map)
        {
            if (map == null || zones.ContainsKey(map)) return;
            zones[map] = Geometry(map);
        }

        internal static List<LiveDbZone> Geometry(MapInfo map)
        {
            var list = new List<LiveDbZone>(map.SafeZones.Count);
            for (var i = 0; i < map.SafeZones.Count; i++)
            {
                var z = map.SafeZones[i];
                if (z != null) list.Add(new LiveDbZone { Location = z.Location, Size = z.Size, StartPoint = z.StartPoint });
            }

            return list;
        }

        private static bool Matches(GameDataRow row, List<KeyValuePair<GameDataColumn, object>> expected)
        {
            if (expected == null) return true;
            for (var i = 0; i < expected.Count; i++)
            {
                var c = expected[i].Key;
                if (!Equals(c.Get(row), expected[i].Value)) return false;
            }

            return true;
        }

        // ================================================================== step 1

        private static void ApplyUpdate(LiveDbUpdate u, GameDataContext main, ILiveDbHooks hooks, Undo undo, Work work, HashSet<string> refusedParents,
            LiveDbApplyResult result)
        {
            var positional = u.Table.PositionKeyed && u.Table.IsChild;
            if (positional && refusedParents.Contains(Key(u.Table, u.ParentIndex)))
            {
                u.Found = true; // the list exists but does not line up: refused, never looked up by position
                Refuse(u, DivergedReason, result);
                return;
            }

            var row = FindMainRow(u.Table, main, u.Index);
            if (row == null) return;
            u.Found = true;
            if (u.Values.Count == 0) return;

            if (positional && !Matches(row, u.Expected))
            {
                Refuse(u, MismatchReason, result);
                return;
            }

            var entity = row.Entity;
            if (entity is ConquestInfo conquest && hooks != null && hooks.ConquestAtWar(conquest))
            {
                Refuse(u, WarReason, result);
                return;
            }

            ItemInfo shopItem = null;
            if (entity is GameShopItem && TryGet(u, "ItemIndex", out var itemIndex))
            {
                shopItem = FindMainItem(main, ToInt(itemIndex));
                if (shopItem == null)
                {
                    Refuse(u, "the running world has no item #" + ToInt(itemIndex).ToString(CultureInfo.InvariantCulture) + "; saved only until a restart", result);
                    return;
                }
            }

            var npc = entity as NPCInfo;
            var npcMoved = false;
            var npcShowedOnBigMap = npc != null && npc.ShowOnBigMap;
            var npcOldMap = npc?.MapIndex ?? 0;
            if (npc != null && hooks != null) npcMoved = MoveNpc(u, npc, hooks, undo, result);

            // copy the remaining live columns (NPC placement columns are handled by MoveNpc, never copied: F10)
            for (var v = 0; v < u.Values.Count; v++)
            {
                var column = u.Values[v].Key;
                if (npc != null && hooks != null && IsNpcPlacementColumn(column.Name)) continue;
                undo.Values.Add(new ValueImage { Column = column, Row = row, Before = column.Get(row) });
                column.Set(row, u.Values[v].Value);
                result.AppliedFields++;
            }

            // what the change means for the world / the clients
            switch (entity)
            {
                case MonsterInfo monster:
                    for (var v = 0; v < u.Values.Count; v++)
                    {
                        var column = u.Values[v].Key;
                        if (NeedsMonsterRefresh(column)) work.Refresh.Add(monster);
                        else if (column.Name == "DropPath") Work.AddOnce(work.Reload, monster);
                    }

                    Work.AddOnce(work.PushMonsters, monster);
                    break;
                case ItemInfo item:
                    for (var v = 0; v < u.Values.Count; v++)
                    {
                        var column = u.Values[v].Key;
                        if (NeedsHolderRefresh(column)) work.Holders.Add(item);
                        else if (column.Name == "RandomStatsId") Work.AddOnce(work.Resolve, item);
                        else if (column.Name == "StartItem") work.StartItems = true;
                    }

                    Work.AddOnce(work.PushItems, item);
                    break;
                case MagicInfo magic:
                    work.PushSpells.Add(magic.Spell);
                    break;
                case NPCInfo n:
                    for (var v = 0; v < u.Values.Count; v++)
                    {
                        var name = u.Values[v].Key.Name;
                        if (IsNpcVisibilityColumn(name)) work.NpcVisibility.Add(n);
                        else if (name == "Image" || name == "Name") work.NpcRebroadcast.Add(n);
                        else if (name == "FileName") work.NpcScripts.Add(new KeyValuePair<NPCInfo, LiveDbUpdate>(n, u));
                    }

                    // big-map info lists NPCs shown on the big map (name, place, icon, rate, teleport)
                    if (npcShowedOnBigMap || n.ShowOnBigMap)
                    {
                        Invalidate(work, FindMainMap(main, n.MapIndex));
                        if (npcMoved) Invalidate(work, FindMainMap(main, npcOldMap));
                    }

                    Work.AddOnce(work.PushNpcs, n);
                    break;
                case QuestInfo quest:
                    // QuestInfo's loader turns a saved 0 into "no maximum"; keep the running copy the same as after a restart.
                    if (quest.RequiredMaxLevel == 0 && TryGet(u, "RequiredMaxLevel", out _)) quest.RequiredMaxLevel = ushort.MaxValue;
                    if (TryGet(u, "FileName", out _)) work.QuestReloads.Add(new KeyValuePair<QuestInfo, LiveDbUpdate>(quest, u));
                    Work.AddOnce(work.PushQuests, quest);
                    break;
                case MapInfo map:
                    if (TryGet(u, "MineIndex", out _)) Work.AddOnce(work.Mines, map);
                    if (TryGet(u, "Title", out _) || TryGet(u, "BigMap", out _)) InvalidateWithSources(work, main, map);
                    if (TryGet(u, "Light", out _) || TryGet(u, "MapDarkLight", out _) || TryGet(u, "WeatherParticles", out _)) work.MapAtmosphere.Add(map);
                    break;
                case SafeZoneInfo zone:
                    Work.AddOnce(work.SafeZoneMaps, row.Parent as MapInfo);
                    if (TryGet(u, "StartPoint", out _) || zone.StartPoint) work.StartPoints = true;
                    break;
                case RespawnInfo respawn:
                {
                    var parent = row.Parent as MapInfo;
                    var cache = false;
                    var saved = false;
                    for (var v = 0; v < u.Values.Count; v++)
                    {
                        var name = u.Values[v].Key.Name;
                        if (IsRespawnCacheColumn(name)) cache = true;
                        else if (name == "SaveRespawnTime" || name == "RespawnTicks") saved = true;
                    }

                    if (cache) work.RespawnRebuilds.Add(new KeyValuePair<MapInfo, RespawnInfo>(parent, respawn));
                    if (saved) work.SavedSpawns.Add(new KeyValuePair<MapInfo, RespawnInfo>(parent, respawn));
                    break;
                }
                case MovementInfo _:
                    Invalidate(work, row.Parent as MapInfo);
                    break;
                case MineZone _:
                    Work.AddOnce(work.Mines, row.Parent as MapInfo);
                    break;
                case GameShopItem shop:
                    if (shopItem != null)
                    {
                        var previous = shop.Info;
                        shop.Info = shopItem; // Main's ItemInfo, never the editor copy's
                        undo.AddBefore(() => shop.Info = previous);
                    }

                    Work.AddOnce(work.PushShop, shop);
                    break;
                case DragonInfo _:
                    for (var v = 0; v < u.Values.Count; v++)
                        if (u.Values[v].Key.Name.StartsWith("DropArea", StringComparison.Ordinal)) work.DragonArea = true;
                    break;
            }
        }

        /// <summary>
        /// An NPC update that changes MapIndex / Location: checks the target, then moves the NPC through the hook (which leaves the old
        /// cell BEFORE the fields change — F10). A target that is not a walkable cell of a loaded map refuses the placement fields only.
        /// Returns true when the NPC moved.
        /// </summary>
        private static bool MoveNpc(LiveDbUpdate u, NPCInfo npc, ILiveDbHooks hooks, Undo undo, LiveDbApplyResult result)
        {
            var mapIndex = npc.MapIndex;
            var x = npc.Location.X;
            var y = npc.Location.Y;
            var count = 0;
            for (var v = 0; v < u.Values.Count; v++)
            {
                var name = u.Values[v].Key.Name;
                if (!IsNpcPlacementColumn(name)) continue;
                count++;
                if (name == "MapIndex") mapIndex = ToInt(u.Values[v].Value);
                else if (name == "Location_X") x = ToInt(u.Values[v].Value);
                else y = ToInt(u.Values[v].Value);
            }

            if (count == 0) return false;
            var target = new Point(x, y);
            if (mapIndex == npc.MapIndex && target == npc.Location)
            {
                result.AppliedFields += count;
                return false;
            }

            var placement = hooks.CheckNpcPlacement(mapIndex, target);
            if (placement != LiveDbPlacement.Valid)
            {
                var reason = placement == LiveDbPlacement.MapNotLoaded
                    ? "the target map is not loaded in the running world; the NPC moves after a restart"
                    : "the target is not a walkable cell of the map; saved only";
                for (var v = 0; v < u.Values.Count; v++)
                    if (IsNpcPlacementColumn(u.Values[v].Key.Name)) u.RefuseField(u.Values[v].Key.Name, reason);
                result.Refused++;
                return false;
            }

            var oldMap = npc.MapIndex;
            var oldLocation = npc.Location;
            hooks.RelocateNpc(npc, mapIndex, target);
            undo.AddBefore(() => hooks.RelocateNpc(npc, oldMap, oldLocation)); // runs before the values are restored (F10 again)
            result.AppliedFields += count;
            result.NpcsMoved++;
            return true;
        }

        private static void Refuse(LiveDbUpdate u, string reason, LiveDbApplyResult result)
        {
            u.RowRefusal = reason;
            result.Refused++;
        }

        private static bool TryGet(LiveDbUpdate u, string column, out object value)
        {
            for (var i = 0; i < u.Values.Count; i++)
            {
                if (u.Values[i].Key.Name != column) continue;
                value = u.Values[i].Value;
                return true;
            }

            value = null;
            return false;
        }

        private static int ToInt(object value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

        private static void Invalidate(Work work, MapInfo map)
        {
            if (map != null) work.MapsInvalidated.Add(map);
        }

        /// <summary>A map's title / big map changed: its own big-map info and every map whose movements show it as a destination.</summary>
        private static void InvalidateWithSources(Work work, GameDataContext main, MapInfo map)
        {
            Invalidate(work, map);
            for (var i = 0; i < main.Maps.Count; i++)
            {
                var other = main.Maps[i];
                if (other == null || other == map) continue;
                for (var m = 0; m < other.Movements.Count; m++)
                {
                    var mv = other.Movements[m];
                    if (mv == null || mv.MapIndex != map.Index || !mv.ShowOnBigMap) continue;
                    Invalidate(work, other);
                    break;
                }
            }
        }

        // ================================================================== step 2

        private static void ApplyInsert(LiveDbInsert ins, GameDataContext main, ILiveDbHooks hooks, Undo undo, Work work, HashSet<string> refusedParents,
            LiveDbApplyResult result)
        {
            var positional = ins.Table.PositionKeyed && ins.Table.IsChild;
            if (positional && refusedParents.Contains(Key(ins.Table, ins.ParentIndex)))
            {
                ins.Refusal = DivergedReason;
                result.Refused++;
                return;
            }

            MapInfo parent = null;
            if (ins.Table.IsChild && ins.Table.ParentEndpoint == "maps")
            {
                parent = FindMainMap(main, ins.ParentIndex);
                if (parent == null)
                {
                    ins.Refusal = "the running world has no map #" + ins.ParentIndex.ToString(CultureInfo.InvariantCulture) + "; it takes effect after a restart";
                    result.Refused++;
                    return;
                }
            }

            switch (ins.Clone)
            {
                case MonsterInfo mob when ins.Table.Endpoint == "monsters":
                    main.Monsters.Add(mob);
                    undo.Inserted.Add(new KeyValuePair<System.Collections.IList, object>(main.Monsters, mob));
                    hooks?.MonsterInserted(mob);
                    Work.AddOnce(work.Reload, mob);
                    break;
                case ItemInfo item when ins.Table.Endpoint == "items":
                    main.Items.Add(item);
                    undo.Inserted.Add(new KeyValuePair<System.Collections.IList, object>(main.Items, item));
                    hooks?.ItemInserted(item);
                    Work.AddOnce(work.Resolve, item);
                    if (item.StartItem) work.StartItems = true;
                    break;
                case NPCInfo npc when ins.Table.Endpoint == "npcs":
                {
                    main.Npcs.Add(npc);
                    undo.Inserted.Add(new KeyValuePair<System.Collections.IList, object>(main.Npcs, npc));
                    hooks?.IndexAllocated("npcs", npc.Index);
                    var map = FindMainMap(main, npc.MapIndex);
                    if (map != null)
                    {
                        map.NPCs.Add(npc);
                        undo.Inserted.Add(new KeyValuePair<System.Collections.IList, object>(map.NPCs, npc));
                        if (npc.ShowOnBigMap) Invalidate(work, map);
                    }

                    if (hooks != null)
                    {
                        if (hooks.SpawnNpc(npc))
                        {
                            undo.AddBefore(() => hooks.RemoveNpc(npc));
                            result.NpcsSpawned++;
                        }
                        else
                        {
                            ins.Note = "added; not placed in the running world (its map is not loaded or the cell is not walkable) until a restart";
                        }
                    }

                    break;
                }
                case GameShopItem shop when ins.Table.Endpoint == "gameshop":
                {
                    var item = FindMainItem(main, shop.ItemIndex);
                    if (item == null)
                    {
                        ins.Refusal = "the running world has no item #" + shop.ItemIndex.ToString(CultureInfo.InvariantCulture) + "; it takes effect after a restart";
                        result.Refused++;
                        return;
                    }

                    shop.Info = item; // Main's ItemInfo, never the editor copy's
                    main.GameShop.Add(shop);
                    undo.Inserted.Add(new KeyValuePair<System.Collections.IList, object>(main.GameShop, shop));
                    hooks?.IndexAllocated("gameshop", shop.GIndex);
                    Work.AddOnce(work.PushShop, shop);
                    break;
                }
                case RespawnInfo respawn when ins.Table.Endpoint == "maprespawns":
                    parent.Respawns.Add(respawn);
                    undo.Inserted.Add(new KeyValuePair<System.Collections.IList, object>(parent.Respawns, respawn));
                    if (hooks != null)
                    {
                        hooks.IndexAllocated("maprespawns", respawn.RespawnIndex);
                        undo.AddBefore(hooks.RespawnInserted(parent, respawn));
                    }

                    break;
                case SafeZoneInfo zone when ins.Table.Endpoint == "mapsafezones":
                    zone.Info = parent;
                    parent.SafeZones.Add(zone);
                    undo.Inserted.Add(new KeyValuePair<System.Collections.IList, object>(parent.SafeZones, zone));
                    Work.AddOnce(work.SafeZoneMaps, parent);
                    if (zone.StartPoint) work.StartPoints = true;
                    break;
                case MovementInfo movement when ins.Table.Endpoint == "mapmovements":
                    parent.Movements.Add(movement);
                    undo.Inserted.Add(new KeyValuePair<System.Collections.IList, object>(parent.Movements, movement));
                    Invalidate(work, parent);
                    break;
                case MineZone mine when ins.Table.Endpoint == "mapminezones":
                    parent.MineZones.Add(mine);
                    undo.Inserted.Add(new KeyValuePair<System.Collections.IList, object>(parent.MineZones, mine));
                    Work.AddOnce(work.Mines, parent);
                    break;
                default:
                    ins.Refusal = "new " + ins.Table.Label + " rows take effect after a restart";
                    return;
            }

            ins.Applied = true;
            result.Inserted++;
        }

        private static void ApplyDeletes(LiveDbLivePlan plan, GameDataContext main, ILiveDbHooks hooks, Undo undo, Work work, HashSet<string> refusedParents,
            LiveDbApplyResult result)
        {
            if (plan.Deletes.Count == 0) return;

            // Resolve every target first (positions are the pre-delete ones), then remove by reference.
            var targets = new List<KeyValuePair<LiveDbDelete, GameDataRow>>(plan.Deletes.Count);
            for (var i = 0; i < plan.Deletes.Count; i++)
            {
                var d = plan.Deletes[i];
                var positional = d.Table.PositionKeyed && d.Table.IsChild;
                if (positional && refusedParents.Contains(Key(d.Table, d.ParentIndex)))
                {
                    d.Refusal = DivergedReason;
                    result.Refused++;
                    continue;
                }

                var row = FindMainRow(d.Table, main, d.Index);
                if (row == null)
                {
                    d.Refusal = NoRowReason;
                    result.Refused++;
                    continue;
                }

                if (positional && !Matches(row, d.Expected))
                {
                    d.Refusal = MismatchReason;
                    result.Refused++;
                    continue;
                }

                targets.Add(new KeyValuePair<LiveDbDelete, GameDataRow>(d, row));
            }

            for (var i = 0; i < targets.Count; i++)
            {
                var d = targets[i].Key;
                var row = targets[i].Value;
                var list = ListOf(d.Table, row, main);
                var position = list == null ? -1 : list.IndexOf(row.Entity);
                if (position < 0)
                {
                    d.Refusal = NoRowReason;
                    result.Refused++;
                    continue;
                }

                list.RemoveAt(position);
                undo.Removed.Add(new RemovedRow { List = list, Position = position, Entity = row.Entity });
                d.Applied = true;
                result.DeletedLive++;

                var parent = row.Parent as MapInfo;
                switch (row.Entity)
                {
                    case RespawnInfo respawn:
                        if (hooks != null) undo.AddBefore(hooks.RespawnDeleted(parent, respawn));
                        break;
                    case GameShopItem shop:
                        if (!work.PushShopRemoved.Contains(shop.GIndex)) work.PushShopRemoved.Add(shop.GIndex);
                        work.PushShop.Remove(shop);
                        break;
                    case SafeZoneInfo zone:
                        Work.AddOnce(work.SafeZoneMaps, parent);
                        if (zone.StartPoint) work.StartPoints = true;
                        break;
                    case MovementInfo _:
                        Invalidate(work, parent);
                        break;
                    case MineZone _:
                        Work.AddOnce(work.Mines, parent);
                        break;
                }
            }
        }

        // ================================================================== step 4

        private static void RunThrowingHooks(ILiveDbHooks hooks, GameDataContext main, Undo undo, Work work, Dictionary<MapInfo, List<LiveDbZone>> zonesBefore,
            LiveDbApplyResult result)
        {
            for (var i = 0; i < work.Reload.Count; i++)
            {
                var previous = hooks.ReloadDrops(work.Reload[i]);
                undo.Drops.Add(new KeyValuePair<MonsterInfo, List<DropInfo>>(work.Reload[i], previous));
                result.DropsReloaded++;
            }

            for (var i = 0; i < work.Resolve.Count; i++)
            {
                var previous = hooks.ResolveRandomStats(work.Resolve[i]);
                undo.RandomStats.Add(new KeyValuePair<ItemInfo, RandomItemStat>(work.Resolve[i], previous));
                result.RandomStatsResolved++;
            }

            if (work.StartItems)
            {
                undo.StartItemsBefore = hooks.RebuildStartItems();
                undo.StartItemsTaken = true;
                result.StartItemsRebuilt = true;
            }

            // Quest files: a refused reload keeps the running quest and refuses only FileName (shared rule with the Scripts editor).
            for (var i = 0; i < work.QuestReloads.Count; i++)
            {
                var quest = work.QuestReloads[i].Key;
                var outcome = hooks.ReloadQuest(quest);
                if (outcome != null && outcome.Reloaded)
                {
                    undo.AddBefore(outcome.Undo);
                    result.QuestsReloaded++;
                    continue;
                }

                RevertField(undo, quest, "FileName");
                work.QuestReloads[i].Value.RefuseField("FileName", "the quest file was not reloaded (" + (outcome?.Refusal ?? "refused") +
                                                                   "); the running quest keeps its old file until a restart");
                result.Refused++;
            }

            // NPC scripts: a script that cannot be loaded keeps the old one and refuses only FileName.
            for (var i = 0; i < work.NpcScripts.Count; i++)
            {
                var npc = work.NpcScripts[i].Key;
                try
                {
                    undo.AddBefore(hooks.RebindNpcScript(npc));
                    work.NpcRebroadcast.Add(npc); // quest marks come from the script
                    result.ScriptsRebound++;
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "[LiveDb] NPC " + npc.Index.ToString(CultureInfo.InvariantCulture) + ": the script " + npc.FileName + " could not be loaded");
                    RevertField(undo, npc, "FileName");
                    work.NpcScripts[i].Value.RefuseField("FileName", "the script could not be loaded (see the server log); the NPC keeps its old script until a restart");
                    result.Refused++;
                }
            }

            for (var i = 0; i < work.RespawnRebuilds.Count; i++)
            {
                undo.AddBefore(hooks.RebuildRespawn(work.RespawnRebuilds[i].Key, work.RespawnRebuilds[i].Value));
                result.RespawnsRebuilt++;
            }

            for (var i = 0; i < work.SavedSpawns.Count; i++)
                undo.AddBefore(hooks.SyncSavedSpawn(work.SavedSpawns[i].Key, work.SavedSpawns[i].Value));

            for (var i = 0; i < work.Mines.Count; i++)
            {
                if (work.Mines[i] == null) continue;
                undo.AddBefore(hooks.RebuildMine(work.Mines[i]));
                result.MinesRebuilt++;
            }

            for (var i = 0; i < work.SafeZoneMaps.Count; i++)
            {
                var map = work.SafeZoneMaps[i];
                if (map == null) continue;
                if (!zonesBefore.TryGetValue(map, out var previous)) previous = new List<LiveDbZone>();
                var created = hooks.RebuildSafeZones(map, previous);
                undo.After.Add(() =>
                {
                    hooks.RebuildSafeZones(map, created ?? new List<LiveDbZone>());
                    hooks.RecomputeSafeZoneFlags(map);
                });
                result.SafeZoneMapsRebuilt++;
            }

            if (work.StartPoints)
            {
                var previous = hooks.RebuildStartPoints();
                undo.AddBefore(() => hooks.RestoreStartPoints(previous));
            }

            if (work.DragonArea) undo.AddBefore(hooks.DragonDropAreaChanged());

            if (work.Refresh.Count > 0)
            {
                hooks.QueueMonsterRefresh(work.Refresh);
                result.MonstersQueued = work.Refresh.Count;
            }
        }

        /// <summary>Puts one copied field back to its before-image (a refused hook keeps the running record as it was).</summary>
        private static void RevertField(Undo undo, object entity, string column)
        {
            for (var i = undo.Values.Count - 1; i >= 0; i--)
            {
                var image = undo.Values[i];
                if (image.Row.Entity != entity || image.Column.Name != column) continue;
                image.Column.Set(image.Row, image.Before);
                return;
            }
        }

        // ================================================================== push snapshot

        /// <summary>Fresh copies of the changed Main records (never Main references: packets must not change after they are queued).</summary>
        internal static LiveDbPushChanges Snapshot(List<ItemInfo> items, List<MonsterInfo> monsters, HashSet<Spell> spells)
        {
            return Snapshot(items, monsters, spells, null, null, null, null, null, null);
        }

        /// <summary>
        /// Fresh copies of the changed Main records: items (<see cref="ItemInfo.CopyFrom"/>), monsters / NPCs (ClientInformation, new
        /// objects), quests (a viewer-free ClientQuestInfo with copied lists; the production push builds each connection's own copy so
        /// the links it mentions are sent first), game-shop rows (a copy whose Info is a copy of Main's item), removed shop indexes and
        /// invalidated maps (Main's MapInfo keys, matched against each connection's SentMapInfo). A shop row's item is copied from
        /// <paramref name="main"/>'s ItemInfo of the same index (rows loaded at world start still point at the editor copy's item:
        /// Known debt of Envir.BindGameShop).
        /// </summary>
        internal static LiveDbPushChanges Snapshot(List<ItemInfo> items, List<MonsterInfo> monsters, HashSet<Spell> spells, List<NPCInfo> npcs,
            List<QuestInfo> quests, List<GameShopItem> shop, List<int> shopRemoved, HashSet<MapInfo> maps, GameDataContext main)
        {
            var changes = new LiveDbPushChanges();
            if (items != null)
                for (var i = 0; i < items.Count; i++)
                {
                    var copy = new ItemInfo();
                    copy.CopyFrom(items[i]);
                    changes.Items[items[i]] = copy;
                }

            if (monsters != null)
                for (var i = 0; i < monsters.Count; i++)
                    changes.Monsters[monsters[i]] = monsters[i].ClientInformation; // a new object with copied values

            if (spells != null)
                foreach (var spell in spells)
                    changes.Spells.Add(spell);

            if (npcs != null)
                for (var i = 0; i < npcs.Count; i++)
                    changes.Npcs[npcs[i]] = npcs[i].ClientInformation; // a new object with copied values

            if (quests != null)
                for (var i = 0; i < quests.Count; i++)
                    changes.Quests[quests[i]] = CopyQuest(quests[i].CreateClientQuestInfo());

            if (shop != null)
                for (var i = 0; i < shop.Count; i++)
                {
                    var g = shop[i];
                    var item = (main != null ? FindMainItem(main, g?.ItemIndex ?? 0) : null) ?? g?.Info;
                    if (g == null || item == null) continue;
                    changes.GameShop.Add(new ServerPackets.GameDataChanged.ShopEntry { Item = CopyShopItem(g, item), StockLevel = g.Stock });
                    changes.GameShopSources.Add(g);
                }

            if (shopRemoved != null)
                for (var i = 0; i < shopRemoved.Count; i++)
                    if (!changes.GameShopRemoved.Contains(shopRemoved[i])) changes.GameShopRemoved.Add(shopRemoved[i]);

            if (maps != null)
                foreach (var map in maps)
                    if (map != null) changes.MapsInvalidated.Add(map);

            return changes;
        }

        /// <summary>A game-shop row for a packet: a shallow copy whose Info is a copy of <paramref name="item"/> (never a live object).</summary>
        internal static GameShopItem CopyShopItem(GameShopItem g, ItemInfo item)
        {
            var copy = (GameShopItem)GameDataClone.Clone(g);
            var info = new ItemInfo();
            info.CopyFrom(item);
            copy.Info = info;
            return copy;
        }

        /// <summary>Own lists for a ClientQuestInfo (CreateClientQuestInfo may hand out Main's lists).</summary>
        internal static ClientQuestInfo CopyQuest(ClientQuestInfo q)
        {
            if (q == null) return null;
            q.Description = new List<string>(q.Description ?? new List<string>());
            q.TaskDescription = new List<string>(q.TaskDescription ?? new List<string>());
            q.ReturnDescription = new List<string>(q.ReturnDescription ?? new List<string>());
            q.CompletionDescription = new List<string>(q.CompletionDescription ?? new List<string>());
            q.RewardsFixedItem = new List<QuestItemReward>(q.RewardsFixedItem ?? new List<QuestItemReward>());
            q.RewardsSelectItem = new List<QuestItemReward>(q.RewardsSelectItem ?? new List<QuestItemReward>());
            return q;
        }

        // ================================================================== rollback

        private static void Rollback(Undo undo, GameDataContext main, ILiveDbHooks hooks)
        {
            // World compensations that need the NEW values (NPC moves back while Info still names the new cell, spawned NPCs, ...).
            for (var i = undo.Before.Count - 1; i >= 0; i--) Try(undo.Before[i], "a world compensation");

            for (var i = undo.Drops.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (undo.Drops[i].Value != null) undo.Drops[i].Key.Drops = undo.Drops[i].Value;
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "[LiveDb] restoring a drop list failed");
                }
            }

            for (var i = undo.RandomStats.Count - 1; i >= 0; i--) undo.RandomStats[i].Key.RandomStats = undo.RandomStats[i].Value;

            if (undo.StartItemsTaken && undo.StartItemsBefore != null && hooks != null)
            {
                try
                {
                    hooks.RestoreStartItems(undo.StartItemsBefore);
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "[LiveDb] restoring the start items failed");
                }
            }

            if (undo.MagicBefore != null)
            {
                try
                {
                    GameDataMagicSync.CopyInto(undo.MagicBefore, main.Magics);
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "[LiveDb] restoring spells failed");
                }
            }

            for (var i = undo.Inserted.Count - 1; i >= 0; i--)
            {
                try
                {
                    undo.Inserted[i].Key.Remove(undo.Inserted[i].Value);
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "[LiveDb] removing an inserted row failed");
                }
            }

            for (var i = undo.Removed.Count - 1; i >= 0; i--)
            {
                try
                {
                    var r = undo.Removed[i];
                    r.List.Insert(Math.Min(r.Position, r.List.Count), r.Entity);
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "[LiveDb] putting a deleted row back failed");
                }
            }

            for (var i = undo.Values.Count - 1; i >= 0; i--)
            {
                try
                {
                    undo.Values[i].Column.Set(undo.Values[i].Row, undo.Values[i].Before);
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "[LiveDb] restoring " + undo.Values[i].Column.Name + " failed");
                }
            }

            // World compensations that need the restored values / rows (safe-zone spells from the old zones).
            for (var i = undo.After.Count - 1; i >= 0; i--) Try(undo.After[i], "a world compensation");

            // Holders recomputed with the new values before the failure: recompute them with the restored ones.
            if (undo.HoldersTouched != null && hooks != null)
            {
                try
                {
                    hooks.RefreshItemHolders(undo.HoldersTouched);
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "[LiveDb] re-refreshing item holders after a rollback failed");
                }
            }
        }

        private static void Try(Action action, string what)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "[LiveDb] rollback: " + what + " failed");
            }
        }

        // ================================================================== Main lookups (plain loops)

        internal static MapInfo FindMainMap(GameDataContext main, int index)
        {
            for (var i = 0; i < main.Maps.Count; i++)
                if (main.Maps[i] != null && main.Maps[i].Index == index) return main.Maps[i];
            return null;
        }

        internal static ItemInfo FindMainItem(GameDataContext main, int index)
        {
            for (var i = 0; i < main.Items.Count; i++)
                if (main.Items[i] != null && main.Items[i].Index == index) return main.Items[i];
            return null;
        }

        /// <summary>The list a map child table keeps its rows in (null for other tables / no map).</summary>
        internal static System.Collections.IList ChildList(string endpoint, MapInfo map)
        {
            if (map == null) return null;
            switch (endpoint)
            {
                case "mapsafezones": return map.SafeZones;
                case "maprespawns": return map.Respawns;
                case "mapmovements": return map.Movements;
                case "mapminezones": return map.MineZones;
                default: return null;
            }
        }

        /// <summary>The list a Main row lives in (for removal).</summary>
        private static System.Collections.IList ListOf(GameDataTable table, GameDataRow row, GameDataContext main)
        {
            switch (table.Endpoint)
            {
                case "gameshop": return main.GameShop;
                case "npcs": return main.Npcs;
                case "quests": return main.Quests;
                case "maps": return main.Maps;
                case "items": return main.Items;
                case "monsters": return main.Monsters;
                case "conquests": return main.Conquests;
                default: return ChildList(table.Endpoint, row.Parent as MapInfo);
            }
        }

        /// <summary>Main's row with this key: plain loops per table (no LINQ on the game thread).</summary>
        internal static GameDataRow FindMainRow(GameDataTable table, GameDataContext main, int index)
        {
            switch (table.Endpoint)
            {
                case "monsters":
                    for (var i = 0; i < main.Monsters.Count; i++)
                        if (main.Monsters[i] != null && main.Monsters[i].Index == index) return new GameDataRow { Index = index, Entity = main.Monsters[i], Position = i };
                    return null;
                case "items":
                    for (var i = 0; i < main.Items.Count; i++)
                        if (main.Items[i] != null && main.Items[i].Index == index) return new GameDataRow { Index = index, Entity = main.Items[i], Position = i };
                    return null;
                case "magic":
                    for (var i = 0; i < main.Magics.Count; i++)
                        if (main.Magics[i] != null && (int)main.Magics[i].Spell == index) return new GameDataRow { Index = index, Entity = main.Magics[i], Position = i };
                    return null;
                case "npcs":
                    for (var i = 0; i < main.Npcs.Count; i++)
                        if (main.Npcs[i] != null && main.Npcs[i].Index == index) return new GameDataRow { Index = index, Entity = main.Npcs[i], Position = i };
                    return null;
                case "quests":
                    for (var i = 0; i < main.Quests.Count; i++)
                        if (main.Quests[i] != null && main.Quests[i].Index == index) return new GameDataRow { Index = index, Entity = main.Quests[i], Position = i };
                    return null;
                case "maps":
                    for (var i = 0; i < main.Maps.Count; i++)
                        if (main.Maps[i] != null && main.Maps[i].Index == index) return new GameDataRow { Index = index, Entity = main.Maps[i], Position = i };
                    return null;
                case "gameshop":
                    for (var i = 0; i < main.GameShop.Count; i++)
                        if (main.GameShop[i] != null && main.GameShop[i].GIndex == index) return new GameDataRow { Index = index, Entity = main.GameShop[i], Position = i };
                    return null;
                case "conquests":
                    for (var i = 0; i < main.Conquests.Count; i++)
                        if (main.Conquests[i] != null && main.Conquests[i].Index == index) return new GameDataRow { Index = index, Entity = main.Conquests[i], Position = i };
                    return null;
                case "dragon":
                {
                    var d = main.Dragon();
                    return index == 1 && d != null ? new GameDataRow { Index = 1, Entity = d } : null;
                }
                case "maprespawns":
                    for (var m = 0; m < main.Maps.Count; m++)
                    {
                        var map = main.Maps[m];
                        if (map == null) continue;
                        for (var i = 0; i < map.Respawns.Count; i++)
                            if (map.Respawns[i] != null && map.Respawns[i].RespawnIndex == index)
                                return new GameDataRow { Index = index, Entity = map.Respawns[i], Parent = map, ParentIndex = map.Index, Position = i };
                    }

                    return null;
                case "mapsafezones":
                case "mapmovements":
                case "mapminezones":
                {
                    var parentIndex = index / GameDataRegistry.Stride;
                    var position = index % GameDataRegistry.Stride;
                    var map = FindMainMap(main, parentIndex);
                    var list = ChildList(table.Endpoint, map);
                    if (list == null || position < 0 || position >= list.Count || list[position] == null) return null;
                    return new GameDataRow { Index = index, Entity = list[position], Parent = map, ParentIndex = parentIndex, Position = position };
                }
                default:
                    // Restart-only tables (conquest children): never applied live; kept for completeness.
                    return table.Find(main, index);
            }
        }
    }
}

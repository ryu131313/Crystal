using System.Drawing;
using System.Text.Json;
using System.Text.RegularExpressions;
using Server.MirDatabase;
using Server.WebAdmin.GameData;
using Server.WebAdmin.LiveDb;

namespace Server.WebAdmin.Tests
{
    /// <summary>
    /// LiveDb Batches A and B: the engine pipeline (validate-all, apply with before-images, save, game-thread live step, journal), the Main
    /// applier, the classification, the journal / LRU, the sweep cursor, live items, the client push (selection, chunking) and the frontend contract. Pure: every test builds its own
    /// Edit / Main contexts and fakes, so nothing touches Envir, Settings or process-wide statics.
    /// </summary>
    public partial class LiveDbTests
    {
        // ------------------------------------------------------------------ fixtures

        private static JsonElement Json(string json)
        {
            using (var doc = JsonDocument.Parse(json))
                return doc.RootElement.Clone();
        }

        /// <summary>The same small world twice (separate objects): Edit and Main.</summary>
        private static GameDataContext World()
        {
            var ctx = new GameDataContext();
            var hen = new MonsterInfo { Index = 1, Name = "Hen", Level = 1, AttackSpeed = 2500 };
            hen.Stats[Stat.HP] = 15;
            ctx.Monsters.Add(hen);
            var deer = new MonsterInfo { Index = 2, Name = "Deer", Level = 3 };
            deer.Stats[Stat.HP] = 30;
            ctx.Monsters.Add(deer);
            ctx.Items.Add(new ItemInfo { Index = 1, Name = "WoodenSword", Type = ItemType.Weapon, Price = 100 });
            ctx.Magics.Add(new MagicInfo { Name = "Fencing", Spell = Spell.Fencing, Level1 = 7 });
            var bichon = new MapInfo { Index = 1, FileName = "0", Title = "Bichon" };
            bichon.SafeZones.Add(new SafeZoneInfo { Info = bichon, Location = new Point(10, 10), Size = 5, StartPoint = true });
            bichon.SafeZones.Add(new SafeZoneInfo { Info = bichon, Location = new Point(20, 20), Size = 6 });
            bichon.Respawns.Add(new RespawnInfo { RespawnIndex = 1, MonsterIndex = 1, Location = new Point(30, 30), Count = 3, Delay = 10 });
            bichon.Respawns.Add(new RespawnInfo { RespawnIndex = 2, MonsterIndex = 2, Location = new Point(40, 40), Count = 2, Delay = 5 });
            bichon.Movements.Add(new MovementInfo { MapIndex = 2, Source = new Point(5, 5), Destination = new Point(6, 6), ShowOnBigMap = true });
            bichon.MineZones.Add(new MineZone { Mine = 1, Location = new Point(50, 50), Size = 3 });
            ctx.Maps.Add(bichon);
            var mine = new MapInfo { Index = 2, FileName = "1", Title = "Mine" };
            ctx.Maps.Add(mine);
            ctx.MineSetCount = () => 2;

            // Batch C tables
            var npc = new NPCInfo { Index = 1, Name = "Merchant", FileName = "Merchant", MapIndex = 1, Location = new Point(12, 12), Image = 3, ShowOnBigMap = true };
            ctx.Npcs.Add(npc);
            bichon.NPCs.Add(npc);
            ctx.Quests.Add(new QuestInfo { Index = 1, Name = "FirstSteps", FileName = "FirstSteps", RequiredMinLevel = 1, RequiredMaxLevel = ushort.MaxValue });
            ctx.GameShop.Add(new GameShopItem { GIndex = 1, ItemIndex = 1, Info = ctx.Items[0], GoldPrice = 500, CreditPrice = 10, Count = 1, Class = "All", Category = "Weapon", Stock = 5 });
            ctx.Conquests.Add(new ConquestInfo { Index = 1, Name = "Sabuk", MapIndex = 1, Size = 10, StartHour = 20, WarLength = 60 });
            return ctx;
        }

        private sealed class FakePersistence : ILiveDbPersistence
        {
            public int Saves;
            public Exception Throw;
            public IReadOnlyList<string> Areas = Array.Empty<string>();

            public void EnsureSavable() { }

            public LiveDbSaveOutcome Save()
            {
                if (Throw != null) throw Throw;
                Saves++;
                return new LiveDbSaveOutcome { BackupFile = "Database test.bak", Areas = Areas };
            }
        }

        private sealed class FakeHooks : ILiveDbHooks
        {
            public readonly List<MonsterInfo> Reloaded = new List<MonsterInfo>();
            public readonly List<MonsterInfo> Inserted = new List<MonsterInfo>();
            public readonly List<MonsterInfo> Queued = new List<MonsterInfo>();
            public readonly List<ItemInfo> ItemsInserted = new List<ItemInfo>();
            public readonly List<ItemInfo> Resolved = new List<ItemInfo>();
            public readonly List<ItemInfo> HolderRefreshes = new List<ItemInfo>();
            public readonly List<LiveDbPushChanges> Pushes = new List<LiveDbPushChanges>();
            public int StartItemRebuilds;
            public int StartItemRestores;
            public bool ThrowOnQueue;
            public bool ThrowOnReload;
            public bool ThrowOnPush;
            public bool ThrowOnResolve;

            // Batch C
            public readonly HashSet<(int Map, Point Location)> InvalidCells = new HashSet<(int, Point)>();
            public readonly HashSet<int> UnloadedMaps = new HashSet<int>();
            public readonly List<string> Calls = new List<string>();
            public readonly List<(int OldMap, Point OldLocation, int NewMap, Point NewLocation)> Relocations = new List<(int, Point, int, Point)>();
            public readonly List<NPCInfo> Spawned = new List<NPCInfo>();
            public readonly List<NPCInfo> Removed = new List<NPCInfo>();
            public readonly List<NPCInfo> Rebound = new List<NPCInfo>();
            public readonly List<NPCInfo> Visibility = new List<NPCInfo>();
            public readonly List<NPCInfo> Rebroadcast = new List<NPCInfo>();
            public readonly List<QuestInfo> QuestsReloaded = new List<QuestInfo>();
            public string QuestRefusal;
            public bool ThrowOnScript;
            public readonly List<MapInfo> Mines = new List<MapInfo>();
            public readonly List<(MapInfo Map, List<LiveDbZone> Previous, List<LiveDbZone> Created)> ZoneRebuilds = new List<(MapInfo, List<LiveDbZone>, List<LiveDbZone>)>();
            public bool ThrowOnZones;
            public int StartPointRebuilds;
            public int StartPointRestores;
            public readonly List<MapInfo> SafeZoneFlags = new List<MapInfo>();
            public readonly List<RespawnInfo> RespawnRebuilds = new List<RespawnInfo>();
            public readonly List<RespawnInfo> RespawnsInserted = new List<RespawnInfo>();
            public readonly List<RespawnInfo> RespawnsDeleted = new List<RespawnInfo>();
            public int RespawnUndos;
            public readonly List<RespawnInfo> SavedSpawnSyncs = new List<RespawnInfo>();
            public bool AtWar;
            public int DragonAreas;
            public readonly List<(string Endpoint, int Index)> Allocated = new List<(string, int)>();

            public List<DropInfo> ReloadDrops(MonsterInfo mainInfo)
            {
                if (ThrowOnReload) throw new IOException("drop file locked");
                Reloaded.Add(mainInfo);
                var previous = mainInfo.Drops;
                mainInfo.Drops = new List<DropInfo>();
                return previous;
            }

            public void MonsterInserted(MonsterInfo mainClone) => Inserted.Add(mainClone);

            public void QueueMonsterRefresh(ICollection<MonsterInfo> mainInfos)
            {
                if (ThrowOnQueue) throw new InvalidOperationException("hook failed");
                Queued.AddRange(mainInfos);
            }

            public void ItemInserted(ItemInfo mainClone) => ItemsInserted.Add(mainClone);

            public RandomItemStat ResolveRandomStats(ItemInfo mainInfo)
            {
                if (ThrowOnResolve) throw new InvalidOperationException("resolve failed");
                Resolved.Add(mainInfo);
                var previous = mainInfo.RandomStats;
                mainInfo.RandomStats = new RandomItemStat();
                return previous;
            }

            public List<ItemInfo> RebuildStartItems()
            {
                StartItemRebuilds++;
                return new List<ItemInfo>();
            }

            public void RestoreStartItems(List<ItemInfo> previous) => StartItemRestores++;

            public int RefreshItemHolders(ICollection<ItemInfo> mainInfos)
            {
                HolderRefreshes.AddRange(mainInfos);
                return mainInfos.Count;
            }

            public readonly List<MapInfo> AtmosphereMaps = new List<MapInfo>();

            public int PushMapAtmosphere(ICollection<MapInfo> mainMaps)
            {
                AtmosphereMaps.AddRange(mainMaps);
                return mainMaps.Count;
            }

            public LiveDbPushResult Push(LiveDbPushChanges changes)
            {
                if (ThrowOnPush) throw new InvalidOperationException("push failed");
                Pushes.Add(changes);
                return new LiveDbPushResult { Connections = 1, Packets = 1, Rows = changes.Rows };
            }

            public LiveDbPlacement CheckNpcPlacement(int mapIndex, Point location)
            {
                if (UnloadedMaps.Contains(mapIndex)) return LiveDbPlacement.MapNotLoaded;
                return InvalidCells.Contains((mapIndex, location)) ? LiveDbPlacement.Invalid : LiveDbPlacement.Valid;
            }

            public void RelocateNpc(NPCInfo mainInfo, int mapIndex, Point location)
            {
                // The applier must hand over the target while the info still names the OLD cell (F10); the production hook then
                // removes, changes and adds in that order (LiveDbNpcMover.Move).
                Relocations.Add((mainInfo.MapIndex, mainInfo.Location, mapIndex, location));
                Calls.Add("relocate");
                LiveDbNpcMover.Move(mainInfo, mapIndex, location, null, null);
            }

            public bool SpawnNpc(NPCInfo mainClone)
            {
                if (UnloadedMaps.Contains(mainClone.MapIndex)) return false;
                Spawned.Add(mainClone);
                return true;
            }

            public void RemoveNpc(NPCInfo mainInfo) => Removed.Add(mainInfo);

            public Action RebindNpcScript(NPCInfo mainInfo)
            {
                if (ThrowOnScript) throw new InvalidDataException("unknown #DIRECTIVE");
                Rebound.Add(mainInfo);
                return () => Calls.Add("script-undo");
            }

            public void RefreshNpcs(ICollection<NPCInfo> visibility, ICollection<NPCInfo> rebroadcast)
            {
                Visibility.AddRange(visibility);
                Rebroadcast.AddRange(rebroadcast);
            }

            public QuestReload.Outcome ReloadQuest(QuestInfo mainInfo)
            {
                if (QuestRefusal != null) return new QuestReload.Outcome { Refusal = QuestRefusal };
                QuestsReloaded.Add(mainInfo);
                return new QuestReload.Outcome { Reloaded = true, Undo = () => Calls.Add("quest-undo") };
            }

            public Action RebuildMine(MapInfo mainMap)
            {
                Mines.Add(mainMap);
                return () => Calls.Add("mine-undo");
            }

            public List<LiveDbZone> RebuildSafeZones(MapInfo mainMap, List<LiveDbZone> previous)
            {
                if (ThrowOnZones) throw new InvalidOperationException("zones failed");
                var created = LiveDbMainApplier.Geometry(mainMap);
                ZoneRebuilds.Add((mainMap, previous, created));
                return created;
            }

            public List<SafeZoneInfo> RebuildStartPoints()
            {
                StartPointRebuilds++;
                return new List<SafeZoneInfo>();
            }

            public void RestoreStartPoints(List<SafeZoneInfo> previous) => StartPointRestores++;

            public void RecomputeSafeZoneFlags(MapInfo mainMap) => SafeZoneFlags.Add(mainMap);

            public Action RebuildRespawn(MapInfo mainMap, RespawnInfo mainInfo)
            {
                RespawnRebuilds.Add(mainInfo);
                return () => RespawnUndos++;
            }

            public Action RespawnInserted(MapInfo mainMap, RespawnInfo mainClone)
            {
                RespawnsInserted.Add(mainClone);
                return () => RespawnUndos++;
            }

            public Action RespawnDeleted(MapInfo mainMap, RespawnInfo mainInfo)
            {
                RespawnsDeleted.Add(mainInfo);
                return () => RespawnUndos++;
            }

            public Action SyncSavedSpawn(MapInfo mainMap, RespawnInfo mainInfo)
            {
                SavedSpawnSyncs.Add(mainInfo);
                return null;
            }

            public bool ConquestAtWar(ConquestInfo mainInfo) => AtWar;

            public Action DragonDropAreaChanged()
            {
                DragonAreas++;
                return null;
            }

            public void IndexAllocated(string endpoint, int index) => Allocated.Add((endpoint, index));
        }

        private sealed class Rig
        {
            public GameDataContext Edit = World();
            public GameDataContext Main = World();
            public FakePersistence Persistence = new FakePersistence();
            public FakeHooks Hooks = new FakeHooks();
            public string WorldState = "running";
            public bool GameThreadRuns = true;
            public int GameThreadCalls;
            public int EditorForms;
            public readonly List<string> Audit = new List<string>();
            public readonly List<string> Pending = new List<string>();
            public readonly LiveDbEnvironment Env;

            public Rig()
            {
                Env = new LiveDbEnvironment
                {
                    Edit = () => Edit,
                    Main = () => Main,
                    WorldState = () => WorldState,
                    EditorFormsOpen = () => EditorForms,
                    Persistence = Persistence,
                    Hooks = Hooks,
                    RunOnGameThread = (action, timeout) =>
                    {
                        GameThreadCalls++;
                        if (!GameThreadRuns) return false;
                        action();
                        return true;
                    },
                    Audit = (action, target, details, reason) => Audit.Add(action + " " + target),
                    Pending = (area, description) => Pending.Add(area + ": " + description),
                };
            }

            public LiveDbBatchResult Run(string id, string endpoint, string rowsJson, bool dryRun = false, bool flush = false, string deletes = null)
            {
                var req = new LiveDbBatchRequest { Id = id, DryRun = dryRun, FlushUnsaved = flush, Admin = "tester" };
                var ops = new LiveDbTableOps { Endpoint = endpoint };
                if (rowsJson != null) ops.Rows.AddRange(Json(rowsJson).EnumerateArray());
                if (deletes != null) ops.Deletes.AddRange(deletes.Split(',').Select(int.Parse));
                req.Tables.Add(ops);
                return new LiveDbEngine(Env).Run(req);
            }

            public string Rev(string endpoint, int index)
            {
                var t = GameDataRegistry.Get(endpoint);
                return t.Rev(t.Rows(Edit).First(r => r.Index == index));
            }
        }

        private static MonsterInfo M(GameDataContext ctx, int index) => ctx.Monsters.First(m => m.Index == index);

        // ------------------------------------------------------------------ pipeline

        [Fact]
        public void Batch_IsAllOrNothing_OneBadRowRejectsEverything()
        {
            var rig = new Rig();
            var rows = "[{\"Index\":1,\"__rev\":\"" + rig.Rev("monsters", 1) + "\",\"Level\":9}," +
                       "{\"Index\":2,\"__rev\":\"" + rig.Rev("monsters", 2) + "\",\"Name\":\"Hen\"}]"; // duplicate name

            var r = rig.Run("b-1", "monsters", rows);

            Assert.Equal("rejected", r.Status);
            Assert.Equal(422, r.HttpStatus);
            Assert.Equal(1, M(rig.Edit, 1).Level);        // the good row did not apply either
            Assert.Equal("Deer", M(rig.Edit, 2).Name);
            Assert.Equal(0, rig.Persistence.Saves);
            Assert.Equal(0, rig.GameThreadCalls);
            Assert.Empty(rig.Audit);
            Assert.Null(rig.Env.Journal.Find("b-1"));
        }

        [Fact]
        public void Batch_StaleRev_IsAConflict_AndChangesNothing()
        {
            var rig = new Rig();
            var r = rig.Run("b-2", "monsters", "[{\"Index\":1,\"__rev\":\"stale\",\"Level\":9}]");

            Assert.Equal("rejected", r.Status);
            Assert.Equal(409, r.HttpStatus);
            Assert.Single(r.Conflicts);
            Assert.Equal(1, M(rig.Edit, 1).Level);
            Assert.Equal(0, rig.Persistence.Saves);
        }

        [Fact]
        public void SaveFailure_RollsEditBack_IncludingInserts()
        {
            var rig = new Rig { Persistence = { Throw = new IOException("disk full") } };
            var rows = "[{\"Index\":1,\"__rev\":\"" + rig.Rev("monsters", 1) + "\",\"Level\":9,\"Stat_HP\":99}," +
                       "{\"Index\":-1,\"Name\":\"Wolf\",\"Level\":5}]";

            var ex = Assert.Throws<WebAdminException>(() => rig.Run("b-3", "monsters", rows));

            Assert.Equal(500, ex.Status);
            Assert.DoesNotContain("disk full", ex.Message); // never the exception text
            Assert.Equal(1, M(rig.Edit, 1).Level);
            Assert.Equal(15, M(rig.Edit, 1).Stats[Stat.HP]);
            Assert.Equal(2, rig.Edit.Monsters.Count);
            Assert.DoesNotContain(rig.Edit.Monsters, m => m.Name == "Wolf");
            Assert.Equal(0, rig.GameThreadCalls);
            Assert.Null(rig.Env.Journal.Find("b-3"));
        }

        [Fact]
        public void HookFailure_RollsMainBack_ButKeepsTheSave()
        {
            var rig = new Rig { Hooks = { ThrowOnQueue = true } };
            rig.Env.Hooks = rig.Hooks;
            var rows = "[{\"Index\":1,\"__rev\":\"" + rig.Rev("monsters", 1) + "\",\"Stat_HP\":99,\"Level\":4}]";

            var r = rig.Run("b-4", "monsters", rows);

            Assert.Equal("applied", r.Status);
            Assert.True(r.Saved);
            Assert.Equal("rolled-back", r.Live.Outcome);
            Assert.Equal(1, rig.Persistence.Saves);
            Assert.Equal(99, M(rig.Edit, 1).Stats[Stat.HP]); // saved
            Assert.Equal(15, M(rig.Main, 1).Stats[Stat.HP]); // Main restored
            Assert.Equal(1, M(rig.Main, 1).Level);
            Assert.All(r.Rows[0].Changes, c => Assert.False(c.Live));
            Assert.True(r.PendingReboot);
            Assert.Single(rig.Pending);
            Assert.NotNull(rig.Env.Journal.Find("b-4"));
        }

        [Fact]
        public void DropReloadFailure_RestoresValuesAndDropLists()
        {
            var rig = new Rig { Hooks = { ThrowOnReload = true } };
            rig.Env.Hooks = rig.Hooks;
            var dropsBefore = M(rig.Main, 2).Drops;
            var rows = "[{\"Index\":2,\"__rev\":\"" + rig.Rev("monsters", 2) + "\",\"DropPath\":\"DeerDrops\",\"Level\":8}]";

            var r = rig.Run("b-4b", "monsters", rows);

            Assert.Equal("rolled-back", r.Live.Outcome);
            Assert.Equal("", M(rig.Main, 2).DropPath);
            Assert.Equal(3, M(rig.Main, 2).Level);
            Assert.Same(dropsBefore, M(rig.Main, 2).Drops);
            Assert.Equal("DeerDrops", M(rig.Edit, 2).DropPath);
        }

        [Fact]
        public void RestartOnlyFields_NeverReachMain_LiveFieldsDo()
        {
            var rig = new Rig();
            var rows = "[{\"Index\":1,\"__rev\":\"" + rig.Rev("monsters", 1) + "\",\"Name\":\"Chicken\",\"Level\":4,\"Stat_HP\":40,\"AI\":3}]";

            var r = rig.Run("b-5", "monsters", rows);

            Assert.Equal("applied", r.Status);
            var main = M(rig.Main, 1);
            Assert.Equal("partial", r.Live.Outcome);
            Assert.Equal("Hen", main.Name);           // restart-only
            Assert.Equal(0, main.AI);                 // restart-only
            Assert.Equal(4, main.Level);              // read-through
            Assert.Equal(40, main.Stats[Stat.HP]);    // hooked
            Assert.Equal(new[] { main }, rig.Hooks.Queued); // one sweep entry for the kind
            Assert.Contains("monsters.Name", r.RestartFields);
            Assert.Contains("monsters.AI", r.RestartFields);
            Assert.DoesNotContain("monsters.Level", r.RestartFields);
            Assert.True(r.Rows[0].Changes.Single(c => c.Field == "Level").Live);
            Assert.False(r.Rows[0].Changes.Single(c => c.Field == "Name").Live);
            Assert.Equal("Chicken", M(rig.Edit, 1).Name);
            Assert.True(r.PendingReboot);
            Assert.Contains(rig.Audit, a => a == "db.update monsters#1");
            Assert.Contains(rig.Audit, a => a == "db.livedb-batch batch:b-5");
        }

        [Fact]
        public void Inserts_AreDeepClonedIntoMain_WithDropsLoaded()
        {
            var rig = new Rig();
            var r = rig.Run("b-6", "monsters", "[{\"Index\":-1,\"Name\":\"Wolf\",\"Level\":5,\"Stat_HP\":70}]");

            Assert.Equal("applied", r.Status);
            var edit = rig.Edit.Monsters.Single(m => m.Name == "Wolf");
            var main = rig.Main.Monsters.Single(m => m.Name == "Wolf");
            Assert.NotSame(edit, main);
            Assert.NotSame(edit.Stats, main.Stats);
            Assert.NotSame(edit.Drops, main.Drops);
            Assert.Equal(edit.Index, main.Index);
            Assert.Equal(70, main.Stats[Stat.HP]);
            Assert.Equal(new[] { main }, rig.Hooks.Inserted);
            Assert.Contains(main, rig.Hooks.Reloaded);
            Assert.True(r.Rows[0].RowLive);
            Assert.Equal(1, r.Live.InsertedLive);
        }

        [Fact]
        public void Magic_IsCopiedInPlace_OntoMainsSpell()
        {
            var rig = new Rig();
            var mainSpell = rig.Main.Magics[0];
            var rev = rig.Rev("magic", (int)Spell.Fencing);

            var r = rig.Run("b-7", "magic", "[{\"Index\":" + (int)Spell.Fencing + ",\"__rev\":\"" + rev + "\",\"Level1\":12}]");

            Assert.Equal("applied", r.Status);
            Assert.Same(mainSpell, rig.Main.Magics[0]);
            Assert.Equal("applied", r.Live.Outcome);
            Assert.Equal(12, mainSpell.Level1);
            Assert.Empty(r.RestartFields);
            Assert.False(r.PendingReboot);
        }

        [Fact]
        public void DropPath_ReloadsThatMonstersDrops()
        {
            var rig = new Rig();
            var r = rig.Run("b-8", "monsters", "[{\"Index\":2,\"__rev\":\"" + rig.Rev("monsters", 2) + "\",\"DropPath\":\"DeerDrops\"}]");

            Assert.Equal("applied", r.Status);
            Assert.Equal("DeerDrops", M(rig.Main, 2).DropPath);
            Assert.Equal(new[] { M(rig.Main, 2) }, rig.Hooks.Reloaded);
            Assert.Empty(rig.Hooks.Queued);
        }

        [Fact]
        public void RestartOnlyFields_SaveOnly_WithAPendingEntry()
        {
            var rig = new Rig();
            var r = rig.Run("b-9", "maps", "[{\"Index\":1,\"__rev\":\"" + rig.Rev("maps", 1) + "\",\"FileName\":\"7\"}]");

            Assert.Equal("applied", r.Status);
            Assert.Equal(1, rig.Persistence.Saves);
            Assert.Equal("7", rig.Edit.Maps[0].FileName);
            Assert.Equal("0", rig.Main.Maps[0].FileName);
            Assert.Contains("maps.FileName", r.RestartFields);
            Assert.True(r.PendingReboot);
        }

        [Fact]
        public void Divergence_RefusesLiveEditsOfPositionKeyedChildren()
        {
            var rig = new Rig();
            // Switch safe zones on (a later batch does this) to exercise the divergence guard.
            rig.Env.Classify = (ep, col) => ep == "mapsafezones" ? (LiveDbClassification.Planned(ep, col) ?? LiveColumnRule.Restart) : LiveDbClassification.Effective(ep, col);
            rig.Env.State.MarkDiverged(LiveDbState.ParentKey("maps", 1));

            var r = rig.Run("b-10", "mapsafezones", "[{\"Index\":10000,\"__rev\":\"" + rig.Rev("mapsafezones", 10000) + "\",\"Size\":9}]");

            Assert.Equal("applied", r.Status);
            Assert.Equal(9, rig.Edit.Maps[0].SafeZones[0].Size);
            Assert.Equal(5, rig.Main.Maps[0].SafeZones[0].Size);
            var f = r.Rows[0].Changes.Single();
            Assert.False(f.Live);
            Assert.Contains("structure", f.Reason);

            // Without the divergence an edit reaches Main ...
            rig.Env.State.ClearDivergence();
            var r2 = rig.Run("b-11", "mapsafezones", "[{\"Index\":10001,\"__rev\":\"" + rig.Rev("mapsafezones", 10001) + "\",\"Size\":7}]");
            Assert.True(r2.Rows[0].Changes.Single().Live);
            Assert.Equal(7, rig.Main.Maps[0].SafeZones[1].Size);

            // ... but not onto the row whose saved-only edit made it differ from the editor copy (Batch C value guard).
            var r3 = rig.Run("b-11b", "mapsafezones", "[{\"Index\":10000,\"__rev\":\"" + rig.Rev("mapsafezones", 10000) + "\",\"Size\":8}]");
            Assert.False(r3.Rows[0].Changes.Single().Live);
            Assert.Equal(5, rig.Main.Maps[0].SafeZones[0].Size);
        }

        [Fact]
        public void StructuralChangeNotAppliedLive_MarksTheParentDiverged()
        {
            var rig = new Rig { Hooks = { ThrowOnZones = true } };
            rig.Env.Hooks = rig.Hooks;
            var r = rig.Run("b-12", "mapsafezones", null, deletes: "10001");

            Assert.Equal("applied", r.Status);
            Assert.Equal("rolled-back", r.Live.Outcome);
            Assert.Equal(1, r.Deleted);
            Assert.Single(rig.Edit.Maps[0].SafeZones);
            Assert.Equal(2, rig.Main.Maps[0].SafeZones.Count);    // the removal was put back
            Assert.True(rig.Env.State.IsDiverged("maps#1"));
            Assert.False(r.Rows.Single().RowLive);
            Assert.Contains(rig.Audit, a => a == "db.delete mapsafezones#10001");
        }

        [Fact]
        public void RepeatedBatchId_ReturnsTheFirstOutcome_WithoutApplyingTwice()
        {
            var rig = new Rig();
            var rows = "[{\"Index\":1,\"__rev\":\"" + rig.Rev("monsters", 1) + "\",\"Level\":4}]";
            var first = rig.Run("b-13", "monsters", rows);
            var again = rig.Run("b-13", "monsters", rows);

            Assert.False(first.Duplicate);
            Assert.True(again.Duplicate);
            Assert.Equal(first.Id, again.Id);
            Assert.Equal(1, rig.Persistence.Saves);
            Assert.Equal(1, rig.GameThreadCalls);
        }

        [Fact]
        public void WorldStopped_SavesOnly()
        {
            var rig = new Rig { WorldState = "stopped" };
            var r = rig.Run("b-14", "monsters", "[{\"Index\":1,\"__rev\":\"" + rig.Rev("monsters", 1) + "\",\"Level\":4}]");

            Assert.Equal("applied", r.Status);
            Assert.Equal("world-stopped", r.Live.Outcome);
            Assert.Equal(1, rig.Persistence.Saves);
            Assert.Equal(0, rig.GameThreadCalls);
            Assert.Equal(1, M(rig.Main, 1).Level);
            Assert.Equal(4, M(rig.Edit, 1).Level);
        }

        [Fact]
        public void WorldStoppingMidway_LeavesTheSave_AndReportsWorldStopped()
        {
            var rig = new Rig { GameThreadRuns = false };
            var r = rig.Run("b-15", "monsters", "[{\"Index\":1,\"__rev\":\"" + rig.Rev("monsters", 1) + "\",\"Level\":4}]");

            Assert.Equal("world-stopped", r.Live.Outcome);
            Assert.True(r.Saved);
            Assert.False(r.Rows[0].Changes.Single().Live);
            Assert.True(r.PendingReboot);
        }

        [Theory]
        [InlineData("starting")]
        [InlineData("stopping")]
        public void WorldStartingOrStopping_Is503(string state)
        {
            var rig = new Rig { WorldState = state };
            var ex = Assert.Throws<WebAdminException>(() => rig.Run("b-16", "monsters", "[{\"Index\":1,\"Level\":4}]"));
            Assert.Equal(503, ex.Status);
            Assert.Equal(1, M(rig.Edit, 1).Level);
        }

        [Fact]
        public void UnsavedLegacyEdits_Are409_UnlessFlushed()
        {
            var rig = new Rig();
            rig.Env.Unsaved.Note("items", 2);
            var rows = "[{\"Index\":1,\"__rev\":\"" + rig.Rev("monsters", 1) + "\",\"Level\":4}]";

            var ex = Assert.Throws<WebAdminException>(() => rig.Run("b-17", "monsters", rows));
            Assert.Equal(409, ex.Status);
            Assert.Equal(0, rig.Persistence.Saves);

            var dry = rig.Run("b-17", "monsters", rows, dryRun: true);
            Assert.Equal("dry-run", dry.Status);
            Assert.Equal(2, dry.UnsavedChanges);

            rig.Persistence.Areas = new[] { "items" };
            var r = rig.Run("b-18", "monsters", rows, flush: true);
            Assert.Equal("applied", r.Status);
            Assert.Equal(new[] { "items" }, r.FlushedAreas);
            Assert.Contains(rig.Pending, p => p.Contains("other saved changes"));
        }

        [Fact]
        public void EditorFormOpen_Is409()
        {
            var rig = new Rig { EditorForms = 1 };
            var ex = Assert.Throws<WebAdminException>(() => rig.Run("b-19", "monsters", "[{\"Index\":1,\"__rev\":\"" + rig.Rev("monsters", 1) + "\",\"Level\":4}]"));
            Assert.Equal(409, ex.Status);
            Assert.Equal(1, M(rig.Edit, 1).Level);
        }

        [Fact]
        public void DryRun_ChangesNothing_AndPredictsLiveAndRestartFields()
        {
            var rig = new Rig();
            var r = rig.Run("b-20", "monsters", "[{\"Index\":1,\"__rev\":\"" + rig.Rev("monsters", 1) + "\",\"Level\":4,\"Name\":\"Chicken\"}]", dryRun: true);

            Assert.Equal("dry-run", r.Status);
            Assert.True(r.Rows[0].Changes.Single(c => c.Field == "Level").Live);
            Assert.False(r.Rows[0].Changes.Single(c => c.Field == "Name").Live);
            Assert.Equal("Hooked", LiveDbClassification.Effective("monsters", "Stat_HP").Class.ToString());
            Assert.Equal(1, M(rig.Edit, 1).Level);
            Assert.Equal(0, rig.Persistence.Saves);
            Assert.Null(rig.Env.Journal.Find("b-20"));
        }

        [Fact]
        public void TooManyRows_Is413_AndDuplicateRowsAre400()
        {
            var rig = new Rig();
            rig.Env.Options.MaxBatchRows = 1;
            var ex = Assert.Throws<WebAdminException>(() => rig.Run("b-21", "monsters", "[{\"Index\":1,\"Level\":4},{\"Index\":2,\"Level\":4}]"));
            Assert.Equal(413, ex.Status);

            rig.Env.Options.MaxBatchRows = 100;
            ex = Assert.Throws<WebAdminException>(() => rig.Run("b-22", "monsters", "[{\"Index\":1,\"Level\":4},{\"Index\":1,\"Level\":5}]"));
            Assert.Equal(400, ex.Status);
        }

        [Fact]
        public void Parse_RequiresAClientIdAndTables()
        {
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => LiveDbBatchRequest.Parse(Json("{\"tables\":[]}"))).Status);
            Assert.Equal(400, Assert.Throws<WebAdminException>(() => LiveDbBatchRequest.Parse(Json("{\"id\":\"a b\",\"tables\":[{\"endpoint\":\"items\"}]}"))).Status);
            var req = LiveDbBatchRequest.Parse(Json("{\"id\":\"b-1\",\"flushUnsaved\":true,\"tables\":[{\"endpoint\":\"monsters\",\"rows\":[{\"Index\":1}],\"deletes\":[3]}]}"));
            Assert.Equal("b-1", req.Id);
            Assert.True(req.FlushUnsaved);
            Assert.Equal(2, req.OpCount);
        }

        // ------------------------------------------------------------------ Batch B: live items + client push

        private static ItemInfo I(GameDataContext ctx, int index) => ctx.Items.First(i => i.Index == index);

        [Fact]
        public void Items_LiveApply_RefreshesHolders_AndPushesASnapshot()
        {
            var rig = new Rig();
            var mainItem = I(rig.Main, 1);
            var r = rig.Run("bb-1", "items", "[{\"Index\":1,\"__rev\":\"" + rig.Rev("items", 1) + "\",\"Price\":250,\"Stat_MaxDC\":7,\"Name\":\"OakSword\"}]");

            Assert.Equal("applied", r.Status);
            Assert.Equal("partial", r.Live.Outcome);          // Name is restart-only
            Assert.Equal(250u, mainItem.Price);                // read-through
            Assert.Equal(7, mainItem.Stats[Stat.MaxDC]);       // hooked
            Assert.Equal("WoodenSword", mainItem.Name);
            Assert.Equal(new[] { mainItem }, rig.Hooks.HolderRefreshes); // one RefreshStats pass, for the Main record
            Assert.Equal(1, r.Live.ItemHoldersRefreshed);

            var push = Assert.Single(rig.Hooks.Pushes);
            Assert.True(push.Items.TryGetValue(mainItem, out var snap));
            Assert.NotSame(mainItem, snap);                    // never a Main reference (EncodeOnce)
            Assert.NotSame(mainItem.Stats, snap.Stats);
            Assert.Equal(250u, snap.Price);
            Assert.Equal(7, snap.Stats[Stat.MaxDC]);
            Assert.Equal("WoodenSword", snap.Name);            // the client sees what the running world uses
            Assert.Equal(1, r.Live.PushConnections);
            Assert.Contains("items.Name", r.RestartFields);
            Assert.DoesNotContain("items.Price", r.RestartFields);
        }

        [Fact]
        public void Items_ReadThroughOnly_DoNotRefreshHolders_ButArePushed()
        {
            var rig = new Rig();
            var r = rig.Run("bb-2", "items", "[{\"Index\":1,\"__rev\":\"" + rig.Rev("items", 1) + "\",\"RequiredAmount\":12}]");

            Assert.Equal("applied", r.Live.Outcome);
            Assert.Empty(rig.Hooks.HolderRefreshes);
            Assert.Single(rig.Hooks.Pushes[0].Items);
        }

        [Fact]
        public void Items_RandomStatsAndStartItem_RunTheirHooks()
        {
            var rig = new Rig();
            var r = rig.Run("bb-3", "items", "[{\"Index\":1,\"__rev\":\"" + rig.Rev("items", 1) + "\",\"RandomStatsId\":1,\"StartItem\":true}]");

            Assert.Equal("applied", r.Live.Outcome);
            Assert.Equal(new[] { I(rig.Main, 1) }, rig.Hooks.Resolved);
            Assert.Equal(1, rig.Hooks.StartItemRebuilds);
            Assert.True(I(rig.Main, 1).StartItem);
        }

        [Fact]
        public void Items_Insert_IsAClone_WithItsOwnStats_AndHooks()
        {
            var rig = new Rig();
            var r = rig.Run("bb-4", "items", "[{\"Index\":-1,\"Name\":\"IronSword\",\"Type\":\"Weapon\",\"Stat_MaxDC\":5,\"StartItem\":true}]");

            Assert.Equal("applied", r.Status);
            var edit = rig.Edit.Items.Single(i => i.Name == "IronSword");
            var main = rig.Main.Items.Single(i => i.Name == "IronSword");
            Assert.NotSame(edit, main);
            Assert.NotSame(edit.Stats, main.Stats);
            Assert.Equal(edit.Index, main.Index);
            Assert.Equal(5, main.Stats[Stat.MaxDC]);
            Assert.Equal(new[] { main }, rig.Hooks.ItemsInserted);
            Assert.Equal(new[] { main }, rig.Hooks.Resolved);   // RandomStats resolved against Main, not copied from Edit
            Assert.Equal(1, rig.Hooks.StartItemRebuilds);
            Assert.True(r.Rows[0].RowLive);
            Assert.Empty(rig.Hooks.Pushes);                     // no client holds a new item yet
        }

        [Fact]
        public void Items_HookFailure_RollsBackValuesAndRandomStats()
        {
            var rig = new Rig { Hooks = { ThrowOnQueue = true } };
            rig.Env.Hooks = rig.Hooks;
            var mainItem = I(rig.Main, 1);
            var req = new LiveDbBatchRequest { Id = "bb-5", Admin = "tester" };
            req.Tables.Add(new LiveDbTableOps { Endpoint = "items", Rows = Json("[{\"Index\":1,\"__rev\":\"" + rig.Rev("items", 1) + "\",\"RandomStatsId\":2,\"Price\":5}]").EnumerateArray().ToList() });
            req.Tables.Add(new LiveDbTableOps { Endpoint = "monsters", Rows = Json("[{\"Index\":1,\"__rev\":\"" + rig.Rev("monsters", 1) + "\",\"Stat_HP\":99}]").EnumerateArray().ToList() });

            var r = new LiveDbEngine(rig.Env).Run(req);

            Assert.Equal("rolled-back", r.Live.Outcome);
            Assert.Equal(100u, mainItem.Price);
            Assert.Equal(0, mainItem.RandomStatsId);
            Assert.Null(mainItem.RandomStats);                 // the resolved value was put back
            Assert.Empty(rig.Hooks.Pushes);
            Assert.Empty(rig.Hooks.HolderRefreshes);
        }

        [Fact]
        public void PushFailure_KeepsTheLiveChange()
        {
            var rig = new Rig { Hooks = { ThrowOnPush = true } };
            rig.Env.Hooks = rig.Hooks;
            var r = rig.Run("bb-6", "items", "[{\"Index\":1,\"__rev\":\"" + rig.Rev("items", 1) + "\",\"Price\":250}]");

            Assert.Equal("applied", r.Live.Outcome);
            Assert.True(r.Live.PushFailed);
            Assert.Equal(250u, I(rig.Main, 1).Price);
            Assert.True(r.Rows[0].Changes.Single().Live);
        }

        [Fact]
        public void MaxPushRows_RefusesItemsLive_AndSkipsThePush()
        {
            var rig = new Rig();
            rig.Env.Options.MaxPushRows = 1;
            var req = new LiveDbBatchRequest { Id = "bb-7", Admin = "tester" };
            req.Tables.Add(new LiveDbTableOps { Endpoint = "items", Rows = Json("[{\"Index\":1,\"__rev\":\"" + rig.Rev("items", 1) + "\",\"Price\":250}]").EnumerateArray().ToList() });
            req.Tables.Add(new LiveDbTableOps { Endpoint = "monsters", Rows = Json("[{\"Index\":2,\"__rev\":\"" + rig.Rev("monsters", 2) + "\",\"Level\":9}]").EnumerateArray().ToList() });

            var dry = new LiveDbEngine(rig.Env).Run(new LiveDbBatchRequest { Id = "bb-7d", DryRun = true, Tables = req.Tables });
            Assert.False(dry.Rows.Single(x => x.Endpoint == "items").Changes.Single().Live);
            Assert.True(dry.Rows.Single(x => x.Endpoint == "monsters").Changes.Single().Live);

            var r = new LiveDbEngine(rig.Env).Run(req);

            Assert.Equal("applied", r.Status);
            Assert.Equal(100u, I(rig.Main, 1).Price);          // refused live: saved only
            Assert.Equal(250u, I(rig.Edit, 1).Price);
            Assert.Equal(9, M(rig.Main, 2).Level);              // monsters still apply live
            Assert.True(r.Live.PushSkipped);
            Assert.Empty(rig.Hooks.Pushes);
            var f = r.Rows.Single(x => x.Endpoint == "items").Changes.Single();
            Assert.False(f.Live);
            Assert.Contains("MaxPushRows", f.Reason);
            Assert.Contains("items.Price", r.RestartFields);
            Assert.Equal("partial", r.Live.Outcome);
        }

        [Fact]
        public void MonsterAndMagicChanges_ArePushed()
        {
            var rig = new Rig();
            var r1 = rig.Run("bb-8", "monsters", "[{\"Index\":2,\"__rev\":\"" + rig.Rev("monsters", 2) + "\",\"Level\":9,\"Stat_HP\":44}]");
            var push = Assert.Single(rig.Hooks.Pushes);
            Assert.True(push.Monsters.TryGetValue(M(rig.Main, 2), out var cm));
            Assert.Equal(9, cm.Level);
            Assert.Equal(44, cm.Stats[Stat.HP]);
            Assert.Equal("applied", r1.Live.Outcome);

            var r2 = rig.Run("bb-9", "magic", "[{\"Index\":" + (int)Spell.Fencing + ",\"__rev\":\"" + rig.Rev("magic", (int)Spell.Fencing) + "\",\"Level1\":15}]");
            Assert.Equal("applied", r2.Live.Outcome);
            Assert.Contains(Spell.Fencing, rig.Hooks.Pushes[1].Spells);
        }

        [Fact]
        public void ExpandForRealItems_AddsClassAndLevelBasedBases()
        {
            var baseItem = new ItemInfo { Index = 1, Name = "Necklace", LevelBased = true };
            var variant = new ItemInfo { Index = 2, Name = "Necklace30" };
            var other = new ItemInfo { Index = 3, Name = "Ring", ClassBased = true };
            var plain = new ItemInfo { Index = 4, Name = "Neck" }; // a prefix, but neither class nor level based

            var set = LiveDbMainApplier.ExpandForRealItems(new[] { variant }, new List<ItemInfo> { baseItem, variant, other, plain });

            Assert.Contains(variant, set);
            Assert.Contains(baseItem, set);
            Assert.DoesNotContain(other, set);
            Assert.DoesNotContain(plain, set);
        }

        // ------------------------------------------------------------------ push selection / chunking

        private static ItemInfo BigItem(int index, int tooltipChars) =>
            new ItemInfo { Index = index, Name = "Item" + index, ToolTip = new string('x', tooltipChars) };

        [Fact]
        public void Push_SendsOnlyRowsTheConnectionHolds_InOnePacketMarkedLast()
        {
            var a = BigItem(1, 10);
            var b = BigItem(2, 10);
            var c = BigItem(3, 10);
            var hen = new MonsterInfo { Index = 1, Name = "Hen" };
            var deer = new MonsterInfo { Index = 2, Name = "Deer" };
            var changes = new LiveDbPushChanges();
            changes.Items[a] = BigItem(1, 20);
            changes.Items[c] = BigItem(3, 20);
            changes.Monsters[deer] = deer.ClientInformation;
            changes.Spells.Add(Spell.Fencing);
            changes.GameShopRemoved.Add(77);

            var conn = new LiveDbPushConnection
            {
                SentItems = new List<ItemInfo> { a, b },
                SentMonsters = new List<MonsterInfo> { hen },
                Magics = new List<ClientMagic> { new ClientMagic { Spell = Spell.Fencing, Name = "Fencing" }, new ClientMagic { Spell = Spell.Slaying, Name = "Slaying" } },
                ReceivesGameShop = false,
            };

            var packets = LiveDbPush.Build(changes, conn, out var result);

            var p = Assert.Single(packets);
            Assert.True(p.Last);
            Assert.Equal(new[] { 1 }, p.Items.Select(i => i.Index));
            Assert.Same(changes.Items[a], p.Items[0]);          // the snapshot, never the Sent (Main) object
            Assert.Empty(p.Monsters);                           // deer was never sent to this client
            Assert.Equal(new[] { Spell.Fencing }, p.Magics.Select(m => m.Spell));
            Assert.Empty(p.GameShopRemoved);                    // not a player in the game
            Assert.Equal(2, result.Rows);
            Assert.Equal(1, result.Connections);

            var none = LiveDbPush.Build(changes, new LiveDbPushConnection { SentItems = new List<ItemInfo> { b } }, out var empty);
            Assert.Empty(none);
            Assert.Equal(0, empty.Connections);
        }

        [Fact]
        public void Push_ChunksStayUnderTheLimit_AndOnlyTheLastIsMarked()
        {
            var changes = new LiveDbPushChanges();
            var sent = new List<ItemInfo>();
            for (var i = 1; i <= 300; i++)
            {
                var main = BigItem(i, 400);
                sent.Add(main);
                changes.Items[main] = BigItem(i, 400);
            }

            const int limit = 8 * 1024;
            var packets = LiveDbPush.Build(changes, new LiveDbPushConnection { SentItems = sent }, out var result, limit);

            Assert.True(packets.Count > 10);
            Assert.Equal(300, packets.Sum(p => p.Items.Count));
            Assert.Equal(300, result.Rows);
            Assert.Equal(packets.Count, result.Packets);
            for (var i = 0; i < packets.Count; i++)
            {
                var size = LiveDbPush.EncodedSize(packets[i]);
                Assert.True(size <= limit, "packet " + i + " is " + size + " bytes");
                Assert.Equal(i == packets.Count - 1, packets[i].Last);
            }

            Assert.Equal(packets.Sum(LiveDbPush.EncodedSize), result.Bytes);
            Assert.Equal(Enumerable.Range(1, 300), packets.SelectMany(p => p.Items).Select(x => x.Index)); // order kept

            // The default chunk size keeps every packet under the 65 535-byte length field.
            var big = LiveDbPush.Build(changes, new LiveDbPushConnection { SentItems = sent }, out _);
            Assert.True(big.Count >= 2);
            Assert.All(big, p => Assert.True(LiveDbPush.EncodedSize(p) <= LiveDbPush.ChunkBytes));
        }

        [Fact]
        public void Push_SkipsAnElementLargerThanOnePacket()
        {
            var huge = BigItem(1, 70000);
            var small = BigItem(2, 10);
            var changes = new LiveDbPushChanges();
            changes.Items[huge] = huge;
            changes.Items[small] = small;

            var packets = LiveDbPush.Build(changes, new LiveDbPushConnection { SentItems = new List<ItemInfo> { huge, small } }, out var result);

            var p = Assert.Single(packets);
            Assert.Equal(new[] { 2 }, p.Items.Select(i => i.Index));
            Assert.Equal(1, result.Oversized);
            Assert.True(LiveDbPush.EncodedSize(p) <= LiveDbPush.MaxPacketBytes);
        }

        [Fact]
        public void Push_GameShopAndMapSections_FollowTheConnection()
        {
            var bichon = new MapInfo { Index = 5, Title = "Bichon" };
            var other = new MapInfo { Index = 6, Title = "Other" };
            var changes = new LiveDbPushChanges();
            changes.MapsInvalidated.Add(bichon);
            changes.MapsInvalidated.Add(other);
            changes.GameShopRemoved.Add(3);
            changes.GameShop.Add(new ServerPackets.GameDataChanged.ShopEntry { Item = new GameShopItem { GIndex = 4, Info = BigItem(9, 1) }, StockLevel = 2 });

            var packets = LiveDbPush.Build(changes, new LiveDbPushConnection { SentMaps = new List<MapInfo> { bichon }, ReceivesGameShop = true }, out _);

            var p = Assert.Single(packets);
            Assert.Equal(new[] { 5 }, p.MapInfoInvalidated);
            Assert.Equal(new[] { 3 }, p.GameShopRemoved);
            Assert.Equal(4, p.GameShop.Single().Item.GIndex);
            Assert.Equal(2, p.GameShop.Single().StockLevel);
        }

        // ------------------------------------------------------------------ Batch C: world tables

        private static NPCInfo N(GameDataContext ctx, int index) => ctx.Npcs.First(n => n.Index == index);

        private static string Upd(Rig rig, string endpoint, int index, string cells) =>
            "[{\"Index\":" + index + ",\"__rev\":\"" + rig.Rev(endpoint, index) + "\"," + cells + "}]";

        private static LiveDbBatchRequest Batch(string id, params (string Endpoint, string Rows, string Deletes)[] tables)
        {
            var req = new LiveDbBatchRequest { Id = id, Admin = "tester" };
            foreach (var t in tables)
            {
                var ops = new LiveDbTableOps { Endpoint = t.Endpoint };
                if (t.Rows != null) ops.Rows.AddRange(Json(t.Rows).EnumerateArray());
                if (t.Deletes != null) ops.Deletes.AddRange(t.Deletes.Split(',').Select(int.Parse));
                req.Tables.Add(ops);
            }

            return req;
        }

        [Fact]
        public void Npc_Relocate_HandsTheTargetOverWhileTheInfoStillNamesTheOldCell()
        {
            var rig = new Rig();
            var main = N(rig.Main, 1);
            var r = rig.Run("bc-1", "npcs", Upd(rig, "npcs", 1, "\"Location_X\":15,\"Location_Y\":16,\"Rate\":120"));

            Assert.Equal("applied", r.Live.Outcome);
            var move = Assert.Single(rig.Hooks.Relocations);
            Assert.Equal(1, move.OldMap);
            Assert.Equal(new Point(12, 12), move.OldLocation);    // F10: the applier never sets Location before the move
            Assert.Equal(new Point(15, 16), move.NewLocation);
            Assert.Equal(new Point(15, 16), main.Location);
            Assert.Equal(120, main.Rate);
            Assert.All(r.Rows[0].Changes, c => Assert.True(c.Live));
            var push = Assert.Single(rig.Hooks.Pushes);
            Assert.True(push.Npcs.TryGetValue(main, out var snap));
            Assert.Equal(new Point(15, 16), snap.Location);
            Assert.Contains(rig.Main.Maps[0], push.MapsInvalidated); // shown on the big map
        }

        [Fact]
        public void NpcMover_LeavesTheOldCellBeforeTheInfoChanges()
        {
            var info = new NPCInfo { MapIndex = 1, Location = new Point(1, 2) };
            var seen = new List<string>();
            LiveDbNpcMover.Move(info, 3, new Point(7, 8),
                () => seen.Add("remove@" + info.MapIndex + ":" + info.Location.X + "," + info.Location.Y),
                () => seen.Add("add@" + info.MapIndex + ":" + info.Location.X + "," + info.Location.Y));

            Assert.Equal(new[] { "remove@1:1,2", "add@3:7,8" }, seen);
        }

        [Fact]
        public void Npc_RelocateOntoAnUnwalkableCell_RejectsTheWholeBatch()
        {
            var rig = new Rig();
            rig.Hooks.InvalidCells.Add((1, new Point(99, 99)));
            var req = Batch("bc-2", ("npcs", Upd(rig, "npcs", 1, "\"Location_X\":99,\"Location_Y\":99"), null),
                ("monsters", Upd(rig, "monsters", 1, "\"Level\":7"), null));

            var r = new LiveDbEngine(rig.Env).Run(req);

            Assert.Equal("rejected", r.Status);
            Assert.Equal(422, r.HttpStatus);
            Assert.Single(r.Validation);
            Assert.Equal(new Point(12, 12), N(rig.Edit, 1).Location); // the editor copy was restored
            Assert.Equal(1, M(rig.Edit, 1).Level);
            Assert.Equal(0, rig.Persistence.Saves);
            Assert.Empty(rig.Hooks.Relocations);
            Assert.Null(rig.Env.Journal.Find("bc-2"));
        }

        [Fact]
        public void Npc_RelocateToAMapTheWorldHasNotLoaded_IsSavedOnly()
        {
            var rig = new Rig();
            rig.Hooks.UnloadedMaps.Add(2);
            var r = rig.Run("bc-3", "npcs", Upd(rig, "npcs", 1, "\"MapIndex\":2,\"Rate\":90"));

            Assert.Equal("applied", r.Status);
            Assert.Equal(2, N(rig.Edit, 1).MapIndex);
            Assert.Equal(1, N(rig.Main, 1).MapIndex);
            Assert.Equal(90, N(rig.Main, 1).Rate);
            var f = r.Rows[0].Changes.Single(c => c.Field == "MapIndex");
            Assert.False(f.Live);
            Assert.Contains("not loaded", f.Reason);
            Assert.True(r.Rows[0].Changes.Single(c => c.Field == "Rate").Live);
            Assert.Empty(rig.Hooks.Relocations);
            Assert.Equal("partial", r.Live.Outcome);
            Assert.Equal(1, r.Live.RefusedLive);
        }

        [Fact]
        public void Npc_Insert_IsCloned_Spawned_AndItsIndexAllocated()
        {
            var rig = new Rig();
            var r = rig.Run("bc-4", "npcs", "[{\"Index\":-1,\"Name\":\"Guard\",\"FileName\":\"Guard\",\"MapIndex\":1,\"Location_X\":20,\"Location_Y\":21}]");

            Assert.Equal("applied", r.Status);
            var edit = rig.Edit.Npcs.Single(n => n.Name == "Guard");
            var main = rig.Main.Npcs.Single(n => n.Name == "Guard");
            Assert.NotSame(edit, main);
            Assert.NotSame(edit.CollectQuestIndexes, main.CollectQuestIndexes);
            Assert.Equal(new[] { main }, rig.Hooks.Spawned);
            Assert.Contains(("npcs", main.Index), rig.Hooks.Allocated);
            Assert.Contains(main, rig.Main.Maps[0].NPCs);
            Assert.True(r.Rows[0].RowLive);
        }

        [Fact]
        public void Npc_VisibilityLooksAndScript_RunTheirHooks()
        {
            var rig = new Rig();
            var main = N(rig.Main, 1);
            var r = rig.Run("bc-5", "npcs", Upd(rig, "npcs", 1, "\"MinLev\":10,\"Image\":9,\"FileName\":\"Merchant2\""));

            Assert.Equal("applied", r.Live.Outcome);
            Assert.Equal(new[] { main }, rig.Hooks.Visibility);
            Assert.Contains(main, rig.Hooks.Rebroadcast);
            Assert.Equal(new[] { main }, rig.Hooks.Rebound);
            Assert.Equal("Merchant2", main.FileName);
            Assert.Equal(9, main.Image);
        }

        [Fact]
        public void Npc_ScriptThatCannotLoad_RefusesOnlyFileName()
        {
            var rig = new Rig { Hooks = { ThrowOnScript = true } };
            rig.Env.Hooks = rig.Hooks;
            var r = rig.Run("bc-6", "npcs", Upd(rig, "npcs", 1, "\"FileName\":\"Broken\",\"Rate\":80"));

            Assert.Equal("partial", r.Live.Outcome);
            Assert.Equal("Merchant", N(rig.Main, 1).FileName); // the running NPC keeps its script
            Assert.Equal("Broken", N(rig.Edit, 1).FileName);
            Assert.Equal(80, N(rig.Main, 1).Rate);
            var f = r.Rows[0].Changes.Single(c => c.Field == "FileName");
            Assert.False(f.Live);
            Assert.Contains("script", f.Reason);
        }

        [Fact]
        public void Quest_FileName_ReloadsThroughTheHook_AndIsPushed()
        {
            var rig = new Rig();
            var main = rig.Main.Quests[0];
            var r = rig.Run("bc-7", "quests", Upd(rig, "quests", 1, "\"FileName\":\"FirstSteps2\",\"RequiredMaxLevel\":0,\"Name\":\"First Steps\""));

            Assert.Equal("applied", r.Live.Outcome);
            Assert.Equal(new[] { main }, rig.Hooks.QuestsReloaded);
            Assert.Equal("FirstSteps2", main.FileName);
            Assert.Equal(ushort.MaxValue, main.RequiredMaxLevel);  // a saved 0 means "no maximum", as after a restart
            Assert.Equal(0, rig.Edit.Quests[0].RequiredMaxLevel);
            Assert.True(rig.Hooks.Pushes[0].Quests.TryGetValue(main, out var snap));
            Assert.Equal("First Steps", snap.Name);
        }

        [Fact]
        public void Quest_ReloadRefused_KeepsTheRunningQuestFile_OtherFieldsStayLive()
        {
            var rig = new Rig { Hooks = { QuestRefusal = "kill tasks 1 -> 2" } };
            rig.Env.Hooks = rig.Hooks;
            var r = rig.Run("bc-8", "quests", Upd(rig, "quests", 1, "\"FileName\":\"Other\",\"Name\":\"First Steps\""));

            Assert.Equal("partial", r.Live.Outcome);
            Assert.Equal("FirstSteps", rig.Main.Quests[0].FileName);
            Assert.Equal("First Steps", rig.Main.Quests[0].Name);
            Assert.Equal("Other", rig.Edit.Quests[0].FileName);
            var f = r.Rows[0].Changes.Single(c => c.Field == "FileName");
            Assert.False(f.Live);
            Assert.Contains("kill tasks 1 -> 2", f.Reason);
            Assert.True(r.Rows[0].Changes.Single(c => c.Field == "Name").Live);
        }

        [Fact]
        public void QuestReload_RefusesAChangedTaskShape_AndLeavesTheQuestUnchanged()
        {
            var hen = new MonsterInfo { Index = 1, Name = "Hen" };
            var quest = new QuestInfo { Index = 1, Name = "Q", FileName = "Q" };
            quest.KillTasks.Add(new QuestKillTask { Monster = hen, Count = 5 });
            quest.Description.Add("old");
            var tasksBefore = quest.KillTasks;

            var refused = QuestReload.Reload(quest, new List<CharacterInfo>(), q =>
            {
                q.ClearInfo();
                q.Description.Add("new");
                q.KillTasks.Add(new QuestKillTask { Monster = hen, Count = 1 });
                q.KillTasks.Add(new QuestKillTask { Monster = hen, Count = 2 });
            });

            Assert.False(refused.Reloaded);
            Assert.Equal("kill tasks 1 -> 2", refused.Refusal);
            Assert.Same(tasksBefore, quest.KillTasks);
            Assert.Equal(new[] { "old" }, quest.Description);

            var unreadable = QuestReload.Reload(quest, new List<CharacterInfo>(), q => throw new IOException("locked"));
            Assert.False(unreadable.Reloaded);
            Assert.DoesNotContain("locked", unreadable.Refusal); // never exception text
            Assert.Same(tasksBefore, quest.KillTasks);

            var ok = QuestReload.Reload(quest, new List<CharacterInfo>(), q =>
            {
                q.ClearInfo();
                q.KillTasks.Add(new QuestKillTask { Monster = hen, Count = 9 });
            });
            Assert.True(ok.Reloaded);
            Assert.Equal(9, quest.KillTasks[0].Count);
            ok.Undo();
            Assert.Same(tasksBefore, quest.KillTasks);
            Assert.Equal(new[] { "old" }, quest.Description);
        }

        [Fact]
        public void Conquest_AtWar_IsSavedOnly()
        {
            var rig = new Rig { Hooks = { AtWar = true } };
            rig.Env.Hooks = rig.Hooks;
            var r = rig.Run("bc-9", "conquests", Upd(rig, "conquests", 1, "\"StartHour\":21"));

            Assert.Equal("applied", r.Status);
            Assert.Equal(21, rig.Edit.Conquests[0].StartHour);
            Assert.Equal(20, rig.Main.Conquests[0].StartHour);
            var f = r.Rows[0].Changes.Single();
            Assert.False(f.Live);
            Assert.Contains("war", f.Reason);
            Assert.Equal(1, r.Live.RefusedLive);

            rig.Hooks.AtWar = false;
            var r2 = rig.Run("bc-10", "conquests", Upd(rig, "conquests", 1, "\"StartHour\":22"));
            Assert.True(r2.Rows[0].Changes.Single().Live);
            Assert.Equal(22, rig.Main.Conquests[0].StartHour);
        }

        [Fact]
        public void GameShop_Insert_BindsToMainsItem_AndPushesACopy()
        {
            var rig = new Rig();
            var r = rig.Run("bc-11", "gameshop", "[{\"Index\":-1,\"ItemIndex\":1,\"GoldPrice\":900,\"Count\":1,\"Class\":\"All\",\"Category\":\"Weapon\"}]");

            Assert.Equal("applied", r.Status);
            Assert.True(r.Rows[0].RowLive);
            var edit = rig.Edit.GameShop.Single(g => g.GoldPrice == 900);
            var main = rig.Main.GameShop.Single(g => g.GoldPrice == 900);
            Assert.NotSame(edit, main);
            Assert.Same(I(rig.Main, 1), main.Info);              // Main's ItemInfo, never the editor copy's
            Assert.NotSame(I(rig.Edit, 1), main.Info);
            Assert.Contains(("gameshop", main.GIndex), rig.Hooks.Allocated);

            var push = Assert.Single(rig.Hooks.Pushes);
            var entry = Assert.Single(push.GameShop);
            Assert.Same(main, Assert.Single(push.GameShopSources));
            Assert.NotSame(main, entry.Item);                       // a copy for the packet
            Assert.NotSame(main.Info, entry.Item.Info);
            Assert.Equal("WoodenSword", entry.Item.Info.Name);
            Assert.Equal(900u, entry.Item.GoldPrice);
        }

        [Fact]
        public void GameShop_UpdateAndDelete_ArePushed()
        {
            var rig = new Rig();
            var r = rig.Run("bc-12", "gameshop", Upd(rig, "gameshop", 1, "\"GoldPrice\":700"));
            Assert.Equal("applied", r.Live.Outcome);
            Assert.Equal(700u, rig.Main.GameShop[0].GoldPrice);
            Assert.Equal(700u, Assert.Single(rig.Hooks.Pushes[0].GameShop).Item.GoldPrice);

            var r2 = rig.Run("bc-13", "gameshop", null, deletes: "1");
            Assert.True(r2.Rows.Single().RowLive);
            Assert.Empty(rig.Main.GameShop);
            Assert.Equal(new[] { 1 }, rig.Hooks.Pushes[1].GameShopRemoved);
            Assert.Equal(1, r2.Live.DeletedLive);
        }

        [Fact]
        public void Respawn_InsertAndDelete_KeepTheBookkeeping()
        {
            var rig = new Rig();
            var r = rig.Run("bc-14", "maprespawns", "[{\"Index\":-1,\"MapIndex\":1,\"MonsterIndex\":2,\"Location_X\":60,\"Location_Y\":61,\"Count\":4,\"Delay\":3}]");

            Assert.Equal("applied", r.Status);
            Assert.True(r.Rows[0].RowLive);
            var edit = rig.Edit.Maps[0].Respawns.Last();
            var main = rig.Main.Maps[0].Respawns.Last();
            Assert.NotSame(edit, main);
            Assert.Equal(edit.RespawnIndex, main.RespawnIndex);
            Assert.Equal(new[] { main }, rig.Hooks.RespawnsInserted);
            Assert.Contains(("maprespawns", main.RespawnIndex), rig.Hooks.Allocated);

            var r2 = rig.Run("bc-15", "maprespawns", null, deletes: "1");
            Assert.True(r2.Rows.Single().RowLive);
            Assert.DoesNotContain(rig.Main.Maps[0].Respawns, x => x.RespawnIndex == 1);
            Assert.Equal(1, Assert.Single(rig.Hooks.RespawnsDeleted).RespawnIndex);
            Assert.False(rig.Env.State.IsDiverged("maps#1"));    // keyed by RespawnIndex, not position
        }

        [Fact]
        public void Respawn_CacheColumnsRebuild_ReadThroughColumnsDoNot()
        {
            var rig = new Rig();
            rig.Run("bc-16", "maprespawns", Upd(rig, "maprespawns", 1, "\"Count\":9"));
            Assert.Empty(rig.Hooks.RespawnRebuilds);
            Assert.Equal(9, rig.Main.Maps[0].Respawns[0].Count);

            rig.Run("bc-17", "maprespawns", Upd(rig, "maprespawns", 1, "\"MonsterIndex\":2,\"Spread\":4"));
            Assert.Equal(new[] { rig.Main.Maps[0].Respawns[0] }, rig.Hooks.RespawnRebuilds);

            rig.Run("bc-18", "maprespawns", Upd(rig, "maprespawns", 2, "\"SaveRespawnTime\":true,\"RespawnTicks\":3"));
            Assert.Equal(new[] { rig.Main.Maps[0].Respawns[1] }, rig.Hooks.SavedSpawnSyncs);
        }

        [Fact]
        public void Respawn_RollbackPutsADeletedRespawnBackInPlace()
        {
            var rig = new Rig { Hooks = { ThrowOnZones = true } };
            rig.Env.Hooks = rig.Hooks;
            var first = rig.Main.Maps[0].Respawns[0];
            var req = Batch("bc-19", ("maprespawns", null, "1"), ("mapsafezones", Upd(rig, "mapsafezones", 10001, "\"Size\":9"), null));

            var r = new LiveDbEngine(rig.Env).Run(req);

            Assert.Equal("rolled-back", r.Live.Outcome);
            Assert.Same(first, rig.Main.Maps[0].Respawns[0]);
            Assert.Equal(2, rig.Main.Maps[0].Respawns.Count);
            Assert.Equal(1, rig.Hooks.RespawnUndos);             // its MapRespawn came back too
            Assert.Equal(6, rig.Main.Maps[0].SafeZones[1].Size);
        }

        [Fact]
        public void SafeZone_Update_RebuildsFromThePreviousGeometry()
        {
            var rig = new Rig();
            var r = rig.Run("bc-20", "mapsafezones", Upd(rig, "mapsafezones", 10000, "\"Size\":8"));

            Assert.Equal("applied", r.Live.Outcome);
            var rebuild = Assert.Single(rig.Hooks.ZoneRebuilds);
            Assert.Same(rig.Main.Maps[0], rebuild.Map);
            Assert.Equal(5, rebuild.Previous[0].Size);             // the old zone's spells are the ones removed
            Assert.Equal(8, rebuild.Created[0].Size);
            Assert.Equal(new[] { rig.Main.Maps[0] }, rig.Hooks.SafeZoneFlags);
            Assert.Equal(1, rig.Hooks.StartPointRebuilds);        // zone 0 is a start point
        }

        [Fact]
        public void SafeZone_InsertAndDelete_AreLive_AndKeepPositionsInStep()
        {
            var rig = new Rig();
            var r = rig.Run("bc-21", "mapsafezones", "[{\"Index\":-1,\"MapIndex\":1,\"Location_X\":70,\"Location_Y\":70,\"Size\":4}]");
            Assert.True(r.Rows[0].RowLive);
            Assert.Equal(3, rig.Main.Maps[0].SafeZones.Count);
            Assert.Same(rig.Main.Maps[0], rig.Main.Maps[0].SafeZones[2].Info);  // bound to Main's map, not the editor copy's
            Assert.NotSame(rig.Edit.Maps[0].SafeZones[2], rig.Main.Maps[0].SafeZones[2]);

            var r2 = rig.Run("bc-22", "mapsafezones", null, deletes: "10000");
            Assert.True(r2.Rows.Single().RowLive);
            Assert.Equal(2, rig.Main.Maps[0].SafeZones.Count);
            Assert.Equal(new Point(20, 20), rig.Main.Maps[0].SafeZones[0].Location);
            Assert.False(rig.Env.State.IsDiverged("maps#1"));

            // Keys line up again: position 0 is now the zone at (20, 20) in both copies.
            var r3 = rig.Run("bc-23", "mapsafezones", Upd(rig, "mapsafezones", 10000, "\"Size\":9"));
            Assert.True(r3.Rows[0].Changes.Single().Live);
            Assert.Equal(9, rig.Main.Maps[0].SafeZones[0].Size);
            Assert.Equal(new Point(20, 20), rig.Main.Maps[0].SafeZones[0].Location);
        }

        [Fact]
        public void PositionKeyed_InsertAndDeleteInOneBatch_IsLive_AndReportsTheInsertsKeyAfterTheDelete()
        {
            // The Maps editor sends one batch per map: a new zone (appended at position 2) and the delete of position 0.
            var rig = new Rig();
            var r = rig.Run("bc-21b", "mapsafezones", "[{\"Index\":-1,\"MapIndex\":1,\"Location_X\":70,\"Location_Y\":70,\"Size\":4}]", deletes: "10000");

            Assert.Equal("applied", r.Status);
            var ins = r.Rows.Single(x => x.Kind == "insert");
            Assert.True(ins.RowLive, ins.RowReason);
            Assert.Equal(10001, ins.NewIndex);   // position 1 once position 0 is gone
            Assert.NotNull(ins.RevAfter);
            Assert.True(r.Rows.Single(x => x.Kind == "delete").RowLive);

            Assert.Equal(new[] { new Point(20, 20), new Point(70, 70) }, rig.Edit.Maps[0].SafeZones.Select(z => z.Location));
            Assert.Equal(new[] { new Point(20, 20), new Point(70, 70) }, rig.Main.Maps[0].SafeZones.Select(z => z.Location));
            Assert.False(rig.Env.State.IsDiverged("maps#1"));
            Assert.Empty(r.RestartFields);
        }

        [Fact]
        public void PositionKeyedChildren_ThatNoLongerLineUp_AreRefused_AndMarkTheParent()
        {
            var rig = new Rig();
            rig.Main.Maps[0].SafeZones.RemoveAt(0); // e.g. a delete saved without reaching the running world (legacy delete path)

            var r = rig.Run("bc-24", "mapsafezones", Upd(rig, "mapsafezones", 10001, "\"Size\":9"));

            Assert.Equal("applied", r.Status);
            Assert.Equal(9, rig.Edit.Maps[0].SafeZones[1].Size);
            Assert.Equal(6, rig.Main.Maps[0].SafeZones[0].Size);  // never applied to the wrong record
            var f = r.Rows[0].Changes.Single();
            Assert.False(f.Live);
            Assert.Contains("line up", f.Reason);
            Assert.True(rig.Env.State.IsDiverged("maps#1"));
            Assert.Empty(rig.Hooks.ZoneRebuilds);
        }

        [Fact]
        public void PositionKeyedChildren_WithOtherValuesAtThePosition_AreRefused()
        {
            var rig = new Rig();
            rig.Main.Maps[0].SafeZones[1].Size = 99; // the running world's row differs (an earlier live step failed)

            var r = rig.Run("bc-25", "mapsafezones", Upd(rig, "mapsafezones", 10001, "\"StartPoint\":true"));
            Assert.False(rig.Main.Maps[0].SafeZones[1].StartPoint);
            var f = r.Rows[0].Changes.Single();
            Assert.False(f.Live);
            Assert.Contains("different values", f.Reason);
            Assert.False(rig.Env.State.IsDiverged("maps#1"));

            // A delete there is refused too; the lists then differ in length, so the parent is marked diverged.
            var r2 = rig.Run("bc-26", "mapsafezones", null, deletes: "10001");
            Assert.False(r2.Rows.Single().RowLive);
            Assert.Equal(2, rig.Main.Maps[0].SafeZones.Count);
            Assert.True(rig.Env.State.IsDiverged("maps#1"));
        }

        [Fact]
        public void DivergedParent_RefusesInsertsAndDeletesLive_InTheDryRunToo()
        {
            var rig = new Rig();
            rig.Env.State.MarkDiverged("maps#1");
            var dry = new LiveDbEngine(rig.Env).Run(new LiveDbBatchRequest
            {
                Id = "bc-27d", DryRun = true,
                Tables = Batch("x", ("mapsafezones", "[{\"Index\":-1,\"MapIndex\":1,\"Location_X\":70,\"Location_Y\":70,\"Size\":4}]", "10001")).Tables,
            });
            Assert.All(dry.Rows, x => Assert.False(x.RowLive));

            var r = rig.Run("bc-27", "mapsafezones", null, deletes: "10001");
            Assert.False(r.Rows.Single().RowLive);
            Assert.Equal(2, rig.Main.Maps[0].SafeZones.Count);
            Assert.Single(rig.Edit.Maps[0].SafeZones);
        }

        [Fact]
        public void Movements_MineZones_AndMaps_RunTheirHooks()
        {
            var rig = new Rig();
            rig.Run("bc-28", "mapmovements", Upd(rig, "mapmovements", 10000, "\"Icon\":4"));
            Assert.Contains(rig.Main.Maps[0], rig.Hooks.Pushes[0].MapsInvalidated);
            Assert.Equal(4, rig.Main.Maps[0].Movements[0].Icon);

            rig.Run("bc-29", "mapminezones", Upd(rig, "mapminezones", 10000, "\"Size\":5"));
            Assert.Equal(new[] { rig.Main.Maps[0] }, rig.Hooks.Mines);

            rig.Run("bc-30", "maps", Upd(rig, "maps", 1, "\"MineIndex\":2,\"NoTeleport\":true"));
            Assert.Equal(2, rig.Hooks.Mines.Count);
            Assert.True(rig.Main.Maps[0].NoTeleport);
        }

        [Fact]
        public void Maps_TitleInvalidatesItselfAndTheMapsWhoseMovementsShowIt()
        {
            var rig = new Rig();
            var r = rig.Run("bc-31", "maps", Upd(rig, "maps", 2, "\"Title\":\"Deep Mine\""));

            Assert.Equal("applied", r.Live.Outcome);
            Assert.Equal("Deep Mine", rig.Main.Maps[1].Title);
            var push = Assert.Single(rig.Hooks.Pushes);
            Assert.Contains(rig.Main.Maps[1], push.MapsInvalidated);
            Assert.Contains(rig.Main.Maps[0], push.MapsInvalidated); // map 1 shows a movement to map 2 on its big map
        }

        [Fact]
        public void Maps_WeatherAndLightArePushedToThePlayersOnTheMap()
        {
            var rig = new Rig();
            var r = rig.Run("bc-31w", "maps", Upd(rig, "maps", 2, "\"WeatherParticles\":65,\"Light\":4"));

            Assert.Equal("applied", r.Live.Outcome);
            Assert.Equal(WeatherSetting.Fog | WeatherSetting.Rain, rig.Main.Maps[1].WeatherParticles);
            Assert.Equal(new[] { rig.Main.Maps[1] }, rig.Hooks.AtmosphereMaps);

            rig.Run("bc-31m", "maps", Upd(rig, "maps", 2, "\"Music\":7")); // read on entry only: no atmosphere push
            Assert.Single(rig.Hooks.AtmosphereMaps);
        }

        [Fact]
        public void Dragon_DropAreaRecomputes_ExpsReadThrough()
        {
            var rig = new Rig();
            var editDragon = new DragonInfo();
            var mainDragon = new DragonInfo();
            rig.Edit.Dragon = () => editDragon;
            rig.Main.Dragon = () => mainDragon;

            var r = rig.Run("bc-32", "dragon", Upd(rig, "dragon", 1, "\"Exps_2\":55555,\"DropAreaTop_X\":70,\"Enabled\":true"));

            Assert.Equal("partial", r.Live.Outcome);              // Enabled is restart-only
            Assert.Equal(55555, mainDragon.Exps[1]);
            Assert.Equal(70, mainDragon.DropAreaTop.X);
            Assert.False(mainDragon.Enabled);
            Assert.Equal(1, rig.Hooks.DragonAreas);
        }

        [Fact]
        public void Push_UsesTheConnectionsOwnQuestCopy_AndShopStock()
        {
            var quest = new QuestInfo { Index = 1, Name = "Q" };
            var changes = new LiveDbPushChanges();
            changes.Quests[quest] = new ClientQuestInfo { Index = 1, Name = "shared", Group = string.Empty };
            changes.GameShop.Add(new ServerPackets.GameDataChanged.ShopEntry { Item = new GameShopItem { GIndex = 4, Info = BigItem(9, 1), Stock = 5 }, StockLevel = 5 });
            changes.GameShop.Add(new ServerPackets.GameDataChanged.ShopEntry { Item = new GameShopItem { GIndex = 5, Info = BigItem(10, 1), Stock = 1, iStock = true }, StockLevel = 1 });

            var conn = new LiveDbPushConnection
            {
                SentQuests = new List<QuestInfo> { quest },
                ReceivesGameShop = true,
                QuestSnapshots = new Dictionary<QuestInfo, ClientQuestInfo> { [quest] = new ClientQuestInfo { Index = 1, Name = "own", Group = string.Empty } },
                ShopStock = new Dictionary<int, int> { [4] = 2, [5] = -1 },
            };

            var p = Assert.Single(LiveDbPush.Build(changes, conn, out _));
            Assert.Equal("own", p.Quests.Single().Name);
            var entry = Assert.Single(p.GameShop);
            Assert.Equal(4, entry.Item.GIndex);
            Assert.Equal(2, entry.StockLevel);
            Assert.Equal(new[] { 5 }, p.GameShopRemoved);         // sold out for this player: hidden, as at login

            var other = Assert.Single(LiveDbPush.Build(changes, new LiveDbPushConnection { SentQuests = new List<QuestInfo> { quest } }, out _));
            Assert.Equal("shared", other.Quests.Single().Name);
        }

        [Fact]
        public void PushTables_CountNpcsQuestsAndTheShop_AgainstMaxPushRows()
        {
            Assert.True(LiveDbEngine.IsPushTable("npcs"));
            Assert.True(LiveDbEngine.IsPushTable("quests"));
            Assert.True(LiveDbEngine.IsPushTable("gameshop"));
            Assert.False(LiveDbEngine.IsPushTable("maps"));

            var rig = new Rig();
            rig.Env.Options.MaxPushRows = 1;
            var r = new LiveDbEngine(rig.Env).Run(Batch("bc-33", ("npcs", Upd(rig, "npcs", 1, "\"Rate\":70"), null), ("quests", Upd(rig, "quests", 1, "\"Name\":\"Q2\""), null)));
            Assert.True(r.Live.PushSkipped);
            Assert.Equal(70, N(rig.Main, 1).Rate);                 // still applied to the running world
            Assert.Empty(rig.Hooks.Pushes);
        }

        [Fact]
        public void Frontend_DeletesGoThroughTheLiveDbBatch()
        {
            var tools = File.ReadAllText(Path.Combine(AdminDir(), "admin-db-tools.js"));
            Assert.Contains("MirLiveDb.deleteRows(ctx.endpoint, realIdx)", tools);
            var js = File.ReadAllText(Path.Combine(AdminDir(), "admin-gamedata.js"));
            Assert.Contains("deletes: list", js);
            Assert.Contains("deleteRows: function (endpoint, indexes)", js);
        }

        // ------------------------------------------------------------------ classification / schema

        [Fact]
        public void Classification_CoversEveryColumnOfEveryTable()
        {
            var missing = new List<string>();
            foreach (var table in GameDataRegistry.Tables)
            {
                if (LiveDbClassification.Table(table.Endpoint) == null)
                {
                    missing.Add(table.Endpoint + " (table)");
                    continue;
                }

                foreach (var c in table.Columns)
                    if (LiveDbClassification.Planned(table.Endpoint, c.Name) == null) missing.Add(table.Endpoint + "." + c.Name);
            }

            Assert.True(missing.Count == 0, "Unclassified LiveDb columns (add them to LiveDbClassification): " + string.Join(", ", missing));
        }

        [Fact]
        public void Classification_BatchC_WorldTablesAreLive()
        {
            Assert.Equal(new[] { "conquests", "dragon", "gameshop", "items", "magic", "mapminezones", "mapmovements", "maprespawns", "maps", "mapsafezones",
                "monsters", "npcs", "quests" }, LiveDbClassification.LiveTables());
            foreach (var child in new[] { "conquestguards", "conquestgates", "conquestwalls", "conquestsieges", "conquestflags", "conquestcontrolpoints" })
                Assert.False(LiveDbClassification.IsLiveTable(child));
            Assert.Equal(LiveClass.ReadThrough, LiveDbClassification.Effective("npcs", "Rate").Class);
            Assert.Equal(LiveClass.Hooked, LiveDbClassification.Effective("npcs", "Location_X").Class);
            Assert.Equal(LiveClass.RestartOnly, LiveDbClassification.Effective("npcs", "Conquest").Class);
            Assert.Equal(LiveClass.RestartOnly, LiveDbClassification.Effective("quests", "Type").Class);
            Assert.Equal(LiveClass.RestartOnly, LiveDbClassification.Effective("maps", "FileName").Class);
            Assert.Equal(LiveClass.Hooked, LiveDbClassification.Effective("maprespawns", "Spread").Class);
            Assert.Equal(LiveClass.RestartOnly, LiveDbClassification.Effective("conquests", "MapIndex").Class);
            Assert.Equal(LiveClass.RestartOnly, LiveDbClassification.Effective("dragon", "Enabled").Class);
            Assert.Equal(LiveClass.ReadThrough, LiveDbClassification.Effective("dragon", "Exps_3").Class);
            Assert.True(LiveDbClassification.InsertLive("npcs"));
            Assert.False(LiveDbClassification.DeleteLive("npcs"));
            Assert.False(LiveDbClassification.InsertLive("quests"));
            Assert.False(LiveDbClassification.InsertLive("maps"));
            foreach (var ep in new[] { "mapsafezones", "maprespawns", "mapmovements", "mapminezones", "gameshop" })
            {
                Assert.True(LiveDbClassification.InsertLive(ep), ep);
                Assert.True(LiveDbClassification.DeleteLive(ep), ep);
            }
            Assert.Equal(LiveClass.ReadThrough, LiveDbClassification.Effective("items", "Price").Class);
            Assert.Equal(LiveClass.Hooked, LiveDbClassification.Effective("items", "Stat_MaxDC").Class);
            Assert.Equal(LiveClass.Hooked, LiveDbClassification.Effective("items", "Unique").Class);
            Assert.Equal(LiveClass.RestartOnly, LiveDbClassification.Effective("items", "Name").Class);
            Assert.Equal(LiveClass.ReadThrough, LiveDbClassification.Effective("gameshop", "DealGoldPrice").Class); // v119
            Assert.Equal(LiveClass.ReadThrough, LiveDbClassification.Effective("gameshop", "DealCreditPrice").Class);
            Assert.Equal(LiveClass.RestartOnly, LiveDbClassification.Effective("items", "Type").Class);
            Assert.Equal(LiveClass.RestartOnly, LiveDbClassification.Effective("monsters", "Name").Class);
            Assert.Equal(LiveClass.ReadThrough, LiveDbClassification.Effective("monsters", "Level").Class);
            Assert.Equal(LiveClass.RestartOnly, LiveDbClassification.Effective("monsters", "NoSuchColumn").Class);
            Assert.True(LiveDbClassification.InsertLive("monsters"));
            Assert.True(LiveDbClassification.InsertLive("items"));
            Assert.False(LiveDbClassification.DeleteLive("items"));

            var monsters = GameDataRegistry.Get("monsters");
            Assert.True(monsters.Column("Stat_HP").LiveSafe);
            Assert.False(monsters.Column("Name").LiveSafe);
            Assert.True(GameDataRegistry.Get("items").Column("Price").LiveSafe);
            Assert.False(GameDataRegistry.Get("items").Column("Name").LiveSafe);
        }

        /// <summary>Every item column the holder refresh reacts to is classified Hooked (a hook without a class would never run).</summary>
        [Fact]
        public void Classification_HolderRefreshColumns_AreHooked()
        {
            var items = GameDataRegistry.Get("items");
            foreach (var c in items.Columns)
                if (LiveDbMainApplier.NeedsHolderRefresh(c))
                    Assert.Equal(LiveClass.Hooked, LiveDbClassification.Effective("items", c.Name).Class);
        }

        [Fact]
        public void Schema_EmitsLiveClassAndTiming()
        {
            var json = JsonSerializer.Serialize(GameDataSchema.Build(GameDataRegistry.Get("monsters")), WebAdminJson.Options);
            using var doc = JsonDocument.Parse(json);
            var props = doc.RootElement.GetProperty("properties").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString(), p => p);
            Assert.Equal("Hooked", props["Stat_HP"].GetProperty("liveClass").GetString());
            Assert.Equal("hook", props["Stat_HP"].GetProperty("liveTiming").GetString());
            Assert.Equal("RestartOnly", props["Name"].GetProperty("liveClass").GetString());
            Assert.Equal("restart", props["Name"].GetProperty("liveTiming").GetString());
            Assert.True(doc.RootElement.GetProperty("liveTable").GetBoolean());
        }

        // ------------------------------------------------------------------ journal / sweep cursor

        [Fact]
        public void Journal_FindsAnIdInTheFiles_AfterTheLruForgotIt()
        {
            var dir = Path.Combine(Path.GetTempPath(), "mir2-livedb-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var j1 = new LiveDbJournal(dir);
                j1.Append(new LiveDbBatchResult { Id = "b-x", Status = "applied", Utc = DateTime.UtcNow, Applied = 3, Saved = true });
                Assert.True(File.Exists(j1.FilePath(DateTime.UtcNow)));

                var j2 = new LiveDbJournal(dir); // a fresh process: empty LRU
                var found = j2.Find("b-x");
                Assert.NotNull(found);
                Assert.Equal(3, found.Applied);
                Assert.Null(j2.Find("b-y"));
                Assert.Single(j2.Recent(10));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void Journal_LruKeepsTheLast500Ids()
        {
            var j = new LiveDbJournal(null);
            for (var i = 0; i < LiveDbJournal.LruCapacity + 10; i++) j.Append(new LiveDbBatchResult { Id = "b-" + i, Status = "applied" });
            Assert.Null(j.Find("b-0"));
            Assert.NotNull(j.Find("b-" + (LiveDbJournal.LruCapacity + 9)));
        }

        [Fact]
        public void SweepCursor_IsBudgeted_AndRestartsWhenItsNodeWasRemoved()
        {
            var list = new LinkedList<int>(Enumerable.Range(1, 10));
            var cursor = new LiveDbSweepCursor<int>();
            var seen = new List<int>();
            cursor.Restart(list);

            Assert.True(cursor.Step(3, seen.Add));
            Assert.Equal(new[] { 1, 2, 3 }, seen);

            list.Remove(4); // the next node vanished: start over (visits are idempotent)
            Assert.True(cursor.Step(3, seen.Add));
            Assert.Equal(new[] { 1, 2, 3, 1, 2, 3 }, seen);

            while (cursor.Step(4, seen.Add)) { }
            Assert.False(cursor.Active);
            Assert.Contains(10, seen);
        }

        // ------------------------------------------------------------------ frontend contract

        private static string AdminDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Server.MirForms", "wwwroot", "admin");
                if (File.Exists(Path.Combine(candidate, "admin-gamedata.js"))) return candidate;
                dir = dir.Parent;
            }

            throw new InvalidOperationException("Server.MirForms\\wwwroot\\admin not found above " + AppContext.BaseDirectory);
        }

        [Fact]
        public void Frontend_SendsOneBatchOfChangedCells_WithAReusedId()
        {
            var js = File.ReadAllText(Path.Combine(AdminDir(), "admin-gamedata.js"));
            Assert.Contains("'/api/admin/livedb/status'", js);
            Assert.Contains("'/api/admin/livedb/batch'", js);
            Assert.Contains("'?dryRun=1'", js);
            Assert.Contains("flushUnsaved", js);
            Assert.Contains("function batchIdFor(", js);
            Assert.Contains("__rev: r.__rev", js);
            Assert.Contains("if (liveDbEnabled()) return persistBatch(endpoint, rows, grid);", js);
            Assert.Contains("gd-live-badge", js);
            Assert.DoesNotMatch(new Regex(@"(?<![\w.$])fetch\s*\("), js);
        }

        [Fact]
        public void Frontend_GridHeadersCarryLiveBadges()
        {
            var js = File.ReadAllText(Path.Combine(AdminDir(), "admin-grid-factory.js"));
            Assert.Contains("function decorateLiveHeaders(", js);
            Assert.Contains("'gd-h-restart'", js);
            Assert.Contains("'gd-h-live'", js);
            Assert.Contains("p.liveClass", js);
            var css = File.ReadAllText(Path.Combine(AdminDir(), "admin-gamedata.css"));
            Assert.Contains(".gd-h-live", css);
            Assert.Contains(".gd-h-restart", css);
        }
    }

    /// <summary>LiveDb pieces that touch process-wide statics (routes register hooks; the backup policy is static).</summary>
    [Collection(WebAdminGlobalsCollection.Name)]
    public class LiveDbGlobalsTests
    {
        [Theory]
        [InlineData("GET", "/api/admin/livedb/status", "/api/admin/livedb/status")]
        [InlineData("GET", "/api/admin/livedb/schema/monsters", "/api/admin/livedb/schema/{endpoint}")]
        [InlineData("POST", "/api/admin/livedb/batch", "/api/admin/livedb/batch")]
        [InlineData("GET", "/api/admin/livedb/history", "/api/admin/livedb/history")]
        public void Routes_AreRegistered(string method, string path, string pattern)
        {
            var router = new WebAdminRouter();
            new Server.WebAdmin.Routes.LiveDbRoutes().Register(router);
            var match = router.Match(method, path);
            Assert.True(match.Success, method + " " + path);
            Assert.Equal(pattern, match.Route.Pattern);
        }

        [Fact]
        public void BatchRoute_IsAdminOnly_AndMutating()
        {
            var router = new WebAdminRouter();
            new Server.WebAdmin.Routes.LiveDbRoutes().Register(router);
            var route = router.Routes.Single(r => r.Method == "POST" && r.Pattern == "/api/admin/livedb/batch");
            Assert.Equal(RouteAuth.Admin, route.Auth);
        }

        [Fact]
        public void BackupPolicy_TimestampedAtMostEveryN_RollingInBetween()
        {
            LiveDbBackupPolicy.ResetForTests();
            try
            {
                var t0 = new DateTime(2026, 10, 10, 12, 0, 0);
                var first = LiveDbBackupPolicy.Next(t0, 10);
                Assert.Equal("Database 2026-10-10 12-00-00.bak", first);
                LiveDbBackupPolicy.Saved(first, t0);

                var soon = LiveDbBackupPolicy.Next(t0.AddMinutes(3), 10);
                Assert.Equal(LiveDbBackupPolicy.RollingName, soon);
                LiveDbBackupPolicy.Saved(soon, t0.AddMinutes(3));

                Assert.Equal("Database 2026-10-10 12-10-00.bak", LiveDbBackupPolicy.Next(t0.AddMinutes(10), 10));
                Assert.Equal("Database 2026-10-10 12-04-00.bak", LiveDbBackupPolicy.Next(t0.AddMinutes(4), 0));
            }
            finally
            {
                LiveDbBackupPolicy.ResetForTests();
            }
        }
    }
}

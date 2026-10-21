using System.Drawing;
using System.Globalization;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirNetwork;
using Server.MirObjects;
using Server.WebAdmin.GameData;
using S = ServerPackets;

namespace Server.WebAdmin.LiveDb
{
    /// <summary>
    /// Production live-step hooks (game thread only, inside the LiveDb admin action):
    /// <list type="bullet">
    /// <item>monster stats / speeds: queue the budgeted <see cref="LiveDbSweeper"/> (RefreshAll + HP clamp, no scaling);</item>
    /// <item>monster DropPath (and new monsters): reload that monster's drop file into a new list and swap it in (file IO, operator-triggered
    /// like Envir.ReloadDrops; creates a template file when missing, exactly like a world start);</item>
    /// <item>new monsters: keep Main's MonsterIndex counter at least as high as the clone's Index.</item>
    /// <item>items: RefreshStats for online holders (players + spawned heroes, equipped / bag / socketed), RandomStats re-resolve,
    /// StartItems rebuild, ItemIndex counter for new items;</item>
    /// <item>client push: <see cref="LiveDbPush"/> per connection under lock(Connections), only rows in that connection's Sent*Info.</item>
    /// </list>
    /// Magic uses <see cref="GameDataMagicSync.CopyInto"/> inside <see cref="LiveDbMainApplier"/>.
    /// </summary>
    internal sealed class LiveDbWorldHooks : ILiveDbHooks
    {
        public List<DropInfo> ReloadDrops(MonsterInfo mainInfo)
        {
            var list = new List<DropInfo>();
            DropInfo.LoadForMonster(list, mainInfo); // its own list by Index, or its DropPath list (drop tables, phase 2)
            var previous = mainInfo.Drops;
            mainInfo.Drops = list;
            return previous;
        }

        public void MonsterInserted(MonsterInfo mainClone)
        {
            var main = Envir.Main;
            if (main.MonsterIndex < mainClone.Index) main.MonsterIndex = mainClone.Index;
        }

        public void QueueMonsterRefresh(ICollection<MonsterInfo> mainInfos)
        {
            LiveDbSweeper.Enqueue(mainInfos, Envir.Main);
        }

        public void ItemInserted(ItemInfo mainClone)
        {
            var main = Envir.Main;
            if (main.ItemIndex < mainClone.Index) main.ItemIndex = mainClone.Index;
        }

        /// <summary>The same rule as LoadDB: an id past the end of Settings.RandomItemStatsList means no random stats.</summary>
        public RandomItemStat ResolveRandomStats(ItemInfo mainInfo)
        {
            var previous = mainInfo.RandomStats;
            var list = Settings.RandomItemStatsList;
            mainInfo.RandomStats = list != null && mainInfo.RandomStatsId < list.Count ? list[mainInfo.RandomStatsId] : null;
            return previous;
        }

        public List<ItemInfo> RebuildStartItems()
        {
            var main = Envir.Main;
            var previous = new List<ItemInfo>(main.StartItems);
            main.StartItems.Clear();
            for (var i = 0; i < main.ItemInfoList.Count; i++)
                if (main.ItemInfoList[i] != null && main.ItemInfoList[i].StartItem) main.StartItems.Add(main.ItemInfoList[i]);
            return previous;
        }

        public void RestoreStartItems(List<ItemInfo> previous)
        {
            var main = Envir.Main;
            main.StartItems.Clear();
            main.StartItems.AddRange(previous);
        }

        public int RefreshItemHolders(ICollection<ItemInfo> mainInfos)
        {
            var main = Envir.Main;
            var set = LiveDbMainApplier.ExpandForRealItems(mainInfos, main.ItemInfoList);
            if (set.Count == 0) return 0;

            var refreshed = 0;
            for (var i = main.Players.Count - 1; i >= 0; i--)
            {
                var player = main.Players[i];
                if (player == null) continue;
                if (Holds(player.Info, set)) refreshed += Refresh(player);

                var hero = player.Hero;
                if (hero != null && Holds(hero.Info, set)) refreshed += Refresh(hero);
            }

            return refreshed;
        }

        private static bool Holds(CharacterInfo info, HashSet<ItemInfo> set)
        {
            if (info == null) return false;
            return HoldsAny(info.Equipment, set) || HoldsAny(info.Inventory, set);
        }

        private static bool HoldsAny(UserItem[] items, HashSet<ItemInfo> set)
        {
            if (items == null) return false;
            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (item == null) continue;
                if (item.Info != null && set.Contains(item.Info)) return true;
                if (item.Slots == null) continue;
                for (var s = 0; s < item.Slots.Length; s++)
                    if (item.Slots[s]?.Info != null && set.Contains(item.Slots[s].Info)) return true;
            }

            return false;
        }

        private static int Refresh(HumanObject human)
        {
            try
            {
                human.RefreshStats(); // also re-broadcasts looks / light when they changed
                return 1;
            }
            catch (Exception ex)
            {
                // One holder must not undo the batch or stop the world.
                WorldErrors.Record("LiveDb item refresh " + WorldErrors.Describe(human), ex);
                return 0;
            }
        }

        // ================================================================== Batch C: world tables (game thread, plain loops)

        private static Map FindMap(int mapIndex)
        {
            var maps = Envir.Main.MapList;
            for (var i = 0; i < maps.Count; i++)
                if (maps[i]?.Info != null && maps[i].Info.Index == mapIndex) return maps[i];
            return null;
        }

        private static MapInfo FindMapInfo(int mapIndex)
        {
            var infos = Envir.Main.MapInfoList;
            for (var i = 0; i < infos.Count; i++)
                if (infos[i] != null && infos[i].Index == mapIndex) return infos[i];
            return null;
        }

        private static NPCObject FindNpcObject(NPCInfo info)
        {
            var npcs = Envir.Main.NPCs;
            for (var i = 0; i < npcs.Count; i++)
                if (npcs[i] != null && npcs[i].Info == info) return npcs[i];
            return null;
        }

        private static MapRespawn FindRespawn(Map map, RespawnInfo info)
        {
            for (var i = 0; i < map.Respawns.Count; i++)
                if (map.Respawns[i]?.Info == info) return map.Respawns[i];
            return null;
        }

        public LiveDbPlacement CheckNpcPlacement(int mapIndex, Point location)
        {
            var map = FindMap(mapIndex);
            if (map == null) return LiveDbPlacement.MapNotLoaded;
            return map.ValidPoint(location) ? LiveDbPlacement.Valid : LiveDbPlacement.Invalid;
        }

        public void RelocateNpc(NPCInfo mainInfo, int mapIndex, Point location)
        {
            var npc = FindNpcObject(mainInfo);
            var oldInfo = FindMapInfo(mainInfo.MapIndex);
            var newInfo = FindMapInfo(mapIndex);

            if (npc == null || npc.CurrentMap == null)
            {
                // Not placed at world start (map not loaded / invalid cell): only the record moves.
                mainInfo.MapIndex = mapIndex;
                mainInfo.Location = location;
            }
            else
            {
                var target = FindMap(mapIndex) ?? throw new InvalidOperationException("map " + mapIndex + " is not loaded");
                if (!target.ValidPoint(location)) throw new InvalidOperationException("the target cell is not walkable");

                LiveDbNpcMover.Move(mainInfo, mapIndex, location,
                    () =>
                    {
                        // NPCObject.CurrentLocation reads Info.Location: still the old cell here (F10).
                        npc.CurrentMap.Broadcast(new S.ObjectRemove { ObjectID = npc.ObjectID }, npc.CurrentLocation);
                        npc.CurrentMap.RemoveObject(npc);
                    },
                    () =>
                    {
                        npc.CurrentMap = target;
                        target.AddObject(npc);
                        npc.VisibleLog.Clear();
                        if (npc.Visible) ShowToNearby(npc);
                    });
            }

            if (oldInfo != newInfo)
            {
                oldInfo?.NPCs.Remove(mainInfo);
                if (newInfo != null && !newInfo.NPCs.Contains(mainInfo)) newInfo.NPCs.Add(mainInfo);
            }
        }

        private static void ShowToNearby(NPCObject npc)
        {
            var map = npc.CurrentMap;
            for (var i = map.Players.Count - 1; i >= 0; i--)
            {
                var player = map.Players[i];
                if (player == null || !Functions.InRange(npc.CurrentLocation, player.CurrentLocation, Globals.DataRange)) continue;
                try
                {
                    npc.CheckVisible(player, true);
                }
                catch (Exception ex)
                {
                    WorldErrors.Record("LiveDb NPC show " + WorldErrors.Describe(player), ex);
                }
            }
        }

        public bool SpawnNpc(NPCInfo mainClone)
        {
            var map = FindMap(mainClone.MapIndex);
            if (map == null || !map.ValidPoint(mainClone.Location)) return false;

            NPCObject npc = null;
            try
            {
                npc = new NPCObject(mainClone) { CurrentMap = map }; // as Map.Load does
                map.AddObject(npc);
            }
            catch (Exception)
            {
                // The constructor registers and spawns the object before it loads the script: take it out again.
                var orphan = npc ?? FindNpcObject(mainClone);
                if (orphan != null) RemoveNpcObject(orphan, map);
                throw;
            }

            if (npc.Visible) ShowToNearby(npc);
            return true;
        }

        public void RemoveNpc(NPCInfo mainInfo)
        {
            var npc = FindNpcObject(mainInfo);
            if (npc != null) RemoveNpcObject(npc, npc.CurrentMap);
        }

        private static void RemoveNpcObject(NPCObject npc, Map map)
        {
            if (npc.CurrentMap == null) npc.CurrentMap = map;
            if (npc.CurrentMap != null && npc.CurrentMap.NPCs.Contains(npc)) npc.CurrentMap.RemoveObject(npc);
            if (npc.Node != null) npc.Despawn();
            Envir.Main.NPCs.Remove(npc);
        }

        public Action RebindNpcScript(NPCInfo mainInfo)
        {
            var npc = FindNpcObject(mainInfo);
            if (npc == null) return null; // not placed: the new file is used when it is placed (restart)

            var script = npc.ScriptID;
            var quests = new List<QuestInfo>(npc.Quests);
            var speech = new List<NPCSpeech>(npc.Speech);
            var goods = new List<UserItem>(npc.UsedGoods);

            npc.Quests.Clear();  // the new script adds its own ([QUESTS] / [SPEECH] append)
            npc.Speech.Clear();
            try
            {
                npc.ReloadScript();
            }
            catch (Exception)
            {
                Restore();
                throw;
            }

            // LoadGoods re-reads the goods file: keep the in-memory used goods (unsaved sales must not be lost).
            npc.UsedGoods.Clear();
            npc.UsedGoods.AddRange(goods);
            return Restore;

            void Restore()
            {
                npc.ScriptID = script;
                npc.Quests.Clear();
                npc.Quests.AddRange(quests);
                npc.Speech.Clear();
                npc.Speech.AddRange(speech);
                npc.UsedGoods.Clear();
                npc.UsedGoods.AddRange(goods);
            }
        }

        public void RefreshNpcs(ICollection<NPCInfo> visibility, ICollection<NPCInfo> rebroadcast)
        {
            var npcs = Envir.Main.NPCs;
            var now = Envir.Main.Now;
            for (var i = npcs.Count - 1; i >= 0; i--)
            {
                var npc = npcs[i];
                if (npc?.Info == null || npc.CurrentMap == null) continue;
                var redraw = rebroadcast != null && rebroadcast.Contains(npc.Info);
                var recheck = visibility != null && visibility.Contains(npc.Info);
                if (!redraw && !recheck) continue;

                try
                {
                    if (recheck)
                    {
                        // The schedule as a world start applies it (visible unless the day / time window excludes it now).
                        var shown = LiveDbNpcMover.ScheduleVisible(npc.Info, now);
                        if (shown && !npc.Visible) npc.Show();
                        else if (!shown && npc.Visible) npc.Hide();
                    }

                    if (redraw && npc.Visible)
                    {
                        npc.CurrentMap.Broadcast(new S.ObjectRemove { ObjectID = npc.ObjectID }, npc.CurrentLocation);
                        npc.VisibleLog.Clear();
                    }

                    if (!npc.Visible) continue;
                    var map = npc.CurrentMap;
                    for (var p = map.Players.Count - 1; p >= 0; p--)
                    {
                        var player = map.Players[p];
                        if (player == null || !Functions.InRange(npc.CurrentLocation, player.CurrentLocation, Globals.DataRange)) continue;
                        npc.CheckVisible(player); // per-player conditions (level, class, flag)
                    }
                }
                catch (Exception ex)
                {
                    WorldErrors.Record("LiveDb NPC refresh " + WorldErrors.Describe(npc), ex);
                }
            }
        }

        public QuestReload.Outcome ReloadQuest(QuestInfo mainInfo) => QuestReload.Reload(mainInfo, Envir.Main.CharacterList);

        public Action RebuildMine(MapInfo mainMap)
        {
            var map = FindMap(mainMap.Index);
            if (map == null) return null;
            var previous = map.Mine;
            try
            {
                map.RebuildMine();
            }
            catch (Exception)
            {
                map.Mine = previous;
                throw;
            }

            return () => map.Mine = previous;
        }

        public List<LiveDbZone> RebuildSafeZones(MapInfo mainMap, List<LiveDbZone> previous)
        {
            var created = LiveDbMainApplier.Geometry(mainMap);
            var map = FindMap(mainMap.Index);
            if (map == null) return created;

            if (previous != null)
                for (var i = 0; i < previous.Count; i++)
                    map.RemoveSafeZoneSpells(previous[i].Location, previous[i].Size);

            for (var i = 0; i < mainMap.SafeZones.Count; i++)
                if (mainMap.SafeZones[i] != null) map.CreateSafeZoneSpells(mainMap.SafeZones[i]);

            return created;
        }

        public List<SafeZoneInfo> RebuildStartPoints()
        {
            var main = Envir.Main;
            var previous = new List<SafeZoneInfo>(main.StartPoints);
            main.StartPoints.Clear();
            for (var m = 0; m < main.MapList.Count; m++) // the order MapInfo.CreateMap adds them in
            {
                var info = main.MapList[m]?.Info;
                if (info == null) continue;
                for (var i = 0; i < info.SafeZones.Count; i++)
                    if (info.SafeZones[i] != null && info.SafeZones[i].StartPoint) main.StartPoints.Add(info.SafeZones[i]);
            }

            return previous;
        }

        public void RestoreStartPoints(List<SafeZoneInfo> previous)
        {
            var main = Envir.Main;
            main.StartPoints.Clear();
            if (previous != null) main.StartPoints.AddRange(previous);
        }

        public void RecomputeSafeZoneFlags(MapInfo mainMap)
        {
            var map = FindMap(mainMap.Index);
            if (map == null) return;

            for (var i = map.Players.Count - 1; i >= 0; i--)
            {
                var player = map.Players[i];
                if (player == null) continue;
                SetSafeZone(player, map);
                for (var p = player.Pets.Count - 1; p >= 0; p--)
                    if (player.Pets[p]?.CurrentMap == map) SetSafeZone(player.Pets[p], map);
            }

            for (var i = map.Heroes.Count - 1; i >= 0; i--)
                if (map.Heroes[i] != null) SetSafeZone(map.Heroes[i], map);
        }

        private static void SetSafeZone(MapObject ob, Map map)
        {
            try
            {
                ob.InSafeZone = map.GetSafeZone(ob.CurrentLocation) != null;
            }
            catch (Exception ex)
            {
                WorldErrors.Record("LiveDb safe-zone flag " + WorldErrors.Describe(ob), ex);
            }
        }

        public Action RebuildRespawn(MapInfo mainMap, RespawnInfo mainInfo)
        {
            var map = mainMap == null ? null : FindMap(mainMap.Index);
            if (map == null) return null;

            var main = Envir.Main;
            var monster = main.GetMonsterInfo(mainInfo.MonsterIndex);
            var respawn = FindRespawn(map, mainInfo);

            if (respawn == null)
            {
                // Skipped at world start (no such monster then): created now when the monster exists.
                return monster == null ? null : AddRespawn(map, mainInfo);
            }

            if (monster == null)
            {
                // As at world start: a respawn without a monster is not kept (its spawned monsters stay).
                var at = map.Respawns.IndexOf(respawn);
                map.Respawns.RemoveAt(at);
                var wasSaved = main.SavedSpawns.Remove(respawn);
                return () =>
                {
                    map.Respawns.Insert(Math.Min(at, map.Respawns.Count), respawn);
                    if (wasSaved && !main.SavedSpawns.Contains(respawn)) main.SavedSpawns.Add(respawn);
                };
            }

            var oldMonster = respawn.Monster;
            var oldCells = respawn.WalkableCells;
            var oldRoute = respawn.Route;
            try
            {
                respawn.LoadRoutes(); // a new list (file read)
                respawn.WalkableCells = map.BuildRespawnCells(mainInfo);
                respawn.Monster = monster;
            }
            catch (Exception)
            {
                respawn.Monster = oldMonster;
                respawn.WalkableCells = oldCells;
                respawn.Route = oldRoute;
                throw;
            }

            return () =>
            {
                respawn.Monster = oldMonster;
                respawn.WalkableCells = oldCells;
                respawn.Route = oldRoute;
            };
        }

        private static Action AddRespawn(Map map, RespawnInfo info)
        {
            var main = Envir.Main;
            var respawn = new MapRespawn(info) { Map = map }; // loads the route file
            if (respawn.Monster == null) return null;
            respawn.WalkableCells = map.BuildRespawnCells(info);
            map.Respawns.Add(respawn);
            var saved = info.SaveRespawnTime && info.RespawnTicks != 0;
            if (saved) main.SavedSpawns.Add(respawn);
            return () =>
            {
                map.Respawns.Remove(respawn);
                if (saved) main.SavedSpawns.Remove(respawn);
            };
        }

        public Action RespawnInserted(MapInfo mainMap, RespawnInfo mainClone)
        {
            var map = FindMap(mainMap.Index);
            return map == null ? null : AddRespawn(map, mainClone);
        }

        public Action RespawnDeleted(MapInfo mainMap, RespawnInfo mainInfo)
        {
            var map = mainMap == null ? null : FindMap(mainMap.Index);
            if (map == null) return null;
            var respawn = FindRespawn(map, mainInfo);
            if (respawn == null) return null;

            var main = Envir.Main;
            var at = map.Respawns.IndexOf(respawn);
            map.Respawns.RemoveAt(at);
            var savedAt = main.SavedSpawns.IndexOf(respawn);
            if (savedAt >= 0) main.SavedSpawns.RemoveAt(savedAt);
            return () =>
            {
                map.Respawns.Insert(Math.Min(at, map.Respawns.Count), respawn);
                if (savedAt >= 0) main.SavedSpawns.Insert(Math.Min(savedAt, main.SavedSpawns.Count), respawn);
            };
        }

        public Action SyncSavedSpawn(MapInfo mainMap, RespawnInfo mainInfo)
        {
            var map = mainMap == null ? null : FindMap(mainMap.Index);
            if (map == null) return null;
            var respawn = FindRespawn(map, mainInfo);
            if (respawn == null) return null;

            var main = Envir.Main;
            var want = mainInfo.SaveRespawnTime && mainInfo.RespawnTicks != 0;
            var has = main.SavedSpawns.Contains(respawn);
            if (want == has) return null;
            if (want) main.SavedSpawns.Add(respawn);
            else main.SavedSpawns.Remove(respawn);
            return () =>
            {
                if (want) main.SavedSpawns.Remove(respawn);
                else if (!main.SavedSpawns.Contains(respawn)) main.SavedSpawns.Add(respawn);
            };
        }

        public bool ConquestAtWar(ConquestInfo mainInfo)
        {
            var conquests = Envir.Main.Conquests;
            for (var i = 0; i < conquests.Count; i++)
                if (conquests[i] != null && conquests[i].Info == mainInfo && conquests[i].WarIsOn) return true;
            return false;
        }

        public Action DragonDropAreaChanged()
        {
            var dragon = Envir.Main.DragonSystem;
            if (dragon == null || !dragon.Loaded) return null;
            var previous = dragon.RefreshDropArea();
            return () => dragon.RestoreDropArea(previous);
        }

        public void IndexAllocated(string endpoint, int index)
        {
            var main = Envir.Main;
            switch (endpoint)
            {
                case "npcs":
                    if (main.NPCIndex < index) main.NPCIndex = index;
                    break;
                case "gameshop":
                    if (main.GameshopIndex < index) main.GameshopIndex = index;
                    break;
                case "maprespawns":
                    if (main.RespawnIndex < index) main.RespawnIndex = index;
                    break;
            }
        }

        public int PushMapAtmosphere(ICollection<MapInfo> mainMaps)
        {
            if (mainMaps == null || mainMaps.Count == 0) return 0;
            var sent = 0;
            var main = Envir.Main;
            lock (main.Connections) // the accept callback adds under this lock (server.md 3.2)
            {
                for (var i = main.Connections.Count - 1; i >= 0; i--)
                {
                    var c = main.Connections[i];
                    if (c == null || c.Disconnecting) continue;

                    PlayerObject player = c.Stage == GameStage.Game ? c.Player : c.Stage == GameStage.Observer ? c.Observing?.Player : null;
                    var info = player?.CurrentMap?.Info;
                    if (info == null || !mainMaps.Contains(info)) continue;

                    c.Enqueue(new S.MapAtmosphere { Lights = info.Light, MapDarkLight = info.MapDarkLight, Weather = info.WeatherParticles });
                    sent++;
                }
            }

            return sent;
        }

        public LiveDbPushResult Push(LiveDbPushChanges changes)
        {
            var total = new LiveDbPushResult();
            if (changes == null || changes.IsEmpty) return total;

            var main = Envir.Main;
            lock (main.Connections) // the accept callback adds under this lock (server.md 3.2)
            {
                for (var i = main.Connections.Count - 1; i >= 0; i--)
                {
                    var c = main.Connections[i];
                    if (c == null || c.Disconnecting) continue;

                    PlayerObject player = c.Stage == GameStage.Game ? c.Player : c.Stage == GameStage.Observer ? c.Observing?.Player : null;
                    if (player == null) continue;

                    var snapshot = new LiveDbPushConnection
                    {
                        SentItems = c.SentItemInfo,
                        SentMonsters = c.SentMonsterInfo,
                        SentNpcs = c.SentNPCInfo,
                        SentQuests = c.SentQuestInfo,
                        SentMaps = c.SentMapInfo,
                        Magics = MagicsOf(player.Info, changes.Spells),
                        HeroMagics = player.Hero != null ? MagicsOf(player.Hero.Info, changes.Spells) : null,
                        ReceivesGameShop = c.Stage == GameStage.Game,
                        QuestSnapshots = QuestsFor(c, player, changes),
                        ShopStock = c.Stage == GameStage.Game ? ShopStockFor(player, changes) : null,
                    };

                    var packets = LiveDbPush.Build(changes, snapshot, out var result);
                    for (var p = 0; p < packets.Count; p++) c.Enqueue(packets[p]);

                    // An invalidated map is sent again when the client asks for it (C.RequestMapInfo → CheckMapInfo).
                    if (changes.MapsInvalidated.Count > 0)
                        for (var m = c.SentMapInfo.Count - 1; m >= 0; m--)
                            if (changes.MapsInvalidated.Contains(c.SentMapInfo[m])) c.SentMapInfo.RemoveAt(m);

                    total.Add(result);
                }
            }

            if (total.Oversized > 0)
                WebAdminLog.Info("[LiveDb] push: " + total.Oversized + " row(s) larger than one packet were not sent; clients get them after a relog");
            return total;
        }

        /// <summary>
        /// The connection's own copies of the changed quests it holds: CreateClientQuestInfo(player) sends the item / monster / NPC
        /// infos the descriptions link to before the push packet is queued (as S.NewQuestInfo does). Null when none.
        /// </summary>
        private static Dictionary<QuestInfo, ClientQuestInfo> QuestsFor(MirConnection c, PlayerObject player, LiveDbPushChanges changes)
        {
            if (changes.Quests.Count == 0 || c.SentQuestInfo == null) return null;
            Dictionary<QuestInfo, ClientQuestInfo> own = null;
            for (var i = 0; i < c.SentQuestInfo.Count; i++)
            {
                var quest = c.SentQuestInfo[i];
                if (quest == null || !changes.Quests.ContainsKey(quest)) continue;
                (own ??= new Dictionary<QuestInfo, ClientQuestInfo>())[quest] = LiveDbMainApplier.CopyQuest(quest.CreateClientQuestInfo(player));
            }

            return own;
        }

        /// <summary>The player's stock level per changed game-shop row, exactly as PlayerObject.GetGameShop computes it (−1 = hidden).</summary>
        private static Dictionary<int, int> ShopStockFor(PlayerObject player, LiveDbPushChanges changes)
        {
            if (changes.GameShopSources.Count == 0) return null;
            var main = Envir.Main;
            var stock = new Dictionary<int, int>();
            for (var i = 0; i < changes.GameShopSources.Count; i++)
            {
                var item = changes.GameShopSources[i];
                if (item == null) continue;
                if (item.Stock == 0)
                {
                    stock[item.GIndex] = 0;
                    continue;
                }

                int purchased;
                if (item.iStock) player.Info.GSpurchases.TryGetValue(item.GIndex, out purchased);
                else main.GameshopLog.TryGetValue(item.GIndex, out purchased);
                var level = item.Stock - purchased;
                stock[item.GIndex] = level >= 0 ? level : -1;
            }

            return stock;
        }

        private static List<ClientMagic> MagicsOf(CharacterInfo info, HashSet<Spell> spells)
        {
            if (info?.Magics == null || spells.Count == 0) return null;
            List<ClientMagic> list = null;
            for (var i = 0; i < info.Magics.Count; i++)
            {
                var magic = info.Magics[i];
                if (magic?.Info == null || !spells.Contains(magic.Spell)) continue;
                (list ??= new List<ClientMagic>()).Add(magic.CreateClientMagic());
            }

            return list;
        }
    }

    /// <summary>
    /// Backup names for editor-copy saves (finding F3): Envir.Edit's clock never advances, so every Edit save used to overwrite the same
    /// "Database &lt;start time&gt;.bak". Now a timestamped backup (real clock) is kept at most every <c>[LiveDb] BackupEveryMinutes</c>; saves
    /// in between replace one rolling <c>Database livedb-previous.bak</c>. 0 = a timestamped backup on every save. Call under EditLock.
    /// </summary>
    internal static class LiveDbBackupPolicy
    {
        public const string RollingName = "Database livedb-previous.bak";

        private static readonly object Gate = new object();
        private static DateTime? _lastTimestamped;

        public static string TimestampedName(DateTime local) =>
            "Database " + local.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + ".bak";

        /// <summary>The backup file name for a save at <paramref name="localNow"/>.</summary>
        public static string Next(DateTime localNow, int everyMinutes)
        {
            lock (Gate)
            {
                if (everyMinutes <= 0 || _lastTimestamped == null || (localNow - _lastTimestamped.Value).TotalMinutes >= everyMinutes)
                    return TimestampedName(localNow);
                return RollingName;
            }
        }

        /// <summary>The save with <paramref name="fileName"/> succeeded.</summary>
        public static void Saved(string fileName, DateTime localNow)
        {
            if (fileName == RollingName) return;
            lock (Gate) _lastTimestamped = localNow;
        }

        internal static void ResetForTests()
        {
            lock (Gate) _lastTimestamped = null;
        }
    }

    /// <summary>Production persistence: GameDataSave.SaveEdit(backupName) (serialised with Main's periodic save via RunFileIo).</summary>
    internal sealed class LiveDbEditPersistence : ILiveDbPersistence
    {
        private readonly int _backupEveryMinutes;

        public LiveDbEditPersistence(int backupEveryMinutes)
        {
            _backupEveryMinutes = backupEveryMinutes;
        }

        public void EnsureSavable() => GameDataSave.EnsureSavable();

        public LiveDbSaveOutcome Save()
        {
            var now = DateTime.Now;
            var name = LiveDbBackupPolicy.Next(now, _backupEveryMinutes);
            var outcome = GameDataSave.SaveEdit(name);
            LiveDbBackupPolicy.Saved(name, now);
            return new LiveDbSaveOutcome { BackupFile = name, Areas = outcome.Areas, Magic = outcome.Magic };
        }
    }

    /// <summary>The production <see cref="LiveDbEnvironment"/> for one request.</summary>
    internal static class LiveDbProduction
    {
        private static readonly LiveDbWorldHooks Hooks = new LiveDbWorldHooks();

        public static LiveDbEnvironment Environment(string admin, string ip)
        {
            var options = LiveDbOptions.FromSettings();
            return new LiveDbEnvironment
            {
                EditLock = WebAdminGame.EditLock,
                Edit = () => GameDataContext.FromEnvir(Envir.Edit),
                Main = () => GameDataContext.FromEnvir(Envir.Main),
                WorldState = () => WebAdminGame.WorldState,
                EditorFormsOpen = () => Envir.EditorFormsOpen,
                Unsaved = GameDataState.Unsaved,
                Persistence = new LiveDbEditPersistence(options.BackupEveryMinutes),
                RunOnGameThread = (action, timeoutMs) => GameDataSave.TryRunOnGameThread(WebAdminGame.World, () =>
                {
                    action();
                    return true;
                }, timeoutMs, out _),
                Hooks = Hooks,
                Journal = LiveDbJournal.Current,
                State = LiveDbState.Current,
                Options = options,
                Audit = (action, target, details, reason) => WebAdminAudit.Record(admin, ip, action, target, details, reason),
                Pending = (area, description) => WebAdminPending.Add(area, description, admin),
                MagicSynced = GameDataMagicSync.ClearPending,
                Log = WebAdminLog.Info,
                LogError = (ex, context) => WebAdminLog.Error(ex, context),
            };
        }
    }

    /// <summary>
    /// The pure parts of an NPC move (testable without a world): the order of finding F10 and the visibility schedule.
    /// </summary>
    internal static class LiveDbNpcMover
    {
        /// <summary>
        /// NPCObject.CurrentLocation reads Info.Location, so the NPC must leave its cell while the info still names it:
        /// <paramref name="removeFromCell"/> runs BEFORE MapIndex / Location change, <paramref name="addToCell"/> after.
        /// </summary>
        public static void Move(NPCInfo info, int mapIndex, Point location, Action removeFromCell, Action addToCell)
        {
            removeFromCell?.Invoke();
            info.MapIndex = mapIndex;
            info.Location = location;
            addToCell?.Invoke();
        }

        /// <summary>
        /// Whether the NPC's schedule shows it at <paramref name="now"/>, as the first NPCObject.Process after a world start decides it
        /// (visible unless DayofWeek names another day or TimeVisible's window excludes the time).
        /// </summary>
        public static bool ScheduleVisible(NPCInfo info, DateTime now)
        {
            if (!string.IsNullOrEmpty(info.DayofWeek) && info.DayofWeek != now.DayOfWeek.ToString()) return false;
            if (!info.TimeVisible) return true;
            var start = info.HourStart * 60 + info.MinuteStart;
            var finish = info.HourEnd * 60 + info.MinuteEnd;
            var current = now.Hour * 60 + now.Minute;
            return start <= current && finish > current;
        }
    }
}

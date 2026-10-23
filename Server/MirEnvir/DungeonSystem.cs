using System.Drawing;
using System.IO.Compression;
using Server.MirDatabase;
using Server.MirObjects;
using Gen = Crystal.DungeonGen;
using S = ServerPackets;

namespace Server.MirEnvir
{
    /// <summary>
    /// Procedural instance dungeons. A party asks (NPC action <c>ENTERDUNGEON profile</c>, or <c>@DUNGEON profile</c> for GMs
    /// and test servers); a worker thread generates the map with Crystal.DungeonGen (vendored in Components\DungeonGen);
    /// the game thread builds a runtime map, spawns monsters by depth and the boss, and moves the party to the start room.
    /// Killing the boss opens the way out in its chamber. An instance nobody is in is torn down after [Dungeons] EmptyMinutes.
    ///
    /// Threads: everything here runs on the game thread except the loading and generation tasks, which touch only their
    /// own data and hand results back through <see cref="Envir.QueueAdminAction"/>.
    /// Persistence: instances are never saved. Their maps live only in <see cref="Envir.MapList"/>, under indexes from
    /// <see cref="IndexBase"/> up that no saved map uses; a player logging out inside one is saved at its entrance, and a
    /// saved index from the band (after a crash) sends the player to their bind point at login.
    /// </summary>
    public sealed class DungeonSystem
    {
        /// <summary>First map index of the instance band. Edit's map counter never gets near it.</summary>
        public const int IndexBase = 1_900_000_000;

        // Exit portals are a 3×3 block of movement cells around the exit point, so a party does not have to find one cell.
        private const int ExitRadius = 1;
        private const long CheckIntervalMs = 1000;

        private readonly Envir envir;
        private readonly List<DungeonInstance> instances = new List<DungeonInstance>();
        private readonly HashSet<uint> waiting = new HashSet<uint>();   // ObjectIDs of players whose dungeon is being generated
        private int generating;
        private int nextIndex;
        private long nextCheck;
        private Lazy<DungeonData> data = new Lazy<DungeonData>(DungeonData.Load, LazyThreadSafetyMode.ExecutionAndPublication);
        private Task warming;

        public DungeonSystem(Envir envir)
        {
            this.envir = envir;
            // Indexes also differ across restarts, so a stale saved index from a crash cannot name a new instance.
            nextIndex = IndexBase + (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100_000_000);
        }

        public static bool IsInstanceIndex(int mapIndex) => mapIndex >= IndexBase;

        public bool IsInstance(Map map) => map != null && IsInstanceIndex(map.Info.Index);

        public int InstanceCount => instances.Count;

        /// <summary>Game thread, once a second: opens exits whose boss died, tears down empty instances.</summary>
        public void Process()
        {
            if (envir.Time < nextCheck) return;
            nextCheck = envir.Time + CheckIntervalMs;

            // Load the generator and its data off the game thread as soon as the system is switched on.
            if (Settings.DungeonsEnabled && warming == null)
            {
                var lazy = data;
                warming = Task.Run(() =>
                {
                    try { Warm(lazy); }
                    catch (Exception ex) { MessageQueue.Instance.Enqueue($"[Dungeon] The generator could not be loaded: {ex.Message}"); }
                });
            }

            long emptyMs = Settings.DungeonEmptyMinutes * 60_000L;
            for (int i = instances.Count - 1; i >= 0; i--)
            {
                var instance = instances[i];
                if (!instance.ExitOpen && (instance.Boss == null || instance.Boss.Dead || instance.Boss.Node == null))
                    OpenExit(instance);

                if (instance.Map.Players.Count > 0) instance.EmptySince = 0;
                else if (instance.EmptySince == 0) instance.EmptySince = envir.Time;
                else if (envir.Time - instance.EmptySince >= emptyMs) TearDown(instance, i);
            }
        }

        /// <summary>
        /// Game thread: <paramref name="player"/> asks for a dungeon of <paramref name="profileName"/> for themselves and the
        /// members of their group standing near them. Group runs are started by the group leader.
        /// </summary>
        public void RequestEntry(PlayerObject player, string profileName)
        {
            if (player?.CurrentMap == null || player.Node == null)
            {
                MessageQueue.Instance.Enqueue($"[Dungeon] Ignored a request for {profileName} from {player?.Name ?? "?"}: not in the world.");
                return;
            }
            if (!Settings.DungeonsEnabled)
            {
                Refuse(player, ServerTextKeys.DungeonsDisabled, "the system is off ([Dungeons] Enabled)");
                return;
            }

            var party = new List<PlayerObject>();
            if (player.GroupMembers == null) party.Add(player);
            else
            {
                if (player.GroupMembers.Count == 0 || player.GroupMembers[0] != player)
                {
                    Refuse(player, ServerTextKeys.DungeonLeaderOnly, "not the group leader");
                    return;
                }
                for (int i = 0; i < player.GroupMembers.Count; i++)
                {
                    var member = player.GroupMembers[i];
                    if (member == player || (member.Node != null && !member.Dead && member.CurrentMap == player.CurrentMap &&
                        Functions.InRange(member.CurrentLocation, player.CurrentLocation, Globals.DataRange)))
                        party.Add(member);
                }
            }

            for (int i = 0; i < party.Count; i++)
                if (IsInstance(party[i].CurrentMap) || waiting.Contains(party[i].ObjectID))
                {
                    Refuse(player, ServerTextKeys.DungeonAlreadyInside, party[i].Name + " is already in or waiting for a dungeon");
                    return;
                }

            if (instances.Count + generating >= Settings.DungeonMaxInstances)
            {
                Refuse(player, ServerTextKeys.DungeonLimitReached, $"{instances.Count} open and {generating} generating ([Dungeons] MaxInstances {Settings.DungeonMaxInstances})");
                return;
            }

            var request = new DungeonRequest
            {
                Profile = profileName ?? string.Empty,
                Seed = envir.Random.Next(),
                LeaderName = player.Name,
                ReturnMapIndex = player.CurrentMap.Info.Index,
                ReturnLocation = player.CurrentLocation,
                Members = new uint[party.Count],
            };
            for (int i = 0; i < party.Count; i++)
            {
                request.Members[i] = party[i].ObjectID;
                waiting.Add(party[i].ObjectID);
                Say(party[i], ServerTextKeys.DungeonPreparing);
            }
            generating++;
            MessageQueue.Instance.Enqueue($"[Dungeon] {player.Name} asked for {request.Profile} with {party.Count} player(s); generating seed {request.Seed}.");

            var lazy = data;
            Task.Run(() => GenerateSafely(request, lazy));
        }

        // Kept apart from Generate so that a missing Crystal.DungeonGen assembly (it fails when Generate is compiled, before
        // its own try) still reaches the player and the log instead of faulting the task unobserved.
        private void GenerateSafely(DungeonRequest request, Lazy<DungeonData> lazy)
        {
            try
            {
                Generate(request, lazy);
            }
            catch (Exception ex)
            {
                request.Error = ex;
                if (!envir.QueueAdminAction(() => Complete(request)))
                    MessageQueue.Instance.Enqueue($"[Dungeon] {request.LeaderName}'s dungeon failed and the game loop is not running: {ex.Message}");
            }
        }

        private static void Warm(Lazy<DungeonData> lazy) => _ = lazy.Value;

        /// <summary>
        /// Game thread, from <see cref="HumanObject.Teleport"/>: a player about to enter an instance map gets the map first,
        /// in the same connection queue ahead of the S.MapChanged, since the client has no file for it.
        /// </summary>
        public void BeforeEnter(PlayerObject player, Map target)
        {
            if (!IsInstance(target)) return;
            for (int i = 0; i < instances.Count; i++)
            {
                if (instances[i].Map != target) continue;
                var parts = instances[i].MapParts;
                for (int p = 0; p < parts.Length; p++)
                    player.Enqueue(new S.DungeonMap { FileName = target.Info.FileName, Part = (ushort)p, Parts = (ushort)parts.Length, Data = parts[p] });
                return;
            }
        }

        /// <summary>Game thread, from <see cref="PlayerObject.StopGame"/>: a player leaving the game inside an instance is saved at its entrance.</summary>
        public void PlayerLeavingGame(PlayerObject player)
        {
            if (!IsInstance(player.CurrentMap)) return;
            for (int i = 0; i < instances.Count; i++)
            {
                if (instances[i].Map != player.CurrentMap) continue;
                player.Info.CurrentMapIndex = instances[i].ReturnMapIndex;
                player.Info.CurrentLocation = instances[i].ReturnLocation;
                return;
            }
        }

        // Worker thread: generate and compress, then hand the result to the game thread. Touches no world state.
        private void Generate(DungeonRequest request, Lazy<DungeonData> lazy)
        {
            try
            {
                var loaded = lazy.Value;
                var profile = loaded.Generator.Profiles.FirstOrDefault(p => string.Equals(p.Name, request.Profile, StringComparison.OrdinalIgnoreCase));
                if (profile != null)
                {
                    request.Result = loaded.Generator.Generate(profile, request.Seed);
                    request.MapBytes = request.Result.Map.ToMapBytes();
                    request.MapParts = Split(Compress(request.MapBytes));
                    request.Monsters = loaded.Monsters;
                }
            }
            catch (Exception ex)
            {
                request.Error = ex;
            }

            // MessageQueue may be called from any thread. A refused hand-back means the world loop is not running.
            if (!envir.QueueAdminAction(() => Complete(request),
                    dropped: ex => MessageQueue.Instance.Enqueue($"[Dungeon] {request.LeaderName}'s dungeon was dropped before the game loop took it.")))
                MessageQueue.Instance.Enqueue($"[Dungeon] {request.LeaderName}'s dungeon was generated but the game loop is not running.");
        }

        // Game thread: build the instance and move the party in.
        private void Complete(DungeonRequest request)
        {
            generating--;
            for (int i = 0; i < request.Members.Length; i++) waiting.Remove(request.Members[i]);

            var members = new List<PlayerObject>();
            for (int i = envir.Players.Count - 1; i >= 0; i--)
                if (Array.IndexOf(request.Members, envir.Players[i].ObjectID) >= 0) members.Add(envir.Players[i]);

            if (request.Error != null || request.Result == null)
            {
                if (request.Error != null)
                {
                    MessageQueue.Instance.Enqueue($"[Dungeon] Generating {request.Profile} (seed {request.Seed}) for {request.LeaderName} failed.");
                    MessageQueue.Instance.Enqueue(request.Error);
                    // A failed load is retried by the next request rather than cached for good.
                    if (data.IsValueCreated == false) data = new Lazy<DungeonData>(DungeonData.Load, LazyThreadSafetyMode.ExecutionAndPublication);
                    warming = null;
                }
                for (int i = 0; i < members.Count; i++)
                    if (request.Error != null) Say(members[i], ServerTextKeys.DungeonFailed);
                    else Say(members[i], ServerTextKeys.DungeonUnknownProfile, request.Profile);
                return;
            }

            if (!Settings.DungeonsEnabled)
            {
                for (int i = 0; i < members.Count; i++) Say(members[i], ServerTextKeys.DungeonsDisabled);
                return;
            }

            var result = request.Result;
            int index = nextIndex++;
            var info = new MapInfo
            {
                Index = index,
                FileName = "Dungeon" + index,
                Title = result.Style,
                Light = LightSetting.Normal,
                // No escape routes out of a run, no pets of the hero kind, and no safe zones (which would rebind players inside).
                NoTeleport = true, NoRandom = true, NoEscape = true, NoRecall = true, NoTownTeleport = true, NoPosition = true, NoHero = true,
            };
            var map = new Map(info);
            if (!map.LoadGenerated(request.MapBytes))
            {
                MessageQueue.Instance.Enqueue($"[Dungeon] The generated map for {request.Profile} (seed {result.Seed}) could not be loaded.");
                for (int i = 0; i < members.Count; i++) Say(members[i], ServerTextKeys.DungeonFailed);
                return;
            }

            envir.MapList.Add(map);
            var instance = new DungeonInstance
            {
                Map = map,
                Layout = result.Layout,
                MapParts = request.MapParts,
                ReturnMapIndex = request.ReturnMapIndex,
                ReturnLocation = request.ReturnLocation,
            };
            instances.Add(instance);
            int monsters = SpawnMonsters(instance, request.Monsters.ForStyle(result.Style));

            var start = StartCells(result.Layout, map, members.Count);
            int entered = 0;
            for (int i = 0; i < members.Count; i++)
            {
                var member = members[i];
                if (member.Node == null || member.Dead || IsInstance(member.CurrentMap)) continue;
                if (!member.Teleport(map, start[entered % start.Count])) continue;
                entered++;
                if (instance.Boss != null) Say(member, ServerTextKeys.DungeonEntered, result.Style, instance.BossName);
            }

            MessageQueue.Instance.Enqueue($"[Dungeon] {request.LeaderName} entered {result.Profile} {result.Style} {result.Type} seed {result.Seed} " +
                $"(asked {result.RequestedSeed}, {result.Rejected.Count} rejected) as map {index}: {entered} players, {monsters} monsters.");
            if (entered == 0) TearDown(instance, instances.Count - 1);
        }

        private int SpawnMonsters(DungeonInstance instance, DungeonMonsterTable.Style style)
        {
            var map = instance.Map;
            var layout = instance.Layout;
            var random = envir.Random;
            int spawned = 0;
            if (style == null) return 0;

            var pool = new List<MonsterInfo>(style.Monsters.Count);
            for (int i = 0; i < style.Monsters.Count; i++)
            {
                var info = envir.GetMonsterInfo(style.Monsters[i], true);
                if (info != null) pool.Add(info);
            }

            int deepest = Math.Max(1, layout.Boss.Depth - 1);
            if (pool.Count > 0)
                foreach (var zone in layout.SpawnZones)
                {
                    if (zone.Cells.Count == 0) continue;
                    // Deeper rooms draw from further up the style's list, which runs from easiest to hardest.
                    double depth = deepest <= 1 ? 1 : (zone.Tier - 1) / (double)(deepest - 1);
                    int centre = (int)Math.Round(Math.Clamp(depth, 0, 1) * (pool.Count - 1));
                    int count = Math.Clamp(zone.Cells.Count / style.CellsPerMonster, 1, style.MaxPerRoom);
                    for (int i = 0; i < count; i++)
                    {
                        var info = pool[Math.Clamp(centre + random.Next(-1, 2), 0, pool.Count - 1)];
                        var cell = zone.Cells[random.Next(zone.Cells.Count)];
                        if (Spawn(info, map, new Point(cell.X, cell.Y)) != null) spawned++;
                    }
                }

            if (pool.Count < style.Monsters.Count)
                MessageQueue.Instance.Enqueue($"[Dungeon] {map.Info.Title}: {style.Monsters.Count - pool.Count} of {style.Monsters.Count} monsters in monsters.json are not in the monster database.");

            var bossInfo = string.IsNullOrEmpty(style.Boss) ? null : envir.GetMonsterInfo(style.Boss, true);
            instance.BossName = bossInfo?.GameName ?? style.Boss ?? "the guardian";
            if (bossInfo != null)
            {
                instance.Boss = Spawn(bossInfo, map, new Point(layout.BossPoint.X, layout.BossPoint.Y));
                for (int i = 0; instance.Boss == null && i < layout.Boss.Cells.Count; i += 7)
                    instance.Boss = Spawn(bossInfo, map, new Point(layout.Boss.Cells[i].X, layout.Boss.Cells[i].Y));
                if (instance.Boss != null) spawned++;
            }
            if (instance.Boss == null)
                MessageQueue.Instance.Enqueue($"[Dungeon] {map.Info.Title}: boss {style.Boss} could not be spawned (not in the monster database?); the way out is open from the start.");
            return spawned;
        }

        private MonsterObject Spawn(MonsterInfo info, Map map, Point location)
        {
            var monster = MonsterObject.GetMonster(info);
            if (monster == null) return null;
            monster.Direction = (MirDirection)envir.Random.Next(8);
            monster.ActionTime = envir.Time + 1000;
            return monster.Spawn(map, location) ? monster : null;
        }

        // Start-room cells nearest the start point, one per member where the room allows.
        private static List<Point> StartCells(Gen.DungeonLayout layout, Map map, int count)
        {
            var cells = new List<Point>();
            var start = new Point(layout.StartPoint.X, layout.StartPoint.Y);
            if (map.ValidPoint(start)) cells.Add(start);
            var room = layout.Start.Cells;
            var byDistance = new List<(int Distance, Point Cell)>(room.Count);
            for (int i = 0; i < room.Count; i++)
            {
                var cell = new Point(room[i].X, room[i].Y);
                if (cell != start && map.ValidPoint(cell))
                    byDistance.Add(((cell.X - start.X) * (cell.X - start.X) + (cell.Y - start.Y) * (cell.Y - start.Y), cell));
            }
            byDistance.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            for (int i = 0; i < byDistance.Count && cells.Count < Math.Max(1, count); i += 2) cells.Add(byDistance[i].Cell);
            if (cells.Count == 0) cells.Add(start);
            return cells;
        }

        // The boss is dead (or was never spawned): movement cells in its chamber lead back to where the party came in.
        private void OpenExit(DungeonInstance instance)
        {
            instance.ExitOpen = true;
            var exit = instance.Layout.ExitPoint;
            for (int dy = -ExitRadius; dy <= ExitRadius; dy++)
            for (int dx = -ExitRadius; dx <= ExitRadius; dx++)
            {
                var source = new Point(exit.X + dx, exit.Y + dy);
                if (!instance.Map.ValidPoint(source)) continue;
                instance.Map.Info.Movements.Add(new MovementInfo
                {
                    MapIndex = instance.ReturnMapIndex,
                    Source = source,
                    Destination = instance.ReturnLocation,
                });
            }

            if (instance.Boss == null) return; // never spawned: nothing to announce
            for (int i = instance.Map.Players.Count - 1; i >= 0; i--)
                Say(instance.Map.Players[i], ServerTextKeys.DungeonExitOpened, instance.BossName);
        }

        // Removes every object from an empty instance and takes its map out of the world.
        private void TearDown(DungeonInstance instance, int listIndex)
        {
            var map = instance.Map;
            var doomed = new List<MapObject>();
            foreach (var cell in map.ObjectCells())
            {
                var objects = cell.Objects;
                if (objects == null) continue;
                for (int i = 0; i < objects.Count; i++) doomed.Add(objects[i]);
            }

            for (int i = 0; i < doomed.Count; i++)
            {
                var ob = doomed[i];
                if (ob.Race == ObjectType.Player || ob.Node == null) continue; // players never stay in an empty instance
                if (ob.Race == ObjectType.Monster && !ob.Dead)
                {
                    Interlocked.Decrement(ref envir.MonsterCount);
                    Interlocked.Decrement(ref map.MonsterCount);
                }
                map.RemoveObject(ob);
                ob.Despawn();
            }

            envir.RemoveMap(map);
            instances.RemoveAt(listIndex);
            MessageQueue.Instance.Enqueue($"[Dungeon] Closed {map.Info.Title} (map {map.Info.Index}); {instances.Count} open.");
        }

        // A request turned down: the player is told why, and the log says why too (operators cannot see player chat).
        private static void Refuse(PlayerObject player, ServerTextKeys key, string reason)
        {
            Say(player, key);
            MessageQueue.Instance.Enqueue($"[Dungeon] Refused {player.Name}: {reason}.");
        }

        private static void Say(PlayerObject player, ServerTextKeys key, params object[] args) =>
            player.ReceiveChat(GameLanguage.ServerTextMap.GetLocalization(key, args), ChatType.System);

        private static byte[] Compress(byte[] bytes)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(bytes, 0, bytes.Length);
            return output.ToArray();
        }

        private static byte[][] Split(byte[] bytes)
        {
            int count = Math.Max(1, (bytes.Length + S.DungeonMap.MaxPartBytes - 1) / S.DungeonMap.MaxPartBytes);
            var parts = new byte[count][];
            for (int i = 0; i < count; i++)
            {
                int offset = i * S.DungeonMap.MaxPartBytes;
                parts[i] = new byte[Math.Min(S.DungeonMap.MaxPartBytes, bytes.Length - offset)];
                Buffer.BlockCopy(bytes, offset, parts[i], 0, parts[i].Length);
            }
            return parts;
        }

        private sealed class DungeonRequest
        {
            public string Profile, LeaderName;
            public int Seed, ReturnMapIndex;
            public Point ReturnLocation;
            public uint[] Members;
            // Filled by the worker.
            public Gen.DungeonResult Result;
            public byte[] MapBytes;     // the map in .map format, for the server's own load
            public byte[][] MapParts;   // the same gzipped and split, for clients
            public DungeonMonsterTable Monsters;
            public Exception Error;
        }

        private sealed class DungeonInstance
        {
            public Map Map;
            public Gen.DungeonLayout Layout;
            public byte[][] MapParts;
            public int ReturnMapIndex;
            public Point ReturnLocation;
            public MonsterObject Boss;
            public string BossName;
            public bool ExitOpen;
            public long EmptySince;
        }

        // The generator and the monster table, loaded together off the game thread.
        private sealed class DungeonData
        {
            public Gen.DungeonGenerator Generator;
            public DungeonMonsterTable Monsters;

            public static DungeonData Load()
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                string root = Path.Combine(AppContext.BaseDirectory, "DungeonGen");
                var loaded = new DungeonData
                {
                    Generator = Gen.DungeonGenerator.Load(Path.Combine(root, "data")),
                    Monsters = DungeonMonsterTable.Load(Path.Combine(root, "monsters.json")),
                };
                MessageQueue.Instance.Enqueue($"[Dungeon] Generator loaded from {root} in {watch.ElapsedMilliseconds} ms ({loaded.Generator.Profiles.Count} profiles).");
                return loaded;
            }
        }
    }
}

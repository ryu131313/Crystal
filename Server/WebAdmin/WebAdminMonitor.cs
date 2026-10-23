using System.Collections.Concurrent;
using System.IO.Compression;
using Server.MirDatabase;
using Server.MirEnvir;
using Server.MirObjects;

namespace Server.WebAdmin
{
    // ======================================================================================================================
    // DTOs (plain classes, filled on the game thread, serialised on the HTTP thread)
    // ======================================================================================================================

    public sealed class MonitorPlayer
    {
        public string Name { get; set; }
        public int CharacterIndex { get; set; }
        public int AccountIndex { get; set; }
        public string AccountId { get; set; }
        public int Level { get; set; }
        public string ClassName { get; set; }
        public string Guild { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int HpPercent { get; set; }
        public int Group { get; set; }
        public bool Dead { get; set; }
        public bool Gm { get; set; }
        public bool SafeZone { get; set; }
    }

    public sealed class MonitorMap
    {
        /// <summary>"&lt;MapInfo.Index&gt;:&lt;instance&gt;", instance 1-based in Envir.MapList order (what GM teleport/spawn use).</summary>
        public string Key { get; set; }
        public int Index { get; set; }
        public int Instance { get; set; }
        public string Title { get; set; }
        public string FileName { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        /// <summary>MapInfo.MiniMap: the frame of the client's MMap.Lib (0 = none).</summary>
        public int MiniMap { get; set; }
        public int PlayerCount { get; set; }
        public int Gms { get; set; }
        /// <summary>Rows of the focus map only (null for the others: the page lists them by count).</summary>
        public List<MonitorPlayer> Players { get; set; }
    }

    /// <summary>The selected player wherever they are (?sel=), so the page can follow them off the focus map.</summary>
    public sealed class MonitorSelected
    {
        public string Key { get; set; }
        public MonitorPlayer Player { get; set; }
    }

    /// <summary>Monsters of one map: <c>rows</c> is a flat [nameIndex, x, y, level, hpPercent, flags, ...] list (flags: 1 boss, 2 pet).</summary>
    public sealed class MonitorMobs
    {
        public string Key { get; set; }
        public string Mode { get; set; }
        public int Total { get; set; }
        public bool Truncated { get; set; }
        public List<string> Names { get; set; }
        public List<int> Rows { get; set; }
    }

    public sealed class MonitorEvent
    {
        public long Seq { get; set; }
        public DateTime Utc { get; set; }
        /// <summary>death, login, logout.</summary>
        public string Type { get; set; }
        public string Name { get; set; }
        /// <summary>Killer name for a death (null when unknown).</summary>
        public string By { get; set; }
        /// <summary>Killer kind for a death: player, monster, pet or null.</summary>
        public string ByKind { get; set; }
        public string Key { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
    }

    public sealed class MonitorSnapshot
    {
        public List<MonitorMap> Maps { get; set; }
        public MonitorMobs Mobs { get; set; }
        public MonitorSelected Selected { get; set; }
        public List<MonitorEvent> Events { get; set; }
        public long LatestSeq { get; set; }
        public int TotalPlayers { get; set; }
        public int Deaths10m { get; set; }
    }

    /// <summary>The cells of one map, copied on the game thread for the image encoder.</summary>
    internal sealed class MonitorMapCells
    {
        public int Index;
        public int Width, Height;
        /// <summary>CellAttribute per cell, x-major like Map.CellData ([x * Height + y]).</summary>
        public byte[] Attributes;
        /// <summary>Safe zones as [x, y, size, ...].</summary>
        public int[] SafeZones;
    }

    /// <summary>
    /// Live Monitor (admin section "monitor"): the game-thread snapshot of every occupied map, the monsters of one map, a small event
    /// feed (deaths, logins, logouts), server-accumulated player heat and a walkability image per map.
    ///
    /// Threads: <see cref="BuildSnapshot"/> and the event ring run on the game thread only (inside <see cref="WebAdminGame.RunAsync{T}"/>),
    /// so the ring needs no lock. Hook subscribers may run on a mob thread (a monster can kill a player during the mob pass), so they only
    /// enqueue into a capped <see cref="ConcurrentQueue{T}"/>. Heat: a game-thread capture every sampler tick (5 s, posted by
    /// <see cref="WebAdminStatusSampler"/>) hands an int[] to a ConcurrentQueue; the sampler / HTTP threads fold it into minute buckets under
    /// <see cref="HeatLock"/>, which the game thread never takes.
    /// </summary>
    public static class WebAdminMonitor
    {
        public const int MaxMobRows = 4000;
        public const int HeatMinutes = 15;
        public const int HeatGrid = 2;
        private const int EventRingSize = 300;
        private const int MaxPendingEvents = 1000;
        private const int MaxPendingHeat = 64;
        private const int RecentEventsOnFirstPoll = 30;

        // ---- hooks -> game thread ---------------------------------------------------------------------------------------

        private sealed class PendingEvent
        {
            public string Type, Name, By, ByKind;
            public Map Map;
            public int X, Y;
            public DateTime Utc;
        }

        private static readonly ConcurrentQueue<PendingEvent> Pending = new ConcurrentQueue<PendingEvent>();
        private static int _pendingCount;
        private static int _hooked;

        /// <summary>Subscribes the hooks once per process (idempotent; called from MonitorRoutes.Register).</summary>
        internal static void EnsureHooked()
        {
            if (Interlocked.Exchange(ref _hooked, 1) != 0) return;

            WebAdminHooks.PlayerEntered += p => Enqueue("login", p, null);
            WebAdminHooks.PlayerLeft += p => Enqueue("logout", p, null);
            WebAdminHooks.PlayerDied += (p, killer) => Enqueue("death", p, killer);
            WebAdminHooks.WorldStarted += () => MapImages.Clear();
        }

        /// <summary>O(1), any thread (a death may be raised on a mob thread): one allocation, dropped beyond the cap.</summary>
        private static void Enqueue(string type, PlayerObject p, MapObject killer)
        {
            if (p == null) return;
            if (Interlocked.Increment(ref _pendingCount) > MaxPendingEvents)
            {
                Interlocked.Decrement(ref _pendingCount);
                return;
            }

            var e = new PendingEvent
            {
                Type = type,
                Name = p.Name,
                Map = p.CurrentMap,
                X = p.CurrentLocation.X,
                Y = p.CurrentLocation.Y,
                Utc = Envir.Main.Now,
            };

            if (killer != null)
            {
                e.By = killer.Name;
                e.ByKind = killer.Race == ObjectType.Player ? "player" : killer.Master != null ? "pet" : killer.Race == ObjectType.Monster ? "monster" : null;
            }

            Pending.Enqueue(e);
        }

        // ---- event ring (game thread only) ------------------------------------------------------------------------------

        private static readonly MonitorEvent[] Ring = new MonitorEvent[EventRingSize];
        private static int _ringStart, _ringCount;
        private static long _seq;

        private static void DrainPending(Dictionary<Map, string> keys)
        {
            while (Pending.TryDequeue(out var p))
            {
                Interlocked.Decrement(ref _pendingCount);

                string key = null;
                if (p.Map != null && !keys.TryGetValue(p.Map, out key)) key = null;

                var e = new MonitorEvent
                {
                    Seq = ++_seq,
                    Utc = p.Utc,
                    Type = p.Type,
                    Name = p.Name,
                    By = p.By,
                    ByKind = p.ByKind,
                    Key = key,
                    X = p.X,
                    Y = p.Y,
                };

                if (_ringCount < Ring.Length)
                {
                    Ring[(_ringStart + _ringCount) % Ring.Length] = e;
                    _ringCount++;
                }
                else
                {
                    Ring[_ringStart] = e;
                    _ringStart = (_ringStart + 1) % Ring.Length;
                }
            }
        }

        // ---- snapshot (game thread) -------------------------------------------------------------------------------------

        /// <summary>
        /// Game thread. Every map with players (plus <paramref name="focusKey"/> when it names a loaded map) with its counts, player rows for
        /// the focus map only, the <paramref name="selectedName"/> player's row wherever they are, the monsters of the focus map when
        /// <paramref name="mobMode"/> is "all" or "bosses", and the events after <paramref name="evSince"/> (a negative value = first poll:
        /// the last few events, which the page lists but never flashes). O(maps + players), rows only for the focus map, plus O(objects)
        /// when monsters are asked.
        /// </summary>
        internal static MonitorSnapshot BuildSnapshot(string focusKey, string mobMode, long evSince, string selectedName = null)
        {
            var envir = Envir.Main;
            var mapList = envir.MapList;
            var instances = new Dictionary<int, int>(mapList.Count);
            var keys = new Dictionary<Map, string>(mapList.Count);
            var maps = new List<MonitorMap>();
            Map focus = null;
            string focusMapKey = null;
            var total = 0;

            for (var i = 0; i < mapList.Count; i++)
            {
                var map = mapList[i];
                if (map?.Info == null) continue;

                instances.TryGetValue(map.Info.Index, out var n);
                n++;
                instances[map.Info.Index] = n;
                var key = map.Info.Index + ":" + n;
                keys[map] = key;

                var isFocus = focusKey != null && string.Equals(key, focusKey, StringComparison.Ordinal);
                if (isFocus)
                {
                    focus = map;
                    focusMapKey = key;
                }

                var players = map.Players;
                if (players.Count == 0 && !isFocus) continue;

                // Full rows (strings and all) only where the page shows them: with many bots online the other maps are just counts.
                var rows = isFocus ? new List<MonitorPlayer>(players.Count) : null;
                int count = 0, gms = 0;
                for (var j = 0; j < players.Count; j++)
                {
                    var p = players[j];
                    if (p?.Info == null) continue;
                    count++;
                    if (p.IsGM) gms++;
                    if (rows != null) rows.Add(PlayerRow(p));
                }

                total += count;
                maps.Add(new MonitorMap
                {
                    Key = key,
                    Index = map.Info.Index,
                    Instance = n,
                    Title = string.IsNullOrEmpty(map.Info.Title) ? map.Info.FileName : map.Info.Title,
                    FileName = map.Info.FileName,
                    Width = map.Width,
                    Height = map.Height,
                    MiniMap = map.Info.MiniMap,
                    PlayerCount = count,
                    Gms = gms,
                    Players = rows,
                });
            }

            DrainPending(keys);

            MonitorSelected selected = null;
            if (!string.IsNullOrEmpty(selectedName))
            {
                var sp = envir.GetPlayer(selectedName);
                if (sp?.Info != null && sp.CurrentMap != null && keys.TryGetValue(sp.CurrentMap, out var selKey))
                    selected = new MonitorSelected { Key = selKey, Player = PlayerRow(sp) };
            }

            var snap = new MonitorSnapshot
            {
                Maps = maps,
                Selected = selected,
                TotalPlayers = total,
                LatestSeq = _seq,
                Events = new List<MonitorEvent>(),
            };

            var cutoff = envir.Now.AddMinutes(-10);
            var firstWanted = evSince < 0 ? Math.Max(0, _ringCount - RecentEventsOnFirstPoll) : 0;
            for (var i = 0; i < _ringCount; i++)
            {
                var e = Ring[(_ringStart + i) % Ring.Length];
                if (e.Type == "death" && e.Utc >= cutoff) snap.Deaths10m++;
                if (evSince < 0 ? i >= firstWanted : e.Seq > evSince) snap.Events.Add(e);
            }

            if (focus != null && (mobMode == "all" || mobMode == "bosses"))
                snap.Mobs = BuildMobs(envir, focus, focusMapKey, mobMode == "bosses");

            return snap;
        }

        private static MonitorPlayer PlayerRow(PlayerObject p)
        {
            var max = p.Stats != null ? p.Stats[Stat.HP] : 0;
            return new MonitorPlayer
            {
                Name = p.Name,
                CharacterIndex = p.Info.Index,
                AccountIndex = p.Account?.Index ?? 0,
                AccountId = p.Account?.AccountID,
                Level = p.Level,
                ClassName = p.Class.ToString(),
                Guild = p.MyGuild?.Name,
                X = p.CurrentLocation.X,
                Y = p.CurrentLocation.Y,
                HpPercent = max > 0 ? Math.Clamp((int)((long)p.HP * 100 / max), 0, 100) : 0,
                Group = p.GroupMembers?.Count ?? 0,
                Dead = p.Dead,
                Gm = p.IsGM,
                SafeZone = p.InSafeZone,
            };
        }

        /// <summary>Game thread, mob threads parked (admin actions drain after the barrier). Walks Envir.Objects once.</summary>
        private static MonitorMobs BuildMobs(Envir envir, Map map, string key, bool bossesOnly)
        {
            var names = new List<string>();
            var nameIndex = new Dictionary<MonsterInfo, int>();
            var rows = new List<int>(256);
            var total = 0;

            for (var node = envir.Objects.First; node != null; node = node.Next)
            {
                var ob = node.Value;
                if (ob == null || ob.Race != ObjectType.Monster || ob.Dead || ob.CurrentMap != map) continue;
                if (ob is not MonsterObject mob || mob.Info == null) continue;

                var boss = mob.Info.IsBoss;
                if (bossesOnly && !boss) continue;

                total++;
                if (rows.Count >= MaxMobRows * 6) continue;

                if (!nameIndex.TryGetValue(mob.Info, out var ni))
                {
                    ni = names.Count;
                    names.Add(mob.Info.Name);
                    nameIndex[mob.Info] = ni;
                }

                var max = mob.MaxHealth;
                rows.Add(ni);
                rows.Add(mob.CurrentLocation.X);
                rows.Add(mob.CurrentLocation.Y);
                rows.Add(mob.Level);
                rows.Add(max > 0 ? Math.Clamp((int)((long)mob.Health * 100 / max), 0, 100) : 0);
                rows.Add((boss ? 1 : 0) | (mob.Master != null ? 2 : 0));
            }

            return new MonitorMobs
            {
                Key = key,
                Mode = bossesOnly ? "bosses" : "all",
                Total = total,
                Truncated = total > MaxMobRows,
                Names = names,
                Rows = rows,
            };
        }

        /// <summary>"&lt;index&gt;:&lt;instance&gt;" with both parts non-negative decimal integers, instance ≥ 1; null otherwise.</summary>
        public static string NormaliseKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            var parts = key.Trim().Split(':');
            if (parts.Length != 2) return null;
            if (!int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)) return null;
            if (!int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var instance) || instance < 1) return null;
            return index + ":" + instance;
        }

        // ---- heat --------------------------------------------------------------------------------------------------------

        private sealed class HeatSample
        {
            public long Minute;
            /// <summary>[mapIndex, x, y, ...].</summary>
            public int[] Points;
        }

        private sealed class HeatBucket
        {
            public long Minute = -1;
            public readonly Dictionary<int, Dictionary<int, int>> Maps = new Dictionary<int, Dictionary<int, int>>();
        }

        private static readonly ConcurrentQueue<HeatSample> PendingHeat = new ConcurrentQueue<HeatSample>();
        private static int _pendingHeatCount;
        private static readonly object HeatLock = new object();
        private static readonly HeatBucket[] HeatBuckets = CreateBuckets();

        private static HeatBucket[] CreateBuckets()
        {
            var b = new HeatBucket[HeatMinutes];
            for (var i = 0; i < b.Length; i++) b[i] = new HeatBucket();
            return b;
        }

        public static long MinuteOf(DateTime utc) => utc.Ticks / TimeSpan.TicksPerMinute;

        /// <summary>
        /// Sampler thread, every tick: folds the captures that arrived, then posts the next capture to the game thread (no-op while the
        /// world is not running). The capture is O(players) with one int[] per tick.
        /// </summary>
        internal static void SampleTick()
        {
            FoldHeat();
            if (!WebAdminGame.IsRunning) return;
            if (Volatile.Read(ref _pendingHeatCount) >= MaxPendingHeat) return;

            var minute = MinuteOf(DateTime.UtcNow);
            WebAdminGame.Post(() =>
            {
                var players = Envir.Main.Players;
                var points = new int[players.Count * 3];
                var n = 0;
                for (var i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    var info = p?.CurrentMap?.Info;
                    if (info == null || p.Dead) continue;
                    points[n++] = info.Index;
                    points[n++] = p.CurrentLocation.X;
                    points[n++] = p.CurrentLocation.Y;
                }

                if (n != points.Length) Array.Resize(ref points, n);
                Interlocked.Increment(ref _pendingHeatCount);
                PendingHeat.Enqueue(new HeatSample { Minute = minute, Points = points });
            });
        }

        private static void FoldHeat()
        {
            if (PendingHeat.IsEmpty) return;

            lock (HeatLock)
            {
                while (PendingHeat.TryDequeue(out var s))
                {
                    Interlocked.Decrement(ref _pendingHeatCount);
                    AddHeat(s.Minute, s.Points);
                }
            }
        }

        /// <summary>Folds one capture ([mapIndex, x, y, ...]) into its minute bucket. Caller holds HeatLock (tests: single thread).</summary>
        internal static void AddHeat(long minute, int[] points)
        {
            if (points == null) return;
            var bucket = HeatBuckets[(int)(minute % HeatMinutes)];
            if (bucket.Minute != minute)
            {
                if (bucket.Minute > minute) return; // a late capture for a minute already recycled
                bucket.Minute = minute;
                bucket.Maps.Clear();
            }

            for (var i = 0; i + 2 < points.Length; i += 3)
            {
                if (!bucket.Maps.TryGetValue(points[i], out var cells))
                {
                    cells = new Dictionary<int, int>();
                    bucket.Maps[points[i]] = cells;
                }

                var cell = HeatCell(points[i + 1], points[i + 2]);
                cells.TryGetValue(cell, out var c);
                cells[cell] = c + 1;
            }
        }

        private static int HeatCell(int x, int y) => ((y / HeatGrid) << 16) | ((x / HeatGrid) & 0xFFFF);

        /// <summary>
        /// HTTP thread. Heat of one map index (all instances) over the last <paramref name="minutes"/> (1..15) as a flat
        /// [gridX, gridY, count, ...] list with grid cells of <see cref="HeatGrid"/> x <see cref="HeatGrid"/> map cells.
        /// </summary>
        public static (List<int> cells, int max, int samples) GetHeat(int mapIndex, int minutes, DateTime nowUtc)
        {
            FoldHeat();
            minutes = Math.Clamp(minutes, 1, HeatMinutes);
            var now = MinuteOf(nowUtc);
            var merged = new Dictionary<int, int>();
            var samples = 0;

            lock (HeatLock)
            {
                for (var i = 0; i < HeatBuckets.Length; i++)
                {
                    var b = HeatBuckets[i];
                    if (b.Minute < 0 || b.Minute > now || b.Minute <= now - minutes) continue;
                    if (!b.Maps.TryGetValue(mapIndex, out var cells)) continue;
                    foreach (var kv in cells)
                    {
                        merged.TryGetValue(kv.Key, out var c);
                        merged[kv.Key] = c + kv.Value;
                        samples += kv.Value;
                    }
                }
            }

            var list = new List<int>(merged.Count * 3);
            var max = 0;
            foreach (var kv in merged)
            {
                list.Add(kv.Key & 0xFFFF);
                list.Add(kv.Key >> 16);
                list.Add(kv.Value);
                if (kv.Value > max) max = kv.Value;
            }

            return (list, max, samples);
        }

        /// <summary>Tests only.</summary>
        internal static void ResetHeatForTests()
        {
            lock (HeatLock)
            {
                while (PendingHeat.TryDequeue(out _)) { }
                Interlocked.Exchange(ref _pendingHeatCount, 0);
                for (var i = 0; i < HeatBuckets.Length; i++)
                {
                    HeatBuckets[i].Minute = -1;
                    HeatBuckets[i].Maps.Clear();
                }
            }
        }

        // ---- map image ---------------------------------------------------------------------------------------------------

        /// <summary>PNG per MapInfo.Index; cleared on WorldStarted (maps are reloaded from disk).</summary>
        private static readonly ConcurrentDictionary<int, byte[]> MapImages = new ConcurrentDictionary<int, byte[]>();

        internal static byte[] CachedImage(int index) => MapImages.TryGetValue(index, out var png) ? png : null;

        internal static void CacheImage(int index, byte[] png)
        {
            if (png != null) MapImages[index] = png;
        }

        /// <summary>Game thread. The first loaded instance of the map index; one W x H copy (cached afterwards).</summary>
        internal static MonitorMapCells CopyCells(int index)
        {
            var mapList = Envir.Main.MapList;
            Map map = null;
            for (var i = 0; i < mapList.Count; i++)
            {
                if (mapList[i]?.Info != null && mapList[i].Info.Index == index)
                {
                    map = mapList[i];
                    break;
                }
            }

            if (map?.CellData == null || map.Width <= 0 || map.Height <= 0) return null;

            var w = map.Width;
            var h = map.Height;
            var cellData = map.CellData; // same x-major layout; drop the fishing bits
            var attrs = new byte[cellData.Length];
            for (var i = 0; i < attrs.Length; i++)
                attrs[i] = (byte)(cellData[i] & Map.CellAttributeMask);

            var zones = map.Info.SafeZones;
            var sz = new int[zones.Count * 3];
            for (var i = 0; i < zones.Count; i++)
            {
                sz[i * 3] = zones[i].Location.X;
                sz[i * 3 + 1] = zones[i].Location.Y;
                sz[i * 3 + 2] = zones[i].Size;
            }

            return new MonitorMapCells { Index = index, Width = w, Height = h, Attributes = attrs, SafeZones = sz };
        }

        // Palette indexes of the map image.
        internal const byte PxNoFloor = 0, PxLowWall = 1, PxWalk = 2, PxSafe = 3;

        private static readonly byte[] Palette =
        {
            12, 14, 19,     // no floor / high wall
            38, 43, 54,     // low wall
            70, 79, 98,     // walkable
            52, 102, 78,    // walkable, inside a safe zone
        };

        /// <summary>Pure (HTTP thread): one palette pixel per cell, row-major (y, then x).</summary>
        internal static byte[] Pixels(MonitorMapCells c)
        {
            var w = c.Width;
            var h = c.Height;
            var px = new byte[w * h];

            for (var x = 0; x < w; x++)
            {
                for (var y = 0; y < h; y++)
                {
                    var a = (CellAttribute)c.Attributes[x * h + y];
                    px[y * w + x] = a == CellAttribute.Walk ? PxWalk : a == CellAttribute.LowWall ? PxLowWall : PxNoFloor;
                }
            }

            var z = c.SafeZones ?? Array.Empty<int>();
            for (var i = 0; i + 2 < z.Length; i += 3)
            {
                var x0 = Math.Max(0, z[i] - z[i + 2]);
                var x1 = Math.Min(w - 1, z[i] + z[i + 2]);
                var y0 = Math.Max(0, z[i + 1] - z[i + 2]);
                var y1 = Math.Min(h - 1, z[i + 1] + z[i + 2]);
                for (var y = y0; y <= y1; y++)
                    for (var x = x0; x <= x1; x++)
                        if (px[y * w + x] == PxWalk) px[y * w + x] = PxSafe;
            }

            return px;
        }

        /// <summary>Pure: an 8-bit palette PNG of the map cells.</summary>
        internal static byte[] EncodePng(MonitorMapCells c) => EncodePalettePng(c.Width, c.Height, Pixels(c), Palette);

        /// <summary>Pure: minimal 8-bit indexed PNG (IHDR, PLTE, one zlib IDAT, IEND), filter 0 on every row.</summary>
        internal static byte[] EncodePalettePng(int width, int height, byte[] pixels, byte[] paletteRgb)
        {
            if (width <= 0 || height <= 0 || pixels == null || pixels.Length != width * height) throw new ArgumentException("Bad image size");

            using var ms = new MemoryStream();
            ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

            var ihdr = new byte[13];
            WriteBE(ihdr, 0, width);
            WriteBE(ihdr, 4, height);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 3;  // colour type: indexed
            WriteChunk(ms, "IHDR", ihdr);
            WriteChunk(ms, "PLTE", paletteRgb);

            byte[] idat;
            using (var raw = new MemoryStream())
            {
                using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
                {
                    for (var y = 0; y < height; y++)
                    {
                        z.WriteByte(0);
                        z.Write(pixels, y * width, width);
                    }
                }
                idat = raw.ToArray();
            }

            WriteChunk(ms, "IDAT", idat);
            WriteChunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        /// <summary>Pure: a truecolour PNG from BGRA pixels (RGBA when <paramref name="alpha"/>, else RGB), filter 0, fast zlib.</summary>
        internal static byte[] EncodeBgraPng(int width, int height, byte[] bgra, bool alpha)
        {
            if (width <= 0 || height <= 0 || bgra == null || bgra.Length != width * height * 4) throw new ArgumentException("Bad image size");

            using var ms = new MemoryStream();
            ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

            var ihdr = new byte[13];
            WriteBE(ihdr, 0, width);
            WriteBE(ihdr, 4, height);
            ihdr[8] = 8;                     // bit depth
            ihdr[9] = (byte)(alpha ? 6 : 2); // colour type: RGBA / RGB
            WriteChunk(ms, "IHDR", ihdr);

            var bpp = alpha ? 4 : 3;
            var row = new byte[1 + width * bpp];
            byte[] idat;
            using (var raw = new MemoryStream())
            {
                using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
                {
                    for (var y = 0; y < height; y++)
                    {
                        row[0] = 0;
                        var si = y * width * 4;
                        var di = 1;
                        for (var x = 0; x < width; x++, si += 4)
                        {
                            row[di++] = bgra[si + 2];
                            row[di++] = bgra[si + 1];
                            row[di++] = bgra[si];
                            if (alpha) row[di++] = bgra[si + 3];
                        }
                        z.Write(row, 0, row.Length);
                    }
                }
                idat = raw.ToArray();
            }

            WriteChunk(ms, "IDAT", idat);
            WriteChunk(ms, "IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        private static void WriteBE(byte[] b, int at, int v)
        {
            b[at] = (byte)(v >> 24);
            b[at + 1] = (byte)(v >> 16);
            b[at + 2] = (byte)(v >> 8);
            b[at + 3] = (byte)v;
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBE(len, 0, data.Length);
            s.Write(len, 0, 4);

            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);

            var crc = Crc32(Crc32(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
            var c = new byte[4];
            WriteBE(c, 0, unchecked((int)crc));
            s.Write(c, 0, 4);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        internal static uint Crc32(uint crc, byte[] data)
        {
            for (var i = 0; i < data.Length; i++) crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}

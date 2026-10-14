using System.Collections.Concurrent;
using Server.MirEnvir;

namespace Server.WebAdmin.MapArt
{
    /// <summary>What the game thread tells the renderer about one map index (first loaded instance).</summary>
    internal sealed class MapArtMapInfo
    {
        public int Index;
        public string FileName;
        public int MiniMap;
        public int Width, Height;
    }

    /// <summary>A frame scaled down by 2^z, premultiplied BGRA.</summary>
    internal sealed class ScaledFrame
    {
        public int W, H;           // scaled size
        public short X, Y;         // full-resolution draw offsets (MImage.X / Y)
        public short FullW, FullH; // full-resolution size (the client's GetSize)
        public byte[] Px;
    }

    /// <summary>
    /// Live Monitor map art, read from a game client's Data folder (<c>[WebAdmin] ClientDataPath</c>, read-only):
    /// the authored minimap of a map (<c>MMap.Lib</c> frame <c>MapInfo.MiniMap</c>, as the client's minimap dialog draws it) and
    /// real map tiles: 768 x 512 px PNGs covering 16·2^z x 16·2^z cells at 1/2^z scale (z = 0..3), drawn with the client's floor
    /// and object rules (Client/MirScenes/GameScene.cs <c>MapControl.DrawFloor</c> / <c>DrawObjects</c>, animation frame 0, doors
    /// closed, no lights or weather) from the server's own <c>Maps\*.map</c> file parsed with the client parser.
    ///
    /// Threads: HTTP threads only (never the game thread; map info comes from one <see cref="WebAdminGame.RunAsync{T}"/> per
    /// index). Renders are serialised by <see cref="RenderGate"/> so map art never takes more than one core from the game server;
    /// identical concurrent requests share one render. Caches are bounded: decoded frames ~128 MB, tile PNGs ~192 MB, parsed maps 4.
    /// </summary>
    internal static class MapArtService
    {
        public const int CellW = 48, CellH = 32;
        public const int MaxZoom = 3;
        public const int TileCellsAtZ0 = 16;
        public const int TilePxW = TileCellsAtZ0 * CellW, TilePxH = TileCellsAtZ0 * CellH;   // 768 x 512 at every z

        private const long FrameBudgetBytes = 128L * 1024 * 1024;
        private const long TileBudgetBytes = 192L * 1024 * 1024;
        private const int MaxMaps = 4;
        private const int RenderWaitMs = 20000;

        private static readonly object LibGate = new object();
        private static string _libRoot;
        private static LibFile[] _libs = new LibFile[MapLibCatalog.Size];
        private static LibFile _miniMaps;

        private static readonly SemaphoreSlim RenderGate = new SemaphoreSlim(1, 1);
        private static readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> InFlight = new ConcurrentDictionary<string, Lazy<Task<byte[]>>>();
        private static readonly ConcurrentDictionary<int, MapArtMapInfo> MapInfos = new ConcurrentDictionary<int, MapArtMapInfo>();
        private static readonly ConcurrentDictionary<int, byte[]> MiniMapPngs = new ConcurrentDictionary<int, byte[]>();

        private static readonly ByteLru<string, byte[]> Tiles = new ByteLru<string, byte[]>(TileBudgetBytes, b => b.Length);
        private static readonly ByteLru<long, ScaledFrame> Frames = new ByteLru<long, ScaledFrame>(FrameBudgetBytes, f => (f.Px?.Length ?? 0) + 64);

        private static readonly object MapGate = new object();
        private static readonly LinkedList<(string key, ClientMapData data)> Maps = new LinkedList<(string, ClientMapData)>();

        private static int _hooked;
        private static (DateTime at, bool minimap, bool tiles, string root) _status;

        internal static void EnsureHooked()
        {
            if (Interlocked.Exchange(ref _hooked, 1) != 0) return;
            WebAdminHooks.WorldStarted += () => MapInfos.Clear();
        }

        /// <summary>The configured client Data folder (process-restart setting), or null.</summary>
        public static string Root => WebAdminOptions.Current?.ClientDataPath;

        /// <summary>(minimap available, tiles available) for the configured folder; re-checked every 30 s.</summary>
        public static (bool minimap, bool tiles) Status()
        {
            var root = Root;
            var s = _status;
            if (s.root == root && (DateTime.UtcNow - s.at).TotalSeconds < 30) return (s.minimap, s.tiles);

            var mini = false;
            var tiles = false;
            if (!string.IsNullOrEmpty(root))
            {
                try
                {
                    mini = File.Exists(Path.Combine(root, "MMap.Lib"));
                    tiles = Directory.Exists(Path.Combine(root, "Map"));
                }
                catch (Exception)
                {
                    // unreadable path: no art
                }
            }

            _status = (DateTime.UtcNow, mini, tiles, root);
            return (mini, tiles);
        }

        // ---- libraries ---------------------------------------------------------------------------------------------------

        private static void EnsureRoot(string root)
        {
            if (_libRoot == root) return;
            for (var i = 0; i < _libs.Length; i++) _libs[i]?.Dispose();
            _miniMaps?.Dispose();
            _libs = new LibFile[MapLibCatalog.Size];
            _miniMaps = null;
            _libRoot = root;
            Frames.Clear();
            Tiles.Clear();
            MiniMapPngs.Clear();
        }

        private static LibFile Lib(int index)
        {
            var root = Root;
            if (string.IsNullOrEmpty(root) || index < 0 || index >= MapLibCatalog.Size) return null;
            lock (LibGate)
            {
                EnsureRoot(root);
                var lib = _libs[index];
                if (lib == null)
                {
                    var rel = MapLibCatalog.RelativePath(index);
                    if (rel == null) return null;
                    lib = new LibFile(Path.Combine(root, rel + ".Lib"));
                    _libs[index] = lib;
                }
                return lib;
            }
        }

        private static LibFile MiniMapLib()
        {
            var root = Root;
            if (string.IsNullOrEmpty(root)) return null;
            lock (LibGate)
            {
                EnsureRoot(root);
                return _miniMaps ??= new LibFile(Path.Combine(root, "MMap.Lib"));
            }
        }

        // ---- map info (game thread, once per index per world run) --------------------------------------------------------

        public static async Task<MapArtMapInfo> GetMapInfoAsync(int index)
        {
            if (MapInfos.TryGetValue(index, out var cached)) return cached;
            if (!WebAdminGame.IsRunning) throw new WorldNotRunningException(WebAdminGame.WorldState);

            var info = await WebAdminGame.RunAsync(() =>
            {
                var list = Envir.Main.MapList;
                for (var i = 0; i < list.Count; i++)
                {
                    var m = list[i];
                    if (m?.Info == null || m.Info.Index != index) continue;
                    return new MapArtMapInfo { Index = index, FileName = m.Info.FileName, MiniMap = m.Info.MiniMap, Width = m.Width, Height = m.Height };
                }
                return null;
            }, 5000).ConfigureAwait(false);

            if (info == null) throw new WebAdminException(404, "Map #" + index + " is not loaded");
            MapInfos[index] = info;
            return info;
        }

        // ---- minimap -----------------------------------------------------------------------------------------------------

        /// <summary>HTTP thread. The authored minimap as an RGBA PNG, or null when the map has none or the library lacks it.</summary>
        public static byte[] MiniMapPng(int miniMapIndex)
        {
            if (miniMapIndex <= 0) return null;
            if (MiniMapPngs.TryGetValue(miniMapIndex, out var png)) return png;

            var lib = MiniMapLib();
            if (lib == null) return null;
            var px = lib.ReadPixels(miniMapIndex, out var info);
            if (px == null) return null;

            var opaque = true;
            for (var i = 3; i < px.Length && opaque; i += 4) opaque = px[i] == 255;
            png = WebAdminMonitor.EncodeBgraPng(info.Width, info.Height, px, alpha: !opaque);
            MiniMapPngs[miniMapIndex] = png;
            return png;
        }

        // ---- tiles -------------------------------------------------------------------------------------------------------

        public static int TileCells(int z) => TileCellsAtZ0 << z;

        /// <summary>Server map file of a MapInfo.FileName (Settings.MapPath, relative to the server's working directory).</summary>
        private static string MapFilePath(string fileName)
        {
            string mapPath;
            lock (WebAdminOptions.SettingsLock) mapPath = Settings.MapPath;
            return Path.GetFullPath(Path.Combine(mapPath, fileName + ".map"));
        }

        /// <summary>HTTP thread. One tile PNG (cached; concurrent identical requests share one render).</summary>
        public static async Task<byte[]> TilePngAsync(MapArtMapInfo map, int z, int tx, int ty)
        {
            if (z < 0 || z > MaxZoom) throw new WebAdminException(400, "z must be 0.." + MaxZoom);
            var cells = TileCells(z);
            if (tx < 0 || ty < 0 || tx * cells >= map.Width || ty * cells >= map.Height) throw new WebAdminException(404, "Tile outside the map");
            if (string.IsNullOrEmpty(Root)) throw new WebAdminException(404, "Map art is not configured ([WebAdmin] ClientDataPath)");

            var file = MapFilePath(map.FileName);
            DateTime stamp;
            try
            {
                stamp = File.GetLastWriteTimeUtc(file);
            }
            catch (Exception)
            {
                throw new WebAdminException(404, "Map file not found");
            }

            var key = file + "|" + stamp.Ticks + "|" + z + "|" + tx + "|" + ty;
            if (Tiles.TryGet(key, out var hit)) return hit;

            var lazy = InFlight.GetOrAdd(key, k => new Lazy<Task<byte[]>>(() => RenderTileAsync(k, file, stamp, z, tx, ty)));
            try
            {
                return await lazy.Value.ConfigureAwait(false);
            }
            finally
            {
                InFlight.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]>>>(key, lazy));
            }
        }

        private static async Task<byte[]> RenderTileAsync(string key, string file, DateTime stamp, int z, int tx, int ty)
        {
            if (!await RenderGate.WaitAsync(RenderWaitMs).ConfigureAwait(false))
                throw new WebAdminException(503, "Map art renderer is busy, try again");

            try
            {
                if (Tiles.TryGet(key, out var hit)) return hit;

                var data = LoadMap(file, stamp);
                if (data == null) throw new WebAdminException(404, "Map file not found or unreadable");

                var bgra = RenderTile(data, z, tx, ty);
                var png = WebAdminMonitor.EncodeBgraPng(TilePxW, TilePxH, bgra, alpha: false);
                Tiles.Add(key, png);
                return png;
            }
            finally
            {
                RenderGate.Release();
            }
        }

        /// <summary>
        /// HTTP thread. The parsed server map file of a MapInfo.FileName (shared parse cache with the tiles), with its path and
        /// write stamp, or null when the file is missing or unreadable. Needs no client Data folder and no running world.
        /// </summary>
        internal static (ClientMapData data, string file, DateTime stamp) MapFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return (null, null, default);
            string file;
            DateTime stamp;
            try
            {
                file = MapFilePath(fileName);
                if (!File.Exists(file)) return (null, file, default);
                stamp = File.GetLastWriteTimeUtc(file);
            }
            catch (Exception)
            {
                return (null, null, default);
            }
            return (LoadMap(file, stamp), file, stamp);
        }

        private static ClientMapData LoadMap(string file, DateTime stamp)
        {
            var key = file + "|" + stamp.Ticks;
            lock (MapGate)
            {
                for (var n = Maps.First; n != null; n = n.Next)
                {
                    if (n.Value.key != key) continue;
                    Maps.Remove(n);
                    Maps.AddFirst(n);
                    return n.Value.data;
                }
            }

            if (!File.Exists(file)) return null;
            ClientMapData data;
            try
            {
                data = new ClientMapData(File.ReadAllBytes(file));
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Live monitor: cannot parse map file " + file);
                return null;
            }

            if (data.Error != null) WebAdminLog.Info("Live monitor: map file " + file + " parsed with an error (" + data.Error + "); drawing what was read");

            lock (MapGate)
            {
                Maps.AddFirst((key, data));
                while (Maps.Count > MaxMaps) Maps.RemoveLast();
            }
            return data;
        }

        /// <summary>Pure apart from the library reads: the tile's BGRA pixels (opaque, TilePxW x TilePxH).</summary>
        internal static byte[] RenderTile(ClientMapData map, int z, int tx, int ty)
        {
            var c = new TileCanvas(z, tx, ty);
            var cells = map.MapCells;
            var w = map.Width;
            var h = map.Height;
            var n = TileCells(z);
            int cx0 = tx * n, cy0 = ty * n, cx1 = cx0 + n, cy1 = cy0 + n;

            // Floor (MapControl.DrawFloor): back tiles on even cells, flat middle and front tiles (1 x 1 or 2 x 2 cells).
            for (var y = Math.Max(1, cy0 - 1); y < Math.Min(h, cy1); y++)
            {
                var drawY = y * CellH;
                for (var x = Math.Max(0, cx0 - 1); x < Math.Min(w, cx1); x++)
                {
                    var drawX = x * CellW;
                    ref var cell = ref cells[x, y];

                    if (y % 2 == 0 && x % 2 == 0 && cell.BackImage != 0 && cell.BackIndex != -1)
                        c.Draw(cell.BackIndex, (cell.BackImage & 0x1FFFFFFF) - 1, drawX, drawY, false);

                    var mid = cell.MiddleImage - 1;
                    if (mid >= 0 && cell.MiddleIndex != -1)
                    {
                        var f = c.Info(cell.MiddleIndex, mid);
                        if (IsFlat(f)) c.Draw(cell.MiddleIndex, mid, drawX, drawY, false);
                    }

                    var front = (cell.FrontImage & 0x7FFF) - 1;
                    if (front >= 0 && cell.FrontIndex != -1 && cell.FrontIndex != 200)
                    {
                        var f = c.Info(cell.FrontIndex, front);
                        if (IsFlat(f)) c.Draw(cell.FrontIndex, front, drawX, drawY, false);
                    }
                }
            }

            // Objects (MapControl.DrawObjects), row by row; rows below the tile and cells to its left can reach into it.
            for (var y = Math.Max(1, cy0); y < Math.Min(h, cy1 + 25); y++)
            {
                var drawY = (y + 1) * CellH;
                for (var x = Math.Max(0, cx0 - 10); x < Math.Min(w, cx1); x++)
                {
                    var drawX = x * CellW;
                    ref var cell = ref cells[x, y];

                    // Shanda tile animation layer (frame 0)
                    if (cell.TileAnimationImage > 0 && cell.TileAnimationFrames > 0)
                    {
                        var ti = cell.TileAnimationImage - 1;
                        var f = c.Info(190, ti);
                        c.Draw(190, ti, drawX, drawY - f.FullH, false);
                    }

                    // middle layer
                    if (cell.MiddleIndex >= 0)
                    {
                        var index = cell.MiddleImage - 1;
                        if (index > 0)
                        {
                            var animation = cell.MiddleAnimationFrame;
                            var blend = false;
                            var drawn = false;
                            var f = c.Info(cell.MiddleIndex, index);
                            if (animation > 0 && animation < 255)
                            {
                                if ((animation & 0x0f) > 0)
                                {
                                    blend = true;
                                    animation &= 0x0f;
                                }
                                if (animation > 0)
                                {
                                    c.Draw(cell.MiddleIndex, index, drawX, drawY - f.FullH, blend && (animation == 10 || animation == 8));
                                    drawn = true;
                                }
                            }
                            if (!IsFlat(f) && !blend && !drawn) c.Draw(cell.MiddleIndex, index, drawX, drawY - f.FullH, false);
                        }
                    }

                    // front layer
                    var fi = (cell.FrontImage & 0x7FFF) - 1;
                    if (fi < 0 || cell.FrontIndex == -1) continue;
                    int fileIndex = cell.FrontIndex;
                    var anim = cell.FrontAnimationFrame;
                    var fblend = (anim & 0x80) > 0;
                    if (fblend) anim &= 0x7F;

                    var fs = c.Info(fileIndex, fi);
                    if (fs.FullW <= 0) continue;
                    if (IsFlat(fs) && anim == 0) continue;

                    if (fblend)
                    {
                        if (fileIndex == 14 || fileIndex == 27 || (fileIndex > 99 && fileIndex < 199))
                            c.Draw(fileIndex, fi, drawX + fs.X, drawY - 3 * CellH + fs.Y, true);
                        else if (fi >= 2723 && fi <= 2732)
                            c.Draw(fileIndex, fi, drawX + fs.X, drawY - fs.FullH + fs.Y, true);
                        else
                            c.Draw(fileIndex, fi, drawX, drawY - fs.FullH, true);
                    }
                    else if (fileIndex == 28 && (fs.X != 0 || fs.Y != 0))
                    {
                        c.Draw(fileIndex, fi, drawX + fs.X, drawY - CellH + fs.Y, false);
                    }
                    else
                    {
                        c.Draw(fileIndex, fi, drawX, drawY - fs.FullH, false);
                    }
                }
            }

            return c.Pixels;
        }

        private static bool IsFlat(ScaledFrame f) =>
            f != null && ((f.FullW == CellW && f.FullH == CellH) || (f.FullW == CellW * 2 && f.FullH == CellH * 2));

        /// <summary>The scaled frame of (library, index) at zoom z, cached; null when missing.</summary>
        private static ScaledFrame Frame(int lib, int index, int z)
        {
            if (index < 0) return null;
            var key = ((long)lib << 40) | ((long)(uint)index << 8) | (uint)z;
            if (Frames.TryGet(key, out var f)) return f;

            var file = Lib(lib);
            if (file == null) return null;
            var px = file.ReadPixels(index, out var info);
            f = Scale(px, info, z);
            Frames.Add(key, f);
            return f;
        }

        /// <summary>Box-filters a straight-alpha BGRA frame by 2^z into premultiplied BGRA (pure).</summary>
        internal static ScaledFrame Scale(byte[] px, LibFrameInfo info, int z)
        {
            var f = new ScaledFrame { X = info.X, Y = info.Y, FullW = info.Width, FullH = info.Height };
            if (px == null || info.IsEmpty) return f;

            var s = 1 << z;
            int w = info.Width, h = info.Height;
            int sw = (w + s - 1) / s, sh = (h + s - 1) / s;
            var o = new byte[sw * sh * 4];

            for (var oy = 0; oy < sh; oy++)
            {
                for (var ox = 0; ox < sw; ox++)
                {
                    int b = 0, g = 0, r = 0, a = 0, n = 0;
                    for (var yy = oy * s; yy < Math.Min(h, oy * s + s); yy++)
                    {
                        var row = yy * w * 4;
                        for (var xx = ox * s; xx < Math.Min(w, ox * s + s); xx++)
                        {
                            var i = row + xx * 4;
                            int pa = px[i + 3];
                            b += px[i] * pa;
                            g += px[i + 1] * pa;
                            r += px[i + 2] * pa;
                            a += pa;
                            n++;
                        }
                    }

                    var oi = (oy * sw + ox) * 4;
                    if (n == 0 || a == 0) continue;
                    o[oi] = (byte)(b / (255 * n));
                    o[oi + 1] = (byte)(g / (255 * n));
                    o[oi + 2] = (byte)(r / (255 * n));
                    o[oi + 3] = (byte)(a / n);
                }
            }

            f.W = sw;
            f.H = sh;
            f.Px = o;
            return f;
        }

        /// <summary>One tile's opaque BGRA buffer; positions are full-resolution map pixels.</summary>
        private sealed class TileCanvas
        {
            private readonly int _z, _s, _ox, _oy;
            public readonly byte[] Pixels = new byte[TilePxW * TilePxH * 4];

            public TileCanvas(int z, int tx, int ty)
            {
                _z = z;
                _s = 1 << z;
                _ox = tx * TilePxW;
                _oy = ty * TilePxH;
                for (var i = 3; i < Pixels.Length; i += 4) Pixels[i] = 255;
            }

            public ScaledFrame Info(int lib, int index) => Frame(lib, index, _z) ?? Empty;

            private static readonly ScaledFrame Empty = new ScaledFrame();

            public void Draw(int lib, int index, int mapX, int mapY, bool additive)
            {
                var f = Frame(lib, index, _z);
                if (f?.Px == null) return;

                var dx = FloorDiv(mapX, _s) - _ox;
                var dy = FloorDiv(mapY, _s) - _oy;
                if (dx >= TilePxW || dy >= TilePxH || dx + f.W <= 0 || dy + f.H <= 0) return;

                int x0 = Math.Max(0, -dx), y0 = Math.Max(0, -dy);
                int x1 = Math.Min(f.W, TilePxW - dx), y1 = Math.Min(f.H, TilePxH - dy);
                var src = f.Px;
                var dst = Pixels;

                for (var y = y0; y < y1; y++)
                {
                    var si = (y * f.W + x0) * 4;
                    var di = ((dy + y) * TilePxW + dx + x0) * 4;
                    for (var x = x0; x < x1; x++, si += 4, di += 4)
                    {
                        int a = src[si + 3];
                        if (a == 0) continue;
                        if (additive)
                        {
                            dst[di] = (byte)Math.Min(255, dst[di] + src[si]);
                            dst[di + 1] = (byte)Math.Min(255, dst[di + 1] + src[si + 1]);
                            dst[di + 2] = (byte)Math.Min(255, dst[di + 2] + src[si + 2]);
                        }
                        else if (a == 255)
                        {
                            dst[di] = src[si];
                            dst[di + 1] = src[si + 1];
                            dst[di + 2] = src[si + 2];
                        }
                        else
                        {
                            var ia = 255 - a;
                            dst[di] = (byte)(src[si] + dst[di] * ia / 255);
                            dst[di + 1] = (byte)(src[si + 1] + dst[di + 1] * ia / 255);
                            dst[di + 2] = (byte)(src[si + 2] + dst[di + 2] * ia / 255);
                        }
                    }
                }
            }

            private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
        }
    }

    /// <summary>A small thread-safe LRU bounded by a byte budget.</summary>
    internal sealed class ByteLru<TKey, TValue>
    {
        private readonly object _gate = new object();
        private readonly Dictionary<TKey, LinkedListNode<(TKey key, TValue value, long size)>> _map = new Dictionary<TKey, LinkedListNode<(TKey, TValue, long)>>();
        private readonly LinkedList<(TKey key, TValue value, long size)> _order = new LinkedList<(TKey, TValue, long)>();
        private readonly long _budget;
        private readonly Func<TValue, long> _sizeOf;
        private long _bytes;

        public ByteLru(long budget, Func<TValue, long> sizeOf)
        {
            _budget = budget;
            _sizeOf = sizeOf;
        }

        public long Bytes
        {
            get
            {
                lock (_gate) return _bytes;
            }
        }

        public bool TryGet(TKey key, out TValue value)
        {
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    value = node.Value.value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        public void Add(TKey key, TValue value)
        {
            var size = Math.Max(1, _sizeOf(value));
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var old))
                {
                    _order.Remove(old);
                    _bytes -= old.Value.size;
                }

                var node = _order.AddFirst((key, value, size));
                _map[key] = node;
                _bytes += size;

                while (_bytes > _budget && _order.Count > 1)
                {
                    var last = _order.Last;
                    _order.RemoveLast();
                    _map.Remove(last.Value.key);
                    _bytes -= last.Value.size;
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _map.Clear();
                _order.Clear();
                _bytes = 0;
            }
        }
    }
}

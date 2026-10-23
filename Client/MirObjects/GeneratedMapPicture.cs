using System.Runtime.InteropServices;
using Client.MirGraphics;
using Client.MirScenes;
using SlimDX;

namespace Client.MirObjects
{
    /// <summary>
    /// The minimap / big map picture of a generated dungeon (<see cref="GeneratedMaps"/>). MMap.Lib has no art for a map made
    /// at run time (the server sends MiniMap = BigMap = 0), so the client draws one from the cells, <see cref="CellPixels"/>
    /// px a cell:
    ///  - colours from the map's own art: each floor cell takes the average colour of its floor tile (Back, else Middle), the
    ///    walls the average of the wall objects (Front) beside the floor, the void a dark shade of that; lifted together when
    ///    the dungeon's art is too dark to read on a minimap;
    ///  - smooth edges: a floor field (<see cref="Field"/>: the walkable grid blurred wide, corridors and pillars put back),
    ///    sampled bilinearly per pixel and cut at <see cref="FloorEdge"/> (floor) and <see cref="WallEdge"/> (the wall band,
    ///    lighter towards the floor), each cut anti-aliased over about two pixels. Floor colours are blended between cells.
    /// It stands in for MMap.Lib[MiniMap / BigMap] in the minimap and big map (classic and modern), with the same draw calls.
    /// Built once per map in <see cref="MapControl.LoadMap"/>; the pixels stay on the CPU and the texture
    /// (<see cref="DXManager.GeneratedMapTexture"/>, released with the other device statics in CleanUp) is re-created from them
    /// on the first draw after a device reset. Same rules as the web client's game/cellPicture.ts. UI thread only.
    /// </summary>
    static class GeneratedMapPicture
    {
        private const int MaxSide = 2048, MaxCellPixels = 3;
        private const int CorridorLength = 8;
        private const float Blur = 3F, Rescue = 1.8F, FloorEdge = 0.4F, WallEdge = 0.12F, TargetLuma = 140F, MaxLift = 2.5F;

        // Used when the art cannot be read (a missing library); also the old flat look.
        private static readonly Vector3 DefaultFloor = new Vector3(150, 138, 112), DefaultWall = new Vector3(72, 64, 54);

        private static int _mapIndex = -1;
        private static int[] _pixels;

        /// <summary>The picture's size in px (Empty when there is none), as Libraries.MiniMap.GetSize would give it.</summary>
        public static Size Size { get; private set; }

        /// <summary>Picture px per cell (1-3: the larger side stays within 2048 px).</summary>
        public static int CellPixels { get; private set; }

        /// <summary>The picture belongs to this map (a generated dungeon, loaded).</summary>
        public static bool IsFor(MapControl map) => map != null && _pixels != null && map.Index == _mapIndex;

        /// <summary>Draws the picture of the map just loaded from its cells (MapControl.LoadMap of a generated map).</summary>
        public static void Build(MapControl map)
        {
            Clear();
            CellInfo[,] cells = map.M2CellInfo;
            int w = map.Width, h = map.Height;
            if (cells == null || w <= 0 || h <= 0) return;

            int px = Math.Clamp(MaxSide / Math.Max(w, h), 1, MaxCellPixels);
            if (w * px > MaxSide || h * px > MaxSide) return; // larger than any generator profile makes

            var walk = new bool[w, h];
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    walk[x, y] = (cells[x, y].BackImage & 0x20000000) == 0 && (cells[x, y].FrontImage & 0x8000) == 0;

            float[,] field = Field(walk, w, h);
            Vector3[,] floor = Palette(cells, walk, w, h, out Vector3 wall);
            Vector3 voidColour = wall * 0.2F;

            int stride = w * px;
            var pixels = new int[stride * h * px];
            float aa = 0.15F / px; // about a pixel either side of each cut at the wide blur's slope
            for (int py = 0; py < h * px; py++)
            {
                float v = (py + 0.5F) / px - 0.5F;
                int y0 = (int)MathF.Floor(v);
                float fy = v - y0;
                for (int pxl = 0; pxl < stride; pxl++)
                {
                    float u = (pxl + 0.5F) / px - 0.5F;
                    int x0 = (int)MathF.Floor(u);
                    float fx = u - x0;

                    float f = Lerp(Lerp(At(field, w, h, x0, y0), At(field, w, h, x0 + 1, y0), fx),
                                   Lerp(At(field, w, h, x0, y0 + 1), At(field, w, h, x0 + 1, y0 + 1), fx), fy);
                    float wallT = SmoothStep(WallEdge - aa, WallEdge + aa, f);
                    float floorT = SmoothStep(FloorEdge - aa, FloorEdge + aa, f);

                    float shade = Math.Clamp((f - WallEdge) / (FloorEdge - WallEdge), 0F, 1F);
                    Vector3 c = Vector3.Lerp(voidColour, wall * (0.75F + 0.25F * shade), wallT);
                    if (floorT > 0)
                    {
                        Vector3 floorColour = Vector3.Lerp(Vector3.Lerp(At(floor, w, h, x0, y0), At(floor, w, h, x0 + 1, y0), fx),
                                                           Vector3.Lerp(At(floor, w, h, x0, y0 + 1), At(floor, w, h, x0 + 1, y0 + 1), fx), fy);
                        c = Vector3.Lerp(c, floorColour, floorT);
                    }

                    pixels[py * stride + pxl] = unchecked((int)0xFF000000) | (Byte(c.X) << 16) | (Byte(c.Y) << 8) | Byte(c.Z);
                }
            }

            _pixels = pixels;
            _mapIndex = map.Index;
            CellPixels = px;
            Size = new Size(w * px, h * px);
        }

        /// <summary>Forgets the picture (a map with MMap art, or none).</summary>
        public static void Clear()
        {
            _pixels = null;
            _mapIndex = -1;
            CellPixels = 0;
            Size = Size.Empty;
            if (DXManager.GeneratedMapTexture != null)
            {
                if (!DXManager.GeneratedMapTexture.Disposed) DXManager.GeneratedMapTexture.Dispose();
                DXManager.GeneratedMapTexture = null;
            }
        }

        /// <summary>
        /// The floor field the picture is cut from (1 = floor, 0 = deep in the wall). The walkable grid is blurred wide
        /// (<see cref="Blur"/> cells), which smooths away the saw-teeth that wall pieces leave on diagonal walls (a tooth every
        /// 8 cells, up to 7 wide). A wide blur also fades what is thinner than it, so two kinds of shape are put back sharp
        /// (blurred 1 cell): corridors, i.e. walkable cells the wide blur dropped that run at least <see cref="CorridorLength"/>
        /// cells (the cut-off tips of teeth are shorter), and pillars, i.e. blocked cells that do not reach the map's edge.
        /// </summary>
        private static float[,] Field(bool[,] walk, int w, int h)
        {
            var floor = new float[w, h];
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    floor[x, y] = walk[x, y] ? 1F : 0F;
            float[,] field = Gauss(floor, w, h, Blur);

            var lost = new bool[w, h];
            var blocked = new bool[w, h];
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    lost[x, y] = walk[x, y] && field[x, y] < FloorEdge;
                    blocked[x, y] = !walk[x, y];
                }
            float[,] corridors = Gauss(Components(lost, w, h, (x0, y0, x1, y1) => Math.Max(x1 - x0, y1 - y0) + 1 >= CorridorLength), w, h, 1F);
            float[,] pillars = Gauss(Components(blocked, w, h, (x0, y0, x1, y1) => x0 > 0 && y0 > 0 && x1 < w - 1 && y1 < h - 1), w, h, 1F);

            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    float f = Math.Max(field[x, y], Math.Min(1F, corridors[x, y] * Rescue));
                    field[x, y] = Math.Min(f, 1F - Math.Min(1F, pillars[x, y] * Rescue));
                }
            return field;
        }

        /// <summary>A mask of the 8-connected groups of set cells whose bounds (x0, y0, x1, y1) <paramref name="keep"/> accepts.</summary>
        private static float[,] Components(bool[,] set, int w, int h, Func<int, int, int, int, bool> keep)
        {
            var mask = new float[w, h];
            var seen = new bool[w, h];
            var stack = new Stack<int>();
            var group = new List<int>();
            for (int sx = 0; sx < w; sx++)
                for (int sy = 0; sy < h; sy++)
                {
                    if (!set[sx, sy] || seen[sx, sy]) continue;
                    group.Clear();
                    seen[sx, sy] = true;
                    stack.Push(sx * h + sy);
                    int x0 = sx, y0 = sy, x1 = sx, y1 = sy;
                    while (stack.Count > 0)
                    {
                        int c = stack.Pop(), x = c / h, y = c % h;
                        group.Add(c);
                        x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                        for (int dx = -1; dx <= 1; dx++)
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || ny < 0 || nx >= w || ny >= h || !set[nx, ny] || seen[nx, ny]) continue;
                                seen[nx, ny] = true;
                                stack.Push(nx * h + ny);
                            }
                    }
                    if (!keep(x0, y0, x1, y1)) continue;
                    for (int i = 0; i < group.Count; i++) mask[group[i] / h, group[i] % h] = 1F;
                }
            return mask;
        }

        /// <summary>A separable Gaussian blur (sigma in cells; outside the map counts as 0).</summary>
        private static float[,] Gauss(float[,] source, int w, int h, float sigma)
        {
            int r = (int)MathF.Ceiling(sigma * 3);
            var kernel = new float[r * 2 + 1];
            float sum = 0;
            for (int i = -r; i <= r; i++) sum += kernel[i + r] = MathF.Exp(-i * i / (2 * sigma * sigma));
            for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;

            var across = new float[w, h];
            var result = new float[w, h];
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    float a = 0;
                    for (int i = -r; i <= r; i++)
                        if (x + i >= 0 && x + i < w) a += source[x + i, y] * kernel[i + r];
                    across[x, y] = a;
                }
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    float a = 0;
                    for (int i = -r; i <= r; i++)
                        if (y + i >= 0 && y + i < h) a += across[x, y + i] * kernel[i + r];
                    result[x, y] = a;
                }
            return result;
        }

        /// <summary>Per-cell floor colours (blocked cells: the map's average floor, so edges blend into it) and the wall colour,
        /// averaged from the tiles the map draws, then lifted together towards <see cref="TargetLuma"/>.</summary>
        private static Vector3[,] Palette(CellInfo[,] cells, bool[,] walk, int w, int h, out Vector3 wall)
        {
            var averages = new Dictionary<long, Vector3?>();
            var floor = new Vector3[w, h];
            var known = new bool[w, h];
            Vector3 floorSum = Vector3.Zero, wallSum = Vector3.Zero;
            int floorCount = 0, wallCount = 0;

            try
            {
                for (int x = 0; x < w; x++)
                    for (int y = 0; y < h; y++)
                    {
                        if (walk[x, y])
                        {
                            // MapControl.DrawFloor: a back tile is drawn at even cells and covers 2 x 2; else the cell's middle tile.
                            CellInfo back = cells[x & ~1, y & ~1], cell = cells[x, y];
                            Vector3? c = back.BackImage != 0 && back.BackIndex != -1
                                ? Average(averages, back.BackIndex, (back.BackImage & 0x1FFFFFFF) - 1)
                                : null;
                            if (c == null && cell.MiddleIndex != -1) c = Average(averages, cell.MiddleIndex, cell.MiddleImage - 1);
                            if (c == null) continue;
                            floor[x, y] = c.Value;
                            known[x, y] = true;
                            floorSum += c.Value;
                            floorCount++;
                        }
                        else if (BesideFloor(walk, w, h, x, y))
                        {
                            CellInfo cell = cells[x, y];
                            if (cell.FrontIndex == -1 || cell.FrontIndex == 200) continue;
                            Vector3? c = Average(averages, cell.FrontIndex, (cell.FrontImage & 0x7FFF) - 1);
                            if (c == null) continue;
                            wallSum += c.Value;
                            wallCount++;
                        }
                    }
            }
            catch (Exception ex)
            {
                // A broken library: keep what was read, the rest takes the defaults.
                if (Settings.LogErrors) CMain.SaveError("[GeneratedMapPicture] " + ex);
            }

            Vector3 floorAverage = floorCount > 0 ? floorSum / floorCount : DefaultFloor;
            wall = wallCount > 0 ? wallSum / wallCount : DefaultWall;

            // Lift a dark dungeon so the minimap reads; keep the walls clearly darker than the floor.
            float lift = Math.Clamp(TargetLuma / Math.Max(1F, Luma(floorAverage)), 1F, MaxLift);
            floorAverage *= lift;
            wall *= lift;
            if (Luma(wall) > Luma(floorAverage) * 0.6F) wall *= Luma(floorAverage) * 0.55F / Luma(wall);

            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    floor[x, y] = known[x, y] ? floor[x, y] * lift : floorAverage;
            return floor;
        }

        /// <summary>The average colour of a map tile's opaque pixels (cached per library + index); null when unreadable.</summary>
        private static Vector3? Average(Dictionary<long, Vector3?> cache, int library, int index)
        {
            if (library < 0 || library >= Libraries.MapLibs.Length || index < 0) return null;
            long key = ((long)library << 32) | (uint)index;
            if (cache.TryGetValue(key, out Vector3? found)) return found;

            Vector3? result = null;
            MLibrary lib = Libraries.MapLibs[library];
            byte[] p = lib?.GetPixels(index, out _);
            if (p != null)
            {
                long r = 0, g = 0, b = 0, n = 0;
                for (int i = 0; i + 3 < p.Length; i += 4)
                {
                    if (p[i + 3] == 0) continue; // BGRA
                    b += p[i]; g += p[i + 1]; r += p[i + 2]; n++;
                }
                if (n > 0) result = new Vector3(r / (float)n, g / (float)n, b / (float)n);
            }
            cache[key] = result;
            return result;
        }

        private static bool BesideFloor(bool[,] walk, int w, int h, int x, int y)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx != 0 || dy != 0) && nx >= 0 && ny >= 0 && nx < w && ny < h && walk[nx, ny]) return true;
                }
            return false;
        }

        private static float At(float[,] a, int w, int h, int x, int y) => x < 0 || y < 0 || x >= w || y >= h ? 0F : a[x, y];
        private static Vector3 At(Vector3[,] a, int w, int h, int x, int y) => a[Math.Clamp(x, 0, w - 1), Math.Clamp(y, 0, h - 1)];
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
        private static float Luma(Vector3 c) => 0.299F * c.X + 0.587F * c.Y + 0.114F * c.Z;
        private static int Byte(float v) => (int)Math.Clamp(v + 0.5F, 0F, 255F);

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0F, 1F);
            return t * t * (3F - 2F * t);
        }

        /// <summary>The texture, made from the pixels on first use and again after a device reset dropped it.</summary>
        private static unsafe bool EnsureTexture()
        {
            if (_pixels == null) return false;
            var texture = DXManager.GeneratedMapTexture;
            if (texture != null && !texture.Disposed) return true;

            texture = DXManager.CreateTexture(Size.Width, Size.Height);
            byte* data = DXManager.LockTexture(texture);
            Marshal.Copy(_pixels, 0, (IntPtr)data, _pixels.Length); // row pitch is Width * 4 (RenderTexture)
            DXManager.UnlockTexture(texture);
            DXManager.GeneratedMapTexture = texture;
            return true;
        }

        /// <summary>Libraries.MiniMap.Draw(index, section, point, colour, opacity) for the picture (the classic minimap).</summary>
        public static void Draw(Rectangle section, Point point, Color colour, float opacity)
        {
            if (!EnsureTexture()) return;
            if (point.X >= Settings.ScreenWidth || point.Y >= Settings.ScreenHeight || point.X + Size.Width < 0 || point.Y + Size.Height < 0)
                return;

            if (section.Right > Size.Width) section.Width -= section.Right - Size.Width;
            if (section.Bottom > Size.Height) section.Height -= section.Bottom - Size.Height;

            DXManager.DrawOpaque(DXManager.GeneratedMapTexture, section, new Vector3(point.X, point.Y, 0.0F), colour, opacity);
        }

        /// <summary>Libraries.MiniMap.Draw(index, point, size, colour) for the picture: the whole picture scaled to
        /// <paramref name="size"/> (the classic big map, the modern minimap and big map).</summary>
        public static void Draw(Point point, Size size, Color colour)
        {
            if (!EnsureTexture() || size.Width <= 0 || size.Height <= 0) return;
            if (point.X >= Settings.ScreenWidth || point.Y >= Settings.ScreenHeight || point.X + size.Width < 0 || point.Y + size.Height < 0)
                return;

            float scaleX = (float)size.Width / Size.Width;
            float scaleY = (float)size.Height / Size.Height;

            // Same scaling as MLibrary.Draw(index, point, size, colour); the transform is reset straight after.
            DXManager.SetSpriteTransform(Matrix.Scaling(scaleX, scaleY, 0));
            DXManager.Draw(DXManager.GeneratedMapTexture, new Rectangle(0, 0, Size.Width, Size.Height), new Vector3(point.X / scaleX, point.Y / scaleY, 0.0F), colour);
            DXManager.SetSpriteTransform(Matrix.Identity);
        }
    }
}

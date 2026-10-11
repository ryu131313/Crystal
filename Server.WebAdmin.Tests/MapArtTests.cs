using Server.WebAdmin.MapArt;

namespace Server.WebAdmin.Tests
{
    /// <summary>
    /// Live Monitor map art. The pure parts run always; <see cref="RealData_RendersATile"/> runs only when MIR2_CLIENT_DATA (a client
    /// Data folder) and MIR2_CLIENT_MAP (a .map file) are set; it only reads them. MIR2_MAPART_OUT (a folder) also saves the PNGs.
    /// </summary>
    [Collection(WebAdminGlobalsCollection.Name)]
    public class MapArtTests
    {
        [Fact]
        public void Scale_BoxFiltersAndPremultiplies()
        {
            // 2 x 2 frame: opaque red, transparent, opaque blue, half-alpha white -> one pixel at z = 1
            var px = new byte[]
            {
                0, 0, 255, 255,   0, 0, 0, 0,
                255, 0, 0, 255,   255, 255, 255, 128,
            };
            var f = MapArtService.Scale(px, new LibFrameInfo(2, 2, 3, 4, 1), 1);

            Assert.Equal(1, f.W);
            Assert.Equal(1, f.H);
            Assert.Equal(2, f.FullW);
            Assert.Equal(3, f.X);
            Assert.Equal(4, f.Y);
            Assert.Equal((255 * 255 + 255 * 128) / (255 * 4), f.Px[0]);     // B: blue + half white
            Assert.Equal(255 * 128 / (255 * 4), f.Px[1]);                    // G
            Assert.Equal((255 * 255 + 255 * 128) / (255 * 4), f.Px[2]);     // R: red + half white
            Assert.Equal((255 + 255 + 128) / 4, f.Px[3]);

            var same = MapArtService.Scale(px, new LibFrameInfo(2, 2, 0, 0, 1), 0);
            Assert.Equal(2, same.W);
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, same.Px.Take(4).ToArray());
            Assert.Equal(new byte[] { 128, 128, 128, 128 }, same.Px.Skip(12).Take(4).ToArray());
        }

        [Fact]
        public void Scale_OfAMissingFrame_KeepsTheSizeOnly()
        {
            var f = MapArtService.Scale(null, new LibFrameInfo(48, 32, 0, 0, 0), 2);
            Assert.Null(f.Px);
            Assert.Equal(48, f.FullW);
        }

        [Fact]
        public void Catalog_MatchesTheClientTable()
        {
            Assert.Equal(@"Map\WemadeMir2\Tiles", MapLibCatalog.RelativePath(0));
            Assert.Equal(@"Map\WemadeMir2\Objects", MapLibCatalog.RelativePath(2));
            Assert.Equal(@"Map\WemadeMir2\Objects2", MapLibCatalog.RelativePath(3));
            Assert.Equal(@"Map\WemadeMir2\Objects27", MapLibCatalog.RelativePath(28));
            Assert.Equal(@"Map\WemadeMir2\Objects_32bit", MapLibCatalog.RelativePath(90));
            Assert.Equal(@"Map\ShandaMir2\Tiles2", MapLibCatalog.RelativePath(101));
            Assert.Equal(@"Map\ShandaMir2\AniTiles1", MapLibCatalog.RelativePath(190));
            Assert.Equal(@"Map\WemadeMir3\sand\Smtilesc", MapLibCatalog.RelativePath(233));
            Assert.Equal(@"Map\ShandaMir3\Object2cforest", MapLibCatalog.RelativePath(373));
            Assert.Null(MapLibCatalog.RelativePath(29));
            Assert.Null(MapLibCatalog.RelativePath(400));
        }

        [Fact]
        public void ClientMapData_ReadsTheOldSchoolFormat()
        {
            // type 0: w, h, then 52-byte header, 12 bytes per cell (x-major)
            var w = 2;
            var h = 3;
            var bytes = new byte[52 + w * h * 12];
            BitConverter.GetBytes((short)w).CopyTo(bytes, 0);
            BitConverter.GetBytes((short)h).CopyTo(bytes, 2);
            var cell = 52 + (1 * h + 2) * 12;                       // x = 1, y = 2
            BitConverter.GetBytes((short)7).CopyTo(bytes, cell);     // back
            BitConverter.GetBytes((short)8).CopyTo(bytes, cell + 2); // middle
            BitConverter.GetBytes((short)9).CopyTo(bytes, cell + 4); // front
            bytes[cell + 10] = 3;                                    // front file -> 3 + 2

            var map = new ClientMapData(bytes);
            Assert.Null(map.Error);
            Assert.Equal(2, map.Width);
            Assert.Equal(3, map.Height);
            Assert.Equal(7, map.MapCells[1, 2].BackImage);
            Assert.Equal(0, map.MapCells[1, 2].BackIndex);
            Assert.Equal(8, map.MapCells[1, 2].MiddleImage);
            Assert.Equal(1, map.MapCells[1, 2].MiddleIndex);
            Assert.Equal(9, map.MapCells[1, 2].FrontImage);
            Assert.Equal(5, map.MapCells[1, 2].FrontIndex);
        }

        [Fact]
        public void EncodeBgraPng_SwapsChannels()
        {
            var png = WebAdminMonitor.EncodeBgraPng(1, 1, new byte[] { 1, 2, 3, 4 }, alpha: true);
            var idat = ExtractIdat(png);
            Assert.Equal(new byte[] { 0, 3, 2, 1, 4 }, idat);

            var rgb = WebAdminMonitor.EncodeBgraPng(1, 1, new byte[] { 1, 2, 3, 4 }, alpha: false);
            Assert.Equal(new byte[] { 0, 3, 2, 1 }, ExtractIdat(rgb));
        }

        private static byte[] ExtractIdat(byte[] png)
        {
            var at = 8;
            while (at < png.Length)
            {
                var len = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
                var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
                if (type == "IDAT")
                {
                    using var z = new System.IO.Compression.ZLibStream(new MemoryStream(png, at + 8, len), System.IO.Compression.CompressionMode.Decompress);
                    using var o = new MemoryStream();
                    z.CopyTo(o);
                    return o.ToArray();
                }
                at += 12 + len;
            }
            return null;
        }

        [Fact]
        public void RealData_RendersATile()
        {
            var data = Environment.GetEnvironmentVariable("MIR2_CLIENT_DATA");
            var mapFile = Environment.GetEnvironmentVariable("MIR2_CLIENT_MAP");
            if (string.IsNullOrEmpty(data) || string.IsNullOrEmpty(mapFile)) return;   // opt-in

            var old = WebAdminOptions.Current;
            WebAdminOptions.Current = new WebAdminOptions { ClientDataPath = data };
            try
            {
                var map = new ClientMapData(File.ReadAllBytes(mapFile));
                Assert.Null(map.Error);
                var outDir = Environment.GetEnvironmentVariable("MIR2_MAPART_OUT");

                for (var z = 0; z <= MapArtService.MaxZoom; z++)
                {
                    var cells = MapArtService.TileCells(z);
                    var tx = Math.Max(0, map.Width / 2 / cells);
                    var ty = Math.Max(0, map.Height / 2 / cells);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var px = MapArtService.RenderTile(map, z, tx, ty);
                    var ms = sw.ElapsedMilliseconds;

                    var distinct = new HashSet<int>();
                    for (var i = 0; i < px.Length && distinct.Count < 64; i += 4 * 97) distinct.Add(px[i] | (px[i + 1] << 8) | (px[i + 2] << 16));
                    Assert.True(distinct.Count > 8, "z" + z + " tile looks blank (" + distinct.Count + " colours)");

                    if (!string.IsNullOrEmpty(outDir))
                    {
                        Directory.CreateDirectory(outDir);
                        File.WriteAllBytes(Path.Combine(outDir, "tile-z" + z + "-" + tx + "-" + ty + "-" + ms + "ms.png"),
                            WebAdminMonitor.EncodeBgraPng(MapArtService.TilePxW, MapArtService.TilePxH, px, alpha: false));
                    }
                }
            }
            finally
            {
                WebAdminOptions.Current = old;
            }
        }
    }
}

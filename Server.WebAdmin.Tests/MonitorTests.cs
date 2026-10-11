using System.IO.Compression;

namespace Server.WebAdmin.Tests
{
    /// <summary>Live Monitor: map image encoder, heat buckets, map keys. Heat is process-wide state, hence the globals collection.</summary>
    [Collection(WebAdminGlobalsCollection.Name)]
    public class MonitorTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

        private static int ReadBE(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

        /// <summary>Walks the chunks, checking every CRC; returns (type, data) in order.</summary>
        private static List<(string type, byte[] data)> Chunks(byte[] png)
        {
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.Take(8).ToArray());
            var list = new List<(string, byte[])>();
            var at = 8;
            while (at < png.Length)
            {
                var len = ReadBE(png, at);
                var typeBytes = png.Skip(at + 4).Take(4).ToArray();
                var data = png.Skip(at + 8).Take(len).ToArray();
                var crc = (uint)ReadBE(png, at + 8 + len);
                Assert.Equal(WebAdminMonitor.Crc32(WebAdminMonitor.Crc32(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu, crc);
                list.Add((System.Text.Encoding.ASCII.GetString(typeBytes), data));
                at += 12 + len;
            }
            return list;
        }

        [Fact]
        public void Crc32_MatchesTheStandardCheckValue()
        {
            var crc = WebAdminMonitor.Crc32(0xFFFFFFFFu, System.Text.Encoding.ASCII.GetBytes("123456789")) ^ 0xFFFFFFFFu;
            Assert.Equal(0xCBF43926u, crc);
        }

        [Fact]
        public void EncodePalettePng_IsAValidIndexedPng_WithTheGivenPixels()
        {
            var pixels = new byte[] { 0, 1, 2, 3, 3, 2, 1, 0 };   // 4 x 2
            var png = WebAdminMonitor.EncodePalettePng(4, 2, pixels, new byte[] { 0, 0, 0, 10, 10, 10, 20, 20, 20, 30, 30, 30 });

            var chunks = Chunks(png);
            Assert.Equal(new[] { "IHDR", "PLTE", "IDAT", "IEND" }, chunks.Select(c => c.type).ToArray());

            var ihdr = chunks[0].data;
            Assert.Equal(4, ReadBE(ihdr, 0));
            Assert.Equal(2, ReadBE(ihdr, 4));
            Assert.Equal(8, ihdr[8]);
            Assert.Equal(3, ihdr[9]);
            Assert.Equal(12, chunks[1].data.Length);

            using var z = new ZLibStream(new MemoryStream(chunks[2].data), CompressionMode.Decompress);
            using var raw = new MemoryStream();
            z.CopyTo(raw);
            Assert.Equal(new byte[] { 0, 0, 1, 2, 3, 0, 3, 2, 1, 0 }, raw.ToArray());
        }

        [Fact]
        public void EncodePalettePng_RejectsAMismatchedSize()
        {
            Assert.Throws<ArgumentException>(() => WebAdminMonitor.EncodePalettePng(3, 3, new byte[8], new byte[3]));
        }

        [Fact]
        public void Pixels_AreRowMajor_AndSafeZonesRecolourOnlyWalkableCells()
        {
            // 3 x 2 map, x-major attributes: x0 = [Walk, Walk], x1 = [HighWall, Walk], x2 = [LowWall, Walk]
            var cells = new MonitorMapCells
            {
                Width = 3,
                Height = 2,
                Attributes = new byte[] { 0, 0, 1, 0, 2, 0 },
                SafeZones = Array.Empty<int>(),
            };

            Assert.Equal(new[]
            {
                WebAdminMonitor.PxWalk, WebAdminMonitor.PxNoFloor, WebAdminMonitor.PxLowWall,
                WebAdminMonitor.PxWalk, WebAdminMonitor.PxWalk, WebAdminMonitor.PxWalk,
            }, WebAdminMonitor.Pixels(cells));

            cells.SafeZones = new[] { 0, 0, 1 };   // covers x 0..1, y 0..1 (clipped)
            Assert.Equal(new[]
            {
                WebAdminMonitor.PxSafe, WebAdminMonitor.PxNoFloor, WebAdminMonitor.PxLowWall,
                WebAdminMonitor.PxSafe, WebAdminMonitor.PxSafe, WebAdminMonitor.PxWalk,
            }, WebAdminMonitor.Pixels(cells));
        }

        [Fact]
        public void Heat_SumsTheRequestedWindow_PerMapIndex_OnTheGrid()
        {
            WebAdminMonitor.ResetHeatForTests();
            try
            {
                var m = WebAdminMonitor.MinuteOf(T0);
                WebAdminMonitor.AddHeat(m - 10, new[] { 1, 10, 10 });            // outside a 5 minute window
                WebAdminMonitor.AddHeat(m - 2, new[] { 1, 10, 11, 1, 11, 10 });  // both in grid cell (5, 5)
                WebAdminMonitor.AddHeat(m, new[] { 1, 10, 10, 2, 0, 0, 1, 40, 2 });

                var (cells, max, samples) = WebAdminMonitor.GetHeat(1, 5, T0);
                var dict = new Dictionary<(int, int), int>();
                for (var i = 0; i < cells.Count; i += 3) dict[(cells[i], cells[i + 1])] = cells[i + 2];

                Assert.Equal(3, dict[(5, 5)]);
                Assert.Equal(1, dict[(20, 1)]);
                Assert.Equal(2, dict.Count);
                Assert.Equal(3, max);
                Assert.Equal(4, samples);

                var all = WebAdminMonitor.GetHeat(1, 15, T0);
                Assert.Equal(5, all.samples);

                var other = WebAdminMonitor.GetHeat(2, 1, T0);
                Assert.Equal(1, other.samples);
                Assert.Equal(0, WebAdminMonitor.GetHeat(3, 15, T0).samples);
            }
            finally
            {
                WebAdminMonitor.ResetHeatForTests();
            }
        }

        [Fact]
        public void Heat_RecyclesABucketForANewMinute_AndIgnoresALateCapture()
        {
            WebAdminMonitor.ResetHeatForTests();
            try
            {
                var m = WebAdminMonitor.MinuteOf(T0);
                WebAdminMonitor.AddHeat(m - WebAdminMonitor.HeatMinutes, new[] { 1, 0, 0 });
                WebAdminMonitor.AddHeat(m, new[] { 1, 2, 2 });                                   // same slot: replaces the old minute
                WebAdminMonitor.AddHeat(m - WebAdminMonitor.HeatMinutes, new[] { 1, 4, 4 });     // late capture for the recycled minute

                var (cells, _, samples) = WebAdminMonitor.GetHeat(1, 15, T0.AddMinutes(1));
                Assert.Equal(1, samples);
                Assert.Equal(new List<int> { 1, 1, 1 }, cells);
            }
            finally
            {
                WebAdminMonitor.ResetHeatForTests();
            }
        }

        [Theory]
        [InlineData("12:1", "12:1")]
        [InlineData(" 0:3 ", "0:3")]
        [InlineData("007:02", "7:2")]
        [InlineData("12", null)]
        [InlineData("12:0", null)]
        [InlineData("-1:1", null)]
        [InlineData("a:1", null)]
        [InlineData("1:2:3", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void NormaliseKey(string input, string expected)
        {
            Assert.Equal(expected, WebAdminMonitor.NormaliseKey(input));
        }
    }
}

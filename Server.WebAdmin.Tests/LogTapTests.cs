namespace Server.WebAdmin.Tests
{
    public class LogTapTests
    {
        private static WebAdminLogRing Filled(int capacity, int lines)
        {
            var ring = new WebAdminLogRing(capacity, new TestClock().Now);
            for (var i = 1; i <= lines; i++) ring.Append("line " + i);
            return ring;
        }

        [Fact]
        public void Sequences_AreMonotonic_AndTextIsTrimmed()
        {
            var ring = new WebAdminLogRing(10);

            Assert.Equal(1, ring.Append("[1/1/2026]: hello\r\n"));
            Assert.Equal(2, ring.Append(null));

            var (lines, last) = ring.Get(0);
            Assert.Equal(2, lines.Count);
            Assert.Equal("[1/1/2026]: hello", lines[0].Text);
            Assert.Equal(string.Empty, lines[1].Text);
            Assert.Equal(2, last);
        }

        [Fact]
        public void AfterSeq_ReturnsOnlyNewerLines_OldestFirst()
        {
            var ring = Filled(100, 5);

            var (lines, last) = ring.Get(3);

            Assert.Equal(new long[] { 4, 5 }, lines.Select(l => l.Seq).ToArray());
            Assert.Equal(5, last);
        }

        [Fact]
        public void NothingNew_ReturnsEmpty_WithLatestSeq()
        {
            var ring = Filled(100, 5);

            var (lines, last) = ring.Get(5);

            Assert.Empty(lines);
            Assert.Equal(5, last);
        }

        [Fact]
        public void Paging_WithMax_ContinuesFromLastSeq()
        {
            var ring = Filled(100, 5);

            var (page1, last1) = ring.Get(1, 2);
            Assert.Equal(new long[] { 2, 3 }, page1.Select(l => l.Seq).ToArray());
            Assert.Equal(3, last1);

            var (page2, last2) = ring.Get(last1, 2);
            Assert.Equal(new long[] { 4, 5 }, page2.Select(l => l.Seq).ToArray());
            Assert.Equal(5, last2);

            var (page3, last3) = ring.Get(last2, 2);
            Assert.Empty(page3);
            Assert.Equal(5, last3);
        }

        [Fact]
        public void InitialLoad_ReturnsNewestTail()
        {
            var ring = Filled(100, 10);

            var (lines, last) = ring.Get(0, 3);

            Assert.Equal(new long[] { 8, 9, 10 }, lines.Select(l => l.Seq).ToArray());
            Assert.Equal(10, last);
        }

        [Fact]
        public void RingOverflow_DropsOldest_KeepsSequence()
        {
            var ring = Filled(3, 5);

            Assert.Equal(3, ring.Count);
            Assert.Equal(5, ring.LastSeq);

            var (all, _) = ring.Get(0);
            Assert.Equal(new long[] { 3, 4, 5 }, all.Select(l => l.Seq).ToArray());
            Assert.Equal("line 3", all[0].Text);

            // A client that fell behind gets everything still available.
            var (behind, last) = ring.Get(1);
            Assert.Equal(new long[] { 3, 4, 5 }, behind.Select(l => l.Seq).ToArray());
            Assert.Equal(5, last);
        }

        [Fact]
        public void AfterSeqFromTheFuture_IsTreatedAsFresh()
        {
            var ring = Filled(100, 3);

            var (lines, last) = ring.Get(999);

            Assert.Equal(new long[] { 1, 2, 3 }, lines.Select(l => l.Seq).ToArray());
            Assert.Equal(3, last);
        }

        [Fact]
        public void StaticTap_KnownKinds_AndUnknownKind()
        {
            Assert.True(WebAdminLogTap.IsKnownKind("server"));
            Assert.True(WebAdminLogTap.IsKnownKind("Debug"));
            Assert.True(WebAdminLogTap.IsKnownKind("chat"));
            Assert.False(WebAdminLogTap.IsKnownKind("nope"));

            var (none, noneLast) = WebAdminLogTap.Get("nope", 0);
            Assert.Empty(none);
            Assert.Equal(0, noneLast);

            var before = WebAdminLogTap.Ring("chat").LastSeq;
            WebAdminLogTap.AppendChat("Bob: hi");
            var (lines, last) = WebAdminLogTap.Get("chat", before);

            Assert.Contains(lines, l => l.Text == "Bob: hi");
            Assert.True(last > before);
        }
    }
}

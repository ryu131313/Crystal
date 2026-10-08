namespace Server.WebAdmin.Tests
{
    public class GmChatLogTests
    {
        private static ChatLogRing Filled(int capacity, int count, TestClock clock = null)
        {
            var ring = new ChatLogRing(capacity, (clock ?? new TestClock()).Now);
            for (var i = 1; i <= count; i++)
                ring.Append(i % 2 == 0 ? "shout" : "normal", i % 3 == 0 ? "Alice" : "Bob", "line " + i, i % 5 == 0 ? "Carol" : null);
            return ring;
        }

        [Fact]
        public void Append_AssignsIncreasingSeqAndCleansText()
        {
            var clock = new TestClock();
            var ring = new ChatLogRing(10, clock.Now);

            Assert.Equal(1, ring.Append("Whisper", "Bob", "hi\r\nthere\u0001", "Alice"));
            Assert.Equal(2, ring.Append(null, "Bob", "x"));

            var page = ring.Query(0, null, 10);
            Assert.Equal(2, page.Lines.Count);
            Assert.Equal("whisper", page.Lines[0].Channel);
            Assert.Equal("hi there", page.Lines[0].Message);
            Assert.Equal("Alice", page.Lines[0].Target);
            Assert.Equal(clock.UtcNow, page.Lines[0].Utc);
            Assert.Equal("normal", page.Lines[1].Channel);
            Assert.Null(page.Lines[1].Target);
            Assert.Equal(2, page.Last);
        }

        [Fact]
        public void Query_InitialReturnsNewestOldestFirst()
        {
            var ring = Filled(100, 30);

            var page = ring.Query(0, null, 5);

            Assert.Equal(new long[] { 26, 27, 28, 29, 30 }, page.Lines.Select(l => l.Seq));
            Assert.Equal(30, page.Last);
            Assert.False(page.Gap);
            Assert.False(page.Reset);
        }

        [Fact]
        public void Query_IncrementalReturnsOnlyNewerLines()
        {
            var ring = Filled(100, 10);

            var page = ring.Query(7, null, 100);
            Assert.Equal(new long[] { 8, 9, 10 }, page.Lines.Select(l => l.Seq));
            Assert.Equal(10, page.Last);

            var empty = ring.Query(10, null, 100);
            Assert.Empty(empty.Lines);
            Assert.Equal(10, empty.Last);
        }

        [Fact]
        public void Query_LimitSetsMoreAndCursor()
        {
            var ring = Filled(100, 10);

            var page = ring.Query(2, null, 3);
            Assert.Equal(new long[] { 3, 4, 5 }, page.Lines.Select(l => l.Seq));
            Assert.True(page.More);
            Assert.Equal(5, page.Last);

            var next = ring.Query(page.Last, null, 100);
            Assert.Equal(new long[] { 6, 7, 8, 9, 10 }, next.Lines.Select(l => l.Seq));
            Assert.False(next.More);
        }

        [Fact]
        public void Query_FilterAdvancesCursorPastSkippedLines()
        {
            var ring = Filled(100, 10);
            var filter = ChatLogFilter.Create(null, "shout", null);

            var page = ring.Query(0, filter, 100);
            Assert.All(page.Lines, l => Assert.Equal("shout", l.Channel));
            Assert.Equal(5, page.Lines.Count);
            Assert.Equal(10, page.Last);

            ring.Append("normal", "Bob", "not a shout");
            var next = ring.Query(page.Last, filter, 100);
            Assert.Empty(next.Lines);
            Assert.Equal(11, next.Last);
        }

        [Fact]
        public void Query_RingWrapReportsGap()
        {
            var ring = Filled(5, 12); // keeps 8..12

            var page = ring.Query(3, null, 100);
            Assert.True(page.Gap);
            Assert.Equal(new long[] { 8, 9, 10, 11, 12 }, page.Lines.Select(l => l.Seq));

            Assert.False(ring.Query(7, null, 100).Gap);
            Assert.Equal(5, ring.Count);
        }

        [Fact]
        public void Query_CursorFromTheFutureResets()
        {
            var ring = Filled(10, 3);

            var page = ring.Query(999, null, 10);
            Assert.True(page.Reset);
            Assert.Equal(3, page.Lines.Count);
            Assert.Equal(3, page.Last);
        }

        [Fact]
        public void Filter_MatchesChannelsNameAndText()
        {
            var e = new ChatLogEntry { Channel = "whisper", Sender = "Bob", Target = "Alice", Message = "Buy my Sword" };

            Assert.True(ChatLogFilter.Create(null, null, null).Matches(e));
            Assert.True(ChatLogFilter.Create(null, null, null).IsEmpty);
            Assert.True(ChatLogFilter.Create("sword", null, null).Matches(e));
            Assert.False(ChatLogFilter.Create("axe", null, null).Matches(e));
            Assert.True(ChatLogFilter.Create(null, "normal, whisper", null).Matches(e));
            Assert.False(ChatLogFilter.Create(null, "normal,shout", null).Matches(e));
            Assert.True(ChatLogFilter.Create(null, "all", null).Matches(e));
            Assert.True(ChatLogFilter.Create(null, null, "ali").Matches(e));   // target substring
            Assert.True(ChatLogFilter.Create(null, null, "BOB").Matches(e));   // sender, case-insensitive
            Assert.False(ChatLogFilter.Create(null, null, "ali", exactName: true).Matches(e));
            Assert.True(ChatLogFilter.Create(null, null, "alice", exactName: true).Matches(e));
            Assert.False(ChatLogFilter.Create(null, null, "Carol").Matches(e));
        }

        // ---------------------------------------------------------------------------------------------- log file parsing

        private static DateTime AsUtc(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Utc);

        [Theory]
        [InlineData("Bob: hello world", "normal", "Bob", null, "hello world")]
        [InlineData("Bob: /Alice psst there", "whisper", "Bob", "Alice", "psst there")]
        [InlineData("Bob: !!group up", "group", "Bob", null, "group up")]
        [InlineData("Bob: !~guild news", "guild", "Bob", null, "guild news")]
        [InlineData("Bob: !#mentor talk", "mentor", "Bob", null, "mentor talk")]
        [InlineData("Bob: !HELLO ALL", "shout", "Bob", null, "HELLO ALL")]
        [InlineData("Bob: :)love you", "relationship", "Bob", null, "love you")]
        [InlineData("Bob: @!Server restart soon", "announcement", "Bob", null, "Server restart soon")]
        [InlineData("Bob: @move 10 10", "command", "Bob", null, "@move 10 10")]
        public void ParseLine_ClassifiesPrefixes(string body, string channel, string sender, string target, string message)
        {
            var e = ChatLogFileParser.ParseLine("2026-10-02 18:01:02,123 - " + body, AsUtc);

            Assert.NotNull(e);
            Assert.Equal(new DateTime(2026, 10, 2, 18, 1, 2, 123), e.Utc);
            Assert.Equal(DateTimeKind.Utc, e.Utc.Kind);
            Assert.Equal(channel, e.Channel);
            Assert.Equal(sender, e.Sender);
            Assert.Equal(target, e.Target);
            Assert.Equal(message, e.Message);
        }

        [Fact]
        public void ParseLine_NonChatAndGarbage()
        {
            var sys = ChatLogFileParser.ParseLine("2026-10-02 18:01:02,123 - Guild 'Knights' notice changed by SYSTEM.", AsUtc);
            Assert.Equal("system", sys.Channel);
            Assert.Equal(string.Empty, sys.Sender);

            Assert.Null(ChatLogFileParser.ParseLine("", AsUtc));
            Assert.Null(ChatLogFileParser.ParseLine("not a log line", AsUtc));
            Assert.Null(ChatLogFileParser.ParseLine("yesterday - Bob: hi", AsUtc));
        }

        [Fact]
        public void ParseLines_RedactsTheLineAfterLoginAndKeepsNewest()
        {
            var lines = new[]
            {
                "2026-10-02 10:00:00,000 - Bob: hello",
                "2026-10-02 10:00:01,000 - Gm: @login",
                "2026-10-02 10:00:02,000 - Bob: hi gm",
                "2026-10-02 10:00:03,000 - Gm: SuperSecret123",
                "2026-10-02 10:00:04,000 - Gm: welcome all",
                "garbage",
            };

            var all = ChatLogFileParser.ParseLines(lines, null, 100, out var total, out var truncated, AsUtc);
            Assert.Equal(5, total);
            Assert.False(truncated);
            Assert.DoesNotContain(all, e => e.Message.Contains("SuperSecret"));
            Assert.Equal(ChatLogFileParser.RedactedPassword, all[3].Message);
            Assert.Equal("welcome all", all[4].Message);
            Assert.Equal(4, all[3].Seq); // line numbers

            var newest = ChatLogFileParser.ParseLines(lines, ChatLogFilter.Create(null, null, "Bob"), 1, out total, out truncated, AsUtc);
            Assert.Single(newest);
            Assert.Equal("hi gm", newest[0].Message);
            Assert.Equal(2, total);
            Assert.True(truncated);
        }

        [Fact]
        public void FileNames_RoundTrip()
        {
            var d = new DateTime(2026, 10, 2);
            Assert.Equal("Chat (02-10-2026).log", ChatLogFileParser.FileNameFor(d));
            Assert.True(ChatLogFileParser.TryParseFileName(@"C:\x\Logs\Chat\Chat (02-10-2026).log", out var parsed));
            Assert.Equal(d, parsed);
            Assert.False(ChatLogFileParser.TryParseFileName("Server (02-10-2026).log", out _));
            Assert.False(ChatLogFileParser.TryParseFileName("Chat (2026-10-02).log", out _));

            Assert.True(ChatLogFileParser.TryParseDate("2026-10-02", out var api));
            Assert.Equal(d, api);
            Assert.False(ChatLogFileParser.TryParseDate("02-10-2026", out _));
            Assert.False(ChatLogFileParser.TryParseDate("../../etc", out _));
        }
    }
}

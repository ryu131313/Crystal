using System.Text;
using Server.WebAdmin.Routes;

namespace Server.WebAdmin.Tests
{
    public class DashboardSampleTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static StatusSample Sample(int i, int players = 0, long cycle = 0, double cpu = 0, long ws = 0)
        {
            return new StatusSample { Utc = T0.AddSeconds(i * 5), Players = players, Connections = players + 1, Monsters = 10 * i, CycleDelayMs = cycle, CpuPercent = cpu, WorkingSetMb = ws };
        }

        [Fact]
        public void SampleRing_OverwritesOldest_AndSinceFiltersByTime()
        {
            var ring = new WebAdminSampleRing(3);
            for (var i = 0; i < 5; i++) ring.Add(Sample(i, players: i));

            Assert.Equal(3, ring.Count);
            Assert.Equal(4, ring.Latest.Players);

            var all = ring.Since(DateTime.MinValue);
            Assert.Equal(new[] { 2, 3, 4 }, all.Select(s => s.Players).ToArray());

            var recent = ring.Since(T0.AddSeconds(15));
            Assert.Equal(new[] { 3, 4 }, recent.Select(s => s.Players).ToArray());
        }

        [Fact]
        public void SampleRing_IgnoresNull_AndEmptyLatestIsNull()
        {
            var ring = new WebAdminSampleRing(2);
            Assert.Null(ring.Latest);
            ring.Add(null);
            Assert.Equal(0, ring.Count);
        }

        [Fact]
        public void Downsample_FewerThanMax_ReturnsCopies()
        {
            var input = new List<StatusSample> { Sample(0, 1), Sample(1, 2) };

            var output = WebAdminSampleRing.Downsample(input, 10);

            Assert.Equal(2, output.Count);
            Assert.NotSame(input[0], output[0]);
            Assert.Equal(2, output[1].Players);
            Assert.Equal(input[1].Utc, output[1].Utc);
        }

        [Fact]
        public void Downsample_KeepsSpikes_AveragesCpu_AndNeverExceedsMax()
        {
            var input = new List<StatusSample>();
            for (var i = 0; i < 1000; i++) input.Add(Sample(i, players: i % 7, cycle: i == 501 ? 900 : 3, cpu: 10, ws: 100 + i));

            var output = WebAdminSampleRing.Downsample(input, 360);

            Assert.True(output.Count <= 360);
            Assert.Contains(output, s => s.CycleDelayMs == 900);
            Assert.All(output, s => Assert.Equal(10, s.CpuPercent));
            Assert.Equal(input[input.Count - 1].Utc, output[output.Count - 1].Utc);
            Assert.Equal(1099, output[output.Count - 1].WorkingSetMb);
            Assert.Equal(2, output[0].Players);   // max of 0, 1, 2
            Assert.Equal(50, output[1].Monsters);  // bucket 2 holds samples 3, 4, 5 (Monsters = 10 * i)
        }

        [Fact]
        public void Downsample_EmptyOrNull_IsEmpty()
        {
            Assert.Empty(WebAdminSampleRing.Downsample(null, 10));
            Assert.Empty(WebAdminSampleRing.Downsample(new List<StatusSample>(), 10));
        }

        [Theory]
        [InlineData(0, 60)]
        [InlineData(-5, 60)]
        [InlineData(1, 5)]
        [InlineData(60, 60)]
        [InlineData(360, 360)]
        [InlineData(1440, 1440)]
        [InlineData(99999, 1440)]
        public void ClampMinutes_StaysInRetention(int requested, int expected)
        {
            Assert.Equal(expected, WebAdminStatusSampler.ClampMinutes(requested));
        }

        [Fact]
        public void SamplerRing_CoversTwentyFourHours()
        {
            Assert.Equal(24 * 3600 / 5, WebAdminStatusSampler.Ring.Capacity);
        }

        [Fact]
        public void LiveRing_CoversFifteenMinutesAtOneSecond()
        {
            Assert.Equal(15 * 60, WebAdminStatusSampler.LiveRing.Capacity);
        }

        [Fact]
        public void Downsample_LoadFields_MaxSpikes_AverageRates_PerIndexArrays()
        {
            var input = new List<StatusSample>();
            for (var i = 0; i < 4; i++)
            {
                var s = Sample(i);
                s.GameBusyPercent = 10 * (i + 1);               // 10, 20, 30, 40
                s.MaxIterationMs = i == 2 ? 80 : 2;
                s.PacketsIn = 100 * (i + 1);                    // 100..400
                s.Gen2 = i == 1 ? 1 : 0;
                s.MonstersAwake = i == 0 ? -1 : 300;            // the first sample did not count
                s.PhaseMs = new[] { 1.0 * i, 2.0 };
                s.MobCpu = i == 3 ? new[] { 0.0, 40, 60 } : new[] { 0.0, 20 }; // a thread appears mid-bucket
                s.MobLateMs = new long[] { 0, i == 1 ? 50 : 1 };
                input.Add(s);
            }

            var output = WebAdminSampleRing.Downsample(input, 1);

            var a = Assert.Single(output);
            Assert.Equal(25, a.GameBusyPercent);
            Assert.Equal(80, a.MaxIterationMs);
            Assert.Equal(250, a.PacketsIn);
            Assert.Equal(1, a.Gen2);
            Assert.Equal(300, a.MonstersAwake);
            Assert.Equal(new[] { 1.5, 2.0 }, a.PhaseMs);
            Assert.Equal(new[] { 0.0, 25, 15 }, a.MobCpu);
            Assert.Equal(new long[] { 0, 50 }, a.MobLateMs);
        }

        [Fact]
        public void Downsample_Copy_DoesNotShareArrays()
        {
            var s = Sample(0);
            s.PhaseMs = new[] { 1.0 };
            s.MobCpu = new[] { 0.0, 5 };

            var copy = WebAdminSampleRing.Downsample(new List<StatusSample> { s }, 10)[0];
            s.PhaseMs[0] = 9;
            s.MobCpu[1] = 9;

            Assert.Equal(1.0, copy.PhaseMs[0]);
            Assert.Equal(5.0, copy.MobCpu[1]);
            Assert.Equal(-1, new StatusSample().MonstersAwake);
        }
    }

    public class DashboardHostMetricsTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Cpu_IsNormalisedAcrossCores()
        {
            // 2 s of CPU in 1 s of wall time on 4 cores = 50 %.
            var pct = WebAdminHostMetrics.ComputeCpuPercent(TimeSpan.FromSeconds(10), T0, TimeSpan.FromSeconds(12), T0.AddSeconds(1), 4);
            Assert.Equal(50.0, pct);
        }

        [Fact]
        public void Cpu_IsClampedAndRounded()
        {
            Assert.Equal(100.0, WebAdminHostMetrics.ComputeCpuPercent(TimeSpan.Zero, T0, TimeSpan.FromSeconds(10), T0.AddSeconds(1), 1));
            Assert.Equal(33.3, WebAdminHostMetrics.ComputeCpuPercent(TimeSpan.Zero, T0, TimeSpan.FromMilliseconds(1000), T0.AddSeconds(3), 1));
        }

        [Fact]
        public void Cpu_ZeroForBadIntervals()
        {
            Assert.Equal(0, WebAdminHostMetrics.ComputeCpuPercent(TimeSpan.Zero, T0, TimeSpan.FromSeconds(1), T0, 4));
            Assert.Equal(0, WebAdminHostMetrics.ComputeCpuPercent(TimeSpan.FromSeconds(5), T0, TimeSpan.FromSeconds(4), T0.AddSeconds(1), 4));
            Assert.Equal(25.0, WebAdminHostMetrics.ComputeCpuPercent(TimeSpan.Zero, T0, TimeSpan.FromMilliseconds(250), T0.AddSeconds(1), 0));
        }

        [Fact]
        public void ThreadCpu_IsPercentOfOneCore_ClampedAndRounded()
        {
            Assert.Equal(50.0, WebAdminThreadCpu.OneCorePercent(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.5), 1000));
            Assert.Equal(100.0, WebAdminThreadCpu.OneCorePercent(TimeSpan.Zero, TimeSpan.FromSeconds(2), 1000));
            Assert.Equal(33.3, WebAdminThreadCpu.OneCorePercent(TimeSpan.Zero, TimeSpan.FromMilliseconds(1000), 3000));
            Assert.Equal(0, WebAdminThreadCpu.OneCorePercent(TimeSpan.Zero, TimeSpan.FromSeconds(1), 0));
            Assert.Equal(0, WebAdminThreadCpu.OneCorePercent(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), 1000));
        }

        [Fact]
        public void Read_ReturnsPlausibleProcessValues()
        {
            var s = WebAdminHostMetrics.Read();

            Assert.True(s.WorkingSetMb > 0);
            Assert.True(s.Threads > 0);
            Assert.True(s.ProcessorCount >= 1);
            Assert.False(string.IsNullOrEmpty(s.DotnetVersion));
            Assert.True(s.Gen2 >= 0);
        }
    }

    public class DashboardActivityFeedTests
    {
        [Fact]
        public void Ring_AssignsSequences_AndPagesAfterSeq()
        {
            var clock = new TestClock();
            var ring = new WebAdminActivityRing(10, clock.Now);

            ring.Add("login", "a entered", "a", "acc1");
            ring.Add("logout", "a left", "a", "acc1");
            ring.Add("world", "World stopped");

            var (all, last) = ring.Get(0);
            Assert.Equal(new long[] { 1, 2, 3 }, all.Select(e => e.Seq).ToArray());
            Assert.Equal(3, last);
            Assert.Equal("acc1", all[0].Account);
            Assert.Null(all[2].Character);
            Assert.Equal(clock.UtcNow, all[0].Utc);

            var (newer, last2) = ring.Get(1);
            Assert.Equal(new long[] { 2, 3 }, newer.Select(e => e.Seq).ToArray());
            Assert.Equal(3, last2);

            var (none, last3) = ring.Get(3);
            Assert.Empty(none);
            Assert.Equal(3, last3);
        }

        [Fact]
        public void Ring_DropsOldest_AndTreatsFutureSeqAsRestart()
        {
            var ring = new WebAdminActivityRing(3);
            for (var i = 1; i <= 5; i++) ring.Add("login", "e" + i);

            var (events, last) = ring.Get(0);
            Assert.Equal(new[] { "e3", "e4", "e5" }, events.Select(e => e.Text).ToArray());
            Assert.Equal(5, last);

            var (afterRestart, _) = ring.Get(999);
            Assert.Equal(3, afterRestart.Count);

            var (limited, lastLimited) = ring.Get(0, 2);
            Assert.Equal(new[] { "e4", "e5" }, limited.Select(e => e.Text).ToArray());
            Assert.Equal(5, lastLimited);
        }

        [Fact]
        public void Ring_EmptyStringsBecomeNull_AndExplicitUtcIsKept()
        {
            var ring = new WebAdminActivityRing(5);
            var when = new DateTime(2025, 5, 5, 1, 2, 3, DateTimeKind.Utc);
            ring.Add("admin", "x", "", "", when);

            var (events, _) = ring.Get(0);
            Assert.Null(events[0].Character);
            Assert.Null(events[0].Account);
            Assert.Equal(when, events[0].Utc);
        }

        private static WebAdminAuditEntry Audit(string id, DateTime ts, string action = "gm.kick")
        {
            return new WebAdminAuditEntry { Id = id, Ts = ts, Admin = "root", Action = action, Target = "Bob" };
        }

        [Fact]
        public void AuditCursor_ReturnsOnlyNewEntries_OldestFirst()
        {
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var cursor = new WebAdminAuditCursor(t0);

            var old = Audit("old", t0.AddSeconds(-1));
            var a = Audit("a", t0.AddSeconds(1));
            var b = Audit("b", t0.AddSeconds(2));

            // Query returns newest first; the cursor sorts oldest first.
            var first = cursor.TakeNew(new[] { b, a, old });
            Assert.Equal(new[] { "a", "b" }, first.Select(e => e.Id).ToArray());
            Assert.Equal(t0.AddSeconds(2), cursor.Since);

            var c = Audit("c", t0.AddSeconds(2));   // same timestamp as b, different id
            var second = cursor.TakeNew(new[] { c, b, a });
            Assert.Equal(new[] { "c" }, second.Select(e => e.Id).ToArray());

            Assert.Empty(cursor.TakeNew(new[] { c, b, a }));
            Assert.Empty(cursor.TakeNew(null));
        }

        [Fact]
        public void AuditCursor_DeduplicatesIdsWithinABatch()
        {
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var cursor = new WebAdminAuditCursor(t0);
            var a = Audit("a", t0.AddSeconds(1));
            var dup = Audit("a", t0.AddSeconds(1));

            Assert.Single(cursor.TakeNew(new[] { a, dup }));
        }

        [Fact]
        public void AuditDescribe_IncludesAdminActionTargetReason()
        {
            var e = new WebAdminAuditEntry { Admin = "root", Action = "account.ban", Target = "bob", Reason = "botting" };
            Assert.Equal("root: account.ban bob (botting)", WebAdminAuditCursor.Describe(e));
            Assert.Equal("system: unknown", WebAdminAuditCursor.Describe(new WebAdminAuditEntry()));
            Assert.Equal(string.Empty, WebAdminAuditCursor.Describe(null));
        }

        [Theory]
        [InlineData("bob", "1.2.3.4", "bad-password", "Login failed for 'bob' from 1.2.3.4: wrong password")]
        [InlineData("bob", null, "banned", "Login failed for 'bob': account banned")]
        [InlineData("", "1.2.3.4", "no-account", "Login failed for (blank) from 1.2.3.4: no such account")]
        [InlineData("bob", "1.2.3.4", "refused:maintenance", "Login failed for 'bob' from 1.2.3.4: refused (maintenance)")]
        [InlineData("bob", "1.2.3.4", "weird", "Login failed for 'bob' from 1.2.3.4: weird")]
        public void LoginFailureText_IsReadable(string account, string ip, string result, string expected)
        {
            Assert.Equal(expected, WebAdminActivity.LoginFailureText(account, ip, result));
        }
    }

    public class DashboardServerControlTests
    {
        [Fact]
        public void Marks_StartWithFullLength_ThenStandardMarksBelowIt()
        {
            Assert.Equal(new[] { 300, 240, 180, 120, 60, 30, 10 }, WebAdminRestartPlan.Marks(300));
            Assert.Equal(new[] { 60, 30, 10 }, WebAdminRestartPlan.Marks(60));
            Assert.Equal(new[] { 3600, 2700, 1800, 1200, 900, 600, 300, 240, 180, 120, 60, 30, 10 }, WebAdminRestartPlan.Marks(3600));
            Assert.Equal(new[] { 420, 300, 240, 180, 120, 60, 30, 10 }, WebAdminRestartPlan.Marks(420));
            Assert.Empty(WebAdminRestartPlan.Marks(0));
        }

        [Fact]
        public void NextAnnouncement_ConsumesReachedMarks_ReturnsSmallest()
        {
            var pending = WebAdminRestartPlan.Marks(300);

            Assert.Equal(300, WebAdminRestartPlan.NextAnnouncement(pending, 299.5));
            Assert.Null(WebAdminRestartPlan.NextAnnouncement(pending, 250));

            // A stalled timer that skips past two marks announces only the latest one.
            Assert.Equal(180, WebAdminRestartPlan.NextAnnouncement(pending, 170));
            Assert.Equal(new[] { 120, 60, 30, 10 }, pending.ToArray());

            Assert.Equal(10, WebAdminRestartPlan.NextAnnouncement(pending, 0));
            Assert.Empty(pending);
            Assert.Null(WebAdminRestartPlan.NextAnnouncement(pending, -1));
            Assert.Null(WebAdminRestartPlan.NextAnnouncement(null, 0));
        }

        [Theory]
        [InlineData(300, null, "Server restarting in 5 minutes.")]
        [InlineData(60, "", "Server restarting in 1 minute.")]
        [InlineData(90, null, "Server restarting in 2 minutes.")]
        [InlineData(30, " Please log out safely. ", "Server restarting in 30 seconds. Please log out safely.")]
        [InlineData(1, null, "Server restarting in 1 second.")]
        public void AnnouncementText_IsHumanReadable(int seconds, string message, string expected)
        {
            Assert.Equal(expected, WebAdminRestartPlan.AnnouncementText(seconds, message));
        }

        [Fact]
        public void Trim_LimitsAndBlanksToNull()
        {
            Assert.Null(ServerControlRoutes.Trim("   ", 10));
            Assert.Null(ServerControlRoutes.Trim(null, 10));
            Assert.Equal("abc", ServerControlRoutes.Trim(" abc ", 10));
            Assert.Equal("abcde", ServerControlRoutes.Trim("abcdefgh", 5));
        }

        [Fact]
        public void ReloadKinds_AreTheContractKinds()
        {
            Assert.Equal(new[] { "npcs", "drops", "linemessages", "recipes" }, ServerControlRoutes.ReloadKinds.ToArray());
        }

        [Theory]
        [InlineData("System.NullReferenceException: Object reference not set", true)]
        [InlineData("   at Server.MirEnvir.Envir.WorkLoop()", false)]
        [InlineData("Something failed: System.InvalidOperationException: Collection was modified", true)]
        [InlineData("Error loading map 12", true)]
        [InlineData("Players online: 4", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsErrorLine_RecognisesExceptions(string text, bool expected)
        {
            Assert.Equal(expected, WebAdminServerLogScan.IsErrorLine(text));
        }

        [Fact]
        public void FindLastError_PicksTheNewestErrorLine()
        {
            var lines = new List<LogLine>
            {
                new LogLine { Seq = 1, Text = "System.Exception: first" },
                new LogLine { Seq = 2, Text = "Normal line" },
                new LogLine { Seq = 3, Text = "System.IO.IOException: second" },
                new LogLine { Seq = 4, Text = "Another normal line" },
            };

            Assert.Equal(3, WebAdminServerLogScan.FindLastError(lines).Seq);
            Assert.Null(WebAdminServerLogScan.FindLastError(new List<LogLine> { new LogLine { Seq = 1, Text = "ok" } }));
            Assert.Null(WebAdminServerLogScan.FindLastError(null));
        }

        [Fact]
        public void Summary_Truncates()
        {
            Assert.Equal("abc…", WebAdminServerLogScan.Summary("abcdef", 3));
            Assert.Equal("ab", WebAdminServerLogScan.Summary("ab", 3));
            Assert.Equal(string.Empty, WebAdminServerLogScan.Summary(null, 3));
        }
    }

    public class DashboardLogFileTests
    {
        private static string NewRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "mircraft-logtests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Server"));
            return root;
        }

        [Fact]
        public void Resolve_AcceptsPlainNames_InTheKindFolder()
        {
            var root = NewRoot();
            try
            {
                var path = WebAdminLogFiles.Resolve(root, "server", "Server (01-01-2026).log");
                Assert.Equal(Path.Combine(Path.GetFullPath(Path.Combine(root, "Server")), "Server (01-01-2026).log"), path);

                Assert.NotNull(WebAdminLogFiles.Resolve(root, "SERVER", "a.log"));
                Assert.EndsWith(Path.Combine("Chat", "c.log"), WebAdminLogFiles.Resolve(root, "chat", "c.log"));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Theory]
        [InlineData("server", "..")]
        [InlineData("server", ".")]
        [InlineData("server", "..\\Setup.ini")]
        [InlineData("server", "../Setup.ini")]
        [InlineData("server", "sub/a.log")]
        [InlineData("server", "C:\\Windows\\win.ini")]
        [InlineData("server", "a.log:stream")]
        [InlineData("server", "a.log.")]
        [InlineData("server", " a.log")]
        [InlineData("server", "CON")]
        [InlineData("server", "")]
        [InlineData("server", null)]
        [InlineData("webadmin", "audit.jsonl")]
        [InlineData(null, "a.log")]
        public void Resolve_RejectsEscapesAndUnknownKinds(string kind, string name)
        {
            Assert.Null(WebAdminLogFiles.Resolve(Path.GetTempPath(), kind, name));
        }

        [Fact]
        public void List_ReturnsNewestFirst_AndEmptyForMissingFolder()
        {
            var root = NewRoot();
            try
            {
                var older = Path.Combine(root, "Server", "old.log");
                var newer = Path.Combine(root, "Server", "new.log");
                File.WriteAllText(older, "x");
                File.WriteAllText(newer, "y");
                File.SetLastWriteTimeUtc(older, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(newer, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

                Assert.Equal(new[] { "new.log", "old.log" }, WebAdminLogFiles.List(root, "server").Select(f => f.Name).ToArray());
                Assert.Empty(WebAdminLogFiles.List(root, "spawn"));
                Assert.Empty(WebAdminLogFiles.List(root, "nope"));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static MemoryStream Utf8(string text, bool bom = false)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bom) bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray();
            return new MemoryStream(bytes);
        }

        [Fact]
        public void Tail_ReturnsLastLines_HandlesCrLfAndTrailingNewline()
        {
            var result = WebAdminLogFiles.Tail(Utf8("one\r\ntwo\r\nthree\r\n"), 2, 1024);

            Assert.Equal(new[] { "two", "three" }, result.Lines.ToArray());
            Assert.True(result.Truncated);

            var all = WebAdminLogFiles.Tail(Utf8("one\ntwo", bom: true), 10, 1024);
            Assert.Equal(new[] { "one", "two" }, all.Lines.ToArray());
            Assert.False(all.Truncated);
        }

        [Fact]
        public void Tail_ByteLimit_DropsPartialFirstLine()
        {
            // "aaaa\nbbbb\ncccc\n" = 15 bytes; the last 8 start inside "bbbb".
            var result = WebAdminLogFiles.Tail(Utf8("aaaa\nbbbb\ncccc\n"), 100, 8);

            Assert.Equal(new[] { "cccc" }, result.Lines.ToArray());
            Assert.True(result.Truncated);
        }

        [Fact]
        public void Tail_EmptyStream_IsEmpty()
        {
            var result = WebAdminLogFiles.Tail(new MemoryStream(), 10, 1024);
            Assert.Empty(result.Lines);
            Assert.False(result.Truncated);
            Assert.Empty(WebAdminLogFiles.Tail(null, 10, 1024).Lines);
        }
    }
}

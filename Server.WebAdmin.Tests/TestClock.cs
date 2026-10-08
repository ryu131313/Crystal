namespace Server.WebAdmin.Tests
{
    /// <summary>Manually advanced UTC clock for deterministic time-based tests.</summary>
    internal sealed class TestClock
    {
        public DateTime UtcNow { get; set; }

        public TestClock(DateTime start)
        {
            UtcNow = DateTime.SpecifyKind(start, DateTimeKind.Utc);
        }

        public TestClock()
            : this(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
        {
        }

        public DateTime Now() => UtcNow;

        public void Advance(TimeSpan by) => UtcNow = UtcNow + by;
    }
}

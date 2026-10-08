namespace Server.WebAdmin.Tests
{
    public class PendingTests
    {
        [Fact]
        public void Add_Snapshot_Clear()
        {
            var clock = new TestClock();
            var list = new WebAdminPendingList(clock.Now);

            list.Add("items", "Edited 3 items", "gm");
            clock.Advance(TimeSpan.FromMinutes(1));
            list.Add("monsters", "Edited Hen", "gm2");

            var snap = list.Snapshot();
            Assert.Equal(2, snap.Count);
            Assert.Equal("items", snap[0].Area);
            Assert.Equal("Edited 3 items", snap[0].Description);
            Assert.Equal("gm", snap[0].Admin);
            Assert.Equal(clock.UtcNow, snap[1].Utc);

            list.Clear();
            Assert.Empty(list.Snapshot());
            Assert.Equal(0, list.Count);
        }

        [Fact]
        public void SameAreaAndDescription_CollapsesToLatest()
        {
            var clock = new TestClock();
            var list = new WebAdminPendingList(clock.Now);

            list.Add("items", "Saved database", "gm");
            clock.Advance(TimeSpan.FromMinutes(5));
            list.Add("ITEMS", "Saved database", "gm2");
            list.Add("items", "Other change", "gm");

            var snap = list.Snapshot();
            Assert.Equal(2, snap.Count);
            Assert.Equal("gm2", snap[0].Admin);
            Assert.Equal(clock.UtcNow, snap[0].Utc);
        }

        [Fact]
        public void Snapshot_IsACopy()
        {
            var list = new WebAdminPendingList();
            list.Add("npcs", "x", "gm");

            var snap = list.Snapshot();
            snap[0].Admin = "tampered";

            Assert.Equal("gm", list.Snapshot()[0].Admin);
        }

        [Fact]
        public void BlankValues_GetDefaults_AndListIsBounded()
        {
            var list = new WebAdminPendingList();
            list.Add(null, null, null);

            var item = Assert.Single(list.Snapshot());
            Assert.Equal("general", item.Area);
            Assert.Equal(string.Empty, item.Description);
            Assert.Equal("unknown", item.Admin);

            for (var i = 0; i < WebAdminPendingList.MaxItems + 20; i++) list.Add("a", "d" + i, "gm");
            Assert.Equal(WebAdminPendingList.MaxItems, list.Count);
        }

        [Fact]
        public void StaticTracker_ClearsWhenTheWorldStarts()
        {
            WebAdminPending.Add("maps", "Edited Bichon Province", "gm");
            Assert.True(WebAdminPending.Count >= 1);

            WebAdminHooks.RaiseWorldStarted();

            Assert.Equal(0, WebAdminPending.Count);
        }
    }
}

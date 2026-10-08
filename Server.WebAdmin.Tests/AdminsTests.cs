namespace Server.WebAdmin.Tests
{
    [Collection(WebAdminGlobalsCollection.Name)]
    public class AdminsTests : IDisposable
    {
        public void Dispose()
        {
            WebAdminGame.World = null;
            WebAdminAdmins.LiveLookup = DefaultLookup;
            WebAdminAdmins.AccountDbPath = DefaultDbPath;
        }

        private static readonly Func<string, WebAdminAdminEntry> DefaultLookup = WebAdminAdmins.LiveLookup;
        private static readonly Func<string> DefaultDbPath = WebAdminAdmins.AccountDbPath;

        private static WebAdminAdminEntry Entry(int index, string id, string password, bool admin = true, bool banned = false, DateTime? expiry = null,
                                                bool requirePasswordChange = false)
        {
            var salt = new byte[24];
            for (var i = 0; i < salt.Length; i++) salt[i] = (byte)(index * 31 + i);

            return new WebAdminAdminEntry
            {
                Index = index,
                AccountId = id,
                PasswordHash = global::Server.Utils.Crypto.HashPassword(password, salt),
                Salt = salt,
                AdminAccount = admin,
                Banned = banned,
                ExpiryDate = expiry ?? DateTime.MinValue,
                RequirePasswordChange = requirePasswordChange,
            };
        }

        [Fact]
        public void VerifyPassword_MatchesCrystalHash()
        {
            var e = Entry(1, "gm", "s3cret!");

            Assert.True(WebAdminAuth.VerifyPassword("s3cret!", e.PasswordHash, e.Salt));
            Assert.False(WebAdminAuth.VerifyPassword("S3cret!", e.PasswordHash, e.Salt));
            Assert.False(WebAdminAuth.VerifyPassword("", e.PasswordHash, e.Salt));
            Assert.False(WebAdminAuth.VerifyPassword("s3cret!", null, e.Salt));
            Assert.False(WebAdminAuth.VerifyPassword("s3cret!", e.PasswordHash, null));
        }

        [Fact]
        public void Serialize_RoundTrips_ExactHashStrings()
        {
            var now = DateTime.UtcNow;
            var entries = new[]
            {
                Entry(1, "gm", "pw1"),
                Entry(2, "Ops|Pipe", "pw2", banned: true, expiry: now.AddDays(1)),
            };

            var text = WebAdminAdmins.Serialize(entries);
            Assert.DoesNotContain("pw1", text);

            var back = WebAdminAdmins.Deserialize(text);

            Assert.Equal(2, back.Count);
            Assert.Equal("gm", back[0].AccountId);
            Assert.Equal(entries[0].PasswordHash, back[0].PasswordHash);
            Assert.Equal(entries[0].Salt, back[0].Salt);
            Assert.True(back[0].AdminAccount);
            Assert.Equal("Ops|Pipe", back[1].AccountId);
            Assert.True(back[1].Banned);
            Assert.Equal(entries[1].ExpiryDate, back[1].ExpiryDate);
            Assert.True(WebAdminAuth.VerifyPassword("pw2", back[1].PasswordHash, back[1].Salt));
        }

        [Fact]
        public void Serialize_RoundTrips_CaptureStamp_And_PasswordChangeFlag()
        {
            var captured = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
            var text = WebAdminAdmins.Serialize(new[] { Entry(1, "gm", "pw", requirePasswordChange: true), Entry(2, "ops", "pw") }, captured);

            var back = WebAdminAdmins.Deserialize(text, out var stamp);

            Assert.Equal(captured, stamp);
            Assert.Equal(2, back.Count);
            Assert.True(back[0].RequirePasswordChange);
            Assert.False(back[1].RequirePasswordChange);

            // Files written before the 8th field / stamp existed still load.
            var legacy = "MirCraft WebAdmins v1\n" + string.Join("|", text.Split('\n')[2].Split('|').Take(7)) + "\n";
            var old = WebAdminAdmins.Deserialize(legacy, out var noStamp);
            Assert.Single(old);
            Assert.Equal(DateTime.MinValue, noStamp);
            Assert.False(old[0].RequirePasswordChange);
        }

        [Fact]
        public void StaleCheck_RejectsMissingOrNewerAccountDatabase()
        {
            var dir = Path.Combine(Path.GetTempPath(), "mircraft-admins-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var db = Path.Combine(dir, "Server.MirADB");

            try
            {
                WebAdminAdmins.AccountDbPath = () => db;

                Assert.NotNull(WebAdminAdmins.CheckStale(DateTime.UtcNow)); // no account DB at all

                File.WriteAllBytes(db, new byte[] { 1, 2, 3 });
                var written = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
                File.SetCreationTimeUtc(db, written);
                File.SetLastWriteTimeUtc(db, written);

                Assert.Null(WebAdminAdmins.CheckStale(written));                        // captured with this DB
                Assert.Null(WebAdminAdmins.CheckStale(written - TimeSpan.FromMinutes(1))); // async save just after the capture
                Assert.NotNull(WebAdminAdmins.CheckStale(written - TimeSpan.FromHours(1))); // DB edited/restored later

                // A restored copy keeps an old LastWriteTime but gets a new CreationTime.
                File.SetLastWriteTimeUtc(db, written - TimeSpan.FromDays(3));
                File.SetCreationTimeUtc(db, written + TimeSpan.FromHours(2));
                Assert.NotNull(WebAdminAdmins.CheckStale(written));
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                }
            }
        }

        [Fact]
        public void PasswordChangeRequired_RefusesOfflineLogin_AndSessions()
        {
            WebAdminAdmins.PersistEnabled = false;
            var now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

            WebAdminAdmins.SetSnapshot(new[] { Entry(21, "MustChange", "right", requirePasswordChange: true) }, persist: false);

            Assert.Null(WebAdminAdmins.TryAuthenticate("MustChange", "right", now, out var reason));
            Assert.Equal("password-change-required", reason);
            Assert.False(WebAdminAdmins.IsActiveAdmin(21, "MustChange", now));
        }

        [Fact]
        public async Task LiveCheck_CutsOffDemotedOrBannedAdmins_WhileWorldRuns()
        {
            WebAdminAdmins.PersistEnabled = false;
            var now = DateTime.UtcNow;
            WebAdminAdmins.SetSnapshot(new[] { Entry(31, "LiveGM", "pw") }, persist: false);

            var live = Entry(31, "LiveGM", "pw");
            var lookups = 0;
            WebAdminAdmins.LiveLookup = id =>
            {
                lookups++;
                return string.Equals(id, live.AccountId, StringComparison.OrdinalIgnoreCase) ? live : null;
            };

            var store = new WebAdminSessionStore(TimeSpan.FromHours(8));
            var session = store.Create(31, "LiveGM", "127.0.0.1");

            // World stopped: directory only, no game-thread lookup.
            WebAdminGame.World = FakeWorld.Stopped();
            Assert.True(await WebAdminAdmins.IsSessionStillAdminAsync(session, now));
            Assert.Equal(0, lookups);

            // World running: live lookup, then cached for LiveCheckInterval.
            WebAdminGame.World = FakeWorld.RunningWorld();
            Assert.True(await WebAdminAdmins.IsSessionStillAdminAsync(session, now));
            Assert.Equal(1, lookups);
            Assert.True(await WebAdminAdmins.IsSessionStillAdminAsync(session, now + TimeSpan.FromSeconds(1)));
            Assert.Equal(1, lookups);

            // Demoted in the WinForms editor / by a GM command: the next check after the interval refuses and updates the directory.
            live = Entry(31, "LiveGM", "pw", admin: false);
            var later = now + WebAdminAdmins.LiveCheckInterval + TimeSpan.FromSeconds(1);
            Assert.False(await WebAdminAdmins.IsSessionStillAdminAsync(session, later));
            Assert.Null(WebAdminAdmins.Find(31));

            // Banned live account.
            WebAdminAdmins.SetSnapshot(new[] { Entry(32, "BanMe", "pw") }, persist: false);
            live = Entry(32, "BanMe", "pw", banned: true, expiry: now.AddDays(1));
            var s2 = store.Create(32, "BanMe", "127.0.0.1");
            Assert.False(await WebAdminAdmins.IsSessionStillAdminAsync(s2, now));
            Assert.False(WebAdminAdmins.IsActiveAdmin(32, "BanMe", now));

            // Deleted account.
            WebAdminAdmins.SetSnapshot(new[] { Entry(33, "Gone", "pw") }, persist: false);
            var s3 = store.Create(33, "Gone", "127.0.0.1");
            Assert.False(await WebAdminAdmins.IsSessionStillAdminAsync(s3, now));
            Assert.Null(WebAdminAdmins.Find(33));
        }

        [Fact]
        public void Deserialize_IgnoresWrongHeaderAndCorruptLines()
        {
            Assert.Empty(WebAdminAdmins.Deserialize("something else\n1|a|b|c|1|0|0\n"));

            var good = WebAdminAdmins.Serialize(new[] { Entry(3, "gm", "pw") });
            var withJunk = good + "garbage line\n4|!!notbase64|x|y|1|0|0\n";

            var back = WebAdminAdmins.Deserialize(withJunk);
            Assert.Single(back);
            Assert.Equal(3, back[0].Index);
        }

        [Fact]
        public void OfflineAuthentication_And_ActiveAdminCheck()
        {
            WebAdminAdmins.PersistEnabled = false;
            var now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

            WebAdminAdmins.SetSnapshot(new[]
            {
                Entry(1, "GameMaster", "right"),
                Entry(2, "BannedGM", "right", banned: true, expiry: now.AddHours(1)),
                Entry(3, "ExpiredBan", "right", banned: true, expiry: now.AddHours(-1)),
                Entry(4, "Demoted", "right", admin: false),
            }, persist: false);

            Assert.NotNull(WebAdminAdmins.TryAuthenticate("gamemaster", "right", now, out var reason));
            Assert.Null(reason);

            Assert.Null(WebAdminAdmins.TryAuthenticate("GameMaster", "wrong", now, out reason));
            Assert.Equal("bad-password", reason);

            Assert.Null(WebAdminAdmins.TryAuthenticate("BannedGM", "right", now, out reason));
            Assert.Equal("banned", reason);

            Assert.NotNull(WebAdminAdmins.TryAuthenticate("ExpiredBan", "right", now, out _));

            Assert.Null(WebAdminAdmins.TryAuthenticate("Demoted", "right", now, out reason));
            Assert.Equal("not-admin", reason);

            Assert.Null(WebAdminAdmins.TryAuthenticate("nobody", "right", now, out reason));
            Assert.Equal("no-account", reason);

            Assert.True(WebAdminAdmins.IsActiveAdmin(1, "GameMaster", now));
            Assert.False(WebAdminAdmins.IsActiveAdmin(1, "SomeoneElse", now));
            Assert.False(WebAdminAdmins.IsActiveAdmin(2, "BannedGM", now));
            Assert.False(WebAdminAdmins.IsActiveAdmin(4, "Demoted", now));
            Assert.False(WebAdminAdmins.IsActiveAdmin(99, "Ghost", now));

            // Promotion/demotion via Upsert.
            var promoted = Entry(5, "NewGM", "pw");
            WebAdminAdmins.Upsert(promoted);
            Assert.True(WebAdminAdmins.IsActiveAdmin(5, "NewGM", now));

            WebAdminAdmins.Upsert(Entry(5, "NewGM", "pw", admin: false));
            Assert.False(WebAdminAdmins.IsActiveAdmin(5, "NewGM", now));
        }
    }
}

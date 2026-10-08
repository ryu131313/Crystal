namespace Server.WebAdmin.Tests
{
    public class AuthTests
    {
        [Theory]
        [InlineData("application/json", "same-origin", "http://127.0.0.1:5680", "127.0.0.1:5680", null)]
        [InlineData("application/json; charset=utf-8", null, null, "127.0.0.1:5680", null)]
        [InlineData("Application/JSON", "none", null, "127.0.0.1:5680", null)]
        [InlineData("application/json", null, "http://127.0.0.1:5680", "127.0.0.1:5680", null)]
        [InlineData("application/json", null, "http://[::1]:5680", "[::1]:5680", null)]
        [InlineData("text/plain", "same-origin", null, "127.0.0.1:5680", "content-type")]
        [InlineData("application/x-www-form-urlencoded", null, null, "127.0.0.1:5680", "content-type")]
        [InlineData("multipart/form-data; boundary=x", null, null, "127.0.0.1:5680", "content-type")]
        [InlineData(null, null, null, "127.0.0.1:5680", "content-type")]
        [InlineData("application/json", "cross-site", "https://evil.example", "127.0.0.1:5680", "cross-site")]
        [InlineData("application/json", "same-site", "http://127.0.0.1:3000", "127.0.0.1:5680", "cross-site")]
        [InlineData("application/json", null, "https://evil.example", "127.0.0.1:5680", "cross-site")]
        [InlineData("application/json", null, "http://127.0.0.1:3000", "127.0.0.1:5680", "cross-site")]
        [InlineData("application/json", null, "null", "127.0.0.1:5680", "cross-site")]
        public void LoginRequest_RequiresJson_AndSameOrigin(string contentType, string secFetchSite, string origin, string host, string expected)
        {
            Assert.Equal(expected, WebAdminAuth.CheckLoginRequest(contentType, secFetchSite, origin, host));
        }

        [Theory]
        [InlineData(1, "starting")]
        [InlineData(1, "stopped")]
        [InlineData(3, "stopping")]
        public void OfflineSignIn_Available_WhenDirectoryHasEntries(int count, string world)
        {
            Assert.Null(WebAdminAuth.OfflineSignInUnavailableMessage(count, world));
        }

        [Fact]
        public void OfflineSignIn_EmptyDirectory_WhileStarting_SaysStarting()
        {
            var msg = WebAdminAuth.OfflineSignInUnavailableMessage(0, "starting");
            Assert.NotNull(msg);
            Assert.Contains("starting", msg);
            Assert.DoesNotContain("Invalid account", msg);
        }

        [Theory]
        [InlineData("stopped")]
        [InlineData("stopping")]
        public void OfflineSignIn_EmptyDirectory_WhileStopped_AsksToStartWorld(string world)
        {
            var msg = WebAdminAuth.OfflineSignInUnavailableMessage(0, world);
            Assert.NotNull(msg);
            Assert.Contains("Start the world", msg);
        }
    }
}

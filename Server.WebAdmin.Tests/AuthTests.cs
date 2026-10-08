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
    }
}

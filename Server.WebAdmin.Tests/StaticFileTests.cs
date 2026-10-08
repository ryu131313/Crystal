namespace Server.WebAdmin.Tests
{
    public class StaticFileTests
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "mircraft-webroot-tests", "wwwroot");

        [Fact]
        public void FileInsideRoot_Resolves()
        {
            var full = WebAdminStaticFiles.Resolve(Root, "/admin/app.js");
            Assert.Equal(Path.Combine(Root, "admin", "app.js"), full);
        }

        [Fact]
        public void EncodedSpaces_AreDecoded()
        {
            var full = WebAdminStaticFiles.Resolve(Root, "/shared/admin%20bg.jpg");
            Assert.Equal(Path.Combine(Root, "shared", "admin bg.jpg"), full);
        }

        [Fact]
        public void DirectoryRequest_MapsToIndexHtml()
        {
            Assert.Equal(Path.Combine(Root, "admin", "index.html"), WebAdminStaticFiles.Resolve(Root, "/admin/"));
            Assert.Equal(Path.Combine(Root, "index.html"), WebAdminStaticFiles.Resolve(Root, "/"));
        }

        [Fact]
        public void RootWithTrailingSeparator_StillWorks()
        {
            var full = WebAdminStaticFiles.Resolve(Root + Path.DirectorySeparatorChar, "/admin/login.html");
            Assert.Equal(Path.Combine(Root, "admin", "login.html"), full);
        }

        [Theory]
        [InlineData("/admin/../../secret.txt")]
        [InlineData("/admin/..%2f..%2fsecret.txt")]
        [InlineData("/admin/..%2F..%2Fsecret.txt")]
        [InlineData("/admin/%2e%2e/%2e%2e/secret.txt")]
        [InlineData("/admin/..%5c..%5csecret.txt")]
        [InlineData("/admin/..%5Csecret.txt")]
        [InlineData("/admin/a%2fb.js")]
        [InlineData("/admin/a\\b.js")]
        [InlineData("/C:/Windows/win.ini")]
        [InlineData("/admin/C:%5cWindows%5cwin.ini")]
        [InlineData("/admin/%00.js")]
        [InlineData("//etc/passwd")]
        [InlineData("/admin//app.js")]
        [InlineData("/admin/./app.js")]
        [InlineData("/..")]
        [InlineData("/admin/app.js.")]
        [InlineData("/admin/app.js%20")]
        [InlineData("/admin/app.js::$DATA")]
        [InlineData("/admin/%7e1.js")]
        [InlineData("admin/app.js")]
        [InlineData("")]
        [InlineData("/shared/COM1")]
        [InlineData("/shared/aux.css")]
        [InlineData("/admin/nul.txt")]
        [InlineData("/admin/CON")]
        [InlineData("/admin/con.tar.gz")]
        [InlineData("/admin/Lpt9.js")]
        [InlineData("/admin/prn")]
        [InlineData("/admin/CONIN$")]
        [InlineData("/admin/conout$.js")]
        [InlineData("/admin/com%C2%B9.js")]
        [InlineData("/admin/nul/app.js")]
        public void TraversalAndAbsoluteAttempts_AreRejected(string rawPath)
        {
            Assert.Null(WebAdminStaticFiles.Resolve(Root, rawPath));
        }

        [Theory]
        [InlineData("console.js", false)]
        [InlineData("company.css", false)]
        [InlineData("com10.js", false)]
        [InlineData("auxiliary.png", false)]
        [InlineData("null.js", false)]
        [InlineData("COM0", true)]
        [InlineData("aux .txt", true)]
        [InlineData("clock$", true)]
        public void ReservedDeviceNames(string segment, bool reserved)
        {
            Assert.Equal(reserved, WebAdminStaticFiles.IsReservedDeviceName(segment));
        }

        [Fact]
        public void SiblingFolderWithSamePrefix_CannotBeReached()
        {
            // Root "...\wwwroot" must not allow "...\wwwroot2\x" even though the string prefix matches.
            Assert.Null(WebAdminStaticFiles.Resolve(Root, "/..%2fwwwroot2%2fx.txt"));
            Assert.Null(WebAdminStaticFiles.Resolve(Root, "/../wwwroot2/x.txt"));
        }

        [Fact]
        public void QueryString_IsIgnored()
        {
            Assert.Equal(Path.Combine(Root, "admin", "app.js"), WebAdminStaticFiles.Resolve(Root, "/admin/app.js?v=3"));
        }

        [Theory]
        [InlineData("a.html", "text/html; charset=utf-8")]
        [InlineData("a.js", "application/javascript; charset=utf-8")]
        [InlineData("a.css", "text/css; charset=utf-8")]
        [InlineData("a.json", "application/json; charset=utf-8")]
        [InlineData("a.PNG", "image/png")]
        [InlineData("a.jpg", "image/jpeg")]
        [InlineData("a.gif", "image/gif")]
        [InlineData("a.svg", "image/svg+xml")]
        [InlineData("a.ico", "image/x-icon")]
        [InlineData("a.woff", "font/woff")]
        [InlineData("a.woff2", "font/woff2")]
        [InlineData("a.txt", "text/plain; charset=utf-8")]
        [InlineData("a.md", "text/markdown; charset=utf-8")]
        [InlineData("a.exe", "application/octet-stream")]
        public void MimeMap(string file, string expected)
        {
            Assert.Equal(expected, WebAdminStaticFiles.GetMime(file));
        }

        [Fact]
        public void HtmlJsCss_AreNoCache()
        {
            Assert.True(WebAdminStaticFiles.IsNoCache("x/index.html"));
            Assert.True(WebAdminStaticFiles.IsNoCache("x/app.js"));
            Assert.True(WebAdminStaticFiles.IsNoCache("x/style.css"));
            Assert.False(WebAdminStaticFiles.IsNoCache("x/bg.jpg"));
        }

        [Fact]
        public void LoginPage_IsTheOnlyAnonymousAdminAsset()
        {
            Assert.True(WebAdminHost.IsAnonymousAdminAsset("/admin/login.html"));
            Assert.True(WebAdminHost.IsAnonymousAdminAsset("/admin/login.html?next=%2Fadmin%2F"));
            Assert.False(WebAdminHost.IsAnonymousAdminAsset("/admin/index.html"));
            Assert.False(WebAdminHost.IsAnonymousAdminAsset("/admin/app.js"));
        }

        [Fact]
        public void Options_ResolveWebRoot_AndPrefix()
        {
            var baseDir = Path.Combine(Path.GetTempPath(), "mircraft-base");

            Assert.Equal(Path.Combine(baseDir, "wwwroot"), WebAdminOptions.ResolveWebRoot(".\\wwwroot", baseDir));
            Assert.Equal(Path.Combine(baseDir, "wwwroot"), WebAdminOptions.ResolveWebRoot("wwwroot", baseDir));
            Assert.Equal(Path.Combine(baseDir, "wwwroot"), WebAdminOptions.ResolveWebRoot("", baseDir));

            var absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "web");
            Assert.Equal(absolute, WebAdminOptions.ResolveWebRoot(absolute, baseDir));

            Assert.Equal("http://127.0.0.1:5680/", new WebAdminOptions { Bind = "127.0.0.1", Port = 5680 }.Prefix);
            Assert.Equal("http://+:5680/", new WebAdminOptions { Bind = "0.0.0.0", Port = 5680 }.Prefix);
            Assert.Equal("http://+:81/", new WebAdminOptions { Bind = "*", Port = 81 }.Prefix);
            Assert.Equal("http://127.0.0.1:5680/admin/", new WebAdminOptions { Bind = "127.0.0.1", Port = 5680 }.DisplayUrl);
        }
    }
}

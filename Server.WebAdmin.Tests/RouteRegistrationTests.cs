using System.Reflection;

namespace Server.WebAdmin.Tests
{
    /// <summary>
    /// Registers every <see cref="IWebAdminRoutes"/> module of Server.Library into a fresh router (the host is never started) and checks
    /// the cross-feature endpoint contracts (FEATURES.md) resolve. Some Register() calls subscribe process-wide hooks or start the
    /// status sampler's timer; all of those are idempotent and the timer is a no-op while the host is not running, so this collection
    /// only keeps the test away from the tests that swap the same statics.
    /// </summary>
    [Collection(WebAdminGlobalsCollection.Name)]
    public class RouteRegistrationTests
    {
        private static IReadOnlyList<Type> RouteModules()
        {
            Type[] types;
            try
            {
                types = typeof(WebAdminRouter).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
            }

            return types
                .Where(t => typeof(IWebAdminRoutes).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract && !t.ContainsGenericParameters)
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Registers each module itself (RegisterAll only logs failures), so a throwing Register() fails the test.</summary>
        private static WebAdminRouter RegisterEveryModule()
        {
            var router = new WebAdminRouter();
            var failures = new List<string>();

            foreach (var type in RouteModules())
            {
                try
                {
                    var module = (IWebAdminRoutes)Activator.CreateInstance(type, nonPublic: true);
                    module.Register(router);
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    failures.Add(type.FullName + ": " + inner.GetType().Name + ": " + inner.Message);
                }
            }

            Assert.True(failures.Count == 0, "Register() threw:\n" + string.Join("\n", failures));
            return router;
        }

        [Fact]
        public void EveryModule_Registers_WithoutException()
        {
            Assert.NotEmpty(RouteModules());
            var router = RegisterEveryModule();
            Assert.NotEmpty(router.Routes);
        }

        [Fact]
        public void EveryModule_Registers_Twice_IntoFreshRouters()
        {
            // Register() runs once per host start; process-wide side effects (hooks, the sampler timer) must be idempotent.
            RegisterEveryModule();
            RegisterEveryModule();
        }

        [Fact]
        public void NoDuplicate_MethodAndPattern()
        {
            var router = RegisterEveryModule();

            var duplicates = router.Routes
                .GroupBy(r => r.Method + " " + r.Pattern.TrimEnd('/').ToLowerInvariant())
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.True(duplicates.Count == 0, "Duplicate routes: " + string.Join(", ", duplicates));
        }

        public static IEnumerable<object[]> ContractRoutes() => new[]
        {
            // GM action (owner B4)
            new object[] { "POST", "/api/admin/gm/action", "/api/admin/gm/action" },
            new object[] { "GET", "/api/admin/gm/caps", "/api/admin/gm/caps" },
            new object[] { "GET", "/api/admin/gm/target", "/api/admin/gm/target" },
            // Account actions (owner B3)
            new object[] { "POST", "/api/admin/accounts/12/ban", "/api/admin/accounts/{index:int}/ban" },
            new object[] { "POST", "/api/admin/accounts/12/unban", "/api/admin/accounts/{index:int}/unban" },
            // Server control (owner B1)
            new object[] { "POST", "/api/admin/server/reboot", "/api/admin/server/reboot" },
            new object[] { "POST", "/api/admin/server/reload/npcs", "/api/admin/server/reload/{kind}" },
            new object[] { "POST", "/api/admin/server/reload/drops", "/api/admin/server/reload/{kind}" },
            new object[] { "POST", "/api/admin/server/reload/linemessages", "/api/admin/server/reload/{kind}" },
            new object[] { "POST", "/api/admin/server/reload/recipes", "/api/admin/server/reload/{kind}" },
            // Character detail (owner B2)
            new object[] { "GET", "/api/admin/characters/7", "/api/admin/characters/{index:int}" },
            // Game Data (owner B8), used by B9 / B10 / B11
            new object[] { "GET", "/api/admin/db/items", "/api/admin/db/{endpoint}" },
            new object[] { "PUT", "/api/admin/db/items", "/api/admin/db/{endpoint}" },
            new object[] { "POST", "/api/admin/db/rename-propagate", "/api/admin/db/rename-propagate" },
            new object[] { "POST", "/api/admin/database/save", "/api/admin/database/save" },
            new object[] { "GET", "/api/admin/database/status", "/api/admin/database/status" },
            new object[] { "POST", "/api/admin/database/reload-edit", "/api/admin/database/reload-edit" },
            // Drops editor (Drops-Tables-Plan phase 2)
            new object[] { "GET", "/api/admin/drops/lists", "/api/admin/drops/lists" },
            new object[] { "GET", "/api/admin/drops/monster", "/api/admin/drops/monster" },
            new object[] { "GET", "/api/admin/drops/list", "/api/admin/drops/list" },
            new object[] { "POST", "/api/admin/drops/save", "/api/admin/drops/save" },
            new object[] { "POST", "/api/admin/drops/create", "/api/admin/drops/create" },
            new object[] { "POST", "/api/admin/drops/copy", "/api/admin/drops/copy" },
            new object[] { "POST", "/api/admin/drops/delete-disabled", "/api/admin/drops/delete-disabled" },
            // History (owner B11) must win over db/{endpoint}
            new object[] { "GET", "/api/admin/db/history", "/api/admin/db/history" },
            // LiveDb (Batch A)
            new object[] { "GET", "/api/admin/livedb/status", "/api/admin/livedb/status" },
            new object[] { "POST", "/api/admin/livedb/batch", "/api/admin/livedb/batch" },
            new object[] { "GET", "/api/admin/livedb/schema/items", "/api/admin/livedb/schema/{endpoint}" },
            new object[] { "GET", "/api/admin/livedb/history", "/api/admin/livedb/history" },
            // Live Monitor
            new object[] { "GET", "/api/admin/monitor/snapshot", "/api/admin/monitor/snapshot" },
            new object[] { "GET", "/api/admin/monitor/heat", "/api/admin/monitor/heat" },
            new object[] { "GET", "/api/admin/monitor/maps/12/image", "/api/admin/monitor/maps/{index:int}/image" },
            new object[] { "GET", "/api/admin/monitor/maps/12/minimap", "/api/admin/monitor/maps/{index:int}/minimap" },
            new object[] { "GET", "/api/admin/monitor/maps/12/tiles/2/3/4", "/api/admin/monitor/maps/{index:int}/tiles/{z:int}/{tx:int}/{ty:int}" },
            // Shell
            new object[] { "GET", "/api/admin/status", "/api/admin/status" },
            new object[] { "POST", "/api/auth/login", "/api/auth/login" },
        };

        [Theory]
        [MemberData(nameof(ContractRoutes))]
        public void ContractRoute_Matches(string method, string path, string expectedPattern)
        {
            var router = RegisterEveryModule();

            var match = router.Match(method, path);

            Assert.True(match.Success, method + " " + path + " did not match any route" + (match.MethodNotAllowed ? " (method not allowed: " + string.Join(",", match.AllowedMethods) + ")" : string.Empty));
            Assert.Equal(expectedPattern, match.Route.Pattern);
        }
    }
}

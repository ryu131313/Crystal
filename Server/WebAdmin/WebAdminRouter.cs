using System.Globalization;
using System.Reflection;

namespace Server.WebAdmin
{
    /// <summary>Every non-abstract implementer in the Server.Library assembly is instantiated (parameterless ctor) and registered at host start.</summary>
    public interface IWebAdminRoutes
    {
        void Register(WebAdminRouter router);
    }

    public enum RouteAuth
    {
        /// <summary>Needs a valid admin session; CSRF is enforced for POST/PUT/PATCH/DELETE. Default for /api/admin/*.</summary>
        Admin = 0,

        /// <summary>No session needed (the handler may still look at req.Session). Default for /api/auth/*.</summary>
        Anonymous = 1,
    }

    public sealed class WebAdminRoute
    {
        internal WebAdminRoute(string method, string pattern, Func<WebAdminRequest, Task> handler, RouteAuth auth, RouteSegment[] segments)
        {
            Method = method;
            Pattern = pattern;
            Handler = handler;
            Auth = auth;
            Segments = segments;
        }

        public string Method { get; }
        public string Pattern { get; }
        public Func<WebAdminRequest, Task> Handler { get; }
        public RouteAuth Auth { get; }

        internal RouteSegment[] Segments { get; }

        /// <summary>Specificity used to pick between several matching patterns: literal &gt; int &gt; any, leftmost segment first.</summary>
        internal string Specificity
        {
            get
            {
                var chars = new char[Segments.Length];
                for (var i = 0; i < Segments.Length; i++)
                    chars[i] = Segments[i].Kind == RouteSegmentKind.Literal ? '2' : Segments[i].Kind == RouteSegmentKind.Int ? '1' : '0';
                return new string(chars);
            }
        }

        public override string ToString() => Method + " " + Pattern;
    }

    internal enum RouteSegmentKind
    {
        Literal,
        Any,
        Int,
    }

    internal sealed class RouteSegment
    {
        public RouteSegmentKind Kind;
        public string Text;
    }

    /// <summary>Route values ({name} placeholders), case-insensitive; missing → null.</summary>
    public sealed class WebAdminRouteValues
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string this[string name]
        {
            get => name != null && _values.TryGetValue(name, out var v) ? v : null;
            internal set => _values[name] = value;
        }

        public bool Contains(string name) => name != null && _values.ContainsKey(name);

        public IReadOnlyDictionary<string, string> All => _values;

        public int Count => _values.Count;
    }

    public sealed class WebAdminRouteMatch
    {
        public WebAdminRoute Route { get; internal set; }
        public WebAdminRouteValues Values { get; internal set; } = new WebAdminRouteValues();

        /// <summary>The path matched at least one pattern, but not for this HTTP method (→ 405).</summary>
        public bool MethodNotAllowed { get; internal set; }

        public IReadOnlyList<string> AllowedMethods { get; internal set; } = Array.Empty<string>();

        public bool Success => Route != null;
    }

    /// <summary>
    /// Route table with simple pattern matching. Patterns: literal segments (case-insensitive), {name} (any non-empty segment),
    /// {name:int} (32-bit integer segment). Trailing slashes are ignored. Segments are URL-decoded individually, so an encoded
    /// "/" (%2F) never splits a segment.
    /// </summary>
    public sealed class WebAdminRouter
    {
        private readonly List<WebAdminRoute> _routes = new List<WebAdminRoute>();
        private readonly object _gate = new object();

        public IReadOnlyList<WebAdminRoute> Routes
        {
            get
            {
                lock (_gate) return _routes.ToArray();
            }
        }

        public WebAdminRoute Get(string pattern, Func<WebAdminRequest, Task> handler, RouteAuth? auth = null) => Map("GET", pattern, handler, auth);

        public WebAdminRoute Post(string pattern, Func<WebAdminRequest, Task> handler, RouteAuth? auth = null) => Map("POST", pattern, handler, auth);

        public WebAdminRoute Put(string pattern, Func<WebAdminRequest, Task> handler, RouteAuth? auth = null) => Map("PUT", pattern, handler, auth);

        public WebAdminRoute Patch(string pattern, Func<WebAdminRequest, Task> handler, RouteAuth? auth = null) => Map("PATCH", pattern, handler, auth);

        public WebAdminRoute Delete(string pattern, Func<WebAdminRequest, Task> handler, RouteAuth? auth = null) => Map("DELETE", pattern, handler, auth);

        /// <summary>
        /// Registers a route. Default auth: Anonymous for /api/auth/*, Admin for everything else (incl. /api/admin/*).
        /// Registering the same method + pattern twice throws.
        /// </summary>
        public WebAdminRoute Map(string method, string pattern, Func<WebAdminRequest, Task> handler, RouteAuth? auth = null)
        {
            if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("method required", nameof(method));
            if (string.IsNullOrWhiteSpace(pattern) || pattern[0] != '/') throw new ArgumentException("pattern must start with '/'", nameof(pattern));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            method = method.Trim().ToUpperInvariant();
            var segments = ParsePattern(pattern);
            var effectiveAuth = auth ?? DefaultAuth(pattern);
            var route = new WebAdminRoute(method, pattern, handler, effectiveAuth, segments);

            lock (_gate)
            {
                foreach (var existing in _routes)
                {
                    if (existing.Method == method && SameShape(existing.Segments, segments))
                        throw new InvalidOperationException($"Duplicate web admin route: {method} {pattern} (already registered as {existing.Pattern})");
                }

                _routes.Add(route);
            }

            return route;
        }

        public static RouteAuth DefaultAuth(string pattern)
        {
            return pattern.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase) || string.Equals(pattern.TrimEnd('/'), "/api/auth", StringComparison.OrdinalIgnoreCase)
                ? RouteAuth.Anonymous
                : RouteAuth.Admin;
        }

        /// <summary>Matches a raw (still percent-encoded) URL path, without query string.</summary>
        public WebAdminRouteMatch Match(string method, string rawPath)
        {
            var result = new WebAdminRouteMatch();
            method = (method ?? string.Empty).Trim().ToUpperInvariant();

            var segments = SplitPath(rawPath);
            if (segments == null) return result;

            WebAdminRoute best = null;
            WebAdminRouteValues bestValues = null;
            string bestScore = null;
            var allowed = new List<string>();

            WebAdminRoute[] routes;
            lock (_gate) routes = _routes.ToArray();

            foreach (var route in routes)
            {
                var values = TryMatch(route.Segments, segments);
                if (values == null) continue;

                if (route.Method != method)
                {
                    if (!allowed.Contains(route.Method)) allowed.Add(route.Method);
                    continue;
                }

                var score = route.Specificity;
                if (best == null || string.CompareOrdinal(score, bestScore) > 0)
                {
                    best = route;
                    bestValues = values;
                    bestScore = score;
                }
            }

            if (best != null)
            {
                result.Route = best;
                result.Values = bestValues;
                return result;
            }

            if (allowed.Count > 0)
            {
                result.MethodNotAllowed = true;
                result.AllowedMethods = allowed;
            }

            return result;
        }

        /// <summary>Instantiates and registers every non-abstract <see cref="IWebAdminRoutes"/> in <paramref name="assembly"/>. Failures are logged per type.</summary>
        public static int RegisterAll(WebAdminRouter router, Assembly assembly)
        {
            if (router == null) throw new ArgumentNullException(nameof(router));
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray();
            }

            var count = 0;

            foreach (var type in types.Where(t => typeof(IWebAdminRoutes).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract && !t.ContainsGenericParameters)
                                      .OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                try
                {
                    var instance = (IWebAdminRoutes)Activator.CreateInstance(type, nonPublic: true);
                    instance.Register(router);
                    count++;
                }
                catch (Exception ex)
                {
                    WebAdminLog.Error(ex, "Failed to register web admin routes from " + type.FullName);
                }
            }

            return count;
        }

        // ---------------------------------------------------------------------------------------------

        internal static RouteSegment[] ParsePattern(string pattern)
        {
            var parts = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var result = new RouteSegment[parts.Length];
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];

                if (part.Length >= 2 && part[0] == '{' && part[part.Length - 1] == '}')
                {
                    var inner = part.Substring(1, part.Length - 2);
                    var colon = inner.IndexOf(':');
                    var name = colon >= 0 ? inner.Substring(0, colon) : inner;
                    var constraint = colon >= 0 ? inner.Substring(colon + 1) : null;

                    if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Empty route parameter name in " + pattern);
                    if (!names.Add(name)) throw new ArgumentException("Duplicate route parameter '" + name + "' in " + pattern);

                    RouteSegmentKind kind;
                    if (constraint == null) kind = RouteSegmentKind.Any;
                    else if (string.Equals(constraint, "int", StringComparison.OrdinalIgnoreCase)) kind = RouteSegmentKind.Int;
                    else throw new ArgumentException("Unknown route constraint '" + constraint + "' in " + pattern);

                    result[i] = new RouteSegment { Kind = kind, Text = name };
                }
                else
                {
                    if (part.IndexOf('{') >= 0 || part.IndexOf('}') >= 0) throw new ArgumentException("Malformed route segment '" + part + "' in " + pattern);
                    result[i] = new RouteSegment { Kind = RouteSegmentKind.Literal, Text = part };
                }
            }

            return result;
        }

        /// <summary>Splits a raw path into decoded segments (empty segments, e.g. a trailing slash, are dropped). Null when undecodable.</summary>
        internal static string[] SplitPath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath)) return Array.Empty<string>();

            var q = rawPath.IndexOf('?');
            if (q >= 0) rawPath = rawPath.Substring(0, q);

            var parts = rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var result = new string[parts.Length];

            for (var i = 0; i < parts.Length; i++)
            {
                try
                {
                    result[i] = Uri.UnescapeDataString(parts[i]);
                }
                catch
                {
                    return null;
                }
            }

            return result;
        }

        private static WebAdminRouteValues TryMatch(RouteSegment[] pattern, string[] path)
        {
            if (pattern.Length != path.Length) return null;

            var values = new WebAdminRouteValues();

            for (var i = 0; i < pattern.Length; i++)
            {
                var seg = pattern[i];
                var value = path[i];

                switch (seg.Kind)
                {
                    case RouteSegmentKind.Literal:
                        if (!string.Equals(seg.Text, value, StringComparison.OrdinalIgnoreCase)) return null;
                        break;
                    case RouteSegmentKind.Int:
                        if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) return null;
                        values[seg.Text] = value;
                        break;
                    default:
                        if (value.Length == 0) return null;
                        values[seg.Text] = value;
                        break;
                }
            }

            return values;
        }

        private static bool SameShape(RouteSegment[] a, RouteSegment[] b)
        {
            if (a.Length != b.Length) return false;

            for (var i = 0; i < a.Length; i++)
            {
                if (a[i].Kind != b[i].Kind) return false;
                if (a[i].Kind == RouteSegmentKind.Literal && !string.Equals(a[i].Text, b[i].Text, StringComparison.OrdinalIgnoreCase)) return false;
            }

            return true;
        }
    }
}

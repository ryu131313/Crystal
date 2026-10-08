using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Server.WebAdmin
{
    /// <summary>Parsed query string: case-insensitive, UTF-8 percent-decoding, '+' = space. Missing → null.</summary>
    public sealed class WebAdminQuery
    {
        private readonly Dictionary<string, List<string>> _values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public static WebAdminQuery Parse(string rawQuery)
        {
            var q = new WebAdminQuery();
            if (string.IsNullOrEmpty(rawQuery)) return q;

            if (rawQuery[0] == '?') rawQuery = rawQuery.Substring(1);

            foreach (var pair in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = Decode(eq >= 0 ? pair.Substring(0, eq) : pair);
                var value = eq >= 0 ? Decode(pair.Substring(eq + 1)) : string.Empty;

                if (string.IsNullOrEmpty(key)) continue;

                if (!q._values.TryGetValue(key, out var list))
                    q._values[key] = list = new List<string>();

                list.Add(value);
            }

            return q;
        }

        /// <summary>First value for the key, or null.</summary>
        public string this[string key] => key != null && _values.TryGetValue(key, out var list) && list.Count > 0 ? list[0] : null;

        public IReadOnlyList<string> GetAll(string key) => key != null && _values.TryGetValue(key, out var list) ? list : (IReadOnlyList<string>)Array.Empty<string>();

        public bool Has(string key) => key != null && _values.ContainsKey(key);

        public IEnumerable<string> Keys => _values.Keys;

        public int Int(string key, int fallback)
        {
            var s = this[key];
            return s != null && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        public long Long(string key, long fallback)
        {
            var s = this[key];
            return s != null && long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        public bool Bool(string key, bool fallback)
        {
            var s = this[key];
            if (s == null) return fallback;

            s = s.Trim();
            if (s.Length == 0) return true; // "?flag"
            if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase) || s.Equals("on", StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase) || s.Equals("no", StringComparison.OrdinalIgnoreCase) || s.Equals("off", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        private static string Decode(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            try
            {
                return Uri.UnescapeDataString(s.Replace('+', ' '));
            }
            catch
            {
                return s;
            }
        }
    }

    /// <summary>
    /// Per-request context + response helpers. Each response helper sends and completes the response; call exactly one.
    /// The helpers write synchronously and return a completed Task, so handlers can simply <c>return req.Ok(data);</c>.
    /// </summary>
    public sealed class WebAdminRequest
    {
        private readonly HttpListenerContext _context;
        private readonly long _maxBodyBytes;
        private string _bodyCache;
        private int _responded;

        internal WebAdminRequest(HttpListenerContext context, long maxBodyBytes, CancellationToken cancellationToken)
        {
            _context = context;
            _maxBodyBytes = maxBodyBytes;
            CancellationToken = cancellationToken;

            var request = context.Request;
            Method = (request.HttpMethod ?? "GET").ToUpperInvariant();
            RawPath = request.Url?.AbsolutePath ?? "/";

            try
            {
                Path = Uri.UnescapeDataString(RawPath);
            }
            catch
            {
                Path = RawPath;
            }

            Query = WebAdminQuery.Parse(request.Url?.Query);
            ClientIp = request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
        }

        public string Method { get; }

        /// <summary>Decoded URL path (display/logging). Routing uses <see cref="RawPath"/> split per segment.</summary>
        public string Path { get; }

        /// <summary>Percent-encoded URL path as received.</summary>
        public string RawPath { get; }

        public WebAdminQuery Query { get; }

        public WebAdminRouteValues Route { get; internal set; } = new WebAdminRouteValues();

        public string ClientIp { get; }

        /// <summary>Null for anonymous requests.</summary>
        public WebAdminSession Session { get; internal set; }

        /// <summary>Session.AccountId, or "anonymous".</summary>
        public string AdminName => Session?.AccountId ?? "anonymous";

        public CancellationToken CancellationToken { get; }

        public bool Responded => Volatile.Read(ref _responded) != 0;

        public bool IsMutating => Method == "POST" || Method == "PUT" || Method == "PATCH" || Method == "DELETE";

        internal HttpListenerContext Context => _context;

        public string Header(string name) => _context.Request.Headers[name];

        public string Cookie(string name) => WebAdminSessions.GetCookie(_context.Request.Headers["Cookie"], name);

        public string UserAgent => _context.Request.UserAgent;

        public int QueryInt(string name, int fallback = 0) => Query.Int(name, fallback);

        public long QueryLong(string name, long fallback = 0) => Query.Long(name, fallback);

        public bool QueryBool(string name, bool fallback = false) => Query.Bool(name, fallback);

        /// <summary>Route value as int; 400 when missing or not an integer.</summary>
        public int RouteInt(string name)
        {
            var s = Route[name];
            if (s == null || !int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v))
                throw new WebAdminException(400, "Invalid route value: " + name);
            return v;
        }

        // ---------------------------------------------------------------- body

        /// <summary>Reads the body as UTF-8 text (cached). 413 when over WebAdminMaxBodyKB.</summary>
        public async Task<string> ReadBodyAsync()
        {
            if (_bodyCache != null) return _bodyCache;

            var request = _context.Request;
            if (!request.HasEntityBody)
                return _bodyCache = string.Empty;

            if (request.ContentLength64 > _maxBodyBytes)
                throw new WebAdminException(413, "Request body too large");

            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[8192];
                var input = request.InputStream;
                long total = 0;
                int read;

                while ((read = await input.ReadAsync(chunk, 0, chunk.Length, CancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > _maxBodyBytes)
                        throw new WebAdminException(413, "Request body too large");
                    buffer.Write(chunk, 0, read);
                }

                var bytes = buffer.ToArray();
                var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                _bodyCache = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
            }

            return _bodyCache;
        }

        /// <summary>Deserializes the JSON body (WebAdminJson.Options). Empty body → default(T). 400 on malformed JSON, 413 when too large.</summary>
        public async Task<T> ReadJsonAsync<T>()
        {
            var body = await ReadBodyAsync().ConfigureAwait(false);
            return WebAdminJson.Deserialize<T>(body);
        }

        /// <summary>Parses the JSON body to a detached JsonElement (ValueKind Undefined for an empty body).</summary>
        public async Task<JsonElement> ReadJsonAsync()
        {
            var body = await ReadBodyAsync().ConfigureAwait(false);
            return WebAdminJson.Parse(body);
        }

        // ---------------------------------------------------------------- responses

        public Task Json(object body, int status = 200)
        {
            var bytes = WebAdminJson.SerializeToUtf8Bytes(body);
            WriteBody(status, "application/json; charset=utf-8", bytes, null);
            return Task.CompletedTask;
        }

        /// <summary>{"ok":true,"message":...,"data":...} (null members omitted).</summary>
        public Task Ok(object data = null, string message = null)
        {
            return Json(new ApiResult { Ok = true, Message = message, Data = data });
        }

        /// <summary>{"ok":false,"message":...,"data":...}.</summary>
        public Task Error(int status, string message, object data = null)
        {
            return Json(new ApiResult { Ok = false, Message = message, Data = data }, status);
        }

        public Task Text(string text, string contentType = "text/plain; charset=utf-8", int status = 200)
        {
            WriteBody(status, contentType, Encoding.UTF8.GetBytes(text ?? string.Empty), null);
            return Task.CompletedTask;
        }

        /// <summary>Binary response; a non-empty fileName makes it an attachment download.</summary>
        public Task Bytes(byte[] data, string contentType, string fileName = null)
        {
            WriteBody(200, string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType, data ?? Array.Empty<byte>(), fileName);
            return Task.CompletedTask;
        }

        public Task Redirect(string location, int status = 302)
        {
            if (!TryBeginResponse()) return Task.CompletedTask;

            var response = _context.Response;
            response.StatusCode = status;
            response.RedirectLocation = location;
            response.ContentLength64 = 0;
            CloseQuietly();
            return Task.CompletedTask;
        }

        /// <summary>Empty response with the given status (e.g. 204, 304).</summary>
        public Task Status(int status)
        {
            if (!TryBeginResponse()) return Task.CompletedTask;

            var response = _context.Response;
            response.StatusCode = status;
            response.ContentLength64 = 0;
            CloseQuietly();
            return Task.CompletedTask;
        }

        public void SetHeader(string name, string value)
        {
            if (Responded) return;
            _context.Response.Headers[name] = value;
        }

        public void AddHeader(string name, string value)
        {
            if (Responded) return;
            _context.Response.Headers.Add(name, value);
        }

        public void AddCookie(string setCookieValue)
        {
            AddHeader("Set-Cookie", setCookieValue);
        }

        internal void WriteBody(int status, string contentType, byte[] bytes, string fileName, bool allowGzip = true)
        {
            if (!TryBeginResponse()) return;

            var response = _context.Response;

            try
            {
                response.StatusCode = status;
                response.ContentType = contentType;

                if (!string.IsNullOrEmpty(fileName))
                    response.Headers["Content-Disposition"] = BuildContentDisposition(fileName);

                if (allowGzip && bytes.Length >= 2048 && IsCompressible(contentType) && AcceptsGzip())
                {
                    bytes = Gzip(bytes);
                    response.Headers["Content-Encoding"] = "gzip";
                    response.Headers["Vary"] = "Accept-Encoding";
                }

                response.ContentLength64 = bytes.Length;

                if (Method != "HEAD" && bytes.Length > 0)
                    response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (HttpListenerException)
            {
                // client went away
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                CloseQuietly();
            }
        }

        internal bool TryBeginResponse()
        {
            return Interlocked.Exchange(ref _responded, 1) == 0;
        }

        internal void CloseQuietly()
        {
            try
            {
                _context.Response.Close();
            }
            catch
            {
                // ignore: client disconnected
            }
        }

        private bool AcceptsGzip()
        {
            var accept = _context.Request.Headers["Accept-Encoding"];
            return accept != null && accept.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsCompressible(string contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return false;
            return contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("application/javascript", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("image/svg", StringComparison.OrdinalIgnoreCase);
        }

        private static byte[] Gzip(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                using (var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
                    gz.Write(data, 0, data.Length);
                return output.ToArray();
            }
        }

        private static string BuildContentDisposition(string fileName)
        {
            var safe = new StringBuilder();
            foreach (var c in fileName)
            {
                if (c < 0x20 || c > 0x7E || c == '"' || c == '\\' || c == ';') safe.Append('_');
                else safe.Append(c);
            }

            return "attachment; filename=\"" + safe + "\"; filename*=UTF-8''" + Uri.EscapeDataString(fileName);
        }

        /// <summary>Standard API envelope.</summary>
        public sealed class ApiResult
        {
            public bool Ok { get; set; }
            public string Message { get; set; }
            public object Data { get; set; }
        }
    }
}

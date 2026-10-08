using System.Text.Json;

namespace Server.WebAdmin.Tests
{
    public class JsonTests
    {
        public enum Shade
        {
            Light = 0,
            Medium = 1,
            Dark = 2,
        }

        public class SampleDto
        {
            public string AccountName { get; set; }
            public int PublicField;
            public string NullValue { get; set; }
            public Shade Shade { get; set; }
            public DateTime When { get; set; }
            public Dictionary<string, object> Row { get; set; }
        }

        [Fact]
        public void DictionaryKeys_AreNotRenamed()
        {
            var row = new Dictionary<string, object>
            {
                ["Index"] = 5,
                ["Name"] = "Sword",
                ["RequiredClass"] = 3,
                ["__rev"] = "abc",
            };

            var json = WebAdminJson.Serialize(new { rows = new[] { row } });

            Assert.Contains("\"rows\":", json);
            Assert.Contains("\"Index\":5", json);
            Assert.Contains("\"Name\":\"Sword\"", json);
            Assert.Contains("\"RequiredClass\":3", json);
            Assert.Contains("\"__rev\":\"abc\"", json);
        }

        [Fact]
        public void Dto_IsCamelCase_IncludesFields_OmitsNulls()
        {
            var json = WebAdminJson.Serialize(new SampleDto { AccountName = "gm", PublicField = 9, Shade = Shade.Dark, When = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) });

            Assert.Contains("\"accountName\":\"gm\"", json);
            Assert.Contains("\"publicField\":9", json);
            Assert.DoesNotContain("nullValue", json);
            Assert.DoesNotContain("AccountName", json);
        }

        [Fact]
        public void Enums_AreNumbers()
        {
            var json = WebAdminJson.Serialize(new { shade = Shade.Dark });
            Assert.Equal("{\"shade\":2}", json);
        }

        [Fact]
        public void DateTime_IsIso8601()
        {
            var json = WebAdminJson.Serialize(new { when = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) });
            Assert.Equal("{\"when\":\"2026-01-02T03:04:05Z\"}", json);
        }

        [Fact]
        public void NonAscii_StaysReadable_HtmlCharsEscaped()
        {
            var json = WebAdminJson.Serialize(new { name = "战士<script>" });
            Assert.Contains("战士", json);
            Assert.DoesNotContain("<script>", json);
        }

        [Fact]
        public void ApiEnvelope_Shape()
        {
            var ok = WebAdminJson.Serialize(new WebAdminRequest.ApiResult { Ok = true, Data = new { count = 1 } });
            Assert.Equal("{\"ok\":true,\"data\":{\"count\":1}}", ok);

            var err = WebAdminJson.Serialize(new WebAdminRequest.ApiResult { Ok = false, Message = "World is not running", Data = new { world = "stopped" } });
            Assert.Equal("{\"ok\":false,\"message\":\"World is not running\",\"data\":{\"world\":\"stopped\"}}", err);
        }

        [Fact]
        public void Deserialize_IsCaseInsensitive_AndReadsEnumNumbers()
        {
            var dto = WebAdminJson.Deserialize<SampleDto>("{\"accountname\":\"x\",\"PUBLICFIELD\":\"4\",\"shade\":1,\"row\":{\"Name\":\"n\"}}");

            Assert.Equal("x", dto.AccountName);
            Assert.Equal(4, dto.PublicField);
            Assert.Equal(Shade.Medium, dto.Shade);
            Assert.True(dto.Row.ContainsKey("Name"));
        }

        [Fact]
        public void MalformedJson_Is400()
        {
            var ex = Assert.Throws<WebAdminException>(() => WebAdminJson.Deserialize<SampleDto>("{not json"));
            Assert.Equal(400, ex.Status);

            var ex2 = Assert.Throws<WebAdminException>(() => WebAdminJson.Parse("[1,"));
            Assert.Equal(400, ex2.Status);

            Assert.Null(WebAdminJson.Deserialize<SampleDto>("   "));
            Assert.Equal(JsonValueKind.Undefined, WebAdminJson.Parse("").ValueKind);
        }

        [Fact]
        public void JsonElementHelpers_AreLenient()
        {
            var el = WebAdminJson.Parse("{\"Count\":\"12\",\"flag\":1,\"name\":\"bob\",\"big\":12345678901}");

            Assert.Equal(12, el.GetInt("count"));
            Assert.True(el.GetBool("FLAG"));
            Assert.Equal("bob", el.GetString("Name"));
            Assert.Equal(12345678901L, el.GetLong("big"));
            Assert.Equal(-1, el.GetInt("missing", -1));
            Assert.Null(el.GetString("missing"));
        }

        [Fact]
        public void Exceptions_CarryStatusAndPayload()
        {
            var notRunning = new WorldNotRunningException();
            Assert.Equal(503, notRunning.Status);
            Assert.Equal("World is not running", notRunning.Message);
            Assert.Equal("{\"world\":\"stopped\"}", WebAdminJson.Serialize(notRunning.Payload));

            Assert.Equal(504, new GameThreadTimeoutException(false).Status);
        }
    }
}

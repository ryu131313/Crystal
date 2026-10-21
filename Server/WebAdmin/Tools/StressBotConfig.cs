using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Server.WebAdmin.Tools
{
    /// <summary>
    /// One bot run as the Tools page sends it, validated, and turned into StressClient arguments. Pure (no Settings, no
    /// files), so the tests cover it. The password is never echoed back (<see cref="ToDto"/>) or audited.
    /// </summary>
    public sealed class StressBotConfig
    {
        public const string TargetScratch = "scratch";
        public const string TargetSelf = "self";
        public const string DefaultPassword = "StressPw1";
        public const int MaxBots = 5000;

        private static readonly Regex PrefixRegex = new Regex("^[A-Za-z0-9]{1,9}$");
        private static readonly Regex PasswordRegex = new Regex("^[A-Za-z0-9]{5,15}$");
        private static readonly string[] Classes = { "warrior", "wizard", "taoist", "assassin", "archer", "mixed" };

        public string Target { get; set; } = TargetScratch;
        public int Bots { get; set; } = 20;
        public double Ramp { get; set; } = 5;
        /// <summary>Run time in minutes; 0 = until stopped.</summary>
        public int DurationMinutes { get; set; }
        public string Prefix { get; set; } = "wb";
        public int FirstIndex { get; set; }
        public string Password { get; set; } = DefaultPassword;
        public string Class { get; set; } = "warrior";

        public bool Hunt { get; set; } = true;
        public bool Loot { get; set; } = true;
        public bool Town { get; set; } = true;
        public bool Quests { get; set; } = true;
        public bool Potions { get; set; } = true;
        public bool Equip { get; set; } = true;
        public bool Chat { get; set; } = true;
        public bool Party { get; set; } = true;
        public bool Director { get; set; } = true;
        public bool Run { get; set; } = true;
        public bool Reconnect { get; set; } = true;

        public int RunPercent { get; set; } = 40;
        public int SeekRange { get; set; } = 8;
        public int RoamRadius { get; set; } = 12;
        public int Workers { get; set; } = 4;
        public int ConnectBudget { get; set; } = 25;

        /// <summary>Reads and validates the request body; throws <see cref="WebAdminException"/> 400 on bad input.</summary>
        public static StressBotConfig FromJson(JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object) throw new WebAdminException(400, "Expected a JSON object body");
            var d = new StressBotConfig();
            var c = new StressBotConfig
            {
                Target = (body.GetString("target") ?? d.Target).Trim().ToLowerInvariant(),
                Bots = Int(body, "bots", d.Bots, 1, MaxBots),
                Ramp = Num(body, "ramp", d.Ramp, 0.1, 200),
                DurationMinutes = Int(body, "durationMinutes", d.DurationMinutes, 0, 24 * 60),
                Prefix = (body.GetString("prefix") ?? d.Prefix).Trim(),
                FirstIndex = Int(body, "firstIndex", d.FirstIndex, 0, 99999),
                Password = body.GetString("password") ?? d.Password,
                Class = (body.GetString("class") ?? d.Class).Trim().ToLowerInvariant(),
                Hunt = body.GetBool("hunt", d.Hunt),
                Loot = body.GetBool("loot", d.Loot),
                Town = body.GetBool("town", d.Town),
                Quests = body.GetBool("quests", d.Quests),
                Potions = body.GetBool("potions", d.Potions),
                Equip = body.GetBool("equip", d.Equip),
                Chat = body.GetBool("chat", d.Chat),
                Party = body.GetBool("party", d.Party),
                Director = body.GetBool("director", d.Director),
                Run = body.GetBool("run", d.Run),
                Reconnect = body.GetBool("reconnect", d.Reconnect),
                RunPercent = Int(body, "runPercent", d.RunPercent, 0, 100),
                SeekRange = Int(body, "seekRange", d.SeekRange, 1, 30),
                RoamRadius = Int(body, "roamRadius", d.RoamRadius, 1, 100),
                Workers = Int(body, "workers", d.Workers, 1, 32),
                ConnectBudget = Int(body, "connectBudget", d.ConnectBudget, 1, 200),
            };

            if (c.Target != TargetScratch && c.Target != TargetSelf) throw new WebAdminException(400, "target must be 'scratch' or 'self'");
            if (!PrefixRegex.IsMatch(c.Prefix)) throw new WebAdminException(400, "prefix must be 1-9 letters or digits");
            if (!PasswordRegex.IsMatch(c.Password)) throw new WebAdminException(400, "password must be 5-15 letters or digits");
            if (Array.IndexOf(Classes, c.Class) < 0) throw new WebAdminException(400, "class must be one of: " + string.Join(", ", Classes));
            if (c.FirstIndex + c.Bots > 100000) throw new WebAdminException(400, "firstIndex + bots must stay below 100000 (5-digit names)");
            return c;
        }

        private static int Int(JsonElement body, string name, int fallback, int min, int max)
        {
            if (!body.TryGetPropertyIgnoreCase(name, out var el) || el.ValueKind == JsonValueKind.Null) return fallback;
            if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v)) throw new WebAdminException(400, name + " must be a whole number");
            if (v < min || v > max) throw new WebAdminException(400, name + " must be between " + min + " and " + max);
            return v;
        }

        private static double Num(JsonElement body, string name, double fallback, double min, double max)
        {
            if (!body.TryGetPropertyIgnoreCase(name, out var el) || el.ValueKind == JsonValueKind.Null) return fallback;
            if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out var v) || double.IsNaN(v)) throw new WebAdminException(400, name + " must be a number");
            if (v < min || v > max) throw new WebAdminException(400, name + " must be between " + min + " and " + max);
            return v;
        }

        /// <summary>StressClient arguments for this run. Always stdin-controlled and with the JSON status line.</summary>
        public List<string> BuildArgs(string host, int port, int serverMaxPacket, IEnumerable<string> mapFolders)
        {
            var a = new List<string>
            {
                "--host", host,
                "--port", port.ToString(CultureInfo.InvariantCulture),
                "--bots", Bots.ToString(CultureInfo.InvariantCulture),
                "--ramp", Ramp.ToString("0.###", CultureInfo.InvariantCulture),
                "--duration", (DurationMinutes * 60).ToString(CultureInfo.InvariantCulture),
                "--prefix", Prefix,
                "--first-index", FirstIndex.ToString(CultureInfo.InvariantCulture),
                "--password", Password,
                "--class", Class,
                "--max-packet", Math.Max(5, serverMaxPacket).ToString(CultureInfo.InvariantCulture),
                "--connect-budget", ConnectBudget.ToString(CultureInfo.InvariantCulture),
                "--workers", Workers.ToString(CultureInfo.InvariantCulture),
                "--seek-range", SeekRange.ToString(CultureInfo.InvariantCulture),
                "--roam-radius", RoamRadius.ToString(CultureInfo.InvariantCulture),
                "--run-percent", RunPercent.ToString(CultureInfo.InvariantCulture),
                "--control-stdin",
                "--status-json",
            };

            foreach (var folder in mapFolders ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                a.Add("--map-folder");
                a.Add(folder);
            }

            if (!Hunt) a.Add("--no-hunt");
            if (!Loot) a.Add("--no-loot");
            if (!Town) a.Add("--no-town");
            if (!Quests) a.Add("--no-quests");
            if (!Potions) a.Add("--no-potions");
            if (!Equip) a.Add("--no-equip");
            if (!Chat) a.Add("--no-chat");
            if (!Party) a.Add("--no-party");
            if (!Director) a.Add("--no-director");
            if (!Run) a.Add("--no-run");
            if (!Reconnect) a.Add("--no-reconnect");
            return a;
        }

        /// <summary>The run as shown on the page and in the audit trail: everything but the password.</summary>
        public object ToDto() => new
        {
            target = Target,
            bots = Bots,
            ramp = Ramp,
            durationMinutes = DurationMinutes,
            prefix = Prefix,
            firstIndex = FirstIndex,
            accounts = Prefix + FirstIndex.ToString("D5", CultureInfo.InvariantCulture) + ".." + Prefix + (FirstIndex + Bots - 1).ToString("D5", CultureInfo.InvariantCulture),
            customPassword = Password != DefaultPassword,
            @class = Class,
            hunt = Hunt,
            loot = Loot,
            town = Town,
            quests = Quests,
            potions = Potions,
            equip = Equip,
            chat = Chat,
            party = Party,
            director = Director,
            run = Run,
            reconnect = Reconnect,
            runPercent = RunPercent,
            seekRange = SeekRange,
            roamRadius = RoamRadius,
            workers = Workers,
            connectBudget = ConnectBudget,
        };
    }
}

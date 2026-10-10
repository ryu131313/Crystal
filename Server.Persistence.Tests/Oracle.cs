using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using Server.MirEnvir;

namespace Server.Persistence.Tests;

/// <summary>
/// Semantic dump ("oracle") of everything a load produces from Server.MirDB + Server.MirADB:
/// a deterministic, line-per-value walk of the persisted Envir lists. Two loads of equivalent
/// data must produce identical dumps, even when the bytes differ (e.g. after a format bump).
/// Live-world types (players, maps, connections, envir) are skipped; already-visited objects
/// print as a reference so back-references and shared *Info objects don't loop.
/// </summary>
internal static class Oracle
{
    private static readonly string[] SkippedNamespaces = { "Server.MirObjects", "Server.MirNetwork", "Server.MirEnvir", "System.Threading", "System.Net" };
    private static readonly HashSet<string> SkippedFields = new(StringComparer.Ordinal)
    {
        // runtime-only, rebuilt or clock-derived; not part of the persisted data
        "Rank", "Connection", "Player", "Owner", "Caster", "RecipientInfo", "AccountInfo",
    };

    public static string Dump(Envir e)
    {
        var sb = new StringBuilder();
        var visited = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        void Root(string name, object v) => Walk(sb, visited, name, v, 0);

        Root("Versions", new { e.MapIndex, e.ItemIndex, e.MonsterIndex, e.NPCIndex, e.QuestIndex, e.GameshopIndex, e.ConquestIndex, e.RespawnIndex });
        Root("MapInfoList", e.MapInfoList);
        Root("ItemInfoList", e.ItemInfoList);
        Root("MonsterInfoList", e.MonsterInfoList);
        Root("NPCInfoList", e.NPCInfoList);
        Root("QuestInfoList", e.QuestInfoList);
        Root("DragonInfo", e.DragonInfo);
        Root("MagicInfoList", e.MagicInfoList);
        Root("GameShopList", e.GameShopList);
        Root("ConquestInfoList", e.ConquestInfoList);
        Root("RespawnTick", e.RespawnTick);
        Root("Ids", new { e.NextAccountID, e.NextCharacterID, e.NextUserItemID, e.NextHeroID, e.NextGuildID, e.NextAuctionID, e.NextMailID });
        Root("HeroList", e.HeroList);
        Root("AccountList", e.AccountList);
        Root("Auctions", e.Auctions);
        return sb.ToString();
    }

    private static void Walk(StringBuilder sb, Dictionary<object, string> visited, string path, object v, int depth)
    {
        if (depth > 40) { sb.Append(path).Append("=<depth>\n"); return; }
        if (v == null) { sb.Append(path).Append("=null\n"); return; }
        var t = v.GetType();
        if (IsScalar(t)) { sb.Append(path).Append('=').Append(Scalar(v)).Append('\n'); return; }
        if (t.Namespace != null && SkippedNamespaces.Any(n => t.Namespace.StartsWith(n, StringComparison.Ordinal))) return;
        if (typeof(Delegate).IsAssignableFrom(t)) return;

        // Framework objects (Regex caches, streams, ...) are runtime internals whose private fields change between .NET
        // versions: print the type only. Collections are still walked below.
        if (t.Namespace != null && t.Namespace.StartsWith("System", StringComparison.Ordinal) && v is not IEnumerable)
        {
            sb.Append(path).Append("=<").Append(t.Name).Append(">\n");
            return;
        }

        if (!t.IsValueType)
        {
            if (visited.TryGetValue(v, out var first)) { sb.Append(path).Append("=ref:").Append(first).Append('\n'); return; }
            visited[v] = path;
        }

        if (v is IDictionary dict)
        {
            sb.Append(path).Append(".Count=").Append(dict.Count).Append('\n');
            foreach (DictionaryEntry kv in dict) Walk(sb, visited, path + "[" + Scalar(kv.Key) + "]", kv.Value, depth + 1);
            return;
        }
        if (v is IEnumerable seq && v is not string)
        {
            int i = 0;
            foreach (var item in seq) Walk(sb, visited, path + "[" + i++ + "]", item, depth + 1);
            sb.Append(path).Append(".Count=").Append(i).Append('\n');
            return;
        }

        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var name = f.Name.StartsWith("<") ? f.Name.Substring(1, f.Name.IndexOf('>') - 1) : f.Name; // auto-property backing field
            if (SkippedFields.Contains(name)) continue;
            Walk(sb, visited, path + "." + name, f.GetValue(v), depth + 1);
        }
        // anonymous root types expose properties only
        if (t.Name.Contains("AnonymousType") && t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length == 0)
            foreach (var p in t.GetProperties()) Walk(sb, visited, path + "." + p.Name, p.GetValue(v), depth + 1);
    }

    private static bool IsScalar(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) ||
        t == typeof(TimeSpan) || t == typeof(Guid) || t == typeof(System.Drawing.Point) || t == typeof(System.Drawing.Color) ||
        t == typeof(System.Drawing.Size);

    private static string Scalar(object v) => v switch
    {
        null => "null",
        string s => "\"" + s.Replace("\n", "\\n") + "\"",
        DateTime d => d.ToBinary().ToString(CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable fo => fo.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    /// <summary>First differing line of two dumps, for assertion messages.</summary>
    public static string FirstDifference(string a, string b)
    {
        var la = a.Split('\n'); var lb = b.Split('\n');
        for (int i = 0; i < Math.Max(la.Length, lb.Length); i++)
        {
            var x = i < la.Length ? la[i] : "<end>"; var y = i < lb.Length ? lb[i] : "<end>";
            if (x != y) return $"line {i + 1}:\n  before: {x}\n  after:  {y}";
        }
        return "identical";
    }
}

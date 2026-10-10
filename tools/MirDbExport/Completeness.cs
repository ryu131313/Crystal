using System.Reflection;
using System.Text;
using Server.MirDatabase;
using Server.WebAdmin.GameData;

namespace MirDbExport
{
    /// <summary>
    /// Completeness report: for each persisted system-data model, the public instance fields / properties that no exported
    /// column (or key) carries. Uses the verifier's independent column → member mapping over the registry's column names.
    /// Needs no Server.MirDB.
    /// </summary>
    internal static class Completeness
    {
        internal static readonly Type[] Models =
        {
            typeof(MapInfo), typeof(ItemInfo), typeof(MonsterInfo), typeof(NPCInfo), typeof(QuestInfo), typeof(MagicInfo), typeof(GameShopItem),
            typeof(ConquestInfo), typeof(ConquestArcherInfo), typeof(ConquestGateInfo), typeof(ConquestWallInfo), typeof(ConquestSiegeInfo),
            typeof(ConquestFlagInfo), typeof(SafeZoneInfo), typeof(RespawnInfo), typeof(MovementInfo), typeof(MineZone), typeof(DragonInfo),
        };

        internal sealed class Gap
        {
            public Type Model;
            public string Member;
            public string MemberType;
            public string Kind;    // field / property (get-only) ...
            public string Note;    // e.g. "child table maprespawns"
        }

        public static List<Gap> Compute(out List<string> problems)
        {
            problems = new List<string>();
            var gaps = new List<Gap>();
            foreach (var model in Models)
            {
                var specs = Verifier.Specs.Where(s => s.EntityType == model).ToList();
                if (specs.Count == 0) { problems.Add(model.Name + ": no exported table"); continue; }

                var covered = new HashSet<string>(StringComparer.Ordinal);
                foreach (var spec in specs)
                {
                    if (spec.KeyMember != null) covered.Add(spec.KeyMember);
                    var table = GameDataRegistry.Get(spec.Endpoint);
                    foreach (var c in table.Columns)
                    {
                        if (c.Name == spec.ParentColumn) continue;
                        if (Verifier.Resolve(spec, c.Name, out var m) == null) problems.Add(spec.Endpoint + "." + c.Name + ": no member");
                        else if (m != null) covered.Add(m.Name);
                    }
                }

                foreach (var m in Verifier.Members(model))
                {
                    if (covered.Contains(m.Name)) continue;
                    var type = Verifier.MemberType(m);
                    gaps.Add(new Gap
                    {
                        Model = model,
                        Member = m.Name,
                        MemberType = Pretty(type),
                        Kind = m is FieldInfo f ? (f.IsInitOnly ? "readonly field" : f.IsLiteral ? "const" : "field")
                            : ((PropertyInfo)m).CanWrite ? "property" : "get-only property",
                        Note = ChildNote(type),
                    });
                }
            }

            return gaps;
        }

        private static string ChildNote(Type t)
        {
            if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(List<>)) return null;
            var element = t.GetGenericArguments()[0];
            var child = Verifier.Specs.Where(s => s.EntityType == element && s.ParentColumn != null).Select(s => s.Endpoint).ToList();
            return child.Count == 0 ? null : "rows in child table " + string.Join(" / ", child);
        }

        internal static string Pretty(Type t)
        {
            if (!t.IsGenericType) return t.Name;
            var name = t.Name.Substring(0, t.Name.IndexOf('`'));
            return name + "<" + string.Join(", ", t.GetGenericArguments().Select(Pretty)) + ">";
        }

        public static string Render(List<Gap> gaps)
        {
            var sb = new StringBuilder();
            sb.AppendLine("| Model | Member | Type | Kind | Note |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var g in gaps)
                sb.Append("| ").Append(g.Model.Name).Append(" | ").Append(g.Member).Append(" | ").Append(g.MemberType.Replace("|", "\\|"))
                    .Append(" | ").Append(g.Kind).Append(" | ").Append(g.Note ?? string.Empty).AppendLine(" |");
            return sb.ToString();
        }
    }
}

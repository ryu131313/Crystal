using System.Text;
using System.Text.Json;
using Server.MirEnvir.Storage;

namespace Server.MirEnvir.Quests
{
    /// <summary>
    /// JSON export / import of the quest definition tables at the store-row level (UserData-and-Quests-Plan.md, Part B phase 1 smoke check:
    /// SQLite → export JSON → import gives identical rows). Cells are written as the store holds them (INTEGER, REAL, TEXT, NULL), with the
    /// column names, so an import is checked against this build's columns. The per-quest author files of the phase 3 editor build on this.
    /// </summary>
    internal static class QuestDefinitionJson
    {
        /// <summary>The tables a quest definition export carries (quests whole: its V2 columns live in that table).</summary>
        public static readonly string[] Tables =
        {
            "quests", "queststeps", "questobjectives", "questbranches", "questendpoints", "questrewards", "questvars", "conditions", "texts",
            "storylines", "questgroups", "questgroupmembers", "questpools", "questpoolmembers",
        };

        private static readonly string[] Counters =
        {
            "QuestIndex", "QuestStepIndex", "QuestObjectiveIndex", "QuestConditionIndex", "QuestGroupIndex", "QuestTextIndex", "QuestVarIndex",
            "StorylineIndex", "QuestPoolIndex",
        };

        public static string Export(WorldRows rows)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("format", 1);
                w.WriteStartObject("counters");
                foreach (var name in Counters) w.WriteNumber(name, rows.Counters[name]);
                w.WriteEndObject();
                w.WriteStartObject("tables");
                foreach (var name in Tables)
                {
                    var spec = SqliteWorldCodec.Specs.First(s => s.Name == name);
                    w.WriteStartObject(name);
                    w.WriteStartArray("columns");
                    foreach (var c in SqliteWorldCodec.ColumnNames(spec)) w.WriteStringValue(c);
                    w.WriteEndArray();
                    w.WriteStartArray("rows");
                    foreach (var cells in rows.Tables[name].Rows)
                    {
                        w.WriteStartArray();
                        foreach (var cell in cells)
                        {
                            switch (cell)
                            {
                                case null: w.WriteNullValue(); break;
                                case long l: w.WriteNumberValue(l); break;
                                case double d: w.WriteStartObject(); w.WriteNumber("real", d); w.WriteEndObject(); break; // keeps 5.0 a REAL
                                case string s: w.WriteStringValue(s); break;
                                default: throw new InvalidDataException("[Quests] cell of type " + cell.GetType());
                            }
                        }

                        w.WriteEndArray();
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndObject();
                w.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>Replaces the quest definition tables and counters of <paramref name="rows"/> with the export's (columns checked).</summary>
        public static void Import(string json, WorldRows rows)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("format").GetInt32() != 1) throw new InvalidDataException("[Quests] unknown export format");
            foreach (var name in Counters) rows.Counters[name] = root.GetProperty("counters").GetProperty(name).GetInt64();

            var tables = root.GetProperty("tables");
            foreach (var name in Tables)
            {
                var spec = SqliteWorldCodec.Specs.First(s => s.Name == name);
                var expected = SqliteWorldCodec.ColumnNames(spec);
                var t = tables.GetProperty(name);
                var columns = t.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
                if (!columns.SequenceEqual(expected, StringComparer.Ordinal))
                    throw new InvalidDataException("[Quests] " + name + ": the export's columns differ from this build's (" + string.Join(", ", columns) + ")");

                var table = new WorldRows.Table { Name = name };
                foreach (var row in t.GetProperty("rows").EnumerateArray())
                {
                    var cells = new object[expected.Count];
                    var i = 0;
                    foreach (var cell in row.EnumerateArray())
                    {
                        if (i >= cells.Length) throw new InvalidDataException("[Quests] " + name + ": a row has too many cells");
                        cells[i++] = cell.ValueKind switch
                        {
                            JsonValueKind.Null => null,
                            JsonValueKind.Number => cell.GetInt64(),
                            JsonValueKind.String => cell.GetString(),
                            JsonValueKind.Object => cell.GetProperty("real").GetDouble(),
                            _ => throw new InvalidDataException("[Quests] " + name + ": unexpected cell " + cell.ValueKind),
                        };
                    }

                    if (i != cells.Length) throw new InvalidDataException("[Quests] " + name + ": a row has too few cells");
                    table.Rows.Add(cells);
                }

                rows.Tables[name] = table;
            }
        }
    }
}

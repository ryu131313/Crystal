using System.Globalization;
using System.Text.Json;

namespace Server.WebAdmin.GameData
{
    /// <summary>One field change (wire values) for previews, audit and live apply.</summary>
    internal sealed class GameDataChange
    {
        public string Field { get; set; }
        public object Before { get; set; }
        public object After { get; set; }
    }

    /// <summary>One applied (or, for a dry run, would-be-applied) row.</summary>
    internal sealed class GameDataRowResult
    {
        /// <summary>The Index the client sent (≤ 0 for a new row).</summary>
        public int Index { get; set; }

        /// <summary>The row's Index after the write (allocated for inserts; 0 for a dry-run insert).</summary>
        public int NewIndex { get; set; }

        /// <summary>"insert" or "update".</summary>
        public string Kind { get; set; }

        public string Name { get; set; }

        public List<GameDataChange> Changes { get; set; } = new List<GameDataChange>();

        /// <summary>Typed new values of the changed columns (for live apply), never serialized.</summary>
        internal List<KeyValuePair<GameDataColumn, object>> Typed = new List<KeyValuePair<GameDataColumn, object>>();
    }

    /// <summary>Outcome of a PUT (see contract §3.4).</summary>
    internal sealed class GameDataWriteResult
    {
        public bool DryRun;
        public int Applied;
        public readonly List<GameDataRowResult> Rows = new List<GameDataRowResult>();
        public readonly List<object> Conflicts = new List<object>();
        public readonly List<object> Validation = new List<object>();
        public readonly List<string> IgnoredFields = new List<string>();
        public readonly List<object> Renames = new List<object>();
    }

    /// <summary>Outcome of a delete.</summary>
    internal sealed class GameDataDeleteResult
    {
        public bool DryRun;
        public int Deleted;
        public readonly List<object> Rejects = new List<object>();

        /// <summary>Snapshots of the deleted (or deletable, for a dry run) rows, for audit.</summary>
        public readonly List<KeyValuePair<int, Dictionary<string, object>>> Removed = new List<KeyValuePair<int, Dictionary<string, object>>>();
    }

    /// <summary>
    /// The grid engine: query rows, apply edited rows (rev check, typed conversion, validation, FK checks, inserts), delete rows with FK
    /// guards. Pure over a <see cref="GameDataContext"/>; the caller holds <see cref="WebAdminGame.EditLock"/>.
    /// </summary>
    internal static class GameDataEngine
    {
        public const int MaxRowsPerWrite = 5000;

        // ================================================================== query

        /// <summary>
        /// Rows of a table, optionally filtered by column equality (<c>?MapIndex=3</c>; any column, numeric or text, case-insensitive),
        /// a free-text <paramref name="q"/>, sorted by <paramref name="sort"/>/<paramref name="dir"/> and paged (pageSize ≤ 0 = all).
        /// </summary>
        public static List<Dictionary<string, object>> Query(GameDataContext ctx, GameDataTable table, IEnumerable<KeyValuePair<string, string>> filters,
            string q, string sort, string dir, int page, int pageSize, out int total)
        {
            var wanted = new List<KeyValuePair<string, string>>();
            if (filters != null)
            {
                foreach (var f in filters)
                {
                    if (string.IsNullOrEmpty(f.Key) || f.Value == null) continue;
                    if (string.Equals(f.Key, "Index", StringComparison.OrdinalIgnoreCase))
                    {
                        wanted.Add(new KeyValuePair<string, string>("Index", f.Value));
                        continue;
                    }

                    var col = table.Column(f.Key);
                    if (col != null) wanted.Add(new KeyValuePair<string, string>(col.Name, f.Value));
                }
            }

            IEnumerable<Dictionary<string, object>> seq = table.Rows(ctx).Select(table.ToWire).ToList();

            if (wanted.Count > 0)
                seq = seq.Where(r => wanted.All(w => r.TryGetValue(w.Key, out var v) && CellEquals(v, w.Value)));

            if (!string.IsNullOrWhiteSpace(q))
            {
                var needle = q.Trim();
                seq = seq.Where(r => r.Any(kv => !kv.Key.StartsWith("__", StringComparison.Ordinal) &&
                                                 GameDataCodec.Canonical(kv.Value).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            var list = seq.ToList();
            total = list.Count;

            if (!string.IsNullOrWhiteSpace(sort))
            {
                var key = list.Count > 0 ? list[0].Keys.FirstOrDefault(k => string.Equals(k, sort, StringComparison.OrdinalIgnoreCase)) : null;
                if (key != null)
                {
                    list = list.OrderBy(r => r[key], ValueComparer.Instance).ToList();
                    if (string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase)) list.Reverse();
                }
            }

            if (pageSize > 0)
            {
                if (page < 1) page = 1;
                list = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            }

            return list;
        }

        private static bool CellEquals(object cell, string wanted)
        {
            var text = GameDataCodec.Canonical(cell);
            if (long.TryParse(wanted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) &&
                long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var have))
                return n == have;

            return string.Equals(text, wanted, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class ValueComparer : IComparer<object>
        {
            public static readonly ValueComparer Instance = new ValueComparer();

            public int Compare(object a, object b)
            {
                var sa = GameDataCodec.Canonical(a);
                var sb = GameDataCodec.Canonical(b);
                if (double.TryParse(sa, NumberStyles.Float, CultureInfo.InvariantCulture, out var da) &&
                    double.TryParse(sb, NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
                    return da.CompareTo(db);
                return string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
            }
        }

        // ================================================================== write

        /// <summary>
        /// Applies grid rows. Per row: conflict check (<c>__rev</c> unless <paramref name="noRev"/>), typed conversion of every known
        /// column, FK existence, table rules on a scratch copy; a row with any problem is skipped and reported, the others apply.
        /// <paramref name="dryRun"/> validates and reports the same way but changes nothing.
        /// </summary>
        public static GameDataWriteResult Apply(GameDataContext ctx, GameDataTable table, IReadOnlyList<JsonElement> rows, bool dryRun, bool noRev)
        {
            var result = new GameDataWriteResult { DryRun = dryRun };
            if (rows == null) return result;

            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var position = 0;
            var byIndex = IndexRows(ctx, table);

            foreach (var json in rows)
            {
                position++;
                if (json.ValueKind != JsonValueKind.Object)
                {
                    result.Validation.Add(new { index = 0, field = "row", message = "row " + position.ToString(CultureInfo.InvariantCulture) + " is not an object" });
                    continue;
                }

                GameDataCodec.TryGetIndex(json, "Index", out var index);
                var existing = index > 0 && byIndex.TryGetValue(index, out var found) ? found : null;
                var isInsert = existing == null;

                // A positive Index that no longer exists but carries the __rev it was loaded with was deleted (or, for position-keyed
                // child rows, re-keyed) since the grid loaded it: a conflict, never a silent re-insert. Rows without a __rev (imports) and
                // noRev requests keep treating an unknown positive Index as a new row.
                if (isInsert && index > 0 && !noRev && json.TryGetPropertyIgnoreCase(GameDataCodec.RevKey, out var staleRev) &&
                    staleRev.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(staleRev.GetString()))
                {
                    result.Conflicts.Add(new { index, message = "deleted since you loaded it (reload; add it again as a new row if you still want it)" });
                    continue;
                }

                if (isInsert && !table.Insertable)
                {
                    result.Validation.Add(new { index, field = "Index", message = index > 0 ? table.Label + " #" + index + " does not exist" : table.Label + " rows cannot be added" });
                    continue;
                }

                if (!isInsert && !noRev && json.TryGetPropertyIgnoreCase(GameDataCodec.RevKey, out var revEl) && revEl.ValueKind == JsonValueKind.String)
                {
                    var submitted = revEl.GetString();
                    if (!string.IsNullOrEmpty(submitted) && !string.Equals(submitted, table.Rev(existing), StringComparison.Ordinal))
                    {
                        result.Conflicts.Add(new { index, message = "changed since you loaded it (reload and re-apply your edit)" });
                        continue;
                    }
                }

                // Parent (child tables): fixed for existing rows; required and must exist for inserts.
                var parentIndex = 0;
                var rowErrors = new List<GameDataFieldError>();
                if (table.IsChild)
                {
                    if (isInsert)
                    {
                        if (!GameDataCodec.TryGetIndex(json, table.ParentField, out parentIndex) || parentIndex <= 0)
                            rowErrors.Add(new GameDataFieldError(table.ParentField, "is required for a new row"));
                        else if (!ctx.Exists(table.ParentEndpoint, parentIndex))
                            rowErrors.Add(new GameDataFieldError(table.ParentField, table.ParentEndpoint + " #" + parentIndex + " does not exist"));
                    }
                    else if (GameDataCodec.TryGetIndex(json, table.ParentField, out var sentParent) && sentParent != existing.ParentIndex)
                    {
                        rowErrors.Add(new GameDataFieldError(table.ParentField, "cannot be changed (delete the row and add it to the other " +
                                                                                (table.ParentEndpoint == "maps" ? "map" : "conquest") + ")"));
                    }
                }

                var scratch = isInsert ? table.NewDetached(ctx) : GameDataClone.CloneRow(existing);
                var changed = new HashSet<string>(StringComparer.Ordinal);
                var rowResult = new GameDataRowResult { Index = index, Kind = isInsert ? "insert" : "update" };

                foreach (var prop in json.EnumerateObject())
                {
                    var key = prop.Name;
                    if (string.Equals(key, "Index", StringComparison.OrdinalIgnoreCase) || key.StartsWith("__", StringComparison.Ordinal)) continue;

                    var column = table.Column(key);
                    if (column == null)
                    {
                        ignored.Add(key);
                        continue;
                    }

                    if (column.IsParentField) continue;

                    if (!GameDataCodec.TryConvert(prop.Value, column, out var value, out var error))
                    {
                        rowErrors.Add(new GameDataFieldError(column.Name, error));
                        continue;
                    }

                    var before = column.Get(scratch);
                    if (GameDataCodec.ValuesEqual(column.ValueType, before, value)) continue;

                    if (column.ReadOnly || column.Set == null)
                    {
                        ignored.Add(column.Name);
                        continue;
                    }

                    var fkError = CheckForeignKey(ctx, column, value);
                    if (fkError != null)
                    {
                        rowErrors.Add(new GameDataFieldError(column.Name, fkError));
                        continue;
                    }

                    column.Set(scratch, value);
                    changed.Add(column.Name);
                    rowResult.Changes.Add(new GameDataChange { Field = column.Name, Before = GameDataCodec.ToWire(column, before), After = GameDataCodec.ToWire(column, value) });
                    rowResult.Typed.Add(new KeyValuePair<GameDataColumn, object>(column, value));
                }

                if (isInsert)
                {
                    // Every column counts as written for an insert: required FKs and table rules judge the whole new record.
                    foreach (var column in table.Columns)
                    {
                        if (column.IsParentField) continue;
                        changed.Add(column.Name);
                        if (column.Required && column.ForeignKey != null && !column.IsList && rowErrors.All(e => e.Field != column.Name))
                        {
                            var v = Convert.ToInt32(column.Get(scratch), CultureInfo.InvariantCulture);
                            if (v == 0) rowErrors.Add(new GameDataFieldError(column.Name, "is required"));
                        }
                    }
                }

                if (rowErrors.Count == 0 && table.Validate != null)
                {
                    scratch.Index = isInsert ? 0 : existing.Index;
                    rowErrors.AddRange(table.Validate(ctx, scratch, changed) ?? Enumerable.Empty<GameDataFieldError>());
                }

                if (rowErrors.Count > 0)
                {
                    foreach (var e in rowErrors)
                        result.Validation.Add(new { index, field = e.Field ?? "row", message = e.Message });
                    continue;
                }

                if (!isInsert && rowResult.Changes.Count == 0) continue; // nothing to do

                // Rename tracking (items / monsters / anything with a Name column the files refer to).
                if (!isInsert && table.NameColumn != null && (table.Endpoint == "items" || table.Endpoint == "monsters"))
                {
                    var nameChange = rowResult.Changes.FirstOrDefault(c => c.Field == table.NameColumn);
                    if (nameChange != null && !string.IsNullOrWhiteSpace(nameChange.Before as string) &&
                        !string.Equals((string)nameChange.Before, (string)nameChange.After, StringComparison.OrdinalIgnoreCase))
                        result.Renames.Add(new { endpoint = table.Endpoint, index, old = (string)nameChange.Before, @new = (string)nameChange.After });
                }

                rowResult.Name = table.DisplayName(scratch);

                if (!dryRun)
                {
                    if (isInsert)
                    {
                        table.Attach(ctx, scratch, parentIndex);
                        table.AfterApply?.Invoke(ctx, scratch);
                        rowResult.NewIndex = scratch.Index;
                        ctx.Invalidate();
                    }
                    else
                    {
                        foreach (var t in rowResult.Typed)
                            t.Key.Set(existing, t.Value);
                        table.AfterApply?.Invoke(ctx, existing);
                        rowResult.NewIndex = existing.Index;
                    }
                }
                else
                {
                    rowResult.NewIndex = isInsert ? 0 : existing.Index;
                }

                result.Rows.Add(rowResult);
                result.Applied++;
            }

            result.IgnoredFields.AddRange(ignored.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>Index → row (first wins if a key were ever duplicated).</summary>
        private static Dictionary<int, GameDataRow> IndexRows(GameDataContext ctx, GameDataTable table)
        {
            var map = new Dictionary<int, GameDataRow>();
            foreach (var row in table.Rows(ctx))
                if (!map.ContainsKey(row.Index)) map[row.Index] = row;
            return map;
        }

        /// <summary>Null when the FK value is acceptable, else the problem.</summary>
        internal static string CheckForeignKey(GameDataContext ctx, GameDataColumn column, object value)
        {
            if (string.IsNullOrEmpty(column.ForeignKey)) return null;

            if (value is List<int> list)
            {
                var missing = list.Where(v => !ctx.Exists(column.ForeignKey, v)).Distinct().ToList();
                if (missing.Count == 0) return null;
                return "references " + TargetLabel(column.ForeignKey) + " " + string.Join(", ", missing.Select(m => "#" + m.ToString(CultureInfo.InvariantCulture))) + " which do(es) not exist";
            }

            int n;
            try
            {
                n = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return "is not a valid reference";
            }

            if (n == 0) return column.Required ? "is required" : null;
            if (!ctx.Exists(column.ForeignKey, n))
                return "references " + TargetLabel(column.ForeignKey) + " #" + n.ToString(CultureInfo.InvariantCulture) + " which does not exist";
            return null;
        }

        private static string TargetLabel(string fk)
        {
            switch (fk)
            {
                case "items": return "item";
                case "monsters": return "monster";
                case "maps": return "map";
                case "npcs": return "NPC";
                case "quests": return "quest";
                case "conquests": return "conquest";
                case "magics": return "spell";
                case "minesets": return "mine set";
                default: return fk;
            }
        }

        // ================================================================== delete

        /// <summary>
        /// Deletes rows (or, for a dry run, reports what would be deleted). Rows are resolved first and removed by reference, so the
        /// position-based child keys of one request stay valid while earlier rows of the same request are removed.
        /// </summary>
        public static GameDataDeleteResult Delete(GameDataContext ctx, GameDataTable table, IReadOnlyList<int> indexes, bool dryRun)
        {
            var result = new GameDataDeleteResult { DryRun = dryRun };
            if (indexes == null) return result;

            var targets = new List<GameDataRow>();
            var seen = new HashSet<int>();
            var byIndex = IndexRows(ctx, table);
            foreach (var index in indexes)
            {
                if (!seen.Add(index)) continue;
                var row = index > 0 && byIndex.TryGetValue(index, out var found) ? found : null;
                if (row == null)
                {
                    result.Rejects.Add(new { index, code = "not-found", message = table.Label + " #" + index.ToString(CultureInfo.InvariantCulture) + " does not exist" });
                    continue;
                }

                // Quest V2 (phase 3): a row a quest definition references cannot be deleted; the refusal lists where it is used.
                var guard = Quests.QuestWhereUsed.Join(table.DeleteGuard?.Invoke(ctx, row), Quests.QuestWhereUsed.DeleteGuard(ctx, table.Endpoint, row.Index));
                if (guard != null)
                {
                    result.Rejects.Add(new { index, code = "in-use", message = guard });
                    continue;
                }

                targets.Add(row);
            }

            foreach (var row in targets)
            {
                result.Removed.Add(new KeyValuePair<int, Dictionary<string, object>>(row.Index, table.ToWire(row)));
                if (!dryRun) table.Remove(ctx, row);
                result.Deleted++;
            }

            if (!dryRun && result.Deleted > 0) ctx.Invalidate();
            return result;
        }
    }
}

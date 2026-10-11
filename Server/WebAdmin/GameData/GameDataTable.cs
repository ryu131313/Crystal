namespace Server.WebAdmin.GameData
{
    /// <summary>A validation problem found by a table rule (field may be null for row-level problems).</summary>
    internal sealed class GameDataFieldError
    {
        public string Field;
        public string Message;

        public GameDataFieldError(string field, string message)
        {
            Field = field;
            Message = message;
        }
    }

    /// <summary>
    /// One grid endpoint (items, monsters, maprespawns, ...): how to enumerate its rows, its columns, how to create / remove a row and
    /// the table-specific validation and delete guards. Definitions live in <see cref="GameDataRegistry"/>.
    /// </summary>
    internal sealed class GameDataTable
    {
        public string Endpoint;
        public string Label;

        /// <summary>Game Data sub-section this table belongs to (maps children → "maps", conquest children → "conquests").</summary>
        public string Group;

        public Type EntityType;

        public bool Insertable;
        public bool Deletable;

        /// <summary>Child tables: the parent endpoint ("maps" / "conquests") and the parent column ("MapIndex" / "ConquestIndex").</summary>
        public string ParentEndpoint;
        public string ParentField;

        /// <summary>The row's display-name column (rename tracking, audit labels, lookups).</summary>
        public string NameColumn;

        /// <summary>Human text describing how "Index" is formed (published in the schema).</summary>
        public string IndexNote;

        /// <summary>
        /// Index = parent * Stride + position in the parent's list: the key moves to another record when an earlier sibling is deleted,
        /// so an Index recorded in the audit log does not identify the same record later (no history revert).
        /// </summary>
        public bool PositionKeyed;

        public List<GameDataColumn> Columns = new List<GameDataColumn>();

        public Func<GameDataContext, IEnumerable<GameDataRow>> Rows;

        /// <summary>A fresh, detached entity with the defaults the WinForms editors use (no index allocated, not in any list).</summary>
        public Func<GameDataContext, GameDataRow> NewDetached;

        /// <summary>Allocates the key and adds a detached row to its list (parentIndex = the row's parent field for child tables).</summary>
        public Action<GameDataContext, GameDataRow, int> Attach;

        public Action<GameDataContext, GameDataRow> Remove;

        /// <summary>Null when the row may be deleted, else why not (FK guard).</summary>
        public Func<GameDataContext, GameDataRow, string> DeleteGuard;

        /// <summary>
        /// Table rules run on a scratch copy after the row's values were applied. The set holds the columns this write changed (every
        /// column for inserts): rules only judge what changed, so pre-existing odd data never blocks an unrelated edit.
        /// </summary>
        public Func<GameDataContext, GameDataRow, ISet<string>, IEnumerable<GameDataFieldError>> Validate;

        /// <summary>Hook after values were applied to an attached row (e.g. rebind GameShopItem.Info).</summary>
        public Action<GameDataContext, GameDataRow> AfterApply;

        /// <summary>
        /// Every list that holds this table's rows (the top-level list, or each parent's child list), so LiveDb can snapshot and restore
        /// them when a batch with inserts / deletes must be rolled back. Null for tables without inserts / deletes (magic, dragon).
        /// </summary>
        public Func<GameDataContext, IEnumerable<System.Collections.IList>> Lists;

        private Dictionary<string, GameDataColumn> _byName;

        public bool IsChild => !string.IsNullOrEmpty(ParentEndpoint);

        public GameDataColumn Column(string name)
        {
            if (name == null) return null;
            _byName ??= Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            return _byName.TryGetValue(name, out var c) ? c : null;
        }

        public GameDataRow Find(GameDataContext ctx, int index)
        {
            foreach (var row in Rows(ctx))
                if (row.Index == index) return row;

            return null;
        }

        /// <summary>The row as the grid sees it: Index, every column's wire value and __rev.</summary>
        public Dictionary<string, object> ToWire(GameDataRow row)
        {
            var dict = new Dictionary<string, object>(StringComparer.Ordinal) { ["Index"] = row.Index };
            foreach (var c in Columns)
                dict[c.Name] = GameDataCodec.ToWire(c, c.Get(row));

            dict[GameDataCodec.RevKey] = GameDataCodec.Rev(dict);
            return dict;
        }

        public string Rev(GameDataRow row) => (string)ToWire(row)[GameDataCodec.RevKey];

        public string DisplayName(GameDataRow row)
        {
            var c = Column(NameColumn);
            var name = c == null ? null : GameDataCodec.ToWire(c, c.Get(row)) as string;
            return string.IsNullOrWhiteSpace(name) ? "#" + row.Index : name;
        }
    }

    /// <summary>Scratch copies of Crystal records, so validation / dry runs never touch the real object.</summary>
    internal static class GameDataClone
    {
        private static readonly System.Reflection.MethodInfo MemberwiseCloneMethod =
            typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>
        /// Shallow copy plus fresh copies of the members column setters mutate in place (Stats, List&lt;int&gt;, primitive arrays), so
        /// writing the copy can never leak into the original. Object references (MapInfo.Respawns, GameShopItem.Info ...) stay shared and
        /// are never written through a copy.
        /// </summary>
        public static object Clone(object entity)
        {
            if (entity == null) return null;
            var copy = MemberwiseCloneMethod.Invoke(entity, null);

            foreach (var f in entity.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (f.IsInitOnly || f.IsLiteral) continue;
                var value = f.GetValue(entity);
                if (value == null) continue;

                if (value is Stats stats) f.SetValue(copy, new Stats(stats));
                else if (value is List<int> list) f.SetValue(copy, new List<int>(list));
                else if (value is Array arr && f.FieldType.GetElementType() != null && f.FieldType.GetElementType().IsPrimitive) f.SetValue(copy, arr.Clone());
            }

            return copy;
        }

        public static GameDataRow CloneRow(GameDataRow row)
        {
            return new GameDataRow
            {
                Index = row.Index,
                Entity = Clone(row.Entity),
                Parent = row.Parent,
                ParentIndex = row.ParentIndex,
                Position = row.Position,
            };
        }
    }
}

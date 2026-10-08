using Server.MirDatabase;
using Server.MirEnvir;

namespace Server.WebAdmin.GameData
{
    /// <summary>
    /// Crystal never reloads MagicInfo: Envir.LoadDB only ADDS spells that are missing (MagicExists), so after Edit.SaveDB() + Reboot
    /// Envir.Main keeps its old MagicInfo objects, and its periodic SaveDB() would later write those old values back into Server.MirDB.
    /// (Players' UserMagic.Info also hold references to those objects.) So when the web admin saves the database, the saved spell values
    /// are copied IN PLACE onto Main's MagicInfo objects (matched by Spell) on the game thread, or inline while the world is fully stopped.
    /// If that is impossible (the world is starting or stopping) the snapshot stays pending and is applied by the WorldStarted hook, which
    /// runs on the game thread after Main.LoadDB() and before Main's first periodic save.
    /// </summary>
    internal static class GameDataMagicSync
    {
        private static readonly object Gate = new object();
        private static List<MagicInfo> _pending;
        private static int _hooked;

        /// <summary>The magic grid's writable columns (Spell is the key and read-only).</summary>
        private static IEnumerable<GameDataColumn> Columns()
        {
            var table = GameDataRegistry.Get("magic");
            if (table == null) return Enumerable.Empty<GameDataColumn>();
            return table.Columns.Where(c => !c.ReadOnly && c.Set != null && !c.IsParentField);
        }

        /// <summary>Detached copies of <paramref name="source"/> (call under <see cref="WebAdminGame.EditLock"/> for the Edit envir).</summary>
        public static List<MagicInfo> Snapshot(IEnumerable<MagicInfo> source)
        {
            return source.Where(m => m != null).Select(m => (MagicInfo)GameDataClone.Clone(m)).ToList();
        }

        /// <summary>
        /// Copies every writable magic column of <paramref name="from"/> onto the entry of <paramref name="to"/> with the same Spell
        /// (in place; no entries are added or removed). Returns the number of spells whose values changed.
        /// </summary>
        public static int CopyInto(IReadOnlyList<MagicInfo> from, IList<MagicInfo> to)
        {
            if (from == null || to == null) return 0;
            var columns = Columns().ToList();
            var changed = 0;

            foreach (var src in from)
            {
                if (src == null) continue;
                MagicInfo dst = null;
                for (var i = 0; i < to.Count; i++)
                {
                    if (to[i] != null && to[i].Spell == src.Spell)
                    {
                        dst = to[i];
                        break;
                    }
                }

                if (dst == null || ReferenceEquals(dst, src)) continue;

                var srcRow = new GameDataRow { Index = (int)src.Spell, Entity = src };
                var dstRow = new GameDataRow { Index = (int)dst.Spell, Entity = dst };
                var any = false;
                foreach (var c in columns)
                {
                    var value = c.Get(srcRow);
                    if (GameDataCodec.ValuesEqual(c.ValueType, c.Get(dstRow), value)) continue;
                    c.Set(dstRow, value);
                    any = true;
                }

                if (any) changed++;
            }

            return changed;
        }

        /// <summary>Remembers the saved values until they reached Envir.Main (see the class summary).</summary>
        public static void SetPending(List<MagicInfo> snapshot)
        {
            EnsureHook();
            lock (Gate) _pending = snapshot;
        }

        /// <summary>Clears the pending snapshot if it is still <paramref name="snapshot"/> (a newer save keeps its own).</summary>
        public static void ClearPending(List<MagicInfo> snapshot)
        {
            lock (Gate)
            {
                if (ReferenceEquals(_pending, snapshot)) _pending = null;
            }
        }

        public static bool HasPending
        {
            get
            {
                lock (Gate) return _pending != null;
            }
        }

        private static void EnsureHook()
        {
            if (Interlocked.Exchange(ref _hooked, 1) != 0) return;
            WebAdminHooks.WorldStarted += OnWorldStarted;
        }

        /// <summary>Game thread, right after Main.StartEnvir()/LoadDB().</summary>
        private static void OnWorldStarted()
        {
            List<MagicInfo> pending;
            lock (Gate) pending = _pending;
            if (pending == null) return;

            try
            {
                var changed = CopyInto(pending, Envir.Main.MagicInfoList);
                ClearPending(pending);
                if (changed > 0) WebAdminLog.Info("Applied " + changed + " saved spell change(s) to the started world");
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "Applying saved magic changes to the started world failed");
            }
        }
    }
}

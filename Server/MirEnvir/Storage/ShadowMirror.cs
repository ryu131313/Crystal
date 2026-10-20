using System.Diagnostics;
using System.Globalization;

namespace Server.MirEnvir.Storage
{
    /// <summary>
    /// <c>[Database] Shadow=True</c> with Backend=Binary (roadmap P7 phase 7.2): after every Envir.Edit save of Server.MirDB, the same data
    /// is written to <see cref="Path"/> by its own writer thread, with the SHA-256 of the binary file in meta.source_sha256. The check is
    /// offline: <c>MirDbExport roundtrip</c> loads the mirror, writes binary and compares it with the file that has that hash (decision D1).
    /// Only Edit saves are mirrored, so the game thread never encodes (with LiveDb, Main skips its design saves once Edit has saved).
    /// A mirror failure is logged and never fails the binary save.
    /// </summary>
    internal static class ShadowMirror
    {
        public static readonly string Path = System.IO.Path.Combine(".", "SystemData.shadow.db");

        private static MessageQueue MessageQueue => MessageQueue.Instance;
        private static readonly Lazy<SqliteWriter> Writer = new Lazy<SqliteWriter>(() => new SqliteWriter(Path, "SystemData shadow writer"));

        public static long Failures => Interlocked.Read(ref _failures);
        private static long _failures;

        /// <summary>Waits until every queued mirror write has finished (tests and tools).</summary>
        internal static void Flush() => Writer.Value.Run(_ => 0);

        /// <summary>Called on the saving thread right after Edit's binary save (never the game thread).</summary>
        public static void AfterBinarySave(Envir e, string binarySha256)
        {
            if (e != Envir.Edit || binarySha256 == null) return;

            WorldRows rows;
            var sw = Stopwatch.StartNew();
            try { rows = SqliteWorldCodec.Encode(e, SqliteWorldStore.CurrentRespawnCounter(e)); }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failures);
                MessageQueue.Enqueue("[Diag] Shadow encode failed: " + ex.Message);
                return;
            }

            var encodeMs = sw.ElapsedMilliseconds;
            Writer.Value.Post(w =>
            {
                var write = Stopwatch.StartNew();
                var meta = new Dictionary<string, object> { ["source"] = "shadow", ["source_sha256"] = binarySha256 };
                try
                {
                    if (!File.Exists(w.Path))
                    {
                        SqliteWorldStore.Create(w.Path, rows, meta);
                    }
                    else
                    {
                        SqliteWorldFile.WriteAll(w.Connection, rows, includeRuntime: true, meta);
                    }
                }
                catch
                {
                    Interlocked.Increment(ref _failures);
                    w.CloseConnection();
                    throw;
                }

                var count = rows.Tables.Values.Sum(t => t.Rows.Count);
                MessageQueue.EnqueueDebugging("[Diag] Shadow encodeMs=" + encodeMs.ToString(CultureInfo.InvariantCulture) + " writeMs=" +
                                              write.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " rows=" + count.ToString(CultureInfo.InvariantCulture) +
                                              " failures=" + Failures.ToString(CultureInfo.InvariantCulture));
            }, "shadow mirror");
        }
    }
}

using System.Security.Cryptography;
using Server.MirDatabase;
using Server.MirEnvir;

namespace MirDbExport
{
    /// <summary>
    /// Loads a Server.MirDB into <see cref="Envir.Edit"/> with the server's own load code, offline and read-only:
    /// the source file is copied into a fresh temp working folder first (Envir resolves every path relative to the
    /// current directory) and loaded there. The source folder is never written.
    /// </summary>
    internal static class WorldLoader
    {
        public const string DbFileName = "Server.MirDB";

        private static readonly object Gate = new object();

        /// <summary>Facts about the source file, recorded in <c>meta</c> and re-checked by the verifier.</summary>
        internal sealed class SourceFile
        {
            public string Path;
            public long Size;
            public string Sha256;
        }

        /// <summary>
        /// Refuses a missing or 0-byte Server.MirDB (Envir.LoadDB would otherwise create an empty DB: WoOOLcraft lesson),
        /// copies it to a temp folder, loads Envir.Edit there and runs <paramref name="body"/> while the Envir statics hold
        /// that load. Throws on any failure.
        /// </summary>
        public static T WithLoadedEdit<T>(string sourceFolder, Func<SourceFile, T> body)
        {
            var src = System.IO.Path.GetFullPath(System.IO.Path.Combine(sourceFolder, DbFileName));
            if (!File.Exists(src)) throw new ToolException("No " + DbFileName + " in " + sourceFolder + " (refusing: an exporter never creates an empty DB).");
            var info = new FileInfo(src);
            if (info.Length == 0) throw new ToolException(src + " is 0 bytes (refusing: an exporter never treats an empty file as an empty DB).");

            lock (Gate)
            {
                var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mirdbexport", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(work);
                var old = Environment.CurrentDirectory;
                try
                {
                    var copy = System.IO.Path.Combine(work, DbFileName);
                    File.Copy(src, copy);
                    var source = new SourceFile { Path = src, Size = new FileInfo(copy).Length, Sha256 = Sha256Of(copy) };

                    Directory.SetCurrentDirectory(work);
                    if (Envir.Main.BuffInfoList.Count == 0)
                        foreach (var b in BuffInfo.Load()) Envir.Main.BuffInfoList.Add(b);

                    if (!Envir.Edit.LoadDB())
                        throw new ToolException("Envir.Edit.LoadDB() refused " + src + " (version " + Envir.LoadVersion + "; supported " + Envir.MinVersion + ".." + Envir.Version + "). " + DrainMessages());

                    // LoadDB reads through the copy; it must not have rewritten it.
                    if (Sha256Of(copy) != source.Sha256) throw new ToolException("Envir.Edit.LoadDB() modified the working copy of " + DbFileName + "; aborting.");

                    return body(source);
                }
                finally
                {
                    Directory.SetCurrentDirectory(old);
                    try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }

        public static string Sha256Of(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private static string DrainMessages()
        {
            var parts = new List<string>();
            while (Server.MessageQueue.Instance.MessageLog.TryDequeue(out var m)) parts.Add(m.Trim());
            return parts.Count == 0 ? string.Empty : "Server messages: " + string.Join(" | ", parts);
        }
    }

    /// <summary>A failure the CLI reports as a one-line error (exit code 2).</summary>
    internal sealed class ToolException : Exception
    {
        public ToolException(string message) : base(message) { }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;

namespace Server.MirEnvir.Storage
{
    /// <summary>
    /// "Open in editor" for the WinForms editors (roadmap P7 phase 7.4). A design file in SystemData.db has no file to open, so a working
    /// copy is written to %TEMP%\mir2-design-edit and opened with the system editor; every save there is read back into the store
    /// (logged), until the server closes. Any other path opens directly, as before.
    /// </summary>
    internal static class DesignEditing
    {
        private static readonly ConcurrentDictionary<string, FileSystemWatcher> Watchers = new ConcurrentDictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);

        public static void OpenInEditor(string path)
        {
            if (!DesignFiles.TryMap(path, out var format, out var name))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }

            var copy = Path.Combine(Path.GetTempPath(), "mir2-design-edit", format.Kind, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(copy));
            File.WriteAllBytes(copy, DesignFiles.ReadAllBytes(path));
            Watch(copy, Path.GetFullPath(path));
            Process.Start(new ProcessStartInfo(copy) { UseShellExecute = true });
        }

        private static void Watch(string copy, string storePath)
        {
            if (Watchers.ContainsKey(copy)) return;
            var watcher = new FileSystemWatcher(Path.GetDirectoryName(copy), Path.GetFileName(copy))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };

            byte[] last = File.ReadAllBytes(copy);
            void Changed(object sender, FileSystemEventArgs e)
            {
                // Editors save in several steps: wait a moment, then take the file once it reads cleanly and differs.
                Thread.Sleep(300);
                try
                {
                    var bytes = File.ReadAllBytes(copy);
                    if (bytes.AsSpan().SequenceEqual(last)) return;
                    last = bytes;
                    var unresolved = DesignFiles.WriteAllBytes(storePath, bytes);
                    MessageQueue.Instance.Enqueue("[Store] Saved " + DesignStore.Key(storePath) + " from the external editor" +
                                                  (unresolved > 0 ? " (" + unresolved + " line(s) name something that doesn't exist)" : string.Empty));
                }
                catch (IOException) { } // still being written; the next event takes it
                catch (Exception ex)
                {
                    MessageQueue.Instance.Enqueue("[Store] Saving " + DesignStore.Key(storePath) + " from the external editor failed: " + ex.Message);
                }
            }

            watcher.Changed += Changed;
            watcher.Created += Changed;
            watcher.Renamed += (s, e) => Changed(s, e);
            watcher.EnableRaisingEvents = true;
            Watchers[copy] = watcher;
        }
    }
}

using System.Text;
using System.Text.Json;

namespace Client.Patching
{
    /// <summary>One file of the relay's /api/patch list (in-game patcher plan §4.1).</summary>
    internal sealed class PatchFile
    {
        public string Path;
        public long Size;
        public string Sha256;
        public int Tier;
        public string Version;
    }

    /// <summary>
    /// The last /api/patch response. <see cref="Raw"/> is kept as it came and saved as Patch\Manifest.bin (atomic), so an
    /// offline start can still size the library arrays.
    /// </summary>
    internal sealed class PatchManifest
    {
        public string Revision = "";
        public readonly Dictionary<string, PatchFile> Files = new Dictionary<string, PatchFile>(StringComparer.OrdinalIgnoreCase);
        public byte[] Raw;

        public static PatchManifest Parse(byte[] json)
        {
            var manifest = new PatchManifest { Raw = json };
            using var doc = JsonDocument.Parse(json);
            manifest.Revision = doc.RootElement.GetProperty("revision").GetString() ?? "";
            foreach (var e in doc.RootElement.GetProperty("files").EnumerateArray())
            {
                string path = PatchPaths.Normalize(e.GetProperty("p").GetString());
                if (!PatchPaths.IsSafe(path) || PatchPaths.IsDenied(path)) continue;
                manifest.Files[path] = new PatchFile
                {
                    Path = path,
                    Size = e.GetProperty("s").GetInt64(),
                    Sha256 = e.GetProperty("h").GetString(),
                    Tier = e.GetProperty("t").GetInt32(),
                    Version = e.GetProperty("v").GetString(),
                };
            }
            return manifest;
        }

        public static PatchManifest TryLoad(string file)
        {
            try
            {
                return File.Exists(file) ? Parse(File.ReadAllBytes(file)) : null;
            }
            catch (Exception ex)
            {
                if (Settings.LogErrors) CMain.SaveError("[Patch] Manifest.bin unreadable: " + ex.Message);
                return null;
            }
        }

        public void Save(string file) => PatchFiles.WriteAtomic(file, Raw);
    }

    /// <summary>
    /// Patch\State.bin: path → size + sha256 of every file the patcher installed or verified. A file is current when its
    /// recorded hash matches the manifest and its size on disk matches. Only these files are ever deleted as "removed".
    /// </summary>
    internal sealed class PatchState
    {
        private const uint Magic = 0x53504D43; // "CMPS"
        public readonly Dictionary<string, (long Size, string Sha256)> Files = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);

        public static PatchState TryLoad(string file)
        {
            if (!File.Exists(file)) return null;
            try
            {
                var state = new PatchState();
                using var reader = new BinaryReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8);
                if (reader.ReadUInt32() != Magic || reader.ReadByte() != 1) return null;
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    string path = reader.ReadString();
                    long size = reader.ReadInt64();
                    state.Files[path] = (size, Convert.ToHexString(reader.ReadBytes(32)).ToLowerInvariant());
                }
                return state;
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or FormatException)
            {
                return null;
            }
        }

        public void Save(string file)
        {
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write((byte)1);
                writer.Write(Files.Count);
                foreach (var pair in Files)
                {
                    writer.Write(pair.Key);
                    writer.Write(pair.Value.Size);
                    writer.Write(Convert.FromHexString(pair.Value.Sha256));
                }
            }
            PatchFiles.WriteAtomic(file, ms.ToArray());
        }
    }

    /// <summary>Path rules shared by the service and the UI-thread pickup: '/'-separated, relative, case-insensitive.</summary>
    internal static class PatchPaths
    {
        public static readonly string Root = Settings.P_Client;
        public static readonly string PatchDir = Path.Combine(Settings.P_Client, "Patch");
        public static readonly string TempDir = Path.Combine(PatchDir, "Temp");
        public static readonly string TrashDir = Path.Combine(PatchDir, "Trash");

        /// <summary>".\Data\Monster\001.Lib" → "Data/Monster/001.Lib".</summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            path = path.Replace('\\', '/');
            if (path.StartsWith(Root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) path = path.Substring(Root.Length);
            while (path.StartsWith("./", StringComparison.Ordinal)) path = path.Substring(2);
            return path.TrimStart('/');
        }

        /// <summary>
        /// Canonical relative paths only, so the prefix checks in <see cref="IsDenied"/> cannot be dodged: no empty, "." or
        /// ".." segment ("Patch//x", "Data/./UserData"), no segment ending in '.' or ' ' (Windows strips them: "Patch./x"),
        /// no ':' or backslash, not rooted.
        /// </summary>
        public static bool IsSafe(string rel)
        {
            if (rel.Length == 0 || rel.Contains(':') || rel.Contains('\\') || Path.IsPathRooted(rel)) return false;
            foreach (var segment in rel.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == "..") return false;
                char last = segment[segment.Length - 1];
                if (last == '.' || last == ' ') return false;
            }
            return true;
        }

        /// <summary>The relay's hard deny list: per-user files and the patcher's own folder are never installed or deleted.</summary>
        public static bool IsDenied(string rel)
        {
            string name = rel.Substring(rel.LastIndexOf('/') + 1);
            return name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Error.txt", StringComparison.OrdinalIgnoreCase)
                || name.Equals("RebuildProfile.txt", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Old__", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("Data/UserData/", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("Screenshots/", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("DirectX/", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("Patch/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Program files, by location and extension whatever their tier (exe folder or runtimes\, .exe/.dll/.json): staged
        /// and swapped by PatchApply at exit / Restart; never installed over the running build.
        /// </summary>
        public static bool IsProgramFile(PatchFile file)
        {
            if (file.Path.Contains('/') && !file.Path.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase)) return false;
            string ext = Path.GetExtension(file.Path);
            return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".json", StringComparison.OrdinalIgnoreCase);
        }

        public static string Local(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

        public static string Url(string host, string rel, string version)
        {
            var sb = new StringBuilder(host).Append("patch/");
            string[] parts = rel.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append('/');
                sb.Append(Uri.EscapeDataString(parts[i]));
            }
            return sb.Append("?v=").Append(Uri.EscapeDataString(version)).ToString();
        }

    }

    /// <summary>File helpers with no Settings dependency (PatchPaths' static fields read Settings; PatchApply must not).</summary>
    internal static class PatchFiles
    {
        public static void WriteAtomic(string file, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temp = file + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, file, overwrite: true);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Client.Utils
{
    public class Download
    {
        public FileInformation Info;
        public long CurrentBytes;
        public bool Completed;
    }

    public class FileInformation
    {
        public string FileName; //Relative.
        public int Length, Compressed;
        public DateTime Creation;
        /// <summary>SHA-256 of the uncompressed file from the PList trailing section (roadmap C2), or null.</summary>
        public byte[] Sha256;

        public FileInformation()
        {

        }
        public FileInformation(BinaryReader reader)
        {
            FileName = reader.ReadString();
            Length = reader.ReadInt32();
            Compressed = reader.ReadInt32();

            Creation = DateTime.FromBinary(reader.ReadInt64());
        }
        public void Save(BinaryWriter writer)
        {
            writer.Write(FileName);
            writer.Write(Length);
            writer.Write(Compressed);
            writer.Write(Creation.ToBinary());
        }
    }

    /// <summary>
    /// Optional trailing section of PList.gz (roadmap C2), written after the counted FileInformation records. Old launchers
    /// read the count and the records and ignore anything after them, so the format stays backward compatible.
    /// Layout: int32 Magic, int32 Version, int32 count + per record (byte length 0|32, SHA-256 of the uncompressed file),
    /// int32 count + string per removed file (files a previous list shipped that players should delete).
    /// Keep this class identical in Client/Utils/FileHelper.cs and AutoPatcherAdminV2/AMain.cs.
    /// </summary>
    public static class PatchListExtension
    {
        public const int Magic = 0x58544C50; // "PLTX"
        public const int Version = 1;

        public static void Write(BinaryWriter writer, IReadOnlyList<byte[]> hashes, IReadOnlyCollection<string> removed)
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(hashes.Count);
            foreach (var h in hashes)
            {
                if (h == null || h.Length != 32) { writer.Write((byte)0); continue; }
                writer.Write((byte)32);
                writer.Write(h);
            }
            writer.Write(removed.Count);
            foreach (var r in removed) writer.Write(r);
        }

        /// <summary>Reads the section if present (stream positioned after the records). Returns false when absent.</summary>
        public static bool TryRead(BinaryReader reader, int recordCount, out byte[][] hashes, out List<string> removed)
        {
            hashes = null; removed = new List<string>();
            var s = reader.BaseStream;
            if (s.Length - s.Position < 12) return false;
            if (reader.ReadInt32() != Magic) return false;
            if (reader.ReadInt32() < 1) return false;
            var n = reader.ReadInt32();
            if (n != recordCount || n < 0) return false;
            hashes = new byte[n][];
            for (var i = 0; i < n; i++)
            {
                var len = reader.ReadByte();
                if (len == 32) hashes[i] = reader.ReadBytes(32);
                else if (len != 0) return false;
            }
            var r = reader.ReadInt32();
            for (var i = 0; i < r; i++) removed.Add(reader.ReadString());
            return true;
        }

        /// <summary>True for a safe relative file name: not rooted, no "..", no drive or stream separators.</summary>
        public static bool IsSafeRelativePath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            var f = fileName.Replace('/', '\\');
            if (Path.IsPathRooted(f) || f.Contains(':')) return false;
            foreach (var part in f.Split('\\'))
                if (part == "..") return false;
            return true;
        }
    }
}

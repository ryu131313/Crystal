using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Server.MirEnvir.Storage
{
    /// <summary>
    /// The D1 rollback (UserData-and-Quests-Plan.md Part A, Backups: Rollback): writes Server.MirADB, Guilds\{position}.mgd,
    /// Envir\Goods\{npc}.msd and Conquests\{index}.mcd back from a UserData.db, byte for byte the files an older build reads.
    /// Offline only: run it with the server stopped (MirDbExport export-userdata). The database is opened read-only.
    /// </summary>
    internal static class UserDataExport
    {
        public sealed class Result
        {
            public int Accounts, Guilds, Goods, Conquests;
            public long AccountBytes;
        }

        /// <summary>
        /// Writes the legacy files under <paramref name="outFolder"/>. Refuses (InvalidOperationException) when any target exists unless
        /// <paramref name="force"/>; with it the old files are replaced and stale guild files beyond the guild count are removed.
        /// </summary>
        public static Result WriteLegacy(string dbPath, string outFolder, bool force)
        {
            if (!File.Exists(dbPath)) throw new FileNotFoundException("UserData.db not found", dbPath);

            UserDataRows rows;
            var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            using (var conn = new SqliteConnection(cs))
            {
                conn.Open();
                SqliteWorldFile.CheckIntegrity(conn);
                UserDataFile.CheckSchema(conn);
                rows = UserDataFile.ReadAll(conn);
            }

            var accountsPath = Path.Combine(outFolder, "Server.MirADB");
            var guildDir = Path.Combine(outFolder, "Guilds");
            var goodsDir = Path.Combine(outFolder, "Envir", "Goods");
            var conquestDir = Path.Combine(outFolder, "Conquests");

            if (!force)
            {
                if (File.Exists(accountsPath)) throw new InvalidOperationException(accountsPath + " exists (pass --force to replace it)");
                foreach (var dir in new[] { guildDir, goodsDir, conquestDir })
                    if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
                        throw new InvalidOperationException(dir + " is not empty (pass --force to replace its files)");
            }

            Directory.CreateDirectory(outFolder);
            var image = UserDataFile.LegacyAccountsImage(rows);
            WriteFile(accountsPath, image);

            Directory.CreateDirectory(guildDir);
            // Legacy guild files are named by list position: the store's guild_index order is the list order.
            for (var i = 0; i < rows.Guilds.Count; i++) WriteFile(Path.Combine(guildDir, i.ToString(CultureInfo.InvariantCulture) + ".mgd"), rows.Guilds[i].Data);
            if (force)
                foreach (var file in Directory.GetFiles(guildDir, "*.mgd"))
                    if (int.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None, CultureInfo.InvariantCulture, out var position) && position >= rows.Guilds.Count)
                        File.Delete(file);

            Directory.CreateDirectory(goodsDir);
            for (var i = 0; i < rows.Goods.Count; i++) WriteFile(Path.Combine(goodsDir, rows.Goods[i].Key.ToString(CultureInfo.InvariantCulture) + ".msd"), rows.Goods[i].Data);

            Directory.CreateDirectory(conquestDir);
            for (var i = 0; i < rows.Conquests.Count; i++) WriteFile(Path.Combine(conquestDir, rows.Conquests[i].Key.ToString(CultureInfo.InvariantCulture) + ".mcd"), rows.Conquests[i].Data);

            return new Result
            {
                Accounts = rows.Accounts.Count, Guilds = rows.Guilds.Count, Goods = rows.Goods.Count, Conquests = rows.Conquests.Count,
                AccountBytes = image.Length,
            };
        }

        /// <summary>Temp file + flush + replace, so a crash never leaves a half-written file under the final name.</summary>
        private static void WriteFile(string path, byte[] data)
        {
            var tmp = path + ".tmp";
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                f.Write(data, 0, data.Length);
                f.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
    }
}

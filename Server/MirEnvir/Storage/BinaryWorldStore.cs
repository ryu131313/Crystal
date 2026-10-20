using Server.MirDatabase;

namespace Server.MirEnvir.Storage
{
    /// <summary>
    /// Server.MirDB, the hand-written binary system-data file versioned by Envir.Version (roadmap P7 phase 7.1). The code is
    /// Envir.SaveDB / Envir.LoadDB moved here unchanged, minus the post-load bindings (now <see cref="WorldBinder"/>).
    /// </summary>
    internal sealed class BinaryWorldStore : IWorldStore
    {
        private static MessageQueue MessageQueue => MessageQueue.Instance;

        public void SaveDesign(Envir e, string backupFileName) => Save(e, backupFileName, hash: false);

        /// <summary>Saves and returns the SHA-256 of the file written (shadow mode, roadmap P7 phase 7.2).</summary>
        internal string SaveDesignAndHash(Envir e, string backupFileName) => Save(e, backupFileName, hash: true);

        private string Save(Envir e, string backupFileName, bool hash)
        {
            // Crash-safe save (roadmap P2): write a complete temp file, then swap it in with File.Replace, which also
            // moves the previous file into Back Up\Database. Server.MirDB is never truncated in place, so a crash or an
            // exception mid-write leaves the last good file (and a stale Server.MirDBn) instead of a half-written DB.
            var tempPath = Envir.DatabasePath + "n";

            using (var stream = File.Create(tempPath))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Envir.Version);
                writer.Write(Envir.CustomVersion);
                writer.Write(e.MapIndex);
                writer.Write(e.ItemIndex);
                writer.Write(e.MonsterIndex);
                writer.Write(e.NPCIndex);
                writer.Write(e.QuestIndex);
                writer.Write(e.GameshopIndex);
                writer.Write(e.ConquestIndex);
                writer.Write(e.RespawnIndex);

                writer.Write(e.MapInfoList.Count);
                for (var i = 0; i < e.MapInfoList.Count; i++)
                    e.MapInfoList[i].Save(writer);

                writer.Write(e.ItemInfoList.Count);
                for (var i = 0; i < e.ItemInfoList.Count; i++)
                    e.ItemInfoList[i].Save(writer);

                writer.Write(e.MonsterInfoList.Count);
                for (var i = 0; i < e.MonsterInfoList.Count; i++)
                    e.MonsterInfoList[i].Save(writer);

                writer.Write(e.NPCInfoList.Count);
                for (var i = 0; i < e.NPCInfoList.Count; i++)
                    e.NPCInfoList[i].Save(writer);

                writer.Write(e.QuestInfoList.Count);
                for (var i = 0; i < e.QuestInfoList.Count; i++)
                    e.QuestInfoList[i].Save(writer);

                e.DragonInfo.Save(writer);
                writer.Write(e.MagicInfoList.Count);
                for (var i = 0; i < e.MagicInfoList.Count; i++)
                    e.MagicInfoList[i].Save(writer);

                writer.Write(e.GameShopList.Count);
                for (var i = 0; i < e.GameShopList.Count; i++)
                    e.GameShopList[i].Save(writer);

                writer.Write(e.ConquestInfoList.Count);
                for (var i = 0; i < e.ConquestInfoList.Count; i++)
                    e.ConquestInfoList[i].Save(writer);

                // F4 (LiveDb): Edit is the only writer of system data once it has saved (SystemDataStale), but the respawn tick
                // counter only advances in Main: write Main's published counter instead of Edit's frozen copy.
                var published = Envir.PublishedRespawnTick;
                if (e == Envir.Edit && published >= 0)
                    e.RespawnTick.Save(writer, (ulong)published);
                else
                    e.RespawnTick.Save(writer);

                writer.Write(e.GTMapList.Count);
                for (var i = 0; i < e.GTMapList.Count; i++)
                    e.GTMapList[i].Save(writer);
            }

            var sha = hash ? SqliteWorldStore.Sha256(tempPath) : null;

            if (File.Exists(Envir.DatabasePath))
            {
                if (!Directory.Exists(Envir.DatabaseBackUpPath)) Directory.CreateDirectory(Envir.DatabaseBackUpPath);

                var fileName = !string.IsNullOrWhiteSpace(backupFileName)
                    ? Path.GetFileName(backupFileName)
                    : $"Database {e.Now.Year:0000}-{e.Now.Month:00}-{e.Now.Day:00} {e.Now.Hour:00}-{e.Now.Minute:00}-{e.Now.Second:00}.bak";

                var backupFile = Path.Combine(Envir.DatabaseBackUpPath, fileName);

                if (File.Exists(backupFile)) File.Delete(backupFile);
                File.Replace(tempPath, Envir.DatabasePath, backupFile);
            }
            else
            {
                File.Move(tempPath, Envir.DatabasePath);
            }

            return sha;
        }

        public bool LoadDesign(Envir e)
        {
            if (!File.Exists(Envir.DatabasePath))
            {
                if (Envir.HasPriorDataEvidence(Envir.DatabasePath, Envir.DatabaseBackUpPath))
                {
                    // Fail closed (roadmap P2): a missing Server.MirDB next to backups/temp files means data was lost, not
                    // that this is a new server. Creating an empty DB here would let the next save bury the real one.
                    MessageQueue.Enqueue("[Load] Server.MirDB is missing but Back Up\\Database or Server.MirDBn exists. Refusing to create an empty database: restore the newest backup (Knowledgebase/Runbook.md).");
                    return false;
                }
                e.SaveDB();
            }

            using (var stream = File.OpenRead(Envir.DatabasePath))
            using (var reader = new BinaryReader(stream))
            {
                Envir.LoadVersion = reader.ReadInt32();
                Envir.LoadCustomVersion = reader.ReadInt32();

                if (Envir.LoadVersion < Envir.MinVersion)
                {
                    MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.CannotLoadDatabaseMinSupported), Envir.LoadVersion, Envir.MinVersion));
                    return false;
                }
                else if (Envir.LoadVersion > Envir.Version)
                {
                    MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.CannotLoadDatabaseMaxSupported), Envir.LoadVersion, Envir.Version));
                    return false;
                }

                e.MapIndex = reader.ReadInt32();
                e.ItemIndex = reader.ReadInt32();
                e.MonsterIndex = reader.ReadInt32();

                e.NPCIndex = reader.ReadInt32();
                e.QuestIndex = reader.ReadInt32();

                if (Envir.LoadVersion >= 63)
                {
                    e.GameshopIndex = reader.ReadInt32();
                }

                if (Envir.LoadVersion >= 66)
                {
                    e.ConquestIndex = reader.ReadInt32();
                }

                if (Envir.LoadVersion >= 68)
                    e.RespawnIndex = reader.ReadInt32();


                var count = reader.ReadInt32();
                e.MapInfoList.Clear();
                e.MapDetailsCache.Clear();
                for (var i = 0; i < count; i++)
                    e.MapInfoList.Add(new MapInfo(reader));

                count = reader.ReadInt32();
                e.ItemInfoList.Clear();
                for (var i = 0; i < count; i++)
                    e.ItemInfoList.Add(new ItemInfo(reader, Envir.LoadVersion, Envir.LoadCustomVersion));

                count = reader.ReadInt32();
                e.MonsterInfoList.Clear();
                for (var i = 0; i < count; i++)
                    e.MonsterInfoList.Add(new MonsterInfo(reader));

                count = reader.ReadInt32();
                e.NPCInfoList.Clear();
                for (var i = 0; i < count; i++)
                    e.NPCInfoList.Add(new NPCInfo(reader));

                count = reader.ReadInt32();
                e.QuestInfoList.Clear();
                for (var i = 0; i < count; i++)
                    e.QuestInfoList.Add(new QuestInfo(reader));

                e.DragonInfo = new DragonInfo(reader);
                count = reader.ReadInt32();
                for (var i = 0; i < count; i++)
                {
                    var m = new MagicInfo(reader, Envir.LoadVersion, Envir.LoadCustomVersion);
                    if (!e.MagicExists(m.Spell))
                        e.MagicInfoList.Add(m);
                }

                if (Envir.LoadVersion >= 63)
                {
                    count = reader.ReadInt32();
                    e.GameShopList.Clear();
                    for (var i = 0; i < count; i++)
                        e.GameShopList.Add(new GameShopItem(reader, Envir.LoadVersion, Envir.LoadCustomVersion));
                }

                if (Envir.LoadVersion >= 66)
                {
                    e.ConquestInfoList.Clear();
                    count = reader.ReadInt32();
                    for (var i = 0; i < count; i++)
                    {
                        e.ConquestInfoList.Add(new ConquestInfo(reader));
                    }
                }

                if (Envir.LoadVersion > 67)
                    e.RespawnTick = new RespawnTimer(reader);
            }

            return true;
        }
    }
}

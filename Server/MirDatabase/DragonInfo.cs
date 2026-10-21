using Server.MirEnvir.Storage;
using System.Drawing;
﻿using Server.MirEnvir;

namespace Server.MirDatabase
{
    public class DragonInfo
    {
        protected static Envir Envir
        {
            get { return Envir.Main; }
        }

        protected static MessageQueue MessageQueue
        {
            get { return MessageQueue.Instance; }
        }

        public bool Enabled;
        public string MapFileName, MonsterName, BodyName;
        public Point Location, DropAreaTop, DropAreaBottom;
        public List<DropInfo>[] Drops = new List<DropInfo>[Globals.MaxDragonLevel];
        public long[] Exps = new long[Globals.MaxDragonLevel - 1];

        public byte Level;
        public long Experience;

        public DragonInfo()
        {
            //Default values
            Enabled = false;
            MapFileName = "D2083";
            MonsterName = "Evil Mir";
            BodyName = "00";
            Location = new Point(82, 44);
            DropAreaTop = new Point(75, 45);
            DropAreaBottom = new Point(86, 57);

            Level = 1;

            for (int i = 0; i < Exps.Length; i++)
            {
                Exps[i] = (i + 1) * 10000;
            }
            for (int i = 0; i < Drops.Length; i++)
            {
                Drops[i] = new List<DropInfo>();
            }
        }
        public DragonInfo(BinaryReader reader)
        {
            Enabled = reader.ReadBoolean();
            MapFileName = reader.ReadString();
            MonsterName = reader.ReadString();
            BodyName = reader.ReadString();
            Location = new Point(reader.ReadInt32(), reader.ReadInt32());
            DropAreaTop = new Point(reader.ReadInt32(), reader.ReadInt32());
            DropAreaBottom = new Point(reader.ReadInt32(), reader.ReadInt32());

            Level = 1;

            for (int i = 0; i < Exps.Length; i++)
            {
                Exps[i] = reader.ReadInt64();
            }
            for (int i = 0; i < Drops.Length; i++)
            {
                Drops[i] = new List<DropInfo>();
            }
        }
        public void Save(BinaryWriter writer)
        {
            writer.Write(Enabled);
            writer.Write(MapFileName);
            writer.Write(MonsterName);
            writer.Write(BodyName);
            writer.Write(Location.X);
            writer.Write(Location.Y);
            writer.Write(DropAreaTop.X);
            writer.Write(DropAreaTop.Y);
            writer.Write(DropAreaBottom.X);
            writer.Write(DropAreaBottom.Y);
            for (int i = 0; i < Exps.Length; i++)
            {
                writer.Write(Exps[i]);
            }
        }

        public void LoadDrops()
        {
            for (int i = 0; i < Globals.MaxDragonLevel; i++) Drops[i].Clear();
            string path = Path.Combine(Settings.DropPath, "DragonItem.txt");
            if (DropTables.TryMap(path, out var listName))
            {
                if (!DropTables.LoadDragon(Drops, listName, LogBadLine))
                    DropTables.Write(listName, Server.MirDatabase.DropInfo.DefaultFileLines);
                return;
            }

            if (!DesignFiles.Exists(path))
            {
                DesignFiles.WriteAllLines(path, Server.MirDatabase.DropInfo.DefaultFileLines);


                return;
            }

            LoadFromLines(Drops, DesignFiles.ReadAllLines(path), LogBadLine);
        }

        internal static void LogBadLine(string line) =>
            MessageQueue.Enqueue(GameLanguage.ServerTextMap.GetLocalization((ServerTextKeys.CouldNotLoadDropDragonItem), line));

        /// <summary>The text loader (files, and <c>MirDbExport check-drops</c>' reference).</summary>
        internal static void LoadFromLines(List<DropInfo>[] drops, string[] lines, Action<string> badLine)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith(";") || string.IsNullOrWhiteSpace(lines[i])) continue;

                DropInfo drop = DropInfo.FromLine(lines[i]);
                if (drop == null)
                {
                    badLine(lines[i]);
                    continue;
                }

                if (drop.level <= 0 || drop.level > Globals.MaxDragonLevel) return;
                drops[drop.level - 1].Add(drop);
            }

            for (int i = 0; i < Globals.MaxDragonLevel; i++)
            {
                drops[i].Sort((drop1, drop2) =>
                {
                    if (drop1.Gold > 0 && drop2.Gold == 0)
                        return 1;
                    if (drop1.Gold == 0 && drop2.Gold > 0)
                        return -1;

                    return drop1.Item.Type.CompareTo(drop2.Item.Type);
                });
            }
        }

        public class DropInfo
        {
            public int Chance;
            public ItemInfo Item;
            public uint Gold;
            public byte level;

            /// <summary>
            /// The syntax of a DragonItem.txt line (shared with the drop tables): <c>1/N Item level</c> or <c>1/N Gold amount level</c>.
            /// A line of fewer than two tokens, or a first token shorter than two characters, throws, as it always did.
            /// </summary>
            internal static bool TryParseLine(string s, out int chance, out uint gold, out string itemName, out byte level)
            {
                string[] parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                gold = 0;
                itemName = null;
                level = 0;
                if (!int.TryParse(parts[0].Substring(2), out chance)) return false;
                if (string.Compare(parts[1], "Gold", StringComparison.OrdinalIgnoreCase) == 0)
                {
                    if (parts.Length < 4) return false;
                    if (!uint.TryParse(parts[2], out gold) || gold == 0) return false;
                    if (!byte.TryParse(parts[3], out level)) return false;
                }
                else
                {
                    if (parts.Length < 3) return false;
                    itemName = parts[1];
                    if (!byte.TryParse(parts[2], out level)) return false;
                }
                return true;
            }

            public static DropInfo FromLine(string s)
            {
                if (!TryParseLine(s, out var chance, out var gold, out var itemName, out var level)) return null;

                DropInfo info = new DropInfo { Chance = chance, Gold = gold, level = level };
                if (itemName != null)
                {
                    info.Item = Envir.GetItemInfo(itemName);
                    if (info.Item == null) return null;
                }
                return info;
            }
        }
    }
}

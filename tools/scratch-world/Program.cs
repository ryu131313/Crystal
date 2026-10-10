using System.Drawing;
using Server.MirDatabase;
using Server.MirEnvir;

namespace ScratchWorld;

/// <summary>
/// Generates a minimal world a scratch Server.exe will start on:
///  - Maps\&lt;name&gt;.map in the oldest Crystal map format ("type 0" in Map.FindType / LoadMapCellsv0):
///    a 52-byte header (Int16 width, Int16 height, rest zero) then 12 bytes per cell, column-major (x outer, y inner).
///    All-zero cells are walkable floor with no doors, no lights and no fishing attributes.
///  - Server.MirDB holding that map (FileName = map name), a StartPoint safe zone in its centre, two monster
///    types with respawns around it, and a few StartItem items.
/// Envir resolves Server.MirDB relative to the current directory, so the save runs with cwd = the output folder.
/// SCRATCH ONLY: never point --out at a live server folder (the tool refuses a folder that already has a Server.MirDB
/// unless --force, and always refuses a path under the repo's Build\ folder).
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string outDir = null;
        string mapName = "scratch0";
        int size = 200;
        int maps = 1;
        bool force = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": outDir = args[++i]; break;
                case "--size": size = int.Parse(args[++i]); break;
                case "--map": mapName = args[++i]; break;
                case "--maps": maps = int.Parse(args[++i]); break;
                case "--force": force = true; break;
                default:
                    Console.Error.WriteLine($"Unknown argument '{args[i]}'.");
                    return Usage();
            }
        }

        if (string.IsNullOrWhiteSpace(outDir)) return Usage();
        if (size < 50 || size > 1000) { Console.Error.WriteLine("--size must be 50..1000"); return 2; }
        if (maps < 1 || maps > 20) { Console.Error.WriteLine("--maps must be 1..20"); return 2; }

        outDir = Path.GetFullPath(outDir);
        var repoBuild = FindRepoBuildFolder();
        if (repoBuild != null && (outDir + "\\").StartsWith(repoBuild, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Refusing to write into '{outDir}': it is inside the repo's live Build\\ folder.");
            return 2;
        }

        Directory.CreateDirectory(outDir);
        if (File.Exists(Path.Combine(outDir, "Server.MirDB")) && !force)
        {
            Console.Error.WriteLine($"'{outDir}' already has a Server.MirDB. Pass --force to overwrite it (scratch folders only).");
            return 3;
        }

        Directory.CreateDirectory(Path.Combine(outDir, "Maps"));
        // Map 0 keeps the --map name; extra maps are <name minus trailing digits><n> (scratch1, scratch2, ...). Each has a
        // start point, and new characters pick a random start point, so bots spread across maps.
        var mapNames = new string[maps];
        var stem = mapName.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        for (int m = 0; m < maps; m++)
        {
            mapNames[m] = m == 0 ? mapName : stem + m;
            var mapPath = Path.Combine(outDir, "Maps", mapNames[m] + ".map");
            File.WriteAllBytes(mapPath, BuildMapV0(size, size));
            Console.WriteLine($"Wrote {mapPath} ({size}x{size}, walkable)");
        }

        var old = Environment.CurrentDirectory;
        Directory.SetCurrentDirectory(outDir);
        try
        {
            if (force)
            {
                // Stale data from an earlier run would otherwise be backed up and kept next to the new DB.
                foreach (var f in new[] { "Server.MirDB", "Server.MirDBn", "Server.MirADB", "Server.MirADBn" })
                    if (File.Exists(f)) File.Delete(f);
            }

            var e = Envir.Main;
            Populate(e, mapNames, size);
            e.SaveDB();
            Console.WriteLine($"Wrote {Path.Combine(outDir, "Server.MirDB")} (version {Envir.Version}): " +
                              $"{e.MapInfoList.Count} map(s), {e.MonsterInfoList.Count} monsters, {e.ItemInfoList.Count} items, " +
                              $"{e.MapInfoList.Sum(m => m.Respawns.Count)} respawns");
        }
        finally
        {
            Directory.SetCurrentDirectory(old);
        }

        return 0;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: ScratchWorld --out <scratch folder> [--size 200] [--map scratch0] [--maps 1] [--force]");
        return 2;
    }

    private static string FindRepoBuildFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Legend of Mir.sln"))) dir = dir.Parent;
        return dir == null ? null : Path.Combine(dir.FullName, "Build") + "\\";
    }

    /// <summary>Map "type 0": FindType returns 0 when bytes 0..3 / 4 match none of the other signatures, which holds
    /// for a non-zero low width byte, a zero byte 4 and an all-zero rest of the header.</summary>
    internal static byte[] BuildMapV0(int width, int height)
    {
        if ((width & 0xFF) == 0 || (width & 0xFF) == 0x0D || (width & 0xFF) == 0x0F || (width & 0xFF) == 0x10 || (width & 0xFF) == 0x15)
            throw new ArgumentException("width's low byte would make Map.FindType pick another format; choose another size");

        const int header = 52, cell = 12;
        var bytes = new byte[header + width * height * cell];
        BitConverter.GetBytes((short)width).CopyTo(bytes, 0);
        BitConverter.GetBytes((short)height).CopyTo(bytes, 2);
        // Cells: Int16 back (bit 15 = high wall), Int16 middle (bit 15 = low wall), Int16 front (bit 15 = no floor),
        // Int16 unused, byte door index, 2 bytes unused, byte light. All zero = walkable floor.
        return bytes;
    }

    private static void Populate(Envir e, string[] mapNames, int size)
    {
        var centre = new Point(size / 2, size / 2);

        e.MapIndex = mapNames.Length; e.ItemIndex = 5; e.MonsterIndex = 2; e.NPCIndex = 0; e.QuestIndex = 0; e.GameshopIndex = 0; e.RespawnIndex = 2 * mapNames.Length;

        int spread = Math.Max(10, size / 2 - 10);
        for (int m = 0; m < mapNames.Length; m++)
        {
            var map = new MapInfo { Index = m + 1, FileName = mapNames[m], Title = m == 0 ? "Scratch Plains" : "Scratch Plains " + m, Light = LightSetting.Day };
            map.SafeZones.Add(new SafeZoneInfo { Info = map, Location = centre, Size = 5, StartPoint = true });
            // Passive "hen" (AI 1 = Deer: wanders, never attacks) and an aggressive melee mob (AI 0 = base MonsterObject).
            map.Respawns.Add(new RespawnInfo { MonsterIndex = 1, Location = centre, Count = (ushort)Math.Max(20, size / 2), Spread = (ushort)spread, Delay = 1, RespawnIndex = 2 * m + 1 });
            map.Respawns.Add(new RespawnInfo { MonsterIndex = 2, Location = centre, Count = (ushort)Math.Max(10, size / 5), Spread = (ushort)spread, Delay = 1, RespawnIndex = 2 * m + 2 });
            e.MapInfoList.Add(map);
        }

        var sword = new ItemInfo { Index = 1, Name = "WoodenSword", Type = ItemType.Weapon, Shape = 1, Durability = 5000, Price = 100, StackSize = 1, StartItem = true };
        sword.Stats[Stat.MinDC] = 2; sword.Stats[Stat.MaxDC] = 5;
        var armour = new ItemInfo { Index = 2, Name = "BaseDress(M)", Type = ItemType.Armour, Durability = 5000, Price = 120, StackSize = 1, StartItem = true };
        armour.Stats[Stat.MinAC] = 1; armour.Stats[Stat.MaxAC] = 3;
        var potion = new ItemInfo { Index = 3, Name = "(HP)DrugSmall", Type = ItemType.Potion, Price = 10, StackSize = 50, StartItem = true };
        potion.Stats[Stat.HP] = 30;
        var gold = new ItemInfo { Index = 4, Name = "Gold", Type = ItemType.Nothing, StackSize = 1 };
        var ore = new ItemInfo { Index = 5, Name = "BlackIronOre", Type = ItemType.Ore, Durability = 15000, StackSize = 1 };
        e.ItemInfoList.AddRange(new[] { sword, armour, potion, gold, ore });

        var hen = new MonsterInfo { Index = 1, Name = "ScratchHen", Image = (Monster)0, AI = 1, Level = 1, Experience = 5, ViewRange = 7, CanPush = true };
        hen.Stats[Stat.HP] = 40;
        var oma = new MonsterInfo { Index = 2, Name = "ScratchOma", Image = (Monster)0, AI = 0, Level = 5, Experience = 40, ViewRange = 5, AttackSpeed = 2500, MoveSpeed = 1800 };
        oma.Stats[Stat.HP] = 120; oma.Stats[Stat.MinDC] = 1; oma.Stats[Stat.MaxDC] = 3; oma.Stats[Stat.MinAC] = 0; oma.Stats[Stat.MaxAC] = 1;
        e.MonsterInfoList.Add(hen);
        e.MonsterInfoList.Add(oma);
    }
}

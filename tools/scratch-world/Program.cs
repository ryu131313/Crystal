using System.Drawing;
using Server.MirDatabase;
using Server.MirEnvir;
using StressClient;

namespace ScratchWorld;

/// <summary>
/// Generates a minimal world a scratch Server.exe will start on:
///  - Maps\&lt;name&gt;.map, either synthetic (the oldest Crystal format, "type 0", all walkable floor) or copies of real
///    server map files (--maps-from; the files are only read, never modified),
///  - Server.MirDB holding those maps (FileName = the map name), a StartPoint safe zone on a walkable cell, two monster
///    types with respawns around it, and a few StartItem items.
/// The walkability scan uses the same parser the stress bots use (StressClient\MapFile.cs, compiled in), so a copied
/// real map always gets a usable start point.
/// Envir resolves Server.MirDB relative to the current directory, so the save runs with cwd = the output folder.
/// SCRATCH ONLY: never point --out at a live server folder (the tool refuses a folder that already has a Server.MirDB
/// unless --force, and always refuses a path under the repo's Build\ folder).
/// </summary>
internal static class Program
{
    private sealed class MapSpec
    {
        public string Name = string.Empty;
        public string Title = string.Empty;
        public Point Start;
        public int Spread;
        public ushort Hens;
        public ushort Omas;
        public MapGrid Grid;
    }

    private static int Main(string[] args)
    {
        string outDir = null;
        string mapName = "scratch0";
        string mapsFrom = null;
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
                case "--maps-from": mapsFrom = args[++i]; break;
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

        MapSpec[] specs;
        if (mapsFrom == null)
        {
            // Map 0 keeps the --map name; extra maps are <name minus trailing digits><n> (scratch1, scratch2, ...). Each has
            // a start point, and new characters pick a random start point, so bots spread across maps.
            var mapNames = new string[maps];
            var stem = mapName.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            specs = new MapSpec[maps];
            int spread = Math.Max(10, size / 2 - 10);

            for (int m = 0; m < maps; m++)
            {
                mapNames[m] = m == 0 ? mapName : stem + m;
                var mapPath = Path.Combine(outDir, "Maps", mapNames[m] + ".map");
                File.WriteAllBytes(mapPath, BuildMapV0(size, size));
                Console.WriteLine($"Wrote {mapPath} ({size}x{size}, walkable)");

                specs[m] = new MapSpec
                {
                    Name = mapNames[m],
                    Title = m == 0 ? "Scratch Plains" : "Scratch Plains " + m,
                    Start = new Point(size / 2, size / 2),
                    Spread = spread,
                    Hens = (ushort)Math.Max(20, size / 2),
                    Omas = (ushort)Math.Max(10, size / 5),
                };
            }
        }
        else
        {
            specs = CopyRealMaps(mapsFrom, outDir, maps);
            if (specs == null) return 2;
        }

        var old = Environment.CurrentDirectory;
        Directory.SetCurrentDirectory(outDir);
        try
        {
            if (force)
            {
                // Stale data from an earlier run would otherwise be backed up and kept next to the new DB.
                foreach (var f in new[] { "Server.MirDB", "Server.MirDBn", "Server.MirADB", "Server.MirADBn", "Server.MirADB.imported", "UserData.db", "UserData.db-journal", "UserData.db.tmp" })
                    if (File.Exists(f)) File.Delete(f);
            }

            WriteDesignFiles(outDir);

            var e = Envir.Main;
            Populate(e, specs);
            new Server.MirEnvir.Storage.BinaryWorldStore().SaveDesign(e, null); // Server.MirDB; the server imports it into SQLite on its first start
            Console.WriteLine($"Wrote {Path.Combine(outDir, "Server.MirDB")} (version {Envir.Version}): " +
                              $"{e.MapInfoList.Count} map(s), {e.MonsterInfoList.Count} monsters, {e.ItemInfoList.Count} items, " +
                              $"{e.MapInfoList.Sum(m => m.Respawns.Count)} respawns, {e.NPCInfoList.Count} NPC(s), {e.QuestInfoList.Count} quest(s)");
        }
        finally
        {
            Directory.SetCurrentDirectory(old);
        }

        return 0;
    }

    /// <summary>The bot-facing content: a merchant script (buy/sell, repair, storage, quests) and one kill quest.</summary>
    private static void WriteDesignFiles(string outDir)
    {
        var npcDir = Path.Combine(outDir, "Envir", "NPCs");
        var questDir = Path.Combine(outDir, "Envir", "Quests");
        Directory.CreateDirectory(npcDir);
        Directory.CreateDirectory(questDir);

        File.WriteAllText(Path.Combine(npcDir, "scratchtrader.txt"), """
            [@MAIN]
            Welcome to the scratch plains.
            <<Buy and sell/@BUYSELL>>
            <<Repair/@REPAIR>>
            <<Storage/@STORAGE>>

            [@BUYSELL]
            What do you need?

            [@REPAIR]
            Let me look at that.

            [@STORAGE]
            Your storage.

            [TRADE]
            (HP)DrugSmall 50
            ScratchJunk 1

            [QUESTS]
            1
            -1
            """);

        File.WriteAllText(Path.Combine(questDir, "scratchhunt.txt"), """
            [@DESCRIPTION]
            The Scratch Oma are getting bold. Thin them out.
            [@TASKDESCRIPTION]
            Kill a Scratch Oma.
            [@KILLTASKS]
            ScratchOma 1 "Scratch Oma slain"
            [@EXPREWARD]
            200
            [@GOLDREWARD]
            500
            """);

        Console.WriteLine($"Wrote {npcDir}\\scratchtrader.txt and {questDir}\\scratchhunt.txt");
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: ScratchWorld --out <scratch folder> [--size 200] [--map scratch0] [--maps 1] [--force]");
        Console.Error.WriteLine("       ScratchWorld --out <scratch folder> --maps-from <folder with real .map files> [--maps N] [--force]");
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

    /// <summary>Copies <paramref name="count"/> real map files into the scratch Maps folder and picks a walkable start cell.</summary>
    private static MapSpec[] CopyRealMaps(string source, string outDir, int count)
    {
        if (!Directory.Exists(source))
        {
            Console.Error.WriteLine($"--maps-from folder '{source}' does not exist.");
            return null;
        }

        var files = Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly)
            .Where(f => string.Equals(Path.GetExtension(f), ".map", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            Console.Error.WriteLine($"--maps-from folder '{source}' has no .map files.");
            return null;
        }

        int wanted = Math.Min(count, files.Count);
        if (wanted < count)
            Console.WriteLine($"Only {files.Count} map file(s) in '{source}'; using {wanted}.");

        var specs = new List<MapSpec>(wanted);

        for (int i = 0; i < wanted; i++)
        {
            // Spread the picks across the alphabetical list so a small --maps is a varied sample of the folder.
            int pick = (int)((long)i * files.Count / wanted);
            var file = files[pick];
            var name = Path.GetFileNameWithoutExtension(file);

            var bytes = File.ReadAllBytes(file);
            File.WriteAllBytes(Path.Combine(outDir, "Maps", name + ".map"), bytes);

            var grid = MapFile.Load(bytes);
            if (grid.Error != null)
                Console.WriteLine($"Warning: {name}.map: {grid.Error}; using the map centre as the start point.");
            if (!grid.Loaded && grid.Error != null) grid.Width = grid.Height = 0;

            var start = FindWalkable(grid) ?? new Point(Math.Max(0, grid.Width / 2), Math.Max(0, grid.Height / 2));
            int spread = grid.Loaded ? Math.Max(5, Math.Min(grid.Width, grid.Height) / 2 - 10) : 20;

            specs.Add(new MapSpec
            {
                Name = name,
                Title = name,
                Start = start,
                Spread = spread,
                Hens = 30,
                Omas = 15,
                Grid = grid,
            });

            Console.WriteLine($"Copied {Path.GetFileName(file)} -> Maps\\{name}.map " +
                              $"({grid.Width}x{grid.Height} type {grid.Type}, start {start.X},{start.Y})");
        }

        return specs.ToArray();
    }

    /// <summary>First walkable cell on a ring expanding from the map centre, or null when the whole map is walls.</summary>
    private static Point? FindWalkable(MapGrid grid)
    {
        if (!grid.Loaded) return null;

        int cx = grid.Width / 2, cy = grid.Height / 2;
        int maxRadius = Math.Max(grid.Width, grid.Height);

        for (int r = 0; r <= maxRadius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    int x = cx + dx, y = cy + dy;
                    if (grid.InBounds(x, y) && !grid.IsBlocked(x, y)) return new Point(x, y);
                }
            }
        }

        return null;
    }

    /// <summary>A walkable cell next to the start point for the merchant (bots must be within Globals.DataRange to talk).</summary>
    private static Point FindNpcSpot(MapGrid grid, Point start)
    {
        if (grid == null || !grid.Loaded) return new Point(start.X + 1, start.Y);

        for (int r = 1; r <= 4; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    int x = start.X + dx, y = start.Y + dy;
                    if (grid.InBounds(x, y) && !grid.IsBlocked(x, y)) return new Point(x, y);
                }
            }
        }

        return start;
    }

    private static void Populate(Envir e, MapSpec[] specs)
    {
        e.MapIndex = specs.Length;
        e.ItemIndex = 6;
        e.MonsterIndex = 2;
        e.NPCIndex = 1;
        e.QuestIndex = 1;
        e.GameshopIndex = 0;
        e.RespawnIndex = 2 * specs.Length;

        for (int m = 0; m < specs.Length; m++)
        {
            var spec = specs[m];
            var map = new MapInfo { Index = m + 1, FileName = spec.Name, Title = spec.Title, Light = LightSetting.Day };
            map.SafeZones.Add(new SafeZoneInfo { Info = map, Location = spec.Start, Size = 5, StartPoint = true });
            // Passive "hen" (AI 1 = Deer: wanders, never attacks) and an aggressive melee mob (AI 0 = base MonsterObject).
            // The Oma stay close to the start point so bots (and the Scratch Hunt quest) find fights without long walks.
            map.Respawns.Add(new RespawnInfo { MonsterIndex = 1, Location = spec.Start, Count = spec.Hens, Spread = (ushort)spec.Spread, Delay = 1, RespawnIndex = 2 * m + 1 });
            map.Respawns.Add(new RespawnInfo { MonsterIndex = 2, Location = spec.Start, Count = spec.Omas, Spread = (ushort)Math.Min(spec.Spread, 20), Delay = 1, RespawnIndex = 2 * m + 2 });
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
        var junk = new ItemInfo { Index = 6, Name = "ScratchJunk", Type = ItemType.Ore, Durability = 1000, Price = 25, StackSize = 1, StartItem = true };
        e.ItemInfoList.AddRange(new[] { sword, armour, potion, gold, ore, junk });

        var hen = new MonsterInfo { Index = 1, Name = "ScratchHen", Image = (Monster)0, AI = 1, Level = 1, Experience = 5, ViewRange = 7, CanPush = true };
        hen.Stats[Stat.HP] = 40;
        var oma = new MonsterInfo { Index = 2, Name = "ScratchOma", Image = (Monster)0, AI = 0, Level = 5, Experience = 40, ViewRange = 5, AttackSpeed = 2500, MoveSpeed = 1800 };
        oma.Stats[Stat.HP] = 120; oma.Stats[Stat.MinDC] = 1; oma.Stats[Stat.MaxDC] = 3; oma.Stats[Stat.MinAC] = 0; oma.Stats[Stat.MaxAC] = 1;
        e.MonsterInfoList.Add(hen);
        e.MonsterInfoList.Add(oma);

        // Merchant on the first map: buy/sell, repair, storage and the Scratch Hunt quest (Envir\NPCs\scratchtrader.txt).
        var first = specs[0];
        e.NPCInfoList.Add(new NPCInfo
        {
            Index = 1,
            FileName = "scratchtrader",
            Name = "Trader",
            MapIndex = 1,
            Location = FindNpcSpot(first.Grid, first.Start),
            Image = 0,
            Rate = 100,
        });

        // Kill quest defined in Envir\Quests\scratchhunt.txt; the NPC script links it to the merchant.
        e.QuestInfoList.Add(new QuestInfo
        {
            Index = 1,
            Name = "Scratch Hunt",
            Group = "Scratch",
            FileName = "scratchhunt",
            RequiredMinLevel = 1,
            RequiredMaxLevel = 0,
            RequiredClass = RequiredClass.None,
            Type = QuestType.General,
        });
    }
}

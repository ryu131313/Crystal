using System.Drawing;

namespace StressClient;

/// <summary>
/// Collision view of one server map file. The cell layout matches the server's loader exactly
/// (Server/MirEnvir/Map.cs: FindType + LoadMapCellsv0..v7 + LoadMapCellsV100); only the wall and door
/// information is kept, because a headless bot never draws tiles.
/// </summary>
internal sealed class MapGrid
{
    public int Width, Height;
    public int Type;

    /// <summary>Column-major, like the file and the server's Cell[,]: index = x * Height + y.</summary>
    public bool[] Blocked = Array.Empty<bool>();
    public byte[] Doors = Array.Empty<byte>();

    public string Error;

    public bool Loaded => Width > 0 && Height > 0 && Blocked.Length == Width * Height;

    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public bool IsBlocked(int x, int y) => !InBounds(x, y) || Blocked[x * Height + y];

    public byte DoorAt(int x, int y) => InBounds(x, y) ? Doors[x * Height + y] : (byte)0;
}

/// <summary>
/// Pure parser: bytes in, walkable/blocked cells out. No game state, no graphics.
/// This single file is compiled into StressClient and linked into tools/scratch-world (ScratchWorld.csproj),
/// so the bot and the scratch world agree on collision for real map files.
/// </summary>
internal static class MapFile
{
    public static MapGrid Load(byte[] bytes)
    {
        var grid = new MapGrid();
        try
        {
            Parse(bytes, grid);
        }
        catch (Exception ex)
        {
            grid.Error = ex.Message;
        }
        return grid;
    }

    private static void Parse(byte[] b, MapGrid g)
    {
        if (b == null || b.Length < 20) throw new InvalidDataException("map file too short");

        switch (Detect(b))
        {
            case 100: LoadV100(b, g); break;
            case 0: LoadV0(b, g); break;
            case 1: LoadV1(b, g); break;
            case 2: LoadV2(b, g); break;
            case 3: LoadV3(b, g); break;
            case 4: LoadV4(b, g); break;
            case 5: LoadV5(b, g); break;
            case 6: LoadV6(b, g); break;
            case 7: LoadV7(b, g); break;
            default: throw new InvalidDataException("unknown map format");
        }
    }

    /// <summary>Same detection as Map.FindType (Server/MirEnvir/Map.cs:94) and the client's MapReader.</summary>
    private static int Detect(byte[] b)
    {
        if (b[2] == 0x43 && b[3] == 0x23) return 100;
        if (b[0] == 0) return 5;
        if (b[0] == 0x0F && b[5] == 0x53 && b[14] == 0x33) return 6;
        if (b[0] == 0x15 && b[4] == 0x32 && b[6] == 0x41 && b[19] == 0x31) return 4;
        if (b[0] == 0x10 && b[2] == 0x61 && b[7] == 0x31 && b[14] == 0x31) return 1;

        // shanda's 2012 format and one of shandas(wemades) older formats share the header; the file size decides.
        if (b[4] == 0x0F || (b[4] == 0x03 && b[18] == 0x0D && b[19] == 0x0A))
        {
            int w = b[0] + (b[1] << 8);
            int h = b[2] + (b[3] << 8);
            return b.Length > 52 + w * h * 14 ? 3 : 2;
        }

        if (b[0] == 0x0D && b[1] == 0x4C && b[7] == 0x20 && b[11] == 0x6D) return 7;
        return 0;
    }

    private static short I16(byte[] b, int o)
    {
        if (o < 0 || o + 2 > b.Length) throw new InvalidDataException("map file truncated");
        return BitConverter.ToInt16(b, o);
    }

    private static int I32(byte[] b, int o)
    {
        if (o < 0 || o + 4 > b.Length) throw new InvalidDataException("map file truncated");
        return BitConverter.ToInt32(b, o);
    }

    private static byte B8(byte[] b, int o)
    {
        if (o < 0 || o >= b.Length) throw new InvalidDataException("map file truncated");
        return b[o];
    }

    private static void Allocate(MapGrid g, int width, int height, int type)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 50_000_000)
            throw new InvalidDataException($"bad map size {width}x{height}");

        g.Width = width;
        g.Height = height;
        g.Type = type;
        g.Blocked = new bool[width * height];
        g.Doors = new byte[width * height];
    }

    private static void Wall(MapGrid g, int x, int y) => g.Blocked[x * g.Height + y] = true;

    private static void Door(MapGrid g, int x, int y, byte door)
    {
        if (door == 0) return;
        door = (byte)(door & 0x7F);
        if (door == 0) return;
        int i = x * g.Height + y;
        g.Doors[i] = door;
        g.Blocked[i] = true; // a door cell is only walkable while the door is open
    }

    // ---- formats (offsets copied from the server loader) ----

    private static void LoadV0(byte[] b, MapGrid g)
    {
        int w = I16(b, 0), h = I16(b, 2);
        Allocate(g, w, h, 0);

        int o = 52;
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                if ((I16(b, o) & 0x8000) != 0) Wall(g, x, y);
                o += 2;
                if ((I16(b, o) & 0x8000) != 0) Wall(g, x, y);
                o += 2;
                if ((I16(b, o) & 0x8000) != 0) Wall(g, x, y);
                o += 4;
                Door(g, x, y, B8(b, o));
                o += 3;
                o++; // light
            }
    }

    private static void LoadV1(byte[] b, MapGrid g)
    {
        int o = 21;
        int w = I16(b, o); o += 2;
        int xor = I16(b, o); o += 2;
        int h = I16(b, o);
        w ^= xor;
        h ^= xor;
        Allocate(g, w, h, 1);

        o = 54;
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                if (((I32(b, o) ^ 0xAA38AA38) & 0x20000000) != 0) Wall(g, x, y);
                o += 6;
                if (((I16(b, o) ^ xor) & 0x8000) != 0) Wall(g, x, y);
                o += 2;
                Door(g, x, y, B8(b, o));
                o += 5;
                o++; // light
                o++;
            }
    }

    private static void LoadV2(byte[] b, MapGrid g)
    {
        int w = I16(b, 0), h = I16(b, 2);
        Allocate(g, w, h, 2);

        int o = 52;
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                if ((I16(b, o) & 0x8000) != 0) Wall(g, x, y);
                o += 2;
                if ((I16(b, o) & 0x8000) != 0) Wall(g, x, y);
                o += 2;
                if ((I16(b, o) & 0x8000) != 0) Wall(g, x, y);
                o += 2;
                Door(g, x, y, B8(b, o));
                o += 5;
                o++; // light
                o += 2;
            }
    }

    private static void LoadV3(byte[] b, MapGrid g)
    {
        int w = I16(b, 0), h = I16(b, 2);
        Allocate(g, w, h, 3);

        int o = 52;
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                if ((I16(b, o) & 0x8000) != 0) Wall(g, x, y);
                o += 2;
                if ((I16(b, o) & 0x8000) != 0) Wall(g, x, y);
                o += 2;
                if ((I16(b, o) & 0x8000) != 0) Wall(g, x, y);
                o += 2;
                Door(g, x, y, B8(b, o));
                o += 12;
                o++; // light
                o += 17;
            }
    }

    private static void LoadV4(byte[] b, MapGrid g)
    {
        int o = 31;
        int w = I16(b, o); o += 2;
        int xor = I16(b, o); o += 2;
        int h = I16(b, o);
        w ^= xor;
        h ^= xor;
        Allocate(g, w, h, 4);

        o = 64;
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                if ((I16(b, o) & 0x8000) != 0) Wall(g, x, y);
                o += 2;
                if ((I16(b, o) & 0x8000) != 0) Wall(g, x, y);
                o += 4;
                Door(g, x, y, B8(b, o));
                o += 6;
            }
    }

    private static void LoadV5(byte[] b, MapGrid g)
    {
        int w = I16(b, 22), h = I16(b, 24);
        Allocate(g, w, h, 5);

        int o = 28 + (3 * ((w / 2) + (w % 2)) * (h / 2));
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                byte flags = B8(b, o);
                if ((flags & 0x01) != 1 || (flags & 0x02) != 2) Wall(g, x, y);
                o += 13;
                o++; // light
            }
    }

    private static void LoadV6(byte[] b, MapGrid g)
    {
        int w = I16(b, 16), h = I16(b, 18);
        Allocate(g, w, h, 6);

        int o = 40;
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                byte flags = B8(b, o);
                if ((flags & 0x01) != 1 || (flags & 0x02) != 2) Wall(g, x, y);
                o += 20;
            }
    }

    private static void LoadV7(byte[] b, MapGrid g)
    {
        int o = 21;
        int w = I16(b, o);
        o += 4;
        int h = I16(b, o);
        Allocate(g, w, h, 7);

        o = 54;
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                if ((I16(b, o) & 0x8000) != 0) Wall(g, x, y);
                o += 6;
                if ((I16(b, o) & 0x8000) != 0) Wall(g, x, y);
                o += 2;
                Door(g, x, y, B8(b, o));
                o += 4;
                o++; // light
                o += 2;
            }
    }

    private static void LoadV100(byte[] b, MapGrid g)
    {
        if (b[0] != 1 || b[1] != 0) throw new InvalidDataException($"unsupported C# map version {b[0]}.{b[1]}");

        int w = I16(b, 4), h = I16(b, 6);
        Allocate(g, w, h, 100);

        int o = 8;
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                o += 2; // back index
                if ((I32(b, o) & 0x20000000) != 0) Wall(g, x, y);
                o += 10;
                if ((I16(b, o) & 0x8000) != 0) Wall(g, x, y); // front image: no floor tile
                o += 2;
                Door(g, x, y, B8(b, o));
                o += 11;
                o++; // light
            }
    }
}

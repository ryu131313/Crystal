namespace StressClient;

/// <summary>
/// Loads map files on demand from the configured folders and caches the collision grids. Thread-safe: bots on
/// different workers (and the Director) share it. File names come from S.MapChanged.FileName (no extension).
/// </summary>
internal sealed class MapManager
{
    private readonly Options _o;
    private readonly Metrics _m;
    private readonly object _lock = new();
    private readonly Dictionary<string, MapGrid> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _index;

    public MapManager(Options options, Metrics metrics)
    {
        _o = options;
        _m = metrics;
    }

    public MapGrid Get(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        lock (_lock)
        {
            if (_cache.TryGetValue(fileName, out var cached)) return cached;
            if (_missing.Contains(fileName)) return null;

            EnsureIndex();
            if (!_index.TryGetValue(fileName, out var path))
            {
                _missing.Add(fileName);
                Metrics.Inc(ref _m.MapsMissing);
                return null;
            }

            MapGrid grid;
            try
            {
                grid = MapFile.Load(File.ReadAllBytes(path));
            }
            catch
            {
                grid = null;
            }

            if (grid == null || !grid.Loaded || grid.Error != null)
            {
                _missing.Add(fileName);
                Metrics.Inc(ref _m.MapsMissing);
                if (!_o.Quiet) Console.WriteLine($"  map {fileName}: {(grid?.Error ?? "unreadable")} ({path})");
                return null;
            }

            _cache[fileName] = grid;
            _m.MapTypes.AddOrUpdate(grid.Type, 1, (_, n) => n + 1);
            Metrics.Inc(ref _m.MapsLoaded);
            if (!_o.Quiet) Console.WriteLine($"  map {fileName}: {grid.Width}x{grid.Height}, type {grid.Type} ({path})");
            return grid;
        }
    }

    private void EnsureIndex()
    {
        if (_index != null) return;

        _index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in _o.MapFolders)
        {
            string root;
            try { root = Path.GetFullPath(folder); }
            catch { continue; }
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var file in files)
            {
                if (!string.Equals(Path.GetExtension(file), ".map", StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileNameWithoutExtension(file);
                if (!_index.ContainsKey(name)) _index[name] = file;
            }
        }

        if (_index.Count == 0 && !_o.Quiet)
            Console.WriteLine($"  warning: no .map files found in: {string.Join(", ", _o.MapFolders)} (bots fall back to greedy movement)");
    }
}

/// <summary>--check-maps: parse every map in a folder (read-only) and print what the bot's loader understands.</summary>
internal static class MapCheck
{
    public static int Run(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            Console.Error.WriteLine($"error: map folder '{folder}' does not exist");
            return 2;
        }

        int ok = 0, failed = 0;
        var types = new SortedDictionary<int, int>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (!string.Equals(Path.GetExtension(file), ".map", StringComparison.OrdinalIgnoreCase)) continue;

                MapGrid grid;
                try { grid = MapFile.Load(File.ReadAllBytes(file)); }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"FAIL  {Path.GetFileName(file)}  ({ex.Message})");
                    continue;
                }

                if (grid.Loaded && grid.Error == null)
                {
                    ok++;
                    types[grid.Type] = types.TryGetValue(grid.Type, out var n) ? n + 1 : 1;
                    Console.WriteLine($"ok    {Path.GetFileName(file)}  {grid.Width}x{grid.Height} type {grid.Type}");
                }
                else
                {
                    failed++;
                    Console.WriteLine($"FAIL  {Path.GetFileName(file)}  {grid.Error ?? "incomplete"}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine($"maps: {ok} parsed, {failed} failed; formats: {string.Join(", ", types.Select(kv => $"type {kv.Key} x{kv.Value}"))}");
        return failed == 0 ? 0 : 1;
    }
}

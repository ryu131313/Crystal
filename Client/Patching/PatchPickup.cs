using System.Diagnostics;
using System.Reflection;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirObjects;
using Client.MirScenes;
using Client.MirSounds;

namespace Client.Patching
{
    /// <summary>
    /// UI thread side of the in-game patcher (plan §4.3): every 250 ms, after <see cref="Libraries.Loaded"/>, takes installed
    /// paths from <see cref="PatchService"/> for up to 4 ms of UI time (and about once a frame (16 ms) while some are left, so
    /// a fresh install's thousands of files land in seconds) and makes the game use them without a restart:
    ///   known .Lib    → <see cref="MLibrary.Reopen"/>, then monsters/NPCs still on the default frames take the new ones
    ///   current .map  → <see cref="MapControl.ReloadCells"/> (no ResetMap)
    ///   Sound\*       → <see cref="SoundManager.InvalidateFile"/>
    ///   anything else → used at the next start
    /// A new manifest grows the library arrays whose highest number went up.
    /// </summary>
    internal static class PatchPickup
    {
        private const int Interval = 250, DrainInterval = 16;
        private static long _nextDrain;
        private const double BudgetMs = 4;
        private static long _nextTime;
        private static bool _backlog;
        private static int _manifestVersion = -1;
        private static Dictionary<string, MLibrary> _libraries;

        public static void Process()
        {
            if (!PatchService.Active || !Libraries.Loaded) return;
            if (CMain.Time < _nextTime)
            {
                // Installed paths still queued: keep draining about once a frame (the update loop is uncapped, and each
                // drain invalidates the map and the UI), without the status / manifest work below.
                if (_backlog && CMain.Time >= _nextDrain)
                {
                    _nextDrain = CMain.Time + DrainInterval;
                    try { Drain(); }
                    catch (Exception ex)
                    {
                        _backlog = false;
                        if (Settings.LogErrors) CMain.SaveError("[Patch] pickup: " + ex);
                        _nextTime = CMain.Time + 10_000;
                    }
                }
                return;
            }
            _nextTime = CMain.Time + Interval;

            try
            {
                if (_libraries == null) BuildLibraryMap();

                // Also on the first tick: the manifest may have changed between the Libraries ctor and Loaded.
                int version = PatchService.ManifestVersion;
                if (version != _manifestVersion)
                {
                    GrowArrays();
                    _manifestVersion = version;
                    SoundManager.InvalidateSilent(); // sounds played before this list was known were cached as silence
                    MapControl current = GameScene.Scene?.MapControl;
                    if (current != null && !current.IsDisposed && !current.WaitingForFile && !File.Exists(current.FileName) && PatchService.Demand(current.FileName))
                        current.WaitingForFile = true;
                    PrefetchMap(current); // the map's libraries could not be asked for before this list existed
                }

                // A map that will not arrive (faulted, gone from the manifest) must not block movement for good.
                MapControl map = GameScene.Scene?.MapControl;
                if (map != null && map.WaitingForFile && !PatchService.IsPending(map.FileName))
                {
                    if (File.Exists(map.FileName)) map.ReloadCells();
                    else map.WaitingForFile = false;
                }

                Drain();

                PatchStatus.Update(map != null && !map.IsDisposed && map.WaitingForFile);
                // Login / select: no download strip there, so a status line (a fresh install draws its art as it arrives).
                CMain.SetPatchLine(MirScene.ActiveScene is GameScene || !PatchStatus.Visible ? null : PatchStatus.Text);
                PatchUpdate.Process();
            }
            catch (Exception ex)
            {
                _backlog = false;
                if (Settings.LogErrors) CMain.SaveError("[Patch] pickup: " + ex);
                _nextTime = CMain.Time + 10_000;
            }
        }

        /// <summary>
        /// Picks up installed paths until the queue is empty or <see cref="BudgetMs"/> is spent (<see cref="_backlog"/> then
        /// asks for the next update), then invalidates the map and redraws the UI once for everything picked up.
        /// </summary>
        private static void Drain()
        {
            long start = Stopwatch.GetTimestamp();
            bool anyLibrary = false, uiLibrary = false;
            _backlog = false;
            while (PatchService.TryDequeueInstalled(out string path))
            {
                PickUp(path, ref anyLibrary, ref uiLibrary);
                if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= BudgetMs)
                {
                    _backlog = true;
                    break;
                }
            }
            if (!anyLibrary) return;
            // The floor and the map are cached textures rebuilt only on a move: redraw them now so tiles, objects and
            // monsters from the new libraries show at once.
            MapControl map = GameScene.Scene?.MapControl;
            if (map != null && !map.IsDisposed)
            {
                map.FloorValid = false;
                map.TextureValid = false;
            }
            // UI art (minimap picture, icons, frames) sits in cached control textures: rebuild them once per drain.
            if (uiLibrary) MirScene.ActiveScene?.Redraw();
        }

        private static bool IsMapLibrary(MLibrary library)
        {
            for (int i = 0; i < Libraries.MapLibs.Length; i++) if (Libraries.MapLibs[i] == library) return true;
            return false;
        }

        private static readonly int[] _mapLibDistance = new int[400];
        private static readonly List<(int Index, int Distance)> _mapLibs = new List<(int, int)>();

        /// <summary>
        /// UI thread, after a map loads or reloads (and on a new patch list): asks for every map library the map's cells use,
        /// nearest to the player first, instead of waiting for each one's first draw. One pass over the cells (the same
        /// fields DrawFloor / DrawObjects read); a library already on disk or already asked for under this list is skipped
        /// by MLibrary.Prefetch.
        /// </summary>
        public static void PrefetchMap(MapControl map)
        {
            if (!PatchService.Active || map == null || map.IsDisposed || map.M2CellInfo == null) return;
            CellInfo[,] cells = map.M2CellInfo;
            int width = Math.Min(map.Width, cells.GetLength(0)), height = Math.Min(map.Height, cells.GetLength(1));
            Point centre = MapObject.User != null ? MapObject.User.CurrentLocation : new Point(width / 2, height / 2);
            Array.Fill(_mapLibDistance, int.MaxValue);
            for (int x = 0; x < width; x++)
            {
                int dx = Math.Abs(x - centre.X);
                for (int y = 0; y < height; y++)
                {
                    CellInfo cell = cells[x, y];
                    if (cell == null) continue;
                    int d = Math.Max(dx, Math.Abs(y - centre.Y));
                    if (cell.BackImage != 0) Near(cell.BackIndex, d);
                    if (cell.MiddleImage > 0) Near(cell.MiddleIndex, d);
                    if ((cell.FrontImage & 0x7FFF) > 0 && cell.FrontIndex != 200) Near(cell.FrontIndex, d);
                }
            }
            _mapLibs.Clear();
            for (int i = 0; i < _mapLibDistance.Length; i++)
                if (_mapLibDistance[i] != int.MaxValue) _mapLibs.Add((i, _mapLibDistance[i]));
            // Farthest first: each request goes to the head of the queue, so the nearest library ends up first.
            _mapLibs.Sort((a, b) => b.Distance.CompareTo(a.Distance));
            for (int i = 0; i < _mapLibs.Count; i++) Libraries.MapLibs[_mapLibs[i].Index]?.Prefetch();
        }

        private static void Near(short index, int distance)
        {
            if (index >= 0 && index < _mapLibDistance.Length && distance < _mapLibDistance[index]) _mapLibDistance[index] = distance;
        }

        /// <summary>Makes one installed path live; the caller does the map invalidation and UI redraw once for the batch.</summary>
        private static void PickUp(string path, ref bool anyLibrary, ref bool uiLibrary)
        {
            string ext = Path.GetExtension(path);
            if (ext.Equals(MLibrary.Extention, StringComparison.OrdinalIgnoreCase))
            {
                if (!_libraries.TryGetValue(path, out MLibrary library)) return;
                library.Reopen();
                RefreshFrames(library);
                anyLibrary = true;
                if (!(MirScene.ActiveScene is GameScene) || !IsMapLibrary(library)) uiLibrary = true;
            }
            else if (ext.Equals(".map", StringComparison.OrdinalIgnoreCase))
            {
                MapControl map = GameScene.Scene?.MapControl;
                if (map != null && !map.IsDisposed && string.Equals(PatchPaths.Normalize(map.FileName), path, StringComparison.OrdinalIgnoreCase))
                    map.ReloadCells();
            }
            else if (path.StartsWith(PatchPaths.Normalize(Settings.SoundPath), StringComparison.OrdinalIgnoreCase))
            {
                SoundManager.InvalidateFile(path);
            }
        }

        /// <summary>
        /// Monsters and NPCs copy BodyLibrary.Frames at Load, so one loaded before its library arrived is still on the
        /// default frames. The default check leaves fixed FrameSets (HellBomb, CaveStatue, DragonStatue, GreatFoxSpirit) and
        /// Shinsu's swap alone; players use FrameSet.Player and need nothing.
        /// </summary>
        private static void RefreshFrames(MLibrary library)
        {
            if (GameScene.Scene?.MapControl == null || library.Frames == null) return;
            var objects = MapControl.ObjectsList;
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i] is MonsterObject monster && monster.BodyLibrary == library && monster.Frames == FrameSet.DefaultMonster)
                {
                    monster.Frames = library.Frames;
                    monster.SetAction();
                }
                else if (objects[i] is NPCObject npc && npc.BodyLibrary == library && npc.Frames == FrameSet.DefaultNPC)
                {
                    npc.Frames = library.Frames;
                    npc.SetAction();
                }
            }
        }

        /// <summary>Every MLibrary the game holds (static fields and arrays of <see cref="Libraries"/>), by normalised path.</summary>
        private static void BuildLibraryMap()
        {
            _libraries = new Dictionary<string, MLibrary>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in typeof(Libraries).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.GetValue(null) is MLibrary single) Add(single);
                else if (field.GetValue(null) is MLibrary[] array)
                    for (int i = 0; i < array.Length; i++) Add(array[i]);
            }
        }

        private static void Add(MLibrary library)
        {
            if (library == null || library == Libraries.Empty) return;
            string path = PatchPaths.Normalize(library.FileName);
            if (path.Length > 0 && !_libraries.ContainsKey(path)) _libraries[path] = library;
        }

        /// <summary>
        /// A new manifest added numbers beyond an array: resize it (UI thread, after Loaded), then point the objects already
        /// on screen that got the empty stand-in (their number had no slot when they arrived) at their real library.
        /// </summary>
        private static void GrowArrays()
        {
            bool grew = false;
            foreach (var spec in Libraries.ArraySpecs)
            {
                var field = typeof(Libraries).GetField(spec.Field, BindingFlags.Public | BindingFlags.Static);
                if (field?.GetValue(null) is not MLibrary[] array) continue;
                int wanted = PatchService.HighestIndex(spec.Path, spec.Suffix) + 1;
                if (wanted <= array.Length) continue;
                int old = array.Length;
                Array.Resize(ref array, wanted);
                for (int i = old; i < wanted; i++)
                {
                    array[i] = new MLibrary(spec.Path + i.ToString(spec.Format) + spec.Suffix);
                    array[i].Initialize();
                    Add(array[i]);
                }
                field.SetValue(null, array);
                grew = true;
            }
            if (grew) RebindObjects();
        }

        /// <summary>
        /// NPCs and monsters loaded while their number was beyond the array draw Libraries.Empty on the default frames:
        /// resolve them again (as Load does), take the library's frames, restart the action and ask for the library at once.
        /// Players re-pick every library (SetLibraries falls back to slot 0 / nothing for an out-of-range number).
        /// </summary>
        private static void RebindObjects()
        {
            if (GameScene.Scene?.MapControl == null) return;
            var objects = MapControl.ObjectsList;
            for (int i = 0; i < objects.Count; i++)
            {
                switch (objects[i])
                {
                    case NPCObject npc when npc.BodyLibrary == Libraries.Empty:
                        npc.LoadLibrary();
                        if (npc.BodyLibrary == Libraries.Empty) break;
                        npc.Frames = npc.BodyLibrary.Frames ?? FrameSet.DefaultNPC;
                        npc.SetAction();
                        npc.BodyLibrary.Prefetch();
                        break;
                    case MonsterObject monster when monster.BodyLibrary == Libraries.Empty && (ushort)monster.BaseImage < Libraries.Monsters.Length:
                        monster.BodyLibrary = Libraries.Monsters[(ushort)monster.BaseImage];
                        monster.Frames = monster.BodyLibrary.Frames ?? FrameSet.DefaultMonster;
                        monster.SetAction();
                        monster.BodyLibrary.Prefetch();
                        break;
                    case PlayerObject player:
                        player.SetLibraries();
                        break;
                }
            }
        }
    }
}

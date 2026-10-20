using System.Reflection;
using Client.MirGraphics;
using Client.MirObjects;
using Client.MirScenes;
using Client.MirSounds;

namespace Client.Patching
{
    /// <summary>
    /// UI thread side of the in-game patcher (plan §4.3): at most every 250 ms, after <see cref="Libraries.Loaded"/>, takes up
    /// to 8 installed paths from <see cref="PatchService"/> and makes the game use them without a restart:
    ///   known .Lib    → <see cref="MLibrary.Reopen"/>, then monsters/NPCs still on the default frames take the new ones
    ///   current .map  → <see cref="MapControl.ReloadCells"/> (no ResetMap)
    ///   Sound\*       → <see cref="SoundManager.InvalidateFile"/>
    ///   anything else → used at the next start
    /// A new manifest grows the library arrays whose highest number went up.
    /// </summary>
    internal static class PatchPickup
    {
        private const int Interval = 250, PerTick = 8;
        private static long _nextTime;
        private static int _manifestVersion = -1;
        private static Dictionary<string, MLibrary> _libraries;

        public static void Process()
        {
            if (!PatchService.Active || CMain.Time < _nextTime || !Libraries.Loaded) return;
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
                }

                // A map that will not arrive (faulted, gone from the manifest) must not block movement for good.
                MapControl map = GameScene.Scene?.MapControl;
                if (map != null && map.WaitingForFile && !PatchService.IsPending(map.FileName))
                {
                    if (File.Exists(map.FileName)) map.ReloadCells();
                    else map.WaitingForFile = false;
                }

                for (int i = 0; i < PerTick && PatchService.TryDequeueInstalled(out string path); i++)
                    PickUp(path);

                PatchStatus.Update(map != null && !map.IsDisposed && map.WaitingForFile);
                PatchUpdate.Process();
            }
            catch (Exception ex)
            {
                if (Settings.LogErrors) CMain.SaveError("[Patch] pickup: " + ex);
                _nextTime = CMain.Time + 10_000;
            }
        }

        private static void PickUp(string path)
        {
            string ext = Path.GetExtension(path);
            if (ext.Equals(MLibrary.Extention, StringComparison.OrdinalIgnoreCase))
            {
                if (!_libraries.TryGetValue(path, out MLibrary library)) return;
                library.Reopen();
                RefreshFrames(library);
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

        /// <summary>A new manifest added numbers beyond an array: resize it (UI thread, after Loaded) so a later Load finds them.</summary>
        private static void GrowArrays()
        {
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
            }
        }
    }
}

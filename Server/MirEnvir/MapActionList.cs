using Server.MirObjects;

namespace Server.MirEnvir
{
    /// <summary>
    /// A map's delayed actions (roadmap P9). <see cref="Add"/> hides <see cref="List{T}.Add"/> so that queuing an action wakes
    /// the map in the game loop's map scheduler (<c>Envir.RunMaps</c>), which otherwise runs a map only when its timed work is due.
    /// Every caller adds through the <see cref="Map.ActionList"/> field, whose static type is this class, so the hiding method is
    /// the one called; never add through a <c>List&lt;DelayedAction&gt;</c> reference to it.
    /// <para>Threading: added from the game thread and from monster code on mob threads, drained by <see cref="Map.Process"/> on
    /// the game thread. An add during a mob pass is handed to the game thread (<see cref="Envir.RunOutsideMobPass"/>) and lands
    /// right after the barrier, before any map runs, so two mob threads never write the list at once.</para>
    /// </summary>
    public sealed class MapActionList : List<DelayedAction>
    {
        private readonly Map _map;

        public MapActionList(Map map)
        {
            _map = map;
        }

        public new void Add(DelayedAction action)
        {
            var envir = Envir.Main;
            if (envir.InMobPass)
            {
                envir.RunOutsideMobPass(() => AddNow(action));
                return;
            }

            AddNow(action);
        }

        private void AddNow(DelayedAction action)
        {
            base.Add(action);
            Envir.Main.WakeMap(_map);
        }
    }
}

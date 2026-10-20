using Client.MirControls;
using Client.MirScenes;

namespace Client.Patching
{
    /// <summary>
    /// UI thread side of program updates (in-game patcher plan §4.4, phase 4): asks "restart now?" once per staged set, and
    /// only while the game scene is the active one (client.md §3.4: a prompt parents to the active scene); otherwise it waits.
    /// Later leaves the Options entry and the strip line; the update is applied at exit anyway.
    /// </summary>
    internal static class PatchUpdate
    {
        private static bool _asked;

        /// <summary>From PatchPickup.Process (every 250 ms).</summary>
        public static void Process()
        {
            if (!PatchService.UpdateReady)
            {
                _asked = false; // a newer set staged later asks again
                return;
            }
            if (_asked || GameScene.Scene == null || GameScene.Scene.IsDisposed || MirScene.ActiveScene != GameScene.Scene) return;
            _asked = true;
            GameScene.Ui?.PatchUpdateReady();
        }

        /// <summary>
        /// Restart now: close the game window; Program.Main applies the staged set and starts the new build. The game's
        /// own exit rules still apply (a close refused during the log-out timer leaves the restart for the next exit).
        /// </summary>
        public static void RestartNow()
        {
            if (!PatchService.UpdateReady) return;
            PatchService.RequestRestart();
            Program.Form?.Close();
        }
    }
}

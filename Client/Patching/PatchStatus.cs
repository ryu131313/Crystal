using Client.MirScenes;

namespace Client.Patching
{
    /// <summary>
    /// What the in-game download strip shows (in-game patcher plan §4.4), UI thread only. <see cref="Update"/> runs from
    /// <see cref="PatchPickup.Process"/> (every 250 ms, so at most 4 times a second) and calls GameScene.Ui.PatchStatusChanged
    /// only when a shown value changed: files left, MB left, the bar's tenth of a percent, paused, failures, map wait.
    /// Hidden when nothing is left to download.
    /// </summary>
    internal static class PatchStatus
    {
        public static bool Visible { get; private set; }
        public static string Text { get; private set; } = string.Empty;
        /// <summary>0..1, bytes done of done + left.</summary>
        public static float Fraction { get; private set; }
        /// <summary>Bytes left if every file were fetched (tier 3 too): the Options "Download everything" size.</summary>
        public static long EverythingLeft { get; private set; }

        private static int _files = -1, _megabytes = -1, _permille = -1, _faults = -1;
        private static bool _paused, _map, _shown;

        public static void Update(bool waitingForMap)
        {
            PatchService.Snapshot p = PatchService.Progress;
            EverythingLeft = p.EverythingLeft;
            long total = p.BytesDone + p.BytesLeft;
            int permille = total > 0 ? (int)(1000 * p.BytesDone / total) : 0;
            int megabytes = (int)(p.BytesLeft >> 20);
            bool visible = p.FilesLeft > 0 || waitingForMap;
            if (visible == _shown && p.FilesLeft == _files && megabytes == _megabytes && permille == _permille && p.Faults == _faults
                && p.Paused == _paused && waitingForMap == _map) return;

            _shown = visible;
            _files = p.FilesLeft;
            _megabytes = megabytes;
            _permille = permille;
            _faults = p.Faults;
            _paused = p.Paused;
            _map = waitingForMap;

            Visible = visible;
            Fraction = permille / 1000f;
            if (visible)
            {
                string text = waitingForMap ? GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PatchDownloadingMap)
                    : GameLanguage.ClientTextMap.GetLocalization(p.Paused ? ClientTextKeys.PatchPausedStatus : ClientTextKeys.PatchDownloading, p.FilesLeft, Size(p.BytesLeft));
                if (p.Faults > 0) text += " · " + GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PatchFailed, p.Faults);
                Text = text;
            }
            // Only the live game scene has a strip; a later GameScene reads these values when it builds its UI.
            if (GameScene.Scene != null && !GameScene.Scene.IsDisposed && MirControls.MirScene.ActiveScene == GameScene.Scene) GameScene.Ui?.PatchStatusChanged();
        }

        /// <summary>RenderTest.UiTestRunner only: show fixed values without a running patcher (the next Update overwrites them).</summary>
        internal static void SetForTest(bool visible, string text, float fraction)
        {
            Visible = visible;
            Text = text ?? string.Empty;
            Fraction = fraction;
            _files = -1; // the next real Update repaints
            GameScene.Ui?.PatchStatusChanged();
        }

        /// <summary>"1.2 GB" / "350 MB" (at least 1 MB while anything is left, so a nearly finished file never reads 0).</summary>
        public static string Size(long bytes) =>
            bytes <= 0 ? "0 MB" : bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0") + " GB" : Math.Max(1, bytes >> 20) + " MB";
    }
}

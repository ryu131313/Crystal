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

        private static int _files = -1, _megabytes = -1, _permille = -1, _faults = -1, _speedTenths = -1;

        // Download speed: bytes done sampled every Update (every 250 ms); the rate over the oldest sample kept, about 3 s ago.
        private const int SpeedSamples = 12;
        private static readonly long[] _sampleTime = new long[SpeedSamples], _sampleBytes = new long[SpeedSamples];
        private static int _sampleNext, _sampleCount;

        /// <summary>The rate over the last ~3 s in tenths of a MB/s (0 until two samples exist).</summary>
        private static int SpeedTenths(long bytesDone)
        {
            long now = CMain.Time;
            _sampleTime[_sampleNext] = now;
            _sampleBytes[_sampleNext] = bytesDone;
            _sampleNext = (_sampleNext + 1) % SpeedSamples;
            if (_sampleCount < SpeedSamples) _sampleCount++;
            int oldest = _sampleCount < SpeedSamples ? 0 : _sampleNext;
            long ms = now - _sampleTime[oldest], bytes = bytesDone - _sampleBytes[oldest];
            if (ms <= 0 || bytes <= 0) return 0;
            return (int)Math.Round(bytes * 1000.0 / ms / (1 << 20) * 10);
        }
        private static bool _paused, _map, _shown, _ready, _offline;

        public static void Update(bool waitingForMap)
        {
            PatchService.Snapshot p = PatchService.Progress;
            EverythingLeft = p.EverythingLeft;
            long total = p.BytesDone + p.BytesLeft;
            int permille = total > 0 ? (int)(1000 * p.BytesDone / total) : 0;
            int megabytes = (int)(p.BytesLeft >> 20);
            bool ready = PatchService.UpdateReady;
            bool offline = p.Offline && !p.ReadOnly; // no patch list yet: the relay is down or still indexing (retried every 15 s)
            bool visible = p.FilesLeft > 0 || waitingForMap || ready || offline;
            bool downloading = p.FilesLeft > 0 && !p.Paused && !offline;
            int speedTenths = downloading ? SpeedTenths(p.BytesDone) : 0;
            if (!downloading) _sampleCount = 0; // a pause or an idle spell must not drag the next rate down
            if (speedTenths == _speedTenths && visible == _shown && p.FilesLeft == _files && megabytes == _megabytes && permille == _permille && p.Faults == _faults
                && p.Paused == _paused && waitingForMap == _map && ready == _ready && offline == _offline) return;

            _shown = visible;
            _speedTenths = speedTenths;
            _files = p.FilesLeft;
            _megabytes = megabytes;
            _permille = permille;
            _faults = p.Faults;
            _paused = p.Paused;
            _map = waitingForMap;
            _ready = ready;
            _offline = offline;

            Visible = visible;
            Fraction = p.FilesLeft == 0 && !waitingForMap ? 1f : permille / 1000f;
            if (visible)
            {
                string text = offline ? GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PatchConnecting)
                    : waitingForMap ? GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PatchDownloadingMap)
                    : p.FilesLeft > 0 ? GameLanguage.ClientTextMap.GetLocalization(p.Paused ? ClientTextKeys.PatchPausedStatus : ClientTextKeys.PatchDownloading, p.FilesLeft, Size(p.BytesLeft))
                    : string.Empty;
                if (speedTenths > 0 && p.FilesLeft > 0 && !waitingForMap) text += " · " + (speedTenths / 10.0).ToString("0.0") + " MB/s";
                if (ready) text = text.Length == 0 ? GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PatchUpdateReadyLine)
                    : text + " · " + GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PatchUpdateReadyLine);
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

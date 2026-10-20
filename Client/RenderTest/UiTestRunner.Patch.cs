using Client.MirScenes;
using Client.MirScenes.Modern;
using Client.Patching;

namespace Client.RenderTest
{
    /// <summary>
    /// In-game patcher phase 3: the download strip in the current UI (classic PatchStatusStrip / modern DownloadStrip), fed
    /// fixed values through PatchStatus.SetForTest: a download in progress, the map wait, then hidden. Screenshots show both.
    /// </summary>
    internal static partial class UiTestRunner
    {
        static partial void AddPatchSteps(List<Step> steps)
        {
            steps.Add(new Step { Name = "patch_strip_show", Act = () =>
            {
                PatchStatus.SetForTest(true, GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PatchDownloading, 12, PatchStatus.Size(350L << 20)), 0.4f);
                Expect(PatchStripVisible(), "the download strip to show");
            } });
            steps.Add(new Step { Name = "patch_strip_map", Act = () =>
            {
                PatchStatus.SetForTest(true, GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.PatchDownloadingMap), 0.9f);
                Expect(PatchStripVisible(), "the strip to stay up for the map wait");
            } });
            steps.Add(new Step { Name = "patch_strip_hide", Act = () =>
            {
                PatchStatus.SetForTest(false, string.Empty, 0f);
                Expect(!PatchStripVisible(), "the download strip to hide");
            } });
        }

        private static bool PatchStripVisible() =>
            GameScene.Ui is ModernUi modern ? modern.DownloadStrip != null && modern.DownloadStrip.Visible : ClassicPatchStripVisible;
    }
}

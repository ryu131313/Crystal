using Client.MirControls;
using Client.Patching;

namespace Client.MirScenes.Dialogs
{
    /// <summary>
    /// The classic in-game download strip (in-game patcher plan §4.4): a dark bar at the top centre with a fill and one line
    /// of text from <see cref="PatchStatus"/>. Never takes the mouse. Changed only from GameScene.ClassicPatchStatusChanged,
    /// which runs when a shown value changed (at most 4 times a second); hidden when nothing is downloading.
    /// </summary>
    public sealed class PatchStatusStrip : MirControl
    {
        private const int StripWidth = 360, StripHeight = 18;
        private readonly MirControl _fill;
        private readonly MirLabel _label;

        public PatchStatusStrip()
        {
            Size = new Size(StripWidth, StripHeight);
            Location = new Point((Settings.ScreenWidth - StripWidth) / 2, 4);
            BackColour = Color.FromArgb(255, 16, 12, 8);
            Opacity = 0.8F;
            DrawControlTexture = true;
            NotControl = true;
            Visible = false;

            _fill = new MirControl
            {
                Parent = this,
                Location = new Point(1, 1),
                Size = new Size(0, StripHeight - 2),
                BackColour = Color.FromArgb(255, 70, 120, 40),
                DrawControlTexture = true,
                NotControl = true,
            };
            _label = new MirLabel
            {
                Parent = this,
                Location = Point.Empty,
                Size = new Size(StripWidth, StripHeight),
                DrawFormat = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter,
                ForeColour = Color.FromArgb(255, 230, 220, 190),
                NotControl = true,
            };
            Refresh();
        }

        /// <summary>Takes the current PatchStatus; each setter redraws only on a real change.</summary>
        public void Refresh()
        {
            if (IsDisposed) return;
            if (!PatchStatus.Visible)
            {
                Visible = false;
                return;
            }
            _label.Text = PatchStatus.Text;
            int width = (int)((StripWidth - 2) * PatchStatus.Fraction);
            _fill.Size = new Size(width, StripHeight - 2);
            _fill.Visible = width > 0;
            Visible = true;
        }
    }
}

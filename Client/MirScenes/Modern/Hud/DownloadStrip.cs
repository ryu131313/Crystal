// The new UI's in-game download strip (in-game patcher plan §4.4): the same Patching.PatchStatus the classic
// PatchStatusStrip shows, as a rounded HUD pill at the top centre with a fill bar. Refreshed only from
// IGameUi.PatchStatusChanged (a real change, at most 4 times a second); hidden when nothing is downloading.
using System.Drawing.Drawing2D;
using Client.MirScenes.Modern.Core;
using Client.Patching;

namespace Client.MirScenes.Modern.Hud
{
    internal sealed class DownloadStrip : ModernSurface
    {
        private const int Width = 380, Height = 22;
        private readonly DownloadPill _pill;
        private readonly Action<bool> _onRelayout;

        public DownloadStrip()
        {
            PassThrough = true;
            Visible = false;
            _pill = new DownloadPill { Parent = this };
            _onRelayout = rebuild => Place();
            if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout += _onRelayout;
            Place();
            Refresh();
        }

        private void Place()
        {
            Size = new Size(S(Width), S(Height));
            Location = new Point(Math.Max(0, (Settings.ScreenWidth - S(Width)) / 2), S(6));
            _pill.Location = Point.Empty;
            _pill.Size = Size;
            _pill.Invalidate();
        }

        public void Refresh()
        {
            if (IsDisposed) return;
            if (!PatchStatus.Visible)
            {
                Visible = false;
                return;
            }
            _pill.Invalidate();
            Visible = true;
            TextureValid = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && ModernRoot.Instance != null) ModernRoot.Instance.Relayout -= _onRelayout;
            base.Dispose(disposing);
        }
    }

    internal sealed class DownloadPill : ModernControl
    {
        public DownloadPill()
        {
            NotControl = true;
        }

        protected override void Paint(Graphics g)
        {
            var r = new RectangleF(0.5f, 0.5f, Size.Width - 1, Size.Height - 1);
            float radius = Size.Height / 2f;
            using (GraphicsPath back = ModernSkin.RoundRect(r, radius))
            {
                using (var br = new SolidBrush(Color.FromArgb(210, 12, 14, 18))) g.FillPath(br, back);
                float fill = r.Width * PatchStatus.Fraction;
                if (fill > 1)
                {
                    g.SetClip(back);
                    using (var br = new SolidBrush(Color.FromArgb(150, ModernTheme.Accent))) g.FillRectangle(br, r.X, r.Y, fill, r.Height);
                    g.ResetClip();
                }
                ModernSkin.Stroke(g, back, ModernTheme.Frame);
            }
            HudKit.ShadowText(g, PatchStatus.Text, ModernSkin.Font(11, true), ModernTheme.Text, new RectangleF(S(8), 0, Size.Width - S(16), Size.Height));
        }
    }
}

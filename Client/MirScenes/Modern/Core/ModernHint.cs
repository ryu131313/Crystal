using System.Drawing.Drawing2D;
using Client.MirControls;

namespace Client.MirScenes.Modern.Core
{
    /// <summary>
    /// The kit's text tooltip (phase 4): the themed card that shows a modern control's <see cref="MirControl.Hint"/> instead of
    /// the classic yellow-on-black hint label (CMain.CreateHintLabel asks <see cref="Update"/> first and hides its label when
    /// this answers). Lives on ModernRoot.Tips; repainted only when the text or the theme changes, moved only when the mouse
    /// moved. Port of the web's title tooltips (core/tooltip.ts text tips).
    /// </summary>
    public sealed class ModernHint : ModernControl
    {
        private const int MaxWidth = 300, PadX = 8, PadY = 5;
        private static ModernHint _shared;

        private string _text = string.Empty;
        private int _themeVersion = -1;
        private float _k = -1;
        private Point _lastMouse = new Point(int.MinValue, int.MinValue);

        private ModernHint()
        {
            NotControl = true;
            Visible = false;
        }

        /// <summary>
        /// Per frame from CMain.CreateHintLabel: true when the control under the mouse belongs to the modern root (its hint, if
        /// any, is shown here; the classic label stays hidden). No allocation unless the hint text changed.
        /// </summary>
        public static bool Update(MirControl control, Point mouse)
        {
            ModernRoot root = ModernRoot.Instance;
            if (root == null || root.IsDisposed || control == null || !IsModern(control, root))
            {
                if (_shared != null && !_shared.IsDisposed && _shared.Visible) _shared.Visible = false;
                return false;
            }

            string hint = control.Hint;
            if (string.IsNullOrEmpty(hint))
            {
                if (_shared != null && !_shared.IsDisposed && _shared.Visible) _shared.Visible = false;
                return true;
            }

            if (_shared == null || _shared.IsDisposed || _shared.Parent != root.Tips)
                _shared = new ModernHint { Parent = root.Tips };
            _shared.Show(hint, mouse);
            return true;
        }

        private static bool IsModern(MirControl c, ModernRoot root)
        {
            for (MirControl p = c; p != null; p = p.Parent)
                if (p == root) return true;
            return false;
        }

        private void Show(string text, Point mouse)
        {
            if (text != _text || _themeVersion != ModernTheme.Version || _k != K)
            {
                _text = text;
                _themeVersion = ModernTheme.Version;
                _k = K;
                Font font = ModernSkin.Font(12);
                SizeF need = ModernSkin.MeasureWrapped(text, font, S(MaxWidth) - S(PadX) * 2);
                Size = new Size((int)Math.Ceiling(need.Width) + S(PadX) * 2 + 2, (int)Math.Ceiling(need.Height) + S(PadY) * 2 + 2);
                Invalidate();
                _lastMouse = new Point(int.MinValue, int.MinValue);
            }

            if (mouse != _lastMouse)
            {
                _lastMouse = mouse;
                int x = mouse.X - Size.Width / 2, y = mouse.Y + S(22);
                if (y + Size.Height >= Settings.ScreenHeight) y = mouse.Y - Size.Height - S(6);
                x = Math.Clamp(x, 0, Math.Max(0, Settings.ScreenWidth - Size.Width - 1));
                y = Math.Clamp(y, 0, Math.Max(0, Settings.ScreenHeight - Size.Height - 1));
                Point at = new Point(x, y);
                if (Location != at) Location = at;
            }
            if (!Visible) Visible = true;
        }

        protected override void Paint(Graphics g)
        {
            if (string.IsNullOrEmpty(_text)) return;
            float k = K, rad = Math.Max(2f, ModernTheme.Radius * k * 0.6f);
            RectangleF outer = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            using (GraphicsPath p = ModernSkin.RoundRect(outer, rad))
            {
                Color top = ModernSkin.Opaque(ModernTheme.Mix(ModernTheme.PanelB, Color.Black, 0.94f));
                Color bottom = ModernSkin.Opaque(ModernTheme.Mix(ModernTheme.PanelB, Color.Black, 0.75f));
                ModernSkin.FillVertical(g, p, outer, new[] { top, bottom });
                ModernSkin.Stroke(g, p, ModernTheme.Alpha(ModernTheme.Frame, 0.7f));
            }
            RectangleF text = new RectangleF(S(PadX) + 1, S(PadY) + 1, Size.Width - S(PadX) * 2 - 2, Size.Height - S(PadY) * 2 - 2);
            ModernSkin.Text(g, _text, ModernSkin.Font(12), ModernTheme.Text, text, StringAlignment.Near, StringAlignment.Near, false, true);
        }

        protected override void Dispose(bool disposing)
        {
            if (_shared == this) _shared = null;
            base.Dispose(disposing);
        }
    }
}

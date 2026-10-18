using System.Drawing.Imaging;
using Client.MirControls;
using Client.MirGraphics;

namespace Client.MirScenes.Modern.Core
{
    /// <summary>
    /// Base of every new-UI widget: a MirControl whose look is painted with GDI+ (<see cref="Paint"/>) into one cached CPU texture
    /// (the MirLabel pattern: registered in DXManager.ControlList, so device resets and DXManager.Clean free it, and it repaints
    /// on the next draw). <see cref="Invalidate"/> repaints the widget and its window's render target once, on the next frame —
    /// call it on a real change only, never per frame. Sizes and positions are device px; build them from HUD px with
    /// <see cref="S"/> (ModernRoot.S). Hover / press states repaint themselves.
    /// </summary>
    public abstract class ModernControl : MirControl
    {
        protected ModernControl()
        {
            DrawControlTexture = true;
            BackColour = Color.Transparent;
        }

        /// <summary>HUD px → device px at the current HUD scale.</summary>
        protected static int S(float hudPx) => ModernRoot.S(hudPx);

        /// <summary>The current HUD scale.</summary>
        protected static float K => ModernRoot.K;

        public bool Hovered => MouseControl == this;
        public bool Pressed { get; private set; }

        /// <summary>Repaint this widget's texture (and its window) on the next frame.</summary>
        public void Invalidate()
        {
            TextureValid = false;
            Redraw();
        }

        /// <summary>Paints the widget into a transparent bitmap of its Size (device px). Must not throw.</summary>
        protected abstract void Paint(Graphics g);

        protected override unsafe void CreateTexture()
        {
            if (Size.Width <= 0 || Size.Height <= 0) return;

            if (TextureSize != Size)
                DisposeTexture();

            if (ControlTexture == null || ControlTexture.Disposed)
            {
                DXManager.ControlList.Add(this);
                ControlTexture = DXManager.CreateTexture(Size.Width, Size.Height);
                TextureSize = Size;
            }

            byte* pixels = DXManager.LockTexture(ControlTexture);
            try
            {
                using var image = new Bitmap(Size.Width, Size.Height, Size.Width * 4, PixelFormat.Format32bppArgb, (IntPtr)pixels);
                using Graphics g = Graphics.FromImage(image);
                ModernSkin.Prepare(g);
                g.Clear(Color.Transparent);
                try
                {
                    Paint(g);
                }
                catch (Exception ex)
                {
                    if (Settings.LogErrors) ModernLog.Once("[ModernUi] paint " + GetType().Name, ex);
                }
            }
            finally
            {
                DXManager.UnlockTexture(ControlTexture);
            }
            TextureValid = true;
        }

        protected override void OnMouseEnter()
        {
            base.OnMouseEnter();
            Invalidate();
        }

        protected override void OnMouseLeave()
        {
            base.OnMouseLeave();
            Pressed = false;
            Invalidate();
        }

        public override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled) return;
            Pressed = true;
            Invalidate();
        }

        public override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!Pressed) return;
            Pressed = false;
            Invalidate();
        }

        /// <summary>The scene sends the wheel to the hovered leaf: a widget that does not use it (<see cref="OnWheel"/> false)
        /// passes it on to the nearest scroll view around it (Widgets\ScrollView), so lists scroll over their rows.</summary>
        public override void OnMouseWheel(MouseEventArgs e)
        {
            if (Enabled && OnWheel(e)) return;
            base.OnMouseWheel(e);
            Widgets.ScrollView.Bubble(this, e);
        }

        /// <summary>The wheel over this widget; true = used (sliders), false = scroll the view around it.</summary>
        protected virtual bool OnWheel(MouseEventArgs e) => false;
    }

    /// <summary>Logging for the new UI: one entry per distinct key and process (the 100-entry SaveError budget is shared).</summary>
    public static class ModernLog
    {
        private static readonly HashSet<string> _seen = new HashSet<string>();

        public static void Once(string key, Exception ex = null)
        {
            if (!Settings.LogErrors || !_seen.Add(key)) return;
            CMain.SaveError(ex == null ? key : key + ": " + ex);
        }
    }

    /// <summary>A text label: one line (ellipsis) or wrapped, any colour, size and weight.</summary>
    public class ModernLabel : ModernControl
    {
        private string _text = string.Empty;
        private Color _colour = Color.Empty;
        private float _fontSize = 12;
        private bool _bold, _wrap;
        private StringAlignment _align = StringAlignment.Near;

        public ModernLabel()
        {
            NotControl = true;
        }

        public string Text
        {
            get => _text;
            set
            {
                value ??= string.Empty;
                if (_text == value) return;
                _text = value;
                Invalidate();
            }
        }

        /// <summary>Text colour; Color.Empty = the theme's text colour.</summary>
        public Color Colour
        {
            get => _colour;
            set
            {
                if (_colour == value) return;
                _colour = value;
                Invalidate();
            }
        }

        /// <summary>Font size in HUD px.</summary>
        public float FontSize { get => _fontSize; set { if (_fontSize == value) return; _fontSize = value; Invalidate(); } }
        public bool Bold { get => _bold; set { if (_bold == value) return; _bold = value; Invalidate(); } }
        public bool Wrap { get => _wrap; set { if (_wrap == value) return; _wrap = value; Invalidate(); } }
        public StringAlignment Align { get => _align; set { if (_align == value) return; _align = value; Invalidate(); } }

        /// <summary>Sizes the label to its one-line text width (device px) at the current font.</summary>
        public void AutoWidth(int extraHudPx = 0)
        {
            Size = new Size((int)Math.Ceiling(ModernSkin.Measure(_text, ModernSkin.Font(_fontSize, _bold))) + S(extraHudPx) + 2, Size.Height);
        }

        protected override void Paint(Graphics g)
        {
            Color c = _colour.IsEmpty ? ModernTheme.Text : _colour;
            ModernSkin.Text(g, _text, ModernSkin.Font(_fontSize, _bold), c, new RectangleF(0, 0, Size.Width, Size.Height), _align,
                _wrap ? StringAlignment.Near : StringAlignment.Center, !_wrap, _wrap);
        }
    }
}

using Client.MirControls;
using Client.MirGraphics;
using Client.MirGraphics.Rendering;

namespace Client.MirScenes.Modern.Core
{
    /// <summary>
    /// A new-UI container that renders itself and its children into one render target (Desktop-Modern-UI-Plan "ModernRoot":
    /// a change inside re-renders this surface only — <see cref="Redraw"/> never climbs to the scene). ModernWindow is one (with
    /// chrome); the HUD pieces (bottom rig, chat, minimap, party / hero frames, tracker) are bare surfaces on ModernRoot.Hud.
    /// <para>
    /// Children draw relative to the surface while it renders (<see cref="DisplayLocation"/> is (0, 0) then), so their own
    /// hit-testing stays in screen pixels. Moving the surface only changes where its target is blitted.
    /// <see cref="PassThrough"/> surfaces take the mouse only over a child control, so the world stays clickable through
    /// their transparent parts. <see cref="Process"/> runs every frame while visible (ModernUi.Process): change nothing
    /// unless a value changed.
    /// </para>
    /// </summary>
    public abstract class ModernSurface : MirControl
    {
        private bool _rendering;

        protected ModernSurface()
        {
            BackColour = Color.Transparent;
            DrawControlTexture = true;
        }

        protected static int S(float hudPx) => ModernRoot.S(hudPx);

        /// <summary>True: only the children take the mouse (HUD); false: the whole rectangle does (windows).</summary>
        public bool PassThrough { get; set; }

        /// <summary>Per-frame work while visible; keep it allocation-free and change state only on a real change.</summary>
        public virtual void Process() { }

        /// <summary>While the surface renders into its own target its children draw relative to it.</summary>
        public override Point DisplayLocation => _rendering ? Point.Empty : base.DisplayLocation;

        public override void Redraw()
        {
            TextureValid = false;
        }

        public override void Draw()
        {
            if (IsDisposed || !Visible || Size.Width <= 0 || Size.Height <= 0) return;
            BeforeDrawControl();
            DrawControl();
            AfterDrawControl();
        }

        protected override void CreateTexture()
        {
            if (Size != TextureSize) DisposeTexture();
            if (ControlTexture == null || ControlTexture.Disposed)
            {
                DXManager.ControlList.Add(this);
                ControlTexture = DXManager.CreateRenderTarget(Size.Width, Size.Height);
                TextureSize = Size;
            }

            RenderTexture oldSurface = DXManager.CurrentSurface;
            DXManager.SetSurface(ControlTexture);
            // The sprite takes its projection from the target only at Begin (D3DX behaviour, kept by the D3D11 backend: golden
            // S14): without a restart, a target smaller than the screen gets every draw scaled by target / screen size.
            DXManager.RestartSpriteAlphaBlend();
            DXManager.Clear(Color.FromArgb(0, 0, 0, 0)); // premultiplied: Color.Transparent is white at alpha 0 and would add white
            bool oldOver = DXManager.AlphaOver;
            DXManager.SetAlphaOver(true);
            _rendering = true;
            try
            {
                DrawChildControls();
            }
            finally
            {
                _rendering = false;
                DXManager.Flush();
                DXManager.SetAlphaOver(oldOver);
                DXManager.SetSurface(oldSurface);
                DXManager.RestartSpriteAlphaBlend();
            }
            TextureValid = true;
        }

        /// <summary>Draws the target premultiplied (see <see cref="Composite"/>).</summary>
        protected internal override void DrawControl()
        {
            if (!TextureValid) CreateTexture();
            if (ControlTexture == null || ControlTexture.Disposed) return;
            Composite(ControlTexture, Size, DisplayLocation, Opacity);
            CleanTime = CMain.Time + Settings.CleanDelay;
        }

        /// <summary>
        /// A new-UI render target is drawn with alpha "over" (DXManager.SetAlphaOver): translucent pixels (sprite shadows, glows)
        /// over opaque ones leave it opaque, and its colour is premultiplied by its alpha, so it is composited with (One,
        /// InverseSourceAlpha) and the opacity scales all four channels. (Plain SourceAlpha blending lowered the target's alpha
        /// under every glow, so the world showed through, and darkened translucent pixels twice.)
        /// </summary>
        internal static void Composite(RenderTexture texture, Size size, Point at, float opacity)
        {
            float o = opacity >= 1 || opacity < 0 ? 1f : opacity;
            int c = (int)Math.Round(255 * o);
            DXManager.SetBlend(true);
            DXManager.SetBlendFactors(RenderBlend.One, RenderBlend.InverseSourceAlpha);
            DXManager.Draw(texture, new Rectangle(0, 0, size.Width, size.Height), new SlimDX.Vector3(at.X, at.Y, 0), Color.FromArgb(c, c, c, c));
            DXManager.SetBlend(false);
        }

        /// <summary>A move changes only where the target is blitted: no re-render (children keep their relative positions).</summary>
        protected override void OnLocationChanged()
        {
        }

        public override bool IsMouseOver(Point p)
        {
            if (!Visible || IsDisposed) return false;
            if (Moving) return true;
            if (!DisplayRectangle.Contains(p)) return false;
            if (!PassThrough) return true;
            for (int i = Controls.Count - 1; i >= 0; i--)
                if (Controls[i].IsMouseOver(p)) return true;
            return false;
        }
    }
}

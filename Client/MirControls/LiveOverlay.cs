using Client.MirGraphics;
using Client.MirGraphics.Rendering;
using SlimDX;

namespace Client.MirControls
{
    /// <summary>
    /// Renders one top-level scene child that changes often (skill-bar cooldowns, minimap dots) into its own screen-sized
    /// render target, composited right after the scene texture, so its changes re-render only itself instead of the whole
    /// UI scene texture (Settings.RebuildOptimisations).
    /// <para>
    /// Pixel parity: the overlay target is cleared to the scene's BackColour (transparent) and the target control is drawn
    /// at the same absolute coordinates and with the same state as inside the scene texture, then blitted 1:1 over its own
    /// rectangle. That is identical to drawing it inside the scene texture as long as nothing else drawn into the scene
    /// texture overlaps that rectangle: there the scene texture is fully transparent, so its blit leaves the pixels as they
    /// were. The overlay is therefore only active while nothing else overlaps the target (checked at every scene rebuild)
    /// and while the target's drawn controls stay inside its rectangle; otherwise the target is drawn in the scene texture
    /// as before. Custom drawing in BeforeDraw/AfterDraw outside the target's rectangle is not detected and would be clipped.
    /// </para>
    /// Main (UI) thread only. The render target is a registered control texture, so device resets and DXManager.Clean
    /// release it like any other.
    /// </summary>
    internal sealed class LiveOverlay : MirControl
    {
        public readonly MirControl Target;
        private readonly MirScene _scene;
        private Rectangle _rect;
        private bool _rendering;

        /// <summary>True while the target is drawn by this overlay instead of inside the scene texture.</summary>
        public bool Active { get; private set; }

        /// <summary>The scene texture skips the target while active, except when this overlay is the one drawing it.</summary>
        internal bool SkipInScene => Active && !_rendering;

        public LiveOverlay(MirControl target, MirScene scene)
        {
            Target = target;
            _scene = scene;
            target.LiveOverlay = this;
            DrawControlTexture = true;
            BackColour = scene.BackColour;
        }

        /// <summary>A change inside the target while active: re-render the overlay only.</summary>
        internal void Invalidate()
        {
            FrameProfiler.NoteInvalidation(RebuildTarget.Overlay, TextureValid);
            TextureValid = false;
        }

        /// <summary>Called by the scene at each rebuild of its texture, before its children are drawn.</summary>
        internal void UpdateEligibility()
        {
            bool eligible = Settings.RebuildOptimisations && !IsDisposed && Target.Parent == _scene && IsDrawn(Target);
            Rectangle rect = Target.DisplayRectangle;

            if (eligible && !ContainedIn(Target, rect)) eligible = false;

            if (eligible)
            {
                List<MirControl> siblings = _scene.Controls;
                for (int i = 0; i < siblings.Count; i++)
                {
                    MirControl sibling = siblings[i];
                    if (sibling == null || sibling == Target || !sibling.DrawsInParentTexture) continue;
                    if (Overlaps(sibling, rect))
                    {
                        eligible = false;
                        break;
                    }
                }
            }

            if (eligible == Active && (!eligible || rect == _rect)) return;

            Active = eligible;
            _rect = rect;
            TextureValid = false;
        }

        /// <summary>
        /// Called every frame before the scene texture is drawn. Falls back to the scene texture when the target moved, was
        /// hidden or disposed, or grew controls outside its rectangle; otherwise re-renders the overlay if it is invalid.
        /// </summary>
        internal void Prepare()
        {
            if (!Active) return;

            if (!Settings.RebuildOptimisations || Target.IsDisposed || Target.Parent != _scene || !IsDrawn(Target) || Target.DisplayRectangle != _rect)
            {
                Deactivate();
                return;
            }

            if (!TextureValid) Render();
        }

        /// <summary>Called every frame right after the scene texture is drawn.</summary>
        internal void Composite(float opacity)
        {
            if (!Active) return;
            if (!TextureValid) Render();
            if (!Active || ControlTexture == null || ControlTexture.Disposed) return;

            Rectangle source = Rectangle.Intersect(_rect, new Rectangle(0, 0, TextureSize.Width, TextureSize.Height));
            if (source.Width <= 0 || source.Height <= 0) return;

            DXManager.DrawOpaque(ControlTexture, source, new Vector3?(new Vector3(source.X, source.Y, 0.0f)), Color.White, opacity);
            CleanTime = CMain.Time + Settings.CleanDelay;
        }

        private void Render()
        {
            long rebuildStart = FrameProfiler.Now();
            int rebuildDraws = CMain.DPSCounter;

            Size size = _scene.Size;
            if (size != TextureSize) DisposeTexture();

            if (ControlTexture == null || ControlTexture.Disposed)
            {
                DXManager.ControlList.Add(this);
                ControlTexture = DXManager.CreateRenderTarget(size.Width, size.Height);
                TextureSize = size;
            }

            RenderTexture oldSurface = DXManager.CurrentSurface;
            DXManager.SetSurface(ControlTexture);
            DXManager.Clear(BackColour);

            _rendering = true;
            try
            {
                Target.Draw();
            }
            finally
            {
                _rendering = false;
            }

            DXManager.Flush();
            DXManager.SetSurface(oldSurface);
            TextureValid = true;

            FrameProfiler.AddRebuild(RebuildTarget.Overlay, rebuildStart, rebuildDraws);

            // BeforeDraw handlers may add or move child controls (minimap quest icons); anything outside the rectangle would
            // be clipped, so hand the target back to the scene texture.
            if (!ContainedIn(Target, _rect)) Deactivate();
        }

        private void Deactivate()
        {
            Active = false;
            _scene.TextureValid = false;
        }

        /// <summary>Same visibility rules as <see cref="MirControl.Draw"/>.</summary>
        private static bool IsDrawn(MirControl control)
        {
            return !control.IsDisposed && control.Visible && control.Size.Width <= Settings.ScreenWidth && control.Size.Height <= Settings.ScreenHeight;
        }

        private static bool ContainedIn(MirControl control, Rectangle rect)
        {
            List<MirControl> children = control.Controls;
            for (int i = 0; i < children.Count; i++)
            {
                MirControl child = children[i];
                if (child == null || !IsDrawn(child)) continue;
                Rectangle r = child.DisplayRectangle;
                if (r.Width > 0 && r.Height > 0 && !rect.Contains(r)) return false;
                if (!ContainedIn(child, rect)) return false;
            }
            return true;
        }

        private static bool Overlaps(MirControl control, Rectangle rect)
        {
            if (!IsDrawn(control)) return false;
            if (control.DisplayRectangle.IntersectsWith(rect)) return true;

            List<MirControl> children = control.Controls;
            for (int i = 0; i < children.Count; i++)
                if (children[i] != null && Overlaps(children[i], rect)) return true;

            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Target.LiveOverlay == this) Target.LiveOverlay = null;
            base.Dispose(disposing);
        }
    }
}

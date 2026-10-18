using Client.MirControls;

namespace Client.MirScenes.Modern.Core
{
    /// <summary>
    /// The new UI's root (Desktop-Modern-UI-Plan "ModernRoot"). A child of the scene placed on top of its other children, so the
    /// scene's normal mouse routing reaches it first, but it draws nothing into the scene texture
    /// (<see cref="DrawsInParentTexture"/> = false): <see cref="DrawOverlay"/> draws its layers after the scene texture, from
    /// GameScene.DrawControl through IGameUi.DrawOverlay. Each window is its own render target, so a change inside a window
    /// re-renders that window only (never the scene texture: <see cref="Redraw"/> stops here).
    /// <para>
    /// HUD scale is applied at layout time, not by transforming textures: windows are laid out in HUD px times <see cref="K"/>,
    /// so text and shapes are rasterised at their final size (sharp at any scale), and hit-testing, MirTextBox caret placement
    /// and CMain.MPoint stay in plain device pixels. Changing the scale rebuilds the open windows (WindowManager.Rescale).
    /// </para>
    /// Layers in z order (core/layers.ts): hud, windows, tips, menu, popups, drag — each a child container in that order.
    /// </summary>
    public sealed class ModernRoot : MirControl
    {
        /// <summary>The HUD scale: Settings.UiScale x (UiAutoScale ? screen height / 768 : 1), kept in 0.25-4 (layers.ts hudScale).</summary>
        public static float K { get; private set; } = 1f;

        public const int AutoScaleHeight = 768;

        /// <summary>HUD px → device px.</summary>
        public static int S(float hudPx) => (int)Math.Round(hudPx * K);

        /// <summary>The live root of the current game, or null.</summary>
        public static ModernRoot Instance { get; private set; }

        /// <summary>The bottom HUD and the other always-on pieces (chat, minimap, frames, tracker): below the windows.</summary>
        public readonly ModernLayer Hud;
        public readonly ModernLayer Windows, Tips, Menu, Drag;

        /// <summary>The popups layer and service (veil + centred card, request toasts): Popups.cs.</summary>
        public readonly Popups Popups;
        public readonly WindowManager Wm;

        private bool _overlayPass;

        public ModernRoot(MirScene scene)
        {
            Instance = this;
            RecomputeScale();
            BackColour = Color.Transparent;
            Size = new Size(Settings.ScreenWidth, Settings.ScreenHeight);
            Parent = scene;

            Hud = new ModernLayer { Parent = this };
            Windows = new ModernLayer { Parent = this };
            Tips = new ModernLayer { Parent = this };
            Menu = new ModernLayer { Parent = this };
            Popups = new Popups { Parent = this };
            Drag = new ModernLayer { Parent = this };

            Wm = new WindowManager(this);
            ModernTheme.Changed += OnThemeChanged;
        }

        /// <summary>Recomputes <see cref="K"/> from Settings; returns true when it changed.</summary>
        public static bool RecomputeScale()
        {
            float s = (Settings.UiScale > 0 ? Settings.UiScale : 1f) * (Settings.UiAutoScale ? Settings.ScreenHeight / (float)AutoScaleHeight : 1f);
            s = Math.Clamp(s, 0.25f, 4f);
            if (Math.Abs(s - K) < 0.001f) return false;
            K = s;
            return true;
        }

        /// <summary>The scene changed size (resolution) or the scale settings changed: resize and relayout.</summary>
        public void ScreenChanged()
        {
            Size = new Size(Settings.ScreenWidth, Settings.ScreenHeight);
            for (int i = 0; i < Controls.Count; i++) Controls[i].Size = Size;
            bool rescaled = RecomputeScale();
            Wm.Rescale(rescaled);
            RaiseRelayout(rescaled);
        }

        /// <summary>
        /// The HUD pieces (not managed windows) must rebuild: raised after a HUD scale change (true) or a screen size / theme
        /// change (false = re-place; theme changes pass true too, since chrome and widgets cache their look).
        /// </summary>
        public event Action<bool> Relayout;

        private int _themeVersion = ModernTheme.Version;

        private void RaiseRelayout(bool rebuild)
        {
            Action<bool> relayout = Relayout;
            if (relayout == null) return;
            foreach (Delegate d in relayout.GetInvocationList()) // one failing piece must not skip the others
            {
                try
                {
                    ((Action<bool>)d)(rebuild);
                }
                catch (Exception ex)
                {
                    ModernLog.Once("[ModernUi] HUD relayout " + d.Method.DeclaringType?.Name, ex);
                }
            }
        }

        private void OnThemeChanged()
        {
            if (_themeVersion == ModernTheme.Version) return;
            _themeVersion = ModernTheme.Version;
            RaiseRelayout(true);
        }

        /// <summary>Per-frame work outside the windows (ModernUi.Process): the widgets' timers (Widgets.ModernTick: popup hold /
        /// repeat buttons, request toast bars, search debounce). Cheap when nothing is ticking.</summary>
        public void Process()
        {
            Widgets.ModernTick.Run();
            List<MirControl> hud = Hud.Controls;
            for (int i = 0; i < hud.Count; i++)
                if (hud[i] is ModernSurface s && s.Visible && !s.IsDisposed) s.Process();
        }

        /// <summary>Keeps the root above every other scene child (a later scene child would otherwise take the mouse first).</summary>
        /// Never above a modal scene child: classic prompts (MirMessageBox / MirInputBox / MirAmountBox) that shared GameScene code
        /// still raises must stay on top and keep the mouse.
        public void KeepOnTop()
        {
            if (Parent == null) return;
            List<MirControl> siblings = Parent.Controls;
            if (siblings.Count == 0 || siblings[^1] == this) return;
            for (int i = siblings.Count - 1; i >= 0; i--)
            {
                MirControl c = siblings[i];
                if (c == this) break;
                if (c != null && !c.IsDisposed && c.Visible && c.Modal) return;
            }
            BringToFront();
        }

        /// <summary>A visible modal scene child (a classic prompt) is up: the root's keys must not act behind it.</summary>
        public bool ModalPromptOpen
        {
            get
            {
                if (Parent == null) return false;
                List<MirControl> siblings = Parent.Controls;
                for (int i = siblings.Count - 1; i >= 0; i--)
                {
                    MirControl c = siblings[i];
                    if (c != null && c != this && !c.IsDisposed && c.Visible && c.Modal) return true;
                }
                return false;
            }
        }

        internal override bool DrawsInParentTexture => false;

        public override void Draw()
        {
            if (!_overlayPass) return; // inside the scene texture: drawn later by DrawOverlay
            base.Draw();
        }

        /// <summary>Draws every layer after the scene texture (each window blits its own render target).</summary>
        public void DrawOverlay()
        {
            if (IsDisposed || !Visible) return;
            _overlayPass = true;
            try
            {
                DrawChildControls();
            }
            finally
            {
                _overlayPass = false;
            }
        }

        /// <summary>Changes inside the root never invalidate the scene texture: windows re-render themselves.</summary>
        public override void Redraw()
        {
        }

        /// <summary>The root takes the mouse only where one of its windows / popups is.</summary>
        public override bool IsMouseOver(Point p)
        {
            if (!Visible || IsDisposed) return false;
            for (int i = Controls.Count - 1; i >= 0; i--)
                if (Controls[i].IsMouseOver(p)) return true;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    Popups.CloseAll();
                }
                catch (Exception ex)
                {
                    ModernLog.Once("[ModernUi] closing popups on dispose", ex);
                }
                finally
                {
                    ModernTheme.Changed -= OnThemeChanged;
                    Relayout = null;
                    Wm.Dispose();
                    ModernTooltip.Forget();
                    Widgets.ModernTick.Clear();
                    if (Instance == this) Instance = null;
                }
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>One z layer of the root: a transparent, screen-sized container that takes the mouse only over its children.</summary>
    public sealed class ModernLayer : MirControl
    {
        public ModernLayer()
        {
            BackColour = Color.Transparent;
            Size = new Size(Settings.ScreenWidth, Settings.ScreenHeight);
        }

        public override void Redraw()
        {
        }

        public override bool IsMouseOver(Point p)
        {
            if (!Visible || IsDisposed) return false;
            for (int i = Controls.Count - 1; i >= 0; i--)
                if (Controls[i].IsMouseOver(p)) return true;
            return false;
        }
    }
}

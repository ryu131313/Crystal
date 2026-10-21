using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirScenes.Modern.Core;
using S = ServerPackets;

namespace Client.MirScenes.Modern.Screens
{
    /// <summary>
    /// Builds the login / select scene of the interface in use (Settings.UiStyle, read at every scene change, so a switch takes
    /// effect at the next screen). Port of WebClient/src/ui/screens.ts's style choice.
    /// </summary>
    public static class AccountScenes
    {
        public static MirScene Login() => UiStyles.IsModern ? new ModernLoginScene(false) : new LoginScene();

        public static MirScene Select(List<SelectInfo> characters) =>
            UiStyles.IsModern ? new ModernSelectScene(characters) : new SelectScene(characters);

        /// <summary>A message on the login scene just made active (S.ReturnToLogin): a modern popup, else the classic box.</summary>
        public static void Notice(string text)
        {
            if (MirScene.ActiveScene is ModernAccountScene modern) modern.ShowNotice(text);
            else MirMessageBox.Show(text);
        }
    }

    /// <summary>
    /// What the server sends around the login that the select screen shows (WebClient accountDetails.ts / serverRules.ts): it
    /// arrives right after S.LoginSuccess, while the login scene still plays the door, so it is kept here across scenes.
    /// UI thread only.
    /// </summary>
    public static class AccountState
    {
        /// <summary>S.SelectDetails (guild, map title, play time per character, account gold); null until it arrives.</summary>
        public static S.SelectDetails Details;
        /// <summary>S.ServerRules (AllowCreateAssassin / AllowCreateArcher); null until it arrives.</summary>
        public static S.ServerRules Rules;

        /// <summary>Keeps the packets the account screens use; true = it was one of them.</summary>
        public static bool Take(Packet p)
        {
            switch (p)
            {
                case S.SelectDetails d:
                    Details = d;
                    return true;
                case S.ServerRules r:
                    Rules = r;
                    return true;
            }
            return false;
        }

        public static ClientSelectDetail Detail(int characterIndex)
        {
            List<ClientSelectDetail> list = Details?.Characters;
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
                if (list[i] != null && list[i].Index == characterIndex) return list[i];
            return null;
        }
    }

    /// <summary>
    /// Base of the new UI's account scenes (port of screens/accountRoot.ts): a scene that owns one <see cref="ModernRoot"/>,
    /// drawn after the scene texture, with the saved theme. Scene art (backdrop, door, preview) stays plain scene children; the
    /// cards are surfaces on the root's HUD layer. Keys: a popup first, then the screen's own keys (never while a text field
    /// has the keyboard: the form previews every key).
    /// </summary>
    public abstract class ModernAccountScene : MirScene
    {
        protected readonly ModernRoot Root;

        protected ModernAccountScene()
        {
            ModernTheme.ApplySettings();
            BackColour = Color.Black;
            Root = new ModernRoot(this);
        }

        protected static int S(float hudPx) => ModernRoot.S(hudPx);

        protected static string T(ClientTextKeys key) => GameLanguage.ClientTextMap.GetLocalization(key);

        protected static string T(ClientTextKeys key, params object[] args) => GameLanguage.ClientTextMap.GetLocalization(key, args);

        /// <summary>A text box has the keyboard (the form's key preview still sends its keys to the scene).</summary>
        protected static bool TypingInField => Program.Form?.ActiveControl is TextBox;

        protected internal override void DrawControl()
        {
            base.DrawControl();
            Root?.DrawOverlay();
        }

        public sealed override void Process()
        {
            if (Root != null && !Root.IsDisposed)
            {
                Root.KeepOnTop();
                Root.Process();
                Root.Wm.Process();
            }
            Tick();
        }

        /// <summary>Per-frame work of the screen: change nothing unless a value changed.</summary>
        protected abstract void Tick();

        /// <summary>The screen's keys (no popup open, no field focused); true = consumed.</summary>
        protected virtual bool ScreenKey(KeyEventArgs e) => false;

        public override void OnKeyDown(KeyEventArgs e)
        {
            if (Root != null && !Root.IsDisposed && Root.Wm.HandleKey(e))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            if (!TypingInField && ScreenKey(e))
            {
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        public void ShowNotice(string text)
        {
            if (Root == null || Root.IsDisposed || string.IsNullOrEmpty(text)) return;
            Root.Popups.Confirm(text, PopupButtons.Ok, PopupKind.Info, (a, _) => { });
        }

        /// <summary>A message box on this screen.</summary>
        protected void Say(string text, PopupKind kind = PopupKind.Info, Action closed = null)
        {
            if (Root == null || Root.IsDisposed) return;
            Root.Popups.Confirm(text, PopupButtons.Ok, kind, (a, _) => closed?.Invoke());
        }
    }

    /// <summary>A glass card of an account screen (accountRoot.ts .glass): its own render target, so a change inside repaints the card only.</summary>
    internal sealed class AccountPanel : ModernSurface
    {
        private readonly AccountGlass _glass;

        public AccountPanel(MirControl parent, int xHud, int yHud, int widthHud, int heightHud)
        {
            Parent = parent;
            Location = new Point(S(xHud), S(yHud));
            Size = new Size(S(widthHud), S(heightHud));
            _glass = new AccountGlass { Parent = this, Size = Size };
        }

        /// <summary>Removes the content (keeps the glass) before a view is rebuilt.</summary>
        public void Clear()
        {
            for (int i = Controls.Count - 1; i >= 0; i--)
                if (Controls[i] != _glass) Controls[i].Dispose();
        }

        public void SetHeight(int heightHud)
        {
            Size size = new Size(Size.Width, S(heightHud));
            if (size == Size) return;
            Size = size;
            _glass.Size = size;
            _glass.Invalidate();
        }
    }

    internal sealed class AccountGlass : ModernControl
    {
        public AccountGlass()
        {
            NotControl = true;
        }

        protected override void Paint(Graphics g)
        {
            float k = K, rad = ModernTheme.Radius * 1.4f * k;
            RectangleF outer = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            RectangleF inner = RectangleF.Inflate(outer, -1f, -1f);
            using (GraphicsPath ring = ModernSkin.RoundRect(outer, rad + 1))
                ModernSkin.Stroke(g, ring, Color.FromArgb(160, 0, 0, 0));
            using GraphicsPath path = ModernSkin.RoundRect(inner, rad);
            using (var b = new SolidBrush(ModernTheme.Alpha(ModernTheme.PanelB, 0.9f)))
                g.FillPath(b, path);
            using (var pen = new Pen(ModernTheme.Alpha(ModernTheme.FrameLight, 0.12f), 1f))
                g.DrawLine(pen, inner.X + rad, inner.Y + 1, inner.Right - rad, inner.Y + 1);
            ModernSkin.Stroke(g, path, ModernTheme.Alpha(ModernTheme.Frame, 0.8f));
        }
    }

    /// <summary>A text link (the screens' &lt;a&gt;): the accent colour on hover.</summary>
    internal sealed class AccountLink : ModernControl
    {
        private string _text;
        private readonly float _fontHud;
        private readonly StringAlignment _align;

        public AccountLink(string text, float fontHud = 12, StringAlignment align = StringAlignment.Near)
        {
            _text = text ?? string.Empty;
            _fontHud = fontHud;
            _align = align;
            Sound = MirSounds.SoundList.ButtonA;
            Size = new Size((int)Math.Ceiling(ModernSkin.Measure(_text, ModernSkin.Font(_fontHud))) + S(4), S(_fontHud + 8));
        }

        public Color Colour { get; set; } = Color.Empty;

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

        protected override void Paint(Graphics g)
        {
            Color c = Hovered ? ModernTheme.Accent : Colour.IsEmpty ? ModernTheme.Text : Colour;
            ModernSkin.Text(g, _text, ModernSkin.Font(_fontHud), c, new RectangleF(0, 0, Size.Width, Size.Height), _align);
        }
    }

    /// <summary>A full-screen shade over the backdrop (the screens' .shade gradients), painted once.</summary>
    internal sealed class AccountShade : ModernControl
    {
        private readonly bool _centre;

        public AccountShade(bool centreLight)
        {
            _centre = centreLight;
            NotControl = true;
            Size = new Size(Settings.ScreenWidth, Settings.ScreenHeight);
        }

        protected override void Paint(Graphics g)
        {
            RectangleF r = new RectangleF(0, 0, Size.Width, Size.Height);
            using (var lr = new LinearGradientBrush(new RectangleF(-1, 0, Size.Width + 2, Size.Height), Color.Black, Color.Black, LinearGradientMode.Horizontal))
            {
                lr.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb(150, 0, 0, 0), Color.FromArgb(_centre ? 50 : 0, 0, 0, 0), Color.FromArgb(_centre ? 50 : 0, 0, 0, 0), Color.FromArgb(160, 0, 0, 0) },
                    Positions = new[] { 0f, 0.35f, 0.65f, 1f },
                };
                g.FillRectangle(lr, r);
            }
            using var tb = new LinearGradientBrush(new RectangleF(0, -1, Size.Width, Size.Height + 2), Color.Black, Color.Black, LinearGradientMode.Vertical);
            tb.InterpolationColors = new ColorBlend
            {
                Colors = new[] { Color.FromArgb(150, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(170, 0, 0, 0) },
                Positions = new[] { 0f, 0.18f, 0.7f, 1f },
            };
            g.FillRectangle(tb, r);
        }
    }
}

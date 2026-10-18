// Ported from WebClient/src/ui/modern/hud/chat.ts ticker() (.cnotice, the desktop's ChatNoticeDialog): an announcement
// (ChatType.Announcement) scrolls once across a dark band at the top centre, "NOTICE" in the accent colour before it, the
// band's ends faded (mask-image). Hidden when the text has passed. Its own surface on the HUD layer; the text moves in
// Process only while it scrolls (one int position per frame).
using System.Drawing.Drawing2D;
using Client.MirScenes.Modern.Core;

namespace Client.MirScenes.Modern.Hud
{
    internal sealed class ChatNotice : ModernSurface
    {
        private NoticeBand _band;
        private NoticeText _text;
        private ChatAlphaMask _mask;
        private string _message = string.Empty;
        private long _start, _duration;

        public ChatNotice()
        {
            PassThrough = true;
            Visible = false;
            Parent = ModernRoot.Instance?.Hud;
            Build();
        }

        private void Build()
        {
            for (int i = Controls.Count - 1; i >= 0; i--) Controls[i].Dispose();
            int w = Math.Min(S(560), (int)(Settings.ScreenWidth * 0.9f));
            Size = new Size(w, S(26));
            _band = new NoticeBand { Parent = this, Location = Point.Empty, Size = Size };
            _text = new NoticeText { Parent = this, Location = new Point(Size.Width, S(4)) };
            _mask = new ChatAlphaMask(ChatAlphaMask.HorizontalFade) { Parent = this, Location = Point.Empty, Size = Size };
            if (_message.Length > 0) _text.SetText(_message);
            Place();
        }

        public void Place()
        {
            if (IsDisposed) return;
            int w = Math.Min(S(560), (int)(Settings.ScreenWidth * 0.9f));
            if (w != Size.Width || S(26) != Size.Height)
            {
                Build();
                return;
            }
            Location = new Point((Settings.ScreenWidth - Size.Width) / 2, S(10));
        }

        /// <summary>Starts the ticker with an announcement (over any one still running).</summary>
        public void Show(string message)
        {
            if (IsDisposed) return;
            _message = message ?? string.Empty;
            _text.SetText(_message);
            _start = CMain.Time;
            _duration = (long)((6 + _message.Length * 0.12) * 1000);
            _text.Location = new Point(Size.Width, S(4));
            Visible = true;
            BringToFront();
        }

        public override void Process()
        {
            if (_duration <= 0) return;
            float t = Math.Clamp((CMain.Time - _start) / (float)_duration, 0f, 1f);
            // translateX(from: the band width) → translateX(-100%)
            int x = (int)Math.Round(Size.Width + (-_text.Size.Width - Size.Width) * t);
            if (_text.Location.X != x) _text.Location = new Point(x, _text.Location.Y);
            if (t >= 1f)
            {
                _duration = 0;
                Visible = false;
            }
        }

        /// <summary>The band: transparent → --pb 85 % → transparent, round ends (.cnotice background).</summary>
        private sealed class NoticeBand : ModernControl
        {
            public NoticeBand() { NotControl = true; }

            protected override void Paint(Graphics g)
            {
                if (Size.Width <= 0) return;
                Color pb = ModernTheme.Alpha(ModernSkin.Opaque(ModernTheme.PanelB), 0.85f);
                using var brush = new LinearGradientBrush(new RectangleF(-1, 0, Size.Width + 2, Size.Height), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal);
                brush.InterpolationColors = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb(0, pb), pb, pb, Color.FromArgb(0, pb) },
                    Positions = new[] { 0f, 0.15f, 0.85f, 1f },
                };
                using GraphicsPath p = ModernSkin.RoundRect(new RectangleF(0, 0, Size.Width, Size.Height), Size.Height / 2f);
                g.FillPath(brush, p);
            }
        }

        /// <summary>"NOTICE" (accent) + the message (white, glowing; .cnotice span).</summary>
        private sealed class NoticeText : ModernControl
        {
            private string _text = string.Empty;

            public NoticeText() { NotControl = true; }

            public void SetText(string text)
            {
                _text = text ?? string.Empty;
                Font f = ModernSkin.Font(13, true);
                string head = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernChatNotice);
                int w = (int)Math.Ceiling(ChatGfx.Measure(head, f) + S(8) + ChatGfx.Measure(_text, f)) + S(6);
                Size = new Size(Math.Min(w, Settings.ScreenWidth), S(18)); // MirControl.Draw skips controls wider than the screen
                Invalidate();
            }

            protected override void Paint(Graphics g)
            {
                Font f = ModernSkin.Font(13, true);
                string head = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernChatNotice);
                float y = (Size.Height - f.Height) / 2f;
                ChatGfx.ShadowText(g, head, f, ModernTheme.Accent, S(2), y);
                ChatGfx.ShadowText(g, _text, f, Color.White, S(2) + ChatGfx.Measure(head, f) + S(8), y);
            }
        }
    }
}

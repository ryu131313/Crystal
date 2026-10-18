// Ported from WebClient/src/ui/modern/hud/extras.ts (NoticeWindow): S.UpdateNotice as a "notice" window (classic NoticeDialog:
// the message split on CRLF, {text/Colour} and (text/link) markup, an empty message ignored, the title replaced only when set).
// A link opens in the default browser after a confirm, http / https only (NoticeDialog opens http:// links).
using System.Diagnostics;
using System.Text.RegularExpressions;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using Client.MirScenes.Modern.Windows;

namespace Client.MirScenes.Modern.Hud
{
    public sealed class NoticeWindow : ModernWindow
    {
        public const string WindowKey = "notice";
        private const int LineH = 19, BodyH = 420;
        private static readonly Regex Markup = new Regex(@"\{((.*?)\/(.*?))\}|\(((.*?)\/(.*?))\)", RegexOptions.Compiled);

        private sealed class Seg
        {
            public string Text;
            public Color Colour;
            public string Link;
            public float X, W; // device px, set at paint time (link hit-testing)
        }

        private readonly List<List<Seg>> _lines = new List<List<Seg>>();
        private ScrollView _body;
        private bool _narrow;

        public NoticeWindow() : base(WindowKey, W.T(ClientTextKeys.ModernGuildNoticeTitle), CentreX(), 90)
        {
        }

        /// <summary>Centred on the screen at first (the web's window has no x).</summary>
        private static int CentreX() => Math.Max(0, (int)((Settings.ScreenWidth / ModernRoot.K - (Settings.ScreenWidth < ModernRoot.S(1040) ? 360 : 420)) / 2));

        internal int LineCount => _lines.Count;
        internal string LineText(int i)
        {
            if (i < 0 || i >= _lines.Count) return string.Empty;
            var sb = new System.Text.StringBuilder();
            foreach (Seg s in _lines[i]) sb.Append(s.Text);
            return sb.ToString();
        }

        protected override void Build()
        {
            _narrow = Settings.ScreenWidth < S(1040);
            int w = _narrow ? 360 : 420, bh = Math.Min(BodyH, Math.Max(LineH * 3, _lines.Count * LineH + 8)); // .nb max-height 420
            ContentWidth = w;
            ContentHeight = bh + 22;
            _body = new ScrollView(w - 8, bh) { Parent = this, Location = new Point(S(4), S(TitleHeight + 10)), Background = W.Page };
            _body.Framed = false;
            Fill();
        }

        /// <summary>NoticeDialog.Update: an empty message is ignored; the title only replaces the old one when set.</summary>
        public void Update(string title, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (!string.IsNullOrEmpty(title)) SetTitle(title);
            _lines.Clear();
            foreach (string line in message.Split(new[] { "\r\n" }, StringSplitOptions.None)) _lines.Add(Parse(line));
            Layout(); // the height follows the text (up to the scroll limit)
            Open();
        }

        /// <summary>NoticeDialog.NewText: {text/Colour} and (text/link) become inline segments.</summary>
        private static List<Seg> Parse(string line)
        {
            var segs = new List<Seg>();
            int last = 0;
            foreach (Match m in Markup.Matches(line ?? string.Empty))
            {
                if (m.Index > last) segs.Add(new Seg { Text = line.Substring(last, m.Index - last), Colour = Color.Empty });
                if (m.Groups[1].Success)
                {
                    Color c = Color.FromName(m.Groups[3].Value);
                    segs.Add(new Seg { Text = m.Groups[2].Value, Colour = c.IsKnownColor ? c : Color.Empty });
                }
                else
                {
                    string link = m.Groups[6].Value;
                    bool web = link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                    segs.Add(new Seg { Text = m.Groups[5].Value, Colour = ModernTheme.Gold, Link = web ? link : null });
                }
                last = m.Index + m.Length;
            }
            if (line != null && last < line.Length) segs.Add(new Seg { Text = line.Substring(last), Colour = Color.Empty });
            return segs;
        }

        private void Fill()
        {
            if (_body == null || _body.IsDisposed) return;
            for (int i = _body.Content.Controls.Count - 1; i >= 0; i--) _body.Content.Controls[i].Dispose();
            int w = _body.InnerWidth;
            for (int i = 0; i < _lines.Count; i++)
            {
                List<Seg> segs = _lines[i];
                bool links = false;
                foreach (Seg s in segs) if (s.Link != null) links = true;
                var row = new PaintBox((g, b) => PaintLine(g, b, segs)) { Parent = _body.Content, Location = new Point(0, S(i * LineH)), Size = new Size(w, S(LineH)), NotControl = !links };
                if (links) row.Click += (o, e) => LineClicked(row, segs);
            }
            _body.FitContent(6);
            _body.ScrollTo(0);
        }

        private static void PaintLine(Graphics g, PaintBox b, List<Seg> segs)
        {
            float k = ModernRoot.K, x = 10 * k;
            Font f = ModernSkin.Font(12);
            for (int i = 0; i < segs.Count; i++)
            {
                Seg s = segs[i];
                float sw = ModernSkin.Measure(s.Text, f);
                s.X = x;
                s.W = sw;
                Color c = s.Colour.IsEmpty ? ModernTheme.Text : s.Colour;
                ModernSkin.Text(g, s.Text, f, c, new RectangleF(x, 0, Math.Max(1, b.Size.Width - x), b.Size.Height), StringAlignment.Near, StringAlignment.Center, true);
                if (s.Link != null)
                    using (var pen = new Pen(c, 1f)) g.DrawLine(pen, x, b.Size.Height - 3 * k, x + sw, b.Size.Height - 3 * k);
                x += sw;
            }
        }

        private static void LineClicked(PaintBox row, List<Seg> segs)
        {
            float x = CMain.MPoint.X - row.DisplayLocation.X;
            foreach (Seg s in segs)
            {
                if (s.Link == null || x < s.X || x > s.X + s.W) continue;
                string link = s.Link;
                ModernRoot.Instance?.Popups.Ask(W.T(ClientTextKeys.ModernGuildOpenLink, link), yes =>
                {
                    if (!yes) return;
                    try
                    {
                        System.Diagnostics.Process.Start(new ProcessStartInfo { FileName = link, UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        ModernLog.Once("[ModernUi] notice link", ex);
                    }
                }, false, PopupKind.Question, W.T(ClientTextKeys.ModernGuildOpenLinkTitle));
                return;
            }
        }
    }
}

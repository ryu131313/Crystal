// Ported from WebClient/src/ui/modern/hud/chat.ts: the chat log (.log / .ln: lineEl, rich, the fade, the top mask, scrolling
// and the follow) and the tab strip (.tstrip / .ct: renderTabs, renderBadge). One painted control each: the log paints only
// the lines in view, on a change (a new line, a scroll, a hover, a fade step), never per frame.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirSounds;
using Client.MirScenes.Modern.Core;

namespace Client.MirScenes.Modern.Hud
{
    internal sealed class ChatLogView : ModernControl
    {
        private readonly ChatHud _hud;
        private int _scroll, _contentH, _layoutKey = int.MinValue;
        private long _nextFade = long.MaxValue;
        private ChatHud.Line _hoverLine;
        private bool _tipShown;

        public ChatLogView(ChatHud hud)
        {
            _hud = hud;
        }

        private int PadTop => S(3);
        private int PadBottom => S(4);
        private int PadX => S(8);
        private int RowH => Math.Max(1, (int)Math.Round(ChatHud.Opts.FontSize * 1.35f * K));
        private int MaxScroll => Math.Max(0, _contentH + PadTop + PadBottom - Size.Height);

        /// <summary>The log fills the box between the top bar and the input row / hint line (chat.ts flex: 1).</summary>
        public void SizeToMode()
        {
            int h = Math.Max(S(20), _hud.Size.Height - S(23) - (_hud.IsTyping ? S(32) : S(17)));
            Size want = new Size(_hud.Size.Width, h);
            if (Size == want) return;
            Size = want;
            Clamp();
        }

        private void Clamp()
        {
            int max = MaxScroll;
            if (_hud.Stick || _scroll > max) _scroll = max;
            if (_scroll < 0) _scroll = 0;
        }

        // ------------------------------------------------------------------ layout

        private int Key()
        {
            int k = Size.Width;
            k = k * 31 + ChatHud.Opts.FontSize;
            k = k * 31 + (ChatHud.Opts.Time ? 1 : 0) + (ChatHud.Opts.Tags ? 2 : 0);
            k = k * 31 + (int)(K * 100);
            return k;
        }

        /// <summary>Re-measures every shown line (width / options / tab change) and keeps a following log at its end.</summary>
        public void Relayout()
        {
            SizeToMode();
            LayoutAll();
            Invalidate();
        }

        /// <summary>Measures the lines whose layout is stale (no resize, no invalidate: safe inside Paint).</summary>
        private void LayoutAll()
        {
            _layoutKey = Key();
            List<ChatHud.Line> shown = _hud.Shown;
            int total = 0;
            for (int i = 0; i < shown.Count; i++)
            {
                if (shown[i].LayoutKey != _layoutKey) Layout(shown[i]);
                total += shown[i].Height;
            }
            _contentH = total;
            Clamp();
        }

        /// <summary>A line was added to the shown list (the oldest may have dropped off the front).</summary>
        public void Appended(ChatHud.Line l)
        {
            if (Key() != _layoutKey)
            {
                Relayout();
                return;
            }
            int before = MaxScroll;
            bool atEnd = _scroll >= before;
            Layout(l);
            int total = 0;
            List<ChatHud.Line> shown = _hud.Shown;
            for (int i = 0; i < shown.Count; i++) total += shown[i].Height;
            _contentH = total;
            if (_hud.Stick || atEnd) _scroll = MaxScroll;
            Clamp();
            Invalidate();
        }

        public void LineChanged(ChatHud.Line l)
        {
            l.LayoutKey = int.MinValue;
            Relayout();
        }

        private static string Fmt(DateTime t) => t.ToString("HH:mm");

        /// <summary>Lays a line out into runs that wrap at the log width (chat.ts lineEl + rich; overflow-wrap: anywhere).</summary>
        private void Layout(ChatHud.Line l)
        {
            l.LayoutKey = _layoutKey;
            l.Runs.Clear();
            float width = Math.Max(S(40), Size.Width - PadX * 2);
            int fs = ChatHud.Opts.FontSize, rowH = RowH;
            bool sys = l.From.Length == 0 && l.Whisper == 0;
            Font body = sys ? ChatGfx.Italic(fs) : ModernSkin.Font(fs), bold = ModernSkin.Font(fs, true);
            Font tag = ModernSkin.Font(fs * 0.78f, true), time = ModernSkin.Font(fs * 0.82f);
            Color colour = ChatGfx.Channel(l.Ch);
            float x = 0;
            int row = 0;

            void Place(string text, Font font, Color c, byte kind, string name, ChatGfxItem item, bool breakable, float extra = 0)
            {
                if (string.IsNullOrEmpty(text)) return;
                string rest = text, buf = string.Empty;
                float bufX = x;
                void Flush()
                {
                    if (buf.Length > 0) AddRun(l, buf, font, c, kind, name, item, bufX, row, rowH, extra);
                    buf = string.Empty;
                }
                while (rest.Length > 0)
                {
                    // the next chunk: a word with its spaces (the whole run when unbreakable)
                    int end = rest.Length;
                    if (breakable)
                    {
                        end = 0;
                        while (end < rest.Length && rest[end] == ' ') end++;
                        while (end < rest.Length && rest[end] != ' ') end++;
                        while (end < rest.Length && rest[end] == ' ') end++;
                    }
                    string chunk = rest.Substring(0, end);
                    float fitW = ChatGfx.Measure(chunk.TrimEnd(), font) + extra;
                    if (x > 0 && x + fitW > width)
                    {
                        Flush();
                        row++;
                        x = bufX = 0;
                        rest = rest.TrimStart();
                        continue;
                    }
                    if (fitW > width)
                    {
                        // one word wider than the line: break it by characters (overflow-wrap: anywhere)
                        Flush();
                        int take = 1;
                        while (take < chunk.Length && ChatGfx.Measure(chunk.Substring(0, take + 1), font) + extra <= width) take++;
                        AddRun(l, chunk.Substring(0, take), font, c, kind, name, item, 0, row, rowH, extra);
                        rest = rest.Substring(take);
                        row++;
                        x = bufX = 0;
                        continue;
                    }
                    if (buf.Length == 0) bufX = x;
                    buf += chunk;
                    x = bufX + ChatGfx.Measure(buf, font) + extra;
                    rest = rest.Substring(end);
                }
                Flush();
            }

            if (ChatHud.Opts.Time) { Place(Fmt(l.At), time, ModernTheme.Dim, 4, null, null, false); x += S(4); }
            if (l.Whisper != 0 && l.From.Length > 0)
            {
                Place(GameLanguage.ClientTextMap.GetLocalization(l.Whisper == 2 ? ClientTextKeys.ModernChatTo : ClientTextKeys.ModernChatFrom), tag, colour, 3, null, null, false);
                x += S(4);
                Place(l.From, bold, colour, 1, l.From, null, false);
                Place(": ", body, colour, 0, null, null, false);
            }
            else
            {
                string t = ChatHud.Opts.Tags ? TagOf(l.Ch) : string.Empty;
                if (t.Length > 0) { Place("[" + t + "]", tag, colour, 3, null, null, false); x += S(4); }
                if (l.From.Length > 0)
                {
                    Place(l.From, bold, colour, 1, l.From, null, false);
                    Place(": ", body, colour, 0, null, null, false);
                }
            }
            string text = l.From.Length > 0 || l.Whisper != 0 ? l.Body : l.Text;
            int last = 0;
            foreach (System.Text.RegularExpressions.Match m in ChatHud.LinkRx.Matches(text))
            {
                if (m.Index > last) Place(text.Substring(last, m.Index - last), body, colour, 0, null, null, true);
                UserItem item = FindChatItem(m.Groups[2].Value);
                if (item != null) Place(m.Groups[1].Value, bold, ModernTheme.Gold, 2, null, new ChatGfxItem(item), false);
                else Place(m.Groups[1].Value, body, colour, 0, null, null, true);
                last = m.Index + m.Length;
            }
            if (last < text.Length) Place(text.Substring(last), body, colour, 0, null, null, true);
            if (l.N > 1)
            {
                x += S(5);
                Place("×" + l.N, tag, Color.Black, 5, null, null, false, S(8));
            }
            l.Height = (row + 1) * rowH + S(2);
        }

        private static string TagOf(ChatChannel ch)
        {
            ClientTextKeys? key = ch switch
            {
                ChatChannel.Shout => ClientTextKeys.ModernChatShout,
                ChatChannel.Group => ClientTextKeys.ModernChatGroup,
                ChatChannel.Guild => ClientTextKeys.ModernChatGuild,
                ChatChannel.Mentor => ClientTextKeys.ModernChatMentor,
                ChatChannel.Lover => ClientTextKeys.ModernChatLover,
                ChatChannel.System => ClientTextKeys.ModernChatTabSystem,
                ChatChannel.Hint => ClientTextKeys.ModernChatTagHint,
                ChatChannel.Announcement => ClientTextKeys.ModernChatTagNotice,
                ChatChannel.LevelUp => ClientTextKeys.ModernChatTagLevel,
                _ => null,
            };
            return key.HasValue ? GameLanguage.ClientTextMap.GetLocalization(key.Value) : string.Empty;
        }

        /// <summary>The item behind an incoming chat link (GameScene.ChatItemList, MirScene.NewChatItem).</summary>
        private static UserItem FindChatItem(string id)
        {
            if (!ulong.TryParse(id, out ulong uid)) return null;
            List<UserItem> list = GameScene.ChatItemList;
            for (int i = 0; i < list.Count; i++) if (list[i] != null && list[i].UniqueID == uid) return list[i];
            return null;
        }

        private static void AddRun(ChatHud.Line l, string text, Font font, Color c, byte kind, string name, ChatGfxItem item, float x, int row, int rowH, float extra)
        {
            float h = font.Height;
            l.Runs.Add(new ChatHud.Run
            {
                X = x, Y = row * rowH + (rowH - h) / 2f + ModernRoot.S(1), W = ChatGfx.Measure(text, font) + extra, H = h,
                Text = text, Kind = kind, Font = font, Colour = c, Name = name, Item = item?.Item,
            });
        }

        // ------------------------------------------------------------------ scrolling

        public void ScrollBy(int dy, bool byHand)
        {
            int max = MaxScroll;
            int next = Math.Clamp(_scroll + dy, 0, max);
            bool atEnd = next >= max;
            if (atEnd && !_hud.Stick)
            {
                _hud.Stick = true;
                _hud.Unseen = 0;
            }
            else if (!atEnd && byHand) _hud.Stick = false;
            _hud.RenderNewPill();
            if (next == _scroll) return;
            _scroll = next;
            Invalidate();
        }

        public void ScrollToEnd()
        {
            _scroll = MaxScroll;
            Invalidate();
        }

        protected override bool OnWheel(MouseEventArgs e)
        {
            int notches = e.Delta / 120;
            if (notches == 0) notches = Math.Sign(e.Delta);
            ScrollBy(-notches * RowH * 3, true);
            return true;
        }

        /// <summary>Per frame (ChatHud.Process): repaint only when a line reaches its next fade step.</summary>
        public void Tick()
        {
            if (CMain.Time >= _nextFade)
            {
                _nextFade = long.MaxValue;
                Invalidate();
            }
        }

        // ------------------------------------------------------------------ mouse

        private bool HitTest(Point screen, out ChatHud.Line line, out ChatHud.Run run, out float runX, out float runY)
        {
            line = null;
            run = default;
            runX = runY = 0;
            int mx = screen.X - DisplayLocation.X, my = screen.Y - DisplayLocation.Y;
            if (mx < 0 || my < 0 || mx >= Size.Width || my >= Size.Height) return false;
            float y = PadTop - _scroll;
            List<ChatHud.Line> shown = _hud.Shown;
            for (int i = 0; i < shown.Count; i++)
            {
                ChatHud.Line l = shown[i];
                if (my >= y && my < y + l.Height)
                {
                    line = l;
                    for (int r = 0; r < l.Runs.Count; r++)
                    {
                        ChatHud.Run rr = l.Runs[r];
                        if (rr.Kind != 1 && rr.Kind != 2) continue;
                        float rx = PadX + rr.X, ry = y + rr.Y;
                        if (mx >= rx && mx < rx + rr.W && my >= ry - S(2) && my < ry + rr.H + S(2))
                        {
                            run = rr;
                            runX = rx;
                            runY = ry;
                            return true;
                        }
                    }
                    return false;
                }
                y += l.Height;
            }
            return false;
        }

        public override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hit = HitTest(CMain.MPoint, out ChatHud.Line line, out ChatHud.Run run, out _, out _);
            if (line != _hoverLine)
            {
                _hoverLine = line;
                Invalidate();
            }
            if (hit && run.Kind == 2 && run.Item != null)
            {
                ModernTooltip.Show(run.Item, null);
                _tipShown = true;
            }
            else HideTip();
        }

        private void HideTip()
        {
            if (!_tipShown) return;
            _tipShown = false;
            ModernTooltip.HideAll();
        }

        protected override void OnMouseLeave()
        {
            base.OnMouseLeave();
            _hoverLine = null;
            HideTip();
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!HitTest(CMain.MPoint, out _, out ChatHud.Run run, out float rx, out float ry)) return;
            if (run.Kind == 1 && !string.IsNullOrEmpty(run.Name))
                _hud.NameMenu(run.Name, new Point(DisplayLocation.X + (int)rx, DisplayLocation.Y + (int)ry - S(4)));
            else if (run.Kind == 2 && run.Item != null)
            {
                ModernTooltip.Show(run.Item, null);
                _tipShown = true;
            }
        }

        public override void OnMouseDoubleClick(MouseEventArgs e) => OnMouseClick(e);

        protected override void Dispose(bool disposing)
        {
            if (disposing) HideTip();
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ paint

        protected override void Paint(Graphics g)
        {
            LayoutAll();
            long now = CMain.Time;
            bool fade = !_hud.Act && ChatHud.Opts.Fade > 0;
            long fadeMs = ChatHud.Opts.Fade * 1000L;
            long next = long.MaxValue;
            float k = K, mask = S(14), padX = PadX;
            float y = PadTop - _scroll;
            List<ChatHud.Line> shown = _hud.Shown;
            for (int i = 0; i < shown.Count; i++)
            {
                ChatHud.Line l = shown[i];
                float top = y;
                y += l.Height;
                if (y <= 0) continue;
                if (top >= Size.Height) break;

                float alpha = 1f;
                if (fade)
                {
                    if (l.Old) continue;
                    long start = Math.Max(l.T, _hud.FadeBase) + fadeMs;
                    if (now < start) next = Math.Min(next, start);
                    else
                    {
                        int step = (int)Math.Min(FadeStepsCount, (now - start) * FadeStepsCount / FadeOut);
                        if (step >= FadeStepsCount) continue;
                        alpha = 1f - step / (float)FadeStepsCount;
                        next = Math.Min(next, start + (step + 1) * FadeOut / FadeStepsCount);
                    }
                }

                RectangleF band = new RectangleF(padX - 4 * k, top, Size.Width - padX * 2 + 8 * k, l.Height);
                float lineMask = Math.Clamp((top + l.Height / 2f) / mask, 0f, 1f);
                if (l.Mention)
                {
                    using (GraphicsPath p = ModernSkin.RoundRect(band, 3 * k))
                    using (var b = new SolidBrush(ModernTheme.Alpha(ModernTheme.Accent, 0.16f * alpha * lineMask)))
                        g.FillPath(b, p);
                    using var bar = new SolidBrush(ModernTheme.Alpha(ModernTheme.Accent, alpha * lineMask));
                    g.FillRectangle(bar, band.X, band.Y, Math.Max(1f, 2f * k), band.Height);
                }
                else if (l == _hoverLine)
                {
                    using GraphicsPath p = ModernSkin.RoundRect(band, 3 * k);
                    using var b = new SolidBrush(Color.FromArgb((int)(13 * lineMask), 255, 255, 255));
                    g.FillPath(b, p);
                }

                for (int r = 0; r < l.Runs.Count; r++)
                {
                    ChatHud.Run run = l.Runs[r];
                    float ry = top + run.Y;
                    float a = alpha * Math.Clamp((ry + run.H / 2f) / mask, 0f, 1f);
                    if (a <= 0.01f || ry > Size.Height) continue;
                    float rx = padX + run.X;
                    if (run.Kind == 5)
                    {
                        // the ×N pill (chat.ts .x: dim fill, black text)
                        RectangleF pr = new RectangleF(rx, ry, run.W, run.H);
                        using (GraphicsPath pp = ModernSkin.RoundRect(pr, 6 * k))
                        using (var pb = new SolidBrush(ModernTheme.Alpha(ModernTheme.Dim, a)))
                            g.FillPath(pb, pp);
                        using var tb = new SolidBrush(Color.FromArgb((int)(255 * a), 0, 0, 0));
                        g.DrawString(run.Text, run.Font, tb, rx + S(4), ry, ChatGfx.Format);
                        continue;
                    }
                    ChatGfx.ShadowText(g, run.Text, run.Font, run.Colour, rx, ry, run.Kind == 3 ? a * 0.9f : a);
                    if ((run.Kind == 1 || run.Kind == 2) && l == _hoverLine && HoveredRun(top, run))
                    {
                        using var pen = new Pen(Color.FromArgb((int)(run.Colour.A * a), run.Colour), Math.Max(1f, k));
                        float uy = ry + run.H - 1.5f * k;
                        g.DrawLine(pen, rx, uy, rx + ChatGfx.Measure(run.Text.TrimEnd(), run.Font), uy);
                    }
                }
            }
            _nextFade = next;
        }

        private const int FadeStepsCount = 8, FadeOut = 1200;

        private bool HoveredRun(float lineTop, ChatHud.Run run)
        {
            if (!Hovered) return false;
            float mx = CMain.MPoint.X - DisplayLocation.X, my = CMain.MPoint.Y - DisplayLocation.Y;
            float rx = PadX + run.X, ry = lineTop + run.Y;
            return mx >= rx && mx < rx + run.W && my >= ry - S(2) && my < ry + run.H + S(2);
        }
    }

    /// <summary>A chat item link's UserItem (a class so the layout's local function can take null).</summary>
    internal sealed class ChatGfxItem
    {
        public readonly UserItem Item;
        public ChatGfxItem(UserItem item) { Item = item; }
    }

    /// <summary>The chat's tab strip (chat.ts .tstrip / .ct): small tabs with unread badges, whisper tabs closable by their ×
    /// (shown while hovered), scrolled sideways by the wheel when they overflow.</summary>
    internal sealed class ChatTabStrip : ModernControl
    {
        private readonly ChatHud _hud;
        private readonly List<(int Tab, RectangleF Box, RectangleF Close)> _rects = new List<(int, RectangleF, RectangleF)>();
        private int _hover = -1, _offset;
        private bool _hoverClose;
        private float _total;

        public ChatTabStrip(ChatHud hud)
        {
            _hud = hud;
            Sound = SoundList.ButtonA;
        }

        public void Relayout() => Invalidate();

        private void Measure()
        {
            _rects.Clear();
            Font f = ModernSkin.Font(11, true), small = ModernSkin.Font(9, true);
            float x = -_offset, gap = S(2), h = Size.Height;
            for (int i = 0; i < _hud.TabCount; i++)
            {
                if (!_hud.IsTabVisible(i)) continue;
                float w = S(14) + ChatGfx.Measure(_hud.TabLabel(i), f);
                int n = _hud.TabUnread(i);
                if (n > 0) w += S(4) + Math.Max(S(14), ChatGfx.Measure(n > 99 ? "99+" : n.ToString(), small) + S(6));
                RectangleF close = RectangleF.Empty;
                if (_hud.TabClosable(i) && i == _hover)
                {
                    close = new RectangleF(x + w - S(4), 0, S(12), h);
                    w += S(10);
                }
                _rects.Add((i, new RectangleF(x, 0, w, h), close));
                x += w + gap;
            }
            _total = x + _offset;
        }

        private int At(Point screen, out bool onClose)
        {
            onClose = false;
            Measure();
            float mx = screen.X - DisplayLocation.X, my = screen.Y - DisplayLocation.Y;
            for (int i = 0; i < _rects.Count; i++)
            {
                if (!_rects[i].Box.Contains(mx, my)) continue;
                onClose = !_rects[i].Close.IsEmpty && _rects[i].Close.Contains(mx, my);
                return _rects[i].Tab;
            }
            return -1;
        }

        public override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = At(CMain.MPoint, out bool close);
            if (i == _hover && close == _hoverClose) return;
            _hover = i;
            _hoverClose = close;
            Invalidate();
        }

        protected override void OnMouseLeave()
        {
            base.OnMouseLeave();
            _hover = -1;
            _hoverClose = false;
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            int i = At(CMain.MPoint, out bool close);
            if (i < 0) return;
            base.OnMouseClick(e);
            if (close) _hud.CloseTab(i);
            else _hud.SelectTab(i);
            _hover = -1;
            Invalidate();
        }

        public override void OnMouseDoubleClick(MouseEventArgs e) => OnMouseClick(e);

        protected override bool OnWheel(MouseEventArgs e)
        {
            Measure();
            int max = Math.Max(0, (int)Math.Ceiling(_total) - Size.Width);
            int next = Math.Clamp(_offset - Math.Sign(e.Delta) * S(40), 0, max);
            if (next != _offset)
            {
                _offset = next;
                Invalidate();
            }
            return true;
        }

        protected override void Paint(Graphics g)
        {
            Measure();
            float k = K, rad = ModernTheme.Radius * 0.7f * k;
            Font f = ModernSkin.Font(11, true), small = ModernSkin.Font(9, true);
            for (int r = 0; r < _rects.Count; r++)
            {
                var (i, box, close) = _rects[r];
                if (box.Right < 0 || box.X > Size.Width) continue;
                bool on = i == _hud.CurrentTab, ping = _hud.TabPing(i), hot = i == _hover;
                if (on || ping)
                {
                    using GraphicsPath p = ModernSkin.RoundTop(box, rad);
                    using (var b = new SolidBrush(ModernTheme.Alpha(ModernTheme.Accent, ping && !on ? 0.38f : 0.22f))) g.FillPath(b, p);
                    if (on)
                        using (var bar = new SolidBrush(ModernTheme.Accent)) g.FillRectangle(bar, box.X, box.Bottom - 2 * k, box.Width, 2 * k);
                }
                Color c = on || ping ? Color.White : hot ? ModernTheme.Text : ModernTheme.Dim;
                string label = _hud.TabLabel(i);
                float tx = box.X + S(7), ty = (box.Height - f.Height) / 2f;
                ChatGfx.ShadowText(g, label, f, c, tx, ty, 1f, 0.7f);
                float x = tx + ChatGfx.Measure(label, f) + S(4);
                int n = _hud.TabUnread(i);
                if (n > 0)
                {
                    string s = n > 99 ? "99+" : n.ToString();
                    float bw = Math.Max(S(14), ChatGfx.Measure(s, small) + S(6)), bh = S(14);
                    RectangleF br = new RectangleF(x, (box.Height - bh) / 2f, bw, bh);
                    using (GraphicsPath bp = ModernSkin.RoundRect(br, bh / 2f))
                    using (var bb = new SolidBrush(ModernSkin.Opaque(ModernTheme.Accent)))
                        g.FillPath(bb, bp);
                    ModernSkin.Text(g, s, small, Color.White, br, StringAlignment.Center);
                }
                if (!close.IsEmpty)
                    ModernSkin.Text(g, "×", f, _hoverClose ? ModernTheme.BadSoft : ModernTheme.Dim, close, StringAlignment.Center);
            }
        }
    }
}

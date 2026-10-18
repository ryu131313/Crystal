// Shared pieces of the phase 4 guild area (WebClient/src/ui/modern/windows/guild.ts, territories.ts, ranking.ts): the guild
// buff clock (GuildBuffClock: when this UI saw each active buff's whole-minute ActiveTimeRemaining, so its countdown runs between
// packets), and painting helpers for the crest shields, rank / status tags, online dots, class badges and feed rows those
// windows repeat (.emblem, .tag, .dot, .cb, .feed in their CSS).
using System.Drawing.Drawing2D;
using Client.MirScenes.Models;
using Client.MirScenes.Modern.Core;

namespace Client.MirScenes.Modern.Windows
{
    /// <summary>
    /// GuildBuffClock (guild.ts): one per ModernUi, built at UI start so it sees S.GuildBuffList from login on. The server sends
    /// ActiveTimeRemaining in whole minutes and nothing until the buff ends, so the countdown runs from the time it was seen.
    /// </summary>
    internal sealed class GuildBuffClock : IDisposable
    {
        private readonly Dictionary<int, (int Rem, long At)> _seen = new Dictionary<int, (int, long)>();
        private readonly GuildState _guild;
        private readonly Action _sync;

        public GuildBuffClock(GuildState guild)
        {
            _guild = guild;
            _sync = Sync;
            _guild?.BuffsChanged.On(_sync);
            Sync();
        }

        /// <summary>Records a new remaining time (a buff activated, or the login list); drops buffs that stopped.</summary>
        private void Sync()
        {
            if (_guild == null) return;
            List<GuildBuff> buffs = _guild.EnabledBuffs;
            var drop = new List<int>();
            foreach (int id in _seen.Keys)
            {
                bool live = false;
                for (int i = 0; i < buffs.Count; i++)
                    if (buffs[i] != null && buffs[i].Id == id && buffs[i].Active) live = true;
                if (!live) drop.Add(id);
            }
            for (int i = 0; i < drop.Count; i++) _seen.Remove(drop[i]);
            for (int i = 0; i < buffs.Count; i++)
            {
                GuildBuff b = buffs[i];
                if (b == null || !b.Active) continue;
                if (!_seen.TryGetValue(b.Id, out var s) || s.Rem != b.ActiveTimeRemaining) _seen[b.Id] = (b.ActiveTimeRemaining, CMain.Time);
            }
        }

        /// <summary>Milliseconds left in an active buff (approximate: the server ends it on its own minute tick).</summary>
        public long Left(GuildBuff b)
        {
            if (b == null) return 0;
            bool known = _seen.TryGetValue(b.Id, out var s);
            long rem = (known ? s.Rem : b.ActiveTimeRemaining) * 60000L;
            return Math.Max(0, rem - (known ? CMain.Time - s.At : 0));
        }

        public void Dispose() => _guild?.BuffsChanged.Off(_sync);
    }

    /// <summary>Guild-area text and drawing helpers.</summary>
    internal static class Gd
    {
        /// <summary>The crest's initials: the first letters of up to two words (or the first two characters).</summary>
        public static string Initials(string name)
        {
            string n = (name ?? string.Empty).Trim();
            if (n.Length == 0) return "–";
            string[] words = n.Split(new[] { ' ', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
            string s = words.Length > 1 ? words[0].Substring(0, 1) + words[1].Substring(0, 1) : n.Substring(0, Math.Min(2, n.Length));
            return s.ToUpperInvariant();
        }

        /// <summary>territories.ts crest: the capitals ("GameMaster" → GM), else the first two letters.</summary>
        public static string Capitals(string name)
        {
            string n = name ?? string.Empty;
            var caps = new System.Text.StringBuilder();
            for (int i = 0; i < n.Length; i++) if (n[i] >= 'A' && n[i] <= 'Z') caps.Append(n[i]);
            string s = caps.Length >= 2 ? caps.ToString() : n;
            return s.Length == 0 ? "–" : s.Substring(0, Math.Min(2, s.Length)).ToUpperInvariant();
        }

        public static string ClassName(MirClass c) => c.ToLocalizedString();

        /// <summary>classTag: the class's short tag (WAR / WIZ / TAO / ASN / ARC).</summary>
        public static string ClassTag(MirClass c) => c switch
        {
            MirClass.Warrior => W.T(ClientTextKeys.ModernGuildTagWarrior),
            MirClass.Wizard => W.T(ClientTextKeys.ModernGuildTagWizard),
            MirClass.Taoist => W.T(ClientTextKeys.ModernGuildTagTaoist),
            MirClass.Assassin => W.T(ClientTextKeys.ModernGuildTagAssassin),
            MirClass.Archer => W.T(ClientTextKeys.ModernGuildTagArcher),
            _ => "?",
        };

        /// <summary>A rank's display name (an unnamed rank shows "Rank N").</summary>
        public static string RankName(string name, int index) => string.IsNullOrEmpty(name) ? W.T(ClientTextKeys.ModernGuildRankN, index + 1) : name;

        /// <summary>"Online" or how long ago (fmtAgo) from a last login; "—" when unknown.</summary>
        public static string Seen(bool online, DateTime lastLogin)
        {
            if (online) return W.T(ClientTextKeys.ModernGuildOnline);
            if (lastLogin <= DateTime.MinValue.AddDays(1) || lastLogin.Year < 2000) return "—";
            return Eco.Ago(lastLogin);
        }

        /// <summary>fmtClock: 1:02:03 / 4:05.</summary>
        public static string Clock(long ms)
        {
            long s = Math.Max(0, (ms + 999) / 1000);
            long h = s / 3600, m = s / 60 % 60, ss = s % 60;
            return h > 0 ? h + ":" + m.ToString("00") + ":" + ss.ToString("00") : m + ":" + ss.ToString("00");
        }

        /// <summary>The .emblem shield (a crest) with text, filled with the panel colour and an accent border.</summary>
        public static void Shield(Graphics g, RectangleF r, string text, float stroke, float fontHud)
        {
            using var p = new GraphicsPath();
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            // M28 2 l24 8 v19 c0 16 -10 27 -24 33 C14 56 4 45 4 29 V10 z  (viewBox 56 x 64)
            PointF P(float px, float py) => new PointF(x + px / 56f * w, y + py / 64f * h);
            p.AddLine(P(28, 2), P(52, 10));
            p.AddLine(P(52, 10), P(52, 29));
            p.AddBezier(P(52, 29), P(52, 45), P(42, 56), P(28, 62));
            p.AddBezier(P(28, 62), P(14, 56), P(4, 45), P(4, 29));
            p.AddLine(P(4, 29), P(4, 10));
            p.CloseFigure();
            using (var b = new SolidBrush(ModernSkin.Opaque(ModernTheme.PanelC))) g.FillPath(b, p);
            ModernSkin.Stroke(g, p, ModernTheme.Accent, stroke);
            ModernSkin.Text(g, text, ModernSkin.Font(fontHud, true), Color.White, new RectangleF(x, y + h * 0.04f, w, h * 0.9f), StringAlignment.Center);
        }

        /// <summary>The .tag pill (rank names, statuses): outline in <paramref name="colour"/>, filled when <paramref name="solid"/>.
        /// Returns its width (device px).</summary>
        public static float Tag(Graphics g, float x, float cy, string text, Color colour, bool solid = false, bool upper = true, float fontHud = 9)
        {
            float k = ModernRoot.K;
            string t = upper ? (text ?? string.Empty).ToUpperInvariant() : text ?? string.Empty;
            Font f = ModernSkin.Font(fontHud, true);
            float w = ModernSkin.Measure(t, f) + 11 * k, h = (fontHud + 6) * k;
            RectangleF r = new RectangleF(x, cy - h / 2f, w, h);
            using (GraphicsPath p = ModernSkin.RoundRect(r, h / 2f))
            {
                if (solid)
                    using (var b = new SolidBrush(ModernSkin.Opaque(colour))) g.FillPath(b, p);
                else
                    using (var b = new SolidBrush(W.Over(colour, 0.12f))) g.FillPath(b, p);
                ModernSkin.Stroke(g, p, solid ? ModernSkin.Opaque(colour) : W.Over(colour, 0.55f));
            }
            ModernSkin.Text(g, t, f, solid ? Color.Black : colour, r, StringAlignment.Center, StringAlignment.Center, false);
            return w;
        }

        /// <summary>A rank tag: the leader rank (index 0) solid in the accent, else outlined.</summary>
        public static float RankTag(Graphics g, float x, float cy, string name, int index)
        {
            if (index == 0)
            {
                float k = ModernRoot.K;
                string t = RankName(name, index).ToUpperInvariant();
                Font f = ModernSkin.Font(9, true);
                float w = ModernSkin.Measure(t, f) + 11 * k, h = 15 * k;
                RectangleF r = new RectangleF(x, cy - h / 2f, w, h);
                using (GraphicsPath p = ModernSkin.RoundRect(r, h / 2f))
                {
                    using (var b = new SolidBrush(W.Over(ModernTheme.Accent, 0.3f))) g.FillPath(b, p);
                    ModernSkin.Stroke(g, p, ModernTheme.Accent);
                }
                ModernSkin.Text(g, t, f, Color.White, r, StringAlignment.Center, StringAlignment.Center, false);
                return w;
            }
            return Tag(g, x, cy, RankName(name, index), ModernTheme.AccentDim);
        }

        /// <summary>The .dot: green when online, grey otherwise.</summary>
        public static void Dot(Graphics g, float x, float cy, bool on, float sizeHud = 7)
        {
            float d = sizeHud * ModernRoot.K;
            using var b = new SolidBrush(on ? Color.FromArgb(255, 0x4f, 0xe0, 0x7a) : Color.FromArgb(255, 0x59, 0x60, 0x6e));
            g.FillEllipse(b, x, cy - d / 2f, d, d);
        }

        /// <summary>The ranking .cb class badge: a disc with the class tag, ringed in the class colour, with an online pip.</summary>
        public static void ClassBadge(Graphics g, RectangleF r, string text, Color ring, bool online, float fontHud)
        {
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(r);
                using var b = new PathGradientBrush(path) { CenterColor = ModernSkin.Opaque(ModernTheme.PanelC), SurroundColors = new[] { ModernSkin.Opaque(ModernTheme.PanelB) } };
                g.FillPath(b, path);
                ModernSkin.Stroke(g, path, ring.IsEmpty ? ModernTheme.Frame : ring, Math.Max(1f, r.Width / 26f));
            }
            ModernSkin.Text(g, text, ModernSkin.Font(fontHud, true), ModernTheme.Text, r, StringAlignment.Center, StringAlignment.Center, false);
            if (!online) return;
            float d = Math.Max(6f, r.Width * 0.3f);
            RectangleF pip = new RectangleF(r.Right - d + 2, r.Bottom - d + 2, d, d);
            using (var b = new SolidBrush(ModernTheme.Ok)) g.FillEllipse(b, pip);
            using (var pen = new Pen(Color.Black, 2f)) g.DrawEllipse(pen, pip);
        }

        /// <summary>A thin progress bar (.bar / .xp): a dark well with an accent gradient fill.</summary>
        public static void Bar(Graphics g, RectangleF r, float fill, Color? colour = null, bool border = false)
        {
            using (GraphicsPath p = ModernSkin.RoundRect(r, r.Height / 2f))
            {
                using (var b = new SolidBrush(Color.FromArgb(255, 4, 6, 10))) g.FillPath(b, p);
                if (border) ModernSkin.Stroke(g, p, ModernTheme.Frame);
            }
            fill = Math.Clamp(fill, 0f, 1f);
            if (fill <= 0) return;
            RectangleF f = new RectangleF(r.X, r.Y, Math.Max(r.Height, r.Width * fill), r.Height);
            Color c = colour ?? ModernTheme.Accent;
            using GraphicsPath fp = ModernSkin.RoundRect(f, r.Height / 2f);
            using var br = new LinearGradientBrush(new RectangleF(f.X - 1, f.Y, f.Width + 2, f.Height), ModernTheme.Mix(c, Color.Black, 0.45f), c, LinearGradientMode.Horizontal);
            g.FillPath(br, fp);
        }

        /// <summary>The .card box with an optional header caption (.ch: small, bold, upper case, accent-dim).</summary>
        public static void CardWithHeader(Graphics g, Size size, string header)
        {
            W.Card(g, size);
            if (string.IsNullOrEmpty(header)) return;
            float k = ModernRoot.K;
            ModernSkin.Text(g, header.ToUpperInvariant(), ModernSkin.Font(10, true), ModernTheme.AccentDim, new RectangleF(12 * k, 8 * k, size.Width - 24 * k, 14 * k));
        }
    }
}

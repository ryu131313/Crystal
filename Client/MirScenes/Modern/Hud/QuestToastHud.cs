// Quest V2 progress toasts (Part B "Clients": "a short bar on progress, a completion banner"; WebClient/src/ui/modern/hud/ has
// the web counterpart). A card at the bottom centre of the screen, above the HUD rig: on an objective's progress (Models.Quests.Progressed) the quest
// name, the objective text, its count and a short bar for 3 s; when a run becomes Ready / fails / is turned in, a banner for 4 s.
// A newer event replaces the shown one. Never takes the mouse. Painted on an event only; Process only checks the expiry.
using Client.MirScenes.Models;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Windows;
using S = ServerPackets;

namespace Client.MirScenes.Modern.Hud
{
    public sealed class QuestToastHud : ModernSurface
    {
        private const int Width = 300, ProgressMs = 3000, BannerMs = 4000;

        private readonly QuestToastCard _card;
        private readonly Action<(int, int, int, int)> _progressed;
        private readonly Action<int> _readied, _failed, _turnedIn;
        private QuestBook _book;
        private long _until;

        public QuestToastHud()
        {
            PassThrough = true;
            NotControl = true;
            Visible = false;
            Parent = ModernRoot.Instance?.Hud;
            _card = new QuestToastCard { Parent = this, NotControl = true, Location = Point.Empty };
            _progressed = OnProgress;
            _readied = q => Banner(q, ClientTextKeys.ModernQuestToastReady, false);
            _failed = q => Banner(q, ClientTextKeys.ModernQuestToastFailed, true);
            _turnedIn = q => Banner(q, ClientTextKeys.ModernQuestToastComplete, false);
            _book = GameScene.Models?.Quests;
            if (_book != null)
            {
                _book.Progressed.On(_progressed);
                _book.Readied.On(_readied);
                _book.Failed.On(_failed);
                _book.TurnedIn.On(_turnedIn);
            }
            if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout += OnRelayout;
        }

        // ---- for the UI test
        internal string ShownText => Visible ? _card.Title + " | " + _card.Text + (_card.Need > 0 ? " " + _card.Have + "/" + _card.Need : string.Empty) : string.Empty;

        public override bool IsMouseOver(Point p) => false;

        private void OnRelayout(bool rebuild)
        {
            if (IsDisposed) return;
            Layout();
        }

        private void OnProgress((int Quest, int Objective, int Old, int Value) e)
        {
            if (_book == null || e.Value <= e.Old) return;
            S.QuestDefinition def = _book.Def(e.Quest);
            S.QuestDefinition.Objective o = null;
            if (def != null)
                for (int i = 0; i < def.Objectives.Count; i++) if (def.Objectives[i].Index == e.Objective) { o = def.Objectives[i]; break; }
            if (o == null || (o.Hidden && e.Value <= 0)) return;
            int need = Math.Max(1, o.Count);
            _card.Set(false, false, def.Name, QuestText.Label(o), Math.Min(e.Value, need), QuestText.Countable(o) ? need : 0);
            Show(ProgressMs);
        }

        private void Banner(int quest, ClientTextKeys title, bool bad)
        {
            S.QuestDefinition def = _book?.Def(quest);
            string name = def?.Name ?? string.Empty;
            if (name.Length == 0)
                foreach (S.QuestHistory.Entry h in _book?.History ?? new List<S.QuestHistory.Entry>()) if (h.Quest == quest) { name = h.Name; break; }
            _card.Set(true, bad, W.T(title), name, 0, 0);
            Show(BannerMs);
        }

        /// <summary>S.QuestRefused (ModernUi.QuestRefused): a red banner, the request + quest as the title and the reason below.</summary>
        public void Refusal(S.QuestRefused p)
        {
            QuestText.Refusal(p, out string title, out string reason);
            _card.Set(true, true, title, reason, 0, 0, small: true);
            Show(BannerMs);
        }

        private void Show(int ms)
        {
            _until = CMain.Time + ms;
            Layout();
            if (!Visible) Visible = true;
            BringToFront();
        }

        private void Layout()
        {
            Size size = new Size(S(Width), S(_card.Banner ? 58 : 56));
            if (Size != size) Size = size;
            if (_card.Size != size) _card.Size = size;
            // bottom centre, above the HUD rig: windows (NPC, quest log) usually cover the upper middle of the screen
            Point at = new Point((Settings.ScreenWidth - size.Width) / 2, Math.Max(0, Settings.ScreenHeight - S(196) - size.Height));
            if (Location != at) Location = at;
        }

        public override void Process()
        {
            if (CMain.Time < _until) return;
            Visible = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_book != null)
                {
                    _book.Progressed.Off(_progressed);
                    _book.Readied.Off(_readied);
                    _book.Failed.Off(_failed);
                    _book.TurnedIn.Off(_turnedIn);
                    _book = null;
                }
                if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout -= OnRelayout;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>The toast's card: a progress line with its bar, or a banner (title + quest name).</summary>
    internal sealed class QuestToastCard : ModernControl
    {
        public bool Banner { get; private set; }
        private bool _bad;
        public string Title { get; private set; } = string.Empty;
        public string Text { get; private set; } = string.Empty;
        public int Have { get; private set; }
        public int Need { get; private set; }

        private bool _small;

        public void Set(bool banner, bool bad, string title, string text, int have, int need, bool small = false)
        {
            _small = small;
            Banner = banner;
            _bad = bad;
            Title = title ?? string.Empty;
            Text = text ?? string.Empty;
            Have = have;
            Need = need;
            Invalidate();
        }

        protected override void Paint(Graphics g)
        {
            float k = K;
            RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            using (var p = ModernSkin.RoundRect(r, ModernTheme.Radius * k))
            {
                using (var br = new SolidBrush(Color.FromArgb(214, 12, 14, 20))) g.FillPath(br, p);
                ModernSkin.Stroke(g, p, _bad ? ModernTheme.Hex("#ff6b5a") : ModernTheme.Hex("#c99a3a"));
            }
            if (Banner)
            {
                Color tc = _bad ? ModernTheme.Hex("#ff6b5a") : ModernTheme.Hex("#f0c860");
                ModernSkin.SpacedText(g, Title.ToUpperInvariant(), ModernSkin.Font(11, true), tc, new RectangleF(0, 7 * k, Size.Width, 16 * k), 1.6f * k);
                ModernSkin.Text(g, Text, ModernSkin.Font(_small ? 11.5f : 14, !_small), Color.White, new RectangleF(10 * k, 25 * k, Size.Width - 20 * k, 24 * k), StringAlignment.Center);
                return;
            }
            ModernSkin.Text(g, Title, ModernSkin.Font(10, true), ModernTheme.Hex("#f0c860"), new RectangleF(12 * k, 6 * k, Size.Width - 24 * k, 15 * k));
            string count = Need > 0 ? Have + "/" + Need : (Have > 0 ? "✓" : string.Empty);
            Font cf = ModernSkin.Font(12, true);
            float cw = count.Length > 0 ? ModernSkin.Measure(count, cf) + 4 : 0;
            ModernSkin.Text(g, Text, ModernSkin.Font(12), Color.White, new RectangleF(12 * k, 21 * k, Size.Width - 24 * k - cw, 17 * k));
            if (cw > 0) ModernSkin.Text(g, count, cf, Need > 0 && Have >= Need ? ModernTheme.Hex("#7ff09a") : Color.White, new RectangleF(Size.Width - 12 * k - cw, 21 * k, cw, 17 * k), StringAlignment.Far);
            if (Need > 0) QuestGfx.Bar(g, new RectangleF(12 * k, 42 * k, Size.Width - 24 * k, 5 * k), Have / (float)Need, Have >= Need);
        }
    }
}

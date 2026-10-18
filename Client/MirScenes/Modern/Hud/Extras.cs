// Ported from WebClient/src/ui/modern/hud/extras.ts (phase 2 part): the output lines (GameScene.OutputMessage: up to 10,
// 5 s each, kept and expired by GameScene; Quest gold, Guild pink, else green) at the top left beside the party frames.
// The poison icon row and the durability strip are the bottom HUD's (Hud.cs), as on the web.
// Not ported yet (each needs an IGameUi member that is still a NotPorted stub in ModernUi.cs, integrator-owned): the timer
// pill (SetTimer / ExpireTimer), the compass (SetCompass), the roll (Roll), the notice board window (ShowNotice), the
// poison status line and screen tints (no PoisonStatus timers on the desktop; the tints need a full-screen layer below the
// windows), OpenBrowser and the NPC input popup (NPC phase).
using Client.MirScenes.Modern.Core;

namespace Client.MirScenes.Modern.Hud
{
    internal sealed class HudOutputs : ModernSurface
    {
        public const int Width = 520, LineH = 17, MaxLines = 10;

        private readonly List<OutputLine> _lines = new List<OutputLine>();
        private int _count = -1;
        private object _first, _last;
        private readonly Action<bool> _onRelayout;

        public HudOutputs()
        {
            PassThrough = true;
            Visible = false;
            _onRelayout = rebuild =>
            {
                Size = new Size(S(Width), S(MaxLines * LineH + 4));
                Location = new Point(S(210), S(14));
                for (int i = Controls.Count - 1; i >= 0; i--) Controls[i].Dispose();
                _lines.Clear();
                _count = -1;
                Check();
            };
            if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout += _onRelayout;
            Size = new Size(S(Width), S(MaxLines * LineH + 4));
            Location = new Point(S(210), S(14));
        }

        public int LineCount => _lines.Count;

        /// <summary>Per frame from the HUD (also while hidden): a count and two reference compares; rebuilds only on a change.</summary>
        internal void Check()
        {
            List<GameScene.OutPutMessage> list = GameScene.Scene?.OutputMessages;
            int n = list?.Count ?? 0;
            object first = n > 0 ? list[0] : null, last = n > 0 ? list[n - 1] : null;
            if (n == _count && first == _first && last == _last) return;
            _count = n;
            _first = first;
            _last = last;
            for (int i = 0; i < _lines.Count; i++) _lines[i].Dispose();
            _lines.Clear();
            for (int i = 0; i < n && i < MaxLines; i++)
            {
                var line = new OutputLine(list[i].Message, list[i].Type) { Parent = this, Location = new Point(0, S(2 + i * LineH)), Size = new Size(S(Width), S(LineH)) };
                _lines.Add(line);
            }
            Visible = n > 0;
            TextureValid = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && ModernRoot.Instance != null) ModernRoot.Instance.Relayout -= _onRelayout;
            base.Dispose(disposing);
        }
    }

    /// <summary>One output line (.houts div): 12 px semi-bold with a dark shadow, coloured by type.</summary>
    internal sealed class OutputLine : ModernControl
    {
        private readonly string _text;
        private readonly Color _colour;

        public OutputLine(string text, OutputMessageType type)
        {
            NotControl = true;
            _text = text ?? string.Empty;
            _colour = type == OutputMessageType.Quest ? Color.FromArgb(255, 255, 216, 74)
                : type == OutputMessageType.Guild ? Color.FromArgb(255, 255, 111, 192) : Color.FromArgb(255, 111, 224, 138);
        }

        protected override void Paint(Graphics g)
        {
            HudKit.ShadowText(g, _text, ModernSkin.Font(12, true), _colour, new RectangleF(1, 0, Size.Width - 2, Size.Height), StringAlignment.Near, StringAlignment.Center, 200);
        }
    }
}

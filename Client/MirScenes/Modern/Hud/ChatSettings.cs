// Ported from WebClient/src/ui/modern/hud/chat.ts settings() (.pop.set): the chat's settings popover — background, fade,
// text size, timestamps, channel tags, lock — and "Hide in every tab", the Settings.Filter*Chat switches shared with the
// classic chat options. Kit widgets on a card, on the Menu layer, its bottom-right at the chat box's top-right.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirSounds;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;

namespace Client.MirScenes.Modern.Hud
{
    internal sealed class ChatSettingsPop : ModernSurface
    {
        private const int Width = 280, Pad = 10, RowH = 24, Gap = 7;

        public ChatSettingsPop(ChatHud hud, Point bottomRight)
        {
            Parent = ModernRoot.Instance?.Menu;
            string L(ClientTextKeys k) => GameLanguage.ClientTextMap.GetLocalization(k);
            ChatHud.Options o = ChatHud.Opts;

            var card = new SettingsCard { Parent = this, Location = Point.Empty };
            int inner = Width - Pad * 2, y = 8;

            var opacity = new Slider(0, 90, o.Opacity, 110, 5, false);
            opacity.Changed += v => { o.Opacity = v; hud.OptionsChanged(false); };
            Row(card, ref y, inner, L(ClientTextKeys.ModernChatBackground), opacity);

            string sec = L(ClientTextKeys.ModernChatSeconds);
            var fade = new Segmented<int>(new[]
            {
                new Choice<int>(10, string.Format(sec, 10)), new Choice<int>(20, string.Format(sec, 20)),
                new Choice<int>(60, L(ClientTextKeys.ModernChatMinute)), new Choice<int>(0, L(ClientTextKeys.ModernChatNever)),
            }, o.Fade, 160);
            fade.Changed += v => { o.Fade = v; hud.OptionsChanged(true); };
            Row(card, ref y, inner, L(ClientTextKeys.ModernChatFadeAfter), fade);

            var size = new Segmented<int>(new[]
            {
                new Choice<int>(11, L(ClientTextKeys.ModernChatSizeS)), new Choice<int>(12, L(ClientTextKeys.ModernChatSizeM)),
                new Choice<int>(14, L(ClientTextKeys.ModernChatSizeL)),
            }, o.FontSize, 96);
            size.Changed += v => { o.FontSize = v; hud.OptionsChanged(false); };
            Row(card, ref y, inner, L(ClientTextKeys.ModernChatTextSize), size);

            var time = new ToggleSwitch(o.Time, true);
            time.Changed += v => { o.Time = v; hud.OptionsChanged(true); };
            Row(card, ref y, inner, L(ClientTextKeys.ModernChatTimestamps), time);

            var tags = new ToggleSwitch(o.Tags, true);
            tags.Changed += v => { o.Tags = v; hud.OptionsChanged(true); };
            Row(card, ref y, inner, L(ClientTextKeys.ModernChatTags), tags);

            var lockSw = new ToggleSwitch(o.Lock, true);
            lockSw.Changed += v => { o.Lock = v; hud.OptionsChanged(false); };
            Row(card, ref y, inner, L(ClientTextKeys.ModernChatLockPosition), lockSw);

            new ModernLabel
            {
                Parent = card,
                Location = new Point(S(Pad), S(y)),
                Size = new Size(S(inner), S(14)),
                Text = L(ClientTextKeys.ModernChatHideInEvery).ToUpperInvariant(),
                FontSize = 9,
                Bold = true,
                Colour = ModernTheme.Dim,
            };
            y += 14 + Gap;

            // [Filters]: shared with the classic chat options (Settings.Filter*Chat).
            var choices = new[]
            {
                new Choice<int>(0, L(ClientTextKeys.ModernChatSay)), new Choice<int>(1, L(ClientTextKeys.ModernChatShout)),
                new Choice<int>(2, L(ClientTextKeys.ModernChatWhisper)), new Choice<int>(3, L(ClientTextKeys.ModernChatGroup)),
                new Choice<int>(4, L(ClientTextKeys.ModernChatGuild)), new Choice<int>(5, L(ClientTextKeys.ModernChatTabSystem)),
            };
            var hidden = new List<int>();
            if (Settings.FilterNormalChat) hidden.Add(0);
            if (Settings.FilterShoutChat) hidden.Add(1);
            if (Settings.FilterWhisperChat) hidden.Add(2);
            if (Settings.FilterGroupChat) hidden.Add(3);
            if (Settings.FilterGuildChat) hidden.Add(4);
            if (Settings.FilterSystemChat) hidden.Add(5);
            var filters = new Chips<int>(choices, hidden, inner, true, true) { Parent = card, Location = new Point(S(Pad), S(y)) };
            filters.Changed += sel =>
            {
                Settings.FilterNormalChat = sel.Contains(0);
                Settings.FilterShoutChat = sel.Contains(1);
                Settings.FilterWhisperChat = sel.Contains(2);
                Settings.FilterGroupChat = sel.Contains(3);
                Settings.FilterGuildChat = sel.Contains(4);
                Settings.FilterSystemChat = sel.Contains(5);
                hud.RenderLog(); // ChatHud.Process sees the mask too; this repaints now
            };
            int bottom = filters.Location.Y + filters.Size.Height + S(10);
            card.Size = new Size(S(Width), bottom);
            Size = card.Size;
            Location = new Point(Math.Max(0, Math.Min(bottomRight.X - Size.Width, Settings.ScreenWidth - Size.Width)), Math.Max(0, bottomRight.Y - Size.Height - S(4)));
        }

        private static void Row(MirControl parent, ref int y, int inner, string label, MirControl control)
        {
            ModernPanel row = new ModernPanel { Parent = parent, Location = new Point(S(Pad), S(y)), Size = new Size(S(inner), S(RowH)) };
            new ModernLabel { Parent = row, Location = Point.Empty, Size = new Size(S(inner) - control.Size.Width - S(8), S(RowH)), Text = label, FontSize = 11 };
            control.Parent = row;
            control.Location = new Point(row.Size.Width - control.Size.Width, (row.Size.Height - control.Size.Height) / 2);
            y += RowH + Gap;
        }

        private sealed class SettingsCard : ModernControl
        {
            protected override void Paint(Graphics g)
            {
                float rad = ModernTheme.Radius * K;
                RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
                using GraphicsPath p = ModernSkin.RoundRect(r, rad);
                using (var b = new SolidBrush(ModernSkin.Opaque(ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, 0.96f)))) g.FillPath(b, p);
                ModernSkin.Stroke(g, p, ModernTheme.Frame);
            }
        }
    }
}

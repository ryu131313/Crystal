// Ported from WebClient/src/ui/modern/hud/tracker.ts (QuestTracker): the tracked quests on the right of the screen, the
// desktop's QuestTrackingDialog. Tracking (at most 5) is kept like the classic tracker: Settings.TrackedQuests per character
// (QuestTracking.ini, Settings.SaveTrackedQuests), a quest the server adds with track = true is tracked. Quest names in gold,
// each ClientQuestProgress.TaskList line under it (a trailing "n/m" is shown as the count and marks the line when complete),
// and "Ready: return to <NPC>" once Completed (FinishNPCIndex = the NPC's ObjectID: its name from the map objects or the map's
// NPC list). The header folds the list (per session); a quest click opens the "quests" window when one is registered.
// Hidden while nothing is tracked. Repainted on ModernEvents.Quests / QuestAdded only (and only when the text changed).
// Desktop idiom: one surface on the HUD layer; long lines end in an ellipsis (the web scrolls them back and forth).
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using Client.MirControls;
using Client.MirObjects;
using Client.MirSounds;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Windows;

namespace Client.MirScenes.Modern.Hud
{
    public sealed class TrackerHud : ModernSurface
    {
        public const int MaxTracked = 5;
        private const int Width = 220;
        private static bool _shut;

        private readonly BigMapData _data;
        private readonly List<int> _tracked = new List<int>();
        private TrackerHead _head;
        private TrackerBody _body;
        private string _key = "\u0000";

        public TrackerHud(BigMapData data)
        {
            _data = data;
            PassThrough = true;
            Visible = false;
            Parent = ModernRoot.Instance?.Hud;
            for (int i = 0; i < Settings.TrackedQuests.Length; i++)
                if (Settings.TrackedQuests[i] >= 0 && !_tracked.Contains(Settings.TrackedQuests[i])) _tracked.Add(Settings.TrackedQuests[i]);
            Build();
            ModernEvents.Quests.On(OnQuests);
            ModernEvents.QuestAdded.On(OnQuestAdded);
            ModernEvents.UserInformation.On(OnUserInformation);
            if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout += OnRelayout;
        }

        /// <summary>The tracked quest ids (QuestTrackingDialog.TrackedQuestsIds).</summary>
        public IReadOnlyList<int> Tracked => _tracked;

        public bool IsTracked(int questId) => _tracked.Contains(questId);

        /// <summary>QuestTrackingDialog.AddQuest / RemoveQuest + UpdateTrackedQuests (saved per character).</summary>
        public void Track(int questId, bool on)
        {
            if (on)
            {
                if (_tracked.Contains(questId) || _tracked.Count >= MaxTracked) return;
                _tracked.Add(questId);
            }
            else if (!_tracked.Remove(questId)) return;
            for (int j = 0; j < Settings.TrackedQuests.Length; j++)
                Settings.TrackedQuests[j] = j < _tracked.Count ? _tracked[j] : -1;
            if (GameScene.User != null) Settings.SaveTrackedQuests(GameScene.User.Name);
            Render();
        }

        private void OnQuests() => Render();

        private void OnQuestAdded((ClientQuestProgress Quest, bool Track) e)
        {
            if (e.Quest != null && e.Track) Track(e.Quest.Id, true);
            else Render();
        }

        /// <summary>A new character's ids (UserObject.Load reads QuestTracking.ini).</summary>
        private void OnUserInformation()
        {
            _tracked.Clear();
            for (int i = 0; i < Settings.TrackedQuests.Length; i++)
                if (Settings.TrackedQuests[i] >= 0 && !_tracked.Contains(Settings.TrackedQuests[i])) _tracked.Add(Settings.TrackedQuests[i]);
            Render();
        }

        private void OnRelayout(bool rebuild)
        {
            if (IsDisposed) return;
            if (rebuild) Build();
            else Place();
        }

        private void Build()
        {
            for (int i = Controls.Count - 1; i >= 0; i--) Controls[i].Dispose();
            _head = new TrackerHead(this) { Parent = this, Location = Point.Empty, Size = new Size(S(Width), S(20)), Hint = T(ClientTextKeys.ModernChatTrackFold) };
            _body = new TrackerBody(this) { Parent = this, Location = new Point(0, S(20)), Size = new Size(S(Width), S(20)) };
            _key = "\u0000";
            Render();
        }

        private static string T(ClientTextKeys k) => GameLanguage.ClientTextMap.GetLocalization(k);

        internal bool Folded => _shut;

        internal void Fold()
        {
            _shut = !_shut;
            _key = "\u0000";
            Render();
        }

        /// <summary>The tracked entries in tracking order (tracker.ts tracked()).</summary>
        internal List<ClientQuestProgress> Entries()
        {
            var list = new List<ClientQuestProgress>();
            List<ClientQuestProgress> current = GameScene.User?.CurrentQuests;
            if (current == null) return list;
            for (int t = 0; t < _tracked.Count; t++)
                for (int i = 0; i < current.Count; i++)
                    if (current[i] != null && current[i].Id == _tracked[t] && current[i].QuestInfo != null) { list.Add(current[i]); break; }
            return list;
        }

        /// <summary>Rebuilds the text when it changed (tracker.ts render's key).</summary>
        private void Render()
        {
            if (IsDisposed || _body == null) return;
            List<ClientQuestProgress> list = Entries();
            var sb = new System.Text.StringBuilder();
            sb.Append(_shut ? '1' : '0').Append(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                sb.Append('\u0002').Append(list[i].Id).Append('|').Append(list[i].Completed).Append('|');
                for (int j = 0; j < list[i].TaskList.Count; j++) sb.Append(list[i].TaskList[j]).Append('\u0001');
            }
            string key = sb.ToString();
            bool show = list.Count > 0;
            if (Visible != show) Visible = show;
            if (key == _key) return;
            _key = key;
            _head.Count = list.Count;
            _body.SetEntries(_shut ? new List<ClientQuestProgress>() : list);
            Size = new Size(S(Width), _head.Size.Height + (_shut ? 0 : _body.Size.Height));
            _body.Visible = !_shut;
            Place();
            _head.Invalidate();
        }

        private void Place()
        {
            Location = new Point(Settings.ScreenWidth - S(10) - Size.Width, Math.Max(0, (Settings.ScreenHeight - Size.Height) / 2));
        }

        /// <summary>The finishing NPC's name (FinishNPCIndex is its ObjectID).</summary>
        internal string NpcName(uint id)
        {
            if (MapControl.Objects.TryGetValue(id, out MapObject ob)) return ChatGfx.NpcName(ob.Name).Name;
            MapControl map = GameScene.Scene?.MapControl;
            ClientMapInfo info = map != null ? _data?.Map(map.Index) : null;
            if (info != null)
                for (int i = 0; i < info.NPCs.Count; i++)
                    if (info.NPCs[i].ObjectID == id) return ChatGfx.NpcName(info.NPCs[i].Name).Name;
            return T(ClientTextKeys.ModernChatTrackQuestGiver);
        }

        /// <summary>A quest click: the quest log when a later phase registers it.</summary>
        internal void OpenQuest(int questId)
        {
            WindowManager wm = ModernRoot.Instance?.Wm;
            if (wm != null && wm.IsRegistered("quests")) wm.Open("quests");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ModernEvents.Quests.Off(OnQuests);
                ModernEvents.QuestAdded.Off(OnQuestAdded);
                ModernEvents.UserInformation.Off(OnUserInformation);
                if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout -= OnRelayout;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>"QUESTS ─── n ▾" (tracker.ts .th): folds the list.</summary>
    internal sealed class TrackerHead : ModernControl
    {
        private readonly TrackerHud _hud;
        private int _count;

        public TrackerHead(TrackerHud hud)
        {
            _hud = hud;
            Sound = SoundList.ButtonA;
        }

        public int Count { get => _count; set { if (_count == value) return; _count = value; Invalidate(); } }

        public override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            _hud.Fold();
        }

        public override void OnMouseDoubleClick(MouseEventArgs e) => OnMouseClick(e);

        protected override void Paint(Graphics g)
        {
            float k = K;
            Font f = ModernSkin.Font(10, true);
            string title = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.Quests).ToUpperInvariant();
            float spacing = 10 * 0.18f * k, x = 0, y = (Size.Height - f.Height) / 2f;
            for (int i = 0; i < title.Length; i++)
            {
                string ch = title[i].ToString();
                ChatGfx.ShadowText(g, ch, f, Color.FromArgb(191, 232, 237, 245), x, y, 1f, 0.8f);
                x += ChatGfx.Measure(ch, f) + spacing;
            }
            x += S(6) - spacing;
            string count = _count + " " + (_hud.Folded ? "▸" : "▾");
            float cw = ChatGfx.Measure(count, f);
            // the line fills the space between the title and the count (.th::after: gold → transparent)
            float lineEnd = Size.Width - cw - S(6);
            if (lineEnd > x)
            {
                using var brush = new LinearGradientBrush(new RectangleF(x - 1, 0, lineEnd - x + 2, 1), Color.FromArgb(140, 240, 200, 110), Color.Transparent, LinearGradientMode.Horizontal);
                g.FillRectangle(brush, x, Size.Height / 2f, lineEnd - x, Math.Max(1f, k));
            }
            ChatGfx.ShadowText(g, count, f, Color.FromArgb(153, 232, 237, 245), Size.Width - cw, y, 1f, 0.8f);
        }
    }

    /// <summary>The quest blocks (tracker.ts .tq / .tn / .to / .tr), painted once per change; clicks open the quest log.</summary>
    internal sealed class TrackerBody : ModernControl
    {
        private static readonly Regex Count = new Regex(@"^(.*?)[\s:]*\(?(\d+)\s*/\s*(\d+)\)?\s*$");

        private readonly TrackerHud _hud;
        private readonly List<(int Quest, float Top, float Bottom)> _blocks = new List<(int, float, float)>();
        private List<ClientQuestProgress> _entries = new List<ClientQuestProgress>();

        public TrackerBody(TrackerHud hud)
        {
            _hud = hud;
            Sound = SoundList.ButtonA;
            Hint = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernChatTrackOpenLog);
        }

        public void SetEntries(List<ClientQuestProgress> entries)
        {
            _entries = entries ?? new List<ClientQuestProgress>();
            float k = K, y = 0;
            float nameH = 12.5f * k * 1.35f, lineH = 11.5f * k * 1.55f;
            for (int i = 0; i < _entries.Count; i++)
            {
                y += 5 * k + nameH;
                y += _entries[i].Completed ? lineH : _entries[i].TaskList.Count * lineH;
                y += 3 * k;
            }
            Size = new Size(Size.Width, Math.Max(1, (int)Math.Ceiling(y)));
            Invalidate();
        }

        public override void OnMouseClick(MouseEventArgs e)
        {
            float my = CMain.MPoint.Y - DisplayLocation.Y;
            for (int i = 0; i < _blocks.Count; i++)
            {
                if (my < _blocks[i].Top || my >= _blocks[i].Bottom) continue;
                base.OnMouseClick(e);
                _hud.OpenQuest(_blocks[i].Quest);
                return;
            }
        }

        protected override void Paint(Graphics g)
        {
            _blocks.Clear();
            float k = K, y = 0, w = Size.Width;
            Font name = ModernSkin.Font(12.5f, true), task = ModernSkin.Font(11.5f), taskBold = ModernSkin.Font(11.5f, true), tick = ModernSkin.Font(10, true);
            float nameH = 12.5f * k * 1.35f, lineH = 11.5f * k * 1.55f;
            Color text = Color.FromArgb(232, 237, 245), ok = ModernTheme.Hex("#ff5a4a"), ready = ModernTheme.Hex("#5fe07a");
            for (int i = 0; i < _entries.Count; i++)
            {
                ClientQuestProgress e = _entries[i];
                float top = y;
                y += 5 * k;
                GoldText(g, e.QuestInfo.Name, name, 0, y + (nameH - name.Height) / 2f, w);
                y += nameH;
                if (e.Completed)
                {
                    string line = string.Format(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernChatTrackReady), _hud.NpcName(e.QuestInfo.FinishNPCIndex));
                    ChatGfx.ShadowText(g, "✓", tick, ready, 0, y + (lineH - tick.Height) / 2f);
                    Clip(g, line, taskBold, ready, 12 * k, y + (lineH - taskBold.Height) / 2f, w - 12 * k);
                    y += lineH;
                }
                else
                    for (int t = 0; t < e.TaskList.Count; t++)
                    {
                        string s = e.TaskList[t] ?? string.Empty;
                        Match m = Count.Match(s);
                        int have = 0, need = 0;
                        if (m.Success) { int.TryParse(m.Groups[2].Value, out have); int.TryParse(m.Groups[3].Value, out need); }
                        bool done = m.Success && need > 0 && have >= need;
                        Color c = done ? ok : text;
                        float ty = y + (lineH - task.Height) / 2f;
                        if (done) ChatGfx.ShadowText(g, "✓", tick, ok, 0, y + (lineH - tick.Height) / 2f);
                        else
                        {
                            // the diamond bullet (.to::before)
                            GraphicsState st = g.Save();
                            g.TranslateTransform(5 * k, y + lineH * 0.5f);
                            g.RotateTransform(45);
                            using (var b = new SolidBrush(Color.FromArgb(179, 232, 237, 245))) g.FillRectangle(b, -2 * k, -2 * k, 4 * k, 4 * k);
                            g.Restore(st);
                        }
                        string count = m.Success && need > 0 ? Math.Min(have, need) + "/" + need : null;
                        float cw = count != null ? ChatGfx.Measure(count, taskBold) : 0;
                        if (count != null) ChatGfx.ShadowText(g, count, taskBold, c, w - cw, ty);
                        Clip(g, m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : s, task, c, 12 * k, ty, w - 12 * k - (cw > 0 ? cw + 6 * k : 0));
                        y += lineH;
                    }
                y += 3 * k;
                _blocks.Add((e.Id, top, y));
            }
        }

        /// <summary>One line cut to its width with an ellipsis.</summary>
        private static void Clip(Graphics g, string text, Font f, Color c, float x, float y, float width)
        {
            if (width <= 0) return;
            string s = text;
            if (ChatGfx.Measure(s, f) > width)
            {
                int n = s.Length;
                while (n > 1 && ChatGfx.Measure(s.Substring(0, n) + "…", f) > width) n--;
                s = s.Substring(0, n) + "…";
            }
            ChatGfx.ShadowText(g, s, f, c, x, y);
        }

        /// <summary>The gold title (tracker.ts .tn .g: a gradient clipped to the text, its shadow a copy underneath).</summary>
        private static void GoldText(Graphics g, string text, Font f, float x, float y, float width)
        {
            if (string.IsNullOrEmpty(text)) return;
            string s = text;
            if (ChatGfx.Measure(s, f) > width)
            {
                int n = s.Length;
                while (n > 1 && ChatGfx.Measure(s.Substring(0, n) + "…", f) > width) n--;
                s = s.Substring(0, n) + "…";
            }
            float tw = Math.Max(1f, ChatGfx.Measure(s, f));
            float k = ModernRoot.K;
            using (var shadow = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
            {
                g.DrawString(s, f, shadow, x, y + k, ChatGfx.Format);
                g.DrawString(s, f, shadow, x + k * 0.5f, y + k * 1.5f, ChatGfx.Format);
            }
            using var brush = new LinearGradientBrush(new RectangleF(x - 1, y, tw + 2, f.Height), Color.White, Color.White, 100f);
            brush.InterpolationColors = new ColorBlend
            {
                Colors = new[]
                {
                    ModernTheme.Hex("#a8741c"), ModernTheme.Hex("#e8b84a"), ModernTheme.Hex("#fff4c2"), ModernTheme.Hex("#f0c860"),
                    ModernTheme.Hex("#b8862b"), ModernTheme.Hex("#e8b84a"),
                },
                Positions = new[] { 0f, 0.30f, 0.45f, 0.55f, 0.75f, 1f },
            };
            g.DrawString(s, f, brush, x, y, ChatGfx.Format);
        }
    }
}

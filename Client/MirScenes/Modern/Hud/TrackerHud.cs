// Ported from WebClient/src/ui/modern/hud/tracker.ts (QuestTracker): the tracked quests on the right of the screen, the
// desktop's QuestTrackingDialog. Tracking (at most 5) stays client-side like the classic tracker: Settings.TrackedQuests per
// character (QuestTracking.ini, Settings.SaveTrackedQuests); a run the server adds with TrackHint is tracked (ModernEvents.QuestAdded).
// Quest V2 (Part B phase 3): the data is GameScene.Models.Quests: the quest name in gold, then the current step's objectives
// (client-built text, QuestText.Label) with their count and a short bar; "Ready: return to <NPC>" once the run is Ready; "Failed"
// for a failed run; the quest / step time left when the run has a limit (refreshed once a second). Ids of runs that ended are
// dropped once the login picture is in (Models.Quests.Settled). The header folds the list (Settings.UiTrackerFolded); a quest
// click opens the "quests" window on it. Hidden while nothing is tracked. Repainted only when its text key changes.
// Desktop idiom: one surface on the HUD layer; long lines end in an ellipsis (the web scrolls them back and forth).
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirSounds;
using Client.MirScenes.Models;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Windows;
using S = ServerPackets;

namespace Client.MirScenes.Modern.Hud
{
    public sealed class TrackerHud : ModernSurface
    {
        public const int MaxTracked = 5;
        private const int Width = 220;
        private static bool _shut = Settings.UiTrackerFolded;

        private readonly BigMapData _data;
        private readonly List<int> _tracked = new List<int>();
        private TrackerHead _head;
        private TrackerBody _body;
        private string _key = "\u0000";
        private bool _timers, _saveDue;
        private long _nextTick;
        private readonly Action _quests;

        public TrackerHud(BigMapData data)
        {
            _data = data;
            PassThrough = true;
            Visible = false;
            Parent = ModernRoot.Instance?.Hud;
            LoadTracked();
            Build();
            _quests = Render;
            ModernEvents.Quests.On(_quests);
            ModernEvents.QuestAdded.On(OnQuestAdded);
            ModernEvents.UserInformation.On(OnUserInformation);
            if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout += OnRelayout;
        }

        /// <summary>The tracked quest ids (QuestTrackingDialog.TrackedQuestsIds).</summary>
        public IReadOnlyList<int> Tracked => _tracked;

        public bool IsTracked(int questId) => _tracked.Contains(questId);

        /// <summary>QuestTrackingDialog.AddQuest / RemoveQuest + UpdateTrackedQuests (saved per character). A player's click saves
        /// at once; changes from packets (TrackHint, ended runs) only update Settings.TrackedQuests and are written on the next click
        /// or when the tracker goes (no file I/O in the packet path).</summary>
        public void Track(int questId, bool on, bool save = true)
        {
            if (on)
            {
                if (_tracked.Contains(questId) || _tracked.Count >= MaxTracked) return;
                _tracked.Add(questId);
            }
            else if (!_tracked.Remove(questId)) return;
            SyncSettings(save);
            Render();
        }

        private void SyncSettings(bool save)
        {
            for (int j = 0; j < Settings.TrackedQuests.Length; j++)
                Settings.TrackedQuests[j] = j < _tracked.Count ? _tracked[j] : -1;
            if (!save) { _saveDue = true; return; }
            _saveDue = false;
            if (GameScene.User != null) Settings.SaveTrackedQuests(GameScene.User.Name);
        }

        private void LoadTracked()
        {
            _tracked.Clear();
            for (int i = 0; i < Settings.TrackedQuests.Length; i++)
                if (Settings.TrackedQuests[i] >= 0 && !_tracked.Contains(Settings.TrackedQuests[i])) _tracked.Add(Settings.TrackedQuests[i]);
        }

        private void OnQuestAdded((int Quest, bool Track) e)
        {
            if (e.Track) Track(e.Quest, true, false);
            else Render();
        }

        /// <summary>A new character's ids (UserObject.Load reads QuestTracking.ini).</summary>
        private void OnUserInformation()
        {
            LoadTracked();
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
            Settings.UiTrackerFolded = _shut;
            _key = "\u0000";
            Render();
        }

        /// <summary>Time limits tick: re-render once a second while a tracked run has one (the key carries the seconds).</summary>
        public override void Process()
        {
            if (!_timers || CMain.Time < _nextTick) return;
            _nextTick = CMain.Time + 1000;
            Render();
        }

        /// <summary>The tracked runs in tracking order whose definition has arrived (tracker.ts tracked()).</summary>
        internal List<TrackerEntry> Entries()
        {
            var list = new List<TrackerEntry>();
            QuestBook book = GameScene.Models?.Quests;
            if (book == null) return list;
            // a run that ended (turned in, abandoned) leaves the tracker once the login picture is in
            if (book.Settled)
            {
                bool pruned = false;
                for (int t = _tracked.Count - 1; t >= 0; t--)
                    if (book.Run(_tracked[t]) == null) { _tracked.RemoveAt(t); pruned = true; }
                if (pruned) SyncSettings(false);
            }
            for (int t = 0; t < _tracked.Count; t++)
            {
                QuestRun run = book.Run(_tracked[t]);
                S.QuestDefinition def = run != null ? book.Def(run.Quest) : null;
                if (def == null) continue;
                var e = new TrackerEntry { Quest = run.Quest, Name = def.Name, Status = run.Status };
                if (run.Status == QuestRunStatus.Ready)
                {
                    string npc = QuestText.EndpointName(def, QuestEndpointRole.Finish);
                    e.Lines.Add(new TrackerLine { Text = npc.Length > 0 ? string.Format(T(ClientTextKeys.ModernChatTrackReady), npc) : T(ClientTextKeys.ModernQuestReadyTurnIn), Done = true });
                }
                else if (run.Status == QuestRunStatus.Failed)
                    e.Lines.Add(new TrackerLine { Text = T(ClientTextKeys.ModernQuestFailed), Failed = true });
                else if (run.Step >= 0)
                {
                    var objectives = new List<S.QuestDefinition.Objective>();
                    QuestBook.ObjectivesOf(def, run.Step, objectives);
                    for (int i = 0; i < objectives.Count; i++)
                    {
                        S.QuestDefinition.Objective o = objectives[i];
                        if (!book.Shown(run.Quest, o)) continue;
                        int value = book.Value(run.Quest, o.Index), need = Math.Max(1, o.Count);
                        e.Lines.Add(new TrackerLine
                        {
                            Text = QuestText.Label(o), Have = Math.Min(value, need), Need = need, Bar = QuestText.Countable(o), Done = value >= need, Optional = o.Optional,
                        });
                    }
                    if (book.ChoiceDue(run.Quest))
                    {
                        string npc = QuestText.EndpointName(def, QuestEndpointRole.Finish);
                        e.Lines.Add(new TrackerLine { Text = string.Format(T(ClientTextKeys.ModernQuestChooseAt), npc.Length > 0 ? npc : T(ClientTextKeys.ModernChatTrackQuestGiver)), Done = true });
                    }
                    long step = QuestText.SecondsLeft(run.StepExpiresAt), quest = QuestText.SecondsLeft(run.ExpiresAt);
                    long left = step >= 0 && (quest < 0 || step < quest) ? step : quest;
                    if (left >= 0) e.TimeLeft = left;
                }
                list.Add(e);
            }
            return list;
        }

        /// <summary>Rebuilds the text when it changed (tracker.ts render's key).</summary>
        private void Render()
        {
            if (IsDisposed || _body == null) return;
            List<TrackerEntry> list = Entries();
            var sb = new System.Text.StringBuilder();
            sb.Append(_shut ? '1' : '0').Append(list.Count);
            bool timers = false;
            for (int i = 0; i < list.Count; i++)
            {
                TrackerEntry e = list[i];
                sb.Append('\u0002').Append(e.Quest).Append('|').Append((int)e.Status).Append('|').Append(e.Name).Append('|').Append(e.TimeLeft).Append('|');
                if (e.TimeLeft >= 0) timers = true;
                for (int j = 0; j < e.Lines.Count; j++) sb.Append(e.Lines[j].Text).Append(':').Append(e.Lines[j].Have).Append('/').Append(e.Lines[j].Need).Append('\u0001');
            }
            _timers = timers;
            string key = sb.ToString();
            bool show = list.Count > 0;
            if (Visible != show) Visible = show;
            if (key == _key) return;
            _key = key;
            _head.Count = list.Count;
            _body.SetEntries(_shut ? new List<TrackerEntry>() : list);
            Size = new Size(S(Width), _head.Size.Height + (_shut ? 0 : _body.Size.Height));
            _body.Visible = !_shut;
            Place();
            _head.Invalidate();
        }

        private void Place()
        {
            Location = new Point(Settings.ScreenWidth - S(10) - Size.Width, Math.Max(0, (Settings.ScreenHeight - Size.Height) / 2));
        }

        /// <summary>A quest click: the quest log on that quest.</summary>
        internal void OpenQuest(int questId)
        {
            WindowManager wm = ModernRoot.Instance?.Wm;
            if (wm == null || !wm.IsRegistered(QuestsWindow.WindowKey)) return;
            wm.Open(QuestsWindow.WindowKey);
            (wm.Get(QuestsWindow.WindowKey) as QuestsWindow)?.FocusQuest(questId);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_saveDue && GameScene.User != null) Settings.SaveTrackedQuests(GameScene.User.Name);
                ModernEvents.Quests.Off(_quests);
                ModernEvents.QuestAdded.Off(OnQuestAdded);
                ModernEvents.UserInformation.Off(OnUserInformation);
                if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout -= OnRelayout;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>One tracked quest as the tracker paints it (built on a change only).</summary>
    internal sealed class TrackerEntry
    {
        public int Quest;
        public string Name;
        public QuestRunStatus Status;
        public long TimeLeft = -1;
        public readonly List<TrackerLine> Lines = new List<TrackerLine>();
    }

    internal sealed class TrackerLine
    {
        public string Text;
        public int Have, Need;
        public bool Bar, Done, Failed, Optional;
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
        private readonly TrackerHud _hud;
        private readonly List<(int Quest, float Top, float Bottom)> _blocks = new List<(int, float, float)>();
        private List<TrackerEntry> _entries = new List<TrackerEntry>();

        public TrackerBody(TrackerHud hud)
        {
            _hud = hud;
            Sound = SoundList.ButtonA;
            Hint = GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernChatTrackOpenLog);
        }

        private static float NameH(float k) => 12.5f * k * 1.35f;
        private static float LineH(float k) => 11.5f * k * 1.55f;
        private static float BarH(float k) => 5 * k;

        public void SetEntries(List<TrackerEntry> entries)
        {
            _entries = entries ?? new List<TrackerEntry>();
            float k = K, y = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                y += 5 * k + NameH(k);
                for (int j = 0; j < _entries[i].Lines.Count; j++) y += LineH(k) + (_entries[i].Lines[j].Bar ? BarH(k) : 0);
                if (_entries[i].TimeLeft >= 0) y += LineH(k);
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
            float nameH = NameH(k), lineH = LineH(k);
            Color text = Color.FromArgb(232, 237, 245), ok = ModernTheme.Hex("#ff5a4a"), ready = ModernTheme.Hex("#5fe07a"), bad = ModernTheme.Hex("#ff6b5a");
            for (int i = 0; i < _entries.Count; i++)
            {
                TrackerEntry e = _entries[i];
                float top = y;
                y += 5 * k;
                GoldText(g, e.Name, name, 0, y + (nameH - name.Height) / 2f, w);
                y += nameH;
                for (int t = 0; t < e.Lines.Count; t++)
                {
                    TrackerLine l = e.Lines[t];
                    float ty = y + (lineH - task.Height) / 2f;
                    if (l.Failed)
                    {
                        ChatGfx.ShadowText(g, "✗", tick, bad, 0, y + (lineH - tick.Height) / 2f);
                        Clip(g, l.Text, taskBold, bad, 12 * k, ty, w - 12 * k);
                        y += lineH;
                        continue;
                    }
                    if (e.Status == QuestRunStatus.Ready || (l.Done && !l.Bar && l.Need <= 0))
                    {
                        ChatGfx.ShadowText(g, "✓", tick, ready, 0, y + (lineH - tick.Height) / 2f);
                        Clip(g, l.Text, taskBold, ready, 12 * k, ty, w - 12 * k);
                        y += lineH;
                        continue;
                    }
                    Color c = l.Done ? ok : l.Optional ? Color.FromArgb(170, 232, 237, 245) : text;
                    if (l.Done) ChatGfx.ShadowText(g, "✓", tick, ok, 0, y + (lineH - tick.Height) / 2f);
                    else
                    {
                        // the diamond bullet (.to::before)
                        GraphicsState st = g.Save();
                        g.TranslateTransform(5 * k, y + lineH * 0.5f);
                        g.RotateTransform(45);
                        using (var b = new SolidBrush(Color.FromArgb(179, 232, 237, 245))) g.FillRectangle(b, -2 * k, -2 * k, 4 * k, 4 * k);
                        g.Restore(st);
                    }
                    string count = l.Bar ? l.Have + "/" + l.Need : null;
                    float cw = count != null ? ChatGfx.Measure(count, taskBold) : 0;
                    if (count != null) ChatGfx.ShadowText(g, count, taskBold, c, w - cw, ty);
                    Clip(g, l.Text, task, c, 12 * k, ty, w - 12 * k - (cw > 0 ? cw + 6 * k : 0));
                    y += lineH;
                    if (l.Bar)
                    {
                        QuestGfx.Bar(g, new RectangleF(12 * k, y, w - 12 * k, 3 * k), l.Need > 0 ? l.Have / (float)l.Need : 0f, l.Done, true);
                        y += BarH(k);
                    }
                }
                if (e.TimeLeft >= 0)
                {
                    string left = string.Format(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernQuestTimeLeft), QuestGfx.Clock(e.TimeLeft));
                    Color tc = e.TimeLeft <= 60 ? bad : Color.FromArgb(200, 240, 200, 110);
                    Clip(g, left, task, tc, 12 * k, y + (lineH - task.Height) / 2f, w - 12 * k);
                    y += lineH;
                }
                y += 3 * k;
                _blocks.Add((e.Quest, top, y));
            }
        }

        /// <summary>One line cut to its width with an ellipsis.</summary>
        private static void Clip(Graphics g, string text, Font f, Color c, float x, float y, float width)
        {
            if (width <= 0) return;
            string s = text ?? string.Empty;
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

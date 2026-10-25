// Quest V2 pieces shared by the new UI's quest log (QuestsWindow), NPC window quest tab (NpcWindow), tracker (TrackerHud) and
// progress toasts (QuestToastHud): objective bars, timers, category names and icons, the reward rows with choice groups, and
// the accept / turn-in / choose / abandon sends (Part B "Wire protocol"). Ported in spirit from WebClient/src/ui/modern/windows/quests.ts.
using System.Drawing.Drawing2D;
using Client.MirControls;
using Client.MirNetwork;
using Client.MirScenes.Models;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using S = ServerPackets;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    internal static class QuestGfx
    {
        /// <summary>A thin objective bar: dark track, gold fill (green once done).</summary>
        public static void Bar(Graphics g, RectangleF r, float frac, bool done, bool hud = false)
        {
            if (r.Width <= 1 || r.Height <= 0) return;
            frac = Math.Clamp(frac, 0f, 1f);
            float rad = r.Height / 2f;
            using (GraphicsPath p = ModernSkin.RoundRect(r, rad))
            using (var br = new SolidBrush(hud ? Color.FromArgb(150, 0, 0, 0) : Color.FromArgb(160, 10, 12, 18)))
                g.FillPath(br, p);
            if (frac <= 0f) return;
            RectangleF f = new RectangleF(r.X, r.Y, Math.Max(r.Height, r.Width * frac), r.Height);
            Color a = done ? ModernTheme.Hex("#3fbf5f") : ModernTheme.Hex("#b8862b"), b = done ? ModernTheme.Hex("#7ff09a") : ModernTheme.Hex("#f0c860");
            using (GraphicsPath p = ModernSkin.RoundRect(f, rad))
            using (var br = new LinearGradientBrush(new RectangleF(f.X - 1, f.Y, f.Width + 2, f.Height), a, b, LinearGradientMode.Horizontal))
                g.FillPath(br, p);
        }

        /// <summary>A countdown: 1:02:03 or 4:05.</summary>
        public static string Clock(long secs)
        {
            if (secs < 0) secs = 0;
            long h = secs / 3600, m = secs / 60 % 60, s = secs % 60;
            return h > 0 ? h + ":" + m.ToString("00") + ":" + s.ToString("00") : m + ":" + s.ToString("00");
        }

        public static string CategoryName(QuestCategory c) => W.T(c switch
        {
            QuestCategory.Story => ClientTextKeys.ModernSocialQTypeStory,
            QuestCategory.Daily => ClientTextKeys.ModernSocialQTypeDaily,
            QuestCategory.Weekly => ClientTextKeys.ModernQuestCatWeekly,
            QuestCategory.Repeatable => ClientTextKeys.ModernSocialQTypeRepeatable,
            QuestCategory.Event => ClientTextKeys.ModernQuestCatEvent,
            QuestCategory.Guild => ClientTextKeys.ModernQuestCatGuild,
            _ => ClientTextKeys.ModernSocialQTypeGeneral,
        });

        public static int CategoryIcon(QuestCategory c) => c switch
        {
            QuestCategory.Story => ModernIcons.Star,
            QuestCategory.Daily or QuestCategory.Weekly => ModernIcons.Sun,
            QuestCategory.Repeatable => ModernIcons.Repeat,
            _ => ModernIcons.Scroll,
        };

        /// <summary>The order the log groups categories in.</summary>
        public static readonly QuestCategory[] Categories =
        {
            QuestCategory.Story, QuestCategory.Side, QuestCategory.Daily, QuestCategory.Weekly, QuestCategory.Repeatable, QuestCategory.Event, QuestCategory.Guild,
        };

        /// <summary>Hidden-category quests and HiddenInLog ones stay out of the log.</summary>
        public static bool InLog(S.QuestDefinition def) => def != null && def.Category != QuestCategory.Hidden && (def.Flags & QuestFlags.HiddenInLog) == 0;
    }

    /// <summary>
    /// The player's reward picks per quest (choice group → position in the definition's Rewards list) and the V2 sends. One
    /// instance for the session (the quest log and the NPC window share the picks).
    /// </summary>
    internal static class QuestActions
    {
        private static readonly Dictionary<int, Dictionary<int, int>> _picks = new Dictionary<int, Dictionary<int, int>>();
        private static readonly Dictionary<int, (long Until, long Sig)> _pending = new Dictionary<int, (long, long)>();
        private const int PendingMs = 5000;

        public static void Reset()
        {
            _picks.Clear();
            _pending.Clear();
        }

        public static int Pick(int quest, int group) => _picks.TryGetValue(quest, out var d) && d.TryGetValue(group, out int p) ? p : -1;

        public static void SetPick(int quest, int group, int position)
        {
            if (!_picks.TryGetValue(quest, out var d)) _picks[quest] = d = new Dictionary<int, int>();
            d[group] = position;
        }

        /// <summary>A quest's buttons lock until its run / history changes (or 5 s: a refusal sends nothing back).</summary>
        public static bool IsPending(int quest) =>
            _pending.TryGetValue(quest, out var p) && p.Until > CMain.Time && p.Sig == Signature(quest);

        private static void Lock(int quest) => _pending[quest] = (CMain.Time + PendingMs, Signature(quest));

        /// <summary>What a reply changes: the run's status and step, the times completed.</summary>
        private static long Signature(int quest)
        {
            QuestBook book = GameScene.Models?.Quests;
            if (book == null) return 0;
            QuestRun run = book.Run(quest);
            long sig = run == null ? 0 : ((long)run.Status << 40) ^ ((long)(run.Step + 2) << 8);
            return sig ^ ((long)(book.Done(quest)?.Times ?? 0) << 48) ^ (run == null ? 1 : 0);
        }

        /// <summary>The choice groups this definition offers at turn-in, ascending.</summary>
        public static void ChoiceGroups(S.QuestDefinition def, bool completedBefore, List<int> into)
        {
            into.Clear();
            if (def == null) return;
            for (int i = 0; i < def.Rewards.Count; i++)
            {
                S.QuestDefinition.Reward r = def.Rewards[i];
                if (r.ChoiceGroup == 0 || !QuestText.RewardShown(r, completedBefore) || into.Contains(r.ChoiceGroup)) continue;
                into.Add(r.ChoiceGroup);
            }
            into.Sort();
        }

        /// <summary>True when every choice group has a pick (turn-in allowed).</summary>
        public static bool PicksComplete(S.QuestDefinition def, bool completedBefore)
        {
            var groups = new List<int>();
            ChoiceGroups(def, completedBefore, groups);
            for (int i = 0; i < groups.Count; i++) if (Pick(def.Quest, groups[i]) < 0) return false;
            return true;
        }

        public static void Accept(int quest, uint npc)
        {
            if (IsPending(quest)) return;
            Lock(quest);
            Network.Enqueue(new C.QuestAccept { Quest = quest, NpcObjectID = npc });
        }

        public static void TurnIn(S.QuestDefinition def, uint npc, bool completedBefore)
        {
            if (def == null || IsPending(def.Quest)) return;
            var groups = new List<int>();
            ChoiceGroups(def, completedBefore, groups);
            var picks = new List<int>();
            for (int i = 0; i < groups.Count; i++)
            {
                int p = Pick(def.Quest, groups[i]);
                if (p >= 0) picks.Add(p);
            }
            Lock(def.Quest);
            Network.Enqueue(new C.QuestTurnIn { Quest = def.Quest, NpcObjectID = npc, RewardPicks = picks });
        }

        public static void Choose(int quest, uint npc, string key)
        {
            if (IsPending(quest)) return;
            Lock(quest);
            Network.Enqueue(new C.QuestChoose { Quest = quest, NpcObjectID = npc, ChoiceKey = key ?? string.Empty });
        }

        public static void Abandon(int quest)
        {
            if (IsPending(quest)) return;
            Lock(quest);
            Network.Enqueue(new C.QuestAbandon { Quest = quest });
        }

    }

    /// <summary>
    /// The reward block of a quest (quest scope: EXP / gold / credit chips, fixed item cells, one "choose one" row per choice group
    /// with selectable cells when <c>pickable</c>). Lays out into a parent at HUD px and returns the bottom.
    /// </summary>
    internal static class QuestRewardsView
    {
        public static int Build(MirControl parent, S.QuestDefinition def, bool completedBefore, bool pickable, int x0, int y, int w, Action changed)
        {
            int x = x0;
            bool any = false;
            long exp = 0, gold = 0, credit = 0;
            var fixedItems = new List<S.QuestDefinition.Reward>();
            var groups = new List<int>();
            QuestActions.ChoiceGroups(def, completedBefore, groups);
            for (int i = 0; i < def.Rewards.Count; i++)
            {
                S.QuestDefinition.Reward r = def.Rewards[i];
                if (!QuestText.RewardShown(r, completedBefore) || r.ChoiceGroup != 0) continue;
                switch (r.Kind)
                {
                    case QuestRewardKind.Exp: exp += r.Amount; break;
                    case QuestRewardKind.Gold: gold += r.Amount; break;
                    case QuestRewardKind.Credit: credit += r.Amount; break;
                    case QuestRewardKind.Item: fixedItems.Add(r); break;
                }
            }
            void ChipBox(string k, long v)
            {
                string val = W.N(v);
                float cw = (ModernSkin.Measure(k, ModernSkin.Font(10, true)) + ModernSkin.Measure(val, ModernSkin.Font(12, true))) / ModernRoot.K + 30;
                int iw = (int)Math.Ceiling(cw);
                if (x + iw > x0 + w && x > x0) { x = x0; y += 30; }
                new PaintBox((g, b) =>
                {
                    float kk = ModernRoot.K;
                    RectangleF rr = new RectangleF(0.5f, 0.5f, b.Size.Width - 1, b.Size.Height - 1);
                    using (GraphicsPath p = ModernSkin.RoundRect(rr, b.Size.Height / 2f))
                    {
                        using (var br = new SolidBrush(W.Over(ModernTheme.FrameLight, 0.08f))) g.FillPath(br, p);
                        ModernSkin.Stroke(g, p, W.Over(ModernTheme.Frame, 0.7f));
                    }
                    float kw = ModernSkin.Measure(k, ModernSkin.Font(10, true)) + 2;
                    ModernSkin.Text(g, k, ModernSkin.Font(10, true), ModernTheme.Dim, new RectangleF(10 * kk, 0, kw, b.Size.Height));
                    ModernSkin.Text(g, val, ModernSkin.Font(12, true), ModernTheme.Text, new RectangleF(10 * kk + kw + 6 * kk, 0, b.Size.Width - kw - 22 * kk, b.Size.Height));
                })
                { Parent = parent, Location = new Point(W.S(x), W.S(y + 4)), Size = new Size(W.S(iw), W.S(24)), NotControl = true };
                x += iw + 6;
                any = true;
            }
            if (exp > 0) ChipBox(W.T(ClientTextKeys.ModernSocialQExp), exp);
            if (gold > 0) ChipBox(W.T(ClientTextKeys.ModernSocialQGold), gold);
            if (credit > 0) ChipBox(W.T(ClientTextKeys.ModernSocialQCredit), credit);
            for (int i = 0; i < fixedItems.Count; i++)
            {
                if (x + 36 > x0 + w) { x = x0; y += 42; }
                if (Cell(parent, fixedItems[i], x, y, false, false, null)) { x += 42; any = true; }
            }
            if (any) y += x > x0 ? 40 : 0;
            if (!any && groups.Count == 0)
            {
                W.Label(parent, W.T(ClientTextKeys.ModernSocialQNoReward), x0, y + 4, w, 18, 11, false, ModernTheme.Dim);
                y += 26;
            }
            for (int gi = 0; gi < groups.Count; gi++)
            {
                int group = groups[gi];
                int picked = QuestActions.Pick(def.Quest, group);
                W.Caption(parent, W.T(pickable ? ClientTextKeys.ModernQuestChooseOneNow : ClientTextKeys.ModernSocialQChooseOne), x0, y + 4, w);
                y += 22;
                x = x0;
                for (int i = 0; i < def.Rewards.Count; i++)
                {
                    S.QuestDefinition.Reward r = def.Rewards[i];
                    if (r.ChoiceGroup != group || !QuestText.RewardShown(r, completedBefore)) continue;
                    if (x + 36 > x0 + w) { x = x0; y += 42; }
                    int pos = i;
                    Action click = pickable ? () => { QuestActions.SetPick(def.Quest, group, pos); changed?.Invoke(); } : null;
                    if (Cell(parent, r, x, y, pickable, picked == pos, click)) x += 42;
                    else if (r.Kind != QuestRewardKind.Item)
                    {
                        // a currency in a choice group: a small selectable chip
                        string label = (r.Kind == QuestRewardKind.Exp ? W.T(ClientTextKeys.ModernSocialQExp) : r.Kind == QuestRewardKind.Gold ? W.T(ClientTextKeys.ModernSocialQGold) : W.T(ClientTextKeys.ModernSocialQCredit)) + " " + W.N(r.Amount);
                        var b = new ModernButton(label, picked == pos ? ButtonKind.Main : ButtonKind.Normal) { Parent = parent, FontSize = 10 };
                        b.AutoWidth(0);
                        b.Location = new Point(W.S(x), W.S(y + 4));
                        if (click != null) b.Click += (o, e) => click();
                        else b.SetDisabled(true, null);
                        x += (int)Math.Round(b.Size.Width / ModernRoot.K) + 6;
                    }
                }
                y += 42;
            }
            return y;
        }

        /// <summary>An item reward cell (false when the item info has not arrived yet: it is requested).</summary>
        private static bool Cell(MirControl parent, S.QuestDefinition.Reward r, int x, int y, bool pickable, bool selected, Action click)
        {
            if (r.Kind != QuestRewardKind.Item) return false;
            ItemInfo info = QuestText.ItemInfo(r.TargetId);
            if (info == null) return false;
            ushort count = (ushort)Math.Clamp(r.Amount, 1, ushort.MaxValue);
            var shown = new UserItem(info) { Count = count, CurrentDura = info.Durability, MaxDura = info.Durability };
            var cell = new NpcItemCell(36, () => shown) { Parent = parent, Location = new Point(W.S(x), W.S(y)), CountOverride = count };
            if (pickable) cell.SetState(selected, false);
            if (click != null) cell.Click += (o, e) => click();
            return true;
        }
    }
}

// Ported from WebClient/src/ui/modern/windows/relationship.ts (RelationshipWindow): the new UI's marriage window over
// GameScene.Models.Relationship (S.LoverUpdate: lover name, date, map — empty while offline — and days), Models.AllowToggles
// (Marriage, LoverRecall), Models.ServerRules (the marriage numbers), the user's left ring (UserItem.WeddingRing) and buffs (the
// Lover buff). The classic RelationshipDialog's refusals are kept (married / not married, the lover offline). Banner: your medal,
// the heart with the days together (or the divorce cooldown / "Single"), the lover's medal (class / level only while in view).
// Married: days / wedding day / online stats, the recall checklist and "Recall <lover>" (@recalllover), Lover chat (":)"
// prefill), Mail (the mail window's ComposeTo), Request divorce (asks, then C.DivorceRequest: the lover must face you and
// agree) and the Allow lover recall switch. Single: the proposal checklist (your level, the cooldown, the player you face and
// whether they face you), Propose (C.MarriageRequest to the player you face) and the Allow marriage requests switch. The
// switches send C.SetAllowToggle (locked until S.AllowToggles); a server without toggles: the classic C.ChangeMarriage (it
// flips Marriage while single and LoverRecall while married). Right: the marriage details and how it works. The faced player,
// the lover in view, the Lover buff, the ring and your level are checked once a second. The incoming marriage / divorce
// requests are ModernUi's request toasts. HIDDEN as on the web: memories, letters, recall count / cooldown, the lover's ring.
using Client.MirControls;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Models;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Windows
{
    public sealed class RelationshipWindow : ModernWindow
    {
        public const string WindowKey = "relationship";
        private const int WidthHud = 760, BanH = 112, BodyH = 440, ColA = 340;
        private static readonly DateTime Y2000 = new DateTime(2000, 1, 1);

        private string _liveKey = string.Empty;
        private bool _dirty = true;
        private readonly HashSet<AllowToggle> _pending = new HashSet<AllowToggle>();
        private long _nextTick;

        private PaintBox _ban;
        private ScrollView _colA, _colB;
        private ToggleSwitch _marry, _recallSw;
        private ModernButton _propose, _recall, _divorce;
        private readonly Action _changed, _toggles, _other;

        public RelationshipWindow() : base(WindowKey, W.T(ClientTextKeys.ModernSocialRelationship), -1, 40)
        {
            ContentWidth = WidthHud;
            ContentHeight = BanH + BodyH;
            _changed = () => _dirty = true;
            _toggles = () => { _pending.Clear(); _dirty = true; };
            _other = () => _dirty = true;
        }

        private static RelationshipState Rel => GameScene.Models.Relationship;
        private static bool Married => Rel.LoverName.Length > 0;
        private static bool LoverOnline => Married && Rel.LoverMap.Length > 0;
        private static int MyLevel => GameScene.User?.Level ?? 0;
        private static DateTime When => Rel.MarriedDate >= Y2000 ? Rel.MarriedDate : DateTime.MinValue;
        private static bool RingOn
        {
            get
            {
                UserItem ring = GameScene.User?.Equipment[(int)EquipmentSlot.RingL];
                return ring != null && ring.WeddingRing != -1;
            }
        }
        private static bool LoverBuff => GameScene.Models.Buffs.Has(BuffType.Lover);
        private static bool Toggle(AllowToggle t) => GameScene.Models.AllowToggles.Known ? GameScene.Models.AllowToggles.Get(t) : _legacy.Contains(t);
        /// <summary>A server without S.AllowToggles: the classic C.ChangeMarriage flips, tracked locally (both start allowed).</summary>
        private static readonly HashSet<AllowToggle> _legacy = new HashSet<AllowToggle> { AllowToggle.Marriage, AllowToggle.LoverRecall };

        // ---- for the UI test
        internal ModernButton ProposeButton => _propose;
        internal ModernButton RecallButton => _recall;
        internal ModernButton DivorceButton => _divorce;
        internal ToggleSwitch MarrySwitch => _marry;
        internal ToggleSwitch RecallSwitch => _recallSw;

        protected override void Build()
        {
            int top = TitleHeight;
            _ban = new PaintBox(PaintBanner) { Parent = this, Location = new Point(S(1), S(top)), Size = new Size(S(WidthHud) - 2, S(BanH)), NotControl = true };
            W.Divider(this, 0, top + BanH, WidthHud);
            int body = top + BanH + 1;
            _colA = new ScrollView(ColA, BodyH - 1) { Parent = this, Location = new Point(0, S(body)), Background = W.Page };
            _colA.Framed = false;
            W.VDivider(this, ColA, body, BodyH - 1);
            _colB = new ScrollView(WidthHud - ColA - 1, BodyH - 1) { Parent = this, Location = new Point(S(ColA + 1), S(body)), Background = W.Page };
            _colB.Framed = false;
            _dirty = true;
            Render();
        }

        protected override void Shown()
        {
            GameModels m = GameScene.Models;
            m.Relationship.LoverChanged.On(_changed);
            m.AllowToggles.Changed.On(_toggles);
            m.ServerRules.Changed.On(_other);
            m.Buffs.Changed.On(_other);
            _dirty = true;
        }

        protected override void Closed()
        {
            GameModels m = GameScene.Models;
            if (m == null) return;
            m.Relationship.LoverChanged.Off(_changed);
            m.AllowToggles.Changed.Off(_toggles);
            m.ServerRules.Changed.Off(_other);
            m.Buffs.Changed.Off(_other);
        }

        public override void Process()
        {
            base.Process();
            if (CMain.Time >= _nextTick)
            {
                _nextTick = CMain.Time + 1000;
                if (StateKey() != _liveKey) _dirty = true;
            }
            if (_dirty) Render();
        }

        /// <summary>The player on the cell the user faces (PlayerObject.MarriageRequest / DivorceRequest look there), and whether
        /// they face the user (Functions.FacingEachOther).</summary>
        private static (PlayerObject Obj, bool Facing)? Faced()
        {
            UserObject u = GameScene.User;
            if (u == null || GameScene.Scene?.MapControl == null) return null;
            Point front = Functions.PointMove(u.CurrentLocation, u.Direction, 1);
            List<MapObject> list = MapControl.ObjectsList;
            for (int i = 0; i < list.Count; i++)
                if (list[i] is PlayerObject p && !(p is HeroObject) && !(p is UserObject) && p.Race == ObjectType.Player && p.CurrentLocation == front)
                    return (p, (int)p.Direction == ((int)u.Direction + 4) % 8);
            return null;
        }

        private static string StateKey()
        {
            var f = Faced();
            PlayerObject l = Married ? Soc.InView(Rel.LoverName) : null;
            return MyLevel + "|" + RingOn + "|" + LoverBuff + "|" + (f.HasValue ? f.Value.Obj.Name + ":" + f.Value.Facing : "") + "|" +
                (l != null ? (int)l.Class + ":" + l.Level + ":" + l.Dead : "") + "|" + Toggle(AllowToggle.Marriage) + Toggle(AllowToggle.LoverRecall) + "|" + CMain.Now.Ticks / TimeSpan.TicksPerMinute;
        }

        /// <summary>Time until the divorce cooldown ends (zero = none or unknown).</summary>
        private static TimeSpan CooldownLeft()
        {
            var r = Soc.Rules;
            if (r == null || Married || When == DateTime.MinValue) return TimeSpan.Zero;
            TimeSpan left = When.AddDays(r.MarriageCooldown) - CMain.Now;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        // ------------------------------------------------------------------ actions

        /// <summary>RelationshipDialog's request button: C.MarriageRequest (the server looks at the player you face).</summary>
        private void Propose()
        {
            if (Married) { GameScene.Models.Chat.Add(W.T(ClientTextKeys.YouAreAlreadyMarried), ChatType.System); return; }
            Network.Enqueue(new C.MarriageRequest());
        }

        /// <summary>RelationshipDialog's divorce button, after a question: C.DivorceRequest (the lover must face you and agree).</summary>
        private void Divorce()
        {
            string lover = Rel.LoverName;
            if (lover.Length == 0) { GameScene.Models.Chat.Add(W.T(ClientTextKeys.YouAreNotMarried), ChatType.System); return; }
            var r = Soc.Rules;
            string cd = r != null ? " " + W.T(r.MarriageCooldown == 1 ? ClientTextKeys.ModernSocialDivorceCooldownOne : ClientTextKeys.ModernSocialDivorceCooldown, r.MarriageCooldown) : string.Empty;
            ModernRoot.Instance?.Popups.Ask(W.T(ClientTextKeys.ModernSocialDivorceAsk, lover, cd) + "\n\n" + W.T(ClientTextKeys.ModernSocialDivorceQuestion, lover), yes =>
            {
                if (yes && Rel.LoverName == lover) Network.Enqueue(new C.DivorceRequest());
            }, false, PopupKind.Danger, W.T(ClientTextKeys.ModernSocialRequestDivorce));
        }

        private static void Recall() => Network.Enqueue(new C.Chat { Message = "@recalllover" });

        /// <summary>RelationshipDialog's mail button: MailComposeLetterDialog.ComposeMail(LoverName).</summary>
        private static void Mail()
        {
            if (!Married) { GameScene.Models.Chat.Add(W.T(ClientTextKeys.YouAreNotMarried), ChatType.System); return; }
            WindowManager wm = Soc.Wm;
            if (wm == null || !wm.IsRegistered(MailWindow.WindowKey)) return;
            wm.Open(MailWindow.WindowKey);
            (wm.Get(MailWindow.WindowKey) as MailWindow)?.ComposeTo(Rel.LoverName);
        }

        private void SetToggle(AllowToggle t, bool on, ToggleSwitch sw)
        {
            sw.Set(Toggle(t)); // server authoritative: the switch moves on S.AllowToggles
            if (_pending.Contains(t) || on == Toggle(t)) return;
            if (GameScene.Models.AllowToggles.Known)
            {
                Network.Enqueue(new C.SetAllowToggle { Toggle = t, Value = on });
                _pending.Add(t);
                sw.Lock(true, W.T(ClientTextKeys.ModernWinWaitingServer));
            }
            else
            {
                // C.ChangeMarriage flips Marriage while single and LoverRecall while married (the switch shown is that one)
                Network.Enqueue(new C.ChangeMarriage());
                if (on) _legacy.Add(t);
                else _legacy.Remove(t);
                _dirty = true;
            }
        }

        // ------------------------------------------------------------------ render

        private void Render()
        {
            _dirty = false;
            if (_ban == null || _ban.IsDisposed || GameScene.User == null) return;
            _liveKey = StateKey();
            _ban.Invalidate();
            RenderA();
            RenderB();
        }

        private void PaintBanner(Graphics g, PaintBox b)
        {
            float k = ModernRoot.K;
            Soc.BannerGlow(g, b.Size, 0.4f, 1f);
            UserObject u = GameScene.User;
            if (u == null) return;
            RelationshipState rel = Rel;
            float sideW = (b.Size.Width - 190 * k - 36 * k - 28 * k) / 2f, cx = b.Size.Width / 2f;
            RectangleF left = new RectangleF(18 * k, 0, sideW, b.Size.Height), right = new RectangleF(b.Size.Width - 18 * k - sideW, 0, sideW, b.Size.Height);
            Soc.Who(g, left, false, Soc.Tag(u.Class), ModernTheme.Class(u.Class), true, true, null, u.Name, W.T(ClientTextKeys.ModernSocialLvClass, MyLevel, Soc.ClassName(u.Class)), null);
            RectangleF heart = new RectangleF(cx - 30 * k, (Married ? 8 : 18) * k, 60 * k, 60 * k);
            if (Married)
            {
                PlayerObject l = Soc.InView(rel.LoverName);
                string sub = l != null ? W.T(ClientTextKeys.ModernSocialLvClass, l.Level > 0 ? l.Level.ToString() : "?", Soc.ClassName(l.Class)) : string.Empty;
                bool online = LoverOnline;
                Soc.Who(g, right, true, l != null ? Soc.Tag(l.Class) : "?", l != null ? ModernTheme.Class(l.Class) : ModernTheme.Accent, online, false, null, rel.LoverName, sub,
                    online ? NpcFmt.Pretty(rel.LoverMap) : W.T(ClientTextKeys.ModernSocialOffline));
                Heart(g, heart, online ? 1 : 0, rel.MarriedDays.ToString());
                string days = W.T(ClientTextKeys.ModernSocialDaysTogether, rel.MarriedDays);
                ModernSkin.Text(g, days, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(cx - 95 * k, 70 * k, 190 * k, 14 * k), StringAlignment.Center);
                var r = Soc.Rules;
                string buff = r != null ? W.T(ClientTextKeys.ModernSocialLoverBuffPct, r.LoverEXPBonus) : W.T(ClientTextKeys.ModernSocialLoverBuff);
                float cw = Soc.ChipWidth(buff);
                Soc.Chip(g, cx - cw / 2f, 88 * k, buff, LoverBuff);
            }
            else
            {
                Soc.Who(g, right, true, "?", ModernTheme.Frame, false, false, null, W.T(ClientTextKeys.ModernSocialNobodyYet), W.T(ClientTextKeys.ModernSocialProposeBelow), null, false);
                Heart(g, heart, -1, null);
                TimeSpan cdLeft = CooldownLeft();
                string t = cdLeft > TimeSpan.Zero ? W.T(ClientTextKeys.ModernSocialCooldownBanner, (int)Math.Ceiling(cdLeft.TotalDays)) : W.T(ClientTextKeys.ModernSocialSingle);
                ModernSkin.Text(g, t, ModernSkin.Font(11, cdLeft == TimeSpan.Zero), cdLeft > TimeSpan.Zero ? ModernTheme.Dim : Color.White, new RectangleF(cx - 95 * k, 82 * k, 190 * k, 16 * k), StringAlignment.Center);
            }
        }

        /// <summary>The heart (.heart): beating fill while the lover is online (state 1), dark while offline (0), dashed when single (-1).</summary>
        private static void Heart(Graphics g, RectangleF r, int state, string days)
        {
            float k = ModernRoot.K;
            using var p = new System.Drawing.Drawing2D.GraphicsPath();
            float x = r.X, y = r.Y + r.Height * 0.05f, w = r.Width, h = r.Height * 0.9f;
            p.AddBezier(x + w / 2, y + h * 0.28f, x + w * 0.42f, y, x, y + h * 0.02f, x + w * 0.04f, y + h * 0.38f);
            p.AddBezier(x + w * 0.04f, y + h * 0.38f, x + w * 0.08f, y + h * 0.66f, x + w * 0.38f, y + h * 0.82f, x + w / 2, y + h);
            p.AddBezier(x + w / 2, y + h, x + w * 0.62f, y + h * 0.82f, x + w * 0.92f, y + h * 0.66f, x + w * 0.96f, y + h * 0.38f);
            p.AddBezier(x + w * 0.96f, y + h * 0.38f, x + w, y + h * 0.02f, x + w * 0.58f, y, x + w / 2, y + h * 0.28f);
            p.CloseFigure();
            Color fill = state > 0 ? ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.Accent), Color.Black, 0.7f)
                : ModernTheme.Mix(ModernSkin.Opaque(ModernTheme.PanelB), Color.Black, state == 0 ? 0.8f : 0.6f);
            using (var br = new SolidBrush(fill)) g.FillPath(br, p);
            using (var pen = new Pen(state > 0 ? Color.White : ModernSkin.Opaque(ModernTheme.Frame), Math.Max(1f, k)))
            {
                if (state < 0) pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                g.DrawPath(pen, p);
            }
            if (!string.IsNullOrEmpty(days))
                ModernSkin.Text(g, days, ModernSkin.Font(14, true), Color.White, new RectangleF(r.X, r.Y - 2 * k, r.Width, r.Height), StringAlignment.Center, StringAlignment.Center, false);
        }

        private int Label(ScrollView v, int icon, string text, int y, int w, string right = null)
        {
            Soc.Section(v.Content, icon, text, 14, y, w - 28, right);
            return y + 20;
        }

        /// <summary>A checklist box (.box + .ck lines + a note); returns its bottom (HUD px).</summary>
        private static int Checks(MirControl parent, int x, int y, int w, List<(int Ok, string Text)> lines, string note)
        {
            int nh = string.IsNullOrEmpty(note) ? 0 : Soc.WrapHeight(note, 11, w - 20) + 4;
            int h = 12 + lines.Count * 22 + nh;
            new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                W.Card(g, b.Size);
                float ly = 6 * k;
                foreach (var l in lines)
                {
                    Soc.Check(g, 10 * k, ly, b.Size.Width - 20 * k, l.Ok, l.Text);
                    ly += 22 * k;
                }
                if (nh > 0) ModernSkin.Text(g, note, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(10 * k, ly + 2 * k, b.Size.Width - 20 * k, nh * k), StringAlignment.Near, StringAlignment.Near, false, true);
            })
            { Parent = parent, Location = new Point(W.S(x), W.S(y)), Size = new Size(W.S(w), W.S(h)), NotControl = true };
            return y + h;
        }

        private void RenderA()
        {
            int keep = _colA.ScrollY;
            Soc.Clear(_colA.Content);
            _propose = _recall = _divorce = null;
            _marry = _recallSw = null;
            int w = ColA - 10, bw = w - 28, y = 12;
            var r = Soc.Rules;
            RelationshipState rel = Rel;
            int myLevel = MyLevel;
            if (Married)
            {
                bool online = LoverOnline;
                DateTime when = When;
                var stats = new (string V, string T)[]
                {
                    (rel.MarriedDays.ToString(), W.T(ClientTextKeys.ModernSocialDaysTogetherShort)),
                    (when != DateTime.MinValue ? Soc.Date(when) : "—", W.T(ClientTextKeys.ModernSocialWeddingDay)),
                    (W.T(online ? ClientTextKeys.ModernSocialOnline : ClientTextKeys.ModernSocialOffline), W.T(ClientTextKeys.ModernSocialYourLover)),
                };
                int sw = (bw - 12) / 3;
                for (int i = 0; i < 3; i++)
                {
                    var s = stats[i];
                    new PaintBox((g, b) =>
                    {
                        float k = ModernRoot.K;
                        using (var p = ModernSkin.RoundRect(new RectangleF(0.5f, 0.5f, b.Size.Width - 1, b.Size.Height - 1), ModernTheme.Radius * k))
                            ModernSkin.Stroke(g, p, W.Over(ModernTheme.Frame, 0.45f));
                        ModernSkin.Text(g, s.V, ModernSkin.Font(15, true), Color.White, new RectangleF(0, 7 * k, b.Size.Width, 20 * k), StringAlignment.Center);
                        ModernSkin.Text(g, s.T, ModernSkin.Font(10), ModernTheme.Dim, new RectangleF(0, 28 * k, b.Size.Width, 14 * k), StringAlignment.Center);
                    })
                    { Parent = _colA.Content, Location = new Point(S(14 + i * (sw + 6)), S(y)), Size = new Size(S(sw), S(48)), NotControl = true };
                }
                y += 58;
                // recall (@recalllover)
                bool ring = RingOn;
                int serverOk = r == null ? -1 : r.WeddingRingRecall ? 1 : 0;
                y = Label(_colA, ModernIcons.Recall, W.T(ClientTextKeys.ModernSocialRecallLover), y, w, "@recalllover");
                y = Checks(_colA.Content, 14, y, bw, new List<(int, string)>
                {
                    (serverOk, W.T(serverOk == 0 ? ClientTextKeys.ModernSocialRecallOff : ClientTextKeys.ModernSocialRecallOn)),
                    (ring ? 1 : 0, W.T(ClientTextKeys.ModernSocialRingOn)),
                    (online ? 1 : 0, W.T(ClientTextKeys.ModernSocialIsOnline, rel.LoverName)),
                }, W.T(ClientTextKeys.ModernSocialRecallNote, rel.LoverName)) + 8;
                _recall = W.At(new ModernButton(W.T(ClientTextKeys.ModernSocialRecallName, rel.LoverName), ButtonKind.Main) { Tooltip = "@recalllover" }, _colA.Content, 14, y);
                _recall.Size = new Size(S(bw), S(26));
                _recall.Click += (o, e) => Recall();
                if (serverOk == 0) _recall.SetDisabled(true, W.T(ClientTextKeys.ModernSocialRecallOff));
                else if (!ring) _recall.SetDisabled(true, W.T(ClientTextKeys.ModernSocialWearRing));
                else if (!online) _recall.SetDisabled(true, W.T(ClientTextKeys.ModernSocialIsOffline, rel.LoverName));
                y += 32;
                int hw = (bw - 6) / 2;
                var chat = W.At(new ModernButton(W.T(ClientTextKeys.ModernSocialLoverChat)) { Tooltip = W.T(ClientTextKeys.ModernSocialLoverChatHint) }, _colA.Content, 14, y);
                chat.Size = new Size(S(hw), S(26));
                chat.Click += (o, e) => Soc.Prefill(":)");
                if (!online) chat.SetDisabled(true, W.T(ClientTextKeys.LoverIsNotOnline));
                var mail = W.At(new ModernButton(W.T(ClientTextKeys.ModernWinMail)) { Tooltip = W.T(ClientTextKeys.ModernSocialMailLover, rel.LoverName) }, _colA.Content, 14 + hw + 6, y);
                mail.Size = new Size(S(hw), S(26));
                mail.Click += (o, e) => Mail();
                if (Soc.Wm == null || !Soc.Wm.IsRegistered(MailWindow.WindowKey)) mail.SetDisabled(true);
                y += 32;
                _divorce = W.At(new ModernButton(W.T(ClientTextKeys.ModernSocialRequestDivorce), ButtonKind.Warn) { Tooltip = W.T(ClientTextKeys.ModernSocialDivorceHint) }, _colA.Content, 14, y);
                _divorce.Size = new Size(S(bw), S(26));
                _divorce.Click += (o, e) => Divorce();
                y += 36;
                _recallSw = W.At(new ToggleSwitch(Toggle(AllowToggle.LoverRecall), true) { Tooltip = W.T(ClientTextKeys.ModernSocialAllowRecall) }, _colA.Content, 14, y + 2);
                ToggleSwitch rs = _recallSw;
                _recallSw.Changed += v => SetToggle(AllowToggle.LoverRecall, v, rs);
                if (_pending.Contains(AllowToggle.LoverRecall)) _recallSw.Lock(true, W.T(ClientTextKeys.ModernWinWaitingServer));
                W.Label(_colA.Content, W.T(ClientTextKeys.ModernSocialLetRecall), 52, y, bw - 40, 20, 12);
                y += 28;
            }
            else
            {
                var f = Faced();
                TimeSpan left = CooldownLeft();
                int lvOk = r == null ? -1 : myLevel >= r.MarriageLevelRequired ? 1 : 0;
                y = Label(_colA, ModernIcons.Heart, W.T(ClientTextKeys.ModernSocialPropose), y, w);
                var lines = new List<(int, string)>
                {
                    (lvOk, r != null ? W.T(ClientTextKeys.ModernSocialLevelReq, r.MarriageLevelRequired) : W.T(ClientTextKeys.ModernSocialLevelReqAny)),
                    (left == TimeSpan.Zero ? 1 : 0, left > TimeSpan.Zero ? W.T(ClientTextKeys.ModernSocialCooldownLeft, (int)Math.Ceiling(left.TotalDays)) : W.T(ClientTextKeys.ModernSocialNoCooldown)),
                    (f.HasValue ? 1 : 0, f.HasValue ? W.T(ClientTextKeys.ModernSocialFacing, f.Value.Obj.Name) : W.T(ClientTextKeys.ModernSocialStandFacing)),
                };
                if (f.HasValue) lines.Add((f.Value.Facing ? 1 : 0, W.T(f.Value.Facing ? ClientTextKeys.ModernSocialFacesYou : ClientTextKeys.ModernSocialMustFaceYou, f.Value.Obj.Name)));
                y = Checks(_colA.Content, 14, y, bw, lines, W.T(ClientTextKeys.ModernSocialProposeNote)) + 8;
                _propose = W.At(new ModernButton(f.HasValue ? W.T(ClientTextKeys.ModernSocialProposeTo, f.Value.Obj.Name) : W.T(ClientTextKeys.ModernSocialPropose), ButtonKind.Main) { Tooltip = W.T(ClientTextKeys.ModernSocialProposeHint) }, _colA.Content, 14, y);
                _propose.Size = new Size(S(bw), S(26));
                _propose.Click += (o, e) => Propose();
                if (lvOk == 0) _propose.SetDisabled(true, W.T(ClientTextKeys.ModernSocialMustBeLevel, r.MarriageLevelRequired));
                else if (left > TimeSpan.Zero) _propose.SetDisabled(true, W.T(ClientTextKeys.ModernSocialDivorcedRecently));
                else if (!f.HasValue) _propose.SetDisabled(true, W.T(ClientTextKeys.ModernSocialFaceToMarry));
                y += 36;
                _marry = W.At(new ToggleSwitch(Toggle(AllowToggle.Marriage), true) { Tooltip = W.T(ClientTextKeys.ModernSocialAllowMarriage) }, _colA.Content, 14, y + 2);
                ToggleSwitch ms = _marry;
                _marry.Changed += v => SetToggle(AllowToggle.Marriage, v, ms);
                if (_pending.Contains(AllowToggle.Marriage)) _marry.Lock(true, W.T(ClientTextKeys.ModernWinWaitingServer));
                W.Label(_colA.Content, W.T(ClientTextKeys.ModernSocialAllowMarriage), 52, y, bw - 40, 20, 12);
                y += 28;
            }
            new ModernPanel { Parent = _colA.Content, Location = new Point(0, S(y)), Size = new Size(1, 1) };
            _colA.FitContent(0);
            _colA.ScrollTo(keep);
        }

        private void RenderB()
        {
            int keep = _colB.ScrollY;
            Soc.Clear(_colB.Content);
            int w = WidthHud - ColA - 11, bw = w - 28, y = 12;
            var r = Soc.Rules;
            RelationshipState rel = Rel;
            DateTime when = When;
            if (Married)
            {
                y = Label(_colB, ModernIcons.Calendar, W.T(ClientTextKeys.ModernSocialMarriage), y, w);
                string text = W.T(ClientTextKeys.ModernSocialMarriedText, rel.LoverName, when != DateTime.MinValue ? " " + W.T(ClientTextKeys.ModernSocialOnDate, Soc.Date(when)) : string.Empty, rel.MarriedDays) + " " +
                    (LoverOnline ? W.T(ClientTextKeys.ModernSocialLoverIn, rel.LoverName, NpcFmt.Pretty(rel.LoverMap)) : W.T(ClientTextKeys.ModernSocialLoverOffline, rel.LoverName));
                y = Soc.TextBox(_colB.Content, 14, y, bw, text) + 8;
                y = Soc.Para(_colB.Content, W.T(ClientTextKeys.ModernSocialRingReplace), 14, y, bw, 11, ModernTheme.Dim) + 10;
            }
            else if (when != DateTime.MinValue)
            {
                y = Soc.TextBox(_colB.Content, 14, y, bw, W.T(ClientTextKeys.ModernSocialDivorcedOn, Soc.Date(when), rel.MarriedDays)) + 10;
            }
            y = Label(_colB, ModernIcons.Info, W.T(ClientTextKeys.ModernSocialHowMarriage), y, w);
            string lvl = r != null ? W.T(ClientTextKeys.ModernSocialLevelOrHigher, r.MarriageLevelRequired) : W.T(ClientTextKeys.ModernSocialHighEnough);
            var how = new (string Lead, string Text)[]
            {
                (W.T(ClientTextKeys.ModernSocialHowProposing), W.T(ClientTextKeys.ModernSocialHowProposingText, lvl)),
                (W.T(ClientTextKeys.ModernSocialHowBuff), r != null ? W.T(ClientTextKeys.ModernSocialHowBuffText, r.LoverEXPBonus) : W.T(ClientTextKeys.ModernSocialHowBuffAny)),
                (W.T(ClientTextKeys.ModernSocialHowRings), W.T(r?.WeddingRingRecall == false ? ClientTextKeys.ModernSocialHowRingsOff : ClientTextKeys.ModernSocialHowRingsOn)),
                (W.T(ClientTextKeys.ModernSocialHowChat), W.T(ClientTextKeys.ModernSocialHowChatText)),
                (W.T(ClientTextKeys.ModernSocialHowDivorce), W.T(ClientTextKeys.ModernSocialHowDivorceText) + (r != null ? " " + W.T(ClientTextKeys.ModernSocialHowDivorceDays, r.MarriageCooldown) : string.Empty)),
            };
            int inner = bw - 20, total = 0;
            var heights = new int[how.Length];
            for (int i = 0; i < how.Length; i++) { heights[i] = Soc.WrapHeight(how[i].Text, 12, inner); total += 17 + heights[i] + 6; }
            new PaintBox((g, b) =>
            {
                float k = ModernRoot.K;
                W.Card(g, b.Size);
                float ty = 10 * k;
                for (int i = 0; i < how.Length; i++)
                {
                    // desktop: the bold lead on its own line, the text under it
                    ModernSkin.Text(g, how[i].Lead, ModernSkin.Font(12, true), Color.White, new RectangleF(10 * k, ty, inner * k, 17 * k), StringAlignment.Near, StringAlignment.Near, false);
                    ModernSkin.Text(g, how[i].Text, ModernSkin.Font(12), ModernTheme.Text,
                        new RectangleF(10 * k, ty + 17 * k, inner * k, heights[i] * k + 2), StringAlignment.Near, StringAlignment.Near, false, true);
                    ty += (17 + heights[i] + 6) * k;
                }
            })
            { Parent = _colB.Content, Location = new Point(S(14), S(y)), Size = new Size(S(bw), S(total + 14)), NotControl = true };
            y += total + 24;
            new ModernPanel { Parent = _colB.Content, Location = new Point(0, S(y)), Size = new Size(1, 1) };
            _colB.FitContent(0);
            _colB.ScrollTo(keep);
        }
    }
}

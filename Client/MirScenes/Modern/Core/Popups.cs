using System.Drawing.Drawing2D;
using System.Globalization;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirScenes.Modern.Core.Widgets;

namespace Client.MirScenes.Modern.Core
{
    /// <summary>What a popup's kind looks like (popups.ts Kind): the badge mark and the primary colour.</summary>
    public enum PopupKind
    {
        Info,
        Question,
        Warning,
        Danger,
    }

    /// <summary>The button set of a confirm box (MirMessageBoxButtons).</summary>
    public enum PopupButtons
    {
        Ok,
        OkCancel,
        YesNo,
        YesNoCancel,
        Cancel,
    }

    /// <summary>The answer of a confirm box; None = closed from code (CloseAll, handle.Close).</summary>
    public enum PopupAnswer
    {
        None,
        Ok,
        Cancel,
        Yes,
        No,
    }

    /// <summary>A shown or queued popup; <see cref="Close"/> dismisses it as cancelled (a request as declined).</summary>
    public sealed class PopupHandle
    {
        internal Action CloseAction;
        internal bool Finished;

        public bool IsOpen => !Finished;

        public void Close() => CloseAction?.Invoke();
    }

    /// <summary>
    /// The new UI's stock boxes (port of core/popups.ts; the desktop's MirMessageBox, MirInputBox, MirAmountBox) and the invite
    /// toasts. This control is ModernRoot's popups layer: a modal box shows one at a time on a veil that covers the screen (and
    /// takes the mouse), others queue behind it ("+N"); requests are non-modal toasts stacked near the top. Keys: WindowManager
    /// asks <see cref="HandleKey"/> before the front window, and while a box shows every key is consumed (Enter = primary,
    /// Esc = cancel / no, Y / N). Text fields in a box (Input, Amount) have the keyboard themselves: Enter / Esc there come
    /// through the field. Callbacks run on the UI thread; a callback may open the next popup.
    /// <para>Not ported yet (phase 2): the countdown answer, the item / gold line under a confirm's text, input suggestions and
    /// live checks, the open / close / shake animations, rebuilding an open box after a theme or scale change.</para>
    /// </summary>
    public sealed class Popups : MirControl
    {
        /// <summary>MirAmountBox's gold icon (Items.Lib).</summary>
        public const int GoldImage = 116;

        private readonly PopupVeil _veil;
        private readonly List<Job> _queue = new List<Job>();
        private readonly List<RequestToast> _toasts = new List<RequestToast>();
        private Job _current;
        private PopupCard _card;

        public Popups()
        {
            BackColour = Color.Transparent;
            Size = new Size(Settings.ScreenWidth, Settings.ScreenHeight);
            _veil = new PopupVeil { Parent = this, Size = Size, Visible = false };
        }

        /// <summary>Whether a modal box shows.</summary>
        public bool IsOpen => _current != null;

        /// <summary>Boxes waiting behind the current one.</summary>
        public int Queued => _queue.Count;

        /// <summary>Request toasts showing.</summary>
        public int Requests => _toasts.Count;

        /// <summary>The box showing now (tests, the integrator's hooks), or null.</summary>
        public PopupCard Card => _card;

        private static string L(ClientTextKeys key) => GameLanguage.ClientTextMap.GetLocalization(key);

        // ------------------------------------------------------------------ public API

        /// <summary>MirMessageBox: text, a button set and a kind. The callback gets the answer and whether "Don't ask again" was
        /// ticked. <paramref name="holdMs"/> &gt; 0 makes the primary button hold-to-confirm (danger).</summary>
        public PopupHandle Confirm(string text, PopupButtons buttons, PopupKind kind, Action<PopupAnswer, bool> callback,
            bool dontAsk = false, string title = null, int holdMs = 0)
        {
            PopupAnswer[] order = buttons switch
            {
                PopupButtons.OkCancel => new[] { PopupAnswer.Cancel, PopupAnswer.Ok },
                PopupButtons.YesNo => new[] { PopupAnswer.No, PopupAnswer.Yes },
                PopupButtons.YesNoCancel => new[] { PopupAnswer.Cancel, PopupAnswer.No, PopupAnswer.Yes },
                PopupButtons.Cancel => new[] { PopupAnswer.Cancel },
                _ => new[] { PopupAnswer.Ok },
            };
            PopupAnswer primary = Array.IndexOf(order, PopupAnswer.Yes) >= 0 ? PopupAnswer.Yes : Array.IndexOf(order, PopupAnswer.Ok) >= 0 ? PopupAnswer.Ok : PopupAnswer.Cancel;
            PopupAnswer esc = Array.IndexOf(order, PopupAnswer.Cancel) >= 0 ? PopupAnswer.Cancel : Array.IndexOf(order, PopupAnswer.No) >= 0 ? PopupAnswer.No : PopupAnswer.Ok;
            bool tick = false;

            var job = new Job();
            job.Cancel = () => Finish(job, () => callback?.Invoke(PopupAnswer.None, tick));
            void Pick(PopupAnswer a) => Finish(job, () => callback?.Invoke(a, tick));

            job.Build = card =>
            {
                card.Setup(kind, title, text, -1);
                if (dontAsk)
                {
                    var chk = new ModernCheckBox(L(ClientTextKeys.ModernDontAskAgain));
                    chk.Changed += v => tick = v;
                    card.Place(chk, 68, 12);
                }
                var list = new List<ModernButton>();
                foreach (PopupAnswer a in order)
                {
                    ModernButton b;
                    if (a == primary && holdMs > 0)
                    {
                        var hold = new HoldButton(L(ClientTextKeys.ModernHoldToConfirm), kind == PopupKind.Danger, true) { HoldMs = holdMs, Kind = kind == PopupKind.Danger ? ButtonKind.Warn : ButtonKind.Main };
                        hold.Done += () => Pick(a);
                        b = hold;
                    }
                    else
                    {
                        bool main = a == primary && (kind != PopupKind.Info || order.Length == 1);
                        b = new ModernButton(AnswerText(a), main ? (kind == PopupKind.Danger ? ButtonKind.Warn : ButtonKind.Main) : ButtonKind.Normal, true);
                        b.Click += (o, e) => Pick(a);
                    }
                    list.Add(b);
                }
                card.Buttons(list.ToArray());
            };
            job.OnKey = e =>
            {
                switch (e.KeyCode)
                {
                    case Keys.Enter:
                        if (holdMs <= 0) Pick(primary);
                        return true;
                    case Keys.Escape:
                        Pick(esc);
                        return true;
                    case Keys.Y:
                        if (Array.IndexOf(order, PopupAnswer.Yes) >= 0 && holdMs <= 0) Pick(PopupAnswer.Yes);
                        return true;
                    case Keys.N:
                        if (Array.IndexOf(order, PopupAnswer.No) >= 0) Pick(PopupAnswer.No);
                        return true;
                }
                return false;
            };
            return Push(job);
        }

        /// <summary>A Yes / No (or OK / Cancel) box: <paramref name="callback"/> gets true on the primary answer.</summary>
        public PopupHandle Ask(string text, Action<bool> callback, bool okCancel = false, PopupKind kind = PopupKind.Question, string title = null)
        {
            return Confirm(text, okCancel ? PopupButtons.OkCancel : PopupButtons.YesNo, kind,
                (a, _) => callback?.Invoke(a == PopupAnswer.Yes || a == PopupAnswer.Ok), false, title);
        }

        /// <summary>MirInputBox: a text field (OK is off while empty). <paramref name="callback"/> gets the trimmed text, or null
        /// when cancelled.</summary>
        public PopupHandle Input(string title, string initial, int maxLength, Action<string> callback, string text = null, bool password = false)
        {
            var job = new Job();
            job.Cancel = () => Finish(job, () => callback?.Invoke(null));
            ModernTextField field = null;
            ModernButton ok = null;
            void Submit()
            {
                string v = field?.Text.Trim() ?? string.Empty;
                if (v.Length == 0) return;
                Finish(job, () => callback?.Invoke(v));
            }

            job.Build = card =>
            {
                card.Setup(PopupKind.Question, title, text, -1);
                field = new ModernTextField(344, 34, 13, 12, maxLength > 0 ? 50 : 12) { AutoFocus = true, Password = password };
                if (maxLength > 0) field.MaxLength = maxLength;
                card.Place(field, 18, 12);
                ModernLabel counter = null;
                if (maxLength > 0)
                {
                    counter = new ModernLabel
                    {
                        Parent = field,
                        Location = new Point(field.Size.Width - ModernRoot.S(46), 0),
                        Size = new Size(ModernRoot.S(38), field.Size.Height),
                        FontSize = 10,
                        Colour = ModernTheme.Dim,
                        Align = StringAlignment.Far,
                    };
                }
                var cancel = new ModernButton(L(ClientTextKeys.ModernCancel), ButtonKind.Normal, true);
                cancel.Click += (o, e) => job.Cancel();
                ok = new ModernButton(L(ClientTextKeys.ModernOk), ButtonKind.Main, true);
                ok.Click += (o, e) => Submit();
                card.Buttons(cancel, ok);
                void Refresh(string v)
                {
                    v ??= string.Empty;
                    if (counter != null) counter.Text = v.Length + "/" + maxLength;
                    ok.SetDisabled(v.Trim().Length == 0);
                }
                field.TextChanged += Refresh;
                field.Submitted += _ => Submit();
                field.Escaped += () => job.Cancel();
                field.Text = initial ?? string.Empty;
                Refresh(field.Text);
            };
            job.OnKey = e =>
            {
                if (e.KeyCode == Keys.Enter) { Submit(); return true; }
                if (e.KeyCode == Keys.Escape) { job.Cancel(); return true; }
                return false;
            };
            return Push(job);
        }

        /// <summary>MirAmountBox: how many of an item (<paramref name="itemImage"/> = ItemInfo.Image in Items.Lib, -1 = gold):
        /// a big number you can type (1.5k / 2m work), - / + that speed up while held, a slider, quick picks and Max.
        /// <paramref name="ok"/> runs with the amount; <paramref name="cancelled"/> when closed.</summary>
        public PopupHandle Amount(string title, int itemImage, uint max, Action<uint> ok, uint? value = null, uint min = 1, Action cancelled = null)
        {
            bool gold = itemImage < 0;
            min = Math.Min(min, max);
            uint val = Math.Clamp(value ?? max, min, Math.Max(min, max));
            var job = new Job();
            job.Cancel = () => Finish(job, () => cancelled?.Invoke());
            ModernTextField big = null;
            Slider slider = null;
            ModernButton okButton = null;
            bool writing = false;
            // the slider works in at most ~400 steps; its value maps back onto min..max
            int sliderMax = (int)Math.Min(max - min, 100000u);
            void Set(uint v, object from)
            {
                val = Math.Clamp(v, min, Math.Max(min, max));
                if (from != big && big != null)
                {
                    writing = true;
                    big.Text = val.ToString("#,##0", CultureInfo.InvariantCulture);
                    writing = false;
                }
                if (from != slider && slider != null)
                    slider.Set(max == min ? 0 : (int)Math.Round((val - min) / (double)(max - min) * sliderMax));
                okButton?.SetDisabled(val < min || (val == 0 && !gold));
            }
            void Submit()
            {
                if (big != null && TryParseAmount(big.Text, out uint typed)) Set(typed, null);
                if (val < min || (val == 0 && !gold)) return;
                uint result = val;
                Finish(job, () => ok?.Invoke(result));
            }
            void Nudge(long d) => Set((uint)Math.Clamp((long)val + d, 0, uint.MaxValue), null);

            job.Build = card =>
            {
                card.Setup(PopupKind.Question, title, null, gold ? GoldImage : itemImage);
                var row = new ModernPanel { Size = new Size(ModernRoot.S(344), ModernRoot.S(40)) };
                var less = new RepeatButton("−") { Parent = row, Big = true, FontSize = 18, Size = new Size(ModernRoot.S(34), ModernRoot.S(40)), Location = Point.Empty };
                big = new ModernTextField(260, 40, 22, 10, 10, true) { Parent = row, Location = new Point(ModernRoot.S(42), 0), AutoFocus = true };
                big.Box.ForeColour = gold ? ModernTheme.Gold : Color.White;
                big.Box.TextBox.TextAlign = HorizontalAlignment.Center;
                var more = new RepeatButton("+") { Parent = row, Big = true, FontSize = 18, Size = new Size(ModernRoot.S(34), ModernRoot.S(40)), Location = new Point(ModernRoot.S(310), 0) };
                less.Stepped += n => Nudge(-StepFor(n, max));
                more.Stepped += n => Nudge(StepFor(n, max));
                big.Box.TextBox.KeyPress += (o, e) =>
                {
                    char c = e.KeyChar;
                    if (!char.IsControl(c) && !char.IsDigit(c) && c != '.' && c != ',' && c != 'k' && c != 'K' && c != 'm' && c != 'M') e.Handled = true;
                };
                big.TextChanged += t =>
                {
                    if (writing) return;
                    if (TryParseAmount(t, out uint n)) Set(n, big);
                };
                big.Submitted += _ => Submit();
                big.Escaped += () => job.Cancel();
                card.Place(row, 18, 12);

                slider = new Slider(0, Math.Max(1, sliderMax), 0, 344, 1, false);
                slider.Changed += s => Set(min + (uint)Math.Round((max - min) * (s / (double)Math.Max(1, sliderMax))), slider);
                card.Place(slider, 18, 10);

                var lim = new ModernPanel { Size = new Size(ModernRoot.S(344), ModernRoot.S(14)) };
                new ModernLabel { Parent = lim, Size = new Size(ModernRoot.S(170), lim.Size.Height), FontSize = 10, Colour = ModernTheme.Dim, Text = min.ToString("#,##0", CultureInfo.InvariantCulture) };
                new ModernLabel
                {
                    Parent = lim,
                    Location = new Point(ModernRoot.S(174), 0),
                    Size = new Size(ModernRoot.S(170), lim.Size.Height),
                    FontSize = 10,
                    Colour = ModernTheme.Dim,
                    Align = StringAlignment.Far,
                    Text = L(ClientTextKeys.ModernMax) + " " + max.ToString("#,##0", CultureInfo.InvariantCulture),
                };
                card.Place(lim, 18, 2);

                var quick = new ModernPanel { Size = new Size(ModernRoot.S(344), ModernRoot.S(22)) };
                uint[] picks = gold ? new uint[] { 10000, 100000, 1000000, max / 2 } : new uint[] { 1, 10, max / 2 };
                int x = 0, w = ModernRoot.S(gold ? 62 : 80), gap = ModernRoot.S(6);
                var used = new HashSet<uint>();
                foreach (uint p in picks)
                {
                    if (p < min || p >= max || p == 0 || !used.Add(p)) continue; // Max has its own button
                    uint pv = p;
                    var b = new ModernButton(Short(p)) { Parent = quick, Location = new Point(x, 0), Size = new Size(w, ModernRoot.S(22)) };
                    b.Click += (o, e) => Set(pv, null);
                    x += w + gap;
                }
                var maxButton = new ModernButton(L(ClientTextKeys.ModernMax)) { Parent = quick, Location = new Point(x, 0), Size = new Size(w, ModernRoot.S(22)) };
                maxButton.Click += (o, e) => Set(max, null);
                card.Place(quick, 18, 10);

                var cancel = new ModernButton(L(ClientTextKeys.ModernCancel), ButtonKind.Normal, true);
                cancel.Click += (o, e) => job.Cancel();
                okButton = new ModernButton(L(ClientTextKeys.ModernOk), ButtonKind.Main, true);
                okButton.Click += (o, e) => Submit();
                card.Buttons(cancel, okButton);
                Set(val, null);
            };
            job.OnKey = e =>
            {
                switch (e.KeyCode)
                {
                    case Keys.Enter: Submit(); return true;
                    case Keys.Escape: job.Cancel(); return true;
                    case Keys.Up: Nudge(1); return true;
                    case Keys.Down: Nudge(-1); return true;
                    case Keys.PageUp: Nudge(Math.Max(1, max / 10)); return true;
                    case Keys.PageDown: Nudge(-Math.Max(1, max / 10)); return true;
                }
                return false;
            };
            return Push(job);
        }

        /// <summary>An invite toast (not modal): Accept / Decline; declines on its own when the bar runs out.</summary>
        public PopupHandle Request(string from, string text, int seconds, Action accept, Action decline)
        {
            var handle = new PopupHandle();
            var toast = new RequestToast(from ?? string.Empty, text ?? string.Empty, Math.Max(1, seconds)) { Parent = this };
            void End(bool yes)
            {
                if (handle.Finished) return;
                handle.Finished = true;
                ModernTick.Remove(toast);
                _toasts.Remove(toast);
                toast.Dispose();
                LayoutToasts();
                try
                {
                    if (yes) accept?.Invoke();
                    else decline?.Invoke();
                }
                catch (Exception ex)
                {
                    ModernLog.Once("[ModernUi] request toast callback", ex);
                }
            }
            toast.Answered = End;
            handle.CloseAction = () => End(false);
            _toasts.Add(toast);
            LayoutToasts();
            ModernTick.Add(toast);
            return handle;
        }

        /// <summary>Closes everything (log-out, dispose): the box and queued boxes as cancelled, requests as declined.</summary>
        public void CloseAll()
        {
            var queued = new List<Job>(_queue);
            _queue.Clear();
            foreach (Job j in queued) j.Cancel?.Invoke();
            _current?.Cancel?.Invoke();
            var toasts = new List<RequestToast>(_toasts);
            foreach (RequestToast t in toasts) t.Answered?.Invoke(false);
        }

        /// <summary>From WindowManager.HandleKey (before the front window): while a box shows, its keys run and every key is
        /// consumed. True = consumed.</summary>
        public bool HandleKey(KeyEventArgs e)
        {
            Job job = _current;
            if (job == null) return false;
            try
            {
                job.OnKey?.Invoke(e);
            }
            catch (Exception ex)
            {
                ModernLog.Once("[ModernUi] popup key", ex);
            }
            e.Handled = true;
            e.SuppressKeyPress = true; // no KeyPress afterwards (Enter would open the chat input once the popup is gone)
            return true;
        }

        // ------------------------------------------------------------------ queue

        private sealed class Job
        {
            public Action<PopupCard> Build;
            public Func<KeyEventArgs, bool> OnKey;
            public Action Cancel;
            public PopupHandle Handle;
        }

        private PopupHandle Push(Job job)
        {
            job.Handle = new PopupHandle();
            job.Handle.CloseAction = () => job.Cancel?.Invoke();
            if (IsDisposed)
            {
                job.Cancel?.Invoke();
                return job.Handle;
            }
            _queue.Add(job);
            if (_current == null) Next();
            else _card?.SetWaiting(_queue.Count);
            return job.Handle;
        }

        /// <summary>Ends a job (shown or queued) once: runs its callback, then shows the next box.</summary>
        private void Finish(Job job, Action callback)
        {
            if (job.Handle == null || job.Handle.Finished) return;
            job.Handle.Finished = true;
            bool wasCurrent = job == _current;
            if (!wasCurrent)
            {
                _queue.Remove(job);
                _card?.SetWaiting(_queue.Count);
            }
            try
            {
                callback?.Invoke(); // may push another popup: it queues behind (current is still set)
            }
            catch (Exception ex)
            {
                ModernLog.Once("[ModernUi] popup callback", ex);
            }
            if (wasCurrent) Next();
        }

        private static bool IsInside(MirControl c, MirControl container)
        {
            for (MirControl p = c; p != null; p = p.Parent)
                if (p == container) return true;
            return false;
        }

        private void Next()
        {
            if (_card != null)
            {
                // a focused native TextBox disposed in its own KeyDown would hand the focus to the next visible text field
                if (Program.Form?.ActiveControl is TextBox tb && tb.Tag is MirTextBox mtb && IsInside(mtb, _card))
                    Program.Form.ActiveControl = null;
                _card.Dispose();
                _card = null;
            }
            _current = null;
            if (_queue.Count == 0 || IsDisposed)
            {
                _veil.Visible = false;
                return;
            }
            Job job = _queue[0];
            _queue.RemoveAt(0);
            _current = job;
            _veil.Visible = true;
            _veil.BringToFront();
            _card = new PopupCard { Parent = _veil };
            try
            {
                job.Build(_card);
            }
            catch (Exception ex)
            {
                ModernLog.Once("[ModernUi] popup build", ex);
            }
            _card.Finish(Size);
            _card.SetWaiting(_queue.Count);
            if (ModernRoot.Instance != null) ModernRoot.Instance.KeepOnTop();
        }

        private void LayoutToasts()
        {
            int y = ModernRoot.S(132);
            for (int i = 0; i < _toasts.Count; i++)
            {
                RequestToast t = _toasts[i];
                t.Location = new Point((Size.Width - t.Size.Width) / 2, y);
                y += t.Size.Height + ModernRoot.S(8);
            }
        }

        protected override void OnSizeChanged()
        {
            base.OnSizeChanged();
            if (_veil == null) return;
            _veil.Size = Size;
            _card?.Finish(Size);
            LayoutToasts();
        }

        // ------------------------------------------------------------------ helpers

        private static string AnswerText(PopupAnswer a) => a switch
        {
            PopupAnswer.Yes => L(ClientTextKeys.ModernYes),
            PopupAnswer.No => L(ClientTextKeys.ModernNo),
            PopupAnswer.Cancel => L(ClientTextKeys.ModernCancel),
            _ => L(ClientTextKeys.ModernOk),
        };

        /// <summary>popups.ts amount - / +: one step, then bigger steps while held.</summary>
        private static long StepFor(int n, uint max) => n < 6 ? 1 : n < 16 ? Math.Max(1, max / 200) : Math.Max(1, max / 40);

        private static string Short(uint v)
        {
            if (v >= 1000000 && v % 1000000 == 0) return (v / 1000000) + "M";
            if (v >= 1000 && v % 1000 == 0) return (v / 1000) + "k";
            return v.ToString("#,##0", CultureInfo.InvariantCulture);
        }

        /// <summary>format.ts parseAmount: "1,500", "1.5k", "2m".</summary>
        public static bool TryParseAmount(string text, out uint value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim().Replace(",", string.Empty).ToLowerInvariant();
            double mul = 1;
            if (s.EndsWith("k")) { mul = 1e3; s = s[..^1]; }
            else if (s.EndsWith("m")) { mul = 1e6; s = s[..^1]; }
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || double.IsNaN(d) || d < 0) return false;
            d = Math.Round(d * mul);
            value = d >= uint.MaxValue ? uint.MaxValue : (uint)d;
            return true;
        }

        public override void Redraw()
        {
        }

        /// <summary>The layer takes the mouse only over its children (the veil while a box shows, the toasts).</summary>
        public override bool IsMouseOver(Point p)
        {
            if (!Visible || IsDisposed) return false;
            for (int i = Controls.Count - 1; i >= 0; i--)
                if (Controls[i].IsMouseOver(p)) return true;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _queue.Clear();
                _current = null;
                foreach (RequestToast t in _toasts) ModernTick.Remove(t);
                _toasts.Clear();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>The screen-covering veil behind a modal box (a flat dark translucent fill: no blur over the live world).</summary>
    internal sealed class PopupVeil : MirControl
    {
        public PopupVeil()
        {
            DrawControlTexture = true;
            BackColour = Color.FromArgb(170, 2, 4, 8);
        }

        protected override void CreateTexture()
        {
            if (Size != TextureSize) DisposeTexture();
            if (Size.Width <= 0 || Size.Height <= 0) return;
            base.CreateTexture();
        }

        public override void Redraw()
        {
        }
    }

    /// <summary>
    /// A modal box's card (popups.ts .hpop): the badge (kind mark or an item icon), title, wrapped text, then the body controls
    /// placed top to bottom (<see cref="Place"/>) and the button row (<see cref="Buttons"/>). Laid out in HUD px at the current scale.
    /// </summary>
    public sealed class PopupCard : ModernControl
    {
        private const int Width = 380;
        private PopupKind _kind;
        private string _title = string.Empty, _text = string.Empty;
        private int _icon = -1, _textY, _textH, _waiting;
        private int _y;

        public PopupCard()
        {
            Size = new Size(S(Width), S(120));
        }

        public PopupKind Kind => _kind;

        /// <summary>The buttons of the row (tests drive them).</summary>
        public readonly List<ModernButton> ButtonRow = new List<ModernButton>();

        internal void Setup(PopupKind kind, string title, string text, int icon)
        {
            _kind = kind;
            _title = title ?? string.Empty;
            _text = text ?? string.Empty;
            _icon = icon;
            float textW = S(Width - 68 - 18);
            _textH = _text.Length == 0 ? 0 : (int)Math.Ceiling(ModernSkin.MeasureWrapped(_text, TextFont, textW).Height);
            int top = _title.Length > 0 ? S(16 + 24) : S(16);
            if (_title.Length == 0 && _textH < S(38)) top = S(16) + (S(38) - _textH) / 2;
            _textY = top;
            _y = Math.Max(S(16 + 38), _textY + _textH);
        }

        private static Font TextFont => ModernSkin.Font(12.5f);

        internal Color KindColour => _kind switch
        {
            PopupKind.Danger => ModernTheme.Bad,
            PopupKind.Warning => ModernTheme.Warn,
            _ => ModernTheme.Accent,
        };

        /// <summary>Adds a body control under the previous one at x (HUD px), after a gap (HUD px).</summary>
        internal void Place(MirControl c, int xHud, int gapHud)
        {
            c.Parent = this;
            c.Location = new Point(S(xHud), _y + S(gapHud));
            _y = c.Location.Y + c.Size.Height;
        }

        /// <summary>The button row, right-aligned, in the order given (cancel first, primary last).</summary>
        internal void Buttons(params ModernButton[] buttons)
        {
            int y = _y + S(16), x = Size.Width - S(18);
            for (int i = buttons.Length - 1; i >= 0; i--)
            {
                ModernButton b = buttons[i];
                b.Parent = this;
                b.AutoWidth(84);
                if (b is ConfirmTwiceButton ct) ct.AutoWidth(84);
                x -= b.Size.Width;
                b.Location = new Point(x, y);
                x -= S(8);
            }
            ButtonRow.Clear();
            ButtonRow.AddRange(buttons);
            if (buttons.Length > 0) _y = y + buttons[0].Size.Height;
        }

        /// <summary>Final height and centring on the screen.</summary>
        internal void Finish(Size screen)
        {
            Size = new Size(S(Width), _y + S(16));
            Location = new Point(Math.Max(0, (screen.Width - Size.Width) / 2), Math.Max(0, (int)((screen.Height - Size.Height) * 0.45f)));
        }

        internal void SetWaiting(int n)
        {
            if (n == _waiting) return;
            _waiting = n;
            Invalidate();
        }

        protected internal override void DrawControl()
        {
            base.DrawControl();
            if (_icon < 0 || Libraries.Items == null) return;
            Size s = Libraries.Items.GetSize(_icon);
            if (s.IsEmpty) return;
            int cx = DisplayLocation.X + S(18 + 19), cy = DisplayLocation.Y + S(16 + 19);
            Libraries.Items.Draw(_icon, new Point(cx - s.Width / 2, cy - s.Height / 2), Color.White, false, 1f);
        }

        protected override void Paint(Graphics g)
        {
            float k = K, rad = (ModernTheme.Radius + 2) * k;
            Color kc = KindColour;
            RectangleF outer = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            RectangleF panel = RectangleF.Inflate(outer, -1f, -1f);
            using (GraphicsPath ring = ModernSkin.RoundRect(outer, rad + 1))
                ModernSkin.Stroke(g, ring, Color.Black);
            using (GraphicsPath p = ModernSkin.RoundRect(panel, rad))
            {
                Color top = ModernTheme.Mix(ModernTheme.PanelC, ModernTheme.PanelB, 0.96f);
                Color bottom = ModernTheme.Mix(ModernTheme.PanelB, Color.Black, 0.8f);
                ModernSkin.FillVertical(g, p, panel, new[] { Opaque(top), Opaque(bottom) });
                ModernSkin.FadedLine(g, panel.X, panel.Y + 1, panel.Width, 2 * k, kc);
                ModernSkin.Stroke(g, p, ModernTheme.Mix(kc, ModernTheme.Frame, 0.55f));
            }

            // badge
            RectangleF badge = new RectangleF(18 * k, 16 * k, 38 * k, 38 * k);
            if (_icon >= 0)
            {
                using GraphicsPath bp = ModernSkin.RoundRect(badge, 10 * k);
                ModernSkin.FillVertical(g, bp, badge, new[] { ModernTheme.PanelC, ModernTheme.PanelB });
                ModernSkin.Stroke(g, bp, kc);
            }
            else
            {
                for (int i = 4; i >= 1; i--)
                    using (var b = new SolidBrush(ModernTheme.Alpha(kc, 0.08f)))
                        g.FillEllipse(b, RectangleF.Inflate(badge, i * k, i * k));
                using (var bp = new GraphicsPath())
                {
                    bp.AddEllipse(badge);
                    ModernSkin.FillVertical(g, bp, badge, new[] { ModernTheme.Mix(kc, ModernTheme.PanelC, 0.35f), ModernTheme.PanelB });
                    ModernSkin.Stroke(g, bp, kc);
                }
                string mark = _kind == PopupKind.Question ? "?" : _kind == PopupKind.Info ? "i" : "!";
                ModernSkin.Text(g, mark, ModernSkin.Font(20, true), kc, badge, StringAlignment.Center, StringAlignment.Center, false);
            }

            if (_title.Length > 0)
                ModernSkin.Text(g, _title, ModernSkin.Font(14, true), Color.White, new RectangleF(68 * k, 16 * k, Size.Width - 86 * k, 24 * k));
            if (_text.Length > 0)
                ModernSkin.Text(g, _text, TextFont, ModernTheme.Mix(ModernTheme.Text, Color.White, 0.92f),
                    new RectangleF(68 * k, _textY, Size.Width - 86 * k, _textH + 4), StringAlignment.Near, StringAlignment.Near, false, true);
            if (_waiting > 0)
                ModernSkin.Text(g, "+" + _waiting, ModernSkin.Font(10, true), ModernTheme.Dim, new RectangleF(Size.Width - 60 * k, 8 * k, 48 * k, 14 * k), StringAlignment.Far);
        }

        private static Color Opaque(Color c) => Color.FromArgb(255, c.R, c.G, c.B);
    }

    /// <summary>An invite toast (popups.ts .hreq): initials, who and what, Decline / Accept, a bar that runs out.</summary>
    internal sealed class RequestToast : ModernControl, IModernTick
    {
        private const int Steps = 120;
        private readonly string _from, _text, _initials;
        private readonly long _start, _ms;
        private int _step = -1;
        private readonly int _textH;
        internal Action<bool> Answered;

        public RequestToast(string from, string text, int seconds)
        {
            _from = from;
            _text = text;
            _initials = from.Length > 3 ? from.Substring(0, 3).ToUpperInvariant() : from.ToUpperInvariant();
            _start = CMain.Time;
            _ms = seconds * 1000L;
            int textW = S(380 - 60 - 160);
            _textH = (int)Math.Ceiling(ModernSkin.MeasureWrapped(_text, ModernSkin.Font(12), textW).Height);
            int h = Math.Max(S(60), S(12 + 16) + _textH + S(12));
            Size = new Size(S(380), h);

            var accept = new ModernButton(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernAccept), ButtonKind.Main) { Parent = this };
            var decline = new ModernButton(GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernDecline)) { Parent = this };
            accept.AutoWidth(64);
            decline.AutoWidth(64);
            int y = (h - accept.Size.Height) / 2;
            accept.Location = new Point(Size.Width - S(12) - accept.Size.Width, y);
            decline.Location = new Point(accept.Location.X - S(6) - decline.Size.Width, y);
            accept.Click += (o, e) => Answered?.Invoke(true);
            decline.Click += (o, e) => Answered?.Invoke(false);
        }

        public float Remaining => Math.Clamp(1f - (CMain.Time - _start) / (float)_ms, 0f, 1f);

        void IModernTick.Tick()
        {
            if (IsDisposed)
            {
                ModernTick.Remove(this);
                return;
            }
            float left = Remaining;
            if (left <= 0f)
            {
                Answered?.Invoke(false);
                return;
            }
            int step = (int)(left * Steps);
            if (step == _step) return;
            _step = step;
            Invalidate();
        }

        protected override void Paint(Graphics g)
        {
            float k = K, rad = ModernTheme.Radius * k;
            RectangleF outer = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            RectangleF panel = RectangleF.Inflate(outer, -1f, -1f);
            using (GraphicsPath ring = ModernSkin.RoundRect(outer, rad + 1))
                ModernSkin.Stroke(g, ring, Color.Black);
            using (GraphicsPath p = ModernSkin.RoundRect(panel, rad))
            {
                ModernSkin.FillVertical(g, p, panel, new[] { Opaque(ModernTheme.PanelA), Opaque(ModernTheme.PanelB) });
                // the remaining-time bar along the bottom
                float frac = _step < 0 ? 1f : _step / (float)Steps;
                Region old = g.Clip;
                g.SetClip(p, CombineMode.Intersect);
                using (var b = new SolidBrush(ModernTheme.Accent))
                    g.FillRectangle(b, panel.X, panel.Bottom - 2 * k, panel.Width * frac, 2 * k);
                g.Clip = old;
                old.Dispose();
                ModernSkin.Stroke(g, p, ModernTheme.Accent);
            }
            RectangleF av = new RectangleF(12 * k, (Size.Height - 36 * k) / 2f, 36 * k, 36 * k);
            using (var ap = new GraphicsPath())
            {
                ap.AddEllipse(av);
                ModernSkin.FillVertical(g, ap, av, new[] { ModernTheme.PanelC, ModernTheme.PanelB });
                using var pen = new Pen(ModernTheme.Accent, 2 * k);
                g.DrawPath(pen, ap);
            }
            ModernSkin.Text(g, _initials, ModernSkin.Font(10, true), ModernTheme.Text, av, StringAlignment.Center, StringAlignment.Center, false);
            float tx = 60 * k, tw = S(380 - 60 - 160);
            ModernSkin.Text(g, _from, ModernSkin.Font(12, true), Color.White, new RectangleF(tx, 10 * k, tw, 18 * k));
            ModernSkin.Text(g, _text, ModernSkin.Font(12), ModernTheme.Text, new RectangleF(tx, 28 * k, tw, _textH + 4), StringAlignment.Near, StringAlignment.Near, false, true);
        }

        private static Color Opaque(Color c) => Color.FromArgb(255, c.R, c.G, c.B);
    }
}

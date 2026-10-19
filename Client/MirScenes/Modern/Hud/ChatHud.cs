// Ported from WebClient/src/ui/modern/hud/chat.ts (ChatHud): the new UI's chat box, bottom left and mostly transparent (the
// desktop's ChatDialog + ChatControlBar + ChatOptionDialog, MirScenes/Dialogs/MainDialogs.cs), as a view over the chat log
// model (GameScene.Models.Chat: Lines + Added). Every send goes out as C.Chat with ChatDialog's rules (LinkedItems, LastPM, the
// local @LEVELEFFECT / @TARGETDEAD / @light commands).
//   Lines   append-only (a ring of 300 parsed lines, at most 150 shown; rebuilt only on a tab or filter change), repeats within
//           30 s folded into ×N, the sender parsed from the pre-formatted text ("Name: text", "(!)Name: text", whisper in
//           "Name=>text", whisper out "/Name text"), "<Title/UniqueID>" item links from GameScene.ChatItemList (the item tooltip
//           on hover), mentions of the user's name marked, lines fading after the chosen time unless the box is active,
//           channel tags and timestamps optional.
//   Tabs    All / Local / Whispers / Group / Guild / System + one per whisper partner (closable), unread badges. The
//           Settings.Filter* switches (shared with the classic chat options) hide a ChatType in every tab; the Settings.Show*
//           switches show / hide the Local / Whispers / Group / Guild tabs and the Shout / Lover / Mentor channels.
//   Input   opens on Enter / Space / '@' / '!' and '/' (last whisper; ChatPanel_KeyPress), Esc closes. Channel pill: Say /
//           Shout '!' / Group '!!' / Guild '!~' / Whisper '/name' / Mentor '!#' / Lover ':)'; typing a prefix then a space
//           switches it. Capped at Globals.MaxChatLength including the prefix, history on Up / Down, whisper names completed
//           from friends, group members and recent partners; F1-F12 and Tab go on to the game (ChatTextBox_KeyDown).
//   Links   ItemChatLink (item cell Shift + right-click): "<FriendlyName> " + LinkedItems, refused past the length.
//   Menu    a name's menu: Whisper, Invite to group (leader, group not full), Add friend, Inspect (a player in view), Copy
//           name, Block / Unblock (the friend list's Blocked entries; their lines are hidden here too).
// Desktop idiom: the box is one ModernSurface on the HUD layer; the log is one painted control (only the visible lines are
// painted, on a change: a new line, a scroll, a fade step); the popovers live on the Menu layer. The settings and the box's
// place are kept for the session (statics; Settings has no fields for them yet).
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirNetwork;
using Client.MirObjects;
using Client.MirScenes.Models;
using Client.MirSounds;
using Client.MirGraphics.Rendering;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using C = ClientPackets;

namespace Client.MirScenes.Modern.Hud
{
    public sealed class ChatHud : ModernSurface
    {
        // ---- constants (chat.ts) ----
        private const int BufferLines = 300, MaxShown = 150, FoldMs = 30000, FadeOutMs = 1200, FadeSteps = 8;

        /// <summary>The chat settings (chat.ts Opts; saved in Settings.UiChat with the box place).</summary>
        public sealed class Options
        {
            public int Opacity = 55;   // background 0-90 %
            public int Fade = 20;      // seconds, 0 = never
            public int FontSize = 12;  // 11 / 12 / 14
            public bool Time, Tags = true, Lock;
        }

        public static readonly Options Opts = new Options();
        // chat.ts Place (HUD px: left, bottom, width, height)
        private static int _left = 8, _bottom = 186, _w = 440, _h = 196;

        static ChatHud()
        {
            int?[] v = HudPrefs.Ints(Settings.UiChat, 10);
            _left = v[0] ?? _left; _bottom = v[1] ?? _bottom; _w = v[2] ?? _w; _h = v[3] ?? _h;
            Opts.Opacity = Math.Clamp(v[4] ?? Opts.Opacity, 0, 90);
            Opts.Fade = Math.Max(0, v[5] ?? Opts.Fade);
            int font = v[6] ?? Opts.FontSize;
            Opts.FontSize = font == 11 || font == 14 ? font : 12;
            Opts.Time = (v[7] ?? (Opts.Time ? 1 : 0)) != 0;
            Opts.Tags = (v[8] ?? (Opts.Tags ? 1 : 0)) != 0;
            Opts.Lock = (v[9] ?? (Opts.Lock ? 1 : 0)) != 0;
        }

        /// <summary>Settings.UiChat (written on change; Settings.Save at exit).</summary>
        internal static void SavePrefs()
        {
            Settings.UiChat = HudPrefs.Join(_left, _bottom, _w, _h, Opts.Opacity, Opts.Fade, Opts.FontSize, Opts.Time ? 1 : 0, Opts.Tags ? 1 : 0, Opts.Lock ? 1 : 0);
        }

        private static readonly (ChatChannel Ch, string Prefix, ClientTextKeys Label)[] Speak =
        {
            (ChatChannel.Normal, "", ClientTextKeys.ModernChatSay), (ChatChannel.Shout, "!", ClientTextKeys.ModernChatShout),
            (ChatChannel.Group, "!!", ClientTextKeys.ModernChatGroup), (ChatChannel.Guild, "!~", ClientTextKeys.ModernChatGuild),
            (ChatChannel.Whisper, "/", ClientTextKeys.ModernChatWhisper), (ChatChannel.Mentor, "!#", ClientTextKeys.ModernChatMentor),
            (ChatChannel.Lover, ":)", ClientTextKeys.ModernChatLover),
        };

        private static string T(ClientTextKeys key) => GameLanguage.ClientTextMap.GetLocalization(key);
        private static string T(ClientTextKeys key, object a) => GameLanguage.ClientTextMap.GetLocalization(key, a);

        /// <summary>Whether a channel you can talk in is offered ([Chat] Show* for Shout / Lover / Mentor).</summary>
        private static bool Offered(ChatChannel ch) => ch switch
        {
            ChatChannel.Shout => Settings.ShowYellChat,
            ChatChannel.Mentor => Settings.ShowMentorChat,
            ChatChannel.Lover => Settings.ShowLoverChat,
            _ => true,
        };

        private static string Tag(ChatChannel ch) => ch switch
        {
            ChatChannel.Shout => T(ClientTextKeys.ModernChatShout),
            ChatChannel.Group => T(ClientTextKeys.ModernChatGroup),
            ChatChannel.Guild => T(ClientTextKeys.ModernChatGuild),
            ChatChannel.Mentor => T(ClientTextKeys.ModernChatMentor),
            ChatChannel.Lover => T(ClientTextKeys.ModernChatLover),
            ChatChannel.System => T(ClientTextKeys.ModernChatTabSystem),
            ChatChannel.Hint => T(ClientTextKeys.ModernChatTagHint),
            ChatChannel.Announcement => T(ClientTextKeys.ModernChatTagNotice),
            ChatChannel.LevelUp => T(ClientTextKeys.ModernChatTagLevel),
            _ => string.Empty,
        };

        // ---- model ----

        internal sealed class Line
        {
            public ChatType Type;
            public ChatChannel Ch;
            public string Text, From, Body;
            /// <summary>0 = not a whisper, 1 = in, 2 = out.</summary>
            public byte Whisper;
            public bool Mention, Old;
            public long T;
            public DateTime At;
            public int N = 1;
            // layout cache (device px, relative to the line's top)
            public int LayoutKey = int.MinValue;
            public int Height;
            public readonly List<Run> Runs = new List<Run>();
        }

        internal struct Run
        {
            public float X, Y, W, H;
            public string Text;
            /// <summary>0 text, 1 name, 2 link, 3 tag, 4 time, 5 the ×N pill.</summary>
            public byte Kind;
            public Font Font;
            public Color Colour;
            public UserItem Item;
            public string Name;
        }

        private sealed class Tab
        {
            public string Key, Name, Whisper;
            /// <summary>Channel bit mask; 0 = every channel.</summary>
            public int Mask;
            public Func<bool> Show;
            public int Unread;
            public bool Ping;
        }

        private readonly List<Line> _lines = new List<Line>();
        /// <summary>The current tab's lines, oldest first (at most <see cref="MaxShown"/>).</summary>
        internal readonly List<Line> Shown = new List<Line>();
        private readonly List<Tab> _tabs = new List<Tab>();
        private int _tab;
        private ChatChannel _ch = ChatChannel.Normal;
        private string _target = string.Empty;
        private readonly List<string> _hist = new List<string>();
        private int _histAt = -1;
        private readonly List<string> _partners = new List<string>();
        private readonly HashSet<string> _blocked = new HashSet<string>();
        private readonly List<ChatItem> _links = new List<ChatItem>();
        private string _lastPM = string.Empty;

        // ---- view state ----
        internal bool Stick = true;
        internal int Unseen;
        private bool _act, _hover, _typing;
        private bool _pinged;
        private long _pingUntil;
        /// <summary>Lines fade from the later of their arrival and this (the box going inactive restarts the fade, like the web's
        /// CSS animation).</summary>
        internal long FadeBase;
        private int _settingsMask = -1;
        private ChatPopover _pop;
        private ModernSurface _settingsPop;
        private bool _mouseWasDown;
        private bool _suppressText;
        private bool _relayoutDue;
        private bool _rebuildingInput;
        private long _blurCheckAt = -1;
        private long _relayoutAt;

        // ---- children ----
        private ChatBack _back;
        private ChatHandle _handle;
        private ChatTabStrip _strip;
        private ChatIconButton _trade, _lock, _gear, _send;
        internal ChatLogView Log;
        private ChatNewPill _newPill;
        private ModernPanel _inputRow;
        private ChatPill _pill;
        private ModernTextField _input;
        private ModernLabel _count;
        private ChatHintLabel _hint;
        private ChatHandle _grip;
        private ChatNotice _notice;

        public ChatHud()
        {
            PassThrough = false;
            FadeBase = CMain.Time;
            Parent = ModernRoot.Instance?.Hud;

            _tabs.Add(new Tab { Key = "all", Name = T(ClientTextKeys.ModernChatTabAll), Mask = 0 });
            _tabs.Add(new Tab { Key = "local", Name = T(ClientTextKeys.ModernChatTabLocal), Mask = Bits(ChatChannel.Normal, ChatChannel.Shout, ChatChannel.System, ChatChannel.Hint, ChatChannel.Announcement, ChatChannel.LevelUp), Show = () => Settings.ShowNormalChat });
            _tabs.Add(new Tab { Key = "whisper", Name = T(ClientTextKeys.ModernChatTabWhispers), Mask = Bits(ChatChannel.Whisper, ChatChannel.Lover, ChatChannel.Mentor), Show = () => Settings.ShowWhisperChat });
            _tabs.Add(new Tab { Key = "group", Name = T(ClientTextKeys.ModernChatGroup), Mask = Bits(ChatChannel.Group, ChatChannel.System), Show = () => Settings.ShowGroupChat });
            _tabs.Add(new Tab { Key = "guild", Name = T(ClientTextKeys.ModernChatGuild), Mask = Bits(ChatChannel.Guild, ChatChannel.Announcement), Show = () => Settings.ShowGuildChat });
            _tabs.Add(new Tab { Key = "system", Name = T(ClientTextKeys.ModernChatTabSystem), Mask = Bits(ChatChannel.System, ChatChannel.Hint, ChatChannel.Announcement, ChatChannel.LevelUp) });

            _notice = new ChatNotice();
            Build();

            GameModels m = GameScene.Models;
            if (m != null)
            {
                ReadBlocked();
                List<ChatLine> lines = m.Chat.Lines;
                for (int i = Math.Max(0, lines.Count - BufferLines); i < lines.Count; i++) Push(lines[i].Text, lines[i].Type, false);
                m.Chat.Added.On(OnAdded);
                m.Friends.Changed.On(OnFriendsChanged);
            }
            _settingsMask = SettingsMask();
            RenderTabs();
            RenderLog();
            UpdatePill();
            if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout += OnRelayout;
        }

        private static int Bits(params ChatChannel[] chans)
        {
            int m = 0;
            for (int i = 0; i < chans.Length; i++) m |= 1 << (int)chans[i];
            return m;
        }

        // ------------------------------------------------------------------ public surface (ModernUi.Chat.cs)

        /// <summary>ChatDialog: the input has the keyboard.</summary>
        public bool Typing => _input != null && _input.Focused;

        /// <summary>Item cell Shift + right-click (ClassicItemChatLink): "&lt;FriendlyName&gt; " + LinkedItems.</summary>
        public void LinkItem(ItemSlotRef cell)
        {
            UserItem item = cell?.Item;
            if (item == null) return;
            string title = item.FriendlyName;
            string text = "<" + title + "> ";
            string v = _input.Text;
            string sep = v.Length > 0 && !v.EndsWith(" ") ? " " : string.Empty;
            if (v.Length + sep.Length + text.Length > MaxLen())
            {
                GameScene.Models?.Chat.Add(T(ClientTextKeys.UnableLinkItemMessageTooLong), ChatType.System);
                return;
            }
            _links.Add(new ChatItem { UniqueID = item.UniqueID, Title = title, Grid = cell.GridType });
            OpenInput(v + sep + text);
        }

        /// <summary>ChatDialog.SetChatText: opens the input with text ("/name " starts a whisper, a bare prefix picks its
        /// channel; chat.ts prefill).</summary>
        public void Prefill(string text)
        {
            text ??= string.Empty;
            Match w = Regex.Match(text, @"^/(\S+)\s*$");
            if (w.Success)
            {
                SetChannel(ChatChannel.Whisper, w.Groups[1].Value);
                OpenInput(string.Empty);
                return;
            }
            string trimmed = text.TrimEnd();
            for (int i = 0; i < Speak.Length; i++)
            {
                if (Speak[i].Prefix.Length == 0 || Speak[i].Prefix == "/" || trimmed != Speak[i].Prefix) continue;
                SetChannel(Speak[i].Ch);
                OpenInput(string.Empty);
                return;
            }
            OpenInput(text.Length > MaxLen() ? text.Substring(0, MaxLen()) : text);
        }

        // ------------------------------------------------------------------ build / place

        private void OnRelayout(bool rebuild)
        {
            if (IsDisposed) return;
            if (rebuild) Build();
            else Place();
            _notice?.Place();
        }

        /// <summary>(Re)creates the children at the current HUD scale (chat.ts constructor structure).</summary>
        private void Build()
        {
            string keepText = _input?.Text ?? string.Empty;
            bool keepTyping = _typing;
            ClosePop();
            for (int i = Controls.Count - 1; i >= 0; i--) Controls[i].Dispose();
            Clamp();
            Size = new Size(S(_w), S(_h));

            _back = new ChatBack(this) { Parent = this, Location = Point.Empty, Size = Size };
            _handle = new ChatHandle(this, false) { Parent = this, Location = Point.Empty, Size = new Size(Size.Width, S(23)) };
            _strip = new ChatTabStrip(this) { Parent = this, Location = new Point(S(4), S(3)), Size = new Size(Size.Width - S(8) - S(3 * 24), S(20)) };
            _trade = IconButton(ModernIcons.Swap, T(ClientTextKeys.ModernChatTrade), 0, () => Network.Enqueue(new C.TradeRequest()));
            _lock = IconButton(Opts.Lock ? ModernIcons.Lock : ModernIcons.Unlock, T(ClientTextKeys.ModernChatLockPosition), 1, () =>
            {
                Opts.Lock = !Opts.Lock;
                SavePrefs();
                _lock.Icon = Opts.Lock ? ModernIcons.Lock : ModernIcons.Unlock;
                ApplyOpts();
            });
            _gear = IconButton(ModernIcons.Gear, T(ClientTextKeys.ModernChatSettings), 2, ToggleSettings);

            int inputH = S(32), hintH = S(17);
            Log = new ChatLogView(this) { Parent = this, Location = new Point(0, S(23)), Size = new Size(Size.Width, Math.Max(S(20), Size.Height - S(23) - hintH)) };
            _newPill = new ChatNewPill(this) { Parent = this, Visible = false };

            _inputRow = new ModernPanel { Parent = this, Location = new Point(0, Size.Height - inputH), Size = new Size(Size.Width, inputH), Visible = false };
            new ChatRule { Parent = _inputRow, Location = Point.Empty, Size = new Size(Size.Width, Math.Max(1, S(1))) };
            _pill = new ChatPill(this) { Parent = _inputRow, Location = new Point(S(4), S(5)) };
            _send = new ChatIconButton(ModernIcons.Send, 22, 20, 13) { Parent = _inputRow, Hint = T(ClientTextKeys.ModernChatSend) };
            _send.Location = new Point(Size.Width - S(4) - _send.Size.Width, S(6));
            _count = new ModernLabel { Parent = _inputRow, Size = new Size(S(34), S(24)), FontSize = 9, Colour = ModernTheme.Dim, Align = StringAlignment.Far };
            _count.Location = new Point(_send.Location.X - S(5) - _count.Size.Width, S(4));
            _send.Click += (o, e) => Send();
            BuildInput(keepText);
            _hint = new ChatHintLabel
            {
                Parent = this,
                Location = new Point(S(8), Size.Height - hintH),
                Size = new Size(Size.Width - S(16), hintH - S(2)),
                Text = T(ClientTextKeys.ModernChatHint),
                Visible = false,
            };
            _grip = new ChatHandle(this, true) { Parent = this, Size = new Size(S(14), S(14)), Location = new Point(Size.Width - S(14), 0), Hint = T(ClientTextKeys.ModernChatResize) };

            Reposition();
            Place();
            _typing = false;
            if (keepTyping) OpenInput(keepText);
            ApplyAct(true);
            UpdatePill();
            RenderTabs();
            Log.Relayout();
        }

        private ChatIconButton IconButton(int icon, string hint, int slot, Action act)
        {
            var b = new ChatIconButton(icon, 22, 20, 13) { Parent = this, Hint = hint };
            b.Location = new Point(Size.Width - S(4) - (3 - slot) * S(24), S(3));
            b.Click += (o, e) => act();
            return b;
        }

        /// <summary>Re-places the children for the current box size (a resize drag, then Build at its end).</summary>
        private void Reposition()
        {
            int inputH = S(32), hintH = S(17);
            if (_back != null) _back.Size = Size;
            if (_handle != null) _handle.Size = new Size(Size.Width, S(23));
            if (_strip != null) _strip.Size = new Size(Math.Max(S(40), Size.Width - S(8) - S(3 * 24)), S(20));
            if (_trade != null)
            {
                _trade.Location = new Point(Size.Width - S(4) - 3 * S(24), S(3));
                _lock.Location = new Point(Size.Width - S(4) - 2 * S(24), S(3));
                _gear.Location = new Point(Size.Width - S(4) - S(24), S(3));
            }
            if (_inputRow != null)
            {
                _inputRow.Location = new Point(0, Size.Height - inputH);
                _inputRow.Size = new Size(Size.Width, inputH);
            }
            if (_hint != null) _hint.Location = new Point(S(8), Size.Height - hintH);
            if (_grip != null) _grip.Location = new Point(Size.Width - S(14), 0);
            Log?.SizeToMode();
            RenderNewPill();
        }

        private void BuildInput(string text)
        {
            bool wasFocused = _input != null && !_input.IsDisposed && _input.Focused;
            _rebuildingInput = true;
            int left = _pill.Location.X + _pill.Size.Width + S(5);
            int w = Math.Max(S(40), _count.Location.X - S(5) - left);
            _input?.Dispose();
            _input = new ModernTextField((int)Math.Round(w / ModernRoot.K), 24, 12) { Parent = _inputRow, Location = new Point(left, S(4)), AutoFocus = true };
            _input.MaxLength = MaxLen();
            _suppressText = true;
            _input.Text = text;
            _suppressText = false;
            _input.TextChanged += OnInputText;
            _input.Submitted += _ => Send();
            _input.Escaped += () =>
            {
                if (_pop != null) { ClosePop(); return; }
                CloseInput();
            };
            _input.Box.TextBox.KeyDown += OnInputKeyDown;
            _input.Box.TextBox.KeyUp += OnInputKeyUp;
            // chat.ts blur: closes an empty input 150 ms later (a click on the pill or a popover row lands first).
            _input.Box.TextBox.LostFocus += (o, e) => { if (!_rebuildingInput) _blurCheckAt = CMain.Time + 150; };
            if (wasFocused) _input.Focus();
            _rebuildingInput = false;
        }

        /// <summary>Applies the place (chat.ts applyPlace: clamped to the screen).</summary>
        private void Place()
        {
            Clamp();
            Location = new Point(S(_left), Settings.ScreenHeight - S(_bottom) - Size.Height);
        }

        private static void Clamp()
        {
            float k = ModernRoot.K;
            int W = (int)(Settings.ScreenWidth / k), H = (int)(Settings.ScreenHeight / k);
            _w = Math.Min(Math.Max(260, _w), Math.Max(260, Math.Min(760, W - 8)));
            _h = Math.Min(Math.Max(110, _h), Math.Max(110, Math.Min(520, H - 40)));
            _left = Math.Min(Math.Max(0, _left), Math.Max(0, W - 80));
            _bottom = Math.Min(Math.Max(0, _bottom), Math.Max(0, H - 60));
        }

        /// <summary>A drag of the top bar (move) or the grip (resize), in device px since the press (chat.ts dragBy).</summary>
        internal void DragBy(bool resize, int dx, int dy, int ox, int oy, int ow, int oh)
        {
            float k = ModernRoot.K;
            if (resize)
            {
                _w = ow + (int)Math.Round(dx / k);
                _h = oh - (int)Math.Round(dy / k);
                Clamp();
                Size = new Size(S(_w), S(_h));
                _relayoutDue = true; // the children follow at most 10 times a second (Process)
                Place();
            }
            else
            {
                _left = ox + (int)Math.Round(dx / k);
                _bottom = oy - (int)Math.Round(dy / k);
                Place();
            }
        }

        internal (int, int, int, int) PlaceNow => (_left, _bottom, _w, _h);

        internal void DragEnded(bool resize)
        {
            SavePrefs();
            if (!resize) return;
            _relayoutDue = false;
            if (_typing) { Reposition(); Log.Relayout(); }
            else Build();
        }

        internal static bool Locked => Opts.Lock;

        // ------------------------------------------------------------------ lines

        private string Me() => GameScene.User?.Name ?? string.Empty;

        private static readonly Regex WhisperIn = new Regex(@"^([^\s=]+)=>\s?");
        private static readonly Regex WhisperOut = new Regex(@"^/(\S+)\s?");
        private static readonly Regex Sender = new Regex(@"^(?:\(!\))?([^\s:<>]+?)\s?:\s?");
        internal static readonly Regex LinkRx = new Regex(@"<([^<>]*?)/(\d+)>");

        /// <summary>The sender of a pre-formatted chat line (chat.ts parseLine).</summary>
        private static void Parse(ChatType type, string text, out string from, out string body, out byte whisper)
        {
            whisper = type == ChatType.WhisperIn ? (byte)1 : type == ChatType.WhisperOut ? (byte)2 : (byte)0;
            Match m = null;
            if (type == ChatType.WhisperIn) m = WhisperIn.Match(text);
            else if (type == ChatType.WhisperOut) m = WhisperOut.Match(text);
            else if (type != ChatType.System && type != ChatType.System2 && type != ChatType.Hint && type != ChatType.Announcement
                && type != ChatType.LineMessage && type != ChatType.LevelUp) m = Sender.Match(text);
            if (m != null && m.Success)
            {
                from = m.Groups[1].Value;
                body = text.Substring(m.Length);
                return;
            }
            from = string.Empty;
            body = text;
        }

        /// <summary>Parses and keeps a line (folding repeats); null when folded into the last line (chat.ts push).</summary>
        private Line Push(string text, ChatType type, bool live)
        {
            long now = live ? CMain.Time : CMain.Time - 3600000;
            Line last = _lines.Count > 0 ? _lines[^1] : null;
            if (last != null && last.Type == type && last.Text == text && now - last.T < FoldMs)
            {
                last.N++;
                last.T = now;
                last.LayoutKey = int.MinValue;
                return null;
            }
            Parse(type, text, out string from, out string body, out byte whisper);
            string me = Me();
            bool mention = me.Length > 0 && from.Length > 0 && from != me && whisper != 2 && text.IndexOf(me, StringComparison.OrdinalIgnoreCase) >= 0;
            var l = new Line
            {
                Type = type, Ch = ChatGfx.ChannelOf(type), Text = text, From = from, Body = body, Whisper = whisper, Mention = mention,
                T = now, At = CMain.Now, Old = !live,
            };
            _lines.Add(l);
            if (_lines.Count > BufferLines) _lines.RemoveAt(0);
            if (whisper != 0 && from.Length > 0) NotePartner(from, live);
            return l;
        }

        /// <summary>A whisper partner: recent list (autocomplete) and a tab of its own (chat.ts notePartner).</summary>
        private void NotePartner(string name, bool live)
        {
            _partners.Remove(name);
            _partners.Insert(0, name);
            if (_partners.Count > 12) _partners.RemoveAt(_partners.Count - 1);
            for (int i = 0; i < _tabs.Count; i++) if (_tabs[i].Whisper == name) return;
            _tabs.Add(new Tab { Key = "w:" + name, Name = name, Mask = Bits(ChatChannel.Whisper), Whisper = name });
            if (live) RenderTabs();
        }

        private static bool ChatVisible(ChatType type) => type switch
        {
            ChatType.Normal or ChatType.LineMessage => !Settings.FilterNormalChat,
            ChatType.WhisperIn or ChatType.WhisperOut => !Settings.FilterWhisperChat,
            ChatType.Shout or ChatType.Shout2 or ChatType.Shout3 => !Settings.FilterShoutChat,
            ChatType.System or ChatType.System2 => !Settings.FilterSystemChat,
            ChatType.Group => !Settings.FilterGroupChat,
            ChatType.Guild => !Settings.FilterGuildChat,
            _ => true,
        };

        private bool Shows(Tab tab, Line l)
        {
            if (!ChatVisible(l.Type)) return false;
            if (l.From.Length > 0 && l.Whisper != 2 && _blocked.Contains(l.From)) return false;
            if (tab.Whisper != null) return l.Ch == ChatChannel.Whisper && l.From == tab.Whisper;
            return tab.Mask == 0 || (tab.Mask & (1 << (int)l.Ch)) != 0;
        }

        private static bool TabVisible(Tab t) => t.Show == null || t.Show();

        /// <summary>A new line from the model (chat.ts add).</summary>
        private void OnAdded(ChatLine msg)
        {
            if (IsDisposed || msg == null) return;
            if (msg.Type == ChatType.Announcement) _notice?.Show(msg.Text);
            Line before = _lines.Count > 0 ? _lines[^1] : null;
            Line l = Push(msg.Text, msg.Type, true);
            if (l == null)
            {
                // folded into the last line: its ×N changed
                if (before != null && Shown.Count > 0 && Shown[^1] == before) Log.LineChanged(before);
                return;
            }
            Tab cur = _tabs[_tab];
            bool own = l.Whisper == 2 || (l.From.Length > 0 && l.From == Me());
            for (int i = 0; i < _tabs.Count; i++)
            {
                Tab t = _tabs[i];
                if (i == _tab || own || !Shows(t, l) || !TabVisible(t)) continue;
                t.Unread++;
                if (l.Mention || (l.Whisper == 1 && t.Whisper != null)) t.Ping = true;
                _strip.Invalidate();
            }
            if (Shows(cur, l))
            {
                Shown.Add(l);
                if (Shown.Count > MaxShown) Shown.RemoveAt(0);
                if (!Stick) Unseen++;
                Log.Appended(l);
                RenderNewPill();
            }
            if (l.Mention)
            {
                _pinged = true;
                _pingUntil = CMain.Time + 4000;
                ApplyAct(false);
            }
        }

        private void OnFriendsChanged()
        {
            if (ReadBlocked()) RenderLog();
        }

        private bool ReadBlocked()
        {
            List<ClientFriend> list = GameScene.Models?.Friends.Friends;
            int count = 0;
            bool changed = false;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] == null || !list[i].Blocked) continue;
                    count++;
                    if (!_blocked.Contains(list[i].Name)) changed = true;
                }
            if (!changed && count == _blocked.Count) return false;
            _blocked.Clear();
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null && list[i].Blocked) _blocked.Add(list[i].Name);
            return true;
        }

        /// <summary>Rebuilds the shown lines for the current tab (tab switch, filter / block / option change; chat.ts renderLog).</summary>
        internal void RenderLog()
        {
            Tab tab = _tabs[_tab];
            long cut = CMain.Time - Opts.Fade * 1000L;
            Shown.Clear();
            for (int i = _lines.Count - 1; i >= 0 && Shown.Count < MaxShown; i--)
            {
                Line l = _lines[i];
                if (!Shows(tab, l)) continue;
                if (Opts.Fade > 0 && l.T < cut) l.Old = true;
                Shown.Add(l);
            }
            Shown.Reverse();
            Stick = true;
            Unseen = 0;
            RenderNewPill();
            Log?.Relayout();
        }

        internal void RenderNewPill()
        {
            if (_newPill == null || _newPill.IsDisposed) return;
            bool show = Unseen > 0 && !Stick;
            if (show) _newPill.Text = Unseen == 1 ? T(ClientTextKeys.ModernChatNewMessage) : T(ClientTextKeys.ModernChatNewMessages, Unseen);
            if (_newPill.Visible != show) _newPill.Visible = show;
            if (show) _newPill.Location = new Point((Size.Width - _newPill.Size.Width) / 2, Size.Height - S(40) - _newPill.Size.Height);
        }

        internal void FollowEnd()
        {
            Stick = true;
            Unseen = 0;
            Log.ScrollToEnd();
            RenderNewPill();
        }

        // ------------------------------------------------------------------ tabs

        internal int TabCount => _tabs.Count;
        internal int CurrentTab => _tab;
        internal bool IsTabVisible(int i) => i >= 0 && i < _tabs.Count && TabVisible(_tabs[i]);
        internal string TabLabel(int i) => _tabs[i].Whisper != null ? "✉ " + _tabs[i].Name : _tabs[i].Name;
        internal bool TabClosable(int i) => _tabs[i].Whisper != null;
        internal int TabUnread(int i) => i == _tab ? 0 : _tabs[i].Unread;
        internal bool TabPing(int i) => i != _tab && _tabs[i].Ping;

        private void RenderTabs()
        {
            if (!TabVisible(_tabs[_tab])) _tab = 0;
            _strip?.Relayout();
        }

        internal void SelectTab(int i)
        {
            if (i < 0 || i >= _tabs.Count) return;
            Tab t = _tabs[i];
            _tab = i;
            t.Unread = 0;
            t.Ping = false;
            if (t.Whisper != null) SetChannel(ChatChannel.Whisper, t.Whisper);
            RenderTabs();
            RenderLog();
        }

        internal void CloseTab(int i)
        {
            if (i < 0 || i >= _tabs.Count || _tabs[i].Whisper == null) return;
            _tabs.RemoveAt(i);
            if (_tab >= i) _tab = Math.Max(0, _tab - 1);
            RenderTabs();
            RenderLog();
        }

        // ------------------------------------------------------------------ input

        private void SetChannel(ChatChannel ch, string target = "")
        {
            _ch = ch;
            _target = ch == ChatChannel.Whisper ? (target.Length > 0 ? target : _target) : string.Empty;
            UpdatePill();
        }

        private string Prefix()
        {
            if (_ch == ChatChannel.Whisper) return "/" + _target + " ";
            for (int i = 0; i < Speak.Length; i++) if (Speak[i].Ch == _ch) return Speak[i].Prefix;
            return string.Empty;
        }

        /// <summary>Globals.MaxChatLength less the channel prefix the send adds.</summary>
        private int MaxLen() => Math.Max(1, Globals.MaxChatLength - Prefix().Length);

        internal ChatChannel Channel => _ch;

        internal string PillText
        {
            get
            {
                if (_ch == ChatChannel.Whisper) return T(ClientTextKeys.ModernChatToName, _target.Length > 0 ? _target : "…");
                for (int i = 0; i < Speak.Length; i++) if (Speak[i].Ch == _ch) return T(Speak[i].Label);
                return T(ClientTextKeys.ModernChatSay);
            }
        }

        private void UpdatePill()
        {
            if (!Offered(_ch))
            {
                _ch = ChatChannel.Normal;
                _target = string.Empty;
            }
            if (_pill == null || _pill.IsDisposed) return;
            int oldW = _pill.Size.Width;
            _pill.Refresh();
            if (_pill.Size.Width != oldW && _input != null) BuildInput(_input.Text);
            if (_input == null) return;
            _input.Placeholder = _ch == ChatChannel.Whisper ? T(ClientTextKeys.ModernChatWhisperTo, _target.Length > 0 ? _target : T(ClientTextKeys.ModernChatWhisperPick))
                : _ch == ChatChannel.Normal ? T(ClientTextKeys.ModernChatSaySomething) : T(ClientTextKeys.ModernChatMessageYour, PillText.ToLowerInvariant());
            int max = MaxLen();
            _input.MaxLength = max;
            if (_input.Text.Length > max)
            {
                _suppressText = true;
                _input.Text = _input.Text.Substring(0, max);
                _suppressText = false;
            }
            int n = _input.Text.Length;
            _count.Text = n + "/" + max;
            _count.Colour = n > max - 10 ? ModernTheme.BadSoft : ModernTheme.Dim;
        }

        private void OnInputText(string v)
        {
            if (_suppressText) return;
            // A prefix then a space switches the channel pill (PlayerObject.Chat's prefixes).
            Match w = Regex.Match(v, @"^/(\S+) $");
            if (w.Success)
            {
                SetChannel(ChatChannel.Whisper, w.Groups[1].Value);
                SetInputText(string.Empty);
            }
            else
                for (int i = 0; i < Speak.Length; i++)
                {
                    string pre = Speak[i].Prefix;
                    if (pre.Length == 0 || pre == "/" || v != pre + " " || !Offered(Speak[i].Ch)) continue;
                    SetChannel(Speak[i].Ch);
                    SetInputText(string.Empty);
                    break;
                }
            UpdatePill();
            Suggest();
        }

        private void SetInputText(string text)
        {
            _suppressText = true;
            _input.Text = text;
            _suppressText = false;
            _input.Box.TextBox.SelectionStart = _input.Text.Length;
            _input.Box.TextBox.SelectionLength = 0;
        }

        /// <summary>"/na" completes whisper names from friends (online first), the group and recent partners (chat.ts suggest).</summary>
        private void Suggest()
        {
            string v = _input.Text;
            if (!Regex.IsMatch(v, @"^/\S*$"))
            {
                if (_pop != null && _pop.Kind == "ac") ClosePop();
                return;
            }
            string q = v.Substring(1).ToLowerInvariant();
            var seen = new HashSet<string>();
            var items = new List<ChatPopItem>();
            string me = Me();
            void Add(string n, string note)
            {
                if (items.Count >= 8 || string.IsNullOrEmpty(n) || n == me || seen.Contains(n) || !n.ToLowerInvariant().StartsWith(q)) return;
                seen.Add(n);
                string name = n;
                items.Add(new ChatPopItem
                {
                    Label = n, Note = note, Act = () =>
                    {
                        SetChannel(ChatChannel.Whisper, name);
                        SetInputText(string.Empty);
                        ClosePop();
                        UpdatePill();
                        _input.Focus();
                    },
                });
            }
            List<ClientFriend> friends = GameScene.Models?.Friends.Friends;
            if (friends != null)
            {
                for (int pass = 0; pass < 2; pass++)
                    for (int i = 0; i < friends.Count; i++)
                    {
                        ClientFriend f = friends[i];
                        if (f == null || f.Blocked || f.Online != (pass == 0)) continue;
                        Add(f.Name, T(f.Online ? ClientTextKeys.ModernChatOnline : ClientTextKeys.ModernChatOffline));
                    }
            }
            List<string> group = GameScene.Models?.Group.Members;
            if (group != null) for (int i = 0; i < group.Count; i++) Add(group[i], T(ClientTextKeys.ModernChatGroupNote));
            for (int i = 0; i < _partners.Count; i++) Add(_partners[i], T(ClientTextKeys.ModernChatRecent));
            if (items.Count == 0)
            {
                if (_pop != null && _pop.Kind == "ac") ClosePop();
                return;
            }
            OpenPop("ac", T(ClientTextKeys.ModernChatWhisperToHead), items, new Point(_input.DisplayLocation.X, _input.DisplayLocation.Y - S(4)));
            _pop.Selected = 0;
        }

        /// <summary>The input's own keys (chat.ts onKey): the autocomplete's arrows / Enter / Tab, history, Backspace on an empty
        /// input back to Say, F1-F12 / Tab on to the game (ChatTextBox_KeyDown → CMain_KeyDown).</summary>
        private void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            CMain.Shift = e.Shift;
            CMain.Alt = e.Alt;
            CMain.Ctrl = e.Control;
            bool fkey = e.KeyCode >= Keys.F1 && e.KeyCode <= Keys.F12;
            if (fkey || e.KeyCode == Keys.Tab)
            {
                if (_pop != null && _pop.Kind == "ac" && e.KeyCode == Keys.Tab && _pop.Count > 0)
                {
                    e.Handled = e.SuppressKeyPress = true;
                    _pop.Activate(_pop.Selected);
                    return;
                }
                e.Handled = e.SuppressKeyPress = true;
                CMain.CMain_KeyDown(sender, e);
                return;
            }
            if (_pop != null && _pop.Count > 0 && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || (e.KeyCode == Keys.Enter && _pop.Kind == "ac")))
            {
                e.Handled = e.SuppressKeyPress = true;
                if (e.KeyCode == Keys.Enter) { _pop.Activate(_pop.Selected); return; }
                _pop.Selected = _pop.Selected + (e.KeyCode == Keys.Up ? -1 : 1);
                return;
            }
            switch (e.KeyCode)
            {
                case Keys.Up:
                case Keys.Down:
                    if (_hist.Count == 0) return;
                    e.Handled = e.SuppressKeyPress = true;
                    bool up = e.KeyCode == Keys.Up;
                    _histAt = up ? (_histAt < 0 ? _hist.Count - 1 : Math.Max(0, _histAt - 1)) : _histAt < 0 ? -1 : _histAt + 1;
                    if (_histAt >= _hist.Count) _histAt = -1;
                    SetInputText(_histAt < 0 ? string.Empty : _hist[_histAt]);
                    UpdatePill();
                    return;
                case Keys.Back:
                    if (_input.Text.Length == 0 && _ch != ChatChannel.Normal) SetChannel(ChatChannel.Normal);
                    return;
            }
        }

        private void OnInputKeyUp(object sender, KeyEventArgs e)
        {
            CMain.Shift = e.Shift;
            CMain.Alt = e.Alt;
            CMain.Ctrl = e.Control;
            if ((e.KeyCode >= Keys.F1 && e.KeyCode <= Keys.F12) || e.KeyCode == Keys.Tab) CMain.CMain_KeyUp(sender, e);
        }

        /// <summary>Opens the input (chat.ts focus).</summary>
        private void OpenInput(string text)
        {
            SetTyping(true);
            if (text != null) SetInputText(text);
            _input.Focus();
            UpdatePill();
        }

        private void SetTyping(bool on)
        {
            if (_typing == on) return;
            _typing = on;
            _inputRow.Visible = on;
            ApplyAct(false);
        }

        /// <summary>Closes the input (cancel or after a send): ChatDialog.LinkedItems.Clear (chat.ts close).</summary>
        private void CloseInput()
        {
            SetInputText(string.Empty);
            _links.Clear();
            ClosePop();
            _input.Blur();
            SetTyping(false);
            UpdatePill();
        }

        /// <summary>ChatTextBox Enter: the channel prefix + text, then ChatDialog's send (chat.ts send + ChatLog.submit).</summary>
        private void Send()
        {
            string text = _input.Text.Trim();
            if (text.Length == 0)
            {
                CloseInput();
                return;
            }
            _hist.Add(text);
            if (_hist.Count > 30) _hist.RemoveAt(0);
            _histAt = -1;
            string msg;
            if (_ch == ChatChannel.Normal || text.StartsWith("@")) msg = text;
            else if (_ch == ChatChannel.Whisper)
            {
                if (_target.Length == 0)
                {
                    GameScene.Models?.Chat.Add(T(ClientTextKeys.ModernChatPickWhisper), ChatType.System);
                    return;
                }
                msg = "/" + _target + " " + text;
            }
            else msg = Prefix() + text;
            if (msg.Length > Globals.MaxChatLength) msg = msg.Substring(0, Globals.MaxChatLength);
            Submit(msg);
            Stick = true;
            Unseen = 0;
            RenderNewPill();
            CloseInput();
            Log.ScrollToEnd();
        }

        /// <summary>ChatTextBox_KeyPress Enter with msg (chatLog.ts submit): the local commands, then C.Chat with the links the
        /// message still names, and LastPM.</summary>
        private void Submit(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;
            if (msg.ToUpperInvariant() == "@LEVELEFFECT") Settings.LevelEffect = !Settings.LevelEffect;
            if (msg.ToUpperInvariant() == "@TARGETDEAD") Settings.TargetDead = !Settings.TargetDead;
            if (TryLightingCommand(msg)) return;
            if (TryAtmosphereCommand(msg)) return;
            var links = new List<ChatItem>();
            for (int i = 0; i < _links.Count; i++)
                if (msg.Contains("<" + _links[i].Title + ">")) links.Add(_links[i]);
            Network.Enqueue(new C.Chat { Message = msg, LinkedItems = links });
            if (msg[0] == '/')
            {
                string[] parts = msg.Split(' ');
                if (parts.Length > 0) _lastPM = parts[0];
            }
        }

        /// <summary>ChatDialog.TryLightingCommand: "@light new" / "@light old" switch the local lighting (never sent).</summary>
        private static bool TryLightingCommand(string msg)
        {
            string[] parts = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !parts[0].Equals("@LIGHT", StringComparison.OrdinalIgnoreCase)) return false;
            string arg = parts.Length > 1 ? parts[1].ToUpperInvariant() : string.Empty;
            ChatLog log = GameScene.Models?.Chat;
            if (arg == "NEW")
            {
                if (!DXManager.EnhancedLightingAvailable)
                {
                    log?.Add(T(ClientTextKeys.LightingEnhancedUnavailable), ChatType.Hint);
                    return true;
                }
                Settings.Lighting = LightingMode.Enhanced;
            }
            else if (arg == "OLD")
                Settings.Lighting = LightingMode.Classic;
            else
            {
                log?.Add(T(ClientTextKeys.LightingUsage), ChatType.Hint);
                return true;
            }
            if (GameScene.Scene?.MapControl != null) GameScene.Scene.MapControl.TextureValid = false;
            log?.Add(T(Settings.Lighting == LightingMode.Enhanced ? ClientTextKeys.LightingEnhanced : ClientTextKeys.LightingClassic), ChatType.Hint);
            return true;
        }

        /// <summary>ChatDialog.TryAtmosphereCommand: "@smooth on|off" / "@weather on|off" set the local render options (never sent).</summary>
        private static bool TryAtmosphereCommand(string msg)
        {
            string[] parts = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            bool smooth = parts[0].Equals("@SMOOTH", StringComparison.OrdinalIgnoreCase);
            bool weather = parts[0].Equals("@WEATHER", StringComparison.OrdinalIgnoreCase);
            if (!smooth && !weather) return false;

            ChatLog log = GameScene.Models?.Chat;
            string arg = parts.Length > 1 ? parts[1].ToUpperInvariant() : string.Empty;
            if (arg != "ON" && arg != "OFF")
            {
                log?.Add(T(ClientTextKeys.SmoothWeatherUsage), ChatType.Hint);
                return true;
            }
            bool on = arg == "ON";
            if (smooth)
            {
                Settings.SmoothMove = on;
                log?.Add(T(on ? ClientTextKeys.SmoothMoveOn : ClientTextKeys.SmoothMoveOff), ChatType.Hint);
                return true;
            }
            if (on && !DXManager.AtmosphereAvailable)
            {
                log?.Add(T(ClientTextKeys.WeatherNeedsEnhanced), ChatType.Hint);
                return true;
            }
            Settings.Atmosphere = on;
            if (GameScene.Scene?.MapControl != null) GameScene.Scene.MapControl.TextureValid = false;
            log?.Add(T(on ? ClientTextKeys.WeatherOn : ClientTextKeys.WeatherOff), ChatType.Hint);
            return true;
        }

        // ------------------------------------------------------------------ keys (ChatPanel_KeyPress / KeyDown)

        private bool KeysBlocked()
        {
            ModernRoot root = ModernRoot.Instance;
            return !Visible || root == null || root.Popups.IsOpen || root.ModalPromptOpen || Typing || Program.Form.ActiveControl is TextBox
                || (GameScene.Ui as ModernUi)?.GameMenu?.IsOpen == true   // the game menu has the keyboard (Enter runs its action)
                || Modern.Windows.KeybindsWindow.CapturingKey;                 // the keybinds window waits for a key: every key is its own
        }

        /// <summary>Enter / Space / '@' / '!' open the input, '/' a whisper to the last partner (ChatPanel_KeyPress).</summary>
        public override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (e.Handled || KeysBlocked() || CMain.Ctrl || CMain.Alt) return;
            char c = e.KeyChar;
            if (c == '\r' || c == ' ' || c == '@' || c == '!')
            {
                e.Handled = true;
                OpenInput(c == '@' || c == '!' ? c.ToString() : _input.Text);
                if (c == '@' || c == '!') OnInputText(_input.Text);
            }
            else if (c == '/')
            {
                e.Handled = true;
                if (_lastPM.Length > 1) Prefill(_lastPM + " ");
                else
                {
                    OpenInput("/");
                    Suggest();
                }
            }
        }

        /// <summary>PageUp / PageDown / End scroll the log while no keybind uses the key (chat.ts handleKey).</summary>
        public override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || KeysBlocked() || e.Control || e.Alt) return;
            if (e.KeyCode != Keys.PageUp && e.KeyCode != Keys.PageDown && e.KeyCode != Keys.End) return;
            List<KeyBind> binds = CMain.InputKeys.Keylist;
            for (int i = 0; i < binds.Count; i++) if (binds[i].Key == e.KeyCode) return;
            switch (e.KeyCode)
            {
                case Keys.PageUp: Log.ScrollBy(-(int)(Log.Size.Height * 0.8f), true); break;
                case Keys.PageDown: Log.ScrollBy((int)(Log.Size.Height * 0.8f), true); break;
                case Keys.End: FollowEnd(); break;
            }
            e.Handled = true;
        }

        // ------------------------------------------------------------------ popovers

        private void OpenPop(string kind, string head, List<ChatPopItem> items, Point bottomLeft)
        {
            ClosePop();
            _pop = new ChatPopover(kind, head, items, bottomLeft);
            ApplyAct(false);
        }

        internal void ClosePop()
        {
            if (_pop != null)
            {
                if (!_pop.IsDisposed) _pop.Dispose();
                _pop = null;
            }
            if (_settingsPop != null)
            {
                if (!_settingsPop.IsDisposed) _settingsPop.Dispose();
                _settingsPop = null;
            }
            if (!IsDisposed && _back != null) ApplyAct(false);
        }

        internal void ChannelMenu()
        {
            if (_pop != null && _pop.Kind == "menu") { ClosePop(); return; }
            var items = new List<ChatPopItem>();
            for (int i = 0; i < Speak.Length; i++)
            {
                var sp = Speak[i];
                if (!Offered(sp.Ch)) continue;
                items.Add(new ChatPopItem
                {
                    Label = T(sp.Label),
                    Dot = ChatGfx.Channel(sp.Ch),
                    Note = sp.Prefix.Length > 0 ? sp.Prefix + (sp.Ch == ChatChannel.Whisper ? T(ClientTextKeys.ModernChatNameHint) : string.Empty) : T(ClientTextKeys.ModernChatNoPrefix),
                    Act = () =>
                    {
                        ClosePop();
                        SetChannel(sp.Ch);
                        _input.Focus();
                        if (sp.Ch == ChatChannel.Whisper && _target.Length == 0)
                        {
                            SetInputText("/");
                            Suggest();
                        }
                    },
                });
            }
            OpenPop("menu", T(ClientTextKeys.ModernChatTalkIn), items, new Point(_pill.DisplayLocation.X, _pill.DisplayLocation.Y - S(4)));
        }

        /// <summary>A name's menu (chat.ts nameMenu).</summary>
        internal void NameMenu(string n, Point at)
        {
            string me = Me();
            if (n == me)
            {
                OpenPop("menu", n, new List<ChatPopItem> { new ChatPopItem { Label = T(ClientTextKeys.ModernChatThatsYou), Act = ClosePop } }, at);
                return;
            }
            var items = new List<ChatPopItem>
            {
                new ChatPopItem { Label = T(ClientTextKeys.ModernChatWhisper), Act = () => { ClosePop(); SetChannel(ChatChannel.Whisper, n); OpenInput(null); } },
            };
            GroupState g = GameScene.Models?.Group;
            if (g != null && g.Members.Count > 0 && g.Members[0] == me && g.Members.Count < Globals.MaxGroup && !g.Contains(n))
                items.Add(new ChatPopItem { Label = T(ClientTextKeys.ModernChatInvite), Act = () => { ClosePop(); Network.Enqueue(new C.AddMember { Name = n }); } });
            ClientFriend friend = null;
            List<ClientFriend> friends = GameScene.Models?.Friends.Friends;
            if (friends != null) for (int i = 0; i < friends.Count; i++) if (friends[i] != null && friends[i].Name == n) { friend = friends[i]; break; }
            if (friend == null)
                items.Add(new ChatPopItem { Label = T(ClientTextKeys.ModernChatAddFriend), Act = () => { ClosePop(); Network.Enqueue(new C.AddFriend { Name = n, Blocked = false }); } });
            uint ob = ChatGfx.PlayerInView(n);
            if (ob != 0)
                items.Add(new ChatPopItem
                {
                    Label = T(ClientTextKeys.ModernChatInspect), Act = () =>
                    {
                        ClosePop();
                        if (CMain.Time < GameScene.InspectTime) return;
                        GameScene.InspectTime = CMain.Time + 500;
                        if (GameScene.Models != null) GameScene.Models.Inspect.InspectID = ob;
                        Network.Enqueue(new C.Inspect { ObjectID = ob });
                    },
                });
            items.Add(new ChatPopItem
            {
                Label = T(ClientTextKeys.ModernChatCopyName), Act = () =>
                {
                    ClosePop();
                    try { Clipboard.SetText(n); }
                    catch (Exception ex) { ModernLog.Once("[ModernUi] chat copy name", ex); }
                },
            });
            if (friend != null && friend.Blocked)
            {
                int index = friend.Index;
                items.Add(new ChatPopItem { Label = T(ClientTextKeys.ModernChatUnblock, n), Act = () => { ClosePop(); Network.Enqueue(new C.RemoveFriend { CharacterIndex = index }); } });
            }
            else if (friend == null)
                items.Add(new ChatPopItem { Label = T(ClientTextKeys.ModernChatBlock, n), Bad = true, Act = () => { ClosePop(); Network.Enqueue(new C.AddFriend { Name = n, Blocked = true }); } });
            OpenPop("menu", n, items, at);
        }

        private void ToggleSettings()
        {
            if (_settingsPop != null) { ClosePop(); return; }
            ClosePop();
            _settingsPop = new ChatSettingsPop(this, new Point(DisplayLocation.X + Size.Width, DisplayLocation.Y));
            ApplyAct(false);
        }

        /// <summary>A setting changed in the settings popover (chat.ts change()).</summary>
        internal void OptionsChanged(bool relog)
        {
            SavePrefs();
            ApplyOpts();
            if (relog) RenderLog();
            else Log.Relayout();
        }

        private void ApplyOpts()
        {
            _back?.Invalidate();
            if (_grip != null) _grip.Visible = _act && !Opts.Lock;
            Log?.Invalidate();
        }

        // ------------------------------------------------------------------ act state / per frame

        internal bool Act => _act;
        internal bool Pinged => _pinged;

        private void ApplyAct(bool force)
        {
            bool on = _hover || _typing || _pop != null || _settingsPop != null;
            bool changed = on != _act || force;
            bool wasAct = _act;
            _act = on;
            if (wasAct && !on) FadeBase = CMain.Time; // the fade restarts (the web's animation restarts)
            if (changed && _back != null) _back.Invalidate();
            if (_grip != null && _grip.Visible != (on && !Opts.Lock)) _grip.Visible = on && !Opts.Lock;
            if (_hint != null && _hint.Visible != (on && !_typing)) _hint.Visible = on && !_typing;
            UpdateTopBar();
            Log?.SizeToMode();
            if (changed) Log?.Invalidate();
        }

        private void UpdateTopBar()
        {
            bool bar = _act || _pinged;
            if (_strip != null && _strip.Visible != bar) _strip.Visible = bar;
            if (_trade != null && _trade.Visible != bar) { _trade.Visible = bar; _lock.Visible = bar; _gear.Visible = bar; }
        }

        internal bool HasHint => _act && !_typing;
        internal bool IsTyping => _typing;

        private int SettingsMask()
        {
            int m = 0, b = 0;
            void Bit(bool v) { if (v) m |= 1 << b; b++; }
            Bit(Settings.FilterNormalChat); Bit(Settings.FilterWhisperChat); Bit(Settings.FilterShoutChat); Bit(Settings.FilterSystemChat);
            Bit(Settings.FilterGroupChat); Bit(Settings.FilterGuildChat);
            Bit(Settings.ShowNormalChat); Bit(Settings.ShowYellChat); Bit(Settings.ShowWhisperChat); Bit(Settings.ShowLoverChat);
            Bit(Settings.ShowMentorChat); Bit(Settings.ShowGroupChat); Bit(Settings.ShowGuildChat);
            return m;
        }

        public override void Process()
        {
            // The Filter* / Show* settings have no change event: compare a cached mask (13 bools).
            int mask = SettingsMask();
            if (mask != _settingsMask)
            {
                bool filters = (mask & 63) != (_settingsMask & 63);
                _settingsMask = mask;
                RenderTabs();
                UpdatePill();
                if (filters) RenderLog();
            }

            bool hover = false;
            for (MirControl c = MouseControl; c != null; c = c.Parent)
                if (c == this) { hover = true; break; }
            if (!hover && Moving) hover = true;
            if (hover != _hover)
            {
                _hover = hover;
                ApplyAct(false);
            }
            if (_pinged && CMain.Time >= _pingUntil)
            {
                _pinged = false;
                UpdateTopBar();
            }

            // Outside clicks close a popover (any press outside it and outside the chat's own buttons).
            bool down = Control.MouseButtons != MouseButtons.None;
            if (down && !_mouseWasDown && (_pop != null || _settingsPop != null))
            {
                Point p = CMain.MPoint;
                bool inside = (_pop != null && !_pop.IsDisposed && _pop.DisplayRectangle.Contains(p))
                    || (_settingsPop != null && !_settingsPop.IsDisposed && _settingsPop.DisplayRectangle.Contains(p))
                    || (_pill != null && _pill.Visible && _pill.DisplayRectangle.Contains(p))
                    || (_gear != null && _gear.DisplayRectangle.Contains(p))
                    || (_input != null && _input.Visible && _input.DisplayRectangle.Contains(p));
                if (!inside) ClosePop();
            }
            _mouseWasDown = down;

            if (_blurCheckAt >= 0 && CMain.Time >= _blurCheckAt)
            {
                _blurCheckAt = -1;
                if (_input != null && !_input.IsDisposed && !_input.Focused && _pop == null && _settingsPop == null && _input.Text.Length == 0) SetTyping(false);
            }

            if (_relayoutDue && CMain.Time >= _relayoutAt)
            {
                _relayoutDue = false;
                _relayoutAt = CMain.Time + 100;
                Reposition();
                Log?.Relayout();
            }

            Log?.Tick();
        }

        public override bool IsMouseOver(Point p)
        {
            if (_handle != null && (_handle.Dragging || _grip.Dragging)) return Visible;
            return base.IsMouseOver(p);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                GameModels m = GameScene.Models;
                if (m != null)
                {
                    m.Chat.Added.Off(OnAdded);
                    m.Friends.Changed.Off(OnFriendsChanged);
                }
                if (ModernRoot.Instance != null) ModernRoot.Instance.Relayout -= OnRelayout;
                ClosePop();
                if (_notice != null && !_notice.IsDisposed) _notice.Dispose();
                _notice = null;
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ test hooks (UiTestRunner.Chat.cs)

        internal string TestState => $"lines={_lines.Count} shown={Shown.Count} tab={_tabs[_tab].Key} tabs={_tabs.Count} ch={_ch} target={_target} typing={_typing} act={_act} input=\"{_input?.Text}\" pop={(_pop != null ? _pop.Kind : _settingsPop != null ? "settings" : "none")} unseen={Unseen} stick={Stick} links={_links.Count} lastPM={_lastPM} blocked={_blocked.Count}";
        internal void TestType(string text) { if (!_typing) OpenInput(null); _input.Text = text; }
        internal void TestEnter() => Send();
        internal void TestEscape() { if (_pop != null) ClosePop(); else CloseInput(); }
        internal void TestNameMenu(string name) => NameMenu(name, new Point(DisplayLocation.X + S(40), DisplayLocation.Y + S(100)));
        internal void TestPopPick(int index) => _pop?.Activate(index);
        internal void TestSettings() => ToggleSettings();
        internal void TestTab(string key) { for (int i = 0; i < _tabs.Count; i++) if (_tabs[i].Key == key) { SelectTab(i); return; } }
        internal void TestScroll(int dy) => Log.ScrollBy(dy, true);

        // ================================================================== children

        /// <summary>The box's background and border while active (chat.ts .hchat.act: --pb at the chosen opacity, a frame
        /// border at 70 %, a soft shadow).</summary>
        private sealed class ChatBack : ModernControl
        {
            private readonly ChatHud _hud;

            public ChatBack(ChatHud hud)
            {
                _hud = hud;
                NotControl = true;
            }

            protected override void Paint(Graphics g)
            {
                if (!_hud._act) return;
                float k = K, rad = ModernTheme.Radius * k;
                RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
                using GraphicsPath p = ModernSkin.RoundRect(r, rad);
                // The surface's own target squares a fill's alpha (the blend has no separate alpha): sqrt keeps the chosen
                // opacity on screen.
                float op = MathF.Sqrt(Math.Clamp(Opts.Opacity / 100f, 0f, 0.9f));
                if (op > 0)
                {
                    using var b = new SolidBrush(ModernTheme.Alpha(ModernSkin.Opaque(ModernTheme.PanelB), op));
                    g.FillPath(b, p);
                }
                ModernSkin.Stroke(g, p, ModernTheme.Alpha(ModernTheme.Frame, 0.7f));
            }
        }

        /// <summary>The thin rule above the input row (chat.ts .cin border-top).</summary>
        private sealed class ChatRule : ModernControl
        {
            public ChatRule() { NotControl = true; }

            protected override void Paint(Graphics g)
            {
                using var b = new SolidBrush(ModernTheme.Alpha(ModernTheme.Frame, 0.5f));
                g.FillRectangle(b, 0, 0, Size.Width, Size.Height);
            }
        }

        /// <summary>The top bar's drag handle (move) and the corner grip (resize): pointer capture through Moving (chat.ts
        /// dragBy), measured once per gesture.</summary>
        internal sealed class ChatHandle : ModernControl
        {
            private readonly ChatHud _hud;
            private readonly bool _resize;
            private Point _start;
            private (int, int, int, int) _from;

            public ChatHandle(ChatHud hud, bool resize)
            {
                _hud = hud;
                _resize = resize;
            }

            public bool Dragging { get; private set; }

            public override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left || ChatHud.Locked) return;
                Dragging = true;
                Moving = true;
                _start = CMain.MPoint;
                _from = _hud.PlaceNow;
            }

            public override void OnMouseMove(MouseEventArgs e)
            {
                if (Dragging)
                {
                    var (l, b, w, h) = _from;
                    _hud.DragBy(_resize, CMain.MPoint.X - _start.X, CMain.MPoint.Y - _start.Y, l, b, w, h);
                    return;
                }
                base.OnMouseMove(e);
            }

            public override void OnMouseUp(MouseEventArgs e)
            {
                bool was = Dragging;
                Dragging = false;
                Moving = false;
                base.OnMouseUp(e);
                if (was) _hud.DragEnded(_resize);
            }

            public override bool IsMouseOver(Point p) => Visible && (DisplayRectangle.Contains(p) || Dragging);

            protected override void Paint(Graphics g)
            {
                if (!_resize) return;
                // chat.ts .grip: the accent corner, top right
                float k = K, w = Math.Max(1.5f, 2f * k);
                using var pen = new Pen(ModernTheme.Alpha(ModernTheme.Accent, 0.8f), w);
                float rad = ModernTheme.Radius * k;
                using var path = new GraphicsPath();
                path.AddLine(0, w / 2, Size.Width - rad, w / 2);
                if (rad > 0.5f) path.AddArc(Size.Width - rad * 2 - w / 2, w / 2, rad * 2, rad * 2, 270, 90);
                path.AddLine(Size.Width - w / 2, rad, Size.Width - w / 2, Size.Height);
                g.DrawPath(pen, path);
            }
        }

        /// <summary>The channel pill before the input (chat.ts .pill: the channel colour, a rounded border, a dark fill).</summary>
        internal sealed class ChatPill : ModernControl
        {
            private readonly ChatHud _hud;
            private string _text = string.Empty;
            private Color _colour;

            public ChatPill(ChatHud hud)
            {
                _hud = hud;
                Sound = SoundList.ButtonA;
                Size = new Size(S(50), S(22));
            }

            public void Refresh()
            {
                string text = _hud.PillText;
                Color c = ChatGfx.Channel(_hud._ch);
                int w = (int)Math.Ceiling(ChatGfx.Measure(text, ModernSkin.Font(11, true))) + S(18);
                if (text == _text && c == _colour && w == Size.Width) return;
                _text = text;
                _colour = c;
                Size = new Size(w, S(22));
                Invalidate();
            }

            public override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                _hud.ChannelMenu();
            }

            public override void OnMouseDoubleClick(MouseEventArgs e) => OnMouseClick(e);

            protected override void Paint(Graphics g)
            {
                RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
                using GraphicsPath p = ModernSkin.RoundRect(r, r.Height / 2f);
                using (var b = new SolidBrush(Color.FromArgb(0xd0, 5, 7, 12))) g.FillPath(b, p);
                ModernSkin.Stroke(g, p, _colour);
                ModernSkin.Text(g, _text, ModernSkin.Font(11, true), _colour, r, StringAlignment.Center);
            }
        }

        /// <summary>The dim hint under the log (chat.ts .hint: 10 px, text-shadow).</summary>
        private sealed class ChatHintLabel : ModernControl
        {
            private string _text = string.Empty;

            public ChatHintLabel() { NotControl = true; }

            public string Text { get => _text; set { if (_text == value) return; _text = value ?? string.Empty; Invalidate(); } }

            protected override void Paint(Graphics g)
            {
                Font f = ModernSkin.Font(10);
                ChatGfx.ShadowText(g, _text, f, ModernTheme.Dim, 0, (Size.Height - f.Height) / 2f, 1f, 0.8f);
            }
        }

        /// <summary>"N new messages ↓" (chat.ts .newp): a click follows the newest line again.</summary>
        private sealed class ChatNewPill : ModernControl
        {
            private readonly ChatHud _hud;
            private string _text = string.Empty;

            public ChatNewPill(ChatHud hud)
            {
                _hud = hud;
                Sound = SoundList.ButtonA;
                Size = new Size(S(120), S(18));
            }

            public string Text
            {
                get => _text;
                set
                {
                    if (_text == value) return;
                    _text = value ?? string.Empty;
                    Size = new Size((int)Math.Ceiling(ChatGfx.Measure(_text, ModernSkin.Font(10, true))) + S(20), S(18));
                    Invalidate();
                }
            }

            public override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                _hud.FollowEnd();
            }

            protected override void Paint(Graphics g)
            {
                RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
                using GraphicsPath p = ModernSkin.RoundRect(r, r.Height / 2f);
                using (var b = new SolidBrush(ModernSkin.Opaque(ModernTheme.Accent))) g.FillPath(b, p);
                ModernSkin.Text(g, _text, ModernSkin.Font(10, true), Color.White, r, StringAlignment.Center);
            }
        }
    }
}

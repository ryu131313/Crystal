using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Client.MirControls;
using Client.MirGraphics;
using Client.MirNetwork;
using Client.MirScenes.Modern.Core;
using Client.MirScenes.Modern.Core.Widgets;
using Client.MirSounds;
using C = ClientPackets;
using S = ServerPackets;

namespace Client.MirScenes.Modern.Screens
{
    /// <summary>
    /// The new UI's login screen (port of WebClient/src/ui/modern/screens/login.ts) on the classic LoginScene's flow: connect,
    /// version check (MD5 of the exe), C.Login / C.NewAccount (three-step wizard) / C.ChangePassword with the classic result
    /// texts, ban countdowns, S.LoginFailed tries left, and the ChrSel door (0-18) after S.LoginSuccess.
    /// Left out (later): the on-screen keyboard, ember / fog animation, the theme picker.
    /// </summary>
    public sealed class ModernLoginScene : ModernAccountScene
    {
        private static readonly Regex AccountReg = new Regex(@"^[A-Za-z0-9]{" + Globals.MinAccountIDLength + "," + Globals.MaxAccountIDLength + "}$");
        private static readonly Regex PasswordReg = new Regex(@"^[A-Za-z0-9]{" + Globals.MinPasswordLength + "," + Globals.MaxPasswordLength + "}$");
        private static readonly Regex EmailReg = new Regex(@"\w+([-+.]\w+)*@\w+([-.]\w+)*\.\w+([-.]\w+)*");
        private const int UserNameMax = 20, EmailMax = 50, SecretMax = 30;
        private const int CardX = 70, CardY = 150, CardW = 340, Pad = 24, Inner = CardW - Pad * 2;

        private enum View { Login, Create, Change }

        private readonly MirAnimatedControl _door;
        private readonly AccountPanel _card, _server;
        private ModernLabel _status, _ping, _version, _caps, _msg;
        private ModernButton _go;
        private ModernCheckBox _remember;
        private readonly AccountLink _classic;

        private View _view = View.Login;
        private int _step = 1;
        private readonly Dictionary<string, string> _form = new Dictionary<string, string>();
        private readonly Dictionary<string, (ModernTextField Field, ModernLabel Hint, Func<string, string> Check, string Describe)> _fields =
            new Dictionary<string, (ModernTextField, ModernLabel, Func<string, string>, string)>();

        private bool _ready, _busy, _entering, _showPassword;
        private Func<string> _msgLive;
        private long _nextSecond, _nextCaps, _nextPing;
        private int _shownAttempt = -1;
        private long _pingMs = -1;

        public ModernLoginScene(bool connected)
        {
            SoundManager.PlayMusic(SoundList.IntroMusic, true);
            Disposing += (o, e) => SoundManager.StopMusic();
            // a new log in: the previous account's select details must not show (they are sent again after S.LoginSuccess)
            AccountState.Details = null;
            AccountState.Rules = null;

            _door = new MirAnimatedControl
            {
                Animated = false,
                AnimationCount = 19,
                AnimationDelay = 100,
                Index = 0,
                Library = Libraries.ChrSel,
                Loop = false,
                Parent = this,
                NotControl = true,
            };
            new AccountShade(false) { Parent = this };

            int W = Settings.ScreenWidth, H = Settings.ScreenHeight;
            new ModernLabel { Parent = Root.Hud, Location = new Point(S(CardX), S(40)), Size = new Size(S(520), S(58)), Text = T(ClientTextKeys.ModernAcctLogo), FontSize = 46, Bold = true, Colour = ModernTheme.Gold };
            new ModernLabel { Parent = Root.Hud, Location = new Point(S(CardX + 3), S(98)), Size = new Size(S(400), S(20)), Text = T(ClientTextKeys.ModernAcctLogoSub), FontSize = 13, Bold = true, Colour = ModernTheme.Accent };

            _card = new AccountPanel(Root.Hud, CardX, CardY, CardW, 400);
            _server = new AccountPanel(Root.Hud, (int)(W / ModernRoot.K) - 70 - 250, CardY, 250, 150);
            BuildServerCard();

            new ModernLabel
            {
                Parent = Root.Hud,
                Location = new Point(S(20), H - S(28)),
                Size = new Size(S(400), S(18)),
                FontSize = 11,
                Colour = ModernTheme.Dim,
                Text = string.Format("Build: {0}.{1}.{2}", Globals.ProductCodename, Settings.UseTestConfig ? "Debug" : "Release", Application.ProductVersion),
            };
            AccountLink close = new AccountLink(T(ClientTextKeys.ModernAcctClose), 12) { Parent = Root.Hud, Colour = ModernTheme.Dim };
            close.Location = new Point(W - S(20) - close.Size.Width, H - S(30));
            close.Click += (o, e) => Program.Form.Close();
            _classic = new AccountLink(T(ClientTextKeys.ModernAcctClassic), 12) { Parent = Root.Hud, Colour = ModernTheme.Dim };
            _classic.Location = new Point(close.Location.X - S(24) - _classic.Size.Width, H - S(30));
            _classic.Click += (o, e) => SwitchToClassic();

            _form["id"] = Settings.AccountID ?? string.Empty;
            BuildCard();

            if (connected) Shown += (o, e) => ShowLogin();
            else Shown += (o, e) => Network.Connect();
        }

        // ------------------------------------------------------------------ cards

        private void BuildServerCard()
        {
            int w = 250 - 32;
            Label(_server, ClientTextKeys.ModernAcctServer, 16, 14, w, 14, 10, ModernTheme.Dim, true);
            Label(_server, T(ClientTextKeys.GameName), 16, 30, w, 22, 15, Color.White, true);
            _status = Label(_server, T(ClientTextKeys.ModernAcctConnecting), 16, 56, w, 18, 12, ModernTheme.Warn);
            string address = Settings.Relay.Length > 0 ? Settings.Relay : Settings.IPAddress + ":" + Settings.Port;
            Label(_server, address, 16, 76, w, 16, 11, ModernTheme.Dim);
            _ping = Label(_server, T(ClientTextKeys.ModernAcctLatency, "-"), 16, 100, w, 18, 12, ModernTheme.Text);
            _version = Label(_server, T(ClientTextKeys.ModernAcctVersionChecking), 16, 120, w, 18, 12, ModernTheme.Text);
        }

        private void BuildCard()
        {
            _card.Clear();
            _fields.Clear();
            _caps = null;
            _remember = null;
            switch (_view)
            {
                case View.Create:
                    BuildCreate();
                    break;
                case View.Change:
                    BuildChange();
                    break;
                default:
                    BuildLogin();
                    break;
            }
            RefreshGo();
            RefreshMessage();
        }

        private void Heading(ClientTextKeys title, string sub)
        {
            Label(_card, title, Pad, 18, Inner, 26, 18, Color.White, true);
            Label(_card, sub, Pad, 44, Inner, 16, 11, ModernTheme.Dim);
        }

        private void BuildLogin()
        {
            Heading(ClientTextKeys.ModernAcctWelcome, T(ClientTextKeys.ModernAcctWelcomeSub));
            Field("id", ClientTextKeys.ModernAcctAccountId, 72, false, Globals.MaxAccountIDLength, CheckId, IdDescribe);
            Field("pw", ClientTextKeys.ModernAcctPassword, 138, true, Globals.MaxPasswordLength, CheckPw, PwDescribe);

            AccountLink eye = new AccountLink(T(_showPassword ? ClientTextKeys.ModernAcctHide : ClientTextKeys.ModernAcctShow), 11, StringAlignment.Far) { Parent = _card, Colour = ModernTheme.Dim };
            eye.Location = new Point(S(Pad + Inner) - eye.Size.Width, S(134));
            eye.Click += (o, e) =>
            {
                _showPassword = !_showPassword;
                if (_fields.TryGetValue("pw", out var pw)) pw.Field.Password = !_showPassword;
                eye.Text = T(_showPassword ? ClientTextKeys.ModernAcctHide : ClientTextKeys.ModernAcctShow);
            };
            if (_fields.TryGetValue("pw", out var p)) p.Field.Password = !_showPassword;

            _caps = Label(_card, T(ClientTextKeys.ModernAcctCapsLock), Pad, 204, Inner, 16, 11, ModernTheme.Warn);
            _caps.Visible = Control.IsKeyLocked(Keys.CapsLock);

            _remember = new ModernCheckBox(T(ClientTextKeys.ModernAcctRemember), !string.IsNullOrEmpty(Settings.AccountID))
            {
                Parent = _card,
                Location = new Point(S(Pad), S(224)),
            };
            AccountLink change = new AccountLink(T(ClientTextKeys.ModernAcctChangePassword), 12) { Parent = _card };
            change.Location = new Point(S(Pad + Inner) - change.Size.Width, S(224));
            change.Click += (o, e) =>
            {
                if (_busy || _entering) return;
                string id = Get("id");
                SetView(View.Change);
                _form["id"] = id;
                BuildCard();
            };

            _go = new ModernButton(string.Empty, ButtonKind.Main, true) { Parent = _card, Location = new Point(S(Pad), S(256)), Size = new Size(S(Inner), S(40)), FontSize = 15 };
            _go.Click += (o, e) => Go();

            _msg = Label(_card, string.Empty, Pad, 306, Inner, 50, 12, ModernTheme.Text, false, true);

            ModernButton create = new ModernButton(T(ClientTextKeys.ModernAcctCreateAccount)) { Parent = _card, Location = new Point(S(Pad), S(362)), Size = new Size(S(Inner), S(28)) };
            create.Click += (o, e) =>
            {
                if (_busy || _entering) return;
                SetView(View.Create);
                BuildCard();
            };
            _card.SetHeight(408);
        }

        private void BuildCreate()
        {
            Heading(ClientTextKeys.ModernAcctCreateTitle, T(ClientTextKeys.ModernAcctStep, _step, 3));
            switch (_step)
            {
                case 1:
                    Field("id", ClientTextKeys.ModernAcctAccountId, 72, false, Globals.MaxAccountIDLength, CheckId, IdDescribe);
                    Field("pw", ClientTextKeys.ModernAcctPassword, 138, true, Globals.MaxPasswordLength, CheckPw, PwDescribe);
                    Field("pw2", ClientTextKeys.ModernAcctConfirmPassword, 204, true, Globals.MaxPasswordLength, v => v == Get("pw") ? string.Empty : T(ClientTextKeys.ModernAcctPasswordsDiffer), string.Empty);
                    break;
                case 2:
                    Field("user", ClientTextKeys.ModernAcctUserName, 72, false, UserNameMax, null, T(ClientTextKeys.ModernAcctOptional));
                    Field("birth", ClientTextKeys.ModernAcctBirthDate, 138, false, 10, CheckBirth, T(ClientTextKeys.ModernAcctBirthHint, System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern));
                    Field("mail", ClientTextKeys.ModernAcctEmail, 204, false, EmailMax, CheckMail, T(ClientTextKeys.ModernAcctOptional));
                    break;
                default:
                    Field("question", ClientTextKeys.ModernAcctSecretQuestion, 72, false, SecretMax, null, T(ClientTextKeys.ModernAcctOptional));
                    Field("answer", ClientTextKeys.ModernAcctSecretAnswer, 138, false, SecretMax, null, T(ClientTextKeys.ModernAcctOptional));
                    break;
            }

            int half = (Inner - 8) / 2;
            ModernButton back = new ModernButton(T(_step == 1 ? ClientTextKeys.ModernAcctCancel : ClientTextKeys.ModernAcctBack), ButtonKind.Normal, true)
            {
                Parent = _card,
                Location = new Point(S(Pad), S(280)),
                Size = new Size(S(half), S(34)),
            };
            back.Click += (o, e) =>
            {
                if (_busy) return;
                Remember();
                if (_step == 1) BackToLogin();
                else
                {
                    _step--;
                    BuildCard();
                }
            };
            _go = new ModernButton(string.Empty, ButtonKind.Main, true) { Parent = _card, Location = new Point(S(Pad + half + 8), S(280)), Size = new Size(S(half), S(34)) };
            _go.Click += (o, e) => Go();
            _msg = Label(_card, string.Empty, Pad, 324, Inner, 50, 12, ModernTheme.Text, false, true);
            BackLink(380);
            _card.SetHeight(410);
        }

        private void BuildChange()
        {
            Heading(ClientTextKeys.ModernAcctChangeTitle, T(ClientTextKeys.ModernAcctChangeSub));
            Field("id", ClientTextKeys.ModernAcctAccountId, 72, false, Globals.MaxAccountIDLength, CheckId, IdDescribe);
            Field("cpw", ClientTextKeys.ModernAcctCurrentPassword, 138, true, Globals.MaxPasswordLength, CheckPw, string.Empty);
            Field("npw", ClientTextKeys.ModernAcctNewPassword, 204, true, Globals.MaxPasswordLength, CheckPw, PwDescribe);
            Field("npw2", ClientTextKeys.ModernAcctConfirmPassword, 270, true, Globals.MaxPasswordLength, v => v == Get("npw") ? string.Empty : T(ClientTextKeys.ModernAcctPasswordsDiffer), string.Empty);
            _go = new ModernButton(string.Empty, ButtonKind.Main, true) { Parent = _card, Location = new Point(S(Pad), S(340)), Size = new Size(S(Inner), S(38)) };
            _go.Click += (o, e) => Go();
            _msg = Label(_card, string.Empty, Pad, 386, Inner, 50, 12, ModernTheme.Text, false, true);
            BackLink(442);
            _card.SetHeight(472);
        }

        private void BackLink(int y)
        {
            AccountLink back = new AccountLink(T(ClientTextKeys.ModernAcctBackToLogin), 12) { Parent = _card };
            back.Location = new Point(S(Pad), S(y));
            back.Click += (o, e) =>
            {
                if (_busy) return;
                BackToLogin();
            };
        }

        private void BackToLogin()
        {
            string id = Get("id");
            SetView(View.Login);
            _form["id"] = id;
            BuildCard();
        }

        private void SetView(View view)
        {
            Remember();
            _view = view;
            _step = 1;
            _form.Clear();
            _msgLive = null;
            _msgText = string.Empty;
        }

        // ------------------------------------------------------------------ fields

        private string IdDescribe => T(ClientTextKeys.ModernAcctIdRule, Globals.MinAccountIDLength, Globals.MaxAccountIDLength);
        private string PwDescribe => T(ClientTextKeys.ModernAcctIdRule, Globals.MinPasswordLength, Globals.MaxPasswordLength);

        private string CheckId(string v) => AccountReg.IsMatch(v) ? string.Empty : IdDescribe;
        private string CheckPw(string v) => PasswordReg.IsMatch(v) ? string.Empty : PwDescribe;
        private string CheckMail(string v) => v.Length == 0 || (EmailReg.IsMatch(v) && v.Length <= EmailMax) ? string.Empty : T(ClientTextKeys.ModernAcctBadEmail);
        private string CheckBirth(string v) => v.Length == 0 || (v.Length <= 10 && DateTime.TryParse(v, out _)) ? string.Empty : T(ClientTextKeys.ModernAcctBadDate);

        /// <summary>A labelled field (label, box, hint line) at y; 66 HUD px tall.</summary>
        private void Field(string key, ClientTextKeys label, int y, bool password, int max, Func<string, string> check, string describe)
        {
            Label(_card, T(label).ToUpperInvariant(), Pad, y, Inner, 14, 10, ModernTheme.Dim, true);
            ModernTextField field = new ModernTextField(Inner, 32, 13) { Parent = _card, Location = new Point(S(Pad), S(y + 16)), MaxLength = max, Password = password };
            field.Text = Get(key);
            ModernLabel hint = Label(_card, describe, Pad, y + 50, Inner, 14, 10, ModernTheme.Dim);
            _fields[key] = (field, hint, check, describe ?? string.Empty);
            field.TextChanged += t =>
            {
                _form[key] = t ?? string.Empty;
                RefreshHint(key);
                RefreshGo();
            };
            field.Submitted += t => Go();
            RefreshHint(key);
        }

        private void RefreshHint(string key)
        {
            if (!_fields.TryGetValue(key, out var f) || f.Hint.IsDisposed) return;
            string v = Get(key);
            string why = v.Length == 0 || f.Check == null ? string.Empty : f.Check(v);
            f.Hint.Text = why.Length > 0 ? why : f.Describe;
            f.Hint.Colour = why.Length > 0 ? ModernTheme.BadSoft : v.Length > 0 && f.Check != null ? ModernTheme.Ok : ModernTheme.Dim;
        }

        private string Get(string key) => _form.TryGetValue(key, out string v) ? v ?? string.Empty : string.Empty;

        /// <summary>The fields keep their text in <see cref="_form"/> already (TextChanged); this only refreshes after a rebuild.</summary>
        private void Remember()
        {
            foreach (var kv in _fields)
                if (!kv.Value.Field.IsDisposed) _form[kv.Key] = kv.Value.Field.Text;
        }

        private bool Valid(string key)
        {
            if (!_fields.TryGetValue(key, out var f)) return true;
            string v = Get(key);
            if (f.Check == null) return true;
            return f.Check(v).Length == 0;
        }

        private bool StepValid()
        {
            foreach (var kv in _fields)
            {
                string v = Get(kv.Key);
                bool required = kv.Key == "id" || kv.Key == "pw" || kv.Key == "pw2" || kv.Key == "cpw" || kv.Key == "npw" || kv.Key == "npw2";
                if (required && v.Length == 0) return false;
                if (!Valid(kv.Key)) return false;
            }
            return true;
        }

        private void RefreshGo()
        {
            if (_go == null || _go.IsDisposed) return;
            switch (_view)
            {
                case View.Login:
                    _go.Text = !_ready ? T(Network.Connected ? ClientTextKeys.ModernAcctChecking : ClientTextKeys.ModernAcctConnectingButton)
                        : _busy ? T(ClientTextKeys.ModernAcctLoggingIn) : T(ClientTextKeys.ModernAcctLogIn);
                    break;
                case View.Create:
                    _go.Text = _busy ? T(ClientTextKeys.ModernAcctCreating) : _step < 3 ? T(ClientTextKeys.ModernAcctNext) : T(ClientTextKeys.ModernAcctCreate);
                    break;
                default:
                    _go.Text = _busy ? T(ClientTextKeys.ModernAcctChanging) : T(ClientTextKeys.ModernAcctChangeButton);
                    break;
            }
            _go.SetDisabled(!_ready || _busy || _entering || !StepValid());
        }

        // ------------------------------------------------------------------ message line

        private string _msgText = string.Empty;
        private Color _msgColour = Color.Empty;

        private void Message(string text, Color colour, Func<string> live = null)
        {
            _msgText = text ?? string.Empty;
            _msgColour = colour;
            _msgLive = live;
            RefreshMessage();
        }

        private void RefreshMessage()
        {
            if (_msg == null || _msg.IsDisposed) return;
            _msg.Text = _msgLive != null ? _msgLive() : _msgText;
            _msg.Colour = _msgColour.IsEmpty ? ModernTheme.Text : _msgColour;
        }

        // ------------------------------------------------------------------ actions

        private void ShowLogin()
        {
            _ready = true;
            _status.Text = T(ClientTextKeys.ModernAcctOnline);
            _status.Colour = ModernTheme.Ok;
            _version.Text = T(ClientTextKeys.ModernAcctVersionOk);
            _version.Colour = ModernTheme.Ok;
            SendPing();
            RefreshGo();

            // the classic auto-login: both saved in the ini ([Game] AccountID / Password)
            if (_view == View.Login && Settings.AccountID != string.Empty && Settings.Password != string.Empty)
            {
                _form["id"] = Settings.AccountID;
                _form["pw"] = Settings.Password;
                BuildCard();
                Login();
            }
            else if (_view == View.Login && _fields.TryGetValue(Get("id").Length > 0 ? "pw" : "id", out var f))
                f.Field.Focus();
        }

        /// <summary>The card's main button or Enter in a field.</summary>
        private void Go()
        {
            switch (_view)
            {
                case View.Login:
                    Login();
                    break;
                case View.Change:
                    ChangePassword();
                    break;
                default:
                    if (_busy || !_ready || !StepValid()) return;
                    Remember();
                    if (_step < 3)
                    {
                        _step++;
                        BuildCard();
                    }
                    else CreateAccount();
                    break;
            }
        }

        private void Login()
        {
            if (_view != View.Login || !_ready || _busy || _entering || !StepValid()) return;
            _busy = true;
            Message(string.Empty, Color.Empty);
            RefreshGo();
            Network.Enqueue(new C.Login { AccountID = Get("id"), Password = Get("pw") });
        }

        private void CreateAccount()
        {
            if (!_ready || _busy) return;
            if (!AccountReg.IsMatch(Get("id")) || !PasswordReg.IsMatch(Get("pw")) || Get("pw") != Get("pw2"))
            {
                _step = 1;
                BuildCard();
                return;
            }
            string birth = Get("birth");
            _busy = true;
            RefreshGo();
            Network.Enqueue(new C.NewAccount
            {
                AccountID = Get("id"),
                Password = Get("pw"),
                EMailAddress = Get("mail"),
                BirthDate = birth.Length > 0 && DateTime.TryParse(birth, out DateTime date) ? date : DateTime.MinValue,
                UserName = Get("user"),
                SecretQuestion = Get("question"),
                SecretAnswer = Get("answer"),
            });
        }

        private void ChangePassword()
        {
            if (_view != View.Change || !_ready || _busy || !StepValid()) return;
            _busy = true;
            Message(string.Empty, Color.Empty);
            RefreshGo();
            Network.Enqueue(new C.ChangePassword { AccountID = Get("id"), CurrentPassword = Get("cpw"), NewPassword = Get("npw") });
        }

        private void SendPing()
        {
            _nextPing = CMain.Time + 5000;
            if (Network.Connected) Network.Enqueue(new C.KeepAlive { Time = CMain.Time });
        }

        private void SwitchToClassic()
        {
            if (_entering) return;
            Settings.UiStyle = UiStyle.Classic;
            ActiveScene = _ready ? new LoginScene(true) : new LoginScene();
            Dispose();
        }

        private void Focus(string key)
        {
            if (_fields.TryGetValue(key, out var f) && !f.Field.IsDisposed) f.Field.Focus();
        }

        // ------------------------------------------------------------------ frame

        protected override void Tick()
        {
            if (!Network.Connected && Network.ConnectAttempt > 0 && Network.ConnectAttempt != _shownAttempt && _status != null)
            {
                _shownAttempt = Network.ConnectAttempt;
                _status.Text = T(ClientTextKeys.ModernAcctConnectingAttempt, _shownAttempt);
                _status.Colour = ModernTheme.Warn;
            }
            if (CMain.Time >= _nextSecond)
            {
                _nextSecond = CMain.Time + 1000;
                if (_msgLive != null) RefreshMessage();
            }
            if (_caps != null && !_caps.IsDisposed && CMain.Time >= _nextCaps)
            {
                _nextCaps = CMain.Time + 250;
                bool caps = Control.IsKeyLocked(Keys.CapsLock);
                if (_caps.Visible != caps) _caps.Visible = caps;
            }
            if (_ready && !_entering && CMain.Time >= _nextPing) SendPing();
        }

        // ------------------------------------------------------------------ packets

        public override void ProcessPacket(Packet p)
        {
            if (AccountState.Take(p)) return;
            switch (p.Index)
            {
                case (short)ServerPacketIds.Connected:
                    Network.Connected = true;
                    _status.Text = T(ClientTextKeys.ModernAcctChecking);
                    RefreshGo();
                    SendVersion();
                    break;
                case (short)ServerPacketIds.ClientVersion:
                    ClientVersion((S.ClientVersion)p);
                    break;
                case (short)ServerPacketIds.KeepAlive:
                    long sent = ((S.KeepAlive)p).Time;
                    if (sent > 0 && sent <= CMain.Time)
                    {
                        long ms = CMain.Time - sent;
                        if (ms != _pingMs)
                        {
                            _pingMs = ms;
                            _ping.Text = T(ClientTextKeys.ModernAcctLatency, ms + " ms");
                        }
                    }
                    break;
                case (short)ServerPacketIds.NewAccount:
                    NewAccount((S.NewAccount)p);
                    break;
                case (short)ServerPacketIds.ChangePassword:
                    ChangePasswordResult((S.ChangePassword)p);
                    break;
                case (short)ServerPacketIds.ChangePasswordBanned:
                    S.ChangePasswordBanned cb = (S.ChangePasswordBanned)p;
                    _busy = false;
                    Banned(cb.Reason, cb.ExpiryDate);
                    RefreshGo();
                    break;
                case (short)ServerPacketIds.Login:
                    LoginResult((S.Login)p);
                    break;
                case (short)ServerPacketIds.LoginBanned:
                    S.LoginBanned lb = (S.LoginBanned)p;
                    _busy = false;
                    Banned(lb.Reason, lb.ExpiryDate);
                    RefreshGo();
                    break;
                case (short)ServerPacketIds.LoginFailed:
                    LoginFailed((S.LoginFailed)p);
                    break;
                case (short)ServerPacketIds.LoginSuccess:
                    LoginSuccess((S.LoginSuccess)p);
                    break;
                default:
                    base.ProcessPacket(p);
                    break;
            }
        }

        private void SendVersion()
        {
            C.ClientVersion p = new C.ClientVersion();
            try
            {
                byte[] sum;
                using (MD5 md5 = MD5.Create())
                using (FileStream stream = File.OpenRead(Application.ExecutablePath))
                    sum = md5.ComputeHash(stream);
                p.VersionHash = sum;
                Network.Enqueue(p);
            }
            catch (Exception ex)
            {
                if (Settings.LogErrors) CMain.SaveError("[ModernLogin] " + ex);
            }
        }

        private void ClientVersion(S.ClientVersion p)
        {
            switch (p.Result)
            {
                case 0:
                    _version.Text = T(ClientTextKeys.ModernAcctVersionBad);
                    _version.Colour = ModernTheme.Bad;
                    Say(T(ClientTextKeys.WrongVersionPleaseUpdateGame), PopupKind.Warning, () => Program.Form.Close());
                    Network.Disconnect();
                    break;
                case 1:
                    ShowLogin();
                    break;
            }
        }

        private void Banned(string reason, DateTime expiry)
        {
            Message(string.Empty, ModernTheme.BadSoft, () =>
            {
                TimeSpan d = expiry - CMain.Now;
                if (d < TimeSpan.Zero) d = TimeSpan.Zero;
                return T(ClientTextKeys.AccountBannedReasonDuration, reason, expiry, Math.Floor(d.TotalHours), d.Minutes, d.Seconds);
            });
        }

        private void LoginResult(S.Login p)
        {
            _busy = false;
            if (_view != View.Login)
            {
                SetView(View.Login);
                BuildCard();
            }
            string focus = "pw";
            switch (p.Result)
            {
                case 0:
                    Message(T(ClientTextKeys.LoginDisabled), ModernTheme.BadSoft);
                    _form["id"] = string.Empty;
                    _form["pw"] = string.Empty;
                    BuildCard();
                    focus = "id";
                    break;
                case 1:
                    Message(T(ClientTextKeys.AccountIdNotAcceptable), ModernTheme.BadSoft);
                    focus = "id";
                    break;
                case 2:
                    Message(T(ClientTextKeys.PasswordNotAcceptable), ModernTheme.BadSoft);
                    break;
                case 3:
                    Message(T(ClientTextKeys.NoAccountID), ModernTheme.BadSoft);
                    focus = "id";
                    break;
                case 4:
                    Message(T(ClientTextKeys.IncorrectPasswordAccountID), ModernTheme.BadSoft);
                    _form["pw"] = string.Empty;
                    if (_fields.TryGetValue("pw", out var pw)) pw.Field.Text = string.Empty;
                    break;
                case 5:
                    string id = Get("id"), cpw = Get("pw");
                    SetView(View.Change);
                    _form["id"] = id;
                    _form["cpw"] = cpw;
                    BuildCard();
                    Message(T(ClientTextKeys.AccountPasswordMustChangeBeforeLogin), ModernTheme.Warn);
                    Focus("npw");
                    RefreshGo();
                    return;
            }
            RefreshGo();
            Focus(focus);
        }

        /// <summary>S.LoginFailed, right after S.Login 3 / 4: the tries left (login.ts loginFailed).</summary>
        private void LoginFailed(S.LoginFailed p)
        {
            if (_view != View.Login || _msgLive != null) return;
            string text = _msgText;
            if (p.AccountTriesLeft > 0) text += "\n" + T(ClientTextKeys.ModernAcctTriesLeft, p.AccountTriesLeft);
            else if (p.AccountTriesLeft == 0) text += "\n" + T(ClientTextKeys.ModernAcctLastTry);
            if (p.ConnectionTriesLeft == 0) text += "\n" + T(ClientTextKeys.ModernAcctConnectionClosing);
            else if (p.ConnectionTriesLeft > 0 && p.ConnectionTriesLeft <= 2) text += "\n" + T(ClientTextKeys.ModernAcctConnectionTries, p.ConnectionTriesLeft);
            Message(text.TrimStart('\n'), ModernTheme.BadSoft);
        }

        private void NewAccount(S.NewAccount p)
        {
            _busy = false;
            int step = 0;
            string field = null;
            switch (p.Result)
            {
                case 0:
                    Message(T(ClientTextKeys.AccountCreationDisabled), ModernTheme.BadSoft);
                    break;
                case 1:
                    Message(T(ClientTextKeys.AccountIdNotAcceptable), ModernTheme.BadSoft);
                    step = 1; field = "id";
                    break;
                case 2:
                    Message(T(ClientTextKeys.PasswordNotAcceptable), ModernTheme.BadSoft);
                    step = 1; field = "pw";
                    break;
                case 3:
                    Message(T(ClientTextKeys.EmailAddressNotAcceptable), ModernTheme.BadSoft);
                    step = 2; field = "mail";
                    break;
                case 4:
                    Message(T(ClientTextKeys.UserNameNotAcceptable), ModernTheme.BadSoft);
                    step = 2; field = "user";
                    break;
                case 5:
                    Message(T(ClientTextKeys.SecretQuestionNotAcceptable), ModernTheme.BadSoft);
                    step = 3; field = "question";
                    break;
                case 6:
                    Message(T(ClientTextKeys.SecretAnswerNotAcceptable), ModernTheme.BadSoft);
                    step = 3; field = "answer";
                    break;
                case 7:
                    Message(T(ClientTextKeys.AccountIdAlreadyExists), ModernTheme.BadSoft);
                    _form["id"] = string.Empty;
                    step = 1; field = "id";
                    break;
                case 8:
                    string id = Get("id");
                    SetView(View.Login);
                    _form["id"] = id;
                    BuildCard();
                    Message(T(ClientTextKeys.AccountCreatedSuccessfully), ModernTheme.Ok);
                    Focus("pw");
                    return;
            }
            if (_view == View.Create && step > 0 && step != _step)
            {
                string text = _msgText;
                Color colour = _msgColour;
                _step = step;
                BuildCard();
                Message(text, colour);
            }
            RefreshGo();
            if (field != null) Focus(field);
        }

        private void ChangePasswordResult(S.ChangePassword p)
        {
            _busy = false;
            switch (p.Result)
            {
                case 0:
                    Message(T(ClientTextKeys.PasswordChangingDisabled), ModernTheme.BadSoft);
                    break;
                case 1:
                    Message(T(ClientTextKeys.AccountIdNotAcceptable), ModernTheme.BadSoft);
                    Focus("id");
                    break;
                case 2:
                    Message(T(ClientTextKeys.CurrentPasswordNotAcceptable), ModernTheme.BadSoft);
                    Focus("cpw");
                    break;
                case 3:
                    Message(T(ClientTextKeys.NewPasswordNotAcceptable), ModernTheme.BadSoft);
                    Focus("npw");
                    break;
                case 4:
                    Message(T(ClientTextKeys.NoAccountID), ModernTheme.BadSoft);
                    Focus("id");
                    break;
                case 5:
                    Message(T(ClientTextKeys.IncorrectPasswordAccountID), ModernTheme.BadSoft);
                    _form["cpw"] = string.Empty;
                    if (_fields.TryGetValue("cpw", out var f)) f.Field.Text = string.Empty;
                    Focus("cpw");
                    break;
                case 6:
                    string id = Get("id");
                    SetView(View.Login);
                    _form["id"] = id;
                    BuildCard();
                    Message(T(ClientTextKeys.PasswordChangedSuccessfully), ModernTheme.Ok);
                    Focus("pw");
                    return;
            }
            RefreshGo();
        }

        private void LoginSuccess(S.LoginSuccess p)
        {
            if (_entering) return;
            _entering = true;
            Settings.AccountID = _remember != null && _remember.Checked ? Get("id") : string.Empty;
            List<SelectInfo> characters = p.Characters ?? new List<SelectInfo>();

            Enabled = false;
            Root.Visible = false;
            if (Program.Form != null) Program.Form.ActiveControl = null;
            SoundManager.PlaySound(SoundList.LoginEffect);
            _door.Animated = true;
            _door.AfterAnimation += (o, e) =>
            {
                ActiveScene = new ModernSelectScene(characters);
                Dispose();
            };
        }

        // ------------------------------------------------------------------ helpers

        private static ModernLabel Label(MirControl parent, ClientTextKeys key, int x, int y, int w, int h, float size, Color colour, bool bold = false, bool wrap = false) =>
            Label(parent, T(key), x, y, w, h, size, colour, bold, wrap);

        private static ModernLabel Label(MirControl parent, string text, int x, int y, int w, int h, float size, Color colour, bool bold = false, bool wrap = false) =>
            new ModernLabel
            {
                Parent = parent,
                Location = new Point(S(x), S(y)),
                Size = new Size(S(w), S(h)),
                Text = text,
                FontSize = size,
                Colour = colour,
                Bold = bold,
                Wrap = wrap,
            };
    }
}

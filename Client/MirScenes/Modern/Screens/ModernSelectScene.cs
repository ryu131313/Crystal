using System.Drawing.Drawing2D;
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
    /// The new UI's character select screen (port of WebClient/src/ui/modern/screens/select.ts) on the classic SelectScene's
    /// flow: roster (most recently played first), the ChrSel class preview with its +560 glow, details from S.SelectDetails
    /// (guild, location, play time, account gold; hidden until it arrives), create (class gating from S.ServerRules), delete
    /// (Yes / No, then type the name), start (waits for the game libraries, S.StartGameDelay countdown re-sends).
    /// "Back to log in" is left out: the server has no select-to-login step (the web page reloads).
    /// </summary>
    public sealed class ModernSelectScene : ModernAccountScene
    {
        /// <summary>Envir.CharacterReg: 3-15 letters, digits, _ or CJK.</summary>
        private static readonly Regex NameReg = new Regex(@"^[一-龥_A-Za-z0-9]{" + Globals.MinCharacterNameLength + "," + Globals.MaxCharacterNameLength + "}$");
        private const int BackdropIndex = 18, PanelW = 270, Side = 28, Top = 80, RowH = 58, PreviewBottom = 590;
        private static readonly MirClass[] Classes = { MirClass.Warrior, MirClass.Wizard, MirClass.Taoist, MirClass.Assassin, MirClass.Archer };

        private readonly List<SelectInfo> _characters;
        private readonly MirAnimatedControl _preview;
        private readonly AccountPanel _left, _right;
        private readonly ModernLabel _mode, _plateName, _plateLine, _tip;
        private readonly ModernButton _start;
        private MirAnimatedControl _spinner;

        private bool _create;
        private int _selected;
        private MirClass _cClass = MirClass.Warrior;
        private MirGender _cGender = MirGender.Male;
        private string _cName = string.Empty;
        private ModernTextField _nameField;
        private ModernLabel _nameHint;
        private ModernButton _createButton;

        private bool _entering, _waitingLibraries, _creating, _deleting;
        private long _delayUntil;
        private int _shownWait = -1, _shownPreview = -1;
        private S.SelectDetails _detailsShown;
        private S.ServerRules _rulesShown;

        public ModernSelectScene(List<SelectInfo> characters)
        {
            _characters = characters ?? new List<SelectInfo>();
            _characters.Sort((c1, c2) => c2.LastAccess.CompareTo(c1.LastAccess));
            int W = Settings.ScreenWidth, H = Settings.ScreenHeight;

            new MirImageControl { Library = Libraries.ChrSel, Index = BackdropIndex, Parent = this, NotControl = true, Opacity = 0.45F };
            new AccountShade(true) { Parent = this };
            _preview = new MirAnimatedControl
            {
                Animated = true,
                AnimationCount = 16,
                AnimationDelay = 250,
                Library = Libraries.ChrSel,
                Index = 20,
                Parent = this,
                UseOffSet = true,
                NotControl = true,
                Visible = false,
            };
            _preview.AfterDraw += (o, e) =>
            {
                if (_preview.Visible) Libraries.ChrSel.DrawBlend(_preview.Index + 560, _preview.DisplayLocationWithoutOffSet, Color.White, true);
            };

            new ModernLabel { Parent = Root.Hud, Location = new Point(S(Side), S(14)), Size = new Size(S(260), S(30)), Text = T(ClientTextKeys.ModernAcctLogo), FontSize = 22, Bold = true, Colour = ModernTheme.Gold };
            _mode = new ModernLabel { Parent = Root.Hud, Location = new Point(S(Side + 250), S(20)), Size = new Size(S(300), S(20)), FontSize = 11, Bold = true, Colour = ModernTheme.Dim };
            AccountLink exit = new AccountLink(T(ClientTextKeys.ModernAcctExit), 12) { Parent = Root.Hud };
            exit.Location = new Point(W - S(Side) - exit.Size.Width, S(20));
            exit.Click += (o, e) => Program.Form.Close();

            _left = new AccountPanel(Root.Hud, Side, Top, PanelW, 100);
            _right = new AccountPanel(Root.Hud, (int)(W / ModernRoot.K) - Side - PanelW, Top, PanelW, 100);

            _plateName = new ModernLabel { Parent = Root.Hud, Location = new Point(W / 2 - S(200), S(PreviewBottom + 8)), Size = new Size(S(400), S(38)), FontSize = 28, Bold = true, Colour = Color.White, Align = StringAlignment.Center };
            _plateLine = new ModernLabel { Parent = Root.Hud, Location = new Point(W / 2 - S(200), S(PreviewBottom + 44)), Size = new Size(S(400), S(18)), FontSize = 12, Bold = true, Colour = ModernTheme.Accent, Align = StringAlignment.Center };

            _start = new ModernButton(T(ClientTextKeys.ModernAcctStartGame), ButtonKind.Main, true)
            {
                Parent = Root.Hud,
                Size = new Size(S(300), S(48)),
                Location = new Point(W / 2 - S(150), H - S(100)),
                FontSize = 17,
            };
            _start.Click += (o, e) => StartGame();
            _tip = new ModernLabel { Parent = Root.Hud, Location = new Point(W / 2 - S(250), H - S(34)), Size = new Size(S(500), S(18)), FontSize = 11, Colour = ModernTheme.Dim, Align = StringAlignment.Center, Text = T(ClientTextKeys.ModernAcctSelectTip) };

            AccountLink classic = new AccountLink(T(ClientTextKeys.ModernAcctClassic), 11) { Parent = Root.Hud, Colour = ModernTheme.Dim };
            classic.Location = new Point(W - S(Side) - classic.Size.Width, H - S(30));
            classic.Click += (o, e) => SwitchToClassic();

            _create = _characters.Count == 0;
            Render();
        }

        private SelectInfo Current => _selected >= 0 && _selected < _characters.Count ? _characters[_selected] : null;

        // ------------------------------------------------------------------ render

        private void Render()
        {
            _left.Clear();
            _right.Clear();
            _nameField = null;
            _nameHint = null;
            _createButton = null;
            _detailsShown = AccountState.Details;
            _rulesShown = AccountState.Rules;
            if (_create)
            {
                _mode.Text = T(ClientTextKeys.ModernAcctModeCreate).ToUpperInvariant();
                BuildClasses();
                BuildCreateInfo();
                ShowPreview(ChrSelIndex(_cClass, _cGender));
                _plateName.Text = _cName.Length > 0 ? _cName : _cClass.ToLocalizedString();
                _plateLine.Text = T(ClientTextKeys.ModernAcctNewHero).ToUpperInvariant();
            }
            else
            {
                _mode.Text = T(ClientTextKeys.ModernAcctModeSelect).ToUpperInvariant();
                BuildRoster();
                BuildDetails();
                SelectInfo c = Current;
                if (c != null)
                {
                    ShowPreview(ChrSelIndex(c.Class, c.Gender));
                    _plateName.Text = c.Name;
                    string guild = AccountState.Detail(c.Index)?.GuildName;
                    string line = T(ClientTextKeys.ModernAcctLevelClass, c.Level, c.Class.ToLocalizedString());
                    _plateLine.Text = (string.IsNullOrEmpty(guild) ? line : line + " · " + guild).ToUpperInvariant();
                }
                else
                {
                    HidePreview();
                    _plateName.Text = string.Empty;
                    _plateLine.Text = string.Empty;
                }
            }
            _start.Visible = !_create;
            _tip.Visible = !_create;
            RefreshStart();
        }

        private void BuildRoster()
        {
            int max = Globals.MaxCharacterCount, count = Math.Min(_characters.Count, max);
            Label(_left, T(ClientTextKeys.ModernAcctCharacters).ToUpperInvariant(), 14, 12, 160, 16, 10, ModernTheme.Dim, true);
            Label(_left, count + " / " + max, 14, 12, PanelW - 28, 16, 11, ModernTheme.Text, true, StringAlignment.Far);
            int y = 36;
            for (int i = 0; i < max; i++)
            {
                AccountRow row;
                if (i < count)
                {
                    SelectInfo c = _characters[i];
                    string map = AccountState.Detail(c.Index)?.MapTitle;
                    row = new AccountRow(c.Name, string.IsNullOrEmpty(map) ? c.Class.ToLocalizedString() : c.Class.ToLocalizedString() + " · " + map,
                        c.Level.ToString(), Portrait(c.Class, c.Gender), i == _selected, false);
                    int index = i;
                    row.Click += (o, e) =>
                    {
                        if (_entering || _selected == index) return;
                        _selected = index;
                        Render();
                    };
                    row.DoubleClick += (o, e) =>
                    {
                        _selected = index;
                        StartGame();
                    };
                }
                else
                {
                    row = new AccountRow(T(ClientTextKeys.ModernAcctCreateSlot), string.Empty, string.Empty, -1, false, true);
                    row.Click += (o, e) => OpenCreate();
                }
                row.Parent = _left;
                row.Location = new Point(S(14), S(y));
                row.Size = new Size(S(PanelW - 28), S(RowH - 6));
                y += RowH;
            }
            _left.SetHeight(y + 8);
        }

        private void BuildDetails()
        {
            SelectInfo c = Current;
            Label(_right, T(ClientTextKeys.ModernAcctDetails).ToUpperInvariant(), 16, 14, PanelW - 32, 16, 10, ModernTheme.Dim, true);
            int y = 38;
            if (c != null)
            {
                ClientSelectDetail d = AccountState.Detail(c.Index);
                Row(ref y, ClientTextKeys.ModernAcctClass, c.Class.ToLocalizedString(), ModernTheme.Class(c.Class));
                Row(ref y, ClientTextKeys.ModernAcctLevel, c.Level.ToString(), Color.White);
                if (d != null)
                {
                    Row(ref y, ClientTextKeys.ModernAcctGuild, string.IsNullOrEmpty(d.GuildName) ? "-" : d.GuildName, Color.White);
                    if (!string.IsNullOrEmpty(d.MapTitle)) Row(ref y, ClientTextKeys.ModernAcctLocation, d.MapTitle, Color.White);
                }
                Row(ref y, ClientTextKeys.ModernAcctLastPlayed, c.LastAccess == DateTime.MinValue ? T(ClientTextKeys.Never) : c.LastAccess.ToString("yyyy/MM/dd HH:mm"), Color.White);
                if (d != null) Row(ref y, ClientTextKeys.ModernAcctPlayed, Duration(d.PlayTime), Color.White);
            }
            if (AccountState.Details != null) Row(ref y, ClientTextKeys.ModernAcctAccountGold, AccountState.Details.AccountGold.ToString("#,##0"), ModernTheme.Gold);

            y += 10;
            int half = (PanelW - 32 - 6) / 2;
            ModernButton create = new ModernButton(T(ClientTextKeys.ModernAcctNewCharacter)) { Parent = _right, Location = new Point(S(16), S(y)), Size = new Size(S(half), S(30)) };
            create.Click += (o, e) => OpenCreate();
            if (_characters.Count >= Globals.MaxCharacterCount) create.SetDisabled(true, T(ClientTextKeys.YouCannotMakeMoreCharacters, Globals.MaxCharacterCount));
            ModernButton delete = new ModernButton(T(ClientTextKeys.ModernAcctDelete), ButtonKind.Warn) { Parent = _right, Location = new Point(S(16 + half + 6), S(y)), Size = new Size(S(half), S(30)) };
            delete.Click += (o, e) => DeleteCharacter();
            if (c == null || _deleting) delete.SetDisabled(true);
            _right.SetHeight(y + 46);
        }

        private void Row(ref int y, ClientTextKeys key, string value, Color colour)
        {
            Label(_right, T(key), 16, y, 110, 20, 12, ModernTheme.Dim);
            Label(_right, value, 16, y, PanelW - 32, 20, 12, colour, true, StringAlignment.Far);
            y += 24;
        }

        private void BuildClasses()
        {
            Label(_left, T(ClientTextKeys.ModernAcctChooseClass).ToUpperInvariant(), 14, 12, PanelW - 28, 16, 10, ModernTheme.Dim, true);
            int y = 36;
            for (int i = 0; i < Classes.Length; i++)
            {
                MirClass cls = Classes[i];
                bool allowed = ClassAllowed(cls);
                AccountRow row = new AccountRow(cls.ToLocalizedString(), allowed ? string.Empty : T(ClientTextKeys.ModernAcctClassClosed), string.Empty,
                    Portrait(cls, _cGender), cls == _cClass, false, !allowed)
                {
                    Parent = _left,
                    Location = new Point(S(14), S(y)),
                    Size = new Size(S(PanelW - 28), S(46)),
                };
                if (allowed)
                    row.Click += (o, e) =>
                    {
                        if (_cClass == cls) return;
                        _cClass = cls;
                        Render();
                    };
                y += 52;
            }
            _left.SetHeight(y + 8);
        }

        private void BuildCreateInfo()
        {
            int w = PanelW - 32;
            Label(_right, _cClass.ToLocalizedString().ToUpperInvariant(), 16, 14, w, 18, 12, ModernTheme.Class(_cClass), true);
            Label(_right, ClassDescription(_cClass), 16, 36, w, 180, 11, ModernTheme.Text, false, StringAlignment.Near, true);

            int y = 224;
            Label(_right, T(ClientTextKeys.ModernAcctGender).ToUpperInvariant(), 16, y, w, 14, 10, ModernTheme.Dim, true);
            var genders = new List<Choice<MirGender>>
            {
                new Choice<MirGender>(MirGender.Male, T(ClientTextKeys.ModernAcctMale)),
                new Choice<MirGender>(MirGender.Female, T(ClientTextKeys.ModernAcctFemale)),
            };
            Segmented<MirGender> gender = new Segmented<MirGender>(genders, _cGender, w) { Parent = _right, Location = new Point(S(16), S(y + 16)) };
            gender.Changed += g =>
            {
                if (_cGender == g) return;
                _cGender = g;
                Render();
            };

            y += 52;
            Label(_right, T(ClientTextKeys.ModernAcctCharacterName).ToUpperInvariant(), 16, y, w, 14, 10, ModernTheme.Dim, true);
            _nameField = new ModernTextField(w, 32, 13) { Parent = _right, Location = new Point(S(16), S(y + 16)), MaxLength = Globals.MaxCharacterNameLength };
            _nameField.Text = _cName;
            _nameHint = Label(_right, string.Empty, 16, y + 50, w, 14, 10, ModernTheme.Dim);
            _nameField.TextChanged += t =>
            {
                _cName = (t ?? string.Empty).Trim();
                _plateName.Text = _cName.Length > 0 ? _cName : _cClass.ToLocalizedString();
                RefreshName();
            };
            _nameField.Submitted += t => CreateCharacter();
            _nameField.Escaped += () => CloseCreate();

            y += 74;
            int half = (w - 6) / 2;
            ModernButton cancel = new ModernButton(T(ClientTextKeys.ModernAcctCancel)) { Parent = _right, Location = new Point(S(16), S(y)), Size = new Size(S(half), S(32)) };
            cancel.Click += (o, e) => CloseCreate();
            if (_characters.Count == 0) cancel.SetDisabled(true);
            _createButton = new ModernButton(T(ClientTextKeys.ModernAcctCreate), ButtonKind.Main) { Parent = _right, Location = new Point(S(16 + half + 6), S(y)), Size = new Size(S(half), S(32)) };
            _createButton.Click += (o, e) => CreateCharacter();
            _right.SetHeight(y + 48);
            RefreshName();
            _nameField.Focus();
        }

        private void RefreshName()
        {
            if (_nameHint == null || _nameHint.IsDisposed) return;
            bool ok = NameReg.IsMatch(_cName);
            _nameHint.Text = T(ClientTextKeys.ModernAcctNameRule, Globals.MinCharacterNameLength, Globals.MaxCharacterNameLength);
            _nameHint.Colour = _cName.Length == 0 ? ModernTheme.Dim : ok ? ModernTheme.Ok : ModernTheme.BadSoft;
            _createButton?.SetDisabled(!ok || _creating || !ClassAllowed(_cClass));
        }

        private void RefreshStart()
        {
            if (_create) return;
            if (_delayUntil > 0)
            {
                int left = (int)Math.Ceiling((_delayUntil - CMain.Time) / 1000.0);
                _start.Text = T(ClientTextKeys.ModernAcctWait, Math.Max(0, left));
                _start.SetDisabled(true);
                return;
            }
            _start.Text = _entering || _waitingLibraries ? T(ClientTextKeys.ModernAcctEntering) : T(ClientTextKeys.ModernAcctStartGame);
            _start.SetDisabled(Current == null || _entering || _waitingLibraries);
        }

        // ------------------------------------------------------------------ preview

        private void ShowPreview(int index)
        {
            if (index == _shownPreview && _preview.Visible) return;
            _shownPreview = index;
            _preview.Index = index;
            Size size = Libraries.ChrSel.GetSize(index);
            Point off = Libraries.ChrSel.GetOffSet(index);
            _preview.Location = new Point(Settings.ScreenWidth / 2 - off.X - size.Width / 2, S(PreviewBottom) - off.Y - size.Height);
            _preview.Visible = true;
        }

        private void HidePreview()
        {
            _shownPreview = -1;
            _preview.Visible = false;
        }

        private static int ChrSelIndex(MirClass cls, MirGender gender)
        {
            bool male = gender == MirGender.Male;
            switch (cls)
            {
                case MirClass.Wizard: return male ? 40 : 320;
                case MirClass.Taoist: return male ? 60 : 340;
                case MirClass.Assassin: return male ? 80 : 360;
                case MirClass.Archer: return male ? 100 : 140;
                default: return male ? 20 : 300;
            }
        }

        /// <summary>GameScene.HeroAvatar + 370: the HeroManage portrait in Prguse.</summary>
        private static int Portrait(MirClass cls, MirGender gender) => 1400 + (int)cls + 10 * (int)gender + 370;

        private static string ClassDescription(MirClass cls) => cls switch
        {
            MirClass.Wizard => T(ClientTextKeys.WizardsDescription),
            MirClass.Taoist => T(ClientTextKeys.TaoistsDescription),
            MirClass.Assassin => T(ClientTextKeys.AssassinsDescription),
            MirClass.Archer => T(ClientTextKeys.ArchersDescription),
            _ => T(ClientTextKeys.WarriorsDescription),
        };

        private static bool ClassAllowed(MirClass cls)
        {
            S.ServerRules rules = AccountState.Rules;
            if (rules == null) return true; // the server still answers S.NewCharacter 3
            if (cls == MirClass.Assassin) return rules.AllowCreateAssassin;
            if (cls == MirClass.Archer) return rules.AllowCreateArcher;
            return true;
        }

        private static string Duration(long seconds)
        {
            if (seconds < 0) seconds = 0;
            long h = seconds / 3600, m = seconds % 3600 / 60;
            return h > 0 ? h.ToString("#,##0") + "h " + m + "m" : m + "m";
        }

        // ------------------------------------------------------------------ actions

        private void OpenCreate()
        {
            if (_entering || _create || _characters.Count >= Globals.MaxCharacterCount) return;
            _create = true;
            _cName = string.Empty;
            if (!ClassAllowed(_cClass)) _cClass = MirClass.Warrior;
            Render();
        }

        private void CloseCreate()
        {
            if (!_create || _characters.Count == 0) return;
            _create = false;
            if (Program.Form != null) Program.Form.ActiveControl = null;
            Render();
        }

        private void CreateCharacter()
        {
            if (!_create || _creating || !NameReg.IsMatch(_cName) || !ClassAllowed(_cClass)) return;
            _creating = true;
            RefreshName();
            Network.Enqueue(new C.NewCharacter { Name = _cName, Class = _cClass, Gender = _cGender });
        }

        private void DeleteCharacter()
        {
            SelectInfo c = Current;
            if (c == null || _deleting || _entering) return;
            string name = c.Name;
            int index = c.Index;
            Root.Popups.Confirm(T(ClientTextKeys.ConfirmDeleteCharacter, name), PopupButtons.YesNo, PopupKind.Danger, (a, _) =>
            {
                if (a != PopupAnswer.Yes) return;
                Root.Popups.Input(T(ClientTextKeys.ModernAcctDelete), string.Empty, Globals.MaxCharacterNameLength, typed =>
                {
                    if (typed != name)
                    {
                        Say(T(ClientTextKeys.IncorrectEntry), PopupKind.Warning);
                        return;
                    }
                    _deleting = true;
                    Network.Enqueue(new C.DeleteCharacter { CharacterIndex = index });
                    Render();
                }, T(ClientTextKeys.PleaseEnterCharacterName));
            });
        }

        private void StartGame()
        {
            SelectInfo c = Current;
            if (_create || c == null || _entering || _delayUntil > 0) return;
            if (!Libraries.Loaded)
            {
                if (_waitingLibraries) return;
                _waitingLibraries = true;
                _spinner = new MirAnimatedControl
                {
                    Library = Libraries.Prguse,
                    Index = 940,
                    Parent = this,
                    Location = new Point(Settings.ScreenWidth / 2 - 40, Settings.ScreenHeight - S(150)),
                    Animated = true,
                    AnimationCount = 9,
                    AnimationDelay = 100,
                    Loop = true,
                    NotControl = true,
                };
                RefreshStart();
                return;
            }
            _entering = true;
            RefreshStart();
            Network.Enqueue(new C.StartGame { CharacterIndex = c.Index });
        }

        private void SwitchToClassic()
        {
            if (_entering) return;
            Settings.UiStyle = UiStyle.Classic;
            ActiveScene = new SelectScene(_characters);
            Dispose();
        }

        protected override bool ScreenKey(KeyEventArgs e)
        {
            if (_create)
            {
                if (e.KeyCode != Keys.Escape) return false;
                CloseCreate();
                return true;
            }
            switch (e.KeyCode)
            {
                case Keys.Up:
                case Keys.Down:
                    if (_entering || _characters.Count == 0) return true;
                    int count = Math.Min(_characters.Count, Globals.MaxCharacterCount);
                    _selected = (_selected + (e.KeyCode == Keys.Up ? count - 1 : 1)) % count;
                    Render();
                    return true;
                case Keys.Enter:
                    StartGame();
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ frame

        protected override void Tick()
        {
            if (_waitingLibraries && Libraries.Loaded)
            {
                _waitingLibraries = false;
                _spinner?.Dispose();
                _spinner = null;
                StartGame();
            }
            if (_delayUntil > 0)
            {
                int left = (int)Math.Ceiling((_delayUntil - CMain.Time) / 1000.0);
                if (left <= 0)
                {
                    _delayUntil = 0;
                    _shownWait = -1;
                    StartGame();
                }
                else if (left != _shownWait)
                {
                    _shownWait = left;
                    RefreshStart();
                }
            }
            // S.SelectDetails / S.ServerRules can arrive after the scene was built (log out, or a slow login)
            if (!ReferenceEquals(_detailsShown, AccountState.Details) || (_create && !ReferenceEquals(_rulesShown, AccountState.Rules)))
            {
                if (_create)
                {
                    _detailsShown = AccountState.Details;
                    _rulesShown = AccountState.Rules;
                    if (!ClassAllowed(_cClass)) _cClass = MirClass.Warrior;
                    Render();
                }
                else Render();
            }
        }

        // ------------------------------------------------------------------ packets

        public override void ProcessPacket(Packet p)
        {
            if (AccountState.Take(p)) return;
            switch (p.Index)
            {
                case (short)ServerPacketIds.NewCharacter:
                    NewCharacter((S.NewCharacter)p);
                    break;
                case (short)ServerPacketIds.NewCharacterSuccess:
                    NewCharacterSuccess((S.NewCharacterSuccess)p);
                    break;
                case (short)ServerPacketIds.DeleteCharacter:
                    _deleting = false;
                    S.DeleteCharacter d = (S.DeleteCharacter)p;
                    if (d.Result == 0) Say(T(ClientTextKeys.DeletingCharactersDisabled), PopupKind.Warning);
                    else if (d.Result == 1) Say(T(ClientTextKeys.SelectedCharacterNotExist), PopupKind.Warning);
                    Render();
                    break;
                case (short)ServerPacketIds.DeleteCharacterSuccess:
                    DeleteCharacterSuccess((S.DeleteCharacterSuccess)p);
                    break;
                case (short)ServerPacketIds.StartGame:
                    StartGameResult((S.StartGame)p);
                    break;
                case (short)ServerPacketIds.StartGameBanned:
                    S.StartGameBanned b = (S.StartGameBanned)p;
                    _entering = false;
                    TimeSpan left = b.ExpiryDate - CMain.Now;
                    if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                    Say(T(ClientTextKeys.AccountBannedReasonExpiryDuration, b.Reason, b.ExpiryDate, Math.Floor(left.TotalHours), left.Minutes, left.Seconds), PopupKind.Danger);
                    RefreshStart();
                    break;
                case (short)ServerPacketIds.StartGameDelay:
                    _entering = false;
                    _delayUntil = CMain.Time + Math.Max(0, ((S.StartGameDelay)p).Milliseconds);
                    _shownWait = -1;
                    RefreshStart();
                    break;
                default:
                    base.ProcessPacket(p);
                    break;
            }
        }

        private void NewCharacter(S.NewCharacter p)
        {
            _creating = false;
            switch (p.Result)
            {
                case 0:
                    Say(T(ClientTextKeys.CreatingNewCharactersDisabled), PopupKind.Warning);
                    break;
                case 1:
                    Say(T(ClientTextKeys.YourCharacterNameNotAcceptable), PopupKind.Warning);
                    break;
                case 2:
                    Say(T(ClientTextKeys.GenderNotExistContactGM), PopupKind.Warning);
                    break;
                case 3:
                    Say(T(ClientTextKeys.ClassNotExistContactGM), PopupKind.Warning);
                    break;
                case 4:
                    Say(T(ClientTextKeys.YouCannotMakeMoreCharacters, Globals.MaxCharacterCount), PopupKind.Warning);
                    break;
                case 5:
                    Say(T(ClientTextKeys.CharacterNameExists), PopupKind.Warning);
                    break;
            }
            RefreshName();
        }

        private void NewCharacterSuccess(S.NewCharacterSuccess p)
        {
            _creating = false;
            if (p.CharInfo == null) return;
            _characters.Insert(0, p.CharInfo);
            _selected = 0;
            _create = false;
            if (Program.Form != null) Program.Form.ActiveControl = null;
            Render();
            Say(T(ClientTextKeys.YourCharacterCreatedSuccessfully), PopupKind.Info);
        }

        private void DeleteCharacterSuccess(S.DeleteCharacterSuccess p)
        {
            _deleting = false;
            for (int i = 0; i < _characters.Count; i++)
                if (_characters[i].Index == p.CharacterIndex)
                {
                    _characters.RemoveAt(i);
                    break;
                }
            if (_selected >= _characters.Count) _selected = Math.Max(0, _characters.Count - 1);
            if (_characters.Count == 0) _create = true;
            Render();
            Say(T(ClientTextKeys.YourCharacterDeletedSuccessfully), PopupKind.Info);
        }

        private void StartGameResult(S.StartGame p)
        {
            _entering = false;
            switch (p.Result)
            {
                case 0:
                    Say(T(ClientTextKeys.StartingGameDisabled), PopupKind.Warning);
                    break;
                case 1:
                    Say(T(ClientTextKeys.YouNotLoggedIn), PopupKind.Warning);
                    break;
                case 2:
                    Say(T(ClientTextKeys.YourCharacterNotFound), PopupKind.Warning);
                    break;
                case 3:
                    Say(T(ClientTextKeys.NoActiveMapOrStartPointFound), PopupKind.Warning);
                    break;
                case 4:
                    // SelectScene.StartGame(S.StartGame) result 4, unchanged
                    if (p.Resolution < Settings.Resolution || Settings.Resolution == 0) Settings.Resolution = p.Resolution;
                    switch (Settings.Resolution)
                    {
                        default:
                        case 1024:
                            Settings.Resolution = 1024;
                            CMain.SetResolution(1024, 768);
                            break;
                        case 1280:
                            CMain.SetResolution(1280, 800);
                            break;
                        case 1366:
                            CMain.SetResolution(1366, 768);
                            break;
                        case 1920:
                            CMain.SetResolution(1920, 1080);
                            break;
                    }
                    ActiveScene = new GameScene();
                    Dispose();
                    return;
            }
            RefreshStart();
        }

        // ------------------------------------------------------------------ helpers

        private static ModernLabel Label(MirControl parent, string text, int x, int y, int w, int h, float size, Color colour, bool bold = false,
            StringAlignment align = StringAlignment.Near, bool wrap = false) =>
            new ModernLabel
            {
                Parent = parent,
                Location = new Point(S(x), S(y)),
                Size = new Size(S(w), S(h)),
                Text = text,
                FontSize = size,
                Colour = colour,
                Bold = bold,
                Align = align,
                Wrap = wrap,
            };
    }

    /// <summary>A roster / class row (select.ts .ch / .cc): portrait, name, sub line, level; selected = accent border.</summary>
    internal sealed class AccountRow : ModernControl
    {
        private readonly string _title, _sub, _right;
        private readonly int _portrait;
        private readonly bool _selected, _empty, _off;

        public AccountRow(string title, string sub, string right, int portrait, bool selected, bool empty, bool off = false)
        {
            _title = title ?? string.Empty;
            _sub = sub ?? string.Empty;
            _right = right ?? string.Empty;
            _portrait = portrait;
            _selected = selected;
            _empty = empty;
            _off = off;
            Sound = SoundList.ButtonA;
        }

        protected override void Paint(Graphics g)
        {
            float k = K, rad = ModernTheme.Radius * k;
            RectangleF r = new RectangleF(0.5f, 0.5f, Size.Width - 1f, Size.Height - 1f);
            using GraphicsPath path = ModernSkin.RoundRect(r, rad);
            Color fill = _selected ? ModernTheme.Alpha(ModernTheme.Accent, 0.22f) : Color.FromArgb(_off ? 50 : 100, 3, 5, 10);
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            Color border = _selected ? ModernTheme.Accent : Hovered && !_off ? ModernTheme.Alpha(ModernTheme.Accent, 0.6f) : ModernTheme.Alpha(ModernTheme.Frame, 0.6f);
            using (var pen = new Pen(border, 1f))
            {
                if (_empty) pen.DashStyle = DashStyle.Dash;
                g.DrawPath(pen, path);
            }
            if (_selected)
                using (var bar = new SolidBrush(ModernTheme.Accent))
                    g.FillRectangle(bar, r.X + 1, r.Y + 4 * k, 3 * k, r.Height - 8 * k);

            if (_empty)
            {
                ModernSkin.Text(g, _title, ModernSkin.Font(12), Hovered ? Color.White : ModernTheme.Dim, r, StringAlignment.Center);
                return;
            }

            float x = _portrait >= 0 ? Size.Height + 6 * k : 12 * k;
            Color title = _off ? ModernTheme.Dim : Color.White;
            bool twoLines = _sub.Length > 0;
            ModernSkin.Text(g, _title, ModernSkin.Font(14, true), title,
                new RectangleF(x, twoLines ? 6 * k : 0, Size.Width - x - 48 * k, twoLines ? 20 * k : Size.Height));
            if (twoLines)
                ModernSkin.Text(g, _sub, ModernSkin.Font(11), ModernTheme.Dim, new RectangleF(x, 26 * k, Size.Width - x - 48 * k, 16 * k));
            if (_right.Length > 0)
            {
                ModernSkin.Text(g, _right, ModernSkin.Font(18, true), ModernTheme.Accent, new RectangleF(Size.Width - 52 * k, 4 * k, 44 * k, 24 * k), StringAlignment.Center);
                ModernSkin.Text(g, GameLanguage.ClientTextMap.GetLocalization(ClientTextKeys.ModernAcctLv), ModernSkin.Font(8, true), ModernTheme.Dim, new RectangleF(Size.Width - 52 * k, 28 * k, 44 * k, 12 * k), StringAlignment.Center);
            }
        }

        protected internal override void DrawControl()
        {
            base.DrawControl();
            if (_portrait < 0 || Libraries.Prguse == null) return;
            int side = Size.Height - S(10);
            if (side <= 0) return;
            Point at = new Point(DisplayLocation.X + S(6), DisplayLocation.Y + S(5));
            Libraries.Prguse.Draw(_portrait, at, new Size(side, side), _off ? Color.Gray : Color.White);
        }
    }
}

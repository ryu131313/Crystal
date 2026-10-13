// LoginScene (Client/MirScenes/LoginScene.cs): the ChrSel.Lib background (frame 0; plays 0-18 once after
// S.LoginSuccess, then the SelectScene), the Build label, and its dialogs: LoginDialog (Prguse 1084), InputKeyDialog
// (the on-screen keyboard, Prguse 1080), NewAccountDialog (Prguse 63) and ChangePasswordDialog (Prguse 50), with the
// desktop's red / green text-box validation and the S.Login / S.NewAccount / S.ChangePassword result boxes.

import type { ServerPacket, SelectInfo } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { MirControl, MirImage, MirButton, MirLabel, MirTextBox, Lib, SCREEN_WIDTH, SCREEN_HEIGHT } from "./mir/controls.ts";
import { MirAnimated } from "./animatedImage.ts";
import { Globals, Text, Colour, format, banText, setBorder, binaryFromWallMs, type SceneHost } from "./accountCommon.ts";

type P<T extends ServerPacket["type"]> = Extract<ServerPacket, { type: T }>;

const ACCOUNT_REG = new RegExp(`^[A-Za-z0-9]{${Globals.MinAccountIDLength},${Globals.MaxAccountIDLength}}$`);
const PASSWORD_REG = new RegExp(`^[A-Za-z0-9]{${Globals.MinPasswordLength},${Globals.MaxPasswordLength}}$`);
const EMAIL_REG = /\w+([-+.]\w+)*@\w+([-.]\w+)*\.\w+([-.]\w+)*/;

/** Location = ((ScreenWidth - Size.Width) / 2, (ScreenHeight - Size.Height) / 2) once the picture's size is known. */
function centre(c: MirImage, offset: [number, number] = [0, 0], then?: () => void): void {
  void c.ready.then((pic) => {
    if (pic) c.location = [Math.trunc((SCREEN_WIDTH - pic.width) / 2) + offset[0], Math.trunc((SCREEN_HEIGHT - pic.height) / 2) + offset[1]];
    then?.();
  });
}

/** Keeps focus in the text boxes when a dialog's buttons are clicked (MirButtons are not WinForms controls). */
function keepFocus(c: MirControl): void {
  c.el.addEventListener("mousedown", (e) => { if (!(e.target instanceof HTMLInputElement)) e.preventDefault(); });
}

/** LoginScene.LoginDialog. */
class LoginDialog {
  readonly win: MirImage;
  readonly okButton: MirButton;
  readonly accountButton: MirButton;
  readonly passButton: MirButton;
  readonly viewKeyButton: MirButton;
  readonly closeButton: MirButton;
  readonly accountIDTextBox: MirTextBox;
  readonly passwordTextBox: MirTextBox;
  /** The text box the on-screen keyboard types into (GetFocussedTextBox). */
  lastFocused: MirTextBox;
  private accountIDValid = false;
  private passwordValid = false;
  private readonly send: (f: Uint8Array) => void;

  constructor(parent: MirControl, send: (f: Uint8Array) => void) {
    this.send = send;
    this.win = new MirImage({ parent, library: Lib.Prguse, index: 1084, visible: false });
    centre(this.win, [0, 0], () => { this.win.size = [328, 220]; });
    keepFocus(this.win);
    const title = new MirImage({ parent: this.win, library: Lib.Title, index: 30 });
    void title.ready.then((pic) => { if (pic) title.location = [Math.trunc((328 - pic.width) / 2), 12]; });
    new MirImage({ parent: this.win, library: Lib.Title, index: 31, location: [52, 83] });
    new MirImage({ parent: this.win, library: Lib.Title, index: 32, location: [43, 105] });

    this.okButton = new MirButton({ parent: this.win, library: Lib.Title, index: 320, hoverIndex: 321, pressedIndex: 322, location: [227, 81], size: [42, 42], enabled: false, onClick: () => this.login() });
    this.accountButton = new MirButton({ parent: this.win, library: Lib.Title, index: 323, hoverIndex: 324, pressedIndex: 325, location: [60, 163] });
    this.passButton = new MirButton({ parent: this.win, library: Lib.Title, index: 326, hoverIndex: 327, pressedIndex: 328, location: [166, 163] });
    this.viewKeyButton = new MirButton({ parent: this.win, library: Lib.Title, index: 332, hoverIndex: 333, pressedIndex: 334, location: [60, 189] });
    this.closeButton = new MirButton({ parent: this.win, library: Lib.Title, index: 329, hoverIndex: 330, pressedIndex: 331, location: [166, 189] });

    this.passwordTextBox = new MirTextBox({ parent: this.win, location: [85, 108], size: [136, 15], password: true, maxLength: Globals.MaxPasswordLength,
      onEnter: () => this.keyPress(), onChange: () => this.passwordChanged() });
    // Settings.AccountID: the web client remembers the last account ID in localStorage (the password is never stored)
    let remembered = "";
    try { remembered = localStorage.getItem("crystal.account") ?? ""; } catch { /* storage blocked */ }
    this.accountIDTextBox = new MirTextBox({ parent: this.win, location: [85, 85], size: [136, 15], maxLength: Globals.MaxAccountIDLength, text: remembered,
      onEnter: () => this.keyPress(), onChange: () => this.accountIDChanged() });
    this.lastFocused = this.accountIDTextBox;
    for (const tb of [this.accountIDTextBox, this.passwordTextBox]) tb.input.addEventListener("focus", () => { this.lastFocused = tb; });
    this.accountIDChanged();
    this.passwordChanged();
  }

  /** AccountIDTextBox_TextChanged. */
  accountIDChanged(): void {
    const t = this.accountIDTextBox.text;
    this.accountIDValid = !!t && ACCOUNT_REG.test(t);
    setBorder(this.accountIDTextBox, this.accountIDValid ? Colour.Green : t ? Colour.Red : null);
    // (the desktop refreshes the OK button from the password box only; checking both is the same once both have been typed)
    this.okButton.enabled = this.accountIDValid && this.passwordValid;
  }

  /** PasswordTextBox_TextChanged + RefreshLoginButton. */
  passwordChanged(): void {
    const t = this.passwordTextBox.text;
    this.passwordValid = !!t && PASSWORD_REG.test(t);
    setBorder(this.passwordTextBox, this.passwordValid ? Colour.Green : t ? Colour.Red : null);
    this.okButton.enabled = this.accountIDValid && this.passwordValid;
  }

  /** TextBox_KeyPress (Enter). */
  keyPress(): void {
    if (!this.accountIDValid) { this.accountIDTextBox.focus(); return; }
    if (!this.passwordValid) { this.passwordTextBox.focus(); return; }
    if (this.okButton.enabled) this.okButton.el.click();
  }

  private login(): void {
    this.okButton.enabled = false;
    try { localStorage.setItem("crystal.account", this.accountIDTextBox.text); } catch { /* storage blocked */ }
    this.send(ClientPackets.Login({ AccountID: this.accountIDTextBox.text, Password: this.passwordTextBox.text }));
  }

  get visible(): boolean { return this.win.visible; }

  /** Show(): focus the account ID (the password when the account ID is remembered). */
  show(): void {
    if (this.win.visible) return;
    this.win.visible = true;
    (this.accountIDValid ? this.passwordTextBox : this.accountIDTextBox).focus();
  }

  hide(): void { this.win.visible = false; }

  clear(): void {
    this.accountIDTextBox.text = "";
    this.passwordTextBox.text = "";
    this.accountIDChanged();
    this.passwordChanged();
  }

  dispose(): void { this.win.dispose(); }
}

/** LoginScene.InputKeyDialog: an on-screen keyboard typing into the login dialog's focused text box. */
class InputKeyDialog {
  private readonly win: MirImage;
  private readonly login: LoginDialog;
  private keys: MirButton[] = [];
  private letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ".split("");
  private numbers = "0123456789".split("");
  private disposed = false;

  constructor(parent: MirControl, login: LoginDialog) {
    this.login = login;
    this.win = new MirImage({ parent, library: Lib.Prguse, index: 1080 });
    centre(this.win, [285, 150]);
    keepFocus(this.win);
    const textButton = (text: string, index: number, location: [number, number], onClick: (e: MouseEvent) => void) =>
      new MirButton({ parent: this.win, library: Lib.Title, index, hoverIndex: index + 1, pressedIndex: index + 2, location, text, centerText: true, onClick });
    textButton(Text.BtnEsc, 300, [12, 12], () => this.dispose());
    textButton(Text.BtnDelete, 303, [140, 76], () => this.secureKeyDelete());
    textButton(Text.BtnEnter, 306, [140, 236], () => this.login.keyPress());
    textButton(Text.BtnRandom, 309, [76, 236], () => {
      const shuffle = (a: string[]) => { for (let i = a.length - 1; i > 0; i--) { const j = Math.floor(Math.random() * (i + 1)); [a[i], a[j]] = [a[j]!, a[i]!]; } };
      shuffle(this.letters);
      shuffle(this.numbers);
      this.updateKeys();
    });
    this.updateKeys();
  }

  get isDisposed(): boolean { return this.disposed; }

  /** UpdateKeys: numbers 6 per row from (12, 44), letters 6 per row from (12, 108), 32 px apart. */
  private updateKeys(): void {
    for (const b of this.keys) b.dispose();
    this.keys = [];
    const key = (ch: string, x: number, y: number) => {
      this.keys.push(new MirButton({ parent: this.win, library: Lib.Prguse, index: 1081, hoverIndex: 1082, pressedIndex: 1083, size: [32, 30], location: [x, y],
        text: ch, centerText: true, onClick: (e) => this.secureKeyPress(ch, e) }));
    };
    this.numbers.forEach((ch, i) => key(ch, 12 + (i % 6) * 32, 44 + Math.floor(i / 6) * 32));
    this.letters.forEach((ch, i) => key(ch, 12 + (i % 6) * 32, 108 + Math.floor(i / 6) * 32));
  }

  private refresh(tb: MirTextBox): void {
    tb.focus();
    tb.input.setSelectionRange(tb.text.length, tb.text.length);
    if (tb === this.login.accountIDTextBox) this.login.accountIDChanged();
    else this.login.passwordChanged();
  }

  /** SecureKeyPress: Caps Lock picks the case. */
  private secureKeyPress(ch: string, e: MouseEvent): void {
    const tb = this.login.lastFocused;
    tb.text += e.getModifierState("CapsLock") ? ch.toUpperCase() : ch.toLowerCase();
    this.refresh(tb);
  }

  /** SecureKeyDelete: the selection, else the last character. */
  private secureKeyDelete(): void {
    const tb = this.login.lastFocused;
    const start = tb.input.selectionStart ?? tb.text.length, end = tb.input.selectionEnd ?? start;
    if (end > start) tb.text = tb.text.slice(0, start) + tb.text.slice(end);
    else if (tb.text.length > 0) tb.text = tb.text.slice(0, -1);
    this.refresh(tb);
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.win.dispose();
  }
}

type Check = (text: string) => string | null; // the border colour for the text (Color.Gray / Red / Green), and validity from it

/** One NewAccountDialog / ChangePasswordDialog text box: Border, Gray, the *_TextChanged colour rule and the GotFocus description. */
interface Field { box: MirTextBox; valid: boolean }

/** LoginScene.NewAccountDialog. */
class NewAccountDialog {
  readonly win: MirImage;
  readonly okButton: MirButton;
  readonly accountIDTextBox: MirTextBox;
  readonly password1TextBox: MirTextBox;
  readonly password2TextBox: MirTextBox;
  readonly eMailTextBox: MirTextBox;
  readonly userNameTextBox: MirTextBox;
  readonly birthDateTextBox: MirTextBox;
  readonly questionTextBox: MirTextBox;
  readonly answerTextBox: MirTextBox;
  private readonly description: MirLabel;
  private readonly fields = new Map<MirTextBox, Field>();
  private disposed = false;
  onDispose: (() => void) | null = null;
  private readonly send: (f: Uint8Array) => void;

  constructor(parent: MirControl, send: (f: Uint8Array) => void) {
    this.send = send;
    this.win = new MirImage({ parent, library: Lib.Prguse, index: 63 });
    centre(this.win);
    keepFocus(this.win);
    new MirButton({ parent: this.win, library: Lib.Title, index: 203, hoverIndex: 204, pressedIndex: 205, location: [409, 425], onClick: () => this.dispose() });
    this.okButton = new MirButton({ parent: this.win, library: Lib.Title, index: 200, hoverIndex: 201, pressedIndex: 202, location: [135, 425], enabled: false, onClick: () => this.createAccount() });

    const box = (y: number, width: number, maxLength: number, password: boolean, check: Check, description: string, initiallyValid: boolean) => {
      const tb = new MirTextBox({ parent: this.win, location: [226, y], size: [width, 18], maxLength, password, border: true, borderColour: Colour.Gray,
        onEnter: () => { if (this.okButton.enabled) this.okButton.el.click(); } });
      const field: Field = { box: tb, valid: initiallyValid };
      this.fields.set(tb, field);
      tb.input.addEventListener("input", () => {
        const colour = check(tb.text);
        field.valid = colour !== Colour.Red;
        setBorder(tb, colour ?? Colour.Gray);
        if (tb === this.password1TextBox) this.password2Changed(); // Password1TextBox_TextChanged re-checks the confirmation
        this.refreshConfirmButton();
      });
      tb.input.addEventListener("focus", () => { this.description.visible = true; this.description.text = description; });
      return tb;
    };
    const required = (reg: RegExp): Check => (t) => (!t || !reg.test(t) ? Colour.Red : Colour.Green);
    const optional = (ok: (t: string) => boolean): Check => (t) => (!t ? Colour.Gray : ok(t) ? Colour.Green : Colour.Red);
    const pwText = format(Text.PasswordDescription, Globals.MinPasswordLength, Globals.MaxPasswordLength);
    this.password1TextBox = box(129, 136, Globals.MaxPasswordLength, true, required(PASSWORD_REG), pwText, false);
    this.password2TextBox = box(155, 136, Globals.MaxPasswordLength, true,
      (t) => (!t || !PASSWORD_REG.test(t) || t !== this.password1TextBox.text ? Colour.Red : Colour.Green), pwText, false);
    this.userNameTextBox = box(189, 136, 20, false, optional((t) => t.length <= 20), Text.UserNameDescription, true);
    this.birthDateTextBox = box(215, 136, 10, false, optional((t) => t.length <= 10 && parseBirthDate(t) !== null), format(Text.BirthDateDescription, "YYYY-MM-DD"), true);
    this.questionTextBox = box(250, 190, 30, false, optional((t) => t.length <= 30), Text.SecretQuestionDescription, true);
    this.answerTextBox = box(276, 190, 30, false, optional((t) => t.length <= 30), Text.SecretAnswerDescription, true);
    this.eMailTextBox = box(311, 136, 50, false, optional((t) => EMAIL_REG.test(t) && t.length <= 50), Text.EmailAddressDescription, true);
    this.description = new MirLabel({ parent: this.win, location: [15, 340], size: [300, 70], visible: false, border: true, borderColour: Colour.Gray });
    this.accountIDTextBox = box(103, 136, Globals.MaxAccountIDLength, false, required(ACCOUNT_REG),
      format(Text.AccountIdDescription, Globals.MinAccountIDLength, Globals.MaxAccountIDLength), false);
    this.accountIDTextBox.focus();
  }

  get isDisposed(): boolean { return this.disposed; }

  /** Password2TextBox_TextChanged (also run when the first password changes). */
  private password2Changed(): void {
    const t = this.password2TextBox.text, f = this.fields.get(this.password2TextBox)!;
    f.valid = !!t && PASSWORD_REG.test(t) && t === this.password1TextBox.text;
    setBorder(this.password2TextBox, f.valid ? Colour.Green : Colour.Red);
  }

  private refreshConfirmButton(): void {
    this.okButton.enabled = [...this.fields.values()].every((f) => f.valid);
  }

  private createAccount(): void {
    this.okButton.enabled = false;
    const birth = this.birthDateTextBox.text ? parseBirthDate(this.birthDateTextBox.text) : null;
    this.send(ClientPackets.NewAccount({
      AccountID: this.accountIDTextBox.text, Password: this.password1TextBox.text, EMailAddress: this.eMailTextBox.text,
      // DateTime.Parse, or DateTime.MinValue (ToBinary() = 0) when left blank
      BirthDate: birth === null ? 0n : binaryFromWallMs(birth),
      UserName: this.userNameTextBox.text, SecretQuestion: this.questionTextBox.text, SecretAnswer: this.answerTextBox.text,
    }));
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.win.dispose();
    const then = this.onDispose;
    this.onDispose = null;
    then?.();
  }
}

/** DateTime.TryParse of a birth date as wall-clock milliseconds (null = not a date). ISO dates parse as UTC midnight. */
function parseBirthDate(text: string): number | null {
  const d = new Date(text);
  if (Number.isNaN(d.getTime())) return null;
  return /^\d{4}-\d{1,2}-\d{1,2}$/.test(text.trim()) ? d.getTime() : d.getTime() - d.getTimezoneOffset() * 60000;
}

/** LoginScene.ChangePasswordDialog. */
class ChangePasswordDialog {
  readonly win: MirImage;
  readonly okButton: MirButton;
  readonly accountIDTextBox: MirTextBox;
  readonly currentPasswordTextBox: MirTextBox;
  readonly newPassword1TextBox: MirTextBox;
  readonly newPassword2TextBox: MirTextBox;
  private accountIDValid = false;
  private currentPasswordValid = false;
  private newPassword1Valid = false;
  private newPassword2Valid = false;
  private disposed = false;
  onDispose: (() => void) | null = null;
  private readonly send: (f: Uint8Array) => void;

  constructor(parent: MirControl, send: (f: Uint8Array) => void) {
    this.send = send;
    this.win = new MirImage({ parent, library: Lib.Prguse, index: 50 });
    centre(this.win);
    keepFocus(this.win);
    new MirButton({ parent: this.win, library: Lib.Title, index: 110, hoverIndex: 111, pressedIndex: 112, location: [222, 236], onClick: () => this.dispose() });
    this.okButton = new MirButton({ parent: this.win, library: Lib.Title, index: 107, hoverIndex: 108, pressedIndex: 109, location: [80, 236], enabled: false, onClick: () => this.changePassword() });
    const box = (y: number, max: number, password: boolean, changed: () => void) => new MirTextBox({ parent: this.win, location: [178, y], size: [136, 18], maxLength: max, password,
      border: true, borderColour: Colour.Gray, onChange: changed, onEnter: () => { if (this.okButton.enabled) this.okButton.el.click(); } });
    this.accountIDTextBox = box(75, Globals.MaxAccountIDLength, false, () => this.accountIDChanged());
    this.currentPasswordTextBox = box(113, Globals.MaxPasswordLength, true, () => this.currentPasswordChanged());
    this.newPassword1TextBox = box(151, Globals.MaxPasswordLength, true, () => this.newPassword1Changed());
    this.newPassword2TextBox = box(188, Globals.MaxPasswordLength, true, () => this.newPassword2Changed());
    this.accountIDTextBox.focus();
  }

  get isDisposed(): boolean { return this.disposed; }

  /** The desktop fills the boxes through Text, which raises TextChanged. */
  fill(accountID: string, currentPassword: string): void {
    this.accountIDTextBox.text = accountID;
    this.currentPasswordTextBox.text = currentPassword;
    if (accountID) this.accountIDChanged();
    if (currentPassword) this.currentPasswordChanged();
  }

  private refreshConfirmButton(): void {
    this.okButton.enabled = this.accountIDValid && this.currentPasswordValid && this.newPassword1Valid && this.newPassword2Valid;
  }

  accountIDChanged(): void {
    this.accountIDValid = ACCOUNT_REG.test(this.accountIDTextBox.text);
    setBorder(this.accountIDTextBox, this.accountIDValid ? Colour.Green : Colour.Red);
    this.refreshConfirmButton();
  }

  currentPasswordChanged(): void {
    this.currentPasswordValid = PASSWORD_REG.test(this.currentPasswordTextBox.text);
    setBorder(this.currentPasswordTextBox, this.currentPasswordValid ? Colour.Green : Colour.Red);
    this.refreshConfirmButton();
  }

  private newPassword1Changed(): void {
    this.newPassword1Valid = PASSWORD_REG.test(this.newPassword1TextBox.text);
    setBorder(this.newPassword1TextBox, this.newPassword1Valid ? Colour.Green : Colour.Red);
    this.newPassword2Changed();
  }

  private newPassword2Changed(): void {
    if (this.newPassword1TextBox.text === this.newPassword2TextBox.text) {
      this.newPassword2Valid = this.newPassword1Valid;
      setBorder(this.newPassword2TextBox, this.newPassword1Valid ? Colour.Green : Colour.Red);
    } else {
      this.newPassword2Valid = false;
      setBorder(this.newPassword2TextBox, Colour.Red);
    }
    this.refreshConfirmButton();
  }

  private changePassword(): void {
    this.okButton.enabled = false;
    this.send(ClientPackets.ChangePassword({ AccountID: this.accountIDTextBox.text, CurrentPassword: this.currentPasswordTextBox.text, NewPassword: this.newPassword1TextBox.text }));
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.win.dispose();
    const then = this.onDispose;
    this.onDispose = null;
    then?.();
  }
}

export class LoginScene {
  private readonly host: SceneHost;
  private readonly root: MirControl;
  private readonly background: MirAnimated;
  private login: LoginDialog | null;
  private account: NewAccountDialog | null = null;
  private password: ChangePasswordDialog | null = null;
  private viewKey: InputKeyDialog | null = null;

  /** `onExit`: the login dialog's Close button (Program.Form.Close). */
  constructor(parent: HTMLElement, host: SceneHost, onExit: () => void) {
    this.host = host;
    // LoginScene ctor
    this.root = new MirControl({ parent, location: [0, 0], size: [SCREEN_WIDTH, SCREEN_HEIGHT] });
    this.background = new MirAnimated({ parent: this.root, library: Lib.ChrSel, index: 0, animated: false, animationCount: 19, animationDelay: 100, loop: false, streamFrames: true });

    const login = new LoginDialog(this.background, (f) => this.host.send(f));
    this.login = login;
    login.accountButton.onClick(() => {
      login.hide();
      this.disposeViewKey();
      const account = new NewAccountDialog(this.background, (f) => this.host.send(f));
      account.onDispose = () => { if (this.account === account) this.account = null; login.show(); };
      this.account = account;
    });
    login.passButton.onClick(() => this.openPasswordChangeDialog("", ""));
    login.viewKeyButton.onClick(() => {
      if (this.viewKey && !this.viewKey.isDisposed) return;
      this.viewKey = new InputKeyDialog(this.background, login);
    });
    login.closeButton.onClick(onExit);

    // Version label: Build: {ProductCodename}.{Release|Debug}.{ProductVersion}
    new MirLabel({ parent: this.background, location: [5, SCREEN_HEIGHT - 20], autoSize: true, backColour: "rgba(50, 50, 50, 0.784)", border: true, borderColour: Colour.Black,
      text: "Build: Crystal.Release.Web" });
  }

  /** ClientVersion(Result 1): the connect box is gone; show the login dialog. */
  showLogin(): void { this.login?.show(); }

  dispose(): void {
    this.background.dispose();
    this.root.dispose();
  }

  private disposeViewKey(): void {
    if (this.viewKey && !this.viewKey.isDisposed) this.viewKey.dispose();
    this.viewKey = null;
  }

  private openPasswordChangeDialog(autoFillID: string, autoFillPassword: string): void {
    const login = this.login;
    if (!login) return;
    login.hide();
    this.disposeViewKey();
    const password = new ChangePasswordDialog(this.background, (f) => this.host.send(f));
    password.fill(autoFillID, autoFillPassword);
    password.onDispose = () => { if (this.password === password) this.password = null; login.show(); };
    this.password = password;
  }

  /** `onSelect` runs after the background animation once S.LoginSuccess arrives (LoginScene.Login(S.LoginSuccess)). */
  handle(p: ServerPacket, onSelect: (characters: SelectInfo[]) => void): boolean {
    switch (p.type) {
      case "Login": this.loginResult(p); return true;
      case "LoginBanned":
        if (this.login) this.login.okButton.enabled = true;
        this.host.box(banText(p.Reason, p.ExpiryDate));
        return true;
      case "LoginSuccess": this.loginSuccess(p.Characters, onSelect); return true;
      case "NewAccount": this.newAccount(p); return true;
      case "ChangePassword": this.changePassword(p); return true;
      case "ChangePasswordBanned":
        this.password?.dispose();
        this.host.box(banText(p.Reason, p.ExpiryDate));
        return true;
      default: return false;
    }
  }

  private loginResult(p: P<"Login">): void {
    const login = this.login;
    if (!login) return;
    login.okButton.enabled = true;
    switch (p.Result) {
      case 0: this.host.box(Text.LoginDisabled); login.clear(); break;
      case 1: this.host.box(Text.AccountIdNotAcceptable); login.accountIDTextBox.focus(); break;
      case 2: this.host.box(Text.PasswordNotAcceptable); login.passwordTextBox.focus(); break;
      case 3: this.host.box(Text.NoAccountID); login.passwordTextBox.focus(); break;
      case 4:
        this.host.box(Text.IncorrectPasswordAccountID);
        login.passwordTextBox.text = "";
        login.passwordChanged();
        login.passwordTextBox.focus();
        break;
      case 5:
        this.host.box(Text.AccountPasswordMustChangeBeforeLogin);
        this.openPasswordChangeDialog(login.accountIDTextBox.text, login.passwordTextBox.text);
        login.passwordTextBox.text = "";
        login.passwordChanged();
        break;
      default: this.host.box(`Login failed (${p.Result}).`); break;
    }
  }

  private loginSuccess(characters: SelectInfo[], onSelect: (characters: SelectInfo[]) => void): void {
    this.root.el.style.pointerEvents = "none"; // Enabled = false
    this.login?.dispose();
    this.login = null;
    this.disposeViewKey();
    // Web: on a cold cache the door cycle (~45 MB) is still streaming; go straight to the select screen rather than wait.
    if (!this.background.framesReady()) { onSelect(characters); return; }
    this.background.afterAnimation = () => onSelect(characters);
    this.background.animated = true;
  }

  private newAccount(p: P<"NewAccount">): void {
    const account = this.account;
    if (account) account.okButton.enabled = true;
    const fail = (text: string, box?: MirTextBox) => { this.host.box(text); box?.focus(); };
    switch (p.Result) {
      case 0: this.host.box(Text.AccountCreationDisabled); account?.dispose(); break;
      case 1: fail(Text.AccountIdNotAcceptable, account?.accountIDTextBox); break;
      case 2: fail(Text.PasswordNotAcceptable, account?.password1TextBox); break;
      case 3: fail(Text.EmailAddressNotAcceptable, account?.eMailTextBox); break;
      case 4: fail(Text.UserNameNotAcceptable, account?.userNameTextBox); break;
      case 5: fail(Text.SecretQuestionNotAcceptable, account?.questionTextBox); break;
      case 6: fail(Text.SecretAnswerNotAcceptable, account?.answerTextBox); break;
      case 7:
        if (account) account.accountIDTextBox.text = "";
        fail(Text.AccountIdAlreadyExists, account?.accountIDTextBox);
        break;
      case 8: this.host.box(Text.AccountCreatedSuccessfully); account?.dispose(); break;
      default: this.host.box(`Account creation failed (${p.Result}).`); break;
    }
  }

  private changePassword(p: P<"ChangePassword">): void {
    const pw = this.password;
    if (pw) pw.okButton.enabled = true;
    switch (p.Result) {
      case 0: this.host.box(Text.PasswordChangingDisabled); pw?.dispose(); break;
      case 1: this.host.box(Text.AccountIdNotAcceptable); pw?.accountIDTextBox.focus(); break;
      case 2: this.host.box(Text.CurrentPasswordNotAcceptable); pw?.currentPasswordTextBox.focus(); break;
      case 3: this.host.box(Text.NewPasswordNotAcceptable); pw?.newPassword1TextBox.focus(); break;
      case 4: this.host.box(Text.NoAccountID); pw?.accountIDTextBox.focus(); break;
      case 5:
        this.host.box(Text.IncorrectPasswordAccountID);
        if (pw) { pw.currentPasswordTextBox.focus(); pw.currentPasswordTextBox.text = ""; pw.currentPasswordChanged(); }
        break;
      case 6: this.host.box(Text.PasswordChangedSuccessfully); pw?.dispose(); break;
      default: this.host.box(`Password change failed (${p.Result}).`); break;
    }
  }
}

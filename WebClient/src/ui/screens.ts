// Account screens in plain DOM over the canvas: login, new account, character select / create.
// Mirrors LoginScene / SelectScene (result codes of S.Login / S.NewAccount / S.NewCharacter / S.StartGame, change password,
// delete character, S.StartGameBanned / StartGameDelay). After a log-out the game hands the character list back (showSelect).

import type { Session } from "../game/session.ts";
import type { ServerPacket, SelectInfo } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { MirClass, MirClassName, MirGender, MirGenderName } from "../protocol/enums.generated.ts";

const root = () => document.getElementById("screen")!;

type Props = Record<string, unknown> & { class?: string };

export function el<K extends keyof HTMLElementTagNameMap>(tag: K, props: Props = {}, ...children: (Node | string)[]): HTMLElementTagNameMap[K] {
  const e = document.createElement(tag);
  const { class: cls, ...rest } = props;
  if (cls) e.className = cls;
  Object.assign(e, rest);
  e.append(...children);
  return e;
}

function field(label: string, input: HTMLElement): HTMLElement[] {
  return [el("label", {}, label), input];
}

const LOGIN_RESULT: Record<number, string> = {
  0: "Logging in is currently disabled.", 1: "Bad account ID.", 2: "Bad password.", 3: "Account does not exist.", 4: "Wrong password.",
};
const NEW_ACCOUNT_RESULT: Record<number, string> = {
  0: "Account creation is disabled.", 1: "Bad account ID.", 2: "Bad password.", 3: "Bad e-mail.", 4: "Bad user name.",
  5: "Bad secret question.", 6: "Bad secret answer.", 7: "Account already exists.", 8: "Account created. Log in.",
};
const NEW_CHAR_RESULT: Record<number, string> = {
  0: "Character creation is disabled.", 1: "Bad character name.", 2: "Bad gender.", 3: "Bad class.", 4: "Maximum characters reached.", 5: "Character name already exists.",
};
const CHANGE_PASSWORD_RESULT: Record<number, string> = {
  0: "Password Changing is currently disabled.", 1: "Your AccountID is not acceptable.", 2: "The current Password is not acceptable.",
  3: "Your new Password is not acceptable.", 4: "The AccountID does not exist.", 5: "Incorrect Password and AccountID combination.",
  6: "Your password was changed successfully.",
};
const DELETE_RESULT: Record<number, string> = {
  0: "Deleting characters is currently disabled.", 1: "The character you selected does not exist. Contact a GM for assistance.",
};

/** LoginScene / SelectScene ban text (AccountBannedReasonExpiryDuration, condensed). */
function banText(reason: string, expiry: bigint): string {
  return `This account is banned.\nReason: ${reason}\nExpires: ${dateFromBinary(expiry).toLocaleString()}`;
}

/** DateTime.FromBinary: low 62 bits = ticks (100 ns since 0001-01-01); the kind bits are ignored here. */
function dateFromBinary(v: bigint): Date {
  const ticks = BigInt.asUintN(62, v);
  return new Date(Number((ticks - 621355968000000000n) / 10000n));
}

const START_RESULT: Record<number, string> = {
  0: "Starting the game is disabled.", 1: "You are not logged in.", 2: "Character not found.", 3: "No active map or start point found.",
};

export interface StartInfo { character: SelectInfo; resolution: number }

/** Runs the account flow until S.StartGame succeeds; `handle` is fed every packet until then. */
export class Screens {
  private readonly session: Session;
  private readonly onStart: (r: StartInfo) => void;
  private msg = el("div", { class: "msg" });
  private characters: SelectInfo[] = [];
  private selected = 0;

  constructor(session: Session, onStart: (r: StartInfo) => void) {
    this.session = session;
    this.onStart = onStart;
    this.show(el("div", { class: "panel" }, el("h1", {}, "Crystal"), el("div", {}, "Connecting to the game server…")));
  }

  private show(panel: HTMLElement): void {
    root().replaceChildren(panel);
    panel.querySelector<HTMLInputElement>("input")?.focus();
  }

  private say(text: string, ok = false): void {
    this.msg.textContent = text;
    this.msg.classList.toggle("ok", ok);
  }

  hide(): void {
    root().replaceChildren();
  }

  /** GameScene.LogOutSuccess -> SelectScene(p.Characters). */
  showSelect(characters: SelectInfo[]): void {
    this.characters = characters;
    this.selected = 0;
    this.selectPanel();
  }

  /** GameScene.ReturnToLogin -> LoginScene + a message box. */
  showLogin(message: string): void {
    this.loginPanel();
    this.say(message);
  }

  fatal(text: string): void {
    this.msg = el("div", { class: "msg" }, text);
    this.show(el("div", { class: "panel" }, el("h1", {}, "Disconnected"), this.msg,
      el("div", { class: "row" }, el("button", { class: "primary", onclick: () => location.reload() }, "Reconnect"))));
  }

  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "ClientVersion":
        if (p.Result !== 1) this.fatal("Wrong client version (the relay's --version-path does not match the server's VersionPath).");
        else this.loginPanel();
        return true;
      case "Login": this.say(LOGIN_RESULT[p.Result] ?? `Login failed (${p.Result}).`); return true;
      case "LoginBanned": this.say(banText(p.Reason, p.ExpiryDate)); return true;
      case "ChangePassword":
        if (p.Result === 6) this.loginPanel(); // success: back to the login panel with the message
        this.say(CHANGE_PASSWORD_RESULT[p.Result] ?? `Failed (${p.Result}).`, p.Result === 6);
        return true;
      case "ChangePasswordBanned": this.say(banText(p.Reason, p.ExpiryDate)); return true;
      case "DeleteCharacter": this.say(DELETE_RESULT[p.Result] ?? `Failed (${p.Result}).`); return true;
      case "DeleteCharacterSuccess":
        // SelectScene.DeleteCharacter(S.DeleteCharacterSuccess)
        this.characters = this.characters.filter((c) => c.Index !== p.CharacterIndex);
        this.selected = 0;
        this.selectPanel();
        this.say("Your character was deleted successfully.", true);
        return true;
      case "StartGameBanned": this.say(banText(p.Reason, p.ExpiryDate)); return true;
      case "StartGameDelay": this.startDelay(Number(p.Milliseconds)); return true;
      case "LoginSuccess": this.characters = p.Characters; this.selected = 0; this.selectPanel(); return true;
      case "NewAccount":
        if (p.Result === 8) { this.loginPanel(); this.say(NEW_ACCOUNT_RESULT[8], true); }
        else this.say(NEW_ACCOUNT_RESULT[p.Result] ?? `Failed (${p.Result}).`);
        return true;
      case "NewCharacter": this.say(NEW_CHAR_RESULT[p.Result] ?? `Failed (${p.Result}).`); return true;
      case "NewCharacterSuccess":
        this.characters.unshift(p.CharInfo);
        this.selected = 0;
        this.selectPanel();
        return true;
      case "StartGame":
        if (p.Result === 4) {
          const c = this.characters[this.selected];
          if (c) this.onStart({ character: c, resolution: p.Resolution });
        } else this.say(START_RESULT[p.Result] ?? `Start failed (${p.Result}).`);
        return true;
      default:
        return false;
    }
  }

  private loginPanel(): void {
    let remembered = "";
    try { remembered = localStorage.getItem("crystal.account") ?? ""; } catch { /* storage blocked */ }
    const id = el("input", { value: remembered, maxLength: 15, autocomplete: "username" });
    const pw = el("input", { type: "password", maxLength: 15, autocomplete: "current-password" });
    this.msg = el("div", { class: "msg" });
    const login = () => {
      try { localStorage.setItem("crystal.account", id.value); } catch { /* storage blocked */ }
      this.say("");
      this.session.send(ClientPackets.Login({ AccountID: id.value, Password: pw.value }));
    };
    const enter = (e: KeyboardEvent) => { if (e.key === "Enter") login(); };
    id.onkeydown = pw.onkeydown = enter;
    this.show(el("div", { class: "panel" }, el("h1", {}, "Log in"), ...field("Account ID", id), ...field("Password", pw), this.msg,
      el("div", { class: "row" }, el("button", { class: "primary", onclick: login }, "Log in"), el("button", { onclick: () => this.newAccountPanel() }, "New account"),
        el("button", { onclick: () => this.changePasswordPanel() }, "Change password"))));
    if (remembered) pw.focus();
  }

  /** LoginScene change-password panel (C.ChangePassword). */
  private changePasswordPanel(): void {
    const id = el("input", { maxLength: 15 }), cur = el("input", { type: "password", maxLength: 15 });
    const pw = el("input", { type: "password", maxLength: 15 }), pw2 = el("input", { type: "password", maxLength: 15 });
    this.msg = el("div", { class: "msg" });
    const submit = () => {
      if (pw.value !== pw2.value) return this.say("Passwords do not match.");
      this.say("");
      this.session.send(ClientPackets.ChangePassword({ AccountID: id.value, CurrentPassword: cur.value, NewPassword: pw.value }));
    };
    this.show(el("div", { class: "panel" }, el("h1", {}, "Change password"), ...field("Account ID", id), ...field("Current password", cur),
      ...field("New password", pw), ...field("Confirm new password", pw2), this.msg,
      el("div", { class: "row" }, el("button", { class: "primary", onclick: submit }, "Change"), el("button", { onclick: () => this.loginPanel() }, "Back"))));
  }

  /** SelectScene.StartGame(S.StartGameDelay): a counting-down message. */
  private startDelay(ms: number): void {
    const until = performance.now() + ms;
    const msg = this.msg;
    const tick = () => {
      const s = Math.ceil((until - performance.now()) / 1000);
      if (this.msg !== msg || s <= 0) { if (this.msg === msg) this.say(""); return; }
      this.say(`You cannot log onto this character for another ${s} seconds.`);
      setTimeout(tick, 250);
    };
    tick();
  }

  /** SelectScene.DeleteCharacter: Yes/No, then type the name; a mismatch is "Incorrect Entry." */
  private deleteCharacter(): void {
    const c = this.characters[this.selected];
    if (!c) return;
    if (!confirm(`Are you sure you want to Delete the character ${c.Name}?`)) return;
    const name = prompt("Please enter the characters name.");
    if (name === null) return;
    if (name !== c.Name) { this.say("Incorrect Entry."); return; }
    this.session.send(ClientPackets.DeleteCharacter({ CharacterIndex: c.Index }));
  }

  private newAccountPanel(): void {
    const id = el("input", { maxLength: 15 }), pw = el("input", { type: "password", maxLength: 15 }), pw2 = el("input", { type: "password", maxLength: 15 });
    const name = el("input", { maxLength: 20 }), q = el("input", { maxLength: 30 }), a = el("input", { maxLength: 30 }), mail = el("input", { type: "email", maxLength: 50 });
    this.msg = el("div", { class: "msg" });
    const create = () => {
      if (pw.value !== pw2.value) return this.say("Passwords do not match.");
      this.say("");
      // BirthDate left blank = DateTime.MinValue, whose ToBinary() is 0 (LoginScene does the same).
      this.session.send(ClientPackets.NewAccount({
        AccountID: id.value, Password: pw.value, BirthDate: 0n, UserName: name.value,
        SecretQuestion: q.value, SecretAnswer: a.value, EMailAddress: mail.value,
      }));
    };
    this.show(el("div", { class: "panel" }, el("h1", {}, "New account"), ...field("Account ID", id), ...field("Password", pw), ...field("Confirm password", pw2),
      ...field("User name", name), ...field("Secret question", q), ...field("Secret answer", a), ...field("E-mail", mail), this.msg,
      el("div", { class: "row" }, el("button", { class: "primary", onclick: create }, "Create"), el("button", { onclick: () => this.loginPanel() }, "Back"))));
  }

  private selectPanel(): void {
    this.msg = el("div", { class: "msg" });
    const list = el("ul", { class: "chars" });
    const start = () => {
      const c = this.characters[this.selected];
      if (!c) return this.say("Create a character first.");
      this.say("");
      this.session.send(ClientPackets.StartGame({ CharacterIndex: c.Index }));
    };
    const render = () => {
      list.replaceChildren(...this.characters.map((c, i) => el("li", { class: i === this.selected ? "sel" : "", onclick: () => { this.selected = i; render(); }, ondblclick: start },
        el("span", {}, c.Name), el("span", { class: "meta" }, `Lv ${c.Level} ${MirClassName[c.Class] ?? c.Class} ${MirGenderName[c.Gender] ?? ""}`))));
    };
    render();
    this.show(el("div", { class: "panel" }, el("h1", {}, "Select character"), list, this.msg,
      el("div", { class: "row" }, el("button", { class: "primary", onclick: start }, "Start"), el("button", { onclick: () => this.newCharacterPanel() }, "New character"),
        el("button", { onclick: () => this.deleteCharacter() }, "Delete"),
        el("button", { onclick: () => { this.session.send(ClientPackets.LogOut()); location.reload(); } }, "Log out"))));
  }

  private newCharacterPanel(): void {
    const name = el("input", { maxLength: 14 });
    const cls = el("select", {}, ...Object.entries(MirClass).map(([k, v]) => el("option", { value: String(v) }, k)));
    const gender = el("select", {}, ...Object.entries(MirGender).map(([k, v]) => el("option", { value: String(v) }, k)));
    this.msg = el("div", { class: "msg" });
    const create = () => {
      this.say("");
      this.session.send(ClientPackets.NewCharacter({ Name: name.value, Class: Number(cls.value), Gender: Number(gender.value) }));
    };
    this.show(el("div", { class: "panel" }, el("h1", {}, "New character"), ...field("Name", name), ...field("Class", cls), ...field("Gender", gender), this.msg,
      el("div", { class: "row" }, el("button", { class: "primary", onclick: create }, "Create"), el("button", { onclick: () => this.selectPanel() }, "Back"))));
  }
}

// Shared bits of the account scenes (loginScene.ts, selectScene.ts, newCharacterDialog.ts): the Globals limits, the
// ClientTextKeys strings they show (Client/Localization/English.json), DateTime.FromBinary formatting, the ban text,
// MirControl.Border / BorderColour on a control, and a MirMessageBox whose Label the caller keeps updating
// (SelectScene.StartGame(S.StartGameDelay) rewrites it in BeforeDraw; messageBox.ts has no label handle).

import type { BoxButton } from "./messageBox.ts";
import { MirWindow, MirLabel, MirButton, MirControl, Lib } from "./mir/controls.ts";
import { SoundList } from "../game/audio.ts";
import { MirClass } from "../protocol/enums.generated.ts";

/** Shared/Globals.cs limits. */
export const Globals = {
  MinAccountIDLength: 3, MaxAccountIDLength: 15, MinPasswordLength: 5, MaxPasswordLength: 15,
  MinCharacterNameLength: 3, MaxCharacterNameLength: 15, MaxCharacterCount: 4,
} as const;

/** The ClientTextKeys the account scenes use (English.json). `{0}` placeholders go through `format`. */
export const Text = {
  GameName: "Legend of Mir 2",
  Never: "Never",
  LastOnlineTitle: "Last Online:",
  AttemptingConnectServer: "Attempting to connect to the server.",
  AccountCreationDisabled: "Account creation is currently disabled.",
  AccountIdNotAcceptable: "Your AccountID is not acceptable.",
  PasswordNotAcceptable: "Your Password is not acceptable.",
  EmailAddressNotAcceptable: "Your E-Mail Address is not acceptable.",
  UserNameNotAcceptable: "Your User Name is not acceptable.",
  SecretQuestionNotAcceptable: "Your Secret Question is not acceptable.",
  SecretAnswerNotAcceptable: "Your Secret Answer is not acceptable.",
  AccountIdAlreadyExists: "An Account with this ID already exists.",
  AccountCreatedSuccessfully: "Your account was created successfully.",
  PasswordChangingDisabled: "Password Changing is currently disabled.",
  CurrentPasswordNotAcceptable: "The current Password is not acceptable.",
  NewPasswordNotAcceptable: "Your new Password is not acceptable.",
  NoAccountID: "The AccountID does not exist.",
  IncorrectPasswordAccountID: "Incorrect Password and AccountID combination.",
  PasswordChangedSuccessfully: "Your password was changed successfully.",
  LoginDisabled: "Logging in is currently disabled.",
  AccountPasswordMustChangeBeforeLogin: "The account's password must be changed before logging in.",
  BannedDuration: "This account is banned.\n\nReason: {0}\nExpiryDate: {1}\nDuration: {2} Hours, {3} Minutes, {4} Seconds",
  BtnEsc: "Esc", BtnDelete: "Delete", BtnEnter: "Enter", BtnRandom: "Random",
  AccountIdDescription: " Description: Account ID.\n Accepted characters: a-z A-Z 0-9.\n Length: between {0} and {1} characters.",
  PasswordDescription: " Description: Password.\n Accepted characters: a-z A-Z 0-9.\n Length: between {0} and {1} characters.",
  EmailAddressDescription: " Description: E-Mail Address.\n Format: Example@Example.Com.\n Max Length: 50 characters.\n Optional Field.",
  UserNameDescription: " Description: User Name.\n Accepted characters:All.\n Length: between 0 and 20 characters.\n Optional Field.",
  BirthDateDescription: " Description: Birth Date.\n Format: {0}.\n Length: 10 characters.\n Optional Field.",
  SecretQuestionDescription: " Description: Secret Question.\n Accepted characters: All.\n Length: between 0 and 30 characters.\n Optional Field.",
  SecretAnswerDescription: " Description: Secret Answer.\n Accepted characters: All.\n Length: between 0 and 30 characters.\n Optional Field.",
  CreatingNewCharactersDisabled: "Creating new characters is currently disabled.",
  YourCharacterNameNotAcceptable: "Your Character Name is not acceptable.",
  GenderNotExistContactGM: "The gender you selected does not exist.\n Contact a GM for assistance.",
  ClassNotExistContactGM: "The class you selected does not exist.\n Contact a GM for assistance.",
  YouCannotMakeMoreCharacters: "You cannot make anymore then {0} Characters.",
  CharacterNameExists: "A Character with this name already exists.",
  YourCharacterCreatedSuccessfully: "Your character was created successfully.",
  ConfirmDeleteCharacter: "Are you sure you want to Delete the character {0}?",
  PleaseEnterCharacterName: "Please enter the characters name.",
  IncorrectEntry: "Incorrect Entry.",
  DeletingCharactersDisabled: "Deleting characters is currently disabled.",
  SelectedCharacterNotExist: "The character you selected does not exist.\n Contact a GM for assistance.",
  YourCharacterDeletedSuccessfully: "Your character was deleted successfully.",
  CannotLoginCharacterSeconds: "You cannot log onto this character for another {0} seconds.",
  StartingGameDisabled: "Starting the game is currently disabled.",
  YouNotLoggedIn: "You are not logged in.",
  YourCharacterNotFound: "Your character could not be found.",
  NoActiveMapOrStartPointFound: "No active map and/or start point found.",
  WarriorsDescription: "Warriors are a class of great strength and vitality. They are not easily killed in battle and have the advantage of being able to use a variety of heavy weapons and Armour. Therefore, Warriors favor attacks that are based on melee physical damage. They are weak in ranged attacks, however the variety of equipment that are developed specifically for Warriors complement their weakness in ranged combat.",
  WizardsDescription: "Wizards are a class of low strength and stamina, but have the ability to use powerful spells. Their offensive spells are very effective, but because it takes time to cast these spells, they're likely to leave themselves open for enemy's attacks. Therefore, the physically weak wizards must aim to attack their enemies from a safe distance.",
  TaoistsDescription: "Taoists are well disciplined in the study of Astronomy, Medicine, and others aside from Mu-Gong. Rather then directly engaging the enemies, their specialty lies in assisting their allies with support. Taoists can summon powerful creatures and have a high resistance to magic, and is a class with well balanced offensive and defensive abilities.",
  AssassinsDescription: "Assassins are members of a secret organization and their history is relatively unknown. They're capable of hiding themselves and performing attacks while being unseen by others, which naturally makes them excellent at making fast kills. It is necessary for them to avoid being in battles with multiple enemies due to their weak vitality and strength.",
  ArchersDescription: "Archers are a class of great accuracy and strength, using their powerful skills with bows to deal extraordinary damage from range. Much like wizards, they rely on their keen instincts to dodge oncoming attacks as they tend to leave themselves open to frontal attacks. However, their physical prowess and deadly aim allows them to instil fear into anyone they hit.",
} as const;

/** string.Format for `{n}` placeholders. */
export function format(text: string, ...args: (string | number)[]): string {
  return text.replace(/\{(\d+)\}/g, (m, i: string) => (args[Number(i)] !== undefined ? String(args[Number(i)]) : m));
}

/** What the account scenes need from Screens: the session and message boxes that close with the scene. */
export interface SceneHost {
  send(frame: Uint8Array): void;
  /** MirMessageBox (OK unless `buttons`); closed when the account screens hide. */
  box(text: string, buttons?: BoxButton[]): () => void;
  /** MirInputBox; closed when the account screens hide. */
  input(text: string, onOk: (value: string) => boolean | void, onCancel?: () => void): () => void;
  /** Registers any other closer (live boxes) to run when the account screens hide. */
  track(close: () => void): void;
}

// — DateTime —

const TICKS_AT_EPOCH = 621355968000000000n;

/** DateTime.FromBinary ticks (low 62 bits; the Kind bits are ignored) as wall-clock milliseconds (read with UTC getters). */
export function wallMs(binary: bigint): number {
  const ticks = BigInt.asUintN(62, binary);
  return Number((ticks - TICKS_AT_EPOCH) / 10000n);
}

/** DateTime.MinValue.ToBinary() is 0. */
export function isMinValue(binary: bigint): boolean {
  return BigInt.asUintN(62, binary) === 0n;
}

/** The current local wall clock in the same scale as wallMs (CMain.Now). */
export function nowWallMs(): number {
  const d = new Date();
  return d.getTime() - d.getTimezoneOffset() * 60000;
}

/** DateTime.ToString("yyyy/MM/dd HH:mm:ss") of a wallMs value. */
export function formatDateTime(ms: number): string {
  const d = new Date(ms);
  const p = (n: number) => String(n).padStart(2, "0");
  return `${d.getUTCFullYear()}/${p(d.getUTCMonth() + 1)}/${p(d.getUTCDate())} ${p(d.getUTCHours())}:${p(d.getUTCMinutes())}:${p(d.getUTCSeconds())}`;
}

/** wallMs -> DateTime ticks with Kind Unspecified (ToBinary = Ticks). */
export function binaryFromWallMs(ms: number): bigint {
  return BigInt(Math.round(ms)) * 10000n + TICKS_AT_EPOCH;
}

/** A server DateTime.ToBinary expiry that honours its Kind bits: Utc (1) and Local (2; ToBinary stores UTC ticks) are
 *  compared with Date.now() and printed in browser-local time; Unspecified (0) stays wall-clock (wallMs vs nowWallMs). */
export function expiryClock(binary: bigint): { leftMs: () => number; label: string } {
  const kind = Number((BigInt.asUintN(64, binary) >> 62n) & 3n);
  if (kind === 0) {
    const exp = wallMs(binary);
    return { leftMs: () => exp - nowWallMs(), label: formatDateTime(exp) };
  }
  const ticks = BigInt.asUintN(62, binary);
  const utc = Number((ticks - TICKS_AT_EPOCH) / 10000n);
  const local = utc - new Date(utc).getTimezoneOffset() * 60000;
  return { leftMs: () => utc - Date.now(), label: formatDateTime(local) };
}

/** AccountBannedReasonDuration / AccountBannedReasonExpiryDuration (same English text). */
export function banText(reason: string, expiry: bigint): string {
  const exp = wallMs(expiry);
  const d = Math.max(0, exp - nowWallMs()) / 1000;
  const hours = Math.floor(d / 3600);
  return format(Text.BannedDuration, reason, formatDateTime(exp), hours.toLocaleString("en-US"), Math.floor((d % 3600) / 60), Math.floor(d % 60));
}

// — Controls —

/** MirControl.Border + BorderColour (a 1 px line round the control). null = Border false. */
export function setBorder(c: MirControl, colour: string | null): void {
  c.el.style.outline = colour ? `1px solid ${colour}` : "";
}

/** Color.Red / Green / Gray as the desktop draws them. */
export const Colour = { Red: "#ff0000", Green: "#008000", Gray: "#808080", Black: "#000000" } as const;

/** SelectScene.UpdateInterface / NewCharacterDialog.UpdateInterface: ChrSel.Lib preview frames by class and gender. */
export function chrSelIndex(cls: number, gender: number): number {
  switch (cls) {
    case MirClass.Warrior: return gender === 0 ? 20 : 300;
    case MirClass.Wizard: return gender === 0 ? 40 : 320;
    case MirClass.Taoist: return gender === 0 ? 60 : 340;
    case MirClass.Assassin: return gender === 0 ? 80 : 360;
    case MirClass.Archer: return gender === 0 ? 100 : 140;
    default: return 20;
  }
}

/** A MirMessageBox (Prguse 360, OK) whose label text can change while it is open. Enter / Escape press OK. */
export function liveMessageBox(text: string, onOk?: () => void): { setText(t: string): void; close(): void } {
  const win = new MirWindow({ library: Lib.Prguse, index: 360, location: "center", modal: true, sort: true });
  const label = new MirLabel({ parent: win, location: [35, 35], size: [390, 110], text });
  const keys = (e: KeyboardEvent) => {
    if (!win.el.isConnected || (e.key !== "Enter" && e.key !== "Escape")) return;
    e.preventDefault();
    e.stopPropagation();
    ok.el.click();
  };
  const close = () => { win.dispose(); document.removeEventListener("keydown", keys, true); };
  const ok = new MirButton({ parent: win, library: Lib.Title, index: 200, hoverIndex: 201, pressedIndex: 202, location: [360, 157], sound: SoundList.ButtonB,
    onClick: () => { close(); onOk?.(); } });
  document.addEventListener("keydown", keys, true);
  win.show();
  return { setText: (t) => { label.text = t; }, close };
}

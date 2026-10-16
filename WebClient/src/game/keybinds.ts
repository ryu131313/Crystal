// Central key map: Client/KeyBindSettings.cs (enum KeybindOptions, class KeyBind, KeyBindSettings.New / GetKey) and the
// matching loop of GameScene.GameScene_KeyDown (Client/MirScenes/GameScene.cs). The desktop keeps the bindings in
// KeyBinds.ini; here only the bindings that differ from the defaults are kept in localStorage ("crystal.keybinds").
//
// Keys are stored by their System.Windows.Forms.Keys name (the KeyBinds.ini RequireKey value): "I", "D1", "NumPad1",
// "F1", "Escape", "Tab", ... `keyName(e)` turns a KeyboardEvent into that name. Each modifier has the desktop's
// Require* tri-state: 0 = must NOT be held, 1 = must be held, 2 = don't care.
// The desktop's "Tilde" modifier is Keys.Oem8 held (CMain.CMain_KeyDown / KeyUp); here the Backquote key (left of 1).

/** KeybindOptions (Client/KeyBindSettings.cs; client-only, not in enums.generated.ts). Values = desktop ordinals. */
export const KeybindOptions = {
  Bar1Skill1: 0, Bar1Skill2: 1, Bar1Skill3: 2, Bar1Skill4: 3, Bar1Skill5: 4, Bar1Skill6: 5, Bar1Skill7: 6, Bar1Skill8: 7,
  Bar2Skill1: 8, Bar2Skill2: 9, Bar2Skill3: 10, Bar2Skill4: 11, Bar2Skill5: 12, Bar2Skill6: 13, Bar2Skill7: 14, Bar2Skill8: 15,
  Inventory: 16, Inventory2: 17, Equipment: 18, Equipment2: 19, Skills: 20, Skills2: 21, Creature: 22, MountWindow: 23,
  Mount: 24, Fishing: 25, Skillbar: 26, Mentor: 27, Relationship: 28, Friends: 29, Guilds: 30, GameShop: 31, Quests: 32,
  Closeall: 33, Options: 34, Options2: 35, Group: 36, Belt: 37, BeltFlip: 38, Pickup: 39,
  Belt1: 40, Belt1Alt: 41, Belt2: 42, Belt2Alt: 43, Belt3: 44, Belt3Alt: 45, Belt4: 46, Belt4Alt: 47,
  Belt5: 48, Belt5Alt: 49, Belt6: 50, Belt6Alt: 51, Belt7: 52, Belt7Alt: 53, Belt8: 54, Belt8Alt: 55,
  Logout: 56, Exit: 57, CreaturePickup: 58, CreatureAutoPickup: 59, Minimap: 60, Bigmap: 61, Trade: 62, Rental: 63,
  ChangeAttackmode: 64, AttackmodePeace: 65, AttackmodeGroup: 66, AttackmodeGuild: 67, AttackmodeEnemyguild: 68,
  AttackmodeRedbrown: 69, AttackmodeAll: 70, ChangePetmode: 71, PetmodeBoth: 72, PetmodeMoveonly: 73, PetmodeAttackonly: 74,
  PetmodeNone: 75, Help: 76, Keybind: 77, Autorun: 78, Cameramode: 79, Screenshot: 80, DropView: 81, TargetDead: 82,
  Ranking: 83, AddGroupMember: 84,
  HeroSkill1: 85, HeroSkill2: 86, HeroSkill3: 87, HeroSkill4: 88, HeroSkill5: 89, HeroSkill6: 90, HeroSkill7: 91, HeroSkill8: 92,
  HeroInventory: 93, HeroEquipment: 94, HeroSkills: 95, TargetSpellLockOn: 96, PetmodeFocusMasterTarget: 97,
  // Web-only (phase 2.9, not in the desktop enum): the hero belt cells (hero Inventory[0..1]) on keys 9 / 0, because
  // Belt7 / Belt8 are the player's belt cells 7-8 here (BeltIdx 8). Explicit unused values after the desktop's last.
  HeroBelt1: 98, HeroBelt1Alt: 99, HeroBelt2: 100, HeroBelt2Alt: 101,
} as const;
export type KeybindOption = (typeof KeybindOptions)[keyof typeof KeybindOptions];

const OPTION_NAMES = new Map<number, string>(Object.entries(KeybindOptions).map(([k, v]) => [v, k]));
/** The KeybindOptions member name (the KeyBinds.ini section name). */
export function optionName(opt: KeybindOption): string { return OPTION_NAMES.get(opt) ?? String(opt); }

/** KeyBind.Require*: 0 = must not be held, 1 = must be held, 2 = don't care. */
export type Require = 0 | 1 | 2;

/** KeyBind (Client/KeyBindSettings.cs). `key` = Keys name, "" = Keys.None (unbound). */
export interface KeyBind {
  readonly function: KeybindOption;
  readonly group: string;
  readonly description: string;
  key: string;
  requireAlt: Require;
  requireShift: Require;
  requireTilde: Require;
  requireCtrl: Require;
}

/** KeyBindSettings.New: the defaults in the desktop's order (Alt, Shift, Tilde, Ctrl as written there). */
function defaults(): KeyBind[] {
  const list: KeyBind[] = [];
  const add = (group: string, description: string, fn: KeybindOption, alt: Require, shift: Require, tilde: Require, ctrl: Require, key: string) =>
    list.push({ function: fn, group, description, key, requireAlt: alt, requireShift: shift, requireTilde: tilde, requireCtrl: ctrl });
  const O = KeybindOptions;
  add("Dialogs", "Inventory Open/Close", O.Inventory, 2, 2, 2, 2, "F9");
  add("Dialogs", "Inventory Open/Close Alt", O.Inventory2, 2, 2, 2, 0, "I");
  add("Dialogs", "Equipment Open/Close", O.Equipment, 2, 2, 2, 2, "F10");
  add("Dialogs", "Equipment Open/Close Alt", O.Equipment2, 2, 2, 2, 0, "C");
  add("Dialogs", "Skills Open/Close", O.Skills, 2, 2, 2, 2, "F11");
  add("Dialogs", "Skills Open/Close Alt", O.Skills2, 2, 2, 2, 0, "S");
  add("Dialogs", "Hero Inventory Open/Close", O.HeroInventory, 2, 2, 2, 1, "I");
  add("Dialogs", "Hero Equipment Open/Close", O.HeroEquipment, 2, 2, 2, 1, "C");
  add("Dialogs", "Hero Skills Open/Close", O.HeroSkills, 2, 2, 2, 1, "S");
  add("Dialogs", "Creatures Open/Close", O.Creature, 2, 2, 2, 2, "E");
  add("Dialogs", "Mount Open/Close", O.MountWindow, 2, 2, 2, 2, "J");
  add("Dialogs", "Fishing Open/Close", O.Fishing, 2, 2, 2, 2, "N");
  add("Dialogs", "Skillbar Open/Close", O.Skillbar, 2, 2, 2, 2, "R");
  add("Dialogs", "Mentor Open/Close", O.Mentor, 2, 2, 2, 2, "");
  add("Dialogs", "Relationship Open/Close", O.Relationship, 2, 2, 2, 2, "L");
  add("Dialogs", "Friends Open/Close", O.Friends, 2, 2, 2, 2, "F");
  add("Dialogs", "Guild Open/Close", O.Guilds, 2, 2, 2, 0, "G");
  add("Dialogs", "Gameshop Open/Close", O.GameShop, 2, 2, 2, 2, "Y");
  add("Dialogs", "Quest Diary Open/Close", O.Quests, 0, 2, 2, 2, "Q");
  add("Dialogs", "Options Open/Close", O.Options, 2, 2, 2, 2, "F12");
  add("Dialogs", "Options Open/Close Alt", O.Options2, 2, 2, 2, 2, "O");
  add("Dialogs", "Group Open/Close", O.Group, 2, 2, 2, 2, "P");
  add("Dialogs", "Belt Open/Close", O.Belt, 2, 2, 2, 0, "Z");
  add("Dialogs", "Minimap Open/Close", O.Minimap, 2, 2, 2, 2, "V");
  add("Dialogs", "Bigmap Open/Close", O.Bigmap, 2, 2, 2, 2, "B");
  add("Dialogs", "Ranking Open/Close", O.Ranking, 2, 2, 2, 2, "K");
  add("Dialogs", "Help Open/Close", O.Help, 2, 0, 2, 0, "H");
  add("Dialogs", "Keybinds Open/Close", O.Keybind, 2, 2, 2, 2, "U");
  add("Dialogs", "Close All Windows", O.Closeall, 2, 2, 2, 2, "Escape");
  for (let i = 0; i < 8; i++) add("Skillbar", `Skillbar Slot ${i + 1}`, (O.Bar1Skill1 + i) as KeybindOption, 2, 0, 0, 0, `F${i + 1}`);
  for (let i = 0; i < 8; i++) add("Skillbar", `Skillbar Alt Slot ${i + 1}`, (O.Bar2Skill1 + i) as KeybindOption, 2, 0, 0, 1, `F${i + 1}`);
  for (let i = 0; i < 8; i++) add("Skillbar", `Hero Skillbar Slot ${i + 1}`, (O.HeroSkill1 + i) as KeybindOption, 2, 1, 0, 0, `F${i + 1}`);
  add("Belt", "Rotate Belt", O.BeltFlip, 2, 2, 2, 1, "Z");
  for (let i = 0; i < 8; i++) {
    add("Belt", `Belt Slot ${i + 1}`, (O.Belt1 + i * 2) as KeybindOption, 2, 2, 2, 2, `D${i + 1}`);
    add("Belt", `Belt Slot ${i + 1} Alt`, (O.Belt1Alt + i * 2) as KeybindOption, 2, 2, 2, 2, `NumPad${i + 1}`);
  }
  add("Belt", "Hero Belt Slot 1", O.HeroBelt1, 2, 2, 2, 2, "D9");
  add("Belt", "Hero Belt Slot 1 Alt", O.HeroBelt1Alt, 2, 2, 2, 2, "NumPad9");
  add("Belt", "Hero Belt Slot 2", O.HeroBelt2, 2, 2, 2, 2, "D0");
  add("Belt", "Hero Belt Slot 2 Alt", O.HeroBelt2Alt, 2, 2, 2, 2, "NumPad0");
  add("General", "Logout", O.Logout, 1, 2, 2, 2, "X");
  add("General", "Exit", O.Exit, 1, 2, 2, 2, "Q");
  add("General", "Mount/Dismount", O.Mount, 2, 2, 2, 2, "M");
  add("General", "Pickup Floor Item", O.Pickup, 2, 2, 2, 2, "Tab");
  add("General", "Creature Item Pickup", O.CreaturePickup, 0, 2, 2, 2, "X");
  add("General", "Creature Auto Pickup", O.CreatureAutoPickup, 1, 2, 2, 0, "A");
  add("General", "Request Trade", O.Trade, 2, 2, 2, 2, "T");
  add("General", "Recruit Group Member", O.AddGroupMember, 2, 2, 2, 1, "G");
  add("Toggle", "Toggle Attack Mode", O.ChangeAttackmode, 2, 0, 2, 1, "H");
  add("Toggle", "Set Attack Mode : Peace", O.AttackmodePeace, 2, 2, 2, 2, "");
  add("Toggle", "Set Attack Mode : Group", O.AttackmodeGroup, 2, 2, 2, 2, "");
  add("Toggle", "Set Attack Mode : Guild", O.AttackmodeGuild, 2, 2, 2, 2, "");
  add("Toggle", "Set Attack Mode : Enemy Guild", O.AttackmodeEnemyguild, 2, 2, 2, 2, "");
  add("Toggle", "Set Attack Mode : Red/Brown", O.AttackmodeRedbrown, 2, 2, 2, 2, "");
  add("Toggle", "Set Attack Mode : All", O.AttackmodeAll, 2, 2, 2, 2, "");
  add("Toggle", "Toggle Pet Mode", O.ChangePetmode, 0, 2, 2, 1, "A");
  add("Toggle", "Set Pet Mode : Both", O.PetmodeBoth, 2, 2, 2, 2, "");
  add("Toggle", "Set Pet Mode : Move Only", O.PetmodeMoveonly, 2, 2, 2, 2, "");
  add("Toggle", "Set Pet Mode : Attack Only", O.PetmodeAttackonly, 2, 2, 2, 2, "");
  add("Toggle", "Set Pet Mode : None", O.PetmodeNone, 2, 2, 2, 2, "");
  add("Toggle", "Set Pet Mode : Focus Master Target", O.PetmodeFocusMasterTarget, 2, 2, 2, 2, "");
  add("Toggle", "Toggle Autorun", O.Autorun, 2, 2, 2, 2, "D");
  add("Toggle", "Toggle Camera Mode", O.Cameramode, 2, 2, 2, 2, "Insert");
  add("Toggle", "Take Screenshot", O.Screenshot, 2, 2, 2, 2, "PrintScreen");
  add("Toggle", "Toggle Dropview", O.DropView, 2, 2, 2, 2, "Tab");
  add("Combat", "Hold to enable target spell lock-on", O.TargetSpellLockOn, 2, 2, 2, 2, "");
  // KeybindOptions.Rental and TargetDead have no KeyBind in KeyBindSettings.New, so they can never fire on the desktop.
  return list;
}

const DEFAULTS: readonly KeyBind[] = defaults();
const current: KeyBind[] = defaults();
const STORAGE_KEY = "crystal.keybinds";
type Stored = Record<string, { key: string; alt: Require; shift: Require; tilde: Require; ctrl: Require }>;

const isReq = (v: unknown): v is Require => v === 0 || v === 1 || v === 2;

/** KeyBindSettings.Load: overrides by option name, unknown names / bad values ignored. */
function load(): void {
  let raw: string | null = null;
  try { raw = localStorage.getItem(STORAGE_KEY); } catch { return; }
  if (!raw) return;
  let data: Stored;
  try { data = JSON.parse(raw) as Stored; } catch { return; }
  for (const b of current) {
    const s = data[optionName(b.function)];
    if (!s || typeof s.key !== "string") continue;
    b.key = s.key;
    if (isReq(s.alt)) b.requireAlt = s.alt;
    if (isReq(s.shift)) b.requireShift = s.shift;
    if (isReq(s.tilde)) b.requireTilde = s.tilde;
    if (isReq(s.ctrl)) b.requireCtrl = s.ctrl;
  }
}

/** KeyBindSettings.Save (only the bindings that differ from the defaults). */
function save(): void {
  const data: Stored = {};
  current.forEach((b, i) => {
    const d = DEFAULTS[i]!;
    if (sameBind(b, d)) return;
    data[optionName(b.function)] = { key: b.key, alt: b.requireAlt, shift: b.requireShift, tilde: b.requireTilde, ctrl: b.requireCtrl };
  });
  try {
    if (Object.keys(data).length === 0) localStorage.removeItem(STORAGE_KEY);
    else localStorage.setItem(STORAGE_KEY, JSON.stringify(data));
  } catch { /* storage blocked: kept for this session only */ }
}

function sameBind(a: KeyBind, b: KeyBind): boolean {
  return a.key === b.key && a.requireAlt === b.requireAlt && a.requireShift === b.requireShift && a.requireTilde === b.requireTilde && a.requireCtrl === b.requireCtrl;
}

load();

// — Tilde (CMain.Tilde: Keys.Oem8 held) —
let tilde = false;
/** Set while the KeyboardLayoutDialog waits for a key (GameScene_KeyDown skips every binding then). */
let capturing = false;
if (typeof window !== "undefined") {
  window.addEventListener("keydown", (e) => { if (e.code === "Backquote") tilde = true; }, { capture: true });
  window.addEventListener("keyup", (e) => { if (e.code === "Backquote") tilde = false; }, { capture: true });
  window.addEventListener("blur", () => { tilde = false; }); // CMain_Deactivate
}
/** CMain.Tilde. */
export function tildeHeld(): boolean { return tilde; }
/** KeyboardLayoutDialog.WaitingForBind != null: every `matches` is false meanwhile. */
export function setCapturing(on: boolean): void { capturing = on; }

// — Key names (System.Windows.Forms.Keys) —
const CODE_NAMES: Record<string, string> = {
  Escape: "Escape", Tab: "Tab", Enter: "Return", NumpadEnter: "Return", Space: "Space", Backspace: "Back", Delete: "Delete",
  Insert: "Insert", Home: "Home", End: "End", PageUp: "PageUp", PageDown: "Next", ArrowUp: "Up", ArrowDown: "Down",
  ArrowLeft: "Left", ArrowRight: "Right", PrintScreen: "PrintScreen", Pause: "Pause", ScrollLock: "Scroll", CapsLock: "Capital",
  NumLock: "NumLock", ContextMenu: "Apps", Backquote: "Oem8", Minus: "OemMinus", Equal: "Oemplus", BracketLeft: "OemOpenBrackets",
  BracketRight: "Oem6", Backslash: "Oem5", IntlBackslash: "OemBackslash", Semicolon: "Oem1", Quote: "Oem7", Comma: "Oemcomma",
  Period: "OemPeriod", Slash: "OemQuestion", NumpadMultiply: "Multiply", NumpadAdd: "Add", NumpadSubtract: "Subtract",
  NumpadDecimal: "Decimal", NumpadDivide: "Divide",
  ShiftLeft: "ShiftKey", ShiftRight: "ShiftKey", ControlLeft: "ControlKey", ControlRight: "ControlKey", AltLeft: "Menu", AltRight: "Menu",
  MetaLeft: "LWin", MetaRight: "RWin",
};

/** The Keys name of a key event (KeyEventArgs.KeyCode.ToString()). Letters follow the layout (e.key, like the desktop's
 *  virtual keys); digits, F-keys, the numpad and the rest follow the physical key (e.code), so Shift+1 is still D1. */
export function keyName(e: KeyboardEvent): string {
  if (e.key.length === 1 && /[a-z]/i.test(e.key)) return e.key.toUpperCase();
  const c = e.code;
  let m = /^Key([A-Z])$/.exec(c);
  if (m) return m[1]!;
  m = /^Digit([0-9])$/.exec(c);
  if (m) return `D${m[1]}`;
  m = /^Numpad([0-9])$/.exec(c);
  if (m) return `NumPad${m[1]}`;
  if (/^F([1-9]|1[0-9]|2[0-4])$/.test(c)) return c;
  return CODE_NAMES[c] ?? (c || e.key);
}

/** Keys names the binding dialog ignores (KeyboardLayoutDialog.CheckNewInput: ControlKey, Menu, ShiftKey, Oem8, None). */
export function isModifierKeyName(name: string): boolean {
  return name === "ControlKey" || name === "Menu" || name === "ShiftKey" || name === "Oem8" || name === "" || name === "LWin" || name === "RWin";
}

// — Lookup / matching —
function find(list: readonly KeyBind[], opt: KeybindOption): KeyBind | undefined {
  for (const b of list) if (b.function === opt) return b;
  return undefined;
}

/** All bindings, in KeyBindSettings.New order (the live objects: change them through `setKeyBind`). */
export function keyBinds(): readonly KeyBind[] { return current; }
export function getKeyBind(opt: KeybindOption): KeyBind | undefined { return find(current, opt); }
export function defaultKeyBind(opt: KeybindOption): KeyBind | undefined { return find(DEFAULTS, opt); }

const req = (r: Require, held: boolean) => r === 2 || r === (held ? 1 : 0);

/** One iteration of GameScene.GameScene_KeyDown's loop: the key and every Require* agree with the event. */
export function matches(e: KeyboardEvent, opt: KeybindOption): boolean {
  if (capturing) return false;
  const b = find(current, opt);
  if (!b || b.key === "") return false;
  const name = keyName(e);
  if (b.key !== name) return false;
  // GameScene_KeyDown: Ctrl+Insert alone is skipped (KeyData = Insert | Control)
  if (b.key === "Insert" && e.ctrlKey && !e.altKey && !e.shiftKey) return false;
  return req(b.requireAlt, e.altKey) && req(b.requireShift, e.shiftKey) && req(b.requireCtrl, e.ctrlKey) && req(b.requireTilde, tilde);
}

/** True when the event matches any binding (game.ts uses it to keep Ctrl / Alt game keys away from the browser). */
export function matchesAnyBinding(e: KeyboardEvent): boolean {
  for (const b of current) if (matches(e, b.function)) return true;
  return false;
}

/** `matches` for any of the options. */
export function matchesAny(e: KeyboardEvent, ...opts: KeybindOption[]): boolean {
  for (const o of opts) if (matches(e, o)) return true;
  return false;
}

/** KeyBindSettings.GetKey: "Alt + Ctrl + Shift + ~ + Key", "" when unbound. */
export function keyLabel(opt: KeybindOption, defaultKey = false): string {
  const b = find(defaultKey ? DEFAULTS : current, opt);
  if (!b || b.key === "") return "";
  const parts: string[] = [];
  if (b.requireAlt === 1) parts.push("Alt");
  if (b.requireCtrl === 1) parts.push("Ctrl");
  if (b.requireShift === 1) parts.push("Shift");
  if (b.requireTilde === 1) parts.push("~");
  parts.push(b.key);
  return parts.join(" + ");
}

// — Changes —
const listeners = new Set<() => void>();
/** Called after any binding changes (rebind, clear, reset). Returns an unsubscribe function. */
export function onKeyBindsChange(fn: () => void): () => void {
  listeners.add(fn);
  return () => listeners.delete(fn);
}
function changed(): void {
  save();
  for (const fn of listeners) { try { fn(); } catch (err) { console.error(err); } }
}

/** KeyboardLayoutDialog.CheckNewInput's assignment. `key` "" = Keys.None (all requirements become "don't care"). */
export function setKeyBind(opt: KeybindOption, key: string, alt: Require, shift: Require, ctrl: Require, tildeReq: Require): void {
  const b = find(current, opt);
  if (!b) return;
  if (key === "") { b.key = ""; b.requireAlt = b.requireShift = b.requireCtrl = b.requireTilde = 2; }
  else { b.key = key; b.requireAlt = alt; b.requireShift = shift; b.requireCtrl = ctrl; b.requireTilde = tildeReq; }
  changed();
}

/** KeyboardLayoutDialog.ResetButton: every binding back to KeyBindSettings.New. */
export function resetKeyBinds(): void {
  current.forEach((b, i) => {
    const d = DEFAULTS[i]!;
    b.key = d.key; b.requireAlt = d.requireAlt; b.requireShift = d.requireShift; b.requireTilde = d.requireTilde; b.requireCtrl = d.requireCtrl;
  });
  changed();
}

/** Other bindings that fire on the same key combination as `opt` (the desktop does not check; the dialog shows them). */
export function overlapping(opt: KeybindOption): KeyBind[] {
  const b = find(current, opt);
  if (!b || b.key === "") return [];
  const ok = (x: Require, y: Require) => x === 2 || y === 2 || x === y;
  return current.filter((o) => o !== b && o.key === b.key && ok(o.requireAlt, b.requireAlt) && ok(o.requireShift, b.requireShift)
    && ok(o.requireCtrl, b.requireCtrl) && ok(o.requireTilde, b.requireTilde));
}

// Help: port of HelpDialog (Client/MirScenes/Dialogs/HelpDialog.cs). Toggle with H (KeybindOptions.Help: no Shift, no Ctrl;
// Ctrl+H is the attack mode) or the Help button. Pages 1-3 are the shortcut tables (ShortcutPage1/2/3), pages 4-45 draw
// Help.Lib images 0-41 under a numbered title. Previous / Next wrap around (PreviousButton / NextButton).
// Shortcut keys: the desktop reads them from CMain.InputKeys.GetKey; pass `keyName` to show the current bindings, else
// the desktop defaults (KeyBindSettings.cs) are shown.

import { KeybindOptions, matches } from "../game/keybinds.ts";
import { libIcon } from "./buffBar.ts";
import { gameSignal } from "../game/scope.ts";
import { el } from "./screens.ts";

/** The KeybindOptions names the shortcut pages list, with their default keys formatted like KeyBindSettings.GetKey. */
export const HELP_DEFAULT_KEYS: Readonly<Record<string, string>> = {
  Exit: "Alt + Q", Logout: "Alt + X", Bar1Skill1: "F1", Bar1Skill8: "F8", Inventory: "F9", Equipment: "F10", Skills: "F11", Group: "P",
  Trade: "T", Friends: "F", Minimap: "V", Guilds: "G", GameShop: "Y", Relationship: "L", Belt: "Z", Options: "F12", Help: "H", Mount: "M",
  TargetSpellLockOn: "", ChangePetmode: "Ctrl + A", ChangeAttackmode: "Ctrl + H", AttackmodePeace: "", AttackmodeGroup: "", AttackmodeGuild: "",
  AttackmodeRedbrown: "", AttackmodeAll: "", Bigmap: "B", Skillbar: "R", Autorun: "D", Cameramode: "Insert", Pickup: "Tab",
  Screenshot: "PrintScreen", Fishing: "N", Mentor: "", CreaturePickup: "X", CreatureAutoPickup: "Alt + A",
};

type Shortcut = [key: string, info: string];

interface HelpPage { title: string; image: number; shortcuts?: Shortcut[] }

export class HelpWindow {
  readonly hudButton: HTMLButtonElement;
  private readonly root = el("div", { class: "window help-window", hidden: true });
  private readonly pageTitle = el("div", { class: "help-page-title" });
  private readonly body = el("div", { class: "help-body" });
  private readonly pageLabel = el("span", { class: "help-page-label" });
  private readonly pages: HelpPage[];
  private current = 0;
  private readonly keyName: (option: string) => string;

  constructor(stage: HTMLElement, keyName?: (option: string) => string) {
    this.keyName = keyName ?? ((o) => HELP_DEFAULT_KEYS[o] ?? "");
    this.pages = this.loadPages();
    this.hudButton = el("button", { title: "Help (H)", onclick: () => this.toggle() }, "Help") as HTMLButtonElement;
    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Help"), el("button", { class: "window-close", title: "Close (H)", onclick: () => this.toggle(false) }, "×")),
      this.pageTitle, this.body,
      el("div", { class: "help-nav" },
        el("button", { title: "Previous", onclick: () => this.displayPage(this.current - 1 < 0 ? this.pages.length - 1 : this.current - 1) }, "◀"),
        this.pageLabel,
        el("button", { title: "Next", onclick: () => this.displayPage(this.current + 1 > this.pages.length - 1 ? 0 : this.current + 1) }, "▶")),
    );
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    stage.append(this.root);
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement || a instanceof HTMLSelectElement) return;
      if (matches(e, KeybindOptions.Help)) this.toggle(); // H (Ctrl+H stays ChangeAttackmode: Help requires no Ctrl)
      else if (matches(e, KeybindOptions.Closeall) && !this.root.hidden) this.toggle(false);
    }, { signal: gameSignal() });
  }

  get visible(): boolean {
    return !this.root.hidden;
  }

  /** HelpDialog.Toggle. The page is built on first show. */
  toggle(show = this.root.hidden): void {
    this.root.hidden = !show;
    if (show) this.displayPage(this.current);
  }

  /** HelpDialog.DisplayPage(string): the first page with that title (case-insensitive). */
  displayPageNamed(name: string): void {
    const i = this.pages.findIndex((p) => p.title.toLowerCase() === name.toLowerCase());
    if (i >= 0) this.displayPage(i);
  }

  /** HelpDialog.DisplayPage(int): clamps, shows the page and the dialog. */
  displayPage(id = 0): void {
    if (this.pages.length < 1) return;
    id = Math.max(0, Math.min(this.pages.length - 1, id));
    this.current = id;
    const page = this.pages[id]!;
    this.pageTitle.textContent = `${id + 1}. ${page.title}`;
    this.pageLabel.textContent = `${id + 1} / ${this.pages.length}`;
    this.body.replaceChildren();
    if (page.shortcuts) {
      // ShortcutInfoPage: "Shortcuts" / "Information" headers, yellow keys, white text
      const table = el("table", { class: "help-shortcuts" },
        el("tr", {}, el("th", {}, "Shortcuts"), el("th", {}, "Information")),
        ...page.shortcuts.map(([k, info]) => el("tr", {}, el("td", { class: "help-key" }, k.replace(/\n/g, " + ")), el("td", {}, info))));
      this.body.append(table);
    } else if (page.image >= 0) {
      // HelpPage.HelpPage_BeforeDraw: Libraries.Help image ImageID
      const img = el("img", { class: "help-image", alt: page.title });
      this.body.append(img);
      void libIcon("Help", page.image).then((url) => { if (url && this.current === id) img.src = url; });
    }
    this.root.hidden = false;
  }

  /** HelpDialog.LoadImagePages. */
  private loadPages(): HelpPage[] {
    const k = this.keyName;
    const page1: Shortcut[] = [ // ShortcutPage1
      [k("Exit"), "Exit the game"], [k("Logout"), "Log out"], [`${k("Bar1Skill1")}-${k("Bar1Skill8")}`, "Skill buttons"],
      [k("Inventory"), "Inventory window (open / close)"], [k("Equipment"), "Status window (open / close)"], [k("Skills"), "Skill window (open / close)"],
      [k("Group"), "Group window (open / close)"], [k("Trade"), "Trade window (open / close)"], [k("Friends"), "Friend window (open / close)"],
      [k("Minimap"), "Minimap window (open / close)"], [k("Guilds"), "Guild window (open / close)"], [k("GameShop"), "Gameshop window (open / close)"],
      [k("Relationship"), "Engagement window (open / close)"], [k("Belt"), "Belt window (open / close)"], [k("Options"), "Option window (open / close)"],
      [k("Help"), "Help window (open / close)"], [k("Mount"), "Mount / Dismount ride"], [k("TargetSpellLockOn"), "Lock spell onto target not cursor location"],
    ];
    const page2: Shortcut[] = [ // ShortcutPage2
      [k("ChangePetmode"), "Toggle pet attack pet"], [k("ChangeAttackmode"), "Toggle player attack mode"],
      [k("AttackmodePeace"), "Peace Mode - Attack monsters only"], [k("AttackmodeGroup"), "Group Mode - Attack all subjects except your group members"],
      [k("AttackmodeGuild"), "Guild Mode - Attack all subjects except your guild members"], [k("AttackmodeRedbrown"), "Good/Evil Mode - Attack PK players and monsters only"],
      [k("AttackmodeAll"), "All Attack Mode - Attack all subjects"], [k("Bigmap"), "Show the field map"], [k("Skillbar"), "Show the skill bar"],
      [k("Autorun"), "Auto run on / off"], [k("Cameramode"), "Show / Hide interface"], [k("Pickup"), "Highlight / Pickup Items"],
      ["Ctrl + Right Click", "Show other players kits"], [k("Screenshot"), "Screen Capture"], [k("Fishing"), "Open / Close fishing window"],
      [k("Mentor"), "Mentor window (open / close)"], [k("CreaturePickup"), "Creature Pickup (Multi Mouse Target)"],
      [k("CreatureAutoPickup"), "Creature Pickup (Single Mouse Target)"],
    ];
    const page3: Shortcut[] = [ // ShortcutPage3
      ["/(username)", "Command to whisper to others"], ["!(text)", "Command to shout to others nearby"], ["!~(text)", "Command to guild chat"],
    ];
    // ClientTextKeys titles (Client/Localization/English.json) per Help.Lib image 0-41
    const titles: [string, number][] = [
      ["Movements", 1], ["Attacking", 1], ["Collecting Items", 1], ["Health", 1], ["Skills", 2], ["Mana", 1], ["Chatting", 1], ["Groups", 1],
      ["Durability:", 1], ["Purchasing", 1], ["Selling", 1], ["Repairing", 1], ["Trading", 1], ["Inspecting", 1], ["Statistics", 6], ["Quests", 4],
      ["Mounts", 2], ["Fishing", 1], ["Gems and Orbs", 1], ["Heroes", 5], ["Guild Buffs", 3], ["Awakening", 5],
    ];
    const pages: HelpPage[] = [
      { title: "Shortcut Information", image: -1, shortcuts: page1 },
      { title: "Shortcut Information", image: -1, shortcuts: page2 },
      { title: "Chat Shortcuts", image: -1, shortcuts: page3 },
    ];
    let image = 0;
    for (const [title, count] of titles) for (let i = 0; i < count; i++) pages.push({ title, image: image++ });
    return pages;
  }
}

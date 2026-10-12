// Help: port of HelpDialog (+ HelpPage, ShortcutInfoPage, ShortcutPage1/2/3) of Client/MirScenes/Dialogs/HelpDialog.cs with
// the desktop artwork (Prguse 920, Title 57, Prguse2 240-245 page buttons). Toggle with H (KeybindOptions.Help: no Shift,
// no Ctrl; Ctrl+H is the attack mode) or the Help button. Pages 1-3 are the shortcut tables, pages 4-45 draw Help.Lib
// images 0-41 under a numbered title. Previous / Next wrap around (PreviousButton / NextButton).
// Shortcut keys: the desktop reads them from CMain.InputKeys.GetKey when the dialog is built; pass `keyName` to show the
// current bindings (here read each time a shortcut page is shown), else the desktop defaults (KeyBindSettings.cs).

import { KeybindOptions, matches } from "../game/keybinds.ts";
import { SoundList } from "../game/audio.ts";
import { gameSignal } from "../game/scope.ts";
import { MirWindow, MirControl, MirImage, MirButton, MirLabel, closeButton, Lib, FONT_9, FONT_10 } from "./mir/controls.ts";
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

interface HelpPage { title: string; image: number; shortcuts?: () => Shortcut[] }

export class HelpWindow {
  private readonly win: MirWindow;
  private readonly pageTitle: MirLabel;
  private readonly pageLabel: MirLabel;
  private readonly image: MirImage;
  private readonly shortcutPage: MirControl;
  private readonly pages: HelpPage[];
  private current = 0;
  private readonly keyName: (option: string) => string;

  constructor(_stage: HTMLElement, keyName?: (option: string) => string) {
    this.keyName = keyName ?? ((o) => HELP_DEFAULT_KEYS[o] ?? "");
    this.pages = this.loadPages();

    // HelpDialog ctor
    const win = this.win = new MirWindow({ library: Lib.Prguse, index: 920, movable: true, sort: true, location: "center" });
    new MirImage({ parent: win, library: Lib.Title, index: 57, location: [18, 9] }); // TitleLabel
    new MirButton({ parent: win, library: Lib.Prguse2, index: 240, hoverIndex: 241, pressedIndex: 242, size: [16, 16], location: [210, 485], sound: SoundList.ButtonA,
      onClick: () => this.displayPage(this.current - 1 < 0 ? this.pages.length - 1 : this.current - 1) }); // PreviousButton
    new MirButton({ parent: win, library: Lib.Prguse2, index: 243, hoverIndex: 244, pressedIndex: 245, size: [16, 16], location: [310, 485], sound: SoundList.ButtonA,
      onClick: () => this.displayPage(this.current + 1 > this.pages.length - 1 ? 0 : this.current + 1) }); // NextButton
    this.pageLabel = new MirLabel({ parent: win, notControl: true, location: [230, 480], size: [80, 20], font: FONT_9, center: true });
    closeButton(win, [509, 3], () => this.toggle(false));

    // HelpPage (Location (12, 35)): Help.Lib ImageID drawn at (0, 40) of the page; PageTitleLabel at (135, 4), 242 x 30
    this.image = new MirImage({ parent: win, library: Lib.Help, index: -1, location: [12, 75], notControl: true });
    this.pageTitle = new MirLabel({ parent: win, notControl: true, location: [147, 39], size: [242, 30], font: "bold 10pt Arial", center: true });
    // ShortcutInfoPage (Parent = the dialog, Location (0, 0))
    this.shortcutPage = new MirControl({ parent: win, notControl: true, visible: false });

    gameSignal().addEventListener("abort", () => win.dispose());
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement || a instanceof HTMLSelectElement) return;
      if (matches(e, KeybindOptions.Help)) this.toggle(); // H (Ctrl+H stays ChangeAttackmode: Help requires no Ctrl)
      else if (matches(e, KeybindOptions.Closeall) && this.win.visible) this.toggle(false);
    }, { signal: gameSignal() });
  }

  get visible(): boolean {
    return this.win.visible;
  }

  /** HelpDialog.Toggle. */
  toggle(show = !this.win.visible): void {
    if (show) this.displayPage(this.current);
    else this.win.hide();
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
    this.pageTitle.text = `${id + 1}. ${page.title}`;
    this.pageLabel.text = `${id + 1} / ${this.pages.length}`;
    // HelpPage_BeforeDraw: Libraries.Help.Draw(ImageID) when ImageID >= 0
    this.image.visible = page.image >= 0;
    if (page.image >= 0) this.image.index = page.image;
    this.shortcutPage.visible = !!page.shortcuts;
    if (page.shortcuts) this.buildShortcuts(page.shortcuts());
    this.win.show();
  }

  /** ShortcutInfoPage ctor + LoadKeyBinds: "Shortcuts" / "Information" headers (10pt), yellow keys and white text (9pt). */
  private buildShortcuts(shortcuts: Shortcut[]): void {
    const page = this.shortcutPage;
    page.el.replaceChildren();
    new MirLabel({ parent: page, location: [13, 75], size: [100, 30], font: FONT_10, center: true, text: "Shortcuts" });
    new MirLabel({ parent: page, location: [114, 75], size: [405, 30], font: FONT_10, center: true, text: "Information" });
    shortcuts.forEach(([key, info], i) => {
      new MirLabel({ parent: page, location: [18, 107 + 20 * i], size: [95, 23], font: FONT_9, vCenter: true, foreColour: "#ffff00", text: key.replace(/\n/g, " + ") });
      new MirLabel({ parent: page, location: [119, 107 + 20 * i], size: [400, 23], font: FONT_9, vCenter: true, text: info });
    });
  }

  /** HelpDialog.LoadImagePages. */
  private loadPages(): HelpPage[] {
    const k = this.keyName;
    const page1 = (): Shortcut[] => [ // ShortcutPage1
      [k("Exit"), "Exit the game"], [k("Logout"), "Log out"], [`${k("Bar1Skill1")}-${k("Bar1Skill8")}`, "Skill buttons"],
      [k("Inventory"), "Inventory window (open / close)"], [k("Equipment"), "Status window (open / close)"], [k("Skills"), "Skill window (open / close)"],
      [k("Group"), "Group window (open / close)"], [k("Trade"), "Trade window (open / close)"], [k("Friends"), "Friend window (open / close)"],
      [k("Minimap"), "Minimap window (open / close)"], [k("Guilds"), "Guild window (open / close)"], [k("GameShop"), "Gameshop window (open / close)"],
      [k("Relationship"), "Engagement window (open / close)"], [k("Belt"), "Belt window (open / close)"], [k("Options"), "Option window (open / close)"],
      [k("Help"), "Help window (open / close)"], [k("Mount"), "Mount / Dismount ride"], [k("TargetSpellLockOn"), "Lock spell onto target not cursor location"],
    ];
    const page2 = (): Shortcut[] => [ // ShortcutPage2
      [k("ChangePetmode"), "Toggle pet attack pet"], [k("ChangeAttackmode"), "Toggle player attack mode"],
      [k("AttackmodePeace"), "Peace Mode - Attack monsters only"], [k("AttackmodeGroup"), "Group Mode - Attack all subjects except your group members"],
      [k("AttackmodeGuild"), "Guild Mode - Attack all subjects except your guild members"], [k("AttackmodeRedbrown"), "Good/Evil Mode - Attack PK players and monsters only"],
      [k("AttackmodeAll"), "All Attack Mode - Attack all subjects"], [k("Bigmap"), "Show the field map"], [k("Skillbar"), "Show the skill bar"],
      [k("Autorun"), "Auto run on / off"], [k("Cameramode"), "Show / Hide interface"], [k("Pickup"), "Highlight / Pickup Items"],
      ["Ctrl + Right Click", "Show other players kits"], [k("Screenshot"), "Screen Capture"], [k("Fishing"), "Open / Close fishing window"],
      [k("Mentor"), "Mentor window (open / close)"], [k("CreaturePickup"), "Creature Pickup (Multi Mouse Target)"],
      [k("CreatureAutoPickup"), "Creature Pickup (Single Mouse Target)"],
    ];
    const page3 = (): Shortcut[] => [ // ShortcutPage3
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

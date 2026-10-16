// Guild: GuildDialog + GuildBuffButton (Client/MirScenes/Dialogs/GuildDialog.cs) with the desktop artwork (Prguse 180,
// Title 25, the page bases Prguse 1850-1853, GuildSkill icons), a view over the guild model (src/game/guild.ts: the
// GuildDialog state + GameScene's guild handlers; the storage items are in Inventory). The window answers the model's
// prompts (GuildInvite, GuildNameRequest, GuildRequestWar) and redraws on its events. Toggle with G (or the Guild
// button), Esc closes.
// Left pages (LeftDialog): notice (view / edit, scroll buttons), members (18 rows: rank drop-down, name, last login,
// kick; show offline), storage (8 x 14 cells, 8 rows shown, gold deposit / leader-only retrieve), ranks (rename, Add
// New, the 8 option bits). Right pages (RightDialog): status (name, level, members, recruit) and buffs (8
// GuildBuffButtons, buy / activate, the hover label). The guild experience bar sits under both.
// The war itself shows only as server name colours. Crystal has no alliances (CanAlterAlliance is an unused rank bit)
// and guild voting is stored but never used by either side.
// Deliberate differences: StatusChanged's NoticeEditButton.Index = 85 is not copied (the desktop shows a stray Prguse
// picture until the next notice arrives); the storage scroll starts at row 0 (the desktop's StorageIndex starts at 1 but
// draws row 0); a rank above your own shows its own option ticks (the desktop keeps the previous rank's); a
// GuildMemberChange status 7 for the user's own rank updates their rights even when the member list was never loaded;
// GuildNameRequest drops characters other than letters and digits on OK (the desktop blocks them while typing).

import { StatName, ChatType } from "../protocol/enums.generated.ts";
import { ClientPackets } from "../protocol/client.ts";
import { placeCell, type InventoryWindow } from "./inventoryWindow.ts";
import { GUILD_STORAGE_SLOTS } from "../game/inventory.ts";
import { GuildRankOptions as Opt, type GuildState, type GuildView } from "../game/guild.ts";
import { dateFromBinary } from "../game/itemChanges.ts";
import { gameSignal } from "../game/scope.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { SoundList } from "../game/audio.ts";
import { MirWindow, MirImage, MirButton, MirLabel, MirControl, MirTextBox, Lib, closeButton, setPicture, getUiRoot, SCREEN_WIDTH, SCREEN_HEIGHT } from "./mir/controls.ts";
import { messageBox, inputBox, amountBox } from "./messageBox.ts";
import { dragY } from "./mir/controls.ts";

/** GuildDialog ctor: the rank option labels, bit i = 1 << i. */
const OPTION_NAMES = ["Edit ranks", "Recruit member", "Kick member", "Store item", "Retrieve item", "Alter alliance", "Change notice", "Activate Buff"];
/** GuildDialog.MemberPageRows. */
const MEMBER_ROWS = 18;
/** Visible notice lines (the desktop's 25 / 24 constants). */
const NOTICE_LINES = 25;
const NOTICE_LINE_HEIGHT = 13;
const FONT_7 = "7pt Arial";

/** Client mouse position -> 1024 x 768 stage pixels. */
function toStage(clientX: number, clientY: number): [number, number] {
  const root = getUiRoot();
  const r = root.getBoundingClientRect();
  const s = r.width / (root.offsetWidth || 1) || 1;
  return [(clientX - r.left) / s, (clientY - r.top) / s];
}

/** WinForms MouseWheel count (+1 = wheel up). */
function wheelCount(e: WheelEvent): number {
  return e.deltaY < 0 ? 1 : e.deltaY > 0 ? -1 : 0;
}

/** Client/MirControls/MirDropDownBox.cs: a label with the selected item, a drop button (Prguse2 207) and up to 5 option
 *  rows starting at ScrollIndex + MinimumOption; ValueChanged reports the wanted index (the owner decides). */
class DropDownBox {
  readonly control: MirControl;
  private readonly label: MirLabel;
  private readonly dropButton: MirButton;
  private readonly options: MirLabel[] = [];
  private readonly scrollUp: MirButton;
  private readonly scrollDown: MirButton;
  private readonly scrollPosition: MirButton;
  private readonly width: number;
  private readonly origHeight: number;
  private _items: string[] = [];
  private _selectedIndex = -1;
  private _enabled = false;
  private isOpen = false;
  minimumOption = 0;
  scrollIndex = 0;
  onValueChanged: ((wantedIndex: number) => void) | null = null;

  constructor(parent: MirControl, location: [number, number], size: [number, number], backColour = "rgb(6,6,6)") {
    this.width = size[0];
    this.origHeight = size[1];
    this.control = new MirControl({ parent, location, size, backColour });
    this.label = new MirLabel({ parent: this.control, location: [0, 0], size: [this.width - 16, 15], backColour, onClick: () => { if (this._enabled) this.dropDownClick(); } });
    for (let i = 0; i < 5; i++) {
      const option = new MirLabel({ parent: this.control, location: [0, 15 + i * 13], size: [this.width - 16, 16], backColour: "rgb(20,20,20)", visible: false, onClick: () => this.selectOption(i) });
      option.el.addEventListener("pointerenter", () => { option.el.style.backgroundColor = "rgb(140,70,0)"; });
      option.el.addEventListener("pointerleave", () => { option.el.style.backgroundColor = "rgb(20,20,20)"; });
      option.el.addEventListener("pointerdown", () => { option.el.style.backgroundColor = "rgb(20,20,20)"; });
      this.options.push(option);
    }
    this.dropButton = new MirButton({ parent: this.control, library: Lib.Prguse2, index: 207, hoverIndex: 208, pressedIndex: 209, location: [this.width - 18, 2], visible: false, onClick: () => this.dropDownClick() });
    this.scrollUp = new MirButton({ parent: this.control, library: Lib.Prguse, index: 2021, hoverIndex: 2022, pressedIndex: 2023, location: [this.width - 12, 14], visible: false,
      onClick: () => { if (this.scrollIndex > 0) this.scrollIndex--; this.refresh(); } });
    this.scrollDown = new MirButton({ parent: this.control, library: Lib.Prguse, index: 2024, hoverIndex: 2025, pressedIndex: 2026, location: [this.width - 12, 52], visible: false,
      onClick: () => { if (this.scrollIndex < this._items.length - 5) this.scrollIndex++; this.refresh(); } });
    this.scrollPosition = new MirButton({ parent: this.control, library: Lib.Prguse, index: 2015, hoverIndex: 2016, pressedIndex: 2017, location: [this.width - 11, 22], visible: false });
    dragY(this.scrollPosition, (y) => this.onScrollMoving(y));
    // OnMouseLeave / BeforeDraw: close once the mouse is off the box and its options
    this.control.el.addEventListener("pointerleave", () => this.closeDropDown());
    this.refresh();
  }

  get items(): string[] { return this._items; }
  /** Items setter -> ItemsChanged: closes and clears the selection. */
  set items(v: string[]) { this._items = v; this.closeDropDown(); this.selectedIndex = -1; }

  get selectedIndex(): number { return this._selectedIndex; }
  set selectedIndex(v: number) { this._selectedIndex = v >= this._items.length ? -1 : v; this.refresh(); }

  set visible(v: boolean) { this.control.visible = v; if (!v) this.closeDropDown(); }

  get enabled(): boolean { return this._enabled; }
  /** OnEnabledChanged: the drop button shows only while enabled. */
  set enabled(v: boolean) {
    this._enabled = v;
    this.dropButton.enabled = v;
    this.dropButton.visible = v;
    if (!v) this.closeDropDown();
  }

  private dropDownClick(): void {
    if (this.isOpen) this.closeDropDown(); else this.openDropDown();
  }

  private selectOption(index: number): void {
    const wanted = this.scrollIndex + index + this.minimumOption;
    this.closeDropDown();
    if (wanted < this._items.length) this.onValueChanged?.(wanted);
  }

  private openDropDown(): void {
    this.isOpen = true;
    const n = this._items.length;
    this.control.size = [this.width, this.origHeight + Math.max(5, n) * 15];
    this.control.el.style.zIndex = "5";
    this.scrollIndex = 0;
    if (n > 5) this.scrollIndex = this._selectedIndex > 3 ? this._selectedIndex - 2 : 0;
    for (let i = 0; i < this.options.length; i++) this.options[i]!.visible = n > i;
    this.scrollUp.visible = this.scrollDown.visible = this.scrollPosition.visible = n > 5;
    this.scrollDown.location = [this.width - 12, 9 + Math.min(5, n) * 13];
    this.refresh();
  }

  private closeDropDown(): void {
    if (!this.isOpen) return;
    this.isOpen = false;
    this.control.size = [this.width, this.origHeight];
    this.control.el.style.zIndex = "";
    for (const o of this.options) o.visible = false;
    this.scrollUp.visible = this.scrollDown.visible = this.scrollPosition.visible = false;
  }

  /** ScrollPosition (the dragged bar). */
  private onScrollMoving(y: number): void {
    const x = this.width - 11;
    const downY = this.scrollDown.location[1];
    if (y >= downY - 14) y = downY - 14;
    if (y < 20) y = 20;
    let h = downY - this.scrollUp.location[1] + 6;
    h = Math.trunc((y - 15) / (h / (this._items.length - 1)));
    if (h > this._items.length - 5) h = Math.max(0, this._items.length - 5);
    if (h !== this.scrollIndex) { this.scrollIndex = h; this.refresh(); }
    this.scrollPosition.location = [x, y];
  }

  /** _label.BeforeDraw + _Option[i].BeforeDraw. */
  private refresh(): void {
    const n = this._items.length;
    if (n > 0 && n >= this._selectedIndex) this.label.text = this._selectedIndex === -1 ? " " : this._items[this._selectedIndex] ?? " ";
    else this.label.text = "None";
    for (let i = 0; i < this.options.length; i++) {
      const at = this.scrollIndex + i + this.minimumOption;
      if (at < n) this.options[i]!.text = this._items[at]!;
    }
  }
}

/** GuildBuffButton: 188 x 33, GuildSkill icon, name / info / obtained labels, orange border while hovered. */
class GuildBuffButton {
  readonly control: MirControl;
  readonly icon: MirImage;
  readonly name: MirLabel;
  readonly info: MirLabel;
  readonly obtained: MirLabel;

  constructor(parent: MirControl, location: [number, number], onClick: () => void, onEnter: (e: PointerEvent) => void, onLeave: () => void) {
    this.control = new MirControl({ parent, location, size: [188, 33], onClick });
    this.icon = new MirImage({ parent: this.control, library: Lib.GuildSkill, index: 0, location: [1, 0], notControl: true });
    this.name = new MirLabel({ parent: this.control, location: [35, 1], autoSize: true, notControl: true });
    this.info = new MirLabel({ parent: this.control, location: [35, 17], autoSize: true, notControl: true });
    this.obtained = new MirLabel({ parent: this.control, location: [140, 17], autoSize: true, notControl: true });
    // OnMouseEnter / OnMouseLeave: Border + GuildDialog.CreateHintLabel / GameScene.DisposeGuildBuffLabel
    this.control.el.addEventListener("pointerenter", (e) => { this.control.el.style.outline = "1px solid #ffa500"; onEnter(e); });
    this.control.el.addEventListener("pointerleave", () => { this.control.el.style.outline = ""; onLeave(); });
  }
}

export class Guild {
  /** The guild state (src/game/guild.ts: name, rank, level, funds, ranks and members, buffs, notice). */
  private readonly m: GuildState;
  private lastBuffRequest = 0;
  /** GuildDialog.LastGuildMsg: one recruit / kick / rank change per 5 s. */
  private lastGuildMsg = 0;
  private lastRankNameChange = Number.MAX_SAFE_INTEGER;
  private editing = false;
  private rankNameTimer: ReturnType<typeof setTimeout> | 0 = 0;
  private readonly stage: HTMLElement;
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly me: () => string;
  private readonly now: () => number;
  private readonly userGold: () => number;

  // — GuildDialog controls —
  private readonly win: MirWindow;
  private leftPage = 0;
  private rightPage = 0;
  private readonly noticeButton: MirButton;
  private readonly membersButton: MirButton;
  private readonly storageButton: MirButton;
  private readonly rankButton: MirButton;
  private readonly statusButton: MirButton;
  private readonly buffButton: MirButton;
  // notice page
  private readonly noticePage: MirControl;
  private readonly noticeBox: MirTextBox;
  private readonly noticeEditButton: MirButton;
  private readonly noticeSaveButton: MirButton;
  private readonly noticePositionBar: MirButton;
  private noticeScrollIndex = 0;
  // members page
  private readonly membersPage: MirControl;
  private readonly membersRanks: DropDownBox[] = [];
  private readonly membersName: MirLabel[] = [];
  private readonly membersStatus: MirLabel[] = [];
  private readonly membersDelete: MirButton[] = [];
  private readonly membersPositionBar: MirButton;
  private readonly membersShowOfflineStatus: MirImage;
  private memberScrollIndex = 0;
  private membersShowCount = 1;
  private showOffline = true;
  // status page + experience bar
  private readonly statusPage: MirControl;
  private readonly statusGuildName: MirLabel;
  private readonly statusLevel: MirLabel;
  private readonly statusMembers: MirLabel;
  private readonly expBack: HTMLDivElement;
  private readonly expClip: HTMLDivElement;
  private readonly expLabel: MirLabel;
  private readonly recruitName: MirTextBox;
  private readonly recruitButton: MirButton;
  private readonly recruitLabel: MirLabel;
  // storage page
  private readonly storagePage: MirControl;
  private readonly storageGoldText: MirLabel;
  private readonly storageGoldRemove: MirButton;
  private readonly storageCells: HTMLElement[] = [];
  private readonly storagePositionBar: MirButton;
  private storageIndex = 0;
  // rank page
  private readonly rankPage: MirControl;
  private readonly ranksSelectBox: DropDownBox;
  private readonly ranksName: MirTextBox;
  private readonly ranksSaveName: MirButton;
  private readonly ranksOptionsButtons: MirButton[] = [];
  private readonly ranksOptionsStatus: MirImage[] = [];
  // buff page
  private readonly buffPage: MirImage;
  private readonly buffs: GuildBuffButton[] = [];
  private readonly pointsLeft: MirLabel;
  private readonly buffPositionBar: MirButton;
  private startIndex = 0;
  private buffLabel: HTMLDivElement | null = null;

  constructor(stage: HTMLElement, guild: GuildState, send: (f: Uint8Array) => void, chat: (text: string, type: number) => void, me: () => string,
    now: () => number, _guildChat: () => void, invWindow: InventoryWindow, userGold: () => number) {
    this.stage = stage;
    this.m = guild;
    this.send = send;
    this.chat = chat;
    this.me = me;
    this.now = now;
    this.userGold = userGold;
    invWindow.guildOptions = () => guild.myOptions;
    invWindow.deny = (text) => this.chat(text, ChatType.System);

    // — GuildDialog ctor: TabUI —
    const win = this.win = new MirWindow({ library: Lib.Prguse, index: 180, movable: true, sort: true, location: "center" });
    new MirImage({ parent: win, library: Lib.Title, index: 25, location: [18, 9] });
    const tab = (index: number, pressed: number, x: number, onClick: () => void, visible = true) =>
      new MirButton({ parent: win, library: Lib.Title, index, pressedIndex: pressed, location: [x, 38], sound: SoundList.ButtonA, visible, onClick });
    this.noticeButton = tab(93, 94, 20, () => this.showLeft(0));
    this.membersButton = tab(99, 100, 91, () => this.showLeft(1));
    this.storageButton = tab(105, 106, 162, () => this.showLeft(2), false);
    this.rankButton = tab(101, -1, 233, () => this.showLeft(3), false);
    this.statusButton = tab(103, -1, 501, () => this.showRight(0));
    this.buffButton = tab(95, -1, 430, () => this.showRight(1), false);
    closeButton(win, [565, 4], () => this.toggle(false));

    // — NoticePageUI —
    this.noticePage = new MirControl({ parent: win, size: [352, 372], location: [0, 60] });
    this.noticeBox = new MirTextBox({ parent: this.noticePage, size: [322, 330], location: [13, 1], multiline: true, readOnly: true, foreColour: "#fff", font: "8pt Arial" });
    this.noticeBox.input.style.overflow = "hidden";
    this.noticeBox.input.style.lineHeight = NOTICE_LINE_HEIGHT + "px";
    this.noticeEditButton = new MirButton({ parent: this.noticePage, library: Lib.Prguse, index: 560, hoverIndex: 561, pressedIndex: 562, location: [20, 342], sound: SoundList.ButtonA, visible: false, onClick: () => this.editNotice() });
    this.noticeSaveButton = new MirButton({ parent: this.noticePage, library: Lib.Prguse, index: 554, hoverIndex: 555, pressedIndex: 556, location: [20, 342], sound: SoundList.ButtonA, visible: false, onClick: () => this.editNotice() });
    this.scrollButtons(this.noticePage, () => this.noticeScroll(-1), () => this.noticeScroll(1));
    this.noticePositionBar = new MirButton({ parent: this.noticePage, library: Lib.Prguse2, index: 206, location: [337, 16], sound: 0 });
    dragY(this.noticePositionBar, (y) => this.noticePositionMoving(y));
    this.noticePage.el.addEventListener("wheel", (e) => { const c = wheelCount(e); if (c) this.noticeScroll(-c); });

    // — MembersPageUI —
    this.membersPage = new MirControl({ parent: win, size: [352, 372], location: [0, 60], visible: false });
    new MirImage({ parent: this.membersPage, library: Lib.Prguse, index: 1852, location: [13, 1] });
    for (let i = MEMBER_ROWS - 1; i >= 0; i--) {
      const box = new DropDownBox(this.membersPage, [24, 30 + i * 15], [100, 14], i % 2 === 0 ? "rgb(10,10,10)" : "rgb(15,15,15)");
      box.visible = false;
      box.onValueChanged = (wanted) => this.onNewRank(i, wanted);
      this.membersRanks[i] = box;
    }
    for (let i = 0; i < MEMBER_ROWS; i++) {
      this.membersName.push(new MirLabel({ parent: this.membersPage, size: [100, 14], location: [125, 30 + i * 15], visible: false, font: FONT_7,
        backColour: i % 2 === 0 ? "rgb(5,5,5)" : "rgb(7,7,7)" }));
    }
    for (let i = 0; i < MEMBER_ROWS; i++) {
      this.membersStatus.push(new MirLabel({ parent: this.membersPage, size: [100, 14], location: [225, 30 + i * 15], visible: false, font: FONT_7,
        backColour: i % 2 === 0 ? "rgb(10,10,10)" : "rgb(15,15,15)" }));
    }
    for (let i = 0; i < MEMBER_ROWS; i++) {
      this.membersDelete.push(new MirButton({ parent: this.membersPage, library: Lib.Prguse, index: 917, location: [210, 30 + i * 15], onClick: () => this.deleteMember(i) }));
    }
    this.scrollButtons(this.membersPage, () => this.membersScroll(-1), () => this.membersScroll(1));
    this.membersPositionBar = new MirButton({ parent: this.membersPage, library: Lib.Prguse2, index: 206, location: [337, 16], sound: 0 });
    dragY(this.membersPositionBar, (y) => this.membersPositionMoving(y));
    new MirButton({ parent: this.membersPage, library: Lib.Prguse, index: 1346, location: [230, 310], sound: SoundList.ButtonA, onClick: () => this.membersShowOfflineSwitch() });
    this.membersShowOfflineStatus = new MirImage({ parent: this.membersPage, library: Lib.Prguse, index: 1347, location: [230, 310], onClick: () => this.membersShowOfflineSwitch() });
    new MirLabel({ parent: this.membersPage, location: [245, 309], size: [150, 12], font: FONT_7, text: "Show Offline" });
    this.membersPage.el.addEventListener("wheel", (e) => { const c = wheelCount(e); if (c) this.membersScroll(-c); });

    // — StatusDialogUI —
    this.statusPage = new MirControl({ parent: win, size: [230, 372], location: [355, 60] });
    new MirImage({ parent: this.statusPage, library: Lib.Prguse, index: 1850, location: [10, 2] });
    new MirLabel({ parent: this.statusPage, location: [7, 47], size: [75, 300], align: "right", notControl: true, foreColour: "#808080", text: "Guild Name\n\nLevel\n\nMembers" });
    this.statusGuildName = new MirLabel({ parent: this.statusPage, location: [82, 47], size: [120, 200], notControl: true });
    this.statusLevel = new MirLabel({ parent: this.statusPage, location: [82, 73], size: [120, 200], notControl: true });
    this.statusMembers = new MirLabel({ parent: this.statusPage, location: [82, 99], size: [120, 200], notControl: true });
    // StatusExpBar (DrawImage = false; BeforeDraw draws Prguse2 424 scaled to 260 x 22, then 423 cut to the percentage)
    const expBar = new MirControl({ parent: win, location: [322, 403], size: [260, 15], notControl: true });
    this.expBack = document.createElement("div");
    this.expBack.className = "mir-pic";
    this.expClip = document.createElement("div");
    this.expClip.style.cssText = "position:absolute;left:0;top:0;height:22px;overflow:hidden;";
    const expFill = document.createElement("div");
    expFill.className = "mir-pic";
    this.expClip.append(expFill);
    expBar.el.append(this.expBack, this.expClip);
    void setPicture(this.expBack, Lib.Prguse2, 424).then(() => { this.expBack.style.width = "260px"; this.expBack.style.height = "22px"; this.expBack.style.backgroundSize = "260px 22px"; });
    void setPicture(expFill, Lib.Prguse2, 423);
    this.expLabel = new MirLabel({ parent: win, location: [322, 405], size: [260, 15], center: true, notControl: true });
    this.recruitName = new MirTextBox({ parent: this.statusPage, location: [40, 300], size: [130, 21], maxLength: 20, backColour: "rgb(25,25,25)", border: true, borderColour: "rgb(35,35,35)",
      onEnter: () => this.addMember() });
    this.recruitButton = new MirButton({ parent: this.statusPage, library: Lib.Title, index: 356, hoverIndex: 357, pressedIndex: 358, location: [170, 298], onClick: () => this.addMember() });
    this.recruitLabel = new MirLabel({ parent: this.statusPage, location: [36, 283], size: [150, 15], notControl: true, text: "Recruit Member" });

    // — StorageDialogUI —
    this.storagePage = new MirControl({ parent: win, size: [352, 372], location: [0, 60], visible: false });
    new MirImage({ parent: this.storagePage, library: Lib.Prguse, index: 1851, location: [30, 19] });
    this.storageGoldText = new MirLabel({ parent: this.storagePage, size: [125, 12], location: [194, 312], notControl: true, text: "0" });
    new MirButton({ parent: this.storagePage, library: Lib.Prguse, index: 918, location: [158, 313], onClick: () => this.storageAddGold() });
    this.storageGoldRemove = new MirButton({ parent: this.storagePage, library: Lib.Prguse, index: 917, location: [142, 313], onClick: () => this.storageRemoveGold() });
    for (let i = 0; i < GUILD_STORAGE_SLOTS; i++) this.storageCells.push(placeCell(invWindow.boundCell("guild", i, [35, 35]), this.storagePage, [0, 0]));
    this.updateStorage();
    this.scrollButtons(this.storagePage, () => this.storageScroll(-1), () => this.storageScroll(1));
    this.storagePositionBar = new MirButton({ parent: this.storagePage, library: Lib.Prguse2, index: 206, location: [337, 16], sound: 0 });
    dragY(this.storagePositionBar, (y) => this.storagePositionMoving(y));
    this.storagePage.el.addEventListener("wheel", (e) => { const c = wheelCount(e); if (c) this.storageScroll(-c); });

    // — RankDialogUI —
    this.rankPage = new MirControl({ parent: win, size: [352, 372], location: [0, 60], visible: false });
    new MirLabel({ parent: this.rankPage, location: [42, 18], size: [150, 20], notControl: true, text: "Edit Rank" });
    new MirLabel({ parent: this.rankPage, location: [198, 18], size: [150, 20], notControl: true, text: "Select Rank" });
    this.ranksSelectBox = new DropDownBox(this.rankPage, [198, 36], [130, 16], "rgb(25,25,25)");
    this.ranksSelectBox.enabled = true;
    this.ranksSelectBox.onValueChanged = (wanted) => this.onRankSelect(wanted);
    this.ranksName = new MirTextBox({ parent: this.rankPage, location: [42, 36], size: [130, 16], maxLength: 20, backColour: "rgb(25,25,25)", border: true, borderColour: "rgb(35,35,35)",
      onEnter: () => this.ranksChangeName(), onEscape: () => this.updateRanks() });
    this.ranksName.input.disabled = true;
    this.ranksName.input.addEventListener("keydown", (e) => { if ((e as KeyboardEvent).key === "\\") e.preventDefault(); }); // RanksName_KeyPress
    this.ranksSaveName = new MirButton({ parent: this.rankPage, library: Lib.Title, index: 90, hoverIndex: 91, pressedIndex: 92, location: [155, 290], sound: SoundList.ButtonA, enabled: false,
      onClick: () => this.ranksChangeName() });
    for (let i = 0; i < OPTION_NAMES.length; i++) {
      const loc: [number, number] = [i % 2 === 0 ? 42 : 202, i % 2 === 0 ? 120 + i * 20 : 120 + (i - 1) * 20];
      this.ranksOptionsButtons.push(new MirButton({ parent: this.rankPage, library: Lib.Prguse, index: 1346, location: loc, sound: SoundList.ButtonA, enabled: false, onClick: () => this.switchRankOption(i) }));
    }
    for (let i = 0; i < OPTION_NAMES.length; i++) {
      const loc: [number, number] = [i % 2 === 0 ? 42 : 202, i % 2 === 0 ? 120 + i * 20 : 120 + (i - 1) * 20];
      this.ranksOptionsStatus.push(new MirImage({ parent: this.rankPage, library: Lib.Prguse, index: 1347, location: loc, notControl: true, visible: false }));
    }
    for (let i = 0; i < OPTION_NAMES.length; i++) {
      new MirLabel({ parent: this.rankPage, notControl: true, autoSize: true, text: OPTION_NAMES[i],
        location: [17 + (i % 2 === 0 ? 42 : 202), i % 2 === 0 ? 118 + i * 20 : 118 + (i - 1) * 20] });
    }

    // — BuffDialogUI —
    this.buffPage = new MirImage({ parent: win, library: Lib.Prguse, index: 1853, location: [360, 61], visible: false });
    for (let i = 0; i < 8; i++) {
      this.buffs.push(new GuildBuffButton(this.buffPage, [4, 27 + i * 38], () => this.requestBuff(i), (e) => this.createHintLabel(i, e), () => this.disposeHintLabel()));
    }
    this.pointsLeft = new MirLabel({ parent: this.buffPage, location: [118, 3], size: [100, 20], align: "center", notControl: true });
    new MirButton({ parent: this.buffPage, library: Lib.Prguse2, index: 197, hoverIndex: 198, pressedIndex: 199, location: [203, 24], sound: SoundList.ButtonA,
      onClick: () => { if (this.startIndex === 0) return; this.startIndex--; this.updatePositionBar(); this.refresh(); } });
    new MirButton({ parent: this.buffPage, library: Lib.Prguse2, index: 207, hoverIndex: 208, pressedIndex: 209, location: [203, 317], sound: SoundList.ButtonA,
      onClick: () => {
        if (this.m.buffInfos.length < 8 || this.startIndex === this.m.buffInfos.length - 8) return;
        this.startIndex++; this.updatePositionBar(); this.refresh();
      } });
    this.buffPositionBar = new MirButton({ parent: this.buffPage, library: Lib.Prguse2, index: 205, hoverIndex: 206, pressedIndex: 206, location: [203, 39], sound: 0 });
    dragY(this.buffPositionBar, (y) => this.buffPositionMoving(y));
    this.buffPage.el.addEventListener("wheel", (e) => this.buffsWheel(e));

    // GameScene key handling: G toggles (Ctrl+G is not this binding), Esc (Closeall) hides
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      if (matches(e, KeybindOptions.Guilds)) this.toggle();
      else if (matches(e, KeybindOptions.Closeall) && this.win.visible) this.toggle(false);
    }, { signal: gameSignal() });
    win.onHide = () => this.disposeHintLabel();
    // The model's events (GameScene's guild handlers): the prompts, the parts to redraw, then (last, after every guild
    // packet) RequestUpdateMembers + the redraw
    const offs = [
      guild.view.on((v) => this.onView(v)),
      guild.changed.on(() => { this.requestUpdates(); this.refresh(); }),
      guild.invite.on(({ name }) => {
        // GameScene.GuildInvite (MirMessageBox YesNo)
        messageBox(this.stage, `Do you want to join the ${name} guild?`, [{ label: "Yes", onClick: () => guild.answerInvite(true) }, { label: "No", onClick: () => guild.answerInvite(false) }]);
      }),
      guild.nameRequest.on(() => {
        // GameScene.GuildNameRequest (MirInputBox; letters and digits only, no backslash: guild.ts returnName)
        inputBox(this.stage, "Please enter a guild name, length must be 3~20 characters.", "", (name) => guild.returnName(name));
      }),
      guild.warRequest.on(() => {
        // GameScene.GuildRequestWar (MirInputBox)
        inputBox(this.stage, "Please enter the guild you would like to go to war with.", "", (name) => guild.returnWar(name));
      }),
    ];
    gameSignal().addEventListener("abort", () => {
      for (const off of offs) off();
      this.disposeHintLabel();
      if (this.rankNameTimer) clearTimeout(this.rankNameTimer);
      win.dispose();
    });
    this.refresh();
  }

  /** The Prguse2 197 / 207 up / down buttons every scrolling page has at (337, 1) / (337, 318). */
  private scrollButtons(parent: MirControl, up: () => void, down: () => void): void {
    new MirButton({ parent, library: Lib.Prguse2, index: 197, hoverIndex: 198, pressedIndex: 199, location: [337, 1], size: [16, 14], sound: SoundList.ButtonA, onClick: up });
    new MirButton({ parent, library: Lib.Prguse2, index: 207, hoverIndex: 208, pressedIndex: 209, location: [337, 318], size: [16, 14], sound: SoundList.ButtonA, onClick: down });
  }

  /** GuildDialog.Show (a MirMessageBox when not in a guild) / Hide. */
  toggle(show = !this.win.visible): void {
    if (!show) { this.win.hide(); return; }
    if (this.win.visible) { this.win.show(); return; }
    if (!this.m.name) { messageBox(this.stage, "You are not in a guild.", [{ label: "OK" }]); return; }
    this.win.show();
    if (this.leftPage === 0) this.requestUpdateNotice();
    this.requestUpdates();
    this.refresh();
  }

  // — LeftDialog / RightDialog —

  /** GuildDialog.LeftDialog. */
  private showLeft(page: number): void {
    this.leftPage = page;
    if (page === 0) this.requestUpdateNotice();
    if (page === 2) this.send(ClientPackets.GuildStorageItemChange({ Type: 3, From: 0, To: 0 }));
    this.requestUpdates();
    this.refresh();
  }

  /** GuildDialog.RightDialog. */
  private showRight(page: number): void {
    this.rightPage = page;
    this.refresh();
  }

  // — RequestUpdateNotice / RequestUpdateMembers —

  /** GuildDialog.RequestUpdateNotice (5 s throttle, guild.ts). */
  private requestUpdateNotice(): void {
    this.m.requestNotice();
  }

  /** MembersPage / RankPage BeforeDraw -> RequestUpdateMembers: asks while the page is shown (5 s throttle, guild.ts); a
   *  change inside the throttle window asks again when it ends. */
  private requestUpdates(): void {
    this.m.requestMembers(() => this.win.visible && (this.leftPage === 1 || this.leftPage === 3));
  }

  // — Notice page —

  /** GuildDialog.EditNotice: edit, then save sends C.EditGuildNotice with the lines. */
  private editNotice(): void {
    if (!this.editing) {
      this.editing = true;
      this.noticeBox.input.readOnly = false;
      this.noticeBox.focus();
    } else {
      this.editing = false;
      this.noticeBox.input.readOnly = true;
      this.send(ClientPackets.EditGuildNotice({ notice: this.noticeBox.text.split(/\r?\n/) }));
    }
    this.refresh();
  }

  /** GuildDialog.NoticeChange (the model holds the new lines). */
  private noticeChange(): void {
    this.editing = false;
    this.noticeBox.input.readOnly = true;
    this.noticeScrollIndex = 0;
    this.noticeBox.text = this.m.notice.join("\n");
    this.updateNotice();
  }

  private noticeLines(): number {
    return this.noticeBox.text.split(/\r?\n/).length;
  }

  /** NoticeUpButton / NoticeDownButton / NoticePanel_MouseWheel: the first shown line (UpdateNotice). */
  private noticeScroll(delta: number): void {
    const max = Math.max(0, this.noticeLines() - NOTICE_LINES);
    const next = Math.min(max, Math.max(0, this.noticeScrollIndex + delta));
    if (next === this.noticeScrollIndex) return;
    this.noticeScrollIndex = next;
    this.updateNotice();
  }

  /** UpdateNotice (scroll the box to the line) + UpdateNoticeScrollPosition. */
  private updateNotice(): void {
    this.noticeBox.input.scrollTop = this.noticeScrollIndex * NOTICE_LINE_HEIGHT;
    const span = this.noticeLines() - NOTICE_LINES;
    let y = span > 0 ? 16 + this.noticeScrollIndex * Math.trunc(289 / span) : 16;
    if (y >= 318 - 20) y = 318 - 20;
    if (y <= 16) y = 16;
    this.noticePositionBar.location = [337, y];
  }

  /** NoticePositionBar_OnMoving. */
  private noticePositionMoving(y: number): void {
    if (y < 16) y = 16;
    const span = this.noticeLines() - NOTICE_LINES;
    if (span <= 0) { this.noticeScrollIndex = 0; this.updateNotice(); return; }
    const interval = Math.max(1, Math.trunc(289 / span));
    this.noticeScrollIndex = Math.min(span, Math.max(0, Math.floor((y - 16) / interval)));
    this.updateNotice();
  }

  // — Members page —

  /** GuildDialog.RefreshMemberList: the drop-down items (guild.ts already set Ranks[i].Index = i). */
  private refreshMemberList(): void {
    this.memberScrollIndex = 0;
    const names = this.m.ranks.map((r) => r.Name);
    for (const box of this.membersRanks) { box.items = names; box.minimumOption = this.m.myRankId; }
    this.ranksSelectBox.items = this.m.ranks.length < 255 ? [...names, "Add New"] : [...names];
    this.ranksSelectBox.minimumOption = 0;
    this.updateMembers();
    this.updateRanks();
  }

  /** GuildDialog.UpdateMembers. */
  private updateMembers(): void {
    this.membersShowCount = 0;
    for (const r of this.m.ranks) for (const m of r.Members) if (this.showOffline || m.Online) this.membersShowCount++;
    if (this.membersShowCount < MEMBER_ROWS) this.memberScrollIndex = 0;
    for (let i = 0; i < MEMBER_ROWS; i++) {
      const shown = this.membersShowCount > i;
      this.membersRanks[i]!.visible = shown;
      this.membersName[i]!.visible = shown;
      this.membersStatus[i]!.visible = shown;
      this.membersDelete[i]!.visible = false;
    }
    const recruit = this.can(Opt.CanRecruit);
    this.recruitButton.visible = this.recruitName.visible = this.recruitLabel.visible = recruit;

    const me = this.me();
    const now = Date.now();
    let offset = 0, row = 0;
    for (const rank of this.m.ranks) {
      for (const m of rank.Members) {
        if (offset < this.memberScrollIndex) {
          if (this.showOffline || m.Online) offset++;
          continue;
        }
        if (!this.showOffline && !m.Online) continue;
        const box = this.membersRanks[row]!;
        box.enabled = this.can(Opt.CanChangeRank) && rank.Index >= this.m.myRankId;
        this.membersDelete[row]!.visible = this.can(Opt.CanKick) && rank.Index >= this.m.myRankId && m.Name !== me;
        box.selectedIndex = rank.Index;
        this.membersName[row]!.text = m.Name;
        const status = this.membersStatus[row]!;
        status.foreColour = m.Online ? "#32cd32" : "#ffffff"; // LimeGreen / White
        if (m.Online) status.text = "Online";
        else {
          const days = Math.trunc((now - dateFromBinary(m.LastLogin).getTime()) / 86400000);
          status.text = days === 0 ? "Today" : days === 1 ? "Yesterday" : `${days} Days ago`;
        }
        row++;
        if (row > MEMBER_ROWS - 1) return;
      }
    }
  }

  private membersScroll(delta: number): void {
    // MembersUpButton / MembersDownButton / MembersPanel_MouseWheel
    if (delta < 0 && this.memberScrollIndex === 0) return;
    if (delta > 0 && this.memberScrollIndex >= this.membersShowCount - MEMBER_ROWS) return;
    this.memberScrollIndex += delta;
    this.updateMembers();
    this.updateMembersScrollPosition();
  }

  /** UpdateMembersScrollPosition. */
  private updateMembersScrollPosition(): void {
    const span = this.membersShowCount - MEMBER_ROWS;
    let y = span > 0 ? 16 + this.memberScrollIndex * Math.trunc(289 / span) : 16;
    if (y >= 318 - 20) y = 318 - 20;
    if (y <= 16) y = 16;
    this.membersPositionBar.location = [337, y];
  }

  /** MembersPositionBar_OnMoving. */
  private membersPositionMoving(y: number): void {
    if (y >= 318 - 20) y = 318 - 20;
    if (y < 16) y = 16;
    const span = this.membersShowCount - MEMBER_ROWS;
    const interval = span > 0 ? Math.max(1, Math.trunc(289 / span)) : 0;
    this.memberScrollIndex = interval ? Math.floor((y - 16) / interval) : 0;
    if (this.memberScrollIndex > span) this.memberScrollIndex = span;
    if (this.memberScrollIndex <= 0) this.memberScrollIndex = 0;
    this.updateMembers();
    this.membersPositionBar.location = [337, y];
  }

  /** MembersShowOfflineSwitch. */
  private membersShowOfflineSwitch(): void {
    this.showOffline = !this.showOffline;
    this.membersShowOfflineStatus.visible = this.showOffline;
    this.updateMembers();
  }

  /** GuildDialog.OnNewRank (a member's rank drop-down; MinimumOption = MyRankId). */
  private onNewRank(row: number, wanted: number): void {
    if (wanted >= this.m.ranks.length || this.lastGuildMsg > this.now()) return;
    const name = this.membersName[row]!.text;
    const rank = this.m.ranks[wanted]!;
    messageBox(this.stage, `Are you sure you want to change the rank of ${name} to ${rank.Name}?`, [{ label: "Yes", onClick: () => {
      this.send(ClientPackets.EditGuildMember({ ChangeType: 2, RankIndex: rank.Index, Name: name, RankName: rank.Name }));
      this.lastGuildMsg = this.now() + 5000;
    } }, { label: "No" }]);
  }

  /** GuildDialog.AddMember (the recruit box on the status page). */
  private addMember(): void {
    if (!this.can(Opt.CanRecruit) || this.lastGuildMsg > this.now()) return;
    const name = this.recruitName.text.trim();
    if (!name) return;
    this.send(ClientPackets.EditGuildMember({ ChangeType: 0, RankIndex: 0, Name: name, RankName: "" }));
    this.lastGuildMsg = this.now() + 5000;
    this.recruitName.text = "";
  }

  /** GuildDialog.DeleteMember. */
  private deleteMember(row: number): void {
    const name = this.membersName[row]!.text;
    if (name === this.me() || this.lastGuildMsg > this.now()) return;
    messageBox(this.stage, `Are you sure you want to kick ${name}?`, [{ label: "Yes", onClick: () => {
      this.send(ClientPackets.EditGuildMember({ ChangeType: 1, RankIndex: 0, Name: name, RankName: "" }));
      this.lastGuildMsg = this.now() + 5000;
    } }, { label: "No" }]);
  }

  // — Storage page —

  /** GuildDialog.UpdateStorage: rows StorageIndex .. StorageIndex + 7 of the 8 x 14 grid. */
  private updateStorage(): void {
    for (let x = 0; x < 8; x++) {
      for (let y = 0; y < 14; y++) {
        const cell = this.storageCells[8 * y + x]!;
        const shown = this.storageIndex <= y && y < this.storageIndex + 8;
        cell.hidden = !shown;
        if (!shown) continue;
        const row = y - this.storageIndex;
        cell.style.left = x * 35 + 31 + x + "px";
        cell.style.top = row * 35 + 20 + row + "px";
      }
    }
  }

  /** StorageUpButton / StorageDownButton / StoragePanel_MouseWheel. */
  private storageScroll(delta: number): void {
    const next = Math.min(6, Math.max(0, this.storageIndex + delta));
    if (next === this.storageIndex) return;
    this.storageIndex = next;
    this.updateStorage();
    // StorageUpdatePositionBar
    let y = 16 + this.storageIndex * Math.trunc(289 / 6);
    if (y >= 318 - 20) y = 318 - 20;
    if (y <= 16) y = 16;
    this.storagePositionBar.location = [337, y];
  }

  /** StoragePositionBar_OnMoving. */
  private storagePositionMoving(y: number): void {
    if (y >= 318 - 20) y = 318 - 20;
    if (y < 16) y = 16;
    this.storageIndex = Math.min(6, Math.max(0, Math.floor((y - 16) / Math.trunc(289 / 8))));
    this.updateStorage();
    this.storagePositionBar.location = [337, y];
  }

  /** GuildDialog.StorageAddGold (MirAmountBox up to GameScene.Gold). */
  private storageAddGold(): void {
    if (this.lastGuildMsg > this.now()) return;
    amountBox("Deposit", 116, this.userGold(), (amount) => {
      if (amount <= 0) return;
      this.lastGuildMsg = this.now() + 100;
      this.send(ClientPackets.GuildStorageGoldChange({ Type: 0, Amount: amount }));
    });
  }

  /** GuildDialog.StorageRemoveGold (MirAmountBox up to the guild's gold). */
  private storageRemoveGold(): void {
    if (this.lastGuildMsg > this.now()) return;
    amountBox("Gold to retrieve:", 116, this.m.gold, (amount) => {
      if (amount <= 0) return;
      this.lastGuildMsg = this.now() + 100;
      this.send(ClientPackets.GuildStorageGoldChange({ Type: 1, Amount: amount }));
    });
  }

  // — Rank page —

  /** GuildDialog.UpdateRanks: ranks above your own (Index < MyRankId) are read-only. */
  private updateRanks(): void {
    const sel = this.ranksSelectBox.selectedIndex;
    const rank = sel >= 0 ? this.m.ranks[sel] : undefined;
    for (let i = 0; i < this.ranksOptionsStatus.length; i++) {
      this.ranksOptionsStatus[i]!.visible = !!rank && (rank.Options & (1 << i)) !== 0;
      this.ranksOptionsButtons[i]!.enabled = !!rank && rank.Index >= this.m.myRankId;
    }
    if (!rank || rank.Index < this.m.myRankId) {
      this.lastRankNameChange = Number.MAX_SAFE_INTEGER;
      this.ranksName.text = "";
    } else {
      this.ranksName.text = rank.Name;
      this.lastRankNameChange = 0;
    }
    this.ranksNameBeforeDraw();
  }

  /** RanksName_BeforeDraw: the name box and Save follow LastRankNameChange. */
  private ranksNameBeforeDraw(): void {
    const t = this.now();
    const on = this.lastRankNameChange < t;
    this.ranksName.input.disabled = !on;
    this.ranksSaveName.enabled = on;
    if (!on && this.lastRankNameChange !== Number.MAX_SAFE_INTEGER && !this.rankNameTimer) {
      this.rankNameTimer = setTimeout(() => { this.rankNameTimer = 0; this.ranksNameBeforeDraw(); }, this.lastRankNameChange - t + 50);
    }
  }

  /** GuildDialog.OnRankSelect: the last entry is "Add New" (C.EditGuildMember ChangeType 4 after a Yes/No). */
  private onRankSelect(index: number): void {
    if (index < this.m.ranks.length) this.ranksSelectBox.selectedIndex = index;
    else if (this.m.ranks.length < 255 && this.lastGuildMsg <= this.now()) {
      messageBox(this.stage, "Are you sure you want to create a new rank?", [{ label: "Yes", onClick: () => {
        this.send(ClientPackets.EditGuildMember({ ChangeType: 4, RankIndex: 0, Name: "", RankName: `Rank-${this.m.ranks.length - 1}` }));
        this.lastGuildMsg = this.now() + 5000;
      } }, { label: "No" }]);
    }
    this.updateRanks();
  }

  /** GuildDialog.SwitchRankOption (ChangeType 5): Name "true" sets the bit, "false" clears it; 300 ms between clicks. */
  private switchRankOption(bit: number): void {
    const sel = this.ranksSelectBox.selectedIndex;
    const rank = this.m.ranks[sel];
    if (sel === -1 || !rank || this.lastGuildMsg > this.now()) return;
    this.send(ClientPackets.EditGuildMember({ ChangeType: 5, RankIndex: rank.Index, Name: this.ranksOptionsStatus[bit]!.visible ? "false" : "true", RankName: String(bit) }));
    this.lastGuildMsg = this.now() + 300;
  }

  /** GuildDialog.RanksChangeName (ChangeType 3). */
  private ranksChangeName(): void {
    const rank = this.m.ranks[this.ranksSelectBox.selectedIndex];
    const name = this.ranksName.text;
    if (!rank || !name || this.lastRankNameChange >= this.now()) return;
    this.send(ClientPackets.EditGuildMember({ ChangeType: 3, RankIndex: rank.Index, Name: "", RankName: name }));
    this.lastRankNameChange = this.now() + 5000;
    this.ranksNameBeforeDraw();
  }

  // — Buff page —

  /** GuildDialog.RequestBuff: buy a buff not owned yet (points, guild level, rank), else activate a timed one (guild gold). */
  private requestBuff(id: number): void {
    const info = this.m.buffInfos[id + this.startIndex];
    if (!info) return;
    const buff = this.m.findBuff(info.Id);
    let error = "";
    if (!buff) {
      if (this.m.sparePoints < info.PointsRequirement) error = "Insufficient points available.";
      if (this.m.level < info.LevelRequirement) error = "Guild level too low.";
    } else {
      if (buff.Active) error = "Buff is still active.";
      if (this.m.gold < info.ActivationCost) error = "Insufficient guild funds.";
    }
    if (!this.can(Opt.CanActivateBuff)) error = "Guild rank does not allow buff activation.";
    if (error) { messageBox(this.stage, error, [{ label: "OK" }]); return; }
    if (this.now() < this.lastBuffRequest + 100) return;
    this.lastBuffRequest = this.now();
    this.send(ClientPackets.GuildBuffUpdate({ Action: buff ? 2 : 1, Id: info.Id }));
  }

  /** UpdatePositionBar. */
  private updatePositionBar(): void {
    const span = this.m.buffInfos.length - 8;
    const h = span > 0 ? Math.trunc(((277 - this.buffPositionBar.size[1]) / span) * this.startIndex) : 0;
    this.buffPositionBar.location = [203, 39 + h];
  }

  /** PositionBar_OnMoving. */
  private buffPositionMoving(y: number): void {
    if (y >= 296) y = 296;
    if (y < 39) y = 39;
    this.buffPositionBar.location = [203, y];
    const span = this.m.buffInfos.length - 8;
    if (span <= 0) return;
    const h = Math.round((y - 39) / ((277 - this.buffPositionBar.size[1]) / span));
    if (h === this.startIndex) return;
    this.startIndex = Math.min(span, Math.max(0, h));
    this.refresh();
  }

  /** BuffsPanel_MouseWheel. */
  private buffsWheel(e: WheelEvent): void {
    const count = wheelCount(e);
    if (this.startIndex === 0 && count >= 0) return;
    if (this.startIndex === this.m.buffInfos.length - 8 && count <= 0) return;
    this.startIndex -= count > 0 ? 1 : -1;
    if (this.m.buffInfos.length <= 8) this.startIndex = 0;
    this.updatePositionBar();
    this.refresh();
  }

  /** GuildDialog.CreateHintLabel (GameScene.GuildBuffLabel: (50,50,50) at 70 %, gray border, at the mouse + 15). */
  private createHintLabel(id: number, e: PointerEvent): void {
    this.disposeHintLabel();
    const info = this.m.buffInfos[id + this.startIndex];
    if (!info) return;
    const lines = [info.Name];
    const req: string[] = [];
    if (info.LevelRequirement > 0) req.push(`Minimum Guild Level: ${info.LevelRequirement}`);
    if (info.PointsRequirement > 0) req.push(`Points Required: ${info.PointsRequirement}`);
    if (info.ActivationCost > 0) req.push(`Activation Cost: ${info.ActivationCost} gold.`);
    if (req.length) lines.push(req.join("\n"));
    if (info.TimeLimit > 0) {
      const active = this.m.findBuff(info.Id);
      lines.push(active?.Active ? `Time Remaining: ${active.ActiveTimeRemaining} minutes` : `Buff Length: ${info.TimeLimit} minutes.`);
    }
    // GuildBuffInfo.ShowStats
    for (const [k, v] of Object.entries(info.Stats)) {
      if (!v) continue;
      const stat = StatName[Number(k)] ?? `Stat${k}`;
      lines.push(`${v < 0 ? "Decreases" : "Increases"} ${stat} by: ${v}${stat.includes("Percent") ? "%" : ""}.`);
    }
    const box = document.createElement("div");
    box.className = "mir mir-label mir-autosize";
    box.style.cssText = "z-index:100000;pointer-events:none;padding:4px;background:rgb(50,50,50);outline:1px solid #808080;opacity:.7;";
    box.textContent = lines.join("\n");
    getUiRoot().append(box);
    this.buffLabel = box;
    const move = (m: PointerEvent) => {
      if (this.buffLabel !== box) return;
      let [x, y] = toStage(m.clientX, m.clientY);
      x += 15;
      if (x + box.offsetWidth > SCREEN_WIDTH) x = SCREEN_WIDTH - box.offsetWidth;
      if (y + box.offsetHeight > SCREEN_HEIGHT) y = SCREEN_HEIGHT - box.offsetHeight;
      box.style.left = Math.round(x) + "px";
      box.style.top = Math.round(y) + "px";
    };
    move(e);
    const el = this.buffs[id]!.control.el;
    el.addEventListener("pointermove", move);
    this.buffMove = () => el.removeEventListener("pointermove", move);
  }

  /** Removes the hover label's mouse-follow listener. */
  private buffMove: (() => void) | null = null;

  /** GameScene.DisposeGuildBuffLabel. */
  private disposeHintLabel(): void {
    this.buffMove?.();
    this.buffMove = null;
    this.buffLabel?.remove();
    this.buffLabel = null;
  }

  private can(option: number): boolean {
    return this.m.can(option);
  }

  // — The model's events (GameScene's guild handlers, src/game/guild.ts) —

  /** The GuildDialog parts a guild packet changed, in the desktop's order (`changed` follows with the redraw). */
  private onView(v: GuildView): void {
    switch (v.kind) {
      case "status":
        // GameScene.GuildStatus: no guild hides the dialog; StatusChanged: the notice box leaves edit mode
        if (v.left) this.win.hide();
        this.editing = false;
        this.noticeBox.input.readOnly = true;
        if (v.left) { this.noticeBox.text = ""; this.refreshMemberList(); }
        this.updateMembers();
        break;
      case "notice": this.noticeChange(); break;
      case "ranks": this.refreshMemberList(); break;
      case "members": this.updateMembers(); break;
      case "rank": this.updateMembers(); this.updateRanks(); break;
    }
  }

  // — Drawing —

  /** GuildDialog BeforeDraw (RefreshInterface) + the pages' BeforeDraw + StatusChanged / ResetButtonStats. */
  private refresh(): void {
    // RefreshInterface: no guild -> hide
    if (!this.m.name) { this.win.hide(); }
    if (this.startIndex < 0) this.startIndex = 0;
    // tab buttons (StatusChanged) + LeftDialog / RightDialog indices
    this.storageButton.visible = this.can(Opt.CanStoreItem) || this.can(Opt.CanRetrieveItem);
    this.rankButton.visible = this.can(Opt.CanChangeRank);
    this.buffButton.visible = this.m.buffInfos.length > 0;
    this.noticeButton.index = this.leftPage === 0 ? 94 : 93;
    this.membersButton.index = this.leftPage === 1 ? 100 : 99;
    this.storageButton.index = this.leftPage === 2 ? 106 : 105;
    this.rankButton.index = this.leftPage === 3 ? 102 : 101;
    this.statusButton.index = this.rightPage === 0 ? 104 : 103;
    this.buffButton.index = this.rightPage === 1 ? 96 : 95;
    this.noticePage.visible = this.leftPage === 0;
    this.membersPage.visible = this.leftPage === 1;
    this.storagePage.visible = this.leftPage === 2;
    this.rankPage.visible = this.leftPage === 3;
    this.statusPage.visible = this.rightPage === 0;
    this.buffPage.visible = this.rightPage === 1;

    // notice page: edit / save
    const canEdit = this.can(Opt.CanChangeNotice);
    this.noticeEditButton.visible = canEdit && !this.editing;
    this.noticeSaveButton.visible = this.editing;

    // StatusPage.BeforeDraw
    this.statusGuildName.text = this.m.name;
    this.statusLevel.text = this.m.name ? String(this.m.level) : "";
    this.statusMembers.text = this.m.name ? `${this.m.memberCount}${this.m.maxMembers === 0 ? "" : "/" + this.m.maxMembers}` : "";

    // StatusExpBar_BeforeDraw
    if (this.m.maxExperience === 0n) {
      this.expLabel.text = "";
      this.expBack.hidden = this.expClip.hidden = true;
    } else {
      let percent = Number(this.m.experience) / Number(this.m.maxExperience);
      this.expLabel.text = `${Math.round(percent * 10000) / 100}%`; // {0:#0.##%}
      if (percent > 1) percent = 1;
      this.expBack.hidden = false;
      this.expClip.hidden = percent <= 0;
      this.expClip.style.width = Math.trunc((260 - 3) * Math.max(0, percent)) + "px";
    }

    // StoragePage.BeforeDraw
    this.storageGoldText.text = this.m.gold > 0 ? this.m.gold.toLocaleString("en-US") : "0";
    this.storageGoldRemove.visible = this.m.myRankId === 0;

    // RefreshInterface: the buff buttons
    this.pointsLeft.text = String(this.m.sparePoints);
    for (let i = 0; i < this.buffs.length; i++) {
      const b = this.buffs[i]!;
      const info = this.m.buffInfos[this.startIndex + i];
      if (!info) { b.control.visible = false; continue; }
      b.control.visible = true;
      const buff = this.m.findBuff(info.Id);
      b.name.text = info.Name;
      let icon = info.Icon;
      if (!buff) {
        const low = info.LevelRequirement > this.m.level;
        b.info.text = low ? "Insufficient Level" : "Available";
        b.info.foreColour = low ? "#ff0000" : "#ffffff";
        icon += 2;
        b.obtained.text = "";
      } else {
        b.info.text = info.TimeLimit > 0 ? (buff.Active ? "Counting down." : "Expired") : "Obtained";
        b.info.foreColour = "#ffffff";
        b.obtained.text = buff.Active ? "Active" : "Inactive";
        if (buff.Active) icon += 1;
      }
      b.icon.index = icon;
    }
  }
}

// Inspect + observe: InspectDialog (Client/MirScenes/Dialogs/MainDialogs.cs; S.PlayerInspect, Ctrl+right-click on another
// player, or a RankingDialog row) with the desktop artwork: Prguse 430 at (536, 0), the CharacterPage (Prguse 340 +
// gender) at (8, 70) with the 14 MirGridType.Inspect cells at the CharacterDialog positions and the CharacterPage.AfterDraw
// paperdoll over them (StateItem armour / weapon / helmet or Prguse hair, Prguse2 wings, at the dialog's location - 20),
// the name / guild labels, class image, LoverButton (Prguse 604, hint = LoverName) and the Group / Friend / Mail / Trade /
// Observe buttons (Prguse 431 / 434 / 437 / 523, Title 854), hidden for a hero. Escape hides it (GameScene Closeall).
// The item labels use the inspected player's level and class (GameScene.CreateItemLabel with Inspect).
// Since phase 1c the inspected player's data, the InspectTime gate, the request and the button sends live in the Inspect
// model (src/game/inspect.ts); this window is a view that opens on its `received` event.
// Also reads the AllowObserve flag (the Account model: S.UserInformation / S.AllowObserve; OptionDialog.ToggleObserve = the
// "@ALLOWOBSERVE" chat command).
// Deliberate differences: the Observe button stays hidden (a granted C.Observe ends the requester's own game (StopGame)
// and the observer mode is not ported); the Mail button calls `composeMail` (set by game.ts; inert while null); the
// name label takes a click (the desktop's NameLabel.Click never fires: NotControl).

import type { UserItem } from "../protocol/server.ts";
import { EquipmentSlot, MirClass, MirGender } from "../protocol/enums.generated.ts";
import type { Account } from "../game/account.ts";
import type { Inspect } from "../game/inspect.ts";
import type { GameInfo } from "../game/gameInfo.ts";
import { gameSignal } from "../game/scope.ts";
import { SoundList } from "../game/audio.ts";
import { KeybindOptions, matches } from "../game/keybinds.ts";
import { MirWindow, MirImage, MirButton, MirLabel, Lib, closeButton, setPicture } from "./mir/controls.ts";
import { showItemLabel, type ItemLabelContext } from "./itemLabel.ts";
import { placeCell, type InventoryWindow } from "./inventoryWindow.ts";
import { gameItemInfos, gameUser } from "./userRefs.ts";

const T = { // ClientTextKeys (English.json)
  InviteToGroup: "Invite to Group", AddToFriendsList: "Add to Friends List", SendMail: "Send Mail", Trade: "Trade", Observe: "Observe",
};

/** InspectDialog cell Locations (Parent = CharacterPage). */
const CELLS: [number, [number, number]][] = [
  [EquipmentSlot.Weapon, [123, 7]], [EquipmentSlot.Armour, [163, 7]], [EquipmentSlot.Helmet, [203, 7]], [EquipmentSlot.Torch, [203, 134]],
  [EquipmentSlot.Necklace, [203, 98]], [EquipmentSlot.BraceletL, [8, 170]], [EquipmentSlot.BraceletR, [203, 170]], [EquipmentSlot.RingL, [8, 206]],
  [EquipmentSlot.RingR, [203, 206]], [EquipmentSlot.Amulet, [8, 242]], [EquipmentSlot.Boots, [48, 242]], [EquipmentSlot.Belt, [88, 242]],
  [EquipmentSlot.Stone, [128, 242]], [EquipmentSlot.Mount, [203, 62]],
];

export class InspectWindow {
  // InspectDialog fields (the model's)
  get items(): readonly (UserItem | null)[] { return this.model.items; }
  get name(): string { return this.model.name; }
  get guildName(): string { return this.model.guildName; }
  get guildRank(): string { return this.model.guildRank; }
  get cls(): number { return this.model.cls; }
  get gender(): number { return this.model.gender; }
  get hair(): number { return this.model.hair; }
  get level(): number { return this.model.level; }
  get loverName(): string { return this.model.loverName; }
  get allowObserve(): boolean { return this.model.allowObserve; }
  get isHero(): boolean { return this.model.isHero; }
  /** InspectDialog.InspectID. */
  get inspectId(): number { return this.model.inspectId; }
  /** GameScene.AllowObserve (the user's own flag): S.UserInformation / S.AllowObserve (the Account model). */
  get userAllowObserve(): boolean { return this.account.allowObserve; }
  /** MailButton: MailComposeLetterDialog.ComposeMail(Name) (game.ts: `= (n) => mail.composeLetter(n)`). */
  composeMail: ((name: string) => void) | null = null;
  private readonly model: Inspect;
  private readonly prefillChat: (text: string) => void;
  private readonly invWindow: InventoryWindow;
  private readonly gameInfo: GameInfo;
  private readonly account: Account;

  private readonly win: MirWindow;
  private readonly characterPage: MirImage;
  private readonly cellLayer: HTMLDivElement;
  private readonly doll: { armour: HTMLDivElement; wing: HTMLDivElement; weapon: HTMLDivElement; head: HTMLDivElement };
  private readonly nameLabel: MirLabel;
  private readonly guildLabel: MirLabel;
  private readonly classImage: MirImage;
  private readonly loverButton: MirButton;
  private readonly playerButtons: MirButton[];

  constructor(_stage: HTMLElement, model: Inspect, prefillChat: (text: string) => void, invWindow: InventoryWindow, gameInfo: GameInfo, account: Account) {
    this.account = account;
    this.model = model;
    this.prefillChat = prefillChat;
    this.invWindow = invWindow;
    this.gameInfo = gameInfo;

    // InspectDialog ctor
    this.win = new MirWindow({ library: Lib.Prguse, index: 430, location: [536, 0], movable: true, sort: true });
    this.win.el.addEventListener("contextmenu", (e) => e.preventDefault());
    this.characterPage = new MirImage({ parent: this.win, library: Lib.Prguse, index: 340, location: [8, 70] });
    // the cells (Parent = CharacterPage), then CharacterPage.AfterDraw over them at the dialog's DisplayLocation + (0, -20)
    this.cellLayer = document.createElement("div");
    this.characterPage.el.append(this.cellLayer);
    const dollLayer = document.createElement("div");
    dollLayer.className = "mir";
    Object.assign(dollLayer.style, { left: "-8px", top: "-90px", pointerEvents: "none" });
    const layer = () => { const d = document.createElement("div"); d.className = "mir-pic"; dollLayer.append(d); return d; };
    this.doll = { armour: layer(), wing: layer(), weapon: layer(), head: layer() };
    this.doll.wing.style.mixBlendMode = "plus-lighter"; // Prguse2.DrawBlend
    this.characterPage.el.append(dollLayer);

    closeButton(this.win, [241, 3], () => this.toggle(false));
    const button = (library: string, index: number, x: number, hint: string, onClick: () => void) =>
      new MirButton({ parent: this.win, library, index, hoverIndex: index + 1, pressedIndex: index + 2, location: [x, 357], sound: SoundList.ButtonA, hint, onClick });
    this.playerButtons = [
      button(Lib.Prguse, 431, 55, T.InviteToGroup, () => this.model.addToGroup()),
      button(Lib.Prguse, 434, 85, T.AddToFriendsList, () => this.model.addFriend()),
      button(Lib.Prguse, 437, 115, T.SendMail, () => this.composeMail?.(this.name)),
      button(Lib.Prguse, 523, 145, T.Trade, () => this.model.tradeRequest()),
    ];
    // ObserveButton (Title 854): hidden until observer mode is ported
    const observe = button(Lib.Title, 854, 175, T.Observe, () => this.model.observe());
    observe.visible = false;
    // NameLabel.Click: "/Name " in the chat box
    this.nameLabel = new MirLabel({ parent: this.win, location: [50, 12], size: [190, 20], center: true, onClick: () => this.prefillChat(`/${this.name} `) });
    this.loverButton = new MirButton({ parent: this.win, library: Lib.Prguse, index: 604, location: [17, 17], sound: 0 });
    this.guildLabel = new MirLabel({ parent: this.win, location: [50, 33], size: [190, 30], center: true, notControl: true });
    this.classImage = new MirImage({ parent: this.win, library: Lib.Prguse, index: 100, location: [15, 33], notControl: true });

    // GameScene.Bind -> RequestItemInfo: redraw when a missing ItemInfo arrives
    gameInfo.onChange((kind, index) => {
      if (kind === "item" && this.win.visible && this.items.some((i) => i?.ItemIndex === index)) this.render();
    });
    const signal = gameSignal();
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      if (matches(e, KeybindOptions.Closeall)) this.toggle(false); // InspectDialog.Hide()
    }, { signal });
    // GameScene.PlayerInspect: InspectDialog.RefreshInferface + Show
    const offReceived = model.received.on(() => this.toggle(true));
    signal.addEventListener("abort", () => { offReceived(); this.win.dispose(); });
  }

  toggle(show = !this.win.visible): void {
    if (show) { this.win.show(); this.render(); }
    else this.win.hide();
  }

  /** MapControl.OnMouseClick (Right + Ctrl on another PlayerObject / HeroObject) and RankingRow.Inspect, with the shared
   *  GameScene.InspectTime gate. `objectId` is the player's ObjectID, or `(uint)PlayerId` with ranking = true. */
  inspect(objectId: number, ranking = false, hero = false): boolean {
    return this.model.inspect(objectId, ranking, hero);
  }

  /** GameScene.CreateItemLabel with Inspect: the inspected player's level and class are the wearer's. */
  private labelContext(): ItemLabelContext {
    const base = this.invWindow.labelContext();
    const infos = gameItemInfos();
    const level = this.level, job = this.cls;
    return { ...base, level, job, realItem: (info) => (infos ? infos.realItem(info, level, job) : info) };
  }

  /** InspectDialog.RefreshInferface (+ the cells, which show InspectDialog.Items). */
  private render(): void {
    if (!this.win.visible) return;
    this.characterPage.index = 340 + (this.gender === MirGender.Male ? 0 : 1);
    this.classImage.index = 100 + Math.min(4, Math.max(0, this.cls)); // Warrior 100 .. Archer 104
    this.nameLabel.text = this.name;
    this.guildLabel.text = `${this.guildName} ${this.guildRank}`;
    this.loverButton.visible = this.loverName !== "";
    this.loverButton.hint = this.loverName;
    for (const b of this.playerButtons) b.visible = !this.isHero; // Trade / Mail / Friend / Group hidden for a hero
    // the MirGridType.Inspect cells: read-only, item labels with the inspected player as the wearer
    const cells: HTMLElement[] = [];
    for (const [slot, loc] of CELLS) {
      const item = this.items[slot] ?? null;
      const c = placeCell(this.invWindow.viewCell(item), this.cellLayer, loc);
      if (item) c.addEventListener("pointerenter", (e) => showItemLabel(item, this.labelContext(), e.clientX, e.clientY, { inspect: true }));
      cells.push(c);
    }
    this.cellLayer.replaceChildren(...cells);
    this.drawDoll();
  }

  /** CharacterPage.AfterDraw. */
  private drawDoll(): void {
    const info = (it: UserItem) => this.invWindow.itemInfo(it) ?? gameItemInfos()?.get(it.ItemIndex);
    const real = (it: UserItem) => { const i = info(it); const infos = gameItemInfos(); return i && infos ? infos.realItem(i, this.level, this.cls) : i; };
    const armour = this.items[EquipmentSlot.Armour] ?? null, weapon = this.items[EquipmentSlot.Weapon] ?? null, helmet = this.items[EquipmentSlot.Helmet] ?? null;
    const armourInfo = armour ? real(armour) : undefined;
    void setPicture(this.doll.armour, armourInfo ? Lib.StateItems : null, armourInfo?.Image ?? -1, true);
    // RealItem.Effect 1 / 2: Prguse2 1200 + 2 / 4 + the gender offset, which the desktop takes from MapObject.User
    const effect = armourInfo?.Effect ?? 0;
    const userMale = (gameUser()?.look.gender ?? MirGender.Male) === MirGender.Male;
    const wing = effect > 0 ? 1200 + (effect === 1 ? 2 : 4) + (userMale ? 0 : 1) : -1;
    void setPicture(this.doll.wing, wing >= 0 ? Lib.Prguse2 : null, wing, true);
    const weaponInfo = weapon ? real(weapon) : undefined;
    void setPicture(this.doll.weapon, weaponInfo ? Lib.StateItems : null, weaponInfo?.Image ?? -1, true);
    const helmetInfo = helmet ? info(helmet) : undefined;
    if (helmet) {
      this.doll.head.style.transform = "";
      void setPicture(this.doll.head, helmetInfo ? Lib.StateItems : null, helmetInfo?.Image ?? -1, true);
    } else {
      const assassin = this.cls === MirClass.Assassin, male = this.gender === MirGender.Male;
      const index = 441 + this.hair + (assassin ? 20 : 0) + (male ? 0 : 40);
      const ox = assassin ? (male ? 6 : 4) : 0, oy = assassin ? (male ? 25 : 18) : 0;
      this.doll.head.style.transform = `translate(${ox}px, ${oy}px)`;
      void setPicture(this.doll.head, Lib.Prguse, index, true);
    }
  }
}

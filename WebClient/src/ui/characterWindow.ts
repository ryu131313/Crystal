// CharacterDialog (Client/MirScenes/Dialogs/CharacterDialog.cs) with the desktop artwork: Title 504 at the top right,
// the Character / Status / State / Skill tabs (Title 500-503), the CharacterPage paperdoll (Prguse 340 + gender; the
// armour / weapon / helmet or hair / wings drawn from StateItem / Prguse / Prguse2 like CharacterPage.AfterDraw) with
// the equipment cells (MirGridType.Equipment, shared with the inventory pick), the two stat pages (Title 506 / 507)
// and the skill page (Title 508: seven MagicButtons, Back / Next). C / F10 open the character page, S / F11 the skill
// page (magicBar.ts), like GameScene's Equipment / Skills keys.
//
// The same class is the hero's CharacterDialog (heroCharacterWindow.ts, `ctrlKey`): its view may carry the hero's
// look and equipment; without `equipCell` the hero's paperdoll has no cells (the hero item windows hold them).

import type { UserItem } from "../protocol/server.ts";
import { EquipmentSlot, MirClass, MirGender, Stat } from "../protocol/enums.generated.ts";
import type { UserStats } from "../game/stats.ts";
import { gameSignal } from "../game/scope.ts";
import { SoundList } from "../game/audio.ts";
import { KeybindOptions, matchesAny, type KeybindOption } from "../game/keybinds.ts";
import { MirWindow, MirImage, MirButton, MirLabel, Lib, closeButton, setPicture, SCREEN_WIDTH } from "./mir/controls.ts";
import { placeCell } from "./inventoryWindow.ts";
import { MagicButton } from "./magicBar.ts";
import { userWindows, registerUserWindow, gameUser, gameItemInfos } from "./userRefs.ts";

export interface CharacterView {
  name: string; cls: number; level: number; hp: number; mp: number; experience: bigint; maxExperience: bigint; stats: UserStats | null;
  /** The paperdoll look (UserObject.Gender / Hair) and GuildName + GuildRankName; the user's window falls back to the
   *  game's user object when these are missing. */
  gender?: number; hair?: number; guildName?: string; guildRank?: string;
  /** The equipped items for the paperdoll (the user's window reads the inventory when missing). */
  equipment?: readonly (UserItem | null)[];
}

/** CharacterDialog equipment cell Locations (Parent = CharacterPage). */
const EQUIP_CELLS: [number, [number, number]][] = [
  [EquipmentSlot.Weapon, [123, 7]], [EquipmentSlot.Armour, [163, 7]], [EquipmentSlot.Helmet, [203, 7]], [EquipmentSlot.Torch, [203, 134]],
  [EquipmentSlot.Necklace, [203, 98]], [EquipmentSlot.BraceletL, [8, 170]], [EquipmentSlot.BraceletR, [203, 170]], [EquipmentSlot.RingL, [8, 206]],
  [EquipmentSlot.RingR, [203, 206]], [EquipmentSlot.Amulet, [8, 242]], [EquipmentSlot.Boots, [48, 242]], [EquipmentSlot.Belt, [88, 242]],
  [EquipmentSlot.Stone, [128, 242]], [EquipmentSlot.Mount, [203, 62]],
];

type Page = "character" | "status" | "state" | "skill";

export class CharacterWindow {
  private readonly view: () => CharacterView | null;
  private readonly isUser: boolean;
  private readonly equipCell: ((slot: number) => HTMLElement) | null;
  /** The view as of the last render (the item label reads the wearer from it). */
  lastView: CharacterView | null = null;

  private readonly win: MirWindow;
  private readonly pages: Record<Page, MirImage>;
  private readonly tabs: Record<Page, MirButton>;
  private page: Page = "character";
  private readonly nameLabel: MirLabel;
  private readonly guildLabel: MirLabel;
  private readonly classImage: MirImage;
  private readonly doll: { wing: HTMLDivElement; armour: HTMLDivElement; weapon: HTMLDivElement; head: HTMLDivElement };
  private readonly cellLayer: HTMLDivElement;
  private readonly status: Record<string, MirLabel> = {};
  private readonly state: Record<string, MirLabel> = {};
  private readonly magics: MagicButton[] = [];
  private startIndex = 0;
  private dollKey = "";

  /** `opts` (the hero's CharacterDialog, heroCharacterWindow.ts): another title / class, and Ctrl+C instead of C
   *  (KeybindOptions.HeroEquipment). A null view hides the window. `equipCell` supplies the paperdoll's cells. */
  constructor(_stage: HTMLElement, view: () => CharacterView | null, opts: { title?: string; className?: string; ctrlKey?: boolean; equipCell?: (slot: number) => HTMLElement } = {}) {
    this.view = view;
    this.isUser = !opts.ctrlKey;
    this.equipCell = opts.equipCell ?? (this.isUser ? (slot) => userWindows.inventory?.equipCell(slot) ?? document.createElement("div") : null);
    if (this.isUser) registerUserWindow("character", this);

    // CharacterDialog ctor
    this.win = new MirWindow({ library: Lib.Title, index: 504, movable: true, sort: true, location: [SCREEN_WIDTH - 264, 0], className: opts.className });
    this.win.el.addEventListener("contextmenu", (e) => e.preventDefault());
    const character = new MirImage({ parent: this.win, library: Lib.Prguse, index: 340, location: [8, 90] });
    const status = new MirImage({ parent: this.win, library: Lib.Title, index: 506, location: [8, 90], visible: false });
    const state = new MirImage({ parent: this.win, library: Lib.Title, index: 507, location: [8, 90], visible: false });
    const skill = new MirImage({ parent: this.win, library: Lib.Title, index: 508, location: [8, 90], visible: false });
    this.pages = { character, status, state, skill };
    const tab = (page: Page, index: number, pressed: number, x: number) =>
      new MirButton({ parent: this.win, library: Lib.Title, index, pressedIndex: pressed, location: [x, 70], size: [64, 20], sound: SoundList.ButtonA, onClick: () => this.showPage(page) });
    this.tabs = { character: tab("character", 500, 500, 8), status: tab("status", -1, 501, 70), state: tab("state", -1, 502, 132), skill: tab("skill", -1, 503, 194) };
    closeButton(this.win, [241, 3], () => this.toggle(false));
    this.nameLabel = new MirLabel({ parent: this.win, location: [0, 12], size: [264, 20], center: true, notControl: true });
    this.guildLabel = new MirLabel({ parent: this.win, location: [0, 33], size: [264, 30], center: true, notControl: true });
    this.classImage = new MirImage({ parent: this.win, library: Lib.Prguse, index: 100, location: [15, 33], notControl: true });

    // equipment cells (Parent = CharacterPage), then CharacterPage.AfterDraw's paperdoll over them, drawn at the
    // dialog's DisplayLocation (= (-8, -90) inside the page) with the images' offsets
    this.cellLayer = document.createElement("div");
    character.el.append(this.cellLayer);
    const dollLayer = document.createElement("div");
    dollLayer.className = "mir";
    Object.assign(dollLayer.style, { left: "-8px", top: "-90px", pointerEvents: "none" });
    const layer = () => { const d = document.createElement("div"); d.className = "mir-pic"; dollLayer.append(d); return d; };
    this.doll = { wing: layer(), armour: layer(), weapon: layer(), head: layer() };
    this.doll.wing.style.mixBlendMode = "plus-lighter"; // Prguse2.DrawBlend
    character.el.append(dollLayer);
    this.refreshEquipment();

    // STATS I / STATS II labels (AutoSize at x 126, 18 px apart)
    ["HP", "MP", "AC", "MAC", "DC", "MC", "SC", "CritR", "CritD", "AttkSpd", "Acc", "Agil", "Luck"].forEach((k, i) => {
      this.status[k] = new MirLabel({ parent: status, autoSize: true, location: [126, 20 + i * 18], notControl: true });
    });
    ["ExpP", "BagW", "WearW", "HandW", "MagicR", "PoisonRes", "HealthR", "ManaR", "PoisonRec", "HolyT", "Freeze", "PoisonAtk"].forEach((k, i) => {
      this.state[k] = new MirLabel({ parent: state, autoSize: true, location: [126, 20 + i * 18], notControl: true });
    });

    // SkillPage: Magics[7], NextButton, BackButton
    for (let i = 0; i < 7; i++) this.magics.push(new MagicButton(skill, [8, 8 + i * 33], !this.isUser));
    new MirButton({ parent: skill, library: Lib.Prguse, index: 396, pressedIndex: 397, location: [140, 250], sound: SoundList.ButtonA,
      onClick: () => { if (this.startIndex + 7 >= this.magicList().length) return; this.startIndex += 7; this.render(); } });
    new MirButton({ parent: skill, library: Lib.Prguse, index: 398, pressedIndex: 399, location: [90, 250], sound: SoundList.ButtonA,
      onClick: () => { if (this.startIndex - 7 < 0) return; this.startIndex -= 7; this.render(); } });

    // KeybindOptions.Equipment (F10) / Equipment2 (C without Ctrl): show the character page, or hide when it is up;
    // the hero's: HeroEquipment (Ctrl+C) toggles
    const keys: KeybindOption[] = opts.ctrlKey ? [KeybindOptions.HeroEquipment] : [KeybindOptions.Equipment, KeybindOptions.Equipment2];
    const signal = gameSignal();
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement) return;
      if (this.isUser && matchesAny(e, KeybindOptions.Closeall)) { this.toggle(false); return; }
      if (!matchesAny(e, ...keys)) return;
      if (e.key === "F10") e.preventDefault(); // the browser's menu bar
      if (e.ctrlKey) {
        if (String(window.getSelection() ?? "") !== "") return; // keep Ctrl+C as copy while text is selected
        e.preventDefault();
      }
      if (opts.ctrlKey) this.toggle();
      else this.openPage("character");
    }, { signal });
    signal.addEventListener("abort", () => this.win.dispose());
  }

  get visible(): boolean {
    return this.win.visible;
  }

  /** The current page ("character", "status", "state", "skill"). */
  get currentPage(): Page {
    return this.page;
  }

  toggle(show = !this.win.visible): void {
    if (show) this.win.show(); else this.win.hide();
    this.render();
  }

  /** GameScene's Equipment / Skills keys: show the window on `page`, or hide it when that page is already up. */
  openPage(page: Page): void {
    if (!this.win.visible || this.page !== page) { this.win.show(); this.showPage(page); }
    else this.win.hide();
    this.render();
  }

  /** ShowCharacterPage / ShowStatusPage / ShowStatePage / ShowSkillPage. */
  showPage(page: Page): void {
    this.page = page;
    const pressed: Record<Page, number> = { character: 500, status: 501, state: 502, skill: 503 };
    for (const p of Object.keys(this.pages) as Page[]) {
      this.pages[p].visible = p === page;
      this.tabs[p].index = p === page ? pressed[p] : -1;
    }
    this.render();
  }

  private magicList() {
    return this.isUser ? userWindows.magic?.magics ?? [] : [];
  }

  /** Rebuilds the equipment cells (the inventory window calls this after every item change). */
  refreshEquipment(): void {
    if (!this.equipCell) return;
    this.cellLayer.replaceChildren();
    for (const [slot, loc] of EQUIP_CELLS) placeCell(this.equipCell(slot), this.cellLayer, loc);
    this.dollKey = "";
  }

  /** CharacterDialog.RefreshInterface + the pages' BeforeDraw; call every frame (cheap when nothing changed). */
  render(): void {
    const v = this.view();
    this.lastView = v;
    if (this.isUser) userWindows.inventory?.process();
    if (!this.win.visible) return;
    if (!v) { this.win.hide(); return; }
    const u = this.isUser ? gameUser() : null;
    const gender = v.gender ?? u?.look.gender ?? MirGender.Male;
    const hair = v.hair ?? u?.look.hair ?? 0;
    const st = v.stats?.stats ?? {};
    const s = (k: number) => st[k] ?? 0;

    this.pages.character.index = 340 + (gender === MirGender.Male ? 0 : 1);
    this.classImage.index = 100 + Math.min(4, Math.max(0, v.cls)); // Warrior 100 .. Archer 104
    this.nameLabel.text = v.name;
    this.guildLabel.text = `${v.guildName ?? u?.guildName ?? ""} ${v.guildRank ?? ""}`;
    if (this.page === "character") this.drawDoll(v, gender, hair);
    else if (this.page === "status") {
      // StatusPage.BeforeDraw
      const t = this.status;
      t.AC!.text = `${s(Stat.MinAC)}-${s(Stat.MaxAC)}`;
      t.MAC!.text = `${s(Stat.MinMAC)}-${s(Stat.MaxMAC)}`;
      t.DC!.text = `${s(Stat.MinDC)}-${s(Stat.MaxDC)}`;
      t.MC!.text = `${s(Stat.MinMC)}-${s(Stat.MaxMC)}`;
      t.SC!.text = `${s(Stat.MinSC)}-${s(Stat.MaxSC)}`;
      t.HP!.text = `${v.hp}/${s(Stat.HP)}`;
      t.MP!.text = `${v.mp}/${s(Stat.MP)}`;
      t.CritR!.text = `${s(Stat.CriticalRate)}%`;
      t.CritD!.text = `${s(Stat.CriticalDamage)}`;
      t.AttkSpd!.text = `${s(Stat.AttackSpeed)}`;
      t.Acc!.text = `+${s(Stat.Accuracy)}`;
      t.Agil!.text = `+${s(Stat.Agility)}`;
      t.Luck!.text = `${s(Stat.Luck)}`;
    } else if (this.page === "state") {
      // StatePage.BeforeDraw ({0:0.##%})
      const t = this.state, w = v.stats;
      const pct = v.maxExperience > 0n ? (Number(v.experience) * 100) / Number(v.maxExperience) : 0;
      t.ExpP!.text = `${Math.round(pct * 100) / 100}%`;
      t.BagW!.text = `${w?.currentBagWeight ?? 0}/${s(Stat.BagWeight)}`;
      t.WearW!.text = `${w?.currentWearWeight ?? 0}/${s(Stat.WearWeight)}`;
      t.HandW!.text = `${w?.currentHandWeight ?? 0}/${s(Stat.HandWeight)}`;
      t.MagicR!.text = `+${s(Stat.MagicResist)}`;
      t.PoisonRes!.text = `+${s(Stat.PoisonResist)}`;
      t.HealthR!.text = `+${s(Stat.HealthRecovery)}`;
      t.ManaR!.text = `+${s(Stat.SpellRecovery)}`;
      t.PoisonRec!.text = `+${s(Stat.PoisonRecovery)}`;
      t.HolyT!.text = `+${s(Stat.Holy)}`;
      t.Freeze!.text = `+${s(Stat.Freezing)}`;
      t.PoisonAtk!.text = `+${s(Stat.PoisonAttack)}`;
    } else {
      // RefreshInterface: Magics[i].Update(Actor.Magics[i + StartIndex])
      const list = this.magicList();
      for (let i = 0; i < this.magics.length; i++) {
        const m = list[i + this.startIndex];
        this.magics[i]!.visible = !!m;
        if (m) this.magics[i]!.update(m);
      }
    }
  }

  /** CharacterPage.AfterDraw. */
  private drawDoll(v: CharacterView, gender: number, hair: number): void {
    const inv = this.isUser ? userWindows.inventory : null;
    const item = (slot: number) => v.equipment ? v.equipment[slot] ?? null : inv?.equipped(slot) ?? null;
    const infoOf = (it: UserItem) => inv?.itemInfo(it) ?? gameItemInfos()?.get(it.ItemIndex);
    const real = (it: UserItem) => { const i = infoOf(it); const infos = gameItemInfos(); return i && infos ? infos.realItem(i, v.level, v.cls) : i; };
    const armour = item(EquipmentSlot.Armour), weapon = item(EquipmentSlot.Weapon), helmet = item(EquipmentSlot.Helmet);
    const wingEffect = v.stats?.wingEffect ?? 0;
    const armourImg = armour ? real(armour)?.Image ?? -1 : -1;
    const weaponImg = weapon ? real(weapon)?.Image ?? -1 : -1;
    const helmetImg = helmet ? infoOf(helmet)?.Image ?? -1 : -1;
    const key = [gender, hair, v.cls, wingEffect, armour ? armourImg : "-", weaponImg, helmet ? helmetImg : "-"].join(",");
    if (key === this.dollKey) return;
    this.dollKey = key;
    const male = gender === MirGender.Male;
    // wings (Armour's WingEffect 1 / 2): Prguse2 1200 + 2 / 4 + gender, blended
    const wing = armour && (wingEffect === 1 || wingEffect === 2) ? 1200 + (wingEffect === 1 ? 2 : 4) + (male ? 0 : 1) : -1;
    void setPicture(this.doll.wing, wing >= 0 ? Lib.Prguse2 : null, wing, true);
    void setPicture(this.doll.armour, armour && armourImg >= 0 ? Lib.StateItems : null, armourImg, true);
    void setPicture(this.doll.weapon, weapon && weaponImg >= 0 ? Lib.StateItems : null, weaponImg, true);
    if (helmet) {
      this.doll.head.style.transform = "";
      void setPicture(this.doll.head, helmetImg >= 0 ? Lib.StateItems : null, helmetImg, true);
    } else {
      const assassin = v.cls === MirClass.Assassin;
      const index = 441 + hair + (assassin ? 20 : 0) + (male ? 0 : 40);
      const ox = assassin ? (male ? 6 : 4) : 0, oy = assassin ? (male ? 25 : 18) : 0;
      this.doll.head.style.transform = `translate(${ox}px, ${oy}px)`;
      void setPicture(this.doll.head, Lib.Prguse, index, true);
    }
  }
}

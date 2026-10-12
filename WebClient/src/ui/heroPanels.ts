// Hero HUD panels with the desktop artwork (Client/MirScenes/Dialogs/HeroDialogs.cs + MainDialogs.cs):
//   HeroInfoPanel       Prguse 14 inside MainDialog at (95, 48): avatar (GameScene.HeroAvatar, danger face blinking below
//                       21 % health, dead face 1379), level + name (Prguse 10), HP / MP / experience bars (Prguse 11 with
//                       1951 / 1952 / 1953 sections) and labels, the HP / MP auto-pot previews (HeroAutoPotPreview, 1392)
//   HeroMenuButton      MainDialog child (Prguse 2164) toggling HeroMenuPanel (Prguse 2179: hero skills / inventory /
//                       character buttons 2173 / 2170 / 2176)
//   HeroSummonButton    MainDialog child (Prguse 2167): C.Chat "@SUMMONHERO" (the server summons or dismisses)
//   HeroBehaviourPanel  MainDialog.Location + (165, 37): Prguse 1840 + i, disabled 1844 + i for the current behaviour
//   HeroBuffsDialog     a BuffDialog at (ScreenWidth - 170, 80)
// Visibility follows GameScene.UpdateHeroSpawnState: info panel, menu button and behaviour panel while State >
// Unsummoned; the summon button while HasHero (State > None).

import { ClientPackets } from "../protocol/client.ts";
import { HeroBehaviour, HeroBehaviourName } from "../protocol/enums.generated.ts";
import type { Hero } from "../game/hero.ts";
import { SoundList } from "../game/audio.ts";
import { KeybindOptions } from "../game/keybinds.ts";
import { gameSignal } from "../game/scope.ts";
import { MirWindow, MirButton, MirImage, MirLabel, Lib, type MirControl, SCREEN_WIDTH } from "./mir/controls.ts";
import { BuffBar, libIcon } from "./buffBar.ts";
import { Section } from "./hudCommon.ts";
import { keyHint } from "./mainDialog.ts";

type Send = (f: Uint8Array) => void;

/** GameScene.HeroAvatar: Prguse index of the hero's face (HeroInfoPanel; HeroManageAvatar adds 370). */
export function heroAvatar(cls: number, gender: number): number {
  return 1400 + cls + 10 * gender;
}

/** Sets an <img> to a Prguse image (async; the frame loop never waits). */
export function setPrguseImage(img: HTMLImageElement, index: number): void {
  img.dataset.index = String(index);
  void libIcon("Prguse", index).then((url) => { if (url && img.dataset.index === String(index)) img.src = url; });
}

/** HeroAutoPotPreview: Prguse 1392 (19 x 15) with the potion's Items image and the count in the hero's bag. */
class AutoPotPreview {
  private readonly box: MirImage;
  private readonly item: MirImage;
  private readonly amount: MirLabel;

  constructor(parent: MirControl, location: [number, number]) {
    this.box = new MirImage({ parent, library: Lib.Prguse, index: 1392, location, size: [19, 15], notControl: true });
    this.item = new MirImage({ parent: this.box, library: Lib.Items, index: -1, location: [2, 3], size: [19, 15] });
    this.item.el.querySelector<HTMLElement>(".mir-pic")!.style.backgroundSize = "19px 15px"; // Items.Draw(image, point, Size)
    this.amount = new MirLabel({ parent: this.box, location: [0, 6], size: [22, 17], align: "right", foreColour: "#ffff00", visible: false });
  }

  update(image: number | null, count: number): void {
    this.amount.visible = image !== null;
    this.item.index = image ?? -1;
    if (image !== null) this.amount.text = String(count);
  }
}

/** HeroInfoPanel (MainDialogs.cs creates it inside MainDialog; Update() on HeroInformation / HeroLevelChanged). */
export class HeroInfoPanel {
  readonly root: MirImage;
  private readonly avatar: MirImage;
  private readonly danger: MirImage;
  private readonly deadAvatar: MirImage;
  private readonly levelLabel: MirLabel;
  private readonly nameLabel: MirLabel;
  private readonly hpBar: Section;
  private readonly mpBar: Section;
  private readonly expBar: Section;
  private readonly hpLabel: MirLabel;
  private readonly mpLabel: MirLabel;
  private readonly exLabel: MirLabel;
  private readonly hpItem: AutoPotPreview;
  private readonly mpItem: AutoPotPreview;
  private nextAvatarChange = 0;

  /** `maxHp` / `maxMp`: Hero.Stats[Stat.HP] / [Stat.MP] (HeroCharacterWindow.maxHp / maxMp). */
  constructor(parent: MirControl, private readonly hero: Hero, private readonly maxHp: () => number, private readonly maxMp: () => number,
    private readonly itemImage: (itemIndex: number) => number | undefined) {
    this.root = new MirImage({ parent, library: Lib.Prguse, index: 14, location: [95, 48], visible: false });
    this.avatar = new MirImage({ parent: this.root, library: Lib.Prguse, index: 1400, location: [14, 19] });
    this.danger = new MirImage({ parent: this.root, library: Lib.Prguse, index: 1750, location: [14, 19], visible: false });
    this.deadAvatar = new MirImage({ parent: this.root, library: Lib.Prguse, index: 1379, location: [14, 19], visible: false });
    const nameContainer = new MirImage({ parent: this.root, library: Lib.Prguse, index: 10, location: [26, 60] });
    this.levelLabel = new MirLabel({ parent: nameContainer, size: [17, 14], location: [3, -1], align: "center" });
    this.nameLabel = new MirLabel({ parent: nameContainer, size: [97, 14], location: [2, 14], align: "center" });
    const healthContainer = new MirImage({ parent: this.root, library: Lib.Prguse, index: 11, location: [57, 26] });
    this.hpBar = new Section(healthContainer, 18, 6);
    this.mpBar = new Section(healthContainer, 18, 19);
    this.expBar = new Section(healthContainer, 18, 32);
    this.hpLabel = new MirLabel({ parent: this.root, size: [55, 18], location: [71, 28] });
    this.mpLabel = new MirLabel({ parent: this.root, size: [55, 18], location: [71, 41] });
    this.exLabel = new MirLabel({ parent: this.root, size: [65, 18], location: [71, 54] });
    this.hpItem = new AutoPotPreview(this.root, [86, 5]);
    this.mpItem = new AutoPotPreview(this.root, [106, 5]);
  }

  /** Per frame: visibility, Update(), the bar BeforeDraws and Avatar_BeforeDraw's blink. */
  update(now: number): void {
    const h = this.hero.info;
    const show = this.hero.summoned && !!h;
    this.root.visible = show;
    if (!show || !h) return;
    // HeroInfoPanel.Update
    const index = heroAvatar(h.cls, h.gender);
    this.avatar.index = index;
    this.danger.index = index + 350;
    this.levelLabel.text = String(h.level);
    this.nameLabel.text = h.name;
    const maxHp = this.maxHp(), maxMp = this.maxMp();
    // PercentHealth / PercentMana = (byte)(HP / (float)Stats[HP] * 100)
    const pHealth = maxHp > 0 ? Math.min(255, Math.floor((h.hp / maxHp) * 100)) : 0;
    const pMana = maxMp > 0 ? Math.min(255, Math.floor((h.mp / maxMp) * 100)) : 0;
    // HealthBar / ManaBar / ExperienceBar BeforeDraw: 52 px sections
    this.hpBar.set(Lib.Prguse, 1951, 0, 0, Math.trunc(52 * Math.min(1, pHealth / 100)), 8);
    this.mpBar.set(Lib.Prguse, 1952, 0, 0, Math.trunc(52 * Math.min(1, pMana / 100)), 8);
    const exp = h.maxExperience > 0n ? (Number(h.experience) / Number(h.maxExperience)) * 100 : 0;
    this.expBar.set(Lib.Prguse, 1953, 0, 0, Math.max(0, Math.trunc(52 * (exp / 100))), 8);
    if (pHealth > 0) this.hpLabel.text = `${h.hp}/${maxHp}`;
    if (pMana > 0) this.mpLabel.text = `${h.mp}/${maxMp}`;
    this.exLabel.text = `${exp.toFixed(2)}%`;
    // HeroAutoPotPreview: HPItem[0] / MPItem[0] and their count in the hero's bag
    const pot = (itemIndex: number) => {
      if (!itemIndex) return [null, 0] as const;
      let count = 0;
      for (const it of h.inventory) if (it && it.ItemIndex === itemIndex) count += it.Count;
      return [this.itemImage(itemIndex) ?? null, count] as const;
    };
    this.hpItem.update(...pot(h.hpItemIndex));
    this.mpItem.update(...pot(h.mpItemIndex));
    // Avatar_BeforeDraw
    this.deadAvatar.visible = this.hero.dead;
    if (pHealth > 20) { this.danger.visible = false; return; }
    if (now < this.nextAvatarChange) return;
    this.nextAvatarChange = now + 400;
    this.danger.visible = !this.danger.visible;
  }
}

/** HeroMenuPanel: three buttons (HeroMagicsButton, HeroInventoryButton, HeroEquipmentButton), toggled by HeroMenuButton. */
export class HeroMenuPanel {
  readonly win: MirWindow;

  constructor(screen: readonly [number, number], open: HeroWindowOpeners) {
    const [SW, SH] = screen;
    this.win = new MirWindow({ library: Lib.Prguse, index: 2179, size: [24, 61], location: [Math.floor(SW / 2 - 12) + 362, SH - 61 - 77], sort: false });
    this.win.el.classList.add("mir-hud");
    const b = (index: number, y: number, hint: string, onClick: () => void) =>
      new MirButton({ parent: this.win, library: Lib.Prguse, index, hoverIndex: index + 1, pressedIndex: index + 2, size: [16, 16], location: [3, y], hint, onClick });
    b(2173, 3, keyHint("Hero Skills ({0})", KeybindOptions.HeroSkills), () => open.openSkills());
    b(2170, 20, keyHint("Hero Inventory ({0})", KeybindOptions.HeroInventory), () => open.openInventory());
    b(2176, 37, keyHint("Hero Character ({0})", KeybindOptions.HeroEquipment), () => open.openCharacter());
  }

  get visible(): boolean { return this.win.visible; }

  /** HeroMenuPanel.Toggle. */
  toggle(show = !this.win.visible): void {
    if (show) this.win.show(); else this.win.hide();
  }
}

/** HeroBehaviourPanel: one button per HeroBehaviour; the current one is disabled (UpdateBehaviour). */
export class HeroBehaviourPanel {
  readonly win: MirWindow;
  private readonly buttons: MirButton[] = [];

  constructor(location: readonly [number, number], private readonly hero: Hero, send: Send) {
    this.win = new MirWindow({ size: [64, 17], location: [location[0], location[1]], sort: false });
    this.win.el.classList.add("mir-hud");
    for (let i = 0; i < 4; i++) {
      const name = HeroBehaviourName[i] ?? String(i);
      // SetBehaviour; Hint = ClientTextKeys.HeroBehaviourFormat
      this.buttons.push(new MirButton({ parent: this.win, library: Lib.Prguse, index: 1840 + i, disabledIndex: 1844 + i, location: [16 * i, 0],
        sound: SoundList.ButtonA, hint: `Hero Behaviour: ${name}`, onClick: () => send(ClientPackets.SetHeroBehaviour({ Behaviour: i as HeroBehaviour })) }));
    }
  }

  update(): void {
    this.win.visible = this.hero.summoned;
    // UpdateBehaviour: Enabled = (byte)behaviour != i
    this.buttons.forEach((b, i) => { const on = this.hero.behaviour !== i; if (b.enabled !== on) b.enabled = on; });
  }
}

/** Opens the hero windows owned elsewhere (HeroDialog skill / character pages, HeroInventoryDialog). */
export interface HeroWindowOpeners {
  openSkills(): void;
  openInventory(): void;
  openCharacter(): void;
}

export interface HeroPanelsOptions {
  /** MainDialog (the info panel and the menu / summon buttons are its children). */
  main: MirControl;
  mainLocation: readonly [number, number];
  screen: readonly [number, number];
  maxHp: () => number;
  maxMp: () => number;
  /** ItemInfo.Image of an item index (the auto-pot previews). */
  itemImage: (itemIndex: number) => number | undefined;
}

/** All hero HUD parts. Call `update(now)` every frame. */
export class HeroPanels {
  readonly info: HeroInfoPanel;
  readonly menu: HeroMenuPanel;
  readonly behaviour: HeroBehaviourPanel;
  /** GameScene.HeroBuffsDialog (under the user's buffs). */
  readonly buffs: BuffBar;
  /** MainDialog.HeroSummonButton. */
  readonly summonButton: MirButton;
  /** MainDialog.HeroMenuButton. */
  readonly menuButton: MirButton;

  constructor(stage: HTMLElement, private readonly hero: Hero, send: Send, open: HeroWindowOpeners, o: HeroPanelsOptions) {
    this.info = new HeroInfoPanel(o.main, hero, o.maxHp, o.maxMp, o.itemImage);
    this.menu = new HeroMenuPanel(o.screen, open);
    this.behaviour = new HeroBehaviourPanel([o.mainLocation[0] + 165, o.mainLocation[1] + 37], hero, send);
    this.buffs = new BuffBar(stage, { y: 80, expandedKey: "ExpandedHeroBuffWindow", screenWidth: o.screen[0] });
    this.menuButton = new MirButton({ parent: o.main, library: Lib.Prguse, index: 2164, hoverIndex: 2165, pressedIndex: 2166, size: [20, 20],
      location: [SCREEN_WIDTH - 160, 65], sound: SoundList.ButtonA, visible: false, onClick: () => this.menu.toggle() });
    this.summonButton = new MirButton({ parent: o.main, library: Lib.Prguse, index: 2167, hoverIndex: 2168, pressedIndex: 2169, size: [20, 20],
      location: [SCREEN_WIDTH - 160, 90], sound: SoundList.ButtonA, visible: false, onClick: () => send(ClientPackets.Chat({ Message: "@SUMMONHERO", LinkedItems: [] })) });
    gameSignal().addEventListener("abort", () => { this.menu.win.dispose(); this.behaviour.win.dispose(); });
  }

  update(now: number): void {
    const h = this.hero;
    this.summonButton.visible = h.hasHero; // GameScene.HasHero setter
    this.menuButton.visible = h.summoned;
    // HeroMenuPanel.Visible = HeroMenuPanel.Visible && HeroMenuButton.Visible
    if (!h.summoned && this.menu.visible) this.menu.toggle(false);
    this.info.update(now);
    this.behaviour.update();
    this.buffs.update(h.info ? h.buffs : [], now);
  }
}

// Buff icons: port of BuffDialog (Client/MirScenes/Dialogs/BuffDialog.cs). The user's dialog sits at (ScreenWidth - 170,
// 0), the hero's (GameScene.HeroBuffsDialog) at (ScreenWidth - 170, 80). Prguse2 20..30 background that fades in while
// the mouse is over it (FadeRate 0.2 every 55 ms), the expand / collapse button (Prguse2 7, Settings.ExpandedBuffWindow /
// ExpandedHeroBuffWindow), the buff count (10pt bold yellow) when collapsed, 23 px icons (BuffIcon / MagIcon / Prguse2 by
// BuffImage) right to left in rows of 10, blinking in their last 5 seconds, with BuffString / CombinedBuffText hints.
// The right edge stays put when the background grows (UpdateWindow). Not ported: the PoisonBuffDialog (the desktop never
// creates one) and the buff caster line (ClientBuff has no Caster on the wire).
// `libIcon` (a .Lib picture as a data URL) is shared with other windows.

import { BuffType, BuffTypeName, StatName } from "../protocol/enums.generated.ts";
import { libAsync } from "../game/libraries.ts";
import { buffIcon, type UserBuff } from "../game/buffs.ts";
import { gameSettings, setSetting } from "../game/settings.ts";
import { gameSignal } from "../game/scope.ts";
import { SoundList } from "../game/audio.ts";
import { MirWindow, MirButton, MirImage, MirLabel, Lib } from "./mir/controls.ts";
import { printTimeSpan } from "./hudCommon.ts";

const iconCache = new Map<string, Promise<string | null>>();
export function libIcon(lib: string, index: number): Promise<string | null> {
  const key = `${lib}#${index}`;
  let p = iconCache.get(key);
  if (!p) {
    p = (async () => {
      const l = await libAsync(lib);
      const img = l && (await l.image(index));
      if (!img || !img.width || !img.height) return null;
      const c = document.createElement("canvas");
      c.width = img.width;
      c.height = img.height;
      c.getContext("2d")!.putImageData(new ImageData(new Uint8ClampedArray(img.rgba), img.width, img.height), 0, 0);
      return c.toDataURL();
    })().catch(() => null);
    iconCache.set(key, p);
  }
  return p;
}

/** GMOptions flags (Shared/Enums.cs). */
const GM_OPTIONS = { GameMaster: 1, Observer: 2, Superman: 4 } as const;

const FADE_DELAY = 55, FADE_RATE = 0.2;

const camel = (s: string) => s.replace(/([a-z])([A-Z])/g, "$1 $2"); // RegexFunctions.SeperateCamelCase

/** Stat.ToLocalizedString (Client/Localization/English.json "Stat_*"; unlisted stats use the member name). */
function statText(stat: number): string {
  const name = StatName[stat] ?? `Stat${stat}`;
  if (name === "CriticalRate") return "Critical";
  return name.replace(/RatePercent$|Percent$|Multiplier$/, "");
}

/** ClientTextKeys.BuffEffect for every stat: "{Increases|Decreases} {stat} by: {value}{sign}.\n". */
function statLines(stats: Record<number, number>): string {
  let text = "";
  for (const [k, v] of Object.entries(stats)) {
    if (!v) continue;
    const name = StatName[Number(k)] ?? "";
    const sign = name.includes("Percent") ? "%" : name.includes("Multiplier") ? "x" : "";
    text += `${v < 0 ? "Decreases" : "Increases"} ${statText(Number(k))} by: ${v}${sign}.\n`;
  }
  return text;
}

/** BuffDialog.BuffString. `guildStats`: GuildDialog.ActiveStats for the Guild buff. */
export function buffString(b: UserBuff, now: number, guildStats = ""): string {
  let text = camel(BuffTypeName[b.Type] ?? `Buff${b.Type}`) + "\n";
  let overrideStats = false;
  const st = (name: string) => { for (const [k, v] of Object.entries(StatName)) if (v === name) return b.Stats[Number(k)] ?? 0; return 0; };
  switch (b.Type) {
    case BuffType.GameMaster: {
      const o = b.Values[0] ?? 0;
      if (o & GM_OPTIONS.GameMaster) text += "-Invisible\n";
      if (o & GM_OPTIONS.Superman) text += "-Superman";
      if (o & GM_OPTIONS.Observer) text += "-Observer";
      break;
    }
    case BuffType.MentalState:
      text += ["Agressive (Full damage)\nCan't shoot over walls.\n", "Trick shot (Minimal damage)\nCan shoot over walls.\n",
        "Group Mode (Medium damage)\nDon't steal agro.\n"][b.Values[0] ?? -1] ?? "";
      break;
    case BuffType.Hiding: case BuffType.ClearRing: text += "Invisible to many monsters.\n"; break;
    case BuffType.MoonLight: text += "Invisible to players and many\nmonsters when at a distance.\n"; break;
    case BuffType.EnergyShield:
      overrideStats = true;
      text += `${st("EnergyShieldPercent")}% chance to gain ${st("EnergyShieldHPGain")} HP when attacked.\n`;
      break;
    case BuffType.DarkBody: text += "Invisible to many monsters and able to move.\n"; break;
    case BuffType.VampireShot: text += "Gives you a vampiric ability\nthat can be released with\ncertain skills.\n"; break;
    case BuffType.PoisonShot: text += "Gives you a poison ability\nthat can be released with\ncertain skills.\n"; break;
    case BuffType.Concentration: text += "Increases chance on element extraction.\n"; break;
    case BuffType.MagicBooster:
      overrideStats = true;
      text += `Increases MC by: ${st("MinMC")}-${st("MaxMC")}.\nIncreases consumption by ${st("ManaPenaltyPercent")}%.\n`;
      break;
    case BuffType.Transform: text += "Disguises your appearance.\n"; break;
    case BuffType.Mentee: text += "Learn skill points twice as quick.\n"; break;
    case BuffType.Guild: text += guildStats; break;
    case BuffType.Blindness: text += "Reduces visibility\n"; break;
    case BuffType.Newbie: text += "A boost provided to members of your guild.\n"; break;
  }
  if (!overrideStats) text += statLines(b.Stats);
  if (b.Paused) text += "Expire: Paused";
  else if (b.Infinite) text += "Expire: Never";
  else text += `Expire: ${printTimeSpan(Math.round((b.expire - now) / 1000))}`;
  return text;
}

export interface BuffBarOptions {
  /** Location.Y (0 for the user, 80 for the hero). */
  y?: number;
  /** Which setting keeps the expanded state. */
  expandedKey?: "ExpandedBuffWindow" | "ExpandedHeroBuffWindow";
  screenWidth?: number;
  /** GuildDialog.ActiveStats (the Guild buff's hint). */
  guildStats?: () => string;
}

export class BuffBar {
  private readonly win: MirWindow;
  private readonly expand: MirButton;
  private readonly countLabel: MirLabel;
  private readonly icons: MirImage[] = [];
  private readonly key: "ExpandedBuffWindow" | "ExpandedHeroBuffWindow";
  private readonly guildStats: () => string;
  private buffCount = -1;
  private opacity = 0;
  private fadedIn = false;
  private fadedOut = true;
  private nextFadeTime = 0;
  private mouseOver = false;
  private right: number;

  constructor(_stage: HTMLElement, opts: BuffBarOptions = {}) {
    this.key = opts.expandedKey ?? "ExpandedBuffWindow";
    this.guildStats = opts.guildStats ?? (() => "");
    const x = (opts.screenWidth ?? 1024) - 170;
    this.right = x + 44;
    this.win = new MirWindow({ library: Lib.Prguse2, index: 20, location: [x, opts.y ?? 0], sort: true, visible: true });
    this.win.el.classList.add("mir-hud");
    this.win.picOpacity = 0;
    this.expand = new MirButton({ parent: this.win, library: Lib.Prguse2, index: 7, hoverIndex: 8, pressedIndex: 9, size: [16, 15], sound: SoundList.ButtonA,
      onClick: () => {
        setSetting(this.key, this.buffCount === 1 ? true : !gameSettings[this.key]);
        this.updateWindow();
      } });
    this.expand.picOpacity = 0;
    this.countLabel = new MirLabel({ parent: this.win, autoSize: true, notControl: true, font: "bold 10pt Arial", foreColour: "#ffff00", visible: false });
    this.win.el.addEventListener("pointerenter", () => { this.mouseOver = true; });
    this.win.el.addEventListener("pointerleave", () => { this.mouseOver = false; });
    gameSignal().addEventListener("abort", () => this.win.dispose());
  }

  /** BuffDialog.UpdateWindow: background for the count, right edge kept, count label when collapsed. */
  private updateWindow(): void {
    const n = this.icons.length;
    this.buffCount = n;
    if (n === 0) return;
    const expanded = gameSettings[this.key];
    const index = expanded ? (n <= 10 ? 20 + n - 1 : 30) : 20;
    const width = [44, 68, 92, 112, 136, 160, 184, 204, 228, 252, 252][index - 20] ?? 44; // Prguse2 20..30 widths
    this.win.index = index;
    this.win.location = [this.right - width, this.win.location[1]];
    this.expand.location = [width - 15, 0];
    this.countLabel.visible = !expanded;
    if (!expanded) {
      this.countLabel.text = String(n);
      // (Size.Width / 2 - label width / 2, Size.Height / 2 - 10)
      this.countLabel.location = [Math.floor(44 / 2 - this.countLabel.el.offsetWidth / 2), 34 / 2 - 10];
      this.countLabel.el.parentElement?.append(this.countLabel.el); // BringToFront
    }
  }

  /** BuffDialog.Process, every frame. */
  update(buffs: readonly UserBuff[], now: number): void {
    while (this.icons.length < buffs.length) this.icons.push(new MirImage({ parent: this.win, library: Lib.BuffIcon, index: 0 }));
    while (this.icons.length > buffs.length) this.icons.pop()!.dispose();
    if (this.icons.length !== this.buffCount) this.updateWindow();
    const expanded = gameSettings[this.key];
    const width = this.win.size[0] || 44;
    let combined: string | null = null;
    for (let i = 0; i < buffs.length; i++) {
      const b = buffs[i]!, image = this.icons[i]!;
      const [lib, index] = buffIcon(b.Type);
      image.location = [width - 10 - 23 - i * 23 + 10 * 23 * Math.floor(i / 10), 6 + Math.floor(i / 10) * 24];
      if (expanded) image.hint = buffString(b, now, b.Type === BuffType.Guild ? this.guildStats() : "");
      else image.hint = combined ??= this.combinedText(buffs);
      image.library = lib;
      const show = expanded || i === 0;
      image.visible = show;
      image.picOpacity = show ? 1 : 0.6;
      let shown = index;
      if (!b.Paused && !b.Infinite && Math.round((b.expire - now) / 1000) <= 5 && Math.round((b.expire - now) / 100) % 10 < 5) shown = -1;
      image.index = shown;
    }
    // fade in while the mouse is over the dialog, out otherwise
    if (this.mouseOver) {
      if (this.buffCount === 0 || (!this.fadedIn && now <= this.nextFadeTime)) return;
      this.opacity += FADE_RATE;
      if (this.opacity > 1) { this.opacity = 1; this.fadedIn = true; this.fadedOut = false; }
    } else {
      if (!this.fadedOut && now <= this.nextFadeTime) return;
      this.opacity -= FADE_RATE;
      if (this.opacity < 0) { this.opacity = 0; this.fadedOut = true; this.fadedIn = false; }
    }
    this.nextFadeTime = now + FADE_DELAY;
    this.win.picOpacity = this.opacity;
    this.expand.picOpacity = this.opacity;
  }

  /** BuffDialog.CombinedBuffText. */
  private combinedText(buffs: readonly UserBuff[]): string {
    const sum: Record<number, number> = {};
    for (const b of buffs) for (const [k, v] of Object.entries(b.Stats)) sum[Number(k)] = (sum[Number(k)] ?? 0) + v;
    return "Active Buffs\n" + statLines(sum);
  }
}

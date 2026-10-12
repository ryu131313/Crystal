// Options: port of OptionDialog (Client/MirScenes/Dialogs/MainDialogs.cs). Toggle with F12 / O (KeybindOptions.Options /
// Options2) or the Options button. The desktop dialog has: skill mode (~ / Ctrl), skill bar, effects, drop view, name view,
// HP view, new move, allow observe and the sound / music volume bars. The "More" section adds the [Game] / [Graphics] values
// the desktop only changes elsewhere (chat commands @LEVELEFFECT / @TARGETDEAD / @LIGHT, ChatOptionDialog transparency,
// BuffDialog, Mir2Config.ini) so they are reachable without an ini file.
// Values go through settings.ts setSetting (localStorage); volumes go to SoundManager (audio.ts persists them).
// Allow observe sends `C.Chat "@ALLOWOBSERVE"` like OptionDialog.ToggleObserve; the state comes from
// UserInformation.AllowObserve (setAllowObserve) and S.AllowObserve (handle).

import { KeybindOptions, matches } from "../game/keybinds.ts";
import type { ServerPacket } from "../protocol/server.ts";
import { ClientPackets } from "../protocol/client.ts";
import { ChatType } from "../protocol/enums.generated.ts";
import type { SoundManager } from "../game/audio.ts";
import { gameSettings, setSetting, onSettingChange, type GameSettings } from "../game/settings.ts";
import { gameSignal } from "../game/scope.ts";
import { el } from "./screens.ts";

type BoolKey = { [K in keyof GameSettings]: GameSettings[K] extends boolean ? K : never }[keyof GameSettings];

interface Row { label: string; title?: string; get: () => boolean; set: (v: boolean) => void; on?: string; off?: string }

export class OptionWindow {
  readonly hudButton: HTMLButtonElement;
  /** GameScene.AllowObserve. */
  allowObserve = false;
  private readonly root = el("div", { class: "window option-window", hidden: true });
  private readonly refreshers: (() => void)[] = [];
  private readonly send: (f: Uint8Array) => void;
  private readonly chat: (text: string, type: number) => void;
  private readonly sound: Pick<SoundManager, "volume" | "musicVolume">;

  constructor(stage: HTMLElement, send: (f: Uint8Array) => void, chat: (text: string, type: number) => void, sound: Pick<SoundManager, "volume" | "musicVolume">) {
    this.send = send;
    this.chat = chat;
    this.sound = sound;
    this.hudButton = el("button", { title: "Options (F12 / O)", onclick: () => this.toggle() }, "Options") as HTMLButtonElement;

    const hint = (text: string) => this.chat(text, ChatType.Hint);
    const flag = (key: BoolKey, label: string, title?: string, after?: (v: boolean) => void): Row =>
      ({ label, title, get: () => gameSettings[key], set: (v) => { setSetting(key, v); after?.(v); } });

    // OptionDialog buttons (in the dialog's order)
    const main: Row[] = [
      // GameScene.ChangeSkillMode: SkillMode true = "~" bar, false = "Ctrl" bar
      { label: "Skill mode", title: "Second skill bar modifier", on: "~", off: "Ctrl", get: () => gameSettings.SkillMode,
        set: (v) => { setSetting("SkillMode", v); hint(v ? "[Skill Mode: ~]" : "[Skill Mode: Ctrl]"); } }, // ClientTextKeys.SkillModeTilde / SkillModeCtrl
      flag("SkillBar", "Skill bar"),
      flag("Effect", "Effects"),
      flag("DropView", "Drop view", "Show ground item names"),
      flag("NameView", "Name view", "Show object names"),
      flag("HPView", "HP view", undefined, (v) => hint(v ? "[HP/MP Mode 1]" : "[HP/MP Mode 2]")), // ClientTextKeys.HpMpMode1 / 2
      flag("NewMove", "New move", undefined, (v) => hint(v ? "[New Movement Style]" : "[Old Movement Style]")), // NewMovementStyle / OldMovementStyle
      // OptionDialog.ToggleObserve: the server flips the flag and answers with S.AllowObserve
      { label: "Allow observe", title: "Let other players observe you", get: () => this.allowObserve,
        set: (v) => { if (this.allowObserve !== v) this.send(ClientPackets.Chat({ Message: "@ALLOWOBSERVE" })); } },
    ];
    const more: Row[] = [
      flag("LevelEffect", "Level effects", "Desktop: @LEVELEFFECT"),
      flag("TransparentChat", "Transparent chat", "Desktop: chat options"),
      flag("ModeView", "Mode view"),
      flag("DuraView", "Durability view"),
      flag("DisplayDamage", "Display damage"),
      flag("TargetDead", "Target dead", "Desktop: @TARGETDEAD"),
      flag("HighlightTarget", "Highlight target"),
      flag("DisplayBodyName", "Display body name", "Show dead monsters' names under the mouse"),
      // ChatDialog.TryLightingCommand (@LIGHT NEW / OLD)
      { label: "Lighting", title: "Desktop: @LIGHT NEW / OLD", on: "Enhanced", off: "Classic", get: () => gameSettings.Lighting === "Enhanced",
        set: (v) => { setSetting("Lighting", v ? "Enhanced" : "Classic"); hint(v ? "[Lighting: Enhanced]" : "[Lighting: Classic]"); } },
    ];

    this.root.append(
      el("div", { class: "window-title" }, el("span", {}, "Options"), el("button", { class: "window-close", title: "Close (F12 / O)", onclick: () => this.toggle(false) }, "×")),
      el("div", { class: "option-grid" }, ...main.map((r) => this.row(r))),
      this.slider("Sound", () => this.sound.volume, (v) => { this.sound.volume = v; }), // OptionDialog.SoundBar_MouseMove
      this.slider("Music", () => this.sound.musicVolume, (v) => { this.sound.musicVolume = v; }), // MusicSoundBar_MouseMove
      el("div", { class: "option-section" }, "More"),
      el("div", { class: "option-grid" }, ...more.map((r) => this.row(r))),
    );
    this.root.addEventListener("mousedown", (e) => e.stopPropagation());
    stage.append(this.root);

    const signal = gameSignal();
    const off = onSettingChange(() => this.refresh());
    signal.addEventListener("abort", off);
    window.addEventListener("keydown", (e) => {
      const a = document.activeElement;
      if (a instanceof HTMLInputElement || a instanceof HTMLTextAreaElement || a instanceof HTMLSelectElement) return;
      if (matches(e, KeybindOptions.Options) || matches(e, KeybindOptions.Options2)) { e.preventDefault(); this.toggle(); } // F12 / O
      else if (matches(e, KeybindOptions.Closeall) && !this.root.hidden) this.toggle(false);
    }, { signal });
  }

  get visible(): boolean {
    return !this.root.hidden;
  }

  toggle(show = this.root.hidden): void {
    this.root.hidden = !show;
    if (show) this.refresh();
  }

  /** UserInformation.AllowObserve (GameScene.UserInformation). */
  setAllowObserve(allow: boolean): void {
    this.allowObserve = allow;
    this.refresh();
  }

  /** S.AllowObserve; returns true when handled. */
  handle(p: ServerPacket): boolean {
    if (p.type !== "AllowObserve") return false;
    this.setAllowObserve(p.Allow);
    return true;
  }

  private refresh(): void {
    if (this.root.hidden) return;
    for (const r of this.refreshers) r();
  }

  /** An On / Off pair like the dialog's two image buttons (the active one pressed). */
  private row(r: Row): HTMLElement {
    const on = el("button", { onclick: () => { r.set(true); this.refresh(); } }, r.on ?? "On");
    const off = el("button", { onclick: () => { r.set(false); this.refresh(); } }, r.off ?? "Off");
    this.refreshers.push(() => { const v = r.get(); on.classList.toggle("active", v); off.classList.toggle("active", !v); });
    return el("div", { class: "option-row", title: r.title ?? "" }, el("span", {}, r.label), el("span", { class: "option-pair" }, on, off));
  }

  private slider(label: string, get: () => number, set: (v: number) => void): HTMLElement {
    const value = el("span", { class: "option-value" });
    const input = el("input", { type: "range", min: "0", max: "100", step: "1" });
    const show = () => { const v = Math.round(get()); input.value = String(v); value.textContent = `${v}%`; };
    input.addEventListener("input", () => { set(Number(input.value)); value.textContent = `${Math.round(get())}%`; });
    this.refreshers.push(show);
    show();
    return el("div", { class: "option-slider" }, el("span", {}, label), input, value);
  }
}
